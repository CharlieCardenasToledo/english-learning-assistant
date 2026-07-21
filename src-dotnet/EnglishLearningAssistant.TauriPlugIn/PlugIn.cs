using System.IO;
using EnglishLearningAssistant.Application.Sessions;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Infrastructure.Translation;
using EnglishLearningAssistant.TauriPlugIn.Controllers;
using EnglishLearningAssistant.TauriPlugIn.Providers;
using EnglishLearningAssistant.TauriPlugIn.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TauriDotNetBridge.Contracts;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn;

public class PlugIn : IPlugIn
{
    public void Initialize(IServiceCollection services)
    {
        EnglishLearningAssistant.Core.AppLogger.Initialize();

        // LLamaSharp no encuentra sus DLLs nativos cuando el runtime es cargado por
        // TauriDotNetBridge vía hostfxr. Assembly.Location puede ser null/vacío en este
        // contexto, así que intentamos múltiples fuentes para encontrar el directorio.
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(PlugIn).Assembly.Location);

            // Fallback: directorio del proceso ejecutable + "dotnet" (estructura de Tauri)
            if (string.IsNullOrEmpty(assemblyDir))
            {
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
                if (!string.IsNullOrEmpty(exeDir))
                    assemblyDir = Path.Combine(exeDir, "dotnet");
            }

            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var nativeDir = Path.Combine(assemblyDir, "runtimes", "win-x64", "native");
                Log.Information("LLamaSharp buscando native dir en: {Dir}", nativeDir);
                if (Directory.Exists(nativeDir))
                {
                    LLama.Native.NativeLibraryConfig.All.WithSearchDirectory(nativeDir);
                    Log.Information("LLamaSharp native dir configurado: {Dir}", nativeDir);
                }
                else
                {
                    Log.Warning("LLamaSharp native dir no encontrado: {Dir}", nativeDir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo configurar LLamaSharp native dir");
        }

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        var config = AppConfiguration.Instance;
        services.AddSingleton(config);

        // Transcription provider: Windows Live Captions (default) or Whisper
        services.AddSingleton<ITranscriptionProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WindowsLiveCaptionsProvider>>();
            return new WindowsLiveCaptionsProvider(logger);
        });

        // Translation provider: Built-in (LLamaSharp) | LM Studio | LibreTranslate
        services.AddSingleton<LmStudioTranslationProvider>();
        services.AddSingleton<LibreTranslateTranslationProvider>();
        services.AddSingleton<FallbackTranslationProvider>();
        services.AddSingleton<LocalLlamaTranslationProvider>();
        services.AddSingleton<ITranslationProvider>(sp =>
        {
            if (config.LmStudio.Provider.Equals("builtin", StringComparison.OrdinalIgnoreCase))
                return sp.GetRequiredService<LocalLlamaTranslationProvider>();
            return sp.GetRequiredService<FallbackTranslationProvider>();
        });

        // Core services
        services.AddSingleton<LmStudioService>(sp =>
            new LmStudioService(config.LmStudio.ModelName));
        services.AddSingleton<QuestionDetectionService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<VocabularyService>();

        // Session orchestrator (Transient: una instancia por sesión activa)
        services.AddTransient<OrchestratorOptions>(_ => new OrchestratorOptions
        {
            CefrLevel      = config.CefrLevel,
            StudentName    = config.StudentName,
            MinWordsForAutoTranslate = 5,
        });
        services.AddTransient<SessionOrchestrator>();

        // Built-in AI (hardware detection + model download)
        services.AddSingleton<BuiltInAiService>();
        services.AddSingleton<BuiltInAiController>();

        // Controllers
        services.AddSingleton<SessionController>();
        services.AddSingleton<VocabularyController>();
        services.AddSingleton<SettingsController>();

        // Hosted service: arranca las captions al iniciar Tauri
        services.AddSingleton<IHostedService, CaptionHostedService>();
    }
}
