# Rename venta → pedido (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Renombrar el concepto "venta" a "pedido" en todo `qep-backend` —símbolos, esquema de base, contrato HTTP, permisos, exportación, correo y numeración— sin cambiar comportamiento: mismas reglas, mismos estados, mismos permisos efectivos, mismos datos.

**Architecture:** Seis commits que compilan y dejan la suite en verde, del más interno al más externo. Primero un refactor puro de símbolos C# (`Sale` → `Order`) que no toca nada serializado ni persistido. Después el esquema: una migración de `Quotations` escrita a mano con `RenameTable`/`RenameColumn`/`RenameIndex` y `RENAME CONSTRAINT`, junto con todo lo que nombra la base en texto. Recién ahí el contrato HTTP (rutas, JSON, códigos), los permisos (con una migración de datos de `Authorization`) y, al final, exportación, correo y el prefijo `PED-` (con una migración de datos de `export_jobs`). Los pedidos siguen dentro de `Modules.Quotations`.

**Tech Stack:** .NET 10, EF Core 10.0.11 + Npgsql 10.0.3, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`, Docker corriendo para las pruebas de integración).

**Spec:** `qep-frontend/docs/superpowers/specs/2026-09-14-rename-sales-to-orders-design.md`

## Global Constraints

- Rama **`feature/rename-sales-to-orders`** (creada desde `develop` en `b4bd22c`). **Nunca se commitea sobre `main`**: cada bloque de commit empieza con el guard de rama.
- Identificador en inglés **`Order`** (D1): `Order`, `OrderId`, tabla `orders`, ruta `/orders`. Regla: `Sale` → `Order` y `Sales` → `Orders` en nombres de tipo, archivo, miembro y variable de `src/` y `tests/`.
- Los números nuevos salen **`PED-{año}-{secuencia:D4}`** con el **mismo contador** (D2). Los `VEN-` existentes no se tocan.
- **Corte duro, sin alias** (D3): ninguna ruta, campo, código, permiso ni `kind` viejo sigue respondiendo.
- Se migra **lo que el código vuelve a leer** (D4): permisos de roles custom, `export_jobs.kind` y la clave `SaleNumber` de `export_jobs.filters`. **No** se reescribe lo histórico que sólo se muestra: acciones de auditoría viejas, texto de `quotation_history`, números `VEN-`.
- Notifications traduce sólo `"Orders"` → `("pedido", "pedidos")`; **sin alias `"Sales"`** (D5): un evento viejo en vuelo cae a "registros".
- Los pedidos **siguen dentro de `Modules.Quotations`** (D6). Los proyectos `Modules.Quotations.*` y `Modules.Reporting.*` no cambian de nombre, y `ArchitectureTests` no cambia de regla.
- **Nunca** se editan migraciones históricas ni sus `.Designer.cs`. El `QuotationsDbContextModelSnapshot.cs` sólo cambia regenerado por `dotnet ef migrations add`.
- Todo texto que ve una persona (correo, Excel, historial, catálogo de permisos) va en **español neutro con tuteo**, y "pedido" es **masculino** (D7): "el pedido", "aprobado", "convertido".
- Commits: conventional commits en español, **sin atribución de IA ni trailer `Co-Authored-By`**. Después de cada commit, `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, corriges con `git commit --amend` antes de seguir.
- `Api.exe` corriendo bloquea `build`/`test`/`ef`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Todo comando para el developer va en **PowerShell** (`curl.exe`, `$env:VAR = "…"`, `A; if ($?) { B }`). Git Bash es alternativa válida, pero se ofrece después.
- TDD: RED antes que GREEN, con la salida literal de los dos en el handoff.
- Formato: sólo `dotnet format Backend.slnx --verify-no-changes --include <archivos>`, nunca mutante. Los archivos de migración generados no van en el `--include`.
- Paquetes: este plan no agrega ninguno. `dotnet restore --locked-mode` tiene que pasar en cada commit.
- Migraciones con el factory de diseño, nunca con `--startup-project`: `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- Regresión **por nombre de prueba** contra el baseline de Task 0. Una prueba renombrada se compara por su nombre nuevo, con la misma regla `Sales` → `Orders`, `Sale` → `Order`.
- `git add` con rutas explícitas (o `src tests` sólo en Task 1, después de comprobar que no hay nada fuera). Nunca `git add -A` ni `git add .`.

---

## Hallazgos contra el código (2026-09-14)

Verificados en `feature/rename-sales-to-orders` = `develop` = `origin/develop` = `b4bd22c`. No son supuestos.

1. **`develop` no se movió respecto del spec.** La rama está en `b4bd22c`, el mismo commit que `develop` y `origin/develop`. Todas las referencias `archivo:línea` del spec siguen valiendo: `SaleEndpoints.cs:20-21,23,31,40,47-48,50,61,68,80,102,130,199`, `ReportingEndpoints.cs:20,27`, `QuotationsUnitOfWork.cs:21`, `SaleNumberGenerator.cs:14,23`, `SaleNumberFormatter.cs:11`, `ExportLoadSeeder.cs:340-380` (`'VEN-'` en `:345`), `ops/export-load-cleanup.sql:28,32`, `ExportJobKind.cs:8`, `QuotationsDbContext.cs:464-467`, `QuotationsExportKindText.cs:16`, `SalesExportProcessor.cs:22,24,34`, `ExportStatusLabels.cs:31`, `ApproveSale.cs:50`, `AddSalePaymentProofs.cs:104`, `ExportJobRunner.cs:131`, `Program.cs:113` y `QepServiceCollectionExtensions.cs:544-549,596-600,617,761-775,1005-1016`. Si Task 0 encuentra `develop` adelante, se para y se re-verifican antes de seguir.
2. **No existe una prueba de `already_converted`, y convertir dos veces no la produce.** `SaleApiTests.ConvertingAnAlreadyConvertedQuotationIsRejected` (`SaleApiTests.cs:146-178`) espera `quotation.quotation.status_not_convertible`: la cotización ya quedó en `Converted` y `EnsureConvertibleToSale` la corta antes de llegar al índice. `QuotationsUnitOfWork` sólo traduce `IX_sales_quotation` a `already_converted` en dos casos (`QuotationsUnitOfWork.cs:15-20`): dos conversiones simultáneas, o una venta de antes de `Converted` cuya cotización siguió en `Sent`. Task 2 escribe la prueba sembrando por SQL un pedido para una cotización `Sent`, que reproduce el segundo caso de forma determinista. Así protege la constante del índice, que es lo que el spec pide.
3. **Símbolo y contrato no se pueden renombrar juntos en Task 1.** System.Text.Json serializa la propiedad del record en camelCase: renombrar `SaleResponse.SaleNumber` cambia `saleNumber` a `orderNumber` en el cable. Lo mismo con los parámetros de endpoint `saleId` (atado a `{saleId:guid}`) y `saleNumber` (query string), que enlazan por nombre. Task 1 renombra los tipos y deja esos miembros —la lista está en Task 1— para Task 3.
4. **El snapshot de EF y el orden de las tareas.** `MigrationsModelDiffer` empareja tablas primero por nombre y después por nombre de tipo de entidad. Task 1 cambia sólo los tipos CLR y deja las tablas, columnas, claves e índices intactos, así que el modelo relacional es el mismo y no hace falta migración. Lo prueba `HasPendingModelChanges()`, que Task 1 agrega como prueba. En Task 2 el snapshot todavía nombra `Modules.Quotations.Domain.Sale` sobre `sales` y el modelo tiene `Modules.Quotations.Domain.Order` sobre `orders`: no coincide ni el nombre ni el tipo, y EF genera `DropTable` + `CreateTable`, que borraría los datos. Por eso Task 2 genera la migración para quedarse con el `.Designer.cs` y el snapshot nuevos, y **reemplaza `Up`/`Down` a mano**. EF tampoco tiene operación para renombrar PK ni FK: van con `ALTER TABLE … RENAME CONSTRAINT`.
5. **La migración de `export_jobs` va en Task 5, no en la de Task 2** (el spec dice "la misma migración"). Tiene que viajar en el mismo commit que `ExportJobKind.Orders`: en un commit con `kind = 'Orders'` en la base y el enum todavía en `Sales`, EF no puede leer un job pendiente. Es una segunda migración de `Quotations`, sólo de datos.
6. **El template del log de la carga sintética está atado al parámetro.** `ExportLoadSeedWorker.cs:31` (`{Sales} sales`) usa `[LoggerMessage]`: el generador exige que cada placeholder tenga su parámetro. Renombrar `sales` sin el template rompe el build (`TreatWarningsAsErrors`), así que Task 1 cambia los dos juntos.
7. **`.atl/skill-registry.md` no tiene nada que cambiar** (D11 lo nombra). Su única coincidencia (`:31`) es la descripción de otra skill ("sales bot"), no el concepto.
8. **El regex de residuo del spec encuentra el verbo "salir".** Con `-i`, `Sale[A-Z]` y `\bsales?\b` coinciden con "sale"/"salen" en comentarios en español: unas 40 líneas hoy (`Program.cs:121`, `ApiExceptionHandler.cs:88`, `AuthPreferenceEndpoints.cs:10`, `CustomerIdentification.cs:18`, `QuotationsReportSource.cs:114`…). Task 6 da el comando y las excepciones.
9. **El repo no tenía cómo migrar hasta una migración puntual.** El host de pruebas migra todo a la última al arrancar (`QuotationsDatabaseInitializer.cs:14`). Las pruebas de migración arman el `DbContext` con la cadena del contenedor y usan `IMigrator.MigrateAsync(<id>)`. Las filas viejas de pedidos se siembran con `SET session_replication_role = replica`, que apaga la FK hacia `quotations.quotations`. Una cotización real en ese estado del esquema exige decenas de columnas obligatorias que no tienen que ver con lo que se prueba, y la FK se verifica igual, por nombre, en `pg_constraint`. El usuario `qep` del contenedor es superusuario (imagen oficial de Postgres), que es lo que ese `SET` pide.
10. **PostgreSQL 18 nombra los NOT NULL.** Viven en `pg_constraint` como `<tabla>_<columna>_not_null`, y `RENAME TABLE` no los renombra. EF no los modela y ninguna prueba los nombra, así que quedan como están. Las pruebas de constraints filtran `contype IN ('p', 'f')`.
11. **OpenAPI:** `app.MapOpenApi()` (`Program.cs:85`) publica el documento, pero ninguna prueba lo cubre. El tag `"Sales"` → `"Orders"` se verifica con `rg`, no con una prueba: ningún consumidor lo lee.
12. **Nombres de migración sin "Sales"**, para que el control de residuo no los encuentre: `RenameToOrders` y `MigrateExportJobsToOrders` (Quotations), `RenamePermissionsToOrders` (Authorization).
13. **`ReportingPermissions.cs:7`** documenta que `sales` "es la excepción y va en plural". Pasa a `orders`, que sigue siendo la excepción.

## Entrega

| Commit | Tarea |
| --- | --- |
| `docs(orders): plan del rename de venta a pedido en el backend` | 0 |
| `refactor(quotations): renombrar símbolos de venta a pedido` | 1 |
| `refactor(quotations): renombrar tablas, columnas e índices de ventas a pedidos` | 2 |
| `feat(quotations)!: contrato HTTP de pedidos en rutas, campos y códigos` | 3 |
| `feat(authorization)!: permisos de pedidos y migración de roles custom` | 4 |
| `feat(quotations): exportación, correo y numeración PED- de pedidos` | 5 |
| `docs(orders): documentación viva de pedidos` | 6 |

**Despliegue:** backend primero (migraciones + endpoints), frontend inmediatamente después (spec, «Despliegue»). Ningún commit intermedio se publica: la rama sale entera.

---

## File Structure

**Renombrar con `git mv` (Task 1)** — 30 de producción y 12 de prueba. La lista exacta está en Task 1, Steps 2 y 5.

**Crear**

| Archivo | Tarea | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_RenameToOrders.cs` (+ `.Designer.cs`) | 2 | Renombra tablas, columnas, índices, PK y FK |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_MigrateExportJobsToOrders.cs` (+ `.Designer.cs`) | 5 | `kind` y la clave `SaleNumber` de los jobs |
| `src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/Migrations/<ts>_RenamePermissionsToOrders.cs` (+ `.Designer.cs`) | 4 | `array_replace` de los tres permisos |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs` | 2, 5 | Las dos migraciones de Quotations sobre filas del esquema viejo |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderContractApiTests.cs` | 3 | El contrato tal como viaja: rutas, JSON crudo, `Location` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationPermissionsMigrationTests.cs` | 4 | Rol custom con los códigos viejos antes y después de migrar |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs` | 5 | Prefijo `PED-` |

**Modificar (resumen; el detalle con líneas está en cada tarea)**

| Área | Tareas |
| --- | --- |
| `QuotationsDbContext.cs`, snapshot (regenerado), `QuotationsUnitOfWork.cs`, `OrderNumberGenerator.cs`, `ExportLoadSeeder.cs`, `ops/export-load-cleanup.sql` | 1, 2 |
| `OrderEndpoints.cs`, `OrdersDtos.cs`, `QuotationsDtos.cs`, `ReportingEndpoints.cs`, `ReportingDtos.cs`, `ReportSummaries.cs` y los archivos que tiran los 17 códigos | 1, 3 |
| `OrdersPermissions.cs`, `ReportingPermissions.cs`, `QepServiceCollectionExtensions.cs` y cada uso de las constantes | 1, 4 |
| `ExportJobKind.cs`, `ExportOrders.cs`, `OrdersExportProcessor.cs`, `ExportJobRunner.cs`, `ApproveOrder.cs`, `AddOrderPaymentProofs.cs`, `ExportStatusLabels.cs`, `OrderNumberFormatter.cs`, `QuotationChangeSummary.cs`, `QuotationsExportKindText.cs` | 1, 5 |
| `README.md`, `CLAUDE.md`, `docs/integracion-cotizaciones-y-ventas.md` → `docs/integracion-cotizaciones-y-pedidos.md` | 6 |

**No se tocan, a propósito:** las migraciones históricas y sus `.Designer.cs` (`20260824210345_AddSalesAndPaymentProofs`, `20260906191344_AddSaleApproval`, `20260912234651_AddExportJobs` y el resto), los specs y planes de `docs/superpowers/` (D11), `.atl/skill-registry.md` (hallazgo 7), "ventas-junior"/"Ventas junior"/"Ventas senior" en Authorization y Tenancy (rol de ejemplo, spec «Copy», regla 3), y los códigos `quotation.quotation.*`, que no llevan "sale".

---

### Task 0: Rama, herramientas y baseline

**Files:**
- Create: `docs/superpowers/plans/2026-09-14-rename-sales-to-orders-backend.md` (este plan; se commitea en el Step 6)

**Interfaces:**
- Consumes: nada.
- Produces: la rama comprobada en `b4bd22c`; `$env:TEMP\qep-rename-orders-baseline-failed.txt` con las pruebas que ya fallan, por nombre; la salida literal del build y de las unitarias como evidencia.

- [ ] **Step 1: Comprobar rama, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git status --short
git fetch origin
git rev-list --left-right --count origin/develop...HEAD
git log --oneline -1
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado:
- la rama es `feature/rename-sales-to-orders`;
- `git status` muestra a lo sumo este plan sin trackear (`?? docs/superpowers/plans/2026-09-14-rename-sales-to-orders-backend.md`);
- el `rev-list` da `0	0` y el log, `b4bd22c Merge branch 'feature/nombre-asesor-listado-cotizaciones' into develop`;
- `Get-Process` no devuelve nada;
- `docker info` devuelve una versión;
- `dotnet ef` devuelve una `10.0.x`.

Si la rama es `main` u otra, **para y pregunta**. Si el primer número del `rev-list` es mayor que 0, `develop` avanzó: con la rama sin commits propios, `git merge --ff-only origin/develop` y re-verificas las líneas del hallazgo 1 antes de seguir. Con commits propios, **para y pregunta**.

- [ ] **Step 2: Restore y build**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`. Pega las últimas cuatro líneas en el handoff.

- [ ] **Step 3: El modelo coincide con el snapshot en los dos contextos que se van a migrar**

```powershell
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Authorization/Modules.Authorization.Infrastructure --context AuthorizationDbContext
```

Esperado, en los dos: `No changes have been made to the model since the last migration.` Si alguno dice `Changes have been made to the model since the last migration`, hay deriva previa y todo el razonamiento del hallazgo 4 deja de valer: **para y pregunta**.

- [ ] **Step 4: Unitarias y arquitectura (sin Docker)**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build
dotnet test tests/Modules/Platform/Modules.Platform.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: `Correctas!` / `Passed!` en cada proyecto, o las fallas conocidas del baseline. Pega el resumen de cada uno (`Con error: 0, Superado: N…`).

- [ ] **Step 5: Baseline de la suite completa, por nombre**

Con Docker corriendo. Tarda decenas de minutos (Testcontainers levanta un Postgres por prueba); corre en primer plano, no en background.

```powershell
$baseline = Join-Path $env:TEMP "qep-rename-orders-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $baseline
Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-rename-orders-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-rename-orders-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff. Es la referencia de Tasks 1–6.

- [ ] **Step 6: Commitear el plan**

```powershell
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git add docs/superpowers/plans/2026-09-14-rename-sales-to-orders-backend.md
git commit -m "docs(orders): plan del rename de venta a pedido en el backend"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit creado y el `Select-String` sin salida.

---

### Task 1: Símbolos internos (refactor puro)

Renombra tipos, archivos, miembros, `DbSet`, handlers, registros de DI y pruebas con la regla `Sale` → `Order`, `Sales` → `Orders`. **No cambia nada observable desde afuera**: ni rutas, ni JSON, ni códigos, ni permisos, ni base, ni `kind`, ni textos. Tampoco genera migración (hallazgo 4). Los archivos de `Migrations/` —históricos, sus `.Designer.cs` y el snapshot— **no se editan**. El snapshot sigue nombrando `Modules.Quotations.Domain.Sale` a propósito, hasta que Task 2 lo regenere.

