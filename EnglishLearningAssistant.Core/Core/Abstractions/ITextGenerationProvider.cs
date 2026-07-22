namespace EnglishLearningAssistant.Core.Abstractions;

/// <summary>Proveedor capaz de generar respuestas del tutor con streaming.</summary>
public interface ITextGenerationProvider
{
    string Name { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onPartialUpdate = null,
        CancellationToken cancellationToken = default);
}
