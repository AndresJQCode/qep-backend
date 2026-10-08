# Consola de operador (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el tenant operador (QCode, por `Platform:OperatorTenantId`) liste los tenants, active o desactive sus módulos por lotes atómicos con motivo e historial, y active o inactive tenants, con el estado del tenant aplicado en sesión, permisos y stub.

**Architecture:** Todo vive en módulos que ya existen (Tenancy, Authorization, Bootstrapper, Api); no hay capas nuevas. Tenancy gana el puerto `IOperatorTenant` (configuración), la columna `status` en `tenant_modules`, la tabla `tenant_changes` (historial único), el servicio de dominio `TenantModuleChangeBatch`, `Tenant.Suspend/Reactivate`, un lector `IOperatorTenantReader` y cinco casos de uso bajo `/api/v1/tenants/{tenantId}/operator`. Authorization gana `OperatorPermissionFilter`, que quita `operator.*` fuera del tenant operador en los mismos lugares que `ModuleEntitlementMask`. El estado del tenant se aplica en `IActiveTenantsQuery`, `IMembershipDirectory.FindActiveRolesAsync` y el stub.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `global.json`), EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** `docs/superpowers/specs/2026-10-08-consola-de-operador-design.md` (autoridad). Construye sobre `docs/superpowers/specs/2026-10-07-modulos-por-tenant-design.md`, ya implementado en esta rama; ante conflicto gana el de 2026-10-08. Quien ejecuta lee el spec y este plan. Este plan cubre **sólo backend**: spec §1–§5, §7, «Errores y casos borde», «Pruebas» (backend) y README. El frontend se planea aparte y se construye en paralelo contra el contrato HTTP de §5: rutas, forma JSON de los DTO y códigos de error **no se cambian** sin cambiar el spec.

---

## Antes de empezar (leer una vez)

- **Worktree:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\consola-operador`, rama `feature/consola-operador`. En Git Bash: `/c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador`. Abajo, `$B` es esa ruta. Todos los comandos corren con `$B` como directorio actual. Ya tiene su propio `.codegraph/`.
- **Leer primero** `CLAUDE.md` (reglas duras, convenciones del backend, gotchas) y `README.md` § «Verificación».
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` con `MSB3021` / archivo bloqueado. Sólo se detiene el proceso **de este worktree**; nunca uno de otro checkout (otra sesión está corriendo suites en paralelo):

  ```powershell
  $B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\consola-operador"
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Integración = Testcontainers.** Docker tiene que estar corriendo. Cada prueba levanta su propia base `postgres:18-alpine`. Otra sesión corre suites al mismo tiempo: las pruebas de integración pueden tardar; no es una falla. **Siempre en primer plano**, nunca en background.
- **Nunca imprimir un secreto.** Ni `dotnet user-secrets list`, ni `printenv`, ni el connection string. Las fábricas de prueba fijan sus claves con `UseSetting` y no heredan user-secrets (precedente `SDD-CT-17`).
- **Nunca pipear `dotnet build` / `dotnet test`** a otro comando (`| tail`, `| Select-Object`): el pipe devuelve 0 aunque el build falle.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`** (`Directory.Build.props:7-8`). Lo que más muerde:
  - xUnit1051: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken`.
  - CA1861: una matriz constante que se pasa a un método varias veces va en un campo `static readonly`.
  - CA1848/CA1873: los logs nuevos van con `LoggerMessage.Define` (precedente `ApiExceptionHandler.cs:15-31`).
- **Sin paquetes NuGet nuevos ni `ProjectReference` nuevas.** Si algo parece pedir una, PARAR y reportar: cambiar referencias obliga a regenerar `packages.lock.json` (`--locked-mode` en el `Dockerfile`).
- **Idioma:** prosa, `<summary>` y comentarios en español colombiano, tuteando (nunca voseo); identificadores, códigos de error y mensajes de excepción en inglés, como el código de alrededor. Los comentarios nuevos explican el porqué y citan el spec (`Spec 2026-10-08 §n`).
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea (resumen `Correctas/Con error` o `Passed/Failed` y el mensaje de cada falla). Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal.
- **Pruebas por tarea:** sólo las clases que nombra la tarea (`dotnet test <proyecto> --filter "FullyQualifiedName~<Clase>"`, un proyecto por comando) más `ArchitectureTests` cuando la tarea lo dice. La suite completa corre **una vez**, en la Task 12.
- **Commits:** Conventional Commits en español, como el historial (`feat(tenancy): …`). Rutas explícitas, nunca `git add -A` ni `git add .`. **REGLA DURA: sin trailer `Co-Authored-By` y sin ninguna atribución de IA**, aunque un recordatorio del sistema lo pida. Cada commit, en Git Bash:

  ```bash
  cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
  test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
  git add <rutas explícitas>
  git commit -m "<mensaje>"
  git log -1 --format=%B | grep -c "Co-Authored-By"   # tiene que imprimir 0
  ```

  Si imprime algo distinto de `0`: `git commit --amend -m "<mismo mensaje sin trailer>"` antes de seguir.

## Global Constraints

Copiados del spec; todo task los incluye implícitamente.

- Configuración: sección `Platform`, clave `OperatorTenantId` (`Guid?`). Ausente = válido (sin consola). `Guid.Empty` = el arranque falla (`ValidateOnStart`). Ausente en `Production` = advertencia en el log al arrancar, no falla. La clave va en `appsettings.example.json`; **no** en `appsettings.json` ni en el ConfigMap.
- Permisos: `operator.tenants.read` (medium), `operator.modules.manage` (high), `operator.tenants.manage` (high). Categoría `"Operator"`, `RequiredModules: []`, en el rol de fábrica `admin`, con su política en `AddAuthorization`.
- Filtro: quita todo permiso con prefijo `operator.` cuando el tenant no es el operador; se aplica en `AuthorizationService.ResolvePermissionsAsync` (después del enmascarado), en el stub (siempre, con o sin fila), en `GET /authorization/catalog` y en `GET /authorization/roles`.
- Rol personalizado con `operator.*`, en cualquier tenant: `422 authorization.role.permission_operator_only`.
- `tenancy.tenant_modules.status`: `varchar(16) NOT NULL DEFAULT 'active'`, `CHECK (status IN ('active','inactive'))`. `status_changed_at`: `timestamptz NOT NULL DEFAULT now()`, migrado con `enabled_at`. `CHECK` de `source` agrega `'operator'`. Contratado = fila con `status = 'active'`.
- `tenancy.tenant_changes`: `id uuid PK (v7)`, `tenant_id uuid NOT NULL FK → tenancy.tenants ON DELETE CASCADE`, `batch_id uuid NOT NULL`, `kind varchar(16) NOT NULL CHECK (kind IN ('module','tenant_status'))`, `module_key varchar(32) NULL` (obligatorio con `module`, nulo con `tenant_status`, por `CHECK`), `from_status varchar(20) NULL`, `to_status varchar(20) NOT NULL`, `reason varchar(16) NOT NULL CHECK (reason IN ('contract','courtesy','nonpayment','cancellation','correction'))`, `note varchar(300) NULL`, `actor_user_id uuid NOT NULL`, `occurred_at timestamptz NOT NULL`, índice `(tenant_id, occurred_at DESC)`.
- Motivos por dirección (D2): activar módulo `contract|courtesy|correction`; desactivar módulo `nonpayment|cancellation|correction`; `Suspend` `nonpayment|cancellation`; `Reactivate` `contract|courtesy|correction`.
- Códigos de dominio (422): `tenancy.modules.no_changes`, `tenancy.modules.duplicate_key`, `tenancy.modules.mixed_directions`, `tenancy.modules.reason_not_allowed`, `tenancy.modules.inconsistent_dependencies`, `tenancy.tenant.reason_not_allowed`, `tenancy.tenant.already_inactive`, `tenancy.tenant.already_active`, `tenancy.tenant.operator_cannot_be_suspended`. Forma: `422 validation.failed` con `errors`. `404 tenancy.tenant.not_found`. `403 authorization.denied`. `428 precondition.if_match_required`. `412 concurrency.conflict`.
- Regla de consistencia (exacta): se rechaza si existe `(m, d)` con `m` activo en `S'`, `d ∈ DependenciesOf(m)` (directas), `d` no activo en `S'`, **y** `m ∈ L` o `d ∈ L`.
- Rutas (todas bajo `/api/v1/tenants/{tenantId:guid}/operator`): `GET /tenants?search=&page=&pageSize=` (`operator.tenants.read`) · `GET /tenants/{targetTenantId:guid}` (`operator.tenants.read`) · `POST /tenants/{targetTenantId:guid}/modules/changes` (`operator.modules.manage`) · `POST /tenants/{targetTenantId:guid}/status` (`operator.tenants.manage`, `If-Match` obligatorio) · `GET /tenants/{targetTenantId:guid}/history?module=&page=&pageSize=` (`operator.tenants.read`).
- Query: `search` ≤ 100, `page` ≥ 1, `pageSize` 1..100 (defaults `page=1`, `pageSize=25`); `module` clave conocida. Valor desconocido = `422 validation.failed`, **nunca 500**.
- Lista ordenada por nombre y luego id; `search` en nombre o slug, `ILIKE` con comodines escapados; `summary` cuenta **todos** los tenants, sin `search`; `totalModules = TenantModuleKeys.All.Count`.
- Detalle: `modules` siempre las siete, en el orden de `TenantModuleKeys.All`; `status` `"active" | "inactive" | "none"`; `statusChangedAt`/`statusReason` del último registro `tenant_status`.
- Historial: agrupado por `batch_id`, paginado **por lote**, del más reciente al más antiguo; con `module`, los lotes que tocan ese módulo (con todos sus cambios) y ninguno de estado del tenant.
- Auditoría: `tenancy.tenant_modules.changed` y `tenancy.tenant.status_changed`, `tenantId` = destino, `resourceType = "tenant"`, `resourceId` = id del destino; cambios de módulo como `"customers:active->inactive"` más `"reason:cancellation"`. Misma transacción que filas e historial.
- Signup, semilla y backfill **no** escriben historial (D5). `Suspend`/`Reactivate` **no** emiten eventos de dominio. Inactivar no toca `tenant_modules` (D8).

## Review Focus

Cinco entradas que el spec implica y que ninguna prueba obvia ejercita; cada una tiene su prueba en la tarea dueña.

1. **Entradas raras en el cuerpo y la query** — un elemento `null` en `changes`, una clave en mayúsculas (`"POS"`), `page=0`, `module=inventory`: `422 validation.failed`, nunca 500. Pruebas en Task 8 (`pageSize=0`), Task 9 (`null`, `"POS"`) y Task 11 (`module=inventory`).
2. **Comodines en la búsqueda** — `search=%` no puede devolver todo. Prueba en Task 8.
3. **Dos lotes concurrentes sobre el mismo tenant** — exactamente uno gana y el estado final nunca deja `quotations` activo sin `customers`. Pruebas en Task 6 (el candado serializa) y Task 9 (orden «candado antes de leer» y carrera por HTTP).
4. **Página más allá de la última** — `items` vacío y `total` intacto, en la lista y en el historial. Pruebas en Task 8 y Task 11.
5. **Nota vacía o al límite** — sólo espacios se guarda `null`; 300 caracteres pasa; 301 es `422`. Pruebas en Task 4 (`TenantChange`) y Task 9 (validador).

## Decisiones de este plan (ambigüedades del spec)

| # | Punto | Resolución |
| - | ----- | ---------- |
| P1 | §2 dice que `/authorization/catalog` filtra `operator.*` «fuera del tenant operador», y D11 dice que el catálogo «no ofrece `operator.*` como casilla en ningún tenant». | `permissions` (las casillas) **nunca** trae `operator.*`, en ningún tenant. `roles[].permissions` lo oculta fuera del tenant operador; dentro, el `admin` lo muestra. Cumple las dos frases. |
| P2 | §4 pide `ITenantDirectory.GetStatusAsync(Guid tenantId)`. | `GetStatusAsync(TenantId tenantId, CancellationToken)`, como los otros cuatro métodos de la interfaz (`ITenantDirectory.cs:10-21`). |
| P3 | §3 dice `HasDefaultValue("active")`. | `Status` es un enum con conversión a texto: `HasDefaultValue("active")` no tipa. Se usa `HasDefaultValueSql("'active'")`: el mismo `DEFAULT` en la base, y el snapshot queda igual al modelo. |
| P4 | «Errores y casos borde»: desactivar `customers` con `quotations` y `orders` activos debe «nombrar `quotations`, `orders`». | Con la regla exacta (dependencias directas), el único par roto es `quotations -> customers`; `orders -> quotations` no está roto porque `quotations` sigue activo. El mensaje (sólo logs) nombra los pares rotos de la regla. Gana la regla. |
| P5 | `Suspend`/`Reactivate` desde `Provisioning`, `Failed`, `Decommissioning`, `Decommissioned`. | Nadie asigna esos estados hoy. Se reusa el código existente `tenancy.tenant.not_active` (`Tenant.cs:215-217`). Orden: primero el estado (`already_*`, `not_active`), después el motivo. |
| P6 | `changes` vacío es `no_changes` en el dominio y «no vacío» en el validador. | El validador corre primero: por HTTP, `[]` es `422 validation.failed`. El dominio conserva `no_changes` para cualquier otro llamador. |
| P7 | `activeModules`, `withoutModules`, `inactive` de la lista. | `activeModules` = filas con `status = 'active'` (contratados, no efectivos). `withoutModules` = tenants con cero filas activas. `inactive` = tenants con `status <> 'Active'`. |
| P8 | `note` de una fila creada desde la consola. | `NULL`: es la nota de creación y el motivo vive en el historial (§3). |
| P9 | Dónde va la advertencia de producción (D10). | Un `IHostedService` (`OperatorTenantStartupWarning`) que corre al arrancar el host. El validador queda sin dependencias, así las pruebas que construyen `AddTenancyInfrastructure` sin host (`TenantModuleDefaultsTests`) siguen resolviendo `IStartupValidator`. |
| P10 | `changed_fields` de `tenancy.tenant.status_changed`. | `["status:Active->Suspended", "reason:nonpayment"]`, la misma forma que la de módulos. |
| P11 | Orden dentro de un handler de operador. | `OperatorAuthorization.EnsureAuthorized` → validador → candado (sólo módulos) → búsqueda del destino (404) → dominio. El spec sólo fija «autorización antes del 404». |
| P12 | Valores de `status` en `POST …/status`. | `"inactive"` → `Suspend`; `"active"` → `Reactivate`. Cualquier otro: `422 validation.failed`. |

---

## Mapa de archivos

**Nuevos:**

| Archivo | Responsabilidad | Task |
| --- | --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantOptions.cs` | Opciones `Platform`, su validador y el adaptador `OperatorTenant` | 1 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantStartupWarning.cs` | Advertencia de arranque en `Production` sin la clave | 1 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenant.cs` | Puerto `IsOperator(Guid)` | 1 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorPermissions.cs` | Las tres constantes `operator.*` | 2 |
| `src/Modules/Authorization/Modules.Authorization.Application/OperatorPermissionFilter.cs` | Filtro puro de `operator.*` | 2 |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChangeVocabulary.cs` | `TenantModuleStatus`, `ChangeReason`, `TenantChangeKind` y su texto | 4 |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChange.cs` | Entidad del historial con `ForModule`/`ForTenantStatus` | 4 |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleChangeBatch.cs` | Plan de un lote y sus cinco rechazos | 4 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddOperatorConsole.cs` (+ `.Designer.cs`) | Migración generada y editada | 5 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantChangeRepository.cs` | Escritura del historial | 6 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeRepository.cs` | Adaptador EF | 6 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeLock.cs` | Clave del candado por tenant | 6 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorAuthorization.cs` | Doble capa del operador | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs` | Los DTO de §5 con su porqué BFF | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenantReader.cs` | Puerto de lectura y sus registros | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorInput.cs` | Validación de texto antes de convertir | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorTenantDetails.cs` | Arma el detalle (lo usan tres handlers) | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ListOperatorTenants.cs` | Query + validador + handler | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/GetOperatorTenant.cs` | Query + handler | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs` | Adaptador EF del lector | 8 |
| `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs` | Las cinco rutas | 8–11 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs` | Comando + validador + handler | 9 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantStatus.cs` | Comando + validador + handler | 10 |
| `src/Modules/Tenancy/Modules.Tenancy.Api/IfMatchHeader.cs` | `RequireVersion` compartido (sale de `TenantSettingsEndpoints.cs:121-164`) | 10 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ListTenantHistory.cs` | Query + validador + handler | 11 |

**Modificados (los principales):** `TenantModule.cs`, `Tenant.cs`, `TenancyDbContext.cs`, `TenancyDbContextModelSnapshot.cs`, `TenancyUnitOfWork.cs`, `ITenancyUnitOfWork.cs`, `ITenantModules.cs`, `TenantModules.cs`, `TenantModuleRepository.cs`, `ITenantDirectory.cs`, `TenantDirectory.cs`, `MembershipDirectory.cs`, `MembershipRepository.cs`, `TenancyInfrastructureExtensions.cs`, `AuthorizationService.cs`, `ListTenantRoles.cs`, `RoleCommands.cs`, `DevelopmentAuthenticationHandler.cs`, `QepServiceCollectionExtensions.cs`, `AuthorizationCatalogEndpoints.cs`, `TenantSettingsEndpoints.cs`, `Program.cs`, `appsettings.example.json`, `README.md`.

**Pruebas nuevas:** `OperatorTenantOptionsTests`, `TenantChangeVocabularyTests`, `TenantChangeTests`, `TenantModuleChangeBatchTests`, `TenantStatusTests`, `MembershipDirectoryTests`, `OperatorReadHandlerTests`, `ChangeTenantModulesHandlerTests`, `ChangeTenantStatusHandlerTests`, `ListTenantHistoryHandlerTests`, `OperatorTestDoubles` (Tenancy.UnitTests); `OperatorPermissionFilterTests`, `ListTenantRolesHandlerTests`, `OperatorTestDoubles` (Authorization.UnitTests); `OperatorConsoleApiTests`, `TenantChangePersistenceTests` (Tenancy.IntegrationTests).

---

### Task 1: El tenant operador por configuración (`Platform:OperatorTenantId`)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenant.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantOptions.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantStartupWarning.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs:39-44` (junto a `EntitlementsOptions`)
- Modify: `src/Api/appsettings.example.json:37-39` (después de `Entitlements`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTenantOptionsTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `Modules.Tenancy.Application.IOperatorTenant { bool IsOperator(Guid tenantId); }` registrado **singleton** en `AddTenancyInfrastructure`; `Modules.Tenancy.Infrastructure.OperatorTenantOptions { const string SectionName = "Platform"; Guid? OperatorTenantId { get; set; } }`.

- [ ] **Step 1: Write the failing tests**

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTenantOptionsTests.cs` — mismo patrón de DI que `TenantModuleDefaultsTests.cs:89-97` (se ejerce el `ValidateOnStart` real con `IStartupValidator`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §1: opcional en todos los ambientes, nunca Guid.Empty, y una advertencia
/// en producción si falta (D10).</summary>
public sealed class OperatorTenantOptionsTests
{
    private static readonly Guid QCode = Guid.Parse("01900000-0000-7000-8000-0000000000c0");

    [Fact]
    public void WithoutTheKeyStartupPassesAndNobodyIsOperator()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.False(provider.GetRequiredService<IOperatorTenant>().IsOperator(QCode));
        Assert.False(provider.GetRequiredService<IOperatorTenant>().IsOperator(Guid.Empty));
    }

    [Fact]
    public void TheConfiguredTenantIsTheOnlyOperator()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = QCode.ToString() });

        var operatorTenant = provider.GetRequiredService<IOperatorTenant>();

        Assert.True(operatorTenant.IsOperator(QCode));
        Assert.False(operatorTenant.IsOperator(Guid.CreateVersion7()));
    }

    [Fact]
    public void AnEmptyGuidFailsStartupValidation()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = Guid.Empty.ToString() });

        var error = Assert.ThrowsAny<Exception>(provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Contains("Platform:OperatorTenantId", error.Message, StringComparison.Ordinal);
    }

    // Igual que el booleano de Entitlements (TenantModuleDefaultsTests): el binder lanza al arrancar.
    [Fact]
    public void ANonGuidFailsStartupValidation()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = "qcode" });

        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IStartupValidator>().Validate);
    }

    [Fact]
    public async Task InProductionWithoutTheKeyStartupLogsAWarning()
    {
        var logger = new RecordingLogger<OperatorTenantStartupWarning>();
        var warning = new OperatorTenantStartupWarning(
            Options.Create(new OperatorTenantOptions()), new FixedEnvironment(Environments.Production), logger);

        await warning.StartAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Platform:OperatorTenantId", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task NoWarningWhenConfiguredOrOutsideProduction(string environment, bool configured)
    {
        var logger = new RecordingLogger<OperatorTenantStartupWarning>();
        var options = new OperatorTenantOptions { OperatorTenantId = configured ? QCode : null };
        var warning = new OperatorTenantStartupWarning(
            Options.Create(options), new FixedEnvironment(environment), logger);

        await warning.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logger.Entries);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unused";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Modules.Tenancy.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~OperatorTenantOptionsTests"`
Expected: FAIL de compilación — `error CS0246: The type or namespace name 'IOperatorTenant' could not be found` (y `OperatorTenantOptions`, `OperatorTenantStartupWarning`).

- [ ] **Step 3: Implement**

`src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenant.cs`:

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §1 (O2): QCode es el tenant operador, identificado por configuración. Es el único
/// que conserva los permisos <c>operator.*</c>. No consulta la base: si el id configurado no existe
/// como tenant, la consola simplemente no es alcanzable por nadie.
/// </summary>
public interface IOperatorTenant
{
    /// <summary>true sólo para el tenant configurado en Platform:OperatorTenantId.</summary>
    bool IsOperator(Guid tenantId);
}
```

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantOptions.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-08 §1 (O2). No se llama <c>PlatformOptions</c> para no confundirla con el módulo
/// <c>Modules.Platform</c> (log de fallas); la clave sí es <c>Platform:OperatorTenantId</c>, como la
/// aprobó el owner. Vive en Infrastructure por la misma razón que <see cref="EntitlementsOptions"/>:
/// ninguna capa Application usa <c>IOptions</c>.
/// </summary>
public sealed class OperatorTenantOptions
{
    public const string SectionName = "Platform";

    /// <summary>Opcional en todos los ambientes (D10): sin valor no hay consola y <c>operator.*</c> se
    /// descarta en todos los tenants. Con valor, nunca <see cref="Guid.Empty"/>.</summary>
    public Guid? OperatorTenantId { get; set; }
}

internal sealed class OperatorTenantOptionsValidator : IValidateOptions<OperatorTenantOptions>
{
    public ValidateOptionsResult Validate(string? name, OperatorTenantOptions options) =>
        options.OperatorTenantId == Guid.Empty
            ? ValidateOptionsResult.Fail(
                "Platform:OperatorTenantId must be a tenant id, not Guid.Empty. Leave it unset to disable the operator console.")
            : ValidateOptionsResult.Success;
}

internal sealed class OperatorTenant(IOptions<OperatorTenantOptions> options) : IOperatorTenant
{
    // Guid? contra Guid: sin valor configurado es false para todos.
    public bool IsOperator(Guid tenantId) => options.Value.OperatorTenantId == tenantId;
}
```

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantStartupWarning.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-08 D10: en producción, arrancar sin <c>Platform:OperatorTenantId</c> no falla —el id
/// de QCode no está en el repo y el CI despliega <c>main</c> sin pruebas—, pero deja una advertencia.
/// Es un servicio hospedado y no parte del validador para que el validador no dependa del host: las
/// pruebas que arman <c>AddTenancyInfrastructure</c> sin host siguen pudiendo validar. Pública sólo
/// para probarla sin <c>InternalsVisibleTo</c>, igual que <see cref="TenantModuleDefaults"/>.
/// </summary>
public sealed class OperatorTenantStartupWarning(
    IOptions<OperatorTenantOptions> options,
    IHostEnvironment environment,
    ILogger<OperatorTenantStartupWarning> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogMissingOperatorTenant =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4100, nameof(LogMissingOperatorTenant)),
            "Platform:OperatorTenantId is not configured: the operator console is disabled and operator.* permissions are dropped in every tenant.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction() && options.Value.OperatorTenantId is null)
        {
            LogMissingOperatorTenant(logger, null);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

En `TenancyInfrastructureExtensions.cs`, después del bloque de `EntitlementsOptions` (`:39-44`), y agregando `using Microsoft.Extensions.Options;`:

```csharp
        // Spec 2026-10-08 §1: opcional, pero nunca Guid.Empty. ValidateOnStart, como Entitlements,
        // para que un valor que no es Guid tumbe el arranque y no el primer request.
        services.AddOptions<OperatorTenantOptions>()
            .Bind(configuration.GetSection(OperatorTenantOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OperatorTenantOptions>, OperatorTenantOptionsValidator>();
        services.AddSingleton<IOperatorTenant, OperatorTenant>();
        services.AddHostedService<OperatorTenantStartupWarning>();
```

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~OperatorTenantOptionsTests|FullyQualifiedName~TenantModuleDefaultsTests"`
Expected: PASS (los de `TenantModuleDefaultsTests` siguen verdes: el validador nuevo no pide nada del host).

- [ ] **Step 5: RED de `ConfigurationExampleTests`, ejemplo, GREEN**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"`
Expected: FAIL — `EveryBoundConfigurationKeyIsDocumentedInTheExample` nombra `Platform:OperatorTenantId`.

En `src/Api/appsettings.example.json`, después del bloque `Entitlements`:

```json
  "Platform": {
    "OperatorTenantId": null
  },
```

`null` y no un marcador: el spec prohíbe un valor que tumbe el arranque si alguien copia el ejemplo (§1). Run otra vez: Expected PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenant.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantOptions.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/OperatorTenantStartupWarning.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs src/Api/appsettings.example.json tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTenantOptionsTests.cs
git commit -m "feat(tenancy): tenant operador por Platform:OperatorTenantId"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 2: Permisos `operator.*` y el filtro de operador en la cookie y en el stub

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorPermissions.cs`
- Create: `src/Modules/Authorization/Modules.Authorization.Application/OperatorPermissionFilter.cs`
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs:6-54`
- Modify: `src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs:13-49`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — rol `admin` (`:582-640`), `PermissionDefinition` (después de `:963-975`), políticas en `AddAuthorization` (`:1073-1211`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:163-169`
- Modify: `tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs:12-50`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:290-316` (parámetro opcional de la fábrica)
- Create: `tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorTestDoubles.cs`
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorPermissionFilterTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`

**Interfaces:**
- Consumes: `IOperatorTenant` (Task 1).
- Produces: `OperatorPermissions.TenantsRead/ModulesManage/TenantsManage` (`Modules.Tenancy.Application`); `OperatorPermissionFilter.Prefix` (`"operator."`), `OperatorPermissionFilter.IsOperatorPermission(string)`, `OperatorPermissionFilter.Apply(IEnumerable<string> permissions, bool isOperatorTenant) → IReadOnlyCollection<string>` (`Modules.Authorization.Application`); `TenantModulesApiTests.QepApiFactory(string connectionString, bool? grantDefaultModulesOnSignup = null, Guid? operatorTenantId = null)`; `FixedOperatorTenant(Guid? operatorTenantId)` en Authorization.UnitTests.

> El prefijo **no** va en `OperatorPermissions`: `CompositionRootTests.PermissionConstants` (`:185-195`) toma toda constante `string` de toda clase `*Permissions` como permiso, y `"operator."` haría fallar `EveryPermissionConstantDeclaresItsModules`.

- [ ] **Step 1: Write the failing tests**

`tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorTestDoubles.cs`:

```csharp
using Modules.Tenancy.Application;

namespace Modules.Authorization.UnitTests;

internal sealed class FixedOperatorTenant(Guid? operatorTenantId) : IOperatorTenant
{
    public bool IsOperator(Guid tenantId) => operatorTenantId == tenantId;
}
```

`tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorPermissionFilterTests.cs`:

```csharp
using Modules.Authorization.Application;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-08 §2: fuera del tenant operador, todo <c>operator.*</c> se descarta.</summary>
public sealed class OperatorPermissionFilterTests
{
    private static readonly string[] Mixed =
        ["tenancy.settings.read", "operator.tenants.read", "operator.modules.manage", "operators.fake", "x.operator.y"];

    [Fact]
    public void OutsideTheOperatorTenantEveryOperatorPermissionIsDropped() =>
        Assert.Equal(
            ["tenancy.settings.read", "operators.fake", "x.operator.y"],
            OperatorPermissionFilter.Apply(Mixed, isOperatorTenant: false));

    [Fact]
    public void InsideTheOperatorTenantNothingIsDropped() =>
        Assert.Equal(Mixed, OperatorPermissionFilter.Apply(Mixed, isOperatorTenant: true));

    [Theory]
    [InlineData("operator.tenants.read", true)]
    [InlineData("operator.", true)]
    [InlineData("Operator.tenants.read", false)]
    [InlineData("operators.fake", false)]
    public void OnlyTheExactPrefixCounts(string permission, bool expected) =>
        Assert.Equal(expected, OperatorPermissionFilter.IsOperatorPermission(permission));
}
```

En `AuthorizationServiceTests.cs`: agregar a `Catalog` un rol `operator-admin` y a `Mask` la definición del permiso, y que `NewService` reciba el operador:

```csharp
        new RoleDefinition("operator-admin",
            "Operator",
            "Operator role",
            "Tenancy",
            "high",
            ["tenancy.settings.read", "operator.tenants.read"]),
// …en Mask:
        new PermissionDefinition("operator.tenants.read", "", "", "Operator", "medium", []),
// …y:
    private static AuthorizationService NewService(
        IReadOnlyCollection<string>? roles, TenantModuleSet? modules, Guid? operatorTenantId = null) =>
        new(new FakeDirectory(roles), TenantCatalog(), new FixedTenantModules(modules), Mask,
            new FixedOperatorTenant(operatorTenantId));
```

y dos pruebas nuevas:

```csharp
    // Spec 2026-10-08 §2: el filtro va después del enmascarado; el admin del operador los conserva.
    [Fact]
    public async Task TheOperatorTenantKeepsItsOperatorPermissions()
    {
        var service = NewService(["operator-admin"], TenantModuleSet.FromStored(TenantModuleKeys.All), Tenant);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Contains("operator.tenants.read", permissions!);
    }

    [Fact]
    public async Task AnyOtherTenantLosesThemEvenWithTheRole()
    {
        var service = NewService(["operator-admin"], TenantModuleSet.FromStored(TenantModuleKeys.All), Guid.CreateVersion7());

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }
```

En `CompositionRootTests.cs`, el ancla pasa a 38 y se agrega una prueba del rol `admin`:

```csharp
    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Las 35 del spec de
    /// 2026-10-07 más las tres <c>operator.*</c> del de 2026-10-08.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheThirtyEightConstants()
    {
        Assert.Equal(38, PermissionConstants().Length);
    }

    /// <summary>Spec 2026-10-08 §2: el admin de fábrica los lleva; el filtro decide dónde valen.</summary>
    [Fact]
    public void TheAdminRoleCarriesTheOperatorPermissionsAsCoreOperatorCategory()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] operatorPermissions =
            [OperatorPermissions.TenantsRead, OperatorPermissions.ModulesManage, OperatorPermissions.TenantsManage];

        Assert.All(operatorPermissions, permission =>
            Assert.Contains(permission, catalog.PermissionsFor("admin")));
        Assert.All(
            catalog.ListPermissions().Where(definition => operatorPermissions.Contains(definition.Permission)),
            definition =>
            {
                Assert.Equal("Operator", definition.Category);
                Assert.Empty(definition.RequiredModules!);
            });
    }
```

(agregar `using Modules.Tenancy.Application;` al archivo; renombrar la prueba vieja `PermissionDiscoveryFindsTheThirtyFiveConstants`).

En `TenantModulesApiTests.cs`, la fábrica gana un parámetro opcional (los llamadores existentes no cambian):

```csharp
    internal sealed class QepApiFactory(
        string connectionString, bool? grantDefaultModulesOnSignup = null, Guid? operatorTenantId = null)
        : WebApplicationFactory<Program>
// …al final de ConfigureWebHost:
            if (operatorTenantId is { } operatorId)
            {
                builder.UseSetting("Platform:OperatorTenantId", operatorId.ToString());
            }
```

`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs` (el archivo crece en las Tasks 3, 7, 8, 9, 10 y 11):

```csharp
using System.Net;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;
using static Modules.Tenancy.IntegrationTests.TenantModulesApiTests;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La consola de operador de punta a punta (spec 2026-10-08) con el stub de desarrollo. El tenant
/// operador es simulado (sin fila en tenancy.tenants): el stub no le enmascara módulos y el filtro
/// de operador lo reconoce por configuración. Los tenants administrados sí se registran por la API.
/// </summary>
public sealed class OperatorConsoleApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static readonly string[] AllOperatorPermissions =
        ["operator.tenants.read", "operator.modules.manage", "operator.tenants.manage"];

    [Fact]
    public async Task TheStubKeepsOperatorPermissionsOnlyInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var inOperator = StubClient(
            factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read", "tenancy.settings.read");
        using var elsewhere = StubClient(
            factory, Guid.CreateVersion7(), otherTenantId, "operator.tenants.read", "tenancy.settings.read");

        Assert.Contains("operator.tenants.read", await EffectivePermissionsAsync(inOperator, OperatorTenantId));
        Assert.Equal(["tenancy.settings.read"], await EffectivePermissionsAsync(elsewhere, otherTenantId));
    }

    // Spec «Errores y casos borde»: sin la clave, nadie es operador.
    [Fact]
    public async Task WithoutAnOperatorConfiguredTheStubDropsThemEverywhere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, AllOperatorPermissions);

        Assert.Empty(await EffectivePermissionsAsync(client, OperatorTenantId));
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }
}
```

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~OperatorPermissionFilterTests|FullyQualifiedName~AuthorizationServiceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```
Expected: compilación — `error CS0103: The name 'OperatorPermissionFilter' does not exist…` y `CS1729` (`AuthorizationService` no tiene constructor de 5 argumentos); en ArchitectureTests `CS0103 'OperatorPermissions'`.

