# Servicios en cotizaciones y pedidos — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un producto del catálogo puede ser servicio, con unidad de venta; cotizaciones y pedidos mezclan productos y servicios, saben si requieren envío y calculan cada retención sobre la base de su alcance.

**Architecture:** Los enums compartidos viven en `BuildingBlocks.Domain` (Catalog y Quotations no se ven). Catalog guarda `Kind`/`SalesUnit` en el producto y `AppliesTo` en la tasa; Quotations los congela en la línea al crearla (por los puertos de lookup, adaptadores en Bootstrapper) y en la foto de la retención. Companies y Customers leen el alcance por su puerto de tasas.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers, Typst (`quotation.typ`).

**Spec:** `docs/superpowers/specs/2026-11-18-servicios-en-cotizaciones-design.md` (léelo; §3.4 y §5 tienen las correcciones del 2026-11-18).

## Global Constraints

- Todo lo nuevo nace con el valor de hoy: `Physical`, `Unit`, `All`. Ningún documento existente cambia de total, retención ni PDF.
- Enums viajan por nombre (`ProductKind`, `SalesUnit`, `WithholdingScope`); columnas `text not null` con default en la base.
- Migraciones aditivas (expand/contract), una por módulo y tarea, con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- Campos nuevos del JSON de entrada opcionales (ausente = default): no hay barrido de cuerpos crudos.
- Códigos de error nuevos, exactos: `catalog.product.packaging_not_allowed_for_service`, `catalog.tax_rate.applies_to_invalid`, `catalog.tax_rate.applies_to_immutable`. Los de repetición no cambian (`{prefijo}.withholding_type_repeated` y equivalentes).
- `WithholdingReason.NoLinesOfScope` se agrega **al final** del enum (se serializa por nombre, pero el orden de los existentes no se toca).
- Prueba de integración en esta máquina: `$env:Seed__Enabled = "false"` antes de `dotnet test`. Nunca dos corridas de integración a la vez (`Get-Process testhost, dotnet -ErrorAction SilentlyContinue`).
- Comentarios y documentación en español tuteando; commits conventional, sin atribución de IA.

## Review Focus

1. Documento mixto con ReteFuente `Goods` y `Services`: cada una retiene sobre su propia base y su propio umbral (Task 8, `MixedDocumentWithholdsEachRateOnItsScopeBase`).
2. Documento con sólo tasas `All` y líneas mixtas: total y retención idénticos a hoy (Task 8, `AllScopeRatesReproduceTodaysTotals`).
3. Línea agregada a un borrador de cotización: el tipo existe antes de enviar (Task 4, `ADraftLineCarriesTheServiceKindBeforeSending`).
4. Convertir una cotización mixta: el pedido conserva el tipo de cada línea aunque el producto haya cambiado de tipo después (Task 4, `ConversionCopiesTheLineKindNotTheCurrentProductKind`).
5. Selección guardada en un cliente con `Goods`+`Services` del mismo tipo sigue válida; con `All`+`Services` se rechaza (Task 9, `CustomerAcceptsGoodsAndServicesOfTheSameType` / `…RejectsAllWithAnotherScope`).

---

## Bloque 1 — Catalog

### Task 1: Enums compartidos

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Catalog/ProductKind.cs`, `.../Catalog/SalesUnit.cs` (enum + `static class SalesUnits`), `src/BuildingBlocks/BuildingBlocks.Domain/Taxes/WithholdingScope.cs` (enum + `static class WithholdingScopes`)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/SalesUnitsTests.cs`, `.../WithholdingScopesTests.cs`

**Interfaces — Produces:**
- `enum ProductKind { Physical, Service }`
- `enum SalesUnit { Unit, Service, Hour, Day, Week, Month, Year, Kilogram, Meter, SquareMeter, Liter }`
- `SalesUnits.CodeOf(SalesUnit) : string` — códigos de la tabla del spec §3.2 (`94`, `E48`, `HUR`, `DAY`, `WEE`, `MON`, `ANN`, `KGM`, `MTR`, `MTK`, `LTR`).
- `enum WithholdingScope { All, Goods, Services }`
- `WithholdingScopes.Covers(WithholdingScope scope, ProductKind kind) : bool` — `All` todo; `Goods` ↔ `Physical`; `Services` ↔ `Service`.
- `WithholdingScopes.Overlap(WithholdingScope a, WithholdingScope b) : bool` — `a == All || b == All || a == b`.

