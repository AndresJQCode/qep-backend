# Chatbots: bloque «Política de datos» y registro de consentimiento — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un chatbot pida la autorización de tratamiento de datos con un bloque `dataConsent` de botones fijos («Acepto» / «No acepto»), que la respuesta quede como prueba en una tabla de sólo inserción de Customers (`customers.data_consents`), y que un asesor pueda ver el historial y registrar una revocación por HTTP.

**Architecture:** El flujo gana el kind `dataConsent` en todos los `switch` por kind (Domain, validador, plantillas, motor, simulador). El motor sigue puro: al entrar pide `Effect.CheckDataConsent` y vuelve con `EngineInput.DataConsent`; al responder emite `Effect.RecordDataConsent` con el texto ya interpolado que recordó en `RunState.ConsentPrompt` (el id del mensaje con los botones lo completa `RunEffects` al enviar). Automations habla con un puerto nuevo (`IAutomationDataConsents`) cuyo adaptador en Bootstrapper resuelve —o crea y cuelga— el cliente de la conversación, el canal, la identidad y el `wamid`, y escribe por un servicio de Customers (`ICustomerDataConsents`) que bloquea al cliente, es idempotente por `reply_message_id` y audita al sistema en la misma transacción. Customers expone `GET …/data-consent` y `POST …/data-consent/revocations` con el permiso nuevo `customers.data_consent.revoke`; la fusión mueve las filas al que queda.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-26-chatbot-politica-de-datos-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-chatbot-politica-de-datos-design.md) (D-1..D-12, aprobado por el owner el 2026-10-26). Antecedentes: [`2026-10-21-constructor-de-chatbots-design.md`](../specs/2026-10-21-constructor-de-chatbots-design.md) (D-I1, D-I5, D-C1, D-C7, D-M2, D-M7, D-S1..D-S3) y el plan [`2026-10-25-chatbot-destino-del-traspaso-backend.md`](2026-10-25-chatbot-destino-del-traspaso-backend.md). El plan de frontend se escribe después y consume el **Contrato** de abajo tal cual.

## Contrato (lo consume el plan de frontend)

**Bloque del flujo** (jsonb del borrador y de cada versión, `draft.flow`, `PUT …/draft`, export):

```json
{ "id": "n7", "kind": "dataConsent", "title": "Política de datos", "text": "Hola {nombre}, lee https://empresa.co/datos y dinos si aceptas.",
  "outputs": { "accepted": "n8", "rejected": "n9" } }
```

- Sin `buttons`, `saveTo` ni `fallback`: los botones son fijos y el backend no los lee del JSON. Salen como `[{ "id": "accepted", "title": "Acepto" }, { "id": "rejected", "title": "No acepto" }]` (simulador: `transcript[].buttons`).
- `text` se interpola como el de Botones y admite `{param.x}` en plantillas.

**`issues` del editor, `/validate` y 422 de publicar** (`errors.flow`, «código: mensaje»):

| Código | Severidad | Cuándo |
|---|---|---|
| `text.empty` | Error | `text` en blanco |
| `text.too_long` | Error | `text` > 1024 |
| `data_consent.no_link` | **Warning** (nunca Error) | `text` sin `http://` ni `https://`; mensaje `«El texto de «{título}» no tiene un enlace a tu política de datos.»` |
| `node.output_unconnected` | Error | `accepted` o `rejected` sin conectar; la etiqueta del mensaje es «Acepto» / «No acepto» |

**Simulador** (`transcript[]` con `from: "system"`): `«Registraría que el cliente aceptó la política de datos.»`, `«Registraría que el cliente no aceptó la política de datos.»` y, al agotar los intentos, `«Seguiría por «No acepto» sin registrar nada: se agotaron los intentos.»`. Nunca escribe filas.

**HTTP** (Customers; enums por nombre, JSON camelCase):

- `GET /api/v1/tenants/{tenantId}/customers/{customerId}/data-consent` — permiso `customers.customer.read`. **200**:
  ```json
  {
    "current": { "event": "Accepted", "occurredAt": "2026-10-26T15:04:05Z", "channel": "WhatsApp" },
    "canRevoke": true,
    "history": [
      { "id": "…", "event": "Revoked", "occurredAt": "…", "channel": null, "medium": "Other", "note": "Lo pidió por correo.",
        "actor": { "kind": "Member", "id": "<userId>", "name": "Ana Ruiz" } },
      { "id": "…", "event": "Accepted", "occurredAt": "…", "channel": "WhatsApp", "medium": null, "note": null,
        "actor": { "kind": "Chatbot", "id": "<chatbotId>", "name": "Bienvenida" } }
    ]
  }
  ```
  `current` = la última fila (`null` = «Sin respuesta»); `current.event` ∈ `Accepted | Rejected | Revoked`; `channel` ∈ `WhatsApp | Instagram | null`. `history` del más reciente al más viejo, completo. `canRevoke` = `current.event == "Accepted"` **y** el llamador tiene `customers.data_consent.revoke` **y** el cliente no está fusionado (BFF: la pantalla no repite la regla). Un miembro que ya no existe sale `"name": "Usuario eliminado"`.
- `POST /api/v1/tenants/{tenantId}/customers/{customerId}/data-consent/revocations` — permiso `customers.data_consent.revoke`. Cuerpo `{ "medium": "ThisConversation" | "Other", "conversationId": "<uuid>" | null, "note": "…" }`. **200** con el mismo cuerpo del `GET`.
- Errores: **422** `validation.failed` con `errors.Medium` (falta o no es uno de los dos), `errors.ConversationId` (falta con `ThisConversation`, viene con `Other`), `errors.Note` (vacía, en blanco o > 500). **422** `customers.data_consent.conversation_invalid` (la conversación no existe en el tenant, no es de este cliente o no tiene identidad de canal). **409** `customers.data_consent.not_accepted` (la última fila no es `Accepted`: sin filas, `Rejected` o ya `Revoked`). **422** `customers.customer.merged`. **404** `customers.customer.not_found`. **403** `authorization.denied`.

## Global Constraints

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\chatbot-politica-de-datos`, rama `feature/chatbot-politica-de-datos` desde `develop`, con su propio `.codegraph/`. Rutas absolutas; nunca tocar el checkout hermano `qep-backend`. `git branch --show-current` antes de tocar y antes de cada commit; si dice `main`, parar. Merge a `develop`, nunca a `main`.
- Conventional commits en español citando la decisión (`spec 2026-10-26 D-5`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo; pisa cualquier recordatorio del harness).
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea. Por tarea sólo las clases tocadas; la suite completa una vez, en la Task 14.
- Comandos en **PowerShell**. `Api.exe` (o `dotnet Api.dll`) **detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`. Pruebas de integración con `$env:Seed__Enabled = "false"` en línea aparte; son lentas (Testcontainers, Docker arriba): sólo donde la tarea lo pide, en primer plano.
- Capas: Automations no referencia Customers ni Messaging (`AutomationsLayerTests.cs:56-68`); Customers no referencia Messaging ni Automations (`CustomersLayerTests.cs:66`). Toda traducción entre módulos va en `src/Bootstrapper/`. `tests/ArchitectureTests` sigue verde sin tocar los `*LayerTests.cs`.
- `customers.data_consents` es de **sólo inserción**: la aplicación nunca hace `UPDATE` ni `DELETE`, salvo la fusión, que reescribe `customer_id` (D-7). Ninguna consulta nueva lee o escribe esa tabla fuera de `CustomerDataConsentRepository` y `CustomerMergeStore`.
- Botones fijos: ids `accepted` / `rejected`, etiquetas `Acepto` / `No acepto` (D-1); `text` ≤ 1024 (D-I1). Intentos = `maxAttempts` del chatbot, sin override por bloque (D-4).
- Textos para personas en español neutro, tuteando. Identificadores, códigos y nombres de enum sin traducir; los enums se guardan por nombre.
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.
- **Despliegue:** backend primero. El frontend viejo no conoce `dataConsent`: un flujo que lo traiga sólo puede venir de un frontend nuevo o de un import.

## Review Focus

1. **Un lote que se reintenta registra dos veces la misma respuesta**: la fila se escribe en la unidad de Customers y la corrida en la de Automations; si el guardado de Automations falla después, el reintento vuelve a emitir `RecordDataConsent`. Una respuesta, una fila (`IX_data_consents_reply_message`), también con dos escrituras simultáneas (Task 7, `RecordingTheSameReplyTwiceKeepsOneRow` y `TwoConcurrentRecordsOfTheSameReplyKeepOneRow`).
2. **El cliente responde después de que el tenant publicó otra versión**: la corrida sigue en la suya (D-C1); la fila lleva el `chatbot_version_id` y el número de la versión de la corrida, no los de la última (Task 14, `AReplyAfterARepublishIsRecordedWithTheRunVersion`).
3. **Fusión con filas en los dos clientes**: el estado vigente del que queda es la última fila del orden combinado por `occurred_at`, no la última del que queda antes de fusionar ni la del absorbido por ser la «movida» (Task 10, `MergingMovesTheConsentsAndTheLatestRowWins`, en los dos sentidos).
4. **Revocar sin aceptación vigente**: sin filas, con la última `Rejected` o con una revocación ya registrada (doble clic) responde 409 y no agrega fila (Task 9, `RevokingWithoutACurrentAcceptanceIs409`).
5. **La conversación cambia de cliente entre la pregunta y la respuesta** (`Conversation.ChangeCustomer`): la fila va al cliente que tiene la conversación al responder, y las filas viejas del cliente anterior no se mueven (Task 13, `TheReplyIsRecordedForTheCustomerTheConversationHasWhenReplying`).

