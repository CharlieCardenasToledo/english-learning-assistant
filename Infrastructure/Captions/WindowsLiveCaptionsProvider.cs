using System.Runtime.CompilerServices;
using System.Windows.Automation;
using EnglishLearningAssistant.Core.Abstractions;
using EnglishLearningAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using WindowsLiveCaptionsReader.Utils;

namespace EnglishLearningAssistant.Infrastructure.Captions;

/// <summary>
/// Adaptador que envuelve el <c>CaptionReader</c> existente como <see cref="ITranscriptionProvider"/>.
///
/// Windows Live Captions emite texto a través de UIAutomation (AutomationElement "CaptionsTextBlock").
/// La lectura es por polling — no hay callbacks push — por lo que se muestrea cada 200ms.
///
/// Limitaciones:
///   - No produce timestamps (StartTime/EndTime = 0); se completará en T3.1.
///   - Requiere que Windows Live Captions esté habilitado en el sistema.
///   - Solo disponible en Windows 11 22H2+.
///
/// (T1.2)
/// </summary>
public sealed class WindowsLiveCaptionsProvider : ITranscriptionProvider
{
    private readonly ILogger<WindowsLiveCaptionsProvider> _logger;

    // UIAutomation element — asignado en InitializeAsync
    private AutomationElement? _captionsWindow;
    private bool _initialized;
    private long _sequenceId;

    /// <summary>Intervalo de polling de la ventana de Live Captions.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public string Name => "Windows Live Captions";
    public bool SupportsPartialResults => true;

    public WindowsLiveCaptionsProvider(ILogger<WindowsLiveCaptionsProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Lanza Live Captions si no está corriendo y localiza el AutomationElement de la ventana.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;

        _logger.LogInformation("Inicializando Windows Live Captions...");

        // Lanzar / localizar la ventana de Live Captions (lógica existente en LiveCaptionsHandler)
        _captionsWindow = LiveCaptionsHandler.LaunchLiveCaptions();
        LiveCaptionsHandler.HideLiveCaptions(_captionsWindow);

        _initialized = true;
        _logger.LogInformation("Windows Live Captions inicializado correctamente");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Produce segmentos de transcripción mediante polling de la ventana de Live Captions.
    /// El stream se completa cuando se cancela el <paramref name="cancellationToken"/>.
    /// </summary>
    public async IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_initialized || _captionsWindow is null)
            throw new InvalidOperationException("Llama a InitializeAsync antes de StartAsync.");

        _logger.LogInformation("Iniciando lectura de Live Captions (polling {Interval}ms)", PollInterval.TotalMilliseconds);

        string lastText = string.Empty;
        var sessionStart = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            string currentText = string.Empty;

            try
            {
                currentText = LiveCaptionsHandler.GetCaptions(_captionsWindow);
            }
            catch (ElementNotAvailableException)
            {
                // Ventana cerrada inesperadamente — intentar relocalizar
                _logger.LogWarning("AutomationElement perdido. Intentando relocalizar Live Captions...");
                try
                {
                    _captionsWindow = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.HideLiveCaptions(_captionsWindow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo relocalizar Live Captions");
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Error leyendo Live Captions");
            }

            if (!string.IsNullOrWhiteSpace(currentText) && currentText != lastText)
            {
                var elapsed = DateTimeOffset.UtcNow - sessionStart;

                yield return new TranscriptSegment
                {
                    SequenceId = Interlocked.Increment(ref _sequenceId),
                    Text = currentText,
                    StartTime = elapsed,        // Aproximado — T3.1 mejorará esto
                    EndTime = elapsed,
                    IsPartial = true,           // Live Captions siempre es parcial
                    Source = Name,
                    Confidence = null           // UIAutomation no provee confianza
                };

                lastText = currentText;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Stream de Live Captions terminado");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deteniendo Windows Live Captions provider");
        // El stop real ocurre por cancelación del token en StartAsync
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_captionsWindow is not null)
        {
            try { LiveCaptionsHandler.KillLiveCaptions(_captionsWindow); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error al cerrar Live Captions"); }
            _captionsWindow = null;
        }
        _initialized = false;
        return Task.CompletedTask;
    }
}
