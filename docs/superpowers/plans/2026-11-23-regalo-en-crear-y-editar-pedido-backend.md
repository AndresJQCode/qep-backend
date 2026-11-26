# Plan — Regalo al crear y editar un pedido (backend)

Spec: `docs/superpowers/specs/2026-11-23-regalo-en-crear-y-editar-pedido-design.md`. **Asume D-1 = A** (lista `gifts` aparte;
en la edición, ausente conserva). Si el owner elige B, no se ejecuta este plan: se rehace el Bloque A.

Reglas que mandan (workspace `CLAUDE.md`, presupuesto del 2026-11-12): un implementador por bloque, un commit por tarea, una
revisión por bloque, TDD con RED y GREEN literales. El RED de compilación no levanta contenedores: la evidencia es la salida del
build. La integración corre en GREEN, **filtrada por nombre**, en primer plano, nunca dos a la vez.

## Preparación (una vez, antes del Bloque A)

```powershell
git -C C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend branch --show-current   # develop
git -C C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend worktree add `
  C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\regalo-en-pedido `
  -b feature/regalo-en-pedido develop
```

Todo se trabaja en ese worktree (`$wt` abajo). El checkout principal tiene cambios ajenos sin commitear en Quotations: no se tocan.
Antes de cada `dotnet test`: `Test-Path <ruta del proyecto>` (una ruta vacía corre toda la solución) y, para integración,
`Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra corrida, se espera, no se detiene. Si `Api.exe` está
corriendo y bloquea el build, se avisa al developer; no se detiene.

Comandos:

```powershell
$wt = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\regalo-en-pedido"
$unit = "$wt\tests\Modules\Quotations\Modules.Quotations.UnitTests"
$int  = "$wt\tests\Modules\Quotations\Modules.Quotations.IntegrationTests"
dotnet test $unit --filter "FullyQualifiedName~<Clase>.<Prueba>"
dotnet test $int  --filter "FullyQualifiedName~<Clase>.<Prueba>" -- xUnit.MaxParallelThreads=4
```

Rutas relativas a `$wt`. `App` = `src/Modules/Quotations/Modules.Quotations.Application`, `Dom` =
`src/Modules/Quotations/Modules.Quotations.Domain`, `Api` = `src/Modules/Quotations/Modules.Quotations.Api`, `UT` =
`tests/Modules/Quotations/Modules.Quotations.UnitTests`, `IT` = `tests/Modules/Quotations/Modules.Quotations.IntegrationTests`.

---

## Bloque A — contrato, validación y alta con regalos · **seguro para Sonnet**

Sin lógica de conciliación: tipos nuevos, reglas de FluentValidation calcadas de las de `Items` y una línea más en `Order.Build`.

### A1. Tipos del contrato y validadores

Archivos: `App/OrdersDtos.cs`, `App/CreateOrder.cs`, `App/PreviewOrder.cs`, `App/OrderEdits.cs`, `App/SaveOrderEdits.cs`,
`App/PreviewOrderEdits.cs`; pruebas en `UT/CreateOrderValidatorTests.cs` y `UT/SaveOrderEditsValidatorTests.cs`.

1. **RED.** Agregar a `CreateOrderValidatorTests`:
   - `RejectsTheSameVariantTwiceAsAGift`: `Gifts = [(V, 1), (V, 2)]` → error con `PropertyName == "Gifts"`.
   - `RejectsAGiftWithoutQuantity` (`[Theory]` 0 y -1) → error en `Gifts[0].Quantity`.
   - `AcceptsTheSameVariantPaidAndAsAGift`: `Items = [(V, 10)]`, `Gifts = [(V, 1)]` → válido.
   - `RequiresAPaidLineEvenWithGifts`: `Items = []`, `Gifts = [(V, 1)]` → error en `Items`.
   Agregar a `SaveOrderEditsValidatorTests`: `RejectsTheSameVariantTwiceAsAGift`, `AcceptsNullGifts`, `AcceptsAnEmptyGiftList`.
   Correr `dotnet build $unit`: falla porque `Gifts` no existe. Esa salida es el RED.
2. **GREEN.**
   - `OrdersDtos.cs`: `public sealed record OrderGiftLineRequest(Guid VariantId, decimal Quantity);` (con `<summary>`: «Un regalo
     del pedido (spec 2026-11-23): variante y cantidad; el precio es el de lista y el descuento el fijo del 100 %.»).
   - `CreateOrderRequest` y `SaveOrderEditsRequest`: propiedad `public IReadOnlyList<OrderGiftLineRequest>? Gifts { get; init; }`
     en el cuerpo del record (no posicional), con `<summary>` que diga: crear, ausente = sin regalos; editar, **ausente o null
     conserva** los guardados, presente (también `[]`) es la lista entera. Los records derivados (`PreviewOrderRequest`,
     `PreviewOrderEditsRequest`) la heredan sin cambios.
   - `IOrderDraft` y `IOrderEdits`: `IReadOnlyList<OrderItemAddition>? Gifts { get; }`.
   - `CreateOrderCommand`, `PreviewOrderQuery`, `SaveOrderEditsCommand`, `PreviewOrderEditsQuery`: último parámetro posicional
     `IReadOnlyList<OrderItemAddition>? Gifts = null`.
   - `OrderDraftValidator` y `OrderEditsValidator`, junto a las reglas de `Items`:
     ```csharp
     RuleForEach(x => x.Gifts).ChildRules(gift =>
     {
         gift.RuleFor(line => line.VariantId).NotEmpty();
         gift.RuleFor(line => line.Quantity).GreaterThan(0m);
     }).When(x => x.Gifts is not null);
     RuleFor(x => x.Gifts)
         .Must(gifts => gifts!.Select(line => line.VariantId).Distinct().Count() == gifts!.Count)
         .WithMessage("La misma variante no puede ir más de una vez como regalo.")
         .When(x => x.Gifts is not null);
     ```
     (en `OrderEditsValidator` usar el `HasDuplicates` que ya existe en la clase).
   - Correr: `dotnet test $unit --filter "FullyQualifiedName~CreateOrderValidatorTests|FullyQualifiedName~SaveOrderEditsValidatorTests"`.
3. Commit: `feat(orders): accept a gifts list on create and edit requests`.

### A2. El dominio crea un pedido con regalos

Archivos: `Dom/OrderInputs.cs`, `Dom/Order.cs` (`Build`); prueba en `UT/OrderGiftTests.cs`.

1. **RED.** En `OrderGiftTests`:
   - `CreateManualTakesAPaidLineAndAGiftOfTheSameVariant`: `CreateManual` con dos `OrderLineInput` de la misma variante, la
     segunda con `IsGift: true` → dos líneas; el regalo con `DiscountPercentage == 100`, `Subtotal == 0` y `UnitPrice` el de la
     entrada; `order.Total` igual al de un pedido sólo con la pagada.
   - `CreateManualRejectsTheSameVariantTwiceAsAGift` → `QuotationsDomainException` con `order.item.duplicate_product`.
   `dotnet build $unit` falla (`IsGift` no existe en `OrderLineInput`): RED.
2. **GREEN.** `OrderLineInput`: último parámetro `bool IsGift = false` (documentarlo en el `<summary>`). En `Order.Build`, la
   llamada a `AddItemCore` pasa `isGift: line.IsGift`. Nada más: `CandidateLines` (Shopify) no cambia.
   Correr: `dotnet test $unit --filter "FullyQualifiedName~OrderGiftTests"`.
3. Commit: `feat(orders): let a manual order be born with gift lines`.

### A3. `OrderDraftBuilder` precia los regalos y los handlers los pasan

Archivos: `App/OrderDraftBuilder.cs`, `App/CreateOrder.cs` (handler), `App/PreviewOrder.cs` (handler); prueba nueva
`UT/OrderDraftBuilderGiftTests.cs` (copiar el armado de `UT/OrderDraftBuilderPersonTypeTests.cs`).

1. **RED.** `OrderDraftBuilderGiftTests`:
   - `PreviewPricesAGiftAtListPriceWithFullDiscount`: catálogo con la variante V a 1.000 y una escala de 10 % desde 10
     unidades; `Items = [(V, 10)]`, `Gifts = [(V, 12)]` → la línea de regalo con `UnitPrice == 1000`, `DiscountPercentage == 100`,
     `Subtotal == 0`; la pagada con su descuento por escala sin contar las 12 del regalo; `order.Total` igual al de un preview
     sin `Gifts`.
   - `BuildAsyncKeepsTheGift`: lo mismo por `BuildAsync`, `Assert.Single(order.Items, item => item.IsGift)`.
   - `NoGiftsWhenTheListIsNull`.
   `dotnet build $unit` falla (`OrderDraftInput` no tiene `Gifts`): RED.
2. **GREEN.**
   - `OrderDraftInput`: último parámetro `IReadOnlyList<OrderItemAddition>? Gifts = null`.
   - `OrderDraftBuilder`: método privado `PriceGiftLinesAsync(Guid tenantId, IReadOnlyList<OrderItemAddition>? gifts, string
     currency, CancellationToken)` que devuelve `[]` con `null` o vacía y, si no, hace lo mismo que `PriceLinesAsync` pero con
     `isRetail: true` en `QuotationProductPricingResolver.Resolve` (mismo motivo que `AddOrderGift`: el regalo va a lista, sin
     escalas) y `IsGift: true` en cada `OrderLineInput`. `PriceAsync` concatena: primero las pagadas, después los regalos. **No**
     tocar `PriceLinesAsync`: la usa `StoreOrderGateway`.
   - `CreateOrderHandler` y `PreviewOrderHandler`: pasar `command.Gifts`/`query.Gifts` al `OrderDraftInput`.
   Correr: `dotnet test $unit --filter "FullyQualifiedName~OrderDraftBuilderGiftTests|FullyQualifiedName~OrderDraftBuilderPersonTypeTests"`.
3. Commit: `feat(orders): price gift lines when creating and previewing an order`.

**Cierre del bloque A:** `dotnet test $unit` (todo el proyecto de unidades; no levanta contenedores). Revisión del bloque.

---

## Bloque B — conciliación de regalos al editar · **cuidado** (Sonnet con revisión atenta)

Toca el guardado atómico del pedido (`SaveOrderEdits`): orden de pasos, «nada cambió», historial y auditoría. Lo delicado:
correr **después** de `OrderItemEdits`, no conciliar nada con `Gifts == null`, y no repreciar al cambiar la cantidad.

### B1. `OrderItemEdit.IsGift` y el texto del cambio de cantidad

Archivos: `App/OrderItemEdits.cs` (sólo el record), `App/OrderChangeSummary.cs`; prueba en `UT/OrderGiftEditsTests.cs` (nueva).

1. **RED.** `GiftQuantityChangedNamesTheGift`: `OrderChangeSummary.GiftQuantityChanged("Aceite", 1m, 3m, format)` ==
   `"Cambió la cantidad del regalo Aceite: 1 → 3."` (el `HistoryFormat` como lo arman las pruebas existentes de
   `OrderChangeSummary`/`QuotationChangeSummary`). Build falla: RED.
2. **GREEN.** `OrderChangeSummary.GiftQuantityChanged(string productName, decimal from, decimal to, HistoryFormat format) =>
   Trim($"Cambió la cantidad del regalo {productName}: {format.Number(from)} → {format.Number(to)}.");`. `OrderItemEdit`: último
   parámetro `bool IsGift = false`.
3. Commit: `feat(orders): history text for a gift quantity change`.

### B2. `OrderGiftEdits.ApplyAsync`

Archivo nuevo `App/OrderGiftEdits.cs`; pruebas en `UT/OrderGiftEditsTests.cs` (armado: copiar `NewOrder`, `AddGift`, `Variant` y
el catálogo de `UT/OrderGiftHandlerTests.cs`; el pedido mayorista, de `UT/WholesaleOrderEditsTests.cs`; el pedido pagado, de
`OrderGiftTests.AGiftOnAPaidOrderFreezesItsCost`).

Firma:

```csharp
internal static class OrderGiftEdits
{
    /// Lleva los regalos del pedido a la lista deseada (spec 2026-11-23). Null no toca nada: un cliente viejo no los borra.
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order,
        IReadOnlyCollection<OrderItemAddition>? desired,
        IQuotationProductPricingLookup pricingLookup,
        IOrderVariantCostLookup costLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}
