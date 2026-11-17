# Productos desde Shopify

Estado: borrador para aprobación del owner (2026-11-17). Trabajo 2 de 3 del onboarding de Shopify (1 reglas de empresa
→ 2 productos → 3 onboarding). Las decisiones S-2 a S-7 del spec `2026-10-21-onboarding-design.md` §9.8 son del owner;
lo marcado **(criterio)** lo decidió quien escribe con el owner fuera de línea (pidió resolver las dudas con buenas
prácticas) y se puede revertir en la revisión.

## 1. Por qué

Cada línea de un pedido de Shopify se empareja hoy por SKU con el `Code` de una variante activa de QEP
(`ShopifyLineResolver.cs`, `ShopifyOrderImporter.cs:140-143`). Una empresa nueva no tiene productos, así que **todos**
sus pedidos quedan en espera con `product_unmatched`. Las variantes sin SKU nunca se emparejan.

## 2. Objetivos, no objetivos, anti-objetivos

**Objetivos**
1. Traer el catálogo de la tienda a QEP con un trabajo asíncrono que muestra su progreso (§9.4 paso 4).
2. Emparejar las líneas de pedido por el id de la variante de Shopify y después por SKU (S-3), así entran también las
   variantes sin SKU.
3. Mantener al día lo importado con `products/create|update|delete` (S-2: en lo importado manda Shopify).
4. Decir cuánto del catálogo de la tienda ya tiene su par en QEP (cobertura), para «Ya los tengo en Lulo CRM».

**No objetivos**
- La pantalla (trabajo 3). Este trabajo deja la API y el trabajo listos.
- Inventario, colecciones, metacampos, más de una imagen por producto, imágenes por variante **(criterio:** una imagen
  por producto, la destacada; es lo que necesita una cotización y el resto multiplica descargas).
- Escribir productos de QEP en Shopify.

**Anti-objetivos**
- Pisar un producto que ya existía en QEP y se emparejó por SKU (S-2).
- Duplicar un producto al reimportar o al recibir el webhook y la importación a la vez.
- Que un fallo de una imagen o de un producto tumbe toda la importación.
- Que el asesor edite en QEP el nombre o el precio de un producto importado y el siguiente webhook lo pise en silencio.

## 3. El vínculo

Tabla nueva `shopify.product_links` (módulo Shopify, como `custom_line_mappings`):

| Columna | Qué es |
|---|---|
| `tenant_id`, `connection_id` | la tienda |
| `shopify_product_id`, `shopify_variant_id` | ids numéricos de Shopify (`bigint`) |
| `variant_id`, `product_id` | la variante y el producto de QEP |
| `origin` | `Imported` (QEP lo creó: manda Shopify) o `MatchedBySku` (ya existía: no se pisa) |
| `created_at`, `updated_at` | |

Índices únicos `(connection_id, shopify_variant_id)` y `(connection_id, variant_id)`. El primero es el que frena el
duplicado entre el webhook y la importación: se traduce **por nombre de índice** en `ShopifyUnitOfWork` y quien pierde
relee el vínculo existente (precedente `IX_orders_tenant_external_key`).

## 4. Emparejar líneas de pedido

- `ShopifyLine` gana `VariantId` (`long?`): REST `line_items[].variant_id`, GraphQL `lineItems.variant.id` (el número del
  GID). Una línea personalizada sigue sin variante.
- `ShopifyLineResolver`: primero `product_links` por `(connection_id, shopify_variant_id)`; si no hay vínculo, el SKU
  como hoy; después las líneas personalizadas como hoy. Una variante vinculada pero inactiva en QEP sigue siendo
  `product_unmatched` (regla de hoy).
- La detección de ediciones (`ShopifyEditPlanner`) usa el mismo resolver: no hay segundo camino.

## 5. La importación

### 5.1 Permiso
- `read_products` entra en `ShopifyAppOptions.DefaultScopes`. Las instalaciones nuevas lo piden desde el inicio (S-5).
- Una tienda instalada antes no lo tiene (`ShopifyFieldKeys.Scopes`). Importar o pedir la cobertura responde **422
  `shopify.products.scope_missing`** con `reauthorizeUrl`: la URL de autorización de Shopify que ya arma
  `ShopifyInstallStarter.AuthorizeUrl`, con los scopes actuales. Al volver, el canje guarda los scopes nuevos (camino de
  hoy). Un scope `write_products` cuenta como `read_products` (regla de hoy de `EnsureScopes`).

### 5.2 El trabajo
- Tabla `shopify.product_imports`: `id`, `tenant_id`, `connection_id`, `mode` (`All` | `Missing`), `status` (`Pending`,
  `Running`, `Done`, `Failed`), `total`, `done`, `created`, `matched`, `without_sku`, `skipped`, `failure_code`,
  `requested_by` (membresía), `lease_until`, `attempts`, `created_at`, `finished_at`.
