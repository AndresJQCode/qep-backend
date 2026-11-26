# Costo de producto y reporte de margen — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que cada variante tenga un costo unitario manual por moneda, visible y editable sólo con permiso; que cada línea de pedido y de venta POS congele su costo una sola vez, la primera vez que cuenta como vendida; y que `GET /reports/margin` y `/reports/margin/summary` den venta neta, costo, utilidad bruta y margen por moneda, por producto y por variante.

**Architecture:** El costo de la variante es una colección propia de `ProductVariant` (tabla `catalog.product_variant_costs`, gemela de `product_prices`). Quotations y Pos no ven Catalog: cada uno declara su puerto de costo (`IOrderVariantCostLookup`, `IPosVariantCostLookup`) y un solo adaptador en Bootstrapper (`CatalogVariantCostLookup`) implementa los dos. El congelamiento vive en el dominio: `Order` y `PosSale` reciben los costos ya buscados (`OrderLineCosts`, `PosSaleLineInput.UnitCost`) en todo método que pueda dejar una línea vendida, y `Order` gana `PaidAt`. El reporte es SQL crudo en Bootstrapper (`MarginReportSource`), un `UNION ALL` de `quotations.order_items` y `pos.sale_lines`, como `OrdersReportSource`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-10-costo-de-producto-design.md`](../specs/2026-11-10-costo-de-producto-design.md) · spec hermano (sólo contexto): `qep-frontend/docs/superpowers/specs/2026-11-10-costo-de-producto-design.md` · base: spec y plan de `feature/estado-del-envio` (`docs/superpowers/specs/2026-11-10-estados-del-pedido-design.md`, `docs/superpowers/plans/2026-11-10-estados-del-pedido.md`).

---

## Antes de empezar

- **Este plan se escribió con `feature/estado-del-envio` a medio construir** (HEAD `8695f50a`, su Tarea 5 en curso). `MarkOrderPaid.cs`, `UnmarkOrderPaid.cs` y `OrderAutoArchive.cs` son de su Tarea 5; las firmas de `Order` se leyeron de `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` en ese worktree. Si al ejecutar algo se movió, busca el símbolo; gana el código.
- Todo comando es PowerShell, uno por línea, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas**: es del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y, en la misma sesión, `$env:Seed__Enabled = "false"`. Un proyecto a la vez, con `--filter` a la clase tocada y `-- xUnit.MaxParallelThreads=4`. Las unitarias se corren en cada tarea; la corrida de integración completa de los proyectos tocados va una sola vez, en la Tarea 8.
- Migraciones con el factory de diseño: `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- Antes de **cada** commit: `git branch --show-current` tiene que decir `feature/costo-de-producto`; si dice otra cosa, frena y pregunta. Commit con `git commit -m "…" -- <rutas>`.

## Global Constraints

- Costo de variante: una fila por moneda, `costs: { "COP": 12000 }`, sin conversión; monto `> 0`; `numeric(18,2)` como `product_prices.amount`; moneda validada con `CurrencyAmountRules` (código `tenancy.currency.unsupported`, clave `{ruta}.{CODE}`), igual que los precios. El costo es **sin IVA**.
- `ProductVariantResponse.costs`: `null` = el llamador no tiene permiso para verlo; `{}` = la variante no tiene costo. Viaja siempre (nunca omitido).
- `ProductVariantRequest.costs`: `null` o ausente = conservar; objeto = reemplazar (`{}` borra). Un objeto sin `catalog.product_cost.manage` → **403 `catalog.product_cost.forbidden`**, en `POST` y en `PUT`.
- Permisos (texto exacto): `catalog.product_cost.read` (`[Catalog]`), `catalog.product_cost.manage` (`[Catalog]`), `reporting.margin.read` (`[Reporting, Catalog]`). Sólo en el rol `admin`. Cada uno con `PermissionDefinition`, entrada en el `RoleDefinition` de admin y `AddPolicy`.
- Líneas (`OrderItem`, `PosSaleLine`): `UnitCost decimal?`, `CostSource LineCostSource? { Manual }`, `CostCapturedAt DateTimeOffset?`. Se congela **una sola vez**; nunca se reescribe.
- Ninguna respuesta de pedido, línea de pedido, venta POS, búsqueda del POS, cotización ni selector de productos expone costo.
- Reporte: pedidos con `payment_status = 'FullPaymentReceived'`, `status <> 'Cancelled'` y `paid_at` en el período; ventas POS `status = 'Completed'` con `created_at` en el período. Venta neta = `subtotal` de la línea. Costo = `round(unit_cost × quantity, 2)`. Margen % = utilidad ÷ venta neta × 100, a 1 decimal, `null` si la venta neta es 0. Sólo líneas con `unit_cost` entran al margen; las demás se cuentan aparte. Todo por moneda, sin convertir.
- `Modules.Quotations.*` y `Modules.Pos.*` no referencian `Modules.Catalog.*`; `Modules.<M>.Application` no referencia EF Core ni Npgsql (`tests/ArchitectureTests`).
- TDD: RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- Commits: conventional commits, **sin `Co-Authored-By` ni atribución de IA**.
- Comentarios de código en español, tuteando. Copy legible por personas en español colombiano, tuteando.
- `docs/estado.md` **no** se toca en esta rama (se actualiza en `develop`, al mergear).

## Decisiones de este plan (lo que el spec deja al constructor)

