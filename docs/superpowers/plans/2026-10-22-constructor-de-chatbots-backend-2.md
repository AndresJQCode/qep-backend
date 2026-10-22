# Constructor de chatbots, backend 2 (motor, bloque HTTP y simulador) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dejar en `develop` las fases 4 a 6 del spec: el motor que lee `messaging.automation_inbox` y conversa por el chatbot publicado (`FlowEngine` puro + worker + puertos a Mensajería), el proveedor «API externa» de Integrations con el bloque HTTP y su registro en `http_calls`, y el simulador sobre el borrador. Con esto el flag `Messaging:Automation:Enqueue` se prende y un chatbot activo atiende de verdad.

**Architecture:** El motor es una función pura en `Modules.Automations.Domain` (`FlowEngine.Step(flow, settings, state, input) → (state', effects)`, D-M2) que comparten el worker real y el simulador. `RunProcessor` (Application) ejecuta los efectos por puertos (`IAutomationMessenger`, `IAutomationConversations`, `IHttpBlockExecutor`, …) adaptados en Bootstrapper sobre Messaging, Integrations y Tenancy; nada en Automations referencia otro módulo (`AutomationsLayerTests`, plan 1). La unidad de trabajo es un entrante por fila del inbox, con bloqueo por conversación, dentro del lease del reclamo (§5.2). El bloque HTTP sale por un cliente nombrado con política anti-SSRF compartida con el probador de la conexión (D-H2).

**Tech Stack:** .NET 10, EF Core + Npgsql, System.Text.Json (`JsonNode`), `IHttpClientFactory` + `SocketsHttpHandler.ConnectCallback`, xunit.v3 + Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md` (§5, §7, §8, §5.5, D-M*, D-H*, D-S*, Anexos A y B). **Depende del plan 1** (`2026-10-21-constructor-de-chatbots-backend-1.md`) ya mergeado en `develop`: el módulo `Automations`, `FlowDefinition`/`FlowValidator`, el agregado `Chatbot`, `IBotOutbound`, `IAutomationInbox`, `Conversation.EndAutomation`, `RecordBySystem` y los puertos `IAutomationConnections`, `IAutomationExternalApis`, `IAutomationBusinessHours`.

## Global Constraints

- Rama `feature/automatizaciones-motor-backend` en un worktree desde `develop` **después** de que el plan 1 esté mergeado (`git log develop --oneline | grep "backend 1 del constructor"` tiene que encontrar el commit de `docs(estado)`); merge `--ff-only` a `develop`, nunca a `main`, nunca push.
- Conventional commits, en español, citando el spec (`spec 2026-10-21 D-M2`); **sin atribución de IA** (`CLAUDE.md:63`): sin `Co-Authored-By`.
- TDD: RED antes que GREEN, con la salida literal de ambas corridas en el reporte de cada tarea (`CLAUDE.md:57`).
- Copy y comentarios en español colombiano, tuteando. Identificadores, códigos de error, rutas y permisos en inglés y tal cual el spec.
- Códigos de error `<módulo>.<recurso>.<motivo>`; ningún status HTTP se arma a mano (`ApiExceptionHandler` mapea las excepciones de `BuildingBlocks.Application/ApplicationExceptions.cs`).
- `Modules.Automations.{Domain,Application}` no referencian Messaging, Integrations, Customers ni Identity (`AutomationsLayerTests`): todo tipo de otro módulo que el motor necesite se **redeclara** en Automations y se adapta en `src/Bootstrapper/Automation*.cs`.
- Aislamiento de tenant doble: ruta `{tenantId:guid}` + política, y el handler revalida (403, nunca 404). Todo método de repositorio y de puerto recibe `tenantId`.
- Nada del cuerpo de una respuesta HTTP externa, de sus headers ni de la clave de API se registra en logs, auditoría, `http_calls` ni `platform.request_failures` (D-H5, D-H6). El cliente HTTP del bloque lleva `RemoveAllLoggers()`.
- Un reintento de la fila del inbox no puede duplicar un mensaje del bot: `clientId` determinístico por `(runId, nodeId, índice, attempts)` (§5.3).
- Las migraciones se generan con la fábrica de diseño (`dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`). Antes de correr pruebas locales: `$env:Seed__Enabled = "false"`.
- Correr sólo las pruebas del módulo tocado más `tests/ArchitectureTests` con `--filter`.
- `docs/estado.md` **no** se toca en la rama (se actualiza en `develop` al mergear, tarea 14).

## Review Focus

1. **Un asesor toma la conversación entre el reclamo de la fila y el envío** (D-M3): `IBotOutbound` responde `NotAutomated` y la corrida termina `Interrupted`, no `Failed`, y nada pasa a `Paused` con `Handoff` (tarea 6, `EngineEndToEndTests.AnAdvisorTakingBetweenClaimAndSendInterruptsTheRun`).
2. **El cliente escribe el título del botón con otra tilde o mayúscula** («servicio tecnico») en vez de tocarlo: cuenta como esa salida (D-I5), no como intento inválido (tarea 3, `FlowEngineWaitingTests.ATypedTitleMatchesWithoutAccentsOrCase`).
3. **Una API responde 200 con HTML** (o JSON que no tiene la ruta del mapeo): el bloque sale por `error` con `outcome = mapping_failed`, sin excepción ni fila `Failed` (tarea 11, `HttpBlockExecutorTests.ANonJsonBodyWithAMappingIsMappingFailed`).
4. **Un texto interpola una variable que es objeto o lista** (`{cliente}`): sale como JSON compacto, nunca `System.Text.Json.Nodes.JsonObject` (tarea 1, `TemplateTextTests.AnObjectVariableInterpolatesAsCompactJson`).
5. **Un `baseUrl` cuyo host resuelve a una IP privada en el momento de llamar** (rebinding de DNS después de «Probar»): `ConnectCallback` rechaza la conexión y el bloque sale por `error` con `url_rejected` (tarea 10, `ExternalApiUrlPolicyTests.AHostResolvingToAPrivateRangeIsRejectedAtConnect`; tarea 11, `HttpBlockExecutorTests.ARejectedUrlIsAnErrorOutcomeWithoutACall`).

---

## Fase 4: motor

### Task 1: Texto, rutas por puntos y variables de la corrida

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Text/TextMatch.cs`, `Text/JsonPath.cs`, `Text/TemplateText.cs`, `Engine/RunVariables.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/TextMatchTests.cs`, `JsonPathTests.cs`, `RunVariablesTests.cs`, `TemplateTextTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class TextMatch { public static string Normalize(string text); public static bool Same(string? a, string? b); public static bool IsAnyOf(string? text, IEnumerable<string> candidates); }   // sin tildes (FormD + quitar NonSpacingMark), minúsculas invariantes, espacios colapsados y recortados
  public static class JsonPath { public static bool IsValid(string path); public static JsonNode? Select(JsonNode? root, string path); }   // `a.b[0].c`; nombres `^[a-zA-Z_][a-zA-Z0-9_]*$`; índice `[n]`; ruta inválida o ausente → null
  public sealed class RunVariables { public static RunVariables Empty(); public static RunVariables Parse(string json); public string ToJson(); public JsonNode? Get(string path); public void Set(string name, JsonNode? value); public bool Has(string path); public string TextOf(string path); public IReadOnlyDictionary<string, JsonNode?> Snapshot(); public RunVariables Clone(); }
  public static class TemplateText { public static string Interpolate(string template, RunVariables variables); public static IReadOnlyList<string> ReferencesIn(string template); }
  ```
- `TextOf`: escalar → su texto (números con `CultureInfo.InvariantCulture`, booleanos `true`/`false`); objeto o lista → JSON compacto; `null` o ausente → `""`. `Interpolate` reemplaza `{ruta}` cuando `JsonPath.IsValid(ruta)`; un `{` que no abre una ruta válida queda literal. `Set` reemplaza el valor entero de la variable raíz (no escribe sub-rutas).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// TextMatchTests
[Fact] public void NormalizeStripsAccentsCaseAndSpaces() => Assert.Equal("servicio tecnico", TextMatch.Normalize("  Servicio   Técnico "));
[Fact] public void SameIgnoresAccentsAndCase() => Assert.True(TextMatch.Same("Mantenimiento", "mantenimiento"));
[Fact] public void IsAnyOfMatchesKeywords() => Assert.True(TextMatch.IsAnyOf("ACEPTO", ["acepto", "sí"]));
// JsonPathTests
[Fact] public void SelectsNestedAndIndexed()      // {"data":[{"id":7}]} + "data[0].id" → 7
[Fact] public void AMissingSegmentIsNull()         // "data[3].id" → null; "nope.x" → null
[Fact] public void InvalidPathsAreRejected()        // "1a", "a..b", "a[x]", "" → IsValid false y Select null
// RunVariablesTests
[Fact] public void SetAndGetRoundTripThroughJson()  // Set("cedula", "123"), Set("cliente", obj) → Parse(ToJson()).Get("cliente.id")
[Fact] public void TextOfFormatsScalarsInvariantly() // 1234.5 → "1234.5"; true → "true"; null → ""
// TemplateTextTests
[Fact] public void InterpolatesNestedVariables()    // "Hola {cliente.firstName}, tu cédula es {cedula}" → "Hola Ana, tu cédula es 123"
[Fact] public void AMissingVariableIsEmpty()        // "{nada}" → ""
[Fact] public void AnObjectVariableInterpolatesAsCompactJson()   // "{cliente}" → "{\"id\":7,\"firstName\":\"Ana\"}"
[Fact] public void ABraceThatIsNotAPathStaysLiteral()           // "{ \"customerId\": {cliente.id} }" → "{ \"customerId\": 7 }"
[Fact] public void ReferencesInListsEveryPath()                  // → ["cliente.firstName", "cedula"] (raíces para el validador: ReferencesIn devuelve la ruta entera; el validador compara la raíz)
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~TextMatchTests|FullyQualifiedName~JsonPathTests|FullyQualifiedName~RunVariablesTests|FullyQualifiedName~TemplateTextTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar los cuatro tipos.** `RunVariables` envuelve un `JsonObject`; `Get` delega en `JsonPath.Select`. `ReferencesIn` con una `Regex` `\{([a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*|\[\d+\])*)\}` compilada.
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (13).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): texto sin tildes, rutas por puntos y variables de la corrida (spec 2026-10-21 D-M8)"`

