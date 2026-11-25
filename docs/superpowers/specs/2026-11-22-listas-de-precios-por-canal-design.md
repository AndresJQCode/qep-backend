# Listas de precios por canal (2026-11-22)

Spec del frontend: pendiente (`qep-frontend`, pantallas «Listas de precios» y «Precios por canal»).
Estado: aprobado por el owner el 2026-11-22 («Ok continuar»: rango propuesto de D-3 y P-1 fuera de alcance) y construido en la rama
`claude/price-list-by-channel-kkjwkr`. Lo que la construcción ajustó respecto de la primera versión está marcado **(ajuste)**.

## Problema

Hoy una variante tiene **un** precio base por moneda (`catalog.product_prices`) y todos los canales lo usan tal cual: el POS
(`PosProductLookup` → `PosProductMapping.PriceIn`), la tienda pública (`StoreCatalogReader`, `QuotePublicCart`), las
cotizaciones y pedidos (`QuotationProductPricingResolver`) y el catálogo del distribuidor (`WholesaleOffers`, con el % de
descuento de su lista mayorista encima). El owner quiere precios distintos por canal: por ejemplo, la tienda online más cara o
más barata que el POS.

## Decisiones del owner (brainstorming 2026-11-22)

| # | Pregunta | Respuesta |
| --- | --- | --- |
| O-1 | Canales | POS, Tienda online, Cotizaciones y pedidos, Distribuidores. Shopify queda fuera: sólo importa precios. |
| O-2 | Cómo define precios una lista | Ajuste % sobre el precio base **más** precios fijos para variantes puntuales. |
| O-3 | Variante sin precio en la lista | Usa el precio base. |
| O-4 | Asignación | Una lista activa por canal; «ninguna» = precio base. |
| O-5 | Mayorista | **Unificar** sus listas con las nuevas. Sin migración de datos: no hay clientes en producción usándolas. |
| O-6 | Escalas | Opción A: se quedan en la variante y su % se calcula sobre el precio del canal. |
| O-7 | Redondeo | El ajuste % usa la regla de redondeo del tenant por moneda; el precio fijo no se redondea. |
| O-8 | Visibilidad, exclusiones y «mostrar precios» del mayorista | Quedan en la lista y **sólo** los usa el catálogo del distribuidor. |
| O-9 | Dónde se asigna | Pantalla «Precios por canal» para POS, Tienda y Cotizaciones; el distribuidor sigue eligiendo la suya en su ficha. |

## Decisiones de diseño

- **D-1. La lista vive en Catalog, no en Wholesale.** Los precios son de Catalog, y el POS, la tienda y las cotizaciones no
  pueden depender de la capacidad `wholesale`. Nuevo agregado `Modules.Catalog.Domain.PriceList`; `Wholesale` deja de tener
  `PriceList` y su `Distributor.PriceListId` pasa a ser una referencia blanda (`Guid`) a la lista de Catalog, validada por un
  puerto (D-11).
- **D-2. El precio de una variante en una lista y una moneda** es, en este orden:
  1. el **precio fijo** de la lista para esa variante y esa moneda, tal cual (O-7);
  2. si no hay precio fijo y la variante tiene precio base en la moneda: `rounding.Apply(base × (1 + ajuste / 100))`, con la
     regla de `ITenantPriceRounding` para esa moneda (O-7; misma regla que hoy usa `WholesalePricing.ListPrice`);
  3. si no tiene ninguno de los dos: sin precio, el mismo caso que hoy (`PriceMissing` en el POS, oculta en la tienda,
     `quotation.item.product_price_unavailable` en la cotización).

  **(ajuste)** Un precio fijo **sólo reemplaza**: una variante sin precio base en una moneda sigue sin precio en ella en todo
  canal. Así el «tiene precio en la moneda» que la tienda, el POS y el mayorista filtran en SQL vale igual con lista, sin
  reescribir esas consultas. La regla vive una sola vez, en `PriceList.PriceFor` del dominio, y nadie la copia.
- **D-3. El ajuste es un porcentaje con signo.** `adjustmentPercent` de −100 a 999,99, con dos decimales como máximo (la columna
  es `numeric(5,2)`, como la de la lista mayorista): negativo es descuento, positivo es recargo y 0 es «igual al base» (útil para
  una lista que sólo tiene precios fijos). El `discountPercent` de 0 a 100 de las listas mayoristas equivale a
  `adjustmentPercent = −discountPercent`. **(ajuste)** El tope es 999,99 y no 1000: es lo que cabe en la columna.
- **D-4. Canales con asignación global: `Pos`, `Store` y `Quotations`.** Es un enum nuevo, `PriceChannel`, y se guarda en
  `catalog.channel_price_lists` (una fila por tenant y canal, o ninguna = precio base, O-3/O-4). Los distribuidores no van en
  esa tabla: cada uno tiene su lista asignada (O-9).
