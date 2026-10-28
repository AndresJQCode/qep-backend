# Layout único para cotización y pedido, y la cotización que se crea en un paso

**Fecha:** 2026-10-18
**Módulos:** Quotations (crear la cotización con el documento completo y su cálculo previo sin id). **Frontend:**
`qep-frontend`, `features/quotes` (editor, alta, cáscara del documento), `features/orders` (alta y edición del
pedido, tarjeta de pago), `routes/_authenticated/quotes/new.tsx`, `routes/_authenticated/orders/$orderId/editar.tsx`.
**Estado:** borrador para la lectura del owner. Decisiones 1–4 tomadas con él el 2026-10-18; P1 abierta con valor
por defecto (§3). Pendiente el plan de implementación.
**Lo necesita:** nada. Va **después** de flete (`2026-10-17-flete-design.md`, ya en `develop`) y del renombre a
«Notas» (`feature/notas-y-quien-paga` en `qep-frontend`, commit `4bd27cd`), sobre los que se apoya.
**Ramas:** `feature/layout-documentos` desde `develop` en `qep-backend` (sólo §7); la homónima desde `develop` en
`qep-frontend`.

## 1. Contexto y alcance

Las cuatro pantallas de documento —crear y editar cotización, crear y editar pedido— no se parecen entre sí, y lo
que más choca es que **crear y editar el mismo pedido tienen layouts distintos**: «Nuevo pedido» usa la cáscara de
la cotización (encabezado pegado, columna principal y riel), «Editar pedido» usa la del detalle (tarjeta de
encabezado, tres tarjetas, entrega y productos al final). Guardas un pedido, le das «Editar» y todo cambió de
sitio. El propio código lo deja escrito (H5).

La cotización tiene el problema al revés: «Nueva cotización» no es un editor sino un paso previo. Eliges el
cliente, la cotización se crea sola en `Draft` y saltas al editor (H1). No hay cómo hacer que se parezca a
«Nuevo pedido» sin cambiar el backend, porque Quotations sólo sabe crear una cotización vacía y todo lo demás del
editor trabaja contra una cotización que ya existe (H9–H11).

**Dentro:**

- Una sola cáscara, `DocumentLayout`, para las cuatro pantallas, con el mismo encabezado, el mismo orden de
  bloques y el mismo apilado en móvil (§4, §5).
- Un solo patrón para decir qué falta para avanzar (§6).
- «Nueva cotización» como editor completo de un paso, igual que «Nuevo pedido» (§7 backend, §8.2 frontend).
- «Editar pedido» sobre la cáscara común, con su tabla de productos en `QuoteLinesEditor kind="order"` y el pago
  unificado en `OrderPaymentCard` (§8.3).
- Migración en pasos chicos, cada uno con las pruebas que lo protegen (§9).

**Fuera** (§11): el detalle del pedido y el asistente de conversión sobre la cáscara nueva, editar en el pedido
las partes, el cliente o la empresa que factura, y cualquier cambio de reglas de negocio de los documentos.

## 2. Hechos verificados en el código

Inventario del 2026-10-18. `qep-backend` en `develop` (`902c2d4b`); `qep-frontend` en `develop` (`6539c0a`,
verificado con `git branch --show-current`; los cambios sin commitear de ese checkout son de Messaging y Campaigns
y no tocan nada de esto). App = `src/Modules/Quotations/Modules.Quotations.Application`, Api =
`src/Modules/Quotations/Modules.Quotations.Api`, Inf = `src/Modules/Quotations/Modules.Quotations.Infrastructure`, FE = `qep-frontend/src`. Abreviaturas de archivo del frontend:
`qcp` = `features/quotes/pages/quote-create-page.tsx`, `qep` = `features/quotes/pages/quote-editor-page.tsx`,
`ocp` = `features/orders/pages/order-create-page.tsx`, `oep` = `features/orders/pages/order-edit-page.tsx`,
`qdl` = `features/quotes/components/quote-document-layout.tsx`.

