# Módulos por tenant (entitlements) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que QCode prenda y apague por tenant los módulos comerciales (`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`, `pos`) desde una tabla de Tenancy, que el backend corte el acceso enmascarando permisos en el request siguiente, y que la SPA esconda el módulo y explique por qué.

**Architecture:** Tenancy gana `tenancy.tenant_modules` (presencia de la fila = módulo contratado) detrás del puerto `ITenantModules`; `TenantModuleSet` calcula los efectivos cerrando dependencias. `PermissionDefinition` declara sus `RequiredModules` y `ModuleEntitlementMask` descarta los permisos de módulos apagados en los dos caminos de autenticación (cookie real y stub). Dos lugares cuyo permiso es de núcleo llevan chequeo explícito (`TenantModuleGuard`): la configuración del Excel de pedidos y los archivos de Storage según su dueño. Un endpoint `GET /tenants/{id}/modules` alimenta a la SPA, que monta un `ModuleGate` por ruta.

**Tech Stack:** Backend .NET 10 (SDK de `global.json`), EF Core + Npgsql, xUnit v3, Testcontainers (`postgres:18-alpine`, Docker corriendo). Frontend React 19, TanStack Router (árbol generado y versionado en `src/routeTree.gen.ts`) y TanStack Query, Vitest + Testing Library, bun, oxlint, `tsc -b` dentro de `bun run build`.

**Spec:** `docs/superpowers/specs/2026-10-07-modulos-por-tenant-design.md` (lo copia la Task B0 desde la carpeta de trabajo de la sesión que lo escribió). El plan argumenta desde el spec: quien ejecuta lee los dos.

## Global Constraints

**Del spec** (valores copiados tal cual):

- Claves, en este orden (topológico, también el de la respuesta y el del `CHECK`): `catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`, `pos`.
- Dependencias: `quotations` → `catalog`, `customers`, `companies`; `orders` → `quotations`; `pos` → `catalog`, `companies`; `reporting` sin dependencias. **POS no depende de `customers` ni opcionalmente**: no hay gate de clientes para POS.
- «Módulos efectivos (fail closed). Un módulo guardado cuya dependencia no está efectivamente habilitada cuenta como apagado, y eso se propaga.»
- `missingDependencies` nombra la **causa raíz** (dependencias transitivas no contratadas), en el orden de `All`, sólo con `contracted: true` y `enabled: false`.
- Tabla `tenancy.tenant_modules` (`tenant_id uuid`, `module_key varchar(32)`, `enabled_at timestamptz`, `source varchar(16)`, `note varchar(300) NULL`), PK `(tenant_id, module_key)`, FK a `tenancy.tenants` `ON DELETE CASCADE`, `CK_tenant_modules_module_key`, `CK_tenant_modules_source IN ('backfill','signup','seed','manual')`.
- Backfill en la misma migración `AddTenantModules`: los seis sin `pos`, `source = 'backfill'`.
- Signup: `Entitlements:GrantDefaultModulesOnSignup` (`bool`, default `true`) ⇒ los seis sin `pos` con `source = 'signup'`; `false` ⇒ ninguna fila. Registro con `ValidateOnStart`, sin validador.
- Semilla: las siete con `source = 'seed'`, **sólo al crear** el tenant.
- Mapa permiso → módulo: la tabla de 35 filas del spec («Mapa permiso → módulo»). `reporting.orders.read` exige `reporting` y `orders`; `reporting.quotation.read` exige `reporting` y `quotations`; `reporting.price_change.read` exige `reporting` y `catalog`; `reporting.customer.read` exige `reporting` y `customers`; `reporting.all_advisors.read` exige sólo `reporting`.
- `RequiredModules = null` = sin mapear = se enmascara siempre. Un permiso no registrado se enmascara.
- Stub: enmascara **sólo si `FindAsync` no es `null`**. Cookie real: `FindAsync ?? TenantModuleSet.Empty` (sólo núcleo).
- Código de error: `tenancy.module_not_enabled` (403, `RequestForbiddenException`).
- `/modules`: siempre las siete en el orden de `All`; con `FindAsync` en `null`, todo prendido y contratado bajo el esquema del stub, todo apagado y sin contratar bajo cualquier otro.
- `/authorization/catalog` se filtra; `/authorization/roles` y `EnsureKnownPermissions` no. `catalogVersion` no cambia.
- Storage: `Product` → `catalog`, `PaymentProof` → `orders`, `User`, `Entity`, `System`, `Tenant` núcleo. El guard va **después del 404** de otro tenant y **antes de cualquier otra regla o efecto**.
- SPA: mensaje «Este módulo no está incluido en el plan de tu empresa.»; singular «Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.»; plural «Este módulo necesita Catálogo y Clientes, que no están incluidos en el plan de tu empresa.»; carga «Cargando módulos…». Etiquetas: Catálogo, Clientes, Empresas, Cotizaciones, Pedidos, Reportes, Punto de venta.
- SPA: `useTenantModules` con `staleTime: 5 * 60 * 1000`, sin reintento ante `TenantModulesPayloadError`; `isEnabled` → `ready`: el `enabled` del ítem; `error`/`denied`: `true`; `loading`: `false`.

**Del proceso:**

- **Worktrees** (decisión de este plan, el owner duerme): backend en `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\modulos-por-tenant`, rama `feature/modulos-por-tenant` desde `origin/develop`; frontend en `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\modulos-por-tenant`, rama `feature/modulos-por-tenant` desde `origin/develop`. Nunca se trabaja en los checkouts principales: puede haber otra sesión activa ahí. Cada worktree inicializa su propio índice de CodeGraph (`gentle-ai codegraph init --cwd <worktree>`); nunca se copia el `.codegraph/` de otro checkout. En los bloques de comandos, `$B` y `$F` son esas dos rutas:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\modulos-por-tenant"
$F = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\modulos-por-tenant"
```

- **Paralelismo:** las tareas F1–F10 (frontend) no dependen de ninguna tarea del backend —prueban contra `fetch` simulado— y pueden correr en paralelo con B1–B12. Dentro de cada parte el orden es el numerado.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff (el resumen Superado/Con error/Omitido, o `Tests  N failed | M passed` de Vitest, y el mensaje de cada falla). Un RED que es error de compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal.
- **PowerShell** en todo comando: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`. **Nunca** se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- **Proceso de la API:** `Api.exe` o `dotnet Api.dll` corriendo bloquea `build`, `test` y `ef` (MSB3021). Antes de cada uno:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -match 'Api\.dll' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

- Las pruebas corren **en primer plano**, nunca en background. Por tarea se corren sólo las clases que la tarea toca (`--filter "FullyQualifiedName~<Clase>"`, un proyecto por comando) más `tests/ArchitectureTests/ArchitectureTests` cuando la tarea toca el composition root. La suite completa corre en B0 (baseline) y en B13.
- **Paquetes:** ninguna tarea agrega paquetes NuGet. Agregar una `ProjectReference` sí cambia el `packages.lock.json` del proyecto que la recibe (B2): se regenera con `dotnet restore <csproj> --force-evaluate` y se commitea **junto** con el `.csproj`; el `Dockerfile` corre `--locked-mode` y fallaría con `NU1004`.
- **Migración** con el factory de diseño, nunca a mano ni con `--startup-project`:
  `dotnet ef migrations add AddTenantModules --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`. El backfill se agrega a mano al `Up` generado. `TenancyDbContextModelSnapshot.cs` sólo cambia regenerado por ese comando.
- **xUnit v3:** toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- **CA1873:** ninguna tarea agrega logs. Si alguna termina agregando uno con un argumento calculado, va con `LoggerMessage` y el valor en una variable local antes del guard `IsEnabled`.
- **Commits:** Conventional Commits en español. **Sin atribución de IA ni trailer `Co-Authored-By`**, aunque el harness lo pida. Cada commit en un solo comando con guard de rama y rutas explícitas (nunca `git add -A` ni `git add .`):

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT: rama equivocada" }; git add <rutas>; git commit -m "<mensaje>"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

  El último comando no devuelve nada; si devuelve algo, `git commit --amend` sin el trailer antes de seguir. En el frontend, igual con `Set-Location $F`.
- **Idioma:** prosa, comentarios y `<summary>` en español colombiano, tuteando (nunca voseo); identificadores, códigos de error y mensajes de excepción en inglés, como el código de alrededor. Los textos que ve la persona en la SPA, tuteando. Los comentarios nuevos explican el porqué y citan el spec.
- **Formato del backend** sobre los `.cs` que toca cada tarea (los de `Migrations/` no). `dotnet format` sobre todo el repo da ~90k errores `ENDOFLINE` de base; se mide sólo lo tocado y se filtran `ENDOFLINE` y `CHARSET`:

```powershell
Set-Location $B
$files = @(git diff --name-only origin/develop -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-modulos-format"
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

  Esperado: sin salida en el último bloque. `dotnet format` quita el BOM de los archivos que toca: no se corre sin `--verify-no-changes`.
- **Frontend:** `bun run test --run <archivo>` por tarea; `bun run lint` (oxlint) y `bun run build` (`tsc -b && vite build`, que además regenera `src/routeTree.gen.ts`) al cerrar cada tarea que toca rutas o tipos. **Una ruta nueva no existe para las pruebas hasta regenerar y commitear `src/routeTree.gen.ts`**: `vitest.config.ts` no carga el plugin del router y las pruebas importan el árbol versionado.

## Review Focus

Los cinco casos que el spec implica y que ninguna prueba nombrada por él cubre, del más probable al menos probable. Cada uno tiene su prueba en la tarea dueña del código:

1. **Volver a prender un módulo restituye todo.** El spec lo promete («Volver a prender devuelve todo como estaba») pero sólo prueba el apagado. Si el enmascarado cacheara algo por request o por scope, o si `TenantModuleSet` guardara estado, prender `customers` otra vez no devolvería `quotations`. Lo esperable: apagar `customers` da 403 en `GET /quotations`; reinsertar la fila da 200 en el request siguiente, sin reiniciar. → Task B6, `TurningCustomersBackOnRestoresQuotations`.
2. **Filtro explícito por dueño de un módulo apagado.** La decisión 31 dice «página vacía, no 403», pero el único caso del spec es el listado sin filtro. Un `?ownerType=PaymentProof&ownerId=…` con `orders` apagado tiene que dar `items: []` y `totalCount: 0`, no el comprobante ni un 403. → Task B12, `AnExplicitOwnerFilterOnADisabledModuleReturnsAnEmptyPage`.
3. **Un valor no booleano en `Entitlements:GrantDefaultModulesOnSignup`.** El spec registra con `ValidateOnStart` para que `"si"` tumbe el arranque y no el primer signup, pero ninguna prueba lo ejerce. → Task B2, `ANonBooleanSwitchFailsWhenTheOptionsAreBound`.
4. **`ModuleGate` con una fuente contratada pero sin su dependencia.** El spec prueba la fuente sin contratar (`['reporting','customers']`), no `['reporting','orders']` con `orders` contratado y `customers` sin contratar. Lo esperable: «Este módulo necesita Clientes…» (la causa raíz que trae `orders.missingDependencies`), no «no está incluido en el plan» ni «necesita Pedidos». → Task F3.
5. **Landing de quien sólo tiene un reporte.** Con sólo `reporting.customer.read`, el primer ítem visible es «Reportes» con su destino alterno; `landingFor` tiene que devolver `/reports/customers`, no `/reports/orders` (el `to` original, que daría la pantalla sin permiso). → Task F9.

---

## Hallazgos contra el código (2026-10-07)

Verificados en `origin/develop` del backend (`f5ef90b`) y del frontend (`e40d7de`).

1. **`ListQuotationsHandler` ya oculta el pedido sin `quotations.order.read`** (`ListQuotations.cs:170-177`): con `orders` apagado el permiso se enmascara y `orderId`/`orderStatus` salen en `null` sin código nuevo. La prueba de regresión del spec va en B6.
2. **Sin `X-Tenant-Id` el stub no llega a armar claims** (`DevelopmentAuthenticationHandler.cs:20-26`): la consulta a `ITenantModules` va después de ese guard y nunca corre sin tenant.
3. **Pruebas unitarias cuyo constructor cambia**: `AuthorizationServiceTests` (`:47`, `:59`, `:73`, `:90`), `IssueDownloadUrlHandlerTests.HandlerFor` (`:72`), `PaymentProofFileManagementTests` (`:130`, `:154`, `DeleteHandler` `:227`, `UnpublishHandler` `:241`), `PaymentProofPublicUrlTests` (`:39`), `GetOrdersExportLayoutHandlerTests.NewHandler` (`:195`), `UpdateOrdersExportLayoutHandlerTests.NewHandler` (`:309`), y `RoleCommandsTests` (`:24`, `:26`, `:28`) por el parámetro posicional de `PermissionDefinition`. `CompleteUploadHandler`, `CancelUploadHandler`, `UpdateFileMetadataHandler` y `TenantRegistrationService` no tienen prueba unitaria hoy.
4. **Dos implementaciones más de `IFileResourceRepository`** cambian con la firma de `SearchAsync`: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs:12` y `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:16`.
5. **`CreateAvailablePaymentProofFileAsync` sube como `User`** (`QuotationsApiHarness.cs:520-524`), el tipo de los comprobantes viejos (núcleo). La prueba de Storage por dueño usa `CreateAvailablePaymentProofImageAsync`, que sí sube `PaymentProof` (`:528-538`).
6. **Las clases de infraestructura de Tenancy son `internal`** (`TenantClock`, repositorios). `TenantModuleDefaults` se declara `public sealed` para probarla desde `Modules.Tenancy.UnitTests` sin `InternalsVisibleTo` (decisión de este plan). `TenantModules` y `TenantModuleRepository` quedan `internal` y se prueban por DI.
7. **`QepServiceCollectionExtensions.cs` importa muchos namespaces de Application.** Para no abrir ambigüedades con `Modules.Tenancy.Domain`, el mapa de módulos usa un alias: `using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;`.
8. **`src/routeTree.gen.ts` está versionado** y `vitest.config.ts` no carga el plugin del router: cada tarea del frontend que crea un archivo de ruta regenera el árbol con `bun run build` y lo commitea con la ruta.
9. **El texto del spec sobre la prueba `CHECK` (`23514`)** la ubica en `TenantModulesApiTests`; este plan la pone en `TenantModulesMigrationTests` (B3), junto al resto de lo que prueba la tabla. Mismo caso, otro archivo.

## Contrato HTTP nuevo (para el frontend)

`GET /api/v1/tenants/{tenantId}/modules`, autenticado, sin permiso. `403` con `code: "authorization.denied"` si el tenant del claim no es el de la ruta.

```ts
type TenantModulesResponse = {
  tenantId: string
  modules: {
    key: 'catalog' | 'customers' | 'companies' | 'quotations' | 'orders' | 'reporting' | 'pos'
    enabled: boolean            // efectivo (contratado y con sus dependencias)
    contracted: boolean         // la fila existe
    missingDependencies: string[] // causas raíz; [] salvo contracted && !enabled
  }[] // siempre las siete, en este orden
}
```

`403` con `code: "tenancy.module_not_enabled"`: `GET`/`PUT /orders-export-layout` sin `orders`; los siete comandos por id de `/files` sobre un archivo cuyo módulo está apagado.

---

## File Structure — backend

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` | B1 | `TenantModuleKey`, `TenantModuleKeys` (claves, orden, dependencias, defaults) |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleSet.cs` | B1 | Efectivos, contratados y causas raíz |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs` | B1 | Entidad de la fila y `TenantModuleSources` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs` | B1 | Puertos `ITenantModules`, `ITenantModuleRepository`, `ITenantModuleDefaults` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs` | B1 | 403 `tenancy.module_not_enabled` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/EntitlementsOptions.cs` | B2 | Sección `Entitlements` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantModuleDefaults.cs` | B2 | Los del signup según el interruptor |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs` | B3 | Adaptador de `ITenantModules` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs` | B3 | Adaptador de `ITenantModuleRepository` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddTenantModules.cs` (+ `.Designer.cs`) | B3 | Tabla y backfill |
| `src/Modules/Authorization/Modules.Authorization.Application/ModuleEntitlementMask.cs` | B5 | El enmascarado |
| `src/Api/TenantModulesEndpoints.cs` | B8 | `GET /modules` y sus DTO |
| `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs` | B10 | Mapa dueño → módulo y `FileOwnerModuleGuard` |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `TenantModuleSetTests.cs`, `TenantModuleTests.cs`, `TenantModuleGuardTests.cs`, `TenantModuleTestDoubles.cs` | B1 | |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleDefaultsTests.cs` | B2 | |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs` | B4 | |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs` | B3 | |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs` | B4, B6, B8 | |
| `tests/Modules/Authorization/Modules.Authorization.UnitTests/ModuleEntitlementMaskTests.cs`, `RoleCatalogTests.cs` | B5 | |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs` | B6, B7, B9 | |
| `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs`, `FileOwnerModuleHandlersTests.cs` | B10, B11 | |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs` | B2, B3 | Opciones y adaptadores |
| `src/Api/appsettings.example.json` | B2 | `Entitlements:GrantDefaultModulesOnSignup` |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj` (+ `packages.lock.json`) | B2 | Referencia a Tenancy.Infrastructure |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` | B3 | `TenantModules` y `ConfigureTenantModule` |
| `.../Persistence/Migrations/TenancyDbContextModelSnapshot.cs` | B3 | Regenerado |
| `src/Modules/Tenancy/Modules.Tenancy.Application/TenantRegistrationService.cs` | B4 | Filas `signup` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs` | B4 | Filas `seed` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs` | B4 | Siete `seed`, idempotente |
| `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs` | B5 | `RequiredModules`; fallback `null` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | B5 | 35 mapas y registro del mask |
| `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs` | B5 | Parámetro nuevo |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | B5 | Completitud del mapa |
| `src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs` | B6 | Enmascara |
| `src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs` | B6 | `async`, enmascara con fila |
| `tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs` | B6 | Constructor y casos nuevos |
| `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs` | B6 | Siembra las claves que faltan |
| `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs` | B6 | Siembra las siete |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs` | B6 | Caso por cookie |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` | B6 | `DisableModuleAsync`, `EnableModuleAsync` |
| `src/Api/AuthorizationCatalogEndpoints.cs` | B7 | Catálogo filtrado |
| `src/Bootstrapper/Authentication/QepAuthenticationMode.cs` | B8 | `IsDevelopmentStub(ClaimsPrincipal)` |
| `src/Api/Program.cs` | B8 | `MapTenantModulesEndpoints()` |
| `README.md` | B8 | § Módulos por tenant |
| `src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs`, `UpdateOrdersExportLayout.cs` | B9 | Guard de `orders` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs`, `UpdateOrdersExportLayoutHandlerTests.cs`, `QuotationsTestDoubles.cs` | B9 | |
| `src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs`, `CompleteUpload.cs`, `CancelUpload.cs`, `UpdateFileMetadata.cs`, `SetFilePublication.cs`, `SoftDeleteFile.cs` | B11 | Guard por dueño |
| `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs`, `IssueDownloadUrlHandlerTests.cs`, `PaymentProofFileManagementTests.cs`, `PaymentProofPublicUrlTests.cs` | B10–B12 | |
| `src/Modules/Storage/Modules.Storage.Application/IFileResourceRepository.cs`, `ListFiles.cs`; `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileResourceRepository.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs` | B12 | `excludedOwnerTypes` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` | B12 | Storage por dueño de punta a punta |

---

# Parte 1 — Backend (`qep-backend`)

### Task B0: Worktree, spec y baseline

**Files:**
- Create: `docs/superpowers/specs/2026-10-07-modulos-por-tenant-design.md`, `docs/superpowers/plans/2026-10-07-modulos-por-tenant.md` (copias de los de la carpeta de trabajo)

**Interfaces:**
- Produces: `$env:TEMP\qep-modulos-baseline-failed.txt` (nombres de las pruebas que ya fallan en `origin/develop`), que consume B13.

- [ ] **Step 1: Crear el worktree desde `origin/develop`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git fetch origin
git worktree add -b feature/modulos-por-tenant ..\qep-backend-worktrees\modulos-por-tenant origin/develop
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\modulos-por-tenant"
Set-Location $B
git branch --show-current
gentle-ai codegraph init --cwd $B
```

Esperado: `feature/modulos-por-tenant`.

- [ ] **Step 2: Copiar el spec y el plan al repo y commitear**

```powershell
$S = "<carpeta donde la sesión dejó el spec y el plan>"
Set-Location $B
Copy-Item "$S\specs\2026-10-07-modulos-por-tenant-design.md" docs\superpowers\specs\
Copy-Item "$S\plans\2026-10-07-modulos-por-tenant.md" docs\superpowers\plans\
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add docs/superpowers/specs/2026-10-07-modulos-por-tenant-design.md docs/superpowers/plans/2026-10-07-modulos-por-tenant.md; git commit -m "docs: spec y plan de módulos por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

- [ ] **Step 3: Baseline de la suite completa, antes del primer cambio de código**

Docker tiene que estar corriendo. Se toma sobre la rama recién creada, que todavía es `origin/develop` más un commit de documentación: no hace falta `stash`.

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -match 'Api\.dll' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet build Backend.slnx
$results = Join-Path $env:TEMP "qep-modulos-baseline"
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $results
Get-ChildItem -LiteralPath $results -Filter *.trx |
    ForEach-Object { [xml](Get-Content -LiteralPath $_.FullName -Raw) } |
    ForEach-Object { $_.TestRun.Results.UnitTestResult } |
    Where-Object { $_.outcome -eq 'Failed' } |
    ForEach-Object { $_.testName } |
    Sort-Object -Unique |
    Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-modulos-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-modulos-baseline-failed.txt")
```

`-LiteralPath` a propósito: los `.trx` de corridas múltiples llevan `[1]` en el nombre y `Get-Content` sin él los salta. Anotar en el handoff el resumen de `dotnet test` y la lista (se espera, por lo menos, las dos rojas conocidas de `OrderExportApiTests` sobre el NIT).

---

### Task B1: Dominio y puertos de módulos (Tenancy)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleSet.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `TenantModuleSetTests.cs`, `TenantModuleTests.cs`, `TenantModuleGuardTests.cs`, `TenantModuleTestDoubles.cs`

**Interfaces:**
- Produces (Tenancy.Domain): `sealed record TenantModuleKey { string Value; static TenantModuleKey Parse(string) }`; `static class TenantModuleKeys { Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos; IReadOnlyList<TenantModuleKey> All; IReadOnlyList<TenantModuleKey> DefaultForNewTenants; IReadOnlyList<TenantModuleKey> DependenciesOf(TenantModuleKey) }`; `sealed class TenantModuleSet { static TenantModuleSet Empty; static TenantModuleSet FromStored(IEnumerable<TenantModuleKey>); IReadOnlyList<TenantModuleKey> Stored, Effective; bool IsEnabled(TenantModuleKey); bool IsContracted(TenantModuleKey); IReadOnlyList<TenantModuleKey> MissingDependencies(TenantModuleKey) }`; `sealed class TenantModule { TenantId TenantId; TenantModuleKey ModuleKey; DateTimeOffset EnabledAt; string Source; string? Note; static TenantModule Create(TenantId, TenantModuleKey, string source, DateTimeOffset enabledAt, string? note) }`; `static class TenantModuleSources { const string Signup = "signup"; const string Seed = "seed"; }`.
- Produces (Tenancy.Application): `ITenantModules.FindAsync(Guid tenantId, CancellationToken) : Task<TenantModuleSet?>`; `ITenantModuleRepository.Add(TenantModule)`; `ITenantModuleDefaults.ForNewTenants : IReadOnlyCollection<TenantModuleKey>`; `static TenantModuleGuard.EnsureEnabledAsync(ITenantModules, Guid tenantId, TenantModuleKey, CancellationToken) : Task` y `TenantModuleGuard.ModuleNotEnabledCode = "tenancy.module_not_enabled"`.
- Produces (tests, Tenancy.UnitTests): `FixedTenantModules(TenantModuleSet? set)` y `FixedTenantModules.All` (los siete).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTestDoubles.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>El puerto de módulos con una respuesta fija. <c>null</c> es el tenant que no existe en
/// <c>tenancy.tenants</c> (sólo pasa con el stub de desarrollo).</summary>
internal sealed class FixedTenantModules(TenantModuleSet? set) : ITenantModules
{
    public static FixedTenantModules All { get; } = new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    public List<Guid> Asked { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(set);
    }
}
```

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Catálogo de módulos»: lista cerrada, en orden topológico, y la única
/// conversión de texto a clave.</summary>
public sealed class TenantModuleKeysTests
{
    [Fact]
    public void AllKeepsTheContractOrder()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos"],
            TenantModuleKeys.All.Select(key => key.Value));
    }

    // FromStored recorre All una sola vez: si una clave apareciera antes que una de sus
    // dependencias, la vería todavía apagada y la contaría como apagada.
    [Fact]
    public void EveryKeyComesAfterAllOfItsDependencies()
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            var key = TenantModuleKeys.All[index];
            foreach (var dependency in TenantModuleKeys.DependenciesOf(key))
            {
                Assert.Contains(dependency, TenantModuleKeys.All);
                Assert.True(
                    TenantModuleKeys.All.ToList().IndexOf(dependency) < index,
                    $"{dependency} must come before {key} in TenantModuleKeys.All.");
            }
        }
    }

    [Fact]
    public void DependenciesAreTheOnesOfTheSpec()
    {
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Customers, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Quotations));
        Assert.Equal([TenantModuleKeys.Quotations], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Orders));
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Pos));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Reporting));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Catalog));
    }

    [Fact]
    public void DefaultForNewTenantsIsEverythingButPos()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            TenantModuleKeys.DefaultForNewTenants.Select(key => key.Value));
    }

    [Fact]
    public void ParseReturnsTheSameInstanceForEveryKey()
    {
        foreach (var key in TenantModuleKeys.All)
        {
            Assert.Same(key, TenantModuleKey.Parse(key.Value));
        }
    }

    // Igual que el CHECK de la tabla: ni claves inventadas ni otra capitalización.
    [Theory]
    [InlineData("inventory")]
    [InlineData("")]
    [InlineData("Catalog")]
    public void ParseRejectsAnythingElse(string value)
    {
        Assert.Throws<ArgumentException>(() => TenantModuleKey.Parse(value));
    }
}
```

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleSetTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Módulos efectivos (fail closed)» y causas raíz.</summary>
public sealed class TenantModuleSetTests
{
    private static TenantModuleSet AllBut(params TenantModuleKey[] missing) =>
        TenantModuleSet.FromStored(TenantModuleKeys.All.Except(missing));

    [Fact]
    public void WithoutCustomersQuotationsAndOrdersAreOff()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.False(set.IsEnabled(TenantModuleKeys.Quotations));
        Assert.False(set.IsEnabled(TenantModuleKeys.Orders));
        Assert.True(set.IsContracted(TenantModuleKeys.Quotations));
        Assert.True(set.IsEnabled(TenantModuleKeys.Catalog));
        Assert.True(set.IsEnabled(TenantModuleKeys.Reporting));
        Assert.True(set.IsEnabled(TenantModuleKeys.Pos));
    }

    // Causa raíz, no la dependencia directa: contratar quotations no arreglaría orders.
    [Fact]
    public void MissingDependenciesNamesTheRootCause()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Quotations));
        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Orders));
    }

    [Fact]
    public void SeveralRootCausesComeInTheOrderOfAll()
    {
        var set = AllBut(TenantModuleKeys.Customers, TenantModuleKeys.Catalog);

        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Customers],
            set.MissingDependencies(TenantModuleKeys.Orders));
    }

    [Fact]
    public void AKeyNotContractedOrEnabledReportsNoMissingDependencies()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.Empty(set.MissingDependencies(TenantModuleKeys.Customers));
        Assert.Empty(set.MissingDependencies(TenantModuleKeys.Catalog));
    }

    [Fact]
    public void StoredAndEffectiveKeepTheOrderOfAll()
    {
        var set = TenantModuleSet.FromStored([TenantModuleKeys.Reporting, TenantModuleKeys.Catalog]);

        Assert.Equal([TenantModuleKeys.Catalog, TenantModuleKeys.Reporting], set.Stored);
        Assert.Equal([TenantModuleKeys.Catalog, TenantModuleKeys.Reporting], set.Effective);
    }

    [Fact]
    public void EmptyHasNothing()
    {
        Assert.Empty(TenantModuleSet.Empty.Stored);
        Assert.Empty(TenantModuleSet.Empty.Effective);
        Assert.All(TenantModuleKeys.All, key => Assert.False(TenantModuleSet.Empty.IsEnabled(key)));
    }

    [Fact]
    public void FromStoredIgnoresRepeatedKeys()
    {
        var set = TenantModuleSet.FromStored([TenantModuleKeys.Catalog, TenantModuleKeys.Catalog]);

        Assert.Equal([TenantModuleKeys.Catalog], set.Stored);
    }
}
```

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTests.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantModuleTests
{
    // El origen no se valida en el dominio (spec, «Dominio»): el CHECK de la tabla es la única
    // validación, y `backfill`/`manual` sólo los escribe SQL.
    [Fact]
    public void CreateKeepsEverythingAsItArrives()
    {
        var tenantId = TenantId.New();
        var enabledAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var module = TenantModule.Create(tenantId, TenantModuleKeys.Orders, "anything", enabledAt, "nota");

        Assert.Equal(tenantId, module.TenantId);
        Assert.Same(TenantModuleKeys.Orders, module.ModuleKey);
        Assert.Equal("anything", module.Source);
        Assert.Equal(enabledAt, module.EnabledAt);
        Assert.Equal("nota", module.Note);
    }
}
```

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleGuardTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantModuleGuardTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public async Task AModuleThatIsOffIsForbiddenWithItsOwnCode()
    {
        var modules = new FixedTenantModules(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Orders])));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            TenantModuleGuard.EnsureEnabledAsync(
                modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
    }

    // Fail closed por dependencia: orders contratado sin quotations efectivo también es "apagado".
    [Fact]
    public async Task AModuleWithoutItsDependencyIsForbiddenToo()
    {
        var modules = new FixedTenantModules(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Customers])));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            TenantModuleGuard.EnsureEnabledAsync(
                modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEnabledModulePasses()
    {
        await TenantModuleGuard.EnsureEnabledAsync(
            FixedTenantModules.All, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken);
    }

    // null = tenant simulado por el stub: no se bloquea (spec, «Puertos»).
    [Fact]
    public async Task ATenantWithoutRowIsNotBlocked()
    {
        await TenantModuleGuard.EnsureEnabledAsync(
            new FixedTenantModules(null), TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModule"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'TenantModuleKeys' could not be found` (y los de `TenantModuleSet`, `TenantModule`, `ITenantModules`, `TenantModuleGuard`).

