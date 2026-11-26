# Siigo: liberar el pedido cuando la factura se anuló en Siigo — diseño (backend)

Fecha: 2026-11-25 · Repo: `qep-backend` · Rama: `feature/siigo-anular-factura` · Estado: **enfoque aprobado por el owner
(2026-11-25).** El detalle de este documento lo escribió el asistente; lo que el enfoque aprobado no decidía está marcado como
**[asistente]** y se lista en «Decisiones tomadas por el asistente». Nada está construido.
Frontend: spec hermano pendiente en `qep-frontend` (ver «Contrato para el frontend»).

## Intención

Cuando un pedido tiene una factura de Siigo viva (`Created`, `Stamped` o `StampRejected`), QEP no ofrece cómo deshacerla. Si la
persona la anula en Siigo, QEP no se entera: el pedido sigue facturado con el número viejo, no se puede volver a facturar y no se puede
anular.

- `Order.RevertInvoicing` rechaza un pedido marcado por un proveedor externo (`order.order.invoiced_externally`).
- `ResolveSiigoInvoiceHandler` sólo cubre `Pending` y `Unknown`.
- `IssueSiigoInvoiceHandler.PrepareAsync` retoma en vez de armar filas nuevas mientras haya **cualquier** fila viva (`IsLive`), y
  `IExternalInvoiceStatus` (`SiigoOpenInvoiceCheck`) bloquea la marca manual y la anulación del pedido con cualquier fila viva.

## Objetivos

1. Un endpoint por pedido, «Ya la anulé en Siigo», que verifica contra Siigo que cada factura viva del pedido está anulada (o borrada)
   y, sólo si todas lo están, las pasa a un estado nuevo `Annulled` y le quita al pedido la marca de facturado.
2. Todo o nada: con la factura del pedido y la de flete vivas, las dos tienen que estar anuladas en Siigo.
3. Ante cualquier duda, rechazar sin cambiar nada.
4. Que la pantalla sepa cuándo ofrecer la acción sin deducirlo (BFF).

## No objetivos

- Anular o borrar la factura desde QEP (`POST /v1/invoices/{id}/annul`, `DELETE /v1/invoices/{id}`): ver «Enfoques», B.
- Notas crédito. Una factura aprobada por la DIAN se corrige con nota crédito en Siigo; QEP no la modela.
- Detectar anulaciones solo (worker): ver «Enfoques», C.
- El botón y sus textos: van en el spec de `qep-frontend`.

## Anti-objetivos

- Liberar un pedido porque Siigo contestó algo que no se entiende. Liberar con la factura viva en Siigo deja que se facture otra vez y
  duplica un documento legal.
- Borrar filas de `siigo.invoices` (registro legal, `.claude/rules/siigo.md`).

## Enfoques

| | Enfoque | Por qué sí / por qué no |
| --- | --- | --- |
| **A (aprobado)** | La persona anula (o borra) la factura en Siigo Nube y pulsa «Ya la anulé en Siigo»; QEP lee la factura en Siigo y, si está anulada, libera el pedido. | Sólo lee en Siigo: no hay riesgo de anular por error una factura real. La anulación la hace quien sabe si corresponde, en la herramienta que Siigo soporta. |
| B | QEP anula por API (`POST /v1/invoices/{id}/annul`). | Las precondiciones del endpoint no están documentadas (qué estados acepta, qué contesta con una aprobada por la DIAN) y **no hay empresa de pruebas de Siigo**: cada prueba sería contra la cuenta real de un cliente. Descartado. |
| C | Un worker que recorre las facturas y detecta anulaciones. | Una llamada a Siigo por cada factura viva en cada pasada, contra un límite de 100 peticiones por minuto por empresa, para un evento raro que la persona ya sabe que hizo. Descartado. |

## Hechos externos y su confiabilidad