- **D-5. Qué canal precia cada documento.** El canal se deriva del origen y no se guarda en la línea:

  | Documento | Canal |
  | --- | --- |
  | Venta y vista previa del POS, buscador y catálogo del POS | `Pos` |
  | Catálogo público, ficha, carrito y pedido de la tienda; edición de un pedido `Source = Store` | `Store` |
  | Cotización (alta, edición, ítems, cambio de moneda, recálculo), pedido `Manual` o `Quotation` y sus ediciones, regalos | `Quotations` |
  | Catálogo, vista previa, orden y pedido del distribuidor (`Source = Wholesale`) | La lista del distribuidor |
  | Pedido `Source = Shopify` | Sin cambio: el precio que trae Shopify |

  Las líneas ya guardadas guardan su precio unitario, como hoy: cambiar una lista o su asignación no toca documentos
  existentes. Sólo los toca un recálculo que hoy ya vuelve a leer el catálogo.
- **D-6. Escalas (O-6).** Siguen definidas en la variante (tramos, descuento, restricción de múltiplo o empaque). El descuento
  del tramo se aplica sobre el precio del canal (D-2) en vez del base: `PriceScale.FinalFor(precioCanal, descuento)` y
  `QuotationDiscountResolver` sin cambios de regla. **No hay casilla «aplica escalas» por lista:** cada canal ya decide si las
  usa, y eso no cambia. La tienda las usa con `StorePricingMode.QuantityScales`, la cotización cuando no es detal
  (`isRetail`), y el POS y el distribuidor nunca.
- **D-7. Unificación con Mayorista (O-5, O-8).** La lista de Catalog tiene, además del precio, los campos que hoy tiene
  `Wholesale.Domain.PriceList`: `Visibility` (`AllActive`/`Categories`), `CategoryIds`, exclusiones de categorías y productos
  (mismos topes: 200, 200, 1000) y `ShowPrices`. **Sólo los lee el catálogo del distribuidor** (`WholesaleOffers`). POS, tienda
  y cotizaciones los ignoran: la tienda sigue usando `VisibleInStore` del producto. El CRUD `/wholesale/price-lists/*` y el
  permiso `wholesale.price_list.manage` se retiran en el mismo cambio.

  **(ajuste)** Se quedan en Mayorista las tres rutas que se arman con el lector del catálogo del distribuidor:
  `POST /wholesale/price-lists/preview` (ahora con `adjustmentPercent`; sólo aplica el ajuste, los precios fijos se ven en su
  tabla), `GET /wholesale/categories` y `GET /wholesale/products`. Piden `catalog.price_list.manage` **y**
  `wholesale.distributor.read`: el segundo lo quita `ModuleEntitlementMask` con el módulo apagado, así que sin «wholesale»
  siguen en 403. El precio de la vista previa sale del mismo resolutor de Catalog (`IPriceListPricing.BookForAdjustmentAsync`).

  **(ajuste)** Lo que ve el distribuidor no cambia de forma: `priceList.discountPercent` del catálogo y
  `basePrice`/`discountPercent`/`listPrice` de cada oferta siguen viajando. El descuento es el ajuste con el signo al revés (un
  recargo es un descuento negativo), acotado a −999,99…100 porque la orden de compra lo guarda en `numeric(5,2)`. Con precio
  fijo se deriva del base y el fijo; si no cabe, la oferta va sin descuento y con el fijo como base, para que «base y descuento»
  siempre cierre. El lado del mayorista sí cambia: `DistributorPriceListDto` es `{ id, name, adjustmentPercent, exists }`.
- **D-8. Sin migración de datos (O-5).** **(ajuste, expand/contract)** La migración de Wholesale `MovePriceListsToCatalog` sólo
  borra `FK_distributors_price_lists_price_list_id` y su índice: `distributors.price_list_id` sigue `NOT NULL` y desde ahora
  guarda el id de una lista de Catalog, sin FK. La tabla `wholesale.price_lists` **no** se borra en esta versión porque los pods
  viejos del rollout todavía la leen; queda sin uso y la borra una migración de contrato posterior (`DROP TABLE IF EXISTS`,
  pendiente). Un distribuidor cuya lista no existe en Catalog —todos los de antes de la unificación— no ve precios: recibe el
  mismo 403 `wholesale.relationship.inactive` que ya daba una lista faltante, y en la pantalla del mayorista sale con
  `priceList.exists: false` para que le asignen una. Es el mismo criterio de D-14 del spec 2026-11-19: sin información no se
  muestra un precio. Invitar y editar un distribuidor siguen exigiendo `priceListId` (404 `wholesale.price_list.not_found` si no
  es una lista de Catalog del tenant). Antes de desplegar se confirma en producción que `wholesale.price_lists` está vacía
  (paso manual).
