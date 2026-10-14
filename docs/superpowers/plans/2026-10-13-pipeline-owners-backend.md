# Lista de dueños del embudo (`GET /pipeline/owners`) — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el formulario de negocio del frontend pueda dibujar el selector de dueño: un endpoint nuevo en Pipeline que devuelve las membresías **activas** del tenant, ordenadas por nombre, como `[{ "memberId", "name" }]`.

**Architecture:** Sin módulo, tabla, migración, permiso ni paquete nuevos. El puerto `IPipelineActiveMembers` (que hoy sólo contesta «¿estos ids son activos?») gana `ListActiveAsync`, que su adaptador de Bootstrapper resuelve con `IMembershipRepository.ListByTenantAsync` filtrando por `Active`. Un handler nuevo (`GetDealOwnersHandler`) autoriza con `PipelineAuthorization.EnsureAsync` (permiso `pipeline.deal.manage` + capacidad `pipeline`), pide los nombres a `IPipelineMemberNames` (la única copia de «`DisplayName` ?? correo») y ordena. Un endpoint de una ruta y el registro a mano del handler cierran el cambio. Pipeline sigue sin referenciar el `Application` de ningún módulo salvo Tenancy.

**Tech Stack:** .NET 10, xUnit v3, Testcontainers (`postgres:18-alpine`), ASP.NET Core minimal APIs.

**Spec:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\embudo-de-ventas\docs\superpowers\specs\2026-10-13-embudo-de-ventas-frontend-design.md` — decisiones D21, D22 y D23, hallazgo H43 y §5.5 (el selector de «Dueño» del diálogo de negocio nuevo). Contexto del backend del embudo: `docs/superpowers/specs/2026-10-13-embudo-de-ventas-design.md` y `docs/superpowers/plans/2026-10-13-embudo-de-ventas-backend.md` (en este repo). Quien ejecuta lee el spec de D21–D23, este plan y el `CLAUDE.md` del repo.

Este plan cubre **sólo el backend** de ese endpoint; el selector es otro slice, en `qep-frontend`, que espera a que esto esté en `develop`. La ruta, la forma JSON, el permiso y los 403 son **contrato** con ese frontend: no se cambian sin cambiar su spec.

## Antes de empezar (leer una vez)

- **Rutas.** Se trabaja en el worktree de la rama, **no** en el checkout principal (ese está en `develop` y el developer mergea ahí en paralelo). Abajo, `$B` es
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners`. El estado del shell no persiste: cada bloque vuelve a definir `$B`. Los comandos son **PowerShell** (Windows 5.1: sin `&&`, `curl.exe` y no `curl`).
- **Rama.** `feature/pipeline-owners`, creada desde `origin/develop` `544da94`. Se comprueba con `git branch --show-current` **en el momento** de commitear; si es otra, se aborta. **Prohibido mergear a `main`**; el destino de esta rama es `develop` y el merge lo decide el developer.
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). Antes del primer build de cada tarea:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    Select-Object ProcessId, Name, CommandLine
  ```

  Si sale algo, **no se mata**: puede ser la sesión de depuración del developer. Se detiene la tarea y se avisa.
- **Pruebas.** Siempre en **primer plano**, nunca con `| Select-Object`, `| tail` ni otro pipe (el pipe esconde el exit code). Por tarea **sólo las clases tocadas**, con un filtro por proyecto (`FullyQualifiedName~A|FullyQualifiedName~B`). Si un paso corre dos proyectos o más: primero `dotnet build "$B\Backend.slnx" --no-restore` con guard de `$LASTEXITCODE`, después cada `dotnet test … --no-build`. **No se corre la suite completa:** el cambio no toca dominio, esquema ni otros módulos, y las clases tocadas más las dos de arquitectura cubren lo que puede romperse. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea. Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal. Un RED de integración por ruta inexistente (404 donde se esperaba otra cosa) también vale; se copia el `Assert.Equal() Failure` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`.** Lo que muerde acá:
  - xUnit1051: toda llamada que acepte `CancellationToken` en una prueba recibe `TestContext.Current.CancellationToken` (en estos archivos, `Ct`).
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1034: nada de tipos anidados públicos (una `const` dentro de un `record` no es un tipo anidado).
  - Si el analizador marca algo que el plan no previó, se corrige **sin cambiar el comportamiento** y con el patrón del código vecino; se anota en el handoff.
