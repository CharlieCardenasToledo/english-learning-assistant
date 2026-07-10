using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsLiveCaptionsReader.Services;
using WindowsLiveCaptionsReader.Models;

namespace WindowsLiveCaptionsReader
{
    public partial class MainWindow : Window
    {
        // ─── Services ─────────────────────────────────────────────────────────
        private CaptionReader _reader;
        private LmStudioService _translator;
        private AudioCaptureService _micService;
        private SessionService _sessionService;
        private QuestionDetectionService _questionService;
        private VocabularyService _vocabularyService;
        private readonly LibreTranslateService _libreTranslate = new();
        private readonly Services.BrowserCaptureService _browserScanner = new();
        private readonly Services.ChromeSessionService  _chromeService  = new();

        // ─── Pipeline ─────────────────────────────────────────────────────────
        private readonly CaptionPipeline _captions = new();

        // Question pipeline: detection is cheap (L1-L3); generation queued via Channel.
        // FIFO queue: every detected question gets answered in order — a new question never
        // cancels the in-flight generation. If 5 pile up, the oldest is dropped.
        private readonly Channel<QuestionJob> _questionChannel =
            Channel.CreateBounded<QuestionJob>(
                new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly CancellationTokenSource _pipelineShutdown = new();
        // Cancelled only by explicit user actions (clear panel / shutdown), never by
        // newly detected questions — those wait in the queue instead.
        private CancellationTokenSource? _currentGenerationAbort;
        // While an answer is being generated, live translation via LM Studio is paused
        // (sentences buffer up and are translated in one batch afterwards) so the LLM
        // is fully dedicated to answering. LibreTranslate route is never paused.
        private volatile bool _isGeneratingAnswer;
        private readonly List<string> _pausedTranslationBuffer = new();
        private readonly object _pauseLock = new();
        // Per-request safety net: don't rely on HttpClient's 120s global timeout
        // Measured: ~3.5s full structured answer on llama-3.2-3b-instruct (no reasoning);
        // 60s leaves headroom for reasoning models (gemma-4-e4b "thinks" 20-80s first).
        private static readonly TimeSpan QuestionGenerationTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan AutoTranslateTimeout     = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan L4ClassificationTimeout  = TimeSpan.FromSeconds(15);
        private string _previousSentence = "";

        // ─── Session ──────────────────────────────────────────────────────────
        private Session? _currentSession;

        // ─── User settings ────────────────────────────────────────────────────
        private string _userName          = "Charlie";
        private string _englishLevel      = "B1";
        private string _lmStudioModelName = "llama-3.2-3b-instruct";
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant");
        private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
        private static readonly string LogPath      = Path.Combine(AppDataDir, "conversation_history.log");

        // ─── UI state ─────────────────────────────────────────────────────────
        private bool _isPaused = false;
        private bool _isMicActive = false;
        private bool _isAssistantOpen = false;
        private bool _isLmStudioOnline = false;

        // ─── Auto-translation ─────────────────────────────────────────────────
        private CancellationTokenSource? _autoTranslateCts;
        private string _autoTranslationBuffer = "";
        private const int MinWordsForAutoTranslate = 5;

        // ─── LibreTranslate ───────────────────────────────────────────────────
        private bool     _libreTranslateAvailable = false;
        private DateTime _libreTranslateLastCheck = DateTime.MinValue;

        // ─── Health monitoring ────────────────────────────────────────────────
        private DispatcherTimer? _lmStudioHealthTimer;

        // ─── History (UI) ─────────────────────────────────────────────────────
        public ObservableCollection<TranslationItem> History { get; set; }

        // ─── Constructor ──────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            History = new ObservableCollection<TranslationItem>();
            _translator = new LmStudioService("llama-3.2-3b-instruct");
            _micService = new AudioCaptureService();

            try
            {
                _reader          = new CaptionReader();
                _sessionService  = new SessionService();
                _questionService = new QuestionDetectionService(_translator);
                _vocabularyService = new VocabularyService(_translator);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Startup initialization failed", ex);
                MessageBox.Show($"Error inicializando servicios: {ex.Message}", "Error de inicio",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (_reader != null)
            {
                _reader.TextChanged   += (s, text) => Reader_TextChanged(s, text);
                _reader.StatusChanged += Reader_StatusChanged;
            }

            _micService.TextCaptured     += (s, text) => Reader_TextChanged(s, text);
            _micService.StatusChanged    += (s, status) =>
                Dispatcher.Invoke(() => { if (_isMicActive) StatusText.Text = $"Mic: {status}"; });
            _micService.AudioLevelChanged += (s, level) =>
                Dispatcher.Invoke(() => { if (MicLevelBar != null) MicLevelBar.Value = level; });

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                Left   = workArea.Left;
                Top    = workArea.Top;
                Width  = workArea.Width;
                Height = workArea.Height;

                ApplyBlurBehind();
                Directory.CreateDirectory(AppDataDir);
                LoadSettings();
                _translator.SetModel(_lmStudioModelName);

                if (_sessionService   != null) await _sessionService.InitializeAsync();
                if (_vocabularyService != null) await _vocabularyService.InitializeAsync();

                await EnsureServicesAreRunning();
                _reader?.Start();

                await CreateNewSessionAsync();
                StartLmStudioHealthCheck();

                // Start the question-generation worker (runs for the lifetime of the window)
                _ = RunQuestionWorkerAsync(_pipelineShutdown.Token);

                AppLogger.Info("Application started");
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainWindow_Loaded failed", ex);
                MessageBox.Show($"Error durante la inicialización: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _pipelineShutdown.Cancel();
            _reader?.Stop();
            _micService.StopListening();
            _sessionService?.Dispose();
            _vocabularyService?.Dispose();
            _libreTranslate.StopServer();
            AppLogger.Info("Application closed");
            base.OnClosed(e);
        }

        // ─── Service startup ──────────────────────────────────────────────────

        private async Task EnsureServicesAreRunning()
        {
            StatusText.Text = "Verificando servicios...";
            bool isRunning = await _translator.IsRunningAsync();

            if (!isRunning)
            {
                StatusText.Text = "LM Studio offline";
                TranslationText.Text = "Abre LM Studio, carga un modelo y vuelve a intentarlo.";

                bool started = _translator.StartServer();
                if (started)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        await Task.Delay(1000);
                        if (await _translator.IsRunningAsync()) { isRunning = true; break; }
                    }
                }
            }

            if (!isRunning)
            {
                StatusText.Text = "LM Studio offline";
                TranslationText.Text = "Abre LM Studio y carga un modelo para continuar.";
                AppLogger.Warn("LM Studio not reachable at startup");
            }
            else
            {
                StatusText.Text = "Listo";
                TranslationText.Text = "";
            }

            _ = Task.Run(async () =>
            {
                bool ltReady = await _libreTranslate.EnsureRunningAsync(
                    onStatusUpdate: msg => Dispatcher.InvokeAsync(() => TranslationStatus.Text = msg));
                _libreTranslateAvailable = ltReady;
                _libreTranslateLastCheck = DateTime.Now;
                if (ltReady)
                    Dispatcher.InvokeAsync(() => TranslationStatus.Text = "LibreTranslate ✓");
            });
        }

        // ─── Caption capture ──────────────────────────────────────────────────

        private void Reader_StatusChanged(object sender, string e) =>
            Dispatcher.Invoke(() => StatusText.Text = e);

        private void Reader_TextChanged(object sender, string text)
        {
            if (_isPaused || string.IsNullOrWhiteSpace(text)) return;
            Dispatcher.Invoke(() => AppendCaption(text, sender is AudioCaptureService));
        }

        private void AppendCaption(string newCaption, bool isMic = false)
        {
            // Snapshot the pending sentence before Feed() commits it
            string pendingSnapshot = _captions.Pending;
            string? committed = _captions.Feed(newCaption);

            if (committed != null)
            {
                _previousSentence = committed;
                _ = ProcessSentenceAsync(committed);
                _ = AutoTranslateSentenceAsync(committed);
            }

            TranscriptionText.Text = _captions.GetDisplayText();
            StatusText.Text = isMic ? "Mic..." : "Live Captions";

            if (TranscriptionScrollViewer.VerticalOffset >= TranscriptionScrollViewer.ScrollableHeight - 40)
                TranscriptionScrollViewer.ScrollToBottom();
        }

        // ─── Question detection pipeline ──────────────────────────────────────

        // Runs on a thread-pool thread (fire-and-forget from AppendCaption).
        // L1-L3 detection is regex-only (<1ms). L4-AI runs in the background
        // without blocking this method or the generation worker.
        private async Task ProcessSentenceAsync(string sentence)
        {
            try
            {
                // L1-L3 fast path — no LLM call
                var result = await _questionService.AnalyzeWithConfidenceAsync(
                    sentence, _userName, CancellationToken.None, skipAI: true);

                // Fragmentation retry: combine with previous sentence if still uncertain
                if (result.Confidence < 0.70f && !string.IsNullOrEmpty(_previousSentence))
                {
                    string combined = _previousSentence + " " + sentence;
                    var combinedResult = await _questionService.AnalyzeWithConfidenceAsync(
                        combined, _userName, CancellationToken.None, skipAI: true);
                    if (combinedResult.Confidence > result.Confidence)
                    {
                        result   = combinedResult;
                        sentence = combined;
                    }
                }

                if (!result.IsQuestion) return;

                if (result.Confidence >= 0.70f)
                {
                    // High confidence → enqueue FIFO; the worker answers questions in order.
                    // Pause live translation right away so the LLM slot goes to the answer.
                    _isGeneratingAnswer = true;
                    await _questionChannel.Writer.WriteAsync(new QuestionJob(sentence, result));
                    ShowQuestionIndicator(result);
                }
                else if (result.Confidence >= 0.60f)
                {
                    // Medium confidence → show badge; run L4-AI in background
                    ShowQuestionIndicator(result);
                    // Skip the AI check while an answer is being generated: a 1-token
                    // classification isn't worth delaying the in-flight answer for.
                    if (_isGeneratingAnswer) return;
                    string sentCapture = sentence;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var l4Cts = new CancellationTokenSource(L4ClassificationTimeout);
                            bool aiSays = await _questionService.IsQuestionViaAI(sentCapture, l4Cts.Token);
                            if (aiSays)
                            {
                                var aiResult = new QuestionDetectionResult
                                {
                                    IsQuestion  = true,
                                    Confidence  = 0.75f,
                                    Type        = QuestionType.Indirect,
                                    DetectedVia = "L4-AI"
                                };
                                if (_questionChannel.Writer.TryWrite(new QuestionJob(sentCapture, aiResult)))
                                    _isGeneratingAnswer = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("[L4-AI] Classification failed", ex);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("[ProcessSentence] Detection failed", ex);
            }
        }

        // Long-running task: reads from the question channel and generates responses.
        // One response at a time, strictly in FIFO order; questions are never cancelled
        // by newer ones. When the queue drains, paused live translation resumes.
        private async Task RunQuestionWorkerAsync(CancellationToken shutdownToken)
        {
            try
            {
                await foreach (var job in _questionChannel.Reader.ReadAllAsync(shutdownToken))
                {
                    _isGeneratingAnswer = true;
                    using var abort = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    _currentGenerationAbort = abort;

                    try
                    {
                        await GenerateResponseAsync(job, abort.Token);
                    }
                    catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() => ResponseLoadingBar.Visibility = Visibility.Collapsed);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("[QuestionWorker] Generation failed", ex);
                        Dispatcher.Invoke(() =>
                        {
                            ResponseLoadingBar.Visibility = Visibility.Collapsed;
                            ResponseStatus.Text = $"err: {ex.Message[..Math.Min(60, ex.Message.Length)]}";
                        });
                    }

                    // Queue drained → release the LLM back to live translation
                    if (_questionChannel.Reader.Count == 0)
                    {
                        _isGeneratingAnswer = false;
                        ResumePausedTranslation();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // Flushes sentences that arrived while translation was paused, as one batch request.
        private void ResumePausedTranslation()
        {
            string pending;
            lock (_pauseLock)
            {
                if (_pausedTranslationBuffer.Count == 0) return;
                pending = string.Join(" ", _pausedTranslationBuffer);
                _pausedTranslationBuffer.Clear();
            }
            _ = AutoTranslateSentenceAsync(pending);
        }

        // Generates context + EN options + ES translation for a detected question.
        // Runs inside RunQuestionWorkerAsync; token is cancelled only on shutdown or
        // explicit user abort (plus the internal QuestionGenerationTimeout).
        private async Task GenerateResponseAsync(QuestionJob job, CancellationToken token)
        {
            string sentence = job.Sentence;
            var qResult = job.Detection;
            bool isForced = qResult.DetectedVia == "Manual";
            int queued = _questionChannel.Reader.Count;

            string context = GetRecentContext();

            Dispatcher.Invoke(() =>
            {
                QuestionText.Text              = sentence;
                QuestionBadge.Text             = (isForced ? "✏️ Manual" : $"❓ {qResult.Confidence:P0}")
                                               + (queued > 0 ? $" · {queued} en cola" : "");
                QuestionBadgeBorder.Visibility = Visibility.Visible;
                QuestionContextText.Text       = "…";
                ResponseEnText.Text            = "Generando opciones...";
                ResponseEsText.Text            = "...";
                ResponseStatus.Text            = "";
                ResponseLoadingBar.Visibility  = Visibility.Visible;
            });

            // Persist detected question to DB
            if (_currentSession != null && _sessionService != null)
            {
                var question = new DetectedQuestion
                {
                    SessionId    = _currentSession.Id,
                    QuestionText = sentence,
                    Type         = qResult.Type,
                    Context      = qResult.DetectedVia,
                    DetectedAt   = DateTime.Now,
                    WasAnswered  = false
                };
                _currentSession.Questions.Add(question);
                _ = _sessionService.SaveQuestionAsync(question);
            }

            // Single structured request: context + EN options + ES translations in one
            // generation, so LM Studio's queue is occupied once per question instead of 3 times
            // and live translation isn't starved behind the assistant.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(QuestionGenerationTimeout);
            var genToken = timeoutCts.Token;

            // [OPTIONS] streams first so option 1 is readable in seconds — what the student
            // needs to SAY. Context analysis is nice-to-have and comes last. Short limits +
            // max_tokens cap keep total generation under ~10s instead of 20s+.
            string systemPrompt =
                $"You are an English tutor helping a {_englishLevel}-level student named {_userName}. " +
                "The teacher just asked the student a question. Answer FAST and SHORT. " +
                "Reply using EXACTLY this template, keeping the bracketed section headers:\n" +
                "[OPTIONS]\n1. (English response, max 12 words)\n2. (English response, max 12 words)\n3. (English response, max 12 words)\n" +
                "[TRANSLATIONS]\n1. (Spanish translation of option 1)\n2. (Spanish translation of option 2)\n3. (Spanish translation of option 3)\n" +
                "[CONTEXT]\n(ONE short sentence in Spanish: why the teacher asks this)\n" +
                "The 3 options MUST be in English only. Be brief. Output nothing outside the template.";

            try
            {
                // Reasoning models "think" 10-40s before emitting visible content — show live
                // progress so the panel never looks frozen. Throttled to one update per second.
                var reasoningSw = System.Diagnostics.Stopwatch.StartNew();
                int lastShownSecond = -1;

                string full = await _translator.StreamChatAsync(
                    systemPrompt,
                    $"Context:\n{context}\n\nTeacher's question: \"{sentence}\"",
                    partial =>
                    {
                        var (ctx, en, es) = ParseAssistantSections(partial);
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (genToken.IsCancellationRequested) return;
                            if (ctx.Length > 0) QuestionContextText.Text = ctx;
                            if (en.Length > 0)  ResponseEnText.Text      = en;
                            if (es.Length > 0)  ResponseEsText.Text      = es;
                        });
                    },
                    genToken,
                    maxTokens: 500,   // reasoning tokens count against the budget
                    onReasoningUpdate: _ =>
                    {
                        int sec = (int)reasoningSw.Elapsed.TotalSeconds;
                        if (sec == lastShownSecond) return;
                        lastShownSecond = sec;
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (!genToken.IsCancellationRequested)
                                ResponseEnText.Text = $"🤔 el modelo está razonando… ({sec}s)";
                        });
                    });

                genToken.ThrowIfCancellationRequested();

                var (fCtx, fEn, fEs) = ParseAssistantSections(full);
                Dispatcher.Invoke(() =>
                {
                    if (fCtx.Length == 0 && fEn.Length == 0 && fEs.Length == 0)
                        ResponseEnText.Text = full;   // model ignored the template — show raw output
                    else
                    {
                        if (fCtx.Length > 0) QuestionContextText.Text = fCtx;
                        if (fEn.Length > 0)  ResponseEnText.Text      = fEn;
                        if (fEs.Length > 0)  ResponseEsText.Text      = fEs;
                    }
                    ResponseStatus.Text           = DateTime.Now.ToString("HH:mm");
                    ResponseLoadingBar.Visibility = Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timed out (not superseded by a newer question) — free the UI instead of hanging
                Dispatcher.Invoke(() =>
                {
                    ResponseStatus.Text           = "⏱ tiempo agotado";
                    ResponseLoadingBar.Visibility = Visibility.Collapsed;
                });
            }
        }

        // Splits the assistant's structured reply into its three UI sections.
        // Template order: OPTIONS → TRANSLATIONS → CONTEXT (answer first, analysis last).
        // Tolerates partial streams: a section is empty until its header has arrived.
        private static (string Ctx, string En, string Es) ParseAssistantSections(string text)
        {
            string en  = ExtractSection(text, "[OPTIONS]", "[TRANSLATIONS]");
            string es  = ExtractSection(text, "[TRANSLATIONS]", "[CONTEXT]");
            string ctx = ExtractSection(text, "[CONTEXT]", null);
            return (ctx, en, es);
        }

        private static string ExtractSection(string text, string start, string? end)
        {
            int s = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (s < 0) return "";
            s += start.Length;
            int e = end == null ? -1 : text.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
            return (e < 0 ? text[s..] : text[s..e]).Trim();
        }

        // ─── Manual chat input ────────────────────────────────────────────────

        private void ManualQuestionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ManualQuestionHint != null)
                ManualQuestionHint.Visibility = string.IsNullOrEmpty(ManualQuestionBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ManualQuestion_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            try { await SubmitManualQuestion(); }
            catch (Exception ex) { AppLogger.Error("ManualQuestion_KeyDown", ex); }
        }

        private async void ManualQuestion_Submit(object sender, RoutedEventArgs e)
        {
            try { await SubmitManualQuestion(); }
            catch (Exception ex) { AppLogger.Error("ManualQuestion_Submit", ex); }
        }

        private async Task SubmitManualQuestion()
        {
            string question = ManualQuestionBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(question)) return;
            ManualQuestionBox.Clear();
            ManualQuestionHint.Visibility = Visibility.Visible;

            var forced = new QuestionDetectionResult
                { IsQuestion = true, Confidence = 1.0f, Type = QuestionType.Direct, DetectedVia = "Manual" };
            ShowQuestionIndicator(forced);
            _isGeneratingAnswer = true;   // manual questions join the same FIFO queue
            await _questionChannel.Writer.WriteAsync(new QuestionJob(question, forced));
        }

        private void ShowQuestionIndicator(QuestionDetectionResult result) =>
            Dispatcher.Invoke(() =>
            {
                int queued = _questionChannel.Reader.Count;
                QuestionBadge.Text             = $"❓ {result.Confidence:P0}"
                                               + (queued > 1 ? $" · {queued} en cola" : "");
                QuestionBadgeBorder.Visibility = Visibility.Visible;
            });

        // ─── Translation ──────────────────────────────────────────────────────

        private async void Translate_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string fullText = string.IsNullOrWhiteSpace(_captions.FullTranscription)
                    ? TranscriptionText.Text
                    : _captions.FullTranscription + (string.IsNullOrWhiteSpace(_captions.Pending)
                        ? "" : "\n" + _captions.Pending);
                string textToTranslate = fullText.Trim();
                if (string.IsNullOrWhiteSpace(textToTranslate)) return;

                _autoTranslateCts?.Cancel();
                _autoTranslationBuffer = "";

                TranslateButtonBorder.IsHitTestVisible = false;
                TranslateButtonBorder.Opacity = 0.4;
                TranslationStatus.Text = "traduciendo...";
                TranslationText.Text = "";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string result = await TranslateTextAsync(textToTranslate, "", cts.Token);

                if (string.IsNullOrWhiteSpace(result) || result.StartsWith("[Error"))
                {
                    TranslationStatus.Text = "sin resultado";
                    return;
                }

                TranslationText.Text   = result;
                TranslationStatus.Text = DateTime.Now.ToString("HH:mm");
                TranslationScrollViewer.ScrollToBottom();

                if (_currentSession != null)
                {
                    var entry = new TranscriptionEntry
                    {
                        SessionId = _currentSession.Id, OriginalText = textToTranslate,
                        TranslatedText = result, Timestamp = DateTime.Now,
                        Source = EntrySource.LiveCaption, ConfidenceScore = 1.0f
                    };
                    _currentSession.Entries.Add(entry);
                    _ = _sessionService?.SaveEntryAsync(entry);
                    AppendToLog(textToTranslate, result);
                }
            }
            catch (Exception ex)
            {
                TranslationStatus.Text = "error";
                TranslationText.Text   = $"[Error: {ex.Message[..Math.Min(60, ex.Message.Length)]}]";
                AppLogger.Error("Translate_Click", ex);
            }
            finally
            {
                TranslateButtonBorder.IsHitTestVisible = true;
                TranslateButtonBorder.Opacity = 1.0;
            }
        }

