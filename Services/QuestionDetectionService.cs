using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WindowsLiveCaptionsReader.Models;

namespace WindowsLiveCaptionsReader.Services
{
    /// <summary>Holds the result of question detection including a 0-1 confidence score.</summary>
    public class QuestionDetectionResult
    {
        public bool IsQuestion        { get; set; }
        public float Confidence       { get; set; }   // 0.0 – 1.0
        public QuestionType Type      { get; set; }
        public string DetectedVia     { get; set; } = "";  // "L1", "L2", "L3", "L4-AI"
        public bool IsDirectedAtUser  { get; set; }   // true when user's name appears in the text
    }

    public class QuestionDetectionService
    {
        private readonly LmStudioService _lmStudioService;

        // Level 1: Direct Markers (Strongest) — English
        private static readonly string[] _whWords =
            { "who", "what", "where", "when", "why", "how", "which", "whose", "whom" };
        private static readonly string[] _auxiliaryVerbs =
            { "do", "does", "did", "is", "are", "was", "were", "can", "could",
              "will", "would", "shall", "should", "may", "might", "must", "have", "has", "had" };

        // Spanish WH-words (with and without accents — ASR may omit them)
        private static readonly string[] _whWordsEs =
            { "qué", "que", "quién", "quien", "quiénes", "quienes",
              "cuándo", "cuando", "dónde", "donde", "cómo", "como",
              "cuál", "cual", "cuáles", "cuales", "cuánto", "cuanto",
              "cuánta", "cuanta", "cuántos", "cuantos", "cuántas", "cuantas" };

        // Spanish auxiliary / copula verbs at sentence start
        private static readonly string[] _auxiliaryVerbsEs =
            { "es", "son", "fue", "era", "están", "está", "puede", "pueden",
              "tiene", "tienes", "sabe", "sabes", "hay", "vas", "van", "eres" };

        // Level 3: Indirect patterns — English + Spanish
        private static readonly string[] _indirectStarters =
        {
            "i wonder", "i was wondering", "could you tell me", "do you know",
            "i'd like to know", "can you explain", "please explain",
            "me pregunto", "podrías decirme", "sabes si", "podrías explicar",
            "quisiera saber", "puedes decirme", "puedes explicar"
        };

        // Tag-question suffix patterns (e.g. "…, right?", "…, isn't it?")
        private static readonly Regex _tagQuestionRegex =
            new(@",\s*(right|isn'?t it|aren'?t you|don'?t you|won'?t you|could you|can you)\??\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public QuestionDetectionService(LmStudioService lmStudioService)
        {
            _lmStudioService = lmStudioService;
        }

        // ── Public: main entry point for the capture flow ────────────────────
        public async Task<DetectedQuestion?> AnalyzeTextAsync(string text, int sessionId, int entryId)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var result = await AnalyzeWithConfidenceAsync(text);
            if (!result.IsQuestion) return null;

            return CreateQuestion(text, sessionId, entryId, result.Type, result.DetectedVia);
        }