- [ ] **Step 1: Tests** — `EveryUnitHasItsRec20Code` (Theory sobre la tabla completa), `EverySalesUnitHasACode` (`Enum.GetValues` sin excepción), `CoversMatchesKindToScope` (Theory 6 casos), `GoodsAndServicesDoNotOverlap`, `AllOverlapsEverything`, `SameScopeOverlaps`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Catalog/Modules.Catalog.UnitTests` falla por tipos inexistentes.
- [ ] **Step 3: Implementar** los tres archivos.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~SalesUnitsTests|FullyQualifiedName~WithholdingScopesTests"`.
- [ ] **Step 5: Commit** `feat(catalog): add shared product kind, sales unit and withholding scope`.

### Task 2: Tipo y unidad del producto

**Files:**
- Modify: `Modules.Catalog.Domain/Product.cs` (propiedades, `Create` ×2 en :151/:180, `Update` :231, chequeo de empaques tras `ReplaceVariants`), `ProductDetails.cs` (agregar `ProductKind Kind`, `SalesUnit SalesUnit` como `init` con default `Physical`/`Unit`) — vía `Apply(ProductDetails)` :274.
- Modify: `Modules.Catalog.Application/CreateProduct.cs`, `UpdateProduct.cs` (comandos: `ProductKind? Kind = null, SalesUnit? SalesUnit = null`), `ExternallyManagedProducts.cs:24` (rechaza `Kind` distinto), `CatalogDtos.cs` (`ProductDto`, `ProductResponse`, `ProductListItemDto`, `CreateProductRequest`, `UpdateProductRequest`), `ProductMapping.cs`, `ListProducts.cs` (`ProductKind? Kind = null`), `IProductRepository.cs` (`SearchSummariesAsync` + `ProductKind? kind`; `ProductSummary.Kind`).
- Modify: `Modules.Catalog.Api/ProductEndpoints.cs` (`?kind=`, mapeos :187/:212/:311).
- Modify: `Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` (`ConfigureProduct` :52: `kind`, `sales_unit`, `HasConversion<string>()`, `HasDefaultValue`, `HasSentinel` no aplica: default CLR = valor de default), `ProductRepository.cs` (filtro como `visibleInStore` :124, select :135, :207).
- Migration: `AddProductKindAndSalesUnit` (Catalog).
- Test: `ProductTests.cs`, `ExternallyManagedProductsTests.cs` (unit); `ProductKindApiTests.cs` (integración, nuevo, con `SharedCatalogHost` y `ProductBodies`).

**Interfaces — Produces:** `Product.Kind : ProductKind`, `Product.SalesUnit : SalesUnit`; JSON `kind`, `salesUnit` (nombres de enum) en detalle y listado; `GET …/catalog/products?kind=Service`.

- [ ] **Step 1: Unit tests** — `ANewProductIsPhysicalByUnit`; `AServiceWithPackagingUnitsIsRejected` (variante con `PackagingUnits` y `Kind = Service` → `DomainException` código `catalog.product.packaging_not_allowed_for_service`); `TurningAProductIntoAServiceRequiresEmptyPackaging` (Update); `AShopifyProductCannotChangeKind` en `ExternallyManagedProductsTests` (código `catalog.product.managed_externally`, clave `Kind`).
- [ ] **Step 2: RED** — build de `Modules.Catalog.UnitTests` falla.
- [ ] **Step 3: Implementar dominio y aplicación.** El 422 de empaques lleva `errors` con clave `PackagingUnits` (mismo patrón `IHasFieldErrors` que los demás de `ProductVariant`).
- [ ] **Step 4: GREEN unit** — `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests`.
- [ ] **Step 5: Migración** — comando de Global Constraints; revisar que sólo agrega dos columnas con default.
- [ ] **Step 6: Integration tests** en `ProductKindApiTests`: `CreateWithoutKindIsPhysicalByUnit`, `CreateAServiceByHourRoundTrips` (detalle devuelve `"kind":"Service","salesUnit":"Hour"`), `UpdateChangesKindAndUnit`, `ListFiltersByKind` (dos productos, `?kind=Service` devuelve uno), `ServiceWithPackagingIs422`.
- [ ] **Step 7: GREEN** — `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductKindApiTests"`.
- [ ] **Step 8: Commit** `feat(catalog): let a product be a service with a sales unit`.