### Task 2: `FlowEngine`: estado, efectos y bloques que no esperan

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Engine/RunState.cs`, `Engine/EngineInput.cs`, `Engine/Effect.cs`, `Engine/InteractiveMessage.cs`, `Engine/EngineSettings.cs`, `Engine/FlowEngine.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum RunStatus { Running, Waiting, Completed, HandedOff, Resolved, WindowClosed, Failed, Abandoned, Interrupted }
  public sealed record TraceEntry(DateTimeOffset At, string NodeId, string Kind, string Outcome);
  public sealed record RunState(RunStatus Status, string? CurrentNodeId, int StepNumber, int Attempts, RunVariables Variables, IReadOnlyList<TraceEntry> Trace)
  { public static RunState Initial(RunVariables fixedVariables); public const int TraceMax = 200; }
  public sealed record EngineSettings(string RestartKeyword, int MaxAttempts);
  public abstract record EngineInput
  {
      public sealed record Start : EngineInput;
      public sealed record CustomerMessage(string Kind, string? Text, string? ReplyId, AttachmentInput? Attachment) : EngineInput;   // Kind: Text | Interactive | Image | Document | Video | Audio | Unsupported
      public sealed record HttpResult(string NodeId, bool Ok, int? Status, RunVariables Mapped, string? ErrorCode) : EngineInput;
      public sealed record BusinessHours(string NodeId, bool IsOpen) : EngineInput;
  }
  public sealed record AttachmentInput(Guid MessageId, string? FileName, string MimeType, bool Stored);
  public abstract record InteractiveMessage(string Body)
  {
      public sealed record Buttons(string Body, IReadOnlyList<(string Id, string Title)> Items) : InteractiveMessage(Body);
      public sealed record List(string Body, string ButtonLabel, IReadOnlyList<(string Id, string Title, string? Description)> Rows) : InteractiveMessage(Body);
      public sealed record CtaUrl(string Body, string Label, string Url) : InteractiveMessage(Body);
  }
  public abstract record Effect(string NodeId)
  {
      public sealed record SendText(string NodeId, string Text) : Effect(NodeId);
      public sealed record SendInteractive(string NodeId, InteractiveMessage Message) : Effect(NodeId);
      public sealed record CallHttp(string NodeId, HttpNode Node) : Effect(NodeId);
      public sealed record CheckBusinessHours(string NodeId) : Effect(NodeId);
      public sealed record WaitForCustomer(string NodeId, string StepTitle) : Effect(NodeId);
      public sealed record Handoff(string NodeId) : Effect(NodeId);
      public sealed record Resolve(string NodeId, Guid? ReasonId) : Effect(NodeId);
      public sealed record End(string NodeId, string Reason) : Effect(NodeId);   // Reason: "output_unconnected"
  }
  public sealed record StepResult(RunState State, IReadOnlyList<Effect> Effects);
  public static class FlowEngine { public static StepResult Step(FlowDefinition flow, EngineSettings settings, RunState state, EngineInput input, DateTimeOffset now); }
  ```
- Reglas de esta tarea (§5.4): `Start` entra por `flow.Start.FirstNodeId` y avanza nodo a nodo; `message` → `SendText` o `SendInteractive(CtaUrl)` si tiene `UrlButton`, luego `next`; `condition` evalúa `Rules` con `Match` `all`/`any` (operadores `isEmpty`, `isNotEmpty`, `equals`, `notEquals`, `contains` sobre `TextOf` con `TextMatch.Same`/`Contains` normalizados; `greaterThan`, `lessThan` numéricos con `decimal.TryParse` invariante; `countEquals`, `countGreaterThan` sobre el largo de un array, 0 si no es array) → `yes`/`no`; `businessHours` → efecto `CheckBusinessHours` y el estado queda `Running` en ese nodo hasta el `EngineInput.BusinessHours` → `open`/`closed`; `http` → `CallHttp` y queda `Running` hasta `HttpResult`: `Ok` → mezcla `Mapped` en `Variables` y sale por `ok`; si no, por `error`; `handoff`/`resolve` → `SendText` opcional (texto interpolado) + `Handoff`/`Resolve`, estado `HandedOff`/`Resolved`. Una salida con `null` → `End("output_unconnected")` y `Completed`. Un nodo que espera (`openQuestion`, `buttons`, `list`, `attachment`) envía y produce `WaitForCustomer(nodeId, node.Title)`, estado `Waiting` con `Attempts = 0` (lo que hace con el mensaje del cliente es la tarea 3). Cada nodo visitado agrega un `TraceEntry` (recorta a los últimos 200) y suma `StepNumber`. Un `HttpResult`/`BusinessHours` cuyo `NodeId` no es el `CurrentNodeId` se ignora (estado igual, sin efectos). Textos y URL del botón se interpolan con `TemplateText`.

- [ ] **Step 1: Escribir las pruebas que fallan** (helpers `Flow(params FlowNode[])`, `Settings()` = («reiniciar», 3), `Start(flow)` = `Step(flow, settings, RunState.Initial(Empty), new Start(), now)`)

```csharp
[Fact] public void StartSendsTheMessagesUntilTheFirstWaitingNode()      // message → openQuestion: efectos [SendText, SendText, WaitForCustomer("n2","Número de documento")], Status Waiting, CurrentNodeId n2, StepNumber 2
[Fact] public void AMessageWithUrlButtonIsACtaUrl()                      // SendInteractive(CtaUrl(label, url interpolada))
[Fact] public void AnUnconnectedOutputEndsCompleted()                   // message next null → [SendText, End("output_unconnected")], Completed
[Fact] public void ConditionAllAndAnyRouteYesOrNo()                     // variables {cliente:{id:1}}: isNotEmpty cliente → yes; any con una regla falsa y una verdadera → yes; all → no
[Fact] public void ConditionCountOperatorsReadArrays()                  // equipos [a,b] countGreaterThan 1 → yes; sobre un objeto → 0 → no
[Fact] public void BusinessHoursWaitsForTheAnswerAndRoutes()           // Start → [CheckBusinessHours("n1")], Running; BusinessHours(n1, false) → closed
[Fact] public void HttpOkMergesMappedVariablesAndRoutesOk()             // Start → [CallHttp]; HttpResult(ok, 200, {cliente:{id:7}}) → siguiente nodo usa {cliente.id} = "7"
[Fact] public void HttpErrorRoutesError()                               // Ok false → error
[Fact] public void AStaleHttpResultIsIgnored()                          // NodeId distinto → mismo estado, sin efectos
[Fact] public void HandoffSendsTheOptionalTextAndEnds()                 // [SendText, Handoff], HandedOff
[Fact] public void ResolveCarriesTheReasonId()                          // Resolve(reasonId), Resolved
[Fact] public void TraceIsCappedAtTwoHundred()                          // un ciclo message→message… de 250 nodos encadenados → Trace.Count == 200 y los últimos
[Fact] public void TheAnnexAFlowStartsWithTheWelcomeAndWaitsForTheDocument()   // fixture annex-a.json de la tarea 11 del plan 1
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngineTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar los records y `FlowEngine.Step` con un bucle `while` sobre el nodo actual que corta en espera, en efecto externo o en terminal.** Los nodos de espera sólo emiten su mensaje y `WaitForCustomer` (la tarea 3 agrega el manejo del `CustomerMessage`; por ahora un `CustomerMessage` lanza `NotImplementedException`).
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (13).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): FlowEngine puro con estado, efectos y bloques sin espera (spec 2026-10-21 D-M2, §5.4)"`

### Task 3: `FlowEngine`: bloques que esperan, intentos, reinicio

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs`
- Create: `src/Modules/Automations/Modules.Automations.Domain/Engine/CustomerReply.cs` (la clasificación de un `CustomerMessage` frente al nodo que espera)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineWaitingTests.cs`

**Interfaces:**
- Produces: `internal static class CustomerReply { public static ReplyOutcome Classify(FlowNode waiting, EngineInput.CustomerMessage message, RunVariables variables); }` con `ReplyOutcome = Output(string output, JsonNode? saved) | Invalid`.
- Reglas (§5.4, D-M7, D-I5, D-C7):
  - Antes de clasificar: si `message.Text` es `TextMatch.Same` con `settings.RestartKeyword` → la corrida **reinicia**: `Variables` vuelve a las fijas (`nombre`, `telefono`, `empresa`, `conexion` se conservan; el resto se borra), `Attempts = 0`, y se ejecuta como `Start` (manda el primer mensaje). Trace `("restart")`.
  - `openQuestion`: sólo `Kind == Text`; `validation.kind`: `none` (todo), `digits` (`^\d+$` sobre el texto recortado), `number` (`decimal.TryParse` invariante, admite `,` como decimal reemplazándola por `.`), `regex` (`Pattern`, timeout 100 ms, `RegexOptions.CultureInvariant`), `keywords` (`TextMatch.IsAnyOf`). Válido → guarda el texto recortado en `SaveTo` (número como `JsonValue` numérico) y sale por `answered`.
  - `buttons`: `ReplyId` igual a un `FlowButton.Id` → esa salida; sin `ReplyId`, texto `TextMatch.Same` con un `Label` → esa salida; guarda `{ id, title }` en `SaveTo` si existe. Otro → inválido.
  - `list`: filas = `Fixed` o, con `dynamic`, las de la colección `variables.Get(Collection)` si es array (campo `IdField` → id, `TitleField` → title, `DescriptionField` → description; filas sin id o sin título se saltan); con `ExtraOption` se agrega al final. Más de 10 filas → **al entrar**, sin enviar, sale por `tooMany`; cero filas y sin `ExtraOption` → también `tooMany` (no hay lista que mostrar). Respuesta: `ReplyId` igual al id de una fila → `chosen` con `{ id, title }` (y, en dinámica, el elemento entero de la colección bajo `SaveTo`: `{ id, title, item }`); igual al `ExtraOption.Id` → `extra`; sin `ReplyId`, texto igual a un título → lo mismo. Otro → inválido.
  - `attachment`: `Kind` `Image`/`Document`/`Video`/`Audio` con `Attachment` → según `Accept` (`image` sólo `Image`; `document` sólo `Document`; `any` los cuatro) → guarda `{ messageId, fileName, mimeType, stored }` en `SaveTo` y sale por `received`; `url` la completa el procesador (tarea 6). Otro kind → inválido.
  - Inválido: `Attempts + 1`; si `Attempts < maxAttempts` del nodo (`OpenQuestionNode.MaxAttempts ?? settings.MaxAttempts`; los otros nodos usan `settings.MaxAttempts`): con `Fallback == "repeat"` (botones y lista) o siempre en pregunta y adjunto, reenvía el mensaje del nodo y sigue `Waiting`. Al agotar: salida `invalid`/`other` si está conectada; si no, reinicia como con la palabra de reinicio (D-M7).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public void ADigitsAnswerIsSavedAndRoutesAnswered()                  // "  10203 " → variables.cedula == "10203"
