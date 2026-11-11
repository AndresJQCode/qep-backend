# Estados del pedido al estilo Shopify — diseño (backend)

Fecha: 2026-11-10. Rama: `feature/estado-del-envio`. Repo: `qep-backend`. El frontend lleva su propio spec en
`qep-frontend` (pendiente), que consume lo que este documento define.

## 1. Intención

El admin y los asesores necesitan saber, sin abrir cada pedido, **qué se despachó y qué no**, para responderle a
un cliente que pregunta y para ver lo que falta por preparar. Hoy el pedido no tiene estado de envío.

Al revisarlo apareció un problema de fondo: el estado del pedido mezcla tres cosas. `Approved` significa, en la
operación, «pagado y verificado» (la pantalla no deja aprobar sin `FullPaymentReceived`), pero además es el
momento en que el pedido se congela, y es requisito para facturar. El owner decidió separar el pedido en
**ejes independientes, como Shopify**:

| Eje | Valores | Hoy |
| --- | --- | --- |
| Estado del pedido | `Open`, `Archived`, `Cancelled` | `Pending`, `Approved`, `Cancelled`, `Invoiced` |
| Pago | `PaymentPending`, `PartialPaymentReceived`, `PendingVerification` (nuevo), `FullPaymentReceived` | sin `PendingVerification`; la asesora elegía el estado |
| Envío | `NotPrepared`, `Prepared` | no existe |
| Facturación | facturado sí/no (`InvoicedAt` no nulo) | era el estado `Invoiced` |

Referencias de Shopify consultadas: estado del pedido (Open / Archived / Canceled), `OrderDisplayFinancialStatus`,
`OrderDisplayFulfillmentStatus`, la configuración «Archivar el pedido automáticamente» (después de preparado y
pagado) y las reglas de edición (un pedido pagado se edita; lo preparado no).

## 2. Metas, no metas y anti-metas

**Metas**
- Marcar un pedido como preparado (despachado) y deshacerlo; filtrar el listado por envío.
- Quitar `Approved`: «marcar como pagado» toma su lugar, con el mismo control de permisos.
- Facturado deja de ser un estado y pasa a ser una marca filtrable.
- Archivar a mano, y automáticamente si el tenant lo configura.
- Tres ajustes nuevos por tenant (§6).
- Todo cambio de estado queda en el historial del pedido y en auditoría.

**No metas (quedan para después)**
- Número de guía, despacho parcial, «Entregado», eventos de la transportadora.
- Sincronizar el `fulfillment_status` de Shopify: los pedidos de Shopify se preparan a mano en QEP, como los demás.
- Reembolsos: el sobrante se muestra, no se gestiona.

**Anti-metas (sería un fracaso aunque funcione)**
- Que al desplegar un tenant pierda un control que hoy tiene sin haberlo decidido (salvo lo que el owner decidió
  explícitamente en §6).
- Que un pedido despachado se pueda editar o anular por el comprador.
- Que un pedido facturado deje de verse como facturado al archivarse.

## 3. Modelo

### 3.1 Estado del pedido (`OrderStatus`)

`Open`, `Archived`, `Cancelled`.

- `Open`: le queda trabajo (cobrar, preparar o facturar). Todo pedido nace `Open`.
- `Archived`: terminado; sale de la vista por defecto del listado. Se desarchiva a mano y vuelve a `Open`.
- `Cancelled`: terminal, como hoy (quién, cuándo, motivo). Sólo desde `Open`. No se anula un pedido facturado
  (regla actual, `order.order.already_invoiced`) **ni uno preparado** (nuevo).
- No se archiva un pedido anulado, ni se anula uno archivado *(decisión del diseñador)*: desarchivar primero.
- **Excepción de Shopify** (owner, 2026-11-10): una anulación que llega de Shopify (`ShopifyOrderWriter.CancelAsync`)
  gana sobre «preparado» y «archivado»: el pedido queda `Cancelled`, **conserva la marca de preparado** y el
  historial lo registra, para que se vea que hay un paquete despachado que recuperar. Facturado sigue como hoy
  (`already_invoiced` → su outcome actual). Igual que el pago: la plataforma manda sobre sus pedidos. Es un método
  de dominio aparte del `Cancel` de la consola, que conserva sus guardas.

`Pending` y `Approved` desaparecen. `Invoiced` desaparece como estado (§3.4).

### 3.2 Pago (`OrderPaymentStatus`)

