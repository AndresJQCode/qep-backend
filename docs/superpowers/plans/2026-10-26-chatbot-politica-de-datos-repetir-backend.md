# Chatbots: «Política de datos» — repetir la pregunta ante «No acepto» — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el bloque `dataConsent` decida qué pasa con un «No acepto» —repetir la pregunta hasta un tope, salir por `rejected` (como hoy) o pasar a la bandeja—, que en los dos modos sin `rejected` ninguna salida del bloque deje la conversación muda, y que el hilo de Mensajería diga que el cliente no aceptó la política.

**Architecture:** El bloque gana tres campos (`onReject`, `maxAsks`, `onRejectMemberId`) con defaults que dejan igual todo flujo guardado. `rejected` existe sólo con `onReject = "rejectedOutput"`: `OutputsOf` la omite en los otros modos (lienzo, validador y alcanzabilidad dejan de verla) y `FlowNormalizer` la quita al guardar e importar, como `other` en Botones. El motor sigue puro: un contador nuevo de `RunState` (`ConsentRejections`, columna propia en `conversation_runs`) cuenta los «No acepto» del bloque; en «repetir» vuelve a preguntar por `Ask` y al tope emite `Effect.Handoff` con la razón nueva; los caminos que hoy salen por `rejected` sin respuesta (intentos agotados, cliente que no se puede registrar) pasan a la bandeja con el destino del bloque. La razón viaja a Mensajería por los dos enums espejo (`HandoffCauseKind` / `AutomationHandoffKind`), que se guardan por nombre en el jsonb del evento: sin migración en Messaging.

**Tech Stack:** .NET 10, EF Core + Npgsql, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-26-chatbot-politica-de-datos-repetir-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-chatbot-politica-de-datos-repetir-design.md) (R-1..R-7, aprobado por el owner el 2026-10-26). Antecedentes: [spec política de datos](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-chatbot-politica-de-datos-design.md) (D-1..D-12) y su plan [`2026-10-26-chatbot-politica-de-datos-backend.md`](2026-10-26-chatbot-politica-de-datos-backend.md); [spec destino del traspaso](../../../../qep-frontend/docs/superpowers/specs/2026-10-25-chatbot-destino-del-traspaso-design.md) (D-1, D-3, D-5) y su plan [`2026-10-25-chatbot-destino-del-traspaso-backend.md`](2026-10-25-chatbot-destino-del-traspaso-backend.md). El plan de frontend ([`qep-frontend/docs/superpowers/plans/2026-10-26-chatbot-politica-de-datos-repetir-frontend.md`](../../../../qep-frontend/docs/superpowers/plans/2026-10-26-chatbot-politica-de-datos-repetir-frontend.md)) consume el **Contrato** de abajo tal cual.

## Contrato (lo consume el plan de frontend)

**Bloque del flujo** (jsonb del borrador y de cada versión, `draft.flow`, `PUT …/draft`, export/import):

```json
{ "id": "n7", "kind": "dataConsent", "title": "Política de datos", "text": "Lee https://empresa.co/datos y dinos si aceptas.",
  "onReject": "repeat", "maxAsks": 3, "onRejectMemberId": null,
  "outputs": { "accepted": "n8" } }
```

| Clave | Tipo | Ausente / regla |
|---|---|---|
| `onReject` | `"repeat"` \| `"rejectedOutput"` \| `"handoff"` | ausente o `null` = `"rejectedOutput"` (un bloque viejo se lee como hoy). Otro valor → `flow.malformed`, como un `fallback` desconocido |
| `maxAsks` | entero | ausente o `null` = `3`. Veces **en total** que la persona ve la pregunta, contando la primera. Sólo cuenta con `"repeat"`; con los otros modos se guarda tal cual y nadie lo lee |
| `onRejectMemberId` | uuid \| `null` | ausente = `null` = **sin asignar**. Sólo cuenta con `"repeat"` y `"handoff"`; con `"rejectedOutput"` el backend lo guarda `null` |
| `outputs.rejected` | id \| `null` | existe **sólo** con `"rejectedOutput"`; con otro modo el backend la quita al guardar el borrador o importar, y nadie la sigue (motor, validador, alcanzabilidad), tampoco en versiones ya publicadas |

- El backend escribe siempre las tres claves (`onRejectMemberId: null` incluido).
- Las etiquetas de pantalla son del frontend: `repeat` = «Repetir la pregunta», `rejectedOutput` = «Salir por «No acepto»», `handoff` = «Pasar a la bandeja».

**`issues` del editor, `/validate` y 422 de publicar** (`errors.flow`, «código: mensaje»):

| Código | Severidad | Cuándo | Mensaje |
|---|---|---|---|
| `data_consent.max_asks_invalid` | Error | `onReject = "repeat"` y `maxAsks` fuera de 1..5 | `«Preguntar hasta» de «{título}» es {n}: tiene que estar entre 1 y 5.` |
| `handoff.member_invalid` | Error | `onReject` ∈ `repeat`, `handoff` y `onRejectMemberId` no puede responder | `«Si no acepta» de «{título}» asigna la conversación a alguien que ya no puede responder conversaciones: elige otro miembro o déjala sin asignar.` |
| `node.output_unconnected` | Error | `rejected` sin conectar **sólo** con `"rejectedOutput"` (como hoy); `accepted` sin conectar siempre | sin cambios |

