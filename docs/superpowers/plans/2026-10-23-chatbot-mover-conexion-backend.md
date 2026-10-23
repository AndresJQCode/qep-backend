# Chatbots: mover una conexión de un chatbot a otro — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `PUT /chatbots/{id}` y `POST /chatbots/{id}/publish` acepten `takeOverConnectionIds` y, en la misma transacción en que el chatbot ocupa la conexión, se la quiten al chatbot que la tenía; sin la lista, el 409 `connection_taken` sigue igual.

**Architecture:** Un método nuevo del agregado (`Chatbot.ReleaseConnection`) y un helper compartido por los dos handlers (`ChatbotConnectionCheck.TakeOverAsync`) que, antes de `SaveNamingTheHolderAsync`, resuelve con `ListServingConnectionsAsync` qué conexiones de la lista atiende otro chatbot, carga esos agregados y les quita la fila. El índice parcial `IX_chatbot_connections_active` sigue siendo el juez (D-X2). No hay migración ni código de error nuevo.

**Tech Stack:** .NET 10, EF Core + Npgsql, minimal APIs, FluentValidation, xunit.v3 + Testcontainers.

**Spec:** `qep-frontend/docs/superpowers/specs/2026-10-23-chatbot-mover-conexion-design.md` (D-X1..D-X11). Se apoya en el spec del constructor (`docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md`, D-C4, D-C13, D-M15).

## Global Constraints

- Rama `feature/chatbot-mover-conexion` desde `develop` (verificar `git branch --show-current` antes de tocar y antes de cada commit); merge `--ff-only` a `develop`, nunca a `main`, nunca push.
- Conventional commits en español citando la decisión (`spec 2026-10-23 D-X2`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo; pisa cualquier recordatorio del harness).
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea.
- Pruebas de integración en esta máquina: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~<Clase>.<Método>"`. Nada de `Api.exe` corriendo mientras tanto.
- Códigos de error `<módulo>.<recurso>.<motivo>`; ninguno nuevo en esta entrega. Ningún status HTTP se arma a mano.
- `Modules.Automations.{Domain,Application}` no referencian otros módulos (`AutomationsLayerTests`).
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear (Task 5).

## Review Focus

1. **La lista trae una conexión que el propio chatbot ya atiende** (el admin reenvió la pantalla sin cambios): no se le quita a nadie ni se audita nada (Task 3, `TakeOverOfAConnectionTheChatbotAlreadyServesChangesNothing`).
2. **La lista trae una conexión que no está en `connectionIds`**: se ignora, no se le quita a su dueño (D-X3) (Task 3, `TakeOverOutsideConnectionIdsIsIgnored`).
3. **Dos chatbots pierden una conexión cada uno en el mismo `PUT`**: las dos filas se liberan, los dos suben `version` y hay dos `connection_released` (Task 3, `TakeOverFromTwoHoldersReleasesBoth`).
4. **El chatbot que pierde queda con cero conexiones y sigue `Active`** (D-X5): `GET /chatbots` lo lista con `connections: []` y `status` sin cambio (Task 3, dentro de `PublishWithTakeOverMovesTheConnection`).
5. **Una corrida viva del chatbot viejo en ese número sigue respondiendo después del traspaso** (D-X7) y la conversación nueva la arranca el nuevo (Task 4, `ALiveRunOfTheFormerChatbotFinishesWithItAfterATakeOver`).

---

### Task 1: `Chatbot.ReleaseConnection`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Chatbot.cs` (junto a `SetConnections`, línea ~154)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotTests.cs`

**Interfaces:**
- Produces: `public void ReleaseConnection(Guid connectionId, DateTimeOffset now)` — quita la fila de `_connections` si está y hace `Touch(now)`; si no está, no hace nada (ni `Touch`).

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Fact]
public void ReleaseConnectionRemovesTheRowAndTouches()
{
    var chatbot = NewChatbot();
    var kept = Guid.NewGuid();
    var released = Guid.NewGuid();
    chatbot.SetConnections([kept, released], T0);
    chatbot.Publish(NoIssues, Member, T0.AddMinutes(1));

    chatbot.ReleaseConnection(released, T0.AddMinutes(2));

    Assert.Equal([kept], chatbot.Connections.Select(connection => connection.ConnectionId));
    Assert.Equal(T0.AddMinutes(2), chatbot.UpdatedAt);
    Assert.Equal(ChatbotStatus.Active, chatbot.Status);
}