- [ ] **Step 3: Implementar el dominio**

`src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`:

```csharp
namespace Modules.Tenancy.Domain;

/// <summary>
/// Una clave de módulo comercial (spec 2026-10-07, «Catálogo de módulos»). Sus únicas instancias son
/// las de <see cref="TenantModuleKeys"/>: el constructor es privado, así que una clave inventada no
/// compila. El texto sólo existe en la base y en el JSON.
/// </summary>
public sealed record TenantModuleKey
{
    private TenantModuleKey(string value) => Value = value;

    public string Value { get; }

    internal static TenantModuleKey Define(string value) => new(value);

    /// <summary>
    /// La única conversión de texto a clave. La usa el mapeo de EF: la base es el único lugar donde
    /// una clave llega como texto. Lanza con cualquier otro texto —ignorarlo escondería una base
    /// corrupta—, y no ignora mayúsculas, igual que el <c>CHECK</c>. No hay <c>TryParse</c>: nadie
    /// necesita ignorar una clave desconocida.
    /// </summary>
    public static TenantModuleKey Parse(string value) =>
        TenantModuleKeys.All.FirstOrDefault(key => string.Equals(key.Value, value, StringComparison.Ordinal))
        ?? throw new ArgumentException($"'{value}' is not a known tenant module key.", nameof(value));

    public override string ToString() => Value;
}

/// <summary>
/// La lista cerrada de módulos. <see cref="All"/> está en orden topológico —cada clave después de
/// todas sus dependencias—, que es el orden de la respuesta de <c>/modules</c>, el del <c>CHECK</c> y
/// del que depende <see cref="TenantModuleSet.FromStored"/>. Un módulo nuevo es otra migración que
/// cambia el <c>CHECK</c>, a propósito.
/// </summary>
public static class TenantModuleKeys
{
    public static readonly TenantModuleKey Catalog = TenantModuleKey.Define("catalog");
    public static readonly TenantModuleKey Customers = TenantModuleKey.Define("customers");
    public static readonly TenantModuleKey Companies = TenantModuleKey.Define("companies");
    public static readonly TenantModuleKey Quotations = TenantModuleKey.Define("quotations");
    public static readonly TenantModuleKey Orders = TenantModuleKey.Define("orders");
    public static readonly TenantModuleKey Reporting = TenantModuleKey.Define("reporting");
    public static readonly TenantModuleKey Pos = TenantModuleKey.Define("pos");

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos];

    /// <summary>Los seis de hoy, sin <c>pos</c>: el backfill y el signup con el interruptor prendido.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> DefaultForNewTenants =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting];

    // POS no depende de customers, ni opcionalmente: el spec de POS quitó la selección de cliente.
    private static readonly Dictionary<TenantModuleKey, TenantModuleKey[]> Dependencies = new()
    {
        [Quotations] = [Catalog, Customers, Companies],
        [Orders] = [Quotations],
        [Pos] = [Catalog, Companies],
    };

    public static IReadOnlyList<TenantModuleKey> DependenciesOf(TenantModuleKey key) =>
        Dependencies.TryGetValue(key, out var dependencies) ? dependencies : [];
}
```

`src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleSet.cs`:

```csharp
namespace Modules.Tenancy.Domain;

/// <summary>
/// Los módulos de un tenant: los contratados (las filas) y los efectivos. Fail closed: un módulo
/// contratado cuya dependencia no es efectiva cuenta como apagado, y eso se propaga (spec
/// 2026-10-07). Inmutable: volver a prender un módulo es otra consulta, no un cambio acá.
/// </summary>
public sealed class TenantModuleSet
{
    private readonly HashSet<TenantModuleKey> _stored;
    private readonly HashSet<TenantModuleKey> _effective;

    private TenantModuleSet(HashSet<TenantModuleKey> stored, HashSet<TenantModuleKey> effective)
    {
        _stored = stored;
        _effective = effective;
        Stored = TenantModuleKeys.All.Where(stored.Contains).ToArray();
        Effective = TenantModuleKeys.All.Where(effective.Contains).ToArray();
    }

    public static TenantModuleSet Empty { get; } = FromStored([]);

    /// <summary>Contratados, en el orden de <see cref="TenantModuleKeys.All"/>.</summary>
    public IReadOnlyList<TenantModuleKey> Stored { get; }

    /// <summary>Efectivos, en el orden de <see cref="TenantModuleKeys.All"/>.</summary>
    public IReadOnlyList<TenantModuleKey> Effective { get; }

    /// <summary>
    /// Una sola pasada por <see cref="TenantModuleKeys.All"/>, que está en orden topológico: cuando
    /// se mira una clave, todas sus dependencias ya se decidieron. No modifica lo que recorre.
    /// </summary>
    public static TenantModuleSet FromStored(IEnumerable<TenantModuleKey> keys)
    {
        var stored = keys.ToHashSet();
        var effective = new HashSet<TenantModuleKey>();
        foreach (var key in TenantModuleKeys.All)
        {
            if (stored.Contains(key) && TenantModuleKeys.DependenciesOf(key).All(effective.Contains))
            {
                effective.Add(key);
            }
        }

        return new TenantModuleSet(stored, effective);
    }

    public bool IsEnabled(TenantModuleKey key) => _effective.Contains(key);

    public bool IsContracted(TenantModuleKey key) => _stored.Contains(key);

    /// <summary>
    /// Las causas raíz de que una clave contratada esté apagada: las dependencias transitivas que no
    /// están contratadas, en el orden de <see cref="TenantModuleKeys.All"/>. Vacío si la clave no
    /// está contratada o está prendida. Nunca vacío para una contratada y apagada: la cadena de
    /// dependencias no efectivas siempre termina en una sin contratar.
    /// </summary>
    public IReadOnlyList<TenantModuleKey> MissingDependencies(TenantModuleKey key)
    {
        if (!IsContracted(key) || IsEnabled(key))
        {
            return [];
        }

        var closure = Closure(key);
        return TenantModuleKeys.All
            .Where(candidate => closure.Contains(candidate) && !_stored.Contains(candidate))
            .ToArray();
    }

    private static HashSet<TenantModuleKey> Closure(TenantModuleKey key)
    {
        var closure = new HashSet<TenantModuleKey>();
        var pending = new Stack<TenantModuleKey>(TenantModuleKeys.DependenciesOf(key));
        while (pending.Count > 0)
        {
            var dependency = pending.Pop();
            if (closure.Add(dependency))
            {
                foreach (var next in TenantModuleKeys.DependenciesOf(dependency))
                {
                    pending.Push(next);
                }
            }
        }

        return closure;
    }
}
```

`src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs`:

```csharp
namespace Modules.Tenancy.Domain;

/// <summary>
/// Un módulo contratado por un tenant: la presencia de la fila **es** el entitlement, y apagar es
/// borrarla (spec 2026-10-07). <see cref="Source"/> no se valida acá: el código sólo escribe
/// <see cref="TenantModuleSources"/>, <c>backfill</c> y <c>manual</c> sólo los escribe SQL, y el
/// <c>CHECK</c> de la tabla es la única validación.
/// </summary>
public sealed class TenantModule
{
    public const int SourceMaxLength = 16;
    public const int NoteMaxLength = 300;

    private TenantModule()
    {
        ModuleKey = null!;
        Source = string.Empty;
    }

    private TenantModule(
        TenantId tenantId, TenantModuleKey moduleKey, string source, DateTimeOffset enabledAt, string? note)
    {
        TenantId = tenantId;
        ModuleKey = moduleKey;
        Source = source;
        EnabledAt = enabledAt;
        Note = note;
    }

    public TenantId TenantId { get; private set; }

    public TenantModuleKey ModuleKey { get; private set; }

    public DateTimeOffset EnabledAt { get; private set; }

    public string Source { get; private set; }

    public string? Note { get; private set; }

    public static TenantModule Create(
        TenantId tenantId, TenantModuleKey moduleKey, string source, DateTimeOffset enabledAt, string? note) =>
        new(tenantId, moduleKey, source, enabledAt, note);
}

/// <summary>Los orígenes que escribe el código. <c>backfill</c> y <c>manual</c> los escribe SQL.</summary>
public static class TenantModuleSources
{
    public const string Signup = "signup";
    public const string Seed = "seed";
}
```

- [ ] **Step 4: Implementar los puertos y el guard**

`src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// De dónde salen los módulos de un tenant. Hoy la tabla local de Tenancy; mañana, el control plane,
/// sin tocar el enforcement (spec 2026-10-07, «Puertos»).
/// </summary>
public interface ITenantModules
{
    /// <summary><c>null</c> = el tenant no existe en <c>tenancy.tenants</c> (sólo pasa con el stub de
    /// desarrollo).</summary>
    Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Se commitea con <see cref="ITenancyUnitOfWork"/>.</summary>
public interface ITenantModuleRepository
{
    void Add(TenantModule module);
}

/// <summary>Los módulos con los que nace un tenant del signup.</summary>
public interface ITenantModuleDefaults
{
    IReadOnlyCollection<TenantModuleKey> ForNewTenants { get; }
}
```

`src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El chequeo explícito para los lugares cuyo permiso es de núcleo pero lo que exponen es de un
/// módulo (spec 2026-10-07): la configuración del Excel de pedidos y los archivos de Storage según
/// su dueño. 403 <c>tenancy.module_not_enabled</c> por <c>ApiExceptionHandler</c>. Con el tenant
/// simulado por el stub (<c>FindAsync</c> en <c>null</c>) no bloquea.
/// </summary>
public static class TenantModuleGuard
{
    public const string ModuleNotEnabledCode = "tenancy.module_not_enabled";

    public static async Task EnsureEnabledAsync(
        ITenantModules modules,
        Guid tenantId,
        TenantModuleKey key,
        CancellationToken cancellationToken)
    {
        var set = await modules.FindAsync(tenantId, cancellationToken);
        if (set is not null && !set.IsEnabled(key))
        {
            throw new RequestForbiddenException(
                ModuleNotEnabledCode,
                $"The '{key.Value}' module is not enabled for this tenant.");
        }
    }
}
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModule"
```

Esperado: todas en verde (`Superado`), 0 con error.

- [ ] **Step 6: Formato y commit**

Correr el chequeo de formato de «Global Constraints». Después:

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleSet.cs src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModule.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Application/TenantModuleGuard.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleSetTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleGuardTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleTestDoubles.cs; git commit -m "feat(tenancy): módulos por tenant en el dominio y su puerto"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B2: Interruptor del signup (`Entitlements`)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/EntitlementsOptions.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantModuleDefaults.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs:28-52`
- Modify: `src/Api/appsettings.example.json` (después de la sección `Registration`, `:34-36`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj` y su `packages.lock.json`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleDefaultsTests.cs`; `tests/ArchitectureTests/ArchitectureTests/ConfigurationExampleTests.cs` (sin cambios: es la que pone el RED del ejemplo)

**Interfaces:**
- Consumes: `TenantModuleKeys.DefaultForNewTenants`, `ITenantModuleDefaults` (B1).
- Produces: `public sealed class EntitlementsOptions { const string SectionName = "Entitlements"; bool GrantDefaultModulesOnSignup { get; set; } = true; }`; `public sealed class TenantModuleDefaults(IOptions<EntitlementsOptions>) : ITenantModuleDefaults` registrada singleton.

- [ ] **Step 1: Referenciar Infrastructure desde las pruebas unitarias de Tenancy**

En `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj`, junto a las dos `ProjectReference` existentes:

```xml
    <ProjectReference Include="..\..\..\..\src\Modules\Tenancy\Modules.Tenancy.Infrastructure\Modules.Tenancy.Infrastructure.csproj" />
```

Mismo criterio que `Modules.Notifications.UnitTests.csproj:15`. Regenerar sólo el lock de ese proyecto:

```powershell
Set-Location $B
dotnet restore tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --force-evaluate
git diff --stat -- tests/Modules/Tenancy/Modules.Tenancy.UnitTests/packages.lock.json
```

Esperado: el lock cambia (proyecto nuevo y sus transitivos). Ningún otro `packages.lock.json` cambia: `git status --short -- '*packages.lock.json'` lista sólo ése.

- [ ] **Step 2: Escribir la prueba que falla**

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleDefaultsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Alta por signup»: los seis de hoy o ninguno, según el interruptor.</summary>
public sealed class TenantModuleDefaultsTests
{
    [Fact]
    public void WithTheSwitchOnANewTenantGetsTheSixOfToday()
    {
        var defaults = new TenantModuleDefaults(
            Options.Create(new EntitlementsOptions { GrantDefaultModulesOnSignup = true }));

        Assert.Equal(TenantModuleKeys.DefaultForNewTenants, defaults.ForNewTenants);
    }

    [Fact]
    public void WithTheSwitchOffANewTenantGetsNothing()
    {
        var defaults = new TenantModuleDefaults(
            Options.Create(new EntitlementsOptions { GrantDefaultModulesOnSignup = false }));

        Assert.Empty(defaults.ForNewTenants);
    }

    // El default conserva el comportamiento de hoy: sin la sección, los seis.
    [Fact]
    public void WithoutTheSectionTheSwitchIsOn()
    {
        var options = new ConfigurationBuilder().Build()
            .GetSection(EntitlementsOptions.SectionName)
            .Get<EntitlementsOptions>() ?? new EntitlementsOptions();

        Assert.True(options.GrantDefaultModulesOnSignup);
    }

    // Review Focus 3: un valor que no es booleano no puede llegar al primer signup. El binder lanza
    // al materializar las opciones, que con ValidateOnStart es el arranque.
    [Fact]
    public void ANonBooleanSwitchFailsWhenTheOptionsAreBound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entitlements:GrantDefaultModulesOnSignup"] = "si",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration.GetSection(EntitlementsOptions.SectionName).Get<EntitlementsOptions>());
    }
}
```

- [ ] **Step 3: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleDefaultsTests"
```

Esperado: `error CS0246: The type or namespace name 'TenantModuleDefaults' could not be found` y `'EntitlementsOptions'`.

- [ ] **Step 4: Implementar opciones y defaults**

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/EntitlementsOptions.cs`:

```csharp
namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-07, «Alta por signup». <c>register-tenant</c> lo puede llamar cualquiera con un
/// token de Google, así que darle módulos comerciales a ese tenant es una decisión de negocio y se
/// configura aparte. Vive en Infrastructure porque ninguna capa Application del repo usa
/// <c>IOptions</c>. Sin validador: un <c>bool</c> no tiene valores inválidos; el binding corre al
/// arrancar con <c>ValidateOnStart</c>, así que <c>"si"</c> tumba el arranque y no el primer signup.
/// </summary>
public sealed class EntitlementsOptions
{
    public const string SectionName = "Entitlements";

    /// <summary><c>true</c> (default, el comportamiento de hoy): el signup da los seis módulos sin
    /// <c>pos</c>. <c>false</c>: no da ninguno (sólo núcleo). Cuando exista el cobro, lo esperable es
    /// que QCode lo ponga en <c>false</c> y prenda los módulos al pagar.</summary>
    public bool GrantDefaultModulesOnSignup { get; set; } = true;
}
```

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantModuleDefaults.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Los módulos de un tenant nuevo del signup. Pública, a diferencia del resto de la infraestructura
/// de Tenancy, sólo para probarla sin <c>InternalsVisibleTo</c>. No hay lista configurable (YAGNI):
/// el paquete por tenant es asunto del control plane.
/// </summary>
public sealed class TenantModuleDefaults(IOptions<EntitlementsOptions> options) : ITenantModuleDefaults
{
    public IReadOnlyCollection<TenantModuleKey> ForNewTenants =>
        options.Value.GrantDefaultModulesOnSignup ? TenantModuleKeys.DefaultForNewTenants : [];
}
```

En `TenancyInfrastructureExtensions.AddTenancyInfrastructure`, después de `services.AddScoped<ITenantRegistration, TenantRegistrationService>();` (`:36`):

```csharp
        // Spec 2026-10-07: ValidateOnStart, como Notifications, para que un valor que no es
        // booleano tumbe el arranque en vez del primer signup.
        services.AddOptions<EntitlementsOptions>()
            .Bind(configuration.GetSection(EntitlementsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ITenantModuleDefaults, TenantModuleDefaults>();
```

- [ ] **Step 5: Ver el GREEN de la unidad y el RED del ejemplo**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleDefaultsTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: la primera en verde (4 superadas). La segunda **en rojo**: `EveryBoundConfigurationKeyIsDocumentedInTheExample` falla con «…no están en src/Api/appsettings.example.json…: Entitlements:GrantDefaultModulesOnSignup». Es el RED que pide el ejemplo.

- [ ] **Step 6: Documentar la clave en el ejemplo**

En `src/Api/appsettings.example.json`, inmediatamente después del bloque `"Registration": { "PublicTenantSignupEnabled": true },`:

```json
  "Entitlements": {
    "GrantDefaultModulesOnSignup": true
  },
```

```powershell
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: verde.

- [ ] **Step 7: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/EntitlementsOptions.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantModuleDefaults.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs src/Api/appsettings.example.json tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj tests/Modules/Tenancy/Modules.Tenancy.UnitTests/packages.lock.json tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleDefaultsTests.cs; git commit -m "feat(tenancy): interruptor Entitlements:GrantDefaultModulesOnSignup"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B3: Tabla, migración con backfill y adaptadores

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:11-35` (DbSet y `OnModelCreating`), y método nuevo junto a `ConfigureTenant`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs`
- Create (generado): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddTenantModules.cs`, `.Designer.cs`; Modify (generado): `TenancyDbContextModelSnapshot.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs`

**Interfaces:**
- Consumes: `TenantModule`, `TenantModuleKey.Parse`, `TenantModuleSet.FromStored`, `ITenantModules`, `ITenantModuleRepository` (B1).
- Produces: `TenancyDbContext.TenantModules : DbSet<TenantModule>` (público, lo usan las pruebas de B4–B12 para apagar módulos); `ITenantModules` y `ITenantModuleRepository` registrados scoped.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La tabla <c>tenancy.tenant_modules</c> (spec 2026-10-07, «Tabla y migración»): el backfill de la
/// misma migración, el <c>CHECK</c> y el adaptador <c>ITenantModules</c>. Migra sólo Tenancy con
/// <see cref="IMigrator"/>, mismo patrón que <c>AuthorizationPermissionsMigrationTests</c>.
/// </summary>
public sealed class TenantModulesMigrationTests
{
    private const string AddMembershipAdvisorCode = "20260924152521_AddMembershipAdvisorCode";
    private const string LegacyTenantId = "01900000-0000-7000-8000-00000000e001";

    // Columnas de tenancy.tenants en AddMembershipAdvisorCode (TenancyDbContextModelSnapshot.cs);
    // owner_membership_id es NOT NULL desde RequireTenantOwnerMembership y no tiene FK.
    private const string LegacyTenantSql = $"""
        INSERT INTO tenancy.tenants (
            id, slug, status, display_name, default_culture, time_zone, date_format,
            owner_membership_id, version, created_at, updated_at)
        VALUES (
            '{LegacyTenantId}', 'backfill-test', 'Active', 'Backfill Test', 'es-CO', 'America/Bogota',
            'yyyy-MM-dd', '01900000-0000-7000-8000-00000000e002', 1, now(), now());
        """;

    [Fact]
    public async Task AnExistingTenantKeepsTheSixModulesOfToday()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyTenantSql);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("catalog", "backfill"), ("companies", "backfill"), ("customers", "backfill"),
                ("orders", "backfill"), ("quotations", "backfill"), ("reporting", "backfill"),
            ],
            await RowsAsync(connectionString, LegacyTenantId));
    }

    [Fact]
    public async Task TheCheckRejectsAnUnknownKey()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(
            AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyTenantSql);
        await context.GetService<IMigrator>().MigrateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES ('{LegacyTenantId}', 'inventory', now(), 'manual');
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    // El adaptador: null para un tenant sin fila en tenancy.tenants (el simulado por el stub) y
    // los efectivos para uno que existe, con las claves ya tipadas por la conversión de EF.
    [Fact]
    public async Task FindAsyncDistinguishesAMissingTenantFromOneWithModules()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var context = NewContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(
                AddMembershipAdvisorCode, TestContext.Current.CancellationToken);
            await ExecuteAsync(connectionString, LegacyTenantSql);
            await context.GetService<IMigrator>().MigrateAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(
            connectionString,
            $"DELETE FROM tenancy.tenant_modules WHERE tenant_id = '{LegacyTenantId}' AND module_key = 'customers';");

        await using var provider = TenancyServices(connectionString);
        await using var scope = provider.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetRequiredService<ITenantModules>();

        Assert.Null(await modules.FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        var set = await modules.FindAsync(Guid.Parse(LegacyTenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(set);
        Assert.False(set.IsContracted(TenantModuleKeys.Customers));
        Assert.False(set.IsEnabled(TenantModuleKeys.Quotations));
        Assert.True(set.IsEnabled(TenantModuleKeys.Catalog));
        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Orders));
    }

    private static ServiceProvider TenancyServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = connectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static TenancyDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<(string Key, string Source)>> RowsAsync(string connectionString, string tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT module_key, source FROM tenancy.tenant_modules WHERE tenant_id = '{tenantId}' ORDER BY module_key",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
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

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesMigrationTests"
```

Esperado: compila (todo lo nombrado existe desde B1) y las tres fallan: las dos primeras con `Npgsql.PostgresException : 42P01: relation "tenancy.tenant_modules" does not exist`, la tercera con `System.InvalidOperationException : No service for type 'Modules.Tenancy.Application.ITenantModules'`.

- [ ] **Step 3: Mapear la entidad**

En `TenancyDbContext.cs`, junto a los otros `DbSet` públicos:

```csharp
    public DbSet<TenantModule> TenantModules => Set<TenantModule>();
```

En `OnModelCreating`, después de `ConfigureTenant(modelBuilder);`:

```csharp
        ConfigureTenantModule(modelBuilder);
```

Y el método, a continuación de `ConfigureTenant`:

```csharp
    // Spec 2026-10-07, «Tabla y migración». La FK va en el modelo y no sólo en SQL: sin ella EF no
    // sabe que la fila depende del tenant y puede ordenar el INSERT de tenant_modules antes que el de
    // tenants en el mismo SaveChangesAsync (23503 en TenantRegistrationService y en TenancySeeder).
    // Sin navegación en Tenant: el agregado no carga sus módulos, y no cierra ningún ciclo porque
    // tenants no apunta a tenant_modules.
    private static void ConfigureTenantModule(ModelBuilder modelBuilder)
    {
        var module = modelBuilder.Entity<TenantModule>();
        module.ToTable("tenant_modules", "tenancy", table =>
        {
            table.HasCheckConstraint(
                "CK_tenant_modules_module_key",
                "module_key IN ('catalog','customers','companies','quotations','orders','reporting','pos')");
            table.HasCheckConstraint(
                "CK_tenant_modules_source",
                "source IN ('backfill','signup','seed','manual')");
        });
        module.HasKey(value => new { value.TenantId, value.ModuleKey });
        module.Property(value => value.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));
        // La única conversión de texto a clave (TenantModuleKey.Parse): una clave desconocida lanza
        // al materializar la fila, ruidoso a propósito.
        module.Property(value => value.ModuleKey)
            .HasColumnName("module_key")
            .HasMaxLength(32)
            .HasConversion(key => key.Value, value => TenantModuleKey.Parse(value));
        module.Property(value => value.EnabledAt).HasColumnName("enabled_at");
        module.Property(value => value.Source)
            .HasColumnName("source")
            .HasMaxLength(TenantModule.SourceMaxLength);
        module.Property(value => value.Note)
            .HasColumnName("note")
            .HasMaxLength(TenantModule.NoteMaxLength);
        module.HasOne<Tenant>().WithMany()
            .HasForeignKey(value => value.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
```

- [ ] **Step 4: Los adaptadores**

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Una sola consulta: el tenant y sus claves, sin tracking. Las claves llegan tipadas por la
/// conversión de EF y van directo a <see cref="TenantModuleSet.FromStored"/>. Sin caché (spec,
/// alternativa descartada): a lo sumo siete filas por PK, y los permisos ya se resuelven contra la
/// base en cada request; el caché es asunto del adaptador del control plane.
/// </summary>
internal sealed class TenantModules(TenancyDbContext dbContext) : ITenantModules
{
    public async Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var found = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == id)
            .Select(tenant => new
            {
                Keys = dbContext.TenantModules
                    .Where(module => module.TenantId == tenant.Id)
                    .Select(module => module.ModuleKey)
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return found is null ? null : TenantModuleSet.FromStored(found.Keys);
    }
}
```

`src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantModuleRepository(TenancyDbContext dbContext) : ITenantModuleRepository
{
    public void Add(TenantModule module) => dbContext.TenantModules.Add(module);
}
```

En `TenancyInfrastructureExtensions.AddTenancyInfrastructure`, junto a los otros repositorios (`:28`):

```csharp
        services.AddScoped<ITenantModules, TenantModules>();
        services.AddScoped<ITenantModuleRepository, TenantModuleRepository>();
```

- [ ] **Step 5: Generar la migración**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddTenantModules --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
git status --short -- src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations
```

Esperado: un `<ts>_AddTenantModules.cs`, su `.Designer.cs` y `TenancyDbContextModelSnapshot.cs` modificado. Abrir el `Up` generado y confirmar: `CreateTable` de `tenant_modules` en el esquema `tenancy` con las cinco columnas, la PK `PK_tenant_modules`, la FK `FK_tenant_modules_tenants_tenant_id` con `ReferentialAction.Cascade` y los dos `CheckConstraint`. Si el `Up` trae cualquier otra operación (sobre `tenants`, `memberships` u otra tabla), **parar**: el modelo tiene una diferencia que no es de esta tarea.

- [ ] **Step 6: Agregar el backfill a mano**

En el `Up` generado, **después** del `migrationBuilder.CreateTable(...)` (y de cualquier `CreateIndex` que venga con él):

```csharp
            // Spec 2026-10-07: los tenants que existen al migrar conservan los seis módulos de hoy
            // (sin pos), en la misma migración y por lo tanto en la misma transacción que crea la
            // tabla, igual que el backfill de AddTenantOwnerMembership. Un tenant que un pod viejo
            // cree durante el rolling update queda sin filas: se repara con el SQL de «Operación».
            migrationBuilder.Sql(
                """
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                SELECT t.id, m.key, now(), 'backfill'
                FROM tenancy.tenants t
                CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key);
                """);
```

El `Down` generado (`DropTable`) no se toca.

- [ ] **Step 7: Ver el GREEN**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesMigrationTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~TenancyLayerTests"
```

Esperado: 3 superadas; `TenancyLayerTests` en verde (Tenancy.Application sigue sin EF Core).

- [ ] **Step 8: Formato y commit**

```powershell
Set-Location $B
$migration = git ls-files --others --exclude-standard -- 'src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/*AddTenantModules*'
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add $migration src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModules.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantModuleRepository.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesMigrationTests.cs; git commit -m "feat(tenancy): tabla tenant_modules con backfill de los seis módulos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B4: El signup y la semilla escriben sus módulos

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantRegistrationService.cs:7-13,42`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs:80-97`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`
- Create: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs`
- Create: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`

**Interfaces:**
- Consumes: `ITenantModuleRepository`, `ITenantModuleDefaults`, `TenantModule.Create`, `TenantModuleSources` (B1–B3), `TenancyDbContext.TenantModules` (B3).
- Produces: `TenantRegistrationService(ITenantRepository, IMembershipRepository, ITenantModuleRepository, ITenantModuleDefaults, ITenancyUnitOfWork, IAuditRecorder, IOutboxWriter, IClock)`. En `TenantModulesApiTests`: `QepApiFactory(string connectionString, bool? grantDefaultModulesOnSignup = null)`, `RegisterAsync(QepApiFactory) : Task<(Guid TenantId, Guid OwnerUserId)>`, `StubClient(QepApiFactory, Guid subjectId, Guid tenantId, params string[] permissions) : HttpClient`, `DisableAsync(QepApiFactory, Guid tenantId, TenantModuleKey)`, `RowsAsync(QepApiFactory, Guid tenantId) : Task<List<(string Key, string Source)>>`, que B6 y B8 reusan.

- [ ] **Step 1: Escribir la prueba unitaria del signup**

`tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Alta por signup»: una fila signup por clave de los defaults, en el mismo
/// commit que el tenant y la membresía. Un tenant nunca existe sin las filas que le tocan.</summary>
public sealed class TenantRegistrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly TenantRegistrationData Data =
        new("Org", "org-test", "es-CO", "America/Bogota", "yyyy-MM-dd");

    [Fact]
    public async Task SignupAddsOneSignupRowPerDefaultKeyAndCommitsOnce()
    {
        var steps = new List<string>();
        var modules = new RecordingTenantModuleRepository(steps);
        var service = NewService(steps, modules, new FixedDefaults(TenantModuleKeys.DefaultForNewTenants));

        var tenantId = await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            modules.Added.Select(module => module.ModuleKey.Value));
        Assert.All(modules.Added, module =>
        {
            Assert.Equal(new TenantId(tenantId), module.TenantId);
            Assert.Equal(TenantModuleSources.Signup, module.Source);
            Assert.Equal(Now, module.EnabledAt);
            Assert.Null(module.Note);
        });
        Assert.Equal("commit", steps[^1]);
        Assert.Single(steps, step => step == "commit");
        Assert.True(steps.IndexOf("tenant") < steps.IndexOf("module:catalog"));
    }

    [Fact]
    public async Task WithEmptyDefaultsNoRowIsAdded()
    {
        var steps = new List<string>();
        var modules = new RecordingTenantModuleRepository(steps);
        var service = NewService(steps, modules, new FixedDefaults([]));

        await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Empty(modules.Added);
        Assert.Single(steps, step => step == "commit");
    }

    private static TenantRegistrationService NewService(
        List<string> steps, RecordingTenantModuleRepository modules, ITenantModuleDefaults defaults) =>
        new(
            new RecordingTenantRepository(steps),
            new InMemoryMembershipRepository(),
            modules,
            defaults,
            new RecordingTenancyUnitOfWork(steps),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now));

    private sealed class RecordingTenantRepository(List<string> steps) : ITenantRepository
    {
        public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
            Task.FromResult<Tenant?>(null);

        public void Add(Tenant tenant) => steps.Add("tenant");
    }

    private sealed class RecordingTenantModuleRepository(List<string> steps) : ITenantModuleRepository
    {
        public List<TenantModule> Added { get; } = [];

        public void Add(TenantModule module)
        {
            steps.Add($"module:{module.ModuleKey.Value}");
            Added.Add(module);
        }
    }

    private sealed class FixedDefaults(IReadOnlyCollection<TenantModuleKey> keys) : ITenantModuleDefaults
    {
        public IReadOnlyCollection<TenantModuleKey> ForNewTenants { get; } = keys;
    }
}
```

`InMemoryMembershipRepository`, `RecordingTenancyUnitOfWork`, `RecordingAuditRecorder`, `RecordingOutboxWriter` y `FixedClock` ya existen en `MembershipHandlerTestDoubles.cs` y `TenantLogoHandlerTestDoubles.cs`.

- [ ] **Step 2: Escribir las pruebas de integración del signup y la semilla**

`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs` (B6 y B8 le suman casos):

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Módulos por tenant de punta a punta (spec 2026-10-07): lo que deja el signup, el enmascarado del
/// stub sobre un tenant con fila y el endpoint <c>/modules</c>. Corre con el stub de desarrollo; la
/// cookie real va en <c>RealAuthenticationApiTests</c>.
/// </summary>
public sealed class TenantModulesApiTests
{
    // Es la prueba de que EF ordena el INSERT de tenants antes que el de tenant_modules: los dos
    // salen del mismo SaveChangesAsync de TenantRegistrationService, y sin la FK en el modelo el
    // orden no estaría garantizado (23503).
    [Fact]
    public async Task SignupStoresTheSixDefaultModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (tenantId, _) = await RegisterAsync(factory);

        Assert.Equal(
            [
                ("catalog", "signup"), ("companies", "signup"), ("customers", "signup"),
                ("orders", "signup"), ("quotations", "signup"), ("reporting", "signup"),
            ],
            await RowsAsync(factory, tenantId));
    }

    [Fact]
    public async Task SignupWithTheSwitchOffStoresNoModule()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), grantDefaultModulesOnSignup: false);

        var (tenantId, _) = await RegisterAsync(factory);

        Assert.Empty(await RowsAsync(factory, tenantId));
    }

    internal static async Task<(Guid TenantId, Guid OwnerUserId)> RegisterAsync(QepApiFactory factory)
    {
        using var bootstrap = StubClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.CreateVersion7():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Modules Test Org",
                slug = $"mod-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisteredDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId);
    }

    internal static HttpClient StubClient(
        QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    internal static async Task DisableAsync(QepApiFactory factory, Guid tenantId, TenantModuleKey key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        await dbContext.TenantModules
            .Where(module => module.TenantId == id && module.ModuleKey == key)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    internal static async Task<List<(string Key, string Source)>> RowsAsync(QepApiFactory factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var rows = await dbContext.TenantModules
            .AsNoTracking()
            .Where(module => module.TenantId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
        return rows
            .Select(module => (module.ModuleKey.Value, module.Source))
            .OrderBy(row => row.Value, StringComparer.Ordinal)
            .ToList();
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

    private sealed record RegisteredDto(Guid TenantId, Guid OwnerUserId);

    internal sealed class QepApiFactory(string connectionString, bool? grantDefaultModulesOnSignup = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados de appsettings.json ni de los user-secrets de quien corre
            // las pruebas (SDD-CT-17): cualquiera de los dos puede tumbar el arranque.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            if (grantDefaultModulesOnSignup is { } grant)
            {
                builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", grant ? "true" : "false");
            }
        }
    }
}
```

