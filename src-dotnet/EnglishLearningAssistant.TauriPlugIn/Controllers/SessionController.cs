using EnglishLearningAssistant.Core.Models;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class SessionController(SessionService sessions, CaptionHostedService captionService)
{
    public object Initialize()
    {
        try
        {
            Task.Run(sessions.InitializeAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error initializing session DB", ex);
            return new { ok = false, error = ex.Message };
        }
        return new { ok = true };
    }

    public object Start()
    {
        try
        {
            Task.Run(captionService.StartSessionAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error starting session", ex);
            return new { ok = false, error = ex.Message };
        }
        return new { ok = true, sessionId = captionService.CurrentSessionId };
    }

    public object Stop()
    {
        try
        {
            Task.Run(captionService.StopSessionAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error stopping session", ex);
            return new { ok = false, error = ex.Message };
        }
        return new { ok = true };
    }

    public object List()
    {
        try
        {
            return Task.Run(async () =>
            {
                await sessions.InitializeAsync();
                var list = await sessions.GetAllSessionsAsync();
                return (object)list.Select(s => new
                {
                    id = s.Id,
                    title = s.Title,
                    startTime = s.StartTime.ToString("o"),
                    endTime = s.EndTime?.ToString("o"),
                    transcriptionCount = s.Metadata?.TotalEntries ?? 0,
                    questionCount = s.Metadata?.QuestionsDetected ?? 0
                }).ToList();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error listing sessions", ex);
            return Array.Empty<object>();
        }
    }

    public object Get(GetSessionRequest req) =>
        Task.Run(async () => (object)(await sessions.LoadSessionAsync(req.Id) ??
            throw new KeyNotFoundException($"Session {req.Id} not found")))
            .GetAwaiter().GetResult();

    public object Delete(DeleteSessionRequest req)
    {
        Task.Run(() => sessions.DeleteSessionAsync(req.Id)).GetAwaiter().GetResult();
        return new { ok = true };
    }

    public object Export(ExportSessionRequest req) =>
        new
        {
            markdown = Task.Run(() => sessions.ExportToMarkdownAsync(req.Id))
                .GetAwaiter().GetResult()
        };
}

public record GetSessionRequest(int Id);
public record DeleteSessionRequest(int Id);
public record ExportSessionRequest(int Id);
