using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace EnglishLearningAssistant.Infrastructure.Transcription;

/// <summary>
/// Proveedor de transcripción en vivo usando Whisper local y captura de NAudio.
/// Implementa Fase 2 (Whisper en vivo por fragmentos):
///   - T2.1: Bounded Channel para cola de fragmentos de audio (AudioChunkChannel).
///   - T2.2: VAD básico por energía RMS para ignorar silencios y prevenir alucinaciones.
///   - T2.3: WhisperWorker en segundo plano para procesar la cola de forma ordenada.
///   - T2.4: Medición de latencia de transcripción por fragmento.
/// </summary>
public sealed class LiveWhisperProvider : ITranscriptionProvider
{
    private readonly ILogger<LiveWhisperProvider> _logger;
    private readonly AppConfiguration _config;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private IWaveIn? _captureDevice;
    private MemoryStream _audioAccumulator = new();
    private readonly object _lock = new();
    private Channel<byte[]>? _chunkChannel;
    private CancellationTokenSource? _sessionCts;
    private WaveFileWriter? _sessionAudioWriter;
    private long _sequenceId;
    private bool _initialized;

    // Formato requerido por Whisper (16kHz, 16 bits, Mono)
    private static readonly WaveFormat WhisperFormat = new(16000, 16, 1);
    private const int ChunkIntervalMs = 3000;      // Fragmentos de 3 segundos
    private const int MaxBufferSeconds = 6;       // Límite de backlog para evitar retrasos
    private const float SilenceRmsThreshold = 0.015f; // Umbral de energía RMS para VAD

    public string Name => $"Whisper Live ({_config.Transcription.WhisperModel})";
    public bool SupportsPartialResults => false;

    public LiveWhisperProvider(ILogger<LiveWhisperProvider> logger, AppConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        var modelName = _config.Transcription.WhisperModel;
        var modelsDir = _config.Storage.ModelsCachePath!;
        Directory.CreateDirectory(modelsDir);

        var modelPath = Path.Combine(modelsDir, $"ggml-{modelName}.bin");

        _logger.LogInformation("Inicializando LiveWhisper. Modelo: {Model}, Ruta: {Path}", modelName, modelPath);

        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Modelo local no encontrado. Descargando {Model}...", modelName);
            await DownloadModelAsync(modelName, modelPath, cancellationToken);
        }

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory
            .CreateBuilder()
            .WithLanguage("en")
            .Build();

