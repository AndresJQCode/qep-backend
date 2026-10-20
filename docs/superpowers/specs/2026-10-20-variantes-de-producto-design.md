# Variantes de producto — diseño

Fecha: 2026-10-20. Autor: brainstorming con el owner (Andrés Jaramillo). Estado: **aprobado por el owner el 2026-10-20**
(las siete decisiones de §9 confirmadas; tres corregidas: 3, 5 y 6).

Este documento describe *qué* se construye y *por qué*. Las decisiones del owner van marcadas **(owner)**; las que tomó
quien escribe y el owner todavía no confirmó van marcadas **(propuesta)**. El código es la autoridad: las rutas citadas
son de `develop` al 2026-10-20 y hay que verificarlas antes de planificar.

## 1. Intención

QEP hoy modela un producto como un único artículo vendible: un `Code` (único por tenant), precios por moneda, escalas por
cantidad, empaques e imagen. Los tenants que venden ropa, calzado o similares tienen el mismo producto en varias tallas y
colores, cada uno con su código, su código de barras y a veces su precio, su empaque y sus escalas. Shopify lo modela así
(producto → opciones → variantes), y el siguiente proyecto —traer los productos de Shopify a QEP— necesita que QEP tenga
el mismo modelo para que cada variante de Shopify caiga en una variante de QEP.

Por eso este proyecto va **antes** de la sincronización de productos con Shopify (orden acordado con el owner: variantes →
productos desde Shopify → centro de notificaciones → alertas de salud de la conexión Shopify).

### Éxito
- Un tenant crea un producto «Camiseta básica» con opciones Talla y Color y vende «M / Azul» en una cotización, un pedido y
  el punto de venta, con el código, el precio, las escalas y el empaque de esa variante.
- Un producto sin opciones se crea, se ve y se vende **exactamente como hoy**: nadie que no use variantes nota el cambio.
- Ninguna cotización, pedido o venta existente cambia de total, código ni nombre.

### Anti-objetivos (fracaso aunque funcione)
- Que crear un producto simple se vuelva más difícil (más pasos, más campos obligatorios).
- Que una línea histórica cambie de precio, código o nombre por la migración.
- Que un escáner del punto de venta deje de encontrar un producto que hoy encuentra.

## 2. Alcance

### Dentro
1. **Biblioteca de opciones por tenant (owner).** QEP trae opciones predefinidas con valores estándar; el tenant las activa,
   les agrega valores y crea opciones propias.
2. **Producto con hasta 3 opciones** y sus variantes (combinaciones de valores).
3. **Variante** con datos propios: `Code`, código de barras, precios por moneda, escalas por cantidad, empaques e imagen
   (owner: código de barras, escalas y empaques por variante; precios e imagen por variante como Shopify).
   **Galería del producto (owner, 2026-10-20):** hasta 250 fotos ordenadas (la primera es la principal, como Shopify);
   cada variante apunta opcionalmente a una foto de la galería. Reemplaza la imagen única de hoy: la imagen actual de cada
   producto migra como primera foto de su galería.
4. **Elegir variante al vender** en cotizaciones, pedidos y punto de venta; producto sin opciones no pide elegir nada
   (owner).
5. **Escaneo por código de barras** en el punto de venta (owner).
6. **Migración** de todos los productos existentes a «producto con una variante».
7. **Reporte de cambios de precio** por producto y por variante (owner: «las dos cosas»).
8. Excel de productos, Excel de pedidos y PDF de la cotización mostrando la variante.
9. Emparejamiento de pedidos de Shopify por SKU contra el `Code` de la **variante**.

### Fuera
- **Sincronización de productos desde Shopify**: es el proyecto siguiente (traspaso en la memoria del proyecto
  `shopify-productos-brainstorm.md`). Este proyecto sólo deja el modelo listo y cambia el emparejamiento de pedidos.
- Inventario, peso y envío por variante (QEP no maneja inventario).
- IVA por variante: el IVA sigue en el producto (owner, no lo corrigió al proponerlo).
- Descripción o nombre propio por variante: el nombre visible se arma (§3.4).
- **Reporte de ventas por producto y por variante (owner, 2026-10-20: «no meter en este proyecto»)**: hoy no existe
  ninguno (el único reporte por producto es el de cambios de precio, `Bootstrapper/PriceChangeReportSource.cs`). Queda
  como proyecto aparte, después. Lo único que este proyecto le deja listo: las líneas guardan `product_id` **y**
  `variant_id`, así que ese reporte podrá agrupar por producto y desglosar por variante sin otra migración.

