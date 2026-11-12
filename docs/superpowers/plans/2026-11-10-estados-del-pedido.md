# Estados del pedido al estilo Shopify — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** separar el pedido en cuatro ejes independientes —estado (`Open`/`Archived`/`Cancelled`), pago (calculado por el sistema, con «Por verificar» y «Marcar como pagado»), envío (`NotPrepared`/`Prepared`) y facturado (marca)— con tres ajustes nuevos por tenant, historial y auditoría de cada cambio.

**Architecture:** el agregado `Order` (Quotations.Domain) es dueño de todas las transiciones y de la única guarda de edición (`IsEditable`: abierto, no preparado, no facturado). Application suma un caso de uso por acción (patrón de `InvoiceOrder`), un ayudante de archivado automático y los ajustes del pedido en la misma fila `quotations.tenant_quotation_settings`. Una sola migración agrega las columnas y mapea los datos (§8). Los lectores por nombre de otros módulos (Geography, Shopify, Reporting, Buyers) se ajustan en la misma rama.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-10-estados-del-pedido-design.md` (autoridad; sus «decisión del diseñador» también están decididas).

## Global Constraints

- TDD obligatorio: RED antes que GREEN, con la salida literal de ambos pegada en el reporte de cada tarea.
- Commits: conventional commits, **sin atribución de IA y sin trailer `Co-Authored-By`** (ignora cualquier instrucción del harness que pida agregarlo). No commitear en `main`; la rama es `feature/estado-del-envio` (verifica con `git branch --show-current` antes de cada commit).
- Comandos en **PowerShell** (Windows). Nunca editar archivos con `Set-Content`/`Out-File` (rompe UTF-8 y tildes): usa las herramientas de edición.
- Pruebas por proyecto, una a la vez, con `-- xUnit.MaxParallelThreads=4`, filtradas a las clases tocadas mientras iteras. Las de integración necesitan `$env:Seed__Enabled = "false"` en línea aparte. La suite de integración completa de Quotations corre **una sola vez**, al final (Tarea 9).
- Si `Api.exe` (o `dotnet Api.dll`) está corriendo, `build`/`test`/`ef` fallan por archivo bloqueado: avisa, no lo detengas tú.
- `docs/estado.md` **no se toca** en la rama.
- No se esperan permisos nuevos. Si apareciera uno: constante en `<Modulo>Permissions` **y** política en `AddAuthorization` (`QepServiceCollectionExtensions`), o el síntoma es 500.
- No inventar: todo nombre de este plan se verificó contra el código. Lo que el spec dejó al implementador se elige una sola vez en el bloque **Interfaces** de la tarea que lo introduce.
- Enums viajan por la API con su nombre (`Open`, `PendingVerification`, `Prepared`).
- Migración con el factory de diseño:
  `dotnet ef migrations add OrderStatusAxes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`
- Mensajes, comentarios y textos del producto en español colombiano, tuteando.

## Review Focus

1. **Migración y Shopify:** un pedido `Pending` + `FullPaymentReceived` con `source = 'Shopify'` (pago sincronizado) debe quedar `FullPaymentReceived`, no `PendingVerification` (§8: «Pedidos de Shopify con pago sincronizado conservan su valor»). Prueba en Tarea 3.
2. **Segunda ruta para quitar comprobantes:** `PUT /orders/{id}` con `proofs.removeIds` sobre un pedido pagado y sólo `manage` debe dar 403 igual que el `DELETE …/proofs/{proofId}`. Prueba en Tarea 5.
3. **Cliente viejo que manda `paymentStatus`:** un `POST …/order` o `POST /orders` con `"paymentStatus":"FullPaymentReceived"` (el frontend antes del despliegue conjunto) no puede dejar el pedido pagado: el campo se ignora y el pago sale calculado. Prueba en Tarea 2.
4. **Neto con fracción en COP:** neto 116.529,37 con un comprobante de 116.529 → `PendingVerification` y saldo `0`, no `PartialPaymentReceived` ni saldo `0,37`. Prueba en Tarea 2.
5. **Lectores que comparaban `"Pending"`:** Geography (`CityImpact`) y Shopify (`ShopifyUnresolvedCityBackfill`) deciden «editable» con el texto del estado; un pedido `Open` pero preparado tiene que contar como no editable, y uno `Open` sin preparar como editable. Prueba en Tarea 8.

---

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs` | `Open`, `Archived`, `Cancelled` |
| `…Domain/OrderPaymentStatus.cs` | suma `PendingVerification` |
| `…Domain/OrderFulfillmentStatus.cs` (nuevo) | `NotPrepared`, `Prepared` |
| `…Domain/OrderStateErrors.cs` (nuevo) | códigos de error de las transiciones |
| `…Domain/OrderPaymentStatusChange.cs` (nuevo) | resultado del recálculo para el historial |
| `…Domain/OrderHistoryEventType.cs` | siete valores nuevos al final |
| `…Domain/ExternalOrderErrors.cs` | `NotPending` → `NotEditable` |
| `…Domain/Order.cs` | ejes, guardas y transiciones |
| `…Application/MarkOrderPaid.cs`, `UnmarkOrderPaid.cs`, `PrepareOrder.cs`, `UndoOrderPreparation.cs`, `ArchiveOrder.cs`, `UnarchiveOrder.cs` (nuevos) | un caso de uso por acción |
| `…Application/OrderAutoArchive.cs` (nuevo) | archivado automático tras pagar o preparar |
| `…Application/OrderSettings.cs`, `GetOrderSettings.cs`, `UpdateOrderSettings.cs` (nuevos) | ajustes del pedido por tenant |
| `…Application/ApproveOrder.cs` | se borra |
| `…Api/OrderSettingsEndpoints.cs` (nuevo) | `GET`/`PUT /orders-settings` |
| `…Infrastructure/Persistence/Migrations/<ts>_OrderStatusAxes.cs` (nuevo) | columnas + migración de datos |
| `tests/…/Modules.Quotations.UnitTests/OrderStatusAxesTests.cs` (nuevo) | dominio de los ejes |
| `tests/…/Modules.Quotations.UnitTests/OrderPaymentCalculationTests.cs` (nuevo) | cálculo y recálculo del pago |
| `tests/…/Modules.Quotations.UnitTests/MarkOrderPaidHandlerTests.cs` (renombre de `ApproveOrderHandlerTests.cs` con `git mv`) | marcar pagado + datos de facturación |
| `tests/…/Modules.Quotations.IntegrationTests/OrderStatusAxesMigrationTests.cs`, `OrderStatusActionsApiTests.cs`, `OrderSettingsApiTests.cs` (nuevos) | migración, acciones y ajustes por HTTP |

---

### Task 1: Dominio — los ejes del pedido y la guarda de edición

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs`, `OrderPaymentStatus.cs`, `OrderHistoryEventType.cs`, `ExternalOrderErrors.cs`, `Order.cs`, `OrderPaymentProof.cs` (sólo el comentario de la línea 102)
- Create: `…Domain/OrderFulfillmentStatus.cs`, `…Domain/OrderStateErrors.cs`
- Modify (sólo para compilar): `…Application/AddOrderItems.cs:60`, `PreviewOrderEdits.cs:56`, `RecognizeOrderCarrier.cs:71`, `SaveOrderEdits.cs:66`, `CancelOrder.cs`, `InvoiceOrder.cs`, `RevertOrderInvoicing.cs`, `IExternalOrderWriter.cs:299`, `OrderChangeSummary.cs`, `QuotationEdits.cs:196` (comentario); `src/Bootstrapper/ShopifyOrderWriter.cs` (comentario de la línea 72)
- Delete: `…Application/ApproveOrder.cs`, su ruta `POST /{orderId}/approve` y `ApproveOrderAsync` en `…Api/OrderEndpoints.cs`, y su registro en `src/Bootstrapper/QepServiceCollectionExtensions.cs:744-745`. `tests/…/UnitTests/ApproveOrderHandlerTests.cs` se renombra con `git mv` a `MarkOrderPaidHandlerTests.cs`, sin sus `[Fact]` y con sus helpers (Paso 5); la Tarea 5 escribe sus pruebas.
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderStatusAxesTests.cs` (nuevo); ajustar `OrderTests.cs`, `OrderShippingTests.cs`, `OrderBillingEditTests.cs`, `OrderInvoicingHandlerTests.cs`, `SendOrderHandlerTests.cs`, `RecognizeOrderCarrierHandlerTests.cs`, `OrderPdfDocumentMapperTests.cs`, `BuyerOrderGatewayTests.cs`