[Fact] public void ANumberAnswerIsSavedAsNumber()                           // "1234,5" → JsonValue 1234.5
[Fact] public void AKeywordsAnswerMatchesWithoutAccents()                   // keywords ["acepto"], texto "ACEPTÓ" → answered
[Fact] public void AnAudioToAnOpenQuestionIsInvalidAndRepeats()             // Kind Audio → efectos [SendText(mismo texto), WaitForCustomer], Attempts 1
[Fact] public void ThreeInvalidAnswersTakeTheInvalidOutputWhenConnected()   // tercer inválido → salida invalid → siguiente nodo
[Fact] public void ThreeInvalidAnswersRestartWhenNotConnected()             // invalid null → vuelve al primer nodo, Attempts 0, variables limpias salvo fijas, Trace tiene "restart"
[Fact] public void AButtonReplyIdRoutesItsOutput()                          // ReplyId "tech" → output tech; motivo == {id:"tech", title:"Servicio técnico"}
[Fact] public void ATypedTitleMatchesWithoutAccentsOrCase()                 // sin ReplyId, texto "servicio tecnico" → tech
[Fact] public void AnUnknownButtonReplyIsInvalid()
[Fact] public void ADynamicListBuildsRowsFromTheCollection()                // equipos [{id:1,referenceCode:"CAL-1",address:"Cra 5"}] → SendInteractive(List) con una fila + extra "Otra dirección"
[Fact] public void ADynamicListWithElevenItemsTakesTooManyWithoutSending()  // efectos sin SendInteractive; siguiente nodo por tooMany
[Fact] public void AnEmptyCollectionWithoutExtraTakesTooMany()
[Fact] public void AListChoiceSavesTheItem()                                // chosen; equipo == {id:"1", title:"CAL-1", item:{...}}
[Fact] public void TheExtraRowRoutesExtra()
[Fact] public void AnImageToAnAttachmentAcceptingDocumentsIsInvalid()
[Fact] public void AnyAttachmentIsSavedWithItsMetadata()                    // factura == {messageId, fileName:"f.pdf", mimeType:"application/pdf", stored:false}
[Fact] public void TheRestartKeywordRestartsFromTheFirstNode()              // "Reiniciar" en cualquier espera
[Fact] public void AnUnsupportedKindCountsAsAnAttempt()                     // Kind Unsupported (sticker) → Attempts 1
[Fact] public void APerNodeMaxAttemptsOverridesTheChatbotOne()              // OpenQuestion MaxAttempts 1 → al primer inválido ya toma invalid
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowEngineWaitingTests"`. Expected: FAIL.
- [ ] **Step 3: Implementar `CustomerReply` y el manejo de `CustomerMessage` en `Step`.** El reenvío de un nodo de espera reutiliza el mismo código que lo envía al entrar (una sola función `Enter(node)`).
- [ ] **Step 4: Correr y verificar que pasan** — filtros `FlowEngineTests|FlowEngineWaitingTests`: PASS (32).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): bloques de espera, intentos, reinicio y comparación sin tildes en el motor (spec 2026-10-21 D-M7, D-I5, D-C7)"`

### Task 4: Corridas: entidad, repositorio y bloqueo por conversación

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs` (si la tarea 9 del plan 1 dejó un `ConversationRunRecord` interno, se reemplaza por la entidad de Domain con el **mismo** mapeo de columnas; `variables` y `trace` jsonb por `ValueConverter` a `string`)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IConversationRunRepository`, `IAutomationsUnitOfWork.BeginTransactionAsync`, `LockConversationAsync`), `AutomationsAuditActions` (corridas)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsRepositories.cs`, `AutomationsUnitOfWork.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ConversationRunTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunsPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class ConversationRun
  {
      public static ConversationRun Start(Guid tenantId, Guid conversationId, Guid connectionId, Guid chatbotId, Guid chatbotVersionId, RunState initial, DateTimeOffset now);
      public void Apply(RunState state, DateTimeOffset now);                       // copia Status, CurrentNodeId, StepNumber, Attempts, Variables (json), Trace (json); si el estado es terminal, EndedAt = now
      public void Finish(RunStatus status, string? failureCode, DateTimeOffset now);  // para Interrupted, WindowClosed, Failed, Abandoned
      public RunState ToState();
      public bool IsLive => Status is RunStatus.Running or RunStatus.Waiting;
      // Id, TenantId, ConversationId, ConnectionId, ChatbotId, ChatbotVersionId, Status, CurrentNodeId, StepNumber, Attempts, VariablesJson, TraceJson, StartedAt, UpdatedAt, EndedAt, FailureCode
  }
  public interface IConversationRunRepository
  {
      Task<ConversationRun?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken ct);   // SELECT … FOR UPDATE dentro de la transacción abierta
      Task<ConversationRun?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task AddAsync(ConversationRun run, CancellationToken ct);
      Task<IReadOnlyList<ConversationRun>> ListAsync(Guid tenantId, Guid chatbotId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);   // por started_at DESC, tope 500
      Task<IReadOnlyList<ConversationRun>> ClaimAbandonedAsync(DateTimeOffset waitingBefore, int limit, CancellationToken ct);   // Waiting con updated_at < waitingBefore, FOR UPDATE SKIP LOCKED
  }
  public interface IAutomationsUnitOfWork
  {
      Task SaveChangesAsync(CancellationToken ct);                                   // (plan 1)
      Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);            // commit en SaveChangesAsync; rollback al Dispose sin commit
      Task LockConversationAsync(Guid conversationId, CancellationToken ct);          // SELECT pg_advisory_xact_lock(hashtext(conversationId::text)) en la transacción abierta (precedente TenancyUnitOfWork.cs:45)
  }
  // AutomationsAuditActions += RunStarted = "automations.run.started", RunHandedOff, RunResolved, RunCompleted, RunFailed, RunAbandoned = "automations.run.abandoned"; ResourceRun = "conversation_run"
  ```
- `RunAbandoned` no está en la lista de §4.6; se agrega porque el barrido de D-M13 termina corridas sin una persona y la auditoría es la única huella.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// unit
[Fact] public void StartIsRunningWithTheInitialVariables()
[Fact] public void ApplyATerminalStateSetsEndedAt()
[Fact] public void ToStateRoundTripsVariablesAndTrace()
// integración (harness del plan 1)
[Fact] public async Task FindLiveForUpdateBlocksASecondTransaction()         // dos scopes: el segundo FindLiveForUpdate espera hasta que el primero commitea (Task.WhenAny con 500 ms)
[Fact] public async Task LockConversationSerializesWithoutARun()             // lo mismo con LockConversationAsync sobre una conversación sin corrida
[Fact] public async Task ClaimAbandonedTakesOnlyWaitingOlderThanTheCutoff()  // 3 runs: Waiting hace 25 h, Waiting hace 1 h, Running hace 25 h → 1
[Fact] public async Task ASecondLiveRunIsRejectedByTheIndex()                // ya en AutomationsSchemaTests del plan 1; acá por el repositorio → ResourceConflictException("automations.run.already_live")
```

- [ ] **Step 2: Correr y verificar que fallan** — filtros `ConversationRunTests` y `RunsPersistenceTests`: FAIL.
- [ ] **Step 3: Implementar entidad, mapeo, repositorio (SQL crudo para `FOR UPDATE` y `SKIP LOCKED`, como `MediaCopyWorker.cs:69-81`), transacción y lock.** La UoW traduce `IX_conversation_runs_live` → `ResourceConflictException("automations.run.already_live")`.
- [ ] **Step 4: Correr y verificar que pasan** — PASS (7). `dotnet build Backend.slnx`: OK.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): entidad ConversationRun, repositorio con FOR UPDATE y lock por conversación (spec 2026-10-21 §5.2, D-M12)"`

### Task 5: Puertos del motor y adaptadores en Bootstrapper

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IAutomationInboundQueue`, `IAutomationConversations`, `IAutomationMessenger`, `IAutomationMedia`, `IAutomationTenantNames`; `IAutomationBusinessHours` gana `IsOpenAsync`)
- Create: `src/Bootstrapper/AutomationInboundQueue.cs` (sobre `IAutomationInbox` de Messaging), `AutomationConversations.cs` (sobre `IConversationRepository`, `IConversationEvents`, `IMessagingAuditRecorder.RecordBySystem`, `IMessagingUnitOfWork`, `IMessagingNotifier`), `AutomationMessenger.cs` (sobre `IBotOutbound`; mapea `InteractiveMessage` → `InteractiveSend`), `AutomationMedia.cs` (sobre `IMediaReads` + `IMessagingMediaStore`), `AutomationTenantNames.cs` (sobre `ITenantDirectory.GetDisplayNameAsync`, como `MessagingTenantNames.cs`)
- Modify: `src/Bootstrapper/AutomationBusinessHours.cs` (plan 1; `IsOpenAsync` = `IBusinessHours.GetStatusAsync(...).IsOpen`), `QepServiceCollectionExtensions.cs` (registros scoped)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs` (ampliar), `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationConversationsAdapterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record InboundQueueItem(Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts);
  public sealed record InboundAttachment(string MimeType, string? FileName, bool Stored);
  public sealed record InboundMessage(Guid MessageId, string Kind, string? Text, string? ReplyId, InboundAttachment? Attachment, DateTimeOffset OccurredAt);   // Kind = nombre del MessageKind de Messaging
  public interface IAutomationInboundQueue { Task<IReadOnlyList<InboundQueueItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken ct); Task CompleteAsync(Guid messageId, string outcome, CancellationToken ct); Task RetryLaterAsync(Guid messageId, TimeSpan delay, CancellationToken ct); Task<InboundMessage?> ReadAsync(Guid tenantId, Guid messageId, CancellationToken ct); }
  public sealed record AutomationConversationState(string Status, Guid? AssignedMemberId, string AutomationStatus, Guid? AutomationChatbotId, Guid ConnectionId, DateTimeOffset? LastInboundAt, string? CustomerName, string? Phone);   // Status: Open | Resolved; AutomationStatus: None | Active | Paused
  public interface IAutomationConversations
  {
      Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken ct);   // Conversation.StartAutomation(chatbotId, now) + SetAutomationStep + guardar + notify; concurrency.conflict → RequestConcurrencyException
      Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken ct);                      // UPDATE directo de automation_step (sin version) + ConversationChangedAsync
      Task HandoffAsync(Guid tenantId, Guid conversationId, CancellationToken ct);      // PauseAutomation(null, Handoff) + evento + RecordBySystem(automation_paused) + SaveChanges + ConversationChangedAsync + InboundMessageAsync(tenant, conv, lastInboundMessageId, assignedMemberId: null)
      Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken ct);   // Resolve(now, reasonId) + evento Resolved con actor nulo + RecordBySystem(resolved) + notify
      Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken ct);         // EndAutomation(now) + guardar + notify; si ya está None, no hace nada
  }
  public abstract record BotSendOutcome { public sealed record Sent(Guid MessageId) : BotSendOutcome; public sealed record NotAutomated : BotSendOutcome; public sealed record WindowClosed : BotSendOutcome; public sealed record Rejected(string Code) : BotSendOutcome; }
  public interface IAutomationMessenger { Task<BotSendOutcome> SendTextAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, string text, CancellationToken ct); Task<BotSendOutcome> SendInteractiveAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, InteractiveMessage message, CancellationToken ct); }
  public sealed record StoredAttachment(string Key, string? FileName, string MimeType, long? SizeBytes);
  public interface IAutomationMedia { Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken ct); Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct); Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken ct); }
  public interface IAutomationTenantNames { Task<string?> FindAsync(Guid tenantId, CancellationToken ct); }
  // IAutomationBusinessHours += Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken ct);
  ```
