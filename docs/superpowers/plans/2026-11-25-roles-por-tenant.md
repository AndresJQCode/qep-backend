# Roles por tenant: `admin` como único rol de sistema — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este repo y del workspace mandan sobre el proceso del skill** (dos bloques, un implementador y una revisión por bloque; ver «Global Constraints»).

**Goal:** Que `admin` sea el único rol de sistema y que `advisor`, `billing`, `cashier`, `scheduling_manager` y `scheduling_provider` vivan como filas editables y borrables de `authorization.roles` en cada tenant, con los mismos permisos de hoy, sembradas al dar de alta el tenant y reconciliadas al arrancar.

**Architecture:** Las cinco `RoleDefinition` pasan a ser `RoleTemplate` (otro tipo, registrado en DI, que ningún catálogo lee). `TenantRoleSeeder` (Authorization.Application) crea en un tenant las plantillas cuya clave no tenga y escribe una marca en `authorization.role_seeds`, todo en un `SaveChanges`; con marca no hace nada. Se llama desde un segundo `INewTenantProvisioning` (alta por signup e invitación; un fallo se registra como `Error`) y desde un paso nuevo dentro de `RunUnderStartupLockAsync`, después de `RunQepSeedAsync` (un fallo detiene el arranque). Borrar un rol pasa a exigir que ninguna membresía `Invited`, `Active` o `Suspended` lo tenga.

