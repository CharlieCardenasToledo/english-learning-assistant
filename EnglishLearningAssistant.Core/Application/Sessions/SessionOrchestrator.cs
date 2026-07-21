using System.Collections.Concurrent;
using System.Threading.Channels;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;

namespace EnglishLearningAssistant.Application.Sessions;

/// <summary>
/// Estado de salud de un proveedor externo.
/// </summary>
public record ProviderHealth(string Name, bool IsOnline, string Message);

/// <summary>
/// Evento emitido cuando llega un nuevo segmento (parcial, confirmado o solo traducción).
/// IsTranslationOnly=true → el segmento ya fue publicado; solo enviar la traducción al frontend.
/// </summary>
public record SegmentReadyEvent(TranscriptSegment Segment, TranslationResult? Translation, bool IsTranslationOnly = false);

/// <summary>
/// Opciones de configuración del orquestador.
/// </summary>
public sealed class OrchestratorOptions
{
    /// <summary>Nivel CEFR del estudiante (A1–C2).</summary>
    public string CefrLevel { get; init; } = "B1";

    /// <summary>Nombre del estudiante para personalizar prompts.</summary>
    public string StudentName { get; init; } = "Estudiante";

    /// <summary>Número máximo de preguntas en cola antes de descartar las más antiguas.</summary>
    public int QuestionQueueCapacity { get; init; } = 5;

    /// <summary>Palabras mínimas en un segmento para disparar auto-traducción.</summary>
    public int MinWordsForAutoTranslate { get; init; } = 5;

    /// <summary>Timeout para generar respuesta a una pregunta.</summary>
    public TimeSpan QuestionGenerationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout para auto-traducción.</summary>
    public TimeSpan AutoTranslateTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Orquestador de la sesión educativa.
/// Centraliza la lógica de negocio que estaba dispersa en MainWindow:
///   - Coordinación transcripción → traducción → detección de preguntas
///   - Pipeline de fragmentos con settle timer
///   - Cola FIFO de preguntas con generación serializada
///   - Monitoreo de salud de servicios externos (LM Studio)
///   - Persistencia de segmentos y preguntas
///
/// Inspirado en el patrón de Meetily: pipeline de audio en canal → worker → eventos UI.
/// (T0.2)
/// </summary>
public sealed class SessionOrchestrator : IAsyncDisposable
{
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ITranslationProvider _translationProvider;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly OrchestratorOptions _options;

    // ─── Question pipeline ─────────────────────────────────────────────────────
    // FIFO bounded channel: preguntas nuevas no cancelan las en-vuelo.
    // Si hay 5 en cola, se descarta la más antigua (DropOldest).
    private readonly Channel<OrchestratorQuestion> _questionChannel;
    private CancellationTokenSource _shutdownCts = new();
    private Task? _questionWorkerTask;

    // ─── State ────────────────────────────────────────────────────────────────
    private volatile bool _isGeneratingAnswer;
    private readonly List<string> _pausedTranslationBuffer = new();
    private readonly object _pauseLock = new();
    private string _previousSegmentText = string.Empty;
    private readonly List<string> _fragmentBuffer = new();
    private Timer? _settleTimer;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.2);

    // ─── Events ───────────────────────────────────────────────────────────────

    /// <summary>Nuevo segmento procesado (con traducción si disponible).</summary>
    public event EventHandler<SegmentReadyEvent>? SegmentReady;

    /// <summary>Cambio en el estado de salud de un proveedor externo.</summary>
    public event EventHandler<ProviderHealth>? ProviderHealthChanged;

    /// <summary>Actualización del texto de transcripción en vivo (parcial o confirmado).</summary>
    public event EventHandler<string>? TranscriptionTextUpdated;

