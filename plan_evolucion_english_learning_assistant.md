# Plan de evolución para English Learning Assistant

## 1. Objetivo general

Convertir **English Learning Assistant** en un asistente de aprendizaje de inglés más sólido, moderno y autónomo, manteniendo su principal diferenciador:

- transcripción en vivo;
- traducción automática al español;
- detección de preguntas;
- sugerencias de respuesta adaptadas al nivel CEFR;
- funcionamiento local y privado.

La meta no es copiar Meetily, sino incorporar las mejores ideas de su arquitectura y experiencia de usuario para fortalecer tu producto.

---

## 2. Visión del producto

La aplicación debería evolucionar desde un lector inteligente de Windows Live Captions hacia una plataforma completa para clases, reuniones, práctica oral y revisión posterior.

### Experiencia esperada

Durante una clase:

1. La aplicación recibe audio o subtítulos.
2. Muestra la transcripción en inglés.
3. Traduce cada oración al español.
4. Detecta preguntas del profesor.
5. Genera tres respuestas posibles.
6. Permite guardar vocabulario.
7. Conserva timestamps y contexto.

Después de la clase:

1. Genera un resumen.
2. Lista las preguntas detectadas.
3. Muestra vocabulario nuevo.
4. Identifica errores o expresiones importantes.
5. Crea ejercicios y flashcards.
6. Permite reproducir fragmentos de audio sincronizados.

---

## 3. Principios que deben guiar el desarrollo

### 3.1 Mantener la especialización

La aplicación debe seguir enfocada en aprendizaje de inglés, no convertirse en una herramienta genérica de reuniones.

### 3.2 Mejorar sin reescribir innecesariamente

Mantener por ahora:

- .NET 8;
- WPF;
- WPF-UI;
- SQLite;
- LM Studio;
- Windows Live Captions como proveedor principal.

### 3.3 Reducir dependencias rígidas

Windows Live Captions debe convertirse en un proveedor más, no en la única fuente posible.

### 3.4 Procesamiento incremental

La transcripción, traducción y detección deben trabajar mediante eventos y colas, sin esperar a que termine toda la sesión.

### 3.5 Privacidad por defecto

Toda función debe priorizar procesamiento local. Los proveedores en la nube deberían ser opcionales y claramente identificados.

---

## 4. Arquitectura objetivo

```text
Fuentes de entrada
├── Windows Live Captions
├── Micrófono
├── Audio del sistema
└── Archivo de audio o video
        ↓
ITranscriptionProvider
├── WindowsLiveCaptionsProvider
├── WhisperProvider
└── FutureMobileProvider
        ↓
TranscriptPipeline
├── Normalización
├── Orden por secuencia
├── Resultados parciales/finales
├── Timestamps
└── Control de duplicados
        ↓
Procesamiento lingüístico
├── Traducción
├── Detección de preguntas
├── Extracción de vocabulario
├── Explicación gramatical
└── Clasificación por nivel CEFR
        ↓
Asistente IA
├── Sugerencias de respuesta
├── Resumen de clase
├── Flashcards
├── Quiz
└── Plan de estudio
        ↓
Persistencia
├── SQLite
├── Audio de sesión
├── Transcripción
├── Traducciones
├── Preguntas
├── Respuestas
└── Vocabulario
```

---

## 5. Fase 0 — Preparación y saneamiento

### Objetivo

Preparar la base actual para crecer sin aumentar la deuda técnica.

### Tareas

- Documentar la arquitectura actual.
- Separar responsabilidades de `MainWindow`.
- Identificar lógica de negocio mezclada con la interfaz.
- Definir servicios e interfaces.
- Revisar excepciones no controladas.
- Añadir logging estructurado.
- Confirmar que toda configuración esté centralizada.
- Establecer convenciones de nombres y carpetas.
- Ampliar las pruebas existentes.

### Estructura sugerida

```text
Core/
├── Abstractions/
├── Models/
├── Pipelines/
└── Events/

Infrastructure/
├── Captions/
├── Audio/
├── Transcription/
├── Translation/
├── AI/
└── Persistence/

Application/
├── Sessions/
├── Questions/
├── Vocabulary/
└── Summaries/

Presentation/
├── Views/
├── ViewModels/
├── Controls/
├── Themes/
└── Converters/
```

### Entregable