**Files:**
- Rename: los 42 archivos de los Steps 2 y 5.
- Modify (producción, sin renombrar): `src/Api/Program.cs:113`; `src/Bootstrapper/QepServiceCollectionExtensions.cs:342-370,394,453-466,544-617,761-775,1003-1016`; `src/Bootstrapper/QuotationsReportSource.cs:10,38,351`; `src/Bootstrapper/CustomerReportSource.cs:13,326`; `src/Bootstrapper/PriceChangeReportSource.cs:11,220,312`; `src/Bootstrapper/QuotationFileLookup.cs:19`; `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:116-126,338-380,385-393`; `src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs:31-33,74-76`; `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs:411-412`; en `Modules.Quotations.Application`: `ExportBatchLoop.cs:5`, `ExportStatusLabels.cs:28-41`, `ListQuotations.cs:61-64,95,168-169`, `QuotationChangeSummary.cs:88-89`, `QuotationMapping.cs:41,61-85`, `QuotationResponseComposer.cs:89`, `QuotationsDtos.cs:76-82`; en `Modules.Quotations.Domain`: `Quotation.cs:10-16,531-588,662-686`, `QuotationBillingAccount.cs:18`, `QuotationParty.cs:61`, `QuotationPartyDetails.cs:115`, `QuotationStatus.cs:7-13`; en `Modules.Quotations.Infrastructure`: `Persistence/QuotationsDbContext.cs:19-23,39-41,335-445`, `Persistence/QuotationUserReferenceProbe.cs:48,51`, `Persistence/QuotationsUnitOfWork.cs:15-17` (sólo el comentario), `QuotationsInfrastructureExtensions.cs:48-49`; en Reporting: `Modules.Reporting.Api/ReportingEndpoints.cs:72-114,138,180,225`, `Modules.Reporting.Application/{CustomerReportSummary,ListQuotationsReport,PriceChangeReportSummary,QuotationsReportSummary,ReportSources,ReportSummaries,ReportingDtos,ReportingFilters}.cs`, `Modules.Reporting.Domain/ReportFilterEnums.cs`.
- Modify (pruebas, sin renombrar): `tests/ArchitectureTests/ArchitectureTests/ReportingLayerTests.cs:16,25,47,82`; en `Modules.Quotations.UnitTests`: `ExportJobRunnerTests.cs:54`, `ExportQuotationsHandlerTests.cs`, `ExportStatusLabelsTests.cs:27-48`, `ListQuotationsHandlerTests.cs:124-195,245-283`, `QuotationTests.cs`, `QuotationsDbContextMappingTests.cs:75-91`, `QuotationsTestDoubles.cs:521-640`; en `Modules.Quotations.IntegrationTests`: `QuotationsApiHarness.cs:80-81` (sólo la clase), `QuotationExpirationApiTests.cs:119`, `ExportLoadSeedTests.cs:81,171-194`, `ExportTestProcessors.cs:8`; en Reporting: `Modules.Reporting.IntegrationTests/{ReportingApiHarness,ReportingResponses}.cs`, `Modules.Reporting.UnitTests/{CustomerReportSummaryHandlerTests,PriceChangeReportSummaryHandlerTests,QuotationsReportSummaryHandlerTests,ReportFilterParserTests,ReportingTestDoubles}.cs`.
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` (prueba nueva de red, Step 1).

**Interfaces:**
- Consumes: nada.
- Produces: los nombres de la tabla «Mapeo» de abajo, que Tasks 2–5 y el plan del frontend usan tal cual. Además, **quedan a propósito para tareas posteriores**:
  - Task 2: `QuotationsUnitOfWork.SaleQuotationIndex`, y los nombres de tabla, columna e índice en `QuotationsDbContext` y en el SQL crudo.
  - Task 3: los miembros que viajan en JSON (`OrderResponse.SaleNumber`, `OrderListItemResponse.SaleNumber`, `OrderDetailResponse.Sale`, `QuotationResponse.CanBeConvertedToSale`, `QuotationListItemResponse.SaleId`/`SaleStatus`, `OrdersReportItemDto.SaleId`/`SaleNumber`, `OrdersReportSummaryDto.SaleCount`) y sus espejos de prueba (`OrdersReportItem.SaleId`/`SaleNumber`, `OrdersReportSummary.SaleCount` en `ReportingResponses.cs`); los parámetros de endpoint `saleId` y `saleNumber`; las rutas, los tags y los códigos.
  - Task 4: `OrdersPermissions.SaleRead`/`SaleManage`, `ReportingPermissions.SalesRead` y sus valores.
  - Task 5: `ExportJobKind.Sales`, `OrdersExportFilters.SaleNumber`, las acciones de auditoría y todo texto.

**Mapeo de identificadores** (regla general `Sale` → `Order`, `Sales` → `Orders`; esta tabla fija los que usan las tareas siguientes)

| Hoy | Queda | Dónde |
| --- | --- | --- |
| `Sale`, `SaleId`, `SaleStatus`, `SalePaymentStatus` | `Order`, `OrderId`, `OrderStatus`, `OrderPaymentStatus` | Domain |
| `SalePaymentProof`, `SalePaymentProofId`, `SalePaymentProofInput`, `SalePaymentProofAmountUpdate` | `OrderPaymentProof`, `OrderPaymentProofId`, `OrderPaymentProofInput`, `OrderPaymentProofAmountUpdate` | Domain |
| `Sale.SaleNumber`, `Sale.SaleNumberMaxLength`, `SalePaymentProof.SaleId` | `Order.OrderNumber`, `Order.OrderNumberMaxLength`, `OrderPaymentProof.OrderId` | Domain |
| `Quotation.ConvertToSale` / `EnsureConvertibleToSale` / `CanBeConvertedToSale` | `ConvertToOrder` / `EnsureConvertibleToOrder` / `CanBeConvertedToOrder` | Domain |
| `SalesPermissions` (sólo la clase) | `OrdersPermissions` | Application |
| `ISaleRepository`, `SaleWithQuotation` (y su propiedad `Sale`), `SaleExportCursor` (y `SaleNumber`), `SalePage` | `IOrderRepository`, `OrderWithQuotation` (`Order`), `OrderExportCursor` (`OrderNumber`), `OrderPage` | Application |
| `SaleListing`, `SaleMapping`, `SaleNotFound`, `SaleNumberFormatter`, `ISaleNumberGenerator`, `SalePaymentProofResolver` | `OrderListing`, `OrderMapping`, `OrderNotFound`, `OrderNumberFormatter`, `IOrderNumberGenerator`, `OrderPaymentProofResolver` | Application |
| `SaleDto` (`SaleNumber`), `SalePaymentProofDto`, `SaleListItemDto` (`SaleNumber`), `SaleDetailDto` (`Sale`) | `OrderDto` (`OrderNumber`), `OrderPaymentProofDto`, `OrderListItemDto` (`OrderNumber`), `OrderDetailDto` (`Order`) | Application, no viajan |
| `SaleResponse`, `SaleListItemResponse`, `SaleDetailResponse`, `SalesPageResponse`, `SalePaymentProofResponse` | `OrderResponse`, `OrderListItemResponse`, `OrderDetailResponse`, `OrdersPageResponse`, `OrderPaymentProofResponse` | Application, viajan: **sus propiedades quedan** |
| `SalePaymentProofRequest`, `SalePaymentProofUpdateRequest`, `ConvertQuotationToSaleRequest`, `AddSalePaymentProofsRequest` | `OrderPaymentProofRequest`, `OrderPaymentProofUpdateRequest`, `ConvertQuotationToOrderRequest`, `AddOrderPaymentProofsRequest` | Application (sus propiedades no llevan "Sale") |
| `ConvertQuotationToSaleCommand` / `Validator` / `Handler`, `SalePaymentProofRequestValidator`, `SalePaymentProofUpdateRequestValidator` | `ConvertQuotationToOrderCommand` / `Validator` / `Handler`, `OrderPaymentProofRequestValidator`, `OrderPaymentProofUpdateRequestValidator` | Application |
| `AddSalePaymentProofsCommand` / `Validator` / `Handler`, `ApproveSaleCommand` / `Handler` | `AddOrderPaymentProofsCommand` / `Validator` / `Handler`, `ApproveOrderCommand` / `Handler` | Application |
| `GetSaleQuery` / `Handler`, `GetSaleByIdQuery` / `Handler` | `GetOrderQuery` / `Handler`, `GetOrderByIdQuery` / `Handler` | Application |
| `ListSalesQuery` (`SaleNumber`) / `ListSalesHandler` | `ListOrdersQuery` (`OrderNumber`) / `ListOrdersHandler` | Application |
| `ExportSalesCommand` (`SaleNumber`) / `Validator` / `Handler`, `SalesExportFilters`, `SalesExportProcessor` | `ExportOrdersCommand` (`OrderNumber`) / `Validator` / `Handler`, `OrdersExportFilters` (**`SaleNumber` queda**), `OrdersExportProcessor` | Application |
| `QuotationChangeSummary.ConvertedToSale(string saleNumber)` | `ConvertedToOrder(string orderNumber)` (texto sin cambio) | Application |
| `QuotationDto.CanBeConvertedToSale`, `QuotationListItemDto.SaleId` / `SaleStatus` | `CanBeConvertedToOrder`, `OrderId` / `OrderStatus` | Application, no viajan |
| `SaleRepository`, `SaleNumberGenerator`, `SaleNumberCounter` | `OrderRepository`, `OrderNumberGenerator`, `OrderNumberCounter` | Infrastructure |
| DbSets `Sales`, `SalePaymentProofs`, `SaleNumberCounters`; `ConfigureSale`, `ConfigureSalePaymentProof`, `ConfigureSaleNumberCounter` | `Orders`, `OrderPaymentProofs`, `OrderNumberCounters`; `ConfigureOrder`, `ConfigureOrderPaymentProof`, `ConfigureOrderNumberCounter` | Infrastructure |
| `SaleEndpoints.MapSaleEndpoints`; `ListSalesAsync`, `GetSaleByIdAsync`, `ExportSalesAsync`, `GetSaleAsync`, `ConvertQuotationToSaleAsync`, `AddSalePaymentProofsAsync`, `ApproveSaleAsync` | `OrderEndpoints.MapOrderEndpoints`; `ListOrdersAsync`, `GetOrderByIdAsync`, `ExportOrdersAsync`, `GetOrderAsync`, `ConvertQuotationToOrderAsync`, `AddOrderPaymentProofsAsync`, `ApproveOrderAsync` | Api (parámetros `saleId`, `saleNumber` quedan) |
| `ListSalesReportQuery` / `Handler`, `GetSalesReportSummaryQuery` / `Handler` | `ListOrdersReportQuery` / `Handler`, `GetOrdersReportSummaryQuery` / `Handler` | Reporting.Application |
| `SalesReportItemDto`, `SalesReportSummaryDto`, `SalesReportAggregate` (`SaleCount`) | `OrdersReportItemDto` (**props quedan**), `OrdersReportSummaryDto` (**`SaleCount` queda**), `OrdersReportAggregate` (`OrderCount`) | Reporting.Application |
| `SalesReportFilter`, `SalesReportFilterValidator`, `SalesReportCriteria`, `ISalesReportSource` | `OrdersReportFilter`, `OrdersReportFilterValidator`, `OrdersReportCriteria`, `IOrdersReportSource` | Reporting.Application |
| `SalePaymentStatusFilter` | `OrderPaymentStatusFilter` | Reporting.Domain |
| `ReportingEndpoints.ListSalesAsync`, `GetSalesSummaryAsync` | `ListOrdersAsync`, `GetOrdersSummaryAsync` | Reporting.Api |
| `SalesReportSource`, `SaleRow`, `FilterSales` | `OrdersReportSource`, `OrderRow`, `FilterOrders` | Bootstrapper |
| `ExportLoadSeeder.SalesSql`, `SaleCountersSql`, `seededSales`; `ExportLoadSeedResult.Sales`; parámetro `sales` y placeholder `{Sales} sales` de `ExportLoadSeedWorker.LogFinished` | `OrdersSql`, `OrderCountersSql`, `seededOrders`; `ExportLoadSeedResult.Orders`; `orders` y `{Orders} orders` (hallazgo 6) | Bootstrapper |
| `StubSaleListRepository`, `RecordedSaleExportSearch`, `FakeSalesReportSource`, `SaleUrl`, `SalesUrl`, `SaleProofsUrl`, `NewSale`, `CreateSaleAsync`, `SetSaleStatusAsync`, `ConvertToSaleAsync` | `StubOrderListRepository`, `RecordedOrderExportSearch`, `FakeOrdersReportSource`, `OrderUrl`, `OrdersUrl`, `OrderProofsUrl`, `NewOrder`, `CreateOrderAsync`, `SetOrderStatusAsync`, `ConvertToOrderAsync` | tests |
| `SalesReportItem`, `SalesReportSummary` (records de `ReportingResponses.cs`) | `OrdersReportItem`, `OrdersReportSummary` (**props quedan**) | tests |
| variables `sale`, `sales`, `saleNumber`, `saleId`, `saleRepository`, `saleCount`, `saleNumberPattern`, `salesJob`, `saleStatus`, `salePaymentStatus`, `firstSale` | `order`, `orders`, `orderNumber`, `orderId`, `orderRepository`, `orderCount`, `orderNumberPattern`, `ordersJob`, `orderStatus`, `orderPaymentStatus`, `firstOrder` | todo, salvo los parámetros de endpoint |
| nombres de clase y método de prueba con `Sale`/`Sales` | la misma regla, mecánica: `ASalesJobIsAuditedAsASaleExport` → `AOrdersJobIsAuditedAsAOrderExport` | tests |

Los nombres de prueba se renombran mecánicamente aunque el inglés quede raro ("AOrder…"): así el baseline se traduce con un `-replace` y la comparación por nombre sigue siendo exacta (Task 6).

**Qué no cambia en Task 1:** ningún literal de texto (rutas, tags, códigos, auditoría, permisos, SQL, nombres de tabla, columna e índice en `HasColumnName`/`ToTable`/`HasDatabaseName`, `'VEN-'`, `"Ventas"`, `"Aprobada"`, `"Sales"`, mensajes). Hay dos excepciones: los huecos de interpolación que nombran una variable renombrada (`$"Sale '{saleId}'…"` pasa a `$"Sale '{orderId}'…"`) y el placeholder del log del hallazgo 6.

**Comentarios:** en los archivos que tocas, un `<see cref>`/`<paramref>` a un símbolo renombrado se actualiza. Sin `GenerateDocumentationFile` el compilador no los valida y quedarían apuntando a nada. La prosa que nombra el concepto ("la venta", "a sale") pasa a "el pedido" / "an order", con concordancia masculina. Quedan como están:
- los IDs de slice (`SALE-01`, `SALE-04`) y las fechas o hashes de specs;
- la prosa que nombra algo que todavía existe con el nombre viejo —una tabla, un índice, una ruta, un código, un permiso—, que cambia en la tarea que cambia ese nombre;
- las rutas de archivos del frontend (`sale-table.tsx`, `sale-list.ts`), que pasan a `order-table.tsx` y `order-list.ts` siguiendo la regla de archivos del spec.

- [ ] **Step 1: Red de seguridad: el modelo coincide con el snapshot**

No es un RED: pasa hoy y tiene que seguir pasando después del rename, que es justamente lo que el hallazgo 4 afirma. Agrega al final de `QuotationsDbContextMappingTests` (antes de la llave de cierre de la clase):

```csharp
    /// <summary>
    /// El modelo y el último snapshot describen la misma base. Renombrar un tipo CLR sin tocar
    /// tablas, columnas ni índices no pide migración (plan 2026-09-14, Task 1), y una migración
    /// generada y después escrita a mano tiene que dejar el snapshot al día (Tasks 2 y 5). No abre
    /// conexión: compara el modelo con el snapshot, no con una base.
    /// </summary>
    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