- Una importación viva por tienda: índice único parcial sobre `connection_id` con `status IN ('Pending','Running')`;
  el segundo `POST` responde **409 `shopify.products.import_running`** (traducido por nombre de índice).
- Un worker reclama con `FOR UPDATE SKIP LOCKED` y lease (precedente `ExportJob`). Es un hosted service nuevo: entra en
  `MultiReplicaInventory` con su evidencia, y la tabla, como cola nueva, en `QueueDoorbellInterceptor` (`Tables`) con su
  cola en `DoorbellQueues`.
- Recorre `products(first: 50, query: "status:active OR status:draft")` con sus variantes paginadas aparte (las de más de
  100), con el ritmo de costo de `ShopifyGraphQlClient` (`PaceAsync`, `Throttled`). `total` sale del conteo inicial
  (`productsCount`) y `done` avanza por **producto**, guardado cada página, para que la pantalla muestre «Importando 340
  de 1.200» **(criterio:** por producto, no por variante: es lo que el comercio ve en su lista de Shopify).
- Un producto que falla (validación de Catalog, imagen) cuenta en `skipped` con su motivo en una tabla hija
  `product_import_issues` (`shopify_product_id`, `title`, `code`), y la importación sigue.
- Un fallo de la tienda (token vencido, 401/403, scope retirado) termina `Failed` con `failure_code`
  (`token_expired`, `scope_missing`, `unreachable`); un `Throttled` o 5xx reintenta con el lease, hasta 4 intentos.
- Al terminar `Done`, la tienda queda con `products_synced_at` (columna nueva de `shopify.stores`): desde ahí los
  webhooks de productos crean, actualizan y desactivan (§6).

### 5.3 Cómo se mapea cada producto
- **Estado (S-6):** `ACTIVE` → activo; `DRAFT` → inactivo; `ARCHIVED` no se trae.
- **Opciones:** un producto con una sola variante y la opción `Title`/`Default Title` entra sin opciones. Si no, cada
  opción de Shopify (hasta 3) se busca por nombre en la biblioteca, sin distinguir mayúsculas ni espacios de borde; si no
  existe se crea, si está inactiva se activa, y lo mismo con sus valores.
- **Variante:** `Code` = SKU recortado. Sin SKU, `Code` = `shp-{id numérico de la variante}` **(criterio:** único, estable
  y reconocible; cabe en los 60) y cuenta en `without_sku`. Código de barras, si no choca con otro del tenant (si choca,
  se omite y se anota). Precio en la moneda de la tienda.
- **Impuesto (S-4 y trabajo 1):** la empresa emisora de la tienda decide.
  - Empresa sin IVA → el producto entra sin tasa y el precio tal cual.
  - Empresa con IVA → variante `taxable` toma la tasa de IVA activa de 19 % del tenant (si hay varias, la más antigua; si
    no hay, sin tasa y se anota); no `taxable` → la tasa Exento si existe, si no sin tasa. Si la tienda tiene
    `taxesIncluded = false`, el precio se guarda con el IVA de esa tasa sumado, redondeado con la regla de la moneda.
- **Imagen:** la destacada del producto, descargada con tope de tamaño a un archivo de Storage del producto. Si falla,
  el producto entra sin imagen y se anota.
- **Emparejamiento por SKU (S-2):** una variante cuyo SKU ya es el `Code` de una variante de QEP no crea nada: se vincula
  `MatchedBySku`. Si **todas** las variantes de un producto casan, se vincula todo y no se crea producto. Si casan
  algunas, se vinculan esas, el producto no se crea y las demás cuentan en `skipped` con el motivo `partial_match`
  **(criterio:** crear un producto con la mitad de las variantes partiría un producto de QEP en dos).
- **Modo `Missing`:** sólo los productos con alguna variante sin vínculo.

### 5.4 Escritura en Catalog
- Shopify no referencia Catalog: puerto `IShopifyCatalogWriter` en `ShopifyPorts.cs`, adaptador en Bootstrapper (como
  `ShopifyProductLookup`). Crea y actualiza por los mismos agregados de Catalog (`Product.Create`, `Update`, opciones),
  sin `IExecutionContext` de miembro: es un camino de sistema, autorizado por la importación.
- `Product` gana `ExternalSource` (`null` | `"shopify"`), sólo para los creados por la importación.

