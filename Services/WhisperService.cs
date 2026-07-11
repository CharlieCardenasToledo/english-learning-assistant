using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace WindowsLiveCaptionsReader.Services
{
    public class WhisperService : IDisposable
    {
        private WhisperFactory? _whisperFactory;
        private WhisperProcessor? _processor;
        private string _modelPath;
        private GgmlType _modelType = GgmlType.SmallEn;
        private string _modelFileName = "ggml-small.en.bin";

        public bool IsModelLoaded => _processor != null;

        // ── Available model presets ──────────────────────────────────────────────
        public record ModelOption(string DisplayName, GgmlType Type, string FileName, long MinBytes);
        public static readonly ModelOption[] AvailableModels =
        [
            new("Tiny English (~75 MB) — Más rápido",    GgmlType.TinyEn,  "ggml-tiny.en.bin",   65_000_000),
            new("Base (~148 MB) — Balanceado",            GgmlType.Base,    "ggml-base.bin",      133_000_000),
            new("Small English (~465 MB) — Recomendado", GgmlType.SmallEn, "ggml-small.en.bin",  418_000_000),
            new("Medium (~1.5 GB) — Alta calidad",        GgmlType.Medium,  "ggml-medium.bin",  1_350_000_000),
        ];

        // Prevents concurrent calls from racing on the same files
        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);

        private long _minModelBytes = 418_000_000;

        public event EventHandler<string>? DownloadProgress;

        private readonly string _modelFolder;

        public WhisperService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _modelFolder = Path.Combine(appData, "WindowsLiveCaptionsReader", "Models");
            Directory.CreateDirectory(_modelFolder);
            _modelPath = Path.Combine(_modelFolder, _modelFileName);
        }

        /// <summary>Switches to a different model. Disposes the current one; next transcription will re-initialize.</summary>
        public void ChangeModel(ModelOption option)
        {
            _modelType    = option.Type;
            _modelFileName = option.FileName;
            _minModelBytes = option.MinBytes;
            _modelPath    = Path.Combine(_modelFolder, _modelFileName);

            DisposeProcessorSafely();
            _whisperFactory?.Dispose();
            _whisperFactory = null;
        }

        public async Task InitializeAsync()
        {
            // If already loaded, return immediately without waiting for the lock
            if (_processor != null) return;

            await _initLock.WaitAsync();
            try
            {
                // Re-check inside the lock — another caller may have finished while we waited
                if (_processor != null) return;

                bool needsDownload = !File.Exists(_modelPath)
                    || new FileInfo(_modelPath).Length < _minModelBytes;

                if (needsDownload)
                {
                    if (File.Exists(_modelPath)) File.Delete(_modelPath);
                    await DownloadModelAsync();
                }

                _whisperFactory = WhisperFactory.FromPath(_modelPath);
                _processor = _whisperFactory.CreateBuilder()
                    .WithLanguage("en")
                    .Build();
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task DownloadModelAsync()
        {
            DownloadProgress?.Invoke(this, $"Descargando {_modelFileName}... 0%");
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(_modelType);

            // Write to a temp file; only rename to final path when fully complete
            string tempPath = _modelPath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);

            long total;
            if (modelStream.CanSeek && modelStream.Length > 0)
                total = modelStream.Length;
            else
                total = _minModelBytes; // use the minimum as conservative denominator

            var  buffer   = new byte[81_920]; // 80 KB chunks
            long written  = 0;
            int  lastPct  = -1;
            int  read;

            using (var fileWriter = File.OpenWrite(tempPath))
            {
                while ((read = await modelStream.ReadAsync(buffer)) > 0)
                {
                    await fileWriter.WriteAsync(buffer.AsMemory(0, read));
                    written += read;
                    // Clamp to 99 while still downloading; 100 only on confirmed completion
                    int pct = (int)Math.Min(99, written * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        DownloadProgress?.Invoke(this, $"Descargando {_modelFileName}... {pct}%");
                    }
                }
            }

            File.Move(tempPath, _modelPath, overwrite: true);
            DownloadProgress?.Invoke(this, $"Descargando {_modelFileName}... 100%");
            DownloadProgress?.Invoke(this, "Cargando modelo en memoria...");
        }

        public async Task<string> TranscribeAsync(string wavFilePath)
        {
            if (_processor == null) await InitializeAsync();
            if (_processor == null) throw new InvalidOperationException("Whisper processor no disponible");

            if (!File.Exists(wavFilePath)) return "";

            using var fileStream = File.OpenRead(wavFilePath);
            var text = "";

            await foreach (var result in _processor.ProcessAsync(fileStream))
                text += result.Text + " ";

            return text.Trim();
        }

        public void Dispose()
        {
            DisposeProcessorSafely();
            _whisperFactory?.Dispose();
        }

        // WhisperProcessor.Dispose() throws if a ProcessAsync enumeration is still
        // running (e.g. closing the app mid-chunk). DisposeAsync waits for the
        // in-flight transcription to finish first.
        private void DisposeProcessorSafely()
        {
            var processor = _processor;
            _processor = null;
            if (processor == null) return;

            try { processor.Dispose(); }
            catch (Exception)
            {
                try { processor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
                catch { }
            }
        }
    }
}
