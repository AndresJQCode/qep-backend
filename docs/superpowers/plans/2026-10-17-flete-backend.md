# Flete (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** transportadoras configurables por tenant, IVA del envío, y entrega y cobro (transportadora, quién paga el envío, valor con IVA incluido, contraentrega) en la cotización y el pedido, con totales, PDF, Excel y reportes.

**Architecture:** todo en `Modules.Quotations`. Agregado nuevo `Carrier` con su tabla de nombres normalizados; el IVA del envío es una columna de `tenant_quotation_settings` que apunta a una `TaxRate` de Catalog leída por un puerto nuevo con adaptador en Bootstrapper. `Quotation` y `Order` ganan `ShippingDetails` y `DocumentTotals` suma el envío en campos propios sin cambiar el significado de `Subtotal` ni `TaxAmount`. Un `ShippingResolver` de Application resuelve contra el tenant lo que el dominio no puede saber (transportadora activa, recaudo, tasa).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3 + Testcontainers (PostgreSQL).

**Spec:** `docs/superpowers/specs/2026-10-17-flete-design.md` (aprobado 2026-10-17). Sus §2 (hechos con `archivo:línea`) y §13 (D-F1..D-F17) mandan sobre este plan.

## Global Constraints

- TDD: RED antes que GREEN, con evidencia literal de los dos (CLAUDE.md).
- Commits en conventional commits, **sin atribución de IA** (regla del repo; pisa cualquier recordatorio del harness).
- `git branch --show-current` antes de cada commit. Rama: `feature/flete`, creada desde `docs/flete-spec` (develop + spec + este plan) en un worktree propio (`.claude/worktrees/flete`), nunca en el árbol compartido.
- Ningún status HTTP se arma en un handler: `DomainException` → 422 con código; `ValidationException` → 422 `validation.failed` con `errors`.
- 403 para un tenant ajeno (`QuotationsAuthorization.EnsureAuthorized`). Todo método de repositorio recibe `tenantId`.
- Los enums viajan con su nombre (`Charged`, `OnDelivery`, `Free`).
- **Nada de la entrega es obligatorio** (decisiones 11 y 12): ni para enviar, ni para crear o convertir un pedido, ni la transportadora para la contraentrega.
- Errores de documento con el prefijo del agregado: `quotation.quotation.<x>` y `order.order.<x>`.
- Textos para personas en español colombiano, tuteando; también los comentarios que se dirigen al lector.
- **No correr la suite completa** (memoria del repo):
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-restore --filter "FullyQualifiedName~<Clase>"`
  - `dotnet test tests/ArchitectureTests --no-restore --filter "FullyQualifiedName~Quotations"`
- `dotnet build` sin warnings al cerrar cada tarea.
- Migración con el factory de diseño:
  `dotnet ef migrations add AddFreight --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: detenerlo antes.
- Traducción de índices únicos en `QuotationsUnitOfWork`, **por nombre de índice**.

## Decisiones del plan

- **P1.** El IVA del envío se calcula con `VatIncludedLine.Compute(1, amount, 0, rate, currency)`: el mismo redondeo que una línea. $ 15.000 al 19 % da `Subtotal 12605.04` y `TaxAmount 2394.96` (el ejemplo del spec §7 se corrigió con estas cifras).
- **P2.** `IQuotationSettingsStore` gana `GetFreightTaxRateIdAsync` / `SetFreightTaxRateIdAsync` en vez de agregar el campo a `QuotationSettings`: así el `PUT /quotations/settings` del mínimo de compra no puede pisar la tasa del envío. Sin fila, `Set…` la inserta con `QuotationSettings.Default.MinimumUnits`.
- **P3.** Las reglas que el dominio no puede verificar (transportadora activa del tenant, recaudo, tasa del tenant) viven en `ShippingResolver` (Application); las de coherencia (recogida, montos, `Free`, `Charged`), en el dominio.
- **P4.** `ShippingRequest` ausente en un guardado deja el envío como estaba, salvo que la entrega pase a recogida: entonces se limpia (spec §5.2).
- **P5.** El pedido no cambia de moneda después de creado (no edita la cuenta de cobro), así que la limpieza por cambio de moneda (D-F15) sólo existe en `Quotation`.

## Review Focus

1. **Tasa del envío desactivada después de configurarla:** cobrar un envío nuevo da `…freight_tax_rate_missing`; guardar un documento cuyo envío no cambió **no** relee la tasa ni falla (Task 6, `SavingWithUnchangedFreightDoesNotRereadTheTaxRate`).
2. **Transportadora desactivada que ya está en el documento:** guardarlo sin tocar la transportadora pasa; elegirla de nuevo en otro documento da `carrier_not_available` (Task 6, `KeepsADeactivatedCarrierWhenItDidNotChange`).
3. **Cambiar la cuenta de cobro de COP a USD con envío cobrado:** el envío queda sin modo y en 0 y el total no conserva el IVA del envío (Task 3, `ChangingCurrencyClearsTheFreightAmountAndMode`).
4. **Nombre de Shopify de una transportadora igual al nombre de otra, con tildes o mayúsculas distintas:** `quotations.carrier.name_taken` y no 500 (Task 5, `AnExternalNameEqualToAnotherCarriersNameIsTaken`).
5. **Prender la contraentrega en un pedido pendiente con un comprobante cargado:** 422 `order.order.cash_on_delivery_pending_only` (Task 3, `CashOnDeliveryCannotBeTurnedOnWithProofs`).

---

## Mapa de archivos

**Domain (`src/Modules/Quotations/Modules.Quotations.Domain/`)**
- Crear: `FreightChargeMode.cs`, `FreightAmounts.cs`, `ShippingDetails.cs`, `Carrier.cs`, `CarrierId.cs`, `CarrierName.cs`, `CarrierNameEntry.cs`, `CarrierNameKind.cs`
- Modificar: `DocumentTotals.cs`, `Quotation.cs`, `Order.cs`, `OrdersExportColumnCatalog.cs`