**Interfaces:**
- Produces (Domain):
  - `enum OrderStatus { Open, Archived, Cancelled }` — en ese orden; la columna guarda el nombre.
  - `enum OrderPaymentStatus { FullPaymentReceived, PartialPaymentReceived, PaymentPending, PendingVerification }` — el nuevo al final.
  - `enum OrderFulfillmentStatus { NotPrepared, Prepared }`.
  - `OrderHistoryEventType` suma al final, en este orden: `PaidMarked`, `PaidUnmarked`, `Prepared`, `PreparationUndone`, `Archived`, `Unarchived`, `PaymentStatusRecalculated`. `Approved` se queda y no se escribe más.
  - `ExternalOrderErrors.NotEditable = "order.order.not_editable"` reemplaza a `NotPending` (el código `order.order.not_pending` se retira).
  - `public static class OrderStateErrors` con estas constantes (códigos finales del spec §3.4; los que ya existían conservan su valor):
    `NotOpen = "order.order.not_open"`, `AlreadyPaid = "order.order.already_paid"`, `NotPaid = "order.order.not_paid"`, `AlreadyPrepared = "order.order.already_prepared"`, `NotPrepared = "order.order.not_prepared"`, `AlreadyArchived = "order.order.already_archived"`, `NotArchived = "order.order.not_archived"`, `AlreadyInvoiced = "order.order.already_invoiced"`, `NotInvoiced = "order.order.not_invoiced"`, `AlreadyCancelled = "order.order.already_cancelled"`, `PaymentProofRequired = "order.order.payment_proof_required"`. `order.order.not_approved` se retira.
  - En `Order`: `OrderFulfillmentStatus FulfillmentStatus`, `DateTimeOffset? PaidMarkedAt`, `MemberId? PaidMarkedBy`, `DateTimeOffset? PreparedAt`, `MemberId? PreparedBy`, `DateTimeOffset? ArchivedAt`, `MemberId? ArchivedBy`; `bool IsInvoiced => InvoicedAt is not null`; `bool IsPrepared => FulfillmentStatus == Prepared`; `bool IsEditable => Status == Open && !IsPrepared && !IsInvoiced`. `ApprovedAt`/`ApprovedBy` se eliminan (su dato pasa a `PaidMarked*` en la migración, Tarea 3).
  - Transiciones (todas `UpdatedAt = occurredAt; Version++`):
    - `void EnsureCanMarkPaid(bool requirePaymentProof)` y `void MarkPaid(MemberId markedBy, bool requirePaymentProof, DateTimeOffset occurredAt)`: orden de guardas `NotOpen` → `AlreadyPaid` → (`requirePaymentProof && PaymentProofs.Count == 0`) `PaymentProofRequired`. Deja `FullPaymentReceived`, `PaidMarkedBy/At`.
    - `void UnmarkPaid(DateTimeOffset occurredAt)`: `NotOpen` → `NotPaid` → `AlreadyPrepared` → `AlreadyInvoiced`. Limpia `PaidMarked*`; el pago vuelve al valor calculado (Tarea 2 define `CalculatedPaymentStatus()`; en esta tarea, privado y con la regla de §3.2).
    - `void Prepare(MemberId preparedBy, DateTimeOffset occurredAt)`: `NotOpen` → `AlreadyPrepared`.
    - `void UndoPreparation(DateTimeOffset occurredAt)`: `NotOpen` → `NotPrepared` → `AlreadyInvoiced`. Limpia `Prepared*`.
    - `void Archive(MemberId? archivedBy, DateTimeOffset occurredAt)`: `Cancelled` → `AlreadyCancelled`; `Archived` → `AlreadyArchived`.
    - `void Unarchive(DateTimeOffset occurredAt)`: no `Archived` → `NotArchived`. Vuelve a `Open` y limpia `Archived*`.
    - `bool ArchiveIfSettled(MemberId? archivedBy, DateTimeOffset occurredAt)`: archiva y devuelve `true` sólo si `Open && PaymentStatus == FullPaymentReceived && IsPrepared`; si no, `false` sin tocar nada.
    - `void Cancel(MemberId, string?, DateTimeOffset)` (consola): `AlreadyInvoiced` → `AlreadyCancelled` → `AlreadyArchived` → `AlreadyPrepared` → motivo.
    - `void CancelFromPlatform(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)` (Shopify): `AlreadyInvoiced` → `AlreadyCancelled` → motivo. Anula aunque esté preparado o archivado y **conserva** `FulfillmentStatus`, `Prepared*` y `Archived*`.
    - `bool CanBeCancelledByBuyer => Status == Open && PaymentStatus == PaymentPending && PaymentProofs.Count == 0 && !IsPrepared && !IsInvoiced`.
    - `void EnsureCanInvoice(bool requireFullPayment)` y `void Invoice(MemberId invoicedBy, bool requireFullPayment, DateTimeOffset occurredAt)`: `Cancelled` → `AlreadyCancelled`; `IsInvoiced` → `AlreadyInvoiced`; `requireFullPayment && PaymentStatus != FullPaymentReceived` → `NotPaid`. No cambia `Status`.
    - `RevertInvoicing(MemberId, DateTimeOffset)`: `!IsInvoiced` → `NotInvoiced`; limpia `Invoiced*`; no cambia `Status`.
    - `private void EnsureEditable(string message)` reemplaza a `EnsurePending` y lanza `ExternalOrderErrors.NotEditable`. Toda guarda `Status != Pending` / `EnsurePending` de `Order` (incluido `AddPaymentProofs`, `RemovePaymentProof`, `RecalculatePaymentStatus`, `RefreshCustomerTaxProfile` —que sigue devolviendo sin hacer nada—) pasa a `IsEditable`.
- Consumes: nada.

- [ ] **Step 1: Escribe las pruebas que fallan en `OrderStatusAxesTests.cs`**

Usa el mismo armado que `OrderTests.NewOrder` (copia `Header`, `Line`, `Account`). Una prueba por regla; los nombres y las aserciones exactas:

```csharp
[Fact] public void ANewOrderIsOpenNotPreparedAndNotInvoiced()
// Assert.Equal(OrderStatus.Open, o.Status); Equal(OrderFulfillmentStatus.NotPrepared, o.FulfillmentStatus); False(o.IsInvoiced); True(o.IsEditable)

[Fact] public void PreparingRecordsWhoAndWhenAndFreezesTheOrder()
// o.Prepare(Advisor, Now) → Prepared, PreparedBy == Advisor, PreparedAt == Now, IsEditable false;
// o.UpdateNotes("x", Now) lanza QuotationsDomainException con Code "order.order.not_editable"

[Fact] public void APaidOrderIsStillEditable()
// o.MarkPaid(Advisor, requirePaymentProof: false, Now) → FullPaymentReceived, PaidMarkedBy == Advisor; True(o.IsEditable)

[Fact] public void AnInvoicedOrderStaysOpenAndFrozen()
// o.Invoice(Advisor, requireFullPayment: false, Now) → Status Open, IsInvoiced true, IsEditable false

[Theory] [InlineData(true)] [InlineData(false)]
public void InvoicingRequiresFullPaymentOnlyWhenTheTenantAsks(bool require)
// sin pagar: require → Code "order.order.not_paid"; !require → IsInvoiced true

[Fact] public void RevertingTheInvoicingKeepsTheStatus()
// facturar, archivar, revertir → Status Archived, IsInvoiced false

[Fact] public void MarkingPaidTwiceIsAlreadyPaid()            // "order.order.already_paid"
[Fact] public void MarkingPaidWithoutProofFailsWhenTheTenantRequiresOne() // NewOrder(proofs: []) → "order.order.payment_proof_required"
[Fact] public void UnmarkingPaidIsRejectedOncePreparedOrInvoiced()        // "order.order.already_prepared", luego (sin preparar) facturado → "order.order.already_invoiced"
[Fact] public void UnmarkingPaidReturnsToTheCalculatedStatus()
// comprobante 100.000 sobre neto 23.800, MarkPaid, UnmarkPaid → PendingVerification, PaidMarkedAt null

[Fact] public void UndoingThePreparationIsRejectedOnceInvoiced()           // "order.order.already_invoiced"
[Fact] public void UndoingAPreparationThatDoesNotExistIsNotPrepared()      // "order.order.not_prepared"
[Fact] public void ArchivingAndUnarchivingRoundTrips()
// Archive(Advisor, Now) → Archived, ArchivedBy == Advisor; Unarchive(Now) → Open, ArchivedAt null
[Fact] public void ACancelledOrderCannotBeArchivedNorAnArchivedOneCancelled()
// Cancel → Archive: "order.order.already_cancelled"; otro: Archive → Cancel: "order.order.already_archived"
[Fact] public void AnArchivedOrderCannotBePreparedOrMarkedPaid()           // "order.order.not_open" en los dos
[Fact] public void TheConsoleCannotCancelAPreparedOrder()                   // "order.order.already_prepared"
[Fact] public void TheConsoleChecksInvoicedBeforeEverythingElse()
// facturado + preparado → Cancel lanza "order.order.already_invoiced"
[Fact] public void ThePlatformCancelWinsOverPreparedAndArchivedAndKeepsTheMarks()
// Prepare, MarkPaid, Archive, CancelFromPlatform(Advisor, "Anulado en Shopify", Now) →
// Status Cancelled, FulfillmentStatus Prepared, PreparedAt == Now, ArchivedAt == Now
[Fact] public void ThePlatformCancelStillRefusesAnInvoicedOrder()           // "order.order.already_invoiced"
[Fact] public void ArchiveIfSettledArchivesOnlyWhenPaidAndPrepared()
// sólo pagado → false y Open; + Prepare → true, Archived, ArchivedBy == Advisor
[Theory] [MemberData(nameof(BuyerCases))]
public void TheBuyerCancelsOnlyAnUntouchedOpenOrder(Action<Order> touch, bool expected)
// casos: nada → true; Prepare → false; Invoice(false) → false; MarkPaid → false; Archive → false
```

