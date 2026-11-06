# Tienda pública: `variantId` en cada tarjeta del listado

**Fecha:** 2026-11-05
**Módulos:** Store (`ListPublicProducts`, DTOs públicos) y Bootstrapper (`StoreCatalogReader.SearchAsync`). **Sin migración, sin
frontend del backoffice.**
**Origen:** `qep-storefront/docs/superpowers/specs/2026-11-05-storefront-redesign-design.md` §4 y §6 (rediseño de la tienda, ya mergeado
en el storefront).
**Amplía** el contrato público del spec `2026-11-03-tienda-publica-design.md` §6 (`PublicProductPage`).

## 1. Intención

La grilla de la tienda quiere un botón «Agregar al carrito» en la tarjeta de un producto que no obliga a elegir nada. Para agregar, el
carrito necesita el **id de la variante**, y el listado (`GET /api/v1/public/stores/{host}/products`) hoy sólo trae `hasVariants`. Sin ese id, el
storefront tendría que pedir la ficha en cada clic: una espera visible por clic y una ruta más para compensar un dato que el backend ya
tiene en la mano al armar la tarjeta (spec del storefront §6, alternativa descartada).

## 2. Contrato

Cada ítem de `items` suma **`variantId: uuid | null`**:

- `hasVariants === false` → el id de **la única variante vendible** del producto en la moneda pedida.
- `hasVariants === true` → `null`.

El campo **siempre viaja**, también en `null` (no se omite): es lo mismo que hace el resto del DTO y lo que el storefront ya acepta
(`variantId: id.nullish().catch(null)` en `PublicProductPageSchema`).

## 3. Qué es «la única variante» (lo que dice el código)

`hasVariants` ya no significa «el producto tiene opciones»: es `prices.Count > 1` sobre las variantes **vendibles en la moneda**
(`StoreCatalogReader.SearchAsync`; vendible = producto activo y visible, variante activa, con precio en la moneda — `IsSellableIn`). La
tarjeta sólo existe con al menos una vendible. Entonces `hasVariants === false` equivale a **exactamente una** variante vendible, y esa es
la que se devuelve. No hay ambigüedad:

- Producto sin opciones: su variante por defecto (que lleva el id del producto, gotcha «La variante por defecto lleva el id del producto»).
- Producto con opciones pero una sola variante vendible en la moneda (las otras inactivas o sin precio en esa moneda): esa variante. Es la
  misma que la ficha mostraría como única y la que `SellableVariantsAsync` acepta al cotizar el carrito, así que el botón de la grilla no
  puede agregar algo que el carrito después rechace por no vendible.

## 4. Diseño

- `StoreProductCard` (Application) suma `Guid? VariantId`; `SearchAsync` lo llena con la vendible cuando hay una sola.
- `PublicProductCardDto` suma `Guid? VariantId`, con la nota del porqué en el DTO (regla del backend: «el motivo se escribe en el DTO»).
- `ListPublicProductsHandler` lo copia. Ninguna consulta nueva: las variantes ya vienen cargadas para calcular los precios.
- El listado sale con `Cache-Control: public, max-age=60, stale-while-revalidate=300` y la caché del storefront toma ese TTL: el campo
  nuevo aparece a los pocos minutos de desplegar. No hace falta invalidar nada.

## 5. Pruebas (TDD, integración del módulo Store, `PublicCatalogApiTests`)

1. Producto sin variantes → `variantId` es el id de su variante (el que devolvió la creación).
2. Producto con dos variantes vendibles → `variantId` presente y `null`.
3. Producto con dos variantes y una desactivada → `hasVariants` falso y `variantId` es la que sigue vendible.
4. Forma del contrato: la clave se llama `variantId` (camelCase) y está en cada ítem.

## 5b. Orden de despliegue

Independiente: el storefront ya trata el campo como opcional y muestra «Ver producto» mientras no llegue.

## 6. Decisiones pendientes

Ninguna.