| # | Decisión | Por qué |
| --- | --- | --- |
| C1 | Tabla `catalog.product_variant_costs` (`variant_id`, `currency character(3)`, `amount numeric(18,2)`, PK `(variant_id, currency)`, `CK_product_variant_costs_amount_positive` `amount > 0`, FK `CASCADE` a `product_variants`). Entidad propia `ProductVariantCost`, owned por `ProductVariant` como `ProductPrice`. | P1 del spec: igual que los precios. Owned se carga solo con la variante, sin tocar los `Include` de `ProductRepository`. |
| C2 | **Ver** costo = `catalog.product_cost.read` **o** `catalog.product_cost.manage` (`CatalogCostAccess.CanRead`). | §5 deja elegir entre impedir `manage` sin `read` o tratarlo como `read`. Los roles son código y el editor de roles es del frontend; el handler es el único punto que ve los dos casos, y quien puede escribir un valor ya lo conoce. |
| C3 | Una variante **nueva** con `costs: null` nace sin costo; no copia el de la variante hermana (a diferencia de `pricing: null`). | «Conservar» sólo aplica a lo guardado. Copiar un costo es inventar un dato que nadie escribió. |
| C4 | `CostSource = Manual` en **toda** línea congelada, con costo o sin él; `null` sólo mientras no se ha congelado. `CostCapturedAt` distingue «congelada sin costo» (`UnitCost null`, `CostCapturedAt` no nulo). | §4.2: «`null` mientras no se ha congelado». Hoy la única fuente es el campo manual, haya valor o no. |
| C5 | `order_items` y `sale_lines` guardan `unit_cost numeric(18,2)`, no `(14,2)` como `unit_price`. | Un costo válido en catálogo (18,2) no puede desbordar la línea al congelarse. |
| C6 | `Order` recibe los costos ya buscados en un valor de dominio, `OrderLineCosts`, en **cada** método que puede dejar una línea vendida: `MarkPaid`, `SyncExternalPaymentStatus`, `CreateExternal` y `AddItem`. Si el pedido está o queda pagado y falta una variante que había que congelar, el dominio lanza `InvalidOperationException` (error de programación, no 422). | §4.3: «el congelamiento vive en el dominio, para que ninguna vía nueva a pagado lo olvide». El parámetro obligatorio hace que el compilador señale cada llamador; la excepción, que nadie pase `None` a un pedido pagado. |
| C7 | `Order.PaidAt` (`paid_at timestamptz NULL`): se escribe en `MarkPaid`, en `SyncExternalPaymentStatus` hacia `FullPaymentReceived` y al crear un pedido que nace pagado; se limpia en `UnmarkPaid`, en `SyncExternalPaymentStatus` hacia otro estado y cuando `RecalculatePaymentStatus` baja a `PartialPaymentReceived`. Backfill: `paid_at = COALESCE(paid_marked_at, converted_at)` donde `payment_status = 'FullPaymentReceived'`. Índice `IX_orders_tenant_paid_at (tenant_id, paid_at)`. | §4.4: `PaidMarkedAt` no cubre Shopify (`SyncExternalPaymentStatus` no escribe fecha). |
| C8 | Puertos: `IOrderVariantCostLookup` (Quotations.Application) e `IPosVariantCostLookup` (Pos.Application), misma firma; adaptador único `CatalogVariantCostLookup` en Bootstrapper. **No** se extiende `IPosProductLookup`. | `PosProductRef` también alimenta la búsqueda del POS (§6.1: no expone costo); un puerto aparte hace imposible que el costo viaje por ahí. |
| C9 | El listado del reporte cuenta **sólo líneas con costo**: un producto con todas sus líneas sin costo no aparece; `quantity` es la de las líneas con costo. Lo sin costo se ve sólo en el resumen (`uncostedLines`, `uncostedNetSales`). | §6.3: «Sólo líneas con `UnitCost` entran a venta neta, costo, utilidad y margen». El aviso del frontend sale del resumen. |
| C10 | El resumen es una lista de objetos por moneda con decimales planos (`MarginReportCurrencyDto`), no listas de `ReportMoneyDto` por cifra. En orden de catálogo (`Currencies.OrderOf`); vacía si no hay filas. | §6.3 lo describe «por moneda, `{ netSales, cost, … }`», y cada objeto ya es de una sola moneda. |
| C11 | Orden del listado: `(currency = @default_currency) DESC, gross_profit DESC, product_id`. La moneda por defecto sale de `ITenantDefaultCurrency`. | §6.3: «utilidad bruta descendente en la moneda por defecto»; las filas son por producto **y** moneda, así que las de la moneda por defecto van primero. |
| C12 | `marginPercent` se calcula en C# sobre las sumas de la base (`ReportMargin.Percent`, `MidpointRounding.AwayFromZero`, el mismo redondeo que `round()` de PostgreSQL). | Las sumas siguen en la base; el porcentaje es una división de dos números ya agregados, y así tiene prueba unitaria. |
| C13 | Riesgo `"medium"` para los tres permisos. | §5 pide el nivel de un dato comercial sensible; `reporting.price_change.read` ya es `"medium"` por la misma razón («el margen visto de costado»). |
| C14 | Tarea de permisos **antes** que la del API de catálogo. | `EveryPermissionConstantDeclaresItsModules` se pone roja en cuanto existe una constante sin su `PermissionDefinition`; así ninguna tarea deja la suite en rojo. |

## Review Focus

1. **Guardar un producto sin permiso de costo.** Un asesor con `catalog.product.manage` y sin `product_cost.*` edita el nombre y guarda: la respuesta trae `costs: null` y el costo guardado sigue igual. Lo amarra la Tarea 3 (`PutWithoutCostsKeepsTheSavedCostForAManagerWithoutCostPermissions`).
2. **Shopify manda `paid` dos veces, o el pedido entra pagado y después se resincroniza.** Las líneas congelan una vez, al entrar; ni la reingesta ni un `paid` repetido las reescriben aunque el costo del catálogo haya cambiado. Lo amarran las Tareas 5 (`SyncingPaidTwiceDoesNotRefreezeTheLines`) y 6 (`ReingestingAPaidExternalOrderDoesNotLookUpCostsAgain`).
3. **Un pago parcial que después se completa con «Marcar como pagado».** El parcial no congela; el pagado sí, con el costo de ese momento, y cuenta en la fecha del pagado. Lo amarra la Tarea 5 (`PartialPaymentDoesNotFreezeAndMarkPaidDoes`).
4. **Un pedido pagado en USD con una variante que sólo tiene costo en COP.** La línea queda congelada sin costo y aparece en `uncostedLines` de USD, nunca como costo 0 ni convertida. Lo amarra la Tarea 7 (`AVariantWithoutCostInTheOrderCurrencyIsUncosted`).
5. **Un pedido pagado antes del despliegue.** Aparece en el reporte en la fecha de `paid_marked_at` (o `converted_at`), con todas sus líneas «sin costo». Lo amarran las Tareas 6 (`PaidAtBackfillUsesPaidMarkedAtThenConvertedAt`) y 7 (`AnOrderPaidBeforeTheDeployIsCountedAsUncosted`).

---

### Task 0: Precondiciones y worktree

**Files:** ninguno de producto.

- [ ] **Step 1: Verificar la base**

```powershell
git -C qep-backend fetch origin
git -C qep-backend log origin/develop --oneline -1
git -C qep-backend branch -a --contains <sha de la punta de feature/estado-del-envio>
```

Expected: `develop` contiene la punta de `feature/estado-del-envio` (merge hecho). Si no, **frena**: este plan no se construye en paralelo (spec §9).

- [ ] **Step 2: Crear el worktree desde `develop`**

```powershell
git -C qep-backend worktree add ..\qep-backend-worktrees\costo-de-producto -b feature/costo-de-producto develop
git -C ..\qep-backend-worktrees\costo-de-producto branch --show-current
```

Expected: `feature/costo-de-producto`. Copia este plan y el spec si todavía no están en `develop` y commitéalos (`docs: plan product cost and margin report`).

- [ ] **Step 3: Línea base**