Valores: `PaymentPending`, `PartialPaymentReceived`, **`PendingVerification` (nuevo, «Por verificar»)**,
`FullPaymentReceived` («Pagado»). El nombre del valor nuevo queda a criterio del implementador.

El proceso que homologa (owner, 2026-11-10): la asesora carga los comprobantes; otra persona verifica la plata en
el banco y, hoy, «aprueba» el pedido. Es el «Marcar como pagado» de Shopify para métodos manuales.

- **Lo calcula el sistema, no la asesora** (owner): con los comprobantes, el pago es `PaymentPending` (nada
  consignado), `PartialPaymentReceived` (consignado < neto) o `PendingVerification` (consignado ≥ neto, con el
  redondeo por moneda de hoy). `FullPaymentReceived` sólo se alcanza con «Marcar como pagado» o por Shopify.
- **El formulario de comprobantes** (`POST /orders/{id}/proofs`, `quotations.order.manage`) deja de recibir el
  estado del pago: el campo `paymentStatus` del cuerpo se elimina y el estado sale del cálculo.
- **Marcar como pagado** (`FullPaymentReceived`): acción nueva, con permiso `quotations.order.approve` (§7),
  desde cualquier estado de pago que no sea pagado (con el ajuste de §6 apagado, también sin comprobantes).
  - Comprobante opcional u obligatorio según el ajuste del tenant (§6).
  - Corre las validaciones de datos de facturación que hoy corren al aprobar (`BilledPersonTypeRequired`,
    `BilledCustomerFieldsRequired`), con la misma configuración del tenant.
  - Registra quién y cuándo (`PaidMarkedBy`, `PaidMarkedAt`; nombres a criterio del implementador).
- **Si la plata no está en el banco** *(decisión del diseñador)*: no hay acción de «rechazar»; se quita o corrige el
  comprobante equivocado, el recálculo baja el pago y el historial lo registra. Un «rechazar» sin tocar los
  comprobantes volvería a `PendingVerification` en el siguiente recálculo.
- **Deshacer pagado**: mismo permiso; vuelve al valor calculado (`PendingVerification` si lo consignado cubre).
  Sólo si el pedido no está preparado ni facturado.
- **Comprobantes sobre un pedido ya pagado** *(decisión del diseñador)*: agregar o corregir un comprobante con
  `manage` **no cambia** el estado del pago. Quitar un comprobante de un pedido pagado exige
  `quotations.order.approve`; sin él, 403. Así ningún camino con sólo `manage` deshace el pagado.
- **El recálculo** (`RecalculatePaymentStatus`, tras editar líneas o quitar un comprobante) **nunca sube a
  pagado** *(decisión del diseñador: si subiera, un asesor con `manage` lo dejaría pagado sin el permiso)*. Sobre
  un pedido pagado sólo baja a `PartialPaymentReceived` cuando **la edición sube el neto** y el nuevo neto supera lo
  consignado *(decisión del diseñador)*: un pedido marcado pagado con comprobantes parciales (un descuento pactado,
  por ejemplo) no pierde el pagado por una edición que no sube el neto. Un pedido pagado **sin comprobantes**
  (marcado a mano) se queda pagado *(decisión del diseñador)*. En todos los casos el historial muestra el total
  anterior y el nuevo.
- **Saldo**: el DTO del pedido expone el saldo (`neto − consignado`, con el mismo redondeo por moneda de
  `RecalculatePaymentStatus`), positivo si falta, negativo si sobra. El sobrante **no** es un estado: un pedido con
  sobrante sigue pagado y la pantalla dice «Sobran $X». Motivo para el DTO: un total que no cierra se explica con
  un campo.
- **Shopify** (`SyncExternalPaymentStatus`) sigue fijando el pago sin validar datos de facturación ni exigir
  comprobante: lo cobró la plataforma y esos pedidos entran con cliente incompleto. Su guarda pasa de
  `EnsurePending` a «abierto y no preparado ni facturado» *(decisión del diseñador)*.

### 3.3 Envío (`OrderFulfillmentStatus`, nuevo)

`NotPrepared`, `Prepared`. Todo pedido, nuevo o existente, nace `NotPrepared`.

- **Marcar como preparado**: pedido `Open` (no anulado, no archivado), pagado o no (contraentrega se despacha
  antes del cobro). Registra quién y cuándo. Sin guía.
