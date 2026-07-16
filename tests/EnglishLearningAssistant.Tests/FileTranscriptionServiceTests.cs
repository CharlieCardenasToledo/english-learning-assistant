using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Application.Transcription;
using EnglishLearningAssistant.Infrastructure.Transcription;
using WindowsLiveCaptionsReader.Services;
using Xunit;

namespace EnglishLearningAssistant.Tests;

public class FileTranscriptionServiceTests
{
    [Fact]
    public async Task ImportFileAsync_ShouldThrowFileNotFoundException_WhenFileDoesNotExist()
    {
        // Arrange
        var config = AppConfiguration.Instance;
        var whisperLogger = NullLogger<WhisperProvider>.Instance;
        var whisperProvider = new WhisperProvider(whisperLogger, config);
        
        var translatorMock = new MockTranslationProvider();
        
        var sessionService = new SessionService();
        var serviceLogger = NullLogger<FileTranscriptionService>.Instance;
        
        var service = new FileTranscriptionService(
            whisperProvider,
            translatorMock,
            sessionService,
            serviceLogger,
            config);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            service.ImportFileAsync("non_existent_file.mp4")
        );
    }
}
