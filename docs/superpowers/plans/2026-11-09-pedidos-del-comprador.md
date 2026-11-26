# Pedidos del comprador (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el comprador con sesión de la tienda pública vea sus pedidos de ese tenant y anule los que siguen `Pending` con pago `PaymentPending`, por rutas bajo `/api/v1/public/stores/{host}/buyer/orders`.

**Architecture:** `Order` gana `BuyerAccountId` (nullable, sin FK) y la regla de dominio `CanBeCancelledByBuyer`/`CancelByBuyer`. Quotations.Application expone un gateway nuevo (`BuyerOrderGateway`, hermano de `StoreOrderGateway`) que lista, lee, anula y vincula por `(TenantId, BuyerAccountId)`. Buyers.Application declara el puerto `IBuyerOrders` y tres handlers que resuelven host → sesión → puerto; el adaptador vive en `src/Bootstrapper` con un scope de DI por llamada. Las rutas se suman al grupo existente de `PublicBuyerEndpoints`. El sellado de `BuyerAccountId` en el checkout y el enlace del alta diferida son el **bloque final**, después del rebase sobre `develop` con pedidos directos ya mergeado (D4).

**Tech Stack:** .NET 10, EF Core + Npgsql (migración en Quotations), FluentValidation, xunit.v3, Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-11-09-pedidos-del-comprador-design.md` (este repo; manda el contrato HTTP). Hermanos: `qep-storefront/docs/superpowers/specs/2026-11-09-pedidos-del-comprador-design.md` y, sólo para D4, `docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`.

## Antes de empezar

- Leer `CLAUDE.md` del repo. Reglas que pesan acá: TDD con evidencia literal; no inventar códigos ni campos; conventional commits sin atribución de IA; `docs/estado.md` **no se toca en la rama**.
- `Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y `dotnet ef`. Verificar antes con `Get-Process Api -ErrorAction SilentlyContinue`; si está corriendo y no lo lanzaste tú, avisar, no detenerlo.
- Pruebas de integración: Docker, `$env:Seed__Enabled = "false"` y **un proyecto a la vez** con `-- xUnit.MaxParallelThreads=4`. Nunca la solución entera.
- Todos los comandos son PowerShell y se corren desde la raíz del worktree.

## Global Constraints

- Ninguna capa de `Modules.Buyers.*` referencia `Modules.Quotations.*`, ni Quotations a Buyers, ni Store a ninguno de los dos (`BuyersLayerTests`, `QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`, `StoreLayerTests.NoStoreLayerReferencesBuyers`). Los cruces van por puertos declarados en el módulo que los necesita y adaptadores en `src/Bootstrapper`.
- `Modules.Quotations.Domain` sólo referencia `BuildingBlocks.Domain`: **no puede lanzar `ResourceConflictException`** (vive en `BuildingBlocks.Application`). La traducción a 409 ocurre en Application (ver «Discrepancias»).
- Anulación del comprador sólo con `Order.Status == Pending` **y** `Order.PaymentStatus == PaymentPending` (D5). Lo demás responde 409 `order.order.not_cancellable_by_buyer` (código nuevo).
- Un pedido ajeno, de otro tenant o inexistente responde siempre 404 `order.order.not_found` (un solo código). Host desconocido: 404 `store.public.not_found`. Sesión inválida: 401 `store.buyer.session_invalid`.
- `GET /orders`: `page >= 1`, `pageSize` en `1..50`; fuera de rango 422 `validation.failed`. Orden `CreatedAt DESC, OrderNumber DESC`.
- `status` y `paymentStatus` viajan con el nombre del enum (`"Pending"`, `"PaymentPending"`), como `OrderDto`. `lineTotal = Subtotal + TaxAmount`. `canCancel` lo calcula el backend y viaja **en el ítem del listado y en el detalle** (spec §4.5, decisión del diseñador 2026-11-09).
- Sólo se guarda el id de la cuenta en el pedido; nunca correo ni nombre. Sin backfill (D3). Sin FK a `buyers.accounts`.
- El motivo fijo de anulación es `Cancelado por el comprador desde la tienda`; `CancelledBy = null`; historial `OrderHistoryEventType.Cancelled` con `memberId: null` y `OrderChangeSummary.Cancelled(reason)`; auditoría `PublishByIntegration(tenantId, "quotation.order.cancelled", orderId, "success", now)`; evento `lifecycleEvents.PublishOrderCancelled(order, now)`.
- Una `RequestConcurrencyException` al guardar la anulación se traduce en el gateway a 409 (nunca 412 hacia el storefront), releyendo el pedido y reintentando **una** vez si sigue anulable.
- Las tareas 10 y 11 se ejecutan **sólo** después del rebase sobre `develop` con pedidos directos mergeado (D4).

## Review Focus

1. Un comprador anula y, en la misma ventana, el asesor registra un comprobante: el perdedor tiene que ver 409 y el pedido quedar con el comprobante, nunca anulado con pago. → `BuyerOrderGatewayTests.ACancelThatLosesTheRaceRereadsAndAnswersConflict` (Task 4) y `PublicBuyerOrdersApiTests.APendingOrderWithPaymentIsNotCancellable` (Task 7).
2. Un `orderId` válido del mismo comprador pero de otro tenant: 404 `order.order.not_found`, sin pista de que existe. → `PublicBuyerOrdersApiTests.AnOrderOfTheSameBuyerInAnotherTenantIs404` (Task 7).
3. Un `pageSize` de 1000 o un `page` 0 no deben recorrer la tabla ni dar 500: 422 `validation.failed` antes de tocar la sesión o el repositorio. → `BuyerOrdersHandlersTests.ListRejectsPageAndPageSizeOutOfRange` (Task 5) y `PublicBuyerOrdersApiTests.ListRejectsAnOutOfRangePageSize` (Task 7).
4. Un segundo `POST …/cancel` sobre el pedido ya anulado responde 409 y no escribe otra entrada de historial ni otro evento. → `PublicBuyerOrdersApiTests.ASecondCancelIs409AndWritesNothing` (Task 7).
5. La consola pinta el historial de un pedido anulado por el comprador sin `member_id` y sin 500. → `PublicBuyerOrdersApiTests.TheConsoleDetailShowsTheBuyerCancellation` (Task 7).

## Discrepancias entre el spec y el código (gana el código)

- **§4.4:** el spec hace que `CancelByBuyer` (Domain) lance `ResourceConflictException`. `Modules.Quotations.Domain.csproj` referencia sólo `BuildingBlocks.Domain`; `ResourceConflictException` está en `BuildingBlocks.Application/ApplicationExceptions.cs`. Acá el dominio lanza `QuotationsDomainException` con el código nuevo (defensa en profundidad) y `BuyerOrderGateway` comprueba `CanBeCancelledByBuyer` antes y lanza la `ResourceConflictException` (409).
- **§4.6:** `QuotationsPersistenceTests` no existe. La prueba de migración va en `OrdersMigrationTests` (Quotations.IntegrationTests), que ya tiene `MigrateAsync`, `MigrationId` y las consultas a `pg_indexes`.
- **§4.6:** `PublicBuyerOrdersApiTests` «(Buyers)»: `BuyersApiHarness` reemplaza `IBuyerStorefronts` por `FakeBuyerStorefronts` y no siembra tenant, tienda ni catálogo; el único harness que crea un `Order` por el camino existente («Tomar») es `StoreInboxRig` (`tests/Modules/Store/Modules.Store.IntegrationTests`). La prueba vive ahí.
- **§4.2(2):** `feat/store-orders-direct` ya no tiene sólo el commit de documentación (`627242a4` trae la configuración del asesor), pero el handler del checkout y `BuyerSignupRequestedMessage.OrderRequestId` siguen como hoy: el bloque D4 sigue siendo al final.