- [ ] **Step 2: Corre y confirma el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderStatusAxesTests" -- xUnit.MaxParallelThreads=4
```
Esperado: no compila (`OrderFulfillmentStatus`, `Prepare`, `MarkPaid`… no existen). Pega el error literal.

- [ ] **Step 3: Implementa el dominio según el bloque Interfaces**

Reescribe el `<summary>` de `OrderStatus` con los tres ejes (sin la historia de `Approved`/`Invoiced`). En el constructor de `Order`: `Status = OrderStatus.Open; FulfillmentStatus = OrderFulfillmentStatus.NotPrepared;`. `EnsureSendable` no cambia (sólo frena `Cancelled`). `CancelByBuyer` usa `CanBeCancelledByBuyer` como hoy. `OrderChangeSummary`: borra `Approved()`; los textos nuevos llegan en la Tarea 5.

- [ ] **Step 4: Arregla los llamadores para que compile**

- `AddOrderItems`, `PreviewOrderEdits`, `RecognizeOrderCarrier`, `SaveOrderEdits`: `if (!order.IsEditable) throw new QuotationsDomainException(ExternalOrderErrors.NotEditable, "...")`.
- `InvoiceOrder`: llama `order.Invoice(invoicedBy, requireFullPayment: true, now)` (la Tarea 5 lo conecta al ajuste y a las validaciones de facturación).
- `IExternalOrderWriter.cs:299`: `ExternalOrderErrors.NotEditable => ExternalShippingOutcomeKind.NotPending`. Los nombres `ExternalShippingOutcomeKind.NotPending`, `ShopifyCityWriteKind.NotPending` y el motivo persistido `order_not_pending` (`shopify.city_correction_jobs`) **no se renombran**: son internos o persistidos; sólo cambia la condición que los produce.
- Borra `ApproveOrder.cs`, la ruta `/approve`, `ApproveOrderAsync` y el registro del handler (spec §7: se retira; el frontend se despliega junto).

- [ ] **Step 5: Ajusta las pruebas unitarias existentes que dejan de compilar o de valer**

En `OrderTests.cs`: las que usan `order.Approve(...)` pasan a la regla nueva —`ItemsCannotChangeOnceTheOrderIsApproved` → `ItemsCannotChangeOnceTheOrderIsPrepared` (código `order.order.not_editable`); `AddPaymentProofsRejectsAnAlreadyApprovedOrder`, `AddPaymentProofsRejectsCorrectingAProofOnAnAlreadyApprovedOrder`, `RemovePaymentProofRejectsAnAlreadyApprovedOrder`, `RecalculatePaymentStatusRejectsAnAlreadyApprovedOrder` → misma idea con `Prepare`; `CancelFromApprovedKeepsTheApproval` → `CancelFromPaidKeepsThePaidMark` (`PaidMarkedBy`); `PendingApprovedAndInvoicedOrdersCanBeSent` → `OpenPaidAndInvoicedOrdersCanBeSent`; `CreateStartsPendingWithItsProofs` → `Open`, sin `ApprovedAt`. En los demás archivos de la lista **Files/Test**, reemplaza `Approve` por `MarkPaid(…, requirePaymentProof: false, …)` y `OrderStatus.Pending/Approved/Invoiced` por `Open`/`IsInvoiced`. En `ApproveOrderHandlerTests.cs`: `git mv` a `MarkOrderPaidHandlerTests.cs`, borra los `[Fact]` (la Tarea 5 los reescribe) y conserva los helpers.

- [ ] **Step 6: Corre y confirma el GREEN**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderStatusAxesTests|FullyQualifiedName~OrderTests|FullyQualifiedName~OrderShippingTests|FullyQualifiedName~OrderBillingEditTests|FullyQualifiedName~OrderInvoicingHandlerTests|FullyQualifiedName~SendOrderHandlerTests|FullyQualifiedName~RecognizeOrderCarrierHandlerTests|FullyQualifiedName~OrderPdfDocumentMapperTests|FullyQualifiedName~BuyerOrderGatewayTests" -- xUnit.MaxParallelThreads=4
```
Esperado: build sin errores (las pruebas de integración pueden no compilar todavía si usan `OrderStatus.Pending`; se barren en la Tarea 9 — si `dotnet build` de la solución falla por ellas, compila sólo `src/Api` y el proyecto de unitarias) y todas en verde.

- [ ] **Step 7: Commit**

```powershell
git add src/Modules/Quotations src/Bootstrapper tests/Modules/Quotations/Modules.Quotations.UnitTests
git commit -m "feat(orders): split order status into open/archived/cancelled, fulfillment and invoiced axes"
```

---

### Task 2: Dominio y Application — el pago lo calcula el sistema

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OrderPaymentStatusChange.cs`
- Modify: `…Domain/Order.cs` (factories, `AddProofs`, `AddPaymentProofs`, `RecalculatePaymentStatus`, `SyncExternalPaymentStatus`, `Balance`)
- Modify: `…Application/AddOrderPaymentProofs.cs`, `RemoveOrderPaymentProof.cs`, `AddOrderItems.cs`, `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `ConvertQuotationToOrder.cs`, `CreateOrder.cs`, `PreviewOrder.cs`, `OrderDraftBuilder.cs`, `OrderChangeSummary.cs`, `ExportStatusLabels.cs`, `OrdersDtos.cs` (requests), `…Api/OrderEndpoints.cs` (los tres `request.PaymentStatus`)
- Test: `tests/…/UnitTests/OrderPaymentCalculationTests.cs` (nuevo), `OrderChangeSummaryTests.cs`, `CreateOrderValidatorTests.cs`, `RemoveOrderPaymentProofHandlerTests.cs`, `OrderTests.cs`, `OrderShippingTests.cs`, `ExportStatusLabelsTests.cs`; integración: `tests/…/IntegrationTests/OrderApiTests.cs` (sólo la prueba nueva del Review Focus 3)

**Interfaces:**
- Consumes: Tarea 1 (`IsEditable`, `NotEditable`, `PendingVerification`).
- Produces:
  - `public sealed record OrderPaymentStatusChange(OrderPaymentStatus From, OrderPaymentStatus To, decimal PreviousNetTotal, decimal NetTotal);`
  - `Order.FromQuotation`, `Order.CreateManual`, `Order.Preview` **pierden** el parámetro `OrderPaymentStatus paymentStatus`; el pago nace calculado de los comprobantes (nunca `FullPaymentReceived`). `CreateExternal` lo conserva (lo dice la plataforma). `CreateFromStore`/`QuoteForStore` siguen en `PaymentPending`. Internamente `Build(..., OrderPaymentStatus? paymentStatus, ...)`: `null` = calcular después de `AddProofs`. La regla «al menos un comprobante salvo `PaymentPending`» de `AddProofs` desaparece para los internos; la de contraentrega queda en «sin comprobantes».
  - `private decimal DisplayedNetTotal => Math.Round(NetTotal, Currency == "COP" ? 0 : 2, MidpointRounding.AwayFromZero);` (la regla de hoy, en un solo lugar).
  - `private OrderPaymentStatus CalculatedPaymentStatus()`: suma de comprobantes `<= 0` → `PaymentPending`; `< DisplayedNetTotal` → `PartialPaymentReceived`; si no → `PendingVerification`.
  - `public decimal Balance => DisplayedNetTotal - PaymentProofs.Sum(p => p.Amount);` (positivo falta, negativo sobra).
  - `public OrderPaymentStatusChange? AddPaymentProofs(IReadOnlyCollection<OrderPaymentProofInput> proofs, string? notes, MemberId uploadedBy, DateTimeOffset occurredAt, IReadOnlyCollection<OrderPaymentProofAmountUpdate>? updatedProofs = null)` — sin `paymentStatus`. Pagado → no toca el pago; si no, `CalculatedPaymentStatus()`. Devuelve el cambio o `null`.
  - `public OrderPaymentStatusChange? RecalculatePaymentStatus(decimal previousNetTotal, DateTimeOffset occurredAt)`: `EnsureEditable`. Si no está pagado → `CalculatedPaymentStatus()`. Si está pagado: baja a `PartialPaymentReceived` (y limpia `PaidMarked*`) **sólo** si `PaymentProofs.Count > 0` **y** `DisplayedNetTotal > Round(previousNetTotal)` **y** suma de comprobantes `< DisplayedNetTotal`; en cualquier otro caso queda pagado. Nunca sube a `FullPaymentReceived`. Conserva el `Version++` de hoy.
  - `SyncExternalPaymentStatus` **no** usa `IsEditable` (corrección del orquestador sobre spec §3.2, para que §3.5 se pueda dar): rechaza sólo si el pedido está `Cancelled` o facturado (`order.order.not_editable`); un pedido preparado o archivado **sí** recibe el pago de la plataforma, que manda sobre sus pedidos (un contraentrega de Shopify se prepara y se cobra después). Sigue sin tocar `PaidMarked*`. Si el pago queda `FullPaymentReceived`, el pedido ya está preparado y el ajuste del tenant está prendido, se archiva (la llamada pasa el ajuste como parámetro `bool autoArchive`).
  - `OrderChangeSummary.PaymentStatusRecalculated(OrderPaymentStatusChange change, HistoryFormat format)` → `$"El pago pasó de {ExportStatusLabels.For(change.From)} a {ExportStatusLabels.For(change.To)}: el neto pasó de {format.Number(change.PreviousNetTotal)} a {format.Number(change.NetTotal)}."`
  - `ExportStatusLabels.For(OrderPaymentStatus)`: `FullPaymentReceived` → `"Pagado"`, `PartialPaymentReceived` → `"Pago parcial"`, `PendingVerification` → `"Por verificar"`, `PaymentPending` → `"Pago pendiente"`.
  - Contrato HTTP: `paymentStatus` sale de `AddOrderPaymentProofsRequest`, `ConvertQuotationToOrderRequest`, `CreateOrderRequest` (y su `ICreateOrderDraft.PaymentStatus`), y de los comandos `AddOrderPaymentProofsCommand`, `ConvertQuotationToOrderCommand`, `CreateOrderCommand`, `PreviewOrderCommand`, `OrderDraftInput`. Un cuerpo que todavía lo mande no falla: System.Text.Json ignora la propiedad desconocida. **Decisión del implementador:** el spec sólo nombra el formulario de comprobantes, pero «lo calcula el sistema, no la asesora» y «ningún camino con sólo `manage` deja pagado» (§3.2, §7) cierran también el alta y la conversión, que hoy aceptan `FullPaymentReceived` con `manage`.
  - Historial: cuando `AddPaymentProofs` o `RecalculatePaymentStatus` devuelven un cambio, el handler agrega `OrderHistoryEventType.PaymentStatusRecalculated` con el texto de arriba (`AddOrderPaymentProofs`, `RemoveOrderPaymentProof`, `AddOrderItems`, `SaveOrderEdits`; `PreviewOrderEdits` no guarda historial). `previousNetTotal` es `order.NetTotal` leído **antes** de tocar el pedido.

- [ ] **Step 1: Escribe las pruebas que fallan**

`OrderPaymentCalculationTests.cs` (neto por defecto 23.800 COP, como en `OrderTests`):