```powershell
dotnet build Backend.slnx
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Expected: build sin errores y `Passed!`. Anota el número que hoy fija `PermissionDiscoveryFinds…Constants` (74 en `932e5eb2`; la rama de estados puede haberlo movido): la Tarea 2 le suma 3.

---

### Task 1: Costo de la variante — dominio, EF y migración

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Domain/ProductVariantCost.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/ProductVariant.cs` (`Costs`, `CostIn`, `Apply`, `ReplaceCosts`), mismo archivo `ProductVariantInput` (`Costs`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` (junto a `product_prices`, `:230-244`)
- Create: migración `AddProductVariantCosts` en `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductVariantTests.cs`, `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogVariantPersistenceTests.cs`

**Interfaces:**
- Produces: `public sealed class ProductVariantCost { string Currency; decimal Amount; internal ProductVariantCost(string currency, decimal amount); internal void ChangeAmount(decimal amount); }` (forma de `ProductPrice`).
- Produces: `ProductVariant.Costs : IReadOnlyCollection<ProductVariantCost>`, `ProductVariant.CostIn(string currency) : decimal?` (null si no hay costo en esa moneda; nunca convierte).
- Produces: `ProductVariantInput.Costs : IReadOnlyDictionary<string, decimal>?` — `null` = conservar los guardados; diccionario = reemplazar; vacío = borrar. Claves ya normalizadas por la aplicación.

- [ ] **Step 1: Write the failing tests** (en `ProductVariantTests`, con el armado de producto que ya usa el archivo)
  - `CreateWithCostsStoresOneRowPerCurrency`: input con `Costs = { COP: 12000, USD: 3 }` → `variant.CostIn("COP") == 12000m`, `CostIn("USD") == 3m`, `CostIn("EUR") is null`.
  - `UpdateWithNullCostsKeepsTheSavedOnes`: crear con `{ COP: 12000 }`, `Update` con `Costs = null` → `CostIn("COP") == 12000m`.
  - `UpdateWithAMapReplacesTheCosts`: crear con `{ COP: 12000, USD: 3 }`, `Update` con `{ COP: 15000 }` → `COP == 15000m`, `USD is null`, `Costs.Count == 1`.
  - `UpdateWithAnEmptyMapClearsTheCosts`: `{}` → `Costs` vacío.
  - `ANonPositiveCostIsAProgrammingError`: `{ COP: 0m }` → `Assert.Throws<ArgumentOutOfRangeException>` (el 422 lo da el validador, Tarea 3).
  - `ANewVariantWithoutCostsStartsEmpty`: variante nueva en `Update` con `Costs = null` → `Costs` vacío.

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductVariantTests"`
Expected: build error `CS0117`/`CS1061` sobre `Costs` / `CostIn`.

- [ ] **Step 3: Implementar el dominio**

`ProductVariant.Apply` llama `ReplaceCosts(input.Costs)` sólo si no es `null`. `ReplaceCosts` va en el lugar, como `ReplacePrices` (mismo comentario: EF sigue la fila por `(dueño, moneda)`), y valida forma de moneda (`EnsureCurrencyCodeShape`) y `amount > 0` (`ArgumentOutOfRangeException`).

- [ ] **Step 4: Run to verify GREEN** — mismo comando. Expected: `Passed!`.

- [ ] **Step 5: Mapear y migrar**

`variant.OwnsMany(value => value.Costs, …)` copiando el bloque de precios con `ToTable("product_variant_costs", "catalog", t => t.HasCheckConstraint("CK_product_variant_costs_amount_positive", "amount > 0"))` y `UsePropertyAccessMode(Field)`.

```powershell
dotnet ef migrations add AddProductVariantCosts --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations
```

Expected: una migración que crea sólo `catalog.product_variant_costs` con su PK, su CHECK y su FK `ON DELETE CASCADE`. Sin backfill.

- [ ] **Step 6: Prueba de persistencia** (integración, `CatalogVariantPersistenceTests`)
  - `VariantCostsRoundTripAndCascadeWithTheVariant`: guardar un producto con `{ COP: 12000.50 }`, releerlo en un `DbContext` nuevo → `CostIn("COP") == 12000.50m`; borrar la variante (`deletedVariantIds` sin uso) → `SELECT count(*) FROM catalog.product_variant_costs WHERE variant_id = @id` da 0.

RED (antes del mapeo) y GREEN:

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CatalogVariantPersistenceTests" -- xUnit.MaxParallelThreads=4
```

- [ ] **Step 7: Commit**

```powershell
git commit -m "feat(catalog): store a manual unit cost per currency on variants" -- src/Modules/Catalog tests/Modules/Catalog
```

---

### Task 2: Permisos

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogPermissions.cs`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingPermissions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin` `:1618-1740`; `PermissionDefinition` de catálogo `:1924-1951` y de reporting `:2087-2126`; políticas junto a `:2573` y `:2683`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` (si cuenta los permisos de admin)

**Interfaces:**
- Produces: `CatalogPermissions.ProductCostRead = "catalog.product_cost.read"`, `CatalogPermissions.ProductCostManage = "catalog.product_cost.manage"`, `ReportingPermissions.MarginRead = "reporting.margin.read"`.
- Definiciones (etiqueta, descripción, categoría, riesgo, módulos):
  - `ProductCostRead`: «Ver costos de productos», «Permite ver el costo unitario de las variantes.», `"Catalog"`, `"medium"`, `[ModuleKeys.Catalog]`.
  - `ProductCostManage`: «Gestionar costos de productos», «Permite escribir y borrar el costo unitario de las variantes.», `"Catalog"`, `"medium"`, `[ModuleKeys.Catalog]`.
  - `MarginRead`: «Reporte de margen», «Permite consultar ventas netas, costo y margen bruto por producto.», `"Reporting"`, `"medium"`, `[ModuleKeys.Reporting, ModuleKeys.Catalog]`.

- [ ] **Step 1: Write the failing tests** (`CompositionRootTests`)
  - Renombra `PermissionDiscoveryFinds…Constants` sumándole 3 al número de la Tarea 0 (p. ej. `…SeventySevenConstants`, `Assert.Equal(77, …)`) y agrega al comentario «+ los tres de costo y margen (spec 2026-11-10 §5)».
  - `OnlyTheAdminRoleCarriesTheCostAndMarginPermissions` (forma de `OnlyTheAdminRoleCarriesTheUsagePermission`): los tres en `PermissionsFor("admin")`; ninguno en `advisor`, `billing` ni `cashier`; `RequiredModules` de los dos de catálogo = `[catalog]`, el de margen = `[reporting, catalog]` (compara con `ModuleKeys`).
  - `TheCostAndMarginPoliciesAreRegistered`: con el proveedor de `BuildPlatformServices()`, `IAuthorizationPolicyProvider.GetPolicyAsync(p)` no es `null` para los tres (sin la política, el síntoma es 500).

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: `CS0117` por las constantes; tras declararlas, `EveryPermissionConstantDeclaresItsModules` falla nombrando los tres.

- [ ] **Step 3: Registrar** — constantes, las tres `PermissionDefinition`, las tres en el `RoleDefinition` de admin (comentario: sólo admin, dato comercial sensible) y las tres `AddPolicy(…, policy => AddPermissionRequirement(policy, …))`.

- [ ] **Step 4: Run to verify GREEN** — mismo comando, más `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests" -- xUnit.MaxParallelThreads=4` (ajusta su conteo si cuenta los permisos de admin). Expected: `Passed!`.

- [ ] **Step 5: Commit** — `feat(authorization): add product cost and margin report permissions`, rutas tocadas.

---

### Task 3: Costos en el API de catálogo

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/CatalogCostAccess.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs` (`ProductVariantResponse` `:97`, `ProductVariantRequest` `:286`)
- Modify: `…/ProductMapping.cs` (`ProductReadLookups`, `ToDtosAsync`, `ToResponse` `:187-229`), `…/ProductVariantChanges.cs` (`Resolve` `:40-89`), `…/ProductWriteRules.cs` (regla por variante, `:182-190`), `…/CreateProduct.cs`, `…/UpdateProduct.cs`, `…/GetProduct.cs`, `…/ActivateProduct.cs`, `…/DeactivateProduct.cs`, `…/CopyPriceScales.cs` (los seis llamadores de `ToDtosAsync`)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductMappingTests.cs`, `…/ProductVariantChangesTests.cs`; `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductVariantCostsApiTests.cs` (nuevo), `…/ProductBodies.cs`

**Interfaces:**
- Consumes: `ProductVariant.Costs`, `ProductVariantInput.Costs` (Tarea 1); `CatalogPermissions.ProductCostRead/Manage` (Tarea 2).
- Produces: `ProductVariantResponse(…, bool CanDelete, IReadOnlyDictionary<string, decimal>? Costs)` — último parámetro, en orden de catálogo. Doc en el DTO: `null` = sin permiso, `{}` = sin costo (BFF: la pantalla distingue «no puedes verlo» de «no tiene»).
- Produces: `ProductVariantRequest(…, bool? IsActive, IReadOnlyDictionary<string, decimal>? Costs = null)`.
- Produces: `internal static class CatalogCostAccess { bool CanRead(IExecutionContext); void EnsureCanWrite(IExecutionContext, IEnumerable<ProductVariantRequest>?); }` — `EnsureCanWrite` lanza `RequestForbiddenException("catalog.product_cost.forbidden", "The subject cannot write product costs.")` si alguna variante trae `Costs` no nulo sin `ProductCostManage`.
- Produces: `ProductReadLookups(…, IReadOnlySet<Guid>? UsedVariantIds = null, bool IncludeCosts = false)`; `ToDtosAsync(…, Guid tenantId, bool includeCosts, CancellationToken)`.

- [ ] **Step 1: Write the failing unit tests**
  - `ProductMappingTests.CostsAreNullWithoutReadAndAMapWithIt`: `IncludeCosts = false` → `variant.Costs is null`; `true` sin costos → vacío (no `null`); `true` con `{ USD: 3, COP: 12000 }` → claves en orden `["COP", "USD"]`.
  - `ProductVariantChangesTests.NullCostsReachTheDomainAsNull` y `ACostMapIsNormalised`: `Costs = { "cop": 12000 }` → input con `{ "COP": 12000 }`; `Costs = null` → `input.Costs is null` (también para una variante nueva, C3).

Run: `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductMappingTests|FullyQualifiedName~ProductVariantChangesTests"` → RED por compilación.

- [ ] **Step 2: Implementar mapeo y resolución** — `ToResponse` agrega `Costs: lookups.IncludeCosts ? CostsOf(variant) : null` (mismo `InCatalogueOrder` que los precios); `Resolve` copia `request.Costs` normalizado con `Currencies.Normalize`. Run → GREEN.

- [ ] **Step 3: Write the failing API tests** (`ProductVariantCostsApiTests`; `ProductBodies.Variant` gana `object? costs = null`)
  - `GetWithoutCostReadReturnsNullCosts`: permisos `product.read` → `variants[0].costs` es JSON `null` (comprobar en el `JsonElement`: la propiedad existe y es `Null`).
  - `GetWithCostReadReturnsTheMapOrEmpty`: `product.read` + `product_cost.read` → `{ "COP": 12000 }` en la que tiene, `{}` en la que no.
  - `ManageWithoutReadAlsoSeesCosts` (C2).
  - `PostWithCostsWithoutManageIs403`: `product.manage` sin `product_cost.manage`, `costs: { COP: 12000 }` → 403, `code == "catalog.product_cost.forbidden"`; no se creó el producto (listado vacío).
  - `PutWithCostsWithoutManageIs403AndChangesNothing`: mismo código; `GET` posterior sigue con el costo anterior y la misma `version`.
  - `PutWithoutCostsKeepsTheSavedCostForAManagerWithoutCostPermissions`: crear con costo (cliente con manage), editar el nombre con un cliente `product.read`+`product.manage` sin `costs` → 200, `costs: null` en la respuesta; releer con `product_cost.read` → `{ "COP": 12000 }`.
  - `PutWithAnEmptyMapClearsTheCost` → `{}`.
  - `ZeroOrUnknownCurrencyCostIs422WithTheField`: `{ COP: 0 }` → 422 `validation.failed`, `errors` con la clave `Variants[0].Costs.COP`; `{ XXX: 5 }` → 422 con `code` de la fila `tenancy.currency.unsupported` en `Variants[0].Costs.XXX`. (Confirma el prefijo exacto con el que ya devuelve un precio inválido, `ProductPricingApiTests`, y úsalo igual.)
  - `TheProductListAndSelectorNeverCarryCosts`: `GET /catalog/products?includeVariants=true` con `product_cost.read` → el texto crudo de la respuesta no contiene `"costs"`.

Run:

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductVariantCostsApiTests" -- xUnit.MaxParallelThreads=4
```

Expected RED: las aserciones de `costs` y los códigos fallan.

- [ ] **Step 4: Implementar** — en `CreateProductHandler` y `UpdateProductHandler`, `CatalogCostAccess.EnsureCanWrite(executionContext, command.Variants)` **después** de `EnsureAuthorized(ProductManage)` y **antes** del validador; los seis handlers pasan `includeCosts: CatalogCostAccess.CanRead(executionContext)`. En `ProductWriteRules`, por variante: `Costs` pasa por `CurrencyAmountRules.Check(costs, $"{path}.Costs")` más una falla «The cost must be greater than zero.» cuando el monto es `0`.

- [ ] **Step 5: Run to verify GREEN** — los dos comandos anteriores, más `--filter "FullyQualifiedName~Product"` sobre el proyecto de integración de Catalog (las pruebas que construyen `ProductVariantResponse` posicionalmente se ajustan). Expected: `Passed!`.

- [ ] **Step 6: Commit** — `feat(catalog): expose and write variant costs behind their permissions`.

---

### Task 4: POS — la venta congela el costo

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Domain/LineCostSource.cs`, `src/Modules/Pos/Modules.Pos.Application/IPosVariantCostLookup.cs`, `src/Bootstrapper/CatalogVariantCostLookup.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosSaleInputs.cs` (`PosSaleLineInput`), `…/PosSaleLine.cs` (`Create` `:61`), `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs` (`ResolveLinesAsync` `:246-285`), `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs` (`sale_lines`, `:168-195`), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `IPosProductLookup`, `:1235`)
- Create: migración `AddSaleLineCost` (Pos)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs`, `…/CreatePosSaleHandlerTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleCostApiTests.cs` (nuevo)

**Interfaces:**
- Produces: `public enum LineCostSource { Manual }` (Pos.Domain; persistido como texto, `varchar(20)`).
- Produces: `PosSaleLineInput(…, int TaxPercentage, decimal? UnitCost = null)`; `PosSaleLine.UnitCost : decimal?`, `CostSource : LineCostSource?`, `CostCapturedAt : DateTimeOffset?`. `PosSale.Create` congela **todas** las líneas en `at` (C4).
- Produces: `public interface IPosVariantCostLookup { Task<IReadOnlyDictionary<Guid, decimal>> FindAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken); }` — sólo las variantes del tenant con costo en esa moneda.
- Produces: `internal sealed class CatalogVariantCostLookup(CatalogDbContext catalog) : IPosVariantCostLookup` — una consulta sobre `catalog.product_variant_costs` unida a `product_variants` por `tenant_id`; lista vacía → sin consulta.

- [ ] **Step 1: Write the failing unit tests**
  - `PosSaleTests.CreateFreezesEachLineCostOnce`: dos líneas, una con `UnitCost = 6000m` y otra `null` → la primera `UnitCost == 6000m`, `CostSource == Manual`, `CostCapturedAt == at`; la segunda `UnitCost is null`, `CostSource == Manual`, `CostCapturedAt == at`.
  - `CreatePosSaleHandlerTests.TheLineTakesTheCostInTheRegisterCurrency`: doble de `IPosVariantCostLookup` que devuelve `{ variantId: 6000m }` sólo para `"COP"`; caja en COP → la línea guardada lleva `6000m`; el doble registra `currency == "COP"` y los ids de las líneas.
  - `AReplayDoesNotLookUpCosts`: reintento idempotente (paso 2 del handler) → el doble no se llamó.

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosSaleTests|FullyQualifiedName~CreatePosSaleHandlerTests"` → RED por compilación.