```

1. **RED.** Pruebas (todas sobre un pedido manual con una línea pagada de V1 x10 salvo que se diga otra cosa):
   - `NullLeavesTheGiftsUntouched`: pedido con un regalo V1 x2; `desired = null` → sin ediciones, el regalo sigue.
   - `AddsAGiftAtListPriceWithFullDiscount`: catálogo V2 a 2.000 con escala de 10 % desde 10, IVA 5; `desired = [(V2, 12)]` →
     regalo con `UnitPrice 2000`, `DiscountPercentage 100`, `Subtotal 0`, `TaxPercentage 5`; una edición `Added`, `IsGift`,
     `ProductName` el del catálogo, `Quantity 12`.
   - `AddsAGiftOfTheSameVariantAsAPaidLine`: `desired = [(V1, 1)]` → dos líneas de V1, la pagada intacta.
   - `ChangesTheQuantityWithoutRepricing`: regalo V1 x2 a 1.000; catálogo de V1 ahora a 9.999; `desired = [(V1, 5)]` →
     `Quantity 5`, `UnitPrice 1000`, `DiscountPercentage 100`; edición `QuantityChanged` 2 → 5, `IsGift`.
   - `LeavesAnUnchangedGiftAlone`: regalo V1 x2; `desired = [(V1, 2)]` → sin ediciones.
   - `RemovesAGiftMissingFromTheList`: regalos V1 y V2; `desired = [(V2, 1)]` → queda V2 y la pagada; edición `Removed` con
     `ProductName` = la foto de la línea quitada.
   - `AnEmptyListRemovesEveryGift`.
   - `AWholesaleOrderRejectsAGift`: `desired = [(V1, 1)]` → `order.gift.wholesale_not_supported`.
   - `AGiftAddedToAPaidOrderFreezesItsCost`.
   Build falla (clase inexistente): RED.
2. **GREEN.** Implementación:
   - `desired is null` → `return [];`.
   - `current = order.Items.Where(item => item.IsGift).ToDictionary(item => item.VariantId)` (el dominio garantiza un regalo por
     variante).
   - Altas (`desired` cuya variante no está en `current`): una sola lectura con
     `QuotationProductPricingResolver.FindVariantsAsync` de esas variantes, costos con `OrderLineCostsLoader.LoadForAdditionsAsync`,
     y por cada una `QuotationProductPricingResolver.Resolve(..., order.Currency, isRetail: true)` y `order.AddGift(OrderItemId.New(),
     variantId, pricing.ProductId, quantity, pricing.Pricing.UnitPrice, pricing.Pricing.TaxPercentage, costs, updatedBy, occurredAt,
     pricing.Kind, pricing.SalesUnit)`; edición `new OrderItemEdit(Added, variantId, pricing.Name, null, quantity, IsGift: true)`.
   - Cambios: `order.UpdateItemQuantity(item.Id, quantity, item.DiscountPercentage, item.ProductTaxPercentage ?? item.TaxPercentage,
     updatedBy, occurredAt)`, sin leer el catálogo; edición `QuantityChanged` con el nombre de la foto
     (`item.ProductName ?? item.ProductCode ?? "un producto"`).
   - Bajas: `order.RemoveItem(item.Id, updatedBy, occurredAt)`; edición `Removed` con el nombre de la foto.
   - Mismo orden que `OrderItemEdits`: altas, cambios, bajas.
   Correr: `dotnet test $unit --filter "FullyQualifiedName~OrderGiftEditsTests"`.
3. Commit: `feat(orders): reconcile the gift lines of an order edit`.

### B3. Guardado y cálculo previo de la edición

Archivos: `App/SaveOrderEdits.cs`, `App/PreviewOrderEdits.cs`; pruebas en `UT/SaveOrderEditsGiftTests.cs` (nueva; armado del
`SaveOrderEditsHandler` copiado de `UT/OrderProofBatchingTests.cs`).

1. **RED.**
   - `PutWithGiftsWritesGiftHistoryAndAudit`: pedido con regalo V2 x1; `Gifts = [(V1, 2)]` y luego, sobre el resultado,
     `Gifts = [(V1, 3)]` (con la versión nueva) → historial `ItemsChanged` con `GiftAdded` de V1 x2 y `GiftRemoved` de V2 en el
     primer guardado, y `GiftQuantityChanged` de V1 2 → 3 en el segundo (textos exactos de B1 y los existentes); auditoría `quotation.order.gift_added`, `quotation.order.gift_removed`,
     `quotation.order.gift_updated`.
   - `PutWithoutGiftsKeepsThemAndChangesNothing`: pedido con regalo; mismo `Items`, `Gifts = null`, nada más → versión sin
     cambio y sin historial (el camino «nada cambió»).
   - `PutWithTheSameGiftsChangesNothing`: `Gifts` igual a lo guardado → igual que la anterior.
   - `PutWithGiftsOnANonEditableOrderIsRejected` → `order.order.not_editable`, el regalo no cambia.
   - La prueba existente `OrderGiftHandlerTests.PutItemsWithTheSameVariantKeepsTheGift` no se toca y sigue en verde.
   Compila (`SaveOrderEditsCommand` ya tiene `Gifts` desde A1), así que el RED es de ejecución, en unidades (sin
   contenedores): `dotnet test $unit --filter "FullyQualifiedName~SaveOrderEditsGiftTests"`; se guarda la salida en rojo.
2. **GREEN.**
   - `SaveOrderEdits.HandleAsync`: justo después de `OrderItemEdits.ApplyAsync`, `var giftEdits = await OrderGiftEdits.ApplyAsync(order,
     command.Gifts, pricingLookup, costLookup, command.TenantId, updatedBy, now, cancellationToken);` y `itemEdits =
     [.. itemEdits, .. giftEdits]` (que `itemEdits` sea una lista o `IReadOnlyList` construida con ambas), así el chequeo de «nada
     cambió», `RecordItemEditsAsync` y `CaptureProductSnapshots` los ven.
   - `RecordItemEditsAsync`: si `edit.IsGift`, `Added` → `OrderChangeSummary.GiftAdded(edit.ProductName!, edit.Quantity!.Value,
     format)` y `"quotation.order.gift_added"`; `QuantityChanged` → `GiftQuantityChanged(...)` y `"quotation.order.gift_updated"`;
     `Removed` → `GiftRemoved(edit.ProductName ?? "un producto")` y `"quotation.order.gift_removed"` (sin buscar en el catálogo).
     Los regalos no entran en `removedVariantIds`.
   - `PreviewOrderEdits.HandleAsync`: la misma llamada a `OrderGiftEdits.ApplyAsync` después de `OrderItemEdits.ApplyAsync`.
   Correr: `dotnet test $unit --filter "FullyQualifiedName~SaveOrderEditsGiftTests|FullyQualifiedName~OrderGiftHandlerTests|FullyQualifiedName~OrderProofBatchingTests"`.
3. Commit: `feat(orders): save and preview gift edits with history and audit`.

**Cierre del bloque B:** `dotnet test $unit` completo. Revisión del bloque, mirando en especial: `Gifts == null` no toca nada,
el orden pagadas → regalos, y que el cambio de cantidad no lea el catálogo.

---

## Bloque C — rutas HTTP e integración · **seguro para Sonnet**

### C1. Las cuatro rutas pasan `gifts`

Archivos: `Api/OrderEndpoints.cs`; pruebas en `IT/OrderGiftApiTests.cs` (helpers de cuerpo y de `If-Match` copiados de
`IT/ManualOrderApiTests.cs` y `IT/OrderEditsApiTests.cs`).

1. **RED.** En `OrderGiftApiTests`:
   - `CreateOrderWithAGiftOfAPaidVariant`: `POST /orders` con `items: [{V, 10}]` y `gifts: [{V, 1}]` → 201; dos líneas de V, una
     con `isGift: true`, `discountPercentage: 100`, `subtotal: 0`; `total` igual al de un pedido sin `gifts`.
   - `PreviewNewOrderPaintsTheGift`: lo mismo por `POST /orders/preview` → 200.
   - `PutWithGiftsAddsChangesAndRemoves`: pedido creado con un regalo; `PUT` con `If-Match` y `gifts` con otra cantidad y otra
     variante → la respuesta trae los regalos nuevos y el historial `GET` las entradas de regalo.
   - `PutWithoutGiftsKeepsTheSavedGift`: `PUT` sin la propiedad `gifts` → el regalo sigue (compatibilidad con el frontend viejo).
   - `PreviewEditsPaintsAGift`: `POST /orders/{id}/preview` con `gifts` → el regalo en la respuesta y `version` sin cambio.
   - `CreateOrderRejectsARepeatedGift`: `gifts` con la misma variante dos veces → 422 `validation.failed` con la clave `Gifts`.
   RED (con contenedores, porque el fallo es de aserción, no de compilación):
   ```powershell
   Get-Process testhost, dotnet -ErrorAction SilentlyContinue
   dotnet test $int --filter "FullyQualifiedName~OrderGiftApiTests.CreateOrderWithAGiftOfAPaidVariant|FullyQualifiedName~OrderGiftApiTests.PutWithGiftsAddsChangesAndRemoves" -- xUnit.MaxParallelThreads=4
   ```
   (dos pruebas bastan para el RED: muestran que el alta y el `PUT` ignoran `gifts`).
2. **GREEN.** En `OrderEndpoints.cs`:
   ```csharp
   private static OrderItemAddition[]? ToGiftAdditions(IReadOnlyList<OrderGiftLineRequest>? gifts) =>
       gifts?.Select(gift => new OrderItemAddition(gift.VariantId, gift.Quantity)).ToArray();
   ```
   `CreateOrderAsync` y `PreviewOrderAsync` pasan `Gifts: ToGiftAdditions(request.Gifts)` al comando/consulta;
   `SaveOrderEditsAsync` y `PreviewOrderEditsAsync` también (conservar el `null`: es «no los toques»).
   GREEN: la misma línea con los seis nombres de prueba.
3. Commit: `feat(orders): take gifts on create, edit and both previews`.

### C2. Corrida antes de mergear

Una sola vez, en primer plano, después de mirar que no haya otra corrida:

```powershell
dotnet test $unit
dotnet test "$wt\tests\ArchitectureTests"
dotnet test $int -- xUnit.MaxParallelThreads=4
```

Sólo el módulo tocado (Quotations) más arquitectura: el cambio no toca BuildingBlocks, Bootstrapper, `Program.cs` ni migraciones.
El merge va a `develop` (nunca a `main`); `docs/estado.md` se actualiza en `develop` al mergear, no en la rama.

---

## Fuera de este plan

- Borrar `POST/DELETE …/gifts` (`AddOrderGift`, `RemoveOrderGift`, `AddOrderGiftRequest`, sus pruebas): cambio aparte, si el owner
  lo aprueba (D-2), cuando el frontend nuevo esté en `main`.
- Frontend: su propio plan en `qep-frontend/docs/superpowers/plans/2026-11-23-regalo-en-crear-y-editar-pedido-frontend.md`. Se
  despliega después de este.