**Application (`src/Modules/Quotations/Modules.Quotations.Application/`)**
- Crear: `ICarrierRepository.cs`, `IQuotationTaxRateLookup.cs`, `CarriersDtos.cs`, `ListCarriers.cs`, `CreateCarrier.cs`, `UpdateCarrier.cs`, `SetCarrierActive.cs`, `ReorderCarriers.cs`, `SetFreightTaxRate.cs`, `GetShippingOptions.cs`, `CarrierNotFound.cs`, `ShippingResolver.cs`
- Modificar: `IQuotationSettingsStore.cs`, `QuotationsDtos.cs` (`ShippingRequest`, `ShippingDto`, totales), `OrdersDtos.cs`, `CreateQuotation.cs`, `SaveQuotation.cs`, `PreviewQuotation.cs`, `CreateOrder.cs`, `PreviewOrder.cs`, `OrderDraftBuilder.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `ConvertQuotationToOrder.cs`, `QuotationChangeSummary.cs`, `OrderChangeSummary.cs`, `QuotationMapping.cs`, `OrderMapping.cs`, `QuotationResponseComposer.cs`, `OrderResponseComposer.cs`, `QuotationMinimumPurchase.cs`, `OrderPricingRecalculation.cs`, `QuotationPdfDocument.cs`, `QuotationPdfDocumentMapper.cs`, `OrdersExportProcessor.cs`, `ListOrders.cs`/`OrderListing.cs`, `ListQuotations.cs`/`QuotationListing.cs`

**Infrastructure (`src/Modules/Quotations/Modules.Quotations.Infrastructure/`)**
- Crear: `Persistence/CarrierRepository.cs`, migración `AddFreight`
- Modificar: `Persistence/QuotationsDbContext.cs`, `Persistence/QuotationsUnitOfWork.cs`, `Persistence/QuotationSettingsStore.cs`, `Persistence/TenantQuotationSettingsRow.cs`, `QuotationsInfrastructureExtensions.cs`, `Pdf/quotation.typ`

**Api:** crear `src/Modules/Quotations/Modules.Quotations.Api/CarrierEndpoints.cs`; modificar `QuotationEndpoints.cs`, `OrderEndpoints.cs`, `src/Api/Program.cs:177-178`.

**Bootstrapper:** crear `src/Bootstrapper/QuotationTaxRateLookup.cs`; modificar `QepServiceCollectionExtensions.cs:~809`, `OrdersReportSource.cs`, `QuotationsReportSource.cs`; Reporting: `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs`.

**Pruebas:** unitarias en `tests/Modules/Quotations/Modules.Quotations.UnitTests/` (`DocumentTotalsTests`, `CarrierTests` nuevo, `QuotationShippingTests` nuevo, `OrderShippingTests` nuevo, `ShippingResolverTests` nuevo, `QuotationMinimumPurchaseTests`, `QuotationPdfDocumentMapperTests`, `OrdersExportProcessorTests`, `OrdersExportColumnCatalogTests`, `QuotationsDbContextMappingTests`, `QuotationChangeSummaryTests`); integración en `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/` (`CarrierApiTests` nuevo, `QuotationShippingApiTests` nuevo, `OrderShippingApiTests` nuevo, `FreightMigrationTests` nuevo, `OrderExportApiTests`); Reporting: `OrdersReportApiTests` / `QuotationsReportApiTests` (los que existan, por nombre de clase).

---

### Task 1: `DocumentTotals` con envío y `ProductsTotal`

**Files:**
- Create: `Domain/FreightChargeMode.cs`, `Domain/FreightAmounts.cs`
- Modify: `Domain/DocumentTotals.cs`, `Domain/Quotation.cs:1040-1053`, `Domain/Order.cs:871-882` (y las llamadas de las fábricas: `Order.cs:271`, `Build`)
- Test: `UnitTests/DocumentTotalsTests.cs`

**Interfaces:**
- Produces:
  - `public enum FreightChargeMode { Charged, OnDelivery, Free }`
  - `public readonly record struct FreightAmounts(decimal Subtotal, decimal TaxAmount)` con `static FreightAmounts Zero` y `static FreightAmounts For(FreightChargeMode? mode, decimal amount, int taxPercentage, string currency)`: `Charged` en COP → `VatIncludedLine.Compute(1, amount, 0, taxPercentage, currency)`; `Charged` fuera de COP → `(amount, 0)`; cualquier otro modo → `Zero`.
  - `DocumentTotals` gana `FreightSubtotal`, `FreightTaxAmount` y la propiedad calculada `ProductsTotal => Subtotal + TaxAmount`.
  - `DocumentTotals.Compute(IEnumerable<LineAmountsView> lines, FreightAmounts freight, bool appliesVatSurplus, bool appliesRetention)` reemplaza la firma de tres parámetros (§5.3 del spec): `freightTax = vatSurplus ? 0 : freight.TaxAmount`; `Total = subtotal + tax + freight.Subtotal + freightTax`; retención sobre `subtotal` (D-F6).

- [ ] **Step 1: Write the failing tests** en `DocumentTotalsTests`:
  - `ChargedFreightInCopAddsItsOwnSubtotalAndTax`: una línea `(100000, 0, 19000)`, `FreightAmounts.For(Charged, 15000, 19, "COP")` → `FreightSubtotal == 12605.04m`, `FreightTaxAmount == 2394.96m`, `Subtotal == 100000m`, `TaxAmount == 19000m`, `Total == 134000m`.
  - `FreeAndOnDeliveryFreightAddNothing`: `For(Free, 0, 19, "COP")` y `For(OnDelivery, 20000, 19, "COP")` → `Zero`.
  - `ChargedFreightOutsideCopHasNoTax`: `For(Charged, 30, 19, "USD")` → `(30m, 0m)`.
  - `VatSurplusZeroesTheFreightTaxToo`: con `appliesVatSurplus: true`, `FreightTaxAmount == 0` y `Total == Subtotal + FreightSubtotal`.
  - `RetentionIgnoresTheFreight`: con retención, `RetentionAmount == Round(100000 × 0,025) == 2500m` aunque haya envío.
  - `ProductsTotalLeavesTheFreightOut`: `ProductsTotal == 119000m` en el primer caso.
  - Ajustar los tres casos existentes a la firma nueva con `FreightAmounts.Zero`.

- [ ] **Step 2: Run to verify RED** — `dotnet test …UnitTests --no-restore --filter "FullyQualifiedName~DocumentTotalsTests"` → no compila (`FreightAmounts` no existe).

- [ ] **Step 3: Implement** `FreightChargeMode`, `FreightAmounts` y la firma nueva de `DocumentTotals.Compute`. `Quotation.RecalculateTotals` y `Order.RecalculateTotals` pasan, por ahora, `FreightAmounts.Zero` (Task 3 los conecta) y copian `FreightSubtotal` / `FreightTaxAmount` a dos propiedades nuevas `public decimal FreightSubtotal { get; private set; }` y `public decimal FreightTaxAmount { get; private set; }` en cada agregado, más `public decimal ProductsTotal => Subtotal + TaxAmount;`.

- [ ] **Step 4: Run to verify GREEN** — mismo comando, más `QuotationTests` y `OrderTests` sin regresiones.

- [ ] **Step 5: Commit** — `feat(quotations): los totales suman el envío en campos propios`

---

### Task 2: Agregado `Carrier`

**Files:**
- Create: `Domain/Carrier.cs`, `Domain/CarrierId.cs`, `Domain/CarrierName.cs`, `Domain/CarrierNameEntry.cs`, `Domain/CarrierNameKind.cs`
- Test: `UnitTests/CarrierTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct CarrierId(Guid Value)` (mismo patrón que `OrderId`).
  - `public enum CarrierNameKind { Name, External }`
  - `public static class CarrierName { public const int MaxLength = 60; public const int ExternalMaxLength = 120; public const int MaxExternalNames = 20; public static string Normalize(string value); }`
  - `public sealed class CarrierNameEntry { Guid TenantId; CarrierNameKind Kind; string Value; string NormalizedName; }` (entidad hija, sin identidad propia fuera del agregado).
  - `Carrier`: `Id`, `TenantId`, `Name`, `IReadOnlyList<string> ExternalNames`, `bool SupportsCashOnDelivery`, `int SortOrder`, `bool IsActive`, `long Version`, `CreatedAt`, `UpdatedAt`, `IReadOnlyCollection<CarrierNameEntry> Names` (el nombre y los externos, normalizados).
  - `static Carrier Create(CarrierId id, Guid tenantId, string name, IReadOnlyCollection<string> externalNames, bool supportsCashOnDelivery, int sortOrder, DateTimeOffset occurredAt)`
  - `void Update(string name, IReadOnlyCollection<string> externalNames, bool supportsCashOnDelivery, DateTimeOffset occurredAt)`, `void Activate(DateTimeOffset)`, `void Deactivate(DateTimeOffset)`, `void MoveTo(int sortOrder, DateTimeOffset)`.
  - Códigos (`QuotationsDomainException`): `quotations.carrier.name_required`, `quotations.carrier.name_too_long`, `quotations.carrier.external_name_too_long`, `quotations.carrier.too_many_external_names`, `quotations.carrier.external_name_duplicated` (repetido dentro de la misma transportadora, o igual a su propio nombre).

`Normalize` (algoritmo fijado; el índice depende de él):

```csharp
var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
var decomposed = collapsed.ToLowerInvariant().Normalize(NormalizationForm.FormD);
var stripped = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
return stripped.Normalize(NormalizationForm.FormC);
```

- [ ] **Step 1: Write the failing tests** (`CarrierTests`):
  - `NormalizeIgnoresCaseAccentsAndSpaces`: `Normalize("  Inter   Rapidísimo ") == "inter rapidisimo"`.
  - `CreateTrimsTheNameAndKeepsItsNormalizedEntry`: `Names` tiene una entrada `Name` con `"coordinadora"`.
  - `RejectsAnEmptyName` / `RejectsANameLongerThan60` / `RejectsAnExternalNameLongerThan120` / `RejectsMoreThan20ExternalNames` (códigos de arriba).
  - `RejectsAnExternalNameRepeatedOrEqualToItsOwnName`: `["Envío", "envio"]` y `name: "Coordinadora", external: ["COORDINADORA"]` → `external_name_duplicated`.
  - `UpdateReplacesTheExternalNames`: después de `Update`, `Names` tiene exactamente el nombre y los externos nuevos.
  - `DeactivateAndActivateToggleIsActive` y suben `Version`.

- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~CarrierTests"` → no compila.
- [ ] **Step 3: Implement** el agregado según la interfaz. `Version++` en cada mutación, como `TaxRate`.
- [ ] **Step 4: GREEN** — mismo filtro.
- [ ] **Step 5: Commit** — `feat(quotations): transportadoras del tenant`