- [ ] **Step 2: Implementar** — `ResolveLinesAsync` llama `IPosVariantCostLookup.FindAsync(command.TenantId, session.Currency, ids, ct)` junto al `FindManyAsync` existente y pasa `costs.GetValueOrDefault(id)` como `decimal?` (usa `TryGetValue`; un `0` no existe porque el catálogo exige `> 0`). Run → GREEN.

- [ ] **Step 3: Persistencia, adaptador y migración** — columnas `unit_cost numeric(18,2) NULL`, `cost_source varchar(20) NULL`, `cost_captured_at timestamptz NULL` en `pos.sale_lines` (C5); `services.AddScoped<IPosVariantCostLookup, CatalogVariantCostLookup>()`.

```powershell
dotnet ef migrations add AddSaleLineCost --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations
```

Expected: tres `AddColumn` nullable, sin backfill.

- [ ] **Step 4: Write the failing API tests** (`PosSaleCostApiTests`, con `PosApiHarness.EnablePosAsync`; el producto se crea por la API de catálogo con `costs: { COP: 6000 }` y `catalog.product_cost.manage`)
  - `ASaleFreezesTheCatalogCost`: vender 2 → `SELECT unit_cost, cost_source, cost_captured_at FROM pos.sale_lines WHERE sale_id = @id` da `6000.00`, `'Manual'`, no nulo.
  - `ChangingTheCatalogCostDoesNotMoveASale`: después de la venta, `PUT` con `costs: { COP: 9000 }` → la línea sigue en `6000.00`.
  - `AVariantWithoutCostIsFrozenAsUncosted`: `unit_cost IS NULL`, `cost_captured_at IS NOT NULL`.
  - `NeitherTheSaleNorTheSearchCarryCosts`: el texto crudo de la respuesta de `POST /pos/sales`, `GET /pos/sales/{id}` y la búsqueda de productos del POS no contiene `cost` (ignorando mayúsculas).

