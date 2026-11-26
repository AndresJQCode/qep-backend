# Orden de compra por correo al proveedor — diseño (backend)

Fecha: 2026-11-18. Estado: pendiente de aprobación del owner. Alcance confirmado por el owner: **todo proveedor, externo e
interno (vinculado)**. Spec del frontend: `qep-frontend/docs/superpowers/specs/2026-11-18-orden-de-compra-por-correo-design.md`.

## Intención

Hoy `POST …/purchases/orders/{id}/send` sólo congela la orden (`Draft → Sent`); el diálogo del frontend dice «Descarga el PDF para
mandársela al proveedor». Al enviar, el proveedor debe recibir por correo la información del pedido, con el PDF adjunto.

## Hechos del código (verificados)

- `TransitionPurchaseOrderHandler` resuelve `send` para externas (`order.Send`) y vinculadas (`wholesaleOrders.SubmitAsync` +
  `order.SendToWholesaler`), y guarda con `unitOfWork.SaveChangesAsync`. No manda correo.
- El proveedor tiene correo propio: `Supplier.Contact.Email` (`SupplierContactInfo`, opcional, validado, máx. 254). Compras sólo ve
  `PurchasesSupplierRef` (sin correo) a través de `IPurchasesSupplierLookup`, adaptador en `Bootstrapper/PurchasesSupplierLookup.cs`.
- Correo: `IEmailChannel.SendAsync(EmailMessage)` en Notifications (`EmailMessage` ya admite `Attachments`). Soporte lo consume desde
  Bootstrapper (`SupportOpeningEmailSender`) con plantilla en Notifications: ese es el patrón a calcar.
- PDF: `GetPurchaseOrderPdfHandler` arma el documento con `PurchaseOrderPdfMapper` + `IPurchaseOrderPdfRenderer`.

## Objetivos

- Al enviar la orden (externa o vinculada), salir un correo al correo del proveedor con el resumen del pedido y el PDF adjunto.
- Que un fallo del correo **no deshaga el envío**: la orden ya quedó en `Sent` (y, si es vinculada, ya existe el pedido en el
  mayorista). El resultado queda visible y se puede **reenviar**.

## No objetivos

- Reply-To al tenant, copia al remitente o varios destinatarios: `EmailMessage` no tiene esos campos. Decisión pendiente del owner.
- Usar los contactos del proveedor (`Supplier.Contacts[].Email`) como destino: v1 usa sólo el correo del proveedor. Decisión pendiente.
- Correo al recibir, confirmar o cancelar.
- Cola con reintentos automáticos (outbox): v1 es un intento síncrono, calco de Soporte.

## Diseño

- **Destino:** `PurchasesSupplierRef` gana `Email` (`string?`, el de `Supplier.Contact.Email`); `PurchasesSupplierLookup` lo proyecta.
- **Puerto:** `IPurchaseOrderEmail` en `Modules.Purchases.Application`:
  `Task<PurchaseOrderEmailResult> SendAsync(PurchaseOrderEmailRequest, CancellationToken)`, con
  `PurchaseOrderEmailRequest(string ToAddress, PurchaseOrderPdfDocument Document, byte[] Pdf, string FileName)` y resultado
  `Sent | Failed`. Adaptador `PurchaseOrderEmailSender` en Bootstrapper sobre `IEmailChannel` y una plantilla nueva
  `PurchaseOrderEmailTemplate` en Notifications.Application. Cualquier excepción del canal es `Failed`; se loguea sólo el tipo de la
  excepción, nunca la dirección ni el contenido.
- **Contenido:** asunto «Orden de compra {número} de {tenant}»; cuerpo (HTML y texto) con número, fecha, proveedor, moneda, tabla
  de líneas (código, nombre, cantidad, costo, subtotal), subtotal, IVA, total y notas; adjunto `{número}.pdf` (`application/pdf`).
  Todo sale de `PurchaseOrderPdfDocument`, que ya trae los valores formateados con el formato regional del tenant: el correo no
  calcula nada.
- **Cuándo:** en `TransitionPurchaseOrderHandler`, **después** de `SaveChangesAsync` y sólo para `Send`. Pasos: leer el correo del
  proveedor; si no hay, resultado `NoRecipient` y no se llama al canal; si hay, armar PDF (misma ruta que `GetPurchaseOrderPdfHandler`,
  extraída a un servicio compartido `PurchaseOrderDocumentBuilder` para no duplicar) y enviar.
- **Persistencia del resultado** (Expand/contract, columnas nuevas nulables en `purchases.purchase_orders`): `last_email_status`
  (texto: `Sent | Failed | NoRecipient`), `last_email_to` (texto 254), `last_email_at` (timestamptz). Se escriben en un segundo
  `SaveChanges` tras el intento; si ese guardado falla, la respuesta igual lleva el resultado y el detalle posterior mostrará «sin
  intento». Un pod viejo ignora las columnas.
- **Detalle (`PurchaseOrderDetailDto`)** gana, al final, `Email: PurchaseOrderEmailDto?` =
  `{ status, to, at, recipient }` donde `recipient` es el correo actual del proveedor (para que el diálogo de envío lo muestre
  antes de enviar) y `status/to/at` son `null` hasta el primer intento.
- **Reenviar:** `POST /api/v1/tenants/{tenantId}/purchases/orders/{orderId}/resend-email`, sin cuerpo ni `If-Match`,
  `PurchasesPermissions.OrderManage`, responde el detalle. Estados permitidos: `Sent`, `Confirmed`, `Received`; `Draft` y
  `Cancelled` → 409 `purchases.order.email_not_allowed`. Proveedor sin correo → 422 `purchases.order.supplier_email_missing`. Fallo del
  canal → 200 con `email.status = Failed` (no 5xx: el resultado es dato, como en Soporte). `AllowedActions` suma `resend_email` en esos
  estados con `manage`.
- **Proveedor inactivo:** reenviar no lo exige (el envío ya lo verificó); sí lo exige `send`, como hoy.
- **Auditoría:** acciones nuevas `PurchasesAuditActions.EmailSent` (`purchases.order.email_sent`) y `EmailFailed`
  (`purchases.order.email_failed`), con `NoRecipient` sin evento. Sin destinatario en el payload.
- **Seguridad:** el destino lo da el proveedor guardado, nunca la petición. El PDF y el correo no se loguean.

## Anti-objetivos

- Que el `send` falle o se revierta por un fallo de correo.
- Que `resend-email` mande a un destino distinto al del proveedor.
- Duplicar el armado del PDF.

## Pruebas

- Unitarias del handler: con correo → llama al puerto una vez, con el destino y el adjunto, y guarda `Sent`; sin correo →
  `NoRecipient`, no llama al puerto; puerto en `Failed` → el estado de la orden sigue `Sent` y se guarda `Failed`; vinculada y externa
  mandan correo; `receive` y `cancel` no mandan.
- Unitarias de la plantilla: asunto, adjunto, texto y HTML escapado (nombre de proveedor o nota con `<`).
- Integración: `POST …/send` → 200, `email.status = Sent` con el adaptador falso; `POST …/resend-email` 200 en `Sent`, 409 en
  `Draft`/`Cancelled`, 422 sin correo, 403 sin `manage`; ArchitectureTests intactos.

## Orden de despliegue

Backend primero (los campos son aditivos); el frontend lee `email` ausente como backend viejo.

## Para quien construye

Nombre de la tabla/columna real en el `DbContext` de Purchases y la migración; ubicación de `PurchaseOrderDocumentBuilder`;
si la plantilla va con el layout común de Notifications.