En `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`, dos casos nuevos después de `SeedCreatesAnActiveAdminMembership`:

```csharp
    // Spec 2026-10-07, «Semilla»: el tenant de la semilla nace con los siete, pos incluido.
    [Fact]
    public async Task SeedEnablesTheSevenModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        Assert.Equal(
            ["catalog", "companies", "customers", "orders", "pos", "quotations", "reporting"],
            await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed));
    }

    // Sólo al crear: el seeder devuelve antes si el tenant ya existe, así que correrlo otra vez no
    // duplica (PK) ni pisa lo que alguien apagó a mano.
    [Fact]
    public async Task SeedingTwiceDoesNotDuplicateTheModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.SeedTenantWithOwnerAsync(
            Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Equal(7, (await SeedModuleKeysAsync(factory, expectedSource: TenantModuleSources.Seed)).Count);
    }

    private static async Task<List<string>> SeedModuleKeysAsync(QepApiFactory factory, string expectedSource)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var seedTenant = new TenantId(TenancySeeder.SeedTenantId);
        var rows = await dbContext.TenantModules
            .AsNoTracking()
            .Where(module => module.TenantId == seedTenant)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(rows, row => Assert.Equal(expectedSource, row.Source));
        return rows.Select(row => row.ModuleKey.Value).Order(StringComparer.Ordinal).ToList();
    }
```

- [ ] **Step 3: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantRegistrationServiceTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests"
```

Esperado: la unitaria no compila (`error CS1729: 'TenantRegistrationService' does not contain a constructor that takes 8 arguments`). Para correr las de integración sin la unitaria, el proyecto de integración compila aparte: `SignupStoresTheSixDefaultModules` falla con `Assert.Equal() Failure: Collections differ` (lista vacía), `SeedEnablesTheSevenModules` igual; `SignupWithTheSwitchOffStoresNoModule` y `SeedingTwiceDoesNotDuplicateTheModules` pasan por el motivo equivocado (no hay filas) o fallan por cantidad: se anota lo que salga.

- [ ] **Step 4: Implementar el signup**

`TenantRegistrationService.cs`, constructor:

```csharp
public sealed class TenantRegistrationService(
    ITenantRepository tenantRepository,
    IMembershipRepository membershipRepository,
    ITenantModuleRepository tenantModuleRepository,
    ITenantModuleDefaults tenantModuleDefaults,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock)
    : ITenantRegistration
```

Inmediatamente después de `tenantRepository.Add(tenant);` (`:42`):

```csharp
        // Spec 2026-10-07, «Alta por signup»: los módulos del tenant se commitean con el tenant y la
        // membresía en el SaveChangesAsync de abajo, así que un tenant nunca existe sin las filas que
        // le tocan. Cuáles, lo decide Entitlements:GrantDefaultModulesOnSignup.
        foreach (var key in tenantModuleDefaults.ForNewTenants)
        {
            tenantModuleRepository.Add(TenantModule.Create(tenantId, key, TenantModuleSources.Signup, now, note: null));
        }
```

- [ ] **Step 5: Implementar la semilla**

En `TenancySeeder.SeedTenantWithOwnerAsync`, después del `dbContext.Memberships.Add(...)` (`:89-95`) y antes del `SaveChangesAsync`:

```csharp
        // Spec 2026-10-07, «Semilla»: los siete, pos incluido, en la misma escritura que el tenant.
        // Sólo al crear: si el tenant ya existía el método devolvió arriba, y una base local vieja
        // prende pos con el SQL de «Operación» (README § Módulos por tenant).
        foreach (var key in TenantModuleKeys.All)
        {
            dbContext.TenantModules.Add(TenantModule.Create(id, key, TenantModuleSources.Seed, now, note: null));
        }
```

- [ ] **Step 6: Ver el GREEN**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantRegistrationServiceTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests|FullyQualifiedName~RegistrationApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todo en verde. `RegistrationApiTests` se corre como regresión del signup.

- [ ] **Step 7: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Tenancy/Modules.Tenancy.Application/TenantRegistrationService.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantRegistrationServiceTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs; git commit -m "feat(tenancy): el signup y la semilla dan de alta sus módulos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B5: Cada permiso declara sus módulos, y el enmascarado puro

**Files:**
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs:19-24,74-80`
- Create: `src/Modules/Authorization/Modules.Authorization.Application/ModuleEntitlementMask.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (los 35 `new PermissionDefinition(` de `:711-935` y el registro junto a `:567`)
- Modify: `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs:24-29`
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/ModuleEntitlementMaskTests.cs`, `RoleCatalogTests.cs`

**Interfaces:**
- Consumes: `TenantModuleKey`, `TenantModuleKeys`, `TenantModuleSet` (B1).
- Produces: `PermissionDefinition(string Permission, string DisplayName, string Description, string Category, string RiskLevel, IReadOnlyCollection<TenantModuleKey>? RequiredModules)`; `public sealed class ModuleEntitlementMask(IEnumerable<PermissionDefinition> registered) { bool Allows(string permission, TenantModuleSet modules); IReadOnlyCollection<string> Apply(IEnumerable<string> permissions, TenantModuleSet modules) }`, registrado singleton.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Authorization/Modules.Authorization.UnitTests/ModuleEntitlementMaskTests.cs`:

```csharp
using Modules.Authorization.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-07, «Dónde se enchufa el enmascarado».</summary>
public sealed class ModuleEntitlementMaskTests
{
    private static readonly ModuleEntitlementMask Mask = new(
    [
        Definition("tenancy.settings.read", []),
        Definition("catalog.product.read", [TenantModuleKeys.Catalog]),
        Definition("reporting.orders.read", [TenantModuleKeys.Reporting, TenantModuleKeys.Orders]),
        Definition("legacy.unmapped", null),
    ]);

    private static readonly TenantModuleSet Everything = TenantModuleSet.FromStored(TenantModuleKeys.All);

    private static PermissionDefinition Definition(string permission, TenantModuleKey[]? modules) =>
        new(permission, permission, "", "Test", "low", modules);

    private static TenantModuleSet AllBut(params TenantModuleKey[] missing) =>
        TenantModuleSet.FromStored(TenantModuleKeys.All.Except(missing));

    [Fact]
    public void CorePassesEvenWithNoModules()
    {
        Assert.Equal(["tenancy.settings.read"], Mask.Apply(["tenancy.settings.read"], TenantModuleSet.Empty));
    }

    [Fact]
    public void APermissionFallsWithItsModule()
    {
        Assert.Empty(Mask.Apply(["catalog.product.read"], AllBut(TenantModuleKeys.Catalog)));
        Assert.Equal(["catalog.product.read"], Mask.Apply(["catalog.product.read"], Everything));
    }

    // Conjunción: un reporte exige reporting y su fuente.
    [Fact]
    public void APermissionWithTwoModulesNeedsBoth()
    {
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Orders)));
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Reporting)));
        Assert.Equal(["reporting.orders.read"], Mask.Apply(["reporting.orders.read"], Everything));
    }

    // Por dependencia: sin customers, orders no es efectivo aunque esté contratado.
    [Fact]
    public void AMissingDependencyMasksToo()
    {
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Customers)));
    }

    [Fact]
    public void AnUnmappedPermissionAlwaysFalls()
    {
        Assert.Empty(Mask.Apply(["legacy.unmapped"], Everything));
    }

    // Un X-Permissions inventado o un permiso de un módulo futuro (POS) en un rol custom.
    [Fact]
    public void AnUnregisteredPermissionFalls()
    {
        Assert.Empty(Mask.Apply(["pos.sale.read"], Everything));
    }

    // El índice sale de las definiciones registradas, no de IRoleCatalog.ListPermissions(): esa lista
    // sólo trae lo que concede algún rol de sistema.
    [Fact]
    public void ARegisteredPermissionThatNoSystemRoleGrantsIsResolvedByItsDefinition()
    {
        var definitions = new[] { Definition("catalog.product.read", [TenantModuleKeys.Catalog]) };
        var catalog = new RoleCatalog([new RoleDefinition("advisor", "Asesor", "", "Tenancy", "low", [])], definitions);

        Assert.DoesNotContain(catalog.ListPermissions(), permission => permission.Permission == "catalog.product.read");
        Assert.Equal(
            ["catalog.product.read"],
            new ModuleEntitlementMask(definitions).Apply(["catalog.product.read"], Everything));
    }

    [Fact]
    public void ApplyRemovesRepeatedPermissions()
    {
        Assert.Equal(
            ["tenancy.settings.read"],
            Mask.Apply(["tenancy.settings.read", "tenancy.settings.read"], Everything));
    }
}
```

`tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCatalogTests.cs`:

```csharp
using Modules.Authorization.Application;

namespace Modules.Authorization.UnitTests;

public sealed class RoleCatalogTests
{
    // Un permiso de rol sin metadata registrada sale del fallback con RequiredModules = null: sin
    // mapear, y por lo tanto enmascarado siempre (spec 2026-10-07).
    [Fact]
    public void ThePermissionMetadataFallbackDeclaresNoModules()
    {
        var catalog = new RoleCatalog(
            [new RoleDefinition("advisor", "Asesor", "", "Tenancy", "low", ["custom.thing"])],
            []);

        var permission = Assert.Single(catalog.ListPermissions());
        Assert.Equal("custom.thing", permission.Permission);
        Assert.Null(permission.RequiredModules);
    }
}
```

En `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, después de `TheTenantRoleCatalogIsScopedAndTheSystemOneIsNot` (agregar `using Modules.Tenancy.Domain;` no hace falta; sí `using System.Reflection;`, que ya está):

```csharp
    /// <summary>
    /// Spec 2026-10-07, criterio 4: un permiso sin módulo declarado se enmascara siempre, así que
    /// olvidarlo deja a todos sin ese permiso en silencio. Como el registro es a mano, esta prueba
    /// lo convierte en regla: toda constante de toda clase <c>*Permissions</c> de los ensamblados
    /// Application tiene su <see cref="PermissionDefinition"/> con <c>RequiredModules</c> no nulo.
    /// </summary>
    [Fact]
    public void EveryPermissionConstantDeclaresItsModules()
    {
        var definitions = RegisteredPermissionDefinitions();

        var unmapped = PermissionConstants()
            .Where(permission =>
                !definitions.TryGetValue(permission, out var definition) || definition.RequiredModules is null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            "Estos permisos no tienen PermissionDefinition con RequiredModules en "
                + "QepServiceCollectionExtensions, así que se enmascaran siempre: "
                + string.Join(", ", unmapped));
    }

    /// <summary>Ningún permiso de un rol de sistema sale del fallback de metadata de RoleCatalog.</summary>
    [Fact]
    public void NoSystemRolePermissionComesFromTheMetadataFallback()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Empty(catalog.ListPermissions()
            .Where(permission => permission.RequiredModules is null)
            .Select(permission => permission.Permission));
    }

    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec; el spec
    /// de POS sube este número cuando declare las suyas.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheThirtyFiveConstants()
    {
        Assert.Equal(35, PermissionConstants().Length);
    }

    [Fact]
    public void TheContainerCanBuildTheEntitlementMask()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ModuleEntitlementMask>());
    }

    private static Dictionary<string, PermissionDefinition> RegisteredPermissionDefinitions() =>
        BuildPlatformServices()
            .Where(descriptor => descriptor.ServiceType == typeof(PermissionDefinition))
            .Select(descriptor => (PermissionDefinition)descriptor.ImplementationInstance!)
            .ToDictionary(definition => definition.Permission, StringComparer.Ordinal);

    private static string[] PermissionConstants() =>
        ApplicationAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: true, IsSealed: true } &&
                type.Name.EndsWith("Permissions", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~ModuleEntitlementMaskTests|FullyQualifiedName~RoleCatalogTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: no compilan: `error CS0246: The type or namespace name 'ModuleEntitlementMask' could not be found`, `error CS1729: 'PermissionDefinition' does not contain a constructor that takes 6 arguments` y `error CS1061: 'PermissionDefinition' does not contain a definition for 'RequiredModules'`.

- [ ] **Step 3: `RequiredModules` en `PermissionDefinition`**

`RoleCatalog.cs:19-24` pasa a (agregar `using Modules.Tenancy.Domain;` arriba):

```csharp
/// <param name="RequiredModules">
/// Los módulos que exige el permiso, en conjunción (spec 2026-10-07, «Mapa permiso → módulo").
/// <c>[]</c> es núcleo. <c>null</c> es «sin mapear» y sólo lo produce el fallback de metadata de
/// <see cref="RoleCatalog"/>: un permiso sin mapear se enmascara siempre. Posicional y sin default
/// para que el compilador obligue a declararlo donde nace el permiso. No entra en
/// <c>CatalogVersion</c>: el hash describe el catálogo del build, no el de un tenant.
/// </param>
public sealed record PermissionDefinition(
    string Permission,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<TenantModuleKey>? RequiredModules);
```

En el fallback (`:75-80`):

```csharp
                new PermissionDefinition(
                    permission,
                    permission,
                    "Permission registered by a module without catalog metadata.",
                    "uncategorized",
                    "medium",
                    RequiredModules: null))
```

`ComputeCatalogVersion` no se toca.

- [ ] **Step 4: El enmascarado**

`src/Modules/Authorization/Modules.Authorization.Application/ModuleEntitlementMask.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Descarta los permisos cuyo módulo no está efectivamente habilitado (spec 2026-10-07, «Decisión»).
/// Como cada endpoint exige su permiso con <c>RequireClaim</c> y cada handler lo revalida por claims,
/// un permiso enmascarado es un 403 en las dos capas, y la SPA lo esconde sola.
///
/// Indexa las <see cref="PermissionDefinition"/> **registradas** en el contenedor, no
/// <c>IRoleCatalog.ListPermissions()</c>: esa lista sólo trae lo que concede algún rol de sistema, y
/// un permiso que llega por un rol custom o por <c>X-Permissions</c> quedaría sin índice. Un permiso
/// no registrado, o registrado sin módulos (<c>null</c>), se enmascara. Puro y singleton.
/// </summary>
public sealed class ModuleEntitlementMask(IEnumerable<PermissionDefinition> registered)
{
    private readonly Dictionary<string, IReadOnlyCollection<TenantModuleKey>?> _requiredModules =
        registered.ToDictionary(
            definition => definition.Permission,
            definition => definition.RequiredModules,
            StringComparer.Ordinal);

    public bool Allows(string permission, TenantModuleSet modules) =>
        _requiredModules.TryGetValue(permission, out var required) &&
        required is not null &&
        required.All(modules.IsEnabled);

    public IReadOnlyCollection<string> Apply(IEnumerable<string> permissions, TenantModuleSet modules) =>
        permissions
            .Where(permission => Allows(permission, modules))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
```

- [ ] **Step 5: Los 35 mapas en el composition root**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agregar arriba, con los demás `using`:

```csharp
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;
```

(alias y no `using Modules.Tenancy.Domain;`: el archivo importa los namespaces Application de once módulos y el alias no abre ninguna ambigüedad). Registrar el mask junto al catálogo, después de `services.AddSingleton<IRoleCatalog, RoleCatalog>();` (`:567`):

```csharp
        // Spec 2026-10-07: puro y sin estado por tenant, así que singleton como el catálogo del
        // código. Indexa las PermissionDefinition registradas abajo.
        services.AddSingleton<ModuleEntitlementMask>();
```

A cada uno de los 35 `services.AddSingleton(new PermissionDefinition(...))` (`:711-935`) se le agrega un sexto argumento nombrado, después del nivel de riesgo. Por ejemplo, `SettingsRead` (`:711-716`) queda:

```csharp
        services.AddSingleton(new PermissionDefinition(
            TenancyPermissions.SettingsRead,
            "Leer configuración",
            "Permite consultar la configuración del tenant.",
            "Tenancy",
            "low",
            RequiredModules: []));
```

y `ProductRead` (`:771-776`):

```csharp
        services.AddSingleton(new PermissionDefinition(
            CatalogPermissions.ProductRead,
            // (las cuatro líneas de texto existentes, sin cambios)
            RequiredModules: [ModuleKeys.Catalog]));
```

El valor de cada uno, en el orden del archivo (línea de la constante en `origin/develop`):

| Línea | Constante | `RequiredModules:` |
|---|---|---|
| 712 | `TenancyPermissions.SettingsRead` | `[]` |
| 718 | `TenancyPermissions.SettingsUpdate` | `[]` |
| 724 | `TenancyPermissions.AdvisorshipInvite` | `[]` |
| 730 | `TenancyPermissions.AdvisorshipRead` | `[]` |
| 736 | `TenancyPermissions.AdvisorshipManage` | `[]` |
| 742 | `TenancyPermissions.AdvisorshipRolesManage` | `[]` |
| 748 | `StoragePermissions.FileUpload` | `[]` |
| 754 | `StoragePermissions.FileRead` | `[]` |
| 760 | `StoragePermissions.FileDelete` | `[]` |
| 766 | `StoragePermissions.FilePublish` | `[]` |
| 772 | `CatalogPermissions.ProductRead` | `[ModuleKeys.Catalog]` |
| 778 | `CatalogPermissions.ProductManage` | `[ModuleKeys.Catalog]` |
| 784 | `CatalogPermissions.TaxRateRead` | `[ModuleKeys.Catalog]` |
| 790 | `CatalogPermissions.TaxRateManage` | `[ModuleKeys.Catalog]` |
| 796 | `CompaniesPermissions.CompanyRead` | `[ModuleKeys.Companies]` |
| 802 | `CompaniesPermissions.CompanyManage` | `[ModuleKeys.Companies]` |
| 808 | `CustomersPermissions.CustomerRead` | `[ModuleKeys.Customers]` |
| 814 | `CustomersPermissions.CustomerManage` | `[ModuleKeys.Customers]` |
| 820 | `CustomersPermissions.CustomerImport` | `[ModuleKeys.Customers]` |
| 829 | `CustomersPermissions.ClassificationRead` | `[ModuleKeys.Customers]` |
| 835 | `CustomersPermissions.ClassificationManage` | `[ModuleKeys.Customers]` |
| 841 | `QuotationsPermissions.QuotationRead` | `[ModuleKeys.Quotations]` |
| 847 | `QuotationsPermissions.QuotationManage` | `[ModuleKeys.Quotations]` |
| 853 | `OrdersPermissions.OrderRead` | `[ModuleKeys.Orders]` |
| 859 | `OrdersPermissions.OrderManage` | `[ModuleKeys.Orders]` |
| 866 | `OrdersPermissions.OrderApprove` | `[ModuleKeys.Orders]` |
| 874 | `OrdersPermissions.OrderCancel` | `[ModuleKeys.Orders]` |
| 882 | `OrdersPermissions.OrderInvoice` | `[ModuleKeys.Orders]` |
| 888 | `ReportingPermissions.OrdersRead` | `[ModuleKeys.Reporting, ModuleKeys.Orders]` |
| 894 | `ReportingPermissions.QuotationRead` | `[ModuleKeys.Reporting, ModuleKeys.Quotations]` |
| 900 | `ReportingPermissions.PriceChangeRead` | `[ModuleKeys.Reporting, ModuleKeys.Catalog]` |
| 908 | `ReportingPermissions.CustomerRead` | `[ModuleKeys.Reporting, ModuleKeys.Customers]` |
| 917 | `ReportingPermissions.AllAdvisorsRead` | `[ModuleKeys.Reporting]` |
| 925 | `PlatformPermissions.RequestLogRead` | `[]` |
| 931 | `PlatformPermissions.RequestLogPurge` | `[]` |

Encima de `ReportingPermissions.AllAdvisorsRead` agregar el porqué:

```csharp
        // Spec 2026-10-07: sólo reporting. No abre ningún reporte por sí solo; amplía los de pedidos
        // y cotizaciones, que ya están enmascarados por su fuente.
```

En `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs:24-29`, los tres `new PermissionDefinition(...)` ganan su último argumento (los módulos no son lo que esas pruebas ejercen):

```csharp
                new PermissionDefinition(
                    "advisorship.manage", "Gestionar miembros", "", "Tenancy", "high", RequiredModules: []),
                new PermissionDefinition(
                    "advisorship.roles.manage", "Definir roles", "", "Tenancy", "high", RequiredModules: []),
                new PermissionDefinition(
                    "catalog.product.read", "Ver productos", "", "Catalog", "low", RequiredModules: []),
```

- [ ] **Step 6: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: build sin errores (si alguno de los 35 quedó sin sexto argumento, `CS7036`); las dos suites en verde, con `EveryPermissionConstantDeclaresItsModules`, `NoSystemRolePermissionComesFromTheMetadataFallback`, `PermissionDiscoveryFindsTheThirtyFiveConstants` y `TheContainerCanBuildTheEntitlementMask` superadas.

- [ ] **Step 7: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs src/Modules/Authorization/Modules.Authorization.Application/ModuleEntitlementMask.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCommandsTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/ModuleEntitlementMaskTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCatalogTests.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs; git commit -m "feat(authorization): cada permiso declara los módulos que exige"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B6: Enmascarar en los dos caminos de autenticación

**Files:**
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs`
- Modify: `src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs`
- Modify: `tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs`
- Modify: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs:45-71`
- Modify: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs:185-202`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs`

**Interfaces:**
- Consumes: `ITenantModules` (B1/B3), `ModuleEntitlementMask` (B5), helpers de `TenantModulesApiTests` (B4).
- Produces: `AuthorizationService(IMembershipDirectory, ITenantRoleCatalog, ITenantModules, ModuleEntitlementMask)`; `QuotationsApiHarness.DisableModuleAsync(QepApiFactory, Guid tenantId, TenantModuleKey)` y `EnableModuleAsync(...)`, que reusan B7, B9 y B12.

- [ ] **Step 1: Pruebas unitarias del camino real**

En `AuthorizationServiceTests.cs`: agregar `using Modules.Tenancy.Domain;`, un rol que concede un permiso de módulo, el mask, el doble de módulos y un constructor de servicio. El `Catalog` estático pasa a:

```csharp
    private static readonly RoleCatalog Catalog = new(
    [
        new RoleDefinition("admin",
            "Owner",
            "Owner role",
            "Tenancy",
            "high",
            ["tenancy.settings.read", "tenancy.settings.update", "advisorship.invite"]),
        new RoleDefinition("advisor",
            "Member",
            "Member role",
            "Tenancy",
            "medium",
            ["tenancy.settings.read"]),
        new RoleDefinition("seller",
            "Seller",
            "Reads the catalog",
            "Catalog",
            "low",
            ["tenancy.settings.read", "catalog.product.read"]),
    ],
    []);

    private static readonly ModuleEntitlementMask Mask = new(
    [
        new PermissionDefinition("tenancy.settings.read", "", "", "Tenancy", "low", []),
        new PermissionDefinition("tenancy.settings.update", "", "", "Tenancy", "high", []),
        new PermissionDefinition("advisorship.invite", "", "", "Tenancy", "medium", []),
        new PermissionDefinition("catalog.product.read", "", "", "Catalog", "low", [TenantModuleKeys.Catalog]),
    ]);

    private static AuthorizationService NewService(
        IReadOnlyCollection<string>? roles, TenantModuleSet? modules) =>
        new(new FakeDirectory(roles), TenantCatalog(), new FixedTenantModules(modules), Mask);

