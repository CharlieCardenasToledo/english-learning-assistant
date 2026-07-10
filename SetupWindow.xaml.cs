using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using WindowsLiveCaptionsReader.Services;

namespace WindowsLiveCaptionsReader
{
    public partial class SetupWindow : FluentWindow
    {
        private readonly WhisperService _whisper = new();
        private readonly LmStudioService  _lmStudio  = new();

        private static readonly string AppDataDir  = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant");
        private static readonly string SettingsPath = System.IO.Path.Combine(AppDataDir, "settings.json");

        // Saved settings (populated from file, written on Start)
        private string _whisperModelFile = "ggml-small.en.bin";
        private string _lmStudioModelName  = "google/gemma-4-e4b";
        private string _userName         = "Charlie";
        private string _englishLevel     = "B1";

        private bool _whisperReady = false;

        public SetupWindow()
        {
            InitializeComponent();
            Loaded += SetupWindow_Loaded;
        }

        private async void SetupWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WindowBackdropType = WindowBackdropType.Mica;

            Directory.CreateDirectory(AppDataDir);
            LoadSettings();

            // Populate Whisper model selector
            WhisperModelCombo.ItemsSource        = WhisperService.AvailableModels;
            WhisperModelCombo.DisplayMemberPath  = "DisplayName";
            WhisperModelCombo.SelectedItem       = Array.Find(
                WhisperService.AvailableModels, m => m.FileName == _whisperModelFile)
                ?? WhisperService.AvailableModels[2]; // default SmallEn

            // Evaluate if the currently selected model is already downloaded
            EvaluateWhisperState();

            // Check LM Studio in parallel
            await CheckLmStudioAsync();
        }

        // ── Whisper ────────────────────────────────────────────────────────