- `HandoffAsync` notifica con `InboundMessageAsync(..., assignedMemberId: null)` para que suene a todo el tenant como un entrante sin asignar (D-M5); el `messageId` es el último entrante (`Conversation.LastInboundMessageId` si existe; si no, `ConversationChangedAsync` solamente). `ConnectionUnavailable` de `IBotOutbound` se mapea a `Rejected("messaging.connection.unavailable")`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// Bootstrapper.UnitTests (dobles)
[Fact] public async Task TheMessengerMapsButtonsListAndCtaUrl()             // InteractiveMessage.Buttons → ButtonsSend con los mismos ids; List → ListSend de una sección; CtaUrl → CtaUrlSend con Uri
[Fact] public async Task TheMessengerMapsEveryBotSendResult()               // Sent/NotAutomated/WindowClosed/ConnectionUnavailable/Rejected
[Fact] public async Task TheQueueMapsItemsAndInboundKinds()                 // AutomationInboxItem → InboundQueueItem; MessageKind.Interactive → "Interactive"
// AutomationConversationsAdapterTests (harness de Automations con conexión y conversación sembradas por SQL, como Messaging)
[Fact] public async Task StartWritesActiveChatbotAndStep()                  // automation_status Active, automation_chatbot_id, automation_step
[Fact] public async Task StartOnAnAssignedConversationThrowsNotAvailable()  // MessagingDomainException → se propaga como DomainException 422 (el procesador la trata como skipped)
[Fact] public async Task HandoffPausesWithASystemActorAndAnEvent()          // automation_status Paused, pause_reason Handoff, paused_by NULL; conversation_events tiene AutomationPaused; audit.entries actor_type System
[Fact] public async Task ResolveWritesResolvedBySystem()                    // status Resolved, automation None, evento Resolved actor NULL
[Fact] public async Task EndLeavesNoneAndIsIdempotent()                     // dos EndAsync seguidos no fallan
[Fact] public async Task GetReadsTheStateTheEngineNeeds()                   // CustomerName = profile_name, Phone = wa_id
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Bootstrapper --filter "FullyQualifiedName~AutomationAdaptersTests"` y `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationConversationsAdapterTests"`. Expected: FAIL por compilación.
- [ ] **Step 3: Declarar los puertos, implementar los adaptadores y registrarlos.** `AutomationConversations` escribe eventos con `IConversationEvents.Record(tenantId, connectionId, conversationId, change, now)` como `ConversationAssignmentSteps.CommitAsync` (`ConversationAssignment.cs:49-67`), pero con `RecordBySystem` en vez de `Record` con `SubjectId`. Los eventos `Resolved` con actor nulo: verificar en `ConversationEventJson.cs` que `Actor` sea opcional.
- [ ] **Step 4: Correr** — Bootstrapper.UnitTests, Automations.IntegrationTests con el filtro, ArchitectureTests: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): puertos del motor y adaptadores sobre Mensajería y Tenancy (spec 2026-10-21 §5.3, D-M5, D-M14)"`

### Task 6: `RunProcessor` y `AutomationInboxWorker`: el bot atiende

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs`, `Engine/TriggerEvaluator.cs`, `Engine/DeterministicClientId.cs`, `Engine/FixedVariables.cs`
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Engine/AutomationInboxWorker.cs`, `Options/AutomationsWorkerOptions.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Options/MessagingAutomationOptions.cs` (`Enqueue` default `true`: el motor existe), `src/Api/appsettings.json` (`Messaging:Automation:Enqueue: true`, `Automations:Workers` con defaults explícitos), `AutomationsInfrastructureExtensions.cs` (opciones + `AddHostedService<AutomationInboxWorker>()`), `QepServiceCollectionExtensions.cs` (`RunProcessor` scoped)
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsApiHarness.cs` (copiar `FakeMetaGraphHandler.cs` y su registro de `MessagingApiHarness.QepApiFactory`; `PostWebhookAsync`, `DrainDeliveriesAsync`, `DrainMediaAsync` copiados; nuevo `DrainAutomationsAsync(host)` = `AutomationInboxWorker.DrainAsync`; `PublishChatbotAsync(client, tenant, flowJson, connectionId)` = crear + draft + publish por la API; `SeedCustomerConversationAsync` no hace falta: el webhook la crea)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/TriggerEvaluatorTests.cs`, `DeterministicClientIdTests.cs`; `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class TriggerEvaluator { public static bool Applies(ChatbotTrigger trigger, IReadOnlyList<string> keywords, bool opened, string? text, bool businessHoursOpen); }   // NewConversation: opened; Keyword: TextMatch.IsAnyOf(text, keywords); OutsideHours: opened && !businessHoursOpen
  public static class DeterministicClientId { public static Guid For(Guid runId, string nodeId, int index, int attempts); }   // primeros 16 bytes de SHA-256 de "{runId}:{nodeId}:{index}:{attempts}"
  public static class FixedVariables { public static RunVariables Build(string? customerName, string? phone, string? tenantName, string connectionName); }   // nombre, telefono, empresa, conexion ("" si null)
  public sealed class RunProcessor(…) { public Task<string> ProcessAsync(InboundQueueItem item, CancellationToken ct); }   // devuelve el outcome que se escribe en la fila: started | advanced | skipped | interrupted | ended | failed
  public sealed class AutomationsWorkerOptions { public const string Section = "Automations:Workers"; public int InboxPollSeconds { get; set; } = 1; public int InboxBatchSize { get; set; } = 20; public int AbandonSweepMinutes { get; set; } = 10; public int HttpCallRetentionDays { get; set; } = 30; public int SimulationSweepMinutes { get; set; } = 10; }
  ```
- `ProcessAsync`, en una transacción de `IAutomationsUnitOfWork` (§5.2, en este orden):
  1. `FindLiveForUpdateAsync`; si no hay, `LockConversationAsync`.
  2. `IAutomationConversations.GetAsync`; `null` → `skipped`. **D-M15:** con corrida viva y (`AssignedMemberId != null` o `AutomationStatus` ∈ {`Paused`} o `Status == Resolved` o `AutomationStatus == None`) → `run.Finish(Interrupted)`, guardar, y seguir sin corrida. Si `Status != Open` o `AssignedMemberId != null` → `skipped`.
  3. Sin corrida y `None`: `IChatbotRepository.FindPublishedByConnectionAsync(item.TenantId, item.ConnectionId)`; `null` o `Status != Active` → `skipped`. Lee el entrante (`ReadAsync`) para el texto; `TriggerEvaluator.Applies(...)` (`OutsideHours` pide `IsOpenAsync`); no aplica → `skipped`. Aplica: `StartAsync(... firstStepTitle: null)`, `ConversationRun.Start(..., chatbot.PublishedVersionId, RunState.Initial(FixedVariables), now)`, `AddAsync`, auditoría `RunStarted` (bySystem) y entra con `EngineInput.Start` (D-M17). `RequestConcurrencyException` en `StartAsync` → `RetryLaterAsync(2 s)` y outcome `retry` (no se escribe `Complete`).
     Sin corrida y `Active` (devuelta por un humano): mismo arranque sin `TriggerEvaluator` ni `StartAsync`; si el chatbot de la conexión ya no está `Active` → `EndAsync` y `skipped`.
  4. Con corrida `Waiting`: `ReadAsync` → `EngineInput.CustomerMessage(kind, text, replyId, attachment)`. Con corrida `Running` (la fila se reintentó y la unidad anterior murió a mitad de un efecto externo): no se entra con un input; se vuelve a ejecutar el efecto pendiente del nodo actual (`CallHttp` si `CurrentNodeId` es un `http`, `CheckBusinessHours` si es `businessHours`) y se sigue el bucle del paso 5 con su resultado.
  5. Bucle de efectos: `SendText`/`SendInteractive` → `IAutomationMessenger` con `clientId = DeterministicClientId.For(run.Id, nodeId, índice, item.Attempts)`; `Sent` sigue; `NotAutomated` → `Finish(Interrupted)` y corta; `WindowClosed` → `Finish(WindowClosed)` + `EndAsync` y corta; `Rejected(code)` → `Finish(Failed, code)` + `HandoffAsync` + auditoría `RunFailed` y corta. `WaitForCustomer` → `SetStepAsync(stepTitle)` y corta. `CheckBusinessHours` → `IsOpenAsync` y reentra con `EngineInput.BusinessHours`. `CallHttp` → **tarea 12** (hasta entonces reentra con `HttpResult(ok: false, ErrorCode: "http.not_available")`). `Handoff` → `HandoffAsync` + `RunHandedOff`. `Resolve` → `ResolveAsync(reasonId)` + `RunResolved`. `End` → `EndAsync` + `RunCompleted`. Al salir de un `attachment` con `received`, si `Attachment.Stored`, completa `{saveTo}.url` con `CreateDownloadUrlAsync(key, 15 min)` (D-M8).
  6. `run.Apply(state)`, `SaveChangesAsync` (commitea la transacción) y `CompleteAsync(messageId, outcome)`. Una excepción no prevista: la transacción se deshace, se registra, y la fila queda para su reintento por lease (a los 5 intentos el inbox la marca `failed`, plan 1).
