using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLiveCaptionsReader.Services
{
    public class SystemAudioCaptureService : IDisposable
    {
        public event EventHandler<string>? TextCaptured;
        public event EventHandler<string>? StatusChanged;

        private readonly WhisperService _whisper;
        private WasapiLoopbackCapture? _capture;
        private MemoryStream _buffer = new();
        private WaveFormat? _captureFormat;
        private Timer? _transcribeTimer;
        private readonly object _lock = new();
        private readonly string _tempFile;
        private bool _isRunning;
        private volatile bool _isTranscribing = false;

        // Chunk interval: smaller = lower latency, more Whisper calls
        private const int ChunkIntervalMs = 2000;
        // Max audio to keep in buffer if Whisper falls behind — older audio is discarded
        private const int MaxBufferSeconds = 4;

        public SystemAudioCaptureService(WhisperService whisper)
        {
            _whisper = whisper;
            _tempFile = Path.Combine(Path.GetTempPath(), "ela_loopback.wav");
            // Subscribe once at construction so we never double-subscribe
            _whisper.DownloadProgress += (_, msg) => StatusChanged?.Invoke(this, msg);
        }

        /// <summary>
        /// Loads the Whisper model first, then starts WASAPI loopback capture.
        /// Use this on initial launch — guarantees the model is ready before the first chunk fires.
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning) return;

            try
            {
                if (!_whisper.IsModelLoaded)
                {
                    StatusChanged?.Invoke(this, "Cargando modelo Whisper...");
                    await _whisper.InitializeAsync();
                }

                StatusChanged?.Invoke(this, "Whisper listo ✓ — iniciando captura...");
                StartCapture();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Error captura: {ex.Message}");
            }
        }

        /// <summary>Resume capture after Stop() — Whisper must already be loaded.</summary>
        public void Start()
        {
            if (_isRunning) return;
            try { StartCapture(); }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Error captura: {ex.Message}"); }
        }

        private void StartCapture()
        {
            _capture = new WasapiLoopbackCapture();
            _captureFormat = _capture.WaveFormat;
            _buffer = new MemoryStream();

            _capture.DataAvailable += (_, e) =>
            {
                lock (_lock)
                    _buffer.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _capture.RecordingStopped += (_, e) =>
            {
                if (_isRunning)
                    StatusChanged?.Invoke(this, "Audio detenido inesperadamente");
            };

            _capture.StartRecording();
            _transcribeTimer = new Timer(TranscribeChunk, null, ChunkIntervalMs, ChunkIntervalMs);
            _isRunning = true;
            StatusChanged?.Invoke(this, "Escuchando audio del sistema...");
        }

        private async void TranscribeChunk(object? _)
        {
            if (_isTranscribing)
            {
                // Keep buffer bounded — discard oldest audio so we stay close to real-time
                lock (_lock)
                {
                    if (_captureFormat != null)
                    {
                        long maxBytes = (long)(_captureFormat.AverageBytesPerSecond * MaxBufferSeconds);
                        if (_buffer.Length > maxBytes)
                        {
                            byte[] all = _buffer.ToArray();
                            _buffer = new MemoryStream();
                            // Keep only the most recent MaxBufferSeconds of audio
                            _buffer.Write(all, (int)(all.Length - maxBytes), (int)maxBytes);
                        }
                    }
                }
                return;
            }
            _isTranscribing = true;

            byte[] raw;
            WaveFormat format;

            lock (_lock)
            {
                // Require at least 1 second of audio before transcribing
                if (_captureFormat == null || _buffer.Length < _captureFormat.AverageBytesPerSecond)
                {
                    _isTranscribing = false;
                    return;
                }

                raw = _buffer.ToArray();
                _buffer = new MemoryStream();
                format = _captureFormat;
            }

            try
            {
                // Resample: system format (48kHz stereo float) → 16kHz mono 16-bit (Whisper)
                using var rawStream = new RawSourceWaveStream(new MemoryStream(raw), format);
                var mono      = rawStream.ToSampleProvider().ToMono();
                var resampled = new WdlResamplingSampleProvider(mono, 16000);
                WaveFileWriter.CreateWaveFile16(_tempFile, resampled);

                StatusChanged?.Invoke(this, "Transcribiendo...");
                string text = await _whisper.TranscribeAsync(_tempFile);

                if (!string.IsNullOrWhiteSpace(text))
                    TextCaptured?.Invoke(this, text.Trim());

                StatusChanged?.Invoke(this, "Escuchando audio del sistema...");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Error Whisper: {ex.Message[..Math.Min(60, ex.Message.Length)]}");
            }
            finally
            {
                _isTranscribing = false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _transcribeTimer?.Dispose();
            _transcribeTimer = null;
            try { _capture?.StopRecording(); } catch { }
            _capture?.Dispose();
            _capture = null;
            StatusChanged?.Invoke(this, "Pausado");
        }

        public void Dispose()
        {
            Stop();
            _buffer.Dispose();
        }
    }
}
