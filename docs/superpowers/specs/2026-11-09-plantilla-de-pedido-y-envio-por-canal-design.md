# Plantilla de pedido por Meta y envío del pedido por el canal del tenant

Fecha: 2026-11-09. Repo: `qep-backend`. Contraparte: spec homónimo en `qep-frontend`.
Cierra DP-1 (opción b) y DP-2 del plan `2026-10-25-envio-por-canal-backend-3.md`.

## Problema

Hoy el pedido sale **sólo por Zenvia** (`WhatsAppChannelSelection.ForOrder`), con el `orderTemplateId`
de la conexión. Una conexión de Meta no puede mandar un pedido porque no existe una plantilla de sistema
de pedido: `MessageTemplateKind` sólo tiene `Quotation` y los dos `Billing*`. La cotización ya sale por
los dos proveedores y el tenant elige el canal cuando tiene varios (spec 2026-10-21). El pedido debe
comportarse igual: si el tenant tiene sólo Zenvia, sale por Zenvia; si tiene sólo Meta con plantilla de
pedido aprobada, sale por Meta; si tiene los dos, elige.

## Decisiones

- **D1. Nuevo `MessageTemplateKind.Order`**, nombre base `qep_pedido`, prefijo `qep_pedido_` (versiones
  `qep_pedido_v2`…), idioma `es`, categoría `UTILITY`, cabecera `DOCUMENT` (PDF de ejemplo
  «Pedido de ejemplo - QEP», `ExampleFileName = "pedido-ejemplo.pdf"`). Una por conexión: índice único
  parcial `UX_message_templates_order` sobre `(tenant_id, connection_id)` con filtro `kind = 'Order'`,
  traducido en `MessagingSaveChanges` a `messaging.template.order_exists`. Migración calcada de
  `AddBillingTemplateKinds` (Down borra las filas `Order`). Nombre y prefijo entran en
  `MessageTemplateName.IsReserved`.
- **D2. Variables** (`SystemTemplateVariables.Order`): las fijas (`nombre_contacto`, `asesor`, `empresa`,
  `saludo`) más `numero_pedido` (ejemplo `PED-000123`) y `valor_total` (ejemplo `2.450.000 COP`). **No hay
  `vigencia`**: el pedido no tiene fecha de validez. No se usa `order_number` como clave: `LegacyKeys` lo
  mapea a `numero_cotizacion` y `SystemTemplateSend.ValueKeyOf` lo aplica a toda plantilla.
  Cuerpo base:
  `Hola {{nombre_contacto}}, te enviamos el pedido {{numero_pedido}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!`
- **D3. Aprovisionamiento y borrador** calcados de cotización: `POST …/template-management/order`
  (cuerpo opcional `{ body }`, 201 con `ManagedTemplateDto`) y `GET …/template-management/order/draft`
  (`SystemTemplateDraftDto`). Mismo permiso `messaging.template.manage`. Adopción de una `External`
  `qep_pedido`/`es` con la misma forma (cabecera DOCUMENT, sólo variables permitidas, sin botones), con
  `messaging.template.order_conflict` si no calza. Misma cuarentena de nombres (crear antes de borrar,
  hasta 20 intentos), misma auditoría (`TemplateOrderProvisioned`). `SystemTemplateDrafts.For` recibe el
  predicado de adoptabilidad en vez de fijar el de cotización. Sin gate de módulo, como cotización.
- **D4. Pantalla de gestión**: `TemplateManagementDto` suma `OrderTemplate` (misma forma que
  `QuotationTemplateStatusDto`), resuelto en `TemplateManagementReader`. El frontend viejo lo ignora.
- **D5. Readiness y envío (Messaging)**: `IOrderTemplateReadiness.FindApprovedAsync(tenant, connection)`
  hermano de `IQuotationTemplateReadiness`, e `IOrderTemplateOutbound.SendAsync(OrderTemplateSend)` que
  llena `nombre_contacto`, `numero_pedido`, `valor_total` (más `asesor`, `empresa`, `saludo` por
  `TemplateSenderValues`) y manda el PDF como cabecera. Se permite generalizar `QuotationTemplateOutbound`
  por mapa de claves siempre que los tests de cotización sigan verdes sin cambios de contrato.