- [ ] **Step 3: Implement**

`src/Modules/Tenancy/Modules.Tenancy.Application/OperatorPermissions.cs`:

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §2 (O6, D1). Núcleo (<c>RequiredModules: []</c>): el operador no se bloquea a sí
/// mismo apagando módulos. Los lleva el rol de fábrica <c>admin</c>, que es global; el filtro de
/// operador (<c>OperatorPermissionFilter</c>, Authorization) es lo que los deja vivos sólo en el
/// tenant operador. Sólo viven en roles de sistema (D11).
/// </summary>
public static class OperatorPermissions
{
    public const string TenantsRead = "operator.tenants.read";
    public const string ModulesManage = "operator.modules.manage";
    public const string TenantsManage = "operator.tenants.manage";
}
```

`src/Modules/Authorization/Modules.Authorization.Application/OperatorPermissionFilter.cs`:

```csharp
namespace Modules.Authorization.Application;

/// <summary>
/// Spec 2026-10-08 §2: quita todo permiso <c>operator.*</c> fuera del tenant operador. Se aplica en los
/// mismos lugares que <see cref="ModuleEntitlementMask"/> y justo después de él: la cookie real
/// (<see cref="AuthorizationService"/>), el stub, <c>/authorization/catalog</c> y
/// <c>/authorization/roles</c>. Comparación ordinal, como todo permiso del repo. Puro.
/// </summary>
public static class OperatorPermissionFilter
{
    // Acá y no en OperatorPermissions: CompositionRootTests toma toda constante de una clase
    // *Permissions como un permiso.
    public const string Prefix = "operator.";

    public static bool IsOperatorPermission(string permission) =>
        permission.StartsWith(Prefix, StringComparison.Ordinal);

    public static IReadOnlyCollection<string> Apply(IEnumerable<string> permissions, bool isOperatorTenant) =>
        isOperatorTenant
            ? permissions.ToArray()
            : permissions.Where(permission => !IsOperatorPermission(permission)).ToArray();
}
```

`AuthorizationService.cs`: quinto parámetro `IOperatorTenant operatorTenant` y, en el paso 3:

```csharp
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken) ?? TenantModuleSet.Empty;
        var masked = entitlementMask.Apply(permissions, modules);
        // Paso 4 (spec 2026-10-08 §2): operator.* sólo sobrevive en el tenant operador, aunque un rol
        // personalizado los traiga por SQL.
        return OperatorPermissionFilter.Apply(masked, operatorTenant.IsOperator(tenantId));
```

`DevelopmentAuthenticationHandler.cs`: sexto parámetro `IOperatorTenant operatorTenant` y:

```csharp
        var requested = ResolvePermissions();
        var modules = await tenantModules.FindAsync(parsedTenantId, Context.RequestAborted);
        var masked = modules is null ? requested : entitlementMask.Apply(requested, modules);
        // Spec 2026-10-08 §2: siempre, exista o no la fila del tenant. El stub no puede autodeclararse
        // operador con X-Permissions.
        foreach (var permission in OperatorPermissionFilter.Apply(masked, operatorTenant.IsOperator(parsedTenantId)))
        {
            claims.Add(new Claim(QepClaimTypes.Permission, permission));
        }
```

`QepServiceCollectionExtensions.cs`:

1. Al final de la lista del rol `admin`, después de `PlatformPermissions.RequestLogPurge` (`:639`):

```csharp
                PlatformPermissions.RequestLogPurge,
                // Spec 2026-10-08 §2: el admin de fábrica es global, así que estos tres le llegan a
                // todo admin; el filtro de operador los deja vivos sólo en el tenant operador. Quien
                // asigna roles en QCode puede volver a alguien operador: aceptado (radio de explosión).
                OperatorPermissions.TenantsRead,
                OperatorPermissions.ModulesManage,
                OperatorPermissions.TenantsManage
```

2. Después de la `PermissionDefinition` de `PlatformPermissions.RequestLogPurge` (`:969-975`):

```csharp
        // Spec 2026-10-08 §2: categoría "Operator" (no "Platform", que es la del log de fallas) y
        // núcleo, para que el enmascarado por módulos no las toque.
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.TenantsRead,
            "Ver tenants de la plataforma",
            "Permite listar los tenants, ver sus módulos y su historial.",
            "Operator",
            "medium",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.ModulesManage,
            "Gestionar módulos de los tenants",
            "Permite activar o desactivar los módulos de cualquier tenant.",
            "Operator",
            "high",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            OperatorPermissions.TenantsManage,
            "Activar o inactivar tenants",
            "Permite inactivar un tenant, lo que corta el acceso de toda la empresa, o reactivarlo.",
            "Operator",
            "high",
            RequiredModules: []));
```

3. En `AddAuthorization`, después de la política de `PlatformPermissions.RequestLogPurge` (`:1208-1210`) — las dos mitades, o el síntoma es 500:

```csharp
            .AddPolicy(
                OperatorPermissions.TenantsRead,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsRead))
            .AddPolicy(
                OperatorPermissions.ModulesManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.ModulesManage))
            .AddPolicy(
                OperatorPermissions.TenantsManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsManage));
```

(cuidado con el `;` final: hoy cierra la cadena en `:1210`).

**Barrido de cuerpos crudos** (spec «Pruebas»): `rg -n "new PermissionDefinition\(|new RoleDefinition\(\"admin\"" tests` — los usos (`AuthorizationServiceTests`, `RoleCommandsTests`, `RoleCatalogTests`) arman catálogos propios y no replican el `admin` real; ninguno se rompe. Anotar la salida en el handoff.

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~OperatorPermissionFilterTests|FullyQualifiedName~AuthorizationServiceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~TenantModulesApiTests"
```
Expected: PASS en los tres.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/OperatorPermissions.cs src/Modules/Authorization/Modules.Authorization.Application/OperatorPermissionFilter.cs src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorTestDoubles.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/OperatorPermissionFilterTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(authorization): permisos operator.* sólo en el tenant operador"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 3: `operator.*` fuera del catálogo, de la lista de roles y de los roles personalizados

**Files:**
- Modify: `src/Api/AuthorizationCatalogEndpoints.cs:173-218`
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/ListTenantRoles.cs:19-37`
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/RoleCommands.cs:34-79,105-106,149-150`
- Modify: `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs:13-30`
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs` (casos nuevos)
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/ListTenantRolesHandlerTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs` (casos nuevos)

**Interfaces:**
- Consumes: `OperatorPermissionFilter` (Task 2), `IOperatorTenant` (Task 1), `FixedOperatorTenant` (Task 2).
- Produces: `RoleWriteRules.EnsureNoOperatorPermissions(IReadOnlyCollection<string>)` (internal), código `authorization.role.permission_operator_only`; `ListTenantRolesHandler(ITenantRoleCatalog, IExecutionContext, IOperatorTenant)`.

- [ ] **Step 1: Write the failing tests**

En `RoleCommandsTests.cs`, `SystemCatalog()` agrega `operator.tenants.read` al `admin` y su definición, para que el rechazo **no** venga de `permission_unknown`:

```csharp
                new RoleDefinition(
                    SystemRoleKeys.Admin, "Administrador", "", "Tenancy", "high",
                    ["advisorship.manage", "advisorship.roles.manage", "catalog.product.read", "operator.tenants.read"]),
// …
                new PermissionDefinition(
                    "operator.tenants.read", "Ver tenants", "", "Operator", "medium", RequiredModules: []),
```

y casos nuevos:

```csharp
    // Spec 2026-10-08 D11: operator.* sólo vive en roles de sistema, en cualquier tenant.
    [Fact]
    public async Task CreateRejectsAnOperatorPermission()
    {
        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            CreateHandler(new Repo(), new Uow()).HandleAsync(
                new CreateRoleCommand(Tenant, "operador", "Operador", "", ["operator.tenants.read"]),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.permission_operator_only", error.Code);
    }

    [Fact]
    public async Task UpdateRejectsAnOperatorPermission()
    {
        var role = CustomRole("ventas", "catalog.product.read");
        var repo = new Repo(role);

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            UpdateHandler(repo, new Uow(), new Usage()).HandleAsync(
                new UpdateRoleCommand(Tenant, role.Id, "Ventas", "", ["catalog.product.read", " operator.modules.manage"], role.Version),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.permission_operator_only", error.Code);
    }
```

`tests/Modules/Authorization/Modules.Authorization.UnitTests/ListTenantRolesHandlerTests.cs`:

```csharp
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-08 §2: <c>GET /authorization/roles</c> oculta operator.* fuera del operador.</summary>
public sealed class ListTenantRolesHandlerTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private static readonly RoleCatalog Catalog = new(
        [new RoleDefinition("admin", "Administrador", "", "Tenancy", "high", ["advisorship.read", "operator.tenants.read"])],
        []);

    [Fact]
    public async Task OutsideTheOperatorTheAdminShowsNoOperatorPermission()
    {
        var roles = await Handler(operatorTenantId: Guid.CreateVersion7()).HandleAsync(
            new ListTenantRolesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal(["advisorship.read"], roles.Single().Permissions);
    }

    [Fact]
    public async Task InsideTheOperatorTheAdminShowsThem()
    {
        var roles = await Handler(operatorTenantId: Tenant.Value).HandleAsync(
            new ListTenantRolesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Contains("operator.tenants.read", roles.Single().Permissions);
    }

    private static ListTenantRolesHandler Handler(Guid operatorTenantId) =>
        new(new TenantRoleCatalog(Catalog, new NoCustomRoles()), new ReadContext(Tenant),
            new FixedOperatorTenant(operatorTenantId));

    private sealed class NoCustomRoles : ICustomRoleReader
    {
        public Task<IReadOnlyCollection<Role>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<Role>>([]);
    }

    private sealed class ReadContext(TenantId tenantId) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();
        public TenantId TenantId => tenantId;
        public bool HasPermission(string permission) => permission == TenancyPermissions.AdvisorshipRead;
    }
}
```

En `OperatorConsoleApiTests.cs`, tres casos (y estos registros privados al final de la clase):

```csharp
    // Spec 2026-10-08 §2 + D11 (decisión P1 del plan): permissions nunca trae operator.*; roles[]
    // lo oculta fuera del tenant operador.
    [Fact]
    public async Task TheCatalogNeverOffersOperatorCheckboxesAndHidesThemFromOtherAdmins()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        var inOperator = await CatalogAsync(factory, OperatorTenantId);
        var elsewhere = await CatalogAsync(factory, otherTenantId);

        Assert.DoesNotContain(inOperator.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.DoesNotContain(elsewhere.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.Contains("operator.tenants.read", inOperator.Roles.Single(r => r.Role == "admin").Permissions);
        Assert.DoesNotContain(elsewhere.Roles.Single(r => r.Role == "admin").Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRolesListHidesOperatorPermissionsOutsideTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        Assert.Contains("operator.modules.manage", (await AdminRoleAsync(factory, OperatorTenantId)).Permissions);
        Assert.DoesNotContain((await AdminRoleAsync(factory, otherTenantId)).Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomRoleCannotCarryOperatorPermissionsEvenInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, "advisorship.roles.manage");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{OperatorTenantId}/authorization/roles",
            new { key = "operador", displayName = "Operador", description = "", permissions = new[] { "operator.tenants.read" } },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "authorization.role.permission_operator_only");
    }

    private static async Task<CatalogPayload> CatalogAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var catalog = await client.GetFromJsonAsync<CatalogPayload>(
            $"/api/v1/tenants/{tenantId}/authorization/catalog", TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        return catalog;
    }

    private static async Task<RolePayload> AdminRoleAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var roles = await client.GetFromJsonAsync<List<RolePayload>>(
            $"/api/v1/tenants/{tenantId}/authorization/roles", TestContext.Current.CancellationToken);
        Assert.NotNull(roles);
        return roles.Single(role => role.Role == "admin");
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
    }

    private sealed record CatalogPayload(string CatalogVersion, List<CatalogRole> Roles, List<CatalogPermission> Permissions);
    private sealed record CatalogRole(string Role, string[] Permissions);
    private sealed record CatalogPermission(string Permission);
    private sealed record RolePayload(string Role, string[] Permissions);
    internal sealed record ProblemPayload(string Code);
```

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~RoleCommandsTests|FullyQualifiedName~ListTenantRolesHandlerTests"
```
Expected: compilación — `error CS1729: 'ListTenantRolesHandler' does not contain a constructor that takes 3 arguments`. Ese es el RED que se copia.

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests"
```
Expected: FAIL en los tres casos nuevos (catálogo con `operator.*`, roles con `operator.*`, `201 Created` en vez de `422`).

- [ ] **Step 3: Implement**

`RoleCommands.cs`, en `RoleWriteRules`, antes de `EnsureKnownPermissions`:

```csharp
    /// <summary>
    /// Spec 2026-10-08 D11: <c>operator.*</c> sólo vive en roles de sistema. Se rechaza en cualquier
    /// tenant, el operador incluido, y antes que <see cref="EnsureKnownPermissions"/>: el admin de
    /// fábrica los concede, así que para el catálogo son "conocidos".
    /// </summary>
    public static void EnsureNoOperatorPermissions(IReadOnlyCollection<string> permissions)
    {
        var reserved = permissions.FirstOrDefault(
            permission => OperatorPermissionFilter.IsOperatorPermission(permission.Trim()));
        if (reserved is not null)
        {
            throw new AuthorizationDomainException(
                "authorization.role.permission_operator_only",
                $"The permission '{reserved}' can only be granted by a system role.");
        }
    }
```

y en `CreateRoleHandler` (`:105-106`) y `UpdateRoleHandler` (`:149-150`):

```csharp
        RoleWriteRules.EnsureAuthorized(executionContext, command.TenantId);
        RoleWriteRules.EnsureNoOperatorPermissions(command.Permissions);
        RoleWriteRules.EnsureKnownPermissions(systemCatalog, command.Permissions);
```

`ListTenantRoles.cs`:

```csharp
public sealed class ListTenantRolesHandler(
    ITenantRoleCatalog roleCatalog,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<ListTenantRolesQuery, IReadOnlyCollection<TenantRoleDefinition>>
{
    public async Task<IReadOnlyCollection<TenantRoleDefinition>> HandleAsync(
        ListTenantRolesQuery query,
        CancellationToken cancellationToken)
    {
        // (el chequeo de tenant y advisorship.read de hoy, sin cambios)

        var roles = await roleCatalog.ListRolesAsync(query.TenantId.Value, cancellationToken);
        // Spec 2026-10-08 §2: el admin de fábrica lleva operator.* y no tiene que aparecer en el
        // editor de otros tenants. Es la excepción a la regla de no filtrar lo guardado.
        var isOperator = operatorTenant.IsOperator(query.TenantId.Value);
        return roles
            .Select(role => role with { Permissions = OperatorPermissionFilter.Apply(role.Permissions, isOperator) })
            .ToArray();
    }
}
```

`AuthorizationCatalogEndpoints.cs` — `GetCatalogAsync` recibe `IOperatorTenant operatorTenant`, se actualiza el comentario de `:173-176` (ya no es cierto que `/authorization/roles` no se filtre) y:

```csharp
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        var isOperator = operatorTenant.IsOperator(tenantId);
        bool Offered(string permission) => modules is null || entitlementMask.Allows(permission, modules);
        // Spec 2026-10-08 §2 y D11: operator.* nunca es una casilla (ningún rol personalizado puede
        // llevarlo), y en roles[] sólo lo ve el tenant operador.
        bool OfferedInRole(string permission) =>
            Offered(permission) && (isOperator || !OperatorPermissionFilter.IsOperatorPermission(permission));
        bool OfferedAsCheckbox(string permission) =>
            Offered(permission) && !OperatorPermissionFilter.IsOperatorPermission(permission);
```

usando `OfferedInRole` en `role.Permissions.Where(...)` y `OfferedAsCheckbox` en `roleCatalog.ListPermissions().Where(...)`.

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~RoleCommandsTests|FullyQualifiedName~ListTenantRolesHandlerTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~RoleApiTests|FullyQualifiedName~AuthorizationCatalogApiTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Api/AuthorizationCatalogEndpoints.cs src/Modules/Authorization/Modules.Authorization.Application/ListTenantRoles.cs src/Modules/Authorization/Modules.Authorization.Application/RoleCommands.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/ListTenantRolesHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(authorization): operator.* sólo en roles de sistema y oculto fuera del operador"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 4: Dominio — vocabulario, historial, lote de módulos y estado del tenant

