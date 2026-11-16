# Cotizaciones y pedidos — Contenido configurable del PDF · diseño (backend)

> **Estado:** aprobado por el owner 2026-11-16.
> **Diseño visual:** canvas «Editor de contenido del PDF» (https://claude.ai/artifact/AtFCKnMY6pVCZUCPsRFcEH).
> **Consola:** spec propio en `qep-frontend` (`docs/superpowers/specs/2026-11-16-pdf-content-design.md`), construido contra §4.
> **Precedente que se copia:** variables de los documentos legales de Soporte (spec `2026-11-16-support-legal-document-variables-design.md`).

## 1. Problema

El PDF de cotizaciones y pedidos (`Modules.Quotations.Infrastructure/Pdf/quotation.typ`, una sola plantilla para los dos) imprime dos
textos escritos a mano en la plantilla, iguales para todos los tenants:

- el aviso «Ten en cuenta: no aceptamos pagos por corresponsal bancario…», debajo de la cuenta para el pago (`quotation.typ:255-262`);
- la segunda página «Proceso de Alistamiento y Recepción de Pedidos» (`quotation.typ:485-536`).

Los dos son la política de **Origen Botánico** (owner, 2026-11-16) y hoy les salen a todos los tenants. Además ningún tenant puede
poner sus propios términos.

## 2. Decisiones del owner (2026-11-16)

| # | Decisión |
| --- | --- |
| P-1 | Cada tenant prende y apaga las secciones del PDF que hoy son fijas (aviso de pago, proceso de entrega) y agrega sus propios términos. |
| P-2 | El texto se compone con el editor enriquecido que ya usa Soporte (TipTap en la consola, HTML saneado en el backend). |
| P-3 | Los textos aceptan variables `{{nombre}}` (ej. `{{empresa}}`), con la lista de §4.3. |
| P-4 | Los textos actuales son sólo de Origen Botánico (`tenancy.tenants.slug = 'origen-botanico'`): ese tenant los conserva prendidos; los demás arrancan con todo apagado. |

## 3. Objetivos, no objetivos y anti-objetivos

**Objetivos.** Tres secciones configurables por tenant; cada una se prende, se marca para cotización y/o pedido, y lleva título y
texto enriquecido con variables. El PDF de Origen Botánico sale igual que hoy después del despliegue.

**No objetivos.** Mover secciones de lugar; más de un bloque de términos; imágenes o tablas dentro del texto; cambiar el resto del
layout (encabezado, ítems, totales); un editor de plantilla Typst.

**Anti-objetivos (sería un fracaso aunque funcione).**
- Que un texto del tenant pueda inyectar markup de Typst o romper la plantilla.
- Que un PDF ya generado siga mostrando términos viejos después de que el tenant los cambie.
- Que un token mal escrito (`{{Empresa}}`) llegue al PDF como texto literal.

## 4. Contrato

### 4.1 Secciones

| `key` | Nombre en pantalla | Dónde sale | Condición extra |
| --- | --- | --- | --- |
| `paymentNotice` | Aviso de pago | Debajo del recuadro de condiciones de pago, en rojo (`naranja` de la plantilla), como hoy | Sólo si el documento tiene cuenta para el pago (`billingAccount != null`), como hoy |
| `terms` | Términos y condiciones | Después del bloque de totales, en la primera página (fluye a la siguiente si no cabe) | — |
| `deliveryProcess` | Proceso de entrega | Página aparte al final (`#pagebreak()`), como hoy | — |

Cada sección guarda: `enabled`, `inQuotation`, `inOrder`, `title` (texto plano, ≤ 120) y `contentHtml` (HTML saneado, ≤ 20 000 después
de sanear). Una sección sale en un documento si `enabled` y el flag del tipo de documento está prendido. Apagar no borra el texto.

### 4.2 Endpoints

`GET /api/v1/tenants/{tenantId}/quotations/settings/pdf-content` — `TenancyPermissions.SettingsRead` (el mismo de
`QuotationSettingsEndpoints`).

```json
{
  "paymentNotice":   { "enabled": true,  "inQuotation": true, "inOrder": true,  "title": "Ten en cuenta:", "contentHtml": "<p>…</p>" },
  "terms":           { "enabled": false, "inQuotation": true, "inOrder": false, "title": "", "contentHtml": "" },
  "deliveryProcess": { "enabled": true,  "inQuotation": true, "inOrder": true,  "title": "Proceso de …", "contentHtml": "<p>…</p>" },
  "variables": [ { "name": "empresa", "label": "Empresa que factura", "example": "Comercial Andina S.A.S." }, … ]
}
```

Un tenant sin filas recibe las tres secciones apagadas, con `inQuotation: true`, `inOrder: true` y textos vacíos (no se escribe nada
al leer). `variables` va **último**, como en Soporte.

`PUT …/quotations/settings/pdf-content` — `TenancyPermissions.SettingsUpdate`. Cuerpo: las tres secciones completas (sin
`variables`). Responde lo mismo que el `GET`. Endpoint aparte y no campos nuevos en `PUT …/quotations/settings`: ese cuerpo lo manda
entero otra pantalla, y sumarle campos obligatorios la rompería.

Errores (mapeo central, `ApiExceptionHandler`):

| Caso | Respuesta |
| --- | --- |
| Falta una sección, título > 120, HTML > 20 000 después de sanear, sección prendida con texto vacío (sin texto visible después de sanear) | `422 validation.failed` con `errors` (`PaymentNotice.Title`, `Terms.ContentHtml`, …) |
| Un `{{…}}` que no es variable de la lista, en el título o en el HTML saneado | `422 quotation.pdf_content.unknown_variable`; nada se guarda |

Sin `If-Match`: último que guarda gana, como `PUT …/quotations/settings`. Se audita (`quotations.pdf_content.updated`) con los nombres
de los campos que cambiaron, nunca el texto.

### 4.3 Variables (P-3)

Sintaxis idéntica a Soporte: `{{nombre}}`, espacios opcionales adentro, nombre en minúsculas y guion bajo. Todo `{{…}}` que no esté en
la lista se rechaza al guardar (incluye `{{Empresa}}` y `{{}}`). Lo guardado conserva el token; se resuelve al generar el PDF. La lista
vive una sola vez en el módulo (`QuotationPdfVariables.All`, en `Modules.Quotations.Domain`), con etiqueta y ejemplo.

| `name` | Valor (`QuotationPdfDocument`) | Si falta |
| --- | --- | --- |
| `empresa` | `BillingAccount.CompanyName` | vacío |
| `nit_empresa` | `BillingAccount.CompanyTaxId` | vacío |
| `cliente` | `CustomerName` | — |
| `numero` | `QuotationNumber` (número de la cotización o del pedido) | — |
| `fecha` | `IssuedOn`, con el mismo formato que la ficha del documento (`fecha()` de la plantilla: «16 de noviembre de 2026») | — |
| `vigencia` | `ValidUntil`, mismo formato | vacío (siempre en un pedido) |
| `asesor` | `AdvisorLabel` | — |
| `total` | `Total`, con símbolo, decimales y separadores del documento (`Symbol`, `Decimals`, `ThousandsSeparator`, `DecimalSeparator`), igual a como lo imprime la plantilla | — |

A diferencia de Soporte, una variable sin valor **no bloquea**: sale vacía. El PDF de una cotización sin cuenta de cobro se tiene que
poder generar igual.

## 5. Diseño

### 5.1 Persistencia

Tabla nueva `quotations.pdf_sections` (`tenant_id`, `section` texto con el nombre de §4.1, `enabled`, `in_quotation`, `in_order`,
`title`, `content_html`, `updated_at`), PK `(tenant_id, section)`. Una fila por sección escrita; la ausencia es «apagada y vacía».
`section` se guarda por nombre (CHECK con los tres valores).

### 5.2 Saneado: una sola lista de etiquetas

El saneador de Soporte (`Modules.Support.Infrastructure/Legal/HtmlSupportSanitizer.cs`: `p, br, strong, em, u, s, h2, h3, h4, ul, ol,
li, blockquote, hr, a`; esquemas `https`, `mailto`, `tel`) pasa su núcleo a `BuildingBlocks.Infrastructure`, y Soporte y Quotations
lo usan. Quotations no referencia a Support (`QuotationsLayerTests`). Cada módulo conserva su puerto y su código de error; la lista de
etiquetas y esquemas existe una vez. El comportamiento de Soporte no cambia (sus pruebas del saneador siguen verdes sin tocarlas).

### 5.3 Del HTML a la plantilla: árbol de bloques, nunca markup

Typst no lee HTML, y armar markup de Typst con texto del tenant abriría inyección. Al generar el PDF, el backend:

1. parsea el HTML saneado (con el parser que ya usa el saneador);
2. lo convierte en un árbol JSON: bloques `paragraph`, `heading` (`level` 2–4), `list` (`ordered`, `items`, anidable), `quote`, `rule`;
   cada bloque de texto lleva `runs` con `text` y las marcas `bold`, `italic`, `underline`, `strike` y `href`. `<br>` es un run
   `{ "break": true }`;
3. resuelve las variables **sobre los `text` de los runs** (y sobre el título), no sobre el HTML: no hay nada que codificar, y la
   plantilla recibe texto puro;
4. lo pasa en `QuotationPdfDocument` como `Sections` (`PaymentNotice`, `Terms`, `DeliveryProcess`, cada una `null` si no sale en ese
   tipo de documento), ya decididas por el mapper.

La plantilla pinta el árbol con funciones recursivas y pasa cada `text` como cadena (`[#run.text]`), nunca con `eval`. Conserva los
estilos de hoy: el aviso en `naranja` 7.5 pt, el proceso con título 11 pt en negrita y la página aparte. Los links salen con `link()`
sólo para los esquemas permitidos.

Un token que dentro del HTML quedó partido por marcas (`{{em<strong>pre</strong>sa}}`) no se reconoce al guardar como variable válida y
se rechaza como desconocido: la consola inserta el token entero desde el selector.

### 5.4 Caché del PDF de la cotización

`QuotationPdf` gana `ContentFingerprint` (texto, nullable): hash SHA-256 de las secciones que salieron en ese documento (título y HTML
con tokens, más los flags), calculado por el mismo código que arma el documento. `IsStaleFor` lo compara con `!=`, como `LogoFileId`;
una fila anterior a la columna (`null`) cuenta como vencida, para que los PDF de los tenants que pierden los textos fijos se regeneren.
Los valores de las variables ya los cubre `QuotationVersion` (salen de la cotización). El pedido no tiene caché (`OrderPdfProvider`):
no cambia.

### 5.5 Migración de datos (P-4)

Una migración de Quotations inserta, para el tenant con `slug = 'origen-botanico'` (SQL que lee `tenancy.tenants` por nombre; si no
existe, no inserta nada):

- `paymentNotice`: prendida en los dos documentos, título `Ten en cuenta:` y el texto de `quotation.typ:259-262` con sus cuatro
  saltos de línea como `<br>`;
- `deliveryProcess`: prendida en los dos, título `Proceso de Alistamiento y Recepción de Pedidos.` y el texto de
  `quotation.typ:498-536`, con cada rótulo («Alistamiento y Entrega:», …) como `<p><strong>…</strong><br>…</p>` y las viñetas como
  `<ul><li>`;
- `terms`: sin fila.

El HTML de la migración tiene que salir igual del saneador (prueba). La migración es aditiva (tabla y columna nuevas): un pod viejo
sigue imprimiendo los textos fijos durante el rollout, que es lo de hoy.

## 6. Pruebas que amarran el cambio

- Un tenant sin filas: el PDF no trae ninguna de las tres secciones (el documento las lleva `null`).
- Origen Botánico después de la migración: las secciones del documento son las de hoy, texto por texto.
- Una sección sólo para cotización no sale en el pedido, y al revés.
- El aviso de pago no sale sin cuenta para el pago aunque esté prendido.
- `PUT` con `{{ciudad}}` → `422 quotation.pdf_content.unknown_variable` y nada guardado; con `<script>` → se guarda saneado.
- Las variables se resuelven con los valores de la tabla de §4.3; `{{vigencia}}` en un pedido y `{{empresa}}` sin cuenta salen vacías.
- Un texto con `#`, `*`, `[`, `]`, `$` o `\` (sintaxis de Typst) sale literal en el PDF.
- Cambiar el contenido vence el PDF cacheado de una cotización; no cambiarlo no lo vence.
- `SettingsRead` sin `SettingsUpdate` → `PUT` 403.

## 7. Deploy

Backend primero: sin consola nueva nadie edita, y los tenants quedan como define P-4. La consola sale después y depende del `GET`.

## 8. Lo que queda al builder

Nombres de clases y archivos; el algoritmo del fingerprint mientras sea estable y determinista; cómo comparte Soporte el núcleo del
saneador; cómo formatea el mapper `{{fecha}}` y `{{total}}` en C# mientras coincida con lo que imprime la plantilla (una prueba lo
compara).
