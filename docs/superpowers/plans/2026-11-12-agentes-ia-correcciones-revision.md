# Correcciones de la revisión del módulo Ai — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** cerrar los tres hallazgos de corrección de la revisión de `feat/agentes-ia-modulo` (2026-11-12): el riesgo de deadlock en la ocupación de conexiones, las conexiones borradas que siguen contando y el comentario engañoso de `AiUnitOfWork`. Además, bloquear el borrado de una conexión que un bot atiende (decisión del owner).

**Architecture:** la sincronización de `messaging.connection_bots` deja de correr cuando el guardado sólo toca el borrador, toma las filas en orden fijo y, si aun así pierde una carrera, responde 412 en vez de 500. Esto se hace igual en los dos gemelos (Ai y Automations). El editor de agentes cuenta y devuelve sólo las conexiones que siguen existiendo en Integrations.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-29-agentes-ia-design.md` (§4.3 ocupación, D-A7 borrador, D-A10 qué ocupa). Plan base: `docs/superpowers/plans/2026-11-12-agentes-ia-claude-backend.md`.

## Global Constraints

- TDD: RED antes que GREEN, con la salida literal de los dos.
- Commits: conventional commits, sin atribución de IA.
- Rama: `feat/agentes-ia-modulo`. Comprueba `git branch --show-current` antes de cada commit.
- Las pruebas de integración necesitan `$env:Seed__Enabled = "false"` en esta máquina.
- Antes de una corrida de integración, mira `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. Si hay otra corrida, espera; no la detengas.
- Mientras iteras, filtra la integración por nombre de prueba (`--filter "FullyQualifiedName~<Prueba>"`).
- El 412 de una carrera usa el código existente `concurrency.conflict` (`RequestConcurrencyException`), el mismo que ya emiten los dos unit of work. No se inventa un código nuevo.
- Precedente de la detección de carreras: `CustomersWriteRaces.IsRace` (`src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersWriteRaces.cs`). Se replica, no se referencia: los módulos no se ven entre sí.

## Review Focus

1. **Autoguardado de un agente publicado.** Hoy cada tecla reescribe todas las filas de `connection_bots` del tenant. Lo esperado: el borrador no toca la ocupación. Lo cubren las pruebas `xmin` de la Tarea 1.
2. **Renombrar un agente o chatbot publicado.** Tiene que seguir actualizando `bot_name`, porque la bandeja y el 409 lo leen. Ya lo cubren las pruebas existentes de renombre; la Tarea 1 no debe romperlas.
3. **Pausar un chatbot.** El filtro nuevo no puede saltarse un cambio que sí mueve la ocupación. Por eso la lista de propiedades incluye `Status` y `PublishedVersionId`, y las pruebas existentes de pausa deben seguir verdes.
4. **Dos guardados simultáneos que igual chocan.** Por ejemplo, dos publicaciones en el mismo tenant: tienen que responder 412 y no 500. Lo cubre la prueba unitaria de `IsWriteRace`. La carrera real no se reproduce de forma determinista, y el `ORDER BY` es lo que la hace improbable.
5. **Borrar una conexión que atiende un bot.** Se bloquea con 409 y el ocupante, también si el bot está en pausa. Lo cubre la Tarea 3. La que se haya borrado antes, o la que cae en la carrera aceptada, no cuenta ni vuelve al editor. Lo cubre la Tarea 2.

---