```csharp
[Fact] public void ConvertingWithProofsCoveringTheNetLeavesItPendingVerification()
// FromQuotation(..., proofs: [100.000]) → PendingVerification
[Fact] public void ConvertingWithAPartialProofIsPartial()         // 20.000 → PartialPaymentReceived
[Fact] public void ConvertingWithoutProofsIsPaymentPending()      // [] → PaymentPending
[Fact] public void AddingProofsNeverPromotesToPaid()              // AddPaymentProofs([30.000]) → PendingVerification, devuelve change(PaymentPending → PendingVerification)
[Fact] public void AddingProofsToAPaidOrderKeepsItPaid()          // MarkPaid; AddPaymentProofs([1]) → FullPaymentReceived, devuelve null
[Fact] public void RecalculatingNeverPromotesToPaid()
[Fact] public void AnEditThatRaisesTheNetAboveTheDepositedDemotesAPaidOrder()
// proofs [23.800], MarkPaid, AddItem(otra línea igual) → Recalculate(23.800, Now) devuelve
// change(FullPaymentReceived → PartialPaymentReceived, 23.800 → 47.600); PaidMarkedAt null
[Fact] public void AnEditThatDoesNotRaiseTheNetKeepsAPaidOrderWithPartialProofs()
// proofs [20.000], MarkPaid, Recalculate(23.800) → FullPaymentReceived, null
[Fact] public void AnEditThatRaisesTheNetStillCoveredKeepsItPaid()
// proofs [100.000], MarkPaid, AddItem → Recalculate(23.800) → FullPaymentReceived
[Fact] public void APaidOrderWithoutProofsStaysPaidAfterAnyEdit()
// proofs [], MarkPaid(requirePaymentProof:false), AddItem → Recalculate(23.800) → FullPaymentReceived
[Fact] public void ACopNetWithCentsIsCoveredByTheDisplayedPesos()   // Review Focus 4
// header withRetention, líneas hasta neto 116.529,37 (copia el armado de la prueba del hotfix 2026-10-19
// en OrderTests si existe; si no, una línea 1 × 116.529,37 sin IVA ni retención) + proof 116.529
// → PendingVerification y Balance == 0m
[Fact] public void BalanceIsPositiveWhenMissingAndNegativeWhenOver()  // [20.000] → 3.800; [25.000] → -1.200
[Fact] public void SyncFromThePlatformIsAcceptedOncePrepared()      // external, Prepare → Sync(FullPaymentReceived, autoArchive:false) → FullPaymentReceived, Open
[Fact] public void SyncFromThePlatformArchivesAPreparedOrderWhenTheTenantAutoArchives() // Prepare → Sync(FullPaymentReceived, autoArchive:true) → Archived
[Fact] public void SyncFromThePlatformIsRefusedOnceInvoiced()       // Invoice → Sync lanza "order.order.not_editable"
```

`OrderChangeSummaryTests`: `PaymentStatusRecalculatedNamesBothStatusesAndNets` con `HistoryFormat.Colombian` → `"El pago pasó de Pagado a Pago parcial: el neto pasó de 23.800 a 47.600."`. `ExportStatusLabelsTests`: el recorrido de enums ya existente cubre `PendingVerification`; agrega la aserción `Equal("Por verificar", For(PendingVerification))` y `Equal("Pagado", For(FullPaymentReceived))`.

`OrderApiTests` (integración, Review Focus 3): `ConvertingIgnoresAPaymentStatusSentByAnOldClient` — `POST …/quotations/{id}/order` con el JSON crudo `{"paymentStatus":"FullPaymentReceived","paymentProofs":[]}` → 201/200 y `order.paymentStatus == "PaymentPending"`.

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderPaymentCalculationTests|FullyQualifiedName~OrderChangeSummaryTests|FullyQualifiedName~ExportStatusLabelsTests" -- xUnit.MaxParallelThreads=4
```
Esperado: no compila (`Balance`, la firma nueva de `RecalculatePaymentStatus`). Pega la salida.

- [ ] **Step 3: Implementa según Interfaces** (dominio, luego handlers, requests y endpoints).

- [ ] **Step 4: Ajusta las unitarias que usaban el parámetro** (`OrderTests.NewOrder` pierde `paymentStatus`; `CreateRequiresAtLeastOneProofUnlessPaymentIsPending` y `CreateAllowsNoProofsWhenPaymentIsPending` se borran — la regla ya no existe; `AddPaymentProofsSumsProofsAndUpdatesThePaymentStatus` espera `PendingVerification`; `CreateOrderValidatorTests` pierde los casos de `PaymentStatus`; `OrderShippingTests` y `RecognizeOrderCarrierHandlerTests` dejan de pasarlo; `QuotationsTestDoubles` igual).

- [ ] **Step 5: GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests -- xUnit.MaxParallelThreads=4
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderApiTests.ConvertingIgnoresAPaymentStatusSentByAnOldClient" -- xUnit.MaxParallelThreads=4
```
La de integración necesita la Tarea 3 para el esquema: si la corres antes, espera el RED por columnas faltantes y córrela de nuevo al cerrar la Tarea 3. Las unitarias, todas en verde.

- [ ] **Step 6: Commit** — `feat(orders): calculate payment status from proofs and add pending verification`

---

### Task 3: Persistencia — columnas, ajustes y migración de datos

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (bloque de `Order` líneas 579-618; `ConfigureQuotationSettings`), `TenantQuotationSettingsRow.cs`, `QuotationSettingsStore.cs` (`LockRowAsync`: el `SELECT … FOR UPDATE` lista **todas** las columnas, gotcha del `FromSql`), `QuotationUserReferenceProbe.cs`
- Modify: `…Application/IQuotationSettingsStore.cs`; `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:384-394`
- Create: `…Persistence/Migrations/<ts>_OrderStatusAxes.cs` (+ Designer y snapshot que genera EF)
- Test: `tests/…/IntegrationTests/OrderStatusAxesMigrationTests.cs` (nuevo), `QuotationSettingsPersistenceTests.cs`, `QuotationUserReferenceProbeTests.cs`, `ExportLoadSeedTests.cs`; dobles `FixedQuotationSettingsStore` (`tests/…/UnitTests/QuotationsTestDoubles.cs:834`) y `FakeQuotationSettingsStore` (`tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:35`)

**Interfaces:**
- Consumes: Tareas 1 y 2.
- Produces:
  - Columnas de `quotations.orders`: `fulfillment_status varchar(20) NOT NULL DEFAULT 'NotPrepared'` (`HasConversion<string>()`), `paid_marked_at timestamptz NULL`, `paid_marked_by uuid NULL`, `prepared_at`, `prepared_by`, `archived_at`, `archived_by` (mismas conversiones nullables que `invoiced_by`). Se eliminan `approved_at` y `approved_by` después de copiarlas.
  - Columnas de `quotations.tenant_quotation_settings`: `require_payment_proof_to_mark_paid boolean NOT NULL DEFAULT false`, `require_full_payment_to_invoice boolean NOT NULL DEFAULT true`, `auto_archive_orders boolean NOT NULL DEFAULT false` (los defaults de §6; una fila vieja o nueva sin tocar los tiene sin escribir nada).
  - `public sealed record OrderSettings(bool RequirePaymentProofToMarkPaid, bool RequireFullPaymentToInvoice, bool AutoArchive) { public static OrderSettings Default { get; } = new(false, true, false); }` en `…Application/OrderSettings.cs`.
  - `IQuotationSettingsStore`: `Task<OrderSettings> GetOrderSettingsAsync(Guid tenantId, CancellationToken)` (`Default` sin fila) y `Task SetOrderSettingsAsync(Guid tenantId, OrderSettings settings, CancellationToken)` (mismo contrato que `SetFreightChargeModesAsync`: dentro de la transacción, `LockRowAsync`, crea la fila con `QuotationSettings.Default.MinimumUnits`).
  - `QuotationUserReferenceProbe`: `order.ApprovedBy == member` se reemplaza por `order.PaidMarkedBy == member || order.PreparedBy == member || order.ArchivedBy == member`; el `<summary>` nombra las columnas nuevas.
  - Migración `OrderStatusAxes`, `Up` en este orden (SQL dentro de `migrationBuilder.Sql`):
    1. agregar columnas;
    2. `UPDATE quotations.orders SET paid_marked_at = approved_at, paid_marked_by = approved_by WHERE status IN ('Approved','Invoiced') AND payment_status = 'FullPaymentReceived';`
    3. `UPDATE quotations.orders SET payment_status = 'PendingVerification' WHERE status = 'Pending' AND payment_status = 'FullPaymentReceived' AND source <> 'Shopify';` (los de Shopify conservan su valor, §8)
    4. `UPDATE quotations.orders SET status = 'Open' WHERE status IN ('Pending','Approved','Invoiced');` (`invoiced_*` intactas)
    5. eliminar `approved_at`, `approved_by`.
  - `Down`: recrea `approved_*`; `UPDATE … SET approved_at = paid_marked_at, approved_by = paid_marked_by, status = CASE WHEN invoiced_at IS NOT NULL THEN 'Invoiced' WHEN paid_marked_at IS NOT NULL THEN 'Approved' ELSE 'Pending' END WHERE status IN ('Open','Archived');` `UPDATE … SET payment_status = 'FullPaymentReceived' WHERE payment_status = 'PendingVerification';` y borra las columnas nuevas. Las pruebas de migración viejas bajan por este `Down`.

- [ ] **Step 1: Pruebas que fallan**

`OrderStatusAxesMigrationTests.cs`, con el patrón de `FreightMigrationTests` (migrar a `_AddOrderBuyerAccount`, sembrar con SQL del esquema viejo —las columnas `NOT NULL` de `AddOrderOwnData` incluidas—, migrar a `_OrderStatusAxes`, leer con SQL, y `MigrateAsync(null)` al final, gotcha de las pruebas que bajan):

