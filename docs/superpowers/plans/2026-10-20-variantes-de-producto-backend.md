# Variantes de producto — plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL REQUERIDA: usa superpowers:subagent-driven-development (recomendado) o superpowers:executing-plans para ejecutar este plan tarea por tarea. Los pasos usan casillas `- [ ]`.

**Meta:** Que un producto tenga opciones y variantes (código, código de barras, precios, escalas, empaques e imagen propios), que se pueda vender una variante en cotización, pedido y POS, y que los productos existentes migren a «producto con una variante» sin cambiar ninguna línea histórica.

**Arquitectura:** `Product` sigue siendo la raíz del agregado y gana tres colecciones: referencias a opciones de la biblioteca (≤3), variantes (≤2.048) y galería (≤250). `Code`, precios, escalas y empaques pasan a la variante. La variante por defecto de cada producto migrado **lleva el mismo id que el producto**, así `variant_id` se rellena en Quotations y Pos sin consultas entre esquemas. Los consumidores (Quotations, Pos, Shopify, Reporting) cambian de `productId` a `variantId` por sus puertos; los adaptadores viven en `src/Bootstrapper`.

**Tecnología:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, CQ propio del repo.

**Spec:** [`docs/superpowers/specs/2026-10-20-variantes-de-producto-design.md`](../specs/2026-10-20-variantes-de-producto-design.md) (aprobado por el owner el 2026-10-20; §9 con las decisiones 3, 5 y 6 corregidas). Léelo antes de empezar: este plan no repite sus razones.

**Alcance de este repo:** sólo backend. El frontend (`features/catalog`, `product-picker-dialog.tsx`, `add-order-items-dialog.tsx`, `features/pos`) sale **en la misma ventana de despliegue** (spec §6) y tiene su propio plan; la Tarea 15 entrega el contrato que ese plan consume.

## Restricciones globales