```

Run:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: `Superado: 4` (las tres que ya existían y la nueva), `Con error: 0`.

- [ ] **Step 2: `git mv` de las pruebas**

```powershell
$qu = "tests/Modules/Quotations/Modules.Quotations.UnitTests"
$qi = "tests/Modules/Quotations/Modules.Quotations.IntegrationTests"
$ru = "tests/Modules/Reporting/Modules.Reporting.UnitTests"
$ri = "tests/Modules/Reporting/Modules.Reporting.IntegrationTests"
git mv "$qu/ExportSalesHandlerTests.cs" "$qu/ExportOrdersHandlerTests.cs"
git mv "$qu/ExportSalesValidatorTests.cs" "$qu/ExportOrdersValidatorTests.cs"
git mv "$qu/ListSalesHandlerTests.cs" "$qu/ListOrdersHandlerTests.cs"
git mv "$qu/SaleTests.cs" "$qu/OrderTests.cs"
git mv "$qu/SalesExportProcessorTests.cs" "$qu/OrdersExportProcessorTests.cs"
git mv "$qi/SaleApiTests.cs" "$qi/OrderApiTests.cs"
git mv "$qi/SaleExportApiTests.cs" "$qi/OrderExportApiTests.cs"
git mv "$qi/SaleListApiTests.cs" "$qi/OrderListApiTests.cs"
git mv "$ru/SalesReportHandlerTests.cs" "$ru/OrdersReportHandlerTests.cs"
git mv "$ru/SalesReportSummaryHandlerTests.cs" "$ru/OrdersReportSummaryHandlerTests.cs"
git mv "$ri/SalesReportApiTests.cs" "$ri/OrdersReportApiTests.cs"
git mv "$ri/SalesReportSummaryApiTests.cs" "$ri/OrdersReportSummaryApiTests.cs"
git status --short -- tests
```

Esperado: 12 líneas `R  …Sale…cs -> …Order…cs`.

- [ ] **Step 3: Renombrar dentro de las pruebas**

Aplica la tabla «Mapeo» y la regla general sobre estos archivos, y **sólo** sobre estos. Los 12 renombrados:
- `Modules.Quotations.UnitTests`: `ExportOrdersHandlerTests`, `ExportOrdersValidatorTests`, `ListOrdersHandlerTests`, `OrderTests`, `OrdersExportProcessorTests`;
- `Modules.Quotations.IntegrationTests`: `OrderApiTests`, `OrderExportApiTests`, `OrderListApiTests`;
- `Modules.Reporting.UnitTests`: `OrdersReportHandlerTests`, `OrdersReportSummaryHandlerTests`;
- `Modules.Reporting.IntegrationTests`: `OrdersReportApiTests`, `OrdersReportSummaryApiTests`.

Y los que no cambian de nombre:
- `tests/ArchitectureTests/ArchitectureTests/ReportingLayerTests.cs` (`typeof(ListOrdersReportQuery)` en `:16,25,47,82`);
- `Modules.Quotations.UnitTests`: `ExportJobRunnerTests.cs` (el nombre de método de `:54`), `ExportQuotationsHandlerTests.cs`, `ExportStatusLabelsTests.cs`, `ListQuotationsHandlerTests.cs`, `QuotationTests.cs`, `QuotationsDbContextMappingTests.cs` (en `:86-90`: `typeof(Order)`, `"OrderNumber"` y el nombre del método `QuotationsAndOrdersHaveAnIndexForTheExportKeyset`; el nombre del índice `"IX_sales_tenant_converted_at_number"` **queda**), `QuotationsTestDoubles.cs`;
- `Modules.Quotations.IntegrationTests`: `QuotationsApiHarness.cs` (sólo `SalesPermissions` → `OrdersPermissions` en `:80-81`), `QuotationExpirationApiTests.cs:119`, `ExportLoadSeedTests.cs` (`OrdersPageResponse`, `OrdersExportFilters`, `result.Orders`, `ordersJob`), `ExportTestProcessors.cs:8` (comentario);
- `Modules.Reporting.IntegrationTests`: `ReportingApiHarness.cs`, `ReportingResponses.cs`;
- `Modules.Reporting.UnitTests`: `CustomerReportSummaryHandlerTests.cs`, `PriceChangeReportSummaryHandlerTests.cs`, `QuotationsReportSummaryHandlerTests.cs`, `ReportFilterParserTests.cs`, `ReportingTestDoubles.cs`.

En las pruebas **quedan sin tocar**:
- las rutas en texto (`"/sale"`, `"/sales"`, `"/reports/sales"`, `?saleNumber=`) y los códigos (`"sale.sale.…"`);
- `"VEN-…"`, `"Ventas"`/`"Venta"`/`"ventas-…"` y `"Aprobada"`;
- `"Sales"` en los payloads de Notifications;
- `ExportJobKind.Sales`;
- las constantes `SaleRead`/`SaleManage`/`SalesRead`;
- los accesos a miembros que viajan (`order.SaleNumber` sobre un `OrderResponse`, `detail.Sale`, `fetched.CanBeConvertedToSale` sobre un `QuotationResponse`, `summary.SaleCount` sobre un `OrdersReportSummaryDto`, `item.SaleId`/`item.SaleNumber` sobre un `OrdersReportItem`).

Ojo con `ListQuotationsHandlerTests.cs:138-145,195`: ahí `SaleId`/`SaleStatus` son de `QuotationListItemDto`, que no viaja, así que pasan a `OrderId`/`OrderStatus`. Y `QuotationsTestDoubles.cs:521` lee `quotation.CanBeConvertedToSale` del dominio: pasa a `CanBeConvertedToOrder`.

- [ ] **Step 4: Compilar las pruebas y verlas fallar (RED)**

```powershell
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore | Select-String -Pattern "error CS" | Select-Object -First 5
dotnet build tests/Modules/Reporting/Modules.Reporting.UnitTests --no-restore | Select-String -Pattern "error CS" | Select-Object -First 5
```

Esperado (RED): errores de compilación como `error CS0246: No se encontró el tipo o el nombre del espacio de nombres 'Order'` (o `The type or namespace name 'Order' could not be found`), `'ConvertQuotationToOrderRequest'`, `'OrdersReportFilter'`, `'OrderPaymentStatusFilter'`. Pega las líneas.

- [ ] **Step 5: `git mv` de producción**

```powershell
$d = "src/Modules/Quotations/Modules.Quotations.Domain"
$a = "src/Modules/Quotations/Modules.Quotations.Application"
$p = "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence"
$api = "src/Modules/Quotations/Modules.Quotations.Api"
$ra = "src/Modules/Reporting/Modules.Reporting.Application"
git mv "$d/Sale.cs" "$d/Order.cs"
git mv "$d/SaleId.cs" "$d/OrderId.cs"
git mv "$d/SalePaymentProof.cs" "$d/OrderPaymentProof.cs"
git mv "$d/SalePaymentProofId.cs" "$d/OrderPaymentProofId.cs"
git mv "$d/SalePaymentStatus.cs" "$d/OrderPaymentStatus.cs"
git mv "$d/SaleStatus.cs" "$d/OrderStatus.cs"
git mv "$a/AddSalePaymentProofs.cs" "$a/AddOrderPaymentProofs.cs"
git mv "$a/ApproveSale.cs" "$a/ApproveOrder.cs"
git mv "$a/ConvertQuotationToSale.cs" "$a/ConvertQuotationToOrder.cs"
git mv "$a/ExportSales.cs" "$a/ExportOrders.cs"
git mv "$a/GetSale.cs" "$a/GetOrder.cs"
git mv "$a/GetSaleById.cs" "$a/GetOrderById.cs"
git mv "$a/ISaleNumberGenerator.cs" "$a/IOrderNumberGenerator.cs"
git mv "$a/ISaleRepository.cs" "$a/IOrderRepository.cs"
git mv "$a/ListSales.cs" "$a/ListOrders.cs"
git mv "$a/SaleListing.cs" "$a/OrderListing.cs"
git mv "$a/SaleMapping.cs" "$a/OrderMapping.cs"
git mv "$a/SaleNotFound.cs" "$a/OrderNotFound.cs"
git mv "$a/SaleNumberFormatter.cs" "$a/OrderNumberFormatter.cs"
git mv "$a/SalePaymentProofResolver.cs" "$a/OrderPaymentProofResolver.cs"
git mv "$a/SalesDtos.cs" "$a/OrdersDtos.cs"
git mv "$a/SalesExportProcessor.cs" "$a/OrdersExportProcessor.cs"
git mv "$a/SalesPermissions.cs" "$a/OrdersPermissions.cs"
git mv "$p/SaleNumberCounter.cs" "$p/OrderNumberCounter.cs"
git mv "$p/SaleNumberGenerator.cs" "$p/OrderNumberGenerator.cs"
git mv "$p/SaleRepository.cs" "$p/OrderRepository.cs"
git mv "$api/SaleEndpoints.cs" "$api/OrderEndpoints.cs"
git mv "$ra/GetSalesReportSummary.cs" "$ra/GetOrdersReportSummary.cs"
git mv "$ra/ListSalesReport.cs" "$ra/ListOrdersReport.cs"
git mv src/Bootstrapper/SalesReportSource.cs src/Bootstrapper/OrdersReportSource.cs
git status --short -- src | Select-String -Pattern "^R" | Measure-Object | Select-Object -ExpandProperty Count
```

Esperado: `30`. Ningún archivo de `Persistence/Migrations/` aparece en `git status`.

- [ ] **Step 6: Renombrar dentro de producción**

Aplica la tabla «Mapeo» en este orden. Cada capa compila contra la anterior, así que los errores se leen de a una.

1. **Domain:** los 6 renombrados, más `Quotation.cs`, `QuotationBillingAccount.cs:18`, `QuotationParty.cs:61`, `QuotationPartyDetails.cs:115` y `QuotationStatus.cs:7-13` (crefs y prosa).
2. **Application:** los 17 renombrados, más `ExportBatchLoop.cs:5`, `ExportStatusLabels.cs:28-41` (tipos `OrderStatus`/`OrderPaymentStatus`; los textos quedan), `ListQuotations.cs:61-64,95,168-169`, `QuotationChangeSummary.cs:88-89` (sólo el nombre del método y del parámetro), `QuotationListing.cs:79` (comentario), `QuotationMapping.cs:41,61-85`, `QuotationResponseComposer.cs:89`, `QuotationsDtos.cs:76-82` (`QuotationDto`; **no** `QuotationResponse:323` ni `QuotationListItemResponse:372-375`), `IQuotationCustomerLookup.cs:28` y `IQuotationFileLookup.cs:6` (comentario).
3. **Infrastructure:**
   - `QuotationsDbContext.cs:19-23,39-41,335-445`: tipos, DbSets, métodos `Configure*`, lambdas y la variable `sale` → `order`. `ToTable`, `HasColumnName` y `HasDatabaseName` **no cambian**.
   - `OrderRepository.cs`, `OrderNumberGenerator.cs`, `OrderNumberCounter.cs`: tipos y variables; el SQL queda.
   - `QuotationUserReferenceProbe.cs:48,51`: `dbContext.Orders`, `dbContext.OrderPaymentProofs`.
   - `QuotationsUnitOfWork.cs:15-17`: en el comentario, `Order.QuotationId` y `EnsureConvertibleToOrder`. La constante y su valor quedan.
   - `QuotationsInfrastructureExtensions.cs:48-49`: `AddScoped<IOrderRepository, OrderRepository>()` y `AddScoped<IOrderNumberGenerator, OrderNumberGenerator>()`.
4. **Api:** `OrderEndpoints.cs` según la tabla. Rutas, tags y los parámetros `saleId` (`:143,149`) y `saleNumber` (`:102,109,130,135`) **quedan**. `Program.cs:113` pasa a `app.MapOrderEndpoints();`.
5. **Reporting:**
   - `ReportingEndpoints.cs`: los métodos y crefs de `:72-114,138,180,225`; las rutas quedan.
   - `ListOrdersReport.cs`, `GetOrdersReportSummary.cs`.
   - `ReportSources.cs`, `ReportSummaries.cs` (`OrdersReportAggregate.OrderCount` en `:68`; `OrdersReportSummaryDto.SaleCount` en `:18` **queda**), `ReportingDtos.cs` (sólo el nombre del record), `ReportingFilters.cs`, `CustomerReportSummary.cs`, `PriceChangeReportSummary.cs`, `QuotationsReportSummary.cs`, `ListQuotationsReport.cs`, `ReportFilterEnums.cs`.
6. **Bootstrapper:**
   - `QepServiceCollectionExtensions.cs`: los handlers de `:342-370`, `AddValidatorsFromAssemblyContaining<OrdersReportFilterValidator>()` en `:394`, `AddScoped<IExportJobProcessor, OrdersExportProcessor>()` en `:456`, `AddScoped<IOrdersReportSource, OrdersReportSource>()` en `:466`, y `SalesPermissions` → `OrdersPermissions` en `:544-617,761-767,1005-1009` (las constantes `SaleRead`/`SaleManage` quedan).
   - `OrdersReportSource.cs`: `OrderRow`, `FilterOrders`, variables.
   - Crefs en `QuotationsReportSource.cs`, `CustomerReportSource.cs`, `PriceChangeReportSource.cs` y `QuotationFileLookup.cs`.
   - `ExportLoadSeeder.cs`: `OrdersSql`, `OrderCountersSql`, `seededOrders`, `ExportLoadSeedResult.Orders`; el SQL queda.
   - `ExportLoadSeedWorker.cs:31-33,74-76`: el parámetro `orders` y el template `"… {Items} items and {Orders} orders in {ElapsedMilliseconds} ms."` juntos (hallazgo 6).

Si el compilador marca `CS0104` (`'Order' is an ambiguous reference`) en un archivo que también importa un namespace con un tipo `Order`, califícalo (`Domain.Order`) o usa un alias en ese archivo. No renombres el tipo.

- [ ] **Step 7: Compilar la solución (GREEN de compilación)**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`. Si queda un error, es un uso que la tabla cubre y no se aplicó: corrígelo con la tabla, sin inventar nombres.

- [ ] **Step 8: Sin migración pendiente**

```powershell
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: `No changes have been made to the model since the last migration.` y `Superado: 4`.

Si dice que hay cambios, algo tocó el modelo relacional (casi siempre un `HasColumnName`, `ToTable` o `HasDatabaseName` que se renombró por error). Para verlo sin dejar rastro:

```powershell
dotnet ef migrations add Probe --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_Probe.cs" | Get-Content
dotnet ef migrations remove --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
```

Corriges el mapeo hasta que `has-pending-model-changes` quede limpio, y el último `git status` no muestra nada en `Migrations/`. **Task 1 no commitea ninguna migración.**

- [ ] **Step 9: Probar que no quedó ningún símbolo con el nombre viejo**

```powershell
$allowed = 'ExportJobKind\.Sales|:\s+Sales,\s*$|OrdersPermissions\.Sale(Read|Manage)\b|ReportingPermissions\.SalesRead\b|const string Sales?(Read|Manage) =|SaleQuotationIndex|string\?? SaleNumber\b|\.SaleNumber\b|Guid\?? SaleId\b|\.SaleId\b|string\? SaleStatus\b|bool CanBeConvertedToSale,|(fetched|fetchedQuotation|afterResend)\.CanBeConvertedToSale\b|int SaleCount,|\.SaleCount\b|OrderResponse Sale\)|detail\.Sale\.|OrderEndpoints\.cs:\d+:.*\bsale(Id|Number)\b|\?saleNumber=|"Sales"|\$"Sale (for|'')|AuthPreferenceEndpoints\.cs:10:|QuotationsReportSource\.cs:114:|CustomerIdentification\.cs:18:'
rg -n -s "\w*Sales?\w*|\bsales?[A-Z]\w*" src tests --glob '*.cs' --glob '!**/Migrations/**' | Select-String -NotMatch -Pattern $allowed
```

Esperado: **ninguna línea**. Lo que el filtro deja pasar es exactamente lo de «Produces»: lo que queda para Tasks 2–5, más las tres líneas donde "Sale" es el verbo *salir* al empezar una oración. Si aparece un comentario que nombra un símbolo ya renombrado —por ejemplo `QepServiceCollectionExtensions.cs:1003` ("los dos de Sales") o `:1013` ("Quotations/Sales")—, pasa a `Orders` y vuelves a correr.

- [ ] **Step 10: Las pruebas siguen como en el baseline**

Con Docker corriendo. Las dos suites de integración tardan decenas de minutos.

```powershell
$run = Join-Path $env:TEMP "qep-rename-orders-task1"
Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
foreach ($project in @(
    "tests/Modules/Quotations/Modules.Quotations.UnitTests",
    "tests/Modules/Reporting/Modules.Reporting.UnitTests",
    "tests/Modules/Notifications/Modules.Notifications.UnitTests",
    "tests/Modules/Authorization/Modules.Authorization.UnitTests",
    "tests/ArchitectureTests/ArchitectureTests",
    "tests/Modules/Quotations/Modules.Quotations.IntegrationTests",
    "tests/Modules/Reporting/Modules.Reporting.IntegrationTests")) {
    dotnet test $project --no-build --logger trx --results-directory $run
}
$expected = Get-Content (Join-Path $env:TEMP "qep-rename-orders-baseline-failed.txt") |
    ForEach-Object { $_ -replace 'Sales', 'Orders' -replace 'Sale', 'Order' }
Get-ChildItem -LiteralPath $run -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique | Where-Object { $expected -notcontains $_ }
```

Esperado: el último comando **no imprime nada** (ninguna falla que no estuviera en el baseline). Pega el resumen `Superado/Con error` de cada proyecto.

- [ ] **Step 11: Formato**

```powershell
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
dotnet format Backend.slnx --verify-no-changes --include $changed
```

Esperado: sale sin errores y sin listar archivos. Si lista alguno, corriges a mano la línea que marca: nunca `dotnet format` sin `--verify-no-changes`.

- [ ] **Step 12: Commit**

```powershell
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git status --short -- . ':!src' ':!tests'
git add -- src tests
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
git commit -m "refactor(quotations): renombrar símbolos de venta a pedido" -m "Sale -> Order en tipos, archivos, miembros, handlers, DI y pruebas. Sin cambios de contrato, esquema ni textos: rutas, JSON, códigos, permisos, tablas y kind se renombran en los commits siguientes."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el primer `git status` sin salida (nada fuera de `src/` y `tests/`), el segundo también (ninguna migración), el commit creado y el `Select-String` sin salida.

---

### Task 2: Esquema de base

