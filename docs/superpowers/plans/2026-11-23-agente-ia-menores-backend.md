# Agente IA: dos menores (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **El presupuesto de pruebas del workspace manda sobre el skill:** un solo bloque, un solo implementador, una revisión al final.

**Goal:** (M1) el `reschedule_appointment` simulado del playground comprueba el cambio de hora como lo hace el real, sin contar el cupo de la propia cita; (M2) un agente creado desde una plantilla guarda su id y el editor responde `sourceTemplateId` y `sourceTemplateName`.

**Architecture:** M1 agrega una comprobación sin candado al lado del núcleo real (`AppointmentRescheduling.CheckAsync`), la expone en `IChannelScheduling.CheckRescheduleAsync` y en el puerto de Ai (`IAiScheduling.PreviewRescheduleAsync`, adaptador en Bootstrapper), y la herramienta simulada la usa en vez de la vista previa de reservar. M2 es una columna nullable en `ai.agents` que sólo escribe `CreateAgentFromTemplateHandler`, y `AgentEditorBuilder` resuelve el nombre con `ISystemAgentTemplateCatalog` al leer.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-agente-ia-menores-design.md`

**Worktree:** `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/agent-minors`, rama `feature/agent-minors`. Antes de tocar archivos y antes de cada commit: `git branch --show-current` debe decir `feature/agent-minors`.

## Global Constraints

- Nombres del contrato, exactos: `AgentEditorDto.SourceTemplateId` / `SourceTemplateName` (`string?`), en JSON `sourceTemplateId` / `sourceTemplateName`. `AgentSummaryDto` **no** cambia. `AgentExportDto` **no** cambia.
- Columna: `ai.agents.source_template_id`, `character varying(64)` (`SystemAgentTemplate.IdMaxLength`), `NULL`, sin índice, sin FK, sin backfill. Migración `AddAgentSourceTemplate`, sólo `AddColumn` en `Up` y `DropColumn` en `Down`.
- Sólo `CreateAgentFromTemplateHandler` pasa `sourceTemplateId`. Crear en blanco, duplicar e importar quedan en `null`.
- `AppointmentRescheduling.RescheduleAsync` no cambia de comportamiento: la extracción mueve código sin reordenar nada (mismo candado, misma transacción, mismas excepciones).
- Ningún registro de DI cambia, así que `ArchitectureTests` no corre. Si un paso obliga a registrar algo, se corre `CompositionRootTests` y `AiLayerTests`.
- TDD con evidencia literal de RED y GREEN. RED por compilación: la salida del `dotnet build` del proyecto de pruebas, sin contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) mientras se itera, con `$env:Seed__Enabled = "false"`. Al final del bloque, una vez: `SchedulingChannelServiceTests` y `AgentTemplatesApiTests` enteras. Nunca dos corridas a la vez: antes de cada una, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, esperar, no detenerla. Pruebas en primer plano. Si `Api.exe` del developer bloquea el build, avisar; no detenerlo.
- Conventional commits **sin atribución de IA ni trailer `Co-Authored-By`**. Nunca a `main`. `docs/estado.md` no se toca en la rama.

## Review Focus

1. Mover una cita a un inicio que se solapa con su propio rango (servicio con margen) → la vista previa dice que se puede, igual que el real. `CheckRescheduleIgnoresTheSeatTheAppointmentHolds` (Tarea 1).
2. Un id de cita de otro cliente en el playground → `appointment_not_found`, sin describirla. `AnotherCustomersAppointmentIsNotFound` ampliada (Tarea 1) y `CancelWithoutCustomerIsNotFound` ampliada (Tarea 2).
3. La misma hora que ya tiene → `alreadyDone: true`, no `slot_taken`. `CheckRescheduleToTheSameTimeIsAlreadyDone` (Tarea 1) y `SimulatedRescheduleToTheSameTimeSaysAlreadyDone` (Tarea 2).
4. El cambio de hora real, después de la extracción → sigue igual. Las pruebas existentes de `SchedulingChannelServiceTests` (`RescheduleToTheSameTimeIsAlreadyDone`, `AgentRescheduleMarksTheHistoryByChannel`, `ARescheduleAfterAnotherMoveIsAConcurrencyConflict`, `AFailedChannelRescheduleLeavesNothingTracked`, `RescheduleInsideTheCutoffIsInvalidTransition`) en la corrida de la clase entera (Tarea 1).
5. Duplicar un agente que salió de una plantilla, o importar su archivo → `null`, nunca hereda el origen; una plantilla que ya no existe → el editor abre con el id y sin nombre. `AnAgentRemembersTheTemplateItCameFromAndNothingElseCopiesIt` (Tarea 3) y `ATemplateThatNoLongerExistsKeepsTheIdWithoutAName` (Tarea 3).

---

### Task 1: Scheduling — comprobar un cambio de hora sin escribir

**Files:**
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Application/AppointmentRescheduling.cs`
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Application/ChannelScheduling.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/AiSchedulingTests.cs` (sólo el doble `FakeChannelScheduling`, para que compile)
- Test: `tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests/SchedulingChannelServiceTests.cs`

**Interfaces:**
- Produces, en `AppointmentRescheduling`:
  - `public async Task<(Service Service, LoadedAvailability Loaded, SlotOption Option)> CheckAsync(Guid tenantId, Appointment appointment, ResourceScope scope, ResourceId? resourceId, DateTimeOffset startsAt, bool byClient, bool requireClientChoice, CancellationToken cancellationToken)` — `EnsureOpen`, y lo mismo que `RescheduleAsync` antes del candado; luego las sesiones activas de los candidatos sin la sesión que se libera, y `AppointmentStarts.PickFree(..., preferred: resourceId ?? appointment.ResourceId)`. Sin candado, sin `RefreshAsync`, sin escribir.
  - Dos privados extraídos del cuerpo actual de `RescheduleAsync`, que usan los dos: `RulesAsync(...)` → `(Service, LoadedAvailability, IReadOnlyList<ResourceId> Candidates)` (servicio, `LoadForStartAsync`, `EnsureClientMayMove` si `byClient`, `sameLocation`, `RulesCandidates`, `EnsureClientChoice`), y `SeatsWithoutTheReleasedOneAsync(Guid tenantId, LoadedAvailability loaded, IReadOnlyList<ResourceId> candidates, Appointment appointment, CancellationToken)` → `(Session OldSession, IReadOnlyList<Session> Fresh)` (la sesión vieja y el filtro `!(Id == old && BookedCount == 1 && !IsPublished)`). `RescheduleAsync` llama `SeatsWithoutTheReleasedOneAsync` en el mismo lugar donde hoy lee `oldSession` y `fresh` (después del segundo `LoadForStartAsync`, bajo el candado).
- Produces, en `ChannelScheduling.cs`:
  - `public sealed record ChannelRescheduleCheck(ChannelStartCheck Start, bool AlreadyDone);`
  - En `IChannelScheduling`: `Task<ChannelRescheduleCheck> CheckRescheduleAsync(Guid tenantId, Guid customerId, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);` con el `<summary>` «Lo que haría `RescheduleAsync`, sin candado ni escritura: la vista previa del playground (D-7).»
  - Implementación: `EnsureAsync` → `LoadOwnAsync` → `AppointmentRescheduling.EnsureOpen` → misma hora y (`requested` nulo o igual al recurso de la cita) ⇒ `AlreadyDone: true` con `Start` armado de `DescribeOneAsync` (estado, horas, zona, servicio, recurso y sede de la cita) → si no, `rescheduling.CheckAsync(tenantId, appointment, ResourceScope.All, requested, startsAt, byClient: true, requireClientChoice: true, ct)` y `Start` con `appointment.Status.ToString()`, las horas de `calendar.ToLocal`, el `DisplayName` del recurso elegido (`loaded.OfferedResources`) y el nombre de su sede (`locations.FindAsync`), como `CheckStartAsync`.
- Produces, en `FakeChannelScheduling` (Bootstrapper.UnitTests): `public (Guid CustomerId, Guid AppointmentId)? LastRescheduleCheck { get; private set; }` y `CheckRescheduleAsync` que llama `MaybeThrow()`, lo anota y devuelve `new ChannelRescheduleCheck(new ChannelStartCheck("Confirmed", Stored.StartsAt, Stored.EndsAt, "America/Bogota", "Consulta", "Dra. Pérez", "Sede"), false)`.

- [ ] **Step 1: Escribir las pruebas que fallan** (en `SchedulingChannelServiceTests`)
  - Ampliar los ayudantes sin romper a nadie: `CreateServiceAsync(..., int duration = 30, int buffer = 0)` (manda `bufferMinutes = buffer`) y `BookSceneAsync(bool allowClientChoice = true, bool bookableByChannel = true, int duration = 30, int buffer = 0)`.
  - `CheckRescheduleIgnoresTheSeatTheAppointmentHolds`: escena con `duration: 30, buffer: 30`. Antes de reservar, `target` = el primer inicio después de las 09:00 y antes de las 10:00 que `SearchAvailabilityAsync(tenant, service, day, day, scene.Resource, 50)` ofrece. Reservar a las 09:00 en `scene.Resource`. Luego:
    - Guarda (prueba que la escena reproduce el error): `CheckStartAsync(tenant, service, target, scene.Resource)` lanza `ResourceConflictException` con `Code == SchedulingErrorCodes.SlotTaken`.
    - `CheckRescheduleAsync(tenant, scene.Customer, id, target, scene.Resource)` → `AlreadyDone == false`, `Start.StartsAt` con la hora de `target`, `Start.Status == "Confirmed"`, `Start.ResourceName == "Dra. Torres"`.
    - La versión de la cita (`ReadAppointmentAsync`) no cambió.
    - Paridad: `RescheduleAsync(tenant, agent, scene.Customer, id, target, scene.Resource)` sale bien (`AlreadyDone == false`).
    - Si ningún inicio de la escena cae en (09:00, 10:00), subir `buffer` hasta que exista; la guarda es la que prueba el escenario, no la hora elegida.
  - `CheckRescheduleToTheSameTimeIsAlreadyDone`: reservar a las 09:00 (`key-check-same`), `CheckRescheduleAsync(..., At(scene, 9), null)` → `AlreadyDone == true`, `Start.StartsAt.Hour == 9`; la versión no cambió.
  - `AnotherCustomersAppointmentIsNotFound`: sumar `var check = await Assert.ThrowsAsync<ResourceNotFoundException>(() => WithServiceAsync(channel => channel.CheckRescheduleAsync(scene.Tenant.Id, stranger, id, At(scene, 10), null, Ct)));` y su `Code` a la tupla del `Assert.Equal`.
  - `EveryReadWithTheModuleOffIsForbidden`: sumar `channel => channel.CheckRescheduleAsync(tenant.Id, id, id, day.ToDateTime(new TimeOnly(9, 0)), null, Ct)` al arreglo.

- [ ] **Step 2: RED por compilación** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests`. Expected: `CS1061: 'IChannelScheduling' does not contain a definition for 'CheckRescheduleAsync'`. Sin contenedores.

