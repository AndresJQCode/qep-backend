# Impuestos por tipo y país (IVA, Retefuente, ReteICA) — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que la retención deje de ser la constante `DocumentTotals.RetentionRate = 0.025m` y pase a ser tasas de impuesto con tipo y país (Colombia: `IVA`, `Retefuente`, `ReteICA`), base mínima en UVT, asignadas al cliente o elegidas en el documento, congeladas al crear la cotización o el pedido y mostradas una por línea.

**Architecture:** Una tabla en código (`BuildingBlocks.Domain/Taxes/TaxCountries.cs`) define tipos, comportamiento y unidad tributaria por país; la leen Catalog (validar), Customers (validar) y Quotations (calcular) sin referenciarse. Catalog gana `TaxRate.Type/Country/MinimumBase`, el porcentaje decimal y la entidad de plataforma `TaxUnitValue` con su consola de operador. Customers guarda las tasas asignadas en `customers.customer_tax_rates`. Quotations congela una foto (`DocumentWithholding`, tablas `quotation_withholdings` / `order_withholdings`) que `DocumentTotals.Compute` usa en cada recálculo; las lecturas cruzan por puertos con adaptador en `Bootstrapper`. Las columnas booleanas viejas quedan como propiedades sombra hasta la migración de contrato (N+1).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, ClosedXML, Typst, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-10-impuestos-por-tipo-design.md`](../specs/2026-11-10-impuestos-por-tipo-design.md) · spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-10-impuestos-por-tipo-design.md`. Este backend sale **primero** y tiene que convivir con el frontend viejo (spec §12).

---

## Antes de empezar

- El spec está sin commitear en `develop`. Desde el checkout principal, en PowerShell: `git branch --show-current` (si no
  dice `develop`, frena y pregunta), después
  `git add docs/superpowers/specs/2026-11-10-impuestos-por-tipo-design.md docs/superpowers/plans/2026-11-10-impuestos-por-tipo.md`
  y `git commit -m "docs(taxes): approve tax types design and add implementation plan" -- docs/superpowers/specs/2026-11-10-impuestos-por-tipo-design.md docs/superpowers/plans/2026-11-10-impuestos-por-tipo.md`.
- Worktree: `git worktree add ..\qep-backend-worktrees\impuestos-por-tipo -b feature/impuestos-por-tipo develop`. Todo lo que
  sigue corre **dentro** del worktree, que necesita su propio índice de CodeGraph. Antes de **cada** commit:
  `git branch --show-current` tiene que decir `feature/impuestos-por-tipo`; si dice otra cosa, frena y pregunta.
- Todo comando es PowerShell, una línea por comando, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas:** es
  del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y `$env:Seed__Enabled = "false"` en una línea aparte. Nunca la solución entera: un
  proyecto a la vez, con `--filter` a la clase tocada y `-- xUnit.MaxParallelThreads=4`. Las corridas completas de cada proyecto van
  una sola vez, en la Tarea 18. Las unitarias corren al cerrar cada tarea.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
  Después de cada una: `dotnet ef migrations has-pending-model-changes --project … --context …` → `No changes have been made to the model since the last migration.`

## Global Constraints

- Tabla de países, exacta (spec §7.1): `TaxCountries.Colombia = ("CO", TaxUnitName "UVT", TaxCurrency "COP", [IVA Adds sin fracción sin base, Retefuente Withholds con fracción con base, ReteICA Withholds con fracción con base])`. Los valores de `Type` viajan como texto: `"IVA"`, `"Retefuente"`, `"ReteICA"`; `Behavior` como `"Adds"` / `"Withholds"`.
- Porcentaje: `decimal`, 0–100; entero para tipos sin fracción (IVA); hasta **3 decimales** para los que retienen (D15). `catalog.tax_rates.percentage numeric(6,3)`; `type varchar(32) not null`, `country char(2) not null`, `minimum_base numeric(14,2) null`.
- `catalog.tax_unit_values (country char(2), year integer, value numeric(14,2), updated_at timestamptz, updated_by uuid, PK (country, year))`, sin `tenant_id`. Año entre 2000 y año actual + 1; valor `> 0`.
- UVT aplicada (D6/D20): la del año de la fecha del documento (`CreatedAt` en la zona del tenant); si falta, la del mayor año cargado **anterior**; si no hay ninguno anterior, `taxUnitValue = null` y la tasa con base mínima **no aplica**.
- Cálculo (D11, spec §7.5): consumidor final → ninguna; sin empresa facturadora → ninguna; moneda del documento ≠ `TaxCurrency` del país de la empresa → ninguna; si no, cada tasa de la fuente vigente con `Country == país de la empresa`: base = `Subtotal` de productos (sin IVA, sin envío); umbral = `MinimumBase × taxUnitValue` (con unidad) o `MinimumBase` (sin unidad); aplica si `MinimumBase == null` o `base >= umbral`; monto = `Round(base × percentage / 100, 2, AwayFromZero)`. `RetentionAmount` = suma; `NetTotal = Total − RetentionAmount`.
- Códigos NUEVOS, exactos (spec §10): `catalog.tax_rate.type_invalid`, `catalog.tax_rate.country_not_available`, `catalog.tax_rate.percentage_not_integer`, `catalog.tax_rate.percentage_too_precise`, `catalog.tax_rate.minimum_base_not_allowed`, `catalog.tax_rate.minimum_base_invalid`, `catalog.tax_rate.tax_unit_value_missing`, `catalog.product.tax_rate_not_vat`, `quotations.freight_tax_rate.not_vat`, `customers.customer.withholding_tax_rate_invalid`, `customers.customer.withholding_type_repeated`, `quotation.billing.withholding_tax_rate_invalid`, `order.billing.withholding_tax_rate_invalid`, `quotation.billing.withholding_type_repeated`, `order.billing.withholding_type_repeated`, `catalog.tax_unit.country_without_unit`, `catalog.tax_unit.year_out_of_range`, `catalog.tax_unit.value_invalid`. Más uno de este plan, aprobado por el owner el 2026-11-10: `order.billing.currency_change_not_allowed` (P10). Reusados: `catalog.tax_rate.in_use` (también por clientes), `quotation.billing.account_required` (también al enviar), `quotation.billing.tax_profile_required` y `order.billing.tax_profile_required` (sólo excedente de IVA).
- Permisos NUEVOS: `operator.tax_units.read` (medium) y `operator.tax_units.manage` (high), categoría `Operator`, `RequiredModules: []`, en el rol `admin`, con su política en `AddAuthorization`. Rutas `/api/v1/tenants/{tenantId:guid}/operator/tax-units` (`GET /`, `PUT /{country}/{year}`).
- Compatibilidad con el frontend viejo (spec §9, §12): todos los campos nuevos son aditivos; `withRetention` (cliente), `billingWithRetention` (cotización y pedido) y `client.withRetention` **siguen en las respuestas, derivados**; en los cuerpos se **ignoran**. `billingWithRetention` con parte propia es `billingWithholdingTaxRates.length > 0` y **nunca `null`**; sin parte propia, `null`. `POST /tax-rates` sin `type` → `IVA`, sin `country` → `CO`.
- Congelamiento (D17, D24): se borran `Quotation.RefreshCustomerTaxProfile` y `Order.RefreshCustomerTaxProfile` y todos sus llamadores; `GET` de una cotización no escribe. Sólo recopian del cliente: `Quotation.ChangeClient` y la edición de retenciones de un pedido `Pending` (D21), que nunca escribe en el cliente.
- Las fábricas `Order.CreateManual`, `CreateExternal` y `CreateFromStore` siguen exigiendo cuenta de cobro (spec §7.7): no se relajan. El webhook de Shopify no puede empezar a fallar.
- Capas: Customers y Quotations **nunca** referencian Catalog ni Companies; todo cruza por puertos con adaptador en `src/Bootstrapper/`. `ArchitectureTests` sin cambios. Application no referencia EF Core ni Npgsql.
- Ningún documento existente cambia de totales con la migración (D10). Ninguna retención sobre el envío. Sin retenciones en POS.
- TDD: RED antes que GREEN, con la salida literal de ambas corridas en el reporte de cada tarea.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**, siempre `git commit -m "…" -- <rutas>` (archivos nuevos con `git add <ruta>` antes).
- Comentarios de código en español, tuteando; el voseo se corrige sólo en las líneas que tocas. Mensajes de excepción en inglés, como sus vecinos.
- `docs/estado.md` **no** se toca en la rama. Nunca se mergea a `main`.

## Decisiones de este plan (lo que el spec deja al constructor)