Renombra en la base las tres tablas, las dos columnas, los cinco índices, las tres PK y las dos FK, con una migración de `Quotations` escrita a mano. En el mismo commit cambia todo lo que nombra la base en texto: la constante del índice, el SQL crudo del generador, la carga sintética y el script de limpieza. Si esos cuatro quedaran para después, el commit compilaría y fallaría en runtime.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:338,346,382-396,410,417,425,440` (y la prosa de `:383,387-388,392-394,398-400,427,435`)
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_RenameToOrders.cs` y su `.Designer.cs` (generados; `Up`/`Down` reemplazados a mano)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` (regenerado, nunca a mano)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs:15-21,59`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderNumberGenerator.cs:14,23,33`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs:124` (comentario) y `QuotationUserReferenceProbe.cs:19` (comentario)
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:338-380`
- Modify: `ops/export-load-cleanup.sql:27-28,32`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` (prueba nueva; índice en `:87`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs` (crear)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs` (prueba nueva)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs:231,235,299-300`

**Interfaces:**
- Consumes: de Task 1, `Order`, `OrderPaymentProof`, `OrderNumberCounter`, `Order.OrderNumber`, `OrderPaymentProof.OrderId`, `OrderUrl`, `ConvertQuotationToOrderRequest` y el harness de `QuotationsApiHarness` (`StartDatabaseAsync`, `QepApiFactory`, `RegisterTenantAsync`, `ManagerPermissions`, `CreateActiveCustomerAsync`, `CreateProductWithScalesAsync`, `CreateSentQuotationAsync`).
- Produces:
  - tablas `quotations.orders`, `quotations.order_payment_proofs`, `quotations.order_number_counters`;
  - columnas `orders.order_number` y `order_payment_proofs.order_id`;
  - índices `IX_orders_tenant`, `IX_orders_quotation`, `IX_orders_tenant_number`, `IX_orders_tenant_converted_at_number`, `IX_order_payment_proofs_order`;
  - constraints `PK_orders`, `PK_order_payment_proofs`, `PK_order_number_counters`, `FK_orders_quotations_quotation_id`, `FK_order_payment_proofs_orders_order_id`;
  - la migración cuyo id termina en `_RenameToOrders`;
  - la constante `QuotationsUnitOfWork.OrderQuotationIndex = "IX_orders_quotation"`;
  - en `OrdersMigrationTests`, los helpers `NewContext`, `MigrationId`, `ExecuteAsync`, `ScalarAsync<T>` y `ListAsync`, que Task 5 reusa.

- [ ] **Step 1: Prueba de mapeo (RED)**

En `QuotationsDbContextMappingTests.cs`, cambia el nombre del índice de `:87` a `"IX_orders_tenant_converted_at_number"` y agrega esta prueba a la clase:

```csharp
    /// <summary>
    /// Los nombres de base de los pedidos (spec 2026-09-14). Van a mano en el mapeo y la migración
    /// que los renombra se escribió a mano: un nombre que no coincida no lo ve el compilador, lo ve
    /// la próxima migración generada, que intentaría recrear la tabla. La FK y la PK salen por
    /// convención del nombre de la tabla, así que también se fijan acá.
    /// </summary>
    [Fact]
    public void OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var order = model.FindEntityType(typeof(Order))!;
        Assert.Equal("orders", order.GetTableName());
        Assert.Equal("quotations", order.GetSchema());
        Assert.Equal("order_number", order.FindProperty(nameof(Order.OrderNumber))!.GetColumnName());
        Assert.Equal("PK_orders", order.FindPrimaryKey()!.GetName());
        Assert.Equal(
            ["IX_orders_quotation", "IX_orders_tenant", "IX_orders_tenant_converted_at_number", "IX_orders_tenant_number"],
            order.GetIndexes().Select(index => index.GetDatabaseName()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            "FK_orders_quotations_quotation_id",
            Assert.Single(order.GetForeignKeys()).GetConstraintName());

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        Assert.Equal("order_payment_proofs", proof.GetTableName());
        Assert.Equal("order_id", proof.FindProperty(nameof(OrderPaymentProof.OrderId))!.GetColumnName());
        Assert.Equal("PK_order_payment_proofs", proof.FindPrimaryKey()!.GetName());
        Assert.Equal("IX_order_payment_proofs_order", Assert.Single(proof.GetIndexes()).GetDatabaseName());
        Assert.Equal(
            "FK_order_payment_proofs_orders_order_id",
            Assert.Single(proof.GetForeignKeys()).GetConstraintName());

        var counter = model.FindEntityType(typeof(OrderNumberCounter))!;
        Assert.Equal("order_number_counters", counter.GetTableName());
        Assert.Equal("PK_order_number_counters", counter.FindPrimaryKey()!.GetName());
    }
```

`OrderNumberCounter` es `internal` en Infrastructure; `Modules.Quotations.UnitTests` ya tiene `InternalsVisibleTo` (`Modules.Quotations.Infrastructure.csproj:8`).

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado (RED), dos fallas:
- `OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints` con `Assert.Equal() Failure: Strings differ` · `Expected: "orders"` · `Actual: "sales"`;
- `QuotationsAndOrdersHaveAnIndexForTheExportKeyset` con `Sequence contains no matching element` (el índice todavía se llama `IX_sales_tenant_converted_at_number`).

- [ ] **Step 3: Prueba de la migración sobre filas viejas (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las migraciones que renombran ventas a pedidos (spec 2026-09-14), contra una base con filas del
/// esquema viejo. Migra sólo Quotations, y hasta una migración puntual, con <see cref="IMigrator"/>:
/// el host de pruebas migra todo a la última al arrancar, y así no habría esquema viejo donde
/// sembrar.
///
/// Las filas viejas se insertan con <c>session_replication_role = replica</c>, que apaga el chequeo
/// de la FK hacia <c>quotations.quotations</c>. Una cotización real en ese estado del esquema exige
/// decenas de columnas obligatorias que no tienen que ver con lo que se prueba, y la FK se verifica
/// igual, por nombre, en <c>pg_constraint</c>. El usuario del contenedor es superusuario, que es lo
/// que ese SET pide.
/// </summary>
public sealed class OrdersMigrationTests
{
    private const string LastMigrationBeforeTheRename = "20260913184747_AddQuotationPartyTaxProfile";

    private const string TenantId = "01900000-0000-7000-8000-00000000c001";
    private const string OrderId = "01900000-0000-7000-8000-00000000c002";
    private const string ProofId = "01900000-0000-7000-8000-00000000c003";
    private const string MemberId = "01900000-0000-7000-8000-00000000c005";

    private const string LegacyRowsSql = $"""
        SET session_replication_role = replica;
        INSERT INTO quotations.sales (
            id, tenant_id, sale_number, quotation_id, status, payment_status, notes, converted_at,
            converted_by, approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        VALUES (
            '{OrderId}', '{TenantId}', 'VEN-2026-0001', '01900000-0000-7000-8000-00000000c004', 'Pending',
            'FullPaymentReceived', NULL, '2026-09-10T15:00:00Z', '{MemberId}',
            NULL, NULL, NULL, '2026-09-10T15:00:00Z', '2026-09-10T15:00:00Z', 1);
        INSERT INTO quotations.sale_payment_proofs (id, sale_id, file_id, amount, uploaded_by, uploaded_at)
        VALUES (
            '{ProofId}', '{OrderId}', '01900000-0000-7000-8000-00000000c006', 150000.00,
            '{MemberId}', '2026-09-10T15:00:00Z');
        INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)
        VALUES ('{TenantId}', 2026, 2);
        RESET session_replication_role;
        """;

    private const string TablesSql = """
        SELECT tablename FROM pg_tables
        WHERE schemaname = 'quotations'
          AND tablename IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
        """;

    private const string IndexesSql = """
        SELECT indexname FROM pg_indexes
        WHERE schemaname = 'quotations'
          AND tablename IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
        """;

    // Sólo PK y FK: en PostgreSQL 18 los NOT NULL también son constraints con nombre
    // (<tabla>_<columna>_not_null), EF no los modela y un RENAME de tabla no los renombra.
    private const string ConstraintsSql = """
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'quotations'
          AND t.relname IN ('sales', 'sale_payment_proofs', 'sale_number_counters',
                            'orders', 'order_payment_proofs', 'order_number_counters')
          AND c.contype IN ('p', 'f')
        """;

    [Fact]
    public async Task TheRenameKeepsEveryOrderProofAndCounterUnderTheNewNames()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRowsSql);

        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("VEN-2026-0001", await ScalarAsync<string>(
            connectionString, $"SELECT order_number FROM quotations.orders WHERE id = '{OrderId}'"));
        Assert.Equal(Guid.Parse(OrderId), await ScalarAsync<Guid>(
            connectionString, $"SELECT order_id FROM quotations.order_payment_proofs WHERE id = '{ProofId}'"));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            $"SELECT next_value FROM quotations.order_number_counters WHERE tenant_id = '{TenantId}' AND year = 2026"));
        Assert.Equal(
            ["order_number_counters", "order_payment_proofs", "orders"],
            await ListAsync(connectionString, TablesSql));
        Assert.Equal(
            ["IX_order_payment_proofs_order", "IX_orders_quotation", "IX_orders_tenant",
             "IX_orders_tenant_converted_at_number", "IX_orders_tenant_number",
             "PK_order_number_counters", "PK_order_payment_proofs", "PK_orders"],
            await ListAsync(connectionString, IndexesSql));
        Assert.Equal(
            ["FK_order_payment_proofs_orders_order_id", "FK_orders_quotations_quotation_id",
             "PK_order_number_counters", "PK_order_payment_proofs", "PK_orders"],
            await ListAsync(connectionString, ConstraintsSql));
    }

    [Fact]
    public async Task RevertingTheRenameBringsBackTheOldNamesWithTheRows()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRowsSql);
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(LastMigrationBeforeTheRename, TestContext.Current.CancellationToken);

        Assert.Equal("VEN-2026-0001", await ScalarAsync<string>(
            connectionString, $"SELECT sale_number FROM quotations.sales WHERE id = '{OrderId}'"));
        Assert.Equal(Guid.Parse(OrderId), await ScalarAsync<Guid>(
            connectionString, $"SELECT sale_id FROM quotations.sale_payment_proofs WHERE id = '{ProofId}'"));
        Assert.Equal(
            ["sale_number_counters", "sale_payment_proofs", "sales"],
            await ListAsync(connectionString, TablesSql));
        Assert.Equal(
            ["IX_sale_payment_proofs_sale", "IX_sales_quotation", "IX_sales_tenant",
             "IX_sales_tenant_converted_at_number", "IX_sales_tenant_number",
             "PK_sale_number_counters", "PK_sale_payment_proofs", "PK_sales"],
            await ListAsync(connectionString, IndexesSql));
        Assert.Equal(
            ["FK_sale_payment_proofs_sales_sale_id", "FK_sales_quotations_quotation_id",
             "PK_sale_number_counters", "PK_sale_payment_proofs", "PK_sales"],
            await ListAsync(connectionString, ConstraintsSql));
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(QuotationsDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Ordenado en C# y no con ORDER BY: el orden de texto de Postgres depende de la
    /// collation de la base.</summary>
    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }
}
```

Se apunta a `_RenameToOrders` y no a "la última" a propósito: cuando Task 5 agregue su migración, esta prueba sigue probando exactamente el rename.

- [ ] **Step 4: Prueba de la constante del índice, y el SQL de las pruebas de la carga (RED)**

No hay una prueba de `already_converted` que reusar (hallazgo 2): `ConvertingAnAlreadyConvertedQuotationIsRejected` sigue esperando `status_not_convertible` y no se toca. En `OrderApiTests.cs` agrega `using Npgsql;` a los `using` y esta prueba, a continuación de esa:

```csharp
    // La red de abajo del índice único de Order.QuotationId (QuotationsUnitOfWork): un pedido de
    // antes de que existiera Converted dejó su cotización en Sent, así que el estado no corta la
    // segunda conversión y la corta el índice. Si el nombre del índice cambia y la constante no, esto
    // sale 500 con el nombre de la constraint adentro (spec 2026-09-14, «Riesgos»).
    [Fact]
    public async Task ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await InsertLegacyOrderAsync(database.GetConnectionString(), tenantId, quotation.Id);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quotation.quotation.already_converted", body, StringComparison.Ordinal);
    }

    /// <summary>Un pedido "legado" para una cotización que siguió en Sent. Número fuera de la
    /// secuencia a propósito: lo único que tiene que chocar es el índice de la cotización.</summary>
    private static async Task InsertLegacyOrderAsync(string connectionString, Guid tenantId, Guid quotationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO quotations.orders (
                id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at,
                converted_by, approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
            VALUES (
                @id, @tenantId, 'LEGADO-0001', @quotationId, 'Pending', 'PaymentPending', NULL, now(),
                @convertedBy, NULL, NULL, NULL, now(), now(), 1)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("quotationId", quotationId);
        command.Parameters.AddWithValue("convertedBy", Guid.CreateVersion7());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
```

En `ExportLoadSeedTests.cs`, el SQL que lee la base:

| Línea | Hoy | Queda |
| --- | --- | --- |
| `:231` | `FROM quotations.sales` | `FROM quotations.orders` |
| `:235` | `… FROM quotations.sale_number_counters WHERE …` | `… FROM quotations.order_number_counters WHERE …` |
| `:299` | `"quotations.sales",` | `"quotations.orders",` |
| `:300` | `"quotations.sale_number_counters",` | `"quotations.order_number_counters",` |

- [ ] **Step 5: Correrlas y verlas fallar**

Con Docker corriendo:

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrdersMigrationTests|FullyQualifiedName~ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted"
```

Esperado (RED), tres fallas:
- las dos de `OrdersMigrationTests` con `System.InvalidOperationException : Sequence contains no matching element` (todavía no hay migración `_RenameToOrders`);
- `ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted` con `Npgsql.PostgresException : 42P01: relation "quotations.orders" does not exist`.

- [ ] **Step 6: Renombrar el mapeo**

En `QuotationsDbContext.cs` sólo cambian los textos de base (Task 1 ya renombró los tipos):

| Línea | Hoy | Queda |
| --- | --- | --- |
| `:338` | `order.ToTable("sales", "quotations");` | `order.ToTable("orders", "quotations");` |
| `:346` | `.HasColumnName("sale_number")` | `.HasColumnName("order_number")` |
| `:382` | `.HasDatabaseName("IX_sales_tenant");` | `.HasDatabaseName("IX_orders_tenant");` |
| `:386` | `.HasDatabaseName("IX_sales_quotation");` | `.HasDatabaseName("IX_orders_quotation");` |
| `:391` | `.HasDatabaseName("IX_sales_tenant_number");` | `.HasDatabaseName("IX_orders_tenant_number");` |
| `:396` | `.HasDatabaseName("IX_sales_tenant_converted_at_number");` | `.HasDatabaseName("IX_orders_tenant_converted_at_number");` |
| `:410` | `proof.ToTable("sale_payment_proofs", "quotations");` | `proof.ToTable("order_payment_proofs", "quotations");` |
| `:417` | `.HasColumnName("sale_id")` | `.HasColumnName("order_id")` |
| `:425` | `.HasDatabaseName("IX_sale_payment_proofs_sale");` | `.HasDatabaseName("IX_order_payment_proofs_order");` |
| `:440` | `counter.ToTable("sale_number_counters", "quotations");` | `counter.ToTable("order_number_counters", "quotations");` |

En la prosa de `:387-388`, "misma lección de SDD-CT-06 que IX_quotations_tenant_number" queda. Donde diga "número de venta", "la venta" o "su venta", pasa a "número de pedido", "el pedido" o "su pedido".

- [ ] **Step 7: La prueba de mapeo pasa y la red de seguridad avisa que falta la migración**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: pasan `OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints` y `QuotationsAndOrdersHaveAnIndexForTheExportKeyset`, y falla **a propósito** `TheModelHasNoChangesPendingAMigration` (`Assert.False() Failure` · `Expected: False` · `Actual: True`): el modelo ya no coincide con el snapshot. Es la prueba de Task 1 haciendo su trabajo.

- [ ] **Step 8: Generar la migración y ver lo que EF infirió**

```powershell
dotnet ef migrations add RenameToOrders --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
$migration = Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_RenameToOrders.cs" | Where-Object { $_.Name -notlike "*.Designer.cs" }
$migration.FullName
Select-String -Path $migration.FullName -Pattern "DropTable|CreateTable" | Measure-Object | Select-Object -ExpandProperty Count
```

Esperado: se crean `<timestamp>_RenameToOrders.cs` y `.Designer.cs`, se modifica `QuotationsDbContextModelSnapshot.cs`, y la cuenta es **mayor que 0**. EF no infirió el rename, por el hallazgo 4, y por eso el `Up`/`Down` generado se descarta entero. El `.Designer.cs` y el snapshot se quedan como salieron: describen el modelo nuevo, que es lo correcto.

- [ ] **Step 9: Reemplazar `Up` y `Down` a mano**

Los nombres viejos salen de `20260824210345_AddSalesAndPaymentProofs.cs:25,49,51,73,75,84-107` y de `20260912234651_AddExportJobs.cs:40-43`. Deja el archivo `<timestamp>_RenameToOrders.cs` exactamente así; el nombre de la clase y el namespace ya los generó EF:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameToOrders : Migration
    {
        // Escrita a mano (spec 2026-09-14; plan, Task 2). EF emparejó por nombre de tabla y de tipo, y
        // cambiaron los dos a la vez: lo que generó era DropTable + CreateTable, que borraba los datos.
        // Esto sólo renombra. PK y FK van por SQL porque EF no tiene operación para renombrarlas; en
        // una PK, RENAME CONSTRAINT renombra también su índice.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "sales", schema: "quotations", newName: "orders", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "sale_payment_proofs", schema: "quotations", newName: "order_payment_proofs", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "sale_number_counters", schema: "quotations", newName: "order_number_counters", newSchema: "quotations");

            migrationBuilder.RenameColumn(name: "sale_number", schema: "quotations", table: "orders", newName: "order_number");
            migrationBuilder.RenameColumn(name: "sale_id", schema: "quotations", table: "order_payment_proofs", newName: "order_id");

            migrationBuilder.RenameIndex(name: "IX_sales_tenant", schema: "quotations", table: "orders", newName: "IX_orders_tenant");
            migrationBuilder.RenameIndex(name: "IX_sales_quotation", schema: "quotations", table: "orders", newName: "IX_orders_quotation");
            migrationBuilder.RenameIndex(name: "IX_sales_tenant_number", schema: "quotations", table: "orders", newName: "IX_orders_tenant_number");
            migrationBuilder.RenameIndex(name: "IX_sales_tenant_converted_at_number", schema: "quotations", table: "orders", newName: "IX_orders_tenant_converted_at_number");
            migrationBuilder.RenameIndex(name: "IX_sale_payment_proofs_sale", schema: "quotations", table: "order_payment_proofs", newName: "IX_order_payment_proofs_order");

            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "PK_sales" TO "PK_orders";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "PK_sale_payment_proofs" TO "PK_order_payment_proofs";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_number_counters RENAME CONSTRAINT "PK_sale_number_counters" TO "PK_order_number_counters";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "FK_sales_quotations_quotation_id" TO "FK_orders_quotations_quotation_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "FK_sale_payment_proofs_sales_sale_id" TO "FK_order_payment_proofs_orders_order_id";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "FK_order_payment_proofs_orders_order_id" TO "FK_sale_payment_proofs_sales_sale_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "FK_orders_quotations_quotation_id" TO "FK_sales_quotations_quotation_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_number_counters RENAME CONSTRAINT "PK_order_number_counters" TO "PK_sale_number_counters";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "PK_order_payment_proofs" TO "PK_sale_payment_proofs";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "PK_orders" TO "PK_sales";""");

            migrationBuilder.RenameIndex(name: "IX_order_payment_proofs_order", schema: "quotations", table: "order_payment_proofs", newName: "IX_sale_payment_proofs_sale");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant_converted_at_number", schema: "quotations", table: "orders", newName: "IX_sales_tenant_converted_at_number");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant_number", schema: "quotations", table: "orders", newName: "IX_sales_tenant_number");
            migrationBuilder.RenameIndex(name: "IX_orders_quotation", schema: "quotations", table: "orders", newName: "IX_sales_quotation");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant", schema: "quotations", table: "orders", newName: "IX_sales_tenant");

            migrationBuilder.RenameColumn(name: "order_id", schema: "quotations", table: "order_payment_proofs", newName: "sale_id");
            migrationBuilder.RenameColumn(name: "order_number", schema: "quotations", table: "orders", newName: "sale_number");

            migrationBuilder.RenameTable(name: "order_number_counters", schema: "quotations", newName: "sale_number_counters", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "order_payment_proofs", schema: "quotations", newName: "sale_payment_proofs", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "orders", schema: "quotations", newName: "sales", newSchema: "quotations");
        }
    }
}
```

- [ ] **Step 10: Comprobar que la migración sólo renombra y que el snapshot quedó al día**

```powershell
Select-String -Path $migration.FullName -Pattern "DropTable|CreateTable|DropIndex|CreateIndex|DropForeignKey|AddForeignKey|DropPrimaryKey|AddPrimaryKey|DropColumn|AddColumn"
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado:
- el `Select-String` sin salida;
- el build en `0 Advertencia(s)`, `0 Errores`;
- `No changes have been made to the model since the last migration.`;
- `Superado: 5`, `Con error: 0`.

- [ ] **Step 11: Probar que la prueba de la constante protege de verdad**

Todavía **sin** cambiar `QuotationsUnitOfWork`:

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted"
```

Esperado (RED, y es la evidencia que importa): `Assert.Equal() Failure: Values differ` · `Expected: UnprocessableEntity` · `Actual: InternalServerError`. El índice ya se llama `IX_orders_quotation` y la constante todavía dice `IX_sales_quotation`: la traducción deja de funcionar sin que nada más falle, exactamente el riesgo del spec. Pega la salida.

- [ ] **Step 12: Todo lo que nombra la base en texto**

`QuotationsUnitOfWork.cs:15-21` queda:

```csharp
    // Order.QuotationId es 1:1 (IX_orders_quotation, único). En el camino normal no se alcanza:
    // convertir deja la cotización en Converted y EnsureConvertibleToOrder rechaza una segunda
    // conversión por estado. Queda de red para dos conversiones simultáneas que lean la cotización
    // antes de que cualquiera guarde, y para las convertidas antes de que existiera Converted, que
    // siguen en Sent (no hubo backfill). Sin traducir, saldría como 500 con el nombre de la
    // constraint adentro. Cambia junto con la migración que renombra el índice: la prueba es
    // OrderApiTests.ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted.
    private const string OrderQuotationIndex = "IX_orders_quotation";
```

y en `:59`, `SaleQuotationIndex,` pasa a `OrderQuotationIndex,`. El mensaje de `:64` cambia en Task 3.

`OrderNumberGenerator.cs`: en `:14`, `INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)` pasa a `INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)`; en `:23`, `UPDATE quotations.sale_number_counters` pasa a `UPDATE quotations.order_number_counters`; y el mensaje de `:33` pasa a `$"The order number counter for tenant '{tenantId}' year {year} could not be read back."`.

`ExportLoadSeeder.cs:338-380`: el prefijo `'VEN-'` queda hasta Task 5.

```csharp
    // Convertido un día después del envío, dentro de la vigencia. VEN-{año UTC de la conversión}-{n}.
    // PaymentPending porque cualquier otro estado de pago exige comprobantes en Storage.
    private const string OrdersSql = """
        INSERT INTO quotations.orders (
            id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        SELECT gen_random_uuid(), @tenant,
               'VEN-' || numbered.year || '-' || lpad(numbered.sequence::text, greatest(4, length(numbered.sequence::text)), '0'),
               numbered.quotation_id, 'Pending', 'PaymentPending', NULL, numbered.converted_at, @advisor,
               NULL, NULL, NULL, numbered.converted_at, numbered.converted_at, 1
        FROM (
            SELECT converted.quotation_id,
                   converted.converted_at,
                   extract(year FROM converted.converted_at AT TIME ZONE 'UTC')::int AS year,
                   row_number() OVER (
                       PARTITION BY extract(year FROM converted.converted_at AT TIME ZONE 'UTC')
                       ORDER BY converted.converted_at, converted.n) AS sequence
            FROM (
                SELECT id AS quotation_id, n, least(sent_at + interval '1 day', @now) AS converted_at
                FROM load_quotations
                WHERE converted
            ) AS converted
        ) AS numbered
        """;
```

```csharp
    private const string OrderCountersSql = """
        INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
        SELECT @tenant, extract(year FROM converted_at AT TIME ZONE 'UTC')::int, count(*) + 1
        FROM quotations.orders
        WHERE tenant_id = @tenant
        GROUP BY 2
        ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = greatest(order_number_counters.next_value, EXCLUDED.next_value)
        """;
```

`ops/export-load-cleanup.sql`:

| Línea | Hoy | Queda |
| --- | --- | --- |
| `:27` | `-- sales -> quotations es RESTRICT: primero las ventas. Sus comprobantes caen en cascada.` | `-- orders -> quotations es RESTRICT: primero los pedidos. Sus comprobantes caen en cascada.` |
| `:28` | `DELETE FROM quotations.sales WHERE …` | `DELETE FROM quotations.orders WHERE …` |
| `:32` | `DELETE FROM quotations.sale_number_counters WHERE …` | `DELETE FROM quotations.order_number_counters WHERE …` |

Los comentarios: en `OrderRepository.cs:124`, `(converted_at, sale_number)` pasa a `(converted_at, order_number)`; en `QuotationUserReferenceProbe.cs:19`, `sale_payment_proofs.uploaded_by` pasa a `order_payment_proofs.uploaded_by`.

- [ ] **Step 13: GREEN**

Con Docker corriendo (decenas de minutos):

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build
```

Esperado:
- pasan `OrdersMigrationTests` (2), `ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted`, `ExportLoadSeedTests` y el resto de `OrderApiTests`, `OrderListApiTests`, `OrderExportApiTests` y Reporting;
- ninguna falla fuera del baseline, comparando con el mismo `Where-Object { $expected -notcontains $_ }` de Task 1, Step 10.

Y que no quedó ningún nombre de base viejo fuera de las migraciones y de las pruebas de migración:

```powershell
rg -n "\bsales\b|sale_number|sale_id|sale_payment_proofs|IX_sales|IX_sale_|PK_sale|FK_sale" src tests ops --glob '!**/Migrations/**' --glob '!**/OrdersMigrationTests.cs'
```

Esperado: ninguna línea que nombre la base. Si queda alguna, es prosa en español ("ventas") de un comentario ajeno a la base, que cambia en Task 6.

- [ ] **Step 14: Formato y commit**

```powershell
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
dotnet format Backend.slnx --verify-no-changes --include $changed
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
$migrations = "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations"
git add "$migrations/QuotationsDbContextModelSnapshot.cs" (Get-ChildItem $migrations -Filter "*_RenameToOrders*.cs").FullName `
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs `
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs `
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderNumberGenerator.cs `
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs `
  src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs `
  src/Bootstrapper/Seeding/ExportLoadSeeder.cs ops/export-load-cleanup.sql `
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs `
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs `
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs `
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
git status --short
git commit -m "refactor(quotations): renombrar tablas, columnas e índices de ventas a pedidos" -m "Migración RenameToOrders escrita a mano (RenameTable/RenameColumn/RenameIndex y RENAME CONSTRAINT): la generada hacía DropTable + CreateTable. En el mismo commit, la constante del índice que traduce already_converted, el SQL del generador de números, la carga sintética y el script de limpieza."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera de lo agregado, y el `Select-String` sin salida.

---

### Task 3: Contrato HTTP

Rutas (incluido el `Location` del `201` y las dos de reportes), tags de OpenAPI, el query param `saleNumber` → `orderNumber` (listado y export), los campos JSON de la lista del spec y los 17 códigos de error, con sus mensajes. Corte duro: las rutas viejas dejan de existir (D3).

**Files:**
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderContractApiTests.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs:16-21,31,39,47-48,102,109,130,135,143,149,199`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:61,83,118`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:323,372-375`
- Modify: `src/Modules/Reporting/Modules.Reporting.Api/ReportingEndpoints.cs:20,27`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs:16-17` y `ReportSummaries.cs:18`
- Modify (códigos y mensajes): `Modules.Quotations.Application/OrderNotFound.cs:10,15`, `OrderListing.cs:24-25,38-39`, `OrderPaymentProofResolver.cs:32,39,46,53`, `ExportOrders.cs:99-100,110`; `Modules.Quotations.Domain/Order.cs:118-119,163-164,170,178-179,205,221-222,228-229,243-244`, `OrderPaymentProof.cs:61,68,84`, `Quotation.cs:594,610,617,626,638,645,656`; `Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs:64`
- Test (modificar): la tabla del Step 2.

**Interfaces:**
- Consumes: de Task 1, `OrderResponse`, `OrderListItemResponse`, `OrderDetailResponse`, `OrdersPageResponse`, `ConvertQuotationToOrderRequest`, `AddOrderPaymentProofsRequest`, `OrderPaymentProofRequest`, `OrdersReportItemDto`, `OrdersReportSummaryDto`; de Task 2, el esquema renombrado.
- Produces (el plan del frontend depende de esto tal cual), todo bajo `/api/v1/tenants/{tenantId:guid}`:
  - rutas `GET /orders`, `GET /orders/{orderId}`, `POST /orders/export`, `GET|POST /quotations/{quotationId}/order`, `POST /quotations/{quotationId}/order/approve`, `POST /quotations/{quotationId}/order/proofs`, `GET /reports/orders`, `GET /reports/orders/summary`;
  - `Location: /api/v1/tenants/{tenantId}/quotations/{quotationId}/order` en el `201` de la conversión;
  - tag `"Orders"`;
  - query param `orderNumber` en el listado y en el export;
  - campos JSON `orderNumber` (pedido y fila), `order` (detalle, junto a `quotation`), `orderId`/`orderStatus` (fila de cotización), `canBeConvertedToOrder` (cotización), `orderId`/`orderNumber` (fila del reporte) y `orderCount` (resumen del reporte);
  - códigos `order.order.{not_found,not_pending,payment_proof_required,number_required,number_too_long,notes_too_long,status_invalid,payment_status_invalid}`, `order.payment_proof.{not_found,file_required,amount_invalid,file_not_found,file_not_available,file_type_not_allowed,file_too_large}` y `order.export.{empty,pending_limit}`.

- [ ] **Step 1: Prueba del contrato tal como viaja (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderContractApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El contrato de pedidos tal como viaja (spec 2026-09-14): rutas, nombres de campo y el header
/// Location. Lee el JSON crudo a propósito: el resto de las pruebas deserializa con los mismos
/// records de producción, así que renombrar una propiedad las deja en verde aunque el frontend deje
/// de encontrar el campo.
/// </summary>
public sealed class OrderContractApiTests
{
    [Fact]
    public async Task TheOrderTravelsWithItsNewRoutesAndFieldNames()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        using (var before = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}/{quotation.Id}"))
        {
            Assert.True(before.RootElement.GetProperty("canBeConvertedToOrder").GetBoolean());
            Assert.False(before.RootElement.TryGetProperty("canBeConvertedToSale", out _));
        }

        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, converted.StatusCode);
        Assert.Equal(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/order",
            converted.Headers.Location?.OriginalString);
        using var created = JsonDocument.Parse(
            await converted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var orderId = created.RootElement.GetProperty("id").GetGuid();
        var orderNumber = created.RootElement.GetProperty("orderNumber").GetString();
        Assert.False(created.RootElement.TryGetProperty("saleNumber", out _));

        using (var byQuotation = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}/{quotation.Id}/order"))
        {
            Assert.Equal(orderNumber, byQuotation.RootElement.GetProperty("orderNumber").GetString());
        }

        using (var list = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/orders?orderNumber={orderNumber}"))
        {
            var row = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(orderNumber, row.GetProperty("orderNumber").GetString());
        }

        using (var detail = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/orders/{orderId}"))
        {
            Assert.False(detail.RootElement.TryGetProperty("sale", out _));
            Assert.Equal(orderNumber, detail.RootElement.GetProperty("order").GetProperty("orderNumber").GetString());
            var composed = detail.RootElement.GetProperty("quotation");
            Assert.Equal(quotation.Id, composed.GetProperty("id").GetGuid());
            Assert.False(composed.GetProperty("canBeConvertedToOrder").GetBoolean());
        }

        using (var quotations = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}?status=Converted"))
        {
            var row = Assert.Single(quotations.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(orderId, row.GetProperty("orderId").GetGuid());
            Assert.Equal("Pending", row.GetProperty("orderStatus").GetString());
            Assert.False(row.TryGetProperty("saleId", out _));
        }
    }

    [Fact]
    public async Task ProofsAndApprovalHangFromTheOrderSubresource()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var proofs = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order/proofs",
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        var approve = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order/approve",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, proofs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var approved = JsonDocument.Parse(
            await approve.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Approved", approved.RootElement.GetProperty("status").GetString());
    }

    // El export recibe el mismo filtro que el listado: con orderNumber sin coincidencias no hay
    // filas, y el 422 lleva el código del área de pedidos (antes sale.export.empty).
    [Fact]
    public async Task ExportFiltersByOrderNumberAndAnswersWithTheOrderCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/orders/export?convertedFrom={today.AddDays(-1):yyyy-MM-dd}&convertedTo={today:yyyy-MM-dd}&orderNumber=NO-EXISTE",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.export.empty", body, StringComparison.Ordinal);
    }

    // Corte duro (D3): ninguna ruta vieja queda como alias.
    [Fact]
    public async Task TheSalesRoutesNoLongerExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var list = await client.GetAsync($"/api/v1/tenants/{tenantId}/sales", TestContext.Current.CancellationToken);
        var byQuotation = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{Guid.CreateVersion7()}/sale", TestContext.Current.CancellationToken);
        var report = await client.GetAsync($"/api/v1/tenants/{tenantId}/reports/sales", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byQuotation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Llevar las pruebas existentes al contrato nuevo (RED)**

Las líneas son las de hoy. Task 1 renombró archivos y variables (`sale` → `order`, `firstSale` → `firstOrder`) pero no agregó ni quitó líneas.

| Archivo | Línea | Hoy | Queda |
| --- | --- | --- | --- |
| `Modules.Quotations.IntegrationTests/OrderApiTests.cs` | `:13` | `…/{quotationId}/sale"` | `…/{quotationId}/order"` |
| | `:75` | `order.SaleNumber` | `order.OrderNumber` (el `"VEN-"` queda hasta Task 5) |
| | `:87` | `fetchedQuotation.CanBeConvertedToSale` | `fetchedQuotation.CanBeConvertedToOrder` |
| | `:311` | `created.SaleNumber, fetched.SaleNumber` | `created.OrderNumber, fetched.OrderNumber` |
| | `:368,373` | `CanBeConvertedToSale` (comentario y `fetched.`) | `CanBeConvertedToOrder` |
| | `:405` | `afterResend.CanBeConvertedToSale` | `afterResend.CanBeConvertedToOrder` |
| `Modules.Quotations.IntegrationTests/OrderListApiTests.cs` | `:18` | `…/{tenantId}/sales"` | `…/{tenantId}/orders"` |
| | `:38` | `row.SaleNumber` | `row.OrderNumber` |
| | `:93` | `?saleNumber={firstOrder.SaleNumber}` | `?orderNumber={firstOrder.OrderNumber}` |
| | `:97` | `firstOrder.SaleNumber, …SaleNumber` | `firstOrder.OrderNumber, …OrderNumber` |
| | `:137` | `…/sale/approve"` | `…/order/approve"` |
| | `:167` | `"sale.sale.status_invalid"` | `"order.order.status_invalid"` |
| | `:204-206` | `detail.Sale.Id`, `order.SaleNumber, detail.Sale.SaleNumber`, `detail.Sale.Status` | `detail.Order.Id`, `order.OrderNumber, detail.Order.OrderNumber`, `detail.Order.Status` |
| | `:238` | `…/{quotationId}/sale"` | `…/{quotationId}/order"` |
| `Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` | `:18` | `…/{tenantId}/sales"` | `…/{tenantId}/orders"` |
| | `:71` | `"sale.export.empty"` | `"order.export.empty"` |
| | `:97` | `"sale.export.pending_limit"` | `"order.export.pending_limit"` |
| | `:165` | `item.SaleNumber` | `item.OrderNumber` |
| | `:322` | `…/{quotation.Id}/sale"` | `…/{quotation.Id}/order"` |
| `Modules.Quotations.IntegrationTests/QuotationExpirationApiTests.cs` | `:118` | `…/{quotation.Id}/sale"` | `…/{quotation.Id}/order"` |
| `Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs` | `:172` | `…/{ExportLoadSeeder.TenantId}/sales"` | `…/{ExportLoadSeeder.TenantId}/orders"` |
| `Modules.Quotations.UnitTests/ExportOrdersHandlerTests.cs` | `:83`, `:84` | `"sale.sale.status_invalid"`, `"sale.sale.payment_status_invalid"` | `"order.order.status_invalid"`, `"order.order.payment_status_invalid"` |
| | `:105`, `:119`, `:138` | `"sale.export.empty"`, `"sale.export.empty"`, `"sale.export.pending_limit"` | `"order.export.empty"`, `"order.export.empty"`, `"order.export.pending_limit"` |
| `Modules.Quotations.UnitTests/ListOrdersHandlerTests.cs` | `:112`, `:134` | `"sale.sale.status_invalid"`, `"sale.sale.payment_status_invalid"` | `"order.order.status_invalid"`, `"order.order.payment_status_invalid"` |
| `Modules.Quotations.UnitTests/OrderTests.cs` | `:58` | `"sale.sale.number_required"` | `"order.order.number_required"` |
| | `:68`, `:186` | `"sale.sale.payment_proof_required"` | `"order.order.payment_proof_required"` |
| | `:86` | `"sale.payment_proof.file_required"` | `"order.payment_proof.file_required"` |
| | `:97`, `:295` | `"sale.payment_proof.amount_invalid"` | `"order.payment_proof.amount_invalid"` |
| | `:205`, `:335` | `"sale.sale.not_pending"` | `"order.order.not_pending"` |
| | `:275` | `"sale.payment_proof.not_found"` | `"order.payment_proof.not_found"` |
| `Modules.Reporting.UnitTests/OrdersReportSummaryHandlerTests.cs` | `:103` | `summary.SaleCount` | `summary.OrderCount` |
| `Modules.Reporting.UnitTests/CustomerReportSummaryHandlerTests.cs` | `:108` | `total / saleCount` (comentario) | `total / orderCount` |
| `Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs` | `:31,70,91,110,124,147,150` | `{ReportsUrl(…)}/sales…` | `{ReportsUrl(…)}/orders…` |
| | `:42-43` | `item.SaleId`, `order.SaleNumber, item.SaleNumber` | `item.OrderId`, `order.OrderNumber, item.OrderNumber` |
| | `:174` | `"sales"` | `"orders"` |
| `Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs` | `:8` | `GET /reports/sales/summary` (comentario) | `GET /reports/orders/summary` |
| | `:32,83,119,143,163,180` | `…/sales/summary…` | `…/orders/summary…` |
| | `:40,90,126` | `summary.SaleCount` | `summary.OrderCount` |
| `Modules.Reporting.IntegrationTests/ReportingResponses.cs` | `:13-14` | `Guid SaleId, string SaleNumber,` | `Guid OrderId, string OrderNumber,` |
| | `:81` | `int SaleCount,` | `int OrderCount,` |
| `Modules.Reporting.IntegrationTests/ReportingApiHarness.cs` | `:365` | `…/quotations/{quotation.Id}/sale"` | `…/quotations/{quotation.Id}/order"` |
| `Modules.Platform.UnitTests/RequestFailureTests.cs` | `:24` | `…/reports/sales"` | `…/reports/orders"` (ejemplo de ruta; el módulo esperado sigue siendo `"reports"`) |

`ReportingResponses.cs` es un record de prueba: con `OrderId`/`OrderNumber`/`OrderCount` y el backend todavía emitiendo `saleId`/`saleNumber`/`saleCount`, las propiedades quedan en su valor por defecto. Eso es un RED de verdad sobre el nombre del campo, no sólo de compilación.

- [ ] **Step 3: Verlas fallar**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore | Select-String -Pattern "error CS" | Select-Object -First 8
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportApiTests"
```

Esperado (RED):
- el build falla en las pruebas con `error CS1061: 'OrderResponse' no contiene una definición para 'OrderNumber'` (también sobre `OrderListItemResponse`, `OrderDetailResponse.Order`, `QuotationResponse.CanBeConvertedToOrder` y `OrdersReportSummaryDto.OrderCount`). Pega las líneas.
- Reporting.IntegrationTests compila, porque sus records son locales, y falla con `Assert.Equal() Failure: Values differ` · `Expected: OK` · `Actual: NotFound`.

- [ ] **Step 4: Rutas, tags, parámetros y `Location`**

`OrderEndpoints.cs`:

| Línea | Hoy | Queda |
| --- | --- | --- |
| `:20` | `.MapGroup("/api/v1/tenants/{tenantId:guid}/sales")` | `.MapGroup("/api/v1/tenants/{tenantId:guid}/orders")` |
| `:21`, `:48` | `.WithTags("Sales");` | `.WithTags("Orders");` |
| `:31` | `collection.MapGet("/{saleId:guid}", GetOrderByIdAsync)` | `collection.MapGet("/{orderId:guid}", GetOrderByIdAsync)` |
| `:39` | comentario `"export" no choca con "/{saleId:guid}"` | `"export" no choca con "/{orderId:guid}"` |
| `:47` | `…/quotations/{quotationId:guid}/sale")` | `…/quotations/{quotationId:guid}/order")` |
| `:102`, `:130` | `string? saleNumber = null` | `string? orderNumber = null` |
| `:109`, `:135` | `clientCuc, saleNumber` | `clientCuc, orderNumber` |
| `:143` | `Guid saleId,` | `Guid orderId,` |
| `:149` | `new GetOrderByIdQuery(tenantId, saleId)` | `new GetOrderByIdQuery(tenantId, orderId)` |
| `:199` | `$"/api/v1/tenants/{tenantId}/quotations/{quotationId}/sale",` | `$"/api/v1/tenants/{tenantId}/quotations/{quotationId}/order",` |

En la prosa de `:16-18`, "las ventas del tenant" pasa a "los pedidos del tenant". `ReportingEndpoints.cs`: en `:20`, `group.MapGet("/sales", ListOrdersAsync)` pasa a `group.MapGet("/orders", ListOrdersAsync)`; en `:27`, `group.MapGet("/sales/summary", GetOrdersSummaryAsync)` pasa a `group.MapGet("/orders/summary", GetOrdersSummaryAsync)`.

- [ ] **Step 5: Campos JSON**

| Archivo | Línea | Hoy | Queda |
| --- | --- | --- | --- |
| `OrdersDtos.cs` (`OrderResponse`) | `:61` | `string SaleNumber,` | `string OrderNumber,` |
| `OrdersDtos.cs` (`OrderListItemResponse`) | `:83` | `string SaleNumber,` | `string OrderNumber,` |
| `OrdersDtos.cs` (`OrderDetailResponse`) | `:118` | `OrderResponse Sale,` | `OrderResponse Order,` |
| `QuotationsDtos.cs` (`QuotationResponse`) | `:323` | `bool CanBeConvertedToSale,` | `bool CanBeConvertedToOrder,` |
| `QuotationsDtos.cs` (`QuotationListItemResponse`) | `:372-375` | `/// <summary>La venta que salio de esta cotizacion. …` / `Guid? SaleId,` / `/// … <c>null</c> sin venta.` / `string? SaleStatus);` | `/// <summary>El pedido que salió de esta cotización. <c>null</c> es "sin convertir".</summary>` / `Guid? OrderId,` / `/// <summary><c>Pending</c> o <c>Approved</c>; <c>null</c> sin pedido.</summary>` / `string? OrderStatus);` |
| `ReportingDtos.cs` (`OrdersReportItemDto`) | `:16-17` | `Guid SaleId,` / `string SaleNumber,` | `Guid OrderId,` / `string OrderNumber,` |
| `ReportSummaries.cs` (`OrdersReportSummaryDto`) | `:18` | `int SaleCount,` | `int OrderCount,` |

Los constructores posicionales (`OrderEndpoints.ToResponse`, `ToListItemResponse`, `QuotationEndpoints.cs:411-412`, `OrdersReportSource`, `GetOrdersReportSummary.cs:37`) no cambian. Si alguno usa argumentos con nombre, el compilador marca cuál.

- [ ] **Step 6: Los 17 códigos y sus mensajes**

| Archivo:línea | Código hoy → queda | Mensaje nuevo (si decía "sale") |
| --- | --- | --- |
| `OrderNotFound.cs:10` | `sale.sale.not_found` → `order.order.not_found` | `$"Order for quotation '{quotationId}' was not found."` |
| `OrderNotFound.cs:15` | `sale.sale.not_found` → `order.order.not_found` | `$"Order '{orderId}' was not found."` |
| `Order.cs:118-119` | `sale.sale.not_pending` → `order.order.not_pending` | `"Only a pending order can be approved."` |
| `Order.cs:163-164` | `sale.sale.not_pending` → `order.order.not_pending` | `"Payment proofs can only be added to a pending order."` |
| `Order.cs:170`, `:205` | `sale.sale.payment_proof_required` → `order.order.payment_proof_required` | — |
| `Order.cs:178-179` | `sale.payment_proof.not_found` → `order.payment_proof.not_found` | `$"Payment proof '{update.ProofId}' was not found on this order."` |
| `Order.cs:221-222` | `sale.sale.number_required` → `order.order.number_required` | `"The order number is required."` |
| `Order.cs:228-229` | `sale.sale.number_too_long` → `order.order.number_too_long` | `$"The order number cannot exceed {OrderNumberMaxLength} characters."` |
| `Order.cs:243-244` | `sale.sale.notes_too_long` → `order.order.notes_too_long` | `$"The order notes cannot exceed {NotesMaxLength} characters."` |
| `OrderListing.cs:24-25` | `sale.sale.status_invalid` → `order.order.status_invalid` | `$"'{status}' is not a valid order status."` |
| `OrderListing.cs:38-39` | `sale.sale.payment_status_invalid` → `order.order.payment_status_invalid` | `$"'{paymentStatus}' is not a valid order payment status."` |
| `OrderPaymentProof.cs:61` | `sale.payment_proof.file_required` → `order.payment_proof.file_required` | — |
| `OrderPaymentProof.cs:68`, `:84` | `sale.payment_proof.amount_invalid` → `order.payment_proof.amount_invalid` | — |
| `OrderPaymentProofResolver.cs:32` | `sale.payment_proof.file_not_found` → `order.payment_proof.file_not_found` | — |
| `OrderPaymentProofResolver.cs:39` | `sale.payment_proof.file_not_available` → `order.payment_proof.file_not_available` | — |
| `OrderPaymentProofResolver.cs:46` | `sale.payment_proof.file_type_not_allowed` → `order.payment_proof.file_type_not_allowed` | — |
| `OrderPaymentProofResolver.cs:53` | `sale.payment_proof.file_too_large` → `order.payment_proof.file_too_large` | — |
| `ExportOrders.cs:99-100` | `sale.export.empty` → `order.export.empty` | `"There are no orders matching the export filters."` |
| `ExportOrders.cs:110` | `sale.export.pending_limit` → `order.export.pending_limit` | — |

`number_required`, `number_too_long` y `payment_proof.not_found` se renombran sin agregarles mensaje en el frontend (D10).

Los `quotation.quotation.*` no cambian de código (spec, «Códigos de error»), sólo el mensaje:
- en `Quotation.cs:594,610,617,626`, "…converted to a sale." pasa a "…converted to an order.";
- en `Quotation.cs:638,645,656`, "…before converting to a sale." pasa a "…before converting to an order.";
- en `QuotationsUnitOfWork.cs:64`, `"This quotation was already converted to a sale."` pasa a `"This quotation was already converted to an order."`.

- [ ] **Step 7: GREEN**

Con Docker corriendo (decenas de minutos):

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build
dotnet test tests/Modules/Platform/Modules.Platform.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build
```

Esperado: build en `0 Advertencia(s)`/`0 Errores`, pasan las cuatro de `OrderContractApiTests` y las del Step 2, y ninguna falla fuera del baseline (mismo filtro que Task 1, Step 10).

- [ ] **Step 8: Nada del contrato viejo quedó**

```powershell
rg -n "/sales\b|/sale\b|/sale/|\{saleId|\bsaleNumber\b|""sale\.|WithTags\(""Sales""\)|\bSaleNumber\b|\bSaleId\b|\bSaleStatus\b|CanBeConvertedToSale|\bSaleCount\b|to a sale|pending sale|sale status|sale number|sale notes|this sale|no sales" src tests --glob '*.cs' --glob '!**/Migrations/**' --glob '!**/OrdersMigrationTests.cs' --glob '!**/OrderContractApiTests.cs'
```

Esperado: sólo lo que es de Task 5:
- `ExportOrders.cs:33` (`string? SaleNumber);` de `OrdersExportFilters`);
- `OrdersExportProcessor.cs:67` (`filters.SaleNumber`) y `:83` (`"Empty: no sales matched…"`);
- `ExportStatusLabels.cs:32` (`"The sale status has no export label."`).

- [ ] **Step 9: Formato y commit**

```powershell
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
dotnet format Backend.slnx --verify-no-changes --include $changed
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git add $changed
git status --short
git commit -m "feat(quotations)!: contrato HTTP de pedidos en rutas, campos y códigos" -m "Rutas /orders, /quotations/{id}/order y /reports/orders; query param orderNumber; campos orderNumber, order, orderId, orderStatus, canBeConvertedToOrder y orderCount; los 17 códigos sale.* pasan a order.*." -m "BREAKING CHANGE: /sales, /quotations/{id}/sale y /reports/sales dejan de existir, sin alias (D3)."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera de lo agregado (el `$changed` incluye `OrderContractApiTests.cs`, que es nuevo; si `git diff HEAD` no lo lista, agrégalo por ruta), y el `Select-String` sin salida.

---

### Task 4: Permisos

Valores y nombres de las tres constantes, el catálogo que ve la SPA en la pantalla de roles (con la concordancia corregida), los roles de fábrica, las políticas y una migración de datos de `Authorization` para los roles custom. Sin la migración, un rol custom pierde el acceso sin error (spec, «Riesgos»). `RoleCatalog.CatalogVersion` es un hash y cambia solo.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs:5-6`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingPermissions.cs:7-8,21`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:544-549,589-601,612-617,760-777,1003-1016`
- Modify (usos de las constantes): `Modules.Quotations.Api/OrderEndpoints.cs:24,32,41,51,62,69,81`; `Modules.Reporting.Api/ReportingEndpoints.cs:21,28`; `Modules.Quotations.Application/{ConvertQuotationToOrder.cs:61, ApproveOrder.cs, GetOrder.cs, GetOrderById.cs, ExportOrders.cs:74, ListOrders.cs, AddOrderPaymentProofs.cs, ListQuotations.cs:168}`; `Modules.Reporting.Application/{ListOrdersReport.cs, GetOrdersReportSummary.cs}`
- Create: `src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/Migrations/<timestamp>_RenamePermissionsToOrders.cs` (+ `.Designer.cs`; el snapshot no cambia)
- Create: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationPermissionsMigrationTests.cs`
- Test (modificar): `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs`; los harnesses `Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:80-81` y `Modules.Reporting.IntegrationTests/ReportingApiHarness.cs:46-47,60`; `Modules.Quotations.IntegrationTests/OrderApiTests.cs:341`, `OrderExportApiTests.cs:121`; `Modules.Quotations.UnitTests/ExportOrdersHandlerTests.cs:42`, `ListQuotationsHandlerTests.cs:191`; `Modules.Reporting.UnitTests/CustomerReportSummaryHandlerTests.cs:47`, `OrdersReportSummaryHandlerTests.cs:26,59,74,96,120,150,172`, `QuotationsReportSummaryHandlerTests.cs:48`, `OrdersReportHandlerTests.cs:25,54,72,90`, `PriceChangeReportSummaryHandlerTests.cs:45`

**Interfaces:**
- Consumes: de Task 1, `OrdersPermissions` (con `SaleRead`/`SaleManage` todavía) y `ReportingPermissions.SalesRead`.
- Produces:
  - `OrdersPermissions.OrderRead = "quotations.order.read"`, `OrdersPermissions.OrderManage = "quotations.order.manage"` y `ReportingPermissions.OrdersRead = "reporting.orders.read"`, que el frontend lee en `report-permissions.ts` y en `/authorization/me`;
  - las entradas del catálogo con los textos del spec;
  - la migración cuyo id termina en `_RenamePermissionsToOrders`.

- [ ] **Step 1: El catálogo nombra los permisos de pedidos (RED)**

En `AuthorizationCatalogApiTests.cs`, agrega a la clase estos records y esta prueba (los `using` que necesita ya están):

```csharp
    private sealed record CatalogPayload(
        string CatalogVersion,
        IReadOnlyCollection<CatalogRolePayload> Roles,
        IReadOnlyCollection<CatalogPermissionPayload> Permissions);

    private sealed record CatalogRolePayload(string Role, IReadOnlyCollection<string> Permissions);

    private sealed record CatalogPermissionPayload(
        string Permission, string DisplayName, string Description, string Category, string RiskLevel);

    /// <summary>
    /// Lo que la pantalla de roles muestra de los permisos de pedidos (spec 2026-09-14): códigos
    /// nuevos, textos en masculino y con tilde, y los mismos permisos efectivos en los tres roles
    /// de fábrica que antes tenían los de ventas.
    /// </summary>
    [Fact]
    public async Task TheCatalogNamesTheOrderPermissions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var catalog = await client.GetFromJsonAsync<CatalogPayload>(
            $"/api/v1/tenants/{TenantId}/authorization/catalog", TestContext.Current.CancellationToken);

        Assert.NotNull(catalog);
        CatalogPermissionPayload[] expected =
        [
            new("quotations.order.manage", "Gestionar pedidos",
                "Permite convertir una cotización enviada en pedido, con sus comprobantes de pago.", "Quotations", "medium"),
            new("quotations.order.read", "Leer pedidos",
                "Permite consultar el pedido convertido de una cotización.", "Quotations", "low"),
            new("reporting.orders.read", "Reporte de pedidos",
                "Permite consultar y exportar el reporte de pedidos convertidos del tenant.", "Reporting", "low"),
        ];
        Assert.Equal(
            expected,
            catalog.Permissions
                .Where(permission => permission.Permission.Contains("order", StringComparison.Ordinal))
                .OrderBy(permission => permission.Permission, StringComparer.Ordinal));
        Assert.DoesNotContain(
            catalog.Permissions, permission => permission.Permission.Contains("sale", StringComparison.Ordinal));
        Assert.Equal(
            ["quotations.order.manage", "quotations.order.read", "reporting.orders.read"],
            OrderPermissionsOf(catalog, "admin"));
        Assert.Equal(
            ["quotations.order.manage", "quotations.order.read", "reporting.orders.read"],
            OrderPermissionsOf(catalog, "advisor"));
        Assert.Equal(["quotations.order.read"], OrderPermissionsOf(catalog, "billing"));
    }

    private static string[] OrderPermissionsOf(CatalogPayload catalog, string role) =>
    [
        .. catalog.Roles.Single(item => item.Role == role).Permissions
            .Where(permission => permission.Contains("order", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];
```

- [ ] **Step 2: Un rol custom con los códigos viejos termina con los nuevos (RED)**

Crea `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationPermissionsMigrationTests.cs`. Vive acá porque `RoleApiTests` ya prueba los roles en este proyecto, que referencia `Api.csproj` y con él `AuthorizationDbContext`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Authorization.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La migración de datos de los permisos de pedidos (spec 2026-09-14). Los roles de fábrica viven
/// en código; los custom guardan sus códigos en <c>authorization.roles.permissions</c> (text[]), y
/// sin esto perderían el acceso sin error. Migra sólo Authorization, hasta una migración puntual,
/// con <see cref="IMigrator"/>: el host de pruebas migra todo a la última al arrancar.
/// </summary>
public sealed class AuthorizationPermissionsMigrationTests
{
    private const string InitialAuthorization = "20260828234451_InitialAuthorization";
    private const string RoleId = "01900000-0000-7000-8000-00000000d001";

    private const string LegacyRoleSql = $"""
        INSERT INTO "authorization".roles (
            id, tenant_id, key, display_name, description, version, created_at, updated_at, permissions)
        VALUES (
            '{RoleId}', '01900000-0000-7000-8000-00000000d002', 'facturacion-junior', 'Facturación junior', '',
            1, now(), now(),
            ARRAY['catalog.product.read', 'quotations.sale.read', 'quotations.sale.manage', 'reporting.sales.read']);
        """;

    [Fact]
    public async Task ACustomRoleWithTheOldCodesEndsWithTheOrderCodes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRoleSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_RenamePermissionsToOrders"), TestContext.Current.CancellationToken);

        // array_replace conserva la posición: el orden de la lista no cambia.
        Assert.Equal(
            ["catalog.product.read", "quotations.order.read", "quotations.order.manage", "reporting.orders.read"],
            await PermissionsAsync(connectionString));
    }

    [Fact]
    public async Task RevertingPutsTheOldCodesBack()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyRoleSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_RenamePermissionsToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(InitialAuthorization, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["catalog.product.read", "quotations.sale.read", "quotations.sale.manage", "reporting.sales.read"],
            await PermissionsAsync(connectionString));
    }

    private static AuthorizationDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "authorization"))
            .Options);

    private static string MigrationId(AuthorizationDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> PermissionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"""SELECT permissions FROM "authorization".roles WHERE id = '{RoleId}'""", connection);
        return (string[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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

- [ ] **Step 3: Verlas fallar**

Con Docker corriendo:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --filter "FullyQualifiedName~TheCatalogNamesTheOrderPermissions|FullyQualifiedName~AuthorizationPermissionsMigrationTests"
```

Esperado (RED):
- `TheCatalogNamesTheOrderPermissions` con `Assert.Equal() Failure: Collections differ` (espera tres, llegan cero);
- las dos de `AuthorizationPermissionsMigrationTests` con `System.InvalidOperationException : Sequence contains no matching element`.

- [ ] **Step 4: Constantes, valores, roles, políticas y catálogo**

`OrdersPermissions.cs` queda:

```csharp
namespace Modules.Quotations.Application;

public static class OrdersPermissions
{
    public const string OrderRead = "quotations.order.read";
    public const string OrderManage = "quotations.order.manage";
}
```

`ReportingPermissions.cs`:
- en `:21`, `public const string SalesRead = "reporting.sales.read";` pasa a `public const string OrdersRead = "reporting.orders.read";`;
- en la documentación de `:7-8`, `<c>sales</c> es la excepción y va en plural` pasa a `<c>orders</c> es la excepción y va en plural`;
- en `:11`, "ventas y cotizaciones son el trabajo diario" pasa a "pedidos y cotizaciones son el trabajo diario".

En **todos** los usos de `src/` y `tests/`: `OrdersPermissions.SaleRead` → `OrdersPermissions.OrderRead`, `OrdersPermissions.SaleManage` → `OrdersPermissions.OrderManage` y `ReportingPermissions.SalesRead` → `ReportingPermissions.OrdersRead`. Son los archivos de «Files»; el compilador marca los que falten.

`QepServiceCollectionExtensions.cs`:
- roles: `:544-545` (admin), `:596-597` (asesor) y `:617` (facturación) quedan con `OrdersPermissions.OrderRead` y `OrdersPermissions.OrderManage`; `:549` y `:600` con `ReportingPermissions.OrdersRead`;
- comentarios: en `:589` y `:594`, "convertir en venta" pasa a "convertir en pedido"; en `:613`, "ver clientes, cotizaciones y ventas" es la cita literal del pedido de negocio y **queda**;
- políticas: `:1005-1009` y `:1015-1016` con los nombres nuevos; en `:1003`, "para los dos de Orders -- mismo gotcha."; en `:1013`, "Quotations/Orders.";
- catálogo:

```csharp
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderRead,
            "Leer pedidos",
            "Permite consultar el pedido convertido de una cotización.",
            "Quotations",
            "low"));
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderManage,
            "Gestionar pedidos",
            "Permite convertir una cotización enviada en pedido, con sus comprobantes de pago.",
            "Quotations",
            "medium"));
        services.AddSingleton(new PermissionDefinition(
            ReportingPermissions.OrdersRead,
            "Reporte de pedidos",
            "Permite consultar y exportar el reporte de pedidos convertidos del tenant.",
            "Reporting",
            "low"));