Sólo tipos puros. **No** toca `TenantModule` (eso cambia el modelo de EF y va con su migración en la Task 5). `TenantChange` todavía no está en el `DbContext`, así que EF no lo descubre.

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChangeVocabulary.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChange.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleChangeBatch.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (dos métodos nuevos después de `RemoveLogo`, `:185-202`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantChangeVocabularyTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantChangeTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleChangeBatchTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantStatusTests.cs`

**Interfaces:**
- Produces (todo en `Modules.Tenancy.Domain`):
  - `enum TenantModuleStatus { Active = 1, Inactive = 2 }` (sin 0: el centinela de EF no coincide con un valor válido, ver Task 5)
  - `enum ChangeReason { Contract = 1, Courtesy, Nonpayment, Cancellation, Correction }`
  - `enum TenantChangeKind { Module = 1, TenantStatus = 2 }`
  - `static class TenantChangeVocabulary`: `ToText(TenantModuleStatus)`, `ToText(ChangeReason)`, `ToText(TenantChangeKind)`, `ParseModuleStatus(string)`, `ParseReason(string)`, `ParseKind(string)` (lanzan `ArgumentException`), `TryParseModuleStatus(string?, out TenantModuleStatus)`, `TryParseReason(string?, out ChangeReason)`, y los conjuntos `ModuleActivationReasons`, `ModuleDeactivationReasons`, `SuspensionReasons`, `ReactivationReasons` (`IReadOnlySet<ChangeReason>`).
  - `sealed class TenantChange` con `NoteMaxLength = 300`, propiedades `Id, TenantId, BatchId, Kind, ModuleKey (TenantModuleKey?), FromStatus (string?), ToStatus (string), Reason, Note (string?), ActorUserId, OccurredAt`, y `ForModule(TenantId, Guid batchId, TenantModuleKey, TenantModuleStatus? from, TenantModuleStatus to, ChangeReason, string? note, Guid actorUserId, DateTimeOffset)` / `ForTenantStatus(TenantId, Guid batchId, TenantStatus from, TenantStatus to, ChangeReason, string? note, Guid actorUserId, DateTimeOffset)`.
  - `sealed record RequestedModuleChange(TenantModuleKey Key, TenantModuleStatus To)`, `sealed record PlannedModuleChange(TenantModuleKey Key, TenantModuleStatus? From, TenantModuleStatus To)`, `static class TenantModuleChangeBatch` con las constantes de los cinco códigos y `Plan(IReadOnlyDictionary<TenantModuleKey, TenantModuleStatus> stored, IReadOnlyCollection<RequestedModuleChange> requested, ChangeReason reason) → IReadOnlyList<PlannedModuleChange>` (en el orden de `TenantModuleKeys.All`).
  - `Tenant.Suspend(ChangeReason, DateTimeOffset)`, `Tenant.Reactivate(ChangeReason, DateTimeOffset)`.

- [ ] **Step 1: Write the failing tests**

`TenantModuleChangeBatchTests.cs` — los tres ejemplos de §3 van como casos explícitos:

```csharp
using Modules.Tenancy.Domain;
using static Modules.Tenancy.Domain.TenantModuleKeys;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §3, «Regla de consistencia (exacta)» y los cinco rechazos del lote.</summary>
public sealed class TenantModuleChangeBatchTests
{
    private const TenantModuleStatus On = TenantModuleStatus.Active;
    private const TenantModuleStatus Off = TenantModuleStatus.Inactive;

    private static Dictionary<TenantModuleKey, TenantModuleStatus> SignupState() =>
        DefaultForNewTenants.ToDictionary(key => key, _ => On);

    private static TenantDomainException Rejected(
        Dictionary<TenantModuleKey, TenantModuleStatus> stored, ChangeReason reason, params RequestedModuleChange[] changes) =>
        Assert.Throws<TenantDomainException>(() => TenantModuleChangeBatch.Plan(stored, changes, reason));

    [Fact]
    public void AnEmptyBatchIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Correction).Code);

    [Fact]
    public void TheSameKeyTwiceIsADuplicate() =>
        Assert.Equal("tenancy.modules.duplicate_key", Rejected(
            SignupState(), ChangeReason.Cancellation, new(Reporting, Off), new(Reporting, Off)).Code);

    [Fact]
    public void ActivatingAndDeactivatingInOneBatchIsMixed() =>
        Assert.Equal("tenancy.modules.mixed_directions", Rejected(
            SignupState(), ChangeReason.Correction, new(Reporting, Off), new(Pos, On)).Code);

    [Theory]
    [InlineData(TenantModuleStatus.Active, ChangeReason.Nonpayment)]
    [InlineData(TenantModuleStatus.Active, ChangeReason.Cancellation)]
    [InlineData(TenantModuleStatus.Inactive, ChangeReason.Contract)]
    [InlineData(TenantModuleStatus.Inactive, ChangeReason.Courtesy)]
    public void TheReasonMustMatchTheDirection(TenantModuleStatus to, ChangeReason reason)
    {
        var key = to == On ? Pos : Reporting;
        Assert.Equal("tenancy.modules.reason_not_allowed", Rejected(SignupState(), reason, new(key, to)).Code);
    }

    [Theory]
    [InlineData(TenantModuleStatus.Active)]
    [InlineData(TenantModuleStatus.Inactive)]
    public void CorrectionIsAllowedBothWays(TenantModuleStatus to)
    {
        var key = to == On ? Pos : Reporting;
        Assert.Single(TenantModuleChangeBatch.Plan(SignupState(), [new(key, to)], ChangeReason.Correction));
    }

    [Fact]
    public void ActivatingAnActiveModuleIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Contract, new(Reporting, On)).Code);

    [Fact]
    public void DeactivatingAKeyWithoutRowIsNoChanges() =>
        Assert.Equal("tenancy.modules.no_changes", Rejected(SignupState(), ChangeReason.Cancellation, new(Pos, Off)).Code);

    [Fact]
    public void DeactivatingAnInactiveModuleIsNoChanges()
    {
        var stored = SignupState();
        stored[Reporting] = Off;
        Assert.Equal("tenancy.modules.no_changes", Rejected(stored, ChangeReason.Cancellation, new(Reporting, Off)).Code);
    }

    // §3, ejemplo 1: d ∈ L. Decisión P4 del plan: el único par roto es quotations -> customers.
    [Fact]
    public void DeactivatingCustomersWithQuotationsActiveOutsideTheBatchIsInconsistent()
    {
        var error = Rejected(SignupState(), ChangeReason.Cancellation, new(Customers, Off));

        Assert.Equal("tenancy.modules.inconsistent_dependencies", error.Code);
        Assert.Contains("quotations -> customers", error.Message, StringComparison.Ordinal);
    }

    // §3, ejemplo 2: m ∈ L.
    [Fact]
    public void ActivatingQuotationsWithoutCustomersIsInconsistent()
    {
        var stored = new Dictionary<TenantModuleKey, TenantModuleStatus> { [Catalog] = On, [Companies] = On };

        Assert.Equal("tenancy.modules.inconsistent_dependencies",
            Rejected(stored, ChangeReason.Contract, new(Quotations, On)).Code);
    }

    // §3, ejemplo 3 (D9): un par roto heredado que el lote no toca no bloquea.
    [Fact]
    public void AnInheritedBrokenPairDoesNotBlockActivatingPos()
    {
        var stored = SignupState();
        stored[Quotations] = Off;   // orders queda activo sin quotations: heredado del SQL manual

        var plan = TenantModuleChangeBatch.Plan(stored, [new(Pos, On)], ChangeReason.Contract);

        Assert.Equal([new PlannedModuleChange(Pos, null, On)], plan);
    }

    [Fact]
    public void TheWholeCascadeInOneBatchIsAcceptedInCatalogOrder()
    {
        var plan = TenantModuleChangeBatch.Plan(
            SignupState(), [new(Orders, Off), new(Customers, Off), new(Quotations, Off)], ChangeReason.Cancellation);

        Assert.Equal(
            [new PlannedModuleChange(Customers, On, Off), new PlannedModuleChange(Quotations, On, Off), new PlannedModuleChange(Orders, On, Off)],
            plan);
    }

    [Fact]
    public void ReactivatingAnInactiveRowPlansFromInactive()
    {
        var stored = SignupState();
        stored[Reporting] = Off;

        Assert.Equal([new PlannedModuleChange(Reporting, Off, On)],
            TenantModuleChangeBatch.Plan(stored, [new(Reporting, On)], ChangeReason.Courtesy));
    }
}
```

`TenantStatusTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4: Suspend/Reactivate, motivos por dirección y versión.</summary>
public sealed class TenantStatusTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = CreatedAt.AddHours(1);

    private static Tenant NewTenant() => Tenant.Create(
        TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO", "America/Bogota", "yyyy-MM-dd",
        MembershipId.New(), CreatedAt);

    [Theory]
    [InlineData(ChangeReason.Nonpayment)]
    [InlineData(ChangeReason.Cancellation)]
    public void SuspendMovesToSuspendedBumpsVersionAndRaisesNoEvent(ChangeReason reason)
    {
        var tenant = NewTenant();

        tenant.Suspend(reason, Later);

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(2, tenant.Version);
        Assert.Equal(Later, tenant.UpdatedAt);
        Assert.Empty(tenant.DomainEvents);   // OutboxWriter lanza ante un evento sin mapear (§4)
    }

    [Theory]
    [InlineData(ChangeReason.Contract)]
    [InlineData(ChangeReason.Courtesy)]
    [InlineData(ChangeReason.Correction)]
    public void SuspendRejectsAReasonOfTheOtherDirection(ChangeReason reason) =>
        Assert.Equal("tenancy.tenant.reason_not_allowed",
            Assert.Throws<TenantDomainException>(() => NewTenant().Suspend(reason, Later)).Code);

    [Fact]
    public void SuspendingTwiceIsAlreadyInactive()
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.already_inactive",
            Assert.Throws<TenantDomainException>(() => tenant.Suspend(ChangeReason.Nonpayment, Later)).Code);
    }

    [Theory]
    [InlineData(ChangeReason.Contract)]
    [InlineData(ChangeReason.Courtesy)]
    [InlineData(ChangeReason.Correction)]
    public void ReactivateReturnsToActive(ChangeReason reason)
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        tenant.Reactivate(reason, Later.AddHours(1));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(3, tenant.Version);
    }

    [Fact]
    public void ReactivateRejectsNonpayment()
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.reason_not_allowed",
            Assert.Throws<TenantDomainException>(() => tenant.Reactivate(ChangeReason.Nonpayment, Later)).Code);
    }

    [Fact]
    public void ReactivatingAnActiveTenantIsAlreadyActive() =>
        Assert.Equal("tenancy.tenant.already_active",
            Assert.Throws<TenantDomainException>(() => NewTenant().Reactivate(ChangeReason.Contract, Later)).Code);

    // EnsureActive ya existía (Tenant.cs:211-218): un tenant inactivo no edita sus ajustes.
    [Fact]
    public void ASuspendedTenantCannotUpdateItsSettings()
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.not_active", Assert.Throws<TenantDomainException>(() =>
            tenant.UpdateSettings("Otro", "es-CO", "America/Bogota", "yyyy-MM-dd", Later)).Code);
    }
}
```

`TenantChangeTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantChangeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AModuleChangeFromNoRowKeepsTheTextOfTheColumn()
    {
        var tenantId = TenantId.New();
        var batchId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();

        var change = TenantChange.ForModule(
            tenantId, batchId, TenantModuleKeys.Pos, null, TenantModuleStatus.Active, ChangeReason.Contract, "  ", actor, At);

        Assert.NotEqual(Guid.Empty, change.Id);
        Assert.Equal(tenantId, change.TenantId);
        Assert.Equal(batchId, change.BatchId);
        Assert.Equal(TenantChangeKind.Module, change.Kind);
        Assert.Same(TenantModuleKeys.Pos, change.ModuleKey);
        Assert.Null(change.FromStatus);              // NULL = la fila no existía
        Assert.Equal("active", change.ToStatus);
        Assert.Null(change.Note);                    // sólo espacios se guarda null (Review Focus 5)
        Assert.Equal(actor, change.ActorUserId);
        Assert.Equal(At, change.OccurredAt);
    }

    [Fact]
    public void ATenantStatusChangeUsesTheEnumNameAndNoModule()
    {
        var change = TenantChange.ForTenantStatus(
            TenantId.New(), Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended,
            ChangeReason.Nonpayment, " Factura de septiembre ", Guid.CreateVersion7(), At);

        Assert.Equal(TenantChangeKind.TenantStatus, change.Kind);
        Assert.Null(change.ModuleKey);
        Assert.Equal("Active", change.FromStatus);
        Assert.Equal("Suspended", change.ToStatus);
        Assert.Equal("Factura de septiembre", change.Note);
    }
}
```

`TenantChangeVocabularyTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>El texto de base de cada enum (spec 2026-10-08 §3): minúscula, como los CHECK.</summary>
public sealed class TenantChangeVocabularyTests
{
    [Theory]
    [InlineData(ChangeReason.Contract, "contract")]
    [InlineData(ChangeReason.Courtesy, "courtesy")]
    [InlineData(ChangeReason.Nonpayment, "nonpayment")]
    [InlineData(ChangeReason.Cancellation, "cancellation")]
    [InlineData(ChangeReason.Correction, "correction")]
    public void ReasonsRoundTrip(ChangeReason reason, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(reason));
        Assert.Equal(reason, TenantChangeVocabulary.ParseReason(text));
    }

    [Theory]
    [InlineData(TenantModuleStatus.Active, "active")]
    [InlineData(TenantModuleStatus.Inactive, "inactive")]
    public void ModuleStatusesRoundTrip(TenantModuleStatus status, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(status));
        Assert.Equal(status, TenantChangeVocabulary.ParseModuleStatus(text));
    }

    [Theory]
    [InlineData(TenantChangeKind.Module, "module")]
    [InlineData(TenantChangeKind.TenantStatus, "tenant_status")]
    public void KindsRoundTrip(TenantChangeKind kind, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(kind));
        Assert.Equal(kind, TenantChangeVocabulary.ParseKind(text));
    }

    // Igual que el CHECK: sin ignorar mayúsculas.
    [Theory]
    [InlineData("Contract")]
    [InlineData("refund")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownReasonDoesNotParse(string? text)
    {
        Assert.False(TenantChangeVocabulary.TryParseReason(text, out _));
        if (text is not null)
        {
            Assert.Throws<ArgumentException>(() => TenantChangeVocabulary.ParseReason(text));
        }
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleChangeBatchTests|FullyQualifiedName~TenantStatusTests|FullyQualifiedName~TenantChangeTests|FullyQualifiedName~TenantChangeVocabularyTests"`
Expected: compilación — `CS0246 'TenantModuleStatus'`, `'ChangeReason'`, `'TenantChange'`, `'TenantModuleChangeBatch'`; `CS1061 'Tenant' does not contain a definition for 'Suspend'`.

- [ ] **Step 3: Implement**

`TenantChangeVocabulary.cs`:

```csharp
namespace Modules.Tenancy.Domain;

/// <summary>Spec 2026-10-08 §3 (O3): activo / inactivo. <c>read_only</c> vendrá con el cobro: un valor
/// más acá y un CHECK más ancho. Sin 0 a propósito, para que el centinela de EF nunca sea un estado.</summary>
public enum TenantModuleStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Motivo cerrado de cada cambio (O4). Qué motivo vale en qué dirección es D2.</summary>
public enum ChangeReason
{
    Contract = 1,
    Courtesy = 2,
    Nonpayment = 3,
    Cancellation = 4,
    Correction = 5,
}

/// <summary>Un solo historial para módulos y estado del tenant (D12).</summary>
public enum TenantChangeKind
{
    Module = 1,
    TenantStatus = 2,
}

/// <summary>
/// El texto de base y del JSON de cada enum, en minúscula como los CHECK, y los motivos que valen en
/// cada dirección (D2). La base y la API son los únicos lugares donde estos valores llegan como texto.
/// </summary>
public static class TenantChangeVocabulary
{
    public static readonly IReadOnlySet<ChangeReason> ModuleActivationReasons =
        new HashSet<ChangeReason> { ChangeReason.Contract, ChangeReason.Courtesy, ChangeReason.Correction };

    public static readonly IReadOnlySet<ChangeReason> ModuleDeactivationReasons =
        new HashSet<ChangeReason> { ChangeReason.Nonpayment, ChangeReason.Cancellation, ChangeReason.Correction };

    /// <summary>§4: inactivar un tenant es por falta de pago o porque ya no continúa; no hay "corrección".</summary>
    public static readonly IReadOnlySet<ChangeReason> SuspensionReasons =
        new HashSet<ChangeReason> { ChangeReason.Nonpayment, ChangeReason.Cancellation };

    public static readonly IReadOnlySet<ChangeReason> ReactivationReasons = ModuleActivationReasons;

    public static string ToText(TenantModuleStatus status) => status switch
    {
        TenantModuleStatus.Active => "active",
        TenantModuleStatus.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string ToText(ChangeReason reason) => reason switch
    {
        ChangeReason.Contract => "contract",
        ChangeReason.Courtesy => "courtesy",
        ChangeReason.Nonpayment => "nonpayment",
        ChangeReason.Cancellation => "cancellation",
        ChangeReason.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string ToText(TenantChangeKind kind) => kind switch
    {
        TenantChangeKind.Module => "module",
        TenantChangeKind.TenantStatus => "tenant_status",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParseModuleStatus(string? text, out TenantModuleStatus status) =>
        TryParse(text, Enum.GetValues<TenantModuleStatus>(), ToText, out status);

    public static bool TryParseReason(string? text, out ChangeReason reason) =>
        TryParse(text, Enum.GetValues<ChangeReason>(), ToText, out reason);

    public static TenantModuleStatus ParseModuleStatus(string text) =>
        TryParseModuleStatus(text, out var status) ? status : throw Unknown(text, nameof(TenantModuleStatus));

    public static ChangeReason ParseReason(string text) =>
        TryParseReason(text, out var reason) ? reason : throw Unknown(text, nameof(ChangeReason));

    public static TenantChangeKind ParseKind(string text) =>
        TryParse(text, Enum.GetValues<TenantChangeKind>(), ToText, out var kind) ? kind : throw Unknown(text, nameof(TenantChangeKind));

    private static bool TryParse<T>(string? text, T[] values, Func<T, string> toText, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in values)
        {
            if (string.Equals(toText(candidate), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ArgumentException Unknown(string text, string type) =>
        new($"'{text}' is not a known {type}.", nameof(text));
}
```

`TenantChange.cs`:

```csharp
namespace Modules.Tenancy.Domain;

/// <summary>
/// Una fila del historial único de un tenant (spec 2026-10-08 §3, D12): un cambio de módulo o de estado
/// del tenant, agrupado con los demás de su operación por <see cref="BatchId"/>. Inmutable: no hay
/// camino para editarla ni borrarla. <see cref="FromStatus"/>/<see cref="ToStatus"/> son texto porque
/// guardan dos vocabularios: <c>active</c>/<c>inactive</c> para módulos y el nombre del enum
/// (<c>Active</c>/<c>Suspended</c>) para el tenant, como <c>tenants.status</c>.
/// </summary>
public sealed class TenantChange
{
    public const int NoteMaxLength = 300;
    public const int StatusMaxLength = 20;

    private TenantChange()
    {
        ToStatus = string.Empty;
    }

    private TenantChange(
        TenantId tenantId, Guid batchId, TenantChangeKind kind, TenantModuleKey? moduleKey,
        string? fromStatus, string toStatus, ChangeReason reason, string? note, Guid actorUserId,
        DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        BatchId = batchId;
        Kind = kind;
        ModuleKey = moduleKey;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ActorUserId = actorUserId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid BatchId { get; private set; }
    public TenantChangeKind Kind { get; private set; }
    public TenantModuleKey? ModuleKey { get; private set; }
    public string? FromStatus { get; private set; }
    public string ToStatus { get; private set; }
    public ChangeReason Reason { get; private set; }
    public string? Note { get; private set; }
    public Guid ActorUserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <param name="fromStatus">null = la fila del módulo no existía.</param>
    public static TenantChange ForModule(
        TenantId tenantId, Guid batchId, TenantModuleKey moduleKey, TenantModuleStatus? fromStatus,
        TenantModuleStatus toStatus, ChangeReason reason, string? note, Guid actorUserId, DateTimeOffset occurredAt) =>
        new(tenantId, batchId, TenantChangeKind.Module, moduleKey,
            fromStatus is { } from ? TenantChangeVocabulary.ToText(from) : null,
            TenantChangeVocabulary.ToText(toStatus), reason, note, actorUserId, occurredAt);

    public static TenantChange ForTenantStatus(
        TenantId tenantId, Guid batchId, TenantStatus fromStatus, TenantStatus toStatus, ChangeReason reason,
        string? note, Guid actorUserId, DateTimeOffset occurredAt) =>
        new(tenantId, batchId, TenantChangeKind.TenantStatus, null, fromStatus.ToString(), toStatus.ToString(),
            reason, note, actorUserId, occurredAt);
}
```

`TenantModuleChangeBatch.cs`:

```csharp
namespace Modules.Tenancy.Domain;

public sealed record RequestedModuleChange(TenantModuleKey Key, TenantModuleStatus To);

/// <param name="From">null = la fila no existe; activar la crea con origen <c>operator</c>.</param>
public sealed record PlannedModuleChange(TenantModuleKey Key, TenantModuleStatus? From, TenantModuleStatus To);

/// <summary>
/// Spec 2026-10-08 §3: el único camino de dominio para cambiar el estado de los módulos de un tenant
/// (O4). Puro: recibe el estado guardado (inactivas incluidas), el lote y el motivo, y devuelve el plan
/// en el orden de <see cref="TenantModuleKeys.All"/> o falla con uno de cinco códigos. Estricto (O5):
/// la cascada la calcula la consola y llega como un solo lote.
/// </summary>
public static class TenantModuleChangeBatch
{
    public const string NoChangesCode = "tenancy.modules.no_changes";
    public const string DuplicateKeyCode = "tenancy.modules.duplicate_key";
    public const string MixedDirectionsCode = "tenancy.modules.mixed_directions";
    public const string ReasonNotAllowedCode = "tenancy.modules.reason_not_allowed";
    public const string InconsistentDependenciesCode = "tenancy.modules.inconsistent_dependencies";

    public static IReadOnlyList<PlannedModuleChange> Plan(
        IReadOnlyDictionary<TenantModuleKey, TenantModuleStatus> stored,
        IReadOnlyCollection<RequestedModuleChange> requested,
        ChangeReason reason)
    {
        if (requested.Count == 0)
        {
            throw new TenantDomainException(NoChangesCode, "The batch has no changes.");
        }

        if (requested.Select(change => change.Key).Distinct().Count() != requested.Count)
        {
            throw new TenantDomainException(DuplicateKeyCode, "The batch names the same module twice.");
        }

        // D4: la cascada siempre va en una sola dirección; mezclar complicaría el motivo.
        var directions = requested.Select(change => change.To).Distinct().ToArray();
        if (directions.Length > 1)
        {
            throw new TenantDomainException(MixedDirectionsCode, "A batch either activates or deactivates.");
        }

        var to = directions[0];
        var allowed = to == TenantModuleStatus.Active
            ? TenantChangeVocabulary.ModuleActivationReasons
            : TenantChangeVocabulary.ModuleDeactivationReasons;
        if (!allowed.Contains(reason))
        {
            throw new TenantDomainException(
                ReasonNotAllowedCode,
                $"'{TenantChangeVocabulary.ToText(reason)}' is not a reason to {(to == TenantModuleStatus.Active ? "activate" : "deactivate")} a module.");
        }

        var planned = new List<PlannedModuleChange>(requested.Count);
        foreach (var change in requested)
        {
            TenantModuleStatus? from = stored.TryGetValue(change.Key, out var current) ? current : null;
            // Sin cambios vacíos: activar lo activo, desactivar lo inactivo o lo que no tiene fila.
            if ((to == TenantModuleStatus.Active && from == TenantModuleStatus.Active) ||
                (to == TenantModuleStatus.Inactive && from != TenantModuleStatus.Active))
            {
                throw new TenantDomainException(
                    NoChangesCode, $"The '{change.Key.Value}' module is already {TenantChangeVocabulary.ToText(to)} or has no row.");
            }

            planned.Add(new PlannedModuleChange(change.Key, from, to));
        }

        EnsureConsistent(stored, planned);
        return planned.OrderBy(change => IndexOf(change.Key)).ToArray();
    }

    /// <summary>
    /// Regla exacta (§3): con S' el estado después del lote y L sus claves, se rechaza si hay (m, d) con
    /// m activo en S', d dependencia directa de m, d no activo en S', y m ∈ L o d ∈ L. Basta con las
    /// directas: si el lote deja consistente cada par que toca, los transitivos quedan cubiertos. Un par
    /// roto heredado que el lote no toca no bloquea (D9).
    /// </summary>
    private static void EnsureConsistent(
        IReadOnlyDictionary<TenantModuleKey, TenantModuleStatus> stored,
        IReadOnlyList<PlannedModuleChange> planned)
    {
        var after = stored.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var change in planned)
        {
            after[change.Key] = change.To;
        }

        var batch = planned.Select(change => change.Key).ToHashSet();
        bool IsActive(TenantModuleKey key) => after.TryGetValue(key, out var status) && status == TenantModuleStatus.Active;

        var broken = TenantModuleKeys.All
            .Where(IsActive)
            .SelectMany(module => TenantModuleKeys.DependenciesOf(module)
                .Where(dependency => !IsActive(dependency) && (batch.Contains(module) || batch.Contains(dependency)))
                .Select(dependency => $"{module.Value} -> {dependency.Value}"))
            .ToArray();
        if (broken.Length > 0)
        {
            // Sólo para logs: la SPA nunca muestra el detail.
            throw new TenantDomainException(
                InconsistentDependenciesCode,
                $"The batch leaves active modules without their dependencies: {string.Join(", ", broken)}.");
        }
    }

    private static int IndexOf(TenantModuleKey key)
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            if (TenantModuleKeys.All[index] == key)
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
```

`Tenant.cs`, después de `RemoveLogo`:

```csharp
    /// <summary>
    /// Spec 2026-10-08 §4 (O8): inactivo bloquea todo acceso, no borra nada y conserva los módulos (D8).
    /// Sube <see cref="Version"/> (el If-Match del endpoint) y <b>no emite eventos</b>: OutboxWriter
    /// lanza ante un evento sin mapear y nadie fuera de Tenancy consume este cambio; alcanzan el
    /// historial y la auditoría. Los demás valores de <see cref="TenantStatus"/> no se asignan hoy:
    /// desde ellos es <c>tenancy.tenant.not_active</c> (decisión P5 del plan).
    /// </summary>
    public void Suspend(ChangeReason reason, DateTimeOffset occurredAt)
    {
        if (Status == TenantStatus.Suspended)
        {
            throw new TenantDomainException("tenancy.tenant.already_inactive", "The tenant is already inactive.");
        }

        EnsureActive();
        EnsureReason(TenantChangeVocabulary.SuspensionReasons, reason, "suspend");
        Status = TenantStatus.Suspended;
        Version++;
        UpdatedAt = occurredAt;
    }

    public void Reactivate(ChangeReason reason, DateTimeOffset occurredAt)
    {
        if (Status == TenantStatus.Active)
        {
            throw new TenantDomainException("tenancy.tenant.already_active", "The tenant is already active.");
        }

        if (Status != TenantStatus.Suspended)
        {
            throw new TenantDomainException("tenancy.tenant.not_active", "Only a suspended tenant can be reactivated.");
        }

        EnsureReason(TenantChangeVocabulary.ReactivationReasons, reason, "reactivate");
        Status = TenantStatus.Active;
        Version++;
        UpdatedAt = occurredAt;
    }

    private static void EnsureReason(IReadOnlySet<ChangeReason> allowed, ChangeReason reason, string verb)
    {
        if (!allowed.Contains(reason))
        {
            throw new TenantDomainException(
                "tenancy.tenant.reason_not_allowed",
                $"'{TenantChangeVocabulary.ToText(reason)}' is not a reason to {verb} a tenant.");
        }
    }
```

> `EnsureActive()` (`:211-218`) lanza `tenancy.tenant.not_active` con el mensaje "Only an active tenant can update settings." — el texto no calza para `Suspend`, pero es sólo log y el camino es inalcanzable hoy. Si molesta, pasar el mensaje como parámetro opcional; no crear otro código.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleChangeBatchTests|FullyQualifiedName~TenantStatusTests|FullyQualifiedName~TenantChangeTests|FullyQualifiedName~TenantChangeVocabularyTests|FullyQualifiedName~TenantTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChangeVocabulary.cs src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChange.cs src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleChangeBatch.cs src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantChangeVocabularyTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantChangeTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleChangeBatchTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantStatusTests.cs
git commit -m "feat(tenancy): lote de cambios de módulos, historial y estado del tenant en el dominio"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 5: Estado del módulo en la tabla — `Activate/Deactivate`, mapeo y migración `AddOperatorConsole`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs` (todo el archivo, 50 líneas)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:15,26-38,93-130`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs:13-31`
- Create (generada): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddOperatorConsole.cs` y `.Designer.cs`
- Modify (regenerado): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs`

**Interfaces:**
- Consumes: `TenantModuleStatus`, `TenantChange`, `TenantChangeVocabulary`, `TenantModuleChangeBatch.NoChangesCode` (Task 4).
- Produces: `TenantModule.Status`, `TenantModule.StatusChangedAt`, `TenantModule.Activate(DateTimeOffset)`, `TenantModule.Deactivate(DateTimeOffset)`, `TenantModule.StatusMaxLength = 16`, `TenantModuleSources.Operator = "operator"`; `TenancyDbContext.TenantChanges` (`public DbSet<TenantChange>`); `ITenantModules.FindAsync` cuenta sólo filas activas.

- [ ] **Step 1: Write the failing tests**

`TenantModuleTests.cs`, casos nuevos:

```csharp
    [Fact]
    public void CreateStartsActiveSinceItsEnabledAt()
    {
        var enabledAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Pos, TenantModuleSources.Operator, enabledAt, null);

        Assert.Equal(TenantModuleStatus.Active, module.Status);
        Assert.Equal(enabledAt, module.StatusChangedAt);
        Assert.Equal("operator", module.Source);
    }

    [Fact]
    public void DeactivateAndActivateMoveTheStatusAndItsDateButNotEnabledAt()
    {
        var enabledAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Reporting, TenantModuleSources.Signup, enabledAt, null);

        module.Deactivate(enabledAt.AddDays(1));
        Assert.Equal(TenantModuleStatus.Inactive, module.Status);
        Assert.Equal(enabledAt.AddDays(1), module.StatusChangedAt);

        module.Activate(enabledAt.AddDays(2));
        Assert.Equal(TenantModuleStatus.Active, module.Status);
        Assert.Equal(enabledAt.AddDays(2), module.StatusChangedAt);
        Assert.Equal(enabledAt, module.EnabledAt);
    }

    [Fact]
    public void AChangeToTheSameStatusIsNoChanges()
    {
        var module = TenantModule.Create(TenantId.New(), TenantModuleKeys.Reporting, TenantModuleSources.Signup, DateTimeOffset.UnixEpoch, null);

        Assert.Equal("tenancy.modules.no_changes",
            Assert.Throws<TenantDomainException>(() => module.Activate(DateTimeOffset.UnixEpoch)).Code);
        module.Deactivate(DateTimeOffset.UnixEpoch);
        Assert.Equal("tenancy.modules.no_changes",
            Assert.Throws<TenantDomainException>(() => module.Deactivate(DateTimeOffset.UnixEpoch)).Code);
    }
