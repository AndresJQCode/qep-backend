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
- **Una factura viva bloquea la marca manual y la anulación del pedido** (`IExternalInvoiceStatus`): todo estado menos `Rejected` y
  `Failed`. Es SQL crudo sobre `siigo.invoices` en el Bootstrapper (`SiigoOpenInvoiceCheck`): renombrar `status`, `order_id` o
  `tenant_id` compila y deja ambas operaciones en 500.
  **Con el módulo `siigo` apagado no bloquea nada** (sin módulo no hay pantalla para resolver una `Pending`); un tenant sin Siigo sigue marcando a mano como siempre.
- **Si la factura existe en Siigo pero el pedido no se puede marcar** (se anuló o alguien lo marcó a mano en medio), la fila queda
  `Created` y la respuesta lleva `orderMarkFailed`; el siguiente `POST` reintenta sólo la marca. Nunca deja el `POST` fallando.
