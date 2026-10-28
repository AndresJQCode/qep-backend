# Mensajería: leída al responder — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que responder (cualquier saliente aceptado por Meta) y resolver quiten la marca de no leída
y manden el acuse, sin subir `version` al responder.

**Architecture:** El acuse que hoy vive dentro de `MarkConversationReadHandler` se extrae a una clase
compartida, `ReadReceipts`. La rama `Sent` de `OutboundGuards.SettleAsync` resta al contador lo que
el asesor tenía a la vista (`seen`) con un `UPDATE` atómico que no toca `version`. `Conversation.Resolve`
pone el contador en 0 y el handler manda el acuse después del commit.

**Tech Stack:** .NET 10, EF Core + SQL crudo (Npgsql), xUnit v3, Testcontainers Postgres.

**Spec:** `docs/superpowers/specs/2026-10-16-mensajeria-leida-al-responder-design.md`. Rigen las
secciones §3 y §5 (backend), con la corrección del plan anotada en §3.1.

## Global Constraints

- TDD obligatorio: RED antes que GREEN, con la salida literal de ambos (CLAUDE.md).
- Conventional commits, **sin atribución de IA**. Antes de cada commit, `git branch --show-current`:
  tiene que decir `develop` o una rama propia, nunca `main`.
- Rama de trabajo: `feature/messaging-leida-al-responder`, creada desde `develop`.
- Comentarios y documentación tutean, en español colombiano, y explican el **por qué** citando el spec
  (`Spec 2026-10-16 §3.1`).
- Responder **no** sube `version` ni `updated_at`.
- Sin migración, sin endpoints nuevos, sin permisos nuevos, sin códigos de error nuevos.
- Un fallo del acuse nunca cambia la respuesta del envío ni la del resolver.

## Review Focus

1. **Un entrante que llega mientras Meta contesta el envío** tiene que seguir pendiente, y no puede
   salir ningún acuse. Lo prueba la Task 3, `AnInboundDuringTheSendSurvivesAndNoReceiptGoesOut`.
2. **Un `/read` de otra pestaña en el hueco** (el contador baja a 0 y después entra un mensaje, así que
   queda en 1 con `seen = 2`) no puede dejar el contador negativo ni borrar el nuevo. Lo prueba la
   Task 3, `ACounterBelowWhatTheAdvisorSawIsLeftAlone`.
3. **Un reintento de un `Failed` con el mismo `clientId`** pasa otra vez por `Sent`, y `seen` tiene que
   ser el de **ese** request. Ya lo cubre el diseño, porque `seen` sale del `LoadAsync` de cada
   request. Lo prueba la Task 3, `ARetriedFailedSendClearsWhatTheRetrySaw`.
4. **Una conversación con `unread_count = 0`** que se responde no puede mandar un acuse vacío a Meta.
   Lo prueba la Task 3, `AReplyWithNothingUnreadSendsNoReceipt`.
5. **Resolver una conversación vieja** (último entrante de hace más de 30 días) baja el contador, pero
   no manda acuse porque `CanAcknowledgeReading` lo impide. Lo prueba la Task 4,
   `ResolvingAnOldConversationClearsWithoutAReceipt`.

---

### Task 1: Extraer `ReadReceipts` del handler de leído

Refactor sin cambio de comportamiento: las pruebas existentes de `/read` son la red.

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ReadReceipts.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs`, la clase `MarkConversationReadHandler`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`, cerca de la línea 640: `services.AddScoped<ReadReceipts>()`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationLifecycleApiTests.cs` (las existentes, sin cambios)

**Interfaces:**
- Produces: `public sealed partial class ReadReceipts(IMessagingConnectionDirectory connections, IWhatsAppCloudClient meta, IInstagramClient instagram, ILogger<ReadReceipts> logger)`
  con `public Task SendAsync(Guid tenantId, Guid conversationId, ReadReceiptTarget target, DateTimeOffset now, CancellationToken cancellationToken)`.
  Nunca lanza, salvo `OperationCanceledException`, y un fallo sólo se loguea con el `LogReceiptFailed`
  que se mueve desde el handler. Las guardas son las de hoy: `CanAcknowledgeReading`; en Instagram,
  `ResolveInstagramSenderAsync` + `MarkSeenAsync(sender, userId)`; en WhatsApp, `ResolveSenderAsync` +
  `MarkReadAsync(sender, wamid)`.

- [ ] **Step 1: Crear la rama**

```bash
git checkout develop && git pull && git checkout -b feature/messaging-leida-al-responder
```

- [ ] **Step 2: Correr las pruebas de leído como línea base**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationLifecycleApiTests"`
Expected: PASS.