    // Los casos de antes no son sobre módulos: con los siete prendidos significan lo mismo que
    // antes. Con null no, porque en el camino real null es fail closed (sólo núcleo).
    private static AuthorizationService NewService(IReadOnlyCollection<string>? roles) =>
        NewService(roles, TenantModuleSet.FromStored(TenantModuleKeys.All));
```

Las cuatro construcciones existentes (`:47`, `:59-60`, `:73-74`, `:90-91`) pasan a `NewService(null)`, `NewService(["admin"])`, `NewService(["advisor"])` y `NewService(["admin", "advisor"])`. Al final de la clase, el doble:

```csharp
    private sealed class FixedTenantModules(TenantModuleSet? set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(set);
    }
```

Y tres casos nuevos:

```csharp
    [Fact]
    public async Task ResolvePermissionsMasksTheOnesOfAModuleThatIsOff()
    {
        var service = NewService(
            ["seller"], TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Catalog])));

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }

    // Hay membresía activa, luego hay tenant: null no debería pasar. Si pasa, fail closed.
    [Fact]
    public async Task WithoutATenantRowOnlyCoreSurvives()
    {
        var service = NewService(["seller"], modules: null);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }

    [Fact]
    public async Task AuthorizeDeniesAMaskedPermission()
    {
        var service = NewService(["seller"], TenantModuleSet.Empty);

        var decision = await service.AuthorizeAsync(
            Subject, Tenant, "catalog.product.read", TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal("permission_denied", decision.ReasonCode);
    }
```

- [ ] **Step 2: Pruebas de integración del enmascarado**

En `TenantModulesApiTests.cs` (B4), tres casos y un helper (agregar `using Modules.Tenancy.Domain;` si no está):

```csharp
    // Criterio de "Cuándo el stub enmascara": con fila en tenancy.tenants, enmascara.
    [Fact]
    public async Task TheStubMasksARegisteredTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId, "catalog.product.read", "tenancy.settings.read");
        Assert.Contains("catalog.product.read", await EffectivePermissionsAsync(client, tenantId));

        await DisableAsync(factory, tenantId, TenantModuleKeys.Catalog);

        var permissions = await EffectivePermissionsAsync(client, tenantId);
        Assert.DoesNotContain("catalog.product.read", permissions);
        Assert.Contains("tenancy.settings.read", permissions);
    }

    [Fact]
    public async Task WithTheSwitchOffTheOwnerOnlyKeepsCorePermissions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), grantDefaultModulesOnSignup: false);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(
            factory, ownerId, tenantId, "tenancy.settings.read", "catalog.product.read", "quotations.quotation.read");

        Assert.Equal(["tenancy.settings.read"], await EffectivePermissionsAsync(client, tenantId));
    }

    // Sin fila (tenant simulado): no enmascara, para no romper las suites que nunca registran tenant.
    [Fact]
    public async Task TheStubLeavesASimulatedTenantAlone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "catalog.product.read");

        Assert.Equal(["catalog.product.read"], await EffectivePermissionsAsync(client, tenantId));
    }

    internal static async Task<string[]> EffectivePermissionsAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetFromJsonAsync<EffectivePermissionsDto>(
            $"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response.Permissions;
    }

    private sealed record EffectivePermissionsDto(Guid TenantId, string[] Permissions);
```

En `RealAuthenticationApiTests.cs`, un caso por cookie (agregar `using Microsoft.EntityFrameworkCore;`, `using Microsoft.Extensions.DependencyInjection;`, `using Modules.Tenancy.Domain;` y `using Modules.Tenancy.Infrastructure.Persistence;`):

```csharp
    // Spec 2026-10-07: el camino real, ExternalClaimsTransformation -> AuthorizationService. Mismo
    // cliente y misma cookie antes y después: los permisos se resuelven en cada request.
    [Fact]
    public async Task TurningCatalogOffForbidsProductsThroughTheCookie()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(owner, tenantId)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var id = new TenantId(tenantId);
            await dbContext.TenantModules
                .Where(module => module.TenantId == id && module.ModuleKey == TenantModuleKeys.Catalog)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await GetProductsAsync(owner, tenantId)).StatusCode);
    }

    private static async Task<HttpResponseMessage> GetProductsAsync(HttpClient client, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/catalog/products");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
```

En `QuotationsApiHarness.cs`, dos helpers públicos (nombres calificados para no importar `Modules.Tenancy.Domain` en un archivo que ya usa los tipos de Quotations; agregar `using Microsoft.EntityFrameworkCore;` si falta):

```csharp
    /// <summary>Spec 2026-10-07: apaga un módulo borrando su fila, como lo hace QCode por SQL.</summary>
    public static async Task DisableModuleAsync(
        QepApiFactory factory, Guid tenantId, Modules.Tenancy.Domain.TenantModuleKey key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(tenantId);
        await dbContext.TenantModules
            .Where(module => module.TenantId == id && module.ModuleKey == key)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Lo prende otra vez con origen <c>manual</c>, el que escribe el SQL de «Operación».</summary>
    public static async Task EnableModuleAsync(
        QepApiFactory factory, Guid tenantId, Modules.Tenancy.Domain.TenantModuleKey key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>();
        dbContext.TenantModules.Add(Modules.Tenancy.Domain.TenantModule.Create(
            new Modules.Tenancy.Domain.TenantId(tenantId), key, "manual", DateTimeOffset.UtcNow, note: null));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Módulos por tenant sobre cotizaciones y pedidos (spec 2026-10-07): un tenant registrado nace con
/// los seis, y apagar uno corta el acceso en el request siguiente.
/// </summary>
public sealed class TenantModulesQuotationsApiTests
{
    [Fact]
    public async Task QuotationsAreForbiddenOnceTheModuleIsTurnedOff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task EffectivePermissionsDropQuotationsOnceTurnedOff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        var permissions = await EffectivePermissionsAsync(client, tenantId);
        Assert.DoesNotContain(permissions, permission => permission.StartsWith("quotations.quotation.", StringComparison.Ordinal));
        Assert.Contains(CustomersPermissions.CustomerRead, permissions);
    }

    // Review Focus 1: volver a prender devuelve todo, en el request siguiente y sin reiniciar.
    [Fact]
    public async Task TurningCustomersBackOnRestoresQuotations()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Customers);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);

        await EnableModuleAsync(factory, tenantId, ModuleKeys.Customers);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(QuotationsUrl(tenantId), TestContext.Current.CancellationToken)).StatusCode);
    }

    // Regresión del spec: el dato de otro módulo dentro de una respuesta de un módulo prendido.
    // ListQuotationsHandler ya pide los pedidos sólo con quotations.order.read
    // (ListQuotations.cs:170-177), y ese permiso queda enmascarado sin orders.
    [Fact]
    public async Task TheListHidesTheOrderOnceOrdersIsTurnedOff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        Assert.NotNull(Assert.Single((await ListAsync(client, tenantId)).Items).OrderId);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var row = Assert.Single((await ListAsync(client, tenantId)).Items);
        Assert.Null(row.OrderId);
        Assert.Null(row.OrderStatus);
    }

    private static async Task<QuotationsPageResponse> ListAsync(HttpClient client, Guid tenantId)
    {
        var page = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<string[]> EffectivePermissionsAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetFromJsonAsync<EffectivePermissionsDto>(
            $"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response.Permissions;
    }

    private sealed record EffectivePermissionsDto(Guid TenantId, string[] Permissions);
}
```

Si `CreateSentQuotationAsync` tiene otra firma que `(client, factory, tenantId, customerId, productId)`, usar la que tenga (`QuotationsApiHarness.cs:576`); `NewSentQuotationAsync` de `OrderPaymentProofPublicationApiTests.cs:1154-1160` es la referencia.

- [ ] **Step 3: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~AuthorizationServiceTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~RealAuthenticationApiTests.TurningCatalogOff"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TenantModulesQuotationsApiTests"
```

Esperado: la unitaria no compila (`CS1729: 'AuthorizationService' does not contain a constructor that takes 4 arguments`). En integración: `TheStubMasksARegisteredTenant`, `WithTheSwitchOffTheOwnerOnlyKeepsCorePermissions`, `TurningCatalogOffForbidsProductsThroughTheCookie` y las cuatro de Quotations fallan (200 donde se espera 403, o el permiso sigue en la lista, o `OrderId` no es `null`); `TheStubLeavesASimulatedTenantAlone` y las de B4 pasan.

- [ ] **Step 4: Enmascarar en `AuthorizationService`**

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed class AuthorizationService(
    IMembershipDirectory membershipDirectory,
    ITenantRoleCatalog roleCatalog,
    ITenantModules tenantModules,
    ModuleEntitlementMask entitlementMask)
    : IAuthorizationService
{
```

Y el final de `ResolvePermissionsAsync` (`:42-44`) pasa a:

```csharp
        // Paso 2: resolver los permisos acotados al tenant — de sistema y custom. DirectGrant
        // y la Policy contextual quedan diferidos (ver docs/decisions/0002).
        var permissions = await roleCatalog.PermissionsForAsync(tenantId, roles, cancellationToken);

        // Paso 3 (spec 2026-10-07): descartar los de módulos que el tenant no tiene. null no
        // debería pasar —hay membresía activa, luego hay tenant—; si pasa, fail closed: sólo núcleo.
        // AuthorizeAsync hereda el enmascarado.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken) ?? TenantModuleSet.Empty;
        return entitlementMask.Apply(permissions, modules);
```

- [ ] **Step 5: Enmascarar en el stub**

`DevelopmentAuthenticationHandler.cs` pasa a:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper.Authentication;

// Los handlers de autenticación se resuelven por request, así que admiten dependencias scoped como
// ITenantModules.
internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITenantModules tenantModules,
    ModuleEntitlementMask entitlementMask)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "Development";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subjectId = Request.Headers["X-Subject-Id"].ToString();
        var tenantId = Request.Headers["X-Tenant-Id"].ToString();
        if (!Guid.TryParse(subjectId, out _) || !Guid.TryParse(tenantId, out var parsedTenantId))
        {
            // Sin tenant no hay consulta de módulos: el request ni siquiera autentica.
            return AuthenticateResult.Fail(
                "Development requests require valid X-Subject-Id and X-Tenant-Id headers.");
        }

        List<Claim> claims =
        [
            new(QepClaimTypes.SubjectId, subjectId),
            new(QepClaimTypes.TenantId, tenantId)
        ];

        // Spec 2026-10-07, «Cuándo el stub enmascara»: el criterio es si el tenant tiene fila en
        // tenancy.tenants. Sin fila (tenant simulado por casi todas las suites de Catalog, Customers
        // y Companies) los permisos quedan como vienen; con fila, se enmascaran igual que por cookie.
        var requested = ResolvePermissions();
        var modules = await tenantModules.FindAsync(parsedTenantId, Context.RequestAborted);
        var permissions = modules is null ? requested : entitlementMask.Apply(requested, modules);
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(QepClaimTypes.Permission, permission));
        }
```

El resto del método (claims de email y el `AuthenticateResult.Success`) queda igual, cambiando los dos `return Task.FromResult(...)` por `return ...` directo. `ResolvePermissions()` no cambia.

- [ ] **Step 6: Ver qué rompe en las suites que insertan su tenant**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerExportApiTests"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductExportApiTests"
```

Esperado (RED que predijo el spec, «Pruebas que insertan tenants directamente»): los casos que llaman `SeedTenantAsync` fallan con `403 (Forbidden)`, porque el tenant sembrado ya tiene fila y ninguna de módulos.

- [ ] **Step 7: Sembrar los módulos en esas dos suites**

`CustomersApiHarness.SeedTenantAsync` (`:45-71`) pasa a (agregar `using Microsoft.EntityFrameworkCore;`):

```csharp
    // Este harness usa un tenant que no pasa por el registro. ExportCustomersHandler nombra el
    // archivo con la hora del tenant (spec 2026-09-17, punto 8a), y sin su fila en
    // tenancy.tenants TenantClock responde tenancy.tenant.not_found.
    //
    // Spec 2026-10-07: con fila, el stub enmascara, así que el tenant también necesita sus módulos.
    // Los siete y no sólo customers: TenantId (...0001) es el tenant de desarrollo que comparten
    // TODAS las pruebas de Customers, y una vez sembrado el stub lo enmascara para cualquier request
    // contra esta base, pida el permiso que pida. Inserta sólo las claves que faltan, así que se
    // puede llamar dos veces sobre la misma base (o sobre un tenant ya sembrado sin módulos).
    public static async Task SeedTenantAsync(QepApiFactory factory, string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        var existing = await tenancy.Tenants.FindAsync(
            [id], TestContext.Current.CancellationToken);
        if (existing is null)
        {
            tenancy.Tenants.Add(Modules.Tenancy.Domain.Tenant.Create(
                id,
                "customers-export-tests",
                "Customers Export Tests",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                Modules.Tenancy.Domain.MembershipId.New(),
                now));
        }

        var stored = await tenancy.TenantModules
            .Where(module => module.TenantId == id)
            .Select(module => module.ModuleKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var key in Modules.Tenancy.Domain.TenantModuleKeys.All.Except(stored))
        {
            tenancy.TenantModules.Add(Modules.Tenancy.Domain.TenantModule.Create(
                id, key, Modules.Tenancy.Domain.TenantModuleSources.Seed, now, note: null));
        }

        await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

`ProductExportApiTests.SeedTenantAsync` (`:185-202`) pasa a:

```csharp
    // Este archivo usa un tenant que no pasa por el registro. ExportProductsHandler nombra el archivo
    // con la hora del tenant (spec 2026-09-17, punto 8a), y sin su fila en tenancy.tenants
    // TenantClock responde tenancy.tenant.not_found. Spec 2026-10-07: con fila el stub enmascara, así
    // que el tenant nace con sus siete módulos, en el mismo SaveChangesAsync.
    private static async Task SeedTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId));
        var now = DateTimeOffset.UtcNow;
        tenancy.Tenants.Add(Modules.Tenancy.Domain.Tenant.Create(
            id,
            "catalog-export-tests",
            "Catalog Export Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            Modules.Tenancy.Domain.MembershipId.New(),
            now));
        foreach (var key in Modules.Tenancy.Domain.TenantModuleKeys.All)
        {
            tenancy.TenantModules.Add(Modules.Tenancy.Domain.TenantModule.Create(
                id, key, Modules.Tenancy.Domain.TenantModuleSources.Seed, now, note: null));
        }

        await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 8: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~RealAuthenticationApiTests|FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~TenantSettingsApiTests|FullyQualifiedName~TenantLogoApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TenantModulesQuotationsApiTests|FullyQualifiedName~QuotationListApiTests"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerExportApiTests"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductExportApiTests"
```

Esperado: todo en verde. `AuthSessionApiTests`, `TenantSettingsApiTests` y `TenantLogoApiTests` van como regresión: insertan su tenant y usan sólo núcleo.

- [ ] **Step 9: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs src/Bootstrapper/Authentication/DevelopmentAuthenticationHandler.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/AuthorizationServiceTests.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs; git commit -m "feat(authorization): enmascarar los permisos de módulos apagados"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B7: El catálogo de roles no ofrece permisos de módulos apagados

**Files:**
- Modify: `src/Api/AuthorizationCatalogEndpoints.cs:80-111`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs`

**Interfaces:**
- Consumes: `ITenantModules`, `ModuleEntitlementMask.Allows` (B5), `DisableModuleAsync` (B6).

- [ ] **Step 1: Escribir la prueba que falla**

En `TenantModulesQuotationsApiTests.cs`:

```csharp
    // Spec 2026-10-07, «Catálogo de roles filtrado»: ni en permissions ni en los roles. El editor de
    // roles sólo pinta checkboxes del catálogo, así que un permiso de un módulo apagado no se ofrece.
    [Fact]
    public async Task TheRoleCatalogHidesThePermissionsOfAModuleThatIsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.AdvisorshipRead]);
        using var _ = client;
        Assert.Contains(
            (await CatalogAsync(client, tenantId)).Permissions,
            permission => permission.Permission == QuotationsPermissions.QuotationRead);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Quotations);

        var catalog = await CatalogAsync(client, tenantId);
        Assert.DoesNotContain(
            catalog.Permissions,
            permission => permission.Permission.StartsWith("quotations.", StringComparison.Ordinal));
        Assert.All(catalog.Roles, role => Assert.DoesNotContain(
            role.Permissions, permission => permission.StartsWith("quotations.", StringComparison.Ordinal)));
        Assert.Contains(
            catalog.Permissions, permission => permission.Permission == CustomersPermissions.CustomerRead);
    }

    private static async Task<CatalogDto> CatalogAsync(HttpClient client, Guid tenantId)
    {
        var catalog = await client.GetFromJsonAsync<CatalogDto>(
            $"/api/v1/tenants/{tenantId}/authorization/catalog", TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        return catalog;
    }

    private sealed record CatalogDto(string CatalogVersion, CatalogRoleDto[] Roles, CatalogPermissionDto[] Permissions);

    private sealed record CatalogRoleDto(string Role, string[] Permissions);

    private sealed record CatalogPermissionDto(string Permission);
```

(`quotations.order.*` también empieza con `quotations.` y también cae: `orders` depende de `quotations`.)

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TheRoleCatalogHidesThePermissionsOfAModuleThatIsOff"
```

Esperado: falla en el primer `Assert.DoesNotContain` (el catálogo todavía lista `quotations.quotation.read`).

- [ ] **Step 3: Filtrar el catálogo**

`GetCatalogAsync` pasa a:

```csharp
    // Spec 2026-10-07, «Catálogo de roles filtrado»: con el tenant en tenancy.tenants, saca de
    // permissions y de cada roles[].permissions lo que el enmascarado descartaría. No se filtran
    // /authorization/roles ni EnsureKnownPermissions: son lo guardado, y si el módulo vuelve el rol
    // vuelve a conceder lo que concedía. CatalogVersion no cambia: describe el catálogo del build.
    private static async Task<IResult> GetCatalogAsync(
        Guid tenantId,
        IExecutionContext executionContext,
        IRoleCatalog roleCatalog,
        ITenantModules tenantModules,
        ModuleEntitlementMask entitlementMask,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId != new TenantId(tenantId))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the authorization catalog for this tenant.");
        }

        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        bool Offered(string permission) => modules is null || entitlementMask.Allows(permission, modules);

        return Results.Ok(new AuthorizationCatalogResponse(
            roleCatalog.CatalogVersion,
            roleCatalog.ListRoles()
                .Select(role => new RoleCatalogItemResponse(
                    role.Role,
                    role.DisplayName,
                    role.Description,
                    role.Category,
                    role.RiskLevel,
                    role.Permissions
                        .Where(Offered)
                        .OrderBy(permission => permission, StringComparer.Ordinal)
                        .ToArray()))
                .ToArray(),
            roleCatalog.ListPermissions()
                .Where(permission => Offered(permission.Permission))
                .Select(permission => new PermissionCatalogItemResponse(
                    permission.Permission,
                    permission.DisplayName,
                    permission.Description,
                    permission.Category,
                    permission.RiskLevel))
                .ToArray()));
    }
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TenantModulesQuotationsApiTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests"
```

Esperado: verde las dos (la segunda es la regresión del catálogo con los seis prendidos).

- [ ] **Step 5: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Api/AuthorizationCatalogEndpoints.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs; git commit -m "feat(authorization): el catálogo de roles oculta los permisos de módulos apagados"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B8: `GET /tenants/{id}/modules` y la operación en el README

**Files:**
- Modify: `src/Bootstrapper/Authentication/QepAuthenticationMode.cs`
- Create: `src/Api/TenantModulesEndpoints.cs`
- Modify: `src/Api/Program.cs:125` (después de `app.MapAuthorizationCatalogEndpoints();`)
- Modify: `README.md` (sección nueva antes de `## API implementada`, `:612`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`

**Interfaces:**
- Consumes: `ITenantModules`, `TenantModuleSet`, `TenantModuleKeys` (B1), helpers de `TenantModulesApiTests` (B4, B6).
- Produces: `QepAuthenticationMode.IsDevelopmentStub(ClaimsPrincipal) : bool`; `TenantModulesResponse(Guid TenantId, IReadOnlyList<TenantModuleResponse> Modules)` con `static From(Guid tenantId, TenantModuleSet? modules, bool isDevelopmentStub)`; `TenantModuleResponse(string Key, bool Enabled, bool Contracted, IReadOnlyList<string> MissingDependencies)`. El JSON es el contrato del frontend (sección «Contrato HTTP nuevo»).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `TenantModulesApiTests.cs` (agregar `using Api;`):

```csharp
    [Fact]
    public async Task ModulesListsTheSevenInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        var modules = await ModulesAsync(client, tenantId);

        Assert.Equal(tenantId, modules.TenantId);
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos"],
            modules.Modules.Select(module => module.Key));
        Assert.All(modules.Modules.Where(module => module.Key != "pos"), module =>
        {
            Assert.True(module.Enabled);
            Assert.True(module.Contracted);
            Assert.Empty(module.MissingDependencies);
        });
        var pos = modules.Modules.Single(module => module.Key == "pos");
        Assert.False(pos.Enabled);
        Assert.False(pos.Contracted);
    }

    [Fact]
    public async Task TurningCustomersOffNamesItAsTheRootCause()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        await DisableAsync(factory, tenantId, TenantModuleKeys.Customers);

        var modules = (await ModulesAsync(client, tenantId)).Modules.ToDictionary(module => module.Key);
        Assert.False(modules["customers"].Contracted);
        Assert.Empty(modules["customers"].MissingDependencies);
        foreach (var key in new[] { "quotations", "orders" })
        {
            Assert.False(modules[key].Enabled);
            Assert.True(modules[key].Contracted);
            Assert.Equal(["customers"], modules[key].MissingDependencies);
        }
    }

    [Fact]
    public async Task ASimulatedTenantSeesEverythingOn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId);

        var modules = await ModulesAsync(client, tenantId);

        Assert.Equal(7, modules.Modules.Count);
        Assert.All(modules.Modules, module =>
        {
            Assert.True(module.Enabled);
            Assert.True(module.Contracted);
        });
    }

    [Fact]
    public async Task ModulesOfAnotherTenantAreForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{Guid.CreateVersion7()}/modules", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Sin HTTP: por cookie real, null exige un tenant sin fila con una membresía activa, que no se
    // puede armar por la API.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutARowTheAnswerDependsOnTheScheme(bool isDevelopmentStub)
    {
        var tenantId = Guid.CreateVersion7();

        var response = TenantModulesResponse.From(tenantId, modules: null, isDevelopmentStub);

        Assert.Equal(7, response.Modules.Count);
        Assert.All(response.Modules, module =>
        {
            Assert.Equal(isDevelopmentStub, module.Enabled);
            Assert.Equal(isDevelopmentStub, module.Contracted);
            Assert.Empty(module.MissingDependencies);
        });
    }

    private static async Task<ModulesDto> ModulesAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/modules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var modules = await response.Content.ReadFromJsonAsync<ModulesDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(modules);
        return modules;
    }

    private sealed record ModulesDto(Guid TenantId, List<ModuleDto> Modules);

    private sealed record ModuleDto(string Key, bool Enabled, bool Contracted, string[] MissingDependencies);
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests"
```

Esperado: no compila: `error CS0103: The name 'TenantModulesResponse' does not exist in the current context`.

- [ ] **Step 3: Implementar**

En `QepAuthenticationMode.cs` (agregar `using System.Security.Claims;`):

```csharp
    /// <summary>
    /// Si el principal lo autenticó el stub por headers. Misma comparación que
    /// <c>ExternalClaimsTransformation</c>; pública porque <see cref="DevelopmentAuthenticationHandler"/>
    /// es <c>internal</c> y <c>/modules</c> vive en <c>src/Api</c> (spec 2026-10-07).
    /// </summary>
    public static bool IsDevelopmentStub(ClaimsPrincipal principal) =>
        principal.Identity?.AuthenticationType == DevelopmentAuthenticationHandler.AuthenticationSchemeName;
```

`src/Api/TenantModulesEndpoints.cs`:

```csharp
using System.Security.Claims;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Api;

public static class TenantModulesEndpoints
{
    public static IEndpointRouteBuilder MapTenantModulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sólo autenticación, sin permiso, igual que /authorization/me y por la misma razón: cualquier
        // miembro activo lo necesita para dibujar su pantalla (spec 2026-10-07, «Endpoint de
        // capacidades»).
        endpoints
            .MapGet("/api/v1/tenants/{tenantId:guid}/modules", GetModulesAsync)
            .WithTags("Tenancy")
            .RequireAuthorization()
            .Produces<TenantModulesResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetModulesAsync(
        Guid tenantId,
        HttpContext httpContext,
        ITenantModules tenantModules,
        CancellationToken cancellationToken)
    {
        // El claim directo y no IExecutionContext.TenantId, que tira 500 sin claim
        // (HttpExecutionContext.cs:21). Que no esté merece un 403.
        var user = httpContext.User;
        var claimedTenant = user.FindFirstValue(QepClaimTypes.TenantId);
        if (!Guid.TryParse(claimedTenant, out var authenticatedTenant) || authenticatedTenant != tenantId)
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the modules of this tenant.");
        }

        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        return Results.Ok(TenantModulesResponse.From(
            tenantId, modules, QepAuthenticationMode.IsDevelopmentStub(user)));
    }
}

/// <summary>
/// Regla BFF (spec 2026-10-07): **siempre las siete, en el orden de <c>TenantModuleKeys.All</c>,
/// aunque estén apagadas** — si faltara una, la SPA tendría que conocer la lista del backend para
/// dibujar la que no vino.
/// </summary>
public sealed record TenantModulesResponse(Guid TenantId, IReadOnlyList<TenantModuleResponse> Modules)
{
    /// <summary>
    /// Con <paramref name="modules"/> en <c>null</c> (el tenant no está en <c>tenancy.tenants</c>), la
    /// respuesta depende del esquema: bajo el stub de desarrollo, todo prendido y contratado, que es lo
    /// que el stub hace con los permisos; bajo cualquier otro, todo apagado y sin contratar, el mismo
    /// fail closed de <c>AuthorizationService</c>: la pantalla y los permisos dicen lo mismo.
    /// </summary>
    public static TenantModulesResponse From(Guid tenantId, TenantModuleSet? modules, bool isDevelopmentStub)
    {
        var set = modules ?? (isDevelopmentStub
            ? TenantModuleSet.FromStored(TenantModuleKeys.All)
            : TenantModuleSet.Empty);

        return new TenantModulesResponse(
            tenantId,
            TenantModuleKeys.All
                .Select(key => new TenantModuleResponse(
                    key.Value,
                    set.IsEnabled(key),
                    set.IsContracted(key),
                    set.MissingDependencies(key).Select(dependency => dependency.Value).ToArray()))
                .ToArray());
    }
}

/// <param name="Enabled">El efectivo: contratado y con sus dependencias prendidas.</param>
/// <param name="Contracted">Que la fila existe. Sin los dos, la pantalla no puede distinguir «no está
/// en tu plan» de «está, pero le falta otro».</param>
/// <param name="MissingDependencies">Las causas raíz —dependencias transitivas sin contratar—, sólo
/// con <c>Contracted</c> y sin <c>Enabled</c>. Así la pantalla nombra lo que hay que contratar y no
/// un intermediario que ya está contratado. Viaja por clave: las etiquetas las tiene la SPA.</param>
public sealed record TenantModuleResponse(
    string Key,
    bool Enabled,
    bool Contracted,
    IReadOnlyList<string> MissingDependencies);
```

En `Program.cs`, después de `app.MapAuthorizationCatalogEndpoints();`:

```csharp
app.MapTenantModulesEndpoints();
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests"
```

Esperado: todas en verde.

- [ ] **Step 5: README § Módulos por tenant**

Insertar en `README.md`, inmediatamente antes de `## API implementada`, la sección siguiente. El SQL es el de «Operación» del spec, sin `$` a propósito (PowerShell lo expandiría dentro de las comillas dobles):

````markdown
## Módulos por tenant

Cada tenant tiene prendidos los módulos comerciales que contrató, en `tenancy.tenant_modules`: la
presencia de la fila es el módulo. Las claves son `catalog`, `customers`, `companies`, `quotations`,
`orders`, `reporting` y `pos`; `quotations` exige `catalog`, `customers` y `companies`, `orders`
exige `quotations` y `pos` exige `catalog` y `companies`. Un módulo sin su dependencia cuenta como
apagado. Identidad, Tenancy, Authorization, Storage, Platform, Geography, Audit y Notifications son
núcleo y no se apagan.

Apagar un módulo descarta sus permisos en el request siguiente, por cookie y por el stub de
desarrollo (este último sólo cuando el tenant existe en `tenancy.tenants`). No borra datos ni corta
trabajos en vuelo. La SPA lo lee de `GET /api/v1/tenants/{tenantId}/modules` (autenticado, sin
permiso), que siempre devuelve los siete con `enabled`, `contracted` y `missingDependencies`, y se
entera en hasta 5 minutos.

Un tenant del signup nace con los seis sin `pos` mientras `Entitlements:GrantDefaultModulesOnSignup`
esté en `true` (el default), y sin ninguno en `false`. El de la semilla nace con los siete.

No hay endpoint de administración. QCode lo hace por SQL. Local, sin leer el connection string:

```powershell
# Ver los módulos de un tenant
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT module_key, source, enabled_at, note FROM tenancy.tenant_modules m JOIN tenancy.tenants t ON t.id = m.tenant_id WHERE t.slug = 'origen-botanico' ORDER BY module_key;"

# Prender pos
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT id, 'pos', now(), 'manual', 'Activado por QCode' FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO NOTHING;"

# Apagar orders
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "DELETE FROM tenancy.tenant_modules m USING tenancy.tenants t WHERE t.id = m.tenant_id AND t.slug = 'origen-botanico' AND m.module_key = 'orders';"
```

**Después de desplegar `AddTenantModules`** (checklist del despliegue): un pod viejo puede crear
tenants sin filas durante el rolling update. La consulta sólo lista —un tenant sin filas también puede
ser legítimo— y la reparación se hace por slug, después de mirar cada uno:

```powershell
# Tenants sin módulos creados en el último día
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT t.id, t.slug, t.created_at FROM tenancy.tenants t WHERE NOT EXISTS (SELECT 1 FROM tenancy.tenant_modules m WHERE m.tenant_id = t.id) AND t.created_at >= now() - interval '1 day' ORDER BY t.created_at;"

# Reparar uno: los seis del signup
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT t.id, m.key, now(), 'manual', 'Alta durante el despliegue de AddTenantModules' FROM tenancy.tenants t CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key) WHERE t.slug = 'slug-del-tenant' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
```

En producción, las mismas sentencias por el acceso a la base que QCode ya usa para operaciones
manuales. Efecto inmediato, sin reiniciar la API.
````

