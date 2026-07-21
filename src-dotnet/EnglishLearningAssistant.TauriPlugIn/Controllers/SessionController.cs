using EnglishLearningAssistant.Core.Models;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn.Controllers;

public class SessionController(SessionService sessions)
{
    public async Task<object> Initialize()
    {
        try
        {
            await sessions.InitializeAsync();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error initializing session DB", ex);
        }
        return new { ok = true };
    }

    public async Task<object> List()
    {
        try
        {
            await sessions.InitializeAsync();
            return await sessions.GetAllSessionsAsync();
        }
        catch (Exception ex)
        {
            EnglishLearningAssistant.Core.AppLogger.Error("Error listing sessions", ex);
            return Array.Empty<object>();
        }
    }

    public async Task<object> Get(GetSessionRequest req) =>
        await sessions.LoadSessionAsync(req.Id) ?? throw new KeyNotFoundException($"Session {req.Id} not found");

    public async Task<object> Delete(DeleteSessionRequest req)
    {
        await sessions.DeleteSessionAsync(req.Id);
        return new { ok = true };
    }

    public async Task<object> Export(ExportSessionRequest req) =>
        new { markdown = await sessions.ExportToMarkdownAsync(req.Id) };
}

public record GetSessionRequest(int Id);
public record DeleteSessionRequest(int Id);
public record ExportSessionRequest(int Id);
