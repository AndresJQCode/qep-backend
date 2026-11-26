# Siigo: facturación en segundo plano y facturas por confirmar — diseño (backend)

Fecha: 2026-11-24 · Repo: `qep-backend` · Estado: **aprobado por el owner (2026-11-24).** Nada está construido.
Frontend: spec hermano pendiente en `qep-frontend` (ver «Qué debe cubrir el spec del frontend»).

Parte de lo que ya está en `develop` y todavía no en producción: el código `siigo.invoice.siigo_service`
(`SiigoApiErrors.IsServiceUnavailable`) y las tandas sin reintentos programados (`SiigoInvoiceBatchItem.MaxAttempts = 1`).

## Intención

En producción (2026-11-24) una tanda tuvo muchos timeouts en `InvoiceCreate`: el `HttpClient` de Siigo corta a los 30 s
(`SiigoApiClient.ConfigureClient`), y Siigo recomienda 120 s o más para crear documentos
([Manejo de errores](https://developers.siigo.com/docs/siigoapi/manejo-de-errores/)). Ocho facturas siguieron el mismo patrón:

1. El primer envío se cortó por timeout: la fila quedó `Pending` (no se sabe si Siigo la creó).
2. Cerca de un minuto después se reenvió con la **misma** `Idempotency-Key`, Siigo contestó 400 `documents_service`, y la fila pasó a
   `Rejected` (`SiigoInvoice.MarkRejected`), **olvidando el primer intento sin respuesta**.
3. `Rejected` libera el pedido (`IsLive = false`): regenerar arma una fila nueva con **otra** clave, y si la primera sí existe en Siigo,
   queda duplicada.

No hubo 429 ni `requests_limit` (el límite documentado es 100 peticiones por minuto por empresa). Siigo documenta que reenviar la misma
`Idempotency-Key` devuelve el documento ya creado, pero en estos casos contestó un error de servicio, no el documento.

Además, «Generar factura» desde el pedido es hoy un `POST` síncrono que espera a Siigo: con 120 s de timeout el navegador quedaría
colgado más que el corte de Cloudflare, y ese corte aborta el request en el servidor a mitad del envío.

## Objetivos

- El navegador nunca espera a Siigo: toda emisión (un pedido o hasta 50) es una tanda, la API responde **202** y el worker existente
  manda.
- Una pantalla tipo «campaña enviada»: cuántas facturas salieron y el estado de cada una, con la causa real.
- Una factura que tuvo un intento sin respuesta, o un error de servicio de Siigo, **nunca** pasa a `Rejected`: queda **por confirmar**
  con la misma clave. La persona la reenvía o declara si existe en Siigo, sin esperar 24 h.
- La pantalla distingue una caída de Siigo (problema de Siigo) de un dato mal enviado y de una falla de la plataforma.
- Timeout de 120 s para crear la factura, con leases y apagado revisados contra ese tiempo.

## No objetivos

- No cambia el armado de la factura (`SiigoInvoiceBuilder`), las reglas de pagos ni el redondeo.
- Sellar ante la DIAN (`POST …/siigo-invoice/stamp`) y descargar el PDF siguen síncronos, con 30 s (ver «Riesgos»).
- No hay cancelar una tanda ni editar sus pedidos.
- No se consulta a Siigo para saber si una factura existe (no hay en el código una búsqueda de facturas por clave; ver decisiones).

## Anti-objetivos

- **Facturas legales duplicadas.** Es el riesgo principal y manda sobre la comodidad: ante la duda, la factura queda por confirmar y la
  decide una persona.
- Reintentos automáticos o programados (owner, 2026-11-24): con Siigo caído la persona puede crear la factura directamente allá, y un
  envío posterior la duplicaría. Reintentar sólo ocurre porque alguien lo pide.
- Decirle al tenant «inténtalo de nuevo» cuando lo que hay que hacer es mirar en Siigo.

## Restricciones

- `siigo.invoices` es registro legal: sin purga, su `Down` no se corre (`.claude/rules/siigo.md`).
- `error_detail` guarda códigos de Siigo, no mensajes (`SiigoNoLeakTests`).
- `SiigoOpenInvoiceCheck` es SQL crudo sobre `siigo.invoices` (`status`, `order_id`, `tenant_id`): no se renombran.
- Un pedido por vez por réplica en el worker; tope de 50 pedidos por tanda (`SiigoInvoiceBatch.MaxItems`).
- Apagado: `ShutdownTimeout` por defecto de .NET (30 s) y `terminationGracePeriodSeconds: 45` (`k8s/prod-deployment.yaml`).
- Migraciones expand/contract: el despliegue corre el código viejo contra el esquema nuevo durante el rollout.

## Enfoques

**A. Toda emisión es una tanda (elegido por el owner).** El pedido suelto crea una tanda de un pedido; `POST …/invoice-batches`
responde 202 y `SiigoInvoiceBatchWorker` la procesa con el mismo `IssueSiigoInvoiceHandler`. Pros: un solo camino al envío (candado por
pedido, clave de idempotencia, ejecución como quien la pidió, auditoría), una sola pantalla de seguimiento, y el pedido suelto hereda
gratis el timeout largo. Contras: la tanda tiene que llevar las formas de pago elegidas (hoy no las lleva) y la pantalla del pedido
consulta el avance en vez de recibir la factura en la respuesta.

**B. Camino de segundo plano propio para la factura suelta.** Descartado: duplica el camino (otra cola, otro reclamo, otro manejo de
leases y de apagado) para el mismo handler, y dos caminos divergen justo en los casos raros que causan duplicados.

## Diseño

### 1. La emisión desde el pedido pasa por una tanda de uno

- `POST /api/v1/tenants/{tenantId}/siigo/invoice-batches` acepta un campo opcional **`payments`** (nuevo) con la misma forma que el
  cuerpo de hoy de `POST …/siigo-invoice` (`SiigoPaymentChoice`: `kind`, `proofId`, `siigoPaymentTypeId`):

  ```json
  { "orderIds": ["…"], "payments": [{ "kind": "Order", "proofId": "…", "siigoPaymentTypeId": "…" }] }
  ```

  - Sólo vale con **un** pedido: con `payments` y más de un `orderId` es 422 `validation.failed` en el campo `payments` (las formas de
    pago son por comprobante de un pedido). Cada elemento se valida con las mismas reglas de `IssueSiigoInvoiceValidator`.
  - Se guarda en el ítem (`payments_json`, nuevo) y `SiigoBatchItemInvoker` lo pasa a `IssueSiigoInvoiceCommand.Payments`; hoy pasa
    `null`. Un comprobante que no es del pedido sigue siendo `siigo.invoice.payments_mismatch`, y sale como `Skipped` al procesarse.
- **Un pedido que ya está en una tanda sin terminar** (ítem `Pending` o `Sending`) no entra a otra: 409
  `siigo.invoice_batch.order_in_progress` (**nuevo**) con `details.orderIds` (los que chocan). Evita que dos clics creen dos tandas que
  compitan por el candado. El candado por pedido sigue siendo la garantía; esto es para que la pantalla diga algo claro.
- **`POST …/orders/{orderId}/siigo-invoice` (síncrono)**: se conserva sin cambios de contrato en la fase *expand*, porque el frontend
  publicado hoy lo usa y espera `200` con `SiigoInvoiceDto[]` (`useIssueSiigoInvoice`). Se **elimina** en la fase *contract*, cuando el
  frontend ya emite por tandas (ver «Rollout»). No se convierte en sitio a un 202: cambiaría la forma de la respuesta bajo el frontend
  viejo. Mientras exista sigue con 30 s (el timeout de 120 s llega en la misma entrega que lo elimina), así nunca supera el corte de
  Cloudflare. Ningún otro consumidor lo usa: lo despacha sólo `SiigoEndpoints` (el worker usa el comando, no la ruta).
- `SiigoInvoiceAvailabilityDto` suma **`queuedBatchId`** (`Guid?`, nuevo): la tanda sin terminar que tiene a ese pedido, para que la
  pantalla del pedido retome el seguimiento al recargar.

### 2. Estados del ítem de la tanda

`SiigoInvoiceBatchItemStatus` gana **`ToConfirm`** (nuevo; se guarda por nombre en `status varchar(16)`, sin `CHECK`: no hay restricción
que ampliar). Se agrega al final del enum.

| Estado | Significa | ¿Cerrado? |
| --- | --- | --- |
| `Pending` | espera turno | no |
| `Sending` | un worker lo tiene | no |
| `Invoiced` | la factura existe en Siigo | sí |
| `ToConfirm` (nuevo) | se mandó y no se sabe si Siigo la creó; la fila de la factura sigue viva con la misma clave | sí |
| `Failed` | no se facturó y se sabe (rechazo de datos, conexión, permiso, interrupción antes de mandar), o la del pedido existe y la de flete sigue `Pending` (ver abajo) | sí |
| `Skipped` | el pedido no estaba en condiciones; no se llamó a Siigo para crear | sí |

`ToConfirm` cierra el ítem (`IsSettled`): la tanda termina aunque queden por confirmar, porque ya no depende del worker sino de una persona.

El ítem guarda **`invoice_id`** (nuevo): la fila `Order` de `siigo.invoices` que produjo el intento. `SiigoBatchItemOutcome` pasa a llevar
el id de la factura en `Invoiced`, `Failed` (cuando hay fila) y el nuevo `ToConfirm(Guid InvoiceId, string? ErrorCode)`.
`SiigoBatchItemInvoker.Map` cambia: `Pending` o `Unknown` → `ToConfirm` (hoy `Failed`), y copia `orderMarkFailed` de la fila del
pedido. **Un reenvío que no se puede hacer** (conexión no disponible, permiso perdido o cualquier otro fallo sin fila) no termina
`Failed` si el pedido ya tiene una factura `Order` vigente `Unknown`, por confirmar o en envío (`IsBeingSent`): termina `ToConfirm` con
el `invoice_id` de esa fila y el código del fallo, porque la factura puede existir. Si esa fila ya existe en Siigo (`Created`, `Stamped`,
`StampRejected`) termina `Invoiced` con su número y su id, y `orderMarkFailed` salvo que una lectura confirme la marca (la misma regla
de la recuperación, §4). La excepción es un pedido ya facturado con la de flete todavía `Pending`: el reenvío era para mandarla, así que
el ítem queda `Failed` con `siigo.invoice.freight_pending`, porque `Invoiced` la dejaría sin mandar sin que nadie lo vea. Con una fila del pedido
que nunca salió y nadie está mandando queda `Failed`, como siempre (decidido el 2026-11-25).

**La misma regla vale cuando la emisión sí contestó, en la recuperación (§4) y en el estado efectivo** (decidido el 2026-11-25). Con
la fila `Order` en Siigo (`Created`, `Stamped`, `StampRejected`), el ítem sólo sale `Invoiced` si su de flete, cuando la tiene, también
existe allá. La de flete de una factura es la más nueva del pedido creada con ella o después:

| Fila de flete | El ítem se lee | `errorCode` |
| --- | --- | --- |
| no hay, o existe en Siigo | `Invoiced` | — |
| `Pending` que se está mandando | `Sending` | — |
| `Pending` (nunca salió, o salió sin respuesta) | `Failed` | `siigo.invoice.freight_pending` |
| `Rejected` o `Failed` (resuelta como inexistente) | `Failed` | `siigo.invoice.freight_not_created` |
| `Unknown` | `ToConfirm`, sin acciones | `siigo.invoice.freight_unconfirmed` |

Al cerrar el ítem sólo se mira la `Pending`: se guarda `Failed` con `freight_pending` y **sin `invoice_id`**. Los demás casos se calculan
al leer, en ítems guardados `ToConfirm` e `Invoiced`. Los códigos son propios para que la pantalla diga que la del pedido ya existe y
nadie la cree otra vez en Siigo; no hay columnas nuevas. Ten presente:

- Otra tanda del pedido manda **sólo la de flete** `Pending`, con su clave: la del pedido ya existe y no se vuelve a crear, y el pedido
  se vuelve a marcar. Pasadas las 24 h la de flete es `Unknown`: ya no se manda y se resuelve desde el pedido.
- Una de flete `Rejected` o `Failed` no se vuelve a armar desde QEP: se crea directamente en Siigo.
- El ítem guardado `Failed` no guarda `orderMarkFailed`, aunque el pedido tampoco haya quedado marcado. El aviso no se pierde: las filas
  del pedido lo calculan al leerse (§5) y el reintento repite la marca. La marca manual sigue bloqueada por la factura vigente.
- En el detalle de la tanda, un ítem guardado `ToConfirm` cuya factura ya existe calcula `orderMarkFailed` al leer, sin una tanda que
  tenga el pedido (decidido el 2026-11-25): el ítem no lo guarda.
- Ningún ítem leído así ofrece acciones (`canResend` y `canResolve` en `false`). El reintento y «Existe / No existe» de la de flete se
  hacen desde el pedido.

`SiigoInvoiceBatchItem` suma `MarkToConfirm(invoiceId, errorCode, now)` y guarda `invoice_id` también en `MarkInvoiced` y `MarkFailed`
cuando hay fila.

**El ítem sigue a la factura al leerse, no al escribirse.** El estado guardado es lo que pasó en la tanda; el estado que devuelve la API
es el **efectivo**, calculado al leer con la fila enlazada:

| Guardado | Fila de la factura ahora | Efectivo | Código / número |
| --- | --- | --- | --- |
| `ToConfirm` | `Pending` y `IsBeingSent` | `Sending` | — |
| `ToConfirm` | `Pending` o `Unknown` | `ToConfirm` | Si la fila no cambió desde que el ítem se cerró (`updated_at` de la fila ≤ el del ítem), el `ErrorCode` del ítem (dice por qué no salió el reenvío); si cambió después (otro reenvío, vencimiento, resolución), el de la fila. Si el que manda viene vacío, el otro (decidido el 2026-11-25) |
| `ToConfirm` | `Created`, `Stamped`, `StampRejected` | `Invoiced` | `Number` de la fila |
| `ToConfirm` | `Failed` (alguien declaró que no existe) | `Failed` | `siigo.invoice.resolved_missing` (nuevo) |
| `ToConfirm` | `Rejected` | `Failed` | `ErrorCode` de la fila |
| otro | cualquiera | el guardado | el guardado |

Así un reenvío desde otra tanda, o una resolución desde el pedido, se ve en todas las tandas que tocaron esa factura sin que
`IssueSiigoInvoiceHandler` ni `ResolveSiigoInvoiceHandler` escriban en ítems ajenos. Los contadores salen del estado efectivo.

### 3. Por confirmar en la fila de la factura

No se agrega un estado a `SiigoInvoiceStatus`. «Por confirmar» es `Pending` con **`unanswered_at`** (`UnansweredAt`, nuevo, nullable):
desde cuándo hay un intento cuyo resultado no se conoce. La transición a `Unknown` a las 24 h (`StaleAfter`) no cambia.

Reglas de dominio en `SiigoInvoice`:

- **`MarkUnanswered(string errorCode, string? errorDetail, now)`** (reemplaza a `MarkTransient` para envíos que salieron): fija
  `ErrorCode`/`ErrorDetail`, `UnansweredAt ??= now`, suelta `SendingSince`. Aplica a: `timeout`, `network`, `http_5xx`, `http_408`,
  `http_3xx`, `invalid_response`, `http_429` (ver decisiones) y **todo error de servicio de Siigo** (`IsServiceUnavailable`), aun en el
  primer intento: `documents_service` no dice si el documento quedó creado.
- `MarkTransient(null, now)` queda sólo para la de flete que espera a la del pedido (nunca salió): no toca `UnansweredAt`.
- **`MarkRejected` sólo si `UnansweredAt` es nulo** y el error no es de servicio. Con `UnansweredAt` puesto, un rechazo de datos en un
  reenvío **no** rechaza: la fila sigue `Pending` por confirmar, con el código traducido (`ErrorCodeOf`) y los códigos de Siigo en
  `ErrorDetail`. Siigo pudo haber creado la primera y contestar otra cosa al reenvío (es lo que pasó en producción).
- **Un envío sin cierre también cuenta.** Al reanudar (`ResumeAsync`), una fila `Pending` cuyo `SendingSince` sigue puesto (vencido) es un
  envío que murió sin registrar resultado: `UnansweredAt ??= now` antes de `BeginSend`. Cubre la caída del proceso y el corte por
  apagado. Salvo en la de flete mientras la del pedido no exista en Siigo: sólo sale después de ella, así que su reclamo vencido es de
  un envío que se cortó antes de llegarle y no queda por confirmar (decidido durante la implementación, 2026-11-24).
- `IsAwaitingConfirmation(now)` = `Pending` ∧ `UnansweredAt` no nulo ∧ no `IsBeingSent(now)`.

En `IssueSiigoInvoiceHandler.ApplyAsync`, la rama `Rejected` llama a la regla de arriba; la de flete hermana se descarta
(`order_rejected`) **sólo** si la del pedido quedó `Rejected` de verdad.

**Resolver sin esperar 24 h.** `ResolveSiigoInvoiceHandler` acepta la fila desde `Unknown` **o** desde `Pending` que no se esté mandando
(`!IsBeingSent`). Una `Pending` en envío sigue siendo 422 `siigo.invoice.state_invalid` (código existente). `ResolveExists` y
`ResolveMissing` cambian su guarda (`EnsureUnknown` → `EnsureResolvable(now)`). Al declarar `missing` la del pedido, la de flete hermana
sin resolver (`Pending` o `Unknown`) se descarta con `MarkFailed("siigo.invoice.order_rejected", …)`, **aunque tenga `UnansweredAt`**: la
de flete sólo sale después de que la del pedido existe, así que nunca salió (el relleno de la migración o el vencimiento a las 24 h pueden
haberla marcado igual). Si no, quedaría viva, bloqueando el pedido para siempre. Al declarar `exists`, o con la del pedido ya facturada,
la de flete no se toca y se resuelve o se reenvía aparte (decidido durante la implementación, 2026-11-24).

**La resolución guardada no falla por lo que viene después** (decidido el 2026-11-25). Al declarar `exists` la del pedido, marcar el
pedido va después del commit: si falla (anulado, marcado a mano, otra escritura, la base), la respuesta es la fila `Created` con
`orderMarkFailed` (§5), no un error, porque repetir la resolución sería 422 `state_invalid`. Con el pedido en una tanda sin terminar el
aviso no viaja (§5). Una cancelación de quien llama sí sube.

**La de flete espera a la del pedido** (decidido el 2026-11-25). Mientras la fila `Order` siga abierta (`Pending` o `Unknown`), la de
flete nunca salió: resolverla, con `exists` o con `missing`, es 422 `siigo.invoice.state_invalid`, y sus filas viajan con `canResolve` en
`false`. Con la del pedido facturada, rechazada o `Failed`, una de flete vigente se resuelve como cualquier otra.

**Espera de 10 minutos para `missing`.** `ResolveMissing` exige que hayan pasado **10 minutos desde el último intento sin respuesta**:
QEP corta a los 120 s, pero Siigo puede seguir procesando, y «No existe» es la única acción que libera el pedido para facturar con otra
clave. Para eso la fila guarda además **`last_unanswered_at`** (`LastUnansweredAt`, nuevo, nullable), que **cada** intento sin respuesta
actualiza (`UnansweredAt` conserva el primero). Antes de ese plazo responde 422 `siigo.invoice.resolve_too_early` (nuevo) con
`details.availableAt`. `ResolveExists` y «Reenviar» no esperan: ninguno puede duplicar. La espera vale también para una `Unknown`: una
`Pending` se puede reenviar poco antes de las 24 h y Siigo puede seguir procesándola. Por eso `ExpireIfStale` no expira una que se está
mandando y conserva el último intento sin respuesta (también un `SendingSince` vencido), desde el que se cuentan los 10 minutos
(decidido durante la implementación, 2026-11-24). La constante vive en el dominio (`SiigoInvoice.MissingResolutionDelay`).

**Reenviar** es crear una tanda con ese pedido (desde el pedido o desde la tanda): el handler reanuda la `Pending` con los mismos bytes y
la misma clave. Una `Unknown` no se reenvía (el handler la devuelve tal cual, como hoy).

**Consecuencia en `SiigoOpenInvoiceCheck`:** una factura por confirmar es viva (`Pending`), así que **bloquea la marca manual y la
anulación** del pedido hasta que alguien la resuelva. Esto **cambia el comportamiento de hoy**: un `documents_service` en el primer
intento dejaba la fila `Rejected` y el pedido libre para marcarlo a mano. Ahora, si la persona creó la factura directamente en Siigo
durante la caída, la registra con «resolver → existe» y su número, que además marca el pedido. El SQL del check no cambia.

### 4. Timeout de 120 s y leases

- `SiigoApiClient`: `HttpClient.Timeout` pasa a `Timeout.InfiniteTimeSpan` y cada llamada lleva su propio plazo con un
  `CancellationTokenSource` enlazado: **120 s para `POST v1/invoices`**, 30 s para todo lo demás (auth, listas, clientes, sellar, PDF).
  El `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` sigue traduciendo el plazo propio a
  `timeout`. Las constantes quedan en `SiigoApiClient` (`InvoiceCreateTimeout`, `DefaultTimeout`), sin configuración.
- Presupuesto peor caso de un pedido (cada llamada hasta su plazo): auth 30 + buscar cliente 30 + crear cliente 30 + auth 30 + factura
  120 + flete 120 = **360 s**. Un 401 se repite una vez pero contesta rápido; no se suma su plazo.
- `SiigoInvoice.SendLease`: 2 min → **8 min** (su comentario hoy dice «30 s más margen»: se corrige). Nace en `BuildNewAsync` con el `now` del inicio (antes de crear el cliente) y cubre
  también a la de flete, que sale después de la del pedido: tiene que superar los 360 s.
- `SiigoInvoiceBatchWorker.Lease`: 5 min → **10 min**, mayor que `SendLease`: un ítem no se recupera mientras su factura todavía se
  considera en envío.
- Una prueba amarra la relación `DefaultTimeout·4 + InvoiceCreateTimeout·2 < SendLease < Worker.Lease`.
- `MaxAttempts = 1` no cambia. **Un ítem recuperado (lease vencido) nunca llama a Siigo**: hoy se da por `Failed(siigo.unavailable)`;
  ahora toma el estado de la fila `Order` viva del pedido: `Pending`/`Unknown` → `ToConfirm` (código de la fila o
  `siigo.invoice.interrupted`, nuevo), creada → `Invoiced`, ninguna → `Failed(siigo.invoice.interrupted)` (no alcanzó a salir la factura).
  Con la del pedido creada y la de flete todavía `Pending` no sale `Invoiced` sino `Failed`, sin `invoice_id`, con
  `siigo.invoice.freight_pending` (§2; decidido el 2026-11-25). Ahí no se lee la marca del pedido.
- **Apagado y redespliegue.** Un envío de 120 s no cabe en los 30 s de `ShutdownTimeout`: el `stoppingToken` corta la llamada. En ese
  caso el worker hace un último guardado con `CancellationToken.None` (una sentencia corta, dentro del presupuesto) y cierra el ítem con la
  misma regla de recuperación, en vez de dejarlo `Sending` 10 minutos. Si ese guardado no alcanza, lo cierra la recuperación del lease.
  La fila de la factura queda con `SendingSince` puesto y el siguiente reenvío la trata como sin respuesta (§3).

### 5. Lista y detalle de tandas (BFF)

**Nueva:** `GET /api/v1/tenants/{tenantId}/siigo/invoice-batches?page&pageSize` (`siigo.invoice.issue`), paginada como
`call-failures` (`page` desde 1, `pageSize` 25 por defecto, tope 100, 422 `validation.failed` si no son enteros), de la más nueva a la más
vieja (`IX_invoice_batches_tenant_created` ya existe). Todas las tandas del tenant, no sólo las propias (igual que el `GET` por id de hoy).

```text
SiigoInvoiceBatchPageDto(Items: SiigoInvoiceBatchSummaryDto[], Total, Page, PageSize)
SiigoInvoiceBatchSummaryDto(Id, Status, Total, StatusCounts, CreatedAt, FinishedAt)
SiigoInvoiceBatchStatusCountDto(Status, Count)
```

**`StatusCounts` viaja completo y en orden fijo, incluso en cero**: `Pending, Sending, Invoiced, ToConfirm, Failed, Skipped`. Así la
pantalla no tiene que conocer el enum para dibujar el que falta (precedente `ReportStatusSliceDto`). Se calcula con el estado efectivo.

**Tanda terminada al leer** (precisión del 2026-11-25): `status` es `Finished` si la tanda quedó cerrada (`finished_at`) o si ningún
ítem guardado le falta por procesar, en el detalle y en la lista. Cubre la carrera de dos réplicas que resuelven a la vez los dos últimos
ítems y no la cierran. En ese caso `finishedAt` es la hora del último ítem resuelto (`updated_at`); la columna sigue vacía.

**Detalle enriquecido** (`GET …/invoice-batches/{batchId}`, y la respuesta 202 del `POST`): `SiigoInvoiceBatchDto` suma `StatusCounts`
y conserva `Invoiced`, `Skipped`, `Failed` (aditivo: el frontend publicado los lee). Cada ítem:

| Campo | Origen | Nota |
| --- | --- | --- |
| `orderId`, `position` | ítem | |
| `orderNumber`, `customerName` | Quotations, nuevo método del puerto | el número y el nombre que muestra `/orders`; `null` si el pedido no es del tenant |
| `status` | efectivo (§2) | incluye `ToConfirm` |
| `errorCode` | efectivo (§2) | código `siigo.*`, lo traduce la pantalla |
| `siigoErrors` | `ErrorDetail` de la fila enlazada | sólo códigos de Siigo (`documents_service`…); nunca mensajes. Vacío en un `ToConfirm` cuyo `errorCode` no es el de la fila (muestra el del ítem, §2): serían de otro intento (decidido el 2026-11-25) |
| `invoiceId`, `invoiceNumber` | fila enlazada / ítem | |
| `missing` | ítem | como hoy |
| `orderMarkFailed` | ítem (nuevo) | la factura existe pero el pedido no se pudo marcar; hoy sólo lo traía la respuesta del `POST` síncrono |
| `canResend` | calculado | efectivo `ToConfirm` ∧ fila `Pending` ∧ no en envío ∧ no vencida ∧ pedido sin ítem abierto en ninguna tanda |
| `canResolve` | calculado | efectivo `ToConfirm` ∧ (factura `Unknown` ∨ factura por confirmar), igual que en `SiigoInvoiceDto`; una `Pending` de más de 24 h cuenta como `Unknown` (decidido el 2026-11-25) |

Los `can*` existen porque la pantalla no ve `SendingSince` ni el reloj de 24 h: sin ellos tendría que adivinar.
`SiigoInvoiceDto` (lista del pedido) suma igual **`awaitingConfirmation`**, **`canResend`** y **`canResolve`** (aditivos): hoy el frontend
muestra toda `Pending` como «Enviando…».

**`orderMarkFailed` en las filas del pedido** (precisión de la etapa contract, 2026-11-25): sin la ruta síncrona, lo calculan al leer las
respuestas que devuelven esas filas (`GET …/siigo-invoice`, `/stamp` y `/resolve`). Va en `true` sólo en la fila `Order` vigente que existe
en Siigo (`Created`, `Stamped` o `StampRejected`, con número) cuando el pedido no está marcado como facturado con ese número y ninguna tanda
sin terminar lo tiene (ítem `Pending` o `Sending`): un envío en curso no muestra el aviso de paso. Si la marca no se puede leer (5 s de
plazo), avisa, igual que la recuperación de una tanda (§4). La de flete va en `false`. «Generar» es una tanda de ese pedido, que sólo
reintenta la marca.

**`canResend` con el pedido en una tanda** (precisión del 2026-11-25): mientras una tanda tenga el pedido (ítem `Pending` o `Sending`),
`canResend` va en `false` en las filas del pedido y en los ítems de cualquier tanda, porque una tanda nueva respondería 409
`order_in_progress`. Un ítem `ToConfirm` de una tanda que todavía está en curso, cuyo pedido ninguna tiene, lo conserva en `true`. Se
consulta una vez con los ids de los pedidos. `canResolve` no cambia.

El ítem y `SiigoInvoiceDto` suman también **`missingAvailableAt`** (nullable): la hora desde la cual se acepta «No existe»
(`LastUnansweredAt + 10 min`), `null` si ya se puede o si no aplica `canResolve`. La pantalla deshabilita «No existe» hasta esa hora.

Los nombres de pedido y cliente salen de un método nuevo en `ISiigoOrderSource` (por lotes, una consulta para la página de ítems),
implementado en `SiigoOrderSource` (Bootstrapper) sobre un método nuevo de `IExternalOrderInvoicing` (Quotations). No se guardan en
Siigo: un pedido puede cambiar de cliente y la tanda mostraría el viejo.

### 6. Fuera de alcance: sellar y PDF

Siguen síncronos con 30 s. **Riesgo:** si Siigo tarda más en sellar, la llamada se corta, la fila sigue `Created` y el 503
`siigo.unavailable` invita a repetir; repetir un sellado que sí llegó a la DIAN es cosa del flujo de sellado de Siigo, no de la clave de
creación. Si se ven timeouts de sellado en `siigo.call_failures` (`operation = InvoiceStamp`), el siguiente paso es llevar el sellado al
mismo worker.

## Contratos de la API (resumen)

| Ruta | Cambio | Fase |
| --- | --- | --- |
| `POST …/siigo/invoice-batches` | `payments` opcional (un solo pedido); 409 `order_in_progress`; respuesta con `statusCounts` e ítems enriquecidos | expand |
| `GET …/siigo/invoice-batches` | **nueva** | expand |
| `GET …/siigo/invoice-batches/{batchId}` | ítems enriquecidos, estado efectivo, `statusCounts` | expand |
| `GET …/orders/{orderId}/siigo-invoice/availability` | `queuedBatchId` | expand |
| `GET …/orders/{orderId}/siigo-invoice` | `awaitingConfirmation`, `canResend`, `canResolve` | expand |
| `POST …/orders/{orderId}/siigo-invoice/resolve` | acepta `Pending` que no se esté mandando | expand |
| `POST …/orders/{orderId}/siigo-invoice` | **se elimina** | contract |

## Datos y migraciones

**Expand** (una migración de Siigo, sólo columnas nullable o con default; el código viejo las ignora):

- `siigo.invoices.unanswered_at` y `siigo.invoices.last_unanswered_at`, ambas `timestamptz NULL`. El relleno de abajo pone las dos.
- `siigo.invoice_batch_items`: `invoice_id uuid NULL` (sin FK, como `order_id`), `payments_json text NULL`,
  `order_mark_failed boolean NOT NULL DEFAULT false`.
- Índice **único** parcial `IX_invoice_batch_items_tenant_order_open` sobre `(tenant_id, order_id)` con `status IN ('Pending', 'Sending')`,
  para `queuedBatchId` y como respaldo del 409: el handler consulta antes para armar `details.orderIds`, y si dos `POST` simultáneos pasan
  la consulta, el `23505` de **ese índice** (por nombre, no sólo por `SqlState`) se traduce en Infrastructure al mismo 409. Antes de crearlo,
  la migración verifica que no haya duplicados abiertos (hoy nada lo impide; si los hay, el owner decide cuál cerrar).
- Relleno, idempotente: `unanswered_at = updated_at` en filas `Pending` con `error_code` en (`siigo.invoice.timeout`,
  `siigo.invoice.network`, `siigo.invoice.rate_limited`, `siigo.invoice.unavailable`, `siigo.invoice.siigo_service`) o con
  `sending_since` no nulo.
- Ítems históricos `Failed` cuya factura quedó `Pending`/`Unknown` no se tocan: no tienen `invoice_id` y su estado efectivo es el
  guardado.

**Contract** (entrega posterior, sin migración): quitar la ruta síncrona y `IssueInvoiceAsync`/`SiigoIssueInvoiceRequest` de
`SiigoEndpoints`. No se borra ninguna columna.

## Códigos de error

Existentes que se usan: `siigo.invoice.siigo_service`, `.timeout`, `.network`, `.rate_limited`, `.unavailable`, `.state_invalid`,
`.payments_mismatch`, `siigo.invoice_batch.size`, `.orders`, `.not_found`, `.forbidden`, `validation.failed`.

Nuevos (en `SiigoErrorCodes`):

| Código | Tipo | Cuándo |
| --- | --- | --- |
| `siigo.invoice_batch.order_in_progress` | 409, `details.orderIds` | un pedido ya está en una tanda sin terminar |
| `siigo.invoice.interrupted` | código de fila/ítem | el envío empezó y el proceso se detuvo sin respuesta |
| `siigo.invoice.freight_pending` | código de ítem | la factura del pedido existe y la de flete sigue `Pending` (decidido el 2026-11-25) |
| `siigo.invoice.freight_not_created` | código de ítem efectivo | la factura del pedido existe y la de flete quedó `Rejected` o `Failed` |
| `siigo.invoice.freight_unconfirmed` | código de ítem efectivo | la factura del pedido existe y la de flete está `Unknown` |
| `siigo.invoice.resolved_missing` | código de ítem efectivo | una persona declaró que la factura no existe en Siigo |
| `siigo.invoice.resolve_too_early` | 422, `details.availableAt` | «No existe» antes de 10 min del último intento sin respuesta |

## Permisos

Sin permisos nuevos: todo cuelga de `siigo.invoice.issue` (política ya registrada). La lista nueva entra en el grupo `batches` existente y
su handler revalida tenant y permiso (`SiigoInvoiceSupport.AuthorizeAsync`), 403 nunca 404.

## Auditoría

- `siigo.invoice_batch.requested` suma `withPayments` (bool) al payload; sigue sin ids de factura.
- **Nueva** `siigo.invoice.unanswered`: la primera vez que una fila queda por confirmar (`UnansweredAt` pasa de nulo a valor), o cuando un
  rechazo se retiene por un intento anterior sin respuesta. Mismo payload de `SiigoInvoiceSupport.Audit` (pedido, tipo, estado, código).
  En el worker el actor es quien pidió la tanda (contexto delegado, como hoy).
- `siigo.invoice.resolved` suma `fromStatus` (`Pending` o `Unknown`).

## Estrategia de pruebas (TDD)

RED primero; donde el fallo esperado es de compilación, la evidencia es el build.

- **Dominio** (`Modules.Siigo.UnitTests`): `MarkUnanswered` fija `UnansweredAt` una sola vez; `MarkRejected` con `UnansweredAt` deja
  `Pending`; error de servicio en primer intento deja `Pending`; resolver desde `Pending` no en envío funciona y en envío lanza
  `state_invalid`; `missing` antes de 10 min del último intento lanza `resolve_too_early` y `exists` no espera; `ToConfirm` cierra el
  ítem; relación de plazos y leases.
- **Handler** (unitarias con `ISiigoApi` falso): timeout → reenvío con la misma clave contesta `documents_service` → la fila sigue
  `Pending` con `UnansweredAt` (el caso de producción); timeout → reenvío con rechazo de datos → no `Rejected`; rechazo de datos en primer
  intento → `Rejected` (no cambia); `ResumeAsync` con `SendingSince` vencido marca sin respuesta.
- **Invoker y worker:** `Pending` → `ToConfirm` con `invoiceId`; ítem recuperado toma el estado de la fila y no llama a Siigo; corte por
  `stoppingToken` cierra el ítem; `payments` llega al comando; con la del pedido creada y la de flete `Pending`, `Failed` en la emisión y
  en la recuperación (decidido el 2026-11-25), y el reintento manda sólo la de flete.
- **Cliente HTTP:** el plazo de `POST v1/invoices` es 120 s y el de lecturas 30 s (plazos inyectables en la prueba, sin esperar de verdad).
- **Integración** (filtradas por nombre mientras se itera): `POST` 202 con `payments`; 422 `payments` con dos pedidos; 409
  `order_in_progress`; lista paginada con `statusCounts` completo en cero; detalle con estado efectivo después de resolver y de reenviar
  desde otra tanda; `SiigoOpenInvoiceCheck` bloquea con una por confirmar; `SiigoNoLeakTests` cubre las rutas nuevas (ni `request_json`,
  ni clave, ni mensajes de Siigo).
- **Pruebas que cambian:** las que esperan `documents_service` → `Rejected` (commit `1d7ad3b8d`) y las que esperan
  `Failed(siigo.unavailable)` al recuperar un lease (`d5bd6014c`).

## Rollout

1. **Backend expand** (`develop` → producción): migración, reglas de por confirmar, `ToConfirm`, `payments` en la tanda, lista, detalle,
   resolver desde `Pending`, leases nuevos. Timeout sigue en 30 s y la ruta síncrona sigue viva.
   Ventana de riesgo: el frontend publicado dibuja una `Pending` como «Enviando…» y no conoce `ToConfirm`; por eso el paso 2 va el
   mismo día.
2. **Frontend:** emite por tandas (también desde el pedido, con `payments`), pantalla de tandas, por confirmar con reenviar y resolver.
   No puede ir antes del paso 1: el backend viejo ignora un `payments` desconocido en silencio y facturaría con la propuesta en vez de lo
   elegido.
3. **Backend contract:** quitar `POST …/siigo-invoice` síncrono y subir la creación de factura a 120 s, en la misma entrega.

Las facturas de producción que ya fallaron el 2026-11-24 no las arregla este cambio.

## Qué debe cubrir el spec del frontend

- «Generar factura» del pedido crea una tanda de uno con las formas de pago elegidas y sigue su avance (`queuedBatchId` al recargar);
  ya no recibe la factura en la respuesta.
- Pantalla de tandas (lista y detalle) tipo campaña: `statusCounts` completo, número de pedido, cliente, estado, causa traducida.
- Traducción de `ToConfirm`, `siigo.invoice.interrupted`, `siigo.invoice.resolved_missing`, `siigo.invoice_batch.order_in_progress`, y
  el texto de `siigo.invoice.siigo_service`: es una caída de Siigo, no de la plataforma.
- Por confirmar, en la tanda y en el pedido: explicar que hay que mirar en Siigo antes de reenviar; acciones «Reenviar» (`canResend`) y
  «Existe en Siigo (número)» / «No existe» (`canResolve`), con advertencia de que «No existe» libera el pedido y regenerar usa otra clave.
  «No existe» queda deshabilitada hasta `missingAvailableAt`, con la hora a la vista; traducir `siigo.invoice.resolve_too_early`.
- Dejar de mostrar «Enviando…» para una `Pending` con `awaitingConfirmation`.
- Traducción de `siigo.invoice.freight_pending`, `freight_not_created` y `freight_unconfirmed` (decidido el 2026-11-25): la factura
  del pedido ya existe en Siigo y no se vuelve a crear; dicen qué pasa con la de flete. En un ítem `ToConfirm` con
  `freight_unconfirmed` no va la instrucción de «Existe en Siigo», que no se ofrece ahí.
- «No existe» queda deshabilitada, con su explicación, cuando `canResolve` viene sin `canResolveMissing` (hay una tanda con el pedido).
- **«Reenviar todas las por confirmar»** en el detalle de la tanda: una acción manual que crea una tanda con los pedidos que tienen
  `canResend` (máximo 50), con el `POST` de tandas de siempre. No necesita cambios en el backend y no programa nada.
- En la pantalla la acción se llama **«Reintentar»** (por fila y para todas). Antes de enviar avisa: la clave sólo reconoce las facturas
  que creó QEP; una creada a mano en Siigo no la reconoce y reintentar la duplicaría. Esa se registra con «Existe en Siigo».
- Explicar que una factura por confirmar bloquea la marca manual y la anulación.

## Decisiones tomadas por el asistente (corrígelas)

- «Por confirmar» es `Pending` + `unanswered_at`, no un estado nuevo de `SiigoInvoiceStatus`: no cambia `SiigoOpenInvoiceCheck`, el
  índice único parcial ni el frontend que ya conoce los estados.
- El ítem sigue a la factura calculando al leer, no escribiendo en los ítems al resolver.
- La ruta síncrona se mantiene en expand y se borra en contract, en vez de transformarse a 202.
- 409 si el pedido ya está en una tanda abierta.
- Un ítem cortado sin factura enviada queda `Failed(interrupted)`, no vuelve a `Pending` (aunque sería seguro), para no tener ningún
  envío que nadie pidió.
- `SendLease` 8 min y `Lease` del worker 10 min.

## Decisiones del owner (2026-11-24)

- **«No existe en Siigo» espera 10 minutos** desde el último intento sin respuesta.
- **Envío cortado y sin reenviar** (D-1 del plan; precisa el §3): un `SendingSince` vencido cuenta como intento sin respuesta; se ve por
  confirmar y los 10 minutos se cuentan desde `SendingSince ?? LastUnansweredAt`.
- **Falla en `/auth` o sin configuración** (D-2): un timeout o falla de red al autenticarse deja la factura por confirmar;
  `partner_id_missing` y `auth_without_token` no, porque la factura nunca salió.
- **`canResolve`** (D-3) sólo en `Unknown` o por confirmar.
- **`siigoErrors`** (D-4) es una lista de códigos.
- **Timeout de creación: 120 s.**
- **Un 429 deja la factura por confirmar.**
- **La lista de tandas muestra todas**, también las de un solo pedido.
- **Consultar a Siigo si la factura existe** queda para un spec aparte; no entra aquí.
- **Las facturas que ya fallaron antes de este cambio** quedan fuera: el diseño sólo cubre las futuras.
