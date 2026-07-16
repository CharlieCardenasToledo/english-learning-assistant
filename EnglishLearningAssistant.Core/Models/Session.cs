using System;
using System.Collections.Generic;

namespace WindowsLiveCaptionsReader.Models
{
    public class Session
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public SessionStatus Status { get; set; } = SessionStatus.Active;
        public string Summary { get; set; } = "";
        public string? RecordingPath { get; set; }
        public List<TranscriptionEntry> Entries { get; set; } = new();
        public List<DetectedQuestion> Questions { get; set; } = new();
        public SessionMetadata Metadata { get; set; } = new();
    }


    public enum SessionStatus { Active, Paused, Completed, Archived }
}
