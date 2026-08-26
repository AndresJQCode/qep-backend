# Cotizaciones y ventas — guía de integración para el frontend

Contrato leído del código (`src/Modules/Quotations/`), no de un spec aparte. Ante cualquier
diferencia con la respuesta real de la API, **gana la API** y este documento se corrige.

## Antes de la primera llamada

- Todas las rutas cuelgan del tenant: `/api/v1/tenants/{tenantId}/…`.
- **Los permisos del módulo están temporalmente desactivados** (a pedido, mientras se prueba
  manualmente) — ver `QuotationEndpoints.cs`/`SaleEndpoints.cs`/`QuotationsAuthorization.cs`,
  todos marcados `TEMPORAL`. Hoy sólo hace falta estar autenticado; **no construir la UI
  asumiendo 403 por falta de permiso específico**, porque va a volver a exigirse antes de
  producción (`quotations.quotation.read/manage`, `quotations.sale.read/manage`). El
  aislamiento por tenant (403 si el tenant no coincide) sí sigue activo siempre.
- Métodos que mutan llevan `X-Qep-Client: web` (CSRF), igual que el resto de la API.
- Errores: `ProblemDetails` con `code` en la raíz — ver
  [README § Verificación](../README.md) y `docs/integracion-imagenes-de-producto.md` para el
  formato exacto.

## Estados

```
Quotation.status: Draft → Sent → Approved
                              ↘ Voided (desde Draft o Sent)
                              ↘ Expired (job automático, sólo desde Sent)

Sale.status:         Approved   (único valor hoy)
Sale.paymentStatus:  FullPaymentReceived | PartialPaymentReceived | PaymentPending
```

Editar (encabezado o líneas) sólo funciona en `Draft`/`Sent`. `Approved`, `Voided` y `Expired`
son de sólo lectura (422 `quotation.quotation.not_editable`).

## Endpoints

| Método | Ruta | Body | Notas |
|---|---|---|---|
| `GET` | `/quotations` | — (query: `clientId`, `advisorId`, `status`, `createdFrom`, `createdTo`, `page`, `pageSize`) | Paginado, sin líneas |
| `GET` | `/quotations/{id}` | — | Con líneas |
| `POST` | `/quotations` | `CreateQuotationRequest` | 201, arranca en `Draft` |
| `PATCH` | `/quotations/{id}` | `UpdateQuotationRequest` | Reemplaza el encabezado entero (no PATCH parcial pese al verbo) |
| `POST` | `/quotations/{id}/items` | `AddQuotationItemRequest` | Descuento se calcula solo por escala del producto |
| `PUT` | `/quotations/{id}/items/{itemId}` | `UpdateQuotationItemRequest` | Re-resuelve el descuento para la nueva cantidad |
| `DELETE` | `/quotations/{id}/items/{itemId}` | — | |
| `POST` | `/quotations/{id}/send` | `SendQuotationRequest` | El PDF ya se subió a Storage antes de este llamado (ver abajo) |
| `POST` | `/quotations/{id}/void` | — (sin body) | |
| `GET` | `/quotations/{id}/sale` | — | 404 si no se convirtió todavía |
| `POST` | `/quotations/{id}/sale` | `ConvertQuotationToSaleRequest` | Aprueba la cotización y crea la venta en una sola operación |

## Formas de los DTOs