```powershell
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosSaleCostApiTests" -- xUnit.MaxParallelThreads=4
```

RED antes del cableado, GREEN después.

- [ ] **Step 5: Commit** — `feat(pos): freeze the variant cost on each sale line`.

---

### Task 5: Pedido — congelamiento y `PaidAt` en el dominio

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/LineCostSource.cs`, `…/OrderLineCosts.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderItem.cs`, `…/Order.cs` (`MarkPaid` `:954`, `UnmarkPaid` `:966`, `AddItem` `:718`, `RecalculatePaymentStatus` `:1242`, `SyncExternalPaymentStatus` `:1275`, `CreateExternal` `:459`, `Build` `:563`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderLineCostTests.cs` (nuevo); ajustar llamadores en `OrderTests.cs`, `OrderStatusAxesTests.cs`, `OrderPaymentCalculationTests.cs` y demás que llamen a los métodos cambiados (el compilador los nombra)

**Interfaces:**
- Produces: `public enum LineCostSource { Manual }` (Quotations.Domain).
- Produces: `public sealed class OrderLineCosts { public static OrderLineCosts None { get; } public static OrderLineCosts Of(IReadOnlyCollection<Guid> lookedUp, IReadOnlyDictionary<Guid, decimal> found); }` con `internal decimal? For(Guid variantId)` que lanza `InvalidOperationException` si la variante no estaba en `lookedUp` (C6).
- Produces: `OrderItem.UnitCost : decimal?`, `CostSource : LineCostSource?`, `CostCapturedAt : DateTimeOffset?`; `internal void FreezeCost(decimal? unitCost, DateTimeOffset at)` — no hace nada si `CostCapturedAt` ya tiene valor.
- Produces: `Order.PaidAt : DateTimeOffset?`; `Order.UncostedVariantIds : IReadOnlyList<Guid>` (variantes de líneas con `CostCapturedAt` nulo, sin repetir).
- Produces (firmas nuevas):
  - `MarkPaid(MemberId markedBy, bool requirePaymentProof, OrderLineCosts costs, DateTimeOffset occurredAt)`
  - `SyncExternalPaymentStatus(OrderPaymentStatus status, bool autoArchive, OrderLineCosts costs, DateTimeOffset occurredAt)`
  - `AddItem(OrderItemId itemId, Guid variantId, Guid productId, decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage, OrderLineCosts costs, MemberId updatedBy, DateTimeOffset occurredAt)`
  - `CreateExternal(…, string? externalShippingTitle, OrderLineCosts costs)` — último parámetro; `Build` recibe `OrderLineCosts costs` y, si el pedido nace `FullPaymentReceived`, congela todas las líneas y fija `PaidAt = occurredAt`. `CreateManual`, `CreateFromStore`, `QuoteForStore` y `Preview` pasan `OrderLineCosts.None`.

