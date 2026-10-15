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

Order.status:        Pending → Approved → Invoiced
                     Invoiced → Approved (revertir la facturación; no anula nada fuera de QEP)
                     Pending | Approved → Cancelled (con motivo; la cotización sigue Converted)
Order.paymentStatus: FullPaymentReceived | PartialPaymentReceived | PaymentPending
Order.source:        Quotation | Manual | Shopify   (de dónde nació; no cambia)
```

Editar (encabezado o líneas) sólo funciona en `Draft`/`Sent`. `Converted`, `Voided` y `Expired`
son de sólo lectura (422 `quotation.quotation.not_editable`), sin excepciones.

**El pedido tiene sus propios datos** (spec 2026-10-15): cliente, asesor, cuenta de cobro, partes,
líneas y totales. Convertir **copia** la cotización al pedido; desde ahí son independientes. Mientras
el pedido sigue `Pending`, `PUT /orders/{orderId}` y `POST /orders/{orderId}/items` editan **las
líneas del pedido**, y la cotización `Converted` no cambia. Un pedido puede no tener cotización:
el manual (`POST /orders`, `source: "Manual"`) y el de una integración (`source: "Shopify"`) traen
`quotationId: null`.

## Endpoints

**El pedido se direcciona por su propio id.** De la cotización cuelgan sólo dos cosas: convertirla
en pedido (`POST /quotations/{id}/order`) y leer el pedido que salió de ella
(`GET /quotations/{id}/order`) — ahí todavía no hay un `orderId` que usar. Todo lo demás
—aprobar, anular, facturar, revertir la facturación, comprobantes, productos, editar y el cálculo
previo— va por
`/orders/{orderId}/…`, porque quien llega desde el listado de pedidos tiene el id del pedido y no
el de su cotización. Un `orderId` que no existe en el tenant responde **404
`order.order.not_found`** en todas ellas. Las rutas viejas `/quotations/{id}/order/...` **no
quedaron como alias**, pero la respuesta depende de si el segmento viejo existía: las seis con
segmento propio (`/approve`, `/cancel`, `/proofs`, `/proofs/{proofId}`, `/items`, `/preview`)
responden **404**, porque ese segmento ya no existe. `PUT /quotations/{id}/order` es distinto: ese
path —sin segmento adicional— sigue vivo, porque de ahí cuelgan `GET` (leer) y `POST`
(convertir); el `PUT` que se mudó a `/orders/{orderId}` responde ahí **405**, no 404.

Todo lo que cuelga de `/quotations/{id}/items...` sigue siendo de la cotización, no del pedido.

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
| `GET` | `/quotations/{id}/order` | — | 200 `OrderDetailResponse`, el mismo de `GET /orders/{orderId}`. 404 si no se convirtió todavía |
| `POST` | `/quotations/{id}/order` | `ConvertQuotationToOrderRequest` | 201 `OrderDetailResponse` (el mismo de `GET /orders/{orderId}`: la pantalla dibuja el resumen sin pedir nada después) + `Location`. Copia la cotización a un pedido en `Pending` (`source: "Quotation"`) y la deja en `Converted`, en una sola operación. Las notas del pedido son las de la cotización más las que vengan acá, separadas por una línea en blanco |

Y los del pedido, siempre por su `orderId` (el `id` que devuelve la conversión, el listado
`GET /orders` o el detalle `GET /orders/{orderId}`):

| Método | Ruta | Body | Notas |
|---|---|---|---|
| `GET` | `/orders` | — (query: `clientId`, `advisorId`, `status`, `paymentStatus`, `convertedFrom`, `convertedTo`, `clientCuc`, `orderNumber`, `source`, `page`, `pageSize`) | Paginado, `OrdersPageResponse`. `source` es `Quotation`, `Manual` o `Shopify` (sin él, todos; otro valor → 422 `order.order.source_invalid`) |
| `POST` | `/orders` | `CreateOrderRequest` | 201 `OrderDetailResponse` + `Location`. Pedido manual, sin cotización, en `Pending`. Exige `quotations.order.manage` y cliente completo con CUC |
| `POST` | `/orders/preview` | `CreateOrderRequest` (los comprobantes pueden ir sin `fileId`) | 200 `OrderDetailResponse` calculado **sin persistir** y sin gastar número de pedido |
| `GET` | `/orders/{orderId}` | — | 200 `OrderDetailResponse`: el pedido con sus datos, el cliente y el asesor resueltos, las líneas, la cotización de origen (si la hay) y el historial |
| `POST` | `/orders/{orderId}/approve` | — (sin body) | 200 `OrderResponse` en `Approved`. Sólo desde `Pending`; exige `quotations.order.approve` (admin y facturación, no el asesor: `quotations.order.manage` no alcanza) |
| `POST` | `/orders/{orderId}/cancel` | `CancelOrderRequest` | 200 `OrderResponse` en `Cancelled`. Desde `Pending` o `Approved`; exige `quotations.order.cancel` (sólo admin). Conserva `approvedAt`/`approvedBy` |
| `POST` | `/orders/{orderId}/invoice` | — (sin body) | 200 `OrderResponse` en `Invoiced`, con `invoicedAt`/`invoicedBy`. Sólo desde `Approved`; no exige el pago completo. Exige `quotations.order.invoice` (admin y facturación: `quotations.order.approve` no alcanza). Es sólo el estado: QEP no emite la factura |
| `POST` | `/orders/{orderId}/uninvoice` | — (sin body) | 200 `OrderResponse` de vuelta en `Approved`, con `invoicedAt`/`invoicedBy` en `null`. Sólo desde `Invoiced`; mismo permiso que facturar. No anula nada fuera de QEP |
| `POST` | `/orders/{orderId}/proofs` | `AddOrderPaymentProofsRequest` | 200 `OrderResponse`. Suma comprobantes y corrige monto o archivo de los ya cargados. Sólo con el pedido en `Pending` |
| `DELETE` | `/orders/{orderId}/proofs/{proofId}` | — | 200 `OrderResponse`. Quita un comprobante y recalcula `paymentStatus`. Sólo con el pedido en `Pending` |
| `POST` | `/orders/{orderId}/items` | `AddOrderItemsRequest` | 200 `OrderDetailResponse`. Sólo con el pedido en `Pending`; suma líneas al pedido y recalcula `paymentStatus` contra el total nuevo |
| `PUT` | `/orders/{orderId}` | `SaveOrderEditsRequest` + header `If-Match: "<order.version>"` | 200 `OrderDetailResponse` + `ETag`. Guarda de una vez productos (lista completa), comprobantes (`add`/`update`/`removeIds`) y notas; `paymentStatus` lo deriva el servidor. Sin cambios reales responde 200 con la misma `version`. Sin `If-Match` → 428; versión vieja → 412 |
| `POST` | `/orders/{orderId}/preview` | `SaveOrderEditsRequest` (sin `fileId` en `add`) | 200 `OrderDetailResponse` recalculado **sin persistir**. `order.version` es la guardada; los comprobantes nuevos vuelven con `id`/`fileId` sintéticos que no se deben usar |
| `POST` | `/orders/export` | — (mismos filtros del listado por query string, `source` incluido) | 202: el Excel del listado llega por correo |

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
  // `paidOn` (2026-10-08) es la fecha del soporte de pago que escribe quien adjunta el comprobante,
  // como fecha ISO sin hora (`"2026-10-06"`). Se guarda con el comprobante y es la que el Excel de
  // pedidos muestra en `Fecha Pago N`; sin ella el comprobante queda con `paidOn: null` y el Excel
  // cae a la fecha de subida. Que sea obligatoria es decisión pendiente. Mismo campo en
  // `POST /orders/{orderId}/proofs` y en `proofs.add` de `PUT /orders/{orderId}`. Al corregir un
  // comprobante que ya existe (`updatedProofs[]` y `proofs.update[]`) también viaja junto al
  // `amount`, y reemplaza la guardada: null o ausente la borra, no es un PATCH parcial.
  paymentProofs: { fileId: string; amount: number; paidOn?: string | null }[]; // vacío sólo permitido si paymentStatus = PaymentPending
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

type CancelOrderRequest = { reason: string }; // obligatorio, se recorta, máx. 500

// Spec 2026-10-15 §5.2: el mismo cuerpo que la pantalla arma para una cotización, más el estado de
// pago y los comprobantes. Precio, descuento por escala y tasa los resuelve el backend.
type CreateOrderRequest = {
  clientId: string;
  paymentMethod: string | null;
  notes: string | null;
  isRetail: boolean;
  globalScaleFloor: number | null;
  parties: QuotationPartiesRequest | null; // null: facturación y envío con los datos del cliente
  billingAccount: { companyId: string; bankName: string; accountNumber: string; currency: string };
  items: { productId: string; quantity: number }[]; // al menos uno, sin productos repetidos
  paymentStatus: "FullPaymentReceived" | "PartialPaymentReceived" | "PaymentPending";
  paymentProofs: { fileId: string; amount: number; paidOn?: string | null }[]; // vacío sólo con PaymentPending
};

type OrderResponse = {
  id: string; orderNumber: string;
  source: "Quotation" | "Manual" | "Shopify";
  quotationId: string | null;          // null en un pedido manual o de una integración
  externalReference: string | null;    // "#1001" en uno de Shopify
  clientId: string; advisorId: string;
  status: "Pending" | "Approved" | "Cancelled" | "Invoiced";
  paymentStatus: string; paymentMethod: string | null; currency: string;
  isRetail: boolean; globalScaleFloor: number | null;
  billingUsesBusinessName: boolean; isStorePickup: boolean; billsToFinalConsumer: boolean;
  billingWithRetention: boolean | null; billingVatSurplus: boolean | null;
  customerVatSurplus: boolean;         // el valor efectivo: con consumidor final es false
  subtotal: number; taxPercentage: number; taxAmount: number; discountAmount: number;
  total: number; retentionAmount: number; netTotal: number;
  notes: string | null;                // sin tope de largo
  billingAccount: { companyId: string; bankName: string; accountNumber: string; currency: string };
  parties: QuotationPartyResponse[];   // las líneas no viajan acá: van en OrderDetailResponse.items
  convertedAt: string; convertedBy: string; // en un pedido manual o externo: cuándo y quién lo creó
  approvedAt: string | null; approvedBy: string | null;
  cancelledAt: string | null; cancelledBy: string | null; cancellationReason: string | null;
  invoicedAt: string | null; invoicedBy: string | null; // null si no está facturado
  ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  version: number; // el If-Match de PUT /orders/{orderId}
  // `uploadedAt` es el instante de la subida; `paidOn` la fecha del soporte que mandó el frontend
  // al adjuntar (2026-10-08), o null si no la trajo.
  paymentProofs: { id, fileId, amount, uploadedAt, paidOn: string | null }[];
};

type AddOrderItemsRequest = { toAdd: { productId: string; quantity: number }[] }; // al menos uno

// Spec 2026-10-15 §6.2: la cotización ya no viaja. `items` son las líneas del pedido con su producto
// resuelto (mismo QuotationItemResponse de la cotización); `sourceQuotation` es null sin cotización.
type OrderDetailResponse = {
  order: OrderResponse;
  client: QuotationClientResponse | null;
  advisorName: string | null; advisorEmail: string | null;
  billingAccount: QuotationBillingResponse | null;
  items: QuotationItemResponse[];
  sourceQuotation: { id: string; quotationNumber: string } | null;
  history: { id: string; eventType: string; eventAt: string; memberId: string | null; memberName: string | null; details: string | null }[];
  availableFloors: number[];
};

type OrderListItemResponse = {
  id: string; orderNumber: string;
  quotationId: string | null; quotationNumber: string | null;
  clientId: string; clientName: string | null; advisorId: string; advisorName: string | null;
  status: string; paymentStatus: string; paymentMethod: string | null;
  convertedAt: string; currency: string; total: number;
  source: string; externalReference: string | null;
};
```