- **Deshacer preparado**: sólo si no está facturado *(decisión del diseñador)*.
- Si el tenant tiene el archivado automático y el pedido ya está pagado, preparar lo archiva (§3.5).
- Permiso: `quotations.order.manage` (decisión de la conversación). Nota: el rol `billing` no tiene `manage`, así
  que facturación no puede preparar; es aceptado.

### 3.4 Facturación (marca)

- Facturado = `InvoicedAt` no nulo. `InvoicedAt`/`InvoicedBy` ya existen (`AddOrderInvoicing`).
- **Facturar**: pedido no anulado y no facturado. Exige pago completo si el ajuste del tenant lo pide (§6). Corre
  las validaciones de datos de facturación (las mismas de marcar pagado). No exige preparado.
- **Revertir facturación**: limpia la marca; no cambia el estado del pedido.
- Los códigos de error actuales (`order.order.not_approved`, `order.order.not_invoiced`…) se revisan: los que
  pierden sentido se reemplazan por códigos nuevos (`order.order.not_paid`, `order.order.already_invoiced`, etc.).
  No inventar: el implementador lista los códigos finales en el plan y el frontend los mapea.

### 3.5 Archivado

- **A mano**: archivar y desarchivar, siempre disponible, con `quotations.order.manage` *(decisión del diseñador)*.
- **Automático**: si el ajuste del tenant está prendido, el pedido se archiva en el instante en que queda
  **pagado y preparado** (la segunda de las dos condiciones que se cumpla, por cualquier camino: marcar pagado,
  marcar preparado o sincronización de Shopify). No se aplica hacia atrás a pedidos existentes.
- Archivar no quita ni pone ninguna otra marca.

### 3.6 Congelamiento (qué deja de editarse)

Hoy todo lo editable exige `Pending`. Pasa a exigir **`Open`, no preparado y no facturado**. Aplica a: editar
líneas, partes, notas y precios (`SaveOrderEdits`, `AddOrderItems`, `PreviewOrderEdits`), comprobantes
(`AddPaymentProofs`, `RemovePaymentProof`), reconocer transportadora (`RecognizeOrderCarrier`) y el resto de
guardas `EnsurePending` de `Order`. **Pagado no congela** (como Shopify): un pedido pagado se edita, y la edición
queda en el historial como hoy. El implementador recorre todas las guardas `Status == Pending` /
`Status != Pending` de Quotations; ninguna puede quedar comparando contra un valor que ya no existe.

### 3.7 Anulación del comprador (`CanBeCancelledByBuyer`)

`Open && PaymentPending && sin comprobantes && no preparado && no facturado`. (Con `PendingVerification` ya hay
comprobantes, así que tampoco se anula.) El 409
`order.order.not_cancellable_by_buyer` no cambia.

## 4. Historial y auditoría

`OrderHistoryEventType` suma: pagado marcado / desmarcado, preparado / preparación deshecha, archivado /
desarchivado (incluido el automático, con el autor de la acción que lo disparó). `Approved` queda en el enum
(filas viejas lo tienen; la columna guarda el nombre) pero ya no se escribe. Cada acción publica su auditoría por
`IQuotationAuditPublisher`, como facturar hoy. Las ediciones de un pedido pagado ya dejan historial
(`ItemsChanged`, `PricingChanged`…); el cambio de pago por recálculo debe quedar visible con total anterior y nuevo.

## 5. Listado, detalle y exportación

- `GET /orders` y `POST /orders/export` suman filtros: `fulfillmentStatus` y `invoiced` (true/false), además de
  `status` (ahora `Open|Archived|Cancelled`) y `paymentStatus`. Por defecto, el listado **sin** filtro de estado
  excluye archivados *(decisión del diseñador, como Shopify)*; filtrar `status=Archived` los muestra.
- `OrderListItemDto` / `OrderListItemResponse` suman `FulfillmentStatus` e `Invoiced` (para pintar las columnas sin
  pedir el detalle). `OrderDto` / `OrderResponse` suman envío, marcas de pagado/preparado/archivado (quién y
  cuándo) y el saldo.
- `ExportStatusLabels` se actualiza; la hoja del ERP (`OrdersExportProcessor`) no cambia de formato salvo que lea
  el estado: revisarlo.
- Enums viajan con su nombre (regla del repo).

## 6. Configuración por tenant (nueva)

