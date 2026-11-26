# Ocultar categorías y productos por lista de precios (2026-11-20)

Backend primero. Frontend: `qep-frontend/docs/superpowers/specs/2026-11-20-listas-ocultar-categorias-productos-design.md`.
Maqueta: https://claude.ai/artifact/CoUT2g9F9vqNLpuJWRMLq5 (tablero A).

## Para qué

El mayorista decide, **por lista de precios**, qué no ve cada grupo de distribuidores. Hoy una lista sólo puede **incluir**
categorías (`Visibility = AllActive | Categories`): para esconder una categoría hay que listar todas las demás, y un producto
suelto no se puede esconder de ninguna forma.

Objetivo: una lista guarda además una lista de **exclusiones** (categorías y productos). Lo excluido no aparece en el catálogo
del distribuidor, ni en su árbol de categorías, ni se puede pedir.

No es objetivo: mostrar productos sueltos fuera de las categorías incluidas, ocultar variantes sueltas, una bandera global
«no vender por mayorista» (decisión del owner: lo decide cada lista), ni tocar órdenes ya creadas.

## Reglas (el contrato de comportamiento)

Visible = **en el alcance** de la lista **y no excluido**; lo excluido gana siempre.

1. **Alcance** como hoy: `AllActive` = todo el catálogo activo; `Categories` = esas categorías y sus descendientes.
2. **Excluir una categoría** oculta los productos de esa categoría y de todas sus descendientes. Excluir un hijo no toca al padre.
3. **Excluir un producto** lo oculta entero, con todas sus variantes.
4. Un producto sin categoría no lo afecta ninguna categoría excluida; sólo se oculta excluyéndolo a él.
5. No hay excepción dentro de una categoría excluida (no existe «mostrar este producto suelto»).
6. Excluir algo que ya queda fuera del alcance o dentro de otra exclusión **no es un error** (queda redundante). Las dos listas
   pueden dejar a la lista sin ningún producto visible: se permite, la vista previa lo dice.

## Contrato HTTP (aditivo)

Todo con `wholesale.price_list.manage`, igual que las listas. Un cliente que no manda `exclusions` guarda la lista sin exclusiones.

### Listas

- `POST` y `PUT …/wholesale/price-lists` y `POST …/price-lists/preview`: el cuerpo suma
  `exclusions: [{ "kind": "Category" | "Product", "id": "<guid>" }] | null` (nulo o ausente = ninguna).
- `GET …/price-lists/{id}` (detalle): suma `exclusions: [{ kind, id, name, code?, categoryName? }]`. `name` es el de la categoría o
  del producto; `code` y `categoryName` sólo para `Product`. Una exclusión cuyo destino ya no existe **no viaja** (como las
  categorías, D-23): el siguiente `PUT` guarda el conjunto limpio. Orden: categorías primero, luego productos, cada grupo por nombre.
  **Campo ausente = backend viejo** (el frontend lo lee así).
- `GET …/price-lists` (resumen): suma `hiddenCategoryCount` y `hiddenProductCount`.
- Validación (422, con `errors.Exclusions[i]`): `kind` válido; `id` no vacío y sin repetir `(kind, id)`; la categoría o el producto
  **existe en el tenant** (como D-9 para `CategoryIds`); topes `MaxHiddenCategories = 200` y `MaxHiddenProducts = 1000`
  (decisión mía, del orden de magnitud de `MaxCategories`; el builder puede ajustarlos con una línea).
- `If-Match` y `Version` como hoy; cambiar exclusiones sube la versión.

### Selector de productos (nuevo)

`GET …/wholesale/products?search=&categoryId=&page=&pageSize=` con `wholesale.price_list.manage` (el que administra listas no
necesita `catalog.product.read`, igual que `GET …/wholesale/categories`, ruling W5). Devuelve
`{ items: [{ id, code, name, categoryId, categoryName, variantCount, imageUrl }], total, page, pageSize }`: **productos activos
con precio en la moneda por defecto** (los mismos que la lista puede mostrar, así el selector no ofrece uno inservible), por
nombre, `total` cuenta productos. `categoryId` filtra con descendientes. Tope de página 50.

