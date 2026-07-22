using EnglishLearningAssistant.Application.Sessions;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using TauriDotNetBridge.Contracts;
using WindowsLiveCaptionsReader.Services;

namespace EnglishLearningAssistant.TauriPlugIn;

/// <summary>
/// Servicio de fondo que gestiona el ciclo de vida del SessionOrchestrator y publica
/// eventos hacia el frontend Next.js vía IEventPublisher (Tauri events nativos).
/// El orquestador NO arranca automáticamente: espera llamadas explícitas de
/// SessionController.Start() / Stop().
/// </summary>
public sealed class CaptionHostedService : IHostedService
{
    private readonly IEventPublisher _publisher;
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ITranslationProvider _translationProvider;
    private readonly ITextGenerationProvider _textGenerationProvider;
    private readonly QuestionDetectionService _questionDetection;
    private readonly LmStudioService _lmStudio;
    private readonly ILogger<CaptionHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SessionService _sessionService;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly SemaphoreSlim _persistenceSemaphore = new(1, 1);
    private readonly object _persistenceLock = new();
    private readonly List<Task> _persistenceTasks = new();

    private SessionOrchestrator? _orchestrator;
    private WindowsLiveCaptionsReader.Models.Session? _currentSession;
    private bool _isRunning;

    public CaptionHostedService(
        IEventPublisher publisher,
        ITranscriptionProvider transcriptionProvider,
        ITranslationProvider translationProvider,
        ITextGenerationProvider textGenerationProvider,
        QuestionDetectionService questionDetection,
        LmStudioService lmStudio,
        ILogger<CaptionHostedService> logger,
        ILoggerFactory loggerFactory,
        SessionService sessionService)
    {
        _publisher              = publisher;
        _transcriptionProvider  = transcriptionProvider;
        _translationProvider    = translationProvider;
        _textGenerationProvider = textGenerationProvider;
        _questionDetection      = questionDetection;
        _lmStudio               = lmStudio;
        _logger                 = logger;
        _loggerFactory          = loggerFactory;
        _sessionService         = sessionService;
    }

