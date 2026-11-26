# Regalos en el pedido y en la factura de Siigo — plan de implementación

> **Para quien ejecute:** usa `superpowers:subagent-driven-development` o `superpowers:executing-plans`, **agrupado en 3 bloques** (regla del owner,
> 2026-11-12): un implementador por bloque, un commit por tarea, una revisión por bloque. Los pasos usan casillas `- [ ]`.

**Meta:** que un pedido acepte líneas de regalo de cualquier variante y que la factura de Siigo las lleve como obsequio sin subir el total.

**Arquitectura:** `OrderItem.IsGift` (descuento fijo en 100 %, precio real conservado); el dominio, el repricing y la sincronización externa
tratan al regalo aparte; el contrato de facturación externa lleva `IsGift` y el precio de lista; `SiigoInvoiceBuilder` arma el ítem con
`price: 0`, `tax_base` y `taxpayer: "Company"`.

**Tecnología:** .NET 10, EF Core + Npgsql, xUnit, Minimal APIs.

**Spec:** [`docs/superpowers/specs/2026-11-21-siigo-regalo-design.md`](../specs/2026-11-21-siigo-regalo-design.md)

## Restricciones globales

- TDD: RED antes que GREEN, con la salida literal de ambos. **El RED de un fallo de compilación es la salida del build**, sin levantar contenedores.
- Pruebas filtradas por nombre mientras se itera (`--filter "FullyQualifiedName~Nombre"`); la clase completa, una vez por bloque. **Nunca dos
  corridas de integración a la vez**: antes, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; y si `Api.exe` corre, el build falla.
- Commits convencionales, **sin atribución de IA** ni `Co-Authored-By`. Rama: `feature/siigo-gift-lines`. No se toca `docs/estado.md` (se actualiza en `develop`, al mergear).
- Errores de dominio con código (`QuotationsDomainException`); ningún status HTTP a mano. Mensajes al usuario, en español colombiano tuteando.
- `Modules.Siigo` no referencia tipos de Quotations: el adaptador `src/Bootstrapper/SiigoOrderSource.cs` traduce.
- Valores del spec: `taxpayer` siempre `"Company"`; `tax_base` = precio de lista **sin IVA** (`ListUnitPrice × 100 / (100 + IVA)`, 6 decimales); regalo con descuento fijo 100 %.

## Foco de revisión

- Regalo de la **misma variante** que una línea pagada: alta, repricing, edición por lista (`PUT`) y factura no deben chocar ni borrarlo (T1, T3, T5).
- Un pedido de Shopify con regalo que se sincroniza de nuevo: el regalo **no** se borra (T1).
- Las escalas y el mínimo de compra **no** cuentan la cantidad del regalo, y el descuento del regalo nunca baja de 100 % (T3).
- Regalo en un pedido **no editable** (aprobado/cerrado) se rechaza con el mismo código que `AddItem` (T1).
- Producto sin IVA (0 %) como regalo: sin `taxes` y `tax_base` = precio de lista (T5).

---

## Bloque 1 — Dominio y persistencia

### Task 1: `IsGift` en `OrderItem` y `Order`

