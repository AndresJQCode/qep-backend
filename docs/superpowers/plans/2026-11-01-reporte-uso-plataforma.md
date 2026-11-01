# Reporte de uso de la plataforma: plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pestaña «Uso» en la consola de plataforma con tenants registrados, conversaciones por tenant y canal, y conectados ahora.

**Architecture:** Tenancy.Application declara el query, su handler y el puerto `IPlatformUsageSource`; el handler calcula totales, exclusión y orden en memoria. Bootstrapper adapta el puerto con SQL crudo sobre `tenancy.tenants` y `messaging.*`, y con el registro de presencia que `MessagingHub` alimenta. El frontend agrega la ruta `/operator/usage` a `features/operator`.

**Tech Stack:** .NET 10, Npgsql/EF Core, SignalR, xUnit, Testcontainers; React 19, TanStack Router y Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-01-reporte-uso-plataforma-design.md`

## Global Constraints

- Permiso `operator.usage.read`, sólo en el rol `admin`. Categoría `Operator`, `RequiredModules: []`.
- Ruta `GET /api/v1/tenants/{tenantId:guid}/operator/usage?period=day|week|month`; `period` ausente vale `month`.
- Períodos en `America/Bogota` para todos los tenants; la semana empieza el lunes y el mes el día 1.
- Una conversación cuenta sólo con un mensaje `direction = 1` y `NOT imported` en el rango, medido sobre `created_at`.
- El tenant operador no sale en la tabla ni en los totales. Los suspendidos sí.
- Sin migraciones ni índices. Si `EXPLAIN ANALYZE` del mes supera 2 segundos en local, se reporta al owner.
- Textos en español con tuteo. Commits convencionales sin atribución de IA. Nunca a `main`.
- `$env:Seed__Enabled = "false"` para las pruebas de integración. Por tarea, sólo las clases tocadas.

## Review Focus

- Reconexión: la conexión nueva llega antes que el cierre de la vieja; el miembro sigue conectado.
- Miembro conectado cuya membresía se quitó después: sale con el nombre disponible, y sin nombre como «Miembro eliminado».
- Conexión de un tenant que no está en la lista (stub con tenant simulado): se ignora sin fallar.
- Semana que empieza en el mes anterior: el total de semana puede superar al de mes y no es un error de pantalla.
- Tenant solo con Instagram o con conexión borrada: sus conversaciones cuentan por `messaging.conversations.channel`, sin pasar por la conexión.

---

## Backend (`qep-backend`, rama `feature/reporte-uso-plataforma` desde `develop`)

### Task 1: Registro de presencia y hub

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPresence.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/Realtime/MessagingHub.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (singleton junto a `AddMessagingInfrastructure`, línea 1088)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingPresenceTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingHubTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IMessagingPresence
  {
      void Connected(string connectionId, Guid tenantId, Guid memberId);
      void Disconnected(string connectionId);
      IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Snapshot(); // tenantId → miembros sin repetir
  }
  ```
  `public sealed class MessagingPresence : IMessagingPresence`, con un `ConcurrentDictionary` por `connectionId`. `Disconnected` de un id desconocido no hace nada.

- [ ] **Step 1: Escribir las pruebas que fallan**

  Unitarias: `TwoConnectionsOfOneMemberCountOnce`, `ClosingOneConnectionKeepsTheMember`, `ClosingTheLastConnectionRemovesTheMember`, `ClosingAnUnknownConnectionDoesNothing`, `ANewConnectionBeforeTheOldOneClosesKeepsTheMember`.
  Integración: `AConnectedMemberAppearsInPresenceAndLeavesOnStop` y `ARejectedConnectionIsNotRegistered` (sin módulo). `OnConnectedAsync` corre después del `StartAsync` del cliente, así que se espera con la sonda de grupo de ese archivo, no con un `Delay`.