- **Archivos.** Se escriben con las herramientas de edición, **nunca** con `Set-Content`/`Out-File` de PowerShell 5.1 (rompe las tildes: doble UTF-8 y BOM). Antes de cada commit, `git status --short` para ver que no quedó un `.cs` de 0 bytes ni un archivo ajeno. Se agregan **sólo** las rutas tocadas, explícitas; nunca `git add -A` ni `git add .`.
- **`dotnet format` no es compuerta** (CRLF: ~90k `ENDOFLINE` en el baseline). La compuerta es el build con analizadores.
- **Locks.** Este plan no agrega proyectos ni referencias ni paquetes: no se toca ningún `.csproj` ni `packages.lock.json`. Si `git status` muestra uno modificado, algo salió mal; se revierte esa ruta y se avisa.
- **Commits:** conventional commits en español, como la historia (`feat(pipeline): …`). **Sin atribución de IA y sin trailer `Co-Authored-By`**, aunque el harness lo pida. Después de cada commit, `git log -1 --format=%B` para comprobarlo.
- **Secretos:** ninguno nuevo. No se imprime ningún valor de `user-secrets` ni de configuración.
- **Comentarios y documentación** en español, tuteando, con el estilo del código vecino (`<summary>` que cita la decisión del spec). Identificadores, códigos y rutas en inglés, como el resto del repo.
- **No hay `sdd/` en este repo** (no existe `qep-backend/sdd/`): la «fila en el ledger del backend» que menciona el spec del frontend no tiene dónde escribirse. Se anota en el handoff y no se crea una carpeta.

## Global Constraints

- Ruta: `GET /api/v1/tenants/{tenantId:guid}/pipeline/owners`, dentro del grupo que ya mapea `PipelineEndpoints`. Permiso: `pipeline.deal.manage` (`PipelinePermissions.DealManage`), **no** `pipeline.deal.read` (D21, D22). No hay permiso ni política nuevos.
- Respuesta `200`: un arreglo JSON plano (sin sobre), camelCase, `[{ "memberId": "<guid>", "name": "<nombre visible o correo>" }]`, ordenado por `name`.
- «Puede ser dueño» es la regla que ya valida `pipeline.deal.owner_not_member`: **membresía `Active` del tenant**. Una `Invited`, `Suspended`, `Removed` o `Expired`, o una de otro tenant, no aparece.
- Autorización (idéntica a los demás handlers del módulo, `PipelineAuthorization.EnsureAsync`): tenant de la ruta distinto del activo, o permiso faltante → 403 `authorization.denied`; capacidad `pipeline` apagada → 403 `tenancy.module_not_enabled`. **Nunca 404.** El frontend (D23) oculta el selector ante cualquier 403 de esta ruta y no reintenta.
- El nombre es `Membership.DisplayName` o, si no tiene, el correo del usuario: la regla ya existe en `MessagingMemberNames` (vía `IPipelineMemberNames`); no se copia.
- Sin migraciones, sin claves de configuración nuevas, sin paquetes NuGet nuevos, sin tablas ni columnas.
- Pipeline no referencia el `Application` de ningún módulo salvo Tenancy (`PipelineLayerTests`): la lectura de membresías va por el puerto `IPipelineActiveMembers` con adaptador en Bootstrapper.

## Review Focus

1. **Membresías que no pueden recibir trabajo:** `Suspended`, `Removed`, `Invited` y `Expired` no aparecen, aunque tengan nombre. → Task 2 (`ListsOnlyTheActiveMembersOfTheTenantOrderedByName`) y Task 1 (`ListActiveReturnsOnlyTheTenantsActiveMemberships`).
2. **Fuga entre tenants:** los miembros activos de otro tenant no aparecen, y la ruta de otro tenant es 403, no 404 ni la lista vacía. → Task 2 (`ListsOnlyTheActiveMembersOfTheTenantOrderedByName`, `AnotherTenantsRouteIs403`), Task 1 (mismo tenant ajeno e id inexistente).
3. **Orden que una persona esperaría en español:** `Ángela` va con la A, `martha` en minúscula con la M, y quien no tiene nombre se ordena por su correo. → Task 2 (`ListsOnlyTheActiveMembersOfTheTenantOrderedByName`).
4. **Miembro activo sin nombre ni correo:** es dueño válido (`AnActiveMemberWithoutNameOrEmailCanOwnADeal` ya lo fija); si la lista lo callara, el selector contradiría la regla. Aparece con un nombre de reemplazo y nunca con `null`. → Task 2 (`AnActiveMemberWithoutNameOrEmailIsListedWithAPlaceholder`).
5. **Lista y regla de dueño que se separan:** todo lo que la lista devuelve se puede usar como `ownerMemberId` al crear un negocio (201). → Task 2 (`EveryListedMemberCanOwnADeal`).
6. **Quien sólo lee el embudo:** `pipeline.deal.read` sin `manage` es 403 (D21–D23), y con la capacidad apagada sale `tenancy.module_not_enabled`. → Task 2 (`WithoutManageTheListIs403AndWithoutTheCapabilityModuleNotEnabled`).

