# Inventario básico — plan de implementación

> **Para agentes:** se ejecuta por **bloques** (regla del owner en `../CLAUDE.md`, «Presupuesto de pruebas»): un implementador Sonnet
> por bloque, una revisión por bloque y la revisión fuerte de toda la rama al final. Cada tarea conserva su commit. Pasos con casillas
> (`- [ ]`).

**Meta:** que una orden de compra recibida sume, un pedido reserve y descuente, y una venta POS descuente, sobre el ledger del WMS.

**Arquitectura:** Purchases, Quotations y POS publican eventos con líneas al outbox de plataforma en su propia transacción; un consumidor
nuevo de Wms (`wms.inventory-events`) los aplica con una máquina de estados por documento, reservas de demanda por bodega y asientos al
ledger en la bodega principal.

**Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers, FluentValidation.

**Spec:** `docs/superpowers/specs/2026-11-27-inventario-basico-design.md` (aprobada por el owner el 2026-11-27, commit `020520e47`). El
plan cita sus secciones como §n; el implementador lee las dos.

## Restricciones globales

- Rama `feature/basic-inventory`, worktree `../qep-backend-worktrees/inventario-basico`. Nunca `main`; el merge va a `develop` con `--no-ff`.
- TDD: RED antes que GREEN, con la salida literal de los dos en el reporte. Si el RED es de compilación, la evidencia es el build y no se
  levantan contenedores.
- Integración mientras se itera: `--filter "FullyQualifiedName~<NombreDePrueba>"`. La clase completa, una vez al final del bloque. Nunca
  dos corridas de integración a la vez en la máquina: antes de una larga, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si
  hay una ajena, se espera, no se detiene. Los subagentes corren las pruebas en primer plano.
- Payloads en camelCase con records posicionales (como `QuotationLifecycleEventPublisher`). Campos de eventos existentes: sólo se agregan
  al final, nunca se renombran ni quitan.
- Ningún status HTTP a mano en un handler; códigos nuevos en `WmsErrorCodes` y su mapeo en `src/Api/ApiExceptionHandler.cs` si no cae solo.
- Traducir errores de base en Infrastructure, por nombre de índice.
- Textos que ve una persona, en español colombiano tuteando.
- Commits conventional, en inglés, terminados en:
  ```
  Session: WMS T3 recepción contra orden de compra
  Co-Authored-By: Claude <modelo que escribió el cambio> <noreply@anthropic.com>
  ```
  (el título de sesión se relee justo antes de cada commit, regla del `CLAUDE.md` del repo).
- Gate antes del merge: `.\scripts\test-feature.ps1` (en PS 5.1, antes
  `$env:GIT_CONFIG_COUNT=1; $env:GIT_CONFIG_KEY_0="core.safecrlf"; $env:GIT_CONFIG_VALUE_0="false"`), leyendo
  `TestResults/feature/report.md`. Los avisos «Requiere autorización del owner» se le muestran al owner: el contrato HTTP de §5.3 ya está
  aprobado; cualquier otro, no.

## Foco de revisión

1. **Secuencias fuera de orden de un mismo pedido** (reintento que vence el lease a mitad de `prepared` → `edited`): el estado final debe
   ser el mismo que en orden. Prueba en Tarea 10 (`ALaterMessageOfTheSameDocumentWaitsForTheOlderOne`).
2. **Pedido de Shopify editado y anulado desde la plataforma ya preparado:** nada vuelve al stock y la reserva abierta, si la hay, se
   libera. Prueba en Tarea 11 (`APlatformCancelOfAPreparedOrderReturnsNothing`).
3. **Venta de una variante que nunca se recibió:** el saldo queda negativo, la venta no se frena y un `Move` posterior desde ahí falla
   con `wms.stock.insufficient`. Prueba en Tarea 6 y Tarea 11.
4. **Tenant que apaga el módulo con pedidos reservados:** al prenderlo, el `prepared` que llegó mientras estaba apagado se aplicó. Prueba
   en Tarea 11 (`EventsOfADocumentWithStateApplyWithTheModuleOff`).
5. **Dos eventos del primer uso a la vez** (dos ventas POS simultáneas de un tenant sin principal): una sola bodega principal. Prueba en
   Tarea 11 (`TwoConcurrentFirstEventsProvisionOnePrincipal`).