## Mapa de archivos

Crear:
- `src/Modules/Quotations/Modules.Quotations.Domain/BuyerOrderErrors.cs` — código `order.order.not_cancellable_by_buyer` y el motivo fijo.
- `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs` — puerto, records y `BuyerOrderGateway`.
- `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrderBuyerAccount.cs` (+ Designer, generados por `dotnet ef`).
- `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs` — puerto `IBuyerOrders`, DTOs, query/command, validador y handlers.
- `src/Bootstrapper/BuyerOrdersAdapter.cs` — Buyers → Quotations, un scope por llamada.
- `tests/Modules/Quotations/Modules.Quotations.UnitTests/BuyerOrderGatewayTests.cs`
- `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs`
- `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs`
- Bloque D4: `src/Bootstrapper/BuyerOrderLinker.cs`.

Modificar:
- `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` — `BuyerAccountId`, `CreateFromStore`, `CanBeCancelledByBuyer`, `CancelByBuyer`.
- `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs` y `…Infrastructure/Persistence/OrderRepository.cs` — dos consultas por comprador.
- `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` — columna e índice.
- `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs` — `BuyerStorefront` gana `TenantId`.
- `src/Modules/Buyers/Modules.Buyers.Api/PublicBuyerEndpoints.cs` — tres rutas.
- `src/Bootstrapper/BuyerStorefronts.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`.
- `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `OrderStoreFactoryTests.cs`, `QuotationsDbContextMappingTests.cs`, `QuotationsTestDoubles.cs` (`StubOrderListRepository`).
- `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`.
- `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyersApiHarness.cs` (`FakeBuyerStorefronts` con `TenantId`).
- `tests/ArchitectureTests/ArchitectureTests/BuyersLayerTests.cs`.
- `README.md` (§ Cuentas de comprador, tabla del contrato), `CLAUDE.md` (gotcha nuevo).
- Bloque D4: `src/Modules/Store/Modules.Store.Application/IStoreBuyerAccounts.cs`, el handler del checkout y `StoreOrderDraft` (Store y Quotations), `src/Bootstrapper/StoreBuyerAccounts.cs`, `StoreOrdersAdapter.cs`, `src/Modules/Buyers/Modules.Buyers.Application/BuyerSignup.cs`, `Ports.cs`, `…Infrastructure/Signup/BuyerSignupRequestedWorker.cs` y sus pruebas.

---

### Task 1: Worktree y rama

**Files:** ninguno del repo (sólo git).

- [ ] **Step 1: Comprobar `develop` y crear el worktree**

```powershell
Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git fetch origin
git branch --show-current
git worktree add -b feature/pedidos-del-comprador "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pedidos-del-comprador" develop
Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\pedidos-del-comprador"
git branch --show-current
```

Esperado: la última línea imprime `feature/pedidos-del-comprador`.

- [ ] **Step 2: Compilar la línea base**

```powershell
Get-Process Api -ErrorAction SilentlyContinue
dotnet build src/Api/Api.csproj
```

Esperado: `Get-Process` sin salida; build con `0 Error(s)`.

- [ ] **Step 3: Confirmar las dos reglas de capas que este plan no debe romper**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~QuotationsLayerTests|FullyQualifiedName~StoreLayerTests"
```

Esperado: PASS. Sin commit en esta tarea.

---

### Task 2: Dominio — `BuyerAccountId`, `CanBeCancelledByBuyer`, `CancelByBuyer`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/BuyerOrderErrors.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (propiedades ~`:260-280`, `CreateFromStore` `:474-497`, `Cancel` `:907-933`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `OrderStoreFactoryTests.cs`

**Interfaces:**
- Produces:
  - `public static class BuyerOrderErrors { public const string NotCancellableByBuyer = "order.order.not_cancellable_by_buyer"; public const string CancellationReason = "Cancelado por el comprador desde la tienda"; }`
  - `Order.BuyerAccountId : Guid? { get; private set; }`
  - `Order.CreateFromStore(…, string requestNumber, string externalKey, Guid? buyerAccountId = null)` — parámetro opcional al final; los llamadores actuales (`StoreOrderGateway`, pruebas) compilan sin cambios.
  - `Order.CanBeCancelledByBuyer : bool` ⇒ `Status == OrderStatus.Pending && PaymentStatus == OrderPaymentStatus.PaymentPending`.
  - `Order.CancelByBuyer(Guid buyerAccountId, DateTimeOffset occurredAt)`: si `BuyerAccountId != buyerAccountId` o `!CanBeCancelledByBuyer` lanza `QuotationsDomainException(BuyerOrderErrors.NotCancellableByBuyer, …)`; si no, `Status = Cancelled`, `CancelledBy = null`, `CancelledAt = occurredAt`, `CancellationReason = BuyerOrderErrors.CancellationReason`, `UpdatedAt = occurredAt`, `Version++`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `OrderStoreFactoryTests.cs` (usa `NewStoreOrder`; agregar una sobrecarga/parámetro `buyerAccountId`):

```csharp
[Fact]
public void CreateFromStoreKeepsTheBuyerAccount()
{
    var buyer = Guid.CreateVersion7();
    var order = Order.CreateFromStore(/* mismos argumentos que NewStoreOrder */, buyerAccountId: buyer);
    Assert.Equal(buyer, order.BuyerAccountId);
}

[Fact]
public void CreateFromStoreWithoutABuyerLeavesItNull() =>
    Assert.Null(NewStoreOrder(Parties(Billing(), Shipping())).BuyerAccountId);
```

En `OrderTests.cs` (nuevo helper `NewBuyerStoreOrder(Guid buyer, OrderPaymentStatus payment = PaymentPending)` sobre `Order.CreateFromStore`; `Approve`/`Cancel`/`Invoice` ya existen para mover el estado):

