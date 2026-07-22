using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class VocabularyController(VocabularyService vocabulary)
{
    public object Initialize()
    {
        Task.Run(vocabulary.InitializeAsync).GetAwaiter().GetResult();
        return new { ok = true };
    }

    public object List()
    {
        var items = Task.Run(vocabulary.GetAllVocabularyAsync).GetAwaiter().GetResult();
        return items.Select(item => new
        {
            id = item.Id,
            word = item.Word,
            definition = item.Definition,
            spanishTranslation = item.SpanishTranslation,
            exampleSentence = item.ExampleSentence,
            timesEncountered = item.TimesEncountered,
            firstSeen = item.FirstSeen.ToString("o"),
            lastSeen = item.LastSeen.ToString("o")
        }).ToList();
    }

    public object Add(AddWordRequest req)
    {
        Task.Run(() => vocabulary.AddOrUpdateWordAsync(
            req.Word, req.Definition, req.Translation, req.Context))
            .GetAwaiter().GetResult();
        return new { ok = true };
    }

    public object Delete(DeleteWordRequest req)
    {
        Task.Run(() => vocabulary.DeleteWordAsync(req.Id)).GetAwaiter().GetResult();
        return new { ok = true };
    }

    public object Analyze(AnalyzeRequest req) =>
        Task.Run(() => vocabulary.ExtractPotentialVocabularyAsync(req.Text))
            .GetAwaiter().GetResult();
}

public record AddWordRequest(string Word, string Definition, string Translation, string Context = "");
public record DeleteWordRequest(int Id);
public record AnalyzeRequest(string Text);