**Tech Stack:** .NET 10, EF Core + Npgsql, CQ propio, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-25-roles-por-tenant-design.md` (commit `870dcec3d`, aprobado por el owner). Sus decisiones no se reabren.

## Global Constraints

- **Worktree y rama:** `feature/roles-por-tenant` (de `develop` en `de3a750ca`), en `..\qep-backend-worktrees\roles-por-tenant`. Nada se edita en el checkout `qep-backend` ni en otros worktrees. `git branch --show-current` y `git status --short` **antes de editar y antes de cada commit**. Un subagente que corra scripts hace `Set-Location` al worktree primero. Edita con las herramientas de edición, no con `Set-Content` (rompe las tildes).
- **Commits:** convencionales, uno por tarea, con rutas explícitas (`git commit -m "<msg>" -- <rutas>`), **sin** `Co-Authored-By` ni ninguna atribución de IA, aunque el entorno la sugiera.
- **TDD:** RED antes que GREEN, con la salida literal de ambos en el reporte de la tarea. Un RED que falla por compilación se prueba con `dotnet build` del proyecto de pruebas, **sin** contenedores. Una prueba verde al primer intento se reporta como caracterización.
- **Bloques:** dos bloques, un implementador (Sonnet) por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una sola vez, al final.
- **Integración:** mientras iteras, filtrada por nombre de prueba (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); cada clase tocada completa **una vez**, al final del bloque (en el Bloque 2 eso lo cubre `.\scripts\test-feature.ps1`). Antes de cada corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una de otra sesión, espera y **nunca** la detengas. Nunca dos corridas de integración a la vez. Las pruebas corren en **primer plano**. Cada corrida va precedida, en línea aparte, de `$env:Seed__Enabled = "false"`, y lleva `-- xUnit.MaxParallelThreads=4`. Si `Api.exe` está corriendo, el build falla por archivo bloqueado: avisa, no lo detengas.
- **Para mergear a `develop`:** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos. `docs/estado.md` **no** se toca en la rama. Sólo merge a `develop`, nunca a `main`, sin push.
- **Comandos:** Windows PowerShell 5.1 (`A; if ($?) { B }`, nada de `&&`).
- **Idioma:** comentarios, XML doc y textos para personas en español colombiano tuteando; identificadores, códigos y mensajes de commit en inglés. Los mensajes de excepción siguen en inglés como en el resto del módulo.
- **Migración:** una sola, `AddRoleSeeds` (Authorization), **aditiva** (crea una tabla). Se genera con
  `dotnet ef migrations add AddRoleSeeds --project src/Modules/Authorization/Modules.Authorization.Infrastructure --context AuthorizationDbContext -o Persistence/Migrations`.
- **Arranque:** el paso nuevo va **dentro** del lambda de `RunUnderStartupLockAsync` en `src/Api/Program.cs`, después de `RunQepSeedAsync` (`QepSeedRunner` crea tenants). No se agrega ningún hosted service ni singleton con estado: `MultiReplicaInventory` no cambia. Si la implementación terminara necesitando uno, se clasifica ahí en el mismo commit.
- **Ninguna consulta nueva en la ruta de cada request.** La siembra corre en el alta y al arrancar, nunca al resolver permisos (`RequestQueryBudgetTests` fija ≤ 8 consultas en frío y ≤ 5 en caliente).
- **Valores fijos del spec:** plantillas con clave, nombre, descripción y permisos **idénticos** a los de hoy en `QepServiceCollectionExtensions.cs:3127-3290` (`advisor` «Asesor», `billing` «Facturación», `cashier` «Cajero», `scheduling_manager` «Gestor de agenda», `scheduling_provider` «Profesional de agenda»); `Role.KeyShape = ^[a-z0-9_-]{1,32}$`; `SystemRoleKeys.All = { "admin" }`; código del borrado bloqueado `authorization.role.in_use` (ya existe).
- **No cambian:** `EnsureSomebodyKeepsManagingAsync` (sigue mirando sólo el catálogo de sistema), `ActiveRoleSetsAsync` (sigue contando sólo activas), el frontend.
- **Verificado contra el código (`de3a750ca`):** las cinco plantillas ya son subconjunto de `admin` (comparación de las listas de `QepServiceCollectionExtensions.cs`); ningún código de `src/` decide por una de las cinco claves salvo su propia definición; `TenantRegistrationService` recibe hoy **un** `INewTenantProvisioning` (`TenantRegistrationService.cs:17`) y la única implementación es `QuotationsNewTenantProvisioning` (`QepServiceCollectionExtensions.cs:1635`).

## Decisiones del plan donde el spec deja criterio

- **Nombres:** `RoleTemplate`, `RoleTemplateKeys`, `ITenantRoleSeeder`/`TenantRoleSeeder`, `ITenantRoleSeedStore`, `IRoleSeedTenantSource`, tabla `authorization.role_seeds` (`tenant_id` PK `PK_role_seeds`, `seeded_at`).
- **Puerto para listar tenants al arrancar:** `IRoleSeedTenantSource` en Authorization.Application, implementado en Bootstrapper leyendo `TenancyDbContext` (Authorization no lee Tenancy salvo por Bootstrapper; mismo patrón que `HrMemberDirectory`).
- **Carrera:** la siembra que pierde choca con `PK_role_seeds` (la marca) o con `IX_roles_tenant_key` (una clave que el otro ya insertó); con cualquiera de esos **dos nombres** el store limpia el `ChangeTracker` y devuelve `false` («ya sembrado»). Cualquier otra excepción sube. El store **no** usa `AuthorizationUnitOfWork`, que traduce `IX_roles_tenant_key` a `authorization.role.key_taken`.
- **Orden de los aprovisionamientos:** `TenantRegistrationService` recibe `IEnumerable<INewTenantProvisioning>` y los corre en orden de registro. El de roles se registra **antes** que el de Quotations, en la misma línea de `QepServiceCollectionExtensions.cs:1635`, para que un fallo de Quotations (que hoy sube) no se salte la siembra.
- **Sin `ITenantAccessVersion.BumpAsync` al sembrar:** los permisos que resuelve cada clave no cambian (antes salían del código, después de una fila igual), así que la caché de permisos no sirve nada distinto.

## Review Focus

1. **Un tenant existente cuyos miembros tienen `billing` o `advisor`:** tras el primer arranque de la versión nueva, `ResolvePermissionsAsync` devuelve para ese miembro **exactamente** el mismo conjunto que antes. Prueba en la Tarea 4 (`StartupKeepsTheEffectivePermissionsOfAnExistingMember`).
2. **Reinicio después de que el tenant editó o borró:** un rol borrado no vuelve y uno editado no se pisa. Prueba en la Tarea 4.
3. **Dos siembras del mismo tenant a la vez** (alta en un pod mientras otro arranca): un solo juego de filas y una marca, y ninguna de las dos lanza. Prueba en la Tarea 2.
4. **Borrar un rol que tiene una persona suspendida, invitada o con la invitación vencida sin marcar:** 422 `authorization.role.in_use`; con sólo `Removed` o `Expired`, se borra. Prueba en la Tarea 3.
5. **Signup con la siembra de roles rota:** el alta responde igual (201), queda un log `Error` con el tenant, y el siguiente arranque lo repara. Pruebas en la Tarea 4 (unitaria del aprovisionamiento y `StartupSeedsAnExistingTenantWithoutMarker`).

---

## Tarea 0 (antes del Bloque 1, sin commit): línea base

- [ ] **Paso 1:** en el worktree, sin cambios, restaura y compila (un worktree nuevo no tiene `obj/project.assets.json`; restore normal, sin `--force-evaluate`), y corre las suites sin contenedores que esta rama toca:

```powershell
dotnet restore -nologo
dotnet build --no-restore -nologo -v q
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build -nologo
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build -nologo
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build -nologo
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build -nologo
```

Esperado: verde, o una lista de fallas con nombre. Esa lista es la **línea base**: una falla que no esté ahí es de la rama.
- [ ] **Paso 2:** `.\scripts\test-feature.ps1 -DryRun` y anota los pasos y avisos con la rama vacía.

---

## Bloque 1 — Siembra y borrado, sin cambiar todavía el catálogo (Tareas 1 a 3, un implementador)

Al terminar el bloque los cinco roles siguen siendo `RoleDefinition` del catálogo: la siembra existe pero nadie la llama. Lo único visible es que `cashier` y los demás dejan de ser claves reservadas y que el borrado mira más estados.

### Task 1: Dominio, plantillas y sembrador

**Files:**
- Modify: `src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs` (sólo `Admin`; `All = { Admin }`; reescribe el XML doc), `src/Modules/Authorization/Modules.Authorization.Domain/Role.cs` (`KeyShape`, el mensaje de `key_invalid` y el `<remarks>` que dice que un rol de sistema no vive acá).
- Create: `src/Modules/Authorization/Modules.Authorization.Application/RoleTemplate.cs` (`RoleTemplate` y `RoleTemplateKeys`), `src/Modules/Authorization/Modules.Authorization.Application/TenantRoleSeeder.cs` (`ITenantRoleSeedStore`, `IRoleSeedTenantSource`, `ITenantRoleSeeder`, `TenantRoleSeeder`).
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:3267,3279` (`SystemRoleKeys.SchedulingManager|Provider` → `RoleTemplateKeys.*`; siguen siendo `RoleDefinition` en esta tarea).
- Test: modify `tests/Modules/Authorization/Modules.Authorization.UnitTests/SystemRoleKeysTests.cs`, `RoleTests.cs`, `RoleCommandsTests.cs:20` y `TenantRoleCatalogTests.cs:22,64` (cambian `SystemRoleKeys.Advisor` por el literal `"advisor"`: son catálogos de juguete y siguen probando lo mismo); create `tests/Modules/Authorization/Modules.Authorization.UnitTests/TenantRoleSeederTests.cs`.