- [ ] **Step 1: Write the failing tests** (`OrderLineCostTests`; costos `OrderLineCosts.Of([v1, v2], { v1: 6000m })`, instantes `t1 < t2 < t3`)
  - `MarkPaidFreezesEveryLineOnce`: v1 → `UnitCost 6000`, `Manual`, `t1`; v2 → `null`, `Manual`, `t1`; `PaidAt == t1`.
  - `UnmarkAndRemarkKeepTheFrozenCostAndMoveThePaidAt`: `MarkPaid(t1)`, `UnmarkPaid(t2)` → `PaidAt is null`, v1 sigue `6000`/`t1`; `MarkPaid(t3)` con costos `{ v1: 9000m }` → v1 sigue `6000`/`t1`, `PaidAt == t3`.
  - `PartialPaymentDoesNotFreezeAndMarkPaidDoes`: `AddPaymentProofs` por la mitad → `PartialPaymentReceived`, todas `CostCapturedAt is null`, `PaidAt is null`; `MarkPaid(t2)` → congeladas en `t2`.
  - `ALineAddedToAPaidOrderFreezesOnArrival`: pagado en `t1`, `AddItem(v3, …, Of([v3], { v3: 500m }), …, t2)` → v3 `500`/`t2`; las demás siguen en `t1`.
  - `ALineAddedToAnUnpaidOrderDoesNotFreeze`: `AddItem(…, OrderLineCosts.None, …)` → `CostCapturedAt is null`.
  - `AddingToAPaidOrderWithoutItsCostIsAProgrammingError`: pagado; `AddItem(…, OrderLineCosts.None, …)` → `InvalidOperationException`.
  - `ChangingTheQuantityKeepsTheUnitCost`: pagado; `UpdateItemQuantity(v1, 5, …)` → `UnitCost == 6000m`, `CostCapturedAt == t1`.
  - `AnExternalOrderBornPaidFreezesAtCreation`: `CreateExternal(…, FullPaymentReceived, …, occurredAt: t1, …, costs)` → líneas congeladas en `t1`, `PaidAt == t1`; con `PaymentPending` → nada congelado, `PaidAt is null`.
  - `SyncingToPaidFreezesAndSetsPaidAt` y `SyncingPaidTwiceDoesNotRefreezeTheLines`: `Sync(Full, t1, costs)`, `Sync(Partial, t2)` → `PaidAt is null`, costos intactos; `Sync(Full, t3, { v1: 9000m })` → v1 sigue `6000`/`t1`, `PaidAt == t3`.
  - `DroppingToPartialAfterAnEditClearsPaidAtAndKeepsCosts`: pagado con comprobante que cubre el neto; agregar una línea (congela) y `RecalculatePaymentStatus` sube el neto → `PartialPaymentReceived`, `PaidAt is null`, costos intactos.

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderLineCostTests"` → RED por compilación.

- [ ] **Step 2: Implementar** — un método privado `FreezeLineCosts(OrderLineCosts costs, DateTimeOffset at)` recorre `_items` y llama `item.FreezeCost(costs.For(item.VariantId), at)` en las no congeladas; lo llaman `MarkPaid`, `SyncExternalPaymentStatus` (sólo hacia `FullPaymentReceived`) y `Build`. `AddItem` congela la línea nueva si `PaymentStatus == FullPaymentReceived`. `PaidAt` según C7.

- [ ] **Step 3: Run to verify GREEN**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`
Expected: `Passed!` (los llamadores de prueba pasan `OrderLineCosts.None` o un `Of(...)` explícito). La aplicación todavía no compila con las firmas nuevas: la Tarea 6 cablea los handlers. Si el proyecto de pruebas depende de Application, haz las Tareas 5 y 6 en un mismo commit y deja este paso como verificación intermedia del dominio.

- [ ] **Step 4: Commit** — `feat(orders): freeze line costs and record the paid instant in the domain` (o junto con la Tarea 6, ver Step 3).

---

### Task 6: Pedido — persistencia, migración y casos de uso

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IOrderVariantCostLookup.cs` (puerto + `OrderLineCostsLoader`)
- Modify: `src/Bootstrapper/CatalogVariantCostLookup.cs` (implementa también `IOrderVariantCostLookup`), `src/Bootstrapper/QepServiceCollectionExtensions.cs`
- Modify: `…Application/MarkOrderPaid.cs`, `…/IExternalOrderWriter.cs` (`ExternalOrderService.CreateAsync` `:137-212`, `SyncPaymentStatusAsync` `:308-324`), `…/AddOrderItems.cs`, `…/OrderItemEdits.cs` (`ApplyAsync`), `…/SaveOrderEdits.cs`, `…/PreviewOrderEdits.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`orders` `:578-600`, `order_items` `:698-720`)
- Create: migración `AddLineCostAndPaidAt` (Quotations)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/MarkOrderPaidHandlerTests.cs`, `…/OrderItemEditsTests.cs`; `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderLineCostApiTests.cs` (nuevo), `…/ExternalOrderServiceTests.cs`, `…/PaidAtMigrationTests.cs` (nuevo)

**Interfaces:**
- Consumes: Tarea 5 entera.
- Produces: `public interface IOrderVariantCostLookup { Task<IReadOnlyDictionary<Guid, decimal>> FindAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken); }` (misma semántica que el de Pos).
- Produces: `internal static class OrderLineCostsLoader { Task<OrderLineCosts> LoadAsync(IOrderVariantCostLookup lookup, Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); }` — lista vacía → `OrderLineCosts.None` sin consultar.
- Qué busca cada camino (siempre en `order.Currency`):
  - `MarkOrderPaidHandler`: `order.UncostedVariantIds`.
  - `ExternalOrderService.CreateAsync`: las variantes del borrador sólo si `draft.PaymentStatus == FullPaymentReceived`, dentro de la fábrica de `KeyedOrderCommit` (la rama `Existing` no busca nada).
  - `ExternalOrderService.SyncPaymentStatusAsync`: `order.UncostedVariantIds` sólo si `status == FullPaymentReceived`.
  - `OrderItemEdits.ApplyAsync` (lo usan `SaveOrderEdits` y `PreviewOrderEdits`) y `AddOrderItems`: las variantes **agregadas** sólo si el pedido está `FullPaymentReceived`; si no, `None`.
- Columnas: `quotations.order_items.unit_cost numeric(18,2) NULL`, `cost_source varchar(20) NULL`, `cost_captured_at timestamptz NULL`; `quotations.orders.paid_at timestamptz NULL` + `IX_orders_tenant_paid_at`.

- [ ] **Step 1: Write the failing unit tests**
  - `MarkOrderPaidHandlerTests.MarkingPaidLooksUpTheUncostedVariantsInTheOrderCurrency`: doble que devuelve `{ v1: 6000m }`; pedido USD con v1 y v2 → el doble recibió `"USD"` y `[v1, v2]`; v1 queda `6000`.
  - `OrderItemEditsTests.AddingToAPaidOrderFreezesTheNewLine` y `AddingToAnUnpaidOrderDoesNotLookUpCosts` (el doble no se llama).

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~MarkOrderPaidHandlerTests|FullyQualifiedName~OrderItemEditsTests"` → RED.

- [ ] **Step 2: Implementar los casos de uso y el adaptador** — inyecta `IOrderVariantCostLookup` en los handlers listados; `CatalogVariantCostLookup` implementa las dos interfaces (`services.AddScoped<IOrderVariantCostLookup, CatalogVariantCostLookup>()`). Run → GREEN.

- [ ] **Step 3: Mapear y migrar**