| # | Hecho | Dónde |
| --- | --- | --- |
| H1 | «Nueva cotización» es una tarjeta con un combobox en `PageContainer variant="form"`. Elegir un cliente activo con CUC dispara `POST /quotations { clientId }` en un `useEffect`, sin botón, y al responder navega a `/quotes/$quoteId/editar`. | FE `qcp:42`; FE `routes/_authenticated/quotes/new.tsx:37-62` |
| H2 | «Nuevo pedido» es un editor completo de un paso sobre `QuoteDocumentLayout`: cliente, productos y pago en la columna; Entrega, Cobro, Facturación, Empresa que factura y Observaciones en el riel. Crea con un clic, **sin confirmación**, y navega al detalle. | FE `ocp:279-646` (riel `355,412,435,456,476`; Cliente `495`; `QuoteLinesEditor` `594`; `NewOrderPaymentCard` `615`); FE `routes/_authenticated/orders/new.tsx:197-220` |
| H3 | El editor de cotización usa la misma cáscara: Cliente, `QuoteLinesEditor` y totales en la columna; Vigencia, Entrega, Cobro, Facturación, Empresa que factura y «Nota» en el riel. | FE `qep:467-1112` (riel `651,683,744,765,790,815`; Cliente `839`; líneas `980`) |
| H4 | Qué falta, de tres maneras: el editor de cotización abre un modal (`QuoteSendRequirementsDialog`, y `QuoteConvertRequirementsDialog` para convertir) y al cerrarlo marca los campos; «Nuevo pedido» deja el botón activo y pone la nota en `actionsNote` bajo los botones, con marcas rojas; «Editar pedido» apaga el botón sin decir por qué. | FE `qep:387,1035-1055`; FE `ocp:331-341`; `features/orders/utils/new-order-draft.ts:192-226`; FE `oep:280,409` |
| H5 | «Editar pedido» arma su propia cáscara: `Card` pegada sólo desde `lg`, banda ámbar copiada a mano, título «Editar pedido PED-…» con subtítulo, avisos como `<p role="alert">` sueltos, `OrderSummaryCards`, una tarjeta «Entrega» propia y `OrderItemsCard` al final. El comentario dice: «No se refactoriza sobre `QuoteDocumentLayout` — son dos cáscaras distintas». | FE `oep:300,321-345,429-445,447,521-573,575` |
| H6 | El borrador de «Editar pedido» sólo lleva líneas, comprobantes nuevos, editados y quitados, notas, piso de escala, detal y envío (transportadora, quién paga, valor, contraentrega). No lleva cliente, partes, recoger/enviar ni cuenta de cobro. | FE `features/orders/types/order-edit-draft.ts:19-42`; backend `App/OrdersDtos.cs` (`SaveOrderEditsRequest`) |
| H7 | La cáscara común ya existe y trae `breadcrumbNav`, `actionsNote` (dentro de la banda pegada), `alerts`, `rail`, `railWidth` (`wide` = 380px) y `stickyRail`; acciones en grilla de dos en móvil. | FE `qdl:25-69,83,114,120-121,128-152` |
| H8 | Duplicados detrás de la diferencia: `handleDeliveryModeChange` (`qep:423` ≈ `ocp:236`), el diálogo «¿Descartar los cambios?» (`qep:1070-1102` ≈ `oep:651-679`), los pares `SaveQuoteEditsDialog`/`SaveOrderEditsDialog` y `UnsavedQuoteEditsDialog`/`UnsavedOrderEditsDialog`, las filas de totales de `OrderItemsCard` frente a `QuoteTotalsRows`, y tres interfaces de comprobantes (`NewOrderPaymentCard`, «Resumen de pago» de `OrderSummaryCards`, paso 1 de convertir). | FE `features/orders/components/order-items-card.tsx:168-251`; `features/quotes/components/quote-totals-summary.tsx`; `features/orders/components/order-summary-cards.tsx:282-452`; `features/quotes/pages/quote-convert-page.tsx` |
| H9 | `POST /quotations` acepta `CreateQuotationRequest(ClientId, ValidUntil, PaymentMethod, Notes, Parties, BillingAccount, Shipping)`: **sin líneas, sin piso de escala y sin detal**. Crea la cotización en `Draft`, consume el consecutivo, deja historial `Created` y audita `quotation.quotation.created`. El frontend sólo manda `clientId` y el encabezado vacío. | `Api/QuotationEndpoints.cs:49-54,243-265`; `App/QuotationsDtos.cs:245-253`; `App/CreateQuotation.cs:8-142`; FE `features/quotes/services/quotes.api.ts:146-175` |
| H10 | El editor trabaja contra una cotización que ya existe: `POST /quotations/{id}/preview` y `PUT /quotations/{id}` (con `If-Match`) llevan el documento entero (`SaveQuotationRequest`: encabezado, `Items`, `GlobalScaleFloor`, `IsRetail`, `Shipping`), y además están las rutas de línea por id. | `Api/QuotationEndpoints.cs:59-120,267-310`; `App/QuotationsDtos.cs:363-381` |
| H11 | El pedido tiene las dos piezas que a la cotización le faltan: `POST /orders/preview` sin id y `POST /orders` con el documento completo, que comparten cuerpo (`CreateOrderRequest`), validador base (`OrderDraftValidator`) y constructor (`OrderDraftBuilder`, que usa el número `"PREVIEW"` y el handler lo borra). | `Api/OrderEndpoints.cs:29-41,312-326`; `App/OrdersDtos.cs:90-103`; `App/CreateOrder.cs:23-60`; `App/PreviewOrder.cs:10-63`; `App/OrderDraftBuilder.cs:31-41` |
| H12 | El guardado y su cálculo previo comparten los pasos en `QuotationEditsApplication.ApplyAsync` (perfil tributario del cliente, cuenta de cobro, moneda, `UpdateDetails`, envío, diff de líneas, recálculo de escala y mínimo de compra) y las reglas en `QuotationEditsValidator`, que **no exige líneas** («una cotización nace sin líneas») y sólo difiere en el documento de la facturación (`requireBillingIdentification`). | `App/QuotationEditsApplication.cs:23-115`; `App/QuotationEdits.cs:22-58,96-155` |
| H13 | La respuesta la arma `QuotationResponseComposer` a partir del `QuotationDto`: cliente, asesor, empresa, productos, mínimo de compra, `CanBeSent`, `CanBeConvertedToOrder`, pisos disponibles, envío. No necesita que la cotización esté guardada. | `App/QuotationResponseComposer.cs:19-108` |
| H14 | Elegibilidad del cliente con sus códigos: `quotation.quotation.client_not_found`, `client_incomplete`, `client_cuc_missing`, `client_inactive`, en ese orden. Cuenta de cobro: `quotation.billing.company_not_found`, `quotation.billing.account_not_found`. | `App/QuotationCustomerEligibility.cs:25-49`; `App/QuotationBillingAccountResolver.cs:42,54` |
| H15 | Quién crea cotizaciones: sólo la pantalla `/quotes/new` del frontend (`useCreateQuote`, enlazada desde la lista) y los harness de pruebas de Quotations y Reporting. Messaging y Pipeline **no** crean cotizaciones: Pipeline reacciona a los eventos `sent`/`converted` y enlaza cotizaciones existentes a mano. Crear una cotización no publica evento de integración (sólo auditoría). | FE `features/quotes/hooks/use-create-quote.ts:10-15`, `features/quotes/pages/quotes-list-page.tsx:94`; `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:684-696`; `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs:458`; `src/Modules/Pipeline/Modules.Pipeline.Api/DealEndpoints.cs:35-36`; `Inf/Persistence/QuotationLifecycleEventPublisher.cs:17-20` |
| H16 | El detalle de la cotización ofrece PDF, «Editar» y «Convertir en pedido»; **enviar por WhatsApp sólo está en el editor**. | FE `features/quotes/pages/quote-detail-page.tsx:179,184,206`; FE `qep:561-580` |
| H17 | No existe `routes/_authenticated/quotes/new.test.tsx`; sí `quote-create-page.test.tsx`, `routes/_authenticated/orders/new.test.tsx` (554 líneas), `quote-editor-page.test.tsx` (1396), `order-create-page.test.tsx` (872), `order-edit-page.test.tsx` (1124), `quote-document-layout.test.tsx` y `use-new-order-preview.test.tsx`. | FE `src/` |

