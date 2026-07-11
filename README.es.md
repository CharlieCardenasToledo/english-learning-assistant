# English Learning Assistant

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-0078D4?logo=windows&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)
[![LM Studio](https://img.shields.io/badge/LM_Studio-IA_local-6B4FBB?logoColor=white)](https://lmstudio.ai/)
[![Licencia: MIT](https://img.shields.io/badge/Licencia-MIT-22c55e.svg)](LICENSE)
[![Plataforma](https://img.shields.io/badge/Plataforma-Windows_10%2F11-0078D4?logo=windows)](https://www.microsoft.com/windows)

> Asistente de IA en tiempo real para estudiantes de inglés. Captura los subtítulos en vivo, traduce al español automáticamente, detecta preguntas del profesor y genera sugerencias de respuesta — todo ejecutándose localmente en tu equipo.

**[English](README.md) | Español**

---

![Demostración de la app](demo.gif)

---

## Cómo funciona

La app corre como una superposición transparente sobre cualquier otra ventana (Zoom, Teams, el navegador). Lee el texto que Windows Live Captions está transcribiendo y:

1. **Muestra** la transcripción en inglés en tiempo real (arriba izquierda)
2. **Traduce** cada oración al español automáticamente (arriba derecha)
3. **Detecta** cuando el profesor hace una pregunta mediante una cascada de 4 niveles (abajo izquierda)
4. **Genera** 3 opciones de respuesta en inglés + español mediante LM Studio (abajo derecha)

Todo corre de forma local — ningún dato sale de tu equipo.

---

## Funcionalidades

### Transcripción y traducción en tiempo real
- Lee Windows Live Captions mediante UI Automation — no requiere procesamiento de audio
- Traduce automáticamente cada oración confirmada al español usando LM Studio
- Respaldo a LibreTranslate (servidor local) para traducciones más rápidas
- Entrada de micrófono como fuente secundaria

### Detección inteligente de preguntas
- **L1** — Signo de interrogación explícito (confianza 0.95)
- **L2** — Palabra WH o verbo auxiliar al inicio (0.80–0.85)
- **L2b** — Preguntas de coletilla como "right?", "isn't it?" (0.85)
- **L3** — Iniciadores indirectos como "I wonder…", "I'd like to know…" (0.70)
- **L4** — Clasificador LM Studio para casos ambiguos (0.75)
- Detección de nombre: aumenta la confianza cuando tu nombre aparece en la oración
- Reintento por fragmentación: combina la oración actual y la anterior si la detección es incierta

### Sugerencias de respuesta con IA
- Genera exactamente 3 opciones numeradas adaptadas a tu nivel MCER (A2–C1)
- Todas las opciones en inglés; traducción al español renderizada en paralelo
- Consciente del contexto: usa las últimas 15 líneas de transcripción como fondo
- Transmite los tokens en tiempo real — sin esperar la respuesta completa

### Gestión de sesiones
- Cada clase se guarda como una sesión en una base de datos SQLite local
- Las sesiones incluyen transcripciones, preguntas detectadas y resúmenes generados por IA
- Retoma una sesión anterior con un solo clic
- Exporta cualquier sesión a Markdown

### Gestor de vocabulario
- Añade palabras con traducción, definición y nivel MCER
- Analiza el texto del portapapeles para extraer vocabulario automáticamente
- Busca y elimina entradas

---

## Requisitos

| Componente | Detalles |
|------------|---------|
| **SO** | Windows 10 22H2+ o Windows 11 (Live Captions lo requiere) |
| **.NET** | Runtime o SDK de .NET 8.0 |
| **LM Studio** | Cualquier versión — debe estar corriendo con un modelo cargado |
| **Windows Live Captions** | Actívalo con `Win + Ctrl + L` |

LM Studio es la única dependencia externa. Whisper es opcional para transcripción de micrófono y no es necesario para el flujo principal.

---

## Inicio rápido

### 1. Clonar y compilar

```bash
git clone https://github.com/CharlieCardenasToledo/english-learning-assistant.git
cd english-learning-assistant
dotnet build
dotnet run
```

### 2. Iniciar LM Studio

Abre LM Studio, carga cualquier modelo (Gemma, Llama, Mistral, etc.) e inicia el servidor local. La app lo detecta automáticamente.

### 3. Activar Windows Live Captions

Presiona `Win + Ctrl + L` — aparece una barra de subtítulos en la parte superior o inferior de tu pantalla. La app la lee mediante UI Automation.

### Primera ejecución vs. ejecuciones posteriores

En la **primera ejecución**, un asistente de configuración verifica tu conexión con LM Studio y te permite descargar opcionalmente un modelo Whisper para entrada de micrófono. En las **ejecuciones posteriores**, el asistente se omite y la app se abre directamente en la superposición.

---

## Diseño de la interfaz

```
┌──────────────────────────────────────────────────────────────────┐
│  LIVE  Sesión 21 may, 14:03          [Asistente] [Vocab] [Mic]  │
├──────────────────────────┬───────────────────────────────────────┤
│  EN  TRANSCRIPCIÓN       │  ES  TRADUCCIÓN                       │
│                          │                                       │
│  El texto de Live        │  The Live Captions text               │
│  Captions aparece aquí   │  appears here automatically           │
│  en tiempo real          │  translated in real time              │
├──────────────────────────┼───────────────────────────────────────┤
│  PREGUNTA DETECTADA      │  OPCIONES DE RESPUESTA                │
│                          │  EN                                   │
│  ❓ 85%  Texto pregunta  │  1. Primera opción...                 │
│                          │  2. Segunda opción...                 │
│  CONTEXTO                │  3. Tercera opción...                 │
│  Contexto de la clase    │  ES                                   │
│                          │  1. Primera opción...                 │
│  [Entrada manual  →]     │  2. Segunda opción...                 │
├──────────────────────────┴───────────────────────────────────────┤
│  [Traducir]  [Limpiar]  [Resumen]                               │
└──────────────────────────────────────────────────────────────────┘
```

La ventana es una superposición transparente a pantalla completa. Arrástrala desde el encabezado para moverla. Ajusta la opacidad en Configuración.

---

## Atajos de teclado

| Atajo | Acción |
|-------|--------|
| `Ctrl + Space` | Alternar panel del Asistente de IA |
| `Ctrl + T` | Traducir toda la transcripción manualmente |
| `Ctrl + M` | Pausar / Reanudar la captura de Live Captions |
| `Ctrl + Shift + C` | Limpiar todos los paneles |
| `Esc` | Cerrar cualquier superposición abierta (Configuración / Sesiones / Asistente) |
| `Win + Ctrl + L` | Alternar Windows Live Captions (atajo del sistema) |

---

## Solución de problemas

**Nada aparece en el panel de transcripción**
Activa Windows Live Captions con `Win + Ctrl + L`. La app lee la ventana de subtítulos mediante UI Automation — si nada aparece después de 10 segundos, intenta desactivar y reactivar los subtítulos.

**LM Studio no conecta**
Abre LM Studio, carga un modelo, inicia el servidor local y luego haz clic en el botón de actualizar junto al selector de modelos en Configuración.

**Pregunta no detectada**
La cascada requiere al menos 3 palabras. Para casos ambiguos, escribe la pregunta manualmente en el cuadro de entrada en la parte inferior del panel "Pregunta detectada" y presiona Enter.

**La traducción no aparece**
La traducción corre automáticamente en cada oración confirmada. Revisa la etiqueta de estado junto a "TRADUCCIÓN" para mensajes de error.

---

## Contribuir

1. Haz un fork del repositorio
2. Crea una rama de funcionalidad: `git checkout -b feat/tu-funcionalidad`
3. Ejecuta las pruebas: `dotnet test WindowsLiveCaptionsReader.Tests/`
4. Haz commit con un mensaje claro y abre un Pull Request

---

## Licencia

[MIT](LICENSE) — Charlie Cardenas Toledo, 2026
