# Detalle del pedido del comprador (backend): plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `GET /buyer/orders/{orderId}` y `POST /buyer/orders/{orderId}/cancel` devuelvan el detalle ampliado (totales, partes de facturación y envío con nombres de lugar, transportadora, a quién se factura y la nota original del comprador), sin ruta ni código de error nuevos.

**Architecture:** `Order` gana `BuyerNotes`, que escribe sólo `CreateFromStore`, con su columna `buyer_notes` y una migración sin backfill. `BuyerOrderGateway` (Quotations.Application) recibe `IQuotationGeographyLookup` y arma el detalle en `ToDetailAsync`: totales, `billedTo` por `OrderBilledParty.SourceFor`, partes con nombres resueltos (una llamada por diccionario) y líneas sin IVA cuando hay excedente de IVA. Buyers.Application duplica los records como DTOs y `BuyerOrdersAdapter` (Bootstrapper) los copia campo a campo. El backend no lee `ShowPrices`.

**Tech Stack:** .NET 10, EF Core + Npgsql (migración en Quotations), xunit.v3, Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-11-10-detalle-pedido-comprador-design.md` (este repo; manda el contrato HTTP). Hermano: `qep-storefront/docs/superpowers/specs/2026-11-10-detalle-pedido-comprador-design.md`. Base: `docs/superpowers/specs/2026-11-09-pedidos-del-comprador-design.md` y su plan `docs/superpowers/plans/2026-11-09-pedidos-del-comprador.md`.

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\detalle-pedido-comprador`, rama `feat/detalle-pedido-comprador`. Antes de cada commit, `git branch --show-current` debe imprimir esa rama.
- Leer el `CLAUDE.md` del repo. Lo que más pesa acá: TDD con evidencia literal de RED y GREEN; no inventar campos ni códigos; conventional commits **sin atribución de IA**; `docs/estado.md` **no se toca en la rama** (se actualiza en `develop` al mergear).
- `Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y `dotnet ef`. Verificar con `Get-Process Api -ErrorAction SilentlyContinue`. Si está corriendo y no lo lanzaste tú, avisa y no lo detengas.
- Pruebas de integración: Docker arriba, `$env:Seed__Enabled = "false"` y **un proyecto a la vez** con `-- xUnit.MaxParallelThreads=4`, filtrado a la clase tocada. Nunca la solución entera.
- Todos los comandos son PowerShell y se corren desde la raíz del worktree.

## Global Constraints

- Contrato aditivo. `BuyerOrderDetailDto` gana exactamente `subtotal`, `taxAmount`, `freightTotal`, `isStorePickup`, `carrierName`, `billedTo`, `billing`, `shipping` y `buyerNotes` (spec §4.3). Sin `discountAmount`, sin `notes`, sin ningún campo de la lista de anti-objetivos (spec §2).
- Claves de `billing`: `name`, `identificationType`, `identificationNumber`, `email`, `phone`, `address`, `country`, `departmentName`, `cityName`. Claves de `shipping`: `name`, `phone`, `address`, `country`, `departmentName`, `cityName` (sin correo ni identificación).
- Identidad exacta: `subtotal + taxAmount + freightTotal == total` (`DocumentTotals.cs:48`). `freightTotal = Order.FreightSubtotal + Order.FreightTaxAmount`.
- `billedTo` es el nombre del enum `BilledPartySource` (`"FinalConsumer" | "BillingParty" | "Customer"`) que devuelve `OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing)` (`OrderBilledParty.cs:21`). `billing != null ⇔ billedTo == "BillingParty"`.
- `shipping` es `null` con `IsStorePickup` o sin fila de envío.
- `country = OrderParty.Country ?? "CO"`. En Colombia (`Country == null`), `departmentName`/`cityName` salen de `IQuotationGeographyLookup` (id ausente → `null`); fuera, `departmentName = null` y `cityName = OrderParty.CityName`.
- `identificationType` viaja con `PartyFiscalTypesParser.ToWireValue` (`PartyFiscalTypes.cs:67`): `"NIT" | "CC" | "CE" | "PASAPORTE" | "NIF" | "NIE" | "TAX_ID"`; `null` si la parte no lo tiene.
- Con `Order.AppliesVatSurplus` (`Order.cs:309`), cada línea va con `unitPrice = item.UnitPriceWithoutTax` (`OrderItem.cs:86`) y `lineTotal = item.Subtotal`; sin excedente, como hoy (`item.UnitPrice`, `item.Subtotal + item.TaxAmount`).
- `buyerNotes = Order.BuyerNotes`, nunca `Order.Notes` (D9). `BuyerNotes` se asigna **sólo** en `Order.CreateFromStore`, con `NormalizeNotes` (`Order.cs:1450`: recorte, vacío → `null`).
- Columna `quotations.orders.buyer_notes text NULL`, migración `AddOrderBuyerNotes` sobre `20261110023855_AddOrderBuyerAccount`, **sin backfill**.
- Una excepción del lookup de Geography se propaga (500); nunca se convierte en nombre `null` (spec §4.5).
- Ninguna capa de `Modules.Buyers.*` referencia `Modules.Quotations.*` ni `Modules.Geography.*`; Quotations.Application no referencia Geography (`BuyersLayerTests`, `QuotationsLayerTests`).
- Ni el gateway ni los handlers leen `OnlineStore.ShowPrices` (D6, spec §4.6).

## Review Focus

1. Un pedido creado antes de la migración (`buyer_notes NULL`) cuyo `notes` tiene texto del asesor: `buyerNotes` es `null`, nunca cae a `notes`. → `PublicBuyerOrdersApiTests.AnOrderWithoutBuyerNotesNeverShowsTheAdvisorNotes` (Task 3).
2. Geography falla (excepción) mientras se arma el detalle: el error sale tal cual y no aparece como ciudad `null`. → `BuyerOrderGatewayTests.AGeographyFailureIsNotDisguisedAsAMissingName` (Task 2).
3. Facturación y envío en la misma ciudad (el caso normal de «usar los mismos datos»): una sola llamada por diccionario, con ids distintos, y las dos partes con nombre. → `BuyerOrderGatewayTests.ColombianPlacesComeFromTheLookupOneCallPerDictionary` (Task 2).
4. Anular después de que el asesor editó las notas: la respuesta de la anulación trae la nota original y el mismo conjunto de claves que el GET. → `BuyerOrderGatewayTests.CancelReturnsTheSameExtendedDetail` (Task 2) y `PublicBuyerOrdersApiTests.CancelReturnsTheExtendedDetail` (Task 3).
5. Una nota con saltos de línea internos y espacios en los bordes: se recortan sólo los bordes. → `OrderTests.BuyerNotesKeepInnerLineBreaks` (Task 1).

## Discrepancias entre el spec y el código (gana el código)

- §4.1 cita `Order.cs:502` para `ShippingDetails.None` en `CreateFromStore`; está en `:503`. Sin efecto.
- §5 (migración) pide que «un `Order` nuevo con `BuyerNotes` se lee de vuelta» en `OrdersMigrationTests`. Ese archivo sólo trabaja con SQL y `IMigrator`, sin camino de escritura del agregado. La ida y vuelta queda en integración: checkout real con nota → `SELECT buyer_notes` → `GET` (Task 3). El mapeo lo fija `QuotationsDbContextMappingTests` (Task 1).
- §5 (integración) «País distinto de `CO` si el rig lo permite»: `BuyerOrdersRig.OpenAsync` no configura `salesCountries` (sólo lo hace `OpenRigAsync` de `PublicOrderRequestApiTests`). Queda en la unitaria (Task 2), como prevé el spec.
- §5 «Con `showPrices: false` (si el spec de precios ya está mergeado)»: `ShowPrices` no está en `develop` (sólo en `feature/tienda-mostrar-precios`, columna `store.stores.show_prices`). Va en el Task 4, condicionado al rebase.
- `StubQuotationGeographyLookup.FindCityNamesAsync` (`QuotationsTestDoubles.cs:1260`) no cuenta llamadas. Para afirmar «sin llamar al lookup» se le agregan `FindCityNamesCalls` y `LastCityRequestedIds` (Task 2).

## Mapa de archivos

Crear:
- `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrderBuyerNotes.cs` (+ `.Designer.cs`, generados por `dotnet ef`; el snapshot se actualiza solo).

Modificar:
- `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`: `BuyerNotes` y su asignación en `CreateFromStore`.
- `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`: columna, junto a `notes` (`:588`).
- `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs`: records y `ToDetailAsync`.
- `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs`: DTOs.
- `src/Bootstrapper/BuyerOrdersAdapter.cs`: mapeo.
- Pruebas: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `QuotationsDbContextMappingTests.cs`, `BuyerOrderGatewayTests.cs`, `QuotationsTestDoubles.cs`; `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs`; `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs` (sólo `Sample()`); `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs`.
- `README.md` (tabla y párrafo de `BuyerOrderDetail`, `:1153` y `:1171`), `CLAUDE.md` (un gotcha).

---

### Task 1: `Order.BuyerNotes`: dominio, columna y migración

Dominio y persistencia van juntos: una propiedad nueva sin mapear pone en rojo `QuotationsDbContextMappingTests.TheModelHasNoChangesPendingAMigration`, y un commit no debe dejar la suite en rojo.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (propiedad junto a `Notes`, `:257`; `CreateFromStore`, `:485-507`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`:588`)
- Create (generado): `…/Persistence/Migrations/<timestamp>_AddOrderBuyerNotes.cs` y `.Designer.cs`; Modify (generado): `QuotationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `QuotationsDbContextMappingTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`