- `AutomationInboxWorker`: `BackgroundService` con `PeriodicTimer(InboxPollSeconds)`, `DrainAsync` interno (reclama `InboxBatchSize` filas en un scope y procesa cada una en el suyo, como `MediaCopyWorker.cs:52-99`); los barridos (abandono, `http_calls`, simulaciones) se agregan en las tareas 7, 12 y 13.

- [ ] **Step 1: Escribir las pruebas que fallan** (E2E: tenant con `messaging` + `automations`, `SeedWhatsAppConnectionAsync`, chatbot publicado con el flujo «bienvenida → botones (Servicio técnico / Mantenimiento) → técnico: pregunta abierta `digits` → resolver; mantenimiento: handoff»; `FakeMetaGraphHandler` responde `wamid.N` a cada POST)

```csharp
// unit
[Fact] public void NewConversationAppliesOnlyWhenOpened()
[Fact] public void KeywordAppliesOnAnyInboundWithoutAccents()
[Fact] public void OutsideHoursNeedsOpenedAndClosedHours()
[Fact] public void ClientIdIsStableForTheSameTuple()                    // dos llamadas iguales → mismo Guid; attempts distinto → otro
// EngineEndToEndTests
[Fact] public async Task AFirstInboundStartsTheRunAndSendsUntilTheButtons()         // webhook "Hola" → DrainDeliveries → DrainAutomations: 2 POST a Meta (texto + interactive), messages.sent_by_kind 2, conversations.automation_status Active, automation_step "¿Qué necesitas?", runs.status Waiting, inbox.outcome "started", audit automations.run.started
[Fact] public async Task TheTriggerInboundIsNotAnAnswer()                           // tras el arranque, ningún intento consumido: runs.attempts 0 (D-M17)
[Fact] public async Task AButtonReplyAdvancesAndADigitsAnswerResolves()             // webhook interactive button_reply "tech" → pregunta; webhook "10203" → conversación Resolved, run Resolved, variables.cedula "10203", audit run.resolved
[Fact] public async Task MaintenanceHandsOffAndNotifiesTheTenant()                  // button "maint" → automation Paused/Handoff, assigned NULL, run HandedOff, evento AutomationPaused, notifier recibió inboundMessage sin asignado (MessagingEventSink doble o tabla de notify)
[Fact] public async Task AnAssignedConversationIsSkipped()                           // webhook (encola) y Take por API ANTES de DrainAutomations → outcome "skipped", sin POST (si el Take fuera antes del webhook no se encolaría: plan 1)
[Fact] public async Task AnAdvisorTakingBetweenClaimAndSendInterruptsTheRun()        // FakeMeta.RespondWith que, antes de responder al primer POST, toma la conversación por SQL (assigned_member_id) → IBotOutbound NotAutomated → run Interrupted, sin Failed, automation_status no es Paused por Handoff
[Fact] public async Task AHumanResumeStartsANewRunWithoutTheTrigger()                // chatbot con trigger Keyword "hola"; conversación Active sin corrida (ActivateAutomationAsync) → webhook "cualquier cosa" → arranca desde el primer nodo
[Fact] public async Task ARetriedRowDoesNotDuplicateTheMessages()                    // FakeMeta.Throw en el segundo POST → la fila queda para reintento; segundo DrainAutomations con FakeMeta sano → el primer mensaje no se reenvía (clientId repetido → AlreadySent), total 2 mensajes del bot
[Fact] public async Task WithTheFlagOffNothingHappens()                              // Messaging:Automation:Enqueue=false por UseSetting → inbox vacío
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~TriggerEvaluatorTests|FullyQualifiedName~DeterministicClientIdTests"` y `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~EngineEndToEndTests"`. Expected: FAIL.
- [ ] **Step 3: Implementar `RunProcessor`, el worker, las opciones, el flag y el harness.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (14). `dotnet test tests/Modules/Messaging --filter "FullyQualifiedName~AutomationInboxTests"`: PASS con el default nuevo (la prueba `WithTheFlagOffNothingIsEnqueued` del plan 1 fija el setting en `false` explícito; si confiaba en el default, ajustarla).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): RunProcessor y AutomationInboxWorker; el chatbot publicado atiende (spec 2026-10-21 §5.2, D-M3, D-M4, D-M15, D-M17)"`

### Task 7: Abandono, ventana cerrada, orden y reinicio de extremo a extremo

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs` (`AbandonAsync(now)`: `ClaimAbandonedAsync(now - 24 h, 100)` → `Finish(Abandoned)` + `EndAsync` + auditoría `RunAbandoned`, cada corrida en su transacción), `Infrastructure/Engine/AutomationInboxWorker.cs` (barrido cada `AbandonSweepMinutes`; `SweepAsync` interno para pruebas)
- Test: `EngineEndToEndTests.cs` (ampliar)

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact] public async Task TwoInboundsAreProcessedInOrderOnePerPass()                 // dos webhooks seguidos ("tech" y "10203") → primer Drain procesa el botón; segundo Drain la cédula; nunca al revés
[Fact] public async Task AClosedWindowEndsWindowClosedAndReturnsToNone()            // last_inbound_at = now − 25 h por SQL antes de drenar una respuesta → run WindowClosed, automation None, sin Handoff
[Fact] public async Task TheRestartKeywordRestartsTheRun()                          // en la pregunta, webhook "Reiniciar" → vuelve a mandar la bienvenida, variables sin cedula
[Fact] public async Task ThreeInvalidAnswersRestart()                               // "abc" ×3 con invalid sin conectar → bienvenida otra vez; attempts 0
[Fact] public async Task AWaitingRunOlderThanADayIsAbandoned()                      // UPDATE runs updated_at = now − 25 h; SweepAsync → Abandoned, automation None, audit run.abandoned; el siguiente webhook arranca una corrida nueva
[Fact] public async Task AResolvedConversationReopenedByTheCustomerStartsAgain()     // tras resolver, webhook nuevo → opened true (D-M16) → corrida nueva
[Fact] public async Task AnUnpublishedChatbotOnAnActiveConversationEndsIt()         // pausar el chatbot por API con la conversación Active sin corrida → webhook → automation None, outcome skipped
[Fact] public async Task AGraphRejectionFailsTheRunAndHandsOff()                    // FakeMeta 400 code 131026 → run Failed failure_code messaging.message.rejected, automation Paused/Handoff, audit run.failed
```

- [ ] **Step 2: Correr y verificar que fallan** — filtro `EngineEndToEndTests`: FAIL (las nuevas).
- [ ] **Step 3: Implementar el barrido y lo que falte en el procesador.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (22).
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): abandono a las 24 h, ventana cerrada y orden por conversación en el motor (spec 2026-10-21 D-M9, D-M13, D-M16)"`