        private void WhisperModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            EvaluateWhisperState();
        }

        private void EvaluateWhisperState()
        {
            if (WhisperModelCombo.SelectedItem is not WhisperService.ModelOption option) return;
            _whisperModelFile = option.FileName;

            string modelPath = GetModelPath(option.FileName);
            bool valid = File.Exists(modelPath) && new FileInfo(modelPath).Length >= option.MinBytes;

            if (valid)
            {
                SetWhisperState("ready", $"Modelo listo: {option.FileName}");
                _whisperReady = true;
            }
            else
            {
                SetWhisperState("download");
                _whisperReady = false;
            }

            UpdateStartButton();
        }

        private async void DownloadWhisper_Click(object sender, RoutedEventArgs e)
        {
            if (WhisperModelCombo.SelectedItem is not WhisperService.ModelOption option) return;

            WhisperDownloadBtn.IsEnabled = false;
            SetWhisperState("progress", "Iniciando descarga...");

            _whisper.ChangeModel(option);
            _whisper.DownloadProgress += (_, msg) => Dispatcher.Invoke(() =>
            {
                WhisperProgressText.Text = msg;

                if (msg == "Cargando modelo en memoria...")
                {
                    // Download done, now loading into RAM — indeterminate phase
                    WhisperProgressBar.IsIndeterminate = true;
                    return;
                }

                // Parse "Descargando xxx... 47%" → extract 47
                WhisperProgressBar.IsIndeterminate = false;
                int pctIdx = msg.LastIndexOf(' ');
                if (pctIdx >= 0 && msg.EndsWith('%') &&
                    int.TryParse(msg[(pctIdx + 1)..^1], out int pct))
                {
                    WhisperProgressBar.Value = Math.Clamp(pct, 0, 100);
                }
            });

            try
            {
                await _whisper.InitializeAsync();
                WhisperProgressBar.IsIndeterminate = false;
                WhisperProgressBar.Value = 100;
                SetWhisperState("ready", $"Listo: {option.FileName}");
                _whisperReady = true;
                UpdateStartButton();
            }
            catch (Exception ex)
            {
                WhisperProgressBar.IsIndeterminate = false;
                SetWhisperState("error", $"Error: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
                WhisperDownloadBtn.IsEnabled = true;
            }
        }

        private void SetWhisperState(string state, string message = "")
        {
            WhisperReadyPanel.Visibility    = Visibility.Collapsed;
            WhisperProgressPanel.Visibility = Visibility.Collapsed;
            WhisperErrorPanel.Visibility    = Visibility.Collapsed;
            WhisperDownloadBtn.Visibility   = Visibility.Collapsed;

            switch (state)
            {
                case "ready":
                    WhisperReadyText.Text         = message;
                    WhisperReadyPanel.Visibility  = Visibility.Visible;
                    StartHintText.Visibility      = Visibility.Collapsed;
                    break;
                case "progress":
                    WhisperProgressText.Text         = message;
                    WhisperProgressBar.Value         = 0;
                    WhisperProgressPanel.Visibility  = Visibility.Visible;
                    break;
                case "error":
                    WhisperErrorText.Text        = message;
                    WhisperErrorPanel.Visibility = Visibility.Visible;
                    WhisperDownloadBtn.Visibility = Visibility.Visible;
                    break;
                case "download":
                    WhisperDownloadBtn.Visibility = Visibility.Visible;
                    WhisperDownloadBtn.IsEnabled  = true;
                    // Whisper is optional — show hint but don't block Start
                    StartHintText.Visibility      = Visibility.Visible;
                    StartHintText.Text            = "Whisper es opcional (necesario solo para captura de audio del sistema)";
                    break;
            }
        }

        // ── LM Studio ──────────────────────────────────────────────────────

        private async void RefreshLmStudio_Click(object sender, RoutedEventArgs e)
        {
            await CheckLmStudioAsync();
        }

        private async Task CheckLmStudioAsync()
        {
            SetLmStudioStatus("checking");
            bool online = await _lmStudio.IsRunningAsync();

            if (!online)
            {
                SetLmStudioStatus("offline");
                return;
            }

            var models = await _lmStudio.GetInstalledModelsAsync();
            if (models.Count == 0)
            {
                SetLmStudioStatus("nomodels");
                return;
            }

            LmStudioModelCombo.ItemsSource = models;
            LmStudioModelCombo.SelectedItem = models.Contains(_lmStudioModelName)
                ? _lmStudioModelName : models[0];
            LmStudioModelCombo.IsEnabled = true;
            SetLmStudioStatus("online", $"{models.Count} modelo(s) disponible(s)");
        }

        private void LmStudioModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LmStudioModelCombo.SelectedItem is string model)
                _lmStudioModelName = model;
        }

        private void SetLmStudioStatus(string state, string detail = "")
        {
            LmStudioHintText.Visibility = Visibility.Collapsed;
            switch (state)
            {
                case "checking":
                    LmStudioStatusBadgeText.Text       = "⏳ Verificando";
                    LmStudioStatusBadge.Background     = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                    LmStudioModelCombo.IsEnabled        = false;
                    break;
                case "online":
                    LmStudioStatusBadgeText.Text       = $"✅ Online";
                    LmStudioStatusBadge.Background     = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x22, 0x4C, 0xAF, 0x50));
                    LmStudioStatusBadgeText.Foreground = System.Windows.Media.Brushes.LightGreen;
                    break;
                case "offline":
                    LmStudioStatusBadgeText.Text       = "⚠️ Offline";
                    LmStudioStatusBadge.Background     = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0x57, 0x22));
                    LmStudioStatusBadgeText.Foreground = System.Windows.Media.Brushes.Orange;
                    LmStudioHintText.Text              = "Abre LM Studio y carga un modelo";
                    LmStudioHintText.Visibility        = Visibility.Visible;
                    LmStudioModelCombo.IsEnabled       = false;
                    break;
                case "nomodels":
                    LmStudioStatusBadgeText.Text       = "⚠️ Sin modelos";
                    LmStudioStatusBadgeText.Foreground = System.Windows.Media.Brushes.Orange;
                    LmStudioHintText.Text              = "Abre LM Studio y carga un modelo";
                    LmStudioHintText.Visibility        = Visibility.Visible;
                    break;
            }
        }

        // ── Start ──────────────────────────────────────────────────────────

        private void UpdateStartButton()
        {
            // Whisper is optional — don't block the Start button
            StartBtn.IsEnabled = true;
            StartHintText.Visibility = Visibility.Collapsed;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            var main = new MainWindow();
            main.Show();
            Close();
        }

        // ── Settings ───────────────────────────────────────────────────────

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("userName",     out var u)) _userName         = u.GetString() ?? "Charlie";
                if (doc.RootElement.TryGetProperty("englishLevel", out var l)) _englishLevel     = l.GetString() ?? "B1";
                if (doc.RootElement.TryGetProperty("whisperModel", out var w)) _whisperModelFile = w.GetString() ?? "ggml-small.en.bin";
                if (doc.RootElement.TryGetProperty("lmStudioModel",  out var o)) _lmStudioModelName  = o.GetString() ?? "google/gemma-4-e4b";
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    userName     = _userName,
                    englishLevel = _englishLevel,
                    whisperModel = _whisperModelFile,
                    lmStudioModel  = _lmStudioModelName
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static string GetModelPath(string fileName)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "WindowsLiveCaptionsReader", "Models", fileName);
        }

    }
}