        /// <summary>
        /// Full cascade detection returning a confidence score.
        /// Callers can use Confidence to decide badge visibility vs. auto-triggering assistant.
        /// Set skipAI=true to skip the Level-4 LM Studio call (L4 confidence is 0.60, below the
        /// 0.70 threshold used by DetectAndRespondAsync, so the call would always be discarded).
        /// </summary>
        public async Task<QuestionDetectionResult> AnalyzeWithConfidenceAsync(
            string text, string? userName = null, CancellationToken token = default, bool skipAI = false)
        {
            string clean = text.Trim().ToLowerInvariant();

            // ── Level 1: Explicit question mark ──────────────────────────────
            if (clean.EndsWith("?"))
            {
                var firstWord = clean.Split(' ')[0];
                var qType = _whWords.Contains(firstWord) ? QuestionType.WhQuestion
                          : _auxiliaryVerbs.Contains(firstWord) ? QuestionType.YesNo
                          : QuestionType.Direct;
                return EnrichWithUserName(new QuestionDetectionResult
                    { IsQuestion = true, Confidence = 0.95f, Type = qType, DetectedVia = "L1" },
                    text, userName);
            }

            // ── Level 2: Structural — WH or Aux at sentence start ────────────
            string startWord = clean.Split(' ')[0];
            int wordCount = clean.Split(' ').Length;

            if ((_whWords.Contains(startWord) || _whWordsEs.Contains(startWord)) && wordCount > 2)
                return EnrichWithUserName(new QuestionDetectionResult
                    { IsQuestion = true, Confidence = 0.85f, Type = QuestionType.WhQuestion, DetectedVia = "L2" },
                    text, userName);

            if ((_auxiliaryVerbs.Contains(startWord) || _auxiliaryVerbsEs.Contains(startWord)) && wordCount > 2)
                return EnrichWithUserName(new QuestionDetectionResult
                    { IsQuestion = true, Confidence = 0.80f, Type = QuestionType.YesNo, DetectedVia = "L2" },
                    text, userName);

            // ── Level 2b: Tag questions ───────────────────────────────────────
            if (_tagQuestionRegex.IsMatch(text))
                return EnrichWithUserName(new QuestionDetectionResult
                    { IsQuestion = true, Confidence = 0.85f, Type = QuestionType.TagQuestion, DetectedVia = "L2b" },
                    text, userName);

            // ── Level 3: Indirect question starters ──────────────────────────
            foreach (var starter in _indirectStarters)
            {
                if (clean.StartsWith(starter))
                    return EnrichWithUserName(new QuestionDetectionResult
                        { IsQuestion = true, Confidence = 0.70f, Type = QuestionType.Indirect, DetectedVia = "L3" },
                        text, userName);
            }

            // ── Level 4: AI verification (only for texts likely to be questions) ──
            // Heuristic gate: at least 4 words and no obvious statement structure
            if (!skipAI && wordCount >= 4)
            {
                bool aiSays = await IsQuestionViaAI(text, token);
                if (aiSays)
                    return EnrichWithUserName(new QuestionDetectionResult
                        { IsQuestion = true, Confidence = 0.75f, Type = QuestionType.Indirect, DetectedVia = "L4-AI" },
                        text, userName);
            }

            return new QuestionDetectionResult { IsQuestion = false, Confidence = 0f };
        }

        // ── Level 4: AI Verification (now fully implemented) ─────────────────
        /// <summary>
        /// Calls LM Studio with a strict classification prompt.
        /// Returns true only if the model replies "YES".
        /// </summary>
        public async Task<bool> IsQuestionViaAI(string text, CancellationToken token = default)
        {
            const string system = "You are a strict linguistic classifier. " +
                "Determine if the following English sentence is a question directed at someone " +
                "(including indirect questions and implicit requests). " +
                "Reply with ONLY the single word YES or NO. No punctuation, no explanation.";

            string userPrompt = $"Sentence: \"{text}\"";

            try
            {
                string answer = await _lmStudioService.AskAsync(system, userPrompt, token);
                return answer.Trim().ToUpper().StartsWith("YES");
            }
            catch
            {
                return false;   // fail-safe: don't flood assistant on error
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static QuestionDetectionResult EnrichWithUserName(
            QuestionDetectionResult result, string text, string? userName)
        {
            if (!string.IsNullOrWhiteSpace(userName) &&
                text.Contains(userName, StringComparison.OrdinalIgnoreCase))
            {
                result.IsDirectedAtUser = true;
                // Boost confidence when the user's name is explicitly mentioned
                result.Confidence = Math.Max(result.Confidence, 0.92f);
            }
            return result;
        }

        private DetectedQuestion CreateQuestion(
            string text, int sessionId, int entryId, QuestionType type, string context)
        {
            return new DetectedQuestion
            {
                SessionId    = sessionId,
                EntryId      = entryId,
                QuestionText = text,
                Type         = type,
                Context      = context,
                DetectedAt   = DateTime.Now,
                WasAnswered  = false
            };
        }
    }
}
