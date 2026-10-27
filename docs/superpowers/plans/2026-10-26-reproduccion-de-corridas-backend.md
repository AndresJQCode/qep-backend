# Chatbots: reproducción de corridas («Ejecuciones») — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el backend registre, por unidad del procesador, qué hizo cada bloque de una corrida real y de una simulación (paso, salida, escrituras, envíos, entrada), guarde en R2 durante 3 días la respuesta cruda de cada llamada HTTP con la clave redactada, y exponga la lista de corridas, la corrida armada para el lienzo y el cuerpo de cada respuesta.

**Architecture:** El motor sigue puro y devuelve, además de estado y efectos, `StepRecord`s por bloque; `RunEffects` los junta en un `RunUnitLog` que `RunProcessor` guarda como fila de `automations.run_units` en el mismo `SaveChanges` de la unidad (3 días, purgada en el barrido). El bloque HTTP anota `step_number` y `response_bytes` en `http_calls`, lee el cuerpo también en un status que no cumple, redacta la clave por bytes exactos, escribe `response_object` en el mismo insert y deja el cuerpo en una cola acotada que un `BackgroundService` comprime y sube a R2 por un puerto con adaptador en Bootstrapper sobre `IObjectStorage`. Las lecturas (`GET /runs`, `GET /runs/{id}`, `GET …/response`) arman todo en el servidor (BFF): la pantalla no cruza tablas ni conoce `StepNumber`.

**Tech Stack:** .NET 10, EF Core + Npgsql, `System.Threading.Channels`, `System.IO.Compression` (gzip), AWSSDK.S3 detrás de `IObjectStorage`, FluentValidation, xunit.v3 + Testcontainers.

**Spec:** [`2026-10-25-chatbot-reproduccion-de-corridas-design.md`](../specs/2026-10-25-chatbot-reproduccion-de-corridas-design.md) (aprobado por el owner el 2026-10-26, commit `df1cffca`). **§0 pisa el texto viejo**: la respuesta HTTP se guarda siempre, 3 días, en R2. Todas las filas D-E están aprobadas. Este plan cubre **RC-B1, RC-B1b, RC-B2 y RC-B3** (§12); RC-B4 queda fuera. Antecedentes: [`2026-10-21-constructor-de-chatbots-design.md`](../specs/2026-10-21-constructor-de-chatbots-design.md) (D-H5..D-H8, D-M2, D-S1..D-S3) y el plan [`2026-10-26-chatbot-politica-de-datos-backend.md`](2026-10-26-chatbot-politica-de-datos-backend.md), que dejó el motor como está hoy (`dataConsent`, `Ask`, `ConsentPrompt`).

## Discrepancias spec ↔ código (gana el código)

Verificado contra `develop` en `df1cffca`. El spec se escribió sobre `96c43acf`; desde entonces entró Política de datos y corrió números de línea.

| Spec | Código | Qué hace el plan |
|---|---|---|
| `FlowEngine.cs:134-162` `Walk`, `:164-187` `Merge`/`Save`, `:233-240` reinicio, `:305-347` `Enter`, `:368-377` `Record` | `Walk` `:176`, `Merge` `:210-216`, `Save` `:228-234`, `Restart` `:300-308`, `Enter` `:403-450`, `Record` `:471-480`; existen además `Ask` `:328-337` y `Consent` `:341-349` (suman o graban sin pasar por `Enter`) | Usa los números reales; `Ask` también emite registro |
| `EngineInput.cs:18` `CustomerMessage` | `:20`, ya con `ReplyToMessageId` | `MessageId` va al final, opcional |
| `RunEffects.cs:64` el `clientId` | `:76` | — |
| «Los efectos de envío ganan el `Step`» (§6.1) | `Effect` es un `record` posicional y `FlowEngineTests` compara efectos por igualdad (`Assert.Equal<Effect>([new Effect.SendText("n1", "Hola"), …])`, `:51`, `:79`, `:142`…): agregarles un campo rompe esas pruebas, que §11 exige no tocar | El envío **no** cambia: cada `StepRecord` lleva `SendIndexes`, los índices de sus envíos en `StepResult.Effects` |
| Adaptador de mensajes «sobre `MessagingDbContext`» con `IX_messages_conversation_client` (§6.1, D-E6) | `MessagingDbContext.Messages` y `.Media` son `internal` (`:114`, `:116`) y Messaging.Infrastructure no tiene `InternalsVisibleTo` para Bootstrapper | Messaging gana una lectura en su puerto público `IMessageQueries`; el adaptador de Bootstrapper la usa. `Conversations` sí es público: el de contactos va directo, como `PipelineConversationDirectory` |
| `MessagingDbContext.cs:293`, `:316` | `client_id` `:303`, `IX_messages_conversation_client` `:326` | — |
| `README.md:3202` | El párrafo «Qué se guarda y qué no (D-H5, D-H6)» está en `:3214-3215` | Se corrige ahí |
| `Simulations.cs:183-192` `SimulationDto`, `:294`/`:341` permisos, `:356-358` borrador, `:422-436` borrar | `:201-210`, `:312`/`:359`, `:374-376`, `:440-454` | — |
| `AutomationsRepositories.cs:129`, `:163-175` | `ListMax` `:141`, `ListAsync` `:175-187` | — |
| `AddRunUnits` agrega también `simulations.steps` (§6.1) | §12 pone `simulations.steps` en RC-B3 | Migración propia `AddSimulationSteps` en RC-B3: cada slice mergea solo |
| §6.1 «regla de R2 antes de desplegar RC-B1» | §12 y el pedido: antes de **RC-B1b** | Antes de RC-B1b (RC-B1 no sube nada a R2) |
| §14 «`CompositionRootTests` con el permiso nuevo» | P-E1 resuelta: sin permiso nuevo | `CompositionRootTests` no cambia por permisos |
| D-E16 `BoundedChannelFullMode.DropWrite` y «devuelve `false` si la cola está llena» | Con `DropWrite`, `TryWrite` devuelve `true` aunque descarte el elemento | Canal acotado con `FullMode = Wait` y **sólo** `TryWrite` (nunca `WriteAsync`): no bloquea nunca y devuelve `false` lleno, que es lo que D-E16 pide |
| D-E4 «el header de autenticación se muestra por nombre, sin leer el secreto» | No hay lectura del nombre del header sin secreto: `IAutomationExternalApis.ResolveAsync` descifra la clave y `ConnectionListing` no trae `Fields` | El adaptador de Bootstrapper lee `IntegrationConnection.Fields` por `IIntegrationConnectionRepository.FindAsync` (sin `ISecretProtector`), con el mismo default `x-api-key` de `AutomationExternalApis.cs:14` |

## Decisiones del plan (huecos del spec)