- [ ] **Step 6: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Bootstrapper/Authentication/QepAuthenticationMode.cs src/Api/TenantModulesEndpoints.cs src/Api/Program.cs README.md tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs; git commit -m "feat(tenancy): GET /modules con los siete módulos del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B9: La configuración del Excel de pedidos exige `orders`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs:14-28`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs:111-129`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (doble nuevo)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs:195-197`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs:309-321`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs`

**Interfaces:**
- Consumes: `ITenantModules`, `TenantModuleGuard` (B1), `DisableModuleAsync` (B6).
- Produces: `GetOrdersExportLayoutHandler(IOrdersExportLayoutRepository, ITenantModules, IExecutionContext)`; `UpdateOrdersExportLayoutHandler(IOrdersExportLayoutRepository, ITenantModules, IQuotationsUnitOfWork, IQuotationAuditPublisher, IExecutionContext, IClock, IValidator<UpdateOrdersExportLayoutCommand>)`; `FixedTenantModules` en Quotations.UnitTests.

- [ ] **Step 1: Doble y pruebas unitarias**

En `QuotationsTestDoubles.cs`:

```csharp
/// <summary>El puerto de módulos con una respuesta fija; <c>null</c> = tenant simulado por el stub
/// (spec 2026-10-07), que no se bloquea. Es el default de los constructores de prueba para que los
/// casos que no son sobre módulos no cambien de significado.</summary>
internal sealed class FixedTenantModules(Modules.Tenancy.Domain.TenantModuleSet? set) : ITenantModules
{
    public static FixedTenantModules Simulated { get; } = new(null);

    public static FixedTenantModules WithoutOrders { get; } = new(
        Modules.Tenancy.Domain.TenantModuleSet.FromStored(
            Modules.Tenancy.Domain.TenantModuleKeys.All.Except([Modules.Tenancy.Domain.TenantModuleKeys.Orders])));

    public Task<Modules.Tenancy.Domain.TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(set);
}
```

`GetOrdersExportLayoutHandlerTests.NewHandler` (`:195-197`) pasa a:

```csharp
    private static GetOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository,
        IExecutionContext? executionContext = null,
        ITenantModules? tenantModules = null) =>
        new(
            repository,
            tenantModules ?? FixedTenantModules.Simulated,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId));
```

y el caso nuevo:

```csharp
    // Spec 2026-10-07: el permiso es de núcleo (tenancy.settings.read) pero lo que se configura es de
    // orders, así que el handler lo chequea explícito, antes de leer el repositorio.
    [Fact]
    public async Task WithoutOrdersItIsForbiddenBeforeReadingTheRepository()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(repository, tenantModules: FixedTenantModules.WithoutOrders);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, repository.FindCalls);
    }
```

`UpdateOrdersExportLayoutHandlerTests.NewHandler` (`:309-321`) pasa a:

```csharp
    private static UpdateOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository,
        RecordingExportAuditPublisher audit,
        CountingQuotationsUnitOfWork unitOfWork,
        IExecutionContext? executionContext = null,
        ITenantModules? tenantModules = null) =>
        new(
            repository,
            tenantModules ?? FixedTenantModules.Simulated,
            unitOfWork,
            audit,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new UpdateOrdersExportLayoutValidator());
```

y el caso nuevo, con el cuerpo inválido de `AnInvalidBodyIsAValidationFailureBeforeReadingAnything` para fijar que el módulo va antes que el validador:

```csharp
    [Fact]
    public async Task WithoutOrdersItIsForbiddenBeforeValidatingOrReading()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork, tenantModules: FixedTenantModules.WithoutOrders);
        var columns = DefaultInputs();
        columns[3] = columns[3] with { Header = "   " };

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(columns, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, repository.FindCalls);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }
```

- [ ] **Step 2: Prueba de integración**

En `TenantModulesQuotationsApiTests.cs` (agregar `using System.Text.Json;`):

```csharp
    [Fact]
    public async Task TheOrdersExportLayoutNeedsOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, Modules.Tenancy.Application.TenancyPermissions.SettingsRead]);
        using var _ = client;
        var url = $"/api/v1/tenants/{tenantId}/orders-export-layout";
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.module_not_enabled", problem.RootElement.GetProperty("code").GetString());
    }
```

- [ ] **Step 3: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExportLayoutHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TheOrdersExportLayoutNeedsOrders"
```

Esperado: la unitaria no compila (`CS1729`, constructores de 3 y 7 argumentos). La de integración falla con `Expected: Forbidden, Actual: OK`.

- [ ] **Step 4: Implementar**

`GetOrdersExportLayoutHandler`:

```csharp
public sealed class GetOrdersExportLayoutHandler(
    IOrdersExportLayoutRepository repository,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrdersExportLayoutQuery, OrdersExportLayoutDto>
{
    public async Task<OrdersExportLayoutDto> HandleAsync(
        GetOrdersExportLayoutQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        // Spec 2026-10-07: el permiso es de núcleo, así que el enmascarado no lo cubre; lo que se
        // configura es de orders. Después de la autorización y antes de leer nada.
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Orders, cancellationToken);

        var stored = await repository.FindAsync(query.TenantId, cancellationToken);
        return OrdersExportLayoutMappings.ToDto(stored, query.TenantId);
    }
}
```

`UpdateOrdersExportLayoutHandler`: el constructor gana `ITenantModules tenantModules` como segundo parámetro (después de `repository`), y en `HandleAsync`, entre `EnsureAuthorized` y el validador:

```csharp
        // B1: autorización antes que el validador (ver el comentario de la clase). Spec 2026-10-07:
        // el módulo también, por la misma razón — un 422 confirmaría que el cuerpo se leyó.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Orders, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
```

(Nombres calificados para no importar `Modules.Tenancy.Domain` junto a `Modules.Quotations.Domain`.)

- [ ] **Step 5: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExportLayoutHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~TenantModulesQuotationsApiTests|FullyQualifiedName~OrdersExportLayoutApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~QuotationsLayerTests"
```

Esperado: todo en verde; `OrdersExportLayoutApiTests` como regresión (tenant registrado con `orders`), `QuotationsLayerTests` confirma que Quotations.Application sólo referencia Tenancy entre los módulos de negocio.

- [ ] **Step 6: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/TenantModulesQuotationsApiTests.cs; git commit -m "feat(quotations): la configuración del Excel de pedidos exige el módulo orders"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B10: Storage — el dueño del archivo decide el módulo

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs` (dobles nuevos)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs`

**Interfaces:**
- Consumes: `ITenantModules`, `TenantModuleGuard`, `TenantModuleKeys`, `TenantModuleSet` (B1).
- Produces: `static class FileOwnerModules { IReadOnlyDictionary<FileOwnerType, TenantModuleKey?> ByOwnerType; TenantModuleKey? ModuleOf(FileOwnerType); IReadOnlyCollection<FileOwnerType> OwnerTypesDisabledIn(TenantModuleSet) }`; `static class FileOwnerModuleGuard { Task EnsureOwnerModuleEnabledAsync(ITenantModules, FileResource, CancellationToken) }`. En las pruebas de Storage: `FixedTenantModules` (`Simulated`, `AllBut(params TenantModuleKey[])`, `Asked`) y los dobles «intocables» `UntouchableObjectStorage`, `UntouchableContentInspector`, `UntouchableVariantGenerator`, `UntouchablePaymentProofProcessor`, `UntouchableScanner`, que usa B11.

- [ ] **Step 1: Dobles y pruebas**

Al final de `StorageApplicationTestDoubles.cs` (ya importa `Modules.Tenancy.Application` y `Modules.Tenancy.Domain`):

```csharp
/// <summary>El puerto de módulos con una respuesta fija; <c>null</c> = tenant simulado por el stub,
/// que ni filtra ni bloquea (spec 2026-10-07). <see cref="Simulated"/> es el default de los
/// constructores de prueba que no son sobre módulos.</summary>
internal sealed class FixedTenantModules(TenantModuleSet? set) : ITenantModules
{
    public static FixedTenantModules Simulated => new(null);

    public static FixedTenantModules AllBut(params TenantModuleKey[] missing) =>
        new(TenantModuleSet.FromStored(TenantModuleKeys.All.Except(missing)));

    public List<Guid> Asked { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Asked.Add(tenantId);
        return Task.FromResult(set);
    }
}

/// <summary>El bucket privado que no se puede tocar: cualquier llamada lanza. Un handler que lo usara
/// antes del guard de módulo tiraría esta excepción en vez del 403, y la prueba lo ve.</summary>
internal sealed class UntouchableObjectStorage : IObjectStorage
{
    private static InvalidOperationException Touched() => new("The private bucket must not be touched.");

    public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
        throw Touched();

    public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
        throw Touched();

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        throw Touched();

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw Touched();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw Touched();

    public Task PromoteAsync(
        string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
        throw Touched();

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw Touched();

    public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
        throw Touched();
}

internal sealed class UntouchableContentInspector : IFileContentInspector
{
    public bool Matches(string name, string mimeType, byte[] content) =>
        throw new InvalidOperationException("The content must not be inspected.");
}

internal sealed class UntouchableVariantGenerator : IImageVariantGenerator
{
    public bool Supports(string mimeType) => throw new InvalidOperationException("No variants must be generated.");

    public Task<IReadOnlyList<GeneratedFileVariant>> GenerateAsync(byte[] content, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No variants must be generated.");
}

internal sealed class UntouchablePaymentProofProcessor : IPaymentProofImageProcessor
{
    public bool Supports(string mimeType) => throw new InvalidOperationException("No proof must be processed.");

    public Task<ProcessedPaymentProofImage> ProcessAsync(byte[] content, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No proof must be processed.");
}

internal sealed class UntouchableScanner : IFileScanner
{
    public Task<FileScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Nothing must be scanned.");
}
```

Si `IObjectStorage` tiene algún miembro que `SigningObjectStorage` (`:41-73`) no implementa, el compilador lo nombra (`CS0535`) y se agrega con el mismo `throw Touched()`.

`tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>Spec 2026-10-07, «Archivos de Storage: el dueño decide el módulo».</summary>
public sealed class FileOwnerModulesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // Un miembro nuevo de FileOwnerType sin mapear no pasa: criterio de éxito 4.
    [Fact]
    public void EveryOwnerTypeIsMapped()
    {
        Assert.Equal(
            Enum.GetValues<FileOwnerType>().Order(),
            FileOwnerModules.ByOwnerType.Keys.Order());
    }

    [Fact]
    public void ProductsAreCatalogAndProofsAreOrdersAndTheRestIsCore()
    {
        Assert.Same(TenantModuleKeys.Catalog, FileOwnerModules.ModuleOf(FileOwnerType.Product));
        Assert.Same(TenantModuleKeys.Orders, FileOwnerModules.ModuleOf(FileOwnerType.PaymentProof));
        foreach (var core in new[] { FileOwnerType.User, FileOwnerType.Entity, FileOwnerType.System, FileOwnerType.Tenant })
        {
            Assert.Null(FileOwnerModules.ModuleOf(core));
        }
    }

    [Fact]
    public void OwnerTypesDisabledInListsTheOnesWhoseModuleIsOff()
    {
        Assert.Equal(
            [FileOwnerType.PaymentProof],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(
                TenantModuleKeys.All.Except([TenantModuleKeys.Orders]))));
        Assert.Equal(
            [FileOwnerType.Product, FileOwnerType.PaymentProof],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.Empty).Order());
        Assert.Empty(FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(TenantModuleKeys.All)));
    }

    [Theory]
    [InlineData(FileOwnerType.Product, "catalog")]
    [InlineData(FileOwnerType.PaymentProof, "orders")]
    public async Task AFileWhoseModuleIsOffIsForbidden(FileOwnerType ownerType, string module)
    {
        var modules = FixedTenantModules.AllBut(TenantModuleKey.Parse(module));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
                modules, FileOf(ownerType), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
    }

    [Theory]
    [InlineData(FileOwnerType.Tenant)]
    [InlineData(FileOwnerType.User)]
    public async Task ACoreFilePassesWithNoModules(FileOwnerType ownerType)
    {
        var modules = new FixedTenantModules(TenantModuleSet.Empty);

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            modules, FileOf(ownerType), TestContext.Current.CancellationToken);

        Assert.Empty(modules.Asked);
    }

    [Fact]
    public async Task ASimulatedTenantIsNotBlocked()
    {
        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            FixedTenantModules.Simulated, FileOf(FileOwnerType.PaymentProof), TestContext.Current.CancellationToken);
    }

    private static FileResource FileOf(FileOwnerType ownerType) =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType,
            "archivo.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/archivo", Now);
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModulesTests"
```

Esperado: `error CS0103: The name 'FileOwnerModules' does not exist in the current context` y lo mismo con `FileOwnerModuleGuard`.

- [ ] **Step 3: Implementar**

`src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs`:

```csharp
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using TenantModuleKey = Modules.Tenancy.Domain.TenantModuleKey;
using TenantModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;
using TenantModuleSet = Modules.Tenancy.Domain.TenantModuleSet;

namespace Modules.Storage.Application;

/// <summary>
/// <c>storage.file.*</c> es núcleo —lo usan el logo del tenant y los comprobantes a la vez—, así que
/// el permiso no sabe de quién es el archivo. Lo sabe <see cref="FileOwnerType"/> (spec 2026-10-07).
/// Un valor <c>null</c> es núcleo. Una prueba exige que las claves sean exactamente
/// <c>Enum.GetValues&lt;FileOwnerType&gt;()</c>: un miembro nuevo sin mapear no pasa.
/// </summary>
public static class FileOwnerModules
{
    public static readonly IReadOnlyDictionary<FileOwnerType, TenantModuleKey?> ByOwnerType =
        new Dictionary<FileOwnerType, TenantModuleKey?>
        {
            // Ninguna pantalla actual lo manda; los comprobantes subidos antes de v2 siguieron siendo
            // User (D13) y quedan como núcleo: residual aceptado (DECISIÓN-PENDIENTE del spec).
            [FileOwnerType.User] = null,
            [FileOwnerType.Entity] = null,
            [FileOwnerType.System] = null,
            [FileOwnerType.Product] = TenantModuleKeys.Catalog,
            [FileOwnerType.PaymentProof] = TenantModuleKeys.Orders,
            [FileOwnerType.Tenant] = null,
        };

    public static TenantModuleKey? ModuleOf(FileOwnerType ownerType) => ByOwnerType[ownerType];

    /// <summary>Los tipos de dueño cuyo módulo no es efectivo: el listado los excluye en SQL.</summary>
    public static IReadOnlyCollection<FileOwnerType> OwnerTypesDisabledIn(TenantModuleSet modules) =>
        ByOwnerType
            .Where(pair => pair.Value is { } key && !modules.IsEnabled(key))
            .Select(pair => pair.Key)
            .ToArray();
}

/// <summary>
/// El chequeo de todo handler de Storage que carga un archivo por id (spec 2026-10-07): va
/// **después del 404** de otro tenant —que va primero para no confirmar que el id existe ahí— y
/// **antes de cualquier otra regla o efecto** (bucket, auditoría, <c>SaveChangesAsync</c>).
/// 403 <c>tenancy.module_not_enabled</c>. Con el tenant simulado por el stub no bloquea.
/// </summary>
public static class FileOwnerModuleGuard
{
    public static async Task EnsureOwnerModuleEnabledAsync(
        ITenantModules tenantModules,
        FileResource resource,
        CancellationToken cancellationToken)
    {
        if (FileOwnerModules.ModuleOf(resource.OwnerType) is { } module)
        {
            await TenantModuleGuard.EnsureEnabledAsync(tenantModules, resource.TenantId, module, cancellationToken);
        }
    }
}
```

(Alias y no `using Modules.Tenancy.Domain;` para no mezclar ese namespace con `Modules.Storage.Domain` en Storage.Application.)

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $B
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModulesTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StorageLayerTests"
```

Esperado: verde las dos.

- [ ] **Step 5: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs; git commit -m "feat(storage): el módulo de un archivo sale de su tipo de dueño"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B11: Storage — los siete comandos por id chequean el módulo del dueño

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs:15-41`
- Modify: `src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs:10-32`
- Modify: `src/Modules/Storage/Modules.Storage.Application/CancelUpload.cs:12-37`
- Modify: `src/Modules/Storage/Modules.Storage.Application/UpdateFileMetadata.cs:13-35`
- Modify: `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs:11-39,62-83`
- Modify: `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs:10-37`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs:72-81`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs:130-138,154-162,227-253`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModuleHandlersTests.cs`

**Interfaces:**
- Consumes: `FileOwnerModuleGuard` y los dobles de B10.
- Produces: los siete constructores ganan `ITenantModules tenantModules` como **segundo** parámetro, después de `IFileResourceRepository repository`. El resto del orden no cambia.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModuleHandlersTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Archivos de Storage»: los siete handlers que cargan un archivo por id. Un
/// comprobante con <c>orders</c> apagado da 403 <c>tenancy.module_not_enabled</c> **sin efecto**
/// —ni bucket, ni auditoría, ni <c>SaveChangesAsync</c>— y un archivo de otro tenant sigue dando 404
/// antes del guard. Los dobles «intocables» lanzan si se los usa, así que un guard puesto tarde
/// aparece como otra excepción. En publicar, despublicar y borrar, el error tiene que ser el de módulo
/// y no el de <c>PaymentProofGuard</c>.
/// </summary>
public sealed class FileOwnerModuleHandlersTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Handlers =>
    [
        "IssueDownloadUrl", "CompleteUpload", "CancelUpload", "UpdateFileMetadata",
        "PublishFile", "UnpublishFile", "SoftDeleteFile",
    ];

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task APaymentProofWithOrdersOffIsForbiddenWithoutAnyEffect(string handler)
    {
        var effects = new Effects();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Run(
            handler, PaymentProofFor(handler), TenantId, FixedTenantModules.AllBut(TenantModuleKeys.Orders), effects));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Empty(effects.Audit.Actions);
        Assert.Equal(0, effects.UnitOfWork.Saves);
        Assert.Empty(effects.PublicStorage.Copies);
        Assert.Empty(effects.PublicStorage.DeletedKeys);
        Assert.Empty(effects.Probe.Asked);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task AFileOfAnotherTenantIsStillNotFoundBeforeTheModule(string handler)
    {
        var modules = FixedTenantModules.AllBut(TenantModuleKeys.Orders);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Run(
            handler, PaymentProofFor(handler), Guid.CreateVersion7(), modules, new Effects()));

        Assert.Empty(modules.Asked);
    }

    // La prueba de control: con orders prendido, cada handler llega a su propia regla (y ahí sí
    // tocaría el bucket, por eso lanza otra cosa). Confirma que el 403 de arriba es el del módulo.
    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task WithOrdersOnTheModuleDoesNotStopIt(string handler)
    {
        var error = await Record.ExceptionAsync(() => Run(
            handler, PaymentProofFor(handler), TenantId, FixedTenantModules.AllBut(), new Effects()));

        Assert.False(
            error is RequestForbiddenException { Code: "tenancy.module_not_enabled" },
            $"{handler} stopped on the module with orders on.");
    }

    private static FileResource PaymentProofFor(string handler)
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        if (handler != "CompleteUpload")
        {
            proof.CompleteUpload("checksum", 2048, Now);
            proof.MarkClean(Now);
        }

        return proof;
    }

    private static Task Run(string handler, FileResource resource, Guid tenantId, ITenantModules modules, Effects effects)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryFileResourceRepository(resource);
        var clock = new FixedClock(Now);
        var context = new AllowAllExecutionContext(tenantId);
        var publication = new FilePublication(effects.PublicStorage, clock);
        var fileId = resource.Id.Value;

        return handler switch
        {
            "IssueDownloadUrl" => new IssueDownloadUrlHandler(
                    repository, modules, new UntouchableObjectStorage(), effects.PublicStorage,
                    effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new IssueDownloadUrlCommand(tenantId, fileId), cancellationToken),
            "CompleteUpload" => new CompleteUploadHandler(
                    repository, modules, new UntouchableObjectStorage(), new UntouchableContentInspector(),
                    new UntouchableVariantGenerator(), new UntouchablePaymentProofProcessor(), new UntouchableScanner(),
                    effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new CompleteUploadCommand(tenantId, fileId), cancellationToken),
            "CancelUpload" => new CancelUploadHandler(
                    repository, modules, new UntouchableObjectStorage(), effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new CancelUploadCommand(tenantId, fileId), cancellationToken),
            "UpdateFileMetadata" => new UpdateFileMetadataHandler(
                    repository, modules, effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new UpdateFileMetadataCommand(tenantId, fileId, "comprobantes", []), cancellationToken),
            "PublishFile" => new PublishFileHandler(
                    repository, modules, effects.UnitOfWork, publication, effects.PublicStorage, effects.Audit,
                    context, clock)
                .HandleAsync(new PublishFileCommand(tenantId, fileId), cancellationToken),
            "UnpublishFile" => new UnpublishFileHandler(
                    repository, modules, effects.UnitOfWork, publication, effects.PublicStorage, [effects.Probe],
                    effects.Audit, context, clock)
                .HandleAsync(new UnpublishFileCommand(tenantId, fileId), cancellationToken),
            "SoftDeleteFile" => new SoftDeleteFileHandler(
                    repository, modules, effects.UnitOfWork, publication, [effects.Probe], effects.Audit, context, clock)
                .HandleAsync(new SoftDeleteFileCommand(tenantId, fileId), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, null),
        };
    }

    private sealed class Effects
    {
        public RecordingStorageAuditPublisher Audit { get; } = new();

        public CountingStorageUnitOfWork UnitOfWork { get; } = new();

        public RecordingPublicObjectStorage PublicStorage { get; } = new();

        // Referenciado: sin el guard de módulo, despublicar y borrar pararían acá con invalid_state.
        public StubFileReferenceProbe Probe { get; } = new(referenced: true);
    }
}
```

En `IssueDownloadUrlHandlerTests.cs`, `HandlerFor` (`:72-81`) gana el puerto y tres casos nuevos (agregar `using BuildingBlocks.Application;` y `using Modules.Tenancy.Domain;`):

```csharp
    private static IssueDownloadUrlHandler HandlerFor(
        FileResource resource, RecordingStorageAuditPublisher audit, ITenantModules? modules = null) =>
        new(
            new InMemoryFileResourceRepository(resource),
            modules ?? FixedTenantModules.Simulated,
            new SigningObjectStorage(),
            new FixedPublicObjectStorage(),
            new CountingStorageUnitOfWork(),
            audit,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
```

```csharp
    [Fact]
    public async Task AProductImageWithCatalogOffIsForbidden()
    {
        var image = AvailableFile(FileOwnerType.Product);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            HandlerFor(image, new RecordingStorageAuditPublisher(), FixedTenantModules.AllBut(TenantModuleKeys.Catalog))
                .HandleAsync(new IssueDownloadUrlCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }

    // El logo es núcleo: se descarga aunque el tenant no tenga ningún módulo.
    [Fact]
    public async Task ATenantLogoIsSignedWithNoModules()
    {
        var logo = AvailableFile(FileOwnerType.Tenant);

        var result = await HandlerFor(logo, new RecordingStorageAuditPublisher(), new FixedTenantModules(TenantModuleSet.Empty))
            .HandleAsync(new IssueDownloadUrlCommand(TenantId, logo.Id.Value), TestContext.Current.CancellationToken);

        Assert.StartsWith(SigningObjectStorage.SignedBaseUrl, result.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASimulatedTenantStillGetsTheUrlOfAProof()
    {
        var proof = AvailablePaymentProof();

        var result = await HandlerFor(proof, new RecordingStorageAuditPublisher(), FixedTenantModules.Simulated)
            .HandleAsync(new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.StartsWith(SigningObjectStorage.SignedBaseUrl, result.Url, StringComparison.Ordinal);
    }

    private static FileResource AvailableFile(FileOwnerType ownerType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType,
            "archivo.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/archivo", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.MarkClean(Now);
        return file;
    }
```

En `PaymentProofFileManagementTests.cs`, las dos construcciones de `PublishFileHandler` (`:130`, `:154`) ganan `FixedTenantModules.Simulated` como segundo argumento, y `DeleteHandler` (`:227-239`) y `UnpublishHandler` (`:241-253`) igual, después de `new InMemoryFileResourceRepository(resource)`.

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModuleHandlersTests|FullyQualifiedName~IssueDownloadUrlHandlerTests|FullyQualifiedName~PaymentProofFileManagementTests"
```

Esperado: no compila, `CS1729` por cada uno de los siete handlers (un argumento de más).

- [ ] **Step 3: Agregar el puerto y el guard a los siete**

En cada uno, el constructor gana `ITenantModules tenantModules,` después de `IFileResourceRepository repository,`, y la línea siguiente va inmediatamente después del 404:

```csharp
        // Spec 2026-10-07: después del 404 —que no confirma que el id existe en otro tenant— y antes
        // de cualquier otra regla o efecto.
        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(tenantModules, resource, cancellationToken);
```

Dónde, exactamente:

| Handler | Archivo | Después de | Antes de |
|---|---|---|---|
| `IssueDownloadUrlHandler` | `IssueDownloadUrl.cs` | el `if (resource is null …) throw` (`:34-38`) | `resource.EnsureDownloadable();` (`:41`) |
| `CompleteUploadHandler` | `CompleteUpload.cs` | `var resource = await LoadAsync(...)` (`:30`) | `objectStorage.StatAsync` (`:32`) |
| `CancelUploadHandler` | `CancelUpload.cs` | el `if (resource is null …) throw` (`:30-34`) | `var now = clock.UtcNow;` (`:36`) |
| `UpdateFileMetadataHandler` | `UpdateFileMetadata.cs` | el `if (resource is null …) throw` (`:29-33`) | `var now = clock.UtcNow;` (`:35`) |
| `PublishFileHandler` | `SetFilePublication.cs` | `var resource = await LoadAsync(...)` (`:36`) | `PaymentProofGuard.EnsureNotPaymentProof(resource);` (`:39`). El chequeo de bucket sin configurar (`:29-34`) sigue primero: no carga nada |
| `UnpublishFileHandler` | `SetFilePublication.cs` | `var resource = await PublishFileHandler.LoadAsync(...)` (`:78-79`) | `PaymentProofGuard.EnsureNotReferencedAsync` (`:83`) |
| `SoftDeleteFileHandler` | `SoftDeleteFile.cs` | el `if (resource is null …) throw` (`:29-33`) | `PaymentProofGuard.EnsureNotReferencedAsync` (`:37`) |

`CreateUploadSessionHandler` **no** lleva el guard (no carga nada por id; la fila `PendingUpload` de un comprobante con `orders` apagado no se puede completar y la purga `StagingCleanupProcessor`). Ningún adaptador del composition root que lee `IFileResourceRepository` cambia.

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todo Storage.UnitTests en verde (los 21 casos de la teoría, los tres nuevos de descarga y los anteriores sin cambio de significado); `CompositionRootTests` en verde (los handlers siguen registrados; `ITenantModules` existe desde B3).

- [ ] **Step 5: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs src/Modules/Storage/Modules.Storage.Application/CancelUpload.cs src/Modules/Storage/Modules.Storage.Application/UpdateFileMetadata.cs src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModuleHandlersTests.cs tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs; git commit -m "feat(storage): los comandos por id respetan el módulo del dueño del archivo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B12: Storage — el listado excluye los archivos de módulos apagados

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IFileResourceRepository.cs:19-31`
- Modify: `src/Modules/Storage/Modules.Storage.Application/ListFiles.cs:18-43`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileResourceRepository.cs:35-64`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs:24-36`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:38-49`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofPublicUrlTests.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`

**Interfaces:**
- Consumes: `FileOwnerModules.OwnerTypesDisabledIn` (B10), los guards de B11, `DisableModuleAsync` (B6).
- Produces: `IFileResourceRepository.SearchAsync(Guid tenantId, string? search, FileResourceStatus? status, string? kind, string? category, string? tag, FileOwnerFilter? owner, IReadOnlyCollection<FileOwnerType> excludedOwnerTypes, int page, int pageSize, CancellationToken)`; `ListFilesHandler(IFileResourceRepository, ITenantModules, IPublicObjectStorage, IExecutionContext)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `StorageApplicationTestDoubles.cs`, `InMemoryFileResourceRepository.SearchAsync` anota lo excluido:

```csharp
    /// <summary>Lo que el handler pidió excluir en la última búsqueda (spec 2026-10-07). El filtro
    /// es SQL y lo cubre integración; acá se fija qué le pide el handler al repositorio.</summary>
    public IReadOnlyCollection<FileOwnerType>? LastExcludedOwnerTypes { get; private set; }

    public Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastExcludedOwnerTypes = excludedOwnerTypes;
        // Todo lo sembrado, sin filtrar: los filtros son SQL y los cubre integración.
        return Task.FromResult<(IReadOnlyList<FileResource>, int)>((resources, resources.Length));
    }