## Decisiones de este plan (ambigüedades del spec o choques con el código)

| ID | Decisión | Motivo |
| --- | --- | --- |
| O1 | El puerto gana un método nuevo, `IPipelineActiveMembers.ListActiveAsync(tenantId, ct) → IReadOnlyList<Guid>`; el existente `FindActiveAsync(tenantId, ids, ct)` toma ids y no sirve para **listar**. La lista devuelve **ids**, y los nombres se piden aparte a `IPipelineMemberNames`. | El método nuevo es del mismo concepto que ya tiene el puerto (quién es miembro activo) y lo implementa el mismo adaptador. Devolver ids deja «`DisplayName` ?? correo» en una sola copia (`MessagingMemberNames`); el costo es una segunda lectura de membresías por request, aceptable para una lista de decenas de personas que se pide al abrir un diálogo. |
| O2 | El adaptador usa `IMembershipRepository.ListByTenantAsync` (ya existe) y filtra `State == MembershipState.Active` en memoria. | Mismo criterio que `FindActiveAsync` en `PipelineActiveMembers`: el repositorio filtra por tenant y el estado se filtra acá. Un tenant tiene decenas de membresías, no miles; no vale un método de repositorio nuevo en Tenancy. |
| O3 | Un miembro activo sin `DisplayName` y sin correo resoluble se **incluye** con el nombre `DealOwnerDto.UnnamedMemberName` (`"Miembro sin nombre"`), no se omite. | `AnActiveMemberWithoutNameOrEmailCanOwnADeal` (DealApiTests) ya fija que ese miembro es dueño válido; omitirlo haría que el selector contradijera la regla. En la práctica no ocurre (todo usuario tiene correo): es la salida segura de un caso defensivo. **El texto es una decisión de copy que el spec no fija** (pendiente de confirmar con el owner; ver el handoff). |
| O4 | El orden es por `name` con la cultura `es-CO`, sin distinguir mayúsculas, y de desempate el `memberId`. | `OrdinalIgnoreCase` mandaría `Ángela` después de `Zoe` (el acento pesa más que la Z). El desempate vuelve determinista la respuesta cuando dos personas se llaman igual. |
| O5 | El nombre del DTO es `DealOwnerDto(Guid MemberId, string Name)` y el de la consulta `GetDealOwnersQuery(Guid TenantId)`, en un archivo nuevo `Owners.cs`. | El frontend lo tipa como `DealOwnerOption { memberId, name }`; el sufijo `Dto` y el prefijo `Deal` siguen a `DealCardDto` y `DealDetailDto`. |
| O6 | No hay validador de FluentValidation. | La consulta no trae entrada del usuario más allá del `tenantId` de la ruta, que ya es `:guid`. |
| O7 | Un solo ensamblado de pruebas nuevo **no** se crea: la clase `PipelineOwnersApiTests` va en `Modules.Pipeline.IntegrationTests` y el caso del adaptador en `PipelineDirectoriesTests`, que ya prueba los tres adaptadores de Bootstrapper. | Mismo harness, misma base por prueba. |

## File Structure

| Archivo | Qué |
| --- | --- |
| `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs` | `IPipelineActiveMembers.ListActiveAsync`. |
| `src/Bootstrapper/PipelineActiveMembers.cs` | Implementa `ListActiveAsync` sobre `IMembershipRepository.ListByTenantAsync`. |
| `src/Modules/Pipeline/Modules.Pipeline.Application/Owners.cs` (nuevo) | `DealOwnerDto`, `GetDealOwnersQuery`, `GetDealOwnersHandler`. |
| `src/Modules/Pipeline/Modules.Pipeline.Api/OwnerEndpoints.cs` (nuevo) | `GET /owners`. |
| `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs` | `group.MapOwnerEndpoints();`. |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Registro a mano del handler. |
| `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs` | Caso del adaptador. |
| `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineOwnersApiTests.cs` (nuevo) | La API contra Postgres. |
| `README.md` | Una fila en la tabla de rutas de «Embudo de ventas (Pipeline)» y una frase. |