[Fact]
public void ReleaseConnectionOfAnUnknownIdChangesNothing()
{
    var chatbot = NewChatbot();
    chatbot.SetConnections([Guid.NewGuid()], T0);

    chatbot.ReleaseConnection(Guid.NewGuid(), T0.AddMinutes(2));

    Assert.Single(chatbot.Connections);
    Assert.Equal(T0, chatbot.UpdatedAt);
}
```

- [ ] **Step 2: Correr y ver que fallan**

Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ChatbotTests.ReleaseConnection"`
Expected: error de compilación `'Chatbot' does not contain a definition for 'ReleaseConnection'`.

- [ ] **Step 3: Implementar `ReleaseConnection` en `Chatbot.cs`** con un `<summary>` que cite D-X2/D-X5 (el estado no cambia; el `Touch` es lo que hace que la otra pestaña reciba 412).

- [ ] **Step 4: Correr y ver que pasan**

Run: el mismo comando. Expected: `Passed! - Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Automations/Modules.Automations.Domain/Chatbot.cs tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotTests.cs
git commit -m "feat(automations): Chatbot.ReleaseConnection para el traspaso de una conexión (spec 2026-10-23 D-X2)"
```

---

### Task 2: Contrato — `takeOverConnectionIds` en `PUT` y en `publish`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs:32-41` (comandos), `:175-187` (validador)
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:365-371` (`AutomationsAuditActions`)
- Modify: `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs:55-57` (ruta), `:137-144` (`UpdateAsync`), `:238-241` (`PublishAsync`), `:285-287` (request)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Application
  public sealed record UpdateChatbotCommand(Guid TenantId, Guid ChatbotId, long Version, string Name, string Description, string Trigger,
      IReadOnlyList<string> Keywords, string RestartKeyword, int MaxAttempts, IReadOnlyList<Guid> ConnectionIds,
      IReadOnlyList<Guid> TakeOverConnectionIds) : ICommand<ChatbotEditorDto>;
  public sealed record PublishChatbotCommand(Guid TenantId, Guid ChatbotId, long Version, IReadOnlyList<Guid> TakeOverConnectionIds) : ICommand<ChatbotEditorDto>;
  public const string ChatbotConnectionReleased = "automations.chatbot.connection_released";   // AutomationsAuditActions
  // Api
  public sealed record UpdateChatbotRequest(..., IReadOnlyList<Guid> ConnectionIds, IReadOnlyList<Guid>? TakeOverConnectionIds = null);
  public sealed record PublishChatbotRequest(IReadOnlyList<Guid>? TakeOverConnectionIds);
  ```
- `UpdateAsync` y `PublishAsync` pasan `request?.TakeOverConnectionIds ?? []`. `PublishAsync` recibe `PublishChatbotRequest? request` (cuerpo opcional: sin cuerpo bindea `null`; si el binder responde 400 ante un cuerpo vacío, leerlo con `ReadBoundedJsonAsync<PublishChatbotRequest>` y `ChatbotLimits.ImportBodyMaxBytes`, aceptando longitud 0 como `null`). La ruta de `publish` agrega `.Accepts<PublishChatbotRequest>("application/json")`.
- Validador: `RuleFor(command => command.TakeOverConnectionIds).NotNull()` y tope 10 (el mismo de `ChatbotRules.ConnectionIds`). `PublishChatbotCommand` no tiene validador hoy; la lista se normaliza en el handler (`Distinct()`), sin validador nuevo.
- Los handlers **todavía no** toman nada en esta tarea: sólo reciben la lista. Los tests de esta tarea prueban el binding.

- [ ] **Step 1: Escribir las pruebas que fallan** (en `ChatbotsApiTests`, junto a `PublishASecondChatbotOnTheSameConnectionIs409`)

