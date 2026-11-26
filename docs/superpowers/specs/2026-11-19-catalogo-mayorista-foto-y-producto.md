# Catálogo del mayorista con foto y producto (2026-11-19)

## Problema

`GET /api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog` devuelve una lista plana de variantes
(`SupplierCatalogItemDto`). El frontend necesita dibujar tarjetas por producto con foto y agrupar las variantes por producto.

## Contrato (aditivo, compatible hacia atrás)

Cada `SupplierCatalogItemDto` suma tres miembros al final, con valor por defecto para que los llamadores existentes compilen:

| Miembro       | Tipo      | Regla                                                                                     |
| ------------- | --------- | ----------------------------------------------------------------------------------------- |
| `ProductId`   | `Guid`    | El producto del mayorista al que pertenece la variante.                                   |
| `ProductName` | `string`  | Nombre del producto (`Name` sigue siendo el nombre visible de la variante).               |
| `ImageUrl`    | `string?` | Foto propia de la variante (galería); si no tiene, la principal del producto; si no, null. |

Los ítems siguen planos por variante, `Total` sigue contando productos, y el orden sigue siendo por nombre de producto con las
variantes de un producto contiguas.

## Resolución de la foto

Misma regla que el `imageUrl` de las variantes vendibles de `GET /catalog/products?includeVariants=true`:
`CatalogVariantReads.ImageFileIdOf` (una foto propia que no está disponible no cae a la principal) y
`CatalogVariantReads.ImageUrlOf`. Se pide a `IProductImageLookup.FindManyAsync` **una sola vez por página**, y la URL sólo sale
si el archivo es del tenant **del mayorista**, está disponible y tiene URL pública.

## Visibilidad y seguridad entre tenants

- No cambia la visibilidad: sólo se decora lo que `WholesaleOffers` ya devolvía (relación `Active`, categorías de la lista).
- `ProductId`, `ProductName` e `ImageUrl` viajan por la cadena `WholesaleCatalogReader` → `WholesaleSellableVariant` →
  `WholesaleOffer` → `WholesaleCatalogItem` → `SupplierCatalogItemDto` como miembros finales opcionales.
- El comprador nunca recibe la URL de un archivo que no sea la foto visible del producto del mayorista: el re-chequeo de tenant
  (`image.TenantId == wholesalerTenantId`) va con el id del mayorista, no con el del comprador.
