# Cotizaciones y pedidos — guía de integración para el frontend

Contrato leído del código (`src/Modules/Quotations/`), no de un spec aparte. Ante cualquier
diferencia con la respuesta real de la API, **gana la API** y este documento se corrige.

## Antes de la primera llamada

- Todas las rutas cuelgan del tenant: `/api/v1/tenants/{tenantId}/…`.
- **Los permisos del módulo están temporalmente desactivados** (a pedido, mientras se prueba
  manualmente) — ver `QuotationEndpoints.cs`/`OrderEndpoints.cs`/`QuotationsAuthorization.cs`,
  todos marcados `TEMPORAL`. Hoy sólo hace falta estar autenticado; **no construir la UI
  asumiendo 403 por falta de permiso específico**, porque va a volver a exigirse antes de
  producción (`quotations.quotation.read/manage`, `quotations.order.read/manage`). El
  aislamiento por tenant (403 si el tenant no coincide) sí sigue activo siempre.
- Métodos que mutan llevan `X-Qep-Client: web` (CSRF), igual que el resto de la API.
- Errores: `ProblemDetails` con `code` en la raíz — ver
  [README § Verificación](../README.md) y `docs/integracion-imagenes-de-producto.md` para el
  formato exacto.

## Estados

```
Quotation.status: Draft → Sent → Converted
                              ↘ Voided (desde Draft o Sent)
                              ↘ Expired (job automático, sólo desde Sent)

Order.status:        Pending → Approved
Order.paymentStatus: FullPaymentReceived | PartialPaymentReceived | PaymentPending
```

Editar (encabezado o líneas) sólo funciona en `Draft`/`Sent`. `Converted`, `Voided` y `Expired`
son de sólo lectura (422 `quotation.quotation.not_editable`). Única excepción: mientras el pedido
sigue `Pending`, `POST /quotations/{id}/order/items` le suma líneas a su cotización —sólo sumar,
nada de cambiar cantidad ni quitar—, sin importar el `status` de la cotización.

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
| `GET` | `/quotations/{id}/order` | — | 404 si no se convirtió todavía |
| `POST` | `/quotations/{id}/order` | `ConvertQuotationToOrderRequest` | Crea el pedido en `Pending` y deja la cotización en `Converted`, en una sola operación |
| `POST` | `/quotations/{id}/order/items` | `AddOrderItemsRequest` | 200 `OrderDetailResponse`. Sólo con el pedido en `Pending`; suma líneas a la cotización y recalcula `paymentStatus` contra el total nuevo |

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

type ConvertQuotationToOrderRequest = {
  paymentStatus: "FullPaymentReceived" | "PartialPaymentReceived" | "PaymentPending";
  notes: string | null;
  paymentProofs: { fileId: string; amount: number }[]; // vacío sólo permitido si paymentStatus = PaymentPending
};

type QuotationResponse = {
  id: string; quotationNumber: string; clientId: string; advisorId: string;
  status: "Draft" | "Sent" | "Voided" | "Expired" | "Converted";
  createdAt: string; validUntil: string | null; paymentMethod: string | null;
  subtotal: number; taxPercentage: number; taxAmount: number; discountAmount: number; total: number;
  notes: string | null;
  billingNameOverride: string | null; billingAddressOverride: string | null;
  deliveryAddressOverride: string | null; deliveryCityOverride: string | null;
  createdBy: string; updatedBy: string | null; updatedAt: string;
  sentAt: string | null; pdfFileId: string | null;
  // Las tres preguntas que la pantalla no reimplementa. `hasChangesSinceSent` (se editó después
  // del último envío) ya está adentro de `canBeConvertedToOrder`; viaja aparte para que el
  // detalle pueda decir *por qué* no se ofrece convertir, en vez de esconder el botón callado.
  canBeSent: boolean; hasChangesSinceSent: boolean; canBeConvertedToOrder: boolean;
  items: { id, productId, quantity, unitPrice, discountPercentage, discountAmount, subtotal, position }[];
};

type OrderResponse = {
  id: string; orderNumber: string; quotationId: string; status: "Pending" | "Approved";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string; ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  paymentProofs: { id, fileId, amount, uploadedAt }[];
};

type AddOrderItemsRequest = { toAdd: { productId: string; quantity: number }[] }; // al menos uno