- TDD: RED con salida literal antes de GREEN, en cada tarea. Conventional commits en español, **sin atribución de IA** (revisa cada commit, también los de subagentes).
- Antes de cada commit: `git branch --show-current`. Trabajar en una rama `feature/variantes-producto` creada desde `develop`; nunca mergear a `main`.
- Pruebas acotadas al módulo tocado (`dotnet test <proyecto> --filter ...`); **no correr la suite completa**. `Api.exe` corriendo bloquea el build.
- Migraciones: `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- Tenant: toda consulta recibe `tenantId`; 403, nunca 404, entre tenants. Permisos: `catalog.product.read` y `catalog.product.manage`; **sin permisos nuevos**.
- Límites (spec): 3 opciones por producto, 2.048 variantes por producto, 250 fotos por producto, `Code` ≤ 60 y único por tenant entre variantes (índice **no parcial**), código de barras ≤ 64, opcional y único por tenant cuando existe.
- Nombre visible: variante por defecto = nombre del producto; con opciones, `«{producto} – {v1} / {v2} / {v3}»` (guion largo con espacios).
- La foto de una línea (`product_code`, `product_name`, `variant_id`) **nunca se recalcula**; los códigos de error `quotation.item.duplicate_product` y `order.item.duplicate_product` se conservan.
- Errores de base se traducen en Infrastructure **por nombre de índice**. Textos de usuario en español colombiano tuteando.
- Cada módulo toca sus propias tablas: Catalog no referencia Quotations ni Pos (`CatalogLayerTests`).

## Enfoque de revisión

Entradas que el spec implica y ninguna tarea cubriría por sí sola. Cada línea tiene su prueba en la tarea indicada.

1. **Producto migrado con ventas al que se le agregan opciones** conserva id, `Code`, precios e historial de la variante original (T5).
2. **Dos usuarios editan el mismo producto a la vez** (variantes y galería): gana la versión; el segundo recibe el 409 de concurrencia, no un producto a medias (T5).
3. **Una línea nueva aparece entre «¿está en uso?» y el borrado**: la variante se desactiva, no se borra ni deja `variant_id` huérfano (T6).
4. **Escáner con un código que es `Code` de una variante y código de barras de otra**: gana el código de barras (T12).
5. **Cantidad de la misma escala sin agrupación repartida en dos variantes** (6 M + 6 L): no suma (T10).

---

## Mapa de archivos

| Qué | Dónde |
| --- | --- |
| Biblioteca de opciones, variante, galería (dominio) | `src/Modules/Catalog/Modules.Catalog.Domain/` — nuevos `OptionDefinition.cs`, `OptionValue.cs`, `ProductVariant.cs`, `VariantId.cs`, `ProductGalleryImage.cs`, `VariantDisplayName.cs`; cambios en `Product.cs`, `ProductPrice.cs`, `PriceScale.cs`, `ProductPriceChange.cs`, `ProductPriceChangeDetector.cs` |
| Persistencia | `Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs`, `ProductRepository.cs`, nuevas migraciones, `CatalogUnitOfWork` (traducción de índices) |
| Casos de uso | `Modules.Catalog.Application/` — nuevos `OptionLibrary*.cs`, `IVariantUsageChecker.cs`, `ProductVariantChanges.cs`; cambios en `CreateProduct.cs`, `UpdateProduct.cs`, `GetProduct.cs`, `ListProducts.cs`, `CopyPriceScales.cs`, `ExportProducts.cs`, `CatalogDtos.cs`, `ProductMapping.cs` |
| API | `Modules.Catalog.Api/ProductEndpoints.cs`, nuevo `OptionEndpoints.cs` |
| Adaptadores | `src/Bootstrapper/`: `QuotationProductLookup.cs`, `QuotationProductPricingLookup.cs`, `PosProductLookup.cs`, `ShopifyProductLookup.cs`, `PriceChangeReportSource.cs`, `ShopifyOrderWriter.cs`, nuevo `CatalogVariantUsageChecker.cs` |
| Consumidores | Quotations (`QuotationItem`, `OrderItem`, handlers de ítems, `QuotationScaleGroupPricing`, `OrderPricingRecalculation`, PDF, Excel de pedidos), Pos (`PosSaleLine`, `CreatePosSale`, `IPosProductLookup`), Reporting (filtros y resumen de cambios de precio) |
| Siembras | `Bootstrapper/Seeding/ExportLoadSeeder.cs`, `Catalog.Infrastructure/Seed/CatalogSeeder.cs` + `catalog-products.json` |

---

### Tarea 1: Biblioteca de opciones (dominio)

**Files:**
- Create: `Modules.Catalog.Domain/OptionDefinition.cs`, `OptionValue.cs`, `OptionDefinitionId.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/OptionDefinitionTests.cs`

**Interfaces:**
- Produces: `OptionDefinition.Create(Guid tenantId, string name, IReadOnlyList<string> values, bool isActive, DateTimeOffset at)`, `Rename(string)`, `AddValue(string)`, `RenameValue(Guid valueId, string)`, `SetActive(bool)`, `SetValueActive(Guid, bool)`, `Values : IReadOnlyList<OptionValue>` (ordenados); consts `NameMaxLength = 60`, `ValueMaxLength = 60`. Errores `CatalogDomainException`: `catalog.option.name_required`, `catalog.option.value_duplicate`, `catalog.option.value_required`.

- [ ] **Paso 1: prueba roja.** `Create_RejectsDuplicateValueIgnoringCaseAndEdgeSpaces`: `["M", " m "]` → `catalog.option.value_duplicate`. `AddValue_AppendsAtEnd`. `Create_RejectsEmptyName`.
- [ ] **Paso 2: correr** `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter OptionDefinitionTests` → FALLA (tipo inexistente).
- [ ] **Paso 3: implementar** las tres clases con las firmas de arriba; comparación `Trim()` + `OrdinalIgnoreCase`.
- [ ] **Paso 4: correr** → PASA.
- [ ] **Paso 5: commit** `feat(catalog): biblioteca de opciones en el dominio`.

### Tarea 2: Variante, galería y reglas del agregado

**Files:**
- Create: `ProductVariant.cs`, `VariantId.cs`, `ProductGalleryImage.cs`, `VariantDisplayName.cs`
- Modify: `Product.cs`, `ProductPrice.cs`, `PriceScale.cs` (su FK pasa de producto a variante), `ProductDetails.cs`, `ProductPricing.cs`
- Test: `ProductVariantTests.cs`, `VariantDisplayNameTests.cs`, ajustar `ProductTests.cs`, `PriceScaleCopyTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct VariantId(Guid Value)`.
  - `ProductVariant`: `Id`, `Code`, `Barcode?`, `OptionValueIds : IReadOnlyList<Guid>` (uno por opción del producto, en orden), `Prices`, `PriceScales`, `PackagingUnits`, `GalleryImageId?`, `IsActive`, `Position`. Reglas y códigos de error de hoy se mueven tal cual (`catalog.product.price_required`, `price_scale.*`, `packaging_units.*`).
  - `Product.Create(..., ProductVariantInput defaultVariant, ...)` crea la variante por defecto con `Id == product.Id.Value`. `Product.Update(ProductDetails, IReadOnlyList<Guid> optionIds, IReadOnlyList<ProductVariantInput> variants, IReadOnlyList<Guid> galleryFileIds, ...)` reemplaza el recurso.
  - Consts: `Product.MaxOptions = 3`, `MaxVariants = 2048`, `MaxGalleryImages = 250`, `ProductVariant.BarcodeMaxLength = 64`.
  - `VariantDisplayName.For(string productName, IReadOnlyList<string> valueNames) : string` (§3.4).
  - Errores nuevos: `catalog.variant.combination_duplicate`, `catalog.variant.limit_exceeded`, `catalog.variant.missing_in_request` (una variante guardada que no viene en el cuerpo), `catalog.variant.value_count_mismatch`, `catalog.product.option_limit`, `catalog.product.gallery_limit`.

- [ ] **Paso 1: pruebas rojas** (nombres): `Create_BuildsDefaultVariantWithProductId`, `Update_RejectsTwoVariantsWithSameCombination`, `Update_RejectsMoreThan2048Variants`, `Update_RejectsMoreThanThreeOptions`, `Update_RejectsMoreThan250GalleryImages`, `Update_RejectsRequestMissingASavedVariant`, `DisplayName_DefaultVariantIsProductName`, `DisplayName_JoinsValuesWithSlash` (esperado `"Camiseta básica – M / Azul"`), `PriceScale_CountsPerVariant`.
- [ ] **Paso 2:** correr `--filter "ProductVariantTests|VariantDisplayNameTests|ProductTests"` → FALLA.
- [ ] **Paso 3:** implementar. Las pruebas de `ProductTests` que hoy afirman precios/escalas del producto pasan a afirmarlos sobre `product.Variants.Single()`.
- [ ] **Paso 4:** correr → PASA.
- [ ] **Paso 5: commit** `feat(catalog): variantes y galería en el agregado Product`.

### Tarea 3: Persistencia y migración de datos de Catalog

**Files:**
- Modify: `CatalogDbContext.cs`, `ProductRepository.cs`, `ICatalogUnitOfWork`/su implementación (traducción de índices)
- Create: migración `AddProductVariants` (+ sus pruebas `ProductVariantsMigrationTests.cs` junto a `ProductPricesMigrationTests.cs`)

**Interfaces:**
- Produces (tablas, esquema `catalog`): `option_definitions`, `option_values`, `product_options (product_id, option_definition_id, position)`, `product_variants`, `product_variant_option_values`, `product_gallery_images (id, product_id, file_id, position)`; `product_prices`, `product_price_scales` y `product_price_changes` cambian su dueño/FK a `variant_id`. Índices con nombre fijo: `IX_product_variants_tenant_code` (único, no parcial), `IX_product_variants_tenant_barcode` (único, parcial `WHERE barcode IS NOT NULL`), `IX_product_variants_product_combination`.
- `IProductRepository` suma `FindVariantsByIdsAsync(Guid tenantId, IReadOnlyCollection<VariantId>, ct)`, `FindVariantByCodeAsync(tenantId, code, ct)`, `FindVariantByBarcodeAsync(tenantId, barcode, ct)`; `FindByCodeAsync`/`FindExistingCodesAsync` pasan a mirar variantes.

- [ ] **Paso 1: prueba roja de migración** (`ProductVariantsMigrationTests`): migrar hasta la última anterior, insertar un producto con dos precios, dos escalas, empaques `[100,150]`, `image_file_id` y un `product_price_changes`; aplicar `AddProductVariants`; afirmar: una variante con `id = product.id`, mismo `code`, precios/escalas/empaques intactos, la imagen es la primera foto de la galería (`position = 0`) y `GalleryImageId` de la variante apunta a ella, el cambio de precio quedó en esa variante. Al final `MigrateAsync(null)` para dejar la base en la última versión.
- [ ] **Paso 2:** `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter ProductVariantsMigrationTests` → FALLA.
- [ ] **Paso 3:** mapear el modelo y generar la migración; escribir el `Up` con `INSERT … SELECT` (una variante por producto, mismo id) **antes** de borrar `code`, y las columnas viejas de `products`. Borrar de paso las columnas heredadas sin mapear solo si no complica el `Down`.
- [ ] **Paso 4:** pruebas de traducción de índices: `Create_WithTakenCodeReturns422CodeTaken` (`catalog.product.code_taken` sigue siendo el código, ahora por `IX_product_variants_tenant_code`) y `…BarcodeTaken` → `catalog.variant.barcode_taken`. Correr ambas rojas, luego implementar la traducción **por nombre de índice**.
- [ ] **Paso 5:** correr migración + traducciones → PASAN. Commit `feat(catalog): migración de productos a variantes`.

### Tarea 4: Biblioteca de opciones — casos de uso y API

**Files:**
- Create: `Application/OptionLibrary*.cs` (List/Create/Update/Deactivate/Delete), `OptionLibraryProvisioner.cs`, `Api/OptionEndpoints.cs`
- Modify: `QepServiceCollectionExtensions` solo si hace falta registrar políticas (no hay permisos nuevos)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/OptionLibraryApiTests.cs`, `…UnitTests/OptionLibraryValidatorTests.cs`