- **D-9. Los precios fijos se editan aparte del encabezado.** Una lista puede tener miles de filas (2.048 variantes por
  producto), así que no viajan en el `PUT` de la lista. Se paginan y se editan por lote (D-12), con el mismo `If-Match` de la
  lista: todo cambio de precios fijos sube su `Version`. Un precio fijo de una variante borrada se va por `CASCADE`, como su
  historial de precios.
- **D-10. Permiso nuevo: `catalog.price_list.manage`.** Cubre listas, precios fijos, vista previa y la pantalla «Precios por
  canal». Lo reciben los roles que hoy tienen `wholesale.price_list.manage` y los que tienen `catalog.product.manage`. No
  exige la capacidad `wholesale`. Lleva las dos mitades de siempre (constante en `CatalogPermissions` y política en
  `AddAuthorization`). Sin permiso de lectura aparte: los canales reciben el precio ya resuelto, nunca la lista.
- **D-11. Catalog no ve Wholesale: los conteos pasan por un puerto.** `IPriceListUsage` (Catalog.Application, implementado en
  Bootstrapper leyendo `WholesaleDbContext`) responde cuántos distribuidores usan cada lista, para `distributorCount` y para
  el 409 al borrar. En sentido contrario, `IWholesalePriceLists` (Wholesale.Application, implementado en Bootstrapper sobre
  Catalog) da la lista de un distribuidor (precio, visibilidad, exclusiones, `ShowPrices`) y valida que el `priceListId` del
  cuerpo exista en el tenant (si no, 404 `wholesale.price_list.not_found`, el código que el formulario ya conoce).
- **D-12. Un solo resolutor de precios.** `IChannelPricing` (Catalog.Application) recibe `(tenantId, canal o priceListId,
  moneda, variantIds)` y devuelve el precio por variante (D-2) o nada. Los adaptadores de Bootstrapper que hoy llaman
  `CatalogVariantReads.PricesOf` pasan por él: `PosProductLookup`, `StoreCatalogReader` (también el mínimo y el máximo del
  listado), la fuente de `QuotationVariantPricingRef` y `WholesaleCatalogReader`. `WholesalePricing.ListPrice` desaparece: el
  % del distribuidor ya es el ajuste de su lista. Sin caché en esta entrega.
- **D-13. Auditoría por outbox** (`ICatalogAuditPublisher`): `catalog.price_list.created`, `.updated`, `.deleted` y
  `.overrides_changed`, todas con `resourceType` `price_list`, y `catalog.channel_prices.updated`.

## Contrato HTTP

Todas bajo `/api/v1/tenants/{tenantId:guid}/catalog`, con política `catalog.price_list.manage`. El handler revalida tenant y
permiso (`CatalogAuthorization`), y otro tenant responde 403.

| Ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /price-lists` | — | `{ items: PriceListSummary[] }` por nombre, sin paginar (como hoy en mayorista) |
| `POST /price-lists` | `PriceListWrite` | 201 `PriceListDetail` |
| `GET /price-lists/{id}` | — | `PriceListDetail` |
| `PUT /price-lists/{id}` | `PriceListWrite` (`If-Match`) | `PriceListDetail` |
| `DELETE /price-lists/{id}` | — (`If-Match`) | 204; 409 `catalog.price_list.in_use` si un canal o un distribuidor la usa |
| `POST /price-lists/preview` | `{ adjustmentPercent, visibility, categoryIds, exclusions }` | Lo que vería un distribuidor (reemplaza el preview mayorista) |
| `GET /price-lists/{id}/overrides?search=&page=&pageSize=` | — | Página de `{ variantId, productId, code, name, currency, basePrice, amount }`. `basePrice` es nullable y le ahorra a la pantalla otra consulta para mostrar «base → lista» |
| `PUT /price-lists/{id}/overrides` | `{ set: [{ variantId, currency, amount }], remove: [{ variantId, currency }] }` (`If-Match`, hasta 500 filas) | `{ version, rows }`: las filas escritas, en el orden recibido |
| `GET /channel-prices` | — | `{ channels: [{ channel, priceList: { id, name } \| null }] }`: siempre los tres canales y en orden fijo, incluso sin lista (precedente `ReportStatusSliceDto`) |
| `PUT /channel-prices` | `{ channels: [{ channel, priceListId \| null }] }` | Lo mismo que el `GET`. Un canal que no viene no cambia; sin `If-Match` (gana la última escritura, como el acceso rápido del POS) |

```
PriceListWrite   { name, adjustmentPercent, visibility: 'AllActive'|'Categories', categoryIds, exclusions, showPrices? }
PriceListSummary { id, name, adjustmentPercent, overrideCount, channels: PriceChannel[], distributorCount, version }
PriceListDetail  { id, name, adjustmentPercent, visibility, categories: [{ id, name }], exclusions, showPrices,
                   overrideCount, channels, distributorCount, version, createdAt, updatedAt }