```csharp
[Theory]
[InlineData(OrderStatus.Pending, OrderPaymentStatus.PaymentPending, true)]
[InlineData(OrderStatus.Pending, OrderPaymentStatus.PartialPaymentReceived, false)]
[InlineData(OrderStatus.Pending, OrderPaymentStatus.FullPaymentReceived, false)]
[InlineData(OrderStatus.Approved, OrderPaymentStatus.PaymentPending, false)]
[InlineData(OrderStatus.Cancelled, OrderPaymentStatus.PaymentPending, false)]
[InlineData(OrderStatus.Invoiced, OrderPaymentStatus.PaymentPending, false)]
public void CanBeCancelledByBuyerCoversStatusByPayment(OrderStatus status, OrderPaymentStatus payment, bool expected)
// construir con `payment` y llevar a `status` con Approve/Cancel/Invoice; Assert.Equal(expected, order.CanBeCancelledByBuyer)

[Fact]
public void CancelByBuyerCancelsAPendingUnpaidOrderWithoutAMember()
{
    var buyer = Guid.CreateVersion7();
    var order = NewBuyerStoreOrder(buyer);
    var version = order.Version;
    order.CancelByBuyer(buyer, Now.AddHours(1));
    Assert.Equal(OrderStatus.Cancelled, order.Status);
    Assert.Null(order.CancelledBy);
    Assert.Equal(Now.AddHours(1), order.CancelledAt);
    Assert.Equal("Cancelado por el comprador desde la tienda", order.CancellationReason);
    Assert.Equal(version + 1, order.Version);
}

[Theory]
[InlineData(OrderPaymentStatus.PartialPaymentReceived)]
[InlineData(OrderPaymentStatus.FullPaymentReceived)]
public void CancelByBuyerRejectsAPendingOrderWithPayment(OrderPaymentStatus payment)
// Assert.Throws<QuotationsDomainException>(...).Code == "order.order.not_cancellable_by_buyer"

[Fact] public void CancelByBuyerRejectsAnApprovedOrder()   // mismo código
[Fact] public void CancelByBuyerRejectsACancelledOrder()   // mismo código (no `already_cancelled`)
[Fact] public void CancelByBuyerRejectsAnInvoicedOrder()   // mismo código (no `already_invoiced`)
[Fact] public void CancelByBuyerRejectsAnotherBuyer()      // buyerAccountId distinto → mismo código
[Fact] public void CancelByBuyerRejectsAnOrderWithoutBuyer() // BuyerAccountId null → mismo código
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~OrderStoreFactoryTests"
```

Esperado: no compila (`BuyerAccountId`, `CanBeCancelledByBuyer`, `CancelByBuyer`, `BuyerOrderErrors` no existen).

- [ ] **Step 3: Implementar** `BuyerOrderErrors.cs` y los miembros de `Order` del bloque Interfaces. `CreateFromStore` asigna `order.BuyerAccountId = buyerAccountId` después de `Build(...)`, igual que ya hace con `ExternalKey`. `CancelByBuyer` no pasa por `NormalizeCancellationReason` (el motivo es constante).

- [ ] **Step 4: GREEN** — mismo comando del Step 2 → PASS. Además `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests` completo → PASS (nada más del módulo cambia).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Domain tests/Modules/Quotations/Modules.Quotations.UnitTests
git commit -m "feat(quotations): link orders to a buyer account and cancel them from the store"
```

---

### Task 3: Persistencia — columna, índice, migración y consultas por comprador

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (mapeo de `Order`, `:494-660`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubOrderListRepository : IOrderRepository`, `:889`)
- Create (generado): `…/Persistence/Migrations/<timestamp>_AddOrderBuyerAccount.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersMigrationTests.cs`

**Interfaces:**
- Produces en `IOrderRepository`:
  - `Task<(IReadOnlyList<Order> Items, int Total)> SearchByBuyerAsync(Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken)` — `WHERE tenant_id = @t AND buyer_account_id = @b`, `ORDER BY created_at DESC, order_number DESC`, `Include(Items)`, sin tracking.
  - `Task<Order?> FindByBuyerAsync(Guid tenantId, Guid buyerAccountId, OrderId orderId, CancellationToken cancellationToken)` — con tracking e `Include(Items)`, `Include(PaymentProofs)` (como `FindByIdAsync`, para que `Cancel`/`Version` funcionen).
- Columna `buyer_account_id uuid NULL`; índice `IX_orders_tenant_buyer_created (tenant_id, buyer_account_id, created_at)` con filtro `buyer_account_id IS NOT NULL`.

- [ ] **Step 1: Pruebas que fallan**

`QuotationsDbContextMappingTests.cs` (mismo patrón que las existentes con `QuotationsDbContextFactory().CreateDbContext([])`):

```csharp
[Fact]
public void TheOrderBuyerAccountMapsToItsColumnAndFilteredIndex()
{
    using var context = new QuotationsDbContextFactory().CreateDbContext([]);
    var entity = context.Model.FindEntityType(typeof(Order))!;
    Assert.Equal("buyer_account_id", entity.FindProperty(nameof(Order.BuyerAccountId))!.GetColumnName());
    var index = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == "IX_orders_tenant_buyer_created");
    Assert.Equal(["TenantId", "BuyerAccountId", "CreatedAt"], index.Properties.Select(p => p.Name).ToArray());
    Assert.Equal("buyer_account_id IS NOT NULL", index.GetFilter());
}
```

`OrdersMigrationTests.cs` (usar `migrator`, `MigrationId(context, "_AddOrderBuyerAccount")` y la consulta a `pg_indexes` que ya tiene el archivo):

```csharp
[Fact]
public async Task AddOrderBuyerAccountAddsTheColumnAndTheIndex()
// MigrateAsync hasta la anterior ("_AddStoreOrderSource") → columna ausente; MigrateAsync hasta "_AddOrderBuyerAccount" →
// information_schema.columns tiene (orders, buyer_account_id, is_nullable = 'YES') y pg_indexes tiene IX_orders_tenant_buyer_created;
// terminar con MigrateAsync(null) (gotcha de CLAUDE.md).
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~QuotationsDbContextMappingTests
```

Esperado: FAIL (`FindProperty` devuelve null / índice ausente). La de migración aún no compila por `MigrationId` inexistente: se corre en el Step 4.

- [ ] **Step 3: Implementar**
  1. Mapeo en `QuotationsDbContext`: `order.Property(v => v.BuyerAccountId).HasColumnName("buyer_account_id");` y `order.HasIndex(v => new { v.TenantId, v.BuyerAccountId, v.CreatedAt }).HasFilter("buyer_account_id IS NOT NULL").HasDatabaseName("IX_orders_tenant_buyer_created");`.
  2. Migración (factory de diseño, no `--startup-project`):

```powershell
dotnet ef migrations add AddOrderBuyerAccount --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

  Revisar que el `Up` sólo tenga `AddColumn<Guid>("buyer_account_id", nullable: true)` y `CreateIndex(... filter: "buyer_account_id IS NOT NULL")`, y que el snapshot no traiga ruido.
  3. `IOrderRepository` + `OrderRepository` con las dos consultas del bloque Interfaces. `StubOrderListRepository` implementa las dos (filtra `rows` por tenant y `BuyerAccountId`, ordena igual) y guarda `LastBuyerSearch = (tenantId, buyerAccountId, page, pageSize)`.

- [ ] **Step 4: GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~QuotationsDbContextMappingTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter FullyQualifiedName~OrdersMigrationTests -- xUnit.MaxParallelThreads=4
dotnet build src/Api/Api.csproj
```

Esperado: PASS, PASS, `0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): persist the order buyer account and query orders by buyer"
```

---