`text.empty`, `text.too_long` y `data_consent.no_link` (aviso) no cambian.

**Simulador** (`transcript[]` con `from: "system"`, además de las líneas de hoy):

- Cada «No acepto» que no agota el tope: `«Registraría que el cliente no aceptó la política de datos.»` y la pregunta otra vez como línea `bot` con los dos botones.
- Al traspasar por «No acepto» (tope agotado en `repeat`, o el primero en `handoff`), después de la línea `Registraría…`: `«{destino}: no aceptó {n} veces.»` con `n ≥ 2`, o `«{destino}: no aceptó la política de datos.»` con `n ≤ 1`, donde `{destino}` es `Pasaría la conversación a la bandeja, sin asignar` o `Pasaría la conversación al miembro elegido` (las frases de hoy).
- Intentos agotados en `repeat`/`handoff`: `«No registraría nada: se agotaron los intentos.»` y después la línea de traspaso de hoy (`Pasaría la conversación a la bandeja, sin asignar` / `…al miembro elegido`). En `rejectedOutput` sigue `«Seguiría por «No acepto» sin registrar nada: se agotaron los intentos.»`.

**Hilo** (`MessageEventDto.handoff` de un `AutomationPaused` con `pauseReason: "Handoff"`; `kind` por nombre):

| Camino | `kind` | `code` | `nodeId` / `nodeTitle` | `assignedTo` |
|---|---|---|---|---|
| Tope de «No acepto» agotado en `repeat`; primer «No acepto» en `handoff` | **`"DataConsentRejected"`** (nuevo) | `null` | el bloque | `onRejectMemberId` si todavía puede responder (D-5 del destino), si no `null` |
| Intentos agotados (respuestas inválidas) en `repeat`/`handoff` | `"AttemptsExhausted"` (existente) | `null` | el bloque | ídem |
| Cliente que no se puede registrar, en `repeat`/`handoff` | **`"DataConsentUnavailable"`** (nuevo) | `null` | el bloque | ídem |

Un backend viejo nunca manda los dos `kind` nuevos.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\chatbot-politica-de-datos-repetir`, rama `feature/chatbot-politica-de-datos-repetir` desde `develop`, con su propio `.codegraph/` (`gentle-ai codegraph init --cwd <worktree>`; nunca copiar el índice de otro checkout). Rutas absolutas; nunca tocar el checkout hermano `qep-backend`. `git branch --show-current` antes de tocar y antes de cada commit; si dice `main`, parar. Merge a `develop`, nunca a `main`, nunca push sin pedido.
- Conventional commits en español citando la decisión (`spec 2026-10-26 repetir R-3`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo; pisa cualquier recordatorio del harness).
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea sólo las clases tocadas; la suite completa una vez, en la Task 7.
- Comandos en **PowerShell**. `Api.exe` (o `dotnet Api.dll`) **detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`. Pruebas de integración con `$env:Seed__Enabled = "false"` en línea aparte; son lentas (Testcontainers, Docker arriba): sólo donde la tarea lo pide, en primer plano.
- Capas: Automations no referencia Messaging (`AutomationsLayerTests.cs`); la traducción de la razón va en `src/Bootstrapper/AutomationConversations.cs:119-133` y la amarra `HandoffCauseKindParityTests`. `tests/ArchitectureTests` sigue verde sin tocar los `*LayerTests.cs`.
- Valores fijos del spec: modos en el orden `repeat`, `rejectedOutput`, `handoff`; default `rejectedOutput` (R-1); `maxAsks` 1..5, default 3, «en total, incluida la primera» (R-1); el contador de «No acepto» y los intentos inválidos son independientes (R-4); cada «No acepto» registra su fila `Rejected` (anti-objetivo «que una repetición no deje prueba»); nada nuevo sigue por `accepted` sin un toque (anti-objetivo).
- En `repeat` y `handoff` el motor **no lee** `EngineSettings.ExhaustedAction` ni `ExhaustedMemberId` (R-4): manda el bloque.
- Textos para personas en español neutro, tuteando. Identificadores, códigos y nombres de enum sin traducir; los enums se guardan por nombre.
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.
- **Despliegue:** backend primero. Un frontend viejo no manda los campos nuevos (el bloque queda en `rejectedOutput`) y pinta los `kind` nuevos con su texto genérico (`HANDOFF_FALLBACK`, `handoff-cause.ts:8-9, 204-205`).

## Review Focus

