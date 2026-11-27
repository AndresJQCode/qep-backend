---
paths:
  - "src/Modules/Siigo/**"
  - "tests/Modules/Siigo/**"
  - "src/Bootstrapper/SiigoOpenInvoiceCheck.cs"
  - "src/Bootstrapper/SiigoInvoiceWhatsApp.cs"
  - "src/Bootstrapper/SiigoOrderSource.cs"
  - "src/Bootstrapper/SiigoGeographyLookup.cs"
  - "src/Bootstrapper/SiigoForeignCityInbox.cs"
---

# Siigo (facturación de pedidos)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **`siigo.settings.price_mode` decide cómo viaja el precio del ítem:** `BaseWithDiscount` (de fábrica) manda el precio de lista y `discount`; `DiscountedPrice`
  manda el precio ya descontado (`Subtotal / cantidad`) y `discount: 0`. El total no cambia entre modos, y un `priceMode` ausente en el `PUT …/document` conserva el guardado.
- **`siigo.invoices` es el registro de un documento legal: no tiene purga y su `Down` no se corre.** Una fila `Created` o
  `Stamped` es una factura que existe en Siigo; borrarla aquí no la borra allá y deja el pedido sin rastro de ella.
- **«Enviar a la DIAN» (`send_to_dian`) nace apagado y prenderlo lo decide quien configura.** No hay empresa de pruebas de Siigo:
  cada factura sale contra la cuenta real, y una emitida a la DIAN no se borra. Con el interruptor apagado la factura queda creada y
  sin emitir, y una persona la revisa en Siigo. Una prueba o un seed nunca lo prende.
- **En `POST /v1/customers`, `id_type` es texto (`"13"`), no `{ "code": "13" }`:** el objeto sólo viene en las respuestas. Con el objeto Siigo
  responde `invalid_type` en `id_type` y no se crea ningún cliente (producción, 2026-11-24 y 25). Una prueba unitaria que fija la forma
  del cuerpo no la valida contra Siigo: la documentación de la petición es la que manda.
- **El documento sale a Siigo sin separadores y el NIT sin dígito de verificación** (`SiigoCustomerMapper.Identification`; owner,
  2026-11-25; Siigo rechazó con 400 la búsqueda de una identificación con puntos). CC: sólo dígitos. NIT: se quita un guion con un solo
  dígito al final o, sin él, el décimo dígito de un número que empiece por 8 o 9 y cuadre con el algoritmo de la DIAN; un guion en otra
  parte no corta nada. CE y pasaporte: sólo letras y dígitos ASCII. Lo guardado en QEP no cambia, y un reenvío manda los bytes ya
  guardados, con la forma que tenían. Si Siigo ya tiene un cliente con otra forma del documento, la búsqueda no lo encuentra y la
  siguiente factura armada intenta crear otro.
- **Las retenciones no van todas en el mismo lugar:** ReteFuente se manda en los `taxes` de **cada ítem** (después del IVA) y ReteICA
  en `retentions` de la factura. Sólo se manda el id del vínculo: el monto lo calcula Siigo. ReteIVA y autorretención no se mandan
  (decisión pendiente, V-7).
- **El total que propone Siigo sólo se usa para absorber redondeo** (owner, 2026-11-25). Cuando los pagos no suman, Siigo rechaza con
  `invalid_total_payments` y el total esperado. Si es el único error, la fila no tiene envíos sin respuesta y la diferencia es de hasta
  un peso (`SiigoInvoiceBuilder.Tolerance`), `SiigoPaymentRounding` ajusta el último pago y la factura sale **una vez más** con **otra
  `Idempotency-Key`**, guardada antes de salir y auditada como `siigo.invoice.payments_adjusted`; la falla del primer envío queda en
  `call_failures` con la clave anterior. Motivo: Siigo redondea de una forma que no siempre se puede predecir (POB959, un centavo; POB916).
  Más de un peso es configuración del impuesto o la tasa: queda `Rejected` (`siigo.invoice.totals_mismatch`) y no se tapa. Un envío sin
  respuesta nunca se ajusta, porque cambiar la clave podría duplicar. El reenvío renueva el reclamo de la fila y de las que esperan su
  turno (`RenewClaim`).