```

Las descripciones llevan tilde ("cotización"). Hoy no la llevan, y el spec las fija así.

- [ ] **Step 5: La migración de `Authorization`**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add RenamePermissionsToOrders --project src/Modules/Authorization/Modules.Authorization.Infrastructure --context AuthorizationDbContext -o Persistence/Migrations
```

Esperado: se crean `<timestamp>_RenamePermissionsToOrders.cs` y `.Designer.cs` con `Up`/`Down` vacíos. El modelo no cambió: los permisos son datos. `AuthorizationDbContextModelSnapshot.cs` no debería cambiar; si `git status` lo muestra modificado, revisa el diff antes de seguir.

Reemplaza el cuerpo de la clase por:

```csharp
    /// <inheritdoc />
    public partial class RenamePermissionsToOrders : Migration
    {
        // Datos, no esquema (spec 2026-09-14, D4). Los roles custom guardan sus códigos en texto, y
        // sin esto perderían el acceso sin error. array_replace conserva la posición en la lista.
        // Los roles de fábrica viven en código (QepServiceCollectionExtensions) y no pasan por acá.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.sale.read',   'quotations.order.read');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.sale.manage', 'quotations.order.manage');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'reporting.sales.read',   'reporting.orders.read');""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'reporting.orders.read',   'reporting.sales.read');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.order.manage', 'quotations.sale.manage');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.order.read',   'quotations.sale.read');""");
        }
    }
```