Cada una es reversible por el owner; la tarea dueña la repite en un comentario de código.

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | El país de la empresa se **congela con la cuenta**: `QuotationBillingAccount.Country` (`billing_company_country char(2) null` en `quotations` y `orders`), llenado por `QuotationBillingAccountVerifier` desde `QuotationCompanyRef.Country`. La migración lo rellena desde `companies.companies.country` y, si no encuentra la empresa, `'CO'`. | Los montos se recalculan en cada edición (spec §7.5) y el agregado no puede ir a Companies; congelarlo con la cuenta sigue el criterio de copia de `QuotationBillingAccount.cs:3-14`. |
| P2 | No hay tablas `*_billing_withholding_rates` / `order_withholding_rates`: lo elegido en un selector es la fila de la foto con `source = 'Billing'` (cotización) o `'Document'` (pedido). | El spec §8 lo permite («puede resolverse con la columna `source` de la foto si basta»); basta, porque hay una fila por tipo. |
| P3 | La foto gana la columna `applied boolean not null`. `withholdings` de la respuesta = filas con `applied`. | Sin ella, «sólo las aplicadas» de una fila con `amount = 0` (tasa al 0 %) no se distingue de «no aplica». |
| P4 | `WithholdingInput` del spec §7.5 se llama `WithholdingRate` (la tasa congelada); `AppliedWithholding` es el resultado por tasa. | Es el mismo dato que se guarda y que se calcula; un segundo tipo sería una copia. |
| P5 | Las columnas `customers.customers.with_retention`, `quotations.{quotations,orders}.customer_with_retention` y `.party_with_retention` quedan como **propiedades sombra** en el `DbContext` (`with_retention`/`customer_with_retention` con `HasDefaultValue(false)`, `party_with_retention` nullable), sin propiedad de dominio. | El modelo las conserva, así EF no genera un `DropColumn` ahora, los `INSERT` no fallan por `NOT NULL`, `ExportLoadSeeder` sigue funcionando y la migración de contrato (§11.5) las quita con un cambio de modelo normal. |
| P6 | La migración de datos (§11.2 y §11.3) es **una migración de Quotations** (`MigrateRetentionToWithholdings`, SQL), no un inicializador: Catalog, Customers y Companies migran antes (`Program.cs:349`, `:356`, `:358`, `:366`). Cada sentencia que toca otro esquema va dentro de `DO $$ … IF to_regclass('…') IS NOT NULL …`. | Una migración corre exactamente una vez; un inicializador necesitaría una marca propia, y «cliente con `with_retention` y sin tasa» no sirve de marca porque la columna ya no se escribe. |
| P7 | Sin `country` en el `POST` de una tasa: `CO` y **sin** comprobar que sea país de una empresa del tenant. Con `country` explícito, se comprueba (`country_not_available`). | El frontend viejo crea IVA sin país, y un tenant sin empresas lo podía hacer hasta hoy. |
| P8 | `minimumBaseUnit` = `TaxUnitName` del país (o su `TaxCurrency` si no tiene unidad) cuando `minimumBase` no es `null`; `null` si no. | El spec da los tres valores posibles pero no cuándo va cada uno. |
| P9 | Los campos nuevos del cuerpo de edición del pedido (`withholdingTaxRateIds`, `billingAccount`) van en la **raíz** de `SaveOrderEditsRequest` (que también usa la vista previa), no dentro de `billing`. Ausentes o `null` = conservar. | `billing` ausente ya significa «no lo toques» (Desvío 6 de 2026-10-26); anidarlos obligaría a reenviar la parte de facturación para cambiar una tasa. Avisar al frontend. |
| P10 | `Order.SetBillingAccount` rechaza una cuenta con otra moneda que la del pedido con 422 NUEVO `order.billing.currency_change_not_allowed`. | El spec dice «cambiar la moneda con la cuenta no reprecia»; cambiar la moneda sin repreciar dejaría pesos rotulados como dólares, y repreciar un pedido de Shopify cambiaría lo que la plataforma cobró. **Aprobado por el owner (2026-11-10).** |
| P11 | La validación de un selector (existe, activa, retiene, tipo, país, uno por tipo) vive en `WithholdingSelection` (Application) con el prefijo del documento; el agregado vuelve a exigir país y unicidad por tipo como invariante. | Igual que `QuotationBillingAccountResolver` + `Normalized()`: Application tiene los datos de Catalog, el dominio protege lo que guarda. |
| P12 | Columnas nuevas del Excel de pedidos: `withholding_retefuente` «Retefuente» y `withholding_reteica` «ReteICA», al final, `DefaultVisible: false`. | Mismo criterio que `variant` y los grupos de 2026-10-28: ningún Excel que ya existe gana columnas. |

## Review Focus

1. **Cotización abierta sin empresa que hoy retiene por el cliente**: tras el despliegue la foto existe pero, al próximo recálculo, sin empresa no aplica y `retentionAmount` baja a 0 (spec §7.5 paso 3); la lectura (`GET`) **no** la recalcula. Lo amarran la Tarea 11 (`ADraftWithoutBillingAccountAppliesNoWithholdingAndAppliesItOnceChosen`) y la Tarea 17 (`MigratedDraftKeepsItsStoredTotalsOnRead`).
2. **Frontend viejo guardando una cotización con parte propia**: manda `billingWithRetention: true` y no manda `billingWithholdingTaxRateIds`: se conservan los selectores, la respuesta trae `billingWithRetention` no nulo y la conversión no queda bloqueada. Lo amarra la Tarea 13 (`OldFrontendBodyKeepsSelectorsAndNeverNullsBillingWithRetention`).
3. **Base exactamente igual al umbral y porcentaje con 3 decimales** (`0.966` sobre `1.000.001`): aplica con `>=` y redondea `AwayFromZero` a 2 decimales. Lo amarra la Tarea 9 (`ThresholdIsInclusiveAndFractionRoundsAwayFromZero`).
4. **Webhook de Shopify con un cliente con tasas de dos países**: el pedido se crea igual, con sólo las del país de la empresa, y nunca lanza por retenciones. Lo amarra la Tarea 14 (`ExternalOrderCopiesOnlyCustomerRatesOfTheCompanyCountry` y la regresión `ShopifyWebhookStillCreatesOrders`).
5. **Cambiar la empresa de un pedido `Pending` a otro país con una tasa ya guardada**: la tasa deja de aplicar sin error; el siguiente guardado que la reenvíe da `order.billing.withholding_tax_rate_invalid`. Lo amarran la Tarea 12 (`ChangingCompanyCountryStopsApplyingTheOldRate`) y la Tarea 14 (`ResendingAForeignRateAfterCountryChangeIs422`).

---

## Mapa de archivos

| Archivo | Responsabilidad | Tarea |
| --- | --- | --- |
| `src/BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs` (NUEVO) | Tabla de países, tipos y unidad | 1 |
| `src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs`, `Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs`, `Seed/CatalogSeeder.cs`, `src/Bootstrapper/{CatalogVariantReads,QuotationTaxRateLookup,ShopifyProductLookup,ShopifyShippingLookup}.cs` | Tasa con tipo, país, base; porcentaje decimal | 2 |
| `src/Modules/Catalog/Modules.Catalog.Domain/TaxUnitValue.cs` (NUEVO), `ITaxUnitValueRepository.cs` (NUEVO), `TaxUnitValueRepository.cs` (NUEVO) | Valor de la unidad tributaria y su resolución | 3 |
| `src/Modules/Catalog/Modules.Catalog.Application/{CreateTaxRate,UpdateTaxRate,ListTaxRates,TaxRateMapping,CatalogDtos}.cs`, `GetTaxRateOptions.cs` (NUEVO), `ITaxRateCountrySource.cs` (NUEVO), `Modules.Catalog.Api/TaxRateEndpoints.cs`, `src/Bootstrapper/TaxRateCountrySource.cs` (NUEVO) | Contrato de tasas | 4 |
| `src/Modules/Catalog/Modules.Catalog.Application/ProductTaxRateResolver.cs`, `Modules.Quotations.Application/{SetFreightTaxRate,IQuotationTaxRateLookup}.cs` | Producto y envío sólo con IVA | 5 |
| `src/Modules/Catalog/Modules.Catalog.Application/{CatalogOperatorPermissions,TaxUnitOperator}.cs` (NUEVOS), `Modules.Catalog.Api/TaxUnitOperatorEndpoints.cs` (NUEVO), `QepServiceCollectionExtensions.cs`, `src/Api/Program.cs` | Consola «Unidad tributaria» | 6 |
| `src/Modules/Customers/Modules.Customers.Domain/{Customer,CustomerCommercialInfo}.cs`, `CustomersDbContext.cs` | Tasas del cliente en el agregado | 7 |
| `src/Modules/Customers/Modules.Customers.Application/{CreateCustomer,UpdateCustomer,CustomerMapping,CustomersDtos,CustomerWriteRules,CustomerProvisioning}.cs`, `ICustomerTaxRateLookup.cs` (NUEVO), `Modules.Customers.Api/CustomerEndpoints.cs`, `src/Bootstrapper/{CustomerTaxRateLookup,TaxRateCustomerUsage}.cs` (NUEVOS), `Catalog.Application/{DeactivateTaxRate,DeleteTaxRate}.cs`, `ITaxRateCustomerUsage.cs` (NUEVO) | Contrato del cliente; «en uso» por clientes | 8 |
| `src/Modules/Customers/Modules.Customers.Application/{IExcelCustomerImporter,ImportCustomers,ExportCustomers}.cs`, `Infrastructure/Excel/ClosedXml*.cs` | Excel de clientes | 9 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{DocumentTotals,WithholdingRate,DocumentWithholding}.cs` | Cálculo | 10 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{Quotation,QuotationBillingAccount,QuotationPartyDetails}.cs`, `QuotationsDbContext.cs`, `Application/{IQuotationCompanyLookup,QuotationBillingAccountVerifier,QuotationBillingAccountResolver}.cs`, `src/Bootstrapper/QuotationCompanyLookup.cs` | Foto en la cotización; país de la empresa; envío exige empresa | 11 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{Order,OrderInputs}.cs`, `QuotationsDbContext.cs` | Foto en el pedido | 12 |
| `src/Modules/Quotations/Modules.Quotations.Application/{IQuotationWithholdingLookup,WithholdingSelection}.cs` (NUEVOS), `{QuotationDraftBuilder,SaveQuotation,PreviewQuotation,PreviewNewQuotation,ChangeQuotationClient,GetQuotation,QuotationEditsApplication,AddQuotationItem,UpdateQuotationItem,RemoveQuotationItem,BatchUpdateQuotationItems,QuotationMapping,QuotationResponseComposer,QuotationsDtos}.cs`, `src/Bootstrapper/{QuotationWithholdingLookup,QuotationCustomerLookup}.cs` | Cotización: fuentes y contrato | 13 |
| `src/Modules/Quotations/Modules.Quotations.Application/{OrderDraftBuilder,IExternalOrderWriter,IStoreOrderGateway,OrderEdits,SaveOrderEdits,PreviewOrderEdits,OrderMapping,OrderResponseComposer,OrdersDtos}.cs`, `Modules.Store.Application/{IStoreCustomerDirectory,IStoreOrders,SubmitOrderRequest}.cs`, `src/Bootstrapper/{StoreOrdersAdapter,StoreCustomerDirectory}.cs` | Pedido: fuentes, edición D21/D22 y contrato | 14 |
| `src/Modules/Quotations/Modules.Quotations.Application/{QuotationPdfDocument,QuotationChangeSummary,OrdersExportProcessor}.cs`, `Infrastructure/Pdf/quotation.typ`, `Domain/OrdersExportColumnCatalog.cs` | PDF, historial y Excel | 15–16 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_MigrateRetentionToWithholdings.cs` (NUEVO) | Migración de datos | 17 |

---

