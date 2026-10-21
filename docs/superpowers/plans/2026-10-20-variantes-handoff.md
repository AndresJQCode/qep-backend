# Variantes de producto — traspaso al frontend

Fecha: 2026-10-21. Rama del backend: `feature/variantes-producto`. Spec:
[`docs/superpowers/specs/2026-10-20-variantes-de-producto-design.md`](../specs/2026-10-20-variantes-de-producto-design.md).
Plan del backend: [`2026-10-20-variantes-de-producto-backend.md`](2026-10-20-variantes-de-producto-backend.md).

Este documento es para quien escriba el plan del frontend. Dice qué rutas existen, qué mandas, qué te
devuelven y con qué errores, tal como quedó el código. **La autoridad es el código**: si algo de acá no
coincide con los DTOs (`CatalogDtos.cs`, `OptionLibraryContracts.cs`, `QuotationsDtos.cs`, `OrdersDtos.cs`,
`PosDtos.cs`, `ReportingDtos.cs`, `PriceChangeReportSummary.cs`), gana el DTO y este documento se corrige.

> [!WARNING]
> **El backend y el frontend se despliegan juntos** (spec §6, decisión 7 del owner). No hay doble contrato:
> las líneas dejan de aceptar `productId` y el listado de productos dejó de traer `code`, `prices`,
> `priceScales` y `packagingUnits` por producto. El frontend de `develop` (`9904a7dc`) todavía lee esos
> campos, así que un backend nuevo con el frontend de hoy rompe el catálogo, el selector de cotizaciones y
> pedidos y el POS.

> [!CAUTION]
> **El despliegue tiene ventana de corte y backup previo.** Las migraciones de esta rama son destructivas y el
> rolling update de `k8s/prod-deployment.yaml` deja el pod viejo roto contra el esquema nuevo. El orden, el
> backup y el rollback están en el §13.

> [!NOTE]
> Las pruebas del backend de este proyecto se escribieron pero **no se ejecutaron** (instrucción del owner,
> 2026-10-21). Sólo está verificado que todo compila. Si algo de aquí no se comporta como dice al probarlo,
> puede ser un defecto del backend: avísalo antes de acomodar el frontend al síntoma.

## 1. El modelo en una página

- Un **producto** tiene nombre, descripción, IVA (`taxRateId`), estado, una **galería** de hasta 250 fotos
  (la primera es la principal) y hasta **3 opciones** (Talla, Color…) tomadas de la biblioteca del tenant.
- Cada **variante** es una combinación de valores (una por opción) y tiene lo que antes era del producto:
  `code` (≤ 60, único en el tenant entre todas las variantes), `barcode` (opcional, ≤ 64, único cuando
  existe), `prices`, `priceScales`, `packagingUnits`, una foto de la galería y su estado. Hasta **2.048**
  por producto.
- Un **producto sin opciones** tiene exactamente una variante, la **variante por defecto**, con el **mismo
  id que el producto**. Todo producto que existía antes quedó así. Para venderlo, mandas como `variantId`
  el id de esa variante (que coincide con el del producto, pero léelo de `variants[0].id`, no lo asumas).
- El **nombre visible** lo arma el backend: el del producto en la variante por defecto, y
  `«{producto} – {valor1} / {valor2} / {valor3}»` (guion largo con espacios) con opciones. Viaja en
  `name` de cada variante. **No lo concatenes en el frontend**: es el mismo texto que guardan las líneas, el
  PDF, el Excel y el ticket del POS.
- Una línea de cotización, pedido o venta del POS vende **una variante**. Dos tallas del mismo producto son
  dos líneas.

Todas las rutas de catálogo cuelgan de `/api/v1/tenants/{tenantId}/catalog`. Permisos: `catalog.product.read`
para leer y `catalog.product.manage` para escribir, **sin permisos nuevos**. Una ruta de otro tenant
responde **403**, nunca 404.

## 2. Cómo llegan los errores

Todo error es un `ProblemDetails` con `code` y `traceId`. El formulario lee dos caminos con el mismo mapa
`errors` (claves en PascalCase, como el resto del repo: `Variants[2].Code`, `OptionIds[0]`,
`Variants[1].OptionValueIds[0]`, `DeletedVariantIds[0]`):

1. **Errores de forma** (lo que se ve en el cuerpo): `422` con `code: "validation.failed"` y el **mensaje**
   bajo la clave del campo (en español en el producto y la copia de escalas; la biblioteca de opciones y
   algunos de nombre y descripción todavía lo dan en inglés, deuda anterior). El código interno del validador (por ejemplo
   `catalog.variant.limit_exceeded`) **no viaja**: marca el campo con el mensaje.