**Interfaces:**
- Produces:
  - `Order.BuyerNotes : string? { get; private set; }`, con comentario que diga por qué existe (D9: `Notes` la sobrescriben `UpdateNotes`, `AddPaymentProofs` y `AttachExternalShipping`; ésta es la nota del checkout y no cambia).
  - `CreateFromStore` asigna `order.BuyerNotes = NormalizeNotes(notes);` junto a `order.BuyerAccountId = buyerAccountId;` (`:505`). Ningún otro método la toca. La firma de `CreateFromStore` no cambia.
  - Columna `buyer_notes` `text` nullable en `quotations.orders`.

- [ ] **Step 1: Escribir las pruebas de dominio que fallan** (`OrderTests.cs`). `NewBuyerStoreOrder` (`:1794`) gana `string? notes = null` y lo pasa a `CreateFromStore` en lugar del `null` actual. Los nombres empiezan por `BuyerNotes` para que el filtro los tome.

```csharp
[Theory]
[InlineData("  Entregar en portería  ", "Entregar en portería")]
[InlineData("   ", null)]
[InlineData(null, null)]
public void BuyerNotesKeepTheNormalizedCheckoutNote(string? note, string? expected)
{
    var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: note);
    Assert.Equal(expected, order.BuyerNotes);
    Assert.Equal(order.Notes, order.BuyerNotes);
}

[Fact]
public void BuyerNotesKeepInnerLineBreaks() =>
    Assert.Equal("Portería\nTorre 2", NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "  Portería\nTorre 2  ").BuyerNotes);

[Fact]
public void BuyerNotesSurviveTheAdvisorNoteEdit()
// UpdateNotes("Texto del asesor", Now.AddHours(1)) → Notes == "Texto del asesor"; BuyerNotes == "Entregar en portería"

[Fact]
public void BuyerNotesSurviveAPaymentProofWithAnotherNote()
// AddPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 5_000m)], OrderPaymentStatus.PaymentPending,
//   "Comprobante recibido", Advisor, Now.AddHours(1)) → Notes == "Comprobante recibido"; BuyerNotes == "Entregar en portería"

[Fact]
public void BuyerNotesSurviveShippingAndBillingEdits()
// SetShipping(new ShippingDetails(null, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false), Advisor, Now)
// y SetBilling(null, true, false, null, null, Advisor, Now) → BuyerNotes sin cambios

[Fact]
public void BuyerNotesAreNullOutsideTheStore()
// NewOrder(notes: "Nota manual") (CreateManual), Order.FromQuotation(…, ConvertedQuotation(), …, "Pago verificado", …)
// y NewShopifyOrder(notes: "Nota de Shopify") (CreateExternal) → los tres con Notes no nulo y BuyerNotes == null
```