### Task 4: `BuyerOrderGateway` en Quotations.Application

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `services.AddScoped<IStoreOrderGateway, StoreOrderGateway>();`, `:795`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/BuyerOrderGatewayTests.cs`

**Interfaces:**
- Consumes: `IOrderRepository.SearchByBuyerAsync/FindByBuyerAsync` (Task 3), `Order.CanBeCancelledByBuyer/CancelByBuyer` (Task 2), `IQuotationsUnitOfWork`, `IQuotationAuditPublisher.PublishByIntegration`, `IQuotationLifecycleEventPublisher.PublishOrderCancelled`, `IClock` (BuildingBlocks.Application, como `CancelOrderHandler`).
- Produces (todo en `IBuyerOrderGateway.cs`, namespace `Modules.Quotations.Application`):

```csharp
public sealed record BuyerOrderLine(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record BuyerOrderSummary(
    Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    decimal Total, string Currency, int LineCount, string FirstLineName, bool CanCancel);
public sealed record BuyerOrderDetail(
    Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    decimal Total, string Currency, int LineCount, string FirstLineName,
    IReadOnlyList<BuyerOrderLine> Lines, DateTimeOffset? CancelledAt, bool CanCancel);
public sealed record BuyerOrderPage(IReadOnlyList<BuyerOrderSummary> Items, int Page, int PageSize, int Total);

public interface IBuyerOrderGateway
{
    Task<BuyerOrderPage> ListAsync(Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken);
    Task<BuyerOrderDetail?> FindAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken);
    /// 409 ResourceConflictException(BuyerOrderErrors.NotCancellableByBuyer) si no es anulable; null si no existe para ese comprador.
    Task<BuyerOrderDetail?> CancelAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken);
    /// Pone BuyerAccountId sólo si está vacío. Devuelve true si escribió. Lo usa el enlace diferido (Task 11).
    Task<bool> LinkAsync(Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken);
}

public sealed class BuyerOrderGateway(
    IOrderRepository orders, IQuotationsUnitOfWork unitOfWork, IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents, IClock clock) : IBuyerOrderGateway
```

  - `Status`/`PaymentStatus` = `order.Status.ToString()` / `order.PaymentStatus.ToString()`. `LineCount = Items.Count`. `FirstLineName` = `ProductName` del ítem con menor `Position`, `?? string.Empty`. `LineTotal = item.Subtotal + item.TaxAmount`. `CanCancel = order.CanBeCancelledByBuyer` **en el summary y en el detail** (spec §4.5, decisión del 2026-11-09: la lista pinta el botón sin pedir el detalle).
  - `CancelAsync`: `FindByBuyerAsync` → `null` si no hay → si `!CanBeCancelledByBuyer` lanza `new ResourceConflictException(BuyerOrderErrors.NotCancellableByBuyer, "The order can no longer be cancelled from the store.")` → `CancelByBuyer(buyerAccountId, now)` → `orders.AddHistoryEntry(OrderHistoryEntry.Create(OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.Cancelled, memberId: null, OrderChangeSummary.Cancelled(order.CancellationReason!), now))` → `auditPublisher.PublishByIntegration(tenantId, "quotation.order.cancelled", order.Id.ToString(), "success", now)` → `lifecycleEvents.PublishOrderCancelled(order, now)` → `unitOfWork.SaveChangesAsync`. `catch (RequestConcurrencyException)`: `unitOfWork.DiscardPendingChanges()`, releer; `null` → devolver `null`; no anulable → 409; anulable → repetir el bloque **una** vez y, si vuelve a lanzar, 409.
  - `LinkAsync`: `orders.FindByIdAsync(tenantId, new OrderId(orderId))`; `null` → `false`; `BuyerAccountId is not null` → `false`; si no, asigna por un método de dominio nuevo `Order.LinkBuyerAccount(Guid buyerAccountId)` (sólo si está vacío; **no** sube `Version`, no toca estado) y guarda → `true`.

- [ ] **Step 1: Pruebas que fallan** (`BuyerOrderGatewayTests.cs`; usar `StubOrderListRepository` con pedidos de `Order.CreateFromStore(..., buyerAccountId: …)`, `RecordingQuotationAuditPublisher`, `RecordingQuotationLifecycleEventPublisher`, `NoOpQuotationsUnitOfWork` y un `ThrowOnceUnitOfWork : IQuotationsUnitOfWork` local que lanza `RequestConcurrencyException` en el primer `SaveChangesAsync`):

```csharp
[Fact] ListReturnsOnlyTheBuyerOrdersOfTheTenantNewestFirst
  // 3 pedidos del comprador en el tenant (CreatedAt distintos), 1 de otro comprador, 1 del mismo comprador en otro tenant
  // → Items.Select(Number) en orden CreatedAt desc; Total == 3; Page/PageSize copiados.
[Fact] ASummaryCarriesLineCountFirstLineNameEnumNamesAndCanCancel
  // Status == "Pending", PaymentStatus == "PaymentPending", LineCount == 2, FirstLineName == nombre del ítem Position 1, CanCancel true.
[Theory] ASummaryReportsCanCancelByRuleD5
  // (Pending, PaymentPending) → true; (Pending, PartialPaymentReceived) → false; (Pending, FullPaymentReceived) → false; (Approved, PaymentPending) → false.
[Fact] FindReturnsNullForAnotherBuyersOrder
[Fact] FindComputesLineTotalAndCanCancel
  // LineTotal == Subtotal + TaxAmount; CanCancel true para Pending+PaymentPending.
[Fact] FindReportsCanCancelFalseWhenAPaymentIsRegistered
[Fact] CancelCancelsWithoutAMemberAuditsByIntegrationAndPublishesTheEvent
  // Status "Cancelled", CancelledAt == clock; History único con MemberId null y Details "Anulado: Cancelado por el comprador desde la tienda";
  // audit.Actions == ["quotation.order.cancelled"]; lifecycle.Published único con Event "cancelled".
[Fact] CancelOfAnApprovedOrderIsAConflictAndWritesNothing   // ResourceConflictException.Code == "order.order.not_cancellable_by_buyer"; History vacío
[Fact] CancelOfAPendingOrderWithPaymentIsAConflict
[Fact] ACancelThatLosesTheRaceRereadsAndAnswersConflict
  // ThrowOnceUnitOfWork; el stub devuelve en la relectura un pedido ya Approved → 409; audit/lifecycle con una sola entrada descartada no importa: History del stub vacío tras Discard.
[Fact] ACancelThatLosesTheRaceToAHarmlessEditRetriesOnce
  // ThrowOnceUnitOfWork; relectura sigue Pending+PaymentPending → resultado Status "Cancelled"; SaveCalls == 2.
[Fact] LinkSetsTheBuyerOnlyWhenEmpty
  // pedido sin comprador → true y BuyerAccountId == id; pedido con otro comprador → false y no cambia; pedido inexistente → false.
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~BuyerOrderGatewayTests
```

Esperado: no compila (`IBuyerOrderGateway`, `BuyerOrderGateway`, `Order.LinkBuyerAccount` no existen).

- [ ] **Step 3: Implementar** `IBuyerOrderGateway.cs` y `Order.LinkBuyerAccount(Guid buyerAccountId)` (lanza `QuotationsDomainException("order.order.buyer_already_linked", …)` si ya tiene otro id; no-op si es el mismo). Registrar `services.AddScoped<IBuyerOrderGateway, BuyerOrderGateway>();` junto a `IStoreOrderGateway`.

- [ ] **Step 4: GREEN** — comando del Step 2 → PASS; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter FullyQualifiedName~QuotationsLayerTests` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations
git commit -m "feat(quotations): buyer order gateway to list, read, cancel and link store orders"
```

---

### Task 5: Buyers — `TenantId` en el storefront, puerto `IBuyerOrders` y handlers

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs` (`BuyerStorefront`, `:113`)
- Modify: `src/Bootstrapper/BuyerStorefronts.cs`
- Modify: `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyersApiHarness.cs` (`FakeBuyerStorefronts`, `:294-303`)
- Create: `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (bloque de Buyers, `:1448-1495`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/BuyersLayerTests.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs`