### Task 3: Alcance de la tasa de retención

**Files:**
- Modify: `Modules.Catalog.Domain/TaxRate.cs` (`AppliesTo`; `Create` :136 + `WithholdingScope appliesTo = WithholdingScope.All`; `Update` sin cambios), `CreateTaxRate.cs` (`WithholdingScope? AppliesTo = null`), `UpdateTaxRate.cs` (`WithholdingScope? AppliesTo = null` en comando y en `UpdateTaxRateRequestMapping`; si viene y difiere → 422 `catalog.tax_rate.applies_to_immutable`), `CatalogDtos.cs` (`CreateTaxRateRequest`, `UpdateTaxRateRequest`, `TaxRateDto`, `TaxRateResponse` + `appliesTo`), `TaxRateMapping.cs`, `TaxRateEndpoints.cs`, `CatalogDbContext.cs` (`ConfigureTaxRate` :383: `applies_to`).
- Migration: `AddTaxRateAppliesTo` (Catalog).
- Test: `TaxRateTests.cs` (unit), `TaxRateApiTests.cs` (integración).

**Interfaces — Produces:** `TaxRate.AppliesTo : WithholdingScope`; JSON `appliesTo`.

- [ ] **Step 1: Unit tests** — `AWithholdingRateKeepsItsScope` (Retefuente + `Services`); `AVatRateWithAScopeIsRejected` (IVA + `Goods` → `catalog.tax_rate.applies_to_invalid`); `ARateWithoutScopeAppliesToAll`.
- [ ] **Step 2: RED** (build) → **Step 3: implementar** → **Step 4: GREEN unit**.
- [ ] **Step 5: Migración.**
- [ ] **Step 6: Integration** en `TaxRateApiTests`: `CreateRetefuenteForServicesRoundTrips`, `UpdateWithAnotherScopeIs422Immutable`, `UpdateWithTheSameScopeOrWithoutItSucceeds`.
- [ ] **Step 7: GREEN** filtrado → luego clase completa del bloque: `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests -- xUnit.MaxParallelThreads=4` (fin de bloque 1).
- [ ] **Step 8: Commit** `feat(catalog): scope a withholding rate to goods, services or all`.

---

## Bloque 2 — Líneas, envío y PDF

### Task 4: Tipo y unidad en la línea

**Files:**
- Modify (Application): `IQuotationProductPricingLookup.cs` (`QuotationVariantPricingRef` + `ProductKind Kind, SalesUnit SalesUnit`), `IQuotationProductLookup.cs` (`QuotationVariantRef` + lo mismo), `QuotationItemEdits.cs`, `AddQuotationItem.cs`, `BatchUpdateQuotationItems.cs`, `OrderItemEdits.cs`, `AddOrderItems.cs`, `OrderDraftBuilder.cs` (:189 `OrderLineInput`), `QuotationsDtos.cs` (`QuotationItemDto`, `QuotationItemResponse` + `kind`, `salesUnit`), `QuotationMapping.cs`/`OrderMapping.cs:139`/`QuotationResponseComposer.cs:302`.
- Modify (Domain): `QuotationItem.cs` (`Kind`, `SalesUnit`; `Create` :176 + `ProductKind kind, SalesUnit salesUnit`), `OrderItem.cs` (:125 igual), `Quotation.cs` (`AddItem` :500 → `AddItemCore` :618), `Order.cs` (`AddItem` :1039, `AddItemCore` :1083, `FromQuotation` copia :497-504, `CandidateLines` :2055/`ReplaceExternalLines` :2078 pasan `Physical`/`Unit`), `OrderInputs.cs` (`OrderLineInput` + `ProductKind Kind = ProductKind.Physical, SalesUnit SalesUnit = SalesUnit.Unit` al final).
- Modify (Bootstrapper): `QuotationProductPricingLookup.cs`, `QuotationProductLookup.cs` (leer `products.kind`/`sales_unit`).
- Modify (Infrastructure): `QuotationsDbContext.cs` (`ConfigureQuotationItem` :347, `ConfigureOrderItem` :848).
- Migration: `AddLineKindAndSalesUnit` (Quotations).
- Test: `QuotationItemEditsTests.cs`, `OrderItemTests.cs` (unit); `Bootstrapper.UnitTests/QuotationProductLookupTests.cs`; integración nueva `LineKindApiTests.cs` (Quotations).

