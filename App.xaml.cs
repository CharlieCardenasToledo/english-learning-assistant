using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using EnglishLearningAssistant.Core;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Application.Sessions;
using EnglishLearningAssistant.Infrastructure.Captions;
using EnglishLearningAssistant.Infrastructure.Transcription;
using EnglishLearningAssistant.Infrastructure.Translation;
using WindowsLiveCaptionsReader.Services;

namespace WindowsLiveCaptionsReader
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant", "settings.json");

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize AppLogger first (T0.3)
            EnglishLearningAssistant.Core.AppLogger.Initialize();

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                EnglishLearningAssistant.Core.AppLogger.Error("Fatal unhandled exception", ex.ExceptionObject as Exception);
                MessageBox.Show($"Fatal Error: {ex.ExceptionObject}", "Application Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, ex) =>
            {
                EnglishLearningAssistant.Core.AppLogger.Error("Unhandled UI exception", ex.Exception);
                MessageBox.Show($"UI Error: {ex.Exception.Message}", "Application Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            // Configure DI container (T0.2)
            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            EnglishLearningAssistant.Core.AppLogger.Shutdown();
            base.OnExit(e);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Logging integration
            services.AddLogging(builder => builder.AddSerilog(dispose: true));

            // Core Config
            services.AddSingleton(AppConfiguration.Instance);

            // Existing Legacy/Refactored Services (registered for compatibility)
            services.AddSingleton<CaptionReader>();
            services.AddSingleton<LmStudioService>(sp => new LmStudioService(sp.GetRequiredService<AppConfiguration>().LmStudio.ModelName));
            services.AddSingleton<WhisperService>();
            services.AddSingleton<AudioCaptureService>();
            services.AddSingleton<SessionService>();
            services.AddSingleton<QuestionDetectionService>();
            services.AddSingleton<VocabularyService>();

            // Application Services
            services.AddTransient<EnglishLearningAssistant.Application.Transcription.FileTranscriptionService>();

            // Providers

            services.AddTransient<WindowsLiveCaptionsProvider>();
            services.AddTransient<WhisperProvider>();
            services.AddTransient<LiveWhisperProvider>();
            services.AddTransient<LmStudioTranslationProvider>();
            services.AddTransient<LibreTranslateTranslationProvider>();
            services.AddTransient<FallbackTranslationProvider>();

            // Factory logic for Transcription & Translation providers
            services.AddTransient<ITranscriptionProvider>(sp =>
            {
                var config = sp.GetRequiredService<AppConfiguration>();
                var providerName = config.Transcription.Provider;

                if (providerName.Equals("Whisper", StringComparison.OrdinalIgnoreCase))
                {
                    return sp.GetRequiredService<WhisperProvider>();
                }
                else if (providerName.Equals("WhisperLive", StringComparison.OrdinalIgnoreCase) ||
                         providerName.Equals("WhisperLoopback", StringComparison.OrdinalIgnoreCase))
                {
                    return sp.GetRequiredService<LiveWhisperProvider>();
                }
                else
                {
                    return sp.GetRequiredService<WindowsLiveCaptionsProvider>();
                }
            });

            services.AddTransient<ITranslationProvider>(sp =>
            {
                // FallbackTranslationProvider automatically manages fallback: LM Studio -> LibreTranslate
                return sp.GetRequiredService<FallbackTranslationProvider>();
            });


            // Session Orchestrator options & instance
            services.AddTransient<OrchestratorOptions>(sp =>
            {
                var config = sp.GetRequiredService<AppConfiguration>();
                return new OrchestratorOptions
                {
                    CefrLevel = config.CefrLevel,
                    StudentName = config.StudentName,
                    MinWordsForAutoTranslate = 5
                };
            });
            services.AddTransient<SessionOrchestrator>();

            // Windows
            services.AddTransient<MainWindow>();
            services.AddTransient<SetupWindow>();
            services.AddTransient<VocabularyWindow>();
        }


        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Dark);

            // Skip setup if already configured before
            if (File.Exists(SettingsPath))
            {
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            else
            {
                var setupWindow = ServiceProvider.GetRequiredService<SetupWindow>();
                setupWindow.Show();
            }
        }
    }
}