### Task 1: `TaxCountries` — la tabla en código

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxCountriesTests.cs` (no hay proyecto de pruebas de BuildingBlocks; `VatIncludedLineTests` vive en `Modules.Pos.UnitTests` por la misma razón)

**Interfaces:**
- Produces (namespace `BuildingBlocks.Domain.Taxes`): `enum TaxBehavior { Adds, Withholds }`; `sealed record TaxTypeDefinition(string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase)`; `sealed record TaxCountryDefinition(string Country, string? TaxUnitName, string TaxCurrency, IReadOnlyList<TaxTypeDefinition> Types)`; `static class TaxCountries` con `Colombia`, `IReadOnlyList<TaxCountryDefinition> All`, `TaxCountryDefinition? Find(string? country)` (recorta y pasa a mayúsculas), `TaxTypeDefinition? FindType(string? country, string? type)` (tipo exacto, `Ordinal`), `const int MaxFractionDigits = 3`, `const string IvaType = "IVA"`, `const string RetefuenteType = "Retefuente"`.

- [ ] **Step 1: Write the failing tests** — `ColombiaHasItsThreeTypes` (tipos `["IVA","Retefuente","ReteICA"]` en ese orden; IVA `Adds`, sin fracción, sin base; los otros dos `Withholds`, con fracción, con base; `TaxUnitName == "UVT"`, `TaxCurrency == "COP"`); `FindIsCaseInsensitiveOnCountry` (`Find(" co ")` es `Colombia`); `UnknownCountryIsNull` (`Find("ES")` y `Find(null)` son `null`); `FindTypeIsExact` (`FindType("CO","reteica")` es `null`, `FindType("CO","ReteICA")` no); `TypeOfAnotherCountryIsNull` (`FindType("ES","IVA")` es `null`).
- [ ] **Step 2: Run, expect FAIL** (compilación: `TaxCountries` no existe).
  `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxCountriesTests"`