- **`error_detail` guarda los códigos de error de Siigo, no sus mensajes.** Siigo repite en el mensaje los valores que rechaza
  (documento, dirección, nombre del cliente) y la fila se guarda y se devuelve en el `GET`. La excepción es `invalid_total_payments`,
  cuyo mensaje es el total esperado. Lo amarra `SiigoNoLeakTests` (precedente: encontró que el mensaje se guardaba completo).
- **Los errores de servicio de Siigo llegan como un 400, no como un 5xx** (catálogo «Manejo de errores» de su documentación; ejemplo real:
  `{"Status":400,"Errors":[{"Code":"documents_service","Message":"Your request could not be completed…"}]}`). `documents_service`,
  `payment_types_service`, `general_service`, `service_unavailable` y `request_timeout` son una caída de Siigo, no un dato mal enviado
  (`SiigoApiErrors.IsServiceUnavailable`: sólo si **todos** los errores son de esos; uno de otro tipo manda). La factura queda **por
  confirmar** (`Pending` con `unanswered_at`), aun en el primer intento, con `error_code = siigo.invoice.siigo_service`: `documents_service`
  no dice si el documento quedó creado (la pantalla debe decir que es un problema de Siigo, no de la plataforma). Al sellar, es un 503
  `siigo.unavailable` y la factura sigue `Created`; una lectura (listas, cliente, PDF) responde 502 con el mismo
  `siigo.invoice.siigo_service`. **No se reintenta solo** (owner, 2026-11-24): con la API caída el usuario puede crear la factura
  directamente en Siigo, y un reintento la duplicaría. `unhandled_error` **no** entra: es un fallo sin clasificar.
- **Una factura por confirmar nunca pasa a `Rejected`** (spec 2026-11-24 §3): ni por una caída ni por un rechazo de datos en un reenvío
  (Siigo pudo haber creado la primera). Cuenta como intento sin respuesta todo lo que pudo salir (timeout, red, 5xx, 429, respuesta
  ilegible, error de servicio y un `sending_since` vencido sin resultado); `partner_id_missing` y `auth_without_token` no
  (`SiigoInvoiceSupport.LeftQep`). La reenvía una persona con la misma clave o la resuelve («existe» o «no existe», esto último 10 minutos
  después del último intento sin respuesta); mientras tanto bloquea la marca manual y la anulación. La de flete que esperaba a la del
  pedido suelta su reclamo y no queda por confirmar. **Mientras la del pedido no exista en Siigo, la de flete nunca salió** (sólo se manda
  después de ella): al reanudar, su `sending_since` vencido no la marca sin respuesta, y declarar que la del pedido no existe la descarta
  aunque tenga `unanswered_at` o ya sea `Unknown`. Un camino nuevo que mande la de flete antes rompe las dos reglas y puede duplicar. La
  espera de «no existe» vale también para una `Unknown`: `ExpireIfStale` no expira una que se está mandando y conserva el último envío sin
  respuesta (incluido un `sending_since` vencido).
- **La de flete no se resuelve mientras la del pedido siga abierta** (`Pending` o `Unknown`): nunca salió, así que el endpoint responde
  422 `siigo.invoice.state_invalid` y sus filas llevan `canResolve` en `false` (`SiigoInvoiceSupport.IsFreightWaitingForTheOrder`; una
  respuesta nueva con filas del pedido tiene que cruzarlo). Con la del pedido facturada, rechazada o `Failed`, sí se resuelve.
- **Durante el despliegue de la facturación en segundo plano hay filas sin `unanswered_at`** (el pod anterior escribe después del relleno de
  `AddSiigoBackgroundInvoicing`). El dominio trata como sin respuesta, desde `updated_at`, una `Pending` con `timeout`, `network`,
  `rate_limited` o `siigo_service`; `unavailable` no, porque también lo deja un envío que nunca salió. **Terminado el rollout, el owner corre
  una vez más el `UPDATE` de relleno** (`BackfillSql` de esa migración) para cubrir las `unavailable` del pod anterior. Un camino nuevo que
  deje una `Pending` con uno de esos cuatro códigos sin `unanswered_at` la vuelve por confirmar.
- **`request_json` lleva datos del cliente** y sólo lo leen los handlers del módulo: ningún DTO ni `GET` lo devuelve.
- **El cliente HTTP `siigo` no tiene loggers (`RemoveAllLoggers`).** Con los defaults de .NET 10 su log ya redacta headers y queries,
  así que quitarlo no filtra un sentinela, y por eso `SiigoNoLeakTests` exige además que no haya **ninguna** línea de su categoría.