---

### Task 3: `ShippingDetails` en `Quotation` y `Order`

**Files:**
- Create: `Domain/ShippingDetails.cs`
- Modify: `Domain/Quotation.cs` (propiedades, `SetShipping`, `ApplyBillingAccount:570-600`, `RecalculateTotals`), `Domain/Order.cs` (propiedades, `SetShipping`, `FromQuotation:220-276`, `CreateManual:279`, `Preview:307`, `Build:362`, `RecalculateTotals`)
- Test: `UnitTests/QuotationShippingTests.cs`, `UnitTests/OrderShippingTests.cs`

**Interfaces:**
- Consumes: `FreightChargeMode`, `FreightAmounts` (Task 1).
- Produces:
  - `public sealed record ShippingDetails(Guid? CarrierId, string? CarrierName, FreightChargeMode? ChargeMode, decimal FreightAmount, int FreightTaxPercentage, bool CashOnDelivery)` con `static ShippingDetails None`.
  - En los dos agregados: propiedades `Guid? CarrierId`, `string? CarrierName`, `FreightChargeMode? FreightChargeMode`, `decimal FreightAmount`, `int FreightTaxPercentage`, `bool CashOnDelivery`, y `ShippingDetails Shipping` (calculada).
  - `Quotation.SetShipping(ShippingDetails shipping, MemberId updatedBy, DateTimeOffset occurredAt)` (con `EnsureEditable`).
  - `Order.SetShipping(ShippingDetails shipping, MemberId updatedBy, DateTimeOffset occurredAt)` (sólo `Pending`, mismo guard y misma forma que `SetIsRetail`).
  - `Order.CreateManual(…, ShippingDetails shipping, …)` y `Order.Preview(…, ShippingDetails shipping, …)`: parámetro nuevo después de `billingAccount`. `CreateExternal` pasa `ShippingDetails.None`.
  - `RecalculateTotals` usa `FreightAmounts.For(FreightChargeMode, FreightAmount, FreightTaxPercentage, Currency)`.