2. **Errores contra la base**: `422`, `404`, `409` o `412` con su **propio `code`** y, cuando señalan una fila,
   también `errors` con el campo. Ahí sí puedes ramificar por `code`.

## 3. Biblioteca de opciones

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /options` | `read` | `200 OptionsResponse` |
| `POST /options` | `manage` | `201 OptionResponse` + `Location` |
| `PUT /options/{optionId}` | `manage` | `200 OptionResponse` |
| `DELETE /options/{optionId}` | `manage` | `204` |

```ts
type OptionsResponse = { items: OptionResponse[] };          // ordenadas por nombre, sin distinguir mayúsculas
type OptionResponse = {
  id: string; name: string; isActive: boolean;
  canDelete: boolean;                                        // ninguna variante la usa
  values: { id: string; name: string; isActive: boolean; canDelete: boolean }[]; // en orden
  createdAt: string; updatedAt: string;
};
type CreateOptionRequest = { name: string; values?: string[] | null };          // nace activa
type UpdateOptionRequest = {
  name: string;
  isActive: boolean;                                         // obligatorio: omitirlo responde 422, no desactiva
  values: { id?: string | null; name: string; isActive: boolean }[];             // lista completa, en orden; isActive obligatorio en cada valor
};
```

- **El primer `GET` del tenant siembra «Talla» (XS…XXL) y «Color» (10 colores), inactivas**, para que no
  aparezcan en el formulario de quien no usa variantes. Se siembran una sola vez: si el tenant borra una, no
  vuelve.
- El `PUT` reemplaza nombre, estado y valores: un valor con `id` es uno guardado (se puede renombrar,
  reordenar o desactivar), sin `id` es nuevo, y uno guardado que no viene **se borra** si ninguna variante lo
  usa. El `PUT` de la opción **no lleva versión**: gana el último (un choque en el guardado mismo da `412
  concurrency.conflict`).
- **Desactivar** es `isActive: false` en el `PUT` (de la opción o de un valor). No hay ruta aparte. Una opción
  o un valor inactivo no se ofrece para variantes nuevas; las que ya lo usan lo conservan.
- **Borrar** una opción o un valor en uso responde `409 catalog.option.in_use` sin cambiar nada. `canDelete`
  te dice de antemano si ofrecer «Eliminar» o sólo «Desactivar».

| Código | Status | Cuándo |
| --- | --- | --- |
| `catalog.option.name_taken` | 422 | Ya hay una opción con ese nombre (sin distinguir mayúsculas ni espacios de borde). Ojo: después del primer `GET` ya existen «Talla» y «Color» |
| `catalog.option.not_found` | 404 | La opción no existe en el tenant |
| `catalog.option.in_use` | 409 | `DELETE` de una opción usada, o `PUT` que quita un valor usado |
| `catalog.option.value_not_found` | 422 | Un `id` de valor que no es de la opción, o repetido en el cuerpo |
| `validation.failed` | 422 | Nombre o valor vacío, de más de 60 o repetido (sin distinguir mayúsculas ni espacios de borde): `errors["Name"]`, `errors["Values[i]"]` (POST) o `errors["Values[i].Name"]` (PUT). En el PUT, `isActive` ausente o `null`: `errors["IsActive"]` (la opción) o `errors["Values[i].IsActive"]` (un valor), sin cambiar nada |

## 4. Producto: detalle, alta y edición

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /products/{productId}` | `read` | `200 ProductResponse` |
| `POST /products` | `manage` | `201 ProductResponse` + `Location` |
| `PUT /products/{productId}` | `manage` | `200 ProductResponse` |
| `POST /products/{productId}/activate` | `manage` | `200 ProductResponse` |
| `POST /products/{productId}/deactivate` | `manage` | `200 ProductResponse` |