- [ ] **Step 3: Implement** el archivo del spec §7.1. XML doc: es la única definición de tipos y unidades; un país nuevo es una entrada más de `All`; `TaxCurrency` generaliza `QuotationItem.ItemizesTaxIn` (`QuotationItem.cs:305-307`).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(taxes): add the per-country tax type table`.

### Task 2: `TaxRate` con tipo, país y base mínima

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs` (constantes `:15-18`, ctor `:23-39`, `Percentage` `:48-49`, `Create`/`Update` `:67-81`, `EnsurePercentageInRange` `:133-138`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs:355-383` (`ConfigureTaxRate`)
- Create (generados): `…/Persistence/Migrations/<ts>_AddTaxRateTypeAndCountry.cs` + `.Designer.cs`; Modify: `CatalogDbContextModelSnapshot.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/CatalogSeeder.cs:45-46`
- Modify (sólo compilación, conversión a entero de una tasa `Adds`): `src/Bootstrapper/CatalogVariantReads.cs:153`, `src/Bootstrapper/QuotationTaxRateLookup.cs:21`, `src/Bootstrapper/ShopifyProductLookup.cs:35`, `src/Bootstrapper/ShopifyShippingLookup.cs:41`; `src/Modules/Catalog/Modules.Catalog.Application/{CreateTaxRate,UpdateTaxRate}.cs` (interino: `Create(..., TaxCountries.IvaType, "CO", null, now)` y `Update(name, percentage, taxRate.MinimumBase, now)`)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxRateTests.cs`, `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxRateTypeMigrationTests.cs` (NUEVO)

**Interfaces:**
- Consumes: `TaxCountries` (Tarea 1).
- Produces: `TaxRate.Create(TaxRateId id, Guid tenantId, string name, decimal percentage, string type, string country, decimal? minimumBase, DateTimeOffset occurredAt)`; `Update(string name, decimal percentage, decimal? minimumBase, DateTimeOffset occurredAt)`; propiedades `decimal Percentage`, `string Type`, `string Country`, `decimal? MinimumBase`; calculadas (ignoradas por EF) `TaxBehavior Behavior`, `bool Withholds`, `int VatPercentage` (lanza `InvalidOperationException` si la tasa no es `Adds` o tiene fracción: sólo la leen los adaptadores de líneas, que nunca reciben otra cosa — Tarea 5). Constantes `TypeMaxLength = 32`, `CountryLength = 2`.

- [ ] **Step 1: Write the failing unit tests** (actualiza los helpers existentes a la firma nueva con `"IVA"`, `"CO"`, `null`):
  - `IvaNineteenIsAccepted`; `IvaWithFractionIsRejected` (`2.5m` → `catalog.tax_rate.percentage_not_integer`).
  - `[Theory] RetentionsAcceptThreeDecimals(2.5, 0.966)` con `"Retefuente"` y `"ReteICA"`; `FourDecimalsAreRejected` (`0.9665m` → `percentage_too_precise`).
  - `TypeOfAnotherCountryIsRejected` (`"IVA"`, `"ES"` → `type_invalid`; `"Retencion"`, `"CO"` → `type_invalid`).
  - `IvaWithMinimumBaseIsRejected` (`minimumBase: 4m` → `minimum_base_not_allowed`); `NegativeMinimumBaseIsRejected` (`-1m` → `minimum_base_invalid`); `ZeroMinimumBaseIsAccepted`.
  - `PercentageOutOfRangeKeepsItsCode` (`100.001m` y `-0.5m` → `catalog.tax_rate.percentage_out_of_range`).
  - `UpdateNeverChangesTypeOrCountry` (crea `ReteICA`/`CO`, `Update("x", 1.104m, 2m, now)` → `Type == "ReteICA"`, `Country == "CO"`, `MinimumBase == 2m`).
  - `CountryIsNormalized` (`" co "` → `"CO"`); `VatPercentageOfAWithholdingThrows`.
- [ ] **Step 2: Run, expect FAIL** (compilación).
  `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxRateTests"`
- [ ] **Step 3: Implement the domain.** Orden de validación: país → tipo (`TaxCountries.FindType`) → rango 0–100 → entero/3 decimales (cuenta los decimales sin ceros finales: `0.9660m` tiene 3) → base mínima. Reescribe el comentario de P-008 (`:15-16`) y el de `Percentage`: «Reabierto por el owner el 2026-11-10 (D15)».
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Write the failing migration test** `TaxRateTypeMigrationTests.ExistingRatesBecomeIvaColombia` — patrón de `QuotationSettingsMigrationTests`: `IMigrator` sobre `CatalogDbContext`, migra a la última migración anterior, inserta una tasa por SQL con `percentage = 19`, `MigrateAsync(null)`, y `SELECT type || '|' || country || '|' || percentage::text` → `"IVA|CO|19.000"`. Más `TheTypeAndCountryColumnsHaveNoDefault` (`information_schema.columns.column_default` de `type` y `country` es `NULL`).
  `$env:Seed__Enabled = "false"`
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~TaxRateTypeMigrationTests" -- xUnit.MaxParallelThreads=4` → FAIL.
- [ ] **Step 6: Map and migrate.** `Percentage` `.HasPrecision(6, 3)`; `Type` `type` `HasMaxLength(32)`; `Country` `country` `HasMaxLength(2).IsFixedLength()`; `MinimumBase` `minimum_base` `.HasPrecision(14, 2)`; `Ignore` de `Behavior`, `Withholds`, `VatPercentage`. Genera `AddTaxRateTypeAndCountry` y edita el `Up` (spec §11.1): `AlterColumn` de `percentage` a `numeric(6,3)`; `AddColumn` de `type` y `country` con `defaultValue: "IVA"` / `"CO"`, y después `AlterColumn` de las dos sin default; `AddColumn` de `minimum_base` nullable.
- [ ] **Step 7: Fix compilation.** Seeder: `TaxRate.Create(…, seed.TaxRate.Percentage, TaxCountries.IvaType, "CO", null, now)`. Los cuatro lectores de `Bootstrapper` leen `taxRate.VatPercentage`.
- [ ] **Step 8: Run** las unitarias de Catalog, Quotations y `Bootstrapper.UnitTests`, y la de migración → PASS. `has-pending-model-changes` limpio.
- [ ] **Step 9: Commit** — `feat(catalog): give tax rates a type, a country and a minimum base`.

### Task 3: `TaxUnitValue` — la UVT por país y año

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Domain/TaxUnitValue.cs`, `src/Modules/Catalog/Modules.Catalog.Domain/TaxUnitValues.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Application/ITaxUnitValueRepository.cs`, `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/TaxUnitValueRepository.cs`
- Modify: `CatalogDbContext.cs` (NUEVO `DbSet<TaxUnitValue> TaxUnitValues`, `ConfigureTaxUnitValue`), registro del repositorio donde se registra `TaxRateRepository`
- Create (generados): migración `AddTaxUnitValues`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxUnitValueTests.cs`, `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxUnitValuePersistenceTests.cs` (NUEVO)

**Interfaces:**
- Produces: `TaxUnitValue.Create(string country, int year, decimal value, Guid updatedBy, DateTimeOffset occurredAt, int currentYear)`; `void Replace(decimal value, Guid updatedBy, DateTimeOffset occurredAt)`; propiedades `Country`, `Year`, `Value`, `UpdatedAt`, `UpdatedBy`. `static class TaxUnitValues` con `decimal? ValueFor(IEnumerable<TaxUnitValue> values, string country, int documentYear)` y `bool NextYearMissing(IEnumerable<TaxUnitValue> values, string country, DateOnly today)`. `ITaxUnitValueRepository`: `Task<IReadOnlyList<TaxUnitValue>> ListAsync(string? country, CancellationToken)`, `Task<TaxUnitValue?> FindAsync(string country, int year, CancellationToken)`, `Task<bool> AnyAsync(string country, CancellationToken)`, `void Add(TaxUnitValue value)`.

- [ ] **Step 1: Write the failing unit tests:**
  - `CountryWithoutUnitIsRejected` (`"ES"` → `catalog.tax_unit.country_without_unit`; país desconocido, mismo código).
  - `[Theory] YearOutOfRange(1999)`, `(currentYear + 2)` → `catalog.tax_unit.year_out_of_range`; `2000` y `currentYear + 1` pasan.
  - `[Theory] ValueMustBePositive(0, -1)` → `catalog.tax_unit.value_invalid` (también en `Replace`).
  - `ValueForUsesTheDocumentYear` (2025 y 2026 cargados, `ValueFor(…, "CO", 2026)` → el de 2026).
  - `ValueForFallsBackToTheLatestEarlierYear` (2024 y 2026; año 2025 → el de 2024; año 2028 → el de 2026).
  - `ValueForWithOnlyLaterYearsIsNull` (sólo 2027; año 2026 → `null`).
  - `NextYearMissing`: `DecemberTwentyThirdIsFalse` (`2026-12-23`, sin 2027 → `false`); `DecemberTwentyFourthWithoutNextYearIsTrue`; `WithNextYearLoadedIsFalse`; `CountryWithoutUnitIsNeverMissing` (`"ES"` → `false`).
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxUnitValueTests"`
- [ ] **Step 3: Implement.** Sin `tenant_id` a propósito (dato de plataforma, como las ciudades de Geography): dilo en el XML doc.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Persistence.** Tabla `catalog.tax_unit_values` del spec §7.3, `PK (country, year)`, `value numeric(14,2)`. Migración `AddTaxUnitValues`. Prueba `TaxUnitValuePersistenceTests.ValuesRoundTripAndReplaceKeepsOneRowPerYear` (agrega 2026, lo reemplaza, `ListAsync("CO")` tiene una fila con el valor nuevo). RED antes del mapeo, GREEN después.
- [ ] **Step 6: Commit** — `feat(catalog): store the tax unit value per country and year`.

### Task 4: Contrato de tasas — filtros, alta con tipo y opciones

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs:359-376` (`TaxRateDto`, `TaxRateResponse`, `CreateTaxRateRequest`, `UpdateTaxRateRequest`), `TaxRateMapping.cs`, `CreateTaxRate.cs`, `UpdateTaxRate.cs`, `ListTaxRates.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Application/ITaxRateCountrySource.cs`, `GetTaxRateOptions.cs`; `src/Bootstrapper/TaxRateCountrySource.cs`
- Modify: `src/Modules/Companies/Modules.Companies.Application/ICompanyRepository.cs` y su implementación (NUEVO `ListCountriesAsync`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Api/TaxRateEndpoints.cs` (rutas `:20-71`, mapeos `:108`, `:124`, `:173`), registro del adaptador en `QepServiceCollectionExtensions.cs` (junto a `:1522`)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxRateApiTests.cs`, `tests/Modules/Catalog/Modules.Catalog.UnitTests/CreateTaxRateValidatorTests.cs` (NUEVO)

**Interfaces:**
- Consumes: Tareas 1–3.
- Produces: `TaxRateDto(Guid Id, string Name, decimal Percentage, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Type, string Country, string Behavior, decimal? MinimumBase, string? MinimumBaseUnit)` (y `TaxRateResponse` igual); `CreateTaxRateRequest(string Name, decimal Percentage, string? Type = null, string? Country = null, decimal? MinimumBase = null)`; `UpdateTaxRateRequest(string Name, decimal Percentage, JsonElement MinimumBase = default)` (ausente → `ValueKind.Undefined` = conservar; `null` = quitar la base; número = valor; otra cosa → `validation.failed` con `errors.MinimumBase`: un `decimal?` no distingue ausente de `null`, y `null` significa «siempre aplica»); `UpdateTaxRateCommand(Guid TenantId, Guid TaxRateId, string Name, decimal Percentage, bool KeepMinimumBase, decimal? MinimumBase)`; `CreateTaxRateCommand(Guid TenantId, string Name, decimal Percentage, string Type, string Country, bool CountryExplicit, decimal? MinimumBase)`; `ListTaxRatesQuery(Guid TenantId, IReadOnlyCollection<string>? Types = null)`; `ITaxRateCountrySource.ListCompanyCountriesAsync(Guid tenantId, CancellationToken) : Task<IReadOnlySet<string>>`; `GetTaxRateOptionsQuery(Guid TenantId) : IQuery<TaxRateOptionsDto>` con `TaxRateOptionsDto(IReadOnlyList<TaxRateCountryOptionDto> Countries)`, `TaxRateCountryOptionDto(string Country, string? TaxUnit, string Currency, IReadOnlyList<TaxRateTypeOptionDto> Types)`, `TaxRateTypeOptionDto(string Type, string Behavior, bool AllowsFraction, bool AllowsMinimumBase)`; `ICompanyRepository.ListCountriesAsync(Guid tenantId, CancellationToken) : Task<IReadOnlySet<string>>` (activas e inactivas).

- [ ] **Step 1: Write the failing integration tests** en `TaxRateApiTests` (la factoría crea una empresa `CO` por el harness de Companies o por SQL, como hagan las pruebas vecinas):
  - `LegacyCreateWithoutTypeOrCountryIsIvaColombia` (`{ name, percentage: 19 }` → 201 con `type: "IVA"`, `country: "CO"`, `behavior: "Adds"`, `minimumBase: null`, `minimumBaseUnit: null`) — vale **sin** empresas (P7).
  - `CreateReteIcaWithMinimumBaseReturnsUnit` (UVT 2026 sembrada; `{ "ReteICA Bogotá", 0.966, "ReteICA", "CO", 4 }` → `percentage: 0.966`, `minimumBaseUnit: "UVT"`).
  - `MinimumBaseWithoutAnyTaxUnitValueIs422` → `catalog.tax_rate.tax_unit_value_missing`.
  - `ExplicitCountryWithoutCompanyIs422` (`country: "ES"`, sólo empresa `CO` → `catalog.tax_rate.country_not_available`).
  - `ListFiltersByRepeatedType` (`?type=Retefuente&type=ReteICA` no trae IVA; sin `type`, todas).
  - `UpdateIgnoresTypeAndCountryAndKeepsAbsentMinimumBase` (`PUT` con `type`/`country` distintos en el JSON y sin `minimumBase` → mismos tipo, país y base); `UpdateWithNullMinimumBaseClearsIt`.
  - `OptionsListOnlyCountriesOfTheTenantCompanies` (empresa `CO` → un país con los tres tipos del spec §9.1; tenant sin empresas → `countries: []`).
  - `ValidationErrorsCarryTheField` (`percentage: 101` → `validation.failed` con `errors.Percentage`).
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~TaxRateApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Validadores: `Percentage` entre 0 y 100, `Type` `MaximumLength(32)`, `Country` largo 2; las reglas de tipo/fracción/base las da el dominio (código). El handler de alta: `CountryExplicit` → `ITaxRateCountrySource` (`country_not_available` si no está en `TaxCountries` o en las empresas); `MinimumBase` no nulo con `TaxUnitName` → `ITaxUnitValueRepository.AnyAsync(country)` o `tax_unit_value_missing`. Endpoint `GET /tax-rate-options` con `CatalogPermissions.TaxRateRead`. Comentario BFF en `TaxRateOptionsDto`: la pantalla no conoce la tabla de países (anti objetivo del frontend).
- [ ] **Step 4: Run, expect PASS**; corre también `TaxRateActivationApiTests` y `TaxRateDeletionApiTests` (los cuerpos crudos con `percentage` entero siguen valiendo).
- [ ] **Step 5: Commit** — `feat(catalog): expose tax rate types, countries and options`.

### Task 5: Producto y envío aceptan sólo tasas que suman

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/ProductTaxRateResolver.cs:34-44`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationTaxRateLookup.cs:11` (NUEVO último parámetro `bool AddsTax = true`), `SetFreightTaxRate.cs:35-45`, `src/Bootstrapper/QuotationTaxRateLookup.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductWriteApiTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/` (la clase que hoy prueba `PUT` de la tasa del envío; búscala con `rg -l "freight_tax_rate" tests`)

**Interfaces:**
- Produces: `QuotationTaxRateRef(Guid Id, string Name, int Percentage, bool IsActive, bool AddsTax = true)`; el adaptador da `Percentage = 0` y `AddsTax = false` para una tasa que retiene (nunca llama `VatPercentage` sobre ella).

- [ ] **Step 1: Write the failing tests:** `ProductWithRetefuenteRateIs422` (crear y editar → `catalog.product.tax_rate_not_vat`); `FreightWithRetefuenteRateIs422` (→ `quotations.freight_tax_rate.not_vat`); `ProductWithInactiveIvaStillAccepted` (regresión del comentario `:36-39`).
- [ ] **Step 2: Run, expect FAIL** (filtra por cada clase, un proyecto a la vez).
- [ ] **Step 3: Implement.** En el resolver, después de existir: `taxRate.Behavior != TaxBehavior.Adds` → `CatalogDomainException("catalog.product.tax_rate_not_vat", …)`. En el envío, después de `inactive`: `!taxRate.AddsTax` → `quotations.freight_tax_rate.not_vat` (mensaje en español como sus vecinos `:40-44`).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(catalog): reject withholding rates on products and freight`.

### Task 6: Consola del operador — «Unidad tributaria»

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/CatalogOperatorPermissions.cs`, `TaxUnitOperator.cs` (queries, comando, validador, DTOs), `src/Modules/Catalog/Modules.Catalog.Api/TaxUnitOperatorEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin` junto a `:1696-1697`; `PermissionDefinition` junto a `:2218-2225`; políticas junto a `:2732-2736`), `src/Api/Program.cs:289` (`app.MapTaxUnitOperatorEndpoints();` después de `MapCityOperatorEndpoints`)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxUnitOperatorApiTests.cs` (NUEVO, calcado de `tests/Modules/Geography/Modules.Geography.IntegrationTests/CityOperatorApiTests.cs`), `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxUnitOperatorHandlerTests.cs` (NUEVO)

**Interfaces:**
- Consumes: Tarea 3; `OperatorAuthorization.EnsureAuthorized(IExecutionContext, TenantId, string, IOperatorTenant)` y `ITenantClock.GetAsync(Guid, CancellationToken)` (Tenancy.Application); `ICatalogAuditPublisher.Publish(...)`.
- Produces: `CatalogOperatorPermissions.TaxUnitsRead = "operator.tax_units.read"`, `TaxUnitsManage = "operator.tax_units.manage"`; `ListTaxUnitsQuery(TenantId TenantId) : IQuery<TaxUnitsDto>`; `SetTaxUnitValueCommand(TenantId TenantId, string Country, int Year, decimal Value) : ICommand<TaxUnitValueDto>`; `TaxUnitsDto(IReadOnlyList<TaxUnitCountryDto> Countries)`, `TaxUnitCountryDto(string Country, string TaxUnit, string Currency, bool NextYearMissing, IReadOnlyList<TaxUnitValueDto> Values)` (años de mayor a menor), `TaxUnitValueDto(int Year, decimal Value, DateTimeOffset UpdatedAt)`.

- [ ] **Step 1: Write the failing unit tests** (`TaxUnitOperatorHandlerTests`, con reloj del operador fijo): `ListShowsOnlyCountriesWithUnit` (sólo `CO`, aunque `All` crezca); `NextYearMissingUsesTheOperatorTenantClock` (UTC `2026-12-24T03:00Z` = 23-dic en Bogotá → `false`; `2026-12-24T06:00Z` → `true`); `SetAuditsValueSet` (acción `catalog.tax_unit.value_set`, recurso `"CO-2026"`); `SetTwiceReplaces`.
- [ ] **Step 2: Write the failing integration tests:** `WithoutReadPermissionIs403`; `NonOperatorTenantIs403` (con permiso, otro tenant); `PutCreatesThenReplaces` (`PUT /CO/2026 { value: 49799 }` dos veces, el `GET` trae una fila con el segundo valor); `PutForCountryWithoutUnitIs422` (`/ES/2026` → `catalog.tax_unit.country_without_unit`); `PutYearOutOfRangeIs422`.
- [ ] **Step 3: Run, expect FAIL** (unitarias, después integración filtrada a `TaxUnitOperatorApiTests`).
- [ ] **Step 4: Implement.** En cada handler `OperatorAuthorization` va **primero**, como `CityOperatorQueries.cs:42`. El `PUT` normaliza el país y llama `Create` o `Replace`; «año actual» y «hoy» salen de `ITenantClock.GetAsync(operatorTenantId)`. Las dos mitades del permiso (constante **y** política): sin la política el síntoma es 500. `PermissionDefinition` en español como las de ciudades: «Ver la unidad tributaria» / «Gestionar la unidad tributaria».
- [ ] **Step 5: Run, expect PASS**; corre también `tests/ArchitectureTests/ArchitectureTests` (`CompositionRootTests` revisa que toda política exista).
- [ ] **Step 6: Commit** — `feat(catalog): add the operator tax unit console`.

### Task 7: `Customer` con tasas de retención asignadas

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (`:286`, `:967`), `CustomerCommercialInfo.cs:21-29` (quita `WithRetention`)
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerTaxRate.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:200` (P5 + `OwnsMany`)
- Modify (compilación): `Modules.Customers.Application/{CustomerMapping,CustomerProvisioning,CreateCustomer,UpdateCustomer,ImportCustomers,CustomerWriteRules}.cs` (cada `ToCommercialInfo(..., withRetention, ...)` pierde el argumento; `CustomerMapping.cs:67` deja el valor derivado para la Tarea 8 — interino `false`)
- Create (generados): migración `AddCustomerTaxRates`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs` (o el archivo vecino que prueba `CustomerCommercialInfo`), `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerTaxRatePersistenceTests.cs` (NUEVO)

**Interfaces:**
- Produces: `IReadOnlyCollection<Guid> Customer.WithholdingTaxRateIds`; `bool Customer.SetWithholdingTaxRates(IReadOnlyCollection<Guid> taxRateIds, DateTimeOffset occurredAt)` (deduplica, `Guid.Empty` → `customers.customer.withholding_tax_rate_invalid`, sube `Version` y devuelve `true` sólo si el conjunto cambió); `CustomerTaxRate(Guid TenantId, Guid TaxRateId)`; `CustomerMapping.ToCommercialInfo(Guid? classificationId, bool vatSurplus)`.

- [ ] **Step 1: Write the failing unit tests:** `SettingTheSameRatesIsNotAChange`; `DuplicatedIdsAreStoredOnce`; `EmptyGuidIsRejected`; `SettingRatesBumpsVersion`.
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerTests"`
- [ ] **Step 3: Implement** y corrige la compilación (búscala con `rg -n "WithRetention" src/Modules/Customers`).
- [ ] **Step 4: Run** todas las unitarias de Customers → PASS.
- [ ] **Step 5: Persistence.** `customers.customer_tax_rates (tenant_id uuid, customer_id uuid, tax_rate_id uuid, PK (customer_id, tax_rate_id))`, sin FK a `catalog`, índice `IX_customer_tax_rates_tenant_tax_rate (tenant_id, tax_rate_id)` (lo usa «en uso», Tarea 8). P5: `customer.Property<bool>("with_retention").HasColumnName("with_retention").HasDefaultValue(false)` con comentario «sombra hasta la migración de contrato (spec §11.5)». Migración `AddCustomerTaxRates`: el `Up` crea la tabla y pone el default de `with_retention`; nada se borra.
- [ ] **Step 6: Persistence tests:** `RatesRoundTrip`; `ANewCustomerInsertsWithRetentionFalse` (la columna sombra no rompe el `INSERT`). RED (sin mapeo) → GREEN.
- [ ] **Step 7: Commit** — `feat(customers): assign withholding tax rates to customers`.

### Task 8: Contrato del cliente y «en uso» por clientes

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerTaxRateLookup.cs`, `src/Bootstrapper/CustomerTaxRateLookup.cs`, `src/Modules/Catalog/Modules.Catalog.Application/ITaxRateCustomerUsage.cs`, `src/Bootstrapper/TaxRateCustomerUsage.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/{CreateCustomer,UpdateCustomer,CustomersDtos,CustomerMapping,ListCustomers,GetCustomer,ChangeCustomerStatus,ManageCustomerAddresses}.cs`, `ICustomerRepository.cs` (NUEVO `AnyWithTaxRateAsync`), su implementación; `Modules.Customers.Api/CustomerEndpoints.cs` (`:296-298`, `:331`, `:363`, `:578-581`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/DeactivateTaxRate.cs:44-51`, `DeleteTaxRate.cs:53-58`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWithholdingTaxRatesApiTests.cs` (NUEVO), `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxRateDeletionApiTests.cs`, `TaxRateActivationApiTests.cs`

**Interfaces:**
- Consumes: Tareas 2 y 7.
- Produces: `ICustomerTaxRateLookup.FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken) : Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>>` y `ListWithholdingAsync(Guid tenantId, CancellationToken) : Task<IReadOnlyList<CustomerTaxRateRef>>` (las del tenant que retienen; la usa el Excel); `CustomerTaxRateRef(Guid Id, string Name, string Type, string Country, decimal Percentage, bool IsActive, bool Withholds)`; `CustomerWithholdingTaxRateDto(Guid Id, string Name, string Type, string Country, decimal Percentage, bool IsActive)`; `CustomerDto` gana `IReadOnlyList<CustomerWithholdingTaxRateDto> WithholdingTaxRates` (último, `= null` → `[]`) y `WithRetention` pasa a derivado (alguna `Type == "Retefuente"`); `CreateCustomerRequest`/`UpdateCustomerRequest` ganan `IReadOnlyList<Guid>? WithholdingTaxRateIds = null` y conservan `WithRetention` como opcional ignorado; comandos con `IReadOnlyList<Guid>? WithholdingTaxRateIds`; `CustomerWithholdingRules.ValidateAsync(ICustomerTaxRateLookup, Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)` (NUEVO, en `CustomerWriteRules.cs`); `ICustomerRepository.AnyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken)`; `ITaxRateCustomerUsage.AnyCustomerWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken)`.

- [ ] **Step 1: Write the failing integration tests** (`CustomerWithholdingTaxRatesApiTests`; tasas creadas por SQL en `catalog.tax_rates` o por la API de Catalog con `X-Permissions`):
  - `CreateWithRetefuenteAndReteIcaReturnsBoth` → `withholdingTaxRates` con los dos (`id`, `name`, `type`, `country`, `percentage`, `isActive`) y `withRetention: true`.
  - `TwoRetefuenteRatesAreRejected` → 422 `customers.customer.withholding_type_repeated`.
  - `[Theory] InvalidRateIsRejected` (IVA, inactiva, inexistente, de otro tenant) → 422 `customers.customer.withholding_tax_rate_invalid`.
  - `UpdateWithoutIdsKeepsRates`; `UpdateWithEmptyListClearsRates`; `LegacyWithRetentionInBodyIsIgnored` (`withRetention: true`, sin ids → `withholdingTaxRates: []`, `withRetention: false`).
  - `OnlyReteIcaDerivesWithRetentionFalse`.
  - `MergeSurvivorKeepsItsRates` (en `CustomerMergeApiTests`, spec §7.4).
  - En Catalog: `DeactivatingARateAssignedToACustomerIs422` y `DeletingARateAssignedToACustomerIs422` → `catalog.tax_rate.in_use`.
- [ ] **Step 2: Run, expect FAIL** — Customers y después Catalog, cada uno filtrado.
- [ ] **Step 3: Implement.** Validación en los dos handlers después de autorizar y validar forma: cada id existe en `FindManyAsync`, `IsActive` y `Withholds`; repetición por `(Country, Type)`. `CustomerMapping.ToDto` recibe `IReadOnlyDictionary<Guid, CustomerTaxRateRef> taxRatesById` (una sola consulta por página en `ListCustomers` y `ExportCustomers`); `ToDtoAsync` gana el parámetro `ICustomerTaxRateLookup`. El chequeo de Catalog se suma al de productos con el **mismo** código y mensaje «…at least one product or customer uses it.».
- [ ] **Step 4: Run, expect PASS**; corre también `CustomerApiTests` y `CustomerWriteApiTests` (cuerpos crudos con `withRetention`).
- [ ] **Step 5: Commit** — `feat(customers): accept and return withholding tax rates`.

### Task 9: Excel de clientes — una columna por tipo

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/IExcelCustomerImporter.cs:52-102`, `ImportCustomers.cs` (`:176-178`, `:413`, `:677`, `:720`, `:763`, `:822`), `CustomersDtos.cs:265` (`CustomerImportRowData`), `ExportCustomers.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerImporter.cs:68`, `ClosedXmlCustomerImportTemplateBuilder.cs:134`, `ClosedXmlCustomerExportBuilder.cs:122`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs`, `CustomerExportApiTests.cs`, y las unitarias de los tres builders en `Modules.Customers.UnitTests`

**Interfaces:**
- Produces: `CustomerImportColumns.Retefuente = "Retefuente"`, `CustomerImportColumns.ReteIca = "ReteICA"` (la constante `WithRetention` se borra); `CustomerImportRowData` cambia `string? WithRetention` por `string? Retefuente`, `string? ReteIca`. Sin código de fila nuevo: el error de fila reutiliza `customers.customer.withholding_tax_rate_invalid`, con la columna como campo (D20, «quien construye puede afinarlo»).

- [ ] **Step 1: Write the failing tests:** `ImportAssignsRatesByNamePerType`; `UnknownOrWrongTypeNameIsARowError` (`"IVA 19"` en la columna Retefuente → error de fila con ese código); `EmptyCellMeansNoRate`; `OldFileWithConRetencionIsReadIgnoringIt` (encabezado viejo presente → importa, sin error); `ExportWritesRateNamesPerType`; `TemplateHasBothColumnsAndNoConRetencion`.
- [ ] **Step 2: Run, expect FAIL** (unitarias de Customers; después integración filtrada).
- [ ] **Step 3: Implement.** Nombres comparados sin mayúsculas, como `FindByNameAsync` (`ITaxRateRepository.cs:22-30`), contra `ListWithholdingAsync` cargada una vez por archivo.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(customers): import and export withholding rates by type`.

