using System.IO;
using EnglishLearningAssistant.TauriPlugIn.Services;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class BuiltInAiController
{
    private readonly BuiltInAiService _service;

    public BuiltInAiController(BuiltInAiService service) => _service = service;

    /// <summary>Returns hardware info, all model statuses, and the recommended model ID.</summary>
    public object GetStatus()
    {
        var hw      = _service.GetHardware();
        var models  = _service.GetAllStatuses();

        bool hasNvidia = hw.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                         hw.GpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                         hw.GpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        bool hasAmd    = hw.GpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                         hw.GpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);

        return new
        {
            hardware = new
            {
                cpuName    = hw.CpuName,
                cpuCores   = hw.CpuCores,
                totalRamGb = Math.Round(hw.TotalRamGb, 1),
                gpuName    = hw.GpuName,
                gpuType    = hasNvidia ? "nvidia" : hasAmd ? "amd" : "integrated",
                freeDiskGb = Math.Round(hw.FreeDiskGb, 1),
            },
            models,
            recommendedModelId = models.FirstOrDefault(m => m.IsRecommended)?.Id,
        };
    }

    /// <summary>Starts downloading the specified model in the background. Returns immediately.</summary>
    public async Task<object> StartDownload(StartDownloadRequest req)
    {
        await _service.StartDownloadAsync(req.ModelId);
        return new { ok = true };
    }

    /// <summary>Cancels any in-progress download.</summary>
    public object CancelDownload()
    {
        _service.CancelDownload();
        return new { ok = true };
    }

    /// <summary>Deletes a downloaded model file.</summary>
    public object DeleteModel(DeleteModelRequest req)
    {
        var path = _service.GetModelPath(req.ModelId);
        if (File.Exists(path)) File.Delete(path);
        return new { ok = true };
    }
}

public record StartDownloadRequest(string ModelId);
public record DeleteModelRequest(string ModelId);