```

En `PaymentProofPublicUrlTests.cs`, `ListAsync` pasa a recibir el repositorio y los módulos, y tres casos nuevos (agregar `using Modules.Tenancy.Domain;`):

```csharp
    [Fact]
    public async Task WithoutOrdersTheHandlerExcludesPaymentProofs()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.AllBut(TenantModuleKeys.Orders));

        Assert.Equal([FileOwnerType.PaymentProof], repository.LastExcludedOwnerTypes);
    }

    [Fact]
    public async Task WithEverythingOnNothingIsExcluded()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.AllBut());

        Assert.Empty(repository.LastExcludedOwnerTypes!);
    }

    [Fact]
    public async Task ASimulatedTenantExcludesNothing()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.Simulated);

        Assert.Empty(repository.LastExcludedOwnerTypes!);
    }

    private static Task<PagedFilesDto> ListAsync(FileResource resource) =>
        ListAsync(new InMemoryFileResourceRepository(resource), FixedTenantModules.Simulated);

    private static Task<PagedFilesDto> ListAsync(InMemoryFileResourceRepository repository, ITenantModules modules) =>
        new ListFilesHandler(
                repository,
                modules,
                new FixedPublicObjectStorage(),
                new AllowAllExecutionContext(TenantId))
            .HandleAsync(
                new ListFilesQuery(TenantId, null, null, null, null, null, null, 1, 20),
                TestContext.Current.CancellationToken);
```

(reemplaza el `ListAsync(FileResource)` actual de `:37-44`; agregar `using Modules.Tenancy.Application;`).

En `OrderPaymentProofPublicationApiTests.cs` (agregar `using SixLabors.ImageSharp;`, `using SixLabors.ImageSharp.PixelFormats;` y `using ModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;`):

```csharp
    // Spec 2026-10-07, criterio 6: con orders apagado el comprobante sale del listado y sus comandos
    // por id dan 403 tenancy.module_not_enabled; el logo del tenant, que es núcleo, no cambia.
    [Fact]
    public async Task WithOrdersOffAProofLeavesTheListingAndItsCommandsAreForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var proofId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", proofId);
        var logoId = await CreateTenantLogoFileAsync(client, factory, tenantId);
        Assert.Contains(proofId, await ListedFileIdsAsync(client, tenantId, query: string.Empty));

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var listed = await ListedFileIdsAsync(client, tenantId, query: string.Empty);
        Assert.DoesNotContain(proofId, listed);
        Assert.Contains(logoId, listed);
        await AssertModuleNotEnabledAsync(await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{proofId}/download-url", content: null,
            TestContext.Current.CancellationToken));
        // El que hoy devolvería su PublicUrl.
        await AssertModuleNotEnabledAsync(await client.PatchAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files/{proofId}/metadata",
            new { category = "comprobantes", tags = Array.Empty<string>() },
            TestContext.Current.CancellationToken));
        var logoUrl = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{logoId}/download-url", content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, logoUrl.StatusCode);
    }

    // Review Focus 2 (decisión 31 del spec): un filtro explícito por el dueño de un módulo apagado
    // da una página vacía, no el archivo ni un 403.
    [Fact]
    public async Task AnExplicitOwnerFilterOnADisabledModuleReturnsAnEmptyPage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var proofId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var ownerId = await OwnerIdOfAsync(client, tenantId, proofId);
        var filter = $"?ownerType=PaymentProof&ownerId={ownerId}";
        Assert.Equal([proofId], await ListedFileIdsAsync(client, tenantId, filter));

        await DisableModuleAsync(factory, tenantId, ModuleKeys.Orders);

        var page = await client.GetFromJsonAsync<FilesPageDto>(
            $"/api/v1/tenants/{tenantId}/files{filter}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    private static async Task<Guid> CreateTenantLogoFileAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        using var image = new Image<Rgba32>(64, 48, Color.CornflowerBlue);
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, TestContext.Current.CancellationToken);
        return await CreateAvailableFileAsync(
            client, factory, tenantId, "image/png", png.ToArray(), "logo.png", ownerType: "Tenant");
    }

    private static async Task<Guid[]> ListedFileIdsAsync(HttpClient client, Guid tenantId, string query)
    {
        var page = await client.GetFromJsonAsync<FilesPageDto>(
            $"/api/v1/tenants/{tenantId}/files{query}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page.Items.Select(item => item.Id).ToArray();
    }

    private static async Task<Guid> OwnerIdOfAsync(HttpClient client, Guid tenantId, Guid fileId)
    {
        var page = await client.GetFromJsonAsync<FilesPageDto>(
            $"/api/v1/tenants/{tenantId}/files?pageSize=100", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page.Items.Single(item => item.Id == fileId).OwnerId;
    }

    private static async Task AssertModuleNotEnabledAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.module_not_enabled", problem.RootElement.GetProperty("code").GetString());
    }

    private sealed record FilesPageDto(List<FileItemDto> Items, int TotalCount);

    private sealed record FileItemDto(Guid Id, Guid OwnerId);
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $B
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~PaymentProofPublicUrlTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~WithOrdersOffAProofLeavesTheListing|FullyQualifiedName~AnExplicitOwnerFilterOnADisabledModule"
```

Esperado: la unitaria no compila (`CS0535: 'InMemoryFileResourceRepository' does not implement interface member 'IFileResourceRepository.SearchAsync(...)'` o `CS1729` de `ListFilesHandler`). Las dos de integración compilan y fallan: el comprobante sigue en el listado (`Assert.DoesNotContain() Failure`) y la página filtrada trae un ítem.

- [ ] **Step 3: Implementar**

`IFileResourceRepository.SearchAsync` gana un parámetro después de `owner`:

```csharp
        FileOwnerFilter? owner,
        // Spec 2026-10-07: los tipos de dueño cuyo módulo está apagado. Vacío = sin excluir.
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes,
        int page,
```

`ListFilesHandler`:

```csharp
public sealed class ListFilesHandler(
    IFileResourceRepository repository,
    ITenantModules tenantModules,
    IPublicObjectStorage publicStorage,
    IExecutionContext executionContext)
    : IQueryHandler<ListFilesQuery, PagedFilesDto>
{
    public async Task<PagedFilesDto> HandleAsync(
        ListFilesQuery query,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, StoragePermissions.FileRead);

        // Spec 2026-10-07: los archivos cuyo dueño es de un módulo apagado no se listan. El filtro va
        // en SQL y no en memoria, para que totalCount y la paginación sigan siendo ciertos. Con el
        // tenant simulado por el stub (null) no se excluye nada.
        var modules = await tenantModules.FindAsync(query.TenantId, cancellationToken);
        var excludedOwnerTypes = modules is null ? [] : FileOwnerModules.OwnerTypesDisabledIn(modules);

        var page = Math.Max(query.Page, 1);
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var (items, totalCount) = await repository.SearchAsync(
            query.TenantId,
            query.Search,
            query.Status,
            query.Kind,
            query.Category,
            query.Tag,
            query.Owner,
            excludedOwnerTypes,
            page,
            pageSize,
            cancellationToken);
```

(el resto igual). `excludedOwnerTypes` se tipa como `IReadOnlyCollection<FileOwnerType>`: si el compilador no infiere el tipo de la expresión condicional con `[]`, declararla con ese tipo explícito.

`FileResourceRepository.SearchAsync`: firma igual a la interfaz, y después del bloque del filtro por dueño (`:59-64`):

```csharp
        // Spec 2026-10-07. Una condición por tipo excluido y no un Contains sobre la lista: la
        // columna se guarda como texto (HasConversion<string>) y así la traducción no depende de cómo
        // EF mapee una colección de enums convertidos.
        foreach (var excluded in excludedOwnerTypes)
        {
            var ownerType = excluded;
            query = query.Where(resource => resource.OwnerType != ownerType);
        }
```

`tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:38-49`: agregar el parámetro `IReadOnlyCollection<FileOwnerType> excludedOwnerTypes,` después de `FileOwnerFilter? owner,` (el cuerpo sigue lanzando `NotSupportedException`).

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests
```

Esperado: todo en verde. La última es la regresión del listado con tenants simulados (sin filtro).

- [ ] **Step 5: Formato y commit**

```powershell
Set-Location $B
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/Modules/Storage/Modules.Storage.Application/IFileResourceRepository.cs src/Modules/Storage/Modules.Storage.Application/ListFiles.cs src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileResourceRepository.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofPublicUrlTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs; git commit -m "feat(storage): el listado de archivos excluye los de módulos apagados"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B13: Suite completa contra la baseline

**Files:** ninguno (sólo verificación; si algo falla, se corrige en la tarea dueña con su propio ciclo RED/GREEN y su commit).

**Interfaces:**
- Consumes: `$env:TEMP\qep-modulos-baseline-failed.txt` (B0).

- [ ] **Step 1: Build y suite completa en primer plano**

```powershell
Set-Location $B
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -match 'Api\.dll' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
$results = Join-Path $env:TEMP "qep-modulos-final"
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $results
```

`--locked-mode` confirma que ningún `packages.lock.json` quedó desincronizado (el del proyecto de B2 se regeneró y commiteó).

- [ ] **Step 2: Comparar fallas por nombre**

```powershell
$final = Get-ChildItem -LiteralPath $results -Filter *.trx |
    ForEach-Object { [xml](Get-Content -LiteralPath $_.FullName -Raw) } |
    ForEach-Object { $_.TestRun.Results.UnitTestResult } |
    Where-Object { $_.outcome -eq 'Failed' } |
    ForEach-Object { $_.testName } |
    Sort-Object -Unique
$baseline = Get-Content (Join-Path $env:TEMP "qep-modulos-baseline-failed.txt")
"Nuevas fallas:"; $final | Where-Object { $_ -notin $baseline }
"Arregladas de pasada:"; $baseline | Where-Object { $_ -notin $final }
```

Esperado: «Nuevas fallas» vacío. Si aparece alguna, el candidato más probable es una suite que usa `register-tenant` o inserta su tenant y pide por `X-Permissions` un permiso **no registrado** o de `pos` (el enmascarado lo descarta): se lee la prueba, se decide si es regresión real o fixture a ajustar, y se corrige en un commit propio con su RED/GREEN. Anotar en el handoff los dos bloques literales y el resumen de `dotnet test`.

- [ ] **Step 3: Formato de todo lo tocado en la rama**

Correr el chequeo de formato de «Global Constraints» (compara contra `origin/develop`). Esperado: sin salida.

- [ ] **Step 4: Handoff del backend**

Sin push: el push y el PR los decide el owner (memoria del repo: `gh` sin sesión; se entrega la URL compare). Anotar en el handoff: commits de la rama (`git log --oneline origin/develop..HEAD`), las salidas de B0 y B13, y el checklist de despliegue: **desplegar el backend antes que el frontend**, correr después la consulta de «tenants sin módulos creados en el último día» del README, y prender `pos` a mano en las bases locales viejas que ya tenían `origin-botanico`.

---
# Parte 2 — Frontend (`qep-frontend`)

Reglas propias del repo (`qep-frontend/CLAUDE.md`), además de las globales: **comentarios e
identificadores en inglés** (el copy de UI en español, tuteando); Prettier sin punto y coma y con
comillas simples; la prueba vive junto a su fuente; `src/routes/` sólo declara la ruta y arma el
container; `src/routeTree.gen.ts` nunca se edita a mano. Los commits llevan Conventional Commits; no
hay ID de slice para este trabajo (el spec es de `docs/superpowers/` del backend), así que el scope es
el área (`feat(modules): …`, `feat(quotes): …`) — decisión de este plan.

Formato y lint de lo tocado, al cerrar cada tarea:

```powershell
Set-Location $F
$touched = @(git diff --name-only origin/develop -- 'src/*.ts' 'src/*.tsx') + @(git ls-files --others --exclude-standard -- 'src/*.ts' 'src/*.tsx') | Where-Object { $_ -ne 'src/routeTree.gen.ts' }
bunx prettier --check $touched
bun run lint
```

Esperado: `All matched files use Prettier code style!` y oxlint sin errores nuevos (comparar contra la salida de F0 si el baseline ya traía advertencias).

## File Structure — frontend

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/features/auth/types/tenant-modules.ts` | F1 | Claves, etiquetas y mensajes del plan |
| `src/test/tenant-modules.ts` | F1 | Fixture de `/modules` para pruebas nuevas |
| `src/features/auth/hooks/use-tenant-modules.ts` (+ `.test.tsx`) | F2 | `useTenantModules()` |
| `src/components/module-gate.tsx` (+ `.test.tsx`) | F3 | El gate y su mensaje |
| `src/routes/_authenticated/{catalog,customers,companies,quotes,orders}.tsx` | F4 | Layouts con gate |
| `src/routes/_authenticated/module-gates.test.tsx` | F4–F8 | Gates de ruta de punta a punta |
| `src/features/customers/components/quotes-history-card.tsx`, `orders-history-card.tsx` | F7 | Una tarjeta, una consulta |

**Modificar**

| Archivo | Tarea |
|---|---|
| `src/features/auth/services/tenancy.api.ts` (+ `.test.ts`) | F1 |
| `src/routeTree.gen.ts` (regenerado) | F4 |
| `src/routes/_authenticated/reports/{orders,quotations,price-changes,customers}.tsx`, `reports/reports-routes.test.tsx` | F5 |
| `src/features/quotes/hooks/use-quote-order.ts` (+ `.test.tsx`), `src/routes/_authenticated/quotes/$quoteId/order.tsx`, `quotes/$quoteId/index.test.tsx` | F6 |
| `src/features/customers/components/customer-history-sections.tsx` (+ `.test.tsx`) | F7 |
| `src/routes/_authenticated/settings/orders-export-columns.tsx`, `settings/index.tsx`, `settings/index.test.tsx`, `src/features/tenant-settings/pages/tenant-settings-page.tsx`, `src/features/tenant-settings/services/orders-export-layout.api.ts` (+ `.test.ts`) | F8 |
| `src/components/app-shell/sidebar-nav-items.ts`, `src/features/auth/services/landing.ts` (+ `.test.ts`) | F9 |
| `src/features/roles/roles-page.tsx` (+ `.test.tsx`) | F10 |

---

### Task F0: Worktree y baseline del frontend

**Interfaces:**
- Produces: `$env:TEMP\qep-front-baseline-failed.txt`, que consume F11.

- [ ] **Step 1: Worktree desde `origin/develop`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git fetch origin
git worktree add -b feature/modulos-por-tenant ..\qep-frontend-worktrees\modulos-por-tenant origin/develop
$F = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\modulos-por-tenant"
Set-Location $F
git branch --show-current
gentle-ai codegraph init --cwd $F
bun install --frozen-lockfile
```

Esperado: `feature/modulos-por-tenant`. (El checkout principal del frontend está en `main`; no se toca.)

- [ ] **Step 2: Baseline de la suite, el lint y el build**

```powershell
Set-Location $F
$report = Join-Path $env:TEMP "qep-front-baseline.json"
bun run test --reporter=json --outputFile=$report
(Get-Content -Raw $report | ConvertFrom-Json).testResults |
    ForEach-Object { $_.assertionResults } |
    Where-Object { $_.status -eq 'failed' } |
    ForEach-Object { $_.fullName } |
    Sort-Object -Unique |
    Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-front-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-front-baseline-failed.txt")
bun run lint
bun run build
git status --short
```

Esperado: la lista de fallas previas (la memoria del repo dice que el frontend no corre Vitest en CI: puede haber rojas de antes; se comparan por nombre, no por archivo). `bun run build` en verde y `git status` limpio (si el build regenera `routeTree.gen.ts` con diferencias, anotarlo: es deuda previa y se commitea aparte con `chore: regenerar routeTree.gen.ts` antes de F1).

---

### Task F1: Contrato de `/modules` en la SPA

**Files:**
- Create: `src/features/auth/types/tenant-modules.ts`
- Modify: `src/features/auth/services/tenancy.api.ts` (al final)
- Create: `src/test/tenant-modules.ts`
- Test: `src/features/auth/services/tenancy.api.test.ts`

**Interfaces:**
- Produces: `TENANT_MODULE_KEYS`, `type TenantModuleKey`, `MODULE_LABELS`, `isTenantModuleKey(value: string): value is TenantModuleKey`, `MODULE_NOT_IN_PLAN_MESSAGE`; en `tenancy.api.ts`: `interface TenantModuleItem { key; enabled; contracted; missingDependencies: TenantModuleKey[] }`, `interface TenantModules { tenantId: string; modules: TenantModuleItem[] }`, `class TenantModulesPayloadError extends Error`, `tenantModulesQueryKey(tenantId) = ['tenancy','modules',tenantId]`, `fetchTenantModules(tenantId): Promise<TenantModules>`; en `src/test/tenant-modules.ts`: `tenantModulesResponse(overrides?, tenantId?)`, `ALL_TENANT_MODULES_ON`, `MODULE_OFF`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Agregar a `src/features/auth/services/tenancy.api.test.ts`. Los tres nombres nuevos de `tenancy.api` van **dentro del import existente** de ese módulo (no un segundo `import` del mismo archivo); el de `@/test/tenant-modules` es nuevo:

```ts
// added to the existing import from '@/features/auth/services/tenancy.api':
//   fetchTenantModules, tenantModulesQueryKey, TenantModulesPayloadError
import { ALL_TENANT_MODULES_ON } from '@/test/tenant-modules'

describe('fetchTenantModules', () => {
  it('reads the modules of the given tenant, carrying X-Tenant-Id', async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse(200, ALL_TENANT_MODULES_ON))

    const result = await fetchTenantModules(TENANT)

    expect(lastUrl()).toBe(`/api/v1/tenants/${TENANT}/modules`)
    const init = vi.mocked(fetch).mock.calls.at(-1)![1]
    expect(new Headers(init?.headers).get('X-Tenant-Id')).toBe(TENANT)
    expect(result.modules.map((module) => module.key)).toEqual([
      'catalog',
      'customers',
      'companies',
      'quotations',
      'orders',
      'reporting',
      'pos',
    ])
  })

  it('keys the cache by tenant', () => {
    expect(tenantModulesQueryKey(TENANT)).toEqual(['tenancy', 'modules', TENANT])
  })

  // The 17 existing route tests answer every unknown URL with `{}`. Rejecting that shape is what
  // keeps them green untouched: the hook ends in 'error' and the gates let the page through.
  it.each([
    ['an empty object', {}],
    ['modules that is not an array', { tenantId: TENANT, modules: 'nope' }],
    [
      'an item without enabled',
      {
        tenantId: TENANT,
        modules: [{ key: 'catalog', contracted: true, missingDependencies: [] }],
      },
    ],
    [
      'an item whose missingDependencies is not an array',
      {
        tenantId: TENANT,
        modules: [
          { key: 'catalog', enabled: true, contracted: true, missingDependencies: null },
        ],
      },
    ],
  ])('rejects %s', async (_, body) => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse(200, body))

    await expect(fetchTenantModules(TENANT)).rejects.toBeInstanceOf(
      TenantModulesPayloadError,
    )
  })

  it('drops an item whose key the SPA does not know', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, {
        tenantId: TENANT,
        modules: [
          ...ALL_TENANT_MODULES_ON.modules,
          { key: 'inventory', enabled: true, contracted: true, missingDependencies: [] },
        ],
      }),
    )

    const result = await fetchTenantModules(TENANT)

    expect(result.modules).toHaveLength(7)
    expect(result.modules.some((module) => String(module.key) === 'inventory')).toBe(false)
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/auth/services/tenancy.api.test.ts
```

Esperado: `Error: Failed to resolve import "@/test/tenant-modules"` / `fetchTenantModules is not a function` — la suite falla al cargar.

- [ ] **Step 3: Implementar**

`src/features/auth/types/tenant-modules.ts`:

```ts
/**
 * Commercial modules a tenant can have (spec 2026-10-07, «Catálogo de módulos»). Same keys and same
 * order as `TenantModuleKeys.All` in the backend, which is also the order `/modules` answers in.
 * The labels live here because the backend sends keys only.
 */
export const TENANT_MODULE_KEYS = [
  'catalog',
  'customers',
  'companies',
  'quotations',
  'orders',
  'reporting',
  'pos',
] as const

export type TenantModuleKey = (typeof TENANT_MODULE_KEYS)[number]

export const MODULE_LABELS: Record<TenantModuleKey, string> = {
  catalog: 'Catálogo',
  customers: 'Clientes',
  companies: 'Empresas',
  quotations: 'Cotizaciones',
  orders: 'Pedidos',
  reporting: 'Reportes',
  pos: 'Punto de venta',
}

export const MODULE_NOT_IN_PLAN_MESSAGE =
  'Este módulo no está incluido en el plan de tu empresa.'

export function isTenantModuleKey(value: string): value is TenantModuleKey {
  return (TENANT_MODULE_KEYS as readonly string[]).includes(value)
}
```

Al final de `src/features/auth/services/tenancy.api.ts` (con `import { isTenantModuleKey, type TenantModuleKey } from '@/features/auth/types/tenant-modules'` arriba):

```ts
/** Mirrors TenantModuleResponse (qep-backend/src/Api/TenantModulesEndpoints.cs). */
export interface TenantModuleItem {
  key: TenantModuleKey
  /** Effective: contracted and with every dependency on. */
  enabled: boolean
  /** The row exists. Without both flags a screen cannot tell "not in your plan" from "in your
   * plan, but missing another module". */
  contracted: boolean
  /** Root causes: transitive dependencies that are not contracted. */
  missingDependencies: TenantModuleKey[]
}

/** Mirrors TenantModulesResponse: always the seven, in the backend order. */
export interface TenantModules {
  tenantId: string
  modules: TenantModuleItem[]
}

/** A `/modules` answer with the wrong shape. Retrying cannot fix it, so the hook does not retry. */
export class TenantModulesPayloadError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'TenantModulesPayloadError'
  }
}

export const tenantModulesQueryKey = (tenantId: string) =>
  ['tenancy', 'modules', tenantId] as const

/**
 * What the tenant bought. Like `fetchEffectivePermissions`, it carries the tenant in X-Tenant-Id:
 * the backend reads the tenant claim from the header, not from the path.
 *
 * Validates the shape instead of trusting it. A malformed answer is an error, never "no modules":
 * hiding every module because of a broken payload would leave the app with no way out, while the
 * backend keeps enforcing either way.
 */
export async function fetchTenantModules(tenantId: string): Promise<TenantModules> {
  const payload = await apiRequest<unknown>(`/api/v1/tenants/${tenantId}/modules`, {
    headers: { 'X-Tenant-Id': tenantId },
  })
  return parseTenantModules(payload, tenantId)
}

function parseTenantModules(payload: unknown, tenantId: string): TenantModules {
  if (typeof payload !== 'object' || payload === null) {
    throw new TenantModulesPayloadError('The modules answer is not an object.')
  }
  const { modules, tenantId: answeredTenant } = payload as Record<string, unknown>
  if (!Array.isArray(modules)) {
    throw new TenantModulesPayloadError('The modules answer has no modules array.')
  }

  const items: TenantModuleItem[] = []
  for (const raw of modules) {
    if (typeof raw !== 'object' || raw === null) {
      throw new TenantModulesPayloadError('A module is not an object.')
    }
    const { key, enabled, contracted, missingDependencies } = raw as Record<string, unknown>
    if (
      typeof key !== 'string' ||
      typeof enabled !== 'boolean' ||
      typeof contracted !== 'boolean' ||
      !Array.isArray(missingDependencies)
    ) {
      throw new TenantModulesPayloadError('A module does not have the expected fields.')
    }
    // A key the SPA does not know yet (a module newer than this build) is dropped, not an error.
    if (!isTenantModuleKey(key)) continue
    items.push({
      key,
      enabled,
      contracted,
      missingDependencies: missingDependencies.filter(
        (dependency): dependency is TenantModuleKey =>
          typeof dependency === 'string' && isTenantModuleKey(dependency),
      ),
    })
  }

  return {
    tenantId: typeof answeredTenant === 'string' ? answeredTenant : tenantId,
    modules: items,
  }
}
```

`src/test/tenant-modules.ts`:

```ts
import {
  TENANT_MODULE_KEYS,
  type TenantModuleKey,
} from '@/features/auth/types/tenant-modules'

type ModuleOverride = {
  enabled?: boolean
  contracted?: boolean
  missingDependencies?: TenantModuleKey[]
}

/** A module that is not in the plan at all. */
export const MODULE_OFF: ModuleOverride = { enabled: false, contracted: false }

/**
 * The `/modules` answer with every module on, minus the overrides. Same role as
 * `ALL_SIDEBAR_PERMISSIONS`: only the new tests need it — the existing ones get `{}` from their
 * catch-all, which the client rejects, and the gates let them through.
 */
export function tenantModulesResponse(
  overrides: Partial<Record<TenantModuleKey, ModuleOverride>> = {},
  tenantId = 'tenant',
) {
  return {
    tenantId,
    modules: TENANT_MODULE_KEYS.map((key) => ({
      key,
      enabled: true,
      contracted: true,
      missingDependencies: [] as TenantModuleKey[],
      ...overrides[key],
    })),
  }
}

export const ALL_TENANT_MODULES_ON = tenantModulesResponse()
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location $F
bun run test --run src/features/auth/services/tenancy.api.test.ts
```

Esperado: `Test Files  1 passed`, todas las pruebas en verde (las de antes y las nuevas).

- [ ] **Step 5: Formato, lint y commit**

```powershell
Set-Location $F
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/features/auth/types/tenant-modules.ts src/features/auth/services/tenancy.api.ts src/features/auth/services/tenancy.api.test.ts src/test/tenant-modules.ts; git commit -m "feat(modules): contrato de GET /modules con validación de forma"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F2: `useTenantModules()`

**Files:**
- Create: `src/features/auth/hooks/use-tenant-modules.ts`
- Test: `src/features/auth/hooks/use-tenant-modules.test.tsx`

**Interfaces:**
- Consumes: `fetchTenantModules`, `tenantModulesQueryKey`, `TenantModulesPayloadError` (F1); `useActiveTenant` (`@/features/auth/hooks/use-active-tenant`).
- Produces: `type TenantModulesStatus = 'loading' | 'ready' | 'denied' | 'error'`; `useTenantModules(): { isEnabled(key: TenantModuleKey): boolean; item(key: TenantModuleKey): TenantModuleItem | undefined; status: TenantModulesStatus }`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/auth/hooks/use-tenant-modules.test.tsx`:

```tsx
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ACTIVE_TENANT_STORAGE_KEY } from '@/features/auth/hooks/use-active-tenant'
import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'
import { createSessionAwareQueryClient } from '@/features/auth/services/session-invalidation'
import { tenantModulesQueryKey } from '@/features/auth/services/tenancy.api'
import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function wrapperFor(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  }
}

/** One tenant in the session, so it auto-selects and the modules can be fetched. */
function routeFetch(modulesResponse: () => Promise<Response>) {
  vi.mocked(fetch).mockImplementation((input) => {
    const url = String(input)
    if (url.endsWith('/auth/me')) {
      return Promise.resolve(
        json(200, { userId: 'u-1', email: 'someone@qcode.co', activeTenantIds: [TENANT] }),
      )
    }
    if (url.endsWith('/modules')) return modulesResponse()
    return Promise.resolve(new Response(null, { status: 404 }))
  })
}

function modulesRequests() {
  return vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/modules'))
}