## 3. Modelo

### 3.1 Biblioteca de opciones (`catalog`)
- **Opción** del tenant: nombre (p. ej. «Talla»), lista ordenada de **valores** (p. ej. «S», «M», «L»), activa/inactiva.
  Nombre único por tenant; valor único dentro de su opción (comparación sin distinguir mayúsculas ni espacios de borde).
- **Predefinidas (propuesta)**: cada tenant nace (o recibe en la migración) con «Talla» (XS, S, M, L, XL, XXL) y «Color»
  (Negro, Blanco, Gris, Azul, Rojo, Verde, Amarillo, Café, Beige, Rosado), **inactivas** hasta que el tenant las active,
  para que no aparezcan en el formulario de quien no usa variantes. El tenant puede renombrar valores, agregar y desactivar.
  Se siembran **en el primer acceso** a la biblioteca con `INSERT … ON CONFLICT DO NOTHING`, como `PipelineProvisioner`
  crea el embudo (Tenancy no publica nada al crear un tenant que Catalog escuche) **(propuesta)**.
  No hay taxonomía global compartida entre tenants: las predefinidas son una semilla, cada tenant tiene su copia.
- Una opción o un valor que alguna variante ya usa **no se borra**: se desactiva (no se ofrece para variantes nuevas; las
  existentes lo conservan). Saber si está en uso es una consulta dentro de Catalog (las variantes viven ahí), sin puertos.
  Una opción o un valor que ninguna variante usa sí se puede borrar. Renombrar un valor cambia el nombre visible de las variantes que lo usan; las líneas ya
  guardadas conservan su foto (§4).
- Permisos **(propuesta)**: leer con `catalog.product.read`, administrar con `catalog.product.manage`. Sin permiso nuevo.

### 3.2 Producto
- Conserva: nombre, descripción, galería de imágenes (§2), IVA (`TaxRateId`), activo/inactivo, `Version`.
- Gana: **hasta 3 opciones** (referencias a opciones de la biblioteca, en orden). Límite de 3 tomado de Shopify
  (hasta 3 opciones por producto, hasta 2.048 variantes: help.shopify.com/en/manual/products/variants/add-variants).
- Pierde, hacia la variante: `Code`, precios, escalas y empaques.
- **Producto sin opciones = exactamente una variante** (la «variante por defecto», sin valores de opción), como en Shopify.
  En pantalla y en la API para el frontend se presenta como hoy (§6): sus datos son los de esa variante.

### 3.3 Variante (entidad dentro del agregado `Product`)
- `Code`: obligatorio, ≤60, **único por tenant entre todas las variantes** (reemplaza a `IX_products_tenant_code`; el
  índice nuevo tampoco es parcial: desactivar no libera el código, como hoy).
- Código de barras: opcional, ≤ el largo que el builder elija razonable (GTIN-14 cabe en 14; se sugiere 64), **único por
  tenant cuando existe** (owner: código de barras por variante; propuesta: opcional y único).
- Un valor por cada opción del producto; la combinación es única dentro del producto.
- Precios por moneda (al menos uno, como hoy `catalog.product.price_required`), escalas por cantidad, empaques: con las
  mismas reglas y códigos de error que hoy tiene el producto (`catalog.product.price_scale.*`, `packaging_units.*`).
- Imagen opcional: una foto de la galería del producto (misma regla de Storage); sin imagen, se usa la principal del producto.
- Activa/inactiva: una variante inactiva no se ofrece al vender; un producto inactivo inactiva la venta de todas.
- Orden de presentación.
- **Una variante guardada se borra sólo si no está en uso; si está en uso, se desactiva (owner, 2026-10-20).** «En uso» =
  alguna línea de `quotations.quotation_items`, `quotations.order_items` o `pos.sale_lines` guarda su `variant_id`.
  Catalog no referencia esos módulos (`CatalogLayerTests`), así que declara un puerto de sólo lectura
  (`IVariantUsageChecker`, nombre tentativo) con adaptador en Bootstrapper que consulta los tres esquemas, como
  `PriceChangeReportSource`. La comprobación ocurre en el handler y la repite el borrado en la misma transacción; una línea
  que apareció entre las dos se resuelve desactivando en vez de borrar, nunca dejando una línea con `variant_id` huérfano.
  La respuesta del `PUT` dice por variante si se puede borrar, para que el formulario ofrezca «Eliminar» o «Desactivar»
  sin adivinar (BFF). Lo mismo con los valores de opción.
