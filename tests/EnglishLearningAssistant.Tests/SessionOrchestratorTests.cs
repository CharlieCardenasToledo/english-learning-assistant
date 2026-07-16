using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Application.Sessions;
using Xunit;

namespace EnglishLearningAssistant.Tests;

public sealed class MockTranscriptionProvider : ITranscriptionProvider
{
    public string Name => "MockProvider";
    public bool SupportsPartialResults => true;

    private readonly List<TranscriptSegment> _segmentsToEmit = new();
    private readonly TaskCompletionSource _startTcs = new();

    public void AddSegment(string text, bool isPartial)
    {
        _segmentsToEmit.Add(new TranscriptSegment
        {
            Text = text,
            IsPartial = isPartial,
            Source = Name,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.Zero
        });
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _startTcs.TrySetResult();

        foreach (var segment in _segmentsToEmit)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return segment;
        }

        // Keep the stream open until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // cleanly exit
        }
    }

    public Task WaitForStartAsync() => _startTcs.Task;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class MockTranslationProvider : ITranslationProvider
{
    public string Name => "MockTranslator";

    public readonly List<string> TranslatedTexts = new();

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        lock (TranslatedTexts)
        {
            TranslatedTexts.Add(text);
        }

        return Task.FromResult(new TranslationResult
        {
            OriginalText = text,
            TranslatedText = $"[ES] {text}",
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            ProviderName = Name
        });
    }
}

public class SessionOrchestratorTests
{
    [Fact]
    public async Task Orchestrator_ShouldFlushFragments_WhenSentenceEndsWithPunctuation()
    {
        // Arrange
        var transcriptionMock = new MockTranscriptionProvider();
        transcriptionMock.AddSegment("Hello", isPartial: false);
        transcriptionMock.AddSegment("world!", isPartial: false); // Ends with !

        var translationMock = new MockTranslationProvider();
        var logger = NullLogger<SessionOrchestrator>.Instance;

        var options = new OrchestratorOptions
        {
            MinWordsForAutoTranslate = 1, // trigger always
            AutoTranslateTimeout = TimeSpan.FromSeconds(5)
        };

        var orchestrator = new SessionOrchestrator(
            transcriptionMock,
            translationMock,
            logger,
            options);

        var segmentReadyTcs = new TaskCompletionSource<SegmentReadyEvent>();
        orchestrator.SegmentReady += (s, e) => segmentReadyTcs.TrySetResult(e);

        // Act
        var request = new TranscriptionRequest();
        await orchestrator.StartAsync(request);
        await transcriptionMock.WaitForStartAsync();

        // Wait for segment to be translated (signaling a flush occurred)
        var resultEvent = await segmentReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(resultEvent);
        Assert.Equal("Hello world!", resultEvent.Segment.Text);
        Assert.Equal("[ES] Hello world!", resultEvent.Translation?.TranslatedText);

        await orchestrator.StopAsync();
    }
}