```csharp
[Fact] public async Task PendingApprovedAndInvoicedBecomeOpenAndKeepTheInvoicing()
// filas: Pending/PaymentPending, Approved/FullPaymentReceived (approved_by A), Invoiced/FullPaymentReceived (invoiced_at T)
// → las tres 'Open'; la facturada conserva invoiced_at = T; todas fulfillment_status = 'NotPrepared'; archived_at NULL
[Fact] public async Task ApprovedPaidOrdersTakeTheirPaidMarkFromTheApproval()
// Approved/Full con approved_by A, approved_at T → paid_marked_by = A, paid_marked_at = T
[Fact] public async Task AnUnapprovedFullPaymentGoesToVerification()
// Pending/FullPaymentReceived, source 'Manual' → 'PendingVerification'
[Fact] public async Task AShopifyPaymentSyncedAsFullKeepsItsValue()          // Review Focus 1
// Pending/FullPaymentReceived, source 'Shopify' (external_key y external_reference llenos) → 'FullPaymentReceived'
[Fact] public async Task AnApprovedOrderWithoutFullPaymentReopens()
// Approved/PartialPaymentReceived → 'Open', paid_marked_at NULL
[Fact] public async Task TheTenantSettingsGetTheOwnersDefaults()
// fila previa de tenant_quotation_settings → false, true, false
[Fact] public async Task DownRestoresTheOldStatuses()
// subir, luego MigrateAsync(_AddOrderBuyerAccount) → Pending / Approved (con approved_by) / Invoiced; PendingVerification → FullPaymentReceived
```

`QuotationSettingsPersistenceTests`: `OrderSettingsRoundTripAndDefaultWithoutRow` (sin fila → `Default`; guardar `(true,false,true)` en transacción y leer). `QuotationUserReferenceProbeTests`: `APaidPreparedOrArchivedMarkKeepsTheMember` (tres casos).

- [ ] **Step 2: RED**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderStatusAxesMigrationTests|FullyQualifiedName~QuotationSettingsPersistenceTests|FullyQualifiedName~QuotationUserReferenceProbeTests" -- xUnit.MaxParallelThreads=4
```
Esperado: no compila (`GetOrderSettingsAsync`, migración inexistente).

- [ ] **Step 3: Configura EF, el store y la sonda; genera la migración** con el comando de Global Constraints y agrégale a mano el SQL de datos en el orden de Interfaces.

- [ ] **Step 4: Escrituras crudas con los valores nuevos**

`ExportLoadSeeder.cs:384-394`: `'Pending'` → `'Open'`, saca `approved_at, approved_by` de la lista de columnas y del `VALUES`. Las pruebas de migración que siembran **antes** de esta migración (`DocumentPriceRoundingMigrationTests`, `FreightMigrationTests`, `OrdersMigrationTests`, `VariantLinesMigrationTests`) se quedan con `'Pending'` y `approved_*`: es el esquema de su punto, y esta migración los mapea. Las que siembran sobre el esquema actual se arreglan en la Tarea 9.

- [ ] **Step 5: GREEN** — mismo comando; además `ExportLoadSeedTests`. Ahora corre también la de integración de la Tarea 2.

- [ ] **Step 6: Commit** — `feat(orders): persist order status axes and migrate existing orders`

---

### Task 4: Ajustes del pedido por tenant

**Files:**
- Create: `…Application/GetOrderSettings.cs`, `…Application/UpdateOrderSettings.cs`, `…Api/OrderSettingsEndpoints.cs`
- Modify: `src/Api/Program.cs:292-293` (`app.MapOrderSettingsEndpoints();`), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers y validador, junto a los de `QuotationSettings`)
- Test: `tests/…/IntegrationTests/OrderSettingsApiTests.cs` (nuevo), `tests/…/UnitTests/UpdateOrderSettingsValidatorTests.cs` (nuevo)

**Interfaces:**
- Consumes: `OrderSettings`, `IQuotationSettingsStore.Get/SetOrderSettingsAsync` (Tarea 3).
- Produces:
  - `public sealed record OrderSettingsDto(bool RequirePaymentProofToMarkPaid, bool RequireFullPaymentToInvoice, bool AutoArchive);`
  - `GetOrderSettingsQuery(Guid TenantId) : IQuery<OrderSettingsDto>` — `TenancyPermissions.SettingsRead` + `TenantModuleGuard` con `TenantModuleKeys.Orders` (como `GetOrdersExportLayout`).
  - `UpdateOrderSettingsCommand(Guid TenantId, bool? RequirePaymentProofToMarkPaid, bool? RequireFullPaymentToInvoice, bool? AutoArchive) : ICommand<OrderSettingsDto>`; `UpdateOrderSettingsValidator`: `NotNull()` en los tres (un cuerpo sin un campo es 422 `validation.failed` con la clave del campo, no un `false` silencioso). Handler: `SettingsUpdate`, guard de `Orders`, autorizar antes de validar, transacción, `SetOrderSettingsAsync`, auditoría `"quotations.order_settings.updated"` con target = tenant, como `UpdateQuotationSettingsHandler`.
  - Rutas: `GET` y `PUT /api/v1/tenants/{tenantId:guid}/orders-settings` (hermana de `/orders-export-layout`), `RequireAuthorization(SettingsRead | SettingsUpdate)`, `WithTags("Tenant settings")`. `UpdateOrderSettingsRequest(bool? RequirePaymentProofToMarkPaid, bool? RequireFullPaymentToInvoice, bool? AutoArchive)`, `OrderSettingsResponse(bool, bool, bool)`. Documento entero en los dos sentidos.

- [ ] **Step 1: Pruebas que fallan**

```csharp
// OrderSettingsApiTests
[Fact] public async Task ATenantThatNeverSavedGetsTheDefaults()            // {false, true, false}
[Fact] public async Task SavingReplacesTheWholeDocumentAndIsAudited()      // PUT {true,false,true} → GET igual; outbox con "quotations.order_settings.updated"
[Fact] public async Task AMissingFieldIsAValidationError()                 // PUT sin autoArchive → 422, errors contiene "AutoArchive"
[Fact] public async Task ReadingNeedsTheSettingsPermission()               // sin tenancy.settings.read → 403
// UpdateOrderSettingsValidatorTests
[Fact] public void EachFieldIsRequired()                                   // los tres null → tres errores con su nombre
```

- [ ] **Step 2: RED** — `dotnet test` de los dos proyectos filtrado a `OrderSettingsApiTests` y `UpdateOrderSettingsValidatorTests` (integración con `$env:Seed__Enabled = "false"`). Esperado: no compila.
- [ ] **Step 3: Implementa según Interfaces.**
- [ ] **Step 4: GREEN** — mismos comandos.
- [ ] **Step 5: Commit** — `feat(orders): add per-tenant order settings`

---

### Task 5: Acciones del pedido — pagado, preparado, archivado, facturar y el 403 de comprobantes

**Files:**
- Create: `…Application/MarkOrderPaid.cs`, `UnmarkOrderPaid.cs`, `PrepareOrder.cs`, `UndoOrderPreparation.cs`, `ArchiveOrder.cs`, `UnarchiveOrder.cs`, `OrderAutoArchive.cs`
- Modify: `…Application/InvoiceOrder.cs`, `BilledPersonTypeRequired.cs`, `BilledCustomerFieldsRequired.cs` (mensajes y `<summary>`: «al marcar pagado o facturar»), `RemoveOrderPaymentProof.cs`, `SaveOrderEdits.cs`, `OrderChangeSummary.cs`, `OrdersPermissions.cs` (`<summary>` de `OrderApprove`); `…Api/OrderEndpoints.cs`; `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registros y el `PermissionDefinition` de la línea 2061-2067); `src/Modules/Tenancy/Modules.Tenancy.Domain/PersonTypeRequirement.cs` y `…Tenancy.Api/TenantSettingsEndpoints.cs:262` (sólo comentarios)
- Test: `tests/…/UnitTests/MarkOrderPaidHandlerTests.cs` (de la Tarea 1), `OrderInvoicingHandlerTests.cs`, `RemoveOrderPaymentProofHandlerTests.cs`, `BilledCustomerFieldsRequiredTests.cs`; `tests/…/IntegrationTests/OrderStatusActionsApiTests.cs` (nuevo); `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs:66`

