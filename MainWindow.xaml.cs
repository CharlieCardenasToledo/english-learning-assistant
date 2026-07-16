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
using System.Windows.Documents;
using WindowsLiveCaptionsReader.Services;
using WindowsLiveCaptionsReader.Models;

namespace WindowsLiveCaptionsReader
{
    public partial class MainWindow : Window
    {
        // ─── Services ─────────────────────────────────────────────────────────
        // null! : assigned in the constructor's try block; on failure the app shows an
        // error dialog and runs degraded — callers already guard with null checks.
        private CaptionReader _reader = null!;
        private LmStudioService _translator;
        private WhisperService _whisperService;
        private AudioCaptureService _micService;
        private SessionService _sessionService = null!;
        private QuestionDetectionService _questionService = null!;
        private VocabularyService _vocabularyService = null!;
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
        private TextBlock? _currentEnText;
        private TextBlock? _currentEsText;
        private TextBlock? _currentCtxText;
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
        // Committed caption fragments accumulate here until the sentence is complete:
        // either it ends with terminal punctuation, or captions go quiet for
        // SentenceSettleDelay (speaker paused) — then the whole thing is processed at
        // once so the question detector always sees the FULL question, never a half.
        private readonly List<string> _fragmentBuffer = new();
        private DispatcherTimer _settleTimer = null!;
        private static readonly TimeSpan SentenceSettleDelay = TimeSpan.FromSeconds(1.2);

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

        private DispatcherTimer? _lmStudioHealthTimer;
        public ObservableCollection<TranslationItem> History { get; set; }
        private List<VocabularyItem> _allVocabWords = new();

        private readonly DispatcherTimer _playbackTimer;
        private bool _isSliderDragging = false;
        private readonly EnglishLearningAssistant.Application.Transcription.FileTranscriptionService _fileTranscriptionService;
        private QuestionJob? _lastProcessedQuestionJob;


        // ─── Constructor ──────────────────────────────────────────────────────

