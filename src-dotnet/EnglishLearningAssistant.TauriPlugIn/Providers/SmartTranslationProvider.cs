using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Infrastructure.Translation;
using Microsoft.Extensions.Logging;
using TauriDotNetBridge.Contracts;

namespace EnglishLearningAssistant.TauriPlugIn.Providers;

/// <summary>
/// Proveedor de traducción inteligente que:
/// 1. Intenta el proveedor configurado por el usuario (builtin / lmstudio / openai).
/// 2. Si no está disponible, intenta LM Studio como fallback.
/// 3. Si tampoco, intenta LibreTranslate.
/// 4. Si ninguno funciona, emite el evento Tauri "translation-provider-error" para que
///    el frontend le muestre al usuario un aviso con un botón para configurar.
/// </summary>
public sealed class SmartTranslationProvider : ITranslationProvider, ITextGenerationProvider
{
    private readonly LocalLlamaTranslationProvider _builtin;
    private readonly LmStudioTranslationProvider   _lmStudio;
    private readonly LibreTranslateTranslationProvider _libre;
    private readonly AppConfiguration _config;
    private readonly IEventPublisher   _publisher;
    private readonly ILogger<SmartTranslationProvider> _logger;

    public string Name => "SmartTranslationProvider";

    public SmartTranslationProvider(
        LocalLlamaTranslationProvider builtin,
        LmStudioTranslationProvider   lmStudio,
        LibreTranslateTranslationProvider libre,
        AppConfiguration config,
        IEventPublisher publisher,
        ILogger<SmartTranslationProvider> logger)
    {
        _builtin   = builtin;
        _lmStudio  = lmStudio;
        _libre     = libre;
        _config    = config;
        _publisher = publisher;
        _logger    = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var (provider, _) = await ResolveProviderAsync(cancellationToken);
        return provider is not null;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TranslationResult.Empty(text);

        var (provider, reason) = await ResolveProviderAsync(cancellationToken);

        if (provider is null)
        {
            _logger.LogWarning("[SmartTranslation] Ningún proveedor disponible. Razón: {Reason}", reason);
            _publisher.Publish("translation-provider-error", new
            {
                reason,
                provider = (_config.LmStudio.Provider ?? "lmstudio"),
                details = reason == "builtin_no_model" ? "No se encontró un modelo GGUF descargado." : "No se pudo conectar con LM Studio.",
            });
            return TranslationResult.Empty(text);
        }

        _logger.LogDebug("[SmartTranslation] Traduciendo con proveedor: {Provider}", provider.Name);

        var result = await provider.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);

        // Si el proveedor seleccionado devolvió vacío (e.g. modelo sin outputs), reportar
        if (string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            _logger.LogWarning("[SmartTranslation] {Provider} devolvió resultado vacío para: {Text}", provider.Name, text[..Math.Min(40, text.Length)]);
            _publisher.Publish("translation-provider-error", new
            {
                reason = "empty_response",
                provider = provider.Name,
                details = "El proveedor respondió sin texto traducido.",
            });
        }

        return result;
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, Action<string>? onPartialUpdate = null, CancellationToken cancellationToken = default)
    {
        var (provider, _) = await ResolveProviderAsync(cancellationToken);
        if (provider is not ITextGenerationProvider generator) return string.Empty;
        return await generator.GenerateAsync(systemPrompt, userPrompt, onPartialUpdate, cancellationToken);
    }

    // ─── Resolución de proveedor activo ──────────────────────────────────────

    private async Task<(ITranslationProvider? Provider, string Reason)> ResolveProviderAsync(
        CancellationToken cancellationToken)
    {
        var configured = _config.LmStudio.Provider ?? "lmstudio";

        // 1. Proveedor configurado por el usuario
        if (configured.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            if (await _builtin.IsAvailableAsync(cancellationToken))
                return (_builtin, string.Empty);

            _logger.LogWarning("[SmartTranslation] Proveedor builtin no disponible (modelo GGUF no descargado). Intentando LM Studio como fallback...");
        }

        // 2. LM Studio (siempre como fallback universal)
        if (await _lmStudio.IsAvailableAsync(cancellationToken))
            return (_lmStudio, string.Empty);

        // 3. LibreTranslate (último recurso)
        if (_config.Translation.FallbackToLibreTranslate &&
            await _libre.IsAvailableAsync(cancellationToken))
            return (_libre, string.Empty);

        // 4. Ninguno disponible
        var reason = configured.Equals("builtin", StringComparison.OrdinalIgnoreCase)
            ? "builtin_no_model"   // modelo GGUF no descargado
            : "lmstudio_offline";  // LM Studio no está corriendo

        return (null, reason);
    }
}
