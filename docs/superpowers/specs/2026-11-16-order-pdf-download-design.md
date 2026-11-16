# Order PDF download — backend

Date: 2026-11-16. Requested by the owner: replace "Imprimir" on the order detail and the order editor with
"Descargar PDF", like the quotation. The frontend spec is
`qep-frontend/docs/superpowers/specs/2026-11-16-order-pdf-download-design.md`.

## Problem

The order PDF exists only inside `POST /orders/{orderId}/send`: `OrderPdfProvider` renders it, stores it in the
private bucket and the send hands it to WhatsApp. Nothing exposes it for download, so the screen can only print
itself, and the printed page is not the document the customer receives.

## Contract

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/pdf` — no body.

- Permission: `quotations.order.read` (`OrdersPermissions.OrderRead`). Exporting is reading the order in another
  format, the same rule as `POST /quotations/{id}/pdf` with `QuotationRead`.
- `200` → `{ "url": string, "generatedAt": string (ISO 8601) }` (`OrderPdfExportDto`). The URL is the signed,
  short-lived download link from `IQuotationPdfStorage.CreateDownloadUrlAsync`, with the file name
  `Pedido-{orderNumber}.pdf`.
- `404` `order.order.not_found` when the order does not exist in the tenant. `403` without the permission.
- POST and not GET because it generates the document, the same reason as the quotation.

## Decisions

- **Any state, including cancelled and archived.** Downloading is reading; `EnsureSendable` guards sending, not
  reading. The PDF of a cancelled order is still the record of what was agreed.
- **No cache.** `OrderPdfProvider` already renders on every send; a cache needs a table invalidated by
  `Order.Version` and a migration. A download per click costs one `qcode-pdf` call. Revisit if it shows up in
  latency or cost.
- **Same document as the send.** The handler composes the detail with `IOrderResponseComposer` from
  `OrderDetails.LoadAsync`, exactly like `SendOrderHandler`, so the download and the WhatsApp attachment never
  differ.
- **No history entry.** Downloading does not change the order; the quotation export does not record one either.

## Tests

- Unit (`ExportOrderPdfHandlerTests`): authorizes with `OrderRead`; 404 for a missing order; renders from the
  composed detail and returns the signed URL with `Pedido-{number}.pdf`.
- Integration (`OrderPdfExportApiTests`): 200 with a URL; 403 without `quotations.order.read`; 404 for an unknown
  id.