```ts
type CreateQuotationRequest = {
  clientId: string;
  validUntil: string | null;        // "yyyy-MM-dd"
  paymentMethod: string | null;
  taxPercentage: number | null;     // default 19.00 si se omite
  notes: string | null;
  overrides: { billingName, billingAddress, deliveryAddress, deliveryCity } | null; // todos string | null
};
// UpdateQuotationRequest: mismos campos, sin clientId (no se puede cambiar el cliente de una cotización)

type AddQuotationItemRequest = { productId: string; quantity: number };
type UpdateQuotationItemRequest = { quantity: number };

type SendQuotationRequest = { pdfFileId: string };

type ConvertQuotationToSaleRequest = {
  paymentStatus: "FullPaymentReceived" | "PartialPaymentReceived" | "PaymentPending";
  notes: string | null;
  paymentProofs: { fileId: string; amount: number }[]; // vacío sólo permitido si paymentStatus = PaymentPending
};

type QuotationResponse = {
  id: string; quotationNumber: string; clientId: string; advisorId: string;
  status: "Draft" | "Sent" | "Approved" | "Voided" | "Expired";
  createdAt: string; validUntil: string | null; paymentMethod: string | null;
  subtotal: number; taxPercentage: number; taxAmount: number; discountAmount: number; total: number;
  notes: string | null;
  billingNameOverride: string | null; billingAddressOverride: string | null;
  deliveryAddressOverride: string | null; deliveryCityOverride: string | null;
  createdBy: string; updatedBy: string | null; updatedAt: string;
  sentAt: string | null; pdfFileId: string | null;
  items: { id, productId, quantity, unitPrice, discountPercentage, discountAmount, subtotal, position }[];
};

type SaleResponse = {
  id: string; saleNumber: string; quotationId: string; status: "Approved";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string; ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  paymentProofs: { id, fileId, amount, uploadedAt }[];
};
```

`advisorId`/`createdBy`/`updatedBy`/`convertedBy` son ids de **membership** (Tenancy), no el
`subject`/usuario — son valores distintos a propósito.

## PDF y comprobantes de pago: no hay generación de PDF en el backend

Tanto el PDF de envío como los comprobantes de pago se suben con el flujo de Storage que ya
existe (`docs/integracion-imagenes-de-producto.md`, pasos 2-4: sesión → `PUT` al storage →
`complete`). Acá sólo cambia qué se referencia:

1. `POST /files` → `{ ownerId, ownerType: "User", name, mimeType, sizeBytes }` → trae `uploadUrl`.
2. `PUT` directo a `uploadUrl` con los bytes.
3. `POST /files/{fileResourceId}/complete` → el archivo queda `Available`.
4. Usar ese `fileResourceId` como `pdfFileId` (send) o `fileId` de cada comprobante (convert).

No hace falta publicar (paso 5 de esa guía) — estos archivos no necesitan URL pública.

- PDF de envío: sólo `application/pdf`.
- Comprobante de pago: `application/pdf`, `image/jpeg` o `image/png`, hasta 10 MB.

## Códigos de error propios del módulo

| `code` | HTTP | Qué pasó |
|---|---|---|
| `quotation.quotation.client_not_found` / `client_inactive` / `client_cuc_missing` | 422 | Cliente inválido al crear o al convertir |
| `quotation.quotation.not_editable` | 422 | La cotización no está en `Draft`/`Sent` |
| `quotation.quotation.not_draft` | 422 | `send` sobre algo que no es `Draft` |
| `quotation.quotation.not_sent` | 422 | `void`/convertir sobre algo que no es `Sent` (void también acepta Draft) |
| `quotation.quotation.pdf_not_found` / `pdf_not_available` / `pdf_not_a_pdf` | 422 | Problema con el `pdfFileId` de `send` |
| `quotation.item.product_not_found` / `product_inactive` / `product_price_unavailable` | 422 | Producto inválido al agregar una línea |
| `sale.sale.payment_proof_required` | 422 | Falta al menos un comprobante y el pago no es `PaymentPending` |
| `sale.payment_proof.file_not_found` / `file_not_available` / `file_type_not_allowed` / `file_too_large` | 422 | Problema con un comprobante |
| `validation.failed` | 422 | Errores de campo, viene con `errors` |

---

Fuente: `src/Modules/Quotations/Modules.Quotations.Api/*.cs`,
`src/Modules/Quotations/Modules.Quotations.Application/*Dtos.cs`,
`src/Api/ApiExceptionHandler.cs`.