- **El cliente de Siigo no corta con `HttpClient.Timeout` (está en `Timeout.InfiniteTimeSpan`): cada llamada lleva su plazo**
  (`SiigoApiClient.SendAsync`, `CancellationTokenSource` enlazado que cubre el envío y la lectura del cuerpo). `POST v1/invoices` usa
  `InvoiceCreateTimeout` (120 s, lo que Siigo recomienda para crear documentos) y todo lo demás, incluido el `/auth` previo a la
  factura, `DefaultTimeout` (30 s); no se configuran. **Una petición nueva tiene que pasar un plazo:** sin él no la corta nada. El
  vencimiento del plazo es `timeout`; una cancelación de quien llama sube como `OperationCanceledException`. Si subes un plazo,
  `SiigoTimeoutBudgetTests` exige que cuatro llamadas generales más dos facturas (lo más que pasa sin renovar un reclamo) quepan en
  `SiigoInvoice.SendLease`, y que ése y cuatro generales más cuatro facturas (cada una reenviada por redondeo) quepan en el lease del
  worker, que no se renueva.
- **Generar una factura no tiene ruta síncrona: toda emisión es una tanda** (spec 2026-11-24, etapa contract). `POST …/siigo-invoice`
  ya no existe; la crean `POST …/siigo/invoice-batches` y el worker. Una petición HTTP no debe esperar a crear la factura: con 120 s la
  cortaría el proxy a mitad del envío. En las pruebas de integración se factura con `SiigoInvoicingHarness.IssueThroughBatchAsync`
  (tanda de uno y `DrainAsync`).
- **Una llamada a Siigo que falla deja su curl y la respuesta en `siigo.call_failures`** (spec 2026-11-21). Una fila por llamada fallida,
  lecturas y escrituras: el `curl.exe` de la petición (token y `access_key` enmascarados, `SiigoCurl.Build`) y lo que contestó Siigo, atada a la
  factura por la `Idempotency-Key` (creación) o el id de Siigo (sellado). **Una llamada buena no se guarda nunca:** la respuesta de `/auth`
  trae el token. Las escribe `SiigoApiClient` por `ISiigoCallFailureLog` (`SiigoCallFailureLog`, con su propio `DbContext`, sin propagar
  errores) y cada escritura borra de paso hasta 200 filas de más de 7 días, de todos los tenants (sin worker). Se leen con `siigo.calls.read`
  (de fábrica `admin` y `billing`, éste por decisión del owner del 2026-11-25; soporte recibe un rol personalizado) en `GET …/siigo/call-failures` (sin curl ni cuerpo) y `…/{id}` (con ellos):
  llevan datos del cliente. El log de requests (`platform/request-log`) conserva sólo la excepción, ligada por `TraceId`; no lleva el curl.
- **Una factura viva bloquea la marca manual y la anulación del pedido** (`IExternalInvoiceStatus`): todo estado menos `Rejected`,
  `Failed` y `Annulled`. Es SQL crudo sobre `siigo.invoices` en el Bootstrapper (`SiigoOpenInvoiceCheck`): renombrar `status`,
  `order_id` o `tenant_id` compila y deja ambas operaciones en 500. La misma lista está en el filtro del índice
  `IX_invoices_tenant_order_kind` y en `SiigoInvoice.IsLive`: un estado nuevo se decide en los tres.
  **Con el módulo `siigo` apagado no bloquea nada** (sin módulo no hay pantalla para resolver una `Pending`); un tenant sin Siigo sigue marcando a mano como siempre.
