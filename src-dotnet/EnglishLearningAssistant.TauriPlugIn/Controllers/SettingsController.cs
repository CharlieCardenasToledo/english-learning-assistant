using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EnglishLearningAssistant.Core.Models;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class SettingsController
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EnglishLearningAssistant", "settings.json");

    public object Get()
    {
        var cfg = AppConfiguration.Instance;
        return new
        {
            studentName = cfg.StudentName,
            cefrLevel   = cfg.CefrLevel,
            llm = new
            {
                provider = cfg.LmStudio.Provider,
                endpoint = cfg.LmStudio.BaseUrl,
                model    = cfg.LmStudio.ModelName,
                apiKey   = cfg.LmStudio.ApiKey ?? "",
            },
            translation = new
            {
                enableCache             = cfg.Translation.EnableCache,
                fallbackToLibreTranslate = cfg.Translation.FallbackToLibreTranslate,
            },
        };
    }

    public object Save(SaveSettingsRequest req)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);

        var existing = File.Exists(SettingsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath)) ?? []
            : [];

        foreach (var kv in req.Fields)
            existing[kv.Key] = kv.Value;

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(existing,
            new JsonSerializerOptions { WriteIndented = true }));

        return new { ok = true };
    }

    public async Task<object> TestConnection(TestConnectionRequest req)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            if (!string.IsNullOrWhiteSpace(req.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", req.ApiKey);

            var url = req.Endpoint.TrimEnd('/') + "/v1/models";
            var response = await http.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var models  = new List<string>();
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                                models.Add(id.GetString() ?? "");
                        }
                    }
                }
                catch { }

                return new { ok = true, message = "Conexión exitosa", models };
            }

            return new
            {
                ok = false,
                message = $"Error {(int)response.StatusCode}: {response.ReasonPhrase}",
                models = Array.Empty<string>(),
            };
        }
        catch (TaskCanceledException)
        {
            return new { ok = false, message = "Timeout: el servidor no respondió en 10 s", models = Array.Empty<string>() };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message, models = Array.Empty<string>() };
        }
    }

    public object GetHardwareInfo() =>
        WindowsLiveCaptionsReader.Services.HardwareDetector.DetectHardware();

    public object CheckFirstRun() =>
        new { isFirstRun = !File.Exists(SettingsPath) };
}

public record SaveSettingsRequest(Dictionary<string, object> Fields);
public record TestConnectionRequest(string Endpoint, string? ApiKey);
