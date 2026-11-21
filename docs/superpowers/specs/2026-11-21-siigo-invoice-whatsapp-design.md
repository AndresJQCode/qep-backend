# Enviar la factura de Siigo por WhatsApp — diseño

Fecha: 2026-11-21. Estado: pendiente de aprobación del owner.

## Intención

Un cliente pide que le reenviemos su factura porque no le llegó por correo. Hoy el PDF de la factura sólo se descarga
(`GET …/orders/{orderId}/siigo-invoice/{invoiceId}/pdf`). Se agrega un envío por WhatsApp desde el detalle del pedido, igual que se
envían hoy la cotización y el pedido.

## Metas / no metas

- **Meta:** botón en el detalle del pedido que manda el PDF de **una** factura de Siigo al WhatsApp del cliente del pedido, por el canal
  del tenant (Meta o Zenvia).
- **No meta:** envío automático al facturar, envío desde la facturación masiva, envío por correo, guardar el PDF de la factura, elegir
  un destinatario distinto del cliente del pedido.
- **Anti-meta:** que la factura quede en un enlace público de larga vida o adivinable.

## Restricciones verificadas

- El PDF vive en Siigo y no se guarda (`GetSiigoInvoicePdfHandler`). Sólo tiene PDF una fila con `HasPdf` (`Created`, `Stamped`,
  `StampRejected` con id de Siigo).
- Meta sube los bytes como adjunto (`IMetaWhatsAppSender` lee el PDF y lo entrega a Mensajería): **no necesita URL**.
- Zenvia exige una URL pública en `fields.documentUrl`. Una URL prefirmada de R2 no sirve: Meta no la baja y el fallo es silencioso
  (memoria `meta-no-baja-url-firmada-de-r2`). Se usa copia en el bucket público con clave aleatoria, como la cotización.
- Cada documento tiene su propia plantilla (Meta: fila de `message_templates` aprobada de la conexión; Zenvia: `templateId` en la
  conexión). La del pedido dice «pedido»; no sirve para una factura.
- Quotations no referencia Siigo ni Messaging; Siigo no referencia Quotations. El cruce va por puertos con adaptador en Bootstrapper.

## Decisiones del owner (2026-11-21)

1. Sólo se envía desde el detalle de un pedido, a pedido del cliente (no automático).
2. Se soportan **Meta y Zenvia**.
3. Para Zenvia se acepta la copia en el bucket público con clave aleatoria y lifecycle de 1 día. No hay borrado programado.

## Enfoques considerados

- **A. Plantilla propia `Invoice` (elegido).** Una plantilla nueva por canal, con texto «factura». Funciona con la ventana de 24 h cerrada.
  Costo: crear y aprobar la plantilla en Meta y en Zenvia, y un tipo de plantilla nuevo en Messaging.
- **B. Reusar la plantilla `Order`.** Sin aprobaciones nuevas, pero el mensaje dice «pedido» y el cliente pidió una factura. Descartado.
- **C. Documento libre dentro de la ventana de 24 h.** Sin plantilla, pero falla cuando la ventana está cerrada, que es lo habitual si el
  cliente pidió la factura por otro medio. Queda como mejora posterior sobre A.

## Diseño

**Flujo.** `POST …/orders/{orderId}/siigo-invoice/{invoiceId}/send-whatsapp` (permiso `siigo.invoice.issue`, política de ruta más
`SiigoAuthorization` en el handler; 403, nunca 404). El handler de Siigo `SendSiigoInvoiceWhatsAppHandler`:

1. Autoriza y carga la fila (de ese pedido y tenant, con `HasPdf`); si no, 404 `siigo.invoice.not_found` sin llamar a Siigo.
2. Pide el PDF a Siigo (misma ruta que la descarga; se extrae un servicio común, no se llama al otro handler).
3. Entrega PDF, número de factura y pedido a un puerto de Siigo `ISiigoInvoiceWhatsAppSender`.

**Adaptador en Bootstrapper** (`SiigoInvoiceWhatsAppSender`, único que ve Siigo, Quotations y Storage): resuelve el destinatario como
`SendOrder` (cliente del pedido, teléfono, nombre), elige canal con `IWhatsAppChannelDirectory.ListAvailableAsync(…, Invoice)` y llama
`IWhatsAppSender.SendInvoiceAsync` (nuevo, hermano de `SendOrderAsync`, con `WhatsAppInvoiceMessage`).