- [ ] **Step 3: Implementar** `CheckAsync`, los dos privados y `CheckRescheduleAsync` con las firmas de arriba, y el miembro del doble `FakeChannelScheduling`.

- [ ] **Step 4: GREEN filtrado** — `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests --filter "FullyQualifiedName~CheckReschedule|FullyQualifiedName~AnotherCustomersAppointmentIsNotFound|FullyQualifiedName~EveryReadWithTheModuleOffIsForbidden"` → pasan. Y `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` compila.

- [ ] **Step 5: Commit**
  ```powershell
  git add src/Modules/Scheduling/Modules.Scheduling.Application/AppointmentRescheduling.cs src/Modules/Scheduling/Modules.Scheduling.Application/ChannelScheduling.cs tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests/SchedulingChannelServiceTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/AiSchedulingTests.cs
  git commit -m "feat(scheduling): check a channel reschedule without counting the appointment's own seat"
  ```

### Task 2: Ai — la herramienta simulada usa la comprobación del cambio de hora

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentSchedulingTools.cs`
- Modify: `src/Bootstrapper/AiScheduling.cs`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentSchedulingToolsTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/AiSchedulingTests.cs`
- Modify: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs` (el doble `FakeScheduling`: el miembro nuevo lanza `NotSupportedException`, como los demás)

**Interfaces:**
- Consumes: `IChannelScheduling.CheckRescheduleAsync` y `ChannelRescheduleCheck` (Tarea 1).
- Produces, en `AgentSchedulingTools.cs`:
  - `public sealed record AiReschedulePreview(AiStartPreview Start, bool AlreadyDone);` con el `<summary>` «Lo que daría mover esa cita, sin escribir: la herramienta simulada del playground (D-7).»
  - En `IAiScheduling`: `Task<AiSchedulingResult<AiReschedulePreview>> PreviewRescheduleAsync(AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken);`
- Produces, en `AiScheduling` (Bootstrapper): `PreviewRescheduleAsync` dentro de `GuardAsync`; sin cliente (`ReadingCustomerAsync` nulo) ⇒ `Error(AgentToolErrors.AppointmentNotFound)` sin llamar al canal; con cliente ⇒ `channel.CheckRescheduleAsync(caller.TenantId, customerId, appointmentId, localStartsAt, resourceId, ct)` y lo traduce a `AiReschedulePreview`.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - En `FakeAiScheduling` (`AgentSchedulingToolsTests`): `public int RescheduleCheckCalls { get; private set; }`, `public (Guid AppointmentId, DateTime Start, Guid? ResourceId)? LastRescheduleCheck { get; private set; }` y `public AiSchedulingResult<AiReschedulePreview> ReschedulePreviewResult { get; set; }` con valor inicial `Ok(new AiReschedulePreview(<el mismo AiStartPreview del PreviewResult inicial>, false))`. `PreviewRescheduleAsync` suma `Calls`, `RescheduleCheckCalls` y anota.
  - Renombrar `SimulatedRescheduleChecksTheNewStartWithTheAppointmentsService` a `SimulatedRescheduleChecksTheMoveOfThatAppointment`: `Assert.False(result.IsError)`; `Assert.Equal((Appointment, new DateTime(2026, 11, 25, 10, 0, 0), (Guid?)null), _scheduling.LastRescheduleCheck)`; `Assert.Equal((1, 0, 0), (_scheduling.RescheduleCheckCalls, _scheduling.PreviewCalls, _scheduling.RescheduleCalls))`.
  - `SimulatedRescheduleToTheSameTimeSaysAlreadyDone`: `ReschedulePreviewResult` con `AlreadyDone: true` → el JSON del resultado tiene `"alreadyDone":true`.
  - `SimulatedRescheduleOfAServiceWithoutChatBookingHandsOff`: cambiar el último assert a `Assert.Equal(0, _scheduling.RescheduleCheckCalls)`.
  - `AiSchedulingTests.PreviewRescheduleChecksTheMoveForTheConversationCustomer`: con la conversación del cliente `CustomerA` (el `_row` por defecto de la clase), `PreviewRescheduleAsync(Caller(ConversationId), AppointmentId, Start, null, Ct)` → `Value.AlreadyDone == false`, `_channel.LastRescheduleCheck == (CustomerA, AppointmentId)`, `_channel.WriteCalls == 0`.
  - `AiSchedulingTests.CancelWithoutCustomerIsNotFound`: sumar `var preview = await adapter.PreviewRescheduleAsync(Caller(ConversationId), AppointmentId, Start, null, Ct);` y `preview.ErrorCode` al `Assert.All`; `_channel.LastRescheduleCheck` sigue `null`.

- [ ] **Step 2: RED por compilación** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests` y `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Expected: `CS0246: The type or namespace name 'AiReschedulePreview' could not be found` y `CS1061 … 'PreviewRescheduleAsync'`.

- [ ] **Step 3: Implementar** el record, el miembro del puerto, el adaptador y, en `SimulatedRescheduleAppointmentTool.ExecuteAsync`, cambiar la llamada a `scheduling.PreviewBookingAsync(caller, appointment.ServiceId, …)` por `scheduling.PreviewRescheduleAsync(caller, appointment.AppointmentId, startsAt, resourceId, cancellationToken)`; el mapeo toma `value.Start` para las horas y los nombres y `["alreadyDone"] = value.AlreadyDone`. `["status"]` sigue siendo `appointment.Status`. Actualizar el `<summary>` de la herramienta: el inicio de una cita real se comprueba como lo haría el cambio de hora (misma sede, sin su propio cupo), no como una reserva.

- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentSchedulingToolsTests|FullyQualifiedName~AgentEditorBuilderTests"` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AiSchedulingTests"` → pasan.