**Por qué hoy son dos pasos (la pregunta del owner).** No es una decisión de pantalla: el backend no tiene cómo
calcular una cotización que no existe ni cómo crearla con productos. `POST /quotations` sólo recibe el encabezado
(H9), y el cálculo previo, el guardado y las líneas piden un `{quotationId}` (H10). El frontend resolvió eso
creando el borrador en cuanto hay cliente y saltando al editor (H1). El pedido manual nació después y con las dos
piezas que faltan (H11), y por eso «Nuevo pedido» sí es un paso. Efecto que se ve en la base: **cada persona que
elige un cliente y se va deja una cotización `Draft` vacía**, con su número de consecutivo gastado.

## 3. Decisiones del owner (2026-10-18)

| # | Pregunta | Elegido |
| --- | --- | --- |
| 1 | ¿«Nuevo pedido» pide confirmación antes de crear? | **No.** Sigue creando directo, sin diálogo. |
| 2 | ¿Cómo se dice qué falta para avanzar? | **Un solo patrón en todas:** la nota bajo las acciones (`actionsNote`, el de «Nuevo pedido» hoy) más las marcas rojas en las secciones afectadas. Reemplaza el modal de requisitos del editor de cotización y el botón apagado sin explicación de «Editar pedido». |
| 3 | ¿«Nueva cotización» funciona como «Nuevo pedido»? | **Sí.** El documento entero visible desde el principio y creado de una vez con todo su contenido. Ya no se crea un borrador al elegir el cliente ni se salta al editor. Necesita el cambio de backend de §7. |
| 4 | ¿«Nota» u «Observaciones»? | **«Notas»** en todas (ya en `feature/notas-y-quien-paga`, `4bd27cd`). La columna «Observaciones» del Excel de pedidos no cambia: es contrato con el ERP. |

Contexto ya decidido antes de este spec, que el diseño respeta:

- «Editar pedido» deja en **solo lectura** recoger/enviar, las partes, el cliente y la empresa que factura: su
  borrador no los lleva (H6). Se muestran, no se editan.
- Su tabla de productos pasa a `QuoteLinesEditor kind="order"` y el pago se unifica en un solo `OrderPaymentCard`.
- Lo propio de la cotización (vigencia, envío por WhatsApp, PDF, cambiar el cliente, convertir, anular, aviso de
  compra mínima) y lo propio del pedido (comprobantes con la regla R16 de contraentrega, aprobar, imprimir, placa
  de origen) van **por props**, no por otra cáscara.

**Pendiente del owner** (el spec trae un valor por defecto):

- **P1. ¿«Anular pedido» se ofrece desde «Editar pedido», detrás de «⋯»?** Hoy sólo vive en el detalle. Por
  defecto **no** (D-L8): la edición es para cambiar el documento, y anular está a un clic en el detalle. Si el
  owner dice que sí, es un ítem del menú «⋯» con el mismo diálogo del detalle y bloqueado con cambios sin guardar,
  como «Anular cotización…» en el editor.

## 4. El layout común

### 4.1 Esqueleto

`DocumentLayout` sale de `QuoteDocumentLayout` (H7), renombrado y movido a `src/components/document-layout/`
porque deja de ser de cotizaciones. Mantiene sus props y suma dos ranuras de riel para el apilado en móvil.

```
┌────────────────────────────────────────────────────────────────────────────────┐
│ ENCABEZADO PEGADO (sangre completa, todos los anchos; ámbar con cambios)       │
│ Miga: Raíz › [Documento] › Tramo                                               │
│ [Título] [Placa estado] [Placa origen]          [Total]  [estado de guardado]  │
│                                   [Salir] [Documento] [Descartar] [Guardar]    │
│                                   [Acción principal] [⋯]                       │
│                                   Nota: qué falta para avanzar (actionsNote)   │
├────────────────────────────────────────────────────────────────────────────────┤
│ Avisos (QuoteAlert): vista previa, guardado, transición, cliente inactivo      │
├───────────────────────────────────────────────┬────────────────────────────────┤
│ COLUMNA PRINCIPAL (minmax(0,1fr))             │ RIEL (380px, sin pegar)        │
│  1. Cliente                                   │ railTop:                       │
│  2. Productos (QuoteLinesEditor)              │  R0. Documento (meta)          │
│  3. Totales / Pago                            │  R1. Entrega                   │
│                                               │  R2. Cobro                     │
│                                               │  R3. Facturación               │
│                                               │  R4. Empresa que factura       │
│                                               │ railBottom:                    │
│                                               │  R5. Notas                     │
└───────────────────────────────────────────────┴────────────────────────────────┘

Debajo de lg (una columna):
  Cliente → R0 → R1 → R2 → R3 → R4 → Productos → Totales/Pago → R5
```

### 4.2 Encabezado (el mismo en las cuatro)

- **Miga** con `DocumentBreadcrumb({ root, parent?, current })`, que reemplaza `QuoteBreadcrumb`,
  `OrderBreadcrumb` y el `<nav>` a mano de `oep:301-318`.
- **Título** = el identificador del documento (`QUO-…`, `PED-…`); en un alta, «Nueva cotización» o «Nuevo
  pedido». «Editar» lo dice la miga, no el título, como ya hace el editor de cotización. Sale el subtítulo de
  `oep:342-345`.
- **Placas:** estado en las ediciones; en «Editar pedido» también la de origen (`OrderSourceBadge`), que hoy sólo
  muestra el detalle.
- **Cifra:** `DocumentTotal` y `DocumentSaveStatus` en las cuatro. En las altas, «—» hasta la primera vista
  previa, en vez de esconder el total; sale el «Recalculando…» con `Loader2` hecho a mano de «Nuevo pedido».
- **Acciones, siempre en este orden:** **Salir** (outline: «Volver al detalle» en una edición, «Cancelar» en un
  alta) · **Documento** (outline: «Descargar PDF» o «Imprimir», bloqueada con `aria-disabled` si hay cambios) ·
  **Descartar cambios** (ghost, sólo con cambios, sólo en ediciones) · **Guardar cambios** (en las dos ediciones,
  mismo texto; llena con cambios) · **Acción principal** (llena sin cambios: «Enviar por WhatsApp», «Aprobar
  pedido», «Crear cotización», «Crear pedido») · **⋯** con lo raro o destructivo (Convertir, Anular).
