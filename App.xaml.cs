using System;
using System.IO;
using System.Windows;

namespace WindowsLiveCaptionsReader
{
    public partial class App : Application
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant", "settings.json");

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                MessageBox.Show($"Fatal Error: {ex.ExceptionObject}", "Application Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);

            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show($"UI Error: {ex.Exception.Message}", "Application Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            base.OnStartup(e);
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Dark);

            // Skip setup if already configured before
            if (File.Exists(SettingsPath))
                new MainWindow().Show();
            else
                new SetupWindow().Show();
        }
    }
}
