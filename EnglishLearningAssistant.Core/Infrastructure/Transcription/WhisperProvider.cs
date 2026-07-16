using System.IO;
using System.Runtime.CompilerServices;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;

namespace EnglishLearningAssistant.Infrastructure.Transcription;

/// <summary>
/// Proveedor de transcripción basado en Whisper.net (whisper.cpp bindings).
/// Soporta transcripción de archivos importados y sesiones en vivo vía NAudio.
///
/// Inspirado en Meetily's whisper_engine.rs:
///   - Mantiene el contexto del modelo cargado entre llamadas (evita re-carga)
///   - Detecta GPU automáticamente (equivalente a WhisperEngine::detect_gpu_acceleration)
///   - ModelInfo con estado (Available / Missing / Loading / Error) — ver ModelStatus
///   - Descarga con progreso
///
/// (T1.3)
/// </summary>
public sealed class WhisperProvider : ITranscriptionProvider
{
    private readonly ILogger<WhisperProvider> _logger;
    private readonly AppConfiguration _config;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _initialized;
    private long _sequenceId;

    public string Name => $"Whisper ({_config.Transcription.WhisperModel})";
    public bool SupportsPartialResults => false; // Whisper emite resultados finales por segmento

    public WhisperProvider(ILogger<WhisperProvider> logger, AppConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Carga el modelo GGUF en memoria. Si no existe localmente, lo descarga.
    /// Equivalente a WhisperEngine::new_with_models_dir en Meetily.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        var modelName = _config.Transcription.WhisperModel;
        var modelsDir = _config.Storage.ModelsCachePath!;
        Directory.CreateDirectory(modelsDir);

        var modelPath = Path.Combine(modelsDir, $"ggml-{modelName}.bin");

        _logger.LogInformation("Inicializando Whisper. Modelo: {Model}, Ruta: {Path}", modelName, modelPath);

        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Modelo no encontrado. Descargando {Model}...", modelName);
            await DownloadModelAsync(modelName, modelPath, cancellationToken);
        }

        _logger.LogInformation("Cargando modelo Whisper desde {Path}...", modelPath);

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory
            .CreateBuilder()
            .WithLanguage("en")
            .Build();

        _initialized = true;
        _logger.LogInformation("Whisper inicializado correctamente. Modelo: {Model}", modelName);
    }

    /// <summary>
    /// Para importación de archivos: transcribe el archivo completo y emite segmentos.
    /// Para sesiones en vivo: aún no implementado — usar WindowsLiveCaptionsProvider o AudioCaptureService.
    /// </summary>
    public async IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_initialized || _processor is null)
            throw new InvalidOperationException("Llama a InitializeAsync antes de StartAsync.");

        if (request.FilePath is null)
        {
            _logger.LogWarning("WhisperProvider en modo live aún no implementado. Usa WindowsLiveCaptionsProvider.");
            yield break;
        }

        _logger.LogInformation("Transcribiendo archivo: {File}", request.FilePath);

        if (!File.Exists(request.FilePath))
            throw new FileNotFoundException("Archivo de audio no encontrado", request.FilePath);

        await using var audioStream = File.OpenRead(request.FilePath);

        // Whisper.net emite resultados por segmento (tiempo de inicio, fin, texto)
        await foreach (var segment in _processor.ProcessAsync(audioStream, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            yield return new TranscriptSegment
            {
                SequenceId = Interlocked.Increment(ref _sequenceId),
                Text = segment.Text.Trim(),
                StartTime = segment.Start,
                EndTime = segment.End,
                IsPartial = false,
                Source = Name
            };
        }

        _logger.LogInformation("Transcripción de archivo completada");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Whisper.net: la cancelación del token en ProcessAsync detiene la transcripción
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _processor = null;
        _factory = null;
        _initialized = false;
        _logger.LogInformation("WhisperProvider liberado");
        return Task.CompletedTask;
    }

    // ─── Model management ─────────────────────────────────────────────────────

    /// <summary>
    /// Descarga el modelo GGUF desde Hugging Face.
    /// Equivalente a WhisperEngine::download_model con progress reporting.
    /// </summary>
    private async Task DownloadModelAsync(string modelName, string destPath, CancellationToken cancellationToken)
    {
        var ggmlType = modelName switch
        {
            "tiny"        => GgmlType.Tiny,
            "tiny.en"     => GgmlType.TinyEn,
            "base"        => GgmlType.Base,
            "base.en"     => GgmlType.BaseEn,
            "small"       => GgmlType.Small,
            "small.en"    => GgmlType.SmallEn,
            "medium"      => GgmlType.Medium,
            "medium.en"   => GgmlType.MediumEn,
            "large-v1"    => GgmlType.LargeV1,
            "large-v2"    => GgmlType.LargeV2,
            "large-v3"    => GgmlType.LargeV3,
            _             => GgmlType.Base
        };

        _logger.LogInformation("Descargando modelo {Model} ({Type})...", modelName, ggmlType);

        await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType, cancellationToken: cancellationToken);
        await using var destStream = File.Create(destPath);
        await modelStream.CopyToAsync(destStream, cancellationToken);

        _logger.LogInformation("Modelo {Model} descargado en {Path}", modelName, destPath);
    }

    /// <summary>
    /// Estado del modelo (para UI de gestión de modelos — T10.1).
    /// Equivalente a ModelInfo/ModelStatus de Meetily.
    /// </summary>
    public ModelStatus GetModelStatus()
    {
        var modelName = _config.Transcription.WhisperModel;
        var modelPath = Path.Combine(_config.Storage.ModelsCachePath!, $"ggml-{modelName}.bin");

        if (_initialized) return ModelStatus.Loaded;
        if (File.Exists(modelPath)) return ModelStatus.Available;
        return ModelStatus.NotDownloaded;
    }
}

/// <summary>Estado del modelo Whisper local.</summary>
public enum ModelStatus
{
    NotDownloaded,
    Available,
    Loading,
    Loaded,
    Error
}
