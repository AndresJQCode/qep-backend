# Descuento sin IVA por línea y forma del precio hacia Siigo — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El descuento de cada línea de cotización y pedido pasa a ser sin IVA, y cada conexión de Siigo elige si manda el precio base con descuento aparte o el precio ya descontado.

**Architecture:** `VatIncludedLine` (BuildingBlocks) calcula además `DiscountAmountWithoutTax`; `QuotationItem` y `OrderItem` guardan ese valor en `DiscountAmount`; el POS sigue con el valor con IVA. `siigo.settings` suma `price_mode` y `SiigoInvoiceBuilder` ramifica `(price, discount)` según él.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, FluentValidation, CQ propio.

**Spec:** `docs/superpowers/specs/2026-11-23-descuento-sin-iva-y-precio-a-siigo-design.md`

## Global Constraints

- Rama `feature/descuento-sin-iva-siigo-precio`, worktree `qep-backend-worktrees/descuento-sin-iva-siigo-precio`. Nada se edita en el checkout de `develop`.
- Commits convencionales, **sin** `Co-Authored-By` ni atribución de IA (regla del owner; ignora cualquier trailer que te pidan).
- TDD: RED antes que GREEN con la salida literal de ambos. Un RED que falla por compilación se prueba con la salida del build, sin contenedores.
- Pruebas filtradas por nombre (`--filter "FullyQualifiedName~Nombre"`). Los subagentes corren las pruebas en **primer plano**. Antes de lanzar integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una corrida de otra sesión, esperar, nunca detenerla. Nunca dos corridas de integración a la vez.
- Comandos al developer en PowerShell. Textos en español colombiano tuteando; identificadores y códigos de error sin traducir.
- Ningún total cambia: `Subtotal`, `TaxAmount`, `Total`, `NetTotal` y `computedOrderTotal` de Siigo salen iguales antes y después.
- `price_mode`: valores `BaseWithDiscount` (por defecto, comportamiento de hoy) y `DiscountedPrice`.
- Un `price_mode` ausente en el `PUT …/document` **conserva** el valor guardado; uno desconocido es 422 `validation.failed` con la clave `priceMode`.
- El regalo (`IsGift`) no cambia en ningún modo.
- No tocar `docs/estado.md`: se actualiza en `develop` al mergear.
- El frontend (selector de `price_mode`) es un spec propio en `qep-frontend`; aquí sólo el contrato.

## Review Focus

1. Línea con IVA 0 % (USD o empresa sin IVA): el descuento sin IVA es igual al de hoy.
2. Línea con descuento 0 %: `DiscountAmountWithoutTax` es 0, nunca negativo.
3. Redondeo de moneda (`PriceRounding` con 0 decimales y `Down`): `bruto sin IVA − descuento = Subtotal` cierra al centavo.
4. `DiscountedPrice` con cantidad fraccionaria o cantidad 0: no divide entre cero y el total de la factura no se mueve.
5. Una fila de `siigo.settings` existente antes de la migración se lee como `BaseWithDiscount`.

---

## Bloque 1 — Descuento sin IVA (Tasks 1 y 2, un solo implementador)

### Task 1: `DiscountAmountWithoutTax` en `VatIncludedLine`

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Domain/Pricing/VatIncludedLine.cs`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/VatIncludedLineTests.cs`

**Interfaces:**
- Produces: `VatIncludedLineAmounts` suma el miembro `decimal DiscountAmountWithoutTax` **al final** del record (los constructores y `with` existentes siguen compilando si se da un valor por defecto en el sitio de creación único, `Compute`). `Compute` conserva firma y valores de `DiscountAmount`, `TaxAmount`, `Subtotal`, `LineTotal`.