**Archivos:**
- Modificar: `src/Modules/Quotations/Modules.Quotations.Domain/OrderItem.cs`, `Order.cs` (`AddItemCore` ~1182, `ApplyGroupDiscounts` ~1333, `ReplaceExternalLines` ~2295)
- Prueba: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderGiftTests.cs` (nuevo; reutiliza los constructores de pedido de las pruebas de `Order` existentes)

**Interfaces:**
- Produce: `bool OrderItem.IsGift { get; }`; `void Order.AddGift(OrderItemId itemId, Guid variantId, Guid productId, decimal quantity, decimal listUnitPrice, int taxPercentage, OrderLineCosts costs, MemberId updatedBy, DateTimeOffset occurredAt, ProductKind kind = ProductKind.Physical, SalesUnit salesUnit = SalesUnit.Unit)`. `Order.RemoveItem` (existente) quita también el regalo.

- [ ] **Paso 1: pruebas en rojo** en `OrderGiftTests`:
  - `AGiftKeepsTheListPriceAndChargesNothing` — `AddGift(qty 1, 1_000, IVA 19)` → `IsGift`, `UnitPrice == 1_000`, `DiscountPercentage == 100`, `Subtotal == 0`, `TaxAmount == 0`.
  - `AGiftOfTheSameVariantAsAPaidLineIsAllowed` — línea pagada (10 u.) + regalo (1 u.) de la misma variante → 2 líneas; `ProductsTotal` igual al de la línea pagada sola.
  - `TwoGiftsOfTheSameVariantAreRejected` y `TwoPaidLinesOfTheSameVariantAreStillRejected` — ambos `order.item.duplicate_product`.
  - `AGiftOnANonEditableOrderIsRejected` — mismo código que `AddItem` (`EnsureEditable`).
  - `ApplyGroupDiscountsNeverLowersAGiftBelow100` — `ApplyGroupDiscounts` con un `QuotationItemDiscount(0m, Own)` para el regalo → sigue en 100 %.
  - `ReplaceExternalLinesKeepsTheGifts` — pedido con regalo; reemplazar líneas externas con una lista que no lo incluye → el regalo sigue.
  - `AGiftOnAPaidOrderFreezesItsCost` — como `AddItem` con `FullPaymentReceived`.
- [ ] **Paso 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderGiftTests"`. Esperado: falla de **compilación** (`AddGift`/`IsGift` no existen). Pega la salida del build.
- [ ] **Paso 3: implementar.** `OrderItem.IsGift` con `private set`; `OrderItem.Create` recibe `isGift` (default `false`) y, si es `true`, llama a `Apply(..., discountPercentage: 100, ...)`.
  `Order.AddGift` hace lo mismo que `AddItem` (`EnsureEditable`, costo congelado si está pagado, `Touch`) pasando `isGift: true`. `AddItemCore` valida duplicados por `(VariantId, IsGift)`.
  `ApplyGroupDiscounts` y `ReplaceExternalLines` saltan las líneas con `IsGift` (en `ReplaceExternalLines`, ni el `RemoveAll` ni el `FirstOrDefault` las ven).
  `UpdateQuantity` de un regalo conserva el 100 %.
- [ ] **Paso 4: GREEN** — mismo comando filtrado. Esperado: todas pasan.
- [ ] **Paso 5: commit** — `git add` de los 3 archivos; `feat(quotations): gift lines in the order domain`.

### Task 2: columna `is_gift`

**Archivos:**
- Modificar: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (bloque `OrderItem`, ~886–942)
- Crear: migración `AddOrderItemIsGift` en `.../Persistence/Migrations/` (+ `Designer` y `QuotationsDbContextModelSnapshot.cs` regenerados)

**Interfaces:** Consume `OrderItem.IsGift` (T1).

- [ ] **Paso 1: mapeo** — `item.Property(value => value.IsGift).HasColumnName("is_gift").HasDefaultValue(false).IsRequired()`.
- [ ] **Paso 2: generar la migración** con el flujo del README (`dotnet ef migrations add AddOrderItemIsGift`, con `Api.exe` detenido). Debe ser sólo `AddColumn<bool>(name: "is_gift", defaultValue: false)`; sin índices (el spec lo fija). Es expand puro: el código viejo la ignora.
- [ ] **Paso 3: build** — `dotnet build` del proyecto de Infrastructure y de `ArchitectureTests`. Esperado: sin errores ni cambios de capas.
- [ ] **Paso 4: commit** — `feat(quotations): add is_gift to order items`.

---

## Bloque 2 — Aplicación y API

### Task 3: alta y baja del regalo, y el resto del pedido lo respeta