### Task 10: `DocumentTotals` con retenciones por tasa

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/WithholdingRate.cs`, `DocumentWithholding.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/DocumentTotals.cs` (doc `:6-15`, `RetentionRate` `:28` se borra, `Compute` `:38-51`)
- Modify (interino, compilación): `Quotation.cs:1184-1188` y `Order.cs:1317` pasan `AppliesRetention ? [LegacyRetefuente] : []`, con `WithholdingRate.LegacyRetefuente` (2,5 %, sin base) **sólo hasta las Tareas 11 y 12**, que lo borran
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentTotalsTests.cs`

**Interfaces:**
- Consumes: `TaxCountries` (Tarea 1).
- Produces: `sealed record WithholdingRate(Guid TaxRateId, string Name, string Type, string Country, decimal Percentage, decimal? MinimumBase, string? MinimumBaseUnit, decimal? TaxUnitValue)` con `decimal? Threshold` (`null` = no puede aplicar: base en unidad tributaria sin valor; `0` sin base); `readonly record struct AppliedWithholding(WithholdingRate Rate, decimal Base, decimal Amount)`; `enum WithholdingSource { Customer, Billing, Document }`; entidad `DocumentWithholding` (`WithholdingSource Source`, `short Position`, `WithholdingRate Rate`, `decimal BaseAmount`, `decimal Amount`, `bool Applied`; `internal void Settle(AppliedWithholding? applied)`); `DocumentTotals.Compute(IEnumerable<LineAmountsView> lines, FreightAmounts freight, bool appliesVatSurplus, IReadOnlyList<WithholdingRate> withholdings)` (las ya filtradas por fuente, país, moneda y consumidor final) y el nuevo campo `IReadOnlyList<AppliedWithholding> Withholdings` (último, `null` en `Zero` se lee como vacío).

