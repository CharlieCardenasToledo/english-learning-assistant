using System.Collections.Concurrent;
using System.Threading.Channels;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using WindowsLiveCaptionsReader.Services;

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
    private readonly QuestionDetectionService? _questionDetection;
    private readonly LmStudioService? _lmStudio;
    private readonly ITextGenerationProvider? _textGenerationProvider;

    // ─── Question pipeline ─────────────────────────────────────────────────────
    // Cola FIFO sin descarte: cada pregunta detectada espera su turno y se responde.

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

    // Rastreadores de oraciones para procesamiento sobre el flujo parcial (Windows Live Captions)
    private readonly HashSet<string> _processedSentences = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRawText = string.Empty;
    private int _sequenceId;

    // ─── Conversation context (últimas 8 frases confirmadas) ─────────────────
    private readonly Queue<string> _conversationContext = new();
    private const int MaxContextSentences = 8;

    // ─── Events ───────────────────────────────────────────────────────────────

    /// <summary>Nuevo segmento procesado (con traducción si disponible).</summary>
    public event EventHandler<SegmentReadyEvent>? SegmentReady;

    /// <summary>Cambio en el estado de salud de un proveedor externo.</summary>
    public event EventHandler<ProviderHealth>? ProviderHealthChanged;

    /// <summary>Actualización del texto de transcripción en vivo (parcial o confirmado).</summary>
    public event EventHandler<string>? TranscriptionTextUpdated;

    /// <summary>Estado general del orquestador para mostrar en status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Pregunta detectada con texto, nivel (1-4) y confianza.</summary>
    public event EventHandler<QuestionDetectedEvent>? QuestionDetected;

    /// <summary>Fragmento de respuesta LLM en streaming.</summary>
    public event EventHandler<string>? AnswerChunkReceived;

    /// <summary>Respuesta LLM completa.</summary>
    public event EventHandler<string>? AnswerCompleted;

    public SessionOrchestrator(
        ITranscriptionProvider transcriptionProvider,
        ITranslationProvider translationProvider,
        ILogger<SessionOrchestrator> logger,
        OrchestratorOptions? options = null,
        QuestionDetectionService? questionDetection = null,
        LmStudioService? lmStudio = null,
        ITextGenerationProvider? textGenerationProvider = null)
    {
        _transcriptionProvider = transcriptionProvider ?? throw new ArgumentNullException(nameof(transcriptionProvider));
        _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new OrchestratorOptions();
        _questionDetection = questionDetection;
        _lmStudio = lmStudio;
        _textGenerationProvider = textGenerationProvider;

        _questionChannel = Channel.CreateUnbounded<OrchestratorQuestion>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
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
            _lastRawText = segment.Text;

            // Enviar el texto parcial actual al frontend para renderizado en cursiva
            SegmentReady?.Invoke(this, new SegmentReadyEvent(segment, null));
            TranscriptionTextUpdated?.Invoke(this, segment.Text);

            // Extraer y confirmar oraciones completas del bloque de texto acumulativo
            ProcessSentencesFromRawText(segment.Text);

            RestartSettleTimer();
            return;
        }

        // Los proveedores pueden entregar segmentos finales por palabras o fragmentos.
        // Acumularlos conserva la oración completa y evita traducir cada palabra por separado.
        if (!string.IsNullOrWhiteSpace(segment.Text))
        {
            var cleanText = segment.Text.Trim();
            if (!_processedSentences.Contains(cleanText))
            {
                _fragmentBuffer.Add(cleanText);
                if (EndsSentence(cleanText))
                    FlushFragmentBuffer();
            }
        }

        RestartSettleTimer();
        TranscriptionTextUpdated?.Invoke(this, segment.Text);
    }

    private void ProcessSentencesFromRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;

        // Limpiar saltos de línea y normalizar espacios
        var normalized = rawText.Replace('\r', ' ').Replace('\n', ' ');

        // Buscar oraciones terminadas en . ? o !
        int start = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '.' || c == '?' || c == '!')
            {
                string sentence = normalized[start..(i + 1)].Trim();
                start = i + 1;

                // Solo procesar si tiene una longitud razonable
                if (sentence.Length > 5)
                {
                    if (_processedSentences.Add(sentence))
                    {
                        _logger.LogInformation("[Orchestrator] Oración confirmada: {Sentence}", sentence);

                        var confirmed = new TranscriptSegment
                        {
                            SequenceId = Interlocked.Increment(ref _sequenceId),
                            Text       = sentence,
                            IsPartial  = false,
                            Source     = _transcriptionProvider.Name,
                            StartTime  = TimeSpan.Zero,
                        };

                        // Publicar oracion confirmada en el panel de transcripcion
                        SegmentReady?.Invoke(this, new SegmentReadyEvent(confirmed, null));

                        // Disparar traducción y detección de preguntas en paralelo
                        _ = ProcessSentenceForQuestionsAsync(sentence, "");
                        _ = AutoTranslateAsync(sentence);
                    }
                }
            }
        }
    }

    private void OnSettleTimerFired(object? state)
    {
        if (_fragmentBuffer.Count > 0)
        {
            FlushFragmentBuffer();
            return;
        }

        // Silencio detectado: forzar procesamiento de cualquier texto pendiente en la cola de Live Captions
        if (string.IsNullOrWhiteSpace(_lastRawText)) return;

        var normalized = _lastRawText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0) return;

        // Encontrar la última parte después del último signo de puntuación
        int lastPunct = Math.Max(normalized.LastIndexOf('.'),
                         Math.Max(normalized.LastIndexOf('?'), normalized.LastIndexOf('!')));

        string remaining = lastPunct >= 0 && lastPunct < normalized.Length - 1
            ? normalized[(lastPunct + 1)..].Trim()
            : normalized;

        if (remaining.Length > 3 && !_processedSentences.Contains(remaining))
        {
            // Añadir punto final si el usuario dejó de hablar y no usó puntuación
            if (!EndsSentence(remaining))
                remaining += ".";

            if (_processedSentences.Add(remaining))
            {
                _logger.LogInformation("[Orchestrator] Forzada oración final por silencio: {Sentence}", remaining);

                var confirmed = new TranscriptSegment
                {
                    SequenceId = Interlocked.Increment(ref _sequenceId),
                    Text       = remaining,
                    IsPartial  = false,
                    Source     = _transcriptionProvider.Name,
                    StartTime  = TimeSpan.Zero,
                };

                SegmentReady?.Invoke(this, new SegmentReadyEvent(confirmed, null));
                _ = ProcessSentenceForQuestionsAsync(remaining, "");
                _ = AutoTranslateAsync(remaining);
            }
        }
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

        if (string.IsNullOrEmpty(sentence) || !_processedSentences.Add(sentence)) return;

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

    private void AddToConversationContext(string sentence)
    {
        _conversationContext.Enqueue(sentence);
        while (_conversationContext.Count > MaxContextSentences)
            _conversationContext.Dequeue();
    }

    private async Task ProcessSentenceForQuestionsAsync(string sentence, string previousSentence)
    {
        if (_questionDetection is null) return;
        if (string.IsNullOrWhiteSpace(sentence)) return;

        AddToConversationContext(sentence);

        try
        {
            var result = await _questionDetection.AnalyzeWithConfidenceAsync(
                sentence, _options.StudentName, _shutdownCts.Token);

            if (!result.IsQuestion) return;

            // Calcular nivel a partir del prefijo DetectedVia
            int level = result.DetectedVia switch
            {
                var v when v.StartsWith("L4") => 4,
                var v when v.StartsWith("L3") => 3,
                var v when v.StartsWith("L2") => 2,
                _ => 1
            };

            _logger.LogInformation("Pregunta {Level} detectada ({Via}, {Conf:P0}): {Text}",
                level, result.DetectedVia, result.Confidence, sentence[..Math.Min(60, sentence.Length)]);

            QuestionDetected?.Invoke(this, new QuestionDetectedEvent(sentence, level, result.Confidence));

            // Encolar sin cancelar respuesta en vuelo
            await _questionChannel.Writer.WriteAsync(
                new OrchestratorQuestion(sentence, Level: level, Confidence: result.Confidence),
                _shutdownCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en detección de preguntas");
        }
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
    /// Genera respuesta estructurada a una pregunta detectada usando LM Studio con streaming.
    /// </summary>
    private async Task GenerateQuestionResponseAsync(OrchestratorQuestion job, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(_options.QuestionGenerationTimeout);

        if (_textGenerationProvider is null)
        {
            _logger.LogWarning("Ningún proveedor de generación disponible — omitiendo respuesta a pregunta");
            AnswerCompleted?.Invoke(this, string.Empty);
            return;
        }

        _logger.LogInformation("Generando respuesta para: {Question}", job.Question[..Math.Min(50, job.Question.Length)]);

        string context = string.Join("\n", _conversationContext);
        string level = _options.CefrLevel;
        string student = _options.StudentName;

        string systemPrompt =
            $"You are a helpful English tutor assisting a {level}-level student named {student}. " +
            $"The student is listening to a conversation in English. " +
            $"Answer the following question clearly and naturally, adapting your language to the {level} level. " +
            $"Keep the answer concise (2-4 sentences). Respond in English.";

        string userPrompt = context.Length > 0
            ? $"Conversation context:\n{context}\n\nQuestion: {job.Question}"
            : $"Question: {job.Question}";

        string lastSent = string.Empty;
        string fullAnswer = string.Empty;

        try
        {
            fullAnswer = await _textGenerationProvider.GenerateAsync(
                systemPrompt,
                userPrompt,
                onPartialUpdate: accumulated =>
                {
                    // StreamChatAsync entrega el texto acumulado; emitir solo el sufijo nuevo
                    if (accumulated.Length > lastSent.Length)
                    {
                        string chunk = accumulated[lastSent.Length..];
                        lastSent = accumulated;
                        AnswerChunkReceived?.Invoke(this, chunk);
                    }
                },
                cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout o cancelación generando respuesta");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en StreamChatAsync");
        }
        finally
        {
            AnswerCompleted?.Invoke(this, fullAnswer);
        }
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
public sealed record OrchestratorQuestion(string Question, bool IsManual = false, int Level = 1, float Confidence = 0.9f);

/// <summary>Datos del evento QuestionDetected.</summary>
public sealed record QuestionDetectedEvent(string Text, int Level, float Confidence);