using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using EnglishLearningAssistant.Infrastructure.Transcription;
using WindowsLiveCaptionsReader.Services;
using WindowsLiveCaptionsReader.Models;

namespace EnglishLearningAssistant.Application.Transcription;

/// <summary>
/// Servicio para transcribir y traducir archivos de audio y video en segundo plano (Fase 4).
///   - T4.1: Extracción de audio nativo mediante Windows Media Foundation (NAudio MediaFoundationReader).
///   - T4.2: Pipeline de transcripción off-line en segundo plano con reporte de progreso.
///   - T4.4: Auto-guardado de transcripción en una nueva sesión histórica persistida.
/// </summary>
public sealed class FileTranscriptionService
{
    private readonly WhisperProvider _whisperProvider;
    private readonly ITranslationProvider _translationProvider;
    private readonly SessionService _sessionService;
    private readonly ILogger<FileTranscriptionService> _logger;
    private readonly AppConfiguration _config;

    private static readonly WaveFormat WhisperFormat = new(16000, 16, 1);

    public FileTranscriptionService(
        WhisperProvider whisperProvider,
        ITranslationProvider translationProvider,
        SessionService sessionService,
        ILogger<FileTranscriptionService> logger,
        AppConfiguration config)
    {
        _whisperProvider = whisperProvider ?? throw new ArgumentNullException(nameof(whisperProvider));
        _translationProvider = translationProvider ?? throw new ArgumentNullException(nameof(translationProvider));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Procesa un archivo multimedia (audio o video) en segundo plano, transcribiendo
    /// y traduciendo su contenido, y lo almacena en una nueva sesión.
    /// </summary>
    /// <param name="filePath">Ruta completa al archivo MP4, MKV, MP3, WAV, etc.</param>
    /// <param name="progress">Callback para notificar progreso porcentual (0.0 a 1.0).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>La nueva sesión creada con todas las entradas de transcripción y traducción.</returns>
    public async Task<Session> ImportFileAsync(
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("La ruta del archivo no puede estar vacía.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Archivo multimedia no encontrado.", filePath);

        _logger.LogInformation("Iniciando importación del archivo: {Path}", filePath);
        progress?.Report(0.05);

        // 1. Extraer y remuestrear audio (T4.1)
        var tempWavPath = Path.Combine(Path.GetTempPath(), $"ela_import_{Guid.NewGuid():N}.wav");
        double fileDurationSeconds = 0;

        try
        {
            _logger.LogInformation("Extrayendo pista de audio con MediaFoundationReader...");
            await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(filePath);
                fileDurationSeconds = reader.TotalTime.TotalSeconds;

                var mono = reader.ToSampleProvider().ToMono();
                var resampled = new WdlResamplingSampleProvider(mono, 16000);
                WaveFileWriter.CreateWaveFile16(tempWavPath, resampled);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al extraer audio de: {Path}", filePath);
            if (File.Exists(tempWavPath)) File.Delete(tempWavPath);
            throw new InvalidOperationException($"No se pudo leer el archivo de audio/video: {ex.Message}", ex);
        }

        progress?.Report(0.15);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Crear sesión en base de datos (T4.4)
        var sessionName = $"Importación: {Path.GetFileName(filePath)}";
        var session = await _sessionService.CreateSessionAsync(sessionName);
        session.RecordingPath = tempWavPath; // Asociar el audio extraído como la grabación de la sesión
        await _sessionService.SaveSessionAsync(session);

        _logger.LogInformation("Nueva sesión creada. ID: {Id}, Guardando audio en: {Wav}", session.Id, tempWavPath);

        // 3. Inicializar Whisper si no está cargado
        progress?.Report(0.20);
        await _whisperProvider.InitializeAsync(cancellationToken);
        progress?.Report(0.25);

        // 4. Transcribir archivo (T4.2)
        var request = new TranscriptionRequest { FilePath = tempWavPath };
        int processedSegmentsCount = 0;

        try
        {
            var segmentsList = new System.Collections.Generic.List<TranscriptSegment>();

            await foreach (var segment in _whisperProvider.StartAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                segmentsList.Add(segment);
            }

            _logger.LogInformation("Transcripción terminada con {Count} segmentos. Iniciando traducción...", segmentsList.Count);

            // 5. Traducir y guardar cada segmento secuencialmente o en lotes
            for (int i = 0; i < segmentsList.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var segment = segmentsList[i];
                TranslationResult? translation = null;

                // Traducir solo si no es vacío y el traductor está online
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    translation = await _translationProvider.TranslateAsync(segment.Text, "en", "es", cancellationToken);
                }

                // Crear entrada de la base de datos
                var entry = new TranscriptionEntry
                {
                    SessionId = session.Id,
                    OriginalText = segment.Text,
                    TranslatedText = translation?.TranslatedText ?? string.Empty,
                    Timestamp = session.StartTime.Add(segment.StartTime),
                    Source = EntrySource.Recording,
                    ConfidenceScore = 1.0f,
                    AudioStartTime = segment.StartTime.TotalSeconds,
                    AudioEndTime = segment.EndTime.TotalSeconds
                };

                session.Entries.Add(entry);
                await _sessionService.SaveEntryAsync(entry);

                processedSegmentsCount++;

                // Reportar progreso (de 0.25 a 0.95)
                double ratio = (double)processedSegmentsCount / segmentsList.Count;
                progress?.Report(0.25 + (ratio * 0.70));
            }

            // 6. Completar y generar resumen básico de la sesión
            session.EndTime = DateTime.Now;
            session.Summary = $"Transcripción importada del archivo {Path.GetFileName(filePath)} el {DateTime.Now:yyyy-MM-dd}. Contiene {processedSegmentsCount} oraciones.";
            await _sessionService.SaveSessionAsync(session);

            progress?.Report(1.0);
            _logger.LogInformation("Importación finalizada con éxito para sesión: {Title}", session.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante el procesamiento del archivo importado");
            // Marcar sesión como incompleta o guardarla degradada
            session.Summary = $"Error durante la importación: {ex.Message}";
            await _sessionService.SaveSessionAsync(session);
            throw;
        }

        return session;
    }
}