Reglas del dominio (`{p}` = `quotation.quotation` o `order.order`), spec §5.2:
- `IsStorePickup` y algo distinto de `ShippingDetails.None` → `{p}.shipping_not_allowed_on_pickup`.
- `FreightAmount < 0`, o con decimales en COP → `{p}.freight_amount_invalid`.
- `Charged` con 0 → `{p}.freight_amount_required`. `Free` con ≠ 0 → `{p}.freight_amount_not_allowed`. Modo `null` con monto ≠ 0 → `{p}.freight_amount_not_allowed`.
- Fuera de COP, `FreightTaxPercentage` se guarda en 0 sea cual sea el que llegue.
- Pedido: `CashOnDelivery` con `PaymentStatus != PaymentPending` → `order.order.cash_on_delivery_pending_only`, al crear (`Build`) y al prenderla en `SetShipping` si además hay comprobantes (D-F14).
- `Quotation.ApplyBillingAccount`: si la moneda cambia, `FreightChargeMode = null`, `FreightAmount = 0`, `FreightTaxPercentage = 0`; transportadora y contraentrega se conservan (D-F15).
- `Order.FromQuotation` copia los seis campos tal cual; el pedido nace con los mismos `Total` y `NetTotal`.

- [ ] **Step 1: Write the failing tests.**
  `QuotationShippingTests`:
  - `ChargedFreightEntersTheTotal`: cotización COP con una línea de total 119000 y `SetShipping(new(carrierId, "Coordinadora", Charged, 15000, 19, false))` → `Total == 134000m`, `FreightSubtotal == 12605.04m`.
  - `PickupRejectsAnyShipping` → `quotation.quotation.shipping_not_allowed_on_pickup`.
  - `ChargedWithZeroIsRejected`, `FreeWithAmountIsRejected`, `NegativeOrFractionalCopAmountIsRejected`.
  - `ShippingWithoutCarrierIsAccepted`: `new(null, null, Charged, 15000, 19, false)` pasa (decisión 11).
  - `CashOnDeliveryWithoutCarrierIsAccepted` (decisión 12).
  - `ChangingCurrencyClearsTheFreightAmountAndMode` (Review Focus 3): de COP con `Charged 15000` a una cuenta USD → `FreightChargeMode == null`, `FreightAmount == 0`, `FreightTaxAmount == 0`, `CarrierId` intacto.
  - `UsdChargedFreightHasNoTax`.
  `OrderShippingTests`:
  - `FromQuotationCopiesTheShippingAndTheTotals`.
  - `CreateManualWithCashOnDeliveryMustBePending` → `order.order.cash_on_delivery_pending_only` con `FullPaymentReceived`.
  - `CashOnDeliveryCannotBeTurnedOnWithProofs` (Review Focus 5).
  - `SetShippingOnlyWhilePending`: en `Approved` falla con el mismo código que hoy usan las ediciones de un pedido no pendiente.
  - `CreateExternalHasNoShipping`.

- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~ShippingTests"` → no compila.
- [ ] **Step 3: Implement** según la interfaz. Llamadas existentes a `CreateManual`/`Preview` pasan `ShippingDetails.None` hasta Task 7.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~ShippingTests|FullyQualifiedName~QuotationTests|FullyQualifiedName~OrderTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): entrega y envío en la cotización y el pedido`

---

### Task 4: Persistencia y migración `AddFreight`

**Files:**
- Modify: `Infrastructure/Persistence/QuotationsDbContext.cs`, `QuotationsUnitOfWork.cs`, `TenantQuotationSettingsRow.cs`
- Create: migración `AddFreight` (comando en Global Constraints)
- Test: `UnitTests/QuotationsDbContextMappingTests.cs`, `IntegrationTests/FreightMigrationTests.cs`

**Interfaces:**
- Produces (esquema `quotations`):
  - `carriers`: `id uuid pk`, `tenant_id`, `name varchar(60)`, `supports_cash_on_delivery bool`, `sort_order int`, `is_active bool`, `version bigint` (token de concurrencia), `created_at`, `updated_at`. Índice `IX_carriers_tenant_sort` (`tenant_id`, `sort_order`), no único.
  - `carrier_names`: `carrier_id` FK `Cascade`, `tenant_id`, `kind varchar(16)` con `CHECK` (`Name`, `External`), `value varchar(120)`, `normalized_name varchar(120)`. PK (`carrier_id`, `normalized_name`). **Único** `IX_carrier_names_tenant_name` (`tenant_id`, `normalized_name`).
  - `quotations` y `orders`: `carrier_id uuid null` (sin FK), `carrier_name varchar(60) null`, `freight_charge_mode varchar(16) null` con `CHECK` (`Charged`, `OnDelivery`, `Free`), `freight_amount numeric(14,2) not null default 0`, `freight_tax_percentage int not null default 0`, `freight_subtotal numeric(14,2) not null default 0`, `freight_tax_amount numeric(14,2) not null default 0`, `cash_on_delivery bool not null default false`.
  - `tenant_quotation_settings`: `freight_tax_rate_id uuid null`.
  - `QuotationsUnitOfWork`: `IX_carrier_names_tenant_name` → `QuotationsDomainException("quotations.carrier.name_taken", …)`.

- [ ] **Step 1: Write the failing tests.**
  - `QuotationsDbContextMappingTests`: casos nuevos para las tablas, columnas, `CHECK` y el índice único de arriba (mismo estilo que los casos existentes de `orders`).
  - `FreightMigrationTests.ExistingDocumentsKeepTheirTotals`: sembrar con la migración anterior una cotización y un pedido con totales conocidos, aplicar `AddFreight`, verificar `carrier_id IS NULL`, `freight_charge_mode IS NULL`, montos 0, `cash_on_delivery = false` y `total`, `net_total` iguales (patrón de `OrdersMigrationTests`).
- [ ] **Step 2: RED** — mapping y migración fallan (tablas inexistentes).
- [ ] **Step 3: Implement** el mapeo EF (`Carrier` con `OwnsMany`/colección de `CarrierNameEntry` sobre `carrier_names`; enums con `HasConversion<string>()`), la columna de ajustes, la traducción del índice y generar la migración. Revisar el SQL generado: sólo `ADD COLUMN … DEFAULT` y `CREATE TABLE`, ningún `UPDATE`.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~QuotationsDbContextMappingTests"` (unitarias) y `--filter "FullyQualifiedName~FreightMigrationTests"` (integración).
- [ ] **Step 5: Commit** — `feat(quotations): tablas de transportadoras y columnas del envío`