```ts
type ProductResponse = {
  id: string; name: string; isActive: boolean; description: string | null;
  imageFileId: string | null;            // la principal: el archivo de gallery[0]. Derivado
  imageUrl: string | null;               // null si no está publicada, no está disponible (cuarentena) o no es del tenant
  taxRateId: string | null;
  options: { id: string; name: string; values: { id: string; name: string }[] }[];
  // options[].values son sólo los valores que usa alguna variante de ESTE producto, en el orden de la
  // biblioteca: lo que necesitas para los encabezados de la tabla. La biblioteca entera está en GET /options.
  variants: ProductVariantResponse[];    // TODAS, activas e inactivas, en orden de presentación
  gallery: { id: string; fileId: string; url: string | null }[];  // en orden; la primera es la principal
  version: number;                       // lo mandas de vuelta como expectedVersion
  createdAt: string; updatedAt: string;
};
type ProductVariantResponse = {
  id: string;
  name: string;                          // nombre visible ya armado
  code: string; barcode: string | null;
  optionValueIds: string[];              // uno por opción, en el orden de options; vacío en la por defecto
  prices: Record<string, number>;        // en orden de catálogo de monedas
  priceScales: PriceScaleResponse[];     // ordenadas por fromUnit, con finals por moneda
  packagingUnits: number[];              // siempre, vacío incluido
  galleryImageId: string | null;         // la fila de galería
  galleryFileId: string | null;          // el archivo de esa foto: es lo que mandas de vuelta
  imageUrl: string | null;               // su foto o, sin foto, la principal del producto
  isActive: boolean;
  canDelete: boolean;                    // ninguna línea de cotización, pedido o venta POS la usa
};
type PriceScaleResponse = {
  id: string; fromUnit: number; toUnit: number; discount: number;
  restriction: "multiple" | "packaging_unit" | null;   // null = escala incompleta (la deja la copia)
  multiple: number | null; allowGrouping: boolean; finals: Record<string, number>;
};

type CreateProductRequest = {
  name: string; description?: string | null; taxRateId?: string | null;
  optionIds?: string[] | null;           // null = []
  variants: ProductVariantRequest[];     // al menos una; sin opciones, exactamente una
  gallery?: string[] | null;             // ids de archivo (Storage), en orden; null = []
};
type UpdateProductRequest = CreateProductRequest & {
  expectedVersion: number;
  deletedVariantIds?: string[] | null;   // ver «Borrar una variante»
};
type ProductVariantRequest = {
  id?: string | null;                    // con id: esa variante guardada; sin id: una nueva
  code: string;                          // obligatorio en TODA variante, guardada o nueva (no hay «null = el suyo»)
  barcode?: string | null;               // vacío = sin código de barras
  optionValueIds?: string[] | null;
  pricing?: { prices?: Record<string, number> | null; scales?: PriceScaleRequest[] | null; packagingUnits?: number[] | null } | null;
  galleryFileId?: string | null;         // tiene que estar en gallery
  isActive?: boolean | null;
};
type PriceScaleRequest = {
  fromUnit: number; toUnit: number; discount: number;
  restriction: "multiple" | "packaging_unit" | null; multiple: number | null; allowGrouping?: boolean | null;
};
```

**El `PUT` reemplaza el recurso entero**: `description`, `taxRateId`, `optionIds` y `gallery` en `null` se
limpian. `isActive` del producto no viaja: se cambia con `activate` / `deactivate`. La respuesta trae el
producto entero para que repintes el formulario sin otra lectura.

**Lo que significa `null` en una variante guardada del `PUT`** (con `id`), para que no tengas que reescribir
cada fila al tocar las opciones:

- `optionValueIds: null` = «los suyos», proyectados a las opciones nuevas: si quitaste una opción, suelta su
  valor; si agregaste una, toma su primer valor activo.
- `pricing: null` = deja precio, escalas y empaques como están.
- `isActive: null` = deja su estado.
- `barcode` y `galleryFileId` en `null` **limpian** (son reemplazo).
- `code` no tiene «null = el suyo»: es obligatorio en toda variante, guardada o nueva (`ProductWriteRules`).
  Mándalo de vuelta tal cual lo leíste; vacío o ausente da `validation.failed` en `Variants[i].Code`.

En una variante **nueva** (sin `id`): `pricing: null` copia precio, escalas y empaques de la guardada que más
valores comparte con ella; `isActive: null` es activa; `code` es obligatorio, como en las guardadas.

**Agregar opciones a un producto que ya se vende** (el caso principal: una «Camiseta» migrada a la que le
agregas Talla): manda la variante por defecto con su `id` (y, si quieres, sin valores ni precio) y las tallas
nuevas sin `id`. La variante por defecto se convierte en la primera combinación y conserva su id, código,
precios e historial; las demás copian su precio. Al **quitar** una opción, las combinaciones que quedan
repetidas se desactivan (sigue activa la primera en el orden del cuerpo).

**Concurrencia**: si el producto cambió desde que lo leíste, el `PUT` responde `412 concurrency.conflict` y no
guarda nada. Recarga y vuelve a aplicar.

### Borrar una variante: `deletedVariantIds`

- **Omitir una variante guardada del `PUT` no la borra**: responde `422 catalog.variant.missing_in_request`.
  La tabla llega a 2.048 filas y la vas a paginar o virtualizar; una fila ausente puede ser una página que no
  mandaste, así que el backend no la interpreta como orden de borrar. **Manda siempre todas las guardadas**.
- Para eliminar, manda su id en `deletedVariantIds` (y no en `variants`). El backend la **borra si ninguna
  línea la usa** y la **desactiva si alguna sí** (la respuesta la trae inactiva, en su posición). Comprueba el
  uso dos veces, la segunda dentro de la transacción del guardado.
