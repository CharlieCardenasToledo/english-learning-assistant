# English Learning Assistant

[![Version](https://img.shields.io/badge/version-2.0.0-blue)](https://github.com/CharlieCardenasToledo/english-learning-assistant/releases)
[![Tauri v2](https://img.shields.io/badge/Tauri-v2-FFC131?logo=tauri&logoColor=white)](https://tauri.app/)
[![Next.js](https://img.shields.io/badge/Next.js-14-black?logo=next.js)](https://nextjs.org/)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5-3178C6?logo=typescript&logoColor=white)](https://www.typescriptlang.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-22c55e.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows_10%2F11-0078D4?logo=windows)](https://www.microsoft.com/windows)

> Real-time AI assistant for English learners. Captures Windows Live Captions, translates to Spanish automatically, detects questions, and generates contextual response suggestions — all running locally on your machine.

**English | [Español](README.es.md)**

---

![Session in progress](docs/screenshots/session-live.png)

---

## How it works

English Learning Assistant runs as a native desktop app (built with Tauri v2 + Next.js). It reads the text that Windows Live Captions is transcribing and:

1. **Shows** the English transcription in real time
2. **Translates** each sentence to Spanish automatically
3. **Detects** questions using a 4-level cascade (L1–L4)
4. **Generates** AI-powered response suggestions in both languages

Everything runs locally — no data leaves your machine.

---

## Screenshots

| Main view | Session history |
|-----------|-----------------|
| ![Main view](docs/screenshots/main-idle.png) | ![Sessions](docs/screenshots/sessions.png) |

| Settings — Built-in AI | Vocabulary manager |
|------------------------|-------------------|
| ![Settings](docs/screenshots/settings.png) | ![Vocabulary](docs/screenshots/vocabulary.png) |

---

## Features

### Real-time transcription and translation
- Reads Windows Live Captions via UI Automation — no audio processing required
- Translates every committed sentence to Spanish
- Three translation providers: **Built-in AI** (no external server), **LM Studio**, **Ollama**
- Microphone input as a secondary source via Whisper

### Intelligent question detection
- **L1** — Explicit question mark (confidence 0.95)
- **L2** — WH-word or auxiliary verb at sentence start (0.80–0.85)
- **L2b** — Tag questions like "right?", "isn't it?" (0.85)
- **L3** — Indirect starters like "I wonder…", "I'd like to know…" (0.70)
- **L4** — LLM classifier for ambiguous cases (0.75)
- Username detection: boosts confidence when your name appears in the sentence

### AI response suggestions
- Generates contextual responses tailored to your CEFR level (A2–C1)
- Streams tokens in real time — responses appear as they are generated
- Works with Built-in AI, LM Studio, or Ollama

### Built-in AI (no external server required)
- Download a model directly from Settings — runs entirely inside the app
- Powered by [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp bindings for .NET)
- Supported models: Qwen 2.5 (0.5B, 1.5B, 3B, 7B) — recommended: 1.5B for most machines

### Session management
- Every class is saved as a session in a local SQLite database
- Sessions include transcription, detected questions, and duration
- Export any session to Markdown
- Resume or review past sessions from the history panel

### Vocabulary manager
- Add words with translation, definition, and CEFR level
- Search and delete entries

---

## Requirements

| Component | Details |
|-----------|---------|
| **OS** | Windows 10 22H2+ or Windows 11 |
| **Windows Live Captions** | Enable with `Win + Ctrl + L` |
| **AI provider** | Built-in AI (no install), LM Studio, or Ollama — at least one required |

> **No Rust or Node.js required to run the app.** They are only needed to build from source.

---

## Getting started

### Option A — Download the installer (recommended)

Go to [Releases](https://github.com/CharlieCardenasToledo/english-learning-assistant/releases) and download the latest `.exe` installer.

### Option B — Build from source

**Prerequisites:** [Node.js 20+](https://nodejs.org/), [pnpm](https://pnpm.io/), [Rust (stable)](https://rustup.rs/), [.NET 8 SDK](https://dotnet.microsoft.com/download)

```bash
git clone https://github.com/CharlieCardenasToledo/english-learning-assistant.git
cd english-learning-assistant
pnpm install
pnpm tauri dev      # development (hot reload)
pnpm tauri build    # production build
```

`pnpm tauri dev` automatically:
1. Builds the .NET plugin (`EnglishLearningAssistant.TauriPlugIn`)
2. Starts the Next.js dev server
3. Launches the Tauri window

### First run

On the **first launch**, an onboarding wizard guides you through:
1. Choosing an AI provider (Built-in, LM Studio, or Ollama)
2. Downloading a model if using Built-in AI
3. Setting your English level (A2–C1) and username

### Enable Windows Live Captions

Press `Win + Ctrl + L` — a caption bar appears. The app reads from it automatically.

---

## Architecture

```
english-learning-assistant/
├── src/                          # Next.js frontend (React + TypeScript)
│   ├── app/                      # Pages: main, sessions, vocabulary, settings
│   ├── components/               # UI components (TranscriptionPanel, QuestionPanel…)
│   ├── hooks/                    # useCaptionEvents, useTauriInvoke
│   └── types/                    # Shared TypeScript types
├── src-tauri/                    # Tauri v2 shell (Rust)
│   ├── src/                      # Rust entry point + tauri-dotnet-bridge
│   └── capabilities/             # Tauri permission model
├── src-dotnet/
│   └── EnglishLearningAssistant.TauriPlugIn/   # .NET 8 HTTP/SignalR plugin
│       ├── Controllers/          # REST endpoints (session, settings, vocabulary, AI)
│       ├── Providers/            # Translation: LmStudio, Ollama, LocalLlama
│       └── Services/             # BuiltInAiService, CaptionHostedService
└── EnglishLearningAssistant.Core/              # Shared logic (.NET class library)
    ├── Application/Sessions/     # SessionOrchestrator — question detection pipeline
    └── Services/                 # LmStudioService, QuestionDetectionService…
```

The Tauri shell hosts a .NET 8 runtime via [`tauri-dotnet-bridge`](https://crates.io/crates/tauri-dotnet-bridge-host). The frontend communicates with the .NET plugin over a local SignalR connection. Live Captions text is read via UI Automation.

---

## AI providers

| Provider | Setup | Notes |
|----------|-------|-------|
| **Built-in AI** | Download model from Settings | No external server. Best for offline use. |
| **LM Studio** | Start server on port 1234 | Supports any GGUF model |
| **Ollama** | `ollama serve` | Supports any Ollama model |

---

## Troubleshooting

**Transcription panel is empty**
Enable Windows Live Captions with `Win + Ctrl + L`. If nothing appears after 10 seconds, toggle captions off and on.

**Translation not working**
Open Settings → Provider and verify your AI provider is running. For Built-in AI, check that a model is downloaded.

**LM Studio not connecting**
Open LM Studio, load a model, start the local server on port 1234.

**Questions not being detected**
The cascade requires at least 3 words. Short fragments are held until a full sentence is committed.

---

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feat/your-feature`
3. Run the test suite: `dotnet test tests/`
4. Commit with a clear message and open a Pull Request

---

## License

[MIT](LICENSE) — Charlie Cardenas Toledo, 2026