- [ ] **Step 6: La migración sólo toca datos**

```powershell
$authMigration = Get-ChildItem src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/Migrations -Filter "*_RenamePermissionsToOrders.cs" | Where-Object { $_.Name -notlike "*.Designer.cs" }
Select-String -Path $authMigration.FullName -Pattern "CreateTable|DropTable|AddColumn|DropColumn|AlterColumn"
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Authorization/Modules.Authorization.Infrastructure --context AuthorizationDbContext
```

Esperado: el `Select-String` sin salida, el build limpio y `No changes have been made to the model since the last migration.`

- [ ] **Step 7: GREEN**

Con Docker corriendo (decenas de minutos). Los harnesses de Quotations y Reporting mandan `X-Permissions` con las constantes, así que sus suites prueban que las políticas nuevas resuelven. Si faltara una política, el síntoma sería 500, no 403 (`CLAUDE.md`, «Un permiso nuevo necesita dos mitades»).

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --filter "FullyQualifiedName~AuthorizationCatalogApiTests|FullyQualifiedName~AuthorizationPermissionsMigrationTests|FullyQualifiedName~RoleApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build
```

Esperado: pasan `TheCatalogNamesTheOrderPermissions`, las dos de `AuthorizationPermissionsMigrationTests` y `RoleApiTests`, y ninguna falla fuera del baseline (filtro de Task 1, Step 10).

- [ ] **Step 8: Ningún permiso viejo quedó**

```powershell
rg -n "quotations\.sale\.|reporting\.sales\.|\bSaleRead\b|\bSaleManage\b|\bSalesRead\b" src tests --glob '!**/Migrations/**'
```

Esperado: sólo `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationPermissionsMigrationTests.cs`, que siembra y comprueba los códigos viejos a propósito.

- [ ] **Step 9: Formato y commit**

```powershell
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
dotnet format Backend.slnx --verify-no-changes --include $changed
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git add $changed tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationPermissionsMigrationTests.cs (Get-ChildItem src/Modules/Authorization/Modules.Authorization.Infrastructure/Persistence/Migrations -Filter "*_RenamePermissionsToOrders*.cs").FullName
git status --short
git commit -m "feat(authorization)!: permisos de pedidos y migración de roles custom" -m "quotations.order.read, quotations.order.manage y reporting.orders.read en constantes, roles de fábrica, políticas y catálogo, con los textos en masculino. RenamePermissionsToOrders reescribe los roles custom con array_replace." -m "BREAKING CHANGE: los códigos quotations.sale.* y reporting.sales.read dejan de existir; sin alias (D3)."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera de lo agregado, y el `Select-String` sin salida.