- [ ] **Step 2: Escribir la prueba de mapeo que falla** (`QuotationsDbContextMappingTests.cs`, junto a `TheOrderBuyerAccountMapsToItsColumnAndFilteredIndex`, `:600`):

```csharp
[Fact]
public void TheOrderBuyerNotesMapsToANullableTextColumn()
{
    using var context = new QuotationsDbContextFactory().CreateDbContext([]);
    var property = context.Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.BuyerNotes))!;
    Assert.Equal("buyer_notes", property.GetColumnName());
    Assert.Equal("text", property.GetColumnType());
    Assert.True(property.IsNullable);
}
```

- [ ] **Step 3: Escribir la prueba de migración** (`OrdersMigrationTests.cs`, debajo de `AddOrderBuyerAccountAddsTheColumnAndTheIndex`; reutiliza `LastMigrationBeforeOwnData`, `QuotationWithLinesSql`, `OrderId`, `MigrationId`, `ListAsync`, `ScalarAsync`):

```csharp
[Fact]
public async Task AddOrderBuyerNotesAddsANullableTextColumnAndLeavesExistingOrdersNull()
// MigrateAsync(LastMigrationBeforeOwnData) → ExecuteAsync(QuotationWithLinesSql) → MigrateAsync(MigrationId(context, "_AddOrderBuyerAccount"));
// columnSql = "SELECT data_type || ':' || is_nullable FROM information_schema.columns
//              WHERE table_schema = 'quotations' AND table_name = 'orders' AND column_name = 'buyer_notes'"
// Assert.Empty(ListAsync(columnSql));
// MigrateAsync(MigrationId(context, "_AddOrderBuyerNotes"));
// Assert.Equal(["text:YES"], ListAsync(columnSql));
// Assert.Equal(1L, count(*) de orders WHERE id = OrderId AND buyer_notes IS NULL);
// Assert.Equal("Entregar en portería\n\nPago verificado", SELECT notes …)   // notes no se copia
// Termina con MigrateAsync(null) (gotcha de CLAUDE.md).
```

- [ ] **Step 4: RED**

```powershell
Get-Process Api -ErrorAction SilentlyContinue
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~BuyerNotes"
```

Esperado: no compila, `error CS1061: 'Order' does not contain a definition for 'BuyerNotes'`. Pega la salida literal en el informe de la tarea. La de migración se corre en el Step 7, cuando exista la migración.

- [ ] **Step 5: Implementar** `Order.BuyerNotes` y su asignación (bloque Interfaces), y el mapeo en `QuotationsDbContext` junto a `notes`:

```csharp
// D9 (spec 2026-11-10): la nota que escribió el comprador en el checkout; Notes es la del asesor.
order.Property(value => value.BuyerNotes).HasColumnName("buyer_notes").HasColumnType("text");
```

