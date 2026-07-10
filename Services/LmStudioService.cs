using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLiveCaptionsReader.Services
{
    // LM Studio expone una API compatible con OpenAI en localhost:1234/v1
    public class LmStudioService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "http://localhost:1234";
        private const string ChatUrl = BaseUrl + "/v1/chat/completions";
        private string _modelName = "google/gemma-4-e4b";

        private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LongTimeout  = TimeSpan.FromSeconds(120);

        public LmStudioService(string modelName = "google/gemma-4-e4b")
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = LongTimeout;
            _modelName = modelName;
        }

        public void SetModel(string modelName) => _modelName = modelName;

        public async Task<List<string>> GetInstalledModelsAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(ShortTimeout);
                var response = await _httpClient.GetAsync(BaseUrl + "/v1/models", cts.Token);
                if (!response.IsSuccessStatusCode) return [];

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);

                var names = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var m in data.EnumerateArray())
                        if (m.TryGetProperty("id", out var id))
                            names.Add(id.GetString() ?? "");

                return names;
            }
            catch { return []; }
        }

        public async Task<bool> IsRunningAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(ShortTimeout);
                var response = await _httpClient.GetAsync(BaseUrl + "/v1/models", cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public bool StartServer()
        {
            // LM Studio es una app de escritorio GUI; no tiene comando CLI equivalente a "ollama serve".
            // El usuario debe abrirla manualmente y cargar un modelo.
            return false;
        }

        public async Task<string> TranslateStreamAsync(string text, Action<string> onPartialUpdate, List<string>? historyContext = null, string targetLang = "Spanish", CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

#if DEBUG
            SafeLog($"[{DateTime.Now:HH:mm:ss}] Requesting: {text}\n");
#endif

            var systemContent = "Translate the input text to Spanish. Return only the translated text. No explanation.";

            var messagesList = new List<object>();
            messagesList.Add(new { role = "system", content = systemContent });

            if (historyContext != null && historyContext.Count > 0)
            {
                var contextBlock = string.Join("\n", historyContext);
                messagesList.Add(new { role = "user", content = $"Recent conversation context:\n{contextBlock}" });
                messagesList.Add(new { role = "assistant", content = "Understood. I will use this context for translation." });
            }

            messagesList.Add(new { role = "user", content = $"Translate to Spanish. Output ONLY the translation. Text: {text}" });

            var requestData = new
            {
                model = _modelName,
                messages = messagesList,
                stream = true,
                temperature = 0.3
            };

            return await SendStreamingRequestAsync(requestData, onPartialUpdate, token);
        }

        public async Task<string> CleanAndTranslateStreamAsync(string rawCaptions, Action<string> onPartialUpdate, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(rawCaptions)) return "";

            const string system =
                "You receive raw text captured by an automatic speech-to-text captioning system. " +
                "The text may contain repeated phrases, incomplete sentence fragments, or incremental duplicates " +
                "caused by the rolling-window nature of the captioning system. " +
                "Your task: " +
                "(1) Remove ALL repetitions, duplicates, and incomplete fragments. " +
                "(2) Reconstruct the content into coherent, complete sentences that preserve the original meaning. " +
                "(3) Translate the cleaned content into natural, fluent Spanish. " +
                "Return ONLY the final Spanish translation — clean, coherent, and ready to read. " +
                "No explanations, no English text, no bullet points.";

            var requestData = new
            {
                model    = _modelName,
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = $"Raw captions:\n\n{rawCaptions}" }
                },
                stream      = true,
                temperature = 0.3
            };

            return await SendStreamingRequestAsync(requestData, onPartialUpdate, token);
        }

        public async Task<string> StreamChatAsync(string systemPrompt, string userPrompt, Action<string> onPartialUpdate, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt)) return "";

            var requestData = new
            {
                model    = _modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt   }
                },
                stream      = true,
                temperature = 0.4
            };

            return await SendStreamingRequestAsync(requestData, onPartialUpdate, token);
        }

        public async Task<string> TranslateAsync(string text, string targetLang = "Spanish")
        {
            var result = "";
            await TranslateStreamAsync(text, (partial) => { result = partial; }, null, targetLang);
            return result;
        }

        public async Task<string> GenerateSummaryAsync(string fullHistory)
        {
            var prompt = $@"
Analyze the following English class transcript/conversation and provide a structured summary in MARKDOWN format.
Focus on educational value for the student.

TRANSCRIPT:
{fullHistory}

OUTPUT FORMAT:
# Class Summary - {DateTime.Now:yyyy-MM-dd}

## 📌 Main Topics
- (List main topics discussed)

## 🧠 Key Vocabulary & Phrases
- (List new or important words used)

## ✅ Action Items / Homework
- (List any tasks mentioned, if none, say 'None detected')

## 💡 Improvement Tips
- (If the student spoke, suggest 1-2 grammatical corrections)
";

            var requestData = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = "You are an English class summarizer. Produce structured Markdown summaries." },
                    new { role = "user",   content = prompt }
                },
                stream = false,
                temperature = 0.4
            };

            return await SendNonStreamingRequestAsync(requestData);
        }

        public async Task<string> GenerateResponseToQuestionAsync(string question, string conversationContext, CancellationToken token = default)
        {
            var prompt = $@"The user is taking an English class or practice session. The teacher asked a specific question.
Helping the user to reply is your goal.
Provide 3 distinct options for the user to reply, with varying complexity.

QUESTION ASKED: ""{question}""

CONTEXT:
{conversationContext}

REQUIREMENTS:
1.  **Simple Option:** Short, direct answer (A2 Level).
2.  **Standard Option:** Natural, complete answer (B1 Level).
3.  **Detailed Option:** Elaborate answer with more details (B2 Level).

FORMAT:
Option 1 (Simple): [Text]
(Translation): [Spanish Translation]

Option 2 (Standard): [Text]
(Translation): [Spanish Translation]

Option 3 (Detailed): [Text]
(Translation): [Spanish Translation]

Only provide the options and translations. No intro/outro.";

            var requestData = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = "You are a helpful English tutor assistant. Your task is to provide reply options for the student." },
                    new { role = "user",   content = prompt }
                },
                stream = false,
                temperature = 0.7
            };

            return await SendNonStreamingRequestAsync(requestData, token);
        }

        public async Task<string> GenerateSuggestionsAsync(string conversationContext, CancellationToken token = default)
        {
            var prompt = $@"The user is in a speaking exam or English conversation practice. Based on the conversation history below, suggest 3 natural, COMPLETE responses the user can say.

IMPORTANT REQUIREMENTS:
- User is learning English at B1 level (CEFR)
- Each response should be 2-4 complete sentences
- Use vocabulary and grammar appropriate for B1 level
- Responses should sound natural and conversational
- Include helpful educational information

Conversation History:
{conversationContext}

Provide the output in this EXACT format for each suggestion:

1. [Complete English response with 2-4 sentences]
   📝 Explicación: [Brief explanation in Spanish about grammar or usage]
   📚 Vocabulario: [2-3 key words/phrases with Spanish meanings]
   🇪🇸 Traducción: [Full Spanish translation]

2. [Second complete response...]
   📝 Explicación: [...]
   📚 Vocabulario: [...]
   🇪🇸 Traducción: [...]

3. [Third complete response...]
   📝 Explicación: [...]
   📚 Vocabulario: [...]
   🇪🇸 Traducción: [...]

Make sure each response is educational and helps the student learn, not just answer.";

            var requestData = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = "You are an expert English tutor for B1 level students (CEFR). Your goal is to help students learn while providing natural, appropriate responses. Each suggestion should be educational, complete (2-4 sentences), and include grammar explanations, vocabulary help, and Spanish translations. Always follow the exact format requested." },
                    new { role = "user",   content = prompt }
                },
                stream = false,
                temperature = 0.7
            };

            return await SendNonStreamingRequestAsync(requestData, token);
        }

        public async Task<string> GenerateVocabularyExtractionAsync(string text, string targetLang = "Spanish", CancellationToken token = default)
        {
            var prompt = $@"Analyze the following English text and extract 3-5 key vocabulary words or phrases suitable for a B1/B2 English learner.
Rules:
1. Ignore proper nouns (names, places) unless relevant.
2. Ignore very common words (A1 level).
3. Focus on useful phrases or intermediate words.
4. Output specific format: Word|Definition (English)|Translation ({targetLang})

TEXT: ""{text}""

OUTPUT FORMAT (one per line):
Word|Definition|Translation";

            var requestData = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = "You are a vocabulary extraction specialist. Extract key B1/B2 words/phrases with definitions and translations." },
                    new { role = "user",   content = prompt }
                },
                stream = false,
                temperature = 0.5
            };

            return await SendNonStreamingRequestAsync(requestData, token);
        }

        public async Task<string> AskAsync(string systemPrompt, string userPrompt, CancellationToken token = default)
        {
            var requestData = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt   }
                },
                stream = false,
                temperature = 0.0
            };

            return await SendNonStreamingRequestAsync(requestData, token);
        }

        public async Task<string> GenerateShortSummaryAsync(string transcript, CancellationToken token = default)
        {
            const string system = "You are a concise summarizer. Given an English class transcript, write a 2-3 sentence summary in Spanish. Plain text only, no markdown, no bullet points.";
            return await AskAsync(system, $"Transcript:\n{transcript}", token);
        }

        // ── Private helpers ────────────────────────────────────────────────

        private async Task<string> SendStreamingRequestAsync(object requestData, Action<string> onPartialUpdate, CancellationToken token)
        {
            var jsonContent = JsonSerializer.Serialize(requestData);
            var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
#if DEBUG
                    SafeLog($"[{DateTime.Now:HH:mm:ss}] Error Status: {response.StatusCode} | {errorBody}\n");
#endif
                    return $"[Error: {response.StatusCode}]";
                }

                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var sb = new StringBuilder();

                while (!reader.EndOfStream)
                {
                    if (token.IsCancellationRequested) break;

                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // LM Studio/OpenAI SSE format: each line is "data: {...}" or "data: [DONE]"
                    if (!line.StartsWith("data: ")) continue;
                    var payload = line["data: ".Length..];
                    if (payload == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(payload);

                        if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                        var first = choices[0];

                        // Check finish_reason
                        if (first.TryGetProperty("finish_reason", out var finishReason) &&
                            finishReason.ValueKind != JsonValueKind.Null &&
                            finishReason.GetString() == "stop")
                            break;

                        if (first.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentEl))
                        {
                            var chunk = contentEl.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                sb.Append(chunk);
                                onPartialUpdate(sb.ToString());
                            }
                        }
                    }
                    catch (JsonException) { }
                }

                return CleanOutput(sb.ToString());
            }
            catch (TaskCanceledException) { return ""; }
            catch (Exception ex)
            {
                SafeLog($"[{DateTime.Now:HH:mm:ss}] Exception: {ex.Message}\n");
                return $"[Error: {ex.Message}]";
            }
        }

        private async Task<string> SendNonStreamingRequestAsync(object requestData, CancellationToken token = default)
        {
            var jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(ChatUrl, content, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return $"[Error: {response.StatusCode}]";

                var responseString = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var cnt))
                {
                    return CleanOutput(cnt.GetString());
                }

                return "Error: respuesta vacía del modelo.";
            }
            catch (TaskCanceledException) { return ""; }
            catch (Exception ex)
            {
                SafeLog($"[{DateTime.Now:HH:mm:ss}] Exception: {ex.Message}\n");
                return $"[Error: {ex.Message}]";
            }
        }

        private void SafeLog(string message)
        {
            try { File.AppendAllText("lmstudio_debug.log", message); } catch { }
        }

        private string CleanOutput(string? output)
        {
            if (string.IsNullOrEmpty(output)) return "";

            int thinkStart = output.IndexOf("<think>");
            int thinkEnd   = output.IndexOf("</think>");
            if (thinkStart >= 0 && thinkEnd > thinkStart)
                output = output.Remove(thinkStart, thinkEnd - thinkStart + 8);

            return output.Trim().Trim('"');
        }
    }
}
