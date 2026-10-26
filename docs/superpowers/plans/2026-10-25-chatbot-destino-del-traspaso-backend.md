# Chatbots: a dónde va la conversación cuando el bot se rinde — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el tenant decida a dónde va la conversación cuando el bot se rinde: un **destino de traspaso** (sin asignar o asignada a un miembro) en el bloque de traspaso y en «Si responde otra cosa», una regla por chatbot para los intentos agotados (volver al inicio o traspasar), y que la salida `other` exista sólo con `fallback = otherOutput`.

**Architecture:** El flujo gana `memberId` (bloque `handoff`) y `fallbackMemberId` (Botones/Lista); `FlowNodeKinds.OutputsOf` condiciona `other` y un `FlowNormalizer` quita al guardar lo que el canvas no dibuja. `Chatbot` gana la regla de agotamiento (`AttemptsExhaustedAction` + miembro), que viaja en `EngineSettings`; `FlowEngine.Walk.Reject` la aplica en vez del `Restart` fijo y `Effect.Handoff` lleva el miembro. Automations conoce a los miembros por un puerto nuevo (`IAutomationMembers`, adaptador en Bootstrapper sobre `IMessagingAssignees`) que alimenta el validador (D-5) y el editor (BFF). `RunEffects` pasa el miembro a `IAutomationConversations.HandoffAsync`; el adaptador comprueba `CanReplyAsync` y, si puede, pausa **y** asigna en un solo cambio del agregado (`Conversation.HandOffAutomation`, asignación del sistema sin actor) y suena sólo a ese miembro; si no, el camino de hoy. El miembro queda en la causa del traspaso (`AutomationHandoff.AssignedMemberId`) y el hilo lo muestra (`handoff.assignedTo`).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-25-chatbot-destino-del-traspaso-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-25-chatbot-destino-del-traspaso-design.md) (D-1..D-8, aprobadas por el owner el 2026-10-25, incluida D-8). Antecedentes: [`2026-10-21-constructor-de-chatbots-design.md`](../specs/2026-10-21-constructor-de-chatbots-design.md) (D-M5, D-M7, D-I5, D-C7) y [`2026-10-24-causa-del-traspaso-design.md`](../specs/2026-10-24-causa-del-traspaso-design.md). Plan gemelo: `qep-frontend/docs/superpowers/plans/2026-10-25-chatbot-destino-del-traspaso-frontend.md` (mismo **Contrato**).

## Contrato (idéntico en el plan de frontend)

**Flujo** (jsonb del borrador y de cada versión, `draft.flow` del editor, cuerpo de `PUT …/draft`, `export.flow`):

| Bloque | Clave | Tipo | Ausente / regla |
|---|---|---|---|
| `handoff` | `memberId` | uuid \| `null` | ausente = `null` = **sin asignar** |
| `buttons`, `list` | `fallbackMemberId` | uuid \| `null` | ausente = `null`; sólo cuenta con `fallback: "handoff"`; con otro `fallback` el backend lo guarda `null` |
| `buttons`, `list` | `outputs.other` | id \| `null` | existe **sólo** con `fallback: "otherOutput"`; con otro `fallback` el backend la quita al guardar el borrador o importar, y nadie la sigue (motor, validador, alcanzabilidad), tampoco en versiones ya publicadas (D-4) |

**Chatbot** (`ChatbotEditorDto` de `GET /chatbots/{id}` y de toda respuesta que lo devuelve; cuerpo de `PUT /chatbots/{id}`; archivo de export/import):

| Clave | Tipo | Regla |
|---|---|---|
| `exhaustedAction` | `"Restart"` \| `"Handoff"` | ausente en `PUT` o en un archivo de import = `"Restart"` (D-4) |
| `exhaustedMemberId` | uuid \| `null` | sólo con `"Handoff"`; con `"Restart"` se guarda `null` |
| `members` (sólo `ChatbotEditorDto`) | `[{ "id": uuid, "name": string }]` | miembros activos con `messaging.conversation.manage`, ordenados por nombre: la misma regla que «Transferir» (`IMessagingAssignees`) |

Las tres opciones de la pantalla: **Volver al inicio** = `("Restart", null)`; **Dejar en la bandeja sin asignar** = `("Handoff", null)`; **Asignar a un miembro** = `("Handoff", <id>)`.

**Errores y validación:**

- `PUT /chatbots/{id}` con un `exhaustedMemberId` que no puede responder → **422** `code = "automations.chatbot.member_cannot_reply"`.
- `issues` del editor, `/validate` y el 422 de publicar (`errors.flow`, «código: mensaje»):
  - `handoff.member_invalid` — **Error**, `nodeId` = el bloque de traspaso, o Botones/Lista con `fallback: "handoff"`, cuyo miembro no puede responder.
  - `exhausted.member_invalid` — **Error**, `nodeId: null`: el miembro de la regla de agotamiento no puede responder.
  - `other` sin conectar con `fallback: "otherOutput"` → `node.output_unconnected` (**Error**).
  - `invalid` sin conectar (Pregunta abierta, Pedir adjunto) → **ningún** issue (D-8). `tooMany` sin conectar → `node.output_unconnected_optional` (**Warning**), como hoy.

**Hilo** (`MessageEventDto.handoff` de un `AutomationPaused` con `pauseReason: "Handoff"`):