describe('useTenantModules', () => {
  beforeEach(() => {
    localStorage.setItem(ACTIVE_TENANT_STORAGE_KEY, TENANT)
  })

  it('reports each module as the backend does, cached under the tenant', async () => {
    routeFetch(() =>
      Promise.resolve(json(200, tenantModulesResponse({ orders: MODULE_OFF }, TENANT))),
    )
    const queryClient = createSessionAwareQueryClient()

    const { result } = renderHook(() => useTenantModules(), { wrapper: wrapperFor(queryClient) })

    await waitFor(() => expect(result.current.status).toBe('ready'))
    expect(result.current.isEnabled('quotations')).toBe(true)
    expect(result.current.isEnabled('orders')).toBe(false)
    expect(result.current.item('orders')?.contracted).toBe(false)
    expect(queryClient.getQueryData(tenantModulesQueryKey(TENANT))).toBeDefined()
  })

  // Deny while loading: a screen of a module that may be off must not mount and fire queries.
  it('enables nothing while loading', async () => {
    routeFetch(() => new Promise<Response>(() => {}))

    const { result } = renderHook(() => useTenantModules(), {
      wrapper: wrapperFor(createSessionAwareQueryClient()),
    })

    await waitFor(() => expect(modulesRequests()).toHaveLength(1))
    expect(result.current.status).toBe('loading')
    expect(result.current.isEnabled('catalog')).toBe(false)
  })

  // Fail open on the client: the backend masks permissions anyway, and hiding everything over a
  // transport failure leaves the app with no way out.
  it('enables everything when the tenant answer is denied', async () => {
    routeFetch(() => Promise.resolve(json(403, { code: 'authorization.denied' })))

    const { result } = renderHook(() => useTenantModules(), {
      wrapper: wrapperFor(createSessionAwareQueryClient()),
    })

    await waitFor(() => expect(result.current.status).toBe('denied'))
    expect(result.current.isEnabled('orders')).toBe(true)
  })

  it('enables everything when the request fails', async () => {
    routeFetch(() => Promise.resolve(new Response(null, { status: 500 })))

    const { result } = renderHook(() => useTenantModules(), {
      wrapper: wrapperFor(createSessionAwareQueryClient()),
    })

    // One retry, like usePermissions: TanStack waits its default second before it.
    await waitFor(() => expect(result.current.status).toBe('error'), { timeout: 3000 })
    expect(result.current.isEnabled('orders')).toBe(true)
  })

  it('does not retry a malformed answer', async () => {
    routeFetch(() => Promise.resolve(json(200, {})))

    const { result } = renderHook(() => useTenantModules(), {
      wrapper: wrapperFor(createSessionAwareQueryClient()),
    })

    await waitFor(() => expect(result.current.status).toBe('error'))
    expect(modulesRequests()).toHaveLength(1)
    expect(result.current.isEnabled('catalog')).toBe(true)
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/auth/hooks/use-tenant-modules.test.tsx
```

Esperado: `Failed to resolve import "@/features/auth/hooks/use-tenant-modules"`.

- [ ] **Step 3: Implementar**

`src/features/auth/hooks/use-tenant-modules.ts`:

```ts
import { useQuery } from '@tanstack/react-query'
import { useCallback } from 'react'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  fetchTenantModules,
  tenantModulesQueryKey,
  TenantModulesPayloadError,
  type TenantModuleItem,
} from '@/features/auth/services/tenancy.api'
import type { TenantModuleKey } from '@/features/auth/types/tenant-modules'
import { ApiError } from '@/lib/api-client'

export type TenantModulesStatus = 'loading' | 'ready' | 'denied' | 'error'

export interface UseTenantModulesResult {
  /** 'ready': the module's own `enabled`. 'error' / 'denied': true (fail open, see below).
   * 'loading': false. */
  isEnabled: (key: TenantModuleKey) => boolean
  item: (key: TenantModuleKey) => TenantModuleItem | undefined
  status: TenantModulesStatus
}

/**
 * The modules the active tenant bought (spec 2026-10-07). Usability, not authorization: the
 * backend masks the permissions of a module that is off on every request, whatever this says.
 *
 * Fails open on 'error' and 'denied', like `visibleSidebarItems` with 'error': hiding every module
 * over a transport failure or a malformed answer leaves the app with no way out, and the backend
 * still enforces. Same `staleTime` and `retry` as `usePermissions`, except that a malformed answer
 * is not retried: retrying cannot fix it.
 */
export function useTenantModules(): UseTenantModulesResult {
  const { tenantId } = useActiveTenant()

  const query = useQuery({
    queryKey: tenantModulesQueryKey(tenantId ?? 'none'),
    queryFn: () => fetchTenantModules(tenantId!),
    enabled: tenantId !== null,
    staleTime: 5 * 60 * 1000,
    retry: (failureCount, error) =>
      !(error instanceof TenantModulesPayloadError) &&
      !(error instanceof ApiError && error.status === 403) &&
      failureCount < 1,
  })

  const modules = query.data?.modules
  const status = resolveStatus(query, tenantId)

  const item = useCallback(
    (key: TenantModuleKey) => modules?.find((module) => module.key === key),
    [modules],
  )

  const isEnabled = useCallback(
    (key: TenantModuleKey) => {
      if (status === 'loading') return false
      if (status !== 'ready') return true
      return item(key)?.enabled ?? false
    },
    [status, item],
  )

  return { isEnabled, item, status }
}

function resolveStatus(
  query: { isPending: boolean; isError: boolean; error: unknown },
  tenantId: string | null,
): TenantModulesStatus {
  if (tenantId === null || (query.isPending && !query.isError)) return 'loading'
  if (query.error instanceof ApiError && query.error.status === 403) return 'denied'
  if (query.isError) return 'error'
  return 'ready'
}
```

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/auth/hooks/use-tenant-modules.test.tsx
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/features/auth/hooks/use-tenant-modules.ts src/features/auth/hooks/use-tenant-modules.test.tsx; git commit -m "feat(modules): useTenantModules con fail open ante error"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: 5 superadas. Correr el bloque de formato y lint antes del commit.

---

### Task F3: `ModuleGate`

**Files:**
- Create: `src/components/module-gate.tsx`
- Test: `src/components/module-gate.test.tsx`

**Interfaces:**
- Consumes: `useTenantModules` (F2), `MODULE_LABELS`, `MODULE_NOT_IN_PLAN_MESSAGE`, `TENANT_MODULE_KEYS` (F1).
- Produces: `<ModuleGate modules={[screen, ...sources]}>children</ModuleGate>` con `modules: readonly [TenantModuleKey, ...TenantModuleKey[]]`; `moduleGateMessage(modules, item): string | null`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/components/module-gate.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ModuleGate } from '@/components/module-gate'
import type { TenantModulesStatus } from '@/features/auth/hooks/use-tenant-modules'
import type { TenantModuleItem } from '@/features/auth/services/tenancy.api'
import type { TenantModuleKey } from '@/features/auth/types/tenant-modules'
import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'

const state = vi.hoisted(() => ({
  status: 'ready' as TenantModulesStatus,
  modules: [] as TenantModuleItem[],
}))

vi.mock('@/features/auth/hooks/use-tenant-modules', () => ({
  useTenantModules: () => ({
    status: state.status,
    item: (key: TenantModuleKey) => state.modules.find((module) => module.key === key),
    isEnabled: (key: TenantModuleKey) =>
      state.modules.find((module) => module.key === key)?.enabled ?? false,
  }),
}))

function withModules(overrides: Parameters<typeof tenantModulesResponse>[0]) {
  state.status = 'ready'
  state.modules = tenantModulesResponse(overrides).modules
}

function renderGate(modules: readonly [TenantModuleKey, ...TenantModuleKey[]]) {
  return render(
    <ModuleGate modules={modules}>
      <p>contenido del módulo</p>
    </ModuleGate>,
  )
}

describe('ModuleGate', () => {
  beforeEach(() => withModules({}))

  it('shows its own loading card while the modules load', () => {
    state.status = 'loading'

    renderGate(['orders'])

    expect(screen.getByText('Cargando módulos…')).toBeInTheDocument()
    expect(screen.queryByText('contenido del módulo')).not.toBeInTheDocument()
  })

  it('says the module is not in the plan when the screen module is not contracted', () => {
    withModules({ orders: MODULE_OFF })

    renderGate(['orders'])

    expect(
      screen.getByText('Este módulo no está incluido en el plan de tu empresa.'),
    ).toBeInTheDocument()
    expect(screen.queryByText('contenido del módulo')).not.toBeInTheDocument()
  })

  it('names one missing dependency in singular', () => {
    withModules({
      customers: MODULE_OFF,
      quotations: { enabled: false, missingDependencies: ['customers'] },
    })

    renderGate(['quotations'])

    expect(
      screen.getByText(
        'Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
  })

  it('names two missing dependencies in plural, in the backend order', () => {
    withModules({
      catalog: MODULE_OFF,
      customers: MODULE_OFF,
      quotations: { enabled: false, missingDependencies: ['customers', 'catalog'] },
    })

    renderGate(['quotations'])

    expect(
      screen.getByText(
        'Este módulo necesita Catálogo y Clientes, que no están incluidos en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
  })

  it('names an uncontracted source instead of blaming the screen module', () => {
    withModules({ customers: MODULE_OFF })

    renderGate(['reporting', 'customers'])

    expect(
      screen.getByText(
        'Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
    expect(
      screen.queryByText('Este módulo no está incluido en el plan de tu empresa.'),
    ).not.toBeInTheDocument()
  })

  // Review Focus 4: the source is contracted but off for lack of its own dependency. The message
  // names the root cause the backend reported (Clientes), not the source (Pedidos).
  it('names the root cause of a contracted source that is off', () => {
    withModules({
      customers: MODULE_OFF,
      quotations: { enabled: false, missingDependencies: ['customers'] },
      orders: { enabled: false, missingDependencies: ['customers'] },
    })

    renderGate(['reporting', 'orders'])

    expect(
      screen.getByText(
        'Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
  })

  it.each<TenantModulesStatus>(['error', 'denied'])(
    'lets the page through when the modules answer is %s',
    (status) => {
      state.status = status
      state.modules = []

      renderGate(['orders'])

      expect(screen.getByText('contenido del módulo')).toBeInTheDocument()
    },
  )

  it('renders the page when every module it needs is on', () => {
    renderGate(['reporting', 'orders'])

    expect(screen.getByText('contenido del módulo')).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/components/module-gate.test.tsx
```

Esperado: `Failed to resolve import "@/components/module-gate"`.

- [ ] **Step 3: Implementar**

`src/components/module-gate.tsx`:

```tsx
import type { ReactNode } from 'react'

import { PageContainer } from '@/components/page-container'
import { Card } from '@/components/ui/card'
import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'
import type { TenantModuleItem } from '@/features/auth/services/tenancy.api'
import {
  MODULE_LABELS,
  MODULE_NOT_IN_PLAN_MESSAGE,
  TENANT_MODULE_KEYS,
  type TenantModuleKey,
} from '@/features/auth/types/tenant-modules'

type GateModules = readonly [TenantModuleKey, ...TenantModuleKey[]]

/**
 * Mounts a module's screen only when the tenant has that module (spec 2026-10-07). `modules[0]` is
 * the screen's module and the rest are its sources (`['reporting', 'customers']`). Lives in
 * `components/` because five features use it (SDD-ADR-07).
 *
 * Used at route level, as a layout or as the outermost element of the route component: several
 * routes call their hooks in the route itself, and wrapping only the page would let those queries
 * fire and fail with 403. While loading it shows its own card (not "Cargando permisos...": both
 * gates can appear one after the other). On 'error' or 'denied' it lets the page through, like the
 * sidebar does: the backend masks permissions anyway.
 *
 * Usability, not authorization (AGENTS.md §6).
 */
export function ModuleGate({
  modules,
  children,
}: {
  modules: GateModules
  children: ReactNode
}) {
  const { status, item } = useTenantModules()

  if (status === 'loading') return <GateCard>Cargando módulos…</GateCard>
  if (status !== 'ready') return children

  const message = moduleGateMessage(modules, item)
  return message === null ? children : <GateCard>{message}</GateCard>
}

function GateCard({ children }: { children: ReactNode }) {
  return (
    <PageContainer>
      <Card className="p-8 text-center text-sm text-muted-foreground">{children}</Card>
    </PageContainer>
  )
}

/**
 * The explanation for a screen that cannot open, or null when it can. In order: the screen module
 * not contracted; then, without repeats and in the backend order, the uncontracted sources plus
 * the root causes of every requested module that is contracted but off.
 */
export function moduleGateMessage(
  modules: GateModules,
  item: (key: TenantModuleKey) => TenantModuleItem | undefined,
): string | null {
  const [screenModule, ...sources] = modules
  if (!item(screenModule)?.contracted) return MODULE_NOT_IN_PLAN_MESSAGE

  const missing = new Set<TenantModuleKey>()
  for (const source of sources) {
    if (!item(source)?.contracted) missing.add(source)
  }
  for (const key of modules) {
    const module = item(key)
    if (module?.contracted && !module.enabled) {
      for (const dependency of module.missingDependencies) missing.add(dependency)
    }
  }

  const labels = TENANT_MODULE_KEYS.filter((key) => missing.has(key)).map(
    (key) => MODULE_LABELS[key],
  )
  if (labels.length === 0) return null
  if (labels.length === 1) {
    return `Este módulo necesita ${labels[0]}, que no está incluido en el plan de tu empresa.`
  }
  return `Este módulo necesita ${joinWithAnd(labels)}, que no están incluidos en el plan de tu empresa.`
}

function joinWithAnd(labels: string[]): string {
  return `${labels.slice(0, -1).join(', ')} y ${labels.at(-1)}`
}
```

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/components/module-gate.test.tsx
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/components/module-gate.tsx src/components/module-gate.test.tsx; git commit -m "feat(modules): ModuleGate explica qué módulo falta en el plan"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: 9 superadas. Formato y lint antes del commit.

---

### Task F4: Layouts con gate para cada carpeta de módulo

**Files:**
- Create: `src/routes/_authenticated/catalog.tsx`, `customers.tsx`, `companies.tsx`, `quotes.tsx`, `orders.tsx`
- Modify (regenerado): `src/routeTree.gen.ts`
- Test: `src/routes/_authenticated/module-gates.test.tsx`

**Interfaces:**
- Consumes: `ModuleGate` (F3), `tenantModulesResponse`, `MODULE_OFF` (F1), `ALL_SIDEBAR_PERMISSIONS` (`@/test/permissions`), `renderRoute` (`@/test/render-route`).
- Produces: en `module-gates.test.tsx`, `stubBackend(modules: unknown, options?: { permissions?: string[] })` que devuelve `{ requested: () => string[] }`; F5, F6 y F8 le suman casos.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/routes/_authenticated/module-gates.test.tsx`:

```tsx
import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { MODULE_NOT_IN_PLAN_MESSAGE } from '@/features/auth/types/tenant-modules'
import { ALL_SIDEBAR_PERMISSIONS } from '@/test/permissions'
import { renderRoute } from '@/test/render-route'
import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

const session = { userId: 'u-1', email: 'owner@qcode.co', activeTenantIds: [TENANT] }

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/**
 * Session, permissions and `/modules` answered for real; everything else gets `{}`. Records every
 * URL so a test can prove the gated child never asked for its data.
 */
function stubBackend(
  modules: unknown,
  { permissions = ALL_SIDEBAR_PERMISSIONS }: { permissions?: string[] } = {},
) {
  const requested: string[] = []
  vi.mocked(fetch).mockImplementation((input: RequestInfo | URL) => {
    const url = String(input)
    requested.push(url)
    if (url.includes('/auth/preferences')) {
      return Promise.resolve(json(200, { colorScheme: 'botanical', mode: 'light' }))
    }
    if (url.includes('/auth/me')) return Promise.resolve(json(200, session))
    if (url.includes('/authorization/me')) {
      return Promise.resolve(json(200, { tenantId: TENANT, userId: 'u-1', permissions }))
    }
    if (url.endsWith('/modules')) return Promise.resolve(json(200, modules))
    return Promise.resolve(json(200, {}))
  })
  return { requested: () => requested }
}

const tenantApi = (path: string) => `/api/v1/tenants/${TENANT}${path}`

describe('module layouts', () => {
  it.each([
    ['/customers', 'customers', '/customers'],
    ['/companies', 'companies', '/companies'],
    ['/catalog/products', 'catalog', '/catalog/'],
    ['/quotes', 'quotations', '/quotations'],
    ['/orders', 'orders', '/orders'],
  ] as const)(
    '%s explains the plan and asks nothing of a module that is off',
    async (path, module, apiPrefix) => {
      const backend = stubBackend(tenantModulesResponse({ [module]: MODULE_OFF }, TENANT))

      renderRoute(path)

      expect(await screen.findByText(MODULE_NOT_IN_PLAN_MESSAGE)).toBeInTheDocument()
      expect(backend.requested().filter((url) => url.includes(tenantApi(apiPrefix)))).toEqual([])
    },
  )

  it('names the root cause on /quotes when customers is missing', async () => {
    stubBackend(
      tenantModulesResponse(
        {
          customers: MODULE_OFF,
          quotations: { enabled: false, missingDependencies: ['customers'] },
          orders: { enabled: false, missingDependencies: ['customers'] },
        },
        TENANT,
      ),
    )

    renderRoute('/quotes')

    expect(
      await screen.findByText(
        'Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/routes/_authenticated/module-gates.test.tsx
```

Esperado: los 6 casos fallan con `Unable to find an element with the text: Este módulo no está incluido…` (la página se monta sin gate y sale su consulta).

- [ ] **Step 3: Crear los cinco layouts**

`src/routes/_authenticated/customers.tsx`:

```tsx
import { createFileRoute, Outlet } from '@tanstack/react-router'

import { ModuleGate } from '@/components/module-gate'

/**
 * Layout for every `/customers/*` route (spec 2026-10-07). The gate sits here and not in each page
 * because several child routes call their hooks in the route component itself
 * (`customers/$customerId.tsx`): below a closed gate nothing mounts, so no query fires.
 */
export const Route = createFileRoute('/_authenticated/customers')({
  component: CustomersLayout,
})

function CustomersLayout() {
  return (
    <ModuleGate modules={['customers']}>
      <Outlet />
    </ModuleGate>
  )
}
```

Los otros cuatro, iguales salvo la ruta, el nombre y el módulo (y el comentario, que nombra el hijo que llama hooks en la ruta):

| Archivo | `createFileRoute(...)` | Componente | `modules` | Hijo citado en el comentario |
|---|---|---|---|---|
| `catalog.tsx` | `'/_authenticated/catalog'` | `CatalogLayout` | `['catalog']` | `catalog/products/new.tsx` |
| `companies.tsx` | `'/_authenticated/companies'` | `CompaniesLayout` | `['companies']` | `companies/$companyId.tsx` |
| `quotes.tsx` | `'/_authenticated/quotes'` | `QuotesLayout` | `['quotations']` | `quotes/$quoteId.tsx`; además: el gate queda por fuera de los `QuotesReadGate` de las hojas |
| `orders.tsx` | `'/_authenticated/orders'` | `OrdersLayout` | `['orders']` | `orders/$orderId/index.tsx` |

- [ ] **Step 4: Regenerar el árbol de rutas**

```powershell
Set-Location $F
bun run build
git diff --stat -- src/routeTree.gen.ts
```

Esperado: build en verde (`tsc -b` sin errores) y `routeTree.gen.ts` modificado: los cinco layouts nuevos y sus hijos colgando de ellos. Nada más cambia en el árbol.

- [ ] **Step 5: Ver el GREEN y la regresión de las rutas que montan un layout**

```powershell
Set-Location $F
bun run test --run src/routes/_authenticated/module-gates.test.tsx
bun run test --run src/routes/_authenticated/catalog src/routes/_authenticated/companies src/routes/_authenticated/customers src/routes/_authenticated/orders src/routes/_authenticated/quotes src/components/app-shell/app-shell.test.tsx src/features/account/components/applied-theme.test.tsx src/routes/index.test.tsx
```

Esperado: la primera en verde. La segunda en verde **sin tocar esos archivos**: su catch-all responde `{}` a `/modules`, el hook queda en `'error'` y el gate deja pasar (spec, decisión 36).

- [ ] **Step 6: Formato, lint y commit**

```powershell
Set-Location $F
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/routes/_authenticated/catalog.tsx src/routes/_authenticated/customers.tsx src/routes/_authenticated/companies.tsx src/routes/_authenticated/quotes.tsx src/routes/_authenticated/orders.tsx src/routeTree.gen.ts src/routes/_authenticated/module-gates.test.tsx; git commit -m "feat(modules): layouts con gate para catálogo, clientes, empresas, cotizaciones y pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F5: Reportes con su módulo y su fuente

**Files:**
- Modify: `src/routes/_authenticated/reports/orders.tsx`, `quotations.tsx`, `price-changes.tsx`, `customers.tsx`
- Test: `src/routes/_authenticated/module-gates.test.tsx`

**Interfaces:**
- Consumes: `ModuleGate` (F3), `stubBackend` de `module-gates.test.tsx` (F4).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `module-gates.test.tsx`:

```tsx
describe('report routes', () => {
  it.each([
    ['/reports/orders', 'orders'],
    ['/reports/quotations', 'quotations'],
    ['/reports/price-changes', 'catalog'],
    ['/reports/customers', 'customers'],
  ] as const)('%s explains the plan when reporting is off', async (path) => {
    const backend = stubBackend(tenantModulesResponse({ reporting: MODULE_OFF }, TENANT))

    renderRoute(path)

    expect(await screen.findByText(MODULE_NOT_IN_PLAN_MESSAGE)).toBeInTheDocument()
    expect(backend.requested().filter((url) => url.includes(tenantApi('/reports/')))).toEqual([])
  })

  it('names the source when reporting is on and customers is not in the plan', async () => {
    const backend = stubBackend(
      tenantModulesResponse(
        {
          customers: MODULE_OFF,
          quotations: { enabled: false, missingDependencies: ['customers'] },
          orders: { enabled: false, missingDependencies: ['customers'] },
        },
        TENANT,
      ),
    )

    renderRoute('/reports/customers')

    expect(
      await screen.findByText(
        'Este módulo necesita Clientes, que no está incluido en el plan de tu empresa.',
      ),
    ).toBeInTheDocument()
    expect(backend.requested().filter((url) => url.includes('/classifications'))).toEqual([])
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/routes/_authenticated/module-gates.test.tsx -t "report routes"
```

Esperado: los 5 fallan (las páginas se montan y piden su reporte).

- [ ] **Step 3: Envolver las cuatro rutas**

`src/routes/_authenticated/reports/customers.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router'

import { ModuleGate } from '@/components/module-gate'
import { CustomersReportPage } from '@/features/reports/pages/customers-report-page'

export const Route = createFileRoute('/_authenticated/reports/customers')({
  component: CustomersReportRoute,
})

// Every report needs reporting and its source (spec 2026-10-07): without customers this one names
// Clientes instead of saying that Reportes is not in the plan.
function CustomersReportRoute() {
  return (
    <ModuleGate modules={['reporting', 'customers']}>
      <CustomersReportPage />
    </ModuleGate>
  )
}
```

Igual para las otras tres, conservando el import de su página actual:

| Archivo | Componente de ruta | Página | `modules` |
|---|---|---|---|
| `reports/orders.tsx` | `OrdersReportRoute` | la que hoy es su `component` | `['reporting', 'orders']` |
| `reports/quotations.tsx` | `QuotationsReportRoute` | ídem | `['reporting', 'quotations']` |
| `reports/price-changes.tsx` | `PriceChangesReportRoute` | ídem | `['reporting', 'catalog']` |

`reports/log.tsx` y `reports/index.tsx` no cambian: el log es núcleo.

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/routes/_authenticated/module-gates.test.tsx src/routes/_authenticated/reports/reports-routes.test.tsx
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/routes/_authenticated/reports/orders.tsx src/routes/_authenticated/reports/quotations.tsx src/routes/_authenticated/reports/price-changes.tsx src/routes/_authenticated/reports/customers.tsx src/routes/_authenticated/module-gates.test.tsx; git commit -m "feat(reports): cada reporte exige reporting y su fuente"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: verde; `reports-routes.test.tsx` sin tocar (catch-all `{}`). No hace falta regenerar el árbol: no hay rutas nuevas.

---

### Task F6: Cotizaciones no consultan pedidos sin `orders`

**Files:**
- Modify: `src/features/quotes/hooks/use-quote-order.ts`
- Modify: `src/features/quotes/hooks/use-quote-order.test.tsx`
- Modify: `src/routes/_authenticated/quotes/$quoteId/order.tsx:36-40`
- Modify: `src/routes/_authenticated/quotes/$quoteId/index.test.tsx`
- Test: `src/routes/_authenticated/module-gates.test.tsx`

**Interfaces:**
- Consumes: `useTenantModules` (F2), `usePermissions` (`@/features/auth/hooks/use-permission`), `ORDER_READ_PERMISSION` (`@/features/quotes/types/order`), `ModuleGate` (F3).
- Produces: `useQuoteOrder` con el mismo contrato (`order: Order | null | undefined`, `isLoading`, `isError`).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `use-quote-order.test.tsx`, debajo del `vi.mock` de `use-quotes-tenant` (agregar `beforeEach` al import de vitest y `import { ORDER_READ_PERMISSION } from '@/features/quotes/types/order'`):

```tsx
const gates = vi.hoisted(() => ({
  modulesStatus: 'ready' as 'loading' | 'ready' | 'denied' | 'error',
  ordersEnabled: true,
  permissionsStatus: 'ready' as 'loading' | 'ready' | 'denied' | 'error',
  granted: [] as string[],
}))

vi.mock('@/features/auth/hooks/use-tenant-modules', () => ({
  useTenantModules: () => ({
    status: gates.modulesStatus,
    isEnabled: (key: string) => (key === 'orders' ? gates.ordersEnabled : true),
    item: () => undefined,
  }),
}))

vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({
    status: gates.permissionsStatus,
    can: (permission: string) => gates.granted.includes(permission),
    sessionLost: false,
  }),
}))
```

Dentro del `describe`, antes de los casos existentes:

```tsx
  beforeEach(() => {
    gates.modulesStatus = 'ready'
    gates.ordersEnabled = true
    gates.permissionsStatus = 'ready'
    gates.granted = [ORDER_READ_PERMISSION]
  })
```

Y tres casos nuevos:

```tsx
  // Spec 2026-10-07: the order belongs to the orders module, read from screens of quotations.
  // Closed means "no order", like the 404 — not `undefined`, which the contract keeps for loading.
  it('asks nothing and reports no order with orders off', async () => {
    gates.ordersEnabled = false
    vi.mocked(fetch).mockImplementation(() => new Promise(() => {}))
    const { Wrapper } = createWrapper()

    const { result } = renderHook(() => useQuoteOrder(QUOTE), { wrapper: Wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.order).toBeNull()
    expect(orderRequests()).toHaveLength(0)
  })

  it('asks nothing and reports no order without the order read permission', async () => {
    gates.granted = []
    vi.mocked(fetch).mockImplementation(() => new Promise(() => {}))
    const { Wrapper } = createWrapper()

    const { result } = renderHook(() => useQuoteOrder(QUOTE), { wrapper: Wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.order).toBeNull()
    expect(orderRequests()).toHaveLength(0)
  })

  it('stays loading while the modules are unknown', () => {
    gates.modulesStatus = 'loading'
    gates.ordersEnabled = false
    vi.mocked(fetch).mockImplementation(() => new Promise(() => {}))
    const { Wrapper } = createWrapper()

    const { result } = renderHook(() => useQuoteOrder(QUOTE), { wrapper: Wrapper })

    expect(result.current.isLoading).toBe(true)
    expect(result.current.order).toBeUndefined()
    expect(orderRequests()).toHaveLength(0)
  })
```

En `quotes/$quoteId/index.test.tsx`, `stubBackend` gana un tercer parámetro opcional y la ruta a `/modules`:

```tsx
function stubBackend(
  quote: unknown,
  permissions: string[] = ['quotations.quotation.read'],
  modules: unknown = {},
) {
```

con, antes del catch-all:

```tsx
    if (url.endsWith('/modules')) return Promise.resolve(json(200, modules))
```

y el caso (agregar `import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'`):

```tsx
describe('/quotes/$quoteId — sin el módulo de pedidos', () => {
  it('paints the whole detail and never asks for its order', async () => {
    stubBackend(
      quotation({ advisorName: 'Camila Restrepo' }),
      ['quotations.quotation.read', 'quotations.order.read'],
      tenantModulesResponse({ orders: MODULE_OFF }, TENANT_ID),
    )

    renderRoute('/quotes/quote-1')

    expect(await screen.findByText('Asesor comercial: Camila Restrepo')).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(
      vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/quote-1/order')),
    ).toHaveLength(0)
  })
})
```

En `module-gates.test.tsx`, el caso de la conversión:

```tsx
describe('/quotes/$quoteId/order', () => {
  it('explains the plan with quotations on and orders off, without loading the quote', async () => {
    const backend = stubBackend(tenantModulesResponse({ orders: MODULE_OFF }, TENANT))

    renderRoute('/quotes/quote-1/order')

    expect(await screen.findByText(MODULE_NOT_IN_PLAN_MESSAGE)).toBeInTheDocument()
    expect(backend.requested().filter((url) => url.includes('/quotations/quote-1'))).toEqual([])
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/quotes/hooks/use-quote-order.test.tsx "src/routes/_authenticated/quotes/`$quoteId/index.test.tsx" src/routes/_authenticated/module-gates.test.tsx
```

Esperado: fallan los tres casos nuevos del hook (sale el request a `.../order`), el del detalle (hay un `GET .../quote-1/order`) y el de la conversión (se monta el asistente y pide la cotización).

- [ ] **Step 3: `useQuoteOrder` mira módulo y permiso**

`src/features/quotes/hooks/use-quote-order.ts` pasa a:

```ts
import { useQuery } from '@tanstack/react-query'

import { usePermissions } from '@/features/auth/hooks/use-permission'
import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'
import { useQuotesTenant } from '@/features/quotes/hooks/use-quotes-tenant'
import { fetchOrder } from '@/features/quotes/services/orders.api'
import { ApiError } from '@/lib/api-client'
import { ORDER_READ_PERMISSION, type Order } from '@/features/quotes/types/order'

export function orderQueryKey(tenantId: string, quotationId: string) {
  return ['quotes', tenantId, 'order', quotationId] as const
}

export interface UseQuoteOrderResult {
  /** `undefined` mientras carga, `null` cuando la cotización todavía no se convirtió
   * (404 tratado como estado, no como error) o cuando quien mira no puede ver pedidos. */
  order: Order | null | undefined
  isLoading: boolean
  isError: boolean
}

/** `GET .../order` devuelve 404 cuando la cotización no se convirtió todavía — no es un
 * fallo de la pantalla, es el estado "sin pedido". */
export function useQuoteOrder(quotationId: string | undefined): UseQuoteOrderResult {
  const { tenantId } = useQuotesTenant()
  // The order belongs to the orders module but is read from quotation screens (detail, editor,
  // conversion). With the module off or without the permission there is nothing to show: "no
  // order", like the 404, instead of a 403 left in isError (spec 2026-10-07).
  const { isEnabled, status: modulesStatus } = useTenantModules()
  const { can, status: permissionsStatus } = usePermissions()
  const resolving = modulesStatus === 'loading' || permissionsStatus === 'loading'
  const canReadOrders = isEnabled('orders') && can(ORDER_READ_PERMISSION)

  const query = useQuery({
    queryKey: orderQueryKey(tenantId ?? 'none', quotationId ?? 'none'),
    queryFn: async () => {
      try {
        return await fetchOrder(tenantId!, quotationId!)
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null
        throw error
      }
    },
    enabled: tenantId !== null && quotationId !== undefined && !resolving && canReadOrders,
    // (comentario existente sobre la caché, sin cambios)
    refetchOnMount: 'always',
    gcTime: 0,
    retry: false,
  })

  if (resolving) return { order: undefined, isLoading: true, isError: false }
  if (!canReadOrders) return { order: null, isLoading: false, isError: false }

  return {
    order: query.isFetching ? undefined : query.data,
    isLoading: query.isFetching || tenantId === null,
    isError: query.isError,
  }
}
```

(Conservar textual el comentario existente sobre `refetchOnMount`/`gcTime` en su lugar. Si `ORDER_READ_PERMISSION` y `Order` no salen del mismo módulo, separar los imports.)

- [ ] **Step 4: Partir la ruta de conversión**

En `src/routes/_authenticated/quotes/$quoteId/order.tsx`, agregar `import { ModuleGate } from '@/components/module-gate'` y cambiar la declaración de la ruta y el nombre del componente actual:

```tsx
export const Route = createFileRoute('/_authenticated/quotes/$quoteId/order')({
  component: RouteComponent,
})

// Lives under the quotes layout (['quotations']), which is not enough: this route converts and
// reads the order. The gate is the outermost element so the container's hooks (useQuote,
// useConvertQuoteToOrder, useOrderDetail, useQuoteOrder) do not mount with orders off.
function RouteComponent() {
  return (
    <ModuleGate modules={['orders']}>
      <ConvertQuoteToOrderContainer />
    </ModuleGate>
  )
}

function ConvertQuoteToOrderContainer() {
  // (el cuerpo actual de RouteComponent, sin cambios)
```

- [ ] **Step 5: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/quotes "src/routes/_authenticated/quotes" src/routes/_authenticated/module-gates.test.tsx
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/features/quotes/hooks/use-quote-order.ts src/features/quotes/hooks/use-quote-order.test.tsx "src/routes/_authenticated/quotes/`$quoteId/order.tsx" "src/routes/_authenticated/quotes/`$quoteId/index.test.tsx" src/routes/_authenticated/module-gates.test.tsx; git commit -m "feat(quotes): sin el módulo de pedidos, la cotización no consulta su pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: todo `src/features/quotes` y las rutas de cotizaciones en verde (las dos pruebas existentes del hook siguen pasando con los mocks en «todo concedido»). El backtick escapa el `$` de `$quoteId` en PowerShell.

---

### Task F7: El historial de la ficha del cliente, una tarjeta por módulo

**Files:**
- Create: `src/features/customers/components/quotes-history-card.tsx`
- Create: `src/features/customers/components/orders-history-card.tsx`
- Modify: `src/features/customers/components/customer-history-sections.tsx`
- Modify: `src/features/customers/components/customer-history-sections.test.tsx`

**Interfaces:**
- Consumes: `useTenantModules` (F2).
- Produces: `QuotesHistoryCard({ customerId })`, `OrdersHistoryCard({ customerId })`, `HISTORY_PAGE_SIZE` exportado desde `quotes-history-card.tsx` y reusado por la otra; `CustomerHistorySections({ customerId })` con la misma API.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `customer-history-sections.test.tsx`, junto a los otros `vi.mock` (agregar `beforeEach` al import de vitest):

```tsx
const modules = vi.hoisted(() => ({ quotations: true, orders: true }))

vi.mock('@/features/auth/hooks/use-tenant-modules', () => ({
  useTenantModules: () => ({
    status: 'ready',
    isEnabled: (key: 'quotations' | 'orders') => modules[key] ?? true,
    item: () => undefined,
  }),
}))
```

Dentro del `describe`, primero:

```tsx
  beforeEach(() => {
    modules.quotations = true
    modules.orders = true
  })
```

y dos casos nuevos:

```tsx
  // Spec 2026-10-07: the customer card belongs to customers, which has no dependencies. Each
  // history card owns its query and mounts only with its module on.
  it('shows only the quotes history and never asks for orders with orders off', async () => {
    modules.orders = false
    stubApi({ items: [] }, { items: [] })

    renderSections()

    expect(await screen.findByText('Historial de cotizaciones')).toBeInTheDocument()
    expect(screen.queryByText('Historial de pedidos')).not.toBeInTheDocument()
    expect(
      vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/orders')),
    ).toHaveLength(0)
  })

  it('renders nothing and asks nothing with neither module', async () => {
    modules.quotations = false
    modules.orders = false
    stubApi({ items: [] }, { items: [] })

    renderSections()

    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.queryByText('Historial de cotizaciones')).not.toBeInTheDocument()
    expect(screen.queryByText('Historial de pedidos')).not.toBeInTheDocument()
    expect(
      vi.mocked(fetch).mock.calls.filter(([input]) => {
        const url = String(input)
        return url.includes('/quotations') || url.includes('/orders')
      }),
    ).toHaveLength(0)
  })
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/customers/components/customer-history-sections.test.tsx
```

Esperado: fallan los dos nuevos (las dos tarjetas se montan siempre y sale `GET /orders`).

- [ ] **Step 3: Partir el componente**

`src/features/customers/components/quotes-history-card.tsx` (el JSX es el de la primera tarjeta de hoy, `customer-history-sections.tsx:55-123`, con las variables renombradas; el comentario de `HISTORY_PAGE_SIZE` de `:17-21` se mueve acá tal cual):

```tsx
import { useState } from 'react'

import { Link } from '@tanstack/react-router'

import { PageNumberControls } from '@/components/page-number-controls'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { QuoteStatusBadge } from '@/features/quotes/components/quote-status-badge'
import { useQuoteList } from '@/features/quotes/hooks/use-quote-list'
import { formatCurrency } from '@/lib/format-currency'

export const HISTORY_PAGE_SIZE = 5

export function QuotesHistoryCard({ customerId }: { customerId: string }) {
  const [page, setPage] = useState(1)
  const { quotes, total, isLoading, isError } = useQuoteList({
    clientId: customerId,
    page,
    pageSize: HISTORY_PAGE_SIZE,
  })
  const totalPages = Math.ceil(total / HISTORY_PAGE_SIZE)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Historial de cotizaciones</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <p className="text-sm text-muted-foreground">Cargando cotizaciones...</p>
        ) : isError ? (
          <p role="alert" className="text-sm text-destructive">
            No se pudo cargar el historial de cotizaciones.
          </p>
        ) : !quotes || quotes.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Este cliente todavía no tiene cotizaciones.
          </p>
        ) : (
          <ul className="divide-y divide-border">
            {quotes.map((quote) => (
              <li
                key={quote.id}
                className="flex flex-col gap-1 py-2 text-sm sm:flex-row sm:items-center sm:justify-between"
              >
                <Link
                  to="/quotes/$quoteId"
                  params={{ quoteId: quote.id }}
                  className="flex flex-col hover:text-primary hover:underline"
                >
                  <span className="font-medium text-foreground">{quote.quotationNumber}</span>
                  <span className="text-xs text-muted-foreground">
                    {quote.createdAt.slice(0, 10)}
                  </span>
                </Link>
                <div className="flex flex-wrap items-center gap-2">
                  <span className="tabular-nums text-foreground">
                    {formatCurrency(quote.total, quote.currency)}
                  </span>
                  <QuoteStatusBadge status={quote.status} />
                  {/* (keep the existing comment of :96-98 here) */}
                  {quote.orderId ? (
                    <Link
                      to="/orders/$orderId"
                      params={{ orderId: quote.orderId }}
                      className="text-xs text-primary hover:underline"
                    >
                      Ver pedido
                    </Link>
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
        )}
        {totalPages > 1 ? (
          <div className="mt-3 border-t border-border pt-3">
            <PageNumberControls page={page} totalPages={totalPages} onPageChange={setPage} />
          </div>
        ) : null}
      </CardContent>
    </Card>
  )
}
```

`src/features/customers/components/orders-history-card.tsx` (la segunda tarjeta, `:125-188`):

```tsx
import { useState } from 'react'

import { Link } from '@tanstack/react-router'

import { PageNumberControls } from '@/components/page-number-controls'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { HISTORY_PAGE_SIZE } from '@/features/customers/components/quotes-history-card'
import { OrderStatusBadge } from '@/features/orders/components/order-status-badge'
import { useOrderList } from '@/features/orders/hooks/use-order-list'
import { formatCurrency } from '@/lib/format-currency'

export function OrdersHistoryCard({ customerId }: { customerId: string }) {
  const [page, setPage] = useState(1)
  const { orders, total, isLoading, isError } = useOrderList({
    clientId: customerId,
    page,
    pageSize: HISTORY_PAGE_SIZE,
  })
  const totalPages = Math.ceil(total / HISTORY_PAGE_SIZE)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Historial de pedidos</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <p className="text-sm text-muted-foreground">Cargando pedidos...</p>
        ) : isError ? (
          <p role="alert" className="text-sm text-destructive">
            No se pudo cargar el historial de pedidos.
          </p>
        ) : !orders || orders.length === 0 ? (
          <p className="text-sm text-muted-foreground">Este cliente todavía no tiene pedidos.</p>
        ) : (
          <ul className="divide-y divide-border">
            {orders.map((order) => (
              <li
                key={order.id}
                className="flex flex-col gap-1 py-2 text-sm sm:flex-row sm:items-center sm:justify-between"
              >
                <Link
                  to="/orders/$orderId"
                  params={{ orderId: order.id }}
                  className="flex flex-col hover:text-primary hover:underline"
                >
                  <span className="font-medium text-foreground">{order.orderNumber}</span>
                  <span className="text-xs text-muted-foreground">
                    {order.convertedAt.slice(0, 10)}
                  </span>
                </Link>
                <div className="flex flex-wrap items-center gap-2">
                  <span className="tabular-nums text-foreground">
                    {formatCurrency(order.total, order.currency)}
                  </span>
                  <OrderStatusBadge status={order.status} />
                  {/* (keep the existing comment of :164-165 here) */}
                  <Link
                    to="/quotes/$quoteId"
                    params={{ quoteId: order.quotationId }}
                    className="text-xs text-primary hover:underline"
                  >
                    Cotización {order.quotationNumber}
                  </Link>
                </div>
              </li>
            ))}
          </ul>
        )}
        {totalPages > 1 ? (
          <div className="mt-3 border-t border-border pt-3">
            <PageNumberControls page={page} totalPages={totalPages} onPageChange={setPage} />
          </div>
        ) : null}
      </CardContent>
    </Card>
  )
}
```

Nota: el link «Ver pedido» de la tarjeta de cotizaciones sólo aparece con `orderId`, que el backend ya manda en `null` sin `orders` (B6): no hace falta otra condición.

`customer-history-sections.tsx` queda:

```tsx
import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'
import { OrdersHistoryCard } from '@/features/customers/components/orders-history-card'
import { QuotesHistoryCard } from '@/features/customers/components/quotes-history-card'

interface CustomerHistorySectionsProps {
  customerId: string
}

/**
 * The customer card belongs to customers, which has no dependencies, but its history reads
 * quotations and orders (spec 2026-10-07). Each card owns its query and mounts only with its
 * module on; with neither, nothing is drawn. While the modules load nothing mounts either, so no
 * query fires against a module that may be off.
 */
export function CustomerHistorySections({ customerId }: CustomerHistorySectionsProps) {
  const { isEnabled } = useTenantModules()
  const showQuotes = isEnabled('quotations')
  const showOrders = isEnabled('orders')
  if (!showQuotes && !showOrders) return null

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      {showQuotes ? <QuotesHistoryCard customerId={customerId} /> : null}
      {showOrders ? <OrdersHistoryCard customerId={customerId} /> : null}
    </div>
  )
}
```

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/customers "src/routes/_authenticated/customers"
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/features/customers/components/quotes-history-card.tsx src/features/customers/components/orders-history-card.tsx src/features/customers/components/customer-history-sections.tsx src/features/customers/components/customer-history-sections.test.tsx; git commit -m "feat(customers): el historial de la ficha monta cada tarjeta con su módulo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: verde, incluida la paginación independiente de las dos tarjetas (prueba existente).

---

### Task F8: Configuración del Excel de pedidos

**Files:**
- Modify: `src/routes/_authenticated/settings/orders-export-columns.tsx`
- Modify: `src/routes/_authenticated/settings/index.tsx`
- Modify: `src/features/tenant-settings/pages/tenant-settings-page.tsx:17-49,130-165`
- Modify: `src/features/tenant-settings/services/orders-export-layout.api.ts:168-174`
- Modify: `src/features/tenant-settings/services/orders-export-layout.api.test.ts`
- Modify: `src/routes/_authenticated/settings/index.test.tsx`
- Test: `src/routes/_authenticated/module-gates.test.tsx`

**Interfaces:**
- Consumes: `ModuleGate`, `useTenantModules`, `MODULE_NOT_IN_PLAN_MESSAGE` (F1–F3).
- Produces: `TenantSettingsPage` gana `showOrdersExport?: boolean` (default `true`).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `orders-export-layout.api.test.ts`:

```ts
  // Spec 2026-10-07: the 403 of a module that is off is not a permission problem.
  it('explains the plan on tenancy.module_not_enabled', () => {
    const failure = describeOrdersExportLayoutFailure(
      new ApiError(403, { code: 'tenancy.module_not_enabled' }),
    )

    expect(failure.message).toBe('Este módulo no está incluido en el plan de tu empresa.')
    expect(failure.shouldReload).toBe(false)
  })
```

En `settings/index.test.tsx`, `stubBackend` gana un parámetro y la ruta a `/modules`:

```tsx
function stubBackend(modules: unknown = {}) {
```

con, antes del catch-all:

```tsx
    if (url.endsWith('/modules')) return Promise.resolve(json(200, modules))
```

y tres casos (agregar `import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'`):

```tsx
  it('hides the orders export section without the orders module', async () => {
    stubBackend(tenantModulesResponse({ orders: MODULE_OFF }, TENANT))

    renderRoute('/settings')

    expect(await screen.findByRole('heading', { name: 'Configuración' })).toBeInTheDocument()
    await waitFor(() =>
      expect(
        screen.queryByRole('link', { name: 'Columnas del Excel de pedidos' }),
      ).not.toBeInTheDocument(),
    )
  })

  it('shows the orders export section with the orders module', async () => {
    stubBackend(tenantModulesResponse({}, TENANT))

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', { name: 'Columnas del Excel de pedidos' }),
    ).toBeInTheDocument()
  })

  it('shows the orders export section while the modules are still loading', async () => {
    vi.mocked(fetch).mockImplementation((input) => {
      const url = String(input)
      if (url.includes('/auth/me')) return Promise.resolve(json(200, session))
      if (url.includes('/authorization/me'))
        return Promise.resolve(
          json(200, {
            tenantId: TENANT,
            userId: 'u-1',
            permissions: ['tenancy.settings.read', 'tenancy.settings.update'],
          }),
        )
      if (url.endsWith('/settings')) return Promise.resolve(json(200, settings))
      if (url.endsWith('/modules')) return new Promise<Response>(() => {})
      return Promise.resolve(json(200, {}))
    })

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', { name: 'Columnas del Excel de pedidos' }),
    ).toBeInTheDocument()
  })
```

(El caso «con error» ya lo cubre la prueba existente `links to the orders export columns page`: su catch-all responde `{}`, el hook queda en `'error'` y la sección se muestra.)

En `module-gates.test.tsx`:

```tsx
describe('/settings/orders-export-columns', () => {
  it('explains the plan and never reads the layout with orders off', async () => {
    const backend = stubBackend(tenantModulesResponse({ orders: MODULE_OFF }, TENANT))

    renderRoute('/settings/orders-export-columns')

    expect(await screen.findByText(MODULE_NOT_IN_PLAN_MESSAGE)).toBeInTheDocument()
    expect(backend.requested().filter((url) => url.endsWith('/orders-export-layout'))).toEqual([])
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/tenant-settings/services/orders-export-layout.api.test.ts src/routes/_authenticated/settings/index.test.tsx src/routes/_authenticated/module-gates.test.tsx
```

Esperado: fallan `explains the plan on tenancy.module_not_enabled` (mensaje de permiso), `hides the orders export section without the orders module` (timeout del `waitFor`) y el de `/settings/orders-export-columns` (se lee el layout).

- [ ] **Step 3: Implementar**

`orders-export-layout.api.ts`, el `case 403:` pasa a (agregar `import { MODULE_NOT_IN_PLAN_MESSAGE } from '@/features/auth/types/tenant-modules'`):

```ts
    case 403:
      // A module that is off answers 403 too, with its own code (spec 2026-10-07): that is the
      // plan, not a missing permission.
      return {
        columns: {},
        message:
          error.code === 'tenancy.module_not_enabled'
            ? MODULE_NOT_IN_PLAN_MESSAGE
            : 'No tienes permiso para editar esta configuración.',
        shouldReload: false,
      }
```

`settings/orders-export-columns.tsx`: el `component` de la ruta pasa a ser el gate y el container actual queda adentro, con su nombre actual:

```tsx
import { ModuleGate } from '@/components/module-gate'

export const Route = createFileRoute('/_authenticated/settings/orders-export-columns')({
  component: OrdersExportColumnsGate,
})

// The permission here is core (tenancy.settings.*) but what it configures belongs to orders, so
// the masked permissions do not cover it (spec 2026-10-07). The gate goes outside the container so
// useOrdersExportLayout does not fire with orders off.
function OrdersExportColumnsGate() {
  return (
    <ModuleGate modules={['orders']}>
      <OrdersExportColumnsRoute />
    </ModuleGate>
  )
}
```

`settings/index.tsx`, en `SettingsRoute` (agregar `import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'`):

```tsx
  const { isEnabled, status: modulesStatus } = useTenantModules()
  // Shown while the modules load or fail, like ModuleGate: the backend enforces anyway.
  const showOrdersExport = modulesStatus === 'loading' || isEnabled('orders')
```

y pasar `showOrdersExport={showOrdersExport}` a `<TenantSettingsPage>`.

`tenant-settings-page.tsx`: en `TenantSettingsPageProps`, después de `logo`:

```ts
  /** False when the tenant does not have the orders module: the orders export section is hidden.
   * The container decides; while the modules load or fail it stays visible. */
  showOrdersExport?: boolean
```

destructurar `showOrdersExport = true` y envolver el `<SettingsSection title="Excel de pedidos" …>…</SettingsSection>` completo (`:133-165`) en `{showOrdersExport ? ( … ) : null}`, con el comentario existente adentro.

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/tenant-settings src/routes/_authenticated/settings src/routes/_authenticated/module-gates.test.tsx
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/routes/_authenticated/settings/orders-export-columns.tsx src/routes/_authenticated/settings/index.tsx src/routes/_authenticated/settings/index.test.tsx src/features/tenant-settings/pages/tenant-settings-page.tsx src/features/tenant-settings/services/orders-export-layout.api.ts src/features/tenant-settings/services/orders-export-layout.api.test.ts src/routes/_authenticated/module-gates.test.tsx; git commit -m "feat(tenant-settings): el Excel de pedidos exige el módulo orders"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: verde, incluido `settings/orders-export-columns.test.tsx` sin tocar (catch-all `{}`).

---

### Task F9: Landing al primer módulo disponible

**Files:**
- Modify: `src/components/app-shell/sidebar-nav-items.ts:20`
- Modify: `src/features/auth/services/landing.ts:12-26`
- Modify: `src/features/auth/services/landing.test.ts`

**Interfaces:**
- Consumes: `visibleSidebarItems`, `SidebarRoute`.
- Produces: `export type SidebarRoute`; `type Landing = SidebarRoute`; `landingFor(can): Landing`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `landing.test.ts`:

```ts
  // Spec 2026-10-07: a tenant without quotations or orders lands on the first sidebar item it can
  // see, in sidebar order (Clientes, Empresas, Productos).
  it('lands on the first sidebar item without quotations or orders', () => {
    expect(
      landingFor(
        granting('catalog.product.read', 'companies.company.read', 'customers.customer.read'),
      ),
    ).toBe('/customers')
    expect(landingFor(granting('catalog.product.read', 'companies.company.read'))).toBe(
      '/companies',
    )
    expect(landingFor(granting('catalog.product.read'))).toBe('/catalog')
  })

  // Review Focus 5: the reports item lands on the report the person can open, not on /reports/orders.
  it('follows the alternate destination of a reports item', () => {
    expect(landingFor(granting('reporting.customer.read'))).toBe('/reports/customers')
  })

  it('keeps /quotes when nothing in the sidebar is granted', () => {
    expect(landingFor(granting())).toBe('/quotes')
  })
```

El caso existente `keeps /quotes when neither is granted, where the missing access is explained` (`:23-25`) concede `customers.customer.read` y hoy espera `/quotes`: con el spec pasa a esperar `/customers`. Cambiar su expectativa y su nombre:

```ts
  it('lands on customers when that is all it can read', () => {
    expect(landingFor(granting('customers.customer.read'))).toBe('/customers')
  })
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/auth/services/landing.test.ts
```

Esperado: fallan los tres casos nuevos y el reescrito (todos devuelven `/quotes`).

- [ ] **Step 3: Implementar**

`sidebar-nav-items.ts:20`: `type SidebarRoute =` pasa a `export type SidebarRoute =`.

`landing.ts` (agregar `import { visibleSidebarItems, type SidebarRoute } from '@/components/app-shell/sidebar-nav-items'`):

```ts
export type Landing = SidebarRoute

/**
 * Where a signed-in member starts. /quotes since 2026-09-12, when the product owner retired the
 * Resumen page — except for whoever reads orders but not quotations (Facturación since
 * 2026-10-01), who would otherwise open on a "no permission" card.
 *
 * Since 2026-10-07 a tenant can lack quotations and orders altogether (modules by tenant): then the
 * first sidebar item the person can see, in sidebar order, with its alternate destination applied.
 * With nothing at all, /quotes, which explains the plan.
 *
 * Decided by permission, not by role key: custom roles exist, and the masked permissions already
 * reflect the tenant's modules.
 */
export function landingFor(can: (permission: string) => boolean): Landing {
  if (can(QUOTE_READ_PERMISSION)) return '/quotes'
  if (can(ORDER_READ_PERMISSION)) return '/orders'
  const first = visibleSidebarItems(can, 'ready').find((item) => !('disabled' in item))
  return first && !('disabled' in first) ? first.to : '/quotes'
}
```

`resolveLanding` no cambia (devuelve `Landing`).

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/auth/services/landing.test.ts src/routes/index.test.tsx src/routes/login.test.tsx src/components/app-shell
bun run build
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/components/app-shell/sidebar-nav-items.ts src/features/auth/services/landing.ts src/features/auth/services/landing.test.ts; git commit -m "feat(auth): sin cotizaciones ni pedidos, aterrizar en el primer módulo del menú"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: verde (si `src/routes/login.test.tsx` no existe, quitarlo del comando); `bun run build` confirma que `navigate({ to: landing })` sigue tipando con `Landing = SidebarRoute`. Si `build` regenera `routeTree.gen.ts`, no debería: no hay rutas nuevas.

---

### Task F10: El editor de roles no cuenta permisos fuera del catálogo

**Files:**
- Modify: `src/features/roles/roles-page.tsx:182-188,232-263`
- Modify: `src/features/roles/roles-page.test.tsx`

**Interfaces:**
- Consumes: el catálogo ya filtrado por el backend (B7) que llega como `permissions: PermissionCatalogItem[]`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `roles-page.test.tsx`:

```tsx
// Spec 2026-10-07: /authorization/catalog leaves out the permissions of modules that are off,
// while /authorization/roles keeps what the role stores. The page shows and counts only what the
// catalog offers; the role keeps the rest and grants it again if the module comes back.
describe('RolesPage — permissions outside the catalog', () => {
  const ROLE_WITH_HIDDEN: TenantRole = {
    ...SYSTEM_ROLE,
    permissions: ['advisorship.manage', 'catalog.product.read', 'quotations.quotation.read'],
  }

  it('neither lists nor counts a permission the catalog does not offer', () => {
    renderPage({ roles: [ROLE_WITH_HIDDEN] })

    expect(screen.getByText(/^2 permisos/)).toBeInTheDocument()
    expect(screen.queryByText(/^3 permisos/)).not.toBeInTheDocument()
    expect(screen.queryByText('quotations.quotation.read')).not.toBeInTheDocument()
    expect(screen.getByText('Ver productos')).toBeInTheDocument()
  })

  it('keeps today behavior while the catalog is not loaded', () => {
    renderPage({ roles: [ROLE_WITH_HIDDEN], permissions: [], permissionsUnavailable: true })

    expect(screen.getByText(/^3 permisos/)).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location $F
bun run test --run src/features/roles/roles-page.test.tsx
```

Esperado: falla el primero (`Unable to find an element with the text: /^2 permisos/`, el contador dice 3, y `quotations.quotation.read` aparece en el detalle).

- [ ] **Step 3: Implementar**

En `roles-page.tsx`, una función junto a `labelOf`/`descriptionOf` (`:509-525`):

```tsx
/**
 * The role permissions the screen shows and counts. With the catalog loaded, only the ones it
 * offers: since 2026-10-07 the catalog leaves out modules that are off, and the role keeps those
 * permissions without granting them. Without the catalog, everything, as before.
 */
function shownPermissions(
  role: TenantRole,
  catalog: PermissionCatalogItem[],
  catalogLoaded: boolean,
): string[] {
  if (!catalogLoaded) return role.permissions
  return role.permissions.filter((permission) =>
    catalog.some((item) => item.permission === permission),
  )
}
```

En `RolesPage`, una vez, cerca de donde se lee `permissions`:

```tsx
  const catalogLoaded = !permissionsUnavailable && permissions.length > 0
```

El contador de la lista (`:182-184`) usa `shownPermissions(role, permissions, catalogLoaded).length` en las dos apariciones (guardarlo en una constante dentro del `map`). `RoleDetail` recibe `catalogLoaded` como prop nueva y su `role.permissions.map(...)` (`:253`) pasa a `shownPermissions(role, permissions, catalogLoaded).map(...)`; pasarla en el uso de `:205`. `RoleEditor` no cambia: ya pinta sólo checkboxes del catálogo e inicializa lo marcado desde `role.permissions`, así que guarda lo oculto sin que nadie lo vea.

- [ ] **Step 4: Ver el GREEN y commitear**

```powershell
Set-Location $F
bun run test --run src/features/roles
if ((git branch --show-current) -ne "feature/modulos-por-tenant") { throw "ABORT" }; git add src/features/roles/roles-page.tsx src/features/roles/roles-page.test.tsx; git commit -m "feat(roles): no mostrar ni contar permisos fuera del catálogo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F11: Suite completa del frontend contra la baseline

**Files:** ninguno (si algo falla, se corrige en la tarea dueña con su ciclo y su commit).

- [ ] **Step 1: Suite, lint y build**

```powershell
Set-Location $F
$report = Join-Path $env:TEMP "qep-front-final.json"
bun run test --reporter=json --outputFile=$report
bun run lint
bun run format:check
bun run build
git status --short
```

Esperado: lint sin errores nuevos, `format:check` en verde para los archivos de la rama (si el baseline ya traía archivos fuera de formato, comparar por nombre), build en verde y `git status` limpio (el build no deja `routeTree.gen.ts` modificado: si lo deja, se commitea con la tarea de rutas que lo cambió).

- [ ] **Step 2: Comparar fallas por nombre**

```powershell
$final = (Get-Content -Raw $report | ConvertFrom-Json).testResults |
    ForEach-Object { $_.assertionResults } |
    Where-Object { $_.status -eq 'failed' } |
    ForEach-Object { $_.fullName } |
    Sort-Object -Unique
$baseline = Get-Content (Join-Path $env:TEMP "qep-front-baseline-failed.txt")
"Nuevas fallas:"; $final | Where-Object { $_ -notin $baseline }
"Arregladas de pasada:"; $baseline | Where-Object { $_ -notin $final }
```

Esperado: «Nuevas fallas» vacío. El candidato más probable a romper es una prueba existente que montaba el detalle de una cotización y esperaba ver un `GET .../order` con permisos que no incluyen `quotations.order.read` (F6 ya no lo pide): se ajusta la prueba si su intención era otra, o se le concede el permiso. Anotar los dos bloques y el resumen literal de Vitest en el handoff.

- [ ] **Step 3: Handoff del frontend**

Sin push. Anotar `git log --oneline origin/develop..HEAD`, las salidas de F0 y F11, y el orden de despliegue: **backend primero**. Con el frontend nuevo contra un backend viejo, `/modules` responde 404, el hook queda en `'error'` y todos los gates dejan pasar: el despliegue en cualquier orden no rompe la SPA, pero los mensajes del plan sólo aparecen con el backend nuevo.

---

## Decisiones tomadas en este plan (para el owner)

1. Worktrees `…\qep-backend-worktrees\modulos-por-tenant` y `…\qep-frontend-worktrees\modulos-por-tenant`, ramas `feature/modulos-por-tenant` desde `origin/develop` en los dos repos.
2. El spec y el plan se copian a `qep-backend/docs/superpowers/` en el primer commit de la rama (B0).
3. `TenantModuleDefaults` es `public` para probarla sin `InternalsVisibleTo`; `Modules.Tenancy.UnitTests` gana la referencia a Tenancy.Infrastructure y su lock se regenera (B2).
4. La prueba del `CHECK` (`23514`) va en `TenantModulesMigrationTests`, no en `TenantModulesApiTests` (B3).
5. Alias de `using` (`ModuleKeys`, `TenantModuleKey`) o nombres calificados donde `Modules.Tenancy.Domain` convive con otro dominio, para no abrir ambigüedades (B5, B9, B10).
6. `EnableModuleAsync` de las pruebas escribe `source = 'manual'`, el origen que usa el SQL de «Operación» (B6).
7. El filtro de `excludedOwnerTypes` en SQL se arma con una condición por tipo y no con `Contains`, para no depender de cómo EF traduce una colección de enums convertidos a texto (B12).
8. `landingFor` también cambia el caso existente «sólo clientes»: el spec manda aterrizar en `/customers`, no en `/quotes` (F9).
9. Frontend: comentarios en inglés por la regla de `qep-frontend/CLAUDE.md`; commits sin ID de slice, con scope por área.
10. Las pruebas de gate de ruta se juntan en `src/routes/_authenticated/module-gates.test.tsx` en vez de repartirse por archivo: un solo stub para todas, y las 17 pruebas de ruta existentes quedan sin tocar.