- [ ] **Step 1: Escribir las pruebas que fallan** en `VatIncludedLineTests`:
  - `DiscountWithoutTaxClosesTheGrossBase`: `Compute(200m, 25_000m, 20m, 19, PriceRounding.LegacyFor("COP"))` → `grossWithoutTax = Round(200 × 25_000) − Round(Round(200 × 25_000) × 19 / 119)`; assert `grossWithoutTax − amounts.DiscountAmountWithoutTax == amounts.Subtotal`.
  - `DiscountWithoutTaxEqualsTheOldOneWithoutVat`: `Compute(10m, 13.19m, 15m, 0, nearest)` → `DiscountAmountWithoutTax == DiscountAmount`.
  - `ADiscountOfZeroPercentGivesZeroWithoutTax`: `Compute(3m, 11_900m, 0m, 19, COP)` → `0m`.
  - `TheWithVatDiscountIsUntouched`: la fila ya cubierta por `Compute(2m, 11_900m, 10m, 19, nearest)` conserva su `DiscountAmount` actual.
  - `DiscountWithoutTaxClosesWithZeroDecimalsRoundingDown`: `Compute(100m, 24.63m, 0m, 0, new PriceRounding(0, PriceRoundingMode.Down))` y otra con 10 % y IVA 19 → `grossWithoutTax − DiscountAmountWithoutTax == Subtotal`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~VatIncludedLineTests"`. Esperado: fallo de compilación por `DiscountAmountWithoutTax`.
- [ ] **Step 3: Implementar** en `VatIncludedLine.Compute`: `gross = Round(quantity × charged)`; `grossTax = Round(gross × t / (100 + t))`; `DiscountAmountWithoutTax = (gross − grossTax) − (lineTotal − taxAmount)`. Un comentario XML explica por qué se deriva así (cierra con `Subtotal`) y que el POS sigue usando `DiscountAmount`.
- [ ] **Step 4: GREEN** — mismo comando; todas las pruebas de la clase pasan.
- [ ] **Step 5: Commit** — `git commit -m "feat(pricing): compute the line discount without VAT beside the one with VAT"`.

### Task 2: Cotización y pedido guardan el descuento sin IVA

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:358-360`, `src/Modules/Quotations/Modules.Quotations.Domain/OrderItem.cs:331-332`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:176`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs:323` (sólo el comentario; ver Step 4)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemTests.cs`, `DocumentTotalsTests.cs`, `QuotationPdfDocumentMapperTests.cs`, `OrdersExportProcessorTests.cs`

**Interfaces:**
- Consumes: `VatIncludedLineAmounts.DiscountAmountWithoutTax` (Task 1).
- Produces: `QuotationItem.DiscountAmount` y `OrderItem.DiscountAmount` pasan a ser base sin IVA; `DocumentTotals.DiscountAmount` es su suma.

- [ ] **Step 1: Pruebas que fallan:**
  - `OrderItemTests.TheLineDiscountIsWithoutVat`: línea de 200 u. a 25.000 con 20 % y IVA 19 → `grossWithoutTax − DiscountAmount == Subtotal`.
  - `DocumentTotalsTests.TheHeaderDiscountSumsTheWithoutVatLines` y `TotalsDoNotChange`: mismas líneas, `Subtotal`, `TaxAmount`, `Total`, `RetentionAmount` y `NetTotal` con los valores de antes del cambio (cópialos de la corrida RED).
  - `QuotationPdfDocumentMapperTests.TheLineTotalDoesNotDependOnTheDiscount`: el total de línea del PDF es `Subtotal + TaxAmount` y es **igual** al de hoy para la misma línea.
  - Un caso POB888: dos líneas con IVA 19 y 40 % de descuento, cuyo descuento de encabezado ÷ 1,19 reproduce el valor viejo ± 0,01 por línea.
  - `OrdersExportProcessorTests`: la celda «descuento» exporta `item.DiscountAmount` (sin IVA) y queda al lado de `DiscountedUnitPriceWithoutTax` y `TaxAmount`, que ya son sin IVA.