### Task 1: Ocupación sin autoguardado, en orden y con 412 ante una carrera

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/Persistence/AiUnitOfWork.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsUnitOfWork.cs`
- Modify: `src/Bootstrapper/AgentConnectionSlots.cs:36-44`
- Modify: `src/Bootstrapper/ChatbotConnectionSlots.cs:36-44`
- Test: `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentsApiTests.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.ConnectionSlots.cs`
- Test (nuevo): `tests/Modules/Ai/Modules.Ai.UnitTests/AiUnitOfWorkTests.cs`
- Test (nuevo): `tests/Modules/Automations/Modules.Automations.UnitTests/AutomationsUnitOfWorkTests.cs`

**Interfaces:**
- Produces: `internal static bool AiUnitOfWork.IsWriteRace(Exception exception)` e `internal static bool AutomationsUnitOfWork.IsWriteRace(Exception exception)`. Las dos devuelven `true` si algún `InnerException` de la cadena es `PostgresException` con `SqlState` `40P01` (`PostgresErrorCodes.DeadlockDetected`) o `40001` (`PostgresErrorCodes.SerializationFailure`). Los dos Infrastructure ya tienen `InternalsVisibleTo` hacia sus proyectos de pruebas.

- [ ] **Step 1: Escribe las pruebas que fallan**

Pruebas unitarias, iguales en los dos proyectos, cada una con su unit of work:

```csharp
[Theory]
[InlineData(PostgresErrorCodes.DeadlockDetected)]
[InlineData(PostgresErrorCodes.SerializationFailure)]
public void ADeadlockOrSerializationFailureIsAWriteRace(string sqlState) =>
    Assert.True(AiUnitOfWork.IsWriteRace(
        new InvalidOperationException("wrapper", new PostgresException("race", "ERROR", "ERROR", sqlState))));

