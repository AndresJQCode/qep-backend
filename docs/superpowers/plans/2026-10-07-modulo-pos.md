# Módulo POS (punto de venta) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un cajero abra su caja, venda productos del catálogo con lector o búsqueda, cobre en efectivo, tarjeta o transferencia (o mezclados), imprima un ticket de 80 mm, anule con motivo y cierre su caja contando el efectivo, con idempotencia de punta a punta y todo detrás de la capacidad `pos`.

**Architecture:** Backend: módulo nuevo `Pos` (Domain/Application/Infrastructure/Api) con schema `pos`, agregados `CashSession` y `PosSale`, la fórmula de línea de IVA incluido extraída a `BuildingBlocks.Domain/Pricing` y compartida con Quotations, puertos hacia Catalog, Companies y Tenancy con adaptadores en `Bootstrapper`, y 12 endpoints BFF bajo `/api/v1/tenants/{tenantId}/pos`. Frontend: feature `features/pos` con un store zustand persistido en `sessionStorage` por tenant y usuario, el preview del servidor como única fuente de totales, una máquina de estados de cobro que nunca manda dos cuerpos con un mismo id, y la pantalla de caja a pantalla completa. Los dos tracks corren **en paralelo**: el frontend trabaja contra el contrato HTTP del spec con `fetch` mockeado.

**Tech Stack:** Backend .NET 10 (SDK de `global.json`, 10.0.400), EF Core 10 + Npgsql, FluentValidation, xUnit v3 3.2.2, Testcontainers.PostgreSql 4.14.0 (`postgres:18-alpine`; Docker corriendo). Frontend React 19.2, TanStack Router 1.170 + Query 5.102, zustand 5.0.15 (`persist`), Vitest 4.1 + Testing Library + jsdom, bun 1.4.2, oxlint, Prettier.

**Spec:** `docs/superpowers/specs/2026-10-07-modulo-pos-design.md` (lo copia la Task B0 al worktree del backend y la Task F0 al del frontend; origen: el scratchpad de la sesión del 2026-10-07). El plan argumenta desde el spec: quien ejecuta lee los dos.

**Precondición dura:** el spec de entitlements (`2026-10-07-modulos-por-tenant-design.md`) **ya está implementado** en las ramas `feature/modulos-por-tenant` de los dos repos: `TenantModuleKeys.Pos`, `PermissionDefinition` con 6.º parámetro `RequiredModules`, la tabla `tenancy.tenant_modules`, la prueba de completitud del mapa permiso → módulo en `CompositionRootTests`, `src/components/module-gate.tsx`, `src/features/auth/hooks/use-tenant-modules.ts` y `src/test/tenant-modules.ts`. Hoy (2026-10-07) esas ramas **no existen** en ninguno de los dos repos. Las Tasks B0 y F0 lo comprueban y, si falta algo, **paran**.

## Global Constraints

**Del spec** (valores copiados tal cual):

- Ruta: grupo `/api/v1/tenants/{tenantId:guid}/pos`, tag `Pos`, en `Modules.Pos.Api/PosEndpoints.cs`. «Cada endpoint declara su propio `RequireAuthorization`.»
- Permisos: `pos.sale.read`, `pos.sale.create`, `pos.sale.discount`, `pos.sale.void`, `pos.register.operate`, `pos.register.read`. «Los seis exigen `[pos]`.» Riesgos: read low, create medium, discount high, void high, operate medium, register.read medium.
- Roles: `admin` suma los seis; `cashier` («Cajero», «Vende en el punto de venta y abre y cierra su propia caja.», `"Tenancy"`, `"medium"`) con `pos.sale.read`, `pos.sale.create`, `pos.register.operate`. «`cashier` se reserva» en `SystemRoleKeys`.
- `PosLimits`: `MaxCashAmount = 100 000 000`, `MaxCountedCash = 1 000 000 000`, `MaxQuantity = 99 999`, escala 2 para dinero, descuento y cantidad. «Toda cifra … con más de 2 decimales se **rechaza**, nunca se redondea.»
- Fórmula: «`gross = q × p`; `DiscountAmount = Round(gross × d / 100)`; `lineTotal = Round(gross) − DiscountAmount`; `TaxAmount = Round(lineTotal × t / (100 + t))`; `Subtotal = lineTotal − TaxAmount`», `Math.Round(value, 2, MidpointRounding.AwayFromZero)`.
- Topes: «200 líneas, 5 pagos, un solo efectivo».
- Numeración `POS-000001` por tenant, contador `pos.sale_number_counters`, «El número se pide **después** de todas las validaciones y adentro de la transacción».
- Idempotencia: huella SHA-256 hex minúscula de JSON canónico `[cashSessionId, [[productId, quantity, discountPercentage, expectedUnitPrice, expectedTaxPercentage], …], [[method, amount, tendered, reference], …]]`, decimales `0.##` InvariantCulture, ausente como `null`.
- Carrito: «clave … **por tenant y por usuario** (`qep.pos.cart.{tenantId}.{userId}`)», `sessionStorage`.
- Desde un intento incierto, «Sólo `pos.sale.discount_not_allowed` y los 422 `pos.*` (pasos 2 a 6) son definitivos»; `authorization.denied`, 403 de tenancy, `validation.failed`, 400 y 404 siguen inciertos. En el primer envío, todo 4xx es definitivo.
- Ticket: `@page pos-ticket { size: 80mm 297mm; margin: 0; }`, `[data-pos-ticket] { page: pos-ticket; width: 72mm; padding: 4mm; }`, `font-mono` 11 px.
- UI: «Cobrar» y «Confirmar venta» ≥ 56 px; objetivos táctiles ≥ 44 px; centavos visibles sólo cuando existen.
- Mensajes del producto en español colombiano, tuteando; códigos, enums, rutas y permisos tal cual.

**Del proceso:**

- **Worktrees** (nunca el checkout principal: en `qep-backend` hay otra sesión y `qep-frontend` está en `main`):
  - Backend: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos`, rama **`feature/pos`**, creada desde `feature/modulos-por-tenant`.
  - Frontend: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos`, rama **`feature/pos`**, creada desde `feature/modulos-por-tenant`.
  - Cada worktree tiene **su propio** `.codegraph/` (nunca se copia otro índice). Cada bloque de comandos empieza con `Set-Location` al worktree.
- **TDD estricto**: RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff (resumen Superado/Con error/Omitido o `Tests  N failed | M passed`, y el mensaje de cada falla). Un RED por error de compilación vale cuando el tipo todavía no existe; se anota así.
- **PowerShell** en todo comando: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`; `curl.exe`; cuerpos JSON a archivo y `-d "@archivo.json"`. **Nunca** se pipea `dotnet build` ni `dotnet test`.
- Backend: antes de cada `build`, `test` o `ef`: `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force` (y si sigue el MSB3021, buscar `dotnet Api.dll` por `CommandLine`). Por tarea se corren **sólo** las clases que la tarea toca (`--filter "FullyQualifiedName~<Clase>"`, un proyecto por comando) más `tests/ArchitectureTests/ArchitectureTests`. La suite completa corre en B0 (línea base) y en B14.
- xUnit v3: `TestContext.Current.CancellationToken` en toda llamada que acepte un token (xUnit1051 es error). Sin FluentAssertions ni NSubstitute: `Assert.*` y dobles a mano.
- Frontend: `bun run test --run <ruta>` por tarea; `bun run lint`; `bun run build` (`tsc -b && vite build`) regenera `src/routeTree.gen.ts`, que se commitea y **nunca** se edita a mano. `fetch` se mockea con `vi.mocked(fetch).mockImplementation(...)` (el setup ya lo stubbea); msw no existe.
- **Commits**: Conventional Commits en español, **sin atribución de IA ni trailer `Co-Authored-By`**, aunque el harness lo pida. Un comando por commit, con guard de rama y rutas explícitas (nunca `git add -A` ni `git add .`):
  `if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add <rutas>; git commit -m "<mensaje>"`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` sin salida (si sale algo: `git commit --amend` antes de seguir). La rama se lee **en el momento** de commitear, nunca de un snapshot.
- **Nunca imprimir un secreto.** `dotnet user-secrets list` sólo enmascarado o contando.
- Backend: prosa, comentarios y `<summary>` en español tuteando; identificadores, códigos de error y mensajes de excepción en inglés. Frontend: identificadores y comentarios en inglés, copy en español tuteando.
- **Chequeo de formato backend** sobre los `.cs` que toca cada tarea (los de `Migrations/` no; `ENDOFLINE` y `CHARSET` son ruido previo):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-pos-format"
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

Esperado: sin salida en el último bloque, o sólo líneas que no tocaste (se anotan en el handoff).

- **Chequeo de frontend** por tarea: `bun run lint` sin errores nuevos y `bunx prettier --check <archivos tocados>` limpio.

## Review Focus

Los cinco casos que el spec implica y que ninguna de sus pruebas nombradas cubre, del más probable al menos probable. Cada uno tiene su prueba en la tarea dueña del código:

1. **Anular una venta dividida con cambio.** Tarjeta 20 000 + efectivo con recibido 20 000 sobre un total de 37 890: el arqueo tiene que restar el **aplicado** (`cashDue` = 17 890), no el billete. Restar `Tendered` deja `CashTotal` en −2 110 y un faltante falso al cerrar. → Task B5, `RegisterVoidOfASplitSaleSubtractsTheAppliedCashNotTheTendered`.
2. **El mismo producto en dos líneas** (una con descuento y otra sin él: `addProduct` sólo acumula sobre la línea sin descuento). El backend tiene que tolerar `productId` repetido en `FindManyAsync` (un `Distinct` y un diccionario, no un `ToDictionary` sobre la lista cruda) y el preview tiene que devolver dos líneas. → Task B5, `TheSameProductOnTwoLinesKeepsBothLines`; Task B9, `PreviewWithTheSameProductTwiceReturnsTwoLines`; Task B10, `CreateWithTheSameProductOnTwoLinesKeepsBothLines`; Task F5, `refreshExpected updates every line of the product when unlocked`.
3. **El filtro de fechas en el borde del día del tenant.** Una venta a las 23:30 de Bogotá (04:30 UTC del día siguiente) es del día local, no del día UTC. → Task B11, `ListSalesCutsTheDayInTheTenantTimeZone`.
4. **El store de otro usuario no pisa el estado guardado.** Con la clave por usuario, el store de B nunca escribe en la clave de A, y crear el store de B no borra lo de A (con `persist` un `set` previo a la hidratación sobrescribiría el intento guardado). → Task F5, `user B does not rehydrate the attempt of user A and never writes into its key`.
5. **Pegar un monto y Enter en el recibido.** El guardia de ráfagas mira intervalos de `keydown`; un pegado no es una ráfaga y su Enter tiene que confirmar. → Task F8, `paste followed by Enter is not treated as a scan`.

---

## Hallazgos contra el código (2026-10-07)

Verificados en `qep-backend` `feature/retencion-excel-pedidos` (`717adea`) y `qep-frontend` `main`. Las Tasks B0 y F0 re-verifican sobre `feature/modulos-por-tenant`; si una línea citada se movió, se usa la del código y se anota.

1. **Composición a mano.** Los handlers se registran uno por uno en `src/Bootstrapper/QepServiceCollectionExtensions.cs` (Companies en `:182-199`), los validadores por ensamblado (`:431-436`), la infraestructura en `:437-450`, los adaptadores después (`:452-537`), los roles en `:582-710`, las `PermissionDefinition` desde `:711` y las políticas en la cadena `AddAuthorizationBuilder()` que termina en `:1170`. `CompositionRootTests` descubre los handlers por reflexión sobre `Modules.*.Application.dll` del directorio de salida (`CompositionRootTests.cs:150-153`): basta con que `ArchitectureTests.csproj` referencie `Modules.Pos.Application`.
2. **El host no llama `AddXInfrastructure`**: lo hace `AddQepPlatform` (Program.cs:41). En `Program.cs` sólo se mapean endpoints (`:122-141`) y se inicializan bases (`:143-179`); `InitializeQuotationsDatabaseAsync` está en `:178`.
3. **Solución `Backend.slnx`** con carpetas `/src/Modules/<M>/` y `/tests/Modules/<M>/`. `Directory.Build.props`: `TreatWarningsAsErrors`, `RestorePackagesWithLockFile`; `Directory.Packages.props` centraliza versiones (`ManagePackageVersionsCentrally`, `CentralPackageTransitivePinningEnabled`). Los paquetes que usa Pos ya están declarados: no se agrega ninguna versión.
4. **Los lock files cambian fuera de Pos.** Todo proyecto que referencia `Api.csproj` (cada `*.IntegrationTests`), `Bootstrapper.csproj` y `ArchitectureTests.csproj` lista sus referencias de proyecto en su `packages.lock.json`; sumar Pos los modifica. Se regeneran con `dotnet restore Backend.slnx --force-evaluate` y se commitean juntos (Task B1).
5. **`AuthorizationCatalogApiTests.TheCatalogNamesTheOrderPermissions` se rompe con POS**: afirma `Assert.DoesNotContain(catalog.Permissions, permission => permission.Permission.Contains("sale", …))` (`AuthorizationCatalogApiTests.cs:81-82`). Los `pos.sale.*` la ponen roja. La Task B2 la acota a los permisos fuera de `pos.`.
6. **`SystemRoleKeys`** (`src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs`) tiene `Admin`, `Advisor`, `Billing`, `All` e `IsReserved`. `RoleApiTests.CreateWithASystemRoleKeyIsRejected` (`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RoleApiTests.cs:132-145`) es el modelo para la prueba de `cashier`.
7. **Catalog**: `IProductRepository` no tiene lectura exacta por código; las pruebas de Catalog con repositorio real usan `factory.Services.CreateScope()` dentro de la clase (`ProductDetailsApiTests.cs:344-370`, con `Product.Create(ProductId.New(), tenant, name, code, ProductDetails.Empty, new ProductPricing { … }, at)`). Columnas de `catalog.products`: `code`, `is_active`, `tax_rate_id`, `price_base_cop`, `image_file_id`.
8. **Harness**: cada proyecto de integración tiene su propio `QepApiFactory`. El de Companies (`CompaniesApiHarness.cs`) es el mínimo; `QuotationsApiHarness.RegisterTenantInTimeZoneAsync` (`:142-170`) registra un tenant real y `ReportingApiHarness.InviteActiveAdvisorAsync` (`:178-206`) invita y acepta un miembro. Las escalas de producto por defecto están en `QuotationsApiHarness.DefaultScales` (`:435-452`).
9. **Frontend**: no hay `persist` de zustand en ningún lado; `src/test/setup.ts` stubbea `fetch` y limpia los dos storages en `afterEach`; `src/test/render-route.tsx` monta el `routeTree` real; `printWithTitle` vive en `features/quotes/utils/` y lo importan `orders/pages/order-detail-page.tsx:18` y `quotes/components/created-order-summary.tsx:22`; no hay `scroll-area`, `sheet` ni `tabs` en `components/ui`; `DataTable` sólo exporta primitivas de tabla.
10. **`Product.Code` distingue mayúsculas** (`IX_products_tenant_code`), y el escaneo es exacto (spec, decisión 30).

## Decisiones que toma este plan (el owner duerme; se anotan en el handoff)

- **P1.** La migración se llama `InitialPos` (pedido explícito del encargo), no `CreatePosSchema` como dice el spec. El DDL es el mismo.
- **P2.** `PosSale.Create` **no** recibe el número: lo asigna `PosSale.AssignNumber(long)` adentro de la transacción, después de todas las validaciones. Es la única forma de cumplir a la vez la firma del spec y «el número se pide después de todas las validaciones».
- **P3.** Código de dominio nuevo `pos.sale.void_reason_required` para el motivo vacío en `PosSale.Void` (el spec pide «motivo obligatorio» y no le da código; por HTTP lo tapa el validador con `validation.failed`).
- **P4.** Las respuestas BFF (`PosSaleResponse`, `RegisterContextResponse`, …) viven en `Modules.Pos.Application/PosDtos.cs` y los endpoints las devuelven tal cual; los enums viajan como `string` (`Status = sale.Status.ToString()`), sin depender de un conversor global.
- **P5.** `zeroTotalNotAllowed` es `true` sólo si hay al menos una línea vendible: un carrito sólo con líneas no vendibles ya bloquea «Cobrar» por su propio motivo.
- **P6.** `CashSession.RegisterVoid` resta 1 a `SalesCount` («resta lo mismo que sumó»): `SalesCount` cuenta ventas completadas y `VoidedCount` las anuladas.
- **P7.** El carrito es una **fábrica de stores por clave** (`getPosCartStore(tenantId, userId)`), con `persist` sobre `sessionStorage`: el store de una clave se crea sólo cuando tenant y usuario existen, e hidrata sincrónicamente al crearse. Es la forma concreta de «no se hidrata mientras la clave no exista» del spec.
- **P8.** El preview exige 1..200 líneas (el frontend no lo pide con el carrito vacío).
- **P9.** Textos de los permisos (spec da las etiquetas, no las descripciones): ver Task B2.
- **P10.** `PosProductLookup` se prueba por HTTP (Task B12, `by-code` exacto, mayúsculas, inactivo, sin precio) y no con dobles de `IProductRepository` en `Bootstrapper.UnitTests`: el comportamiento que importa (exactitud y mayúsculas) es del repositorio, y lo cubre además la Task B7.
- **P11.** Una referencia de pago en una línea `Cash` es `validation.failed` (campo `payments[i].reference`); el dominio la descarta si llega igual.
- **P12.** Todos los inputs de dinero del POS (base, recibido, monto de tarjeta o transferencia, efectivo contado) son `PosMoneyInput` (F8) y no el `CurrencyInput` compartido que el spec nombra para la base: el guardia de ráfagas necesita el `keydown` y el `paste` del input.
- **P13.** `adoptSession` devuelve `boolean` (si cerró un cobro abierto) en vez de `void`, para que la pantalla muestre «La caja cambió mientras cobrabas»; y el carrito lleva un `lineId` por línea (el spec tipa `selectedLineId` sin decir cómo se identifica una línea, y el mismo producto puede estar en dos).
- **P14.** Orden del track frontend: ticket (F10) → apertura y cierre (F11) → cobro (F12) → pantalla de caja (F13) → listados (F14), para que cada tarea componga piezas ya probadas. Las páginas de F9 nacen como cascarones con su título y F11-F14 las reemplazan.

## Contrato HTTP

El contrato es la sección «API» del spec (endpoints 1-12, cuerpos de ejemplo, códigos). El frontend lo implementa en Task F1 sin esperar al backend. Lo único que este plan fija además: los enums viajan como texto (`"Open"`, `"Completed"`, `"Cash"`, `"Inactive"`), las fechas `*Local` con offset (`2026-10-07T08:02:11-05:00`), y `POST /pos/sales` responde `201` con `Location` o `200` en la repetición, con el mismo cuerpo.

---

## File Structure

### Backend (`qep-backend`)

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/Modules/Pos/Modules.Pos.Domain/Modules.Pos.Domain.csproj` | B1 | Dominio (sólo `BuildingBlocks.Domain`) |
| `src/Modules/Pos/Modules.Pos.Application/Modules.Pos.Application.csproj` | B1 | Casos de uso (Domain, Tenancy.Application, BuildingBlocks.Application, FluentValidation) |
| `src/Modules/Pos/Modules.Pos.Infrastructure/Modules.Pos.Infrastructure.csproj` | B1 | EF Core + Npgsql |
| `src/Modules/Pos/Modules.Pos.Api/Modules.Pos.Api.csproj` | B1 | Endpoints |
| `tests/Modules/Pos/Modules.Pos.UnitTests/Modules.Pos.UnitTests.csproj` | B1 | Unitarias de dominio y aplicación |
| `tests/Modules/Pos/Modules.Pos.IntegrationTests/Modules.Pos.IntegrationTests.csproj` | B1 | Testcontainers + API |
| `tests/ArchitectureTests/ArchitectureTests/PosLayerTests.cs` | B1 | Capas del módulo |
| `src/Modules/Pos/Modules.Pos.Domain/PosDomainException.cs` | B1 | Excepción de dominio |
| `src/Modules/Pos/Modules.Pos.Application/IPosUnitOfWork.cs` | B1 | Unidad de trabajo con transacción y `ResetAsync` |
| `src/Modules/Pos/Modules.Pos.Infrastructure/PosInfrastructureExtensions.cs` | B1, B6 | `AddPosInfrastructure` |
| `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs` | B1, B12 | `MapPosEndpoints` |
| `src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs` | B2 | Las seis constantes |
| `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs` | B3 | Fórmula de línea compartida |
| `src/Modules/Pos/Modules.Pos.Domain/{PosIds,MemberId,PosEnums,PosLimits,PosFinalConsumer,PosCompanySnapshot}.cs` | B3 | Tipos |
| `src/Modules/Pos/Modules.Pos.Domain/CashSession.cs` | B4 | Agregado caja |
| `src/Modules/Pos/Modules.Pos.Domain/{PosSale,PosSaleLine,PosPayment,PosSaleInputs,PosSaleNumber,PosVoidability}.cs` | B5 | Agregado venta |
| `src/Modules/Pos/Modules.Pos.Application/{ICashSessionRepository,IPosSaleRepository,IPosSaleNumberGenerator,IPosAuditPublisher}.cs` | B6 | Puertos de persistencia |
| `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/{PosDbContext,PosDbContextFactory,PosOutboxMessage,PosSaleNumberCounter,PosUnitOfWork,CashSessionRepository,PosSaleRepository,PosSaleNumberGenerator,PosAuditPublisher,PosUserReferenceProbe}.cs` | B6 | Persistencia |
| `src/Modules/Pos/Modules.Pos.Infrastructure/PosDatabaseInitializer.cs` | B6 | `InitializePosDatabaseAsync` |
| `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/<ts>_InitialPos.cs` (+ `.Designer.cs`, snapshot) | B6 | Generados |
| `src/Modules/Pos/Modules.Pos.Application/{PosAuthorization,PosCashierResolver,PosNotFound,PosDtos,IPosCompanyLookup,IPosCashierLookup,PosSessionMapping}.cs` | B8 | Autorización, DTOs, puertos |
| `src/Modules/Pos/Modules.Pos.Application/{GetRegisterContext,OpenCashSession,CloseCashSession}.cs` | B8 | Caja |
| `src/Modules/Pos/Modules.Pos.Application/{IPosProductLookup,PosProductMapping,SearchPosProducts,FindPosProductByCode,PreviewPosSale}.cs` | B9 | Productos y preview |
| `src/Modules/Pos/Modules.Pos.Application/{PosSaleFingerprint,PosSaleResponses,CreatePosSale}.cs` | B10 | Venta |
| `src/Modules/Pos/Modules.Pos.Application/{PosListFilters,PosScope,GetPosSale,ListPosSales,VoidPosSale,ListCashSessions,GetCashSession}.cs` | B11 | Lecturas y anulación |
| `src/Bootstrapper/{PosProductLookup,PosCompanyLookup,PosCashierLookup}.cs` | B12 | Adaptadores |
| `tests/Modules/Pos/Modules.Pos.UnitTests/{PosDomainExceptionTests,VatIncludedLineTests,PosTypesTests,PosFixtures,CashSessionTests,PosSaleTests,PosTestDoubles,RegisterHandlersTests,ProductAndPreviewHandlersTests,PosSaleFingerprintTests,CreatePosSaleHandlerTests,SaleReadAndVoidHandlersTests}.cs` | B1-B11 | Unitarias |
| `tests/Modules/Pos/Modules.Pos.IntegrationTests/{PosApiHarness,PosPersistenceTests,PosWorld,PosRegisterApiTests,PosSaleApiTests,PosIsolationApiTests}.cs` | B6, B12, B13 | Integración |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `Backend.slnx` | B1 | Seis proyectos en `/src/Modules/Pos/` y `/tests/Modules/Pos/` |
| `src/Api/Api.csproj`, `src/Bootstrapper/Bootstrapper.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` | B1 | Referencias a Pos |
| todos los `packages.lock.json` afectados | B1 | Regenerados |
| `src/Api/Program.cs` | B1, B6 | `MapPosEndpoints` (`:141`), `InitializePosDatabaseAsync` (después de `:179`) |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | B1, B2, B8-B12 | Infraestructura, permisos, políticas, roles, handlers, validadores, adaptadores |
| `src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs` | B2 | `Cashier` |
| `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs` | B3 | `Apply` llama `VatIncludedLine.Compute` |
| `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs`, `.../Persistence/ProductRepository.cs` | B7 | `FindByCodeAsync` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` | B2 | Prueba nueva + acota la de `"sale"` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RoleApiTests.cs`, `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleTests.cs` | B2 | `cashier` reservado |
| `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductDetailsApiTests.cs` | B7 | `FindByCodeAsync` exacto |
| `CLAUDE.md` | B14 | «Los trece módulos», con Pos |

### Frontend (`qep-frontend`)

**Crear** (todo bajo `src/features/pos/` salvo indicación)

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `types/pos.ts` | F1 | DTOs del contrato, `POS_PERMISSIONS`, `PAYMENT_METHOD_LABELS`, `VOID_BLOCKED_REASON_LABELS` |
| `services/pos.api.ts` | F1 | Llamadas, `POS_CODE_MESSAGES`, `describePosFailure` |
| `utils/money-cents.ts`, `utils/format-pos-money.ts`, `utils/quick-cash-amounts.ts` | F2 | Dinero en centavos |
| `utils/build-payments.ts` | F3 | Cuerpo de pagos |
| `utils/sale-attempt-outcome.ts` | F4 | Clasificación de la respuesta del POST |
| `stores/pos-cart-store.ts`, `hooks/use-pos-cart.tsx` | F5 | Carrito persistido y su contexto |
| `src/lib/print-with-title.ts` (movido) | F6 | Impresión con título |
| `hooks/use-register-context.ts`, `use-pos-product-search.ts`, `use-product-by-code.ts`, `use-sale-preview.ts`, `use-create-sale.ts`, `use-void-sale.ts`, `use-open-session.ts`, `use-close-session.ts`, `use-pos-sales.ts`, `use-pos-sessions.ts`, `use-pos-sale.ts`, `use-pos-session.ts` | F7 | Server state |
| `hooks/use-scan-queue.ts`, `use-scanner-burst-guard.ts`, `use-pos-shortcuts.ts`, `components/pos-money-input.tsx` | F8 | Lector, input de dinero y atajos |
| `components/pos-gate.tsx`, cascarones de `pages/`, `src/routes/_authenticated/pos.tsx`, `pos/index.tsx`, `pos/close.tsx`, `pos/sales/index.tsx`, `pos/sessions/index.tsx` | F9 | Gates y rutas |
| `components/{pos-ticket,ticket-print-portal}.tsx`, `pos-print.css`, `utils/{format-local-stamp,pos-sale.fixture}.ts` | F10 | Ticket |
| `components/{open-session-form,close-session-form,closing-summary}.tsx`, `pages/pos-close-session-page.tsx` | F11 | Apertura y cierre |
| `components/{pay-dialog,payment-method-buttons,cash-quick-amounts,payment-lines}.tsx`, `hooks/use-pay-flow.ts` | F12 | Cobro |
| `pages/pos-register-page.tsx`, `components/{register-header,product-search-bar,product-grid,product-card,cart-panel,cart-line,quantity-stepper,line-discount-popover,cart-totals,sale-success-panel}.tsx`, `hooks/use-expected-refresh.ts`, `utils/charge-state.ts` | F13 | Pantalla de caja |
| `components/{sales-table,void-sale-dialog,sessions-table}.tsx`, `pages/{pos-sales-page,pos-sessions-page}.tsx`, `index.ts` | F14 | Listados y barril |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `src/features/orders/pages/order-detail-page.tsx`, `src/features/quotes/components/created-order-summary.tsx` | F6 | Importan `@/lib/print-with-title` |
| `src/components/app-shell/sidebar-nav-items.ts`, `src/test/permissions.ts` | F9 | Ítem «Punto de venta» |
| `src/features/auth/services/landing.ts` | F9 | `/pos` para el cajero |
| `src/routeTree.gen.ts` | F9 | Regenerado |

## Entrega

Los dos tracks son independientes hasta la Task F15 (humo contra la API real). Orden sugerido con dos ejecutores: **B0-B14** en un hilo, **F0-F15** en otro.

| Commit | Tarea |
|---|---|
| `docs(pos): spec y plan del módulo POS` | B0 |
| `build(pos): proyectos del módulo Pos y pruebas de capas` | B1 |
| `feat(pos): permisos pos.* y rol de sistema cajero` | B2 |
| `refactor(quotations): fórmula de línea con IVA incluido compartida en BuildingBlocks` | B3 |
| `feat(pos): agregado CashSession` | B4 |
| `feat(pos): agregado PosSale con líneas, pagos y anulación` | B5 |
| `feat(pos): persistencia del módulo Pos y migración InitialPos` | B6 |
| `feat(catalog): búsqueda exacta de producto por código` | B7 |
| `feat(pos): abrir, cerrar y consultar la caja` | B8 |
| `feat(pos): búsqueda de productos y preview de la venta` | B9 |
| `feat(pos): crear venta idempotente por id de cliente` | B10 |
| `feat(pos): lectura, listado y anulación de ventas y cajas` | B11 |
| `feat(pos): endpoints y adaptadores del punto de venta` | B12 |
| `test(pos): integración de idempotencia, concurrencia, aislamiento y auditoría` | B13 |
| `docs(pos): módulo Pos en las reglas del repositorio` | B14 |
| `docs(pos): spec y plan del módulo POS` | F0 |
| `feat(pos): tipos y servicio del punto de venta` | F1 |
| `feat(pos): aritmética de dinero en centavos` | F2 |
| `feat(pos): armado de pagos del cobro` | F3 |
| `feat(pos): clasificación del resultado del cobro` | F4 |
| `feat(pos): carrito persistido por tenant y usuario` | F5 |
| `refactor(print): printWithTitle pasa a src/lib` | F6 |
| `feat(pos): hooks de datos del punto de venta` | F7 |
| `feat(pos): cola de escaneo, guardia de ráfagas y atajos` | F8 |
| `feat(pos): rutas, gate, menú y aterrizaje del punto de venta` | F9 |
| `feat(pos): ticket de 80 mm` | F10 |
| `feat(pos): apertura y cierre de caja` | F11 |
| `feat(pos): diálogo de cobro` | F12 |
| `feat(pos): pantalla de caja` | F13 |
| `feat(pos): listados de ventas y cajas` | F14 |

---

# Track backend (`qep-backend`)

### Task B0: Worktree, precondición, línea base y documentos

**Files:**
- Create: `docs/superpowers/specs/2026-10-07-modulo-pos-design.md` (copia del spec), `docs/superpowers/plans/2026-10-07-modulo-pos.md` (copia de este plan)

**Interfaces:**
- Consumes: la rama `feature/modulos-por-tenant` con el spec de entitlements implementado.
- Produces: el worktree `qep-backend-worktrees\pos` en `feature/pos`, su índice de CodeGraph, la lista de pruebas que ya fallan antes de POS (`$env:TEMP\qep-pos-baseline-failed.txt`) y el commit de documentos.

- [ ] **Step 1: Crear el worktree desde la rama de entitlements**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git fetch origin
git branch -a --list "*modulos-por-tenant*"
```

Esperado: aparece `feature/modulos-por-tenant` o `remotes/origin/feature/modulos-por-tenant`. **Si no aparece ninguna, para**: POS no se empieza antes que entitlements (spec, «Despliegue»). Con la rama local:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git worktree add ..\qep-backend-worktrees\pos -b feature/pos feature/modulos-por-tenant
```

(Si sólo existe la remota, el último argumento es `origin/feature/modulos-por-tenant`.)

- [ ] **Step 2: Comprobar que entitlements está de verdad**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
git branch --show-current
git grep -n "TenantModuleKey Pos" -- src
git grep -n "RequiredModules" -- src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs
git grep -n "tenant_modules" -- src/Modules/Tenancy
git grep -n "RequiredModules" -- tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs
```

Esperado: rama `feature/pos`; las cuatro búsquedas devuelven al menos una línea. Si `TenantModuleKeys.Pos` se declara con otra forma, anota la línea real y úsala en la Task B2. Si alguna búsqueda viene vacía, **para y pregunta**.

- [ ] **Step 3: Índice de CodeGraph propio del worktree**

```powershell
gentle-ai codegraph init --cwd C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
```

Esperado: crea `.codegraph/` en el worktree. Nunca se copia el del checkout principal. (Para borrar el worktree al final, primero hay que cerrar el `codegraph serve` que lo tenga abierto.)

- [ ] **Step 4: Restore, build y línea base de la suite completa**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
docker info --format "{{.ServerVersion}}"
dotnet ef --version
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
$baseline = Join-Path $env:TEMP "qep-pos-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $baseline
```

Esperado: build `0 Advertencia(s)`, `0 Errores`. La suite puede tener fallas previas (por ejemplo las dos de `OrderExportApiTests` del NIT). Corre en primer plano; tarda.

- [ ] **Step 5: Guardar los nombres de las pruebas que ya fallan**

```powershell
$baseline = Join-Path $env:TEMP "qep-pos-baseline"
$failed = Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique
$failed | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-pos-baseline-failed.txt")
"Fallas previas: {0}" -f @($failed).Count
```

Esperado: un número (puede ser 0). `-LiteralPath` porque los `.trx` pueden llevar corchetes en el nombre y `Get-Content` sin él los salta.

- [ ] **Step 6: Copiar spec y plan, y commitear**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
$scratch = "C:\Users\andre\AppData\Local\Temp\claude\c--Users-andre-OneDrive-Documentos2-repositories-QCode-templates-qep-qep-backend\b6ad7612-c9ff-4dff-a4c1-51df3b0a1bcb\scratchpad"
New-Item -ItemType Directory -Force docs\superpowers\specs, docs\superpowers\plans | Out-Null
Copy-Item "$scratch\specs\2026-10-07-modulo-pos-design.md" docs\superpowers\specs\2026-10-07-modulo-pos-design.md
Copy-Item "$scratch\plans\2026-10-07-modulo-pos.md" docs\superpowers\plans\2026-10-07-modulo-pos.md
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add docs/superpowers/specs/2026-10-07-modulo-pos-design.md docs/superpowers/plans/2026-10-07-modulo-pos.md; git commit -m "docs(pos): spec y plan del módulo POS"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: sin salida en la última línea. Si el scratchpad ya no existe, pide los dos archivos al owner antes de seguir.

---

### Task B1: Proyectos del módulo Pos y pruebas de capas

**Files:**
- Create: los seis `.csproj` de la tabla, `PosDomainException.cs`, `IPosUnitOfWork.cs`, `PosInfrastructureExtensions.cs`, `PosEndpoints.cs`, `tests/ArchitectureTests/ArchitectureTests/PosLayerTests.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/PosDomainExceptionTests.cs`
- Modify: `Backend.slnx`, `src/Api/Api.csproj:12-13`, `src/Bootstrapper/Bootstrapper.csproj:16-17`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj:26-33`, `src/Api/Program.cs:141`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:450`, los `packages.lock.json` que cambien

**Interfaces:**
- Consumes: nada.
- Produces:
  - `namespace Modules.Pos.Domain; public sealed class PosDomainException(string code, string message) : DomainException(code, message);`
  - `namespace Modules.Pos.Application; public interface IPosUnitOfWork { Task<int> SaveChangesAsync(CancellationToken); Task<IPosTransaction> BeginTransactionAsync(CancellationToken); Task ResetAsync(CancellationToken); }` y `public interface IPosTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken); }`
  - `Modules.Pos.Infrastructure.PosInfrastructureExtensions.AddPosInfrastructure(this IServiceCollection, IConfiguration)`
  - `Modules.Pos.Api.PosEndpoints.MapPosEndpoints(this IEndpointRouteBuilder)`

- [ ] **Step 1: Escribir las pruebas de capas que fallan**

Crea `tests/ArchitectureTests/ArchitectureTests/PosLayerTests.cs`:

```csharp
using System.Reflection;
using Modules.Pos.Api;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Pos.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Pos, copia de <see cref="CompaniesLayerTests"/>. La regla que más importa es la
/// última: Pos lee productos, empresas y cajeros de otros módulos, y todo eso entra por puertos con
/// adaptador en Bootstrapper (spec 2026-10-07, «Aplicación»). Sin esta aserción, el primero que
/// necesite un producto agrega el ProjectReference a Catalog y nada se pone rojo.
/// </summary>
public sealed class PosLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(PosDomainException).Assembly,
            typeof(IPosUnitOfWork).Assembly,
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(IPosUnitOfWork).Assembly,
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    // Traducir errores de base (23505 del índice parcial, PK_sales, concurrencia) es tarea de
    // PosUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(IPosUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(IPosUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pos", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(
        Assembly source,
        params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
```

En `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`, junto a las cuatro referencias de Companies (`:26-33`):

```xml
    <ProjectReference Include="..\..\..\src\Modules\Pos\Modules.Pos.Api\Modules.Pos.Api.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pos\Modules.Pos.Application\Modules.Pos.Application.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pos\Modules.Pos.Domain\Modules.Pos.Domain.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Pos\Modules.Pos.Infrastructure\Modules.Pos.Infrastructure.csproj" />
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/ArchitectureTests/ArchitectureTests
```

Esperado: FAIL de restore/compilación por los proyectos de Pos inexistentes (`MSB3202` o `NU1105`). RED por tipo inexistente; se anota así.

- [ ] **Step 3: Crear los cuatro proyectos de `src`**

`src/Modules/Pos/Modules.Pos.Domain/Modules.Pos.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Domain\BuildingBlocks.Domain.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Pos/Modules.Pos.Application/Modules.Pos.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- PosAuthorization, PosCashierResolver y PosSaleFingerprint son internal: se prueban
         unitariamente. Mismo patron que Companies. -->
    <InternalsVisibleTo Include="Modules.Pos.UnitTests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Pos.Domain\Modules.Pos.Domain.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FluentValidation" />
  </ItemGroup>
</Project>
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Modules.Pos.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\Modules.Pos.Application\Modules.Pos.Application.csproj" />
    <ProjectReference Include="..\Modules.Pos.Domain\Modules.Pos.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
  <ItemGroup>
    <!-- PosUnitOfWork es internal; la prueba determinista de concurrencia (spec, «Pruebas») lo
         usa directo contra dos DbContext. -->
    <InternalsVisibleTo Include="Modules.Pos.UnitTests" />
    <InternalsVisibleTo Include="Modules.Pos.IntegrationTests" />
  </ItemGroup>
</Project>
```

`src/Modules/Pos/Modules.Pos.Api/Modules.Pos.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Pos.Application\Modules.Pos.Application.csproj" />
    <ProjectReference Include="..\Modules.Pos.Domain\Modules.Pos.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Pos/Modules.Pos.Domain/PosDomainException.cs`:

```csharp
using BuildingBlocks.Domain;

namespace Modules.Pos.Domain;

/// <summary>Regla de negocio del punto de venta: ApiExceptionHandler la responde 422 con su código.</summary>
public sealed class PosDomainException(string code, string message) : DomainException(code, message);
```

`src/Modules/Pos/Modules.Pos.Application/IPosUnitOfWork.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>
/// Unidad de trabajo del módulo. Además de guardar, abre la transacción en la que corre el
/// contador de números (spec, «Crear venta», paso 7) y deja limpiar el contexto después de un
/// choque al guardar, para releer sin arrastrar las entidades del intento fallido (paso 8).
/// </summary>
public interface IPosUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Revierte la transacción abierta, si la hay, y suelta todo lo rastreado.</summary>
    Task ResetAsync(CancellationToken cancellationToken);
}

public interface IPosTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/PosInfrastructureExtensions.cs` (la Task B6 lo completa):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Pos.Infrastructure;

public static class PosInfrastructureExtensions
{
    public static IServiceCollection AddPosInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar
        // en el primer request con un error de Npgsql que no explica nada.
        _ = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        return services;
    }
}
```

`src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs` (la Task B12 lo completa):

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Modules.Pos.Api;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como companies y catalog. Cada endpoint declara su propio
        // RequireAuthorization (spec, «API»): el grupo no lleva política.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/pos")
            .WithTags("Pos");

        return endpoints;
    }
}
```

- [ ] **Step 4: Crear los dos proyectos de pruebas y su primera prueba**

`tests/Modules/Pos/Modules.Pos.UnitTests/Modules.Pos.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Modules\Pos\Modules.Pos.Application\Modules.Pos.Application.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Pos\Modules.Pos.Domain\Modules.Pos.Domain.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Pos\Modules.Pos.Infrastructure\Modules.Pos.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Pos/Modules.Pos.IntegrationTests/Modules.Pos.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Api\Api.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Pos/Modules.Pos.UnitTests/PosDomainExceptionTests.cs`:

```csharp
using BuildingBlocks.Domain;
using Modules.Pos.Domain;

namespace Modules.Pos.UnitTests;

public sealed class PosDomainExceptionTests
{
    // ApiExceptionHandler responde 422 a todo DomainException con su Code: si PosDomainException
    // no heredara de ahí, cada regla del POS saldría como 500.
    [Fact]
    public void IsADomainExceptionThatCarriesItsCode()
    {
        DomainException error = new PosDomainException("pos.session.not_open", "No open session.");

        Assert.Equal("pos.session.not_open", error.Code);
    }
}
```

- [ ] **Step 5: Sumar los proyectos a la solución, al host y al composition root**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
dotnet sln Backend.slnx add --solution-folder src/Modules/Pos src/Modules/Pos/Modules.Pos.Api/Modules.Pos.Api.csproj src/Modules/Pos/Modules.Pos.Application/Modules.Pos.Application.csproj src/Modules/Pos/Modules.Pos.Domain/Modules.Pos.Domain.csproj src/Modules/Pos/Modules.Pos.Infrastructure/Modules.Pos.Infrastructure.csproj
dotnet sln Backend.slnx add --solution-folder tests/Modules/Pos tests/Modules/Pos/Modules.Pos.UnitTests/Modules.Pos.UnitTests.csproj tests/Modules/Pos/Modules.Pos.IntegrationTests/Modules.Pos.IntegrationTests.csproj
Select-String -Path Backend.slnx -Pattern "Pos"
```

Esperado: seis líneas `<Project Path="…Pos…" />` dentro de `<Folder Name="/src/Modules/Pos/">` y `<Folder Name="/tests/Modules/Pos/">`. Si `dotnet sln` creó la carpeta con otro nombre, edita `Backend.slnx` a mano para que quede como las de Companies (`Backend.slnx:29-34` y `:110-113`).

En `src/Api/Api.csproj`, junto a las de Companies (`:12-13`):

```xml
    <ProjectReference Include="..\Modules\Pos\Modules.Pos.Api\Modules.Pos.Api.csproj" />
    <ProjectReference Include="..\Modules\Pos\Modules.Pos.Infrastructure\Modules.Pos.Infrastructure.csproj" />
```

En `src/Bootstrapper/Bootstrapper.csproj`, junto a las de Companies (`:16-17`):

```xml
    <ProjectReference Include="..\Modules\Pos\Modules.Pos.Application\Modules.Pos.Application.csproj" />
    <ProjectReference Include="..\Modules\Pos\Modules.Pos.Infrastructure\Modules.Pos.Infrastructure.csproj" />
```

En `src/Api/Program.cs`, agrega `using Modules.Pos.Api;` junto a los demás `using Modules.*.Api;` y, después de `app.MapPlatformEndpoints();` (`:141`):

```csharp
app.MapPosEndpoints();
```

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega `using Modules.Pos.Infrastructure;` y, después de `services.AddPlatformInfrastructure(configuration);` (`:450`):

```csharp
        // Pos (spec 2026-10-07): su único vecino directo es Tenancy; productos, empresas y
        // cajeros entran por adaptadores que se registran más abajo, con los demás.
        services.AddPosInfrastructure(configuration);
```

- [ ] **Step 6: Regenerar los lock files y revisar su diff**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
dotnet restore Backend.slnx --force-evaluate
git status --short -- '*packages.lock.json'
git diff -- '*packages.lock.json' | Select-String -Pattern '"resolved"'
dotnet restore Backend.slnx --locked-mode
```

Esperado: seis lock files nuevos (`??`) y modificados los de `Api`, `Bootstrapper`, `ArchitectureTests` y cada `*.IntegrationTests` que referencia `Api.csproj`. El tercer comando **no** debe mostrar ninguna línea `+`/`-` con `"resolved"`: si muestra una, el `--force-evaluate` cambió la versión de un paquete ajeno a este cambio; **para**, anótalo y pregunta. El último restore pasa en modo bloqueado, que es lo que corre el `Dockerfile` (`NU1004` si un lock quedó atrás).

- [ ] **Step 7: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build
```

Esperado: build `0 Advertencia(s)`, `0 Errores`; PASS en las dos (las cinco de `PosLayerTests` y la de `PosDomainExceptionTests`).

- [ ] **Step 8: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
$locks = @(git ls-files --modified --others --exclude-standard -- '*packages.lock.json')
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add Backend.slnx src/Modules/Pos tests/Modules/Pos tests/ArchitectureTests/ArchitectureTests/PosLayerTests.cs tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj src/Api/Api.csproj src/Api/Program.cs src/Bootstrapper/Bootstrapper.csproj src/Bootstrapper/QepServiceCollectionExtensions.cs $locks; git commit -m "build(pos): proyectos del módulo Pos y pruebas de capas"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas (`bin/` y `obj/` ya están ignorados).

---

### Task B2: Permisos `pos.*` y rol de sistema `cashier`

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs`
- Modify: `src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol admin `:635`, rol nuevo después de billing `:710`, definiciones después de la última `PermissionDefinition` `:935`, políticas al final de la cadena `:1170`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RoleApiTests.cs`, `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleTests.cs`, el ancla de conteo de `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Consumes: `TenantModuleKeys.Pos` y `PermissionDefinition(..., IReadOnlyCollection<TenantModuleKey>? RequiredModules)` (entitlements).
- Produces: `public static class PosPermissions { SaleRead = "pos.sale.read"; SaleCreate = "pos.sale.create"; SaleVoid = "pos.sale.void"; SaleDiscount = "pos.sale.discount"; RegisterOperate = "pos.register.operate"; RegisterRead = "pos.register.read"; }`, seis políticas con el mismo nombre, `SystemRoleKeys.Cashier = "cashier"`, y el rol `cashier`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `AuthorizationCatalogApiTests.cs`, dentro de `TheCatalogNamesTheOrderPermissions`, reemplaza la aserción de `"sale"` (`:81-82`) por:

```csharp
        // Los únicos permisos de venta son los del punto de venta (spec 2026-10-07): la regla
        // sigue cuidando que ningún permiso de pedidos se llame "sale".
        Assert.DoesNotContain(
            catalog.Permissions,
            permission => permission.Permission.Contains("sale", StringComparison.Ordinal)
                && !permission.Permission.StartsWith("pos.", StringComparison.Ordinal));
```

y agrega, después de esa prueba:

```csharp
    /// <summary>
    /// Spec 2026-10-07 (módulo POS): seis permisos propios, todos de admin; el rol de sistema
    /// `cashier` vende, abre y cierra su caja y lee sus ventas, sin descontar ni anular
    /// (decisión 4). Asesor y facturación no ven el punto de venta.
    /// </summary>
    [Fact]
    public async Task TheCatalogNamesThePosPermissionsAndTheCashierRole()
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
            new("pos.register.operate", "Abrir y cerrar su caja",
                "Permite abrir y cerrar la caja propia del punto de venta.", "Pos", "medium"),
            new("pos.register.read", "Ver todas las cajas y ventas",
                "Permite consultar las cajas y las ventas de todos los cajeros del tenant.", "Pos", "medium"),
            new("pos.sale.create", "Vender en caja",
                "Permite registrar ventas en el punto de venta con la caja propia abierta.", "Pos", "medium"),
            new("pos.sale.discount", "Dar descuentos en caja",
                "Permite dar descuentos por línea y cobrar ventas en $0 en el punto de venta.", "Pos", "high"),
            new("pos.sale.read", "Ver ventas y cierres de caja",
                "Permite consultar las ventas del punto de venta y los cierres de caja propios.", "Pos", "low"),
            new("pos.sale.void", "Anular ventas de caja",
                "Permite anular, con un motivo, una venta cuya caja sigue abierta.", "Pos", "high"),
        ];
        Assert.Equal(
            expected,
            catalog.Permissions
                .Where(permission => permission.Permission.StartsWith("pos.", StringComparison.Ordinal))
                .OrderBy(permission => permission.Permission, StringComparer.Ordinal));
        Assert.Equal(
            expected.Select(permission => permission.Permission).ToArray(),
            PermissionsOf(catalog, "admin", "pos."));
        Assert.Equal(
            ["pos.register.operate", "pos.sale.create", "pos.sale.read"],
            catalog.Roles.Single(role => role.Role == "cashier").Permissions
                .Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(PermissionsOf(catalog, "advisor", "pos."));
        Assert.Empty(PermissionsOf(catalog, "billing", "pos."));
    }
```

En `RoleTests.cs`, después de `CreateRejectsAKeyThatCollidesWithASystemRole`:

```csharp
    // Spec 2026-10-07: `cashier` es rol de sistema desde POS. Un custom con esa clave pisaría al
    // de sistema en TenantRoleCatalog sin avisar.
    [Fact]
    public void CreateRejectsTheCashierKey()
    {
        var error = Assert.Throws<AuthorizationDomainException>(() => Role.Create(
            RoleId.New(),
            Tenant,
            "cashier",
            "Mi cajero",
            "Descripcion",
            ["advisorship.read"],
            DateTimeOffset.UnixEpoch));

        Assert.Equal("authorization.role.key_reserved", error.Code);
    }
```

En `RoleApiTests.cs`, después de `CreateWithASystemRoleKeyIsRejected` (`:132-145`):

```csharp
    [Fact]
    public async Task CreateWithTheCashierKeyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);

        var response = await CreateAsync(client, "cashier", "Mi cajero");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("authorization.role.key_reserved", body, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~RoleTests.CreateRejectsTheCashierKey"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests|FullyQualifiedName~RoleApiTests.CreateWithTheCashierKeyIsRejected"
```

Esperado: FAIL en `CreateRejectsTheCashierKey` (`Assert.Throws() Failure: No exception was thrown`), en `TheCatalogNamesThePosPermissionsAndTheCashierRole` (`Collections differ`) y en `CreateWithTheCashierKeyIsRejected` (`Expected: UnprocessableEntity / Actual: Created`). `TheCatalogNamesTheOrderPermissions` sigue verde.

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>
/// Spec 2026-10-07, «Permisos y políticas». Los seis exigen la capacidad `pos` (RequiredModules),
/// y cada uno necesita su política en AddAuthorization: sin ella, RequireAuthorization no resuelve
/// y el síntoma es 500, no 403.
/// </summary>
public static class PosPermissions
{
    public const string SaleRead = "pos.sale.read";
    public const string SaleCreate = "pos.sale.create";
    public const string SaleVoid = "pos.sale.void";

    /// <summary>Descuento de línea > 0 y venta en total 0 (spec, decisión 26).</summary>
    public const string SaleDiscount = "pos.sale.discount";

    public const string RegisterOperate = "pos.register.operate";

    /// <summary>Amplía la lectura a las cajas y ventas de otros cajeros; no la reemplaza.</summary>
    public const string RegisterRead = "pos.register.read";
}
```

En `SystemRoleKeys.cs`, después de `Billing`, y en `All` (conserva los `///` que ya tenga):

```csharp
    /// <summary>Rol de sistema del punto de venta (spec 2026-10-07). Reservado para que un custom
    /// no lo pise en silencio en TenantRoleCatalog.</summary>
    public const string Cashier = "cashier";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Admin, Advisor, Billing, Cashier };
```

En `QepServiceCollectionExtensions.cs` agrega `using Modules.Pos.Application;` (y `using Modules.Tenancy.Domain;` si entitlements no lo dejó). En la `RoleDefinition` de `admin`, el último elemento `PlatformPermissions.RequestLogPurge` (`:635`) pasa a:

```csharp
                PlatformPermissions.RequestLogPurge,
                // Punto de venta (spec 2026-10-07): admin tiene los seis, incluidos descontar y
                // anular, que el cajero no tiene.
                PosPermissions.SaleRead,
                PosPermissions.SaleCreate,
                PosPermissions.SaleVoid,
                PosPermissions.SaleDiscount,
                PosPermissions.RegisterOperate,
                PosPermissions.RegisterRead
```

Después de la `RoleDefinition` de `billing` (tras `:710`, `]));`):

```csharp
        // Spec 2026-10-07, decisión 4: vende, abre y cierra su caja y lee sus ventas. Sin
        // descuentos ni anulación, y sin catálogo ni clientes: /pos/products le da lo que la caja
        // dibuja y el MVP no elige cliente. Un tenant que quiera cajeros con descuento hace un
        // rol custom.
        services.AddSingleton(new RoleDefinition(
            SystemRoleKeys.Cashier,
            "Cajero",
            "Vende en el punto de venta y abre y cierra su propia caja.",
            "Tenancy",
            "medium",
            [
                PosPermissions.SaleRead,
                PosPermissions.SaleCreate,
                PosPermissions.RegisterOperate
            ]));
```

(Si `Modules.Authorization.Domain` no está importado en ese archivo, usa el literal `"cashier"`.)

Después de la última `PermissionDefinition` (`PlatformPermissions.RequestLogPurge`, `:930-935`):

```csharp
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleRead,
            "Ver ventas y cierres de caja",
            "Permite consultar las ventas del punto de venta y los cierres de caja propios.",
            "Pos",
            "low",
            [TenantModuleKeys.Pos]));
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleCreate,
            "Vender en caja",
            "Permite registrar ventas en el punto de venta con la caja propia abierta.",
            "Pos",
            "medium",
            [TenantModuleKeys.Pos]));
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleDiscount,
            "Dar descuentos en caja",
            "Permite dar descuentos por línea y cobrar ventas en $0 en el punto de venta.",
            "Pos",
            "high",
            [TenantModuleKeys.Pos]));
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.SaleVoid,
            "Anular ventas de caja",
            "Permite anular, con un motivo, una venta cuya caja sigue abierta.",
            "Pos",
            "high",
            [TenantModuleKeys.Pos]));
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.RegisterOperate,
            "Abrir y cerrar su caja",
            "Permite abrir y cerrar la caja propia del punto de venta.",
            "Pos",
            "medium",
            [TenantModuleKeys.Pos]));
        services.AddSingleton(new PermissionDefinition(
            PosPermissions.RegisterRead,
            "Ver todas las cajas y ventas",
            "Permite consultar las cajas y las ventas de todos los cajeros del tenant.",
            "Pos",
            "medium",
            [TenantModuleKeys.Pos]));
```

Al final de la cadena de políticas, la de `PlatformPermissions.RequestLogPurge` (`:1168-1170`, termina en `));`) pasa a continuar la cadena:

```csharp
            .AddPolicy(
                PlatformPermissions.RequestLogPurge,
                policy => AddPermissionRequirement(policy, PlatformPermissions.RequestLogPurge))
            .AddPolicy(
                PosPermissions.SaleRead,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleRead))
            .AddPolicy(
                PosPermissions.SaleCreate,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleCreate))
            .AddPolicy(
                PosPermissions.SaleVoid,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleVoid))
            .AddPolicy(
                PosPermissions.SaleDiscount,
                policy => AddPermissionRequirement(policy, PosPermissions.SaleDiscount))
            .AddPolicy(
                PosPermissions.RegisterOperate,
                policy => AddPermissionRequirement(policy, PosPermissions.RegisterOperate))
            .AddPolicy(
                PosPermissions.RegisterRead,
                policy => AddPermissionRequirement(policy, PosPermissions.RegisterRead));
```

En `CompositionRootTests.cs`, si la prueba de completitud del mapa de entitlements fija un conteo exacto de constantes («Ancla: la reflexión encuentra las 35»), súbelo en 6 con el comentario `// +6 de PosPermissions (spec 2026-10-07).`

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build --filter "FullyQualifiedName~RoleTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --filter "FullyQualifiedName~AuthorizationCatalogApiTests|FullyQualifiedName~RoleApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en las cuatro, 0 con error. La completitud del mapa permiso → módulo ve los seis `pos.*` con `RequiredModules = [pos]`.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RoleApiTests.cs tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleTests.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs; git commit -m "feat(pos): permisos pos.* y rol de sistema cajero"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---


### Task B3: Fórmula de línea compartida y tipos del dominio

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`, `src/Modules/Pos/Modules.Pos.Domain/{PosIds,MemberId,PosEnums,PosLimits,PosFinalConsumer,PosCompanySnapshot}.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:243-258` (y `:261-262` si `Round` queda sin uso)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/VatIncludedLineTests.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/PosTypesTests.cs`; red de la extracción: `tests/Modules/Quotations/Modules.Quotations.UnitTests` completo (sin cambios)

**Interfaces:**
- Consumes: `PosDomainException` (B1).
- Produces:
  - `BuildingBlocks.Domain.Pricing.VatIncludedLine.Compute(decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage) : VatIncludedLineAmounts` y `VatIncludedLine.Round(decimal)`; `public readonly record struct VatIncludedLineAmounts(decimal DiscountAmount, decimal TaxAmount, decimal Subtotal) { decimal LineTotal }`
  - `CashSessionId`, `PosSaleId` (rechaza `Guid.Empty` con `pos.sale.id_required`), `PosSaleLineId`, `PosPaymentId`: `readonly record struct` con `Value` y `New()`
  - `public readonly record struct MemberId(Guid Value)`
  - `enum CashSessionStatus { Open, Closed }`, `enum PosSaleStatus { Completed, Voided }`, `enum PosPaymentMethod { Cash, Card, Transfer }`
  - `PosLimits` (`MaxCashAmount`, `MaxCountedCash`, `MaxQuantity`, `MaxLines = 200`, `MaxPayments = 5`, `ReferenceMaxLength = 60`, `NoteMaxLength = 500`, `VoidReasonMinLength = 3`, `HasValidScale(decimal)`)
  - `PosFinalConsumer.Name = "Consumidor final"`, `PosFinalConsumer.IdentificationNumber = "222222222222"`
  - `public sealed record PosCompanySnapshot(Guid CompanyId, string Name, string TaxId, string? Address, string? Phone)`

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pos/Modules.Pos.UnitTests/VatIncludedLineTests.cs`:

```csharp
using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.UnitTests;

/// <summary>
/// La fórmula de QuotationItem.Apply, extraída sin cambios (spec 2026-10-07, «Fórmula de línea
/// compartida»). Los tres primeros casos son el ejemplo trabajado del spec, al centavo.
/// </summary>
public sealed class VatIncludedLineTests
{
    [Theory]
    [InlineData("2", "11900", "10", 19, "2380.00", "3420.00", "18000.00")]
    [InlineData("1.5", "5000", "0", 0, "0", "0", "7500.00")]
    [InlineData("3", "2990", "0", 5, "0", "427.14", "8542.86")]
    // Descuento del 100 %: la línea queda en cero y no hay IVA que extraer.
    [InlineData("2", "11900", "100", 19, "23800.00", "0", "0")]
    // Cantidad con dos decimales y descuento con centavos: 3737.5 bruto, 261.625 de descuento.
    [InlineData("1.25", "2990", "7", 19, "261.63", "554.97", "2920.90")]
    public void ComputeMatchesTheQuotationFormula(
        string quantity, string unitPrice, string discount, int tax,
        string expectedDiscount, string expectedTax, string expectedSubtotal)
    {
        var amounts = VatIncludedLine.Compute(
            decimal.Parse(quantity, CultureInfo.InvariantCulture),
            decimal.Parse(unitPrice, CultureInfo.InvariantCulture),
            decimal.Parse(discount, CultureInfo.InvariantCulture),
            tax);

        Assert.Equal(decimal.Parse(expectedDiscount, CultureInfo.InvariantCulture), amounts.DiscountAmount);
        Assert.Equal(decimal.Parse(expectedTax, CultureInfo.InvariantCulture), amounts.TaxAmount);
        Assert.Equal(decimal.Parse(expectedSubtotal, CultureInfo.InvariantCulture), amounts.Subtotal);
    }

    // AwayFromZero y no ToEven: 0.5 × 0.25 = 0.125 tiene que dar 0.13. Con el redondeo bancario
    // de .NET por defecto daría 0.12 y el ticket no coincidiría con la cotización.
    [Fact]
    public void ThePointFiveCentRoundsAwayFromZero()
    {
        var amounts = VatIncludedLine.Compute(0.5m, 0.25m, 0m, 0);

        Assert.Equal(0.13m, amounts.Subtotal);
        Assert.Equal(0.13m, amounts.LineTotal);
    }

    [Fact]
    public void LineTotalIsSubtotalPlusTax()
    {
        var amounts = VatIncludedLine.Compute(2m, 11_900m, 10m, 19);

        Assert.Equal(21_420m, amounts.LineTotal);
    }
}
```

Agrega `using System.Globalization;` arriba del archivo.

`tests/Modules/Pos/Modules.Pos.UnitTests/PosTypesTests.cs`:

```csharp
using Modules.Pos.Domain;

namespace Modules.Pos.UnitTests;

public sealed class PosTypesTests
{
    // PosSaleId es la clave de idempotencia y la manda el cliente: un Guid vacío no identifica
    // ningún intento.
    [Fact]
    public void ASaleIdCannotBeEmpty()
    {
        var error = Assert.Throws<PosDomainException>(() => new PosSaleId(Guid.Empty));

        Assert.Equal("pos.sale.id_required", error.Code);
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("2.00", true)]
    [InlineData("209.30", true)]
    [InlineData("2.005", false)]
    [InlineData("0.001", false)]
    public void HasValidScaleAcceptsAtMostTwoDecimals(string value, bool expected)
    {
        Assert.Equal(expected, PosLimits.HasValidScale(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheFinalConsumerIsTheSameOneQuotationsUses()
    {
        Assert.Equal("Consumidor final", PosFinalConsumer.Name);
        Assert.Equal("222222222222", PosFinalConsumer.IdentificationNumber);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0234: The type or namespace name 'Pricing' does not exist in the namespace 'BuildingBlocks.Domain'`, `CS0246` para `PosSaleId`, `PosLimits`, `PosFinalConsumer`).

- [ ] **Step 3: Implementar la fórmula y los tipos**

`src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`:

```csharp
namespace BuildingBlocks.Domain.Pricing;

/// <summary>Importes de una línea con IVA incluido en el precio.</summary>
public readonly record struct VatIncludedLineAmounts(
    decimal DiscountAmount, decimal TaxAmount, decimal Subtotal)
{
    /// <summary>Lo que se cobra por la línea, IVA adentro.</summary>
    public decimal LineTotal => Subtotal + TaxAmount;
}

/// <summary>
/// La fórmula de línea de una cotización detal, extraída de QuotationItem.Apply para que POS cobre
/// al centavo lo mismo que cotizaría (spec 2026-10-07, criterio de éxito 3). Dos copias de la
/// fórmula del IVA pueden divergir sin que ninguna prueba lo note; una sola no.
/// </summary>
public static class VatIncludedLine
{
    public static VatIncludedLineAmounts Compute(
        decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage)
    {
        // El precio se carga con IVA incluido: aquí no se suma impuesto, se extrae el que ya viene.
        var gross = quantity * unitPrice;
        var discountAmount = Round(gross * discountPercentage / 100m);

        // Lo que efectivamente se cobra por la línea, IVA adentro.
        var lineTotal = Round(gross) - discountAmount;

        // total × tasa / (100 + tasa), no total × tasa / 100: esa es la de agregar IVA a una base, y
        // sobre un precio que ya lo trae cobraría el impuesto dos veces. Con tasa 0 da 0.
        var taxAmount = Round(lineTotal * taxPercentage / (100m + taxPercentage));

        return new VatIncludedLineAmounts(discountAmount, taxAmount, lineTotal - taxAmount);
    }

    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
```

En `QuotationItem.cs`, agrega `using BuildingBlocks.Domain.Pricing;` y reemplaza el bloque de cálculo (`:243-258`, desde `var gross = quantity * unitPrice;` hasta `Subtotal = lineTotal - TaxAmount;`) por:

```csharp
        // La fórmula vive en BuildingBlocks (spec 2026-10-07): POS la comparte para cobrar al
        // centavo lo mismo que una cotización detal. Las validaciones y los códigos
        // quotation.item.* siguen aquí.
        var amounts = VatIncludedLine.Compute(quantity, unitPrice, discountPercentage, taxPercentage);
        DiscountAmount = amounts.DiscountAmount;
        TaxAmount = amounts.TaxAmount;
        Subtotal = amounts.Subtotal;
```

Deja `UpdatedAt = occurredAt;` donde estaba. Después busca otros usos de `Round(` en `QuotationItem.cs`; si no queda ninguno, borra el `private static decimal Round` (`:261-262`).

`src/Modules/Pos/Modules.Pos.Domain/PosIds.cs`:

```csharp
namespace Modules.Pos.Domain;

public readonly record struct CashSessionId(Guid Value)
{
    public static CashSessionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Id de la venta. Lo genera el cliente (crypto.randomUUID() al abrir el cobro) y es la clave de
/// idempotencia del POST (spec, «Crear venta — idempotencia por id de cliente»).
/// </summary>
public readonly record struct PosSaleId
{
    public PosSaleId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new PosDomainException("pos.sale.id_required", "The sale id is required.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static PosSaleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct PosSaleLineId(Guid Value)
{
    public static PosSaleLineId New() => new(Guid.CreateVersion7());
}

public readonly record struct PosPaymentId(Guid Value)
{
    public static PosPaymentId New() => new(Guid.CreateVersion7());
}
```

`src/Modules/Pos/Modules.Pos.Domain/MemberId.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// La persona en un documento es su membresía, no su usuario (mismo modelo que Quotations). Tipo
/// propio porque el dominio no referencia a Tenancy.
/// </summary>
public readonly record struct MemberId(Guid Value);
```

`src/Modules/Pos/Modules.Pos.Domain/PosEnums.cs`:

```csharp
namespace Modules.Pos.Domain;

// Se persisten y viajan por nombre: el diccionario de etiquetas lo tiene el frontend.
public enum CashSessionStatus
{
    Open,
    Closed,
}

public enum PosSaleStatus
{
    Completed,
    Voided,
}

public enum PosPaymentMethod
{
    Cash,
    Card,
    Transfer,
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosLimits.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// Topes contra errores de digitación, no reglas de negocio (spec, decisión 18). Toda cifra de
/// dinero, descuento o cantidad con más de 2 decimales se rechaza: numeric(14,2) la redondearía en
/// silencio y rompería Σ Amount = Total.
/// </summary>
public static class PosLimits
{
    public const decimal MaxCashAmount = 100_000_000m;
    public const decimal MaxCountedCash = 1_000_000_000m;
    public const decimal MaxQuantity = 99_999m;
    public const int MaxLines = 200;
    public const int MaxPayments = 5;
    public const int ReferenceMaxLength = 60;
    public const int NoteMaxLength = 500;
    public const int VoidReasonMinLength = 3;
    public const int VoidReasonMaxLength = 500;
    public const int CashierNameMaxLength = 320;

    public static bool HasValidScale(decimal value) => value == Math.Round(value, 2);
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosFinalConsumer.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// Copia de las dos constantes de Quotations.FinalConsumer (spec, decisión 11): moverlas obligaría
/// a tocar siete llamadores de Quotations por nada, y el frontend ya las duplica igual.
/// </summary>
public static class PosFinalConsumer
{
    public const string Name = "Consumidor final";
    public const string IdentificationNumber = "222222222222";
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosCompanySnapshot.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// Empresa emisora congelada al abrir la caja (spec, decisión 7): el ticket es el comprobante que
/// se le entregó al cliente y su reimpresión tiene que decir lo mismo.
/// </summary>
public sealed record PosCompanySnapshot(
    Guid CompanyId, string Name, string TaxId, string? Address, string? Phone);
```

- [ ] **Step 4: Ver el GREEN, y la red de Quotations**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build --filter "FullyQualifiedName~VatIncludedLineTests|FullyQualifiedName~PosTypesTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en las cuatro. `Modules.Quotations.UnitTests` entero, sin tocar ninguna de sus pruebas, es la prueba de que la extracción no cambió el comportamiento.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs src/Modules/Pos/Modules.Pos.Domain tests/Modules/Pos/Modules.Pos.UnitTests/VatIncludedLineTests.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosTypesTests.cs; git commit -m "refactor(quotations): fórmula de línea con IVA incluido compartida en BuildingBlocks"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task B4: Agregado `CashSession` (abrir y cerrar)

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Domain/CashSession.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/PosFixtures.cs`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs`

**Interfaces:**
- Consumes: tipos de B3.
- Produces: `public sealed class CashSession` con las propiedades de la tabla del spec («`CashSession`»), `static CashSession Open(CashSessionId id, Guid tenantId, MemberId cashier, string cashierName, PosCompanySnapshot company, decimal openingFloat, DateTimeOffset at)`, `void Close(decimal countedCash, string? note, DateTimeOffset at)`, `decimal LiveExpectedCash { get; }` (= `OpeningFloat + CashTotal`). `RegisterSale`/`RegisterVoid` llegan en B5. `PosFixtures` (pruebas): `Now`, `TenantId`, `Cashier`, `Company`, `OpenSession(...)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pos/Modules.Pos.UnitTests/PosFixtures.cs`:

```csharp
using Modules.Pos.Domain;

namespace Modules.Pos.UnitTests;

/// <summary>Datos del ejemplo trabajado del spec, compartidos por las pruebas del módulo.</summary>
internal static class PosFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 20, 3, TimeSpan.Zero);

    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static readonly MemberId Cashier = new(Guid.Parse("01920000-0000-7000-8000-000000000010"));

    public static readonly PosCompanySnapshot Company = new(
        Guid.Parse("01910000-0000-7000-8000-000000000001"),
        "Origen Botánico SAS",
        "900123456-1",
        "Cra 50 # 10-20, Rionegro",
        "6045551234");

    public static CashSession OpenSession(decimal openingFloat = 100_000m, MemberId? cashier = null) =>
        CashSession.Open(
            CashSessionId.New(),
            TenantId,
            cashier ?? Cashier,
            "Laura Gómez",
            Company,
            openingFloat,
            Now);
}
```

`tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs`:

```csharp
using System.Globalization;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class CashSessionTests
{
    [Fact]
    public void OpenStartsAtVersionOneWithTheCompanySnapshotAndNoSales()
    {
        var session = OpenSession(100_000m);

        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(1, session.Version);
        Assert.Equal(TenantId, session.TenantId);
        Assert.Equal(Cashier, session.CashierId);
        Assert.Equal("Laura Gómez", session.CashierName);
        Assert.Equal(Company.CompanyId, session.CompanyId);
        Assert.Equal("Origen Botánico SAS", session.CompanyName);
        Assert.Equal("900123456-1", session.CompanyTaxId);
        Assert.Equal("Cra 50 # 10-20, Rionegro", session.CompanyAddress);
        Assert.Equal("6045551234", session.CompanyPhone);
        Assert.Equal(100_000m, session.OpeningFloat);
        Assert.Equal(0, session.SalesCount);
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(100_000m, session.LiveExpectedCash);
        Assert.Equal(Now, session.OpenedAt);
        Assert.Null(session.ExpectedCash);
        Assert.Null(session.ClosedAt);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100000000.01")]
    [InlineData("10.005")]
    public void OpenRejectsAFloatOutOfRangeOrWithThreeDecimals(string openingFloat)
    {
        var error = Assert.Throws<PosDomainException>(() =>
            OpenSession(decimal.Parse(openingFloat, CultureInfo.InvariantCulture)));

        Assert.Equal("pos.session.opening_float_invalid", error.Code);
    }

    [Fact]
    public void OpenAcceptsAZeroFloatAndTheTopOne()
    {
        Assert.Equal(0m, OpenSession(0m).OpeningFloat);
        Assert.Equal(PosLimits.MaxCashAmount, OpenSession(PosLimits.MaxCashAmount).OpeningFloat);
    }

    // Faltante con signo negativo (spec, «Ejemplo trabajado»): la pantalla sólo elige el color.
    [Fact]
    public void CloseFreezesExpectedCountedAndASignedDifference()
    {
        var session = OpenSession(100_000m);
        var closedAt = Now.AddHours(8);

        session.Close(99_110m, "  Faltan 890  ", closedAt);

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(100_000m, session.ExpectedCash);
        Assert.Equal(99_110m, session.CountedCash);
        Assert.Equal(-890m, session.CashDifference);
        Assert.Equal("Faltan 890", session.ClosingNote);
        Assert.Equal(closedAt, session.ClosedAt);
        Assert.Equal(2, session.Version);
    }

    [Fact]
    public void CloseWithABlankNoteStoresNoNote()
    {
        var session = OpenSession();

        session.Close(100_000m, "   ", Now);

        Assert.Null(session.ClosingNote);
        Assert.Equal(0m, session.CashDifference);
    }

    [Fact]
    public void ClosingAClosedSessionIsRejected()
    {
        var session = OpenSession();
        session.Close(100_000m, null, Now);

        var error = Assert.Throws<PosDomainException>(() => session.Close(1m, null, Now));

        Assert.Equal("pos.session.not_open", error.Code);
        Assert.Equal(100_000m, session.CountedCash);
        Assert.Equal(2, session.Version);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1000000000.01")]
    [InlineData("1.001")]
    public void CloseRejectsACountOutOfRangeOrWithThreeDecimals(string counted)
    {
        var session = OpenSession();

        var error = Assert.Throws<PosDomainException>(() =>
            session.Close(decimal.Parse(counted, CultureInfo.InvariantCulture), null, Now));

        Assert.Equal("pos.session.counted_cash_invalid", error.Code);
        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(1, session.Version);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación, `CS0246: The type or namespace name 'CashSession' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Domain/CashSession.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// Caja o turno de un cajero (spec 2026-10-07, «CashSession»). Los acumulados se mueven en la
/// misma transacción que cada venta y la venta mueve la Version: si un cierre y una venta se
/// cruzan, el que commitea segundo choca por concurrencia (412) en vez de dejar una venta fuera
/// del arqueo.
/// </summary>
public sealed class CashSession
{
    // EF Core materializa por aquí; el código sólo crea cajas con Open.
    private CashSession()
    {
        CashierName = string.Empty;
        CompanyName = string.Empty;
        CompanyTaxId = string.Empty;
    }

    public CashSessionId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Quien abrió. Sólo él vende y cierra en esta caja.</summary>
    public MemberId CashierId { get; private set; }

    public string CashierName { get; private set; }

    public Guid CompanyId { get; private set; }

    public string CompanyName { get; private set; }

    public string CompanyTaxId { get; private set; }

    public string? CompanyAddress { get; private set; }

    public string? CompanyPhone { get; private set; }

    public CashSessionStatus Status { get; private set; }

    public decimal OpeningFloat { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public int SalesCount { get; private set; }

    public int VoidedCount { get; private set; }

    public decimal SalesTotal { get; private set; }

    /// <summary>Efectivo neto: lo aplicado de cada venta (recibido − cambio), no el billete.</summary>
    public decimal CashTotal { get; private set; }

    public decimal CardTotal { get; private set; }

    public decimal TransferTotal { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? CountedCash { get; private set; }

    public decimal? CashDifference { get; private set; }

    public string? ClosingNote { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El esperado mientras la caja sigue abierta: base + efectivo neto.</summary>
    public decimal LiveExpectedCash => OpeningFloat + CashTotal;

    public static CashSession Open(
        CashSessionId id,
        Guid tenantId,
        MemberId cashier,
        string cashierName,
        PosCompanySnapshot company,
        decimal openingFloat,
        DateTimeOffset at)
    {
        if (openingFloat < 0 || openingFloat > PosLimits.MaxCashAmount || !PosLimits.HasValidScale(openingFloat))
        {
            throw new PosDomainException(
                "pos.session.opening_float_invalid",
                "The opening float must be between 0 and 100000000 with at most 2 decimals.");
        }

        return new CashSession
        {
            Id = id,
            TenantId = tenantId,
            CashierId = cashier,
            CashierName = cashierName,
            CompanyId = company.CompanyId,
            CompanyName = company.Name,
            CompanyTaxId = company.TaxId,
            CompanyAddress = company.Address,
            CompanyPhone = company.Phone,
            Status = CashSessionStatus.Open,
            OpeningFloat = openingFloat,
            OpenedAt = at,
            // Nace en 1, como Company: el If-Match del cierre manda esta versión.
            Version = 1,
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    /// <summary>
    /// Congela el arqueo. La versión esperada la compara el handler, no el dominio (mismo reparto
    /// que UpdateOrdersExportLayout).
    /// </summary>
    public void Close(decimal countedCash, string? note, DateTimeOffset at)
    {
        EnsureOpen("pos.session.not_open", "The cash session is not open.");

        if (countedCash < 0 || countedCash > PosLimits.MaxCountedCash || !PosLimits.HasValidScale(countedCash))
        {
            throw new PosDomainException(
                "pos.session.counted_cash_invalid",
                "The counted cash must be between 0 and 1000000000 with at most 2 decimals.");
        }

        var expected = LiveExpectedCash;
        ExpectedCash = expected;
        CountedCash = countedCash;
        CashDifference = countedCash - expected;
        ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = CashSessionStatus.Closed;
        ClosedAt = at;
        Touch(at);
    }

    private void EnsureOpen(string code, string message)
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new PosDomainException(code, message);
        }
    }

    private void Touch(DateTimeOffset at)
    {
        Version++;
        UpdatedAt = at;
    }
}
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~CashSessionTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Domain/CashSession.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosFixtures.cs tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs; git commit -m "feat(pos): agregado CashSession"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---

### Task B5: Agregado `PosSale` con líneas, pagos, anulación y arqueo

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Domain/{PosSaleInputs,PosSaleLine,PosPayment,PosSale,PosSaleNumber,PosVoidability}.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Domain/CashSession.cs` (`RegisterSale`, `RegisterVoid`), `tests/Modules/Pos/Modules.Pos.UnitTests/PosFixtures.cs` (líneas y pagos del ejemplo)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs`

**Interfaces:**
- Consumes: `CashSession` (B4), `VatIncludedLine` (B3).
- Produces:
  - `public sealed record PosSaleLineInput(Guid ProductId, string ProductCode, string ProductName, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage)`
  - `public sealed record PosPaymentInput(PosPaymentMethod Method, decimal? Amount, decimal? Tendered, string? Reference)`
  - `public sealed record PosTaxBreakdownEntry(int TaxPercentage, decimal Base, decimal TaxAmount)`
  - `PosSale.Create(PosSaleId id, string fingerprint, CashSession session, IReadOnlyList<PosSaleLineInput> lines, IReadOnlyList<PosPaymentInput> payments, DateTimeOffset at)`; `void AssignNumber(long value)`; `IReadOnlyList<PosTaxBreakdownEntry> TaxBreakdown()`; `void Void(string reason, MemberId by, DateTimeOffset at)`; propiedades de la tabla del spec («`PosSale`»), `Lines`, `Payments`
  - `PosSaleLine` (`LineTotal` derivado), `PosPayment`
  - `PosSaleNumber.Format(long) => "POS-000042"`
  - `PosVoidability.For(PosSaleStatus, CashSessionStatus) : (bool Voidable, string? BlockedReason)` con `"AlreadyVoided"` / `"SessionClosed"`
  - `CashSession.RegisterSale(PosSale, DateTimeOffset)`, `CashSession.RegisterVoid(PosSale, DateTimeOffset)`
  - `PosFixtures.WorkedExampleLines()`, `Cash(t)`, `Card(a, ref)`, `Transfer(a)`, `Sale(...)`, `Fingerprint`

- [ ] **Step 1: Escribir las pruebas que fallan**

Agrega a `PosFixtures.cs`, dentro de la clase:

```csharp
    public static readonly Guid Shampoo = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    public static readonly Guid Avena = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    public static readonly Guid Jabon = Guid.Parse("01900000-0000-7000-8000-0000000000a3");

    public static readonly string Fingerprint = new('a', 64);

    public static PosSaleLineInput[] WorkedExampleLines() =>
    [
        new(Shampoo, "SH-400", "Shampoo 400 ml", 2m, 11_900m, 10m, 19),
        new(Avena, "AV-01", "Avena granel (kg)", 1.5m, 5_000m, 0m, 0),
        new(Jabon, "JB-03", "Jabón", 3m, 2_990m, 0m, 5),
    ];

    public static PosPaymentInput Cash(decimal tendered) => new(PosPaymentMethod.Cash, null, tendered, null);

    public static PosPaymentInput Card(decimal amount, string? reference = null) =>
        new(PosPaymentMethod.Card, amount, null, reference);

    public static PosPaymentInput Transfer(decimal amount) => new(PosPaymentMethod.Transfer, amount, null, null);

    /// <summary>La venta del ejemplo: tarjeta 20 000 (ref 1234) + efectivo, recibido 20 000.</summary>
    public static PosSale Sale(
        CashSession session,
        PosSaleLineInput[]? lines = null,
        PosPaymentInput[]? payments = null,
        PosSaleId? id = null) =>
        PosSale.Create(
            id ?? PosSaleId.New(),
            Fingerprint,
            session,
            lines ?? WorkedExampleLines(),
            payments ?? [Card(20_000m, "1234"), Cash(20_000m)],
            Now);
```

`tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs`:

```csharp
using System.Globalization;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class PosSaleTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void CreateComputesTheWorkedExampleToTheCent()
    {
        var session = OpenSession();

        var sale = Sale(session);

        Assert.Equal(34_042.86m, sale.Subtotal);
        Assert.Equal(3_847.14m, sale.TaxAmount);
        Assert.Equal(2_380m, sale.DiscountAmount);
        Assert.Equal(37_890m, sale.Total);
        Assert.Equal(2_110m, sale.ChangeAmount);
        Assert.Equal(PosSaleStatus.Completed, sale.Status);
        Assert.Equal(session.Id, sale.CashSessionId);
        Assert.Equal(session.CashierId, sale.CashierId);
        Assert.Equal(TenantId, sale.TenantId);
        Assert.Equal(Fingerprint, sale.RequestFingerprint);
        Assert.Equal(string.Empty, sale.SaleNumber);
        Assert.Equal(new[] { 1, 2, 3 }, sale.Lines.Select(line => line.Position));
        Assert.Equal(21_420m, sale.Lines[0].LineTotal);
        Assert.Collection(
            sale.Payments,
            card =>
            {
                Assert.Equal(PosPaymentMethod.Card, card.Method);
                Assert.Equal(20_000m, card.Amount);
                Assert.Null(card.Tendered);
                Assert.Equal("1234", card.Reference);
                Assert.Equal(1, card.Position);
            },
            cash =>
            {
                Assert.Equal(PosPaymentMethod.Cash, cash.Method);
                Assert.Equal(17_890m, cash.Amount);
                Assert.Equal(20_000m, cash.Tendered);
                Assert.Null(cash.Reference);
                Assert.Equal(2, cash.Position);
            });
        Assert.Equal(sale.Total, sale.Payments.Sum(payment => payment.Amount));
    }

    [Fact]
    public void TheSaleIsAlwaysToTheFinalConsumer()
    {
        var sale = Sale(OpenSession());

        Assert.Null(sale.CustomerId);
        Assert.Equal("Consumidor final", sale.CustomerName);
        Assert.Null(sale.CustomerIdentificationType);
        Assert.Equal("222222222222", sale.CustomerIdentificationNumber);
    }

    [Fact]
    public void TaxBreakdownGroupsByRateInOrder()
    {
        var sale = Sale(OpenSession());

        Assert.Equal(
            new[]
            {
                new PosTaxBreakdownEntry(0, 7_500m, 0m),
                new PosTaxBreakdownEntry(5, 8_542.86m, 427.14m),
                new PosTaxBreakdownEntry(19, 18_000m, 3_420m),
            },
            sale.TaxBreakdown());
    }

    // Review Focus 2: addProduct sólo acumula sobre la línea sin descuento, así que el mismo
    // producto puede llegar en dos líneas.
    [Fact]
    public void TheSameProductOnTwoLinesKeepsBothLines()
    {
        var sale = Sale(
            OpenSession(),
            [
                new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 10m, 19),
                new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 0m, 19),
            ],
            [Cash(30_000m)]);

        Assert.Equal(2, sale.Lines.Count);
        Assert.Equal(10_710m + 11_900m, sale.Total);
    }

    [Fact]
    public void CardOnlyAndTransferOnlyNeedNoCashLine()
    {
        Assert.Equal(37_890m, Sale(OpenSession(), payments: [Card(37_890m)]).Total);
        Assert.Equal(0m, Sale(OpenSession(), payments: [Transfer(37_890m)]).ChangeAmount);
    }

    [Fact]
    public void ExactCashLeavesNoChange()
    {
        var sale = Sale(OpenSession(), payments: [Cash(37_890m)]);

        Assert.Equal(0m, sale.ChangeAmount);
        Assert.Equal(37_890m, sale.Payments.Single().Amount);
    }

    // Regla 8: con total 0, un único Cash con recibido 0, y es la única venta cuyo Cash guarda 0.
    [Fact]
    public void AZeroTotalTakesASingleCashWithZeroTendered()
    {
        var sale = Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 100m, 19)],
            [Cash(0m)]);

        Assert.Equal(0m, sale.Total);
        var cash = Assert.Single(sale.Payments);
        Assert.Equal(0m, cash.Amount);
        Assert.Equal(0m, cash.Tendered);
        Assert.Equal(0m, sale.ChangeAmount);
    }

    public static TheoryData<string, PosPaymentInput[]> RejectedPayments => new()
    {
        { "pos.sale.payment_required", [] },
        { "pos.sale.too_many_payments", [Card(1m), Card(1m), Card(1m), Card(1m), Card(1m), Cash(40_000m)] },
        { "pos.sale.duplicate_cash_payment", [Cash(20_000m), Cash(20_000m)] },
        { "pos.sale.payment_amount_invalid", [Card(0m), Cash(40_000m)] },
        { "pos.sale.payment_amount_invalid", [Card(1.005m), Cash(40_000m)] },
        { "pos.sale.payment_amount_invalid", [new PosPaymentInput(PosPaymentMethod.Card, null, null, null), Cash(40_000m)] },
        { "pos.sale.tendered_only_for_cash", [new PosPaymentInput(PosPaymentMethod.Card, 37_890m, 37_890m, null)] },
        { "pos.sale.tendered_invalid", [new PosPaymentInput(PosPaymentMethod.Cash, null, null, null)] },
        { "pos.sale.tendered_invalid", [Cash(-1m)] },
        { "pos.sale.tendered_invalid", [Cash(100_000_000.01m)] },
        { "pos.sale.tendered_invalid", [Cash(40_000.005m)] },
        { "pos.sale.cash_amount_not_allowed", [new PosPaymentInput(PosPaymentMethod.Cash, 37_890m, 40_000m, null)] },
        { "pos.sale.payment_exceeds_total", [Card(40_000m)] },
        { "pos.sale.payment_insufficient", [Cash(37_889.99m)] },
        { "pos.sale.payment_insufficient", [Card(20_000m)] },
        { "pos.sale.cash_payment_unneeded", [Card(37_890m), Cash(0m)] },
    };

    [Theory]
    [MemberData(nameof(RejectedPayments))]
    public void EachPaymentRuleRejectsWithItsCode(string code, PosPaymentInput[] payments)
    {
        var session = OpenSession();

        var error = Assert.Throws<PosDomainException>(() => Sale(session, payments: payments));

        Assert.Equal(code, error.Code);
    }

    // Regla 8 y decisión 46: una venta en cero no recibe billete ni da cambio.
    [Fact]
    public void AZeroTotalWithTenderedRejectsAndCreatesNothing()
    {
        var session = OpenSession();

        var error = Assert.Throws<PosDomainException>(() => Sale(
            session,
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 11_900m, 100m, 19)],
            [Cash(5_000m)]));

        Assert.Equal("pos.sale.tendered_invalid", error.Code);
        Assert.Equal(0, session.SalesCount);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public void AZeroTotalPaidByCardExceedsTheTotal()
    {
        var error = Assert.Throws<PosDomainException>(() => Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 0m, 0m, 19)],
            [Card(1m)]));

        Assert.Equal("pos.sale.payment_exceeds_total", error.Code);
    }

    [Theory]
    [InlineData("0", "0", "pos.sale.quantity_invalid")]
    [InlineData("100000", "0", "pos.sale.quantity_invalid")]
    [InlineData("1.005", "0", "pos.sale.quantity_invalid")]
    [InlineData("1", "100.01", "pos.sale.discount_out_of_range")]
    [InlineData("1", "-1", "pos.sale.discount_out_of_range")]
    [InlineData("1", "7.005", "pos.sale.discount_out_of_range")]
    public void LinesRejectQuantityAndDiscountOutOfRangeOrScale(string quantity, string discount, string code)
    {
        var error = Assert.Throws<PosDomainException>(() => Sale(
            OpenSession(),
            [new(Shampoo, "SH-400", "Shampoo 400 ml", D(quantity), 11_900m, D(discount), 19)],
            [Cash(100_000_000m)]));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void ASaleNeedsBetweenOneAndTwoHundredLines()
    {
        var none = Assert.Throws<PosDomainException>(() => Sale(OpenSession(), [], [Cash(0m)]));
        var line = new PosSaleLineInput(Shampoo, "SH-400", "Shampoo 400 ml", 1m, 1m, 0m, 0);
        var tooMany = Assert.Throws<PosDomainException>(() =>
            Sale(OpenSession(), Enumerable.Repeat(line, 201).ToArray(), [Cash(1_000m)]));

        Assert.Equal("pos.sale.lines_required", none.Code);
        Assert.Equal("pos.sale.too_many_lines", tooMany.Code);
    }

    [Fact]
    public void ACashLineIgnoresAReferenceAndTrimsCardReferences()
    {
        var sale = Sale(
            OpenSession(),
            payments: [Card(20_000m, "  1234  "), new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, "x")]);

        Assert.Equal("1234", sale.Payments[0].Reference);
        Assert.Null(sale.Payments[1].Reference);
    }

    [Fact]
    public void AssignNumberFormatsAndCanOnlyHappenOnce()
    {
        var sale = Sale(OpenSession());

        sale.AssignNumber(42);

        Assert.Equal("POS-000042", sale.SaleNumber);
        Assert.Throws<InvalidOperationException>(() => sale.AssignNumber(43));
        Assert.Equal("POS-1000000", PosSaleNumber.Format(1_000_000));
    }

    [Fact]
    public void VoidRecordsWhoWhenAndWhyAndOnlyOnce()
    {
        var sale = Sale(OpenSession());
        var admin = new MemberId(Guid.CreateVersion7());

        sale.Void("  Cliente se arrepintió  ", admin, Now.AddMinutes(5));

        Assert.Equal(PosSaleStatus.Voided, sale.Status);
        Assert.Equal("Cliente se arrepintió", sale.VoidReason);
        Assert.Equal(admin, sale.VoidedBy);
        Assert.Equal(Now.AddMinutes(5), sale.VoidedAt);
        var again = Assert.Throws<PosDomainException>(() => sale.Void("Otra vez", admin, Now));
        Assert.Equal("pos.sale.already_voided", again.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VoidNeedsAReason(string reason)
    {
        var sale = Sale(OpenSession());

        var error = Assert.Throws<PosDomainException>(() =>
            sale.Void(reason, new MemberId(Guid.CreateVersion7()), Now));

        Assert.Equal("pos.sale.void_reason_required", error.Code);
        Assert.Equal(PosSaleStatus.Completed, sale.Status);
    }

    [Theory]
    [InlineData(PosSaleStatus.Completed, CashSessionStatus.Open, true, null)]
    [InlineData(PosSaleStatus.Voided, CashSessionStatus.Open, false, "AlreadyVoided")]
    [InlineData(PosSaleStatus.Voided, CashSessionStatus.Closed, false, "AlreadyVoided")]
    [InlineData(PosSaleStatus.Completed, CashSessionStatus.Closed, false, "SessionClosed")]
    public void VoidabilityDescribesTheSaleNotTheCaller(
        PosSaleStatus status, CashSessionStatus sessionStatus, bool voidable, string? reason)
    {
        Assert.Equal((voidable, reason), PosVoidability.For(status, sessionStatus));
    }
}
```

Agrega a `CashSessionTests.cs`:

```csharp
    [Fact]
    public void RegisterSaleAddsByMethodAndMovesTheVersion()
    {
        var session = OpenSession(100_000m);
        var sale = Sale(session);

        session.RegisterSale(sale, Now);

        Assert.Equal(1, session.SalesCount);
        Assert.Equal(37_890m, session.SalesTotal);
        Assert.Equal(17_890m, session.CashTotal);
        Assert.Equal(20_000m, session.CardTotal);
        Assert.Equal(0m, session.TransferTotal);
        Assert.Equal(117_890m, session.LiveExpectedCash);
        Assert.Equal(2, session.Version);
    }

    // Review Focus 1: el arqueo resta lo aplicado en efectivo (17 890), no el billete (20 000).
    [Fact]
    public void RegisterVoidOfASplitSaleSubtractsTheAppliedCashNotTheTendered()
    {
        var session = OpenSession(100_000m);
        var sale = Sale(session);
        session.RegisterSale(sale, Now);
        sale.Void("Cliente se arrepintió", Cashier, Now);

        session.RegisterVoid(sale, Now);

        Assert.Equal(0, session.SalesCount);
        Assert.Equal(1, session.VoidedCount);
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(0m, session.CashTotal);
        Assert.Equal(0m, session.CardTotal);
        Assert.Equal(100_000m, session.LiveExpectedCash);
        Assert.Equal(3, session.Version);
    }

    [Fact]
    public void ClosingAfterASaleExpectsTheFloatPlusTheNetCash()
    {
        var session = OpenSession(100_000m);
        session.RegisterSale(Sale(session), Now);

        session.Close(117_000m, null, Now);

        Assert.Equal(117_890m, session.ExpectedCash);
        Assert.Equal(-890m, session.CashDifference);
    }

    [Fact]
    public void AClosedSessionTakesNoSaleAndNoVoid()
    {
        var session = OpenSession();
        var sale = Sale(session);
        session.RegisterSale(sale, Now);
        session.Close(117_890m, null, Now);

        var selling = Assert.Throws<PosDomainException>(() => session.RegisterSale(Sale(session), Now));
        var voiding = Assert.Throws<PosDomainException>(() => session.RegisterVoid(sale, Now));

        Assert.Equal("pos.session.not_open", selling.Code);
        Assert.Equal("pos.sale.void_session_closed", voiding.Code);
        Assert.Equal(1, session.SalesCount);
    }
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0246` para `PosSale`, `PosSaleLineInput`, `PosPaymentInput`, `PosTaxBreakdownEntry`, `PosSaleNumber`, `PosVoidability`; `CS1061` para `RegisterSale`).

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Domain/PosSaleInputs.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>Una línea ya resuelta contra el catálogo: el precio y la tasa son los de ahora.</summary>
public sealed record PosSaleLineInput(
    Guid ProductId,
    string ProductCode,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage);

/// <summary>Cash trae sólo Tendered (el Amount lo calcula el dominio); Card/Transfer traen Amount.</summary>
public sealed record PosPaymentInput(
    PosPaymentMethod Method,
    decimal? Amount,
    decimal? Tendered,
    string? Reference);

/// <summary>Desglose de IVA por tasa para el ticket.</summary>
public sealed record PosTaxBreakdownEntry(int TaxPercentage, decimal Base, decimal TaxAmount);
```

`src/Modules/Pos/Modules.Pos.Domain/PosSaleLine.cs`:

```csharp
using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.Domain;

public sealed class PosSaleLine
{
    private PosSaleLine()
    {
        ProductCode = string.Empty;
        ProductName = string.Empty;
    }

    public PosSaleLineId Id { get; private set; }

    public PosSaleId SaleId { get; private set; }

    public int Position { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductCode { get; private set; }

    public string ProductName { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public int TaxPercentage { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal Subtotal { get; private set; }

    /// <summary>Derivado y no persistido, como el precio con descuento de Quotations.</summary>
    public decimal LineTotal => Subtotal + TaxAmount;

    internal static PosSaleLine Create(PosSaleId saleId, int position, PosSaleLineInput input)
    {
        if (input.Quantity <= 0 || input.Quantity > PosLimits.MaxQuantity || !PosLimits.HasValidScale(input.Quantity))
        {
            throw new PosDomainException(
                "pos.sale.quantity_invalid",
                "The quantity must be greater than 0, at most 99999 and have at most 2 decimals.");
        }

        if (input.DiscountPercentage < 0 || input.DiscountPercentage > 100 || !PosLimits.HasValidScale(input.DiscountPercentage))
        {
            throw new PosDomainException(
                "pos.sale.discount_out_of_range",
                "The discount must be between 0 and 100 with at most 2 decimals.");
        }

        // Precio y tasa vienen del catálogo, no del cliente: fuera de rango es un bug.
        ArgumentOutOfRangeException.ThrowIfNegative(input.UnitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(input.TaxPercentage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.TaxPercentage, 100);

        var amounts = VatIncludedLine.Compute(
            input.Quantity, input.UnitPrice, input.DiscountPercentage, input.TaxPercentage);

        return new PosSaleLine
        {
            Id = PosSaleLineId.New(),
            SaleId = saleId,
            Position = position,
            ProductId = input.ProductId,
            ProductCode = input.ProductCode,
            ProductName = input.ProductName,
            Quantity = input.Quantity,
            UnitPrice = input.UnitPrice,
            DiscountPercentage = input.DiscountPercentage,
            TaxPercentage = input.TaxPercentage,
            DiscountAmount = amounts.DiscountAmount,
            TaxAmount = amounts.TaxAmount,
            Subtotal = amounts.Subtotal,
        };
    }
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosPayment.cs`:

```csharp
namespace Modules.Pos.Domain;

public sealed class PosPayment
{
    private PosPayment()
    {
    }

    public PosPaymentId Id { get; private set; }

    public PosSaleId SaleId { get; private set; }

    public int Position { get; private set; }

    public PosPaymentMethod Method { get; private set; }

    /// <summary>Lo aplicado al total. En Cash lo calcula el servidor (cashDue).</summary>
    public decimal Amount { get; private set; }

    /// <summary>Sólo Cash: el billete que entregó el cliente.</summary>
    public decimal? Tendered { get; private set; }

    /// <summary>Sólo Card/Transfer, opcional.</summary>
    public string? Reference { get; private set; }

    internal static PosPayment Create(
        PosSaleId saleId, int position, PosPaymentMethod method, decimal amount, decimal? tendered, string? reference) =>
        new()
        {
            Id = PosPaymentId.New(),
            SaleId = saleId,
            Position = position,
            Method = method,
            Amount = amount,
            Tendered = tendered,
            Reference = reference,
        };
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosSaleNumber.cs`:

```csharp
using System.Globalization;

namespace Modules.Pos.Domain;

/// <summary>Numeración propia por tenant, sin año ni formato configurable (spec, decisión 15).</summary>
public static class PosSaleNumber
{
    public static string Format(long value) =>
        "POS-" + value.ToString("D6", CultureInfo.InvariantCulture);
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosVoidability.cs`:

```csharp
namespace Modules.Pos.Domain;

/// <summary>
/// Describe el estado de la venta, no al que pregunta (spec, endpoint 9): sin pos.sale.void la
/// pantalla no dibuja la acción. La primera razón gana.
/// </summary>
public static class PosVoidability
{
    public const string AlreadyVoided = "AlreadyVoided";
    public const string SessionClosed = "SessionClosed";

    public static (bool Voidable, string? BlockedReason) For(
        PosSaleStatus status, CashSessionStatus sessionStatus) =>
        status == PosSaleStatus.Voided ? (false, AlreadyVoided)
        : sessionStatus == CashSessionStatus.Closed ? (false, SessionClosed)
        : (true, null);
}
```

`src/Modules/Pos/Modules.Pos.Domain/PosSale.cs`:

```csharp
using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.Domain;

/// <summary>
/// Venta de mostrador (spec 2026-10-07, «PosSale»). Atómica: sólo existe completada o anulada. Sin
/// Version propia: la anulación la serializa la Version de la caja.
/// </summary>
public sealed class PosSale
{
    private readonly List<PosSaleLine> _lines = [];
    private readonly List<PosPayment> _payments = [];

    private PosSale()
    {
        RequestFingerprint = string.Empty;
        SaleNumber = string.Empty;
        CustomerName = string.Empty;
        CustomerIdentificationNumber = string.Empty;
    }

    public PosSaleId Id { get; private set; }

    /// <summary>SHA-256 del cuerpo canónico: sólo sirve para reconocer una repetición.</summary>
    public string RequestFingerprint { get; private set; }

    public Guid TenantId { get; private set; }

    public CashSessionId CashSessionId { get; private set; }

    public MemberId CashierId { get; private set; }

    /// <summary>Vacío hasta AssignNumber, que corre adentro de la transacción (decisión P2).</summary>
    public string SaleNumber { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string CustomerName { get; private set; }

    public string? CustomerIdentificationType { get; private set; }

    public string CustomerIdentificationNumber { get; private set; }

    public IReadOnlyList<PosSaleLine> Lines => _lines;

    public IReadOnlyList<PosPayment> Payments => _payments;

    public decimal Subtotal { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal Total { get; private set; }

    public decimal ChangeAmount { get; private set; }

    public PosSaleStatus Status { get; private set; }

    public string? VoidReason { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public MemberId? VoidedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Valida y calcula líneas → encabezado → pagos, y sólo entonces construye: nada se asigna
    /// hasta que todo valida (lección de Company.Update).
    /// </summary>
    public static PosSale Create(
        PosSaleId id,
        string fingerprint,
        CashSession session,
        IReadOnlyList<PosSaleLineInput> lines,
        IReadOnlyList<PosPaymentInput> payments,
        DateTimeOffset at)
    {
        if (lines.Count == 0)
        {
            throw new PosDomainException("pos.sale.lines_required", "A sale needs at least one line.");
        }

        if (lines.Count > PosLimits.MaxLines)
        {
            throw new PosDomainException("pos.sale.too_many_lines", "A sale cannot have more than 200 lines.");
        }

        var builtLines = lines.Select((line, index) => PosSaleLine.Create(id, index + 1, line)).ToList();

        // Idéntico a Quotation.RecalculateTotals sin excedente ni retención.
        var subtotal = VatIncludedLine.Round(builtLines.Sum(line => line.Subtotal));
        var discount = VatIncludedLine.Round(builtLines.Sum(line => line.DiscountAmount));
        var tax = VatIncludedLine.Round(builtLines.Sum(line => line.TaxAmount));
        var total = subtotal + tax;

        var (builtPayments, change) = BuildPayments(id, total, payments);

        var sale = new PosSale
        {
            Id = id,
            RequestFingerprint = fingerprint,
            TenantId = session.TenantId,
            CashSessionId = session.Id,
            CashierId = session.CashierId,
            CustomerId = null,
            CustomerName = PosFinalConsumer.Name,
            CustomerIdentificationType = null,
            CustomerIdentificationNumber = PosFinalConsumer.IdentificationNumber,
            Subtotal = subtotal,
            DiscountAmount = discount,
            TaxAmount = tax,
            Total = total,
            ChangeAmount = change,
            Status = PosSaleStatus.Completed,
            CreatedAt = at,
        };
        sale._lines.AddRange(builtLines);
        sale._payments.AddRange(builtPayments);
        return sale;
    }

    public void AssignNumber(long value)
    {
        if (SaleNumber.Length > 0)
        {
            throw new InvalidOperationException($"Sale '{Id}' already has number '{SaleNumber}'.");
        }

        SaleNumber = PosSaleNumber.Format(value);
    }

    public IReadOnlyList<PosTaxBreakdownEntry> TaxBreakdown() => _lines
        .GroupBy(line => line.TaxPercentage)
        .OrderBy(group => group.Key)
        .Select(group => new PosTaxBreakdownEntry(
            group.Key,
            VatIncludedLine.Round(group.Sum(line => line.Subtotal)),
            VatIncludedLine.Round(group.Sum(line => line.TaxAmount))))
        .ToArray();

    public void Void(string reason, MemberId by, DateTimeOffset at)
    {
        if (Status == PosSaleStatus.Voided)
        {
            throw new PosDomainException("pos.sale.already_voided", "The sale is already voided.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new PosDomainException("pos.sale.void_reason_required", "A void reason is required.");
        }

        Status = PosSaleStatus.Voided;
        VoidReason = trimmed;
        VoidedBy = by;
        VoidedAt = at;
    }

    /// <summary>Las ocho reglas de pago del spec, en su orden.</summary>
    private static (List<PosPayment> Payments, decimal Change) BuildPayments(
        PosSaleId saleId, decimal total, IReadOnlyList<PosPaymentInput> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new PosDomainException("pos.sale.payment_required", "A sale needs at least one payment.");
        }

        if (inputs.Count > PosLimits.MaxPayments)
        {
            throw new PosDomainException("pos.sale.too_many_payments", "A sale cannot have more than 5 payments.");
        }

        if (inputs.Count(input => input.Method == PosPaymentMethod.Cash) > 1)
        {
            throw new PosDomainException("pos.sale.duplicate_cash_payment", "A sale takes at most one cash payment.");
        }

        foreach (var input in inputs)
        {
            if (input.Method == PosPaymentMethod.Cash)
            {
                if (input.Amount is not null)
                {
                    throw new PosDomainException(
                        "pos.sale.cash_amount_not_allowed",
                        "A cash payment carries only the tendered amount; the server computes what it applies.");
                }

                if (input.Tendered is not { } tendered || tendered < 0 || tendered > PosLimits.MaxCashAmount
                    || !PosLimits.HasValidScale(tendered))
                {
                    throw new PosDomainException(
                        "pos.sale.tendered_invalid",
                        "The tendered cash must be between 0 and 100000000 with at most 2 decimals.");
                }
            }
            else
            {
                if (input.Amount is not { } amount || amount <= 0 || !PosLimits.HasValidScale(amount))
                {
                    throw new PosDomainException(
                        "pos.sale.payment_amount_invalid",
                        "A card or transfer payment needs an amount greater than 0 with at most 2 decimals.");
                }

                if (input.Tendered is not null)
                {
                    throw new PosDomainException(
                        "pos.sale.tendered_only_for_cash", "Only a cash payment carries a tendered amount.");
                }
            }
        }

        var nonCash = inputs.Where(input => input.Method != PosPaymentMethod.Cash).Sum(input => input.Amount!.Value);
        if (nonCash > total)
        {
            throw new PosDomainException("pos.sale.payment_exceeds_total", "A card or transfer gives no change.");
        }

        var cashDue = total - nonCash;
        var cash = inputs.SingleOrDefault(input => input.Method == PosPaymentMethod.Cash);
        decimal change;

        if (total == 0)
        {
            // Regla 8: la única venta cuyo Cash guarda 0. Cualquier billete dejaría un ChangeAmount
            // igual al billete, que el arqueo no distingue de un vuelto real (decisión 46).
            if (cash is null || cash.Tendered!.Value > 0)
            {
                throw new PosDomainException(
                    "pos.sale.tendered_invalid", "A zero-total sale takes a single cash payment of 0.");
            }

            change = 0;
        }
        else if (cashDue > 0)
        {
            if (cash is null || cash.Tendered!.Value < cashDue)
            {
                throw new PosDomainException(
                    "pos.sale.payment_insufficient", "The payments do not cover the sale total.");
            }

            change = cash.Tendered.Value - cashDue;
        }
        else
        {
            if (cash is not null)
            {
                throw new PosDomainException(
                    "pos.sale.cash_payment_unneeded", "The other payments already cover the total.");
            }

            change = 0;
        }

        var payments = inputs.Select((input, index) => input.Method == PosPaymentMethod.Cash
            ? PosPayment.Create(saleId, index + 1, PosPaymentMethod.Cash, cashDue, input.Tendered, null)
            : PosPayment.Create(
                saleId, index + 1, input.Method, input.Amount!.Value, null,
                string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim()))
            .ToList();

        return (payments, change);
    }
}
```

En `CashSession.cs`, antes de `EnsureOpen`:

```csharp
    /// <summary>Suma la venta a los acumulados. Exige la caja abierta.</summary>
    public void RegisterSale(PosSale sale, DateTimeOffset at)
    {
        EnsureOpen("pos.session.not_open", "The cash session is not open.");
        EnsureOwns(sale);
        Accumulate(sale, +1);
        SalesCount++;
        Touch(at);
    }

    /// <summary>
    /// Resta exactamente lo que sumó RegisterSale. Una caja cerrada es un arqueo que alguien ya
    /// firmó: tocarla lo dejaría mintiendo.
    /// </summary>
    public void RegisterVoid(PosSale sale, DateTimeOffset at)
    {
        EnsureOpen("pos.sale.void_session_closed", "The sale belongs to a closed cash session.");
        EnsureOwns(sale);
        Accumulate(sale, -1);
        SalesCount--;
        VoidedCount++;
        Touch(at);
    }

    private void EnsureOwns(PosSale sale)
    {
        if (sale.CashSessionId != Id)
        {
            throw new InvalidOperationException($"Sale '{sale.Id}' does not belong to cash session '{Id}'.");
        }
    }

    // Lo aplicado por medio: en Cash es cashDue, no el billete (Review Focus 1).
    private void Accumulate(PosSale sale, int sign)
    {
        SalesTotal += sign * sale.Total;
        foreach (var payment in sale.Payments)
        {
            switch (payment.Method)
            {
                case PosPaymentMethod.Cash:
                    CashTotal += sign * payment.Amount;
                    break;
                case PosPaymentMethod.Card:
                    CardTotal += sign * payment.Amount;
                    break;
                case PosPaymentMethod.Transfer:
                    TransferTotal += sign * payment.Amount;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown payment method '{payment.Method}'.");
            }
        }
    }
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosSaleTests|FullyQualifiedName~CashSessionTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Domain tests/Modules/Pos/Modules.Pos.UnitTests/PosFixtures.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs; git commit -m "feat(pos): agregado PosSale con líneas, pagos y anulación"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---


### Task B6: Persistencia del módulo Pos y migración `InitialPos`

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/{ICashSessionRepository,IPosSaleRepository,IPosSaleNumberGenerator,IPosAuditPublisher}.cs`
- Create: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/{PosDbContext,PosDbContextFactory,PosOutboxMessage,PosSaleNumberCounter,PosUnitOfWork,CashSessionRepository,PosSaleRepository,PosSaleNumberGenerator,PosAuditPublisher,PosUserReferenceProbe}.cs`, `src/Modules/Pos/Modules.Pos.Infrastructure/PosDatabaseInitializer.cs`
- Create (generados): `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/<ts>_InitialPos.cs`, `<ts>_InitialPos.Designer.cs`, `PosDbContextModelSnapshot.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Infrastructure/PosInfrastructureExtensions.cs`, `src/Api/Program.cs:179`
- Test: `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs`

**Interfaces:**
- Consumes: `CashSession`, `PosSale` (B4, B5); `IPosUnitOfWork` (B1).
- Produces:
  - `ICashSessionRepository { Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken); Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken); void Add(CashSession); }` (B11 suma `ListAsync`)
  - `IPosSaleRepository { Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken); void Add(PosSale); }` (B11 suma `ListAsync`)
  - `IPosSaleNumberGenerator { Task<long> NextAsync(Guid tenantId, CancellationToken); }`
  - `IPosAuditPublisher { void Publish(Guid tenantId, Guid actorId, string action, string resourceType, string resourceId, string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt); }`
  - `PosUnitOfWork` traduce: `DbUpdateConcurrencyException` → `RequestConcurrencyException("concurrency.conflict")`; 23505 en `IX_cash_sessions_one_open_per_cashier` → `PosDomainException("pos.session.already_open")`; 23505 en `PK_sales` → `PosDomainException("pos.sale.id_taken")`; 23505 en `IX_sales_tenant_number` sin traducir.
  - `InitializePosDatabaseAsync(this IServiceProvider, CancellationToken)`
  - Harness `PosApiHarness` (lo usan B12 y B13): `StartDatabaseAsync`, `QepApiFactory`, `CreateClient`, `RegisterTenantAsync`, `EnablePosAsync`, `DisablePosAsync`, `CreateCompanyAsync`, `CreateTaxRateAsync`, `CreateProductAsync`, `ExecuteSqlAsync`, `InviteCashierAsync`, `AuditCountAsync`, `PosUrl`, `CashierPermissions`, `AdminPosPermissions`, `SeedPermissions`.

- [ ] **Step 1: Escribir el harness y las pruebas de persistencia que fallan**

`tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Pos.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Harness del módulo Pos (spec 2026-10-07, «Integración»). Registra un tenant real —el cajero se
/// resuelve a su membresía por IMembershipDirectory, que lee membresías de verdad— y prende `pos`
/// por SQL: ni el signup ni ninguna configuración lo prenden, y sin la fila todo /pos/* da 403
/// aunque el header pida los permisos.
/// </summary>
internal static class PosApiHarness
{
    public static string PosUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/pos";

    public static readonly string[] CashierPermissions =
    [
        PosPermissions.SaleRead,
        PosPermissions.SaleCreate,
        PosPermissions.RegisterOperate,
    ];

    public static readonly string[] AdminPosPermissions =
    [
        .. CashierPermissions,
        PosPermissions.SaleVoid,
        PosPermissions.SaleDiscount,
        PosPermissions.RegisterRead,
    ];

    /// <summary>Lo que hace falta para sembrar empresa, tasas y productos por sus APIs.</summary>
    public static readonly string[] SeedPermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        CompaniesPermissions.CompanyRead,
        CompaniesPermissions.CompanyManage,
    ];

    private static readonly string[] CashierRole = ["cashier"];

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static HttpClient CreateClient(
        QepApiFactory factory,
        Guid subjectId,
        Guid tenantId,
        params string[] permissions)
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

    internal sealed record PosTenant(Guid TenantId, Guid OwnerUserId, HttpClient Seeder);

    /// <summary>Copia de QuotationsApiHarness.RegisterTenantInTimeZoneAsync, en Bogotá.</summary>
    public static async Task<PosTenant> RegisterTenantAsync(QepApiFactory factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Pos Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);

        return new PosTenant(
            registered.TenantId,
            registered.OwnerUserId,
            CreateClient(factory, registered.OwnerUserId, registered.TenantId, SeedPermissions));
    }

    public static Task EnablePosAsync(PostgreSqlContainer database, Guid tenantId) =>
        ExecuteSqlAsync(
            database,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'pos', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO NOTHING
            """,
            ("tenantId", tenantId));

    public static Task DisablePosAsync(PostgreSqlContainer database, Guid tenantId) =>
        ExecuteSqlAsync(
            database,
            "DELETE FROM tenancy.tenant_modules WHERE tenant_id = @tenantId AND module_key = 'pos'",
            ("tenantId", tenantId));

    public static async Task ExecuteSqlAsync(
        PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<long> CountAsync(
        PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Filas de auditoría del POS con esa acción en el outbox de plataforma.</summary>
    public static Task<long> AuditCountAsync(PostgreSqlContainer database, Guid tenantId, string action) =>
        CountAsync(
            database,
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'source' = 'pos'
              AND payload->>'action' = @action
              AND payload->>'tenantId' = @tenantId
            """,
            ("action", action),
            ("tenantId", tenantId.ToString()));

    public static async Task<Guid> EnsureCityIdAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyDepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);

        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyCityDto>>(
                $"/api/v1/cities?departmentId={department.Id}", TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                return cities[0].Id;
            }
        }

        throw new InvalidOperationException("No seeded DIVIPOLA department has at least one city.");
    }

    internal sealed record CompanyRef(Guid Id, string Name, string TaxId);

    public static async Task<CompanyRef> CreateCompanyAsync(HttpClient client, Guid tenantId)
    {
        var cityId = await EnsureCityIdAsync(client);
        var taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Origen Botánico SAS",
                bankAccounts = new[]
                {
                    new { bankName = "Bancolombia", accountNumber = $"{Random.Shared.Next(100000000, 999999999)}", currency = "COP" },
                },
                taxId,
                cityId,
                phone = "6045551234",
                email = "caja@origen.example.co",
                address = "Cra 50 # 10-20, Rionegro",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new CompanyRef(body.Id, "Origen Botánico SAS", taxId);
    }

    public static async Task<Guid> CreateTaxRateAsync(HttpClient client, Guid tenantId, string name, int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Producto con precio COP de lista y las escalas por defecto de QuotationsApiHarness.</summary>
    public static async Task<Guid> CreateProductAsync(
        HttpClient client, Guid tenantId, string code, string name, decimal priceCop, Guid? taxRateId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                code,
                taxRateId,
                pricing = new
                {
                    baseCop = priceCop,
                    scales = new object[]
                    {
                        new { fromUnit = 1, toUnit = 9, discount = 0m, restriction = "multiple", multiple = 1, finalCop = priceCop },
                        new { fromUnit = 10, toUnit = 19, discount = 5m, restriction = "multiple", multiple = 1, finalCop = priceCop * 0.95m },
                        new { fromUnit = 20, toUnit = 999_999, discount = 10m, restriction = "multiple", multiple = 1, finalCop = priceCop * 0.90m },
                    },
                    packagingUnits = Array.Empty<int>(),
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    internal sealed record InvitedCashier(Guid MembershipId, Guid UserId, HttpClient Client);

    /// <summary>Copia de ReportingApiHarness.InviteActiveAdvisorAsync con el rol `cashier`.</summary>
    public static async Task<InvitedCashier> InviteCashierAsync(
        QepApiFactory factory, PostgreSqlContainer database, PosTenant tenant, params string[] permissions)
    {
        // Sin X-Permissions el stub concede los de tenancy, que traen advisorship.invite.
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId);
        var email = $"cashier-{Guid.CreateVersion7():N}@example.com";
        var invited = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/memberships",
            new { email, displayName = "Cajero Invitado", roles = CashierRole },
            TestContext.Current.CancellationToken);
        invited.EnsureSuccessStatusCode();
        var membership = await invited.Content.ReadFromJsonAsync<InvitedMembershipDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        var token = await FindInvitationTokenAsync(database, membership.Id);
        using var accepting = CreateClient(factory, membership.UserId, tenant.TenantId);
        var accepted = await accepting.PostAsync(
            $"/api/v1/invitations/{token}/accept", content: null, TestContext.Current.CancellationToken);
        accepted.EnsureSuccessStatusCode();

        return new InvitedCashier(
            membership.Id,
            membership.UserId,
            CreateClient(factory, membership.UserId, tenant.TenantId, permissions));
    }

    private static async Task<string> FindInvitationTokenAsync(PostgreSqlContainer database, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("membershipId", membershipId.ToString());
        var payload = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
        Assert.NotNull(payload);

        using var document = JsonDocument.Parse(payload);
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            // Fijados, nunca heredados de appsettings ni de los user-secrets de quien corre las
            // pruebas (mismo criterio que CompaniesApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
        }
    }

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);

    private sealed record InvitedMembershipDto(Guid Id, Guid UserId);

    private sealed record GeographyDepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record GeographyCityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record IdDto(Guid Id);
}
```

`tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Domain;
using Modules.Pos.Infrastructure.Persistence;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Lo que sólo la base hace cumplir (spec 2026-10-07, «Persistencia» y «Traducción de errores»):
/// el índice parcial de una caja abierta por cajero, el choque determinista cierre/venta sobre la
/// Version, el contador sin huecos y la PK de la venta.
/// </summary>
public sealed class PosPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    private static PosSaleLineInput Line(decimal price = 10_000m) =>
        new(Guid.CreateVersion7(), "SH-400", "Shampoo 400 ml", 1m, price, 0m, 19);

    private static async Task<(Guid TenantId, PosCompanySnapshot Company)> ArrangeTenantAsync(QepApiFactory factory)
    {
        var tenant = await RegisterTenantAsync(factory);
        var company = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        return (tenant.TenantId, new PosCompanySnapshot(company.Id, company.Name, company.TaxId, null, null));
    }

    private static CashSession NewSession(Guid tenantId, PosCompanySnapshot company, MemberId cashier) =>
        CashSession.Open(CashSessionId.New(), tenantId, cashier, "Laura Gómez", company, 100_000m, Now);

    [Fact]
    public async Task TwoOpenSessionsForTheSameCashierAreRejectedByThePartialIndex()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var cashier = new MemberId(Guid.CreateVersion7());

        using (var first = factory.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(NewSession(tenantId, company, cashier));
            await new PosUnitOfWork(db).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var second = factory.Services.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<PosDbContext>();
        other.CashSessions.Add(NewSession(tenantId, company, cashier));

        var error = await Assert.ThrowsAsync<PosDomainException>(
            () => new PosUnitOfWork(other).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pos.session.already_open", error.Code);
    }

    [Fact]
    public async Task AClosedSessionDoesNotBlockOpeningAnother()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var cashier = new MemberId(Guid.CreateVersion7());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var closed = NewSession(tenantId, company, cashier);
        closed.Close(100_000m, null, Now);
        db.CashSessions.Add(closed);
        db.CashSessions.Add(NewSession(tenantId, company, cashier));

        // Dos filas escritas: el índice es parcial (status = 'Open') y la cerrada no cuenta.
        Assert.Equal(2, await new PosUnitOfWork(db).SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // Spec, «Concurrencia cierre/venta, determinista»: A carga la caja, B vende y commitea, A cierra
    // y guarda. A tiene que chocar; si no, el arqueo de A dejaría la venta de B afuera.
    [Fact]
    public async Task ACloseLoadedBeforeASaleCommittedConflictsAndTheCountKeepsTheSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(session);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PosDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PosDbContext>();
        var closing = await dbA.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        var selling = await dbB.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);

        var sale = PosSale.Create(
            PosSaleId.New(), new string('b', 64), selling, [Line(10_000m)],
            [new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, null)], Now);
        sale.AssignNumber(1);
        dbB.Sales.Add(sale);
        selling.RegisterSale(sale, Now);
        await new PosUnitOfWork(dbB).SaveChangesAsync(TestContext.Current.CancellationToken);

        closing.Close(100_000m, null, Now);
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => new PosUnitOfWork(dbA).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("concurrency.conflict", error.Code);

        using var scopeC = factory.Services.CreateScope();
        var dbC = scopeC.ServiceProvider.GetRequiredService<PosDbContext>();
        var reloaded = await dbC.CashSessions.SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(CashSessionStatus.Open, reloaded.Status);
        Assert.Equal(1, reloaded.SalesCount);
        reloaded.Close(110_000m, null, Now);
        await dbC.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(110_000m, reloaded.ExpectedCash);
        Assert.Equal(0m, reloaded.CashDifference);
    }

    [Fact]
    public async Task TheCounterNumbersInOrderAndARolledBackNumberIsReused()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await ArrangeTenantAsync(factory);
        var tenantId = Guid.CreateVersion7();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var generator = new PosSaleNumberGenerator(db);

        Assert.Equal(1, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
        Assert.Equal(2, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(3, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Un 422 o un 412 dentro de la transacción no gastan número (spec, «Crear venta», paso 7).
        Assert.Equal(3, await generator.NextAsync(tenantId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASaleIdAlreadyUsedIsTranslatedToIdTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var saleId = PosSaleId.New();
        var cash = new PosPaymentInput(PosPaymentMethod.Cash, null, 20_000m, null);

        using (var first = factory.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<PosDbContext>();
            var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
            var sale = PosSale.Create(saleId, new string('c', 64), session, [Line()], [cash], Now);
            sale.AssignNumber(1);
            db.CashSessions.Add(session);
            db.Sales.Add(sale);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Otro tenant (sin FK a tenancy: tenant_id es un id suelto), otra caja, el mismo id de venta.
        using var second = factory.Services.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<PosDbContext>();
        var otherSession = NewSession(Guid.CreateVersion7(), company, new MemberId(Guid.CreateVersion7()));
        other.CashSessions.Add(otherSession);
        await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        var duplicate = PosSale.Create(saleId, new string('d', 64), otherSession, [Line()], [cash], Now);
        duplicate.AssignNumber(1);
        other.Sales.Add(duplicate);

        var error = await Assert.ThrowsAsync<PosDomainException>(
            () => new PosUnitOfWork(other).SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pos.sale.id_taken", error.Code);
    }

    [Fact]
    public async Task ASaleRoundTripsWithItsLinesPaymentsAndVoid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, company) = await ArrangeTenantAsync(factory);
        var session = NewSession(tenantId, company, new MemberId(Guid.CreateVersion7()));
        var sale = PosSale.Create(
            PosSaleId.New(), new string('e', 64), session,
            [Line(11_900m), new(Guid.CreateVersion7(), "AV-01", "Avena granel (kg)", 1.5m, 5_000m, 0m, 0)],
            [new PosPaymentInput(PosPaymentMethod.Card, 10_000m, null, "1234"), new PosPaymentInput(PosPaymentMethod.Cash, null, 10_000m, null)],
            Now);
        sale.AssignNumber(7);
        var voidedBy = new MemberId(Guid.CreateVersion7());
        using (var write = factory.Services.CreateScope())
        {
            var db = write.ServiceProvider.GetRequiredService<PosDbContext>();
            db.CashSessions.Add(session);
            db.Sales.Add(sale);
            session.RegisterSale(sale, Now);
            sale.Void("Cliente se arrepintió", voidedBy, Now);
            session.RegisterVoid(sale, Now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var read = factory.Services.CreateScope();
        var loaded = await read.ServiceProvider.GetRequiredService<PosDbContext>().Sales
            .SingleAsync(s => s.Id == sale.Id, TestContext.Current.CancellationToken);
        Assert.Equal("POS-000007", loaded.SaleNumber);
        Assert.Equal(19_400m, loaded.Total);
        Assert.Equal(new[] { 1, 2 }, loaded.Lines.OrderBy(l => l.Position).Select(l => l.Position));
        Assert.Equal(1.5m, loaded.Lines.Single(l => l.Position == 2).Quantity);
        Assert.Equal(new[] { PosPaymentMethod.Card, PosPaymentMethod.Cash }, loaded.Payments.OrderBy(p => p.Position).Select(p => p.Method));
        Assert.Equal(PosSaleStatus.Voided, loaded.Status);
        Assert.Equal(voidedBy, loaded.VoidedBy);
        Assert.Equal(new string('e', 64), loaded.RequestFingerprint);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.IntegrationTests
```

Esperado: FAIL de compilación (`CS0234: The type or namespace name 'Persistence' does not exist in the namespace 'Modules.Pos.Infrastructure'`, `CS0246` para `PosDbContext`, `PosUnitOfWork`, `PosSaleNumberGenerator`).

- [ ] **Step 3: Puertos de persistencia (Application)**

`src/Modules/Pos/Modules.Pos.Application/ICashSessionRepository.cs`:

```csharp
using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <summary>Todo método recibe tenantId: el id de una caja de otro tenant responde igual que uno inexistente.</summary>
public interface ICashSessionRepository
{
    Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken);

    Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken);

    void Add(CashSession session);
}
```

`src/Modules/Pos/Modules.Pos.Application/IPosSaleRepository.cs`:

```csharp
using Modules.Pos.Domain;

namespace Modules.Pos.Application;

public interface IPosSaleRepository
{
    /// <summary>Con líneas y pagos, rastreada (la anulación la modifica).</summary>
    Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken);

    void Add(PosSale sale);
}
```

`src/Modules/Pos/Modules.Pos.Application/IPosSaleNumberGenerator.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>
/// Contador atómico por tenant. Se llama adentro de la transacción de la venta: si la venta no
/// commitea, el número no se gasta.
/// </summary>
public interface IPosSaleNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

`src/Modules/Pos/Modules.Pos.Application/IPosAuditPublisher.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>
/// platform.audit.recorded.v1 en la proyección de outbox, en el mismo SaveChanges que el cambio.
/// changedFields es el único campo libre del contrato: ahí viajan los descuentos de la venta
/// (spec, «Descuentos en la auditoría»).
/// </summary>
public interface IPosAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}
```

- [ ] **Step 4: Infraestructura**

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosOutboxMessage.cs`:

```csharp
namespace Modules.Pos.Infrastructure.Persistence;

// Proyección de escritura del Outbox de plataforma, propiedad de Tenancy. Mapeada como
// ExcludeFromMigrations: Pos inserta acá y no crea la tabla.
internal sealed class PosOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosSaleNumberCounter.cs`:

```csharp
namespace Modules.Pos.Infrastructure.Persistence;

// Sólo existe para que la migración cree la tabla: se lee y escribe con SQL crudo
// (PosSaleNumberGenerator), nunca por el ChangeTracker.
internal sealed class PosSaleNumberCounter
{
    public Guid TenantId { get; init; }

    public long NextValue { get; init; }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<MemberId, Guid> MemberIdConverter =
        new(id => id.Value, value => new MemberId(value));

    public DbSet<CashSession> CashSessions => Set<CashSession>();

    public DbSet<PosSale> Sales => Set<PosSale>();

    internal DbSet<PosSaleNumberCounter> SaleNumberCounters => Set<PosSaleNumberCounter>();

    internal DbSet<PosOutboxMessage> Outbox => Set<PosOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCashSession(modelBuilder);
        ConfigureSale(modelBuilder);
        ConfigureSaleLine(modelBuilder);
        ConfigurePayment(modelBuilder);
        ConfigureCounter(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureCashSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<CashSession>();
        session.ToTable("cash_sessions", "pos", table =>
        {
            table.HasCheckConstraint("CK_cash_sessions_status", "status IN ('Open','Closed')");
            table.HasCheckConstraint("CK_cash_sessions_opening_float", "opening_float >= 0");
        });
        session.HasKey(value => value.Id);
        session.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CashSessionId(value))
            .ValueGeneratedNever();
        session.Property(value => value.TenantId).HasColumnName("tenant_id");
        // tenancy.memberships(id), sin FK: otro módulo.
        session.Property(value => value.CashierId).HasColumnName("cashier_id").HasConversion(MemberIdConverter);
        session.Property(value => value.CashierName).HasColumnName("cashier_name").HasMaxLength(PosLimits.CashierNameMaxLength);
        // FK real a companies.companies, agregada a mano en la migración (spec, decisión 8): EF no
        // modela relaciones entre DbContext de módulos distintos.
        session.Property(value => value.CompanyId).HasColumnName("company_id");
        session.Property(value => value.CompanyName).HasColumnName("company_name").HasMaxLength(160);
        session.Property(value => value.CompanyTaxId).HasColumnName("company_tax_id").HasMaxLength(32);
        session.Property(value => value.CompanyAddress).HasColumnName("company_address").HasMaxLength(200);
        session.Property(value => value.CompanyPhone).HasColumnName("company_phone").HasMaxLength(32);
        session.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10);
        session.Property(value => value.OpeningFloat).HasColumnName("opening_float").HasPrecision(14, 2);
        session.Property(value => value.SalesCount).HasColumnName("sales_count");
        session.Property(value => value.VoidedCount).HasColumnName("voided_count");
        session.Property(value => value.SalesTotal).HasColumnName("sales_total").HasPrecision(14, 2);
        session.Property(value => value.CashTotal).HasColumnName("cash_total").HasPrecision(14, 2);
        session.Property(value => value.CardTotal).HasColumnName("card_total").HasPrecision(14, 2);
        session.Property(value => value.TransferTotal).HasColumnName("transfer_total").HasPrecision(14, 2);
        session.Property(value => value.ExpectedCash).HasColumnName("expected_cash").HasPrecision(14, 2);
        session.Property(value => value.CountedCash).HasColumnName("counted_cash").HasPrecision(14, 2);
        session.Property(value => value.CashDifference).HasColumnName("cash_difference").HasPrecision(14, 2);
        session.Property(value => value.ClosingNote).HasColumnName("closing_note").HasMaxLength(PosLimits.NoteMaxLength);
        session.Property(value => value.OpenedAt).HasColumnName("opened_at");
        session.Property(value => value.ClosedAt).HasColumnName("closed_at");
        session.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        session.Property(value => value.CreatedAt).HasColumnName("created_at");
        session.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        session.Ignore(value => value.LiveExpectedCash);

        // Lo que de verdad impone "una caja abierta por cajero": dos aperturas simultáneas sólo las
        // frena la base. PosUnitOfWork lo traduce a pos.session.already_open por este nombre.
        session.HasIndex(value => new { value.TenantId, value.CashierId })
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName("IX_cash_sessions_one_open_per_cashier");
        session.HasIndex(value => new { value.TenantId, value.OpenedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_cash_sessions_tenant_opened");
        session.HasIndex(value => value.CompanyId).HasDatabaseName("IX_cash_sessions_company");
    }

    private static void ConfigureSale(ModelBuilder modelBuilder)
    {
        var sale = modelBuilder.Entity<PosSale>();
        sale.ToTable("sales", "pos", table =>
            table.HasCheckConstraint("CK_sales_status", "status IN ('Completed','Voided')"));
        // PK_sales: PosUnitOfWork traduce su 23505 a pos.sale.id_taken.
        sale.HasKey(value => value.Id);
        sale.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosSaleId(value))
            .ValueGeneratedNever();
        sale.Property(value => value.TenantId).HasColumnName("tenant_id");
        sale.Property(value => value.CashSessionId)
            .HasColumnName("cash_session_id")
            .HasConversion(id => id.Value, value => new CashSessionId(value));
        sale.Property(value => value.CashierId).HasColumnName("cashier_id").HasConversion(MemberIdConverter);
        sale.Property(value => value.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("character(64)");
        sale.Property(value => value.SaleNumber).HasColumnName("sale_number").HasMaxLength(20);
        // Siempre null en el MVP; sin FK (spec, decisión 35).
        sale.Property(value => value.CustomerId).HasColumnName("customer_id");
        sale.Property(value => value.CustomerName).HasColumnName("customer_name").HasMaxLength(160);
        sale.Property(value => value.CustomerIdentificationType).HasColumnName("customer_identification_type").HasMaxLength(10);
        sale.Property(value => value.CustomerIdentificationNumber).HasColumnName("customer_identification_number").HasMaxLength(32);
        sale.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        sale.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        sale.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        sale.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        sale.Property(value => value.ChangeAmount).HasColumnName("change_amount").HasPrecision(14, 2);
        sale.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10);
        sale.Property(value => value.VoidReason).HasColumnName("void_reason").HasMaxLength(PosLimits.VoidReasonMaxLength);
        sale.Property(value => value.VoidedAt).HasColumnName("voided_at");
        sale.Property(value => value.VoidedBy).HasColumnName("voided_by").HasConversion(MemberIdConverter);
        sale.Property(value => value.CreatedAt).HasColumnName("created_at");

        sale.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(value => value.CashSessionId)
            .OnDelete(DeleteBehavior.Restrict);
        sale.HasMany(value => value.Lines)
            .WithOne()
            .HasForeignKey(line => line.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        sale.HasMany(value => value.Payments)
            .WithOne()
            .HasForeignKey(payment => payment.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        // Una venta sin sus líneas o sus pagos no se puede mostrar ni anular: siempre viajan juntas.
        sale.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        sale.Navigation(value => value.Payments).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // El contador es atómico: un 23505 aquí es un bug y se relanza sin traducir (500).
        sale.HasIndex(value => new { value.TenantId, value.SaleNumber })
            .IsUnique()
            .HasDatabaseName("IX_sales_tenant_number");
        sale.HasIndex(value => new { value.CashSessionId, value.CreatedAt }).HasDatabaseName("IX_sales_session");
        sale.HasIndex(value => new { value.TenantId, value.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_sales_tenant_created");
    }

    private static void ConfigureSaleLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<PosSaleLine>();
        line.ToTable("sale_lines", "pos");
        line.HasKey(value => value.Id);
        line.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosSaleLineId(value))
            .ValueGeneratedNever();
        line.Property(value => value.SaleId)
            .HasColumnName("sale_id")
            .HasConversion(id => id.Value, value => new PosSaleId(value));
        line.Property(value => value.Position).HasColumnName("position");
        // catalog.products(id), sin FK: la venta guarda snapshot y un producto borrado no invalida un ticket.
        line.Property(value => value.ProductId).HasColumnName("product_id");
        line.Property(value => value.ProductCode).HasColumnName("product_code").HasMaxLength(60);
        line.Property(value => value.ProductName).HasColumnName("product_name").HasMaxLength(200);
        line.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(10, 2);
        line.Property(value => value.UnitPrice).HasColumnName("unit_price").HasPrecision(14, 2);
        line.Property(value => value.DiscountPercentage).HasColumnName("discount_percentage").HasPrecision(5, 2);
        line.Property(value => value.TaxPercentage).HasColumnName("tax_percentage");
        line.Property(value => value.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
        line.Property(value => value.TaxAmount).HasColumnName("tax_amount").HasPrecision(14, 2);
        line.Property(value => value.Subtotal).HasColumnName("subtotal").HasPrecision(14, 2);
        line.Ignore(value => value.LineTotal);
        line.HasIndex(value => new { value.SaleId, value.Position }).IsUnique();
    }

    private static void ConfigurePayment(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<PosPayment>();
        payment.ToTable("sale_payments", "pos", table =>
            table.HasCheckConstraint("CK_sale_payments_method", "method IN ('Cash','Card','Transfer')"));
        payment.HasKey(value => value.Id);
        payment.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PosPaymentId(value))
            .ValueGeneratedNever();
        payment.Property(value => value.SaleId)
            .HasColumnName("sale_id")
            .HasConversion(id => id.Value, value => new PosSaleId(value));
        payment.Property(value => value.Position).HasColumnName("position");
        payment.Property(value => value.Method).HasColumnName("method").HasConversion<string>().HasMaxLength(10);
        payment.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        payment.Property(value => value.Tendered).HasColumnName("tendered").HasPrecision(14, 2);
        payment.Property(value => value.Reference).HasColumnName("reference").HasMaxLength(PosLimits.ReferenceMaxLength);
        payment.HasIndex(value => new { value.SaleId, value.Position }).IsUnique();
    }

    private static void ConfigureCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<PosSaleNumberCounter>();
        counter.ToTable("sale_number_counters", "pos");
        counter.HasKey(value => value.TenantId);
        counter.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PosOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Pos.Infrastructure.Persistence;

public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "pos"))
            .Options;
        return new PosDbContext(options);
    }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosUnitOfWork.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Npgsql;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice o constraint, no sólo por SqlState (spec,
/// «Traducción de errores»): hay varios únicos y devolver el código equivocado manda a corregir lo
/// equivocado.
/// </summary>
internal sealed class PosUnitOfWork(PosDbContext dbContext) : IPosUnitOfWork
{
    private const string OneOpenPerCashierIndex = "IX_cash_sessions_one_open_per_cashier";
    private const string SalesPrimaryKey = "PK_sales";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The cash session changed while the operation was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, OneOpenPerCashierIndex))
        {
            throw new PosDomainException(
                "pos.session.already_open", "The cashier already has an open cash session.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, SalesPrimaryKey))
        {
            // Interno: CreatePosSaleHandler lo consume y relee (paso 8). Nunca sale por HTTP.
            throw new PosDomainException("pos.sale.id_taken", "The sale id is already in use.");
        }
    }

    public async Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new PosTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        dbContext.ChangeTracker.Clear();
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class PosTransaction(IDbContextTransaction transaction) : IPosTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/CashSessionRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class CashSessionRepository(PosDbContext dbContext) : ICashSessionRepository
{
    public Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken) =>
        dbContext.CashSessions.SingleOrDefaultAsync(
            session => session.TenantId == tenantId && session.Id == id, cancellationToken);

    public Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken) =>
        dbContext.CashSessions.SingleOrDefaultAsync(
            session => session.TenantId == tenantId
                && session.CashierId == cashier
                && session.Status == CashSessionStatus.Open,
            cancellationToken);

    public void Add(CashSession session) => dbContext.CashSessions.Add(session);
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosSaleRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class PosSaleRepository(PosDbContext dbContext) : IPosSaleRepository
{
    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken) =>
        dbContext.Sales.SingleOrDefaultAsync(
            sale => sale.TenantId == tenantId && sale.Id == id, cancellationToken);

    public void Add(PosSale sale) => dbContext.Sales.Add(sale);
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosSaleNumberGenerator.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>Copia de OrderNumberGenerator sin la columna year: UPDATE ... RETURNING atómico sobre una fila por tenant.</summary>
internal sealed class PosSaleNumberGenerator(PosDbContext dbContext) : IPosSaleNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO pos.sale_number_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE pos.sale_number_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The POS sale number counter for tenant '{tenantId}' could not be read back.");
    }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosAuditPublisher.cs`:

```csharp
using System.Text.Json;
using Modules.Pos.Application;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class PosAuditPublisher(PosDbContext dbContext) : IPosAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new AuditEventPayload(
            tenantId, actorId, "Human", action, resourceType, resourceId, outcome, changedFields, "pos", occurredAt));

        dbContext.Outbox.Add(new PosOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minúscula como el resto de los payloads del outbox.
    private sealed record AuditEventPayload(
        Guid tenantId,
        Guid actorId,
        string actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        string source,
        DateTimeOffset occurredAt);
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosUserReferenceProbe.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Una venta es historia: el cajero no se borra mientras tenga una (spec, «Retención de
/// usuarios»). Copia de QuotationUserReferenceProbe.
/// </summary>
internal sealed class PosUserReferenceProbe(
    PosDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "pos";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        foreach (var membershipId in membershipIds)
        {
            var member = new MemberId(membershipId);
            if (await dbContext.CashSessions.AnyAsync(session => session.CashierId == member, cancellationToken)
                || await dbContext.Sales.IgnoreAutoIncludes().AnyAsync(
                    sale => sale.CashierId == member || sale.VoidedBy == member, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
```

`src/Modules/Pos/Modules.Pos.Infrastructure/PosDatabaseInitializer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Infrastructure.Persistence;

namespace Modules.Pos.Infrastructure;

public static class PosDatabaseInitializer
{
    public static async Task InitializePosDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
```

`PosInfrastructureExtensions.cs` queda:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Application;
using Modules.Pos.Infrastructure.Persistence;

namespace Modules.Pos.Infrastructure;

public static class PosInfrastructureExtensions
{
    public static IServiceCollection AddPosInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<PosDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "pos")));

        services.AddScoped<ICashSessionRepository, CashSessionRepository>();
        services.AddScoped<IPosSaleRepository, PosSaleRepository>();
        services.AddScoped<IPosSaleNumberGenerator, PosSaleNumberGenerator>();
        services.AddScoped<IPosUnitOfWork, PosUnitOfWork>();
        services.AddScoped<IPosAuditPublisher, PosAuditPublisher>();
        services.AddScoped<IUserReferenceProbe, PosUserReferenceProbe>();

        return services;
    }
}
```

En `src/Api/Program.cs`, agrega `using Modules.Pos.Infrastructure;` y, después de `InitializeQuotationsDatabaseAsync` (`:178-179`) y antes de `RunQepSeedAsync`:

```csharp
// Después de Companies: pos.cash_sessions lleva una FK real a companies.companies (spec,
// decisión 8), así que esa tabla tiene que existir cuando esta migración corre.
await app.Services.InitializePosDatabaseAsync(
    app.Lifetime.ApplicationStopping);
```

- [ ] **Step 5: Generar la migración y agregarle la FK a empresas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build src/Modules/Pos/Modules.Pos.Infrastructure
dotnet ef migrations add InitialPos --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations
```

Esperado: tres archivos nuevos en `Persistence/Migrations/`. Abre `<ts>_InitialPos.cs` y comprueba: `EnsureSchema(name: "pos")`; el índice `IX_cash_sessions_one_open_per_cashier` con `unique: true` y `filter: "status = 'Open'"`; `character(64)` en `request_fingerprint`; los cuatro `CK_*`; `FK_sales_cash_sessions_cash_session_id` con `ReferentialAction.Restrict`. Al final de `Up`, agrega:

```csharp
            // FK real a la empresa emisora (spec, decisión 8): borrar una empresa con cajas sale
            // como 422 companies.company.in_use por CompaniesUnitOfWork, sin código nuevo en
            // Companies. A mano porque EF no modela relaciones entre DbContext de módulos distintos
            // (mismo precedente que companies → geography).
            migrationBuilder.AddForeignKey(
                name: "FK_cash_sessions_companies_company_id",
                schema: "pos",
                table: "cash_sessions",
                column: "company_id",
                principalSchema: "companies",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
```

y al principio de `Down`:

```csharp
            migrationBuilder.DropForeignKey(
                name: "FK_cash_sessions_companies_company_id",
                schema: "pos",
                table: "cash_sessions");
```

Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
dotnet ef migrations has-pending-model-changes --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext
```

Esperado: `No changes have been made to the model since the last migration.`

- [ ] **Step 6: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --no-build --filter "FullyQualifiedName~PosPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en las seis de `PosPersistenceTests` y en arquitectura. Si `ASaleIdAlreadyUsedIsTranslatedToIdTaken` falla con `DbUpdateException` sin traducir, imprime `postgres.ConstraintName` en un `Assert.Fail` temporal y ajusta la constante `SalesPrimaryKey` al nombre real (y anótalo en el handoff).

- [ ] **Step 7: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Application src/Modules/Pos/Modules.Pos.Infrastructure src/Api/Program.cs tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs; git commit -m "feat(pos): persistencia del módulo Pos y migración InitialPos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---

### Task B7: Lectura exacta de producto por código en Catalog

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs`, `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductRepository.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductDetailsApiTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `Task<Product?> IProductRepository.FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)` — igualdad exacta con mayúsculas, `AsNoTracking`, sin `Include` de escalas, activos e inactivos. Lo consume el adaptador `PosProductLookup` (B12); Pos no referencia Catalog.

- [ ] **Step 1: Escribir la prueba que falla**

En `ProductDetailsApiTests.cs`, después de `AForeignKeyViolationOnTheTaxRateIsTranslatedInsteadOfCrashing`:

```csharp
    /// <summary>
    /// Spec 2026-10-07 (POS), decisión 30: el lector teclea Product.Code y el escaneo es una
    /// búsqueda por el índice único, exacta y con mayúsculas — no el ILIKE '%x%' con COUNT de
    /// SearchAsync. Devuelve también los inactivos: la caja los muestra marcados en vez de decir
    /// que no existen. Y no cruza tenants.
    /// </summary>
    [Fact]
    public async Task FindByCodeMatchesTheExactCodeOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        Assert.Empty(await ListAsync(client, TenantId));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();
        var product = Product.Create(
            ProductId.New(),
            Guid.Parse(TenantId),
            "Shampoo 400 ml",
            "SH-400",
            ProductDetails.Empty,
            new ProductPricing { BaseCop = 11_900m },
            DateTimeOffset.UtcNow);
        repository.Add(product);
        await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exact = await repository.FindByCodeAsync(Guid.Parse(TenantId), "SH-400", TestContext.Current.CancellationToken);
        var lowercase = await repository.FindByCodeAsync(Guid.Parse(TenantId), "sh-400", TestContext.Current.CancellationToken);
        var partial = await repository.FindByCodeAsync(Guid.Parse(TenantId), "SH-40", TestContext.Current.CancellationToken);
        var otherTenant = await repository.FindByCodeAsync(Guid.Parse(OtherTenantId), "SH-400", TestContext.Current.CancellationToken);

        Assert.NotNull(exact);
        Assert.Equal(product.Id, exact.Id);
        Assert.Null(lowercase);
        Assert.Null(partial);
        Assert.Null(otherTenant);
    }
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Catalog/Modules.Catalog.IntegrationTests
```

Esperado: FAIL de compilación, `CS1061: 'IProductRepository' does not contain a definition for 'FindByCodeAsync'`.

- [ ] **Step 3: Implementar**

En `IProductRepository.cs`, después de `FindAsync`:

```csharp
    /// <summary>
    /// Igualdad exacta, con mayúsculas, servida por IX_products_tenant_code (spec 2026-10-07,
    /// decisión 30). Sin respaldo sin mayúsculas: el lector teclea el código tal cual. Activos e
    /// inactivos; sin escalas, sin conteo, sin rastreo.
    /// </summary>
    Task<Product?> FindByCodeAsync(
        Guid tenantId,
        string code,
        CancellationToken cancellationToken);
```

En `ProductRepository.cs`, después de `FindAsync`:

```csharp
    public Task<Product?> FindByCodeAsync(
        Guid tenantId,
        string code,
        CancellationToken cancellationToken) =>
        dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(
                product => product.TenantId == tenantId && product.Code == code,
                cancellationToken);
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --no-build --filter "FullyQualifiedName~ProductDetailsApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductRepository.cs tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductDetailsApiTests.cs; git commit -m "feat(catalog): búsqueda exacta de producto por código"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---


### Task B8: Abrir, cerrar y consultar la caja (aplicación)

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/{PosAuthorization,PosCashierResolver,PosNotFound,PosDtos,IPosCompanyLookup,IPosCashierLookup,PosSessionMapping,GetRegisterContext,OpenCashSession,CloseCashSession}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers después de los de Companies `:199`; validadores `:436`)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/RegisterHandlersTests.cs`

**Interfaces:**
- Consumes: B4-B6.
- Produces:
  - `internal static class PosAuthorization { void EnsureAuthorized(IExecutionContext, Guid tenantId, string permission); RequestForbiddenException Denied(); }`
  - `internal static class PosCashierResolver { Task<MemberId> ResolveAsync(IMembershipDirectory, IExecutionContext, Guid tenantId, CancellationToken); }`
  - `internal static class PosNotFound { ResourceNotFoundException Session(Guid); ResourceNotFoundException Sale(Guid); ResourceNotFoundException ProductCode(string); }`
  - `IPosCompanyLookup { Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken); Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken); }` y `public sealed record PosCompanyRef(Guid Id, string Name, string TaxId, string? Address, string? Phone, bool IsActive)`
  - `IPosCashierLookup { Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken); }` (`DisplayName ?? Email`)
  - Todos los DTOs de `PosDtos.cs` (los usan B9-B12 y el contrato del frontend)
  - `GetRegisterContextQuery(Guid TenantId) : IQuery<RegisterContextResponse>`, `OpenCashSessionCommand(Guid TenantId, Guid? CompanyId, decimal OpeningFloat) : ICommand<PosOpenSessionResponse>`, `CloseCashSessionCommand(Guid TenantId, Guid SessionId, long ExpectedVersion, decimal CountedCash, string? Note) : ICommand<PosSessionSummaryResponse>`, con sus handlers y validadores
  - Dobles (pruebas): `PosTestBed`, `FakeExecutionContext`, `FakeMembershipDirectory`, `FakeClock`, `FakeTenantClock`, `InMemoryCashSessionRepository`, `InMemoryPosSaleRepository`, `FakeUnitOfWork`, `FakeNumberGenerator`, `RecordingAuditPublisher`, `FakeCompanyLookup`, `FakeCashierLookup`

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

`tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pos.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class FakeTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, Bogota));
}

internal sealed class InMemoryCashSessionRepository : ICashSessionRepository
{
    public List<CashSession> Sessions { get; } = [];

    public Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session => session.TenantId == tenantId && session.Id == id));

    public Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(session =>
            session.TenantId == tenantId && session.CashierId == cashier && session.Status == CashSessionStatus.Open));

    public void Add(CashSession session) => Sessions.Add(session);
}

/// <summary>
/// Sólo encuentra lo "commiteado" (Stored), como una consulta real: lo agregado sin guardar no
/// aparece en FindAsync. FakeUnitOfWork pasa Added a Stored al guardar y lo descarta en Reset.
/// </summary>
internal sealed class InMemoryPosSaleRepository(InMemoryCashSessionRepository sessions) : IPosSaleRepository
{
    public List<PosSale> Stored { get; } = [];

    public List<PosSale> Added { get; } = [];

    public InMemoryCashSessionRepository Sessions { get; } = sessions;

    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.SingleOrDefault(sale => sale.TenantId == tenantId && sale.Id == id));

    public void Add(PosSale sale) => Added.Add(sale);

    public void Commit()
    {
        Stored.AddRange(Added);
        Added.Clear();
    }

    public void Discard() => Added.Clear();
}

internal sealed class FakeUnitOfWork(InMemoryPosSaleRepository sales) : IPosUnitOfWork
{
    public int SaveCalls { get; private set; }

    public int ResetCalls { get; private set; }

    public int Commits { get; private set; }

    /// <summary>Cada SaveChanges saca un gancho; si devuelve una excepción, la lanza.</summary>
    public Queue<Func<Exception?>> OnSave { get; } = new();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCalls++;
        if (OnSave.TryDequeue(out var hook) && hook() is { } exception)
        {
            throw exception;
        }

        sales.Commit();
        return Task.FromResult(1);
    }

    public Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IPosTransaction>(new FakeTransaction(this));

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCalls++;
        sales.Discard();
        return Task.CompletedTask;
    }

    private sealed class FakeTransaction(FakeUnitOfWork owner) : IPosTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeNumberGenerator : IPosSaleNumberGenerator
{
    private long _next = 1;

    public int Calls { get; private set; }

    public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_next++);
    }
}

internal sealed record AuditEntry(
    Guid TenantId, Guid ActorId, string Action, string ResourceType, string ResourceId,
    IReadOnlyCollection<string> ChangedFields);

internal sealed class RecordingAuditPublisher : IPosAuditPublisher
{
    public List<AuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceType, string resourceId,
        string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt) =>
        Entries.Add(new AuditEntry(tenantId, actorId, action, resourceType, resourceId, changedFields));
}

internal sealed class FakeCompanyLookup : IPosCompanyLookup
{
    public List<(Guid TenantId, PosCompanyRef Company)> Companies { get; } = [];

    public Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PosCompanyRef>>(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.IsActive)
            .Select(entry => entry.Company)
            .ToList());

    public Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies
            .Where(entry => entry.TenantId == tenantId && entry.Company.Id == companyId)
            .Select(entry => entry.Company)
            .SingleOrDefault());
}

internal sealed class FakeCashierLookup : IPosCashierLookup
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        Task.FromResult(Names.TryGetValue(membershipId, out var name) ? name : null);
}

/// <summary>Todo lo que un handler del POS necesita, con el cajero del ejemplo ya miembro activo.</summary>
internal sealed class PosTestBed
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");

    public PosTestBed()
    {
        Sales = new InMemoryPosSaleRepository(Sessions);
        UnitOfWork = new FakeUnitOfWork(Sales);
        Memberships.Active[(UserId, PosFixtures.TenantId)] = PosFixtures.Cashier.Value;
        Cashiers.Names[PosFixtures.Cashier.Value] = "Laura Gómez";
    }

    public FakeMembershipDirectory Memberships { get; } = new();

    public InMemoryCashSessionRepository Sessions { get; } = new();

    public InMemoryPosSaleRepository Sales { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeNumberGenerator Numbers { get; } = new();

    public RecordingAuditPublisher Audit { get; } = new();

    public FakeCompanyLookup Companies { get; } = new();

    public FakeCashierLookup Cashiers { get; } = new();

    public FakeClock Clock { get; } = new(PosFixtures.Now);

    public FakeTenantClock TenantClock { get; } = new(PosFixtures.Now);

    public FakeExecutionContext Context(params string[] permissions) =>
        new(PosFixtures.TenantId, UserId, permissions);

    public CashSession OpenSessionInStore(decimal openingFloat = 100_000m)
    {
        var session = PosFixtures.OpenSession(openingFloat);
        Sessions.Add(session);
        return session;
    }

    public PosCompanyRef AddCompany(string name = "Origen Botánico SAS", bool active = true, Guid? tenantId = null)
    {
        var company = new PosCompanyRef(Guid.CreateVersion7(), name, "900123456-1", "Cra 50 # 10-20", "6045551234", active);
        Companies.Companies.Add((tenantId ?? PosFixtures.TenantId, company));
        return company;
    }
}
```

`tests/Modules/Pos/Modules.Pos.UnitTests/RegisterHandlersTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class RegisterHandlersTests
{
    private static readonly string[] Operator = [PosPermissions.RegisterOperate];

    private static GetRegisterContextHandler Context(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Companies, bed.Cashiers, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    private static OpenCashSessionHandler Open(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Companies, bed.Cashiers, bed.UnitOfWork, bed.Audit, bed.Memberships,
            bed.Context(permissions), bed.Clock, bed.TenantClock, new OpenCashSessionValidator());

    private static CloseCashSessionHandler Close(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.UnitOfWork, bed.Audit, bed.Memberships, bed.Context(permissions),
            bed.Clock, bed.TenantClock, new CloseCashSessionValidator());

    [Fact]
    public async Task RegisterContextWithoutASessionOffersTheOnlyActiveCompanyAsDefault()
    {
        var bed = new PosTestBed();
        var company = bed.AddCompany();
        bed.AddCompany("Inactiva SAS", active: false);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Null(context.Session);
        Assert.Equal(new PosCashierResponse(Cashier.Value, "Laura Gómez"), context.Cashier);
        Assert.Equal(company.Id, context.DefaultCompanyId);
        Assert.Equal([company.Id], context.Companies.Select(option => option.Id).ToArray());
    }

    [Fact]
    public async Task RegisterContextWithTwoActiveCompaniesHasNoDefault()
    {
        var bed = new PosTestBed();
        bed.AddCompany("Una SAS");
        bed.AddCompany("Otra SAS");

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Null(context.DefaultCompanyId);
        Assert.Equal(2, context.Companies.Count);
    }

    [Fact]
    public async Task RegisterContextCarriesTheOpenSessionWithItsVersionAndTheThreeMethods()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Session);
        Assert.Equal(session.Id.Value, context.Session.Id);
        Assert.Equal("Open", context.Session.Status);
        Assert.Equal(2, context.Session.Version);
        Assert.Equal(117_890m, context.Session.ExpectedCash);
        Assert.Equal(
            new[] { new PosPaymentTotalResponse("Cash", 17_890m), new PosPaymentTotalResponse("Card", 20_000m), new PosPaymentTotalResponse("Transfer", 0m) },
            context.Session.PaymentTotals);
        Assert.Equal(TimeSpan.FromHours(-5), context.Session.OpenedAtLocal.Offset);
        Assert.False(context.Session.OpenedBeforeToday);
    }

    // 04:00 UTC del 7 son las 23:00 del 6 en Bogotá: la caja quedó abierta desde ayer aunque la
    // fecha UTC sea la misma (spec, endpoint 1, openedBeforeToday).
    [Fact]
    public async Task RegisterContextFlagsASessionOpenedYesterdayInTheTenantTimeZone()
    {
        var bed = new PosTestBed();
        var session = CashSession.Open(
            CashSessionId.New(), TenantId, Cashier, "Laura Gómez", Company, 100_000m,
            new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.Zero));
        bed.Sessions.Add(session);

        var context = await Context(bed, Operator).HandleAsync(
            new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(context.Session!.OpenedBeforeToday);
    }

    [Fact]
    public async Task RegisterContextNeedsTheOperatePermissionAndTheRouteTenant()
    {
        var bed = new PosTestBed();

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Context(bed, PosPermissions.SaleRead).HandleAsync(new GetRegisterContextQuery(TenantId), TestContext.Current.CancellationToken));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Context(bed, Operator).HandleAsync(new GetRegisterContextQuery(Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
    }

    [Fact]
    public async Task OpenUsesTheOnlyActiveCompanyAndAudits()
    {
        var bed = new PosTestBed();
        var company = bed.AddCompany();

        var opened = await Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 100_000m), TestContext.Current.CancellationToken);

        var session = Assert.Single(bed.Sessions.Sessions);
        Assert.Equal(company.Id, session.CompanyId);
        Assert.Equal("Laura Gómez", session.CashierName);
        Assert.Equal(Cashier, session.CashierId);
        Assert.Equal(1, opened.Version);
        Assert.Equal(100_000m, opened.ExpectedCash);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(("pos.session.opened", "cash_session", session.Id.ToString()), (audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(PosTestBed.UserId, audit.ActorId);
    }

    [Fact]
    public async Task OpenWithTwoActiveCompaniesNeedsOneAndUsesTheChosenOne()
    {
        var bed = new PosTestBed();
        bed.AddCompany("Una SAS");
        var chosen = bed.AddCompany("Otra SAS");

        var missing = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));
        await Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, chosen.Id, 0m), TestContext.Current.CancellationToken);

        Assert.Equal("pos.session.company_required", missing.Code);
        Assert.Equal(chosen.Id, Assert.Single(bed.Sessions.Sessions).CompanyId);
    }

    [Fact]
    public async Task OpenRejectsMissingForeignAndInactiveCompanies()
    {
        var bed = new PosTestBed();
        var foreign = bed.AddCompany("Ajena SAS", tenantId: Guid.CreateVersion7());
        var inactive = bed.AddCompany("Inactiva SAS", active: false);

        var none = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));
        var other = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, foreign.Id, 0m), TestContext.Current.CancellationToken));
        var off = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, inactive.Id, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("pos.session.no_active_company", none.Code);
        Assert.Equal("pos.session.company_not_found", other.Code);
        Assert.Equal("pos.session.company_inactive", off.Code);
        Assert.Empty(bed.Sessions.Sessions);
    }

    [Fact]
    public async Task OpenWithASessionAlreadyOpenIsRejected()
    {
        var bed = new PosTestBed();
        bed.AddCompany();
        bed.OpenSessionInStore();

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("pos.session.already_open", error.Code);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task OpenValidatesTheFloatScaleBeforeTouchingAnything()
    {
        var bed = new PosTestBed();
        bed.AddCompany();

        await Assert.ThrowsAsync<ValidationException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 10.005m), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 100_000_000.01m), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Sessions.Sessions);
    }

    [Fact]
    public async Task OpenWithoutAnActiveMembershipIsForbidden()
    {
        var bed = new PosTestBed();
        bed.Memberships.Active.Clear();
        bed.AddCompany();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Open(bed, Operator).HandleAsync(
            new OpenCashSessionCommand(TenantId, null, 0m), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task CloseReturnsTheSummaryWithASignedDifferenceAndAudits()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var summary = await Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 2, 117_000m, "Faltan 890"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Closed", summary.Status);
        Assert.Equal(117_890m, summary.ExpectedCash);
        Assert.Equal(117_000m, summary.CountedCash);
        Assert.Equal(-890m, summary.CashDifference);
        Assert.Equal("Faltan 890", summary.Note);
        Assert.Equal("pos.session.closed", Assert.Single(bed.Audit.Entries).Action);
    }

    // El cajero cierra contra el arqueo que vio (spec, «Cerrar caja», paso 2).
    [Fact]
    public async Task CloseWithAStaleVersionIs412AndLeavesTheSessionOpen()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        session.RegisterSale(Sale(session), Now);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 117_890m, null),
            TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(CashSessionStatus.Open, session.Status);
        Assert.Equal(0, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task CloseSomeoneElsesSessionIsForbiddenAndAnUnknownOneIsNotFound()
    {
        var bed = new PosTestBed();
        var foreign = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(foreign);

        var forbidden = await Assert.ThrowsAsync<RequestForbiddenException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, foreign.Id.Value, 1, 0m, null), TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, Guid.CreateVersion7(), 1, 0m, null), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", forbidden.Code);
        Assert.Equal("pos.session.not_found", missing.Code);
    }

    [Fact]
    public async Task CloseValidatesTheCountAndTheNote()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();

        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 1_000_000_000.01m, null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 1.001m, null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => Close(bed, Operator).HandleAsync(
            new CloseCashSessionCommand(TenantId, session.Id.Value, 1, 0m, new string('x', 501)), TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0246` para `IPosCompanyLookup`, `IPosCashierLookup`, `PosCompanyRef`, `GetRegisterContextHandler`, `OpenCashSessionHandler`, `CloseCashSessionHandler`, …).

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Application/PosAuthorization.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// Copia de CompaniesAuthorization: tenant de la ruta distinto del activo, o permiso faltante →
/// 403. Nunca 404: confirmaría que el id existe en otro tenant.
/// </summary>
internal static class PosAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this pos operation for this tenant.");
}
```

`src/Modules/Pos/Modules.Pos.Application/PosCashierResolver.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Subject autenticado → su membresía activa (copia de QuotationAdvisorResolver).</summary>
internal static class PosCashierResolver
{
    public static async Task<MemberId> ResolveAsync(
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membershipId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, tenantId, cancellationToken);
        return membershipId is { } value
            ? new MemberId(value)
            : throw new RequestForbiddenException(
                "authorization.denied",
                "The subject does not have an active membership in this tenant.");
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/PosNotFound.cs`:

```csharp
using BuildingBlocks.Application;

namespace Modules.Pos.Application;

/// <summary>404 dentro del tenant de la ruta (spec, decisión 29): los repositorios filtran por tenant.</summary>
internal static class PosNotFound
{
    public static ResourceNotFoundException Session(Guid id) =>
        new("pos.session.not_found", $"Cash session '{id}' was not found.");

    public static ResourceNotFoundException Sale(Guid id) =>
        new("pos.sale.not_found", $"Sale '{id}' was not found.");

    public static ResourceNotFoundException ProductCode(string code) =>
        new("pos.product.not_found", $"No product has the code '{code}'.");
}
```

`src/Modules/Pos/Modules.Pos.Application/IPosCompanyLookup.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>Puerto hacia Companies; el adaptador vive en Bootstrapper.</summary>
public interface IPosCompanyLookup
{
    Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Activas e inactivas: el handler decide. Null si no existe en el tenant.</summary>
    Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

public sealed record PosCompanyRef(Guid Id, string Name, string TaxId, string? Address, string? Phone, bool IsActive);
```

`src/Modules/Pos/Modules.Pos.Application/IPosCashierLookup.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>Nombre visible de una membresía: DisplayName ?? Email, como QuotationAdvisorLookup.</summary>
public interface IPosCashierLookup
{
    Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);
}
```

`src/Modules/Pos/Modules.Pos.Application/PosDtos.cs`:

```csharp
namespace Modules.Pos.Application;

// Respuestas BFF del punto de venta (spec 2026-10-07, «API»). Se diseñan por lo que la pantalla
// dibuja: los enums viajan por nombre (el diccionario de etiquetas lo tiene el frontend), las
// colecciones de tamaño fijo viajan completas incluso en cero, y las horas viajan ya en el huso
// del tenant porque la pantalla no lo conoce.

public sealed record PosCashierResponse(Guid MemberId, string Name);

public sealed record PosCompanyOption(Guid Id, string Name, string TaxId);

public sealed record PosCompanyHeader(string Name, string TaxId);

/// <summary>Los tres medios siempre, en cero si no hubo: la pantalla no conoce el enum.</summary>
public sealed record PosPaymentTotalResponse(string Method, decimal Amount);

/// <param name="OpenedBeforeToday">Fecha local de apertura &lt; hoy del tenant: la pantalla avisa de una caja que quedó abierta desde otro día.</param>
/// <param name="ExpectedCash">Vivo: base + efectivo neto.</param>
/// <param name="Version">La que el cierre manda en If-Match (1 al abrir, +1 por venta o anulación).</param>
public sealed record PosOpenSessionResponse(
    Guid Id,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset OpenedAtLocal,
    bool OpenedBeforeToday,
    PosCompanyOption Company,
    decimal OpeningFloat,
    int SalesCount,
    int VoidedCount,
    decimal SalesTotal,
    decimal ExpectedCash,
    long Version,
    IReadOnlyList<PosPaymentTotalResponse> PaymentTotals);

/// <param name="Session">null sin caja abierta: no tener caja es un estado, no un 404.</param>
/// <param name="Companies">Viaja aquí porque el cajero no tiene companies.company.read y el selector de apertura la necesita.</param>
/// <param name="DefaultCompanyId">null cuando hay que elegir (cero o más de una activa).</param>
public sealed record RegisterContextResponse(
    PosCashierResponse Cashier,
    PosOpenSessionResponse? Session,
    IReadOnlyList<PosCompanyOption> Companies,
    Guid? DefaultCompanyId);

/// <param name="CashDifference">Con signo y calculada: negativo = faltante. La pantalla sólo elige el color.</param>
public sealed record PosSessionSummaryResponse(
    Guid Id,
    string Status,
    string CashierName,
    PosCompanyHeader Company,
    DateTimeOffset OpenedAtLocal,
    DateTimeOffset? ClosedAtLocal,
    decimal OpeningFloat,
    int SalesCount,
    int VoidedCount,
    decimal SalesTotal,
    IReadOnlyList<PosPaymentTotalResponse> PaymentTotals,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? CashDifference,
    string? Note);

public sealed record PosPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <param name="UnsellableReason">Inactive, PriceMissing o NotFound (este último sólo en el preview). Un producto no vendible se muestra marcado en vez de esconderse: el cajero escanearía un código existente y vería "no existe".</param>
public sealed record PosProductResponse(
    Guid Id,
    string Code,
    string Name,
    decimal? UnitPrice,
    int TaxPercentage,
    string? ImageUrl,
    bool Sellable,
    string? UnsellableReason);

public sealed record PosPreviewLineRequest(Guid ProductId, decimal Quantity, decimal DiscountPercentage);

/// <param name="Code">null con NotFound: la pantalla lo toma del snapshot del carrito.</param>
public sealed record PosPreviewLineResponse(
    Guid ProductId,
    string? Code,
    string? Name,
    decimal Quantity,
    decimal? UnitPrice,
    int? TaxPercentage,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Subtotal,
    decimal LineTotal,
    bool Sellable,
    string? UnsellableReason);

/// <summary>
/// Existe para que el frontend nunca calcule IVA. Una línea no vendible vuelve marcada y fuera de
/// los totales, no como 422: el carrito tiene que poder mostrar qué está mal.
/// </summary>
/// <param name="ZeroTotalNotAllowed">Total 0 con al menos una línea vendible y sin pos.sale.discount: la pantalla bloquea "Cobrar" con el motivo; sólo el POST responde 403.</param>
public sealed record PosPreviewResponse(
    IReadOnlyList<PosPreviewLineResponse> Lines,
    decimal Subtotal,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    bool ZeroTotalNotAllowed);

public sealed record PosSaleLineRequest(
    Guid ProductId,
    decimal Quantity,
    decimal DiscountPercentage,
    decimal ExpectedUnitPrice,
    int ExpectedTaxPercentage);

/// <param name="Method">Cash, Card o Transfer, por nombre exacto.</param>
/// <param name="Amount">Sólo Card/Transfer; en Cash lo calcula el servidor.</param>
/// <param name="Tendered">Sólo Cash: el billete del cliente.</param>
public sealed record PosPaymentRequest(string Method, decimal? Amount, decimal? Tendered, string? Reference);

public sealed record PosIssuerResponse(string Name, string TaxId, string? Address, string? Phone);

public sealed record PosCustomerResponse(string Name, string? IdentificationType, string IdentificationNumber);

public sealed record PosSaleLineResponse(
    int Position,
    Guid ProductId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Subtotal,
    decimal LineTotal);

public sealed record PosTaxBreakdownResponse(int TaxPercentage, decimal Base, decimal TaxAmount);

public sealed record PosPaymentResponse(string Method, decimal Amount, decimal? Tendered, string? Reference);

public sealed record PosVoidResponse(string Reason, DateTimeOffset VoidedAtLocal, string VoidedByName);

/// <summary>
/// La respuesta de la venta y a la vez los datos del ticket: emisor congelado, hora local, desglose
/// de IVA y cambio. El frontend no arma nada.
/// </summary>
/// <param name="Voidable">Estado de la venta, no del que pregunta: Completed y su caja Open. Sin él la lista de ventas tendría que pedir una caja por fila.</param>
/// <param name="VoidBlockedReason">AlreadyVoided o SessionClosed; null con Voidable.</param>
public sealed record PosSaleResponse(
    Guid Id,
    string SaleNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset CreatedAtLocal,
    Guid CashSessionId,
    string CashierName,
    PosIssuerResponse Issuer,
    PosCustomerResponse Customer,
    IReadOnlyList<PosSaleLineResponse> Lines,
    decimal Subtotal,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    IReadOnlyList<PosTaxBreakdownResponse> TaxBreakdown,
    IReadOnlyList<PosPaymentResponse> Payments,
    decimal ChangeAmount,
    PosVoidResponse? Void,
    bool Voidable,
    string? VoidBlockedReason);

public sealed record PosSaleListItemResponse(
    Guid Id,
    string SaleNumber,
    DateTimeOffset CreatedAtLocal,
    string CashierName,
    string CustomerName,
    decimal Total,
    string Status,
    IReadOnlyList<string> PaymentMethods,
    bool Voidable,
    string? VoidBlockedReason);
```

`src/Modules/Pos/Modules.Pos.Application/PosSessionMapping.cs`:

```csharp
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

internal static class PosSessionMapping
{
    public static IReadOnlyList<PosPaymentTotalResponse> PaymentTotals(CashSession session) =>
    [
        new(nameof(PosPaymentMethod.Cash), session.CashTotal),
        new(nameof(PosPaymentMethod.Card), session.CardTotal),
        new(nameof(PosPaymentMethod.Transfer), session.TransferTotal),
    ];

    public static PosOpenSessionResponse ToOpenResponse(CashSession session, TenantCalendar calendar)
    {
        var openedLocal = calendar.ToLocal(session.OpenedAt);
        return new PosOpenSessionResponse(
            session.Id.Value,
            session.Status.ToString(),
            session.OpenedAt,
            openedLocal,
            DateOnly.FromDateTime(openedLocal.DateTime) < calendar.Today,
            new PosCompanyOption(session.CompanyId, session.CompanyName, session.CompanyTaxId),
            session.OpeningFloat,
            session.SalesCount,
            session.VoidedCount,
            session.SalesTotal,
            session.LiveExpectedCash,
            session.Version,
            PaymentTotals(session));
    }

    public static PosSessionSummaryResponse ToSummary(CashSession session, TenantCalendar calendar) =>
        new(
            session.Id.Value,
            session.Status.ToString(),
            session.CashierName,
            new PosCompanyHeader(session.CompanyName, session.CompanyTaxId),
            calendar.ToLocal(session.OpenedAt),
            session.ClosedAt is { } closedAt ? calendar.ToLocal(closedAt) : null,
            session.OpeningFloat,
            session.SalesCount,
            session.VoidedCount,
            session.SalesTotal,
            PaymentTotals(session),
            session.ExpectedCash ?? session.LiveExpectedCash,
            session.CountedCash,
            session.CashDifference,
            session.ClosingNote);
}
```

`src/Modules/Pos/Modules.Pos.Application/GetRegisterContext.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Todo lo que la pantalla de caja necesita para decidir qué dibujar, en un viaje (endpoint 1).</summary>
public sealed record GetRegisterContextQuery(Guid TenantId) : IQuery<RegisterContextResponse>;

public sealed class GetRegisterContextHandler(
    ICashSessionRepository sessions,
    IPosCompanyLookup companies,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetRegisterContextQuery, RegisterContextResponse>
{
    public async Task<RegisterContextResponse> HandleAsync(
        GetRegisterContextQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.RegisterOperate);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var name = await cashiers.FindNameAsync(query.TenantId, cashier.Value, cancellationToken) ?? cashier.Value.ToString();
        var session = await sessions.FindOpenByCashierAsync(query.TenantId, cashier, cancellationToken);
        var active = await companies.ListActiveAsync(query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        return new RegisterContextResponse(
            new PosCashierResponse(cashier.Value, name),
            session is null ? null : PosSessionMapping.ToOpenResponse(session, calendar),
            active.Select(company => new PosCompanyOption(company.Id, company.Name, company.TaxId)).ToArray(),
            active.Count == 1 ? active[0].Id : null);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/OpenCashSession.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record OpenCashSessionCommand(Guid TenantId, Guid? CompanyId, decimal OpeningFloat)
    : ICommand<PosOpenSessionResponse>;

// El validador da el campo (422 validation.failed con errors); el dominio da el código. Los dos
// existen aunque se repitan, como pide la convención.
public sealed class OpenCashSessionValidator : AbstractValidator<OpenCashSessionCommand>
{
    public OpenCashSessionValidator()
    {
        RuleFor(command => command.OpeningFloat)
            .InclusiveBetween(0m, PosLimits.MaxCashAmount)
            .Must(PosLimits.HasValidScale).WithMessage("The opening float accepts at most 2 decimals.");
    }
}

public sealed class OpenCashSessionHandler(
    ICashSessionRepository sessions,
    IPosCompanyLookup companies,
    IPosCashierLookup cashiers,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<OpenCashSessionCommand> validator)
    : ICommandHandler<OpenCashSessionCommand, PosOpenSessionResponse>
{
    public async Task<PosOpenSessionResponse> HandleAsync(
        OpenCashSessionCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: un llamador ajeno no se lleva el mapa de errores.
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.RegisterOperate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Por legibilidad; dos aperturas simultáneas sólo las frena el índice parcial.
        if (await sessions.FindOpenByCashierAsync(command.TenantId, cashier, cancellationToken) is not null)
        {
            throw new PosDomainException("pos.session.already_open", "The cashier already has an open cash session.");
        }

        var company = await ResolveCompanyAsync(command, cancellationToken);
        var name = await cashiers.FindNameAsync(command.TenantId, cashier.Value, cancellationToken) ?? cashier.Value.ToString();
        var now = clock.UtcNow;
        var session = CashSession.Open(
            CashSessionId.New(),
            command.TenantId,
            cashier,
            name,
            new PosCompanySnapshot(company.Id, company.Name, company.TaxId, company.Address, company.Phone),
            command.OpeningFloat,
            now);

        sessions.Add(session);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.session.opened", "cash_session",
            session.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        return PosSessionMapping.ToOpenResponse(session, calendar);
    }

    private async Task<PosCompanyRef> ResolveCompanyAsync(OpenCashSessionCommand command, CancellationToken cancellationToken)
    {
        if (command.CompanyId is { } companyId)
        {
            // Mismo código para "no existe" y "es de otro tenant".
            var chosen = await companies.FindAsync(command.TenantId, companyId, cancellationToken)
                ?? throw new PosDomainException("pos.session.company_not_found", "The company was not found.");
            return chosen.IsActive
                ? chosen
                : throw new PosDomainException("pos.session.company_inactive", "The company is inactive.");
        }

        var active = await companies.ListActiveAsync(command.TenantId, cancellationToken);
        return active.Count switch
        {
            0 => throw new PosDomainException("pos.session.no_active_company", "The tenant has no active company."),
            1 => active[0],
            _ => throw new PosDomainException("pos.session.company_required", "Choose the issuing company."),
        };
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/CloseCashSession.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="ExpectedVersion">La version de GET /pos/register que pintó el arqueo; llega por If-Match.</param>
public sealed record CloseCashSessionCommand(
    Guid TenantId, Guid SessionId, long ExpectedVersion, decimal CountedCash, string? Note)
    : ICommand<PosSessionSummaryResponse>;

public sealed class CloseCashSessionValidator : AbstractValidator<CloseCashSessionCommand>
{
    public CloseCashSessionValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        RuleFor(command => command.CountedCash)
            .InclusiveBetween(0m, PosLimits.MaxCountedCash)
            .Must(PosLimits.HasValidScale).WithMessage("The counted cash accepts at most 2 decimals.");
        RuleFor(command => command.Note).MaximumLength(PosLimits.NoteMaxLength);
    }
}

public sealed class CloseCashSessionHandler(
    ICashSessionRepository sessions,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<CloseCashSessionCommand> validator)
    : ICommandHandler<CloseCashSessionCommand, PosSessionSummaryResponse>
{
    public async Task<PosSessionSummaryResponse> HandleAsync(
        CloseCashSessionCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.RegisterOperate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var session = await sessions.FindAsync(command.TenantId, new CashSessionId(command.SessionId), cancellationToken)
            ?? throw PosNotFound.Session(command.SessionId);
        if (session.CashierId != cashier)
        {
            // Sólo el cajero dueño cierra (spec, decisión 5; el cierre forzado es DECISIÓN-PENDIENTE 7).
            throw PosAuthorization.Denied();
        }

        // El cajero cierra contra el arqueo que vio: si entró una venta o una anulación después, el
        // cierre no la absorbe sin que la pantalla la muestre.
        if (session.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The cash session changed after the count was loaded.");
        }

        var now = clock.UtcNow;
        session.Close(command.CountedCash, command.Note, now);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.session.closed", "cash_session",
            session.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PosSessionMapping.ToSummary(session, await tenantClock.GetAsync(command.TenantId, cancellationToken));
    }
}
```

En `QepServiceCollectionExtensions.cs`, agrega `using Modules.Pos.Application;` si falta y, después del último handler de Companies (`:199`):

```csharp
        // Pos (spec 2026-10-07). Registro a mano, uno por uno, como los demás: un handler olvidado
        // responde 500 y CompositionRootTests lo detecta.
        services.AddScoped<
            IQueryHandler<GetRegisterContextQuery, RegisterContextResponse>,
            GetRegisterContextHandler>();
        services.AddScoped<
            ICommandHandler<OpenCashSessionCommand, PosOpenSessionResponse>,
            OpenCashSessionHandler>();
        services.AddScoped<
            ICommandHandler<CloseCashSessionCommand, PosSessionSummaryResponse>,
            CloseCashSessionHandler>();
```

y después de `services.AddValidatorsFromAssemblyContaining<OrdersReportFilterValidator>();` (`:436`):

```csharp
        services.AddValidatorsFromAssemblyContaining<OpenCashSessionValidator>();
```

**Nota:** `GetRegisterContextHandler` y `OpenCashSessionHandler` dependen de `IPosCompanyLookup` y `IPosCashierLookup`, cuyos adaptadores llegan en la Task B12. Hasta entonces `CompositionRootTests` sólo comprueba que el handler esté registrado (no lo resuelve), así que sigue verde; por HTTP nada los llama todavía porque no hay endpoints.

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build --filter "FullyQualifiedName~RegisterHandlersTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en las dos. Si `CompositionRootTests` intenta **resolver** handlers (no sólo verificar el registro) y falla por los dos puertos sin adaptador, mueve el registro de los adaptadores de la Task B12 (Step 3, sólo los `AddScoped` de `PosCompanyLookup` y `PosCashierLookup` y sus dos archivos) a esta tarea y anótalo.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs tests/Modules/Pos/Modules.Pos.UnitTests/RegisterHandlersTests.cs; git commit -m "feat(pos): abrir, cerrar y consultar la caja"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---

### Task B9: Búsqueda de productos, escaneo por código y preview de la venta

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/{IPosProductLookup,PosProductMapping,SearchPosProducts,FindPosProductByCode,PreviewPosSale}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers), `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs` (`FakeProductLookup` y `PosTestBed.Products`)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/ProductAndPreviewHandlersTests.cs`

**Interfaces:**
- Consumes: B8 (`PosAuthorization`, `PosNotFound`, DTOs).
- Produces:
  - `IPosProductLookup { Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(Guid tenantId, string? search, int page, int pageSize, CancellationToken); Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken); Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken); }`
  - `public sealed record PosProductRef(Guid Id, string Code, string Name, bool IsActive, decimal? PriceCop, int TaxPercentage, string? ImageUrl)`
  - `internal static class PosProductMapping { PosProductResponse ToResponse(PosProductRef); string? UnsellableReason(PosProductRef); }`
  - `SearchPosProductsQuery(Guid TenantId, string? Search, int Page, int PageSize) : IQuery<PosPage<PosProductResponse>>`, `FindPosProductByCodeQuery(Guid TenantId, string Code) : IQuery<PosProductResponse>`, `PreviewPosSaleCommand(Guid TenantId, IReadOnlyList<PosPreviewLineRequest> Lines) : ICommand<PosPreviewResponse>`, con handlers y validadores

- [ ] **Step 1: Escribir el doble y las pruebas que fallan**

Agrega a `PosTestDoubles.cs`:

```csharp
internal sealed class FakeProductLookup : IPosProductLookup
{
    public List<(Guid TenantId, PosProductRef Product)> Products { get; } = [];

    public PosProductRef Add(string code, string name, decimal? price, int tax, bool active = true, Guid? id = null, Guid? tenantId = null)
    {
        var product = new PosProductRef(id ?? Guid.CreateVersion7(), code, name, active, price, tax, null);
        Products.Add((tenantId ?? PosFixtures.TenantId, product));
        return product;
    }

    public void Replace(PosProductRef product)
    {
        var index = Products.FindIndex(entry => entry.Product.Id == product.Id);
        Products[index] = (Products[index].TenantId, product);
    }

    // Como el adaptador: sólo activos en la búsqueda.
    public Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = Products
            .Where(entry => entry.TenantId == tenantId && entry.Product.IsActive)
            .Select(entry => entry.Product)
            .Where(product => string.IsNullOrWhiteSpace(search)
                || product.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || product.Code.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<(IReadOnlyList<PosProductRef>, int)>(
            (matches.Skip((page - 1) * pageSize).Take(pageSize).ToList(), matches.Count));
    }

    public Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken) =>
        Task.FromResult(Products
            .Where(entry => entry.TenantId == tenantId && string.Equals(entry.Product.Code, code, StringComparison.Ordinal))
            .Select(entry => entry.Product)
            .SingleOrDefault());

    public Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PosProductRef>>(Products
            .Where(entry => entry.TenantId == tenantId && productIds.Contains(entry.Product.Id))
            .ToDictionary(entry => entry.Product.Id, entry => entry.Product));
}
```

y en `PosTestBed`, junto a las demás propiedades:

```csharp
    public FakeProductLookup Products { get; } = new();

    /// <summary>Los tres productos del ejemplo trabajado, con sus ids de PosFixtures.</summary>
    public void AddWorkedExampleProducts()
    {
        Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: PosFixtures.Shampoo);
        Products.Add("AV-01", "Avena granel (kg)", 5_000m, 0, id: PosFixtures.Avena);
        Products.Add("JB-03", "Jabón", 2_990m, 5, id: PosFixtures.Jabon);
    }
```

`tests/Modules/Pos/Modules.Pos.UnitTests/ProductAndPreviewHandlersTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class ProductAndPreviewHandlersTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];
    private static readonly string[] Discounter = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];

    private static SearchPosProductsHandler Search(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new SearchPosProductsValidator());

    private static FindPosProductByCodeHandler ByCode(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new FindPosProductByCodeValidator());

    private static PreviewPosSaleHandler Preview(PosTestBed bed, params string[] permissions) =>
        new(bed.Products, bed.Context(permissions), new PreviewPosSaleValidator());

    private static PosPreviewLineRequest[] WorkedExampleCart() =>
    [
        new(Shampoo, 2m, 10m),
        new(Avena, 1.5m, 0m),
        new(Jabon, 3m, 0m),
    ];

    [Fact]
    public async Task SearchMarksAProductWithoutAPesoPriceAsUnsellable()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19);
        bed.Products.Add("CR-77", "Crema", null, 19);

        var page = await Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Total);
        var shampoo = page.Items.Single(item => item.Code == "SH-400");
        var cream = page.Items.Single(item => item.Code == "CR-77");
        Assert.True(shampoo.Sellable);
        Assert.Null(shampoo.UnsellableReason);
        Assert.Equal(11_900m, shampoo.UnitPrice);
        Assert.False(cream.Sellable);
        Assert.Equal("PriceMissing", cream.UnsellableReason);
        Assert.Null(cream.UnitPrice);
    }

    [Fact]
    public async Task ByCodeIsExactAndReturnsAnInactiveProductMarked()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19);
        bed.Products.Add("JB-09", "Jabón viejo", 2_000m, 5, active: false);

        var found = await ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "SH-400"), TestContext.Current.CancellationToken);
        var inactive = await ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "JB-09"), TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "sh-400"), TestContext.Current.CancellationToken));

        Assert.True(found.Sellable);
        Assert.False(inactive.Sellable);
        Assert.Equal("Inactive", inactive.UnsellableReason);
        Assert.Equal("pos.product.not_found", missing.Code);
    }

    [Fact]
    public async Task ProductReadsNeedTheSalePermission()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => Search(bed, PosPermissions.SaleRead).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 40), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => ByCode(bed, PosPermissions.SaleRead).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, "SH-400"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAndByCodeValidateTheirInputs()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<ValidationException>(() => Search(bed, Seller).HandleAsync(
            new SearchPosProductsQuery(TenantId, null, 1, 61), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, " "), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => ByCode(bed, Seller).HandleAsync(
            new FindPosProductByCodeQuery(TenantId, new string('X', 61)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreviewComputesTheWorkedExampleOnTheServer()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        var preview = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, WorkedExampleCart()), TestContext.Current.CancellationToken);

        Assert.Equal(34_042.86m, preview.Subtotal);
        Assert.Equal(3_847.14m, preview.TaxAmount);
        Assert.Equal(2_380m, preview.DiscountAmount);
        Assert.Equal(37_890m, preview.Total);
        Assert.False(preview.ZeroTotalNotAllowed);
        var shampoo = preview.Lines[0];
        Assert.Equal("SH-400", shampoo.Code);
        Assert.Equal(11_900m, shampoo.UnitPrice);
        Assert.Equal(19, shampoo.TaxPercentage);
        Assert.Equal((2_380m, 3_420m, 18_000m, 21_420m), (shampoo.DiscountAmount, shampoo.TaxAmount, shampoo.Subtotal, shampoo.LineTotal));
    }

    // Un producto borrado vuelve NotFound y fuera de los totales, no como 422 (spec, decisión 34).
    [Fact]
    public async Task PreviewKeepsUnsellableLinesOutOfTheTotals()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: Shampoo);
        bed.Products.Add("AV-01", "Avena granel (kg)", 5_000m, 0, active: false, id: Avena);
        bed.Products.Add("JB-03", "Jabón", null, 5, id: Jabon);
        var gone = Guid.CreateVersion7();

        var preview = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m), new(Avena, 1m, 0m), new(Jabon, 1m, 0m), new(gone, 1m, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(11_900m, preview.Total);
        Assert.Equal(
            new[] { (true, (string?)null), (false, "Inactive"), (false, "PriceMissing"), (false, "NotFound") },
            preview.Lines.Select(line => (line.Sellable, line.UnsellableReason)).ToArray());
        var notFound = preview.Lines[3];
        Assert.Null(notFound.Code);
        Assert.Null(notFound.Name);
        Assert.Equal(0m, notFound.LineTotal);
    }

    // Review Focus 2.
    [Fact]
    public async Task PreviewWithTheSameProductTwiceReturnsTwoLines()
    {
        var bed = new PosTestBed();
        bed.Products.Add("SH-400", "Shampoo 400 ml", 11_900m, 19, id: Shampoo);

        var preview = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 10m), new(Shampoo, 1m, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(10_710m + 11_900m, preview.Total);
    }

    [Fact]
    public async Task PreviewWithADiscountWithoutThePermissionIs403()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, WorkedExampleCart()), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.discount_not_allowed", error.Code);
    }

    // Un carrito con productos a precio 0 se tiene que poder dibujar; lo que no se puede es cobrarlo.
    [Fact]
    public async Task PreviewFlagsAZeroTotalWithoutTheDiscountPermission()
    {
        var bed = new PosTestBed();
        bed.Products.Add("MU-00", "Muestra gratis", 0m, 0, id: Shampoo);

        var seller = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m)]), TestContext.Current.CancellationToken);
        var discounter = await Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo, 1m, 0m)]), TestContext.Current.CancellationToken);

        Assert.True(seller.ZeroTotalNotAllowed);
        Assert.False(discounter.ZeroTotalNotAllowed);
    }

    // Decisión P5: con sólo líneas no vendibles, "Cobrar" ya está bloqueado por su propio motivo.
    [Fact]
    public async Task PreviewDoesNotFlagZeroWhenNoLineIsSellable()
    {
        var bed = new PosTestBed();

        var preview = await Preview(bed, Seller).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Guid.CreateVersion7(), 1m, 0m)]), TestContext.Current.CancellationToken);

        Assert.False(preview.ZeroTotalNotAllowed);
    }

    [Theory]
    [InlineData("1.005", "0")]
    [InlineData("0", "0")]
    [InlineData("100000", "0")]
    [InlineData("1", "100.5")]
    [InlineData("1", "7.005")]
    public async Task PreviewRejectsQuantityOrDiscountOutOfRangeOrScale(string quantity, string discount)
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();

        await Assert.ThrowsAsync<ValidationException>(() => Preview(bed, Discounter).HandleAsync(
            new PreviewPosSaleCommand(TenantId, [new(Shampoo,
                decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture))]),
            TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0246` para `IPosProductLookup`, `PosProductRef`, `SearchPosProductsHandler`, `PreviewPosSaleHandler`, …).

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Application/IPosProductLookup.cs`:

```csharp
namespace Modules.Pos.Application;

/// <summary>
/// Puerto hacia Catalog (adaptador en Bootstrapper). Precio = lista COP con IVA incluido (regla
/// detal); TaxPercentage 0 sin tasa o con tasa inexistente, como QuotationProductPricingResolver.
/// </summary>
public interface IPosProductLookup
{
    /// <summary>Sólo activos, en el orden de relevancia de ProductRepository.SearchAsync.</summary>
    Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Igualdad exacta con mayúsculas. Activos e inactivos.</summary>
    Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken);

    /// <summary>Activos e inactivos; los ids que no existen en el tenant no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}

public sealed record PosProductRef(
    Guid Id, string Code, string Name, bool IsActive, decimal? PriceCop, int TaxPercentage, string? ImageUrl);
```

`src/Modules/Pos/Modules.Pos.Application/PosProductMapping.cs`:

```csharp
namespace Modules.Pos.Application;

internal static class PosProductMapping
{
    public const string Inactive = "Inactive";
    public const string PriceMissing = "PriceMissing";
    public const string NotFound = "NotFound";

    public static string? UnsellableReason(PosProductRef product) =>
        !product.IsActive ? Inactive
        : product.PriceCop is null ? PriceMissing
        : null;

    public static PosProductResponse ToResponse(PosProductRef product)
    {
        var reason = UnsellableReason(product);
        return new PosProductResponse(
            product.Id, product.Code, product.Name, product.PriceCop, product.TaxPercentage,
            product.ImageUrl, reason is null, reason);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/SearchPosProducts.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record SearchPosProductsQuery(Guid TenantId, string? Search, int Page, int PageSize)
    : IQuery<PosPage<PosProductResponse>>;

public sealed class SearchPosProductsValidator : AbstractValidator<SearchPosProductsQuery>
{
    public SearchPosProductsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 60);
        RuleFor(query => query.Search).MaximumLength(200);
    }
}

public sealed class SearchPosProductsHandler(
    IPosProductLookup products,
    IExecutionContext executionContext,
    IValidator<SearchPosProductsQuery> validator)
    : IQueryHandler<SearchPosProductsQuery, PosPage<PosProductResponse>>
{
    public async Task<PosPage<PosProductResponse>> HandleAsync(
        SearchPosProductsQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var (items, total) = await products.SearchAsync(
            query.TenantId, query.Search?.Trim(), query.Page, query.PageSize, cancellationToken);
        return new PosPage<PosProductResponse>(
            items.Select(PosProductMapping.ToResponse).ToArray(), query.Page, query.PageSize, total);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/FindPosProductByCode.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>El escaneo: el lector teclea Product.Code (no hay campo EAN, spec decisión 25).</summary>
public sealed record FindPosProductByCodeQuery(Guid TenantId, string Code) : IQuery<PosProductResponse>;

public sealed class FindPosProductByCodeValidator : AbstractValidator<FindPosProductByCodeQuery>
{
    public FindPosProductByCodeValidator()
    {
        RuleFor(query => query.Code).NotEmpty().Must(code => !string.IsNullOrWhiteSpace(code)).MaximumLength(60);
    }
}

public sealed class FindPosProductByCodeHandler(
    IPosProductLookup products,
    IExecutionContext executionContext,
    IValidator<FindPosProductByCodeQuery> validator)
    : IQueryHandler<FindPosProductByCodeQuery, PosProductResponse>
{
    public async Task<PosProductResponse> HandleAsync(
        FindPosProductByCodeQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        // 404 es correcto aquí: la búsqueda ya está acotada al tenant de la ruta.
        var product = await products.FindByCodeAsync(query.TenantId, query.Code, cancellationToken)
            ?? throw PosNotFound.ProductCode(query.Code);
        return PosProductMapping.ToResponse(product);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/PreviewPosSale.cs`:

```csharp
using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Sin efectos: los totales que pinta la caja salen de aquí, nunca del frontend.</summary>
public sealed record PreviewPosSaleCommand(Guid TenantId, IReadOnlyList<PosPreviewLineRequest> Lines)
    : ICommand<PosPreviewResponse>;

public sealed class PreviewPosSaleValidator : AbstractValidator<PreviewPosSaleCommand>
{
    public PreviewPosSaleValidator()
    {
        RuleFor(command => command.Lines).NotEmpty().Must(lines => lines.Count <= PosLimits.MaxLines)
            .WithMessage("A sale cannot have more than 200 lines.");
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(value => value.ProductId).NotEmpty();
            line.RuleFor(value => value.Quantity)
                .GreaterThan(0m).LessThanOrEqualTo(PosLimits.MaxQuantity)
                .Must(PosLimits.HasValidScale).WithMessage("The quantity accepts at most 2 decimals.");
            line.RuleFor(value => value.DiscountPercentage)
                .InclusiveBetween(0m, 100m)
                .Must(PosLimits.HasValidScale).WithMessage("The discount accepts at most 2 decimals.");
        });
    }
}

public sealed class PreviewPosSaleHandler(
    IPosProductLookup products,
    IExecutionContext executionContext,
    IValidator<PreviewPosSaleCommand> validator)
    : ICommandHandler<PreviewPosSaleCommand, PosPreviewResponse>
{
    public async Task<PosPreviewResponse> HandleAsync(PreviewPosSaleCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var canDiscount = executionContext.HasPermission(PosPermissions.SaleDiscount);
        if (!canDiscount && command.Lines.Any(line => line.DiscountPercentage > 0))
        {
            throw new RequestForbiddenException(
                "pos.sale.discount_not_allowed", "Line discounts need the pos.sale.discount permission.");
        }

        // Distinct: el mismo producto puede venir en dos líneas (Review Focus 2).
        var found = await products.FindManyAsync(
            command.TenantId, command.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);

        var lines = command.Lines.Select(line => ToLine(line, found)).ToArray();
        var sellable = lines.Where(line => line.Sellable).ToArray();
        var subtotal = VatIncludedLine.Round(sellable.Sum(line => line.Subtotal));
        var tax = VatIncludedLine.Round(sellable.Sum(line => line.TaxAmount));
        var discount = VatIncludedLine.Round(sellable.Sum(line => line.DiscountAmount));
        var total = subtotal + tax;

        return new PosPreviewResponse(
            lines, subtotal, tax, discount, total,
            ZeroTotalNotAllowed: sellable.Length > 0 && total == 0 && !canDiscount);
    }

    private static PosPreviewLineResponse ToLine(
        PosPreviewLineRequest line, IReadOnlyDictionary<Guid, PosProductRef> found)
    {
        if (!found.TryGetValue(line.ProductId, out var product))
        {
            return new PosPreviewLineResponse(
                line.ProductId, null, null, line.Quantity, null, null, line.DiscountPercentage,
                0m, 0m, 0m, 0m, false, PosProductMapping.NotFound);
        }

        var reason = PosProductMapping.UnsellableReason(product);
        if (reason is not null)
        {
            return new PosPreviewLineResponse(
                line.ProductId, product.Code, product.Name, line.Quantity, product.PriceCop, product.TaxPercentage,
                line.DiscountPercentage, 0m, 0m, 0m, 0m, false, reason);
        }

        var amounts = VatIncludedLine.Compute(
            line.Quantity, product.PriceCop!.Value, line.DiscountPercentage, product.TaxPercentage);
        return new PosPreviewLineResponse(
            line.ProductId, product.Code, product.Name, line.Quantity, product.PriceCop, product.TaxPercentage,
            line.DiscountPercentage, amounts.DiscountAmount, amounts.TaxAmount, amounts.Subtotal, amounts.LineTotal,
            true, null);
    }
}
```

`Modules.Pos.Application` ya referencia `BuildingBlocks.Domain` por `Modules.Pos.Domain`; no hace falta otra referencia.

En `QepServiceCollectionExtensions.cs`, después de los handlers de B8:

```csharp
        services.AddScoped<
            IQueryHandler<SearchPosProductsQuery, PosPage<PosProductResponse>>,
            SearchPosProductsHandler>();
        services.AddScoped<
            IQueryHandler<FindPosProductByCodeQuery, PosProductResponse>,
            FindPosProductByCodeHandler>();
        services.AddScoped<
            ICommandHandler<PreviewPosSaleCommand, PosPreviewResponse>,
            PreviewPosSaleHandler>();
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build --filter "FullyQualifiedName~ProductAndPreviewHandlersTests|FullyQualifiedName~RegisterHandlersTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs tests/Modules/Pos/Modules.Pos.UnitTests/ProductAndPreviewHandlersTests.cs; git commit -m "feat(pos): búsqueda de productos y preview de la venta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---


### Task B10: Crear venta idempotente por id de cliente

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/{PosSaleFingerprint,PosSaleResponses,CreatePosSale}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/CreatePosSaleHandlerTests.cs`

**Interfaces:**
- Consumes: B5-B9.
- Produces:
  - `internal static class PosSaleFingerprint { string Compute(CreatePosSaleCommand command); }` — SHA-256 hex minúscula del JSON canónico
  - `internal static class PosSaleResponses { Task<PosSaleResponse> BuildAsync(PosSale sale, ICashSessionRepository sessions, IPosCashierLookup cashiers, ITenantClock tenantClock, CancellationToken); }` (lo reusan B11)
  - `CreatePosSaleCommand(Guid TenantId, Guid Id, Guid CashSessionId, IReadOnlyList<PosSaleLineRequest> Lines, IReadOnlyList<PosPaymentRequest> Payments) : ICommand<PosSaleCreation>`; `public sealed record PosSaleCreation(PosSaleResponse Sale, bool Created)` — `Created = false` en la repetición (el endpoint responde 200 en vez de 201); `CreatePosSaleValidator`, `CreatePosSaleHandler`

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Modules.Pos.Application;

namespace Modules.Pos.UnitTests;

public sealed class PosSaleFingerprintTests
{
    private static readonly Guid Session = Guid.Parse("01920000-0000-7000-8000-0000000000aa");
    private static readonly Guid ProductA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid ProductB = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static CreatePosSaleCommand Command(
        PosSaleLineRequest[]? lines = null, PosPaymentRequest[]? payments = null, Guid? id = null) =>
        new(
            PosFixtures.TenantId,
            id ?? Guid.Parse("6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11"),
            Session,
            lines ?? [new(ProductA, 2m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)],
            payments ?? [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)]);

    [Fact]
    public void TheSameBodyGivesTheSameLowercaseSha256()
    {
        var first = PosSaleFingerprint.Compute(Command());
        var second = PosSaleFingerprint.Compute(Command());

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), first);
    }

    // La escala ya está validada: 2, 2.0 y 2.00 son el mismo carrito.
    [Fact]
    public void TrailingZerosDoNotChangeTheFingerprint()
    {
        var plain = Command([new(ProductA, 2m, 10m, 11_900m, 19)]);
        var padded = Command([new(ProductA, 2.00m, 10.0m, 11_900.00m, 19)]);

        Assert.Equal(PosSaleFingerprint.Compute(plain), PosSaleFingerprint.Compute(padded));
    }

    [Fact]
    public void AnyChangeInTheCartOrThePaymentsChangesTheFingerprint()
    {
        string[] fingerprints =
        [
            PosSaleFingerprint.Compute(Command()),
            PosSaleFingerprint.Compute(Command([new(ProductA, 3m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
            PosSaleFingerprint.Compute(Command([new(ProductB, 2m, 10m, 11_900m, 19), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
            PosSaleFingerprint.Compute(Command([new(ProductB, 1.5m, 0m, 5_000m, 0), new(ProductA, 2m, 10m, 11_900m, 19)])),
            PosSaleFingerprint.Compute(Command(payments: [new("Card", 20_000m, null, "1234"), new("Cash", null, 50_000m, null)])),
            PosSaleFingerprint.Compute(Command(payments: [new("Transfer", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)])),
            PosSaleFingerprint.Compute(Command([new(ProductA, 2m, 10m, 11_900m, 5), new(ProductB, 1.5m, 0m, 5_000m, 0)])),
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ANullReferenceAndAnEmptyOneAreDifferent()
    {
        var withNull = Command(payments: [new("Card", 37_890m, null, null)]);
        var withEmpty = Command(payments: [new("Card", 37_890m, null, "")]);

        Assert.NotEqual(PosSaleFingerprint.Compute(withNull), PosSaleFingerprint.Compute(withEmpty));
    }

    // JSON canónico con cadenas escapadas: una referencia con separadores no imita otro reparto de
    // campos (spec, decisión 43).
    [Fact]
    public void AReferenceWithSeparatorsCannotImitateAnotherSplitOfFields()
    {
        var tricky = Command(payments: [new("Card", 1m, null, "1\",\"Cash"), new("Cash", null, 40_000m, null)]);
        var split = Command(payments: [new("Card", 1m, null, "1"), new("Cash", null, 40_000m, null)]);
        var piped = Command(payments: [new("Card", 1m, null, "1|null|Cash"), new("Cash", null, 40_000m, null)]);

        string[] fingerprints = [PosSaleFingerprint.Compute(tricky), PosSaleFingerprint.Compute(split), PosSaleFingerprint.Compute(piped)];
        Assert.Equal(3, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    // El id no entra en la huella: la huella dice "qué carrito", el id dice "qué intento".
    [Fact]
    public void TheSaleIdIsNotPartOfTheFingerprint()
    {
        Assert.Equal(
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())),
            PosSaleFingerprint.Compute(Command(id: Guid.CreateVersion7())));
    }
}
```

`tests/Modules/Pos/Modules.Pos.UnitTests/CreatePosSaleHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class CreatePosSaleHandlerTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];
    private static readonly string[] Discounter = [PosPermissions.SaleCreate, PosPermissions.SaleDiscount];

    private static (PosTestBed Bed, CashSession Session) Arrange()
    {
        var bed = new PosTestBed();
        bed.AddWorkedExampleProducts();
        return (bed, bed.OpenSessionInStore());
    }

    private static CreatePosSaleCommand Command(
        CashSession session, Guid? id = null, PosSaleLineRequest[]? lines = null, PosPaymentRequest[]? payments = null) =>
        new(
            TenantId,
            id ?? Guid.CreateVersion7(),
            session.Id.Value,
            lines ?? [new(Shampoo, 2m, 10m, 11_900m, 19), new(Avena, 1.5m, 0m, 5_000m, 0), new(Jabon, 3m, 0m, 2_990m, 5)],
            payments ?? [new("Card", 20_000m, null, "1234"), new("Cash", null, 20_000m, null)]);

    private static CreatePosSaleHandler Handler(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Sales, bed.Numbers, bed.UnitOfWork, bed.Audit, bed.Products, bed.Cashiers,
            bed.Memberships, bed.Context(permissions), bed.Clock, bed.TenantClock, new CreatePosSaleValidator());

    // La venta "del otro request" que el choque deja en la base: mismo id, mismo cajero, misma huella.
    private static PosSale WinnerFor(CreatePosSaleCommand command, CashSession session)
    {
        var sale = PosSale.Create(
            new PosSaleId(command.Id), PosSaleFingerprint.Compute(command), session,
            WorkedExampleLines(), [Card(20_000m, "1234"), Cash(20_000m)], Now);
        sale.AssignNumber(1);
        return sale;
    }

    [Fact]
    public async Task CreateRegistersNumbersAndAuditsTheSale()
    {
        var (bed, session) = Arrange();

        var result = await Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Equal("POS-000001", result.Sale.SaleNumber);
        Assert.Equal(37_890m, result.Sale.Total);
        Assert.Equal(2_110m, result.Sale.ChangeAmount);
        Assert.Equal("Laura Gómez", result.Sale.CashierName);
        Assert.Equal("Origen Botánico SAS", result.Sale.Issuer.Name);
        Assert.Equal("Consumidor final", result.Sale.Customer.Name);
        Assert.Equal(3, result.Sale.TaxBreakdown.Count);
        Assert.True(result.Sale.Voidable);
        Assert.Single(bed.Sales.Stored);
        Assert.Equal(1, session.SalesCount);
        Assert.Equal(1, bed.UnitOfWork.Commits);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(("pos.sale.created", "pos_sale"), (audit.Action, audit.ResourceType));
        Assert.Equal(["discount:1:SH-400:10"], audit.ChangedFields.ToArray());
    }

    [Fact]
    public async Task ARepeatWithTheSameBodyReturnsTheSameSaleWithoutSavingOrNumbering()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        var first = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        var repeat = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(repeat.Created);
        Assert.Equal(first.Sale.Id, repeat.Sale.Id);
        Assert.Equal(first.Sale.SaleNumber, repeat.Sale.SaleNumber);
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
        Assert.Equal(1, bed.Numbers.Calls);
        Assert.Equal(1, session.SalesCount);
    }

    [Fact]
    public async Task TheSameIdWithAnotherBodyIsAConflictAndTheOriginalStays()
    {
        var (bed, session) = Arrange();
        var id = Guid.CreateVersion7();
        await Handler(bed, Discounter).HandleAsync(Command(session, id), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, id, payments: [new("Cash", null, 50_000m, null)]), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
        Assert.Equal(37_890m, Assert.Single(bed.Sales.Stored).Total);
    }

    [Fact]
    public async Task TheSameIdFromAnotherCashierIsAConflict()
    {
        var (bed, _) = Arrange();
        var otherSession = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(otherSession);
        var foreignCommand = Command(otherSession);
        bed.Sales.Stored.Add(WinnerFor(foreignCommand, otherSession));
        var mySession = bed.Sessions.Sessions[0];

        var error = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(mySession, foreignCommand.Id), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
    }

    [Fact]
    public async Task ANewPriceOrOnlyANewTaxRateIsPriceChangedAndSpendsNoNumber()
    {
        var (bed, session) = Arrange();
        var shampoo = bed.Products.Products.Single(entry => entry.Product.Id == Shampoo).Product;

        bed.Products.Replace(shampoo with { PriceCop = 12_500m });
        var price = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        bed.Products.Replace(shampoo with { TaxPercentage = 5 });
        var tax = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.price_changed", price.Code);
        Assert.Equal("pos.sale.price_changed", tax.Code);
        Assert.Equal(0, bed.Numbers.Calls);
        Assert.Empty(bed.Sales.Stored);
    }

    [Fact]
    public async Task InactiveUnpricedAndMissingProductsAreRejected()
    {
        var (bed, session) = Arrange();
        var shampoo = bed.Products.Products.Single(entry => entry.Product.Id == Shampoo).Product;

        bed.Products.Replace(shampoo with { IsActive = false });
        var inactive = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        bed.Products.Replace(shampoo with { PriceCop = null });
        var unpriced = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, lines: [new(Guid.CreateVersion7(), 1m, 0m, 1_000m, 0)], payments: [new("Cash", null, 1_000m, null)]),
            TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.product_inactive", inactive.Code);
        Assert.Equal("pos.sale.product_price_unavailable", unpriced.Code);
        Assert.Equal("pos.sale.product_not_found", missing.Code);
        Assert.Equal(0, bed.Numbers.Calls);
    }

    [Fact]
    public async Task ALineDiscountWithoutThePermissionIs403BeforeAnyNumber()
    {
        var (bed, session) = Arrange();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(bed, Seller).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.discount_not_allowed", error.Code);
        Assert.Equal(0, bed.Numbers.Calls);
    }

    // Regalar también es un descuento (spec, «Crear venta», paso 6).
    [Fact]
    public async Task AZeroTotalNeedsTheDiscountPermissionAndIsAudited()
    {
        var (bed, session) = Arrange();
        bed.Products.Add("MU-00", "Muestra gratis", 0m, 0, id: Guid.Parse("01900000-0000-7000-8000-0000000000b0"));
        PosSaleLineRequest[] free = [new(Guid.Parse("01900000-0000-7000-8000-0000000000b0"), 1m, 0m, 0m, 0)];
        PosPaymentRequest[] zero = [new("Cash", null, 0m, null)];

        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(bed, Seller).HandleAsync(Command(session, lines: free, payments: zero), TestContext.Current.CancellationToken));
        var created = await Handler(bed, Discounter).HandleAsync(
            Command(session, lines: free, payments: zero), TestContext.Current.CancellationToken);

        Assert.Equal("pos.sale.discount_not_allowed", denied.Code);
        Assert.Equal(0m, created.Sale.Total);
        Assert.Equal(["total:0"], Assert.Single(bed.Audit.Entries).ChangedFields.ToArray());
    }

    [Fact]
    public async Task WithoutAnOpenSessionOrWithAnotherSessionIdTheSaleIsRejected()
    {
        var (bed, session) = Arrange();

        var mismatch = await Assert.ThrowsAsync<PosDomainException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session) with { CashSessionId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));
        session.Close(100_000m, null, Now);
        var notOpen = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.session_mismatch", mismatch.Code);
        Assert.Equal("pos.session.not_open", notOpen.Code);
    }

    // Spec, «Crear venta», paso 8, primer orden: lo normal es que el duplicado choque en la Version
    // de la caja (412), no en PK_sales.
    [Fact]
    public async Task AConcurrencyClashWhereTheSaleAppearsReturnsItAs200()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        bed.UnitOfWork.OnSave.Enqueue(() =>
        {
            bed.Sales.Stored.Add(WinnerFor(command, session));
            return new RequestConcurrencyException("concurrency.conflict", "clash");
        });

        var result = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal(command.Id, result.Sale.Id);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
        Assert.Single(bed.Sales.Stored);
    }

    [Fact]
    public async Task AnIdTakenClashWhereTheSaleAppearsReturnsItAs200()
    {
        var (bed, session) = Arrange();
        var command = Command(session);
        bed.UnitOfWork.OnSave.Enqueue(() =>
        {
            bed.Sales.Stored.Add(WinnerFor(command, session));
            return new PosDomainException("pos.sale.id_taken", "taken");
        });

        var result = await Handler(bed, Discounter).HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
    }

    // Fue un cierre de caja u otra venta del mismo cajero en otra pestaña, no un duplicado.
    [Fact]
    public async Task AConcurrencyClashWithoutTheSaleIsRethrownAs412()
    {
        var (bed, session) = Arrange();
        bed.UnitOfWork.OnSave.Enqueue(() => new RequestConcurrencyException("concurrency.conflict", "clash"));

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(1, bed.UnitOfWork.ResetCalls);
    }

    // El id es la PK de una venta de otro tenant: terminal, el cliente genera otro id.
    [Fact]
    public async Task AnIdTakenClashWithoutTheSaleInTheTenantIsAConflict()
    {
        var (bed, session) = Arrange();
        bed.UnitOfWork.OnSave.Enqueue(() => new PosDomainException("pos.sale.id_taken", "taken"));

        var error = await Assert.ThrowsAsync<PosDomainException>(() =>
            Handler(bed, Discounter).HandleAsync(Command(session), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.id_conflict", error.Code);
    }

    // Review Focus 2.
    [Fact]
    public async Task CreateWithTheSameProductOnTwoLinesKeepsBothLines()
    {
        var (bed, session) = Arrange();

        var result = await Handler(bed, Discounter).HandleAsync(
            Command(session,
                lines: [new(Shampoo, 1m, 10m, 11_900m, 19), new(Shampoo, 1m, 0m, 11_900m, 19)],
                payments: [new("Cash", null, 30_000m, null)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Sale.Lines.Count);
        Assert.Equal(22_610m, result.Sale.Total);
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task TheValidatorRejectsFieldsOutOfShape(PosSaleLineRequest[] lines, PosPaymentRequest[] payments)
    {
        var (bed, session) = Arrange();

        await Assert.ThrowsAsync<ValidationException>(() => Handler(bed, Discounter).HandleAsync(
            Command(session, lines: lines, payments: payments), TestContext.Current.CancellationToken));

        Assert.Equal(0, bed.Numbers.Calls);
    }

    public static TheoryData<PosSaleLineRequest[], PosPaymentRequest[]> InvalidBodies => new()
    {
        { [new(Shampoo, 1.005m, 0m, 11_900m, 19)], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 7.005m, 11_900m, 19)], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", 11_900m, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", null, 20_000m, "ref")] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Cash", null, 20_000.001m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Card", 11_900m, null, new string('9', 61))] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [new("Card", null, null, null)] },
        { [], [new("Cash", null, 20_000m, null)] },
        { [new(Shampoo, 1m, 0m, 11_900m, 19)], [] },
    };
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0246` para `CreatePosSaleCommand`, `PosSaleFingerprint`, `CreatePosSaleHandler`, `CreatePosSaleValidator`).

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Application/PosSaleFingerprint.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modules.Pos.Application;

/// <summary>
/// Huella del request (spec, «Crear venta — idempotencia»): SHA-256 en hex minúscula de un JSON
/// canónico con posiciones fijas, [cashSessionId, [[productId, quantity, discountPercentage,
/// expectedUnitPrice, expectedTaxPercentage], …], [[method, amount, tendered, reference], …]]. Los
/// decimales van como texto 0.## (la escala ya está validada: 2, 2.0 y 2.00 son lo mismo) y lo
/// ausente como null de JSON, así null no se confunde con "" y un separador dentro de una cadena no
/// imita otro reparto de campos. Se eligió la huella y no "total + líneas" porque dos carritos
/// distintos pueden dar el mismo total.
/// </summary>
internal static class PosSaleFingerprint
{
    public static string Compute(CreatePosSaleCommand command)
    {
        object?[] canonical =
        [
            command.CashSessionId.ToString("D"),
            command.Lines
                .Select(line => new object?[]
                {
                    line.ProductId.ToString("D"),
                    Format(line.Quantity),
                    Format(line.DiscountPercentage),
                    Format(line.ExpectedUnitPrice),
                    line.ExpectedTaxPercentage.ToString(CultureInfo.InvariantCulture),
                })
                .ToArray(),
            command.Payments
                .Select(payment => new object?[]
                {
                    payment.Method,
                    Format(payment.Amount),
                    Format(payment.Tendered),
                    payment.Reference,
                })
                .ToArray(),
        ];

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string? Format(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture);
}
```

`src/Modules/Pos/Modules.Pos.Application/PosSaleResponses.cs`:

```csharp
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Arma PosSaleResponse —la venta y los datos del ticket— para crear, leer y anular.</summary>
internal static class PosSaleResponses
{
    public static async Task<PosSaleResponse> BuildAsync(
        PosSale sale,
        ICashSessionRepository sessions,
        IPosCashierLookup cashiers,
        ITenantClock tenantClock,
        CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(sale.TenantId, sale.CashSessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale '{sale.Id}' points to a missing cash session.");
        var calendar = await tenantClock.GetAsync(sale.TenantId, cancellationToken);

        PosVoidResponse? voidInfo = null;
        if (sale.Status == PosSaleStatus.Voided)
        {
            var voidedBy = sale.VoidedBy is { } member
                ? await cashiers.FindNameAsync(sale.TenantId, member.Value, cancellationToken)
                : null;
            voidInfo = new PosVoidResponse(sale.VoidReason!, calendar.ToLocal(sale.VoidedAt!.Value), voidedBy ?? string.Empty);
        }

        var (voidable, blockedReason) = PosVoidability.For(sale.Status, session.Status);

        return new PosSaleResponse(
            sale.Id.Value,
            sale.SaleNumber,
            sale.Status.ToString(),
            sale.CreatedAt,
            calendar.ToLocal(sale.CreatedAt),
            sale.CashSessionId.Value,
            session.CashierName,
            new PosIssuerResponse(session.CompanyName, session.CompanyTaxId, session.CompanyAddress, session.CompanyPhone),
            new PosCustomerResponse(sale.CustomerName, sale.CustomerIdentificationType, sale.CustomerIdentificationNumber),
            sale.Lines
                .OrderBy(line => line.Position)
                .Select(line => new PosSaleLineResponse(
                    line.Position, line.ProductId, line.ProductCode, line.ProductName, line.Quantity, line.UnitPrice,
                    line.DiscountPercentage, line.TaxPercentage, line.DiscountAmount, line.TaxAmount, line.Subtotal,
                    line.LineTotal))
                .ToArray(),
            sale.Subtotal,
            sale.TaxAmount,
            sale.DiscountAmount,
            sale.Total,
            sale.TaxBreakdown()
                .Select(entry => new PosTaxBreakdownResponse(entry.TaxPercentage, entry.Base, entry.TaxAmount))
                .ToArray(),
            sale.Payments
                .OrderBy(payment => payment.Position)
                .Select(payment => new PosPaymentResponse(
                    payment.Method.ToString(), payment.Amount, payment.Tendered, payment.Reference))
                .ToArray(),
            sale.ChangeAmount,
            voidInfo,
            voidable,
            blockedReason);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs`:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="Id">Lo genera el cliente al abrir el cobro: es la clave de idempotencia.</param>
public sealed record CreatePosSaleCommand(
    Guid TenantId,
    Guid Id,
    Guid CashSessionId,
    IReadOnlyList<PosSaleLineRequest> Lines,
    IReadOnlyList<PosPaymentRequest> Payments) : ICommand<PosSaleCreation>;

/// <param name="Created">false en una repetición reconocida: el endpoint responde 200 en vez de 201.</param>
public sealed record PosSaleCreation(PosSaleResponse Sale, bool Created);

public sealed class CreatePosSaleValidator : AbstractValidator<CreatePosSaleCommand>
{
    private static readonly string[] Methods =
        [nameof(PosPaymentMethod.Cash), nameof(PosPaymentMethod.Card), nameof(PosPaymentMethod.Transfer)];

    public CreatePosSaleValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.CashSessionId).NotEmpty();
        RuleFor(command => command.Lines).NotEmpty()
            .Must(lines => lines.Count <= PosLimits.MaxLines).WithMessage("A sale cannot have more than 200 lines.");
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(value => value.ProductId).NotEmpty();
            line.RuleFor(value => value.Quantity)
                .GreaterThan(0m).LessThanOrEqualTo(PosLimits.MaxQuantity)
                .Must(PosLimits.HasValidScale).WithMessage("The quantity accepts at most 2 decimals.");
            line.RuleFor(value => value.DiscountPercentage)
                .InclusiveBetween(0m, 100m)
                .Must(PosLimits.HasValidScale).WithMessage("The discount accepts at most 2 decimals.");
            line.RuleFor(value => value.ExpectedUnitPrice)
                .GreaterThanOrEqualTo(0m)
                .Must(PosLimits.HasValidScale).WithMessage("The price accepts at most 2 decimals.");
            line.RuleFor(value => value.ExpectedTaxPercentage).InclusiveBetween(0, 100);
        });
        RuleFor(command => command.Payments).NotEmpty()
            .Must(payments => payments.Count <= PosLimits.MaxPayments).WithMessage("A sale cannot have more than 5 payments.");
        RuleForEach(command => command.Payments).ChildRules(payment =>
        {
            payment.RuleFor(value => value.Method)
                .Must(method => Methods.Contains(method, StringComparer.Ordinal))
                .WithMessage("The method must be Cash, Card or Transfer.");
            payment.When(value => value.Method == nameof(PosPaymentMethod.Cash), () =>
            {
                // En Cash el Amount lo calcula el servidor (spec, decisión 41).
                payment.RuleFor(value => value.Amount).Null().WithMessage("A cash payment carries only tendered.");
                payment.RuleFor(value => value.Tendered).NotNull()
                    .InclusiveBetween(0m, PosLimits.MaxCashAmount)
                    .Must(tendered => tendered is null || PosLimits.HasValidScale(tendered.Value))
                    .WithMessage("The tendered cash accepts at most 2 decimals.");
                // Decisión P11.
                payment.RuleFor(value => value.Reference).Null().WithMessage("A cash payment has no reference.");
            }).Otherwise(() =>
            {
                payment.RuleFor(value => value.Amount).NotNull().GreaterThan(0m)
                    .Must(amount => amount is null || PosLimits.HasValidScale(amount.Value))
                    .WithMessage("The amount accepts at most 2 decimals.");
                payment.RuleFor(value => value.Tendered).Null().WithMessage("Only cash carries tendered.");
                payment.RuleFor(value => value.Reference).MaximumLength(PosLimits.ReferenceMaxLength);
            });
        });
    }
}

public sealed class CreatePosSaleHandler(
    ICashSessionRepository sessions,
    IPosSaleRepository sales,
    IPosSaleNumberGenerator numbers,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IPosProductLookup products,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<CreatePosSaleCommand> validator)
    : ICommandHandler<CreatePosSaleCommand, PosSaleCreation>
{
    public async Task<PosSaleCreation> HandleAsync(CreatePosSaleCommand command, CancellationToken cancellationToken)
    {
        // 1. Lo que se responde aquí (authorization.denied, validation.failed) sale antes de buscar
        // la repetición: el cliente no lo toma como definitivo al reintentar un cobro incierto.
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var fingerprint = PosSaleFingerprint.Compute(command);
        var saleId = new PosSaleId(command.Id);

        // 2. Repetición: antes de mirar la caja, para que reintentar sea idempotente aunque la caja
        // ya no esté abierta.
        if (await FindReplayAsync(command.TenantId, saleId, cashier, fingerprint, cancellationToken) is { } replay)
        {
            return new PosSaleCreation(await RespondAsync(replay, cancellationToken), Created: false);
        }

        // 3. Caja.
        var session = await sessions.FindOpenByCashierAsync(command.TenantId, cashier, cancellationToken)
            ?? throw new PosDomainException("pos.session.not_open", "The cashier has no open cash session.");
        if (session.Id.Value != command.CashSessionId)
        {
            throw new PosDomainException(
                "pos.sale.session_mismatch", "The cash session sent is not the cashier's open one.");
        }

        // 4. Productos: el precio y la tasa que se cobran son siempre los del catálogo.
        var lines = await ResolveLinesAsync(command, cancellationToken);

        // 5. Descuento.
        var canDiscount = executionContext.HasPermission(PosPermissions.SaleDiscount);
        if (!canDiscount && command.Lines.Any(line => line.DiscountPercentage > 0))
        {
            throw DiscountNotAllowed();
        }

        // 6. Venta (valida líneas y pagos). Un total en cero sin descuento sólo sale de productos
        // con precio 0, y regalar también es un descuento.
        var now = clock.UtcNow;
        var sale = PosSale.Create(
            saleId, fingerprint, session, lines, command.Payments.Select(ToInput).ToArray(), now);
        if (sale.Total == 0 && !canDiscount)
        {
            throw DiscountNotAllowed();
        }

        // 7. Número adentro de la transacción y después de todas las validaciones: un 422 o un 412
        // no gastan número.
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            sale.AssignNumber(await numbers.NextAsync(command.TenantId, cancellationToken));
            sales.Add(sale);
            session.RegisterSale(sale, now);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "pos.sale.created", "pos_sale",
                sale.Id.ToString(), "success", AuditFields(sale), now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // 8. Choque al guardar: un duplicado en paralelo choca casi siempre en la Version de la caja
        // (412) y a veces en PK_sales. Los dos se tratan igual: limpiar, releer una vez.
        catch (Exception exception) when (exception is RequestConcurrencyException
            || exception is PosDomainException { Code: "pos.sale.id_taken" })
        {
            await unitOfWork.ResetAsync(cancellationToken);
            if (await FindReplayAsync(command.TenantId, saleId, cashier, fingerprint, cancellationToken) is { } winner)
            {
                return new PosSaleCreation(await RespondAsync(winner, cancellationToken), Created: false);
            }

            if (exception is PosDomainException)
            {
                // La PK es de una venta de otro tenant: terminal, el cliente genera otro id.
                throw new PosDomainException("pos.sale.id_conflict", "The sale id is already in use.");
            }

            throw;
        }

        return new PosSaleCreation(await RespondAsync(sale, cancellationToken), Created: true);
    }

    private async Task<PosSale?> FindReplayAsync(
        Guid tenantId, PosSaleId saleId, MemberId cashier, string fingerprint, CancellationToken cancellationToken)
    {
        var existing = await sales.FindAsync(tenantId, saleId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        // Otro cajero u otro carrito con el mismo id: la venta vieja nunca se presenta como la nueva.
        return existing.CashierId == cashier && string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? existing
            : throw new PosDomainException("pos.sale.id_conflict", "The sale id belongs to another sale.");
    }

    private async Task<PosSaleLineInput[]> ResolveLinesAsync(CreatePosSaleCommand command, CancellationToken cancellationToken)
    {
        var found = await products.FindManyAsync(
            command.TenantId, command.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);

        return command.Lines.Select(line =>
        {
            if (!found.TryGetValue(line.ProductId, out var product))
            {
                throw new PosDomainException("pos.sale.product_not_found", $"Product '{line.ProductId}' was not found.");
            }

            if (!product.IsActive)
            {
                throw new PosDomainException("pos.sale.product_inactive", $"Product '{product.Code}' is inactive.");
            }

            if (product.PriceCop is not { } price)
            {
                throw new PosDomainException(
                    "pos.sale.product_price_unavailable", $"Product '{product.Code}' has no peso price.");
            }

            // Basta con que cambie uno: un cambio sólo de tasa no mueve el total (IVA incluido) pero
            // sí el desglose del ticket.
            if (price != line.ExpectedUnitPrice || product.TaxPercentage != line.ExpectedTaxPercentage)
            {
                throw new PosDomainException(
                    "pos.sale.price_changed", $"The price or tax rate of '{product.Code}' changed.");
            }

            return new PosSaleLineInput(
                product.Id, product.Code, product.Name, line.Quantity, price, line.DiscountPercentage, product.TaxPercentage);
        }).ToArray();
    }

    private Task<PosSaleResponse> RespondAsync(PosSale sale, CancellationToken cancellationToken) =>
        PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);

    private static PosPaymentInput ToInput(PosPaymentRequest payment) =>
        new(Enum.Parse<PosPaymentMethod>(payment.Method), payment.Amount, payment.Tendered, payment.Reference);

    private static RequestForbiddenException DiscountNotAllowed() =>
        new("pos.sale.discount_not_allowed", "Discounts and zero-total sales need the pos.sale.discount permission.");

    // changedFields es el único campo libre del contrato de auditoría (spec, «Descuentos en la
    // auditoría»): discount:{position}:{productCode}:{discountPercentage} y total:0.
    private static string[] AuditFields(PosSale sale)
    {
        var fields = sale.Lines
            .Where(line => line.DiscountPercentage > 0)
            .Select(line => string.Create(
                CultureInfo.InvariantCulture,
                $"discount:{line.Position}:{line.ProductCode}:{line.DiscountPercentage:0.##}"))
            .ToList();
        if (sale.Total == 0)
        {
            fields.Add("total:0");
        }

        return [.. fields];
    }
}
```

En `QepServiceCollectionExtensions.cs`, después de los handlers de B9:

```csharp
        services.AddScoped<
            ICommandHandler<CreatePosSaleCommand, PosSaleCreation>,
            CreatePosSaleHandler>();
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build --filter "FullyQualifiedName~PosSaleFingerprintTests|FullyQualifiedName~CreatePosSaleHandlerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos/Modules.Pos.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs tests/Modules/Pos/Modules.Pos.UnitTests/CreatePosSaleHandlerTests.cs; git commit -m "feat(pos): crear venta idempotente por id de cliente"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---

### Task B11: Lectura, listado y anulación de ventas y cajas

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/{PosListFilters,PosScope,GetPosSale,ListPosSales,VoidPosSale,ListCashSessions,GetCashSession}.cs`
- Modify: `ICashSessionRepository.cs`, `IPosSaleRepository.cs`, `Persistence/CashSessionRepository.cs`, `Persistence/PosSaleRepository.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/SaleReadAndVoidHandlersTests.cs`

**Interfaces:**
- Consumes: B6-B10.
- Produces:
  - `public sealed record CashSessionFilter(Guid TenantId, MemberId? Cashier, DateTimeOffset? OpenedFromUtc, DateTimeOffset? OpenedToUtc, CashSessionStatus? Status, int Page, int PageSize)`
  - `public sealed record PosSaleFilter(Guid TenantId, MemberId? Cashier, CashSessionId? SessionId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, PosSaleStatus? Status, string? Number, int Page, int PageSize)`
  - `public sealed record PosSaleListRow(PosSale Sale, string CashierName, CashSessionStatus SessionStatus)`
  - `ICashSessionRepository.ListAsync(CashSessionFilter, CancellationToken) : Task<(IReadOnlyList<CashSession> Items, int Total)>`; `IPosSaleRepository.ListAsync(PosSaleFilter, CancellationToken) : Task<(IReadOnlyList<PosSaleListRow> Items, int Total)>`
  - `GetPosSaleQuery(Guid TenantId, Guid SaleId) : IQuery<PosSaleResponse>`; `ListPosSalesQuery(Guid TenantId, Guid? SessionId, DateOnly? From, DateOnly? To, string? Status, string? Number, int Page, int PageSize) : IQuery<PosPage<PosSaleListItemResponse>>`; `VoidPosSaleCommand(Guid TenantId, Guid SaleId, string? Reason) : ICommand<PosSaleResponse>`; `ListCashSessionsQuery(Guid TenantId, DateOnly? From, DateOnly? To, string? Status, int Page, int PageSize) : IQuery<PosPage<PosSessionSummaryResponse>>`; `GetCashSessionQuery(Guid TenantId, Guid SessionId) : IQuery<PosSessionSummaryResponse>`

- [ ] **Step 1: Escribir los dobles de listado y las pruebas que fallan**

En `PosTestDoubles.cs`, agrega a `InMemoryCashSessionRepository`:

```csharp
    public Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken)
    {
        var matches = Sessions
            .Where(session => session.TenantId == filter.TenantId)
            .Where(session => filter.Cashier is not { } cashier || session.CashierId == cashier)
            .Where(session => filter.OpenedFromUtc is not { } from || session.OpenedAt >= from)
            .Where(session => filter.OpenedToUtc is not { } to || session.OpenedAt < to)
            .Where(session => filter.Status is not { } status || session.Status == status)
            .OrderByDescending(session => session.OpenedAt)
            .ToList();
        return Task.FromResult<(IReadOnlyList<CashSession>, int)>(
            (matches.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList(), matches.Count));
    }
```

y a `InMemoryPosSaleRepository`:

```csharp
    public Task<(IReadOnlyList<PosSaleListRow> Items, int Total)> ListAsync(
        PosSaleFilter filter, CancellationToken cancellationToken)
    {
        var matches = Stored
            .Where(sale => sale.TenantId == filter.TenantId)
            .Where(sale => filter.Cashier is not { } cashier || sale.CashierId == cashier)
            .Where(sale => filter.SessionId is not { } sessionId || sale.CashSessionId == sessionId)
            .Where(sale => filter.FromUtc is not { } from || sale.CreatedAt >= from)
            .Where(sale => filter.ToUtc is not { } to || sale.CreatedAt < to)
            .Where(sale => filter.Status is not { } status || sale.Status == status)
            .Where(sale => filter.Number is null || sale.SaleNumber == filter.Number)
            .OrderByDescending(sale => sale.CreatedAt)
            .ToList();
        var rows = matches
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(sale =>
            {
                var session = Sessions.Sessions.Single(item => item.Id == sale.CashSessionId);
                return new PosSaleListRow(sale, session.CashierName, session.Status);
            })
            .ToList();
        return Task.FromResult<(IReadOnlyList<PosSaleListRow>, int)>((rows, matches.Count));
    }
```

`tests/Modules/Pos/Modules.Pos.UnitTests/SaleReadAndVoidHandlersTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

public sealed class SaleReadAndVoidHandlersTests
{
    private static readonly string[] Reader = [PosPermissions.SaleRead];
    private static readonly string[] Supervisor = [PosPermissions.SaleRead, PosPermissions.RegisterRead];
    private static readonly string[] Voider = [PosPermissions.SaleVoid];

    private static PosSale StoreSale(PosTestBed bed, CashSession session, DateTimeOffset? at = null)
    {
        var when = at ?? Now;
        var sale = PosSale.Create(
            PosSaleId.New(), Fingerprint, session, WorkedExampleLines(), [Card(20_000m, "1234"), Cash(20_000m)], when);
        sale.AssignNumber(bed.Sales.Stored.Count + 1);
        session.RegisterSale(sale, when);
        bed.Sales.Stored.Add(sale);
        return sale;
    }

    private static CashSession ForeignSession(PosTestBed bed)
    {
        var session = OpenSession(cashier: new MemberId(Guid.CreateVersion7()));
        bed.Sessions.Add(session);
        return session;
    }

    private static GetPosSaleHandler Get(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Sessions, bed.Cashiers, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    private static ListPosSalesHandler List(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Memberships, bed.Context(permissions), bed.TenantClock, new ListPosSalesValidator());

    private static VoidPosSaleHandler Void(PosTestBed bed, params string[] permissions) =>
        new(bed.Sales, bed.Sessions, bed.UnitOfWork, bed.Audit, bed.Cashiers, bed.Memberships,
            bed.Context(permissions), bed.Clock, bed.TenantClock, new VoidPosSaleValidator());

    private static ListCashSessionsHandler Sessions(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Memberships, bed.Context(permissions), bed.TenantClock, new ListCashSessionsValidator());

    private static GetCashSessionHandler Session(PosTestBed bed, params string[] permissions) =>
        new(bed.Sessions, bed.Memberships, bed.Context(permissions), bed.TenantClock);

    [Fact]
    public async Task TheCashierReadsTheirOwnSaleButNotSomeoneElses()
    {
        var bed = new PosTestBed();
        var own = StoreSale(bed, bed.OpenSessionInStore());
        var foreign = StoreSale(bed, ForeignSession(bed));

        var read = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, own.Id.Value), TestContext.Current.CancellationToken);
        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken));
        var supervisor = await Get(bed, Supervisor).HandleAsync(
            new GetPosSaleQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(own.SaleNumber, read.SaleNumber);
        Assert.Equal("authorization.denied", denied.Code);
        Assert.Equal(foreign.Id.Value, supervisor.Id);
    }

    [Fact]
    public async Task AnUnknownSaleIsNotFoundInsideTheTenant()
    {
        var bed = new PosTestBed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.not_found", error.Code);
    }

    // voidable describe la venta, no al que pregunta (spec, decisión 33).
    [Fact]
    public async Task VoidableFollowsTheSaleAndItsSessionEvenWithoutTheVoidPermission()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);

        var open = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, sale.Id.Value), TestContext.Current.CancellationToken);
        session.Close(117_890m, null, Now);
        var closed = await Get(bed, Reader).HandleAsync(new GetPosSaleQuery(TenantId, sale.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal((true, (string?)null), (open.Voidable, open.VoidBlockedReason));
        Assert.Equal((false, (string?)"SessionClosed"), (closed.Voidable, closed.VoidBlockedReason));
    }

    [Fact]
    public async Task VoidSubtractsFromTheCountAuditsAndShowsWhoVoided()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var sale = StoreSale(bed, session);

        var voided = await Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, sale.Id.Value, "Cliente se arrepintió"), TestContext.Current.CancellationToken);

        Assert.Equal("Voided", voided.Status);
        Assert.Equal("Cliente se arrepintió", voided.Void!.Reason);
        Assert.Equal("Laura Gómez", voided.Void.VoidedByName);
        Assert.Equal((false, (string?)"AlreadyVoided"), (voided.Voidable, voided.VoidBlockedReason));
        Assert.Equal(0m, session.SalesTotal);
        Assert.Equal(1, session.VoidedCount);
        Assert.Equal(("pos.sale.voided", "pos_sale"), (Assert.Single(bed.Audit.Entries).Action, bed.Audit.Entries[0].ResourceType));
        Assert.Equal(1, bed.UnitOfWork.SaveCalls);
    }

    [Fact]
    public async Task VoidIsRejectedTwiceOnAClosedSessionWithoutPermissionAndForAnUnknownSale()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var twice = StoreSale(bed, session);
        await Void(bed, Voider).HandleAsync(new VoidPosSaleCommand(TenantId, twice.Id.Value, "Motivo"), TestContext.Current.CancellationToken);
        var late = StoreSale(bed, session);
        session.Close(117_890m, null, Now);

        var again = await Assert.ThrowsAsync<PosDomainException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, twice.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var closed = await Assert.ThrowsAsync<PosDomainException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var noPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() => Void(bed, Reader).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, "Motivo"), TestContext.Current.CancellationToken));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, Guid.CreateVersion7(), "Motivo"), TestContext.Current.CancellationToken));
        var shortReason = await Assert.ThrowsAsync<ValidationException>(() => Void(bed, Voider).HandleAsync(
            new VoidPosSaleCommand(TenantId, late.Id.Value, " ab "), TestContext.Current.CancellationToken));

        Assert.Equal("pos.sale.already_voided", again.Code);
        Assert.Equal("pos.sale.void_session_closed", closed.Code);
        Assert.Equal("authorization.denied", noPermission.Code);
        Assert.Equal("pos.sale.not_found", unknown.Code);
        Assert.NotNull(shortReason);
    }

    [Fact]
    public async Task ListWithoutRegisterReadOnlyShowsTheCallersSalesEvenForAnotherSession()
    {
        var bed = new PosTestBed();
        var own = StoreSale(bed, bed.OpenSessionInStore());
        var foreignSession = ForeignSession(bed);
        StoreSale(bed, foreignSession);

        var mine = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var sneaky = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, foreignSession.Id.Value, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var all = await List(bed, Supervisor).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([own.Id.Value], mine.Items.Select(item => item.Id).ToArray());
        Assert.Empty(sneaky.Items);
        Assert.Equal(2, all.Total);
        var item = mine.Items[0];
        Assert.Equal(new[] { "Card", "Cash" }, item.PaymentMethods);
        Assert.Equal("Laura Gómez", item.CashierName);
        Assert.True(item.Voidable);
    }

    // Review Focus 3: 04:30 UTC del 8 son las 23:30 del 7 en Bogotá.
    [Fact]
    public async Task ListSalesCutsTheDayInTheTenantTimeZone()
    {
        var bed = new PosTestBed();
        bed.TenantClock.UtcNow = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var session = bed.OpenSessionInStore();
        var lateOnThe7th = StoreSale(bed, session, new DateTimeOffset(2026, 10, 8, 4, 30, 0, TimeSpan.Zero));
        StoreSale(bed, session, new DateTimeOffset(2026, 10, 8, 5, 30, 0, TimeSpan.Zero));
        var day = new DateOnly(2026, 10, 7);

        var page = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, day, day, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([lateOnThe7th.Id.Value], page.Items.Select(item => item.Id).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.FromHours(-5)), page.Items[0].CreatedAtLocal);
    }

    [Fact]
    public async Task ListFiltersByStatusAndExactNumberAndValidatesItsInputs()
    {
        var bed = new PosTestBed();
        var session = bed.OpenSessionInStore();
        var first = StoreSale(bed, session);
        StoreSale(bed, session);
        await Void(bed, Voider).HandleAsync(new VoidPosSaleCommand(TenantId, first.Id.Value, "Motivo"), TestContext.Current.CancellationToken);

        var voided = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, "Voided", null, 1, 20), TestContext.Current.CancellationToken);
        var byNumber = await List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, "POS-000002", 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal([first.Id.Value], voided.Items.Select(item => item.Id).ToArray());
        Assert.Equal("POS-000002", Assert.Single(byNumber.Items).SaleNumber);
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, "Anulada", null, 1, 20), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, null, null, null, null, 1, 101), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ValidationException>(() => List(bed, Reader).HandleAsync(
            new ListPosSalesQuery(TenantId, null, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 7), null, null, 1, 20), TestContext.Current.CancellationToken));
    }

    // Decisión 32: el cajero lee sus propias cajas, abiertas y cerradas, para reimprimir un cierre.
    [Fact]
    public async Task TheCashierReadsTheirOwnClosedSessionButNotSomeoneElses()
    {
        var bed = new PosTestBed();
        var own = bed.OpenSessionInStore();
        own.Close(100_000m, null, Now);
        var foreign = ForeignSession(bed);

        var summary = await Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, own.Id.Value), TestContext.Current.CancellationToken);
        var denied = await Assert.ThrowsAsync<RequestForbiddenException>(() => Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, foreign.Id.Value), TestContext.Current.CancellationToken));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Session(bed, Reader).HandleAsync(
            new GetCashSessionQuery(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));
        var mine = await Sessions(bed, Reader).HandleAsync(
            new ListCashSessionsQuery(TenantId, null, null, null, 1, 20), TestContext.Current.CancellationToken);
        var everyone = await Sessions(bed, Supervisor).HandleAsync(
            new ListCashSessionsQuery(TenantId, null, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal("Closed", summary.Status);
        Assert.Equal(100_000m, summary.ExpectedCash);
        Assert.Equal("authorization.denied", denied.Code);
        Assert.Equal("pos.session.not_found", unknown.Code);
        Assert.Equal([own.Id.Value], mine.Items.Select(item => item.Id).ToArray());
        Assert.Equal(2, everyone.Total);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Pos/Modules.Pos.UnitTests
```

Esperado: FAIL de compilación (`CS0246` para `CashSessionFilter`, `PosSaleFilter`, `PosSaleListRow`, `GetPosSaleHandler`, `ListPosSalesHandler`, `VoidPosSaleHandler`, `ListCashSessionsHandler`, `GetCashSessionHandler`).

- [ ] **Step 3: Implementar**

`src/Modules/Pos/Modules.Pos.Application/PosListFilters.cs`:

```csharp
using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <param name="Cashier">null = todas (pos.register.read); si no, sólo las del cajero llamador.</param>
/// <param name="OpenedFromUtc">Inicio del día local del tenant, ya en UTC.</param>
/// <param name="OpenedToUtc">Fin exclusivo del día local del tenant, ya en UTC.</param>
public sealed record CashSessionFilter(
    Guid TenantId,
    MemberId? Cashier,
    DateTimeOffset? OpenedFromUtc,
    DateTimeOffset? OpenedToUtc,
    CashSessionStatus? Status,
    int Page,
    int PageSize);

public sealed record PosSaleFilter(
    Guid TenantId,
    MemberId? Cashier,
    CashSessionId? SessionId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    PosSaleStatus? Status,
    string? Number,
    int Page,
    int PageSize);

/// <summary>Venta con lo que la lista necesita de su caja, resuelto en lote (no una consulta por fila).</summary>
public sealed record PosSaleListRow(PosSale Sale, string CashierName, CashSessionStatus SessionStatus);
```

En `ICashSessionRepository.cs`:

```csharp
    /// <summary>Por apertura descendente.</summary>
    Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken);
```

En `IPosSaleRepository.cs`:

```csharp
    /// <summary>Por creación descendente; sin líneas, con pagos.</summary>
    Task<(IReadOnlyList<PosSaleListRow> Items, int Total)> ListAsync(
        PosSaleFilter filter, CancellationToken cancellationToken);
```

En `CashSessionRepository.cs`:

```csharp
    public async Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.CashSessions.AsNoTracking().Where(session => session.TenantId == filter.TenantId);
        if (filter.Cashier is { } cashier)
        {
            query = query.Where(session => session.CashierId == cashier);
        }

        if (filter.OpenedFromUtc is { } from)
        {
            query = query.Where(session => session.OpenedAt >= from);
        }

        if (filter.OpenedToUtc is { } to)
        {
            query = query.Where(session => session.OpenedAt < to);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(session => session.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(session => session.OpenedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
```

En `PosSaleRepository.cs`:

```csharp
    public async Task<(IReadOnlyList<PosSaleListRow> Items, int Total)> ListAsync(
        PosSaleFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.Sales.AsNoTracking().IgnoreAutoIncludes().Where(sale => sale.TenantId == filter.TenantId);
        if (filter.Cashier is { } cashier)
        {
            query = query.Where(sale => sale.CashierId == cashier);
        }

        if (filter.SessionId is { } sessionId)
        {
            query = query.Where(sale => sale.CashSessionId == sessionId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(sale => sale.CreatedAt >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(sale => sale.CreatedAt < to);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(sale => sale.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Number))
        {
            var number = filter.Number.Trim();
            query = query.Where(sale => sale.SaleNumber == number);
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query
            .OrderByDescending(sale => sale.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Include(sale => sale.Payments)
            .ToListAsync(cancellationToken);

        // Una consulta por página, no una por fila: el estado de la caja decide voidable.
        var sessionIds = page.Select(sale => sale.CashSessionId).Distinct().ToList();
        var sessions = await dbContext.CashSessions
            .AsNoTracking()
            .Where(session => sessionIds.Contains(session.Id))
            .Select(session => new { session.Id, session.CashierName, session.Status })
            .ToDictionaryAsync(session => session.Id, cancellationToken);

        return (page
            .Select(sale => new PosSaleListRow(
                sale, sessions[sale.CashSessionId].CashierName, sessions[sale.CashSessionId].Status))
            .ToList(), total);
    }
```

`src/Modules/Pos/Modules.Pos.Application/PosScope.cs`:

```csharp
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// Alcance por cajero (spec, «Autorización»): sin pos.register.read se ven sólo las ventas y cajas
/// propias, como ReportingPermissions.AllAdvisorsRead. pos.register.read amplía; no reemplaza.
/// </summary>
internal static class PosScope
{
    public static async Task<MemberId?> CashierFilterAsync(
        IMembershipDirectory membershipDirectory, IExecutionContext executionContext, Guid tenantId,
        CancellationToken cancellationToken) =>
        executionContext.HasPermission(PosPermissions.RegisterRead)
            ? null
            : await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, tenantId, cancellationToken);

    public static async Task EnsureCanSeeAsync(
        MemberId owner, IMembershipDirectory membershipDirectory, IExecutionContext executionContext,
        Guid tenantId, CancellationToken cancellationToken)
    {
        if (await CashierFilterAsync(membershipDirectory, executionContext, tenantId, cancellationToken) is { } caller
            && caller != owner)
        {
            throw PosAuthorization.Denied();
        }
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/GetPosSale.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Detalle, reimpresión y "Verificar" del cobro incierto.</summary>
public sealed record GetPosSaleQuery(Guid TenantId, Guid SaleId) : IQuery<PosSaleResponse>;

public sealed class GetPosSaleHandler(
    IPosSaleRepository sales,
    ICashSessionRepository sessions,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetPosSaleQuery, PosSaleResponse>
{
    public async Task<PosSaleResponse> HandleAsync(GetPosSaleQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        var sale = await sales.FindAsync(query.TenantId, new PosSaleId(query.SaleId), cancellationToken)
            ?? throw PosNotFound.Sale(query.SaleId);
        await PosScope.EnsureCanSeeAsync(sale.CashierId, membershipDirectory, executionContext, query.TenantId, cancellationToken);
        return await PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/ListPosSales.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="From">Fecha local del tenant; se corta con StartOfDayUtc.</param>
/// <param name="To">Fecha local del tenant, incluida; se corta con EndOfDayExclusiveUtc.</param>
public sealed record ListPosSalesQuery(
    Guid TenantId, Guid? SessionId, DateOnly? From, DateOnly? To, string? Status, string? Number, int Page, int PageSize)
    : IQuery<PosPage<PosSaleListItemResponse>>;

public sealed class ListPosSalesValidator : AbstractValidator<ListPosSalesQuery>
{
    public ListPosSalesValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Status)
            .Must(status => status is null || Enum.GetNames<PosSaleStatus>().Contains(status, StringComparer.Ordinal))
            .WithMessage("The status must be Completed or Voided.");
        RuleFor(query => query.Number).MaximumLength(20);
        RuleFor(query => query)
            .Must(query => query.From is null || query.To is null || query.From <= query.To)
            .WithName("from").WithMessage("from must not be after to.");
    }
}

public sealed class ListPosSalesHandler(
    IPosSaleRepository sales,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ListPosSalesQuery> validator)
    : IQueryHandler<ListPosSalesQuery, PosPage<PosSaleListItemResponse>>
{
    public async Task<PosPage<PosSaleListItemResponse>> HandleAsync(ListPosSalesQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        // Sin pos.register.read filtra al cajero llamador aunque mande el sessionId de otro.
        var cashier = await PosScope.CashierFilterAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var (rows, total) = await sales.ListAsync(
            new PosSaleFilter(
                query.TenantId,
                cashier,
                query.SessionId is { } sessionId ? new CashSessionId(sessionId) : null,
                query.From is { } from ? calendar.StartOfDayUtc(from) : null,
                query.To is { } to ? calendar.EndOfDayExclusiveUtc(to) : null,
                query.Status is { } status ? Enum.Parse<PosSaleStatus>(status) : null,
                string.IsNullOrWhiteSpace(query.Number) ? null : query.Number.Trim(),
                query.Page,
                query.PageSize),
            cancellationToken);

        var items = rows.Select(row =>
        {
            var (voidable, reason) = PosVoidability.For(row.Sale.Status, row.SessionStatus);
            return new PosSaleListItemResponse(
                row.Sale.Id.Value,
                row.Sale.SaleNumber,
                calendar.ToLocal(row.Sale.CreatedAt),
                row.CashierName,
                row.Sale.CustomerName,
                row.Sale.Total,
                row.Sale.Status.ToString(),
                row.Sale.Payments.OrderBy(payment => payment.Position).Select(payment => payment.Method.ToString()).ToArray(),
                voidable,
                reason);
        }).ToArray();

        return new PosPage<PosSaleListItemResponse>(items, query.Page, query.PageSize, total);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/VoidPosSale.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record VoidPosSaleCommand(Guid TenantId, Guid SaleId, string? Reason) : ICommand<PosSaleResponse>;

public sealed class VoidPosSaleValidator : AbstractValidator<VoidPosSaleCommand>
{
    public VoidPosSaleValidator()
    {
        RuleFor(command => command.Reason)
            .Must(reason => reason is not null
                && reason.Trim().Length is >= PosLimits.VoidReasonMinLength and <= PosLimits.VoidReasonMaxLength)
            .WithMessage("The void reason must have between 3 and 500 characters.");
    }
}

/// <summary>
/// Anula quien tenga pos.sale.void (hoy sólo admin), no hace falta ser el cajero. Sólo con la caja
/// de la venta abierta: una caja cerrada es un arqueo que alguien ya firmó.
/// </summary>
public sealed class VoidPosSaleHandler(
    IPosSaleRepository sales,
    ICashSessionRepository sessions,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<VoidPosSaleCommand> validator)
    : ICommandHandler<VoidPosSaleCommand, PosSaleResponse>
{
    public async Task<PosSaleResponse> HandleAsync(VoidPosSaleCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleVoid);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var member = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var sale = await sales.FindAsync(command.TenantId, new PosSaleId(command.SaleId), cancellationToken)
            ?? throw PosNotFound.Sale(command.SaleId);
        var session = await sessions.FindAsync(command.TenantId, sale.CashSessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale '{sale.Id}' points to a missing cash session.");

        var now = clock.UtcNow;
        sale.Void(command.Reason!, member, now);   // already_voided primero (paso 3)
        session.RegisterVoid(sale, now);           // void_session_closed después (paso 4)
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.sale.voided", "pos_sale",
            sale.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/ListCashSessions.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record ListCashSessionsQuery(
    Guid TenantId, DateOnly? From, DateOnly? To, string? Status, int Page, int PageSize)
    : IQuery<PosPage<PosSessionSummaryResponse>>;

public sealed class ListCashSessionsValidator : AbstractValidator<ListCashSessionsQuery>
{
    public ListCashSessionsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Status)
            .Must(status => status is null || Enum.GetNames<CashSessionStatus>().Contains(status, StringComparer.Ordinal))
            .WithMessage("The status must be Open or Closed.");
        RuleFor(query => query)
            .Must(query => query.From is null || query.To is null || query.From <= query.To)
            .WithName("from").WithMessage("from must not be after to.");
    }
}

public sealed class ListCashSessionsHandler(
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ListCashSessionsQuery> validator)
    : IQueryHandler<ListCashSessionsQuery, PosPage<PosSessionSummaryResponse>>
{
    public async Task<PosPage<PosSessionSummaryResponse>> HandleAsync(ListCashSessionsQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var cashier = await PosScope.CashierFilterAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var (items, total) = await sessions.ListAsync(
            new CashSessionFilter(
                query.TenantId,
                cashier,
                query.From is { } from ? calendar.StartOfDayUtc(from) : null,
                query.To is { } to ? calendar.EndOfDayExclusiveUtc(to) : null,
                query.Status is { } status ? Enum.Parse<CashSessionStatus>(status) : null,
                query.Page,
                query.PageSize),
            cancellationToken);

        return new PosPage<PosSessionSummaryResponse>(
            items.Select(session => PosSessionMapping.ToSummary(session, calendar)).ToArray(),
            query.Page, query.PageSize, total);
    }
}
```

`src/Modules/Pos/Modules.Pos.Application/GetCashSession.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>El resumen de cierre (o el vivo si está abierta). La propia siempre; una ajena con pos.register.read.</summary>
public sealed record GetCashSessionQuery(Guid TenantId, Guid SessionId) : IQuery<PosSessionSummaryResponse>;

public sealed class GetCashSessionHandler(
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetCashSessionQuery, PosSessionSummaryResponse>
{
    public async Task<PosSessionSummaryResponse> HandleAsync(GetCashSessionQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        var session = await sessions.FindAsync(query.TenantId, new CashSessionId(query.SessionId), cancellationToken)
            ?? throw PosNotFound.Session(query.SessionId);
        await PosScope.EnsureCanSeeAsync(session.CashierId, membershipDirectory, executionContext, query.TenantId, cancellationToken);
        return PosSessionMapping.ToSummary(session, await tenantClock.GetAsync(query.TenantId, cancellationToken));
    }
}
```

En `QepServiceCollectionExtensions.cs`, después del handler de B10:

```csharp
        services.AddScoped<
            IQueryHandler<GetPosSaleQuery, PosSaleResponse>,
            GetPosSaleHandler>();
        services.AddScoped<
            IQueryHandler<ListPosSalesQuery, PosPage<PosSaleListItemResponse>>,
            ListPosSalesHandler>();
        services.AddScoped<
            ICommandHandler<VoidPosSaleCommand, PosSaleResponse>,
            VoidPosSaleHandler>();
        services.AddScoped<
            IQueryHandler<ListCashSessionsQuery, PosPage<PosSessionSummaryResponse>>,
            ListCashSessionsHandler>();
        services.AddScoped<
            IQueryHandler<GetCashSessionQuery, PosSessionSummaryResponse>,
            GetCashSessionHandler>();
```

- [ ] **Step 4: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS de todo `Modules.Pos.UnitTests` y de arquitectura.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Modules/Pos src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs tests/Modules/Pos/Modules.Pos.UnitTests/SaleReadAndVoidHandlersTests.cs; git commit -m "feat(pos): lectura, listado y anulación de ventas y cajas"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---


### Task B12: Adaptadores, endpoints y recorrido feliz por HTTP

**Files:**
- Create: `src/Bootstrapper/{PosProductLookup,PosCompanyLookup,PosCashierLookup}.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosWorld.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosRegisterApiTests.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (adaptadores, junto a `IQuotationProductPricingLookup` `:501`)

**Interfaces:**
- Consumes: B7 (`IProductRepository.FindByCodeAsync`), B8-B11 (comandos, consultas y DTOs).
- Produces: los 12 endpoints del spec bajo `/api/v1/tenants/{tenantId:guid}/pos`; `PosWorld.ArrangeAsync(QepApiFactory, PostgreSqlContainer)` con tenant, `pos` prendido, empresa, tasas 19 % y 5 %, y los tres productos del ejemplo; helpers `OpenSessionAsync`, `SaleBody`, `WorkedExampleLines`, `SplitPayments`.

- [ ] **Step 1: Escribir las pruebas de API que fallan**

`tests/Modules/Pos/Modules.Pos.IntegrationTests/PosWorld.cs`:

```csharp
using System.Net.Http.Json;
using Modules.Pos.Application;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>El tenant del ejemplo trabajado del spec, listo para vender.</summary>
internal sealed record PosWorld(
    PosTenant Tenant,
    HttpClient Admin,
    CompanyRef Company,
    Guid Shampoo,
    Guid Avena,
    Guid Jabon,
    Guid Tax19,
    Guid Tax5)
{
    public string Url => PosUrl(Tenant.TenantId);

    public static async Task<PosWorld> ArrangeAsync(QepApiFactory factory, PostgreSqlContainer database)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePosAsync(database, tenant.TenantId);
        var company = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        var tax19 = await CreateTaxRateAsync(tenant.Seeder, tenant.TenantId, "IVA 19", 19);
        var tax5 = await CreateTaxRateAsync(tenant.Seeder, tenant.TenantId, "IVA 5", 5);
        var shampoo = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "SH-400", "Shampoo 400 ml", 11_900m, tax19);
        var avena = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "AV-01", "Avena granel (kg)", 5_000m, null);
        var jabon = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "JB-03", "Jabón", 2_990m, tax5);
        // El dueño del tenant es miembro activo (admin): el handler lo resuelve a su membresía.
        var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AdminPosPermissions);
        return new PosWorld(tenant, admin, company, shampoo, avena, jabon, tax19, tax5);
    }

    public object[] WorkedExampleLines() =>
    [
        new { productId = Shampoo, quantity = 2m, discountPercentage = 10m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 },
        new { productId = Avena, quantity = 1.5m, discountPercentage = 0m, expectedUnitPrice = 5_000m, expectedTaxPercentage = 0 },
        new { productId = Jabon, quantity = 3m, discountPercentage = 0m, expectedUnitPrice = 2_990m, expectedTaxPercentage = 5 },
    ];

    public object[] PlainLine(decimal quantity = 1m) =>
    [
        new { productId = Shampoo, quantity, discountPercentage = 0m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 },
    ];

    public static object[] SplitPayments() =>
    [
        new { method = "Card", amount = 20_000m, reference = "1234" },
        new { method = "Cash", tendered = 20_000m },
    ];

    public static object[] CashPayment(decimal tendered) => [new { method = "Cash", tendered }];

    public static object SaleBody(Guid id, Guid sessionId, object[] lines, object[] payments) =>
        new { id, cashSessionId = sessionId, lines, payments };

    public async Task<PosOpenSessionResponse> OpenSessionAsync(HttpClient client, decimal openingFloat = 100_000m)
    {
        var response = await client.PostAsJsonAsync(
            $"{Url}/sessions", new { companyId = (Guid?)null, openingFloat }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<PosOpenSessionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        return session;
    }
}
```

`tests/Modules/Pos/Modules.Pos.IntegrationTests/PosRegisterApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosRegisterApiTests
{
    // Spec, «Integración»: abrir → vender (dividido, tarjeta, efectivo) → cerrar, con cuerpos
    // completos, paymentTotals con los tres medios y la diferencia.
    [Fact]
    public async Task OpenSellWithSplitCardAndCashThenCloseWithADifference()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);

        var before = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Session);
        Assert.Equal(world.Company.Id, before.DefaultCompanyId);

        var session = await world.OpenSessionAsync(world.Admin);
        Assert.Equal(1, session.Version);

        var preview = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview",
            new { lines = new[] { new { productId = world.Shampoo, quantity = 2m, discountPercentage = 10m }, new { productId = world.Avena, quantity = 1.5m, discountPercentage = 0m }, new { productId = world.Jabon, quantity = 3m, discountPercentage = 0m } } },
            TestContext.Current.CancellationToken);
        var previewBody = await preview.Content.ReadFromJsonAsync<PosPreviewResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(37_890m, previewBody!.Total);
        Assert.Equal(3_847.14m, previewBody.TaxAmount);

        var splitId = Guid.CreateVersion7();
        var split = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(splitId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()),
            TestContext.Current.CancellationToken);
        var sale = await split.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, split.StatusCode);
        Assert.Equal($"/api/v1/tenants/{world.Tenant.TenantId}/pos/sales/{splitId}", split.Headers.Location?.OriginalString);
        Assert.Equal("POS-000001", sale!.SaleNumber);
        Assert.Equal(2_110m, sale.ChangeAmount);
        Assert.Equal(34_042.86m, sale.Subtotal);
        Assert.Equal(new[] { 0, 5, 19 }, sale.TaxBreakdown.Select(entry => entry.TaxPercentage));
        Assert.Equal(17_890m, sale.Payments.Single(payment => payment.Method == "Cash").Amount);
        Assert.Equal("Consumidor final", sale.Customer.Name);
        Assert.Equal(world.Company.TaxId, sale.Issuer.TaxId);
        Assert.Equal(TimeSpan.FromHours(-5), sale.CreatedAtLocal.Offset);

        var card = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), [new { method = "Card", amount = 37_890m }]),
            TestContext.Current.CancellationToken);
        var cash = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), PosWorld.CashPayment(50_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal("POS-000002", (await card.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
        Assert.Equal(12_110m, (await cash.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.ChangeAmount);

        var live = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(4, live!.Session!.Version);
        Assert.Equal(100_000m + 17_890m + 37_890m, live.Session.ExpectedCash);
        Assert.Equal(
            new[] { new PosPaymentTotalResponse("Cash", 55_780m), new PosPaymentTotalResponse("Card", 57_890m), new PosPaymentTotalResponse("Transfer", 0m) },
            live.Session.PaymentTotals);

        using var close = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 155_780m - 890m, note = "Faltan 890" }),
        };
        close.Headers.TryAddWithoutValidation("If-Match", "\"4\"");
        var closed = await world.Admin.SendAsync(close, TestContext.Current.CancellationToken);
        var summary = await closed.Content.ReadFromJsonAsync<PosSessionSummaryResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("Closed", summary!.Status);
        Assert.Equal(155_780m, summary.ExpectedCash);
        Assert.Equal(-890m, summary.CashDifference);
        Assert.Equal(3, summary.SalesCount);
    }

    [Fact]
    public async Task ARejectedSaleDoesNotLeaveAGapInTheNumbering()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        var first = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var stale = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id,
                [new { productId = world.Shampoo, quantity = 1m, discountPercentage = 0m, expectedUnitPrice = 12_500m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var second = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Contains("pos.sale.price_changed", await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("POS-000002", (await second.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
    }

    // Decisión 30 y P10: exacto y con mayúsculas; inactivo y sin precio vuelven marcados.
    [Fact]
    public async Task ByCodeIsExactAndMarksInactiveAndUnpricedProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET is_active = false WHERE id = @id", ("id", world.Jabon));
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET price_base_cop = NULL, price_base_usd = 10 WHERE id = @id", ("id", world.Avena));

        var exact = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=SH-400", TestContext.Current.CancellationToken);
        var lower = await world.Admin.GetAsync($"{world.Url}/products/by-code?code=sh-400", TestContext.Current.CancellationToken);
        var inactive = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=JB-03", TestContext.Current.CancellationToken);
        var unpriced = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=AV-01", TestContext.Current.CancellationToken);
        var search = await world.Admin.GetFromJsonAsync<PosPage<PosProductResponse>>($"{world.Url}/products?search=sham", TestContext.Current.CancellationToken);

        Assert.True(exact!.Sellable);
        Assert.Equal(19, exact.TaxPercentage);
        Assert.Equal(HttpStatusCode.NotFound, lower.StatusCode);
        Assert.Contains("pos.product.not_found", await lower.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(("Inactive", false), (inactive!.UnsellableReason, inactive.Sellable));
        Assert.Equal(("PriceMissing", (decimal?)null), (unpriced!.UnsellableReason, unpriced.UnitPrice));
        Assert.Equal("SH-400", Assert.Single(search!.Items).Code);
    }

    // Spec, «Integración»: las seis políticas resuelven (403 y no 500 sin el permiso).
    [Fact]
    public async Task EveryEndpointAnswers403WithoutItsPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var nobody = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, "advisorship.read");
        var id = Guid.CreateVersion7();

        HttpResponseMessage[] responses =
        [
            await nobody.GetAsync($"{world.Url}/register", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sessions", new { openingFloat = 0m }, TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sessions/{id}/close", new { countedCash = 0m }, TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sessions", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sessions/{id}", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/products", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/products/by-code?code=SH-400", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales/preview", new { lines = world.PlainLine() }, TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales", PosWorld.SaleBody(id, id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sales/{id}", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sales", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales/{id}/void", new { reason = "Motivo" }, TestContext.Current.CancellationToken),
        ];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    // Con el spec de entitlements: sin la fila `pos`, los permisos se enmascaran y todo /pos/* da 403.
    [Fact]
    public async Task WithoutThePosModuleTheRegisterIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await DisablePosAsync(database, world.Tenant.TenantId);

        var response = await world.Admin.GetAsync($"{world.Url}/register", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El rol de sistema `cashier` vende sin descontar (spec, decisión 4).
    [Fact]
    public async Task AnInvitedCashierSellsButCannotDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var cashier = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var session = await world.OpenSessionAsync(cashier.Client);

        var plain = await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var discounted = await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        Assert.Equal("Cajero Invitado", (await plain.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.CashierName);
        Assert.Equal(HttpStatusCode.Forbidden, discounted.StatusCode);
        Assert.Contains("pos.sale.discount_not_allowed", await discounted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosRegisterApiTests"
```

Esperado: FAIL en las seis: `Expected: Created / Actual: NotFound` (o `NotFound` donde se espera 200/403): las rutas no existen todavía.

- [ ] **Step 3: Adaptadores en Bootstrapper**

`src/Bootstrapper/PosProductLookup.cs`:

```csharp
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Pos. Resuelve tasa e imagen como QuotationProductPricingLookup y QuotationProductLookup:
/// tasas por id distinto y las imágenes de toda la página en una sola llamada, no una por producto.
/// </summary>
internal sealed class PosProductLookup(
    IProductRepository products,
    ITaxRateRepository taxRates,
    IProductImageLookup images)
    : IPosProductLookup
{
    public async Task<(IReadOnlyList<PosProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (items, total) = await products.SearchAsync(
            tenantId, string.IsNullOrWhiteSpace(search) ? null : search, null, null, true, page, pageSize, cancellationToken);
        return (await MapAsync(tenantId, items, cancellationToken), total);
    }

    public async Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        var product = await products.FindByCodeAsync(tenantId, code, cancellationToken);
        return product is null ? null : (await MapAsync(tenantId, [product], cancellationToken))[0];
    }

    public async Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, PosProductRef>();
        }

        var found = await products.ListByIdsAsync(
            tenantId, productIds.Distinct().Select(id => new ProductId(id)).ToArray(), cancellationToken);
        return (await MapAsync(tenantId, found, cancellationToken)).ToDictionary(product => product.Id);
    }

    private async Task<IReadOnlyList<PosProductRef>> MapAsync(
        Guid tenantId, IReadOnlyList<Product> items, CancellationToken cancellationToken)
    {
        var percentages = new Dictionary<TaxRateId, int>();
        foreach (var taxRateId in items.Where(item => item.TaxRateId.HasValue).Select(item => item.TaxRateId!.Value).Distinct())
        {
            var taxRate = await taxRates.FindAsync(tenantId, taxRateId, cancellationToken);
            if (taxRate is not null)
            {
                percentages[taxRateId] = taxRate.Percentage;
            }
        }

        var imageIds = items.Where(item => item.ImageFileId.HasValue).Select(item => item.ImageFileId!.Value).Distinct().ToArray();
        IReadOnlyDictionary<Guid, ProductImageRef> found = imageIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await images.FindManyAsync(imageIds, cancellationToken);

        return items.Select(item => new PosProductRef(
            item.Id.Value,
            item.Code,
            item.Name,
            item.IsActive,
            item.PriceBaseCop,
            // Sin tasa, o con una inexistente, cotiza con 0 % (QuotationProductPricingResolver).
            item.TaxRateId is { } id && percentages.TryGetValue(id, out var percentage) ? percentage : 0,
            // La comprobación de tenant es del que consume, no del adaptador (IProductImageLookup).
            item.ImageFileId is { } fileId && found.TryGetValue(fileId, out var image) && image.TenantId == tenantId
                ? image.PublicUrl
                : null)).ToList();
    }
}
```

`src/Bootstrapper/PosCompanyLookup.cs`:

```csharp
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

internal sealed class PosCompanyLookup(ICompanyRepository companies) : IPosCompanyLookup
{
    public async Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken))
            .Select(ToRef)
            .ToList();

    public async Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is { } company
            ? ToRef(company)
            : null;

    private static PosCompanyRef ToRef(Company company) =>
        new(company.Id.Value, company.Name, company.TaxId, company.Address, company.Phone, company.IsActive);
}
```

`src/Bootstrapper/PosCashierLookup.cs`:

```csharp
using Modules.Identity.Application;
using Modules.Pos.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>DisplayName ?? Email, de las mismas fuentes que QuotationAdvisorLookup.</summary>
internal sealed class PosCashierLookup(IMembershipRepository memberships, IUserDirectory users) : IPosCashierLookup
{
    public async Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), [new MembershipId(membershipId)], cancellationToken);
        var membership = scoped.SingleOrDefault();
        if (membership is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(membership.DisplayName)
            ? await users.GetEmailAsync(membership.UserId, cancellationToken)
            : membership.DisplayName;
    }
}
```

En `QepServiceCollectionExtensions.cs`, junto a `services.AddScoped<IQuotationProductPricingLookup, QuotationProductPricingLookup>();` (`:501`):

```csharp
        // Pos (spec 2026-10-07): Catalog, Companies y Tenancy/Identity entran por adaptadores
        // (PosLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules).
        services.AddScoped<IPosProductLookup, PosProductLookup>();
        services.AddScoped<IPosCompanyLookup, PosCompanyLookup>();
        services.AddScoped<IPosCashierLookup, PosCashierLookup>();
```

(Si en la Task B8 ya moviste aquí `PosCompanyLookup` y `PosCashierLookup`, sólo falta el primero.)

- [ ] **Step 4: Endpoints**

`src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs` queda:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pos.Application;

namespace Modules.Pos.Api;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como companies y catalog. Cada endpoint declara su propio
        // RequireAuthorization (spec, «API»): el grupo no lleva política. Los handlers revalidan
        // tenant y permiso (doble capa) y devuelven 403, nunca 404, ante otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/pos")
            .WithTags("Pos");

        group.MapGet("/register", GetRegisterAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Produces<RegisterContextResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/sessions", OpenSessionAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Accepts<OpenCashSessionRequest>("application/json")
            .Produces<PosOpenSessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sessions/{sessionId:guid}/close", CloseSessionAsync)
            .RequireAuthorization(PosPermissions.RegisterOperate)
            .Accepts<CloseCashSessionRequest>("application/json")
            .Produces<PosSessionSummaryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/sessions", ListSessionsAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosPage<PosSessionSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/sessions/{sessionId:guid}", GetSessionAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosSessionSummaryResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/products", SearchProductsAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Produces<PosPage<PosProductResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/products/by-code", FindProductByCodeAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Produces<PosProductResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales/preview", PreviewSaleAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Accepts<PreviewPosSaleRequest>("application/json")
            .Produces<PosPreviewResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales", CreateSaleAsync)
            .RequireAuthorization(PosPermissions.SaleCreate)
            .Accepts<CreatePosSaleRequest>("application/json")
            .Produces<PosSaleResponse>(StatusCodes.Status201Created)
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/sales/{saleId:guid}", GetSaleAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/sales", ListSalesAsync)
            .RequireAuthorization(PosPermissions.SaleRead)
            .Produces<PosPage<PosSaleListItemResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sales/{saleId:guid}/void", VoidSaleAsync)
            .RequireAuthorization(PosPermissions.SaleVoid)
            .Accepts<VoidPosSaleRequest>("application/json")
            .Produces<PosSaleResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetRegisterAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetRegisterContextQuery(tenantId), cancellationToken));

    private static async Task<IResult> OpenSessionAsync(
        Guid tenantId, OpenCashSessionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var session = await dispatcher.SendAsync(
            new OpenCashSessionCommand(tenantId, request.CompanyId, request.OpeningFloat), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/pos/sessions/{session.Id}", session);
    }

    private static async Task<IResult> CloseSessionAsync(
        Guid tenantId,
        Guid sessionId,
        CloseCashSessionRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // El cajero cierra contra el arqueo que vio: la version de GET /pos/register (spec,
        // decisión 31). Mismo contrato que /orders-export-layout: sin If-Match 428, vieja 412.
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded cash session version is required.");
        }

        return Results.Ok(await dispatcher.SendAsync(
            new CloseCashSessionCommand(tenantId, sessionId, expectedVersion, request.CountedCash, request.Note),
            cancellationToken));
    }

    private static async Task<IResult> ListSessionsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        string? status = null,
        int page = 1,
        int pageSize = 20) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListCashSessionsQuery(tenantId, from, to, status, page, pageSize), cancellationToken));

    private static async Task<IResult> GetSessionAsync(
        Guid tenantId, Guid sessionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCashSessionQuery(tenantId, sessionId), cancellationToken));

    private static async Task<IResult> SearchProductsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 40) =>
        Results.Ok(await dispatcher.QueryAsync(
            new SearchPosProductsQuery(tenantId, search, page, pageSize), cancellationToken));

    private static async Task<IResult> FindProductByCodeAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken, string? code = null) =>
        // Sin code, el validador responde 422 con el campo en vez de un 400 del binder.
        Results.Ok(await dispatcher.QueryAsync(
            new FindPosProductByCodeQuery(tenantId, code ?? string.Empty), cancellationToken));

    private static async Task<IResult> PreviewSaleAsync(
        Guid tenantId, PreviewPosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PreviewPosSaleCommand(tenantId, request.Lines ?? []), cancellationToken));

    private static async Task<IResult> CreateSaleAsync(
        Guid tenantId, CreatePosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var creation = await dispatcher.SendAsync(
            new CreatePosSaleCommand(tenantId, request.Id, request.CashSessionId, request.Lines ?? [], request.Payments ?? []),
            cancellationToken);

        // 201 la primera vez; 200 en la repetición reconocida, con el mismo cuerpo (spec, «Crear venta»).
        return creation.Created
            ? Results.Created($"/api/v1/tenants/{tenantId}/pos/sales/{creation.Sale.Id}", creation.Sale)
            : Results.Ok(creation.Sale);
    }

    private static async Task<IResult> GetSaleAsync(
        Guid tenantId, Guid saleId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPosSaleQuery(tenantId, saleId), cancellationToken));

    private static async Task<IResult> ListSalesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        Guid? sessionId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? status = null,
        string? number = null,
        int page = 1,
        int pageSize = 20) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListPosSalesQuery(tenantId, sessionId, from, to, status, number, page, pageSize), cancellationToken));

    private static async Task<IResult> VoidSaleAsync(
        Guid tenantId, Guid saleId, VoidPosSaleRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new VoidPosSaleCommand(tenantId, saleId, request.Reason), cancellationToken));

    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

public sealed record OpenCashSessionRequest(Guid? CompanyId, decimal OpeningFloat);

public sealed record CloseCashSessionRequest(decimal CountedCash, string? Note);

public sealed record PreviewPosSaleRequest(IReadOnlyList<PosPreviewLineRequest>? Lines);

public sealed record CreatePosSaleRequest(
    Guid Id,
    Guid CashSessionId,
    IReadOnlyList<PosSaleLineRequest>? Lines,
    IReadOnlyList<PosPaymentRequest>? Payments);

public sealed record VoidPosSaleRequest(string? Reason);
```

- [ ] **Step 5: Ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --no-build --filter "FullyQualifiedName~PosRegisterApiTests|FullyQualifiedName~PosPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS. Si `ByCodeIsExactAndMarksInactiveAndUnpricedProducts` falla en el `UPDATE` por el nombre de la columna en USD o por un `CHECK` de precio, usa los nombres de `CatalogDbContext` (`ConfigureProduct`) y anótalo.

- [ ] **Step 6: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/Bootstrapper/PosProductLookup.cs src/Bootstrapper/PosCompanyLookup.cs src/Bootstrapper/PosCashierLookup.cs src/Bootstrapper/QepServiceCollectionExtensions.cs src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs tests/Modules/Pos/Modules.Pos.IntegrationTests/PosWorld.cs tests/Modules/Pos/Modules.Pos.IntegrationTests/PosRegisterApiTests.cs; git commit -m "feat(pos): endpoints y adaptadores del punto de venta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---


### Task B13: Integración de idempotencia, concurrencia, anulación, auditoría y aislamiento

**Files:**
- Create: `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosIsolationApiTests.cs`
- Modify: sólo si una prueba destapa un defecto (con su RED literal y el arreglo en el archivo dueño)

**Interfaces:**
- Consumes: B6-B12 (`PosWorld`, `PosApiHarness`).
- Produces: la red de integración que pide el spec («Pruebas → Integración»). No agrega código de producción salvo que una prueba falle por un defecto real.

Esta tarea es de **pruebas de caracterización**: escribe cada archivo, córrelo, y si algo falla, ese es el RED; arregla el código dueño (la tarea donde vive) con el mínimo cambio y vuelve a correr. Si todo pasa a la primera, el handoff lo dice así (no se inventa un RED).

- [ ] **Step 1: Escribir `PosSaleApiTests.cs`**

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosSaleApiTests
{
    private static Task<long> SalesWithIdAsync(Testcontainers.PostgreSql.PostgreSqlContainer database, Guid id) =>
        CountAsync(database, "SELECT count(*) FROM pos.sales WHERE id = @id", ("id", id));

    [Fact]
    public async Task TheSamePostTwiceAnswers201Then200WithOneRowAndOneNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        var body = PosWorld.SaleBody(id, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments());

        var first = await world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);
        var repeat = await world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        var b = await repeat.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(a!.SaleNumber, b!.SaleNumber);
        Assert.Equal(1, await SalesWithIdAsync(database, id));
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(1, register!.Session!.SalesCount);
    }

    [Fact]
    public async Task TheSameIdWithAnotherQuantityIsAConflictAndTheOriginalStays()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, world.PlainLine(1m), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var other = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, world.PlainLine(2m), PosWorld.CashPayment(30_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Contains("pos.sale.id_conflict", await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var original = await world.Admin.GetFromJsonAsync<PosSaleResponse>($"{world.Url}/sales/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(11_900m, original!.Total);
    }

    // Spec, «Integración»: dos POST idénticos en paralelo, repetido 20 veces → cada vez una fila,
    // las dos respuestas 2xx y el mismo saleNumber.
    [Fact]
    public async Task TwoIdenticalPostsInParallelAlwaysLeaveExactlyOneSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var id = Guid.CreateVersion7();
            var body = PosWorld.SaleBody(id, session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m));

            var responses = await Task.WhenAll(
                world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken),
                world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken));

            Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"attempt {attempt}: {response.StatusCode}"));
            var sales = await Task.WhenAll(responses.Select(response =>
                response.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken)));
            Assert.Equal(sales[0]!.SaleNumber, sales[1]!.SaleNumber);
            Assert.Equal(1, await SalesWithIdAsync(database, id));
        }

        Assert.Equal(20, await CountAsync(database, "SELECT count(*) FROM pos.sales WHERE tenant_id = @tenantId", ("tenantId", world.Tenant.TenantId)));
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(20, register!.Session!.SalesCount);
        Assert.Equal(PosSaleNumberFor(20), (await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?pageSize=1", TestContext.Current.CancellationToken))!.Items[0].SaleNumber);
    }

    private static string PosSaleNumberFor(int value) => $"POS-{value:D6}";

    [Fact]
    public async Task CloseWithAStaleIfMatchIs412AndWithoutItIs428()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        using var stale = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 111_900m }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var conflict = await world.Admin.SendAsync(stale, TestContext.Current.CancellationToken);
        var missing = await world.Admin.PostAsJsonAsync($"{world.Url}/sessions/{session.Id}/close", new { countedCash = 111_900m }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Contains("concurrency.conflict", await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Contains("precondition.if_match_required", await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoidGivesTheMoneyBackWhileOpenAndIsRejectedOnceClosed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var voidedId = Guid.CreateVersion7();
        var keptId = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(voidedId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(keptId, session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var voided = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{voidedId}/void", new { reason = "Cliente se arrepintió" }, TestContext.Current.CancellationToken);
        var again = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{voidedId}/void", new { reason = "Otra vez" }, TestContext.Current.CancellationToken);
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal("Voided", (await voided.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.Status);
        Assert.Contains("pos.sale.already_voided", await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(111_900m, register!.Session!.ExpectedCash);
        Assert.Equal(1, register.Session.VoidedCount);

        using var close = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 111_900m }),
        };
        close.Headers.TryAddWithoutValidation("If-Match", $"\"{register.Session.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await world.Admin.SendAsync(close, TestContext.Current.CancellationToken)).StatusCode);

        var late = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{keptId}/void", new { reason = "Tarde" }, TestContext.Current.CancellationToken);
        var list = await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales", TestContext.Current.CancellationToken);

        Assert.Contains("pos.sale.void_session_closed", await late.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal((false, "AlreadyVoided"), (list!.Items.Single(item => item.Id == voidedId).Voidable, list.Items.Single(item => item.Id == voidedId).VoidBlockedReason));
        Assert.Equal((false, "SessionClosed"), (list.Items.Single(item => item.Id == keptId).Voidable, list.Items.Single(item => item.Id == keptId).VoidBlockedReason));
    }

    // Auditoría en la misma transacción que el cambio, y ninguna fila si el cambio falla.
    [Fact]
    public async Task EveryOperationLeavesItsAuditRowAndAFailedSaleLeavesNone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var saleId = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(saleId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);
        var failed = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(1m)), TestContext.Current.CancellationToken);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{saleId}/void", new { reason = "Motivo" }, TestContext.Current.CancellationToken);

        Assert.Contains("pos.sale.payment_insufficient", await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.session.opened"));
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.sale.created"));
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.sale.voided"));
        Assert.Equal(1, await CountAsync(database,
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'pos.sale.created' AND payload::text LIKE '%discount:1:SH-400:10%'"));
    }

    [Fact]
    public async Task CardOnlyAndTransferOnlyCarryNoCashLineAndBadCashBodiesAre422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        var card = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Card", amount = 11_900m }]), TestContext.Current.CancellationToken);
        var transfer = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Transfer", amount = 11_900m, reference = "TRX-9" }]), TestContext.Current.CancellationToken);
        var cashWithAmount = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Cash", amount = 11_900m, tendered = 20_000m }]), TestContext.Current.CancellationToken);
        var zeroWithTendered = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id,
                [new { productId = world.Shampoo, quantity = 1m, discountPercentage = 100m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(1_000m)), TestContext.Current.CancellationToken);
        var next = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        Assert.DoesNotContain((await card.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.Payments, payment => payment.Method == "Cash");
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cashWithAmount.StatusCode);
        Assert.Contains("validation.failed", await cashWithAmount.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zeroWithTendered.StatusCode);
        Assert.Contains("pos.sale.tendered_invalid", await zeroWithTendered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        // Ni el 422 de validación ni el de dominio gastaron número.
        Assert.Equal("POS-000003", (await next.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
    }

    // Precio igual y tasa cambiada después del preview: el total no se mueve, el desglose sí.
    [Fact]
    public async Task OnlyTheTaxRateChangedAfterThePreviewIsPriceChanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET tax_rate_id = @tax WHERE id = @id", ("tax", world.Tax5), ("id", world.Shampoo));

        var response = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("pos.sale.price_changed", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Escribir `PosIsolationApiTests.cs`**

```csharp
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosIsolationApiTests
{
    [Fact]
    public async Task ARouteForAnotherTenantIs403AndASaleOfAnotherTenantIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var mine = await PosWorld.ArrangeAsync(factory, database);
        var theirs = await PosWorld.ArrangeAsync(factory, database);
        var theirSession = await theirs.OpenSessionAsync(theirs.Admin);
        var theirSaleId = Guid.CreateVersion7();
        await theirs.Admin.PostAsJsonAsync($"{theirs.Url}/sales",
            PosWorld.SaleBody(theirSaleId, theirSession.Id, theirs.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var foreignRoute = await mine.Admin.GetAsync($"{theirs.Url}/register", TestContext.Current.CancellationToken);
        var foreignSale = await mine.Admin.GetAsync($"{mine.Url}/sales/{theirSaleId}", TestContext.Current.CancellationToken);
        var foreignVoid = await mine.Admin.PostAsJsonAsync($"{mine.Url}/sales/{theirSaleId}/void", new { reason = "Ajena" }, TestContext.Current.CancellationToken);
        var foreignSession = await mine.Admin.GetAsync($"{mine.Url}/sessions/{theirSession.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSale.StatusCode);
        Assert.Contains("pos.sale.not_found", await foreignSale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, foreignVoid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSession.StatusCode);
    }

    // La FK a companies.companies la crea la migración de Pos: sólo este harness la tiene.
    [Fact]
    public async Task DeletingACompanyWithACashSessionIsInUse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await world.OpenSessionAsync(world.Admin);

        var response = await world.Tenant.Seeder.DeleteAsync(
            $"/api/v1/tenants/{world.Tenant.TenantId}/companies/{world.Company.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("companies.company.in_use", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUserProbeKeepsACashierWithSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var cashier = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var session = await world.OpenSessionAsync(cashier.Client);
        await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(item => item.Source == "pos");

        Assert.True(await probe.HasReferencesAsync(cashier.UserId, TestContext.Current.CancellationToken));
        Assert.False(await probe.HasReferencesAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACashierOnlySeesTheirOwnSalesAndSessionsAndASupervisorSeesAll()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var first = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var second = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var firstSession = await world.OpenSessionAsync(first.Client);
        var secondSession = await world.OpenSessionAsync(second.Client);
        var firstSale = Guid.CreateVersion7();
        await first.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(firstSale, firstSession.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        await second.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), secondSession.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")).DateTime).ToString("yyyy-MM-dd");

        var peek = await second.Client.GetAsync($"{world.Url}/sales/{firstSale}", TestContext.Current.CancellationToken);
        var ownSales = await second.Client.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?sessionId={firstSession.Id}", TestContext.Current.CancellationToken);
        var ownSessions = await second.Client.GetFromJsonAsync<PosPage<PosSessionSummaryResponse>>($"{world.Url}/sessions", TestContext.Current.CancellationToken);
        var allToday = await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?from={today}&to={today}", TestContext.Current.CancellationToken);
        var allSessions = await world.Admin.GetFromJsonAsync<PosPage<PosSessionSummaryResponse>>($"{world.Url}/sessions?status=Open", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, peek.StatusCode);
        Assert.Empty(ownSales!.Items);
        Assert.Equal([secondSession.Id], ownSessions!.Items.Select(item => item.Id).ToArray());
        Assert.Equal(2, allToday!.Total);
        Assert.Equal(2, allSessions!.Total);
    }
}
```

- [ ] **Step 3: Correr y tratar cada falla como RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --no-build --filter "FullyQualifiedName~PosSaleApiTests|FullyQualifiedName~PosIsolationApiTests"
```

Esperado: PASS. Guías para las fallas más probables:

- `TwoIdenticalPostsInParallel…` con un `500`: el log del host muestra la excepción. Si es `InvalidOperationException` por una transacción ya disuelta en `ResetAsync`, el `await using` de `CreatePosSaleHandler` ya la cerró: `ResetAsync` sólo debe hacer `RollbackAsync` si `CurrentTransaction` sigue viva (ya es así; revisa que no se haya cambiado). Si es un `23505` de `IX_sales_tenant_number`, el contador no está serializando: revisa que `NextAsync` corra **dentro** de la transacción.
- `ACashierOnlySeesTheirOwn…` con 500 en `/sales`: la traducción de `sessionIds.Contains(session.Id)` con conversor de valor. Reemplaza por `var rawIds = sessionIds.Select(id => id.Value).ToList();` y `.Where(session => rawIds.Contains(EF.Property<Guid>(session, "Id")))` en `PosSaleRepository.ListAsync`, con su prueba en rojo como evidencia.

- [ ] **Step 4: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después (agrega a `git add` los archivos de producción que hayas tenido que arreglar):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs tests/Modules/Pos/Modules.Pos.IntegrationTests/PosIsolationApiTests.cs; git commit -m "test(pos): integración de idempotencia, concurrencia, aislamiento y auditoría"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

---

### Task B14: Reglas del repo, suite completa contra la línea base y humo manual

**Files:**
- Modify: `CLAUDE.md` (gotcha «`Conversations` no existe»: «Los doce módulos» → trece, con Pos)

**Interfaces:**
- Consumes: B0-B13.
- Produces: la evidencia literal del handoff y el procedimiento de humo.

- [ ] **Step 1: Actualizar `CLAUDE.md`**

En el último gotcha, reemplaza «Los doce módulos construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity, Notifications, Quotations, Reporting, Storage y Tenancy» por «Los trece módulos construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity, Notifications, Pos, Quotations, Reporting, Storage y Tenancy». Agrega, como gotcha nuevo, inmediatamente antes:

```markdown
- **`Pos` necesita la capacidad `pos` y no viene con el signup.** `GrantDefaultModulesOnSignup`
  concede seis módulos sin `pos`, así que en un tenant nuevo todo `/pos/*` da 403 aunque el rol
  tenga los permisos: se prende con la fila de `tenancy.tenant_modules` (spec de entitlements,
  «Operación»). En pruebas, `PosApiHarness.EnablePosAsync`. Y `cashier` es clave de rol de sistema
  reservada: antes de desplegar se comprueba que ningún tenant tenga un rol custom con esa clave
  (spec 2026-10-07, «Despliegue»).
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add CLAUDE.md; git commit -m "docs(pos): módulo Pos en las reglas del repositorio"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

- [ ] **Step 2: Restore, build y modelos sin cambios pendientes**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
git status --short
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext
```

Esperado: `git status` vacío; build `0 Advertencia(s)`, `0 Errores`; tres veces `No changes have been made to the model since the last migration.`

- [ ] **Step 3: Suite completa, una vez, en primer plano**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$final = Join-Path $env:TEMP "qep-pos-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $final
```

- [ ] **Step 4: Comparar por nombre contra la línea base de B0**

```powershell
$final = Join-Path $env:TEMP "qep-pos-final"
$failedNow = Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique
$baseline = @(Get-Content -LiteralPath (Join-Path $env:TEMP "qep-pos-baseline-failed.txt") -ErrorAction SilentlyContinue)
"Nuevas fallas:"
$failedNow | Where-Object { $_ -notin $baseline }
"Fallas previas que ahora pasan:"
$baseline | Where-Object { $_ -and $_ -notin $failedNow }
```

Esperado: «Nuevas fallas:» sin líneas debajo. Cualquier nombre nuevo es una regresión de esta rama: se corrige (RED literal, arreglo, GREEN, commit) antes de cerrar. Las previas se anotan en el handoff tal cual.

- [ ] **Step 5: Formato de toda la rama y residuos**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
$base = git merge-base feature/modulos-por-tenant HEAD
$files = git diff --name-only $base HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-pos-format"
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
git log --oneline "$base..HEAD"
git log --format=%B "$base..HEAD" | Select-String -SimpleMatch "Co-Authored-By"
git branch --show-current
```

Esperado: sin diagnósticos propios; quince commits (B0-B14); el `Select-String` sin salida; rama `feature/pos`.

- [ ] **Step 6: Humo manual de la API (PowerShell)**

Contra la base local `dev_lulo_crm_v2` del contenedor `postgres18`. **Primero**, el chequeo previo al despliegue del spec: ningún rol custom con la clave `cashier`. El SQL va a un archivo porque PowerShell 5.1 rompe las comillas dobles que `"authorization"` necesita:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
docker start postgres18
Set-Content -Path $env:TEMP\check-cashier-role.sql -Encoding ascii -Value 'SELECT tenant_id, id, display_name FROM "authorization".roles WHERE key = ''cashier'';'
Get-Content $env:TEMP\check-cashier-role.sql | docker exec -i postgres18 psql -U postgres -d dev_lulo_crm_v2
```

Esperado: `(0 rows)`. Con alguna fila, **para**: se decide con el owner (spec, «Despliegue»).

Levanta la API con la semilla (crea `origen-botanico` y le da admin a `Seed:OwnerEmail`) y con el stub de headers, para el humo por `curl.exe`:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5199"
$env:Authentication__UseDevelopmentStub = "true"
$env:Storage__R2__PublicBucket = "qep-public"
$env:Storage__R2__PublicBaseUrl = "https://cdn.qep.test"
$env:Seed__Enabled = "true"
dotnet run --project src/Api --no-launch-profile -p:NuGetAudit=false
```

Esperado en el log: `Now listening on: http://localhost:5199` y la migración `InitialPos` aplicada. En **otra** ventana de PowerShell, prende `pos` en el tenant sembrado y busca al dueño:

```powershell
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT id, 'pos', now(), 'manual', 'Humo POS' FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -At -c "SELECT t.id || ' ' || m.user_id FROM tenancy.tenants t JOIN tenancy.memberships m ON m.tenant_id = t.id WHERE t.slug = 'origen-botanico' LIMIT 1;"
```

Con los dos ids que imprime la última línea (tenant y usuario):

```powershell
$tenant = "<tenant-id>"
$user = "<user-id>"
$perms = "pos.sale.read,pos.sale.create,pos.sale.void,pos.sale.discount,pos.register.operate,pos.register.read"
curl.exe -s "http://localhost:5199/api/v1/tenants/$tenant/pos/register" -H "X-Subject-Id: $user" -H "X-Tenant-Id: $tenant" -H "X-Permissions: $perms"
Set-Content -Path $env:TEMP\open.json -Encoding ascii -Value '{"companyId":null,"openingFloat":100000}'
curl.exe -s -X POST "http://localhost:5199/api/v1/tenants/$tenant/pos/sessions" -H "Content-Type: application/json" -H "X-Subject-Id: $user" -H "X-Tenant-Id: $tenant" -H "X-Permissions: $perms" -d "@$env:TEMP\open.json"
curl.exe -s "http://localhost:5199/api/v1/tenants/$tenant/pos/products?pageSize=3" -H "X-Subject-Id: $user" -H "X-Tenant-Id: $tenant" -H "X-Permissions: $perms"
```

Esperado: el primero con `"session":null` y la empresa sembrada; el segundo `201` con `"version":1` (si el tenant tiene más de una empresa activa, `pos.session.company_required`: repite con su `companyId`); el tercero con productos y `"sellable"`. El humo de la pantalla (`/pos` en la SPA) es la Task F15, con la API corriendo **sin** el stub.

- [ ] **Step 7: Handoff del backend**

Entrega, en este orden: RED y GREEN literales de cada tarea; la salida de los Steps 2-5; el resultado del humo; las decisiones P1-P11 de este plan como puntos a confirmar; las fallas previas de la línea base; y el recordatorio del orden de despliegue: **entitlements primero, después POS; backend antes que frontend**. La rama **no** se mergea ni se publica desde este plan.

---


# Track frontend (`qep-frontend`)

Corre en paralelo con el backend: todo se prueba contra el contrato del spec con `fetch` mockeado. Rutas relativas a `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos`. Cada prueba nueva va junto a su archivo (`*.test.ts(x)`), nunca en `__tests__`. Copy en español tuteando; identificadores y comentarios en inglés.

**Tareas con skills de diseño.** F10 (ticket), F11 (apertura y cierre), F12 (diálogo de cobro), F13 (pantalla de caja) y F14 (listados) se ejecutan cargando antes `impeccable` y `frontend-design` (y `emil-design-eng` para el detalle de interacción: foco, resaltado de 600 ms, `prefers-reduced-motion`), **sobre el tema claro botánico existente** (`src/index.css` `:root`, `--primary #a65331`, `--accent #dceae3`, sin modo oscuro). La lógica de esas tareas sigue siendo TDD; las skills deciden composición, espaciado, jerarquía y micro-interacciones, nunca el comportamiento que fijan las pruebas.

### Task F0: Worktree, precondición, línea base y documentos

**Files:**
- Create: `docs/superpowers/specs/2026-10-07-modulo-pos-design.md`, `docs/superpowers/plans/2026-10-07-modulo-pos.md` (copias)

**Interfaces:**
- Consumes: la rama `feature/modulos-por-tenant` del frontend con `ModuleGate`, `useTenantModules` y `src/test/tenant-modules.ts`.
- Produces: el worktree en `feature/pos`, su índice de CodeGraph y `$env:TEMP\qep-pos-front-baseline-failed.txt`.

- [ ] **Step 1: Worktree desde la rama de entitlements**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git fetch origin
git branch -a --list "*modulos-por-tenant*"
git worktree add ..\qep-frontend-worktrees\pos -b feature/pos feature/modulos-por-tenant
```

Esperado: la rama existe (local o `origin/…`; con la remota, el último argumento es `origin/feature/modulos-por-tenant`). Si no existe, **para**. El checkout principal de `qep-frontend` está en `main` y no se toca.

- [ ] **Step 2: Comprobar entitlements, CodeGraph e instalar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
git branch --show-current
Test-Path src\components\module-gate.tsx
Test-Path src\features\auth\hooks\use-tenant-modules.ts
Test-Path src\test\tenant-modules.ts
git grep -n "'pos'" -- src/features/auth/types/tenant-modules.ts
gentle-ai codegraph init --cwd C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun install --frozen-lockfile
```

Esperado: rama `feature/pos`; tres `True`; `'pos'` en la unión `TenantModuleKey`. Si algo falta, **para y pregunta**.

- [ ] **Step 3: Línea base de pruebas, lint y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run --reporter=json --outputFile="$env:TEMP\qep-pos-front-baseline.json"
$report = Get-Content -LiteralPath "$env:TEMP\qep-pos-front-baseline.json" -Raw | ConvertFrom-Json
$report.testResults | ForEach-Object { $_.assertionResults | Where-Object { $_.status -eq 'failed' } | ForEach-Object { $_.fullName } } | Sort-Object -Unique | Set-Content -Encoding utf8 "$env:TEMP\qep-pos-front-baseline-failed.txt"
"Fallas previas: {0}" -f @(Get-Content -LiteralPath "$env:TEMP\qep-pos-front-baseline-failed.txt").Count
bun run lint
bun run build
```

Esperado: un número de fallas previas (por nombre de prueba, no por archivo: el frontend no tiene CI de pruebas y puede haberlas); lint y build sin errores.

- [ ] **Step 4: Copiar spec y plan, y commitear**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
$scratch = "C:\Users\andre\AppData\Local\Temp\claude\c--Users-andre-OneDrive-Documentos2-repositories-QCode-templates-qep-qep-backend\b6ad7612-c9ff-4dff-a4c1-51df3b0a1bcb\scratchpad"
New-Item -ItemType Directory -Force docs\superpowers\specs, docs\superpowers\plans | Out-Null
Copy-Item "$scratch\specs\2026-10-07-modulo-pos-design.md" docs\superpowers\specs\2026-10-07-modulo-pos-design.md
Copy-Item "$scratch\plans\2026-10-07-modulo-pos.md" docs\superpowers\plans\2026-10-07-modulo-pos.md
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add docs/superpowers/specs/2026-10-07-modulo-pos-design.md docs/superpowers/plans/2026-10-07-modulo-pos.md; git commit -m "docs(pos): spec y plan del módulo POS"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F1: Tipos y servicio del punto de venta

**Files:**
- Create: `src/features/pos/types/pos.ts`, `src/features/pos/services/pos.api.ts`
- Test: `src/features/pos/services/pos.api.test.ts`

**Interfaces:**
- Consumes: `apiRequest`, `ApiError`, `NetworkError` de `@/lib/api-client`.
- Produces (los usan todas las tareas siguientes):
  - Tipos: `PaymentMethod`, `UnsellableReason`, `VoidBlockedReason`, `PosCompanyOption`, `PosPaymentTotal`, `PosOpenSession`, `RegisterContext`, `SessionSummary`, `PosPage<T>`, `PosProduct`, `PreviewLineRequest`, `PreviewLine`, `SalePreview`, `SaleLineRequest`, `PaymentRequest`, `CreatePosSaleRequest`, `PosSale`, `PosSaleListItem`, `SalesFilters`, `SessionsFilters`
  - Constantes: `POS_PERMISSIONS`, `PAYMENT_METHOD_LABELS`, `VOID_BLOCKED_REASON_LABELS`, `UNSELLABLE_REASON_LABELS`
  - Servicio: `fetchRegisterContext`, `openSession`, `closeSession`, `fetchSessions`, `fetchSession`, `searchProducts`, `fetchProductByCode`, `previewSale`, `createSale`, `fetchSale`, `fetchSales`, `voidSale`, `POS_CODE_MESSAGES`, `describePosFailure`

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/pos/services/pos.api.test.ts`:

```ts
import { ApiError, NetworkError } from '@/lib/api-client'

import {
  closeSession,
  createSale,
  describePosFailure,
  fetchProductByCode,
  fetchSales,
  previewSale,
} from './pos.api'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function lastCall() {
  const [input, init] = vi.mocked(fetch).mock.calls.at(-1)!
  return { url: String(input), init: init ?? {}, headers: new Headers(init?.headers) }
}

describe('pos.api', () => {
  it('closes a session sending the loaded version in If-Match', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, { id: 's-1', status: 'Closed' }))

    await closeSession(TENANT, 's-1', 4, { countedCash: 117000, note: null })

    const call = lastCall()
    expect(call.url).toBe(`/api/v1/tenants/${TENANT}/pos/sessions/s-1/close`)
    expect(call.init.method).toBe('POST')
    expect(call.headers.get('If-Match')).toBe('"4"')
    expect(JSON.parse(String(call.init.body))).toEqual({ countedCash: 117000, note: null })
  })

  it('posts the exact sale body and accepts 200 and 201 alike', async () => {
    const body = {
      id: '6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11',
      cashSessionId: 's-1',
      lines: [{ productId: 'p-1', quantity: 2, discountPercentage: 10, expectedUnitPrice: 11900, expectedTaxPercentage: 19 }],
      payments: [{ method: 'Cash' as const, tendered: 30000 }],
    }
    vi.mocked(fetch).mockResolvedValueOnce(json(201, { id: body.id, saleNumber: 'POS-000001' }))
    vi.mocked(fetch).mockResolvedValueOnce(json(200, { id: body.id, saleNumber: 'POS-000001' }))

    const created = await createSale(TENANT, body)
    const repeated = await createSale(TENANT, body)

    expect(lastCall().url).toBe(`/api/v1/tenants/${TENANT}/pos/sales`)
    expect(JSON.parse(String(lastCall().init.body))).toEqual(body)
    expect(created.saleNumber).toBe('POS-000001')
    expect(repeated.saleNumber).toBe('POS-000001')
  })

  it('encodes the scanned code and the list filters', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, { items: [], page: 1, pageSize: 20, total: 0 }))

    await fetchProductByCode(TENANT, 'SH-400 #2')
    expect(lastCall().url).toBe(`/api/v1/tenants/${TENANT}/pos/products/by-code?code=SH-400+%232`)

    await fetchSales(TENANT, { from: '2026-10-07', to: '2026-10-07', status: 'Voided', number: 'POS-000042', page: 2, pageSize: 20 })
    expect(lastCall().url).toBe(
      `/api/v1/tenants/${TENANT}/pos/sales?from=2026-10-07&to=2026-10-07&status=Voided&number=POS-000042&page=2&pageSize=20`,
    )
  })

  it('previews only productId, quantity and discount', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, { lines: [], subtotal: 0, taxAmount: 0, discountAmount: 0, total: 0, zeroTotalNotAllowed: false }))

    await previewSale(TENANT, [{ productId: 'p-1', quantity: 1.5, discountPercentage: 0 }])

    expect(JSON.parse(String(lastCall().init.body))).toEqual({
      lines: [{ productId: 'p-1', quantity: 1.5, discountPercentage: 0 }],
    })
  })

  it('describes failures by code, network and fallback', () => {
    expect(describePosFailure(new ApiError(422, { code: 'pos.sale.price_changed' }))).toBe(
      'El precio o el IVA de un producto cambió. Revisa el carrito y vuelve a cobrar.',
    )
    expect(describePosFailure(new ApiError(403, { code: 'pos.sale.discount_not_allowed' }))).toBe(
      'No tienes permiso para dar descuentos ni cobrar ventas en $0.',
    )
    expect(describePosFailure(new NetworkError(new TypeError('Failed to fetch')))).toBe(
      'No pudimos conectarnos. Revisa tu conexión e intenta de nuevo.',
    )
    expect(describePosFailure(new ApiError(500, {}))).toBe('Algo salió mal. Intenta de nuevo.')
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/services/pos.api.test.ts
```

Esperado: FAIL, `Failed to resolve import "./pos.api"`.

- [ ] **Step 3: Implementar**

`src/features/pos/types/pos.ts`:

```ts
// Contract of qep-backend Modules.Pos.Application/PosDtos.cs (spec 2026-10-07, «API»). Enums
// travel by name; the labels live here, never in the backend.

export const POS_PERMISSIONS = {
  saleRead: 'pos.sale.read',
  saleCreate: 'pos.sale.create',
  saleVoid: 'pos.sale.void',
  saleDiscount: 'pos.sale.discount',
  registerOperate: 'pos.register.operate',
  registerRead: 'pos.register.read',
} as const

export type PaymentMethod = 'Cash' | 'Card' | 'Transfer'
export type UnsellableReason = 'Inactive' | 'PriceMissing' | 'NotFound'
export type VoidBlockedReason = 'AlreadyVoided' | 'SessionClosed'

export const PAYMENT_METHOD_LABELS: Record<PaymentMethod, string> = {
  Cash: 'Efectivo',
  Card: 'Tarjeta',
  Transfer: 'Transferencia',
}

export const VOID_BLOCKED_REASON_LABELS: Record<VoidBlockedReason, string> = {
  AlreadyVoided: 'Ya está anulada',
  SessionClosed: 'La caja de esta venta ya se cerró',
}

export const UNSELLABLE_REASON_LABELS: Record<UnsellableReason, string> = {
  Inactive: 'Inactivo',
  PriceMissing: 'Sin precio',
  NotFound: 'Ya no existe',
}

export interface PosCompanyOption {
  id: string
  name: string
  taxId: string
}

export interface PosPaymentTotal {
  method: PaymentMethod
  amount: number
}

export interface PosOpenSession {
  id: string
  status: 'Open' | 'Closed'
  openedAt: string
  openedAtLocal: string
  openedBeforeToday: boolean
  company: PosCompanyOption
  openingFloat: number
  salesCount: number
  voidedCount: number
  salesTotal: number
  expectedCash: number
  version: number
  paymentTotals: PosPaymentTotal[]
}

export interface RegisterContext {
  cashier: { memberId: string; name: string }
  session: PosOpenSession | null
  companies: PosCompanyOption[]
  defaultCompanyId: string | null
}

export interface SessionSummary {
  id: string
  status: 'Open' | 'Closed'
  cashierName: string
  company: { name: string; taxId: string }
  openedAtLocal: string
  closedAtLocal: string | null
  openingFloat: number
  salesCount: number
  voidedCount: number
  salesTotal: number
  paymentTotals: PosPaymentTotal[]
  expectedCash: number
  countedCash: number | null
  cashDifference: number | null
  note: string | null
}

export interface PosPage<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

export interface PosProduct {
  id: string
  code: string
  name: string
  unitPrice: number | null
  taxPercentage: number
  imageUrl: string | null
  sellable: boolean
  unsellableReason: UnsellableReason | null
}

export interface PreviewLineRequest {
  productId: string
  quantity: number
  discountPercentage: number
}

export interface PreviewLine {
  productId: string
  code: string | null
  name: string | null
  quantity: number
  unitPrice: number | null
  taxPercentage: number | null
  discountPercentage: number
  discountAmount: number
  taxAmount: number
  subtotal: number
  lineTotal: number
  sellable: boolean
  unsellableReason: UnsellableReason | null
}

export interface SalePreview {
  lines: PreviewLine[]
  subtotal: number
  taxAmount: number
  discountAmount: number
  total: number
  zeroTotalNotAllowed: boolean
}

export interface SaleLineRequest {
  productId: string
  quantity: number
  discountPercentage: number
  expectedUnitPrice: number
  expectedTaxPercentage: number
}

// Cash carries only `tendered`; the server computes what it applies (spec, decisión 41).
export type PaymentRequest =
  | { method: 'Cash'; tendered: number }
  | { method: 'Card' | 'Transfer'; amount: number; reference: string | null }

export interface CreatePosSaleRequest {
  id: string
  cashSessionId: string
  lines: SaleLineRequest[]
  payments: PaymentRequest[]
}

export interface PosSale {
  id: string
  saleNumber: string
  status: 'Completed' | 'Voided'
  createdAt: string
  createdAtLocal: string
  cashSessionId: string
  cashierName: string
  issuer: { name: string; taxId: string; address: string | null; phone: string | null }
  customer: { name: string; identificationType: string | null; identificationNumber: string }
  lines: {
    position: number
    productId: string
    code: string
    name: string
    quantity: number
    unitPrice: number
    discountPercentage: number
    taxPercentage: number
    discountAmount: number
    taxAmount: number
    subtotal: number
    lineTotal: number
  }[]
  subtotal: number
  taxAmount: number
  discountAmount: number
  total: number
  taxBreakdown: { taxPercentage: number; base: number; taxAmount: number }[]
  payments: { method: PaymentMethod; amount: number; tendered: number | null; reference: string | null }[]
  changeAmount: number
  void: { reason: string; voidedAtLocal: string; voidedByName: string } | null
  voidable: boolean
  voidBlockedReason: VoidBlockedReason | null
}

export interface PosSaleListItem {
  id: string
  saleNumber: string
  createdAtLocal: string
  cashierName: string
  customerName: string
  total: number
  status: 'Completed' | 'Voided'
  paymentMethods: PaymentMethod[]
  voidable: boolean
  voidBlockedReason: VoidBlockedReason | null
}

export interface SalesFilters {
  sessionId?: string
  from?: string
  to?: string
  status?: 'Completed' | 'Voided'
  number?: string
  page?: number
  pageSize?: number
}

export interface SessionsFilters {
  from?: string
  to?: string
  status?: 'Open' | 'Closed'
  page?: number
  pageSize?: number
}
```

`src/features/pos/services/pos.api.ts`:

```ts
import { ApiError, NetworkError, apiRequest } from '@/lib/api-client'

import type {
  CreatePosSaleRequest,
  PosOpenSession,
  PosPage,
  PosProduct,
  PosSale,
  PosSaleListItem,
  PreviewLineRequest,
  RegisterContext,
  SalePreview,
  SalesFilters,
  SessionSummary,
  SessionsFilters,
} from '../types/pos'

function posPath(tenantId: string, resource = ''): string {
  return `/api/v1/tenants/${tenantId}/pos${resource}`
}

function withQuery(path: string, params: URLSearchParams): string {
  const query = params.toString()
  return query ? `${path}?${query}` : path
}

function postJson<T>(path: string, body: unknown, headers: Record<string, string> = {}): Promise<T> {
  return apiRequest<T>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: JSON.stringify(body),
  })
}

export function fetchRegisterContext(tenantId: string): Promise<RegisterContext> {
  return apiRequest<RegisterContext>(posPath(tenantId, '/register'))
}

export function openSession(
  tenantId: string,
  body: { companyId: string | null; openingFloat: number },
): Promise<PosOpenSession> {
  return postJson<PosOpenSession>(posPath(tenantId, '/sessions'), body)
}

// The cashier closes against the count they saw: the version of GET /pos/register goes in
// If-Match (backend PosEndpoints.CloseSessionAsync; 428 without it, 412 if stale).
export function closeSession(
  tenantId: string,
  sessionId: string,
  version: number,
  body: { countedCash: number; note: string | null },
): Promise<SessionSummary> {
  return postJson<SessionSummary>(posPath(tenantId, `/sessions/${sessionId}/close`), body, {
    'If-Match': `"${version}"`,
  })
}

export function fetchSessions(tenantId: string, filters: SessionsFilters = {}): Promise<PosPage<SessionSummary>> {
  const params = new URLSearchParams()
  if (filters.from) params.set('from', filters.from)
  if (filters.to) params.set('to', filters.to)
  if (filters.status) params.set('status', filters.status)
  if (filters.page) params.set('page', String(filters.page))
  if (filters.pageSize) params.set('pageSize', String(filters.pageSize))
  return apiRequest<PosPage<SessionSummary>>(withQuery(posPath(tenantId, '/sessions'), params))
}

export function fetchSession(tenantId: string, sessionId: string): Promise<SessionSummary> {
  return apiRequest<SessionSummary>(posPath(tenantId, `/sessions/${sessionId}`))
}

export function searchProducts(
  tenantId: string,
  search: string,
  page = 1,
  pageSize = 40,
): Promise<PosPage<PosProduct>> {
  const params = new URLSearchParams()
  if (search.trim()) params.set('search', search.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return apiRequest<PosPage<PosProduct>>(withQuery(posPath(tenantId, '/products'), params))
}

export function fetchProductByCode(tenantId: string, code: string): Promise<PosProduct> {
  return apiRequest<PosProduct>(withQuery(posPath(tenantId, '/products/by-code'), new URLSearchParams({ code })))
}

export function previewSale(tenantId: string, lines: PreviewLineRequest[]): Promise<SalePreview> {
  return postJson<SalePreview>(posPath(tenantId, '/sales/preview'), { lines })
}

// 201 the first time, 200 when the server recognizes a repeat: same body either way.
export function createSale(tenantId: string, body: CreatePosSaleRequest): Promise<PosSale> {
  return postJson<PosSale>(posPath(tenantId, '/sales'), body)
}

export function fetchSale(tenantId: string, saleId: string): Promise<PosSale> {
  return apiRequest<PosSale>(posPath(tenantId, `/sales/${saleId}`))
}

export function fetchSales(tenantId: string, filters: SalesFilters = {}): Promise<PosPage<PosSaleListItem>> {
  const params = new URLSearchParams()
  if (filters.sessionId) params.set('sessionId', filters.sessionId)
  if (filters.from) params.set('from', filters.from)
  if (filters.to) params.set('to', filters.to)
  if (filters.status) params.set('status', filters.status)
  if (filters.number) params.set('number', filters.number)
  if (filters.page) params.set('page', String(filters.page))
  if (filters.pageSize) params.set('pageSize', String(filters.pageSize))
  return apiRequest<PosPage<PosSaleListItem>>(withQuery(posPath(tenantId, '/sales'), params))
}

export function voidSale(tenantId: string, saleId: string, reason: string): Promise<PosSale> {
  return postJson<PosSale>(posPath(tenantId, `/sales/${saleId}/void`), { reason })
}

// One message per backend code (Modules.Pos: PosDomainException, PosNotFound, PosAuthorization).
export const POS_CODE_MESSAGES: Record<string, string> = {
  'pos.session.already_open': 'Ya tienes una caja abierta.',
  'pos.session.not_open': 'Tu caja ya no está abierta. Ábrela de nuevo; el carrito sigue intacto.',
  'pos.session.not_found': 'Esa caja no existe.',
  'pos.session.company_required': 'Elige la empresa emisora.',
  'pos.session.company_not_found': 'La empresa elegida no existe.',
  'pos.session.company_inactive': 'La empresa elegida está desactivada.',
  'pos.session.no_active_company':
    'Todavía no hay ninguna empresa emisora activa. Pídele a un administrador que active una en Empresas.',
  'pos.session.opening_float_invalid': 'La base debe estar entre $0 y $100.000.000, con máximo 2 decimales.',
  'pos.session.counted_cash_invalid':
    'El efectivo contado debe estar entre $0 y $1.000.000.000, con máximo 2 decimales.',
  'pos.sale.id_conflict': 'No pudimos registrar este cobro. Vuelve a cobrar.',
  'pos.sale.not_found': 'Esa venta no existe.',
  'pos.sale.session_mismatch': 'La caja cambió. Abre tu caja de nuevo; el carrito sigue intacto.',
  'pos.sale.lines_required': 'El carrito está vacío.',
  'pos.sale.too_many_lines': 'Una venta admite hasta 200 productos.',
  'pos.sale.quantity_invalid': 'Revisa las cantidades: hasta 99.999 y máximo 2 decimales.',
  'pos.sale.discount_out_of_range': 'El descuento va de 0 a 100 %, con máximo 2 decimales.',
  'pos.sale.discount_not_allowed': 'No tienes permiso para dar descuentos ni cobrar ventas en $0.',
  'pos.sale.product_not_found': 'Uno de los productos ya no existe. Quítalo del carrito.',
  'pos.sale.product_inactive': 'Uno de los productos está inactivo. Quítalo del carrito.',
  'pos.sale.product_price_unavailable': 'Uno de los productos no tiene precio en pesos. Quítalo del carrito.',
  'pos.sale.price_changed': 'El precio o el IVA de un producto cambió. Revisa el carrito y vuelve a cobrar.',
  'pos.sale.payment_required': 'Agrega al menos un medio de pago.',
  'pos.sale.too_many_payments': 'Una venta admite hasta 5 medios de pago.',
  'pos.sale.duplicate_cash_payment': 'Usa una sola línea de efectivo.',
  'pos.sale.payment_amount_invalid': 'Revisa el monto de tarjeta o transferencia.',
  'pos.sale.tendered_only_for_cash': 'Sólo el efectivo lleva valor recibido.',
  'pos.sale.tendered_invalid': 'Revisa el efectivo recibido.',
  'pos.sale.cash_amount_not_allowed': 'Revisa el efectivo recibido.',
  'pos.sale.payment_exceeds_total': 'La tarjeta o la transferencia no pueden pasar del total.',
  'pos.sale.payment_insufficient': 'Los pagos no cubren el total.',
  'pos.sale.cash_payment_unneeded': 'Los otros medios ya cubren el total; quita el efectivo.',
  'pos.sale.already_voided': 'Esta venta ya estaba anulada.',
  'pos.sale.void_session_closed': 'La caja de esta venta ya se cerró; no se puede anular.',
  'pos.product.not_found': 'No hay un producto con ese código.',
  'concurrency.conflict': 'Entraron movimientos mientras tanto. Revisa y vuelve a intentar.',
  'authorization.denied': 'No tienes permiso para hacer esto.',
  'validation.failed': 'Revisa los datos e intenta de nuevo.',
}

export function describePosFailure(error: unknown): string {
  if (error instanceof NetworkError) {
    return 'No pudimos conectarnos. Revisa tu conexión e intenta de nuevo.'
  }

  const message = error instanceof ApiError && error.code ? POS_CODE_MESSAGES[error.code] : undefined
  return message ?? 'Algo salió mal. Intenta de nuevo.'
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/services/pos.api.test.ts
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/types/pos.ts src/features/pos/services/pos.api.ts src/features/pos/services/pos.api.test.ts; git commit -m "feat(pos): tipos y servicio del punto de venta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: PASS (5 pruebas), lint y prettier limpios, sin salida en la última línea.

---

### Task F2: Aritmética de dinero en centavos, formato y billetes rápidos

**Files:**
- Create: `src/features/pos/utils/money-cents.ts`, `src/features/pos/utils/format-pos-money.ts`, `src/features/pos/utils/quick-cash-amounts.ts`
- Test: `src/features/pos/utils/money-cents.test.ts`, `src/features/pos/utils/format-pos-money.test.ts`, `src/features/pos/utils/quick-cash-amounts.test.ts`

**Interfaces:**
- Produces: `toCents(value: number): number`, `fromCents(cents: number): number`, `addQuantity(quantity: number, deltaUnits: number): number`, `parseDecimalInput(text: string): number | null`, `hasAtMostTwoDecimals(value: number): boolean`; `formatPosMoney(value: number): string`; `quickCashAmounts(dueCents: number): number[]` (centavos).

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/utils/money-cents.test.ts`:

```ts
import { addQuantity, fromCents, hasAtMostTwoDecimals, parseDecimalInput, toCents } from './money-cents'

describe('money-cents', () => {
  it('adds in integer cents so 0.1 + 0.2 is exactly 0.3', () => {
    expect(toCents(0.1) + toCents(0.2)).toBe(toCents(0.3))
    expect(fromCents(toCents(37890) - toCents(20000))).toBe(17890)
  })

  it('steps quantities by whole units without float noise', () => {
    expect(addQuantity(1.1, 1)).toBe(2.1)
    expect(addQuantity(2.1, -1)).toBe(1.1)
    expect(addQuantity(0.29, -1)).toBeLessThanOrEqual(0)
  })

  it('parses comma or dot decimals with at most two places', () => {
    expect(parseDecimalInput('1,5')).toBe(1.5)
    expect(parseDecimalInput('20.000')).toBeNull()
    expect(parseDecimalInput('17890,30')).toBe(17890.3)
    expect(parseDecimalInput('0,005')).toBeNull()
    expect(parseDecimalInput('abc')).toBeNull()
    expect(parseDecimalInput('')).toBeNull()
  })

  it('knows when a number has more than two decimals', () => {
    expect(hasAtMostTwoDecimals(209.3)).toBe(true)
    expect(hasAtMostTwoDecimals(1.005)).toBe(false)
  })
})
```

`src/features/pos/utils/format-pos-money.test.ts`:

```ts
import { formatPosMoney } from './format-pos-money'

// Intl puts a non-breaking space (U+00A0) between the symbol and the number.
const NBSP = '\u00a0'

describe('formatPosMoney', () => {
  it('shows cents only when they are not zero', () => {
    expect(formatPosMoney(37890)).toBe(`$${NBSP}37.890`)
    expect(formatPosMoney(209.3)).toBe(`$${NBSP}209,30`)
    expect(formatPosMoney(0.5)).toBe(`$${NBSP}0,50`)
    expect(formatPosMoney(17890.0000001)).toBe(`$${NBSP}17.890`)
  })
})
```

`src/features/pos/utils/quick-cash-amounts.test.ts`:

```ts
import { fromCents, toCents } from './money-cents'
import { quickCashAmounts } from './quick-cash-amounts'

const pesos = (due: number) => quickCashAmounts(toCents(due)).map(fromCents)

describe('quickCashAmounts', () => {
  it('offers exact and the next 1.000, 10.000 and 50.000 above the due amount', () => {
    expect(pesos(17890)).toEqual([17890, 18000, 20000, 50000])
  })

  it('never repeats a button and adds 100.000 when there is room', () => {
    expect(pesos(20000)).toEqual([20000, 50000, 100000])
    expect(pesos(209.3)).toEqual([209.3, 1000, 10000, 50000])
  })

  it('offers nothing when nothing is due', () => {
    expect(quickCashAmounts(0)).toEqual([])
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils
```

Esperado: FAIL, los tres módulos no existen.

- [ ] **Step 3: Implementar**

`src/features/pos/utils/money-cents.ts`:

```ts
// Every sum, difference or comparison of money the client does runs in integer cents: in
// `number`, 0.1 + 0.2 !== 0.3, and a "Falta por cubrir $0,00" that is really 0.0000001 would keep
// "Confirmar venta" disabled forever (spec 2026-10-07, «Aritmética de dinero en centavos»).

export function toCents(value: number): number {
  return Math.round(value * 100)
}

export function fromCents(cents: number): number {
  return cents / 100
}

export function hasAtMostTwoDecimals(value: number): boolean {
  return Math.abs(value * 100 - Math.round(value * 100)) < 1e-6
}

// Quantities use the same scale as money (2 decimals): 1,1 + 1 is 2,1, not 2.1000000000000001.
export function addQuantity(quantity: number, deltaUnits: number): number {
  return fromCents(toCents(quantity) + deltaUnits * 100)
}

// Accepts "1,5" and "1.5". A dot followed by exactly three digits is read as a thousands
// separator typed by habit ("20.000") and rejected instead of silently becoming 20.
export function parseDecimalInput(text: string): number | null {
  const trimmed = text.trim()
  if (!/^\d+([.,]\d{1,2})?$/.test(trimmed)) {
    return null
  }

  const value = Number(trimmed.replace(',', '.'))
  return Number.isFinite(value) ? value : null
}
```

`src/features/pos/utils/format-pos-money.ts`:

```ts
const FORMATTERS = {
  0: new Intl.NumberFormat('es-CO', {
    style: 'currency',
    currency: 'COP',
    minimumFractionDigits: 0,
    maximumFractionDigits: 0,
  }),
  2: new Intl.NumberFormat('es-CO', {
    style: 'currency',
    currency: 'COP',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }),
} as const

// POS shows cents only when they exist, so the change never lies (209,30 and never 209,3).
// lib/format-currency.ts stays as is: quotes keep showing no cents.
export function formatPosMoney(value: number): string {
  const cents = Math.round(value * 100)
  return FORMATTERS[cents % 100 === 0 ? 0 : 2].format(cents / 100)
}
```

`src/features/pos/utils/quick-cash-amounts.ts`:

```ts
const THOUSAND = 100_000 // 1.000 pesos in cents
const TEN_THOUSAND = 1_000_000
const FIFTY_THOUSAND = 5_000_000
const HUNDRED_THOUSAND = 10_000_000
const MAX_BUTTONS = 4

function ceilTo(cents: number, step: number): number {
  return Math.ceil(cents / step) * step
}

// "Exacto", then the next multiples of 1.000, 10.000 and 50.000 at or above the due amount, plus
// 100.000 when the due amount is smaller; no repeats, at most four buttons. All in cents.
export function quickCashAmounts(dueCents: number): number[] {
  if (dueCents <= 0) {
    return []
  }

  const candidates = [
    dueCents,
    ceilTo(dueCents, THOUSAND),
    ceilTo(dueCents, TEN_THOUSAND),
    ceilTo(dueCents, FIFTY_THOUSAND),
  ]
  if (dueCents < HUNDRED_THOUSAND) {
    candidates.push(HUNDRED_THOUSAND)
  }

  return [...new Set(candidates)].slice(0, MAX_BUTTONS)
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/utils; git commit -m "feat(pos): aritmética de dinero en centavos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F3: Armado de pagos del cobro

**Files:**
- Create: `src/features/pos/utils/build-payments.ts`
- Test: `src/features/pos/utils/build-payments.test.ts`

**Interfaces:**
- Consumes: `toCents`, `fromCents` (F2); `PaymentMethod`, `PaymentRequest` (F1).
- Produces:
  - `type PaymentLine = { key: string; method: 'Cash'; tendered: number | null } | { key: string; method: 'Card' | 'Transfer'; amount: number | null; reference: string }`
  - `initialPaymentLines(): PaymentLine[]` (una línea Efectivo vacía)
  - `replaceSingleLineMethod(lines, method, totalCents): PaymentLine[]`
  - `addPaymentLine(lines, method, totalCents): PaymentLine[]`, `removePaymentLine(lines, key): PaymentLine[]`, `updatePaymentLine(lines, key, patch): PaymentLine[]`
  - `paymentSummary(lines, totalCents): { nonCashCents: number; cashDueCents: number; missingCents: number; changeCents: number; covers: boolean; exceedsTotal: boolean }`
  - `buildPaymentsBody(lines, totalCents): PaymentRequest[]`

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/pos/utils/build-payments.test.ts`:

```ts
import {
  addPaymentLine,
  buildPaymentsBody,
  initialPaymentLines,
  paymentSummary,
  replaceSingleLineMethod,
  updatePaymentLine,
  type PaymentLine,
} from './build-payments'

const TOTAL = 3_789_000 // $37.890 in cents

describe('build-payments', () => {
  it('starts with one empty cash line', () => {
    expect(initialPaymentLines()).toEqual([expect.objectContaining({ method: 'Cash', tendered: null })])
  })

  it('a method button replaces the only line: card takes the whole total and drops cash', () => {
    const card = replaceSingleLineMethod(initialPaymentLines(), 'Card', TOTAL)

    expect(card).toEqual([expect.objectContaining({ method: 'Card', amount: 37890, reference: '' })])
    expect(buildPaymentsBody(card, TOTAL)).toEqual([{ method: 'Card', amount: 37890, reference: null }])
  })

  it('transfer only sends only the transfer', () => {
    const transfer = replaceSingleLineMethod(initialPaymentLines(), 'Transfer', TOTAL)

    expect(buildPaymentsBody(transfer, TOTAL)).toEqual([{ method: 'Transfer', amount: 37890, reference: null }])
  })

  it('an empty cash line beside a card that covers the total never travels', () => {
    let lines = replaceSingleLineMethod(initialPaymentLines(), 'Card', TOTAL)
    lines = addPaymentLine(lines, 'Cash', TOTAL)

    expect(buildPaymentsBody(lines, TOTAL)).toEqual([{ method: 'Card', amount: 37890, reference: null }])
  })

  it('a split payment sends tendered for cash and never amount', () => {
    let lines: PaymentLine[] = replaceSingleLineMethod(initialPaymentLines(), 'Card', TOTAL)
    const cardKey = lines[0].key
    lines = updatePaymentLine(lines, cardKey, { amount: 20000, reference: ' 1234 ' })
    lines = addPaymentLine(lines, 'Cash', TOTAL)
    lines = updatePaymentLine(lines, lines[1].key, { tendered: 20000 })

    const body = buildPaymentsBody(lines, TOTAL)
    const summary = paymentSummary(lines, TOTAL)

    expect(body).toEqual([
      { method: 'Card', amount: 20000, reference: '1234' },
      { method: 'Cash', tendered: 20000 },
    ])
    expect(body[1]).not.toHaveProperty('amount')
    expect(summary).toMatchObject({ cashDueCents: 1_789_000, missingCents: 0, changeCents: 211_000, covers: true })
  })

  it('a zero total sends a single cash of zero', () => {
    expect(buildPaymentsBody(initialPaymentLines(), 0)).toEqual([{ method: 'Cash', tendered: 0 }])
    expect(paymentSummary(initialPaymentLines(), 0).covers).toBe(true)
  })

  it('computes what is missing in cents and blocks a card above the total', () => {
    const cash = initialPaymentLines()
    const shortLines = updatePaymentLine(cash, cash[0].key, { tendered: 37889.99 })
    let tooMuch = replaceSingleLineMethod(initialPaymentLines(), 'Card', TOTAL)
    tooMuch = updatePaymentLine(tooMuch, tooMuch[0].key, { amount: 40000 })

    expect(paymentSummary(shortLines, TOTAL)).toMatchObject({ missingCents: 1, covers: false })
    expect(paymentSummary(tooMuch, TOTAL)).toMatchObject({ exceedsTotal: true, covers: false })
  })

  it('allows at most five lines and a single cash line', () => {
    let lines = replaceSingleLineMethod(initialPaymentLines(), 'Card', TOTAL)
    lines = addPaymentLine(lines, 'Cash', TOTAL)
    lines = addPaymentLine(lines, 'Cash', TOTAL)
    for (let index = 0; index < 5; index++) lines = addPaymentLine(lines, 'Transfer', TOTAL)

    expect(lines.filter((line) => line.method === 'Cash')).toHaveLength(1)
    expect(lines).toHaveLength(5)
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils/build-payments.test.ts
```

Esperado: FAIL, el módulo no existe.

- [ ] **Step 3: Implementar**

`src/features/pos/utils/build-payments.ts`:

```ts
import type { PaymentMethod, PaymentRequest } from '../types/pos'

import { fromCents, toCents } from './money-cents'

export type PaymentLine =
  | { key: string; method: 'Cash'; tendered: number | null }
  | { key: string; method: 'Card' | 'Transfer'; amount: number | null; reference: string }

const MAX_PAYMENT_LINES = 5

function newLine(method: PaymentMethod, amountCents: number): PaymentLine {
  const key = crypto.randomUUID()
  return method === 'Cash'
    ? { key, method, tendered: null }
    : { key, method, amount: fromCents(amountCents), reference: '' }
}

function nonCashCents(lines: PaymentLine[]): number {
  return lines.reduce((sum, line) => (line.method === 'Cash' ? sum : sum + toCents(line.amount ?? 0)), 0)
}

export function initialPaymentLines(): PaymentLine[] {
  return [newLine('Cash', 0)]
}

// With a single line, Efectivo / Tarjeta / Transferencia replace it: "Tarjeta" leaves one Card line
// for the whole total and no cash (spec, «Diálogo de cobro»).
export function replaceSingleLineMethod(lines: PaymentLine[], method: PaymentMethod, totalCents: number): PaymentLine[] {
  return lines.length === 1 ? [newLine(method, totalCents)] : lines
}

// "+ Otro medio de pago": up to five lines, a single cash line. A non-cash line starts with what is
// still missing.
export function addPaymentLine(lines: PaymentLine[], method: PaymentMethod, totalCents: number): PaymentLine[] {
  if (lines.length >= MAX_PAYMENT_LINES) return lines
  if (method === 'Cash' && lines.some((line) => line.method === 'Cash')) return lines
  const remaining = Math.max(0, totalCents - nonCashCents(lines))
  return [...lines, newLine(method, remaining)]
}

export function removePaymentLine(lines: PaymentLine[], key: string): PaymentLine[] {
  const next = lines.filter((line) => line.key !== key)
  return next.length === 0 ? initialPaymentLines() : next
}

export function updatePaymentLine(
  lines: PaymentLine[],
  key: string,
  patch: { tendered?: number | null; amount?: number | null; reference?: string },
): PaymentLine[] {
  return lines.map((line) => {
    if (line.key !== key) return line
    if (line.method === 'Cash') {
      return patch.tendered === undefined ? line : { ...line, tendered: patch.tendered }
    }
    return {
      ...line,
      amount: patch.amount === undefined ? line.amount : patch.amount,
      reference: patch.reference === undefined ? line.reference : patch.reference,
    }
  })
}

export function paymentSummary(lines: PaymentLine[], totalCents: number) {
  const nonCash = nonCashCents(lines)
  const cash = lines.find((line) => line.method === 'Cash')
  const tenderedCents = cash?.method === 'Cash' ? toCents(cash.tendered ?? 0) : 0
  const cashDueCents = Math.max(0, totalCents - nonCash)
  const exceedsTotal = nonCash > totalCents
  const missingCents = Math.max(0, cashDueCents - tenderedCents)
  const changeCents = cashDueCents > 0 ? Math.max(0, tenderedCents - cashDueCents) : 0
  const covers = totalCents === 0 || (!exceedsTotal && missingCents === 0)
  return { nonCashCents: nonCash, cashDueCents, missingCents, changeCents, covers, exceedsTotal }
}

// The cash line is omitted when the other lines already cover the total (else the server answers
// pos.sale.cash_payment_unneeded), except with a zero total, which travels as Cash tendered 0.
// Cash carries `tendered` and never `amount`.
export function buildPaymentsBody(lines: PaymentLine[], totalCents: number): PaymentRequest[] {
  if (totalCents === 0) {
    return [{ method: 'Cash', tendered: 0 }]
  }

  const { cashDueCents } = paymentSummary(lines, totalCents)
  return lines.flatMap<PaymentRequest>((line) => {
    if (line.method === 'Cash') {
      return cashDueCents > 0 ? [{ method: 'Cash', tendered: fromCents(toCents(line.tendered ?? 0)) }] : []
    }
    return [
      {
        method: line.method,
        amount: fromCents(toCents(line.amount ?? 0)),
        reference: line.reference.trim() === '' ? null : line.reference.trim(),
      },
    ]
  })
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils/build-payments.test.ts
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/utils/build-payments.ts src/features/pos/utils/build-payments.test.ts; git commit -m "feat(pos): armado de pagos del cobro"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F4: Clasificación del resultado del cobro

**Files:**
- Create: `src/features/pos/utils/sale-attempt-outcome.ts`
- Test: `src/features/pos/utils/sale-attempt-outcome.test.ts`

**Interfaces:**
- Consumes: `ApiError`, `UnauthorizedError`, `NetworkError` (`@/lib/api-client`).
- Produces: `type AttemptOutcome = 'success' | 'definitive' | 'uncertain'`; `type CreateSaleResult = { kind: 'ok' } | { kind: 'error'; error: unknown }`; `classifyCreateSaleOutcome(result: CreateSaleResult, wasUncertain: boolean): AttemptOutcome`.

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/pos/utils/sale-attempt-outcome.test.ts`:

```ts
import { ApiError, NetworkError, UnauthorizedError } from '@/lib/api-client'

import { classifyCreateSaleOutcome } from './sale-attempt-outcome'

const fail = (error: unknown) => ({ kind: 'error' as const, error })
const api = (status: number, code?: string) => fail(new ApiError(status, code ? { code } : {}))

describe('classifyCreateSaleOutcome', () => {
  it('a 2xx is a success on any send', () => {
    expect(classifyCreateSaleOutcome({ kind: 'ok' }, false)).toBe('success')
    expect(classifyCreateSaleOutcome({ kind: 'ok' }, true)).toBe('success')
  })

  it('on the first send every listed 4xx is definitive', () => {
    expect(classifyCreateSaleOutcome(api(400), false)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(403, 'authorization.denied'), false)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(403, 'pos.sale.discount_not_allowed'), false)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(404), false)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(422, 'validation.failed'), false)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(422, 'pos.sale.price_changed'), false)).toBe('definitive')
  })

  // Spec 2026-10-07, ronda 4: authorization.denied, tenancy 403s and validation.failed come from
  // step 1, before the server looks for the repeat, so they prove nothing about the first send.
  it('retrying an uncertain attempt only trusts answers from step 2 on', () => {
    expect(classifyCreateSaleOutcome(api(403, 'authorization.denied'), true)).toBe('uncertain')
    expect(classifyCreateSaleOutcome(api(403, 'tenancy.module_not_enabled'), true)).toBe('uncertain')
    expect(classifyCreateSaleOutcome(api(422, 'validation.failed'), true)).toBe('uncertain')
    expect(classifyCreateSaleOutcome(api(400), true)).toBe('uncertain')
    expect(classifyCreateSaleOutcome(api(404), true)).toBe('uncertain')
    expect(classifyCreateSaleOutcome(api(403, 'pos.sale.discount_not_allowed'), true)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(422, 'pos.sale.session_mismatch'), true)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(422, 'pos.sale.id_conflict'), true)).toBe('definitive')
    expect(classifyCreateSaleOutcome(api(422, 'pos.sale.price_changed'), true)).toBe('definitive')
  })

  it('401, 412, 5xx, a network error and any unlisted status stay uncertain', () => {
    for (const wasUncertain of [false, true]) {
      expect(classifyCreateSaleOutcome(fail(new UnauthorizedError()), wasUncertain)).toBe('uncertain')
      expect(classifyCreateSaleOutcome(api(412, 'concurrency.conflict'), wasUncertain)).toBe('uncertain')
      expect(classifyCreateSaleOutcome(api(500), wasUncertain)).toBe('uncertain')
      expect(classifyCreateSaleOutcome(api(409), wasUncertain)).toBe('uncertain')
      expect(classifyCreateSaleOutcome(fail(new NetworkError(new TypeError('Failed to fetch'))), wasUncertain)).toBe('uncertain')
    }
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils/sale-attempt-outcome.test.ts
```

Esperado: FAIL, el módulo no existe.

- [ ] **Step 3: Implementar**

`src/features/pos/utils/sale-attempt-outcome.ts`:

```ts
import { ApiError, UnauthorizedError } from '@/lib/api-client'

export type AttemptOutcome = 'success' | 'definitive' | 'uncertain'

export type CreateSaleResult = { kind: 'ok' } | { kind: 'error'; error: unknown }

const FIRST_SEND_DEFINITIVE_STATUSES = new Set([400, 403, 404, 422])

// A definitive answer proves nothing was stored for this id, so the store may drop the id and
// unlock the cart. On the first send any listed 4xx proves it. Once an attempt was uncertain, the
// first send may have landed: only an answer the server gives *after* looking for the repeat
// (CreatePosSaleHandler step 2) proves it did not — pos.sale.discount_not_allowed (step 5) and the
// pos.* 422s (steps 2-6). authorization.denied, tenancy 403s and validation.failed come from step 1
// (spec 2026-10-07, «Ciclo de vida del id»).
export function classifyCreateSaleOutcome(result: CreateSaleResult, wasUncertain: boolean): AttemptOutcome {
  if (result.kind === 'ok') {
    return 'success'
  }

  const { error } = result
  if (!(error instanceof ApiError) || error instanceof UnauthorizedError) {
    return 'uncertain'
  }

  const afterReplayLookup =
    (error.status === 403 && error.code === 'pos.sale.discount_not_allowed') ||
    (error.status === 422 && error.code !== undefined && error.code.startsWith('pos.'))
  if (afterReplayLookup) {
    return 'definitive'
  }

  return !wasUncertain && FIRST_SEND_DEFINITIVE_STATUSES.has(error.status) ? 'definitive' : 'uncertain'
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils/sale-attempt-outcome.test.ts
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/utils/sale-attempt-outcome.ts src/features/pos/utils/sale-attempt-outcome.test.ts; git commit -m "feat(pos): clasificación del resultado del cobro"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F5: Carrito persistido por tenant y usuario

**Files:**
- Create: `src/features/pos/stores/pos-cart-store.ts`, `src/features/pos/hooks/use-pos-cart.tsx`
- Test: `src/features/pos/stores/pos-cart-store.test.ts`, `src/features/pos/hooks/use-pos-cart.test.tsx`

**Interfaces:**
- Consumes: `addQuantity` (F2), `CreatePosSaleRequest` (F1), `useActiveTenant`, `useSession`.
- Produces:
  - `posCartStorageKey(tenantId: string, userId: string): string` → `qep.pos.cart.{tenantId}.{userId}`
  - `createPosCartStore(key: string): PosCartStore`, `getPosCartStore(tenantId: string, userId: string): PosCartStore`, `resetPosCartStoresForTests(): void`
  - `type PosCartStore = StoreApi<PosCartState>`; `PosCartLine { lineId, productId, code, name, imageUrl, quantity, discountPercentage, expectedUnitPrice, expectedTaxPercentage }`; `PosSaleAttempt { saleId, body, status: 'inFlight' | 'uncertain', wasUncertain }`; `SellableProduct { id, code, name, imageUrl, unitPrice: number, taxPercentage }`
  - Acciones de `PosCartState`: `addProduct(p, quantity?) : 'added' | 'locked' | 'limit'`, `setQuantity(lineId, q)`, `setDiscount(lineId, d)`, `removeLine(lineId)`, `selectLine(lineId | null)`, `clear()`, `refreshExpected(productId, unitPrice, taxPercentage): 'updated' | 'locked'`, `adoptSession(sessionId): boolean` (devuelve `true` si cerró un cobro abierto, para el aviso), `ensurePendingSaleId(): string`, `openPay(): boolean`, `closePay(): void`, `beginAttempt(body)`, `retryAttempt()`, `markUncertain()`, `resolveAttempt('success' | 'definitive' | 'uncertain')`
  - `isCartLocked(state): boolean`
  - `PosCartProvider({ store, children })`, `usePosCart<T>(selector: (state: PosCartState) => T): T`, `usePosCartStoreInstance(): PosCartStore`, `useResolvedPosCartStore(): PosCartStore | null` (null mientras tenant o sesión no están resueltos)

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/stores/pos-cart-store.test.ts`:

```ts
import type { CreatePosSaleRequest } from '../types/pos'

import {
  createPosCartStore,
  getPosCartStore,
  posCartStorageKey,
  resetPosCartStoresForTests,
  type SellableProduct,
} from './pos-cart-store'

const TENANT = 't-1'
const KEY_A = posCartStorageKey(TENANT, 'user-a')
const KEY_B = posCartStorageKey(TENANT, 'user-b')

const shampoo: SellableProduct = { id: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', imageUrl: null, unitPrice: 11900, taxPercentage: 19 }
const soap: SellableProduct = { id: 'p-jb', code: 'JB-03', name: 'Jabón', imageUrl: null, unitPrice: 2990, taxPercentage: 5 }

const body = (id = 'sale-1'): CreatePosSaleRequest => ({
  id,
  cashSessionId: 'session-a',
  lines: [{ productId: 'p-sh', quantity: 1, discountPercentage: 0, expectedUnitPrice: 11900, expectedTaxPercentage: 19 }],
  payments: [{ method: 'Cash', tendered: 20000 }],
})

afterEach(() => resetPosCartStoresForTests())

describe('pos-cart-store', () => {
  it('keys the cart by tenant and user', () => {
    expect(KEY_A).toBe('qep.pos.cart.t-1.user-a')
  })

  it('adds the same undiscounted product onto one line', () => {
    const store = createPosCartStore(KEY_A)

    expect(store.getState().addProduct(shampoo)).toBe('added')
    expect(store.getState().addProduct(shampoo)).toBe('added')

    expect(store.getState().lines).toHaveLength(1)
    expect(store.getState().lines[0]).toMatchObject({ quantity: 2, expectedUnitPrice: 11900, expectedTaxPercentage: 19 })
  })

  it('starts a new line when the existing one carries a discount', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().setDiscount(store.getState().lines[0].lineId, 10)

    store.getState().addProduct(shampoo)

    expect(store.getState().lines.map((line) => line.discountPercentage)).toEqual([10, 0])
  })

  it('refuses line 201 with limit', () => {
    const store = createPosCartStore(KEY_A)
    for (let index = 0; index < 200; index++) {
      store.getState().addProduct({ ...shampoo, id: `p-${index}` })
    }

    expect(store.getState().addProduct(soap)).toBe('limit')
    expect(store.getState().lines).toHaveLength(200)
  })

  it('locks every cart change while paying or with an attempt', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    const lineId = store.getState().lines[0].lineId
    store.getState().openPay()

    expect(store.getState().addProduct(soap)).toBe('locked')
    store.getState().setQuantity(lineId, 5)
    store.getState().setDiscount(lineId, 50)
    store.getState().removeLine(lineId)
    expect(store.getState().refreshExpected('p-sh', 12500, 19)).toBe('locked')

    expect(store.getState().lines).toEqual([expect.objectContaining({ quantity: 1, discountPercentage: 0, expectedUnitPrice: 11900 })])
  })

  it('keeps the pending sale id until a definitive outcome', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().openPay()
    const id = store.getState().ensurePendingSaleId()

    store.getState().closePay()
    store.getState().openPay()
    expect(store.getState().ensurePendingSaleId()).toBe(id)

    store.getState().beginAttempt(body(id))
    store.getState().resolveAttempt('definitive')
    expect(store.getState().pendingSaleId).toBeNull()
    expect(store.getState().attempt).toBeNull()
    expect(store.getState().addProduct(soap)).toBe('locked') // still paying: the dialog shows the message
    store.getState().closePay()
    expect(store.getState().addProduct(soap)).toBe('added')
  })

  it('an uncertain attempt keeps the same id and body locked; a retry remembers it was uncertain', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().openPay()
    store.getState().beginAttempt(body())

    store.getState().resolveAttempt('uncertain')
    store.getState().closePay()
    expect(store.getState().attempt).toMatchObject({ saleId: 'sale-1', status: 'uncertain', wasUncertain: true })
    expect(store.getState().payOpen).toBe(true)

    store.getState().retryAttempt()
    expect(store.getState().attempt).toMatchObject({ status: 'inFlight', wasUncertain: true, body: body() })
  })

  it('a success clears everything', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().openPay()
    store.getState().beginAttempt(body())

    store.getState().resolveAttempt('success')

    expect(store.getState()).toMatchObject({ lines: [], pendingSaleId: null, attempt: null, payOpen: false })
  })

  it('refreshExpected updates every line of the product when unlocked', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().setDiscount(store.getState().lines[0].lineId, 10)
    store.getState().addProduct(shampoo)

    expect(store.getState().refreshExpected('p-sh', 11900, 5)).toBe('updated')

    expect(store.getState().lines.map((line) => line.expectedTaxPercentage)).toEqual([5, 5])
  })

  it('adoptSession keeps the lines, drops the pending id and closes an open pay dialog', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().adoptSession('session-a')
    store.getState().openPay()
    store.getState().ensurePendingSaleId()

    const closedPay = store.getState().adoptSession('session-b')

    expect(closedPay).toBe(true)
    expect(store.getState()).toMatchObject({ sessionId: 'session-b', pendingSaleId: null, payOpen: false })
    expect(store.getState().lines).toHaveLength(1)
  })

  it('adoptSession never touches an attempt', () => {
    const store = createPosCartStore(KEY_A)
    store.getState().addProduct(shampoo)
    store.getState().openPay()
    store.getState().beginAttempt(body())
    store.getState().markUncertain()

    expect(store.getState().adoptSession('session-b')).toBe(false)
    expect(store.getState().attempt).toMatchObject({ saleId: 'sale-1' })
  })

  it('rehydrates an in-flight attempt as uncertain whatever the open session is', () => {
    const first = createPosCartStore(KEY_A)
    first.getState().addProduct(shampoo)
    first.getState().adoptSession('session-a')
    first.getState().openPay()
    first.getState().beginAttempt(body())

    const reloaded = createPosCartStore(KEY_A)

    expect(reloaded.getState().attempt).toMatchObject({ saleId: 'sale-1', status: 'uncertain', wasUncertain: true })
    expect(reloaded.getState().sessionId).toBe('session-a')
    expect(reloaded.getState().lines).toHaveLength(1)
  })

  // Spec, ronda 4, MAJOR 1 — and Review Focus 4.
  it('user B does not rehydrate the attempt of user A and never writes into its key', () => {
    const a = createPosCartStore(KEY_A)
    a.getState().addProduct(shampoo)
    a.getState().openPay()
    a.getState().beginAttempt(body())
    a.getState().markUncertain()
    const storedA = sessionStorage.getItem(KEY_A)

    const b = createPosCartStore(KEY_B)
    b.getState().addProduct(soap)

    expect(b.getState().attempt).toBeNull()
    expect(b.getState().lines.map((line) => line.code)).toEqual(['JB-03'])
    expect(sessionStorage.getItem(KEY_A)).toBe(storedA)
    expect(sessionStorage.getItem(KEY_B)).toContain('JB-03')
  })

  it('two tenants do not share a cart and getPosCartStore memoizes per key', () => {
    const one = getPosCartStore('t-1', 'user-a')
    one.getState().addProduct(shampoo)

    expect(getPosCartStore('t-1', 'user-a')).toBe(one)
    expect(getPosCartStore('t-2', 'user-a').getState().lines).toEqual([])
  })
})
```

`src/features/pos/hooks/use-pos-cart.test.tsx`:

```tsx
import { renderHook } from '@testing-library/react'

import { resetPosCartStoresForTests } from '../stores/pos-cart-store'

import { useResolvedPosCartStore } from './use-pos-cart'

const tenant = vi.hoisted(() => ({ value: 't-1' as string | null }))
const session = vi.hoisted(() => ({ value: { userId: 'user-a' } as { userId: string } | null }))

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: tenant.value }),
}))
vi.mock('@/features/auth/hooks/use-session', () => ({
  useSession: () => ({ session: session.value, status: session.value ? 'authenticated' : 'loading' }),
}))

afterEach(() => resetPosCartStoresForTests())

describe('useResolvedPosCartStore', () => {
  it('has no store until tenant and user are known', () => {
    session.value = null
    expect(renderHook(() => useResolvedPosCartStore()).result.current).toBeNull()
  })

  it('resolves the store of the signed-in user', () => {
    session.value = { userId: 'user-a' }
    tenant.value = 't-1'
    const first = renderHook(() => useResolvedPosCartStore()).result.current
    session.value = { userId: 'user-b' }
    const second = renderHook(() => useResolvedPosCartStore()).result.current

    expect(first).not.toBeNull()
    expect(second).not.toBe(first)
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/stores src/features/pos/hooks/use-pos-cart.test.tsx
```

Esperado: FAIL, los módulos no existen.

- [ ] **Step 3: Implementar**

`src/features/pos/stores/pos-cart-store.ts`:

```ts
import { createJSONStorage, persist } from 'zustand/middleware'
import { createStore, type StoreApi } from 'zustand/vanilla'

import type { CreatePosSaleRequest } from '../types/pos'
import { addQuantity } from '../utils/money-cents'

export const POS_CART_MAX_LINES = 200

export interface PosCartLine {
  lineId: string
  productId: string
  code: string
  name: string
  imageUrl: string | null
  quantity: number
  discountPercentage: number
  expectedUnitPrice: number
  expectedTaxPercentage: number
}

// The exact body that was sent and how it ended. While it is not null, cart and payment are
// locked: no setter changes `lines` or the payments, so an id never travels with two bodies.
export interface PosSaleAttempt {
  saleId: string
  body: CreatePosSaleRequest
  status: 'inFlight' | 'uncertain'
  // A send of this id already had an unknown outcome: only answers from step 2 on are definitive.
  wasUncertain: boolean
}

export interface SellableProduct {
  id: string
  code: string
  name: string
  imageUrl: string | null
  unitPrice: number
  taxPercentage: number
}

interface PosCartData {
  sessionId: string | null
  lines: PosCartLine[]
  pendingSaleId: string | null
  payOpen: boolean
  attempt: PosSaleAttempt | null
  selectedLineId: string | null
}

export interface PosCartState extends PosCartData {
  addProduct(product: SellableProduct, quantity?: number): 'added' | 'locked' | 'limit'
  setQuantity(lineId: string, quantity: number): void
  setDiscount(lineId: string, discountPercentage: number): void
  removeLine(lineId: string): void
  selectLine(lineId: string | null): void
  clear(): void
  refreshExpected(productId: string, unitPrice: number, taxPercentage: number): 'updated' | 'locked'
  adoptSession(sessionId: string): boolean
  ensurePendingSaleId(): string
  openPay(): boolean
  closePay(): void
  beginAttempt(body: CreatePosSaleRequest): void
  retryAttempt(): void
  markUncertain(): void
  resolveAttempt(outcome: 'success' | 'definitive' | 'uncertain'): void
}

export type PosCartStore = StoreApi<PosCartState>

export function isCartLocked(state: Pick<PosCartData, 'payOpen' | 'attempt'>): boolean {
  return state.payOpen || state.attempt !== null
}

// By tenant AND user: on a shared counter tab, cashier B logs in after cashier A, sessionStorage
// survives the logout, and with a tenant-only key B would rehydrate A's uncertain attempt and
// retry it under B's identity (spec 2026-10-07, «Por qué el usuario sí va en la clave»). The cash
// session is a field, not part of the key («Por qué la caja no va en la clave»).
export function posCartStorageKey(tenantId: string, userId: string): string {
  return `qep.pos.cart.${tenantId}.${userId}`
}

const EMPTY: PosCartData = {
  sessionId: null,
  lines: [],
  pendingSaleId: null,
  payOpen: false,
  attempt: null,
  selectedLineId: null,
}

// The store of a key is created only once tenant and user exist, and sessionStorage is
// synchronous, so persist hydrates during creation: nothing can write an empty state over the
// stored one before hydration (plan decision P7).
export function createPosCartStore(key: string): PosCartStore {
  return createStore<PosCartState>()(
    persist(
      (set, get) => ({
        ...EMPTY,

        addProduct(product, quantity = 1) {
          const state = get()
          if (isCartLocked(state)) return 'locked'
          const existing = state.lines.find(
            (line) => line.productId === product.id && line.discountPercentage === 0,
          )
          if (existing) {
            set({
              lines: state.lines.map((line) =>
                line.lineId === existing.lineId ? { ...line, quantity: addQuantity(line.quantity, quantity) } : line,
              ),
              selectedLineId: existing.lineId,
            })
            return 'added'
          }
          if (state.lines.length >= POS_CART_MAX_LINES) return 'limit'
          const lineId = crypto.randomUUID()
          set({
            lines: [
              ...state.lines,
              {
                lineId,
                productId: product.id,
                code: product.code,
                name: product.name,
                imageUrl: product.imageUrl,
                quantity,
                discountPercentage: 0,
                expectedUnitPrice: product.unitPrice,
                expectedTaxPercentage: product.taxPercentage,
              },
            ],
            selectedLineId: lineId,
          })
          return 'added'
        },

        setQuantity(lineId, quantity) {
          if (isCartLocked(get())) return
          set({ lines: get().lines.map((line) => (line.lineId === lineId ? { ...line, quantity } : line)) })
        },

        setDiscount(lineId, discountPercentage) {
          if (isCartLocked(get())) return
          set({ lines: get().lines.map((line) => (line.lineId === lineId ? { ...line, discountPercentage } : line)) })
        },

        removeLine(lineId) {
          if (isCartLocked(get())) return
          const lines = get().lines.filter((line) => line.lineId !== lineId)
          set({ lines, selectedLineId: get().selectedLineId === lineId ? null : get().selectedLineId })
        },

        selectLine(lineId) {
          set({ selectedLineId: lineId })
        },

        // Only a successful sale and a confirmed "Cerrar caja" call this.
        clear() {
          set({ lines: [], pendingSaleId: null, attempt: null, payOpen: false, selectedLineId: null })
        },

        refreshExpected(productId, unitPrice, taxPercentage) {
          if (isCartLocked(get())) return 'locked'
          set({
            lines: get().lines.map((line) =>
              line.productId === productId
                ? { ...line, expectedUnitPrice: unitPrice, expectedTaxPercentage: taxPercentage }
                : line,
            ),
          })
          return 'updated'
        },

        // The open cash session is no longer `sessionId`. With an attempt the screen never calls
        // this; the guard keeps it harmless if it does.
        adoptSession(sessionId) {
          const state = get()
          if (state.attempt !== null) return false
          const closedPay = state.payOpen
          set({ sessionId, pendingSaleId: null, payOpen: false })
          return closedPay
        },

        ensurePendingSaleId() {
          const current = get().pendingSaleId
          if (current) return current
          const id = crypto.randomUUID()
          set({ pendingSaleId: id })
          return id
        },

        openPay() {
          const state = get()
          if (state.lines.length === 0 || state.attempt !== null) return false
          set({ payOpen: true })
          get().ensurePendingSaleId()
          return true
        },

        // An attempt cannot be dismissed: only a definitive answer for its id unlocks it.
        closePay() {
          if (get().attempt !== null) return
          set({ payOpen: false })
        },

        beginAttempt(body) {
          set({ attempt: { saleId: body.id, body, status: 'inFlight', wasUncertain: false } })
        },

        retryAttempt() {
          const attempt = get().attempt
          if (!attempt) return
          set({ attempt: { ...attempt, status: 'inFlight' } })
        },

        markUncertain() {
          const attempt = get().attempt
          if (!attempt) return
          set({ attempt: { ...attempt, status: 'uncertain', wasUncertain: true } })
        },

        resolveAttempt(outcome) {
          if (outcome === 'success') {
            get().clear()
            return
          }
          if (outcome === 'definitive') {
            // Nothing was stored: the next charge generates another id. The dialog stays open to
            // show the message; closing it unlocks the cart.
            set({ attempt: null, pendingSaleId: null })
            return
          }
          get().markUncertain()
        },
      }),
      {
        name: key,
        storage: createJSONStorage(() => sessionStorage),
        version: 1,
        partialize: (state) => ({
          sessionId: state.sessionId,
          lines: state.lines,
          pendingSaleId: state.pendingSaleId,
          attempt: state.attempt,
        }),
        // A reload with an in-flight attempt cannot know whether it arrived: it comes back
        // uncertain, and the uncertain dialog is the first thing /pos draws.
        merge: (persisted, current) => {
          const stored = (persisted ?? {}) as Partial<PosCartData>
          const attempt =
            stored.attempt && stored.attempt.status === 'inFlight'
              ? { ...stored.attempt, status: 'uncertain' as const, wasUncertain: true }
              : (stored.attempt ?? null)
          return { ...current, ...stored, attempt, payOpen: attempt !== null }
        },
      },
    ),
  )
}

const stores = new Map<string, PosCartStore>()

export function getPosCartStore(tenantId: string, userId: string): PosCartStore {
  const key = posCartStorageKey(tenantId, userId)
  let store = stores.get(key)
  if (!store) {
    store = createPosCartStore(key)
    stores.set(key, store)
  }
  return store
}

export function resetPosCartStoresForTests(): void {
  stores.clear()
}
```

`src/features/pos/hooks/use-pos-cart.tsx`:

```tsx
import { createContext, useContext, type ReactNode } from 'react'
import { useStore } from 'zustand'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import { useSession } from '@/features/auth/hooks/use-session'

import { getPosCartStore, type PosCartState, type PosCartStore } from '../stores/pos-cart-store'

const PosCartContext = createContext<PosCartStore | null>(null)

export function PosCartProvider({ store, children }: { store: PosCartStore; children: ReactNode }) {
  return <PosCartContext.Provider value={store}>{children}</PosCartContext.Provider>
}

export function usePosCartStoreInstance(): PosCartStore {
  const store = useContext(PosCartContext)
  if (!store) throw new Error('usePosCart must be used inside <PosCartProvider>')
  return store
}

export function usePosCart<T>(selector: (state: PosCartState) => T): T {
  return useStore(usePosCartStoreInstance(), selector)
}

// Null while the tenant or the session is unresolved: the register is not drawn without its key.
export function useResolvedPosCartStore(): PosCartStore | null {
  const { tenantId } = useActiveTenant()
  const { session } = useSession()
  if (!tenantId || !session) return null
  return getPosCartStore(tenantId, session.userId)
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/stores src/features/pos/hooks/use-pos-cart.test.tsx
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/stores src/features/pos/hooks/use-pos-cart.tsx src/features/pos/hooks/use-pos-cart.test.tsx; git commit -m "feat(pos): carrito persistido por tenant y usuario"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: PASS. Si la prueba de rehidratación falla porque `persist` hidrata de forma asíncrona en esta versión de zustand, agrega `await first.persist.rehydrate()`/`await reloaded.persist.rehydrate()` en la prueba y en `getPosCartStore` usa el `onFinishHydration` para no dibujar hasta hidratar; anótalo en el handoff.

---


### Task F6: `printWithTitle` pasa a `src/lib`

**Files:**
- Move: `src/features/quotes/utils/print-with-title.ts` → `src/lib/print-with-title.ts` (con su prueba si existe)
- Modify: `src/features/orders/pages/order-detail-page.tsx:18`, `src/features/quotes/components/created-order-summary.tsx:22`
- Test: `src/lib/print-with-title.test.ts`

**Interfaces:**
- Produces: `printWithTitle(title: string): void` en `@/lib/print-with-title` (lo usan pedidos, cotizaciones y POS: tres features, Screaming Architecture `SDD-ADR-07`).

- [ ] **Step 1: Escribir la prueba que falla**

`src/lib/print-with-title.test.ts` (si `src/features/quotes/utils/print-with-title.test.ts` ya existe, muévelo con `git mv` en el Step 3 y suma sólo el caso que falte):

```ts
import { printWithTitle } from './print-with-title'

describe('printWithTitle', () => {
  it('prints under the given title and restores the previous one after printing', () => {
    document.title = 'QEP'
    const titles: string[] = []
    const print = vi.spyOn(window, 'print').mockImplementation(() => titles.push(document.title))

    printWithTitle('POS-000042')
    window.dispatchEvent(new Event('afterprint'))

    expect(print).toHaveBeenCalledOnce()
    expect(titles).toEqual(['POS-000042'])
    expect(document.title).toBe('QEP')
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/lib/print-with-title.test.ts
```

Esperado: FAIL, `Failed to resolve import "./print-with-title"`.

- [ ] **Step 3: Mover y actualizar los dos importadores**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
git mv src/features/quotes/utils/print-with-title.ts src/lib/print-with-title.ts
if (Test-Path src/features/quotes/utils/print-with-title.test.ts) { git mv src/features/quotes/utils/print-with-title.test.ts src/lib/print-with-title.existing.test.ts }
git grep -n "print-with-title" -- src
```

En `order-detail-page.tsx:18` y `created-order-summary.tsx:22`, el import pasa a `import { printWithTitle } from '@/lib/print-with-title'`. Si había una prueba previa movida a `print-with-title.existing.test.ts`, une sus casos en `print-with-title.test.ts` y borra el archivo `existing`. El `git grep` final no debe mostrar ninguna ruta `features/quotes/utils/print-with-title`.

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/lib/print-with-title.test.ts src/features/orders src/features/quotes
bun run lint
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add -u src/features/quotes/utils src/features/orders/pages/order-detail-page.tsx src/features/quotes/components/created-order-summary.tsx; git add src/lib/print-with-title.ts src/lib/print-with-title.test.ts; git commit -m "refactor(print): printWithTitle pasa a src/lib"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: PASS de la prueba nueva y de las de `orders` y `quotes` que imprimen (comparar por nombre con la línea base si alguna ya fallaba).

---

### Task F7: Hooks de datos del punto de venta

**Files:**
- Create: `src/features/pos/hooks/{use-register-context,use-sale-preview,use-create-sale,use-pos-product-search,use-product-by-code,use-open-session,use-close-session,use-void-sale,use-pos-sales,use-pos-sessions,use-pos-sale,use-pos-session}.ts`
- Test: `src/features/pos/hooks/use-register-context.test.tsx`, `src/features/pos/hooks/use-sale-preview.test.tsx`, `src/features/pos/hooks/use-create-sale.test.tsx`

**Interfaces:**
- Consumes: F1 (servicio, tipos), `useActiveTenant`, `useDebouncedValue` (`@/components/use-debounced-value`, `(value, delayMs = 400)`).
- Produces:
  - `posRegisterKey(tenantId)`, `useRegisterContext(): { context: RegisterContext | undefined; isLoading; isError; error; refetch }`
  - `posPreviewPrefix(tenantId)`, `previewLinesKey(lines): string`, `useSalePreview(lines: PreviewLineRequest[]): { preview: SalePreview | undefined; isCurrent: boolean; isError: boolean; refetch(): void }`, `invalidatePosPreview(queryClient, tenantId): Promise<void>`
  - `useCreateSale(): (body: CreatePosSaleRequest) => Promise<{ kind: 'ok'; sale: PosSale } | { kind: 'error'; error: unknown }>`; `useFetchSale(): (saleId: string) => Promise<{ kind: 'ok'; sale: PosSale } | { kind: 'error'; error: unknown }>`
  - `usePosProductSearch(search: string)`, `useProductByCodeLookup(): (code: string) => Promise<PosProduct>`
  - `useOpenSession()`, `useCloseSession()`, `useVoidSale()` (mutaciones de TanStack), `usePosSales(filters)`, `usePosSessions(filters)`, `usePosSale(id)`, `usePosSession(id)`

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/hooks/use-register-context.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'

import { useRegisterContext } from './use-register-context'

const TENANT = 't-1'
vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: TENANT }) }))

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('useRegisterContext', () => {
  it('reads the register of the active tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(
      json(200, { cashier: { memberId: 'm-1', name: 'Laura Gómez' }, session: null, companies: [], defaultCompanyId: null }),
    )

    const { result } = renderHook(() => useRegisterContext(), { wrapper })

    await waitFor(() => expect(result.current.context?.cashier.name).toBe('Laura Gómez'))
    expect(String(vi.mocked(fetch).mock.calls[0][0])).toBe('/api/v1/tenants/t-1/pos/register')
    expect(result.current.context?.session).toBeNull()
  })

  it('exposes an error state', async () => {
    vi.mocked(fetch).mockResolvedValue(json(403, { code: 'authorization.denied' }))

    const { result } = renderHook(() => useRegisterContext(), { wrapper })

    await waitFor(() => expect(result.current.isError).toBe(true))
  })
})
```

`src/features/pos/hooks/use-sale-preview.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'

import type { PreviewLineRequest } from '../types/pos'

import { previewLinesKey, useSalePreview } from './use-sale-preview'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
}

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

const preview = (total: number) => ({ lines: [], subtotal: total, taxAmount: 0, discountAmount: 0, total, zeroTotalNotAllowed: false })

describe('useSalePreview', () => {
  it('keys the preview only on productId, quantity and discount', () => {
    expect(previewLinesKey([{ productId: 'p', quantity: 2, discountPercentage: 0 }])).toBe('[["p",2,0]]')
  })

  it('asks nothing for an empty cart', async () => {
    const { result } = renderHook(() => useSalePreview([]), { wrapper })

    await new Promise((resolve) => setTimeout(resolve, 250))
    expect(fetch).not.toHaveBeenCalled()
    expect(result.current.preview).toBeUndefined()
  })

  it('debounces, keeps the previous numbers while recalculating and reports when it is current', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(preview(11900))).mockResolvedValueOnce(json(preview(23800)))
    let lines: PreviewLineRequest[] = [{ productId: 'p', quantity: 1, discountPercentage: 0 }]
    const { result, rerender } = renderHook(() => useSalePreview(lines), { wrapper })

    await waitFor(() => expect(result.current.isCurrent).toBe(true))
    expect(result.current.preview?.total).toBe(11900)

    lines = [{ productId: 'p', quantity: 2, discountPercentage: 0 }]
    rerender()
    expect(result.current.isCurrent).toBe(false)
    expect(result.current.preview?.total).toBe(11900)

    await waitFor(() => expect(result.current.preview?.total).toBe(23800))
    expect(result.current.isCurrent).toBe(true)
    expect(JSON.parse(String(vi.mocked(fetch).mock.calls[1][1]?.body))).toEqual({
      lines: [{ productId: 'p', quantity: 2, discountPercentage: 0 }],
    })
  })
})
```

`src/features/pos/hooks/use-create-sale.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'

import { ApiError } from '@/lib/api-client'

import { useCreateSale } from './use-create-sale'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
}

const body = { id: 's-1', cashSessionId: 'c-1', lines: [], payments: [] }

describe('useCreateSale', () => {
  it('never throws: it returns the sale or the error', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(new Response(JSON.stringify({ id: 's-1', saleNumber: 'POS-000001' }), { status: 201, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: 'pos.sale.price_changed' }), { status: 422, headers: { 'Content-Type': 'application/problem+json' } }))
    const { result } = renderHook(() => useCreateSale(), { wrapper })

    const ok = await result.current(body)
    const failed = await result.current(body)

    expect(ok).toMatchObject({ kind: 'ok', sale: { saleNumber: 'POS-000001' } })
    expect(failed.kind).toBe('error')
    expect(failed.kind === 'error' && failed.error instanceof ApiError && failed.error.code).toBe('pos.sale.price_changed')
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/hooks
```

Esperado: FAIL en las tres pruebas nuevas (módulos inexistentes); las de F5 siguen verdes.

- [ ] **Step 3: Implementar**

`src/features/pos/hooks/use-register-context.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchRegisterContext } from '../services/pos.api'

// Tenant in the key so another tenant's cache is never served; transactional, so no cache and a
// refetch on mount (same criterion as use-order-list). The refetch on window focus is also what
// lets the register notice a cash session closed or reopened in another tab.
export const posRegisterKey = (tenantId: string | null) => ['pos', tenantId ?? 'none', 'register'] as const

export function useRegisterContext() {
  const { tenantId } = useActiveTenant()
  const query = useQuery({
    queryKey: posRegisterKey(tenantId),
    queryFn: () => fetchRegisterContext(tenantId!),
    enabled: tenantId !== null,
    refetchOnMount: 'always',
    gcTime: 0,
    retry: false,
  })

  return {
    context: query.data,
    isLoading: tenantId === null || query.isPending,
    isError: query.isError,
    error: query.error,
    refetch: () => void query.refetch(),
  }
}
```

`src/features/pos/hooks/use-sale-preview.ts`:

```ts
import { keepPreviousData, useQuery, type QueryClient } from '@tanstack/react-query'
import { useMemo } from 'react'

import { useDebouncedValue } from '@/components/use-debounced-value'
import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { previewSale } from '../services/pos.api'
import type { PreviewLineRequest } from '../types/pos'

const PREVIEW_DEBOUNCE_MS = 200

export const posPreviewPrefix = (tenantId: string | null) => ['pos', tenantId ?? 'none', 'preview'] as const

// Only what the preview body carries: refreshExpected changes no field here, so it never
// triggers another preview (spec, «Estado del carrito»).
export function previewLinesKey(lines: PreviewLineRequest[]): string {
  return JSON.stringify(lines.map((line) => [line.productId, line.quantity, line.discountPercentage]))
}

function linesFromKey(key: string): PreviewLineRequest[] {
  return (JSON.parse(key) as [string, number, number][]).map(([productId, quantity, discountPercentage]) => ({
    productId,
    quantity,
    discountPercentage,
  }))
}

// After pos.sale.price_changed the cart did not change, so the key did not either: without an
// explicit invalidation no new preview would go out and "Cobrar" would stay disabled (ronda 4).
export function invalidatePosPreview(queryClient: QueryClient, tenantId: string | null): Promise<void> {
  return queryClient.invalidateQueries({ queryKey: posPreviewPrefix(tenantId) })
}

export function useSalePreview(lines: PreviewLineRequest[]) {
  const { tenantId } = useActiveTenant()
  const key = previewLinesKey(lines)
  const debouncedKey = useDebouncedValue(key, PREVIEW_DEBOUNCE_MS)
  const debouncedLines = useMemo(() => linesFromKey(debouncedKey), [debouncedKey])

  const query = useQuery({
    queryKey: [...posPreviewPrefix(tenantId), debouncedKey],
    queryFn: () => previewSale(tenantId!, debouncedLines),
    enabled: tenantId !== null && debouncedLines.length > 0,
    // The numbers keep the previous answer while recalculating, so they do not flicker.
    placeholderData: keepPreviousData,
    retry: false,
  })

  const isEmpty = lines.length === 0
  const isCurrent =
    !isEmpty &&
    debouncedKey === key &&
    query.data !== undefined &&
    !query.isPlaceholderData &&
    !query.isFetching &&
    !query.isError

  return {
    preview: isEmpty ? undefined : query.data,
    isCurrent,
    isError: query.isError,
    refetch: () => void query.refetch(),
  }
}
```

`src/features/pos/hooks/use-create-sale.ts`:

```ts
import { useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { createSale, fetchSale } from '../services/pos.api'
import type { CreatePosSaleRequest, PosSale } from '../types/pos'

import { posRegisterKey } from './use-register-context'

export type SaleCallResult = { kind: 'ok'; sale: PosSale } | { kind: 'error'; error: unknown }

// Not a TanStack mutation on purpose: no retries of its own. The only retry is the cashier's
// "Reintentar", with the same id and body (spec, «Diálogo de cobro»).
export function useCreateSale() {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()
  return useCallback(
    async (body: CreatePosSaleRequest): Promise<SaleCallResult> => {
      try {
        const sale = await createSale(tenantId!, body)
        void queryClient.invalidateQueries({ queryKey: posRegisterKey(tenantId) })
        return { kind: 'ok', sale }
      } catch (error) {
        return { kind: 'error', error }
      }
    },
    [tenantId, queryClient],
  )
}

// "Verificar": GET /pos/sales/{attempt.saleId}.
export function useFetchSale() {
  const { tenantId } = useActiveTenant()
  return useCallback(
    async (saleId: string): Promise<SaleCallResult> => {
      try {
        return { kind: 'ok', sale: await fetchSale(tenantId!, saleId) }
      } catch (error) {
        return { kind: 'error', error }
      }
    },
    [tenantId],
  )
}
```

`src/features/pos/hooks/use-pos-product-search.ts`:

```ts
import { keepPreviousData, useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { searchProducts } from '../services/pos.api'

const GRID_PAGE_SIZE = 40

// Catalog data may be cached 30 s: the preview revalidates price and tax anyway.
export function usePosProductSearch(search: string) {
  const { tenantId } = useActiveTenant()
  const query = useQuery({
    queryKey: ['pos', tenantId ?? 'none', 'products', search.trim()],
    queryFn: () => searchProducts(tenantId!, search, 1, GRID_PAGE_SIZE),
    enabled: tenantId !== null,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
    retry: false,
  })
  return {
    products: query.data?.items,
    isLoading: query.isPending,
    isError: query.isError,
    refetch: () => void query.refetch(),
  }
}
```

`src/features/pos/hooks/use-product-by-code.ts`:

```ts
import { useCallback } from 'react'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchProductByCode } from '../services/pos.api'
import type { PosProduct } from '../types/pos'

// Imperative on purpose: each scan is one request, resolved by the scan queue in arrival order.
export function useProductByCodeLookup(): (code: string) => Promise<PosProduct> {
  const { tenantId } = useActiveTenant()
  return useCallback((code: string) => fetchProductByCode(tenantId!, code), [tenantId])
}
```

`src/features/pos/hooks/use-open-session.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { openSession } from '../services/pos.api'

import { posRegisterKey } from './use-register-context'

export function useOpenSession() {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { companyId: string | null; openingFloat: number }) => openSession(tenantId!, body),
    // already_open (another tab) also reloads the register, which then shows the open one.
    onSettled: () => queryClient.invalidateQueries({ queryKey: posRegisterKey(tenantId) }),
  })
}
```

`src/features/pos/hooks/use-close-session.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { closeSession } from '../services/pos.api'

import { posRegisterKey } from './use-register-context'

export function useCloseSession() {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { sessionId: string; version: number; countedCash: number; note: string | null }) =>
      closeSession(tenantId!, input.sessionId, input.version, { countedCash: input.countedCash, note: input.note }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: posRegisterKey(tenantId) }),
  })
}
```

`src/features/pos/hooks/use-void-sale.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { voidSale } from '../services/pos.api'

export function useVoidSale() {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { saleId: string; reason: string }) => voidSale(tenantId!, input.saleId, input.reason),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['pos', tenantId ?? 'none'] }),
  })
}
```

`src/features/pos/hooks/use-pos-sales.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchSales } from '../services/pos.api'
import type { SalesFilters } from '../types/pos'

export function usePosSales(filters: SalesFilters) {
  const { tenantId } = useActiveTenant()
  const query = useQuery({
    queryKey: ['pos', tenantId ?? 'none', 'sales', filters],
    queryFn: () => fetchSales(tenantId!, filters),
    enabled: tenantId !== null,
    refetchOnMount: 'always',
    gcTime: 0,
    retry: false,
  })
  return { page: query.data, isLoading: tenantId === null || query.isPending, isError: query.isError, refetch: () => void query.refetch() }
}
```

`src/features/pos/hooks/use-pos-sessions.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchSessions } from '../services/pos.api'
import type { SessionsFilters } from '../types/pos'

export function usePosSessions(filters: SessionsFilters) {
  const { tenantId } = useActiveTenant()
  const query = useQuery({
    queryKey: ['pos', tenantId ?? 'none', 'sessions', filters],
    queryFn: () => fetchSessions(tenantId!, filters),
    enabled: tenantId !== null,
    refetchOnMount: 'always',
    gcTime: 0,
    retry: false,
  })
  return { page: query.data, isLoading: tenantId === null || query.isPending, isError: query.isError, refetch: () => void query.refetch() }
}
```

`src/features/pos/hooks/use-pos-sale.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchSale } from '../services/pos.api'

export function usePosSale(saleId: string | null) {
  const { tenantId } = useActiveTenant()
  return useQuery({
    queryKey: ['pos', tenantId ?? 'none', 'sale', saleId],
    queryFn: () => fetchSale(tenantId!, saleId!),
    enabled: tenantId !== null && saleId !== null,
    gcTime: 0,
    retry: false,
  })
}
```

`src/features/pos/hooks/use-pos-session.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'

import { fetchSession } from '../services/pos.api'

export function usePosSession(sessionId: string | null) {
  const { tenantId } = useActiveTenant()
  return useQuery({
    queryKey: ['pos', tenantId ?? 'none', 'session', sessionId],
    queryFn: () => fetchSession(tenantId!, sessionId!),
    enabled: tenantId !== null && sessionId !== null,
    gcTime: 0,
    retry: false,
  })
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/hooks
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/hooks; git commit -m "feat(pos): hooks de datos del punto de venta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F8: Cola de escaneo, guardia de ráfagas, input de dinero y atajos

**Files:**
- Create: `src/features/pos/hooks/use-scan-queue.ts`, `src/features/pos/hooks/use-scanner-burst-guard.ts`, `src/features/pos/hooks/use-pos-shortcuts.ts`, `src/features/pos/components/pos-money-input.tsx`
- Test: `src/features/pos/hooks/use-scan-queue.test.tsx`, `src/features/pos/components/pos-money-input.test.tsx`, `src/features/pos/hooks/use-pos-shortcuts.test.tsx`

**Interfaces:**
- Consumes: `ApiError` (`@/lib/api-client`), `PosProduct` (F1), `parseDecimalInput` (F2).
- Produces:
  - `type ScanResult = { code: string; kind: 'found'; product: PosProduct } | { code: string; kind: 'not-found' } | { code: string; kind: 'error'; error: unknown }`; `useScanQueue(lookup: (code: string) => Promise<PosProduct>, onResult: (result: ScanResult) => void): { enqueue(code: string): void; pending: number }`
  - `BURST_MESSAGE`; `useScannerBurstGuard(options: { onBurst(): void; now?: () => number }): { onKeyDown(event): boolean; onPaste(): void }` (`true` = era una ráfaga y se tragó el Enter)
  - `PosMoneyInput(props: { id: string; label: string; value: number | null; onChange(value: number | null): void; onConfirm?(): void; disabled?: boolean; max?: number; autoFocus?: boolean; now?: () => number })` (decisión P12)
  - `usePosShortcuts(handlers: PosShortcutHandlers, cartListRef: RefObject<HTMLElement | null>, enabled?: boolean)`; `PosShortcutHandlers { focusSearch(); openPay(); printLast?(); escape(); moveSelection(delta: 1 | -1); changeQuantity(delta: 1 | -1); removeSelected() }`

**Decisión P12 (de este plan):** todos los inputs de dinero del POS (recibido, monto de tarjeta o transferencia, base y efectivo contado) son `PosMoneyInput`, no `CurrencyInput`: el guardia de ráfagas necesita el `keydown` y el `paste` del input, y el compartido no los expone. Se anota en el handoff.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/hooks/use-scan-queue.test.tsx`:

```tsx
import { act, renderHook, waitFor } from '@testing-library/react'

import { ApiError } from '@/lib/api-client'

import type { PosProduct } from '../types/pos'

import { useScanQueue, type ScanResult } from './use-scan-queue'

const product = (code: string): PosProduct => ({
  id: `id-${code}`, code, name: code, unitPrice: 1000, taxPercentage: 0, imageUrl: null, sellable: true, unsellableReason: null,
})

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

describe('useScanQueue', () => {
  it('delivers results in scan order even when the second answer arrives first', async () => {
    const first = deferred<PosProduct>()
    const second = deferred<PosProduct>()
    const lookup = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const results: ScanResult[] = []
    const { result } = renderHook(() => useScanQueue(lookup, (scan) => results.push(scan)))

    act(() => {
      result.current.enqueue('SH-400')
      result.current.enqueue('JB-03')
    })
    expect(result.current.pending).toBe(2)
    expect(lookup).toHaveBeenNthCalledWith(1, 'SH-400')
    expect(lookup).toHaveBeenNthCalledWith(2, 'JB-03')

    await act(async () => second.resolve(product('JB-03')))
    expect(results).toEqual([])
    await act(async () => first.resolve(product('SH-400')))

    await waitFor(() => expect(result.current.pending).toBe(0))
    expect(results.map((scan) => scan.code)).toEqual(['SH-400', 'JB-03'])
  })

  it('turns a 404 into not-found and anything else into an error', async () => {
    const lookup = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(404, { code: 'pos.product.not_found' }))
      .mockRejectedValueOnce(new ApiError(500, {}))
    const results: ScanResult[] = []
    const { result } = renderHook(() => useScanQueue(lookup, (scan) => results.push(scan)))

    act(() => {
      result.current.enqueue('XX')
      result.current.enqueue('YY')
    })

    await waitFor(() => expect(results).toHaveLength(2))
    expect(results.map((scan) => scan.kind)).toEqual(['not-found', 'error'])
  })
})
```

`src/features/pos/components/pos-money-input.test.tsx`:

```tsx
import { fireEvent, render, screen } from '@testing-library/react'
import { useRef, useState } from 'react'

import { BURST_MESSAGE } from '../hooks/use-scanner-burst-guard'

import { PosMoneyInput } from './pos-money-input'

// A fake clock that advances `step` ms per keystroke; a ref so re-renders do not reset it.
function Harness({ onConfirm, step }: { onConfirm: (value: number | null) => void; step: number }) {
  const [value, setValue] = useState<number | null>(null)
  const clock = useRef(0)
  return (
    <PosMoneyInput
      id="tendered"
      label="Recibido"
      value={value}
      onChange={setValue}
      onConfirm={() => onConfirm(value)}
      now={() => (clock.current += step)}
    />
  )
}

function typeKeys(input: HTMLElement, text: string) {
  for (const key of text) {
    fireEvent.keyDown(input, { key })
    fireEvent.change(input, { target: { value: (input as HTMLInputElement).value + key } })
  }
}

describe('PosMoneyInput', () => {
  // Spec: an EAN scanned into "recibido" would type 7701234567890 and its Enter would confirm.
  it('discards a scanner burst and swallows its Enter', () => {
    const onConfirm = vi.fn()
    render(<Harness onConfirm={onConfirm} step={5} />)
    const input = screen.getByLabelText('Recibido')

    typeKeys(input, '7701234567890')
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(onConfirm).not.toHaveBeenCalled()
    expect(input).toHaveValue('')
    expect(screen.getByText(BURST_MESSAGE)).toBeVisible()
  })

  it('confirms human typing', () => {
    const onConfirm = vi.fn()
    render(<Harness onConfirm={onConfirm} step={120} />)
    const input = screen.getByLabelText('Recibido')

    typeKeys(input, '20000')
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(onConfirm).toHaveBeenCalledWith(20000)
  })

  // Review Focus 5: a paste is not keystrokes; its Enter has to confirm.
  it('paste followed by Enter is not treated as a scan', () => {
    const onConfirm = vi.fn()
    render(<Harness onConfirm={onConfirm} step={5} />)
    const input = screen.getByLabelText('Recibido')

    fireEvent.paste(input)
    fireEvent.change(input, { target: { value: '50000' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(onConfirm).toHaveBeenCalledWith(50000)
  })

  it('accepts a comma decimal and rejects a third decimal', () => {
    const onChange = vi.fn()
    render(<PosMoneyInput id="a" label="Monto" value={null} onChange={onChange} />)

    fireEvent.change(screen.getByLabelText('Monto'), { target: { value: '209,30' } })
    expect(onChange).toHaveBeenLastCalledWith(209.3)
    fireEvent.change(screen.getByLabelText('Monto'), { target: { value: '209,305' } })
    expect(onChange).toHaveBeenLastCalledWith(null)
  })
})
```

`src/features/pos/hooks/use-pos-shortcuts.test.tsx`:

```tsx
import { fireEvent, render, screen } from '@testing-library/react'
import { useRef } from 'react'

import { usePosShortcuts, type PosShortcutHandlers } from './use-pos-shortcuts'

function Harness({ handlers }: { handlers: PosShortcutHandlers }) {
  const listRef = useRef<HTMLUListElement>(null)
  usePosShortcuts(handlers, listRef)
  return (
    <div>
      <input aria-label="Buscar" />
      <ul ref={listRef}>
        <li>
          <button type="button">Línea</button>
        </li>
      </ul>
      <button type="button">Fuera</button>
    </div>
  )
}

const handlers = (): PosShortcutHandlers => ({
  focusSearch: vi.fn(),
  openPay: vi.fn(),
  printLast: vi.fn(),
  escape: vi.fn(),
  moveSelection: vi.fn(),
  changeQuantity: vi.fn(),
  removeSelected: vi.fn(),
})

describe('usePosShortcuts', () => {
  it('maps F2, F9, F8 and Esc anywhere', () => {
    const h = handlers()
    render(<Harness handlers={h} />)

    fireEvent.keyDown(screen.getByLabelText('Buscar'), { key: 'F2' })
    fireEvent.keyDown(screen.getByLabelText('Buscar'), { key: 'F9' })
    fireEvent.keyDown(document.body, { key: 'F8' })
    fireEvent.keyDown(document.body, { key: 'Escape' })

    expect(h.focusSearch).toHaveBeenCalledOnce()
    expect(h.openPay).toHaveBeenCalledOnce()
    expect(h.printLast).toHaveBeenCalledOnce()
    expect(h.escape).toHaveBeenCalledOnce()
  })

  // Product codes carry "-" (SH-400): a global "−" would remove a unit mid-scan.
  it('ignores + − Supr and arrows in the search input and outside the cart list', () => {
    const h = handlers()
    render(<Harness handlers={h} />)

    for (const key of ['-', '+', 'Delete', 'ArrowDown']) {
      fireEvent.keyDown(screen.getByLabelText('Buscar'), { key })
      fireEvent.keyDown(screen.getByRole('button', { name: 'Fuera' }), { key })
    }

    expect(h.changeQuantity).not.toHaveBeenCalled()
    expect(h.removeSelected).not.toHaveBeenCalled()
    expect(h.moveSelection).not.toHaveBeenCalled()
  })

  it('edits lines only with the focus inside the cart list', () => {
    const h = handlers()
    render(<Harness handlers={h} />)
    const line = screen.getByRole('button', { name: 'Línea' })

    fireEvent.keyDown(line, { key: '+' })
    fireEvent.keyDown(line, { key: '-' })
    fireEvent.keyDown(line, { key: 'ArrowUp' })
    fireEvent.keyDown(line, { key: 'Delete' })

    expect(h.changeQuantity).toHaveBeenNthCalledWith(1, 1)
    expect(h.changeQuantity).toHaveBeenNthCalledWith(2, -1)
    expect(h.moveSelection).toHaveBeenCalledWith(-1)
    expect(h.removeSelected).toHaveBeenCalledOnce()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/hooks/use-scan-queue.test.tsx src/features/pos/components/pos-money-input.test.tsx src/features/pos/hooks/use-pos-shortcuts.test.tsx
```

Esperado: FAIL, módulos inexistentes.

- [ ] **Step 3: Implementar**

`src/features/pos/hooks/use-scan-queue.ts`:

```ts
import { useCallback, useEffect, useRef, useState } from 'react'

import { ApiError } from '@/lib/api-client'

import type { PosProduct } from '../types/pos'

export type ScanResult =
  | { code: string; kind: 'found'; product: PosProduct }
  | { code: string; kind: 'not-found' }
  | { code: string; kind: 'error'; error: unknown }

// The scanner can send the next code before the first answer comes back. Requests fly in
// parallel, but results are delivered in the order the codes were scanned, so the cart keeps the
// scan order (spec, «Flujo de lector»).
export function useScanQueue(
  lookup: (code: string) => Promise<PosProduct>,
  onResult: (result: ScanResult) => void,
) {
  const chain = useRef<Promise<void>>(Promise.resolve())
  const onResultRef = useRef(onResult)
  const [pending, setPending] = useState(0)

  useEffect(() => {
    onResultRef.current = onResult
  }, [onResult])

  const enqueue = useCallback(
    (code: string) => {
      setPending((count) => count + 1)
      const request: Promise<ScanResult> = lookup(code).then(
        (product) => ({ code, kind: 'found', product }),
        (error: unknown) =>
          error instanceof ApiError && error.status === 404 ? { code, kind: 'not-found' } : { code, kind: 'error', error },
      )
      chain.current = chain.current
        .then(() => request)
        .then((result) => {
          onResultRef.current(result)
          setPending((count) => count - 1)
        })
    },
    [lookup],
  )

  return { enqueue, pending }
}
```

`src/features/pos/hooks/use-scanner-burst-guard.ts`:

```ts
import { useCallback, useRef, type KeyboardEvent } from 'react'

export const BURST_MESSAGE = 'Eso parece un escaneo; no se tomó como monto.'

const MIN_BURST_CHARS = 4
const MAX_BURST_INTERVAL_MS = 30

// A burst of 4+ characters less than 30 ms apart ending in Enter is the scanner, not a person.
// The caller discards the value and the Enter is swallowed, so scanning with the pay dialog open
// cannot confirm a sale (spec, decisión 39). A paste resets the buffer: it is not keystrokes.
export function useScannerBurstGuard({ onBurst, now = () => performance.now() }: { onBurst: () => void; now?: () => number }) {
  const stamps = useRef<number[]>([])

  const onKeyDown = useCallback(
    (event: KeyboardEvent<HTMLInputElement>): boolean => {
      if (event.key === 'Enter') {
        const times = stamps.current
        stamps.current = []
        const isBurst =
          times.length >= MIN_BURST_CHARS &&
          times.every((time, index) => index === 0 || time - times[index - 1] < MAX_BURST_INTERVAL_MS)
        if (isBurst) {
          event.preventDefault()
          event.stopPropagation()
          onBurst()
          return true
        }
        return false
      }

      if (event.key.length === 1) {
        const time = now()
        const last = stamps.current.at(-1)
        if (last !== undefined && time - last >= MAX_BURST_INTERVAL_MS) {
          stamps.current = []
        }
        stamps.current.push(time)
      }
      return false
    },
    [now, onBurst],
  )

  const onPaste = useCallback(() => {
    stamps.current = []
  }, [])

  return { onKeyDown, onPaste }
}
```

`src/features/pos/components/pos-money-input.tsx` (la forma visual la ajusta F11/F12 con las skills de diseño; el comportamiento queda fijo):

```tsx
import { useCallback, useEffect, useState } from 'react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

import { BURST_MESSAGE, useScannerBurstGuard } from '../hooks/use-scanner-burst-guard'
import { parseDecimalInput } from '../utils/money-cents'

interface PosMoneyInputProps {
  id: string
  label: string
  value: number | null
  onChange: (value: number | null) => void
  onConfirm?: () => void
  disabled?: boolean
  max?: number
  autoFocus?: boolean
  now?: () => number
}

function display(value: number | null): string {
  return value === null ? '' : String(value).replace('.', ',')
}

export function PosMoneyInput({ id, label, value, onChange, onConfirm, disabled, max, autoFocus, now }: PosMoneyInputProps) {
  const [text, setText] = useState(display(value))
  const [message, setMessage] = useState<string | null>(null)

  // External changes (a quick-cash button) replace the text; typing keeps its own text.
  useEffect(() => {
    if (parseDecimalInput(text) !== value) setText(display(value))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value])

  const onBurst = useCallback(() => {
    setText('')
    onChange(null)
    setMessage(BURST_MESSAGE)
  }, [onChange])
  const guard = useScannerBurstGuard({ onBurst, now })

  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        inputMode="decimal"
        autoComplete="off"
        autoFocus={autoFocus}
        disabled={disabled}
        value={text}
        aria-describedby={message ? `${id}-message` : undefined}
        onPaste={guard.onPaste}
        onKeyDown={(event) => {
          if (guard.onKeyDown(event)) return
          if (event.key === 'Enter') {
            event.preventDefault()
            onConfirm?.()
          }
        }}
        onChange={(event) => {
          const next = event.target.value
          setText(next)
          setMessage(null)
          const parsed = parseDecimalInput(next)
          onChange(parsed !== null && max !== undefined && parsed > max ? null : parsed)
        }}
      />
      {message && (
        <p id={`${id}-message`} role="status" className="text-sm text-warning-foreground">
          {message}
        </p>
      )}
    </div>
  )
}
```

(Si `react-hooks/exhaustive-deps` no está habilitado en oxlint, borra el comentario `eslint-disable`; si el token `text-warning-foreground` no existe en `index.css`, usa el que el tema tenga para avisos —búscalo con `git grep -n "warning" -- src/index.css`.)

`src/features/pos/hooks/use-pos-shortcuts.ts`:

```ts
import { useEffect, useRef, type RefObject } from 'react'

export interface PosShortcutHandlers {
  focusSearch(): void
  openPay(): void
  printLast?: () => void
  escape(): void
  moveSelection(delta: 1 | -1): void
  changeQuantity(delta: 1 | -1): void
  removeSelected(): void
}

function isEditable(element: HTMLElement): boolean {
  return element.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName)
}

// Function keys are captured only inside /pos. "+", "−" and Supr never act from an input or
// outside the cart list: codes carry "-" and a scan must never edit a line (spec, «Atajos»).
export function usePosShortcuts(
  handlers: PosShortcutHandlers,
  cartListRef: RefObject<HTMLElement | null>,
  enabled = true,
) {
  const latest = useRef(handlers)
  useEffect(() => {
    latest.current = handlers
  }, [handlers])

  useEffect(() => {
    if (!enabled) return

    function onKeyDown(event: KeyboardEvent) {
      const current = latest.current
      switch (event.key) {
        case 'F2':
          event.preventDefault()
          current.focusSearch()
          return
        case 'F9':
          event.preventDefault()
          current.openPay()
          return
        case 'F8':
          if (current.printLast) {
            event.preventDefault()
            current.printLast()
          }
          return
        case 'Escape':
          current.escape()
          return
      }

      const target = event.target instanceof HTMLElement ? event.target : null
      if (!target || isEditable(target) || !cartListRef.current?.contains(target)) return

      if (event.key === 'ArrowDown') {
        event.preventDefault()
        current.moveSelection(1)
      } else if (event.key === 'ArrowUp') {
        event.preventDefault()
        current.moveSelection(-1)
      } else if (event.key === '+') {
        current.changeQuantity(1)
      } else if (event.key === '-') {
        current.changeQuantity(-1)
      } else if (event.key === 'Delete') {
        current.removeSelected()
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [enabled, cartListRef])
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/hooks/use-scan-queue.ts src/features/pos/hooks/use-scan-queue.test.tsx src/features/pos/hooks/use-scanner-burst-guard.ts src/features/pos/hooks/use-pos-shortcuts.ts src/features/pos/hooks/use-pos-shortcuts.test.tsx src/features/pos/components/pos-money-input.tsx src/features/pos/components/pos-money-input.test.tsx; git commit -m "feat(pos): cola de escaneo, guardia de ráfagas y atajos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F9: Gate, rutas, menú y aterrizaje

**Files:**
- Create: `src/features/pos/components/pos-gate.tsx`, `src/features/pos/pages/{pos-register-page,pos-close-session-page,pos-sales-page,pos-sessions-page}.tsx` (cascarones que F11-F14 completan), `src/routes/_authenticated/pos.tsx`, `src/routes/_authenticated/pos/index.tsx`, `src/routes/_authenticated/pos/close.tsx`, `src/routes/_authenticated/pos/sales/index.tsx`, `src/routes/_authenticated/pos/sessions/index.tsx`
- Modify: `src/components/app-shell/sidebar-nav-items.ts`, `src/test/permissions.ts`, `src/features/auth/services/landing.ts`, `src/routeTree.gen.ts` (regenerado)
- Test: `src/features/pos/components/pos-gate.test.tsx`, `src/routes/_authenticated/pos.test.tsx`, `src/components/app-shell/sidebar-nav-items.test.ts` (o el archivo de pruebas que ya cubra `visibleSidebarItems`), `src/features/auth/services/landing.test.ts`

**Interfaces:**
- Consumes: `ModuleGate` (entitlements), `usePermissions`, `PageContainer`, `Card`, `POS_PERMISSIONS`.
- Produces: `PosGate({ required: readonly string[]; children })`; `POS_REGISTER_PERMISSIONS = [saleCreate, registerOperate, saleRead]`; rutas `/pos`, `/pos/close`, `/pos/sales`, `/pos/sessions`; ítem de menú «Punto de venta»; `landingFor` → `/pos` para el cajero.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/components/pos-gate.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'

import gateSource from './pos-gate.tsx?raw'
import { POS_REGISTER_PERMISSIONS, PosGate } from './pos-gate'

const permissions = vi.hoisted(() => ({ granted: [] as string[], status: 'ready' as 'loading' | 'ready' }))
vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({
    can: (permission: string) => permissions.granted.includes(permission),
    status: permissions.status,
    sessionLost: false,
  }),
}))

const pairs = [
  ['pos.sale.create', 'pos.register.operate'],
  ['pos.sale.create', 'pos.sale.read'],
  ['pos.register.operate', 'pos.sale.read'],
]

describe('PosGate', () => {
  it.each(pairs)('shows the permission card with only %s and %s', (first, second) => {
    permissions.granted = [first, second]
    render(<PosGate required={POS_REGISTER_PERMISSIONS}>la caja</PosGate>)

    expect(screen.getByText('No tienes permiso para usar el punto de venta.')).toBeVisible()
    expect(screen.queryByText('la caja')).toBeNull()
  })

  it('opens the register with the three permissions', () => {
    permissions.granted = ['pos.sale.create', 'pos.register.operate', 'pos.sale.read']
    render(<PosGate required={POS_REGISTER_PERMISSIONS}>la caja</PosGate>)

    expect(screen.getByText('la caja')).toBeVisible()
  })

  it('denies by default while permissions load', () => {
    permissions.status = 'loading'
    render(<PosGate required={POS_REGISTER_PERMISSIONS}>la caja</PosGate>)

    expect(screen.getByText('Cargando permisos...')).toBeVisible()
    permissions.status = 'ready'
  })

  // One single place decides module messages and their fail-open: ModuleGate (decisión 48).
  it('does not read tenant modules on its own', () => {
    expect(gateSource).not.toContain('useTenantModules')
  })
})
```

`src/routes/_authenticated/pos.test.tsx` (sigue el patrón de las pruebas de ruta de entitlements; la URL de módulos es la que use `fetchTenantModules` en `features/auth/services/tenancy.api.ts`):

```tsx
import { screen, waitFor } from '@testing-library/react'

import { ALL_SIDEBAR_PERMISSIONS } from '@/test/permissions'
import { renderRoute } from '@/test/render-route'
import { tenantModulesResponse } from '@/test/tenant-modules'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'
const POS = ['pos.sale.create', 'pos.register.operate', 'pos.sale.read']

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function routeFetch({ posEnabled, permissions }: { posEnabled: boolean; permissions: string[] }) {
  vi.mocked(fetch).mockImplementation((input) => {
    const url = String(input)
    if (url.endsWith('/auth/me')) return Promise.resolve(json(200, { userId: 'u-1', email: 'caja@qcode.co', activeTenantIds: [TENANT] }))
    if (url.endsWith('/authorization/me')) return Promise.resolve(json(200, { tenantId: TENANT, userId: 'u-1', permissions }))
    if (url.includes('/modules')) return Promise.resolve(json(200, tenantModulesResponse({ pos: posEnabled })))
    if (url.endsWith('/pos/register'))
      return Promise.resolve(json(200, { cashier: { memberId: 'm-1', name: 'Laura Gómez' }, session: null, companies: [], defaultCompanyId: null }))
    return Promise.resolve(new Response(null, { status: 404 }))
  })
}

describe('/pos', () => {
  it('without the pos module shows the ModuleGate message and never asks for the register', async () => {
    routeFetch({ posEnabled: false, permissions: [...ALL_SIDEBAR_PERMISSIONS, ...POS] })

    renderRoute('/pos')

    await waitFor(() => expect(screen.getByText('Este módulo no está incluido en el plan de tu empresa.')).toBeVisible())
    expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).endsWith('/pos/register'))).toBe(false)
  })

  it('with the module and the three permissions draws the register', async () => {
    routeFetch({ posEnabled: true, permissions: POS })

    renderRoute('/pos')

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Punto de venta' })).toBeVisible())
  })
})
```

(Si `tenantModulesResponse` recibe otra forma de override —por ejemplo `{ disabled: ['pos'] }`—, ajusta la llamada a su firma real; el criterio es «`pos` apagado» y «`pos` prendido».)

En el archivo de pruebas que hoy cubre `visibleSidebarItems` (búscalo con `git grep -ln "visibleSidebarItems" -- src`), agrega:

```ts
  it('shows Punto de venta with pos.sale.create and falls back to the sessions with pos.sale.read', () => {
    const seller = visibleSidebarItems((permission) => permission === 'pos.sale.create', 'ready')
    const reader = visibleSidebarItems((permission) => permission === 'pos.sale.read', 'ready')

    expect(seller).toContainEqual(expect.objectContaining({ label: 'Punto de venta', to: '/pos' }))
    expect(reader).toContainEqual(expect.objectContaining({ label: 'Punto de venta', to: '/pos/sessions' }))
  })
```

En `src/features/auth/services/landing.test.ts`, agrega:

```ts
  it('sends a cashier straight to the register', () => {
    const cashier = ['pos.sale.create', 'pos.register.operate', 'pos.sale.read']

    expect(landingFor((permission) => cashier.includes(permission))).toBe('/pos')
    for (const missing of cashier) {
      const partial = cashier.filter((permission) => permission !== missing)
      expect(landingFor((permission) => partial.includes(permission))).not.toBe('/pos')
    }
    expect(landingFor((permission) => [...cashier, 'quotations.quotation.read'].includes(permission))).toBe('/quotes')
  })
```

(Si entitlements cambió la firma de `landingFor` —por ejemplo para recibir los módulos—, pásale todos los módulos prendidos y conserva el criterio.)

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/components/pos-gate.test.tsx src/routes/_authenticated/pos.test.tsx src/features/auth/services/landing.test.ts src/components/app-shell
```

Esperado: FAIL en las cuatro (módulo `pos-gate` inexistente, ruta `/pos` inexistente, ítem y aterrizaje ausentes).

- [ ] **Step 3: Implementar**

`src/features/pos/components/pos-gate.tsx`:

```tsx
import type { ReactNode } from 'react'

import { PageContainer } from '@/components/page-container'
import { Card } from '@/components/ui/card'
import { usePermissions } from '@/features/auth/hooks/use-permission'

import { POS_PERMISSIONS } from '../types/pos'

// The register needs the three: GET /pos/register asks pos.register.operate and "Verificar" an
// uncertain charge (GET /pos/sales/{id}) asks pos.sale.read (spec, decisión 47).
export const POS_REGISTER_PERMISSIONS = [
  POS_PERMISSIONS.saleCreate,
  POS_PERMISSIONS.registerOperate,
  POS_PERMISSIONS.saleRead,
] as const

// Only permissions, deny-by-default while they load (QuotesReadGate pattern). The module is
// ModuleGate's job, on the /pos layout route.
export function PosGate({ required, children }: { required: readonly string[]; children: ReactNode }) {
  const { can, status } = usePermissions()

  if (status === 'loading') {
    return (
      <PageContainer>
        <Card className="p-8 text-center text-sm text-muted-foreground">Cargando permisos...</Card>
      </PageContainer>
    )
  }

  if (!required.every((permission) => can(permission))) {
    return (
      <PageContainer>
        <Card className="p-8 text-center text-sm text-muted-foreground">
          No tienes permiso para usar el punto de venta.
        </Card>
      </PageContainer>
    )
  }

  return children
}
```

Cascarones de página (F11-F14 los reemplazan enteros):

`src/features/pos/pages/pos-register-page.tsx`:

```tsx
export function PosRegisterPage() {
  return <h1 className="sr-only">Punto de venta</h1>
}
```

`src/features/pos/pages/pos-close-session-page.tsx`, `pos-sales-page.tsx`, `pos-sessions-page.tsx`: lo mismo con `PosCloseSessionPage` / «Cerrar caja», `PosSalesPage` / «Ventas», `PosSessionsPage` / «Cajas».

`src/routes/_authenticated/pos.tsx`:

```tsx
import { createFileRoute, Outlet } from '@tanstack/react-router'

import { ModuleGate } from '@/components/module-gate'

// Same layout pattern as catalog/orders since entitlements: with `pos` off no /pos/* page mounts
// and no query goes out.
export const Route = createFileRoute('/_authenticated/pos')({
  component: PosLayout,
})

function PosLayout() {
  return (
    <ModuleGate modules={['pos']}>
      <Outlet />
    </ModuleGate>
  )
}
```

`src/routes/_authenticated/pos/index.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router'

import { POS_REGISTER_PERMISSIONS, PosGate } from '@/features/pos/components/pos-gate'
import { PosRegisterPage } from '@/features/pos/pages/pos-register-page'

export const Route = createFileRoute('/_authenticated/pos/')({
  component: () => (
    <PosGate required={POS_REGISTER_PERMISSIONS}>
      <PosRegisterPage />
    </PosGate>
  ),
})
```

`src/routes/_authenticated/pos/close.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router'

import { PosGate } from '@/features/pos/components/pos-gate'
import { PosCloseSessionPage } from '@/features/pos/pages/pos-close-session-page'
import { POS_PERMISSIONS } from '@/features/pos/types/pos'

export const Route = createFileRoute('/_authenticated/pos/close')({
  component: () => (
    <PosGate required={[POS_PERMISSIONS.registerOperate]}>
      <PosCloseSessionPage />
    </PosGate>
  ),
})
```

`src/routes/_authenticated/pos/sales/index.tsx` y `pos/sessions/index.tsx`: lo mismo con `createFileRoute('/_authenticated/pos/sales/')` / `PosSalesPage` y `createFileRoute('/_authenticated/pos/sessions/')` / `PosSessionsPage`, los dos con `required={[POS_PERMISSIONS.saleRead]}`.

En `sidebar-nav-items.ts`: `SidebarRoute` suma `| '/pos' | '/pos/sessions'`; importa `Store` de `lucide-react`; y después del ítem de Pedidos:

```ts
  // Who reads without selling (a supervisor with pos.sale.read + pos.register.read) lands on the
  // sessions. `Store` repeats none of the nine icons in use (spec, «Menú y aterrizaje»).
  {
    to: '/pos',
    label: 'Punto de venta',
    icon: Store,
    permission: 'pos.sale.create',
    alternates: [{ permission: 'pos.sale.read', to: '/pos/sessions' }],
  },
```

(Si entitlements agregó a `SidebarLinkItem` un campo de módulo, ponle `'pos'`.) En `src/test/permissions.ts`, suma `'pos.sale.create'` a `ALL_SIDEBAR_PERMISSIONS`.

En `landing.ts`: `Landing` suma `'/pos'`, y `landingFor` empieza con:

```ts
  // A cashier opens straight on the register: the three permissions of its gate and neither
  // quotes nor orders (spec, «Menú y aterrizaje»).
  const isCashierOnly =
    can('pos.sale.create') &&
    can('pos.register.operate') &&
    can('pos.sale.read') &&
    !can(QUOTE_READ_PERMISSION) &&
    !can(ORDER_READ_PERMISSION)
  if (isCashierOnly) return '/pos'
```

Regenera el árbol de rutas:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run build
git status --short src/routeTree.gen.ts
```

Esperado: build verde y `src/routeTree.gen.ts` modificado (nunca a mano).

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos src/routes src/features/auth src/components/app-shell
bun run lint
bunx prettier --check src/features/pos src/routes/_authenticated/pos.tsx src/routes/_authenticated/pos src/components/app-shell/sidebar-nav-items.ts src/features/auth/services/landing.ts
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/components/pos-gate.tsx src/features/pos/components/pos-gate.test.tsx src/features/pos/pages src/routes/_authenticated/pos.tsx src/routes/_authenticated/pos.test.tsx src/routes/_authenticated/pos src/routeTree.gen.ts src/components/app-shell src/test/permissions.ts src/features/auth/services/landing.ts src/features/auth/services/landing.test.ts; git commit -m "feat(pos): rutas, gate, menú y aterrizaje del punto de venta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: PASS. Cualquier prueba de ruta o de app-shell que ya existía y ahora falla porque su lista de ítems del menú cambió se compara por nombre con la línea base; si es nueva, ajústala (el ítem nuevo es esperado) y anótalo.

---


### Task F10: Ticket de 80 mm *(con skills de diseño)*

**Files:**
- Create: `src/features/pos/components/pos-ticket.tsx`, `src/features/pos/components/ticket-print-portal.tsx`, `src/features/pos/pos-print.css`, `src/features/pos/utils/format-local-stamp.ts`, `src/features/pos/utils/pos-sale.fixture.ts`
- Modify: `src/features/pos/utils/format-pos-money.ts` (`formatPosNumber`)
- Test: `src/features/pos/components/pos-ticket.test.tsx`, `src/features/pos/utils/format-local-stamp.test.ts`, `src/features/pos/utils/format-pos-money.test.ts`

**Interfaces:**
- Consumes: `printWithTitle` (F6), `PosSale` (F1), `formatPosMoney` (F2).
- Produces: `PosTicket({ sale: PosSale })`; `TicketPrintPortal({ title: string; onDone(): void; children })` (monta, imprime desde `useLayoutEffect`, desmonta en `afterprint`); `formatLocalStamp(localIso: string): string` (`dd/MM/yyyy HH:mm`); `formatPosNumber(value: number): string` (como `formatPosMoney` sin símbolo); `workedExampleSale(overrides?): PosSale` (fixture de pruebas).

Antes del Step 3, carga `frontend-design` e `impeccable` para la tipografía mono de 11 px, el ancho de 72 mm y la jerarquía del ticket (negro sobre blanco, sin colores del tema).

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/utils/pos-sale.fixture.ts`:

```ts
import type { PosSale } from '../types/pos'

// The worked example of the spec: card 20.000 (ref 1234) + cash, tendered 20.000.
export function workedExampleSale(overrides: Partial<PosSale> = {}): PosSale {
  return {
    id: '6f1c2a52-8a3e-4c4e-9d55-3c2b1e0f7a11',
    saleNumber: 'POS-000042',
    status: 'Completed',
    createdAt: '2026-10-07T15:20:03Z',
    createdAtLocal: '2026-10-07T10:20:03-05:00',
    cashSessionId: 'c-1',
    cashierName: 'Laura Gómez',
    issuer: { name: 'Origen Botánico SAS', taxId: '900123456-1', address: 'Cra 50 # 10-20, Rionegro', phone: '6045551234' },
    customer: { name: 'Consumidor final', identificationType: null, identificationNumber: '222222222222' },
    lines: [
      { position: 1, productId: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', quantity: 2, unitPrice: 11900, discountPercentage: 10, taxPercentage: 19, discountAmount: 2380, taxAmount: 3420, subtotal: 18000, lineTotal: 21420 },
      { position: 2, productId: 'p-av', code: 'AV-01', name: 'Avena granel (kg)', quantity: 1.5, unitPrice: 5000, discountPercentage: 0, taxPercentage: 0, discountAmount: 0, taxAmount: 0, subtotal: 7500, lineTotal: 7500 },
      { position: 3, productId: 'p-jb', code: 'JB-03', name: 'Jabón', quantity: 3, unitPrice: 2990, discountPercentage: 0, taxPercentage: 5, discountAmount: 0, taxAmount: 427.14, subtotal: 8542.86, lineTotal: 8970 },
    ],
    subtotal: 34042.86,
    taxAmount: 3847.14,
    discountAmount: 2380,
    total: 37890,
    taxBreakdown: [
      { taxPercentage: 0, base: 7500, taxAmount: 0 },
      { taxPercentage: 5, base: 8542.86, taxAmount: 427.14 },
      { taxPercentage: 19, base: 18000, taxAmount: 3420 },
    ],
    payments: [
      { method: 'Card', amount: 20000, tendered: null, reference: '1234' },
      { method: 'Cash', amount: 17890, tendered: 20000, reference: null },
    ],
    changeAmount: 2110,
    void: null,
    voidable: true,
    voidBlockedReason: null,
    ...overrides,
  }
}
```

`src/features/pos/utils/format-local-stamp.test.ts`:

```ts
import { formatLocalStamp } from './format-local-stamp'

describe('formatLocalStamp', () => {
  // The server already converted to the tenant time zone; the screen must not convert again.
  it('formats the local ISO as dd/MM/yyyy HH:mm without converting it', () => {
    expect(formatLocalStamp('2026-10-07T10:20:03-05:00')).toBe('07/10/2026 10:20')
    expect(formatLocalStamp('2026-10-07T23:59:59-05:00')).toBe('07/10/2026 23:59')
  })
})
```

En `format-pos-money.test.ts`, agrega:

```ts
import { formatPosNumber } from './format-pos-money'

describe('formatPosNumber', () => {
  it('formats like the money but without the symbol', () => {
    expect(formatPosNumber(11900)).toBe('11.900')
    expect(formatPosNumber(427.14)).toBe('427,14')
  })
})
```

`src/features/pos/components/pos-ticket.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react'
import { useState } from 'react'

import { workedExampleSale } from '../utils/pos-sale.fixture'

import { PosTicket } from './pos-ticket'
import { TicketPrintPortal } from './ticket-print-portal'

describe('PosTicket', () => {
  it('renders issuer, header, customer, lines, tax breakdown, payments and change', () => {
    render(<PosTicket sale={workedExampleSale()} />)
    const ticket = screen.getByTestId('pos-ticket')

    for (const text of [
      'Origen Botánico SAS',
      'NIT 900123456-1',
      'Venta POS-000042',
      '07/10/2026 10:20',
      'Cajero: Laura Gómez',
      'Consumidor final',
      '222222222222',
      '2 x 11.900',
      '-10%',
      'IVA 19% base 18.000 = 3.420',
      'IVA 5% base 8.542,86 = 427,14',
      'Tarjeta 20.000 ref 1234',
      'Efectivo 20.000',
      'Cambio 2.110',
      'Documento interno de venta. No es factura electrónica.',
    ]) {
      expect(within(ticket).getByText(text, { exact: false })).toBeInTheDocument()
    }
    expect(within(ticket).queryByText('ANULADA')).toBeNull()
  })

  it('marks a voided sale with its reason', () => {
    render(
      <PosTicket
        sale={workedExampleSale({
          status: 'Voided',
          void: { reason: 'Cliente se arrepintió', voidedAtLocal: '2026-10-07T11:00:00-05:00', voidedByName: 'Admin' },
        })}
      />,
    )

    expect(screen.getByText('ANULADA')).toBeVisible()
    expect(screen.getByText('Cliente se arrepintió', { exact: false })).toBeVisible()
  })
})

describe('TicketPrintPortal', () => {
  it('prints with the ticket already in the DOM and unmounts after printing', () => {
    let printedText: string | null = null
    const print = vi.spyOn(window, 'print').mockImplementation(() => {
      printedText = document.querySelector('[data-pos-ticket]')?.textContent ?? null
    })

    function Harness() {
      const [printing, setPrinting] = useState(true)
      return printing ? (
        <TicketPrintPortal title="POS-000042" onDone={() => setPrinting(false)}>
          <PosTicket sale={workedExampleSale()} />
        </TicketPrintPortal>
      ) : null
    }
    render(<Harness />)

    expect(print).toHaveBeenCalledOnce()
    expect(printedText).toContain('Venta POS-000042')
    const region = document.querySelector('[data-pos-ticket]')
    expect(region).toHaveAttribute('data-print-region')

    window.dispatchEvent(new Event('afterprint'))
    expect(document.querySelector('[data-pos-ticket]')).toBeNull()
  })
})
```

(`render` ya envuelve en `act`, así que el `afterprint` despachado fuera puede necesitar `act(() => window.dispatchEvent(...))`; úsalo si React avisa.)

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/components/pos-ticket.test.tsx src/features/pos/utils
```

Esperado: FAIL (módulos `pos-ticket`, `ticket-print-portal`, `format-local-stamp` y la función `formatPosNumber` inexistentes).

- [ ] **Step 3: Implementar**

`src/features/pos/utils/format-local-stamp.ts`:

```ts
// "2026-10-07T10:20:03-05:00" → "07/10/2026 10:20". The backend sends *Local times already in the
// tenant time zone (the screen does not know it): read the digits, never re-convert.
export function formatLocalStamp(localIso: string): string {
  const [date, time = ''] = localIso.split('T')
  const [year, month, day] = date.split('-')
  return `${day}/${month}/${year} ${time.slice(0, 5)}`
}
```

En `format-pos-money.ts`, agrega:

```ts
const NUMBER_FORMATTERS = {
  0: new Intl.NumberFormat('es-CO', { minimumFractionDigits: 0, maximumFractionDigits: 0 }),
  2: new Intl.NumberFormat('es-CO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
} as const

// The ticket's numbers: same cents rule, no currency symbol (spec, «Ticket»).
export function formatPosNumber(value: number): string {
  const cents = Math.round(value * 100)
  return NUMBER_FORMATTERS[cents % 100 === 0 ? 0 : 2].format(cents / 100)
}
```

`src/features/pos/pos-print.css`:

```css
/* Named page: only the element with `page: pos-ticket` takes it. An unnamed @page would be
   global and would turn the order print (order-detail-page) and the conversion summary into 80 mm.
   `size: 80mm auto` is not valid CSS (size takes no `auto` per axis) and the browser would drop the
   whole rule: a fixed roll height, and the thermal driver (continuous paper, auto cut) cuts where
   the content ends. Chrome and Edge support `page:`; elsewhere the ticket prints on the driver's
   default paper without breaking anything (spec 2026-10-07, «Ticket»). */
@page pos-ticket {
  size: 80mm 297mm;
  margin: 0;
}

@media print {
  [data-pos-ticket] {
    page: pos-ticket;
    width: 72mm;
    padding: 4mm;
  }
}
```

`src/features/pos/components/ticket-print-portal.tsx`:

```tsx
import { useLayoutEffect, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

import { printWithTitle } from '@/lib/print-with-title'

import '../pos-print.css'

// Reuses the data-print-region mechanism of src/index.css: those rules hide everything else and
// flatten the ancestors. The ticket mounts on document.body only while printing, and printWithTitle
// runs from useLayoutEffect, never in the same handler as the setState that mounts it — there
// React has not written the ticket yet and the page would print empty.
export function TicketPrintPortal({ title, onDone, children }: { title: string; onDone: () => void; children: ReactNode }) {
  useLayoutEffect(() => {
    const done = () => onDone()
    window.addEventListener('afterprint', done, { once: true })
    printWithTitle(title)
    return () => window.removeEventListener('afterprint', done)
    // Print once per mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return createPortal(
    <div data-print-region data-pos-ticket className="bg-white text-black">
      {children}
    </div>,
    document.body,
  )
}
```

`src/features/pos/components/pos-ticket.tsx` (estructura y textos fijos; tipografía y espaciado con las skills):

```tsx
import type { PosSale } from '../types/pos'
import { PAYMENT_METHOD_LABELS } from '../types/pos'
import { formatLocalStamp } from '../utils/format-local-stamp'
import { formatPosNumber } from '../utils/format-pos-money'

const quantity = (value: number) => formatPosNumber(value)

export function PosTicket({ sale }: { sale: PosSale }) {
  return (
    <article data-testid="pos-ticket" className="font-mono text-[11px] leading-snug text-black">
      {sale.status === 'Voided' && (
        <header className="mb-2 text-center">
          <p className="text-xl font-bold">ANULADA</p>
          {sale.void && <p>Motivo: {sale.void.reason}</p>}
        </header>
      )}

      <section className="text-center">
        <p className="font-bold">{sale.issuer.name}</p>
        <p>NIT {sale.issuer.taxId}</p>
        {sale.issuer.address && <p>{sale.issuer.address}</p>}
        {sale.issuer.phone && <p>{sale.issuer.phone}</p>}
      </section>

      <section className="mt-2">
        <p>Venta {sale.saleNumber}</p>
        <p>{formatLocalStamp(sale.createdAtLocal)}</p>
        <p>Cajero: {sale.cashierName}</p>
      </section>

      <section className="mt-2">
        <p>Cliente: {sale.customer.name}</p>
        <p>{sale.customer.identificationNumber}</p>
      </section>

      <ul className="mt-2 space-y-1">
        {sale.lines.map((line) => (
          <li key={line.position}>
            <p>{line.name}</p>
            <p className="flex justify-between gap-2">
              <span>
                {quantity(line.quantity)} x {formatPosNumber(line.unitPrice)}
                {line.discountPercentage > 0 && `  -${formatPosNumber(line.discountPercentage)}%`}
              </span>
              <span>{formatPosNumber(line.lineTotal)}</span>
            </p>
          </li>
        ))}
      </ul>

      <section className="mt-2 border-t border-dashed border-black pt-1">
        <p className="flex justify-between"><span>Subtotal</span><span>{formatPosNumber(sale.subtotal)}</span></p>
        {sale.discountAmount > 0 && (
          <p className="flex justify-between"><span>Descuentos</span><span>{formatPosNumber(sale.discountAmount)}</span></p>
        )}
        {sale.taxBreakdown.map((entry) => (
          <p key={entry.taxPercentage}>
            IVA {entry.taxPercentage}% base {formatPosNumber(entry.base)} = {formatPosNumber(entry.taxAmount)}
          </p>
        ))}
        <p className="flex justify-between text-sm font-bold"><span>TOTAL</span><span>{formatPosNumber(sale.total)}</span></p>
      </section>

      <section className="mt-2">
        {sale.payments.map((payment, index) => (
          <p key={index}>
            {PAYMENT_METHOD_LABELS[payment.method]} {formatPosNumber(payment.tendered ?? payment.amount)}
            {payment.reference && ` ref ${payment.reference}`}
          </p>
        ))}
        {sale.changeAmount > 0 && <p>Cambio {formatPosNumber(sale.changeAmount)}</p>}
      </section>

      <footer className="mt-3 text-center">Documento interno de venta. No es factura electrónica.</footer>
    </article>
  )
}
```

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/components/pos-ticket.tsx src/features/pos/components/pos-ticket.test.tsx src/features/pos/components/ticket-print-portal.tsx src/features/pos/pos-print.css src/features/pos/utils; git commit -m "feat(pos): ticket de 80 mm"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Después, **prueba manual de impresión** con Chrome o Edge (vista previa: papel de 80 mm, sólo el ticket, sin menú ni fondo). Con la impresora térmica del tenant, la aceptación del corte automático es del slice (spec, «Ticket»); anota el resultado en el handoff.

---

### Task F11: Apertura y cierre de caja *(con skills de diseño)*

**Files:**
- Create: `src/features/pos/components/open-session-form.tsx`, `src/features/pos/components/close-session-form.tsx`, `src/features/pos/components/closing-summary.tsx`
- Modify: `src/features/pos/pages/pos-close-session-page.tsx` (reemplaza el cascarón), `src/features/pos/hooks/use-close-session.ts` (`useFetchSessionSummary`)
- Test: `src/features/pos/components/open-session-form.test.tsx`, `src/features/pos/pages/pos-close-session-page.test.tsx`

**Interfaces:**
- Consumes: F5 (store), F7 (`useOpenSession`, `useCloseSession`, `useRegisterContext`), F8 (`PosMoneyInput`), F10 (`TicketPrintPortal`, `formatLocalStamp`).
- Produces: `OpenSessionForm({ context: RegisterContext })`; `CloseSessionForm({ session: PosOpenSession; onClosed(summary: SessionSummary): void })`; `ClosingSummary({ summary: SessionSummary; onOpenAnother(): void })` con «Imprimir cierre»; `useFetchSessionSummary(): (sessionId: string) => Promise<SessionSummary>`; `PosCloseSessionPage`.

Antes del Step 3, carga `impeccable` y `frontend-design`: formulario centrado `max-w-md`, diferencia en vivo con color **y** texto («Faltante», «Sobrante», «Cuadra»; nunca sólo color), objetivos ≥ 44 px.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/components/open-session-form.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'

import type { RegisterContext } from '../types/pos'

import { OpenSessionForm } from './open-session-form'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))

const base: RegisterContext = {
  cashier: { memberId: 'm-1', name: 'Laura Gómez' },
  session: null,
  companies: [{ id: 'co-1', name: 'Origen Botánico SAS', taxId: '900123456-1' }],
  defaultCompanyId: 'co-1',
}

function renderForm(context: RegisterContext) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <OpenSessionForm context={context} />
    </QueryClientProvider>,
  )
}

describe('OpenSessionForm', () => {
  it('asks for the company only when there is no default', () => {
    const { unmount } = renderForm(base)
    expect(screen.queryByLabelText('Empresa emisora')).toBeNull()
    unmount()

    renderForm({ ...base, defaultCompanyId: null, companies: [...base.companies, { id: 'co-2', name: 'Otra SAS', taxId: '1' }] })
    expect(screen.getByLabelText('Empresa emisora')).toBeInTheDocument()
  })

  it('explains that there is no active company and shows no form', () => {
    renderForm({ ...base, companies: [], defaultCompanyId: null })

    expect(
      screen.getByText('Todavía no hay ninguna empresa emisora activa. Pídele a un administrador que active una en Empresas.'),
    ).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Abrir caja' })).toBeNull()
  })

  it('opens with the float and lets the server pick the only company', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ id: 'c-1' }), { status: 201, headers: { 'Content-Type': 'application/json' } }))
    renderForm(base)

    fireEvent.change(screen.getByLabelText('Base de efectivo'), { target: { value: '100000' } })
    fireEvent.click(screen.getByRole('button', { name: 'Abrir caja' }))

    await waitFor(() => expect(fetch).toHaveBeenCalled())
    const [input, init] = vi.mocked(fetch).mock.calls[0]
    expect(String(input)).toBe('/api/v1/tenants/t-1/pos/sessions')
    expect(JSON.parse(String(init?.body))).toEqual({ companyId: null, openingFloat: 100000 })
  })
})
```

`src/features/pos/pages/pos-close-session-page.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'

import { getPosCartStore, resetPosCartStoresForTests } from '../stores/pos-cart-store'

import { PosCloseSessionPage } from './pos-close-session-page'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))
vi.mock('@/features/auth/hooks/use-session', () => ({ useSession: () => ({ session: { userId: 'u-1' }, status: 'authenticated' }) }))
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-router')>()),
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
  useNavigate: () => vi.fn(),
}))

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const session = (version: number, expectedCash: number) => ({
  id: 'c-1', status: 'Open', openedAt: '2026-10-07T13:02:11Z', openedAtLocal: '2026-10-07T08:02:11-05:00', openedBeforeToday: false,
  company: { id: 'co-1', name: 'Origen Botánico SAS', taxId: '900123456-1' }, openingFloat: 100000, salesCount: 1, voidedCount: 0,
  salesTotal: 37890, expectedCash, version,
  paymentTotals: [{ method: 'Cash', amount: expectedCash - 100000 }, { method: 'Card', amount: 20000 }, { method: 'Transfer', amount: 0 }],
})
const register = (version: number, expectedCash: number) =>
  ({ cashier: { memberId: 'm-1', name: 'Laura Gómez' }, session: session(version, expectedCash), companies: [], defaultCompanyId: null })
const closedSummary = {
  id: 'c-1', status: 'Closed', cashierName: 'Laura Gómez', company: { name: 'Origen Botánico SAS', taxId: '900123456-1' },
  openedAtLocal: '2026-10-07T08:02:11-05:00', closedAtLocal: '2026-10-07T18:31:40-05:00', openingFloat: 100000, salesCount: 1,
  voidedCount: 0, salesTotal: 37890, paymentTotals: [], expectedCash: 117890, countedCash: 117000, cashDifference: -890, note: null,
}

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <PosCloseSessionPage />
    </QueryClientProvider>,
  )
}

afterEach(() => resetPosCartStoresForTests())

describe('PosCloseSessionPage', () => {
  it('closes with If-Match carrying the version that drew the count', async () => {
    vi.mocked(fetch).mockImplementation((input, init) => {
      const url = String(input)
      if (url.endsWith('/pos/register')) return Promise.resolve(json(200, register(2, 117890)))
      if (url.endsWith('/close') && init?.method === 'POST') return Promise.resolve(json(200, closedSummary))
      return Promise.resolve(new Response(null, { status: 404 }))
    })
    renderPage()

    await screen.findByText('$\u00a0117.890')
    fireEvent.change(screen.getByLabelText('Efectivo contado'), { target: { value: '117000' } })
    expect(screen.getByText(/Faltante/)).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

    await screen.findByRole('button', { name: 'Imprimir cierre' })
    const closeCall = vi.mocked(fetch).mock.calls.find(([input]) => String(input).endsWith('/close'))!
    expect(new Headers(closeCall[1]?.headers).get('If-Match')).toBe('"2"')
  })

  it('a 412 with the session still open reloads the count and keeps what was counted', async () => {
    let registerCalls = 0
    vi.mocked(fetch).mockImplementation((input, init) => {
      const url = String(input)
      if (url.endsWith('/pos/register')) return Promise.resolve(json(200, registerCalls++ === 0 ? register(2, 117890) : register(3, 129790)))
      if (url.endsWith('/close') && init?.method === 'POST') return Promise.resolve(json(412, { code: 'concurrency.conflict' }))
      if (url.endsWith('/pos/sessions/c-1')) return Promise.resolve(json(200, { ...closedSummary, status: 'Open', closedAtLocal: null }))
      return Promise.resolve(new Response(null, { status: 404 }))
    })
    renderPage()

    await screen.findByText('$\u00a0117.890')
    fireEvent.change(screen.getByLabelText('Efectivo contado'), { target: { value: '117000' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

    await screen.findByText('Entraron movimientos mientras contabas. Revisa el efectivo esperado antes de cerrar.')
    await screen.findByText('$\u00a0129.790')
    expect(screen.getByLabelText('Efectivo contado')).toHaveValue('117000')
  })

  it.each([
    [412, 'concurrency.conflict'],
    [422, 'pos.session.not_open'],
  ])('a %s %s with the session already closed shows its summary as success', async (status, code) => {
    vi.mocked(fetch).mockImplementation((input, init) => {
      const url = String(input)
      if (url.endsWith('/pos/register')) return Promise.resolve(json(200, register(2, 117890)))
      if (url.endsWith('/close') && init?.method === 'POST') return Promise.resolve(json(status, { code }))
      if (url.endsWith('/pos/sessions/c-1')) return Promise.resolve(json(200, closedSummary))
      return Promise.resolve(new Response(null, { status: 404 }))
    })
    renderPage()

    await screen.findByText('$\u00a0117.890')
    fireEvent.change(screen.getByLabelText('Efectivo contado'), { target: { value: '117000' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

    await screen.findByRole('button', { name: 'Imprimir cierre' })
    expect(screen.getByText(/-\$\u00a0890|Faltante/)).toBeVisible()
  })

  it('does not let close with an unconfirmed charge and links to the register', async () => {
    const store = getPosCartStore('t-1', 'u-1')
    store.getState().addProduct({ id: 'p', code: 'SH-400', name: 'Shampoo', imageUrl: null, unitPrice: 11900, taxPercentage: 19 })
    store.getState().openPay()
    store.getState().beginAttempt({ id: 's-1', cashSessionId: 'c-1', lines: [], payments: [] })
    store.getState().markUncertain()
    vi.mocked(fetch).mockResolvedValue(json(200, register(2, 117890)))
    renderPage()

    expect(await screen.findByText('Hay un cobro sin confirmar. Resuélvelo en la caja antes de cerrar.')).toBeVisible()
    expect(screen.getByRole('link', { name: 'Ir a la caja' })).toHaveAttribute('href', '/pos')
    expect(screen.queryByRole('button', { name: 'Cerrar caja' })).toBeNull()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/components/open-session-form.test.tsx src/features/pos/pages/pos-close-session-page.test.tsx
```

Esperado: FAIL (componentes inexistentes; la página es el cascarón de F9).

- [ ] **Step 3: Implementar el comportamiento (y después la forma, con las skills)**

En `use-close-session.ts`, agrega:

```ts
import { useCallback } from 'react'

import { fetchSession } from '../services/pos.api'

// After a 412 or pos.session.not_open the close is not taken as an error without looking: if the
// session is already Closed, the previous close did land and its answer was lost.
export function useFetchSessionSummary() {
  const { tenantId } = useActiveTenant()
  return useCallback((sessionId: string) => fetchSession(tenantId!, sessionId), [tenantId])
}
```

`src/features/pos/components/open-session-form.tsx`:

```tsx
import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

import { useOpenSession } from '../hooks/use-open-session'
import { describePosFailure } from '../services/pos.api'
import type { RegisterContext } from '../types/pos'

import { PosMoneyInput } from './pos-money-input'

const MAX_FLOAT = 100_000_000

export function OpenSessionForm({ context }: { context: RegisterContext }) {
  const openSession = useOpenSession()
  const [openingFloat, setOpeningFloat] = useState<number | null>(0)
  const [companyId, setCompanyId] = useState<string | null>(null)

  if (context.companies.length === 0) {
    return (
      <Card className="mx-auto max-w-md p-8 text-center text-sm text-muted-foreground">
        Todavía no hay ninguna empresa emisora activa. Pídele a un administrador que active una en Empresas.
      </Card>
    )
  }

  const needsCompany = context.defaultCompanyId === null
  const canSubmit = openingFloat !== null && (!needsCompany || companyId !== null) && !openSession.isPending

  return (
    <Card className="mx-auto max-w-md space-y-4 p-6">
      <h1 className="text-lg font-semibold">Abrir caja</h1>
      {needsCompany && (
        <div className="space-y-1">
          <Label htmlFor="pos-company">Empresa emisora</Label>
          <Select value={companyId ?? undefined} onValueChange={setCompanyId}>
            <SelectTrigger id="pos-company" aria-label="Empresa emisora">
              <SelectValue placeholder="Elige la empresa" />
            </SelectTrigger>
            <SelectContent>
              {context.companies.map((company) => (
                <SelectItem key={company.id} value={company.id}>
                  {company.name} · {company.taxId}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      )}
      <PosMoneyInput
        id="pos-opening-float"
        label="Base de efectivo"
        value={openingFloat}
        onChange={setOpeningFloat}
        max={MAX_FLOAT}
        autoFocus
      />
      {openSession.isError && (
        <p role="alert" className="text-sm text-destructive">
          {describePosFailure(openSession.error)}
        </p>
      )}
      <Button
        className="min-h-11 w-full"
        disabled={!canSubmit}
        onClick={() =>
          openSession.mutate({ companyId: needsCompany ? companyId : null, openingFloat: openingFloat ?? 0 })
        }
      >
        Abrir caja
      </Button>
    </Card>
  )
}
```

`src/features/pos/components/closing-summary.tsx`:

```tsx
import { useState } from 'react'

import { Button } from '@/components/ui/button'

import type { SessionSummary } from '../types/pos'
import { PAYMENT_METHOD_LABELS } from '../types/pos'
import { formatLocalStamp } from '../utils/format-local-stamp'
import { formatPosMoney, formatPosNumber } from '../utils/format-pos-money'

import { TicketPrintPortal } from './ticket-print-portal'

export function differenceLabel(difference: number): string {
  if (difference < 0) return `Faltante ${formatPosMoney(-difference)}`
  if (difference > 0) return `Sobrante ${formatPosMoney(difference)}`
  return 'Cuadra'
}

export function ClosingTicket({ summary }: { summary: SessionSummary }) {
  return (
    <article className="font-mono text-[11px] leading-snug text-black">
      <p className="text-center font-bold">{summary.company.name}</p>
      <p className="text-center">NIT {summary.company.taxId}</p>
      <p className="mt-2">Cierre de caja · {summary.cashierName}</p>
      <p>Apertura {formatLocalStamp(summary.openedAtLocal)}</p>
      {summary.closedAtLocal && <p>Cierre {formatLocalStamp(summary.closedAtLocal)}</p>}
      <p>Base {formatPosNumber(summary.openingFloat)}</p>
      <p>Ventas {summary.salesCount} · Anuladas {summary.voidedCount}</p>
      {summary.paymentTotals.map((total) => (
        <p key={total.method}>
          {PAYMENT_METHOD_LABELS[total.method]} {formatPosNumber(total.amount)}
        </p>
      ))}
      <p>Esperado {formatPosNumber(summary.expectedCash)}</p>
      {summary.countedCash !== null && <p>Contado {formatPosNumber(summary.countedCash)}</p>}
      {summary.cashDifference !== null && <p>{differenceLabel(summary.cashDifference)}</p>}
      {summary.note && <p>Nota: {summary.note}</p>}
    </article>
  )
}

export function ClosingSummary({ summary, onOpenAnother }: { summary: SessionSummary; onOpenAnother: () => void }) {
  const [printing, setPrinting] = useState(false)
  return (
    <section className="mx-auto max-w-md space-y-4">
      <h1 className="text-lg font-semibold">Caja cerrada</h1>
      <dl className="grid grid-cols-2 gap-2 text-sm">
        <dt>Efectivo esperado</dt>
        <dd className="text-right tabular-nums">{formatPosMoney(summary.expectedCash)}</dd>
        <dt>Efectivo contado</dt>
        <dd className="text-right tabular-nums">{summary.countedCash === null ? '—' : formatPosMoney(summary.countedCash)}</dd>
        <dt>Diferencia</dt>
        <dd className="text-right tabular-nums">{summary.cashDifference === null ? '—' : differenceLabel(summary.cashDifference)}</dd>
      </dl>
      <div className="flex gap-2">
        <Button className="min-h-11" onClick={() => setPrinting(true)}>Imprimir cierre</Button>
        <Button className="min-h-11" variant="outline" onClick={onOpenAnother}>Abrir otra caja</Button>
      </div>
      {printing && (
        <TicketPrintPortal title={`Cierre ${summary.cashierName}`} onDone={() => setPrinting(false)}>
          <ClosingTicket summary={summary} />
        </TicketPrintPortal>
      )}
    </section>
  )
}
```

`src/features/pos/components/close-session-form.tsx`:

```tsx
import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api-client'

import { useCloseSession, useFetchSessionSummary } from '../hooks/use-close-session'
import { describePosFailure } from '../services/pos.api'
import type { PosOpenSession, SessionSummary } from '../types/pos'
import { PAYMENT_METHOD_LABELS } from '../types/pos'
import { formatPosMoney } from '../utils/format-pos-money'
import { toCents } from '../utils/money-cents'

import { differenceLabel } from './closing-summary'
import { PosMoneyInput } from './pos-money-input'

const MAX_COUNTED = 1_000_000_000
const MOVED_MESSAGE = 'Entraron movimientos mientras contabas. Revisa el efectivo esperado antes de cerrar.'

export function CloseSessionForm({
  session,
  onClosed,
  onReload,
}: {
  session: PosOpenSession
  onClosed: (summary: SessionSummary) => void
  onReload: () => void
}) {
  const close = useCloseSession()
  const fetchSummary = useFetchSessionSummary()
  const [counted, setCounted] = useState<number | null>(null)
  const [note, setNote] = useState('')
  const [message, setMessage] = useState<string | null>(null)

  // Live difference in integer cents; its tone is color AND text, never color alone.
  const difference = counted === null ? null : (toCents(counted) - toCents(session.expectedCash)) / 100

  async function submit() {
    if (counted === null) return
    setMessage(null)
    try {
      onClosed(
        await close.mutateAsync({ sessionId: session.id, version: session.version, countedCash: counted, note: note.trim() || null }),
      )
    } catch (error) {
      const lostAnswer =
        error instanceof ApiError && (error.status === 412 || error.code === 'pos.session.not_open')
      if (!lostAnswer) {
        setMessage(describePosFailure(error))
        return
      }
      const current = await fetchSummary(session.id).catch(() => null)
      if (current?.status === 'Closed') {
        onClosed(current)
        return
      }
      setMessage(MOVED_MESSAGE)
      onReload()
    }
  }

  return (
    <section className="mx-auto max-w-md space-y-4">
      <h1 className="text-lg font-semibold">Cerrar caja</h1>
      <dl className="grid grid-cols-2 gap-2 text-sm">
        <dt>Base</dt>
        <dd className="text-right tabular-nums">{formatPosMoney(session.openingFloat)}</dd>
        <dt>Ventas</dt>
        <dd className="text-right tabular-nums">{session.salesCount}</dd>
        <dt>Anuladas</dt>
        <dd className="text-right tabular-nums">{session.voidedCount}</dd>
        {session.paymentTotals.map((total) => (
          <div key={total.method} className="contents">
            <dt>{PAYMENT_METHOD_LABELS[total.method]}</dt>
            <dd className="text-right tabular-nums">{formatPosMoney(total.amount)}</dd>
          </div>
        ))}
        <dt>Efectivo esperado</dt>
        <dd className="text-right font-semibold tabular-nums">{formatPosMoney(session.expectedCash)}</dd>
      </dl>
      <PosMoneyInput id="pos-counted" label="Efectivo contado" value={counted} onChange={setCounted} max={MAX_COUNTED} autoFocus />
      {difference !== null && (
        <p
          aria-live="polite"
          className={difference < 0 ? 'text-destructive' : difference > 0 ? 'text-warning-foreground' : 'text-primary'}
        >
          {differenceLabel(difference)}
        </p>
      )}
      <div className="space-y-1">
        <Label htmlFor="pos-close-note">Nota (opcional)</Label>
        <Textarea id="pos-close-note" maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} />
      </div>
      {message && (
        <p role="alert" className="text-sm">
          {message}
        </p>
      )}
      <Button className="min-h-11 w-full" disabled={counted === null || close.isPending} onClick={() => void submit()}>
        Cerrar caja
      </Button>
    </section>
  )
}
```

`src/features/pos/pages/pos-close-session-page.tsx`:

```tsx
import { Link, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'

import { PageContainer } from '@/components/page-container'
import { Card } from '@/components/ui/card'

import { ClosingSummary } from '../components/closing-summary'
import { CloseSessionForm } from '../components/close-session-form'
import { PosCartProvider, usePosCart, usePosCartStoreInstance, useResolvedPosCartStore } from '../hooks/use-pos-cart'
import { useRegisterContext } from '../hooks/use-register-context'
import type { SessionSummary } from '../types/pos'

export function PosCloseSessionPage() {
  const store = useResolvedPosCartStore()
  return (
    <PageContainer variant="form">
      <h1 className="sr-only">Cerrar caja</h1>
      {store ? (
        <PosCartProvider store={store}>
          <CloseSessionScreen />
        </PosCartProvider>
      ) : (
        <Card className="p-8 text-center text-sm text-muted-foreground">Cargando…</Card>
      )}
    </PageContainer>
  )
}

function CloseSessionScreen() {
  const navigate = useNavigate()
  const store = usePosCartStoreInstance()
  const attempt = usePosCart((state) => state.attempt)
  const { context, isLoading, isError, refetch } = useRegisterContext()
  const [closed, setClosed] = useState<SessionSummary | null>(null)

  if (attempt) {
    return (
      <Card className="space-y-3 p-8 text-center text-sm">
        <p>Hay un cobro sin confirmar. Resuélvelo en la caja antes de cerrar.</p>
        <Link to="/pos">Ir a la caja</Link>
      </Card>
    )
  }

  if (closed) {
    return <ClosingSummary summary={closed} onOpenAnother={() => void navigate({ to: '/pos' })} />
  }

  if (isLoading) return <Card className="p-8 text-center text-sm text-muted-foreground">Cargando la caja…</Card>
  if (isError || !context) {
    return (
      <Card className="p-8 text-center text-sm">
        No pudimos cargar la caja. <button type="button" onClick={refetch}>Reintentar</button>
      </Card>
    )
  }

  if (!context.session) {
    return (
      <Card className="space-y-3 p-8 text-center text-sm">
        <p>No tienes una caja abierta.</p>
        <Link to="/pos">Ir a la caja</Link>
      </Card>
    )
  }

  return (
    <CloseSessionForm
      session={context.session}
      onReload={refetch}
      onClosed={(summary) => {
        // "Cerrar caja" confirmed: the products left in the cart are discarded (spec, «Cerrar caja con carrito»).
        store.getState().clear()
        setClosed(summary)
      }}
    />
  )
}
```

(`useNavigate` necesita el router: en la prueba, agrega `useNavigate: () => vi.fn()` al mock de `@tanstack/react-router`.)

Después, con `impeccable` y `frontend-design`, ajusta composición, jerarquía y estados de los tres componentes sobre el tema botánico; las pruebas no cambian.

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/components/open-session-form.tsx src/features/pos/components/open-session-form.test.tsx src/features/pos/components/close-session-form.tsx src/features/pos/components/closing-summary.tsx src/features/pos/pages/pos-close-session-page.tsx src/features/pos/pages/pos-close-session-page.test.tsx src/features/pos/hooks/use-close-session.ts; git commit -m "feat(pos): apertura y cierre de caja"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F12: Diálogo de cobro y su máquina de estados *(con skills de diseño)*

**Files:**
- Create: `src/features/pos/hooks/use-pay-flow.ts`, `src/features/pos/components/pay-dialog.tsx`, `src/features/pos/components/payment-method-buttons.tsx`, `src/features/pos/components/payment-lines.tsx`, `src/features/pos/components/cash-quick-amounts.tsx`
- Test: `src/features/pos/components/pay-dialog.test.tsx`

**Interfaces:**
- Consumes: F3 (`build-payments`), F4 (`classifyCreateSaleOutcome`), F5 (store), F7 (`useCreateSale`, `useFetchSale`, `invalidatePosPreview`, `posRegisterKey`), F8 (`PosMoneyInput`).
- Produces:
  - `usePayFlow({ total: number; sessionId: string | null; onSuccess(sale: PosSale): void }): { mode: 'editing' | 'sending' | 'uncertain'; lines; setLines; summary; message: string | null; confirm(): Promise<void>; retry(): Promise<void>; verify(): Promise<void> }`
  - `PayDialog({ open: boolean; total: number; sessionId: string | null; onSuccess(sale: PosSale): void; onClose(): void })`
  - Mensajes: `UNCERTAIN_MESSAGE = 'No pudimos confirmar si la venta quedó registrada.'`, `RETRY_FORBIDDEN_MESSAGE`, `VERIFY_NOT_FOUND_MESSAGE`, `VERIFY_FAILED_MESSAGE`, `SESSION_EXPIRED_MESSAGE`

Antes del Step 3, carga `impeccable`, `frontend-design` y `emil-design-eng`: total grande arriba, botones de medio ≥ 44 px, «Confirmar venta» ≥ 56 px, «Falta por cubrir» y «Cambio» en una región `aria-live`, estado incierto que no se puede descartar (sin «Cancelar», Esc y clic afuera no hacen nada).

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/components/pay-dialog.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'

import { PosCartProvider } from '../hooks/use-pos-cart'
import { createPosCartStore, posCartStorageKey, type PosCartStore } from '../stores/pos-cart-store'
import { workedExampleSale } from '../utils/pos-sale.fixture'

import { PayDialog } from './pay-dialog'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function seededStore(): PosCartStore {
  const store = createPosCartStore(posCartStorageKey('t-1', 'u-1'))
  store.getState().adoptSession('c-1')
  store.getState().addProduct({ id: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', imageUrl: null, unitPrice: 11900, taxPercentage: 19 })
  store.getState().openPay()
  return store
}

function renderDialog(store: PosCartStore, total = 11900, handlers = { onSuccess: vi.fn(), onClose: vi.fn() }) {
  const queryClient = new QueryClient()
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  render(
    <QueryClientProvider client={queryClient}>
      <PosCartProvider store={store}>
        <PayDialog open total={total} sessionId="c-1" onSuccess={handlers.onSuccess} onClose={handlers.onClose} />
      </PosCartProvider>
    </QueryClientProvider>,
  )
  return { ...handlers, invalidate }
}

const salePosts = () =>
  vi.mocked(fetch).mock.calls.filter(([input, init]) => String(input).endsWith('/pos/sales') && init?.method === 'POST')
const bodyOf = (call: Parameters<typeof fetch>) => JSON.parse(String(call[1]?.body))

function typeTendered(value: string) {
  fireEvent.change(screen.getByLabelText('Recibido'), { target: { value } })
}

describe('PayDialog', () => {
  it('keeps confirm disabled while anything is missing and shows the live change in cents', () => {
    renderDialog(seededStore())

    typeTendered('11899,99')
    expect(screen.getByRole('button', { name: /Confirmar venta/ })).toBeDisabled()
    expect(screen.getByText('$\u00a00,01')).toBeVisible()

    typeTendered('20000')
    expect(screen.getByRole('button', { name: /Confirmar venta/ })).toBeEnabled()
    expect(screen.getByText('$\u00a08.100')).toBeVisible()
  })

  it('a double click sends a single request', async () => {
    vi.mocked(fetch).mockReturnValue(new Promise(() => {}))
    renderDialog(seededStore())
    typeTendered('20000')

    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta|Registrando/ }))

    await waitFor(() => expect(salePosts()).toHaveLength(1))
  })

  it('a network error locks the dialog and the cart; Reintentar resends the same id and body', async () => {
    vi.mocked(fetch)
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(json(200, workedExampleSale()))
    const store = seededStore()
    const { onClose, onSuccess } = renderDialog(store)
    typeTendered('20000')

    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByText('No pudimos confirmar si la venta quedó registrada.')
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })
    expect(onClose).not.toHaveBeenCalled()
    expect(store.getState().addProduct({ id: 'x', code: 'X', name: 'X', imageUrl: null, unitPrice: 1, taxPercentage: 0 })).toBe('locked')

    fireEvent.click(screen.getByRole('button', { name: 'Reintentar' }))
    await waitFor(() => expect(onSuccess).toHaveBeenCalled())
    const [first, second] = salePosts()
    expect(bodyOf(second)).toEqual(bodyOf(first))
    expect(store.getState().attempt).toBeNull()
  })

  it.each([
    [404, 'pos.sale.not_found', 'Todavía no aparece. Reintentar la registra una sola vez.'],
    [403, 'authorization.denied', 'No pudimos verificar la venta. Reintentar la registra una sola vez.'],
  ])('Verificar answered %s stays locked', async (status, code, message) => {
    vi.mocked(fetch).mockImplementation((input, init) => {
      if (init?.method === 'POST') return Promise.reject(new TypeError('Failed to fetch'))
      return Promise.resolve(json(status, { code }))
    })
    const store = seededStore()
    renderDialog(store)
    typeTendered('20000')
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByRole('button', { name: 'Verificar' })

    fireEvent.click(screen.getByRole('button', { name: 'Verificar' }))

    expect(await screen.findByText(message)).toBeVisible()
    expect(store.getState().attempt).toMatchObject({ status: 'uncertain' })
  })

  it('Verificar answered 200 goes to success', async () => {
    vi.mocked(fetch).mockImplementation((input, init) =>
      init?.method === 'POST' ? Promise.reject(new TypeError('Failed to fetch')) : Promise.resolve(json(200, workedExampleSale())),
    )
    const store = seededStore()
    const { onSuccess } = renderDialog(store)
    typeTendered('20000')
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByRole('button', { name: 'Verificar' })

    fireEvent.click(screen.getByRole('button', { name: 'Verificar' }))

    await waitFor(() => expect(onSuccess).toHaveBeenCalledWith(expect.objectContaining({ saleNumber: 'POS-000042' })))
    expect(store.getState().lines).toEqual([])
  })

  // Spec, ronda 4, MAJOR 2.
  it('Reintentar answered 403 authorization.denied stays locked with the same id and body', async () => {
    vi.mocked(fetch)
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(json(403, { code: 'authorization.denied' }))
    const store = seededStore()
    const { onClose } = renderDialog(store)
    typeTendered('20000')
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByRole('button', { name: 'Reintentar' })
    const attemptBefore = store.getState().attempt

    fireEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(
      await screen.findByText(
        'No tienes permiso para registrar ventas en este momento. La venta sigue sin confirmar: cuando te devuelvan el acceso, reintenta o verifica.',
      ),
    ).toBeVisible()
    expect(store.getState().attempt).toMatchObject({ saleId: attemptBefore?.saleId, body: attemptBefore?.body, status: 'uncertain' })
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' })
    expect(onClose).not.toHaveBeenCalled()
  })

  it('Reintentar answered 422 session_mismatch frees the cart and closes the charge', async () => {
    vi.mocked(fetch)
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(json(422, { code: 'pos.sale.session_mismatch' }))
    const store = seededStore()
    renderDialog(store)
    typeTendered('20000')
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByRole('button', { name: 'Reintentar' })

    fireEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await waitFor(() => expect(store.getState().attempt).toBeNull())
    expect(store.getState().payOpen).toBe(false)
    expect(store.getState().lines).toHaveLength(1)
  })

  it('a 422 on the first send gives the next charge a new id', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(json(422, { code: 'pos.sale.payment_insufficient' }))
      .mockReturnValueOnce(new Promise(() => {}))
    renderDialog(seededStore())
    typeTendered('20000')

    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByText('Los pagos no cubren el total.')
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    await waitFor(() => expect(salePosts()).toHaveLength(2))
    expect(bodyOf(salePosts()[1]).id).not.toBe(bodyOf(salePosts()[0]).id)
  })

  it('price_changed closes the charge and invalidates the preview', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(422, { code: 'pos.sale.price_changed' }))
    const store = seededStore()
    const { invalidate } = renderDialog(store)
    typeTendered('20000')

    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    await waitFor(() => expect(store.getState().payOpen).toBe(false))
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['pos', 't-1', 'preview'] })
    expect(store.getState().pendingSaleId).toBeNull()
  })

  it.each([401, 409])('a %s stays uncertain', async (status) => {
    vi.mocked(fetch).mockResolvedValueOnce(json(status, {}))
    const store = seededStore()
    renderDialog(store)
    typeTendered('20000')

    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    await waitFor(() => expect(store.getState().attempt).toMatchObject({ status: 'uncertain' }))
  })

  it('a card-only charge sends only the card', async () => {
    vi.mocked(fetch).mockReturnValue(new Promise(() => {}))
    renderDialog(seededStore())

    fireEvent.click(screen.getByRole('button', { name: 'Tarjeta' }))
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    await waitFor(() => expect(salePosts()).toHaveLength(1))
    expect(bodyOf(salePosts()[0]).payments).toEqual([{ method: 'Card', amount: 11900, reference: null }])
  })

  it('with a zero total the tendered is fixed at $0 and travels as tendered 0', async () => {
    vi.mocked(fetch).mockReturnValue(new Promise(() => {}))
    renderDialog(seededStore(), 0)

    expect(screen.getByLabelText('Recibido')).toBeDisabled()
    expect(screen.queryByRole('button', { name: /Otro medio de pago/ })).toBeNull()
    await act(async () => fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ })))

    expect(bodyOf(salePosts()[0]).payments).toEqual([{ method: 'Cash', tendered: 0 }])
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/components/pay-dialog.test.tsx
```

Esperado: FAIL, `Failed to resolve import "./pay-dialog"`.

- [ ] **Step 3: Implementar la máquina de estados**

`src/features/pos/hooks/use-pay-flow.ts`:

```ts
import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import { ApiError, UnauthorizedError } from '@/lib/api-client'

import { describePosFailure } from '../services/pos.api'
import type { CreatePosSaleRequest, PosSale } from '../types/pos'
import { buildPaymentsBody, initialPaymentLines, paymentSummary, type PaymentLine } from '../utils/build-payments'
import { toCents } from '../utils/money-cents'
import { classifyCreateSaleOutcome } from '../utils/sale-attempt-outcome'

import { useCreateSale, useFetchSale } from './use-create-sale'
import { usePosCart, usePosCartStoreInstance } from './use-pos-cart'
import { posRegisterKey } from './use-register-context'
import { invalidatePosPreview } from './use-sale-preview'

export const UNCERTAIN_MESSAGE = 'No pudimos confirmar si la venta quedó registrada.'
export const RETRY_FORBIDDEN_MESSAGE =
  'No tienes permiso para registrar ventas en este momento. La venta sigue sin confirmar: cuando te devuelvan el acceso, reintenta o verifica.'
export const VERIFY_NOT_FOUND_MESSAGE = 'Todavía no aparece. Reintentar la registra una sola vez.'
export const VERIFY_FAILED_MESSAGE = 'No pudimos verificar la venta. Reintentar la registra una sola vez.'
export const SESSION_EXPIRED_MESSAGE = 'Tu sesión expiró. Vuelve a iniciar sesión y reintenta: la venta sigue sin confirmar.'

function uncertainMessage(error: unknown): string {
  if (error instanceof UnauthorizedError) return SESSION_EXPIRED_MESSAGE
  if (error instanceof ApiError && error.status === 403) return RETRY_FORBIDDEN_MESSAGE
  return UNCERTAIN_MESSAGE
}

export function usePayFlow({ total, sessionId, onSuccess }: { total: number; sessionId: string | null; onSuccess: (sale: PosSale) => void }) {
  const store = usePosCartStoreInstance()
  const attempt = usePosCart((state) => state.attempt)
  const createSale = useCreateSale()
  const fetchSale = useFetchSale()
  const queryClient = useQueryClient()
  const { tenantId } = useActiveTenant()
  const [lines, setLines] = useState<PaymentLine[]>(initialPaymentLines)
  const [message, setMessage] = useState<string | null>(null)

  const totalCents = toCents(total)
  const summary = useMemo(() => paymentSummary(lines, totalCents), [lines, totalCents])
  const mode = attempt === null ? 'editing' : attempt.status === 'inFlight' ? 'sending' : 'uncertain'

  const send = useCallback(
    async (body: CreatePosSaleRequest) => {
      setMessage(null)
      const wasUncertain = store.getState().attempt?.wasUncertain ?? false
      const result = await createSale(body)
      const outcome = classifyCreateSaleOutcome(result.kind === 'ok' ? { kind: 'ok' } : result, wasUncertain)
      store.getState().resolveAttempt(outcome)

      if (result.kind === 'ok') {
        onSuccess(result.sale)
        return
      }
      if (outcome === 'uncertain') {
        setMessage(uncertainMessage(result.error))
        return
      }

      // Definitive: nothing was stored. The message stays in the dialog, except for the two cases
      // that close it to let the cashier fix the cart or reopen the cash session.
      const code = result.error instanceof ApiError ? result.error.code : undefined
      setMessage(describePosFailure(result.error))
      if (code === 'pos.sale.price_changed') {
        store.getState().closePay()
        // The cart did not change, so the preview key did not either: force a fresh one.
        void invalidatePosPreview(queryClient, tenantId)
      } else if (code === 'pos.sale.session_mismatch' || code === 'pos.session.not_open') {
        store.getState().closePay()
        void queryClient.invalidateQueries({ queryKey: posRegisterKey(tenantId) })
      }
    },
    [createSale, onSuccess, queryClient, store, tenantId],
  )

  const confirm = useCallback(async () => {
    const state = store.getState()
    if (state.attempt !== null || !summary.covers || !sessionId) return
    const body: CreatePosSaleRequest = {
      id: state.ensurePendingSaleId(),
      cashSessionId: sessionId,
      lines: state.lines.map((line) => ({
        productId: line.productId,
        quantity: line.quantity,
        discountPercentage: line.discountPercentage,
        expectedUnitPrice: line.expectedUnitPrice,
        expectedTaxPercentage: line.expectedTaxPercentage,
      })),
      payments: buildPaymentsBody(lines, totalCents),
    }
    // Freeze the exact body before sending: a retry can only resend this one.
    state.beginAttempt(body)
    await send(body)
  }, [lines, send, sessionId, store, summary.covers, totalCents])

  const retry = useCallback(async () => {
    const current = store.getState().attempt
    if (!current || current.status !== 'uncertain') return
    store.getState().retryAttempt()
    await send(current.body)
  }, [send, store])

  // Only a 200 gets out of the lock this way: a 404 can still be a request on its way, and a 403
  // says nothing about whether the sale landed.
  const verify = useCallback(async () => {
    const current = store.getState().attempt
    if (!current) return
    const result = await fetchSale(current.saleId)
    if (result.kind === 'ok') {
      store.getState().resolveAttempt('success')
      onSuccess(result.sale)
      return
    }
    setMessage(
      result.error instanceof ApiError && result.error.status === 404 ? VERIFY_NOT_FOUND_MESSAGE : VERIFY_FAILED_MESSAGE,
    )
  }, [fetchSale, onSuccess, store])

  return { mode, lines, setLines, summary, message, confirm, retry, verify }
}
```

- [ ] **Step 4: Implementar el diálogo (comportamiento fijo; forma con las skills)**

`src/features/pos/components/cash-quick-amounts.tsx`:

```tsx
import { Button } from '@/components/ui/button'

import { formatPosMoney } from '../utils/format-pos-money'
import { fromCents } from '../utils/money-cents'
import { quickCashAmounts } from '../utils/quick-cash-amounts'

export function CashQuickAmounts({ dueCents, onPick }: { dueCents: number; onPick: (value: number) => void }) {
  const amounts = quickCashAmounts(dueCents)
  return (
    <div className="flex flex-wrap gap-2">
      {amounts.map((cents, index) => (
        <Button key={cents} type="button" variant="outline" className="min-h-11" onClick={() => onPick(fromCents(cents))}>
          {index === 0 ? `Exacto ${formatPosMoney(fromCents(cents))}` : formatPosMoney(fromCents(cents))}
        </Button>
      ))}
    </div>
  )
}
```

`src/features/pos/components/payment-method-buttons.tsx`:

```tsx
import { Button } from '@/components/ui/button'

import type { PaymentMethod } from '../types/pos'
import { PAYMENT_METHOD_LABELS } from '../types/pos'

const METHODS: PaymentMethod[] = ['Cash', 'Card', 'Transfer']

export function PaymentMethodButtons({ onPick, disabled }: { onPick: (method: PaymentMethod) => void; disabled?: boolean }) {
  return (
    <div className="grid grid-cols-3 gap-2">
      {METHODS.map((method) => (
        <Button key={method} type="button" variant="outline" className="min-h-11" disabled={disabled} onClick={() => onPick(method)}>
          {PAYMENT_METHOD_LABELS[method]}
        </Button>
      ))}
    </div>
  )
}
```

`src/features/pos/components/payment-lines.tsx`:

```tsx
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

import { PAYMENT_METHOD_LABELS } from '../types/pos'
import { removePaymentLine, updatePaymentLine, type PaymentLine } from '../utils/build-payments'
import { fromCents, toCents } from '../utils/money-cents'

import { CashQuickAmounts } from './cash-quick-amounts'
import { PosMoneyInput } from './pos-money-input'

const MAX_TENDERED = 100_000_000

export function PaymentLines({
  lines,
  onChange,
  totalCents,
  cashDueCents,
  onConfirm,
  disabled,
}: {
  lines: PaymentLine[]
  onChange: (lines: PaymentLine[]) => void
  totalCents: number
  cashDueCents: number
  onConfirm: () => void
  disabled: boolean
}) {
  const zeroTotal = totalCents === 0
  return (
    <ul className="space-y-3">
      {lines.map((line) => {
        const removable = lines.length > 1 && !disabled
        if (line.method === 'Cash') {
          return (
            <li key={line.key} className="space-y-2">
              <PosMoneyInput
                id={`tendered-${line.key}`}
                label="Recibido"
                value={zeroTotal ? 0 : line.tendered}
                onChange={(tendered) => onChange(updatePaymentLine(lines, line.key, { tendered }))}
                onConfirm={onConfirm}
                disabled={disabled || zeroTotal}
                max={MAX_TENDERED}
                autoFocus
              />
              {!zeroTotal && !disabled && (
                <CashQuickAmounts
                  dueCents={cashDueCents}
                  onPick={(tendered) => onChange(updatePaymentLine(lines, line.key, { tendered }))}
                />
              )}
              {removable && (
                <Button type="button" variant="ghost" onClick={() => onChange(removePaymentLine(lines, line.key))} aria-label="Quitar efectivo">
                  ✕
                </Button>
              )}
            </li>
          )
        }

        // A card or transfer cannot exceed what is missing: the input caps it and says why.
        const others = lines.reduce((sum, other) => (other.key === line.key || other.method === 'Cash' ? sum : sum + toCents(other.amount ?? 0)), 0)
        const cap = fromCents(Math.max(0, totalCents - others))
        return (
          <li key={line.key} className="space-y-2">
            <PosMoneyInput
              id={`amount-${line.key}`}
              label={`Monto ${PAYMENT_METHOD_LABELS[line.method].toLowerCase()}`}
              value={line.amount}
              onChange={(amount) => onChange(updatePaymentLine(lines, line.key, { amount }))}
              onConfirm={onConfirm}
              disabled={disabled}
              max={cap}
            />
            <div className="space-y-1">
              <Label htmlFor={`reference-${line.key}`}>Referencia (opcional)</Label>
              <Input
                id={`reference-${line.key}`}
                maxLength={60}
                disabled={disabled}
                value={line.reference}
                onChange={(event) => onChange(updatePaymentLine(lines, line.key, { reference: event.target.value }))}
              />
            </div>
            {removable && (
              <Button type="button" variant="ghost" onClick={() => onChange(removePaymentLine(lines, line.key))} aria-label={`Quitar ${PAYMENT_METHOD_LABELS[line.method].toLowerCase()}`}>
                ✕
              </Button>
            )}
          </li>
        )
      })}
    </ul>
  )
}
```

`src/features/pos/components/pay-dialog.tsx`:

```tsx
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'

import { usePayFlow } from '../hooks/use-pay-flow'
import type { PosSale } from '../types/pos'
import { addPaymentLine, replaceSingleLineMethod } from '../utils/build-payments'
import { formatPosMoney } from '../utils/format-pos-money'
import { fromCents, toCents } from '../utils/money-cents'

import { PaymentLines } from './payment-lines'
import { PaymentMethodButtons } from './payment-method-buttons'

export function PayDialog({
  open,
  total,
  sessionId,
  onSuccess,
  onClose,
}: {
  open: boolean
  total: number
  sessionId: string | null
  onSuccess: (sale: PosSale) => void
  onClose: () => void
}) {
  const flow = usePayFlow({ total, sessionId, onSuccess })
  const totalCents = toCents(total)
  const locked = flow.mode !== 'editing'

  return (
    <Dialog
      open={open}
      // With an attempt in flight or uncertain the dialog cannot be dismissed: only a definitive
      // answer for that id unlocks it (spec, «Intento incierto»).
      onOpenChange={(next) => {
        if (!next && !locked) onClose()
      }}
    >
      <DialogContent
        onEscapeKeyDown={(event) => locked && event.preventDefault()}
        onInteractOutside={(event) => locked && event.preventDefault()}
      >
        <DialogTitle>Cobrar</DialogTitle>
        <DialogDescription>
          Total a cobrar <span className="text-3xl font-semibold tabular-nums">{formatPosMoney(total)}</span>
        </DialogDescription>

        {flow.mode === 'uncertain' ? (
          <div className="space-y-4">
            <p role="alert">{flow.message ?? 'No pudimos confirmar si la venta quedó registrada.'}</p>
            <div className="flex gap-2">
              <Button className="min-h-14 flex-1" onClick={() => void flow.retry()}>Reintentar</Button>
              <Button className="min-h-14 flex-1" variant="outline" onClick={() => void flow.verify()}>Verificar</Button>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            {totalCents > 0 && (
              <PaymentMethodButtons
                disabled={locked}
                onPick={(method) => flow.setLines(replaceSingleLineMethod(flow.lines, method, totalCents))}
              />
            )}
            <PaymentLines
              lines={flow.lines}
              onChange={flow.setLines}
              totalCents={totalCents}
              cashDueCents={flow.summary.cashDueCents}
              onConfirm={() => void flow.confirm()}
              disabled={locked}
            />
            <dl aria-live="polite" className="grid grid-cols-2 gap-1 text-sm">
              <dt>Falta por cubrir</dt>
              <dd className="text-right tabular-nums">{formatPosMoney(fromCents(flow.summary.missingCents))}</dd>
              <dt>Cambio</dt>
              <dd className="text-right text-lg font-semibold tabular-nums">{formatPosMoney(fromCents(flow.summary.changeCents))}</dd>
            </dl>
            {totalCents > 0 && flow.lines.length < 5 && !locked && (
              // Without a cash line the new line is cash (the common "card + cash" split); with one,
              // it is a card, which the cashier can switch per line.
              <Button
                type="button"
                variant="ghost"
                onClick={() =>
                  flow.setLines(
                    addPaymentLine(flow.lines, flow.lines.some((line) => line.method === 'Cash') ? 'Card' : 'Cash', totalCents),
                  )
                }
              >
                + Otro medio de pago
              </Button>
            )}
            {flow.message && <p role="alert" className="text-sm text-destructive">{flow.message}</p>}
            <div className="flex gap-2">
              <Button type="button" variant="outline" className="min-h-14" disabled={locked} onClick={onClose}>
                Cancelar <kbd className="ml-2 text-xs">Esc</kbd>
              </Button>
              <Button
                type="button"
                className="min-h-14 flex-1"
                disabled={!flow.summary.covers || locked}
                onClick={() => void flow.confirm()}
              >
                {flow.mode === 'sending' ? 'Registrando…' : 'Confirmar venta'} <kbd className="ml-2 text-xs">Enter</kbd>
              </Button>
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
```

(Si `DialogContent` del proyecto no reenvía `onEscapeKeyDown` / `onInteractOutside` a Radix, pásalos por la prop que exponga o extiende el wrapper de `components/ui/dialog.tsx` para reenviarlos: es un cambio de dos líneas en un primitivo compartido, con su propia prueba en `dialog`.)

Después, con `impeccable`, `frontend-design` y `emil-design-eng`, ajusta la composición sobre el tema botánico (total dominante, medios de pago como segmentos, estado incierto visualmente distinto y calmo). Las pruebas no cambian.

- [ ] **Step 5: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos/hooks/use-pay-flow.ts src/features/pos/components/pay-dialog.tsx src/features/pos/components/pay-dialog.test.tsx src/features/pos/components/payment-method-buttons.tsx src/features/pos/components/payment-lines.tsx src/features/pos/components/cash-quick-amounts.tsx; git commit -m "feat(pos): diálogo de cobro"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---


### Task F13: Pantalla de caja *(con skills de diseño)*

**Files:**
- Create: `src/features/pos/utils/charge-state.ts`, `src/features/pos/hooks/use-expected-refresh.ts`, `src/features/pos/components/{register-header,product-search-bar,product-grid,product-card,cart-panel,cart-line,quantity-stepper,line-discount-popover,cart-totals,sale-success-panel}.tsx`
- Modify: `src/features/pos/pages/pos-register-page.tsx` (reemplaza el cascarón)
- Test: `src/features/pos/utils/charge-state.test.ts`, `src/features/pos/hooks/use-expected-refresh.test.tsx`, `src/features/pos/pages/pos-register-page.test.tsx`

**Interfaces:**
- Consumes: F1-F12.
- Produces:
  - `chargeState({ lines, pendingScans, preview, previewCurrent, previewError }): { enabled: boolean; label: 'Cobrar' | 'Calculando el total…'; reason: string | null }`
  - `type ExpectedChangeNotice = { productId; name; fromPrice; toPrice; fromTax; toTax }`, `describeExpectedChange(notice): string`, `useExpectedRefresh(preview, previewCurrent): { notices: ExpectedChangeNotice[]; dismiss(): void }`
  - `PosRegisterPage` (la caja completa)

Antes del Step 4, carga `impeccable`, `frontend-design` y `emil-design-eng`. Lo que fijan el spec y las pruebas no se negocia: `h-full` sin `PageContainer`, `grid-cols-[1fr_400px]` desde `lg` y carrito como panel inferior plegable debajo de `lg` con total y «Cobrar» siempre visibles; tarjeta de producto 1:1 con `object-cover` e inicial como placeholder, código en `font-mono text-xs`; resaltado de línea de 600 ms con `bg-accent` que se desvanece (sin animación con `prefers-reduced-motion`); «Cobrar» ≥ 56 px; `kbd` y `Tooltip` en los atajos; tokens del tema (`bg-background`, `bg-card`, `text-primary`, `border-border`). Si la grilla necesita `scroll-area`: `bunx shadcn@latest add scroll-area` (estilo `radix-nova`).

- [ ] **Step 1: Escribir las pruebas de lógica que fallan**

`src/features/pos/utils/charge-state.test.ts`:

```ts
import type { PosCartLine } from '../stores/pos-cart-store'
import type { PreviewLine, SalePreview } from '../types/pos'

import { chargeState } from './charge-state'

const line = (overrides: Partial<PosCartLine> = {}): PosCartLine => ({
  lineId: 'l-1', productId: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', imageUrl: null,
  quantity: 1, discountPercentage: 0, expectedUnitPrice: 11900, expectedTaxPercentage: 19, ...overrides,
})
const previewLine = (overrides: Partial<PreviewLine> = {}): PreviewLine => ({
  productId: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', quantity: 1, unitPrice: 11900, taxPercentage: 19,
  discountPercentage: 0, discountAmount: 0, taxAmount: 1900, subtotal: 10000, lineTotal: 11900,
  sellable: true, unsellableReason: null, ...overrides,
})
const preview = (lines: PreviewLine[], overrides: Partial<SalePreview> = {}): SalePreview => ({
  lines, subtotal: 10000, taxAmount: 1900, discountAmount: 0, total: 11900, zeroTotalNotAllowed: false, ...overrides,
})
const base = { pendingScans: 0, previewCurrent: true, previewError: false }

describe('chargeState', () => {
  it('enables Cobrar only for a current, sellable, matching preview', () => {
    expect(chargeState({ ...base, lines: [line()], preview: preview([previewLine()]) })).toEqual({ enabled: true, label: 'Cobrar', reason: null })
  })

  it('blocks with a reason for every case the spec lists', () => {
    expect(chargeState({ ...base, lines: [], preview: undefined }).enabled).toBe(false)
    expect(chargeState({ ...base, pendingScans: 1, lines: [line()], preview: preview([previewLine()]) }).enabled).toBe(false)
    expect(chargeState({ ...base, previewCurrent: false, lines: [line()], preview: preview([previewLine()]) })).toMatchObject({ enabled: false, label: 'Calculando el total…' })
    expect(chargeState({ ...base, previewError: true, lines: [line()], preview: undefined }).reason).toBe('No pudimos actualizar los totales')
    expect(chargeState({ ...base, lines: [line()], preview: preview([previewLine({ sellable: false, unsellableReason: 'Inactive' })]) }).reason).toBe(
      'Quita los productos que no se pueden vender',
    )
    expect(chargeState({ ...base, lines: [line()], preview: preview([previewLine({ taxPercentage: 5 })]) })).toMatchObject({ enabled: false, label: 'Calculando el total…' })
    expect(chargeState({ ...base, lines: [line()], preview: preview([previewLine()], { zeroTotalNotAllowed: true }) }).reason).toBe(
      'Una venta en $0 necesita el permiso de descuentos.',
    )
  })
})
```

`src/features/pos/hooks/use-expected-refresh.test.tsx`:

```tsx
import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'

import { createPosCartStore, posCartStorageKey } from '../stores/pos-cart-store'
import type { SalePreview } from '../types/pos'

import { PosCartProvider } from './use-pos-cart'
import { describeExpectedChange, useExpectedRefresh } from './use-expected-refresh'

const previewWith = (unitPrice: number, taxPercentage: number): SalePreview => ({
  lines: [{ productId: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', quantity: 1, unitPrice, taxPercentage, discountPercentage: 0, discountAmount: 0, taxAmount: 0, subtotal: 0, lineTotal: unitPrice, sellable: true, unsellableReason: null }],
  subtotal: 0, taxAmount: 0, discountAmount: 0, total: unitPrice, zeroTotalNotAllowed: false,
})

function setup() {
  const store = createPosCartStore(posCartStorageKey('t-1', 'u-1'))
  store.getState().addProduct({ id: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', imageUrl: null, unitPrice: 11900, taxPercentage: 19 })
  const wrapper = ({ children }: { children: ReactNode }) => <PosCartProvider store={store}>{children}</PosCartProvider>
  return { store, wrapper }
}

describe('useExpectedRefresh', () => {
  it('applies a tax-only change and explains it', () => {
    const { store, wrapper } = setup()

    const { result } = renderHook(() => useExpectedRefresh(previewWith(11900, 5), true), { wrapper })

    expect(store.getState().lines[0]).toMatchObject({ expectedUnitPrice: 11900, expectedTaxPercentage: 5 })
    expect(result.current.notices.map(describeExpectedChange)).toEqual(['El IVA de Shampoo 400 ml cambió del 19 % al 5 %.'])
  })

  it('waits while the cart is locked and applies once it unlocks', () => {
    const { store, wrapper } = setup()
    store.getState().openPay()

    const { result } = renderHook(() => useExpectedRefresh(previewWith(12500, 19), true), { wrapper })
    expect(store.getState().lines[0].expectedUnitPrice).toBe(11900)

    act(() => store.getState().closePay())

    expect(store.getState().lines[0].expectedUnitPrice).toBe(12500)
    expect(result.current.notices.map(describeExpectedChange)).toEqual(['El precio de Shampoo 400 ml cambió de $\u00a011.900 a $\u00a012.500.'])
  })

  it('ignores a preview that is not current', () => {
    const { store, wrapper } = setup()

    renderHook(() => useExpectedRefresh(previewWith(12500, 5), false), { wrapper })

    expect(store.getState().lines[0]).toMatchObject({ expectedUnitPrice: 11900, expectedTaxPercentage: 19 })
  })

  it('describes both changes in one notice', () => {
    expect(describeExpectedChange({ productId: 'p', name: 'Jabón', fromPrice: 2990, toPrice: 3100, fromTax: 5, toTax: 19 })).toBe(
      'El precio de Jabón cambió de $\u00a02.990 a $\u00a03.100. El IVA de Jabón cambió del 5 % al 19 %.',
    )
  })
})
```

- [ ] **Step 2: Ver el RED de la lógica**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/utils/charge-state.test.ts src/features/pos/hooks/use-expected-refresh.test.tsx
```

Esperado: FAIL, módulos inexistentes.

- [ ] **Step 3: Implementar la lógica**

`src/features/pos/utils/charge-state.ts`:

```ts
import type { PosCartLine } from '../stores/pos-cart-store'
import type { SalePreview } from '../types/pos'

export interface ChargeState {
  enabled: boolean
  label: 'Cobrar' | 'Calculando el total…'
  reason: string | null
}

// "Cobrar" (and F9) only with the scan queue empty, the preview current for this exact cart,
// every line sellable and every expected price/tax equal to the preview's. The total shown is the
// total charged (spec, «Totales», «Líneas no vendibles», decisión 40).
export function chargeState(input: {
  lines: PosCartLine[]
  pendingScans: number
  preview: SalePreview | undefined
  previewCurrent: boolean
  previewError: boolean
}): ChargeState {
  const { lines, pendingScans, preview, previewCurrent, previewError } = input
  if (lines.length === 0) return { enabled: false, label: 'Cobrar', reason: null }
  if (pendingScans > 0) return { enabled: false, label: 'Cobrar', reason: 'Agregando los productos escaneados…' }
  if (previewError) return { enabled: false, label: 'Cobrar', reason: 'No pudimos actualizar los totales' }
  if (!previewCurrent || !preview) return { enabled: false, label: 'Calculando el total…', reason: null }
  if (preview.lines.some((line) => !line.sellable)) {
    return { enabled: false, label: 'Cobrar', reason: 'Quita los productos que no se pueden vender' }
  }
  const stale = preview.lines.some(
    (previewLine, index) =>
      previewLine.unitPrice !== lines[index]?.expectedUnitPrice ||
      previewLine.taxPercentage !== lines[index]?.expectedTaxPercentage,
  )
  if (stale) return { enabled: false, label: 'Calculando el total…', reason: null }
  if (preview.zeroTotalNotAllowed) {
    return { enabled: false, label: 'Cobrar', reason: 'Una venta en $0 necesita el permiso de descuentos.' }
  }
  return { enabled: true, label: 'Cobrar', reason: null }
}
```

`src/features/pos/hooks/use-expected-refresh.ts`:

```ts
import { useCallback, useEffect, useState } from 'react'

import { isCartLocked } from '../stores/pos-cart-store'
import type { SalePreview } from '../types/pos'
import { formatPosMoney } from '../utils/format-pos-money'

import { usePosCart, usePosCartStoreInstance } from './use-pos-cart'

export interface ExpectedChangeNotice {
  productId: string
  name: string
  fromPrice: number
  toPrice: number
  fromTax: number
  toTax: number
}

export function describeExpectedChange(notice: ExpectedChangeNotice): string {
  const parts: string[] = []
  if (notice.fromPrice !== notice.toPrice) {
    parts.push(`El precio de ${notice.name} cambió de ${formatPosMoney(notice.fromPrice)} a ${formatPosMoney(notice.toPrice)}.`)
  }
  if (notice.fromTax !== notice.toTax) {
    parts.push(`El IVA de ${notice.name} cambió del ${notice.fromTax} % al ${notice.toTax} %.`)
  }
  return parts.join(' ')
}

// Refreshes price AND tax: refreshing only the price would leave a tax change with no way out —
// the server rejects either (pos.sale.price_changed) and the preview would see nothing to fix.
// Depends on the lock too, so a refresh refused while paying is applied as soon as the cart
// unlocks, even if the preview did not change (spec, ronda 4).
export function useExpectedRefresh(preview: SalePreview | undefined, previewCurrent: boolean) {
  const store = usePosCartStoreInstance()
  const lines = usePosCart((state) => state.lines)
  const locked = usePosCart(isCartLocked)
  const [notices, setNotices] = useState<ExpectedChangeNotice[]>([])

  useEffect(() => {
    if (!preview || !previewCurrent || locked) return
    const changes = new Map<string, ExpectedChangeNotice>()
    preview.lines.forEach((previewLine, index) => {
      const line = lines[index]
      if (!line || line.productId !== previewLine.productId || !previewLine.sellable) return
      if (previewLine.unitPrice === null || previewLine.taxPercentage === null) return
      if (previewLine.unitPrice !== line.expectedUnitPrice || previewLine.taxPercentage !== line.expectedTaxPercentage) {
        changes.set(line.productId, {
          productId: line.productId,
          name: line.name,
          fromPrice: line.expectedUnitPrice,
          toPrice: previewLine.unitPrice,
          fromTax: line.expectedTaxPercentage,
          toTax: previewLine.taxPercentage,
        })
      }
    })
    if (changes.size === 0) return
    for (const change of changes.values()) {
      store.getState().refreshExpected(change.productId, change.toPrice, change.toTax)
    }
    setNotices((previous) => [...previous.filter((notice) => !changes.has(notice.productId)), ...changes.values()])
  }, [preview, previewCurrent, locked, lines, store])

  const dismiss = useCallback(() => setNotices([]), [])
  return { notices, dismiss }
}
```

Corre el Step 2 otra vez: PASS.

- [ ] **Step 4: Escribir las pruebas de la página que fallan**

`src/features/pos/pages/pos-register-page.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'

import { getPosCartStore, resetPosCartStoresForTests } from '../stores/pos-cart-store'
import { workedExampleSale } from '../utils/pos-sale.fixture'

import { PosRegisterPage } from './pos-register-page'

const permissions = vi.hoisted(() => ({ granted: ['pos.sale.create', 'pos.register.operate', 'pos.sale.read'] }))
vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))
vi.mock('@/features/auth/hooks/use-session', () => ({ useSession: () => ({ session: { userId: 'u-1' }, status: 'authenticated' }) }))
vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({ can: (permission: string) => permissions.granted.includes(permission), status: 'ready', sessionLost: false }),
}))
const navigate = vi.hoisted(() => vi.fn())
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-router')>()),
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
  useNavigate: () => navigate,
}))

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const openSession = (id = 'c-1', overrides = {}) => ({
  id, status: 'Open', openedAt: '2026-10-07T13:02:11Z', openedAtLocal: '2026-10-07T08:02:11-05:00', openedBeforeToday: false,
  company: { id: 'co-1', name: 'Origen Botánico SAS', taxId: '900123456-1' }, openingFloat: 100000, salesCount: 0, voidedCount: 0,
  salesTotal: 0, expectedCash: 100000, version: 1,
  paymentTotals: [{ method: 'Cash', amount: 0 }, { method: 'Card', amount: 0 }, { method: 'Transfer', amount: 0 }],
  ...overrides,
})
const register = (session: unknown) => ({
  cashier: { memberId: 'm-1', name: 'Laura Gómez' }, session,
  companies: [{ id: 'co-1', name: 'Origen Botánico SAS', taxId: '900123456-1' }], defaultCompanyId: 'co-1',
})
const shampoo = { id: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', unitPrice: 11900, taxPercentage: 19, imageUrl: null, sellable: true, unsellableReason: null }
const soap = { id: 'p-jb', code: 'JB-03', name: 'Jabón', unitPrice: 2990, taxPercentage: 5, imageUrl: null, sellable: true, unsellableReason: null }
const previewOf = (lines: { productId: string; quantity: number; discountPercentage: number }[], prices: Record<string, [number, number]>, overrides = {}) => {
  const previewLines = lines.map((line) => {
    const [unitPrice, taxPercentage] = prices[line.productId] ?? [0, 0]
    return { ...line, code: line.productId, name: line.productId, unitPrice, taxPercentage, discountAmount: 0, taxAmount: 0, subtotal: unitPrice * line.quantity, lineTotal: unitPrice * line.quantity, sellable: true, unsellableReason: null }
  })
  const total = previewLines.reduce((sum, line) => sum + line.lineTotal, 0)
  return { lines: previewLines, subtotal: total, taxAmount: 0, discountAmount: 0, total, zeroTotalNotAllowed: false, ...overrides }
}

interface Routes {
  register?: () => unknown
  prices?: () => Record<string, [number, number]>
  previewOverrides?: () => object
  sale?: (body: unknown) => Response | Promise<Response>
  byCode?: Record<string, unknown>
}

function routeFetch(routes: Routes) {
  vi.mocked(fetch).mockImplementation((input, init) => {
    const url = String(input)
    if (url.endsWith('/pos/register')) return Promise.resolve(json(200, routes.register?.() ?? register(openSession())))
    if (url.includes('/pos/products/by-code')) {
      const code = new URL(url, 'http://x').searchParams.get('code') ?? ''
      const product = routes.byCode?.[code]
      return Promise.resolve(product ? json(200, product) : json(404, { code: 'pos.product.not_found' }))
    }
    if (url.includes('/pos/products')) return Promise.resolve(json(200, { items: [shampoo, soap], page: 1, pageSize: 40, total: 2 }))
    if (url.endsWith('/pos/sales/preview')) {
      const { lines } = JSON.parse(String(init?.body))
      return Promise.resolve(json(200, previewOf(lines, routes.prices?.() ?? { 'p-sh': [11900, 19], 'p-jb': [2990, 5] }, routes.previewOverrides?.())))
    }
    if (url.endsWith('/pos/sales') && init?.method === 'POST') {
      return Promise.resolve(routes.sale?.(JSON.parse(String(init.body))) ?? json(201, workedExampleSale()))
    }
    return Promise.resolve(new Response(null, { status: 404 }))
  })
}

function renderPage() {
  const queryClient = new QueryClient()
  const view = render(
    <QueryClientProvider client={queryClient}>
      <PosRegisterPage />
    </QueryClientProvider>,
  )
  return { ...view, queryClient }
}

const store = () => getPosCartStore('t-1', 'u-1')
const addShampoo = (overrides = {}) =>
  store().addProduct({ id: 'p-sh', code: 'SH-400', name: 'Shampoo 400 ml', imageUrl: null, unitPrice: 11900, taxPercentage: 19, ...overrides })
const chargeButton = () => screen.getByRole('button', { name: /Cobrar|Calculando el total/ })
const salePosts = () => vi.mocked(fetch).mock.calls.filter(([input, init]) => String(input).endsWith('/pos/sales') && init?.method === 'POST')
const previewPosts = () => vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/pos/sales/preview'))

afterEach(() => resetPosCartStoresForTests())

describe('PosRegisterPage', () => {
  it('warns about a register left open since another day', async () => {
    routeFetch({ register: () => register(openSession('c-1', { openedBeforeToday: true, openedAtLocal: '2026-10-06T08:00:00-05:00' })) })
    renderPage()

    expect(await screen.findByText(/Caja abierta desde el 06\/10\. Ciérrala si ese turno ya terminó\./)).toBeVisible()
  })

  it('asks before closing the register with products in the cart', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({})
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: /Cerrar caja/ }))

    expect(screen.getByText('Hay 1 producto sin cobrar. Si cierras la caja, se descartan.')).toBeVisible()
    expect(navigate).not.toHaveBeenCalled()
  })

  // Spec, «Sólo cambia la tasa».
  it('a tax-only change refreshes the line, explains it and lets charge with the new tax', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({ prices: () => ({ 'p-sh': [11900, 5] }) })
    renderPage()

    expect(await screen.findByText('El IVA de Shampoo 400 ml cambió del 19 % al 5 %.')).toBeVisible()
    await waitFor(() => expect(chargeButton()).toBeEnabled())
    fireEvent.click(chargeButton())
    fireEvent.change(await screen.findByLabelText('Recibido'), { target: { value: '20000' } })
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    await waitFor(() => expect(salePosts()).toHaveLength(1))
    expect(JSON.parse(String(salePosts()[0][1]?.body)).lines[0].expectedTaxPercentage).toBe(5)
  })

  // Spec, ronda 4, menor 1.
  it('price_changed with a cached preview equal to the expected price asks the preview again and re-enables Cobrar', async () => {
    store().adoptSession('c-1')
    addShampoo()
    let price = 11900
    routeFetch({
      prices: () => ({ 'p-sh': [price, 19] }),
      sale: () => {
        price = 12500
        return json(422, { code: 'pos.sale.price_changed' })
      },
    })
    renderPage()

    await waitFor(() => expect(chargeButton()).toBeEnabled())
    fireEvent.click(chargeButton())
    fireEvent.change(await screen.findByLabelText('Recibido'), { target: { value: '20000' } })
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    expect(await screen.findByText(/El precio de Shampoo 400 ml cambió de \$.11\.900 a \$.12\.500\./)).toBeVisible()
    expect(previewPosts().length).toBeGreaterThanOrEqual(2)
    expect(JSON.parse(String(previewPosts().at(-1)?.[1]?.body))).toEqual(JSON.parse(String(previewPosts()[0][1]?.body)))
    await waitFor(() => expect(chargeButton()).toBeEnabled())
    expect(store().lines[0].expectedUnitPrice).toBe(12500)
  })

  it('a price change seen while paying is applied when the charge closes', async () => {
    store().adoptSession('c-1')
    addShampoo()
    store().openPay()
    routeFetch({ prices: () => ({ 'p-sh': [12500, 19] }) })
    renderPage()

    await screen.findByLabelText('Recibido')
    expect(store().lines[0].expectedUnitPrice).toBe(11900)
    fireEvent.click(screen.getByRole('button', { name: /Cancelar/ }))

    await waitFor(() => expect(store().lines[0].expectedUnitPrice).toBe(12500))
    await waitFor(() => expect(chargeButton()).toBeEnabled())
  })

  // Spec, ronda 4, menor 2.
  it('closes an open charge when the open register changes and keeps the lines', async () => {
    store().adoptSession('c-1')
    addShampoo()
    let current = openSession('c-1')
    routeFetch({ register: () => register(current) })
    const { queryClient } = renderPage()
    await waitFor(() => expect(chargeButton()).toBeEnabled())
    fireEvent.click(chargeButton())
    await screen.findByLabelText('Recibido')

    current = openSession('c-2')
    await act(() => queryClient.invalidateQueries({ queryKey: ['pos', 't-1', 'register'] }))

    expect(await screen.findByText('La caja cambió mientras cobrabas. Revisa el carrito y vuelve a cobrar.')).toBeVisible()
    expect(screen.queryByLabelText('Recibido')).toBeNull()
    expect(store()).toMatchObject({ sessionId: 'c-2', payOpen: false })
    expect(store().lines).toHaveLength(1)
  })

  it.each([
    ['another open register', () => register(openSession('c-2')), 'Shampoo 400 ml'],
    ['no open register', () => register(null), 'Abrir caja'],
  ])('with an uncertain attempt and %s draws the uncertain dialog first; session_mismatch frees the cart', async (_case, registerBody, after) => {
    store().adoptSession('c-1')
    addShampoo()
    store().openPay()
    store().beginAttempt({ id: 'sale-1', cashSessionId: 'c-1', lines: [], payments: [{ method: 'Cash', tendered: 20000 }] })
    store().markUncertain()
    routeFetch({ register: registerBody, sale: () => json(422, { code: 'pos.sale.session_mismatch' }) })
    renderPage()

    expect(await screen.findByRole('button', { name: 'Reintentar' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Verificar' })).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await waitFor(() => expect(store().attempt).toBeNull())
    expect(await screen.findAllByText(after)).not.toHaveLength(0)
    expect(store().lines).toHaveLength(1)
  })

  it('a scan adds without waiting for the debounce, clears the input at once and keeps scan order', async () => {
    store().adoptSession('c-1')
    routeFetch({ byCode: { 'SH-400': shampoo, 'JB-03': soap } })
    renderPage()
    const search = await screen.findByRole('searchbox')

    fireEvent.change(search, { target: { value: 'SH-400' } })
    fireEvent.keyDown(search, { key: 'Enter' })
    expect(search).toHaveValue('')
    fireEvent.change(search, { target: { value: 'JB-03' } })
    fireEvent.keyDown(search, { key: 'Enter' })

    await waitFor(() => expect(store().lines.map((line) => line.code)).toEqual(['SH-400', 'JB-03']))
  })

  it('a 404 scan says so and gives the text back only if the input is still empty', async () => {
    store().adoptSession('c-1')
    routeFetch({})
    renderPage()
    const search = await screen.findByRole('searchbox')

    fireEvent.change(search, { target: { value: 'ZZ-1' } })
    fireEvent.keyDown(search, { key: 'Enter' })

    expect(await screen.findByText('No hay un producto con el código «ZZ-1».')).toBeVisible()
    expect(search).toHaveValue('ZZ-1')
  })

  it('a scan while paying is refused with a message', async () => {
    store().adoptSession('c-1')
    addShampoo()
    store().openPay()
    store().beginAttempt({ id: 'sale-1', cashSessionId: 'c-1', lines: [], payments: [] })
    store().markUncertain()
    store().resolveAttempt('definitive')
    routeFetch({ byCode: { 'JB-09': { ...soap, code: 'JB-09' } } })
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: /Cancelar/ }))
    store().openPay()
    const search = await screen.findByRole('searchbox')

    fireEvent.change(search, { target: { value: 'JB-09' } })
    fireEvent.keyDown(search, { key: 'Enter' })

    expect(await screen.findByText(/No se agregó JB-09/)).toBeVisible()
  })

  it('after a sale the focus is in the search; a scan starts the next sale without printing; F8 prints', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({ byCode: { '7701234567890': { ...soap, code: '7701234567890' } } })
    const print = vi.spyOn(window, 'print').mockImplementation(() => {})
    renderPage()
    await waitFor(() => expect(chargeButton()).toBeEnabled())
    fireEvent.click(chargeButton())
    fireEvent.change(await screen.findByLabelText('Recibido'), { target: { value: '20000' } })
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))

    expect(await screen.findByText('Venta POS-000042 registrada')).toBeVisible()
    const search = screen.getByRole('searchbox')
    await waitFor(() => expect(search).toHaveFocus())

    fireEvent.change(search, { target: { value: '7701234567890' } })
    fireEvent.keyDown(search, { key: 'Enter' })
    await waitFor(() => expect(screen.queryByText('Venta POS-000042 registrada')).toBeNull())
    expect(print).not.toHaveBeenCalled()
    expect(store().lines.map((line) => line.code)).toEqual(['7701234567890'])
  })

  it('F8 prints the last sale while the success panel is visible', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({})
    const print = vi.spyOn(window, 'print').mockImplementation(() => {})
    renderPage()
    await waitFor(() => expect(chargeButton()).toBeEnabled())
    fireEvent.click(chargeButton())
    fireEvent.change(await screen.findByLabelText('Recibido'), { target: { value: '20000' } })
    fireEvent.click(screen.getByRole('button', { name: /Confirmar venta/ }))
    await screen.findByText('Venta POS-000042 registrada')

    fireEvent.keyDown(document.body, { key: 'Enter' })
    expect(print).not.toHaveBeenCalled()
    fireEvent.keyDown(document.body, { key: 'F8' })

    await waitFor(() => expect(print).toHaveBeenCalledOnce())
  })

  it('blocks Cobrar with the reason for unsellable lines and for a zero total without permission', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({ previewOverrides: () => ({ zeroTotalNotAllowed: true }) })
    renderPage()

    expect(await screen.findByText('Una venta en $0 necesita el permiso de descuentos.')).toBeVisible()
    expect(chargeButton()).toBeDisabled()
  })

  it('F9 with the preview pending does not open and says why', async () => {
    store().adoptSession('c-1')
    addShampoo()
    vi.mocked(fetch).mockImplementation((input) =>
      String(input).endsWith('/pos/sales/preview') ? new Promise(() => {}) : Promise.resolve(json(200, register(openSession()))),
    )
    renderPage()
    await screen.findByRole('searchbox')

    fireEvent.keyDown(document.body, { key: 'F9' })

    expect(screen.queryByLabelText('Recibido')).toBeNull()
    expect(screen.getByRole('button', { name: /Calculando el total…/ })).toBeDisabled()
  })

  it('shows the discount button only with pos.sale.discount and renders grid and cart as lists', async () => {
    store().adoptSession('c-1')
    addShampoo()
    routeFetch({})
    renderPage()

    await screen.findByRole('searchbox')
    expect(screen.queryByRole('button', { name: /Descuento/ })).toBeNull()
    const lists = await screen.findAllByRole('list')
    expect(lists.length).toBeGreaterThanOrEqual(2)
    for (const list of lists) {
      expect(within(list).getAllByRole('listitem').length).toBeGreaterThan(0)
    }
  })
})
```

- [ ] **Step 5: Ver el RED de la página**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/pages/pos-register-page.test.tsx
```

Esperado: FAIL en todas (la página es el cascarón de F9).

- [ ] **Step 6: Implementar la pantalla**

Escribe los componentes con estas firmas y responsabilidades (forma con las skills; textos exactos de las pruebas y del spec):

- `RegisterHeader({ cashierName, companyName, session: PosOpenSession, cartLineCount, onCloseRegister })`: «Punto de venta · Caja de {cashierName} · {companyName}», enlaces «Ventas» (`/pos/sales`) y «Cajas» (`/pos/sessions`), botón «Cerrar caja». Con `session.openedBeforeToday`, banner `warning` arriba: `Caja abierta desde el ${dd}/${MM}. Ciérrala si ese turno ya terminó.` (día y mes de `openedAtLocal` leídos como en `formatLocalStamp`). `onCloseRegister` lo decide la página (confirma si hay líneas).
- `ProductSearchBar({ inputRef, value, onValueChange, onEnter, message })`: `<input type="search" inputMode="search" autoFocus>` (rol `searchbox`), placeholder «Escanea o busca por nombre o código…», `kbd` F2; Enter con texto → `onEnter(texto)`; `message` debajo en una región `aria-live="polite"`.
- `ProductGrid({ products, isLoading, isError, search, onRetry, onAdd })`: `<ul role="list">` con un `<li>` por `ProductCard`; 8 esqueletos al cargar; vacío «Ningún producto coincide con «{search}».»; error inline con «Reintentar».
- `ProductCard({ product, onAdd })`: botón con `aria-label={`Agregar ${name}, ${formatPosMoney(unitPrice)}`}`; no vendible: deshabilitado, opaco, `Badge` con `UNSELLABLE_REASON_LABELS`.
- `CartLine({ line, previewLine, selected, highlighted, canDiscount, locked, onQuantity, onRequestRemove, onDiscount, onSelect })` dentro de `<li>`; `QuantityStepper` (input que acepta coma, `parseDecimalInput` en blur, `addQuantity` para ±1; un `−` que deja ≤ 0 llama `onRequestRemove`); `LineDiscountPopover` sólo con `canDiscount` (botón con nombre «Descuento», `Popover` con «% descuento» 0-100 y el monto del preview); total de línea `tabular-nums`; motivo de no vendible si `previewLine.sellable === false`.
- `CartTotals({ preview, isCurrent })`: Subtotal, IVA, Descuentos, **TOTAL** del preview con `formatPosMoney`, indicador sutil mientras `!isCurrent`, en `aria-live="polite"`.
- `CartPanel({ listRef, lines, preview, isCurrent, charge, canDiscount, locked, selectedLineId, highlightLineId, onCharge, …acciones de línea })`: «Carrito · N productos», `<ul ref={listRef}>`, vacío «Escanea un producto o búscalo para empezar.», `CartTotals`, botón «Cobrar» / «Calculando el total…» (`min-h-14`, `kbd` F9) deshabilitado con `charge.reason` visible debajo, avisos de precio/IVA arriba de las líneas.
- `SaleSuccessPanel({ sale, onPrint, onNewSale })`: **no modal**, no toma el foco: check, «Venta {saleNumber} registrada», «Entrega de cambio {formatPosMoney(changeAmount)}» en grande si hay cambio, botones «Imprimir ticket» (`kbd` F8) y «Nueva venta» con `onKeyDown` que anula Enter (`event.key === 'Enter' && event.preventDefault()`).

`src/features/pos/pages/pos-register-page.tsx`:

```tsx
import { useNavigate } from '@tanstack/react-router'
import { useCallback, useMemo, useRef, useState } from 'react'

import { useDebouncedValue } from '@/components/use-debounced-value'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog'
import { usePermissions } from '@/features/auth/hooks/use-permission'

import { CartPanel } from '../components/cart-panel'
import { OpenSessionForm } from '../components/open-session-form'
import { PayDialog } from '../components/pay-dialog'
import { PosTicket } from '../components/pos-ticket'
import { ProductGrid } from '../components/product-grid'
import { ProductSearchBar } from '../components/product-search-bar'
import { RegisterHeader } from '../components/register-header'
import { SaleSuccessPanel } from '../components/sale-success-panel'
import { TicketPrintPortal } from '../components/ticket-print-portal'
import { useExpectedRefresh, describeExpectedChange } from '../hooks/use-expected-refresh'
import { PosCartProvider, usePosCart, usePosCartStoreInstance, useResolvedPosCartStore } from '../hooks/use-pos-cart'
import { usePosProductSearch } from '../hooks/use-pos-product-search'
import { usePosShortcuts } from '../hooks/use-pos-shortcuts'
import { useProductByCodeLookup } from '../hooks/use-product-by-code'
import { useRegisterContext } from '../hooks/use-register-context'
import { useSalePreview } from '../hooks/use-sale-preview'
import { useScanQueue, type ScanResult } from '../hooks/use-scan-queue'
import { describePosFailure } from '../services/pos.api'
import { isCartLocked } from '../stores/pos-cart-store'
import type { PosProduct, PosSale } from '../types/pos'
import { POS_PERMISSIONS } from '../types/pos'
import { chargeState } from '../utils/charge-state'
import { addQuantity } from '../utils/money-cents'
import { useEffect } from 'react'

const SESSION_CHANGED_MESSAGE = 'La caja cambió mientras cobrabas. Revisa el carrito y vuelve a cobrar.'

export function PosRegisterPage() {
  const store = useResolvedPosCartStore()
  // Without tenant and user there is no cart key: the register is not drawn (spec, «Estado del carrito»).
  if (!store) return <Card className="m-4 p-8 text-center text-sm text-muted-foreground">Cargando la caja…</Card>
  return (
    <PosCartProvider store={store}>
      <h1 className="sr-only">Punto de venta</h1>
      <PosRegisterScreen />
    </PosCartProvider>
  )
}

function PosRegisterScreen() {
  const navigate = useNavigate()
  const store = usePosCartStoreInstance()
  const { context, isLoading, isError, refetch } = useRegisterContext()
  const lines = usePosCart((state) => state.lines)
  const attempt = usePosCart((state) => state.attempt)
  const payOpen = usePosCart((state) => state.payOpen)
  const locked = usePosCart(isCartLocked)
  const selectedLineId = usePosCart((state) => state.selectedLineId)
  const { can } = usePermissions()
  const canDiscount = can(POS_PERMISSIONS.saleDiscount)

  const previewRequest = useMemo(
    () => lines.map(({ productId, quantity, discountPercentage }) => ({ productId, quantity, discountPercentage })),
    [lines],
  )
  const { preview, isCurrent, isError: previewError } = useSalePreview(previewRequest)
  const refresh = useExpectedRefresh(preview, isCurrent)

  const [searchText, setSearchText] = useState('')
  const debouncedSearch = useDebouncedValue(searchText, 250)
  const products = usePosProductSearch(debouncedSearch)
  const [scanMessage, setScanMessage] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [lastSale, setLastSale] = useState<PosSale | null>(null)
  const [printing, setPrinting] = useState(false)
  const [highlightLineId, setHighlightLineId] = useState<string | null>(null)
  const [confirmClose, setConfirmClose] = useState(false)
  const [confirmRemoveLineId, setConfirmRemoveLineId] = useState<string | null>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const cartListRef = useRef<HTMLUListElement>(null)

  const focusSearch = useCallback(() => {
    searchRef.current?.focus()
    searchRef.current?.select()
  }, [])

  const add = useCallback(
    (product: PosProduct, label: string) => {
      if (!product.sellable || product.unitPrice === null) {
        setScanMessage(product.unsellableReason === 'Inactive' ? `${label} está inactivo` : `${label} no tiene precio en pesos`)
        return
      }
      const result = store.getState().addProduct({ ...product, unitPrice: product.unitPrice })
      if (result === 'added') {
        // The first product of the next sale removes the success panel.
        setLastSale(null)
        setHighlightLineId(store.getState().selectedLineId)
        window.setTimeout(() => setHighlightLineId(null), 600)
        return
      }
      setScanMessage(`No se agregó ${label}: ${result === 'locked' ? 'hay un cobro en curso.' : 'la venta ya tiene 200 productos.'}`)
    },
    [store],
  )

  const onScanResult = useCallback(
    (scan: ScanResult) => {
      if (scan.kind === 'found') add(scan.product, scan.code)
      else if (scan.kind === 'not-found') {
        setScanMessage(`No hay un producto con el código «${scan.code}».`)
        // Gives the text back only if nothing else is being scanned.
        setSearchText((current) => (current === '' ? scan.code : current))
      } else setScanMessage(describePosFailure(scan.error))
    },
    [add],
  )
  const queue = useScanQueue(useProductByCodeLookup(), onScanResult)
  const charge = chargeState({ lines, pendingScans: queue.pending, preview, previewCurrent: isCurrent, previewError })

  // The open register is no longer the cart's: adopt it (lines stay, pendingSaleId resets, an open
  // charge closes). With an attempt the uncertain dialog comes first and nothing is adopted.
  const openSessionId = context?.session?.id
  useEffect(() => {
    if (!openSessionId || attempt) return
    if (store.getState().sessionId !== openSessionId && store.getState().adoptSession(openSessionId)) {
      setNotice(SESSION_CHANGED_MESSAGE)
    }
  }, [openSessionId, attempt, store])

  const openPay = useCallback(() => {
    if (!charge.enabled) {
      setScanMessage(charge.reason ?? charge.label)
      return
    }
    refresh.dismiss()
    setNotice(null)
    store.getState().openPay()
  }, [charge, refresh, store])

  const moveSelection = useCallback(
    (delta: 1 | -1) => {
      const index = lines.findIndex((line) => line.lineId === selectedLineId)
      const next = lines[Math.min(lines.length - 1, Math.max(0, index + delta))]
      if (next) store.getState().selectLine(next.lineId)
    },
    [lines, selectedLineId, store],
  )

  const changeQuantity = useCallback(
    (lineId: string, delta: 1 | -1) => {
      const line = store.getState().lines.find((item) => item.lineId === lineId)
      if (!line) return
      const next = addQuantity(line.quantity, delta)
      if (next <= 0) setConfirmRemoveLineId(lineId)
      else store.getState().setQuantity(lineId, next)
    },
    [store],
  )

  usePosShortcuts(
    {
      focusSearch,
      openPay,
      printLast: lastSale ? () => setPrinting(true) : undefined,
      escape: () => {
        if (payOpen && !attempt) store.getState().closePay()
        else setSearchText('')
      },
      moveSelection,
      changeQuantity: (delta) => selectedLineId && changeQuantity(selectedLineId, delta),
      removeSelected: () => selectedLineId && setConfirmRemoveLineId(selectedLineId),
    },
    cartListRef,
  )

  const payDialog = (payOpen || attempt) && (
    <PayDialog
      open
      total={preview?.total ?? 0}
      sessionId={context?.session?.id ?? null}
      onSuccess={(sale) => {
        setLastSale(sale)
        window.setTimeout(focusSearch, 0)
      }}
      onClose={() => {
        store.getState().closePay()
        window.setTimeout(focusSearch, 0)
      }}
    />
  )

  if (isLoading) return <Card className="m-4 p-8 text-center text-sm text-muted-foreground">Cargando la caja…</Card>
  if (isError || !context) {
    return (
      <Card className="m-4 space-y-3 p-8 text-center text-sm">
        <p>No pudimos cargar la caja</p>
        <Button variant="outline" onClick={refetch}>Reintentar</Button>
      </Card>
    )
  }

  if (!context.session) {
    return (
      <div className="flex h-full items-center justify-center p-4">
        <OpenSessionForm context={context} />
        {payDialog}
      </div>
    )
  }

  return (
    <div className="grid h-full grid-rows-[auto_1fr] bg-background lg:grid-cols-[1fr_400px] lg:grid-rows-[auto_1fr]">
      <div className="lg:col-span-2">
        <RegisterHeader
          cashierName={context.cashier.name}
          companyName={context.session.company.name}
          session={context.session}
          cartLineCount={lines.length}
          onCloseRegister={() => (lines.length > 0 ? setConfirmClose(true) : void navigate({ to: '/pos/close' }))}
        />
      </div>
      <section className="min-h-0 overflow-auto p-4">
        <ProductSearchBar
          inputRef={searchRef}
          value={searchText}
          onValueChange={(value) => {
            setSearchText(value)
            setScanMessage(null)
          }}
          onEnter={(code) => {
            // Capture, clear at once and enqueue: the next code must not concatenate to this one
            // and the grid must not search the old text (spec, «Flujo de lector»).
            setSearchText('')
            setScanMessage(null)
            queue.enqueue(code)
          }}
          message={scanMessage}
        />
        <ProductGrid
          products={products.products}
          isLoading={products.isLoading}
          isError={products.isError}
          search={debouncedSearch}
          onRetry={products.refetch}
          onAdd={(product) => add(product, product.code)}
        />
      </section>
      <aside className="border-t border-border bg-card lg:border-l lg:border-t-0">
        {lastSale ? (
          <SaleSuccessPanel
            sale={lastSale}
            onPrint={() => {
              setPrinting(true)
              window.setTimeout(focusSearch, 0)
            }}
            onNewSale={() => {
              setLastSale(null)
              focusSearch()
            }}
          />
        ) : null}
        {notice && <p role="status" className="p-3 text-sm">{notice}</p>}
        {refresh.notices.map((change) => (
          <p key={change.productId} role="status" className="p-3 text-sm">{describeExpectedChange(change)}</p>
        ))}
        <CartPanel
          listRef={cartListRef}
          lines={lines}
          preview={preview}
          isCurrent={isCurrent}
          charge={charge}
          canDiscount={canDiscount}
          locked={locked}
          selectedLineId={selectedLineId}
          highlightLineId={highlightLineId}
          onCharge={openPay}
          onSelect={(lineId) => store.getState().selectLine(lineId)}
          onQuantity={(lineId, quantity) => store.getState().setQuantity(lineId, quantity)}
          onStep={changeQuantity}
          onRequestRemove={setConfirmRemoveLineId}
          onDiscount={(lineId, discount) => store.getState().setDiscount(lineId, discount)}
        />
      </aside>

      {payDialog}

      {printing && lastSale && (
        <TicketPrintPortal title={lastSale.saleNumber} onDone={() => setPrinting(false)}>
          <PosTicket sale={lastSale} />
        </TicketPrintPortal>
      )}

      <Dialog open={confirmClose} onOpenChange={setConfirmClose}>
        <DialogContent>
          <DialogTitle>Cerrar caja</DialogTitle>
          <p>{`Hay ${lines.length} ${lines.length === 1 ? 'producto' : 'productos'} sin cobrar. Si cierras la caja, se descartan.`}</p>
          <div className="flex justify-end gap-2">
            <Button variant="outline" onClick={() => setConfirmClose(false)}>Seguir vendiendo</Button>
            <Button onClick={() => void navigate({ to: '/pos/close' })}>Ir a cerrar la caja</Button>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={confirmRemoveLineId !== null} onOpenChange={(open) => !open && setConfirmRemoveLineId(null)}>
        <DialogContent>
          <DialogTitle>Quitar producto</DialogTitle>
          <p>¿Quitas este producto del carrito?</p>
          <div className="flex justify-end gap-2">
            <Button variant="outline" onClick={() => setConfirmRemoveLineId(null)}>Cancelar</Button>
            <Button
              onClick={() => {
                if (confirmRemoveLineId) store.getState().removeLine(confirmRemoveLineId)
                setConfirmRemoveLineId(null)
              }}
            >
              Quitar
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}
```

(Ordena los `import` como pida Prettier/oxlint —el `useEffect` va con los demás de `react`—. `CartPanel` recibe `onStep(lineId, delta)` para los botones `+`/`−` del stepper y `onQuantity` para el input editable.)

Después, con `impeccable`, `frontend-design` y `emil-design-eng`, compón la pantalla según el wireframe del spec («Pantalla de caja») sobre el tema botánico. Las pruebas no cambian.

- [ ] **Step 7: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos; git commit -m "feat(pos): pantalla de caja"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Después, **revisión visual** en el navegador (`bun dev` contra el backend de la Task B14 o con la red mockeada): 1280 px y 390 px de ancho, foco visible, resaltado de 600 ms, `prefers-reduced-motion`. Anota capturas o hallazgos en el handoff.

---

### Task F14: Listados de ventas y cajas *(con skills de diseño)*

**Files:**
- Create: `src/features/pos/components/sales-table.tsx`, `src/features/pos/components/void-sale-dialog.tsx`, `src/features/pos/components/sessions-table.tsx`, `src/features/pos/index.ts`
- Modify: `src/features/pos/pages/pos-sales-page.tsx`, `src/features/pos/pages/pos-sessions-page.tsx` (reemplazan los cascarones)
- Test: `src/features/pos/components/sales-table.test.tsx`, `src/features/pos/components/void-sale-dialog.test.tsx`, `src/features/pos/pages/pos-sales-page.test.tsx`, `src/features/pos/pages/pos-sessions-page.test.tsx`

**Interfaces:**
- Consumes: F7 (`usePosSales`, `usePosSessions`, `useVoidSale`, `usePosSale`), F10 (`PosTicket`, `TicketPrintPortal`), F11 (`ClosingTicket`, `differenceLabel`).
- Produces: `SalesTable({ items, canVoid, showCashier, onReprint(id), onVoid(item) })`, `VoidSaleDialog({ sale: PosSaleListItem | null; onClose(); onVoided() })`, `SessionsTable({ items, showCashier, onOpen(summary) })`, `PosSalesPage`, `PosSessionsPage`, barril `index.ts`.

Antes del Step 3, carga `impeccable` y `frontend-design`: patrón de listados existente (`DataTable` y `ListToolbar`/`FiltersPopover` del proyecto), estado (`Badge`), montos `tabular-nums`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/pos/components/sales-table.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'

import type { PosSaleListItem } from '../types/pos'

import { SalesTable } from './sales-table'

const item = (overrides: Partial<PosSaleListItem> = {}): PosSaleListItem => ({
  id: 's-1', saleNumber: 'POS-000042', createdAtLocal: '2026-10-07T10:20:03-05:00', cashierName: 'Laura Gómez',
  customerName: 'Consumidor final', total: 37890, status: 'Completed', paymentMethods: ['Card', 'Cash'],
  voidable: true, voidBlockedReason: null, ...overrides,
})

describe('SalesTable', () => {
  it('draws no Anular without pos.sale.void', () => {
    render(<SalesTable items={[item()]} canVoid={false} showCashier={false} onReprint={vi.fn()} onVoid={vi.fn()} />)

    expect(screen.queryByRole('button', { name: /Anular/ })).toBeNull()
    expect(screen.getByRole('button', { name: /Reimprimir/ })).toBeEnabled()
    expect(screen.queryByText('Laura Gómez')).toBeNull()
  })

  it('follows voidable and explains a blocked void from its reason', () => {
    render(
      <SalesTable
        items={[item(), item({ id: 's-2', saleNumber: 'POS-000043', voidable: false, voidBlockedReason: 'SessionClosed' })]}
        canVoid
        showCashier
        onReprint={vi.fn()}
        onVoid={vi.fn()}
      />,
    )

    const buttons = screen.getAllByRole('button', { name: /Anular/ })
    expect(buttons[0]).toBeEnabled()
    expect(buttons[1]).toBeDisabled()
    expect(screen.getByText('La caja de esta venta ya se cerró')).toBeInTheDocument()
    expect(screen.getAllByText('Laura Gómez')).toHaveLength(2)
  })
})
```

`src/features/pos/components/void-sale-dialog.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'

import { VoidSaleDialog } from './void-sale-dialog'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))

const sale = {
  id: 's-1', saleNumber: 'POS-000042', createdAtLocal: '2026-10-07T10:20:03-05:00', cashierName: 'Laura Gómez',
  customerName: 'Consumidor final', total: 37890, status: 'Completed' as const, paymentMethods: ['Cash' as const],
  voidable: true, voidBlockedReason: null,
}

function renderDialog(onVoided = vi.fn()) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <VoidSaleDialog sale={sale} onClose={vi.fn()} onVoided={onVoided} />
    </QueryClientProvider>,
  )
  return onVoided
}

describe('VoidSaleDialog', () => {
  it('asks a reason of 3 to 500 characters', () => {
    renderDialog()

    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'ab' } })
    expect(screen.getByRole('button', { name: 'Anular venta' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'Cliente se arrepintió' } })
    expect(screen.getByRole('button', { name: 'Anular venta' })).toBeEnabled()
  })

  // Its previous request landed and the answer was lost, or someone else voided it.
  it('takes already_voided as success', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ code: 'pos.sale.already_voided' }), { status: 422, headers: { 'Content-Type': 'application/problem+json' } }),
    )
    const onVoided = renderDialog()

    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'Cliente se arrepintió' } })
    fireEvent.click(screen.getByRole('button', { name: 'Anular venta' }))

    await waitFor(() => expect(onVoided).toHaveBeenCalled())
  })
})
```

`src/features/pos/pages/pos-sales-page.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'

import { PosSalesPage } from './pos-sales-page'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))
vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({ can: () => true, status: 'ready', sessionLost: false }),
}))

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <PosSalesPage />
    </QueryClientProvider>,
  )
}

describe('PosSalesPage', () => {
  it('asks for today by default and shows the empty state', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, { items: [], page: 1, pageSize: 20, total: 0 }))
    renderPage()

    expect(await screen.findByText('No hay ventas en este periodo.')).toBeVisible()
    const url = String(vi.mocked(fetch).mock.calls[0][0])
    expect(url).toMatch(/from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}/)
  })

  it('shows an error with a retry', async () => {
    vi.mocked(fetch).mockResolvedValue(json(500, {}))
    renderPage()

    expect(await screen.findByRole('button', { name: 'Reintentar' })).toBeVisible()
  })
})
```

`src/features/pos/pages/pos-sessions-page.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'

import { PosSessionsPage } from './pos-sessions-page'

const permissions = vi.hoisted(() => ({ granted: ['pos.sale.read'] }))
vi.mock('@/features/auth/hooks/use-active-tenant', () => ({ useActiveTenant: () => ({ tenantId: 't-1' }) }))
vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({ can: (permission: string) => permissions.granted.includes(permission), status: 'ready', sessionLost: false }),
}))

const summary = {
  id: 'c-1', status: 'Closed', cashierName: 'Laura Gómez', company: { name: 'Origen Botánico SAS', taxId: '900123456-1' },
  openedAtLocal: '2026-10-07T08:02:11-05:00', closedAtLocal: '2026-10-07T18:31:40-05:00', openingFloat: 100000, salesCount: 1,
  voidedCount: 0, salesTotal: 37890, paymentTotals: [], expectedCash: 117890, countedCash: 117000, cashDifference: -890, note: null,
}

function renderPage() {
  vi.mocked(fetch).mockResolvedValue(
    new Response(JSON.stringify({ items: [summary], page: 1, pageSize: 20, total: 1 }), { status: 200, headers: { 'Content-Type': 'application/json' } }),
  )
  render(
    <QueryClientProvider client={new QueryClient()}>
      <PosSessionsPage />
    </QueryClientProvider>,
  )
}

describe('PosSessionsPage', () => {
  it('hides the cashier column without pos.register.read and shows the signed difference', async () => {
    permissions.granted = ['pos.sale.read']
    renderPage()

    expect(await screen.findByText('Origen Botánico SAS')).toBeVisible()
    expect(screen.queryByText('Laura Gómez')).toBeNull()
    expect(screen.getByText(/Faltante/)).toBeVisible()
  })

  it('shows the cashier with pos.register.read', async () => {
    permissions.granted = ['pos.sale.read', 'pos.register.read']
    renderPage()

    expect(await screen.findByText('Laura Gómez')).toBeVisible()
  })
})
```

- [ ] **Step 2: Ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos/components/sales-table.test.tsx src/features/pos/components/void-sale-dialog.test.tsx src/features/pos/pages/pos-sales-page.test.tsx src/features/pos/pages/pos-sessions-page.test.tsx
```

Esperado: FAIL (componentes inexistentes; páginas cascarón).

- [ ] **Step 3: Implementar**

`src/features/pos/components/sales-table.tsx`:

```tsx
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, DataTableBody, DataTableCell, DataTableHead, DataTableHeader, DataTableHeaderRow, DataTableRow } from '@/components/ui/data-table'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

import type { PosSaleListItem } from '../types/pos'
import { PAYMENT_METHOD_LABELS, VOID_BLOCKED_REASON_LABELS } from '../types/pos'
import { formatLocalStamp } from '../utils/format-local-stamp'
import { formatPosMoney } from '../utils/format-pos-money'

export function SalesTable({
  items,
  canVoid,
  showCashier,
  onReprint,
  onVoid,
}: {
  items: PosSaleListItem[]
  canVoid: boolean
  showCashier: boolean
  onReprint: (saleId: string) => void
  onVoid: (item: PosSaleListItem) => void
}) {
  return (
    <DataTable>
      <DataTableHeader>
        <DataTableHeaderRow>
          <DataTableHead>Número</DataTableHead>
          <DataTableHead>Hora</DataTableHead>
          {showCashier && <DataTableHead>Cajero</DataTableHead>}
          <DataTableHead>Cliente</DataTableHead>
          <DataTableHead>Medios</DataTableHead>
          <DataTableHead className="text-right">Total</DataTableHead>
          <DataTableHead>Estado</DataTableHead>
          <DataTableHead>Acciones</DataTableHead>
        </DataTableHeaderRow>
      </DataTableHeader>
      <DataTableBody>
        {items.map((item) => {
          const blocked = item.voidBlockedReason ? VOID_BLOCKED_REASON_LABELS[item.voidBlockedReason] : null
          return (
            <DataTableRow key={item.id}>
              <DataTableCell className="font-mono">{item.saleNumber}</DataTableCell>
              <DataTableCell>{formatLocalStamp(item.createdAtLocal)}</DataTableCell>
              {showCashier && <DataTableCell>{item.cashierName}</DataTableCell>}
              <DataTableCell>{item.customerName}</DataTableCell>
              <DataTableCell>{item.paymentMethods.map((method) => PAYMENT_METHOD_LABELS[method]).join(' + ')}</DataTableCell>
              <DataTableCell className="text-right tabular-nums">{formatPosMoney(item.total)}</DataTableCell>
              <DataTableCell>
                <Badge variant={item.status === 'Voided' ? 'destructive' : 'secondary'}>
                  {item.status === 'Voided' ? 'Anulada' : 'Completada'}
                </Badge>
              </DataTableCell>
              <DataTableCell className="flex gap-2">
                <Button variant="outline" size="sm" onClick={() => onReprint(item.id)}>Reimprimir</Button>
                {canVoid &&
                  (item.voidable ? (
                    <Button variant="outline" size="sm" onClick={() => onVoid(item)}>Anular</Button>
                  ) : (
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span tabIndex={0}>
                          <Button variant="outline" size="sm" disabled>Anular</Button>
                          <span className="sr-only">{blocked}</span>
                        </span>
                      </TooltipTrigger>
                      <TooltipContent>{blocked}</TooltipContent>
                    </Tooltip>
                  ))}
              </DataTableCell>
            </DataTableRow>
          )
        })}
      </DataTableBody>
    </DataTable>
  )
}
```

(Si `Tooltip` exige un `TooltipProvider` en el árbol y la app no lo pone global, envuelve la tabla en `<TooltipProvider>`; los nombres de variantes de `Badge` y `Button` son los de `components/ui`.)

`src/features/pos/components/void-sale-dialog.tsx`:

```tsx
import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api-client'

import { useVoidSale } from '../hooks/use-void-sale'
import { describePosFailure } from '../services/pos.api'
import type { PosSaleListItem } from '../types/pos'

export function VoidSaleDialog({ sale, onClose, onVoided }: { sale: PosSaleListItem | null; onClose: () => void; onVoided: () => void }) {
  const voidSale = useVoidSale()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const trimmed = reason.trim()
  const valid = trimmed.length >= 3 && trimmed.length <= 500

  async function submit() {
    if (!sale || !valid) return
    setError(null)
    try {
      await voidSale.mutateAsync({ saleId: sale.id, reason: trimmed })
      onVoided()
    } catch (failure) {
      if (failure instanceof ApiError && failure.code === 'pos.sale.already_voided') {
        onVoided()
        return
      }
      setError(describePosFailure(failure))
    }
  }

  return (
    <Dialog open={sale !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogTitle>Anular {sale?.saleNumber}</DialogTitle>
        <div className="space-y-1">
          <Label htmlFor="pos-void-reason">Motivo</Label>
          <Textarea id="pos-void-reason" maxLength={500} value={reason} onChange={(event) => setReason(event.target.value)} />
        </div>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onClose}>Cancelar</Button>
          <Button variant="destructive" disabled={!valid || voidSale.isPending} onClick={() => void submit()}>
            Anular venta
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
```

`src/features/pos/components/sessions-table.tsx`:

```tsx
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, DataTableBody, DataTableCell, DataTableHead, DataTableHeader, DataTableHeaderRow, DataTableRow } from '@/components/ui/data-table'

import type { SessionSummary } from '../types/pos'
import { formatLocalStamp } from '../utils/format-local-stamp'
import { formatPosMoney } from '../utils/format-pos-money'

import { differenceLabel } from './closing-summary'

export function SessionsTable({ items, showCashier, onOpen }: { items: SessionSummary[]; showCashier: boolean; onOpen: (summary: SessionSummary) => void }) {
  return (
    <DataTable>
      <DataTableHeader>
        <DataTableHeaderRow>
          {showCashier && <DataTableHead>Cajero</DataTableHead>}
          <DataTableHead>Empresa</DataTableHead>
          <DataTableHead>Estado</DataTableHead>
          <DataTableHead>Apertura</DataTableHead>
          <DataTableHead>Cierre</DataTableHead>
          <DataTableHead className="text-right">Ventas</DataTableHead>
          <DataTableHead>Diferencia</DataTableHead>
          <DataTableHead />
        </DataTableHeaderRow>
      </DataTableHeader>
      <DataTableBody>
        {items.map((summary) => (
          <DataTableRow key={summary.id}>
            {showCashier && <DataTableCell>{summary.cashierName}</DataTableCell>}
            <DataTableCell>{summary.company.name}</DataTableCell>
            <DataTableCell>
              <Badge variant="secondary">{summary.status === 'Open' ? 'Abierta' : 'Cerrada'}</Badge>
            </DataTableCell>
            <DataTableCell>{formatLocalStamp(summary.openedAtLocal)}</DataTableCell>
            <DataTableCell>{summary.closedAtLocal ? formatLocalStamp(summary.closedAtLocal) : '—'}</DataTableCell>
            <DataTableCell className="text-right tabular-nums">{formatPosMoney(summary.salesTotal)}</DataTableCell>
            <DataTableCell>{summary.cashDifference === null ? '—' : differenceLabel(summary.cashDifference)}</DataTableCell>
            <DataTableCell>
              <Button variant="outline" size="sm" onClick={() => onOpen(summary)}>Ver cierre</Button>
            </DataTableCell>
          </DataTableRow>
        ))}
      </DataTableBody>
    </DataTable>
  )
}
```

`src/features/pos/pages/pos-sales-page.tsx`:

```tsx
import { useState } from 'react'

import { PageContainer } from '@/components/page-container'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { usePermissions } from '@/features/auth/hooks/use-permission'

import { PosTicket } from '../components/pos-ticket'
import { SalesTable } from '../components/sales-table'
import { TicketPrintPortal } from '../components/ticket-print-portal'
import { VoidSaleDialog } from '../components/void-sale-dialog'
import { usePosSale } from '../hooks/use-pos-sale'
import { usePosSales } from '../hooks/use-pos-sales'
import type { PosSaleListItem, SalesFilters } from '../types/pos'
import { POS_PERMISSIONS } from '../types/pos'

function today(): string {
  const now = new Date()
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

export function PosSalesPage() {
  const { can } = usePermissions()
  const [filters, setFilters] = useState<SalesFilters>(() => ({ from: today(), to: today(), page: 1, pageSize: 20 }))
  const { page, isLoading, isError, refetch } = usePosSales(filters)
  const [voiding, setVoiding] = useState<PosSaleListItem | null>(null)
  const [reprintId, setReprintId] = useState<string | null>(null)
  const reprint = usePosSale(reprintId)

  return (
    <PageContainer>
      <h1 className="text-xl font-semibold">Ventas</h1>
      {/* Filters: rango (from/to), estado y número exacto; con ListToolbar/FiltersPopover del proyecto. */}
      <div className="my-4 flex flex-wrap gap-2">
        <input aria-label="Desde" type="date" value={filters.from ?? ''} onChange={(event) => setFilters({ ...filters, from: event.target.value || undefined, page: 1 })} />
        <input aria-label="Hasta" type="date" value={filters.to ?? ''} onChange={(event) => setFilters({ ...filters, to: event.target.value || undefined, page: 1 })} />
        <input aria-label="Número" placeholder="POS-000042" value={filters.number ?? ''} onChange={(event) => setFilters({ ...filters, number: event.target.value || undefined, page: 1 })} />
      </div>
      {isLoading ? (
        <Card className="p-8 text-center text-sm text-muted-foreground">Cargando ventas…</Card>
      ) : isError ? (
        <Card className="space-y-3 p-8 text-center text-sm">
          <p>No pudimos cargar las ventas.</p>
          <Button variant="outline" onClick={refetch}>Reintentar</Button>
        </Card>
      ) : !page || page.items.length === 0 ? (
        <Card className="p-8 text-center text-sm text-muted-foreground">No hay ventas en este periodo.</Card>
      ) : (
        <SalesTable
          items={page.items}
          canVoid={can(POS_PERMISSIONS.saleVoid)}
          showCashier={can(POS_PERMISSIONS.registerRead)}
          onReprint={setReprintId}
          onVoid={setVoiding}
        />
      )}
      <VoidSaleDialog sale={voiding} onClose={() => setVoiding(null)} onVoided={() => { setVoiding(null); refetch() }} />
      {reprint.data && (
        <TicketPrintPortal title={reprint.data.saleNumber} onDone={() => setReprintId(null)}>
          <PosTicket sale={reprint.data} />
        </TicketPrintPortal>
      )}
    </PageContainer>
  )
}
```

`src/features/pos/pages/pos-sessions-page.tsx`:

```tsx
import { useState } from 'react'

import { PageContainer } from '@/components/page-container'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog'
import { usePermissions } from '@/features/auth/hooks/use-permission'

import { ClosingTicket } from '../components/closing-summary'
import { SessionsTable } from '../components/sessions-table'
import { TicketPrintPortal } from '../components/ticket-print-portal'
import { usePosSessions } from '../hooks/use-pos-sessions'
import type { SessionSummary, SessionsFilters } from '../types/pos'
import { POS_PERMISSIONS } from '../types/pos'

export function PosSessionsPage() {
  const { can } = usePermissions()
  const [filters] = useState<SessionsFilters>({ page: 1, pageSize: 20 })
  const { page, isLoading, isError, refetch } = usePosSessions(filters)
  const [open, setOpen] = useState<SessionSummary | null>(null)
  const [printing, setPrinting] = useState(false)

  return (
    <PageContainer>
      <h1 className="text-xl font-semibold">Cajas</h1>
      {isLoading ? (
        <Card className="p-8 text-center text-sm text-muted-foreground">Cargando cajas…</Card>
      ) : isError ? (
        <Card className="space-y-3 p-8 text-center text-sm">
          <p>No pudimos cargar las cajas.</p>
          <Button variant="outline" onClick={refetch}>Reintentar</Button>
        </Card>
      ) : !page || page.items.length === 0 ? (
        <Card className="p-8 text-center text-sm text-muted-foreground">No hay cajas en este periodo.</Card>
      ) : (
        <SessionsTable items={page.items} showCashier={can(POS_PERMISSIONS.registerRead)} onOpen={setOpen} />
      )}
      <Dialog open={open !== null} onOpenChange={(next) => !next && setOpen(null)}>
        <DialogContent>
          <DialogTitle>Cierre de caja</DialogTitle>
          {open && <ClosingTicket summary={open} />}
          <Button onClick={() => setPrinting(true)}>Imprimir cierre</Button>
        </DialogContent>
      </Dialog>
      {printing && open && (
        <TicketPrintPortal title={`Cierre ${open.cashierName}`} onDone={() => setPrinting(false)}>
          <ClosingTicket summary={open} />
        </TicketPrintPortal>
      )}
    </PageContainer>
  )
}
```

`src/features/pos/index.ts`:

```ts
export * from './pages/pos-register-page'
export * from './pages/pos-close-session-page'
export * from './pages/pos-sales-page'
export * from './pages/pos-sessions-page'
export * from './services/pos.api'
export * from './types/pos'
```

Después, con `impeccable` y `frontend-design`, reemplaza los `input` de filtros por los componentes de listado del proyecto (`ListToolbar`, `FiltersPopover`, `DateRangePresets`, `ListPagination`) y ajusta la composición. Las pruebas no cambian.

- [ ] **Step 4: Ver el GREEN, lint y commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
bun run test --run src/features/pos
bun run lint
bunx prettier --check src/features/pos
if ((git branch --show-current) -ne "feature/pos") { throw "ABORT" }; git add src/features/pos; git commit -m "feat(pos): listados de ventas y cajas"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F15: Suite completa contra la línea base y humo de punta a punta

**Files:** ninguno (sólo lectura y ejecución).

**Interfaces:**
- Consumes: F0-F14 y el backend de B14 corriendo.
- Produces: la evidencia literal del handoff del frontend.

- [ ] **Step 1: Suite completa, lint y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
git status --short
bun run test --run --reporter=json --outputFile="$env:TEMP\qep-pos-front-final.json"
bun run lint
bun run build
```

Esperado: `git status` vacío; lint y build verdes; `routeTree.gen.ts` sin cambios nuevos después del build.

- [ ] **Step 2: Comparar por nombre contra la línea base**

```powershell
$report = Get-Content -LiteralPath "$env:TEMP\qep-pos-front-final.json" -Raw | ConvertFrom-Json
$failedNow = $report.testResults | ForEach-Object { $_.assertionResults | Where-Object { $_.status -eq 'failed' } | ForEach-Object { $_.fullName } } | Sort-Object -Unique
$baseline = @(Get-Content -LiteralPath "$env:TEMP\qep-pos-front-baseline-failed.txt" -ErrorAction SilentlyContinue)
"Nuevas fallas:"
$failedNow | Where-Object { $_ -notin $baseline }
"Fallas previas que ahora pasan:"
$baseline | Where-Object { $_ -and $_ -notin $failedNow }
```

Esperado: «Nuevas fallas:» sin líneas debajo. Una nueva es regresión de esta rama: RED, arreglo, GREEN y commit antes de cerrar.

- [ ] **Step 3: Commits y residuos**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
$base = git merge-base feature/modulos-por-tenant HEAD
git log --oneline "$base..HEAD"
git log --format=%B "$base..HEAD" | Select-String -SimpleMatch "Co-Authored-By"
git branch --show-current
```

Esperado: dieciséis commits (F0-F14 más el de docs), sin `Co-Authored-By`, rama `feature/pos`.

- [ ] **Step 4: Humo de punta a punta (PowerShell)**

Backend del worktree `qep-backend-worktrees\pos` con **auth real** (el perfil de `launchSettings.json`, puerto 5000) y la semilla, que crea `origen-botanico` y le da admin a `Seed:OwnerEmail`:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pos
docker start postgres18
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$env:Seed__Enabled = "true"
$env:Storage__R2__PublicBucket = "qep-public"
$env:Storage__R2__PublicBaseUrl = "https://cdn.qep.test"
dotnet run --project src/Api -p:NuGetAudit=false
```

En otra ventana, `pos` prendido para el tenant sembrado (si no lo hizo la Task B14) y tres códigos para escanear:

```powershell
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT id, 'pos', now(), 'manual', 'Humo POS' FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT p.code, p.price_base_cop FROM catalog.products p JOIN tenancy.tenants t ON t.id = p.tenant_id WHERE t.slug = 'origen-botanico' AND p.is_active AND p.price_base_cop IS NOT NULL ORDER BY p.code LIMIT 3;"
```

Frontend contra esa API (el `.env.local` está ignorado por git):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\pos
Set-Content -Path .env.local -Encoding ascii -Value "QEP_API_PROXY_TARGET=http://localhost:5000"
bun dev
```

Abre la URL que imprime `bun dev`, entra con la cuenta de `Seed:OwnerEmail`, elige el tenant «origen-botanico» y ve a `/pos`. Recorrido (anota el resultado de cada uno en el handoff):

1. «Punto de venta» aparece en el menú, después de Pedidos.
2. Sin caja: formulario de apertura; abre con base $100.000.
3. F2, teclea el primer código y Enter, tres veces (o con un lector real): las líneas entran en orden, el input queda vacío cada vez; sin mouse.
4. F9, recibido mayor al total, Enter: «Venta POS-00000N registrada» y el cambio; el foco vuelve a la búsqueda. Cronometra del F2 al registro: el criterio es **menos de 15 s**.
5. F8: la vista previa de impresión muestra sólo el ticket en 80 mm (Chrome o Edge).
6. Cobro incierto: abre el cobro, detén la API (`Get-Process -Name Api | Stop-Process -Force` o `Ctrl+C` en su ventana), confirma: aparece «No pudimos confirmar si la venta quedó registrada.» y Esc no cierra. Levanta la API, «Verificar» (404 → sigue bloqueado), «Reintentar» → éxito; en `/pos/sales` hay **una** venta con ese número.
7. F5 en medio del cobro incierto: vuelve el diálogo incierto.
8. `/pos/sales`: anula la venta con motivo; la caja descuenta el total.
9. «Cerrar caja»: con productos en el carrito pide confirmar; en `/pos/close`, cuenta $890 menos de lo esperado → «Faltante $ 890»; cierra; «Imprimir cierre».
10. Borra `.env.local` al terminar: `Remove-Item .env.local`.

- [ ] **Step 5: Handoff del frontend**

Entrega, en este orden: RED y GREEN literales de cada tarea; la salida de los Steps 1-3; el resultado del humo (con el tiempo del paso 4 y lo que se vio en la impresión); las decisiones P7 y P12 y las adaptaciones a la forma real de entitlements (`tenantModulesResponse`, `landingFor`, campo de módulo del sidebar) como puntos a confirmar; y las fallas previas de la línea base. La rama **no** se mergea ni se publica desde este plan; el orden de despliegue es **backend antes que frontend**, y los dos después de entitlements.

---

## Self-review

**Cobertura del spec:**

| Requisito del spec | Tarea |
|---|---|
| Módulo `Pos` en cuatro capas, schema `pos`, `PosDbContext`, proyección de outbox, factory de diseño | B1, B6 |
| `PosLayerTests` (capas, sin EF en Application, sólo Tenancy) | B1 |
| Seis permisos con `RequiredModules = [pos]`, políticas, admin con los seis, rol `cashier` con tres, `cashier` reservado | B2 |
| `VatIncludedLine` extraída; Quotations sin cambio de comportamiento | B3 |
| Tipos, `PosLimits`, escala de 2 decimales rechazada (nunca redondeada), `PosFinalConsumer` | B3, B5 |
| `CashSession`: abrir (versión 1), acumulados, anular resta exacto, cerrar con esperado y diferencia con signo | B4, B5 |
| `PosSale`: líneas, totales del ejemplo al centavo, ocho reglas de pago, total 0, desglose por tasa, anular | B5 |
| DDL con índice parcial, CHECKs, índices, FK a `companies` a mano, contador sin año | B6 |
| Traducción de errores por nombre (`already_open`, `id_taken`, 412) y `ResetAsync` | B6 |
| Concurrencia determinista cierre/venta | B6 |
| `IProductRepository.FindByCodeAsync` exacta y con mayúsculas | B7 |
| `GET /pos/register` (`openedBeforeToday`, empresas, versión), abrir (empresa por defecto, `company_required`, …), cerrar con `If-Match` | B8, B12 |
| Productos: búsqueda, by-code (inactivo marcado, 404), preview sin efectos con `NotFound`/`zeroTotalNotAllowed` | B9, B12 |
| Crear venta: huella canónica, repetición 200, `id_conflict`, choque al guardar en los dos órdenes, número después de validar, auditoría con `changedFields` | B10, B13 |
| Lectura, listados con alcance por cajero y días del tenant, `voidable`, anulación | B11, B13 |
| 403 entre tenants, 404 dentro del tenant | B11, B13 |
| `PosUserReferenceProbe` | B6, B13 |
| Doce endpoints, 201/200, 428/412, adaptadores en Bootstrapper | B12 |
| Chequeo previo del rol `cashier` y orden de despliegue | B14 |
| Clave del carrito por tenant **y usuario**; B no rehidrata a A (ronda 4, MAJOR 1) | F5 |
| 403 al reintentar no libera; sólo `discount_not_allowed` y 422 `pos.*` del paso 2 en adelante (ronda 4, MAJOR 2) | F4, F12 |
| `price_changed` invalida el preview y el refresco depende del bloqueo (ronda 4, menor 1) | F7, F12, F13 |
| `adoptSession` cierra el cobro sin intento (ronda 4, menor 2) | F5, F13 |
| Store: bloqueo, `pendingSaleId`, intento incierto, rehidratación `inFlight` → `uncertain` | F5 |
| Dinero en centavos, formato con centavos sólo si existen, billetes rápidos | F2 |
| Pagos armados en el cliente (efectivo omitido, total 0) | F3 |
| Cola de escaneo FIFO, guardia de ráfagas, atajos sólo en la lista | F8 |
| `ModuleGate` en el layout, `PosGate` sólo permisos, menú con alterno, aterrizaje del cajero | F9 |
| Ticket por `window.print`, página con nombre, portal | F6, F10 |
| Apertura y cierre con 412 / `not_open` recuperados | F11 |
| Diálogo de cobro: doble clic, incierto no descartable, Reintentar/Verificar, 401 y no listados inciertos | F12 |
| Pantalla de caja: «Cobrar» bloqueado con su motivo, avisos de precio/IVA, panel de éxito no modal, F8, banner de otro día, confirmación de cerrar con carrito | F13 |
| Listados con `voidable`, `already_voided` como éxito, cajero sólo con `pos.register.read` | F14 |
| Accesibilidad: `ul`/`li`, `aria-live`, ≥ 44/56 px | F13 (y skills en F10-F14) |
| Suites completas comparadas por nombre y humo | B14, F15 |

**Huecos conscientes:** el estilo visual fino queda a las skills de diseño (F10-F14) por pedido explícito; la aceptación del corte en la impresora térmica real y el tiempo < 15 s se verifican en el humo (F15), no en pruebas automáticas.

**Placeholders:** los únicos «completa después» son los cascarones de página de F9, que F11-F14 reemplazan con código completo dentro de este plan. Las adaptaciones a entitlements (forma de `tenantModulesResponse`, firma de `landingFor`, campo de módulo del sidebar) dependen de código que todavía no existe y están señaladas donde aplican.

**Consistencia de tipos:** `PosSaleCreation(Sale, Created)` (B10) es lo que lee el endpoint (B12); `CashSessionFilter`/`PosSaleFilter`/`PosSaleListRow` (B11) los usan repositorios y dobles; `PosCartState` (F5) expone exactamente las acciones que usan `use-pay-flow` (F12), `use-expected-refresh` y la página (F13); `classifyCreateSaleOutcome(result, wasUncertain)` (F4) recibe el `wasUncertain` que guarda `PosSaleAttempt` (F5); `PaymentLine` y `buildPaymentsBody` (F3) son los de `PaymentLines` y `usePayFlow` (F12).

**Review Focus:** los cinco tienen su prueba en la tarea dueña (B5, B9/B10/F5, B11, F5, F8).