- **`actionsNote`**: §6.
- En móvil, grilla de dos columnas para las acciones (H7).

### 4.3 Cuerpo

- Grilla `lg:grid-cols-[minmax(0,1fr)_380px]`, riel sin pegar (es un formulario).
- **Todo bloque es `DocumentSection`** (`QuoteSection` renombrado), con título, resumen y `needsAttention`.
  Desaparecen el `SectionCard` de `OrderSummaryCards` y la tarjeta en mayúsculas de `oep:521-525` en la edición.
- **Apilado en móvil a propósito.** Hoy el riel cae entero al fondo y en un celular la entrega y la facturación
  quedan debajo de una tabla de productos de 54rem. Con dos ranuras (`railTop`, `railBottom`) el orden debajo de
  `lg` es: Cliente → R0–R4 → Productos → Totales/Pago → R5.
- **Cargando y error** con un solo `DocumentPageState` (`PageContainer` + `Card` con «Reintentar»), que reemplaza
  el `<main className="mx-auto max-w-5xl p-8">` sin reintentar de
  `routes/_authenticated/quotes/$quoteId/editar.tsx` y los tres estados a mano de las otras pantallas.

## 5. Orden de bloques por pantalla

CQ = crear cotización (`/quotes/new`), EQ = editar cotización (`/quotes/$quoteId/editar`), CP = crear pedido
(`/orders/new`), EP = editar pedido (`/orders/$orderId/editar`).

| Ranura | CQ | EQ | CP | EP |
| --- | --- | --- | --- | --- |
| Título / placas | «Nueva cotización» | `QUO-…` · estado | «Nuevo pedido» | `PED-…` · estado · origen |
| Cifra | Total («—» sin vista previa) + estado | Total + estado | Total a pagar + estado | Total a pagar + estado |
| Acciones | Cancelar · **Crear cotización** | Volver al detalle · Descargar PDF · [Descartar] · Guardar cambios · Enviar por WhatsApp · ⋯ (Convertir, Anular) | Cancelar · Crear pedido | Volver al detalle · Imprimir · [Descartar] · Guardar cambios · Aprobar pedido · [⋯ Anular, si P1 = sí] |
| 1. Cliente | combobox → ficha + Cambiar (sin confirmación) + asesor + Detal + Descuento global | ficha + asesor + Cambiar el cliente (con confirmación) + Detal + Descuento global | combobox → ficha + Cambiar (sin confirmación) + **asesor** + Detal + Descuento global | ficha **sin** «Cambiar» + asesor + Detal + Descuento global |
| 2. Productos | `QuoteLinesEditor` + `ProductPickerDialog`, con aviso de compra mínima; antes del cliente, deshabilitado con «Elige el cliente para agregar productos» | `QuoteLinesEditor` + `ProductPickerDialog` | `QuoteLinesEditor kind="order"` | `QuoteLinesEditor kind="order"` con `unsavedItemIds` (reemplaza `OrderItemsCard`) |
| 3. Totales / Pago | `QuoteTotalsSummary` | `QuoteTotalsSummary` | `OrderPaymentCard` (totales + comprobantes nuevos) | `OrderPaymentCard` (totales + comprobantes guardados y nuevos, editar/quitar, excedente) |
| R0. Documento | **Vigencia** (15 días por defecto, editable) | **Vigencia** | — | **Información del pedido** (fecha, cotización de origen, aprobado el), solo lectura |
| R1. Entrega | Recoger/Enviar + dirección + flete | igual | igual | **modo y dirección en solo lectura**; el flete sí se edita |
| R2. Cobro | `QuotePaymentMode` | igual | igual + bloqueo R16 por comprobantes | igual + bloqueo R16 por comprobantes |
| R3. Facturación | `QuoteBillingParty` | igual | igual | **solo lectura** |
| R4. Empresa que factura | `QuoteBillingCard` | igual | igual | **solo lectura** |
| R5. Notas | «Notas» | «Notas» | «Notas» | «Notas» (sale de «Información del pedido») |
| Diálogos | salir sin guardar | guardar, descartar, salir, anular, cambiar cliente | salir sin guardar | guardar, descartar, salir, agregar/editar comprobante |

En CQ y CP, antes de elegir el cliente, R1–R4 muestran «Elige el cliente para ver su facturación y entrega»
(el texto que CP ya usa, `ocp:220-223`).

## 6. «Qué falta para avanzar»: un solo patrón

Decisión 2 del owner. Las cuatro pantallas hacen lo mismo que «Nuevo pedido» hoy (H4):

1. **El botón de la acción no se apaga** por datos faltantes. Queda activo (o con `aria-disabled`, si lo bloquea un
   estado y no un dato) y un clic con algo faltante no llama a la API.
2. **La nota va en `actionsNote`**, dentro de la banda pegada, bajo las acciones, con `role="alert"` y enlazada al
   botón por `aria-describedby`: «Faltan datos para <acción>: el cliente, al menos un producto y la empresa y la
   cuenta que factura.» La lista sale de una función pura por pantalla (como `newOrderIssueLabels`,
   `features/orders/utils/new-order-draft.ts:216`), en el orden en que la pantalla muestra los bloques.
3. **Las marcas rojas** (`needsAttention` de `DocumentSection`) se prenden en cada sección afectada desde el primer
   intento, no al cerrar un modal.
4. La nota se calla sola cuando el último faltante se resuelve.

Qué cambia en cada pantalla:

| Pantalla | Acción | Hoy | Con este spec |
| --- | --- | --- | --- |
| EQ | Enviar por WhatsApp | `QuoteSendRequirementsDialog` y marcas al cerrar | nota + marcas; el modal se borra |
| EQ | Convertir en pedido (⋯) | `QuoteConvertRequirementsDialog` y marcas al cerrar | nota + marcas; el modal se borra (D-L5) |
| EQ | Enviar o Descargar PDF con cambios | `aria-disabled` + hint propio | igual, con el texto en la nota |
| CQ | Crear cotización | — | nota + marcas (cliente, al menos un producto, D-L3) |
| CP | Crear pedido | nota + marcas | sin cambio |
| EP | Guardar cambios | botón apagado sin explicación (`canSave`, `oep:280`) | sin cambios sigue apagado (no hay nada que guardar); recalculando o con error de la vista previa, `aria-disabled` y la nota dice por qué |
| EP | Aprobar pedido | bloqueado con cambios | `aria-disabled` y la nota: «Guarda los cambios antes de aprobar.» |