`CompositionRootTests` y `PipelineLayerTests` **no se editan**: la primera encuentra sola la consulta nueva por reflexión (y es el RED del registro), y la segunda sigue verde porque el cambio no agrega ninguna referencia entre ensamblados. Se corren las dos en la Task 2.

---

### Task 1: Puerto `ListActiveAsync` y su adaptador

**Files:**
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs:50-56`
- Modify: `src/Bootstrapper/PipelineActiveMembers.cs`
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs`

**Interfaces:**
- Consumes: `IMembershipRepository.ListByTenantAsync(TenantId, CancellationToken) → IReadOnlyList<Membership>`, `MembershipState.Active`, `Membership.Id.Value`.
- Produces: `IPipelineActiveMembers.ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) → Task<IReadOnlyList<Guid>>`: los ids de las membresías `Active` del tenant, sin orden garantizado; vacío para un tenant sin miembros o inexistente. Lo consume `GetDealOwnersHandler` (Task 2).

- [ ] **Step 1: Comprobar rama, procesos y restaurar**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
if ((git -C $B branch --show-current) -ne "feature/pipeline-owners") { throw "rama equivocada" }
git -C $B log -1 --format=%h
git -C $B status --short
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  Select-Object ProcessId, Name, CommandLine
dotnet restore "$B\Backend.slnx" --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore falló" }
```

Esperado: `544da94` (o un commit encima que sólo toque `docs/`, como este plan), `git status` vacío o con sólo este plan sin commitear, ningún proceso de `Api` y el restore en verde. Si hay un proceso de `Api`, se detiene la tarea y se avisa.

- [ ] **Step 2: Escribir la prueba que falla**

En `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs`, **dentro de la clase**, después de `MemberNamesAreTheTenantsOnly` (los `using` del archivo ya alcanzan):

```csharp
    // D21 del spec del frontend: la lista de dueños posibles es la misma regla que valida al dueño (membresía Active del tenant).
    [Fact]
    public async Task ListActiveReturnsOnlyTheTenantsActiveMemberships()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        var (advisor, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Laura Gómez");
        foreach (var state in new[] { "Suspended", "Removed", "Invited", "Expired" })
        {
            await SeedMemberAsync(connectionString, tenant.TenantId, $"Inactiva {state}", state: state);
        }

        await SeedMemberAsync(connectionString, other.TenantId, "Intrusa Activa");

        using var scope = factory.Services.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<IPipelineActiveMembers>();
        var active = await members.ListActiveAsync(tenant.TenantId, Ct);

        Assert.Equal(new[] { owner, advisor }.Order(), active.Order());
        Assert.Empty(await members.ListActiveAsync(Guid.CreateVersion7(), Ct));
    }
```

- [ ] **Step 3: Correr para verificar que falla (RED por compilación)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
dotnet build "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-restore
```

Esperado: el build **falla** con `error CS1061: 'IPipelineActiveMembers' does not contain a definition for 'ListActiveAsync'`. Se copia la línea del error al handoff.

- [ ] **Step 4: Agregar el método al puerto**

En `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs`, reemplazar la interfaz `IPipelineActiveMembers` entera:

```csharp
/// <summary>Quién puede ser dueño de un negocio (<c>pipeline.deal.owner_not_member</c>): las membresías del tenant en estado
/// <c>Active</c>. Una invitada todavía no puede entrar y una suspendida, quitada o vencida no debe recibir trabajo. Adaptador en
/// Bootstrapper sobre Tenancy; un id de otro tenant o inexistente no aparece.</summary>
public interface IPipelineActiveMembers
{
    Task<IReadOnlySet<Guid>> FindActiveAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>D21 del spec del frontend: todas las membresías <c>Active</c> del tenant, para el selector de dueño. Es la misma
    /// regla que <see cref="FindActiveAsync"/> aplica a unos ids. Sólo ids y sin orden: el nombre lo da
    /// <see cref="IPipelineMemberNames"/> y el orden lo decide quien llama.</summary>
    Task<IReadOnlyList<Guid>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Implementarlo en el adaptador**

En `src/Bootstrapper/PipelineActiveMembers.cs`, agregar este método **después** de `FindActiveAsync` (dentro de la clase):

```csharp

    /// <summary>D21 del spec del frontend: <c>ListByTenantAsync</c> trae el tenant en cualquier estado, y el estado se filtra acá,
    /// igual que en <see cref="FindActiveAsync"/>.</summary>
    public async Task<IReadOnlyList<Guid>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var all = await memberships.ListByTenantAsync(new TenantId(tenantId), cancellationToken);
        return [.. all.Where(membership => membership.State == MembershipState.Active).Select(membership => membership.Id.Value)];
    }
