# Facturación masiva con Siigo — diseño (backend)

Estado: **borrador pendiente de aprobación del owner.** Nada está construido. Frontend: spec hermano en `qep-frontend`
(`2026-11-21-siigo-bulk-invoicing-design.md`).

## Intención

En `/orders` el owner filtra los pedidos «sin facturar» y «pagado», los marca y elige «Más acciones → Facturar con Siigo».
Hoy cada factura se emite entrando pedido por pedido. Cada factura sale contra la cuenta **real** de Siigo (no hay empresa de
pruebas) y una emitida a la DIAN no se borra.

## Objetivos

- Una tanda de pedidos se factura sin que la pestaña siga abierta, con resultado por pedido.
- Un pedido que falla no frena ni revierte a los demás.
- Reintentar la tanda o el worker no duplica facturas.
- Quedan auditados quién la lanzó, cuándo y qué pasó con cada pedido.

## No objetivos

- No cambia cómo se factura un pedido suelto ni sus reglas de armado (`SiigoInvoiceBuilder`).
- No se eligen formas de pago por pedido: la tanda usa la propuesta de la cuenta de cobro.
- No hay «reintentar fallidos» ni cancelar una tanda en esta primera versión.
- No se factura en masa desde otro lugar que `/orders`.

## Anti-objetivos

- Emitir una factura por un pedido que ya no cumple las reglas al momento de procesarlo (anulado, ya facturado, sin pagar).
- Que una tanda mal armada deje facturas duplicadas o equivocadas en Siigo.

## Aproximaciones consideradas

1. **Tandas propias en el módulo Siigo + worker** (elegida). Tablas `siigo.invoice_batches` y `siigo.invoice_batch_items`; un worker
   reclama ítems con `FOR UPDATE SKIP LOCKED` y lease, y por cada uno corre el `IssueSiigoInvoiceHandler` que ya existe. Copia el
   patrón de `CampaignDispatchWorker` (WhatsApp) con timbre `QueueDoorbell`. Pros: reutiliza el handler idempotente y su candado por
   pedido; el módulo dueño de Siigo es el dueño de la tanda. Contras: el handler exige un `IExecutionContext` con usuario y permisos,
   y el worker no tiene request (ver «Ejecución como el miembro»).
2. **Tanda en Quotations.** Descartada: Quotations no ve Siigo (los puertos van de Siigo hacia los demás), y la regla de dependencias
   (`SiigoLayerTests`) lo impide sin SQL cruzado.
3. **Endpoint síncrono con tope.** Descartado por el owner (eligió la B, de segundo plano).

## Diseño

**Rutas** (tenant en la ruta, política `siigo.invoice.issue`, handler revalida tenant y permiso, 403 nunca 404):

- `POST /api/v1/tenants/{tenantId}/siigo/invoice-batches` con `{ orderIds: [...] }` → **202** con el id y el resumen. Máximo
  **50** ids, sin repetidos; más es 422 `validation.failed`. Crea la tanda y un ítem `Pending` por pedido, hace commit y timbra.
- `GET …/siigo/invoice-batches/{batchId}` → estado de la tanda y de cada ítem (para que la pantalla consulte el avance).

**Dominio.** `SiigoInvoiceBatch` (id, tenant, `RequestedBy` miembro, `CreatedAt`, `FinishedAt`) e ítem (pedido, estado
`Pending | Sending | Invoiced | Skipped | Failed`, `ErrorCode`, `InvoiceId` de `siigo.invoices`, intentos, lease). La tanda está
`Finished` cuando no quedan ítems `Pending` ni `Sending`. Los códigos son los de `SiigoErrorCodes` (`not_ready`, `rejected`,
`connection_unavailable`, `totals_mismatch`…), nunca el texto de Siigo ni datos del cliente (regla de `error_detail`).

**Procesamiento.** Un ítem por vez por tanda, en el orden pedido, para respetar el límite de llamadas de Siigo (bloqueo al 80 %
de fallidas). Por ítem, en su propio scope: reclama (lease), revalida el pedido y llama al handler con **pagos nulos**. Resultado:
`Created`/`Stamped` → `Invoiced`; 422 `not_ready` o `totals_mismatch` → `Skipped` con el código; rechazo de Siigo → `Failed`;
conexión no disponible → la tanda se pausa y el ítem vuelve a `Pending` (no se marca fallido: no es culpa del pedido); un fallo
transitorio deja la fila de factura `Pending` y el ítem reintenta con la misma clave. Con lease vencido el ítem vuelve a
`Pending` y el handler (que ya reanuda por pedido) evita duplicar.

**Ejecución como el miembro.** El handler usa `IExecutionContext`. Hay precedente: `DelegatedExecutionContext` y
`ExecutionContextAccessor` (Bootstrapper/Copilot) corren una consulta como un miembro fuera de un request. Se reutiliza ese
mecanismo con los permisos que `IAuthorizationService.ResolvePermissionsAsync` da **al procesar** cada ítem: si el miembro perdió el
permiso o la membresía, el ítem queda `Failed` con `forbidden` y la factura no sale. Hoy ese contexto lo crea sólo el ejecutor del
copiloto y lo vigila `CompositionRootTests`: el worker lo necesita, así que ese test se amplía.

**Auditoría.** Una acción de auditoría al crear la tanda (cuántos pedidos) y la de factura de cada ítem sigue siendo la del handler.

**Rastros de solo lectura.** `GET` no devuelve `request_json` ni datos del cliente (igual que `SiigoNoLeakTests`).

## Restricciones

- `siigo.invoices` no se purga ni se revierte. Las tablas nuevas siguen esa regla mientras tengan ítems `Invoiced`.
- Migración nueva y aditiva en el módulo Siigo (expand/contract, sin tocar tablas existentes).
- El worker corre en varias réplicas: reclamo con `SKIP LOCKED` y lease, como las campañas.
- El módulo `siigo` apagado: las rutas dan 403 (`ModuleEntitlementMask`).

## Pruebas (TDD)

Unitarias del dominio (estados, cierre de tanda, tope y duplicados). Integración con el `FakeSiigoApi`: tanda feliz; un pedido
`not_ready` salta y los demás salen; rechazo de Siigo en uno; relanzar el worker no duplica; lease vencido; permiso perdido; 403 sin
permiso o con otro tenant; módulo apagado. Arquitectura: `SiigoLayerTests` y `CompositionRootTests`.

## Decisiones tomadas por el asistente (corrígelas)

Tope de 50; saltar y listar los no listos; sin pagos por pedido; sin reintentar fallidos ni cancelar; ítems en fila (sin paralelismo);
el 202 devuelve el id y la pantalla consulta cada pocos segundos (sin tiempo real); permiso `siigo.invoice.issue`.

## Pendiente del owner

- ¿Tope de 50 está bien?
- ¿«Saltar y listar» es lo que quieres para los no listos?
- ¿Quieres «reintentar fallidos» y «cancelar tanda» ya, o en una segunda versión?
- Dónde mergear: ramas `feature/siigo-bulk-invoicing` en ambos repos, sin tocar `main`.