El patrón de `aria-disabled` repetido en cinco archivos pasa a un `BlockedButton({ reason })` (§8.1).

## 7. Backend: crear la cotización en un paso

### 7.1 Qué se elige y por qué

Se **extiende `POST /quotations`** con lo que le falta para llevar el documento completo, y se agrega
`POST /quotations/preview` sin id. No se abre una ruta de creación nueva.

- **Es el mismo recurso, el mismo permiso y la misma respuesta.** `CreateQuotationRequest` ya lleva todo el
  encabezado (H9); le faltan tres campos que `SaveQuotationRequest` ya tiene (H10). Una ruta gemela duplicaría el
  handler, la numeración, el historial y la auditoría, y habría dos maneras de crear una cotización que se
  desincronizan a la primera que alguien toque.
- **El contrato viejo sigue funcionando sin cambiar una coma.** Los tres campos nuevos son opcionales y ausentes
  dan exactamente lo de hoy: sin líneas, sin piso, no detal. `{ clientId }` sigue creando un borrador vacío. Así
  el despliegue del backend no depende del frontend y los harness de pruebas (H15) siguen compilando: son
  parámetros nuevos al final del record con valor por defecto.
- **Es el mismo par que el pedido** (H11): `POST /orders` + `POST /orders/preview`, con el mismo cuerpo. La
  pantalla de alta de la cotización se arma con la misma mecánica que la del pedido.

El contrato `{ clientId }` **no se retira**: su único consumidor real, `/quotes/new` del frontend, deja de usarlo
en §8.2, pero quitarlo no ahorra nada (es el mismo endpoint) y obligaría a reescribir los harness de Quotations y
Reporting. Messaging y Pipeline no crean cotizaciones (H15).

### 7.2 Contrato

Bajo `/api/v1/tenants/{tenantId:guid}`, permiso `quotations.quotation.manage` en los dos (el mismo de hoy, sin
permiso nuevo):

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `POST /quotations` | `CreateQuotationRequest` extendido | `201` + `Location` + `QuotationResponse` (como hoy) |
| `POST /quotations/preview` | el mismo `CreateQuotationRequest` | `200` + `QuotationResponse` con `id = 00000000-…`, `quotationNumber = ""`, `version = 0` |

```json
{
  "clientId": "…",
  "validUntil": null,
  "paymentMethod": null,
  "notes": "…",
  "parties": { "billing": null, "shipping": { … }, "billingUsesBusinessName": false, "isStorePickup": false, "billsToFinalConsumer": false },
  "billingAccount": { "companyId": "…", "bankName": "…", "accountNumber": "…", "currency": "COP" },
  "shipping": { … },
  "items": [ { "productId": "…", "quantity": 2 } ],
  "globalScaleFloor": null,
  "isRetail": false
}
```

```csharp
public sealed record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    ShippingRequest? Shipping = null,
    // Nuevos (spec 2026-10-18). Ausentes = lo de hoy: sin líneas, sin piso, no detal.
    IReadOnlyList<QuotationEditItemRequest>? Items = null,
    int? GlobalScaleFloor = null,
    bool IsRetail = false);
```

`Items` lleva producto y cantidad, igual que el guardado: precio, escala, descuentos de grupo y tasa los resuelve
el backend. La nota BFF va en el XML del DTO: «crear y calcular sin id existen para que "Nueva cotización" pinte
el documento entero antes de que exista; sin esto la pantalla tiene que crear un borrador para poder calcularlo».

`/preview` no choca con `/{quotationId:guid}`: la restricción de guid no lo acepta, igual que `/export`
(`Api/QuotationEndpoints.cs:23-28`).

### 7.3 Reglas: las mismas del guardado

- **Un solo constructor, `QuotationDraftBuilder`** (como `OrderDraftBuilder`, H11), que usan crear y calcular.
  Arma el agregado en memoria con `Quotation.Create` (el número real al crear; `"PREVIEW"` en el cálculo previo,
  que el handler borra antes de responder) y le aplica **`QuotationEditsApplication.ApplyAsync`** con el cuerpo
  entero: perfil tributario, cuenta de cobro y moneda, encabezado, envío, líneas, escala y mínimo de compra
  (H12). Crear, calcular, guardar y calcular el guardado pasan por los mismos pasos en el mismo orden, así que
  el total del cálculo previo es el que la creación deja.
- **Validación de campo con FluentValidation.** `CreateQuotationCommand` y `PreviewNewQuotationQuery`
  implementan `IQuotationEdits` y sus validadores heredan de `QuotationEditsValidator` (H12), más
  `ClientId NotEmpty`: el de crear con `requireBillingIdentification: true` (como hoy, `CreateQuotation.cs:26-28`),
  el del cálculo previo con `false`. Las líneas repetidas, la cantidad ≤ 0 y el producto vacío dan
  `422 validation.failed` con el mapa `errors`. **No se exigen líneas** (H12; D-L3 explica dónde se pide al
  menos una).
- **Códigos de dominio**: los mismos que el guardado, sin códigos nuevos. Cliente: los cuatro de H14, en ese
  orden, en crear **y** en el cálculo previo (como el pedido manual, que no relaja la elegibilidad en su
  preview). Cuenta de cobro, envío, detal con piso (`quotation.retail.floor_not_allowed`), producto repetido
  (`quotation.item.duplicate_product`) y los de valorización de línea, los que ya lanza `ApplyAsync`.
- **Doble capa de tenant.** Ruta con `{tenantId:guid}` y política del permiso; los dos handlers llaman a
  `QuotationsAuthorization.EnsureAuthorized` antes de validar (`CreateQuotation.cs:58-60`). Cliente, productos y
  empresa se buscan con `tenantId`: un id de otro tenant en el cuerpo no se encuentra y da el 422 de «no
  encontrado» de cada uno, como hoy en el guardado; no hay id de cotización en la ruta, así que no hay 404 que
  esconder.
