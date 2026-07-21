# Plan de Upgrade: WPF → Tauri + Next.js con TauriDotNetBridge

## Resumen ejecutivo

Reemplazar el frontend WPF por una UI moderna con **Next.js 14 + shadcn/ui + Tailwind CSS + Tauri 2**, conservando íntegramente el backend C# (`EnglishLearningAssistant.Core`). La integración C#↔Tauri se hace mediante **TauriDotNetBridge**, que convierte el Core en una Class Library (.dll) cargada directamente por Tauri en memoria. Sin servidores HTTP, sin puertos, sin procesos externos. Cero reescritura de lógica de negocio.

---

## Cómo funciona TauriDotNetBridge

TauriDotNetBridge es un puente RPC entre Tauri (Rust) y .NET. El Rust host carga la .dll en memoria usando la API de hosting de .NET Core (`netcorehost`). El flujo completo es:

```
Next.js (TypeScript)
  → invoke('dotnet_request', { request: JSON.stringify({ controller, action, data }) })
  → Rust: tauri_dotnet_bridge_host::process_request(request)
  → Router C#: encuentra el Controller por nombre, llama el método por nombre
  → Controller devuelve valor → Router lo serializa → regresa al frontend

C# IHostedService (CaptionHostedService)
  → IEventPublisher.Publish("transcription-line", payload)
  → Rust emit_wrapper → app_handle.emit("transcription-line", payload)
  → Next.js: listen("transcription-line", handler)
```

**Lo que el bridge descubre automáticamente:**
- Todas las DLLs en `{exe}/dotnet/` cuyo nombre termina en `.TauriPlugIn.dll`
- Llama a `IPlugIn.Initialize(services)` para registrar los servicios
- Inicia todos los `IHostedService` registrados en DI

---

## Arquitectura objetivo

