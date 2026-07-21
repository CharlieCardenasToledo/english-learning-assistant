# Plan de finalización: Tauri + Next.js + .NET

## Objetivo

Completar la migración del frontend WPF a Tauri + Next.js sin duplicar la lógica existente. Al final, las sesiones se inician y detienen desde la UI, la detección L1–L4 y la respuesta de IA se transmiten al frontend, y el Sidebar navega a las rutas de historial, vocabulario y configuración.

## Estado de partida verificado

- El frontend Next.js, Tauri 2 y `EnglishLearningAssistant.TauriPlugIn` ya existen.
- El bridge `dotnet_request` y los eventos Tauri están configurados.
- `SessionOrchestrator` ya recibe transcripción y publica segmentos/traducciones.
- Faltan dos conexiones del Core: detección de preguntas y streaming de respuestas.
- Los botones de inicio/parada solo cambian estado React.
- Existen los directorios `src/app/sessions`, `src/app/vocabulary` y `src/app/settings`, pero no sus `page.tsx`.

## Alcance y exclusiones

Incluido:

- Rutas Next.js integradas con el Sidebar.
- Control real del ciclo de vida de captions.
- Eventos .NET → Tauri → React: pregunta detectada, chunks y respuesta final.
- Vista de historial, vocabulario y configuración por rutas.
- Pruebas de build y compilación.

Excluido por ahora:

- Tray icon, atajos globales y transparencia; son mejoras independientes de la funcionalidad bloqueante.
- Cambios de esquema de SQLite o migraciones.

## Contratos de integración

### RPC frontend → .NET

| Controller | Acción | Datos | Resultado |
|---|---|---|---|
| `session` | `start` | ninguno | `{ ok: true }` |
| `session` | `stop` | ninguno | `{ ok: true }` |
| `session` | `initialize` | ninguno | `{ ok: true }` |
| `session` | `list` | ninguno | lista de sesiones |
| `session` | `get` | `{ id }` | sesión completa |
| `session` | `export` | `{ id }` | `{ markdown }` |
| `vocabulary` | `initialize`, `list`, `add`, `delete`, `analyze` | según controller | datos de vocabulario |
| `settings` | `get`, `save`, `testConnection`, `checkFirstRun` | según controller | configuración/resultado |

### Eventos .NET → frontend

| Evento | Payload |
|---|---|
| `transcription-line` | `{ text, timestamp, sequenceId }` |
| `transcription-partial` | `{ text, timestamp }` |
| `translation-ready` | `{ original, translated, provider, fromCache }` |
| `question-detected` | `{ text, level: 1..4, confidence }` |
| `answer-chunk` | `{ chunk }` |
| `answer-complete` | `{ answer }` |
| `status-changed` | `{ status }` |

## Implementación

### 1. Completar el Core: `SessionOrchestrator`

Archivo: `EnglishLearningAssistant.Core/Application/Sessions/SessionOrchestrator.cs`.

1. Importar `WindowsLiveCaptionsReader.Services`.
2. Añadir dependencias opcionales `QuestionDetectionService` y `LmStudioService` al constructor. Mantener compatibilidad con consumidores existentes usando valores opcionales.
3. Añadir eventos explícitos:
   - `QuestionDetected` con texto, nivel y confianza.
   - `AnswerChunkReceived` con cada fragmento nuevo.
   - `AnswerCompleted` con la respuesta completa.
4. Guardar las últimas 8 frases confirmadas como contexto de conversación.
5. Reemplazar el TODO de `ProcessSentenceForQuestionsAsync`:
   - llamar a `AnalyzeWithConfidenceAsync(sentence, studentName, cancellationToken)`;
   - si no es pregunta, terminar;
   - calcular nivel: prefijo `L4` → 4; `L3` → 3; `L2` → 2; otro → 1;
   - emitir `QuestionDetected`;
   - encolar la pregunta, sin cancelar una respuesta en curso.
6. Reemplazar el placeholder de `GenerateQuestionResponseAsync`:
   - construir prompt de tutor con tres opciones (simple, estándar, detallada) y traducciones;
   - invocar `LmStudioService.StreamChatAsync`;
   - como el callback entrega el texto acumulado, emitir solamente el sufijo nuevo;
   - emitir `AnswerCompleted` al finalizar, incluso si el resultado está vacío/error controlado.
7. No modificar modelos EF ni el esquema de base de datos.

### 2. Controlar captions desde el plugin

Archivo: `src-dotnet/EnglishLearningAssistant.TauriPlugIn/CaptionHostedService.cs`.

1. Convertir `StartAsync(IHostedService)` en inicialización sin iniciar captions automáticamente.
2. Añadir `StartSessionAsync` y `StopSessionAsync`; protegerlos con `SemaphoreSlim` para impedir dos arranques/paradas simultáneos.
3. En el arranque, construir `SessionOrchestrator` inyectando `QuestionDetectionService` y `LmStudioService`.
4. Suscribir eventos:
   - segmentos/traducciones existentes;
   - `QuestionDetected` → `question-detected`;
   - `AnswerChunkReceived` → `answer-chunk`;
   - `AnswerCompleted` → `answer-complete`.
