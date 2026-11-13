# Líneas personalizadas de Shopify — diseño (backend)

Fecha: 2026-11-13. Estado: borrador para aprobación del owner.
Spec hermano en `qep-frontend`: la acción «Asociar a un producto» de la bandeja (se escribe tras aprobar este).

## Intención

Algunas apps de Shopify (formularios de contraentrega con upsell) agregan al pedido una **línea personalizada**: una línea
sin producto en Shopify. El caso real: «Garantía por 1 año + Envío Prioritario🚛», $5.000, en las tres tiendas que vienen de
Lulo CRM v1 (spec de la migración: `lulo-crm-migrator/docs/2026-11-12-migracion-lulo-crm-v1-design.md`). Cerca del 8 % de
sus pedidos la trae.

Hoy el importador casa cada línea por SKU (`ShopifyOrderImporter.ResolveLinesAsync`). Una línea sin SKU deja el pedido
entero en `product_unmatched`, y una línea personalizada **nunca** puede tener SKU: no hay producto en Shopify donde
ponérselo. Resultado: esos pedidos quedan en espera para siempre.

Decisión del owner (2026-11-13): la línea personalizada entra al pedido de QEP **como una línea con un producto de servicio**
del catálogo, no como cargo. El vínculo es una **asociación guardada por tienda**: `nombre exacto de la línea → variante`,
creada desde la bandeja, que luego aplica sola a los pedidos siguientes.

### Evidencia (backup `prod_lulo_crm_20261112`, colección `shopifyOrderTemp`, 108 payloads con línea personalizada)

- La línea trae `product_id: null`, `variant_id: null`, `product_exists: false`, `sku: null`, `vendor: null`,
  `properties: []`. El único id (`admin_graphql_api_id`) es de la línea en ese pedido.
- Ningún otro dato del pedido la identifica (`note_attributes`, descuentos y `source_name` son iguales con o sin ella).
- El nombre es **constante por tienda**, carácter por carácter, en todos los payloads:
  colombia-gadgets `Garantía por 1 año + Envío Prioritario🚛`; zqia1d-kv `… en este pedido🚛`;
  manga-store-colombia `… en este Pedido🚛`.
- Otra tienda manda otra línea personalizada («Envío Prioritario», `requires_shipping: true`): cada tienda le da su nombre
  y su uso. La llave confiable es **tienda + nombre exacto**.

## Objetivos

1. Un pedido con una línea personalizada cuyo nombre tiene asociación en su tienda se importa sin espera, con esa línea
   apuntando a la variante asociada.
2. Un pedido con una línea personalizada sin asociación espera en `product_unmatched`, y el detalle dice qué nombres faltan
   para que la bandeja ofrezca «Asociar a un producto».
3. Asociar desde la bandeja y reintentar importa el pedido.

## No objetivos

- Casar líneas de productos reales sin SKU. Una línea con producto en Shopify y sin SKU sigue esperando como hoy: la salida
  es ponerle el SKU en Shopify.
- Casar por id de producto o variante de Shopify (sería otro spec).
- Tratar la línea como cargo del pedido (opción descartada por el owner).
- Recuperar el nombre de pedidos que ya están en espera antes del despliegue (ver «Pedidos en espera de antes»).

## Anti-objetivos

- Que una línea de producto real sin SKU case por nombre con una asociación: el nombre de un producto lo edita el comercio y
  casaría con lo que no es.
- Que una asociación de una tienda aplique en otra del mismo tenant.

## Enfoques considerados

1. **Asociación guardada en el módulo Shopify** (elegido por el owner). Tabla nueva en `shopify`, creada desde la bandeja.
   No toca Catalog; cada variación del nombre se asocia una vez; el migrador la puede sembrar.
2. **Campo «nombre en Shopify» en la variante de Catalog.** Mete un concepto de Shopify en Catalog, toca su formulario y
   necesita una lista por variación. Descartado.
3. **Casar por id de Shopify.** No sirve: la línea personalizada no tiene id de producto ni de variante.

## Diseño

### Lectura de la línea

`ShopifyLine` suma dos campos con default, para que los snapshots ya guardados en `shopify.orders.payload` sigan
deserializando:

```csharp
public sealed record ShopifyLine(
    string? Sku, decimal Quantity, decimal OriginalUnitPrice, decimal DiscountAmount,
    string? Title = null, bool IsCustom = false);
```

- **REST** (`RestOrderParser`): `Title` = `title` de la línea; `IsCustom` = `product_id` nulo **y** `variant_id` nulo.
- **GraphQL** (`GraphQlOrderParser` y la consulta de `ShopifyGraphQlClient`): `Title` = `title`; `IsCustom` = `variant`
  nulo **y** `product` nulo. La consulta pide esos campos.
- No se usa `product_exists`: también es `false` cuando el producto se borró después de la venta, y esa línea no es
  personalizada (tiene ids). Una línea con ids de un producto borrado sigue el camino del SKU.

### Asociación

Tabla `shopify.custom_line_mappings`:

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid PK | GUID v7 |
| `tenant_id` | uuid | todo método de repositorio lo recibe |
| `connection_id` | uuid | la tienda (`shopify.stores.connection_id`) |
| `title` | varchar(255) | nombre de la línea, recortado (`Trim`), sensible a mayúsculas y emojis |
| `variant_id` | uuid | variante de Catalog; sin FK (otro esquema) |
| `created_at`, `created_by` | | auditoría mínima |

Índice único `IX_custom_line_mappings_connection_title` sobre `(connection_id, title)`. Una violación se traduce por
**nombre de índice** a `409 shopify.custom_line.already_mapped`, en Infrastructure.

Comparación: `Title.Trim()` exacto (ordinal). No se normalizan mayúsculas ni emojis: las tres tiendas muestran que la
diferencia entre «pedido» y «Pedido» es del comercio, y cada una casa su propio nombre.