- [ ] **Step 6: Generar la migración** (factory de diseño, no `--startup-project`):

```powershell
dotnet ef migrations add AddOrderBuyerNotes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

Revisa que el `Up` tenga **sólo** `AddColumn<string>(name: "buyer_notes", schema: "quotations", table: "orders", type: "text", nullable: true)`, que el `Down` tenga sólo el `DropColumn`, que no haya `Sql(...)` de backfill y que el diff del snapshot sea sólo esa propiedad.

- [ ] **Step 7: GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~BuyerNotes|FullyQualifiedName~TheModelHasNoChangesPendingAMigration"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter FullyQualifiedName~OrdersMigrationTests -- xUnit.MaxParallelThreads=4
dotnet build src/Api/Api.csproj
```

Esperado: los tres `dotnet test` con `Failed: 0`; build con `0 Error(s)`. Pega las salidas literales.

- [ ] **Step 8: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): keep the buyer checkout note apart from the advisor notes"
```

---

### Task 2: `BuyerOrderGateway`: detalle ampliado

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubQuotationGeographyLookup`, `:1230-1273`)
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs` (sólo `SampleDetail()`, `:53-56`, para que compile; el Task 3 lo amplía)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/BuyerOrderGatewayTests.cs`

**Interfaces:**
- Consumes: `Order.BuyerNotes` (Task 1); `IQuotationGeographyLookup.FindDepartmentNamesAsync` / `FindCityNamesAsync` (`IQuotationGeographyLookup.cs`, ya registrado en DI en `QepServiceCollectionExtensions.cs:1511`); `OrderBilledParty.SourceFor`; `PartyFiscalTypesParser.ToWireValue`.
- Produces (namespace `Modules.Quotations.Application`):

```csharp
public sealed record BuyerOrderBilling(
    string Name, string? IdentificationType, string? IdentificationNumber, string? Email, string? Phone,
    string? Address, string Country, string? DepartmentName, string? CityName);

public sealed record BuyerOrderShipping(
    string? Name, string? Phone, string? Address, string Country, string? DepartmentName, string? CityName);

// Los nueve campos nuevos van DESPUÉS de CanCancel: el orden posicional de los existentes no cambia.
public sealed record BuyerOrderDetail(
    Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    decimal Total, string Currency, int LineCount, string FirstLineName,
    IReadOnlyList<BuyerOrderLine> Lines, DateTimeOffset? CancelledAt, bool CanCancel,
    decimal Subtotal, decimal TaxAmount, decimal FreightTotal, bool IsStorePickup, string? CarrierName,
    string BilledTo, BuyerOrderBilling? Billing, BuyerOrderShipping? Shipping, string? BuyerNotes);

public sealed class BuyerOrderGateway(
    IOrderRepository orders, IQuotationsUnitOfWork unitOfWork, IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents, IClock clock, IQuotationGeographyLookup geography)
```

  - `BuyerOrderBilling.Name` no es nullable porque `billing` sólo existe con `BilledPartySource.BillingParty`, que exige nombre (`OrderBilledParty.cs:27`).
  - `ToDetail(Order)` pasa a `private async Task<BuyerOrderDetail> ToDetailAsync(Order order, CancellationToken cancellationToken)`. Lo usan `FindAsync` y las dos ramas de `CancelAsync` (`:94`, `:112`). `ToSummary` no cambia.
  - Armado: `billedTo = OrderBilledParty.SourceFor(order.BillsToFinalConsumer, order.Billing)`; `billingParty = billedTo == BillingParty ? order.Billing : null`; `shippingParty = order.IsStorePickup ? null : order.Shipping`. De las partes no nulas con `Country == null` (Colombia) salen los `DepartmentId` y `CityId` no nulos, `Distinct()`. Cada diccionario se pide **una vez y sólo si hay ids**; si no hay, diccionario vacío sin llamar. La facturación que no se muestra no se consulta [decisión del plan: no cambia el contrato].
  - Línea: `private static BuyerOrderLine ToLine(OrderItem item, bool appliesVatSurplus)` según Global Constraints. El comentario de `BuyerOrderLine` (`:6-7`) explica la regla de §4.7 (sin él, el siguiente lo «simplifica»).
  - `BilledTo = billedTo.ToString()`, `FreightTotal = order.FreightSubtotal + order.FreightTaxAmount`, `IsStorePickup = order.IsStorePickup`, `CarrierName = order.CarrierName`, `BuyerNotes = order.BuyerNotes`.
  - Una excepción del lookup no se captura.
- Produces en `StubQuotationGeographyLookup`: `public int FindCityNamesCalls { get; private set; }` y `public IReadOnlyCollection<Guid> LastCityRequestedIds { get; private set; } = [];`, con el mismo patrón que `FindDepartmentNamesCalls`.