- [ ] **Step 2: Correr y ver el fallo**

  Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter MessagingPresenceTests`
  Expected: error de compilación, `IMessagingPresence` no existe.
- [ ] **Step 3: Implementar**

  Inyectar `IMessagingPresence` en `MessagingHub`. Llamar `Connected` después de `base.OnConnectedAsync()`, y agregar `OnDisconnectedAsync` que llama `Disconnected(Context.ConnectionId)` y luego a la base. Comentar en el registro que es por proceso y vale con `replicas: 1`.
- [ ] **Step 4: Correr las dos clases**

  Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter MessagingHubTests`
  Expected: PASS.
- [ ] **Step 5: Commit** `feat(messaging): track who holds an open inbox connection`

### Task 2: Permiso, query y handler

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorPermissions.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/GetPlatformUsage.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin`, 1512; `PermissionDefinition`, 2010; política, 2529; registro del handler, 115)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/GetPlatformUsageHandlerTests.cs`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public const string UsageRead = "operator.usage.read"; // en OperatorPermissions
  public sealed record GetPlatformUsageQuery(TenantId TenantId, string? Period) : IQuery<PlatformUsageDto>;
  public sealed record PlatformUsageTenantRow(Guid TenantId, string Name, DateTimeOffset CreatedAt, int WhatsApp, int Instagram, IReadOnlyList<PlatformConnectedMemberDto> Connected);
  public interface IPlatformUsageSource { Task<IReadOnlyList<PlatformUsageTenantRow>> ListAsync(DateTimeOffset startUtc, CancellationToken cancellationToken); }
  public sealed record PlatformConnectedMemberDto(Guid MemberId, string Name);
  public sealed record PlatformConversationCountsDto(int WhatsApp, int Instagram, int Total);
  public sealed record PlatformUsageTenantDto(Guid TenantId, string Name, PlatformConversationCountsDto Conversations, IReadOnlyList<PlatformConnectedMemberDto> Connected);
  public sealed record PlatformRegisteredCountsDto(int Day, int Week, int Month);
  public sealed record PlatformUsageDto(PlatformRegisteredCountsDto Registered, string Period, IReadOnlyList<PlatformUsageTenantDto> Tenants);
  ```

- [ ] **Step 1: Escribir las pruebas que fallan**

  Reloj fijo `2026-11-04T15:00:00Z` (miércoles): día desde `2026-11-04T05:00Z`, semana desde `2026-11-02T05:00Z`, mes desde `2026-11-01T05:00Z`.
  - `AWrongTenantMissingPermissionOrNonOperatorIsForbidden` (`[Theory]`, los tres casos).
  - `AnUnknownPeriodIsAValidationError`, con la clave `Period` en `errors`.
  - `TheSourceReceivesTheStartOfTheChosenPeriod`, para los tres valores y para `null` → mes.
  - `RegisteredCountsIgnoreThePeriod`, con un tenant creado el `2026-11-01T04:59Z`, que no cuenta en el mes.
  - `AWeekStartingInThePreviousMonthCountsMoreThanTheMonth`, con reloj `2026-11-01T15:00Z`: la semana inicia el lunes `2026-10-26`.
  - `TheOperatorTenantIsLeftOut`, `TenantsAtZeroStay`, `OrderIsTotalDescendingThenName`, `TotalSumsBothChannels`.
  - En `CompositionRootTests`: `OnlyTheAdminRoleCarriesTheUsagePermission`, copia de la de ciudades.
- [ ] **Step 2: Correr y ver el fallo**

  Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter GetPlatformUsageHandlerTests`
  Expected: error de compilación.
- [ ] **Step 3: Implementar `GetPlatformUsageHandler(IPlatformUsageSource, IExecutionContext, IOperatorTenant, IClock)`**

  Primero `OperatorAuthorization.EnsureAuthorized(..., OperatorPermissions.UsageRead, ...)`. Los cortes salen de `new TenantCalendar(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"))` y `StartOfDayUtc`. El validador de FluentValidation acepta `day`, `week`, `month` o `null`. El `Registered` se cuenta sobre las filas ya sin el operador, comparando `CreatedAt >= inicio`. Un tenant creado en la ventana cuenta aunque el corte que recibe la fuente sea otro: `CreatedAt` no depende de él.
