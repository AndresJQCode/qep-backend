# Siigo: disponibilidad de la factura de un pedido

Fecha: 2026-11-20. Pedido del owner: el botón «Facturar en Siigo» no debe salir si el pedido no se puede
mandar por Siigo. Que exista otra conexión activa no alcanza: el envío sale por la conexión que eligió la
empresa de la cuenta de cobro del pedido, y si esa está pausada el envío falla.

## Contrato

`GET /api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice/availability`

- Permiso `siigo.invoice.issue` y módulo `siigo` (el mismo grupo de rutas que la vista previa).
- 200 `{ "available": bool, "reason": string | null }`. No escribe y no llama a Siigo.
- `reason` es `null` con `available: true`. Con `false` es uno de:
  - `siigo.invoice.currency_unsupported`: el pedido no está en COP.
  - `siigo.invoice.no_company`: la cuenta de cobro del pedido no tiene empresa.
  - `siigo.invoice.no_connection`: ninguna conexión factura con la empresa del pedido.
  - `siigo.connection_unavailable`: la conexión de esa empresa existe pero no se puede usar (pausada,
    clave rechazada, incompleta…).
- 404 si el pedido no es del tenant. 403 sin permiso o con el módulo apagado.

## Decisiones

- Resuelve igual que el envío (`SiigoInvoiceSupport.LoadAsync` y la resolución de credenciales de la
  conexión de la empresa), para que el botón y el envío nunca discrepen. No filtra por «hay alguna
  conexión activa».
- No evalúa lo que no es de la conexión (pedido sin pagar, datos del cliente): eso sigue siendo el 422
  de la vista previa, que explica qué falta.
- La vista previa y el `POST` no cambian. El `POST` conserva su 409 `siigo.connection_unavailable`.
- Es un endpoint aparte, no un campo de la lista de facturas, para no cambiar un contrato en uso.