### 5.5 Qué protege QEP en un producto importado
- Con `ExternalSource = "shopify"`, la API de Catalog rechaza cambiar nombre, opciones, variantes (alta, baja, código) y
  precios de lista con **422 `catalog.product.managed_externally`** (en el validador del `PUT`, con el campo en
  `errors`). Se siguen editando la tasa de IVA, las escalas, los empaques, los costos, la descripción, la categoría, la
  galería y `visibleInStore` (S-2).
- **Desvincular:** `DELETE …/shopify/products/{productId}/link` quita los vínculos y `ExternalSource`; el
  producto queda como propio de QEP y los webhooks lo ignoran **(criterio:** la salida para quien quiera dejar de
  sincronizar un producto, sin borrar nada).

## 6. Webhooks de productos

- `products/create`, `products/update` y `products/delete` entran en `ShopifyWebhookTopics.Registered` (Integrations) y
  en las constantes del módulo Shopify, amarradas por `ShopifyWebhookTopicsParityTests`. Las tiendas ya enlazadas las
  registran con el probador de la conexión o el siguiente reenlace (`ShopifyWebhookRegistration.EnsureAsync`).
- Sin `products_synced_at` en la tienda, se ignoran (`Ignored`, motivo `products_not_synced`): el comercio todavía no
  eligió traer sus productos.
- **create:** se importa el producto con las reglas de §5.3.
- **update:** si el producto tiene vínculos `Imported`, se actualizan nombre, opciones, variantes, códigos, precios e
  imagen. Una variante nueva se agrega, una que ya no está se manda en `deletedVariantIds` (Catalog la borra o la
  desactiva según su uso, regla de hoy). `DRAFT` desactiva, `ACTIVE` reactiva, `ARCHIVED` desactiva. Con vínculos
  `MatchedBySku` sólo se actualizan los vínculos de variantes nuevas por SKU, nunca el producto (S-2). Sin vínculos: como
  `create`.
- **delete:** desactiva los productos `Imported` (nunca borra: las líneas de documentos los referencian) y deja los
  vínculos. Los `MatchedBySku` no se tocan.
- El webhook trae el producto completo en REST; si trae más de 100 variantes, se relee por GraphQL **(criterio:** el
  payload de REST corta las variantes).
- Orden: un `update` viejo que llega después de uno nuevo no pisa: se compara `updated_at` del payload contra
  `shopify_updated_at` guardado en el vínculo (columna más de §3).

## 7. Contrato (BFF)

```
POST /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/products/import   integrations.connection.manage
     { "mode": "All" | "Missing" }  → 202 { importId }
     409 shopify.products.import_running · 422 shopify.products.scope_missing { reauthorizeUrl }
GET  …/stores/{connectionId}/products/import            → 200 la última importación o null
     { id, mode, status, total, done, created, matched, withoutSku, skipped, failureCode,
       issues: [{ shopifyProductId, title, code }] (hasta 50) }
GET  …/stores/{connectionId}/products/coverage           → 200 { shopifyVariants, matched, catalogEmpty,
       missingSample: [{ sku, title }] (hasta 20) }   · 422 scope_missing
DELETE /api/v1/tenants/{tenantId}/shopify/products/{productId}/link → 204   (busca los vínculos del producto
       en cualquier tienda del tenant: el detalle de Catalog no conoce la conexión; ajuste por el spec del frontend H13)
```

La cobertura consulta Shopify en el momento (SKUs e ids de variante) y la compara con vínculos y códigos de QEP; con
catálogos grandes tarda, y el resultado no se guarda **(criterio:** se llama una vez al abrir el paso; cachear es
optimización de después). El paso 4 del onboarding (trabajo 3) la compone en su `GET …/setup`.

## 8. Despliegue

- Migraciones: Shopify (`product_links`, `product_imports`, `product_import_issues`, `stores.products_synced_at`) y
  Catalog (`products.external_source`). Todas aditivas.
- La app de Shopify necesita `read_products` en su configuración de scopes del Partner Dashboard antes de desplegar, y
  `Shopify:App:Scopes` en `k8s/prod-configMap.yaml` y README.
- Los webhooks de productos se registran solos en el siguiente enlace o prueba de la conexión.

## 9. Verificación

- Unitarias: mapeo (opciones, `Default Title`, sin SKU, códigos de barras que chocan, `taxable`, `taxesIncluded=false`,
  empresa sin IVA), emparejamiento parcial, resolver por id de variante antes que SKU, orden de `updated_at`.
- Integración: importación de punta a punta con un doble del gateway de Shopify (paginación, `Throttled`, fallo de un
  producto, `Failed` por token), 409 con una importación viva, webhook y importación a la vez dejan un solo producto,
  `managed_externally` en el `PUT`, desvincular.