    // IHostedService.StartAsync — solo inicialización, NO arranca captions
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CaptionHostedService inicializado y listo.");
        _publisher.Publish("status-changed", new { status = "Listo" });
        return Task.CompletedTask;
    }

    // IHostedService.StopAsync — se llama al cerrar Tauri
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopSessionAsync();
    }

    // ─── Control de sesión ────────────────────────────────────────────────────

    public int? CurrentSessionId => _currentSession?.Id;

    public async Task StartSessionAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_isRunning)
            {
                _logger.LogWarning("StartSessionAsync llamado mientras ya está activo.");
                return;
            }

            var config = AppConfiguration.Instance;
            await _sessionService.InitializeAsync();
            _currentSession = await _sessionService.CreateSessionAsync($"Sesión en vivo — {DateTime.Now:yyyy-MM-dd HH:mm}");

            _orchestrator = new SessionOrchestrator(
                _transcriptionProvider,
                _translationProvider,
                _loggerFactory.CreateLogger<SessionOrchestrator>(),
                new OrchestratorOptions
                {
                    CefrLevel   = config.CefrLevel,
                    StudentName = config.StudentName,
                    MinWordsForAutoTranslate = 5,
                },
                questionDetection: _questionDetection,
                lmStudio: _lmStudio,
                textGenerationProvider: _textGenerationProvider);

            // Suscribir eventos
            _orchestrator.SegmentReady        += OnSegmentReady;
            _orchestrator.StatusChanged       += (_, s) => _publisher.Publish("status-changed", new { status = s });
            _orchestrator.QuestionDetected    += OnQuestionDetected;
            _orchestrator.AnswerChunkReceived += OnAnswerChunkReceived;
            _orchestrator.AnswerCompleted     += OnAnswerCompleted;

            var request = new TranscriptionRequest
            {
                CefrLevel      = config.CefrLevel,
                SourceLanguage = "en",
                RecordAudio    = config.Transcription.RecordAudio,
            };

            try
            {
                await _orchestrator.StartAsync(request, CancellationToken.None);
                _isRunning = true;
                _logger.LogInformation("Sesión de captions iniciada.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al iniciar SessionOrchestrator.");
                await CompleteCurrentSessionAsync($"Error al iniciar: {ex.Message}");
                _publisher.Publish("status-changed", new { status = $"Error: {ex.Message}" });
                _orchestrator = null;
                throw;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StopSessionAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!_isRunning || _orchestrator is null) return;

            try
            {
                await _orchestrator.DisposeAsync();
                Task[] pending;
                lock (_persistenceLock) pending = _persistenceTasks.ToArray();
                await Task.WhenAll(pending);
                await CompleteCurrentSessionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al detener SessionOrchestrator.");
            }
            finally
            {
                _orchestrator = null;
                _isRunning = false;
                _logger.LogInformation("Sesión de captions detenida.");
                _publisher.Publish("status-changed", new { status = "Detenido" });
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ─── Handlers de eventos del orquestador ─────────────────────────────────

    private void OnSegmentReady(object? sender, SegmentReadyEvent e)
    {
        var seg = e.Segment;

        if (!e.IsTranslationOnly)
        {
            if (seg.IsPartial)
            {
                _publisher.Publish("transcription-partial", new
                {
                    text      = seg.Text,
                    timestamp = seg.CreatedAt.ToString("o"),
                });
                return;
            }

            QueuePersistence(() => PersistConfirmedSegmentAsync(seg));

            _publisher.Publish("transcription-line", new
            {
                text       = seg.Text,
                timestamp  = seg.CreatedAt.ToString("o"),
                sequenceId = seg.SequenceId,
            });
        }

        if (e.Translation is not null && !string.IsNullOrEmpty(e.Translation.TranslatedText))
        {
            QueuePersistence(() => PersistTranslationAsync(e.Translation));
            _publisher.Publish("translation-ready", new
            {
                original   = e.Translation.OriginalText,
                translated = e.Translation.TranslatedText,
                provider   = e.Translation.ProviderName,
                fromCache  = e.Translation.IsFromCache,
            });
        }
    }

    private void OnQuestionDetected(object? sender, QuestionDetectedEvent e)
    {
        QueuePersistence(() => PersistQuestionAsync(e));
        _publisher.Publish("question-detected", new
        {
            text       = e.Text,
            level      = e.Level,
            confidence = e.Confidence,
        });
    }

    private void OnAnswerChunkReceived(object? sender, string chunk)
    {
        _publisher.Publish("answer-chunk", new { chunk });
    }

    private void OnAnswerCompleted(object? sender, string answer)
    {
        QueuePersistence(() => PersistAnswerAsync(answer));
        _publisher.Publish("answer-complete", new { answer });
    }
    private void QueuePersistence(Func<Task> operation)
    {
        var task = PersistSafelyAsync(operation);
        lock (_persistenceLock) _persistenceTasks.Add(task);
    }

    private async Task PersistSafelyAsync(Func<Task> operation)
    {
        await _persistenceSemaphore.WaitAsync();
        try { await operation(); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo guardar información de la sesión en vivo"); }
        finally { _persistenceSemaphore.Release(); }
    }

    private async Task PersistConfirmedSegmentAsync(TranscriptSegment segment)
    {
        var session = _currentSession;
        if (session is null || string.IsNullOrWhiteSpace(segment.Text)) return;

        var entry = new WindowsLiveCaptionsReader.Models.TranscriptionEntry
        {
            SessionId = session.Id,
            OriginalText = segment.Text.Trim(),
            Timestamp = segment.CreatedAt.LocalDateTime,
            Source = WindowsLiveCaptionsReader.Models.EntrySource.LiveCaption,
            ConfidenceScore = segment.Confidence is null ? null : (float)segment.Confidence.Value,
            AudioStartTime = segment.StartTime.TotalSeconds,
            AudioEndTime = segment.EndTime.TotalSeconds,
        };
        session.Entries.Add(entry);
        await _sessionService.SaveEntryAsync(entry);
    }

    private async Task PersistTranslationAsync(TranslationResult translation)
    {
        var session = _currentSession;
        if (session is null) return;
        var entry = session.Entries.LastOrDefault(e =>
            string.Equals(e.OriginalText.Trim(), translation.OriginalText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        entry.TranslatedText = translation.TranslatedText;
        await _sessionService.SaveSessionAsync(session);
    }

    private async Task PersistQuestionAsync(QuestionDetectedEvent detected)
    {
        var session = _currentSession;
        if (session is null) return;
        var entry = session.Entries.LastOrDefault(e =>
            string.Equals(e.OriginalText.Trim(), detected.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        var question = new WindowsLiveCaptionsReader.Models.DetectedQuestion
        {
            SessionId = session.Id,
            EntryId = entry?.Id ?? 0,
            QuestionText = detected.Text,
            DetectedAt = DateTime.Now,
            Type = WindowsLiveCaptionsReader.Models.QuestionType.Direct,
        };
        if (entry is not null) entry.ContainsQuestion = true;
        session.Questions.Add(question);
        await _sessionService.SaveQuestionAsync(question);
        await _sessionService.SaveSessionAsync(session);
    }

    private async Task PersistAnswerAsync(string answer)
    {
        var session = _currentSession;
        if (session is null) return;
        var question = session.Questions.LastOrDefault(q => !q.WasAnswered);
        if (question is null) return;
        question.SuggestedAnswer = answer;
        question.WasAnswered = !string.IsNullOrWhiteSpace(answer);
        var entry = session.Entries.FirstOrDefault(e => e.Id == question.EntryId);
        if (entry is not null) entry.AiResponse = answer;
        await _sessionService.SaveSessionAsync(session);
    }

    private async Task CompleteCurrentSessionAsync(string? summary = null)
    {
        var session = _currentSession;
        if (session is null) return;
        await _persistenceSemaphore.WaitAsync();
        try
        {
            session.EndTime = DateTime.Now;
            session.Status = WindowsLiveCaptionsReader.Models.SessionStatus.Completed;
            if (!string.IsNullOrWhiteSpace(summary)) session.Summary = summary;
            await _sessionService.SaveSessionAsync(session);
            _publisher.Publish("session-updated", new { sessionId = session.Id });
            _sessionService.StopAutoSave();
            _currentSession = null;
        }
        finally { _persistenceSemaphore.Release(); }
    }
}