- [ ] **Step 1: Write the failing tests** (reescribe los cinco `Compute` existentes a la firma nueva):
  - `RegressionRetefuenteTwoAndAHalfGivesTodaysNumbers` — mismas líneas y envío que la prueba existente con `appliesRetention: true` (`:91`), lista `[Retefuente 2.5, sin base]` → `RetentionAmount` y `NetTotal` **idénticos** a los valores que esa prueba afirma hoy.
  - `TwoWithholdingsOnSubtotalWithoutVatNorFreight` (subtotal 1.000.000, IVA 190.000, envío 20.000; Retefuente 2,5 % y ReteICA 0,966 % → montos 25.000 y 9.660, `RetentionAmount` 34.660, `NetTotal = Total − 34.660`).
  - `MinimumBaseInUvt` con UVT 50.000 y base 4 (umbral 200.000): subtotal 199.999,99 no aplica; 200.000 aplica; 200.000,01 aplica.
  - `NullMinimumBaseAlwaysApplies`; `MinimumBaseInUvtWithoutValueNeverApplies`.
  - `ThresholdIsInclusiveAndFractionRoundsAwayFromZero` (0,966 % sobre 1.000.001 → 9.660,01; un caso con `…5` en el tercer decimal sube).
  - `NoWithholdingsMeansNetEqualsTotal`.
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~DocumentTotalsTests"`
- [ ] **Step 3: Implement.** Base = `subtotal` ya redondeado; `Threshold`: sin base → 0; con `TaxUnitValue` → `Round(MinimumBase × TaxUnitValue)`; sin unidad en el país (`TaxCountries.Find(Country)?.TaxUnitName is null`) → `MinimumBase`; si no → `null`. Reescribe el XML doc de `DocumentTotals` (sin «2,5 %»).
- [ ] **Step 4: Run** todas las unitarias de Quotations → PASS (las de cotización y pedido siguen verdes con el interino).
- [ ] **Step 5: Commit** — `feat(quotations): compute withholdings per frozen tax rate`.

### Task 11: La cotización congela sus retenciones

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (ctor `:46-92`, `:165-177`, `:343-381`, `Create` `:406-438`, `UpdateDetails` `:646-684`, `ChangeClient` `:745-773`, `EnsureSendable` `:847-866`, `EnsureConvertibleToOrder` `:960-978`, `CanBeConvertedToOrder` `:985-993`, `RefreshCustomerTaxProfile` `:995-1021` se borra, `RecalculateTotals` `:1180-1197`)
- Modify: `QuotationBillingAccount.cs` (doc `:16-18`; NUEVO `string? Country`, normalizado a mayúsculas), `QuotationPartyDetails.cs:238-246` (se borra `BillingWithRetention` de `QuotationParties`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCompanyLookup.cs:20` (NUEVO `string Country` después de `DocumentType`), `QuotationBillingAccountVerifier.cs:7` (`VerifiedBillingAccount` gana `string Country`), `QuotationBillingAccountResolver.cs:41-47`, `src/Bootstrapper/QuotationCompanyLookup.cs:24-28`
- Modify: `QuotationsDbContext.cs:136-141` (P5 para `customer_with_retention`/`party_with_retention`), `OwnsOne(BillingAccount)` de cotización y pedido (`billing_company_country`), NUEVO `OwnsMany(Withholdings)` → `quotations.quotation_withholdings` (calcado de `carrier_names`, `:948-955`)
- Create (generados): migración `AddQuotationWithholdings` (tabla, `billing_company_country` en `quotations` y `orders` con relleno desde `companies.companies` y `'CO'`, P1)
- Modify (compilación): todo llamador de `Quotation.Create`/`ChangeClient`/`RefreshCustomerTaxProfile` en Application con `[]` interino (los conecta la Tarea 13)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`, `QuotationWithholdingsTests.cs` (NUEVO), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationWithholdingsPersistenceTests.cs` (NUEVO)

**Interfaces:**
- Consumes: Tarea 10.
- Produces: `Quotation.Create(…, QuotationBillingAccount? billingAccount, string defaultCurrency, bool customerVatSurplus, IReadOnlyList<WithholdingRate> customerWithholdings, MemberId createdBy, DateTimeOffset occurredAt)`; `ChangeClient(Guid clientId, bool customerVatSurplus, IReadOnlyList<WithholdingRate> customerWithholdings, MemberId updatedBy, DateTimeOffset occurredAt)` (recopia, vacía la fuente `Billing`); `bool SetBillingWithholdings(IReadOnlyList<WithholdingRate> rates, MemberId updatedBy, DateTimeOffset occurredAt)` (exige `Billing` propio y `BillingAccount`; cada tasa del país de la cuenta → si no `quotation.billing.withholding_tax_rate_invalid`; dos del mismo `Type` → `quotation.billing.withholding_type_repeated`); `IReadOnlyCollection<DocumentWithholding> Withholdings`, `IEnumerable<DocumentWithholding> CustomerWithholdings`, `BillingWithholdings`, `AppliedWithholdings`; `QuotationBillingAccount.Country`; `QuotationCompanyRef(…, string DocumentType, string Country, bool IsActive, …)`.

- [ ] **Step 1: Write the failing unit tests** (`QuotationWithholdingsTests`; cuenta `CO`/`COP` salvo que se diga):
  - `FinalConsumerAppliesNone`; `UsdWithColombianCompanyAppliesNone`.
  - `ADraftWithoutBillingAccountAppliesNoWithholdingAndAppliesItOnceChosen` (Review Focus 1).
  - `ClientDataUsesCustomerRates`; `OwnBillingUsesSelectorsAndEmptySelectorsApplyNone`; `BackToClientDataRecoversTheCustomerPhoto` (la fuente `Billing` sobrevive al cambio de modo).
  - `OnlyRatesOfTheCompanyCountryApply` (tasa `ES` en la foto del cliente, cuenta `CO` → no aplica).
  - `ChangeClientRecopiesRatesAndVatSurplusAndClearsSelectors`.
  - `SendWithoutBillingAccountIs422` (`quotation.billing.account_required`) y `DraftWithoutAccountCanStillBeSaved`.
  - `SelectorOfAnotherCountryIs422`; `TwoSelectorsOfTheSameTypeAre422`; `SelectorsWithoutCompanyAre422`.
  - `ConvertRequiresOnlyVatSurplusAnswer` (parte propia, excedente contestado, sin selectores → convertible).
  - `AmountsFollowLineEdits` (agregar una línea cambia `Amount`; la tasa congelada no cambia).
  - Borra las pruebas de `RefreshCustomerTaxProfile` y de `PartyWithRetention`.
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationWithholdingsTests|FullyQualifiedName~QuotationTests"`
- [ ] **Step 3: Implement.** `RecalculateTotals` arma las candidatas (spec §7.5 pasos 1–4: fuente vigente `Billing` si hay parte propia, si no `Customer`; consumidor final, cuenta, `TaxCountries.Find(BillingAccount.Country)?.TaxCurrency == Currency`, `Country ==`) y liquida **cada** fila con `Settle`. Borra `WithholdingRate.LegacyRetefuente`. `Position` = orden de llegada dentro de la fuente.
- [ ] **Step 4: Run** las unitarias de Quotations → PASS.
- [ ] **Step 5: Persistence tests** (`QuotationWithholdingsPersistenceTests`): `PhotoRoundTripsWithBothSources`; `BillingCompanyCountryIsBackfilledFromCompanies` (patrón `QuotationSettingsMigrationTests`: baja a la migración anterior, inserta empresa `ES` y una cotización con su `billing_company_id`, sube → `ES`; sin empresa → `CO`; termina con `MigrateAsync(null)`). RED → mapeo y migración → GREEN.
- [ ] **Step 6: Commit** — `feat(quotations): freeze withholding rates on quotations`.

### Task 12: El pedido congela sus retenciones

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`:86-98`, `:188-220`, `AppliesRetention` `:302-306`, `FromQuotation` `:317-360`, fábricas `:383-540`, `EnsureBillingRules` `:591-618`, `SetBilling` `:629-684`, `RefreshCustomerTaxProfile` `:849-864` se borra, `RecalculateTotals` `:1315-1330`), `OrderInputs.cs:4-12`
- Modify: `QuotationsDbContext.cs:552-554` (P5), NUEVO `OwnsMany(Withholdings)` → `quotations.order_withholdings`
- Create (generados): migración `AddOrderWithholdings`
- Modify (compilación): `OrderEdits.cs:84-88` (sin `BillingWithRetention`), llamadores de `OrderHeader` con `[]` interino (Tarea 14)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `OrderWithholdingsTests.cs` (NUEVO)