Una versión funcionalmente equivalente a la actual, pero con responsabilidades mejor separadas.

### Criterio de éxito

La interfaz puede cambiar sin modificar la lógica de transcripción y la lógica puede probarse sin abrir ventanas WPF.

---

## 6. Fase 1 — Sistema de proveedores de transcripción

### Objetivo

Eliminar la dependencia exclusiva de Windows Live Captions.

### Interfaz recomendada

```csharp
public interface ITranscriptionProvider
{
    string Name { get; }
    bool SupportsPartialResults { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<TranscriptSegment> StartAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
```

### Modelo de segmento

```csharp
public sealed class TranscriptSegment
{
    public Guid Id { get; init; }
    public long SequenceId { get; init; }
    public string Text { get; init; } = string.Empty;
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public double Confidence { get; init; }
    public bool IsPartial { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
```

### Proveedores iniciales

#### WindowsLiveCaptionsProvider

Debe conservar el flujo actual como opción recomendada para Windows.

#### WhisperProvider

Debe usar Whisper local para micrófono o archivos.

#### ManualTextProvider

Útil para pruebas y entrada manual.

### Entregable

Selector de proveedor en configuración.

### Criterio de éxito

El usuario puede iniciar una sesión con Windows Live Captions o Whisper sin cambiar el resto de la aplicación.

---

## 7. Fase 2 — Whisper en vivo por fragmentos

### Objetivo

Hacer que Whisper funcione como transcriptor en tiempo real y no solamente como función secundaria.

### Pipeline sugerido

```text
AudioCaptureService
      ↓
AudioChunkChannel
      ↓
Voice Activity Detection
      ↓
WhisperWorker
      ↓
TranscriptSegment
      ↓
TranscriptChannel
```

### Recomendaciones

- Fragmentos de entre 3 y 8 segundos.
- Superposición pequeña entre fragmentos para no cortar palabras.
- Detección de silencio.
- Cola ordenada por `SequenceId`.
- Cancelación limpia al detener la sesión.
- Diferenciar resultados parciales y confirmados.
- Evitar traducir texto parcial salvo que sea necesario.
- Medir latencia promedio por fragmento.

### Modelos iniciales

- Whisper Tiny cuantizado para equipos modestos.
- Whisper Base como opción equilibrada.
- Whisper Small únicamente para equipos con suficiente hardware.

### Entregable

Modo “Whisper local en vivo”.

### Criterio de éxito

El texto aparece durante la sesión con una latencia aceptable y sin perder fragmentos.

---

## 8. Fase 3 — Timestamps y reproducción sincronizada

### Objetivo

Convertir las sesiones guardadas en material de estudio reutilizable.

### Funciones

- Guardar inicio y final de cada segmento.
- Guardar audio completo o fragmentos.
- Reproducir desde una frase.
- Saltar a la siguiente pregunta.
- Repetir una oración.
- Reducir velocidad de reproducción.
- Marcar fragmentos difíciles.
- Exportar SRT y VTT.

### Mejoras educativas

- Botón “Escuchar de nuevo”.
- Botón “Mostrar traducción”.
- Botón “Explicar esta frase”.
- Botón “Guardar vocabulario”.
- Botón “Practicar respuesta”.

### Entregable

Vista de revisión de sesión con audio sincronizado.

### Criterio de éxito

El usuario puede hacer clic en cualquier frase y escuchar el momento correspondiente.

---

## 9. Fase 4 — Importación de audio y video

### Objetivo

Permitir usar la aplicación después de la clase o con contenido grabado.

### Formatos iniciales

- WAV
- MP3
- M4A
- MP4
- MKV

### Flujo

```text
Archivo
  ↓
FFmpeg
  ↓
Audio normalizado
  ↓
Whisper
  ↓
Traducción
  ↓
Preguntas
  ↓
Vocabulario
  ↓
Resumen
```

### Funciones adicionales

- Barra de progreso.
- Cancelación.
- Reanudación cuando sea posible.
- Detección automática de idioma.
- Selección del nivel CEFR.
- Exportación completa.

### Entregable

Pantalla “Importar clase”.

### Criterio de éxito

El usuario puede procesar una clase grabada sin usar Live Captions.

---

## 10. Fase 5 — Modernización completa de la interfaz

### Objetivo

Conseguir una experiencia visual más clara y amigable sin abandonar WPF.

### Tecnología