1. **Un toque sobre la pregunta anterior después de repetir**: WhatsApp deja tocar «No acepto» en el primer mensaje cuando ya salió el segundo. Lo frena `IsStaleConsentTap` (`FlowEngine.cs:103-107`) siempre que `RunEffects` complete el `PromptMessageId` de la **nueva** pregunta; debe contar como respuesta inválida —sin fila y sin subir el contador— (Task 3, `ATapOnThePreviousPromptAfterARepeatIsNotARejection`; Task 7, `ATapOnTheFirstPromptAfterARepeatRecordsNothing`).
2. **Un bloque publicado antes de este cambio** (sin las tres claves, `rejected` conectada): se comporta exactamente como hoy, también en una corrida `Waiting` guardada antes del despliegue sin la columna nueva (Task 1, `AnOldDataConsentReadsAsRejectedOutput`; Task 3, `AnOldBlockLeavesByRejectedAsBefore`; Task 4, `ARunSavedBeforeTheCounterReadsZero`).
3. **Respuestas inválidas mezcladas con «No acepto»**: ni suben el contador ni lo reinician, y un «No acepto» no suma ni reinicia intentos (Task 3, `InvalidRepliesAndRejectionsCountApart`).
4. **La palabra de reinicio o una corrida nueva** (un asesor devolvió la conversación al asistente) vuelve a preguntar desde 1 (Task 3, `TheRestartKeywordStartsTheCountAgain`; Task 4, `ANewRunStartsWithoutRejections`).
5. **`rejected` conectada en un modo que no la tiene** (un import o una versión publicada a mano): el motor nunca la sigue y el validador no la exige ni la usa para la alcanzabilidad (Task 2, `ABlockBehindAHiddenRejectedIsUnreachable`; Task 3, `ARepeatBlockNeverFollowsAHiddenRejected`).

## Mapa de archivos

