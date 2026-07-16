using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using WindowsLiveCaptionsReader.Data;
using WindowsLiveCaptionsReader.Models;


namespace WindowsLiveCaptionsReader.Services
{
    public class SessionService : IDisposable
    {
        private readonly AppDbContext _context;
        private System.Timers.Timer? _autoSaveTimer;
        private Session? _currentSession;

        public SessionService()
        {
            _context = new AppDbContext();
        }

        public async Task InitializeAsync()
        {
            // Si el archivo de base de datos existe pero está vacío (0 bytes), lo eliminamos para recrearlo
            try
            {
                var dbPath = EnglishLearningAssistant.Core.Models.AppConfiguration.Instance.Storage.DatabasePath!;
                if (System.IO.File.Exists(dbPath) && new System.IO.FileInfo(dbPath).Length == 0)
                {
                    System.IO.File.Delete(dbPath);
                }
            }
            catch { }

            await _context.Database.EnsureCreatedAsync();

            // Garantizar que todas las tablas existan (útil si el archivo de base de datos existía pero estaba vacío o corrupto)
            try
            {
                var databaseCreator = _context.Database.GetService<IRelationalDatabaseCreator>();
                if (!await databaseCreator.HasTablesAsync())
                {
                    await databaseCreator.CreateTablesAsync();
                }
            }
            catch (Exception ex)
            {
                EnglishLearningAssistant.Core.AppLogger.Error("No se pudo verificar o crear las tablas de la base de datos", ex);
            }

            // Asegurar que la tabla de caché de traducciones existe (Fase 6)
            try
            {
                await _context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS TranslationCache (" +
                    "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                    "OriginalText TEXT, " +
                    "TranslatedText TEXT, " +
                    "SourceLanguage TEXT, " +
                    "TargetLanguage TEXT, " +
                    "ProviderName TEXT, " +
                    "CreatedAt TEXT)");
            }
            catch (Exception ex)
            {
                EnglishLearningAssistant.Core.AppLogger.Error("No se pudo verificar/crear la tabla TranslationCache", ex);
            }
        }


        public async Task<Session> CreateSessionAsync(string title)
        {
            var session = new Session
            {
                Title = title,
                StartTime = DateTime.Now,
                Status = SessionStatus.Active
            };

            // Use separate context or just add to current? 
            // Better to add and save to get ID.
            _context.Sessions.Add(session);
            await _context.SaveChangesAsync();
            
            _currentSession = session;
            StartAutoSave(session);
            
            return session;
        }

        public async Task SaveSessionAsync(Session session)
        {
            if (session == null) return;
            
            // If the session is already tracked by context, SaveChanges is enough.
            // If detached, we might need to Update.
            // Assuming we keep the same context instance for simplicity in this WPF app (not recommended for long running apps usually, but implementing per plan).
            
            // To ensure we don't have issues, we can check state
            session.Metadata.TotalEntries = session.Entries.Count;
            session.Metadata.QuestionsDetected = session.Questions.Count;
            session.Metadata.QuestionsAnswered = session.Questions.Count(q => q.WasAnswered);
            
            if (session.Entries.Any())
            {
                session.Metadata.Duration = (session.EndTime ?? DateTime.Now) - session.StartTime;
            }

            // Only call update if it's not already tracked or if we want to force update
            // With a single context, changes are tracked automatically.
            await _context.SaveChangesAsync();
        }

        public async Task<Session?> LoadSessionAsync(int sessionId)
        {
            return await _context.Sessions
                .Include(s => s.Entries)
                .Include(s => s.Questions)
                .Include(s => s.Metadata) // Owned type, usually included automatically but explicitly doesn't hurt
                .FirstOrDefaultAsync(s => s.Id == sessionId);
        }

        public async Task<List<Session>> GetAllSessionsAsync()
        {
            return await _context.Sessions
                .OrderByDescending(s => s.StartTime)
                .ToListAsync();
        }

        public async Task<List<Session>> SearchSessionsAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return await GetAllSessionsAsync();
            
            var lowerQuery = query.ToLower();
            return await _context.Sessions
                .Where(s => s.Title.ToLower().Contains(lowerQuery) ||
                            (s.Summary != null && s.Summary.ToLower().Contains(lowerQuery)))
                .OrderByDescending(s => s.StartTime)
                .ToListAsync();
        }

        public async Task DeleteSessionAsync(int sessionId)
        {
            var session = await _context.Sessions.FindAsync(sessionId);
            if (session != null)
            {
                _context.Sessions.Remove(session);
                await _context.SaveChangesAsync();
            }
        }
        
