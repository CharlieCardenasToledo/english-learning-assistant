# 🎓 Windows Live Captions Reader - Asistente de Aprendizaje de Inglés

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/WPF-Windows-0078D4?logo=windows)](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)
[![Ollama](https://img.shields.io/badge/Ollama-AI-000000?logo=ai)](https://ollama.ai/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **Asistente de aprendizaje de inglés con IA** que captura conversaciones en vivo, proporciona traducciones en tiempo real y genera sugerencias inteligentes de respuestas para estudiantes de nivel B1.

**[English](README.md) | Español**

---

## 📋 Tabla de Contenidos

- [Características](#-características)
- [Requisitos Previos](#-requisitos-previos)
- [Instalación](#-instalación)
- [Uso](#-uso)
- [Cómo Funciona](#-cómo-funciona)
- [Configuración](#-configuración)
- [Atajos de Teclado](#-atajos-de-teclado)
- [Solución de Problemas](#-solución-de-problemas)
- [Contribuir](#-contribuir)
- [Licencia](#-licencia)

---

## ✨ Características

### 🎤 **Captura de Audio Multi-Fuente**
- **Subtítulos en Vivo de Windows**: Captura automáticamente los subtítulos del sistema
- **Entrada de Micrófono**: Graba tus respuestas habladas mediante reconocimiento de voz
- **Integración con Navegador**: Extrae texto de páginas web usando Chrome DevTools Protocol (CDP)

### 🤖 **Asistente de Aprendizaje con IA**
- **Sugerencias Inteligentes de Respuestas**: Genera 3 respuestas contextuales (2-4 oraciones cada una)
- **Explicaciones Gramaticales**: Proporciona explicaciones en español sobre el uso gramatical
- **Soporte de Vocabulario**: Resalta palabras clave con definiciones
- **Traducciones al Español**: Traducciones completas para mejor comprensión
- **Optimizado para Nivel B1**: Respuestas adaptadas para nivel de competencia CEFR B1

### 📊 **Gestión de Conversaciones**
- **Traducción en Tiempo Real**: Traducciones instantáneas al español del texto capturado en inglés
- **Historial de Conversaciones**: Rastrea las interacciones entre profesor y estudiante
- **Consciente del Contexto**: Usa las últimas 5 conversaciones para sugerencias inteligentes
- **Resúmenes de Clase**: Genera resúmenes estructurados en markdown con temas clave, vocabulario y tareas

### 🎨 **UI/UX Moderna**
- **Diseño Glassmorphism**: Interfaz hermosa y moderna con efectos de transparencia
- **Opacidad Personalizable**: Transparencia de ventana ajustable
- **Modo Siempre Visible**: Permanece visible durante exámenes o sesiones de práctica
- **Ventanas Redimensionables**: Diseño flexible para diferentes tamaños de pantalla
- **Tema Oscuro**: Diseño amigable para la vista para uso prolongado

---

## 📦 Requisitos Previos

### Software Requerido

1. **Windows 10/11** (con soporte para Subtítulos en Vivo)
2. **.NET 8.0 SDK** o posterior
   ```bash
   winget install Microsoft.DotNet.SDK.8
   ```

3. **Ollama** (Servidor de IA local)
   ```bash
   # Descargar desde https://ollama.ai/download
   # O instalar vía winget:
   winget install Ollama.Ollama
   ```

4. **Modelo de Ollama** (llama3.2 recomendado)
   ```bash
   ollama pull llama3.2
   ```

### Opcional (para Integración con Navegador)

5. **Google Chrome** (para integración CDP)
6. **Selenium WebDriver** (incluido vía NuGet)

---

## 📥 Descargar e Instalar (Para Usuarios Finales)

### Instalación Rápida (Recomendado para Usuarios No Técnicos)

**Opción 1: Instalador Automático (PowerShell)**
1. Descarga el proyecto ZIP desde GitHub
2. Extrae en una carpeta
3. Clic derecho en `INSTALAR.ps1` → "Ejecutar con PowerShell"
4. Sigue las instrucciones en pantalla

**Opción 2: Instalador Portable (Próximamente)**
- Descarga `EnglishLearningAssistant-v1.0-Portable.zip` desde [Releases](https://github.com/CharlieCardenasToledo/WindowsLiveCaptionsRead/releases)
- Extrae y ejecuta `INSTALAR.bat`

**Opción 3: Instalador Autoextraíble (Próximamente)**
- Descarga `EnglishLearningAssistant-v1.0-Setup.exe` desde [Releases](https://github.com/CharlieCardenasToledo/WindowsLiveCaptionsRead/releases)
- Ejecuta el instalador

📖 **Guía de Instalación Detallada**: Ver [INSTALACION.md](INSTALACION.md)

---

## 🚀 Instalación (Para Desarrolladores)

### 1. Clonar el Repositorio
```bash
git clone https://github.com/CharlieCardenasToledo/WindowsLiveCaptionsRead.git
cd WindowsLiveCaptionsReader
```

### 2. Restaurar Dependencias
```bash
dotnet restore
```

### 3. Compilar el Proyecto
```bash
dotnet build
```

### 4. Ejecutar la Aplicación
```bash
dotnet run
```

---

## 📖 Uso

### Flujo de Trabajo Básico

1. **Iniciar el Servidor Ollama**
   ```bash
   ollama serve
   ```
   *(La aplicación intentará iniciarlo automáticamente si no está ejecutándose)*

2. **Habilitar Subtítulos en Vivo de Windows**
   - Presiona `Win + Ctrl + L` para activar/desactivar Subtítulos en Vivo
   - O ve a Configuración → Accesibilidad → Subtítulos

3. **Lanzar la Aplicación**
   ```bash
   dotnet run
   ```

4. **Comenzar a Capturar**
   - La aplicación escucha automáticamente los Subtítulos en Vivo
   - Habilita el micrófono con el botón 🎤 para capturar tu voz
   - Usa el botón 🌐 para escanear contenido del navegador

5. **Obtener Asistencia de IA**
   - Presiona `Ctrl + Espacio` para abrir el Asistente de IA
   - Ve 3 sugerencias inteligentes de respuestas con explicaciones
   - Haz clic en "Refresh Analysis" para regenerar sugerencias

### Avanzado: Integración con Navegador (Modo Alta Fidelidad)

Para mejor extracción de texto del navegador:

1. **Ejecutar Chrome en Modo Debug**
   ```bash
   .\LANZAR_MODO_EXAMEN.bat
   ```
   *(Esto lanza Chrome con depuración remota habilitada)*

2. **Haz clic en el botón 🌐 Escanear Navegador**
   - La aplicación se conectará vía CDP para extracción precisa del DOM
   - Recurre a UI Automation si CDP no está disponible

---

## 🔧 Cómo Funciona

### Resumen de Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│                  Ventana Principal (WPF)                    │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │  Subtítulos  │  │  Captura de  │  │  Escáner de  │     │
│  │   en Vivo    │  │  Micrófono   │  │  Navegador   │     │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘     │
│         │                  │                  │             │
│         └──────────────────┴──────────────────┘             │
│                            │                                │
│                    ┌───────▼────────┐                       │
│                    │ Servicio Ollama│                       │
│                    │  (Traducción   │                       │
│                    │  y Sugerencias)│                       │
│                    └───────┬────────┘                       │
│                            │                                │
│                    ┌───────▼────────┐                       │
│                    │    Ventana     │                       │
│                    │   Asistente    │                       │
│                    └────────────────┘                       │
└─────────────────────────────────────────────────────────────┘
```

### Generación de Respuestas de IA

El asistente usa un prompt sofisticado para generar respuestas educativas:

```
Contexto: Últimas 5 conversaciones (Profesor/Estudiante)
↓
IA Ollama (llama3.2)
↓
3 Sugerencias de Respuesta:
  ├─ 2-4 oraciones completas
  ├─ 📝 Explicación gramatical (español)
  ├─ 📚 Vocabulario clave con definiciones
  └─ 🇪🇸 Traducción completa al español
```

---

## ⚙️ Configuración

### Cambiar Modelo de IA

Edita `MainWindow.xaml.cs` (línea 38):
```csharp
_translator = new OllamaService("llama3.2"); // Cambiar modelo aquí
```

Modelos disponibles:
- `llama3.2` (predeterminado, recomendado)
- `llama3.1`
- `deepseek-r1`
- Cualquier modelo compatible con Ollama

### Ajustar Temperatura de Respuesta

Edita `Services/OllamaService.cs` (línea 239):
```csharp
temperature = 0.7  // Mayor = más creativo, Menor = más enfocado
```

### Modificar Ventana de Contexto

Edita `MainWindow.xaml.cs` (línea 487):
```csharp
var recentItems = History.TakeLast(5)  // Cambiar 5 al número deseado
```

---

## ⌨️ Atajos de Teclado

| Atajo | Acción |
|-------|--------|
| `Ctrl + Espacio` | Activar/Desactivar Asistente de IA |
| `Esc` | Cerrar Configuración u Overlay |
| `Win + Ctrl + L` | Activar/Desactivar Subtítulos en Vivo de Windows |

---

## 🐛 Solución de Problemas

### Ollama No Se Conecta

**Error**: "Could not start Ollama"

**Solución**:
```bash
# Iniciar Ollama manualmente
ollama serve

# Verificar que está ejecutándose
curl http://localhost:11434
```

### El Micrófono No Funciona

**Error**: "No Speech Recognizer found"

**Solución**:
1. Instala el paquete de idioma Inglés (EE.UU.) en Windows
2. Ve a Configuración → Hora e idioma → Idioma
3. Agrega "English (United States)"
4. Descarga reconocimiento de voz

### El Escaneo del Navegador Devuelve Vacío

**Error**: "CDP Empty Result" o "Legacy Scan Failed"

**Solución**:
1. Cierra todas las instancias de Chrome
2. Ejecuta `LANZAR_MODO_EXAMEN.bat` desde la carpeta del proyecto
3. Navega a tu examen/página web
4. Haz clic en el botón 🌐 Escanear Navegador

### Las Respuestas de IA Son Muy Genéricas

**Problema**: Las sugerencias no coinciden con el contexto de la conversación

**Solución**:
- Asegúrate de que el historial de conversación tenga al menos 3-5 intercambios
- No borres el historial durante conversaciones activas
- Verifica que Ollama esté usando el modelo correcto

---

## 🤝 Contribuir

¡Las contribuciones son bienvenidas! Por favor sigue estos pasos:

1. Haz fork del repositorio
2. Crea una rama de característica (`git checkout -b feature/caracteristica-increible`)
3. Haz commit de tus cambios (`git commit -m 'Agregar característica increíble'`)
4. Haz push a la rama (`git push origin feature/caracteristica-increible`)
5. Abre un Pull Request

---

## 📄 Licencia

Este proyecto está licenciado bajo la Licencia MIT - consulta el archivo [LICENSE](LICENSE) para más detalles.

---

## 🙏 Agradecimientos

- **Ollama** - Inferencia de IA local
- **Windows Live Captions** - Reconocimiento de voz del sistema
- **Selenium WebDriver** - Automatización de navegador
- **NAudio** - Biblioteca de captura de audio

---

## 📧 Contacto

Para preguntas o soporte, por favor abre un issue en GitHub.

---

**Hecho con ❤️ para estudiantes de inglés preparándose para la certificación B1**