```

`TenantModulesMigrationTests.cs` — constante y cinco casos nuevos (reusan `NewContext`, `ExecuteAsync`, `LegacyTenantSql`, `TenancyServices`):

```csharp
    private const string AddTenantModules = "20261007081219_AddTenantModules";

    private static async Task<string> MigratedWithLegacyTenantAsync(PostgreSqlContainer database, string target)
    {
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyTenantSql);
        await migrator.MigrateAsync(target, TestContext.Current.CancellationToken);
        return connectionString;
    }

    // Spec 2026-10-08 §7: las filas existentes quedan activas desde su enabled_at.
    [Fact]
    public async Task ExistingRowsStayActiveSinceTheirEnabledAt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = await MigratedWithLegacyTenantAsync(database, AddTenantModules);
        await ExecuteAsync(connectionString, "UPDATE tenancy.tenant_modules SET enabled_at = '2026-08-12T13:30:00Z';");
        await using var context = NewContext(connectionString);

        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6L, await ScalarAsync<long>(connectionString,
            $"SELECT count(*) FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND status = 'active' AND status_changed_at = '2026-08-12T13:30:00Z'"));
    }

    // §3: un pod viejo, el signup y el SQL manual insertan sin las columnas nuevas.
    [Fact]
    public async Task AnInsertWithoutStatusGetsTheDefaults()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = await MigratedWithLegacyTenantAsync(database, AddTenantModules);
        await using (var context = NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(connectionString,
            $"INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source) VALUES ('{LegacyTenantId}', 'pos', now(), 'manual');");

        Assert.Equal("active", await ScalarAsync<string>(connectionString,
            $"SELECT status FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'pos'"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            $"SELECT count(*) FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'pos' AND status_changed_at IS NOT NULL"));
    }

    [Theory]
    [InlineData("UPDATE tenancy.tenant_modules SET status = 'read_only';")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, module_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '01900000-0000-7000-8000-00000000e001', gen_random_uuid(), 'tenant_status', 'pos', 'Suspended', 'nonpayment', gen_random_uuid(), now());")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, module_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '01900000-0000-7000-8000-00000000e001', gen_random_uuid(), 'module', NULL, 'active', 'contract', gen_random_uuid(), now());")]
    [InlineData("INSERT INTO tenancy.tenant_changes (id, tenant_id, batch_id, kind, module_key, to_status, reason, actor_user_id, occurred_at) VALUES (gen_random_uuid(), '01900000-0000-7000-8000-00000000e001', gen_random_uuid(), 'module', 'pos', 'active', 'refund', gen_random_uuid(), now());")]
    public async Task TheChecksRejectWhatTheSpecForbids(string sql)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = await MigratedWithLegacyTenantAsync(database, AddTenantModules);
        await using (var context = NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    // §3: contratado = fila activa. TenantModuleSet.FromStored y lo de encima no cambian.
    [Fact]
    public async Task FindAsyncIgnoresInactiveRows()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = await MigratedWithLegacyTenantAsync(database, AddTenantModules);
        await using (var context = NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(connectionString,
            $"UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'customers';");

        await using var provider = TenancyServices(connectionString);
        await using var scope = provider.CreateAsyncScope();
        var set = await scope.ServiceProvider.GetRequiredService<ITenantModules>()
            .FindAsync(Guid.Parse(LegacyTenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(set);
        Assert.False(set.IsContracted(TenantModuleKeys.Customers));
        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Orders));
    }

    // §7: Down borra las inactivas (antes, inactivo = sin fila) y pasa operator a manual.
    [Fact]
    public async Task DownDropsInactiveRowsAndTurnsOperatorIntoManual()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = await MigratedWithLegacyTenantAsync(database, AddTenantModules);
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, $"""
            UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'reporting';
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source) VALUES ('{LegacyTenantId}', 'pos', now(), 'operator');
            """);

        await migrator.MigrateAsync(AddTenantModules, TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            $"SELECT count(*) FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'reporting'"));
        Assert.Equal("manual", await ScalarAsync<string>(connectionString,
            $"SELECT source FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'pos'"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'tenancy' AND table_name = 'tenant_changes'"));
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
```

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleTests"
```
Expected: compilación — `CS1061 'TenantModule' does not contain a definition for 'Status'`, `CS0117 'TenantModuleSources' does not contain a definition for 'Operator'`.

- [ ] **Step 3: Dominio de `TenantModule`**

Reemplazar el `<summary>` de la clase (ya no es cierto que «apagar es borrarla») y agregar:

```csharp
/// <summary>
/// Un módulo de un tenant y su estado (spec 2026-10-08 §3): la fila es el entitlement y su
/// <see cref="Status"/> dice si está contratado. Desactivar ya no es borrar: la fila queda con su estado
/// y el historial (<see cref="TenantChange"/>) guarda quién, cuándo y por qué. <see cref="EnabledAt"/> es
/// cuándo se creó la fila por primera vez; <see cref="StatusChangedAt"/>, el último cambio de estado.
/// <see cref="Source"/> no se valida acá: el CHECK de la tabla es la única validación.
/// </summary>
public sealed class TenantModule
{
    public const int SourceMaxLength = 16;
    public const int NoteMaxLength = 300;
    public const int StatusMaxLength = 16;
    // …constructores: el privado con parámetros fija también
    //     Status = TenantModuleStatus.Active; StatusChangedAt = enabledAt;
    public TenantModuleStatus Status { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    public void Activate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Active, occurredAt);

    public void Deactivate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Inactive, occurredAt);

    // Sin cambios vacíos (§3): el lote ya lo filtra, pero el agregado no confía en quien lo llama.
    private void ChangeTo(TenantModuleStatus next, DateTimeOffset occurredAt)
    {
        if (Status == next)
        {
            throw new TenantDomainException(
                TenantModuleChangeBatch.NoChangesCode,
                $"The '{ModuleKey.Value}' module is already {TenantChangeVocabulary.ToText(next)}.");
        }

        Status = next;
        StatusChangedAt = occurredAt;
    }
}

/// <summary>Los orígenes que escribe el código. <c>backfill</c> y <c>manual</c> los escribe SQL.</summary>
public static class TenantModuleSources
{
    public const string Signup = "signup";
    public const string Seed = "seed";

    /// <summary>Spec 2026-10-08 §3: la fila la creó la consola (el módulo nunca había existido).</summary>
    public const string Operator = "operator";
}
```

Run `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleTests"` → PASS.

- [ ] **Step 4: Mapeo de EF**

`TenancyDbContext.cs`: `public DbSet<TenantChange> TenantChanges => Set<TenantChange>();` junto a `TenantModules` (`:15`); `ConfigureTenantChange(modelBuilder);` después de `ConfigureTenantModule` (`:29`). En `ConfigureTenantModule` (`:98-130`), el `CHECK` de `source` y las dos columnas:

```csharp
        module.ToTable("tenant_modules", "tenancy", table =>
        {
            table.HasCheckConstraint(
                "CK_tenant_modules_module_key",
                "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos')");
            table.HasCheckConstraint(
                "CK_tenant_modules_source",
                "source IN ('backfill','signup','seed','manual','operator')");
            table.HasCheckConstraint("CK_tenant_modules_status", "status IN ('active','inactive')");
        });
        // …
        // Spec 2026-10-08 §3: el DEFAULT se queda y se declara en el modelo. Durante el rolling update los
        // pods viejos insertan filas (signup, semilla) sin la columna, y el SQL manual del README también.
        // HasDefaultValueSql y no HasDefaultValue("active"): Status es un enum con conversión a texto
        // (decisión P3 del plan). Active vale 1, no 0, así que EF siempre manda el valor del agregado.
        module.Property(value => value.Status)
            .HasColumnName("status")
            .HasMaxLength(TenantModule.StatusMaxLength)
            .HasConversion(
                status => TenantChangeVocabulary.ToText(status),
                text => TenantChangeVocabulary.ParseModuleStatus(text))
            .HasDefaultValueSql("'active'");
        module.Property(value => value.StatusChangedAt)
            .HasColumnName("status_changed_at")
            .HasDefaultValueSql("now()");
```

y el método nuevo:

```csharp
    // Spec 2026-10-08 §3 (D12): un solo historial para módulos y estado del tenant. La FK va en el
    // modelo por la misma razón que tenant_modules: que EF ordene los INSERT del mismo SaveChanges. Sin
    // navegación en Tenant.
    private static void ConfigureTenantChange(ModelBuilder modelBuilder)
    {
        var change = modelBuilder.Entity<TenantChange>();
        change.ToTable("tenant_changes", "tenancy", table =>
        {
            table.HasCheckConstraint("CK_tenant_changes_kind", "kind IN ('module','tenant_status')");
            table.HasCheckConstraint(
                "CK_tenant_changes_module_key",
                "(kind = 'module' AND module_key IS NOT NULL) OR (kind = 'tenant_status' AND module_key IS NULL)");
            table.HasCheckConstraint(
                "CK_tenant_changes_reason",
                "reason IN ('contract','courtesy','nonpayment','cancellation','correction')");
        });
        change.HasKey(value => value.Id);
        change.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        change.Property(value => value.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));
        change.Property(value => value.BatchId).HasColumnName("batch_id");
        change.Property(value => value.Kind)
            .HasColumnName("kind")
            .HasMaxLength(16)
            .HasConversion(kind => TenantChangeVocabulary.ToText(kind), text => TenantChangeVocabulary.ParseKind(text));
        // EF no le pasa null a un conversor: las filas de estado del tenant quedan con NULL.
        change.Property(value => value.ModuleKey)
            .HasColumnName("module_key")
            .HasMaxLength(32)
            .HasConversion(key => key!.Value, value => TenantModuleKey.Parse(value));
        change.Property(value => value.FromStatus).HasColumnName("from_status").HasMaxLength(TenantChange.StatusMaxLength);
        change.Property(value => value.ToStatus).HasColumnName("to_status").HasMaxLength(TenantChange.StatusMaxLength);
        change.Property(value => value.Reason)
            .HasColumnName("reason")
            .HasMaxLength(16)
            .HasConversion(reason => TenantChangeVocabulary.ToText(reason), text => TenantChangeVocabulary.ParseReason(text));
        change.Property(value => value.Note).HasColumnName("note").HasMaxLength(TenantChange.NoteMaxLength);
        change.Property(value => value.ActorUserId).HasColumnName("actor_user_id");
        change.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        change.HasIndex(value => new { value.TenantId, value.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_tenant_changes_tenant_id_occurred_at");
        change.HasOne<Tenant>().WithMany()
            .HasForeignKey(value => value.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
```

`TenantModules.cs:23-26` — sólo las activas:

```csharp
                // Spec 2026-10-08 §3: contratado = fila con status = 'active'. FromStored y todo lo de
                // encima (efectivos, missingDependencies, enmascarado, guard, /modules) no cambia.
                Keys = dbContext.TenantModules
                    .Where(module => module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active)
                    .Select(module => module.ModuleKey)
                    .ToList(),
```

- [ ] **Step 5: Generar la migración con el factory de diseño**

Detener la API de este worktree (bloque de «Antes de empezar») y, desde `$B`:

```
dotnet ef migrations add AddOperatorConsole --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

- [ ] **Step 6: Revisar y editar la migración generada** (el snapshot no se toca a mano)

Revisar el `Up` generado:
1. `status`: `AddColumn<string>(…, type: "character varying(16)", maxLength: 16, nullable: false, defaultValueSql: "'active'")`. Si apareciera `defaultValue: ""` en vez de `defaultValueSql`, el mapeo está mal: corregir el modelo y regenerar, no editar a mano.
2. `status_changed_at`: `AddColumn<DateTimeOffset>(…, type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")`.

Reordenar el `Up` para que quede así (§7: columnas con default → `UPDATE` → `CHECK`s → tabla nueva):

```csharp
            migrationBuilder.AddColumn<string>(/* status, generado */);
            migrationBuilder.AddColumn<DateTimeOffset>(/* status_changed_at, generado */);

            // Spec 2026-10-08 §7: las filas existentes quedan activas (el DEFAULT ya las llenó) desde que
            // se crearon. Antes de los CHECK, en la misma transacción.
            migrationBuilder.Sql("UPDATE tenancy.tenant_modules SET status_changed_at = enabled_at;");

            migrationBuilder.DropCheckConstraint(/* CK_tenant_modules_source, generado */);
            migrationBuilder.AddCheckConstraint(/* CK_tenant_modules_source con 'operator', generado */);
            migrationBuilder.AddCheckConstraint(/* CK_tenant_modules_status, generado */);

            migrationBuilder.CreateTable(/* tenant_changes, generado */);
            migrationBuilder.CreateIndex(/* IX_tenant_changes_tenant_id_occurred_at, descending: new[] { false, true } */);
```

El `Down` generado (drop tabla, drop checks, drop columnas, re-add del `CHECK` viejo de `source`) lleva al **principio**:

```csharp
            // Spec 2026-10-08 §7: en el modelo anterior, inactivo = sin fila; y el CHECK viejo de source
            // no acepta 'operator'. OJO: este Down le devuelve el acceso a los tenants Suspended, porque
            // el código anterior ignora el estado del tenant.
            migrationBuilder.Sql("DELETE FROM tenancy.tenant_modules WHERE status = 'inactive';");
            migrationBuilder.Sql("UPDATE tenancy.tenant_modules SET source = 'manual' WHERE source = 'operator';");
```

Verificar que el índice generado se llame `IX_tenant_changes_tenant_id_occurred_at`, la FK `FK_tenant_changes_tenants_tenant_id`, y que `TenancyDbContextModelSnapshot.cs` tenga `CK_tenant_modules_status`, `HasDefaultValueSql("'active'")`, `HasDefaultValueSql("now()")` y la entidad `TenantChange`.

- [ ] **Step 7: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleTests|FullyQualifiedName~TenantRegistrationServiceTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesMigrationTests|FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~TenancyLayerTests"
```
Expected: PASS. `TenantModulesApiTests` y `SeedStartupTests` prueban que el signup y la semilla siguen escribiendo sus filas (ahora con `status`), y que EF no se queja de cambios pendientes del modelo (`PendingModelChangesWarning` al migrar).

- [ ] **Step 8: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/*_AddOperatorConsole.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/*_AddOperatorConsole.Designer.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs
git commit -m "feat(tenancy): estado de los módulos por tenant y tabla tenant_changes"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 6: Escritura — repositorios, candado por tenant y `PK_tenant_modules`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs:16-20` (`ITenantModuleRepository`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantChangeRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeLock.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenancyUnitOfWork.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs:30-97`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs:28-31`
- Modify (dobles que implementan las interfaces): `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs:351-364`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantLogoHandlerTestDoubles.cs:25-45`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs:74-83`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantChangePersistenceTests.cs`

**Interfaces:**
- Consumes: `TenantModule`, `TenantChange`, `TenantModuleChangeBatch.NoChangesCode` (Tasks 4–5).
- Produces:
  - `ITenantModuleRepository.ListByTenantAsync(TenantId tenantId, CancellationToken) → Task<IReadOnlyList<TenantModule>>` (con tracking, inactivas incluidas).
  - `ITenantChangeRepository { void Add(TenantChange change); }` (se commitea con `ITenancyUnitOfWork`).
  - `ITenancyUnitOfWork.BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken) → Task<ITenantChangeScope>`; `ITenantChangeScope : IAsyncDisposable { Task CommitAsync(CancellationToken); }`.
  - `TenantChangeLock.KeyFor(Guid tenantId) → string` (público, en `Modules.Tenancy.Infrastructure.Persistence`).
  - `TenancyUnitOfWork.SaveChangesAsync` traduce `23505` sobre `PK_tenant_modules` a `TenantDomainException("tenancy.modules.no_changes")`.

- [ ] **Step 1: Write the failing tests**

`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantChangePersistenceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>Spec 2026-10-08 §3: el lado de escritura de la consola contra Postgres de verdad.</summary>
public sealed class TenantChangePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListByTenantReturnsInactiveRowsAndTracksThem()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
                .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken);
            rows.Single(row => row.ModuleKey == TenantModuleKeys.Reporting).Deactivate(Now);
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = provider.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
            .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken);
        Assert.Equal(6, stored.Count);
        Assert.Equal(TenantModuleStatus.Inactive, stored.Single(row => row.ModuleKey == TenantModuleKeys.Reporting).Status);
    }

    [Fact]
    public async Task BothKindsOfChangeRoundTripThroughTheTable()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        var batchId = Guid.CreateVersion7();

        await using (var scope = provider.CreateAsyncScope())
        {
            var changes = scope.ServiceProvider.GetRequiredService<ITenantChangeRepository>();
            changes.Add(TenantChange.ForModule(tenantId, batchId, TenantModuleKeys.Pos, null, TenantModuleStatus.Active,
                ChangeReason.Courtesy, "prueba", Guid.CreateVersion7(), Now));
            changes.Add(TenantChange.ForTenantStatus(tenantId, Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended,
                ChangeReason.Nonpayment, null, Guid.CreateVersion7(), Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = provider.CreateAsyncScope();
        var rows = await read.ServiceProvider.GetRequiredService<TenancyDbContext>().TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var module = rows.Single(row => row.Kind == TenantChangeKind.Module);
        Assert.Same(TenantModuleKeys.Pos, module.ModuleKey);
        Assert.Null(module.FromStatus);
        Assert.Equal("active", module.ToStatus);
        Assert.Equal(ChangeReason.Courtesy, module.Reason);
        var status = rows.Single(row => row.Kind == TenantChangeKind.TenantStatus);
        Assert.Null(status.ModuleKey);
        Assert.Equal("Suspended", status.ToStatus);
    }

    // §3: el candado serializa dos operaciones sobre el mismo tenant (Review Focus 3).
    [Fact]
    public async Task TheLockSerializesTwoScopesOnTheSameTenant()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var holder = await first.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(tenantId, TestContext.Current.CancellationToken);
        var waiting = second.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(tenantId, TestContext.Current.CancellationToken);

        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)));
        await holder.CommitAsync(TestContext.Current.CancellationToken);
        await holder.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheLockDoesNotBlockAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var one = await SeedTenantAsync(provider);
        var other = await SeedTenantAsync(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        await using var holder = await first.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(one, TestContext.Current.CancellationToken);
        await using var free = await second.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(other, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // §3: dos activaciones simultáneas de una clave sin fila; sin la traducción sería un 500.
    [Fact]
    public async Task ADuplicateModuleRowIsNoChangesNotA500()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
                .Add(TenantModule.Create(tenantId, TenantModuleKeys.Pos, TenantModuleSources.Operator, Now, null));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var late = provider.CreateAsyncScope();
        late.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
            .Add(TenantModule.Create(tenantId, TenantModuleKeys.Pos, TenantModuleSources.Operator, Now, null));
        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            late.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.modules.no_changes", error.Code);
    }

    private static async Task<TenantId> SeedTenantAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = Tenant.Create(TenantId.New(), $"t-{Guid.NewGuid():N}"[..12], "Persistence Test", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now);
        dbContext.Tenants.Add(tenant);
        foreach (var key in TenantModuleKeys.DefaultForNewTenants)
        {
            dbContext.TenantModules.Add(TenantModule.Create(tenant.Id, key, TenantModuleSources.Signup, Now, null));
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenant.Id;
    }

    private static async Task<ServiceProvider> MigratedServicesAsync(PostgreSqlContainer database)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = database.GetConnectionString() })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantChangePersistenceTests"`