**Interfaces:**
- Produces:
  - `public sealed record RoleTemplate(string Key, string DisplayName, string Description, IReadOnlyCollection<string> Permissions)`. XML doc: plantilla de siembra, no resuelve permisos ni entra a ningún catálogo; un permiso nuevo en una plantilla sólo llega a tenants nuevos (decisión 2 del owner).
  - `public static class RoleTemplateKeys { Advisor = "advisor"; Billing = "billing"; Cashier = "cashier"; SchedulingManager = "scheduling_manager"; SchedulingProvider = "scheduling_provider"; }` — no reservadas.
  - `public interface ITenantRoleSeedStore { Task<bool> IsSeededAsync(Guid tenantId, CancellationToken ct); Task<IReadOnlySet<Guid>> SeededTenantIdsAsync(CancellationToken ct); Task<IReadOnlyCollection<string>> RoleKeysAsync(Guid tenantId, CancellationToken ct); Task<bool> TrySaveAsync(Guid tenantId, IReadOnlyCollection<Role> roles, DateTimeOffset seededAt, CancellationToken ct); }` — `TrySaveAsync` guarda roles y marca juntos; `false` = perdió la carrera.
  - `public interface IRoleSeedTenantSource { Task<IReadOnlyCollection<Guid>> ListTenantIdsAsync(CancellationToken ct); }` — todos los tenants, en cualquier estado.
  - `public interface ITenantRoleSeeder { Task<bool> SeedAsync(Guid tenantId, CancellationToken ct); Task<int> SeedMissingAsync(CancellationToken ct); }`; `public sealed class TenantRoleSeeder(IEnumerable<RoleTemplate> templates, ITenantRoleSeedStore store, IRoleSeedTenantSource tenants, IClock clock) : ITenantRoleSeeder`.

Reglas: `SeedAsync` con marca devuelve `false` sin llamar `TrySaveAsync`; sin marca crea con `Role.Create(RoleId.New(), tenantId, t.Key, t.DisplayName, t.Description, t.Permissions, clock.UtcNow)` cada plantilla cuya clave no esté en `RoleKeysAsync` y llama `TrySaveAsync` **aunque la lista quede vacía** (la marca se escribe igual); devuelve lo que devuelva `TrySaveAsync`. `SeedMissingAsync` siembra cada id de `ListTenantIdsAsync` que no esté en `SeededTenantIdsAsync`, en serie, devuelve cuántos sembró (`true`) y deja subir cualquier excepción.

- [ ] **Paso 1: pruebas que fallan.**
  - `SystemRoleKeysTests`: `OnlyAdminIsReserved` (theory: `admin` ⇒ `true`; `advisor`, `billing`, `cashier`, `scheduling_manager`, `scheduling_provider` ⇒ `false`); `TheTemplateKeysKeepTheirValues` (las cinco constantes de `RoleTemplateKeys` iguales a los literales). Borra `TheSchedulingRolesHaveTheirConstants` y los casos no-admin de `ASystemRoleKeyIsReserved`.
  - `RoleTests`: `CreateAcceptsTheSeededKeys` (theory con las cinco claves: `role.Key` igual a la entrada); quita `CreateRejectsTheCashierKey`; en `CreateRejectsAKeyThatIsNotASlug` cambia el caso `"Mayuscula_guion_bajo"` por `"con.punto"`.
  - `TenantRoleSeederTests` (dobles en memoria del store y de la fuente; `IClock` fijo):
    - `AnUnseededTenantGetsEveryTemplateAndTheMarker` — dos plantillas de juguete ⇒ `TrySaveAsync` recibe dos `Role` con clave, nombre, descripción y permisos de la plantilla, `seededAt == clock.UtcNow`; devuelve `true`.
    - `AnExistingKeyIsNotRecreated` — `RoleKeysAsync` trae una de las dos ⇒ se guarda sólo la otra.
    - `ATenantWithEveryKeyStillGetsTheMarker` — `TrySaveAsync` llamado con lista vacía.
    - `ASeededTenantIsLeftAlone` — `IsSeededAsync == true` ⇒ `TrySaveAsync` nunca llamado, `false`.
    - `ALostRaceCountsAsAlreadySeeded` — `TrySaveAsync` devuelve `false` ⇒ `SeedAsync` devuelve `false` sin lanzar.
    - `SeedMissingSeedsOnlyTenantsWithoutMarker` — fuente `[a, b, c]`, marcados `{b}` ⇒ siembra `a` y `c`, devuelve `2`.
    - `SeedMissingPropagatesAFailure` — el store lanza en `a` ⇒ `SeedMissingAsync` lanza la misma excepción.
- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Authorization/Modules.Authorization.UnitTests -nologo -v q`. Esperado: `CS0246` por `TenantRoleSeeder`/`RoleTemplateKeys`.
- [ ] **Paso 3: implementa** lo del bloque Interfaces y los cambios de dominio.
- [ ] **Paso 4: GREEN** — `dotnet build --no-restore -nologo -v q` de la solución (nada más referencia las constantes borradas: verificado con `rg "SystemRoleKeys\." src tests`) y `dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests -nologo` completo.
- [ ] **Paso 5: commit** — `feat(authorization): add role templates and the tenant role seeder`

### Task 2: Marca de siembra, migración y store

**Files:**
- Create: `src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/RoleSeed.cs` (entidad interna `RoleSeed { Guid TenantId; DateTimeOffset SeededAt }`), `src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/TenantRoleSeedStore.cs`; migración `AddRoleSeeds` (comando en Global Constraints).
- Modify: `AuthorizationDbContext.cs` (`DbSet<RoleSeed>`, tabla `role_seeds` en el esquema `authorization`, PK con nombre `PK_role_seeds`), `AuthorizationInfrastructureExtensions.cs` (`services.AddScoped<ITenantRoleSeedStore, TenantRoleSeedStore>()`).
- Test: create `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantRoleSeedStoreTests.cs` (Authorization no tiene proyecto de integración; las pruebas de roles por HTTP ya viven en Tenancy, `RoleApiTests`). Host compartido (`SharedTenancyHost`): cada prueba usa su propio `Guid` de tenant y no cuenta nada global.

**Interfaces:**
- Consumes: Task 1 (`ITenantRoleSeedStore`, `Role`).
- Produces: `TenantRoleSeedStore(AuthorizationDbContext)`; `TrySaveAsync` agrega los `Role` y el `RoleSeed` y llama `SaveChangesAsync` una vez (EF lo envuelve en una transacción); captura `DbUpdateException` cuyo `PostgresException` sea `23505` con `ConstraintName` `PK_role_seeds` o `IX_roles_tenant_key` ⇒ `ChangeTracker.Clear()` y `false`. `RoleKeysAsync` y `SeededTenantIdsAsync` con `AsNoTracking`.

- [ ] **Paso 1: pruebas que fallan** (cada una resuelve `ITenantRoleSeedStore` desde `factory.Services` en su propio scope y arma los `Role` con `Role.Create`):
  - `SavingWritesTheRolesAndTheMarkerTogether` — `TrySaveAsync` con `ventas` y `bodega` ⇒ `true`; `IsSeededAsync` `true`; `RoleKeysAsync` = `{ventas, bodega}`; `SeededTenantIdsAsync` contiene el tenant; `GET /api/v1/tenants/{id}/authorization/roles` los lista con `isSystem: false`.
  - `ASecondSaveForASeededTenantWritesNothing` — después de la primera, otra `TrySaveAsync` con `otra` ⇒ `false` y `otra` no existe.
  - `TwoConcurrentSavesEndWithOneSetOfRoles` — dos scopes, `Task.WhenAll` de `TrySaveAsync` con las mismas dos claves ⇒ exactamente un `true`, dos filas en `authorization.roles` y una en `authorization.role_seeds` para ese tenant (SQL con `tenant_id`).
  - `ASaveWithoutRolesStillWritesTheMarker` — lista vacía ⇒ `true` e `IsSeededAsync` `true`.
- [ ] **Paso 2: RED** — build del proyecto de integración (falla por `ITenantRoleSeedStore` sin registrar sólo en ejecución: si compila, corre filtrado por `TenantRoleSeedStoreTests` y pega la falla de resolución o de tabla inexistente).
- [ ] **Paso 3: implementa** entidad, mapeo, store y registro; genera la migración y revisa el `.cs`: sólo `CreateTable` de `authorization.role_seeds` con `PK_role_seeds`, nada sobre `roles`.
- [ ] **Paso 4: GREEN** — filtrado por `TenantRoleSeedStoreTests`.
- [ ] **Paso 5: commit** — `feat(authorization): persist the per-tenant role seed marker`

### Task 3: No borrar un rol que tenga cualquier miembro vivo

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/IMembershipRoleUsage.cs` (método nuevo y `<remarks>`), `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRoleUsage.cs`, `src/Modules/Authorization/Modules.Authorization.Application/RoleCommands.cs` (`DeleteRoleHandler` usa el método nuevo; `UpdateRoleHandler` sigue con `ActiveRoleSetsAsync`).
- Test: modify `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs` (el doble `Usage` implementa el método nuevo); create `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipRoleUsageTests.cs` (host compartido; inserta su tenant y sus membresías como `MembershipApiTests.SeedSeededTenantAsync`, `MembershipApiTests.cs:1281`).