**Interfaces:**
- Rutas: `GET/POST /api/tenants/{tenantId:guid}/options`, `PUT/DELETE …/options/{optionId:guid}`; DELETE devuelve 409 `catalog.option.in_use` si alguna variante usa la opción o un valor (se desactiva en su lugar); una opción o valor sin uso sí se borra.
- `OptionLibraryProvisioner.EnsureAsync(Guid tenantId, ct)`: `INSERT … ON CONFLICT DO NOTHING` de «Talla» (XS, S, M, L, XL, XXL) y «Color» (Negro, Blanco, Gris, Azul, Rojo, Verde, Amarillo, Café, Beige, Rosado), **inactivas**; lo llama el listado en el primer acceso.
- Validador FluentValidation por texto libre (nombre, valores) con mapa `errors`.

- [ ] **Paso 1: pruebas rojas:** `FirstList_SeedsTallaAndColorInactive`, `SecondList_DoesNotDuplicateSeed`, `Create_RejectsDuplicateNameInTenant` (409/422 según el mapeo central), `Delete_UnusedOptionSucceeds`, `Delete_OptionUsedByVariantReturnsInUse`, `OtherTenantGets403`, `ReadWithoutManagePermissionCanList` (stub pide `X-Permissions`).
- [ ] **Paso 2:** correr `--filter OptionLibraryApiTests` → FALLA (404 de ruta).
- [ ] **Paso 3:** implementar handlers, validadores y rutas; revalidar tenant y permiso en el handler (patrón `CatalogAuthorization`).
- [ ] **Paso 4:** correr → PASA. Commit `feat(catalog): biblioteca de opciones por tenant`.