- **Soltar un pedido por una factura anulada en Siigo se verifica, es todo o nada y guarda Siigo primero** (spec 2026-11-25, `POST
  …/siigo-invoice/annulment`). QEP no anula allá: lee cada factura viva y sólo si **todas** (la del pedido y la de flete) contestan
  anuladas pasan a `Annulled`. Falla cerrado: lo que no se entiende, y una anulada que además dice `stamp.status = Accepted`, no cambia
  nada; liberar con la factura viva deja facturar otra vez y duplica un documento legal. **`SiigoApiErrors.InvoiceNotFoundCodes` está
  vacía a propósito:** ningún 404 cuenta como «borrada» (un 404 pelado también es una ruta o una `BaseUrl` mal armada) hasta tener la
  respuesta real de Siigo, que queda en `call_failures` como `InvoiceRead`; se llena sólo con esa evidencia y su prueba. Las filas se
  guardan con el candado del pedido y **la marca se quita después del commit**: al revés, un fallo al guardar dejaría el pedido libre
  con la fila `Created` y la siguiente emisión lo volvería a marcar. Si falla la marca, `orderUnmarkFailed` lo dice y repetir la acción
  la reintenta sin llamar a Siigo. Una marca manual o de otro número no se toca. Una respuesta nueva con filas del pedido tiene que
  calcular `canConfirmAnnulment` y `orderUnmarkFailed`.
- **Si la factura existe en Siigo pero el pedido no se puede marcar** (se anuló o alguien lo marcó a mano en medio), la fila queda
  `Created` y el ítem de la tanda lleva `orderMarkFailed`; la siguiente tanda del pedido reintenta sólo la marca. Nunca deja la
  emisión fallando.
- **`orderMarkFailed` de las filas del pedido se calcula al leer** (lista, sellar y resolver; no se guarda en `siigo.invoices`): sólo en
  la fila `Order` que existe en Siigo y tiene número, y nunca mientras una tanda tenga el pedido (ítem `Pending` o `Sending`), para que un
  envío en curso no parpadee un aviso. Lee la marca con `SiigoOrderMark`, el mismo de la recuperación de una tanda: 5 s de plazo y, si la
  lectura falla, avisa. Una respuesta nueva que devuelva filas del pedido tiene que calcularlo: sin él la pantalla no ofrece «Generar».
- **`canResend` va en `false` mientras una tanda tenga el pedido** (en las filas del pedido y en los ítems de las tandas): otra tanda
  respondería 409. Se pregunta una vez con todos los ids (`ListOpenOrderIdsAsync`), nunca por fila. No lo decide el dominio
  (`SiigoInvoice.CanResend` y `SiigoInvoiceBatchItem.Effective` no ven las otras tandas): una respuesta nueva que ofrezca reenviar tiene
  que cruzarlo. `canResolve` no lo mira.
- **Lo que va después del commit no puede tumbar la respuesta** (sellar y resolver, `SiigoSavedInvoice`): la operación ya está
  guardada —hasta un sellado ante la DIAN— y repetirla responde 422. Si la consulta de la tanda abierta falla, el pedido se trata como
  en cola (`canResend` en `false`, sin aviso de marca) y queda un warning. Una respuesta nueva armada tras guardar pasa por ahí. **Marcar
  el pedido al resolver «existe» tampoco** la tumba (decidido el 2026-11-25): si falla, la resolución queda guardada, queda un warning
  con el tipo de la excepción y la respuesta avisa con `orderMarkFailed`, salvo con el pedido en una tanda (o sin poder saberlo): ahí
  no ves el aviso, y es la tanda la que vuelve a marcar y lo dice en su ítem.
- **«No existe» se rechaza mientras una tanda tenga el pedido** (409 `siigo.invoice_batch.order_in_progress`, dentro de la transacción
  que bloquea el pedido): soltar la fila haría que el worker armara otra factura con otra clave en vez de reenviar la misma, y podría
  duplicarla. «Existe» sí se acepta. Crear una tanda no toma ese candado, así que una creada justo entre la consulta y el guardado se
  cuela.
- **Un ítem no sale `Failed` mientras su factura exista o pueda existir:** si el reenvío no se puede hacer (conexión, permiso),
  `SiigoBatchItemInvoker` mira la fila `Order` vigente. `Unknown`, por confirmar o en envío (reclamo sin vencer: otro envío en
  camino o uno cortado hace menos de 8 minutos) → `ToConfirm` con esa fila y el código del fallo. Ya facturada (`Created`, `Stamped`,
  `StampRejected`) → `Invoiced` con su número e id, y `orderMarkFailed` salvo que una lectura confirme la marca (`SiigoOrderMark`, como
  la recuperación de una tanda; decidido el 2026-11-25). **Salvo que la de flete siga `Pending`:** el reenvío era para mandarla, así que
  el ítem sale `Failed` con `siigo.invoice.freight_pending`; «facturado» la dejaría sin mandar sin que nadie lo vea.