---

## Bloque A — Eventos de los módulos que venden y compran

### Tarea 1: Purchases publica `purchases.order.received.v1`

**Archivos:**
- Crear: `src/Modules/Purchases/Modules.Purchases.Application/IPurchaseOrderEventPublisher.cs`
- Crear: `src/Modules/Purchases/Modules.Purchases.Infrastructure/Persistence/PurchaseOrderEventPublisher.cs`
- Crear: `src/Modules/Purchases/Modules.Purchases.Infrastructure/Persistence/PurchasesOutboxMessage.cs` (entidad que escribe; la de hoy,
  `PurchasesOutboxRecord`, es proyección de sólo lectura del consumidor mayorista y no se toca)
- Modificar: `PurchasesDbContext.cs` (mapear la entidad nueva a `platform.outbox_messages`, `ExcludeFromMigrations`, mismas columnas que
  `PosOutboxMessage`), `TransitionPurchaseOrder.cs` (publicar al recibir), registro DI de Purchases
- Pruebas: `tests/Modules/Purchases/Modules.Purchases.UnitTests/PurchaseOrderReceivedEventTests.cs`,
  `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/PurchaseOrderReceivedOutboxTests.cs`

**Interfaces — produce:**
- `void IPurchaseOrderEventPublisher.PublishReceived(PurchaseOrder order, string origin, DateTimeOffset occurredAt)`
- `PurchaseOrderEventOrigins.Manual = "Manual"`, `.WmsReceipt = "WmsReceipt"` (Application)
- Payload: `{ tenantId, purchaseOrderId, version, origin, occurredAt, lines: [{ lineId, variantId (null ok), quantity }] }`, con
  `quantity = ConfirmedQuantity` y sin líneas de cantidad confirmada 0. `CorrelationId` = id de la orden.

- [ ] **Paso 1: pruebas que fallan.** Unitarias con un publicador grabador: `ReceivingPublishesTheConfirmedLinesWithManualOrigin`
  (líneas = confirmadas, `origin == "Manual"`, `version == order.Version` después de `Receive`), `ARejectedLineDoesNotTravel`,
  `ALinkedLineWithoutVariantTravelsWithNullVariant`, `CancellingPublishesNothing`. Integración:
  `ReceiveWritesTheEventInTheSameTransaction` (fila en `platform.outbox_messages` con `event_name = 'purchases.order.received.v1'` y
  payload que deserializa a los campos de arriba).
- [ ] **Paso 2:** build → RED de compilación (evidencia: salida del build).
- [ ] **Paso 3:** implementar publicador, entidad y llamada en `TransitionPurchaseOrder` sólo para la transición a `Received`.
- [ ] **Paso 4:** unitarias en verde; integración filtrada por nombre en verde.
- [ ] **Paso 5:** commit `feat(purchases): publish purchases.order.received.v1 with confirmed lines`.

### Tarea 2: POS publica `pos.sale.created.v1` y `pos.sale.voided.v1`

**Archivos:**
- Crear: `src/Modules/Pos/Modules.Pos.Application/IPosSaleEventPublisher.cs`,
  `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosSaleEventPublisher.cs`
- Modificar: `CreatePosSale.cs`, `VoidPosSale.cs`, DI de Pos
- Pruebas: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleEventsTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleOutboxTests.cs`

**Interfaces — produce:**
- `void PublishCreated(PosSale sale, DateTimeOffset occurredAt)` → `{ tenantId, saleId, version: 1, occurredAt, lines: [{ lineId, variantId, quantity }] }`
- `void PublishVoided(PosSale sale, DateTimeOffset occurredAt)` → `{ tenantId, saleId, version: 2, occurredAt }`
- `CorrelationId` = id de la venta.

- [ ] **Paso 1:** pruebas `CreatingASalePublishesItsLinesWithVersionOne`, `VoidingPublishesVersionTwo`,
  `AVoidThatFailsPublishesNothing` (venta ya anulada → `pos.sale.already_voided` y ningún evento), e integración
  `CreateAndVoidWriteTheirEventsInTheSameTransaction`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar; publicar después de la mutación, antes del `SaveChanges` del caso de uso.
- [ ] **Paso 4:** verde (unitarias + integración filtrada).
- [ ] **Paso 5:** commit `feat(pos): publish sale created and voided events with lines`.

### Tarea 3: Quotations amplía `created`, `cancelled` y `wholesale-approved`

**Archivos:**
- Modificar: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs`,
  `IQuotationLifecycleEventPublisher.cs` (firma de `PublishOrderCancelled` gana `bool fromPlatform`), los cuatro llamadores de cancelar
  (`CancelOrder`, `IBuyerOrderGateway`, `IWholesaleOrderGateway`, `IExternalOrderWriter` → `CancelFromPlatform` pasa `true`)