- **Meta:** `MessagingInvoiceTemplateOutbound` (hermano de `OrderTemplateOutbound`) con claves `nombre_contacto`, `numero_factura`,
  `valor_total`. El PDF va en memoria; **no se escribe en Storage**.
- **Zenvia:** sube el PDF a una clave aleatoria bajo un prefijo propio `invoices/` del bucket público (sin id de pedido ni número de
  factura en la clave), manda esa URL en `fields.documentUrl` con `invoiceTemplateId`. Sin bucket público configurado, el envío **falla**
  (nunca cae a URL firmada).
- Total: el de la factura de Siigo si la fila lo guarda; si no, el del pedido. **Decisión del builder** al ver qué guarda `siigo.invoices`;
  nunca inventar un campo.

**Messaging.** Tipo de plantilla nuevo `Invoice` (migración que ensancha el discriminador, como `AddOrderTemplateKind`),
`InvoiceTemplateDefinition` (nombre versionado `qep_factura`, `qep_factura_v2`…), aprovisionamiento y readiness como `Order`. Texto base:
«Hola {{nombre_contacto}}, te enviamos la factura {{numero_factura}} por un total de {{valor_total}}. Adjuntamos el documento.».
**Zenvia:** campo opcional `invoiceTemplateId` en la conexión (como `orderTemplateId`); sin él, ese canal no puede enviar facturas.

**Disponibilidad (`GET` del pedido).** El detalle ya trae `hasPdf`; se agrega `canSendWhatsApp` por factura (hay canal listo y el cliente
tiene teléfono) para que el botón no muestre una acción que va a fallar. Frontend: botón en el detalle del pedido (spec aparte en
`qep-frontend`).

**Errores (códigos nuevos, 422 salvo indicación).** `siigo.invoice.whatsapp_no_channel`, `siigo.invoice.whatsapp_no_phone`,
`siigo.invoice.whatsapp_send_failed` (con el motivo de Meta/Zenvia), `siigo.invoice.not_found` (404, existente).

**Auditoría.** Evento de auditoría en el handler (quién, pedido, factura, canal); **sin** teléfono ni contenido del PDF. La entrada del
hilo de Mensajería la deja el envío por Meta como en cotizaciones.

## Privacidad

- El PDF de la factura nunca se persiste en el bucket privado ni en base de datos.
- Zenvia: copia pública con clave aleatoria de v7 sin datos de negocio, vigente hasta que corra el lifecycle (1 día). Es el riesgo
  aceptado por el owner. La regla de lifecycle de `invoices/` se crea a mano en Cloudflare (`wrangler r2 bucket lifecycle add`), igual que
  `quotations/`; **va documentada en el README y es parte del despliegue**.
- Ninguna línea de log, `call_failures` ni DTO lleva el PDF, la URL pública ni el teléfono (`SiigoNoLeakTests` se extiende).

## Pruebas (TDD)

Unitarias: handler (autorización 403, fila ajena/sin PDF 404, sin canal, sin teléfono, éxito por Meta, éxito por Zenvia, falla del canal),
mensaje de Zenvia con `invoiceTemplateId` y sin él, clave pública aleatoria bajo `invoices/`, plantilla `Invoice` (definición, versiones,
adoptable). Integración (una vez al final del bloque): endpoint con permisos, y que la copia pública no se cree para Meta.
`MessagingLayerTests`/`SiigoLayerTests`/`CompositionRootTests` sin cambios de referencias.

## Despliegue (orden)

1. Migración de Messaging (tipo `Invoice`). 2. Crear y aprobar la plantilla en Meta y en Zenvia, y poner `invoiceTemplateId` en cada
conexión de Zenvia. 3. Regla de lifecycle de `invoices/`. 4. Desplegar el backend, luego el frontend. Sin los pasos 2 y 3 el botón no
aparece (`canSendWhatsApp = false`) o el envío por Zenvia falla ruidosamente.

## Queda para el builder

Qué total guarda `siigo.invoices`; forma exacta de la ruta y del DTO de disponibilidad; texto final de la plantilla; si el puerto vive en
`Modules.Siigo.Application` junto a `ISiigoApi`.
