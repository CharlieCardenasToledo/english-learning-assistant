using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using TauriDotNetBridge.Contracts;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Services;

// ─── Model catalog ────────────────────────────────────────────────────────────

public sealed record BuiltInModelInfo(
    string Id,
    string DisplayName,
    string Description,
    string Repo,
    string Filename,
    long   FileSizeBytes,
    int    RequiredRamGb);

public sealed class BuiltInModelStatus
{
    public string Id            { get; init; } = "";
    public string DisplayName   { get; init; } = "";
    public string Description   { get; init; } = "";
    public long   FileSizeBytes { get; init; }
    public int    RequiredRamGb { get; init; }
    public bool   IsRecommended { get; init; }
    public string Status        { get; init; } = "not_downloaded"; // not_downloaded | downloading | available | corrupted
    public double FileSizeMb    => FileSizeBytes / (1024.0 * 1024.0);
}

// ─── Service ──────────────────────────────────────────────────────────────────

public sealed class BuiltInAiService
{
    static readonly BuiltInModelInfo[] Catalog =
    [
        new("qwen2.5-0.5b",
            "Qwen 2.5 · 0.5B",
            "Ligero · ideal para equipos con poca RAM",
            "bartowski/Qwen2.5-0.5B-Instruct-GGUF",
            "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf",
            338_000_000L, 4),

        new("qwen2.5-1.5b",
            "Qwen 2.5 · 1.5B",
            "Equilibrado · buena calidad en equipos medios",
            "bartowski/Qwen2.5-1.5B-Instruct-GGUF",
            "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
            985_000_000L, 8),

        new("qwen2.5-3b",
            "Qwen 2.5 · 3B",
            "Calidad · recomendado con 16+ GB de RAM",
            "bartowski/Qwen2.5-3B-Instruct-GGUF",
            "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
            1_931_000_000L, 16),
    ];

    private readonly IEventPublisher _publisher;
    private readonly ILogger<BuiltInAiService> _logger;
    private readonly string _modelsDir;

    private readonly object _downloadGate = new();
    private CancellationTokenSource? _downloadCts;
    private volatile string? _downloadingModelId;

    public BuiltInAiService(IEventPublisher publisher, ILogger<BuiltInAiService> logger)
    {
        _publisher = publisher;
        _logger    = logger;
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant", "models");
    }

    // ── Hardware ─────────────────────────────────────────────────────────────

    public HardwareSpecs GetHardware() => HardwareDetector.DetectHardware();

    public string GetRecommendedModelId(HardwareSpecs hw) =>
        hw.TotalRamGb switch
        {
            >= 16 => "qwen2.5-3b",
            >= 8  => "qwen2.5-1.5b",
            _     => "qwen2.5-0.5b",
        };

    // ── Model status ──────────────────────────────────────────────────────────

    public IReadOnlyList<BuiltInModelStatus> GetAllStatuses()
    {
        var hw          = GetHardware();
        var recommended = GetRecommendedModelId(hw);

        return Catalog.Select(m => new BuiltInModelStatus
        {
            Id            = m.Id,
            DisplayName   = m.DisplayName,
            Description   = m.Description,
            FileSizeBytes = m.FileSizeBytes,
            RequiredRamGb = m.RequiredRamGb,
            IsRecommended = m.Id == recommended,
            Status        = _downloadingModelId == m.Id ? "downloading" : FileStatus(m),
        }).ToArray();
    }

    public string GetModelPath(string modelId)
    {
        var model = Catalog.First(m => m.Id == modelId);
        return Path.Combine(_modelsDir, model.Filename);
    }

    string FileStatus(BuiltInModelInfo m)
    {
        var path = Path.Combine(_modelsDir, m.Filename);
        if (!File.Exists(path)) return "not_downloaded";
        var len = new FileInfo(path).Length;
        if (len < m.FileSizeBytes * 0.9) return "incomplete";
        return IsValidGguf(path) ? "available" : "corrupted";
    }