## Dónde se aplica (el comprador)

`WholesaleTerms` suma `HiddenCategoryIds` y `HiddenProductIds` (vacías cuando no hay).

- `ListVisibleCategoriesAsync` (árbol del comprador, spec 2026-11-20 de categorías): quita las categorías excluidas **y sus
  descendientes**; el resto de la normalización (huérfanas a raíz) sigue.
- `SearchAsync` (catálogo): un `categoryId` que cae en el árbol recortado ya se valida (422 `wholesale.category.not_visible`);
  el lector filtra además en SQL los productos excluidos y los de categorías excluidas, así `Total` y la paginación son verdad.
- `PriceVisibleAsync` (armar y enviar la orden): una variante cuyo producto está excluido, o cuyo `CategoryPath` toca una
  categoría excluida, es 422 `wholesale.product.not_visible` (mismo código de siempre).
- `PreviewPriceList` (`SampleAsync`): el lector recibe las exclusiones del cuerpo, así la vista previa refleja lo no guardado.
- **No cambia:** `OffersForOrderAsync` / `ListPricesForOrderAsync` (el mayorista edita su propio pedido, D-5) ni las líneas ya
  guardadas. Una orden de compra en borrador con un producto que luego se oculta falla **al enviar** con el 422 de arriba, como hoy
  con uno desactivado; el distribuidor la edita.

## Puertos

`IWholesaleCatalogReader`: `SearchSellableAsync` y `SampleAsync` reciben `excludedCategoryIds` y `excludedProductIds` (el lector
expande descendientes de las categorías, como hace con `categoryIds`); nuevo `SearchProductsAsync(tenantId, currency, categoryId,
search, page, pageSize)` para el selector; `FindSellableAsync` no cambia (ya trae `ProductId` y `CategoryPath`).
`ExistsAsync`-style comprobación de existencia de categorías ya existe (`FindCategoriesAsync`); para productos el lector suma
`FindProductsAsync(tenantId, ids)` (existentes del tenant, para validar y para armar los nombres del detalle).

## Persistencia

Aditivo, sin tocar datos: dos columnas `uuid[]` en `price_lists` (`excluded_category_ids`, `excluded_product_ids`, por defecto
vacías) como `category_ids`; el dominio y el contrato exponen **una** lista de `(Kind, Id)`. Migración de Wholesale. Auditoría de
`Update` incluye `exclusions` en `changedFields`.

## Pruebas

Dominio: `Create`/`Update` guardan, deduplican y topan; `AllActive` con exclusiones las conserva. `WholesaleOffers`:
categoría excluida quita a sus descendientes del árbol y del catálogo; producto excluido sale de `SearchAsync` con `Total` bien;
`PriceVisibleAsync` rechaza los dos casos con `product.not_visible`; sin exclusiones, igual que antes; excluir un hijo no toca al
padre; producto sin categoría sólo cae por su id; `categoryId` excluido es 422 `category.not_visible`. Validador: id inexistente,
repetido, vacío, sobre el tope. Detalle: nombres y `code`; destino borrado no viaja. Selector: filtro, búsqueda y total.
Integración filtrada por nombre: un mayorista con lista `Categories` y exclusiones; el distribuidor ve árbol y catálogo recortados
y el 422 al enviar una orden con un producto oculto. Unitarias por tarea; integración una vez al final del bloque.

## Despliegue

Backend primero. Un frontend nuevo contra un backend viejo no manda ni recibe `exclusions`: el detalle sin el campo se lee como
backend viejo y el frontend **no ofrece** la sección (un cuerpo con `exclusions` contra un backend viejo se ignoraría en silencio).

## Decisiones mías para revisar

Topes 200/1000; el selector sólo ofrece productos con precio en la moneda por defecto; una exclusión a un destino borrado se
descarta en silencio al leer; 422 y no página vacía para una categoría excluida en el catálogo (consistente con el spec de
categorías, que sigue pendiente del owner).