- Pruebas: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderEventContractTests.cs`

**Interfaces — produce (campos nuevos, al final):**
- `OrderLinePayload(Guid lineId, Guid variantId, decimal quantity, string productKind)` — `productKind` = `OrderItem.Kind.ToString()`.
- `created`: `+ int version, bool awaitingWholesaleApproval, IReadOnlyList<OrderLinePayload> lines` (`source` ya viaja).
- `cancelled`: `+ long version, bool fromPlatform`.
- `wholesale-approved`: `+ long version, DateTimeOffset occurredAt`; cada línea `+ Guid lineId, string productKind`.

- [ ] **Paso 1:** pruebas de contrato que serializan cada payload y verifican **los campos viejos con su nombre exacto** (los que hoy leen
  Pipeline `QuotationEventPayloads`, Shopify `QuotationOrderEventPayloads` y Purchases `WholesaleOrderEventPayloads`) y los nuevos:
  `CreatedKeepsItsFieldsAndAddsVersionAndLines`, `CancelledAddsVersionAndFromPlatform`, `PlatformCancelSetsFromPlatform`,
  `WholesaleApprovedAddsVersionOccurredAtAndLineIds`, `AServiceLineTravelsWithServiceKind`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar. La versión se lee de `order.Version` en el momento de publicar, que ya va después de la mutación.
- [ ] **Paso 4:** verde; además, las unitarias existentes de Pipeline, Shopify y Purchases que deserializan estos eventos siguen en verde
  (`dotnet test` de sus `UnitTests`).
- [ ] **Paso 5:** commit `feat(quotations): add version and lines to order created, cancelled and wholesale-approved`.

### Tarea 4: Quotations publica `edited`, `prepared`, `unprepared`, `archived`, `unarchived` en todos los caminos

**Archivos:**
- Modificar: `QuotationLifecycleEventPublisher.cs` + interfaz (cinco métodos nuevos), y los sitios de §5.1: `SaveOrderEdits.cs`,
  `AddOrderItems.cs`, `IExternalOrderWriter.cs` (`ApplyExternalEdit`, `CorrectByMemberAsync` si cambia líneas, `SyncPaymentStatusAsync`
  cuando `SyncExternalPaymentStatus` devuelve `true`), `IWholesaleOrderGateway.cs` y `IBuyerOrderGateway.cs` donde cambien líneas,
  `PrepareOrder.cs`, `UndoOrderPreparation.cs`, `ArchiveOrder.cs`, `UnarchiveOrder.cs`, `OrderAutoArchive.cs`, `MarkOrderPaid.cs`
- Pruebas: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderStockEventsTests.cs`,
  `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderStockEventsOutboxTests.cs`

**Interfaces — produce:**
- `PublishOrderEdited(Order, DateTimeOffset)` → `{ tenantId, orderId, version, occurredAt, lines }`
- `PublishOrderPrepared(Order, string origin, DateTimeOffset)` → `+ origin` (`OrderEventOrigins.Manual` | `.WmsPacking`), `lines`
- `PublishOrderUnprepared(Order, DateTimeOffset)` → `{ tenantId, orderId, version, occurredAt }`
- `PublishOrderArchived(Order, DateTimeOffset)` → `+ bool isPrepared, lines`
- `PublishOrderUnarchived(Order, DateTimeOffset)` → `+ bool isPrepared`
- Nombres: `quotations.order.edited.v1`, `.prepared.v1`, `.unprepared.v1`, `.archived.v1`, `.unarchived.v1`.