- **El cálculo previo no escribe nada:** no consume consecutivo, no deja historial, auditoría ni outbox, y no
  llama a `SaveChangesAsync`. Vigencia ausente = hoy del tenant + 15 días, igual que al crear
  (`CreateQuotation.cs:100`), para que la pantalla muestre la fecha que va a quedar.
- **Asesor** = la membresía activa de quien crea (`QuotationAdvisorResolver`), como hoy. La respuesta trae su
  nombre, así que CQ lo pinta sin otra llamada.

### 7.4 Historial y auditoría

Crear con líneas deja **lo mismo que hoy dejaban crear y después guardar en el editor**, sin la fila «Edited» del
encabezado (el encabezado es parte de la creación): una fila `Created` (`QuotationChangeSummary.Created`) y una
fila `Edited` con `ItemAdded` por línea; auditoría `quotation.quotation.created` y una
`quotation.quotation.item_added` por línea (`SaveQuotation.cs:127-173`). Todo en el mismo `SaveChangesAsync`
(D-L2). Sin líneas, sólo `Created`, como hoy.

No hay evento de integración nuevo: crear una cotización no publica nada a Pipeline (H15) y sigue sin hacerlo.

### 7.5 Efecto que se ve

- **No quedan más borradores vacíos** de quien elige un cliente y se va: sin clic en «Crear cotización» no se
  escribe nada ni se gasta un número del consecutivo. Los que ya existen se quedan como están (§11).
- Los borradores vacíos se siguen pudiendo crear por la API con `{ clientId }`, pero ninguna pantalla lo hace.

## 8. Frontend (`qep-frontend`)

### 8.1 Componentes

| Componente | Sale de | Lo usan |
| --- | --- | --- |
| `DocumentLayout` | `QuoteDocumentLayout` + `railTop`/`railBottom` + miga genérica | las cuatro y el detalle de la cotización |
| `DocumentBreadcrumb` | `QuoteBreadcrumb` + `OrderBreadcrumb` + `oep:301-318` | todas |
| `DocumentSection` | `QuoteSection`, renombrado | todos los bloques |
| `DocumentCustomerSection` | `qep:839-975` + `ocp:495-590`; `mode: 'pick' \| 'change-confirmed' \| 'change-free' \| 'fixed'` | CQ (`pick`→`change-free`), EQ (`change-confirmed`), CP (`pick`→`change-free`), EP (`fixed`) |
| `DocumentDeliverySection` | `qep:683-743` + `ocp:355-411` + `handleDeliveryModeChange`; prop `modeLocked` | CQ, EQ, CP, EP |
| `DocumentPaymentModeSection` | `qep:744-764` + `ocp:412-434` + `oep:548-570`; prop `blockedReason` (R16) | CQ, EQ, CP, EP |
| `DocumentBillingSections` | `qep:765-814` + `ocp:435-475`; prop `readOnly` | CQ, EQ, CP, EP |
| `DocumentNotesSection` | `qep:815-835` + `ocp:476-494` + `OrderNotesField`; título «Notas» | las cuatro |
| `QuoteLinesEditor` | ya existe; suma `unsavedItemIds` y pie opcional de «Términos comerciales» | las cuatro |
| `QuoteTotalsRows` | ya existe con `kind`; retira las filas `TotalRow` de `order-items-card.tsx:168-251` | las cuatro |
| `OrderPaymentCard` | `NewOrderPaymentCard` + «Resumen de pago» de `order-summary-cards.tsx:282-452` | CP, EP (y después el paso 1 de convertir, §11) |
| `SaveDocumentEditsDialog`, `UnsavedChangesDialog`, `DiscardChangesDialog` | los pares duplicados (H8) | EQ, EP, CQ, CP |
| `BlockedButton({ reason })` | la clase de `aria-disabled` repetida | EQ, EP, `ApproveOrderButton`, `ExportByEmailButton` |
| `DocumentPageState` | los estados de cargando y error | todas |

`DocumentNotesSection` guarda **al salir del campo** en las cuatro (hoy EQ y CP en cada tecla, EP al salir):
la nota viaja en la vista previa, y una vista previa por tecla no le aporta nada a un campo que no mueve el total
(D-L6).

`OrderSummaryCards` y `OrderItemsCard` se quedan **sólo en el detalle del pedido**; sus props de edición
(`addProofsAction`, `onEditProof`, `onNotesChange`, `customerAction`, `onUpdateQuantity`…) se borran cuando EP
deje de usarlas.

### 8.2 «Nueva cotización»

- `quote-create-page.tsx` pasa a ser un editor sobre `DocumentLayout`, armado como `ocp`: borrador local
  (`useNewQuoteDraft`, espejo de `use-new-order-draft.ts`), cálculo previo con `useNewQuotePreview` (espejo de
  `use-new-order-preview.ts`, sobre `use-debounced-preview.ts`) contra `POST /quotations/preview`, y
  `createQuote` con el cuerpo completo (`toQuoteRequestBody` suma `items`, `globalScaleFloor`, `isRetail` y
  `shipping`, que hoy no manda, `quotes.api.ts:160-175`).
- El cálculo previo arranca cuando hay cliente; sin cliente, el total es «—» y las secciones muestran su
  marcador.
- **«Crear cotización»** crea sin confirmación (como CP, decisión 1), con el mismo candado contra el doble clic de
  `routes/_authenticated/orders/new.tsx:197-220` (`submittingRef`, `isSuccess` cuenta como enviando), y navega
  al **editor** `/quotes/$quoteId/editar` con el toast «Cotización QUO-… creada.» (D-L4): ahí está «Enviar por
  WhatsApp», que es lo siguiente (H16), y es la misma URL a la que se llega hoy.
- Salir con algo cargado pide confirmación con `UnsavedChangesDialog`, como CP.
- Se borran el `useEffect` que creaba solo (`new.tsx:55-62`), el «Reintentar» del alta y la tarjeta angosta.

### 8.3 «Editar pedido»