**Interfaces:**
- `public sealed record BuyerStorefront(string Host, string Name, Guid TenantId);` — el adaptador pasa `store.TenantId` (`OnlineStore.TenantId`); el fake de pruebas, un `Guid` fijo `FakeBuyerStorefronts.TenantId`.
- Produces en `BuyerOrders.cs` (namespace `Modules.Buyers.Application`):

```csharp
public sealed record BuyerOrderLineDto(string Name, decimal Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record BuyerOrderSummaryDto(Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    decimal Total, string Currency, int LineCount, string FirstLineName, bool CanCancel);
public sealed record BuyerOrderDetailDto(Guid Id, string Number, DateTimeOffset CreatedAt, string Status, string PaymentStatus,
    decimal Total, string Currency, int LineCount, string FirstLineName,
    IReadOnlyList<BuyerOrderLineDto> Lines, DateTimeOffset? CancelledAt, bool CanCancel);
public sealed record BuyerOrderPageDto(IReadOnlyList<BuyerOrderSummaryDto> Items, int Page, int PageSize, int Total);

/// Buyers → Quotations, sin verlo: adaptador en Bootstrapper. null = no existe para ese comprador en ese tenant.
public interface IBuyerOrders
{
    Task<BuyerOrderPageDto> ListAsync(Guid tenantId, Guid accountId, int page, int pageSize, CancellationToken cancellationToken);
    Task<BuyerOrderDetailDto?> FindAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken);
    Task<BuyerOrderDetailDto?> CancelAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken);
}

public sealed record ListBuyerOrdersQuery(string Host, string? Token, int Page, int PageSize) : IQuery<BuyerOrderPageDto>;
public sealed class ListBuyerOrdersQueryValidator : AbstractValidator<ListBuyerOrdersQuery>   // Page >= 1; PageSize 1..50 (const MaxPageSize = 50)
public sealed record GetBuyerOrderQuery(string Host, string? Token, Guid OrderId) : IQuery<BuyerOrderDetailDto>;
public sealed record CancelBuyerOrderCommand(string Host, string? Token, Guid OrderId) : ICommand<BuyerOrderDetailDto>;
```

  - Handlers `ListBuyerOrdersHandler(IBuyerStorefronts, BuyerSessions, IBuyerOrders, IValidator<ListBuyerOrdersQuery>)`, `GetBuyerOrderHandler(IBuyerStorefronts, BuyerSessions, IBuyerOrders)`, `CancelBuyerOrderHandler(IBuyerStorefronts, BuyerSessions, IBuyerOrders)`. Orden: `storefronts.ResolveAsync(host)` (404) → validador (422, sólo el listado) → `sessions.RequireAsync(storefront.Host, token)` (401) → puerto con `storefront.TenantId` y `session.AccountId`. Un `null` del puerto en detalle y anulación es `new ResourceNotFoundException("order.order.not_found", "The order was not found.")` (código literal: Buyers no ve `OrderNotFound` de Quotations).
  - El validador entra solo: `AddValidatorsFromAssemblyContaining<RegisterBuyerBodyValidator>()` ya cubre el assembly.

- [ ] **Step 1: Pruebas que fallan** (`BuyerOrdersHandlersTests.cs`; `BuyersWorld` de `BuyersTestDoubles.cs` da `Sessions`, `UnitOfWork`, `Clock`; construir `new BuyerSessions(world.Sessions, world.UnitOfWork, world.Clock)` como `LoginBuyerHandlerTests`; un `RecordingBuyerOrders : IBuyerOrders` local que guarda los argumentos y devuelve lo que se le configure; un `FixedStorefronts : IBuyerStorefronts` con `TenantId` fijo):

```csharp
[Fact] ListPassesTheStorefrontTenantAndTheSessionAccount
  // sesión emitida con BuyerSession.Issue para el host; orders.LastList == (tenantId, accountId, 2, 20)
[Fact] ListRejectsPageAndPageSizeOutOfRange
  // (0, 20), (1, 0), (1, 51) → ValidationException; orders.Calls == 0; no se consultó la sesión
[Fact] ListWithoutAValidSessionIs401  // RequestUnauthorizedException.Code == "store.buyer.session_invalid"
[Fact] GetMapsANullFromThePortToOrderNotFound  // ResourceNotFoundException.Code == "order.order.not_found"
[Fact] CancelMapsANullFromThePortToOrderNotFound
[Fact] CancelLetsTheConflictThrough  // RecordingBuyerOrders lanza ResourceConflictException("order.order.not_cancellable_by_buyer") → misma excepción
[Fact] ASessionOfAnotherHostIs401
```

Y en `BuyersLayerTests.cs`:

```csharp
[Fact]
public void NoBuyersLayerReferencesQuotations()
{
    foreach (var layer in AllLayers())
    {
        Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
            name is not null && name.StartsWith("Modules.Quotations", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter FullyQualifiedName~BuyerOrdersHandlersTests
```

Esperado: no compila.

- [ ] **Step 3: Implementar** `BuyerStorefront` con `TenantId` (adaptador y `FakeBuyerStorefronts`), `BuyerOrders.cs` y el registro en DI de los tres handlers (`IQueryHandler<ListBuyerOrdersQuery, BuyerOrderPageDto>`, `IQueryHandler<GetBuyerOrderQuery, BuyerOrderDetailDto>`, `ICommandHandler<CancelBuyerOrderCommand, BuyerOrderDetailDto>`), al lado de `GetBuyerSessionHandler`.

- [ ] **Step 4: GREEN**

```powershell
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests --filter FullyQualifiedName~BuyersLayerTests
dotnet build src/Api/Api.csproj
```