### Tarea 5: Producto con variantes — crear, ver, listar y editar

**Files:**
- Modify: `CreateProduct.cs`, `UpdateProduct.cs`, `GetProduct.cs`, `ListProducts.cs`, `CatalogDtos.cs`, `ProductMapping.cs`, `ProductPricingMapping.cs`, `ProductWriteRules.cs`, `ProductEndpoints.cs`
- Create: `ProductVariantChanges.cs` (algoritmo §3.3.1)
- Test: `ProductWriteApiTests.cs`, `ProductDetailsApiTests.cs`, `ProductPricingApiTests.cs` (ajustar) + nuevo `ProductVariantsApiTests.cs`, `…UnitTests/ProductVariantChangesTests.cs`

**Interfaces:**
- Produces: `ProductResponse` con `options : [{ id, name, values:[{id,name}] }]`, `variants : [{ id, name /*visible*/, code, barcode?, optionValueIds, prices, priceScales, packagingUnits, galleryImageId?, isActive, canDelete }]`, `gallery : [{ id, fileId, url }]`, y los campos del producto. Un producto sin opciones devuelve **una** variante. `ProductListItem` suma `variantCount` y `priceRange`. `UpdateProductRequest` lleva `Version`, `optionIds`, `variants[]` (con `id?`), `gallery[]` (fileIds ordenados).
- `ProductVariantChanges.Apply(Product, UpdateProductRequest) : void` implementa: agregar opciones a un producto sin opciones (la variante por defecto se convierte en la combinación elegida, id/código/precios/historial intactos; las demás combinaciones nacen copiadas de ella con `Code` obligatorio), agregar una opción más, quitar una opción (desactiva las que sobran, nunca deja dos activas con la misma combinación). Cada archivo de galería se valida con `ProductImageResolver` (referencia blanda: sin esta comprobación un archivo de otro tenant entra con 201).
- Mensajes del 422 con mapa `errors` por campo (`variants[3].code`).

- [ ] **Paso 1: pruebas rojas** (`ProductVariantChangesTests`): `AddingOptionToMigratedProductKeepsVariantIdCodePricesAndHistory` (**Enfoque de revisión 1**), `AddingSecondOptionKeepsExistingVariantIds`, `RemovingOptionDeactivatesDuplicatedCombinations`, `RemovingAllOptionsLeavesOneActiveDefaultVariant`. (`ProductVariantsApiTests`): `Create_WithoutOptionsReturnsSingleVariantLikeToday`, `Create_ThreeOptionsReturnsVisibleNamesInOrder`, `Put_StaleVersionReturns409Concurrency` (**Enfoque 2**), `Put_GalleryFileOfOtherTenantRejected`, `Put_MissingSavedVariantRejected`, `Get_ReturnsGalleryAndVariantImage`.
- [ ] **Paso 2:** correr `--filter "ProductVariantChangesTests|ProductVariantsApiTests"` → FALLA.
- [ ] **Paso 3:** implementar DTOs, handlers, validadores (texto libre → FluentValidation) y mapeo central; ajustar `ProductMapping`/`ProductPricingMapping`.
- [ ] **Paso 4:** barrer **las pruebas de integración de Catalog que arman cuerpos a mano** (`ProductWriteApiTests`, `ProductPricingApiTests`, `ProductDetailsApiTests`, `ProductImageApiTests`, `ProductActivationApiTests`): pasan a un cuerpo con una variante por defecto. Correr `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests` (módulo entero) → PASA.
- [ ] **Paso 5: commit** `feat(catalog): crear y editar productos con variantes`.