- [ ] **Step 1: Preparar el fixture de pruebas** (`BuyerOrderGatewayTests.cs`):
  - Campos `static readonly Guid Department, City`. `Parties()` los usa en lugar de los ids aleatorios y gana `bool vatSurplus = false` (lo pasa como `BillingVatSurplus`) y `QuotationPartyDetails? billing = null`.
  - `StoreOrder(...)` gana `string? notes = null` y `QuotationParties? parties = null`, que pasa a `CreateFromStore`.
  - `Gateway(...)` gana `IQuotationGeographyLookup? geography = null`, por defecto `new StubQuotationGeographyLookup(new Dictionary<Guid, string> { [City] = "Cali" }, departmentNames: new Dictionary<Guid, string> { [Department] = "Valle del Cauca" })`.
  - Constante local `Charged = new ShippingDetails(null, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false)`.
  - Clase local `ThrowingGeographyLookup : IQuotationGeographyLookup`, que lanza `new InvalidOperationException("geography down")` en los tres métodos.

- [ ] **Step 2: Escribir las pruebas que fallan** (las existentes siguen en verde sin cambios):

```csharp
[Fact] AFreshStoreOrderCarriesItsTotalsWithoutFreightOrCarrier
  // StoreOrder(Buyer) → (Subtotal, TaxAmount) == (order.Subtotal, order.TaxAmount); FreightTotal == 0m;
  // Subtotal + TaxAmount + FreightTotal == Total; IsStorePickup false; CarrierName null; BilledTo "BillingParty"
[Fact] AChargedFreightAddsToTheIdentityAndNamesTheCarrier
  // order.SetShipping(Charged, Advisor, Now) → FreightTotal == order.FreightSubtotal + order.FreightTaxAmount; FreightTotal > 0m;
  // Subtotal + TaxAmount + FreightTotal == Total; CarrierName "Coordinadora"
[Fact] WithVatSurplusTheLinesGoWithoutTaxAndAddUpToTheTotal
  // StoreOrder(Buyer, parties: Parties(vatSurplus: true)) + SetShipping(Charged) → order.AppliesVatSurplus true (precondición);
  // TaxAmount == 0m; cada línea i: UnitPrice == item.UnitPriceWithoutTax, LineTotal == item.Subtotal (por Position);
  // Math.Round(Lines.Sum(LineTotal), 2, MidpointRounding.AwayFromZero) + FreightTotal == Total; Subtotal + TaxAmount + FreightTotal == Total
[Fact] CancelReturnsTheSameExtendedDetail
  // StoreOrder(Buyer, notes: "Entregar en portería", parties: Parties(vatSurplus: true)); order.UpdateNotes("Texto del asesor", Now);
  // CancelAsync → Status "Cancelled"; BuyerNotes "Entregar en portería"; Billing no nulo; Lines[0].LineTotal == item.Subtotal
[Theory] TheBillingCarriesTheWireDocumentTypeAndCountry
  // (Cc, null, "CC", "CO"), (Nit, null, "NIT", "CO"), (TaxId, "US", "TAX_ID", "US"). Para "US": DepartmentId/CityId null, CityName "Miami".
  // Billing: Name "Ana Pérez", IdentificationType esperado, IdentificationNumber "1020304050", Email "ana@example.com",
  // Phone "+573001112233", Address "Calle 1 # 2-3", Country esperado
[Fact] ColombianPlacesComeFromTheLookupOneCallPerDictionary
  // facturación y envío con el mismo Department/City → Billing y Shipping con ("Valle del Cauca", "Cali");
  // geography.FindDepartmentNamesCalls == 1, FindCityNamesCalls == 1; LastDepartmentRequestedIds == [Department]; LastCityRequestedIds == [City]
[Fact] AnIdTheLookupDoesNotKnowIsNull
  // lookup vacío → Billing.DepartmentName null, Billing.CityName null; Country "CO"
[Fact] AForeignPartyTakesItsCityAsTextAndSkipsTheLookup
  // billing/shipping con Country "ES", IdentificationType Nif, DepartmentId/CityId null, CityName "Madrid" →
  // Billing (Country, DepartmentName, CityName) == ("ES", null, "Madrid"); Shipping igual; FindDepartmentNamesCalls == 0; FindCityNamesCalls == 0
[Fact] TheShippingCarriesNoEmailOrDocument
  // Shipping: Name "Ana Pérez", Phone "+573001112233", Address "Calle 1 # 2-3", Country "CO"
  // (el record no tiene Email ni identificación: lo fija el Interfaces)
[Fact] BillingToTheFinalConsumerHidesTheBillingParty
  // order.SetBilling(null, true, false, null, null, Advisor, Now) → BilledTo "FinalConsumer"; Billing null; Shipping no nulo
[Fact] BillingToTheCustomerHidesTheBillingParty
  // order.SetBilling(null, false, false, null, null, Advisor, Now) → BilledTo "Customer"; Billing null
[Fact] AStorePickupHasNoShipping
  // Order.CreateManual(…, new QuotationParties(billing, shipping, IsStorePickup: true, BillingWithRetention: false, BillingVatSurplus: false),
  //   Account, ShippingDetails.None, [Line("Aceite")], OrderPaymentStatus.PaymentPending, null, Advisor, [], Now); order.LinkBuyerAccount(Buyer)
  // → IsStorePickup true; Shipping null; Billing no nulo (el gateway no mira el origen)
[Fact] BuyerNotesComeFromTheCheckoutNotFromTheAdvisor
  // StoreOrder(Buyer, notes: "Entregar en portería"); order.UpdateNotes("Texto del asesor", Now) → FindAsync.BuyerNotes == "Entregar en portería"
[Fact] AGeographyFailureIsNotDisguisedAsAMissingName
  // Gateway(orders, geography: new ThrowingGeographyLookup()) → Assert.ThrowsAsync<InvalidOperationException>(FindAsync).Message == "geography down"
```

