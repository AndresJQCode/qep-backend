# Catálogo: el listado de productos filtra por categoría

Fecha: 2026-11-19. Cambio **aditivo** que pide la pantalla (backend for frontend): el catálogo de la orden de compra del
frontend muestra chips de categoría, y filtrar una página en el navegador rompería el total y la paginación. Ningún
campo de la respuesta cambia.

## Contrato

`GET /api/v1/tenants/{tenantId}/catalog/products` acepta el query opcional `categoryId` (Guid).

- Ausente, no filtra: el listado queda como hasta hoy, con y sin categoría.
- Presente, trae sólo los productos de esa categoría **y de todas sus descendientes**. Es la misma semántica de
  `GET /pos/products?categoryId=` (spec 2026-10-26 §3.4). Un producto sin categoría no entra.
- `total`, `page` y `pageSize` cuentan los productos filtrados: el total es el del filtro, no el del catálogo.
- Se combina con AND con los demás filtros (`search`, `name`, `code`, `isActive`, `visibleInStore`, `kind`) y con
  `includeVariants`, que sigue trayendo las variantes vendibles de los productos de la página.
- Un id que no existe o que es de otro tenant responde `200` con la página vacía (`items: []`, `total: 0`): nunca
  `404`, nunca `500`, y nunca datos de otro tenant.

## Implementación

- `ListProductsQuery.CategoryId` (`Guid?`, al final con default `null`: las construcciones existentes no cambian).
- `ListProductsHandler` expande el id con `IProductCategoryRepository.ListIdsWithDescendantsAsync`, que ya filtra por
  tenant; si la expansión viene vacía corta con la página vacía sin consultar productos (igual que `PosProductLookup`).
- Los ids expandidos van como `categoryIds` a `IProductRepository.SearchSummariesAsync`, que ya implementa el filtro en
  SQL; el conteo y el orden salen de la misma consulta.

## Fuera de alcance

- El export de productos (`POST …/products/export`) no filtra por categoría.
- El conteo por categoría de `GET …/catalog/categories` no cambia.
