# Servicios en cotizaciones: pendientes del frontend (backend)

Fecha: 2026-11-18. Sigue al spec `2026-11-18-servicios-en-cotizaciones-design.md` y a su plan, ya implementados y
mergeados. Son dos cambios **aditivos** que pide la pantalla (backend for frontend): ningún campo se quita ni cambia de
significado, y un frontend viejo no se entera.

## 1. El export de productos filtra por tipo

`POST /api/v1/tenants/{tenantId}/catalog/products/export` acepta el query opcional `kind` (`Physical` | `Service`), igual
que `GET …/catalog/products`.

- Se parsea con `CatalogEnumParsing.ParseOptional<ProductKind>(kind, "kind")`, el mismo del listado: un valor que no es
  del enum responde `422 validation.failed` con el mapa `errors` (`kind`).
- Ausente, no filtra: trae productos y servicios, como hasta hoy.
- Por qué: la grilla del catálogo ya filtra por tipo, y el export es «lo que se está viendo» (`ExportProductsCommand`).
  Sin el filtro, el Excel traería servicios con la grilla mostrando sólo productos.
- Va por `ExportProductsCommand.Kind` → `IProductRepository.ListForExportAsync(kind)`, el mismo `WHERE` que
  `SearchSummariesAsync`.

## 2. Las tasas de retención guardadas traen su alcance

Cada tasa guardada viaja con `appliesTo`: el nombre de `WithholdingScope` (`All` | `Goods` | `Services`).

| Respuesta | Campo |
| --- | --- |
| Ficha de la empresa facturadora (`CompanyDto.withholdingTaxRates[]`) | `appliesTo` |
| Ficha del cliente (`CustomerDto.withholdingTaxRates[]` y demás DTOs que la llevan) | `appliesTo` |
| Cotización y pedido (`customerWithholdingTaxRates[]`, `billingWithholdingTaxRates[]`) | `appliesTo` |

- Por qué: con dos tasas del mismo tipo y nombre parecido, una de bienes y otra de servicios, la pantalla tiene que
  decir «Retefuente (servicios)». Hoy sólo puede cruzar el id con el catálogo activo, y una tasa que ya no está ahí
  (desactivada después de guardarse) se queda sin etiqueta.
- Empresa y cliente lo leen del catálogo por sus puertos (`CompanyTaxRateRef.AppliesTo`, `CustomerTaxRateRef.AppliesTo`,
  que ya existían).
- Cotización y pedido lo toman de la **foto congelada** del documento (`DocumentWithholding.AppliesTo`), no del
  catálogo: el documento dice el alcance con el que se congeló la tasa.
- El campo va al final de cada record con default `"All"`, así que las construcciones existentes no cambian.

## Fuera de alcance

- Sin migraciones: el alcance ya está en `catalog.tax_rates.applies_to` y en la foto del documento.
- El listado de empresas sigue sin tasas (§9.2 de ReteICA por empresa).