        private async Task AutoTranslateSentenceAsync(string sentence)
        {
            if (sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < MinWordsForAutoTranslate)
                return;

            // While an answer is being generated, don't compete for the LLM: buffer the
            // sentence and translate the whole batch when the question queue drains.
            // LibreTranslate doesn't touch LM Studio, so that route is never paused.
            if (_isGeneratingAnswer && !_libreTranslateAvailable)
            {
                lock (_pauseLock) _pausedTranslationBuffer.Add(sentence);
                _ = Dispatcher.InvokeAsync(() =>
                    TranslationStatus.Text = "⏸ pausada (respondiendo pregunta)");
                return;
            }

            _autoTranslateCts?.Cancel();
            _autoTranslateCts = new CancellationTokenSource(AutoTranslateTimeout);
            var token = _autoTranslateCts.Token;

            string snapshot = _autoTranslationBuffer;
            Dispatcher.Invoke(() =>
            {
                TranslationText.Text   = snapshot;
                TranslationStatus.Text = "traduciendo...";
            });

            try
            {
                string result = await TranslateTextAsync(sentence, snapshot, token);
                if (token.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(result) || result.StartsWith("[Error")) return;

                var bufferLines = string.IsNullOrEmpty(_autoTranslationBuffer)
                    ? new System.Collections.Generic.List<string>()
                    : _autoTranslationBuffer.Split('\n').ToList();
                bufferLines.Add(result);
                if (bufferLines.Count > 150) bufferLines = bufferLines[^150..].ToList();
                _autoTranslationBuffer = string.Join("\n", bufferLines);

                Dispatcher.Invoke(() =>
                {
                    TranslationText.Text   = _autoTranslationBuffer;
                    TranslationStatus.Text = DateTime.Now.ToString("HH:mm");
                    TranslationScrollViewer.ScrollToBottom();
                });

                if (_currentSession != null)
                {
                    var entry = new TranscriptionEntry
                    {
                        SessionId = _currentSession.Id, OriginalText = sentence,
                        TranslatedText = result, Timestamp = DateTime.Now,
                        Source = EntrySource.LiveCaption, ConfidenceScore = 1.0f
                    };
                    _currentSession.Entries.Add(entry);
                    _ = _sessionService?.SaveEntryAsync(entry);
                    AppendToLog(sentence, result);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    AppLogger.Error("[AutoTranslate]", ex);
                    Dispatcher.Invoke(() => TranslationStatus.Text = "error auto-traducción");
                }
            }
        }