- Pasa a `DocumentLayout`. Título `PED-…`, placas de estado y origen, banda a sangre completa, acciones en el
  orden común.
- R1 muestra recoger/enviar y la dirección en solo lectura (`QuotePartySummary`) y deja editar el flete; hoy la
  dirección no se ve (`oep:526-537`). R3 y R4 en solo lectura; hoy no aparecen, salvo «Se factura por».
- Productos en `QuoteLinesEditor kind="order"` con `unsavedItemIds`: gana «Precio con descuento» y «Ver escalas»,
  como CP.
- Pago en `OrderPaymentCard`: comprobantes guardados con editar y quitar, nuevos, excedente contra el neto, R16 y
  lo que «Aprobar» necesita leer.
- «Imprimir» sigue imprimiendo la pantalla; marcar `data-print-region` en la edición queda fuera (§11).

## 9. Migración en pasos chicos

La regla: cada paso deja verdes las pruebas que ya existen sin reescribirlas, salvo donde se dice, y va en su
propio commit con RED antes que GREEN. Se corren sólo los archivos de la última columna
(`bunx vitest run <archivos>` en el frontend; `dotnet test` del proyecto de Quotations en el backend), no la suite
completa.

| # | Paso | Pantallas | Riesgo | Qué lo protege |
| --- | --- | --- | --- | --- |
| B1 | **Backend: crear y calcular sin id.** `CreateQuotationRequest` extendido, `QuotationDraftBuilder`, `PreviewNewQuotationQuery` + validador + `POST /quotations/preview`; `CreateQuotationHandler` sobre el builder | — | **Medio**: toca la creación de todas las cotizaciones. El contrato viejo tiene que dar lo mismo byte a byte | RED primero: `CreateQuotationHandlerTests` y `QuotationApiTests` con líneas, piso y detal; las pruebas de creación existentes sin tocar (`QuotationApiTests`, `QuotationShippingApiTests`, `IncompleteCustomerQuotationApiTests`, `QuotationBillingIdentificationContractTests`); `ReportingApiHarness` compilando sin cambios |
| F0 | Extraer `DiscardChangesDialog`, `SaveDocumentEditsDialog`, `UnsavedChangesDialog` y `BlockedButton`, con los textos de hoy por props | EQ, EP, CP | **Bajo**: mismo DOM | `save-quote-edits-dialog.test.tsx`, `save-order-edits-dialog.test.tsx`, `unsaved-*-edits-dialog.test.tsx`, `quote-editor-page.test.tsx`, `order-edit-page.test.tsx`, `approve-order-button.test.tsx` |
| F1 | Extraer `DocumentDeliverySection`, `DocumentPaymentModeSection`, `DocumentBillingSections`, `DocumentNotesSection` y `DocumentCustomerSection` **desde EQ y CP**, que ya comparten layout | EQ, CP | **Bajo-medio**: diferencias que preservar (asesor, confirmación al cambiar el cliente, `savedCarrierName`, R16) | `quote-editor-page.test.tsx`, `order-create-page.test.tsx`, `quote-delivery-mode.test.tsx`, `quote-payment-mode.test.tsx`, `quote-billing-*.test.tsx`, `routes/_authenticated/orders/new.test.tsx` |
| F2 | `QuoteDocumentLayout` → `DocumentLayout` con `DocumentBreadcrumb`, `railTop`/`railBottom` y `DocumentPageState`. Los `data-testid` `quote-document-*` se mantienen por ahora | EQ, CP, detalle de cotización | **Bajo**, salvo el orden en móvil, que hay que mirar a mano | `quote-document-layout.test.tsx`, `quote-detail-page.test.tsx`, `quote-breadcrumb.test.tsx`; **nueva**: el orden de apilado debajo de `lg` |
| F3a | **EP, la cáscara.** `DocumentLayout` con el contenido actual en la columna, sin riel todavía. Título `PED-…`, sin subtítulo, placa de origen, acciones en el orden común | EP | **Medio**: cambia el encabezado | `order-edit-page.test.tsx` (se actualizan las aserciones sobre `order-edit-header`), `routes/_authenticated/orders/$orderId/editar.test.tsx` |
| F3b | **EP, el riel.** Entrega (modo fijo + flete), Cobro, Facturación y Empresa en solo lectura, y Notas, con las secciones de F1 (`modeLocked`, `readOnly`). «Información del pedido» pasa a R0 | EP | **Medio**: R16 y el bloqueo por comprobantes tienen que seguir igual | las pruebas de contraentrega y comprobantes de `order-edit-page.test.tsx`; **nuevas**: Facturación, Empresa y dirección se ven y no se editan |
| F3c | **EP, productos.** `OrderItemsCard` editable → `QuoteLinesEditor kind="order"` con `unsavedItemIds` | EP | **Medio-alto**: cambian columnas y textos que las pruebas buscan («Sin guardar», «N ítems») | RED primero: EP muestra «Precio con descuento»; `order-edit-page.test.tsx` (líneas), `quote-lines-editor.test.tsx` |
| F3d | **EP y CP, pago.** `NewOrderPaymentCard` → `OrderPaymentCard` con comprobantes guardados (editar, quitar, «Sin guardar», excedente). `OrderSummaryCards` pierde sus props de edición | EP, CP | **Alto**: el bloque con más reglas (excedente contra el neto, cupo de comprobantes, R16, «Usar el faltante») | `order-edit-page.test.tsx` (comprobantes), `order-summary-cards.test.tsx`, `order-create-page.test.tsx` (pago), `add-order-payment-proofs-dialog.test.tsx`, `edit-payment-proof-dialog.test.tsx` |
| F4 | **«Qué falta» único** (§6). Se borran `QuoteSendRequirementsDialog` y `QuoteConvertRequirementsDialog`; EP deja de apagar en silencio | EQ, EP | **Medio**: cambia el comportamiento, no sólo la forma | RED primero en `quote-editor-page.test.tsx`: enviar con faltantes muestra la nota y las marcas **sin** modal; ídem convertir; en `order-edit-page.test.tsx`, aprobar con cambios dice por qué; `order-create-page.test.tsx` sin cambios |
| F5 | **«Nueva cotización» en un paso** (§8.2). **Depende de B1 desplegado** | CQ | **Medio**: cambia el flujo de entrada a todas las cotizaciones | **Antes de tocar nada**, `routes/_authenticated/quotes/new.test.tsx` (no existe, H17) con el comportamiento nuevo en RED: elegir cliente no llama a la API; «Crear cotización» manda el cuerpo completo una sola vez (doble clic) y navega al editor; error con la nota. Después `quote-create-page.test.tsx` reescrita y `use-new-quote-preview.test.tsx` |