Esperado: PASS, PASS (incluida la aserción nueva), `0 Error(s)` (si `BuyerStorefrontAdapterApiTests` u otra prueba construye `BuyerStorefront` con dos argumentos, se le agrega el `TenantId`).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Buyers src/Bootstrapper tests/Modules/Buyers tests/ArchitectureTests
git commit -m "feat(buyers): list, read and cancel the buyer orders behind the IBuyerOrders port"
```

---

### Task 6: Adaptador en Bootstrapper y rutas públicas

**Files:**
- Create: `src/Bootstrapper/BuyerOrdersAdapter.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (bloque de Buyers)
- Modify: `src/Modules/Buyers/Modules.Buyers.Api/PublicBuyerEndpoints.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IBuyerOrderGateway` (Task 4), `IBuyerOrders` y DTOs (Task 5).
- Produces: `internal sealed class BuyerOrdersAdapter(IServiceScopeFactory scopes) : IBuyerOrders` — cada método abre `scopes.CreateAsyncScope()`, resuelve `IBuyerOrderGateway` y mapea record a record (misma razón que `StoreOrdersAdapter`: el gateway puede limpiar el tracker de `QuotationsDbContext` en la carrera). Registro: `services.AddScoped<Modules.Buyers.Application.IBuyerOrders, BuyerOrdersAdapter>();`.
- Rutas en el grupo existente (heredan `AllowAnonymous`, `StorefrontKeyFilter`, tope de cuerpo, `readPolicy`; `no-store` lo pone `BuyerNoStoreMiddleware` por prefijo):
  - `GET /orders` → `RequireVisitorIp(httpContext)` → `dispatcher.QueryAsync(new ListBuyerOrdersQuery(host, SessionToken(httpContext), page ?? 1, pageSize ?? 20))` → `200 BuyerOrderPageDto`. `page`/`pageSize` son `int?` de query string; un valor no numérico lo rechaza el binder con 400.
  - `GET /orders/{orderId:guid}` → `RequireVisitorIp` → `GetBuyerOrderQuery` → `200 BuyerOrderDetailDto`.
  - `POST /orders/{orderId:guid}/cancel` → `RequireVisitorIp` → sin cuerpo → `CancelBuyerOrderCommand` → `200 BuyerOrderDetailDto`.
  - `.Produces<…>()`/`.ProducesProblem(...)` con 401, 404, 409 (sólo cancel), 422 (sólo listado), 429.

- [ ] **Step 1: Prueba que falla** (`BuyerOrdersAdapterTests.cs`, Bootstrapper.UnitTests ve los dos módulos): un `ServiceCollection` con un `RecordingBuyerOrderGateway : IBuyerOrderGateway` scoped que cuenta instancias; `CancelAsync` dos veces → dos instancias (un scope por llamada); `FindAsync` mapea campo a campo (`Lines`, `CanCancel`, `CancelledAt`); `null` del gateway → `null` del adaptador; `ResourceConflictException` del gateway sale tal cual.

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerOrdersAdapterTests
```

Esperado: no compila.

- [ ] **Step 3: Implementar** el adaptador, su registro y las tres rutas.

- [ ] **Step 4: GREEN** — comando del Step 2 → PASS; `dotnet build src/Api/Api.csproj` → `0 Error(s)`; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~CompositionRootTests"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Bootstrapper src/Modules/Buyers/Modules.Buyers.Api tests/Bootstrapper
git commit -m "feat(buyers): public buyer order routes and the Bootstrapper adapter to Quotations"
```

---

### Task 7: Integración de punta a punta — `PublicBuyerOrdersApiTests`

**Files:**
- Create: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs`
- Consumes: `StoreInboxRig` (`OpenAsync`, `SubmitAsync`, `TakeOkAsync`, `OrderAsync`, `Fixture.Storefront`, `Factory`), `StoreApiHarness.CreateClient(factory, subjectId, tenantId, permissions)` para la consola, `PublicStoreFixture.Host = "tienda-demo.lulocrm.com"`, `StoreApiHarness.TenantId`/`OtherTenantId`, `StoreApiHarness.SeedTenantAsync` para el otro tenant. Registro del comprador como en `PublicOrderBuyerAccountApiTests` (`POST /buyer/register` con `turnstileToken`, token de verificación desde `platform.outbox_messages` `buyers.email-verification-requested.v1`, `POST /buyer/email/verify`, `POST /buyer/session`): el `FakeCaptchaVerifier` del rig también atiende a Buyers (`Bootstrapper/BuyerCaptcha` envuelve `ICaptchaVerifier`).

**Siembra de pedidos (hasta el rebase, D4):** `rig.SubmitAsync()` → `rig.TakeOkAsync(requestId, body)` (como `StoreTakeApiTests.TakingWithANewCustomerCreatesACompleteCustomerFromTheBilling`) da un `Order` `Source = Store`, `Pending`, `PaymentPending`; luego `UPDATE quotations.orders SET buyer_account_id = @account WHERE id = @order` (el `account_id` sale de `SELECT id FROM buyers.accounts WHERE email = @email`). Helper local `SeedBuyerOrderAsync(rig, accountId)` → `Guid orderId`. Para «pago registrado»: `UPDATE quotations.orders SET payment_status = 'PartialPaymentReceived' WHERE id = @order` (la columna es texto del enum; evita subir un archivo a Storage). Para «aprobado»: `POST /api/v1/tenants/{TenantId}/orders/{id}/approve` con un cliente de consola con permiso `quotations.order.approve`. Para «otro tenant»: `StoreApiHarness.SeedTenantAsync(OtherTenantId…)` y `UPDATE quotations.orders SET tenant_id = @other WHERE id = @order`.

- [ ] **Step 1: Pruebas que fallan** (helpers: `BuyerGetAsync(path, token)`, `BuyerPostAsync(path, token)` con `X-Qep-Visitor-Ip` y `X-Qep-Buyer-Session` sobre `rig.Fixture.Storefront`; `JsonAsync`):

```csharp
[Fact] WithoutASessionTheListIs401SessionInvalid
[Fact] WithoutTheVisitorIpTheListIs422VisitorIpRequired   // "store.public.visitor_ip_required"
[Fact] TheListIsPagedNewestFirstAndOnlyShowsTheBuyerOrders
  // 3 pedidos del comprador + 1 de otro comprador (otra cuenta) + 1 invitado (buyer_account_id NULL):
  // ?page=1&pageSize=2 → items.Length 2, total 3, orden por createdAt desc; ?page=2&pageSize=2 → 1 item; cada item con
  // id, number, createdAt, status "Pending", paymentStatus "PaymentPending", total, currency "COP", lineCount 1, firstLineName "Camiseta", canCancel true.
[Fact] TheListReportsCanCancelPerOrder
  // 3 pedidos del comprador: uno Pending+PaymentPending → canCancel true; uno Pending con payment_status 'PartialPaymentReceived' → false;
  // uno aprobado por consola → false. El detalle de cada uno coincide con su item.
[Fact] ListRejectsAnOutOfRangePageSize   // ?pageSize=51 → 422 "validation.failed" con errors["pageSize"]; ?page=0 → 422
[Fact] TheDetailCarriesLinesAndCanCancel   // lines[0].lineTotal == unitPrice*quantity (10_000 × 2 con IVA 0 según la siembra: comparar contra el detalle de la consola rig.OrderAsync), canCancel true, cancelledAt null
[Fact] AnotherBuyersOrderIs404OrderNotFound
[Fact] AnOrderOfTheSameBuyerInAnotherTenantIs404
[Fact] AnUnknownHostIs404StorePublicNotFound   // host "no-existe.lulocrm.com"
[Fact] CancelCancelsThePendingUnpaidOrder
  // 200, status "Cancelled", canCancel false, cancelledAt no nulo;
  // SELECT count(*) FROM quotations.order_history WHERE order_id=@id AND event_type='Cancelled' AND member_id IS NULL → 1
  //   (confirmar el nombre de tabla y de columna en el mapeo de OrderHistoryEntry de QuotationsDbContext antes de escribir el SQL);
  // platform.outbox_messages con event_name 'quotations.order.cancelled.v1' y payload orderId → 1;
  // SELECT cancellation_reason → "Cancelado por el comprador desde la tienda"; cancelled_by IS NULL.