```

- [ ] **Step 6: Correr para verificar que pasa**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~PipelineDirectoriesTests"
```

Esperado: build sin errores ni advertencias y las cinco pruebas de `PipelineDirectoriesTests` en verde (las cuatro que ya había y la nueva). Docker tiene que estar corriendo. Se copia el resumen de la corrida al handoff.

- [ ] **Step 7: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
if ((git -C $B branch --show-current) -ne "feature/pipeline-owners") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs src/Bootstrapper/PipelineActiveMembers.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineDirectoriesTests.cs
git -C $B commit -m "feat(pipeline): el puerto de miembros activos puede listar los del tenant"
git -C $B log -1 --format=%B
```

Esperado en `git status --short` antes del `add`: sólo esas tres rutas (y, si aún no se commiteó, este plan). Esperado en el último comando: el mensaje de una línea, **sin** `Co-Authored-By` ni otra atribución. Si aparece, se enmienda ese commit antes de seguir.

---

### Task 2: Handler, endpoint `GET /owners`, registro y README

**Files:**
- Create: `src/Modules/Pipeline/Modules.Pipeline.Application/Owners.cs`
- Create: `src/Modules/Pipeline/Modules.Pipeline.Api/OwnerEndpoints.cs`
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro, junto a los de «negocios», ~línea 636)
- Modify: `README.md` (tabla de rutas de «Embudo de ventas (Pipeline)», ~línea 2063)
- Test: `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineOwnersApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IPipelineActiveMembers.ListActiveAsync` (Task 1), `IPipelineMemberNames.FindAsync(tenantId, ids, ct) → IReadOnlyDictionary<Guid, string>` (omite al que no tiene nombre ni correo), `PipelineAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, permission, ct)` (interno, mismo ensamblado), `PipelinePermissions.DealManage`, `IExecutionContext`, `ITenantModules`, `IQuery<T>`/`IQueryHandler<,>`/`IRequestDispatcher` de `BuildingBlocks.Application`. En las pruebas: `PipelineApiHarness` (`StartDatabaseAsync`, `QepApiFactory`, `RegisterTenantAsync`, `EnablePipelineAsync`, `DisableModuleAsync`, `OwnerMembershipIdAsync`, `SeedMemberAsync`, `SeedCustomerAsync`, `CreateClient`, `ProblemAsync`, `ScalarAsync`, `PipelineUrl`, `ManagePermissions`, `ReadPermissions`) y `CreateDealRequest(Guid? CustomerId, string? Title, decimal? Amount, string? Currency, DateOnly? ExpectedCloseDate, Guid? OwnerMemberId)` de `Modules.Pipeline.Api`.
- Produces:
  - `public sealed record DealOwnerDto(Guid MemberId, string Name)` con `public const string UnnamedMemberName = "Miembro sin nombre"`.
  - `public sealed record GetDealOwnersQuery(Guid TenantId) : IQuery<IReadOnlyList<DealOwnerDto>>`.
  - `public sealed class GetDealOwnersHandler(...) : IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>`.
  - `GET /api/v1/tenants/{tenantId}/pipeline/owners` → `200 DealOwnerDto[]` (contrato del frontend).

- [ ] **Step 1: Escribir las pruebas que fallan**

Crear `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineOwnersApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>D21–D23 del spec del frontend (2026-10-13) y H43: <c>GET /pipeline/owners</c>, la lista de dueños posibles que el
/// formulario de negocio sólo dibuja. Quién puede ser dueño lo decide el backend: membresía <c>Active</c> del tenant.</summary>
public sealed class PipelineOwnersApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid OwnerMemberId, string OwnerEmail);

    private static string OwnersUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/owners";

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        // La membresía del dueño del tenant no tiene DisplayName: su nombre en la lista es el correo del usuario.
        var email = await ScalarAsync<string>(
            connectionString, "SELECT email FROM identity.users WHERE id = @id", ("id", tenant.OwnerUserId));
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), owner, email);
    }

    private static async Task<DealOwnerDto[]> OwnersAsync(Setup setup)
    {
        var response = await setup.Client.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealOwnerDto[]>(Ct))!;
    }

    // Review Focus 1, 2 y 3: sólo las Active del tenant, ordenadas por nombre en español sin distinguir mayúsculas, y quien no tiene
    // nombre entra con su correo.
    [Fact]
    public async Task ListsOnlyTheActiveMembersOfTheTenantOrderedByName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (zoe, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Zoe Ramos");
        var (angela, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Ángela Ríos");
        var (martha, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "martha lópez");
        foreach (var state in new[] { "Suspended", "Removed", "Invited", "Expired" })
        {
            await SeedMemberAsync(connectionString, setup.Tenant.TenantId, $"Inactiva {state}", state: state);
        }

        var other = await SetupAsync(factory, connectionString);
        using var otherClient = other.Client;
        await SeedMemberAsync(connectionString, other.Tenant.TenantId, "Intrusa Activa");

        var owners = await OwnersAsync(setup);

        Assert.Equal([angela, martha, setup.OwnerMemberId, zoe], owners.Select(owner => owner.MemberId));
        Assert.Equal(["Ángela Ríos", "martha lópez", setup.OwnerEmail, "Zoe Ramos"], owners.Select(owner => owner.Name));
    }

    // Review Focus 4: es un miembro válido (AnActiveMemberWithoutNameOrEmailCanOwnADeal), así que la lista lo trae, y nunca con null.
    [Fact]
    public async Task AnActiveMemberWithoutNameOrEmailIsListedWithAPlaceholder()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (unnamed, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, displayName: null);

        var owners = await OwnersAsync(setup);

        Assert.Equal(DealOwnerDto.UnnamedMemberName, Assert.Single(owners, owner => owner.MemberId == unnamed).Name);
        Assert.Equal(2, owners.Length);
    }

    // Review Focus 5: lo que la lista devuelve se puede usar como dueño al crear (el selector no ofrece lo que el backend rechaza).
    [Fact]
    public async Task EveryListedMemberCanOwnADeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Marta Ruiz");
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, displayName: null);
        await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Pedro Gil", state: "Suspended");
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");

        var owners = await OwnersAsync(setup);

        Assert.Equal(3, owners.Length);
        foreach (var owner in owners)
        {
            var response = await setup.Client.PostAsJsonAsync(
                $"{PipelineUrl(setup.Tenant.TenantId)}/deals",
                new CreateDealRequest(customerId, "Dotación", null, null, null, owner.MemberId), Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    // Review Focus 6: quien sólo lee el embudo no ve la lista (D21), y con la capacidad apagada el código es el del módulo.
    [Fact]
    public async Task WithoutManageTheListIs403AndWithoutTheCapabilityModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);

        var forbidden = await reader.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.GetAsync(OwnersUrl(setup.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }

    // Review Focus 2: la ruta de otro tenant es 403, no 404 ni una lista vacía.
    [Fact]
    public async Task AnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;

        var underForeignRoute = await b.GetAsync(OwnersUrl(setupA.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(underForeignRoute)).Code);
    }
}
```

- [ ] **Step 2: Correr para verificar que falla (RED por compilación)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  Select-Object ProcessId, Name, CommandLine
dotnet build "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-restore
```