- **Con la del pedido en Siigo, un ítem sólo sale `Invoiced` si su de flete, cuando la tiene, también está allá** (decidido el
  2026-11-25). Al cerrar (`SiigoBatchItemInvoker.Map` y `SiigoInvoiceBatchWorker.RecoverAsync`) sólo se mira la `Pending`: sale `Failed`
  sin `invoice_id` con `siigo.invoice.freight_pending`. **Lo demás se decide al leer** (`SiigoInvoiceBatchItem.Effective`), en ítems
  guardados `ToConfirm` e `Invoiced`: de flete `Pending` → `Sending` mientras se manda y si no `Failed` con `freight_pending`;
  `Rejected`, `Failed` o `Annulled` → `Failed` con `siigo.invoice.freight_not_created` (QEP no la vuelve a armar: se crea en Siigo); `Unknown` →
  `ToConfirm` con `siigo.invoice.freight_unconfirmed` y sin acciones (las de la tanda actúan sobre la factura del pedido). Los códigos
  son propios para que nadie cree otra vez en Siigo la del pedido; el motivo de la de flete se ve en el pedido. La de flete de una
  factura es la más nueva del pedido creada con ella o después (`SiigoInvoiceBatchViews.FreightsAsync`). Una lectura nueva de ítems
  tiene que pasársela a `Effective`: es un parámetro opcional y sin él responde `Invoiced`. Reintentar el pedido manda sólo la de flete
  `Pending` y vuelve a marcarlo.
- **Un ítem enlazado a una fila `Annulled` se lee `Failed` con el código de la fila, esté guardado `ToConfirm` o `Invoiced`** (spec
  2026-11-25 §1), sin número ni acciones y con `orderMarkFailed` en `false`. Se decide antes que la de flete, así que ninguno vuelve a
  leerse facturado, tampoco el ítem viejo de un pedido que se volvió a facturar.
- **El `orderMarkFailed` de un ítem guardado `ToConfirm` se calcula al leer el detalle de la tanda** (`SiigoOrderMark.IsMissingAsync`,
  sólo con su factura ya en Siigo y sin una tanda que tenga el pedido): el ítem no lo guarda. La lista de tandas no lo lleva. Un ítem
  cerrado `Failed` por la de flete tampoco lo guarda: el aviso lo calculan las filas del pedido.
- **El `errorCode` de un ítem `ToConfirm` depende de cuál se escribió de último** (`SiigoInvoiceBatchItem.Effective`, decidido el
  2026-11-25): mientras la fila no cambie desde que el ítem se cerró (`updated_at` de la fila ≤ el del ítem) ves el del ítem, que dice
  por qué no salió el reenvío; si la fila cambia después (otro reenvío, vencimiento, resolución), manda la fila. Si el que manda viene
  vacío, ves el otro. Por eso el ítem se guarda **después** de su fila y cerrado no se vuelve a escribir: si tocas el `updated_at` de un
  ítem cerrado, o escribes una fila sin mover el suyo, muestras el código equivocado. Cuando el que ves no es el de la fila,
  `siigoErrors` viaja vacío (`SiigoInvoiceBatchItemDto.From`): el `error_detail` de la fila es de otro intento.
- **«No existe» se ofrece con `canResolveMissing`, no con `canResolve`** (decidido el 2026-11-25): es `canResolve` y ninguna tanda con
  el pedido, en las filas y en los ítems. «Existe» sí se acepta con una tanda, así que `canResolve` no lo mira.
- **`canResolve` es el mismo en las filas del pedido y en los ítems de las tandas** (`SiigoInvoice.OffersResolution`: `Unknown`, una
  `Pending` vencida que todavía no se expiró, o por confirmar). El endpoint acepta más (`IsResolvable`: cualquier `Pending` que nadie
  esté mandando); no uses ése para dibujar la acción. `missingAvailableAt` sólo viaja con `canResolve`.