| Archivo | Qué |
|---|---|
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs` (`DataConsentNode` `:112-115`, `InteractiveFallbacks` `:148-160`) | Modify: campos y `DataConsentRejectActions` |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNodeKinds.cs` (`OutputsOf` `:30-43`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowJson.cs` (`Write` `:289-291`, `ReadNode` `:356`, `JsonFields` `:608-660`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNormalizer.cs` (`:12-25`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs` (`CheckNode` `:214-223`, `CheckFallbackMember` `:272-283`, `MinAttempts`/`MaxAttempts` `:28-29`), `FlowIssue.cs` (`:52`) | Modify |
| `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (`DropForeignMembers` `:938-952`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Engine/RunState.cs` (`:58-71`), `Effect.cs` (`HandoffReasons` `:57-67`), `FlowEngine.cs` (`OnDataConsent` `:147-161`, `Walk` `:176-208`, `Reject` `:242-293`, `Restart` `:300-308`, `Leave` `:316-321`, `Consent` `:341-349`, `Go` `:385-386`, `Result` `:482-483`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs` (`Apply` `:88-100`, `ToState` `:149-156`), `Simulation.cs` (`ToState` `:90-98`, `WriteState` `:101-105`, `StateRow` `:108-110`) | Modify |
| `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs` (`:205-208`), migración `AddRunConsentRejections` | Modify / Create |
| `src/Modules/Automations/Modules.Automations.Application/Simulations.cs` (`:88-96`) | Modify |
| `src/Modules/Automations/Modules.Automations.Application/Engine/HandoffCause.cs` (`:5-21`), `Engine/RunEffects.cs` (`:131-140`) | Modify |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs` (`:26-42`), `Modules.Messaging.Application/MessagingDtos.cs` (`:125-128`, comentario) | Modify |

---

### Task 1: El bloque gana el modo, el tope y el destino (R-1, R-2)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs:112-115` y clase nueva junto a `InteractiveFallbacks` (`:148-160`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNodeKinds.cs:40`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowJson.cs:289-291, 356` y `JsonFields` (`:608-660`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNormalizer.cs:12-25`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowJsonTests.cs`, `FlowNormalizerTests.cs`, `TemplateParametersTests.cs`

**Interfaces:**
- Produces:
  - `static class DataConsentRejectActions { const string Repeat = "repeat"; const string RejectedOutput = "rejectedOutput"; const string Handoff = "handoff"; static readonly IReadOnlySet<string> All; }` y `static class DataConsentAsks { const int Min = 1; const int Max = 5; const int Default = 3; }`.
  - `sealed record DataConsentNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, string OnReject = DataConsentRejectActions.RejectedOutput, int MaxAsks = DataConsentAsks.Default, Guid? OnRejectMemberId = null)`.
  - `OutputsOf(DataConsentNode)` = `[accepted, rejected]` con `RejectedOutput`; `[accepted]` con los otros dos.
  - `static string? JsonFields.OptionalOneOf(JsonElement element, string name, string where, IReadOnlySet<string> allowed)` — `null` si falta o es `null`; fuera de `allowed` → el mismo `Malformed` que `RequiredOneOf`.

- [ ] **Step 1: RED.**
  - `FlowJsonTests.ADataConsentWithItsRejectSettingsRoundTrips` — el JSON del Contrato → `OnReject = "repeat"`, `MaxAsks = 3`, `OnRejectMemberId = null`; `Parse(Serialize(x)) == x`; `Serialize` escribe `onReject`, `maxAsks` y `onRejectMemberId: null`.
  - `FlowJsonTests.AnOldDataConsentReadsAsRejectedOutput` (Review Focus 2) — el JSON del plan base (sin las tres claves) → `("rejectedOutput", 3, null)` y `Outputs["rejected"]` conservada.
  - `FlowJsonTests.AnUnknownRejectActionIsMalformed` — `"onReject": "ask"` → `FlowFormatException` con código `flow.malformed`.
  - `FlowJsonTests.OutputsOfADataConsentDependOnTheRejectAction` — `[Theory]`: `rejectedOutput` → `["accepted", "rejected"]`; `repeat` y `handoff` → `["accepted"]`.
  - `FlowNormalizerTests.ADataConsentWithoutTheRejectedOutputDropsIt` — `repeat` y `handoff` con `rejected: "n9"` → sin la clave; `rejectedOutput` la conserva.
  - `FlowNormalizerTests.TheRejectMemberOnlyStaysWhenItHandsOff` — `rejectedOutput` con miembro → `null`; `repeat` y `handoff` lo conservan; `MaxAsks` nunca cambia.
  - `TemplateParametersTests.ADataConsentKeepsItsRejectSettings` — `{param.x}` en `text` se sustituye y `OnReject`, `MaxAsks`, `OnRejectMemberId` llegan iguales (el `with` de `Templates.cs:122` ya los pasa; la prueba lo amarra).
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowJsonTests|FullyQualifiedName~FlowNormalizerTests|FullyQualifiedName~TemplateParametersTests"
  ```
  Esperado: no compila (`CS0117 'DataConsentNode' does not contain a definition for 'OnReject'`).
- [ ] **Step 2: GREEN.** `ReadNode`: `OptionalOneOf(…, "onReject", …, DataConsentRejectActions.All) ?? RejectedOutput`, `OptionalInt(…, "maxAsks", …) ?? DataConsentAsks.Default`, `OptionalGuid(…, "onRejectMemberId", …)`. `Write`: `writer.WriteString("onReject", …)`, `writer.WriteNumber("maxAsks", …)`, `WriteGuid(writer, "onRejectMemberId", …)`. `OutputsOf` con una función `RejectedOutput(string onReject)` como `OtherOutput` (`:50`). `FlowNormalizer`: `DataConsentNode consent => consent with { Outputs = …sin rejected salvo RejectedOutput…, OnRejectMemberId = …null con RejectedOutput… }`; generalizar `WithoutHiddenOther` a `WithoutHidden(outputs, string key, bool keep)` para no duplicar el filtro. Comentarios citando `spec 2026-10-26 repetir R-1, R-2`.
- [ ] **Step 3:** el mismo comando, verde; `Modules.Automations.UnitTests` completo verde (los `new DataConsentNode(…)` existentes compilan por los defaults). **Commit** `feat(automations): modo, tope y destino de «Si no acepta» en el bloque Política de datos (spec 2026-10-26 repetir R-1, R-2)`.

### Task 2: Validador e import — el tope y el destino (R-5)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowIssue.cs:52` (`DataConsentMaxAsksInvalid`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs:214-223` (`case DataConsentNode`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs:944-950` (`DropForeignMembers`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `FlowIssueCodes.DataConsentMaxAsksInvalid = "data_consent.max_asks_invalid"`. El miembro inválido reusa `FlowIssueCodes.HandoffMemberInvalid` con el mensaje del Contrato.

- [ ] **Step 1: RED** (`Consent(string onReject, int maxAsks = 3, Guid? member = null, string? rejected = "end")` arma el bloque):
  - `FlowValidatorTests.ARepeatCapOutsideOneToFiveIsAnError` — `[Theory]` `repeat` con `0` y `6` → `data_consent.max_asks_invalid` (Error, `NodeId` del bloque) con `«Preguntar hasta» de «Política de datos» es 6: tiene que estar entre 1 y 5.`; con `1` y `5` → sin el issue.
  - `FlowValidatorTests.TheCapIsOnlyCheckedWhenItRepeats` — `rejectedOutput` y `handoff` con `maxAsks = 9` → sin el issue.
  - `FlowValidatorTests.ARejectMemberWhoCannotReplyIsAnError` — `repeat` y `handoff` con un miembro fuera de `context.HandoffMembers` → `handoff.member_invalid` con el mensaje del Contrato; con un miembro de la lista o `null` → nada; `rejectedOutput` con un miembro huérfano → nada (borrador sin normalizar).
  - `FlowValidatorTests.RejectedIsOnlyRequiredWhenItExits` — `rejected: null` con `repeat` y `handoff` → ningún `node.output_unconnected`; con `rejectedOutput` → el de hoy («La salida «No acepto» de «Política de datos» no está conectada.»).
  - `FlowValidatorTests.ABlockBehindAHiddenRejectedIsUnreachable` (Review Focus 5) — `repeat` con `rejected: "n9"` y `n9` sin otra entrada → `node.unreachable` para `n9`; `node.unknown_target` no se reporta para una `rejected` oculta que apunta a un id inexistente.
  - `ChatbotsApiTests.ImportDropsARejectMemberWhoCannotReplyHere` — export con un `dataConsent` `handoff` cuyo `onRejectMemberId` no es miembro del tenant destino → el borrador importado lo tiene `null` y conserva `onReject` y `maxAsks` (como `ImportDropsMembersWhoCannotReplyHereAndKeepsTheAction`, `:806`).
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowValidatorTests"
  ```
  Esperado: FAIL (`Assert.Single() Failure: The collection was empty` para `data_consent.max_asks_invalid`).
- [ ] **Step 2: GREEN.** En `case DataConsentNode`: con `Repeat` y `MaxAsks` fuera de `DataConsentAsks.Min..Max` → `Error(DataConsentMaxAsksInvalid, …)`; con `Repeat` o `Handoff` y un miembro que no está en `context.HandoffMembers` → `Error(HandoffMemberInvalid, …)`. No se toca `CheckOutputs` ni la alcanzabilidad: los dos leen `OutputsOf` (Task 1). `DropForeignMembers`: `DataConsentNode consent => consent with { OnRejectMemberId = Keep(consent.OnRejectMemberId) }`.
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests.ImportDropsARejectMemberWhoCannotReplyHere"
  ```
  **Commit** `feat(automations): el validador revisa el tope y el destino de «Si no acepta» (spec 2026-10-26 repetir R-5)`.

### Task 3: Motor — repetir, pasar a la bandeja y no quedar mudo (R-3, R-4, R-4b)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/RunState.cs:53-71`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:57-67`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs` (rangos del mapa)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineDataConsentTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `RunState(…, ConsentPrompt? ConsentPrompt = null, int ConsentRejections = 0)` — los «No acepto» al bloque Política de datos en que espera la corrida; vuelve a 0 al entrar en un bloque por `Go`, en `Leave` y en `Restart`; **no** en `Resend` ni en `Ask`.
  - `HandoffReasons.DataConsentRejected = "data_consent_rejected"`, `HandoffReasons.DataConsentUnavailable = "data_consent_unavailable"`.

- [ ] **Step 1: RED** en `FlowEngineDataConsentTests` (flujo `consent → accepted: "yes"`, con `rejected: "no"` sólo en `rejectedOutput`; `Repeating(int maxAsks, Guid? member = null)` y `HandingOff(Guid? member = null)` arman el flujo; `Settings(AttemptsExhaustedAction.Restart)` salvo que se diga):
  - `RepeatingAsksAgainAfterARejectionAndRecordsIt` — `maxAsks: 3`, `Tapped("rejected")` → `Effects[0] == RecordDataConsent(…, Rejected, …)`, después `SendInteractive` con los dos botones y `WaitForCustomer`; `Status = Waiting`; `ConsentRejections = 1`; `ConsentPrompt` nuevo con `PromptMessageId = null`; `StepNumber` subió en 1; ningún `Leave` en el trace.
  - `ReachingTheCapHandsOffWithTheBlockDestination` — tres «No acepto» → tres `RecordDataConsent(Rejected)`; el tercero emite `Handoff("consent", "data_consent_rejected", Member)`, `Status = HandedOff`, trace `handedOff` (`TraceOutcomes.HandedOff`), `ConsentRejections = 3` (el simulador lo lee, Task 5); ningún `SendInteractive` después del tercero.
  - `ACapOfOneHandsOffOnTheFirstRejection` — `maxAsks: 1` → `RecordDataConsent` + `Handoff(…, "data_consent_rejected", null)`.
  - `HandingOffModeHandsOffOnTheFirstRejection` — `HandingOff(Member)` → `RecordDataConsent(Rejected)` y `Handoff(…, "data_consent_rejected", Member)`; `ConsentRejections = 0`.
  - `InvalidRepliesAndRejectionsCountApart` (Review Focus 3) — `maxAsks: 2`, `Settings` con 3 intentos: `Typed("hola")` (`Attempts = 1`, `ConsentRejections = 0`), `Tapped("rejected")` (`Attempts = 1`, `ConsentRejections = 1`, vuelve a preguntar), `Typed("hola")` (`Attempts = 2`), `Tapped("rejected")` → `Handoff(…, "data_consent_rejected", …)`.
  - `ExhaustedAttemptsHandOffWithTheBlockDestination` (R-4) — `Repeating(3, Member)` y `HandingOff(Member)` con `Settings(AttemptsExhaustedAction.Restart)` y tres inválidas → `DataConsentUnanswered("consent")` y `Handoff("consent", "attempts_exhausted", Member)`; ningún `RecordDataConsent`; ningún reinicio. Con `Settings(AttemptsExhaustedAction.Handoff, otroMiembro)` → el miembro sigue siendo `Member` (manda el bloque).
  - `ExhaustedAttemptsInRejectedOutputLeaveAsToday` — `rejectedOutput` → la prueba `ExhaustedAttemptsLeaveByRejectedWithoutRecording` (`:177`) sigue igual.
  - `AnUnrecordableCustomerHandsOffWithoutAsking` (R-4b) — `Repeating(3, Member)` y `HandingOff(Member)`, `DataConsent("consent", Accepted: false, CanRecord: false)` → `Handoff("consent", "data_consent_unavailable", Member)`, `Status = HandedOff`; sin `SendInteractive` ni `RecordDataConsent`. `rejectedOutput` sale por `rejected` como hoy (`:91`).
  - `ACurrentAcceptanceStillLeavesByAccepted` — `Repeating(3)` y `HandingOff()` con `Accepted: true` → sale por `accepted` (sin cambios).
  - `ARepeatBlockNeverFollowsAHiddenRejected` (Review Focus 5) — `Repeating(3)` con `outputs.rejected = "no"` (sin normalizar) → `Tapped("rejected")` vuelve a preguntar; el `SendText` de `no` nunca sale, ni al agotar el tope.
  - `AnOldBlockLeavesByRejectedAsBefore` (Review Focus 2) — `new DataConsentNode(…)` sin los campos nuevos → `Tapped("rejected")` registra y sale por `no`.
  - `ATapOnThePreviousPromptAfterARepeatIsNotARejection` (Review Focus 1) — estado tras repetir con `ConsentPrompt("consent", "…", Second)` y `ConsentRejections = 1`; `Tapped("rejected", "No acepto", quoting: First)` → sin `RecordDataConsent`, `Attempts = 1`, `ConsentRejections = 1`.
  - `TheRestartKeywordStartsTheCountAgain` (Review Focus 4) — `ConsentRejections = 2` esperando, `Typed("reiniciar")` → `ConsentRejections = 0` y vuelve a `CheckDataConsent("consent")`.
  - `EnteringTheBlockFromAnotherStartsTheCountAgain` — un estado con `ConsentRejections = 2` en otro bloque que sale hacia `consent` → al entrar `ConsentRejections = 0`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngineDataConsentTests"
  ```
  Esperado: no compila (`CS0117 'HandoffReasons' does not contain a definition for 'DataConsentRejected'`).
- [ ] **Step 2: GREEN.**
  - `Walk` guarda `int consentRejections` (de `state.ConsentRejections`) y lo pasa en `Result()`; `Go` lo pone en 0 junto a `attempts = 0` (`:385-386`); `Leave` y `Restart` también.
  - Un helper privado `StepResult HandOff(FlowNode node, string reason, Guid? memberId)` (efecto `Handoff`, `consentPrompt = null`, `Stop(node, HandedOff, TraceOutcomes.HandedOff)`).
  - `Consent(node, output)`: con `Rejected` y `OnReject != RejectedOutput`, después de agregar `RecordDataConsent` (con el `shownText` de la pregunta recordada, como hoy): `Handoff` → `HandOff(node, DataConsentRejected, node.OnRejectMemberId)`; `Repeat` → `consentRejections++`; si `consentRejections < node.MaxAsks` → `Ask(node)`, si no → `HandOff(…)`. Reenviar por `Ask` y no por `Resend`: el cliente acaba de no aceptar, así que no hay aceptación vigente que consultar, y `Ask` ya sube `StepNumber` (P2-R5) sin tocar los intentos.
  - `OnDataConsent`: `!CanRecord` con `OnReject != RejectedOutput` → `HandOff(node, DataConsentUnavailable, node.OnRejectMemberId)`.
  - `Reject`: en la rama de `DataConsentNode` (`:273-277`), con `OnReject != RejectedOutput` → `DataConsentUnanswered` y `HandOff(node, HandoffReasons.AttemptsExhausted, node.OnRejectMemberId)`; sin leer `settings.ExhaustedAction`.
- [ ] **Step 3:** `Modules.Automations.UnitTests` completo verde. **Commit** `feat(automations): el motor repite la pregunta de la política de datos hasta el tope y pasa a la bandeja (spec 2026-10-26 repetir R-3, R-4, R-4b)`.

### Task 4: La corrida y la simulación guardan el contador (R-3)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs:88-100, 149-156` (propiedad `ConsentRejections`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs:90-110`
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs:205-208`
- Create: migración `AddRunConsentRejections` en `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ConversationRunTests.cs`, `SimulationRunnerTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces: `int ConversationRun.ConsentRejections { get; private set; }` → columna `automations.conversation_runs.consent_rejections smallint NOT NULL DEFAULT 0`, mapeada como `attempts` (`HasColumnType("smallint").HasDefaultValue(0)`). `Simulation.StateJson` gana la clave `consentRejections` (sin migración: es el jsonb `state`).

- [ ] **Step 1: RED.**
  - `ConversationRunTests.TheConsentRejectionsSurviveApplyAndToState` — `Apply(state with { ConsentRejections = 2 })` → `ToState().ConsentRejections == 2`.
  - `ConversationRunTests.ANewRunStartsWithoutRejections` (Review Focus 4) — `ConversationRun.Start(…, RunState.Initial(…), …).ToState().ConsentRejections == 0`.
  - `SimulationRunnerTests.ASimulationKeepsTheConsentRejections` — `Simulation.Start(…, state con 2, …).ToState().ConsentRejections == 2`; un `StateJson` viejo sin la clave → `0` (como `AnOldStateJsonWithoutTheKeyHasNoConsentPrompt`).
  - `AutomationsPersistenceTests.ARunSavedBeforeTheCounterReadsZero` (Review Focus 2) — `INSERT` de una corrida por SQL sin la columna → se lee con `ConsentRejections = 0`; guardar con 2 y releer en otro contexto → 2.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ConversationRunTests|FullyQualifiedName~SimulationRunnerTests"
  ```
  Esperado: FAIL (`Assert.Equal() Failure: Expected: 2 Actual: 0`).
- [ ] **Step 2: GREEN.** `StateRow` suma `int ConsentRejections = 0` al final (un state guardado antes se lee sin ella). Migración (`Api.exe` detenido):
  ```powershell
  dotnet ef migrations add AddRunConsentRejections --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations
  ```
  El `Up` sólo agrega la columna con default 0.
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsPersistenceTests|FullyQualifiedName~AutomationsSchemaTests"
  ```
  **Commit** `feat(automations): la corrida recuerda cuántas veces no aceptó la política (spec 2026-10-26 repetir R-3)`.

### Task 5: Simulador — repeticiones y traspaso visibles (R-6)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Simulations.cs:88-96`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/SimulationRunnerTests.cs`

**Interfaces:**
- Consumes: Tasks 3 y 4. Sin dependencias nuevas en `SimulationRunner`.

- [ ] **Step 1: RED** (textos exactos del Contrato):
  - `RepeatingShowsEachRejectionAndTheQuestionAgain` — `repeat`, `maxAsks: 3`, `replyId: "rejected"` → `Registraría que el cliente no aceptó la política de datos.` y una línea `bot` con los dos botones; `State.Status = Waiting`.
  - `ReachingTheCapSaysWhereItWouldGo` — tres `rejected` sin miembro → la última línea `system` es `Pasaría la conversación a la bandeja, sin asignar: no aceptó 3 veces.`; con miembro → `Pasaría la conversación al miembro elegido: no aceptó 3 veces.`; `Ended = true`.
  - `HandingOffOnTheFirstRejectionSaysItDidNotAccept` — `handoff` → `Pasaría la conversación a la bandeja, sin asignar: no aceptó la política de datos.`; igual con `repeat` y `maxAsks: 1`.
  - `ExhaustingWithoutRejectedSaysItRecordsNothingAndHandsOff` — `repeat`, tres «hola» → `No registraría nada: se agotaron los intentos.` y después `Pasaría la conversación a la bandeja, sin asignar`; en `rejectedOutput` la línea de hoy (`ExhaustingTheConsentSaysItRecordsNothing` no cambia).
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~SimulationRunnerTests"
  ```
  Esperado: FAIL (`Assert.Equal() Failure` contra `Pasaría la conversación a la bandeja, sin asignar`).
- [ ] **Step 2: GREEN.** `case Effect.Handoff`: la frase de destino de hoy; con `Reason == HandoffReasons.DataConsentRejected` se le suma `: no aceptó {n} veces.` (`n = state.ConsentRejections`, `n ≥ 2`) o `: no aceptó la política de datos.` (`n ≤ 1`). `case Effect.DataConsentUnanswered`: el bloque (`flow.Nodes.OfType<DataConsentNode>()`, el primero con ese id) en `RejectedOutput` → la línea de hoy; si no → `No registraría nada: se agotaron los intentos.`.
- [ ] **Step 3: Commit** `feat(automations): el simulador muestra las repeticiones de la política de datos y el traspaso (spec 2026-10-26 repetir R-6)`.

### Task 6: La razón nueva llega al hilo (R-4c)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/HandoffCause.cs:5-21`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs:26-42`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:133-140`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:126` (la lista de `kind` del comentario)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs`, `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/HandoffCauseKindParityTests.cs` (sin cambios: tiene que seguir verde)

**Interfaces:**
- Consumes: Task 3.
- Produces: `HandoffCauseKind.DataConsentRejected`, `HandoffCauseKind.DataConsentUnavailable` y los mismos nombres en `AutomationHandoffKind`, **al final** de cada enum (se guardan por nombre; el orden no se persiste, pero así un diff no reordena).

- [ ] **Step 1: RED.**
  - `RunProcessorTests.ARejectionAtTheCapHandsOffWithTheConsentCause` — doble `RecordingDataConsents(NotAccepted)`, `dataConsent` `repeat` con `maxAsks: 1` y miembro, corrida `Waiting` con `ConsentPrompt`, `Tapped("rejected")` → `Records` con `Rejected` **antes** del traspaso; `Conversations.Handoffs == [(new HandoffCause(HandoffCauseKind.DataConsentRejected, null, "consent", "Política de datos", null, null, null), member)]`; corrida `HandedOff`; `Audit` con `automations.run.handed_off`.
  - `RunProcessorTests.AnUnrecordableCustomerHandsOffWithTheUnavailableCause` — `Unavailable` con `handoff` → causa `DataConsentUnavailable` con el miembro del bloque; `Audit` con `automations.run.data_consent_unavailable` y con `automations.run.handed_off`.
  - `RunProcessorTests.ExhaustedConsentHandsOffAsAttemptsExhausted` — `repeat`, tres inválidas, chatbot con `ExhaustedAction = Restart` → causa `AttemptsExhausted` con el miembro del bloque; `Audit` con `automations.run.data_consent_unanswered`.
  - `RunProcessorTests.ARecordThatFailsDoesNotHandOff` — `records: false` y `handoff` → `FailAsync` con `automations.run.data_consent_unrecorded` (como `AReplyThatCannotBeRecordedFailsTheRunBeforeContinuing`, `:811`); ningún traspaso con `DataConsentRejected`.
  - `ConversationEventJsonTests.TheConsentKindsRoundTrip` — `[Theory]` con `DataConsentRejected` y `DataConsentUnavailable` y un `AssignedMemberId` → se escribe y se lee el mismo `AutomationHandoff`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests"
  dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationEventJsonTests"
  ```
  Esperado: no compila (`CS0117 'HandoffCauseKind' does not contain a definition for 'DataConsentRejected'`).
- [ ] **Step 2: GREEN.** Los dos enums con su `<summary>` citando `spec 2026-10-26 repetir R-4c` (y R-4b para `DataConsentUnavailable`). `RunEffects`: el `switch` de `handoff.Reason` suma `DataConsentRejected => HandoffCauseKind.DataConsentRejected` y `DataConsentUnavailable => HandoffCauseKind.DataConsentUnavailable`; el resto del `case Effect.Handoff` no cambia (el adaptador ya asigna o deja sin asignar, D-5).
- [ ] **Step 3:**
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HandoffCauseKindParityTests"
  dotnet test tests/ArchitectureTests
  ```
  **Commit** `feat(automations): razón de traspaso «no aceptó la política de datos» en el hilo (spec 2026-10-26 repetir R-4b, R-4c)`.

### Task 7: De punta a punta, suite completa y CLAUDE.md

**Files:**
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.DataConsent.cs` (un `RepeatFlowJson`: `n1` dataConsent `onReject: "repeat"`, `maxAsks: 2`, `onRejectMemberId: null` → `accepted: n2` (Mensaje «Gracias.» → resolve))
- Modify: `CLAUDE.md` («Gotchas verificados»)

- [ ] **Step 1: RED → GREEN** (las piezas ya existen; si algo falla es un hueco de su tarea y se arregla allí):
  - `RejectingTwiceRecordsTwoRowsAndHandsOff` — `InboundAsync(f, "w1", "Hola")`, drenar; `TapAsync(f, "w2", "rejected", "No acepto")`, drenar → dos envíos con botones; `TapAsync(f, "w3", "rejected", "No acepto")` citando la **segunda** pregunta, drenar → dos filas `Rejected` en `customers.data_consents` (`reply_wamid` `w2` y `w3`, `prompt_message_id` = la pregunta que cada toque contestó), la conversación `Paused`/`Handoff` sin asesor, y el `AutomationPaused` con `handoff->>'kind' = 'DataConsentRejected'` y `nodeTitle` del bloque.
  - `ATapOnTheFirstPromptAfterARepeatRecordsNothing` (Review Focus 1) — tras el primer «No acepto», un toque de `rejected` que cita la **primera** pregunta → una sola fila `Rejected` (la del primer toque); la pregunta se reenvía (intento inválido); la conversación sigue con el asistente.
- [ ] **Step 2: Suite completa, una vez, en primer plano** (`Api.exe` detenido):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test
  ```
  Comparar las fallas por nombre contra la base de `develop`; sólo cuentan las nuevas.
- [ ] **Step 3: CLAUDE.md**, en «Gotchas verificados»: «**«Política de datos» tiene tres modos para «No acepto»** (spec 2026-10-26 repetir): `onReject` = `repeat` | `rejectedOutput` (default de todo bloque viejo) | `handoff`. `rejected` existe sólo con `rejectedOutput`: `OutputsOf` la omite y `FlowNormalizer` la quita. En `repeat`, el contador es `RunState.ConsentRejections` (columna `conversation_runs.consent_rejections`), no `Attempts` ni `ConsentPrompt`: vuelve a 0 en `Go`, `Leave` y `Restart`, nunca en `Resend`. En los dos modos sin `rejected`, todo camino que hoy saldría por ella (intentos agotados, cliente sin registrar) traspasa con `onRejectMemberId`, sin leer la regla de agotamiento del chatbot.»
- [ ] **Step 4: Commit** `test(automations): repetir la política de datos de punta a punta (spec 2026-10-26 repetir)` y `docs: gotcha de los modos de «No acepto»`.

---

## Orden y dependencias

- Task 1 → 2 → 3 → 4 → 5. Task 6 necesita la 3. Task 7 al final.
- El frontend depende sólo del **Contrato**; se despliega después del backend.

## Decisiones del builder (el spec las deja abiertas)

- **Nombres del JSON** (R-1): `onReject` / `onRejectMemberId` siguen el par `fallback` / `fallbackMemberId` de Botones, pero no reusan `fallback`: en Botones significa «si responde otra cosa», y en este bloque eso sigue siendo la regla de intentos (R-4). El valor del modo por defecto es `rejectedOutput`, como `otherOutput`. El tope es `maxAsks`.
- **`rejected` stale** (R-2): el backend la quita en `FlowNormalizer` (al guardar e importar) y no al leer, igual que `other` (`FlowNormalizer.cs:4-6`); con `OutputsOf` sin ella, ni el motor ni el validador la ven aunque una versión publicada la traiga. El frontend sí la descarta al leer (`readChoiceOutputs`).
- **Repetir por `Ask`, no por `Resend`** (R-3): `Resend` volvería a pedir `CheckDataConsent` (un viaje al adaptador sin sentido: acaba de no aceptar) y lo trataría como un reintento inválido. `Ask` reenvía, sube `StepNumber` (P2-R5) y no toca `Attempts`, que es lo que pide R-4 («ni al revés»): un «No acepto» no suma ni **reinicia** los intentos inválidos. Lo segundo es interpretación del agente: R-4 sólo dice que no se cuentan uno para el otro.
- **R-4b usa una razón nueva, `DataConsentUnavailable`**: ninguna existente es honesta. `Block` diría que el flujo llegó a un bloque de traspaso; `FlowFailed` exige `code`, cierra la corrida `Failed` y nunca asigna (`RunEffects.cs:210-211`), lo que contradice «pasa a la bandeja con el destino del bloque»; `DataConsentRejected` diría que el cliente no aceptó, y no se le preguntó.
- **Texto del simulador** (R-6): «Pasaría la conversación a la bandeja: no aceptó {n} veces.» del spec se compone con la frase de destino de hoy (sin asignar / al miembro elegido, spec destino del traspaso D-4), y con `n ≤ 1` dice «no aceptó la política de datos» para no escribir «1 veces».