**Por dónde empezar:** B1 en el backend en paralelo con F0 → F1 → F3a en el frontend. Con F3a ya se ve lo que el
owner pide de «Editar pedido» sin tocar reglas de pago. F3c y F3d son los de más riesgo. F5 espera a que B1 esté
en el ambiente donde se prueba: el frontend nuevo contra un backend viejo crearía cotizaciones sin líneas (el
backend viejo ignora `items`), así que el orden de despliegue es **backend primero** (D-L7).

## 10. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias (Quotations):** `CreateQuotationHandlerTests` con líneas (precio y escala resueltos por el backend),
con piso, con detal, detal + piso (`quotation.retail.floor_not_allowed`), producto repetido (validador), sin
líneas igual que hoy; historial `Created` + una `ItemAdded` por línea y la auditoría de §7.4;
`PreviewNewQuotationHandlerTests`: no consume consecutivo (el generador no se llama), no escribe, devuelve
`Id = Guid.Empty` y número vacío, la vigencia por defecto, el mínimo de compra en la respuesta, el documento de la
facturación no exigido, la elegibilidad del cliente sí; un mismo cuerpo da el mismo total en el cálculo previo y
en la creación.

**Integración (Quotations):** `POST /quotations` con el cuerpo completo (201, líneas en la respuesta, totales);
`{ clientId }` igual que hoy; `POST /quotations/preview` (200, sin fila en `quotations.quotations`); 403 con el
`tenantId` de otro tenant en la ruta en los dos; 403 sin `quotations.quotation.manage` (pedido por
`X-Permissions`, no 500: la política ya existe); producto de otro tenant en `items` → el 422 que da hoy el
guardado; cliente incompleto → `client_incomplete` en los dos.

**Frontend:** las de la última columna de §9, con las nuevas marcadas allí.

**Sin barrido de cuerpos crudos:** los tres campos nuevos son opcionales; ninguna prueba que arma
`CreateQuotationRequest` a mano cambia de resultado.

**Arquitectura:** sin capas ni proyectos nuevos; `QuotationsLayerTests` sin cambios.

## 11. Fuera de alcance

- El detalle del pedido y el paso 1 de convertir sobre `DocumentLayout` + `OrderPaymentCard` (impresión con
  `data-print-region`, el asistente de tres pasos). Va después, con su propio spec o plan.
- Editar en el pedido recoger/enviar, la dirección, las partes, el cliente o la empresa que factura.
- Limpiar los borradores vacíos que ya existen.
- Retirar el contrato `{ clientId }` de `POST /quotations` o las rutas de línea por id.
- Cambiar alguna regla de negocio de los documentos (totales, R16, mínimo de compra, elegibilidad).

## 12. Decisiones tomadas sin el owner (a ratificar)

| ID | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-L1 | Se **extiende** `POST /quotations` en vez de abrir una ruta nueva, y el contrato `{ clientId }` se conserva | Una ruta más y un handler duplicado, o una prueba de compatibilidad que se borra |
| D-L2 | Crear con líneas deja `Created` + una `ItemAdded` por línea y una auditoría `item_added` por línea, como crear y después guardar | Una sola fila `Created` con el resumen: cambiar `RecordItemEditsAsync` en la creación y sus pruebas |
| D-L3 | El backend **no exige** líneas al crear (como hoy y como el guardado); la pantalla pide al menos un producto antes de «Crear cotización» con la nota de §6 | Exigirlo en el validador de crear: un `Must` y barrer los harness que crean sin líneas |
| D-L4 | Después de crear, la pantalla va al **editor** de la cotización (donde está enviar), no al detalle como el pedido | Cambiar el destino del `navigate` |
| D-L5 | El modal de requisitos de **convertir** también se reemplaza por la nota, no sólo el de enviar | Dejar ese modal: no borrar un componente |
| D-L6 | «Notas» guarda al salir del campo en las cuatro pantallas | Volver a guardar en cada tecla en CQ, EQ y CP |
| D-L7 | Despliegue: backend primero; el frontend de F5 no sale antes | Un frontend nuevo contra un backend viejo crea cotizaciones sin líneas, sin error |
| D-L8 | P1 por defecto en **no**: «Anular pedido» sigue sólo en el detalle | Un ítem en «⋯» con el diálogo que ya existe |
| D-L9 | El cálculo previo sin id aplica la elegibilidad del cliente (no la relaja), como el del pedido manual | Relajarla: un parámetro en el builder |
| D-L10 | En CQ, cambiar el cliente es libre (sin confirmación), como en CP: no hay nada guardado que perder | Un diálogo como el de EQ |

## 13. Entregables

1. Backend: `CreateQuotationRequest` extendido, `QuotationDraftBuilder`, `POST /quotations/preview`, pruebas de
   §10, la sección de cotizaciones del README y la guía de integración del frontend
   (`docs/integracion-cotizaciones-y-pedidos.md`, en este repo) con el contrato nuevo.
2. Frontend: los pasos F0–F5 de §9.
3. Plan de implementación (`writing-plans`) tras la lectura del owner, uno por repositorio.

## Historial de revisión

- 2026-10-18: auditoría de layout de las cuatro pantallas (comparación con `archivo:línea`, diferencias,
  propuesta, ruta de migración) y decisiones 1–4 tomadas con el owner. Spec escrito con los hechos verificados
  contra `qep-backend` `902c2d4b` y `qep-frontend` `6539c0a` (las líneas del editor y de «Nuevo pedido» se
  movieron entre 4 y 7 respecto de la auditoría, hecha sobre `4d1cf05`). P1 abierta con su valor por defecto;
  pendientes la lectura del owner y el plan.