- [ ] **Paso 1:** unitarias con el publicador grabador, una por camino: `SavingEditsPublishesEdited`, `AddingItemsPublishesEdited`,
  `AnExternalEditPublishesEdited`, `AnExternalEditThatChangesNothingPublishesNothing`, `PreparingPublishesPreparedWithManualOrigin`,
  `PreparingAPaidOrderPublishesPreparedThenArchivedWithIncreasingVersions` (el auto-archivo: dos eventos, `version` del segundo mayor que
  la del primero), `UndoingPreparationPublishesUnprepared`, `ArchivingPublishesIsPrepared`, `UnarchivingCarriesTheSameIsPrepared`,
  `MarkingPaidThatAutoArchivesPublishesArchived`, `APaymentSyncThatArchivesPublishesArchived`. Integración:
  `PrepareWritesItsEventInTheSameTransaction`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar. Cada publicación va **justo después de su mutación** (no al final del caso de uso), para que dos eventos
  del mismo caso de uso lleven versiones distintas.
- [ ] **Paso 4:** verde (unitarias + integración filtrada). Al cerrar el bloque, las clases de integración tocadas de Quotations, Pos y
  Purchases corren una vez cada una, en fila.
- [ ] **Paso 5:** commit `feat(quotations): publish order stock events from every path`.

**Revisión del bloque A** (un subagente de solo lectura): caminos de §5.1 completos, campos viejos intactos, versión por mutación.

---

## Bloque B — Modelo del WMS

### Tarea 5: Migración y dominio (principal, ubicación general, reservas de demanda, estado por documento, inbox, catálogo)

**Archivos:**
- Modificar: `Modules.Wms.Domain/Warehousing/Warehouse.cs` (`IsPrincipal`, `GeneralLocationId`, `MarkPrincipal()`, `ClearPrincipal()`,
  `EnsureGeneralLocation(DateTimeOffset now) : Location`, `Update` rechaza desactivar la principal), `WmsErrorCodes.cs`
  (`WarehousePrincipalCannotBeDeactivated = "wms.warehouse.principal_cannot_be_deactivated"`,
  `MovementReverseFromDocument = "wms.movement.reverse_from_document"`), `Modules.Wms.Domain/Inventory/Reservation.cs`
  (`LocationId` → `Guid?`; factoría `OpenDemand(...)`, `Close(...)`, `Consume(...)`), `VariantLogisticsProfile.CreateDefault` (gana
  `int baseUomPrecision`), `WmsPorts.cs` (`WmsVariantSnapshot` + `ProductKind Kind`, `SalesUnit SalesUnit`), `src/Bootstrapper/WmsCatalogReader.cs`,
  `VariantProfileRepository.GetOrCreateDefaultAsync` (precisión 0 para `Unit`, 2 para `Kilogram|Meter|SquareMeter|Liter`, 0 el resto)
- Crear: `Modules.Wms.Domain/Inventory/DocumentStockState.cs`, `DocumentStockStatus.cs`, `InventoryDocumentTypes.cs`
  (`PurchaseOrder`, `Order`, `PosSale`), `Modules.Wms.Infrastructure/Persistence/WmsInbox.cs`, migración `AddBasicInventory`
