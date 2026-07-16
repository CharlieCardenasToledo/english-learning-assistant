using System;
using System.Collections.Generic;

namespace WindowsLiveCaptionsReader.Models
{
    public class SessionMetadata
    {
        public int Id { get; set; } // Needed for EF Core primary key usually, or Owned Type
        public int SessionId { get; set; } // Foreign key if needed, or owned

        public int TotalEntries { get; set; }
        public int QuestionsDetected { get; set; }
        public int QuestionsAnswered { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> TopVocabulary { get; set; } = new();
        public string PrimaryTopic { get; set; } = "";
    }
}