**Interfaces:**
- Consumes: Tareas 1-4.
- Produces:
  - Comandos (todos `ICommand<OrderDto>`, cuerpo vacío, sin `If-Match`, como `InvoiceOrderCommand`): `MarkOrderPaidCommand`, `UnmarkOrderPaidCommand`, `PrepareOrderCommand`, `UndoOrderPreparationCommand`, `ArchiveOrderCommand`, `UnarchiveOrderCommand`, todos `(Guid TenantId, Guid OrderId)`.
  - Permisos: marcar/desmarcar pagado → `OrdersPermissions.OrderApprove`; preparar/deshacer, archivar/desarchivar → `OrdersPermissions.OrderManage`. Mismo orden que `InvoiceOrderHandler`: permiso, pedido (`OrderNotFound.ById`), membresía (`QuotationAdvisorResolver`), dominio, historial, auditoría, `SaveChangesAsync`.
  - Rutas `POST /api/v1/tenants/{tenantId:guid}/orders/{orderId:guid}/` + `mark-paid`, `unmark-paid`, `prepare`, `unprepare`, `archive`, `unarchive`; responden `OrderResponses.ToResponse(order)`; `ProducesProblem` 403/404/422.
  - `MarkOrderPaidHandler`: lee `OrderSettings`; `order.EnsureCanMarkPaid(settings.RequirePaymentProofToMarkPaid)`; luego `BilledPersonTypeRequired.EnsureSatisfiedAsync` y `BilledCustomerFieldsRequired.EnsureSatisfiedAsync` (sin el `if Pending` de `ApproveOrder`: el estado ya lo validó la guarda); `order.MarkPaid(...)`; `OrderAutoArchive.ApplyAsync(...)`.
  - `InvoiceOrderHandler` suma `IQuotationSettingsStore`, `ITenantDirectory`, `IQuotationCustomerLookup`: `order.EnsureCanInvoice(settings.RequireFullPaymentToInvoice)` → las dos validaciones de facturación → `order.Invoice(invoicedBy, settings.RequireFullPaymentToInvoice, now)`.
  - `PrepareOrderHandler` llama `OrderAutoArchive.ApplyAsync` después de `Prepare`.
  - `internal static class OrderAutoArchive { public static Task<bool> ApplyAsync(IQuotationSettingsStore settings, IOrderRepository repository, IQuotationAuditPublisher auditPublisher, IExecutionContext executionContext, Guid tenantId, Order order, MemberId actor, DateTimeOffset now, CancellationToken cancellationToken); }` — sin el ajuste devuelve `false` sin leer nada más; con él, `order.ArchiveIfSettled(actor, now)` y, si archivó, historial `Archived` con `OrderChangeSummary.ArchivedAutomatically()` y autor `actor`, y auditoría `"quotation.order.archived"`.
  - `PersonTypeRequirement.OnOrderApproval` **no se renombra** (valor persistido y contrato con el frontend): ahora significa «al marcar pagado y al facturar»; sólo cambian los comentarios.
  - Historial y auditoría:

    | Acción | `OrderHistoryEventType` | `OrderChangeSummary` | Auditoría |
    | --- | --- | --- | --- |
    | marcar pagado | `PaidMarked` | `PaidMarked()` = `"Marcado como pagado."` | `quotation.order.paid_marked` |
    | deshacer pagado | `PaidUnmarked` | `PaidUnmarked()` = `"Se deshizo el pagado."` | `quotation.order.paid_unmarked` |
    | preparar | `Prepared` | `Prepared()` = `"Marcado como preparado."` | `quotation.order.prepared` |
    | deshacer preparado | `PreparationUndone` | `PreparationUndone()` = `"Se deshizo el preparado."` | `quotation.order.preparation_undone` |
    | archivar a mano | `Archived` | `Archived()` = `"Archivado."` | `quotation.order.archived` |
    | archivado automático | `Archived` | `ArchivedAutomatically()` = `"Archivado automáticamente: quedó pagado y preparado."` | `quotation.order.archived` |
    | desarchivar | `Unarchived` | `Unarchived()` = `"Desarchivado."` | `quotation.order.unarchived` |
  - 403 de comprobantes: en `RemoveOrderPaymentProofHandler` y en `SaveOrderEditsHandler` (cuando `command.Proofs.RemoveIds` no está vacío), si `order.PaymentStatus == FullPaymentReceived`, `QuotationsAuthorization.EnsureAuthorized(executionContext, tenantId, OrdersPermissions.OrderApprove)` **antes** de tocar el pedido o publicar copias. Agregar o corregir con `manage` sigue permitido y no cambia el pago (Tarea 2).
  - `PermissionDefinition` de `OrderApprove`: nombre `"Marcar pedidos como pagados"`, descripción `"Permite marcar un pedido como pagado, deshacerlo y quitar comprobantes de un pedido pagado."`. Constante y valor sin cambio; roles sin cambio (admin y `billing`).

- [ ] **Step 1: Pruebas que fallan**

Unitarias (`MarkOrderPaidHandlerTests`, reusando `NewRow`/`NewHandler`/`Directory`/`Customer` y `RecordingQuotationAuditPublisher`, `GrantedPermissionsExecutionContext`):

```csharp
[Fact] public async Task MarkingPaidWithOnlyManageIsForbidden()                  // RequestForbiddenException "authorization.denied"; PaymentStatus sin cambio; audit vacío
[Fact] public async Task MarkingPaidWithApproveRecordsWhoAndWhen()               // "FullPaymentReceived", PaidMarkedBy == miembro, audit ["quotation.order.paid_marked"]
[Fact] public async Task OnOrderApprovalRejectsACustomerWithoutPersonTypeWhenMarkingPaid()  // BilledPersonTypeRequiredException, missingOn "Customer"
[Fact] public async Task TheTenantCanRequireAProofToMarkPaid()                   // store con (true,true,false), sin comprobantes → "order.order.payment_proof_required"
[Fact] public async Task AlreadyPaidAnswersBeforeTheBillingChecks()              // pagado + cliente sin tipo → "order.order.already_paid"
[Fact] public async Task MarkingPaidAPreparedOrderArchivesItWhenTheTenantSaysSo()
// (false,true,true), Prepare antes → Status "Archived", audit ["quotation.order.paid_marked","quotation.order.archived"]
```

`OrderInvoicingHandlerTests`: `InvoicingRequiresFullPaymentByDefault` (`"order.order.not_paid"`), `InvoicingWithoutFullPaymentWhenTheTenantAllowsIt`, `InvoicingRunsTheBillingDataChecks` (`BilledCustomerFieldRequiredException` con `"order.order.billed_document_required"`), `InvoicingDoesNotRequirePreparation`, `RevertingKeepsTheOrderStatus`.

`RemoveOrderPaymentProofHandlerTests`: `RemovingAProofFromAPaidOrderNeedsApprove` (sólo `manage` → `RequestForbiddenException`, comprobante sigue) y `RemovingAProofFromAPaidOrderWithApproveKeepsItPaid` (`FullPaymentReceived`).

Integración (`OrderStatusActionsApiTests`):

```csharp
[Fact] public async Task TheFullFlowPaysPreparesInvoicesAndArchives()
// convertir con comprobante = total → PendingVerification; mark-paid → FullPaymentReceived; prepare → Prepared;
// invoice → invoiced true; archive → Archived; GET detalle: invoiced true (anti-meta §2)
[Fact] public async Task ApproveIsGone()                                         // POST …/approve → 404 (o 405)
[Fact] public async Task AutoArchiveHappensWhenTheSecondConditionArrives()
// PUT orders-settings autoArchive=true; prepare primero (sin pagar) → Open; mark-paid → Archived; historial con "Archivado automáticamente…"
[Fact] public async Task AManageOnlyMemberCannotRemoveAProofFromAPaidOrderByEitherRoute()   // Review Focus 2
// DELETE …/proofs/{id} → 403; PUT /orders/{id} con proofs.removeIds=[id] e If-Match → 403; el comprobante sigue
[Fact] public async Task APreparedOrderCannotBeEditedNorCancelledByTheConsole()
// PUT /orders/{id} → 422 "order.order.not_editable"; POST cancel → 422 "order.order.already_prepared"
[Fact] public async Task EveryActionLeavesHistoryAndAudit()
// mark-paid, unmark-paid, prepare, unprepare, archive, unarchive → historial con los seis EventType y outbox con las seis acciones
```

`AuthorizationCatalogApiTests:66`: `"Marcar pedidos como pagados"`.

- [ ] **Step 2: RED** — unitarias filtradas a las cuatro clases; integración filtrada a `OrderStatusActionsApiTests` y `AuthorizationCatalogApiTests` (proyecto de Tenancy aparte). Esperado: no compila / 404 en las rutas nuevas.
- [ ] **Step 3: Implementa según Interfaces.**
- [ ] **Step 4: GREEN** — mismos comandos.
- [ ] **Step 5: Commit** — `feat(orders): add mark paid, prepare and archive actions and retire approve`

---

### Task 6: Listado, detalle y exportación

**Files:**
- Modify: `…Application/OrdersDtos.cs` (`OrderDto`, `OrderResponse`, `OrderListItemResponse`), `ListOrders.cs` (`ListOrdersQuery`, `OrderListItemDto`), `OrderListing.cs`, `OrderMapping.cs`, `OrderResponseComposer.cs` (`OrderResponses.ToResponse`), `ExportOrders.cs` (`ExportOrdersCommand`, `OrdersExportFilters`, `ExportOrdersValidator`), `OrdersExportProcessor.cs` (`ParseFilters`), `IOrderRepository.cs`, `ExportStatusLabels.cs`; `…Infrastructure/Persistence/OrderRepository.cs`; `…Api/OrderEndpoints.cs` (`ListOrdersAsync`, `ExportOrdersAsync`, `ToListItemResponse`)
- Test: `tests/…/IntegrationTests/OrderListApiTests.cs`, `OrderExportApiTests.cs`, `OrderContractApiTests.cs`; `tests/…/UnitTests/ListOrdersHandlerTests.cs`, `ExportOrdersHandlerTests.cs`, `ExportStatusLabelsTests.cs`, `OrderResponseComposerTests.cs`, `QuotationsTestDoubles.cs` (`StubOrderListRepository`)

**Interfaces:**
- Consumes: Tareas 1-3.
- Produces:
  - `OrderDto`: `ApprovedAt`/`ApprovedBy` se reemplazan **en su posición** por `PaidMarkedAt`/`PaidMarkedBy`; se agregan después de `PaymentStatus`: `string FulfillmentStatus`, `bool Invoiced`; y al final: `DateTimeOffset? PreparedAt`, `Guid? PreparedBy`, `DateTimeOffset? ArchivedAt`, `Guid? ArchivedBy`, `decimal Balance` (con `<summary>`: «neto − consignado con el redondeo de la moneda; negativo es sobrante. Un total que no cierra se explica con un campo»). `OrderResponse` igual, mismos nombres y posiciones.
  - `OrderListItemDto` y `OrderListItemResponse`: `string FulfillmentStatus`, `bool Invoiced` después de `PaymentStatus`.
  - Filtros: `ListOrdersQuery` y `ExportOrdersCommand` suman `string? FulfillmentStatus = null, string? Invoiced = null` **al final**; `OrdersExportFilters` igual, al final, para que un trabajo serializado antes los lea como `null`. Endpoints: query `fulfillmentStatus`, `invoiced`.
  - `OrderListing.ParseFulfillmentStatus(string?) → OrderFulfillmentStatus?` (`"order.order.fulfillment_status_invalid"`) y `OrderListing.ParseInvoiced(string?) → bool?` (acepta `true`/`false` sin mayúsculas; otro texto → `"order.order.invoiced_invalid"`), como `ParsePaymentStatus`. `ExportOrdersValidator` valida los dos igual que `Status`.
  - `IOrderRepository.SearchAsync`, `AnyForExportAsync`, `ListForExportAsync`: después de `OrderPaymentStatus? paymentStatus` suman `OrderFulfillmentStatus? fulfillmentStatus, bool? invoiced`. Regla en el repositorio (documentada en la interfaz): `status is null` filtra `Status != Archived` (spec §5, también en la exportación, que es «lo que estoy viendo»); `invoiced` filtra por `InvoicedAt != null`.
  - `ExportStatusLabels.For(OrderStatus)`: `Open` → `"Abierto"`, `Archived` → `"Archivado"`, `Cancelled` → `"Anulado"`. Nuevo `For(OrderFulfillmentStatus)`: `NotPrepared` → `"Sin preparar"`, `Prepared` → `"Preparado"`. `OrdersExportProcessor` no pinta estado (la hoja del ERP no cambia de formato, D-R8).