Mantener:

- WPF;
- WPF-UI;
- MVVM;
- XAML Resource Dictionaries.

### Sistema de diseño

Crear recursos globales para:

- colores;
- tipografía;
- espaciado;
- radios;
- sombras;
- alturas de controles;
- estados hover;
- estados disabled;
- animaciones.

### Escala sugerida

```text
Espaciado: 4, 8, 12, 16, 24, 32
Radios: 6, 10, 14
Texto: 12, 14, 16, 20, 28
Animaciones: 120–220 ms
```

### Nueva estructura visual

#### Navegación principal

- En vivo
- Sesiones
- Vocabulario
- Práctica
- Configuración

#### Vista en vivo

- Transcripción como contenido principal.
- Traducción debajo o al lado.
- Pregunta detectada como tarjeta emergente.
- Respuestas sugeridas en panel expandible.
- Estado del modelo visible, pero discreto.
- Indicador “Escuchando / Pausado / Procesando”.

#### Estados vacíos

Mostrar mensajes claros cuando:

- no hay audio;
- no hay conexión con LM Studio;
- no hay modelo;
- todavía no se detectaron preguntas;
- una sesión está vacía.

### Componentes reutilizables

- `TranscriptCard`
- `TranslationCard`
- `QuestionCard`
- `ResponseOptionCard`
- `VocabularyChip`
- `ModelStatusBadge`
- `SessionSummaryCard`
- `EmptyState`
- `LoadingState`

### Entregable

Interfaz modernizada con navegación consistente y tema claro/oscuro.

### Criterio de éxito

La aplicación puede utilizarse sin que todas las funciones compitan visualmente al mismo tiempo.

---

## 11. Fase 6 — Motor de traducción mejorado

### Objetivo

Reducir latencia y evitar traducciones incoherentes.

### Proveedores

```csharp
public interface ITranslationProvider
{
    string Name { get; }

    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
```

### Implementaciones

- LM Studio.
- LibreTranslate local.
- Modelo local especializado.
- Proveedor en la nube opcional.

### Reglas

- Traducir solo frases confirmadas.
- Usar caché.
- Detectar duplicados.
- Mantener nombres propios.
- Conservar términos de vocabulario.
- Reintentar fallos transitorios.
- Mostrar estado de traducción.

### Entregable

Selector de proveedor de traducción y fallback automático.

### Criterio de éxito

La traducción aparece rápidamente y no se repite innecesariamente.

---

## 12. Fase 7 — Detección de preguntas y respuestas mejoradas

### Objetivo

Fortalecer el principal diferenciador del producto.

### Mantener

La cascada actual de reglas rápidas y clasificación por IA.

### Mejoras

- Diferenciar pregunta directa, indirecta y retórica.
- Detectar preguntas dirigidas al usuario.
- Detectar instrucciones del profesor.
- Priorizar preguntas recientes.
- Cancelar respuestas obsoletas.
- Generar respuestas por longitud:
  - breve;
  - natural;
  - detallada.
- Añadir nivel de formalidad.
- Añadir explicación de por qué una respuesta es correcta.
- Permitir regenerar.
- Permitir copiar una respuesta.
- Permitir practicar pronunciación.

### Respuesta sugerida

Cada opción debería incluir:

- inglés;
- traducción;
- dificultad CEFR;
- palabras clave;
- explicación breve;
- botón para escuchar pronunciación.

### Entregable

Panel de respuestas más útil para aprendizaje activo.

### Criterio de éxito

El estudiante puede comprender y practicar la respuesta, no solamente copiarla.

---

## 13. Fase 8 — Resúmenes educativos por plantillas

### Objetivo

Transformar cada sesión en material de estudio.

### Plantillas

#### Resumen general

- tema principal;
- ideas clave;
- conclusiones.

#### Preguntas del profesor

- pregunta;
- contexto;
- respuesta sugerida;
- respuesta del estudiante si existe.

#### Vocabulario

- palabra;
- traducción;
- definición;
- ejemplo;
- nivel CEFR.

#### Gramática

- estructuras importantes;
- posibles errores;
- explicación;
- ejemplos.

#### Plan de estudio

- qué revisar;
- prioridades;
- ejercicios recomendados.

#### Flashcards

- frente;
- reverso;
- contexto;
- ejemplo.

### Entregable

Generador de informes por plantilla.

