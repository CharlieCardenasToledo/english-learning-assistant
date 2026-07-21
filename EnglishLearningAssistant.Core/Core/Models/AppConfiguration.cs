using System.IO;
using Microsoft.Extensions.Configuration;

namespace EnglishLearningAssistant.Core.Models;

/// <summary>Configuración de acceso al LLM (LM Studio u otro proveedor OpenAI-compatible).</summary>
public sealed class LmStudioConfig
{
    public string Provider { get; set; } = "lmstudio";
    public string BaseUrl { get; set; } = "http://localhost:1234";
    public string ModelName { get; set; } = "llama-3.2-3b-instruct";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Configuración de LibreTranslate.</summary>
public sealed class LibreTranslateConfig
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiKey { get; set; }
}

/// <summary>Configuración de transcripción.</summary>
public sealed class TranscriptionConfig
{
    public string Provider { get; set; } = "WindowsLiveCaptions";
    public string WhisperModel { get; set; } = "base";
    public bool RecordAudio { get; set; } = true;
    public string? AudioDeviceName { get; set; }
}

/// <summary>Configuración de traducción.</summary>
public sealed class TranslationConfig
{
    public string Provider { get; set; } = "LmStudio";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "es";
    public bool EnableCache { get; set; } = true;
    public bool FallbackToLibreTranslate { get; set; } = true;
}

/// <summary>Configuración de almacenamiento.</summary>
public sealed class StorageConfig
{
    public string? DatabasePath { get; set; }
    public string? AudioRecordingsPath { get; set; }
    public string? ModelsCachePath { get; set; }
}

/// <summary>
/// Configuración centralizada de la aplicación.
/// Carga desde appsettings.json usando Microsoft.Extensions.Configuration.
/// (T0.4)
/// </summary>
public sealed class AppConfiguration
{
    private static AppConfiguration? _instance;
    private static readonly object _lock = new();

    public string StudentName { get; private set; } = "Charlie";
    public string CefrLevel { get; private set; } = "B1";
    public string Theme { get; private set; } = "System";
    public LmStudioConfig LmStudio { get; private set; } = new();
    public LibreTranslateConfig LibreTranslate { get; private set; } = new();
    public TranscriptionConfig Transcription { get; private set; } = new();
    public TranslationConfig Translation { get; private set; } = new();
    public StorageConfig Storage { get; private set; } = new();

    private IConfiguration? _raw;

    public static AppConfiguration Instance
    {
        get
        {
            if (_instance is null)
            {
                lock (_lock)
                {
                    _instance ??= Load();
                }
            }
            return _instance;
        }
    }

    private static AppConfiguration Load()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var baseDir = Path.Combine(appData, "EnglishLearningAssistant");
        var userSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant", "settings.json");

        // AppDomain.CurrentDomain.BaseDirectory devuelve "" cuando el runtime es cargado vía
        // netcorehost (Tauri/TauriDotNetBridge). En ese caso usamos la ubicación del assembly.
        var basePath = string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory)
            ? Path.GetDirectoryName(typeof(AppConfiguration).Assembly.Location)!
            : AppDomain.CurrentDomain.BaseDirectory;

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        if (File.Exists(userSettingsPath))
        {
            configBuilder.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false);
        }

        var raw = configBuilder.Build();
        var cfg = new AppConfiguration { _raw = raw };

        raw.GetSection("App").Bind(cfg);
        raw.GetSection("LmStudio").Bind(cfg.LmStudio);
        raw.GetSection("LibreTranslate").Bind(cfg.LibreTranslate);
        raw.GetSection("Transcription").Bind(cfg.Transcription);
        raw.GetSection("Translation").Bind(cfg.Translation);
        raw.GetSection("Storage").Bind(cfg.Storage);

        // Mapear configuraciones planas desde settings.json del usuario
        if (raw["userName"] is string user) cfg.StudentName = user;
        if (raw["englishLevel"] is string level) cfg.CefrLevel = level;
        if (raw["llmProvider"] is string llmProv) cfg.LmStudio.Provider = llmProv;
        if (raw["lmStudioBaseUrl"] is string lmBaseUrl) cfg.LmStudio.BaseUrl = lmBaseUrl;
        if (raw["lmStudioModel"] is string lmModel) cfg.LmStudio.ModelName = lmModel;
        if (raw["lmStudioApiKey"] is string lmApiKey) cfg.LmStudio.ApiKey = lmApiKey;
        if (raw["whisperModel"] is string whModel)
        {
            // Convertir "ggml-small.en.bin" -> "small.en"
            var cleanModel = whModel.Replace("ggml-", "").Replace(".bin", "");
            cfg.Transcription.WhisperModel = cleanModel;
        }

        // Rutas por defecto si no están configuradas
        cfg.Storage.DatabasePath ??= Path.Combine(baseDir, "data.db");
        cfg.Storage.AudioRecordingsPath ??= Path.Combine(baseDir, "recordings");
        cfg.Storage.ModelsCachePath ??= Path.Combine(baseDir, "models");

        return cfg;
    }

    /// <summary>Acceso al IConfiguration raw para leer secciones adicionales.</summary>
    public IConfiguration Raw => _raw ?? throw new InvalidOperationException("Configuration not loaded.");
}