```

`channels` y `distributorCount` van en el resumen para que la pantalla diga «usada en POS y en 3 distribuidores» sin cruzar
dos respuestas, y el DTO lo explica. `showPrices` ausente en el `PUT` conserva el valor (D-2 del 2026-11-19).

Errores: 404 `catalog.price_list.not_found`. 409 `catalog.price_list.name_taken`, por índice único de nombre sin mayúsculas, y
`catalog.price_list.in_use`. 422 `catalog.price_list.adjustment_out_of_range`, `catalog.price_list.category_not_found`,
`catalog.price_list.override_amount_invalid` (un monto ≤ 0 o con más de dos decimales), `catalog.price_list.variant_not_found`,
`catalog.price_list.unsupported_currency` y `validation.failed` con el mapa `errors`. En las rutas de mayorista, los
distribuidores mantienen `priceListId` en el cuerpo y en `DistributorOptions.priceLists` (ahora `{ id, name,
adjustmentPercent }`).

## Modelo de datos (Catalog)

| Tabla | Columnas |
| --- | --- |
| `catalog.price_lists` | `id`, `tenant_id`, `name` (120), `adjustment_percent numeric(7,2)`, `visibility`, `category_ids uuid[]`, `excluded_category_ids uuid[]`, `excluded_product_ids uuid[]`, `show_prices`, `version`, `created_at`, `updated_at`. Índice único `(tenant_id, lower(name))` |
| `catalog.price_list_overrides` | `price_list_id` (FK `CASCADE`), `tenant_id`, `variant_id` (FK a `product_variants`, `CASCADE`), `currency char(3)`, `amount numeric(14,2)`. PK `(price_list_id, variant_id, currency)` |
| `catalog.channel_price_lists` | `tenant_id`, `channel` (texto), `price_list_id` (FK `RESTRICT`). PK `(tenant_id, channel)` |

Todo método de repositorio recibe `tenantId`. La traducción de los índices únicos (`name_taken`) va en Infrastructure, por
nombre de índice.

## Despliegue

Aditivo: Catalog agrega tres tablas (`AddPriceLists`) y Wholesale sólo suelta una FK y un índice (`MovePriceListsToCatalog`). Un
pod viejo durante el rollout sigue usando `wholesale.price_lists`, que no se borra; un distribuidor que alguien invite desde un
pod viejo con una lista vieja queda sin lista en el código nuevo (D-8). No hay clientes usando las listas mayoristas en
producción (O-5), así que se acepta. Pendiente: la migración de contrato que borra `wholesale.price_lists`.

## Fuera de alcance (pendiente de decisión)

- **P-1. Agente de IA y copiloto** (`AiAgentSources`, `CopilotDirectoryTools`): ¿con qué canal responden precios? Propuesta:
  el copiloto con `Quotations` y el agente que habla con clientes con `Store`. Mientras no se decida, siguen con el precio base
  (owner, 2026-11-22: fuera de esta entrega).
- **P-2. Ver y editar los precios de canal desde la ficha del producto.** En esta entrega se editan desde la lista.
- **P-3. Reporte de cambios de precio** (`PriceChangeReportSource`): sigue cubriendo sólo el precio base y las escalas.
- **P-4. Vigencias o temporadas** de una lista (descartado en O-4 para esta entrega).
- Export de productos a Excel: sigue con el precio base.

## Pruebas (TDD)

- Dominio: `PriceList.PriceFor` con precio fijo, solo base, sin nada, redondeo COP y USD, ajuste negativo y positivo, y precio
  fijo sin base en la moneda.
- Integración por canal: con una lista asignada, el POS, el carrito de la tienda y una cotización (detal y con escala) dan el
  precio de la lista; sin asignación dan el base; un pedido `Shopify` no cambia.
- Mayorista: el catálogo del distribuidor usa el ajuste y los precios fijos de su lista; un distribuidor sin lista ve el
  catálogo cerrado; borrar una lista en uso por un distribuidor o un canal responde 409.
- Permisos: sin `catalog.price_list.manage` responde 403 y no 500 (la política está registrada). Otro tenant responde 403.
- Auditoría: cada escritura deja su fila en el outbox en la misma transacción.
- ArchitectureTests: Catalog no referencia Wholesale ni al revés.