```csharp
// Sin cuerpo, publicar sigue igual que hoy: el cuerpo es opcional.
[Fact]
public async Task PublishWithoutABodyStillPublishes()   // reutiliza PublishableAsync + ActAsync; Assert OK y Status Active

// Con cuerpo JSON y la lista vacía, publicar sigue igual.
[Fact]
public async Task PublishWithAnEmptyTakeOverListPublishes()
// POST con JsonContent.Create(new { takeOverConnectionIds = Array.Empty<Guid>() }) e If-Match → 200.

// Once ids en la lista del PUT: 422 validation.failed con la clave takeOverConnectionIds.
[Fact]
public async Task UpdateWithMoreThanTenTakeOverIdsIs422()
// PutAsync con new UpdateChatbotRequest("Servicio", "", "NewConversation", [], "reiniciar", 3, [setup.WhatsAppId], Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList())
// Assert 422 y que ProblemAsync(...).Errors contiene la clave "takeOverConnectionIds" (ver cómo lo hace el test de 422 existente en la línea ~285).
```

Agregar un helper junto a `ActAsync`:

```csharp
/// <summary><c>POST …/publish</c> con cuerpo: la lista de conexiones que se le quitan a otro chatbot (spec 2026-10-23 D-X1).</summary>
private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Setup setup, Guid chatbotId, long version, IReadOnlyList<Guid> takeOver)
```

- [ ] **Step 2: Correr y ver que fallan**

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests.PublishWith|FullyQualifiedName~ChatbotsApiTests.UpdateWithMoreThanTen"`
Expected: `UpdateWithMoreThanTenTakeOverIdsIs422` no compila (el record no tiene el parámetro); tras agregar sólo el record, `PublishWithAnEmptyTakeOverListPublishes` responde 415 o 400 y el 422 no llega.

- [ ] **Step 3: Implementar** los records, los comandos, el validador, la constante de auditoría y los dos handlers de endpoint. Los handlers de Application reciben el campo y lo ignoran por ahora.