### Tarea 6: Borrar o desactivar variantes (puerto de uso)

**Files:**
- Create: `Application/IVariantUsageChecker.cs`, `src/Bootstrapper/CatalogVariantUsageChecker.cs`
- Modify: `UpdateProduct.cs` (rama de variantes ausentes), `DeactivateProduct.cs`/`ActivateProduct.cs` si cuentan variantes
- Test: `ProductVariantDeletionApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`

**Interfaces:**
- Produces: `IVariantUsageChecker.FindUsedAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct) : Task<IReadOnlySet<Guid>>` — ids con al menos una fila en `quotations.quotation_items`, `quotations.order_items` o `pos.sale_lines`. Solo lectura. El `PUT` marca `canDelete` por variante; una variante usada que el cuerpo omite se desactiva y una no usada se borra. Los valores de opción siguen la misma regla (T4).
- Consecuencia de capas: `CatalogLayerTests` no cambia (Catalog no referencia a los otros módulos); la prueba de arquitectura de Bootstrapper no se toca.

- [ ] **Paso 1: pruebas rojas:** `Checker_FindsVariantUsedByQuotationOrderAndSaleLine`, `Put_UnusedVariantAbsentFromBodyIsDeleted`, `Put_UsedVariantMarkedCanDeleteFalse`, `Put_LineAppearsBetweenCheckAndDelete_DeactivatesInsteadOfDeleting` (**Enfoque 3**: el doble del puerto devuelve vacío en la primera llamada y la variante en la segunda, dentro de la misma transacción).
- [ ] **Paso 2:** correr los dos filtros → FALLAN.
- [ ] **Paso 3:** implementar puerto, adaptador (`SELECT DISTINCT variant_id … WHERE tenant_id = @t AND variant_id = ANY(@ids)` sobre cada tabla) y registro en Bootstrapper; la repetición de la comprobación va dentro de la unidad de trabajo del `PUT`.
- [ ] **Paso 4:** correr → PASA. Commit `feat(catalog): borrar variantes sin uso y desactivar las usadas`.

### Tarea 7: Historial de precios y copiar escalas por variante

**Files:**
- Modify: `ProductPriceChange.cs`, `ProductPriceChangeDetector.cs`, `CopyPriceScales.cs`, `PriceScaleCopy.cs`, `CatalogDtos.cs`
- Test: `ProductPriceChangeDetectorTests.cs`, `PriceScaleCopyTests.cs`, `ProductPriceHistoryApiTests.cs`, `CopyPriceScalesApiTests.cs`

**Interfaces:**
- Produces: `ProductPriceChange.VariantId` (además de `ProductId`); el detector compara por variante. `POST /products/price-scales/copy` recibe `sourceVariantId` y `targetVariantIds` (tope 200, contados en variantes).

- [ ] **Paso 1: pruebas rojas:** `Detector_RecordsOneChangePerVariant`, `Detector_IgnoresUnchangedVariants`, `Copy_FromVariantToVariantsOfSeveralProducts`, `Copy_Rejects201Targets`.
- [ ] **Paso 2:** correr `--filter "ProductPriceChangeDetectorTests|PriceScaleCopyTests|ProductPriceHistoryApiTests|CopyPriceScalesApiTests"` → FALLA.
- [ ] **Paso 3:** implementar. **Paso 4:** correr → PASA. Commit `feat(catalog): historial de precios y copia de escalas por variante`.

### Tarea 8: Excel de productos por variante

**Files:** `Catalog.Infrastructure/Excel/ClosedXmlProductExportBuilder.cs`, `ExportProducts.cs`; test `ProductExportApiTests.cs` + prueba del constructor.

**Interfaces:** una fila por variante, columnas Código, Código de barras, Producto, Variante, y las de precio/escala de hoy.

- [ ] **Pasos:** prueba roja `Export_WritesOneRowPerVariantWithVisibleName` → correr (FALLA) → implementar → correr (PASA) → commit `feat(catalog): exportar productos por variante`.

### Tarea 9: Adaptadores de Catalog hacia Quotations, Pos y Shopify