type OrderDetailResponse = { order: OrderResponse; quotation: QuotationResponse };
```

`advisorId`/`createdBy`/`updatedBy`/`convertedBy` son ids de **membership** (Tenancy), no el
`subject`/usuario — son valores distintos a propósito.

## PDF y comprobantes de pago: no hay generación de PDF en el backend

Tanto el PDF de envío como los comprobantes de pago se suben con el flujo de Storage que ya
existe (`docs/integracion-imagenes-de-producto.md`, pasos 2-4: sesión → `PUT` al storage →
`complete`). Acá sólo cambia qué se referencia:

1. `POST /files` → `{ ownerId, ownerType, name, mimeType, sizeBytes }` → trae `uploadUrl`.
   `ownerType` es `"PaymentProof"` para un comprobante de pago y `"User"` para el PDF de envío.
2. `PUT` directo a `uploadUrl` con los bytes.
3. `POST /files/{fileResourceId}/complete` → el archivo queda `Available`.
4. Usar ese `fileResourceId` como `pdfFileId` (send) o `fileId` de cada comprobante (convert).

No hace falta publicar (paso 5 de esa guía). Un comprobante `PaymentProof` no se promueve: espera en
`staging/` y, si es imagen, `complete` ya lo deja en WebP de hasta 2000 px, así que el `mimeType` y la
extensión del `name` de su respuesta cambian. Con `Quotations:PaymentProofs:PublicLinks` encendida, el
backend copia cada comprobante nuevo al bucket público al convertir o al sumar comprobantes, para que
el Excel de pedidos lo enlace, y segundos después Storage borra el temporal. Desde ahí
`POST /files/{id}/download-url` de ese comprobante devuelve la URL pública, que el navegador **abre**
en vez de descargar con el nombre original. Un comprobante `User` sigue como antes. La respuesta de los
endpoints de pedidos no cambia.

Reemplazar el archivo de un comprobante (`updatedProofs[].newFileId`) o quitarlo
(`DELETE /order/proofs/{proofId}`) borra, segundos después, el archivo que el pedido deja de usar: su
URL pública deja de abrir y un `PaymentProof` quitado ya no se puede volver a adjuntar
(`order.payment_proof.file_not_available`). Para corregir, sube un archivo nuevo.

- PDF de envío: sólo `application/pdf`.
- Comprobante de pago: `application/pdf`, `image/jpeg`, `image/png` o `image/webp`, hasta 10 MB.

## Códigos de error propios del módulo

| `code` | HTTP | Qué pasó |
|---|---|---|
| `quotation.quotation.client_not_found` / `client_inactive` / `client_cuc_missing` | 422 | Cliente inválido al crear o al convertir |
| `quotation.quotation.not_editable` | 422 | La cotización no está en `Draft`/`Sent` |
| `quotation.quotation.not_draft` | 422 | `send` sobre algo que no es `Draft` |
| `quotation.quotation.not_sent` | 422 | `void`/convertir sobre algo que no es `Sent` (void también acepta Draft) |
| `quotation.quotation.changed_since_sent` | 422 | Convertir una cotización editada después de su último envío: hay que reenviarla primero |
| `quotation.quotation.pdf_not_found` / `pdf_not_available` / `pdf_not_a_pdf` | 422 | Problema con el `pdfFileId` de `send` |
| `quotation.item.product_not_found` / `product_inactive` / `product_price_unavailable` | 422 | Producto inválido al agregar una línea |
| `quotation.item.duplicate_product` | 422 | El producto ya está en la cotización: se cambia la cantidad de su línea, no se agrega otra |
| `order.order.not_pending` | 422 | El pedido ya está `Approved`: no admite comprobantes (`/order/proofs`), productos (`/order/items`) ni otra aprobación |
| `order.order.payment_proof_required` | 422 | `POST /order` sin comprobantes y el pago no es `PaymentPending`; en `POST /order/proofs`, ni `paymentProofs` ni `updatedProofs` traen nada |
| `order.payment_proof.file_not_found` / `file_not_available` / `file_type_not_allowed` / `file_too_large` | 422 | Problema con un comprobante nuevo |
| `order.payment_proof.amount_invalid` | 422 | Un comprobante (nuevo o corregido en `updatedProofs`) con monto ≤ 0 |
| `order.payment_proof.not_found` | 422 | `updatedProofs` referencia un `proofId` que no es de este pedido |
| `validation.failed` | 422 | Errores de campo, viene con `errors` |

---

Fuente: `src/Modules/Quotations/Modules.Quotations.Api/*.cs`,
`src/Modules/Quotations/Modules.Quotations.Application/*Dtos.cs`,
`src/Api/ApiExceptionHandler.cs`.