`advisorId`/`createdBy`/`updatedBy`/`convertedBy`/`approvedBy`/`cancelledBy`/`invoicedBy` son ids de **membership** (Tenancy), no el
`subject`/usuario — son valores distintos a propósito.

## Comprobantes de pago: se suben con el flujo de Storage

El PDF de envío ya no lo sube el cliente: lo genera el backend, y `POST /quotations/{id}/send` va sin
cuerpo (un `pdfFileId` que llegue se ignora). Los comprobantes de pago sí se suben con el flujo de
Storage que ya existe (`docs/integracion-imagenes-de-producto.md`, pasos 2-4: sesión → `PUT` al
storage → `complete`):

1. `POST /files` → `{ ownerId, ownerType, name, mimeType, sizeBytes }` → trae `uploadUrl`.
   `ownerType` es `"PaymentProof"`.
2. `PUT` directo a `uploadUrl` con los bytes.
3. `POST /files/{fileResourceId}/complete` → el archivo queda `Available`.
4. Usar ese `fileResourceId` como `fileId` de cada comprobante (convert, sumar comprobantes o
   `newFileId` de un reemplazo). Cada comprobante `PaymentProof` se adjunta **una sola vez**: el mismo
   archivo repetido en un request responde `422 validation.failed`, y uno que ya usa otro comprobante
   responde `422 order.payment_proof.file_not_available`. Para corregir, sube un archivo nuevo.