        _initialized = true;
        _logger.LogInformation("LiveWhisper cargado y listo");
    }

    public async IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_initialized || _processor is null)
            throw new InvalidOperationException("Llama a InitializeAsync antes de comenzar.");

        _logger.LogInformation("Iniciando captura de audio en vivo...");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _sessionCts.Token;

        // T2.1: Cola de fragmentos mediante un canal acotado
        _chunkChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // T3.2: Grabación de audio de sesión (WAV)
        if (request.RecordAudio)
        {
            var recordingsDir = _config.Storage.AudioRecordingsPath!;
            Directory.CreateDirectory(recordingsDir);
            var sessionWavPath = Path.Combine(recordingsDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            _logger.LogInformation("Grabación de sesión iniciada: {Path}", sessionWavPath);
            _sessionAudioWriter = new WaveFileWriter(sessionWavPath, WhisperFormat);
        }

        // Configurar dispositivo de captura (Micrófono o Loopback)
        bool useLoopback = _config.Transcription.Provider.Equals("WhisperLoopback", StringComparison.OrdinalIgnoreCase);

        if (useLoopback)
        {
            var loopback = new WasapiLoopbackCapture();
            _captureDevice = loopback;
            loopback.DataAvailable += (s, e) => ProcessLoopbackAudio(e.Buffer, e.BytesRecorded, loopback.WaveFormat);
        }
        else
        {
            var mic = new WaveInEvent
            {
                DeviceNumber = -1, // WAVE_MAPPER (dispositivo por defecto)
                WaveFormat = WhisperFormat,
                BufferMilliseconds = 100
            };
            _captureDevice = mic;
            mic.DataAvailable += (s, e) => ProcessMicAudio(e.Buffer, e.BytesRecorded);
        }

        _captureDevice.StartRecording();
        _logger.LogInformation("Captura de NAudio iniciada ({Mode})", useLoopback ? "System Loopback" : "Microphone");

        // Timer para empaquetar chunks de audio cada 3 segundos
        var chunkTimer = new Timer(_ => PushAudioChunk(), null, ChunkIntervalMs, ChunkIntervalMs);

        // T2.3: WhisperWorker (consumidor del canal en segundo plano)
        await foreach (var audioChunk in _chunkChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            // T2.2: VAD por energía RMS
            float rms = CalculateRms(audioChunk);
            if (rms < SilenceRmsThreshold)
            {
                _logger.LogDebug("Chunk ignorado por VAD (RMS: {Rms:F4} < {Threshold})", rms, SilenceRmsThreshold);
                continue;
            }

            // T2.4: Medición de latencia
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string text = string.Empty;
            try
            {
                // Whisper.net procesa desde un stream de memoria (sin tocar disco)
                using var wavStream = CreateWavStream(audioChunk);
                await foreach (var segment in _processor.ProcessAsync(wavStream, token).ConfigureAwait(false))
                {
                    text += " " + segment.Text;
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error al procesar chunk de audio en Whisper");
            }

            watch.Stop();
            text = text.Trim();

            if (!string.IsNullOrWhiteSpace(text) && !IsWhisperNoise(text))
            {
                var elapsed = TimeSpan.FromMilliseconds(ChunkIntervalMs);
                var sequence = Interlocked.Increment(ref _sequenceId);

                _logger.LogInformation("[Chunk #{Seq}] Transcrito en {Latency}ms: \"{Text}\"",
                    sequence, watch.ElapsedMilliseconds, text);

                yield return new TranscriptSegment
                {
                    SequenceId = sequence,
                    Text = text,
                    StartTime = TimeSpan.FromMilliseconds((sequence - 1) * ChunkIntervalMs),
                    EndTime = TimeSpan.FromMilliseconds(sequence * ChunkIntervalMs),
                    IsPartial = false,
                    Source = Name
                };
            }
        }

        chunkTimer.Dispose();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deteniendo LiveWhisper...");
        _sessionCts?.Cancel();

        try { _captureDevice?.StopRecording(); } catch { }
        _captureDevice?.Dispose();
        _captureDevice = null;

        lock (_lock)
        {
            _audioAccumulator.Dispose();
            _audioAccumulator = new MemoryStream();

            if (_sessionAudioWriter is not null)
            {
                _logger.LogInformation("Cerrando grabación de audio de sesión...");
                try
                {
                    _sessionAudioWriter.Flush();
                    _sessionAudioWriter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al guardar archivo WAV de la sesión");
                }
                _sessionAudioWriter = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        StopAsync();
        _processor?.Dispose();
        _factory?.Dispose();
        _processor = null;
        _factory = null;
        _initialized = false;
        return Task.CompletedTask;
    }

    // ─── Procesamiento de Audio ──────────────────────────────────────────────

    private void ProcessMicAudio(byte[] buffer, int bytesRecorded)
    {
        lock (_lock)
        {
            _audioAccumulator.Write(buffer, 0, bytesRecorded);
            _sessionAudioWriter?.Write(buffer, 0, bytesRecorded);
        }
    }

    private void ProcessLoopbackAudio(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // Resample Loopback (48kHz/44.1kHz Stereo) -> Whisper (16kHz Mono 16bit)
        try
        {
            using var rawStream = new RawSourceWaveStream(new MemoryStream(buffer, 0, bytesRecorded), format);
            var mono = rawStream.ToSampleProvider().ToMono();
            var resampled = new WdlResamplingSampleProvider(mono, 16000);

            // Convertir float a 16-bit PCM
            var tempStream = new MemoryStream();
            using (var writer = new WaveFileWriter(new IgnoreDisposeStream(tempStream), WhisperFormat))
            {
                var buffer32 = new float[bytesRecorded / 4];
                int read = resampled.Read(buffer32, 0, buffer32.Length);
                writer.WriteSamples(buffer32, 0, read);
            }

            lock (_lock)
            {
                var pcmBytes = tempStream.ToArray();
                _audioAccumulator.Write(pcmBytes, 0, pcmBytes.Length);
                _sessionAudioWriter?.Write(pcmBytes, 0, pcmBytes.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al remuestrear loopback");
        }
    }

    private void PushAudioChunk()
    {
        byte[] chunk;
        lock (_lock)
        {
            // Validar tamaño mínimo (al menos 1s de audio)
            if (_audioAccumulator.Length < WhisperFormat.AverageBytesPerSecond) return;

            chunk = _audioAccumulator.ToArray();
            _audioAccumulator = new MemoryStream();
        }

        // Enviar al canal sin bloquear el hilo de captura
        _chunkChannel?.Writer.TryWrite(chunk);
    }

    // ─── VAD y Utilidades de Audio ────────────────────────────────────────────

    private static float CalculateRms(byte[] buffer)
    {
        float sum = 0;
        int sampleCount = buffer.Length / 2;

        for (int i = 0; i < buffer.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            float sampleFloat = sample / 32768f;
            sum += sampleFloat * sampleFloat;
        }

        return MathF.Sqrt(sum / sampleCount);
    }

    private static MemoryStream CreateWavStream(byte[] rawPcm)
    {
        var wavStream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wavStream), WhisperFormat))
        {
            writer.Write(rawPcm, 0, rawPcm.Length);
        }
        wavStream.Position = 0;
        return wavStream;
    }

    private static bool IsWhisperNoise(string text)
    {
        string t = text.Trim();
        return (t.StartsWith('[') && t.EndsWith(']'))
            || (t.StartsWith('(') && t.EndsWith(')'))
            || (t.StartsWith('*') && t.EndsWith('*'))
            || t.Equals("Thank you.", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Thanks for watching.", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadModelAsync(string modelName, string destPath, CancellationToken cancellationToken)
    {
        var ggmlType = modelName switch
        {
            "tiny" => GgmlType.Tiny,
            "tiny.en" => GgmlType.TinyEn,
            "base" => GgmlType.Base,
            "base.en" => GgmlType.BaseEn,
            "small" => GgmlType.Small,
            "small.en" => GgmlType.SmallEn,
            "medium" => GgmlType.Medium,
            "medium.en" => GgmlType.MediumEn,
            _ => GgmlType.Base
        };

        _logger.LogInformation("Descargando modelo GGML {Model}...", modelName);
        await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType, cancellationToken: cancellationToken);
        await using var destStream = File.Create(destPath);
        await modelStream.CopyToAsync(destStream, cancellationToken);
    }
}

/// <summary>
/// Stream decorador que ignora el método Dispose para mantener el stream base abierto.
/// </summary>
internal sealed class IgnoreDisposeStream : Stream
{
    private readonly Stream _inner;
    public IgnoreDisposeStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // No hacer nada para evitar cerrar el MemoryStream subyacente
    }
}