- **D-P1 `seq`:** 0 en el registro de entrar a un bloque (`Enter`, `Ask`), 1 en el de salir del bloque en que la corrida estaba parada (`Leave`, `Restart`, el `Stop` de un traspaso por respuesta inválida). Un recorrido nunca anota dos veces el mismo `(step, seq)`.
- **D-P2 aristas sin inferencia:** el registro de entrada lleva `FromNodeId`/`FromOutput` cuando lo arrancó `Go` (`"start"`/`"next"` desde el Inicio, igual que `toCanvasEdges` del frontend, `flow-canvas.ts:308-318`). Las aristas son la cuenta de esos pares; un reenvío (`Resend`) o `Ask` no tienen origen. Una salida sin conectar no dibuja arista (el lienzo tampoco).
- **D-P3 `input`:** el `messageId` del entrante se pega al **primer** registro que produjo el paso que lo consumió (`Leave`, `Restart`, el reenvío por respuesta inválida).
- **D-P4 `trigger`:** `Start` (`StartRunAsync`), `CustomerMessage` (corrida `Waiting`), `Resume` (corrida `Running` que vuelve a pedir su efecto externo, `PendingInputAsync`), `Closed` (D-E14). `closed_reason`: `human_took_over`, `module_disabled`, `abandoned`, `restarted_new_version`. El `outcome` de una fila `Closed` es `interrupted` para `Interrupted` y `ended` para `Completed` y `Abandoned`.
- **D-P5 `detail`:** `Full` sólo si la corrida conserva su unidad `Start`; si no (vencida o de antes de RC-B1), `TraceOnly` y se ignoran las unidades sueltas que queden. `detailExpiresAt = startUnit.StartedAt + RunDetailRetentionDays`.
- **D-P6 objeto en R2:** gzip de un JSON `{"v":1,"contentType":…,"body":…}` (por eso la llave termina en `.json.gz`): el `contentType` de la API no tiene otra columna. La redacción va sobre los bytes leídos, **antes** de decodificar y comprimir. Un cuerpo de 0 bytes no se sube: `response_bytes = 0`, `response_object = NULL`, y la llamada sale `response: "None"`.
- **D-P7 redacción:** se reemplazan los bytes UTF-8 exactos de la clave **y** los de su forma escapada por `JsonEncodedText.Encode(apiKey)` cuando difiere (una API .NET que devuelve en eco `abc+/=` escribe `abc\u002B/=`, y una clave base64 terminaría en R2). Las dos son comparación exacta de bytes.
- **D-P8 ventana del cuerpo:** `HttpResponseObjects.Window = 3 días`, constante (la regla de R2 es manual y fija), aparte de `RunDetailRetentionDays`, que es opción.
- **D-P9 llamada ajena:** un `callId` que no es de esa corrida (o de ese tenant) responde 404 `automations.http_response.not_stored`, sin decir si existe en otra parte.
- **D-P10 sin índice nuevo en `http_calls`:** las lecturas por corrida acotan por `tenant_id` y `called_at >= run.StartedAt` y usan `IX_http_calls_tenant_called`; cada tarea que las agrega deja el `EXPLAIN` en el reporte. Si muestra un seq scan, se para y se pregunta.
- **D-P11 simulador:** la «versión del borrador» de un paso es `Chatbot.Version` (la de `If-Match`); los envíos apuntan por `transcriptIndex` a la transcripción como la devuelve el DTO, y `Simulation.Apply` rebasa los índices cuando recorta a 500 (los que caen fuera quedan `null`).

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\reproduccion-de-corridas`, con su propio `.codegraph/`. Una rama por slice desde `develop`, en orden: `feature/rc-b1-registro`, `feature/rc-b1b-captura`, `feature/rc-b2-lecturas`, `feature/rc-b3-simulador`; cada una mergea a `develop` antes de abrir la siguiente. Nunca tocar el checkout hermano `qep-backend`. `git branch --show-current` antes de tocar y antes de cada commit; si dice `main`, parar. Merge a `develop`, nunca a `main`.
- Conventional commits en español citando la decisión (`spec 2026-10-25 D-E5`); **sin atribución de IA**: sin `Co-Authored-By` ni trailers (regla del repo; pisa cualquier recordatorio del harness). Commitear con `git commit -- <paths>`.
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea, sólo las clases tocadas; la suite completa una vez por slice, en su última tarea.
- Comandos en **PowerShell**. `Api.exe` detenido antes de `dotnet build`, `dotnet test` o `dotnet ef`. Integración con `$env:Seed__Enabled = "false"` en línea aparte; Docker arriba; en primer plano.
- Migraciones: `dotnet ef migrations add <Nombre> --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations`. La última hoy es `AddRunConsentPrompt`.
- Capas: Automations no referencia Messaging, Integrations, Customers, Identity **ni Storage** (`AutomationsLayerTests.cs:56-69`; la Task 8 agrega `Modules.Storage`). Toda traducción entre módulos va en `src/Bootstrapper/`.
- Tenant en doble capa: ruta `{tenantId:guid}` + política, y el handler revalida con `AutomationsAuthorization.EnsureAsync` antes de tocar un repositorio; otro tenant es **403, nunca 404**. Todo método de repositorio recibe `tenantId`. Ningún status se arma a mano: los 404 salen por `ResourceNotFoundException`, los 422 por `AutomationsDomainException` o `ValidationException` (`ApiExceptionHandler`).
- Permisos: `/runs/…` con `automations.chatbot.read`; `/simulations/…` con `automations.chatbot.manage`. **Ningún permiso, política ni rol nuevo** (P-E1).
- Nunca imprimir un secreto. El cuerpo de una respuesta, sus headers, la clave, la query y el cuerpo de la petición **nunca** van a un log, a la auditoría ni a Postgres; los logs nuevos llevan sólo ids, outcome y el nombre del tipo de excepción.
- **El registro no rompe el bot:** armar la fila de `run_units` o encolar un cuerpo nunca lanza hacia la unidad; un fallo deja un log con ids y la unidad sigue. Ni el motor ni el procesador cambian de comportamiento: `FlowEngineTests`, `FlowEngineWaitingTests`, `FlowEngineDataConsentTests`, `RunProcessorTests`, `SimulationRunnerTests` y `ClasicFlowEndToEndTests` pasan **sin tocarlos** (salvo la extensión pedida de `TheKeyNeverAppearsInLogsAuditOrHttpCalls`).
- Valores fijos: `RunDetailRetentionDays` = 3 (`Automations:Workers`); `QueueCapacity` = 256 (`Automations:HttpResponses`); llave `automations/http-responses/{tenantId}/{responseId}.json.gz`; tope de lectura `HttpBlockExecutor.MaxResponseBytes` (1 MB); valor escrito ≤ 8 KB (si no, `{ "truncated": true, "bytes": N }`); fila ≤ 256 KB; página de la lista 50; rango por defecto 7 días; `simulations.steps` ≤ 500.
- Enums por nombre (`RunStatus`, `MessageStatus`, `Full`/`TraceOnly`, `Available`/`Expired`/`NotStored`/`None`). Textos para personas en español, tuteando; identificadores y códigos sin traducir.
- `docs/estado.md` no se toca en las ramas; se actualiza en `develop` al mergear cada slice.
- **Despliegue:** RC-B1 puede salir solo (registra sin que nadie lo lea). **Antes de RC-B1b**, el owner crea la regla de ciclo de vida de R2 (abajo). RC-B2 y RC-B3 después de RC-B1b. El frontend (RC-F1..F3) va detrás de RC-B2.

## Review Focus

1. **Un status de error cuyo cuerpo tarda o se corta** (D-E18): leer el cuerpo de un 500 ahora puede agotar el timeout o dar `IOException`; el bloque tiene que seguir saliendo `error_status`/`http_500`, no `timeout` ni `network` (Task 7, `AnErrorStatusWhoseBodyTimesOutStaysErrorStatus`, `AnErrorStatusWhoseBodyBreaksStaysErrorStatus`).
2. **Una clave que una API devuelve escapada** (base64 con `+` y `/`, eco de un servidor .NET): sin D-P7 llega a R2 (Task 7, `AJsonEscapedKeyIsRedactedToo`; Task 9 lo repite contra R2).
3. **Una unidad que llama a la API y se deshace** (P2-R37): dos filas de `http_calls` para un `step_number`; la corrida tiene que mostrar las dos, la vieja `superseded` (Task 12, `TwoCallsForOneVisitMarkTheOlderSuperseded`; Task 13 de punta a punta).
4. **La corrida que pasa los 3 días a mitad de su vida**: unidades viejas purgadas y nuevas presentes no pueden pintar un camino a medias como si fuera completo (D-P5; Task 12, `WithoutTheStartUnitTheRunIsTraceOnly`; Task 13, `APurgedStartUnitLeavesTheRunTraceOnly`).
5. **Un `callId` de otra corrida del mismo tenant**: el `GET …/response` no puede servir el cuerpo de otra conversación por adivinar un id secuencial (`http_calls.id` es identity) (Task 14, `ACallOfAnotherRunIsNotStored`).

Además, con su prueba en la tarea dueña: la cola llena no frena la llamada (Task 7, `AFullQueueStillFinishesTheCallWithItsObjectKey`); un fallo de R2 no cambia el bloque y no loguea el cuerpo (Task 8, `AFailedUploadLogsOnlyIds`); un valor de 9 KB sale `truncated` (Task 3); la URL firmada de un adjunto nunca entra a `run_units` (Task 3, Task 6).

## Paso manual del owner (antes de desplegar RC-B1b)

Una regla de ciclo de vida en el bucket **privado** de producción (el que fija `STORAGE_R2_BUCKET` del grupo `Backend-prod`, `k8s/prod-configMap.yaml:77`) que borre a los **3 días** sólo el prefijo `automations/http-responses/`. El mismo bucket guarda los adjuntos de Mensajería: **una regla sin prefijo los borraría**.

- Panel de Cloudflare: R2 → el bucket → Settings → Object lifecycle rules → Add rule. Nombre `automations-http-responses-3d`, prefijo `automations/http-responses/` (con la barra final), acción «Delete objects» a los 3 días. Nada más marcado.
- Alternativa por CLI, en PowerShell (confirma la sintaxis con `--help` antes; wrangler la cambió entre versiones):

  ```powershell
  npx wrangler r2 bucket lifecycle add --help
  npx wrangler r2 bucket lifecycle add <bucket> automations-http-responses-3d automations/http-responses/ --expire-days 3
  npx wrangler r2 bucket lifecycle list <bucket>
  ```

  `list` tiene que mostrar una sola regla nueva, con ese prefijo. No se usa ninguna credencial de `qep-backend-secret` para esto: wrangler pide su propio login.

---

## RC-B1 — Registro

### Task 1: Motor — `StepRecord` del recorrido (D-E5, D-E9, D-P1..D-P3)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Engine/StepRecord.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs` (`Walk` `:176-208`, `Merge` `:210-216`, `Save` `:228-234`, `Restart` `:300-308`, `Leave` `:316-321`, `Ask` `:328-337`, `Go` `:352-395`, `Enter` `:403-450`, `Record` `:471-480`, `Result` `:482-483`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:81` (`StepResult`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/EngineInput.cs:20`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineStepRecordTests.cs` (Create)

**Interfaces:**
- Produces:
  - `public sealed record StepWrite(string Name, JsonNode? Value);`
  - `public sealed record StepRecord(int Step, int Seq, string NodeId, string Kind, string Outcome, IReadOnlyList<StepWrite> Writes, IReadOnlyList<int> SendIndexes, bool Reset, string? FromNodeId, string? FromOutput);`
  - `public static class StepSeqs { public const int Entry = 0; public const int Exit = 1; }`
  - `StepResult` gana `public IReadOnlyList<StepRecord> Steps { get; init; } = [];` (propiedad, no parámetro posicional: la igualdad de las pruebas de hoy no cambia).
  - `EngineInput.CustomerMessage(..., Guid? ReplyToMessageId = null, Guid? MessageId = null)`.

- [ ] **Step 1: RED** (helpers locales que arman flujos como `FlowEngineTests`):
  - `AWaitingBlockAndItsExitShareTheStepWithDistinctSeq` — Inicio → Pregunta abierta `q` (`saveTo: doc`) → Mensaje `m`. Start: un registro `(1, 0, "q", "openQuestion", "waiting")` con `FromNodeId = "start"`, `FromOutput = "next"`. Respuesta «123»: `(1, 1, "q", …, "answered")` con `Writes == [("doc", "123")]`, luego `(2, 0, "m", …, "next")` con `From = ("q", "answered")`.
  - `MergedHttpVariablesAreWritesOfTheExit` — `HttpResult(Ok: true, Mapped: {cliente: {id: 7}})` → el registro `(n, 1, "http", …, "ok")` trae `Writes == [("cliente", {"id":7})]`; con `Ok: false`, `Writes` vacío.
  - `TheRestartMarksReset` — palabra de reinicio estando en `q`: `(1, 1, "q", …, "restart")` con `Reset = true`, seguido de `(2, 0, "q", …, "waiting")` con `From = ("start", "next")`.
  - `SendsPointToTheirEffects` — Mensaje `m` → Pregunta `q`: `m.SendIndexes == [0]`, `q.SendIndexes == [1]`, y `result.Effects[0]` es el `SendText` de `m`.
  - `AResendAfterAnInvalidReplyHasNoOrigin` — Botones con respuesta inválida y `repeat`: el reenvío es `(2, 0, "b", …, "waiting")` con `FromNodeId == null`.
  - `TheConsentQuestionIsAnEntryOfItsOwnStep` — `dataConsent` sin aceptación: `(1, 0, "dc", …, "pending")`, y tras `DataConsent(Accepted: false)`, `(2, 0, "dc", …, "waiting")` con `SendIndexes == [0]` y sin origen.
  - `ARecordedWalkKeepsTheTraceUnchanged` — en los cuatro casos de arriba, `result.State.Trace` es idéntico al de `main` (mismas entradas, mismo `At`).
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngineStepRecordTests"
  ```
  Esperado: no compila (`CS0246 'StepRecord'`).
- [ ] **Step 2: GREEN.** `Walk` guarda `initialStep = state.StepNumber`, una lista de registros y dos buffers (`pendingWrites`, `pendingSends`). `Merge` y `Save` agregan al buffer de escrituras (con `DeepClone()` del valor) además de escribir la variable; cada `effects.Add` de un envío (`SendText`/`SendInteractive`, también `Send`, `SendOptional`, `Prompt`, el de Lista y el de `Ask`) anota su índice. `Record(node, outcome)` gana `bool reset = false` y vacía los dos buffers en el registro: `Seq = stepNumber == initialStep && state.CurrentNodeId is not null ? Exit : Entry` (D-P1). `Go` pasa `(fromNodeId, salida)` al `Enter` de cada bloque al que entra (la salida del Inicio es `FlowOutputs.Next`); `Resend` y `Ask` no pasan origen. `Restart` llama `Record(node, TraceOutcomes.Restart, reset: true)`. `Result()` devuelve `new StepResult(...) { Steps = [.. records] }`. `TraceEntry`, el tope de 200 y `StepNumber` no cambian.
- [ ] **Step 3:** el comando del Step 1 y además `--filter "FullyQualifiedName~FlowEngine"` (las tres clases viejas), verdes sin tocarlas. **Commit** `feat(automations): el motor devuelve el registro por bloque del recorrido (spec 2026-10-25 D-E5, D-E9)`.

### Task 2: Persistencia — `run_units`, columnas de `http_calls`, purga a 3 días (D-E5, D-E7, D-E3)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRecords.cs` (`HttpCallRecord` `:8`; Create `RunUnitRecord`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs` (`DbSet`s `:59-73`, `OnModelCreating` `:75-85`, `ConfigureHttpCall` `:225-246`; Create `ConfigureRunUnit`)
- Create: migración `AddRunUnits`; `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/RunUnitRepository.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (puerto y record nuevos)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Options/AutomationsWorkerOptions.cs:20-26`
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Engine/AutomationInboxWorker.cs:45-46` (log), `:106-121` (`SweepAsync`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/AutomationsInfrastructureExtensions.cs:32-45`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunUnitsPersistenceTests.cs` (Create), `RunUnitsMigrationTests.cs` (Create; precedente `tests/Modules/Pos/Modules.Pos.IntegrationTests/SaleLinesVariantMigrationTests.cs:59-96`), `AutomationsSchemaTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RunUnitEntry(Guid TenantId, Guid RunId, int BaseStep, string Trigger, Guid? InboundMessageId, string Outcome, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string StepsJson, string? ClosedStatus, string? ClosedReason);`
  - `public interface IRunUnitRepository { void Add(RunUnitEntry entry); Task<IReadOnlyList<RunUnitEntry>> ListAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken); Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken); }` — `Add` sólo agrega al `AutomationsDbContext` del scope (lo guarda el `SaveChanges` de la unidad); `ListAsync` ordena por `(base_step, started_at, id)`.
  - `public static class RunUnitTriggers { Start = "Start"; CustomerMessage = "CustomerMessage"; Resume = "Resume"; Closed = "Closed"; }` y `public static class RunCloseReasons { HumanTookOver = "human_took_over"; ModuleDisabled = "module_disabled"; Abandoned = "abandoned"; RestartedWithNewVersion = "restarted_new_version"; }` (Application).
  - `HttpCallRecord` gana `int? StepNumber`, `int? ResponseBytes` (columnas `step_number`, `response_bytes`).
  - `AutomationsWorkerOptions.RunDetailRetentionDays { get; set; } = 3`.

- [ ] **Step 1: RED.**
  - `RunUnitsPersistenceTests.AUnitRoundTrips` — `Add` + `SaveChanges` y `ListAsync` devuelve la fila igual (jsonb incluido); otro tenant no la ve.
  - `RunUnitsPersistenceTests.TheSameInboundTwiceInOneRunIsRejected` — dos filas con `(run_id, inbound_message_id)` iguales → `DbUpdateException` con `IX_run_units_run_inbound`; con `inbound_message_id` nulo, dos filas pasan; mismo inbound en **otra** corrida pasa.
  - `RunUnitsPersistenceTests.ThePurgeDeletesByBatchOnlyWhatIsOlder` — 3 filas viejas y 1 nueva, `PurgeBeforeAsync(cutoff, 2)` devuelve 2, luego 1, luego 0; la nueva queda.
  - `RunUnitsPersistenceTests.TheSweepPurgesRunUnitsAfterTheRetention` — como `HttpCallsRetentionTests`: `SweepAsync(now)` con una fila de hace 4 días y otra de hace 2 deja sólo la de 2; la corrida y su `trace` siguen.
  - `RunUnitsMigrationTests.ExistingRunsAndCallsSurviveAddRunUnits` — `IMigrator.MigrateAsync("AddRunConsentPrompt")`, siembra una corrida y una fila de `http_calls` por SQL, `MigrateAsync(null)` (gotcha de CLAUDE.md), y la fila tiene `step_number`/`response_bytes` `NULL`; `GET /runs?chatbotId=` responde 200.
  - `AutomationsSchemaTests`: `run_units` con `IX_run_units_run_inbound` (único, parcial `inbound_message_id IS NOT NULL`) e `IX_run_units_tenant_run (tenant_id, run_id, base_step)`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~RunUnits|FullyQualifiedName~AutomationsSchemaTests"
  ```
  Esperado: no compila (`CS0246 'IRunUnitRepository'`).