### Criterio de éxito

Una sesión terminada produce un paquete de estudio accionable.

---

## 14. Fase 9 — Exportaciones

### Formatos

- Markdown
- TXT
- JSON
- CSV
- SRT
- VTT
- PDF
- CSV compatible con Anki

### Exportación recomendada

```text
00:12:08 — Teacher
What would you do differently?

Traducción
¿Qué harías de manera diferente?

Respuesta sugerida
I would prepare more examples beforehand.

Vocabulario
beforehand — de antemano
```

### Entregable

Centro de exportación.

### Criterio de éxito

El usuario puede reutilizar la sesión fuera de la aplicación.

---

## 15. Fase 10 — Gestión de modelos y hardware

### Objetivo

Hacer que la aplicación configure automáticamente la mejor opción disponible.

### Funciones

- Detectar RAM.
- Detectar CPU.
- Detectar GPU.
- Detectar espacio disponible.
- Recomendar modelo.
- Mostrar tamaño de descarga.
- Mostrar memoria aproximada.
- Descargar y verificar modelos.
- Eliminar modelos.
- Probar el modelo.
- Mostrar estado.

### Perfiles

#### Equipo básico

- Whisper Tiny cuantizado.
- LLM pequeño.
- Contexto reducido.

#### Equipo medio

- Whisper Base.
- LLM de 3B–4B.
- Contexto normal.

#### Equipo potente

- Whisper Small.
- LLM más grande.
- Mayor contexto.

### Entregable

Asistente de configuración de hardware.

### Criterio de éxito

Un usuario no técnico puede elegir correctamente un modelo.

---

## 16. Fase 11 — Preparación para móvil

### Objetivo

Preparar la lógica para una futura app Android sin iniciar todavía una reescritura móvil completa.

### Acciones

- Mantener lógica de negocio independiente de WPF.
- Usar interfaces para audio, transcripción, traducción y almacenamiento.
- Evitar dependencias directas de Windows fuera de Infrastructure.
- Definir contratos serializables.
- Considerar una biblioteca `Core` compartida.
- Documentar qué servicios son portables.
- Evaluar Moonshine, Whisper.cpp o Sherpa-ONNX.
- Diseñar la experiencia móvil alrededor de micrófono y archivos.

### No hacer todavía

- No reescribir toda la app en MAUI.
- No intentar capturar audio interno de otras apps como primera función.
- No migrar toda la interfaz antes de estabilizar el Core.

### Entregable

Core desacoplado y evaluaciones técnicas para Android.

### Criterio de éxito

La lógica educativa puede reutilizarse en otro cliente sin copiarla manualmente.

---

## 17. Roadmap recomendado

### Etapa inmediata: 0–6 semanas

1. Refactor arquitectónico.
2. MVVM más limpio.
3. Sistema de proveedores.
4. Modelo de segmento con timestamps.
5. Mejoras visuales básicas.
6. Estados de conexión y errores.

### Etapa corta: 6–12 semanas

1. Whisper en vivo.
2. Audio por fragmentos.
3. Resultados parciales y finales.
4. Reproducción sincronizada.
5. Exportación SRT/VTT.
6. Importación de archivos.

### Etapa media: 3–6 meses

1. Interfaz completa modernizada.
2. Plantillas educativas.
3. Gestión de modelos.
4. Resúmenes avanzados.
5. Flashcards y Anki.
6. Pronunciación y práctica.

### Etapa larga: 6–12 meses

1. Core multiplataforma.
2. Prototipo Android.
3. Motor móvil local.
4. Sincronización opcional.
5. Mejoras de diarización.
6. Sistema de plugins o proveedores externos.

---

## 18. Orden exacto de implementación recomendado

1. Crear `Core`, `Application`, `Infrastructure` y `Presentation`.
2. Extraer la lógica de `MainWindow`.
3. Crear `TranscriptSegment`.
4. Crear `ITranscriptionProvider`.
5. Implementar `WindowsLiveCaptionsProvider`.
6. Añadir pruebas del pipeline.
7. Crear `WhisperProvider`.
8. Implementar captura por fragmentos.
9. Añadir timestamps.
10. Guardar audio de sesión.
11. Crear vista de revisión.
12. Añadir importación de archivos.
13. Modernizar navegación y tarjetas.
14. Crear `ITranslationProvider`.
15. Mejorar respuestas y CEFR.
16. Añadir plantillas educativas.
17. Añadir exportaciones.
18. Añadir configuración automática de hardware.
19. Desacoplar Core para móvil.
20. Crear un prototipo Android independiente.

