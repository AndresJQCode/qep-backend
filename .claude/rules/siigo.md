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
- **Una llamada a Siigo que falla deja su curl y la respuesta en `siigo.call_failures`** (spec 2026-11-21). Una fila por llamada fallida,
  lecturas y escrituras: el `curl.exe` de la petición (token y `access_key` enmascarados, `SiigoCurl.Build`) y lo que contestó Siigo, atada a la
  factura por la `Idempotency-Key` (creación) o el id de Siigo (sellado). **Una llamada buena no se guarda nunca:** la respuesta de `/auth`
  trae el token. Las escribe `SiigoApiClient` por `ISiigoCallFailureLog` (`SiigoCallFailureLog`, con su propio `DbContext`, sin propagar
  errores) y cada escritura borra de paso hasta 200 filas de más de 7 días, de todos los tenants (sin worker). Se leen con `siigo.calls.read`
  (sólo `admin` de fábrica; soporte recibe un rol personalizado) en `GET …/siigo/call-failures` (sin curl ni cuerpo) y `…/{id}` (con ellos):
  llevan datos del cliente. El log de requests (`platform/request-log`) conserva sólo la excepción, ligada por `TraceId`; no lleva el curl.
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
- **El PDF de la factura no se guarda: se pide a Siigo en cada descarga** (`GET …/siigo-invoice/{invoiceId}/pdf`, `GetSiigoInvoicePdfHandler`).
  Mismo permiso que generar (`siigo.invoice.issue`: de fábrica `admin` y `billing`), así que un rol con ese permiso también lo ve. Siigo lo
  entrega como `{"base64": "…"}` (campo tomado de la documentación del SDK, no probado contra una cuenta real: si falla, mirar ese campo
  primero). Sólo lo tiene una fila con id de Siigo y `Created`, `Stamped` o `StampRejected` (`SiigoInvoice.HasPdf`, que viaja como `hasPdf`);
  una resuelta a mano no tiene id y no se puede pedir.
