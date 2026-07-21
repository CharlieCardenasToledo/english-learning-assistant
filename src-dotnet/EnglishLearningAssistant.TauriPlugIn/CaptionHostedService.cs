using EnglishLearningAssistant.Application.Sessions;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TauriDotNetBridge.Contracts;

namespace EnglishLearningAssistant.TauriPlugIn;

/// <summary>
/// Servicio de fondo que arranca el SessionOrchestrator y publica eventos
/// hacia el frontend Next.js vía IEventPublisher (Tauri events nativos).
/// Reemplaza la lógica de MainWindow.xaml.cs del proyecto WPF original.
/// </summary>
public sealed class CaptionHostedService : IHostedService
{
    private readonly IEventPublisher _publisher;
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ITranslationProvider _translationProvider;
    private readonly ILogger<CaptionHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private SessionOrchestrator? _orchestrator;

    public CaptionHostedService(
        IEventPublisher publisher,
        ITranscriptionProvider transcriptionProvider,
        ITranslationProvider translationProvider,
        ILogger<CaptionHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _publisher              = publisher;
        _transcriptionProvider  = transcriptionProvider;
        _translationProvider    = translationProvider;
        _logger                 = logger;
        _loggerFactory          = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CaptionHostedService starting.");

        var config = EnglishLearningAssistant.Core.Models.AppConfiguration.Instance;

        _orchestrator = new SessionOrchestrator(
            _transcriptionProvider,
            _translationProvider,
            _loggerFactory.CreateLogger<SessionOrchestrator>(),
            new OrchestratorOptions
            {
                CefrLevel   = config.CefrLevel,
                StudentName = config.StudentName,
                MinWordsForAutoTranslate = 5,
            });

        // Conectar eventos del orquestador a eventos Tauri
        _orchestrator.SegmentReady += OnSegmentReady;
        _orchestrator.StatusChanged += (_, status) =>
            _publisher.Publish("status-changed", new { status });

        var request = new TranscriptionRequest
        {
            CefrLevel   = config.CefrLevel,
            SourceLanguage = "en",
            RecordAudio = config.Transcription.RecordAudio,
        };

        try
        {
            await _orchestrator.StartAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting SessionOrchestrator.");
        }
    }

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

            // Segmento confirmado: publicar transcripción inmediatamente
            _publisher.Publish("transcription-line", new
            {
                text       = seg.Text,
                timestamp  = seg.CreatedAt.ToString("o"),
                sequenceId = seg.SequenceId,
            });
        }

        // Publicar traducción si está disponible (puede llegar más tarde que la transcripción)
        if (e.Translation is not null && !string.IsNullOrEmpty(e.Translation.TranslatedText))
        {
            _publisher.Publish("translation-ready", new
            {
                original   = e.Translation.OriginalText,
                translated = e.Translation.TranslatedText,
                provider   = e.Translation.ProviderName,
                fromCache  = e.Translation.IsFromCache,
            });
        }
    }
}