### Task 8: Lecturas: «Datos capturados» para el asesor y `GET /runs`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IConversationAutomationDetails.cs`, `GetConversationAutomation.cs` (`GetConversationAutomationQuery(Guid TenantId, Guid ConversationId)` → `ConversationAutomationDetailsDto`, permiso `messaging.conversation.read`, 404 `messaging.automation.not_available`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:32` (`GET /conversations/{conversationId:guid}/automation`), `MessagingErrorCodes.cs` (`AutomationNotAvailable` ya existe; reutilizar), `QepServiceCollectionExtensions.cs`
- Create: `src/Bootstrapper/ConversationAutomationDetails.cs` (sobre `IConversationRunRepository.FindLatestAsync` + `AutomationsDbContext.Chatbots`/`ChatbotVersions` para nombre y número)
- Create: `src/Modules/Automations/Modules.Automations.Application/Runs.cs` (`ListRunsQuery(Guid TenantId, Guid ChatbotId, DateTimeOffset? From, DateTimeOffset? To)` → `IReadOnlyList<RunDto>`, permiso `automations.chatbot.read`; default `From` = hoy − 7 días, `To` = ahora, tope 500), `Modules.Automations.Api/RunEndpoints.cs` (`GET /runs?chatbotId=&from=&to=`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationAutomationDetailsApiTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/RunsApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ConversationAutomationDetails(Guid ChatbotId, string ChatbotName, int VersionNumber, string Status, string? StepTitle, JsonElement Variables, IReadOnlyList<ConversationAutomationTraceEntry> Trace, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
  public sealed record ConversationAutomationTraceEntry(DateTimeOffset At, string NodeId, string Kind, string Outcome);
  public interface IConversationAutomationDetails { Task<ConversationAutomationDetails?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken ct); }
  public sealed record ConversationAutomationDetailsDto(Guid ChatbotId, string ChatbotName, int VersionNumber, string Status, string? StepTitle, JsonElement Variables, IReadOnlyList<ConversationAutomationTraceEntry> Trace, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
  public sealed record RunDto(Guid Id, Guid ConversationId, string Status, string? CurrentNodeId, int StepNumber, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int? DurationSeconds, string? FailureCode);
  ```

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// Messaging (harness de Messaging + filas en automations.* por SQL: el host tiene los dos esquemas)
[Fact] public async Task TheDetailsCarryNameVersionVariablesAndTrace()   // run Waiting con variables {cedula:"1"} y trace de 2 → 200 con chatbotName real, versionNumber 1
[Fact] public async Task AConversationWithoutRunsIs404NotAvailable()
[Fact] public async Task ReadPermissionIsEnough()                         // miembro con messaging.conversation.read sin automations.* → 200
[Fact] public async Task AnotherTenantIs403()
// Automations
[Fact] public async Task ListsTheRunsOfAChatbotInTheRange()               // 3 runs (2 en rango, 1 fuera) → 2, por startedAt DESC, durationSeconds calculado
[Fact] public async Task TheDefaultRangeIsTheLastSevenDays()
```

- [ ] **Step 2: Correr y verificar que fallan** — filtros `ConversationAutomationDetailsApiTests` y `RunsApiTests`: FAIL (404 de ruta).
- [ ] **Step 3: Implementar puerto, handler, adaptador, query y rutas.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (6). ArchitectureTests `CompositionRootTests`: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(messaging,automations): datos capturados de la corrida para el asesor y GET /runs (spec 2026-10-21 §5.5, §4.4)"`

## Fase 5: proveedor «API externa» y bloque HTTP

### Task 9: Proveedor `external-api` en el catálogo de Integrations

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs:6-14` (`IntegrationCategory.Automation`), `IntegrationProviders.cs` (`ExternalApiFieldKeys { BaseUrl = "baseUrl", AuthHeader = "authHeader", ApiKey = "apiKey", TestPath = "testPath" }`; `ExternalApi` con `Key "external-api"`, `DisplayName "API externa"`, `IntegrationCategory.Automation`, `[TenantModuleKeys.Automations]`, campos: `baseUrl` `FieldKind.Url` required ≤ 300 patrón `^https://[^\s/?#]+(/[^\s?#]*)?$` mensaje «Escribe la dirección completa con https://, sin query.»; `authHeader` `Text` opcional ≤ 64 patrón `^[A-Za-z0-9-]{1,64}$`; `apiKey` `Secret` required ≤ 512 patrón `^[\x21-\x7E]{1,512}$`; `testPath` `Text` opcional ≤ 200 patrón `^/[^\s?#]*$`; `maxConnections: 10`, `ProviderOnboarding.Form`, `requiresTenantEnablement: false`; `All` lo agrega al final)
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/Migrations/<ts>_AddExternalApiProvider.cs` (copiar `20261018123600_AddShopifyProvider.cs`: recrea `CK_connections_provider_key` con `ProviderKeyCheck()`)
- Modify: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs` (listas literales de proveedores), `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConsumerPortsApiTests.cs`, `ConnectionsApiTests.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsApiHarness.cs` (`SeedExternalApiConnectionAsync` real: `POST /integrations/connections` con `providerKey external-api`, `fields { baseUrl, authHeader }`, `secrets { apiKey }`; quitar el `Skip` de la prueba de `externalApis` del plan 1, tarea 16)

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// unit
[Fact] public void ExternalApiIsAFormProviderConsumedByAutomations()      // Category Automation, ConsumingModules [automations], MaxConnections 10, PublicFields [baseUrl, authHeader, testPath], SecretFields [apiKey]
[Theory] [InlineData("https://api-clasic.qcode.co", true)] [InlineData("http://api.x.co", false)] [InlineData("https://api.x.co/v1?x=1", false)] public void BaseUrlPatternRequiresHttpsWithoutQuery(string value, bool valid)
// api
[Fact] public async Task CreatesAnExternalApiConnectionAndTheCatalogShowsItForAutomations()   // tenant con automations → catálogo trae external-api; POST 201; GET no devuelve apiKey
[Fact] public async Task AnHttpBaseUrlIs422OnTheField()                                       // errors["fields.baseUrl"]
[Fact] public async Task ResolveAsyncCarriesBaseUrlAndTheKey()                                // ConsumerPortsApiTests: Fields["baseUrl"], Secrets["apiKey"]
[Fact] public async Task WithoutTheAutomationsModuleTheProviderIsHidden()
[Fact] public async Task TheEditorListsTheExternalApis()   // Automations: la del plan 1, ya sin Skip
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Integrations --filter "FullyQualifiedName~IntegrationCatalogTests|FullyQualifiedName~ConsumerPortsApiTests|FullyQualifiedName~ConnectionsApiTests"`. Expected: FAIL.
- [ ] **Step 3: Implementar el proveedor y generar la migración** — Run: `dotnet ef migrations add AddExternalApiProvider --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext -o Persistence/Migrations`. El `Up` sólo recrea el CHECK.
- [ ] **Step 4: Correr Integrations completo y Automations con filtro `ExternalApi|Connections`** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(integrations): proveedor external-api para Automatizaciones (spec 2026-10-21 D-H1)"`

### Task 10: Política anti-SSRF de la URL y probador de la conexión

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/ExternalApi/ExternalApiUrlPolicy.cs`, `ExternalApi/ExternalApiConnectionTester.cs`
- Modify: `IntegrationsInfrastructureExtensions.cs` (`AddHttpClient(ExternalApiConnectionTester.HttpClientName, ConfigureClient).ConfigurePrimaryHttpMessageHandler(() => ExternalApiUrlPolicy.CreateHandler()).RemoveAllLoggers()`; `AddSingleton<IProviderConnectionTester, ExternalApiConnectionTester>()`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ExternalApiUrlPolicyTests.cs`, `ExternalApiConnectionTesterTests.cs`; `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs` (ampliar)

**Interfaces:**
- Produces:
  ```csharp
  public static class ExternalApiUrlPolicy
  {
      public static string? Reject(Uri url);                                    // null = aceptada; "scheme_not_https" | "host_is_ip_literal" | "host_missing"
      public static bool IsForbidden(IPAddress address);                        // loopback, link-local (169.254/16, fe80::/10), privadas (10/8, 172.16/12, 192.168/16, fc00::/7), multicast, 0.0.0.0, ::, mapeadas IPv4 en IPv6 (se desenvuelven)
      public static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolve = null);   // AllowAutoRedirect false; ConnectCallback resuelve el host (Dns.GetHostAddressesAsync por defecto), rechaza con ExternalApiUrlRejectedException("resolved_to_forbidden_range") si alguna dirección es prohibida, y conecta a la primera permitida
  }
  public sealed class ExternalApiUrlRejectedException(string reason) : Exception(reason) { public string Reason { get; } = reason; }
  internal sealed class ExternalApiConnectionTester : IProviderConnectionTester { public const string HttpClientName = "integrations.external-api"; … }
  ```
- Probador (D-H3): `Reject(baseUrl)` ≠ null → `ConnectionTestResult.Invalid("baseUrl", reason)`; `GET {baseUrl}{testPath ?? "/"}` con header `{authHeader ?? "x-api-key"}: {apiKey}`, `HttpCompletionOption.ResponseHeadersRead`, sin leer el cuerpo; 2xx o 3xx → `Ok`; 401/403 → `CredentialsRejected`; otro status → `Unreachable("http_<status>")`; timeout → `Unreachable("timeout")`; `HttpRequestException` con `ExternalApiUrlRejectedException` adentro → `Invalid("baseUrl", reason)`; otra → `Unreachable("network")`. 10 s y `User-Agent: qep-integrations` como Zenvia.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// ExternalApiUrlPolicyTests
[Theory] [InlineData("http://api.x.co", "scheme_not_https")] [InlineData("https://10.0.0.5", "host_is_ip_literal")] [InlineData("https://[::1]", "host_is_ip_literal")] [InlineData("https://api.x.co", null)] public void RejectClassifiesTheUrl(string url, string? reason)
[Theory] [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("172.16.0.1")] [InlineData("192.168.1.1")] [InlineData("169.254.169.254")] [InlineData("::1")] [InlineData("fe80::1")] [InlineData("::ffff:10.0.0.1")] public void ForbiddenRanges(string ip)
[Theory] [InlineData("181.49.1.1")] [InlineData("2606:4700::1")] public void PublicAddressesAreAllowed(string ip)
[Fact] public async Task AHostResolvingToAPrivateRangeIsRejectedAtConnect()   // CreateHandler(resolve: _ => [181.49.1.1, 10.0.0.1]) + HttpClient → HttpRequestException cuyo InnerException es ExternalApiUrlRejectedException("resolved_to_forbidden_range")
// ExternalApiConnectionTesterTests (handler stub en el IHttpClientFactory doble, como ZenviaConnectionTesterTests)
[Fact] public async Task A200IsOkAndSendsTheHeader()                  // header "x-api-key" por defecto; GET a baseUrl + "/"
[Fact] public async Task A302IsOkWithoutFollowing()
[Fact] public async Task A401IsCredentialsRejected()
[Fact] public async Task A500IsUnreachableWithTheStatus()             // Reason "http_500"
[Fact] public async Task ACustomHeaderAndTestPathAreUsed()            // authHeader "X-Token", testPath "/health" → GET baseUrl/health con header X-Token
[Fact] public async Task AnHttpBaseUrlIsInvalidOnTheField()
// ConnectionSecretLeakTests
[Fact] public async Task AnExternalApiKeyThatTheProviderEchoesNeverLeaks()   // 401 con cuerpo que repite la clave → POST connection 422 credentials_rejected; AssertNothingLeaksAsync(clave)
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Integrations --filter "FullyQualifiedName~ExternalApi"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar la política, el handler y el probador; registrar.**
- [ ] **Step 4: Correr Integrations completo** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(integrations): política anti-SSRF y probador de la conexión API externa (spec 2026-10-21 D-H2, D-H3)"`

### Task 11: `HttpBlockExecutor` y registro en `http_calls`

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Http/HttpBlockRequest.cs` (puro), `Http/IHttpBlockExecutor.cs`
- Modify: `AutomationsPorts.cs` (`IAutomationExternalApis.ResolveAsync`; nuevo `IExternalApiUrlPolicy`)
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs`, `Http/HttpCallLog.cs` (INSERT en `automations.http_calls` por `AutomationsDbContext`, commit propio: el registro sobrevive al rollback de la unidad)
- Create: `src/Bootstrapper/AutomationExternalApiUrlPolicy.cs` (sobre `ExternalApiUrlPolicy.Reject`), modificar `AutomationExternalApis.cs` (`ResolveAsync` sobre `IIntegrationConnections.ResolveAsync` filtrando `ProviderKey == "external-api"`), `QepServiceCollectionExtensions.cs` (`AddHttpClient(HttpBlockExecutor.HttpClientName, HttpBlockExecutor.ConfigureClient).ConfigurePrimaryHttpMessageHandler(() => ExternalApiUrlPolicy.CreateHandler()).RemoveAllLoggers()` — se registra en Bootstrapper porque el handler es de Integrations.Infrastructure)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/HttpBlockRequestTests.cs`, `HttpBlockExecutorTests.cs` (stub `HttpMessageHandler` + `IHttpClientFactory` doble + dobles de los puertos; `HttpCallLog` doble en memoria)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ExternalApiCredentials(Uri BaseUrl, string AuthHeader, string ApiKey) { public override string ToString() => $"ExternalApiCredentials {{ BaseUrl = {BaseUrl} }}"; }
  // IAutomationExternalApis += Task<ExternalApiCredentials?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken ct);   // null si no es external-api, no está Active o no es del tenant
  public interface IExternalApiUrlPolicy { string? Reject(Uri url); }
  public sealed record HttpBlockRequest(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Query, HttpBlockBody? Body)
  { public static HttpBlockRequest Build(HttpNode node, RunVariables variables); }   // Path y Query interpolados (valores de query con Uri.EscapeDataString al enviar); Body json: Template interpolado; multipart: Fields interpolados + (FileField, FileVariable)
  public abstract record HttpBlockBody { public sealed record Json(string Content) : HttpBlockBody; public sealed record Multipart(IReadOnlyList<KeyValuePair<string, string>> Fields, string FileField, string FileVariable) : HttpBlockBody; }
  public sealed record HttpCallContext(Guid TenantId, Guid? RunId, Guid? SimulationId, string NodeId);
  public sealed record HttpCallResult(bool Ok, int? Status, RunVariables Mapped, string Outcome, string? ErrorCode);   // Outcome: ok | error_status | timeout | network | mapping_failed | url_rejected | connection_unavailable | media_not_ready | response_too_large | body_not_json
  public interface IHttpBlockExecutor { Task<HttpCallResult> ExecuteAsync(HttpCallContext context, HttpNode node, RunVariables variables, CancellationToken ct); }
  public interface IHttpCallLog { Task RecordAsync(HttpCallEntry entry, CancellationToken ct); }
  public sealed record HttpCallEntry(Guid TenantId, Guid? RunId, Guid? SimulationId, string NodeId, Guid ConnectionId, string Method, string Path, int? StatusCode, int DurationMs, string Outcome, string? ErrorCode, DateTimeOffset CalledAt);
  ```
- Ejecutor (D-H4..D-H8): `node.ConnectionId` null o `ResolveAsync` null → `connection_unavailable` (sin fila en `http_calls`: no hay conexión que registrar); `Reject(baseUrl)` ≠ null → `url_rejected` (sí registra); URL = `BaseUrl` + `Path` (el `Path` empieza con `/`; `Path` ya interpolado se guarda en `http_calls.path` sin query, recortado a 512); header `AuthHeader: ApiKey`; `json` → `StringContent(application/json)`; `multipart` → `MultipartFormDataContent` con los campos y el archivo: `variables.Get(FileVariable)` debe tener `messageId`; `TryGetStoredAsync` null → espera hasta **20 s** reintentando cada 5 s (ver nota) y luego `media_not_ready`; `StreamContent` de `OpenReadAsync` con `ContentType = mimeType` y `fileName`; timeout = `node.TimeoutSeconds` por `CancellationTokenSource` ligado (`timeout`); `HttpRequestException` → `network` (si `InnerException is ExternalApiUrlRejectedException` → `url_rejected`); lee el cuerpo con tope 1 MB (`response_too_large` si `Content-Length` > 1 MB o el stream pasa el tope); `okWhen` (`null` = 2xx) no cumple → `error_status` con `ErrorCode = "http_<status>"`; con `Mapping` no vacío: `JsonNode.Parse` falla → `mapping_failed` (`body_not_json`); cada `From` por `JsonPath.Select` → `Mapped.Set(To, nodo clonado)` (ruta ausente → `null`, no es fallo); sin mapeo no se guarda nada (D-H7). Siempre registra en `IHttpCallLog` salvo `connection_unavailable`. Nunca loguea cuerpo, headers ni clave: los `LoggerMessage` del ejecutor llevan sólo `tenantId`, `nodeId`, `outcome`, `status` y `durationMs`.
- **Nota (desvío de D-H8):** el spec dice 60 s de espera por el adjunto; la unidad entera corre dentro de un lease de 30 s (plan 1, tarea 7), así que esperar 60 s haría que otra réplica reclamara la misma fila a mitad. Se espera 20 s (4 × 5 s): `MediaCopyWorker` corre cada 5 s y copia una imagen en menos de un segundo. Se deja escrito en el README del módulo.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// HttpBlockRequestTests
[Fact] public void InterpolatesPathQueryAndJsonBody()                   // path "/customers/{cliente.id}" → "/customers/7"; query value "{cedula}"; body template con {cliente.id}
[Fact] public void MultipartCarriesFieldsAndTheFileVariable()
// HttpBlockExecutorTests
[Fact] public async Task SendsTheKeyHeaderAndMapsTheResponse()           // stub 200 {"data":[{"id":7}]} con mapping data[0] → cliente; Ok, Mapped.cliente.id == 7; request tenía "x-api-key: K"; log outcome ok, path "/customers", status 200
[Fact] public async Task QueryValuesAreEscaped()                        // value "a b&c" → "a%20b%26c" en la URL
[Fact] public async Task ANon2xxIsErrorStatusUnlessOkWhenAllowsIt()     // 404 → error_status http_404; okWhen [200, 404] → Ok
[Fact] public async Task ATimeoutIsTimeout()                            // handler que espera 3 s; TimeoutSeconds 1 → timeout, log duration ≥ 1000
[Fact] public async Task ABodyOverOneMegabyteIsTooLarge()
[Fact] public async Task ANonJsonBodyWithAMappingIsMappingFailed()      // 200 "<html>" + mapping → Ok false, outcome mapping_failed, ErrorCode body_not_json
[Fact] public async Task AMissingMappingPathIsNullNotAFailure()
[Fact] public async Task WithoutMappingNothingIsStored()
[Fact] public async Task ARejectedUrlIsAnErrorOutcomeWithoutACall()     // IExternalApiUrlPolicy doble devuelve "scheme_not_https" → url_rejected; stub no recibió nada; log tiene la fila
[Fact] public async Task AMissingConnectionIsConnectionUnavailableWithoutALog()
[Fact] public async Task MultipartStreamsTheStoredAttachment()          // IAutomationMedia doble con bytes "PDF" → el stub recibe multipart con el campo "file" filename "f.pdf" y los bytes
[Fact] public async Task AnAttachmentNotYetStoredIsMediaNotReady()      // TryGetStoredAsync siempre null; reloj/espera inyectada (delay doble) → media_not_ready tras 4 reintentos
[Fact] public async Task NothingOfTheBodyOrTheKeyIsLogged()             // CapturedLogs (copiar UnitTestLogs.cs de Integrations): ni "K" ni "<html>" ni "x-api-key" en los logs
```

- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~HttpBlock"`. Expected: FAIL por compilación.
- [ ] **Step 3: Implementar `HttpBlockRequest`, el ejecutor (la espera por el adjunto por un `Func<TimeSpan, CancellationToken, Task>` inyectable, `Task.Delay` por defecto), el log y los adaptadores; registrar el cliente nombrado.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (16). ArchitectureTests: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): ejecutor del bloque HTTP con mapeo, multipart y registro en http_calls (spec 2026-10-21 D-H4..D-H8)"`

### Task 12: El bloque HTTP en el motor y el flujo de Clasic de extremo a extremo

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs` (efecto `CallHttp` → `IHttpBlockExecutor.ExecuteAsync(new HttpCallContext(tenant, run.Id, null, nodeId), node, state.Variables)` → `EngineInput.HttpResult(nodeId, result.Ok, result.Status, result.Mapped, result.ErrorCode)`; barrido `PurgeHttpCallsAsync(now - HttpCallRetentionDays)` en lotes de 1000), `Infrastructure/Engine/AutomationInboxWorker.cs` (el barrido junto al de abandono), `Infrastructure/Http/HttpCallLog.cs` (`PurgeBeforeAsync`)
- Create: `tests/Modules/Automations/Modules.Automations.IntegrationTests/FakeExternalApiHandler.cs` (como `FakeMetaGraphHandler`: `Respond(pathPattern, status, body)`, `Requests` con método, URI, header `x-api-key`, cuerpo o partes multipart; registrado como handler primario del cliente `automations.external-api` en el harness), `Fixtures/clasic-subset.json` (EmbeddedResource: Anexo B pasos 4, 5, 7, 9, 10 y 11 —documento → `GET /customers?identificationNumber=` → condición → `GET /invoices/customer/{id}/all-invoice-items` → lista dinámica → botones → contacto → `POST /service-orders` → resolver; `error` → handoff— con `connectionId` fijo `11111111-…` para mapear al importar; y el subflujo de cliente nuevo con `attachment` + `POST /customers/new-customer` multipart)
- Modify: `AutomationsApiHarness.cs` (`ImportChatbotAsync(client, tenant, fixture, connectionMap)` por `POST /chatbots/import`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs`, `HttpCallsRetentionTests.cs`

- [ ] **Step 1: Escribir las pruebas que fallan** (tenant con `messaging` + `automations`, conexión WhatsApp, conexión `external-api` por `SeedExternalApiConnectionAsync` con `baseUrl https://api-clasic.test` —el `FakeExternalApiHandler` reemplaza el handler, así que el DNS no se consulta—, chatbot importado con el `connectionMap` y publicado)

```csharp
[Fact] public async Task TheHappyPathCreatesAServiceOrder()                   // "Hola" → bienvenida + pregunta documento; "10203" → GET /customers?identificationNumber=10203 (fake: data[0] id 7) → GET all-invoice-items (2 equipos) → lista con 2 filas + extra; list_reply "1" → botones; "tech" → fallas (fake GET /failures) → …; "Ana" → "3001234567" → POST /service-orders (fake 201 {id: 55}) con body que contiene customerId 7 e invoiceItems[0].id "1" → resolver «Tu orden es la 55» → conversación Resolved; http_calls tiene 4+ filas con outcome ok, path sin query, run_id, sin cuerpo
[Fact] public async Task AnUnknownCustomerGoesToRegistrationAndUploadsTheInvoice()   // fake data [] → mensaje con CTA url + botones; "leave" → preguntas → webhook imagen (DrainMediaAsync antes del DrainAutomations) → POST /customers/new-customer multipart con la parte "file" y los bytes de la imagen (fake R2 del harness de Messaging) → Resolved
[Fact] public async Task AnApiErrorHandsOff()                                  // fake POST /service-orders 409 → handoff; http_calls outcome error_status, error_code http_409
[Fact] public async Task ATimeoutHandsOffWithinTheLease()                      // fake que tarda 9 s con TimeoutSeconds 8 → timeout → error → handoff; la fila del inbox quedó processed (no reclamada otra vez)
[Fact] public async Task TheKeyNeverAppearsInLogsAuditOrHttpCalls()            // CapturedLogs + SELECT de http_calls y audit.entries: sin "clasic-key-SENTINEL"
// HttpCallsRetentionTests
[Fact] public async Task CallsOlderThanThirtyDaysArePurged()                   // 2 filas: called_at −31 d y −1 d → SweepAsync → 1
```

- [ ] **Step 2: Correr y verificar que fallan** — filtros `ClasicFlowEndToEndTests|HttpCallsRetentionTests`: FAIL.
- [ ] **Step 3: Implementar el efecto en el procesador, el fake, el fixture, la purga y el harness.** El fixture se valida también en `FlowValidatorTests` del plan 1 (`TheClasicSubsetHasOnlyWarnings`): agregarla.
- [ ] **Step 4: Correr y verificar que pasan** — PASS (6). `dotnet test tests/Modules/Automations`: PASS completo.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): bloque HTTP en el motor, purga de http_calls y el flujo de Clasic de extremo a extremo (spec 2026-10-21 §7, Anexo B)"`