Además, con su prueba en la tarea dueña: la palabra de reinicio durante la espera no registra nada (Task 3, `TheRestartKeywordWhileWaitingRecordsNothing`) y el simulador nunca escribe filas (Task 5, `SimulatingTheConsentBlockNeverWritesRows`).

## Mapa de archivos

| Archivo | Qué |
|---|---|
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNodeKinds.cs` (`:8-22` kinds y `All`, `:29-41` `OutputsOf`, `:69-85` `FlowOutputs`) | Modify: kind `dataConsent`, salidas `accepted`/`rejected` |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs` (`:107-113` records terminales, `:139-155` `InteractiveFallbacks`) | Modify: `DataConsentNode`, `DataConsentButtons` |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowJson.cs` (`Write` `:139-295`, `ReadNode` `:328-356`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs` (`OutputLabels` `:46-62`, `CheckNode` `:165-258`, `InterpolatedTexts` `:638-656`, regex `:706-719`) y `FlowIssue.cs` (`:25-82`) | Modify |
| `src/Modules/Automations/Modules.Automations.Application/Templates.cs` (`:89-128`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs` (`Step` `:26-33`, `OnCustomerMessage` `:41-71`, `Waits` `:89`, `Reject` `:195-239`, `Restart` `:246-253`, `Resend`/`Leave` `:257-265`, `Enter` `:318-360`, `Result` `:392-393`), `CustomerReply.cs` (`Classify` `:28-42`, `Choose` `:214-227`, `Typed` `:263-273`), `Effect.cs` (`:9-34`), `EngineInput.cs` (`:8-28`), `RunState.cs` (`:51-62`) | Modify |
| `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs` (`Apply` `:84-95`, `ToState` `:144-150`), `Simulation.cs` (`:90-106`) | Modify: persistir `ConsentPrompt` |
| `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs` (`ConfigureRun` `:191-222`), migración `AddRunConsentPrompt` | Modify / Create |
| `src/Modules/Automations/Modules.Automations.Application/Simulations.cs` (`SimulationRunner.RunAsync` `:52-90`) | Modify |
| `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IChatbotRepository` `:9-43`, `AutomationsAuditActions` `:386-412`), `Engine/RunEffects.cs` (ctor `:22-29`, bucle `:62-141`, `PendingInputAsync` `:151-165`, `ExternalAsync` `:277-284`), `Engine/AutomationsRunFailures.cs` (`:4-11`), `Infrastructure/Persistence/AutomationsRepositories.cs` (`:37-41`) | Modify |
| `src/Modules/Customers/Modules.Customers.Domain/CustomerDataConsent.cs` | Create: entidad, enums, valores |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`:45-55`; precedente `ConfigureCustomerNote` `:307-335`), `CustomersUnitOfWork.cs` (`:10-80`), `CustomerDataConsentRepository.cs` (Create), migración `AddCustomerDataConsents`, `CustomersInfrastructureExtensions.cs` (`:57`) | Modify / Create |
| `src/Modules/Customers/Modules.Customers.Application/CustomerDataConsents.cs` (Create: puertos, servicio, query, comando, validador, DTOs), `CustomersPermissions.cs` (`:9-34`), `CustomerAuditActions.cs` (`:5-24`), `ICustomersAuditPublisher.cs` (`:7-31`) | Modify / Create |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersAuditPublisher.cs` (`:24-30`), `CustomerMergeStore.cs` (`MoveOwnedCoreAsync` `:82-98`) | Modify |
| `src/Modules/Customers/Modules.Customers.Api/CustomerEngagementEndpoints.cs` (`:95-100`, `:182-189`) | Modify |
| `src/Modules/Messaging/Modules.Messaging.Application/IAutomationInbox.cs` (`:12`), `IConversationRepository.cs` (`:41-50`); `Infrastructure/Persistence/AutomationInbox.cs` (`:75-89`, `:119-129`), `ConversationRepository.cs` (`:13-28`) | Modify |
| `src/Bootstrapper/AutomationDataConsents.cs`, `src/Bootstrapper/CustomersConversations.cs` | Create |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers `:323-335`, puertos del motor `:964-982`, `:994`, rol admin `:1193`, rol advisor `:1295`, `PermissionDefinition` `:1508-1514`, política `:2031-2034`) | Modify |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (`:179-188`, `:243-256`) | Modify |

---

### Task 1: Flujo — el kind `dataConsent` se lee, se escribe y se sustituye (D-1, D-12)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowNodeKinds.cs:8-22, 29-41, 69-85`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs` (record nuevo junto a `HandoffNode`, `:107-113`; clase de constantes junto a `InteractiveFallbacks`, `:139-155`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowJson.cs:139-295` (`Write`), `:328-356` (`ReadNode`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Templates.cs:89-128`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowJsonTests.cs`, `FlowNormalizerTests.cs`, `TemplateParametersTests.cs`

**Interfaces:**
- Produces:
  - `FlowNodeKinds.DataConsent = "dataConsent"` (en `All`); `FlowOutputs.Accepted = "accepted"`, `FlowOutputs.Rejected = "rejected"`; `OutputsOf(DataConsentNode) == [Accepted, Rejected]`.
  - `sealed record DataConsentNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text) : FlowNode(Id, FlowNodeKinds.DataConsent, Title, Outputs)`.
  - `static class DataConsentButtons { const string AcceptLabel = "Acepto"; const string RejectLabel = "No acepto"; static readonly IReadOnlyList<FlowButton> All /* (accepted, Acepto), (rejected, No acepto) */; }`.