```
┌──────────────────────────────────────────────────────────┐
│  Tauri 2 (shell nativo Windows)                          │
│                                                          │
│  ┌───────────────────────────────────┐                   │
│  │  Next.js 14 + React 18            │  ← frontend       │
│  │  shadcn/ui + Tailwind CSS         │                   │
│  │  Framer Motion, Lucide            │                   │
│  │                                   │                   │
│  │  invoke('dotnet_request', ...)    │ ← RPC directo     │
│  │  listen('transcription-line', …) │ ← eventos nativos  │
│  └────────────┬──────────────────────┘                   │
│               │  (sin HTTP, sin puertos)                 │
│  ┌────────────▼──────────────────────┐                   │
│  │  tauri-dotnet-bridge-host (Rust)  │                   │
│  │  carga .dll con netcorehost       │                   │
│  └────────────┬──────────────────────┘                   │
│               │  en memoria                              │
│  ┌────────────▼──────────────────────────────────────┐   │
│  │  EnglishLearningAssistant.TauriPlugIn  (nueva)    │   │
│  │  ├─ PlugIn.cs          → DI registration          │   │
│  │  ├─ SessionController.cs                          │   │
│  │  ├─ VocabularyController.cs                       │   │
│  │  ├─ SettingsController.cs                         │   │
│  │  └─ CaptionHostedService.cs → IEventPublisher     │   │
│  │                                                   │   │
│  │  EnglishLearningAssistant.Core  (sin cambios)     │   │
│  │  ├─ LmStudioService                               │   │
│  │  ├─ QuestionDetectionService                      │   │
│  │  ├─ SessionService                                │   │
│  │  ├─ WhisperService                                │   │
│  │  ├─ VocabularyService                             │   │
│  │  ├─ LibreTranslateService                         │   │
│  │  └─ EF Core → SQLite                              │   │
│  └───────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

**Lo que se conserva sin cambios:**
- `EnglishLearningAssistant.Core` completo (~4,400 líneas)
- Base de datos SQLite + migraciones EF Core
- Windows Live Captions reader (UI Automation)
- NAudio (captura de audio WASAPI)
- Whisper.net, LM Studio, LibreTranslate
- `appsettings.json` (misma configuración)

**Lo que se reemplaza:**
- `WindowsLiveCaptionsReader.csproj` (WPF) → Next.js + Tauri

**Lo que se agrega (nuevo, mínimo):**
- `EnglishLearningAssistant.TauriPlugIn` (Class Library, ~150 líneas)
- Crate `tauri-dotnet-bridge-host` en `Cargo.toml`
- Frontend Next.js

---

## Stack tecnológico

| Categoría | Tecnología |
|-----------|-----------|
| Desktop shell | Tauri 2.x |
| Integración C#↔Tauri | TauriDotNetBridge 2.2.0 (NuGet) + tauri-dotnet-bridge-host 0.8.0 (Cargo) |
| Framework frontend | Next.js 14 (output: export) |
| Lenguaje frontend | TypeScript 5.x |
| UI Components | shadcn/ui (new-york style) |
| Estilos | Tailwind CSS 3.x |
| Primitivos UI | Radix UI |
| Animaciones | Framer Motion |
| Iconos | Lucide React |
| Formularios | React Hook Form + Zod |
| Notificaciones | Sonner |
| Estado global | React Context |
| Streaming en tiempo real | `IEventPublisher` C# → `listen()` Tauri en TypeScript |
| Backend | `EnglishLearningAssistant.Core` (sin cambios) |
| Gestor de paquetes | pnpm |

---

## Estructura del repositorio (final)

```
english_learning_assistant/
│
├── src/                                  # NUEVO: Frontend Next.js
│   ├── app/
│   │   ├── page.tsx                      # Overlay principal
│   │   ├── sessions/page.tsx
│   │   ├── vocabulary/page.tsx
│   │   ├── settings/page.tsx
│   │   └── layout.tsx
│   ├── components/
│   │   ├── ui/                           # shadcn/ui generados
│   │   ├── TranscriptionPanel/
│   │   ├── TranslationPanel/
│   │   ├── QuestionPanel/
│   │   ├── SessionControls/
│   │   ├── VocabularyManager/
│   │   └── Settings/
│   ├── hooks/
│   │   ├── useTauriInvoke.ts
│   │   ├── useCaptionEvents.ts
│   │   ├── useSession.ts
│   │   └── useVocabulary.ts
│   └── types/index.ts
│
├── src-tauri/                            # MODIFICADO
│   ├── src/
│   │   ├── lib.rs                        # lógica Tauri (dotnet_request + register_emit)
│   │   └── main.rs                       # entry point (llama lib::run())
│   ├── Cargo.toml
│   └── tauri.conf.json
│
├── src-dotnet/                           # NUEVO
│   ├── EnglishLearningAssistant.TauriPlugIn/
│   │   ├── EnglishLearningAssistant.TauriPlugIn.csproj
│   │   ├── PlugIn.cs
│   │   ├── SessionController.cs
│   │   ├── VocabularyController.cs
│   │   ├── SettingsController.cs
│   │   └── CaptionHostedService.cs
│   └── src-dotnet.sln
│
├── EnglishLearningAssistant.Core/        # SIN CAMBIOS
├── EnglishLearningAssistant.Tests/       # SIN CAMBIOS
├── WindowsLiveCaptionsReader.csproj      # deprecar al finalizar
│
├── package.json
├── pnpm-lock.yaml
├── tsconfig.json
├── next.config.js
├── tailwind.config.js
└── components.json
```

---

## Fases de implementación

### Fase 1 — Crear el Plugin C#

**Nombre obligatorio:** `EnglishLearningAssistant.TauriPlugIn` (el bridge busca DLLs que terminen en `.TauriPlugIn.dll`).

#### `EnglishLearningAssistant.TauriPlugIn.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- Ruta verificada del repo oficial de TauriDotNetBridge -->
    <OutputPath>..\..\src-tauri\target\$(Configuration)\dotnet</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\EnglishLearningAssistant.Core\EnglishLearningAssistant.Core.csproj" />
    <PackageReference Include="TauriDotNetBridge" Version="2.2.0" />
  </ItemGroup>
</Project>
```

> **Nota `net8.0-windows`:** El Core usa APIs Windows-only (UI Automation, NAudio WASAPI), por lo que el plugin hereda `net8.0-windows`. El bridge Rust carga la DLL con `netcorehost`, que en Windows soporta tanto `net8.0` como `net8.0-windows` sin diferencia.

#### `PlugIn.cs`

```csharp
namespace EnglishLearningAssistant.TauriPlugIn;

using Microsoft.Extensions.DependencyInjection;
using TauriDotNetBridge.Contracts;

