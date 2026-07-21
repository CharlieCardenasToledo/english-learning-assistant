using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class VocabularyController(VocabularyService vocabulary)
{
    public async Task<object> Initialize()
    {
        await vocabulary.InitializeAsync();
        return new { ok = true };
    }

    public async Task<object> List() =>
        await vocabulary.GetAllVocabularyAsync();

    public async Task<object> Add(AddWordRequest req)
    {
        await vocabulary.AddOrUpdateWordAsync(req.Word, req.Definition, req.Translation, req.Context);
        return new { ok = true };
    }

    public async Task<object> Delete(DeleteWordRequest req)
    {
        await vocabulary.DeleteWordAsync(req.Id);
        return new { ok = true };
    }

    public async Task<object> Analyze(AnalyzeRequest req) =>
        await vocabulary.ExtractPotentialVocabularyAsync(req.Text);
}

public record AddWordRequest(string Word, string Definition, string Translation, string Context = "");
public record DeleteWordRequest(int Id);
public record AnalyzeRequest(string Text);
