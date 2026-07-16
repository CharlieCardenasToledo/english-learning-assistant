using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using WindowsLiveCaptionsReader.Data;
using WindowsLiveCaptionsReader.Models;

namespace EnglishLearningAssistant.Infrastructure.Translation;

/// <summary>
/// Proveedor de traducción compuesto que implementa fallback automático (Fase 6 - T6.2).
/// Si falla el proveedor primario (LM Studio), usa el secundario (LibreTranslate) si está habilitado.
/// Incorpora además una caché local en SQLite (Fase 6 - T6.3) para evitar llamadas duplicadas.
/// </summary>
public sealed class FallbackTranslationProvider : ITranslationProvider
{
    private readonly LmStudioTranslationProvider _primary;
    private readonly LibreTranslateTranslationProvider _secondary;
    private readonly AppConfiguration _config;
    private readonly ILogger<FallbackTranslationProvider> _logger;
    private readonly IServiceProvider _serviceProvider;

    public string Name => "FallbackTranslator";

    public FallbackTranslationProvider(
        LmStudioTranslationProvider primary,
        LibreTranslateTranslationProvider secondary,
        AppConfiguration config,
        ILogger<FallbackTranslationProvider> logger,
        IServiceProvider serviceProvider)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    private AppDbContext CreateDbContext()
    {
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<AppDbContext>(_serviceProvider);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Disponible si el primario está online, o si el secundario está online y habilitado como fallback
        if (await _primary.IsAvailableAsync(cancellationToken))
        {
            return true;
        }

        if (_config.Translation.FallbackToLibreTranslate)
        {
            return await _secondary.IsAvailableAsync(cancellationToken);
        }

        return false;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Empty(text);
        }

        // 1. Intentar consultar caché (T6.3)
        if (_config.Translation.EnableCache)
        {
            try
            {
                using var db = CreateDbContext();
                var cleanText = text.Trim().ToLower();
                var cached = await db.TranslationCache.FirstOrDefaultAsync(c =>
                    c.OriginalText.ToLower() == cleanText &&
                    c.SourceLanguage == sourceLanguage &&
                    c.TargetLanguage == targetLanguage,
                    cancellationToken);

                if (cached != null)
                {
                    _logger.LogInformation("Cache hit para traducción: {Text} -> {Translation}", text, cached.TranslatedText);
                    return new TranslationResult
                    {
                        OriginalText = text,
                        TranslatedText = cached.TranslatedText,
                        SourceLanguage = sourceLanguage,
                        TargetLanguage = targetLanguage,
                        ProviderName = cached.ProviderName,
                        IsFromCache = true
                    };
                }
            }
            catch (Exception cacheEx)
            {
                _logger.LogWarning(cacheEx, "Error al consultar la caché de traducción");
            }
        }

        TranslationResult? result = null;

        // 2. Intentar primero con LM Studio
        try
        {
            result = await _primary.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "El proveedor primario (LM Studio) falló. Intentando fallback a LibreTranslate...");

            if (_config.Translation.FallbackToLibreTranslate)
            {
                try
                {
                    result = await _secondary.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
                    _logger.LogInformation("Traducción completada con éxito vía fallback (LibreTranslate).");
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "El proveedor secundario (LibreTranslate) también falló.");
                    throw new AggregateException("Ambos proveedores de traducción fallaron.", ex, fallbackEx);
                }
            }
            else
            {
                throw; // Fallback deshabilitado, relanzar error original
            }
        }

        // 3. Guardar en caché si se completó con éxito (T6.3)
        if (result != null && _config.Translation.EnableCache && !string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            try
            {
                using var db = CreateDbContext();
                db.TranslationCache.Add(new TranslationCacheEntry
                {
                    OriginalText = text,
                    TranslatedText = result.TranslatedText,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    ProviderName = result.ProviderName,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception cacheEx)
            {
                _logger.LogWarning(cacheEx, "Error al guardar la traducción en caché");
            }
        }

        return result ?? TranslationResult.Empty(text);
    }
}