- Tope de variantes por producto **(owner, 2026-10-20)**: 2.048, igual que Shopify, así que un producto de
  Shopify siempre cabe. Con ese tope la tabla del formulario tiene que paginar o virtualizar (builder, §10).

### 3.3.1 Cambiar las opciones de un producto que ya existe (propuesta)
Es el caso principal: un tenant de ropa tiene «Camiseta» migrada (una variante por defecto con ventas) y le agrega Talla.
- **Agregar opciones a un producto sin opciones**: la variante por defecto **se convierte** en una de las combinaciones
  nuevas —el tenant elige cuál; por defecto, la primera— y conserva su id, `Code`, código de barras, precios, escalas,
  empaques e historial de precios. Las demás combinaciones nacen como variantes nuevas con sus precios, escalas y empaques
  **copiados** de ella y el `Code` por llenar (obligatorio para guardar).
- **Agregar una opción más a un producto con opciones** (p. ej. Color a uno que ya tiene Talla): cada variante existente
  toma un valor de la opción nueva (el tenant elige; por defecto el primero) y conserva su id; las combinaciones que
  faltan nacen nuevas, copiadas de la variante de su misma talla.
- **Quitar una opción**: sólo se permite si después de quitarla no quedan dos variantes **activas** con la misma
  combinación; las que sobran se desactivan (se desactivan o se borran según §3.3). Quitar todas deja una sola variante activa, que pasa a ser
  la variante por defecto.
- El `PUT` empareja variantes por su id: una variante del cuerpo con id existente es esa variante; sin id, es nueva.

### 3.4 Nombre visible
- Variante por defecto: el nombre del producto (como hoy).
- Con opciones: `«{producto} – {valor1} / {valor2} / {valor3}»` en el orden de las opciones, p. ej.
  «Camiseta básica – M / Azul» (mismo formato que el título de línea de un pedido de Shopify).
- Es lo que se guarda como foto en las líneas (§4) y lo que muestran PDF, Excel, POS y reportes.

### 3.5 Historial de precios
- `product_price_changes` gana la variante; cada cambio de precio o de descuento de escala se registra **por variante**.
- El reporte de cambios de precio filtra por producto y por variante, y el resumen agrupa por producto con desglose por
  variante (owner: las dos cosas).

## 4. Líneas que venden un producto

Hoy guardan `product_id` (sin FK) + foto `product_code`/`product_name` (≤60/≤200) y precios calculados:
`quotations.quotation_items`, `quotations.order_items`, `pos.sale_lines`.

- Ganan **`variant_id`** (sin FK, igual que `product_id`) y la foto pasa a ser la de la variante: `product_code` = `Code`
  de la variante, `product_name` = nombre visible (§3.4). `product_id` se conserva (agrupar por producto en reportes).
- **La foto nunca se recalcula**: una línea guardada no cambia si después se renombra el producto, un valor de opción o el
  código de la variante. Hoy las cotizaciones en borrador leen código y nombre vivos hasta enviarse
  (`QuotationItemProductLabel`); se mantiene ese comportamiento, ahora resolviendo la variante.
- **Una línea por variante**: la regla de hoy «un producto no se repite» (`quotation.item.duplicate_product`,
  `order.item.duplicate_product`, validadores de `QuotationEdits`/`OrderEdits`/`CreateOrder`, diff por `ProductId` en
  `QuotationItemEdits`/`OrderItemEdits`) pasa a ser por **variante**: dos tallas del mismo producto son dos líneas.
  Los códigos de error se conservan (son contrato con el frontend); su texto dice «variante».