---

### Task 5: API de transportadoras e IVA del envío

**Files:**
- Create: Application `ICarrierRepository.cs`, `IQuotationTaxRateLookup.cs`, `CarriersDtos.cs`, `ListCarriers.cs`, `CreateCarrier.cs`, `UpdateCarrier.cs`, `SetCarrierActive.cs`, `ReorderCarriers.cs`, `SetFreightTaxRate.cs`, `GetShippingOptions.cs`, `CarrierNotFound.cs`; Infrastructure `Persistence/CarrierRepository.cs`; Api `CarrierEndpoints.cs`; Bootstrapper `QuotationTaxRateLookup.cs`
- Modify: `IQuotationSettingsStore.cs`, `Persistence/QuotationSettingsStore.cs`, `QuotationsInfrastructureExtensions.cs`, `src/Api/Program.cs:177-178`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:~809`
- Test: `IntegrationTests/CarrierApiTests.cs`

**Interfaces:**
- Consumes: `Carrier` (Task 2), tablas (Task 4).
- Produces:
  - `ICarrierRepository`: `Task<IReadOnlyList<Carrier>> ListAsync(Guid tenantId, CancellationToken)` (ordenadas por `SortOrder`), `Task<Carrier?> FindAsync(Guid tenantId, CarrierId id, CancellationToken)`, `void Add(Carrier carrier)`.
  - `IQuotationTaxRateLookup.FindAsync(Guid tenantId, Guid taxRateId, CancellationToken) → Task<QuotationTaxRateRef?>`, `record QuotationTaxRateRef(Guid Id, string Name, int Percentage, bool IsActive)`. Adaptador sobre `ITaxRateRepository.FindAsync` (patrón de `QuotationProductPricingLookup.cs:37-41`).
  - `IQuotationSettingsStore.GetFreightTaxRateIdAsync(Guid tenantId, CancellationToken) → Task<Guid?>` y `SetFreightTaxRateIdAsync(Guid tenantId, Guid? taxRateId, CancellationToken)` (P2).
  - DTOs: `CarrierDto(Guid Id, string Name, IReadOnlyList<string> ExternalNames, bool SupportsCashOnDelivery, int SortOrder, bool IsActive, long Version)`, `FreightTaxRateDto(Guid Id, string Name, int Percentage, bool IsActive)`, `CarrierSettingsDto(FreightTaxRateDto? FreightTaxRate, IReadOnlyList<CarrierDto> Carriers)`, `ShippingOptionsDto(IReadOnlyList<ShippingCarrierOptionDto> Carriers, FreightTaxRateDto? FreightTaxRate)`, `ShippingCarrierOptionDto(Guid Id, string Name, bool SupportsCashOnDelivery)`.
  - Rutas y permisos exactamente como spec §4.3 (`tenancy.settings.read` / `.update`; `GET /shipping-options` con `quotations.quotation.read`). `PUT /carriers/{id}` con `If-Match` (patrón de `OrdersExportLayoutEndpoints`).
  - Códigos: `quotations.carrier.not_found` (404 dentro del tenant, por `CarrierNotFound`), `quotations.carrier.order_mismatch`, `quotations.freight_tax_rate.not_found`, `quotations.freight_tax_rate.inactive`.
  - Validadores FluentValidation de crear y editar: `Name` no vacío ≤ 60; cada `ExternalNames[i]` no vacío ≤ 120; ≤ 20 elementos.

- [ ] **Step 1: Write the failing tests** (`CarrierApiTests`, harness `QuotationsApiHarness.RegisterTenantAsync`):
  - `CreatesListsAndOrdersCarriers`: crear tres → `GET /carriers` las trae en orden, `freightTaxRate` `null`.
  - `AnotherTenantGets403`: cliente del tenant A sobre la ruta del B → 403.
  - `ARepeatedNameIsTaken`: `"Coordinadora"` y `" coordinadora "` → 422 `quotations.carrier.name_taken`.
  - `AnExternalNameEqualToAnotherCarriersNameIsTaken` (Review Focus 4): externa `"Servientrega"` en Coordinadora con Servientrega existente → `name_taken`.
  - `AnInactiveCarrierStillHoldsItsName`.
  - `ReorderWithMissingOrExtraIdsIsRejected` → `quotations.carrier.order_mismatch`.
  - `SetsAndClearsTheFreightTaxRate`: con `QuotationsApiHarness.CreateTaxRateAsync` (19 %) → `freightTaxRate.percentage == 19`; con `null` → `null`; con una tasa inactiva → `quotations.freight_tax_rate.inactive`; con un id inexistente → `quotations.freight_tax_rate.not_found`.
  - `ShippingOptionsReturnOnlyActiveCarriersInOrder` (con permiso `quotations.quotation.read` por `X-Permissions` en modo stub, gotcha del repo).
  - `SavingMinimumPurchaseKeepsTheFreightTaxRate`: fijar la tasa, `PUT /quotations/settings` → la tasa sigue.
- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~CarrierApiTests"` → 404 en las rutas.
- [ ] **Step 3: Implement** handlers (cada uno revalida con `QuotationsAuthorization.EnsureAuthorized(executionContext, tenantId, permiso)` antes del repositorio), repositorio, store, adaptador, endpoints y su registro. Crear asigna `SortOrder = max + 1`. `ReorderCarriers` recibe todos los ids y asigna 0..n-1. Cada mutación devuelve lo que la pantalla repinta (spec §4.3).
- [ ] **Step 4: GREEN** — mismo filtro, más `--filter "FullyQualifiedName~QuotationSettingsApiTests"` y `ArchitectureTests` de Quotations.
- [ ] **Step 5: Commit** — `feat(quotations): API de transportadoras y del IVA del envío`

