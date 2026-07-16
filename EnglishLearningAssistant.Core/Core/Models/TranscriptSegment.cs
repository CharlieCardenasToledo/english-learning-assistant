namespace EnglishLearningAssistant.Core.Models;

/// <summary>
/// Segmento de transcripción con timestamps.
/// Equivalente a Meetily Rust: struct Transcript { audio_start_time, audio_end_time, ... }
/// y struct SpeechSegment { samples, start_timestamp_ms, end_timestamp_ms, confidence }
/// </summary>
public sealed class TranscriptSegment
{
    /// <summary>Identificador único del segmento.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Número de secuencia para ordenar segmentos del mismo proveedor.</summary>
    public long SequenceId { get; init; }

    /// <summary>Texto transcrito.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Tiempo de inicio relativo al inicio de la sesión.</summary>
    public TimeSpan StartTime { get; init; }

    /// <summary>Tiempo de fin relativo al inicio de la sesión.</summary>
    public TimeSpan EndTime { get; init; }

    /// <summary>Duración del segmento.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Confianza del modelo de transcripción (0.0 – 1.0). Null si no disponible.</summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// true = resultado parcial, puede cambiar.
    /// false = resultado confirmado, listo para traducir.
    /// IMPORTANTE: No traducir ni detectar preguntas en segmentos parciales.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>Nombre del proveedor que generó este segmento.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Momento absoluto en que se creó el segmento.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Indica si este segmento podría contener una pregunta (pre-filtro rápido).</summary>
    public bool MayContainQuestion => Text.Contains('?') || Text.TrimEnd().EndsWith("...");

    public override string ToString() =>
        $"[{StartTime:mm\\:ss\\.ff}-{EndTime:mm\\:ss\\.ff}] {(IsPartial ? "(parcial) " : "")}{Text}";
}