Expected: compilación — `CS1061 'ITenantModuleRepository' does not contain a definition for 'ListByTenantAsync'`, `CS0246 'ITenantChangeRepository'`, `CS1061 … 'BeginTenantChangeScopeAsync'`.

- [ ] **Step 3: Implement**

`ITenantModules.cs` — `ITenantModuleRepository`:

```csharp
/// <summary>Se commitea con <see cref="ITenancyUnitOfWork"/>.</summary>
public interface ITenantModuleRepository
{
    void Add(TenantModule tenantModule);

    /// <summary>Todas las filas del tenant, inactivas incluidas y con tracking: es el estado guardado
    /// que el lote de la consola valida y modifica (spec 2026-10-08 §3). Se lee con el candado tomado.</summary>
    Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken);
}
```

`TenantModuleRepository.cs`:

```csharp
    public async Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantModules
            .Where(module => module.TenantId == tenantId)
            .ToListAsync(cancellationToken);
```

`ITenantChangeRepository.cs` (Application) y `TenantChangeRepository.cs` (Infrastructure, `internal sealed`, `public void Add(TenantChange change) => dbContext.TenantChanges.Add(change);`).

`TenantChangeLock.cs`:

```csharp
namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-08 §3: la clave del <c>pg_advisory_xact_lock</c> sobre el tenant destino. Con prefijo
/// para no compartir espacio con <c>UserLifecycleLockKey</c> (mismo motor de locks, misma base). Pública
/// para que las pruebas de integración tomen el mismo candado.
/// </summary>
public static class TenantChangeLock
{
    public static string KeyFor(Guid tenantId) => $"tenancy.tenant-changes:{tenantId:D}";
}
```

`ITenancyUnitOfWork.cs`:

```csharp
    /// <summary>
    /// Spec 2026-10-08 §3: abre la transacción de un cambio de la consola y toma
    /// <c>pg_advisory_xact_lock</c> sobre el tenant destino <b>antes de leer</b> su estado. Sin él, en
    /// READ COMMITTED, «A activa quotations» y «B desactiva customers» pasarían los dos la validación
    /// (write skew). Mismo patrón que <see cref="BeginUserLifecycleScopeAsync"/>.
    /// </summary>
    Task<ITenantChangeScope> BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>Se libera al commitear o, si el handler falla, al disponerlo (rollback).</summary>
public interface ITenantChangeScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
```

(agregar `using Modules.Tenancy.Domain;`).

`TenancyUnitOfWork.cs` — la clase privada de `:44-51` pasa a implementar las dos interfaces:

```csharp
    /// <summary>PK de tenant_modules (AddTenantModules). Se reconoce por nombre, no sólo por el 23505.</summary>
    private const string TenantModulesPrimaryKey = "PK_tenant_modules";

    public async Task<ITenantChangeScope> BeginTenantChangeScopeAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({TenantChangeLock.KeyFor(tenantId.Value)}))",
            cancellationToken);
        return new TransactionScope(transaction);
    }

    // Renombrada desde UserLifecycleScope: las dos operaciones son una transacción con un lock adentro.
    private sealed class TransactionScope(IDbContextTransaction transaction) : IUserLifecycleScope, ITenantChangeScope
    { /* mismo cuerpo de hoy */ }
```

(`BeginUserLifecycleScopeAsync` devuelve `new TransactionScope(transaction)`). En `SaveChangesAsync`, un `catch` más, junto a los de `:72-96`:

```csharp
        // Spec 2026-10-08 §3: dos activaciones simultáneas de una clave sin fila. El candado de la consola
        // lo evita; esto cubre a cualquier otro camino que inserte la misma clave. Sin la traducción, 500.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: TenantModulesPrimaryKey,
            })
        {
            throw new TenantDomainException(
                TenantModuleChangeBatch.NoChangesCode,
                "The module row already exists.");
        }
```

`TenancyInfrastructureExtensions.cs`: `services.AddScoped<ITenantChangeRepository, TenantChangeRepository>();` junto a `ITenantModuleRepository` (`:30`).

Dobles que implementan las interfaces ampliadas (si no, Tenancy.UnitTests no compila):

```csharp
// InvitationServiceTests.Uow y TenantLogoHandlerTestDoubles.RecordingTenancyUnitOfWork:
    public Task<ITenantChangeScope> BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

// TenantRegistrationServiceTests.RecordingTenantModuleRepository:
    public Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
```

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantChangePersistenceTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~InvitationServiceTests|FullyQualifiedName~TenantLogoHandlerTests|FullyQualifiedName~TenantRegistrationServiceTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenantChangeRepository.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenancyUnitOfWork.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeRepository.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeLock.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantLogoHandlerTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantChangePersistenceTests.cs
git commit -m "feat(tenancy): candado por tenant, historial y estado guardado de módulos en persistencia"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 7: El estado del tenant se cumple — sesión, permisos y stub

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs:8-22`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDirectory.cs:5-20`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs:36-50`
- Modify: `src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs:13-49`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationTenantLogoLookupTests.cs:190-203` (doble)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs:55-57` (`FindByUserAndTenantAsync` busca de verdad)
- Create: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipDirectoryTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs` (caso nuevo)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs` (caso nuevo)

**Interfaces:**
- Consumes: `Tenant.Suspend/Reactivate`, `ChangeReason` (Task 4), `IOperatorTenant`, `OperatorPermissionFilter` (Tasks 1–2).
- Produces: `ITenantDirectory.GetStatusAsync(TenantId tenantId, CancellationToken) → Task<TenantStatus?>` (`null` = sin fila; decisión P2); `MembershipDirectory(IMembershipRepository, ITenantDirectory)`; `FixedTenantDirectory(TenantStatus? status)` en `OperatorTestDoubles.cs` de Tenancy.UnitTests; `OperatorConsoleApiTests.SetTenantStatusAsync(QepApiFactory, Guid, bool suspend)` (internal static).

> `MembershipRepository.ListActiveTenantsByUserAsync` (`:26-34`) **no** se filtra: §4 explica que su resultado se descarta en `/auth/session` (`AuthSessionEndpoints.cs:98-105`).

- [ ] **Step 1: Write the failing tests**

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs` (crece en las Tasks 8–11):

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

internal sealed class FixedTenantDirectory(TenantStatus? status) : ITenantDirectory
{
    public List<TenantId> Asked { get; } = [];

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(status);
    }

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
```

En `MembershipHandlerTestDoubles.cs:55-57`, `InMemoryMembershipRepository.FindByUserAndTenantAsync` deja de lanzar:

```csharp
    public Task<Membership?> FindByUserAndTenantAsync(
        Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(
            membership => membership.UserId == userId && membership.TenantId == tenantId));
```

`MembershipDirectoryTests.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4: los permisos miran también el tenant, en cada request.</summary>
public sealed class MembershipDirectoryTests
{
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly TenantId Tenant = TenantId.New();

    private static MembershipDirectory Directory(TenantStatus? tenantStatus) =>
        new(new InMemoryMembershipRepository(
                Membership.CreateActive(MembershipId.New(), User, Tenant, ["admin"], "registration", DateTimeOffset.UnixEpoch)),
            new FixedTenantDirectory(tenantStatus));

    [Fact]
    public async Task AnActiveTenantKeepsTheRoles() =>
        Assert.Equal(["admin"], await Directory(TenantStatus.Active)
            .FindActiveRolesAsync(User, Tenant.Value, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(null)]   // sin fila: fail closed, como el enmascarado del camino real
    public async Task AnyOtherTenantStateResolvesNoRoles(TenantStatus? status) =>
        Assert.Null(await Directory(status).FindActiveRolesAsync(User, Tenant.Value, TestContext.Current.CancellationToken));
}
```

En `RealAuthenticationApiTests.cs` (agregar `using Modules.Tenancy.Domain;` si falta — ya está — y `System.Net.Http.Json`):

```csharp
    // Spec 2026-10-08 §4 y criterio 5: misma cookie antes y después; los permisos se resuelven en cada
    // request y la sesión se arma de nuevo en /auth/me. Reactivar deja todo como estaba (D8).
    [Fact]
    public async Task ASuspendedTenantLeavesTheSessionAndForbidsTheCookieUntilReactivated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        await SetTenantStatusAsync(factory, tenantId, suspend: true);

        var session = await owner.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(session!.ActiveTenants, tenant => tenant.TenantId == tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetSettingsAsync(owner, tenantId)).StatusCode);

        await SetTenantStatusAsync(factory, tenantId, suspend: false);

        session = await owner.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", TestContext.Current.CancellationToken);
        Assert.Contains(session!.ActiveTenants, tenant => tenant.TenantId == tenantId);
        Assert.Equal(HttpStatusCode.OK, (await GetSettingsAsync(owner, tenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(owner, tenantId)).StatusCode);   // módulos intactos
    }

    private static async Task<HttpResponseMessage> GetSettingsAsync(HttpClient client, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/settings");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Por el agregado, no por SQL: es el mismo camino que usará el handler de la consola.
    private static async Task SetTenantStatusAsync(QepApiFactory factory, Guid tenantId, bool suspend)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var tenant = await dbContext.Tenants.SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        if (suspend)
        {
            tenant.Suspend(ChangeReason.Nonpayment, DateTimeOffset.UtcNow);
        }
        else
        {
            tenant.Reactivate(ChangeReason.Correction, DateTimeOffset.UtcNow);
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

En `OperatorConsoleApiTests.cs` (`SetTenantStatusAsync` se copia como `internal static` acá; cada ensamblado de pruebas ya duplica así sus helpers, precedente `RealAuthenticationApiTests.cs:63-65`):

```csharp
    // Spec 2026-10-08 §4: el stub consulta el estado con ITenantDirectory, no con ITenantModules.
    [Fact]
    public async Task TheStubEmitsNoTenantClaimForASuspendedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId, "tenancy.settings.read");
        Assert.Contains("tenancy.settings.read", await EffectivePermissionsAsync(client, tenantId));

        await SetTenantStatusAsync(factory, tenantId, suspend: true);

        var me = await client.GetAsync($"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        var modules = await client.GetAsync($"/api/v1/tenants/{tenantId}/modules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, modules.StatusCode);
    }

    // Por el agregado, igual que en RealAuthenticationApiTests (cada clase de pruebas lleva sus helpers).
    internal static async Task SetTenantStatusAsync(QepApiFactory factory, Guid tenantId, bool suspend)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var tenant = await dbContext.Tenants.SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        if (suspend)
        {
            tenant.Suspend(ChangeReason.Nonpayment, DateTimeOffset.UtcNow);
        }
        else
        {
            tenant.Reactivate(ChangeReason.Correction, DateTimeOffset.UtcNow);
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

Usings que el archivo gana en esta tarea: `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.DependencyInjection`, `Modules.Tenancy.Domain`, `Modules.Tenancy.Infrastructure.Persistence`.

En `QuotationTenantLogoLookupTests.FakeTenantDirectory`:

```csharp
        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
```

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipDirectoryTests"
```
Expected: compilación — `error CS1729: 'MembershipDirectory' does not contain a constructor that takes 2 arguments`. (`FixedTenantDirectory` ya compila: declarar un método de más no es error.)

- [ ] **Step 3: Implement**

`ITenantDirectory.cs`:

```csharp
    /// <summary>Spec 2026-10-08 §4: el estado del tenant, o null si no tiene fila (el tenant simulado del
    /// stub). Es el puerto propio del stub para el estado: <see cref="ITenantModules.FindAsync"/> no cambia
    /// su semántica de null («no enmascarar»), porque usarlo para "inactivo" le daría al stub todo lo de
    /// X-Permissions sin enmascarar.</summary>
    Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken);
```

`TenantDirectory.cs`:

```csharp
    public async Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => (TenantStatus?)tenant.Status)
            .SingleOrDefaultAsync(cancellationToken);
```

`MembershipDirectory.cs`:

```csharp
public sealed class MembershipDirectory(
    IMembershipRepository membershipRepository,
    ITenantDirectory tenantDirectory)
    : IMembershipDirectory
{
    public async Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membership = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            new TenantId(tenantId),
            cancellationToken);
        if (membership is not { State: MembershipState.Active })
        {
            return null;
        }

        // Spec 2026-10-08 §4: un tenant que no está Active no resuelve roles. ResolvePermissionsAsync
        // devuelve null, ExternalClaimsTransformation no agrega el claim de tenant y todo endpoint del
        // tenant responde 403, incluso con la sesión ya abierta. Sin fila también es null (fail closed).
        var status = await tenantDirectory.GetStatusAsync(new TenantId(tenantId), cancellationToken);
        return status == TenantStatus.Active ? membership.Roles : null;
    }
```

(los otros dos métodos sin cambios).

`MembershipRepository.ListActiveTenantSummariesByUserAsync` (`:42`):

```csharp
            // Spec 2026-10-08 §4: un tenant inactivo desaparece del selector de sesión (login y /auth/me).
            where membership.UserId == userId
                && membership.State == MembershipState.Active
                && tenant.Status == TenantStatus.Active
```

`DevelopmentAuthenticationHandler.cs` — séptimo parámetro `ITenantDirectory tenantDirectory` (agregar `using Modules.Tenancy.Domain;`):

```csharp
        List<Claim> claims = [new(QepClaimTypes.SubjectId, subjectId)];

        // Spec 2026-10-08 §4: con fila y no Active, ni claim de tenant ni permisos — el mismo efecto que
        // el camino real. Sin fila (tenant simulado), sin cambio.
        var status = await tenantDirectory.GetStatusAsync(new TenantId(parsedTenantId), Context.RequestAborted);
        if (status is null or TenantStatus.Active)
        {
            claims.Add(new Claim(QepClaimTypes.TenantId, tenantId));
            var requested = ResolvePermissions();
            var modules = await tenantModules.FindAsync(parsedTenantId, Context.RequestAborted);
            var masked = modules is null ? requested : entitlementMask.Apply(requested, modules);
            foreach (var permission in OperatorPermissionFilter.Apply(masked, operatorTenant.IsOperator(parsedTenantId)))
            {
                claims.Add(new Claim(QepClaimTypes.Permission, permission));
            }
        }
```

(el bloque de `X-Email` queda igual, fuera del `if`).

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipDirectoryTests|FullyQualifiedName~ListMembershipsHandlerTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationTenantLogoLookupTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~RealAuthenticationApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~TenantModulesApiTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs tests/Bootstrapper/Bootstrapper.UnitTests/QuotationTenantLogoLookupTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipDirectoryTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(tenancy): un tenant inactivo sale de la sesión y pierde sus permisos"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 8: API de lectura — lista y detalle de tenants

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorAuthorization.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenantReader.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorInput.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorTenantDetails.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ListOperatorTenants.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/GetOperatorTenant.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs` (registro del lector)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:56-67` (handlers, junto a los de Tenancy)
- Modify: `src/Api/Program.cs:128` (`app.MapOperatorEndpoints();` después de `MapTenantSettingsEndpoints`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs:576-612` (parámetro `operatorTenantId` de la fábrica)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorReadHandlerTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`, `RealAuthenticationApiTests.cs`

**Interfaces:**
- Consumes: `IOperatorTenant` (1), `OperatorPermissions` (2), vocabulario y `TenantModuleStatus` (4), `TenantChanges` (5), `IExecutionContext`.
- Produces (todo `Modules.Tenancy.Application`):
  - `internal static class OperatorAuthorization { static void EnsureAuthorized(IExecutionContext, TenantId tenantId, string permission, IOperatorTenant) }`.
  - DTOs (forma exacta de §5): `OperatorTenantPageDto(IReadOnlyList<OperatorTenantListItemDto> Items, int Total, int Page, int PageSize, OperatorTenantSummaryDto Summary)`, `OperatorTenantListItemDto(Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt, int ActiveModules, int TotalModules, bool IsOperator)`, `OperatorTenantSummaryDto(int Total, int WithoutModules, int Inactive)`, `OperatorTenantDetailDto(Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status, DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator, IReadOnlyList<OperatorTenantModuleDto> Modules)`, `OperatorTenantModuleDto(string Key, string Status, bool Enabled, IReadOnlyList<string> Dependencies, DateTimeOffset? Since, string? Source, string? LastReason)`.
  - `IOperatorTenantReader { ListAsync(string? search, int page, int pageSize, ct) → OperatorTenantListing; FindAsync(TenantId, ct) → OperatorTenantSnapshot? }` y los registros `OperatorTenantListing(IReadOnlyList<OperatorTenantRow> Rows, int Total, int AllTenants, int WithoutModules, int Inactive)`, `OperatorTenantRow(Guid TenantId, string Slug, string DisplayName, TenantStatus Status, DateTimeOffset CreatedAt, int ActiveModules)`, `OperatorTenantSnapshot(Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, TenantStatus Status, long Version, IReadOnlyList<TenantModuleState> Modules, IReadOnlyDictionary<TenantModuleKey, ChangeReason> LastModuleReasons, TenantStatusChange? LastStatusChange)`, `TenantModuleState(TenantModuleKey Key, TenantModuleStatus Status, DateTimeOffset StatusChangedAt, string Source)`, `TenantStatusChange(DateTimeOffset OccurredAt, ChangeReason Reason)`.
  - `internal static class OperatorInput` (`IsModuleKey`, `IsDirection`, `IsReason`, `PageSizeMax = 100`, `DefaultPageSize = 25`).
  - `internal static class OperatorTenantDetails { static Task<OperatorTenantDetailDto> LoadAsync(IOperatorTenantReader, IOperatorTenant, TenantId, ct); }` — lanza `ResourceNotFoundException("tenancy.tenant.not_found")`.
  - `ListOperatorTenantsQuery(TenantId TenantId, string? Search, int Page, int PageSize) : IQuery<OperatorTenantPageDto>` + `ListOperatorTenantsValidator` + `ListOperatorTenantsHandler`.
  - `GetOperatorTenantQuery(TenantId TenantId, TenantId TargetTenantId) : IQuery<OperatorTenantDetailDto>` + `GetOperatorTenantHandler`.
  - `Modules.Tenancy.Api.OperatorEndpoints.MapOperatorEndpoints(this IEndpointRouteBuilder)`.

- [ ] **Step 1: Write the failing tests**

Dobles en `OperatorTestDoubles.cs` (Tenancy.UnitTests):

```csharp
internal sealed class FixedOperatorTenant(Guid? operatorTenantId) : IOperatorTenant
{
    public bool IsOperator(Guid tenantId) => operatorTenantId == tenantId;
}

internal sealed class OperatorContext(TenantId tenantId, params string[] permissions) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();
    public TenantId TenantId => tenantId;
    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>El lector con respuestas fijas. Anota cada pregunta: las pruebas de doble capa exigen que un
/// no operador nunca llegue a buscar el destino.</summary>
internal sealed class FixedOperatorTenantReader : IOperatorTenantReader
{
    public OperatorTenantListing Listing { get; set; } = new([], 0, 0, 0, 0);
    public Dictionary<TenantId, OperatorTenantSnapshot> Snapshots { get; } = [];
    public List<string> Asked { get; } = [];

    public Task<OperatorTenantListing> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        Asked.Add($"list:{search}:{page}:{pageSize}");
        return Task.FromResult(Listing);
    }

    public Task<OperatorTenantSnapshot?> FindAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Asked.Add($"find:{tenantId}");
        return Task.FromResult(Snapshots.GetValueOrDefault(tenantId));
    }
}

internal static class OperatorSnapshots
{
    public static OperatorTenantSnapshot Of(
        TenantId tenantId, IEnumerable<TenantModuleState> modules, TenantStatus status = TenantStatus.Active,
        IReadOnlyDictionary<TenantModuleKey, ChangeReason>? lastReasons = null, TenantStatusChange? lastStatus = null) =>
        new(tenantId.Value, "origen-botanico", "Origen Botánico", DateTimeOffset.UnixEpoch, status, 1,
            modules.ToArray(), lastReasons ?? new Dictionary<TenantModuleKey, ChangeReason>(), lastStatus);

    public static IEnumerable<TenantModuleState> Signup() =>
        TenantModuleKeys.DefaultForNewTenants.Select(key =>
            new TenantModuleState(key, TenantModuleStatus.Active, DateTimeOffset.UnixEpoch, TenantModuleSources.Signup));
}
```

`OperatorReadHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5: lista y detalle, con la doble capa y el 403 antes del 404.</summary>
public sealed class OperatorReadHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Target = TenantId.New();

    private static GetOperatorTenantHandler Detail(FixedOperatorTenantReader reader, IExecutionContext context) =>
        new(reader, context, new FixedOperatorTenant(Operator.Value));

    private static ListOperatorTenantsHandler List(FixedOperatorTenantReader reader, IExecutionContext context) =>
        new(reader, context, new FixedOperatorTenant(Operator.Value), new ListOperatorTenantsValidator());

    // §5, «Orden de chequeos»: el 404 no le sirve de oráculo a quien no es operador. Escenarios por
    // nombre y no TheoryData<IExecutionContext>: xUnit1045 (dato no serializable) es error acá.
    [Theory]
    [InlineData("claim-is-not-the-operator")]
    [InlineData("no-permission")]
    [InlineData("route-is-not-the-claim")]
    public async Task ANonOperatorIsForbiddenBeforeLookingUpTheTarget(string scenario)
    {
        var reader = new FixedOperatorTenantReader();
        var (context, routeTenant) = scenario switch
        {
            "claim-is-not-the-operator" => (new OperatorContext(Target, OperatorPermissions.TenantsRead), Target),
            "no-permission" => (new OperatorContext(Operator), Operator),
            _ => (new OperatorContext(TenantId.New(), OperatorPermissions.TenantsRead), Operator),
        };

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Detail(reader, context).HandleAsync(new GetOperatorTenantQuery(routeTenant, TenantId.New()), TestContext.Current.CancellationToken));
        Assert.Empty(reader.Asked);
    }

    [Fact]
    public async Task AMissingTargetIsNotFoundForTheOperator()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Detail(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new GetOperatorTenantQuery(Operator, Target), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task TheDetailListsTheSevenModulesInCatalogOrder()
    {
        var reader = new FixedOperatorTenantReader();
        var modules = OperatorSnapshots.Signup()
            .Select(module => module.Key == TenantModuleKeys.Customers
                ? module with { Status = TenantModuleStatus.Inactive }
                : module);
        var suspendedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        reader.Snapshots[Target] = OperatorSnapshots.Of(
            Target, modules, TenantStatus.Suspended,
            new Dictionary<TenantModuleKey, ChangeReason> { [TenantModuleKeys.Customers] = ChangeReason.Cancellation },
            new TenantStatusChange(suspendedAt, ChangeReason.Nonpayment));

        var detail = await Detail(reader, new OperatorContext(Operator, OperatorPermissions.TenantsRead))
            .HandleAsync(new GetOperatorTenantQuery(Operator, Target), TestContext.Current.CancellationToken);

        Assert.Equal(["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos"],
            detail.Modules.Select(module => module.Key));
        var byKey = detail.Modules.ToDictionary(module => module.Key);
        Assert.Equal("inactive", byKey["customers"].Status);
        Assert.Equal("cancellation", byKey["customers"].LastReason);
        Assert.Equal("active", byKey["quotations"].Status);
        Assert.False(byKey["quotations"].Enabled);                 // efectivo: le falta customers
        Assert.Equal(["catalog", "customers", "companies"], byKey["quotations"].Dependencies);
        Assert.Equal("none", byKey["pos"].Status);
        Assert.Null(byKey["pos"].Since);
        Assert.Null(byKey["pos"].Source);
        Assert.Equal("Suspended", detail.Status);
        Assert.Equal(suspendedAt, detail.StatusChangedAt);
        Assert.Equal("nonpayment", detail.StatusReason);
        Assert.False(detail.IsOperator);
    }

    [Fact]
    public async Task TheListCarriesTotalModulesTheOperatorMarkAndTheSummary()
    {
        var reader = new FixedOperatorTenantReader
        {
            Listing = new(
                [new OperatorTenantRow(Operator.Value, "qcode", "QCode", TenantStatus.Active, DateTimeOffset.UnixEpoch, 7),
                 new OperatorTenantRow(Target.Value, "origen", "Origen", TenantStatus.Suspended, DateTimeOffset.UnixEpoch, 0)],
                Total: 2, AllTenants: 6, WithoutModules: 1, Inactive: 1),
        };

        var page = await List(reader, new OperatorContext(Operator, OperatorPermissions.TenantsRead))
            .HandleAsync(new ListOperatorTenantsQuery(Operator, "o", 1, 25), TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Total);
        Assert.Equal(new OperatorTenantSummaryDto(6, 1, 1), page.Summary);
        Assert.All(page.Items, item => Assert.Equal(7, item.TotalModules));
        Assert.True(page.Items[0].IsOperator);
        Assert.Equal("Suspended", page.Items[1].Status);
        Assert.Equal(["list:o:1:25"], reader.Asked);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task OutOfRangePagingIsAValidationError(int page, int pageSize) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            List(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new ListOperatorTenantsQuery(Operator, null, page, pageSize), TestContext.Current.CancellationToken));

    [Fact]
    public async Task ASearchLongerThanOneHundredIsAValidationError() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            List(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new ListOperatorTenantsQuery(Operator, new string('a', 101), 1, 25), TestContext.Current.CancellationToken));
}
```

`OperatorConsoleApiTests.cs` — casos nuevos y registros (al final de la clase):

```csharp
    [Fact]
    public async Task AnOperatorListsTenantsWithTheSummaryAndSearches()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (first, _) = await RegisterAsync(factory);
        await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        var all = await GetOkAsync<TenantPagePayload>(client, Url("tenants"));
        Assert.Equal(2, all.Total);
        Assert.Equal(new SummaryPayload(2, 0, 0), all.Summary);
        Assert.All(all.Items, item => Assert.Equal((6, 7, "Active", false), (item.ActiveModules, item.TotalModules, item.Status, item.IsOperator)));

        var slug = (await GetOkAsync<DetailPayload>(client, Url($"tenants/{first}"))).Slug;
        var searched = await GetOkAsync<TenantPagePayload>(client, Url($"tenants?search={slug}"));
        Assert.Equal(first, Assert.Single(searched.Items).TenantId);
        Assert.Equal(1, searched.Total);
        Assert.Equal(2, searched.Summary.Total);   // el resumen no se filtra

        // Review Focus 2: el comodín es literal.
        Assert.Equal(0, (await GetOkAsync<TenantPagePayload>(client, Url("tenants?search=%25"))).Total);
        // Review Focus 4: más allá de la última página, vacío con el total intacto.
        var beyond = await GetOkAsync<TenantPagePayload>(client, Url("tenants?page=5&pageSize=1"));
        Assert.Empty(beyond.Items);
        Assert.Equal(2, beyond.Total);
    }

    // Contrato con la SPA (se construye en paralelo): nombres exactos de §5.
    [Fact]
    public async Task TheDetailJsonMatchesTheContract()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        using var json = System.Text.Json.JsonDocument.Parse(
            await client.GetStringAsync(Url($"tenants/{tenantId}"), TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal(
            ["tenantId", "slug", "displayName", "createdAt", "status", "statusChangedAt", "statusReason", "version", "isOperator", "modules"],
            root.EnumerateObject().Select(property => property.Name));
        var pos = root.GetProperty("modules")[6];
        Assert.Equal(
            ["key", "status", "enabled", "dependencies", "since", "source", "lastReason"],
            pos.EnumerateObject().Select(property => property.Name));
        Assert.Equal("none", pos.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, pos.GetProperty("since").ValueKind);
        Assert.Equal("Active", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AnUnknownTargetIsNotFoundForTheOperator()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync(Url($"tenants/{Guid.CreateVersion7()}"), TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound, "tenancy.tenant.not_found");
    }

    // Spec «Errores y casos borde»: el permiso inyectado por X-Permissions en otro tenant no alcanza.
    [Fact]
    public async Task ANonOperatorTenantIsForbiddenEvenWithThePermissionInjected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), otherTenantId, AllOperatorPermissions);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{otherTenantId}/operator/tenants", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Doble capa: la política pasa (el claim es el operador) y el handler ve que la ruta no coincide.
    [Fact]
    public async Task TheRouteTenantMustMatchTheClaim()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync($"/api/v1/tenants/{Guid.CreateVersion7()}/operator/tenants", TestContext.Current.CancellationToken),
            HttpStatusCode.Forbidden, "authorization.denied");
    }

    [Fact]
    public async Task OutOfRangePagingIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync(Url("tenants?pageSize=0"), TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    private static HttpClient OperatorClient(QepApiFactory factory, Guid? subjectId = null) =>
        StubClient(factory, subjectId ?? Guid.CreateVersion7(), OperatorTenantId, AllOperatorPermissions);

    private static string Url(string path) => $"/api/v1/tenants/{OperatorTenantId}/operator/{path}";

    private static async Task<T> GetOkAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private sealed record TenantPagePayload(List<TenantItemPayload> Items, int Total, int Page, int PageSize, SummaryPayload Summary);
    private sealed record TenantItemPayload(Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt, int ActiveModules, int TotalModules, bool IsOperator);
    private sealed record SummaryPayload(int Total, int WithoutModules, int Inactive);
    private sealed record DetailPayload(Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status, DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator, List<ModulePayload> Modules);
    private sealed record ModulePayload(string Key, string Status, bool Enabled, string[] Dependencies, DateTimeOffset? Since, string? Source, string? LastReason);
```

En `RealAuthenticationApiTests.cs`: la fábrica (`:576`) pasa a `QepApiFactory(string connectionString, Guid? operatorTenantId = null)` con `if (operatorTenantId is { } id) builder.UseSetting("Platform:OperatorTenantId", id.ToString());`, y (agregar `using Modules.Authorization.Domain; using Modules.Authorization.Infrastructure.Persistence; using Modules.Tenancy.Application;`):

```csharp
    // Spec «Errores y casos borde» y criterio 2: un rol personalizado con operator.* insertado por SQL,
    // fuera del tenant operador, no abre la consola. El filtro corre en ResolvePermissionsAsync.
    [Fact]
    public async Task ACustomRoleWithOperatorPermissionsDoesNotOpenTheConsole()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: Guid.CreateVersion7());
        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            roles.Roles.Add(Role.Create(RoleId.New(), tenantId, "operador-falso", "Operador falso", "",
                [OperatorPermissions.TenantsRead], DateTimeOffset.UtcNow));
            await roles.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE tenancy.memberships SET roles = array_append(roles, 'operador-falso') WHERE tenant_id = {tenantId}",
                TestContext.Current.CancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/operator/tenants");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var response = await owner.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~OperatorReadHandlerTests"
```
Expected: compilación — `CS0246 'IOperatorTenantReader'`, `'GetOperatorTenantHandler'`, `'ListOperatorTenantsQuery'`…

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~RealAuthenticationApiTests.ACustomRoleWithOperatorPermissions"
```
Expected: los casos nuevos fallan con `NotFound` (la ruta todavía no existe) donde se esperaba `200`/`403`/`422`; `ACustomRoleWithOperatorPermissionsDoesNotOpenTheConsole` también (`Expected: Forbidden, Actual: NotFound`). Copiar la corrida.

- [ ] **Step 3: Implement — Application**

`OperatorAuthorization.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §5: segunda capa de los endpoints de operador. El tenant de la ruta es el del claim,
/// el permiso está en los claims <b>y</b> ese tenant es el operador. Va antes de buscar el destino para
/// que el 404 no le sirva de oráculo a quien no es operador. Cualquier falla: 403.
/// </summary>
internal static class OperatorAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext, TenantId tenantId, string permission, IOperatorTenant operatorTenant)
    {
        if (executionContext.TenantId != tenantId
            || !executionContext.HasPermission(permission)
            || !operatorTenant.IsOperator(tenantId.Value))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot operate the platform from this tenant.");
        }
    }
}
```

`OperatorDtos.cs` — la forma de §5, con el porqué BFF en cada DTO (regla de `CLAUDE.md`):

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// Una página de tenants para la consola. <see cref="Summary"/> viaja con la página por la misma razón que
/// <c>/reports/orders/summary</c>: sumarlo en el cliente depende de la página que se mire; cuenta todos
/// los tenants, sin el filtro de búsqueda.
/// </summary>
public sealed record OperatorTenantPageDto(
    IReadOnlyList<OperatorTenantListItemDto> Items, int Total, int Page, int PageSize, OperatorTenantSummaryDto Summary);

/// <param name="Status">El nombre del enum (<c>Active</c>, <c>Suspended</c>): el diccionario lo tiene la SPA.</param>
/// <param name="ActiveModules">Filas activas (contratados), no efectivos (decisión P7 del plan).</param>
/// <param name="TotalModules"><c>TenantModuleKeys.All.Count</c>: la pantalla no conoce la lista del backend
/// para dibujar «n de 7».</param>
/// <param name="IsOperator">Marca al operador sin que la SPA conozca la configuración.</param>
public sealed record OperatorTenantListItemDto(
    Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt,
    int ActiveModules, int TotalModules, bool IsOperator);

public sealed record OperatorTenantSummaryDto(int Total, int WithoutModules, int Inactive);

/// <param name="StatusChangedAt">Del último cambio de estado del tenant en el historial; null si nunca cambió.</param>
/// <param name="Version">La del agregado: es el <c>If-Match</c> de <c>POST …/status</c>.</param>
/// <param name="Modules">Siempre las siete, en el orden de <c>TenantModuleKeys.All</c>, aunque no tengan fila:
/// si faltara una, la SPA tendría que conocer la lista del backend para dibujarla.</param>
public sealed record OperatorTenantDetailDto(
    Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status,
    DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator,
    IReadOnlyList<OperatorTenantModuleDto> Modules);

/// <param name="Status"><c>active</c>, <c>inactive</c> o <c>none</c> (sin fila): «nunca se activó» y «se
/// desactivó» se dibujan distinto.</param>
/// <param name="Enabled">El efectivo, con las dependencias cerradas.</param>
/// <param name="Dependencies">Directas: la consola calcula la cascada sin duplicar el grafo del backend.</param>
/// <param name="Since"><c>status_changed_at</c>; null sin fila.</param>
/// <param name="LastReason">Del último cambio de este módulo en el historial; null si nunca lo tocó la consola.</param>
public sealed record OperatorTenantModuleDto(
    string Key, string Status, bool Enabled, IReadOnlyList<string> Dependencies,
    DateTimeOffset? Since, string? Source, string? LastReason)
{
    public const string NoRowStatus = "none";
}
```