---

### Task 6: `ShippingResolver` y el envío en la cotización

**Files:**
- Create: `Application/ShippingResolver.cs`
- Modify: `QuotationsDtos.cs` (`ShippingRequest`, `ShippingDto`, totales), `CreateQuotation.cs`, `SaveQuotation.cs`, `PreviewQuotation.cs`, `QuotationChangeSummary.cs`, `QuotationMapping.cs`, `QuotationResponseComposer.cs`, `QuotationListing.cs`, `Api/QuotationEndpoints.cs`
- Test: `UnitTests/ShippingResolverTests.cs`, `UnitTests/QuotationChangeSummaryTests.cs`, `IntegrationTests/QuotationShippingApiTests.cs`

**Interfaces:**
- Consumes: `ICarrierRepository`, `IQuotationTaxRateLookup`, `IQuotationSettingsStore.GetFreightTaxRateIdAsync` (Task 5); `ShippingDetails` y `SetShipping` (Task 3).
- Produces:
  - `public sealed record ShippingRequest(Guid? CarrierId, string? FreightChargeMode, decimal FreightAmount, bool CashOnDelivery)` (el modo como nombre del enum; validador: `null` o uno de los tres nombres).
  - `ShippingResolver.ResolveAsync(Guid tenantId, ShippingRequest? request, ShippingDetails current, bool isStorePickup, string currency, string errorPrefix, CancellationToken) → Task<ShippingDetails?>` (`null` = no tocar). Reglas, en orden (spec §5.2 y P4):
    1. `isStorePickup` → `ShippingDetails.None` (si `current` ya es `None`, `null`).
    2. `request is null` → `null`.
    3. Transportadora: igual a `current.CarrierId` → conserva `current.CarrierName` sin leer el repositorio; distinta y no nula → `FindAsync`; inexistente o inactiva → `{errorPrefix}.carrier_not_available`.
    4. `CashOnDelivery` con transportadora cuyo `SupportsCashOnDelivery` es falso (sólo si la transportadora es nueva o la contraentrega se prende ahora) → `{errorPrefix}.cash_on_delivery_not_supported`.
    5. `Free` → monto 0. `Charged` en COP: si modo y monto son iguales a `current`, conserva `current.FreightTaxPercentage`; si no, lee la tasa del tenant; sin tasa, inactiva o borrada → `{errorPrefix}.freight_tax_rate_missing`.
  - `ShippingDto(bool IsStorePickup, ShippingCarrierDto? Carrier, string? ChargeMode, decimal FreightAmount, int FreightTaxPercentage, decimal FreightSubtotal, decimal FreightTaxAmount, bool CashOnDelivery)`, `ShippingCarrierDto(Guid Id, string Name)`. `QuotationDto` gana `Shipping`, `FreightSubtotal`, `FreightTaxAmount`; la fila de la lista, `Shipping`. El motivo BFF se escribe en el DTO (spec §7).
  - Cuerpos de crear, guardar y `preview` ganan `shipping: ShippingRequest?` junto a `parties`.

- [ ] **Step 1: Write the failing tests.**
  `ShippingResolverTests` (dobles en `QuotationsTestDoubles.cs`):
  - `PickupClearsEverything`, `AbsentRequestLeavesItAlone`.
  - `KeepsADeactivatedCarrierWhenItDidNotChange` (Review Focus 2) y `RejectsAnInactiveCarrierWhenChosen` → `quotation.quotation.carrier_not_available`.
  - `RejectsCashOnDeliveryWithACarrierThatDoesNotCollect`; `AcceptsCashOnDeliveryWithoutCarrier`.
  - `ChargedReadsTheTenantTaxRate` → `FreightTaxPercentage == 19`.
  - `SavingWithUnchangedFreightDoesNotRereadTheTaxRate` (Review Focus 1): el doble del lookup falla si se llama.
  - `ChargedWithoutTaxRateIsRejected` → `quotation.quotation.freight_tax_rate_missing`.
  - `ChargedInUsdNeedsNoTaxRate`.
  `QuotationChangeSummaryTests`: `ReportsCarrierModeAmountAndCashOnDeliveryChanges`.
  `QuotationShippingApiTests`:
  - `CreatesAQuotationWithChargedFreight`: con tasa 19 % y transportadora → `total` incluye 15000, `shipping.freightSubtotal == 12605.04`, `shipping.carrier.name == "Coordinadora"`.
  - `FreeAndOnDeliveryDoNotChangeTheTotal`.
  - `SwitchingToPickupClearsTheShipping`.
  - `SendsAndConvertsWithoutAnyShipping`: `CreateSentQuotationAsync` sin `shipping` → 200 (decisión 11).
  - `OldClientsWithoutShippingKeepIt`: guardar sin `shipping` no borra el envío guardado.
  - `PreviewComputesTheFreight`.
- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~ShippingResolverTests|FullyQualifiedName~QuotationShippingApiTests"`.
- [ ] **Step 3: Implement**: los handlers de crear/guardar/`preview` llaman a `ResolveAsync` **después** de aplicar partes y cuenta de cobro (la moneda y la recogida ya son las nuevas) y, si no es `null`, a `quotation.SetShipping`. Mapear y componer la respuesta.
- [ ] **Step 4: GREEN** — mismo filtro, más `QuotationApiTests`, `QuotationEditsApiTests` y `QuotationSendVoidApiTests` sin regresiones.
- [ ] **Step 5: Commit** — `feat(quotations): la cotización lleva transportadora, envío y contraentrega`

---

### Task 7: El envío en el pedido (manual, conversión y edición)

**Files:**
- Modify: `OrdersDtos.cs` (`CreateOrderRequest`, `SaveOrderEditsRequest`, `OrderDetailDto`, fila de lista), `CreateOrder.cs`, `PreviewOrder.cs`, `OrderDraftBuilder.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `ConvertQuotationToOrder.cs`, `OrderChangeSummary.cs`, `OrderMapping.cs`, `OrderResponseComposer.cs`, `OrderListing.cs`, `Api/OrderEndpoints.cs`
- Test: `IntegrationTests/OrderShippingApiTests.cs`

**Interfaces:**
- Consumes: `ShippingResolver.ResolveAsync` con `errorPrefix: "order.order"` (Task 6); `Order.SetShipping`, `CreateManual(…, ShippingDetails shipping, …)` (Task 3).
- Produces: `CreateOrderCommand`, `PreviewOrderQuery`, `SaveOrderEditsCommand` y `PreviewOrderEditsQuery` ganan `ShippingRequest? Shipping`. `OrderDetailDto` y la fila de la lista ganan `ShippingDto` y los totales del envío. Conversión: si la cotización tiene `CashOnDelivery` y el `paymentStatus` del cuerpo no es `PaymentPending` → `order.order.cash_on_delivery_pending_only` (lo lanza el dominio en `FromQuotation`).

- [ ] **Step 1: Write the failing tests** (`OrderShippingApiTests`):
  - `CreatesAManualOrderWithFreight` → `total` con el envío.
  - `ManualOrderWithCashOnDeliveryMustBePending` → 422 `order.order.cash_on_delivery_pending_only`.
  - `ConvertingCopiesTheShippingAndTheTotals`: cotización con envío cobrado → pedido con el mismo `total`, `netTotal` y `shipping`.
  - `ConvertingACashOnDeliveryQuotationAsPaidIsRejected`.
  - `EditsTheShippingOfAPendingOrderAndRecalculatesThePayment`: pedido con comprobante igual a `netTotal` (pagado), agregar envío cobrado → `paymentStatus == "PartialPaymentReceived"`.
  - `EditingTheShippingWritesOrderHistory`: `GET` del historial del pedido trae la entrada del cambio de envío (`OrderChangeSummary`).
  - `ManualOrderWithoutShippingStillWorks` (barrido: `ManualOrderApiTests` existentes sin cambios).
- [ ] **Step 2: RED** — `--filter "FullyQualifiedName~OrderShippingApiTests"`.
- [ ] **Step 3: Implement** igual que en la cotización: resolver con la moneda del pedido y su `IsStorePickup`; en edición, `SetShipping` antes de `RecalculatePaymentStatus` (que ya corre en `SaveOrderEdits.cs:247`).
- [ ] **Step 4: GREEN** — mismo filtro, más `ManualOrderApiTests`, `OrderEditsApiTests`, `OrderApiTests`, `ExternalOrderServiceTests`.
- [ ] **Step 5: Commit** — `feat(quotations): el pedido lleva el envío y la contraentrega`

---

### Task 8: El mínimo de compra no cuenta el envío

**Files:**
- Modify: `QuotationMinimumPurchase.cs:37`, `QuotationResponseComposer.cs:96`, `OrderPricingRecalculation.cs:51,59`
- Test: `UnitTests/QuotationMinimumPurchaseTests.cs`, `UnitTests/OrderPricingRecalculationTests.cs`

**Interfaces:**
- Consumes: `ProductsTotal` de los dos agregados (Task 1).

- [ ] **Step 1: Write the failing tests**:
  - `QuotationMinimumPurchaseTests.FreightDoesNotHelpReachTheMinimum`: mínimo COP 200000, productos 150000 con IVA, envío cobrado 60000 → `IsSatisfiedBy == false`.
  - `OrderPricingRecalculationTests.FreightDoesNotEnableTheGlobalFloor`: mismo caso sobre el pedido.
- [ ] **Step 2: RED** — los dos devuelven `true` (miden `Total`).
- [ ] **Step 3: Implement** — reemplazar `Total` por `ProductsTotal` en los cuatro lugares.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~MinimumPurchase|FullyQualifiedName~PricingRecalculation"`.
- [ ] **Step 5: Commit** — `fix(quotations): el envío no cuenta para el mínimo de compra`

---

### Task 9: PDF de la cotización

**Files:**
- Modify: `QuotationPdfDocument.cs`, `QuotationPdfDocumentMapper.cs`, `Infrastructure/Pdf/quotation.typ` (entrega `:272-291`, totales `:376-399`, forma de pago `:221-229`)
- Test: `UnitTests/QuotationPdfDocumentMapperTests.cs`, `UnitTests/QuotationTemplateTests.cs` (si verifica claves de la plantilla)

**Interfaces:**
- Produces: `QuotationPdfDocument` gana `string? CarrierName`, `string? FreightChargeMode`, `decimal FreightAmount`, `int FreightTaxPercentage`, `decimal FreightSubtotal`, `decimal FreightTaxAmount`, `bool CashOnDelivery`.