```powershell
dotnet ef migrations add AddLineCostAndPaidAt --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

Agrega a mano en `Up`, después de las columnas:

```sql
UPDATE quotations.orders
SET paid_at = COALESCE(paid_marked_at, converted_at)
WHERE payment_status = 'FullPaymentReceived';
```

`Down` sólo quita columnas e índice. Busca `FromSql` que lean `quotations.orders` u `order_items` y suma las columnas nuevas si listan columnas (gotcha de `QuotationSettingsStore`).

- [ ] **Step 4: Write the failing integration tests**
  - `PaidAtMigrationTests.PaidAtBackfillUsesPaidMarkedAtThenConvertedAt`: migrar a la anterior, sembrar tres pedidos por SQL (pagado con `paid_marked_at = 2026-11-01T15:00Z`; pagado sin `paid_marked_at` con `converted_at = 2026-10-01T15:00Z`; `PaymentPending`), migrar a la última (`MigrateAsync(null)`) → `paid_at` = `2026-11-01T15:00Z`, `2026-10-01T15:00Z`, `NULL`; en los tres, `order_items.cost_captured_at IS NULL`.
  - `OrderLineCostApiTests` (producto con `costs: { COP: 6000 }`):
    - `MarkPaidFreezesTheCatalogCost`: convertir sin pagar, `POST …/mark-paid` → `order_items.unit_cost = 6000.00`, `paid_at` = reloj de la factoría.
    - `UnmarkKeepsTheCostAndRemarkMovesPaidAt`: `unmark-paid` → `paid_at IS NULL`, costo intacto; cambiar el costo del catálogo a 9000; `mark-paid` → sigue `6000.00`.
    - `AnItemAddedAfterPaidFreezesOnArrival`: pagado; `POST …/items` con otra variante de costo 500 → esa línea `500.00`.
    - `TheOrderResponsesNeverCarryCosts`: el texto crudo del detalle y del `mark-paid` no contiene `unitCost`, `costSource` ni `costCapturedAt`.
  - `ExternalOrderServiceTests.AnExternalOrderBornPaidFreezesItsLines` (`unit_cost` y `paid_at` no nulos) y `ReingestingAPaidExternalOrderDoesNotLookUpCostsAgain` (segunda llamada con la misma clave: el doble de costo cuenta una sola llamada y las líneas no cambian).

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~PaidAtMigrationTests|FullyQualifiedName~OrderLineCostApiTests|FullyQualifiedName~ExternalOrderServiceTests" -- xUnit.MaxParallelThreads=4
```

RED antes del mapeo y cableado; GREEN después.

- [ ] **Step 5: Arquitectura**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests`
Expected: `Passed!` (`QuotationsLayerTests`, `PosLayerTests`: ninguno referencia Catalog).

- [ ] **Step 6: Commit** — `feat(orders): freeze line costs when an order becomes paid`.

---

### Task 7: Reporte de margen

**Files:**
- Create: `src/Modules/Reporting/Modules.Reporting.Application/ListMarginReport.cs`, `…/GetMarginReportSummary.cs`, `…/ReportMargin.cs`
- Modify: `…/ReportSources.cs` (puerto), `…/ReportingFilters.cs` (filtro, criterio, validador, `ToCriteria`), `…/ReportingDtos.cs` (DTOs)
- Modify: `src/Modules/Reporting/Modules.Reporting.Api/ReportingEndpoints.cs`
- Create: `src/Bootstrapper/MarginReportSource.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers junto a `:835`, fuente junto a `:1558`)
- Test: `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportMarginTests.cs`, `…/MarginReportHandlerTests.cs`; `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/MarginReportApiTests.cs` (nuevo), `…/ReportingApiHarness.cs`

**Interfaces:**
- Consumes: `paid_at` y las columnas de costo (Tareas 4 y 6), `ReportingPermissions.MarginRead` (Tarea 2).
- Produces:
  - `public sealed record MarginReportFilter(Guid TenantId, DateOnly? From, DateOnly? To, string? Source);` + `MarginReportFilterValidator` (las reglas de `source` y `to >= from` de `OrdersReportFilterValidator`).
  - `public sealed record MarginReportCriteria(Guid TenantId, ReportPeriod Period, OrderSourceFilter? Source);` + `ToCriteria(this MarginReportFilter, TenantCalendar)`.
  - `public interface IMarginReportSource { Task<(IReadOnlyList<MarginReportProductDto> Items, int Total)> ListAsync(MarginReportCriteria criteria, int page, int pageSize, CancellationToken ct); Task<IReadOnlyList<MarginReportCurrencyDto>> SummarizeAsync(MarginReportCriteria criteria, CancellationToken ct); }`
  - `public sealed record MarginReportCurrencyDto(string Currency, decimal NetSales, decimal Cost, decimal GrossProfit, decimal? MarginPercent, int UncostedLines, decimal UncostedNetSales);`
  - `public sealed record MarginReportSummaryDto(IReadOnlyList<MarginReportCurrencyDto> Currencies);`
  - `public sealed record MarginReportProductDto(Guid ProductId, string ProductName, string Currency, decimal Quantity, decimal NetSales, decimal Cost, decimal GrossProfit, decimal? MarginPercent, IReadOnlyList<MarginReportVariantDto> Variants);`
  - `public sealed record MarginReportVariantDto(Guid VariantId, string VariantName, decimal Quantity, decimal NetSales, decimal Cost, decimal GrossProfit, decimal? MarginPercent);`
  - `ListMarginReportQuery(MarginReportFilter Filter, int Page, int PageSize) : IQuery<ReportPage<MarginReportProductDto>>`; `GetMarginReportSummaryQuery(MarginReportFilter Filter) : IQuery<MarginReportSummaryDto>`. Autorizar (`MarginRead`) → validar → calendario → fuente. Sin alcance de asesor.
  - `public static class ReportMargin { public static decimal? Percent(decimal netSales, decimal grossProfit); }`
  - Rutas: `GET /api/v1/tenants/{tenantId:guid}/reports/margin` (query `from`, `to`, `source`, `page`, `pageSize`) y `/reports/margin/summary`, con `RequireAuthorization(ReportingPermissions.MarginRead)`, `ProducesProblem` 403 y 422.
- SQL (`MarginReportSource`, misma técnica que `OrdersReportSource`: fragmentos constantes, parámetros tipados, una rama omitida si el `source` no la trae, CTE `l` con una fila por línea):
  - Rama pedidos: `quotations.order_items i JOIN quotations.orders o ON o.id = i.order_id` con `o.tenant_id = @tenant_id AND o.payment_status = 'FullPaymentReceived' AND o.status <> 'Cancelled'`, período sobre `o.paid_at`, `source` sobre `o.source`. Fecha de la línea para «la más reciente»: `o.paid_at`.
  - Rama POS: `pos.sale_lines l JOIN pos.sales s ON s.id = l.sale_id` con `s.tenant_id = @tenant_id AND s.status = 'Completed'`, período sobre `s.created_at`; sólo si `source` es nulo o `Pos`.
  - Columnas comunes: `product_id, variant_id, currency, quantity, subtotal AS net_sales, round(unit_cost * quantity, 2) AS cost, unit_cost IS NOT NULL AS costed, product_name AS line_name, product_code AS line_code, <fecha> AS sold_at`.
  - Nombre del producto: `catalog.products.name` actual (`LEFT JOIN` por `id` y `tenant_id`); si no está, el `line_name` de la línea más reciente. Nombre de la variante: `line_name` de su línea más reciente; si es nulo, `catalog.product_variants.code` o el `line_code`; si nada, `"Producto sin nombre"`.
  - Resumen: `GROUP BY currency` con `sum(net_sales) FILTER (WHERE costed)`, `sum(cost) FILTER (WHERE costed)`, `count(*) FILTER (WHERE NOT costed)`, `sum(net_sales) FILTER (WHERE NOT costed)`.
  - Listado: `GROUP BY product_id, currency` sobre `WHERE costed` (C9), orden C11, `LIMIT/OFFSET`; total = `count(*)` de esos grupos; variantes de la página en **una** consulta más, agrupadas por `(product_id, currency, variant_id)`, ordenadas por utilidad descendente y `variant_id`.