No hace falta publicar (paso 5 de esa guía). Un comprobante `PaymentProof` no se promueve: espera en
`staging/` y, si es imagen, `complete` ya lo deja en WebP de hasta 2000 px, así que el `mimeType` y la
extensión del `name` de su respuesta cambian. Con `Quotations:PaymentProofs:PublicLinks` encendida, el
backend copia cada comprobante nuevo al bucket público al convertir o al sumar comprobantes, para que
el Excel de pedidos lo enlace, y segundos después Storage borra el temporal. Desde ahí
`POST /files/{id}/download-url` de ese comprobante devuelve la URL pública, que el navegador **abre**
en vez de descargar con el nombre original. Un comprobante `User` sigue como antes. La respuesta de los
endpoints de pedidos no cambia.

Reemplazar el archivo de un comprobante (`updatedProofs[].newFileId`) o quitarlo
(`DELETE /orders/{orderId}/proofs/{proofId}`) borra, segundos después, el archivo que el pedido deja de usar: su
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
| `quotation.item.product_not_found` / `product_inactive` / `product_price_unavailable` | 422 | Producto inválido al agregar una línea, a la cotización o al pedido (también en `POST /orders`) |
| `quotation.item.duplicate_product` | 422 | El producto ya está en la cotización: se cambia la cantidad de su línea, no se agrega otra |
| `order.order.client_not_found` / `client_inactive` / `client_incomplete` / `client_cuc_missing` | 422 | `POST /orders` (y su `/preview`) con un cliente que no existe en el tenant, inactivo, incompleto o sin CUC |
| `order.order.items_required` | 422 | `POST /orders` sin líneas |
| `order.item.duplicate_product` | 422 | El producto ya está en el pedido (`POST /orders`, `PUT /orders/{orderId}`, `POST /orders/{orderId}/items`): se cambia la cantidad de su línea |
| `order.billing.account_required` | 422 | `POST /orders` sin cuenta de cobro |
| `order.billing.party_incomplete` / `order.shipping.party_incomplete` | 422 | La parte de facturación o de envío del pedido quedó incompleta |
| `order.billing.identification_required` | 422 | Una parte de facturación con nombre propio sin documento de identidad |
| `order.billing.tax_profile_required` | 422 | Una parte de facturación propia sin decir si practica retención y si tiene excedente de IVA |
| `order.order.source_invalid` | 422 | `GET /orders` o `POST /orders/export` con un `source` que no es `Quotation`, `Manual` ni `Shopify` |
| `order.order.not_pending` | 422 | El pedido ya está `Approved`, `Invoiced` o `Cancelled`: no admite comprobantes (`/orders/{orderId}/proofs`), productos (`/orders/{orderId}/items`), edición (`PUT /orders/{orderId}` y su `/preview`) ni otra aprobación |
| `concurrency.conflict` | 412 | `PUT /orders/{orderId}` con un `If-Match` que ya no es la `version` del pedido, o cualquiera de `/approve`, `/cancel`, `/invoice` y `/uninvoice` que choca con otro cambio simultáneo sobre el mismo pedido: otra persona lo cambió. Recargar |
| `precondition.if_match_required` | 428 | `PUT /orders/{orderId}` sin `If-Match` o con un valor que no es un número positivo |
| `order.order.not_found` | 404 | El `orderId` de la ruta no existe en este tenant. Lo devuelven todas las acciones de `/orders/{orderId}/…` |
| `order.order.already_cancelled` | 422 | `POST /orders/{orderId}/cancel` sobre un pedido ya `Cancelled` |
| `order.order.cancellation_reason_required` | 422 | `POST /orders/{orderId}/cancel` con el campo `reason` ausente, `null`, vacío o en blanco. Código de dominio, sin `errors`. Un request sin body no llega hasta acá: el binding lo rechaza con `400` |
| `order.order.cancellation_reason_too_long` | 422 | `POST /orders/{orderId}/cancel` con `reason` de más de 500 caracteres ya recortado. Código de dominio, sin `errors` |
| `order.order.already_invoiced` | 422 | `POST /orders/{orderId}/cancel` sobre un pedido `Invoiced`, con o sin motivo: primero se revierte la facturación |
| `order.order.not_approved` | 422 | `POST /orders/{orderId}/invoice` sobre un pedido que no está `Approved` (`Pending`, `Cancelled` o ya `Invoiced`) |
| `order.order.not_invoiced` | 422 | `POST /orders/{orderId}/uninvoice` sobre un pedido que no está `Invoiced` |
| `order.order.payment_proof_required` | 422 | `POST /quotations/{id}/order` o `POST /orders` sin comprobantes y el pago no es `PaymentPending`; en `POST /orders/{orderId}/proofs`, ni `paymentProofs` ni `updatedProofs` traen nada |
| `order.payment_proof.file_not_found` / `file_not_available` / `file_type_not_allowed` / `file_too_large` | 422 | Problema con un comprobante nuevo |
| `order.payment_proof.amount_invalid` | 422 | Un comprobante (nuevo o corregido en `updatedProofs`) con monto ≤ 0 |
| `order.payment_proof.not_found` | 422 | `updatedProofs` referencia un `proofId` que no es de este pedido |
| `validation.failed` | 422 | Errores de campo, viene con `errors` |

---

Fuente: `src/Modules/Quotations/Modules.Quotations.Api/*.cs`,
`src/Modules/Quotations/Modules.Quotations.Application/*Dtos.cs`,
`src/Api/ApiExceptionHandler.cs`.