- [ ] **Step 2: RED** — filtrado por esas clases: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderItemTests|FullyQualifiedName~DocumentTotalsTests|FullyQualifiedName~QuotationPdfDocumentMapperTests|FullyQualifiedName~OrdersExportProcessorTests"`. Esperado: fallan los valores nuevos.
- [ ] **Step 3: Implementar:** `DiscountAmount = amounts.DiscountAmountWithoutTax` en los dos `Apply`; en `QuotationPdfDocumentMapper.LineFor` el total de línea pasa a `item.Subtotal + item.TaxAmount` (misma fórmula que el dominio: `LineTotal`).
- [ ] **Step 4:** En `OrdersExportProcessor`, la celda no cambia de código; se agrega un comentario de que el descuento por línea es sin IVA desde 2026-11-23 y que la hoja del ERP lo recibe así (decisión pendiente del owner si el ERP esperaba el de antes). Barrer por lectura `CopilotDocumentTools`, `StoreOrdersAdapter`, `IStoreOrderGateway`, `QuotePublicCart` y `OrderPaymentCalculation`: confirmar que ninguno resta `DiscountAmount` de un total con IVA; si alguno lo hace, corregirlo y cubrirlo con prueba.
- [ ] **Step 5: GREEN** — mismo filtro. Luego `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests` completo (el POS no debe moverse) y `dotnet build` de la solución.
- [ ] **Step 6: Commit** — `git commit -m "feat(quotations): store the line discount without VAT in quotations and orders"`.

---

## Bloque 2 — `price_mode` de Siigo (Tasks 3 y 4, un solo implementador)

