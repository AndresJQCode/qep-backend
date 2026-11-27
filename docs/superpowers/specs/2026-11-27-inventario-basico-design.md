# Inventario básico: las compras suman stock y las ventas lo descuentan — diseño del backend

- **Fecha:** 2026-11-27
- **Tarjeta:** [CW22v9pI](https://trello.com/c/CW22v9pI) (`[backend]`). Desbloquea la del frontend
  ([Z3l9JnqX](https://trello.com/c/Z3l9JnqX)) y T3 ([3Ft6shos](https://trello.com/c/3Ft6shos)).
- **Módulos:** Wms, Purchases, Quotations (pedidos), Pos, Bootstrapper (adaptador Catalog → Wms).
- **Fuente aprobada por el owner:** `qep-frontend/docs/superpowers/specs/2026-11-26-inventario-basico-design.md` (commit `4bdb8a87`,
  `develop` local de qep-frontend; en adelante, «diseño compartido»). Este documento no lo reabre: lo baja al backend, resuelve los tres
  choques con el WMS completo que encontró la revisión del motor de tareas y toma las decisiones que el diseño dejó al constructor.
- **Verificado contra el código:** `develop` `8ad0c5627`. Las citas van por símbolo.
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
- **Anulación desde la plataforma (2026-11-27, nueva):** `Order.CancelFromPlatform` (Shopify) pasa por encima de «preparado» y
  «archivado». Si el pedido ya descontó, **el stock no vuelve solo**: si el paquete regresa, alguien lo registra con un ajuste. La
  anulación sólo libera la reserva que siga abierta.

## 3. Lo que existe hoy (verificado)

- **Outbox único:** todos los módulos escriben en `platform.outbox_messages` y cada `DbContext` lo proyecta con
  `ExcludeFromMigrations()`; `WmsDbContext` ya lo proyecta. El consumidor que se copia es `WholesaleOrderEventsProcessor` +
  `ClaimedOutboxConsumer` de Purchases (inbox propio por consumidor, reclamo con lease, efecto e inbox en la misma transacción).
- **Purchases:** `PurchaseOrder.Receive` es un cambio de estado sin datos; las cantidades son `ConfirmedQuantity` por línea (0 = línea
  rechazada). `Received` es final. `PurchaseOrderLine.VariantId` es `Guid?`: nulo en líneas vinculadas al mayorista hasta enlazarlas. Sólo
  hay acción de auditoría (`PurchasesAuditActions.Received`), ningún evento.
- **Quotations:** `QuotationLifecycleEventPublisher` publica `quotations.order.created.v1`, `…cancelled.v1`, `…wholesale-approved.v1`,
  `…wholesale-cancelled.v1` y `…shipping-party-changed.v1`. Sólo `wholesale-approved` lleva líneas. No hay eventos de preparado, deshecho,
  editado, archivado ni desarchivado. `OrderItem` congela `VariantId`, `Quantity`, `Kind` (`ProductKind`) y `SalesUnit`. `Order.Version`
  sube en cada cambio. `Order.ArchiveIfSettled` sólo archiva pedidos ya preparados.
- **POS:** `PosSale` nace `Completed`, `Void(reason)` la pasa a `Voided`; sin `Version`, sin devoluciones, sin eventos. `PosSaleLine`
  tiene `VariantId`, `ProductId` y `Quantity`, pero no el tipo de producto.
- **WMS:** `IInventoryLedger.PostAsync(LedgerBatch)` exige la transacción de la unidad de trabajo. Un débito a una ubicación física pasa
  por `DebitAsync`, que exige `on_hand + delta >= reserved` y si no lanza `StockInsufficientException` o `wms.stock.reserved`; sólo las
  ubicaciones con `Location.AllowsNegative` (`Supplier`, `Customer`, `Adjustment`) aceptan negativos, y el saldo copia esa marca
  (`allows_negative`) para el CHECK de la base. Un variante sin perfil recibe uno por defecto (`CreateDefaultProfileAsync`).
  `Reservation` y `StockBalance.Reserved` existen sólo como forma persistida; nadie los escribe. `Warehouse.Create` crea `SUPPLIER`,
  `CUSTOMER`, `ADJUSTMENT`, `TRANSIT`, la zona `RECEIVING` y el muelle `REC-01`. Hay un dueño `Self` por tenant (`Owner.CreateSelf`).
  No hay consumidor de inbox en Wms.

## 4. Enfoques

| Enfoque | Qué cambia | Por qué sí o no |
|---|---|---|
| **A. Eventos del outbox al inbox de Wms** (elegido por el owner) | Cada módulo publica lo que pasó con sus líneas; Wms reserva, libera y asienta | Respeta las fronteras que vigila `ArchitectureTests`, cada módulo en su transacción, y una falla del WMS no toca la venta |
| B. Llamada síncrona desde el módulo que vende | Purchases, Quotations y POS llaman al WMS al guardar | Rechazado: cruza módulos, no hay transacción entre las dos bases y una falla del WMS fallaría la venta |
| C. El WMS consulta a los módulos cada tanto | Un trabajo compara documentos | Rechazado: frágil y tarde |

## 5. Diseño

### 5.1 Eventos (contrato)

Todos al outbox en la misma transacción que el cambio, como ya hace cada módulo. Campos en camelCase, leídos por nombre. **Toda línea
lleva** `lineId`, `variantId` y `quantity` (en la unidad del documento). **Todo evento lleva**
`tenantId`, el id del documento, su `version` y `occurredAt`.

| Evento | Módulo | Nuevo o ampliado | Datos además de lo común |
|---|---|---|---|
| `purchases.order.received.v1` | Purchases | Nuevo | `origin`, líneas con la cantidad **confirmada** (las de 0 no viajan); `variantId` puede ser nulo |
| `quotations.order.created.v1` | Quotations | Ampliado (campos nuevos al final) | `source`, `awaitingWholesaleApproval`, líneas |
| `quotations.order.wholesale-approved.v1` | Quotations | Ampliado | `version`, `lineId` por línea (las líneas ya viajan) |
| `quotations.order.edited.v1` | Quotations | Nuevo | Todas las líneas después de la edición |
| `quotations.order.prepared.v1` | Quotations | Nuevo | `origin`, líneas |
| `quotations.order.unprepared.v1` | Quotations | Nuevo | — |
| `quotations.order.cancelled.v1` | Quotations | Ampliado | `version`, `fromPlatform` |
| `quotations.order.archived.v1` | Quotations | Nuevo | `wasPrepared`, líneas |
| `quotations.order.unarchived.v1` | Quotations | Nuevo | — |
| `pos.sale.created.v1` | Pos | Nuevo | líneas |
| `pos.sale.voided.v1` | Pos | Nuevo | — |

- **Ampliar sin romper:** Pipeline, Compras y Shopify leen `created`, `cancelled` y `wholesale-approved` por nombre de campo. Agregar
  campos no los rompe; no se renombra ni se quita ninguno. Una prueba de contrato por evento ampliado fija los campos viejos.
- **`origin` (choque 3):** `purchases.order.received.v1` lleva `origin` = `Manual` (el «Recibir» de la consola, el único que existe hoy)
  o `WmsReceipt` (lo pondrá T3 cuando la recepción del WMS marque la orden). `quotations.order.prepared.v1` lleva `Manual` o `WmsPacking`
  (T7). **El consumidor del inventario básico ignora `WmsReceipt` y `WmsPacking`:** ese stock ya lo movió la recepción o el empaque del
  propio WMS. Así T3 y T7 no cambian este contrato.
- **Versión del POS:** `PosSale` no tiene `Version`. **Decisión del agente:** `created` viaja con `version = 1` y `voided` con `2`.
- **Tipo de producto — decisión del agente:** POS y Purchases no lo tienen (`PosProductRef` y los puertos de Purchases no exponen
  `ProductKind`). En vez de abrir dos contratos con Catalog, **lo decide el WMS:** un puerto `IWmsCatalogKinds.FindKindsAsync(tenantId,
  variantIds)` en `Modules.Wms.Application`, implementado en Bootstrapper sobre Catalog (como `ShopifyProductLookup`). Por eso las líneas
  **no** llevan `productKind`. Una variante que Catalog ya no encuentra cuenta como `Physical` (más vale un movimiento de más, que se
  ajusta, que una venta física que no descuenta).
- **Todos los caminos publican:** toda ruta que cambia las líneas de un pedido publica `edited` (consola `SaveOrderEdits`, edición de
  Shopify `Order.ApplyExternalEdit` vía `IExternalOrderWriter`, y las pasarelas del mayorista y del comprador). Toda ruta que archiva o
  desarchiva publica `archived` / `unarchived` (manual y `ArchiveIfSettled`). Toda ruta que anula publica `cancelled`, que ya sale en
  todas; `wholesale-cancelled` lo ignora el consumidor del inventario, para no liberar dos veces.
- **La versión se toma después del cambio:** el publicador lee `Order.Version` (o `PurchaseOrder.Version`) ya incrementada, en la misma
  transacción.

### 5.2 Consumidor en Wms

`WmsInventoryEventsProcessor` + su copia de `ClaimedOutboxConsumer` (consumidor `wms.inventory-events`), con inbox propio
(`wms.inbox_messages`), worker con despertador y red de seguridad como el de Purchases.

- **Idempotente por mensaje** (inbox) **y por orden** (versión). Por documento se guarda `wms.document_stock_states`
  (`tenant_id`, `document_type`, `document_id`, `last_version`, `state`, `warehouse_id`, `location_id`). Un evento con versión menor o
  igual a `last_version` se marca procesado sin efecto. Las versiones pueden saltar (el pedido sube versión por pagos e historial); sólo
  importa que crecen.
- **En orden por documento — decisión del agente:** el lote sólo toma un mensaje si no hay otro **más viejo del mismo documento**
  (misma `correlationId`) sin procesar. Un reintento o un lease vencido no puede invertir `prepared` y `edited`. Documentos distintos
  avanzan en paralelo.
- **Mensaje que no se puede aplicar — decisión del agente:** se reintenta con la curva de Purchases (5 s, 30 s, 5 min, y luego cada
  5 min) y, pasado el umbral, deja un log de error y una métrica. **No se descarta**, porque saltarlo dejaría mal el estado del documento;
  sólo frena a ese documento, no a la cola.
- **Mayorista en espera:** `created` con `awaitingWholesaleApproval = true` no crea estado; un `cancelled` antes de la aprobación no hace
  nada.
- **Sólo tenants con el módulo `wms`:** con `TenantModuleGuard`. Un evento de un tenant sin el módulo se marca procesado sin efecto y no
  provisiona nada. **Arranque en limpio:** nada se rellena hacia atrás; un pedido que nunca reservó no tiene estado, y su `unprepare`,
  anulación o edición no produce nada.
- **Efecto e inbox en la misma transacción** del `WmsDbContext`, con `PostAsync` del ledger. Payload malformado: procesado sin efecto y
  registrado. Una falla (ubicación bloqueada o en conteo, perfil incompatible) vuelve a intentarse con la curva de Purchases y, pasado el
  umbral, deja un log de error y una métrica; **nunca toca al módulo que vendió**.
- **Una reversa vuelve a donde fue el original:** cada estado de documento y cada reserva guardan su bodega y ubicación. Anular,
  deshacer, liberar o editar opera contra eso, no contra la bodega principal de hoy. Cambiar la principal sólo afecta lo que viene.
- **Sólo se deshace lo que se movió:** el estado del documento dice qué hizo (reservó, descontó o nada).

**Máquina por pedido** (estado en `document_stock_states`):

| Estado | `prepared` / `archived` sin preparar | `unprepared` / `unarchived` | `edited` | `cancelled` |
|---|---|---|---|---|
| (sin estado) | nada | nada | nada | nada |
| `Reserved` | descuenta, consume la reserva → `TakenOut` | — | reemplaza la reserva | libera → `Released` |
| `TakenOut` | — | devuelve y vuelve a reservar → `Reserved` | nada (un pedido preparado no se edita) | **nada** si `fromPlatform` (decisión del owner); no ocurre desde la consola |
| `Released` | nada | nada | nada | nada |

`created` (no mayorista en espera) y `wholesale-approved` reservan desde «sin estado». `archived` con `wasPrepared = true` no hace nada,
y su `unarchived` tampoco.

### 5.3 Bodega principal

- `Warehouse.IsPrincipal` (columna `is_principal`), **una por tenant** (índice único parcial). Compras y ventas usan siempre la principal
  en esta entrega.
- **Ubicación general — decisión del agente:** una ubicación nueva `GENERAL` (rol `Storage`), creada con la bodega principal. `REC-01`
  se descarta porque T3 la usa como muelle de llegada y mezclarla con lo vendible confundiría la recepción.
- **Cuándo se crea — decisión del agente:** en el primer evento que la necesite, si el tenant tiene el módulo y no tiene principal. Si ya
  tiene bodegas, la más antigua activa pasa a ser principal (y gana su `GENERAL` si no la tenía); si no tiene ninguna, se crea
  `PRINCIPAL` con todas sus ubicaciones de nacimiento (y el dueño `Self` si falta). Encender el módulo no provisiona nada. Dos eventos
  del mismo tenant no pueden provisionar a la vez: el consumidor toma un candado por tenant (`pg_advisory_xact_lock`) antes de buscar la
  principal. Agregar `GENERAL` a una bodega que ya existe es un método nuevo del agregado `Warehouse`; si ya tiene una ubicación con ese
  código, se usa esa.
- **Cambiarla:** `PUT /tenants/{tenantId}/wms/warehouses/{warehouseId}/principal` con `wms.warehouse.manage` (permiso que ya existe),
  `If-Match` con la versión. Responde la bodega. Es **contrato HTTP nuevo: requiere tu autorización antes del merge.** `WarehouseDto`
  gana `isPrincipal`.

### 5.4 Reservas: un solo modelo para el pedido y para T5 (choque 2)

El freno `on_hand + delta >= reserved` está en el saldo de cada ubicación. Si los pedidos reservaran ahí, un `Move` o un `Putaway` que
saque mercancía de la ubicación general fallaría con `wms.stock.reserved`, y T5 (que reserva al liberar la ola y hace viajar la reserva
con la mercancía, DA-21) tendría que convivir con otra cosa.

**Decisión del agente: dos niveles en la misma tabla `wms.reservations`.**

- **Reserva de demanda** (esta entrega): por bodega, variante y dueño, **sin ubicación** (`location_id` pasa a nulo). La deja un pedido
  al existir. **No toca `StockBalance.Reserved`**, así que nunca frena un `Move`, un `Putaway` ni una venta.
- **Asignación** (T5): por ubicación, con `location_id`. Es la que suma en `StockBalance.Reserved` y la que el freno del ledger respeta.
  T5 convierte la reserva de demanda del pedido en asignaciones al liberar la ola; el contrato de este corte no cambia.
- **Lo que se lee:** `reserved` y `available` del resumen por bodega y variante (`v_stock_summary`) suman las reservas de demanda
  abiertas: `available = onHand − reservado`, negativo si toca, sin recortar. Las filas por ubicación siguen mostrando sólo lo asignado
  (cero hasta T5). **Los DTO no cambian de campos**, sólo cambia lo que suman `reserved` y `available` del resumen.
- **Clave:** una reserva de demanda abierta es única por (`tenant_id`, `demand_type`, `demand_id`, `demand_line_id`). Una edición cierra
  las abiertas del pedido y abre las nuevas en la misma transacción.
- Antes de volver nulo `location_id`, el constructor revisa que ninguna vista, índice o SQL del ledger lo suponga no nulo
  (`IX_reservations_open_key`, `v_stock_summary`).

### 5.5 Saldo negativo por motivo, no por ubicación (choque 1)

Que la ubicación general admita negativos para todo dejaría a un `Move` sacar stock que no existe (DA-26 confía en que el ledger lo
rechace). **Regla:** sólo una salida por venta (`MovementReason.Ship` con documento `Order` o `PosSale`) puede dejar en negativo el saldo
de origen; cualquier otro movimiento conserva el freno, también sobre un saldo que ya esté negativo.

**Mecanismo — decisión del agente:** el freno queda en el código del ledger, no en el CHECK. La salida por venta va por el camino del
upsert (sin `DebitAsync`) y deja `allows_negative = true` en esa fila de saldo, que lo conserva; el CHECK
`on_hand >= 0 OR allows_negative` deja de frenarla. Todo otro débito desde una ubicación física sigue por `DebitAsync`, que exige
`on_hand + delta >= reserved` y por eso falla sobre un saldo negativo con `wms.stock.insufficient`. Una compensación de una venta suma, así
que puede subir un saldo negativo.

### 5.6 Asientos

| Hecho | Movimiento | Motivo | Documento |
|---|---|---|---|
| Orden recibida | `SUPPLIER` → `GENERAL` | `Receipt` | `PurchaseOrder` / línea |
| Pedido descontado | `GENERAL` → `CUSTOMER` | `Ship` | `Order` / línea |
| Pedido devuelto (`unprepare`, `unarchive`) | compensación del anterior | `Reversal` | `Order` |
| Venta POS | `GENERAL` → `CUSTOMER` | `Ship` | `PosSale` / línea |
| Venta POS anulada | compensación | `Reversal` | `PosSale` |

- Dueño `Self`, estado `Available`, sin lote. `LedgerDocument.Type` gana `PurchaseOrder`, `Order` y `PosSale`.
- **Cantidades — decisión del agente:** la cantidad del documento se toma como cantidad en la unidad base del perfil (factor 1). Las
  presentaciones (ADR-08) no entran: ni la venta ni la compra dicen en qué presentación se movió. Los empaques por producto no cambian
  esto: la cantidad del pedido ya está en unidades (250 = dos empaques de 100 y 150), no en empaques. Un producto físico vendido por kilo,
  metro o litro mueve su cantidad tal cual; si trae más decimales de los que admite el perfil de la variante, la línea se salta con log y
  métrica, como la de lote.
- **Línea de compra con `variantId` nulo — decisión del agente:** se salta, con log de advertencia y métrica; `Received` es final, así
  que esperar no la resolvería. Se corrige con un ajuste.
- **Variante controlada por lote o serie:** no puede moverse sin lote. **Decisión del agente:** la línea se salta con log y métrica; el
  inventario básico no conoce lotes. Sólo pasa con variantes que alguien configuró así: el perfil por defecto
  (`GetOrCreateDefaultAsync`) no controla lote (el constructor lo confirma).
- **Fecha:** `occurredAt` del evento. Si quedó fuera de la ventana de 30 días del ledger (`LedgerEntryRules`), se usa la hora del
  proceso y se registra.

### 5.7 Frontend (se comenta en su tarjeta al llegar a «Code Review»)

- Tipos de documento del ledger: `PurchaseOrder`, `Order`, `PosSale`.
- `WarehouseDto.isPrincipal` y la ruta para cambiarla (§5.3).
- El resumen de stock suma las reservas de demanda en `reserved` y `available` (§5.4).

## 6. Pruebas

TDD, RED antes que GREEN. Unitarias para la máquina por pedido (cada celda de §5.2), la regla de negativo por motivo y cada payload.
Integración (Testcontainers) para: cada publicador escribe su evento en la misma transacción; el consumidor asienta, reserva, libera y
compensa; idempotencia por mensaje y por versión; tenant sin módulo; los tres choques (un `Move` desde un saldo negativo falla; una
reserva de demanda no frena un `Move`; `origin = WmsReceipt` no suma). Pruebas de contrato que fijan los campos viejos de los tres eventos
ampliados. Una `WmsLayerTests` nueva si el consumidor cruza capas, y la de arquitectura de los módulos que publican no cambia.

## 7. Al criterio del constructor

- La forma exacta de los records de payload y del índice de `document_stock_states`.
- Nombre de la métrica y del log de líneas saltadas.
- Cómo se consulta «hay un mensaje más viejo del mismo documento» sin volver lenta la consulta del lote.

## 8. Despliegue

Migración en Wms (`warehouses.is_principal` con su índice único parcial, `reservations.location_id` nulo, `document_stock_states`,
`inbox_messages`). Se despliega antes que el frontend, que lee todo lo nuevo de forma tolerante.

## 9. Para aprobar

1. Este diseño entero, con las «Decisiones del agente».
2. El contrato HTTP nuevo de §5.3 (`PUT …/principal`) y el campo `isPrincipal`.
