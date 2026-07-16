using System;

namespace WindowsLiveCaptionsReader.Models;

/// <summary>
/// Entrada de caché para almacenar traducciones y evitar llamadas repetidas al LLM (Fase 6 - T6.3).
/// </summary>
public sealed class TranslationCacheEntry
{
    public int Id { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