- `canDelete` te dice de antemano si ofrecer «Eliminar» o «Desactivar». No mira la regla de «al menos una
  activa»: ésa la responde el `PUT` con `catalog.variant.active_required`.
- Un error de validación sobre una variante que se desactivó por estar en uso se marca en
  `errors["DeletedVariantIds[k]"]`, la entrada que mandaste.
- Borrar una variante borra **su historial de precios** (ver decisiones pendientes, §11).

### Errores del producto

Por `validation.failed` (con el mensaje en `errors[campo]`): más de 3 opciones o una repetida (`OptionIds`);
más de 250 fotos o una repetida (`Gallery`); ninguna variante o más de 2.048 (`Variants`); el mismo `id` dos
veces (`Variants[i].Id`); `code` vacío o de más de 60, o repetido **dentro del cuerpo** (`Variants[i].Code`);
código de barras de más de 64 o repetido dentro del cuerpo (`Variants[i].Barcode`); una variante sin un valor
por opción (`Variants[i].OptionValueIds`); una foto que no está en `gallery` (`Variants[i].GalleryFileId`);
precio, escala o empaque inválido (`Variants[i].Pricing…`, antes `Pricing…`).

Con su propio `code`:

| Código | Status | Cuándo | Campo en `errors` |
| --- | --- | --- | --- |
| `catalog.option.not_found` | 422 | Una opción que no es del tenant | `OptionIds[i]` |
| `catalog.option.inactive` | 422 | Una opción inactiva que el producto no usaba | `OptionIds[i]` |
| `catalog.option.value_not_found` | 422 | Un valor que no es de esa opción | `Variants[i].OptionValueIds[j]` |
| `catalog.option.value_inactive` | 422 | Un valor inactivo que el producto no usaba | `Variants[i].OptionValueIds[j]` |
| `catalog.variant.value_count_mismatch` | 422 | No hay un valor por cada opción | `Variants[i].OptionValueIds` |
| `catalog.variant.combination_duplicate` | 422 | Dos variantes **activas** con la misma combinación | `Variants[i].OptionValueIds` |
| `catalog.variant.active_required` | 422 | Ninguna variante activa | `Variants` |
| `catalog.product.code_taken` | 422 | El `code` ya es de una variante de otro producto (activa o no: desactivar no libera el código) | `Variants[i].Code` |
| `catalog.variant.barcode_taken` | 422 | El código de barras ya lo usa otra variante del tenant: el índice es único en todo el tenant | `Variants[i].Barcode` |
| `catalog.variant.missing_in_request` | 422 | Falta una variante guardada que no está en `deletedVariantIds` | — |
| `catalog.variant.duplicated_in_request` | 422 | Un id en `variants` y en `deletedVariantIds`, o repetido en `deletedVariantIds` | — |
| `catalog.variant.not_found` | 422 | Un id (en `variants` o `deletedVariantIds`) que no es de este producto | — |
| `catalog.product.image_not_found` / `image_not_available` / `image_not_an_image` | 422 | Una foto de `gallery` que no existe en el tenant, no está disponible o no es imagen | — (todavía no marca la posición) |
| `catalog.product.not_found` | 404 | El producto no existe en el tenant | — |
| `catalog.product.inactive` | 422 | Editar un producto desactivado | — |
| `concurrency.conflict` | 412 | `expectedVersion` vieja | — |

Se conservan sin cambio de significado `catalog.product.name_*`, `description_too_long`, `tax_rate_not_found`,
`price_required`, `price_negative`, `price_scale.*`, `packaging_units*`, `already_active` / `already_inactive`.

### Galería y fotos

La foto se sube y publica por Storage como hasta hoy (`/files`: sesión, `PUT` a R2, `complete`,
`publication`). Lo que cambia es dónde la asignas: ya no hay `imageFileId` en el cuerpo, sino `gallery` (ids de
archivo en orden; la primera es la principal) y, por variante, `galleryFileId`. Reordenar la galería cambia la
principal. La guía `docs/integracion-imagenes-de-producto.md` que cita el README **no existe** en el repo.

## 5. Listado de productos y selector de cotizaciones y pedidos

`GET /products?search=&name=&code=&isActive=&page=&pageSize=&includeVariants=` → `200 ProductsResponse`.

```ts
type ProductsResponse = { items: ProductListItemDto[]; total: number; page: number; pageSize: number };
type ProductListItemDto = {
  id: string; name: string;
  code: string | null;                   // el de la única variante; null si tiene varias
  isActive: boolean; description: string | null;
  imageFileId: string | null; imageUrl: string | null; taxRateId: string | null;
  variantCount: number;                  // activas e inactivas
  priceRange: Record<string, { min: number; max: number }>;  // sólo variantes activas, en orden de catálogo
  version: number; createdAt: string; updatedAt: string;
  variants?: ProductSellableVariantDto[];  // SÓLO con includeVariants=true; sin él, la clave no viaja
};
type ProductSellableVariantDto = {
  id: string; name: string;              // nombre visible
  code: string; barcode: string | null;
  prices: Record<string, number>; priceScales: PriceScaleResponse[]; packagingUnits: number[];
  imageUrl: string | null;
};
```

