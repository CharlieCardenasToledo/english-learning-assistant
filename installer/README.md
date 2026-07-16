# 🎓 Windows Live Captions Reader - English Learning Assistant

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-0078D4?logo=windows)](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)
[![Ollama](https://img.shields.io/badge/Ollama-AI-000000?logo=ai)](https://ollama.ai/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **AI-powered English learning assistant** that captures live conversations, provides real-time translations, and generates intelligent response suggestions for B1 level students.

**English | [Español](README.es.md)**

---

## 📋 Table of Contents

- [Features](#-features)
- [Prerequisites](#-prerequisites)
- [Installation](#-installation)
- [Usage](#-usage)
- [How It Works](#-how-it-works)
- [Configuration](#-configuration)
- [Keyboard Shortcuts](#-keyboard-shortcuts)
- [Troubleshooting](#-troubleshooting)
- [Contributing](#-contributing)
- [License](#-license)

---

## ✨ Features

### 🎤 **Multi-Source Audio Capture**
- **Windows Live Captions**: Automatically captures system-wide subtitles
- **Microphone Input**: Records your spoken responses via speech recognition
- **Browser Integration**: Extracts text from web pages using Chrome DevTools Protocol (CDP)

### 🤖 **AI-Powered Learning Assistant**
- **Intelligent Response Suggestions**: Generates 3 contextual responses (2-4 sentences each)
- **Grammar Explanations**: Provides Spanish explanations of grammar usage
- **Vocabulary Support**: Highlights key words with definitions
- **Spanish Translations**: Full translations for better comprehension
- **B1 Level Optimized**: Responses tailored for CEFR B1 proficiency

### 📊 **Conversation Management**
- **Real-time Translation**: Instant Spanish translations of captured English text
- **Conversation History**: Tracks teacher and student interactions
- **Context-Aware**: Uses last 5 conversations for intelligent suggestions
- **Class Summaries**: Generates structured markdown summaries with key topics, vocabulary, and action items

### 🎨 **Modern UI/UX**
- **Glassmorphism Design**: Beautiful, modern interface with transparency effects
- **Customizable Opacity**: Adjustable window transparency
- **Always-on-Top Mode**: Stays visible during exams or practice sessions
- **Resizable Windows**: Flexible layout for different screen sizes
- **Dark Theme**: Eye-friendly design for extended use

---

## 📦 Prerequisites

### Required Software

1. **Windows 10/11** (with Live Captions support)
2. **.NET 8.0 SDK** or later
   ```bash
   winget install Microsoft.DotNet.SDK.8
   ```

3. **Ollama** (Local AI server)
   ```bash
   # Download from https://ollama.ai/download
   # Or install via winget:
   winget install Ollama.Ollama
   ```

4. **Ollama Model** (llama3.2 recommended)
   ```bash
   ollama pull llama3.2
   ```

### Optional (for Browser Integration)

5. **Google Chrome** (for CDP integration)
6. **Selenium WebDriver** (included via NuGet)

---

## 📥 Download & Install (For End Users)

### Quick Install (Recommended for Non-Technical Users)

**Option 1: Automatic Installer (PowerShell)**
1. Download the project ZIP from GitHub
2. Extract to a folder
3. Right-click `INSTALAR.ps1` → "Run with PowerShell"
4. Follow the on-screen instructions

**Option 2: Portable Installer (Coming Soon)**
- Download `EnglishLearningAssistant-v1.0-Portable.zip` from [Releases](https://github.com/CharlieCardenasToledo/WindowsLiveCaptionsRead/releases)
- Extract and run `INSTALAR.bat`

**Option 3: Self-Extracting Installer (Coming Soon)**
- Download `EnglishLearningAssistant-v1.0-Setup.exe` from [Releases](https://github.com/CharlieCardenasToledo/WindowsLiveCaptionsRead/releases)
- Run the installer

📖 **Detailed Installation Guide**: See [INSTALACION.md](INSTALACION.md)

---

## 🚀 Installation (For Developers)

### 1. Clone the Repository
```bash
git clone https://github.com/YOUR_USERNAME/WindowsLiveCaptionsReader.git
cd WindowsLiveCaptionsReader
```

### 2. Restore Dependencies
```bash
dotnet restore
```

### 3. Build the Project
```bash
dotnet build
```

### 4. Run the Application
```bash
dotnet run
```

---

## 📖 Usage

### Basic Workflow

1. **Start Ollama Server**
   ```bash
   ollama serve
   ```
   *(The app will attempt to start it automatically if not running)*

2. **Enable Windows Live Captions**
   - Press `Win + Ctrl + L` to toggle Live Captions
   - Or go to Settings → Accessibility → Captions

3. **Launch the Application**
   ```bash
   dotnet run
   ```

4. **Start Capturing**
   - The app automatically listens to Live Captions
   - Enable microphone with the 🎤 button to capture your speech
   - Use the 🌐 button to scan browser content

5. **Get AI Assistance**
   - Press `Ctrl + Space` to open the AI Assistant
   - View 3 intelligent response suggestions with explanations
   - Click "Refresh Analysis" to regenerate suggestions

### Advanced: Browser Integration (High-Fidelity Mode)

For better browser text extraction:

1. **Run Chrome in Debug Mode**
   ```bash
   .\LANZAR_MODO_EXAMEN.bat
   ```
   *(This launches Chrome with remote debugging enabled)*

2. **Click the 🌐 Browser Scan button**
   - The app will connect via CDP for accurate DOM extraction
   - Fallback to UI Automation if CDP is unavailable

---

## 🔧 How It Works

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                     Main Window (WPF)                       │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │ Live Captions│  │  Microphone  │  │   Browser    │     │
│  │   Reader     │  │   Capture    │  │   Scanner    │     │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘     │
│         │                  │                  │             │
│         └──────────────────┴──────────────────┘             │
│                            │                                │
│                    ┌───────▼────────┐                       │
│                    │ Ollama Service │                       │
│                    │  (Translation  │                       │
│                    │  & Suggestions)│                       │
│                    └───────┬────────┘                       │
│                            │                                │
│                    ┌───────▼────────┐                       │
│                    │   Assistant    │                       │
│                    │     Window     │                       │
│                    └────────────────┘                       │
└─────────────────────────────────────────────────────────────┘
```

### AI Response Generation

The assistant uses a sophisticated prompt to generate educational responses:

```
Context: Last 5 conversations (Teacher/Student)
↓
Ollama AI (llama3.2)
↓
3 Response Suggestions:
  ├─ 2-4 complete sentences
  ├─ 📝 Grammar explanation (Spanish)
  ├─ 📚 Key vocabulary with definitions
  └─ 🇪🇸 Full Spanish translation
```

---

## ⚙️ Configuration

### Change AI Model

Edit `MainWindow.xaml.cs` (line 38):
```csharp
_translator = new OllamaService("llama3.2"); // Change model here
```

Available models:
- `llama3.2` (default, recommended)
- `llama3.1`
- `deepseek-r1`
- Any Ollama-compatible model

### Adjust Response Temperature

Edit `Services/OllamaService.cs` (line 239):
```csharp
temperature = 0.7  // Higher = more creative, Lower = more focused
```

### Modify Context Window

Edit `MainWindow.xaml.cs` (line 487):
```csharp
var recentItems = History.TakeLast(5)  // Change 5 to desired number
```

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl + Space` | Toggle AI Assistant |
| `Esc` | Close Settings or Overlay |
| `Win + Ctrl + L` | Toggle Windows Live Captions |

---

## 🐛 Troubleshooting

### Ollama Not Connecting

**Error**: "Could not start Ollama"

**Solution**:
```bash
# Manually start Ollama
ollama serve

# Verify it's running
curl http://localhost:11434
```

### Microphone Not Working

**Error**: "No Speech Recognizer found"

**Solution**:
1. Install English (US) language pack in Windows
2. Go to Settings → Time & Language → Language
3. Add "English (United States)"
4. Download speech recognition

### Browser Scan Returns Empty

**Error**: "CDP Empty Result" or "Legacy Scan Failed"

**Solution**:
1. Close all Chrome instances
2. Run `LANZAR_MODO_EXAMEN.bat` from project folder
3. Navigate to your exam/webpage
4. Click 🌐 Browser Scan button

### AI Responses Are Too Generic

**Issue**: Suggestions don't match conversation context

**Solution**:
- Ensure conversation history has at least 3-5 exchanges
- Don't clear history during active conversations
- Check that Ollama is using the correct model

---

## 🤝 Contributing

Contributions are welcome! Please follow these steps:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 🙏 Acknowledgments

- **Ollama** - Local AI inference
- **Windows Live Captions** - System-wide speech recognition
- **Selenium WebDriver** - Browser automation
- **NAudio** - Audio capture library

---

## 📧 Contact

For questions or support, please open an issue on GitHub.

---

**Made with ❤️ for English learners preparing for B1 certification**