- [ ] **Step 4: Correr y ver que pasan**, más el resto de `ChatbotsApiTests` (los tests existentes construyen `UpdateChatbotRequest` con 7 argumentos: el parámetro opcional los deja compilar).

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests"` y `dotnet test tests/ArchitectureTests`.
Expected: todo en verde.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Automations tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs
git commit -m "feat(automations): takeOverConnectionIds en PUT /chatbots/{id} y POST …/publish (spec 2026-10-23 D-X1)"
```

---

### Task 3: El traspaso en los handlers

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` — `ChatbotConnectionCheck` (línea ~262), `UpdateChatbotHandler.HandleAsync` (~440), `PublishChatbotHandler.HandleAsync` (~545)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`

**Interfaces:**
- Consumes: `Chatbot.ReleaseConnection` (Task 1), `TakeOverConnectionIds` en los comandos (Task 2), `IChatbotRepository.ListServingConnectionsAsync` / `FindAsync`, `IAutomationsAuditRecorder.Record`.
- Produces:
  ```csharp
  /// <summary>Spec 2026-10-23 D-X1..D-X3: a cada conexión del chatbot que esté en la lista y que atienda otro chatbot, se la quita a ese
  /// chatbot en esta misma unidad de trabajo, con un <c>connection_released</c> por conexión (D-X6). Lo que no está ocupado por otro, o no
  /// está en las conexiones del chatbot, se ignora. El índice sigue decidiendo al guardar (D-X2).</summary>
  public static async Task TakeOverAsync(
      IChatbotRepository chatbots, IAutomationsAuditRecorder audit, Guid actorId, Chatbot chatbot,
      IReadOnlyList<Guid> takeOverConnectionIds, DateTimeOffset now, CancellationToken cancellationToken)
  ```
- `UpdateChatbotHandler`: después de `chatbot.SetConnections(...)` y **sólo si `chatbot.Status != ChatbotStatus.Draft`** (D-X4), llama `TakeOverAsync` antes de `SaveNamingTheHolderAsync`. `PublishChatbotHandler`: después de `chatbot.Publish(...)`, siempre.
- Orden de las operaciones en el `SaveChangesAsync`: la fila liberada es un `DELETE` y la que ocupa es un `INSERT` (PUT sobre publicado) o un `UPDATE active = true` (publicar un borrador). EF Core ordena los `DELETE` antes de los `INSERT`/`UPDATE` de la misma tabla; `PublishWithTakeOverMovesTheConnection` lo verifica. Si el índice llegara a disparar igual, la salida es abrir `unitOfWork.BeginTransactionAsync` en el handler y guardar dos veces dentro de ella (primero las liberaciones); sólo si hace falta.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
// D-X1, D-X2, D-X5, D-X6: publicar con la lista toma la conexión; el otro queda sin ella, sigue Active, sube version y audita.
[Fact]
public async Task PublishWithTakeOverMovesTheConnection()
// first = PublishableAsync() publicado; second = PublishableAsync("Ventas").
// var response = await PublishAsync(setup.Client, setup, second, secondVersion, [setup.WhatsAppId]);
// Assert 200; ActiveRowsAsync == 1; GetAsync(second).Chatbot.Connections tiene WhatsAppId;
// var former = await GetAsync(setup, first): Connections vacío, Status "Active", Version > la que tenía;
// audit: count(action='automations.chatbot.connection_released' AND resource_id=first) == 1;
// ServedBy de la conexión en GetAsync(second).Connections == new ServedByDto(second, "Ventas").

// D-X1: PUT sobre un chatbot publicado con la lista.
[Fact]
public async Task UpdateAPublishedChatbotWithTakeOverMovesTheConnection()
// first publicado en WhatsAppId; second = PublishableAsync("Ventas") publicado en una segunda conexión (SeedWhatsAppConnectionAsync).
// PutAsync(second) con ConnectionIds [segunda, WhatsAppId] y TakeOverConnectionIds [WhatsAppId] → 200; first queda con Connections vacío; ActiveRowsAsync(WhatsAppId) == 1.

// D-X4: sobre un borrador la lista se ignora.
[Fact]
public async Task UpdateADraftWithTakeOverTouchesNobody()
// first publicado; second borrador. PutAsync(second) con ConnectionIds [WhatsAppId] y TakeOverConnectionIds [WhatsAppId] → 200;
// GetAsync(first).Chatbot.Connections sigue con WhatsAppId; sin connection_released; ActiveRowsAsync == 1.

// D-X3: una conexión libre en la lista no cambia nada; una que el chatbot ya atiende, tampoco.
[Fact]
public async Task TakeOverOfAFreeConnectionIsIgnored()
// PublishAsync(second, [WhatsAppId]) con first todavía borrador → 200, sin connection_released.
[Fact]
public async Task TakeOverOfAConnectionTheChatbotAlreadyServesChangesNothing()
// first publicado; PublishAsync(first, [WhatsAppId]) de nuevo (v2) → 200; Version sube sólo por publicar; sin connection_released.

// Review Focus 2: la lista nombra una conexión que no está en connectionIds.
[Fact]
public async Task TakeOverOutsideConnectionIdsIsIgnored()
// first publicado en WhatsAppId; second con ConnectionIds [segunda] publica con take-over [WhatsAppId] → 200; first conserva WhatsAppId.

// Review Focus 3: dos chatbots pierden una cada uno.
[Fact]
public async Task TakeOverFromTwoHoldersReleasesBoth()
// a publicado en WhatsAppId, b publicado en segunda; c publica con ConnectionIds [WhatsAppId, segunda] y take-over de las dos → 200;
// a y b con Connections vacío; dos connection_released (uno por resource_id); ActiveRowsAsync == 1 en cada conexión.

// Sin la lista, el 409 sigue (ya existe: PublishASecondChatbotOnTheSameConnectionIs409). D-X10: un pausado puede tomar.
[Fact]
public async Task APausedChatbotCanTakeOverAConnection()
// second publicado en segunda y pausado; first publicado en WhatsAppId. PutAsync(second) con [segunda, WhatsAppId] + take-over [WhatsAppId] → 200;
// second.Status sigue "Paused"; ActiveRowsAsync(WhatsAppId) == 1 y pertenece a second (ServedBy).
```

- [ ] **Step 2: Correr y ver que fallan**

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests.PublishWithTakeOver|FullyQualifiedName~ChatbotsApiTests.TakeOver|FullyQualifiedName~ChatbotsApiTests.UpdateAPublished|FullyQualifiedName~ChatbotsApiTests.UpdateADraft|FullyQualifiedName~ChatbotsApiTests.APausedChatbot"`
Expected: los que toman responden 409 `connection_taken` en vez de 200.

