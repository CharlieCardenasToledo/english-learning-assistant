using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WindowsLiveCaptionsReader.Data;
using WindowsLiveCaptionsReader.Models;

namespace WindowsLiveCaptionsReader.Services
{
    public class VocabularyService : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly LmStudioService _lmStudio;

        public VocabularyService(LmStudioService lmStudio)
        {
            _context = new AppDbContext();
            _lmStudio = lmStudio;
        }

        public async Task InitializeAsync()
        {
            await _context.Database.EnsureCreatedAsync();
        }

        public async Task<List<VocabularyItem>> GetAllVocabularyAsync()
        {
            return await _context.Vocabulary.OrderByDescending(v => v.LastSeen).ToListAsync();
        }

        public async Task AddOrUpdateWordAsync(string word, string definition, string translation, string context = "", int sessionId = 0)
        {
            word = word.Trim().ToLowerInvariant();
            var existing = await _context.Vocabulary.FirstOrDefaultAsync(v => v.Word.ToLower() == word);

            if (existing != null)
            {
                existing.TimesEncountered++;
                existing.LastSeen = DateTime.Now;
                if (!existing.SessionIds.Contains(sessionId) && sessionId > 0)
                {
                    existing.SessionIds.Add(sessionId);
                }
                // Optional: Update definition if provided and empty
                if (string.IsNullOrEmpty(existing.Definition) && !string.IsNullOrEmpty(definition))
                    existing.Definition = definition;
            }
            else
            {
                var newItem = new VocabularyItem
                {
                    Word = word,
                    Definition = definition,
                    SpanishTranslation = translation,
                    ExampleSentence = context,
                    FirstSeen = DateTime.Now,
                    LastSeen = DateTime.Now,
                    Level = VocabularyLevel.New,
                    SessionIds = sessionId > 0 ? new List<int> { sessionId } : new List<int>()
                };
                _context.Vocabulary.Add(newItem);
            }

            await _context.SaveChangesAsync();
        }

        public async Task UpdateReviewAsync(int id, bool correct)
        {
            var item = await _context.Vocabulary.FindAsync(id);
            if (item == null) return;

            item.TimesReviewed++;
            if (correct)
            {
                // Simple promotion logic
                if (item.Level < VocabularyLevel.Mastered)
                    item.Level++;
            }
            else
            {
                // Demotion if wrong
                if (item.Level > VocabularyLevel.New)
                    item.Level--;
            }
            await _context.SaveChangesAsync();
        }

        public async Task<List<string>> ExtractPotentialVocabularyAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 20) return new List<string>();

            // Use LM Studio to suggest vocabulary
            var result = await _lmStudio.GenerateVocabularyExtractionAsync(text);
            
            // Parse result "Word|Definition|Translation"
            var lines = result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var extractedWords = new List<string>();
            
            foreach (var line in lines)
            {
                // Basic cleanup
                var cleanLine = line.Trim();
                if (string.IsNullOrWhiteSpace(cleanLine) || cleanLine.StartsWith("output", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = cleanLine.Split('|');
                if (parts.Length >= 3)
                {
                    string word = parts[0].Trim();
                    string def = parts[1].Trim();
                    string trans = parts[2].Trim();
                    
                    // For now, return formatted strings. 
                    // In future, we can return VocabularyItem objects or save them directly.
                    extractedWords.Add($"{word}|{def}|{trans}");
                }
            }
            
            return extractedWords;
        }

        public async Task DeleteWordAsync(int id)
        {
            var item = await _context.Vocabulary.FindAsync(id);
            if (item != null)
            {
                _context.Vocabulary.Remove(item);
                await _context.SaveChangesAsync();
            }
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