**Interfaces:**
- Consumes: Tareas 10–11.
- Produces: `OrderHeader(Guid ClientId, MemberId AdvisorId, string Currency, string? PaymentMethod, bool IsRetail, int? GlobalScaleFloor, bool CustomerVatSurplus, IReadOnlyList<WithholdingRate> Withholdings)`; `Order.SetBilling(QuotationPartyDetails? billing, bool billsToFinalConsumer, bool billingUsesBusinessName, bool? partyVatSurplus, MemberId updatedBy, DateTimeOffset occurredAt, bool enforceCompleteness = true)`; `bool Order.SetWithholdings(IReadOnlyList<WithholdingRate> rates, MemberId updatedBy, DateTimeOffset occurredAt)` (`Pending`; país de la cuenta y uno por tipo, códigos `order.billing.*`); `bool Order.SetBillingAccount(QuotationBillingAccount billingAccount, MemberId updatedBy, DateTimeOffset occurredAt)` (`Pending`; otra moneda → `order.billing.currency_change_not_allowed`, P10); `IReadOnlyCollection<DocumentWithholding> Withholdings`, `AppliedWithholdings` (fuente única `Document`).

- [ ] **Step 1: Write the failing unit tests:** `FromQuotationInheritsTheActiveSourceAsIs` (cotización con parte propia y selector → el pedido tiene esa tasa como `Document`, mismos montos); `PendingAcceptsSelectorsAndNotPendingRejectsThem` (`order.order.not_pending`); `SetBillingAccountEnablesWithholdings` (pedido de `Preview` sin cuenta → sin retención; con cuenta `CO` → aplica); `ChangingCompanyCountryStopsApplyingTheOldRate` (Review Focus 5); `CurrencyChangeOnSetBillingAccountIs422`; `EnsureBillingRulesAsksOnlyVatSurplus` (parte propia con `partyVatSurplus: null` → `order.billing.tax_profile_required`; con valor y sin retención → pasa).
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderWithholdingsTests|FullyQualifiedName~OrderTests"`
- [ ] **Step 3: Implement** con la misma regla de candidatas que la cotización (extráela a un `internal static class WithholdingCandidates` en Domain, la usan los dos agregados).
- [ ] **Step 4: Run** → PASS; persistencia `OrderWithholdingsPersistenceTests.PhotoRoundTrips` (RED → mapeo y migración → GREEN).
- [ ] **Step 5: Commit** — `feat(quotations): freeze withholding rates on orders`.

### Task 13: Cotización — fuentes de tasas, guardado y contrato

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationWithholdingLookup.cs`, `WithholdingSelection.cs`, `src/Bootstrapper/QuotationWithholdingLookup.cs`
- Modify: `QuotationDraftBuilder.cs:78`, `:88`; `ChangeQuotationClient.cs:71-72`; `SaveQuotation.cs`; `PreviewQuotation.cs`; `PreviewNewQuotation.cs`; `QuotationEditsApplication.cs:41-45`; `GetQuotation.cs:27-41`; `AddQuotationItem.cs:48-53`; `UpdateQuotationItem.cs:50-55`; `RemoveQuotationItem.cs:39-44`; `BatchUpdateQuotationItems.cs:74-80` (se borran los refrescos); `QuotationMapping.cs:151-161`; `QuotationsDtos.cs` (`:32-75`, `:237-263`, `:447-460`, `:507`); `QuotationResponseComposer.cs`; `src/Bootstrapper/QuotationCustomerLookup.cs:103` (`WithRetention` derivado: alguna tasa asignada de tipo `Retefuente`); registro en `QepServiceCollectionExtensions.cs` junto a `:1522`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationWithholdingsApiTests.cs` (NUEVO), `tests/Modules/Quotations/Modules.Quotations.UnitTests/WithholdingSelectionTests.cs` (NUEVO), `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationWithholdingLookupTests.cs` (NUEVO)

**Interfaces:**
- Consumes: Tareas 3, 8, 11; `ITenantClock`.
- Produces: `IQuotationWithholdingLookup.ForCustomerAsync(Guid tenantId, Guid customerId, string? country, DateOnly documentDate, CancellationToken) : Task<IReadOnlyList<WithholdingRate>>` (`country` nulo = todos los países) y `ForRatesAsync(Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, DateOnly documentDate, CancellationToken) : Task<IReadOnlyDictionary<Guid, WithholdingRateCandidate>>`; `WithholdingRateCandidate(WithholdingRate Rate, bool IsActive, bool Withholds)`; `WithholdingSelection.ResolveAsync(IQuotationWithholdingLookup lookup, Guid tenantId, IReadOnlyCollection<Guid> ids, string? companyCountry, DateOnly documentDate, string codePrefix, CancellationToken) : Task<IReadOnlyList<WithholdingRate>>` (`codePrefix` `"quotation"` u `"order"`); `DocumentDates.LocalDate(TenantCalendar calendar, DateTimeOffset createdAt) : DateOnly` (NUEVO, la fecha del documento en la zona del tenant); `QuotationPartiesRequest` gana `IReadOnlyList<Guid>? BillingWithholdingTaxRateIds = null` (al final; `BillingWithRetention` se queda en el record, ignorado, con comentario); DTOs `WithholdingDto(Guid TaxRateId, string Name, string Type, string Country, decimal Percentage, decimal? MinimumBase, string? MinimumBaseUnit, decimal? TaxUnitValue, decimal Base, decimal Amount)` y `WithholdingTaxRateDto(Guid Id, string Name, string Type, string Country, decimal Percentage)`; `QuotationDto`/`QuotationResponse` ganan `Withholdings`, `CustomerWithholdingTaxRates`, `BillingWithholdingTaxRates` y derivan `BillingWithRetention`.

- [ ] **Step 1: Write the failing unit tests** (`WithholdingSelectionTests`): `[Theory]` inexistente, inactiva, que suma, de otro país, sin empresa → `{prefix}.billing.withholding_tax_rate_invalid`; dos del mismo tipo → `{prefix}.billing.withholding_type_repeated`. `QuotationWithholdingLookupTests`: `UsesTheUvtOfTheDocumentYearOrTheLatestEarlier`; `FiltersByCountryWhenGiven`. `DocumentDatesTests.NewYearsEveEveningInBogotaIsThatYear` (`2027-01-01T01:00Z` con `America/Bogota` → `2026-12-31`, spec §13).
- [ ] **Step 2: Write the failing integration tests** (`QuotationWithholdingsApiTests`; UVT, empresa `CO`, tasas y cliente sembrados):
  - `TwoWithholdingsGiveTwoLinesAndTheirSum` (cliente con Retefuente 2,5 % y ReteICA 0,966 % → `withholdings` de dos filas con `base`/`amount`, `retentionAmount` = suma, `customerWithholdingTaxRates` con las dos).
  - `SelectorRetefuenteServiciosAppliesFourPercent` (parte propia, `billingWithholdingTaxRateIds: [id4%]` → 4 %; `billingWithRetention: true`).
  - `OldFrontendBodyKeepsSelectorsAndNeverNullsBillingWithRetention` (Review Focus 2).
  - `GetNoLongerWrites` (cambia las tasas del cliente por la API; `GET` → misma `version`, mismos montos).
  - `EditingTheCustomerDoesNotMoveADraft` (D17, D24: tasas **y** excedente).
  - `PreviewsReturnTheSameFields` (`POST /quotations/preview` y `/{id}/preview`).
  - `SendWithoutCompanyIs422` (`quotation.billing.account_required`).
  - `ClientWithRetentionIsDerived` (`client.withRetention`).
  - `RateFrozenOnlyInAQuotationCanBeDeactivated` (la tasa sale de la ficha del cliente, queda en la foto; `POST …/deactivate` → 200, y la cotización sigue mostrándola).
- [ ] **Step 3: Run, expect FAIL** (unitarias de Quotations y `Bootstrapper.UnitTests`; integración filtrada a `QuotationWithholdingsApiTests`).
- [ ] **Step 4: Implement.** Fecha del documento: `DateOnly.FromDateTime(calendar.ToLocal(quotation.CreatedAt).DateTime)` con `ITenantClock.GetAsync(tenantId)` (al crear, `calendar.Today`). Al crear y en `ChangeClient`: `ForCustomerAsync(country: null)`. Al guardar con `Billing` propio y `BillingWithholdingTaxRateIds` no nulo: `WithholdingSelection` con el país de la **nueva** cuenta, después `SetBillingWithholdings`; nulo conserva. El adaptador congela `TaxUnitValue = TaxUnitValues.ValueFor(...)` sólo si `MinimumBase` no es nulo y el país tiene unidad. Escribe en el DTO el motivo de `billingWithRetention` derivado (BFF: el frontend viejo bloquea la conversión con `null`, `quote-actions.ts:104-106`).
- [ ] **Step 5: Sweep raw bodies** (gotcha de CLAUDE.md): `rg -l "/send" tests` → toda prueba que envía una cotización sin `billingAccount` gana una (incluido `ReportingApiHarness.CreateSentQuotationAsync` y `QuotationsApiHarness`). Corre las clases tocadas de Quotations y Reporting.
- [ ] **Step 6: Run, expect PASS** — integración filtrada a `QuotationWithholdingsApiTests|QuotationApiTests|QuotationSendVoidApiTests`.
- [ ] **Step 7: Commit** — `feat(quotations): resolve and expose withholdings on quotations`.

### Task 14: Pedido — alta, tienda, Shopify y edición `Pending`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderDraftBuilder.cs:120-130`, `IExternalOrderWriter.cs:150-185`, `IStoreOrderGateway.cs:55-65`, `:175-195`, `OrderEdits.cs:23-97`, `SaveOrderEdits.cs:78-86`, `PreviewOrderEdits.cs:60-67`, `OrdersDtos.cs` (`:18-44`, `:154-175`, `:212-238`), `OrderMapping.cs`, `OrderResponseComposer.cs`, `ConvertQuotationToOrder.cs`
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreCustomerDirectory.cs:3-4` (se borra `WithRetention`), `IStoreOrders.cs:62`, `SubmitOrderRequest.cs:296-309`; `src/Bootstrapper/StoreOrdersAdapter.cs:129`, `StoreCustomerDirectory.cs:183`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderWithholdingsApiTests.cs` (NUEVO), `OrderEditsApiTests.cs`, `StoreOrderGatewayTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/` (la clase del webhook de pedidos; `rg -l "orders/create" tests/Modules/Shopify`); `tests/Modules/Store/Modules.Store.UnitTests` (los dobles de `StoreCustomerRef`)

