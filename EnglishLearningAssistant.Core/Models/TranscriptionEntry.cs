using System;

namespace WindowsLiveCaptionsReader.Models
{
    public class TranscriptionEntry
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string OriginalText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public EntrySource Source { get; set; }  // LiveCaption, Microphone, Browser, Recording
        public float? ConfidenceScore { get; set; }
        public bool ContainsQuestion { get; set; }
        public string? AiResponse { get; set; }
        public double? AudioStartTime { get; set; } // En segundos, relativo al inicio del audio/sesión
        public double? AudioEndTime { get; set; }
    }

    public enum EntrySource { LiveCaption, Microphone, Browser, Recording }
}