- [ ] **Step 4: Correr las dos clases** Expected: PASS.
- [ ] **Step 5: Commit** `feat(tenancy): add the platform usage query and its permission`

### Task 3: Fuente SQL y endpoint

**Files:**
- Create: `src/Bootstrapper/PlatformUsageSource.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`AddScoped<IPlatformUsageSource, PlatformUsageSource>`, junto a la línea 1378)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/PlatformUsageApiTests.cs`

**Interfaces:**
- Consumes: `IMessagingPresence.Snapshot()`, `IPlatformUsageSource`, `IMessagingMemberNames.FindAsync(tenantId, ids, ct)`.
- Produces: `GET .../operator/usage` con `.RequireAuthorization(OperatorPermissions.UsageRead)`, 403 y 422.

- [ ] **Step 1: Escribir las pruebas que fallan**

  `QepApiFactory(connectionString, operatorTenantId: ...)`, con mensajes sembrados por `MessagingApiHarness` y SQL. Todas contra el esquema migrado:
  `AnInboundCountsItsConversationOnce`, `AnOutboundWithoutInboundDoesNotCount` (campaña, API y asesor), `AnImportedInboundDoesNotCount`, `AnEventRowDoesNotCount`, `AnOldConversationWithANewInboundCounts`, `AnInboundBeforeThePeriodDoesNotCount`, `ChannelsAreSeparated`, `AnInstagramConversationCountsAfterItsConnectionIsDeleted`, `ATenantAtZeroStays`, `ConnectedMembersComeWithTheirNames`, `ARemovedMemberStaysAsMiembroEliminado`, `AnUnknownTenantInPresenceIsIgnored`, `AClientTenantIsForbidden`, `WithoutThePermissionIsForbidden`.
