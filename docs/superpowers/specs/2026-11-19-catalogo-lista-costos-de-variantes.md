# Costo de las variantes en el listado con `includeVariants` — diseño backend

Fecha: 2026-11-19. Repo: `qep-backend`. Contraparte: el catálogo con carrito de la orden de compra en `qep-frontend`
(`features/purchases`). Reglas del costo: [`2026-11-10-costo-de-producto-design.md`](2026-11-10-costo-de-producto-design.md).

## 1. Por qué

El formulario de la orden de compra es un catálogo: cada tarjeta muestra el costo del producto y, al agregarlo al carrito,
la línea arranca con ese costo. El catálogo se arma con `GET /api/v1/tenants/{tenantId}/catalog/products?includeVariants=true`
y hoy ese listado **nunca** trae costo. Sin él la pantalla tendría que pedir el detalle de cada producto, y la regla BFF lo
prohíbe: el contrato lo manda la pantalla.

## 2. Contrato

`ProductSellableVariantDto` (la variante de `ProductListItemDto.Variants`) gana un último campo `costs`, con **la misma
semántica que `ProductVariantResponse.Costs` del detalle** (spec de costo §6.1):

- `null` = quien llama no tiene `catalog.product_cost.read` ni `catalog.product_cost.manage` (`CatalogCostAccess.CanRead`).
- `{}` = la variante no tiene costo.
- `{ "COP": 12000, "USD": 3 }` = costo unitario **sin IVA** por moneda, en orden de catálogo, sin conversión.

```json
{
  "id": "0193…",
  "name": "Camiseta – M",
  "code": "CAM-M",
  "barcode": null,
  "prices": { "COP": 80000 },
  "priceScales": [],
  "packagingUnits": [],
  "imageUrl": null,
  "costs": { "COP": 12000 }
}
```

Sin `includeVariants` el listado no cambia: no hay `variants`, así que tampoco hay `costs`.

## 3. Cómo

- El permiso se decide igual que en el detalle (`CatalogCostAccess.CanRead`) y viaja en `ProductReadLookups.IncludeCosts`.
  El mapeo (`ToSellableDto`) usa el mismo orden de catálogo que el detalle.
- Los costos se leen dentro de `IProductRepository.ListActiveVariantsAsync`, en **una** consulta plana sobre
  `catalog.product_variant_costs` para todas las variantes de la página (como ya se leen precios y escalas), y **sólo** si
  quien llama puede verlos: sin permiso no se consulta la tabla.

## 4. Decisión que cambia el spec de costo

El spec de costo §6.1 dice «Las búsquedas del POS, cotizaciones y pedidos **no** exponen costo». Este listado con
`includeVariants=true` es también el selector de cotizaciones y pedidos, así que **con el permiso** el selector ahora recibe
`costs` (lo ignora). La regla que se conserva es la del §2 de ese spec: **sin permiso, el costo no viaja**. La búsqueda del
POS (`/pos/products`) no cambia. La herramienta del copiloto `search_products` usa el listado sin variantes y no cambia.

## 5. Pruebas

Integración (Testcontainers), clase nueva `ProductListVariantCostsApiTests`:

1. Con `catalog.product_cost.read`, cada variante trae sus costos por moneda en orden de catálogo, y `{}` la que no tiene.
2. Sin el permiso, `costs` es `null`.
3. Sin `includeVariants`, el listado no trae `costs` (ajuste de `ProductVariantCostsApiTests`, que afirmaba que el selector
   nunca los traía).
4. Una sola consulta a `product_variant_costs` por request, sea la página de 1 o de 3 productos, y ninguna sin el permiso
   (interceptor de comandos de EF sobre `CatalogDbContext`, como `RequestQueryBudgetTests` de Tenancy).

Unitarias: `ToSellableDto` deja `costs` en `null` sin `IncludeCosts`, `{}` sin costos y el mapa en orden de catálogo con él.