**Interfaces:**
- Produces: `Task<bool> AnyHoldsRoleAsync(TenantId tenantId, string role, CancellationToken cancellationToken)` en `IMembershipRoleUsage`: `true` si alguna membresía del tenant en `Invited`, `Active` o `Suspended` lleva la clave (comparación ordinal; la columna `roles` es la colección primitiva `_roles`, `TenancyDbContext.cs:333`). `Removed` y `Expired` no cuentan. Una `Invited` con la ventana vencida sigue en `Invited` y cuenta.

- [ ] **Paso 1: pruebas que fallan.**
  - Unitarias (`RoleCommandsTests`): `DeleteRefusesARoleHeldOnlyByASuspendedOrInvitedMember` (`ActiveRoleSetsAsync` vacío, `AnyHoldsRoleAsync` `true` ⇒ `authorization.role.in_use`, el repositorio no borra); `DeleteRemovesARoleNoLiveMemberHolds` (`AnyHoldsRoleAsync` `false` ⇒ borrado y versión de acceso subida dos veces). Ajusta `DeleteRefusesARoleSomebodyStillHas` al método nuevo.
  - Integración (`MembershipRoleUsageTests`): `AMembershipHoldsTheRoleInTheseStates` (theory `Invited`, `Active`, `Suspended` ⇒ `true`); `RemovedAndExpiredDoNotHoldIt` (theory ⇒ `false`); `ALapsedInvitationStillHoldsIt` (`Invited` con vencimiento en el pasado ⇒ `true`); `AnotherTenantsMembershipDoesNotCount`; `DeletingARoleHeldByASuspendedMemberIs422InUse` (crea `jefa-bodega` por `POST …/authorization/roles` con `advisorship.roles.manage`, inserta una membresía `Suspended` con esa clave, `DELETE` ⇒ 422 `authorization.role.in_use`).
- [ ] **Paso 2: RED** — build de `Modules.Authorization.UnitTests` (`CS0535`/`CS1061` por `AnyHoldsRoleAsync`).
- [ ] **Paso 3: implementa** la consulta (`AnyAsync`, sin traer filas) y el cambio del handler; corrige el `<remarks>` de `IMembershipRoleUsage` y de `MembershipRoleUsage`, que hoy dicen que una suspendida no retiene un rol de ser borrado.
- [ ] **Paso 4: GREEN** — unitarias completas de Authorization; integración filtrada por `MembershipRoleUsageTests`.
- [ ] **Paso 5: commit** — `feat(authorization): block deleting a role held by invited or suspended members`

**Cierre del Bloque 1:** clases completas `TenantRoleSeedStoreTests`, `MembershipRoleUsageTests` y `RoleApiTests` (en fila), más las unitarias de Authorization y Tenancy y `ArchitectureTests`. Revisión del bloque.

---

## Bloque 2 — El corte, las pruebas que lo sienten y la documentación (Tareas 4 a 6, un implementador)

### Task 4: Plantillas en composición, alta y arranque

