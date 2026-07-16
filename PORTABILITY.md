# 📱 Guía de Portabilidad — English Learning Assistant Core

Este documento detalla la arquitectura desacoplada del proyecto y explica cómo reutilizar el núcleo de lógica de negocio y base de datos (`EnglishLearningAssistant.Core`) para portarlo a plataformas móviles como **.NET MAUI**, **Android** o **iOS**.

---

## 🏗️ Arquitectura Desacoplada

Tras la evolución de la Fase 11, la solución está estructurada en proyectos independientes:

1. **`EnglishLearningAssistant.Core` (Biblioteca de clases `net8.0-windows`):**
   * **Capa de Dominio y Modelos:** Contiene las definiciones de base de datos de SQLite (`Session`, `TranscriptionEntry`, `DetectedQuestion`, `VocabularyItem`, `TranslationCacheEntry`).
   * **Capa de Datos (`AppDbContext`):** Persistencia e inicialización física de SQLite.
   * **Capa de Servicios de Negocio:**
     * `WhisperService` / `WhisperProvider` (Motor de transcripción local Whisper).
     * `LmStudioService` (Conexión e inferencia con LLM local/servidores OpenAI API).
     * `LibreTranslateService` / `FallbackTranslationProvider` (Proveedor de traducción local y caché en SQLite).
     * `QuestionDetectionService` (Clasificación de preguntas por reglas de ASR y AI).
     * `VocabularyService` y `FileTranscriptionService` (Importación offline y análisis).
     * `HardwareDetector` (Detección de recursos de CPU, RAM, GPU y disco).

2. **`WindowsLiveCaptionsReader` (Aplicación WPF Desktop):**
   * Contiene la interfaz de usuario en WPF (pantallas, sidebar, temas visuales, estilos premium).
   * Contiene capturadores de audio dependientes de la API de audio de Windows (`AudioCaptureService` y `SystemAudioCaptureService` usando NAudio).
   * Contiene la automatización de accesibilidad de Windows para subtítulos (`CaptionReader` y `BrowserCaptureService` vía UIA COM Client).

---

## 📲 Portabilidad a Móviles (.NET MAUI / iOS / Android)

Para llevar la aplicación a dispositivos móviles, el núcleo de procesamiento de IA y persistencia (`EnglishLearningAssistant.Core`) se puede reutilizar directamente siguiendo estas directrices:

### 1. Reemplazo del Capturador de Audio
Las clases `AudioCaptureService` y `SystemAudioCaptureService` dependen de la API nativa de Windows Multimedia (WinMM) y WASAPI a través de NAudio. En plataformas móviles, debes reemplazar la captura de audio por APIs nativas:
* **Android:** Utiliza `Android.Media.AudioRecord` para capturar el micrófono a 16000 Hz, Mono, 16 bits PCM.
* **iOS:** Utiliza `AVFoundation.AVAudioEngine` o `AudioQueue` para obtener los buffers de audio del micrófono.
* **.NET MAUI:** Puedes implementar una interfaz `IAudioRecorder` y registrar su implementación multiplataforma nativa. Una vez obtenidos los bytes de audio o el archivo temporal `.wav` resampleado, se pasan directamente a `WhisperService` para su transcripción.

### 2. Eliminación de Dependencias de Windows en la Compilación Core
Actualmente, `EnglishLearningAssistant.Core.csproj` apunta a `net8.0-windows` debido a que contiene los importadores de `FileTranscriptionService` y `LiveWhisperProvider` que usan Windows Media Foundation nativo.
Para una portabilidad multiplataforma pura (`net8.0` standard):
1. **Desacoplar importadores:** Mueve `FileTranscriptionService.cs` y `LiveWhisperProvider.cs` al proyecto ejecutable Windows, o bien:
2. **Multi-targeting:** Modifica el `.csproj` para soportar múltiples plataformas y utiliza directivas de preprocesador en el código:
   ```xml
   <TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>
   ```
   En el código, envuelve los componentes dependientes de Windows con directivas de compilación condicional:
   ```csharp
   #if WINDOWS
       // Lógica nativa de Windows (NAudio, Media Foundation)
   #endif
   ```

### 3. Inicialización del DbContext de SQLite
El motor de base de datos EF Core SQLite funciona de forma totalmente nativa en Android e iOS. En móviles, solo debes asegurarte de configurar el path de la base de datos apuntando a un directorio de almacenamiento persistente de la sandbox de la app:
* **Android:** `Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "english_assistant.db")`
* **iOS:** `Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "..", "Library", "english_assistant.db")`

### 4. Transcripción con Whisper en Dispositivos Móviles
`Whisper.net` compila de manera cruzada para arquitecturas ARM64 (utilizadas por la gran mayoría de teléfonos móviles modernos). En plataformas móviles:
* Reemplaza el runtime por `Whisper.net.Runtime.Clang` o el runtime nativo del sistema para optimizar la aceleración por hardware (por ejemplo, CoreML en iOS mediante Neural Engine).
* Utiliza modelos muy ligeros (como `tiny.en` o `base.en` cuantizados a 4 bits) para minimizar el consumo de batería y memoria RAM.