- Pruebas: `tests/Modules/Wms/Modules.Wms.UnitTests/WarehouseTests.cs` (casos nuevos), `.../Inventory/DemandReservationTests.cs`,
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/BasicInventoryMigrationTests.cs`

**Interfaces — produce:**
- `DocumentStockStatus { None, Reserved, TakenOutByPrepare, TakenOutByArchive, Released, Parked, HandedToOutbound, Received, ReceivedByWms, Shipped, Voided }`
- `DocumentStockState`: `TenantId, DocumentType, DocumentId, LastVersion, Status, WarehouseId?, LocationId?, PostingBatchId?, PostingOccurredAt?, SkippedLineIds (jsonb)`;
  clave (`tenant_id`, `document_type`, `document_id`), tabla `wms.document_stock_states`.
- `Reservation.OpenDemand(Guid tenantId, Guid ownerId, Guid warehouseId, Guid variantId, decimal quantity, string demandType, Guid demandId, Guid demandLineId, DateTimeOffset now)`
- Índices: `IX_warehouses_tenant_principal` (único parcial `WHERE is_principal`), `IX_reservations_open_demand` (único parcial
  `(tenant_id, demand_type, demand_id, demand_line_id) WHERE status = 'Open' AND location_id IS NULL`).
- `wms.inbox_messages (consumer, message_id, claimed_until, processed_at, attempts)`, PK (`consumer`, `message_id`), copia de Purchases.

- [ ] **Paso 1:** unitarias `MarkingPrincipalSetsTheFlag`, `DeactivatingThePrincipalIsRejected` (código exacto arriba),
  `EnsureGeneralLocationCreatesARootStorageBinThatAllowsPicking`, `EnsureGeneralLocationTakesTheNextFreeCode` (con `GENERAL` ocupado →
  `GENERAL-1`), `EnsureGeneralLocationIsIdempotent`, `ADemandReservationHasNoLocation`, `DefaultProfilePrecisionFollowsTheSalesUnit`
  (`Unit` → 0, `Kilogram` → 2). Integración: `TheMigrationAllowsOnePrincipalPerTenant` (segunda marca → violación de
  `IX_warehouses_tenant_principal`), `TwoOpenDemandReservationsForTheSameLineAreRejected`,
  `TwoAllocationsForTheSameLineInDifferentLocationsAreAllowed`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar y generar la migración con `dotnet ef migrations add AddBasicInventory` del proyecto de Infrastructure de Wms
  (antes, verificar que no corra `Api.exe`).
- [ ] **Paso 4:** verde (unitarias + integración filtrada).
- [ ] **Paso 5:** commit `feat(wms): add principal warehouse, demand reservations and document stock state`.

### Tarea 6: Débito por venta y reversa manual bloqueada

**Archivos:**
- Modificar: `Modules.Wms.Infrastructure/Persistence/InventoryLedger.cs` (`ApplyDeltaAsync` elige `SaleDebitAsync` cuando la entrada es
  `MovementReason.Ship` con documento `Order` o `PosSale` y la ubicación no es virtual), `Modules.Wms.Application/Inventory/ReverseMovement.cs`
- Pruebas: `tests/Modules/Wms/Modules.Wms.IntegrationTests/SaleDebitTests.cs`, `ReverseMovementApiTests.cs` (caso nuevo)

**Interfaces — produce:** ninguna pública; la regla vive en el ledger.

- [ ] **Paso 1:** integración `ASaleCanLeaveThePhysicalBalanceNegative` (saldo 0, venta de 3 → `on_hand = -3`,
  `allows_negative = true`), `AMoveFromANegativeBalanceFails` (`wms.stock.insufficient`), `APutawayFromANegativeBalanceFails`,
  `ASaleCannotGoBelowAllocatedStock` (saldo 5, `reserved` 4 por una asignación sembrada, venta de 2 → `wms.stock.reserved`),
  `AReversalOfASaleRaisesANegativeBalance`, `ReversingAMovementOfAnOrderIsRejected` (403/422 según el mapeo de `WmsDomainException`,
  código `wms.movement.reverse_from_document`).
- [ ] **Paso 2:** RED: las pruebas fallan por la regla (el build pasa); integración filtrada por nombre.
- [ ] **Paso 3:** `SaleDebitAsync`: un `UPDATE … SET on_hand = on_hand + delta, allows_negative = true … WHERE … AND (reserved = 0 OR on_hand + delta >= reserved)`
  y, si no hay fila, el upsert con `allows_negative = true`; si no actualiza por `reserved`, `wms.stock.reserved`. `ReverseMovementHandler`
  rechaza si `movement.DocumentType` está en `InventoryDocumentTypes`.
- [ ] **Paso 4:** verde.
- [ ] **Paso 5:** commit `feat(wms): let sales leave the balance negative without touching allocations`.

### Tarea 7: Elegir la bodega principal (contrato aprobado)

**Archivos:**
- Crear: `Modules.Wms.Application/Warehousing/SetPrincipalWarehouse.cs` (comando, validador, handler)
- Modificar: `WarehouseEndpoints.cs` (`PUT /tenants/{tenantId:guid}/wms/warehouses/{warehouseId:guid}/principal`, cuerpo
  `{ "version": n }`, política de `wms.warehouse.manage`), `WmsDtos.cs` (`WarehouseDto` + `bool IsPrincipal, Guid? GeneralLocationId`),
  `WarehouseRepository.cs` (`FindPrincipalAsync(Guid tenantId, CancellationToken)`)
- Pruebas: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WarehouseApiTests.cs` (casos nuevos)

**Interfaces — produce:** `Task<Warehouse?> IWarehouseRepository.FindPrincipalAsync(Guid tenantId, CancellationToken ct)`.