- **El listado ya no trae `prices`, `priceScales` ni `packagingUnits` por producto.** La grilla del catálogo pinta
  `code` (o «N variantes» con `variantCount`) y `priceRange`.
- **El selector de cotizaciones y pedidos tiene que cambiar.** Hoy `useQuoteProducts`
  (`src/features/quotes/hooks/use-quote-products.ts`, verificado en `develop` `9904a7dc`) pide
  `GET /catalog/products` y arma cada opción con `product.code` y `quotePriceScalesOf(product)`, campos que
  ya no existen. Tiene que pedir `includeVariants=true` y armar **una opción por variante** desde `variants[]`:
  los nombres JSON son los mismos que leía del producto (`code`, `priceScales[].fromUnit/toUnit/discount/
  restriction/multiple`, `packagingUnits`), así que `quotePriceScalesOf` sirve pasándole la variante. Lo que
  mandas al agregar la línea es `variantId = variant.id`.
- `variants[]` son las **vendibles**: activas, de un producto activo, en orden de presentación. Un producto
  desactivado sigue en el listado con `variants: []`.
- Con `includeVariants=true` la página se **topa en 50** (sin él, en 200). El selector pide 40, así que no lo
  notas; la respuesta trae el `pageSize` aplicado.
- `search` busca por nombre, `code` de cualquier variante **y código de barras** (también en la grilla del
  catálogo). Un producto que coincide por una variante trae **todas** sus variantes vendibles, no sólo esa.
  Las cajas `name` y `code` no miran el código de barras.

## 6. Copiar escalas

`POST /products/price-scales/copy` (`manage`) → `200 { products: ProductResponse[] }`.

```ts
type CopyPriceScalesRequest = { sourceVariantId: string; targetVariantIds: string[] }; // antes sourceProductId/targetProductIds
```

- Copia de **una variante** a **variantes** de uno o varios productos. Tope: 200 destinos, **contados en
  variantes**; sin repetidos y sin el origen.
- La respuesta trae cada producto tocado **una vez**, en el orden de su primera variante destino, con todas sus
  variantes y su `canDelete`.
- Errores: `validation.failed` en `TargetVariantIds` / `SourceVariantId` (vacío, más de 200, repetido, origen
  dentro de destinos); **`404 catalog.variant.not_found`** si el origen o un destino no existe en el tenant
  (antes era el 404 de producto); `422 catalog.product.inactive` si un producto destino está desactivado;
  `catalog.product.price_scale_copy.source_without_scales` se conserva.

## 7. Cotizaciones y pedidos: `productId` → `variantId`

Todas las peticiones que mandaban `productId` en una línea ahora mandan **`variantId`**. Las rutas no cambian:

| Ruta (bajo `/api/v1/tenants/{tenantId}`) | Cuerpo |
| --- | --- |
| `POST /quotations` y `POST /quotations/preview` | `items: { variantId, quantity }[]` |
| `PUT /quotations/{id}` y `POST /quotations/{id}/preview` | `items: { variantId, quantity }[]` (lista completa) |
| `POST /quotations/{id}/items` | `{ variantId, quantity }` |
| `POST /quotations/{id}/items/batch` | `toAdd: { variantId, quantity }[]` |
| `POST /orders` y `POST /orders/preview` | `items: { variantId, quantity }[]` |
| `PUT /orders/{id}` y `POST /orders/{id}/preview` | `items: { variantId, quantity }[]` (lista completa) |
| `POST /orders/{id}/items` | `toAdd: { variantId, quantity }[]` |

- La respuesta de cada línea (`QuotationItemResponse`, la misma en cotización y pedido) **gana `variantId`**
  y conserva `productId` (el producto dueño de la variante). `productCode` es el `code` de la variante y
  `productName` su nombre visible. El guardado de la lista completa se direcciona por `variantId`: mándalo de
  vuelta tal cual.
- **Ojo con `productName`, que no significa lo mismo en todas partes.** En las líneas de cotización y pedido (y
  en `name` de las líneas del POS) es el **nombre visible de la variante** («Camiseta – M»). En el reporte de
  cambios de precio (§9) es el **nombre del producto** («Camiseta»), y el visible viaja aparte en `variantName`.