[Fact]
public void AUniqueViolationIsNotAWriteRace() =>
    Assert.False(AiUnitOfWork.IsWriteRace(new PostgresException("dup", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));
```

Prueba de integración en `AgentsApiTests`:
- `SavingTheDraftOfAPublishedAgentDoesNotRewriteItsSlot`.
- Publica con `PublishableAsync` más `POST {Agents}/{id}/publish`.
- Lee `SELECT xmin::text FROM messaging.connection_bots WHERE connection_id = @id`. Agrega un helper `SlotXminAsync` junto a `SlotAsync`.
- Hace `PUT {Agents}/{id}/draft` con `ValidConfig()` y la versión vigente.
- Afirma `200` y que el `xmin` es el mismo.

Su gemela en `ChatbotsApiTests.ConnectionSlots.cs`: `SavingTheDraftOfAPublishedChatbotDoesNotRewriteItsSlot`, con los helpers que ese archivo ya usa para publicar y leer la fila.

- [ ] **Step 2: Comprueba el RED**

Unitarias: `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests` y `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests`.
Esperado: `CS0117` (`IsWriteRace` no existe). Sin contenedores.

Integración:

```powershell
dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~SavingTheDraftOfAPublishedAgentDoesNotRewriteItsSlot"
```

Lo mismo con el filtro de `SavingTheDraftOfAPublishedChatbotDoesNotRewriteItsSlot` en `Modules.Automations.IntegrationTests`.
Esperado: FAIL por `Assert.Equal`, con un `xmin` distinto: hoy el `ON CONFLICT DO UPDATE` reescribe la fila en cada guardado.

- [ ] **Step 3: Implementa**

1. **El filtro.** `TenantsWithConnectionChanges` cuenta un `Agent` o un `Chatbot` sólo si está `Added`, `Deleted`, o `Modified` con alguna de las propiedades que mueven la ocupación.
   - Las propiedades son `Name`, `Status` y `PublishedVersionId`, en una constante `SlotProperties` por unit of work.
   - Se mira con `entry.Properties.Any(p => p.IsModified && SlotProperties.Contains(p.Metadata.Name))`.
   - `AgentConnection` y `ChatbotConnection` siguen contando ante cualquier cambio.
   - El comentario explica por qué el borrador queda fuera: el editor guarda mientras se escribe.
2. **El orden.** Agrega `ORDER BY ac.connection_id` al `INSERT … SELECT` de `AgentConnectionSlots`, y `ORDER BY cc.connection_id` al de `ChatbotConnectionSlots`. El comentario de cada uno explica que dos guardados toman las filas en el mismo orden y no se cruzan.
3. **El 412.** Agrega `IsWriteRace` y un `catch (Exception exception) when (IsWriteRace(exception)) { throw Concurrency(exception); }` en cada unit of work.
   - Va después de los `catch` de `DbUpdateException`, porque el `ExecuteSqlAsync` de la sincronización lanza `PostgresException` sin envolver.
   - Junto al catch, cita `CustomersWriteRaces` como precedente.
4. **El comentario de `AiUnitOfWork`, hallazgo 3 de la revisión.** El `<summary>` deja de decir «Copia de `AutomationsUnitOfWork`». Ahora dice lo que hace distinto: sólo confirma la transacción que abrió él, porque Ai no expone `BeginTransactionAsync`. Si algún día lo expone, tiene que decidir si guardar confirma la del llamador, como hace Automations.

- [ ] **Step 4: Comprueba el GREEN**

Corre las dos unitarias completas, las dos pruebas filtradas de Step 2 y, una vez por bloque, las clases completas:

```powershell
dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AgentsApiTests"
dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ChatbotsApiTests"
```

Esperado: todo verde, incluidas las pruebas existentes de renombre y pausa (Review Focus 2 y 3).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Ai/Modules.Ai.Infrastructure/Persistence/AiUnitOfWork.cs src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsUnitOfWork.cs src/Bootstrapper/AgentConnectionSlots.cs src/Bootstrapper/ChatbotConnectionSlots.cs tests/Modules/Ai tests/Modules/Automations
git commit -m "fix(ai): keep draft autosaves off connection slots and map write races to 412"
```

### Task 2: Las conexiones borradas no cuentan ni vuelven al editor

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentEditorBuilder.cs:28-39` (`ValidateAsync`) y `:57` (`ConnectionIds` en `BuildAsync`)
- Test (nuevo): `tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs`

**Interfaces:**
- Consumes: `IAiConnections.ListAsync(Guid tenantId, CancellationToken)`, que ya existe y ya usan `BuildListAsync` y `EnsureConnectionsAsync`.
- Produces: no cambia ninguna firma pública.

- [ ] **Step 1: Escribe las pruebas que fallan**

Usa fakes en el mismo archivo para `IAiConnections`, `IAgentConnectionSlots`, `IAiMembers` e `IAiModelCatalog`, o los dobles de `Modules.Ai.UnitTests` si ya existen.

Prueba `ADeletedConnectionDoesNotCountForPublishing`:
- Un agente con `SetConnections([deletedId], now)`.
- Un fake de `IAiConnections` que no devuelve `deletedId`.
- Afirma que `ValidateAsync(agent, publishing: true, ct)` contiene un issue con código `connections_missing`.

Prueba `TheEditorOnlyReturnsLiveConnections`:
- Un agente con `[liveId, deletedId]`.
- Un fake que devuelve sólo `liveId`.
- Afirma que `BuildAsync(agent, ct).ConnectionIds` es `[liveId]` y que no hay `connections_missing`.

- [ ] **Step 2: Comprueba el RED**

```powershell
dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentEditorBuilderTests"
```

Esperado: las dos FAIL. La primera no encuentra `connections_missing`, porque hoy cuenta 1. La segunda recibe los dos ids.

- [ ] **Step 3: Implementa**

- `ValidateAsync` lee `connections.ListAsync(agent.TenantId, ct)` y pasa a `AgentValidationContext` sólo la cantidad de `agent.Connections` cuyo id está en esa lista.
- `BuildAsync` filtra `ConnectionIds` con la misma lista. Reutiliza la lectura y no la hagas dos veces por petición: `BuildAsync` ya llama a `ValidateAsync` y a `ListSelectableAsync`.
- Agrega un comentario en `ValidateAsync`: Integrations borra la conexión sin avisar (nadie consume `integrations.connection.deleted`), y la referencia de `ai.agent_connections` es blanda.

- [ ] **Step 4: Comprueba el GREEN**

Corre las unitarias completas de `Modules.Ai.UnitTests` y, al cerrar el bloque, `AgentsApiTests`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Ai/Modules.Ai.Application/AgentEditorBuilder.cs tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs
git commit -m "fix(ai): ignore deleted connections when validating and editing agents"
```

---

### Task 3: No se borra una conexión que un bot atiende

Decisión del owner, 2026-11-12: borrar una conexión que ocupa un chatbot o un agente se **bloquea** con 409. No se limpia en cascada.

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionBotOccupancy.cs` (puerto, record del ocupante y excepción)
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsErrorCodes.cs` (código nuevo)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/DeleteConnection.cs`
- Create: `src/Bootstrapper/IntegrationsConnectionBotOccupancy.cs` (adaptador SQL sobre `messaging.connection_bots`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro scoped, junto a los de §4.3, línea ~1168)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestDoubles.cs` (fake del puerto)
- Test (nuevo): `tests/Modules/Integrations/Modules.Integrations.UnitTests/DeleteConnectionHandlerTests.cs`
- Test: `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentsApiTests.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.ConnectionSlots.cs`

**Interfaces:**
- Produces, en `Modules.Integrations.Application`:
  - `public sealed record ConnectionBotOccupant(string Kind, Guid Id, string Name)`, con `Kind` igual a `"chatbot"` o `"agent"`. Son los mismos literales que `AiConnectionOccupant` y `ConnectionOccupant`: la pantalla ya los conoce.
  - `public interface IConnectionBotOccupancy { Task<ConnectionBotOccupant?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken); }`
  - `public sealed class ConnectionInUseByBotException(ConnectionBotOccupant occupant) : ResourceConflictException, IHasProblemExtensions`, con la extensión `occupant = { kind, id, name }`. La forma es la misma que `AgentConnectionTakenException` (`src/Modules/Ai/Modules.Ai.Application/AgentPorts.cs:91-104`).
  - `IntegrationsErrorCodes.InUseByBot = "integrations.connection.in_use_by_bot"`.
- Mensajes, con tuteo:
  - Chatbot: «El chatbot «{name}» atiende esta conexión. Quítasela antes de borrarla.»
  - Agente: «El agente IA «{name}» atiende esta conexión. Quítasela antes de borrarla.»

- [ ] **Step 1: Escribe las pruebas que fallan**

Unitarias en `DeleteConnectionHandlerTests`, con un fake de `IConnectionBotOccupancy` en `IntegrationsTestDoubles.cs`:
- `DeletingAConnectionABotServesIsAConflictWithTheOccupant`:
  - El fake devuelve `new ConnectionBotOccupant("agent", id, "Asesor virtual")`.
  - `Assert.ThrowsAsync<ConnectionInUseByBotException>`, y `Code == "integrations.connection.in_use_by_bot"`.
  - La extensión `occupant` lleva `kind = "agent"`.
  - El repositorio no recibió `Remove` y no se publicó evento.
- `DeletingAFreeConnectionStillWorks`: el fake devuelve `null` y el handler devuelve `true`.

Integración del adaptador real:
- `DeletingAConnectionAnAgentServesIsRejected` en `AgentsApiTests`:
  - Publica con `PublishableAsync` más `/publish`.
  - Hace `DELETE /api/v1/tenants/{t}/integrations/connections/{setup.WhatsAppId}` con el `If-Match` de la conexión.
  - Afirma 409, `code = integrations.connection.in_use_by_bot` y `occupant.kind = "agent"`.
  - Afirma que `SlotAsync` sigue devolviendo la fila.
- Gemela para chatbots en `ChatbotsApiTests.ConnectionSlots.cs`: `DeletingAConnectionAChatbotServesIsRejected`, con `occupant.kind = "chatbot"`.
- Una tercera prueba: después de pausar, el borrado sigue bloqueado, porque un agente pausado conserva la conexión (D-A10).

El cliente de prueba necesita el permiso `integrations.connection.manage`. Pídelo por `X-Permissions` si el harness no lo trae (CLAUDE.md, stub de desarrollo).

- [ ] **Step 2: Comprueba el RED**

Unitarias: `dotnet build tests/Modules/Integrations/Modules.Integrations.UnitTests`.
Esperado: `CS0246` (`IConnectionBotOccupancy` no existe). Sin contenedores.

- [ ] **Step 3: Implementa**

1. **El handler.** `DeleteConnectionHandler` recibe `IConnectionBotOccupancy`. Después de `ConcurrencyGuard.EnsureVersion` y antes de `repository.Remove`, si `FindAsync` devuelve un ocupante, lanza `ConnectionInUseByBotException`.
2. **El adaptador.**
   - Es `internal sealed class IntegrationsConnectionBotOccupancy(IntegrationsDbContext integrations) : IConnectionBotOccupancy`.
   - Corre `SELECT bot_kind, bot_id, bot_name FROM messaging.connection_bots WHERE tenant_id = {tenantId} AND connection_id = {connectionId} FOR SHARE`, con `SqlQuery` y todas las columnas, mapeando `bot_kind` 2 a `"agent"` y 1 a `"chatbot"`.
   - Comentario de acoplamiento por nombre, como en `AgentConnectionSlots`, que diga qué pruebas lo amarran.
   - Integrations no ve Mensajería (`IntegrationsLayerTests`): por eso el adaptador va en Bootstrapper.
3. **El registro.** Scoped en `QepServiceCollectionExtensions`, junto a los adaptadores de §4.3.

**Carrera aceptada.** Un bot que publica justo después del chequeo puede ocupar una conexión que se está borrando. El `FOR SHARE` no la evita, porque no hay fila que bloquear. Ese caso lo cubre la Tarea 2: la conexión muerta no cuenta ni vuelve al editor. No se agrega un lock por esto.

- [ ] **Step 4: Comprueba el GREEN**

- Las unitarias completas de `Modules.Integrations.UnitTests`.
- Las tres pruebas de integración filtradas por nombre.
- Al cerrar el bloque, `ConnectionLifecycleApiTests` (el borrado sin bot sigue respondiendo como antes) y `ArchitectureTests` (`IntegrationsLayerTests`).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Integrations src/Bootstrapper/IntegrationsConnectionBotOccupancy.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Integrations tests/Modules/Ai tests/Modules/Automations
git commit -m "feat(integrations): block deleting a connection a bot serves"
```

**Frontend (otro repo, fuera de este plan).** La pantalla de Integraciones tiene que mostrar el 409 `integrations.connection.in_use_by_bot` con el nombre del ocupante. Sin eso, el usuario ve un error genérico.

---

## Decisiones pendientes del owner (fuera de este plan)

- **Filas huérfanas de conexiones borradas antes de la Tarea 3.** Las que ya existan en `ai.agent_connections`, `automations.chatbot_connections` y `messaging.connection_bots` no se limpian. Son inofensivas tras la Tarea 2.
- **Pruebas de concurrencia real**, por ejemplo chatbot y agente publicando la misma conexión a la vez. Son no deterministas con Testcontainers; quedan como prueba manual si el owner la quiere.

---

## Tareas 4 y 5 (revisión final, 2026-11-12)

La revisión final de la rama encontró dos hallazgos medios. Van en un solo bloque, con un commit por tarea.

### Task 4: La sincronización de la ocupación se serializa por tenant

**Por qué.** El `ORDER BY` de la Tarea 1 no alcanza. El `DELETE` previo toma las filas en cualquier orden, y `ON CONFLICT DO UPDATE ... WHERE` bloquea la fila aunque el `WHERE` dé falso. Dos sincronizaciones del mismo tenant (renombrar un agente mientras otro quita su conexión, o un chatbot y un agente a la vez) todavía pueden cruzarse en un deadlock.

**Files:**
- Create: `src/Bootstrapper/ConnectionSlotsLock.cs`
- Modify: `src/Bootstrapper/AgentConnectionSlots.cs`, `src/Bootstrapper/ChatbotConnectionSlots.cs`
- Test: `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentsApiTests.cs`, `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.ConnectionSlots.cs`

- [ ] **RED.** `TheSlotSyncWaitsForTheTenantLock` en los dos gemelos. Toma `pg_advisory_xact_lock(20261112, hashtext('<tenantId>'))` desde otra conexión, lanza el renombre de un bot publicado y comprueba que no termina en 1,5 s. Al soltar el candado, responde 200. Esperado en RED: el renombre termina antes del plazo.
- [ ] **GREEN.** `ConnectionSlotsLock` con `Namespace = 20261112`, en la forma de dos claves. Es la primera sentencia de `SyncAgentsAsync` y de `SyncChatbotsAsync`, con la misma clave en los dos, así que un chatbot y un agente del mismo tenant también hacen fila. Se suelta con el commit o el rollback del unit of work. Se corrigen los comentarios que decían que el `ORDER BY` bastaba.
- [ ] **Commit:** `fix(ai): serialize connection slot sync per tenant`.

### Task 5: Validar el borrador sólo contra los ids de las conexiones

**Por qué.** Desde la Tarea 2, `ValidateAsync` llama a `IAiConnections.ListAsync`, y su adaptador corre `ListAllAsync` y `ListActiveAsync`. El segundo resuelve cada conexión activa, con su secreto. Eso pasa en cada autoguardado del borrador.

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentPorts.cs`, `src/Modules/Ai/Modules.Ai.Application/AgentEditorBuilder.cs`, `src/Bootstrapper/AiConnections.cs`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs`

- [ ] **RED.** `ValidatingReadsOnlyConnectionIds`: el doble cuenta las llamadas. Tras `ValidateAsync`, `ListAsync` va en 0 y `ListIdsAsync` en 1. Esperado en RED: `CS0535`/`CS1061`, porque el método no existe.
- [ ] **GREEN.** `IAiConnections.ListIdsAsync` devuelve un `IReadOnlySet<Guid>`, y el adaptador lo arma sólo con `ListAllAsync`. El `ValidateAsync` público lo usa. `BuildAsync` conserva su única lectura completa y saca los ids de ahí.
- [ ] **Commit:** `perf(ai): validate agent connections against ids only`.

---

## Task 6: Hallazgos de la revisión del bloque 4-5 (2026-11-13)

Cuatro hallazgos, un commit cada uno.

- **El borrado de una conexión se serializa con la sincronización.** El `FOR SHARE` de `IntegrationsConnectionBotOccupancy` no protegía nada: el handler no abría transacción, y un candado de fila no cubre la fila que una publicación todavía no inserta. Ahora `IIntegrationsUnitOfWork` expone `BeginTransactionAsync` (guardar confirma), `DeleteConnectionHandler` chequea y borra dentro de ella, y el adaptador toma `ConnectionSlotsLock` del tenant antes de leer `connection_bots`. Pruebas: `TheOccupancyCheckRunsInsideTheTransactionThatDeletes`, `AConflictDisposesTheTransactionWithoutSaving` (unitarias) y `AgentsApiTests.DeletingAConnectionWaitsForASlotSyncInFlight` (integración).
- **El catch de `IsWriteRace` tiene prueba** en `AiUnitOfWorkTests` y `AutomationsUnitOfWorkTests`: 40P01 y 40001, envueltos o sueltos, salen como `concurrency.conflict`; un 23503 no se traduce. En el motor, `RunProcessor` reprograma esa excepción a 2 s (`Retry`, gasta un intento): un deadlock ya no espera el lease ni traspasa un arranque manual a una persona. No empeora; queda documentado en la prueba.
- **El doble de ocupación responde por `(tenantId, connectionId)`**, con la prueba de que otra conexión u otro tenant no bloquean.
- **Gemelo del chatbot en pausa:** `ChatbotsApiTests.DeletingAConnectionAPausedChatbotServesIsRejected`. `Chatbot.Pause` no toca `chatbot_connections.active`, así que conserva la fila.

**Pendiente al mergear `develop`:** `ShopifyTokenLock.Namespace` de `develop` también es `20261112`, igual que `ConnectionSlotsLock.Namespace`. Las claves difieren (tenant contra conexión o instalación), así que sólo chocan si dos `hashtext` coinciden, y el efecto sería una espera de más, no un error. Igual conviene darle a uno de los dos otra constante al mergear.