- [ ] **Paso 1:** `SettingThePrincipalMovesTheFlag` (la anterior queda `isPrincipal = false`, la nueva `true` con `generalLocationId`),
  `SettingThePrincipalWithAStaleVersionIs412`, `SettingThePrincipalOfAnotherTenantIs403`, `SettingThePrincipalWithoutPermissionIs403`,
  `DeactivatingThePrincipalIs422` (código de Tarea 5), `ListingWarehousesShowsIsPrincipal`.
- [ ] **Paso 2:** RED (build: el endpoint no existe).
- [ ] **Paso 3:** handler con `WmsAuthorization.EnsureAsync`, `WmsVersions.Ensure`, quitar la marca de la anterior y `FlushAsync` antes de
  marcar la nueva (índice no diferible), auditoría como los demás handlers de bodega.
- [ ] **Paso 4:** verde.
- [ ] **Paso 5:** commit `feat(wms): choose the principal warehouse`.

### Tarea 8: Lo que se lee: resumen, totales y aviso de cambio

**Archivos:**
- Modificar: `InventoryLedgerSql.cs` (`CreateStockSummaryView`: unión con reservas de demanda abiertas), migración nueva
  `UpdateStockSummaryForDemandReservations`, `StockReadQueries.SearchAsync` (totales suman las reservas de demanda del filtro), `WmsDtos.cs`
  (comentario en el DTO: `pickable` puede superar a `available`, y por qué)
- Crear: `Modules.Wms.Application/Inventory/IDemandReservations.cs` + `Modules.Wms.Infrastructure/Persistence/DemandReservations.cs`
- Pruebas: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockQueryTests.cs` (casos nuevos)

**Interfaces — produce:**
- `IDemandReservations.OpenAsync(DemandLine line, Guid warehouseId, CancellationToken)`, `CloseAllAsync(string demandType, Guid demandId, …)`,
  `ConsumeAllAsync(…) : IReadOnlyList<Reservation>`, `ReopenAsync(IReadOnlyList<Reservation> consumed, …)`; cada una encola el cambio en
  `IBalanceChangeQueue` para (tenant, dueño, bodega, variante).
- `DemandLine(Guid TenantId, Guid OwnerId, Guid VariantId, decimal Quantity, string DemandType, Guid DemandId, Guid DemandLineId)`.

- [ ] **Paso 1:** `ADemandReservationWithoutBalanceAppearsInTheSummary` (`onHand 0`, `reserved 2`, `available -2`),
  `TheListTotalsMatchTheSummary` (mismo filtro → mismos `reserved` y `available`), `RowsByLocationDoNotShowDemandReservations`,
  `OpeningADemandReservationEnqueuesABalanceChange`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar.
- [ ] **Paso 4:** verde; al cerrar el bloque, las clases `StockQueryTests`, `WarehouseApiTests`, `ReverseMovementApiTests`,
  `InventoryLedgerPostingTests` una vez cada una, en fila.
- [ ] **Paso 5:** commit `feat(wms): count demand reservations in the stock summary`.

**Revisión del bloque B:** índices parciales, guardas del débito por venta, contrato del `PUT` igual al aprobado.

---

## Bloque C — Consumidor

### Tarea 9: Máquina por documento (dominio puro)

**Archivos:**
- Crear: `Modules.Wms.Domain/Inventory/DocumentStockMachine.cs`, `InventoryEvent.cs`
- Pruebas: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/DocumentStockMachineTests.cs`

**Interfaces — produce:**
- `InventoryEventKind { OrderCreated, OrderWholesaleApproved, OrderEdited, OrderPrepared, OrderUnprepared, OrderCancelled, OrderArchived, OrderUnarchived, PurchaseReceived, PosCreated, PosVoided }`
- `InventoryEvent(InventoryEventKind Kind, long Version, string? Origin, bool IsPrepared, bool FromPlatform, bool AwaitingWholesaleApproval)`
- `StockDecision DocumentStockMachine.Decide(DocumentStockStatus current, long lastVersion, InventoryEvent evt)` →
  `StockDecision(StockAction Action, DocumentStockStatus Next)` con
  `StockAction { None, Reserve, ReplaceReservation, TakeOut, CompensateAndReserve, Release, Receive, Ship, Compensate }`.
  Versión ≤ `lastVersion` → `(None, current)`.