- [ ] **Step 3: RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~BuyerOrderGatewayTests
```

Esperado: no compila. Errores del tipo `error CS1729: 'BuyerOrderGateway' does not contain a constructor that takes 6 arguments` y `error CS1061: 'BuyerOrderDetail' does not contain a definition for 'Subtotal'`. Pega la salida literal.

- [ ] **Step 4: Implementar** los records, el constructor y `ToDetailAsync` según el bloque Interfaces. Agrega los dos miembros a `StubQuotationGeographyLookup`. En `BuyerOrdersAdapterTests.SampleDetail()` agrega los nueve argumentos nuevos con valores neutros (`0m, 0m, 0m, false, null, "BillingParty", null, null, null`) para que Bootstrapper.UnitTests compile. El DI no cambia: `IQuotationGeographyLookup` ya está registrado y el contenedor resuelve el parámetro nuevo.

- [ ] **Step 5: GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerOrdersAdapterTests
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~QuotationsLayerTests|FullyQualifiedName~CompositionRootTests"
dotnet build src/Api/Api.csproj
```

Esperado: `Failed: 0` en los tres; build `0 Error(s)`. Pega las salidas literales.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations/Modules.Quotations.Application tests/Modules/Quotations tests/Bootstrapper
git commit -m "feat(quotations): extend the buyer order detail with totals, parties and the buyer note"
```

---

### Task 3: Contrato de Buyers, adaptador y prueba de punta a punta

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs` (`:6-15`)
- Modify: `src/Bootstrapper/BuyerOrdersAdapter.cs` (`ToDetail`, `:44-49`)
- Modify: `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs` (`Sample()`, `:158-159`, sólo para compilar)
- Modify: `README.md` (`:1153`, `:1171`), `CLAUDE.md` (gotchas)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs`

**Interfaces:**
- Consumes: `BuyerOrderDetail`, `BuyerOrderBilling`, `BuyerOrderShipping` (Task 2).
- Produces (namespace `Modules.Buyers.Application`): copias con los mismos nombres y tipos, sufijo `Dto`:

```csharp
public sealed record BuyerOrderBillingDto(
    string Name, string? IdentificationType, string? IdentificationNumber, string? Email, string? Phone,
    string? Address, string Country, string? DepartmentName, string? CityName);
public sealed record BuyerOrderShippingDto(
    string? Name, string? Phone, string? Address, string Country, string? DepartmentName, string? CityName);
public sealed record BuyerOrderDetailDto(
    /* los 12 de hoy, sin cambios */ …, bool CanCancel,
    decimal Subtotal, decimal TaxAmount, decimal FreightTotal, bool IsStorePickup, string? CarrierName,
    string BilledTo, BuyerOrderBillingDto? Billing, BuyerOrderShippingDto? Shipping, string? BuyerNotes);
```

  - El comentario de `BuyerOrderDetailDto` dice (regla BFF del repo): la identidad `subtotal + taxAmount + freightTotal == total`; que no hay fila de descuento porque `subtotal` ya lo incluye (D8); que con excedente de IVA las líneas van sin IVA para que sumen (D5); que `billing` es `null` salvo `billedTo == "BillingParty"` (D7); que `buyerNotes` es la nota del checkout, nunca la del asesor (D9); y que el backend manda montos aunque la tienda los oculte, porque los quita el servidor del storefront (D6).
  - `BuyerOrdersAdapter.ToDetail` copia los nueve campos; `Billing`/`Shipping` `null` se quedan en `null`.

- [ ] **Step 1: Escribir las pruebas de integración que fallan** (`PublicBuyerOrdersApiTests.cs`). `SeedBuyerOrderAsync` (`:97`) gana `Action<Dictionary<string, object?>>? tweak = null`, que pasa a `rig.CheckoutBody(tweak: tweak)`. Helpers locales: `Keys(JsonNode node) => node.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray()`; `PlaceNameAsync(rig, "departments" | "cities", id)` con `SELECT name FROM geography.<tabla> WHERE id = @id`; `BillingPhoneAsync(rig, orderId)` con `SELECT phone FROM quotations.order_parties WHERE order_id = @order AND role = 'Billing'`. El teléfono se compara contra lo guardado porque el checkout lo normaliza. Constantes:

```csharp
private static readonly string[] DetailKeys = [
    "billedTo", "billing", "buyerNotes", "canCancel", "cancelledAt", "carrierName", "createdAt", "currency", "firstLineName",
    "freightTotal", "id", "isStorePickup", "lineCount", "lines", "number", "paymentStatus", "shipping", "status", "subtotal",
    "taxAmount", "total"];
