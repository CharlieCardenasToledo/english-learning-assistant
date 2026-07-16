namespace EnglishLearningAssistant.Core.Abstractions;

/// <summary>
/// Proveedor de traducción unificado.
/// Implementaciones: LmStudioTranslationProvider, LibreTranslateProvider.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>Nombre identificador del proveedor.</summary>
    string Name { get; }

    /// <summary>Indica si el proveedor está disponible (conexión activa).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Traduce un texto.
    /// IMPORTANTE: Solo llamar con segmentos confirmados (IsPartial = false).
    /// </summary>
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}

/// <summary>Resultado de una traducción.</summary>
public sealed class TranslationResult
{
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public bool IsFromCache { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static TranslationResult Empty(string originalText) => new()
    {
        OriginalText = originalText,
        TranslatedText = string.Empty,
    };
}