**Archivos:**
- Crear: `src/Modules/Quotations/Modules.Quotations.Application/AddOrderGift.cs`, `RemoveOrderGift.cs`
- Modificar: `OrderItemEdits.cs` (`current` y `current.Values` sólo con `!IsGift`), `OrderPricingRecalculation.cs` (`lines` y `Items.Sum(Quantity)` sin regalos), `OrdersDtos.cs` + `OrderMapping.cs` (campo `IsGift`), `Modules.Quotations.Api/OrderEndpoints.cs`, el historial (`OrderChangeSummary`) y la auditoría
- Prueba: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderGiftHandlerTests.cs` y una de integración en `Modules.Quotations.IntegrationTests` (copia el harness de las pruebas de `AddOrderItems`)

**Interfaces:**
- Produce: `record AddOrderGiftCommand(Guid TenantId, Guid OrderId, Guid VariantId, decimal Quantity) : ICommand<OrderDetailDto>`; `record RemoveOrderGiftCommand(Guid TenantId, Guid OrderId, Guid ItemId) : ICommand<OrderDetailDto>`; `bool IsGift` en el DTO de línea del pedido; `POST /{orderId}/gifts` y `DELETE /{orderId}/gifts/{itemId}` (mismo permiso `OrdersPermissions.OrderManage` y misma doble revisión de tenant que `AddOrderItems`).

- [ ] **Paso 1: pruebas en rojo.**
  - Unitarias: `AddGiftUsesTheCatalogListPriceAndTheProductTax`; `AddGiftRejectsAnOtherTenantOrder` (403, nunca 404); `RemoveGiftDoesNotTouchThePaidLineOfTheSameVariant`; `PutItemsWithTheSameVariantKeepsTheGift` (la lista deseada del `PUT` no la quita ni lanza); `RepricingIgnoresGiftQuantityForScalesAndMinimumPurchase`; `AGiftWritesHistoryAndAudit`.
  - Integración: `PostGiftThenGetOrderReturnsIsGiftAndSameTotal` — pedido con 10 u. a 1.000 (IVA incluido) + regalo 1 u. → `total` 10.000 y la línea de regalo con `isGift: true`.
- [ ] **Paso 2: RED** — build roto (comandos inexistentes); luego, ya compilando, el filtro `OrderGiftHandlerTests` falla por comportamiento. Salida literal de ambos.
- [ ] **Paso 3: implementar.** `AddOrderGiftHandler` resuelve la variante con `IQuotationProductPricingLookup`, toma `pricing.Pricing.UnitPrice` de **lista** (sin escalas ni descuento) y llama a `order.AddGift`; recalcula con `OrderPricingRecalculation.ApplyAsync`, registra `OrderHistoryEntry` (`ItemsChanged`) y publica la auditoría `quotation.order.gift_added`. `RemoveOrderGiftHandler` valida que el `ItemId` sea un regalo del pedido (si no, `order.item.not_found`) y registra `quotation.order.gift_removed`. Validador de FluentValidation para ambos (`VariantId` y `Quantity > 0`). Un pedido mayorista usa el precio de lista del distribuidor (`IWholesaleLinePricing`), como `AddOrderItems`.
- [ ] **Paso 4: GREEN** — filtrado primero; la clase completa de integración **una vez** al final del bloque (sin otra corrida en paralelo).
- [ ] **Paso 5: auditar `VariantId`** — `rg "\.VariantId" src/Modules/Quotations` y revisar los usos fuera de lo ya tocado (`OrderDraftBuilder`, `OrdersExportProcessor`, `DeletedVariantLineStore`, `QuotationLifecycleEventPublisher`, `OrderResponseComposer`): ninguno debe asumir una línea por variante. Deja una nota en el commit de lo revisado y de cualquier ajuste.
- [ ] **Paso 6: commit** — `feat(quotations): add and remove gift lines on an order`.

---

## Bloque 3 — Facturación en Siigo

### Task 4: el contrato de facturación externa lleva el regalo

**Archivos:**
- Modificar: `Modules.Quotations.Application/IExternalOrderInvoicing.cs` (`ExternalInvoicingLine`), `ExternalOrderInvoicingService.cs` (~60), `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs` (`SiigoInvoiceLineInput`), `src/Bootstrapper/SiigoOrderSource.cs` (~38)
- Prueba: `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`; la de paridad de contratos que ya ata los enums si toca

**Interfaces:**
- Produce: `ExternalInvoicingLine(..., decimal TaxAmount, bool IsGift = false, decimal ListUnitPrice = 0m)` y `SiigoInvoiceLineInput(..., decimal TaxAmount, bool IsGift = false, decimal ListUnitPrice = 0m)`; `ListUnitPrice` = `OrderItem.UnitPrice` (con IVA incluido). Los defaults mantienen compilando los constructores existentes.

- [ ] **Paso 1: prueba en rojo** — `AGiftLineTravelsWithItsFlagAndListPrice` en `SiigoOrderSourceTests`: un pedido con un regalo (`UnitPrice 1_000`) → `SiigoInvoiceLineInput` con `IsGift == true` y `ListUnitPrice == 1_000`.
- [ ] **Paso 2: RED** — build roto por los campos nuevos.
- [ ] **Paso 3: implementar** los campos y los dos mapeos (`item.IsGift`, `item.UnitPrice`).
- [ ] **Paso 4: GREEN** — `--filter "FullyQualifiedName~SiigoOrderSourceTests"`.
- [ ] **Paso 5: commit** — `feat(siigo): carry gift lines in the invoicing contract`.

### Task 5: el builder arma el obsequio

**Archivos:**
- Modificar: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBuilder.cs` (`Resolve` ~364, `ResolvedItem` ~354, `Item` ~474)
- Prueba: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceBuilderTests.cs`

**Interfaces:** Consume `SiigoInvoiceLineInput.IsGift/ListUnitPrice` (T4).

- [ ] **Paso 1: pruebas en rojo** (usa el helper de entradas que ya tiene el archivo):
  - `AGiftItemIsSentWithZeroPriceTaxBaseAndCompanyTaxpayer` — línea de regalo 1 u., `ListUnitPrice 1_000`, IVA 19 → ítem con `price 0`, `discount 0`, `tax_base 840.336134` (6 decimales), `taxpayer "Company"` y `taxes` con el id del IVA.
  - `AGiftDoesNotChangeTheOrderTotal` — 10 u. pagas a 1.000 con IVA + regalo 1 u. → `Ready`, `Draft.Total == 10_000`, sin `TotalsMismatch`; el pago suma 10.000.
  - `AGiftWithoutVatHasNoTaxesAndTaxBaseEqualsTheListPrice` — IVA 0 → sin `taxes`, `tax_base == 1_000`.
  - `AGiftOfAnUnlinkedVariantIsMissing` — `variant_unlinked`, como una línea normal.
  - `AGiftWithAnUnlinkedTaxIsMissing` — `tax_unlinked`.
  - `PaidItemsAreSerializedExactlyAsBefore` — regresión: el JSON de una línea pagada no cambia ni un byte.
- [ ] **Paso 2: RED** — `--filter "FullyQualifiedName~SiigoInvoiceBuilderTests"`. Esperado: falla por comportamiento (hoy el regalo sale con `price 0` sin `tax_base`). Salida literal.
- [ ] **Paso 3: implementar.** `ResolvedItem` gana `bool IsGift` y `decimal? TaxBase`. En `Resolve`, si `line.IsGift`: `Subtotal`/`Tax` = 0, `price` 0, `discount` 0, `TaxBase = Round(ListUnitPrice × 100 / (100 + TaxPercentage), 6)`. En `Item`, un regalo agrega `tax_base` y `taxpayer: "Company"` (constante `GiftTaxpayer`); el resto del ítem no cambia. Un regalo no recibe retenciones en `taxes` (no tiene base).
- [ ] **Paso 4: GREEN** — filtrado y luego la clase completa `SiigoInvoiceBuilderTests`.
- [ ] **Paso 5: regla y commit** — agrega a `.claude/rules/siigo.md` el bullet «Un regalo viaja como obsequio» (`price: 0`, `tax_base` sin IVA, `taxpayer: "Company"`, total sin IVA del regalo; **no probado contra una factura real**, verificarlo con `send_to_dian` apagado) y commitea: `feat(siigo): invoice gift lines as obsequios`.

### Task 6: cierre del bloque

- [ ] `dotnet build` de la solución y `dotnet test` de `tests/Modules/Siigo/*`, `tests/Modules/Quotations/*`, `tests/Bootstrapper/*` y `tests/ArchitectureTests`, **de a uno** y con `-- xUnit.MaxParallelThreads=4`. El conjunto toca Quotations y Bootstrapper, pero no BuildingBlocks ni `Program.cs`: no hace falta la solución entera.
- [ ] Revisión fuerte única de la rama completa antes de mergear a `develop`.

---

## Pendiente fuera de este plan

- El botón «Agregar regalo» y cómo se dibuja la línea: spec propio en `qep-frontend`, después de desplegar este backend.
- Verificar con una factura real que `taxpayer: "Company"` deja el total en 10.000 y que el contador acepta la cuenta contrapartida.
