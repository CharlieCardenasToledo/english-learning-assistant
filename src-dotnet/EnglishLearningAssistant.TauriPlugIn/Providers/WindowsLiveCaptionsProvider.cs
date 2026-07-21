using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Automation;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using WindowsLiveCaptionsReader.Apis;

namespace EnglishLearningAssistant.TauriPlugIn.Providers;

public sealed class WindowsLiveCaptionsProvider : ITranscriptionProvider
{
    private readonly ILogger<WindowsLiveCaptionsProvider> _logger;
    private readonly Channel<TranscriptSegment> _channel;
    private CancellationTokenSource? _cts;
    private long _sequenceId;

    private const string LiveCaptionsProcess = "LiveCaptions";
    private const string LiveCaptionsWindowClass = "LiveCaptionsDesktopWindow";

    public string Name => "Windows Live Captions";
    public bool SupportsPartialResults => true;

    public WindowsLiveCaptionsProvider(ILogger<WindowsLiveCaptionsProvider> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<TranscriptSegment>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("WindowsLiveCaptionsProvider initialized.");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunCaptionLoop(_cts.Token), _cts.Token);

        await foreach (var segment in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return segment;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    // ─── Caption loop ─────────────────────────────────────────────────────────

    private async Task RunCaptionLoop(CancellationToken token)
    {
        try
        {
            var (window, _) = await LaunchAndFindLiveCaptionsAsync(token);
            if (window is null)
            {
                _logger.LogError("Could not find Live Captions window.");
                return;
            }

            // Fix position first, then hide — same order as the working WPF CaptionReader.
            FixWindowPosition(window);
            HideWindow(window);

            _logger.LogInformation("Live Captions window ready, entering caption loop.");
            var sessionStart = DateTimeOffset.UtcNow;
            var prevText = string.Empty;
            var pipeline = new WindowsLiveCaptionsReader.Services.CaptionPipeline();
            AutomationElement? textBlock = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Lazy find after hiding — mirrors LiveCaptionsHandler.GetCaptions pattern.
                    if (textBlock is null)
                    {
                        textBlock = window.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));

                        if (textBlock is null)
                        {
                            await Task.Delay(100, token);
                            continue;
                        }
                        _logger.LogInformation("CaptionsTextBlock found lazily.");
                    }

                    string raw;
                    try { raw = textBlock.Current.Name ?? string.Empty; }
                    catch (ElementNotAvailableException) { textBlock = null; await Task.Delay(500, token); continue; }

                    if (raw == prevText)
                    {
                        await Task.Delay(50, token);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(raw))
                        _logger.LogDebug("Caption raw: {Text}", raw.Length > 80 ? raw[..80] + "…" : raw);

                    prevText = raw;
                    var committed = pipeline.Feed(raw);

                    if (!string.IsNullOrWhiteSpace(pipeline.Pending))
                    {
                        var now = DateTimeOffset.UtcNow;
                        await _channel.Writer.WriteAsync(new TranscriptSegment
                        {
                            SequenceId = Interlocked.Increment(ref _sequenceId),
                            Text       = pipeline.Pending,
                            IsPartial  = true,
                            Source     = Name,
                            StartTime  = now - sessionStart,
                            EndTime    = now - sessionStart,
                        }, token);
                    }

                    if (!string.IsNullOrWhiteSpace(committed))
                    {
                        _logger.LogInformation("Caption committed: {Text}", committed.Length > 80 ? committed[..80] + "…" : committed);
                        var now = DateTimeOffset.UtcNow;
                        await _channel.Writer.WriteAsync(new TranscriptSegment
                        {
                            SequenceId = Interlocked.Increment(ref _sequenceId),
                            Text       = committed,
                            IsPartial  = false,
                            Source     = Name,
                            StartTime  = now - sessionStart,
                            EndTime    = now - sessionStart,
                        }, token);
                    }

                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading Live Captions.");
                    await Task.Delay(500, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Live Captions loop.");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    // ─── Window helpers ───────────────────────────────────────────────────────

    private async Task<(AutomationElement? Window, int ProcessId)> LaunchAndFindLiveCaptionsAsync(CancellationToken token)
    {
        foreach (var p in Process.GetProcessesByName(LiveCaptionsProcess))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }

        var process = Process.Start(LiveCaptionsProcess)!;
        _logger.LogInformation("Launched LiveCaptions PID={Pid}", process.Id);

        for (int i = 0; i < 60 && !token.IsCancellationRequested; i++)
        {
            await Task.Delay(500, token);

            // If the launcher process exits, the real LiveCaptions process may have a new PID.
            if (process.HasExited)
            {
                _logger.LogDebug("LiveCaptions launcher exited, restarting...");
                process = Process.Start(LiveCaptionsProcess)!;
            }

            var window = FindWindowByProcessId(process.Id);
            if (window is not null && window.Current.ClassName == LiveCaptionsWindowClass)
            {
                _logger.LogInformation("Found LiveCaptions window via PID={Pid}", process.Id);
                return (window, process.Id);
            }
        }

        return (null, -1);
    }

    private static AutomationElement? FindWindowByProcessId(int processId)
    {
        try
        {
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        }
        catch { return null; }
    }

    private async Task<AutomationElement?> FindCaptionTextBlockWithRetryAsync(
        AutomationElement window, CancellationToken token, int maxAttempts = 30)
    {
        for (int i = 0; i < maxAttempts && !token.IsCancellationRequested; i++)
        {
            try
            {
                var el = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
                if (el is not null)
                {
                    _logger.LogInformation("Found CaptionsTextBlock on attempt {Attempt}", i + 1);
                    return el;
                }
            }
            catch { }
            await Task.Delay(300, token);
        }
        return null;
    }

    private void FixWindowPosition(AutomationElement window)
    {
        try
        {
            var hWnd = new nint((long)window.Current.NativeWindowHandle);
            if (WindowsAPI.GetWindowRect(hWnd, out var rect))
            {
                int width  = rect.Right  - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (rect.Left < 0 || rect.Top < 0 || width < 100 || height < 100)
                {
                    _logger.LogDebug("LiveCaptions window off-screen or too small, moving to valid position.");
                    WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fix Live Captions window position.");
        }
    }

    private static void HideWindow(AutomationElement window)
    {
        try
        {
            var hWnd    = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
        }
        catch { }
    }
}