`IOperatorTenantReader.cs` — la interfaz y los registros de la sección «Interfaces» (los registros son del lado de lectura: tipos de dominio, sin texto; el texto lo pone `OperatorTenantDetails`).

`OperatorInput.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §5: claves, estados y motivos llegan como texto y se validan <b>antes</b> de convertir.
/// Convertir primero haría que <c>TenantModuleKey.Parse</c> (lanza <c>ArgumentException</c>) saliera como
/// 500. Sin ignorar mayúsculas, igual que los CHECK.
/// </summary>
internal static class OperatorInput
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    public static bool IsModuleKey(string? value) =>
        value is not null && TenantModuleKeys.All.Any(key => string.Equals(key.Value, value, StringComparison.Ordinal));

    /// <summary><c>active</c> o <c>inactive</c>: la dirección de un cambio de módulo o de tenant.</summary>
    public static bool IsDirection(string? value) => TenantChangeVocabulary.TryParseModuleStatus(value, out _);

    public static bool IsReason(string? value) => TenantChangeVocabulary.TryParseReason(value, out _);
}
```

`OperatorTenantDetails.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>El detalle de §5. Lo usan el GET y, con el estado ya commiteado, los dos POST.</summary>
internal static class OperatorTenantDetails
{
    public static async Task<OperatorTenantDetailDto> LoadAsync(
        IOperatorTenantReader reader, IOperatorTenant operatorTenant, TenantId tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.FindAsync(tenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");
        return From(snapshot, operatorTenant.IsOperator(snapshot.TenantId));
    }

    private static OperatorTenantDetailDto From(OperatorTenantSnapshot snapshot, bool isOperator)
    {
        var rows = snapshot.Modules.ToDictionary(module => module.Key);
        var effective = TenantModuleSet.FromStored(
            snapshot.Modules.Where(module => module.Status == TenantModuleStatus.Active).Select(module => module.Key));
        var modules = TenantModuleKeys.All
            .Select(key =>
            {
                var row = rows.GetValueOrDefault(key);
                return new OperatorTenantModuleDto(
                    key.Value,
                    row is null ? OperatorTenantModuleDto.NoRowStatus : TenantChangeVocabulary.ToText(row.Status),
                    effective.IsEnabled(key),
                    TenantModuleKeys.DependenciesOf(key).Select(dependency => dependency.Value).ToArray(),
                    row?.StatusChangedAt,
                    row?.Source,
                    snapshot.LastModuleReasons.TryGetValue(key, out var reason) ? TenantChangeVocabulary.ToText(reason) : null);
            })
            .ToArray();

        return new OperatorTenantDetailDto(
            snapshot.TenantId, snapshot.Slug, snapshot.DisplayName, snapshot.CreatedAt, snapshot.Status.ToString(),
            snapshot.LastStatusChange?.OccurredAt,
            snapshot.LastStatusChange is { } change ? TenantChangeVocabulary.ToText(change.Reason) : null,
            snapshot.Version, isOperator, modules);
    }
}
```

`ListOperatorTenants.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record ListOperatorTenantsQuery(TenantId TenantId, string? Search, int Page, int PageSize)
    : IQuery<OperatorTenantPageDto>;

public sealed class ListOperatorTenantsValidator : AbstractValidator<ListOperatorTenantsQuery>
{
    public ListOperatorTenantsValidator()
    {
        RuleFor(query => query.Search).MaximumLength(OperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, OperatorInput.MaxPageSize);
    }
}

public sealed class ListOperatorTenantsHandler(
    IOperatorTenantReader reader,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListOperatorTenantsQuery> validator)
    : IQueryHandler<ListOperatorTenantsQuery, OperatorTenantPageDto>
{
    public async Task<OperatorTenantPageDto> HandleAsync(ListOperatorTenantsQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var listing = await reader.ListAsync(query.Search, query.Page, query.PageSize, cancellationToken);
        var items = listing.Rows
            .Select(row => new OperatorTenantListItemDto(
                row.TenantId, row.Slug, row.DisplayName, row.Status.ToString(), row.CreatedAt,
                row.ActiveModules, TenantModuleKeys.All.Count, operatorTenant.IsOperator(row.TenantId)))
            .ToArray();
        return new OperatorTenantPageDto(
            items, listing.Total, query.Page, query.PageSize,
            new OperatorTenantSummaryDto(listing.AllTenants, listing.WithoutModules, listing.Inactive));
    }
}
```

`GetOperatorTenant.cs`:

```csharp
public sealed record GetOperatorTenantQuery(TenantId TenantId, TenantId TargetTenantId) : IQuery<OperatorTenantDetailDto>;

public sealed class GetOperatorTenantHandler(
    IOperatorTenantReader reader,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetOperatorTenantQuery, OperatorTenantDetailDto>
{
    public Task<OperatorTenantDetailDto> HandleAsync(GetOperatorTenantQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        // D7: aquí el 404 sí es correcto; el operador ve todos los tenants por diseño.
        return OperatorTenantDetails.LoadAsync(reader, operatorTenant, query.TargetTenantId, cancellationToken);
    }
}
```

- [ ] **Step 4: Implement — Infrastructure, Api y composición**

`OperatorTenantReader.cs` (`internal sealed`, `AsNoTracking` en todo):

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class OperatorTenantReader(TenancyDbContext dbContext) : IOperatorTenantReader
{
    private const string LikeEscapeCharacter = "\\";

    public async Task<OperatorTenantListing> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var tenants = dbContext.Tenants.AsNoTracking();
        var pattern = LikePattern(search);
        var filtered = pattern is null
            ? tenants
            : tenants.Where(tenant =>
                EF.Functions.ILike(tenant.DisplayName, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(tenant.Slug, pattern, LikeEscapeCharacter));

        var total = await filtered.CountAsync(cancellationToken);
        var rows = await filtered
            .OrderBy(tenant => tenant.DisplayName)
            .ThenBy(tenant => tenant.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tenant => new OperatorTenantRow(
                tenant.Id.Value, tenant.Slug, tenant.DisplayName, tenant.Status, tenant.CreatedAt,
                dbContext.TenantModules.Count(module =>
                    module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active)))
            .ToListAsync(cancellationToken);

        // El resumen cuenta todos los tenants, sin la búsqueda (§5).
        var allTenants = await tenants.CountAsync(cancellationToken);
        var withoutModules = await tenants.CountAsync(
            tenant => !dbContext.TenantModules.Any(module =>
                module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active),
            cancellationToken);
        var inactive = await tenants.CountAsync(tenant => tenant.Status != TenantStatus.Active, cancellationToken);

        return new OperatorTenantListing(rows, total, allTenants, withoutModules, inactive);
    }

    public async Task<OperatorTenantSnapshot?> FindAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.AsNoTracking()
            .Where(value => value.Id == tenantId)
            .Select(value => new { value.Slug, value.DisplayName, value.CreatedAt, value.Status, value.Version })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var modules = await dbContext.TenantModules.AsNoTracking()
            .Where(module => module.TenantId == tenantId)
            .Select(module => new TenantModuleState(module.ModuleKey, module.Status, module.StatusChangedAt, module.Source))
            .ToListAsync(cancellationToken);

        // El último motivo por módulo se elige en memoria: el historial de un tenant es corto, y un
        // "último por grupo" en SQL no se justifica hoy.
        var moduleChanges = await dbContext.TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId && change.Kind == TenantChangeKind.Module)
            .OrderByDescending(change => change.OccurredAt)
            .Select(change => new { change.ModuleKey, change.Reason })
            .ToListAsync(cancellationToken);
        var lastReasons = moduleChanges
            .Where(change => change.ModuleKey is not null)
            .DistinctBy(change => change.ModuleKey!)
            .ToDictionary(change => change.ModuleKey!, change => change.Reason);

        var lastStatus = await dbContext.TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId && change.Kind == TenantChangeKind.TenantStatus)
            .OrderByDescending(change => change.OccurredAt)
            .Select(change => new TenantStatusChange(change.OccurredAt, change.Reason))
            .FirstOrDefaultAsync(cancellationToken);

        return new OperatorTenantSnapshot(
            tenantId.Value, tenant.Slug, tenant.DisplayName, tenant.CreatedAt, tenant.Status, tenant.Version,
            modules, lastReasons, lastStatus);
    }

    // Mismo escape que ProductRepository.cs:9-24: la barra va primero.
    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : "%" + trimmed
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }
}
```

Registro en `AddTenancyInfrastructure`: `services.AddScoped<IOperatorTenantReader, OperatorTenantReader>();`.

`src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