---

## 19. Riesgos técnicos

### Latencia

Whisper local puede retrasarse en equipos modestos.

**Mitigación:** modelos cuantizados, VAD, fragmentos pequeños y selección automática.

### Duplicación de texto

La superposición de audio puede producir frases repetidas.

**Mitigación:** comparación por similitud, secuencia y timestamps.

### Sobrecarga de LM Studio

Traducción, clasificación y generación pueden competir por el mismo modelo.

**Mitigación:** colas separadas, prioridades, cancelación y modelos especializados.

### Complejidad visual

Añadir demasiadas funciones puede volver a saturar la interfaz.

**Mitigación:** mostrar funciones progresivamente y usar navegación secundaria.

### Dependencia de Windows

Live Captions y UI Automation no son portables.

**Mitigación:** proveedores desacoplados.

### Tamaño de modelos

Los modelos pueden consumir mucho espacio.

**Mitigación:** gestor de modelos y perfiles de hardware.

---

## 20. Métricas de éxito

### Rendimiento

- Latencia de transcripción.
- Latencia de traducción.
- Tiempo de generación de respuestas.
- Uso de RAM.
- Uso de CPU/GPU.
- Fragmentos perdidos.
- Errores por sesión.

### Calidad

- Preguntas detectadas correctamente.
- Traducciones aceptables.
- Respuestas apropiadas al CEFR.
- Duplicados de transcripción.
- Segmentos sin timestamps.

### Producto

- Sesiones completadas.
- Palabras guardadas.
- Respuestas copiadas o practicadas.
- Exportaciones.
- Revisiones posteriores.
- Uso del modo Whisper frente a Live Captions.

---

## 21. Qué tomar de Meetily

### Adoptar como patrón

- transcripción incremental;
- procesamiento por fragmentos;
- eventos de actualización;
- timestamps;
- gestión de modelos;
- importación de audio;
- exportaciones;
- plantillas de resumen;
- separación entre frontend y motor;
- indicadores de estado;
- experiencia de configuración.

### Adaptar a tu producto

- resumen orientado a aprendizaje;
- vocabulario;
- preguntas del profesor;
- respuestas CEFR;
- reproducción para práctica;
- flashcards;
- explicación gramatical.

### No copiar innecesariamente

- toda la arquitectura Tauri;
- toda la interfaz React;
- funciones genéricas de reuniones que no aporten al aprendizaje;
- dependencias Rust si ya tienes soluciones sólidas en .NET.

---

## 22. Recomendación final

La mejor decisión por ahora es mantener tu aplicación en **.NET 8 + WPF**, pero transformar su arquitectura para que sea modular y preparada para distintos proveedores.

El mayor valor inmediato está en esta combinación:

```text
Windows Live Captions como modo rápido
+ Whisper local como modo independiente
+ timestamps y audio sincronizado
+ detección de preguntas
+ respuestas CEFR
+ revisión educativa posterior
```

La prioridad no debería ser crear una versión móvil inmediatamente. Primero conviene conseguir que la versión Windows sea:

- estable;
- modular;
- visualmente moderna;
- independiente de una sola fuente de transcripción;
- útil tanto durante como después de una clase.

Cuando el Core esté separado de WPF, una versión Android será mucho más viable y requerirá menos duplicación.

---

## 23. Próximo sprint recomendado

### Sprint 1

- Crear ramas y estructura por capas.
- Extraer modelos y servicios.
- Implementar `TranscriptSegment`.
- Implementar `ITranscriptionProvider`.
- Adaptar Live Captions al nuevo proveedor.
- Añadir pruebas.

### Sprint 2

- Crear captura de audio por fragmentos.
- Implementar Whisper en vivo.
- Añadir resultados parciales/finales.
- Medir latencia.
- Guardar timestamps.

### Sprint 3

- Crear vista de sesión revisable.
- Añadir reproducción sincronizada.
- Añadir exportación SRT/VTT.
- Mejorar tarjetas visuales.
- Añadir estados vacíos y errores.

Al completar esos tres sprints, la aplicación ya tendrá una base significativamente más fuerte y cercana a un producto profesional.