- Precio, escalas y empaques se resuelven de la **variante** (`QuotationProductPricingResolver` y los lookups de
  Bootstrapper). **Regla de conteo (propuesta): cada variante se comporta como hoy un producto.** Una escala **sin**
  `AllowGrouping` se cuenta sólo con la cantidad de esa variante: 6 M y 6 L no suman 12 (igual que Shopify B2B:
  help.shopify.com/manual/b2b/catalogs/quantity-pricing). Una escala **con** `AllowGrouping` agrupa entre líneas igual
  que hoy agrupa entre productos (`QuotationScaleGroupPricing`, clave `(FromUnit, ToUnit, Multiple)`), así que dos
  variantes con la misma escala agrupable sí suman. El cambio en `QuotationScaleGroupPricing` y
  `OrderPricingRecalculation` es cambiar la clave de producto a variante, sin regla nueva; ninguna cotización migrada
  cambia de total porque cada producto migrado tiene una sola variante.
- **Copiar escalas** (`POST /products/price-scales/copy`, propuesta): copia de **una variante** a las **variantes**
  destino elegidas (de uno o varios productos), con el mismo tope de hoy, 200 destinos, contados en variantes. El
  historial de precios registra el cambio por variante.
- El punto de venta hoy no aplica escalas (`IPosProductLookup` no las trae): no cambia en este proyecto.
- Las peticiones que hoy mandan `productId` (agregar/editar ítems de cotización y pedido, crear pedido, venta del POS)
  pasan a mandar **`variantId`**. Para un producto sin opciones el frontend manda el id de su variante por defecto.

## 5. Punto de venta
- Búsqueda: por nombre del producto, `Code` de la variante o código de barras.
- Escaneo: `GET /pos/products/by-code` busca **primero por código de barras y después por `Code`** (propuesta; el owner
  pidió código de barras por variante). Devuelve la variante con su precio y nombre visible.
- La cuadrícula muestra productos; tocar uno con opciones abre un selector de opciones; uno sin opciones va directo al
  carrito como hoy. El carrito junta por **variante** (hoy junta por `productId` cuando el descuento es 0).
- El ticket imprime el nombre visible de la variante.

## 6. API para el frontend (BFF)
El consumidor es `qep-frontend` y nada más, así que la forma se decide por la pantalla:
- El detalle del producto devuelve sus opciones y **todas** sus variantes en orden, cada una con su nombre visible ya
  armado (el frontend no concatena), su `Code`, código de barras, precios, escalas, empaques, imagen y estado.
- Un producto sin opciones devuelve una sola variante; el formulario la muestra como los campos de siempre.
- El guardado reemplaza el recurso entero (como `UpdateProduct` hoy): producto + variantes en un solo `PUT`, para que el
  formulario repinte lo que devuelve. Una variante guardada que falta en el cuerpo se rechaza con un código propio (no se
  borra; se desactiva o se borra con su estado, §3.3).
- **Despliegue (propuesta):** backend y frontend van juntos, como manda el precedente de Shopify en `CLAUDE.md`: el
  frontend es el único consumidor, así que el cambio de `productId` a `variantId` es directo, sin aceptar los dos
  contratos a la vez.
- El listado de productos sigue siendo por producto (con el rango de precios o la cantidad de variantes como dato BFF).
- El selector de productos de cotizaciones y pedidos devuelve productos con sus variantes vendibles, para elegir sin otra
  llamada.

## 7. Documentos y exportaciones
- **Excel de productos** (`ClosedXmlProductExportBuilder`): una fila por variante, con columnas Código, Código de barras,
  Producto, Variante, y las de precio y escala de hoy **(propuesta)**.
- **Excel de pedidos**: la columna «Cod. Producto» lleva el `Code` de la variante. Se agrega al catálogo de columnas una
  columna «Variante» (nombre visible) **(propuesta)**; ojo con la regla de choque de encabezados al sembrarla
  (README § Columnas del Excel de pedidos).
- **PDF de la cotización**: hoy muestra sólo el nombre (`QuotationPdfLine.ProductName`); pasa a mostrar el nombre visible
  de la variante. Sin cambio de plantilla si el campo conserva el nombre `productName`.

## 8. Migración
- Cada producto existente se convierte en un producto **sin opciones con una variante por defecto** que hereda su `Code`,
  precios, escalas y empaques. Sugerencia para el builder: dar a la variante por defecto **el mismo id que el producto**
  para rellenar `variant_id` en `quotation_items`, `order_items` y `sale_lines` sin consultas entre esquemas
  (cada módulo tiene su `DbContext` y su migración).