        private async Task<string> TranslateTextAsync(string text, string snapshot, CancellationToken token)
        {
            if ((DateTime.Now - _libreTranslateLastCheck).TotalSeconds > 30)
            {
                _libreTranslateAvailable = await _libreTranslate.IsRunningAsync();
                _libreTranslateLastCheck = DateTime.Now;
                UpdateLibreTranslateStatus();
            }

            if (_libreTranslateAvailable)
            {
                string result = await _libreTranslate.TranslateAsync(text, token: token);
                if (!string.IsNullOrWhiteSpace(result)) return result;
                _libreTranslateAvailable = false;
                _libreTranslateLastCheck = DateTime.MinValue;
                UpdateLibreTranslateStatus();
            }

            return await _translator.TranslateStreamAsync(
                text,
                partial => Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    TranslationText.Text = string.IsNullOrEmpty(snapshot)
                        ? partial : snapshot + "\n" + partial;
                    if (TranslationScrollViewer.VerticalOffset >= TranslationScrollViewer.ScrollableHeight - 40)
                        TranslationScrollViewer.ScrollToBottom();
                }),
                token: token);
        }

        private void UpdateLibreTranslateStatus()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_libreTranslateAvailable) TranslationStatus.Text = "LibreTranslate ✓";
            });
        }

        // ─── Context ──────────────────────────────────────────────────────────

        // 8 lines: enough to ground the answer; every extra input token slows prefill
        public string GetRecentContext() => _captions.GetRecentContext(8);

        // ─── LM Studio health ─────────────────────────────────────────────────

        private void StartLmStudioHealthCheck()
        {
            _lmStudioHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _lmStudioHealthTimer.Tick += async (_, __) =>
            {
                try { await CheckLmStudioHealthAsync(); }
                catch (Exception ex) { AppLogger.Error("HealthCheck tick", ex); }
            };
            _lmStudioHealthTimer.Start();
            _ = CheckLmStudioHealthAsync();
        }

        private async Task CheckLmStudioHealthAsync()
        {
            bool online = await _translator.IsRunningAsync();
            if (online == _isLmStudioOnline) return;
            _isLmStudioOnline = online;
            if (!online) AppLogger.Warn("LM Studio went offline");
            Dispatcher.Invoke(() =>
            {
                LmStudioStatusBadge.Background = online
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xAF, 0x50))
                    : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x44, 0x44));
                LmStudioStatusText.Text = online ? "● LM Studio" : "● LM Studio offline";
                LmStudioStatusText.Foreground = online
                    ? new SolidColorBrush(Color.FromArgb(0xCC, 0x80, 0xFF, 0x80))
                    : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x88, 0x88));
            });
        }

        // ─── Keyboard shortcuts ───────────────────────────────────────────────

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift;

            if (ctrl && e.Key == Key.Space)
                Copilot_Click(this, new RoutedEventArgs());
            else if (ctrl && e.Key == Key.T)
                Translate_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            else if (ctrl && e.Key == Key.M)
                PrimaryAction_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            else if (ctrl && shift && e.Key == Key.C)
                ClearHistory_Click(this, new RoutedEventArgs());
            else if (e.Key == Key.Escape)
            {
                if (SuggestionsOverlay.Visibility == Visibility.Visible)
                    SuggestionsOverlay.Visibility = Visibility.Collapsed;
                else if (SettingsPanel.Visibility == Visibility.Visible)
                    SettingsPanel.Visibility = Visibility.Collapsed;
                else if (SessionPanel.Visibility == Visibility.Visible)
                    SessionPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ─── Copy buttons ─────────────────────────────────────────────────────

        private void ShowCopyFeedback()
        {
            string prev = StatusText.Text;
            StatusText.Text = "¡Copiado!";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, __) => { StatusText.Text = prev; t.Stop(); };
            t.Start();
        }

        private void CopyQuestion_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(QuestionText.Text)
                && QuestionText.Text != "Esperando pregunta del profesor...")
            {
                Clipboard.SetText(QuestionText.Text);
                ShowCopyFeedback();
            }
        }

        private void CopyEnOptions_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ResponseEnText.Text)
                && !ResponseEnText.Text.StartsWith("Las opciones"))
            {
                Clipboard.SetText(ResponseEnText.Text);
                ShowCopyFeedback();
            }
        }

        private void CopyEsOptions_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ResponseEsText.Text)
                && !ResponseEsText.Text.StartsWith("Las opciones"))
            {
                Clipboard.SetText(ResponseEsText.Text);
                ShowCopyFeedback();
            }
        }

        // ─── Session rename ───────────────────────────────────────────────────

        private void RenameSession_Click(object sender, RoutedEventArgs e)
        {
            SessionTitleBox.Text = CurrentSessionTitle.Text;
            BtnSessions.Visibility = Visibility.Collapsed;
            BtnRenameSession.Visibility = Visibility.Collapsed;
            SessionRenamePanel.Visibility = Visibility.Visible;
            SessionTitleBox.Focus();
            SessionTitleBox.SelectAll();
        }

        private void SessionTitleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitSessionRename();
            else if (e.Key == Key.Escape) CancelSessionRename();
        }

        private void SessionTitleBox_LostFocus(object sender, RoutedEventArgs e) =>
            CommitSessionRename();

        private void CommitSessionRename()
        {
            string newTitle = SessionTitleBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newTitle) && _currentSession != null)
            {
                _currentSession.Title = newTitle;
                CurrentSessionTitle.Text = newTitle;
                _ = _sessionService?.SaveSessionAsync(_currentSession);
            }
            CancelSessionRename();
        }

        private void CancelSessionRename()
        {
            SessionRenamePanel.Visibility = Visibility.Collapsed;
            BtnSessions.Visibility = Visibility.Visible;
            BtnRenameSession.Visibility = Visibility.Visible;
        }

        // ─── Settings ─────────────────────────────────────────────────────────

        private void AppendToLog(string original, string translated)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {original} | {translated}{Environment.NewLine}");
            }
            catch (Exception ex) { AppLogger.Error("AppendToLog", ex); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("userName",      out var u)) _userName          = u.GetString() ?? "Charlie";
                if (doc.RootElement.TryGetProperty("englishLevel",  out var l)) _englishLevel      = l.GetString() ?? "B1";
                if (doc.RootElement.TryGetProperty("lmStudioModel", out var o)) _lmStudioModelName = o.GetString() ?? "llama-3.2-3b-instruct";
            }
            catch (Exception ex) { AppLogger.Error("LoadSettings", ex); }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
                {
                    userName      = _userName,
                    englishLevel  = _englishLevel,
                    lmStudioModel = _lmStudioModelName
                }));
            }
            catch (Exception ex) { AppLogger.Error("SaveSettings", ex); }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainBorder != null)
            {
                byte alpha = (byte)(e.NewValue * 255);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            }
        }

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SettingsPanel.Visibility == Visibility.Visible)
                {
                    SettingsPanel.Visibility = Visibility.Collapsed;
                    SaveSettings();
                }
                else
                {
                    UserNameBox.Text = _userName;
                    foreach (ComboBoxItem item in LevelCombo.Items)
                        if (item.Content.ToString() == _englishLevel) { LevelCombo.SelectedItem = item; break; }
                    await PopulateLmStudioModelsAsync();
                    SettingsPanel.Visibility = Visibility.Visible;
                    LoadMicrophones();
                }
            }
            catch (Exception ex) { AppLogger.Error("Settings_Click", ex); }
        }

        private async Task PopulateLmStudioModelsAsync()
        {
            LmStudioModelStatus.Text = "Cargando modelos...";
            var models = await _translator.GetInstalledModelsAsync();
            LmStudioModelCombo.ItemsSource  = models.Count > 0 ? models : new System.Collections.Generic.List<string> { _lmStudioModelName };
            LmStudioModelCombo.SelectedItem = models.Contains(_lmStudioModelName) ? _lmStudioModelName
                                            : (models.Count > 0 ? models[0] : _lmStudioModelName);
            LmStudioModelStatus.Text = models.Count > 0
                ? $"{models.Count} modelo(s) instalado(s)"
                : "LM Studio offline o sin modelos";
        }

        private void LoadMicrophones()
        {
            var devices = _micService.GetMicrophones();
            ComboMicrophones.ItemsSource = devices;
            ComboMicrophones.DisplayMemberPath = "Name";
            if (ComboMicrophones.SelectedIndex < 0 && devices.Count > 0)
                ComboMicrophones.SelectedIndex = 0;
        }

        private void ComboMicrophones_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboMicrophones.SelectedItem is AudioCaptureService.AudioDevice device)
                _micService.SetDevice(device.Index);
        }

        private async void TestSystem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestResultText.Text = "Ejecutando diagnósticos...";
                bool lmStudioOk = await _translator.IsRunningAsync();
                bool micOk = ComboMicrophones.SelectedItem != null;

                var sb = new StringBuilder();
                sb.AppendLine(lmStudioOk ? "✅ LM Studio: En línea"  : "❌ LM Studio: Sin conexión");
                sb.AppendLine("✅ Live Captions: Activo");
                sb.AppendLine(micOk       ? "✅ Micrófono: Seleccionado" : "⚠️ Micrófono: Ninguno");

                TestResultText.Text       = sb.ToString();
                TestResultText.Foreground = lmStudioOk ? Brushes.LightGreen : Brushes.Orange;
            }
            catch (Exception ex) { AppLogger.Error("TestSystem_Click", ex); }
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SaveSettings();
        }

        private void UserNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
                _userName = tb.Text.Trim();
        }

        private void LevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
                _englishLevel = item.Content.ToString() ?? "B1";
        }

        private void WhisperModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void LmStudioModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LmStudioModelCombo.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
            {
                _lmStudioModelName = model;
                _translator.SetModel(model);
            }
        }

        private async void RefreshLmStudioModels_Click(object sender, RoutedEventArgs e)
        {
            try { await PopulateLmStudioModelsAsync(); }
            catch (Exception ex) { AppLogger.Error("RefreshLmStudioModels_Click", ex); }
        }

        private void Topmost_Checked(object sender, RoutedEventArgs e)   => Topmost = true;
        private void Topmost_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

        private void CloseApp_Click(object sender, RoutedEventArgs e) => Close();

        // ─── Embedded assistant panel ─────────────────────────────────────────

        private void Copilot_Click(object sender, RoutedEventArgs e) => ToggleAssistantPanel();

        private void CloseAssistantPanel_Click(object sender, RoutedEventArgs e)
        {
            SuggestionsOverlay.Visibility = Visibility.Collapsed;
            _isAssistantOpen = false;
        }

        private async void RefreshAssistantPanel_Click(object sender, RoutedEventArgs e)
        {
            try { await LoadAssistantSuggestionsAsync(); }
            catch (Exception ex) { AppLogger.Error("RefreshAssistantPanel_Click", ex); }
        }

        private void CopyAssistantSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string text)
            {
                Clipboard.SetText(text);
                AssistantStatusText.Text = "¡Copiado!";

                var lastQ = _currentSession?.Questions.LastOrDefault(q => !q.WasAnswered);
                if (lastQ != null)
                {
                    lastQ.WasAnswered = true;
                    _ = _sessionService?.SaveQuestionAsync(lastQ);
                }

                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, __) => { AssistantStatusText.Text = ""; t.Stop(); };
                t.Start();
            }
        }

        private void ToggleAssistantPanel()
        {
            _isAssistantOpen = true;
            SuggestionsOverlay.Visibility = Visibility.Visible;
            AssistantContextText.Text = GetRecentContext();
            _ = LoadAssistantSuggestionsAsync();
        }

        private async Task LoadAssistantSuggestionsAsync()
        {
            AssistantLoadingText.Visibility = Visibility.Visible;
            AssistantSuggestionsList.ItemsSource = null;
            AssistantStatusText.Text = "Analyzing...";

            try
            {
                string context = GetRecentContext();
                AssistantContextText.Text = context;

                string systemPrompt =
                    $"You are an exam assistant for a {_englishLevel} English student named {_userName}." +
                    " When given a recent conversation, produce ONLY a JSON array of objects." +
                    " Each object has exactly these keys: title (string), text (string), translation (string in Spanish)," +
                    " pronunciation (string with IPA or simple phonetic hint for 1-3 key words)." +
                    $" Give exactly 3 candidate answers appropriate for {_englishLevel} level (≤ 40 words each). No other text.";

                string userPrompt = $"Conversation:\n{context}" +
                    "\n\nRespond with ONLY the JSON array. No markdown fences, no explanation." +
                    "\nExample: [{\"title\":\"Option 1\",\"text\":\"...\",\"translation\":\"...\"}]";

                string raw = await _translator.AskAsync(systemPrompt, userPrompt, CancellationToken.None);
                var suggestions = ParseSuggestions(raw);
                AssistantSuggestionsList.ItemsSource = suggestions;
                AssistantStatusText.Text = $"{suggestions.Count} suggestions";
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoadAssistantSuggestions", ex);
                AssistantStatusText.Text = $"Error: {ex.Message[..Math.Min(40, ex.Message.Length)]}";
            }
            finally
            {
                AssistantLoadingText.Visibility = Visibility.Collapsed;
            }
        }

        private static System.Collections.Generic.List<AssistantSuggestion> ParseSuggestions(string raw)
        {
            try
            {
                int start = raw.IndexOf('[');
                int end   = raw.LastIndexOf(']');
                if (start < 0 || end < 0) return [];
                string json = raw[start..(end + 1)];
                return JsonSerializer.Deserialize<System.Collections.Generic.List<AssistantSuggestion>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { return []; }
        }

        // ─── Primary action (pause/resume) ────────────────────────────────────

        private void Minimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void PrimaryAction_Click(object sender, MouseButtonEventArgs e)
        {
            _isPaused = !_isPaused;
            if (_isPaused)
            {
                _reader?.Stop();
                UpdatePrimaryButton("▶", "Reanudar", "#FF9800");
                StatusText.Text = "Pausado";
                ModeText.Text = "PAUSADO";
                ModeIndicatorBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x98, 0x00));
            }
            else
            {
                _reader?.Start();
                UpdatePrimaryButton("⏸", "Pausar", "#4CAF50");
                StatusText.Text = "Live Captions";
                ModeText.Text = "LIVE";
                ModeIndicatorBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
            }
        }

        private void SecondaryAction_Click(object sender, MouseButtonEventArgs e) { }

        private void UpdatePrimaryButton(string icon, string text, string color)
        {
            PrimaryButtonIcon.Text = icon;
            PrimaryButtonText.Text = text;
            PrimaryButtonBorder.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!;
        }

        private void MicToggle_Click(object sender, RoutedEventArgs e)
        {
            _isMicActive = !_isMicActive;
            if (_isMicActive)
            {
                _micService.Start();
                StatusText.Text = "Micrófono activo";
                BtnMicToggle.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xAF, 0x50));
            }
            else
            {
                _micService.Stop();
                StatusText.Text = "Micrófono desactivado";
                BtnMicToggle.Background = Brushes.Transparent;
            }
        }

        // ─── Clear / reset ────────────────────────────────────────────────────

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "¿Limpiar todos los paneles?\nEsta acción no se puede deshacer.",
                "Confirmar limpieza", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _currentGenerationAbort?.Cancel();
            while (_questionChannel.Reader.TryRead(out _)) { }   // drain pending questions
            _isGeneratingAnswer = false;
            lock (_pauseLock) _pausedTranslationBuffer.Clear();
            _autoTranslateCts?.Cancel();
            _autoTranslationBuffer = "";
            _previousSentence = "";
            _captions.Reset();
            History.Clear();
            ResetAssistantPanel();
        }

        private void ResetAssistantPanel()
        {
            TranscriptionText.Text  = "";
            TranslationText.Text    = "";
            TranslationStatus.Text  = "";
            QuestionText.Text        = "Esperando pregunta del profesor...";
            QuestionContextText.Text = "El contexto de la pregunta aparecerá aquí...";
            ResponseEnText.Text      = "Las opciones aparecerán al detectar una pregunta...";
            ResponseEsText.Text      = "Las opciones en español aparecerán aquí...";
            ResponseStatus.Text      = "";
            ResponseLoadingBar.Visibility  = Visibility.Collapsed;
            QuestionBadgeBorder.Visibility = Visibility.Collapsed;
        }

        private void ToggleHistory_Click(object sender, RoutedEventArgs e) { }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        // ─── Browser scan ─────────────────────────────────────────────────────

        private async void BrowserScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Conectando al navegador...";
                string text = "";
                bool isCdpConnected = _chromeService.ConnectToExistingSession();

                if (isCdpConnected)
                {
                    StatusText.Text = "CDP conectado";
                    text = _chromeService.CaptureActiveTabContent();
                    _chromeService.Disconnect();
                }
                else
                {
                    StatusText.Text = "Escaneando...";
                    await Task.Delay(2000);
                    text = await _browserScanner.GetSelectedTextAsync();
                }

                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("Error") || text.StartsWith("Debug"))
                {
                    StatusText.Text = isCdpConnected ? "Sin contenido CDP" : "Escaneo fallido";
                }
                else
                {
                    StatusText.Text = "Contenido capturado";
                    string captureContext = $"[SOURCE: {(isCdpConnected ? "CHROME_DOM" : "SCREEN_READER")}]\n{text}";
                    if (!_isAssistantOpen)
                        ToggleAssistantPanel();
                    else
                    {
                        AssistantContextText.Text = captureContext;
                        _ = LoadAssistantSuggestionsAsync();
                    }
                }
            }
            catch (Exception ex) { AppLogger.Error("BrowserScan_Click", ex); }
        }

        // ─── Summary ──────────────────────────────────────────────────────────

        private async void GenerateSummary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string full = TranscriptionText.Text.Trim();
                if (string.IsNullOrWhiteSpace(full)) { StatusText.Text = "Sin transcripción para resumir."; return; }

                StatusText.Text = "Generando resumen...";
                string summary  = await _translator.GenerateSummaryAsync(full);
                string filename = $"Resumen_Clase_{DateTime.Now:yyyyMMdd_HHmm}.md";
                File.WriteAllText(filename, summary);
                StatusText.Text = "Resumen guardado";
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo { FileName = filename, UseShellExecute = true });
                }
                catch { }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error al generar resumen";
                AppLogger.Error("GenerateSummary_Click", ex);
                MessageBox.Show(ex.Message, "Error al generar resumen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── Sessions ─────────────────────────────────────────────────────────

        private async Task CreateNewSessionAsync()
        {
            if (_currentSession != null && _currentSession.Entries.Count >= 3)
                _ = GenerateAndSaveSummaryAsync(_currentSession);

            string defaultTitle = $"Sesión {DateTime.Now:dd MMM, HH:mm}";
            _currentSession = await _sessionService!.CreateSessionAsync(defaultTitle);

            Dispatcher.Invoke(() =>
            {
                CurrentSessionTitle.Text = _currentSession.Title;
                _autoTranslateCts?.Cancel();
                _autoTranslationBuffer = "";
                _previousSentence = "";
                _captions.Reset();
                History.Clear();
                ResetAssistantPanel();
            });
        }

        private async Task GenerateAndSaveSummaryAsync(Session session)
        {
            try
            {
                var transcript = string.Join("\n", session.Entries
                    .OrderBy(e => e.Timestamp)
                    .Take(25)
                    .Select(e => $"{(e.Source == EntrySource.Microphone ? _userName : "Profesor")}: {e.OriginalText}"));

                string summary = await _translator.GenerateShortSummaryAsync(transcript);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    session.Summary = summary;
                    await _sessionService!.SaveSessionAsync(session);
                }
            }
            catch (Exception ex) { AppLogger.Error("GenerateAndSaveSummary", ex); }
        }

        private async void Sessions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SessionPanel.Visibility == Visibility.Visible)
                    SessionPanel.Visibility = Visibility.Collapsed;
                else
                {
                    SettingsPanel.Visibility = Visibility.Collapsed;
                    SuggestionsOverlay.Visibility = Visibility.Collapsed;
                    SessionPanel.Visibility = Visibility.Visible;
                    await RefreshSessionsList();
                }
            }
            catch (Exception ex) { AppLogger.Error("Sessions_Click", ex); }
        }

        private async Task RefreshSessionsList(string query = "")
        {
            var sessions = await _sessionService!.SearchSessionsAsync(query);
            SessionsList.ItemsSource = sessions;
        }

        private async void NewSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var confirm = MessageBox.Show("¿Crear una nueva sesión?\nLa sesión actual se guardará.",
                    "Nueva sesión", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                if (_currentSession != null)
                    await _sessionService!.SaveSessionAsync(_currentSession);

                await CreateNewSessionAsync();
                SessionPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { AppLogger.Error("NewSession_Click", ex); }
        }

        private void CloseSessions_Click(object sender, RoutedEventArgs e) =>
            SessionPanel.Visibility = Visibility.Collapsed;

        private async void DeleteSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SessionsList.SelectedItem is not Session selected) return;

                if (selected.Id == _currentSession?.Id)
                {
                    MessageBox.Show("No puedes eliminar la sesión activa.", "Eliminar",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirm = MessageBox.Show($"¿Eliminar \"{selected.Title}\"?", "Confirmar eliminación",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                await _sessionService!.DeleteSessionAsync(selected.Id);
                await RefreshSessionsList();
            }
            catch (Exception ex) { AppLogger.Error("DeleteSession_Click", ex); }
        }

        private async void SessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (SessionsList.SelectedItem is not Session selectedSession) return;

                var loaded = await _sessionService!.LoadSessionAsync(selectedSession.Id);
                if (loaded == null) return;

                _currentSession = loaded;
                _sessionService.StartAutoSave(_currentSession);
                CurrentSessionTitle.Text = loaded.Title;

                var entries = loaded.Entries.OrderBy(x => x.Timestamp).ToList();
                string committed = string.Join("\n", entries.Select(en => en.OriginalText));
                _captions.LoadHistory(committed);

                TranscriptionText.Text = committed;
                TranslationText.Text   = string.Join("\n\n",
                    entries.Where(en => !string.IsNullOrWhiteSpace(en.TranslatedText))
                           .Select(en => en.TranslatedText));
                TranslationStatus.Text = entries.Count > 0
                    ? entries.Last().Timestamp.ToString("HH:mm") : "";

                History.Clear();
                SessionPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { AppLogger.Error("SessionsList_MouseDoubleClick", ex); }
        }

        private void SearchSessionsBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchSessionsBox.Text == "Buscar...")
            {
                SearchSessionsBox.Text = "";
                SearchSessionsBox.Foreground = Brushes.White;
            }
        }

        private void SearchSessionsBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchSessionsBox.Text))
            {
                SearchSessionsBox.Text = "Buscar...";
                SearchSessionsBox.Foreground = Brushes.Gray;
            }
        }

        private async void SearchSessionsBox_KeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                string query = SearchSessionsBox.Text == "Buscar..." ? "" : SearchSessionsBox.Text;
                await RefreshSessionsList(query);
            }
            catch (Exception ex) { AppLogger.Error("SearchSessionsBox_KeyUp", ex); }
        }

        private async void ExportSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Session? sessionToExport = SessionsList.SelectedItem as Session;

                if (sessionToExport == null)
                {
                    if (_currentSession == null)
                    {
                        MessageBox.Show("Selecciona una sesión para exportar.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (MessageBox.Show("No hay sesión seleccionada. ¿Exportar la sesión activa?", "Export",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    sessionToExport = _currentSession;
                }

                var dialog = new SaveFileDialog
                {
                    Title  = "Exportar sesión a Markdown",
                    Filter = "Markdown Files (*.md)|*.md|All Files (*.*)|*.*",
                    FileName = $"Session_{sessionToExport.StartTime:yyyyMMdd_HHmm}.md"
                };

                if (dialog.ShowDialog() != true) return;

                string content = await _sessionService!.ExportToMarkdownAsync(sessionToExport.Id);
                await File.WriteAllTextAsync(dialog.FileName, content);
                MessageBox.Show("Exportación exitosa.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Error("ExportSession_Click", ex);
                MessageBox.Show($"Error al exportar: {ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Vocabulary_Click(object sender, RoutedEventArgs e)
        {
            var vocabWin = new VocabularyWindow(_vocabularyService);
            vocabWin.Topmost = true;
            vocabWin.ShowDialog();
        }

        // ─── DWM blur-behind ──────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_BLURBEHIND
        {
            public uint   dwFlags;
            public bool   fEnable;
            public IntPtr hRgnBlur;
            public bool   fTransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND pBlurBehind);

        private void ApplyBlurBehind()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var blur = new DWM_BLURBEHIND { dwFlags = 0x01, fEnable = true, hRgnBlur = IntPtr.Zero };
                DwmEnableBlurBehindWindow(hwnd, ref blur);
            }
            catch { }
        }
    }

    public record QuestionJob(string Sentence, QuestionDetectionResult Detection);

    public class AssistantSuggestion
    {
        public string Title         { get; set; } = "";
        public string Text          { get; set; } = "";
        public string Translation   { get; set; } = "";
        public string Pronunciation { get; set; } = "";
    }

    public class TranslationItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _translatedText = "";

        public string OriginalText { get; set; } = "";
        public string TranslatedText
        {
            get => _translatedText;
            set
            {
                _translatedText = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(TranslatedText)));
            }
        }
        public DateTime Timestamp { get; set; }
        public string SourceIcon  { get; set; } = "";
        public string SourceColor { get; set; } = "White";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
