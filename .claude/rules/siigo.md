---
paths:
  - "src/Modules/Siigo/**"
  - "tests/Modules/Siigo/**"
  - "src/Bootstrapper/SiigoOpenInvoiceCheck.cs"
---

# Siigo (facturación de pedidos)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **`siigo.invoices` es el registro de un documento legal: no tiene purga y su `Down` no se corre.** Una fila `Created` o
  `Stamped` es una factura que existe en Siigo; borrarla aquí no la borra allá y deja el pedido sin rastro de ella.
- **«Enviar a la DIAN» (`send_to_dian`) nace apagado y prenderlo lo decide quien configura.** No hay empresa de pruebas de Siigo:
  cada factura sale contra la cuenta real, y una emitida a la DIAN no se borra. Con el interruptor apagado la factura queda creada y
  sin emitir, y una persona la revisa en Siigo. Una prueba o un seed nunca lo prende.
- **Las retenciones no van todas en el mismo lugar:** ReteFuente se manda en los `taxes` de **cada ítem** (después del IVA) y ReteICA
  en `retentions` de la factura. Sólo se manda el id del vínculo: el monto lo calcula Siigo. ReteIVA y autorretención no se mandan
  (decisión pendiente, V-7).
- **Nunca se reintenta con el total que propone Siigo.** Cuando los pagos no suman, Siigo rechaza con `invalid_total_payments` y el
  total esperado; ajustar los pagos a ese valor taparía justo la diferencia que la regla quiere mostrar. La fila queda `Rejected`
  (`siigo.invoice.totals_mismatch`) y se corrige la configuración del impuesto o la tasa.
- **`error_detail` guarda los códigos de error de Siigo, no sus mensajes.** Siigo repite en el mensaje los valores que rechaza
  (documento, dirección, nombre del cliente) y la fila se guarda y se devuelve en el `GET`. La excepción es `invalid_total_payments`,
  cuyo mensaje es el total esperado. Lo amarra `SiigoNoLeakTests` (precedente: encontró que el mensaje se guardaba completo).
- **`request_json` lleva datos del cliente** y sólo lo leen los handlers del módulo: ningún DTO ni `GET` lo devuelve.
- **El cliente HTTP `siigo` no tiene loggers (`RemoveAllLoggers`).** Con los defaults de .NET 10 su log ya redacta headers y queries,
  así que quitarlo no filtra un sentinela, y por eso `SiigoNoLeakTests` exige además que no haya **ninguna** línea de su categoría.
- **Una llamada a Siigo que falla deja su curl en el log de fallas (`platform/request-log`), sin interruptor.** El detalle de la fila lleva el
  `curl.exe` de la petición (token y `access_key` enmascarados) y la respuesta de Siigo, con datos del cliente: sólo lo lee quien tiene
  `RequestLogRead`. Dos caminos: la factura y el sellado no lanzan, así que `SiigoApiClient` escribe la fila por `ISiigoCallFailureLog`
  (`siigo.call_failed`, adaptador `SiigoCallFailureLog` en Bootstrapper, sólo en requests que escriben); las lecturas y el cliente nuevo
  lanzan, y el curl viaja en `Exception.Data["request.diagnostic"]` hasta `RequestFailureCapture.Describe`. Ese literal está en Siigo y en
  Api: cambiar uno compila y deja el detalle sin curl. Una respuesta buena nunca se guarda: la de `/auth` trae el token.
- **Una factura viva bloquea la marca manual y la anulación del pedido** (`IExternalInvoiceStatus`): todo estado menos `Rejected` y
  `Failed`. Es SQL crudo sobre `siigo.invoices` en el Bootstrapper (`SiigoOpenInvoiceCheck`): renombrar `status`, `order_id` o
  `tenant_id` compila y deja ambas operaciones en 500.
  **Con el módulo `siigo` apagado no bloquea nada** (sin módulo no hay pantalla para resolver una `Pending`); un tenant sin Siigo sigue marcando a mano como siempre.
- **Si la factura existe en Siigo pero el pedido no se puede marcar** (se anuló o alguien lo marcó a mano en medio), la fila queda
  `Created` y la respuesta lleva `orderMarkFailed`; el siguiente `POST` reintenta sólo la marca. Nunca deja el `POST` fallando.
- **La factura no lleva `additional_fields.purchase_order`.** Siigo lo rechaza con `document_settings` (`Params: ["additional_fields.purchase_order"]`) si el
  tipo de comprobante no tiene la orden de compra configurada, y Ritual Botánico factura sin él. El número del pedido ya va en `observations`.
- **Los pagos suman el total que calcula Siigo, no el de QEP.** QEP saca el IVA de un precio con IVA incluido como bruto − neto; Siigo lo redondea por
  línea desde la base ya redondeada (`ValorBase = round(cant × precio − descuento, 2)`, `IVA = round(base × % / 100, 2)`), así que su total puede
  quedar un centavo abajo y `invalid_total_payments` rechaza. `SiigoInvoiceBuilder` dimensiona los pagos con ese total (`computedOrderTotal`) y no con
  el de QEP; un saldo de hasta un peso (`Tolerance`) es redondeo y se suma al último comprobante en vez de salir como un segundo pago a crédito, y un
  comprobante que pasa por no más de un peso se recorta. Nada de esto lee el total del error de Siigo: sigue vigente la regla de arriba.

- **La facturación masiva es una tanda con worker, no un bucle en el request** (`siigo.invoice_batches` e `invoice_batch_items`,
  `SiigoInvoiceBatchWorker`). Cada pedido de la tanda lo factura el mismo `IssueSiigoInvoiceHandler` de siempre, así que el candado por
  pedido y la clave de idempotencia evitan duplicados al repetir o recuperar un lease. El worker corre **como quien pidió la tanda**:
  `SiigoBatchItemInvoker` (Bootstrapper) instala un `DelegatedExecutionContext` con los permisos que esa persona tiene al procesar *ese*
  pedido; si ya no tiene `siigo.invoice.issue`, el pedido falla con `siigo.invoice_batch.forbidden` y no se llama a Siigo. Es, junto al
  copiloto, el único que instala ese contexto (`CompositionRootTests`). El reclamo es un `UPDATE … SKIP LOCKED` de un pedido (lease de 5 min;
  más de 5 reclamos y se da por fallido). Un pedido que no está en condiciones sale `Skipped` con su código (y, si es `not_ready`, los códigos
  de lo que falta); Siigo sin responder o la conexión caída reintenta (`Retry`) con espera creciente; el resto de la tanda sigue. El timbre
  sale solo del `INSERT` en `siigo.invoice_batch_items` (`QueueDoorbellInterceptor`): si renombras la tabla, el timbre calla y sólo queda la
  red de seguridad de 30 s. Tope de 50 pedidos por tanda.