- **Un regalo viaja como obsequio** (spec 2026-11-21): el ítem lleva `price: 0`, `discount: 0`, `tax_base` (el precio de lista **sin IVA**,
  `ListUnitPrice × 100 / (100 + IVA)` a **2 decimales**: la documentación de Siigo da a `tax_base` «máximo 11 enteros y 2 decimales», y con 6
  Siigo rechazó con `invalid_amount` en `items[n].tax_base`; sin IVA es el precio de lista tal cual) y `taxpayer: "Company"`, con el IVA en `taxes` si lo
  hay y **sin retenciones** (no tiene base). No suma al `computedOrderTotal`: el total sin IVA del regalo es 0 y los pagos no cambian. Un regalo
  de una variante o un IVA sin vincular sale como faltante igual que una línea pagada. **No está probado contra una factura real de Siigo:**
  verifícalo con `send_to_dian` apagado antes de confiar en `tax_base` y `taxpayer`.
- **El teléfono del cliente nuevo se parte en `SiigoOrderSource`** (Bootstrapper; spec 2026-11-25) con `IPhoneNumberSplitter` de
  Customers, porque Siigo no lo referencia: región `CO` por defecto (país de la parte vacío o `CO`) y, si no, el país de la parte. Lo que no
  se puede partir viaja crudo y `SiigoCustomerMapper` deja sólo dígitos: si el número queda vacío o pasa de 10, **omite `phones`** y el
  cliente se crea igual; sin indicativo utilizable el objeto no lleva la propiedad `indicative`. **No está probado contra Siigo real.**
- **La factura no lleva `additional_fields.purchase_order`.** Siigo lo rechaza con `document_settings` (`Params: ["additional_fields.purchase_order"]`) si el
  tipo de comprobante no tiene la orden de compra configurada, y Ritual Botánico factura sin él. El número del pedido ya va en `observations`.
- **Los pagos suman el total que calcula Siigo, no el de QEP.** QEP saca el IVA de un precio con IVA incluido como bruto − neto; Siigo lo redondea por
  línea desde la base ya redondeada (`ValorBase = round(cant × precio − descuento, 2)`, `IVA = round(base × % / 100, 2)`), así que su total puede
  quedar un centavo abajo y `invalid_total_payments` rechaza. `SiigoInvoiceBuilder` dimensiona los pagos con ese total (`computedOrderTotal`) y no con
  el de QEP; un saldo de hasta un peso (`Tolerance`) es redondeo y se suma al último comprobante en vez de salir como un segundo pago a crédito, y un
  comprobante que pasa por no más de un peso se recorta. Nada de esto lee el total del error de Siigo: sigue vigente la regla de arriba.
  **La retención por ítem sigue la misma regla** (2026-11-24): ReteFuente viaja en los `taxes` de cada ítem y Siigo la redondea ítem por ítem, mientras
  QEP la saca sobre la base entera; en un pedido real de 8 líneas la diferencia fue de 2 centavos (QEP 20.595.927,31 contra 20.595.927,33 de Siigo) y
  Siigo rechazó con `invalid_total_payments`. `RetentionRoundingDelta` recalcula la retención por ítem con el porcentaje del pedido
  (`SiigoWithholdingInput.Percentage`) y corrige los pagos sólo si la diferencia es de redondeo (hasta `Tolerance`); más que eso es otra base u otro
  porcentaje y se deja rechazar a la vista. ReteICA va en la factura y no se corrige: no se conoce cómo la redondea Siigo.
- **Todo pago lleva `due_date` = fecha de la factura, comprobantes incluidos** (owner, 2026-11-25). Que una forma de pago maneje
  vencimiento se configura en Siigo, y sin el campo rechaza con `parameter_required` en `payments[n].due_date` (observado con un
  comprobante). Se envía siempre, sin leer la marca de `/payment-types`: la forma de pago puede cambiar en Siigo en cualquier momento.
  **Sin probar contra Siigo:** que una forma sin vencimiento acepte el campo (a la fecha ninguna factura aceptada en producción lo había
  llevado). La documentación además admite un solo pago cuando la forma maneja vencimiento; QEP no junta pagos, así que un pedido con
  dos pagos en una de ésas puede salir rechazado.
- **El PDF de la factura no se guarda: se pide a Siigo en cada descarga** (`GET …/siigo-invoice/{invoiceId}/pdf`, `GetSiigoInvoicePdfHandler`).
  Mismo permiso que generar (`siigo.invoice.issue`: de fábrica `admin` y `billing`), así que un rol con ese permiso también lo ve. Siigo lo
  entrega como `{"base64": "…"}` (campo tomado de la documentación del SDK, no probado contra una cuenta real: si falla, mirar ese campo
  primero). Sólo lo tiene una fila con id de Siigo y `Created`, `Stamped` o `StampRejected` (`SiigoInvoice.HasPdf`, que viaja como `hasPdf`);
  una resuelta a mano no tiene id y no se puede pedir.

