using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.Infrastructure.Translation;

/// <summary>
/// Adaptador que envuelve el <c>LmStudioService</c> existente como <see cref="ITranslationProvider"/>.
/// LM Studio expone una API compatible con OpenAI en http://localhost:1234.
///
/// Características:
///   - Traducción vía streaming (TranslateStreamAsync)
///   - Health check cada 30s (IsRunningAsync)
///   - Fallback automático a LibreTranslateProvider si LM Studio no responde
///
/// (T6.1)
/// </summary>
public sealed class LmStudioTranslationProvider : ITranslationProvider, ITextGenerationProvider
{
    private readonly LmStudioService _service;
    private readonly ILogger<LmStudioTranslationProvider> _logger;
    private readonly AppConfiguration _config;

    public string Name => $"LM Studio ({_config.LmStudio.ModelName})";

    public LmStudioTranslationProvider(
        LmStudioService service,
        ILogger<LmStudioTranslationProvider> logger,
        AppConfiguration config)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try { return await _service.IsRunningAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verificando disponibilidad de LM Studio");
            return false;
        }
    }

    /// <summary>
    /// Traduce usando LM Studio con streaming.
    /// Solo llamar con segmentos confirmados (IsPartial = false).
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslationResult.Empty(text);

        _logger.LogDebug("Traduciendo ({From}→{To}): {Text}",
            sourceLanguage, targetLanguage, text[..Math.Min(40, text.Length)]);

        try
        {
            string translated = await _service.TranslateStreamAsync(
                text,
                onPartialUpdate: _ => { }, // no streaming callback needed en este adaptador
                token: cancellationToken);
            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = translated,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                ProviderName = Name,
                IsFromCache = false
            };
        }
        catch (OperationCanceledException) { return TranslationResult.Empty(text); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en traducción LM Studio");
            return TranslationResult.Empty(text);
        }
    }
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, Action<string>? onPartialUpdate = null, CancellationToken cancellationToken = default)
    {
        return await _service.StreamChatAsync(systemPrompt, userPrompt, onPartialUpdate ?? (_ => { }), cancellationToken);
    }

}