5. En la parada, llamar `DisposeAsync`, limpiar la referencia y actualizar el estado.

Archivo: `src-dotnet/EnglishLearningAssistant.TauriPlugIn/PlugIn.cs`.

1. Registrar primero `CaptionHostedService` como singleton concreto.
2. Registrar `IHostedService` resolviendo esa misma instancia.
3. Mantener los servicios existentes como singleton.

Archivo: `src-dotnet/EnglishLearningAssistant.TauriPlugIn/Controllers/SessionController.cs`.

1. Inyectar `CaptionHostedService` además de `SessionService`.
2. Añadir acciones públicas `Start()` y `Stop()` que llamen los métodos del servicio y devuelvan `{ ok: true }`.

### 3. Conectar los controles de sesión React

Archivo: `src/app/page.tsx`.

1. Convertir `handleStart` y `handleStop` a funciones async.
2. Antes de cambiar el estado local, llamar respectivamente a `dotnetRequest("session", "start")` y `dotnetRequest("session", "stop")`.
3. Manejar errores con `sonner` y no marcar la sesión como activa si falla el backend.
4. Pasar `activeSessionId` a `SessionControls`, para habilitar exportación cuando haya una sesión seleccionada.
5. Mantener el estado visual sincronizado con `status-changed` cuando sea posible.

### 4. Rutas y Sidebar

Actualizar `src/components/Sidebar/Sidebar.tsx`.

1. Usar `usePathname` y `useRouter` de `next/navigation`.
2. Añadir navegación principal antes del historial:
   - Inicio: `/`
   - Historial: `/sessions`
   - Vocabulario: `/vocabulary`
   - Configuración: `/settings`
3. Mostrar icono + texto cuando expandido y solo icono cuando colapsado.
4. Resaltar la ruta activa.
5. Al seleccionar una sesión del historial, navegar a `/?session=<id>` o a `/sessions?id=<id>` de forma consistente.

Crear `src/app/sessions/page.tsx`.

1. Reutilizar el layout con Sidebar.
2. Cargar `session.initialize` y `session.list`.
3. Mostrar lista, fecha, contador de preguntas y acciones de abrir/exportar/eliminar.
4. Navegar al inicio al abrir una sesión, con el identificador en query string.

Crear `src/app/vocabulary/page.tsx`.

1. Reutilizar el layout con Sidebar.
2. Inicializar y listar vocabulario vía controller.
3. Incluir búsqueda y formulario para agregar palabra, definición, traducción y contexto.
4. Permitir eliminar y refrescar la lista.

Crear `src/app/settings/page.tsx`.

1. Reutilizar el layout con Sidebar.
2. Renderizar `SettingsPanel` abierto de manera permanente o extraer un `SettingsContent` reutilizable del diálogo actual.
3. Evitar doble modal y mantener las operaciones RPC ya existentes.

### 5. Robustez y UX

1. En `useCaptionEvents`, conservar la limpieza de listeners al desmontar.
2. Mostrar una notificación si start/stop falla.
3. No permitir iniciar otra sesión mientras una petición start está pendiente.
4. Si LM Studio no está disponible, mostrar el error final en el panel de respuesta, sin bloquear transcripción/traducción.

## Validación

Ejecutar desde la raíz:

```powershell
dotnet build src-dotnet/EnglishLearningAssistant.TauriPlugIn/EnglishLearningAssistant.TauriPlugIn.csproj
dotnet test
npm run build
npm run tauri:build
```

Prueba manual mínima:

1. Ejecutar `npm run tauri:dev`.
2. Pulsar Iniciar: debe aparecer el estado en vivo y comenzar captions.
3. Verificar transcripción parcial, confirmada y traducción.
4. Pronunciar/inyectar una pregunta: deben mostrarse badge L1–L4 y respuesta en streaming.
5. Pulsar Detener: captions deben detenerse y la UI reflejarlo.
6. Navegar Sidebar entre Inicio, Historial, Vocabulario y Configuración.
7. Exportar una sesión desde historial o controles.

## Criterios de aceptación

- Las tres rutas existen, compilan y se navegan con Sidebar.
- Iniciar/detener controlan el proceso .NET real, no solo React.
- Un texto interrogativo produce `question-detected` y una respuesta en chunks.
- La app sigue mostrando transcripción aunque LM Studio esté apagado.
- `dotnet build`, `dotnet test` y `npm run build` terminan correctamente.
- La build de Tauri contiene el directorio `dotnet/` con el plugin y dependencias.

## Orden recomendado para una nueva sesión

1. Leer este archivo y verificar `git status`.
2. Implementar pasos 1 y 2 juntos, luego compilar el plugin.
3. Implementar paso 3 y verificar comunicación Tauri en modo dev.
4. Implementar rutas/Sidebar en paso 4.
5. Ejecutar la validación completa y corregir tipos/errores de build antes de empaquetar.
