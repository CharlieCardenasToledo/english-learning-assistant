using EnglishLearningAssistant.Core.Models;

namespace EnglishLearningAssistant.Core.Abstractions;

/// <summary>
/// Proveedor de transcripción unificado.
/// Implementaciones: WindowsLiveCaptionsProvider, WhisperProvider, ManualTextProvider.
/// Inspirado en el patrón de Meetily (Rust): audio pipeline → VAD → Whisper → TranscriptSegment.
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>Nombre identificador del proveedor (e.g. "Windows Live Captions", "Whisper local").</summary>
    string Name { get; }

    /// <summary>Indica si el proveedor emite resultados parciales además de los finales.</summary>
    bool SupportsPartialResults { get; }

    /// <summary>Inicializa recursos (modelo, dispositivo de audio, etc.).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inicia la transcripción y emite segmentos de forma asíncrona.
    /// El stream se completa cuando se llama a StopAsync o se cancela el token.
    /// </summary>
    IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Detiene la transcripción de forma limpia.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Libera recursos del proveedor.</summary>
    Task DisposeAsync();
}