- **Siigo nunca guarda el PDF de una factura ni lo loguea, y enviarla por WhatsApp es de Quotations** (spec 2026-11-21).
  `POST …/siigo-invoice/{invoiceId}/send-whatsapp` (`SendSiigoInvoiceWhatsAppHandler`) pide el PDF a Siigo por el mismo camino de la descarga
  (`SiigoInvoiceSupport.LoadPdfAsync`), lo entrega por el puerto `ISiigoInvoiceWhatsApp` y audita `siigo.invoice.whatsapp_sent` sólo si el
  envío salió. El canal, el teléfono y la plantilla los resuelve `IInvoiceWhatsAppDelivery` (Quotations; adaptador `SiigoInvoiceWhatsApp`, Bootstrapper):
  **`WhatsAppChannelSelection` es `internal` de Quotations y no se reimplementa en Siigo**, y `canSendWhatsApp` de la lista sale de ese mismo
  `CanSendAsync` (dos canales sin elección guardada → `false`). Ni el PDF, ni la URL pública, ni el teléfono entran a logs, `call_failures`,
  auditoría ni respuestas (`SiigoNoLeakTests`). La copia pública de Zenvia vive en `invoices/` del bucket público con clave aleatoria y su
  lifecycle de 1 día **se crea a mano** (`npx wrangler r2 bucket lifecycle add <bucket-publico> expire-invoices "invoices/" --expire-days 1`):
  sin esa regla las copias se quedan para siempre abiertas a quien tenga la URL. Por Meta Quotations no publica copia, pero Mensajería deja una copia **privada** del adjunto en el hilo (por tenant, misma retención que cualquier adjunto saliente); sólo Zenvia necesita la copia pública. Mandar una factura no recuerda la elección de canal del tenant.

- **La facturación masiva es una tanda con worker, no un bucle en el request** (`siigo.invoice_batches` e `invoice_batch_items`,
  `SiigoInvoiceBatchWorker`). Cada pedido de la tanda lo factura el mismo `IssueSiigoInvoiceHandler` de siempre, así que el candado por
  pedido y la clave de idempotencia evitan duplicados al repetir o recuperar un lease. El worker corre **como quien pidió la tanda**:
  `SiigoBatchItemInvoker` (Bootstrapper) instala un `DelegatedExecutionContext` con los permisos que esa persona tiene al procesar *ese*
  pedido; si ya no tiene `siigo.invoice.issue`, el pedido falla con `siigo.invoice_batch.forbidden` y no se llama a Siigo. Es, junto al
  copiloto, el único que instala ese contexto (`CompositionRootTests`). El reclamo es un `UPDATE … SKIP LOCKED` de un pedido (lease de
  **12 min**, mayor que `SiigoInvoice.SendLease` de 8 y que los 10 del peor pedido, con cada factura reenviada por redondeo: un ítem no se recupera mientras su factura se considera en envío). Un pedido que no
  está en condiciones sale `Skipped` con su código (y, si es `not_ready`, los códigos de lo que falta); **no se programan reintentos** (owner,
  2026-11-24; `MaxAttempts` = 1). Siigo sin responder deja el pedido **`ToConfirm`** con el `invoice_id` de su fila, que sigue `Pending`: la
  reenvía una persona (mismos bytes y clave) o la resuelve, y a las 24 h pasa a `Unknown`. Un rechazo de datos en un **primer** intento, o
  una conexión caída, lo dejan `Failed`; un rechazo de datos en el reenvío de una que ya tuvo un envío sin respuesta deja la fila `Pending`
  y el ítem sale `ToConfirm`. **Un ítem recuperado (lease vencido) o cortado por el apagado nunca llama a Siigo:** toma el estado de la fila `Order` viva del
  pedido (`Pending`/`Unknown` → `ToConfirm`, creada → `Invoiced` salvo con la de flete `Pending` —arriba—, ninguna → `Failed` con
  `siigo.invoice.interrupted`). Una creada que sale facturada lleva `orderMarkFailed` salvo que el pedido ya lleve la marca de ese número: la recuperación sólo lo lee (5 s de plazo; si la lectura falla,
  también avisa) y reenviar es lo que lo marca. El corte por apagado guarda ese resultado con `CancellationToken.None` y relanza la
  cancelación. `ToConfirm` cierra el ítem: al leerlo, su estado sigue a la factura (`SiigoInvoiceBatchItem.Effective`), así que un reenvío
  desde otra tanda o una resolución desde el pedido se ven sin escribir en el ítem. Una tanda de **un** pedido puede llevar las formas de
  pago elegidas (`payments_json`). Un pedido que ya está en un ítem `Pending` o `Sending` es 409 `siigo.invoice_batch.order_in_progress` con
  `details.orderIds` (la consulta antes de guardar y, en la carrera, el índice `IX_invoice_batch_items_tenant_order_open`). El resto de la
  tanda sigue. **`finished_at` puede quedar vacío** si dos réplicas resuelven a la vez los dos últimos pedidos: al leer, la tanda es
  `Finished` si está cerrada o si ningún pedido guardado le falta, con la hora del último resuelto
  (`SiigoInvoiceBatch.ReadFinishedAt`); quien filtre por `finished_at` en SQL no ve esas. El timbre sale solo del `INSERT` en `siigo.invoice_batch_items` (`QueueDoorbellInterceptor`): si renombras la tabla, el timbre
  calla y sólo queda la red de seguridad de 30 s. Tope de 50 pedidos por tanda.

