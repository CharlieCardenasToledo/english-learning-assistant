using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;

namespace EnglishLearningAssistant.Infrastructure.Translation;

/// <summary>
/// Proveedor de traducción secundario usando LibreTranslate (Fase 6 - T6.1).
/// </summary>
public sealed class LibreTranslateTranslationProvider : ITranslationProvider
{
    private readonly ILogger<LibreTranslateTranslationProvider> _logger;
    private readonly AppConfiguration _config;
    private readonly HttpClient _httpClient;

    public string Name => "LibreTranslate";

    public LibreTranslateTranslationProvider(
        ILogger<LibreTranslateTranslationProvider> logger,
        AppConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5) // Timeout corto para fallbacks rápidos
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var baseUrl = _config.LibreTranslate.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;

            // Verificar disponibilidad con la API de idiomas soportados
            var requestUrl = $"{baseUrl}/languages";
            using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

        try
        {
            var baseUrl = _config.LibreTranslate.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("La URL base de LibreTranslate no está configurada.");
            }

            var requestUrl = $"{baseUrl}/translate";
            var payload = new
            {
                q = text,
                source = sourceLanguage,
                target = targetLanguage,
                format = "text",
                api_key = _config.LibreTranslate.ApiKey ?? string.Empty
            };

            using var response = await _httpClient.PostAsJsonAsync(requestUrl, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("LibreTranslate retornó código de error {Code}: {Error}", response.StatusCode, errContent);
                throw new HttpRequestException($"LibreTranslate retornó estado {response.StatusCode}");
            }

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            if (doc == null)
            {
                throw new InvalidOperationException("Respuesta vacía de LibreTranslate.");
            }

            var translatedText = doc.RootElement.GetProperty("translatedText").GetString() ?? string.Empty;

            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = translatedText,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                ProviderName = Name,
                IsFromCache = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al traducir vía LibreTranslate: {Text}", text);
            throw;
        }
    }
}