private static readonly string[] BillingKeys = [
    "address", "cityName", "country", "departmentName", "email", "identificationNumber", "identificationType", "name", "phone"];
private static readonly string[] ShippingKeys = ["address", "cityName", "country", "departmentName", "name", "phone"];
private const string BuyerNote = "Entregar en portería";
```

```csharp
[Fact] TheDetailCarriesTheBillingTotalsAndTheBuyerNote
  // SeedBuyerOrderAsync(rig, account, b => b["note"] = "  Entregar en portería  ") → GET detalle:
  // billedTo "BillingParty"; isStorePickup false; carrierName null; freightTotal 0;
  // subtotal/taxAmount == los de rig.OrderAsync(orderId)["order"]; subtotal + taxAmount + freightTotal == total;
  // billing: name "Ana Pérez", identificationType "CC", identificationNumber "123456", email "ana@test.co",
  //   phone == BillingPhoneAsync, address "Calle 1 # 2-3", country "CO",
  //   departmentName == PlaceNameAsync("departments", rig.Department), cityName == PlaceNameAsync("cities", rig.City);
  // buyerNotes == BuyerNote; y rig.TextAsync("SELECT buyer_notes FROM quotations.orders WHERE id = @order") == BuyerNote (ida y vuelta)
[Fact] UsingTheSameDataShipsToTheBillingWithoutEmailOrDocument
  // checkout por defecto ("shipping" = null) → shipping (name, phone, address, country, departmentName, cityName) == los mismos de billing;
  // Keys(shipping) == ShippingKeys
[Fact] TheDetailHasExactlyTheContractKeys
  // Keys(detail) == DetailKeys; Keys(detail["billing"]) == BillingKeys; Keys(detail["shipping"]) == ShippingKeys
[Fact] TheConsoleNoteEditDoesNotChangeTheBuyerNote
  // con nota BuyerNote; rig.Console PUT /api/v1/tenants/{TenantId}/orders/{orderId} con If-Match "\"{order.version}\"" y
  // new SaveOrderEditsRequest([new OrderEditItemRequest(rig.Variant, 2m)], null, "Texto del asesor", IsRetail: order.isRetail) → 200;
  // rig.OrderAsync(orderId)["order"]["notes"] == "Texto del asesor"; GET detalle → buyerNotes == BuyerNote
[Fact] AnOrderWithoutBuyerNotesNeverShowsTheAdvisorNotes
  // checkout sin nota; UPDATE quotations.orders SET notes = 'Texto del asesor', buyer_notes = NULL WHERE id = @order
  // (un pedido anterior a la migración) → buyerNotes null (JsonNode null)
[Fact] CancelReturnsTheExtendedDetail
  // con nota BuyerNote → POST …/cancel → 200; Keys == DetailKeys; status "Cancelled"; buyerNotes == BuyerNote; billing no nulo;
  // subtotal + taxAmount + freightTotal == total
```

Los 404 de siempre (otra cuenta, otro tenant, host desconocido) ya los cubren `AnotherBuyersOrderIs404OrderNotFound`, `AnOrderOfTheSameBuyerInAnotherTenantIs404` y `AnUnknownHostIs404StorePublicNotFound`, que siguen sin cambios.

- [ ] **Step 2: RED de integración** (Docker arriba)

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~PublicBuyerOrdersApiTests -- xUnit.MaxParallelThreads=4
```

Esperado: compila; las seis nuevas FAIL porque el JSON no trae `billing`, `buyerNotes`, etc. (`NullReferenceException` o `Assert.Equal() Failure` en las claves). Las existentes, PASS. Pega la salida literal.

- [ ] **Step 3: Escribir las pruebas del adaptador que fallan** (`BuyerOrdersAdapterTests.cs`). `SampleDetail()` pasa a valores reales: `Subtotal 84.03m, TaxAmount 15.97m, FreightTotal 19.5m, IsStorePickup false, CarrierName "Coordinadora", BilledTo "BillingParty"`, un `BuyerOrderBilling` y un `BuyerOrderShipping` completos y `BuyerNotes "Entregar en portería"`. Constructor del fake: `RecordingBuyerOrderGateway(counter, detail, cancelFailure)` sin cambios.

```csharp
[Fact] FindMapsTheTotalsPartiesAndBuyerNotes
  // cada uno de los nueve campos nuevos igual al de SampleDetail(); Billing y Shipping comparados record a record
  // (new BuyerOrderBillingDto(...) == detail.Billing)
[Fact] AMissingBillingAndShippingStayNull
  // SampleDetail() with { Billing = null, Shipping = null, BilledTo = "FinalConsumer" } → Billing null, Shipping null, BilledTo "FinalConsumer"
  // (agregar a CreateAdapter un parámetro opcional BuyerOrderDetail? detail)
[Fact] TheDetailDtosMirrorTheGatewayRecords
  // por cada par (BuyerOrderDetail, BuyerOrderDetailDto), (BuyerOrderBilling, BuyerOrderBillingDto),
  // (BuyerOrderShipping, BuyerOrderShippingDto), (BuyerOrderLine, BuyerOrderLineDto):
  // typeof(A).GetProperties().Select(p => p.Name).Order() == typeof(B)…; un campo que se agregue a un lado y no al otro pone esto en rojo
```