- **Una línea por variante**: la regla «un producto no se repite» ahora es «una variante no se repite». Los
  códigos se conservan: `quotation.item.duplicate_product` y `order.item.duplicate_product` (422); en las
  listas completas, una variante repetida llega antes como `validation.failed` con la clave **`errors.Items`**
  (la lista, no una posición). El mensaje dice «variante».
- Una variante vacía da `validation.failed`, con la clave según la ruta (antes `ProductId` en el mismo lugar):

  | Ruta | Clave en `errors` |
  | --- | --- |
  | `POST /quotations/{id}/items` | `VariantId` |
  | `POST /quotations/{id}/items/batch` y `POST /orders/{id}/items` | `ToAdd[i].VariantId` |
  | Listas completas: `POST`/`PUT` de cotización y pedido y sus `preview` | `Items[i].VariantId` |
  | POS: `POST /pos/sales` y `POST /pos/sales/preview` | `Lines[i].VariantId` (§8) |
- `quotation.item.product_not_found`, `product_inactive` y `product_price_unavailable` se conservan: ahora
  hablan de la variante (inexistente, inactiva o de un producto inactivo, o sin precio en la moneda).
- **Escalas por variante**: una escala sin `allowGrouping` cuenta sólo la cantidad de su variante (6 M y 6 L no
  alcanzan una escala de 12); una con `allowGrouping` suma entre variantes con el mismo rango y múltiplo, como
  antes sumaba entre productos.
- **Nombre de la línea**: un borrador lee en vivo el nombre visible y el código de la variante y lo muestra
  **completo**; al enviarse (o convertirse) la línea guarda la foto, recortada a **200 caracteres con «…»** si
  el nombre visible es más largo (producto de 200 más los valores). No le pongas tope al nombre en el
  borrador: el recorte lo hace el backend al congelar.
- El PDF no cambia de plantilla: `productName` ya es el nombre visible.

## 8. Punto de venta

Bajo `/api/v1/tenants/{tenantId}/pos`. **Cambia sin compatibilidad**.

```ts
// GET /products?search=&page=&pageSize=  →  PosPage<PosCatalogProductResponse>
type PosPage<T> = { items: T[]; page: number; pageSize: number; total: number };  // total cuenta productos
type PosCatalogProductResponse = {
  productId: string; name: string;       // el del producto: título de la tarjeta
  hasOptions: boolean;                   // true: tocar la tarjeta abre el selector; false: variants trae una y va directo al carrito
  imageUrl: string | null;               // portada (la principal)
  variants: PosProductResponse[];        // las activas, en orden
};
// GET /products/by-code?code=  →  PosProductResponse (el escáner)
type PosProductResponse = {
  variantId: string; productId: string;  // antes: id
  code: string; barcode: string | null;
  name: string;                          // nombre visible
  unitPrice: number | null; taxPercentage: number; imageUrl: string | null;
  sellable: boolean; unsellableReason: "Inactive" | "PriceMissing" | null;
};
type PosPreviewLineRequest = { variantId: string; quantity: number; discountPercentage: number };
type PosSaleLineRequest = { variantId: string; quantity: number; discountPercentage: number; expectedUnitPrice: number; expectedTaxPercentage: number };
// PosPreviewLineResponse gana variantId y productId (null cuando la variante no existe, como code y name);
// PosSaleLineResponse gana variantId.
```

- **El carrito junta por `variantId`**, no por `productId` (hoy junta por producto cuando el descuento es 0).
  Dos tallas del mismo producto son dos líneas, cada una con su precio.
- **Escáner**: busca primero por **código de barras exacto** y, si ninguna variante lo tiene, por `code` exacto
  (sensible a mayúsculas). Si un texto es el código de barras de una variante y el `code` de otra, gana el
  código de barras. Una variante inactiva vuelve con `sellable: false` y `unsellableReason: "Inactive"`. Sin
  coincidencia: `404 pos.product.not_found`.
- El tope de `code` en `by-code` sube de **60 a 64** (el largo de un código de barras). De 61 a 64 caracteres,
  antes daba 422 y ahora busca.
- La búsqueda de la cuadrícula encuentra por nombre, `code` o código de barras, y muestra sólo variantes
  activas de productos activos.
- Los códigos de la venta se conservan: `pos.sale.product_not_found` (un `variantId` que no existe),
  `product_inactive`, `product_price_unavailable`, `price_changed`, `id_conflict` (mismo `id` de venta con otra
  variante). Una variante vacía da `validation.failed` en `Lines[i].VariantId`.
- El ticket imprime el nombre visible, guardado con el mismo recorte a 200.

## 9. Reporte de cambios de precio

Bajo `/api/v1/tenants/{tenantId}/reports`, permiso de siempre.