**Interfaces:**
- Consumes: Task 1 enums; Task 2 columnas `catalog.products.kind/sales_unit`.
- Produces: `QuotationItem.Kind/SalesUnit`, `OrderItem.Kind/SalesUnit`, `QuotationItemResponse.Kind/SalesUnit` (JSON `kind`, `salesUnit`).

- [ ] **Step 1: Unit tests** — `AnAddedLineTakesKindAndUnitFromTheVariant` (QuotationItemEdits con doble del lookup que devuelve `Service`/`Hour`); `AnUnchangedLineKeepsItsKindOnFullSave`; `FromQuotationCopiesKindAndUnit` (OrderItemTests o Order); `AnExternalLineIsPhysicalByUnit`.
- [ ] **Step 2: RED** (build de `Modules.Quotations.UnitTests` y `Bootstrapper.UnitTests`).
- [ ] **Step 3: Implementar.** Al cambiar la firma de `Create`/`AddItem`, el compilador enumera los llamadores: todos pasan los valores del ref; los de Shopify pasan `Physical`/`Unit`.
- [ ] **Step 4: GREEN unit** de Quotations y Bootstrapper (`--filter "FullyQualifiedName~QuotationProductLookupTests"`).
- [ ] **Step 5: Migración.**
- [ ] **Step 6: Integration** `LineKindApiTests` (producto servicio creado por la API de Catalog con `kind: "Service", salesUnit: "Hour"`): `ADraftLineCarriesTheServiceKindBeforeSending`, `ConversionCopiesTheLineKindNotTheCurrentProductKind` (cambiar el producto a `Physical` antes de convertir), `AManualOrderLineTakesTheCatalogKind`.
- [ ] **Step 7: GREEN** filtrado por `LineKindApiTests`.
- [ ] **Step 8: Commit** `feat(quotations): freeze kind and sales unit on each line`.

### Task 5: `requiresShipping`

**Files:**
- Modify: `Quotation.cs`, `Order.cs` (`public bool RequiresShipping => Items.Count == 0 || Items.Any(i => i.Kind == ProductKind.Physical)`), `QuotationsDtos.cs` (`QuotationResponse`, `QuotationListItemResponse`), `OrdersDtos.cs` (`OrderDetailResponse`, `OrderResponse`, `OrderListItemResponse`), composers, `ListQuotations.cs`, `ListOrders.cs`/`OrderListing.cs:133`, `QuotationRepository.cs` y `OrderRepository.cs` (`Task<IReadOnlySet<Guid>> FindIdsRequiringShippingAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)`, copia de `FindIdsWithItemsAsync` :208 con `!Items.Any() || Items.Any(Physical)`), y sus puertos.
- Test: `QuotationShippingTests.cs`/`OrderShippingTests.cs` (unit), `LineKindApiTests.cs` (integración).

**Interfaces — Produces:** JSON `requiresShipping` en detalle y listado de cotizaciones y pedidos. Comentario en cada DTO con el motivo BFF (spec §5).

- [ ] **Step 1: Unit** — `AnEmptyDocumentRequiresShipping`, `OnlyServicesDoesNotRequireShipping`, `MixedRequiresShipping` (cotización y pedido).
- [ ] **Step 2: RED** → **Step 3: implementar** → **Step 4: GREEN unit**.
- [ ] **Step 5: Integration** — `ServicesOnlyOrderDoesNotRequireShippingInDetailAndList`, `MixedQuotationRequiresShippingInList`, `ServicesOnlyOrderMarksPaidWithoutShippingCity` (tenant con política de ciudad al facturar; prepara y marca pagado sin 422).
- [ ] **Step 6: GREEN** filtrado → **Step 7: Commit** `feat(quotations): tell the screen whether a document requires shipping`.

### Task 6: Unidad en el PDF