Textos fijados por el spec §7: «Transportadora: {nombre}»; «Envío ({nombre})» (o «Envío» sin transportadora) con `FreightSubtotal`; «IVA del envío ({tasa} %)» con `FreightTaxAmount`, en una fila propia debajo del IVA de productos; `Free` → «Envío ({nombre}): Gratis»; `OnDelivery` → «Envío ({nombre}): al cobro, lo paga el cliente a la transportadora» (más el valor informativo si es > 0); `CashOnDelivery` → «Forma de pago: Contraentrega».

- [ ] **Step 1: Write the failing tests**: `MapsTheShippingFields` (los siete campos desde un `QuotationResponse` con envío cobrado) y `MapsNoShippingAsNullsAndZeros`. Si `QuotationTemplateTests` compara las claves que la plantilla lee contra el JSON, agregar las nuevas.
- [ ] **Step 2: RED**. **Step 3: Implement** mapper y plantilla. **Step 4: GREEN** — `--filter "FullyQualifiedName~QuotationPdf|FullyQualifiedName~QuotationTemplate"`. Render real: lo verifica el developer en local con qcode-pdf (no hay prueba de render).
- [ ] **Step 5: Commit** — `feat(quotations): el PDF muestra la transportadora, el envío y la contraentrega`

---

### Task 10: Excel de pedidos

**Files:**
- Modify: `Domain/OrdersExportColumnCatalog.cs` (al final, después de `retention_amount`), `Application/OrdersExportProcessor.cs` (`:234` y el final del arreglo de `RowsFor`)
- Test: `UnitTests/OrdersExportProcessorTests.cs`, `UnitTests/OrdersExportColumnCatalogTests.cs`, `IntegrationTests/OrderExportApiTests.cs`

**Interfaces:**
- Produces: columnas `freight_amount` («Valor Flete», visible), `freight_without_tax` («Flete sin IVA»), `freight_tax` («IVA Flete»), `freight_charge_mode` («Pago Flete»), `cash_on_delivery` («Contraentrega»), las cuatro últimas `DefaultVisible: false` (spec §7, D-F10). Anchos: 16 como `retention_amount`.

- [ ] **Step 1: Write the failing tests**:
  - `CarrierColumnUsesTheOrdersCarrier` («Servientrega»), `CarrierColumnFallsBackToCoordinadoraWithoutCarrier` (D-F8), pickup sigue «Recoger en tienda».
  - `FreightColumns`: `Charged 15000` → `15000`, `12605.04`, `2394.96`, «Cobrado», «No»; `Free` → `0`, `0`, `0`, «Gratis»; `OnDelivery` → vacío, `0`, `0`, «Al cobro»; recogida → vacío y modo vacío; contraentrega → «Sí».
  - `OrdersExportColumnCatalogTests`: las cinco claves al final y con su `DefaultVisible`.
- [ ] **Step 2: RED**. **Step 3: Implement** (catálogo y arreglo en la misma posición). **Step 4: GREEN** — `--filter "FullyQualifiedName~OrdersExport"` y `OrderExportApiTests`.
- [ ] **Step 5: Commit** — `feat(quotations): el Excel de pedidos lleva la transportadora real y el flete`

---

### Task 11: Reportes y eventos

**Files:**
- Modify: `src/Bootstrapper/OrdersReportSource.cs:80-82,318-371`, `src/Bootstrapper/QuotationsReportSource.cs:63-65`, `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs`
- Test: las pruebas de integración de Reporting del resumen de pedidos y de cotizaciones (por clase existente); `tests/Modules/Pipeline/…/QuotationEventPayloadsTests.cs`

**Interfaces:**
- Produces: el resumen por moneda y la fila del listado de pedidos y de cotizaciones ganan `FreightSubtotal` y `FreightTaxAmount` (sumas), de modo que `subtotal + taxAmount + freightSubtotal + freightTaxAmount == total`.

- [ ] **Step 1: Write the failing tests**: `SummaryClosesWithFreight` en pedidos y en cotizaciones (un documento con envío cobrado); `QuotationEventPayloadsTests.TotalIncludesTheChargedFreight` (el `total` del payload de `order.created.v1` es el `Total` con envío; contrato sin campos nuevos, spec §8).
- [ ] **Step 2: RED**. **Step 3: Implement** las sumas y los DTO (el motivo BFF en el DTO: «un total que no cierra se explica con un campo»). **Step 4: GREEN** — filtros de las clases tocadas.
- [ ] **Step 5: Commit** — `feat(reporting): los resúmenes explican el envío dentro del total`

---

### Task 12: Documentación y cierre

**Files:**
- Modify: `README.md` (sección de Quotations: transportadoras, IVA del envío, contraentrega; dónde está la pantalla de tasas de impuesto), la guía de errores que ya lista los del pedido (`docs/integracion-cotizaciones-y-pedidos.md` o la que el README enlace) con todos los códigos nuevos de Tasks 2-7, `CLAUDE.md` sólo si aparece un gotcha verificado durante la implementación.
- Spec: historial de revisión con lo que el plan decidió (P1-P5).

- [ ] **Step 1:** `dotnet build` sin warnings y las pruebas por módulo tocado: Quotations (unitarias e integración, por las clases de este plan y las de regresión nombradas), Reporting, Pipeline (`QuotationEventPayloadsTests`), `ArchitectureTests` de Quotations. Evidencia literal de cada corrida.
- [ ] **Step 2:** Docs y spec.
- [ ] **Step 3: Commit** — `docs(quotations): transportadoras, envío y contraentrega`

El frontend (Configuración › Transportadoras, tasas de impuesto movidas a Configuración, cotizador, pedido, totales, reportes) va en su propio plan en `qep-frontend`, después de este. El delta al spec de Shopify (§10 del spec) se aplica cuando ese spec se apruebe.