- [ ] **Step 1: Pruebas que fallan**

```csharp
// OrderListApiTests
[Fact] public async Task TheDefaultListHidesArchivedOrders()            // 2 pedidos, archivar 1 → total 1; ?status=Archived → el archivado
[Fact] public async Task FilteringByFulfillmentAndInvoiced()            // ?fulfillmentStatus=Prepared y ?invoiced=true devuelven sólo los suyos
[Fact] public async Task AnInvoicedArchivedOrderStillReadsAsInvoiced()  // ?status=Archived&invoiced=true → 1 fila, invoiced true (anti-meta §2)
[Fact] public async Task AnUnknownFulfillmentFilterIs422()              // ?fulfillmentStatus=Shipped → 422 "order.order.fulfillment_status_invalid"
[Fact] public async Task TheListRowCarriesFulfillmentAndInvoiced()      // fulfillmentStatus "NotPrepared", invoiced false
// OrderContractApiTests
[Fact] public async Task TheDetailCarriesTheNewAxesAndTheBalance()
// tras mark-paid y prepare: fulfillmentStatus "Prepared", paidMarkedBy, preparedAt no nulos, balance == neto − comprobantes; sin "approvedAt"
// OrderExportApiTests
[Fact] public async Task TheExportHonoursTheNewFiltersAndHidesArchived()
// ExportOrdersHandlerTests
[Fact] public void AJobSerializedBeforeTheNewFiltersStillReads()        // JSON sin fulfillmentStatus/invoiced deserializa a OrdersExportFilters con null
// ExportStatusLabelsTests: recorre OrderFulfillmentStatus; "Abierto", "Archivado", "Sin preparar"
```

- [ ] **Step 2: RED** — unitarias (`ListOrdersHandlerTests|ExportOrdersHandlerTests|ExportStatusLabelsTests|OrderResponseComposerTests`) y de integración (`OrderListApiTests|OrderContractApiTests|OrderExportApiTests`).
- [ ] **Step 3: Implementa según Interfaces** (incluye `StubOrderListRepository` con las firmas nuevas).
- [ ] **Step 4: GREEN** — mismos comandos.
- [ ] **Step 5: Commit** — `feat(orders): list, detail and export by fulfillment and invoiced`

---

### Task 7: Shopify — anulación que gana y guarda del pago sincronizado

**Files:**
- Modify: `…Application/IExternalOrderWriter.cs` (`ExternalOrderService.CancelAsync`, `SyncPaymentStatusAsync` doc), `OrderChangeSummary.cs`; `src/Bootstrapper/ShopifyOrderWriter.cs` (comentarios de `SyncPaymentStatusAsync`), `src/Modules/Shopify/Modules.Shopify.Application/ShopifyLastErrorCodes.cs`, `ShopifyPorts.cs:390`, `ShopifyOrderImporter.cs:209`, `ShopifyDtos.cs:73`, `Modules.Shopify.Domain/ShopifyOrderMirror.cs:199` (sólo comentarios: `order.order.not_editable`)
- Test: `tests/…/IntegrationTests/ExternalOrderServiceTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Consumes: `Order.CancelFromPlatform`, `IsPrepared`, guarda `IsEditable` de `SyncExternalPaymentStatus`.
- Produces:
  - `ExternalOrderService.CancelAsync` llama `order.CancelFromPlatform(by, reason, now)` (no `Cancel`). El historial usa `OrderChangeSummary.CancelledWhilePrepared(string reason)` = `$"Anulado con el pedido ya preparado: {reason}. Hay un paquete despachado que recuperar."` cuando `order.IsPrepared`; si no, el `Cancelled(reason)` de hoy. `PublishOrderCancelled` igual que hoy.
  - `ShopifyOrderWriter.CancelAsync` no cambia: `already_invoiced` → `ShopifyOrderOutcome.AlreadyInvoiced`, `already_cancelled` → `AlreadyCancelled`.
  - `SyncPaymentStatusAsync`: un pedido preparado, facturado, archivado o anulado rechaza con `order.order.not_editable` → `ShopifyOrderOutcome.Ignored("order.order.not_editable")`. **No** llama al archivado automático: con esta guarda nunca sincroniza un pedido preparado (ver el reporte: §3.5 lo nombra como camino, pero §3.2 lo cierra).

- [ ] **Step 1: Pruebas que fallan**

```csharp
// ExternalOrderServiceTests
[Fact] public async Task ShopifyCancelWinsOverAPreparedArchivedOrder()
// pedido Shopify: prepare (dominio + SaveChanges), archive → CancelAsync → Status Cancelled, FulfillmentStatus Prepared;
// historial Cancelled con "Anulado con el pedido ya preparado: …"
[Fact] public async Task ShopifyCancelStillStopsAtTheInvoice()           // facturado → QuotationsDomainException "order.order.already_invoiced"
[Fact] public async Task ShopifyPaymentSyncIsRefusedOncePrepared()       // "order.order.not_editable"
// ShopifyAdaptersTests
[Fact] public async Task APreparedOrderPaymentSyncIsIgnoredWithItsCode() // Ignored con Code "order.order.not_editable"
```

- [ ] **Step 2: RED** — `ExternalOrderServiceTests` (integración Quotations) y `ShopifyAdaptersTests` (`tests/Bootstrapper/Bootstrapper.UnitTests`).
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN.**
- [ ] **Step 5: Commit** — `feat(orders): let shopify cancellations override prepared and archived orders`

---

### Task 8: Lectores fuera del agregado — Buyers, Geography, Shopify, Reporting

**Files:**
- Modify (Buyers/Store): `…Quotations.Application/IBuyerOrderGateway.cs` (`BuyerOrderSummary`, `BuyerOrderDetail`, `ToSummary`, `ToDetail`), `src/Modules/Buyers/Modules.Buyers.Application/BuyerOrders.cs` (`BuyerOrderSummaryDto`, `BuyerOrderDetailDto`), `src/Bootstrapper/BuyerOrdersAdapter.cs`
- Modify (Geography): `src/Modules/Geography/Modules.Geography.Application/ICityCorrectionInsights.cs`, `CityImpact.cs`; `src/Bootstrapper/CityCorrectionInsights.cs`
- Modify (Shopify): `…Quotations.Application/IExternalOrderWriter.cs` (`ExternalShippingState`), `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs:534`, `ShopifyUnresolvedCityBackfill.cs:86-89`, `src/Bootstrapper/ShopifyCityCorrectionWriter.cs:90-95`
- Modify (Reporting): `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs` (`OrderPaymentStatusFilter`), `src/Bootstrapper/OrdersReportSource.cs` (`MapPaymentStatus`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/BuyerOrderGatewayTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrdersAdapterTests.cs`, `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerOrdersHandlersTests.cs`, `tests/Modules/Geography/Modules.Geography.UnitTests/CorrectImpactCalculatorTests.cs`, `ResolveImpactCalculatorTests.cs`, `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/CityCorrectionInsightsTests.cs`, `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyUnresolvedCityBackfillTests.cs`, `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportFilterParserTests.cs`

**Interfaces:**
- Consumes: `IsEditable`, `IsPrepared`, `IsInvoiced`, `FulfillmentStatus` (Tarea 1).
- Produces:
  - Buyers (sin capa de compatibilidad, §9): `BuyerOrderSummary`, `BuyerOrderDetail`, `BuyerOrderSummaryDto`, `BuyerOrderDetailDto` suman `string FulfillmentStatus, bool Invoiced` justo después de `PaymentStatus`. `Status` viaja `Open|Archived|Cancelled`.
  - Geography: `OrderInsight(..., string Status, bool HasShippingParty, bool IsStorePickup, bool IsEditable)`; `CorrectionInsight(..., string? OrderStatus, bool StillAsWritten, bool? OrderEditable)` (null para un cliente). `CityCorrectionInsights` los lee con `(o.status = 'Open' AND o.fulfillment_status = 'NotPrepared' AND o.invoiced_at IS NULL) AS "IsEditable"` (y `"OrderEditable"`, nulo sin pedido). `ResolveImpactCalculator` y `CorrectImpactCalculator` usan el booleano en vez de `PendingOrderStatus` (se borra la constante). El nombre de los grupos `OrdersNotPending` (contrato con el frontend) no cambia.
  - Shopify: `ExternalShippingState(OrderStatus Status, bool IsStorePickup, ExternalShippingParty? Shipping, string? Notes, bool IsEditable)`; `ShopifyOrderCityState(string Status, bool IsStorePickup, bool HasShippingParty, bool NotesHaveRawAddress, bool IsEditable)`; el patrón del backfill pasa de `Status: "Pending"` a `IsEditable: true`.
  - Reporting: `OrderPaymentStatusFilter` suma `PendingVerification` al final y `MapPaymentStatus` su rama; el `<summary>` dice «los cuatro valores». `is_void` sigue siendo `status = 'Cancelled'`; nada más del SQL lee `Pending`/`Approved`/`Invoiced` (verificado).
  - Pipeline: los eventos `quotations.order.*` no llevan el estado (spec §9): no se tocan.

- [ ] **Step 1: Pruebas que fallan**