**Files:** `src/Bootstrapper/QuotationProductLookup.cs`, `QuotationProductPricingLookup.cs`, `QuotationPriceScaleMapping.cs`, `PosProductLookup.cs`, `ShopifyProductLookup.cs`, `ProductImageLookup.cs`; los puertos `IQuotationProductLookup`, `IQuotationProductPricingLookup`, `IPosProductLookup`, `IShopifyProductLookup`; tests `PosProductLookupTests.cs`, `ShopifyAdaptersTests.cs` (+ nuevos para Quotations).

**Interfaces:**
- `QuotationProductRef` → `QuotationVariantRef(Guid VariantId, Guid ProductId, string Name /*visible*/, string Code, string? ImageUrl, IReadOnlyCollection<QuotationPriceScaleRef> Scales)`; `IQuotationProductLookup.FindManyAsync(tenantId, IReadOnlyCollection<Guid> variantIds, ct)` devuelve diccionario por **variantId**. Igual para el de precios (`FindVariantAsync`).
- `PosProductRef` suma `VariantId`, `ProductId`, `Barcode?`; `IPosProductLookup.FindByCodeAsync(tenantId, code, ct)` busca **primero por código de barras y luego por `Code`**.
- `IShopifyProductLookup` empareja por `Code` de variante (exacto, sensible a mayúsculas) y devuelve `VariantId`.
- La imagen de una variante es su foto de galería; sin ella, la principal del producto.

- [ ] **Paso 1: pruebas rojas:** `QuotationLookup_ReturnsVariantNameImageAndScales`, `QuotationLookup_FallsBackToProductMainImage`, `PosLookup_BarcodeBeatsCode` (**Enfoque 4**), `PosLookup_FindsByCodeWhenNoBarcodeMatches`, `ShopifyLookup_MatchesVariantCodeExactly`.
- [ ] **Paso 2:** correr `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "…Lookup…|ShopifyAdaptersTests"` → FALLA (compilación: firmas).
- [ ] **Paso 3:** cambiar puertos y adaptadores. Esta tarea deja Quotations/Pos sin compilar hasta T10–T12: **hazla en la misma rama y no la commitees sola**; commitea al terminar T12 o usa un commit de trabajo temporal.

### Tarea 10: Quotations — líneas por variante

**Files:**
- Modify (dominio): `QuotationItem.cs`, `OrderItem.cs`, `QuotationProductSnapshot.cs`, `Quotation.cs`, `Order.cs`, `OrderInputs.cs`
- Modify (aplicación): `AddQuotationItem.cs`, `UpdateQuotationItem.cs`, `RemoveQuotationItem.cs`, `BatchUpdateQuotationItems.cs`, `QuotationEdits*.cs`, `QuotationItemEdits.cs`, `QuotationItemEditHistory.cs`, `AddOrderItems.cs`, `CreateOrder.cs`, `SaveOrderEdits.cs`, `OrderEdits.cs`, `OrderItemEdits.cs`, `OrderDraftBuilder.cs`, `OrderMapping.cs`, `OrderResponseComposer.cs`, `QuotationResponseComposer.cs`, `QuotationMapping.cs`, `QuotationItemProductLabel.cs`, `OrderItemProductLabel.cs`, `QuotationProductPricingResolver.cs`, `QuotationScaleGroupPricing.cs`, `QuotationPricingRecalculation.cs`, `OrderPricingRecalculation.cs`, `OrdersDtos.cs`, `QuotationsDtos.cs`, `IExternalOrderWriter.cs`, `OrdersExportProcessor.cs`
- Modify (API/infra): `QuotationEndpoints.cs`, `OrderEndpoints.cs`, `QuotationsDbContext.cs`; migración `AddVariantToLines` (`variant_id` en `quotation_items` y `order_items`, relleno `variant_id = product_id`, luego `NOT NULL`)
- Tests: los listados bajo `tests/Modules/Quotations/` que contienen `ProductId` (ver `grep -rl ProductId tests/Modules/Quotations`) más `QuotationVariantLinesApiTests.cs`, `VariantLinesMigrationTests.cs`

**Interfaces:**
- Consumes: T9 (`QuotationVariantRef`).
- Produces: requests con `variantId` en lugar de `productId`; las líneas guardan `variant_id` y `product_id`; foto `product_code` = `Code` de la variante, `product_name` = nombre visible; duplicado por **variante** con los códigos de hoy; `QuotationScaleGroupPricing` agrupa con clave de variante: escala sin `AllowGrouping` cuenta sólo la cantidad de la variante, con `AllowGrouping` suma entre variantes con la misma `(FromUnit, ToUnit, Multiple)`. `IExternalOrderWriter` recibe `VariantId`.