    static bool IsValidGguf(string path)
    {
        try
        {
            Span<byte> buf = stackalloc byte[4];
            using var fs = File.OpenRead(path);
            return fs.Read(buf) == 4 &&
                   buf[0] == 0x47 && buf[1] == 0x47 && buf[2] == 0x55 && buf[3] == 0x46;
        }
        catch { return false; }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public Task StartDownloadAsync(string modelId)
    {
        var model = Catalog.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        CancellationTokenSource download;
        lock (_downloadGate)
        {
            _downloadCts?.Cancel();
            download = new CancellationTokenSource();
            _downloadCts = download;
            _downloadingModelId = modelId;
        }

        _ = Task.Run(() => DownloadAsync(model, download));
        return Task.CompletedTask;
    }

    public void CancelDownload()
    {
        CancellationTokenSource? current;
        lock (_downloadGate)
        {
            current = _downloadCts;
            _downloadCts = null;
            _downloadingModelId = null;
        }
        current?.Cancel();
    }

    private bool ClearDownload(CancellationTokenSource download)
    {
        lock (_downloadGate)
        {
            if (!ReferenceEquals(_downloadCts, download)) return false;
            _downloadCts = null;
            _downloadingModelId = null;
            return true;
        }
    }

    async Task DownloadAsync(BuiltInModelInfo model, CancellationTokenSource download)
    {
        var token = download.Token;
    {
        Directory.CreateDirectory(_modelsDir);
        var dest = Path.Combine(_modelsDir, model.Filename);
        var url  = $"https://huggingface.co/{model.Repo}/resolve/main/{model.Filename}";

        var existingBytes = File.Exists(dest) ? new FileInfo(dest).Length : 0L;
        if (existingBytes > 0 &&
            (!IsValidGguf(dest) || existingBytes >= model.FileSizeBytes * 0.99))
        {
            File.Delete(dest);
            existingBytes = 0;
        }
        _logger.LogInformation("Downloading {Model} from {Url} (resume at {Bytes} B)", model.Id, url, existingBytes);

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EnglishLearningAssistant/1.0");

            if (existingBytes > 0)
                http.DefaultRequestHeaders.Range = new RangeHeaderValue(existingBytes, null);

            Emit(model.Id, 0, existingBytes, model.FileSizeBytes, 0, "downloading");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var resumed = existingBytes > 0 &&
                response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed) existingBytes = 0;

            var contentLen = response.Content.Headers.ContentLength ?? model.FileSizeBytes;
            var totalBytes = response.Content.Headers.ContentRange?.Length ??
                (resumed ? existingBytes + contentLen : contentLen);

            using var net  = await response.Content.ReadAsStreamAsync(token);
            await using var file = new FileStream(dest,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 8 * 1024 * 1024);

            var buf         = new byte[65_536];
            var downloaded  = existingBytes;
            var lastTime    = DateTime.UtcNow;
            var lastBytes   = existingBytes;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await net.ReadAsync(buf, token);
                if (read == 0) break;

                await file.WriteAsync(buf.AsMemory(0, read), token);
                downloaded += read;

                var now = DateTime.UtcNow;
                if ((now - lastTime).TotalMilliseconds >= 500)
                {
                    var secs  = (now - lastTime).TotalSeconds;
                    var speed = (downloaded - lastBytes) / secs / (1024 * 1024);
                    Emit(model.Id, (double)downloaded / totalBytes, downloaded, totalBytes, speed, "downloading");
                    lastTime  = now;
                    lastBytes = downloaded;
                }
            }

            await file.FlushAsync(token);
            ClearDownload(download);

            if (FileStatus(model) == "available")
            {
                _logger.LogInformation("{Model} downloaded and validated.", model.Id);
                Emit(model.Id, 1.0, totalBytes, totalBytes, 0, "available");
            }
            else
            {
                _logger.LogWarning("{Model} failed GGUF validation, deleting.", model.Id);
                File.Delete(dest);
                Emit(model.Id, 0, 0, model.FileSizeBytes, 0, "corrupted");
            }
        }
        catch (OperationCanceledException)
        {
            var wasCurrent = ClearDownload(download);
            var partialBytes = File.Exists(dest) ? new FileInfo(dest).Length : 0L;
            _logger.LogInformation("Download of {Model} cancelled.", model.Id);
            if (wasCurrent) Emit(model.Id, 0, partialBytes, model.FileSizeBytes, 0, "incomplete");
        }
        catch (Exception ex)
        {
            var wasCurrent = ClearDownload(download);
            _logger.LogError(ex, "Error downloading {Model}.", model.Id);
            if (wasCurrent) Emit(model.Id, 0, 0, model.FileSizeBytes, 0, "error");
        }
    }
    }

    void Emit(string modelId, double progress, long downloadedBytes, long totalBytes, double speedMbps, string status) =>
        _publisher.Publish("builtin-ai-progress", new
        {
            model        = modelId,
            progress,
            downloaded_mb = downloadedBytes / (1024.0 * 1024.0),
            total_mb      = totalBytes      / (1024.0 * 1024.0),
            speed_mbps    = speedMbps,
            status,
        });
}