- [ ] **Step 5: Commit**
  ```powershell
  git add src/Modules/Ai/Modules.Ai.Application/AgentSchedulingTools.cs src/Bootstrapper/AiScheduling.cs tests/Modules/Ai/Modules.Ai.UnitTests/AgentSchedulingToolsTests.cs tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/AiSchedulingTests.cs
  git commit -m "fix(ai): preview the playground reschedule as a move, not as a new booking"
  ```

### Task 3: Ai — la plantilla de origen del agente

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Domain/Agent.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/Persistence/AiDbContext.cs` (`ConfigureAgent`)
- Create: `src/Modules/Ai/Modules.Ai.Infrastructure/Persistence/Migrations/<timestamp>_AddAgentSourceTemplate.cs` (+ `.Designer.cs`), Modify: `AiDbContextModelSnapshot.cs` (los genera `dotnet ef`)
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentTemplates.cs` (`CreateAgentFromTemplateHandler`)
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentDtos.cs`, `src/Modules/Ai/Modules.Ai.Application/AgentEditorBuilder.cs`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentEditorBuilderTests.cs`, `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentTemplatesApiTests.cs`

**Interfaces:**
- Produces:
  - `Agent.Create(Guid tenantId, string name, AgentConfig config, Guid by, DateTimeOffset now, string? sourceTemplateId = null)` y `public string? SourceTemplateId { get; private set; }` con el `<summary>` «La plantilla del sistema de la que salió (spec 2026-11-23 menores, M2). Sólo la pone crear desde plantilla; duplicar e importar no la copian. No cambia nunca.»
  - EF: `agent.Property(value => value.SourceTemplateId).HasColumnName("source_template_id").HasMaxLength(SystemAgentTemplate.IdMaxLength);`
  - `AgentEditorDto(..., AgentSchedulingReadinessDto Scheduling, string? SourceTemplateId, string? SourceTemplateName)` — los dos al final, con `<param>` que diga que el nombre se resuelve al leer y es `null` si la plantilla ya no existe.
  - `AgentEditorBuilder(..., IAiScheduling scheduling, ISystemAgentTemplateCatalog templates)`; en `BuildAsync`: `agent.SourceTemplateId` y `agent.SourceTemplateId is { } id ? templates.Find(id)?.Name : null`.
  - `CreateAgentFromTemplateHandler`: `Agent.Create(..., now, sourceTemplateId: template.Id)`.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `AgentEditorBuilderTests`: doble `private sealed class FakeTemplates(params SystemAgentTemplate[] templates) : ISystemAgentTemplateCatalog` (`All` = `templates`, `Find` ordinal); `Builder(...)` gana `ISystemAgentTemplateCatalog? templates = null` → `templates ?? new FakeTemplates()`.
    - `TheEditorNamesTheTemplateTheAgentCameFrom`: agente con `sourceTemplateId: "asistente-de-citas"`, catálogo con `new SystemAgentTemplate("asistente-de-citas", "Asistente de citas", "Reserva citas.", [], "{}")` → `("asistente-de-citas", "Asistente de citas") == (dto.SourceTemplateId, dto.SourceTemplateName)`.
    - `ATemplateThatNoLongerExistsKeepsTheIdWithoutAName`: mismo agente, catálogo vacío → `("asistente-de-citas", null)`.
    - `AnAgentWithoutATemplateHasNoSource`: `NewAgent()` → `(null, null)`.
  - `AgentTemplatesApiTests.AnAgentRemembersTheTemplateItCameFromAndNothingElseCopiesIt` (host propio, como las demás de la clase):
    - `POST {FromTemplate}` con `{ templateId = "asistente-de-citas", name = "Citas" }` → `201`, `sourceTemplateId == "asistente-de-citas"`, `sourceTemplateName == "Asistente de citas"`.
    - `GET {AiUrl()}/agents/{id}` → los mismos dos valores.
    - `ScalarAsync<string>(cs, "SELECT source_template_id FROM ai.agents WHERE id = @id", ("id", id))` → `"asistente-de-citas"`.
    - `GET {AiUrl()}/agents/{id}/export` → el JSON no tiene la propiedad `sourceTemplateId` (`TryGetProperty` falso).
    - `POST {AiUrl()}/agents` con `{ name = "Citas copia", source = new { kind = "duplicate", agentId = id } }` → `201`, `sourceTemplateId` y `sourceTemplateName` en `JsonValueKind.Null`.
    - `POST {AiUrl()}/agents/import` con `{ name = "Citas importado", export }` (el archivo del paso anterior) → `201`, los dos en `JsonValueKind.Null`.

