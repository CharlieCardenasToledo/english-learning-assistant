using System;

namespace WindowsLiveCaptionsReader.Services
{
    /// <summary>
    /// Accumulates raw caption updates into committed sentences and a rolling pending one.
    /// Call all methods on the UI thread (Dispatcher.Invoke).
    /// </summary>
    public class CaptionPipeline
    {
        private const int MaxCommittedLines = 100;

        private string _committed = "";
        private string _pending = "";
        private string _fullTranscription = "";
        // Raw pending text at the moment of a forced commit, so Feed() can strip it
        // if Live Captions keeps extending the same line after the pause.
        private string _forceCommitted = "";

        public string Pending => _pending;
        public string FullTranscription => _fullTranscription;

        public string GetDisplayText() =>
            string.IsNullOrEmpty(_committed) ? _pending : _committed + "\n" + _pending;

        /// <summary>
        /// Feed a new raw caption update.
        /// Returns the committed sentence if a sentence boundary was crossed, null otherwise.
        /// </summary>
        public string? Feed(string newCaption)
        {
            // After a forced commit, Live Captions may resend the same line extended —
            // strip the part we already committed so it isn't duplicated.
            if (_forceCommitted.Length > 0)
            {
                if (newCaption.StartsWith(_forceCommitted))
                {
                    newCaption = newCaption[_forceCommitted.Length..].TrimStart();
                    if (newCaption.Length == 0) return null;
                }
                else
                {
                    _forceCommitted = ""; // a genuinely new line started
                }
            }

            if (newCaption == _pending) return null;

            bool isNewSentence = !string.IsNullOrEmpty(_pending)
                && _pending.Length > 4
                && !newCaption.StartsWith(_pending[..Math.Min(8, _pending.Length)].TrimEnd());

            if (isNewSentence)
            {
                string committed = _pending;
                CommitLine(committed);
                _pending = newCaption;
                return committed;
            }

            _pending = newCaption;
            return null;
        }

        /// <summary>
        /// Commits the pending line without waiting for a new caption line.
        /// Called when captions stop changing (speaker paused) so trailing
        /// sentences — typically questions — don't sit unprocessed.
        /// </summary>
        public string? ForceCommitPending()
        {
            if (string.IsNullOrWhiteSpace(_pending) || _pending.Trim().Length < 2) return null;

            string committed = _pending.Trim();
            CommitLine(committed);
            _forceCommitted = _pending;
            _pending = "";
            return committed;
        }

        private void CommitLine(string committed)
        {
            _fullTranscription = string.IsNullOrEmpty(_fullTranscription)
                ? committed
                : _fullTranscription + "\n" + committed;

            string newCommitted = string.IsNullOrEmpty(_committed)
                ? committed
                : _committed + "\n" + committed;

            var lines = newCommitted.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _committed = lines.Length > MaxCommittedLines
                ? string.Join("\n", lines[^MaxCommittedLines..])
                : newCommitted;
        }

        public string GetRecentContext(int maxLines = 15)
        {
            string full = string.IsNullOrWhiteSpace(_fullTranscription) ? _committed : _fullTranscription;
            if (string.IsNullOrWhiteSpace(full)) return "Sin contexto";
            var lines = full.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines[^Math.Min(maxLines, lines.Length)..]);
        }

        public void LoadHistory(string committed)
        {
            _committed = committed;
            _pending = "";
            _fullTranscription = committed;
        }

        public void Reset()
        {
            _committed = "";
            _pending = "";
            _fullTranscription = "";
            _forceCommitted = "";
        }
    }
}