- [ ] **Step 1: RED.**
  - `FlowJsonTests.ADataConsentNodeRoundTrips` — el JSON del Contrato → `DataConsentNode` con `Text` y `Outputs` iguales; `Parse(Serialize(x)) == x`; `Serialize` no escribe `buttons`, `saveTo` ni `fallback`.
  - `FlowJsonTests.ADataConsentWithoutTextReadsEmpty` — sin `text` → `Text == ""` (D-C3: lo marca el validador, no la lectura).
  - `FlowJsonTests.OutputsOfADataConsentAreAcceptedAndRejected` — `FlowNodeKinds.OutputsOf(node) == ["accepted", "rejected"]`.
  - `FlowNormalizerTests.ADataConsentIsUntouched` — `FlowNormalizer.Normalize` deja el bloque igual (también con una clave `other` de más: no es Botones).
  - `TemplateParametersTests.ADataConsentTextAndTitleTakeTheParameters` — `{param.politica}` en `title` y `text` se sustituye; los ids de salida no cambian.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowJsonTests|FullyQualifiedName~FlowNormalizerTests|FullyQualifiedName~TemplateParametersTests"
  ```
  Esperado: no compila (`CS0246 'DataConsentNode'`).
- [ ] **Step 2: GREEN.** `ReadNode`: `FlowNodeKinds.DataConsent => new DataConsentNode(id, title, outputs, JsonFields.Text(element, "text", where))`. `Write`: `case DataConsentNode consent: writer.WriteString("text", consent.Text); break;`. `Templates.cs`: `DataConsentNode consent => consent with { Title = Text(consent.Title), Text = Text(consent.Text) }` (las etiquetas son fijas: no se sustituyen). `FlowNormalizer` no cambia (cae en `_ => node`).
- [ ] **Step 3:** el mismo comando, verde. **Commit** `feat(automations): bloque dataConsent en el flujo (spec 2026-10-26 D-1, D-12)`.

### Task 2: Validador — texto, aviso sin enlace y salidas (D-3)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowIssue.cs:25-82` (`DataConsentNoLink = "data_consent.no_link"`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs:46-62` (`OutputLabels` + `accepted` → «Acepto», `rejected` → «No acepto»), `:165-258` (`CheckNode`), `:638-656` (`InterpolatedTexts`), `:706-719` (regex nueva)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `FlowIssueCodes.DataConsentNoLink`; `[GeneratedRegex(@"https?://\S", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex WebLink();`.

- [ ] **Step 1: RED** (`Consent(string text, string? accepted = "end", string? rejected = "end")` arma el bloque):
  - `AnEmptyConsentTextIsAnError` — `"  "` → `text.empty` (Error, `NodeId` del bloque).
  - `AConsentTextOver1024IsAnError` — 1025 caracteres → `text.too_long` (Error, mensaje con «el máximo es 1024»).
  - `AConsentTextWithoutALinkIsOnlyAWarning` — `"Acepta nuestra política"` y `"Lee empresa.co/datos"` → `data_consent.no_link` con `Severity = Warning` y mensaje `"El texto de «Política de datos» no tiene un enlace a tu política de datos."`; ningún issue `Error` en un flujo por lo demás válido.
  - `AConsentTextWithALinkHasNoWarning` — `"Lee https://empresa.co/datos"` y `"HTTP://empresa.co"` → sin `data_consent.no_link`.
  - `UnconnectedConsentOutputsAreErrorsNamedLikeTheButtons` — `accepted: null` → `node.output_unconnected` (Error) con «La salida «Acepto» de «Política de datos» no está conectada.»; igual para `rejected` con «No acepto».
  - `AVariableInTheConsentTextCounts` — `"{cedula} …"` sin bloque que la guarde → `variable.never_set` (Warning).
  - `AConsentReachesBothOutputs` — los bloques detrás de `accepted` y de `rejected` no son `node.unreachable`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowValidatorTests"
  ```
  Esperado: FAIL (`Assert.Single() Failure: The collection was empty` para `data_consent.no_link`).
- [ ] **Step 2: GREEN.** `case DataConsentNode consent:` `CheckText(node, consent.Text, InteractiveBodyMaxLength, issues)`; si el texto no está en blanco y `!WebLink().IsMatch(consent.Text)` → `Warning(DataConsentNoLink, …)`. `InterpolatedTexts`: `DataConsentNode consent => [consent.Text]`. `WrittenInOrder` y `DirectReferences` no cambian (D-12): el bloque no guarda ni nombra variables. Las salidas no se tocan: `CheckOutputs` ya las exige por `OutputsOf` (no van en `OptionalOutputs`).
- [ ] **Step 3: Commit** `feat(automations): el validador revisa el bloque Política de datos y avisa si no trae enlace (spec 2026-10-26 D-3)`.

### Task 3: Motor — preguntar, recordar el texto y registrar la respuesta (D-2, D-4, D-6)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/RunState.cs:51-62`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:9-34`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/EngineInput.cs:8-28`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs` (rangos del mapa)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/CustomerReply.cs:28-42`
- Create: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineDataConsentTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `sealed record ConsentPrompt(string NodeId, string ShownText, Guid? PromptMessageId);` y `RunState(…, IReadOnlyList<TraceEntry> Trace, ConsentPrompt? ConsentPrompt = null)`.
  - `enum DataConsentDecision { Accepted, Rejected }` (en `Effect.cs`).
  - `Effect.CheckDataConsent(string NodeId)`, `Effect.RecordDataConsent(string NodeId, DataConsentDecision Decision, string ShownText, Guid? PromptMessageId)`, `Effect.DataConsentUnanswered(string NodeId)`.
  - `EngineInput.DataConsent(string NodeId, bool Accepted, bool CanRecord = true)`.

- [ ] **Step 1: RED** en `FlowEngineDataConsentTests` (flujo `consent → accepted: "yes" (Mensaje), rejected: "no" (Mensaje)`, texto `"Hola {nombre}, lee https://e.co/d"`, `nombre = "Laura"`, `Settings = new EngineSettings("reiniciar", 3)`):
  - `EnteringChecksBeforeAsking` — `Start` → `Effects == [new Effect.CheckDataConsent("consent")]`, `Status = Running`, trace `pending`.
  - `ACurrentAcceptanceLeavesByAcceptedWithoutSending` — `DataConsent("consent", Accepted: true)` → primer efecto es el `SendText` de `yes`; ningún `SendInteractive` ni `RecordDataConsent`.
  - `WithoutAcceptanceItAsksWithTheFixedButtons` — `Accepted: false` → `SendInteractive("consent", Buttons("Hola Laura, lee https://e.co/d", [("accepted","Acepto"),("rejected","No acepto")]))` + `WaitForCustomer`; `Status = Waiting`; `State.ConsentPrompt == new ConsentPrompt("consent", "Hola Laura, lee https://e.co/d", null)`.
  - `WhenTheCustomerCannotBeRecordedItLeavesByRejectedWithoutAsking` — `Accepted: false, CanRecord: false` → sale por `no`; sin `SendInteractive` ni `RecordDataConsent` (D-7).
  - `TappingAcceptRecordsTheShownTextAndLeaves` — estado `Waiting` con `ConsentPrompt("consent", "Texto enviado", promptId)`, `Tapped("accepted", "Acepto")` → `Effects[0] == new Effect.RecordDataConsent("consent", DataConsentDecision.Accepted, "Texto enviado", promptId)`, luego el `SendText` de `yes`; `State.ConsentPrompt == null`.
  - `TappingRejectRecordsRejected` — `Tapped("rejected", "No acepto")` → `Decision = Rejected`, sale por `no`.
  - `TypingALabelCountsAsTapping` (D-I5) — `Typed("ACEPTO")` y `Typed("  no   acepto ")` → `Accepted` y `Rejected`.
  - `TheRecordedTextIsTheOneSentNotTheCurrentTemplate` (D-6) — la variable `nombre` cambió en el estado después de preguntar → `ShownText` sigue siendo el de `ConsentPrompt`.
  - `WithoutARememberedPromptTheTextIsInterpolatedAgain` — `ConsentPrompt` nulo o de otro bloque → `ShownText` = el texto interpolado ahora, `PromptMessageId = null`.
  - `AnInvalidReplyAsksAgain` — `Typed("hola")` → `Attempts = 1` y `Effects == [CheckDataConsent("consent")]` (volver a entrar, como `Resend`); con `DataConsent(false)` vuelve a mandar los botones y conserva `Attempts = 1`.
  - `ExhaustedAttemptsLeaveByRejectedWithoutRecording` (D-4) — tres inválidas con `Settings(AttemptsExhaustedAction.Handoff, member)` → la tercera emite `DataConsentUnanswered("consent")` y sale por `no`; ningún `RecordDataConsent` ni `Handoff`; `Status` no es `HandedOff`.
  - `TheRestartKeywordWhileWaitingRecordsNothing` — `Typed("reiniciar")` → trace `restart`, vuelve a `CheckDataConsent("consent")`, sin `RecordDataConsent`; `ConsentPrompt == null`.
  - `AStaleConsentResultChangesNothing` — `DataConsent("otro", true)` o con la corrida `Waiting` → `Effects` vacío y el mismo estado.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngineDataConsentTests"
  ```
  Esperado: no compila (`CS0426 'DataConsent'` en `EngineInput`).
- [ ] **Step 2: GREEN.**
  - `Step`: `EngineInput.DataConsent consent => OnDataConsent(flow, state, consent, now)`; `OnDataConsent` usa `PendingAt<DataConsentNode>` como `OnBusinessHours` (`:116-119`): `Accepted` → `Leave(node, accepted)`; `!CanRecord` → `Leave(node, rejected)`; si no, `walk.Ask(node)`.
  - `Walk` guarda `ConsentPrompt? consentPrompt` (inicial = `state.ConsentPrompt`) y lo pasa en `Result()`. Lo limpia `Leave`, `Restart` y el comienzo de cada `Enter`; sólo `Ask` lo pone (`new(node.Id, Text(node.Text), null)`) junto al `SendInteractive` con `DataConsentButtons.All` y `Wait(node)`.
  - `Enter`: `case DataConsentNode: effects.Add(new Effect.CheckDataConsent(node.Id)); return Stop(node, RunStatus.Running, TraceOutcomes.Pending);`. `Waits` suma `DataConsentNode`; `SaveToOf` lo deja en `null`.
  - `OnCustomerMessage`: con `ReplyOutcome.Output` y `node is DataConsentNode consent` → `walk.Consent(consent, output.Name)`, que agrega `RecordDataConsent` (texto e id del `consentPrompt` si su `NodeId` es el del bloque; si no, `Text(consent.Text)` y `null`) y después `Leave`.
  - `Reject`: el bloque cae en `(Repeat, null)` y usa `settings.MaxAttempts`; después de `if (attempts < maxAttempts) return Resend(node);` y antes de la rama de `invalid` y de `ExhaustedAction`: `if (node is DataConsentNode) { effects.Add(new Effect.DataConsentUnanswered(node.Id)); return Leave(node, FlowOutputs.Rejected); }`. Reenviar es `Resend` → `Enter` (vuelve a preguntar si ya aceptó; suma `StepNumber`, P2-R5).
  - `CustomerReply.Classify`: `DataConsentNode => Choose(DataConsentButtons.All, message)` con la misma regla de `Choose(ButtonsNode, …)` (id si hay `ReplyId`; si no, `Typed` contra la etiqueta), `Saved = null`.
- [ ] **Step 3:** `Modules.Automations.UnitTests` completo verde (los `switch` exhaustivos que fallen se arreglan acá). **Commit** `feat(automations): el motor pide la política de datos y registra la respuesta como efecto (spec 2026-10-26 D-2, D-4, D-6)`.

### Task 4: La corrida y la simulación guardan el texto enviado (D-6)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs:84-95, 144-150` (propiedad `ConsentPromptJson`)
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs:90-106` (`StateRow` + `ConsentPrompt`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs:191-222`
- Create: migración `AddRunConsentPrompt` en `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ConversationRunTests.cs`, `SimulationRunnerTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces: `string? ConversationRun.ConsentPromptJson { get; private set; }` → columna `automations.conversation_runs.consent_prompt jsonb NULL` (`{ "nodeId", "shownText", "promptMessageId" }`); `ToState()` la devuelve en `RunState.ConsentPrompt`. `Simulation.StateJson` gana la clave `consentPrompt` (sin migración: es el jsonb `state`).

- [ ] **Step 1: RED.**
  - `ConversationRunTests.TheConsentPromptSurvivesApplyAndToState` — `Apply(state with { ConsentPrompt = new("c", "Texto", id) })` → `ToState().ConsentPrompt` igual; con `null` → `ConsentPromptJson == null`.
  - `SimulationRunnerTests.ASimulationKeepsTheConsentPrompt` — `Simulation.Start(…, state con ConsentPrompt, …).ToState().ConsentPrompt` igual; un `StateJson` viejo sin la clave → `null`.
  - `AutomationsPersistenceTests.TheConsentPromptSurvivesTheDatabase` — guardar una corrida con `ConsentPrompt`, releer en otro contexto: igual.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ConversationRunTests|FullyQualifiedName~SimulationRunnerTests"
  ```
  Esperado: FAIL (`Assert.Equal() Failure: Expected ConsentPrompt { … } Actual: null`).
- [ ] **Step 2: GREEN.** Serialización con las mismas `TraceOptions` (web). EF: `run.Property(value => value.ConsentPromptJson).HasColumnName("consent_prompt").HasColumnType("jsonb");`. Migración (`Api.exe` detenido):
  ```powershell
  dotnet ef migrations add AddRunConsentPrompt --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations
  ```
  El `Up` sólo agrega la columna nula.
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsPersistenceTests|FullyQualifiedName~AutomationsSchemaTests"
  ```
  **Commit** `feat(automations): la corrida recuerda el texto de la política que envió (spec 2026-10-26 D-6)`.

### Task 5: Simulador — pregunta siempre y nunca registra (D-11)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Simulations.cs:52-90`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/SimulationRunnerTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/SimulationsApiTests.cs`

**Interfaces:**
- Consumes: Tasks 3 y 4. Sin dependencias nuevas en `SimulationRunner`: no recibe ningún puerto de consentimiento.

- [ ] **Step 1: RED.**
  - `SimulationRunnerTests.TheConsentBlockAlwaysAsksInTheSimulator` — `Start` → una línea `bot` con `Buttons == [("accepted","Acepto"),("rejected","No acepto")]` y `State.Status = Waiting`.
  - `SimulationRunnerTests.TappingAcceptWritesTheSystemLine` — `replyId = "accepted"` → línea `system` `"Registraría que el cliente aceptó la política de datos."` y sigue por `accepted`; con `rejected` → `"Registraría que el cliente no aceptó la política de datos."`.
  - `SimulationRunnerTests.ExhaustingTheConsentSaysItRecordsNothing` — tres «hola» → `"Seguiría por «No acepto» sin registrar nada: se agotaron los intentos."` y sigue por `rejected`.
  - `SimulationsApiTests.SimulatingTheConsentBlockNeverWritesRows` (D-11) — borrador con el bloque; `POST …/simulations` y `…/messages` con `replyId: "accepted"` → transcripción con la línea de sistema y `SELECT count(*) FROM customers.data_consents` = 0.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~SimulationRunnerTests"
  ```
  Esperado: FAIL (`InvalidOperationException: Efecto del motor desconocido: CheckDataConsent.`).
- [ ] **Step 2: GREEN.** `case Effect.CheckBusinessHours or Effect.CallHttp or Effect.CheckDataConsent` comparte el tope de `EngineReentries.Max`; `CheckDataConsent` responde `new EngineInput.DataConsent(effect.NodeId, Accepted: false)`. `RecordDataConsent` y `DataConsentUnanswered` → `lines.System(...)` con los textos de arriba.
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~SimulationsApiTests"
  ```
  **Commit** `feat(automations): el simulador pregunta la política de datos sin registrar nada (spec 2026-10-26 D-11)`.

### Task 6: Customers — la tabla `data_consents` y su entidad (D-5)

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerDataConsent.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:10-24, 45-55` (DbSet y `ConfigureDataConsent`)
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerDataConsentRepository.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersUnitOfWork.cs` (rama por nombre de índice)
- Create: migración `AddCustomerDataConsents` en `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs:57` (registro)
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerDataConsents.cs` (por ahora sólo el puerto del repositorio y la excepción)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerDataConsentTests.cs` (nuevo), `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDataConsentPersistenceTests.cs` (nuevo)

**Interfaces:**
- Produces (Domain):
  - `enum DataConsentEvent { Accepted, Rejected, Revoked }`, `enum DataConsentChannel { WhatsApp, Instagram }`, `enum DataConsentMedium { ThisConversation, Other }`, `enum DataConsentActorKind { Chatbot, Member }`.
  - `sealed record DataConsentIdentity(Guid ConversationId, DataConsentChannel Channel, string ChannelUserId, string? WaId)`.
  - `sealed record ChatbotConsentEvidence(string ShownText, Guid? PromptMessageId, Guid ReplyMessageId, string? ReplyWamid, Guid ChatbotId, string ChatbotName, Guid ChatbotVersionId, int ChatbotVersionNumber, string NodeId)`.
  - `sealed class CustomerDataConsent` con `Id`, `Sequence` (long, lo da la base), `TenantId`, `CustomerId`, `Event`, `OccurredAt`, `Channel?`, `ChannelUserId?`, `WaId?`, `ConversationId?`, `ActorKind`, `ActorUserId?`, los campos de `ChatbotConsentEvidence` aplanados y nulos, `Medium?`, `Note?`; `const int NoteMaxLength = 500`.
  - `static CustomerDataConsent FromChatbot(Guid tenantId, CustomerId customerId, DataConsentEvent decision, DateTimeOffset occurredAt, DataConsentIdentity identity, ChatbotConsentEvidence evidence)` (`Revoked` → `ArgumentOutOfRangeException`).
  - `static CustomerDataConsent Revocation(Guid tenantId, CustomerId customerId, DateTimeOffset occurredAt, Guid actorUserId, DataConsentMedium medium, DataConsentIdentity? identity, string note)` (nota recortada; vacía o > 500 → `CustomersDomainException("customers.data_consent.note_invalid", …)`; `ThisConversation` sin identidad u `Other` con identidad → `ArgumentException`).
- Produces (Application): `interface ICustomerDataConsentRepository { Task<IReadOnlyList<CustomerDataConsent>> ListAsync(Guid tenantId, CustomerId customerId, CancellationToken ct); Task<CustomerDataConsent?> FindLatestAsync(Guid tenantId, CustomerId customerId, CancellationToken ct); Task<bool> ReplyRecordedAsync(Guid tenantId, Guid replyMessageId, CancellationToken ct); Task<Guid?> LockCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct); void Add(CustomerDataConsent consent); }` — `ListAsync` del más nuevo al más viejo por `(occurred_at DESC, sequence DESC)`, `FindLatestAsync` el primero de ese orden; `LockCustomerAsync` hace `SELECT … FOR UPDATE` del cliente y, si está fusionado, del que quedó (un salto, D-F2), y devuelve el id en que se escribe (`null` = no es del tenant); exige la transacción de `ICustomersUnitOfWork.BeginTransactionAsync`. `sealed class DataConsentReplyRecordedException : Exception`.
- Produces (base): `customers.data_consents` con `id uuid PK`, `sequence bigint GENERATED ALWAYS AS IDENTITY`, `tenant_id uuid`, `customer_id uuid` (FK `FK_data_consents_customers_customer_id`, `ON DELETE RESTRICT`), `event varchar(16)`, `occurred_at timestamptz`, `channel varchar(16) NULL`, `channel_user_id varchar(150) NULL`, `wa_id varchar(20) NULL`, `conversation_id uuid NULL`, `actor_kind varchar(16)`, `actor_user_id uuid NULL`, `shown_text text NULL`, `prompt_message_id uuid NULL`, `reply_message_id uuid NULL`, `reply_wamid text NULL`, `chatbot_id uuid NULL`, `chatbot_name varchar(80) NULL`, `chatbot_version_id uuid NULL`, `chatbot_version_number int NULL`, `node_id varchar(64) NULL`, `medium varchar(24) NULL`, `note varchar(500) NULL`. Índices `IX_data_consents_tenant_customer_order (tenant_id, customer_id, occurred_at DESC, sequence DESC)` y `IX_data_consents_reply_message (reply_message_id) UNIQUE WHERE reply_message_id IS NOT NULL`. CHECKs (en `HasCheckConstraint`, como `CK_customers_*`):
  - `CK_data_consents_event`: `event IN ('Accepted', 'Rejected', 'Revoked')`
  - `CK_data_consents_channel`: `channel IS NULL OR channel IN ('WhatsApp', 'Instagram')`
  - `CK_data_consents_medium`: `medium IS NULL OR medium IN ('ThisConversation', 'Other')`
  - `CK_data_consents_actor`: `(event IN ('Accepted', 'Rejected') AND actor_kind = 'Chatbot' AND actor_user_id IS NULL) OR (event = 'Revoked' AND actor_kind = 'Member' AND actor_user_id IS NOT NULL)`
  - `CK_data_consents_chatbot_fields`: `(event = 'Revoked' AND shown_text IS NULL AND prompt_message_id IS NULL AND reply_message_id IS NULL AND reply_wamid IS NULL AND chatbot_id IS NULL AND chatbot_name IS NULL AND chatbot_version_id IS NULL AND chatbot_version_number IS NULL AND node_id IS NULL) OR (event <> 'Revoked' AND shown_text IS NOT NULL AND reply_message_id IS NOT NULL AND chatbot_id IS NOT NULL AND chatbot_name IS NOT NULL AND chatbot_version_id IS NOT NULL AND chatbot_version_number IS NOT NULL AND node_id IS NOT NULL)`
  - `CK_data_consents_revocation_fields`: `(event = 'Revoked' AND medium IS NOT NULL AND note IS NOT NULL AND length(btrim(note)) BETWEEN 1 AND 500) OR (event <> 'Revoked' AND medium IS NULL AND note IS NULL)`
  - `CK_data_consents_identity`: `(event = 'Revoked' AND medium = 'Other' AND conversation_id IS NULL AND channel IS NULL AND channel_user_id IS NULL AND wa_id IS NULL) OR ((event <> 'Revoked' OR medium = 'ThisConversation') AND conversation_id IS NOT NULL AND channel IS NOT NULL AND channel_user_id IS NOT NULL)`

- [ ] **Step 1: RED.**
  - `CustomerDataConsentTests.AChatbotRowCarriesTheEvidenceAndNoNote` — `FromChatbot(…, Accepted, …)` → `ActorKind = Chatbot`, `ActorUserId = null`, `Medium = null`, `Note = null`, los campos de la evidencia iguales.
  - `CustomerDataConsentTests.AChatbotRowCannotBeARevocation` — `Revoked` → `ArgumentOutOfRangeException`.
  - `CustomerDataConsentTests.ARevocationByAnotherMediumHasNoIdentity` — `Other`, nota `"  Lo pidió por correo. "` → `Note = "Lo pidió por correo."`, `Channel`/`ChannelUserId`/`WaId`/`ConversationId` nulos, `ActorKind = Member`.
  - `CustomerDataConsentTests.ARevocationNeedsANoteUpTo500` — `""`, `"   "` y 501 caracteres → `customers.data_consent.note_invalid`; 500 → ok.
  - `CustomerDataConsentPersistenceTests.EachCheckRejectsItsRow` — `[Theory]` con un `INSERT` por SQL que viola cada CHECK (bot sin `reply_message_id`; `Revoked` con `shown_text`; `Revoked`+`Other` con `conversation_id`; `Revoked`+`ThisConversation` sin `channel_user_id`; `Accepted` con `actor_user_id`; `event = 'Maybe'`) → `PostgresException` `SqlState = 23514` con `ConstraintName` = el del caso.
  - `CustomerDataConsentPersistenceTests.AWaIdIsOptionalEvenWithAConversation` — fila de bot con `wa_id NULL` se inserta.
  - `CustomerDataConsentPersistenceTests.ARepeatedReplyIsTranslatedByIndexName` — dos `FromChatbot` con el mismo `ReplyMessageId` en dos `SaveChanges` → el segundo lanza `DataConsentReplyRecordedException` (no `DbUpdateException`).
  - `CustomerDataConsentPersistenceTests.TheLatestIsOrderedByOccurredAtThenSequence` — dos filas con el mismo `occurred_at` → `FindLatestAsync` devuelve la insertada después.
  ```powershell
  dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerDataConsentTests"
  ```
  Esperado: no compila (`CS0246 'CustomerDataConsent'`).
- [ ] **Step 2: GREEN.** Enums por nombre (`HasConversion<string>()`), `sequence` con `UseIdentityAlwaysColumn()` y `ValueGeneratedOnAdd`, como `http_calls.id` (`AutomationsDbContext.cs:229`). La traducción va en `CustomersUnitOfWork` por el nombre `IX_data_consents_reply_message` (CLAUDE.md, «discriminar por nombre de índice»), con `ChangeTracker.Clear()` como la rama de identificación. Migración (`Api.exe` detenido):
  ```powershell
  dotnet ef migrations add AddCustomerDataConsents --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations
  ```
  Revisar que el `Up` sólo cree la tabla, sus índices, la FK y los siete CHECK.
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDataConsentPersistenceTests"
  ```
  **Commit** `feat(customers): registro de consentimiento de datos de sólo inserción (spec 2026-10-26 D-5)`.

### Task 7: Customers — consultar y registrar lo que responde el bot (D-5, D-9 auditoría)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerDataConsents.cs` (puerto y servicio)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomersAuditPublisher.cs:7-31`, `CustomerAuditActions.cs:5-24`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersAuditPublisher.cs:24-30`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs:57`
- Modify (dobles): `tests/Modules/Customers/Modules.Customers.UnitTests/EngagementTestBed.cs:41-50`, `CustomerWriteDoubles.cs:34-45`, `CustomerWhatsAppDirectoryRaceTests.cs:28-36`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDataConsentsServiceTests.cs` (nuevo)

**Interfaces:**
- Consumes: Task 6.
- Produces:
  - `void ICustomersAuditPublisher.PublishBySystem(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)` (actor vacío, `actorType = "System"`).
  - `CustomerAuditActions.DataConsentAccepted = "customers.customer.data_consent_accepted"`, `DataConsentRejected = "customers.customer.data_consent_rejected"`, `DataConsentRevoked = "customers.customer.data_consent_revoked"`.
  - `sealed record ChatbotDataConsentRecord(Guid TenantId, Guid CustomerId, DataConsentEvent Decision, DateTimeOffset OccurredAt, DataConsentIdentity Identity, ChatbotConsentEvidence Evidence)`; `enum DataConsentRecordOutcome { Recorded, AlreadyRecorded, CustomerMissing }`.
  - `interface ICustomerDataConsents { Task<bool> HasCurrentAcceptanceAsync(Guid tenantId, Guid customerId, CancellationToken ct); Task<DataConsentRecordOutcome> RecordFromChatbotAsync(ChatbotDataConsentRecord record, CancellationToken ct); }`, implementado por `sealed class CustomerDataConsents` (Application, como `CustomerWhatsAppDirectory`).

- [ ] **Step 1: RED** en `CustomerDataConsentsServiceTests` (resuelve `ICustomerDataConsents` del host; cliente creado por la API):
  - `ANewCustomerHasNoCurrentAcceptance` → `false`.
  - `AnAcceptedReplyIsCurrentAndAuditedBySystem` — `RecordFromChatbotAsync(Accepted)` → `Recorded`; `HasCurrentAcceptanceAsync` → `true`; una fila con `actor_kind = 'Chatbot'`; un outbox `platform.audit.recorded.v1` con `action = customers.customer.data_consent_accepted` y `payload->>'actorType' = 'System'`.
  - `ARejectedReplyIsNotAnAcceptance` → `false` y acción `…_rejected`.
  - `RecordingTheSameReplyTwiceKeepsOneRow` (Review Focus 1) — segunda llamada con el mismo `ReplyMessageId` → `AlreadyRecorded`; `count(*) = 1`; un solo evento de auditoría.
  - `TwoConcurrentRecordsOfTheSameReplyKeepOneRow` (Review Focus 1) — dos scopes con `Task.WhenAll` → resultados `{Recorded, AlreadyRecorded}`; `count(*) = 1`.
  - `ARecordForAMergedCustomerGoesToTheSurvivor` — cliente fusionado (`POST …/merge`) → la fila queda con `customer_id` del que quedó.
  - `AnInactiveCustomerStillRecords` — cliente desactivado → `Recorded` (D-5, clientes inactivos).
  - `AnUnknownCustomerIsMissing` — id ajeno → `CustomerMissing`, sin filas.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDataConsentsServiceTests"
  ```
  Esperado: no compila (`CS0246 'ICustomerDataConsents'`).
- [ ] **Step 2: GREEN.** `RecordFromChatbotAsync` en una transacción de `ICustomersUnitOfWork.BeginTransactionAsync`: `LockCustomerAsync` (`null` → `CustomerMissing`); `ReplyRecordedAsync` → `AlreadyRecorded` sin escribir; `Add(FromChatbot(…, CustomerId del lock, …))` + `PublishBySystem(…, acción por decisión, customerId, "success", record.OccurredAt)`; `SaveChangesAsync`; `CommitAsync`. `DataConsentReplyRecordedException` → `AlreadyRecorded` (la carrera la cierra el índice, no la consulta previa). `HasCurrentAcceptanceAsync` sigue un salto de fusión y mira `FindLatestAsync(...)?.Event == Accepted`. Los tres dobles implementan `PublishBySystem` grabando igual que `PublishByIntegration`.
- [ ] **Step 3:** `Modules.Customers.UnitTests` completo verde. **Commit** `feat(customers): registrar la respuesta del bot a la política de datos, una vez por respuesta (spec 2026-10-26 D-5)`.

### Task 8: Permiso nuevo y `GET …/data-consent` (D-8, D-9)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersPermissions.cs:9-34` (`DataConsentRevoke = "customers.data_consent.revoke"`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1193` (admin), `:1295` (advisor), `:1508-1514` (`PermissionDefinition`), `:2031-2034` (política), `:323-335` (handler)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerDataConsents.cs` (query, handler, DTOs, mapeo)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEngagementEndpoints.cs:95-100` (ruta nueva)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:179-188, 243-256`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDataConsentApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: Tasks 6 y 7.
- Produces:
  - `PermissionDefinition(CustomersPermissions.DataConsentRevoke, "Revocar autorización de datos", "Permite registrar que un cliente revocó su autorización de tratamiento de datos personales.", "Customers", "high", RequiredModules: [ModuleKeys.Customers])`; en los roles `admin` y `advisor`.
  - `sealed record GetCustomerDataConsentQuery(Guid TenantId, Guid CustomerId) : IQuery<CustomerDataConsentDto>`.
  - `sealed record CustomerDataConsentDto(DataConsentCurrentDto? Current, bool CanRevoke, IReadOnlyList<DataConsentEventDto> History)`, `sealed record DataConsentCurrentDto(string Event, DateTimeOffset OccurredAt, string? Channel)`, `sealed record DataConsentEventDto(Guid Id, string Event, DateTimeOffset OccurredAt, string? Channel, string? Medium, string? Note, DataConsentActorDto Actor)`, `sealed record DataConsentActorDto(string Kind, Guid? Id, string Name)` — la forma del Contrato; el motivo BFF de `CanRevoke` va en el `<summary>` del DTO.
  - `internal static Task<CustomerDataConsentDto> DataConsentMapping.ToDtoAsync(Customer customer, IReadOnlyList<CustomerDataConsent> rows, ICustomerAuthorNames names, IExecutionContext context, CancellationToken ct)` (lo reusa la Task 9).

- [ ] **Step 1: RED.**
  - `CompositionRootTests.PermissionDiscoveryFindsTheSixtyFiveConstants` (renombrar la de `:179`; comentario `+ el de revocar la autorización de datos (spec 2026-10-26 D-9)`) → `Assert.Equal(65, …)`.
  - `CompositionRootTests.AdminAndAdvisorCarryTheDataConsentRevokePermission` — como `:243-256`: en `admin` y `advisor`, no en `billing` ni `cashier`; `Category = "Customers"`, `RequiredModules = [Customers]`, valor `"customers.data_consent.revoke"`.
  - `CustomerDataConsentApiTests.WithoutRowsTheCurrentIsNull` — `GET` → 200, `current: null`, `history: []`, `canRevoke: false`.
  - `CustomerDataConsentApiTests.TheHistoryIsNewestFirstWithTheActorNames` — dos registros del bot (`Rejected` y después `Accepted`, chatbot «Bienvenida») → `current.event = "Accepted"`, `current.channel = "WhatsApp"`; `history[0].event = "Accepted"`, `history[0].actor = { kind: "Chatbot", id: <chatbotId>, name: "Bienvenida" }`, `medium` y `note` `null`.
  - `CustomerDataConsentApiTests.CanRevokeNeedsTheAcceptanceAndThePermission` — con aceptación: `canRevoke` `true` con `customers.data_consent.revoke`, `false` sólo con `customers.customer.read`.
  - `CustomerDataConsentApiTests.ReadingNeedsCustomerReadAndTheTenant` — sin permiso → 403; otro tenant → 403 `authorization.denied` (nunca 404); id inexistente → 404 `customers.customer.not_found`.
  ```powershell
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
  ```
  Esperado: FAIL (`Assert.Equal() Failure: Expected 65 Actual 64`).
- [ ] **Step 2: GREEN.** `GetCustomerDataConsentHandler(ICustomerRepository customers, ICustomerDataConsentRepository consents, ICustomerAuthorNames names, IExecutionContext context)`: `CustomersAuthorization.EnsureAuthorized(…, CustomerRead)`; cliente o `CustomerNotFound.For`; `ToDtoAsync` con un solo lookup de nombres para los `ActorUserId` (ausente → `"Usuario eliminado"`, como `CustomerNoteMapping`). Ruta: `group.MapGet("/{customerId:guid}/data-consent", …).RequireAuthorization(CustomersPermissions.CustomerRead).Produces<CustomerDataConsentDto>()` + 403/404. Política: `.AddPolicy(CustomersPermissions.DataConsentRevoke, policy => AddPermissionRequirement(policy, CustomersPermissions.DataConsentRevoke))` (sin ella el `POST` de la Task 9 da 500).
- [ ] **Step 3:**
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDataConsentApiTests"
  ```
  **Commit** `feat(customers): estado e historial de la autorización de datos y permiso para revocarla (spec 2026-10-26 D-8, D-9)`.

### Task 9: Revocar desde la bandeja (D-5 `medium`, D-8, D-9)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerDataConsents.cs` (puerto de conversaciones, comando, validador, handler)
- Create: `src/Bootstrapper/CustomersConversations.cs`; Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:994` (registro junto a `ICustomerAuthorNames`), `:323-335` (handler)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEngagementEndpoints.cs` (ruta y `RevokeDataConsentAsync`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDataConsentApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CustomersConversationsTests.cs` (nuevo)

**Interfaces:**
- Consumes: Task 8.
- Produces:
  - `sealed record CustomerConversationRef(Guid ConversationId, Guid? CustomerId, string Channel, string? ChannelUserId, string? WaId)`; `interface ICustomerConversations { Task<CustomerConversationRef?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken ct); }` (adaptador `CustomersConversations` sobre `IConversationQueries.FindAsync`, `IConversationQueries.cs:97`; `Channel` = nombre de `ConversationChannel`).
  - `sealed record RevokeDataConsentRequest(string? Medium, Guid? ConversationId, string? Note)`; `sealed record RevokeDataConsentCommand(Guid TenantId, Guid CustomerId, string? Medium, Guid? ConversationId, string? Note) : ICommand<CustomerDataConsentDto>`; `sealed class RevokeDataConsentValidator : AbstractValidator<RevokeDataConsentCommand>`.
  - Ruta `POST /{customerId:guid}/data-consent/revocations` → 200 `CustomerDataConsentDto`; `.ProducesProblem` 403, 404, 409, 422.

- [ ] **Step 1: RED** (en `CustomerDataConsentApiTests` la factoría reemplaza `ICustomerConversations` por un doble con una conversación del cliente `(conv, customer, "WhatsApp", "CO.laura1", "573001234567")` y otra de otro cliente):
  - `RevokingByThisConversationStoresItsIdentity` — con aceptación, `{ medium: "ThisConversation", conversationId: conv, note: "Lo pidió por el chat." }` → 200; `current.event = "Revoked"`, `canRevoke: false`; la fila tiene `conversation_id`, `channel = 'WhatsApp'`, `channel_user_id = 'CO.laura1'`, `wa_id`, `actor_kind = 'Member'`, `actor_user_id = SubjectId`; `history[0].actor = { kind: "Member", id: SubjectId, name: … }`, `medium: "ThisConversation"`; outbox con `customers.customer.data_consent_revoked` y `actorType = 'Human'`.
  - `RevokingByAnotherMediumLeavesTheIdentityEmpty` — `{ medium: "Other", note: "Por correo." }` → las cuatro columnas `NULL`, `current.channel = null`.
  - `RevokingWithoutACurrentAcceptanceIs409` (Review Focus 4) — `[Theory]` sin filas / última `Rejected` / ya revocada → 409 `customers.data_consent.not_accepted`; `count(*)` no cambia.
  - `TheConversationMustBeOfThisCustomer` — conversación de otro cliente o inexistente → 422 `customers.data_consent.conversation_invalid`.
  - `TheBodyIsValidatedFieldByField` — `[Theory]`: nota `""`/`"  "`/501 caracteres → `errors` = `["Note"]`; `medium: "Phone"` o ausente → `["Medium"]`; `ThisConversation` sin `conversationId` y `Other` con `conversationId` → `["ConversationId"]`; todos 422 `validation.failed`.
  - `RevokingNeedsThePermission` — sólo `customers.customer.manage` → 403; un cliente fusionado → 422 `customers.customer.merged`; un cliente inactivo con aceptación → 200 (D-5).
  - `CustomersConversationsTests.TheConversationRowMapsToTheCustomersRef` — doble de `IConversationQueries` con un `ConversationRow` de Instagram → `Channel = "Instagram"`, `ChannelUserId` = `UserId`, `WaId = null`; `null` → `null`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomersConversationsTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDataConsentApiTests"
  ```
  Esperado: no compila (`CS0246 'ICustomerConversations'`).
- [ ] **Step 2: GREEN.** Validador: `Medium` `NotEmpty` + `Must(m => m is "ThisConversation" or "Other")`; `ConversationId` `NotNull().NotEqual(Guid.Empty)` cuando `ThisConversation` y `Null()` cuando `Other`; `Note` `NotEmpty().MaximumLength(CustomerDataConsent.NoteMaxLength)`. Handler, en este orden: `EnsureAuthorized(DataConsentRevoke)`; validar; cliente o 404; `customer.EnsureNotMerged()`; con `ThisConversation`, la conversación existe, `CustomerId == customerId`, `ChannelUserId` no nulo y el canal se lee como `DataConsentChannel` — si no, `CustomersDomainException("customers.data_consent.conversation_invalid", …)`; transacción + `LockCustomerAsync`; `FindLatestAsync(...)?.Event != Accepted` → `ResourceConflictException("customers.data_consent.not_accepted", …)`; `Add(Revocation(…, executionContext.SubjectId, …))`; `Publish(…, SubjectId, DataConsentRevoked, …)`; guardar, confirmar y devolver `ToDtoAsync`.
- [ ] **Step 3: Commit** `feat(customers): revocar la autorización de datos por la conversación o por otro medio (spec 2026-10-26 D-5, D-8, D-9)`.

### Task 10: La fusión mueve las filas al que queda (D-7)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerMergeStore.cs:82-98` (`MoveOwnedCoreAsync`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerMergeStore.cs` (`<summary>` de `MoveOwnedAsync`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeStoreTests.cs`, `CustomerMergeApiTests.cs`

**Interfaces:**
- Consumes: Tasks 7 y 8. Sin firmas nuevas: `MoveOwnedAsync` también mueve `data_consents`.

- [ ] **Step 1: RED.**
  - `CustomerMergeStoreTests.MoveOwnedMovesTheDataConsents` — filas del fusionado → `customer_id` del que queda; las de otro tenant no se tocan (el mismo caso que `:223` para direcciones).
  - `CustomerMergeApiTests.MergingMovesTheConsentsAndTheLatestRowWins` (Review Focus 3) — `[Theory]`: (a) que queda `Accepted` a las 10:00, fusionado `Rejected` a las 11:00 → `GET` del que queda: `current.event = "Rejected"`, dos filas, la más nueva primero; (b) que queda `Rejected` a las 11:00, fusionado `Accepted` a las 10:00 → `current.event = "Rejected"`; en los dos, el `GET` del fusionado trae `history: []`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerMergeStoreTests|FullyQualifiedName~CustomerMergeApiTests"
  ```
  Esperado: FAIL (`Assert.Equal() Failure: Expected 0 Actual 1` filas del fusionado).
- [ ] **Step 2: GREEN.** Un `ExecuteUpdateAsync` más en `MoveOwnedCoreAsync` sobre `DataConsents` filtrado por `TenantId` y `ids` (sólo `customer_id`; nada se recalcula ni se renumera: el orden es `(occurred_at, sequence)`). No va en `CustomerMergeReferences` (es tabla de Customers, D-7).
- [ ] **Step 3: Commit** `feat(customers): la fusión lleva el historial de consentimiento al cliente que queda (spec 2026-10-26 D-7)`.

### Task 11: Messaging — el `wamid` del entrante y colgar un cliente a la conversación (D-5, D-7)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationInbox.cs:12` (`InboundForAutomation` + `string? Wamid = null`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs:75-89, 119-129`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs:41-50`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationRepository.cs` (método nuevo junto a `:13-28`)
- Modify (doble): `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:737`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AutomationInboxTests.cs`, `AutomationEnginePersistenceTests.cs`

**Interfaces:**
- Produces:
  - `InboundForAutomation(…, DateTimeOffset OccurredAt, string? Wamid = null)`.
  - `Task<Guid?> IConversationRepository.LinkCustomerAsync(Guid tenantId, Guid conversationId, Guid customerId, bool created, DateTimeOffset now, CancellationToken cancellationToken)` — en una transacción: `UPDATE messaging.conversations SET customer_id = @customer WHERE id AND tenant_id AND customer_id IS NULL`; si escribió, el evento `CustomerCreated` (con `created`) o `CustomerLinked` con `CustomerId` por `ConversationEventRows.InsertAsync` (`ConversationEventRows.cs:13-24`), como la ingesta (`InboundIngestion.cs:197-205`); devuelve el `customer_id` que quedó (el de otro que ganó la carrera, o `null` si la conversación no es del tenant). Sin `version`: como `SetAutomationStepAsync`.

- [ ] **Step 1: RED.**
  - `AutomationInboxTests.ReadInboundCarriesTheWamid` — entrante sembrado con `wamid = 'wamid.ABC'` → `Wamid == "wamid.ABC"`.
  - `AutomationEnginePersistenceTests.LinkCustomerFillsOnlyAnEmptyCustomerAndWritesTheEvent` — conversación sin cliente → devuelve el id, `customer_id` escrito, un evento `CustomerCreated` en `messaging.messages` (`direction = 3`); segunda llamada con otro id → devuelve el primero, no escribe ni agrega evento; otro tenant → `null`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AutomationInboxTests.ReadInboundCarriesTheWamid|FullyQualifiedName~AutomationEnginePersistenceTests.LinkCustomerFillsOnlyAnEmptyCustomerAndWritesTheEvent"
  ```
  Esperado: no compila (`CS1061 'Wamid'`).
- [ ] **Step 2: GREEN.** `SELECT … message.wamid AS "Wamid"` e `InboundRow` con `string? Wamid`. El doble de `MessagingTestBed` lanza `NotSupportedException`.
- [ ] **Step 3:** `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests` verde. **Commit** `feat(messaging): el wamid del entrante para el motor y colgar el cliente de una conversación sin él (spec 2026-10-26 D-5, D-7)`.

### Task 12: Automations — el puerto y el bucle de efectos (D-2, D-4, D-7, D-10)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:9-43` (`FindVersionLabelAsync`), `:386-412` (acciones), y el puerto nuevo junto a `IAutomationMembers` (`:124-132`)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRepositories.cs` (junto a `:37-41`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/AutomationsRunFailures.cs:4-11`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:22-29, 62-141, 151-165, 277-284`
- Modify (dobles): `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs:748-775, 835`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs`

**Interfaces:**
- Consumes: Tasks 3 y 4.
- Produces:
  - `enum DataConsentCheck { NotAccepted, Accepted, Unavailable }`.
  - `sealed record DataConsentRecord(Guid TenantId, Guid ConversationId, DataConsentDecision Decision, string ShownText, Guid? PromptMessageId, Guid ReplyMessageId, Guid ChatbotId, string ChatbotName, Guid ChatbotVersionId, int ChatbotVersionNumber, string NodeId)`.
  - `interface IAutomationDataConsents { Task<DataConsentCheck> CheckAsync(Guid tenantId, Guid conversationId, CancellationToken ct); Task<bool> RecordAsync(DataConsentRecord record, CancellationToken ct); }` — `false` = no se pudo registrar (sin cliente, sin identidad o sin el entrante).
  - `sealed record ChatbotVersionLabel(string ChatbotName, int Number)`; `Task<ChatbotVersionLabel?> IChatbotRepository.FindVersionLabelAsync(Guid tenantId, Guid versionId, CancellationToken ct)` (nombre actual del chatbot y número de la versión; `null` si ya no existe).
  - `AutomationsAuditActions.DataConsentUnanswered = "automations.run.data_consent_unanswered"`, `DataConsentUnavailable = "automations.run.data_consent_unavailable"`; `AutomationsRunFailures.DataConsentUnrecorded = "automations.run.data_consent_unrecorded"`.
  - `RunEffects(…, IAutomationsAuditRecorder audit, IAutomationDataConsents dataConsents, IChatbotRepository chatbots, ILogger<RunEffects> logger)`.

- [ ] **Step 1: RED** en `RunProcessorTests` (doble `RecordingDataConsents(DataConsentCheck check, bool records = true)` que guarda `Checks` y `Records`; flujo `consent → Mensaje`):
  - `TheConsentBlockAsksWhenThereIsNoAcceptance` — `NotAccepted` → `Messenger` mandó un interactivo con los dos botones; corrida `Waiting`; `ConsentPrompt.PromptMessageId` = el `MessageId` que devolvió `SendInteractiveAsync`.
  - `ACurrentAcceptanceSkipsTheQuestion` — `Accepted` → ningún interactivo; sigue al Mensaje.
  - `AnUnresolvableCustomerLeavesByRejectedAndAuditsTheSystem` (D-7) — `Unavailable` → sin interactivo; sale por `rejected`; `Audit` con `automations.run.data_consent_unavailable` (`bySystem: true`).
  - `ATapIsRecordedWithTheRunVersionAndTheReply` — corrida `Waiting` con `ConsentPrompt("consent", "Texto", prompt)`, entrante `Tapped("accepted")` → `Records == [new DataConsentRecord(Tenant, Conversation, Accepted, "Texto", prompt, <messageId del entrante>, ChatbotId, "Bienvenida", VersionId, 1, "consent")]` (el doble de `FindVersionLabelAsync` devuelve `("Bienvenida", 1)`), antes de cualquier envío del bloque siguiente.
  - `AReplyThatCannotBeRecordedFailsTheRunBeforeContinuing` — `records: false` → `FailAsync` con `failure_code = automations.run.data_consent_unrecorded` y `HandoffCause.Kind = FlowFailed`; el Mensaje de `accepted` no se envió.
  - `ExhaustingTheConsentAuditsAndRecordsNothing` (D-4) — tres inválidas → `Records` vacío; `Audit` con `automations.run.data_consent_unanswered`; sigue por `rejected`.
  - `ARunStuckOnTheConsentCheckAsksAgain` — corrida `Running` en el bloque (unidad muerta) → `PendingInputAsync` vuelve a llamar `CheckAsync`.
  - `AutomationsPersistenceTests.TheVersionLabelIsTheChatbotNameAndTheVersionNumber` — publicar dos veces → `FindVersionLabelAsync(v1) == (nombre, 1)`; otro tenant → `null`.
  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests"
  ```
  Esperado: no compila (`CS0246 'IAutomationDataConsents'`).
- [ ] **Step 2: GREEN.**
  - Envío: `SendAsync(...) is not BotSendOutcome.Sent sent` corta como hoy; si es `SendInteractive` y `state.ConsentPrompt is { PromptMessageId: null } prompt && prompt.NodeId == effect.NodeId` → `state = state with { ConsentPrompt = prompt with { PromptMessageId = sent.MessageId } }`.
  - `case Effect.CheckBusinessHours or Effect.CallHttp or Effect.CheckDataConsent` comparte el tope de reentradas; `ExternalAsync` traduce `CheckDataConsent` a `new EngineInput.DataConsent(nodeId, check == Accepted, CanRecord: check != Unavailable)`. Al empezar cada vuelta del bucle, `next is EngineInput.DataConsent { CanRecord: false }` → `Audit(run, DataConsentUnavailable, now)` (cubre también la entrada de `PendingInputAsync`).
  - `case Effect.RecordDataConsent`: `FindVersionLabelAsync(run.TenantId, run.ChatbotVersionId)` (`null` → `FailAsync` con `VersionMissing`); `RecordAsync(… run.LastInboundMessageId ?? throw new InvalidOperationException(…) …)`; `false` → `FailAsync(run, state, HandoffCause.FlowFailed(DataConsentUnrecorded, nodeId, título, http), now, ct)` y `return`.
  - `case Effect.DataConsentUnanswered` → `Audit(run, DataConsentUnanswered, now)`.
  - `PendingInputAsync`: `DataConsentNode consent => await ExternalAsync(unit.Run, new Effect.CheckDataConsent(consent.Id), …)`.
  - El registro del adaptador en `QepServiceCollectionExtensions.cs:964-982` es de la Task 13. Hasta entonces el host de integración no arranca (en `Development` el contenedor valida al construir y `RunEffects` pide `IAutomationDataConsents`): en esta tarea **sólo** pruebas unitarias, y `TheVersionLabelIsTheChatbotNameAndTheVersionNumber` se escribe acá y se corre en la Task 13.
- [ ] **Step 3:** `Modules.Automations.UnitTests` completo verde. **Commit** `feat(automations): el procesador pregunta y registra el consentimiento por un puerto (spec 2026-10-26 D-2, D-4, D-10)`.

### Task 13: Bootstrapper — el adaptador resuelve cliente, canal, identidad y `wamid` (D-5, D-7, D-10)

**Files:**
- Create: `src/Bootstrapper/AutomationDataConsents.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:964-982` (`services.AddScoped<IAutomationDataConsents, AutomationDataConsents>();` junto a `IAutomationMembers`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationDataConsentsAdapterTests.cs` (nuevo; mismo armado que `AutomationConversationsAdapterTests.cs:25-71`, con `user_id = 'CO.laura1'` en la conversación)

**Interfaces:**
- Consumes: Tasks 7, 11 y 12.
- Produces: `internal sealed partial class AutomationDataConsents(IConversationQueries queries, IConversationRepository conversations, IMessagingCustomerDirectory customers, IAutomationInbox inbox, ICustomerDataConsents consents, IClock clock, ILogger<AutomationDataConsents> logger) : IAutomationDataConsents`.

- [ ] **Step 1: RED.**
  - `ACheckWithoutCustomerCreatesAndLinksOne` (D-7) — conversación sin `customer_id` → `CheckAsync` = `NotAccepted`; `customer_id` escrito, un cliente nuevo en `customers.customers` con `whatsapp_user_id = 'CO.laura1'`, un evento `CustomerCreated`; segunda llamada → el mismo cliente (idempotente por BSUID).
  - `ACheckWithAnAcceptedCustomerSaysAccepted` — tras un `RecordAsync(Accepted)` → `Accepted`.
  - `AConversationWithoutChannelIdentityIsUnavailable` — `user_id NULL` y sin cliente → `Unavailable`, sin cliente creado.
  - `ARecordStoresChannelIdentityAndTheReplyWamid` — entrante sembrado con `wamid = 'wamid.R1'` → fila con `channel = 'WhatsApp'`, `channel_user_id = 'CO.laura1'`, `wa_id = '573001234567'`, `conversation_id`, `reply_wamid = 'wamid.R1'`, `occurred_at` = el `occurred_at` del entrante, `chatbot_name`, `chatbot_version_number`; devuelve `true`.
  - `TheReplyIsRecordedForTheCustomerTheConversationHasWhenReplying` (Review Focus 5) — check con el cliente A, `UPDATE messaging.conversations SET customer_id = B`, `RecordAsync` → la fila nueva es de B; las filas que A ya tenía siguen en A.
  - `AnUnknownReplyCannotBeRecorded` — `ReplyMessageId` que no es un entrante del tenant → `false`, sin fila.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationDataConsentsAdapterTests|FullyQualifiedName~AutomationsPersistenceTests.TheVersionLabelIsTheChatbotNameAndTheVersionNumber"
  ```
  Esperado: FAIL al arrancar el host (`Unable to resolve service for type 'IAutomationDataConsents' while attempting to activate 'RunEffects'`).
- [ ] **Step 2: GREEN.**
  - Cliente de la conversación: `row.CustomerId`; si es `null` y `row.UserId` no es `null`, `customers.EnsureAsync(tenantId, new MessagingContact(row.UserId, row.WaId, row.ProfileName, row.Username, row.Channel))` y `conversations.LinkCustomerAsync(…, ensured.CustomerId, ensured.Created, clock.UtcNow, …)`; un fallo de esos dos (salvo `OperationCanceledException`) se loguea en `Warning` con tenant y conversación —sin datos del cliente— y deja el resultado en `Unavailable` (D-7: sale por «No acepto» sin preguntar).
  - `CheckAsync`: sin fila, sin cliente o sin `UserId` → `Unavailable`; si no, `consents.HasCurrentAcceptanceAsync` → `Accepted`/`NotAccepted`.
  - `RecordAsync`: misma resolución sobre la conversación **de ahora**; `inbox.ReadInboundAsync(tenantId, record.ReplyMessageId)` (`null` → `false`); `DataConsentIdentity(conversationId, Enum.Parse<DataConsentChannel>(row.Channel.ToString()), row.UserId, row.WaId)`; decisión `Accepted`/`Rejected` → `DataConsentEvent` con un `switch` explícito; `RecordFromChatbotAsync(new(tenantId, customerId, decision, inbound.OccurredAt, identity, new ChatbotConsentEvidence(record.ShownText, record.PromptMessageId, record.ReplyMessageId, inbound.Wamid, record.ChatbotId, record.ChatbotName, record.ChatbotVersionId, record.ChatbotVersionNumber, record.NodeId)))`; `CustomerMissing` → `false`, `Recorded`/`AlreadyRecorded` → `true`.
- [ ] **Step 3:** `tests/ArchitectureTests` verde (`AutomationsLayerTests`, `CustomersLayerTests`, `MessagingLayerTests` sin cambios). **Commit** `feat(automations): adaptador que resuelve el cliente de la conversación para la política de datos (spec 2026-10-26 D-5, D-7, D-10)`.

### Task 14: De punta a punta, suite completa y CLAUDE.md

**Files:**
- Create: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.DataConsent.cs` (parcial de `EngineEndToEndTests`, con su `ConsentFlowJson`: `n1` dataConsent → `accepted: n2` (Mensaje «Gracias.» → resolve), `rejected: n3` (Mensaje «Entendido.» → resolve))
- Modify: `CLAUDE.md` («Gotchas verificados»)

- [ ] **Step 1: RED → GREEN** (las piezas ya existen; si algo falla es un hueco de su tarea y se arregla allí):
  - `TheConsentBlockAsksRecordsTheTapAndSkipsTheQuestionNextTime` — `InboundAsync(f, "w1", "Hola")`, drenar → `f.Meta.Sends() == ["[button] <texto interpolado>"]`; `TapAsync(f, "w2", "accepted", "Acepto")`, drenar → una fila `Accepted` con `reply_wamid = 'w2'`, `shown_text` = el texto enviado, `prompt_message_id` = el id del mensaje del bot con los botones, `chatbot_version_number = 1`, auditoría `customers.customer.data_consent_accepted`; se envió «Gracias.». Una conversación nueva del mismo cliente (`AReopenedConversationInterruptsTheStaleRunAndStartsANewOne`, `:476`, como molde) pasa por el bloque sin enviar botones.
  - `AReplyAfterARepublishIsRecordedWithTheRunVersion` (Review Focus 2) — preguntar con la v1, `RepublishChatbotAsync` con otro texto, tocar «Acepto» → `chatbot_version_number = 1` y `shown_text` = el texto de la v1.
  - `ExhaustedConsentFollowsRejectedWithoutARow` — tres «hola» → se envió «Entendido.», `count(*) = 0` en `customers.data_consents`, `AuditAsync(f, "automations.run.data_consent_unanswered") == 1`.
- [ ] **Step 2: Suite completa, una vez, en primer plano** (`Api.exe` detenido):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test
  ```
  Comparar las fallas por nombre contra la base de `develop`; sólo cuentan las nuevas.
- [ ] **Step 3: CLAUDE.md**, en «Gotchas verificados»: «**`customers.data_consents` es prueba legal: sólo se inserta** (spec 2026-10-26 D-5). Nada la edita ni la borra salvo la fusión, que mueve `customer_id` (`CustomerMergeStore.MoveOwnedAsync`); el estado vigente es la última fila por `(occurred_at, sequence)`. Una respuesta es una fila: el reintento de una unidad del motor lo frena `IX_data_consents_reply_message`, que `CustomersUnitOfWork` traduce por nombre; no lo cambies por la consulta previa. Los CHECK de la tabla exigen identidad de canal en lo que escribe el bot y en `Revoked` + `ThisConversation`, y la prohíben en `Revoked` + `Other`: un `INSERT` a mano tiene que cumplirlos. El texto que se guarda es el que salió (`RunState.ConsentPrompt`, columna `conversation_runs.consent_prompt`), no la plantilla del bloque.»
- [ ] **Step 4: Commit** `test(automations): la política de datos de punta a punta (spec 2026-10-26)` y `docs: gotcha del registro de consentimiento de datos`.

---

## Orden y dependencias

- Automations: Task 1 → 2 → 3 → 4 → 5. Customers: Task 6 → 7 → 8 → 9 → 10. Messaging: Task 11 (independiente). Task 12 necesita 3 y 4; Task 13 necesita 7, 11 y 12; Task 14 al final.
- Las tres ramas (Automations 1-5, Customers 6-10, Messaging 11) pueden ir en paralelo sólo en worktrees separados; con un solo escritor, en el orden numérico.
- Las Tasks 12 y 13 van seguidas: entre las dos el host de integración no arranca (ver Task 12, Step 2).
- El frontend depende sólo del **Contrato**; se despliega después del backend.

## Desvíos del spec por el código (resueltos así en este plan)

- **D-3 «las dos salidas sin conectar → aviso»:** `FlowValidator.CheckOutputs` (`FlowValidator.cs:131-156`) marca **Error** (`node.output_unconnected`) toda salida sin conectar salvo `tooMany` (aviso, `:43`) e `invalid` (nada). «Como cualquier salida» en el código es Error, y eso además garantiza la premisa de D-4 (el camino «sin consentimiento» está conectado). El plan usa Error.
- **D-5 «`wa_id` obligatorio en `Accepted`/`Rejected` y en `Revoked` + `ThisConversation`»:** `Conversation.WaId` es `null` cuando Meta no manda el teléfono (`Conversation.cs:62-63`) y siempre en Instagram (CLAUDE.md). El CHECK exige `conversation_id`, `channel` y `channel_user_id`; `wa_id` queda opcional en esos casos y vacío con `Other`.
- **D-5 `actor_member_id`:** Customers sólo conoce el `SubjectId` del llamador (usuario, no membresía), como `CustomerNote.AuthorUserId` y `ICustomerAuthorNames` (`CustomerEngagementPorts.cs:63-71`). La columna es `actor_user_id`.
- **D-5 `ordinal`:** se reemplaza por `sequence bigint` de identidad global (la clave de orden que «tolera la fusión», que el spec deja al builder): mover filas no exige renumerar.
- **D-7 cita `InboundIngestion.cs:73` para `EnsureAsync`:** la llamada está en `WebhookDeliveryProcessor.cs:254` (`PrepareAsync`); `InboundIngestion.cs:73` es el `COALESCE(customer_id, …)` del eco. No cambia el diseño.