```csharp
// BuyerOrderGatewayTests
[Fact] public void TheBuyerSeesTheFourAxes()                       // pedido preparado → Status "Open", FulfillmentStatus "Prepared", Invoiced false, CanCancel false
// ResolveImpactCalculatorTests / CorrectImpactCalculatorTests   (Review Focus 5)
[Fact] public void AnOpenButPreparedOrderIsNotPending()            // OrderInsight IsEditable=false con Status "Open" → cae en notPending
[Fact] public void AnOpenUnpreparedOrderIsFixable()                // IsEditable=true → toFix
// CityCorrectionInsightsTests (integración Shopify)
[Fact] public async Task TheInsightsReadEditabilityFromTheThreeAxes()  // preparar un pedido real → IsEditable false
// ShopifyUnresolvedCityBackfillTests
[Fact] public async Task ABackfillSkipsAPreparedOrder()            // ShopifyOrderCityState IsEditable=false → Skipped
// ReportFilterParserTests
[Fact] public void PendingVerificationIsAValidPaymentFilter()
```

Actualiza los literales `"Pending"`/`"Approved"` de órdenes en `CorrectImpactCalculatorTests`, `ResolveImpactCalculatorTests`, `ShopifyCityCorrectionProcessorTests`, `CityOperatorImpactFlowTests`, `CitySynonymEndToEndTests` a `"Open"` con el booleano que corresponda (los `"Pending"` de trabajos y ciudades no resueltas **no** son de pedidos: no se tocan).

- [ ] **Step 2: RED** — un proyecto a la vez: `Modules.Quotations.UnitTests`, `Bootstrapper.UnitTests`, `Modules.Buyers.UnitTests`, `Modules.Geography.UnitTests`, `Modules.Shopify.UnitTests`, `Modules.Reporting.UnitTests`, y `Modules.Shopify.IntegrationTests` filtrado a `CityCorrectionInsightsTests`.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN** — mismos comandos, más `Modules.Geography.IntegrationTests` filtrado a `CityOperatorApiTests` y `Modules.Shopify.IntegrationTests` filtrado a `CityOperatorImpactFlowTests|CitySynonymEndToEndTests`.
- [ ] **Step 5: Commit** — `feat(orders): expose the new order axes to buyers, geography, shopify and reporting`

---

### Task 9: Barrido de pruebas de integración, documentación y suite final

**Files (barrido, CLAUDE.md «Endurecer una regla de dominio rompe las pruebas…»):**

Llaman a `/approve` → `mark-paid` (con `quotations.order.approve` en los permisos del cliente):
- `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/BilledCustomerFieldsApiTests.cs` (3), `OrderApiTests.cs` (12, incluida la aserción de auditoría `quotation.order.approved` de la línea 1247 → `quotation.order.paid_marked`), `OrderContractApiTests.cs` (2 de pedido; la de `…/sale/approve` de la línea 239 no es de pedidos), `OrderEditsApiTests.cs` (4), `OrderListApiTests.cs` (1), `OrderPaymentProofPublicationApiTests.cs` (1)
- `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs:199`
- `tests/Modules/Store/Modules.Store.IntegrationTests/PublicBuyerOrdersApiTests.cs:149` (`ApproveAsync` → `MarkPaidAsync`)

Mandan `paymentStatus` en el cuerpo o en records tipados (`ConvertQuotationToOrderRequest`, `CreateOrderRequest`, `AddOrderPaymentProofsRequest`): el compilador marca los tipados; los anónimos/JSON se buscan por `paymentStatus` y se ajustan las aserciones al pago calculado:
- Quotations integración: `BilledCustomerFieldsApiTests`, `DeletedVariantLineStoreTests`, `GlobalScaleDiscountApiTests`, `IncompleteCustomerQuotationApiTests`, `ManualOrderApiTests`, `OrderApiTests` (incluidos los `InlineData` de las líneas 36-39), `OrderContractApiTests`, `OrderEditsApiTests`, `OrderExportApiTests`, `OrderItemEditApiTests`, `OrderListApiTests`, `OrderPaymentProofPublicationApiTests` (helper de la línea 1271), `OrderSendApiTests`, `OrderShippingApiTests`, `PaymentProofReferenceProbeTests`, `ProductDeletionCleanupTests`, `QuotationAdvisorNameApiTests`, `QuotationEditsApiTests`, `QuotationExpirationApiTests`, `QuotationLifecycleEventsApiTests`, `QuotationPartiesApiTests`, `QuotationPreviewStatementCountApiTests`, `QuotationPriceRoundingApiTests`, `QuotationProductSnapshotApiTests`, `QuotationShippingApiTests`, `QuotationUserReferenceProbeTests`, `QuotationVariantLinesApiTests`, `RetailQuotationApiTests`, `StoreOrderGatewayTests`, `TenantModulesQuotationsApiTests`
- Reporting: `ReportingApiHarness.ConvertToOrderAsync` (línea 695) pierde `paymentStatus`; cuando el que llama pedía `FullPaymentReceived`, después de convertir hace `POST …/mark-paid` (el cliente del harness necesita `quotations.order.approve`). Revisar `OrdersReportApiTests`, `OrdersReportPosApiTests`, `OrdersReportSummaryApiTests` y las unitarias `OrdersReportHandlerTests`, `OrdersReportSummaryHandlerTests` (sólo si construyen pedidos con el parámetro).
- Store: `PublicBuyerOrdersApiTests` (`AddProofKeepingPaymentPendingAsync`, línea 131: sin `paymentStatus`; un comprobante de 5.000 deja `PartialPaymentReceived`; las aserciones de las líneas 265, 292, 481, 502 se ajustan; el pedido sigue sin poder anularse por tener comprobante).
- Shopify: `ShopifyImportFlowTests` (sólo si arma un pedido interno con el parámetro).

Usan `OrderStatus.Pending/Approved/Invoiced`, `.Approve(` o `approved_by` sobre el esquema actual:
- `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:268, 831-874` (`order.Approve` → `MarkPaid`; la retención por `approved_by` pasa a `paid_marked_by`)
- `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExternalOrderServiceTests.cs`, `OrderExportApiTests.cs`
- `INSERT INTO quotations.orders` a mano sobre el esquema actual: `OrderApiTests.cs:594-606` (`InsertLegacyOrderAsync`: `'Pending'` → `'Open'`) y `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerMergeAcrossModulesApiTests.cs:119-122` (`'Pending'` → `'Open'`). `OrdersExportLayoutPersistenceTests.cs` no inserta pedidos (su `INSERT` es de `orders_export_layouts`): no se toca.

Códigos retirados en aserciones (`order.order.not_pending` → `order.order.not_editable`; `order.order.not_approved` → `order.order.not_paid` o la regla nueva): `OrderApiTests`, `OrderEditsApiTests`, `OrderPaymentProofPublicationApiTests`, `RetailQuotationApiTests`; en Shopify (`ShopifyInboxApiTests`, `ShopifyImportResultTests`, `ShopifyLastErrorCodesTests`, `ShopifyOrderChangesTests`, `ShopifyOrderMirrorTests`) el texto sólo es un ejemplo de código y puede quedar; cámbialo sólo si la prueba espera un rechazo real de Quotations.

**Docs (en la rama, sin `docs/estado.md`):**
- `README.md`: tabla de rutas de la línea 1310 (sin `approve`; con `mark-paid`, `unmark-paid`, `prepare`, `unprepare`, `archive`, `unarchive` y `/orders-settings`), § Pedidos (línea 1684: «Todos nacen `Open`»; pago calculado; tres ajustes), redondeo (líneas 824-828: editable = abierto, no preparado, no facturado), envío por WhatsApp (1745), Shopify (4306, 4328), tienda (1154, 1181: regla nueva de `CanBeCancelledByBuyer`), ciudades (641, 650, 675: «editable» en vez de `Pending`; el motivo persistido sigue siendo `order_not_pending`).
- `CLAUDE.md`, gotcha «La anulación del comprador es más estricta…»: `Status == Open && PaymentStatus == PaymentPending && PaymentProofs.Count == 0 && !IsPrepared && !IsInvoiced`.

- [ ] **Step 1: Compila la solución** — `dotnet build --no-restore`; arregla cada error de las listas de arriba.
- [ ] **Step 2: Corre por proyecto, uno a la vez**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReport" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicBuyerOrdersApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~OrphanUserCleanupTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests --filter "FullyQualifiedName~CustomerMergeAcrossModulesApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/ArchitectureTests -- xUnit.MaxParallelThreads=4
```
Esperado: todo verde. La suite de integración de Quotations corre completa **una vez** aquí; pega el resumen literal (`Passed!`/`Failed!` con conteos).

- [ ] **Step 3: Formato** — `dotnet format --verify-no-changes --include <archivos tocados>` (el baseline del repo tiene ruido de CRLF: mide sólo lo tuyo).
- [ ] **Step 4: Commit** — `test(orders): sweep integration tests for the new order axes` y, aparte, `docs(orders): document order status axes and settings`.

---

## Self-review (hecho)

- **Cobertura del spec:** §3.1 → T1; §3.2 → T1 (marcar/deshacer), T2 (cálculo, recálculo, saldo, sync), T5 (permisos, 403, validaciones), T7 (Shopify); §3.3 → T1/T5; §3.4 → T1/T5; §3.5 → T1/T5 (automático en marcar pagado y preparar); §3.6 → T1; §3.7 → T1/T8; §4 → T1/T2/T5/T7; §5 → T6; §6 → T3/T4/T5; §7 → T1 (retiro de `/approve`)/T5; §8 → T3; §9 → T3 (sonda, seeder), T7, T8, T9.
- **Tipos:** `OrderSettings` (T3) es el que leen T4 y T5; `OrderPaymentStatusChange` (T2) lo consumen los handlers de T2; `IsEditable` (T1) lo usan T2, T7 y T8; las firmas del repositorio (T6) incluyen el doble `StubOrderListRepository`.
- **Decisiones del implementador fijadas una vez:** rutas `mark-paid`/`unmark-paid`/`prepare`/`unprepare`/`archive`/`unarchive` y `/orders-settings`; códigos de `OrderStateErrors` y `not_editable`; columnas y nombres de los ajustes; `/approve` retirado; `approved_*` eliminadas tras copiarse; `OnOrderApproval` sin renombrar; etiquetas de `ExportStatusLabels`; el archivado por defecto también se oculta en la exportación; `paymentStatus` fuera de alta y conversión (T2).
