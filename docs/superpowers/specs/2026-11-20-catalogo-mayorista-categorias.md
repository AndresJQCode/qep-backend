# Categorías en el catálogo del mayorista (2026-11-20)

## Problema

La orden de compra a un proveedor enlazado se arma desde `GET /api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog`.
El comprador no puede acotar por categoría: el contrato no las expone y el filtro `categoryId` del catálogo propio no aplica
aquí. El mayorista ya tiene las categorías (`WholesaleCatalogReader.ListCategoriesAsync`) y ya las usa para decidir qué ve cada
distribuidor (`WholesaleTerms.VisibleCategoryIds`, con sus descendientes), pero nada de eso llega al comprador.

## Contrato (aditivo, compatible hacia atrás)

### 1. `GET …/purchases/suppliers/{supplierId}/catalog/categories`

Mismo permiso (`purchases.order.manage`) y mismas reglas de proveedor que el catálogo: 404 `suppliers.supplier.not_found`,
422 `purchases.order.supplier_not_linked`, 403 `wholesale.relationship.inactive`.

Respuesta: `{ items: [{ id, name, parentId }] }`, el árbol **que ese distribuidor puede ver**, plano y con `parentId`.

- Lista con `AllActive` (`VisibleCategoryIds == null`): todas las categorías del mayorista.
- Lista con `Categories`: las de la lista **más sus descendientes**, nada más. Un ancestro de una categoría de la lista que no
  esté en ella **no** se devuelve, y la categoría de la lista cuyo padre queda fuera sale con `parentId = null`.
- Nunca viaja una categoría que `WholesaleOffers.PriceVisibleAsync` rechazaría con `wholesale.product.not_visible`.
- El orden lo da el backend (por nombre dentro de cada padre): el frontend no ordena.

### 2. `categoryId` (query, opcional) en el catálogo

El filtro incluye los descendientes de la categoría elegida (como `ExpandAsync` del lector). Se **intersecta** con la
visibilidad: nunca amplía lo que la lista permite.

- Categoría fuera del árbol visible (ajena, borrada o no permitida): **422 `wholesale.category.not_visible`**, sin decir cuál
  de las tres es. Decisión pendiente del owner: la alternativa es una página vacía; se prefiere el 422 porque un id que el
  cliente no pudo sacar de `…/categories` es un bug o un manipuleo, no un caso normal.
- `Total` sigue contando productos y respeta el filtro.

### 3. Sin cambios

`SupplierCatalogItemDto` no suma campos. La orden y sus validaciones (`PriceVisibleAsync`) no cambian.

## Implementación

- `WholesaleOffers.ListVisibleCategoriesAsync(wholesalerTenantId, buyerTenantId)`: `ResolveTermsAsync` (misma comprobación de
  relación), `catalog.ListCategoriesAsync`, recorte con `ProductCategoryTree.WithDescendants` sobre `VisibleCategoryIds`.
- `WholesaleOffers.SearchAsync` recibe `categoryId`: valida contra ese árbol y pasa `[categoryId]` como `categoryIds` a
  `SearchSellableAsync` (el lector ya expande descendientes). Con el filtro, la visibilidad de la lista queda garantizada por la
  validación previa, no por el valor que se pasa al lector.
- Puertos: `IWholesaleCatalog.SearchAsync` suma `Guid? categoryId`; nuevo `ListCategoriesAsync(buyer, wholesaler)`;
  `PurchasesWholesaleCatalog` los adapta. Handler `ListSupplierCatalogCategories` en Purchases, endpoint junto al del catálogo.

## Pruebas

- Lista `AllActive`: devuelve todo el árbol; filtrar por una raíz trae sus productos y los de sus hijos.
- Lista `Categories` con un hijo suelto: el hijo sale con `parentId = null` y su ancestro no aparece.
- `categoryId` fuera de lo visible: 422 `wholesale.category.not_visible`; no se filtra por un tenant ajeno.
- Relación no activa: 403 `wholesale.relationship.inactive` en las dos rutas.
- Sin `categoryId` el catálogo responde igual que antes (compatibilidad).

## Pendiente del owner

- 422 vs. página vacía para una categoría no visible (arriba).
- Contador de productos por categoría (`productCount`): **no** se incluye. Si se quiere, es una consulta agregada por
  categoría, y el frontend nunca lo suma.
- Despliegue: backend primero; un frontend nuevo contra un backend viejo recibe 404 en `…/categories` y deja sin filtro.