La asociación se ata a la conexión, como la configuración de la tienda. Borrar la tienda y volver a enlazarla crea otra
conexión: hay que volver a asociar (es una vez por nombre).

### Importador

`ResolveLinesAsync`:

1. Líneas con `IsCustom` y `Title` no vacío: se buscan en las asociaciones de la conexión (`IShopifyCustomLineMappings`,
   una consulta por pedido con todos los nombres). Con asociación, se resuelve la variante por id (nuevo método del puerto
   `IShopifyProductLookup.FindByVariantIdsAsync`, que devuelve el mismo `ShopifyProductRef`). Variante inexistente o
   `!IsActive` → no casa.
2. El resto, por SKU, igual que hoy.
3. Precio, descuento e IVA: igual que una línea por SKU (precio y descuento de Shopify vía `ShopifyPricing`, IVA de la
   variante).

`wait_detail` de `product_unmatched` pasa a `{"skus":[…], "customTitles":[…]}`:

- `skus`: los SKU que no casaron de líneas **no** personalizadas (`null` para una sin SKU), como hoy.
- `customTitles`: los nombres de líneas personalizadas sin asociación o con variante inactiva, sin repetir. Una línea
  personalizada sin `Title` (no debería pasar) entra en `skus` como `null`.
- Las dos llaves están siempre presentes (arreglos vacíos si no aplica): el frontend no tiene que tratar la ausencia.

### Endpoints

Bajo `/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/custom-line-mappings`, permiso
`quotations.order.manage` (escritura) y `quotations.order.read` (lectura), **igual que la bandeja** (decisión del owner,
2026-11-13): quien atiende los pedidos en espera asocia y reintenta en el mismo lugar, sin depender de quien administra la
integración. Son permisos que ya existen, así que no hay política nueva que registrar. El handler revalida tenant, permiso y módulo con `ShopifyAuthorization.EnsureAsync` y responde 403, nunca 404.

- `GET` → lista `[{id, title, variantId, productName, variantCode, isActive}]` ordenada por `title`. BFF: el nombre y el
  código de la variante viajan para que la pantalla no consulte Catalog por cada fila.
- `POST {title, variantId}` → 201 con la fila. Validador FluentValidation: `title` no vacío y ≤ 255 tras `Trim`; `variantId`
  no vacío. La variante debe existir en el tenant y estar activa (`422 shopify.custom_line.variant_not_found` /
  `shopify.custom_line.variant_inactive`). Repetido → `409 shopify.custom_line.already_mapped`.
- `DELETE /{id}` → 204. Borrar no toca pedidos ya importados.

Lo que hace la bandeja: `POST` de la asociación y luego `POST /orders/{orderId}/retry` (ya existe). No se reintenta solo:
el reintento es la acción explícita que ya conoce la pantalla.

Auditoría: crear y borrar una asociación se auditan con el publicador del módulo, como la configuración de la tienda.

### Pedidos en espera de antes

El espejo guarda el snapshot, no el JSON de Shopify (`ShopifyOrderIngestion.WritePayload`). Un pedido que entró en espera
**antes** del despliegue no tiene `Title` ni `IsCustom`, y su línea sigue en `skus` como `null`. Se resuelve cuando Shopify
lo vuelva a mandar (webhook de actualización o conciliación). Se acepta: los tenants que motivan el cambio llegan después
del despliegue, y hoy no hay tiendas reales en QEP.

### Migración Lulo CRM v1

El migrador (fase 2, con la conexión ya enlazada) crea el producto de servicio de cada tenant y siembra la fila de
`custom_line_mappings` con el nombre exacto de su tienda. Este spec tiene que estar desplegado antes de la fase 2.

Decisión de diseño: el migrador escribe **directo por SQL**, igual que el catálogo, los clientes y los pedidos, con
`INSERT … ON CONFLICT (connection_id, title) DO NOTHING`. No pasa por el `POST`, porque necesitaría una sesión de miembro, y
la API externa (`IApiCaller`) no recibe permisos internos. Eso vuelve la tabla un contrato con el repo del migrador: las
columnas de arriba, `title` ya recortado y `created_by` = id del dueño del tenant. Renombrar una columna rompe la siembra; el
spec del migrador la cita por nombre. La siembra no deja auditoría: se acepta en una migración de un solo uso.

## Pruebas

TDD, RED antes que GREEN.

- Unitarias de los parsers: línea personalizada REST y GraphQL (`IsCustom`, `Title`); línea de producto borrado (ids
  presentes, `product_exists: false`) no es personalizada; snapshot viejo sin los campos deserializa con defaults.
- Unitarias del importador (`FakeProductLookup` + doble de asociaciones): casa por asociación; sin asociación espera con
  `customTitles`; variante inactiva espera; línea de producto real sin SKU con un nombre igual a una asociación **no** casa;
  mezcla de SKU faltante y nombre faltante lista ambos.
- Integración (`ShopifyApiHarness`): POST/GET/DELETE con 403 de otro tenant, 409 por nombre repetido, 422 por variante;
  flujo completo webhook → espera → asociar → reintentar → pedido importado con la variante asociada; un segundo pedido con
  el mismo nombre entra sin espera.
- `ShopifyLayerTests` sigue verde: la búsqueda por id va por el puerto y su adaptador en Bootstrapper.

## Queda al builder

- Nombres de clases y archivos, y la forma del doble de asociaciones.
- Si la búsqueda por id reusa `IProductRepository` o una consulta nueva en el adaptador.
- El texto exacto de la auditoría.
- Cómo obtiene el `GET` el nombre y el código de la variante: `ShopifyProductRef` no trae el nombre; puede ser un resultado
  más rico del puerto o un método aparte.
