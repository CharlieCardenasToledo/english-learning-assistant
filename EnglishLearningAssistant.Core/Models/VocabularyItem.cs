using System;
using System.Collections.Generic;

namespace WindowsLiveCaptionsReader.Models
{
    public class VocabularyItem
    {
        public int Id { get; set; }
        public string Word { get; set; } = "";
        public string Definition { get; set; } = "";
        public string SpanishTranslation { get; set; } = "";
        public string ExampleSentence { get; set; } = "";
        public string Pronunciation { get; set; } = "";
        public int TimesEncountered { get; set; } = 1;
        public int TimesReviewed { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public VocabularyLevel Level { get; set; } = VocabularyLevel.New;
        public List<int> SessionIds { get; set; } = new();
    }

    public enum VocabularyLevel { New, Learning, Familiar, Mastered }
}