    /// <summary>Estado general del orquestador para mostrar en status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    public SessionOrchestrator(
        ITranscriptionProvider transcriptionProvider,
        ITranslationProvider translationProvider,
        ILogger<SessionOrchestrator> logger,
        OrchestratorOptions? options = null)
    {
        _transcriptionProvider = transcriptionProvider ?? throw new ArgumentNullException(nameof(transcriptionProvider));
        _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new OrchestratorOptions();

        _questionChannel = Channel.CreateBounded<OrchestratorQuestion>(
            new BoundedChannelOptions(_options.QuestionQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // Settle timer: se reinicia en cada fragmento; cuando dispara significa que
        // el hablante hizo pausa → flush del buffer de fragmentos
        _settleTimer = new Timer(OnSettleTimerFired, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Inicializa el proveedor y arranca el worker de preguntas.
    /// </summary>
    public async Task StartAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Iniciando sesión. Proveedor: {Provider}, CEFR: {Level}",
            _transcriptionProvider.Name, _options.CefrLevel);

        await _transcriptionProvider.InitializeAsync(cancellationToken);

        // Worker de preguntas — corre de por vida de la sesión
        _questionWorkerTask = Task.Run(
            () => RunQuestionWorkerAsync(_shutdownCts.Token),
            _shutdownCts.Token);

        // Consumidor del stream de transcripción
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var segment in _transcriptionProvider.StartAsync(request, _shutdownCts.Token))
                {
                    OnSegmentReceived(segment);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el stream de transcripción");
                StatusChanged?.Invoke(this, $"Error: {ex.Message}");
            }
        }, _shutdownCts.Token);

        StatusChanged?.Invoke(this, "En vivo");
    }

    /// <summary>Detiene la sesión de forma limpia.</summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Deteniendo sesión");
        _shutdownCts.Cancel();
        _questionChannel.Writer.TryComplete();

        await _transcriptionProvider.StopAsync();

        if (_questionWorkerTask is not null)
        {
            try { await _questionWorkerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* ignora timeout al cerrar */ }
        }

        StatusChanged?.Invoke(this, "Detenido");
    }

    // ─── Segmentos entrantes ──────────────────────────────────────────────────

    private void OnSegmentReceived(TranscriptSegment segment)
    {
        if (segment.IsPartial)
        {
            // Parciales: publicar al frontend y reiniciar timer
            SegmentReady?.Invoke(this, new SegmentReadyEvent(segment, null));
            TranscriptionTextUpdated?.Invoke(this, segment.Text);
            RestartSettleTimer();
            return;
        }

        // Segmento confirmado: agregar al buffer de fragmentos
        _fragmentBuffer.Add(segment.Text);

        // Si termina oración → flush inmediato (fast path)
        if (EndsSentence(segment.Text))
            FlushFragmentBuffer();

        // Reiniciar settle timer en cada fragmento
        RestartSettleTimer();
        TranscriptionTextUpdated?.Invoke(this, segment.Text);
    }

    private void OnSettleTimerFired(object? state)
    {
        // Silencio detectado → flush del buffer de fragmentos
        FlushFragmentBuffer();
    }

    private void RestartSettleTimer()
    {
        _settleTimer?.Change(SettleDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Junta todos los fragmentos del buffer en una oración completa
    /// y la envía a traducción y detección de preguntas.
    /// </summary>
    private void FlushFragmentBuffer()
    {
        if (_fragmentBuffer.Count == 0) return;

        string sentence = string.Join(" ", _fragmentBuffer).Trim();
        _fragmentBuffer.Clear();

        if (string.IsNullOrEmpty(sentence)) return;

        string previous = _previousSegmentText;
        _previousSegmentText = sentence;

        // Publicar transcripción confirmada inmediatamente (sin esperar traducción)
        var confirmed = new TranscriptSegment
        {
            Text      = sentence,
            IsPartial = false,
            Source    = _transcriptionProvider.Name,
            StartTime = TimeSpan.Zero,
        };
        SegmentReady?.Invoke(this, new SegmentReadyEvent(confirmed, null));

        // Fire and forget — traducción y detección de preguntas en paralelo
        _ = ProcessSentenceForQuestionsAsync(sentence, previous);
        _ = AutoTranslateAsync(sentence);
    }

    private static bool EndsSentence(string s)
    {
        s = s.TrimEnd();
        return s.Length > 0 && (s[^1] == '.' || s[^1] == '?' || s[^1] == '!');
    }

    // ─── Auto-traducción ──────────────────────────────────────────────────────

    private CancellationTokenSource? _autoTranslateCts;

    private async Task AutoTranslateAsync(string sentence)
    {
        int wordCount = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < _options.MinWordsForAutoTranslate) return;

        // Mientras se genera una respuesta a pregunta, buffear las traducciones
        if (_isGeneratingAnswer)
        {
            lock (_pauseLock) _pausedTranslationBuffer.Add(sentence);
            StatusChanged?.Invoke(this, "⏸ pausada (respondiendo pregunta)");
            return;
        }

        _autoTranslateCts?.Cancel();
        _autoTranslateCts = new CancellationTokenSource(_options.AutoTranslateTimeout);

        try
        {
            bool available = await _translationProvider.IsAvailableAsync(_autoTranslateCts.Token);
            if (!available)
            {
                _logger.LogWarning("Proveedor de traducción no disponible para: {Sentence}", sentence[..Math.Min(30, sentence.Length)]);
                return;
            }

            StatusChanged?.Invoke(this, "traduciendo...");
            var result = await _translationProvider.TranslateAsync(
                sentence, "en", "es", _autoTranslateCts.Token);

            if (!string.IsNullOrWhiteSpace(result.TranslatedText))
            {
                var segment = new TranscriptSegment
                {
                    Text      = sentence,
                    Source    = _transcriptionProvider.Name,
                    StartTime = TimeSpan.Zero,
                    IsPartial = false,
                };

                // IsTranslationOnly=true: la transcripción ya fue publicada en FlushFragmentBuffer
                SegmentReady?.Invoke(this, new SegmentReadyEvent(segment, result, IsTranslationOnly: true));
                StatusChanged?.Invoke(this, DateTimeOffset.Now.ToString("HH:mm"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en auto-traducción");
        }
    }

    private void ResumePausedTranslations()
    {
        string pending;
        lock (_pauseLock)
        {
            if (_pausedTranslationBuffer.Count == 0) return;
            pending = string.Join(" ", _pausedTranslationBuffer);
            _pausedTranslationBuffer.Clear();
        }
        _ = AutoTranslateAsync(pending);
    }

    // ─── Detección de preguntas ───────────────────────────────────────────────

    /// <summary>
    /// Envía manualmente una pregunta a la cola de respuestas.
    /// Útil para el cuadro de input manual del usuario.
    /// </summary>
    public async Task SubmitManualQuestionAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;

        _logger.LogInformation("Pregunta manual enviada: {Question}", question);
        _isGeneratingAnswer = true;
        await _questionChannel.Writer.WriteAsync(new OrchestratorQuestion(question, IsManual: true));
    }

    // Hook para conectar con el servicio de detección existente (QuestionDetectionService)
    // Se completa en T7.1 cuando se refactoriza ese servicio.
    private async Task ProcessSentenceForQuestionsAsync(string sentence, string previousSentence)
    {
        // TODO T7.1: Mover lógica L1-L4 de MainWindow aquí
        // Por ahora se mantiene en MainWindow y se conectará en T7.1
        await Task.CompletedTask;
        _ = sentence;
        _ = previousSentence;
    }

    // ─── Question worker ──────────────────────────────────────────────────────

    /// <summary>
    /// Worker FIFO serializado: responde preguntas de una en una.
    /// Las preguntas nuevas NO cancelan las en vuelo — esperan en la cola.
    /// (Mismo patrón que Meetily: channel consumer con un único consumidor)
    /// </summary>
    private async Task RunQuestionWorkerAsync(CancellationToken shutdownToken)
    {
        _logger.LogDebug("Question worker iniciado");

        try
        {
            await foreach (var job in _questionChannel.Reader.ReadAllAsync(shutdownToken))
            {
                _isGeneratingAnswer = true;

                try
                {
                    await GenerateQuestionResponseAsync(job, shutdownToken);
                }
                catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Generación de respuesta cancelada por timeout");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generando respuesta para: {Question}", job.Question);
                }

                // Cola vacía → liberar LLM para traducción
                if (_questionChannel.Reader.Count == 0)
                {
                    _isGeneratingAnswer = false;
                    ResumePausedTranslations();
                }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogDebug("Question worker detenido");
    }

    /// <summary>
    /// Genera respuesta estructurada a una pregunta detectada.
    /// TODO T7.3: Conectar con prompt builder y streaming real de LM Studio.
    /// </summary>
    private async Task GenerateQuestionResponseAsync(OrchestratorQuestion job, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(_options.QuestionGenerationTimeout);

        // TODO T7.3: Mover el bloque StreamChatAsync de MainWindow.GenerateResponseAsync aquí
        _logger.LogInformation("Generando respuesta para: {Question}", job.Question[..Math.Min(50, job.Question.Length)]);

        await Task.Delay(50, timeoutCts.Token); // placeholder — se reemplaza en T7.3
    }

    // ─── Health monitoring ────────────────────────────────────────────────────

    /// <summary>Verifica disponibilidad de los proveedores y emite eventos de estado.</summary>
    public async Task CheckProvidersHealthAsync(CancellationToken cancellationToken = default)
    {
        bool translationOnline = await _translationProvider.IsAvailableAsync(cancellationToken);
        ProviderHealthChanged?.Invoke(this, new ProviderHealth(
            _translationProvider.Name, translationOnline,
            translationOnline ? "Online" : "Offline — abre LM Studio y carga un modelo"));
    }

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _settleTimer?.Dispose();
        _settleTimer = null;
        _autoTranslateCts?.Dispose();
        _shutdownCts.Dispose();
        await _transcriptionProvider.DisposeAsync();
    }
}

/// <summary>Representa una pregunta en la cola interna del orquestador.</summary>
public sealed record OrchestratorQuestion(string Question, bool IsManual = false);