## Fase 6: simulador

### Task 13: Simulador sobre el borrador

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs`, `src/Modules/Automations/Modules.Automations.Application/Simulations.cs` (`SimulationRunner`, comandos, DTOs), `Modules.Automations.Api/SimulationEndpoints.cs`
- Modify: `AutomationsPorts.cs` (`ISimulationRepository`), `AutomationsDbContext.cs` (si el plan 1 dejó `SimulationRecord`, reemplazar por la entidad con el mismo mapeo), `AutomationsRepositories.cs`, `RunProcessor.cs`/`AutomationInboxWorker.cs` (barrido de simulaciones vencidas cada `SimulationSweepMinutes`), `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/SimulationRunnerTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/SimulationsApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class Simulation
  {
      public static Simulation Start(Guid tenantId, Guid chatbotId, Guid memberId, bool liveHttp, RunState state, DateTimeOffset now);   // ExpiresAt = now + 2 h
      public void Apply(RunState state, IReadOnlyList<TranscriptEntry> appended, DateTimeOffset now);   // UpdatedAt y ExpiresAt = now + 2 h (se renueva con cada mensaje)
      public bool IsExpired(DateTimeOffset now);
      // Id, TenantId, ChatbotId, MemberId, LiveHttp, StateJson, TranscriptJson, CreatedAt, UpdatedAt, ExpiresAt
  }
  public sealed record TranscriptEntry(string From, string? NodeId, string? NodeTitle, string Text, IReadOnlyList<TranscriptButton>? Buttons, TranscriptList? List, DateTimeOffset At);   // From: bot | customer | system
  public sealed record TranscriptButton(string Id, string Title);   public sealed record TranscriptList(string ButtonLabel, IReadOnlyList<TranscriptRow> Rows);   public sealed record TranscriptRow(string Id, string Title, string? Description);
  public sealed class SimulationRunner(IHttpBlockExecutor http, IAutomationBusinessHours businessHours, IClock clock)
  { public Task<(RunState State, IReadOnlyList<TranscriptEntry> Transcript)> RunAsync(FlowDefinition flow, EngineSettings settings, RunState state, EngineInput input, SimulationContext context, CancellationToken ct); }
  public sealed record SimulationContext(Guid TenantId, Guid SimulationId, Guid ConnectionId, bool LiveHttp);
  public sealed record SimulationDto(Guid Id, Guid ChatbotId, bool LiveHttp, string? CurrentNodeId, string Status, bool Ended, JsonElement Variables, IReadOnlyList<TranscriptEntry> Transcript, DateTimeOffset ExpiresAt);
  public sealed record StartSimulationRequest(bool LiveHttp);   public sealed record SimulationMessageRequest(string? Text, string? ReplyId, SimulationAttachmentRequest? Attachment);   public sealed record SimulationAttachmentRequest(string FileName, string MimeType);
  // StartSimulationCommand(TenantId, ChatbotId, LiveHttp) → 201 SimulationDto; SendSimulationMessageCommand(TenantId, SimulationId, Text, ReplyId, Attachment) → SimulationDto; DeleteSimulationCommand(TenantId, SimulationId) → 204
  public interface ISimulationRepository { Task<Simulation?> FindAsync(Guid tenantId, Guid id, CancellationToken ct); Task AddAsync(Simulation s, CancellationToken ct); void Remove(Simulation s); Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct); }
  ```
- Reglas (§8, D-S1..S3): arranca sobre el **borrador** (`chatbot.DraftFlowJson`); si `FlowValidator` da errores con el contexto real → `AutomationsDomainException(ChatbotInvalid)` con los issues (como publicar). Variables fijas: `nombre` «Cliente de prueba», `telefono` «3000000000», `empresa` el nombre del tenant, `conexion` el nombre de la primera conexión del chatbot (o «Simulador»). `SendText`/`SendInteractive` → entrada `bot` (con `Buttons` o `List`); `WaitForCustomer` corta; `CallHttp`: sin `LiveHttp` → `node.SampleResponse` (null → `HttpResult(ok: false, ErrorCode: "http.no_sample_response")` y entrada `system` «Este bloque no tiene respuesta de ejemplo»); con `SampleResponse`, aplica el mapeo con `JsonPath` y `ok` (status 200) — en Application, sin el ejecutor; con `LiveHttp` → `IHttpBlockExecutor` con `HttpCallContext(tenant, RunId: null, SimulationId, nodeId)` (un `multipart` con un adjunto ficticio → `media_not_ready`); `CheckBusinessHours` → `IsOpenAsync` real (D-S3); `Handoff`/`Resolve`/`End` → entrada `system` («Pasaría a un asesor», «Resolvería la conversación», «Terminó: salida sin conectar») y `Ended`. El mensaje del tester → entrada `customer` + `EngineInput.CustomerMessage(kind: Attachment != null ? por mime (image/* → Image; de lo contrario Document) : Text, text, replyId, attachment: new AttachmentInput(Guid.Empty, fileName, mimeType, Stored: false))`. Vencida → `ResourceNotFoundException(SimulationExpired)`; de otro miembro del tenant → se permite (la ve quien tenga `manage`). Sólo `manage`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// SimulationRunnerTests (dobles)
[Fact] public async Task StartTranscribesTheBotMessagesAndStopsAtTheWait()      // Anexo A: [bot Bienvenida, bot Número de documento], CurrentNodeId n2
[Fact] public async Task HttpUsesTheSampleResponseWithoutTheExecutor()           // n3 sampleResponse {data:[{id:7}]} → variables.cliente.id 7; el IHttpBlockExecutor doble no fue llamado
[Fact] public async Task LiveHttpCallsTheExecutorWithTheSimulationId()
[Fact] public async Task WithoutSampleResponseTheErrorOutputIsTakenWithASystemLine()
[Fact] public async Task HandoffWritesASystemLineAndEnds()
// SimulationsApiTests
[Fact] public async Task StartReturnsTheInitialTranscriptAndCurrentNode()        // 201; transcript 2 entradas bot; currentNodeId; variables.nombre "Cliente de prueba"
[Fact] public async Task StartOnADraftWithErrorsIs422Invalid()
[Fact] public async Task AReplyIdAdvancesAndAnAttachmentIsAccepted()             // POST messages {replyId:"tech"}; POST {attachment:{fileName:"f.pdf", mimeType:"application/pdf"}} → factura.fileName
[Fact] public async Task BusinessHoursReadsTheRealPort()                          // flujo businessHours; conexión con horario cerrado (sembrado como en BusinessHoursApiTests) → closed
[Fact] public async Task LiveHttpRecordsInHttpCallsWithTheSimulationId()          // FakeExternalApiHandler; http_calls.simulation_id == id, run_id NULL
[Fact] public async Task AnExpiredSimulationIs404ExpiredAndTheSweepDeletesIt()    // UPDATE expires_at = now − 1 m → 404 automations.simulation.expired; SweepAsync → fila borrada → 404 not_found
[Fact] public async Task DeleteIs204AndReadIsManageOnly()                         // miembro con read → 403
```

- [ ] **Step 2: Correr y verificar que fallan** — filtros `SimulationRunnerTests|SimulationsApiTests`: FAIL.
- [ ] **Step 3: Implementar entidad, runner, comandos, repositorio, rutas y barrido.**
- [ ] **Step 4: Correr y verificar que pasan** — PASS (12). `dotnet test tests/Modules/Automations` y ArchitectureTests: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(automations): simulador del borrador con respuestas de ejemplo y API real opcional (spec 2026-10-21 §8, D-S1..D-S3)"`

### Task 14: Verificación final, documentación y merge

**Files:**
- Modify: `README.md` (sección Automations: motor, puertos, `http_calls`, simulador, opciones `Automations:Workers` y `Messaging:Automation:Enqueue`; proveedor `external-api` en Integrations; la nota del desvío de D-H8), `CLAUDE.md` (gotchas: «el motor sólo responde `Open`, sin asesor y `Active`; `IBotOutbound` lo vuelve a verificar al enviar»; «`clientId` determinístico por `(runId, nodeId, índice, attempts)`: un reintento de la fila no duplica»; «el cliente HTTP del bloque se registra en Bootstrapper porque el handler anti-SSRF es de Integrations.Infrastructure»; reemplazar la gotcha del plan 1 sobre el flag apagado), `docs/estado.md` **sólo al mergear en `develop`**.

- [ ] **Step 1: Correr todo lo tocado** — `dotnet test tests/Modules/Automations`, `tests/Modules/Messaging`, `tests/Modules/Integrations`, `tests/Bootstrapper`, `tests/ArchitectureTests`. Expected: PASS; pegar el resumen de cada corrida en el reporte.
- [ ] **Step 2: `dotnet build Backend.slnx -warnaserror`** — Expected: sin advertencias nuevas.
- [ ] **Step 3: Revisar la rama contra el spec §5, §7, §8, §5.5 y D-M*, D-H*, D-S*** — cada decisión tiene una prueba que la ejerce; `GET /runs` y los tres endpoints de simulación existen; la lista de §4.4 queda completa. Quedan fuera, a propósito: frontend (plan 3) y Clasic (plan 4).
- [ ] **Step 4: Commit de docs** — `git commit -m "docs(automations): README y CLAUDE.md del motor, el bloque HTTP y el simulador"`
- [ ] **Step 5: Merge** — `git branch --show-current` (debe decir `feature/automatizaciones-motor-backend`); `git checkout develop`; `git merge --ff-only feature/automatizaciones-motor-backend`; actualizar `docs/estado.md` (fila «Automatizaciones … spec 4» → «Backend 2 construido (`<sha>`): el motor atiende; falta frontend (plan 3) y Clasic (plan 4)» y una línea en Registro) y `git commit -m "docs(estado): backend 2 del constructor de chatbots en develop"`. Nunca `main`, nunca push.