public class PlugIn : IPlugIn
{
    public void Initialize(IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(GetDbPath()));
        services.AddSingleton<LmStudioService>();
        services.AddSingleton<QuestionDetectionService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<VocabularyService>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<LibreTranslateService>();
        // IHostedService de TauriDotNetBridge.Contracts — no de Microsoft.Extensions.Hosting
        services.AddSingleton<IHostedService, CaptionHostedService>();
        services.AddSingleton<SessionController>();
        services.AddSingleton<VocabularyController>();
        services.AddSingleton<SettingsController>();
    }

    private static string GetDbPath() =>
        // Mismo path que usa App.xaml.cs actualmente
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EnglishLearningAssistant", "app.db");
}
```

#### `CaptionHostedService.cs`

```csharp
namespace EnglishLearningAssistant.TauriPlugIn;

using TauriDotNetBridge.Contracts;

// IHostedService de TauriDotNetBridge.Contracts — solo tiene StartAsync, sin StopAsync
public class CaptionHostedService(
    IEventPublisher publisher,
    QuestionDetectionService questionDetection,
    LmStudioService lmStudio) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Mueve aquí la lógica del MainWindow.xaml.cs:
        // - Iniciar CaptionReader (UI Automation)
        // - Por cada línea transcrita:
        publisher.Publish("transcription-line", new { text = "...", timestamp = DateTime.Now });
        // - Por cada traducción lista:
        publisher.Publish("translation-ready", new { original = "...", translated = "..." });
        // - Pregunta detectada:
        publisher.Publish("question-detected", new { text = "...", level = 2, confidence = 0.85 });
        // - Chunk de respuesta LLM (streaming):
        publisher.Publish("answer-chunk", new { chunk = "..." });
        // - Respuesta completa:
        publisher.Publish("answer-complete", new { answer = "..." });
    }
}
```

#### `SessionController.cs`

```csharp
namespace EnglishLearningAssistant.TauriPlugIn;

// Los controllers devuelven valores directamente — el Router los serializa.
// No usar RouteResponse (es internal de TauriDotNetBridge).
public class SessionController(SessionService sessions)
{
    public async Task<object> Start() => await sessions.StartNewSession();
    public async Task<object> Stop()  => await sessions.StopCurrentSession();
    public async Task<object> List()  => await sessions.GetAllSessionsAsync();
    public async Task<object> Export(ExportRequest req) =>
        await sessions.ExportToMarkdown(req.Id);
}
```

**Estimación:** 3-4 días

---

### Fase 2 — Modificar Rust

#### `Cargo.toml`

```toml
[package]
name = "english-learning-assistant"
version = "2.0.0"
edition = "2021"

[lib]
name = "english_learning_assistant_lib"
crate-type = ["staticlib", "cdylib", "rlib"]

[build-dependencies]
tauri-build = { version = "2", features = [] }

[dependencies]
tauri            = { version = "2", features = [] }
tauri-plugin-shell = "2"                           # requerido por el bridge
serde            = { version = "1", features = ["derive"] }
serde_json       = "1"
tauri-dotnet-bridge-host = "0.8.0"
```

#### `src/lib.rs`

```rust
use tauri::Emitter;

