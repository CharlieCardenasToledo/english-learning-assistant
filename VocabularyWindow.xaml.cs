using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using WindowsLiveCaptionsReader.Models;
using WindowsLiveCaptionsReader.Services;

namespace WindowsLiveCaptionsReader
{
    public partial class VocabularyWindow : FluentWindow
    {
        private readonly VocabularyService _service;
        private List<VocabularyItem> _allWords = new();

        public VocabularyWindow(VocabularyService service)
        {
            InitializeComponent();
            _service = service;
            Loaded += VocabularyWindow_Loaded;
        }

        private async void VocabularyWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WindowBackdropType = WindowBackdropType.Mica;
            await LoadVocabulary();
        }

        private async Task LoadVocabulary()
        {
            StatusText.Text = "Cargando...";
            try
            {
                _allWords = await _service.GetAllVocabularyAsync();
                FilterList(SearchBox.Text);
                StatusText.Text = "Listo";
                CountText.Text = $"Total: {_allWords.Count} palabras";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error al cargar";
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private void FilterList(string query)
        {
            if (_allWords == null) return;

            if (string.IsNullOrWhiteSpace(query) || query == "Buscar...")
            {
                VocabGrid.ItemsSource = _allWords;
            }
            else
            {
                var filtered = _allWords.Where(w =>
                    w.Word.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    w.SpanishTranslation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (w.Definition != null && w.Definition.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
                VocabGrid.ItemsSource = filtered;
            }
        }

        private void SearchBox_KeyUp(object sender, KeyEventArgs e)
        {
            FilterList(SearchBox.Text);
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text == "Buscar...")
            {
                SearchBox.Text = "";
                SearchBox.Foreground = System.Windows.Media.Brushes.White;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "Buscar...";
                SearchBox.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        // ── Add Word ──────────────────────────────────────────────────────────

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
                StatusText.Text = "Palabra y traducción son obligatorias.";
                return;
            }

            StatusText.Text = "Guardando...";
            try
            {
                await _service.AddOrUpdateWordAsync(word, def, trans);
                AddWordForm.Visibility = Visibility.Collapsed;
                await LoadVocabulary();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error al guardar";
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        // ── Extract from clipboard ────────────────────────────────────────────

        private async void ExtractFromText_Click(object sender, RoutedEventArgs e)
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length < 20)
            {
                MessageBox.Show("Copia texto (mín. 20 caracteres) al portapapeles antes de hacer clic.", "Analizar texto");
                return;
            }

            StatusText.Text = "Analizando portapapeles...";
            try
            {
                var suggestions = await _service.ExtractPotentialVocabularyAsync(text);
                if (suggestions.Count > 0)
                {
                    string msg = "Palabras encontradas:\n\n" + string.Join("\n", suggestions) + "\n\n¿Añadir al vocabulario?";
                    if (MessageBox.Show(msg, "Resultado del análisis", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        foreach (var s in suggestions)
                        {
                            var parts = s.Split('|');
                            if (parts.Length >= 3)
                                await _service.AddOrUpdateWordAsync(parts[0], parts[1], parts[2],
                                    text[..Math.Min(text.Length, 60)] + "...");
                        }
                        await LoadVocabulary();
                    }
                }
                else
                {
                    MessageBox.Show("No se encontró vocabulario nuevo de nivel B1/B2.", "Análisis completado");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error en el análisis: {ex.Message}");
            }
            finally
            {
                StatusText.Text = "Listo";
            }
        }

        // ── Delete ────────────────────────────────────────────────────────────

        private async void DeleteWord_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                if (MessageBox.Show("¿Eliminar esta palabra?", "Confirmar", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    await _service.DeleteWordAsync(id);
                    await LoadVocabulary();
                }
            }
        }

    }
}