| Ajuste | Valor por defecto | Efecto |
| --- | --- | --- |
| Exigir comprobante para marcar pagado | **No** (owner) | Sí: «marcar pagado» exige al menos un comprobante |
| Exigir pago completo para facturar | **Sí** | Sí: facturar exige `FullPaymentReceived` |
| Archivar automáticamente | **No** | Sí: archiva al quedar pagado y preparado |

Los requisitos de datos de facturación ya son configurables (`PersonTypeRequirement`, `CustomerFieldPolicy`) y no se
duplican. `PersonTypeRequirement.OnOrderApproval` cambia de momento (ahora: al marcar pagado y al facturar); si se
renombra el valor persistido, va con migración de datos.

Dónde viven los tres ajustes (Tenancy o la configuración de Quotations) lo decide el implementador siguiendo cómo se
guardan hoy los ajustes de pedidos del tenant; endpoint de lectura/escritura para la pantalla de configuración.

Nota de despliegue: «exigir comprobante» arranca en No, así que desde el despliegue cualquier tenant puede marcar
pagado sin comprobante hasta prenderlo. Decisión explícita del owner.

## 7. Permisos

- `quotations.order.approve` se conserva (constante y valor) y pasa a significar «marcar pedidos como pagados»;
  cambia su nombre visible. Lo tienen admin y `billing`, como hoy.
- `quotations.order.manage`: preparar / deshacer preparado, archivar / desarchivar, comprobantes (sin poder dejar
  pagado).
- `quotations.order.invoice`, `quotations.order.cancel`: sin cambio.
- `POST /orders/{id}/approve` se retira (o responde como «marcar pagado» durante la transición, a criterio del
  implementador, si eso simplifica el despliegue conjunto con el frontend).

## 8. Migración de datos

- `Pending` → `Open`; `Approved` → `Open`; `Invoiced` → `Open` con `InvoicedAt` intacto; `Cancelled` igual.
- `fulfillment_status` nuevo, `NotPrepared` para todos.
- `Approved` (y `Invoiced`) con `FullPaymentReceived`: quedan `FullPaymentReceived`; su `PaidMarkedBy/At` se toma
  de `ApprovedBy/At` *(decisión del diseñador: es quien de hecho verificó el pago)*.
- `Pending` con `FullPaymentReceived` (la asesora lo dejó en «Pago total» y nadie lo aprobó): pasan a
  `PendingVerification` (owner, 2026-11-10: la verificación de facturación no se salta).
- `Approved` sin pago completo (sólo posible por API directa): quedan `Open` y editables otra vez.
- Pedidos de Shopify con pago sincronizado: conservan su valor.
- Ningún pedido existente se archiva.
- Toda escritura cruda a `quotations.orders` (`ExportLoadSeeder`, pruebas con `INSERT` a mano) usa los valores
  nuevos.

## 9. Lectores fuera de la entidad (revisar todos)

- `OrdersReportSource` (SQL crudo): `is_void` usa `Cancelled`, que no cambia; verificar que nada más lea
  `Approved`/`Invoiced`/`Pending`.
- Eventos hacia Pipeline (`quotations.order.*`): verificar si alguno lleva el estado; si sí, es contrato.
- Store/Buyers: la anulación del comprador (§3.7) y el contrato con `qep-storefront`. `IBuyerOrderGateway` manda
  hoy `order.Status.ToString()`. No hay compradores en producción (owner, 2026-11-10), así que el contrato cambia
  sin capa de compatibilidad: el gateway manda los ejes nuevos (estado, pago, envío, facturado) y `qep-storefront`
  se ajusta en su propio spec, en el mismo despliegue.
- Eventos hacia Pipeline: verificado por el revisor, no llevan el estado del pedido. No se tocan.
- Shopify: `ExternalOrderService`/ingesta y `SyncExternalPaymentStatus`.
- `QuotationUserReferenceProbe`: sumar las columnas nuevas de autor (`PaidMarkedBy`, `PreparedBy`, `ArchivedBy`) si
  sigue el patrón de `InvoicedBy`.
- `docs/estado.md` no se toca en la rama (regla del repo).

## 10. Lo que queda al implementador

Nombres de columnas y métodos, rutas exactas de las acciones nuevas (patrón: `POST /orders/{id}/<acción>` sin
cuerpo, como `invoice`/`uninvoice`), forma de guardar los ajustes del tenant, y los códigos de error finales
(listados en el plan). TDD con RED y GREEN literales; pruebas por clase tocada y la suite de integración de
Quotations al final.