- **`qep_siigo_call_failures_total{operation,reason}` no lleva tenant, conexión ni ruta** (cardinalidad): el tenant se busca en `siigo.call_failures`
  o por `TraceId`. Lo suma `SiigoApiClient.RecordAsync` **antes** de la guarda de captura, así que cuentan también las fallas sin curl
  (`partner_id_missing`). Un motivo nuevo que alimente `RecordAsync` aparece solo; uno que deba sembrarse en cero para que su alerta dispare
  desde el primer evento (`increase()` toma la primera muestra como base) entra en la lista fija de `SiigoMetrics`. El log del cliente HTTP
  sigue apagado: contar no agrega líneas de log (`SiigoNoLeakTests`).

- **Ciudades del exterior: resolver es lectura, anotar es escritura, y sólo la vista previa y la emisión escriben** (spec 2026-11-26).
  `SiigoOrderSource` resuelve con `ISiigoGeographyLookup.FindForeignCodesAsync` (sólo lee) y deja el país de Siigo (`Us`), el
  departamento y la ciudad, o un `SiigoUnlinkedCity` con el faltante `customer_city_unlinked` (sin `reference` ni `label`: el texto es
  del cliente y no se imprime). Anotar en la bandeja del operador es `ISiigoForeignCityInbox`, en otra clase, y lo llaman **sólo**
  `PreviewSiigoInvoiceHandler` y `IssueSiigoInvoiceHandler.BuildNewAsync` (a través de `SiigoForeignCityAnnotation`). Por eso la vista
  previa **ya no es de sólo lectura**: escribe en Geography. `SiigoInvoiceAvailabilityHandler` no recibe el puerto: si se lo das, mirar
  la pantalla del pedido llena la bandeja con cada pedido visto. Un fallo al anotar deja un warning con **el tipo** de la excepción
  (nunca el texto de la ciudad) y la facturación sigue; la anotación usa el `DbContext` de Geography, así que no entra en la
  transacción de Siigo. `ResumeAsync` no carga el pedido ni anota. Un país que no está en el catálogo se factura como
  `customer_city_unlinked` pero no se anota (`Reportable: false`). Lo amarran `SiigoForeignCityAnnotationTests`,
  `SiigoOrderSourceTests` y `SiigoForeignCityInvoiceTests` (incluida `TheAvailabilityCheckNeverAnnotates`).
- **`id_type` 50 (Tax ID) y 42 (NIF y NIE) no se probaron contra un Siigo real y falta que quien lleva la contabilidad los confirme.**
  El documento de NIF, NIE y Tax ID viaja como el pasaporte (sólo letras y dígitos ASCII). Antes de emitir a la DIAN, factura una de
  prueba con `send_to_dian` apagado y revisa en Siigo el país, el departamento, la ciudad y el teléfono del cliente creado. **Nunca
  prendas `send_to_dian` para probar esto.** Ninguna prueba llama a Siigo: `FakeSiigoApi` sólo comprueba lo que QEP manda.