Esperado: ningún proceso de `Api` y el build **falla** con `error CS0246: The type or namespace name 'DealOwnerDto' could not be found`. Se copia la línea al handoff.

- [ ] **Step 3: Escribir el DTO, la consulta y el handler**

Crear `src/Modules/Pipeline/Modules.Pipeline.Application/Owners.cs`:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>
/// BFF (D21 del spec del frontend, H43): el formulario de negocio sólo DIBUJA esta lista. Quién puede ser dueño lo decide el
/// backend —una membresía <c>Active</c> del tenant, la misma regla que valida <c>pipeline.deal.owner_not_member</c>—, no la
/// pantalla. Sin este endpoint el selector dependería de <c>GET /memberships</c>, que exige <c>advisorship.read</c> (un permiso de
/// Tenancy ajeno al embudo) y tendría que repetir la regla de la membresía activa. El nombre viaja resuelto
/// (<c>DisplayName</c> o, si no tiene, el correo): la pantalla nunca recibe <c>null</c> ni vacío, que dejaría una opción sin
/// texto.
/// </summary>
public sealed record DealOwnerDto(Guid MemberId, string Name)
{
    /// <summary>Un miembro activo sin nombre ni correo resoluble sigue siendo un dueño válido (<c>AnActiveMemberWithoutNameOrEmailCanOwnADeal</c>):
    /// se lista con este texto en vez de callarlo. Es un caso defensivo; todo usuario tiene correo.</summary>
    public const string UnnamedMemberName = "Miembro sin nombre";
}