**Files:**
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — las cinco `RoleDefinition` de `:3127-3290` pasan a `services.AddSingleton(new RoleTemplate(RoleTemplateKeys.X, nombre, descripción, [permisos idénticos]))`, conservando los comentarios de cada permiso; borra el argumento `Category`/`RiskLevel` (no existen en la plantilla); reescribe el comentario de D-13 (`:3260-3264`). En `AddAuthorizationCapability` registra `services.AddScoped<ITenantRoleSeeder, TenantRoleSeeder>()` y `services.AddScoped<IRoleSeedTenantSource, TenancyRoleSeedTenantSource>()`. En `:1635`, **justo antes** de `QuotationsNewTenantProvisioning`, `services.AddScoped<Modules.Tenancy.Application.INewTenantProvisioning, TenantRolesProvisioning>()`.
- Create: `src/Bootstrapper/TenantRolesProvisioning.cs` (aprovisionamiento y su log), `src/Bootstrapper/TenancyRoleSeedTenantSource.cs` (lee `TenancyDbContext.Tenants` y devuelve los ids, `AsNoTracking`), `src/Bootstrapper/TenantRoleStartupSeeding.cs` (extensión del arranque).
- Modify: `src/Api/Program.cs` (una línea después de `RunQepSeedAsync`, dentro del lambda), `src/Modules/Tenancy/Modules.Tenancy.Application/TenantRegistrationService.cs` (`IEnumerable<INewTenantProvisioning>`, en orden, en los dos caminos `:95` y `:167`), `src/Modules/Tenancy/Modules.Tenancy.Application/INewTenantProvisioning.cs` (el XML doc deja de decir «hoy, los ajustes de cotizaciones»).
- Test: modify `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventoryTests.cs:332`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs` (sitios de construcción `:224`, `:247`, `InvitedFixture` `:383`, `NewService` `:440-455`); create `tests/Bootstrapper/Bootstrapper.UnitTests/TenantRolesProvisioningTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantRoleSeedingTests.cs`.

**Interfaces:**
- Consumes: Task 1 (`RoleTemplate`, `RoleTemplateKeys`, `ITenantRoleSeeder`, `IRoleSeedTenantSource`), Task 2 (store registrado).
- Produces:
  - `internal sealed class TenantRolesProvisioning(ITenantRoleSeeder seeder, ILogger<TenantRolesProvisioning> logger) : INewTenantProvisioning` — llama `SeedAsync(tenantId.Value, ct)`; cualquier excepción que no sea `OperationCanceledException` con el token cancelado se registra como `LogLevel.Error` con el `tenantId` (`[LoggerMessage]`, ver CA1873) y **no** sube.
  - `public static Task SeedTenantRolesAsync(this IServiceProvider services, CancellationToken cancellationToken)` en Bootstrapper — un scope, `SeedMissingAsync`, log `Information` con cuántos sembró; deja subir la excepción (el arranque falla).
  - `TenantRegistrationService(..., IEnumerable<INewTenantProvisioning> provisioning)`.
  - En `CompositionRootTests`, un helper privado `static IReadOnlyCollection<string> FactoryPermissions(IServiceProvider provider, string role)`: `admin` ⇒ `IRoleCatalog.PermissionsFor("admin")`; cualquier otra clave ⇒ `provider.GetServices<RoleTemplate>().Single(t => t.Key == role).Permissions`.

- [ ] **Paso 1: pruebas que fallan.**
  - `CompositionRootTests`:
    - `OnlyAdminIsASystemRole` — `IRoleCatalog.ListRoles()` tiene una sola definición, `admin`.
    - `TheFiveTemplatesAreRegisteredWithTodaysNames` — claves de `GetServices<RoleTemplate>()` = las cinco de `RoleTemplateKeys`, sin repetir, con `DisplayName` «Asesor», «Facturación», «Cajero», «Gestor de agenda», «Profesional de agenda».
    - `EveryRoleTemplateIsASubsetOfAdmin` — cada permiso de cada plantilla está en `IRoleCatalog.ListPermissions()` (motivo en el spec: si no, no se podría marcar ni guardar, `authorization.role.permission_unknown`).
    - Reemplaza en todo el archivo `catalog.PermissionsFor(` por `FactoryPermissions(provider, ` para que las aserciones de las 16 pruebas que nombran `advisor`, `billing`, `cashier` o `scheduling_*` (507, 524, 560, 639, 667, 860, 882, 906, 929, 1050, 1145, 1175, 1204, 1260, 1280 y las `DoesNotContain` de las `OnlyTheAdmin…`) pasen a afirmarse sobre las plantillas; `TheSchedulingRolesAreAssignableOnlyWithTheSchedulingModule` (`:1087`) toma los permisos de la plantilla en vez de `ListRoles()`.
  - `MultiReplicaInventoryTests`: agrega `SeedTenantRolesAsync` a la regex de `StartupWorkAgainstTheLock` (`:332`) y crea `TheTenantRoleSeedRunsUnderTheLockAfterTheQepSeed` (en `Program.cs`, `SeedTenantRolesAsync` está en `inside` y su índice es mayor que el de `RunQepSeedAsync`).
  - `TenantRegistrationServiceTests`: `SignupRunsEveryProvisioningInRegistrationOrder` y `RegisterInvitedTenantRunsEveryProvisioningInRegistrationOrder` (dos dobles que anotan en `steps` ⇒ `["commit", "provision:a", "provision:b"]`); las pruebas de provisión existentes pasan la lista con un solo doble.
  - `TenantRolesProvisioningTests`: `ProvisioningSeedsTheNewTenant` (el doble del sembrador recibe el id); `AFailedSeedIsLoggedAsErrorAndDoesNotThrow` (el sembrador lanza ⇒ `ProvisionAsync` termina; un logger de captura privado registra una entrada `Error` que nombra el tenant).
  - `TenantRoleSeedingTests` (integración):
    - `ASignedUpTenantIsBornWithTheFiveRoles` (host compartido) — `POST /api/v1/auth/register-tenant` como `MembershipLifecycleApiTests.RegisterTenantWithOwnerAsync` (`:742`); `GET …/authorization/roles` ⇒ `admin` con `isSystem: true` y las cinco con `isSystem: false`, `id` no nulo, `category: "Custom"`, nombre y permisos iguales a la plantilla.
    - `ASeededRoleCanBeInvitedRightAfterSignup` (host compartido) — `POST …/memberships` con `roles: ["advisor"]` ⇒ 201.
    - `StartupSeedsAnExistingTenantWithoutMarker` (host propio: `StartDatabaseAsync` + `new QepApiFactory`) — con el primer host arrancado, inserta un tenant directo (nace sin marca: el arranque ya pasó); descarta el host; un segundo `QepApiFactory` sobre la misma base arranca y el tenant lista las cinco.
    - `StartupKeepsTheEffectivePermissionsOfAnExistingMember` (host propio, Review Focus 1) — tenant insertado directo con todos los módulos prendidos y una membresía activa `["billing"]`; en el primer host `IAuthorizationService.ResolvePermissionsAsync(userId, tenantId)` no da ningún permiso (la prueba lo afirma: es el hueco que el arranque cierra); en el segundo da, como conjunto, exactamente los permisos de la plantilla `billing`, que son la lista que concedía el rol de sistema (ninguno es `operator.*` ni queda enmascarado con todos los módulos).
    - `StartupDoesNotReviveADeletedRoleNorOverwriteAnEditedOne` (host propio, Review Focus 2) — tenant registrado; `DELETE` del `cashier`; `PUT` del `advisor` con `If-Match` y sólo `customers.customer.read`; reinicia el host ⇒ no hay `cashier` y `advisor` conserva sólo ese permiso.
- [ ] **Paso 2: RED** — builds de `ArchitectureTests`, `Bootstrapper.UnitTests` y `Modules.Tenancy.UnitTests` (fallan por `RoleTemplate` sin registrar en las aserciones, `TenantRolesProvisioning` inexistente y el constructor de `TenantRegistrationService`); pega la salida. Luego, con lo que compile, corre `CompositionRootTests` filtrado por `OnlyAdminIsASystemRole` y pega la falla.
- [ ] **Paso 3: implementa** composición, aprovisionamiento, fuente de tenants, paso de arranque y registro.
- [ ] **Paso 4: GREEN** — `ArchitectureTests`, `Bootstrapper.UnitTests`, `Modules.Tenancy.UnitTests` y `Modules.Authorization.UnitTests` completos (sin contenedores, salvo `MultiReplicaInventoryTests`, que arma el contenedor real); integración filtrada por `TenantRoleSeedingTests`.
- [ ] **Paso 5: commit** — `feat(authorization): seed the five factory roles per tenant and keep admin as the only system role`

### Task 5: Pruebas de integración que crean tenants sin pasar por el alta

Todo tenant insertado directo en una prueba nace **después** del arranque (el `DbContext` sale de `factory.Services`, que arranca el host), así que no tiene los cinco roles. Las que sí pasan por `register-tenant` quedan sembradas solas (Messaging, Pipeline, Automations, Pos, Reporting, Shopify, Quotations, Integrations, Identity y los `RegisterTenant…` de Tenancy): no se tocan. Sólo se agrega la siembra donde el rol llega a una ruta que lo resuelve (invitar, `IRolePermissionChecker`, `ITenantRoleCatalog`); los harnesses que hoy sólo usan `admin` no se tocan.

**Files:** en cada helper, después de insertar el tenant:

```csharp
await using var seeding = factory.Services.CreateAsyncScope();
await seeding.ServiceProvider.GetRequiredService<ITenantRoleSeeder>().SeedAsync(tenantGuid, TestContext.Current.CancellationToken);
```

- Modify (helper que inserta el tenant):
  - `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/ServiceContractsApiHarness.cs:48` `SeedTenantAsync` (rompen `ServiceContractsReminderProcessorTests`, `OverdueDigestEventTests`, `ServiceContractsProfileApiTests`, `ServiceContractsDirectoriesTests`: el destinatario `billing` deja de poder gestionar).
  - `tests/Modules/Support/Modules.Support.IntegrationTests/SupportApiHarness.cs:53` `SeedTenantAsync` (lo usan `AppHubHarness.ConnectMemberAsync` y los `NewTenantAsync` de cada archivo; rompen las suites de tickets, tareas, bandeja, hub y eventos de asignación).
  - `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansApiHarness.cs:49` `SeedTenantAsync` (`LoansAccessApiTests.TheAssigneesAreTheActiveMembersWhoCanManage`).
  - `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs:1281` `SeedSeededTenantAsync` (≈38 pruebas que invitan con `advisor`/`billing`, más `AuthorizationCatalogReturnsRolesAndPermissions`).
  - `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/InvitationApiTests.cs:288` `SeedSeededTenantAsync`.
  - `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs:592` `SeedSeededTenantAsync` y el bloque `Tenants.Add` de `LoginListsActiveTenantsWithDisplayNamesAndExcludesSuspendedMembership` (`:160-186`, siembra los dos tenants; la aserción `("advisor", "Asesor")` sigue valiendo porque la fila lleva ese nombre).
  - `tests/Modules/Audit/Modules.Audit.IntegrationTests/AuditRecordingTests.cs:166` `SeedTenantAsync`.
  - `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InvitationNotificationTests.cs` `SeedSeededTenantAsync` (~`:51`).
- Modify (sin helper de tenant):
  - `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` — `TheCatalogNamesTheOrderPermissions` y `TheCatalogNamesThePosPermissionsAndTheCashierRole` siembran su `TenantId` (no tiene fila en `tenancy.tenants`; el sembrador no la exige) antes del `GET`.
  - `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RoleApiTests.cs` — `CreateWithTheCashierKeyIsRejected` pasa a `CreateWithASeededKeyIsTaken`: siembra el tenant y espera 422 `authorization.role.key_taken` (el código de `CreateWithADuplicateKeyIsRejected`; `cashier` ya no es reservada).
  - `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/WholesaleAccessApiTests.cs:55` — `AnAdvisorReadsDistributors` lee `GetServices<RoleTemplate>().Single(t => t.Key == RoleTemplateKeys.Advisor).Permissions` en vez de `RoleDefinition`.

- [ ] **Paso 1: RED** — con la Tarea 4 ya commiteada, corre en fila, filtradas por nombre, una prueba representativa por helper: `InviteStoresOnlyTheTokenHashAndTheOutboxCarriesThePlainToken`, `ListFiltersByRoleWhenRequested`, `FirstLoginLinksActivatesAndAcceptsInvitedMembership`, `InviteWritesAuditEntryAtomicallyToAuditStore`, `InvitingMemberDeliversExactlyOneInvitationNotification`, `TheAssigneesAreTheActiveManagers`, `TheAssigneesAreTheActiveMembersWhoCanManage`, `AContractWithoutProfileGoesToEveryManager`, `TheCatalogNamesThePosPermissionsAndTheCashierRole`, `CreateWithTheCashierKeyIsRejected`, `AnAdvisorReadsDistributors`. Esperado: 422 `tenancy.membership.role_unknown`, listas sin el miembro `billing`/`advisor`, o `InvalidOperationException` de `Single`. Pega cada falla.
- [ ] **Paso 2: aplica** los cambios de la lista.
- [ ] **Paso 3: GREEN** — las mismas pruebas filtradas (con el nombre nuevo para `CreateWithASeededKeyIsTaken`).
- [ ] **Paso 4: commit** — `test: seed the factory roles in harnesses that insert tenants directly`

### Task 6: Documentación y comentarios

**Files:**
- Modify: `.claude/rules/tenancy-y-servicios.md` — bullet nuevo: sólo `admin` es de sistema; los otros cinco son plantillas (`RoleTemplate`) sembradas por tenant en el alta y al arrancar, con marca en `authorization.role_seeds`; **un permiso nuevo en una plantilla sólo llega a tenants nuevos** (decisión 2 del owner) y renombrar o partir un permiso exige una migración de datos sobre `authorization.roles` (precedente: `RenamePermissionsToOrders`); un rol no se borra si lo tiene una membresía `Invited`, `Active` o `Suspended`. En el bullet de `ServiceContracts`, «el rol de sistema `billing` (`SystemRoleKeys.Billing`)» pasa a «el rol sembrado `billing` (`RoleTemplateKeys.Billing`)».
- Modify: `.claude/rules/pruebas-y-entorno-local.md` — bullet nuevo: un tenant insertado directo después del arranque no tiene los cinco roles; si la prueba invita o resuelve uno de ellos, el helper llama `ITenantRoleSeeder.SeedAsync` (ejemplo: `SupportApiHarness.SeedTenantAsync`).
- Modify: `README.md:2492` y toda mención de los cinco como roles de sistema que aparezca con `rg -n -i "rol(es)? de sistema" README.md`.
- Modify: comentarios que describen a los cinco como de sistema: `SystemRoleKeys.cs` y `Role.cs` (si quedó algo de la Tarea 1), `RoleCatalog.cs` (resumen de `RoleDefinition`), `TenantRoleCatalog.cs` (el comentario de `Role.Create` y las claves reservadas), `ITenantRoleCatalog.cs` (`IsSystem`).
- Modify: `docs/superpowers/specs/2026-11-21-agenda-citas-design.md:272` — la fila D-13 lleva al final «Reemplazada por `2026-11-25-roles-por-tenant-design.md`: roles sembrados por tenant».

- [ ] **Paso 1:** aplica los cambios.
- [ ] **Paso 2:** `rg -n "SystemRoleKeys\.(Advisor|Billing|Cashier|Scheduling)" src tests .claude README.md` sin resultados; `dotnet build --no-restore -nologo -v q` sin advertencias.
- [ ] **Paso 3: commit** — `docs: describe per-tenant seeded roles`

**Cierre del Bloque 2:** `dotnet format --verify-no-changes` y `.\scripts\test-feature.ps1` en verde (cubre las clases tocadas de Tenancy, ServiceContracts, Support, Loans, Audit, Notifications y Wholesale), leyendo sus avisos; compara contra la línea base de la Tarea 0. Revisión del bloque.

---

## Verificación final y merge

- [ ] Revisión fuerte de toda la rama (modelo más capaz), con el spec y la lista «Review Focus» delante.
- [ ] Merge `--no-ff` a `develop` (nunca a `main`, sin push). Después, en `develop`, commit `docs(estado): …` con la fila de la feature y el Registro: migración aditiva `AddRoleSeeds`; el arranque falla si la siembra falla; la ventana del rollout (un tenant registrado en un pod viejo después de la siembra queda sin los cinco hasta el siguiente arranque) quedó aceptada en el spec.
- [ ] Aviso al frontend: sin cambios de contrato; los cinco roles llegan con `isSystem: false`, `id` y `version`, categoría `Custom` y nivel de riesgo calculado; `CatalogVersion` cambia.

## Decisiones pendientes del owner

Ninguna abierta. Las del spec («Decisiones tomadas al redactar») se aplican tal cual; las de este plan están en «Decisiones del plan donde el spec deja criterio».
