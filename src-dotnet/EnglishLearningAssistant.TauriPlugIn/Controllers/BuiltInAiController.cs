using System.IO;
using EnglishLearningAssistant.TauriPlugIn.Services;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.TauriPlugIn.Providers;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class BuiltInAiController
{
    private readonly BuiltInAiService _service;
    private readonly LocalLlamaTranslationProvider _localProvider;
    private readonly AppConfiguration _config;

    public BuiltInAiController(BuiltInAiService service, LocalLlamaTranslationProvider localProvider, AppConfiguration config)
    {
        _service = service;
        _localProvider = localProvider;
        _config = config;
    }

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
    public object StartDownload(StartDownloadRequest req)
    {
        _service.StartDownloadAsync(req.ModelId).GetAwaiter().GetResult();
        return new { ok = true };
    }

    /// <summary>Loads the selected model and performs a real inference before onboarding completes.</summary>
    public object TestModel(TestModelRequest req)
    {
        try
        {
            _config.LmStudio.ModelName = req.ModelId;
            // Task.Run evita el deadlock de .GetAwaiter().GetResult() al salir del
            // contexto de sincronización del dispatcher de TauriDotNetBridge.
            var answer = Task.Run(() => _localProvider.GenerateAsync(
                "You are a model health checker. Reply with the single word READY.",
                "Reply READY.",
                cancellationToken: CancellationToken.None)).GetAwaiter().GetResult();
            var ok = !string.IsNullOrWhiteSpace(answer);
            return new { ok, model = req.ModelId, output = answer, error = ok ? null : (_localProvider.LastError ?? "El modelo no produjo una respuesta.") };
        }
        catch (Exception ex)
        {
            return new { ok = false, model = req.ModelId, output = "", error = ex.Message };
        }
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

public record TestModelRequest(string ModelId);
public record StartDownloadRequest(string ModelId);
public record DeleteModelRequest(string ModelId);