### Task 3: Ajuste `price_mode` por conexión

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoPriceMode.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettings.cs` (record `SiigoDocumentSettings`, propiedad, `Create`, `UpdateDocument`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs:81` (mapeo) y crear migración `AddSiigoPriceMode` en `Persistence/Migrations/` (+ snapshot)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoSettingsDto.cs` (`SiigoDocumentDto`), `Settings/UpdateSiigoDocument.cs` (command, validador, handler), `Settings/SiigoSettingsSupport.cs:146` (DTO)
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs:362` y `:481` (`SiigoDocumentRequest`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoSettingsTests.cs`, `SiigoDbContextMappingTests.cs`, `SiigoSettingsSellersDtoTests.cs` (sitios que construyen `SiigoDocumentDto`)

**Interfaces:**
- Produces: `enum SiigoPriceMode { BaseWithDiscount, DiscountedPrice }`; `SiigoSettings.PriceMode { get; private set; }` (por defecto `BaseWithDiscount` en `Create`); `SiigoDocumentSettings(..., string? FreightProductCode, SiigoPriceMode? PriceMode = null)` donde `null` conserva el valor guardado; `SiigoDocumentDto(..., string FreightProductCode?, string PriceMode)` con el nombre del enum; `UpdateSiigoDocumentCommand(..., string? FreightProductCode, string? PriceMode, long ExpectedVersion)`; `SiigoDocumentRequest(..., string? FreightProductCode, string? PriceMode)`.

- [ ] **Step 1: Pruebas que fallan:**
  - `SiigoSettingsTests.APriceModeCreatedByDefaultIsBaseWithDiscount`.
  - `UpdateDocumentKeepsThePriceModeWhenItIsNull` y `UpdateDocumentChangesThePriceMode` (también sube `Version`).
  - `UpdateSiigoDocumentValidator`: `PriceMode = "Nope"` → error con `priceMode`; `null`, `"BaseWithDiscount"` y `"DiscountedPrice"` válidos (sin distinguir mayúsculas).
  - `SiigoDbContextMappingTests`: `price_mode` es texto `NOT NULL` con default `'BaseWithDiscount'`.
  - `SiigoSettingsSellersDtoTests`: el DTO lleva `PriceMode` con el nombre del enum.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoSettingsTests|FullyQualifiedName~SiigoDbContextMappingTests|FullyQualifiedName~SiigoSettingsSellersDtoTests"`. Esperado: no compila.
- [ ] **Step 3: Implementar** siguiendo `FreightSeparateInvoice` en cada capa; el enum se guarda como texto (mismo estilo que `SiigoInvoice.Status` en `SiigoDbContext`); el validador usa `Enum.TryParse<SiigoPriceMode>(ignoreCase: true)` con `OverridePropertyName("priceMode")`; el handler pasa `PriceMode` ya parseado o `null`. Corregir los sitios de prueba que construyen `SiigoDocumentDto`/`UpdateSiigoDocumentCommand`.
- [ ] **Step 4: Migración** — verificar antes que `Api.exe` no esté corriendo (`Get-Process Api -ErrorAction SilentlyContinue`; si lo está, avisar, no detenerlo). Generarla con `dotnet ef migrations add AddSiigoPriceMode --project src/Modules/Siigo/Modules.Siigo.Infrastructure --startup-project src/Api --context SiigoDbContext`; revisar que sea sólo `AddColumn<string> price_mode` con `defaultValue: "BaseWithDiscount"`. Aplicarla a `dev_qep` y comprobar que una fila vieja de `siigo.settings` lee `BaseWithDiscount`.
- [ ] **Step 5: GREEN** — mismo filtro más `dotnet build`.
- [ ] **Step 6: Commit** — `git commit -m "feat(siigo): add a price mode setting per connection"`.

### Task 4: `SiigoInvoiceBuilder` ramifica por `price_mode` y cierre

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBuilder.cs:390-396` (`Resolve`)
- Modify: `.claude/rules/siigo.md` (una línea nueva) 
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceBuilderTests.cs`

**Interfaces:**
- Consumes: `SiigoSettings.PriceMode` (Task 3).
- Produces: en `DiscountedPrice`, cada ítem pagado sale con `price = Round(Subtotal / quantity, 6)` y `discount = 0`; `BaseWithDiscount` queda idéntico a hoy.

- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceBuilderTests`:
  - `DiscountedPriceSendsThePriceAfterTheDiscount`: línea con 20 % → `discount == 0` y `price == Round(Subtotal / quantity, 6)`.
  - `BothModesProduceTheSameInvoiceTotals`: misma entrada, `computedOrderTotal`, impuestos y pagos iguales en los dos modos.
  - `DiscountedPriceWithZeroQuantityDoesNotDivide`: cantidad 0 → `(0, 0)` sin excepción.
  - `AGiftIsTheSameInBothModes`: el obsequio sale igual (`price 0`, `tax_base`).
  - `BaseWithDiscountStaysAsBefore`: el caso existente de descuento no cambia (golden de la prueba actual).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceBuilderTests"`. Esperado: fallan los dos casos de `DiscountedPrice`.
- [ ] **Step 3: Implementar** la rama en `Resolve`: `settings.PriceMode == SiigoPriceMode.DiscountedPrice` ⇒ `(Math.Round(line.Subtotal / quantity, 6, AwayFromZero), 0m)` si `quantity > 0`, si no `(0m, 0m)`; el resto (`subtotal`, `tax`) sigue con la fórmula existente. Comentario: en este modo el `Subtotal` ya trae el descuento.
- [ ] **Step 4: Documentación** — en `.claude/rules/siigo.md` agregar: «`siigo.settings.price_mode` decide si el ítem viaja con el precio de lista y `discount`, o con el precio ya descontado y `discount: 0`; el total no cambia entre modos y un valor ausente en el `PUT` conserva el guardado». Sin tocar `docs/estado.md`.
- [ ] **Step 5: GREEN** — mismo filtro, luego los proyectos de prueba de los módulos tocados **uno a la vez**: `Modules.Siigo.UnitTests`, `Modules.Quotations.UnitTests`, `Modules.Pos.UnitTests`, `ArchitectureTests`; la integración de Siigo y Quotations filtrada por nombre (`SiigoNoLeakTests`, `OrderExportApiTests`) con `-- xUnit.MaxParallelThreads=4`, esperando si hay otra corrida activa.
- [ ] **Step 6: Commit** — `git commit -m "feat(siigo): send the discounted price when the connection asks for it"`.

---

## Cierre

- Revisión fuerte una sola vez, sobre toda la rama, con el modelo más capaz.
- Entregar al owner: la lista de las dos decisiones pendientes del spec (filas viejas, columna del ERP) y el contrato del `PUT …/document` (`priceMode`) para el spec del frontend.