**Interfaces:**
- Consumes: Tareas 12–13.
- Produces: `IOrderEdits` gana `IReadOnlyList<Guid>? WithholdingTaxRateIds` y `QuotationBillingAccountRequest? BillingAccount`; `SaveOrderEditsRequest` gana los dos al final (P9); `OrderBillingEditRequest.BillingWithRetention` se queda, ignorado; `OrderDto`/`OrderResponse` ganan `Withholdings`, `CustomerWithholdingTaxRates` (`[]` en el pedido), `BillingWithholdingTaxRates` (= tasas vigentes) y derivan `BillingWithRetention`; `StoreCustomerRef(Guid Id, bool IsComplete, bool VatSurplus)`; `StoreOrderDraft` sin `BillingWithRetention`.

- [ ] **Step 1: Write the failing integration tests** (`OrderWithholdingsApiTests` salvo que se diga):
  - `ConvertedOrderInheritsTheQuotationPhoto`.
  - `ManualOrderTakesCustomerRatesOfItsCompanyCountry` (cliente con tasas `CO` y `ES`, empresa `CO` → sólo la `CO`).
  - `StoreOrderTakesCustomerRatesDespiteOwnBilling` (en `StoreOrderGatewayTests`).
  - `ExternalOrderCopiesOnlyCustomerRatesOfTheCompanyCountry` y la regresión `ShopifyWebhookStillCreatesOrders` (en la clase del webhook de Shopify; Review Focus 4).
  - `PendingEditChangesRatesAndNeverTouchesTheCustomer` (D21; el cliente sigue con las suyas).
  - `PendingEditSetsBillingAccountAndEnablesSelectors` (D22; `billingAccount` en el cuerpo) y `ResendingAForeignRateAfterCountryChangeIs422` (Review Focus 5).
  - `PreviewEditReturnsTheSameWithholdingsTheSaveWouldStore`.
  - `NonPendingEditIs422` (`order.order.not_pending`).
- [ ] **Step 2: Run, expect FAIL** — Quotations filtrado; después Shopify y Store, cada uno solo.
- [ ] **Step 3: Implement.** Manual, tienda y externo: `ForCustomerAsync(country: billingAccount.Country, documentDate)` antes de `KeyedOrderCommit`, con la fecha local de `ITenantClock`. Conversión: `Order.FromQuotation` copia (Tarea 12). Edición: `BillingAccount` primero (`QuotationBillingAccountResolver` + `SetBillingAccount`), después `WithholdingSelection` con el país resultante y `SetWithholdings`; el historial registra los dos cambios con el texto de `OrderChangeSummary` (agrega `WithholdingsChanged(before, after)` y `BillingAccountChanged`, mismo estilo que `BillingChanged`). Nada de esto puede lanzar en el camino del webhook: un cliente sin tasas da `[]`.
- [ ] **Step 4: Run, expect PASS**, más `OrderEditsApiTests`, `OrderApiTests` y las unitarias de Store.
- [ ] **Step 5: Commit** — `feat(quotations): resolve, edit and expose withholdings on orders`.

### Task 15: PDF e historial de la cotización

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs:56-62` (NUEVO `IReadOnlyList<QuotationPdfWithholding> Withholdings`), su armado, `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:413-431`
- Modify: `QuotationChangeSummary.cs:182`, `:318`, `:345`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs`, la prueba unitaria vecina de `QuotationChangeSummary`

**Interfaces:**
- Produces: `QuotationPdfWithholding(string Name, decimal Amount)` (sólo las aplicadas); `QuotationChangeSummary` compara `BillingWithholdingTaxRateIds` (conjunto ordenado) en lugar de `BillingWithRetention`.

- [ ] **Step 1: Write the failing tests:** `PdfHasOneRowPerAppliedWithholdingWithTheRateName` (el JSON que recibe Typst trae `withholdings: [{ name: "Retefuente 2,5 %", amount }, { name: "ReteICA Bogotá", amount }]`; el PDF real, en la integración, contiene los dos nombres); `PdfWithoutWithholdingsHasNoRetentionRow`; `ChangingASelectorIsAHeaderChange`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.** En el `.typ`: una fila `"<nombre>:"` con `-` y moneda por retención, en lugar de la fila fija «Retención en la fuente:»; «Neto a pagar» sigue igual (`hay-retencion` = lista no vacía).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(quotations): print one row per withholding`.

### Task 16: Excel de pedidos — una columna por tipo

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` (al final, P12), `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs:335`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`, `OrdersExportColumnCatalogTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`

**Interfaces:**
- Produces: claves `withholding_retefuente` («Retefuente») y `withholding_reteica` («ReteICA»), `DefaultVisible: false`; celda = suma de `Amount` de las aplicadas de ese tipo, `0` si ninguna. «Retencion» (`retention_amount`) sigue con `order.RetentionAmount`.

- [ ] **Step 1: Write the failing tests:** `ExportHasAmountPerWithholdingType`; `NewColumnsAreHiddenByDefaultAndLast`; `NoHeaderCollidesWithTheSeedLayout` (gotcha de CLAUDE.md: ninguna visible del layout semilla se llama «Retefuente» ni «ReteICA»).
- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExport"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS** (más `OrderExportApiTests` filtrado).
- [ ] **Step 5: Commit** — `feat(quotations): export withholdings per type in the orders workbook`.

### Task 17: Migración de datos — «Retefuente 2,5 %» y las fotos

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_MigrateRetentionToWithholdings.cs` (con `dotnet ef migrations add MigrateRetentionToWithholdings …` y el `Up` escrito a mano; `Down` vacío con comentario «no hay vuelta: es una migración de datos»)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RetentionToWithholdingsMigrationTests.cs` (NUEVO)

**Interfaces:**
- Consumes: tablas de las Tareas 2, 7, 11 y 12 (P6).

- [ ] **Step 1: Write the failing migration tests** (baja a `AddOrderWithholdings`, siembra por SQL, sube, `MigrateAsync(null)` al final):
  - `CreatesOneRetefuentePerTenantThatUsedIt` (tenant con cliente `with_retention = true` → una tasa `Retefuente`/`CO`/`2.500`/sin base/activa llamada «Retefuente 2,5 %»; tenant sólo con una cotización `party_with_retention = true`, también; tenant sin nada, ninguna).
  - `NameTakenUsesMigratedSuffix` (ya existe «Retefuente 2,5 %» de tipo IVA → «Retefuente 2,5 % (migrada)»).
  - `AssignsTheRateToEveryCustomerWithRetention`.
  - `QuotationGetsCustomerAndBillingRows` (`customer_with_retention` → fila `Customer`; parte propia con `party_with_retention` → fila `Billing`).
  - `OrderGetsOneDocumentRow` (parte propia: `party_with_retention`; si no, `customer_with_retention`).
  - `NoTotalChanges` (`subtotal`, `total`, `retention_amount`, `net_total` idénticos antes y después en cotizaciones y pedidos); `MigratedDraftKeepsItsStoredTotalsOnRead` (un `GET` por la API devuelve el mismo `retentionAmount`; Review Focus 1).
  - `AppliedRowsCarryTodaysAmount` (`applied = true`, `amount = retention_amount`, `base_amount = subtotal` donde aplicaba: fuente vigente, no consumidor final, `currency = 'COP'`; si no, `applied = false`, montos 0).
  - `ExistingRatesAreIvaColombia` (regresión de la Tarea 2 vista desde aquí).
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~RetentionToWithholdingsMigrationTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement** el SQL en un `migrationBuilder.Sql(...)` por paso, cada uno dentro de `DO $$ BEGIN IF to_regclass('catalog.tax_rates') IS NOT NULL AND to_regclass('customers.customer_tax_rates') IS NOT NULL THEN … END IF; END $$;`. Ids con `gen_random_uuid()`, `version = 1`, `created_at = updated_at = now()`. Las filas de la foto copian nombre, tipo, país y porcentaje de la tasa migrada, `minimum_base`, `minimum_base_unit` y `tax_unit_value` nulos, `position = 0`.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(quotations): migrate the fixed retention to withholding rates`.

### Task 18: Verificación de la rama entera

- [ ] **Step 1: Build y arquitectura**
  `dotnet build`
  `dotnet test tests/ArchitectureTests/ArchitectureTests`
  Esperado: `Build succeeded. 0 Warning(s) 0 Error(s)`; `CatalogLayerTests`, `CustomersLayerTests`, `QuotationsLayerTests`, `StoreLayerTests`, `ShopifyLayerTests` y `CompositionRootTests` en verde, sin cambios.
- [ ] **Step 2: Unitarias tocadas**
  `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests`
  `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests`
  `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests`
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`
  Esperado: `Passed!`, 0 fallidas.
- [ ] **Step 3: Integración, un proyecto a la vez (una sola vez)**
  `$env:Seed__Enabled = "false"`
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests -- xUnit.MaxParallelThreads=4`
  Cada uno al terminar el anterior. Una falla que también falla en `develop` se reporta como preexistente, no se arregla aquí. Si cortas una corrida, busca huérfanos `Modules.*.IntegrationTests.exe` por la ruta del worktree.
- [ ] **Step 4: Recorrido del spec.** §10 (cada código) y §13 contra los nombres de prueba de las Tareas 1–17; pega el mapeo en el reporte. Lo que falte es una prueba nueva en la tarea dueña, no una nota.
- [ ] **Step 5: Estado final**
  `git branch --show-current`
  `git status --short`
  `git log --oneline develop..HEAD`
  Esperado: `feature/impuestos-por-tipo`, árbol limpio, un commit por tarea; `git diff --stat develop..HEAD -- docs/estado.md` vacío.
- [ ] **Step 6: Notas para el merge** (no se ejecutan en la rama): despliegue backend primero; el operador carga la UVT de Colombia del año en curso **antes** de que alguien cree una tasa con base mínima; avisar al frontend de P9 y P10; pendientes del spec §15 (tenants sin empresa facturadora, pedidos con `billing_company_id` nulo) — verificar con `SELECT` en producción antes del release; la migración de contrato (§11.5) es otra rama, en N+1.
