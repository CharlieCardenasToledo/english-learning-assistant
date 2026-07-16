namespace EnglishLearningAssistant.Core.Models;

/// <summary>
/// Parámetros para iniciar una sesión de transcripción.
/// </summary>
public sealed class TranscriptionRequest
{
    /// <summary>Nivel CEFR esperado del estudiante (A1, A2, B1, B2, C1, C2).</summary>
    public string CefrLevel { get; init; } = "B1";

    /// <summary>Idioma de origen (en = inglés por defecto).</summary>
    public string SourceLanguage { get; init; } = "en";

    /// <summary>
    /// Ruta al archivo de audio/video para importación.
    /// Null = transcripción en vivo (micrófono o Live Captions).
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>Nombre del dispositivo de audio preferido. Null = dispositivo por defecto.</summary>
    public string? AudioDeviceName { get; init; }

    /// <summary>
    /// Grabar audio de la sesión para reproducción posterior.
    /// Solo aplica a sesiones en vivo con Whisper.
    /// </summary>
    public bool RecordAudio { get; init; } = true;
}
