using System.IO;
using System.Text;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.TauriPlugIn.Services;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace EnglishLearningAssistant.TauriPlugIn.Providers;

/// <summary>
/// Traducción local usando LLamaSharp (llama.cpp en proceso).
/// Carga el modelo GGUF de forma lazy en el primer uso.
/// </summary>
public sealed class LocalLlamaTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly BuiltInAiService _builtIn;
    private readonly ILogger<LocalLlamaTranslationProvider> _logger;
    private readonly AppConfiguration _config;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private LLamaWeights?   _weights;
    private ModelParams?    _params;
    private string?         _loadedModelPath;

    public string Name => "Built-in (LLamaSharp)";

    public LocalLlamaTranslationProvider(
        BuiltInAiService builtIn,
        ILogger<LocalLlamaTranslationProvider> logger,
        AppConfiguration config)
    {
        _builtIn = builtIn;
        _logger  = logger;
        _config  = config;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var modelId  = _config.LmStudio.ModelName; // re-use field for built-in model id
        try
        {
            var path = _builtIn.GetModelPath(modelId);
            return Task.FromResult(File.Exists(path));
        }
        catch { return Task.FromResult(false); }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslationResult.Empty(text);

        try
        {
            var executor = await EnsureModelLoadedAsync(cancellationToken);
            if (executor is null) return TranslationResult.Empty(text);

            var prompt = BuildPrompt(text, sourceLanguage, targetLanguage);

            var sb = new StringBuilder();
            var inferParams = new InferenceParams
            {
                MaxTokens   = 512,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>", "\n\n\n"],
            };

            await foreach (var token in executor.InferAsync(prompt, inferParams, cancellationToken))
            {
                if (inferParams.AntiPrompts.Any(a => token.Contains(a, StringComparison.Ordinal)))
                    break;
                sb.Append(token);
            }

            var translated = sb.ToString().Trim();
            _logger.LogDebug("Translated ({From}→{To}): {Snippet}",
                sourceLanguage, targetLanguage, translated[..Math.Min(60, translated.Length)]);

            return new TranslationResult
            {
                OriginalText   = text,
                TranslatedText = translated,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                ProviderName   = Name,
                IsFromCache    = false,
            };
        }
        catch (OperationCanceledException) { return TranslationResult.Empty(text); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocalLlama translation error");
            return TranslationResult.Empty(text);
        }
    }

    // ── Lazy model loading ────────────────────────────────────────────────────

    private async Task<StatelessExecutor?> EnsureModelLoadedAsync(CancellationToken token)
    {
        var modelId  = _config.LmStudio.ModelName;
        var path     = _builtIn.GetModelPath(modelId);

        if (!File.Exists(path))
        {
            _logger.LogWarning("Built-in model not found at {Path}", path);
            return null;
        }

        if (_loadedModelPath == path && _weights is not null && _params is not null)
            return new StatelessExecutor(_weights, _params);

        await _loadLock.WaitAsync(token);
        try
        {
            if (_loadedModelPath == path && _weights is not null && _params is not null)
                return new StatelessExecutor(_weights, _params);

            _logger.LogInformation("Loading GGUF model from {Path}", path);
            _weights?.Dispose();

            var hw      = _builtIn.GetHardware();
            bool hasGpu = hw.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                          hw.GpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                          hw.GpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);

            _params = new ModelParams(path)
            {
                ContextSize   = 1024,
                GpuLayerCount = hasGpu ? 20 : 0,
            };
            _weights         = LLamaWeights.LoadFromFile(_params);
            _loadedModelPath = path;

            _logger.LogInformation("GGUF model loaded (GPU layers: {N}).", _params.GpuLayerCount);
            return new StatelessExecutor(_weights, _params);
        }
        finally { _loadLock.Release(); }
    }

    // ── Prompt format (ChatML / Qwen2.5-Instruct) ────────────────────────────

    static string BuildPrompt(string text, string from, string to)
    {
        var targetLang = to switch
        {
            "es" => "Spanish",
            "en" => "English",
            "fr" => "French",
            "de" => "German",
            "pt" => "Portuguese",
            "it" => "Italian",
            _    => to,
        };

        return $"""
            <|im_start|>system
            Translate the following {(from == "en" ? "English" : from)} text to {targetLang}. Output ONLY the translation, no explanations.
            <|im_end|>
            <|im_start|>user
            {text}
            <|im_end|>
            <|im_start|>assistant

            """;
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _loadLock.Dispose();
    }
}