- `ArchitectureTests`: Shopify no referencia Catalog; `MultiReplicaInventory` con el worker nuevo.
- Contra Shopify real (manual, con el trabajo 3): tienda con variantes sin SKU y con `taxesIncluded=false`.
- Las pruebas se escriben; las corre el owner al final.

## 10. Decisiones de la revisión (criterio, owner fuera de línea)

- **P1. Imagen.** Se sube como archivo `FileOwnerType.Product` con `ownerId` = id del producto, en una segunda pasada
  después de crearlo, por el camino de servidor de Storage (`IStorageSystemUploads` o una variante para dueños no de
  sistema). Quien construye verifica que el barrido de staging no borre un archivo de producto ya referenciado por la
  galería; si lo borra, agrega la sonda.
- **P2. Moneda.** Un solo precio, en la moneda de la tienda. Si esa moneda no está en el catálogo de monedas de QEP
  (`Currencies.Normalize`), la importación termina `Failed` con `currency_unsupported` antes de crear nada.
- **P3. Autor.** La importación audita como `requested_by`; el webhook, como sistema (el patrón de auditoría de sistema
  que ya use Catalog o Shopify; si no hay, actor nulo con origen `shopify`). Los eventos de outbox que Catalog ya publica
  por producto se aceptan tal cual.
- **P4. Empresa y tasas.** Importar exige que la tienda tenga empresa: si no, **422 `shopify.products.company_required`**.
  La **tasa** del producto se asigna siempre por `taxable` (19 % o Exento, si existen), sin mirar si la empresa cobra IVA:
  los documentos de una empresa sin IVA ya cobran 0 % (trabajo 1, §5.2), y así un cambio posterior de la empresa no deja
  tasas viejas. Lo único que depende de la empresa es sumar el IVA al precio con `taxesIncluded = false` (sólo empresas
  con IVA). Cambiar la empresa después no reprocesa precios.
- **P5. Dependencia.** Se construye apilado sobre `feature/reglas-empresa` y se despliega después de él.
- **P6. Sin cruces entre productos.** Una variante nueva (importación o `update`) cuyo SKU es de una variante de **otro**
  producto de QEP no se vincula: se anota `sku_in_other_product` y se salta. Todos los vínculos de un producto de QEP
  apuntan a variantes de ese producto.
- **P7. Límites.** Un producto que no cabe en Catalog (nombre de opción o valor más largo que el límite de la biblioteca,
  validación de Catalog) se salta entero con su código. Nunca se crea a medias.
- **P8. Fallas en webhooks.** No hay importación a la que anotarlas: se loguean (id de producto y código, nunca el
  payload) y suman en un contador `qep_shopify_product_sync_failures_total{reason}`.
- **P9. Producto borrado y recreado en Shopify.** Si el SKU casa con una variante inactiva de un producto `Imported` de la
  misma conexión, se vuelve a vincular como `Imported` y el producto se reactiva (no queda como `MatchedBySku` muerto).
  **Sólo si el producto viejo ya no existe en Shopify** (revisión 2026-11-17): se consulta el producto de Shopify de los
  vínculos viejos y, si sigue vivo, el nuevo es un duplicado del comerciante: se salta con `sku_in_other_product` y no
  escribe nada. Sin esa consulta, original y duplicado se quitarían los vínculos en cada webhook.
- **P10. Registro de webhooks sin `read_products`.** `ShopifyWebhookRegistration.EnsureAsync` registra los
  `products/*` sólo si los scopes concedidos de la conexión incluyen `read_products` (o `write_products`); sin él no los
  intenta y la conexión no pasa a `NeedsAttention`. El canje con el scope nuevo los registra.
- **P11. Tienda vuelta a enlazar** (revisión 2026-11-17). Borrar la conexión y enlazar la tienda otra vez da una conexión
  nueva, y los vínculos quedan en la vieja. Las conexiones viejas son las otras tiendas (`shopify.stores`) del mismo tenant
  con el mismo dominio, el criterio de `AdoptWaitingOrdersAsync`. Un producto de Shopify sin vínculos en la conexión actual
  **adopta** los de una vieja por id de producto de Shopify (se mueven con su origen, versión e imagen), así que no se
  duplica ni un producto sin SKU; y un SKU que casa con un importado de una vieja cuenta para P9 (con la misma consulta de
  si sigue vivo). Desinstalar o `shop/redact` **no** sueltan los vínculos de la vieja: no son datos personales, y son lo que
  permite adoptarlos. Otro tenant con la misma tienda nunca adopta.

## 11. Lo que queda a quien construye

- Nombres de clases del worker y del procesador, y el tamaño de lote por página.
- Si la imagen se descarga en el mismo paso del producto o en una segunda pasada.
- El texto de los motivos en `product_import_issues` (códigos `snake_case`).
