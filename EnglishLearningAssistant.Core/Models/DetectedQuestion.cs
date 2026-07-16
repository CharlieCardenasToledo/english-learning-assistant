using System;

namespace WindowsLiveCaptionsReader.Models
{
    public class DetectedQuestion
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public int EntryId { get; set; }
        public string QuestionText { get; set; } = "";
        public string Context { get; set; } = "";
        public string? SuggestedAnswer { get; set; }
        public QuestionType Type { get; set; }
        public DateTime DetectedAt { get; set; }
        public bool WasAnswered { get; set; }
    }

    public enum QuestionType
    {
        Direct,        // "What is your name?"
        TagQuestion,   // "You like coffee, don't you?"
        Indirect,      // "I was wondering if you could help"
        YesNo,         // "Do you understand?"
        WhQuestion,    // "Where did you go?"
        Choice,        // "Do you want tea or coffee?"
        Rhetorical,    // "Isn't it obvious?"
        Instruction    // "Read the text aloud" / "Explain the answer"
    }
}