- [ ] **Paso 1: pruebas rojas:** `ScaleGroup_SixMAndSixLDoNotAddUp` (**Enfoque 5**), `ScaleGroup_AllowGroupingAddsAcrossVariants`, `AddItem_TwoSizesOfSameProductAreTwoLines`, `AddItem_SameVariantTwiceReturnsDuplicateProductCode`, `Draft_LabelFollowsRenamedVariantUntilSent`, `SentQuotation_SnapshotDoesNotChangeAfterRename`, `Migration_BackfillsVariantIdFromProductIdAndKeepsTotals`.
- [ ] **Paso 2:** correr `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "QuotationScaleGroupPricingTests|QuotationItemEditsTests|QuotationItemProductLabelTests|OrderItemEditsTests"` → FALLA.
- [ ] **Paso 3:** implementar dominio, migración y handlers; el diff de ítems (`QuotationItemEdits`/`OrderItemEdits`) empareja por `VariantId`.
- [ ] **Paso 4:** barrer **todas** las pruebas de integración de Quotations que postean cuerpos crudos con `productId` (`QuotationItemApiTests`, `OrderApiTests`, `OrderEditsApiTests`, `QuotationEditsApiTests`, `RetailQuotationApiTests`, …) y los harness (`CreateSentQuotationAsync`): pasan a `variantId`. Correr `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests` y luego `…IntegrationTests` → PASAN.
- [ ] **Paso 5:** commit `feat(quotations): líneas de cotización y pedido por variante`.

### Tarea 11: Pedidos externos (Shopify) por variante

**Files:** `src/Bootstrapper/ShopifyOrderWriter.cs`, `Modules.Shopify.Application/ShopifyPorts.cs`, `ShopifyOrderImporter.cs`; tests `ShopifyAdaptersTests.cs`, `ExternalOrderServiceTests.cs`.

**Interfaces:** el importador empareja `sku` ↔ `Code` de variante vía `IShopifyProductLookup` (T9) y pasa `VariantId` a `IExternalOrderWriter`; una línea sin variante sigue entrando sin producto como hoy. `ShopifyWebhookTopicsParityTests` y `ShopifyNormalizationParityTests` no se tocan.

- [ ] **Pasos:** prueba roja `Import_MatchesLineByVariantCode` y `Import_UnmatchedSkuStaysWithoutProduct` → correr `dotnet test tests/Modules/Shopify tests/Bootstrapper/Bootstrapper.UnitTests --filter ShopifyAdapters…` (FALLA) → implementar → correr (PASA) → commit `feat(shopify): emparejar pedidos por el Code de la variante`.

### Tarea 12: Punto de venta por variante

**Files:** `Pos.Application/CreatePosSale.cs`, `PreviewPosSale.cs`, `PosDtos.cs`, `PosSaleFingerprint.cs`, `PosSaleResponses.cs`; `Pos.Domain/PosSaleLine.cs`, `PosSaleInputs.cs`; `PosDbContext.cs` + migración `AddVariantToSaleLines` (`variant_id = product_id`); `PosEndpoints.cs`; tests `PosSaleApiTests.cs`, `PosRegisterApiTests.cs`, `PosProductLookupTests.cs`, nuevo `PosVariantApiTests.cs`.

**Interfaces:** `GET /pos/products/by-code?code=` usa el orden de T9 y devuelve la variante con nombre visible; `GET /pos/products` busca por nombre del producto, `Code` o código de barras y devuelve productos con sus variantes vendibles; las ventas mandan `variantId`; la huella de idempotencia (`PosSaleFingerprint`) usa `variantId`; las líneas guardan la foto del nombre visible.

- [ ] **Paso 1: pruebas rojas:** `ByCode_ScannerBarcodeReturnsVariant`, `ByCode_BarcodeWinsOverOtherVariantCode` (**Enfoque 4** de punta a punta), `Sale_LinesGroupedByVariantNotProduct`, `Sale_TicketNameIsVisibleVariantName`, `Fingerprint_ChangesWhenVariantChanges`.
- [ ] **Paso 2:** correr `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "PosVariantApiTests|PosSaleApiTests"` → FALLA.
- [ ] **Paso 3:** implementar. Recordar que `Pos` exige la capacidad `pos` (`PosApiHarness.EnablePosAsync`). **Paso 4:** barrer cuerpos crudos de las pruebas de Pos; correr el módulo → PASA.
- [ ] **Paso 5:** commit conjunto de T9–T12: `feat: vender por variante en cotizaciones, pedidos y POS`.

### Tarea 13: Reporte de cambios de precio por producto y variante

