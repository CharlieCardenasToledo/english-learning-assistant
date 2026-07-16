using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using MsILogger = Microsoft.Extensions.Logging.ILogger;

namespace EnglishLearningAssistant.Core;

/// <summary>
/// Configura Serilog como backend de logging estructurado.
/// Uso: AppLogger.Initialize() en App.xaml.cs antes de crear la MainWindow.
/// Logging escribe a consola de debug + archivo rotativo en %AppData%.
/// (T0.3)
/// </summary>
public static class AppLogger
{
    private static ILoggerFactory? _factory;

    /// <summary>Inicializa Serilog con sink a archivo y a debug output.</summary>
    public static void Initialize()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EnglishLearningAssistant",
            "logs");

        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.WithProperty("App", "EnglishLearningAssistant")
            .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _factory = LoggerFactory.Create(builder =>
            builder.AddSerilog(dispose: false));

        Log.Information("=== English Learning Assistant iniciado ===");
    }

    /// <summary>Crea un ILogger tipado para la clase T.</summary>
    public static ILogger<T> CreateLogger<T>() =>
        (_factory ?? throw new InvalidOperationException("AppLogger.Initialize() debe llamarse primero."))
        .CreateLogger<T>();

    /// <summary>Crea un ILogger con la categoría indicada.</summary>
    public static MsILogger CreateLogger(string categoryName) =>
        (_factory ?? throw new InvalidOperationException("AppLogger.Initialize() debe llamarse primero."))
        .CreateLogger(categoryName);

    /// <summary>Escribe un mensaje informativo rápido desde cualquier lugar.</summary>
    public static void Info(string message) => Log.Information(message);

    /// <summary>Escribe un error con excepción opcional.</summary>
    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null) Log.Error(message);
        else Log.Error(ex, message);
    }

    /// <summary>Flush y cierre de Serilog al salir de la app.</summary>
    public static void Shutdown()
    {
        Log.Information("=== English Learning Assistant cerrando ===");
        Log.CloseAndFlush();
        _factory?.Dispose();
    }
}