- [ ] **Paso 1:** una prueba por fila de la tabla de §5.2 y por los casos de compra y POS, con nombres que digan la fila, más:
  `PrepareArchiveUnarchiveReturnsNothing` (hallazgo 1), `UnarchiveAfterArchiveWithoutPrepareCompensatesAndReserves`,
  `WmsPackingOriginIsIgnored`, `WmsReceiptOriginIsIgnored`, `AnOlderVersionDoesNothing`, `ACancelBeforeCreatedLeavesNoneAndBlocksTheLateCreated`,
  `HandedToOutboundOnlyListensToCancel`, `ACreatedAwaitingWholesaleApprovalDoesNothing`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar como `switch` de tuplas (estado, tipo).
- [ ] **Paso 4:** verde.
- [ ] **Paso 5:** commit `feat(wms): add the per-document stock state machine`.

### Tarea 10: Infraestructura del consumidor (orden por documento, marca de arranque, worker)

**Archivos:**
- Crear: `Modules.Wms.Infrastructure/Messaging/ClaimedOutboxConsumer.cs` (copia de la de Purchases, con orden por (`OccurredAt`,
  `version` del payload, `Id`) y exclusión de mensajes con uno más viejo del mismo `CorrelationId` sin procesar),
  `WmsInventoryEventsProcessor.cs`, `WmsInventoryEventsWorker.cs` (escucha `DoorbellQueues.Outbox` con `SafetyNet`), `InventoryEventPayloads.cs`
- Modificar: `WmsDbContext.cs` (inbox; la proyección del outbox gana `CorrelationId`), `WmsInfrastructureExtensions.cs` (registro),
  opción `Wms:InventoryEvents:StartAt` (marca de arranque, `DateTimeOffset`, obligatoria; validada en `WmsWorkerOptionsValidator`),
  `appsettings` de ejemplo, `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (clasificar el worker; y, si el inbox
  cuenta como cola, `QueueDoorbellInterceptor` y `QueueMetricsSource`)
- Pruebas: `tests/Modules/Wms/Modules.Wms.IntegrationTests/InventoryEventsConsumerTests.cs`

**Interfaces — produce:** `Task<bool> WmsInventoryEventsProcessor.ProcessBatchAsync(CancellationToken)`; delega cada mensaje en
`IInventoryEventApplier.ApplyAsync(InventoryMessage message, CancellationToken)` (Tarea 11), con
`InventoryMessage(Guid MessageId, string EventName, string PayloadJson, DateTimeOffset OccurredAt, int Attempts)`.
Consumidor `wms.inventory-events`; eventos: los once de §5.1 (no `wholesale-cancelled`).

- [ ] **Paso 1:** con un aplicador grabador: `ALaterMessageOfTheSameDocumentWaitsForTheOlderOne` (el viejo falla una vez; el nuevo no se
  aplica hasta que el viejo pase), `DifferentDocumentsAdvanceIndependently`, `MessagesBeforeTheStartMarkAreIgnored`,
  `SameOccurredAtIsOrderedByVersion`, `AProcessedMessageIsNotAppliedTwice`, `AMalformedPayloadIsMarkedProcessedAndLogged`,
  `WholesaleCancelledIsNotConsumed`, `AFailingMessagePastTheThresholdParksItsDocumentAndLogsAnError` (el documento queda `Parked`, el
  mensaje sigue sin procesar y otros documentos avanzan). Arquitectura: `MultiReplicaInventoryTests` en verde con el worker clasificado.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar; efecto e inbox en la misma transacción del `WmsDbContext` (`BeginTransactionAsync` de la unidad de trabajo,
  como exige `InventoryLedger.PostAsync`). Curva de reintento de Purchases.
- [ ] **Paso 4:** verde (integración filtrada + `ArchitectureTests`).
- [ ] **Paso 5:** commit `feat(wms): consume inventory events in document order`.

### Tarea 11: Aplicador: provisión, filtro de líneas, reservas y asientos

**Archivos:**
- Crear: `Modules.Wms.Application/Inventory/InventoryEventApplier.cs` (implementa `IInventoryEventApplier`), `PrincipalWarehouseProvisioner.cs`,
  `InventoryLineFilter.cs`
- Modificar: `WmsProvisioner.cs` si hace falta crear el dueño `Self`; `WmsMetrics.cs` (contador de líneas apartadas por motivo)
- Pruebas: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/InventoryLineFilterTests.cs`,
  `tests/Modules/Wms/Modules.Wms.IntegrationTests/BasicInventoryEndToEndTests.cs`