- [ ] **Step 1: Write the failing unit tests**
  - `ReportMarginTests`: `Percent(200000m, 80000m) == 40.0m`; `Percent(30000m, 10000m) == 33.3m`; `Percent(3m, 2m) == 66.7m`; `Percent(0m, 0m) is null`; `Percent(0m, -500m) is null`; `Percent(1000m, -250m) == -25.0m`.
  - `MarginReportHandlerTests.WithoutTheMarginPermissionIs403BeforeValidating`: sin `MarginRead` y con `source = "Bogus"` → `RequestForbiddenException`, la fuente no se llamó.
  - `AnUnknownSourceIs422`: `ValidationException` con la propiedad `Source`.
  - `ThePeriodIsCutInTheTenantDay`: calendario Bogotá, `from = to = 2026-12-31` → la fuente recibe `Start = 2026-12-31T05:00Z`, `EndExclusive = 2027-01-01T05:00Z`.

Run: `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --filter "FullyQualifiedName~ReportMarginTests|FullyQualifiedName~MarginReportHandlerTests"` → RED por compilación.

- [ ] **Step 2: Implementar Application y endpoints** → mismo comando GREEN.

- [ ] **Step 3: Write the failing integration tests** (`MarginReportApiTests`; `ReportingApiHarness.CreateProductAsync` gana `IReadOnlyDictionary<string, decimal>? costs = null`, y un `MarginPermissions` = permisos de manager + `catalog.product_cost.manage` + `reporting.margin.read` + `quotations.order.approve`; productos sin tasa de impuesto, así `subtotal == precio`)

Escenario base: producto A precio 100000 COP costo 60000; cotización por 2, convertir y `mark-paid` → netSales 200000, cost 120000, grossProfit 80000, margin 40.0.

  - `TheSummaryAndTheListAgreeOnACostedOrder`: resumen COP `{ netSales: 200000, cost: 120000, grossProfit: 80000, marginPercent: 40.0, uncostedLines: 0, uncostedNetSales: 0 }`; listado una fila de A con `quantity 2` y una variante con las mismas cifras.
  - `CancelledIsOutAndArchivedIsIn`: un pedido pagado y anulado no suma; uno pagado, preparado y archivado sí.
  - `UnmarkedIsOutAndRemarkedComesBack`: tras `unmark-paid` el resumen queda vacío; tras `mark-paid` vuelve con las mismas cifras.
  - `AVoidedPosSaleIsOut`: venta POS de A por 100000 (costo 60000) suma 40000 de utilidad; anulada, no suma.
  - `AnUncostedLineIsCountedApart`: producto B sin costo, precio 50000, en otro pedido pagado → resumen COP `uncostedLines: 1`, `uncostedNetSales: 50000`, netSales/cost sin cambio; B no aparece en el listado (C9).
  - `AVariantWithoutCostInTheOrderCurrencyIsUncosted`: A con precio USD 30 y costo sólo en COP; pedido en USD pagado → objeto USD con `netSales 0`, `marginPercent null`, `uncostedLines 1`, `uncostedNetSales 30`; COP intacto.
  - `TwoCurrenciesNeverMix`: costo USD 10 en A; pedido USD pagado por 1 (30 USD) → dos objetos, `COP` primero, USD `{ 30, 10, 20, 66.7 }`; el listado trae dos filas de A, la de COP primero.
  - `ThePeriodFollowsTheTenantDay`: factoría con `NewYearsEveInBogota` como reloj → `from = to = 2026-12-31` lo incluye; `from = to = 2027-01-01` no.
  - `AnOrderPaidBeforeTheDeployIsCountedAsUncosted`: `UPDATE quotations.order_items SET unit_cost = NULL, cost_source = NULL, cost_captured_at = NULL` en un pedido pagado → cuenta en `uncostedLines`, no en el margen.
  - `ADeletedProductStillShowsWithItsFrozenName`: vender A, borrar el producto por SQL de catálogo (`DELETE FROM catalog.products`) → la fila sigue con el nombre congelado de su línea.
  - `TheListIsOrderedByProfitInTheDefaultCurrency`: A (utilidad 80000) y C (utilidad 10000) → A primero.
  - `WithoutTheMarginPermissionIs403`: cliente sin `reporting.margin.read` → 403 en las dos rutas.

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~MarginReportApiTests" -- xUnit.MaxParallelThreads=4
```

RED antes de `MarginReportSource`, GREEN después.

- [ ] **Step 4: Commit** — `feat(reporting): add the gross margin report`.

---

### Task 8: Corrida final y documentación

**Files:**
- Modify: `README.md` (tabla de rutas de reportes y de catálogo; § Pedidos: `paidAt` y costo congelado), `CLAUDE.md` (gotcha nuevo, abajo)
- No se toca: `docs/estado.md`.

- [ ] **Step 1: Gotcha en `CLAUDE.md`** (una entrada, junto a la de `OrdersReportSource`): «`MarginReportSource` lee `quotations.order_items`/`orders` (`unit_cost`, `paid_at`), `pos.sale_lines`/`sales` y `catalog.products`/`product_variants` por nombre. Renombrar una columna compila y deja `/reports/margin` en 500; lo amarra `MarginReportApiTests`. Una vía nueva a `FullPaymentReceived` pasa `OrderLineCosts` al dominio: si no, el pedido cuenta en el reporte con líneas «sin costo».»

- [ ] **Step 2: Suite de los proyectos tocados, uno a la vez**

```powershell
$env:Seed__Enabled = "false"
dotnet build Backend.slnx
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests -- xUnit.MaxParallelThreads=4
```

Expected: todos `Passed!`, 0 fallidas. Una falla en Shopify o en Buyers suele ser un llamador de `CreateExternal`/`SyncExternalPaymentStatus` que la Tarea 6 no vio: búscalo, no lo silencies. Si un `Modules.X.IntegrationTests.exe` queda vivo tras cortar una corrida, búscalo por la ruta del worktree.

- [ ] **Step 3: Commit** — `docs: document product cost and the margin report`.

---

## Self-review

- **Cobertura del spec:** §4.1 → T1, T3; §4.2–4.3 → T4 (POS), T5–T6 (pedido: pagado manual, Shopify al crear y al sincronizar, reingesta, línea agregada pagado, cantidad, borrado, desmarcar/remarcar, parcial); §4.4 → T5 (`PaidAt`), T6 (backfill); §4.5 → T1, T4, T6; §5 → T2, T3 (C2); §6.1 → T3; §6.2 → T4 Step 4, T6 Step 4; §6.3 → T7; §7 → T3; §8 → T1–T7; §9 → T0. Anulado/archivado/POS anulada → T7.
- **Consistencia de nombres:** `OrderLineCosts`, `IOrderVariantCostLookup`, `IPosVariantCostLookup`, `CatalogVariantCostLookup`, `LineCostSource`, `PaidAt`, `UncostedVariantIds`, `MarginReport*` se usan igual en todas las tareas.
- **Línea borrada:** sale del reporte sola (la fila ya no existe); no necesita código.