- [ ] **Step 2: Correr y ver el fallo**

  Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter PlatformUsageApiTests`
  Expected: 404 o error de compilación.
- [ ] **Step 3: Implementar `PlatformUsageSource`**

  Consulta única, en la conexión del `MessagingDbContext`:
  ```sql
  WITH active AS (
      SELECT m.tenant_id, m.conversation_id FROM messaging.messages m
      WHERE m.direction = 1 AND NOT m.imported AND m.created_at >= @start
      GROUP BY 1, 2)
  SELECT t.id, t.display_name, t.created_at,
         count(*) FILTER (WHERE c.channel = 'WhatsApp')::int,
         count(*) FILTER (WHERE c.channel = 'Instagram')::int
  FROM tenancy.tenants t
  LEFT JOIN active a ON a.tenant_id = t.id
  LEFT JOIN messaging.conversations c ON c.id = a.conversation_id
  GROUP BY t.id, t.display_name, t.created_at
  ```
  Con `LEFT JOIN`, `count(*) FILTER` da 0 cuando `c` es nulo. Los nombres salen de `IMessagingMemberNames.FindAsync` por tenant con conectados; el que falta es «Miembro eliminado». Un tenant de la presencia sin fila se ignora. La nota de clase avisa que lee por nombre.
- [ ] **Step 4: Correr la clase** Expected: PASS.
- [ ] **Step 5: Medir**

  Correr `EXPLAIN ANALYZE` del mes en la base local con `docker exec postgres18 psql`. Expected: menos de 2 segundos. Si no, reportar al owner y no agregar índice.
- [ ] **Step 6: Documentar y commit**

  Agregar a `CLAUDE.md` dos gotchas: `PlatformUsageSource` lee por nombre y qué prueba lo amarra, y la presencia es por proceso, condición para subir `replicas`. Agregar el endpoint al README.
  Commit: `feat(operator): expose the platform usage report`

## Frontend (`qep-frontend`, rama `feature/reporte-uso-plataforma` desde `develop`)

### Task 4: Tipos, API y hook

**Files:**
- Modify: `src/features/operator/types/operator.ts`
- Create: `src/features/operator/services/operator-usage.api.ts`, `src/features/operator/hooks/use-platform-usage.ts`
- Test: `src/features/operator/services/operator-usage.api.test.ts`, `src/features/operator/hooks/use-platform-usage.test.tsx`

**Interfaces:**
- Produces: `OPERATOR_PERMISSIONS.usageRead = 'operator.usage.read'`; `type PlatformUsagePeriod = 'day' | 'week' | 'month'`; `type PlatformUsage`, que refleja el JSON del spec; `fetchPlatformUsage(tenantId, period)`; `usePlatformUsage(period)` que devuelve `{ usage, status, retry }`, igual que `useOperatorTenants`.

- [ ] **Step 1: Escribir las pruebas que fallan:** una forma inválida lanza `OperatorPayloadError`; la clave de query empieza por `['operator', tenantId, 'usage', period]`; `refetchOnWindowFocus: true`; `keepPreviousData` al cambiar de período.
- [ ] **Step 2: Correr y ver el fallo**

  Run: `bun run test --run src/features/operator/services/operator-usage.api.test.ts`
  Expected: FAIL, módulo no encontrado.
- [ ] **Step 3: Implementar** siguiendo `operator.api.ts` y `use-operator-tenants.ts`.
- [ ] **Step 4: Correr** Expected: PASS.
- [ ] **Step 5: Commit** `feat(operator): read the platform usage report`

### Task 5: Página, pestaña y ruta

**Files:**
- Create: `src/features/operator/pages/operator-usage-page.tsx`, `src/features/operator/components/platform-usage-table.tsx`, `src/routes/_authenticated/operator/usage.tsx`
- Modify: `src/features/operator/components/operator-console-tabs.tsx`, `src/features/operator/pages/operator-tenants-page.tsx`, `src/features/operator/pages/operator-cities-shell.tsx`, `src/routes/_authenticated/operator/tenants/index.tsx`, `src/routes/_authenticated/operator/cities.tsx`
- Test: `src/features/operator/pages/operator-usage-page.test.tsx`, `src/routes/_authenticated/operator/usage.test.tsx`

**Interfaces:**
- Consumes: `usePlatformUsage`, `OperatorReadGate`.
- Produces: prop `canReadUsage: boolean` en `OperatorConsoleTabs` y en las dos páginas que lo usan. `canReadCities` también pasa a ser real en la pantalla de Uso.

- [ ] **Step 1: Escribir las pruebas que fallan:** `drawsTheUsageTabOnlyWithItsPermission`; `theSelectorChangesTheTableAndNotTheTotals`; `opensOnMonth`; `aRowWithConnectedExpandsToTheirNames`; `aRowWithoutConnectedDoesNotExpand`; `showsTheEmptyState`; `showsTheErrorStateWithRetry`; `aRefreshButtonRefetches`; la ruta niega con «No tienes permiso para ver el uso de la plataforma.».
- [ ] **Step 2: Correr y ver el fallo**

  Run: `bun run test --run src/features/operator/pages/operator-usage-page.test.tsx`
  Expected: FAIL.
- [ ] **Step 3: Implementar** con la organización de la opción B: tres tarjetas, selector Hoy, Semana y Mes, y tabla Tenant, WhatsApp, Instagram, Total, Conectados. Los números van con el formateador regional que ya use la feature. La ruta es contenedor; la página es presentacional.
- [ ] **Step 4: Correr `bun run test --run src/features/operator` y `bun run build`** Expected: PASS y `routeTree.gen.ts` regenerado.
- [ ] **Step 5: Commit** `feat(operator): add the platform usage tab`

### Task 6: Cierre

- [ ] **Step 1:** Correr una vez las clases de integración tocadas y las de Tenancy por nombre.
- [ ] **Step 2:** Probar en local con auth real: abrir la bandeja en una pestaña y la consola en otra, y ver al usuario conectado.
- [ ] **Step 3:** Mergear a `develop` en los dos repos cuando el owner lo pida y actualizar `docs/estado.md` en ese merge. Desplegar primero el backend.
