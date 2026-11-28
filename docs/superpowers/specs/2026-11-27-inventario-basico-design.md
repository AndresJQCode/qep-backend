# Inventario básico: las compras suman stock y las ventas lo descuentan — diseño del backend

- **Fecha:** 2026-11-27
- **Tarjeta:** [CW22v9pI](https://trello.com/c/CW22v9pI) (`[backend]`). Desbloquea la del frontend
  ([Z3l9JnqX](https://trello.com/c/Z3l9JnqX)) y T3 ([3Ft6shos](https://trello.com/c/3Ft6shos)).
- **Módulos:** Wms, Purchases, Quotations (pedidos), Pos, Bootstrapper (adaptador Catalog → Wms).
- **Fuente aprobada por el owner:** `qep-frontend/docs/superpowers/specs/2026-11-26-inventario-basico-design.md` (commit `4bdb8a87`,
  `develop` local de qep-frontend; en adelante, «diseño compartido»). Este documento no lo reabre: lo baja al backend, resuelve los tres
  choques con el WMS completo que encontró la revisión del motor de tareas y toma las decisiones que el diseño dejó al constructor.
- **Verificado contra el código:** `develop` `8ad0c5627`. Las citas van por símbolo.
- **Revisiones:** una lectura de constructor (preguntas resueltas en el texto) y una revisión adversarial con Fable (15 hallazgos, todos
  incorporados; §10 dice dónde).
- **Estado:** borrador para aprobar. Lo marcado **«Decisión del agente»** no lo eligió el owner: corrígelo si no te sirve.

---

## 1. Intención

El owner necesita, pronto y en producción, un control de inventario básico: el stock sube cuando llega una orden de compra y baja cuando
se vende. **No es un segundo inventario:** es el ledger del WMS, con una configuración inicial que no pide nada (una bodega principal con
su ubicación general). Quien quiera más agrega bodegas, recepción con la app y picking después, sobre los mismos saldos y la misma
historia, sin migración ni cambio de modo.

**Metas:** una orden de compra recibida suma; un pedido reserva al existir y descuenta al quedar «Preparado»; una venta POS descuenta al
crearse; anular, deshacer o desarchivar devuelve las cosas a donde estaban; la pantalla de stock dice disponible, reservado y a la mano.

**No metas (esta entrega):** ningún freno por stock en 0 (el saldo puede quedar negativo); recepción con la app, putaway y picking (T3–T7);
recepciones parciales; interruptor «controla inventario» por producto (sale del tipo de producto); stock en la tienda, Shopify o el POS;
reservar los pedidos abiertos de un tenant que enciende el módulo después (tarea aparte).

**Antimetas:** una falla del inventario nunca falla ni frena una venta o una compra. Ningún saldo fuera del WMS y nada sumado en el
navegador. Ningún cambio de este corte obliga a T3, T5 o T7 a cambiar el contrato de los eventos.

## 2. Reglas decididas por el owner

| Evento | Efecto |
|---|---|
| Orden de compra pasa a **Recibida** | Suma la cantidad confirmada de cada línea en la bodega principal |
| Pedido **creado** (Manual, Shopify, Store, cotización convertida) | **Reserva** cada línea |
| Pedido del mayorista **aprobado** | **Reserva** cada línea (antes es una propuesta editable) |
| Pedido **editado** (sin preparar) | La reserva sigue a las líneas nuevas |
| Pedido marcado **Preparado** | **Descuenta** y consume la reserva |
| «Preparado» deshecho | Devuelve el stock y vuelve a reservar |
| Pedido **anulado** desde la consola (sólo sin preparar) | Libera la reserva |
| Pedido **archivado sin preparar** | **Descuenta**, como «Preparado»: archivar es entregar |
| Pedido **desarchivado** | Deshace lo que hizo su archivo; archivar uno preparado no movió nada, así que desarchivarlo tampoco |
| **Venta POS** creada | Descuenta de una vez |
| **Venta POS anulada** | Devuelve el stock |

- **Sólo productos físicos** (`ProductKind.Physical`); una línea de servicio no produce nada.
- **Camino A, por eventos** (2026-11-26): Purchases, Quotations y POS publican al outbox; el WMS consume con su inbox.
- **Anulación desde la plataforma (2026-11-27):** `Order.CancelFromPlatform` (Shopify) pasa por encima de «preparado» y «archivado». Si el
  pedido ya descontó, **el stock no vuelve solo**: si el paquete regresa, alguien lo registra con un ajuste. La anulación sólo libera la
  reserva que siga abierta.

## 3. Lo que existe hoy (verificado)

- **Outbox único:** todos los módulos escriben en `platform.outbox_messages` y cada `DbContext` lo proyecta con
  `ExcludeFromMigrations()`; `WmsDbContext` ya lo proyecta. El consumidor que se copia es `WholesaleOrderEventsProcessor` +
  `ClaimedOutboxConsumer` de Purchases (inbox propio por consumidor, reclamo con lease, efecto e inbox en la misma transacción), que hoy
  ordena el lote por `OccurredAt`.
- **Purchases:** `PurchaseOrder.Receive` es un cambio de estado sin datos, y el único camino es `TransitionPurchaseOrder`. Las cantidades
  son `ConfirmedQuantity` por línea (`numeric(10,2)`; 0 = línea rechazada). `Received` es final. `PurchaseOrderLine.VariantId` es `Guid?`:
  nulo en líneas vinculadas al mayorista hasta enlazarlas. Sólo hay acción de auditoría (`PurchasesAuditActions.Received`).
- **Quotations:** `QuotationLifecycleEventPublisher` publica `quotations.order.created.v1`, `…cancelled.v1`, `…wholesale-approved.v1`
  (con `approvedAt`, sin `occurredAt`), `…wholesale-cancelled.v1` y `…shipping-party-changed.v1`. Sólo `wholesale-approved` lleva líneas.
  `OrderItem` congela `VariantId`, `Quantity`, `Kind` (`ProductKind`) y `SalesUnit`. `Order.Version` sube en cada mutación, también en las
  que no tocan stock. `Order.Archive` acepta pedidos preparados y sin preparar; `Order.Unarchive` sólo mira `Status`; `Prepare` y
  `UndoPreparation` exigen pedido abierto, así que un archivado no cambia de preparado.
- **POS:** `PosSale` nace `Completed`, `Void(reason)` la pasa a `Voided`; sin `Version`, sin devoluciones, sin eventos. `PosSaleLine`
  tiene `VariantId`, `ProductId` y `Quantity`, sin tipo de producto.
- **WMS:** `IInventoryLedger.PostAsync(LedgerBatch)` exige la transacción de la unidad de trabajo y valida el lote entero
  (`LedgerEntryRules.Validate`): la primera entrada mala tumba todo el lote. Un débito a una ubicación física pasa por `DebitAsync`, que
  exige `on_hand + delta >= reserved`; sólo `Supplier`, `Customer` y `Adjustment` aceptan negativos, y el saldo copia esa marca
  (`allows_negative`) para dos CHECK: `CK_stock_balances_non_negative` y `CK_stock_balances_reserved`
  (`reserved >= 0 AND (allows_negative OR reserved <= on_hand)`). Una variante sin perfil recibe uno por defecto
  (`VariantLogisticsProfile.CreateDefault`: unidad `UND`, precisión 0); si Catalog no conoce la variante, lanza
  `WmsErrorCodes.ProfileVariantUnknown`. El puerto `IWmsCatalogReader` (Bootstrapper `WmsCatalogReader`) ya lee Catalog por variante
  (`WmsVariantSnapshot`). `Reservation` existe sólo como forma persistida (`location_id` no nulo, `FK_reservations_location`,
  `IX_reservations_open_key` no único). `v_stock_summary` sale sólo de `stock_balances`. `ReverseMovementHandler` deja revertir a mano
  cualquier movimiento que no sea `Reversal`. `Warehouse.Create` crea `SUPPLIER`, `CUSTOMER`, `ADJUSTMENT`, `TRANSIT`, la zona `RECEIVING`
  y el muelle `REC-01`. Hay un dueño `Self` por tenant. No hay consumidor de inbox en Wms. La API de Wms lleva la versión en el cuerpo
  (`WmsVersions.Ensure`), no en `If-Match`.

## 4. Enfoques

| Enfoque | Qué cambia | Por qué sí o no |
|---|---|---|
| **A. Eventos del outbox al inbox de Wms** (elegido por el owner) | Cada módulo publica lo que pasó con sus líneas; Wms reserva, libera y asienta | Respeta las fronteras que vigila `ArchitectureTests`, cada módulo en su transacción, y una falla del WMS no toca la venta |
| B. Llamada síncrona desde el módulo que vende | Purchases, Quotations y POS llaman al WMS al guardar | Rechazado: cruza módulos, no hay transacción entre las dos bases y una falla del WMS fallaría la venta |
| C. El WMS consulta a los módulos cada tanto | Un trabajo compara documentos | Rechazado: frágil y tarde |

## 5. Diseño

### 5.1 Eventos (contrato)

Todos al outbox en la misma transacción que el cambio. Campos en camelCase, leídos por nombre. **Toda línea lleva** `lineId`,
`variantId` y `quantity` (en la unidad del documento); las de pedido llevan además `productKind`, que ya está congelado en `OrderItem.Kind`.
**Todo evento lleva** `tenantId`, el id del documento, su `version` y `occurredAt`.

| Evento | Módulo | Nuevo o ampliado | Datos además de lo común |
|---|---|---|---|
| `purchases.order.received.v1` | Purchases | Nuevo | `origin`, líneas con la cantidad **confirmada** (las de 0 no viajan); `variantId` puede ser nulo |
| `quotations.order.created.v1` | Quotations | Ampliado (campos nuevos al final) | `source`, `awaitingWholesaleApproval`, líneas |
| `quotations.order.wholesale-approved.v1` | Quotations | Ampliado | `version`, `occurredAt`, `lineId` y `productKind` por línea |
| `quotations.order.edited.v1` | Quotations | Nuevo | Todas las líneas después de la edición |
| `quotations.order.prepared.v1` | Quotations | Nuevo | `origin`, líneas |
| `quotations.order.unprepared.v1` | Quotations | Nuevo | — |
| `quotations.order.cancelled.v1` | Quotations | Ampliado | `version`, `fromPlatform` |
| `quotations.order.archived.v1` | Quotations | Nuevo | `isPrepared`, líneas |
| `quotations.order.unarchived.v1` | Quotations | Nuevo | `isPrepared` |
| `pos.sale.created.v1` | Pos | Nuevo | líneas |
| `pos.sale.voided.v1` | Pos | Nuevo | — |

- **Ampliar sin romper:** Pipeline, Compras y Shopify leen `created`, `cancelled` y `wholesale-approved` por nombre de campo. Agregar
  campos no los rompe; no se renombra ni se quita ninguno. Una prueba de contrato por evento ampliado fija los campos viejos.
- **`origin` (choque 3):** `purchases.order.received.v1` lleva `origin` = `Manual` (el «Recibir» de la consola, el único que existe hoy)
  o `WmsReceipt` (T3). `quotations.order.prepared.v1` lleva `Manual` o `WmsPacking` (T7). El consumidor ignora `WmsReceipt` y
  `WmsPacking`. Esto solo no basta para T3: ver §5.7.
- **`isPrepared` en el archivo (hallazgo 1):** un pedido archivado no puede prepararse ni deshacerse, así que el `isPrepared` de
  `unarchived` es el mismo de su `archived`. Con él el consumidor sabe si el desarchivo devuelve algo.
- **La versión se toma después de cada mutación, no al final del caso de uso.** Si un caso de uso hace dos mutaciones con evento (por
  ejemplo `PrepareOrder`, que prepara y puede archivar con `OrderAutoArchive.Apply`), cada evento lleva la versión que dejó su mutación.
  El POS no tiene `Version`: `created` viaja con `1` y `voided` con `2` (decisión del agente).
- **Todos los caminos publican** (hallazgo 10, verificado):
  - `edited`: `SaveOrderEdits`, `AddOrderItems` (`order.AddItem`), la edición de Shopify (`Order.ApplyExternalEdit` vía
    `IExternalOrderWriter`), `ExternalOrderWriter.CorrectByMemberAsync` si cambia líneas, y las pasarelas del mayorista y del comprador.
  - `archived` / `unarchived`: `ArchiveOrder`, `UnarchiveOrder`, `OrderAutoArchive.Apply` (desde `PrepareOrder` y `MarkOrderPaid`) y
    `Order.SyncExternalPaymentStatus` (desde `ExternalOrderWriter.SyncPaymentStatusAsync`).
  - `cancelled`: ya sale en todos (`CancelOrder`, `CancelByBuyer`, `CancelByWholesaleBuyer`, `CancelFromPlatform`).
    `wholesale-cancelled` lo ignora el consumidor, para no liberar dos veces.
  - POS: `CreatePosSale` y `VoidPosSale`. Purchases: `TransitionPurchaseOrder`.
  - Una prueba por camino verifica que su evento sale en la misma transacción.

### 5.2 Consumidor en Wms

`WmsInventoryEventsProcessor` + su copia de `ClaimedOutboxConsumer` (consumidor `wms.inventory-events`), con inbox propio
(`wms.inbox_messages`) y worker que escucha `DoorbellQueues.Outbox` con red de seguridad, como el de Purchases. Se clasifica en
`tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` y, si su inbox cuenta como cola, va en `QueueDoorbellInterceptor` y
`QueueMetricsSource` (`.claude/rules/infra-valkey-sesiones.md`).

- **Marca de arranque:** el consumidor sólo mira mensajes posteriores al despliegue (una fecha fija guardada en configuración o en la
  migración). Así no recorre la historia de `created` y `cancelled` de todos los tenants antes del primer evento real.
- **Orden:** el lote se ordena por (`OccurredAt`, `version` del payload, `Id` del outbox, que es `Guid.CreateVersion7()`), y sólo toma un
  mensaje si no hay otro **más viejo del mismo documento** (misma `correlationId`) sin procesar. Un reintento o un lease vencido no puede
  invertir `prepared` y `edited`. Documentos distintos avanzan en paralelo.
- **Idempotencia:** por mensaje (inbox) y por versión. **Todo evento visto escribe o actualiza su fila** en `wms.document_stock_states`,
  aunque no tenga efecto (estado `None`): así un `cancelled` que llegó antes que su `created` deja `last_version` y el `created` viejo no
  reserva (hallazgo 8). Un evento con versión menor o igual a `last_version` se marca procesado sin efecto.
- **Módulo `wms` (hallazgo 6):** sólo los eventos que **crean** estado (`created`, `wholesale-approved`, `received`, `pos.sale.created`)
  miran si el tenant tiene el módulo; los de un documento que ya tiene estado se aplican siempre, aunque el módulo se haya apagado después.
  La consulta es `ITenantModules.FindAsync` con nulo = apagado; no se usa `TenantModuleGuard`, que lanza 403. **Arranque en limpio:** nada
  se rellena hacia atrás.
- **El consumidor filtra las líneas antes de llamar al ledger** (hallazgo 2): el ledger tumba el lote entero en la primera entrada mala,
  así que las líneas que no se pueden mover (§5.6) se apartan antes, con log y métrica, y el asiento lleva sólo las demás.
- **Falla transitoria o determinista** (hallazgo 2): una excepción de infraestructura se reintenta con la curva de Purchases (5 s, 30 s,
  5 min, luego cada 5 min). Una `WmsDomainException` determinista (perfil, ubicación bloqueada, ubicación en conteo) también se reintenta,
  porque puede resolverse sola (el conteo cierra, alguien desbloquea); pasado el umbral, log de error y métrica, y el documento queda
  `Parked` hasta que el mensaje pase. Sólo frena a ese documento.
- **Una reversa vuelve a donde fue el original** y **sólo se deshace lo que se movió** (hallazgo 11): la fila de estado guarda
  `warehouse_id`, `location_id`, el `batch_id` del asiento y su `occurred_at` (los movimientos están particionados por mes y buscarlos sin
  fecha recorre todas las particiones, `wms.md`). La compensación revierte exactamente los movimientos de ese lote, y la nueva reserva sale
  de las reservas que ese asiento consumió, no de las líneas actuales.

**Máquina por pedido.** Estados: `None`, `Reserved`, `TakenOutByPrepare`, `TakenOutByArchive`, `Released`, `HandedToOutbound`.

| Desde | Evento | Efecto | Hacia |
|---|---|---|---|
| `None` | `created` (no en espera de mayorista) o `wholesale-approved` | reserva | `Reserved` |
| `Reserved` | `edited` | cierra las reservas abiertas y abre las nuevas | `Reserved` |
| `Reserved` | `prepared` (`origin = Manual`) | descuenta y consume la reserva | `TakenOutByPrepare` |
| `Reserved` | `archived` con `isPrepared = false` | descuenta y consume la reserva | `TakenOutByArchive` |
| `Reserved` | `cancelled` | libera | `Released` |
| `TakenOutByPrepare` | `unprepared` | compensa el asiento y vuelve a reservar | `Reserved` |
| `TakenOutByPrepare` | `archived` / `unarchived` con `isPrepared = true` | nada | igual |
| `TakenOutByPrepare` | `cancelled` (`fromPlatform`) | nada: decisión del owner | `Released` |
| `TakenOutByArchive` | `unarchived` con `isPrepared = false` | compensa el asiento y vuelve a reservar | `Reserved` |
| `TakenOutByArchive` | `cancelled` (`fromPlatform`) | nada: decisión del owner | `Released` |
| `HandedToOutbound` | cualquiera menos `cancelled` | nada (el pedido lo lleva T5) | igual |

Cualquier otra combinación no hace nada y sólo avanza `last_version`. `HandedToOutbound` no lo escribe nadie en esta entrega: queda
reservado para T5 (§5.7).

### 5.3 Bodega principal

- `Warehouse.IsPrincipal` (`is_principal`), **una por tenant** (índice único parcial), y `Warehouse.GeneralLocationId`
  (`general_location_id`): la ubicación general se encuentra por id, no por código, porque el código se puede cambiar (hallazgo 13).
- **Ubicación general — decisión del agente:** una ubicación nueva `GENERAL`, tipo `Bin` en la raíz, rol `Storage`, con almacenamiento y
  picking permitidos (si no, `pickable` sería 0 y T5 no podría asignar de ahí). `REC-01` se descarta porque T3 la usa como muelle de llegada.
- **Cuándo se crea — decisión del agente:** en el primer evento que la necesite, si el tenant tiene el módulo y no tiene principal. Si
  tiene bodegas activas, la más antigua pasa a ser principal y gana su ubicación general (un método nuevo del agregado; si el código
  `GENERAL` ya existe, usa `GENERAL-1`, `GENERAL-2`…). Si no tiene ninguna activa, crea una con código libre (`PRINCIPAL`, o `PRINCIPAL-1`…
  si `IX_warehouses_tenant_code` lo choca con una inactiva), con sus ubicaciones de nacimiento y el dueño `Self` si falta. Encender el
  módulo no provisiona nada. Un candado por tenant (`pg_advisory_xact_lock`) evita que dos eventos provisionen a la vez.
- **No se desactiva la principal:** `Warehouse.Update` con `isActive = false` sobre la principal falla con un código nuevo
  (`wms.warehouse.principal_cannot_be_deactivated`); primero se elige otra.
- **Cambiarla:** `PUT /tenants/{tenantId}/wms/warehouses/{warehouseId}/principal` con `wms.warehouse.manage` (permiso y política ya
  existen), versión en el cuerpo como el resto de la API de Wms (`{ "version": n }`). Quita la marca de la anterior y guarda antes de
  ponerla en la nueva (el índice no es diferible); si la nueva no tiene ubicación general, la gana. Responde la bodega. Es **contrato HTTP
  nuevo: requiere tu autorización antes del merge.** `WarehouseDto` gana `isPrincipal` y `generalLocationId`.

### 5.4 Reservas: un solo modelo para el pedido y para T5 (choque 2)

El freno `on_hand + delta >= reserved` está en el saldo de cada ubicación. Si los pedidos reservaran ahí, un `Move` o un `Putaway` que
saque mercancía de la ubicación general fallaría con `wms.stock.reserved`, y T5 (que asigna al liberar la ola y hace viajar la asignación
con la mercancía, DA-21) tendría que convivir con otra cosa.

**Decisión del agente: dos niveles en la misma tabla `wms.reservations`.**

- **Reserva de demanda** (esta entrega): por bodega, variante y dueño, **sin ubicación** (`location_id` pasa a nulo; la FK queda para
  las que sí la tienen). No toca `StockBalance.Reserved`, así que nunca frena un `Move`, un `Putaway` ni una venta. Única entre las
  abiertas por (`tenant_id`, `demand_type`, `demand_id`, `demand_line_id`) **con índice parcial** `WHERE status = 'Open' AND location_id IS
  NULL`, para que T5 pueda asignar una línea desde dos ubicaciones o lotes (hallazgo 12).
- **Asignación** (T5): por ubicación, con `location_id`. Es la que suma en `StockBalance.Reserved` y la que respeta el freno del ledger.
  T5 convierte la reserva de demanda del pedido en asignaciones al liberarlo.
- **Lo que se lee (hallazgo 9):**
  - `v_stock_summary` se une con las reservas de demanda abiertas, para que una variante reservada antes de recibir nada aparezca con
    `onHand = 0` y `reserved > 0`. `available = onHand − reservado`, negativo si toca, sin recortar.
  - Los `totals` de la lista de stock (`StockReadQueries.SearchAsync`, que suma `v_stock_rows`) suman también las reservas de demanda del
    mismo filtro, para que el total de la lista y el resumen digan lo mismo. Las filas por ubicación muestran sólo lo asignado.
  - `pickable` sigue siendo lo físico que se puede tomar y puede quedar por encima de `available`: es correcto, y se dice en el DTO.
  - Abrir, cerrar o consumir una reserva de demanda encola el cambio de saldo (`IBalanceChangeQueue`) de esa bodega y variante, para que
    la pantalla se entere aunque no haya asiento.
  - **Los DTO no cambian de campos**, sólo lo que suman `reserved` y `available`.

### 5.5 Saldo negativo por motivo, no por ubicación (choque 1)

Que la ubicación general admita negativos para todo dejaría a un `Move` sacar stock que no existe (DA-26 confía en que el ledger lo
rechace). **Regla:** sólo una salida por venta (`MovementReason.Ship` con documento `Order` o `PosSale`) puede dejar en negativo el saldo
de origen; cualquier otro movimiento conserva el freno, también sobre un saldo que ya esté negativo.

**Mecanismo — decisión del agente:**
- La salida por venta usa un débito propio que **sí respeta lo asignado:** puede bajar `on_hand` por debajo de cero, pero nunca por
  debajo de `reserved` cuando `reserved > 0` (hallazgo 5b). Si toca lo asignado de T5, falla con `wms.stock.reserved` y el mensaje queda
  `Parked` (no frena la venta, que ya pasó).
- La fila de saldo que una venta deja negativa pasa a `allows_negative = true` y lo conserva, para que `CK_stock_balances_non_negative`
  no la rechace. Como esa marca también relaja `CK_stock_balances_reserved`, la garantía de que lo asignado no queda descubierto vive en el
  débito de venta (arriba) y en `DebitAsync` para todo lo demás. El constructor puede preferir cambiar el CHECK; la condición es la misma.
- Todo otro débito desde una ubicación física sigue por `DebitAsync`, que exige `on_hand + delta >= reserved` y por eso falla sobre un
  saldo negativo con `wms.stock.insufficient`. Una compensación de una venta suma, así que puede subir un saldo negativo.

### 5.6 Asientos

| Hecho | Movimiento | Motivo | Documento |
|---|---|---|---|
| Orden recibida | `SUPPLIER` → general | `Receipt` | `PurchaseOrder` / línea |
| Pedido descontado | general → `CUSTOMER` | `Ship` | `Order` / línea |
| Pedido devuelto (`unprepare`, `unarchive`) | compensación del asiento guardado | `Reversal` | `Order` |
| Venta POS | general → `CUSTOMER` | `Ship` | `PosSale` / línea |
| Venta POS anulada | compensación del asiento guardado | `Reversal` | `PosSale` |

- Dueño `Self`, estado `Available`, sin lote. `LedgerDocument.Type` gana `PurchaseOrder`, `Order` y `PosSale`.
- **Tipo de producto (hallazgo 14):** el de pedido viaja en el evento (`OrderItem.Kind`, congelado), así reservar y descontar ven lo mismo
  aunque el producto cambie después. Para POS y Purchases lo resuelve el WMS con el puerto que ya existe: `WmsVariantSnapshot` gana `Kind`
  y `SalesUnit`, y `WmsCatalogReader` (Bootstrapper) los llena.
- **Cantidades y precisión (hallazgo 3) — decisión del agente:** la cantidad del documento se toma en la unidad base del perfil (factor
  1). Los empaques por producto no cambian esto: la cantidad ya está en unidades, no en empaques. El perfil por defecto que crea el consumidor
  toma la precisión de la unidad de venta: 0 para `Unit`, 2 para `Kilogram`, `Meter`, `SquareMeter` y `Liter` (las cantidades de POS,
  pedidos y compras tienen como máximo 2 decimales). Un perfil que ya existe no se toca.
- **Líneas que se apartan antes del asiento**, con log de advertencia y métrica, sin frenar el resto:
  - línea de compra con `variantId` nulo (`Received` es final; esperar no la resuelve);
  - variante que Catalog no encuentra y que no tiene perfil (el ledger lanzaría `ProfileVariantUnknown` para siempre);
  - variante controlada por lote o serie (el inventario básico no conoce lotes);
  - cantidad con más decimales de los que admite un perfil que alguien configuró a mano.
  Una línea apartada queda anotada en la fila de estado, así una compensación o una reserva nueva tampoco la toca.
- **Fecha:** `occurredAt` del evento. Si quedó fuera de la ventana de 30 días del ledger (`LedgerEntryRules`), se usa la hora del proceso y
  se registra.
- **Reversa manual (hallazgo 7):** `ReverseMovementHandler` rechaza los movimientos con documento `PurchaseOrder`, `Order` o `PosSale`
  con un código nuevo (`wms.movement.reverse_from_document`): se deshacen desde el documento. Si no, una reversa manual seguida de un
  `unprepare` o un `voided` devolvería dos veces.
- **Conteo en la ubicación general:** mientras haya un conteo abierto ahí, el ledger sólo acepta ajustes y compensaciones
  (`LedgerEntryRules`). Ventas y compras de ese tenant quedan en reintento hasta que el conteo cierre; ninguna venta se frena, sólo su
  asiento espera. El frontend debería avisarlo al abrir un conteo sobre esa ubicación (se comenta en su tarjeta).

### 5.7 Lo que queda previsto para T3, T5 y T7

- **T3, recepción (hallazgo 4).** `origin` no basta: con la recepción del WMS y el «Recibir» manual a la vez, la misma orden podría sumar
  dos veces. La fila de la orden en `document_stock_states` es el árbitro:
  - el consumidor aparta un `received` con `origin = Manual` si la orden ya tiene documento de recepción en el WMS;
  - T3 no deja abrir recepción sobre una orden cuya fila dice que ya la sumó el inventario básico.
  T3 lo construye; este corte deja la fila y su estado (`Received` / `ReceivedByWms`).
- **T5, salida (hallazgo 5a).** Al liberar un pedido a una ola, T5 pasa su fila a `HandedToOutbound` y convierte la reserva de demanda en
  asignaciones, en la bodega que elija. Desde ahí este consumidor sólo atiende `cancelled` (y sólo para liberar lo que no se haya
  asignado); `edited` y `prepared` con `origin = Manual` no hacen nada.
- **T7, empaque.** Marca el pedido `Prepared` con `origin = WmsPacking`, que este consumidor ignora.

### 5.8 Frontend (se comenta en su tarjeta al llegar a «Code Review»)

- Tipos de documento del ledger: `PurchaseOrder`, `Order`, `PosSale`, y que esos movimientos no se revierten a mano (código nuevo).
- `WarehouseDto.isPrincipal`, `generalLocationId` y la ruta para cambiar la principal (§5.3); el código nuevo al desactivarla.
- El resumen y los totales de stock suman las reservas de demanda en `reserved` y `available`; `pickable` puede superar a `available`.
- Abrir un conteo sobre la ubicación general deja en espera los asientos de ventas y compras.

## 6. Pruebas

TDD, RED antes que GREEN. Unitarias para la máquina por pedido (cada fila de §5.2, incluida la secuencia preparar → archivar →
desarchivar del hallazgo 1), el orden de mensajes por documento, la regla de negativo por motivo y cada payload. Integración
(Testcontainers) para: cada camino de §5.1 escribe su evento en la misma transacción; el consumidor asienta, reserva, libera y compensa;
idempotencia por mensaje y por versión, incluido un `cancelled` antes de su `created`; tenant sin módulo y módulo apagado a mitad de camino;
líneas apartadas (variante desconocida, lote, decimales); los tres choques (un `Move` desde un saldo negativo falla; una reserva de demanda
no frena un `Move`; una venta no descubre lo asignado; `origin = WmsReceipt` no suma); la reversa manual rechazada; la vista y los totales
con una reserva sin saldo. Pruebas de contrato que fijan los campos viejos de los tres eventos ampliados. Cambian las pruebas de
arquitectura del inventario de réplicas (worker nuevo).

## 7. Al criterio del constructor

- La forma exacta de los records de payload y de los índices de `document_stock_states` y `inbox_messages`.
- Cómo se consulta «hay un mensaje más viejo del mismo documento» sin volver lenta la consulta del lote.
- Dónde vive la marca de arranque del consumidor.
- Nombre de la métrica y del log de líneas apartadas.
- Si el negativo por venta se logra con la marca de la fila o cambiando el CHECK (§5.5), con las mismas garantías.

## 8. Despliegue

Migración en Wms: `warehouses.is_principal` (índice único parcial) y `general_location_id`, `reservations.location_id` nulo con su índice
parcial nuevo, `document_stock_states`, `inbox_messages` y la vista `v_stock_summary` nueva. Se despliega antes que el frontend, que lee
todo lo nuevo de forma tolerante.

## 9. Para aprobar

1. Este diseño entero, con las «Decisiones del agente».
2. El contrato HTTP nuevo de §5.3 (`PUT …/principal`), los campos `isPrincipal` y `generalLocationId`, y los dos códigos de error nuevos
   (`wms.warehouse.principal_cannot_be_deactivated`, `wms.movement.reverse_from_document`).

## 10. Hallazgos de la revisión de Fable y dónde quedaron

| # | Hallazgo | Dónde |
|---|---|---|
| 1 | Desarchivar un pedido preparado devolvía stock | §5.1 `isPrepared`, §5.2 estados separados |
| 2 | Variante desconocida o línea inválida dejaba el documento trabado | §5.2 filtro previo y `Parked`, §5.6 líneas apartadas |
| 3 | Cantidades con decimales nunca se contaban | §5.6 precisión por unidad de venta |
| 4 | Recepción manual + recepción del WMS sumaban dos veces | §5.7 T3 |
| 5 | Traspaso a T5/T7 indefinido; la venta podía comerse lo asignado | §5.2 `HandedToOutbound`, §5.5, §5.7 |
| 6 | Apagar y prender el módulo dejaba reservas colgadas | §5.2 módulo |
| 7 | Reversa manual podía devolver dos veces | §5.6 reversa manual |
| 8 | Versión sin lápida y sin desempate | §5.1 versión por mutación, §5.2 orden e idempotencia |
| 9 | Vista y totales no veían la reserva de demanda | §5.4 lo que se lee |
| 10 | Caminos sin publicar (`AddOrderItems`, `SyncExternalPaymentStatus`) | §5.1 todos los caminos |
| 11 | La fila de estado no alcanzaba para revertir | §5.2 `batch_id` y `occurred_at` |
| 12 | Índice de reserva debía ser parcial | §5.4 |
| 13 | Ubicación general por código, principal desactivable, `If-Match` | §5.3 |
| 14 | Puerto duplicado; tipo de producto inestable | §5.6 tipo de producto |
| 15 | Worker sin clasificar, sin marca de arranque, conteo congela asientos | §5.2, §5.6, §6 |