- `GET /price-changes` y `GET /price-changes/summary` aceptan **`variantId`**, que se combina con `productId`
  por **Y** (un id de otro producto o desconocido da vacío, no error).
- Cada fila del listado (`PriceChangeReportItemDto`) gana `variantId` y `variantName` (nombre visible).
  `productCode` pasa a ser el `code` de la variante de esa fila; `productName` sigue siendo el del producto.
- En el resumen, cada entrada de `byProduct` gana `variants: { variantId, name, code, count }[]` con **todas**
  las variantes del producto, también las que tienen `count: 0`, en orden de presentación; la suma cierra con
  el `count` del producto. `productCode` es `null` si el producto tiene varias variantes. La fila «Otros»
  trae `variants: []`.

## 10. Exportaciones

- **Excel de productos**: una fila por variante (activas e inactivas, `Estado = Inactivo`). Columnas fijas
  `Código`, `Código de barras`, `Producto`, `Variante`, `Descripción`, `Estado`, `Tasa de impuesto` y después
  precios y escalas. `Variante` va vacía en la variante por defecto. Los encabezados cambiaron (`Nombre` →
  `Producto`, con tilde): quien tenga macros sobre el archivo lo nota. El tope de 20.000 filas
  (`catalog.export.too_many_rows`) cuenta **variantes**; `productCount` de la respuesta sigue contando productos.
- **Excel de pedidos**: `Cod. Producto` lleva el `code` de la variante, y el catálogo de columnas gana
  **`Variante`** (`key: "variant"`, al final, `defaultVisible: false`): **nace oculta** y el tenant la prende
  desde Configuración › Excel de pedidos. Si esa pantalla pinta el catálogo que devuelve
  `GET /orders-export-layout`, no necesita cambios. En una variante por defecto lleva el nombre del producto.

## 11. Decisiones pendientes del owner

Tomadas por quien construyó el backend para no frenar; el owner puede cambiarlas. Ninguna cambia la forma de
los DTOs de arriba salvo donde se dice.

1. **`Variante` vacía o con nombre en la variante por defecto.** El Excel de productos la deja **vacía**; el
   Excel de pedidos lleva el **nombre del producto**, porque la línea no sabe si su variante es la por defecto
   sin consultar Catalog. Igualarlas exige guardar en la línea una marca «sin opciones».
2. **El código de barras de una variante inactiva gana al escanear.** Si el código de barras de A (inactiva) es
   el `code` de B (activa), el escáner devuelve A marcada como no vendible. Ninguna variante migrada tiene
   código de barras, así que hoy no pasa con nada de lo que ya se escanea.
3. **Borrar una variante borra su historial de precios** (`CASCADE`). Coherente con «no está en uso», pero el
   reporte de cambios de precio pierde esas filas. La alternativa es `SET NULL`.
4. **Riesgo aceptado en la carrera del borrado.** Si una línea se confirma en la ventana de milisegundos entre
   la segunda comprobación de uso y el commit del `PUT`, puede quedar una línea con el `variant_id` de una
   variante borrada. Se lee igual (un borrador muestra nombre y código vacíos, una enviada conserva su foto).
   Cerrarla del todo pide `FOR UPDATE` en el `PUT` de Catalog **y** `FOR KEY SHARE` en los nueve caminos que
   escriben líneas.
5. **La búsqueda del listado de productos encuentra también por código de barras**, en todos sus usos (grilla
   del catálogo, selector de cotizaciones y pedidos, POS). No suma puntaje de relevancia: un producto que
   coincide sólo por código de barras queda detrás de los que coinciden por nombre o código.

Otras decisiones del backend que conviene que el owner conozca:

- El tope de **50 productos por página con `includeVariants=true`** (para acotar el peor caso: 50 × 2.048
  variantes). Quitarlo es una constante.
- El nombre visible **se recorta** a 200 en la foto en vez de ensanchar `product_name`.
- Al **quitar una opción**, las combinaciones repetidas se desactivan, no se borran; para borrarlas, el
  formulario las manda en `deletedVariantIds`.
- El `PUT` de la opción **no lleva versión**. Su `isActive` (y el de cada valor) es obligatorio: un cuerpo sin
  él responde `422 validation.failed` con el campo marcado y no cambia nada.

## 12. Lo que toca cada pantalla (para armar el plan)

- **Formulario de producto**: opciones de la biblioteca (máx. 3), tabla de variantes paginada o virtualizada
  hasta 2.048 filas (generar combinaciones al elegir valores, editar en bloque), galería ordenable de hasta
  250 fotos con foto por variante, `expectedVersion`, `deletedVariantIds` guiado por `canDelete`, y los
  errores por fila (`Variants[i].…`). Un producto sin opciones se ve como hoy: los campos de su única
  variante.