---

### Task 5: Exportación, mensajería y numeración

`ExportJobKind.Sales` → `Orders`, con su migración de datos (hallazgo 5), la clave `OrderNumber` de los filtros guardados, el nombre del tipo en el correo sin alias (D5), el archivo, la hoja y la columna del Excel, la etiqueta `Aprobado`, las tres acciones de auditoría, el prefijo `PED-` y el texto del historial.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/ExportJobKind.cs:8`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportOrders.cs:33,118,127`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs:22,24,34,44,67,83` (y la prosa de `:8,27`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobRunner.cs:131`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ApproveOrder.cs:50` y `AddOrderPaymentProofs.cs:104`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs:7,31-32`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderNumberFormatter.cs:5,11`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs:89`
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportKindText.cs:16`
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:338,345`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_MigrateExportJobsToOrders.cs` (+ `.Designer.cs`)
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs`
- Test (modificar):
  - `Modules.Quotations.UnitTests`: `ExportStatusLabelsTests.cs:27-36`, `QuotationChangeSummaryTests.cs`, `OrdersExportProcessorTests.cs:28,30,95,135`, `ExportJobRunnerTests.cs:57,59,62,135`, `ExportOrdersHandlerTests.cs:157`, `ExportQuotationsHandlerTests.cs:244`;
  - `Modules.Quotations.IntegrationTests`: `OrderExportApiTests.cs:35,151,155,163,164`, `OrderApiTests.cs:75`, `OrderListApiTests.cs:38`, `ExportJobQueueTests.cs:138-140`, `ExportJobWorkerTests.cs:62,66`, `ExportLoadSeedTests.cs:175-180,191`, `OrdersMigrationTests.cs`;
  - Notifications: `Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs:19-34`, `Modules.Notifications.IntegrationTests/QuotationsExportNotificationTests.cs:59`, `Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs:123`;
  - números de fixture `VEN-` → `PED-` (Step 4).

**Interfaces:**
- Consumes: de Task 1, `OrdersExportFilters`, `OrdersExportProcessor`, `OrderNumberFormatter`, `QuotationChangeSummary.ConvertedToOrder`, `OrderStatus`; de Task 2, los helpers de `OrdersMigrationTests` y la migración `_RenameToOrders`.
- Produces (el plan del frontend y soporte leen esto):
  - `ExportJobKind.Orders`, guardado como `'Orders'` en `export_jobs.kind` y enviado como `"kind": "Orders"` en `quotations.export-ready.v1` / `quotations.export-failed.v1`;
  - `OrdersExportFilters.OrderNumber` (clave `OrderNumber` en `export_jobs.filters`);
  - archivo `pedidos-yyyy-MM-dd-HHmm.xlsx`, hoja `Pedidos`, primera columna `Pedido`;
  - `ExportStatusLabels.For(OrderStatus.Approved) == "Aprobado"`;
  - auditoría `quotation.order.approved`, `quotation.order.payment_proofs_added` y `quotation.order.exported`;
  - números `PED-{año}-{secuencia:D4}`;
  - historial `"Convertida en el pedido {n}."`;
  - la migración cuyo id termina en `_MigrateExportJobsToOrders`.