**Files:**
- Create: `Modules.Quotations.Infrastructure/Pdf/SalesUnitLabels.cs` — `internal static string Spanish(SalesUnit unit, decimal quantity)` (singular si `quantity == 1`: unidad/unidades, servicio/servicios, hora/horas, día/días, semana/semanas, mes/meses, año/años, kg, m, m², l).
- Modify: `QuotationPdfDocument.cs:169` (`QuotationPdfLine` + `string? Unit`), `QuotationPdfDocumentMapper.cs` (`LineFor` :160; `Unit = null` en todas las líneas si todas son `Unit`), `Pdf/quotation.typ` (:407: `#cantidad(item.quantity)` seguido de ` #item.unit` cuando no es `none`; texto plano, nunca `eval`).
- Test: `QuotationPdfDocumentMapperTests.cs`, `OrderPdfDocumentMapperTests.cs`, `QCodePdfRendererTests.cs` (la plantilla compila con unidad).

- [ ] **Step 1: Unit** — `AllUnitLinesOmitTheUnit`, `AMixedDocumentLabelsEveryLine` (`2 horas`, `1 unidad`), `PluralFollowsQuantity` (Theory), `TheTemplateCompilesWithUnits`.
- [ ] **Step 2: RED** → **Step 3: implementar** → **Step 4: GREEN** (`--filter "FullyQualifiedName~PdfDocumentMapperTests|FullyQualifiedName~QCodePdfRendererTests"`).
- [ ] **Step 5:** clase completa de integración del bloque: `QuotationPdfExportApiTests`, `OrderPdfExportApiTests`, `LineKindApiTests`, `QuotationItemApiTests`, `ManualOrderApiTests`, `OrderConvertApiTests` (fin de bloque 2).
- [ ] **Step 6: Commit** `feat(quotations): show the sales unit on the quotation pdf`.

---

## Bloque 3 — Retenciones por alcance

### Task 7: Alcance en la foto de la retención

**Files:**
- Modify: `Modules.Quotations.Domain/WithholdingRate.cs` (`public WithholdingScope AppliesTo { get; init; } = WithholdingScope.All;`), `DocumentWithholding.cs` (columna `AppliesTo`; `Rate` :74 y `Replace` :91 la copian), `Bootstrapper/QuotationWithholdingLookup.cs` (`Photo` :173 la toma de la tasa), `QuotationsDbContext.cs` (`ConfigureWithholding` :268: `applies_to`), DTO de la tasa en el desglose (`WithholdingBreakdownResponse`, QuotationsDtos.cs:214, + `appliesTo`).
- Migration: `AddWithholdingAppliesTo` (Quotations; tablas `quotation_withholdings`, `order_withholdings`).
- Test: `QuotationWithholdingLookupTests.cs` (Bootstrapper), `QuotationWithholdingsPersistenceTests.cs` (integración).

- [ ] **Step 1:** `ThePhotoCarriesTheRateScope` (Bootstrapper unit); `AScopedRateSurvivesARoundTrip` (persistencia).
- [ ] **Step 2: RED** → **Step 3: implementar + migración** → **Step 4: GREEN** filtrado.
- [ ] **Step 5: Commit** `feat(quotations): carry the withholding scope in the document photo`.

### Task 8: Base por alcance

**Files:**
- Create: `Modules.Quotations.Domain/WithholdingBases.cs` — `public readonly record struct WithholdingBases(decimal All, decimal Goods, decimal Services) { public static WithholdingBases From(IEnumerable<LineAmountsView> lines); public decimal For(WithholdingScope scope); }` (cada base redondeada igual que `subtotal` en `Compute`).
- Modify: `DocumentTotals.cs` (`LineAmountsView` + `ProductKind Kind = ProductKind.Physical`; `Compute` usa `WithholdingBases`; `Withhold(WithholdingBases, rates)`), `WithholdingVerdict.cs` (`NoLinesOfScope` al final), `WithholdingCandidates.cs` (`Explain(..., WithholdingBases bases)`: tras `Gate.Blocked`, si `bases.For(rate.AppliesTo) == 0` → `NoLinesOfScope`; si no, `ThresholdReason(rate, base)`; `Base` del veredicto = base propia), `DocumentWithholding.cs:196` (`Explain` recibe `WithholdingBases`), `Quotation.cs` (:1471 pasa `Kind`; `WithholdingVerdicts` :405 pasa `WithholdingBases.From(...)`), `OrderItem.cs:123` (`Amounts` con `Kind`), `Order.cs` (:288, :2225).
- Test: `DocumentTotalsTests.cs`, `WithholdingVerdictTests.cs` (unit); `OrderWithholdingsApiTests.cs` (integración).