- **Biblioteca de opciones**: listar, crear, renombrar, reordenar, activar/desactivar y borrar con
  `canDelete`. Recuerda que el primer `GET` siembra Talla y Color inactivas.
- **Grilla del catálogo**: `code` o «N variantes», `priceRange`, búsqueda que encuentra por código de barras.
- **Selector de cotizaciones y pedidos** (`useQuoteProducts`, `product-picker-dialog`, `add-order-items-dialog`):
  `includeVariants=true`, una opción por variante, `variantId` al agregar.
- **Editor de cotización y pedido**: mandar `variantId` en las listas y en las altas; las líneas traen
  `variantId`; claves de error `VariantId`, `ToAdd[i].VariantId` o `Items[i].VariantId` según la ruta (§7).
- **POS**: cuadrícula por producto con selector si `hasOptions`, escáner que devuelve una variante, carrito
  agrupado por `variantId`, preview y venta con `variantId`.
- **Copia de escalas**: elegir variante origen y variantes destino.
- **Reporte de cambios de precio**: filtro por variante, `variantName` en la tabla, desglose por variante en el
  resumen.

## 13. Despliegue

Este backend **no se despliega con el rolling update de siempre**. Tres migraciones cambian el esquema de forma que el
código anterior deja de funcionar contra él, y las tres corren al arrancar el pod nuevo (`Program.cs`, un
`Initialize<Modulo>DatabaseAsync` por módulo):

| Migración | Módulo | Qué rompe para el código anterior |
| --- | --- | --- |
| `AddProductVariants` | Catalog | **Destructiva.** Borra `catalog.products.code`, `image_file_id` y `packaging_units` (los copia antes a la variante por defecto y a la galería) y renombra `product_id` → `variant_id` en `catalog.product_prices` y `catalog.product_price_scales`. El código anterior lee esas columnas: el catálogo entero responde 500 (`42703`) |
| `AddVariantToLines` | Quotations | `variant_id` `NOT NULL` sin default en `quotation_items` y `order_items`. El código anterior no lo escribe: agregar una línea o convertir responde 500 (`23502`) |
| `AddVariantToSaleLines` | Pos | Lo mismo en `pos.sale_lines`: el código anterior no puede registrar una venta |

`k8s/prod-deployment.yaml` usa `RollingUpdate` con `maxSurge: 1` y `maxUnavailable: 0`: el pod nuevo arranca, migra, y
mientras se pone listo **el pod viejo sigue atendiendo contra el esquema nuevo**, roto. Y como el frontend de `develop`
tampoco sabe hablar con este backend (aviso del principio), el corte es de los dos lados. El yaml no se cambia: la
estrategia de esta versión se decide en el despliegue.

**Orden.**

1. **Backup de la base** justo antes, con lo que use el proveedor o `pg_dump -Fc`. Como mínimo los esquemas `catalog`,
   `quotations` y `pos`; el `Down` de Catalog pierde datos (abajo), así que el backup es el único rollback sin pérdida.
2. **Ventana de corte anunciada.** Sin pod viejo vivo durante la migración: escala el backend a cero réplicas antes de
   desplegar (o cambia la estrategia a `Recreate` sólo para esta versión) y no lo vuelvas a abrir hasta que el pod nuevo
   esté listo.
3. **Backend y frontend juntos**, en la misma ventana. El frontend nuevo contra el backend viejo tampoco funciona.
4. Verificar: el catálogo lista, el detalle de un producto migrado trae una variante con el id del producto, una
   cotización y una venta del POS se crean.

**Rollback.** Se corre con el código de esta rama (es el que tiene los `Down`), con el backend detenido, y **en este
orden: Pos → Quotations → Catalog**. Después se despliega la imagen anterior de backend y frontend.

```powershell
$env:ConnectionStrings__QepDatabase = Read-Host "Cadena de conexión de producción"
dotnet ef database update AddPosCurrency --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext
dotnet ef database update AddOrderExternalKey --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet ef database update AddProductPrices --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext
Remove-Item Env:ConnectionStrings__QepDatabase
```

- **Pos y Quotations** sólo quitan `variant_id` de las líneas. Una línea que vendió una variante que no era la por
  defecto conserva su `product_id`, su foto y sus importes: se sigue leyendo.
- **Catalog pierde datos.** Vuelve una variante por producto —la que lleva el id del producto o, si ya no existe, la
  primera— con su código, precios, escalas y empaques. **Las demás variantes se pierden con sus precios y escalas**, y
  también los códigos de barras, la galería salvo la foto principal y la biblioteca de opciones. Su historial de precios
  no se borra: se queda colgado del producto, sin la variante, mezclado con el de la que vuelve. Si ya hay productos con
  variantes, el rollback que no pierde nada es restaurar el backup del paso 1.