- [ ] **Step 1: Pruebas unitarias (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs` (`OrderNumberFormatter` es `internal`; `Modules.Quotations.Application.csproj:7` ya da `InternalsVisibleTo` a este proyecto):

```csharp
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El número de pedido (spec 2026-09-14, D2): prefijo PED- con el mismo contador que emitía los
/// VEN-, y al menos cuatro dígitos. Los VEN- ya emitidos no se reescriben.
/// </summary>
public sealed class OrderNumberFormatterTests
{
    [Theory]
    [InlineData(2026, 1L, "PED-2026-0001")]
    [InlineData(2026, 42L, "PED-2026-0042")]
    [InlineData(2027, 12345L, "PED-2027-12345")]
    public void FormatsWithThePedPrefix(int year, long sequence, string expected) =>
        Assert.Equal(expected, OrderNumberFormatter.Format(year, sequence));
}
```

En `QuotationChangeSummaryTests.cs`, agrega a la clase:

```csharp
    // "Pedido" es masculino (spec 2026-09-14, D7). El sujeto sigue siendo la cotización, por eso
    // "Convertida".
    [Fact]
    public void ConvertingNamesTheOrderInMasculine() =>
        Assert.Equal(
            "Convertida en el pedido PED-2026-0001.",
            QuotationChangeSummary.ConvertedToOrder("PED-2026-0001"));
```

En `ExportStatusLabelsTests.cs:27-36`:

```csharp
    // order-list.ts (frontend), las etiquetas de estado del pedido
    [Fact]
    public void EveryOrderStatusHasTheLabelOfTheOrdersTable() =>
        AssertLabels(
            new Dictionary<OrderStatus, string>
            {
                [OrderStatus.Pending] = "Pendiente",
                [OrderStatus.Approved] = "Aprobado",
            },
            ExportStatusLabels.For);
```

El nombre del método ya lo cambió Task 1 con la regla mecánica. Queda con este nombre, y el comentario de `:38` pasa a `// order-list.ts (frontend), las etiquetas de estado del pago`.

`OrdersExportProcessorTests.cs`:
- `:28`: `Assert.Equal("Pedidos", writer.SheetName);`
- `:30`: `["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"],`
- `:95`: `Assert.Equal("pedidos-2026-09-12-1530.xlsx", result.FileName);`
- `:135`: `ExportJobKind.Orders,`

`ExportJobRunnerTests.cs`:
- `:57`, `:59` y `:135`: `ExportJobKind.Sales` pasa a `ExportJobKind.Orders`;
- `:62`: `Assert.Equal("quotation.order.exported", Assert.Single(harness.Audit.Entries).Action);`

`ExportOrdersHandlerTests.cs:157` y `ExportQuotationsHandlerTests.cs:244`: `ExportJobKind.Sales` pasa a `ExportJobKind.Orders`.

`QuotationsExportEmailTemplateTests.cs`, la prueba de `:19-34` y una nueva debajo:

```csharp
    [Fact]
    public void ReadyNamesTheKindAndPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Orders", SignedUrl, "pedidos-2026-09-12-1530.xlsx", 42, ExpiresAt);

        Assert.Equal("ana@qcode.co", message.ToAddress);
        Assert.Equal("Tu exportación de pedidos está lista", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("pedidos-2026-09-12-1530.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 pedidos", body, StringComparison.Ordinal);
            Assert.Contains("13/09/2026 15:30 UTC", body, StringComparison.Ordinal);
        }

        Assert.Contains(SignedUrl, message.TextBody, StringComparison.Ordinal);
    }

    // Sin alias (spec 2026-09-14, D5): un evento emitido antes del deploy con el kind viejo cae al
    // nombre genérico, igual que cualquier kind desconocido.
    [Fact]
    public void TheOldSalesKindFallsBackToTheGenericName()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Sales");

        Assert.Equal("No pudimos generar tu exportación de registros", message.Subject);
    }
```

- [ ] **Step 2: Verlas fallar**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore | Select-String -Pattern "error CS" | Select-Object -First 3
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~QuotationsExportEmailTemplateTests"
```

Esperado (RED):
- Quotations.UnitTests no compila: `error CS0117: 'ExportJobKind' no contiene una definición para 'Orders'`;
- en Notifications fallan dos pruebas:
  - `ReadyNamesTheKindAndPutsTheLinkAndTheExpiryInBothBodies`, con `Expected: "Tu exportación de pedidos está lista"` · `Actual: "Tu exportación de registros está lista"`;
  - `TheOldSalesKindFallsBackToTheGenericName`, con `Expected: "…de registros"` · `Actual: "…de ventas"`.

- [ ] **Step 3: Pruebas de integración (RED)**

`OrderExportApiTests.cs`:
- `:35`: `Assert.Equal(ExportJobKind.Orders, job.Kind);`
- `:151`: `Assert.Matches(@"^pedidos-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$", job.FileName);`
- `:155`: `Assert.Equal("Orders", payload.RootElement.GetProperty("kind").GetString());`
- `:163`: `Assert.Equal("Pedidos", sheet.Name);`
- `:164`: `Assert.Equal(["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"], sheet.Rows[0]);`

En `OrderApiTests.cs:75` y `OrderListApiTests.cs:38`, `$"VEN-{DateTime.UtcNow.Year}-"` pasa a `$"PED-{DateTime.UtcNow.Year}-"`.

`ExportJobQueueTests.cs:138-140`, `ExportJobWorkerTests.cs:62,66` y `ExportLoadSeedTests.cs:191`: `ExportJobKind.Sales` pasa a `ExportJobKind.Orders`. En `ExportLoadSeedTests.cs:175-180`, agrega la primera aserción del `Assert.All`:

```csharp
        Assert.All(orders.Items, item =>
        {
            Assert.StartsWith("PED-", item.OrderNumber, StringComparison.Ordinal);
            Assert.NotNull(item.ClientName);
            Assert.Equal(OwnerEmail, item.AdvisorEmail);
            Assert.Equal("PaymentPending", item.PaymentStatus);
        });
```

`QuotationsExportNotificationTests.cs:59` y `DeliveryWorkersCharacterizationTests.cs:123`: `kind = "Sales"` pasa a `kind = "Orders"`.

En `OrderApiTests.cs`, agrega `using System.Text.Json;` y `using Modules.Quotations.Infrastructure.Persistence;`, y esta prueba:

```csharp
    // Acciones de auditoría nuevas (spec 2026-09-14). Las filas viejas de audit.entries quedan como
    // están (D4); lo que se prueba es lo que se escribe desde ahora.
    [Fact]
    public async Task AddingProofsAndApprovingAreAuditedAsOrderActions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();

        Assert.Contains("quotation.order.payment_proofs_added", actions);
        Assert.Contains("quotation.order.approved", actions);
        Assert.DoesNotContain(actions, action => action.StartsWith("quotation.sale.", StringComparison.Ordinal));
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }
```

(`QuotationAuditPublisher.cs:17-18,29-32` serializa `AuditEventPayload` con la propiedad `action`; `QuotationsOutboxMessage` es `internal` y este proyecto tiene `InternalsVisibleTo`.)

En `OrdersMigrationTests.cs`, agrega a la clase:

```csharp
    private const string OrdersJobId = "01900000-0000-7000-8000-00000000c007";
    private const string QuotationsJobId = "01900000-0000-7000-8000-00000000c008";

    // Un job de pedidos pendiente, tal como lo dejó el código anterior al rename, y uno de
    // cotizaciones que la migración no tiene que tocar.
    private const string LegacyExportJobsSql = $$"""
        INSERT INTO quotations.export_jobs (
            id, tenant_id, requested_by, kind, filters, status, attempts, next_attempt_at, requested_at)
        VALUES
            ('{{OrdersJobId}}', '{{TenantId}}', '{{MemberId}}', 'Sales',
             '{"ClientId":null,"AdvisorId":null,"Status":null,"PaymentStatus":null,"ConvertedFrom":"2026-09-01","ConvertedTo":"2026-09-12","ClientCuc":null,"SaleNumber":"VEN-2026"}',
             'Pending', 0, now(), now()),
            ('{{QuotationsJobId}}', '{{TenantId}}', '{{MemberId}}', 'Quotations',
             '{"ClientId":null,"QuotationNumber":"COT-2026"}',
             'Pending', 0, now(), now());
        """;

    [Fact]
    public async Task PendingOrderExportsKeepTheirKindAndTheirNumberFilter()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyExportJobsSql);

        await migrator.MigrateAsync(
            MigrationId(context, "_MigrateExportJobsToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("Orders", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("VEN-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'OrderNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.False(await ScalarAsync<bool>(
            connectionString, $"SELECT filters ? 'SaleNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("2026-09-01", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'ConvertedFrom' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("Quotations", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{QuotationsJobId}'"));
        Assert.Equal("COT-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'QuotationNumber' FROM quotations.export_jobs WHERE id = '{QuotationsJobId}'"));
    }

    [Fact]
    public async Task RevertingPutsBackTheOldKindAndKey()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);
        await ExecuteAsync(connectionString, LegacyExportJobsSql);
        await migrator.MigrateAsync(
            MigrationId(context, "_MigrateExportJobsToOrders"), TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(MigrationId(context, "_RenameToOrders"), TestContext.Current.CancellationToken);

        Assert.Equal("Sales", await ScalarAsync<string>(
            connectionString, $"SELECT kind FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.Equal("VEN-2026", await ScalarAsync<string>(
            connectionString, $"SELECT filters ->> 'SaleNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
        Assert.False(await ScalarAsync<bool>(
            connectionString, $"SELECT filters ? 'OrderNumber' FROM quotations.export_jobs WHERE id = '{OrdersJobId}'"));
    }
```

- [ ] **Step 4: Números de fixture a `PED-`**

No es comportamiento: son números inventados de las pruebas unitarias. Se alinean para que el control de residuo de Task 6 no los encuentre y ninguna prueba nueva copie un `VEN-`. El formato real lo fija `OrderNumberFormatterTests`.

```powershell
$fixtures = @(
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/ListOrdersHandlerTests.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportOrdersHandlerTests.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/ListQuotationsHandlerTests.cs")
foreach ($file in $fixtures) {
    $text = Get-Content -LiteralPath $file -Raw
    [System.IO.File]::WriteAllText((Resolve-Path $file), ($text -replace 'VEN-', 'PED-'), [System.Text.UTF8Encoding]::new($true))
}
git diff --stat -- $fixtures
```

`WriteAllText` con `UTF8Encoding($true)` conserva el BOM, y `-Raw` conserva los finales de línea. Además, a mano en `OrdersExportProcessorTests.cs:103`, el filtro `"VEN"` del final pasa a `"PED"`. Esperado en el `--stat`: sólo esos cinco archivos, con líneas cambiadas y ninguna agregada.

- [ ] **Step 5: Verlas fallar**

```powershell
dotnet build Backend.slnx --no-restore | Select-String -Pattern "error CS" | Select-Object -First 3
```

Esperado (RED): `error CS0117: 'ExportJobKind' no contiene una definición para 'Orders'` en Quotations.UnitTests y Quotations.IntegrationTests. Las pruebas de integración de este Step se ven fallar por su aserción en el Step 8, contra la producción del Step 6 a medias. Ese orden está explicado ahí.

- [ ] **Step 6: Sólo el enum, para que las pruebas compilen**

Cambia **únicamente** el miembro del enum y sus tres usos de producción. Nada de textos todavía:
- `ExportJobKind.cs:8`: `Sales,` pasa a `Orders,`;
- `ExportOrders.cs:118`: `ExportJobKind.Orders,`;
- `OrdersExportProcessor.cs:44`: `public ExportJobKind Kind => ExportJobKind.Orders;`;
- `ExportJobRunner.cs:131`: `ExportJobKind.Orders => "quotation.sale.exported",` (la acción cambia en el Step 9).

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Errores`.

- [ ] **Step 7: RED de las unitarias**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build --filter "FullyQualifiedName~QuotationsExportEmailTemplateTests"
```

Esperado (RED), fallando por su aserción:
- `OrderNumberFormatterTests.FormatsWithThePedPrefix` (×3): `Expected: "PED-2026-0001"` · `Actual: "VEN-2026-0001"`;
- `ConvertingNamesTheOrderInMasculine`: `Actual: "Convertida en la venta PED-2026-0001."`;
- `EveryOrderStatusHasTheLabelOfTheOrdersTable`: `(Approved, "Aprobado")` contra `(Approved, "Aprobada")`;
- `WritesTheOrdersListColumnsInTheirOrder`: `Expected: "Pedidos"` · `Actual: "Ventas"`, y la del nombre de archivo: `Actual: "ventas-2026-09-12-1530.xlsx"`;
- `AOrdersJobIsAuditedAsAOrderExport`: `Actual: "quotation.sale.exported"`;
- las dos de Notifications del Step 2.

- [ ] **Step 8: RED de las de integración**

Con Docker corriendo:

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~AddingProofsAndApprovingAreAuditedAsOrderActions|FullyQualifiedName~ConvertCreatesTheOrderAndLeavesTheQuotationConverted|FullyQualifiedName~ListReturnsTheOrderWithItsClientAdvisorAndTotalsResolved|FullyQualifiedName~PendingOrderExportsKeepTheirKindAndTheirNumberFilter|FullyQualifiedName~RevertingPutsBackTheOldKindAndKey|FullyQualifiedName~ExportLoadSeedTests"
```

Esperado (RED):
- `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` con `Assert.Matches() Failure` sobre `ventas-…xlsx`;
- `AddingProofsAndApprovingAreAuditedAsOrderActions` con `Assert.Contains() Failure: Item not found in collection` · `Not found: "quotation.order.payment_proofs_added"`;
- las dos de `PED-` con `Assert.StartsWith() Failure` · `Actual: "VEN-2026-…"`;
- la de la carga sintética, igual, sobre `VEN-`;
- las dos de `OrdersMigrationTests` nuevas con `Sequence contains no matching element`.

Pega las líneas de cada falla.

- [ ] **Step 9: Textos, auditoría, numeración y filtros**

| Archivo:línea | Hoy | Queda |
| --- | --- | --- |
| `ExportOrders.cs:33` | `string? SaleNumber);` | `string? OrderNumber);` |
| `OrdersExportProcessor.cs:22` | `public const string SheetName = "Ventas";` | `public const string SheetName = "Pedidos";` |
| `OrdersExportProcessor.cs:24` | `public const string FilePrefix = "ventas";` | `public const string FilePrefix = "pedidos";` |
| `OrdersExportProcessor.cs:34` | `new("Venta", 18),` | `new("Pedido", 18),` |
| `OrdersExportProcessor.cs:67` | `filters.SaleNumber,` | `filters.OrderNumber,` |
| `OrdersExportProcessor.cs:83` | `"Empty: no sales matched the export filters when the export ran."` | `"Empty: no orders matched the export filters when the export ran."` |
| `ExportJobRunner.cs:131` | `ExportJobKind.Orders => "quotation.sale.exported",` | `ExportJobKind.Orders => "quotation.order.exported",` |
| `ApproveOrder.cs:50` | `"quotation.sale.approved",` | `"quotation.order.approved",` |
| `AddOrderPaymentProofs.cs:104` | `"quotation.sale.payment_proofs_added",` | `"quotation.order.payment_proofs_added",` |
| `ExportStatusLabels.cs:31` | `OrderStatus.Approved => "Aprobada",` | `OrderStatus.Approved => "Aprobado",` |
| `ExportStatusLabels.cs:32` | `"The sale status has no export label."` | `"The order status has no export label."` |
| `ExportStatusLabels.cs:7` | `quote-status-badge.tsx y sale-list.ts` | `quote-status-badge.tsx y order-list.ts` |
| `OrderNumberFormatter.cs:5` | `/// <summary>Formato <c>VEN-2026-0001</c> — …` | `/// <summary>Formato <c>PED-2026-0001</c> — …` (el resto de la línea queda) |
| `OrderNumberFormatter.cs:11` | `$"VEN-{year}-{sequence:D4}");` | `$"PED-{year}-{sequence:D4}");` |
| `QuotationChangeSummary.cs:89` | `Trim($"Convertida en la venta {orderNumber}.");` | `Trim($"Convertida en el pedido {orderNumber}.");` |
| `QuotationsExportKindText.cs:16` | `"Sales" => new("venta", "ventas"),` | `"Orders" => new("pedido", "pedidos"),` |
| `ExportLoadSeeder.cs:338` | `// Convertido un día después del envío, … VEN-{año UTC de la conversión}-{n}.` | `// Convertido un día después del envío, … PED-{año UTC de la conversión}-{n}.` |
| `ExportLoadSeeder.cs:345` | `'VEN-' \|\| numbered.year \|\| …` | `'PED-' \|\| numbered.year \|\| …` |

En la prosa de `OrdersExportProcessor.cs:8` y `:27`, "el listado de ventas" pasa a "el listado de pedidos", y "(sale-table.tsx: Venta, Cliente, …)" pasa a "(order-table.tsx: Pedido, Cliente, …)".

`ExportOrders.cs:127` (`command.OrderNumber`) ya quedó bien en Task 1: el record es posicional.

Los números `VEN-` que ya están en la base no se reescriben (D2). El contador es el mismo `order_number_counters`, así que el primer `PED-` de un año sigue la secuencia del último `VEN-`.

- [ ] **Step 10: La migración de datos de `export_jobs`**

Va en este commit y no en el de Task 2 (hallazgo 5). Desde el Step 6, EF no puede materializar un job con `kind = 'Sales'` (la conversión a enum tira), y desde el Step 9 `OrdersExportFilters` ignoraría la clave `SaleNumber`: el export saldría sin el filtro de número, sin error.

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add MigrateExportJobsToOrders --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

Esperado: `Up`/`Down` vacíos (el modelo no cambió: el enum se guarda como texto de 20). Reemplaza el cuerpo de la clase por:

```csharp
    /// <inheritdoc />
    public partial class MigrateExportJobsToOrders : Migration
    {
        // Datos, no esquema (spec 2026-09-14, D4). Un job pendiente con kind 'Sales' no lo puede leer
        // EF desde que ExportJobKind dice Orders, y OrdersExportFilters ignoraría la clave SaleNumber.
        // Va en el mismo commit que el enum; la de RenameToOrders no podía llevarlo (plan, hallazgo 5).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE quotations.export_jobs SET kind = 'Orders' WHERE kind = 'Sales';");
            migrationBuilder.Sql("""
                UPDATE quotations.export_jobs
                   SET filters = (filters - 'SaleNumber') || jsonb_build_object('OrderNumber', filters -> 'SaleNumber')
                 WHERE filters ? 'SaleNumber';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE quotations.export_jobs
                   SET filters = (filters - 'OrderNumber') || jsonb_build_object('SaleNumber', filters -> 'OrderNumber')
                 WHERE filters ? 'OrderNumber';
                """);
            migrationBuilder.Sql("UPDATE quotations.export_jobs SET kind = 'Sales' WHERE kind = 'Orders';");
        }
    }
```

Comprobación:

```powershell
$dataMigration = Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_MigrateExportJobsToOrders.cs" | Where-Object { $_.Name -notlike "*.Designer.cs" }
Select-String -Path $dataMigration.FullName -Pattern "CreateTable|DropTable|AddColumn|DropColumn|AlterColumn|Rename"
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: el `Select-String` sin salida, el build limpio y `No changes have been made to the model since the last migration.`

- [ ] **Step 11: GREEN**

Con Docker corriendo (decenas de minutos):

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build
```

Esperado: pasan todas las de los Steps 1 y 3, las cuatro de `OrdersMigrationTests`, y ninguna falla fuera del baseline (filtro de Task 1, Step 10).

- [ ] **Step 12: Nada de exportación, auditoría ni numeración viejas**

```powershell
rg -n "ExportJobKind\.Sales|""Sales""|\bSaleNumber\b|VEN-|quotation\.sale\.|""Aprobada""|""Ventas""|""Venta""|""ventas" src tests --glob '!**/Migrations/**'
```

Esperado: sólo lo que está a propósito:
- `OrdersMigrationTests.cs`, que siembra `'Sales'`, `SaleNumber` y `VEN-` del esquema viejo;
- `QuotationsExportEmailTemplateTests.cs`, que prueba que `"Sales"` cae a "registros" (D5);
- `OrderApiTests.cs`, en el `StartsWith("quotation.sale.", …)` que prueba su ausencia.

- [ ] **Step 13: Formato y commit**

```powershell
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
dotnet format Backend.slnx --verify-no-changes --include $changed
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git add $changed tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs (Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_MigrateExportJobsToOrders*.cs").FullName
git status --short
git commit -m "feat(quotations): exportación, correo y numeración PED- de pedidos" -m "ExportJobKind.Orders con MigrateExportJobsToOrders (kind y clave OrderNumber de los jobs pendientes); Notifications nombra Orders como pedido/pedidos, sin alias Sales; pedidos-*.xlsx, hoja Pedidos, etiqueta Aprobado; auditoría quotation.order.*; números PED- con el mismo contador; historial 'Convertida en el pedido'."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera de lo agregado (si el snapshot no cambió, `git add` no lo toca), y el `Select-String` sin salida.

---

### Task 6: Documentación viva y verificación final

**Files:**
- Modify: `README.md:347,913-916`
- Modify: `CLAUDE.md:118`
- Rename + modify: `docs/integracion-cotizaciones-y-ventas.md` → `docs/integracion-cotizaciones-y-pedidos.md` (`:1,10,13,27-28,47-48,68,85,87,91-92,130-133`)
- Modify: los comentarios de `src/` y `tests/` que el control de residuo del Step 3 encuentre fuera de las excepciones
- Sin cambios: `.atl/skill-registry.md` (hallazgo 7), `docs/superpowers/**` (D11)

**Interfaces:**
- Consumes: todo lo anterior.
- Produces: la rama lista para PR, con los siete commits (el del plan y los seis de implementación) y la suite completa sin fallas nuevas.

- [ ] **Step 1: `README.md` y `CLAUDE.md`**

| Archivo:línea | Hoy | Queda |
| --- | --- | --- |
| `README.md:347` | `- el 30 % convertidas en venta;` | `- el 30 % convertidas en pedido;` |
| `README.md:913` | `listados de cotizaciones y ventas (`…`quotations/export`,` | `listados de cotizaciones y pedidos (`…`quotations/export`,` |
| `README.md:914` | `` `POST /tenants/{tenantId}/sales/export`) no devuelven el archivo… `` | `` `POST /tenants/{tenantId}/orders/export`) no devuelven el archivo… `` |
| `README.md:916` | `…cotizaciones y ventas contestan `202` y lo encolan en` | `…cotizaciones y pedidos contestan `202` y lo encolan en` |
| `CLAUDE.md:118` | `` `/reports/sales/summary` existe porque… `` | `` `/reports/orders/summary` existe porque… `` |

`README.md:115` y `:952` dicen "sale" como verbo (*salir*): no se tocan.

- [ ] **Step 2: La guía de integración**

```powershell
git mv docs/integracion-cotizaciones-y-ventas.md docs/integracion-cotizaciones-y-pedidos.md
```

Líneas a cambiar. Donde la línea que se toca ya no coincidía con el código, se corrige con él (`CLAUDE.md`, «La autoridad es el código»):

| Línea | Hoy | Queda |
| --- | --- | --- |
| `:1` | `# Cotizaciones y ventas — guía de integración para el frontend` | `# Cotizaciones y pedidos — guía de integración para el frontend` |
| `:10` | `` `QuotationEndpoints.cs`/`SaleEndpoints.cs`/`QuotationsAuthorization.cs`, `` | `` `QuotationEndpoints.cs`/`OrderEndpoints.cs`/`QuotationsAuthorization.cs`, `` |
| `:13` | `` (`quotations.quotation.read/manage`, `quotations.sale.read/manage`). El `` | `` (`quotations.quotation.read/manage`, `quotations.order.read/manage`). El `` |
| `:27` | `Sale.status:         Approved   (único valor hoy)` | `Order.status:        Pending → Approved` |
| `:28` | `Sale.paymentStatus:  FullPaymentReceived \| …` | `Order.paymentStatus: FullPaymentReceived \| …` |
| `:47` | `` \| `GET` \| `/quotations/{id}/sale` \| — \| 404 si no se convirtió todavía \| `` | `` \| `GET` \| `/quotations/{id}/order` \| — \| 404 si no se convirtió todavía \| `` |
| `:48` | `` \| `POST` \| `/quotations/{id}/sale` \| `ConvertQuotationToSaleRequest` \| Aprueba la cotización y crea la venta en una sola operación \| `` | `` \| `POST` \| `/quotations/{id}/order` \| `ConvertQuotationToOrderRequest` \| Crea el pedido en `Pending` y deja la cotización en `Converted`, en una sola operación \| `` |
| `:68` | `type ConvertQuotationToSaleRequest = {` | `type ConvertQuotationToOrderRequest = {` |
| `:85` | `` // del último envío) ya está adentro de `canBeConvertedToSale`; viaja aparte para que el `` | `` // del último envío) ya está adentro de `canBeConvertedToOrder`; viaja aparte para que el `` |
| `:87` | `canBeSent: boolean; hasChangesSinceSent: boolean; canBeConvertedToSale: boolean;` | `canBeSent: boolean; hasChangesSinceSent: boolean; canBeConvertedToOrder: boolean;` |
| `:91` | `type SaleResponse = {` | `type OrderResponse = {` |
| `:92` | `id: string; saleNumber: string; quotationId: string; status: "Approved";` | `id: string; orderNumber: string; quotationId: string; status: "Pending" \| "Approved";` |
| `:130` | `` \| `sale.sale.payment_proof_required` \| 422 \| `POST /sale` … en `POST /sale/proofs`, … \| `` | `` \| `order.order.payment_proof_required` \| 422 \| `POST /order` … en `POST /order/proofs`, … \| `` |
| `:131` | `` \| `sale.payment_proof.file_not_found` / … \| `` | `` \| `order.payment_proof.file_not_found` / … \| `` |
| `:132` | `` \| `sale.payment_proof.amount_invalid` \| … \| `` | `` \| `order.payment_proof.amount_invalid` \| … \| `` |
| `:133` | `` \| `sale.payment_proof.not_found` \| 422 \| `updatedProofs` referencia un `proofId` que no es de esta venta \| `` | `` \| `order.payment_proof.not_found` \| 422 \| `updatedProofs` referencia un `proofId` que no es de este pedido \| `` |

```powershell
rg -n -i "sale|venta" docs/integracion-cotizaciones-y-pedidos.md
rg -n "integracion-cotizaciones-y-ventas" --glob '!docs/superpowers/**' .
```

Esperado: las dos sin salida. Nadie fuera de los históricos enlaza el nombre viejo, verificado el 2026-09-14.

- [ ] **Step 3: Residuo con el comando del spec**

```powershell
rg -n -i "\bsales?\b|\bventas?\b|Sale[A-Z]|sale-" src tests --glob '!**/Migrations/**' --glob '!**/packages.lock.json' --glob '!**/localities.json'
```

Revisa cada línea. Sólo son válidas las de estas categorías (spec, «Residuo», más el hallazgo 8):
1. **El verbo *salir*** en un comentario en español ("sale como 500", "salen de la misma consulta", "Sale del claim"). Hoy son unas 40, por ejemplo `Program.cs:121`, `ApiExceptionHandler.cs:88`, `AuthPreferenceEndpoints.cs:10,13,73`, `CustomerIdentification.cs:18`, `QuotationsReportSource.cs:114`, `ReportRankFolding.cs:9` y `PriceChangeReportSource.cs:151`.
2. **"ventas-junior", "Ventas-Junior", "Ventas junior", "Ventas senior"**: el rol de ejemplo de Authorization y Tenancy (spec, «Copy», regla 3).
3. **IDs de slice** `SALE-01`/`SALE-04`, que no se renumeran.
4. **Pruebas que escriben o prueban lo viejo a propósito:** `OrdersMigrationTests.cs`, `AuthorizationPermissionsMigrationTests.cs`, `QuotationsExportEmailTemplateTests.cs` (D5), `OrderContractApiTests.cs` (rutas viejas y campos ausentes) y `OrderApiTests.cs` (`"quotation.sale."` como ausencia).
5. **La cita literal del pedido de negocio** en `QepServiceCollectionExtensions.cs:613` ("ver clientes, cotizaciones y ventas").

Cualquier otra línea es prosa que quedó con el concepto viejo, por ejemplo `QuotationsReportSummary.cs:19` ("Convertir una cotización en venta") o `OrderTests.cs:189` ("Aprobada, la venta es…"). Corrígela con concordancia masculina ("Aprobado, el pedido es…") y en español neutro con tuteo (D7). Pega la salida final del comando en el handoff.

- [ ] **Step 4: Controles estrictos sobre código y textos**

```powershell
$intentional = 'OrdersMigrationTests\.cs|AuthorizationPermissionsMigrationTests\.cs|QuotationsExportEmailTemplateTests\.cs|OrderContractApiTests\.cs|OrderApiTests\.cs:\d+:.*quotation\.sale\.|AuthPreferenceEndpoints\.cs:10:|CustomerIdentification\.cs:18:|QuotationsReportSource\.cs:114:'
rg -n -s "Sale[A-Z]|\bSales?\b|\bsales?[A-Z]\w*|\bsale_|\.sale\.|/sales?\b|\bVEN-" src tests ops --glob '!**/Migrations/**' | Select-String -CaseSensitive -NotMatch -Pattern $intentional
rg -n "\""[^\""]*\b[Vv]entas?\b[^\""]*\""" src tests --glob '*.cs' --glob '!**/Migrations/**' | Select-String -NotMatch -Pattern '[Vv]entas[- ](junior|Junior|senior)'
```

Esperado: **los dos sin salida**. El primero cubre identificadores, rutas, códigos, tablas y números. El segundo, cualquier literal de texto con "venta(s)" que no sea el rol de ejemplo.

- [ ] **Step 5: Build, formato y suite completa**

Con Docker corriendo. La suite completa tarda decenas de minutos (Testcontainers, un Postgres por prueba de integración); corre en primer plano.

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
$changed = git diff --name-only HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
if ($changed) { dotnet format Backend.slnx --verify-no-changes --include $changed }
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Authorization/Modules.Authorization.Infrastructure --context AuthorizationDbContext
$final = Join-Path $env:TEMP "qep-rename-orders-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $final
$expected = Get-Content (Join-Path $env:TEMP "qep-rename-orders-baseline-failed.txt") |
    ForEach-Object { $_ -replace 'Sales', 'Orders' -replace 'Sale', 'Order' }
$actual = Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique
"Fallas nuevas:"; $actual | Where-Object { $expected -notcontains $_ }
"Arregladas desde el baseline:"; $expected | Where-Object { $actual -notcontains $_ }
```

Esperado:
- restore y build limpios (`0 Advertencia(s)`, sin warnings nuevos, como pide el spec);
- `--verify-no-changes` sin archivos;
- los dos `has-pending-model-changes` en `No changes have been made…`;
- **"Fallas nuevas:" vacío**.

Pega los dos bloques y el total `Superado/Con error` de cada proyecto.

- [ ] **Step 6: Commit**

```powershell
if ((git branch --show-current) -ne "feature/rename-sales-to-orders") { throw "ABORT: rama equivocada" }
git add README.md CLAUDE.md docs/integracion-cotizaciones-y-pedidos.md $changed
git status --short
git commit -m "docs(orders): documentación viva de pedidos" -m "README, CLAUDE.md y la guía de integración, que pasa a docs/integracion-cotizaciones-y-pedidos.md; comentarios que todavía nombraban la venta. Los specs y planes históricos no se tocan (D11)."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` muestra el rename `R  docs/integracion-cotizaciones-y-ventas.md -> docs/integracion-cotizaciones-y-pedidos.md` y nada sin agregar. El `Select-String`, sin salida.

- [ ] **Step 7: Los commits de la rama**

```powershell
git log --oneline b4bd22c..HEAD
git rev-list --count b4bd22c..HEAD
```

Esperado: `7`, del más nuevo al más viejo:

```
<sha> docs(orders): documentación viva de pedidos
<sha> feat(quotations): exportación, correo y numeración PED- de pedidos
<sha> feat(authorization)!: permisos de pedidos y migración de roles custom
<sha> feat(quotations)!: contrato HTTP de pedidos en rutas, campos y códigos
<sha> refactor(quotations): renombrar tablas, columnas e índices de ventas a pedidos
<sha> refactor(quotations): renombrar símbolos de venta a pedido
<sha> docs(orders): plan del rename de venta a pedido en el backend
```

La rama no se publica ni se mergea desde este plan. El PR y el despliegue —backend primero, frontend inmediatamente después— los decide el developer.

---

## Autorrevisión contra el spec

| Sección del spec | Dónde |
| --- | --- |
| D1 `Order` | Task 1 (mapeo), Task 2 (tablas) |
| D2 `PED-` con el mismo contador; `VEN-` intactos | Task 5, Steps 1 y 9 (`OrderNumberFormatterTests`, `OrderApiTests:75`, `OrderListApiTests:38`, seeder) |
| D3 corte duro | Task 3 (`TheSalesRoutesNoLongerExist`), Task 4 (sin códigos viejos) |
| D4 qué se migra | Task 4 (roles custom), Task 5 (`kind` + `SaleNumber`); lo histórico no se toca (Global Constraints) |
| D5 sin alias `"Sales"` | Task 5 (`TheOldSalesKindFallsBackToTheGenericName`) |
| D6 dentro de `Modules.Quotations` | Global Constraints; ningún proyecto se renombra |
| D7 copy masculino y con tuteo | Task 4 (catálogo), Task 5 (Excel, historial, correo), Task 6, Step 3 (prosa) |
| D8 URL del asistente | Sólo frontend; el backend no tiene esa ruta |
| D9 reporte de pedidos | Task 1 (tipos), Task 3 (rutas y campos), Task 4 (permiso) |
| D10 códigos sin mensaje | Task 3, Step 6 (se renombran, nada más) |
| D11 documentos vivos | Task 6 (`.atl/skill-registry.md` sin cambios, hallazgo 7) |
| Rutas, `Location` y tags | Task 3, Step 4 |
| Query string y JSON | Task 3, Steps 1 y 5 |
| Códigos (17) | Task 3, Step 6 |
| Auditoría (3) | Task 5, Steps 3 y 9 |
| Permisos, roles, políticas y catálogo | Task 4 |
| Numeración | Task 5 |
| Exportación y correo | Task 5 |
| Historial | Task 5 (`ConvertingNamesTheOrderInMasculine`) |
| Migración de `Quotations` sin `DropTable`/`CreateTable` | Task 2, Steps 8-10 |
| `export_jobs` | Task 5, Step 10 (migración propia, hallazgo 5) |
| Acoplados a nombres de base | Task 2, Step 12 |
| Migración de `Authorization` | Task 4, Step 5 |
| Símbolos del backend | Task 1 |
| Build sin warnings; unitarias de Quotations, Reporting, Notifications y ArchitectureTests | Cada tarea; Task 6, Step 5 |
| Integración de la migración con pedidos, comprobantes, contadores, `export_job` y rol custom | Task 2 (`OrdersMigrationTests`), Task 5 (sus dos pruebas nuevas), Task 4 (`AuthorizationPermissionsMigrationTests`) |
| Conversión doble → `already_converted` | Task 2, Steps 4 y 11 (hallazgo 2) |
| Residuo | Task 6, Steps 3-4 |
| Despliegue | «Entrega» |
| Riesgos | Índice: Task 2, Step 11. Rol custom: Task 4. Job pendiente: Task 5, Step 10. Concordancia: Tasks 4-6. Rama: guard en cada commit |
| Frontend | Fuera de este plan: tiene el suyo |