- [ ] **Step 3: Mover el bloque del acuse (`if (target.LastInboundWamid ...)` hasta el `catch`) a `ReadReceipts.SendAsync`, y que `MarkConversationReadHandler` reciba `ReadReceipts` en lugar de `connections`, `meta`, `instagram` y `logger`.**

- [ ] **Step 4: Correr las mismas pruebas y la suite unitaria de Messaging**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationLifecycleApiTests"` y `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests`
Expected: PASS, el mismo conteo que el Step 2.

- [ ] **Step 5: Commit**

```bash
git branch --show-current
git commit -am "refactor(messaging): el acuse de leído pasa a ReadReceipts para compartirlo"
```

### Task 2: `MarkRepliedAsync` en el repositorio

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationRepository.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (`FakeConversationRepository`)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingPersistenceTests.cs`

**Interfaces:**
- Produces: `Task<RepliedTarget?> MarkRepliedAsync(Guid tenantId, Guid conversationId, int seen, CancellationToken cancellationToken)` en `IConversationRepository`.
- Produces: `public sealed record RepliedTarget(ReadReceiptTarget Receipt, int RemainingUnread);` en `IConversationRepository.cs`.
  Devuelve `null` si `seen <= 0` (sin ir a la base) o si el `UPDATE` no tocó ninguna fila.
- Produces (fake): `FakeConversationRepository.Replied` (`List<(Guid ConversationId, int Seen)>`) y `NextReplied` (`RepliedTarget?`, por defecto `null`).

- [ ] **Step 1: Escribir las pruebas que fallan** (en `MessagingPersistenceTests`, con el mismo arreglo de base que las vecinas)

```csharp
[Fact]
public async Task MarkRepliedSubtractsWhatWasSeenAndLeavesVersionAndUpdatedAtAlone()
// unread_count = 3, seen = 2 → RemainingUnread == 1, unread_count == 1,
// version y updated_at iguales a los de antes; Receipt.ConnectionId y Receipt.LastInboundWamid los de la fila.

[Fact]
public async Task MarkRepliedLeavesACounterBelowWhatWasSeenAlone()
// unread_count = 1, seen = 2 → null, unread_count sigue 1.

[Fact]
public async Task MarkRepliedWithNothingSeenNeverTouchesTheRow()
// seen = 0 → null, unread_count y version sin cambios.
```

- [ ] **Step 2: Correrlas y verificar que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~MarkReplied"`
Expected: falla la compilación porque `MarkRepliedAsync` no existe.

- [ ] **Step 3: Implementar**

Usa el mismo patrón que `MarkReadAsync` (`SqlQuery<…>` con `RETURNING`, mismo `ReadRow` más la columna
`unread_count`):

```sql
UPDATE messaging.conversations
   SET unread_count = unread_count - {seen}
 WHERE id = {conversationId} AND tenant_id = {tenantId} AND unread_count >= {seen}
RETURNING connection_id, last_inbound_wamid, last_inbound_at, channel, user_id, unread_count
```

Sin `version` ni `updated_at` en el `SET`. En el comentario va la razón: es parte del envío
(«Fix 1, hallazgo 1»). El fake registra la llamada y devuelve `NextReplied`.

- [ ] **Step 4: Correr las pruebas y verificar que pasan**

Run: el mismo comando del Step 2.
Expected: PASS (3).

- [ ] **Step 5: Commit**

```bash
git branch --show-current
git commit -am "feat(messaging): MarkRepliedAsync resta lo que el asesor vio sin tocar version"
```