[Fact] ASecondCancelIs409AndWritesNothing   // 409 "order.order.not_cancellable_by_buyer"; historial Cancelled sigue en 1; evento sigue en 1
[Fact] APendingOrderWithPaymentIsNotCancellable   // payment_status Partial → 409; status sigue Pending; el detalle dice canCancel false
[Fact] AnApprovedOrderIsNotCancellable   // approve por consola → 409
[Fact] TheConsoleDetailShowsTheBuyerCancellation
  // rig.OrderAsync(orderId) → history con eventType "Cancelled", memberId null, details "Anulado: Cancelado por el comprador desde la tienda"; order.cancelledBy null
[Fact] EveryBuyerOrderResponseIsNoStore   // Cache-Control: no-store en 200, 404 y 409
```

- [ ] **Step 2: RED**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~PublicBuyerOrdersApiTests -- xUnit.MaxParallelThreads=4
```

Esperado: compila (todo lo que usa ya existe desde Task 6) y **falla** sólo si algo de las tareas 2–6 quedó mal; si pasa todo en verde a la primera, anotarlo como evidencia y revisar que cada aserción realmente se ejecutó (`dotnet test … --logger "console;verbosity=normal"`). La RED genuina de esta tarea es escribir primero la prueba y correrla antes de corregir cualquier falla que destape.

- [ ] **Step 3: Corregir** lo que destape (sin tocar las pruebas para que pasen).

- [ ] **Step 4: GREEN** — mismo comando → PASS. Luego, uno a la vez:

```powershell
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~StoreOrderGatewayTests|FullyQualifiedName~OrderApiTests|FullyQualifiedName~OrdersMigrationTests" -- xUnit.MaxParallelThreads=4
```

Esperado: PASS los tres.

- [ ] **Step 5: Commit**

```powershell
git add tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs
git commit -m "test(buyers): end-to-end buyer order history and cancellation from the store"
```

(Si el Step 3 tocó código, commitearlo con `fix(...)` **antes** de este, con su propio mensaje.)

---

### Task 8: Documentación del contrato (sin `estado.md`)

**Files:**
- Modify: `README.md` § «Cuentas de comprador» → «Contrato HTTP para el storefront» (tabla que arranca en `:1082`): tres filas nuevas (`GET /buyer/orders`, `GET /buyer/orders/{orderId}`, `POST /buyer/orders/{orderId}/cancel`) con cuerpo, respuesta, errores, limitador (`lectura`), Turnstile `no`, IP `sí`, sesión `sí`; más un párrafo «Pedidos del comprador» con D3 y D5.
- Modify: `CLAUDE.md` § «Gotchas verificados», una entrada: la anulación del comprador es más estricta que `Order.Cancel` (D5), responde **409** `order.order.not_cancellable_by_buyer` también para la carrera (`RequestConcurrencyException` traducida en `BuyerOrderGateway`, nunca 412 hacia el storefront), el dominio no puede lanzar `ResourceConflictException` (Domain sólo ve `BuildingBlocks.Domain`), y `BuyerStorefront` lleva `TenantId`: una prueba que lo construya a mano necesita los tres argumentos.

- [ ] **Step 1: Escribir** las dos ediciones.
- [ ] **Step 2: Verificar** que los códigos y rutas citados existen: `Select-String -Path src -Pattern "order.order.not_cancellable_by_buyer" -Recurse | Measure-Object` → `Count` ≥ 2.
- [ ] **Step 3: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs(buyers): document the buyer order routes and the store cancellation rule"
```

---

### Task 9: Cierre del bloque independiente

- [ ] **Step 1: Suite por proyecto** (uno a la vez):

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests
dotnet test tests/Modules/Store/Modules.Store.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4
```

Esperado: todo PASS. Sin commit. **Aquí se detiene la construcción hasta que `feat/store-orders-direct` entre a `develop`.**

---

## Bloque final (D4): después del rebase sobre `develop` con pedidos directos mergeado

Precondición, verificada en el momento (no por este documento):

```powershell
git fetch origin
git log --oneline -5 origin/develop
git log --oneline origin/develop --grep="store" -i | Select-Object -First 5
git rebase origin/develop
```

Conflictos esperados y cómo se resuelven: `QepServiceCollectionExtensions.cs` (se conservan los registros de los dos lados), `StoreOrdersAdapter.cs`/`IStoreOrderGateway.cs` si pedidos directos cambió `StoreOrderDraft` (se conserva su forma nueva; esta rama sólo **suma** `BuyerAccountId`). Tras el rebase: `dotnet build src/Api/Api.csproj` y la suite de Task 9 antes de seguir. Los nombres que siguen (`SubmitOrderRequestHandler` o `CreateStoreOrderHandler`, `StoreOrderDraft` sin `RequestId`, `BuyerSignupRequestedMessage.OrderId`) **se confirman leyendo el código rebasado**; el spec de pedidos directos los deja al builder.

### Task 10: Sellar `BuyerAccountId` en el checkout con sesión

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreBuyerAccounts.cs`
- Modify: `src/Bootstrapper/StoreBuyerAccounts.cs`
- Modify: `StoreOrderDraft` en `src/Modules/Store/Modules.Store.Application/IStoreOrders.cs` (o donde quede tras el rebase) y en `src/Modules/Quotations/Modules.Quotations.Application/IStoreOrderGateway.cs`; `StoreOrderGateway.CreateAsync` pasa `draft.BuyerAccountId` a `Order.CreateFromStore`; `src/Bootstrapper/StoreOrdersAdapter.cs` copia el campo.
- Modify: el handler del checkout (`SubmitOrderRequestHandler` hoy, `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs:194` recibe `IStoreBuyerAccounts buyers`).
- Test: `tests/Modules/Store/Modules.Store.UnitTests/<handler>Tests.cs` (el archivo de pruebas del handler tal como quede), `tests/Modules/Quotations/Modules.Quotations.UnitTests/StoreOrderGatewayTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/StoreOrdersAdapterTests.cs`.

**Interfaces:**
- `IStoreBuyerAccounts` gana `Task<Guid?> ResolveAccountIdAsync(string host, string? sessionToken, CancellationToken cancellationToken)`: `null` si el token es nulo, vencido, de otro host o de cuenta borrada. Adaptador: `BuyerSessions.TryResolveAsync(host, token)` y luego `IBuyerAccountRepository.FindAsync(session.AccountId)` (como `ReplaceBuyerProfileFromOrder`); devuelve `account?.Id`.
- `StoreOrderDraft` (Store y Quotations) gana `Guid? BuyerAccountId` al final, con default `null`.
- Regla en el handler: si llega `BuyerSessionToken`, se resuelve **antes** de crear el pedido, sin importar `updateProfile`; una excepción del adaptador se registra como `Warning` y el pedido sigue con `null` (la cuenta nunca hace fallar el pedido). Con `createAccount` y sesión a la vez manda el validador existente.

- [ ] **Step 1: Pruebas que fallan**
  - Handler (unit): con sesión válida (`FakeStoreBuyerAccounts.ResolveAccountIdAsync` → `id`) el draft que recibe `IStoreOrders.CreateAsync` lleva `BuyerAccountId == id` aunque `updateProfile == false`; con sesión inválida → `null` y el pedido se crea; si el adaptador lanza → `null`, el pedido se crea y hay un log `Warning`.
  - `StoreOrderGatewayTests` (unit, Quotations): el `Order` construido lleva el `BuyerAccountId` del draft.
  - `StoreOrdersAdapterTests`: copia `BuyerAccountId`.
  - `PublicOrderBuyerAccountApiTests` (integración): `RegisterVerifiedAsync` + `LoginAsync` → `POST /order-requests` con `X-Qep-Buyer-Session` y sin `updateProfile` → `SELECT buyer_account_id FROM quotations.orders WHERE id = @orderId` == id de la cuenta; con un token de otra tienda (`BuyerSessions.IssueAsync` para `otra-tienda.lulocrm.com`, como ya hace el archivo) → `NULL` y `buyerSessionRejected` según la regla vigente; luego `GET /buyer/orders` con esa sesión lista el pedido.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Store/Modules.Store.UnitTests`; `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~StoreOrderGatewayTests`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~StoreOrdersAdapterTests` → no compilan.
- [ ] **Step 3: Implementar** el bloque Interfaces.
- [ ] **Step 4: GREEN** — los tres de arriba, y `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicOrderBuyerAccountApiTests|FullyQualifiedName~PublicBuyerOrdersApiTests" -- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StoreLayerTests|FullyQualifiedName~BuyersLayerTests"` → PASS.
- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(store): stamp the buyer account on store orders created with a valid session"
```