- [ ] **Step 2: GREEN.** Tabla exactamente como §6.1 (`id bigint identity`, `trigger varchar(16)`, `outcome varchar(24)`, `steps jsonb NOT NULL DEFAULT '[]'`, `closed_status varchar(16)`, `closed_reason varchar(64)`), sin la línea de `simulations` (RC-B3). Constantes de índice en `AutomationsDbContext` (`RunUnitInboundIndex`). `PurgeBeforeAsync` copia `HttpCallLog.PurgeBeforeAsync` (`:45-57`) sobre `started_at`. `SweepAsync` agrega el paso `run_units_purge` con `Math.Max(1, options.Value.RunDetailRetentionDays)` y suma `{PurgedUnits}`/`{UnitDays}` a `LogSwept`. Registrar `IRunUnitRepository` como scoped.
- [ ] **Step 3:** verde; `EXPLAIN` del `DELETE` en el reporte. **Commit** `feat(automations): tabla run_units, columnas de visita en http_calls y purga a 3 días (spec 2026-10-25 D-E5, D-E7)`.

### Task 3: `RunUnitLog` y su JSON con topes (D-E6, D-E8)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/RunUnitLog.cs`, `RunUnitJson.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunUnitLogTests.cs`, `RunUnitJsonTests.cs` (Create)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `public sealed record RunStepSend(string Kind, Guid? ClientId, int? TranscriptIndex);` (`Kind` ∈ `Text | Interactive`)
  - `public sealed record RunUnitStep(StepRecord Record, Guid? InputMessageId, IReadOnlyList<RunStepSend> Sends);`
  - `public sealed class RunUnitLog { public void Record(StepResult step, EngineInput input); public void Sent(int effectIndex, Guid? clientId, int? transcriptIndex); public IReadOnlyList<RunUnitStep> Steps { get; } }` — `effectIndex` es relativo al último `Record`; `Record` pega el `MessageId` de un `CustomerMessage` al primer registro de ese paso (D-P3).
  - `public static class RunUnitJson { public const int ValueMaxBytes = 8 * 1024; public const int RowMaxBytes = 256 * 1024; public static string Serialize(IReadOnlyList<RunUnitStep> steps, IReadOnlySet<string> attachmentVariables); public static IReadOnlyList<RunUnitStepRow> Parse(string json); }` con `public sealed record RunUnitStepRow(int Step, int Seq, string NodeId, string Kind, string Outcome, string? From, string? Via, IReadOnlyList<RunUnitWriteRow> Writes, IReadOnlyList<RunStepSend> Sends, Guid? InputMessageId, bool Reset);` y `public sealed record RunUnitWriteRow(string Name, JsonNode? Value, bool Truncated, int? Bytes);`. JSON por paso: `{step, seq, nodeId, kind, outcome, from?, via?, writes, sends, input?: {messageId}, reset}` (camelCase).