**Interfaces:**
- Consume: `DocumentStockMachine.Decide` (T9), `IDemandReservations` (T8), `IInventoryLedger.PostAsync`, `IWarehouseRepository.FindPrincipalAsync` (T7),
  `IWmsCatalogReader.GetVariantsAsync` con `Kind`/`SalesUnit` (T5), `ITenantModules.FindAsync`.
- Produce: `Task<(Guid WarehouseId, Guid LocationId)> PrincipalWarehouseProvisioner.EnsureAsync(Guid tenantId, CancellationToken)` (toma
  `pg_advisory_xact_lock` por tenant); `InventoryLineFilter.Split(lines, snapshots, profiles) : (IReadOnlyList<Line> Movable, IReadOnlyList<SkippedLine> Skipped)`
  con motivos `NoVariant | UnknownVariant | Service | LotOrSerial | Precision`.

- [ ] **Paso 1:** unitarias del filtro, una por motivo. Integración de punta a punta (publicando al outbox como lo haría cada módulo y
  corriendo el procesador): `AReceivedPurchaseAddsToGeneral`, `AManualOriginReceiptIsSkippedWhenTheOrderHasAWmsReceipt` (sembrando la
  fila `ReceivedByWms`), `ACreatedOrderReservesAndPreparedTakesOut`, `EditingReplacesTheReservation`,
  `UnpreparingCompensatesExactlyThePostedBatchAndReservesAgain`, `ArchivingUnpreparedTakesOutAndUnarchivingReturns`,
  `APlatformCancelOfAPreparedOrderReturnsNothing`, `AConsoleCancelReleases`, `APosSaleTakesOutAndVoidReturns`,
  `ASaleOfANeverReceivedVariantGoesNegative`, `AServiceLineMovesNothing`, `ALotTrackedVariantIsSkippedAndTheRestPosts`,
  `AnUnknownVariantIsSkippedNotRetried`, `ATenantWithoutTheModuleCreatesNothing`, `EventsOfADocumentWithStateApplyWithTheModuleOff`,
  `TwoConcurrentFirstEventsProvisionOnePrincipal`, `TheOldestActiveWarehouseBecomesPrincipal`, `ACountOnGeneralParksTheSaleUntilItCloses`,
  `AReversalAfterAChangeOfPrincipalGoesToTheOriginalWarehouse`.
- [ ] **Paso 2:** RED (build).
- [ ] **Paso 3:** implementar: decidir con la máquina; filtrar antes del ledger; reservar/consumir/reabrir con `IDemandReservations`;
  asentar con `LedgerDocument(InventoryDocumentTypes.X, documentId, lineId)`, dueño `Self`, estado `Available`; guardar en la fila
  `PostingBatchId`, `PostingOccurredAt`, bodega, ubicación y líneas apartadas; compensar leyendo los movimientos del lote con su
  `occurred_at` (partición). `occurredAt` fuera de la ventana de 30 días → hora del proceso, con log.
- [ ] **Paso 4:** verde; luego `BasicInventoryEndToEndTests` y `InventoryEventsConsumerTests` completas, una vez cada una, en fila.
- [ ] **Paso 5:** commit `feat(wms): apply basic inventory events to reservations and the ledger`.

**Revisión del bloque C.**

---

## Cierre (lo hace la sesión que orquesta, no un implementador)

- [ ] `.\scripts\test-feature.ps1` en verde, con sus avisos leídos y mostrados al owner.
- [ ] Revisión final de toda la rama por un subagente de solo lectura sin el contexto de quien implementó (modelo más capaz).
- [ ] Merge a `develop` con `--no-ff` y, en el mismo merge, la fila del WMS y el Registro de `docs/estado.md`.
- [ ] Tarjeta CW22v9pI a «Code Review» con rama, commit, qué se probó y qué no. En la del frontend (Z3l9JnqX), el contrato final de §5.8.
  En la de T3 (3Ft6shos), el contrato final de `purchases.order.received.v1` y la fila `ReceivedByWms`.