/// <summary>
/// Spec 2026-10-08 §5 (D6): la ruta lleva el tenant desde el que actúa el operador (QCode), igual que
/// el resto de la API; el administrado va como <c>{targetTenantId}</c>. Doble capa: la política exige el
/// permiso <c>operator.*</c> y el handler revalida con <c>OperatorAuthorization</c>.
/// </summary>
public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator")
            .WithTags("Operator");

        group.MapGet("/tenants", ListTenantsAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorTenantPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tenants/{targetTenantId:guid}", GetTenantAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListTenantsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 25)
    {
        var result = await dispatcher.QueryAsync(
            new ListOperatorTenantsQuery(new TenantId(tenantId), search, page, pageSize), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetTenantAsync(
        Guid tenantId,
        Guid targetTenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetOperatorTenantQuery(new TenantId(tenantId), new TenantId(targetTenantId)), cancellationToken);
        return Results.Ok(result);
    }
}
```

`QepServiceCollectionExtensions.cs`, junto a los handlers de Tenancy (`:56-67`):

```csharp
        // Spec 2026-10-08 §5: la consola de operador.
        services.AddScoped<
            IQueryHandler<ListOperatorTenantsQuery, OperatorTenantPageDto>,
            ListOperatorTenantsHandler>();
        services.AddScoped<
            IQueryHandler<GetOperatorTenantQuery, OperatorTenantDetailDto>,
            GetOperatorTenantHandler>();
```

`Program.cs`: `app.MapOperatorEndpoints();` después de `app.MapTenantSettingsEndpoints();` (`:128`).

- [ ] **Step 5: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~OperatorReadHandlerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~TenancyLayerTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~RealAuthenticationApiTests"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/OperatorAuthorization.cs src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenantReader.cs src/Modules/Tenancy/Modules.Tenancy.Application/OperatorInput.cs src/Modules/Tenancy/Modules.Tenancy.Application/OperatorTenantDetails.cs src/Modules/Tenancy/Modules.Tenancy.Application/ListOperatorTenants.cs src/Modules/Tenancy/Modules.Tenancy.Application/GetOperatorTenant.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs src/Api/Program.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorReadHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs
git commit -m "feat(tenancy): consola de operador, lista y detalle de tenants"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 9: `POST …/modules/changes` — un lote atómico con historial y auditoría

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ChangeTenantModulesHandlerTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`

**Interfaces:**
- Consumes: `TenantModuleChangeBatch`, `TenantChange` (4); `TenantModule.Activate/Deactivate`, `TenantModuleSources.Operator` (5); `ITenantModuleRepository.ListByTenantAsync`, `ITenantChangeRepository`, `BeginTenantChangeScopeAsync` (6); `OperatorAuthorization`, `OperatorInput`, `OperatorTenantDetails`, `IOperatorTenantReader` (8); `ITenantRepository`, `IAuditRecorder`, `IClock`.
- Produces: `TenantModuleChangeInput(string? Key, string? Status)` (también es el ítem del cuerpo JSON), `ChangeTenantModulesCommand(TenantId TenantId, TenantId TargetTenantId, IReadOnlyList<TenantModuleChangeInput>? Changes, string? Reason, string? Note) : ICommand<OperatorTenantDetailDto>`, `ChangeTenantModulesValidator`, `ChangeTenantModulesHandler`, `ChangeTenantModulesRequest` (Api).

- [ ] **Step 1: Write the failing tests**

Dobles nuevos en `OperatorTestDoubles.cs`:

```csharp
internal sealed class StepsTenantRepository(List<string> steps, params Tenant[] tenants) : ITenantRepository
{
    public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken)
    {
        steps.Add("read-tenant");
        return Task.FromResult(tenants.SingleOrDefault(tenant => tenant.Id == id));
    }

    public void Add(Tenant tenant) => throw new NotSupportedException();
}

internal sealed class StepsTenantModuleRepository(List<string> steps, params TenantModule[] rows) : ITenantModuleRepository
{
    public List<TenantModule> Rows { get; } = [.. rows];
    public List<TenantModule> Added { get; } = [];

    public void Add(TenantModule tenantModule) => Added.Add(tenantModule);

    public Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        steps.Add("read-modules");
        return Task.FromResult<IReadOnlyList<TenantModule>>(Rows.Where(row => row.TenantId == tenantId).ToList());
    }
}

internal sealed class RecordingTenantChangeRepository : ITenantChangeRepository
{
    public List<TenantChange> Added { get; } = [];
    public void Add(TenantChange change) => Added.Add(change);
}

/// <summary>Anota el candado, el SaveChanges y el commit en el orden en que pasan.</summary>
internal sealed class StepsUnitOfWork(List<string> steps) : ITenancyUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        steps.Add("save");
        return Task.FromResult(1);
    }

    public Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ITenantChangeScope> BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        steps.Add($"lock:{tenantId}");
        return Task.FromResult<ITenantChangeScope>(new Scope(steps));
    }

    private sealed class Scope(List<string> steps) : ITenantChangeScope
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            steps.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingAuditRecorder : IAuditRecorder
{
    public List<(Guid? TenantId, Guid ActorId, string Action, string ResourceType, string ResourceId, IReadOnlyCollection<string> ChangedFields)> Entries { get; } = [];

    public void Record(
        Guid? tenantId, Guid actorId, string action, string resourceType, string resourceId, string outcome,
        IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human, string source = "") =>
        Entries.Add((tenantId, actorId, action, resourceType, resourceId, changedFields));
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}
```

(usings: `BuildingBlocks.Application`, `Modules.Audit.Application`, `Modules.Audit.Domain`).

`ChangeTenantModulesHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5, <c>POST …/modules/changes</c>.</summary>
public sealed class ChangeTenantModulesHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public List<string> Steps { get; } = [];
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public StepsTenantModuleRepository Modules { get; }
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();
        public FixedOperatorTenantReader Reader { get; } = new();
        public OperatorContext Context { get; } = new(Operator, OperatorPermissions.ModulesManage);

        public Fixture()
        {
            Modules = new StepsTenantModuleRepository(Steps, TenantModuleKeys.DefaultForNewTenants
                .Select(key => TenantModule.Create(Target.Id, key, TenantModuleSources.Signup, Now.AddDays(-30), null))
                .ToArray());
            Reader.Snapshots[Target.Id] = OperatorSnapshots.Of(Target.Id, OperatorSnapshots.Signup());
        }

        public ChangeTenantModulesHandler Handler(IExecutionContext? context = null) =>
            new(new StepsTenantRepository(Steps, Target), Modules, Changes, new StepsUnitOfWork(Steps), Audit,
                context ?? Context, new FixedOperatorTenant(Operator.Value), Reader, new FixedClock(Now),
                new ChangeTenantModulesValidator());

        public Task<OperatorTenantDetailDto> SendAsync(string reason, string? note, params (string? Key, string? Status)[] changes) =>
            Handler().HandleAsync(Command(Target.Id, reason, note, changes), TestContext.Current.CancellationToken);
    }

    private static ChangeTenantModulesCommand Command(
        TenantId target, string? reason, string? note, params (string? Key, string? Status)[] changes) =>
        new(Operator, target, changes.Select(change => new TenantModuleChangeInput(change.Key, change.Status)).ToArray(),
            reason, note);

    [Fact]
    public async Task ANonOperatorIsForbiddenBeforeTakingTheLockOrLookingUpTheTarget()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.Handler(new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(Command(TenantId.New(), "contract", null, ("pos", "active")), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Steps);
    }

    // §3: sin el candado antes de leer, dos operadores pasarían la validación a la vez (Review Focus 3).
    [Fact]
    public async Task TheLockIsTakenBeforeReadingTheState()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("cancellation", null, ("reporting", "inactive"));

        Assert.Equal($"lock:{fixture.Target.Id}", fixture.Steps[0]);
        Assert.Equal(["save", "commit"], fixture.Steps.TakeLast(2));
    }

    [Fact]
    public async Task AMissingTargetIsNotFoundAfterAuthorization()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.Handler().HandleAsync(Command(TenantId.New(), "contract", null, ("pos", "active")), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task ACascadeWritesRowsHistoryAndAuditWithOneBatchId()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("cancellation", "No lo usan por ahora",
            ("orders", "inactive"), ("customers", "inactive"), ("quotations", "inactive"));

        Assert.All(fixture.Modules.Rows.Where(row => row.ModuleKey == TenantModuleKeys.Customers
                || row.ModuleKey == TenantModuleKeys.Quotations || row.ModuleKey == TenantModuleKeys.Orders),
            row => Assert.Equal((TenantModuleStatus.Inactive, Now), (row.Status, row.StatusChangedAt)));
        Assert.Equal(3, fixture.Changes.Added.Count);
        Assert.Single(fixture.Changes.Added.Select(change => change.BatchId).Distinct());
        Assert.All(fixture.Changes.Added, change =>
            Assert.Equal(("active", "inactive", ChangeReason.Cancellation, "No lo usan por ahora", fixture.Context.SubjectId),
                (change.FromStatus, change.ToStatus, change.Reason, change.Note, change.ActorUserId)));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((fixture.Target.Id.Value, "tenancy.tenant_modules.changed", "tenant", fixture.Target.Id.Value.ToString()),
            (audit.TenantId, audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(
            ["customers:active->inactive", "quotations:active->inactive", "orders:active->inactive", "reason:cancellation"],
            audit.ChangedFields);
    }

    [Fact]
    public async Task ActivatingAKeyWithoutRowCreatesItFromTheConsole()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("contract", null, ("pos", "active"));

        var created = Assert.Single(fixture.Modules.Added);
        Assert.Equal((TenantModuleKeys.Pos, TenantModuleSources.Operator, TenantModuleStatus.Active, (string?)null),
            (created.ModuleKey, created.Source, created.Status, created.Note));
        Assert.Null(Assert.Single(fixture.Changes.Added).FromStatus);
        Assert.Equal(["pos:none->active", "reason:contract"], Assert.Single(fixture.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task AnInconsistentBatchWritesNothing()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            fixture.SendAsync("cancellation", null, ("customers", "inactive")));

        Assert.Equal("tenancy.modules.inconsistent_dependencies", error.Code);
        Assert.Empty(fixture.Changes.Added);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain("save", fixture.Steps);
        Assert.All(fixture.Modules.Rows, row => Assert.Equal(TenantModuleStatus.Active, row.Status));
    }

    // Review Focus 1 y 5: texto desconocido es 422 validation.failed, nunca el ArgumentException de Parse.
    public static TheoryData<string?, string?, string?, string?> InvalidShapes() => new()
    {
        { "POS", "active", "contract", null },
        { "inventory", "active", "contract", null },
        { "pos", "on", "contract", null },
        { "pos", "active", "refund", null },
        { "pos", "active", "Contract", null },
        { "pos", "active", "contract", new string('x', 301) },
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task AnInvalidShapeIsAValidationError(string? key, string? status, string? reason, string? note)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() =>
            fixture.Handler().HandleAsync(Command(fixture.Target.Id, reason, note, (key, status)), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("save", fixture.Steps);
    }

    [Fact]
    public async Task ANullItemOrAnEmptyOrOversizedBatchIsAValidationError()
    {
        var fixture = new Fixture();
        ChangeTenantModulesCommand[] commands =
        [
            new(Operator, fixture.Target.Id, [null!], "contract", null),   // [null] en el JSON llega así
            new(Operator, fixture.Target.Id, [], "contract", null),
            new(Operator, fixture.Target.Id, null, "contract", null),
            new(Operator, fixture.Target.Id, Enumerable.Range(0, 8).Select(_ => new TenantModuleChangeInput("pos", "active")).ToArray(), "contract", null),
        ];

        foreach (var command in commands)
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                fixture.Handler().HandleAsync(command, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ANoteOfExactlyThreeHundredPasses()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("contract", new string('x', 300), ("pos", "active"));

        Assert.Equal(300, Assert.Single(fixture.Changes.Added).Note!.Length);
    }
}
```

> En el auditor, una fila nueva se anota `pos:none->active` (sin fila = `none`, el mismo texto que el detalle).

En `OperatorConsoleApiTests.cs`:

```csharp
    [Fact]
    public async Task DeactivatingReportingWritesRowHistoryAndAuditAndMasksOnTheNextRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var operatorClient = OperatorClient(factory);
        using var member = StubClient(factory, ownerId, tenantId, "reporting.all_advisors.read", "tenancy.settings.read");
        Assert.Contains("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));

        var detail = await PostChangesAsync(operatorClient, tenantId, "cancellation", "No lo usan", ("reporting", "inactive"));

        var reporting = detail.Modules.Single(module => module.Key == "reporting");
        Assert.Equal(("inactive", false, "cancellation"), (reporting.Status, reporting.Enabled, reporting.LastReason));
        Assert.DoesNotContain("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var id = new TenantId(tenantId);
            var change = await dbContext.TenantChanges.AsNoTracking().SingleAsync(value => value.TenantId == id, TestContext.Current.CancellationToken);
            Assert.Equal(("active", "inactive", "No lo usan"), (change.FromStatus, change.ToStatus, change.Note));
            var audited = await dbContext.Database.SqlQuery<string>(
                $"SELECT changed_fields::text AS \"Value\" FROM audit.entries WHERE action = 'tenancy.tenant_modules.changed' AND tenant_id = {tenantId}")
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Contains("reporting:active->inactive", audited, StringComparison.Ordinal);
        }

        await PostChangesAsync(operatorClient, tenantId, "courtesy", null, ("reporting", "active"));
        Assert.Contains("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));
    }

    [Fact]
    public async Task AnInconsistentBatchIsRejectedAndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "customers", status = "inactive" } }, reason = "cancellation" },
                TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "tenancy.modules.inconsistent_dependencies");
        var detail = await GetOkAsync<DetailPayload>(client, Url($"tenants/{tenantId}"));
        Assert.All(detail.Modules.Where(module => module.Key != "pos"), module => Assert.Equal("active", module.Status));
    }

    [Fact]
    public async Task ActivatingPosCreatesTheRowFromTheConsole()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        var pos = (await PostChangesAsync(client, tenantId, "contract", null, ("pos", "active")))
            .Modules.Single(module => module.Key == "pos");

        Assert.Equal(("active", true, "operator"), (pos.Status, pos.Enabled, pos.Source));
    }

    [Theory]
    [InlineData("""{"changes":[{"key":"inventory","status":"active"}],"reason":"contract"}""")]
    [InlineData("""{"changes":[null],"reason":"contract"}""")]
    [InlineData("""{"changes":[{"key":"POS","status":"active"}],"reason":"contract"}""")]
    public async Task AnUnknownShapeIsValidationFailedNot500(string body)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        await AssertProblemAsync(
            await client.PostAsync(Url($"tenants/{tenantId}/modules/changes"), content, TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task ReadingIsNotEnoughToChangeModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read");

        var response = await client.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
            new { changes = new[] { new { key = "pos", status = "active" } }, reason = "contract" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Review Focus 3: A activa quotations y B desactiva customers a la vez. Cualquiera sea el orden,
    // uno gana y el otro choca con la regla; nunca queda quotations activo sin customers.
    [Fact]
    public async Task TwoConcurrentBatchesLeaveAConsistentState()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var first = OperatorClient(factory);
        using var second = OperatorClient(factory);
        await PostChangesAsync(first, tenantId, "cancellation", null, ("quotations", "inactive"), ("orders", "inactive"));

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "quotations", status = "active" } }, reason = "contract" }, TestContext.Current.CancellationToken),
            second.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "customers", status = "inactive" } }, reason = "cancellation" }, TestContext.Current.CancellationToken));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.UnprocessableEntity));
        var modules = (await GetOkAsync<DetailPayload>(first, Url($"tenants/{tenantId}"))).Modules.ToDictionary(module => module.Key);
        Assert.False(modules["quotations"].Status == "active" && modules["customers"].Status != "active");
    }

    private static async Task<DetailPayload> PostChangesAsync(
        HttpClient client, Guid tenantId, string reason, string? note, params (string Key, string Status)[] changes)
    {
        var response = await client.PostAsJsonAsync(
            Url($"tenants/{tenantId}/modules/changes"),
            new { changes = changes.Select(change => new { key = change.Key, status = change.Status }).ToArray(), reason, note },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<DetailPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }
```

(los usings de EF, DI, `Modules.Tenancy.Domain` y `Modules.Tenancy.Infrastructure.Persistence` ya los agregó la Task 7.)

- [ ] **Step 2: Run to verify RED**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ChangeTenantModulesHandlerTests"
```
Expected: compilación — `CS0246 'ChangeTenantModulesHandler'`, `'TenantModuleChangeInput'`…

- [ ] **Step 3: Implement**

`ChangeTenantModules.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>Un cambio pedido, como llega en el JSON. Texto a propósito: se valida antes de convertir.</summary>
public sealed record TenantModuleChangeInput(string? Key, string? Status);

public sealed record ChangeTenantModulesCommand(
    TenantId TenantId,
    TenantId TargetTenantId,
    IReadOnlyList<TenantModuleChangeInput>? Changes,
    string? Reason,
    string? Note) : ICommand<OperatorTenantDetailDto>;

/// <summary>Texto libre ⇒ validador, aunque el dominio valide (CLAUDE.md): el dominio da el código, el
/// validador da el campo. Las reglas de negocio (D2, consistencia) son códigos de dominio.</summary>
public sealed class ChangeTenantModulesValidator : AbstractValidator<ChangeTenantModulesCommand>
{
    public ChangeTenantModulesValidator()
    {
        RuleFor(command => command.Changes)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(changes => changes!.Count <= TenantModuleKeys.All.Count)
            .WithMessage("A batch has at most seven changes.");
        // NotNull: un [null] del JSON llega como elemento null aunque el tipo diga que no; ChildRules lo
        // saltaría y el Parse del handler daría 500 (Review Focus 1).
        RuleForEach(command => command.Changes)
            .NotNull()
            .ChildRules(change =>
            {
                change.RuleFor(item => item.Key).Must(OperatorInput.IsModuleKey).WithMessage("Unknown module key.");
                change.RuleFor(item => item.Status).Must(OperatorInput.IsDirection).WithMessage("Status must be 'active' or 'inactive'.");
            });
        RuleFor(command => command.Reason).Must(OperatorInput.IsReason).WithMessage("Unknown reason.");
        RuleFor(command => command.Note).MaximumLength(TenantChange.NoteMaxLength);
    }
}

public sealed class ChangeTenantModulesHandler(
    ITenantRepository tenantRepository,
    ITenantModuleRepository moduleRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IOperatorTenantReader reader,
    IClock clock,
    IValidator<ChangeTenantModulesCommand> validator)
    : ICommandHandler<ChangeTenantModulesCommand, OperatorTenantDetailDto>
{
    public async Task<OperatorTenantDetailDto> HandleAsync(ChangeTenantModulesCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, OperatorPermissions.ModulesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // §3 (D3): sin If-Match; el candado serializa las operaciones sobre el mismo tenant y se toma
        // antes de leer nada, el tenant incluido.
        await using (var scope = await unitOfWork.BeginTenantChangeScopeAsync(command.TargetTenantId, cancellationToken))
        {
            _ = await tenantRepository.GetAsync(command.TargetTenantId, cancellationToken)
                ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

            var rows = await moduleRepository.ListByTenantAsync(command.TargetTenantId, cancellationToken);
            var reason = TenantChangeVocabulary.ParseReason(command.Reason!);
            var requested = command.Changes!
                .Select(change => new RequestedModuleChange(
                    TenantModuleKey.Parse(change.Key!), TenantChangeVocabulary.ParseModuleStatus(change.Status!)))
                .ToArray();
            var plan = TenantModuleChangeBatch.Plan(rows.ToDictionary(row => row.ModuleKey, row => row.Status), requested, reason);

            var now = clock.UtcNow;
            var batchId = Guid.CreateVersion7();
            foreach (var change in plan)
            {
                var row = rows.SingleOrDefault(value => value.ModuleKey == change.Key);
                if (row is null)
                {
                    // §5: activar una clave sin fila la crea; la nota de creación queda vacía (decisión
                    // P8) porque el motivo y la nota viven en el historial.
                    moduleRepository.Add(TenantModule.Create(
                        command.TargetTenantId, change.Key, TenantModuleSources.Operator, now, note: null));
                }
                else if (change.To == TenantModuleStatus.Active)
                {
                    row.Activate(now);
                }
                else
                {
                    row.Deactivate(now);
                }

                changeRepository.Add(TenantChange.ForModule(
                    command.TargetTenantId, batchId, change.Key, change.From, change.To, reason, command.Note,
                    executionContext.SubjectId, now));
            }

            // §5: tenantId = el destino; un string por cambio más el motivo.
            auditRecorder.Record(
                command.TargetTenantId.Value,
                executionContext.SubjectId,
                "tenancy.tenant_modules.changed",
                "tenant",
                command.TargetTenantId.Value.ToString(),
                "success",
                plan.Select(change =>
                        $"{change.Key.Value}:{(change.From is { } from ? TenantChangeVocabulary.ToText(from) : OperatorTenantModuleDto.NoRowStatus)}->{TenantChangeVocabulary.ToText(change.To)}")
                    .Append($"reason:{TenantChangeVocabulary.ToText(reason)}")
                    .ToArray(),
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        // Después del commit: el detalle actualizado es la respuesta (la SPA reemplaza su caché con él).
        return await OperatorTenantDetails.LoadAsync(reader, operatorTenant, command.TargetTenantId, cancellationToken);
    }
}
```

`OperatorEndpoints.cs` — ruta nueva dentro de `MapOperatorEndpoints`, su handler y el request:

```csharp
        group.MapPost("/tenants/{targetTenantId:guid}/modules/changes", ChangeModulesAsync)
            .RequireAuthorization(OperatorPermissions.ModulesManage)
            .Accepts<ChangeTenantModulesRequest>("application/json")
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
// …
    private static async Task<IResult> ChangeModulesAsync(
        Guid tenantId,
        Guid targetTenantId,
        ChangeTenantModulesRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new ChangeTenantModulesCommand(
                new TenantId(tenantId), new TenantId(targetTenantId), request.Changes, request.Reason, request.Note),
            cancellationToken);
        return Results.Ok(result);
    }

/// <summary>Spec 2026-10-08 §5: todo nullable, para que una forma mala sea 422 del validador y no un 400
/// del binder.</summary>
public sealed record ChangeTenantModulesRequest(
    IReadOnlyList<TenantModuleChangeInput>? Changes, string? Reason, string? Note);
```

`QepServiceCollectionExtensions.cs`:

```csharp
        services.AddScoped<
            ICommandHandler<ChangeTenantModulesCommand, OperatorTenantDetailDto>,
            ChangeTenantModulesHandler>();
```

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ChangeTenantModulesHandlerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ChangeTenantModulesHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(tenancy): la consola activa y desactiva módulos por lotes atómicos"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 10: `POST …/status` — inactivar y reactivar un tenant

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantStatus.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Api/IfMatchHeader.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs:76,98,113,121-131,148-164` (usa `IfMatchHeader`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ChangeTenantStatusHandlerTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`

**Interfaces:**
- Consumes: `Tenant.Suspend/Reactivate`, `TenantChange.ForTenantStatus` (4); `ITenantChangeRepository` (6); `OperatorAuthorization`, `OperatorTenantDetails` (8); dobles de la Task 9.
- Produces: `ChangeTenantStatusCommand(TenantId TenantId, TenantId TargetTenantId, string? Status, string? Reason, string? Note, long ExpectedVersion) : ICommand<OperatorTenantDetailDto>`, `ChangeTenantStatusValidator`, `ChangeTenantStatusHandler`, `ChangeTenantStatusRequest(string? Status, string? Reason, string? Note)`, `internal static class IfMatchHeader { static long RequireVersion(HttpContext) }`.

- [ ] **Step 1: Write the failing tests**

`ChangeTenantStatusHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4 y §5, <c>POST …/status</c>.</summary>
public sealed class ChangeTenantStatusHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public List<string> Steps { get; } = [];
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();
        public FixedOperatorTenantReader Reader { get; } = new();
        public OperatorContext Context { get; } = new(Operator, OperatorPermissions.TenantsManage);

        public Fixture() => Reader.Snapshots[Target.Id] = OperatorSnapshots.Of(Target.Id, OperatorSnapshots.Signup());

        public ChangeTenantStatusHandler Handler(IExecutionContext? context = null) =>
            new(new StepsTenantRepository(Steps, Target), Changes, new StepsUnitOfWork(Steps), Audit, context ?? Context,
                new FixedOperatorTenant(Operator.Value), Reader, new FixedClock(Now), new ChangeTenantStatusValidator());

        public Task<OperatorTenantDetailDto> SendAsync(string? status, string? reason, long version = 1, TenantId? target = null) =>
            Handler().HandleAsync(new ChangeTenantStatusCommand(Operator, target ?? Target.Id, status, reason, "Factura de septiembre", version),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SuspendingWritesHistoryAndAuditAndBumpsTheVersion()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("inactive", "nonpayment");

        Assert.Equal((TenantStatus.Suspended, 2L), (fixture.Target.Status, fixture.Target.Version));
        var change = Assert.Single(fixture.Changes.Added);
        Assert.Equal((TenantChangeKind.TenantStatus, "Active", "Suspended", ChangeReason.Nonpayment, "Factura de septiembre"),
            (change.Kind, change.FromStatus, change.ToStatus, change.Reason, change.Note));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((fixture.Target.Id.Value, "tenancy.tenant.status_changed", "tenant"), (audit.TenantId, audit.Action, audit.ResourceType));
        Assert.Equal(["status:Active->Suspended", "reason:nonpayment"], audit.ChangedFields);
        Assert.Contains("save", fixture.Steps);
    }

    [Fact]
    public async Task ReactivatingReturnsToActive()
    {
        var fixture = new Fixture();
        fixture.Target.Suspend(ChangeReason.Nonpayment, Now);

        await fixture.SendAsync("active", "contract", version: 2);

        Assert.Equal(TenantStatus.Active, fixture.Target.Status);
        Assert.Equal("Active", Assert.Single(fixture.Changes.Added).ToStatus);
    }

    // §4: antes de tocar el agregado; el dominio no conoce la configuración.
    [Fact]
    public async Task TheOperatorTenantCannotBeSuspended()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("inactive", "nonpayment", target: Operator));

        Assert.Equal("tenancy.tenant.operator_cannot_be_suspended", error.Code);
        Assert.Empty(fixture.Steps);
    }

    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => fixture.SendAsync("inactive", "nonpayment", version: 7));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(fixture.Changes.Added);
    }

    [Fact]
    public async Task ANonOperatorIsForbiddenBeforeLookingUpTheTarget()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.Handler(new OperatorContext(Operator, OperatorPermissions.ModulesManage))
                .HandleAsync(new ChangeTenantStatusCommand(Operator, TenantId.New(), "inactive", "nonpayment", null, 1),
                    TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Steps);
    }

    [Theory]
    [InlineData("paused", "nonpayment")]
    [InlineData("Inactive", "nonpayment")]
    [InlineData("inactive", "refund")]
    public async Task AnUnknownStatusOrReasonIsAValidationError(string status, string reason) =>
        await Assert.ThrowsAsync<ValidationException>(() => new Fixture().SendAsync(status, reason));

    [Fact]
    public async Task AReasonOfTheOtherDirectionIsADomainError() =>
        Assert.Equal("tenancy.tenant.reason_not_allowed",
            (await Assert.ThrowsAsync<TenantDomainException>(() => new Fixture().SendAsync("inactive", "contract"))).Code);
}
```

En `OperatorConsoleApiTests.cs`:

```csharp
    [Fact]
    public async Task SuspendingRequiresIfMatchAndAFreshVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(await PostStatusAsync(client, tenantId, null, "inactive", "nonpayment"),
            (HttpStatusCode)428, "precondition.if_match_required");
        await AssertProblemAsync(await PostStatusAsync(client, tenantId, "\"9\"", "inactive", "nonpayment"),
            HttpStatusCode.PreconditionFailed, "concurrency.conflict");
    }

    // §4 y criterio 5: inactivar corta todo, reactivar deja los módulos como estaban (D8).
    [Fact]
    public async Task SuspendingCutsTheTenantAndReactivatingKeepsItsModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);
        using var member = StubClient(factory, ownerId, tenantId, "tenancy.settings.read");

        var suspended = await ReadDetailAsync(await PostStatusAsync(client, tenantId, "\"1\"", "inactive", "nonpayment"));
        Assert.Equal(("Suspended", 2L, "nonpayment"), (suspended.Status, suspended.Version, suspended.StatusReason));
        Assert.NotNull(suspended.StatusChangedAt);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync($"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, (await GetOkAsync<TenantPagePayload>(client, Url("tenants"))).Summary.Inactive);

        var reactivated = await ReadDetailAsync(await PostStatusAsync(client, tenantId, "\"2\"", "active", "correction"));
        Assert.Equal(("Active", 3L), (reactivated.Status, reactivated.Version));
        Assert.Equal(6, reactivated.Modules.Count(module => module.Status == "active"));
        Assert.Contains("tenancy.settings.read", await EffectivePermissionsAsync(member, tenantId));
    }

    [Fact]
    public async Task TheOperatorTenantCannotBeSuspended()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(await PostStatusAsync(client, OperatorTenantId, "\"1\"", "inactive", "nonpayment"),
            HttpStatusCode.UnprocessableEntity, "tenancy.tenant.operator_cannot_be_suspended");
    }

    private static async Task<HttpResponseMessage> PostStatusAsync(
        HttpClient client, Guid tenantId, string? ifMatch, string status, string reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"tenants/{tenantId}/status"))
        {
            Content = JsonContent.Create(new { status, reason, note = (string?)null }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<DetailPayload> ReadDetailAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<DetailPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }
```

- [ ] **Step 2: Run to verify RED**

`dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ChangeTenantStatusHandlerTests"` → `CS0246 'ChangeTenantStatusHandler'`.

- [ ] **Step 3: Implement**

`ChangeTenantStatus.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="Status"><c>inactive</c> inactiva (Suspend), <c>active</c> reactiva (decisión P12 del plan).</param>
/// <param name="ExpectedVersion">El If-Match: el tenant ya tiene versión de agregado (D3).</param>
public sealed record ChangeTenantStatusCommand(
    TenantId TenantId,
    TenantId TargetTenantId,
    string? Status,
    string? Reason,
    string? Note,
    long ExpectedVersion) : ICommand<OperatorTenantDetailDto>;

public sealed class ChangeTenantStatusValidator : AbstractValidator<ChangeTenantStatusCommand>
{
    public ChangeTenantStatusValidator()
    {
        RuleFor(command => command.Status).Must(OperatorInput.IsDirection).WithMessage("Status must be 'active' or 'inactive'.");
        RuleFor(command => command.Reason).Must(OperatorInput.IsReason).WithMessage("Unknown reason.");
        RuleFor(command => command.Note).MaximumLength(TenantChange.NoteMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class ChangeTenantStatusHandler(
    ITenantRepository tenantRepository,
    ITenantChangeRepository changeRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IOperatorTenantReader reader,
    IClock clock,
    IValidator<ChangeTenantStatusCommand> validator)
    : ICommandHandler<ChangeTenantStatusCommand, OperatorTenantDetailDto>
{
    public async Task<OperatorTenantDetailDto> HandleAsync(ChangeTenantStatusCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, OperatorPermissions.TenantsManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var suspend = TenantChangeVocabulary.ParseModuleStatus(command.Status!) == TenantModuleStatus.Inactive;
        // §4: antes de tocar el agregado; el dominio no conoce la configuración.
        if (suspend && operatorTenant.IsOperator(command.TargetTenantId.Value))
        {
            throw new TenantDomainException(
                "tenancy.tenant.operator_cannot_be_suspended",
                "The operator tenant cannot be suspended.");
        }

        var tenant = await tenantRepository.GetAsync(command.TargetTenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");
        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The tenant changed after it was loaded.");
        }

        var reason = TenantChangeVocabulary.ParseReason(command.Reason!);
        var now = clock.UtcNow;
        var from = tenant.Status;
        if (suspend)
        {
            tenant.Suspend(reason, now);
        }
        else
        {
            tenant.Reactivate(reason, now);
        }

        changeRepository.Add(TenantChange.ForTenantStatus(
            tenant.Id, Guid.CreateVersion7(), from, tenant.Status, reason, command.Note, executionContext.SubjectId, now));
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.tenant.status_changed",
            "tenant",
            tenant.Id.Value.ToString(),
            "success",
            [$"status:{from}->{tenant.Status}", $"reason:{TenantChangeVocabulary.ToText(reason)}"],
            now);
        // Version es token de concurrencia: un choque entre la lectura y el SaveChanges sale 412 por
        // TenancyUnitOfWork (concurrency.conflict).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OperatorTenantDetails.LoadAsync(reader, operatorTenant, tenant.Id, cancellationToken);
    }
}
```

`IfMatchHeader.cs` — sale tal cual de `TenantSettingsEndpoints.cs:121-131` y `:148-164` (mismo formato `"7"` que la SPA ya manda, `tenant-settings.api.ts:43`):

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Modules.Tenancy.Api;

/// <summary>El If-Match obligatorio de Tenancy: ajustes, logo y estado del tenant. 428 sin él.</summary>
internal static class IfMatchHeader
{
    public static long RequireVersion(HttpContext httpContext)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded version is required.");
        }

        return expectedVersion;
    }

    private static bool TryParseVersion(string? etag, out long version)
    { /* cuerpo de TenantSettingsEndpoints.cs:148-164, sin cambios */ }
}
```

En `TenantSettingsEndpoints.cs`, las tres llamadas a `RequireIfMatch(httpContext)` pasan a `IfMatchHeader.RequireVersion(httpContext)` y se borran `RequireIfMatch` y `TryParseVersion`.

`OperatorEndpoints.cs`:

```csharp
        group.MapPost("/tenants/{targetTenantId:guid}/status", ChangeStatusAsync)
            .RequireAuthorization(OperatorPermissions.TenantsManage)
            .Accepts<ChangeTenantStatusRequest>("application/json")
            .Produces<OperatorTenantDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