- **D6. Canales disponibles por documento.** `IWhatsAppChannelDirectory.ListAvailableAsync(tenantId,
  WhatsAppDocument document, ct)` con `WhatsAppDocument { Quotation, Order }`. Para `Order`: Meta cuenta
  con plantilla `Order` **APPROVED** (`IOrderTemplateReadiness`); Zenvia cuenta con token, número,
  `templateId` **y** `orderTemplateId`. Para `Quotation` nada cambia. `order.whatsapp.not_configured`
  desaparece de la selección: una Zenvia sin `orderTemplateId` simplemente no está disponible para
  pedidos (si no queda ninguna, `order.whatsapp.no_channel`). El sender de Zenvia conserva su chequeo
  defensivo.
- **D7. Selección del canal del pedido = la de cotización.** `WhatsAppChannelSelection.Resolve` se
  parametriza por prefijo (`quotation` | `order`) y `ForOrder` desaparece. Reglas (D5–D10 del spec
  2026-10-21): un canal → sale; varios sin elección guardada → `order.whatsapp.channel_required`; elección
  guardada disponible → sale por ella sin preguntar (`connectionId` igual a la guardada se acepta; distinta
  → `order.whatsapp.channel_already_set`); elección guardada **no disponible para pedidos** → se ignora y
  rigen las reglas anteriores; `connectionId` que no está en la lista → `order.whatsapp.channel_unavailable`.
  La elección es **una por tenant, compartida** con cotización (`IWhatsAppChannelChoiceStore`), y un envío
  de pedido con `connectionId` la guarda igual que uno de cotización (misma auditoría, misma transacción,
  nunca si el envío falla).
- **D8. Contrato HTTP del pedido.** `POST /api/v1/tenants/{t}/orders/{id}/send` acepta
  `{ recipient?: "Customer"|"Billing", connectionId?: uuid }` (cuerpo opcional). `SendOrderCommand` suma
  `ConnectionId`. `GET /api/v1/tenants/{t}/quotations/whatsapp-channels?document=order` (default
  `quotation`; otro valor → 422) responde la misma `WhatsAppChannelsDto` filtrada por D6, con
  `SelectedConnectionId` sólo si la guardada está en esa lista y `NeedsChoice` calculado sobre ella. La
  política de lectura admite además `quotations.order.manage`: quien manda pedidos tiene que poder
  preguntar por el canal.
- **D9. Envío por Meta del pedido**: `IMetaWhatsAppSender.SendOrderAsync(MetaWhatsAppChannel, waId,
  WhatsAppOrderMessage)`; `WhatsAppOrderMessage` suma `TenantId`; el router normaliza el teléfono antes
  (`order.whatsapp.recipient_missing`) y bifurca por tipo de canal; para Meta no se publica copia pública
  del PDF (lee por `DocumentStorageKey`), relee la fila aprobada al enviar (`order.whatsapp.send_failed`
  `(template_not_approved)` si ya no está) y el nombre del adjunto es `Pedido-{numero}.pdf`.
- **D10. Comentarios que citan «D12» para «pedido sólo por Zenvia»** (`WhatsAppChannelSelection.cs`,
  `WhatsAppSenderRouter.cs`, `WhatsAppChannels.cs`) se corrigen: la fuente era la decisión 12 del plan
  backend-3, hoy superada por este spec.

## Fuera de alcance

Elección de canal distinta para pedido y cotización; plantilla de pedido en Zenvia distinta de la actual;
cambios en el PDF del pedido.

## Pruebas (TDD, RED antes que GREEN)

Unitarias: `MessageTemplateTests` (nombre reservado, `AdoptAsOrder`), `OrderTemplateDefinitionTests`
(payload con cabecera, versionado, adoptabilidad), `ProvisionOrderTemplateHandlerTests`,
`SystemTemplateDraftsTests` (predicado), `WhatsAppChannelSelectionTests` con prefijo `order`,
`SendOrderHandlerTests` (Meta sale, elección guardada ignorada si no sirve, recuerda la elección,
`channel_already_set`), `WhatsAppSenderRouterTests` (pedido por Meta ya no es error),
`QuotationsWhatsAppChannelDirectoryTests` (por documento), `QuotationsMetaWhatsAppSenderTests` (pedido).
Integración: `OrderTemplateProvisioningApiTests` (calcar las de cotización, incluida la readiness),
`MessageTemplatePersistenceTests` (índice → `order_exists`), `TemplateManagementApiTests`
(`orderTemplate`), `OrderSendApiTests` (`connectionId`, 422 con códigos `order.*`),
`WhatsAppChannelApiTests` (`?document=order`).

## Documentación

`docs/estado.md`: cerrar DP-1 y DP-2 en la fila «Envío de cotizaciones por el canal del tenant» y
corregir «el pedido sigue sólo por Zenvia». README: sección del envío de pedido.