- [ ] **Step 3: Implementar `TakeOverAsync`** y llamarlo desde los dos handlers. Agrupar las filas de `ListServingConnectionsAsync` por `ChatbotId` para cargar cada holder una vez; `audit.Record(tenantId, actorId, false, AutomationsAuditActions.ChatbotConnectionReleased, AutomationsAuditActions.ResourceChatbot, holder.Id, now)` por conexión.

- [ ] **Step 4: Correr y ver que pasan**, más toda la clase y `AutomationsAuditTests`.

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests|FullyQualifiedName~AutomationsAuditTests"`
Expected: todo en verde (incluido `PublishASecondChatbotOnTheSameConnectionIs409` y `ConcurrentPublishOnTheSameConnectionLeavesOneActive`, que no cambian).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Automations/Modules.Automations.Application/Chatbots.cs tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs
git commit -m "feat(automations): el traspaso de una conexión libera al chatbot que la tenía en la misma transacción (spec 2026-10-23 D-X2, D-X6)"
```

---

### Task 4: El motor después del traspaso (D-X7)

**Files:**
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs`

**Interfaces:**
- Consumes: los helpers de `EngineEndToEndTests.cs` (publicar un chatbot en la conexión, encolar un entrante, drenar el worker, leer `conversation_runs`). Leer `TwoInboundsAreProcessedInOrderOnePerPass` y `AResolvedConversationReopenedByTheCustomerStartsAgain` como patrón.
- No hay código de producción: la prueba documenta que D-M15 ya cubre D-X7. Si falla, es un hallazgo que se reporta antes de tocar el motor.

- [ ] **Step 1: Escribir la prueba**

```csharp
// Spec 2026-10-23 D-X7: lo que el chatbot viejo ya atiende termina con él; lo nuevo lo arranca el nuevo.
[Fact]
public async Task ALiveRunOfTheFormerChatbotFinishesWithItAfterATakeOver()
// 1. Chatbot A publicado en la conexión; entrante de la conversación 1 → corrida viva de A (Waiting en una pregunta).
// 2. Chatbot B (mismo flujo del Anexo A, nombre «Ventas») publica con takeOverConnectionIds [conexión].
// 3. Entrante de la conversación 1 (la respuesta a la pregunta) → la corrida sigue y run.ChatbotId == A.
// 4. Entrante de una conversación 2 nueva → se crea una corrida con ChatbotId == B.
// Assert: conversation_runs de la conversación 1 tiene una sola fila, chatbot_id = A; la de la conversación 2, chatbot_id = B.
```

- [ ] **Step 2: Correr**

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~EngineEndToEndTests.ALiveRunOfTheFormerChatbot"`
Expected: PASS a la primera. Si falla, parar y reportar con la salida: el spec dice que el motor no se toca en esta entrega.

- [ ] **Step 3: Commit**

```bash
git add tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs
git commit -m "test(automations): una corrida viva del chatbot que pierde la conexión termina con él (spec 2026-10-23 D-X7)"
```

---

### Task 5: Documentación, merge y estado

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (el `<summary>` de `UpdateChatbotHandler` y `PublishChatbotHandler` cita D-X1/D-X4)
- Modify (en `develop`, después del merge): `docs/estado.md`

- [ ] **Step 1: Revisar que los `<summary>` de los dos handlers y de `ChatbotConnectionCheck` digan qué hace la lista** y citen `spec 2026-10-23`. Commit `docs(automations): …` si hubo cambios.

- [ ] **Step 2: Suite del módulo completa**

Run: `Seed__Enabled=false dotnet test tests/Modules/Automations && dotnet test tests/ArchitectureTests`
Expected: todo en verde.

- [ ] **Step 3: Merge a `develop`** (`git checkout develop && git merge --ff-only feature/chatbot-mover-conexion`). Nunca push.

- [ ] **Step 4: En `develop`, agregar a `docs/estado.md`** una fila en la tabla de Automatizaciones: «Mover una conexión de un chatbot a otro (spec 2026-10-23): backend construido y mergeado (`<sha>`); frontend pendiente (plan en `qep-frontend`)». Commit `docs(estado): traspaso de conexión entre chatbots en develop`.
