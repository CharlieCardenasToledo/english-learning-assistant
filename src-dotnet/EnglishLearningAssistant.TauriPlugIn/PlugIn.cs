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
        // EF Core satellite assemblies for non-English cultures are not shipped in the NuGet
        // package. Setting InvariantCulture prevents the runtime from probing for them.
        System.Globalization.CultureInfo.CurrentUICulture =
            System.Globalization.CultureInfo.InvariantCulture;

        EnglishLearningAssistant.Core.AppLogger.Initialize();

        // LLamaSharp: el exe de Tauri está en AppData (CARGO_TARGET_DIR movido) y no puede
        // encontrar llama.dll/ggml.dll que están en src-tauri/target/dotnet/runtimes/win-x64/native/.
        // WithSearchDirectory no resuelve la dependencia transitiva ggml→llama, así que:
        // 1. Pre-cargamos ggml.dll con ruta absoluta (Windows lo registra en la tabla de módulos)
        // 2. Usamos WithLibrary para apuntar a llama.dll explícitamente (avx2 > avx > base)
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(PlugIn).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDir))
            {
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
                if (!string.IsNullOrEmpty(exeDir))
                    assemblyDir = Path.Combine(exeDir, "dotnet");
            }

            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var nativeBase = Path.Combine(assemblyDir, "runtimes", "win-x64", "native");
                string[] variants = ["avx2", "avx", ""];
                bool configured = false;
                foreach (var variant in variants)
                {
                    var dir   = string.IsNullOrEmpty(variant) ? nativeBase : Path.Combine(nativeBase, variant);
                    var ggml  = Path.Combine(dir, "ggml.dll");
                    var llama = Path.Combine(dir, "llama.dll");
                    if (!File.Exists(llama) || !File.Exists(ggml)) continue;

                    // Pre-cargar ggml para que Windows lo encuentre como dependencia de llama
                    System.Runtime.InteropServices.NativeLibrary.Load(ggml);
                    LLama.Native.NativeLibraryConfig.Instance.WithLibrary(llama, null);
                    Log.Information("LLamaSharp native configurado ({V}): {P}", string.IsNullOrEmpty(variant) ? "base" : variant, llama);
                    configured = true;
                    break;
                }
                if (!configured)
                    Log.Warning("LLamaSharp: no se encontraron llama.dll/ggml.dll bajo {Dir}", nativeBase);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo configurar LLamaSharp native library");
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
        services.AddSingleton<SmartTranslationProvider>();
        services.AddSingleton<ITranslationProvider>(sp =>
            sp.GetRequiredService<SmartTranslationProvider>());
        services.AddSingleton<ITextGenerationProvider>(sp =>
            sp.GetRequiredService<SmartTranslationProvider>());

        // Core services
        services.AddSingleton<LmStudioService>(sp =>
            new LmStudioService(
                config.LmStudio.ModelName,
                config.LmStudio.BaseUrl,
                config.LmStudio.ApiKey));
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

        // Hosted service: CaptionHostedService registrado como singleton concreto
        // para que SessionController pueda inyectarlo directamente.
        services.AddSingleton<CaptionHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CaptionHostedService>());
    }
}
