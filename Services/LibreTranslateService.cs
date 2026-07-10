using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLiveCaptionsReader.Services
{
    /// <summary>
    /// Cliente para LibreTranslate corriendo localmente en localhost:5000.
    /// Instalar: pip install libretranslate
    /// La app lo arranca automáticamente si no está corriendo.
    /// Latencia típica EN→ES: 50–200ms por oración (sin streaming).
    /// </summary>
    public class LibreTranslateService
    {
        private readonly HttpClient _http;
        private const string BaseUrl = "http://localhost:5000";
        private Process? _serverProcess;

        public LibreTranslateService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        }

        public async Task<bool> IsRunningAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var resp = await _http.GetAsync(BaseUrl + "/languages", cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>
        /// Arranca LibreTranslate en background si no está corriendo.
        /// Espera hasta <paramref name="timeoutSeconds"/> segundos a que quede disponible.
        /// Devuelve true si quedó listo, false si no se pudo levantar.
        /// </summary>
        public async Task<bool> EnsureRunningAsync(
            Action<string>? onStatusUpdate = null,
            int timeoutSeconds = 60,
            CancellationToken token = default)
        {
            if (await IsRunningAsync()) return true;

            onStatusUpdate?.Invoke("Iniciando LibreTranslate...");

            // Buscar el ejecutable libretranslate en el PATH de Python
            string exe = FindLibreTranslateExe();
            if (exe == null)
            {
                onStatusUpdate?.Invoke("LibreTranslate no instalado (pip install libretranslate)");
                return false;
            }

            try
            {
                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = exe,
                        Arguments              = "--load-only en,es --port 5000",
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError  = false,
                    }
                };
                _serverProcess.Start();
            }
            catch (Exception ex)
            {
                onStatusUpdate?.Invoke($"No se pudo iniciar LibreTranslate: {ex.Message}");
                return false;
            }

            // Esperar a que el servidor acepte conexiones (poll cada 2 segundos)
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
                if (await IsRunningAsync()) return true;
                onStatusUpdate?.Invoke("Cargando modelos EN/ES...");
            }

            onStatusUpdate?.Invoke("LibreTranslate tardó demasiado en iniciar");
            return false;
        }

        /// <summary>
        /// Detiene el proceso de LibreTranslate si fue iniciado por esta instancia.
        /// </summary>
        public void StopServer()
        {
            try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
            _serverProcess = null;
        }

        /// <summary>
        /// Traduce <paramref name="text"/> de <paramref name="source"/> a <paramref name="target"/>.
        /// Devuelve cadena vacía si LibreTranslate no está disponible o hay un error.
        /// </summary>
        public async Task<string> TranslateAsync(
            string text,
            string source = "en",
            string target = "es",
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var payload = JsonSerializer.Serialize(new
            {
                q      = text,
                source = source,
                target = target,
                format = "text"
            });

            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.PostAsync(BaseUrl + "/translate", content, token);
                if (!resp.IsSuccessStatusCode) return "";

                var json = await resp.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("translatedText", out var t))
                    return t.GetString()?.Trim() ?? "";

                return "";
            }
            catch (OperationCanceledException) { return ""; }
            catch { return ""; }
        }

        // Busca el ejecutable libretranslate en las ubicaciones típicas de pip en Windows
        private static string? FindLibreTranslateExe()
        {
            // 1. En el PATH del sistema
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var candidate = System.IO.Path.Combine(dir.Trim(), "libretranslate.exe");
                    if (System.IO.File.Exists(candidate)) return candidate;
                    // Sin extensión (por si acaso)
                    candidate = System.IO.Path.Combine(dir.Trim(), "libretranslate");
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            // 2. Ubicaciones típicas de pip en Windows
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData      = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates   = new[]
            {
                System.IO.Path.Combine(localAppData, "Programs", "Python", "Python314", "Scripts", "libretranslate.exe"),
                System.IO.Path.Combine(localAppData, "Programs", "Python", "Python313", "Scripts", "libretranslate.exe"),
                System.IO.Path.Combine(localAppData, "Programs", "Python", "Python312", "Scripts", "libretranslate.exe"),
                System.IO.Path.Combine(localAppData, "Programs", "Python", "Python311", "Scripts", "libretranslate.exe"),
                System.IO.Path.Combine(appData, "Python", "Python314", "Scripts", "libretranslate.exe"),
                System.IO.Path.Combine(appData, "Python", "Scripts", "libretranslate.exe"),
            };

            foreach (var path in candidates)
                if (System.IO.File.Exists(path)) return path;

            return null;
        }
    }
}