### Task 3: Responder quita la marca

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs`, la firma de `SettleAsync` y su rama `SendOutcome.Sent`
- Modify: `SendMessage.cs`, `SendAudioMessage.cs`, `SendMediaMessage.cs`, `TemplateOutbound.cs`, `SendTemplateMessage.cs` y `StartConversation.cs`, todos en `Modules.Messaging.Application`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs`, los cinco constructores de handler, que reciben `new ReadReceipts(Connections, Meta, Instagram, NullLogger<ReadReceipts>.Instance)`; además `FakeWhatsAppClient.MarkReadAsync` pasa a registrar en `ReadReceipts` (`List<string>` de wamid) y devuelve `true`
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ReplyMarksReadApiTests.cs`

**Interfaces:**
- Consumes: `ReadReceipts.SendAsync` (Task 1) y `IConversationRepository.MarkRepliedAsync` (Task 2).
- Produces: `OutboundGuards` recibe `ReadReceipts receipts` como último parámetro del constructor.
  `SettleAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, SendResult result, DateTimeOffset answeredAt, string? metaMediaId, Func<Task>? afterRollback, int seen)`.
  Cada caller pasa `target.Conversation.UnreadCount` como `seen`. `TemplateOutbound` lo saca del
  `OutboundTarget` que ya recibe.

En la rama `Sent`, el orden es este:
1. `CommitSentAsync`.
2. `MarkRepliedAsync(tenantId, claim.ConversationId, seen, CancellationToken.None)`.
3. `NotifyConsolidatedAsync`.
4. Si el resultado tiene `RemainingUnread == 0`, `receipts.SendAsync(..., result.Receipt, UtcNow, CancellationToken.None)`.

Un fallo de los pasos 2 y 4 se traga igual que el aviso, porque Meta ya aceptó el envío.

- [ ] **Step 1: Escribir las pruebas de integración que fallan** (`ReplyMarksReadApiTests`, con el arreglo de `SendMessageApiTests`: el webhook entrante `w1` deja `unread_count = 1`; antes de enviar, `UPDATE … SET unread_count = 2` y `assigned_member_id` del owner para aislar la autoasignación)

```csharp
[Fact] public async Task ATextReplyClearsTheMarkAndAcknowledgesTheLastInbound()
// 201; unread_count == 0; version y updated_at iguales; un request a /111/messages con "\"status\":\"read\"" y "\"message_id\":\"w1\"".

[Fact] public async Task ATemplateReplyClearsTheMark()
[Fact] public async Task AVoiceNoteReplyClearsTheMark()
[Fact] public async Task AnAttachmentReplyClearsTheMark()
[Fact] public async Task StartingAnExistingConversationWithATemplateClearsTheMark()
// cada una con el arreglo de su *ApiTests vecino (SendTemplateApiTests, SendAudioMessageApiTests,
// SendMediaMessageApiTests, StartConversationApiTests) → unread_count == 0.

[Fact] public async Task AnInstagramReplyClearsTheMarkWithMarkSeen()
// arreglo de InstagramSendApiTests → unread_count == 0 y un mark_seen.

[Fact] public async Task ARejectedSendLeavesTheMarkAndSendsNoReceipt()
// Meta responde GraphError(131026) → unread_count == 2, ningún "\"status\":\"read\"".

[Fact] public async Task AnInboundDuringTheSendSurvivesAndNoReceiptGoesOut()
// RespondWith("/111/messages", req => { ExecuteAsync(cs, "UPDATE messaging.conversations SET unread_count = unread_count + 1").GetAwaiter().GetResult(); return Json(OK, sendOk); })
// → unread_count == 1, ningún "\"status\":\"read\"".

[Fact] public async Task ACounterBelowWhatTheAdvisorSawIsLeftAlone()
// RespondWith: SET unread_count = 1 durante el envío (seen = 2) → unread_count == 1.

[Fact] public async Task ARetriedFailedSendClearsWhatTheRetrySaw()
// primer envío con 500 (Failed) → unread_count == 2; luego SET unread_count = 3; reintento con el mismo clientId OK → 0.

[Fact] public async Task AReplyWithNothingUnreadSendsNoReceipt()
// unread_count = 0 → ningún "\"status\":\"read\"".
```

- [ ] **Step 2: Correrlas y verificar que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ReplyMarksReadApiTests"`
Expected: FAIL en las que esperan `unread_count == 0`. `ARejectedSend…`, `ACounterBelow…` y `AReplyWithNothing…` pueden pasar ya, y eso está bien.