- [ ] **Step 4: RED del adaptador**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerOrdersAdapterTests
```

Esperado: no compila, `error CS0246: The type or namespace name 'BuyerOrderBillingDto' could not be found`. Pega la salida literal.

- [ ] **Step 5: Implementar** los DTOs y su comentario, el mapeo del adaptador y el `Sample()` de `BuyerOrdersHandlersTests` (nueve argumentos nuevos con valores neutros, igual que el Task 2).

- [ ] **Step 6: GREEN**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~QuotationsLayerTests|FullyQualifiedName~StoreLayerTests|FullyQualifiedName~CompositionRootTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~PublicBuyerOrdersApiTests -- xUnit.MaxParallelThreads=4
dotnet build src/Api/Api.csproj
```

Esperado: `Failed: 0` en los cuatro; build `0 Error(s)`. Pega las salidas literales.

- [ ] **Step 7: Documentación**
  - `README.md:1153`: `200` `BuyerOrderDetail` (`lines`, `cancelledAt`, `canCancel`, totales, partes y `buyerNotes`). `:1171`: `BuyerOrderDetail` suma `lines`, `cancelledAt`, `subtotal`, `taxAmount`, `freightTotal` (`subtotal + taxAmount + freightTotal == total`), `isStorePickup`, `carrierName`, `billedTo` (`FinalConsumer | BillingParty | Customer`), `billing` (sólo con `BillingParty`), `shipping` (sin correo ni documento; `null` con recogida) y `buyerNotes`. Con excedente de IVA, las líneas van sin IVA.
  - `CLAUDE.md`, sección de gotchas, una entrada: «**La nota del comprador es `Order.BuyerNotes`, no `Order.Notes`** (spec 2026-11-10, D9). `Notes` es del asesor: la sobrescriben `UpdateNotes`, `AddPaymentProofs` y `AttachExternalShipping`. `BuyerNotes` la escribe sólo `CreateFromStore` y nada más la toca; un método nuevo que la edite le muestra al comprador un texto que no escribió. Los pedidos anteriores a `AddOrderBuyerNotes` la tienen en `NULL` a propósito (sin backfill).»

- [ ] **Step 8: Commit**

```powershell
git branch --show-current
git add src/Modules/Buyers src/Bootstrapper tests/Bootstrapper tests/Modules/Buyers tests/Modules/Store README.md CLAUDE.md
git commit -m "feat(buyers): expose the extended buyer order detail to the storefront"
```

---

### Task 4: Montos con `showPrices = false` (sólo después del rebase con precios)

Se ejecuta **sólo** si `feature/tienda-mostrar-precios` ya está en `develop` y esta rama se rebasó sobre él: `git merge-base --is-ancestor 04fe23e9 HEAD` sin error. Si no, se deja sin marcar y se avisa en el informe final.

**Files:**
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs`

**Interfaces:**
- Consumes: columna `store.stores.show_prices` (`20261111033717_AddStoreShowPrices`, rama de precios).

- [ ] **Step 1: Escribir la prueba**

```csharp
[Fact]
public async Task WithShowPricesOffTheDetailStillCarriesAmounts()
// pedido del comprador; UPDATE store.stores SET show_prices = false WHERE tenant_id = @tenant →
// GET detalle: total, subtotal, taxAmount, freightTotal y lines[0].unitPrice / lineTotal presentes y no nulos (D6: los quita el storefront)
```

- [ ] **Step 2: Correr**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~WithShowPricesOffTheDetailStillCarriesAmounts -- xUnit.MaxParallelThreads=4
```

Esperado: PASS en la primera corrida. Es una prueba de no regresión de un no-objetivo (D6: el backend no cambia), así que no tiene RED posible. Pega la salida literal y dilo así en el informe.

- [ ] **Step 3: Commit**

```powershell
git branch --show-current
git add tests/Modules/Store
git commit -m "test(buyers): keep amounts in the buyer order detail when the store hides prices"
```

---

## Cierre de la rama

- [ ] Corridas finales, una a la vez: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`, `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests`, `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`, `dotnet test tests/ArchitectureTests/ArchitectureTests`; con `$env:Seed__Enabled = "false"`: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter FullyQualifiedName~OrdersMigrationTests -- xUnit.MaxParallelThreads=4` y `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicBuyerOrdersApiTests|FullyQualifiedName~PublicOrderRequestApiTests" -- xUnit.MaxParallelThreads=4`. Esperado: `Failed: 0` en todas.
- [ ] `docs/estado.md` **no** se toca en la rama. Al mergear a `develop` (no a `main`) se agrega la fila y el Registro: backend primero, porque trae la migración `AddOrderBuyerNotes` (spec §6).
