# Facturar un pedido — diseño (backend + frontend)

**Fecha:** 2026-10-05
**Repos:** `qep-backend` (módulo Quotations, Bootstrapper) y `qep-frontend` (features `orders`,
`quotes`, `reports`, `customers`)
**Estado:** aprobado en conversación; pendiente de revisión escrita

## Problema

Un pedido hoy puede estar `Pending`, `Approved` o `Cancelled` (`OrderStatus.cs:14-19`). Facturación
no tiene forma de dejar constancia en QEP de que un pedido aprobado ya se facturó: el comentario de
`qep-frontend` `features/orders/types/order-list.ts:40-42` lo deja pendiente ("Facturado ... todavía
no existen en el dominio").

## Decisiones tomadas

| #   | Decisión | Alternativa descartada |
| --- | -------- | ---------------------- |
| 1   | Se factura **sólo desde `Approved`**. | Desde `Pending` también: se saltaría la revisión. |
| 2   | `Invoiced` **no se anula ni se edita**. Para corregir un error se revierte (decisión 4). | Permitir anular un facturado: la factura ya existe fuera de QEP, y anular el pedido no la anula. |
| 3   | Al facturar se guarda **quién y cuándo** (`InvoicedAt`, `InvoicedBy`). El request no lleva cuerpo. | Número de factura obligatorio u opcional: no hay hoy quién lo consuma. |
| 4   | **Revertir facturación** (`Invoiced → Approved`): limpia `InvoicedAt`/`InvoicedBy`. La historia queda en la auditoría. | Sin reversa: una marca equivocada sólo se arreglaría tocando la base a mano. |
| 5   | Permiso nuevo **`quotations.order.invoice`**, asignado a **admin y billing**. Cubre facturar **y** revertir. | Un permiso aparte para revertir, sólo admin: billing dependería de un admin para corregir su propio error. |
| 6   | Facturar **no exige** que el pedido esté pagado, igual que aprobar (`Order.Approve` no mira `PaymentStatus`). | Exigir `Paid`: es una regla nueva que no se pidió. |
| 7   | Reportes: un facturado **cuenta como venta** en el resumen. El listado y la exportación lo muestran como "Facturado". | Excluirlo: el resumen sólo excluye `Cancelled` (`OrdersReportSource.cs:71`), y facturado es una venta concretada. |

## Backend (`qep-backend`)

### Dominio

- `OrderStatus.Invoiced`, con una línea en el comentario del enum.
- `Order.InvoicedAt` (`DateTimeOffset?`) y `Order.InvoicedBy` (`MemberId?`).
- `Order.Invoice(MemberId invoicedBy, DateTimeOffset occurredAt)`:
  - Si el estado no es `Approved` → `QuotationsDomainException("order.order.not_approved")`.
  - Setea `Status`, `InvoicedBy`, `InvoicedAt`, `UpdatedAt` y hace `Version++`. No toca
    `ApprovedAt`/`ApprovedBy`.
- `Order.RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)`:
  - Si el estado no es `Invoiced` → `order.order.not_invoiced`.
  - Vuelve a `Approved`, pone `InvoicedAt`/`InvoicedBy` en null, setea `UpdatedAt` y hace
    `Version++`. `revertedBy` sólo viaja a la auditoría; el agregado no lo guarda.
- `Order.Cancel`: si el estado es `Invoiced` → `order.order.already_invoiced`. Este guard va antes que
  el de `already_cancelled` y antes de validar el motivo.
- Las mutaciones que exigen `Pending` (`Order.cs:201,258,289,416`, `AddOrderItems.cs:64`,
  `PreviewOrderEdits.cs:66`, `SaveOrderEdits.cs:73`, `RemoveQuotationItem.cs:43`,
  `UpdateQuotationItem.cs:54`) ya bloquean un facturado, así que no se tocan.

### Aplicación y API

- `InvoiceOrder.cs` y `RevertOrderInvoicing.cs`, calcados de `ApproveOrder.cs`. Cada uno hace, en
  orden:
  1. `QuotationsAuthorization.EnsureAuthorized(..., OrdersPermissions.OrderInvoice)`.
  2. `FindByIdAsync`; si no existe, `OrderNotFound.ById`.
  3. `QuotationAdvisorResolver.ResolveAsync`.
  4. Llamada al método de dominio.
  5. Auditoría (`quotation.order.invoiced` / `quotation.order.invoice_reverted`).
  6. `SaveChangesAsync`.
  - No hay validador, porque no hay texto libre. Tampoco hay evento de outbox: aprobar no lo tiene.
- Los dos handlers se registran a mano en `QepServiceCollectionExtensions.cs` (junto a los de
  `:367-371`).
- Endpoints en `OrderEndpoints.cs`, junto a `approve`/`cancel` (`:80-95`):
  - `POST /api/v1/tenants/{tenantId}/orders/{orderId}/invoice`
  - `POST /api/v1/tenants/{tenantId}/orders/{orderId}/uninvoice`
  - Los dos usan `RequireAuthorization(OrdersPermissions.OrderInvoice)`.
  - Respuestas: `200` con `OrderResponse`, `403`, `404`, `422`.
- `ToResponse`, `OrderMapping.cs` y `OrdersDtos.cs` suman `invoicedAt` e `invoicedBy` (nullable).

### Permiso

- `OrdersPermissions.OrderInvoice = "quotations.order.invoice"`.
- `QepServiceCollectionExtensions.cs` necesita tres cambios:
  - `PermissionDefinition` en el catálogo (junto a `:853-861`), módulo Quotations, riesgo "medium".
  - La política en `AddAuthorization` (junto a `:1114-1118`). Si falta, el síntoma es un **500**.
  - El permiso en los `RoleDefinition` de admin (`:610-613`) y de billing (`:692`).
- Los roles de sistema viven en código, así que no hay migración de datos.
- Se actualiza `AuthorizationCatalogApiTests.cs:95-102`: el conjunto de pedidos de billing pasa a ser
  `approve`, `invoice` y `read`.

### Persistencia

- `QuotationsDbContext.cs`, junto a `approved_at`/`approved_by` (`:427-432`): `invoiced_at` e
  `invoiced_by`, con conversión nullable de `MemberId`.
- Migración `yyyyMMddHHmmss_AddOrderInvoicing` con las dos columnas nullable. `status` ya es
  `varchar(20)` sin check constraint, así que `Invoiced` entra sin migrar la columna.
- `QuotationUserReferenceProbe.cs:18-19,50-51` suma `invoiced_by`.

### Reportes y exportación

- `ExportStatusLabels.cs:28-34`: `Invoiced → "Facturado"`. Sin esa etiqueta,
  `ExportStatusLabelsTests` falla porque recorre `Enum.GetValues`.
- `OrdersReportSource.cs` no cambia (decisión 7).

### Pruebas (RED antes que GREEN)

- `OrderTests.cs`:
  - Facturar un aprobado: guarda quién y cuándo, y conserva la aprobación.
  - Facturar un pendiente o un anulado: `not_approved`.
  - Facturar dos veces: `not_approved`.
  - Revertir un facturado: vuelve a `Approved` sin las marcas.
  - Revertir algo que no está facturado: `not_invoiced`.
  - Anular un facturado: `already_invoiced`.
  - Un facturado rechaza `AddPaymentProofs`, `RecalculatePaymentStatus`, `RemovePaymentProof` y
    `Approve`.
- `OrderApiTests.cs`:
  - Facturar devuelve `200` con los campos nuevos.
  - Revertir devuelve `200` y, después, anular el pedido funciona.
  - `403` sin `quotations.order.invoice`, incluido un miembro que sí tiene `approve`.
  - `404` con un pedido inexistente.
  - `422` por cada código.
  - Las dos acciones quedan auditadas.
- `QuotationsDbContextMappingTests`, `ExportStatusLabelsTests`, `AuthorizationCatalogApiTests`, y la
  prueba de huérfanos que cubre `QuotationUserReferenceProbe`.

## Frontend (`qep-frontend`)

### Tipos y contrato

- `'Invoiced'` entra en el union de `features/orders/types/order-list.ts` (etiqueta "Facturado" en
  `ORDER_STATUS_LABELS`) y en `features/quotes/types/order.ts`.
- `Order` suma `invoicedAt: string | null` e `invoicedBy`, con el mismo tipo que `approvedBy`.
- `ORDER_INVOICE_PERMISSION = 'quotations.order.invoice'` en `features/quotes/types/order.ts`.
- En `features/quotes/services/orders.api.ts`:
  - `invoiceOrder` y `revertOrderInvoicing`, junto a `approveOrder`.
  - `describe…Failure` para cada uno: `403` → "No tienes permiso para facturar pedidos.".
  - Mensajes `422` por código: `not_approved`, `not_invoiced`, `already_invoiced`.
- Hooks `use-invoice-order.ts` y `use-revert-order-invoicing.ts`, calcados de `use-approve-order.ts`.
  Invalidan el listado de pedidos y el de cotizaciones, igual que `use-cancel-order.ts`.

### UI

- `InvoiceOrderButton` ("Marcar como facturado"):
  - Visible sólo con estado `Approved` y `can(ORDER_INVOICE_PERMISSION)`.
  - Abre un `AlertDialog` de confirmación.
- `RevertInvoicingButton` ("Revertir facturación"):
  - Visible sólo con estado `Invoiced` y el mismo permiso.
  - También abre un `AlertDialog` de confirmación.
- `cancel-order-button.tsx`: se oculta también con estado `Invoiced`.
- Badge y textos:
  - `order-status-badge.tsx` y `STATUS_DISPLAY` de `order-summary-card.tsx` reciben `Invoiced` →
    "Facturado".
  - Hay que corregir los ternarios que hoy dirían "Aprobado el" en un facturado
    (`order-items-card.tsx`, `order-detail-page.tsx`, `order-summary-cards.tsx`).
  - Se agrega "Facturado el {fecha}".
- `routes/_authenticated/orders/$orderId/index.tsx` cablea los hooks y pasa `canInvoice`. La página
  sigue siendo presentacional.
- `features/reports/types/orders-report.ts`: `Invoiced → "Facturado"`. Se ajusta
  `orders-report.test.ts:17`, que hoy espera `'Invoiced'` crudo.

### Pruebas (RED antes que GREEN)

- `orders.api.test.ts`: método, ruta y `X-Qep-Client` de las dos llamadas, más el mapeo de errores.
- Pruebas de los dos botones:
  - Se ocultan sin permiso o con un estado que no corresponde.
  - El diálogo confirma antes de enviar.
  - Muestran el error y cierran cuando hay éxito.
- `cancel-order-button`: se oculta con `Invoiced`.
- `order-detail-page`: el estado facturado muestra la fecha y no ofrece acciones de edición.

## Entrega

1. **Backend** en `feature/facturar-pedido` de `qep-backend`. Commits en este orden: dominio y
   persistencia → permiso → handlers y endpoints → etiquetas.
2. **Frontend** en su propia rama de `qep-frontend`. Se trabaja en paralelo, contra el contrato de
   este spec.
3. **Despliegue:** primero el backend, después el frontend.

Conventional commits, sin atribución de IA.