        public MainWindow(
            CaptionReader reader,
            LmStudioService translator,
            WhisperService whisperService,
            AudioCaptureService micService,
            SessionService sessionService,
            QuestionDetectionService questionService,
            VocabularyService vocabularyService,
            EnglishLearningAssistant.Application.Transcription.FileTranscriptionService fileTranscriptionService)
        {
            InitializeComponent();
            History = new ObservableCollection<TranslationItem>();
            _reader = reader;
            _translator = translator;
            _whisperService = whisperService;
            _micService = micService;
            _sessionService = sessionService;
            _questionService = questionService;
            _vocabularyService = vocabularyService;
            _fileTranscriptionService = fileTranscriptionService;


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

            _settleTimer = new DispatcherTimer { Interval = SentenceSettleDelay };
            _settleTimer.Tick += SettleTimer_Tick;

            _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _playbackTimer.Tick += PlaybackTimer_Tick;

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
                if (_vocabularyService != null)
                {
                    await _vocabularyService.InitializeAsync();
                    await LoadVocabulary();
                }

                await EnsureServicesAreRunning();
                _reader?.Start();

                await CreateNewSessionAsync();
                StartLmStudioHealthCheck();

                // Start the question-generation worker (runs for the lifetime of the window)
                _ = RunQuestionWorkerAsync(_pipelineShutdown.Token);

                // Detect system hardware asynchronously (Fase 10)
                _ = Task.Run(() =>
                {
                    try
                    {
                        var specs = Services.HardwareDetector.DetectHardware();
                        var recommendation = Services.HardwareDetector.GetModelRecommendation(specs);
                        
                        Dispatcher.Invoke(() =>
                        {
                            HardwareCpuText.Text = $"CPU: {specs.CpuName} ({specs.CpuCores} núcleos)";
                            HardwareRamText.Text = $"RAM: {specs.TotalRamGb} GB instalados";
                            HardwareGpuText.Text = $"GPU: {specs.GpuName}";
                            HardwareRecommendationText.Text = $"Disco C: {specs.FreeDiskGb} GB libres de {specs.TotalDiskGb} GB\n\n" + recommendation;

                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Hardware detection failed", ex);
                    }
                });

                AppLogger.Info("Application started");

            }
            catch (Exception ex)
            {
                AppLogger.Error("MainWindow_Loaded failed", ex);
                try
                {
                    var dbPath = EnglishLearningAssistant.Core.Models.AppConfiguration.Instance.Storage.DatabasePath;
                    var detail = $"DatabasePath: {dbPath}\nMessage: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException?.Message}\nInner Stack: {ex.InnerException?.StackTrace}";
                    File.WriteAllText("startup_error.txt", detail);
                }
                catch { }
                
                MessageBox.Show($"Error durante la inicialización: {ex.Message}\n\nDetalle: {ex.InnerException?.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _pipelineShutdown.Cancel();
            _reader?.Stop();
            _micService.Dispose();
            _whisperService.Dispose();
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
                    _ = Dispatcher.InvokeAsync(() => TranslationStatus.Text = "LibreTranslate ✓");
            });
        }

        // ─── Caption capture ──────────────────────────────────────────────────

        private void Reader_StatusChanged(object? sender, string e) =>
            Dispatcher.Invoke(() => StatusText.Text = e);

        private void Reader_TextChanged(object? sender, string text)
        {
            if (_isPaused || string.IsNullOrWhiteSpace(text)) return;
            Dispatcher.Invoke(() => AppendCaption(text, sender is AudioCaptureService));
        }

        private void AppendCaption(string newCaption, bool isMic = false)
        {
            string? committed = _captions.Feed(newCaption);

            if (committed != null)
            {
                _fragmentBuffer.Add(committed);
                // Complete sentence — process right away (fast path).
                // Otherwise hold the fragment; the settle timer will flush it.
                if (EndsSentence(committed)) FlushFragmentBuffer();
            }

            // Every caption update restarts the timer; when it fires, captions have
            // been quiet for SentenceSettleDelay — the speaker finished the sentence.
            _settleTimer.Stop();
            _settleTimer.Start();

            TranscriptionText.Text = _captions.GetDisplayText();
            StatusText.Text = isMic ? "Mic..." : "Live Captions";

            if (TranscriptionScrollViewer.VerticalOffset >= TranscriptionScrollViewer.ScrollableHeight - 40)
                TranscriptionScrollViewer.ScrollToBottom();
        }

        private void SettleTimer_Tick(object? sender, EventArgs e)
        {
            _settleTimer.Stop();
            string? forced = _captions.ForceCommitPending();
            if (forced != null)
            {
                _fragmentBuffer.Add(forced);
                TranscriptionText.Text = _captions.GetDisplayText();
            }
            FlushFragmentBuffer();
        }

        // Joins buffered fragments into one sentence and sends it through detection
        // and translation. The detector gets the full question, not the pieces.
        private void FlushFragmentBuffer()
        {
            if (_fragmentBuffer.Count == 0) return;
            string sentence = string.Join(" ", _fragmentBuffer).Trim();
            _fragmentBuffer.Clear();
            if (sentence.Length == 0) return;

            string previous = _previousSentence;
            _previousSentence = sentence;
            _ = ProcessSentenceAsync(sentence, previous);
            _ = AutoTranslateSentenceAsync(sentence);
        }

        private static bool EndsSentence(string s)
        {
            s = s.TrimEnd();
            return s.Length > 0 && (s[^1] == '.' || s[^1] == '?' || s[^1] == '!');
        }

        // ─── Question detection pipeline ──────────────────────────────────────

        // Runs on a thread-pool thread (fire-and-forget from AppendCaption).
        // L1-L3 detection is regex-only (<1ms). L4-AI runs in the background
        // without blocking this method or the generation worker.
        private async Task ProcessSentenceAsync(string sentence, string previousSentence = "")
        {
            try
            {
                // L1-L3 fast path — no LLM call
                var result = await _questionService.AnalyzeWithConfidenceAsync(
                    sentence, _userName, CancellationToken.None, skipAI: true);

                // Fragmentation retry: combine with previous sentence if still uncertain
                if (result.Confidence < 0.70f && !string.IsNullOrEmpty(previousSentence))
                {
                    string combined = previousSentence + " " + sentence;
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
                    // T7.2: Cancelar generación activa y vaciar cola
                    _currentGenerationAbort?.Cancel();
                    while (_questionChannel.Reader.TryRead(out _)) { }

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

                                // T7.2: Cancelar generación activa y vaciar cola
                                _currentGenerationAbort?.Cancel();
                                while (_questionChannel.Reader.TryRead(out _)) { }

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
            _lastProcessedQuestionJob = job;
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
                ResponseStatus.Text            = "";
                ResponseLoadingBar.Visibility  = Visibility.Visible;

                _currentEnText  = AppendAnswerBlock(ResponseEnStack,  ResponseScrollViewer,  sentence, "Generando opciones...",
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)), 13);
                _currentEsText  = AppendAnswerBlock(ResponseEsStack,  ResponseEsScrollViewer, sentence, "...",
                    System.Windows.Media.Brushes.White, 13);
                _currentCtxText = AppendAnswerBlock(QuestionContextStack, QuestionContextScrollViewer, sentence, "…",
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xBB, 0xCC, 0xE8, 0xFF)), 12, italic: true);
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
            // T7.3: Opciones diferenciadas por longitud y formalidad
            string systemPrompt =
                $"You are an English tutor helping a {_englishLevel}-level student named {_userName}. " +
                "The teacher just asked the student a question. " +
                "Provide 3 response options of different lengths and levels of formality:\n" +
                "Option 1: Brief (Very short, direct, max 5 words)\n" +
                "Option 2: Natural (Standard conversational response, max 12 words)\n" +
                "Option 3: Detailed (More formal, expanded explanation, max 25 words)\n" +
                "Reply using EXACTLY this template, keeping the bracketed section headers:\n" +
                "[OPTIONS]\n1. (Brief option)\n2. (Natural option)\n3. (Detailed option)\n" +
                "[TRANSLATIONS]\n1. (Spanish translation of option 1)\n2. (Spanish translation of option 2)\n3. (Spanish translation of option 3)\n" +
                "[CONTEXT]\n(ONE short sentence in Spanish explaining the grammatical or situational context of the question)\n" +
                "The 3 options MUST be in English only. Output nothing outside the template.";


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
                            if (ctx.Length > 0 && _currentCtxText != null) _currentCtxText.Text = ctx;
                            if (en.Length > 0  && _currentEnText  != null) _currentEnText.Text  = en;
                            if (es.Length > 0  && _currentEsText  != null) _currentEsText.Text  = es;
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
                            if (!genToken.IsCancellationRequested && _currentEnText != null)
                                _currentEnText.Text = $"🤔 el modelo está razonando… ({sec}s)";
                        });
                    });

                genToken.ThrowIfCancellationRequested();

                var (fCtx, fEn, fEs) = ParseAssistantSections(full);
                Dispatcher.Invoke(() =>
                {
                    if (fCtx.Length == 0 && fEn.Length == 0 && fEs.Length == 0)
                    {
                        if (_currentEnText != null) _currentEnText.Text = full;
                    }
                    else
                    {
                        if (fCtx.Length > 0 && _currentCtxText != null) _currentCtxText.Text = fCtx;
                        if (fEn.Length > 0  && _currentEnText  != null) _currentEnText.Text  = fEn;
                        if (fEs.Length > 0  && _currentEsText  != null) _currentEsText.Text  = fEs;
                    }
                    ResponseStatus.Text           = $"{DateTime.Now:HH:mm} ({reasoningSw.Elapsed.TotalSeconds:F1}s)";
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

        // Appends a new answer card to a StackPanel and returns the TextBlock that will receive the answer text.
        // Hides the placeholder on first use. Scrolls to bottom so the newest answer is visible.
        private TextBlock AppendAnswerBlock(
            StackPanel stack, ScrollViewer scroller,
            string question, string placeholder,
            System.Windows.Media.Brush fg, double fontSize, bool italic = false)
        {
            // Hide placeholder (it's always the first child when present)
            if (stack.Children.Count == 1 && stack.Children[0] is TextBlock ph && (ph.Opacity < 1 || ph.Name.Contains("Placeholder")))
                ph.Visibility = Visibility.Collapsed;
            else if (stack.Children.Count > 0)
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 6),
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF))
                });

            stack.Children.Add(new TextBlock
            {
                Text = question,
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });

            var tb = new TextBlock
            {
                Text = placeholder,
                Foreground = fg,
                FontSize = fontSize,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = fontSize < 13 ? 18 : 20,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            };
            stack.Children.Add(tb);
            scroller.ScrollToBottom();
            return tb;
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
            // T7.2: Cancelar generación activa y vaciar cola
            _currentGenerationAbort?.Cancel();
            while (_questionChannel.Reader.TryRead(out _)) { }

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
                    var elapsed = DateTime.Now - _currentSession.StartTime;
                    var entry = new TranscriptionEntry
                    {
                        SessionId = _currentSession.Id, OriginalText = textToTranslate,
                        TranslatedText = result, Timestamp = DateTime.Now,
                        Source = EntrySource.LiveCaption, ConfidenceScore = 1.0f,
                        AudioStartTime = Math.Max(0, elapsed.TotalSeconds - 4),
                        AudioEndTime = elapsed.TotalSeconds
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
                    var elapsed = DateTime.Now - _currentSession.StartTime;
                    var entry = new TranscriptionEntry
                    {
                        SessionId = _currentSession.Id, OriginalText = sentence,
                        TranslatedText = result, Timestamp = DateTime.Now,
                        Source = EntrySource.LiveCaption, ConfidenceScore = 1.0f,
                        AudioStartTime = Math.Max(0, elapsed.TotalSeconds - 4),
                        AudioEndTime = elapsed.TotalSeconds
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
                else if (HistoryPanel.Visibility == Visibility.Visible)
                    SwitchToPanel(LiveViewPanel, BtnNavLive);
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

        // T7.4: Regenerar respuestas sugeridas

        private async void RegenerateAnswers_Click(object sender, RoutedEventArgs e)
        {
            if (_lastProcessedQuestionJob == null) return;

            _currentGenerationAbort?.Cancel();
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(_pipelineShutdown.Token);
            _currentGenerationAbort = abort;

            try
            {
                await GenerateResponseAsync(_lastProcessedQuestionJob, abort.Token);
            }
            catch (Exception ex)
            {
                AppLogger.Error("RegenerateAnswers_Click failed", ex);
            }
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
            string? text = _currentEnText?.Text;
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("Generando"))
            {
                Clipboard.SetText(text);
                ShowCopyFeedback();
            }
        }

        private void CopyEsOptions_Click(object sender, RoutedEventArgs e)
        {
            string? text = _currentEsText?.Text;
            if (!string.IsNullOrWhiteSpace(text) && text != "...")
            {
                Clipboard.SetText(text);
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
                SwitchToPanel(SettingsPanel, BtnNavSettings);
                await PopulateLmStudioModelsAsync();
                LoadMicrophones();
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
            SwitchToPanel(LiveViewPanel, BtnNavLive);
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
            _settleTimer.Stop();
            _fragmentBuffer.Clear();
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
            ResponseStatus.Text      = "";
            ResponseLoadingBar.Visibility  = Visibility.Collapsed;
            QuestionBadgeBorder.Visibility = Visibility.Collapsed;

            ResponseEnStack.Children.Clear();
            ResponseEnStack.Children.Add(ResponseEnPlaceholder);
            ResponseEsStack.Children.Clear();
            ResponseEsStack.Children.Add(ResponseEsPlaceholder);
            QuestionContextStack.Children.Clear();
            QuestionContextStack.Children.Add(QuestionContextPlaceholder);

            _currentEnText  = null;
            _currentEsText  = null;
            _currentCtxText = null;
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

        private void GenerateSummary_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            var menu = new ContextMenu();
            
            var item1 = new MenuItem { Header = "📝 Resumen General de Clase" };
            item1.Click += async (s, ev) => await ProcessSummaryGeneration(LmStudioService.SummaryTemplate.General);
            menu.Items.Add(item1);

            var item2 = new MenuItem { Header = "📚 Glosario y Vocabulario" };
            item2.Click += async (s, ev) => await ProcessSummaryGeneration(LmStudioService.SummaryTemplate.Glossary);
            menu.Items.Add(item2);

            var item3 = new MenuItem { Header = "✍️ Puntos Gramaticales Clave" };
            item3.Click += async (s, ev) => await ProcessSummaryGeneration(LmStudioService.SummaryTemplate.GrammarHighlight);
            menu.Items.Add(item3);

            var item4 = new MenuItem { Header = "📅 Plan de Estudio Personalizado" };
            item4.Click += async (s, ev) => await ProcessSummaryGeneration(LmStudioService.SummaryTemplate.StudyPlan);
            menu.Items.Add(item4);

            var item5 = new MenuItem { Header = "🎴 Tarjetas de Anki (Flashcards)" };
            item5.Click += async (s, ev) => await ProcessSummaryGeneration(LmStudioService.SummaryTemplate.Flashcards);
            menu.Items.Add(item5);

            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private async Task ProcessSummaryGeneration(LmStudioService.SummaryTemplate template)
        {
            try
            {
                string full = TranscriptionText.Text.Trim();
                if (string.IsNullOrWhiteSpace(full))
                {
                    StatusText.Text = "Sin transcripción para resumir.";
                    return;
                }

                StatusText.Text = "Generando resumen...";
                string summary = await _translator.GenerateSummaryAsync(full, template);
                
                string templateSuffix = template == LmStudioService.SummaryTemplate.Glossary ? "Glosario"
                                      : template == LmStudioService.SummaryTemplate.GrammarHighlight ? "Gramatica"
                                      : template == LmStudioService.SummaryTemplate.StudyPlan ? "PlanEstudio"
                                      : template == LmStudioService.SummaryTemplate.Flashcards ? "Flashcards"
                                      : "Resumen";

                string filename = $"{templateSuffix}_Clase_{DateTime.Now:yyyyMMdd_HHmm}.md";
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
                AppLogger.Error("ProcessSummaryGeneration failed", ex);
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
                _settleTimer.Stop();
                _fragmentBuffer.Clear();
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
                SwitchToPanel(HistoryPanel, BtnNavHistory);
                await RefreshSessionsList();
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
                SwitchToPanel(LiveViewPanel, BtnNavLive);
            }
            catch (Exception ex) { AppLogger.Error("NewSession_Click", ex); }
        }

        private void CloseSessions_Click(object sender, RoutedEventArgs e) =>
            SwitchToPanel(LiveViewPanel, BtnNavLive);

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

                // T3.4: Convertir segmentos en links interactivos de reproducción
                TranscriptionText.Text = "";
                TranscriptionText.Inlines.Clear();
                foreach (var entry in entries)
                {
                    var run = new Run(entry.OriginalText + " ");
                    var link = new Hyperlink(run)
                    {
                        TextDecorations = null,
                        Foreground = Brushes.White,
                        Cursor = Cursors.Hand
                    };

                    var startTime = entry.AudioStartTime;
                    link.Click += (s, ev) =>
                    {
                        if (startTime.HasValue && SessionAudioPlayback.Source != null)
                        {
                            SessionAudioPlayback.Position = TimeSpan.FromSeconds(startTime.Value);
                            SessionAudioPlayback.Play();
                            BtnPlayerPlay.Visibility = Visibility.Collapsed;
                            BtnPlayerPause.Visibility = Visibility.Visible;
                        }
                    };

                    TranscriptionText.Inlines.Add(link);
                }

                TranslationText.Text   = string.Join("\n\n",
                    entries.Where(en => !string.IsNullOrWhiteSpace(en.TranslatedText))
                           .Select(en => en.TranslatedText));
                TranslationStatus.Text = entries.Count > 0
                    ? entries.Last().Timestamp.ToString("HH:mm") : "";

                History.Clear();
                SwitchToPanel(LiveViewPanel, BtnNavLive);

                // T3.3: Cargar grabación de audio si existe
                bool hasAudio = false;
                if (!string.IsNullOrEmpty(loaded.RecordingPath) && File.Exists(loaded.RecordingPath))
                {
                    hasAudio = true;
                    SessionAudioPlayback.Source = new Uri(loaded.RecordingPath);
                }
                else
                {
                    // Fallback: buscar archivos en la carpeta de grabaciones que comiencen con la fecha de la sesión
                    var recDir = EnglishLearningAssistant.Core.Models.AppConfiguration.Instance.Storage.AudioRecordingsPath!;
                    if (Directory.Exists(recDir))
                    {
                        var searchPattern = $"session_{loaded.StartTime:yyyyMMdd}_*.wav";
                        var files = Directory.GetFiles(recDir, searchPattern);
                        if (files.Length > 0)
                        {
                            hasAudio = true;
                            loaded.RecordingPath = files[0];
                            SessionAudioPlayback.Source = new Uri(files[0]);
                        }
                    }
                }

                if (hasAudio)
                {
                    LiveActionPanel.Visibility = Visibility.Collapsed;
                    ReviewPlayerPanel.Visibility = Visibility.Visible;
                    PlayerTimeText.Text = "00:00 / 00:00";
                    PlayerSlider.Value = 0;
                    _playbackTimer.Start();
                }
                else
                {
                    LiveActionPanel.Visibility = Visibility.Visible;
                    ReviewPlayerPanel.Visibility = Visibility.Collapsed;
                    SessionAudioPlayback.Source = null;
                }
            }
            catch (Exception ex) { AppLogger.Error("SessionsList_MouseDoubleClick", ex); }
        }

        // ─── Audio playback control for session review (T3.3) ──────────────────

        private void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            if (_isSliderDragging || SessionAudioPlayback.NaturalDuration.HasTimeSpan == false) return;

            double current = SessionAudioPlayback.Position.TotalSeconds;
            double total = SessionAudioPlayback.NaturalDuration.TimeSpan.TotalSeconds;

            PlayerSlider.Value = total > 0 ? (current / total) : 0;
            PlayerTimeText.Text = $"{SessionAudioPlayback.Position:mm\\:ss} / {SessionAudioPlayback.NaturalDuration.TimeSpan:mm\\:ss}";
        }

        private void Playback_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (SessionAudioPlayback.NaturalDuration.HasTimeSpan)
            {
                var total = SessionAudioPlayback.NaturalDuration.TimeSpan;
                PlayerTimeText.Text = $"00:00 / {total:mm\\:ss}";
            }
        }

        private void Playback_MediaEnded(object sender, RoutedEventArgs e)
        {
            SessionAudioPlayback.Stop();
            BtnPlayerPlay.Visibility = Visibility.Visible;
            BtnPlayerPause.Visibility = Visibility.Collapsed;
            PlayerSlider.Value = 0;
            PlayerTimeText.Text = $"00:00 / {(SessionAudioPlayback.NaturalDuration.HasTimeSpan ? SessionAudioPlayback.NaturalDuration.TimeSpan.ToString("mm\\:ss") : "00:00")}";
        }

        private void PlayerPlay_Click(object sender, RoutedEventArgs e)
        {
            SessionAudioPlayback.Play();
            BtnPlayerPlay.Visibility = Visibility.Collapsed;
            BtnPlayerPause.Visibility = Visibility.Visible;
        }

        private void PlayerPause_Click(object sender, RoutedEventArgs e)
        {
            SessionAudioPlayback.Pause();
            BtnPlayerPlay.Visibility = Visibility.Visible;
            BtnPlayerPause.Visibility = Visibility.Collapsed;
        }

        private void PlayerSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
        }

        private void PlayerSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = false;
            if (SessionAudioPlayback.NaturalDuration.HasTimeSpan)
            {
                double total = SessionAudioPlayback.NaturalDuration.TimeSpan.TotalSeconds;
                SessionAudioPlayback.Position = TimeSpan.FromSeconds(PlayerSlider.Value * total);
            }
        }

        private void PlayerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSliderDragging && SessionAudioPlayback.NaturalDuration.HasTimeSpan)
            {
                double total = SessionAudioPlayback.NaturalDuration.TimeSpan.TotalSeconds;
                var current = TimeSpan.FromSeconds(e.NewValue * total);
                PlayerTimeText.Text = $"{current:mm\\:ss} / {SessionAudioPlayback.NaturalDuration.TimeSpan:mm\\:ss}";
            }
        }

        private void ExitReview_Click(object sender, RoutedEventArgs e)
        {
            SessionAudioPlayback.Stop();
            _playbackTimer.Stop();
            SessionAudioPlayback.Source = null;

            LiveActionPanel.Visibility = Visibility.Visible;
            ReviewPlayerPanel.Visibility = Visibility.Collapsed;
        }

        private async void ImportFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Seleccionar Archivo de Audio o Video",
                Filter = "Archivos Multimedia (*.mp4;*.mkv;*.avi;*.mp3;*.wav;*.m4a)|*.mp4;*.mkv;*.avi;*.mp3;*.wav;*.m4a|Videos (*.mp4;*.mkv;*.avi)|*.mp4;*.mkv;*.avi|Audio (*.mp3;*.wav;*.m4a)|*.mp3;*.wav;*.m4a"
            };

            if (dialog.ShowDialog() != true) return;

            string filePath = dialog.FileName;
            ImportProgressOverlay.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 0;
            ImportPercentText.Text = "0%";
            ImportStatusText.Text = "Iniciando importación...";

            var progress = new Progress<double>(pct =>
            {
                Dispatcher.Invoke(() =>
                {
                    ImportProgressBar.Value = pct * 100;
                    ImportPercentText.Text = $"{pct:P0}";
                    if (pct < 0.15) ImportStatusText.Text = "🎬 Extrayendo y remuestreando audio...";
                    else if (pct < 0.25) ImportStatusText.Text = "⚙️ Inicializando transcriptor Whisper...";
                    else if (pct < 0.95) ImportStatusText.Text = "✍️ Transcribiendo y traduciendo segmentos...";
                    else ImportStatusText.Text = "💾 Completando y guardando sesión...";
                });
            });

            try
            {
                var session = await Task.Run(async () => 
                    await _fileTranscriptionService.ImportFileAsync(filePath, progress)
                );

                MessageBox.Show($"¡Importación exitosa!\nSe ha creado la sesión: \"{session.Title}\"", "Importar Archivo", 
                    MessageBoxButton.OK, MessageBoxImage.Information);

                // Recargar listado de sesiones en UI
                await RefreshSessionsList();

                // Cargar automáticamente la nueva sesión para su revisión
                SessionsList.SelectedItem = SessionsList.Items.Cast<Session>()
                    .FirstOrDefault(s => s.Id == session.Id);
                
                // Simular doble clic para cargarla
                SessionsList_MouseDoubleClick(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) 
                { 
                    RoutedEvent = Control.MouseDoubleClickEvent 
                });

            }
            catch (Exception ex)
            {
                EnglishLearningAssistant.Core.AppLogger.Error("ImportFile_Click failed", ex);
                MessageBox.Show($"Ocurrió un error al importar el archivo:\n{ex.Message}", "Error de Importación", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ImportProgressOverlay.Visibility = Visibility.Collapsed;
            }
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
                        MessageBox.Show("Selecciona una sesión para exportar.", "Exportar Sesión",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (MessageBox.Show("No hay sesión seleccionada. ¿Exportar la sesión activa?", "Exportar Sesión",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    sessionToExport = _currentSession;
                }

                // Cargar todas las entradas asociadas
                var fullSession = await _sessionService!.LoadSessionAsync(sessionToExport.Id);

                if (fullSession == null) return;

                var dialog = new SaveFileDialog
                {
                    Title  = "Exportar Sesión",
                    Filter = "Markdown (*.md)|*.md|Texto Plano (*.txt)|*.txt|Subtítulos SRT (*.srt)|*.srt|Subtítulos VTT (*.vtt)|*.vtt|CSV de Anki (*.csv)|*.csv|JSON (*.json)|*.json",
                    FileName = $"Session_{fullSession.StartTime:yyyyMMdd_HHmm}.md"
                };

                if (dialog.ShowDialog() != true) return;

                string filePath = dialog.FileName;
                string ext = Path.GetExtension(filePath).ToLower();
                string content = "";

                if (ext == ".md")
                {
                    content = await _sessionService.ExportToMarkdownAsync(fullSession.Id);
                }
                else if (ext == ".txt")
                {
                    var lines = new System.Collections.Generic.List<string>
                    {
                        $"=== SESIÓN: {fullSession.Title} ===",
                        $"Inicio: {fullSession.StartTime}",
                        $"Fin: {fullSession.EndTime}",
                        $"Resumen: {fullSession.Summary}",
                        ""
                    };
                    foreach (var entry in fullSession.Entries.OrderBy(en => en.Timestamp))
                    {
                        lines.Add($"[{entry.Timestamp:HH:mm:ss}]");
                        lines.Add($"  EN: {entry.OriginalText}");
                        if (!string.IsNullOrWhiteSpace(entry.TranslatedText))
                        {
                            lines.Add($"  ES: {entry.TranslatedText}");
                        }
                        lines.Add("");
                    }
                    content = string.Join("\n", lines);
                }
                else if (ext == ".srt")
                {
                    content = await _sessionService.ExportToSrtAsync(fullSession.Id);
                }
                else if (ext == ".vtt")
                {
                    content = await _sessionService.ExportToVttAsync(fullSession.Id);
                }
                else if (ext == ".csv")
                {
                    // T9.3: CSV para Anki: Front;Back
                    var lines = new System.Collections.Generic.List<string> { "Front;Back" };
                    foreach (var entry in fullSession.Entries.OrderBy(en => en.Timestamp))
                    {
                        if (string.IsNullOrWhiteSpace(entry.OriginalText)) continue;
                        string front = entry.OriginalText.Replace("\"", "\"\"");
                        string back = entry.TranslatedText.Replace("\"", "\"\"");
                        lines.Add($"\"{front}\";\"{back}\"");
                    }
                    content = string.Join("\n", lines);
                }
                else if (ext == ".json")
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    content = JsonSerializer.Serialize(fullSession, options);
                }

                await File.WriteAllTextAsync(filePath, content);
                MessageBox.Show("Exportación completada con éxito.", "Exportar Sesión", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Error("ExportSession_Click", ex);
                MessageBox.Show($"Error al exportar: {ex.Message}", "Exportar Sesión", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Vocabulary_Click(object sender, RoutedEventArgs e)
        {
            SwitchToPanel(VocabularyPanel, BtnNavVocab);
            await LoadVocabulary();
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

        // ======================================================================
        // SIDEBAR NAVIGATION & THEME INTEGRATION (Fase 5 - T5.2, T5.5)
        // ======================================================================

        private void SwitchToPanel(FrameworkElement targetPanel, Button activeNavBtn)
        {
            // Ocultar todos los paneles principales
            LiveViewPanel.Visibility = Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Collapsed;
            VocabularyPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;

            // Mostrar el seleccionado
            targetPanel.Visibility = Visibility.Visible;

            // Actualizar estilos de botones
            BtnNavLive.Style = (Style)FindResource("SidebarBtn");
            BtnNavHistory.Style = (Style)FindResource("SidebarBtn");
            BtnNavVocab.Style = (Style)FindResource("SidebarBtn");
            BtnNavSettings.Style = (Style)FindResource("SidebarBtn");

            activeNavBtn.Style = (Style)FindResource("SidebarBtnActive");
        }

        private async void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            if (btn == BtnNavLive)
            {
                SwitchToPanel(LiveViewPanel, BtnNavLive);
            }
            else if (btn == BtnNavHistory)
            {
                SwitchToPanel(HistoryPanel, BtnNavHistory);
                await RefreshSessionsList();
            }
            else if (btn == BtnNavVocab)
            {
                SwitchToPanel(VocabularyPanel, BtnNavVocab);
                await LoadVocabulary();
            }
            else if (btn == BtnNavSettings)
            {
                SwitchToPanel(SettingsPanel, BtnNavSettings);
                await PopulateLmStudioModelsAsync();
                LoadMicrophones();
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (ThemeDarkRadio == null || ThemeLightRadio == null) return;
            
            if (ThemeDarkRadio.IsChecked == true)
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            }
            else
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            }
        }

        // ======================================================================
        // INTEGRATED VOCABULARY PANEL (Fase 5 - T5.3)
        // ======================================================================

        private async Task LoadVocabulary()
        {
            try
            {
                _allVocabWords = await _vocabularyService.GetAllVocabularyAsync();
                FilterVocabList(VocabSearchBox.Text);
                SidebarCefrText.Text = $"Nivel: {EnglishLearningAssistant.Core.Models.AppConfiguration.Instance.CefrLevel}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoadVocabulary failed", ex);
            }
        }

        private void FilterVocabList(string query)
        {
            if (_allVocabWords == null) return;

            if (string.IsNullOrWhiteSpace(query) || query == "Buscar...")
            {
                VocabGrid.ItemsSource = _allVocabWords;
            }
            else
            {
                var filtered = _allVocabWords.Where(w =>
                    w.Word.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    w.SpanishTranslation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (w.Definition != null && w.Definition.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
                VocabGrid.ItemsSource = filtered;
            }
        }

        private void VocabSearchBox_KeyUp(object sender, KeyEventArgs e)
        {
            FilterVocabList(VocabSearchBox.Text);
        }

        private void VocabSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (VocabSearchBox.Text == "Buscar...")
            {
                VocabSearchBox.Text = "";
                VocabSearchBox.Foreground = Brushes.White;
            }
        }

        private void VocabSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VocabSearchBox.Text))
            {
                VocabSearchBox.Text = "Buscar...";
                VocabSearchBox.Foreground = Brushes.Gray;
            }
        }

        private void AddWord_Click(object sender, RoutedEventArgs e)
        {
            AddWordForm.Visibility = Visibility.Visible;
            NewWordBox.Clear();
            NewTransBox.Clear();
            NewDefBox.Clear();
            NewWordBox.Focus();
        }

        private void CancelAddWord_Click(object sender, RoutedEventArgs e)
        {
            AddWordForm.Visibility = Visibility.Collapsed;
        }

        private async void SaveNewWord_Click(object sender, RoutedEventArgs e)
        {
            string word = NewWordBox.Text.Trim();
            string trans = NewTransBox.Text.Trim();
            string def   = NewDefBox.Text.Trim();

            if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(trans))
            {
                MessageBox.Show("La palabra y su traducción son campos requeridos.", "Agregar palabra", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                await _vocabularyService.AddOrUpdateWordAsync(word, def, trans);
                AddWordForm.Visibility = Visibility.Collapsed;
                await LoadVocabulary();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al guardar palabra: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExtractFromText_Click(object sender, RoutedEventArgs e)
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length < 20)
            {
                MessageBox.Show("Copia algún texto (mín. 20 caracteres) en inglés al portapapeles antes de hacer clic.", "Analizar portapapeles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var suggestions = await _vocabularyService.ExtractPotentialVocabularyAsync(text);
                if (suggestions.Count > 0)
                {
                    string msg = "Palabras y definiciones sugeridas:\n\n" + string.Join("\n", suggestions) + "\n\n¿Deseas agregarlas a tu libro de vocabulario?";
                    if (MessageBox.Show(msg, "Vocabulario Sugerido", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        foreach (var s in suggestions)
                        {
                            var parts = s.Split('|');
                            if (parts.Length >= 3)
                            {
                                await _vocabularyService.AddOrUpdateWordAsync(parts[0], parts[1], parts[2], text[..Math.Min(text.Length, 60)] + "...");
                            }
                        }
                        await LoadVocabulary();
                    }
                }
                else
                {
                    MessageBox.Show("No se detectó vocabulario nuevo para tu nivel en el portapapeles.", "Análisis completado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al analizar texto: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteWord_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                if (MessageBox.Show("¿Estás seguro de que deseas eliminar esta palabra?", "Eliminar Palabra", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await _vocabularyService.DeleteWordAsync(id);
                    await LoadVocabulary();
                }
            }
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
