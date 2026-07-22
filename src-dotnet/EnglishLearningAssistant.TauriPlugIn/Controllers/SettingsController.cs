using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.TauriPlugIn.Services;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class SettingsController
{
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "builtin", "lmstudio", "ollama", "openai", "custom" };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EnglishLearningAssistant", "settings.json");

    private readonly BuiltInAiService _builtInAi;
    private readonly LmStudioService _llmService;

    public SettingsController(BuiltInAiService builtInAi, LmStudioService llmService)
    {
        _builtInAi = builtInAi;
        _llmService = llmService;
    }

    public object Get()
    {
        var cfg = AppConfiguration.Instance;
        return new
        {
            studentName = cfg.StudentName,
            cefrLevel = cfg.CefrLevel,
            llm = new
            {
                provider = cfg.LmStudio.Provider,
                endpoint = cfg.LmStudio.BaseUrl,
                model = cfg.LmStudio.ModelName,
                apiKey = cfg.LmStudio.ApiKey ?? "",
            },
            translation = new
            {
                enableCache = cfg.Translation.EnableCache,
                fallbackToLibreTranslate = cfg.Translation.FallbackToLibreTranslate,
            },
        };
    }

    public object Save(SaveSettingsRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(req.Fields);

        var studentName = GetFieldString(req.Fields, "userName");
        var cefrLevel = GetFieldString(req.Fields, "englishLevel");
        var provider = GetFieldString(req.Fields, "llmProvider");
        var endpoint = GetFieldString(req.Fields, "lmStudioBaseUrl");
        var model = GetFieldString(req.Fields, "lmStudioModel");
        var apiKey = GetFieldString(req.Fields, "lmStudioApiKey");

        if (studentName is not null && string.IsNullOrWhiteSpace(studentName))
            throw new ArgumentException("El nombre no puede estar vacío.");
        if (cefrLevel is not null && !IsValidCefrLevel(cefrLevel))
            throw new ArgumentException("El nivel de inglés no es válido.");
        if (provider is not null && !SupportedProviders.Contains(provider))
            throw new ArgumentException("El proveedor de IA no es válido.");
        if (provider is not null && !provider.Equals("builtin", StringComparison.OrdinalIgnoreCase))
            ValidateEndpoint(endpoint);

        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        var existing = ReadExistingSettings();
        foreach (var pair in req.Fields) existing[pair.Key] = pair.Value;

        var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);

        var runtimeConfig = AppConfiguration.Instance;
        runtimeConfig.ApplyUserSettings(studentName, cefrLevel, provider, endpoint, model, apiKey);

        if (!string.IsNullOrWhiteSpace(runtimeConfig.LmStudio.BaseUrl))
            _llmService.Configure(
                runtimeConfig.LmStudio.BaseUrl,
                runtimeConfig.LmStudio.ModelName,
                runtimeConfig.LmStudio.ApiKey);

        return new { ok = true };
    }

    public object CheckFirstRun()
    {
        if (!File.Exists(SettingsPath)) return new { isFirstRun = true };

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            var completed = root.TryGetProperty("onboardingDone", out var value) &&
                (value.ValueKind == JsonValueKind.True ||
                 value.ValueKind == JsonValueKind.String &&
                 bool.TryParse(value.GetString(), out var parsed) && parsed);
            return new { isFirstRun = !completed };
        }
        catch (JsonException)
        {
            return new { isFirstRun = true };
        }
        catch (IOException)
        {
            return new { isFirstRun = true };
        }
    }

    public object TestConnection(TestConnectionRequest req)
    {
        var result = Task.Run(() => ProbeConnectionAsync(req)).GetAwaiter().GetResult();
        return new { ok = result.Ok, message = result.Message, models = result.Models };
    }

    public object GetTranslationStatus()
    {
        var cfg = AppConfiguration.Instance;
        var provider = cfg.LmStudio.Provider;

        if (provider.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            var model = _builtInAi.GetAllStatuses()
                .FirstOrDefault(item => item.Id.Equals(cfg.LmStudio.ModelName, StringComparison.OrdinalIgnoreCase));
            var available = model?.Status == "available";
            return new
            {
                ok = available,
                isServerRunning = true,
                isModelLoaded = available,
                provider,
                modelName = cfg.LmStudio.ModelName,
                availableModels = Array.Empty<string>(),
                error = available ? null : "El modelo seleccionado aún no está descargado.",
            };
        }

        var result = Task.Run(() => ProbeConnectionAsync(new TestConnectionRequest(
            provider,
            cfg.LmStudio.BaseUrl,
            cfg.LmStudio.ApiKey,
            cfg.LmStudio.ModelName))).GetAwaiter().GetResult();

        return new
        {
            ok = result.Ok,
            isServerRunning = result.Ok,
            isModelLoaded = result.Ok,
            provider,
            modelName = cfg.LmStudio.ModelName,
            modelsCount = result.Models.Length,
            availableModels = result.Models,
            error = result.Ok ? null : result.Message,
        };
    }

    private static async Task<ConnectionProbeResult> ProbeConnectionAsync(TestConnectionRequest req)
    {
        var provider = string.IsNullOrWhiteSpace(req.Provider)
            ? "lmstudio"
            : req.Provider.Trim().ToLowerInvariant();

        if (!SupportedProviders.Contains(provider) || provider == "builtin")
            return new(false, "Selecciona un proveedor externo válido.", []);

        Uri endpoint;
        try
        {
            endpoint = ValidateEndpoint(req.Endpoint);
        }
        catch (ArgumentException exception)
        {
            return new(false, exception.Message, []);
        }

        if (provider == "openai" && string.IsNullOrWhiteSpace(req.ApiKey))
            return new(false, "La API key es obligatoria para OpenAI.", []);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var discoveryUri = BuildDiscoveryUri(endpoint, provider);
        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey.Trim());

        try
        {
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var statusMessage = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "La API key no es válida.",
                    System.Net.HttpStatusCode.Forbidden => "La cuenta no tiene permiso para consultar modelos.",
                    System.Net.HttpStatusCode.NotFound => "El endpoint no expone una API de modelos compatible.",
                    _ => $"El servidor respondió con HTTP {(int)response.StatusCode}.",
                };
                return new(false, statusMessage, []);
            }

            var models = ParseModels(body, provider);
            if (!string.IsNullOrWhiteSpace(req.Model) &&
                models.Length > 0 &&
                !models.Contains(req.Model.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return new(false, $"El modelo «{req.Model.Trim()}» no está disponible en el servidor.", models);
            }

            var message = models.Length > 0
                ? $"Conexión exitosa. {models.Length} modelo(s) disponible(s)."
                : "Conexión exitosa.";
            return new(true, message, models);
        }
        catch (TaskCanceledException)
        {
            return new(false, "El servidor no respondió en 12 segundos.", []);
        }
        catch (HttpRequestException exception)
        {
            return new(false, $"No se pudo conectar con el servidor: {exception.Message}", []);
        }
        catch (JsonException)
        {
            return new(false, "El servidor respondió con un formato de modelos no válido.", []);
        }
    }

    private static string[] ParseModels(string json, string provider)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<string>();

        if (provider == "ollama" &&
            document.RootElement.TryGetProperty("models", out var ollamaModels) &&
            ollamaModels.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ollamaModels.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                    models.Add(value);
                else if (item.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } fallback)
                    models.Add(fallback);
            }
        }
        else if (document.RootElement.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                    models.Add(value);
        }

        return models.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    }

    private static Uri BuildDiscoveryUri(Uri endpoint, string provider)
    {
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        if (provider == "ollama")
            return new Uri(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? root[..^3] + "/api/tags"
                : root + "/api/tags");

        return new Uri(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? root + "/models"
            : root + "/v1/models");
    }

    private static Uri ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Ingresa una URL HTTP o HTTPS válida.");
        }

        return uri;
    }

    private static bool IsValidCefrLevel(string value) =>
        value.Trim().ToUpperInvariant() is "A1" or "A2" or "B1" or "B2" or "C1" or "C2";

    private static Dictionary<string, object> ReadExistingSettings()
    {
        if (!File.Exists(SettingsPath)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetFieldString(Dictionary<string, object> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null) return null;
        if (value is string text) return text;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.String) return element.GetString();
        return value.ToString();
    }

    private sealed record ConnectionProbeResult(bool Ok, string Message, string[] Models);
}

public record SaveSettingsRequest(Dictionary<string, object> Fields);
public record TestConnectionRequest(string Provider, string Endpoint, string? ApiKey, string? Model);