**Files:** `Bootstrapper/PriceChangeReportSource.cs`, `Reporting.Application/{ReportSources,ReportingFilters,PriceChangeReportMapping,PriceChangeReportSummary,ReportingDtos}.cs`; tests `PriceChangeReportApiTests.cs`, `PriceChangeReportSummaryApiTests.cs`, `PriceChangeReportSummaryHandlerTests.cs`.

**Interfaces:** filtro `variantId?` además de `productId?`; las filas llevan `variantId` y nombre visible; el resumen agrupa por producto con `variants[]` desglosado (colección completa, aunque venga en cero, según el patrón BFF del repo).

- [ ] **Pasos:** pruebas rojas `Report_FiltersByVariant`, `Summary_GroupsByProductWithVariantBreakdown` → correr (FALLA) → implementar → correr (PASA) → commit `feat(reporting): cambios de precio por producto y variante`.

### Tarea 14: PDF y Excel de pedidos

**Files:** `Quotations.Domain/OrdersExportColumnCatalog.cs`, `OrdersExportProcessor.cs`, `QuotationPdfDocument.cs` (`QuotationPdfLine.ProductName` conserva el nombre), semilla de columnas del layout; tests `OrdersExportProcessorTests.cs`, prueba del catálogo.

**Interfaces:** «Cod. Producto» = `Code` de la variante; nueva columna «Variante» (nombre visible). El PDF muestra el nombre visible sin cambiar la plantilla.

- [ ] **Pasos:** pruebas rojas `OrdersExport_ProductCodeIsVariantCode`, `OrdersExport_NewVariantColumnDoesNotCollideWithSeededHeaders` (la regla de choque del README: una columna nueva con encabezado visible igual nace oculta) → correr (FALLA) → implementar → correr (PASA) → commit `feat(quotations): variante en el Excel de pedidos y el PDF`.

### Tarea 15: Siembras, pruebas de arquitectura, documentación y contrato para el frontend

**Files:** `Bootstrapper/Seeding/ExportLoadSeeder.cs` (SQL crudo: `variant_id`, filas de `product_variants`), `CatalogSeeder.cs` + `catalog-products.json`; `tests/ArchitectureTests/CatalogLayerTests.cs` (verificar que nada nuevo rompe capas); `README.md` (§ Catálogo, § Columnas del Excel de pedidos); `CLAUDE.md` (gotchas: «variante por defecto lleva el id del producto», «un `INSERT` a mano en líneas necesita `variant_id`», «el cuerpo crudo con `productId` ya no sirve»); nuevo `docs/superpowers/plans/2026-10-20-variantes-handoff.md` con rutas, DTOs y códigos de error para el plan del frontend.

- [ ] **Paso 1: prueba roja:** `CatalogSeedTests` espera variante por defecto con `Code` del JSON; una prueba del sembrador de carga espera filas con `variant_id`.
- [ ] **Paso 2:** correr `--filter "CatalogSeedTests"` y la del sembrador → FALLAN. **Paso 3:** implementar y corregir README/CLAUDE.md (lo escrito con voseo que se toque, a tuteo).
- [ ] **Paso 4:** verificación final del repo (README § Verificación: `restore`, `format`, `build`) y las pruebas de **los módulos tocados**: Catalog, Quotations, Pos, Reporting, Shopify, `Bootstrapper.UnitTests`, `ArchitectureTests`. Si el cambio de paquetes no aplica, no regenerar lock files.
- [ ] **Paso 5:** commit `docs: README, CLAUDE.md y traspaso al frontend para variantes`.

---

## Autorrevisión

- **Cobertura del spec:** §2.1 → T4; §2.2–2.3 y galería → T2/T3/T5; §2.4 → T10/T12; §2.5 → T9/T12; §2.6 → T3 (+T10, T12 para las líneas); §2.7 → T7/T13; §2.8 → T8/T14; §2.9 → T11; §3.3.1 → T5; §3.3 borrado condicionado → T6; §3.5 → T7; §4 → T10; §5 → T12; §6 → T5/T15; §7 → T8/T14; §8 → T3/T10/T12/T15.
- **Tipos consistentes:** `VariantId`, `QuotationVariantRef`, `PosProductRef.VariantId`, `IVariantUsageChecker.FindUsedAsync`, `Product.MaxVariants/MaxOptions/MaxGalleryImages` se usan con el mismo nombre en todas las tareas.
- **Orden y despliegue:** T1–T8 compilan y se prueban dentro de Catalog; T9–T12 forman un bloque que cambia contratos entre módulos y se commitea junto; T13–T15 siguen. Todo sale en la misma ventana que el frontend (decisión 7).
- **Decisiones que el plan deja al builder (spec §10):** nombres de columnas internas no citados arriba, UI de la tabla de variantes (2.048 filas) y de la galería (250 fotos), largo final del código de barras (64 sugerido).