- [ ] **Step 3: Implementar** según el bloque de Interfaces. Registrar `ReadReceipts` en los cinco handlers vía DI: agregar `ReadReceipts receipts` al constructor y pasarlo a `new OutboundGuards(...)`. Actualizar `MessagingTestBed`.

- [ ] **Step 4: Correr las pruebas y la suite completa de Messaging**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ReplyMarksReadApiTests"`. Después `dotnet test tests/Modules/Messaging` y `dotnet test tests/ArchitectureTests`.
Expected: todo en PASS, incluida `AHappySendAnswers201SentWithTheClientIdAndUpdatesTheSnapshot` sin cambios.

- [ ] **Step 5: Commit**

```bash
git branch --show-current
git commit -am "feat(messaging): responder quita la marca de no leída y manda el acuse"
```

### Task 4: Resolver quita la marca

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs`, `Resolve`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs`, `ResolveConversationHandler`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `ReadReceipts.SendAsync` (Task 1).
- `ResolveConversationHandler` recibe `ReadReceipts receipts`. Antes de `Resolve`, guarda
  `var hadUnread = conversation.UnreadCount > 0`. Después del `SaveChangesAsync` y del aviso, si
  `hadUnread`, llama `receipts.SendAsync(tenantId, conversation.Id, new ReadReceiptTarget(conversation.ConnectionId, conversation.LastInboundWamid, conversation.LastInboundAt, conversation.Channel, conversation.UserId), now, cancellationToken)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// ConversationTests
[Fact] public void ResolveClearsTheUnreadCounter()
// Conversation.ForTests(..., unreadCount: 3, lastInboundAt: now) → Resolve(now) → UnreadCount == 0.

// ConversationLifecycleApiTests
[Fact] public async Task ResolvingClearsTheMarkAndAcknowledgesOnce()
// unread 1 (del arreglo) → resolve con If-Match → summary.unreadCount == 0, version == f.Version + 1, un "\"status\":\"read\"" con "wamid.in".
[Fact] public async Task ResolvingWithNothingUnreadSendsNoReceipt()
[Fact] public async Task ResolvingAnOldConversationClearsWithoutAReceipt()
// UPDATE last_inbound_at = now() - interval '31 days' → unread 0, ningún acuse.
```

- [ ] **Step 2: Correrlas y verificar que fallan**

Run: `dotnet test tests/Modules/Messaging --filter "FullyQualifiedName~Resolv"`
Expected: FAIL en `ResolveClearsTheUnreadCounter` y en `ResolvingClearsTheMarkAndAcknowledgesOnce`.

- [ ] **Step 3: Implementar** `UnreadCount = 0` en `Conversation.Resolve` y el acuse en el handler, según Interfaces.

- [ ] **Step 4: Correr la suite de Messaging y la de arquitectura**

Run: `dotnet test tests/Modules/Messaging` y `dotnet test tests/ArchitectureTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git branch --show-current
git commit -am "feat(messaging): resolver quita la marca de no leída y manda el acuse"
```

### Task 5: Documentación y merge a `develop`

**Files:**
- Modify: el summary de `ConversationLifecycleApiTests` y el de `MarkConversationReadHandler`. Hoy dicen que el hilo marca al abrir; tienen que decir que `/read` es el botón de la barra (spec 2026-10-16 §3.3).
- Modify: `docs/superpowers/specs/2026-10-14-mensajeria-marcar-no-leida-design.md`, con una línea al principio: «Parcialmente sustituido por `2026-10-16-mensajeria-leida-al-responder-design.md`».

- [ ] **Step 1: Editar los dos summaries y el encabezado del spec viejo.**
- [ ] **Step 2: Correr la suite completa del backend.** Run: `dotnet test`. Expected: PASS.
- [ ] **Step 3: Commit y merge a `develop`, nunca a `main`.**

```bash
git branch --show-current
git commit -am "docs(messaging): /read pasa a ser el botón; el spec de no leída apunta al nuevo"
git checkout develop && git merge --no-ff feature/messaging-leida-al-responder -m "merge: feature/messaging-leida-al-responder en develop"
```