### Task 11: Enlace del alta diferida (`createAccount`)

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerSignup.cs` — `SignUpAsync` pasa a devolver `Task<Guid?>`: el id de la cuenta en las ramas «nueva» y «sin verificar, reemplazada»; `null` en «ya tienes cuenta».
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs` — `public interface IBuyerOrderLinker { Task LinkAsync(Guid tenantId, Guid orderId, Guid accountId, CancellationToken cancellationToken); }`.
- Modify: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Signup/BuyerSignupRequestedWorker.cs` — tras `SignUpAsync` (fuera de su `SaveChanges`), si devolvió id: `await services.GetRequiredService<IBuyerOrderLinker>().LinkAsync(message.TenantId, message.OrderId, accountId, ct)` en `try/catch` con `LogLinkFailed(logger, orderId, exception)` a nivel `Warning`; el alta no se revierte ni se reintenta por eso.
- Create: `src/Bootstrapper/BuyerOrderLinker.cs` — `internal sealed class BuyerOrderLinker(IServiceScopeFactory scopes) : IBuyerOrderLinker` → scope propio → `IBuyerOrderGateway.LinkAsync(tenantId, orderId, accountId, ct)` (Task 4); registro `services.AddScoped<Modules.Buyers.Application.IBuyerOrderLinker, BuyerOrderLinker>();`.
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerSignupTests.cs`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerSignupRequestedWorkerTests.cs` (el harness ya permite `configure` para reemplazar `IBuyerOrderLinker` por un recorder o uno que lanza), `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs`.

- [ ] **Step 1: Pruebas que fallan**
  - `BuyerSignupTests`: `ANewEmail…` → el resultado es el id de la cuenta guardada; `AnUnverifiedEmail…` → el id de la cuenta existente; `AVerifiedEmailOnlyQueues…` → `null`.
  - `BuyerSignupRequestedWorkerTests`: con `RecordingLinker` → una llamada `(message.TenantId, message.OrderId, accountId de buyers.accounts)`; con `ThrowingLinker` → la cuenta queda creada, el mensaje queda procesado y el inbox cerrado (mismas aserciones que `ProcessingTheSameMessageTwiceCreatesOneAccountAndOneEmail`); con correo ya verificado → el linker **no** se llama.
  - `PublicOrderBuyerAccountApiTests`: checkout con `createAccount` → `DrainBuyerSignupsAsync` → `buyer_account_id` del pedido == id de la cuenta nueva; verificar e iniciar sesión → `GET /buyer/orders` lo lista. Variante: correo que ya tenía cuenta verificada → `buyer_account_id` sigue `NULL` (R2).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter FullyQualifiedName~BuyerSignupTests` → no compila (`SignUpAsync` devuelve `Task`).
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: GREEN** — el de arriba; `$env:Seed__Enabled = "false"`, `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter FullyQualifiedName~BuyerSignupRequestedWorkerTests -- xUnit.MaxParallelThreads=4`; `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~PublicOrderBuyerAccountApiTests -- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter FullyQualifiedName~BuyersLayerTests`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~BuyerParityTests` → PASS.
- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(buyers): link the store order to the account created by the deferred signup"
```

### Task 12: Cierre, merge y estado

- [ ] **Step 1:** repetir la suite de Task 9 completa (uno a la vez) → PASS.
- [ ] **Step 2:** agregar al `CLAUDE.md` (gotcha de Task 8) la frase sobre el enlace diferido: «si `IBuyerOrderLinker` falla, el pedido queda como invitado para siempre: queda un `Warning` con `OrderId` y no hay reproceso (R1)». Commit `docs(buyers): note the deferred buyer link has no retry`.
- [ ] **Step 3:** merge a `develop` con `--no-ff` (nunca a `main`), desde el checkout principal:

```powershell
Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git branch --show-current
git switch develop
git merge --no-ff feature/pedidos-del-comprador
```

- [ ] **Step 4:** **`docs/estado.md` se actualiza en `develop`, al mergear, nunca en la rama**: fila nueva en «Trabajo en curso» («Pedidos del comprador: historial y anulación desde la tienda», construido, con el sha del merge, la migración `AddOrderBuyerAccount`, las tres rutas, D3/D5, «Orden de salida: backend primero; el storefront después; requiere pedidos directos ya desplegado») y la entrada en el Registro, en un commit `docs: record the buyer orders merge in estado` inmediatamente después del merge.

---

## Autorrevisión (hecha al escribir)

- **Cobertura del spec:** §4.2 columna/índice/migración → T3; `CreateFromStore` → T2; sellado y enlace diferido → T10/T11 (bloque D4). §4.3 puerto, adaptador con scope, handlers, `TenantId` en `BuyerStorefront` → T5/T6. §4.4 regla D5, actor sin miembro, motivo fijo, historial, auditoría por integración, evento, carrera → 409 → T2/T4/T7. §4.5 contrato (rutas, DTOs, orden, códigos) → T5/T6/T7. §4.6 pruebas → cada una tiene tarea (con las reubicaciones de «Discrepancias»). §4.7 despliegue y `estado.md` → T12.
- **Tipos:** `BuyerOrderDetail`/`BuyerOrderDetailDto`, `IBuyerOrderGateway`/`IBuyerOrders`, `BuyerOrderErrors.NotCancellableByBuyer`, `BuyerStorefront(Host, Name, TenantId)`, `SearchByBuyerAsync`/`FindByBuyerAsync` se usan con el mismo nombre en todas las tareas.
- **Pendiente del owner (no bloquea):** ninguno nuevo; el texto del motivo fijo y el código nuevo salen del spec.