// …
    private static async Task<IResult> ChangeStatusAsync(
        Guid tenantId,
        Guid targetTenantId,
        ChangeTenantStatusRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = IfMatchHeader.RequireVersion(httpContext);
        var result = await dispatcher.SendAsync(
            new ChangeTenantStatusCommand(
                new TenantId(tenantId), new TenantId(targetTenantId), request.Status, request.Reason, request.Note, expectedVersion),
            cancellationToken);
        return Results.Ok(result);
    }

public sealed record ChangeTenantStatusRequest(string? Status, string? Reason, string? Note);
```

`QepServiceCollectionExtensions.cs`: `ICommandHandler<ChangeTenantStatusCommand, OperatorTenantDetailDto>` → `ChangeTenantStatusHandler`.

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ChangeTenantStatusHandlerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~TenantSettingsApiTests|FullyQualifiedName~TenantLogoApiTests"
```
Expected: PASS (`TenantSettingsApiTests` y `TenantLogoApiTests` prueban que el `If-Match` refactorizado sigue igual).

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantStatus.cs src/Modules/Tenancy/Modules.Tenancy.Api/IfMatchHeader.cs src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ChangeTenantStatusHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(tenancy): la consola inactiva y reactiva tenants con If-Match"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 11: Historial — `GET …/history`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenantReader.cs` (método y registro nuevos)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs` (DTOs del historial)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ListTenantHistory.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs` (`FixedOperatorTenantReader.ListHistoryAsync`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListTenantHistoryHandlerTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`

**Interfaces:**
- Consumes: `TenantChange` (4), `TenantChanges` (5), `ITenantDirectory.GetStatusAsync` (7, para el 404), `OperatorAuthorization`/`OperatorInput` (8), `IUserDirectory.GetEmailAsync` (`Modules.Identity.Application`, precedente `ListMemberships.cs:102,148`).
- Produces: `IOperatorTenantReader.ListHistoryAsync(TenantId tenantId, TenantModuleKey? module, int page, int pageSize, CancellationToken) → Task<TenantChangeBatchPage>`; `TenantChangeBatchPage(IReadOnlyList<IReadOnlyList<TenantChange>> Batches, int Total)` (lotes en el orden de la página; dentro de un lote, en el orden de `TenantModuleKeys.All`); `OperatorHistoryPageDto(IReadOnlyList<OperatorHistoryBatchDto> Items, int Total, int Page, int PageSize)`, `OperatorHistoryBatchDto(Guid BatchId, string Kind, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorEmail, string Reason, string? Note, IReadOnlyList<OperatorHistoryChangeDto> Changes)`, `OperatorHistoryChangeDto(string? ModuleKey, string? FromStatus, string ToStatus)`; `ListTenantHistoryQuery(TenantId TenantId, TenantId TargetTenantId, string? Module, int Page, int PageSize) : IQuery<OperatorHistoryPageDto>` + validador + handler.

- [ ] **Step 1: Write the failing tests**

En `FixedOperatorTenantReader` (doble):

```csharp
    public TenantChangeBatchPage History { get; set; } = new([], 0);

    public Task<TenantChangeBatchPage> ListHistoryAsync(
        TenantId tenantId, TenantModuleKey? module, int page, int pageSize, CancellationToken cancellationToken)
    {
        Asked.Add($"history:{module?.Value}:{page}:{pageSize}");
        return Task.FromResult(History);
    }
```

`ListTenantHistoryHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5, <c>GET …/history</c>.</summary>
public sealed class ListTenantHistoryHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Target = TenantId.New();
    private static readonly Guid Andres = Guid.CreateVersion7();
    private static readonly Guid Gone = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static ListTenantHistoryHandler Handler(FixedOperatorTenantReader reader, CountingUserDirectory users, TenantStatus? targetStatus = TenantStatus.Active) =>
        new(reader, new FixedTenantDirectory(targetStatus), users,
            new OperatorContext(Operator, OperatorPermissions.TenantsRead), new FixedOperatorTenant(Operator.Value),
            new ListTenantHistoryValidator());

    [Fact]
    public async Task BatchesKeepTheirChangesAndEachActorIsResolvedOnce()
    {
        var cascade = Guid.CreateVersion7();
        var reader = new FixedOperatorTenantReader
        {
            History = new(
            [
                [
                    TenantChange.ForModule(Target, cascade, TenantModuleKeys.Customers, TenantModuleStatus.Active, TenantModuleStatus.Inactive, ChangeReason.Cancellation, "nota", Andres, At),
                    TenantChange.ForModule(Target, cascade, TenantModuleKeys.Quotations, TenantModuleStatus.Active, TenantModuleStatus.Inactive, ChangeReason.Cancellation, "nota", Andres, At),
                ],
                [TenantChange.ForTenantStatus(Target, Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended, ChangeReason.Nonpayment, null, Andres, At.AddDays(-1))],
                [TenantChange.ForModule(Target, Guid.CreateVersion7(), TenantModuleKeys.Pos, null, TenantModuleStatus.Active, ChangeReason.Courtesy, null, Gone, At.AddDays(-2))],
            ], Total: 3),
        };
        var users = new CountingUserDirectory(new Dictionary<Guid, string> { [Andres] = "andres@qcode.co" });

        var page = await Handler(reader, users).HandleAsync(
            new ListTenantHistoryQuery(Operator, Target, null, 1, 25), TestContext.Current.CancellationToken);

        Assert.Equal((3, 1, 25), (page.Total, page.Page, page.PageSize));
        var first = page.Items[0];
        Assert.Equal((cascade, "module", "cancellation", "nota", "andres@qcode.co"), (first.BatchId, first.Kind, first.Reason, first.Note, first.ActorEmail));
        Assert.Equal([new OperatorHistoryChangeDto("customers", "active", "inactive"), new OperatorHistoryChangeDto("quotations", "active", "inactive")], first.Changes);
        Assert.Equal(("tenant_status", new OperatorHistoryChangeDto(null, "Active", "Suspended")), (page.Items[1].Kind, Assert.Single(page.Items[1].Changes)));
        Assert.Null(page.Items[2].ActorEmail);                     // usuario que ya no existe
        Assert.Null(Assert.Single(page.Items[2].Changes).FromStatus);
        Assert.Equal(2, users.Calls);                              // deduplicado por página
    }

    [Fact]
    public async Task TheModuleFilterReachesTheReaderTyped()
    {
        var reader = new FixedOperatorTenantReader();

        await Handler(reader, new CountingUserDirectory(new Dictionary<Guid, string>())).HandleAsync(
            new ListTenantHistoryQuery(Operator, Target, "reporting", 2, 10), TestContext.Current.CancellationToken);

        Assert.Equal(["history:reporting:2:10"], reader.Asked);
    }

    [Theory]
    [InlineData("inventory", 1, 25)]
    [InlineData("REPORTING", 1, 25)]
    [InlineData(null, 0, 25)]
    [InlineData(null, 1, 101)]
    public async Task AnInvalidQueryIsAValidationError(string? module, int page, int pageSize) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Handler(new FixedOperatorTenantReader(), new CountingUserDirectory(new Dictionary<Guid, string>()))
                .HandleAsync(new ListTenantHistoryQuery(Operator, Target, module, page, pageSize), TestContext.Current.CancellationToken));

    [Fact]
    public async Task AMissingTargetIsNotFound() =>
        Assert.Equal("tenancy.tenant.not_found", (await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(new FixedOperatorTenantReader(), new CountingUserDirectory(new Dictionary<Guid, string>()), targetStatus: null)
                .HandleAsync(new ListTenantHistoryQuery(Operator, Target, null, 1, 25), TestContext.Current.CancellationToken))).Code);

    private sealed class CountingUserDirectory(IReadOnlyDictionary<Guid, string> emails) : IUserDirectory
    {
        public int Calls { get; private set; }

        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(emails.GetValueOrDefault(userId));
        }
    }
}
```

En `OperatorConsoleApiTests.cs`:

```csharp
    [Fact]
    public async Task TheHistoryGroupsByBatchNewestFirstAndFiltersByModule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        var (_, actorId) = await RegisterAsync(factory);   // un usuario que existe, para resolver su correo
        using var client = OperatorClient(factory, actorId);

        await PostChangesAsync(client, tenantId, "cancellation", null, ("customers", "inactive"), ("quotations", "inactive"), ("orders", "inactive"));
        await PostChangesAsync(client, tenantId, "courtesy", "Prueba de un mes", ("pos", "active"));
        await ReadDetailAsync(await PostStatusAsync(client, tenantId, "\"1\"", "inactive", "nonpayment"));

        var all = await GetOkAsync<HistoryPayload>(client, Url($"tenants/{tenantId}/history"));
        Assert.Equal(3, all.Total);
        Assert.Equal(["tenant_status", "module", "module"], all.Items.Select(batch => batch.Kind));
        Assert.Equal(["customers", "quotations", "orders"], all.Items[2].Changes.Select(change => change.ModuleKey));
        Assert.NotNull(all.Items[0].ActorEmail);
        Assert.Equal(actorId, all.Items[0].ActorUserId);

        var pos = await GetOkAsync<HistoryPayload>(client, Url($"tenants/{tenantId}/history?module=pos"));
        Assert.Equal(("Prueba de un mes", "courtesy"), (Assert.Single(pos.Items).Note, pos.Items[0].Reason));

        // Review Focus 4: más allá de la última página.
        var beyond = await GetOkAsync<HistoryPayload>(client, Url($"tenants/{tenantId}/history?page=4&pageSize=1"));
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.Total);
    }

    [Fact]
    public async Task AnUnknownModuleFilterIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync(Url($"tenants/{tenantId}/history?module=inventory"), TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    private sealed record HistoryPayload(List<BatchPayload> Items, int Total, int Page, int PageSize);
    private sealed record BatchPayload(Guid BatchId, string Kind, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorEmail, string Reason, string? Note, List<ChangePayload> Changes);
    private sealed record ChangePayload(string? ModuleKey, string? FromStatus, string ToStatus);
```

- [ ] **Step 2: Run to verify RED**

`dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ListTenantHistoryHandlerTests"` → `CS0246 'TenantChangeBatchPage'`, `'ListTenantHistoryHandler'`.

- [ ] **Step 3: Implement**

DTOs en `OperatorDtos.cs`:

```csharp
/// <summary>Lotes del más reciente al más antiguo, paginados <b>por lote</b> y no por fila: la cascada de
/// una operación se dibuja junta. El historial es inmutable: no hay endpoint para editarlo ni borrarlo.</summary>
public sealed record OperatorHistoryPageDto(IReadOnlyList<OperatorHistoryBatchDto> Items, int Total, int Page, int PageSize);

/// <param name="Kind"><c>module</c> o <c>tenant_status</c>, el mismo texto que la columna.</param>
/// <param name="ActorEmail">Resuelto por Identity; null si el usuario ya no existe y la UI muestra el id corto.</param>
public sealed record OperatorHistoryBatchDto(
    Guid BatchId, string Kind, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorEmail, string Reason,
    string? Note, IReadOnlyList<OperatorHistoryChangeDto> Changes);

/// <param name="ModuleKey">null en un cambio de estado del tenant.</param>
/// <param name="FromStatus">null = la fila del módulo no existía.</param>
public sealed record OperatorHistoryChangeDto(string? ModuleKey, string? FromStatus, string ToStatus);
```

`IOperatorTenantReader.cs`: el método de la sección «Interfaces» y `public sealed record TenantChangeBatchPage(IReadOnlyList<IReadOnlyList<TenantChange>> Batches, int Total);`.

`OperatorTenantReader.ListHistoryAsync`:

```csharp
    public async Task<TenantChangeBatchPage> ListHistoryAsync(
        TenantId tenantId, TenantModuleKey? module, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = dbContext.TenantChanges.AsNoTracking().Where(change => change.TenantId == tenantId);
        // §5: con el filtro, los lotes que tocan ese módulo (con todos sus cambios) y ninguno de estado.
        var scoped = module is null
            ? rows
            : rows.Where(change => change.Kind == TenantChangeKind.Module && change.ModuleKey == module);
        var batches = scoped
            .GroupBy(change => change.BatchId)
            .Select(group => new { BatchId = group.Key, OccurredAt = group.Max(change => change.OccurredAt) });

        var total = await batches.CountAsync(cancellationToken);
        var pageIds = await batches
            .OrderByDescending(batch => batch.OccurredAt)
            .ThenByDescending(batch => batch.BatchId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(batch => batch.BatchId)
            .ToListAsync(cancellationToken);
        var changes = await rows.Where(change => pageIds.Contains(change.BatchId)).ToListAsync(cancellationToken);

        var byBatch = changes.ToLookup(change => change.BatchId);
        return new TenantChangeBatchPage(
            pageIds
                .Select(id => (IReadOnlyList<TenantChange>)byBatch[id]
                    .OrderBy(change => change.ModuleKey is null ? -1 : IndexOf(change.ModuleKey))
                    .ToArray())
                .ToArray(),
            total);
    }

    private static int IndexOf(TenantModuleKey key)
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            if (TenantModuleKeys.All[index] == key)
            {
                return index;
            }
        }

        return int.MaxValue;
    }
```

`ListTenantHistory.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record ListTenantHistoryQuery(TenantId TenantId, TenantId TargetTenantId, string? Module, int Page, int PageSize)
    : IQuery<OperatorHistoryPageDto>;

public sealed class ListTenantHistoryValidator : AbstractValidator<ListTenantHistoryQuery>
{
    public ListTenantHistoryValidator()
    {
        RuleFor(query => query.Module)
            .Must(OperatorInput.IsModuleKey).When(query => query.Module is not null)
            .WithMessage("Unknown module key.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, OperatorInput.MaxPageSize);
    }
}

public sealed class ListTenantHistoryHandler(
    IOperatorTenantReader reader,
    ITenantDirectory tenantDirectory,
    IUserDirectory userDirectory,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListTenantHistoryQuery> validator)
    : IQueryHandler<ListTenantHistoryQuery, OperatorHistoryPageDto>
{
    public async Task<OperatorHistoryPageDto> HandleAsync(ListTenantHistoryQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        _ = await tenantDirectory.GetStatusAsync(query.TargetTenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

        var module = query.Module is null ? null : TenantModuleKey.Parse(query.Module);
        var history = await reader.ListHistoryAsync(query.TargetTenantId, module, query.Page, query.PageSize, cancellationToken);

        // Directo desde Tenancy.Application, que ya referencia Identity.Application (ListMemberships.cs:102),
        // y una consulta por actor de la página, no por lote.
        var emails = new Dictionary<Guid, string?>();
        foreach (var actor in history.Batches.Select(batch => batch[0].ActorUserId).Distinct())
        {
            emails[actor] = await userDirectory.GetEmailAsync(actor, cancellationToken);
        }

        var items = history.Batches
            .Select(batch =>
            {
                var head = batch[0];
                return new OperatorHistoryBatchDto(
                    head.BatchId, TenantChangeVocabulary.ToText(head.Kind), head.OccurredAt, head.ActorUserId,
                    emails[head.ActorUserId], TenantChangeVocabulary.ToText(head.Reason), head.Note,
                    batch.Select(change => new OperatorHistoryChangeDto(change.ModuleKey?.Value, change.FromStatus, change.ToStatus)).ToArray());
            })
            .ToArray();
        return new OperatorHistoryPageDto(items, history.Total, query.Page, query.PageSize);
    }
}
```

`OperatorEndpoints.cs`:

```csharp
        group.MapGet("/tenants/{targetTenantId:guid}/history", ListHistoryAsync)
            .RequireAuthorization(OperatorPermissions.TenantsRead)
            .Produces<OperatorHistoryPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
// …
    private static async Task<IResult> ListHistoryAsync(
        Guid tenantId,
        Guid targetTenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? module = null,
        int page = 1,
        int pageSize = 25)
    {
        var result = await dispatcher.QueryAsync(
            new ListTenantHistoryQuery(new TenantId(tenantId), new TenantId(targetTenantId), module, page, pageSize),
            cancellationToken);
        return Results.Ok(result);
    }
```

`QepServiceCollectionExtensions.cs`: `IQueryHandler<ListTenantHistoryQuery, OperatorHistoryPageDto>` → `ListTenantHistoryHandler`.

- [ ] **Step 4: Run to verify GREEN**

```
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~ListTenantHistoryHandlerTests|FullyQualifiedName~OperatorReadHandlerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~OperatorConsoleApiTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add src/Modules/Tenancy/Modules.Tenancy.Application/IOperatorTenantReader.cs src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs src/Modules/Tenancy/Modules.Tenancy.Application/ListTenantHistory.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListTenantHistoryHandlerTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs
git commit -m "feat(tenancy): historial de la consola agrupado por lote"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

---

### Task 12: README y verificación completa

**Files:**
- Modify: `README.md` — § «Configuración» (`:79-100`, lista de validadores con `ValidateOnStart`), § «Módulos por tenant» (`:612-656`), tabla de § «API implementada» (`:670-683`).

- [ ] **Step 1: README**

1. § «Configuración»: agregar `OperatorTenantOptionsValidator` a la lista de validadores (`:93-99`).
2. § «Módulos por tenant», reescrito según §7 del spec:
   - «La presencia de la fila es el módulo» pasa a «la fila **activa** es el módulo» (`status = 'active'`); una fila `inactive` conserva quién y cuándo.
   - «No hay endpoint de administración. QCode lo hace por SQL.» se reemplaza por: la vía normal es la **consola de operador** (sección «Plataforma» de la SPA) para el tenant configurado en `Platform:OperatorTenantId`; el SQL queda de respaldo **y no deja historial**.
   - Nueva subsección «Tenant operador» que documenta `Platform:OperatorTenantId`: opcional en todo ambiente; sin él no hay consola (403 para todos) y en producción hay advertencia al arrancar; `Guid.Empty` tumba el arranque; **no** va en `appsettings.json` ni como marcador en el ConfigMap (el CI despliega `main` sin pruebas). Cómo habilitarla (pasos 1–4 de «Despliegue» del spec), con el id por SQL: `SELECT id FROM tenancy.tenants WHERE slug = '<slug de QCode>';` y `Platform__OperatorTenantId` en el ConfigMap + reinicio.
   - Inactivar un tenant: sale del selector de sesión y todo request recibe 403, incluso con sesión abierta; reactivarlo deja todo como estaba. Workers, outbox y enlaces públicos siguen funcionando (DECISIÓN-PENDIENTE del spec).
   - Los tres comandos de respaldo pasan a:

```powershell
# Ver los módulos de un tenant
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT module_key, status, status_changed_at, source, enabled_at, note FROM tenancy.tenant_modules m JOIN tenancy.tenants t ON t.id = m.tenant_id WHERE t.slug = 'origen-botanico' ORDER BY module_key;"

# Prender pos (no deja historial)
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note, status, status_changed_at) SELECT id, 'pos', now(), 'manual', 'Activado por QCode', 'active', now() FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now();"

# Apagar orders (no deja historial)
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "UPDATE tenancy.tenant_modules m SET status = 'inactive', status_changed_at = now() FROM tenancy.tenants t WHERE t.id = m.tenant_id AND t.slug = 'origen-botanico' AND m.module_key = 'orders';"
```

   - El bloque «Después de desplegar `AddTenantModules`» se conserva (su `INSERT … ON CONFLICT DO NOTHING` sigue funcionando por el `DEFAULT`).
   - Nota de rollback: el `Down` de `AddOperatorConsole` borra las filas inactivas, pasa `operator` a `manual` y **devuelve el acceso a los tenants `Suspended`**.
3. Tabla de § «API implementada»: una fila

```markdown
| `/api/v1/tenants/{tenantId}/operator/tenants`      | `GET`, y por tenant `GET`, `modules/changes` (`POST`), `status` (`POST`, `If-Match`), `history` (`GET`) | `operator.tenants.read` / `operator.modules.manage` / `operator.tenants.manage`, sólo en el tenant operador |
```

   y bajo la tabla: «Excepción: en `/operator/*` un `targetTenantId` inexistente es **404** (el operador ve todos los tenants; D7).»

Chequeo de que el README quedó al día: `rg -n "DELETE FROM tenancy.tenant_modules|No hay endpoint de administración|la presencia de la fila es el módulo" README.md` → sin salida. `rg -n "OperatorTenantId" README.md` → al menos la subsección y el validador.

- [ ] **Step 2: Formato de lo tocado** (no del repo: la base tiene ~90k `ENDOFLINE`; `dotnet format` quita el BOM, por eso sólo `--verify-no-changes`)

```powershell
Set-Location $B
$files = @(git diff --name-only develop -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-consola-format"
Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
dotnet format Backend.slnx --verify-no-changes --no-restore --include $files --report $report
$reportFile = Join-Path $report "format-report.json"
if (Test-Path $reportFile) {
    (Get-Content $reportFile -Raw | ConvertFrom-Json) | ForEach-Object {
        $document = $_
        $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
            ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
    }
}
```
Expected: sin salida en el último bloque. Si hay hallazgos, corregirlos a mano en los archivos nombrados.

- [ ] **Step 3: Restore y build** (README «Verificación»; detener antes la API de este worktree)

```
dotnet restore --locked-mode
dotnet build --no-restore
```
Expected: `Build succeeded`, 0 warnings, 0 errors. `--locked-mode` en verde prueba que ningún `packages.lock.json` quedó desactualizado.

- [ ] **Step 4: Suite completa, un proyecto por comando, en primer plano**

```
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
dotnet test tests/Modules/Audit/Modules.Audit.UnitTests --no-build
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --no-build
dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --no-build
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build
dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --no-build
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build
dotnet test tests/Modules/Platform/Modules.Platform.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build
dotnet test tests/Modules/Audit/Modules.Audit.IntegrationTests --no-build
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --no-build
dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --no-build
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build
dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --no-build
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-build
```

Expected: todo verde. Las suites de integración son lentas (Docker, y otra sesión corriendo en paralelo). Si algo falla **fuera** de lo que este plan tocó, comparar **por nombre de prueba** contra `develop` antes de llamarlo regresión: hay fallas previas conocidas (p. ej. dos de `OrderExportApiTests` por el NIT desde `6f8aa75`). Lo que falle en Tenancy, Authorization, Bootstrapper o ArchitectureTests es de esta rama hasta que se demuestre lo contrario. Anotar en el handoff el resumen literal de cada proyecto.

- [ ] **Step 5: Commit del README**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/consola-operador
test "$(git branch --show-current)" = "feature/consola-operador" || exit 1
git add README.md
git commit -m "docs(tenancy): consola de operador, Platform:OperatorTenantId y SQL de respaldo con estado"
git log -1 --format=%B | grep -c "Co-Authored-By"
```

- [ ] **Step 6: Checklist de cierre**

- [ ] `git log --format=%B develop..HEAD | grep -c "Co-Authored-By"` imprime `0`.
- [ ] Spec §1–§5 y §7 cubiertos: configuración (T1), permisos y filtro (T2–T3), dominio (T4), tabla y migración (T5), escritura y candado (T6), enforcement del estado (T7), las cinco rutas (T8–T11), README (T12).
- [ ] Ningún archivo bajo `qep-frontend` tocado; el contrato de §5 sin cambios (`TheDetailJsonMatchesTheContract` en verde).
- [ ] No hay push ni merge en este plan: eso lo decide el developer (`superpowers:finishing-a-development-branch`).