**Interfaces — Produces:** `WithholdingBases`, `WithholdingReason.NoLinesOfScope`; `AppliedWithholding.Base` = base propia.

- [ ] **Step 1: Unit** con valores exactos — líneas: bien 1.000.000 + servicio 500.000; ReteFuente `Goods` 2,5 % y `Services` 4 % sin base mínima:
  - `MixedDocumentWithholdsEachRateOnItsScopeBase` → 25.000 (base 1.000.000) y 20.000 (base 500.000), retención 45.000.
  - `AllScopeRatesReproduceTodaysTotals` → una `All` 2,5 % sobre 1.500.000 = 37.500.
  - `AGoodsRateOnAServicesOnlyDocumentIsNoLinesOfScope` (veredicto `NoLinesOfScope`, monto 0).
  - `TheThresholdIsComparedWithTheScopeBase` → `Services` con umbral 600.000: no aplica (`BelowMinimumBase`) aunque el subtotal total sea 1.500.000.
  - `FinalConsumerStillWinsOverNoLinesOfScope` (orden de motivos).
- [ ] **Step 2: RED** → **Step 3: implementar** → **Step 4: GREEN** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`.
- [ ] **Step 5: Integration** — `AMixedOrderWithholdsByScope` (tasas `Goods`/`Services` insertadas con `InsertWithholdingRateAsync` + `UPDATE catalog.tax_rates SET applies_to`; o creadas por la API de Task 3) y verifica montos del detalle y del desglose (`base` de cada fila).
- [ ] **Step 6: GREEN** filtrado → **Step 7: Commit** `feat(quotations): withhold each rate on the base of its scope`.

### Task 9: «Una por tipo y alcance»

**Files:**
- Modify: `Modules.Quotations.Domain/WithholdingSelectorRules.cs:37`, `Modules.Quotations.Application/WithholdingSelection.cs:138`, `Modules.Companies.Application/CompanyWithholdingRules.cs:44` + `ICompanyTaxRateLookup.cs:8` (`CompanyTaxRateRef` + `WithholdingScope AppliesTo`), `Modules.Customers.Application/CustomerWriteRules.cs:233` + `ICustomerTaxRateLookup.cs:8` (igual), `Bootstrapper/CompanyTaxRateLookup.cs:30`, `Bootstrapper/CustomerTaxRateLookup.cs:37`.
- Regla común: dos tasas del mismo grupo (el agrupador actual de cada sitio) son repetidas si `WithholdingScopes.Overlap(a.AppliesTo, b.AppliesTo)`.
- Test: `WithholdingSelectionTests.cs`, `CompanyWithholdingRulesTests.cs`, `CustomerWithholdingRulesTests.cs` (unit); `CustomerWithholdingTaxRatesApiTests.cs` (integración).

- [ ] **Step 1: Unit** en los cuatro: `GoodsAndServicesOfTheSameTypeAreAccepted`, `AllWithAnotherScopeIsRepeated`, `TwoOfTheSameScopeAreRepeated` (códigos de error de hoy).
- [ ] **Step 2: RED** → **Step 3: implementar** → **Step 4: GREEN** unit de Quotations, Companies y Customers.
- [ ] **Step 5: Integration** — `CustomerAcceptsGoodsAndServicesOfTheSameType`, `CustomerRejectsAllWithAnotherScope` en `CustomerWithholdingTaxRatesApiTests`; idem empresa en `CompanyWithholdingTaxRatesApiTests`.
- [ ] **Step 6:** fin de bloque 3: clases `QuotationWithholdings*`, `OrderWithholdings*`, `CustomerWithholdingTaxRatesApiTests`, `CompanyWithholdingTaxRatesApiTests`.
- [ ] **Step 7: Commit** `feat(quotations): allow one withholding per type and scope`.

---

## Cierre

- [ ] Suite de los módulos tocados, en fila: Catalog, Quotations, Companies, Customers (unit + integración) y `Bootstrapper.UnitTests`, `ArchitectureTests`.
- [ ] Revisión fuerte de toda la rama.
- [ ] Merge a `develop` y fila en `docs/estado.md` en `develop` (nunca en la rama).