- `product_price_changes` existentes se asignan a la variante por defecto.
- Las fotos de las líneas existentes no se tocan.
- Hay inserciones a mano que hay que actualizar: `Bootstrapper/Seeding/ExportLoadSeeder.cs` (SQL crudo sobre
  `quotation_items`, `order_items`, `catalog.products`, `catalog.product_prices`) y el sembrador del catálogo
  (`CatalogSeeder`, `catalog-products.json`).
- Las columnas heredadas sin mapear de `catalog.products` (`price_base_usd`, `price_base_cop`, `final_*`) se pueden borrar
  en la misma ventana si el builder lo ve conveniente; no es requisito.

## 9. Decisiones que el owner debe confirmar al aprobar
Preguntas ya resueltas: reporte de ventas por producto/variante **fuera** de este proyecto (§2). El revisor del diseño
(builder check, 2026-10-20) hizo siete preguntas; cinco quedaron respondidas en el texto como **propuesta** (cambiar las
opciones de un producto existente §3.3.1; conteo de escalas §4; copiar escalas §4; borrado de variantes §3.3 (luego corregido por el owner);
despliegue conjunto §6). Quedan por confirmar, al aprobar el documento, estas propuestas que cambian la estructura:
1. ✔ Opciones predefinidas «Talla» y «Color», sembradas **inactivas** en el primer acceso (§3.1).
2. ✔ Código de barras opcional y único por tenant; el escáner busca primero por código de barras y luego por `Code`
   (§3.3, §5).
3. ✔ **Corregida por el owner:** tope de 2.048 variantes por producto (§3.3).
4. ✔ Sin permisos nuevos: `catalog.product.read` y `catalog.product.manage` (§3.1).
5. ✔ **Corregida por el owner:** galería de hasta 250 fotos por producto; cada variante elige una (§2).
6. ✔ **Corregida por el owner:** una variante se borra si no está en uso; en uso, se desactiva (§3.3).
7. ✔ Despliegue de backend y frontend juntos, `productId` → `variantId` sin doble contrato (§6).

## 10. Lo que queda al builder
- Nombres de tablas, columnas, endpoints y DTOs; forma exacta de las migraciones y su orden entre módulos.
- Cómo se presenta la tabla de variantes en el formulario (generar combinaciones al elegir valores, editar en bloque).
- Textos de la interfaz, en español colombiano tuteando.
- Si la variante por defecto lleva el mismo id que el producto (§8) o se resuelve de otra forma.
- Límite de largo del código de barras.
- Cómo pagina o virtualiza la tabla de variantes con 2.048 filas, y cómo se sube y ordena una galería de 250 fotos.

## 11. Referencias en el código (develop, 2026-10-20)
- Catalog: `Catalog.Domain/Product.cs`, `ProductPrice.cs`, `PriceScale.cs`, `ProductPriceChange(Detector).cs`;
  `CatalogDbContext.cs` (índice único `(tenant_id, code)`); `CatalogDtos.cs`; `ProductEndpoints.cs`;
  `CatalogPermissions.cs`.
- Adaptadores por producto en `src/Bootstrapper`: `QuotationProductLookup`, `QuotationProductPricingLookup`,
  `QuotationPriceScaleMapping`, `PosProductLookup`, `ShopifyProductLookup`, `ProductImageLookup`,
  `PriceChangeReportSource`.
- Quotations: `QuotationItem.cs`, `OrderItem.cs`, `QuotationProductSnapshot.cs`, `QuotationItemProductLabel.cs`,
  `QuotationProductPricingResolver.cs`, `QuotationScaleGroupPricing.cs`, `OrdersExportColumnCatalog.cs`,
  `QuotationPdfDocument.cs`.
- Pos: `PosSaleLine.cs`, `IPosProductLookup.cs`, `CreatePosSale.cs`, `PosEndpoints.cs`.
- Shopify: `ShopifyPorts.cs` (`IShopifyProductLookup`), `ShopifyOrderImporter.cs` (emparejamiento exacto por SKU).
- Frontend: `features/catalog` (formulario de producto), `features/quotes/components/product-picker-dialog.tsx` y
  `add-order-items-dialog.tsx`, `features/pos` (barra de escaneo, carrito que junta por `productId`),
  `features/reports` (reporte de cambios de precio).