#[tauri::command]
fn dotnet_request(request: &str) -> String {
    tauri_dotnet_bridge_host::process_request(request)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .invoke_handler(tauri::generate_handler![dotnet_request])
        .setup(|app| {
            let app_handle = app.handle().clone();
            tauri_dotnet_bridge_host::register_emit(move |event_name, payload| {
                app_handle
                    .emit(event_name, payload)
                    .expect(&format!("Failed to emit event {}", event_name));
            });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
```

#### `src/main.rs`

```rust
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
fn main() { english_learning_assistant_lib::run(); }
```

#### `tauri.conf.json`

```json
{
  "$schema": "https://schema.tauri.app/config/2",
  "productName": "English Learning Assistant",
  "version": "2.0.0",
  "identifier": "com.englishlearning.assistant",
  "build": {
    "beforeDevCommand": "dotnet build src-dotnet/EnglishLearningAssistant.TauriPlugIn/EnglishLearningAssistant.TauriPlugIn.csproj && pnpm dev",
    "beforeBuildCommand": "dotnet publish -c Release src-dotnet/EnglishLearningAssistant.TauriPlugIn/EnglishLearningAssistant.TauriPlugIn.csproj && pnpm build",
    "devUrl": "http://localhost:3000",
    "frontendDist": "../out"
  },
  "app": {
    "windows": [{
      "title": "English Learning Assistant",
      "width": 900,
      "height": 600,
      "resizable": true,
      "transparent": true,
      "decorations": false,
      "alwaysOnTop": true
    }],
    "security": { "csp": null }
  },
  "bundle": {
    "active": true,
    "targets": ["nsis", "msi"],
    "resources": {
      "./target/Release/dotnet/*": "dotnet/"
    }
  }
}
```

**Estimación:** 1 día

---

### Fase 3 — Crear el frontend Next.js

**Diseño visual de la ventana principal:**

```
┌─────────────────────────────────────────────┐
│  English Learning Assistant     [─][□][✕]   │  ← data-tauri-drag-region
├─────────────────┬───────────────────────────┤
│  TRANSCRIPCIÓN  │  TRADUCCIÓN               │
│  (inglés)       │  (español)                │
│                 │                           │
│  "Hello, what  │  "Hola, ¿cuál es tu       │
│   is your name │   nombre hoy?"             │
│   today?"       │                           │
│  [scroll live]  │  [scroll live]            │
├─────────────────┴───────────────────────────┤
│  PREGUNTA DETECTADA  L2 · 85%               │
│  "What is your name today?"                 │
│                                             │
│  RESPUESTA (streaming en vivo):             │
│  "My name is... [cursor parpadeando]"       │
├─────────────────────────────────────────────┤
│  [▶ Iniciar]  [⏹ Detener]  [📤 Exportar]   │
│  Sesión: 00:12:34    Preguntas: 3           │
└─────────────────────────────────────────────┘
```

#### Hooks clave

```typescript
// hooks/useCaptionEvents.ts
import { listen } from '@tauri-apps/api/event';
import { useEffect } from 'react';

export function useCaptionEvents(handlers: {
  onTranscription: (payload: { text: string; timestamp: string }) => void;
  onTranslation:   (payload: { original: string; translated: string }) => void;
  onQuestion:      (payload: { text: string; level: number; confidence: number }) => void;
  onAnswerChunk:   (payload: { chunk: string }) => void;
  onAnswerComplete:(payload: { answer: string }) => void;
}) {
  useEffect(() => {
    const subs = [
      listen('transcription-line', e => handlers.onTranscription(e.payload as any)),
      listen('translation-ready',  e => handlers.onTranslation(e.payload as any)),
      listen('question-detected',  e => handlers.onQuestion(e.payload as any)),
      listen('answer-chunk',       e => handlers.onAnswerChunk(e.payload as any)),
      listen('answer-complete',    e => handlers.onAnswerComplete(e.payload as any)),
    ];
    return () => { subs.forEach(p => p.then(fn => fn())); };
  }, []);
}

// hooks/useTauriInvoke.ts
import { invoke } from '@tauri-apps/api/core';

export async function dotnetRequest<T>(
  controller: string,
  action: string,
  data?: unknown
): Promise<T> {
  const raw = await invoke<string>('dotnet_request', {
    request: JSON.stringify({ controller, action, data })
  });
  const res = JSON.parse(raw);
  if (res.ErrorMessage) throw new Error(res.ErrorMessage);
  return res.Data as T;
}
```

**Características visuales:**
- Ventana semi-transparente, opacidad ajustable en settings
- Tema oscuro por defecto (shadcn/ui dark mode)
- Framer Motion para animaciones de entrada por línea
- Auto-scroll en paneles de transcripción y traducción
- Badge de color por nivel de pregunta: L1=verde, L2=amarillo, L3=naranja, L4=rojo

**Estimación:** 5-7 días

---

### Fase 4 — Migrar funcionalidades WPF

| Funcionalidad WPF | Equivalente nuevo |
|-------------------|-----------------|
| Ventana transparente siempre encima | `transparent: true`, `alwaysOnTop: true` en tauri.conf.json |
| Barra de título draggable | `data-tauri-drag-region` en div custom |
| SetupWindow | Ruta `/settings` en Next.js |
| VocabularyWindow | Ruta `/vocabulary` en Next.js |
| Hotkeys globales (start/stop) | `tauri-plugin-global-shortcut` |
| `AllowsTransparency` WPF | `transparent: true` + `body { background: transparent }` |
| `Topmost` WPF | `getCurrentWindow().setAlwaysOnTop(true)` |
| Tray icon | `tauri-plugin-tray-icon` |

**Estimación:** 2 días

---

### Fase 5 — Testing y empaquetado

- Verificar que `EnglishLearningAssistant.Tests` sigue pasando (`dotnet test`)
- Prueba manual del flujo completo: arranque → captions → transcripción → traducción → pregunta → respuesta LLM
- Build de producción: `pnpm tauri:build`
- El instalador NSIS/MSI incluye: Tauri shell + Next.js estático + `dotnet/` con las DLLs

**Estimación:** 2 días

---

## Cronograma estimado

| Fase | Tarea | Días |
|------|-------|------|
| 1 | Plugin C# (controllers + IHostedService) | 3-4 |
| 2 | Rust: lib.rs, Cargo.toml, tauri.conf.json | 1 |
| 3 | Frontend Next.js + shadcn/ui | 5-7 |
| 4 | Migración funcionalidades WPF | 2 |
| 5 | Testing + empaquetado | 2 |
| **Total** | | **13-16 días** |

---

## Riesgos y mitigaciones

| Riesgo | Impacto | Mitigación |
|--------|---------|------------|
| **Bug Tauri 2: `transparent + decorations: false` en Windows** | Alto | Issues [#8308](https://github.com/tauri-apps/tauri/issues/8308) y [#8406](https://github.com/tauri-apps/tauri/issues/8406) — probar primero antes de comprometerse; fallback: `decorations: true` con titlebar custom en React |
| Windows Live Captions es Windows-only | Bajo (app ya era Windows-only) | `CaptionHostedService` usa UI Automation igual que antes |
| EF Core / ruta de DB al cambiar de contexto de ejecución | Bajo | Usar ruta absoluta con `Environment.SpecialFolder.ApplicationData` en `PlugIn.cs` (ya hecho en el ejemplo de arriba) |
| `net8.0-windows` vs `net8.0` en el bridge | Bajo | `netcorehost` en Windows carga DLLs `net8.0-windows` sin problema |

---

## Lo que NO cambia

- `EnglishLearningAssistant.Core.csproj` — sin una sola línea modificada
- Base de datos SQLite — misma ruta, mismos modelos EF Core
- `appsettings.json` — misma configuración
- Lógica de detección de preguntas (4 niveles)
- Integración con LM Studio y streaming de respuestas
- Transcripción con Whisper.net
- Exportación de sesiones a Markdown
- `EnglishLearningAssistant.Tests` — siguen funcionando

---

## Prerrequisitos

- [ ] Rust + Tauri CLI (`cargo install tauri-cli`)
- [ ] pnpm (`npm install -g pnpm`)
- [ ] Node.js 20+
- [ ] `dotnet test` pasa en verde
- [ ] Backup de la DB SQLite antes de empezar
- [ ] Probar `transparent + decorations: false` en Tauri 2 **antes** de Fase 3

---

## Fuentes verificadas con código fuente real

- [TauriDotNetBridge — GitHub (plainionist)](https://github.com/plainionist/TauriDotNetBridge)
- [Sample.TauriPlugIn.csproj — ejemplo real](https://github.com/plainionist/TauriDotNetBridge/blob/main/src/tauri-dotnet-sample/src-dotnet/Sample.TauriPlugIn/Sample.TauriPlugIn.csproj)
- [lib.rs — ejemplo real](https://github.com/plainionist/TauriDotNetBridge/blob/main/src/tauri-dotnet-sample/src-tauri/src/lib.rs)
- [tauri.conf.json — ejemplo real](https://github.com/plainionist/TauriDotNetBridge/blob/main/src/tauri-dotnet-sample/src-tauri/tauri.conf.json)
- [TauriDotNetBridge — NuGet v2.2.0](https://www.nuget.org/packages/TauriDotNetBridge)
- [tauri-dotnet-bridge-host — Cargo v0.8.0](https://lib.rs/crates/tauri-dotnet-bridge-host)
- [Tauri 2 — Window Customization](https://v2.tauri.app/learn/window-customization/)
- [Bug Tauri 2: transparent + decorations #8308](https://github.com/tauri-apps/tauri/issues/8308)
- [Bug Tauri 2: transparent + decorations #8406](https://github.com/tauri-apps/tauri/issues/8406)