        public async Task SaveEntryAsync(TranscriptionEntry entry)
        {
            // If persisting immediately separately from session
            if (entry.Id == 0)
            {
                _context.Entries.Add(entry);
            }
            // else it is tracked
            await _context.SaveChangesAsync();
        }

        public async Task SaveQuestionAsync(DetectedQuestion question)
        {
            if (question.Id == 0)
            {
                _context.Questions.Add(question);
            }
            await _context.SaveChangesAsync();
        }

        public async Task<string> ExportToMarkdownAsync(int sessionId)
        {
            var session = await LoadSessionAsync(sessionId);
            if (session == null) return "# Session not found";

            var sb = new StringBuilder();
            sb.AppendLine($"# {session.Title}");
            sb.AppendLine($"Date: {session.StartTime:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Duration: {session.Metadata.Duration:hh\\:mm\\:ss}");
            sb.AppendLine();
            
            sb.AppendLine("## Summary");
            sb.AppendLine(session.Summary);
            sb.AppendLine();
            
            sb.AppendLine("## Transcript");
            foreach (var entry in session.Entries.OrderBy(e => e.Timestamp))
            {
                sb.AppendLine($"**[{entry.Timestamp:HH:mm:ss}]** {entry.OriginalText}");
                if (!string.IsNullOrEmpty(entry.TranslatedText))
                {
                    sb.AppendLine($"> *{entry.TranslatedText}*");
                }
                if (entry.ContainsQuestion && !string.IsNullOrEmpty(entry.AiResponse))
                {
                   sb.AppendLine($"\n> 🤖 **AI Assistant:** {entry.AiResponse}\n");
                }
                sb.AppendLine();
            }
            
            return sb.ToString();
        }

        public async Task<string> ExportToJsonAsync(int sessionId)
        {
            var session = await LoadSessionAsync(sessionId);
            if (session == null) return "{}";
            
            return JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        }

        public async Task<string> ExportToSrtAsync(int sessionId)
        {
            var session = await LoadSessionAsync(sessionId);
            if (session == null) return string.Empty;

            var sb = new StringBuilder();
            int counter = 1;

            foreach (var entry in session.Entries.OrderBy(e => e.Timestamp))
            {
                var start = TimeSpan.FromSeconds(entry.AudioStartTime ?? 0);
                var end = TimeSpan.FromSeconds(entry.AudioEndTime ?? (entry.AudioStartTime ?? 0) + 3);

                sb.AppendLine(counter.ToString());
                sb.AppendLine($"{start:hh\\:mm\\:ss\\,fff} --> {end:hh\\:mm\\:ss\\,fff}");
                sb.AppendLine(entry.OriginalText);
                if (!string.IsNullOrEmpty(entry.TranslatedText))
                {
                    sb.AppendLine(entry.TranslatedText);
                }
                sb.AppendLine();
                counter++;
            }

            return sb.ToString();
        }

        public async Task<string> ExportToVttAsync(int sessionId)
        {
            var session = await LoadSessionAsync(sessionId);
            if (session == null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("WEBVTT");
            sb.AppendLine();

            int counter = 1;
            foreach (var entry in session.Entries.OrderBy(e => e.Timestamp))
            {
                var start = TimeSpan.FromSeconds(entry.AudioStartTime ?? 0);
                var end = TimeSpan.FromSeconds(entry.AudioEndTime ?? (entry.AudioStartTime ?? 0) + 3);

                sb.AppendLine(counter.ToString());
                sb.AppendLine($"{start:hh\\:mm\\:ss\\.fff} --> {end:hh\\:mm\\:ss\\.fff}");
                sb.AppendLine(entry.OriginalText);
                if (!string.IsNullOrEmpty(entry.TranslatedText))
                {
                    sb.AppendLine(entry.TranslatedText);
                }
                sb.AppendLine();
                counter++;
            }

            return sb.ToString();
        }


        public void StartAutoSave(Session session)
        {
            StopAutoSave();
            _currentSession = session;
            _autoSaveTimer = new System.Timers.Timer(30000); // 30 seconds
            _autoSaveTimer.Elapsed += async (s, e) => 
            {
                if (_currentSession != null)
                {
                    var syncContext = System.Threading.SynchronizationContext.Current;
                    if (syncContext != null)
                    {
                        syncContext.Post(async _ => 
                        {
                            try
                            {
                                await SaveSessionAsync(_currentSession);
                            }
                            catch { }
                        }, null);
                    }
                    else
                    {
                        try
                        {
                            await SaveSessionAsync(_currentSession);
                        }
                        catch { }
                    }
                }
            };

            _autoSaveTimer.Start();
        }

        public void StopAutoSave()
        {
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;
        }

        public void Dispose()
        {
            StopAutoSave();
            _context.Dispose();
        }
    }
}