public sealed record GetDealOwnersQuery(Guid TenantId) : IQuery<IReadOnlyList<DealOwnerDto>>;

/// <summary>D21 y D22: <c>pipeline.deal.manage</c>, el único permiso que revisa el backend para asignar un negocio (no hay regla
/// de sólo administrador). Las membresías activas salen de <see cref="IPipelineActiveMembers"/>, los nombres de
/// <see cref="IPipelineMemberNames"/>; el orden es por nombre con la cultura <c>es-CO</c> (el acento de <c>Ángela</c> no la manda
/// después de <c>Zoe</c>) y, de desempate, por id.</summary>
public sealed class GetDealOwnersHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineActiveMembers activeMembers,
    IPipelineMemberNames memberNames)
    : IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>
{
    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("es-CO"), ignoreCase: true);

    public async Task<IReadOnlyList<DealOwnerDto>> HandleAsync(GetDealOwnersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealManage, cancellationToken);

        var ids = await activeMembers.ListActiveAsync(query.TenantId, cancellationToken);
        var names = await memberNames.FindAsync(query.TenantId, ids, cancellationToken);

        return
        [
            .. ids
                .Select(id => new DealOwnerDto(id, names.GetValueOrDefault(id) ?? DealOwnerDto.UnnamedMemberName))
                .OrderBy(owner => owner.Name, ByName)
                .ThenBy(owner => owner.MemberId),
        ];
    }
}
```

- [ ] **Step 4: Correr para verificar que el registro falta (RED de composición y de ruta)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~CompositionRootTests"
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~PipelineOwnersApiTests"
```

Esperado:
- El build **compila** (el DTO ya existe).
- `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` **falla** con un mensaje que contiene `GetDealOwnersQuery -> IQueryHandler`2` («Estos casos de uso no tienen su handler registrado…»).
- `PipelineOwnersApiTests`: las cinco **fallan** porque la ruta no existe todavía: las tres que leen la lista, con `Assert.Equal() Failure` por `Expected: OK / Actual: NotFound`, y `WithoutManageTheListIs403…` / `AnotherTenantsRouteIs403`, con `Expected: Forbidden / Actual: NotFound`. Se copian las líneas literales de las dos corridas al handoff.

- [ ] **Step 5: Mapear la ruta**

Crear `src/Modules/Pipeline/Modules.Pipeline.Api/OwnerEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>D21 y D23 del spec del frontend: los posibles dueños de un negocio, para el selector del formulario. Con 403 el
/// frontend oculta el selector y no reintenta, así que el permiso (<c>pipeline.deal.manage</c>) y los códigos del 403 son contrato.</summary>
internal static class OwnerEndpoints
{
    public static void MapOwnerEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/owners", ListOwnersAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Produces<IReadOnlyList<DealOwnerDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListOwnersAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDealOwnersQuery(tenantId), cancellationToken));
}
```

En `src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs`, agregar `group.MapOwnerEndpoints();` después de `group.MapDealEndpoints();`:

```csharp
        group.MapSettingsEndpoints();
        group.MapDealEndpoints();
        group.MapOwnerEndpoints();
        group.MapBoardEndpoints();
```

- [ ] **Step 6: Registrar el handler a mano**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, justo después de la línea de `SetPrimaryQuotationHandler` (último registro del bloque «negocios»):

```csharp
        services.AddScoped<ICommandHandler<SetPrimaryQuotationCommand, DealDetailDto>, SetPrimaryQuotationHandler>();
        // Pipeline (D21 del spec del frontend): los posibles dueños de un negocio, para el selector del formulario.
        services.AddScoped<IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>, GetDealOwnersHandler>();