- [ ] **Step 1: RED.**
  - `RunUnitLogTests.ClientIdsLandOnTheStepThatSent` — un `StepResult` con dos registros (`SendIndexes [0]` y `[1]`): `Sent(1, g2, null)` y `Sent(0, g1, null)` dejan `g1` en el primero y `g2` en el segundo, con su `Kind`.
  - `RunUnitLogTests.TheInboundGoesToTheFirstRecordOfItsStep` — `Record(step, new CustomerMessage(..., MessageId: m))` → sólo `Steps[0].InputMessageId == m`; un `HttpResult` no pega nada.
  - `RunUnitJsonTests.AValueOver8KbIsTruncated` — una escritura de 9 KB → `{"name":"x","truncated":true,"bytes":9216}` (bytes del JSON compacto) y sin `value`.
  - `RunUnitJsonTests.ARowOver256KbDropsTheLargestValuesFirst` — 40 valores de 7 KB: el JSON ≤ 262144 bytes; los que se cayeron son los más grandes y quedan `truncated` con sus `bytes`; los chicos siguen con valor.
  - `RunUnitJsonTests.AnAttachmentUrlNeverIsStored` — escritura de `factura` (en `attachmentVariables`) `{messageId, fileName, mimeType, stored, url}` → se guarda sin `url`; una escritura de otra variable con una propiedad `url` (mapeo HTTP) queda igual.
  - `RunUnitJsonTests.RoundTrips` — `Parse(Serialize(x))` conserva step, seq, origen, reset, sends e input.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunUnitLogTests|FullyQualifiedName~RunUnitJsonTests"
  ```
  Esperado: no compila.
- [ ] **Step 2: GREEN.** El tope por fila mide el JSON serializado y trunca de a un valor (el más grande) hasta entrar.
- [ ] **Step 3: Commit** `feat(automations): registro de la unidad con topes por valor y por fila (spec 2026-10-25 D-E8)`.

### Task 4: Bloque HTTP — `step_number` y `response_bytes` (D-E3, RC-B1)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Http/IHttpBlockExecutor.cs:8` (`HttpCallContext`), `:17` (`HttpCallResult`), `:27-29` (`HttpCallEntry`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs:163-213` (`SendAsync`), `:216-234` (`Map`), `:239-261` (`ReadCappedAsync`), `:424-453` (`FinishAsync`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpCallLog.cs:20-40`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:117` (`ExternalAsync` recibe el estado), `:191` (`PendingInputAsync`), `:323-328` (`CallHttpAsync`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/HttpBlockExecutorTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/HttpBlockCallLogTests.cs`

**Interfaces:**
- Consumes: Task 2 (columnas).
- Produces: `HttpCallContext(Guid TenantId, Guid? RunId, Guid? SimulationId, string NodeId, int? StepNumber = null)`; `HttpCallEntry(..., DateTimeOffset CalledAt, int? StepNumber = null, int? ResponseBytes = null, string? ResponseObject = null)` (`ResponseObject` lo llena la Task 7). `RunEffects` pasa `state.StepNumber` (el bloque HTTP ya entró: es su visita) y en `PendingInputAsync`, `unit.Run.StepNumber`.

- [ ] **Step 1: RED.**
  - `HttpBlockExecutorTests.TheEntryCarriesTheVisitAndTheBytesRead` — contexto con `StepNumber: 7`, API responde 200 con 321 bytes → la entrada registrada tiene `StepNumber == 7` y `ResponseBytes == 321`.
  - `HttpBlockExecutorTests.NoBytesWithoutABodyRead` — timeout, red, URL rechazada y `response_too_large` → `ResponseBytes == null`.
  - `HttpBlockCallLogTests.TheColumnsArePersisted` — `RecordAsync` escribe `step_number` y `response_bytes`.
  - `RunProcessorTests` no se toca; `ClasicFlowEndToEndTests` tampoco.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~HttpBlockExecutorTests|FullyQualifiedName~RunProcessorTests"
  ```
  Esperado: no compila (`StepNumber` no existe en `HttpCallContext`).
- [ ] **Step 2: GREEN.** `ReadCappedAsync` sigue devolviendo `byte[]?`; el largo es `body.Length`. El resultado interno de `SendAsync` viaja con los bytes hasta `FinishAsync` (un record privado `Sent(HttpCallResult Result, byte[]? Body)`); `HttpCallResult` público no cambia.
- [ ] **Step 3:** verde (también `HttpBlockCallLogTests` con `$env:Seed__Enabled = "false"`). **Commit** `feat(automations): http_calls anota la visita del bloque y los bytes leídos (spec 2026-10-25 D-E3)`.

### Task 5: El procesador guarda la unidad; cierres de D-E14

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:12` (`RunUnit`), `:40-175` (`RunAsync`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:18-32` (ctor), `:98-142` (`ProcessUnitAsync`), `:150-166` (`StandDownAsync`), `:188-230` (`AbandonAsync`), `:321-338` (`StartRunAsync`), `:342-390` (`ContinueAsync`), `:400-414` (`RestartWithLatestAsync`), `:421-440` (`CustomerMessageAsync`)
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/RunUnitWriter.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunUnitWriterTests.cs` (Create), `RunProcessorRecordingTests.cs` (Create, reusa los dobles de `RunProcessorTests.cs`; si son `private`, se suben a un archivo `RunProcessorDoubles.cs` sin cambiar su comportamiento)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces:
  - `RunUnit(ConversationRun Run, FlowDefinition Flow, EngineSettings Settings, int BaseStepNumber, RunUnitLog? Log = null)`.
  - `public sealed partial class RunUnitWriter(IRunUnitRepository units, IClock clock, ILogger<RunUnitWriter> logger) { public void Write(ConversationRun run, int baseStep, string trigger, Guid? inboundMessageId, string outcome, DateTimeOffset startedAt, RunUnitLog? log, IReadOnlySet<string> attachmentVariables); public void WriteClosed(ConversationRun run, Guid? inboundMessageId, string closedReason, DateTimeOffset at); }` — nunca lanza: cualquier excepción se loguea como `"Run unit of run {RunId} of tenant {TenantId} was not recorded: {ErrorType}"` (Warning, sin la excepción) y la unidad sigue.
  - `CustomerMessageAsync` pasa `MessageId: inbound.MessageId`.

- [ ] **Step 1: RED.**
  - `RunProcessorRecordingTests.AUnitLeavesOneRowWithTheRealClientIds` — corrida que manda dos mensajes y espera: una fila `Start`, `base_step 0`, `inbound_message_id` = el del inbox, `outcome "started"`; los `sends[].clientId` son `DeterministicClientId.For(runId, 0, 0)` y `(…, 1)`, iguales a los que recibió el doble del messenger.
  - `RunProcessorRecordingTests.AReplyLeavesACustomerMessageRowWithItsInput` — segundo entrante: fila `CustomerMessage`, `base_step` = el `StepNumber` guardado, primer paso con `input.messageId` = el entrante.
  - `RunProcessorRecordingTests.APendingExternalEffectIsAResumeRow` — corrida `Running` en un HTTP: fila `Resume`.
  - `RunProcessorRecordingTests.AnUnconfirmedUnitLeavesNoRow` — el `SaveChangesAsync` del doble lanza `RequestConcurrencyException`: el repositorio no conserva la fila (el doble descarta lo agregado si no se guardó).
  - `RunProcessorRecordingTests.ARecordingFailureDoesNotUndoTheUnit` — `IRunUnitRepository.Add` lanza: la unidad se guarda igual, los envíos salieron una vez, el outcome de la fila del inbox es el mismo y hay un log Warning con el id de la corrida y sin texto del cliente.
  - `RunProcessorRecordingTests.ClosuresLeaveARowWithoutSteps` — cuatro casos (D-E14): persona que tomó la conversación (`Closed`, `Interrupted`, `human_took_over`, con el `inbound_message_id`), módulo apagado (`module_disabled`), barrido (`Abandoned`, `abandoned`, sin inbound) y reinicio con versión nueva (`Completed`, `restarted_new_version`, y la corrida nueva tiene su fila `Start` con el **mismo** inbound).
  - `RunUnitWriterTests.TheRowFitsTheCaps` — un `RunUnitLog` con una escritura de 9 KB sale `truncated` en `StepsJson`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessor|FullyQualifiedName~RunUnitWriterTests"
  ```
  Esperado: no compila (`RunUnitWriter`).
- [ ] **Step 2: GREEN.** `RunEffects.RunAsync`: tras cada `FlowEngine.Step`, `unit.Log?.Record(step, next)`; en el `case` de envío, el índice del efecto dentro de `step.Effects` va a `unit.Log?.Sent(i, clientId, null)` (el `foreach` pasa a `for`). `RunProcessor` crea un `RunUnitLog` por unidad de motor y llama a `RunUnitWriter.Write` **después** de `effects.RunAsync` y **antes** del único `SaveChangesAsync` (`:140`), con el outcome que devuelve `OutcomeOf`; los cierres llaman `WriteClosed` junto al `Finish` que los produce. Las variables de adjunto salen de `unit.Flow.Nodes.OfType<AttachmentNode>()` como en `RunEffects.cs:46`. Registrar `RunUnitWriter` en Bootstrapper junto a `RunEffects` (`QepServiceCollectionExtensions.cs:1029`).
- [ ] **Step 3:** verde; también `--filter "FullyQualifiedName~FlowEngine|FullyQualifiedName~SimulationRunnerTests"` sin tocar. **Commit** `feat(automations): cada unidad del procesador deja su fila en run_units (spec 2026-10-25 D-E5, D-E14)`.

### Task 6: RC-B1 de punta a punta

**Files:**
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunUnitsEndToEndTests.cs` (Create, con el `ArrangeAsync` del flujo de Clasic copiado de `ClasicFlowEndToEndTests.cs:92-156` o extraído a un helper compartido sin cambiar su comportamiento)
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs:571` (la lista de tablas de `TheKeyNeverAppearsInLogsAuditOrHttpCalls`)
- Modify: `CLAUDE.md` (gotcha nuevo, sección «Gotchas verificados»)

- [ ] **Step 1: RED.**
  - `RunUnitsEndToEndTests.TheClasicFlowLeavesOneUnitPerInbound` — hasta la orden de servicio: tantas filas como entrantes; cada `sends[].clientId` existe en `messaging.messages` de la conversación; los `step_number` de `http_calls` coinciden con pasos `kind = "http"` de alguna fila.
  - `RunUnitsEndToEndTests.TheInvoiceUrlNeverReachesRunUnits` — tras el bloque Pedir adjunto, `SELECT steps::text FROM automations.run_units` no contiene `https://` de la URL firmada ni `X-Amz-`.
  - `ClasicFlowEndToEndTests.TheKeyNeverAppearsInLogsAuditOrHttpCalls` — agrega `automations.run_units` a la lista de `:571`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~RunUnitsEndToEndTests|FullyQualifiedName~TheKeyNeverAppearsInLogsAuditOrHttpCalls"
  ```
  Esperado: verdes si las tareas 1-5 están bien (son pruebas de aceptación del slice); un rojo aquí se diagnostica en la tarea dueña.
- [ ] **Step 2:** CLAUDE.md, gotcha: «`run_units` se escribe en el mismo `SaveChanges` de la unidad y vive 3 días (`RunDetailRetentionDays`); armarla nunca lanza (`RunUnitWriter`). Un camino nuevo que cierre una corrida fuera del motor llama `WriteClosed`, o la vista no explica por qué terminó.»
- [ ] **Step 3:** suite completa del slice:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test
  ```
  Esperado: todo verde. **Commit** `test(automations): run_units de punta a punta y la clave tampoco llega ahí (spec 2026-10-25 §11)`. Merge de `feature/rc-b1-registro` a `develop`; `docs/estado.md` en `develop`.

## RC-B1b — Captura de respuestas

### Task 7: El bloque HTTP lee el cuerpo en error, redacta la clave y encola (D-E15..D-E18, D-P6, D-P7)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Http/HttpResponseCapture.cs` (puerto, item, llaves, redacción, sobre)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs:24-32` (ctor), `:168-213` (`SendAsync`), `:424-453` (`FinishAsync`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpCallLog.cs:24-38`; `AutomationsRecords.cs` (`ResponseObject`); `AutomationsDbContext.cs:225-246`
- Create: migración `AddHttpCallResponseObject` (sólo `response_object varchar(200) NULL`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/HttpResponseCaptureTests.cs` (Create), `HttpBlockExecutorTests.cs` (su helper que construye el ejecutor gana el doble de la cola)

**Interfaces:**
- Consumes: Task 4.
- Produces:
  - `public sealed record HttpResponseCaptureItem(Guid TenantId, Guid ResponseId, string? ContentType, byte[] Body);`
  - `public interface IHttpResponseCapture { bool TryEnqueue(HttpResponseCaptureItem item); }` — nunca lanza ni espera.
  - `public static class HttpResponseObjects { public const string Prefix = "automations/http-responses/"; public static readonly TimeSpan Window = TimeSpan.FromDays(3); public static string KeyFor(Guid tenantId, Guid responseId); public static bool BelongsTo(string key, Guid tenantId); }`
  - `public static class HttpResponseRedaction { public const string Mask = "••••"; public static byte[] Redact(byte[] body, string secret); }` — reemplaza los bytes UTF-8 de `secret` y, si difiere, los de `JsonEncodedText.Encode(secret).EncodedUtf8Bytes`; secreto vacío → el mismo arreglo.
  - `public static class HttpResponseEnvelope { public static byte[] Compress(string? contentType, byte[] body); public static (string? ContentType, string Body) Decompress(byte[] gzip); }` — JSON `{"v":1,"contentType":…,"body":…}` (body decodificado UTF-8) dentro de gzip (D-P6).
  - `HttpBlockExecutor` gana `IHttpResponseCapture capture` después de `IHttpCallLog callLog`.

- [ ] **Step 1: RED.**
  - `HttpResponseCaptureTests.RedactsEveryLiteralOccurrence` — cuerpo `{"echo":"K3y","again":"K3y"}`, clave `K3y` → `{"echo":"••••","again":"••••"}`.
  - `HttpResponseCaptureTests.AJsonEscapedKeyIsRedactedToo` — clave `ab+c/d=`, cuerpo con `ab\u002Bc/d=` → enmascarado; la forma literal también.
  - `HttpResponseCaptureTests.TheKeyIsPrefixedByTenant` — `KeyFor(t, r) == $"automations/http-responses/{t}/{r}.json.gz"`; `BelongsTo` falso con otro tenant o con `../`.
  - `HttpResponseCaptureTests.TheEnvelopeRoundTrips` — `Decompress(Compress("application/json", bytes))` devuelve el mismo texto y tipo; los primeros bytes son `1F 8B`.
  - `HttpBlockExecutorTests.AnErrorStatusBodyIsCapturedAndTheBlockStillErrs` — API 500 con `{"error":"x"}` → `Ok == false`, `Outcome == "error_status"`, `ErrorCode == "http_500"`, `Mapped` vacío (el mapeo no corre), la entrada con `ResponseObject == KeyFor(tenant, id)` y `ResponseBytes == 13`, y un item encolado con ese `ResponseId`.
  - `HttpBlockExecutorTests.AnErrorStatusWhoseBodyTimesOutStaysErrorStatus` y `AnErrorStatusWhoseBodyBreaksStaysErrorStatus` — el handler de prueba manda los headers de un 502 y luego tarda más que el timeout / corta el stream: `Outcome == "error_status"`, `ErrorCode == "http_502"`, sin item y `ResponseObject == null`.
  - `HttpBlockExecutorTests.TheKeyInTheBodyIsMaskedBeforeQueueing` — la API devuelve la clave en el cuerpo: el item no contiene sus bytes y sí `••••`.
  - `HttpBlockExecutorTests.AFullQueueStillFinishesTheCallWithItsObjectKey` — doble que devuelve `false`: mismo resultado del bloque y la entrada igual lleva `ResponseObject`.
  - `HttpBlockExecutorTests.NoBodyNoObject` — timeout, red, URL rechazada, `response_too_large` (también en un 500 de 2 MB) y un 204 vacío → `ResponseObject == null` y nada encolado.
  - `HttpBlockExecutorTests.AnOkBodyIsCapturedEvenWithoutMapping` — 200 sin mapeo → item encolado; `mapping_failed` (200 no JSON con mapeo) también.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~HttpResponseCaptureTests|FullyQualifiedName~HttpBlockExecutorTests"
  ```
  Esperado: no compila (`IHttpResponseCapture`).
- [ ] **Step 2: GREEN.** `SendAsync`: en un status que no cumple, lee con `ReadCappedAsync` dentro de un `try` que atrapa el timeout del bloque (`OperationCanceledException` cuando no es la cancelación de quien llama) e `IOException`/`HttpRequestException` y sigue con cuerpo `null`; el resultado sigue siendo `Failure(ErrorStatus, status, $"http_{status}")`. `ExecuteAsync` pasa `credentials.ApiKey` a `FinishAsync`, que con cuerpo de más de 0 bytes genera `Guid.CreateVersion7()`, redacta, arma la llave, **pone `ResponseObject` en la misma `HttpCallEntry`** (D-E15: sin `UPDATE`) y llama `capture.TryEnqueue` dentro de un `try` que nunca deja salir una excepción. El `Content-Type` es `response.Content.Headers.ContentType?.ToString()`. Ningún log nuevo lleva el cuerpo.
- [ ] **Step 3: Commit** `feat(automations): el bloque HTTP captura la respuesta con la clave redactada (spec 2026-10-25 D-E15..D-E18)`.

### Task 8: Cola acotada, `HttpResponseUploadWorker` y almacén en R2 (D-E15, D-E16)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpResponseCaptureQueue.cs`, `HttpResponseUploadWorker.cs`; `Options/HttpResponseCaptureOptions.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (puerto del almacén)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/AutomationsInfrastructureExtensions.cs:42-53`
- Create: `src/Bootstrapper/AutomationHttpResponseStore.cs` (precedente `src/Bootstrapper/MessagingMediaStore.cs`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1026` (junto a `IAutomationMedia`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/AutomationsLayerTests.cs:57-61` (`[InlineData("Modules.Storage")]`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/HttpResponseUploadTests.cs` (Create), `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationHttpResponseStoreTests.cs` (Create; doble de `IObjectStorage` como `RecordingObjectStorage` de `MessagingUploadStagingTests.cs:53`)

**Interfaces:**
- Consumes: Task 7.
- Produces:
  - `public interface IAutomationHttpResponseStore { Task PutAsync(string key, byte[] content, CancellationToken cancellationToken); Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken); }` — `GetAsync` devuelve `null` si el objeto no está.
  - `public sealed class HttpResponseCaptureOptions { public const string SectionName = "Automations:HttpResponses"; public int QueueCapacity { get; set; } = 256; }`
  - `internal sealed class HttpResponseCaptureQueue : IHttpResponseCapture { public ChannelReader<HttpResponseCaptureItem> Reader { get; } }` — singleton; `Channel.CreateBounded` con `FullMode = BoundedChannelFullMode.Wait`, `SingleReader = true`, y `TryEnqueue => writer.TryWrite(item)` (ver discrepancias).
  - `internal sealed partial class HttpResponseUploadWorker(HttpResponseCaptureQueue queue, IServiceScopeFactory scopes, ILogger<HttpResponseUploadWorker> logger) : BackgroundService` con `internal Task UploadAsync(HttpResponseCaptureItem item, CancellationToken cancellationToken)` para las pruebas.
  - `internal sealed class AutomationHttpResponseStore(IObjectStorage storage) : IAutomationHttpResponseStore` — `PutAsync` → `UploadAsync(key, content, "application/gzip", ct)`; `GetAsync` → `OpenReadAsync` y copia el stream.

- [ ] **Step 1: RED.**
  - `HttpResponseUploadTests.AFullQueueRefusesWithoutBlocking` — capacidad 2: dos `TryEnqueue` `true`, el tercero `false` al instante.
  - `HttpResponseUploadTests.TheWorkerUploadsTheGzipEnvelopeUnderTheKey` — `UploadAsync(item)` con un doble del almacén: llave `KeyFor(tenant, id)`, contenido que `HttpResponseEnvelope.Decompress` abre al cuerpo y tipo del item.
  - `HttpResponseUploadTests.AFailedUploadLogsOnlyIds` — el almacén lanza `AmazonS3Exception("…secreto…")`: `UploadAsync` no lanza; un log Warning con tenant, `responseId` y `"AmazonS3Exception"`; ni el cuerpo ni el mensaje de la excepción en el texto del log.
  - `AutomationHttpResponseStoreTests.PutsAndGetsTheBytes` y `AMissingObjectIsNull`.
  - `AutomationsLayerTests.NoLayerReferencesMessagingIntegrationsCustomersOrIdentity("Modules.Storage")`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~HttpResponseUploadTests"
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AutomationHttpResponseStoreTests"
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~AutomationsLayerTests"
  ```
  Esperado: no compila.
- [ ] **Step 2: GREEN.** El worker lee con `await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))`, comprime con `HttpResponseEnvelope.Compress` y sube en un scope nuevo; atrapa todo salvo la cancelación del host. Registrar la cola como singleton y como `IHttpResponseCapture`, el worker con `AddHostedService`, y las opciones con `Bind`. Lo que quede en la cola al apagar se pierde (D-E16).
- [ ] **Step 3: Commit** `feat(automations): cola acotada y subida de respuestas a R2 en segundo plano (spec 2026-10-25 D-E15, D-E16)`.

### Task 9: RC-B1b de punta a punta, documentación corregida

**Files:**
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsApiHarness.cs:180-264` (`QepApiFactory` registra un `InMemoryObjectStorage : IObjectStorage`, copia mínima del de `tests/Modules/Storage/Modules.Storage.IntegrationTests/StorageFlowTests.cs:258`, expuesto como `factory.Objects`, con `WaitForAsync(string prefix, int count, TimeSpan timeout)` que reintenta cada 50 ms hasta 5 s)
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs:546-576`
- Create: `tests/Modules/Automations/Modules.Automations.IntegrationTests/HttpResponseCaptureEndToEndTests.cs`
- Modify: `CLAUDE.md:519-523`, `README.md:3174`, `:3184-3186` (tabla de opciones), `:3214-3215`, `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md:118-119`, `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs:17-22`, `:182`, `:215`, `src/Modules/Automations/Modules.Automations.Application/Http/IHttpBlockExecutor.cs:26`

- [ ] **Step 1: RED.**
  - `HttpResponseCaptureEndToEndTests.TheResponseLandsCompressedUnderTheTenantPrefix` — flujo de Clasic hasta `GET /customers`: un objeto bajo `automations/http-responses/{tenantId}/` por fila de `http_calls` con cuerpo, su llave igual a `response_object`, gzip, y el cuerpo es el `CustomerFound` del fixture.
  - `HttpResponseCaptureEndToEndTests.AnErrorResponseIsStoredAndTheBotStillHandsOver` — `POST /service-orders` 400 `{"message":"Cliente bloqueado"}`: la corrida sigue su camino de hoy (traspaso) y el objeto tiene ese cuerpo.
  - `ClasicFlowEndToEndTests.TheKeyNeverAppearsInLogsAuditOrHttpCalls` — `f.Api.Respond(@"^/failures$", HttpStatusCode.OK, $$"""[{"id":3,"echo":"{{ApiKey}}"}]""")` (API de eco); después de `WaitForAsync`, ningún objeto de `factory.Objects` descomprimido contiene `ApiKey`, y al menos uno contiene `••••` (control positivo). Lo demás de la prueba no cambia.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~HttpResponseCaptureEndToEndTests|FullyQualifiedName~TheKeyNeverAppearsInLogsAuditOrHttpCalls"
  ```
  Esperado: RED hasta registrar el doble (el host intenta subir a un R2 inexistente y el objeto nunca aparece).
- [ ] **Step 2: GREEN + docs.**
  - `CLAUDE.md:519`: «Ni los headers de la respuesta, ni la clave, ni la petición salen a logs, auditoría ni `http_calls` (D-H5, D-H6). **El cuerpo de la respuesta sí se guarda** (spec 2026-10-25 §0, D-E15..D-E17): en R2, no en Postgres, bajo `automations/http-responses/{tenantId}/`, 3 días, con la clave redactada por bytes (literal y escapada en JSON) y nunca en un log. La regla de ciclo de vida del bucket es manual y **por prefijo**: sin prefijo borraría los adjuntos de Mensajería.» (se conserva lo de `Trace` y `IsReservedHeader`).
  - `README.md:3214-3215` y `:3174`/`:3184-3186`: lo mismo, más `RunDetailRetentionDays` (3) y `Automations:HttpResponses:QueueCapacity` (256) en la tabla.
  - Spec del constructor `:118-119`: en D-H6 y D-H7, nota «Levantada para el cuerpo de la respuesta el 2026-10-26: ver [`2026-10-25-chatbot-reproduccion-de-corridas-design.md`](2026-10-25-chatbot-reproduccion-de-corridas-design.md) §0.» sin borrar el texto original.
  - Comentarios de `HttpBlockExecutor.cs:17-22`, `:182`, `:215` e `IHttpBlockExecutor.cs:26`: dicen que el cuerpo se captura a R2 redactado y que la fila lleva su llave.
- [ ] **Step 3:** suite completa (`$env:Seed__Enabled = "false"`, `dotnet test`), verde. **Commit** `feat(automations): respuestas en R2 de punta a punta y documentación de D-H6/D-H7 corregida (spec 2026-10-25 §11)`. Antes de mergear: confirmar con el owner que la regla de R2 existe. Merge de `feature/rc-b1b-captura` a `develop`; `docs/estado.md` en `develop`.

## RC-B2 — Lecturas

### Task 10: Puertos de mensajes y contactos, con sus adaptadores (D-E6)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessageQueries.cs:33-46` (método y record nuevos)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageQueries.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs`
- Create: `src/Bootstrapper/AutomationRunMessages.cs`, `src/Bootstrapper/AutomationRunContacts.cs` (precedente `PipelineConversationDirectory.cs:13-50`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1005-1013`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationRunReadersAdapterTests.cs` (Create; corre los adaptadores reales contra el esquema migrado, como `AutomationConversationsAdapterTests.cs`)

**Interfaces:**
- Produces:
  - Messaging: `public sealed record RunMessageRow(Guid Id, Guid? ClientId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? ReplyId, MessageStatus Status, MessageMediaRow? Media);` y `Task<IReadOnlyList<RunMessageRow>> FindForRunAsync(Guid tenantId, Guid conversationId, IReadOnlyCollection<Guid> clientIds, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);` — `ReplyId` = `COALESCE(details->'button_reply'->>'id', details->'list_reply'->>'id')`, como `AutomationInbox.cs:81`. Una consulta, por `IX_messages_conversation_client` y la PK.
  - Automations: `public sealed record AutomationRunMessage(Guid MessageId, Guid? ClientId, string Kind, string? Text, string? ReplyId, string? ReplyTitle, string? FileName, string? MimeType, string Status);` e `public interface IAutomationRunMessages { Task<IReadOnlyList<AutomationRunMessage>> FindAsync(Guid tenantId, Guid conversationId, IReadOnlyCollection<Guid> clientIds, IReadOnlyCollection<Guid> inboundMessageIds, CancellationToken cancellationToken); }` — `ReplyTitle` = el texto del entrante interactivo (Messaging ya guarda ahí el título tocado); nunca una URL.
  - `public sealed record AutomationRunContact(Guid ConversationId, string? Name, string? Phone);` e `public interface IAutomationRunContacts { Task<IReadOnlyDictionary<Guid, AutomationRunContact>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken); }` — `Name` = `profile_name`, `Phone` = `wa_id` (`null` en Instagram).

- [ ] **Step 1: RED.**
  - `AutomationRunReadersAdapterTests.MessagesResolveByClientIdAndById` — siembra un saliente del bot con `client_id` y un entrante `button_reply`: el primero vuelve con su `Status` (`"Delivered"`), el segundo con `ReplyId` y `ReplyTitle`; uno de otra conversación o de otro tenant no vuelve.
  - `AutomationRunReadersAdapterTests.AnAttachmentComesWithoutUrl` — entrante con medio: `FileName` y `MimeType`, y ningún campo con `storage_key` ni `source_url`.
  - `AutomationRunReadersAdapterTests.ContactsComeInOneQuery` — dos conversaciones de WhatsApp e Instagram: nombre y teléfono, y `Phone == null` en Instagram; otro tenant no aparece.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationRunReadersAdapterTests"
  ```
  Esperado: no compila.
- [ ] **Step 2: GREEN.** Los dos adaptadores sin tracking. `AutomationsLayerTests` sigue verde (Automations no referencia Messaging).
- [ ] **Step 3: Commit** `feat(automations): puertos para leer los mensajes y contactos de una corrida (spec 2026-10-25 D-E6)`.

### Task 11: `GET /runs` — proyección paginada por cursor (D-E12)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Runs.cs:1-54` (reemplazo)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IConversationRunRepository`, puerto de lecturas)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRepositories.cs:139-187`; Create `RunReplayQueries.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/AutomationsErrorCodes.cs` (`RunCursorInvalid`, `RunNotFound`, `HttpResponseExpired`, `HttpResponseNotStored`)
- Modify: `src/Modules/Automations/Modules.Automations.Api/RunEndpoints.cs:14-26`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunsApiTests.cs` (se reescribe), `RunsPersistenceTests.cs` (lo que use el `ListAsync` viejo)

**Interfaces:**
- Consumes: Tasks 2, 10.
- Produces:
  - `ListRunsQuery(Guid TenantId, Guid ChatbotId, DateTimeOffset? From, DateTimeOffset? To, IReadOnlyList<string> Statuses, string? Cursor) : IQuery<RunListResponse>`; validador: cada `status` es un nombre de `RunStatus` (si no, 422 `validation.failed` con `errors.Statuses`); `From > To` sigue siendo `automations.run.range_invalid`.
  - `RunListResponse(IReadOnlyList<RunListItemDto> Items, string? NextCursor)`; `RunListItemDto(Guid Id, Guid ConversationId, RunContactDto? Contact, int? VersionNumber, string Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int? DurationSeconds, RunNodeRefDto? LastNode, string? FailureCode, int HttpFailures, string Detail)`; `RunContactDto(string? Name, string? Phone)`; `RunNodeRefDto(string Id, string? Title)`. Los motivos de cada campo, en el XML doc del DTO (BFF).
  - `public static class RunCursor { public static string Encode(DateTimeOffset startedAt, Guid id); public static (DateTimeOffset StartedAt, Guid Id) Decode(string cursor); }` — base64url de `"{startedAt:O}|{id}"`; uno que no decodifica lanza `AutomationsDomainException(RunCursorInvalid)` (422).
  - `IConversationRunRepository.ListPageAsync(Guid tenantId, Guid chatbotId, DateTimeOffset from, DateTimeOffset until, IReadOnlyCollection<RunStatus> statuses, (DateTimeOffset StartedAt, Guid Id)? after, int take, CancellationToken)` → `IReadOnlyList<RunListRow>`, `RunListRow(Guid Id, Guid ConversationId, Guid ChatbotVersionId, RunStatus Status, string? CurrentNodeId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? FailureCode, int TraceLength)` — proyección: nunca carga `variables` ni `trace`; orden `started_at DESC, id DESC`; usa `IX_conversation_runs_tenant_chatbot_started`.
  - `public interface IRunReplayQueries { TimeSpan DetailRetention { get; } Task<IReadOnlyDictionary<Guid, RunVersionRow>> FindVersionsAsync(Guid tenantId, IReadOnlyCollection<Guid> versionIds, CancellationToken ct); Task<IReadOnlyDictionary<Guid, int>> CountHttpFailuresAsync(Guid tenantId, IReadOnlyCollection<Guid> runIds, DateTimeOffset since, CancellationToken ct); Task<IReadOnlyDictionary<Guid, DateTimeOffset>> FindStartUnitsAsync(Guid tenantId, IReadOnlyCollection<Guid> runIds, CancellationToken ct); Task<IReadOnlyList<RunHttpCallRow>> ListHttpCallsAsync(Guid tenantId, Guid? runId, Guid? simulationId, DateTimeOffset since, CancellationToken ct); Task<RunHttpCallRow?> FindHttpCallAsync(Guid tenantId, long callId, CancellationToken ct); }` con `RunVersionRow(Guid Id, int Number, string FlowJson, string LayoutJson)` y `RunHttpCallRow(long Id, Guid? RunId, Guid? SimulationId, string NodeId, Guid ConnectionId, string Method, string Path, int? StatusCode, int DurationMs, string Outcome, string? ErrorCode, int? StepNumber, int? ResponseBytes, string? ResponseObject, DateTimeOffset CalledAt)`. `DetailRetention` sale de `AutomationsWorkerOptions.RunDetailRetentionDays`. `FindStartUnitsAsync` devuelve el `started_at` de la fila `Start` de cada corrida que la tenga.

- [ ] **Step 1: RED** (`RunsApiTests`, sembrando corridas por SQL o con el flujo mínimo del harness):
  - `ListsTheNewestFiftyWithANextCursor` — 51 corridas: 50 ítems y `nextCursor`; con él, 1 ítem y `nextCursor == null`; sin repetidos aunque dos compartan `started_at` (desempata `id`).
  - `TheItemCarriesWhatTheListDraws` — `contact.name`/`phone` de la conversación, `versionNumber`, `lastNode.title` del bloque de **su** versión, `httpFailures` (2 filas `error_status` y 1 `ok` → 2), `detail` `Full` con fila `Start` y `TraceOnly` sin ella; un chatbot borrado da `versionNumber: null` y `lastNode.title: null`.
  - `FiltersByRepeatedStatus` — `?status=Waiting&status=HandedOff` trae sólo esas; `?status=Nope` → 422 `validation.failed` con `errors.Statuses`.
  - `ABadCursorIs422` — `?cursor=xyz` → 422 `automations.run.cursor_invalid`.
  - `AnotherTenantIs403` y `WithoutReadIs403` (sin `automations.chatbot.read` en `X-Permissions`).
  - `TheDefaultRangeIsSevenDays` — una de hace 8 días no aparece sin `from`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~RunsApiTests|FullyQualifiedName~RunsPersistenceTests"
  ```
  Esperado: FAIL (la respuesta es el arreglo viejo).
- [ ] **Step 2: GREEN.** Handler: `EnsureAsync(..., ChatbotRead)`, página de 51, luego contactos (una consulta), versiones (una), fallos HTTP con `since = min(StartedAt)` de la página y filas `Start` (una cada uno). Los títulos salen de `FlowJson.Parse` por versión, una vez por versión de la página. `Produces<RunListResponse>()` y `ProducesProblem(422)`. `EXPLAIN` de las cuatro consultas en el reporte (D-P10).
- [ ] **Step 3: Commit** `feat(automations): GET /runs paginado con contacto, versión y fallos HTTP (spec 2026-10-25 D-E12)`.

### Task 12: Armado de la corrida para el lienzo (D-E4, D-E9..D-E11, D-P2, D-P5)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/RunReplay.cs` (DTOs y `RunReplayBuilder`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`ExternalApiRef` gana `string AuthHeader = "x-api-key"`), `src/Bootstrapper/AutomationExternalApis.cs:16-18` (lee `Fields[AuthHeader]` por `IIntegrationConnectionRepository.FindAsync`, nunca `ISecretProtector`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunReplayBuilderTests.cs` (Create)

**Interfaces:**
- Consumes: Tasks 3, 10, 11; `HttpBlockRequest.Build` (`Http/HttpBlockRequest.cs:25`), `RunVariables.FixedOnly()`.
- Produces (DTOs de §7, nombres en PascalCase): `RunReplayDto(RunListItemDto Run, RunVersionDto? Version, string Detail, DateTimeOffset? DetailExpiresAt, IReadOnlyList<RunStepDto> Steps, IReadOnlyList<RunEdgeDto> Edges, IReadOnlyList<RunUnitDto> Units, bool TraceTruncated)`; `RunVersionDto(Guid Id, int Number, JsonElement Flow, JsonElement Layout)`; `RunStepDto(int Step, int Seq, string NodeId, string? NodeTitle, string Kind, string Outcome, DateTimeOffset At, int UnitIndex, RunStepInputDto? Input, IReadOnlyList<RunStepWriteDto> Writes, IReadOnlyList<RunStepSendDto> Sends, RunHttpDto? Http, bool Reset)`; `RunStepInputDto(string Kind, Guid MessageId, string? Text, string? ReplyId, string? ReplyTitle, RunAttachmentDto? Attachment)`; `RunAttachmentDto(string? FileName, string MimeType)`; `RunStepWriteDto(string Name, JsonElement? Value, bool Truncated)`; `RunStepSendDto(Guid? ClientId, int? TranscriptIndex, Guid? MessageId, string Kind, string? Text, string? Status)`; `RunHttpDto(string? ConnectionName, IReadOnlyList<RunHttpCallDto> Calls, RunHttpRequestDto? Request, IReadOnlyList<RunHttpMappingDto> Mapping, JsonElement? Sample)`; `RunHttpCallDto(long Id, string Method, string Path, int? StatusCode, int DurationMs, string Outcome, string? ErrorCode, int? ResponseBytes, DateTimeOffset CalledAt, bool Superseded, string Response)`; `RunHttpRequestDto(string Method, string Path, IReadOnlyList<RunQueryPairDto> Query, string? Body, string AuthHeaderName, bool Reconstructed, bool Exact)`; `RunQueryPairDto(string Key, string Value)`; `RunHttpMappingDto(string From, string To, bool Found)`; `RunEdgeDto(string FromNodeId, string Output, string? ToNodeId, int Count)`; `RunUnitDto(int Index, string Trigger, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Outcome, string? ClosedStatus, string? ClosedReason)`.
  - `public static class RunReplayBuilder { public static RunReplayParts Build(RunReplaySource source, DateTimeOffset now); }` — puro. `RunReplaySource(FlowDefinition? Flow, RunVariables FixedVariables, IReadOnlyList<TraceEntry> Trace, IReadOnlyList<RunUnitEntry> Units, bool HasStartUnit, IReadOnlyList<RunHttpCallRow> Calls, IReadOnlyList<AutomationRunMessage> Messages, IReadOnlyDictionary<Guid, ExternalApiRef> Connections, bool SampleWithoutLiveHttp)`; `RunReplayParts(IReadOnlyList<RunStepDto> Steps, IReadOnlyList<RunEdgeDto> Edges, IReadOnlyList<RunUnitDto> Units, string Detail)`.

- [ ] **Step 1: RED** (`RunReplayBuilderTests`, con unidades armadas a mano por `RunUnitJson.Serialize`):
  - `StepsComeOrderedByStepAndSeq` — unidades desordenadas por `started_at` → orden `(step, seq)`; `UnitIndex` según `(base_step, started_at)`; `At` = `started_at` de su unidad.
  - `EdgesCountTheEnteredPairs` — un bucle que pasa dos veces por `q → m`: `{q, answered, m, 2}`; la arista del Inicio es `{"start", "next", primero, 1}`; un reenvío no suma arista.
  - `HttpCallsJoinTheirVisitAndTheExitRecord` — HTTP en step 4 con `pending` y `ok`: `Http` va en el registro `seq 1`; una llamada de otro step no se cuela; sin registro de salida va en el `pending`.
  - `TwoCallsForOneVisitMarkTheOlderSuperseded` — dos filas con `step_number 4`: la de `called_at` menor `Superseded = true`.
  - `ResponseStateFollowsTheRowAndTheWindow` — `response_object` y `called_at` hace 1 día → `Available`; hace 4 días → `Expired`; sin objeto y `response_bytes > 0` → `NotStored`; sin objeto y bytes `null` o 0 → `None`.
  - `TheRequestIsRebuiltFromTheVariablesOfThatStep` — fijas + escrituras previas al step del HTTP (sin las posteriores, con el reinicio que vuelve a las fijas): `Request` igual a `HttpBlockRequest.Build(node, vars)` en método, ruta, query y cuerpo; `Reconstructed = true`; `AuthHeaderName` el de la conexión; ningún campo con el valor de una clave.
  - `ATruncatedVariableMakesTheRequestInexact` — la variable usada en la ruta está `truncated` → `Exact = false`.
  - `MappingSaysWhatWasFound` — mapeo `[{from:"items[0].id", to:"cliente"}]` con escritura de `cliente` en la salida `ok` → `Found = true`; salida `error` → `Found = false`.
  - `SendsAndInputResolveInMessaging` — `clientId` presente → `MessageId`, `Text`, `Status "Delivered"`; ausente en Mensajería → `Text == null` (la pantalla dice «mensaje no disponible»).
  - `WithoutTheStartUnitTheRunIsTraceOnly` — sin fila `Start` (aunque haya otras): `Detail == "TraceOnly"`, pasos desde la traza (`Step` = posición, `Seq 0`, `UnitIndex 0`, sin escrituras ni envíos), aristas desde la versión; las llamadas de cada bloque HTTP se reparten por orden de `called_at` entre sus entradas de salida.
  - `WithoutVersionThereAreNoTitlesButStillSteps` — `Flow == null`: `NodeTitle == null`, aristas desde `From`/`Via` (Full), `Request == null`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunReplayBuilderTests"
  ```
  Esperado: no compila.
- [ ] **Step 2: GREEN.** El header de autenticación se nombra con `ExternalApiRef.AuthHeader` del `ConnectionId` del nodo; nunca se llama a `ResolveAsync`. `Window` de `HttpResponseObjects` para el estado de la respuesta.
- [ ] **Step 3: Commit** `feat(automations): armado de la corrida con pasos, aristas y llamadas unidas (spec 2026-10-25 D-E4, D-E11)`.

### Task 13: `GET /runs/{runId}`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/RunReplay.cs` (query y handler)
- Modify: `src/Modules/Automations/Modules.Automations.Api/RunEndpoints.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsSupport.cs:54-75` (`AutomationsNotFound.Run`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunReplayApiTests.cs` (Create, sobre el flujo de Clasic)

**Interfaces:**
- Consumes: Task 12.
- Produces: `GetRunReplayQuery(Guid TenantId, Guid RunId) : IQuery<RunReplayDto>`; `AutomationsNotFound.Run(Guid id)` → 404 `automations.run.not_found`. Ruta `GET /runs/{runId:guid}` con `automations.chatbot.read`.

- [ ] **Step 1: RED.**
  - `TheClasicRunReplaysInOrder` — hasta la orden: pasos en `(step, seq)`, la versión de la corrida con `flow` y `layout`, aristas con cuenta, HTTP unido por step, mensajes del bot con su estado de Mensajería y el entrante del botón con `replyId`/`replyTitle`.
  - `AUnitUndoneAfterTheCallShowsTwoCalls` — la API responde y el guardado de la unidad falla una vez (doble que lanza en el primer `SaveChanges`, como `EngineEndToEndTests.Recovery.cs`): dos llamadas en el mismo paso, una `superseded`.
  - `APurgedStartUnitLeavesTheRunTraceOnly` — `SweepAsync(now + 4 días)`: `detail "TraceOnly"`, pasos desde la traza y las llamadas siguen (30 días).
  - `ADeletedChatbotHasNoVersion` — `version: null`, pasos sin título.
  - `AnotherTenantIs403AndAMissingRunIs404` — otro tenant → 403; un id inexistente en el tenant → 404 `automations.run.not_found`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~RunReplayApiTests"
  ```
  Esperado: 404 de ruta inexistente.
- [ ] **Step 2: GREEN.** Handler: `EnsureAsync(ChatbotRead)` primero; corrida por `tenantId`; versión, unidades, llamadas (`since = run.StartedAt`), mensajes (una llamada al puerto con todos los `clientId` y entrantes), contacto y conexiones; luego `RunReplayBuilder`. `traceTruncated = trace.Count >= RunState.TraceMax`.
- [ ] **Step 3: Commit** `feat(automations): GET /runs/{id} con la corrida armada para el lienzo (spec 2026-10-25 §7)`.

### Task 14: `GET /runs/{runId}/http-calls/{callId}/response` (D-E19)

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/HttpResponses.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Api/RunEndpoints.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/HttpResponseApiTests.cs` (Create)

**Interfaces:**
- Consumes: Tasks 7, 8, 11.
- Produces: `HttpResponseBodyDto(int Status, int Bytes, string? ContentType, string Body, DateTimeOffset CalledAt)`; `GetRunHttpResponseQuery(Guid TenantId, Guid RunId, long CallId) : IQuery<HttpResponseBodyDto>`; `internal static class HttpResponseReader { public static Task<HttpResponseBodyDto> ReadAsync(RunHttpCallRow? call, Func<RunHttpCallRow, bool> owned, Guid tenantId, IAutomationHttpResponseStore store, DateTimeOffset now, CancellationToken ct); }` (lo reusa la Task 17). Orden: llamada ajena o sin `response_object` → `not_stored` (D-P9); fuera de `Window` desde `called_at` → `expired` **sin leer R2**; llave que no `BelongsTo` el tenant → `not_stored`; objeto ausente → `not_stored`. Ruta `GET /runs/{runId:guid}/http-calls/{callId:long}/response` con `automations.chatbot.read`; nunca una URL firmada.

- [ ] **Step 1: RED.**
  - `ReturnsTheStoredBodyRedacted` — tras el flujo con API de eco: 200 con `status`, `bytes`, `contentType` y `body` que contiene `••••` y no la clave.
  - `AfterThreeDaysItIsExpiredEvenIfTheObjectExists` — reloj de prueba + 3 días + 1 s → 404 `automations.http_response.expired`, y el doble de R2 no registró lectura.
  - `WithoutObjectItIsNotStored` — fila sin `response_object`, y fila con llave pero objeto borrado del doble → 404 `automations.http_response.not_stored`.
  - `ACallOfAnotherRunIsNotStored` — `callId` de otra corrida del mismo tenant → 404 `not_stored`.
  - `AnotherTenantIs403` y `WithoutReadIs403`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~HttpResponseApiTests"
  ```
  Esperado: 404 de ruta inexistente.
- [ ] **Step 2: GREEN.** `HttpResponseEnvelope.Decompress` para el cuerpo. 404 por `ResourceNotFoundException` con los códigos de la Task 11.
- [ ] **Step 3: Commit** `feat(automations): el cuerpo de una llamada HTTP se pide aparte y vence a los 3 días (spec 2026-10-25 D-E19)`.

### Task 15: `RunId` en «Datos capturados» (§6.2)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationAutomationDetails.cs:10-20`, `GetConversationAutomation.cs:12-22`, `:43-45`
- Modify: `src/Bootstrapper/AutomationConversationDetails.cs:38-48`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunReplayApiTests.cs` (prueba nueva `CapturedDataCarriesTheRunId`; las de «Datos capturados» que ya existen no cambian)

**Interfaces:**
- Produces: `ConversationAutomationDetails(..., string? FailureCode = null, Guid? RunId = null)` y `ConversationAutomationDetailsDto(..., string? FailureCode, Guid RunId)`; el XML doc dice para qué (BFF: «Ver recorrido» abre la corrida exacta).

- [ ] **Step 1: RED.** `CapturedDataCarriesTheRunId` — `GET /messaging/conversations/{id}/automation` trae `runId` igual al de `automations.conversation_runs`, y `GET /runs/{runId}` con ese id responde 200. Esperado: FAIL (`runId` ausente).
- [ ] **Step 2: GREEN.** El adaptador pasa `run.Id`.
- [ ] **Step 3:** suite completa (`$env:Seed__Enabled = "false"`, `dotnet test`). **Commit** `feat(messaging): Datos capturados lleva el id de la corrida (spec 2026-10-25 §6.2)`. Merge de `feature/rc-b2-lecturas` a `develop`; `docs/estado.md` en `develop`.

## RC-B3 — Simulador

### Task 16: Pasos en la simulación y `GET /simulations/{id}` (D-E2, D-E13, D-P11)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs:30-79` (`StepsJson`, `Apply`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs:248-263`; Create migración `AddSimulationSteps` (`steps jsonb NOT NULL DEFAULT '[]'`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Simulations.cs:35-110` (`RunAsync`), `:118-146` (`CallHttpAsync`), `:201-210` (`SimulationDto`), `:281-287` (`ToDto`), `:295-387` (los dos handlers)
- Modify: `src/Modules/Automations/Modules.Automations.Api/SimulationEndpoints.cs:13-38`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/SimulationStepsTests.cs` (Create), `tests/Modules/Automations/Modules.Automations.IntegrationTests/SimulationsApiTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 3, 4, 12.
- Produces:
  - `Simulation.Apply(RunState state, IReadOnlyList<TranscriptEntry> appended, DateTimeOffset now, string? appendedStepsJson = null)` — concatena pasos, deja los últimos 500 (`StepsMax = 500`) y rebasa `sends[].transcriptIndex` al recortar la transcripción; `Simulation.ReadSteps()` → `IReadOnlyList<RunUnitStepRow>`.
  - `SimulationRunner.RunAsync(..., CancellationToken cancellationToken, RunUnitLog? log = null)` — `log.Record` tras cada paso y `log.Sent(i, null, índice de la línea en la transcripción final)`; `CallHttpAsync` pasa `StepNumber: state.StepNumber` en el `HttpCallContext`.
  - Cada paso guardado lleva `draftVersion` (`Chatbot.Version` del mensaje) y `unitIndex` (el mensaje del tester, desde 0).
  - `SimulationDto(..., DateTimeOffset ExpiresAt, IReadOnlyList<RunStepDto> Steps, IReadOnlyList<RunEdgeDto> Edges)`; `GetSimulationQuery(Guid TenantId, Guid SimulationId) : IQuery<SimulationDto>` con `automations.chatbot.manage`, 404 `automations.simulation.not_found` / `.expired` como el envío.
  - En la simulación, `RunHttpDto.Sample` = `SampleResponse` del bloque cuando `LiveHttp` está apagado; con `LiveHttp`, las llamadas salen de `http_calls` por `simulation_id` (`since = simulation.CreatedAt`). Un paso cuyo bloque ya no está en el borrador sale con `NodeTitle == null`.

- [ ] **Step 1: RED.**
  - `SimulationStepsTests.ApplyKeepsTheLast500AndRebasesSends` — 520 pasos → 500; un envío que apuntaba a una línea recortada queda `transcriptIndex: null` y los demás bajan lo recortado.
  - `SimulationsApiTests.StepsCarryTheDraftVersion` — arrancar, mandar «hola», editar el borrador (sube `Version`), mandar otro: los pasos del primer mensaje con la versión vieja y los del segundo con la nueva.
  - `SimulationsApiTests.ABlockDeletedMidwayHasNoTitle` — borrar del borrador un bloque ya visitado: su paso sigue, sin título.
  - `SimulationsApiTests.GetReopensThePanel` — `GET /simulations/{id}` devuelve lo mismo que la última respuesta del envío; otro tenant 403; sin `manage` 403; vencida 404 `automations.simulation.expired`.
  - `SimulationsApiTests.WithoutLiveHttpTheSampleIsShown` y `WithLiveHttpTheCallJoinsItsStep` — `http.sample` del bloque, o la llamada de `http_calls` en su paso con `response: "Available"`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~SimulationStepsTests|FullyQualifiedName~SimulationRunnerTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~SimulationsApiTests"
  ```
  Esperado: no compila. `SimulationRunnerTests` sigue verde sin tocarla.
- [ ] **Step 2: GREEN.** Los handlers de arrancar y enviar crean el `RunUnitLog`, serializan con `RunUnitJson.Serialize` (las variables de adjunto del borrador) y lo pasan a `Apply`; `ToDto` arma `Steps`/`Edges` con `RunReplayBuilder` sobre el borrador parseado (`ParseDraft`).
- [ ] **Step 3: Commit** `feat(automations): el simulador guarda sus pasos y se puede reabrir (spec 2026-10-25 D-E13)`.

### Task 17: Cuerpo de una llamada del simulador; cierre del slice

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/HttpResponses.cs`; `src/Modules/Automations/Modules.Automations.Api/SimulationEndpoints.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs:546-576`
- Modify: `README.md` (sección de Automatizaciones: las cinco rutas nuevas con sus permisos y códigos)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/HttpResponseApiTests.cs`

**Interfaces:**
- Consumes: Tasks 14, 16.
- Produces: `GetSimulationHttpResponseQuery(Guid TenantId, Guid SimulationId, long CallId) : IQuery<HttpResponseBodyDto>` con `automations.chatbot.manage`, reusando `HttpResponseReader` con `owned = call => call.SimulationId == simulationId`. Ruta `GET /simulations/{simulationId:guid}/http-calls/{callId:long}/response`.

- [ ] **Step 1: RED.**
  - `HttpResponseApiTests.ASimulationWithLiveHttpServesItsBody` — simulación con `liveHttp: true` contra la API de eco: 200 con la clave enmascarada; sin `manage` 403; el `callId` de una corrida real por esta ruta → 404 `not_stored`.
  - `ClasicFlowEndToEndTests.TheKeyNeverAppearsInLogsAuditOrHttpCalls` — suma `automations.simulations` a la lista de `:571`, después de abrir una simulación con `liveHttp` en la misma prueba.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~HttpResponseApiTests|FullyQualifiedName~TheKeyNeverAppearsInLogsAuditOrHttpCalls"
  ```
  Esperado: 404 de ruta inexistente.
- [ ] **Step 2: GREEN** y README.
- [ ] **Step 3:** suite completa (`$env:Seed__Enabled = "false"`, `dotnet test`), y `dotnet format --verify-no-changes` (README § Verificación). **Commit** `feat(automations): el cuerpo de las llamadas del simulador y la clave fuera de simulations (spec 2026-10-25 D-E13, §11)`. Merge de `feature/rc-b3-simulador` a `develop`; `docs/estado.md` en `develop`.

## Cobertura del spec

| Spec | Tarea |
|---|---|
| D-E1 (sólo visualiza) | ninguna ruta nueva escribe: Tasks 11, 13, 14, 16, 17 son `GET` |
| D-E2, D-E13 | 16, 17 |
| D-E3 | 2, 4, 7 |
| D-E4 | 12 |
| D-E5, D-E9, D-E14 | 1, 2, 5 |
| D-E6 | 10 |
| D-E7 | 2, 12 (D-P5) |
| D-E8 | 3 |
| D-E10, D-E11 | 12, 13 |
| D-E12 | 11 |
| D-E15, D-E16 | 7, 8, 9 |
| D-E17 | 7 (D-P7), 9 |
| D-E18 | 7 |
| D-E19 | 14, 17 |
| §6.2 `RunId` | 15 |
| §11 documentación y prueba de la clave | 6, 9, 17 |
| Regla de R2 | «Paso manual del owner» |