- [ ] **Step 2: RED por compilación** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Expected: `CS1739: The best overload for 'Create' does not have a parameter named 'sourceTemplateId'` y `CS1061 … 'SourceTemplateId'`. La integración no se corre en RED.

- [ ] **Step 3: Implementar** dominio, EF, DTO, builder y handler con las firmas de arriba. Luego generar la migración:
  ```powershell
  dotnet ef migrations add AddAgentSourceTemplate --project src/Modules/Ai/Modules.Ai.Infrastructure --context AiDbContext -o Persistence/Migrations
  ```
  Revisar a mano: `Up` es **sólo** `AddColumn<string>(name: "source_template_id", schema: "ai", table: "agents", type: "character varying(64)", maxLength: 64, nullable: true)`; `Down` sólo el `DropColumn`; el snapshot suma esa propiedad y nada más. Si aparece otra diferencia, el modelo tenía cambios pendientes de antes: parar y avisar.

- [ ] **Step 4: GREEN**
  - `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentEditorBuilderTests"` → pasan.
  - `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AnAgentRemembersTheTemplateItCameFromAndNothingElseCopiesIt"` → pasa.

- [ ] **Step 5: Commit**
  ```powershell
  git add src/Modules/Ai tests/Modules/Ai
  git commit -m "feat(ai): remember the system template an agent was created from"
  ```

### Cierre del bloque (una vez)

- [ ] `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests` y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests` → pasan enteras.
- [ ] De a una, con `$env:Seed__Enabled = "false"` y mirando antes `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`:
  `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests --filter "FullyQualifiedName~SchedulingChannelServiceTests" -- xUnit.MaxParallelThreads=4` y
  `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~AgentTemplatesApiTests" -- xUnit.MaxParallelThreads=4` → pasan. La primera es la que prueba que el cambio de hora real no se movió (Review Focus 4).
- [ ] Revisión fuerte de toda la rama, una sola vez, al final.