```

(La primera línea ya existe: es el ancla; sólo se agregan las dos siguientes.)

- [ ] **Step 7: Correr para verificar que pasa**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests\Modules.Pipeline.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~PipelineOwnersApiTests|FullyQualifiedName~PipelineDirectoriesTests|FullyQualifiedName~DealApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~PipelineLayerTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: build sin errores ni advertencias; las cinco pruebas de `PipelineOwnersApiTests`, las de `PipelineDirectoriesTests` y las de `DealApiTests` (que comparten la regla de dueño) en verde; `CompositionRootTests` y `PipelineLayerTests` en verde. Si `AnotherTenantsRouteIs403` o la de 403 fallan con un código distinto de `authorization.denied`, se mira el cuerpo del `ProblemDetails` antes de tocar nada: el permiso de la política y el del handler son el mismo. Si el orden de `ListsOnlyTheActiveMembersOfTheTenantOrderedByName` sale con `Ángela` al final, el entorno corre con `InvariantGlobalization` (la comparación de cultura se vuelve ordinal): se anota en el handoff y no se cambia el comparador sin avisar. Se copia el resumen de las tres corridas al handoff.

- [ ] **Step 8: README**

En `README.md`, sección «Embudo de ventas (Pipeline)», agregar la fila en la tabla de rutas, **después** de la fila de `PATCH /deals/{dealId}` o, si el orden de la tabla lo pide, junto a las de `/deals`:

```markdown
| `GET` | `/owners` | `pipeline.deal.manage` |
```

Y en el párrafo que empieza con «Toda escritura sobre un negocio lleva `version`…», agregar al final esta frase:

```markdown
`GET /owners` devuelve las membresías `Active` del tenant que pueden ser dueñas de un negocio, ordenadas por nombre
(`[{ memberId, name }]`, con el correo cuando no hay nombre): el formulario sólo dibuja la lista y la regla de «dueño
válido» sigue en el backend. Exige `pipeline.deal.manage`; con cualquier 403 el frontend oculta el selector.
```

- [ ] **Step 9: Commit**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pipeline-owners"
if ((git -C $B branch --show-current) -ne "feature/pipeline-owners") { throw "rama equivocada" }
git -C $B status --short
git -C $B add src/Modules/Pipeline/Modules.Pipeline.Application/Owners.cs src/Modules/Pipeline/Modules.Pipeline.Api/OwnerEndpoints.cs src/Modules/Pipeline/Modules.Pipeline.Api/PipelineEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/PipelineOwnersApiTests.cs README.md
git -C $B commit -m "feat(pipeline): GET /pipeline/owners lista los posibles dueños de un negocio"
git -C $B log -1 --format=%B
```

Esperado en `git status --short` antes del `add`: sólo esas seis rutas (y, si aún no se commiteó, este plan); ningún `.csproj`, `packages.lock.json` ni `.cs` de 0 bytes. Esperado en el último comando: el mensaje de una línea, **sin** `Co-Authored-By` ni otra atribución. Si aparece, se enmienda ese commit antes de seguir. **No se empuja ni se mergea:** eso lo decide el developer.

---

## Self-review (hecho al escribir el plan)

- **Cobertura del spec.** D21 (ruta, permiso `pipeline.deal.manage`, activas, orden por nombre, `[{memberId, name}]`, nombre visible o correo, BFF en el DTO) → Task 2 (`Owners.cs`, `OwnerEndpoints.cs`, pruebas 1 y 2). D22 (sólo `manage`, sin regla de administrador) → handler con `DealManage` y prueba 4 (`read` solo da 403). D23 (403 también del lado de la capacidad y de otro tenant, sin reintento del cliente) → pruebas 4 y 5. H43 (reusar `IPipelineActiveMembers` e `IPipelineMemberNames`) → Task 1 y Task 2. §5.5 (selector con cualquier miembro de la lista) → prueba 3 (`EveryListedMemberCanOwnADeal`). README → Task 2, Step 8.
- **Placeholders.** Ninguno: todos los pasos de código llevan el código. Los únicos textos «a confirmar» son el copy `Miembro sin nombre` (O3), que está escrito y se reporta como pregunta de contrato.
- **Consistencia de tipos.** `ListActiveAsync(Guid, CancellationToken) → Task<IReadOnlyList<Guid>>` (Task 1) es lo que llama el handler (Task 2); `DealOwnerDto(Guid MemberId, string Name)` y `GetDealOwnersQuery(Guid TenantId)` se usan igual en el handler, el endpoint, el registro y las pruebas; `IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>` es idéntico en el handler y en el registro (si no, `CompositionRootTests` falla).
- **Review Focus.** Los seis puntos tienen su prueba nombrada en la tarea dueña del código.