| Hecho | Fuente | Confiabilidad |
| --- | --- | --- |
| `GET /v1/invoices/{id}` devuelve `InvoiceViewModel` con el booleano opcional `annulled` («Represents whether the document has been cancelled or not») y `stamp.status` (`Draft`, `Accepted`, `Rejected`). | Documentación del SDK oficial: [InvoiceApi.md](https://github.com/SiigoDev/siigo_sdk_javascript/blob/master/docs/InvoiceApi.md), [InvoiceViewModel.md](https://github.com/SiigoDev/siigo_sdk_javascript/blob/master/docs/InvoiceViewModel.md) | Media: es documentación oficial del SDK, no del API REST, y **no está probada contra una cuenta real**. El campo es opcional: su ausencia no dice nada. |
| Existen `POST /v1/invoices/{id}/annul` y `DELETE /v1/invoices/{id}`. | Misma documentación del SDK | Media. No se usan aquí. |
| En Siigo Nube, una factura electrónica guardada o no aprobada por la DIAN se puede anular o borrar; una aprobada necesita nota crédito. | Artículos de soporte de Siigo: [anular](https://siigonube.portaldeclientes.siigo.com/anular-factura-electronica/), [borrar](https://siigonube.portaldeclientes.siigo.com/borrar-factura-electronica/) | Media: es ayuda al usuario, no contrato del API. |
| **Qué contesta `GET /v1/invoices/{id}` con una factura anulada o borrada** (status, cuerpo, código de error de «no existe»). | — | **Sin verificar.** No se inventa: el diseño falla cerrado ante cualquier respuesta que no reconozca y deja la evidencia en `siigo.call_failures`. Ver «Verificación pendiente». |

## Diseño

### 1. Estado `Annulled` de la factura

`SiigoInvoiceStatus` gana `Annulled`, **al final** (se guarda por nombre, `HasConversion<string>`, columna de 16 caracteres). Significa
«existió en Siigo y una persona la anuló o la borró allá; QEP lo verificó». No es vigente:

- `SiigoInvoice.IsLive` excluye `Rejected`, `Failed` **y** `Annulled`.
- El índice único parcial `IX_invoices_tenant_order_kind` pasa a `status NOT IN ('Rejected', 'Failed', 'Annulled')` (migración).
- `SiigoOpenInvoiceCheck` (SQL crudo, Bootstrapper) pasa a `status NOT IN ('Rejected', 'Failed', 'Annulled')`.
- Quien ya usa `IsLive` lo hereda sin cambio: `IssueSiigoInvoiceHandler.PrepareAsync` (sin filas vivas arma filas nuevas con otra
  clave), `ResolveSiigoInvoiceHandler`, `StampSiigoInvoiceHandler` (404 si no hay viva), `SiigoInvoiceSupport.Current`,
  `SiigoBatchItemInvoker.KeepWhatMayExistAsync` y `SiigoInvoiceBatchWorker.RecoverAsync`.
- Los `switch` sobre el estado que no pasan por `IsLive` se completan:
  - `SiigoInvoiceBatchItem.Effective` hoy lanza `InvalidOperationException` ante un estado desconocido. Un ítem `ToConfirm` cuya fila
    se resolvió como existente y después se anuló llegaría ahí: leer la tanda daría 500. **[asistente]** `Annulled` → ítem `Failed`
    con el `ErrorCode` de la fila (`siigo.invoice.annulled_in_siigo` o `siigo.invoice.deleted_in_siigo`): el pedido ya no está
    facturado por esa tanda. Vale igual para un ítem guardado `Invoiced` (si no, uno que se leía `Failed` por la de flete volvería a
    leerse facturado) y para el ítem viejo de un pedido que se volvió a facturar, que sigue enlazado a su fila anulada. Va sin número
    ni acciones, y con `orderMarkFailed` en `false` aunque lo hubiera guardado. Una de flete `Annulled` cuenta como `Rejected` o
    `Failed` (`siigo.invoice.freight_not_created`); como la anulación suelta todas las del pedido a la vez, no se espera con la del
    pedido viva.
  - `SiigoBatchItemInvoker.Map`: inalcanzable (una emisión nunca devuelve como vigente una `Annulled`), pero se agrega al brazo de
    `Rejected`/`Failed` para no caer en `ToConfirm`.
  - `SiigoOrderMark.IsMissingAsync` y `SiigoInvoice.HasPdf`/`CanStamp`/`IsOpen`/`CanResend` ya listan estados concretos: `Annulled`
    queda fuera de todos (sin PDF, sin sellar, sin reenviar). **[asistente]** No se ofrece el PDF de una anulada.

`SiigoInvoice.MarkAnnulled(bool deleted, DateTimeOffset now)`: sólo desde `Created`, `Stamped` o `StampRejected` con
`SiigoInvoiceId` (si no, `SiigoDomainException` `siigo.invoice.state_invalid`). Conserva id de Siigo, número, CUFE y total; pone
`ErrorCode` = `siigo.invoice.annulled_in_siigo` o `siigo.invoice.deleted_in_siigo`, `ErrorDetail` y `ExpectedPaymentsTotal` en `null`,
`SendingSince` en `null` y `UpdatedAt = now`. **[asistente]** El código viaja en la fila como motivo, igual que el de una `Failed`; el
rechazo de la DIAN de una `StampRejected` se pisa (queda en el estado de origen de la auditoría).

`SiigoInvoice.IsAnnulmentVerifiable`: `Created`, `Stamped` o `StampRejected` **y** `SiigoInvoiceId` no nulo.

### 2. Lectura de la factura en Siigo

`ISiigoApi.GetInvoiceAsync(SiigoCredentials, string siigoInvoiceId, CancellationToken)` → `SiigoInvoiceLookup`, una unión cerrada:

| Respuesta de Siigo | Resultado |
| --- | --- |
| 2xx, JSON con `id` igual al pedido y `annulled: true` | `Found(Annulled: true, StampStatus)` |
| 2xx, JSON con `id` igual y `annulled: false` | `Found(Annulled: false, StampStatus)` |
| 2xx sin `annulled` booleano, con otro `id` o ilegible | `Unrecognized` (y la llamada queda en `call_failures` como `invalid_response`) |
| 404 cuyo cuerpo trae errores de Siigo y **todos** sus códigos están en `SiigoApiErrors.InvoiceNotFoundCodes` | `NotFound` |
| Cualquier otro 4xx (incluido un 404 sin esos códigos) | `Unrecognized` |
| Timeout, red, 408, 429, 5xx, 3xx | lanza `ServiceUnavailableException` `siigo.unavailable` (503), como las demás lecturas |
| 400 con todos los errores de servicio (`SiigoApiErrors.IsServiceUnavailable`) | lanza `UpstreamServiceException` `siigo.invoice.siigo_service` (502), como las demás lecturas |
| Credenciales rechazadas | lanza `ResourceConflictException` `siigo.connection_unavailable` (409) |

- **`InvoiceNotFoundCodes` nace vacío [asistente].** Qué contesta Siigo con una borrada no está verificado, y un 404 pelado también es
  una ruta mal armada o una `BaseUrl` equivocada: tomarlo como «borrada» liberaría pedidos con la factura viva. Con la lista vacía, una
  factura borrada se rechaza como respuesta no reconocida y su 404 queda entero en `siigo.call_failures`; con esa evidencia se agrega
  el código (una línea y su prueba). Así «una borrada cuenta como anulada sólo si Siigo contesta claro que no existe» se cumple sin
  inventar el código. Es una decisión pendiente del owner aceptar que, hasta esa verificación, borrar en Siigo no libera el pedido.
- Operación nueva de `call_failures`: `SiigoCallOperation.InvoiceRead`, **al final** (se guarda por nombre y es contrato con la
  pantalla). Toda falla queda con su curl, ligada por el id de Siigo, incluido el 404 de una borrada reconocida **[asistente]**: es la
  evidencia de lo que contestó Siigo. Un 2xx bueno no se guarda (regla de `call_failures`).
- Plazo: `DefaultTimeout` (30 s), como toda lectura. No toca el presupuesto de leases (`SiigoTimeoutBudgetTests`): no corre en el
  worker.
- El cuerpo de la factura trae datos del cliente: no se loguea ni sale en una respuesta; sólo llega a `call_failures` cuando es una
  falla, como cualquier otra lectura.

### 3. El caso de uso

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice/annulment`, sin cuerpo, permiso `siigo.invoice.issue` (doble capa:
política en la ruta y `SiigoInvoiceSupport.AuthorizeAsync` en el handler; 403, nunca 404). Exige una membresía activa
(`FindActiveMembershipIdAsync`, 403 si no), que firma el historial del pedido.

`ConfirmSiigoAnnulmentHandler`, en este orden:

1. Tenant, permiso y módulo (403). Membresía activa (403).
2. Transacción de `SiigoDbContext` y `LockOrderAsync` (el mismo `pg_advisory_xact_lock` por pedido de emitir, sellar y resolver).
3. Filas del pedido; sin ninguna, 404 `siigo.invoice.not_found`. `ExpireIfStale` en cada una (perezosa, como en todas partes).
4. Si una tanda sin terminar tiene el pedido (`FindOpenBatchIdAsync`): 409 `siigo.invoice_batch.order_in_progress` con
   `details.orderIds`. Soltar las filas mientras el worker puede estar por mandar el pedido es la misma carrera que «No existe».
5. Filas vivas:
   - **Ninguna viva:** si la última fila `Order` es `Annulled` con número, es un **reintento** (paso 9). Si no, 422
     `siigo.invoice.state_invalid` (sólo hay rechazadas o descartadas: no hay nada que anular).
   - Si alguna viva no es verificable (`Pending`, `Unknown`, o creada sin `SiigoInvoiceId` porque se resolvió a mano): 422
     `siigo.invoice.annulment_unverifiable` con `details.kind`. Nada sale a Siigo.
6. Credenciales de la conexión de cada fila (`SiigoInvoiceSupport.CredentialsAsync`, sólo `Active`; 409 `siigo.connection_unavailable`).
7. `GetInvoiceAsync` de cada fila viva, la del pedido primero. Se cortan al primer resultado que no confirma:
   - `Found(Annulled: false)` → 422 `siigo.invoice.not_annulled` con `details.kind` y `details.number`.
   - `Found(Annulled: true)` con `StampStatus == "Accepted"` → 422 `siigo.invoice.annulment_unclear` **[asistente]**: Siigo documenta
     que una aprobada por la DIAN no se anula; las dos cosas a la vez son una respuesta contradictoria.
   - `Unrecognized` → 422 `siigo.invoice.annulment_unclear` con `details.kind` y `details.number`.
   - Las excepciones del cliente (503, 502, 409) suben tal cual.
8. Todas confirmadas: `MarkAnnulled` en cada fila viva (`deleted` según `NotFound`), auditoría `siigo.invoice.annulled` por fila
   (`SiigoInvoiceSupport.Audit` con `outcome` = `annulled` | `deleted` y `fromStatus`), `SaveChangesAsync` y commit. Hasta aquí, un
   error no deja nada escrito.
9. **Después del commit**, fuera de la transacción: `ISiigoOrderSource.UnmarkInvoicedAsync(tenantId, orderId, number, memberId)` con
   el número de la fila `Order` anulada. Si lanza, la anulación queda guardada, queda un warning con el tipo de la excepción y la
   respuesta lo avisa con `orderUnmarkFailed` (mismo patrón que `TryMarkAsync` de resolver).
10. Respuesta 200: las filas actuales del pedido (`SiigoInvoiceSupport.Current`), con los flags de la sección 5. Lo que se lee después
    del commit no tumba la respuesta (patrón de `SiigoSavedInvoice`).

Los 422 nuevos son una sola excepción, `SiigoAnnulmentRejectedException(code, kind, number?) : DomainException, IHasProblemExtensions`,
con `details = { kind, number }`; el status lo pone el mapeo central. **[asistente]** Una respuesta no reconocida es 422 y no 502 para
llevar `details.kind` (la pantalla dice cuál factura) y porque `UpstreamServiceException` es `sealed` y sin extensiones.

### 4. El pedido pierde la marca

Quotations gana `Order.RevertExternalInvoicing(string provider, string number, MemberId revertedBy, DateTimeOffset occurredAt)` →
`bool`:

- Marcado por ese proveedor **y** ese número: limpia `InvoicedAt`, `InvoicedBy`, `InvoiceProvider`, `InvoiceNumber` e
  `InvoiceExternalId`, toca `UpdatedAt` y sube `Version`. Devuelve `true`.
- Sin marca, o con otra (manual, otro número, otro proveedor): no hace nada y devuelve `false`. **[asistente]** No lanza: la anulación
  en Siigo ya está guardada, y una marca manual puesta en medio (el caso de `orderMarkFailed`) es una declaración de la persona que
  QEP no debe borrar; se revierte con la acción de siempre (`RevertInvoicing`, que sí acepta la manual).

`IExternalOrderInvoicing.RevertExternalInvoicingAsync(tenantId, orderId, provider, number, memberId)` → `bool` (lo implementa
`ExternalOrderInvoicingService`): con `true` escribe historial `InvoicingReverted` con
`OrderChangeSummary.ExternalInvoiceAnnulled(provider, number)` («Se anuló en Siigo la factura FV-1-123; el pedido quedó sin
facturar.») y audita `quotation.order.invoice_reverted` con `provider` y `number` en los detalles **[asistente]** (la pantalla de
auditoría ya conoce la acción; no se crea un tipo de historial nuevo). Reintenta una vez ante `RequestConcurrencyException`, como
`MarkInvoicedAsync`. El adaptador `SiigoOrderSource.UnmarkInvoicedAsync` pasa el proveedor `siigo`.

`Order.RevertInvoicing` no cambia: sigue rechazando un pedido de proveedor externo. El camino de Siigo es este endpoint.

### 5. Lo que ve la pantalla (BFF)

`SiigoInvoiceDto` suma al final:

- **`canConfirmAnnulment`**: si la pantalla ofrece «Ya la anulé en Siigo». Sólo en la fila `Order`, y en `true` cuando ninguna tanda
  sin terminar tiene el pedido **y** se cumple una de dos:
  - la fila es vigente, toda fila vigente del pedido es verificable (`IsAnnulmentVerifiable`) y ninguna es `Pending`/`Unknown`; o
  - la fila es `Annulled` y `orderUnmarkFailed` es `true` (reintento de la marca).
  La pantalla no puede deducirlo: no ve si la fila tiene id de Siigo, ni las tandas, ni el estado de la de flete en el mismo cálculo.
- **`orderUnmarkFailed`**: en la fila `Order` `Annulled` con número cuando el pedido todavía lleva la marca de Siigo con ese número
  (`ISiigoOrderSource.IsMarkedInvoicedAsync`, por `SiigoOrderMark.IsConfirmedAsync`: 5 s y, si la lectura falla, `false` y un
  warning). La de flete siempre en `false`.

Los calculan `GET …/siigo-invoice` y la respuesta de `…/annulment`. **[asistente]** Las respuestas de sellar y resolver (una sola fila)
los traen en `false`, como hoy `canSendWhatsApp`: la pantalla relee la lista. Una respuesta nueva con filas del pedido tiene que
calcularlos.

### 6. Concurrencia y orden de candados [asistente]

- Un solo candado: el advisory lock del pedido en la transacción de Siigo, que también toman emitir, sellar y resolver. Las llamadas a
  Siigo van dentro, como en `StampSiigoInvoiceHandler`: hasta tres llamadas de 30 s (autenticar y dos facturas) con el candado tomado.
  Una emisión de la tanda que llegue en medio espera y después ve las filas anuladas.
- La escritura del pedido (otro `DbContext`) va **después** del commit de Siigo y fuera del candado: nunca se toman los dos a la vez,
  así que no hay interbloqueo entre contextos.
- **Por qué Siigo primero y el pedido después.** Si el pedido se liberara primero y el commit de Siigo fallara, el pedido quedaría sin
  marca con la fila todavía `Created`, y la siguiente emisión (`EnsureOrderMarkedAsync`) lo volvería a marcar con el número anulado.
  Al revés, si falla la marca después del commit, las filas quedan `Annulled` y el pedido marcado: no se puede facturar ni anular, pero
  nada se duplica, `orderUnmarkFailed` lo dice y repetir la acción reintenta sólo la marca, sin llamar a Siigo.
- Repetir la acción es idempotente: con las filas ya anuladas y el pedido libre responde 200 con las filas; dos pulsaciones a la vez se
  serializan en el candado y la segunda cae en el reintento.
- Sellar en curso: termina primero; si la DIAN la aceptó, Siigo dirá que no está anulada (o la respuesta contradictoria) y se rechaza.
- Una tanda creada después de la consulta del paso 4 (crear una tanda no toma el candado) espera el candado: si el pedido sigue
  marcado, el ítem sale `Skipped` con `order.order.already_invoiced`; si ya está libre, factura de nuevo, que es lo que esa persona
  pidió.
- Pedido anulado en QEP con la factura viva (anulado entre crear la factura y marcarlo, el caso de `orderMarkFailed`): se acepta; las
  filas pasan a `Annulled` y el pedido no se toca (no tiene marca).

### 7. Rollout (expand/contract)

- **La migración es aditiva:** sólo cambia el filtro del índice parcial. Sin filas `Annulled`, el índice nuevo tiene las mismas filas
  que el viejo, así que el código viejo corre igual contra el esquema nuevo. No hay relleno de datos. El `Down` no se corre (registro
  legal).
- **El riesgo es de datos, no de esquema:** un pod viejo que lea una fila `Annulled` falla al convertir el enum (500 en esa pantalla
  de Siigo y en el worker para ese pedido), y su `SiigoOpenInvoiceCheck` la cuenta como viva (bloquea la marca manual y la anulación
  de ese pedido). Nada se corrompe y se corrige solo al terminar el rollout. **[asistente]** Se evita por orden de publicación: el
  botón vive en `qep-frontend` y se publica después de que el backend terminó su rollout, así que nadie escribe `Annulled` mientras
  hay pods viejos. No hace falta corte (`Recreate`).
- **Lo mismo pasa con `call_failures`:** un pod viejo no conoce la operación `InvoiceRead`, así que leer una fila que la tenga le hace
  fallar la lista de llamadas fallidas del tenant (500 en la página que la trae, que por venir de la más nueva a la más vieja suele ser
  la primera) y su detalle. Esa fila sólo la escribe la anulación, así que el orden de publicación también lo evita.
- **Devolver el backend no es gratis:** con alguna fila `Annulled` escrita, volver a la versión anterior deja en 500 las pantallas de
  Siigo de esos pedidos y las lecturas de las tandas que los traen, hasta volver a desplegar la versión nueva (su worker falla con
  esos pedidos y `SiigoOpenInvoiceCheck` los vuelve a contar como vivos). Las filas `InvoiceRead` de `call_failures` rompen la lista
  igual mientras existan (se borran pasados 7 días, con la siguiente escritura). Se corrige adelante; no se tocan filas a mano.

## Contratos de la API (resumen)

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `POST …/orders/{orderId}/siigo-invoice/annulment` (sin cuerpo) | `siigo.invoice.issue` | 200 `SiigoInvoiceDto[]` (filas actuales, la del pedido primero) |
| `GET …/orders/{orderId}/siigo-invoice` | `siigo.invoice.issue` | Igual que hoy, con `canConfirmAnnulment` y `orderUnmarkFailed` y el estado `Annulled` posible |

## Datos y migraciones

- Migración `AddSiigoInvoiceAnnulled` (Siigo): `DropIndex` + `CreateIndex` de `IX_invoices_tenant_order_kind` con el filtro nuevo. Sin
  columnas nuevas (`status` ya es `varchar(16)`).
- Ninguna migración en Quotations: las columnas de la marca ya son nullable.

## Códigos de error

| Código | Status | Cuándo | `details` |
| --- | --- | --- | --- |
| `siigo.invoice.not_found` (existe) | 404 | El pedido no tiene filas de Siigo | — |
| `siigo.invoice_batch.order_in_progress` (existe) | 409 | Una tanda sin terminar tiene el pedido | `orderIds` |
| `siigo.invoice.state_invalid` (existe) | 422 | Nada vivo y la última del pedido no es `Annulled` | — |
| `siigo.invoice.annulment_unverifiable` (nuevo) | 422 | Una viva `Pending`/`Unknown` o resuelta a mano sin id de Siigo | `kind` |
| `siigo.invoice.not_annulled` (nuevo) | 422 | Siigo dice `annulled: false` | `kind`, `number` |
| `siigo.invoice.annulment_unclear` (nuevo) | 422 | Respuesta no reconocida o contradictoria | `kind`, `number` |
| `siigo.connection_unavailable` (existe) | 409 | Conexión no operativa o credenciales rechazadas | — |
| `siigo.unavailable` (existe) | 503 | Siigo no contestó | — |
| `siigo.invoice.siigo_service` (existe) | 502 | Error de servicio de Siigo | — |

Códigos de fila (no son respuestas HTTP; viajan en `errorCode` de la fila y del ítem de tanda): `siigo.invoice.annulled_in_siigo` y
`siigo.invoice.deleted_in_siigo`.

## Permisos

`siigo.invoice.issue` (de fábrica `admin` y `billing`), el mismo de generar, sellar y resolver. No hay permiso nuevo ni política nueva.

## Auditoría

- `siigo.invoice.annulled` (nueva, Siigo), una por fila, atómica con el cambio: pedido, tipo, estado, número, código, `outcome`
  (`annulled` | `deleted`) y `fromStatus`. Nunca la petición ni datos del cliente.
- `quotation.order.invoice_reverted` (existe, Quotations), con `provider` y `number`, sólo si el pedido perdió la marca.

## Estrategia de pruebas (TDD)

- **Unitarias de dominio:** `MarkAnnulled` desde cada estado (permitido y prohibido), `IsLive`, `IsAnnulmentVerifiable`, `Effective`
  con una fila `Annulled`; `Order.RevertExternalInvoicing` con la misma marca, sin marca, manual y con otro número.
- **Unitarias del cliente** (`SiigoApiClientTests`, stub HTTP): cada fila de la tabla de la sección 2, incluido que un 404 sin código
  reconocido es `Unrecognized` y queda en `call_failures` como `InvoiceRead`.
- **Unitarias de Quotations:** `RevertExternalInvoicingAsync` escribe historial y auditoría sólo cuando libera.
- **Integración** (`SiigoInvoiceAnnulmentApiTests`, host entero y `FakeSiigoApi`): libera el pedido y se vuelve a facturar con otra
  clave; todo o nada con flete (una anulada y otra no: nada cambia); cada rechazo (no anulada, no reconocida, contradictoria, sin id,
  `Pending`, tanda abierta, Siigo caído) deja filas y pedido intactos; reintento de la marca; repetir es idempotente; la marca manual y
  la anulación del pedido quedan libres (`SiigoOpenInvoiceCheck` real); `canConfirmAnnulment` y `orderUnmarkFailed` en la lista.
- **Mapeo:** el filtro del índice y `TheModelHasNoChangesPendingAMigration`.
- **No fuga:** el cuerpo de la factura no aparece en logs ni respuestas (`SiigoNoLeakTests` sigue verde).

## Verificación pendiente (contra Siigo real, con el owner)

1. Qué contesta `GET /v1/invoices/{id}` con una factura **anulada**: confirmar `annulled: true` y el `stamp.status`.
2. Qué contesta con una factura **borrada**: status y código de error, para llenar `SiigoApiErrors.InvoiceNotFoundCodes`.
   La evidencia queda sola en `siigo.call_failures` (operación `InvoiceRead`) la primera vez que alguien lo intente.
3. Qué contesta con una factura con nota crédito (fuera de alcance, sólo para saber si `annulled` cambia).

## Contrato para el frontend

Lo que el spec de `qep-frontend` necesita:

- Acción «Ya la anulé en Siigo» en la tarjeta de la factura del pedido, visible sólo con `canConfirmAnnulment` en la fila `Order`.
  Antes de llamar, una confirmación que diga que primero hay que anular (o borrar) en Siigo Nube **todas** las facturas del pedido,
  incluida la de flete, y que una factura aprobada por la DIAN se corrige con nota crédito, que QEP no maneja.
- `POST …/siigo-invoice/annulment` sin cuerpo; 200 devuelve las filas actuales, que reemplazan las de la tarjeta, y conviene releer el
  pedido (pierde `invoiced`, `invoiceProvider` e `invoiceNumber`).
- Estado nuevo `Annulled` en `status` de la fila, con `errorCode` `siigo.invoice.annulled_in_siigo` o `siigo.invoice.deleted_in_siigo`.
- `orderUnmarkFailed` en `true`: la factura quedó anulada pero el pedido sigue facturado; la misma acción lo reintenta.
- Errores a traducir: `siigo.invoice.not_annulled`, `siigo.invoice.annulment_unclear` y `siigo.invoice.annulment_unverifiable` (con
  `details.kind`: `Order` o `Freight`, y `details.number` cuando lo hay), más los que ya traduce (`order_in_progress`,
  `connection_unavailable`, `unavailable`, `siigo_service`, `state_invalid`, `not_found`).
- Un ítem de tanda, también uno que se veía facturado, puede salir `Failed` con `siigo.invoice.annulled_in_siigo` /
  `deleted_in_siigo`, sin `invoiceNumber`, sin acciones y con `orderMarkFailed` en `false`; conserva su `invoiceId`.
- `call_failures`: operación nueva `InvoiceRead`. Auditoría: acción nueva `siigo.invoice.annulled`.
- Se publica **después** de que el backend terminó su rollout (sección 7).

## Decisiones tomadas por el asistente (corrígelas)

1. Un 404 de Siigo sólo cuenta como «borrada» con un código reconocido, y la lista nace vacía: hasta verificarlo, borrar en Siigo no
   libera el pedido (falla cerrado).
2. `annulled: true` con `stamp.status = Accepted` se rechaza como contradictorio.
3. Una respuesta no reconocida es 422 `siigo.invoice.annulment_unclear` (con `details`), no 502.
4. La de flete sigue la misma regla que la del pedido: si está `Pending`/`Unknown` o sin id, se rechaza todo hasta resolverla.
5. Siigo se guarda primero (con el candado) y el pedido se libera después; si falla la marca, `orderUnmarkFailed` y reintento sin
   llamar a Siigo.
6. Repetir con todo ya anulado y el pedido libre responde 200 (idempotente); sin nada vivo ni anulado, 422 `state_invalid`.
7. Una marca manual o de otro número no se toca: la fila pasa a `Annulled` y el pedido sigue marcado; pedido anulado en QEP se acepta.
8. Sólo con la conexión `Active` (como sellar y el PDF): una pausada no verifica.
9. Una fila `Annulled` pisa su código de error con el motivo y no ofrece PDF.
10. El 404 reconocido de una borrada se guarda en `call_failures` como evidencia.
11. El historial reutiliza `InvoicingReverted` con un resumen propio y la auditoría reutiliza `quotation.order.invoice_reverted`.
12. Rollout sin corte: el frontend se publica después del backend.
13. Ítem de tanda `ToConfirm` o `Invoiced` cuya fila terminó `Annulled` → `Failed` con el código de la fila, sin número, acciones ni
    aviso de marca.

## Decisiones pendientes del owner

1. Aceptar que, hasta verificar la respuesta real, una factura **borrada** en Siigo no libera el pedido (decisión 1), o decidir otra
   forma de reconocerla.
2. Facturas resueltas a mano como «existe» (sin id de Siigo): hoy no se pueden verificar y la acción las rechaza. ¿Se dejan así o se
   pide otra vía (por ejemplo, buscar por número)? No se diseña sin la respuesta.
3. Una factura `Stamped` que se corrigió con nota crédito en Siigo: hoy se rechaza (no anulada o contradictoria). Las notas crédito
   están fuera de alcance; queda registrado.
4. ¿Una conexión pausada debería poder verificar una anulación (decisión 8)?