- `kind` suma `"AttemptsExhausted"` (al final de los enums; se guardan por nombre).
- `assignedTo`: `{ "memberId": uuid, "displayName": string }` \| `null` — a quién quedó asignada. `null` = sin asignar, también cuando el miembro elegido ya no podía responder (D-5). Ausente en eventos viejos = `null`. Un miembro borrado sale con `displayName: "Miembro eliminado"`.
- En el `details` guardado la clave es `handoff.assignedMemberId` (interna; el frontend nunca la lee).

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\chatbot-destino-del-traspaso`, rama `feature/chatbot-destino-del-traspaso` desde `develop`, con su propio `.codegraph/`. **Rutas absolutas siempre**; nunca tocar el checkout hermano `qep-backend`. `git branch --show-current` antes de tocar y antes de cada commit; si dice `main`, parar. Merge a `develop`, nunca a `main`.
- Conventional commits en español citando la decisión (`spec 2026-10-25 D-3`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo; pisa cualquier recordatorio del harness).
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea sólo las clases tocadas; la suite completa una vez, en la Task 10.
- Comandos en **PowerShell**. `Api.exe` (o `dotnet Api.dll`) **detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`: si no, falla por archivo bloqueado. Pruebas de integración:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~<Clase>.<Método>"
  ```
  Son lentas (Testcontainers, Docker arriba): sólo donde la tarea lo pide, en primer plano.
- Capas: Automations no referencia Messaging ni Tenancy.Domain; toda traducción entre módulos va en `src/Bootstrapper/`. `tests/ArchitectureTests` sigue verde sin tocarlo.
- Sin compatibilidad hacia atrás del motor (D-4): una conexión de `other` con otro `fallback` se ignora en todas partes, también en versiones publicadas. Sin migración del jsonb: lo viejo se lee con los defaults (sin asignar, `Restart`).
- Textos para personas en español neutro, tuteando. Identificadores, códigos y nombres de enum sin traducir. Los enums nuevos van **al final** (se guardan por nombre).
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.
- **Despliegue:** backend primero. Contra este backend el frontend viejo sigue funcionando: ignora las claves nuevas y un `PUT` sin `exhaustedAction` guarda `Restart` (que es el default).

## Review Focus

1. **El tenant cambia la regla con una corrida esperando al cliente:** la corrida viva lee los ajustes del chatbot en cada paso (`FindEngineSettingsAsync`, como la palabra de reinicio y los intentos, D-C7), así que el siguiente agotamiento usa la regla nueva aunque la versión publicada sea la misma (Task 6, `AWaitingRunUsesTheRuleTheChatbotHasNow`).
2. **El miembro dejó de poder responder entre publicar y el traspaso** (lo sacaron, le quitaron el rol): cae sin asignar, suena a todos, la corrida termina `HandedOff` y el evento no trae `assignedMemberId` (Task 9, `AHandoffToAMemberWhoCannotReplyFallsUnassigned`).
3. **Un borrador viejo con `other` conectada y `fallback: "repeat"`** que se publica sin volver a guardarse: la versión conserva la clave, pero el motor no la sigue y aplica la regla del chatbot (Task 5, `ExhaustedRepeatButtonsIgnoreAHiddenOtherAndRestart`; Task 4, `AConnectedOtherWithRepeatIsNotFollowedForReachability`).
4. **Un archivo de otro tenant** con miembros en bloques y en la regla: importa sin fallar, los miembros quedan sin asignar y la regla conserva `Handoff` (Task 8, `ImportDropsMembersWhoCannotReplyHereAndKeepsTheAction`).
5. **La palabra de reinicio con la regla en `Handoff`** sigue reiniciando (D-I5 no cambia) (Task 5, `TheRestartKeywordRestartsWhateverTheRule`).

---

### Task 1: Messaging — el traspaso del sistema puede asignar (D-1, D-7, D-8)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs:26-39` (`AutomationHandoffKind.AttemptsExhausted`), `:51-77` (`AutomationHandoff.AssignedMemberId`, parámetro de `Create`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` (método nuevo junto a `PauseAutomation`, `:383-407`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/HandoffCause.cs:5-18` (`HandoffCauseKind.AttemptsExhausted`, para que la paridad siga verde desde esta tarea)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/AutomationHandoffTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/HandoffCauseKindParityTests.cs` (sin cambios: debe seguir verde)

**Interfaces:**
- Produces:
  - `enum AutomationHandoffKind { Block, InvalidReply, SendFailed, FlowFailed, AttemptsExhausted }` y `enum HandoffCauseKind { …, AttemptsExhausted }`.
  - `sealed record AutomationHandoff(…, AutomationHandoffHttp? Http, Guid? AssignedMemberId = null)`; `AutomationHandoff.Create(…, AutomationHandoffHttp? http, Guid? assignedMemberId = null)` (`Guid.Empty` → `null`).
  - `ConversationEvent Conversation.HandOffAutomation(AutomationHandoff handoff, DateTimeOffset now)`: mismas comprobaciones y en el mismo orden que `PauseAutomation` (abierta, hay asistente, no está pausado); marca `Paused` con motivo `Handoff` y sin actor; con `handoff.AssignedMemberId` asigna (`AssignedMemberId`, `AssignedAt = now`); devuelve **un** evento `AutomationPaused` con `Actor = null`, `PauseReason = Handoff`, `Handoff = handoff`.

- [ ] **Step 1: RED.** En `ConversationAutomationTests`:
  - `AHandoffToAMemberPausesAndAssignsWithoutActor` — conversación con asistente activo, `HandOffAutomation(AutomationHandoff.Create(AutomationHandoffKind.AttemptsExhausted, null, "q", "Documento", null, null, null, member), now)` → `AutomationStatus = Paused`, `AutomationPauseReason = Handoff`, `AutomationPausedByMemberId = null`, `AssignedMemberId = member`, `AssignedAt = now`; el evento: `Type = AutomationPaused`, `Actor = null`, `Handoff.AssignedMemberId = member`.
  - `AnUnassignedHandoffLeavesNoAssignee` — sin miembro: `AssignedMemberId = null` y el mismo evento que `PauseAutomation(null, Handoff, now, handoff)` hoy.
  - `AHandoffNeedsAnOpenConversationWithAnActiveAssistant` — `Paused` → `automation_already_paused`; `None` → `automation_not_available`; resuelta → `conversation_not_open`; en los tres, `AssignedMemberId` y `Version` no cambian.
  Con el asistente activo la conversación no tiene asesor (tomarla pausa el bot y `ResumeAutomation` la libera), así que `HandOffAutomation` no compara contra un asignado previo.
  En `AutomationHandoffTests`: `TheAssignedMemberSurvivesCleaning` — `Create(…, assignedMemberId: m).AssignedMemberId == m`; `Guid.Empty` → `null`.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationAutomationTests|FullyQualifiedName~AutomationHandoffTests"
  ```
- [ ] **Step 2: GREEN.** `HandOffAutomation` reutiliza `EnsureOpenForAutomation` y `MarkPaused(null, Handoff, now)`; asigna con los campos directamente (no con `TransferTo`, que exige actor y escribe `Transferred`: D-8 pide que el hilo no diga «Alguien transfirió…»). Los dos enums suman `AttemptsExhausted` al final.
- [ ] **Step 3:** `Modules.Messaging.UnitTests` completo y `HandoffCauseKindParityTests` verdes.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HandoffCauseKindParityTests"
  ```
- [ ] **Step 4: Commit** `feat(messaging): el traspaso del asistente puede asignar la conversación sin actor (spec 2026-10-25 D-1, D-7, D-8)`.

### Task 2: Messaging — `assignedMemberId` en el evento y `assignedTo` en el hilo (D-8)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` (`WriteHandoff` `:90-116`, `ReadHandoff` `:129-153`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:122-124` (`AutomationHandoffDto`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` (`MemberIdsOf` `:309-334`, `ToDto(AutomationHandoff?)` `:347-354` recibe `memberNames`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationMappingTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: clave JSON `handoff.assignedMemberId` (omitida si `null`); `AutomationHandoffDto(…, AutomationHandoffHttpDto? Http, MemberRefDto? AssignedTo = null)` → `handoff.assignedTo` del Contrato.

- [ ] **Step 1: RED.** `ConversationEventJsonTests`:
  - `AnAssignedHandoffRoundTrips` — `kind = AttemptsExhausted` con miembro → `Parse(Serialize(x)) == x`.
  - `TheAssignedMemberIsWrittenAsAssignedMemberId` — el JSON contiene `"kind":"AttemptsExhausted"` y `"assignedMemberId":"<guid D>"`; sin miembro no aparece la clave.
  - `AnUnreadableAssignedMemberReadsAsNull` — `"assignedMemberId":"ana"` o `12` → `AssignedMemberId = null` y el resto de la causa se lee.
  `ConversationAutomationMappingTests`:
  - `AnAssignedHandoffCarriesTheMemberWithItsName` — `MessageMapping.MemberIdsOf` incluye el miembro; con `memberNames = { m: "Ana Ruiz" }` el `Event.Handoff.AssignedTo == new MemberRefDto(m, "Ana Ruiz")`; sin nombre → `"Miembro eliminado"`; sin miembro → `AssignedTo = null`.
  ```powershell
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationEventJsonTests|FullyQualifiedName~ConversationAutomationMappingTests"
  ```
- [ ] **Step 2: GREEN.** `ReadHandoff` usa `ReadId(handoff, "assignedMemberId")` y lo pasa a `AutomationHandoff.Create`; `ToDto` usa el mismo `Member(...)` que actor/target.
- [ ] **Step 3: Commit** `feat(messaging): el hilo dice a quién asignó el asistente la conversación (spec 2026-10-25 D-8)`.

### Task 3: Flujo — destinos, `other` condicionada y normalización (D-1, D-3, D-4)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs:43-54, 106-107, 140-152` (records y `<summary>` de `InteractiveFallbacks`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowJson.cs` (`Write` `:159-173, 209-210, 283-285`; `ReadNode` `:332-337, 346`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNodeKinds.cs:24-61` (`OutputsOf`, `ListOutputs`)
- Create: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNormalizer.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs:557` (`SaveChatbotDraftHandler` guarda `FlowJson.Serialize(FlowNormalizer.Normalize(flow))` y valida lo normalizado)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowJsonTests.cs`, `tests/Modules/Automations/Modules.Automations.UnitTests/FlowNormalizerTests.cs` (nuevo), `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Produces:
  - `ButtonsNode(…, string Fallback, Guid? FallbackMemberId = null)`, `ListNode(…, string Fallback, Guid? FallbackMemberId = null)`, `HandoffNode(…, string? Text, Guid? MemberId = null)`.
  - `FlowNodeKinds.OutputsOf`: Botones = ids de botón + `other` **sólo** con `Fallback == InteractiveFallbacks.OtherOutput`; Lista = `chosen`, `extra`?, `tooMany`?, + `other` con la misma condición.
  - `static class FlowNormalizer { static FlowDefinition Normalize(FlowDefinition flow); }`: en Botones/Lista quita la clave `other` de `Outputs` salvo con `otherOutput`, y deja `FallbackMemberId = null` salvo con `handoff`; los demás bloques salen iguales.

- [ ] **Step 1: RED.**
  - `FlowJsonTests.TheHandoffDestinationsRoundTrip` — `memberId` en `handoff` y `fallbackMemberId` en `buttons`/`list` → `Parse(Serialize(x)) == x`; `Serialize` escribe `"memberId":null` y `"fallbackMemberId":null` cuando no hay.
  - `FlowJsonTests.AFlowWithoutDestinationsReadsUnassigned` — el JSON de hoy (sin las claves) → `MemberId = null`, `FallbackMemberId = null` (D-4).
  - `FlowJsonTests.ADestinationThatIsNotAGuidIsMalformed` — `"memberId":"ana"` → `FlowFormatException` con `Code = flow.malformed` (como `reasonId`, vía `JsonFields.OptionalGuid`).
  - Reemplazar `OutputsOfAButtonsNodeAreItsButtonsPlusOther` (`FlowJsonTests.cs:124`) por `[Theory] OutputsOfButtonsAndListIncludeOtherOnlyWithOtherOutput` — `repeat` y `handoff` → sin `other`; `otherOutput` → con `other` al final (Botones y Lista).
  - `FlowNormalizerTests`: `AHiddenOtherRouteIsDropped` (con `repeat` y `handoff` la clave `other` desaparece aunque esté conectada; con `otherOutput` queda), `AFallbackMemberOutsideHandoffIsDropped`, `TheOtherNodesAreUntouched` (mensaje, pregunta, bloque `handoff` con miembro: iguales).
  - `ChatbotsApiTests.SavingADraftDropsTheRouteTheCanvasDoesNotDraw` — `PUT …/draft` con Botones `repeat` + `other` conectada → el `GET` devuelve el bloque sin `outputs.other`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowJsonTests|FullyQualifiedName~FlowNormalizerTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests.SavingADraftDropsTheRouteTheCanvasDoesNotDraw"
  ```
- [ ] **Step 2: GREEN.** Lectura con `JsonFields.OptionalGuid` (claves `memberId`, `fallbackMemberId`); escritura con `WriteGuid`. Actualizar los `<summary>` de `InteractiveFallbacks.OtherOutput` («sale por `other`; el validador la exige conectada») y de `FlowNode` (D-3).
- [ ] **Step 3:** `Modules.Automations.UnitTests` completo: lo que se rompa por `OutputsOf` (validador, motor) es esperable y lo arreglan las Tasks 4 y 5; anotar en el reporte qué pruebas quedaron rojas y por qué. **Commit** `feat(automations): destino del traspaso en el flujo y «otra respuesta» sólo con otherOutput (spec 2026-10-25 D-1, D-3, D-4)`.

### Task 4: Validador — `other` obligatoria, `invalid` sin aviso y miembros (D-3, D-5, D-8)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowIssue.cs:18-20` (`FlowValidationContext` + `IReadOnlySet<Guid> HandoffMembers`), `:24-74` (`HandoffMemberInvalid = "handoff.member_invalid"`, `ExhaustedMemberInvalid = "exhausted.member_invalid"`, `<summary>` de `NodeOutputUnconnectedOptional`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs:41-43` (`OptionalOutputs` → sólo `tooMany`; `invalid` sin conectar no emite nada), `:131-150` (`CheckOutputs`), `:159-240` (`CheckNode`: `HandoffNode`, `ButtonsNode`, `CheckList`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/ChatbotEditorBuilder.cs:165-169` (por ahora `HandoffMembers = new HashSet<Guid>()`; la Task 7 pasa los reales)
- Modify (helpers): `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs:552`, `SystemTemplatesTests.cs:15`, `ClasicFlow.cs:19`, `AnnexAFlow.cs:23`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces: `FlowValidationContext(IReadOnlySet<string> Channels, IReadOnlySet<Guid> ActiveExternalApis, IReadOnlySet<Guid> PausedExternalApis, bool AllConnectionsHaveBusinessHours, IReadOnlySet<Guid> HandoffMembers)`; `FlowIssueCodes.HandoffMemberInvalid`, `FlowIssueCodes.ExhaustedMemberInvalid`.

- [ ] **Step 1: RED.** En `FlowValidatorTests` (`Ctx(…, members: params Guid[])` agrega el conjunto):
  - `AnUnconnectedOtherWithOtherOutputIsAnError` — `Severity = Error`, `Code = node.output_unconnected`, `NodeId` del bloque; con `repeat` no hay issue de `other`.
  - `AConnectedOtherWithRepeatIsNotFollowedForReachability` (Review Focus 3) — un bloque al que sólo llega el `other` de unos Botones `repeat` → `node.unreachable` (Warning).
  - Reemplazar `AnUnconnectedInvalidIsAWarning` (`:50`) por `AnUnconnectedInvalidIsNotAnIssue` (D-8): Pregunta abierta y Pedir adjunto sin `invalid` → ningún issue con ese `NodeId`.
  - `AnUnconnectedTooManyIsStillAWarning`.
  - `AHandoffToAMemberWhoCannotReplyIsAnError` — bloque `handoff` con `MemberId` fuera de `HandoffMembers` → `Error`, `handoff.member_invalid`, `NodeId` del bloque; mensaje: `$"«{node.Title}» asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro o déjala sin asignar."`.
  - `AFallbackHandoffToAMemberWhoCannotReplyIsAnError` — Botones y Lista con `fallback = handoff`; mensaje que nombra «Si responde otra cosa».
  - `AnUnassignedHandoffIsValid` y `AMemberWhoCanReplyIsValid`.
  - `AFallbackMemberIsIgnoredOutsideHandoff` — `repeat` con un `FallbackMemberId` desconocido (borrador sin normalizar) → sin issue.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowValidatorTests|FullyQualifiedName~SystemTemplatesTests"
  ```
- [ ] **Step 2: GREEN.** `CheckOutputs` salta `invalid`; `other` cae en el camino de error porque ya no es opcional; `CheckReachability` no cambia (ya recorre `OutputsOf`). `SystemTemplatesTests` debe seguir sin errores (`welcome-menu-handoff.json` usa `repeat` con `other: null`, que ya no cuenta).
- [ ] **Step 3: Commit** `feat(automations): el validador exige «otra respuesta» sólo con otherOutput y marca miembros que no pueden responder (spec 2026-10-25 D-3, D-5, D-8)`.

### Task 5: Motor — la regla del chatbot al agotar los intentos (D-2, D-3)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/ChatbotEnums.cs` (`enum AttemptsExhaustedAction { Restart, Handoff }`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/EngineSettings.cs:5`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:22-43` (`Handoff.MemberId`, `HandoffReasons.AttemptsExhausted`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs:186-226` (`Reject` y su `<summary>`), `:324` (bloque `handoff` con su miembro)
- Modify (fixture): `tests/Modules/Automations/Modules.Automations.IntegrationTests/Fixtures/clasic.json` (quitar las siete `"other": "handoff"` de Botones con `repeat`, `:180, 382, 511, 660, 700, 744, 798`: ya no las sigue nadie)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineWaitingTests.cs`, `FlowEngineTests.cs`, `ClasicFlowTests.cs`

**Interfaces:**
- Produces:
  - `enum AttemptsExhaustedAction { Restart, Handoff }` (Domain, se guarda por nombre).
  - `sealed record EngineSettings(string RestartKeyword, int MaxAttempts, AttemptsExhaustedAction ExhaustedAction = AttemptsExhaustedAction.Restart, Guid? ExhaustedMemberId = null)`.
  - `Effect.Handoff(string NodeId, string Reason = HandoffReasons.Block, Guid? MemberId = null)`; `HandoffReasons.AttemptsExhausted = "attempts_exhausted"`.

- [ ] **Step 1: RED.** En `FlowEngineWaitingTests` (`Settings(AttemptsExhaustedAction action = Restart, Guid? member = null)`):
  - Reemplazar `ExhaustedRepeatButtonsTakeOtherWhenConnected` (`:629`) por `ExhaustedRepeatButtonsIgnoreAHiddenOtherAndRestart` (D-4, Review Focus 3) — `Buttons("b", other: "o")` y tres respuestas inválidas: dos reenvíos y, en la tercera, el trace tiene `{ NodeId: "b", Outcome: TraceOutcomes.Restart }` y ninguna entrada `Outcome: other`; `Attempts = 0`.
  - `ExhaustedAttemptsHandOffUnassignedWithTheChatbotRule` — `Question("q", validation: Digits, invalid: null)`, `Settings(Handoff)`, tres «abc» → `Effects == [new Effect.Handoff("q", HandoffReasons.AttemptsExhausted, null)]`, `Status = HandedOff`, `CurrentNodeId = "q"`.
  - `ExhaustedAttemptsHandOffToTheChosenMember` — igual con `Settings(Handoff, m)` → `MemberId = m`.
  - `ExhaustedRepeatButtonsApplyTheChatbotRule` — `Buttons("b")` + `Settings(Handoff, m)` → tercera: `Handoff("b", AttemptsExhausted, m)`.
  - `AConnectedInvalidStillWinsOverTheRule` — `Question(invalid: "bad")` + `Settings(Handoff)` → sale por `invalid` (D-2: la regla sólo aplica sin la salida conectada).
  - `AnUnconnectedOtherOutputRepeatsThenAppliesTheRule` — `Buttons("b", "otherOutput")` sin `other` (borrador simulado sin validar, D-3) + `Settings(Handoff)` → reenvía y en la tercera traspasa.
  - `AHandoffFallbackCarriesItsMember` — `Buttons` con `fallback = handoff` y `FallbackMemberId = m` → `[new Effect.Handoff("b", HandoffReasons.InvalidReply, m)]` en la primera inválida.
  - `TheRestartKeywordRestartsWhateverTheRule` (D-2, Review Focus 5) — `Settings(Handoff, m)`, «reiniciar» → reinicia como `TheRestartKeywordRestartsFromTheFirstNode` (`:359`).
  En `FlowEngineTests`: `AHandoffBlockCarriesItsMember` — `HandoffNode` con `MemberId = m` → `Effect.Handoff("n1", HandoffReasons.Block, m)`; sin miembro, los asserts de `:245, 250` siguen iguales.
  En `ClasicFlowTests`: `ThePolicyButtonsHandOffWhenAttemptsRunOutAndTheChatbotSaysSo` — `Start = policy`, `new EngineSettings("reiniciar", 3, AttemptsExhaustedAction.Handoff)`, tres «hola» → `(HandedOff, "policy")` y `Effect.Handoff("policy", AttemptsExhausted, null)`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngine|FullyQualifiedName~ClasicFlowTests"
  ```
- [ ] **Step 2: GREEN.** `Reject` en este orden: `handoff` → `Handoff(node.Id, InvalidReply, FallbackMemberId)` y `Stop`; `otherOutput` con `other` conectada → `Leave(other)`; si quedan intentos → `Resend`; la salida de agotamiento existe sólo en Pregunta abierta y Pedir adjunto (`invalid`): conectada → `Leave(invalid)`; si no, `settings.ExhaustedAction` decide entre `Restart(node)` y `Handoff(node.Id, AttemptsExhausted, settings.ExhaustedMemberId)` + `Stop(node, HandedOff, TraceOutcomes.HandedOff)`.
- [ ] **Step 3:** `Modules.Automations.UnitTests` completo verde (las rojas que dejó la Task 3 quedan resueltas). **Commit** `feat(automations): al agotar los intentos manda la regla del chatbot (spec 2026-10-25 D-2, D-3)`.

### Task 6: `Chatbot` guarda la regla; migración y lectura del motor (D-2, D-4)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Chatbot.cs` (propiedades, `SetAttemptsExhausted`, `EngineSettings`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs:112-115` (columnas)
- Create: migración `AddChatbotAttemptsExhausted` en `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/Migrations/`
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRepositories.cs:26-28, 37-41`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:64-66` (`ServingChatbot` + `ExhaustedAction`, `ExhaustedMemberId`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:335`, `src/Modules/Automations/Modules.Automations.Application/Simulations.cs:76-78, 261`
- Modify (dobles): `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs:729-735`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotTests.cs`, `SimulationRunnerTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 5.
- Produces: `AttemptsExhaustedAction Chatbot.ExhaustedAction { get; private set; }` (default `Restart`), `Guid? Chatbot.ExhaustedMemberId { get; private set; }`, `void Chatbot.SetAttemptsExhausted(AttemptsExhaustedAction action, Guid? memberId, DateTimeOffset now)` (con `Restart` guarda `null`; `Touch`), `EngineSettings Chatbot.EngineSettings` (`=> new(RestartKeyword, MaxAttempts, ExhaustedAction, ExhaustedMemberId)`); columnas `automations.chatbots.exhausted_action varchar(16) NOT NULL DEFAULT 'Restart'` y `exhausted_member_id uuid NULL`.

- [ ] **Step 1: RED.**
  - `ChatbotTests.ANewChatbotRestartsWhenAttemptsRunOut` — `Create(...)` → `(Restart, null)`.
  - `ChatbotTests.SetAttemptsExhaustedKeepsTheMemberOnlyForHandoff` — `(Handoff, m)` → `m`; `(Restart, m)` → `null`; `Version` sube.
  - `ChatbotTests.TheEngineSettingsCarryTheRule` — `chatbot.EngineSettings == new EngineSettings(restart, max, Handoff, m)`.
  - `SimulationRunnerTests.TheSimulatorSaysWhereTheHandoffGoes` — `Effect.Handoff` sin miembro → línea de sistema «Pasaría la conversación a la bandeja, sin asignar»; con miembro → «Pasaría la conversación al miembro elegido».
  - `RunProcessorTests.AWaitingRunUsesTheRuleTheChatbotHasNow` (Review Focus 1) — corrida esperando en una pregunta sin `invalid` con dos intentos ya hechos; el doble de `FindEngineSettingsAsync` devuelve `(Handoff, m)` → el tercer entrante termina en `HandoffAsync` (la Task 9 afirma la causa y el miembro; aquí basta `RunStatus.HandedOff`).
  - `AutomationsPersistenceTests.TheAttemptsExhaustedRuleSurvivesTheDatabase` — guardar `(Handoff, m)`, releer en otro contexto: igual; y `FindEngineSettingsAsync` / `FindServingByConnectionAsync` la traen. `AnExistingRowReadsRestart` — fila insertada por SQL sin las columnas → `Restart`, `null`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ChatbotTests|FullyQualifiedName~SimulationRunnerTests|FullyQualifiedName~RunProcessorTests"
  ```
- [ ] **Step 2: GREEN.** EF: `HasConversion<string>().HasMaxLength(16).HasDefaultValue(AttemptsExhaustedAction.Restart).HasSentinel((AttemptsExhaustedAction)(-1))`, como `Trigger` (`:101-102`). Migración (PowerShell, `Api.exe` detenido):
  ```powershell
  dotnet ef migrations add AddChatbotAttemptsExhausted --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations
  ```
  Revisar que el `Up` sólo agregue las dos columnas con su default. `RunProcessor:335` y `Simulations.SettingsOf` usan `EngineSettings` del chatbot/proyección (una sola forma de armarlo).
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsPersistenceTests|FullyQualifiedName~AutomationsSchemaTests"
  ```
  **Commit** `feat(automations): el chatbot guarda qué hacer al agotar los intentos (spec 2026-10-25 D-2, D-4)`.

### Task 7: Miembros elegibles en el editor y en la validación (D-5, BFF)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (junto a `IAutomationConnections`, `:107-117`)
- Create: `src/Bootstrapper/AutomationMembers.cs`; Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:964-966` (registro)
- Modify: `src/Modules/Automations/Modules.Automations.Application/ChatbotEditorBuilder.cs` (constructor `:13-19`, `BuildAsync` `:34-63`, `ValidateAsync` `:99-106`, `Validate` `:151-171`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsDtos.cs:39-49` (`ChatbotEditorDto`, `ChatbotMemberDto`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Consumes: Tasks 4 y 6.
- Produces: `sealed record AutomationMemberRef(Guid Id, string Name)`; `interface IAutomationMembers { Task<IReadOnlyList<AutomationMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken); }` (adaptador sobre `IMessagingAssignees.ListAsync`: mismos miembros, mismo orden); `sealed record ChatbotMemberDto(Guid Id, string Name)`; `ChatbotEditorDto(…, IReadOnlyList<string> Variables, string ExhaustedAction, Guid? ExhaustedMemberId, IReadOnlyList<ChatbotMemberDto> Members)`.

- [ ] **Step 1: RED.**
  - `AutomationAdaptersTests.TheMembersAreTheAssigneesOfMessaging` — doble de `IMessagingAssignees` con dos `AssigneeDto` → dos `AutomationMemberRef` con id y nombre, en el mismo orden.
  - `ChatbotsApiTests.TheEditorBringsTheRuleAndTheMembersWhoCanReply` — `GET /chatbots/{id}` → `exhaustedAction = "Restart"`, `exhaustedMemberId = null`, `members` con el asesor sembrado (id y nombre) y sin un miembro sin `messaging.conversation.manage`.
  - `ChatbotsApiTests.AHandoffToAMemberWhoCannotReplyBlocksPublishing` — borrador con `handoff.memberId` = un Guid ajeno → `issues` trae `handoff.member_invalid` (Error) y `POST /publish` responde 422 con ese código en `errors.flow`.
  - `ChatbotsApiTests.TheExhaustedMemberMustStillReplyToPublish` — `PUT` con `("Handoff", asesor)`, suspender la membresía por SQL, `POST /publish` → 422 con `exhausted.member_invalid`; el `GET` lo muestra en `issues` con `nodeId: null`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AutomationAdaptersTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests"
  ```
- [ ] **Step 2: GREEN.** El builder lee los miembros **una vez** por request (como `apis`) y los usa para `members` y para `HandoffMembers`. `exhausted.member_invalid` lo agrega `Validate` (no el validador del flujo: es un ajuste del chatbot, como `trigger.keywords_empty`) cuando `ExhaustedAction == Handoff` y el miembro no está; mensaje: «Al agotar los intentos, el chatbot asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro en los ajustes.» Así lo ven el editor, `/validate` y publicar por el mismo camino.
- [ ] **Step 3:** `tests/ArchitectureTests` verde. **Commit** `feat(automations): el editor trae los miembros que pueden responder y valida los destinos (spec 2026-10-25 D-5)`.

### Task 8: Ajustes, duplicar, exportar e importar (D-2, D-5, D-8)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs:293-295` (`UpdateChatbotRequest`) y el mapeo al comando
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (`UpdateChatbotCommand` `:34-37`, `UpdateChatbotValidator` `:181-199`, `UpdateChatbotHandler` `:486-525`, `CreateChatbotHandler` `:440-444`, `DuplicateChatbotHandler` `:730-732`, `ExportChatbotHandler` `:813-826`, `ImportChatbotValidator` `:242-252`, `ImportChatbotHandler` `:859-907`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsDtos.cs:56-70` (`ChatbotExportDto`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/AutomationsErrorCodes.cs` (`ChatbotMemberCannotReply = "automations.chatbot.member_cannot_reply"`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`, `TemplatesApiTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 6 y 7.
- Produces: `UpdateChatbotRequest(…, IReadOnlyList<Guid>? TakeOverConnectionIds = null, string? ExhaustedAction = null, Guid? ExhaustedMemberId = null)`; `UpdateChatbotCommand(…, string? ExhaustedAction, Guid? ExhaustedMemberId)`; `ChatbotExportDto(…, DateTimeOffset ExportedAt, string? ExhaustedAction = null, Guid? ExhaustedMemberId = null)`.

- [ ] **Step 1: RED** (`ChatbotsApiTests`, salvo la última):
  - `SettingsSaveTheAttemptsExhaustedRule` — `PUT` con `("Handoff", asesor)` → la respuesta y el `GET` lo traen; con `("Restart", asesor)` → `exhaustedMemberId = null`.
  - `AnOmittedRuleSavesRestart` — el cuerpo de hoy, sin las claves → `"Restart"`, `null`.
  - `AnUnknownActionIsAValidationError` — `"exhaustedAction":"Later"` → 422 `validation.failed` con `errors.exhaustedAction`.
  - `AMemberWhoCannotReplyIsRefusedOnSave` (D-5) — `("Handoff", Guid ajeno)` → 422 `code = automations.chatbot.member_cannot_reply`; el chatbot no cambió (`version` igual).
  - `DuplicateKeepsTheRuleAndTheMembers` (D-8) — `POST /duplicate` y `POST /chatbots` con `source.kind = "duplicate"` conservan `("Handoff", asesor)` y los `memberId` del flujo.
  - `ExportCarriesTheRule` — el archivo trae `exhaustedAction` y `exhaustedMemberId`.
  - `ImportDropsMembersWhoCannotReplyHereAndKeepsTheAction` (D-8, Review Focus 4) — archivo con `handoff.memberId`, `fallbackMemberId` y `exhaustedMemberId` ajenos → importa; el borrador queda con los tres en `null` y `exhaustedAction = "Handoff"`; un `other` con `repeat` del archivo desaparece (normalizado).
  - `AnOldExportWithoutTheRuleImportsAsRestart`.
  - `TemplatesApiTests.CreatingFromATemplateStartsWithRestart` (D-8) — guardar como plantilla un chatbot con `("Handoff", asesor)` y un bloque con miembro; crear desde la plantilla → `"Restart"`, `null`, y el bloque **conserva** su `memberId`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests|FullyQualifiedName~TemplatesApiTests"
  ```
- [ ] **Step 2: GREEN.**
  - Validador: `ExhaustedAction` nulo o `Restart`/`Handoff` (mismo criterio que `TryParseTrigger`); en import bajo `export.exhaustedAction`.
  - `UpdateChatbotHandler`: con `Handoff` y miembro, consulta `IAutomationMembers.ListAsync` y, si no está, lanza `AutomationsDomainException(ChatbotMemberCannotReply, "Ese miembro ya no puede responder conversaciones: elige otro.")` **antes** de tocar el agregado; después `chatbot.SetAttemptsExhausted(...)`.
  - Import: un `Remap` de miembros contra el conjunto de `IAutomationMembers` (bloques y regla; la acción se conserva) y `FlowNormalizer.Normalize` antes de `Serialize`. Nota: se compara contra los miembros **que pueden responder**, no contra «pertenece al tenant»; un miembro propio sin permiso también queda sin asignar, que es lo que D-5 marcaría igual al publicar.
  - Plantillas no se tocan: siguen sin copiar ajustes (`Chatbots.cs:440-471`).
- [ ] **Step 3: Commit** `feat(automations): ajustes, duplicado, export e import llevan la regla de agotamiento (spec 2026-10-25 D-2, D-5, D-8)`.

### Task 9: El procesador traspasa al destino y el adaptador asigna (D-1, D-5, D-7)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:239-241` (`HandoffAsync` + `Guid? assignToMemberId`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:98-109, 170`
- Modify: `src/Bootstrapper/AutomationConversations.cs:79-115`
- Modify (dobles y llamadas): `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs:811`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunsPersistenceTests.cs:375`, `AutomationConversationsAdapterTests.cs:116, 141, 169, 192, 206, 224` (pasan `null`)
- Test: `RunProcessorTests.cs`, `AutomationConversationsAdapterTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2 y 5.
- Produces: `Task IAutomationConversations.HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)`; en `RunEffects`, `HandoffReasons.AttemptsExhausted` → `HandoffCauseKind.AttemptsExhausted`.

- [ ] **Step 1: RED.**
  - `RunProcessorTests` (el doble guarda `List<(HandoffCause Cause, Guid? Member)>`):
    - `AnExhaustedQuestionHandsOffWithItsCauseAndMember` — flujo con pregunta sin `invalid` y `ServingChatbot` con `(Handoff, m)` → `(new HandoffCause(AttemptsExhausted, null, "q", "<título>", null, null, null), m)`.
    - `AHandoffBlockPassesItsMember` — bloque `handoff` con `memberId = m` → `(Block…, m)`.
    - `AFailureNeverAssigns` — el camino de `FailAsync` (caso `TheReentryCapSaysFlowFailedLoopDetected`) → miembro `null`.
  - `AutomationConversationsAdapterTests` (integración):
    - `AHandoffToAMemberWhoCanReplyAssignsAndRingsOnlyThem` — con el asesor sembrado: por SQL `assigned_member_id = asesor`, `automation_status = 'Paused'`, `details->'handoff'->>'kind' = 'AttemptsExhausted'`, `details->'handoff'->>'assignedMemberId' = asesor`, ningún evento `Transferred`; el `RecordingNotifier` (`:384`) registró `InboundMessageAsync(…, assignedMemberId: asesor)`.
    - `AHandoffToAMemberWhoCannotReplyFallsUnassigned` (D-5, Review Focus 2) — Guid ajeno y, en otra variante, membresía suspendida → `assigned_member_id IS NULL`, pausada, sin clave `assignedMemberId`, aviso con `assignedMemberId: null`, y no lanza.
    - `HandoffPausesWithASystemActorAndAnEvent` (`:100-122`) sigue igual con `null`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationConversationsAdapterTests|FullyQualifiedName~RunsPersistenceTests"
  ```
- [ ] **Step 2: GREEN.** En el adaptador, dentro del scope: `Guid? assignee = assignToMemberId is { } id && await scope.ServiceProvider.GetRequiredService<IMessagingAssignees>().CanReplyAsync(tenantId, id, ct) ? id : null;`; `conversation.HandOffAutomation(ToHandoff(cause, assignee), now)` reemplaza a `PauseAutomation`; el aviso usa `assignedMemberId: assignee` y el comentario D-M5 se actualiza (sin asignar suena a todos; asignada, sólo a ese miembro, D-1). Con un miembro que no puede responder, un `LogInformation` con tenant, conversación y miembro (sin datos del cliente), para que soporte entienda por qué cayó sin asignar.
- [ ] **Step 3:** `Modules.Automations.UnitTests`, `Bootstrapper.UnitTests` y `tests/ArchitectureTests` verdes. **Commit** `feat(automations): el traspaso asigna al miembro elegido o cae sin asignar (spec 2026-10-25 D-1, D-5)`.

### Task 10: De punta a punta, suite completa y CLAUDE.md

**Files:**
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.cs`
- Modify: `CLAUDE.md` («Gotchas verificados»)

- [ ] **Step 1: RED → GREEN** (las piezas ya existen; si algo falla es un hueco de su tarea y se arregla allí).
  - `AnExhaustedQuestionAssignsTheConversationToTheChosenMember` — chatbot publicado con una pregunta de dígitos sin `invalid` y `PUT` con `("Handoff", asesor)`; tres «abc» por el webhook; drenar. Afirmar: corrida `HandedOff`; conversación `Open|Paused` asignada al asesor; el cliente recibió la pregunta tres veces y nada más (el traspaso por agotamiento no escribe, spec «Fuera de alcance»); y por `GET …/messages` el evento `handoff.kind = "AttemptsExhausted"`, `handoff.assignedTo.memberId = asesor` con su `displayName`.
  - `AnExhaustedQuestionRestartsByDefault` — mismo flujo sin tocar los ajustes → la corrida vuelve al inicio (D-4) y la conversación sigue con el asistente activo.
- [ ] **Step 2: Suite completa, una vez, en primer plano** (`Api.exe` detenido):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test
  ```
  Comparar las fallas por nombre contra la base de `develop`; sólo cuentan las nuevas.
- [ ] **Step 3: CLAUDE.md**, en «Gotchas verificados»: «**`other` de Botones/Lista existe sólo con `fallback = otherOutput`** (spec 2026-10-25 D-3): `FlowNodeKinds.OutputsOf` la omite con `repeat` y `handoff`, el motor no la sigue aunque la versión publicada la tenga, y `FlowNormalizer` la quita al guardar. Al agotar los intentos sin salida conectada manda `Chatbot.ExhaustedAction` (`Restart` por defecto), no el flujo. El traspaso a un miembro pasa por `IMessagingAssignees.CanReplyAsync` en el adaptador: si no puede, cae sin asignar sin fallar.»
- [ ] **Step 4: Commit** `test(automations): el traspaso por intentos agotados asigna al miembro elegido (spec 2026-10-25)` y `docs: gotcha del destino del traspaso`.

---

## Orden y dependencias

- Task 1 → Task 2 (Messaging). Task 3 → Task 4 → Task 5 → Task 6 → Task 7 → Task 8. Task 9 necesita 1, 2 y 5. Task 10 al final.
- El frontend sólo depende del **Contrato**; puede empezar en paralelo y se despliega después del backend.
- Nota operativa (no es código): un chatbot de pruebas que dependía de «repetir y después `other`» (el de Clasic) pasa a **volver al inicio** al agotar los intentos (D-4). Si debe traspasar, el tenant elige «Dejar en la bandeja sin asignar» en sus ajustes después del despliegue.
