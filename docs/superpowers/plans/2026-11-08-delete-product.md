# Eliminar producto del catálogo — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `DELETE /api/v1/tenants/{tenantId}/catalog/products/{productId}` borre un producto con todas sus variantes de forma permanente, en una sola transacción, sin cambiar ningún documento cerrado, recotizando los borradores y sin dejar fotos huérfanas en R2.

**Architecture:** El handler de Catalog abre la transacción de Catalog y llama a un puerto nuevo, `IProductDeletionCleanup`, **antes** de quitar el producto. Su adaptador en Bootstrapper enrola el `QuotationsDbContext` en la conexión y la transacción de Catalog, corre el servicio público `IQuotationVariantDeletion` de Quotations (foto de las líneas cerradas y borradores recotizados) y borra las líneas de las solicitudes `New` de la tienda con SQL sobre la conexión de Catalog. Las fotos salen por el outbox de plataforma (`catalog.product.images-released.v1`, al borrar y al quitar fotos por el `PUT`), las consume un procesador nuevo de Storage que pregunta a las `IFileReferenceProbe`, entre ellas una nueva de Catalog.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-08-delete-product-design.md`](../specs/2026-11-08-delete-product-design.md) · spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-08-delete-product-design.md`

---

## Antes de empezar

- Worktree `qep-backend-worktrees/delete-product`, rama `feat/delete-product`. Antes de **cada** commit: `git branch --show-current` tiene que decir `feat/delete-product`; si dice otra cosa, frena y pregunta.
- Todo comando es PowerShell, una línea por comando, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas:** es del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y, en la misma sesión, `$env:Seed__Enabled = "false"`. Nunca la solución entera: un proyecto a la vez, con `--filter` a la clase que tocas y `-- xUnit.MaxParallelThreads=4`. La corrida completa va una sola vez, en la Tarea 11.

## Global Constraints

- Ruta exacta: `DELETE /api/v1/tenants/{tenantId:guid}/catalog/products/{productId:guid}`, permiso `CatalogPermissions.ProductManage`, sin permiso nuevo.
- Respuestas: 204 sin cuerpo · 403 sin permiso · 404 `catalog.product.not_found` · 412 `concurrency.conflict` · 428 `precondition.if_match_required`. Sin 422. `If-Match` acepta `"7"` y `W/"7"`.
- Auditoría: acción `catalog.product.deleted`, `resourceId` = id del producto, outcome `success`, por `ICatalogAuditPublisher`.
- Evento nuevo: `catalog.product.images-released.v1`, payload camelCase `{ "tenantId": "<guid>", "fileIds": ["<guid>", …] }`, escrito en `platform.outbox_messages` en la misma transacción, **sólo si la lista no está vacía**.
- Consumidor de Storage: `storage.product-image-release`; auditoría `storage.file.purged`, resourceType `file`, outcome `product_image_released`.
- Todo el borrado es **una** transacción de base: si un paso falla no queda nada escrito, outbox incluido.
- Sin columnas nuevas. La única migración es un índice de Catalog (Tarea 3).
- `Modules.<M>.Application` no referencia EF Core ni Npgsql (`tests/ArchitectureTests/ArchitectureTests`). El SQL entre esquemas va en Infrastructure o en Bootstrapper.
- TDD: RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- Commits: conventional commits, **sin `Co-Authored-By` ni atribución de IA**, siempre `git commit -m "…" -- <rutas>`.
- Comentarios de código en español, tuteando; el voseo se corrige sólo en las líneas que tocas.
- `docs/estado.md` **no** se toca en esta rama (se actualiza en `develop` al mergear).

## Decisiones de este plan (lo que el spec deja al constructor)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | **Orden:** dentro de la transacción de Catalog, la limpieza (foto → borradores → tienda) corre **antes** de `repository.Remove(product)`. En cada borrador se quitan las líneas **antes** de la pasada de precios. | La foto lee el nombre del catálogo con el producto todavía vivo, y la pasada nunca pide la variante borrada (`QuotationPricingRecalculation.cs:70-73`). |
| P2 | **Historial del borrador:** ninguna entrada ni auditoría por la línea quitada. Sube `Version` y `UpdatedAt`; `UpdatedBy` no cambia (como `Quotation.Expire`). | Decisión del owner 3 («desaparece en silencio»). El historial existente no se toca. |
| P3 | **Solicitud que queda sin líneas:** se queda `New`. Tomarla responde 422 `order.order.items_required` (`Order.cs:559-566`) y suelta el lease; el asesor la descarta. Toda solicitud `New` que pierde líneas sube `version`. | Recomendación del spec. Subir `version` hace que una toma en curso choque por concurrencia en vez de crear un pedido con la línea vieja. |
| P4 | **Recorte de la foto en código:** `new QuotationProductSnapshot(variant.Code, variant.Name)` desde `IQuotationProductLookup`, y un `UPDATE … FROM unnest(…)` por tabla. | Es la misma fuente y el mismo constructor que usa el envío. El recorte no se copia a SQL. |
| P5 | **Cambio de moneda con una línea borrada:** 422 `quotation.item.product_not_found` (lo que ya hace `QuotationProductPricingResolver.cs:114-120`). Sin cambio de código; lo amarra una prueba. Primero se quita la línea y se guarda, después se cambia la moneda. | El dominio exige precio para **toda** línea al cambiar de moneda (`quotation.quotation.repricing_incomplete`, `Quotation.cs:693-696`), y no se admiten dos monedas. El frontend ya traduce el código en `quotes.api.ts:561`. |
| P6 | **Orquestación:** puerto `IProductDeletionCleanup` (Catalog.Application), adaptador `Bootstrapper/CatalogProductDeletionCleanup.cs`, servicio público `IQuotationVariantDeletion` en Quotations.Application registrado como `IStoreOrderGateway`. | Catalog no ve Quotations ni Store (`CatalogLayerTests`); la pasada de precios es `internal` de Quotations. |
| P7 | **Bucket y claves:** las fotos de producto son `FileOwnerType.Product` (`qep-frontend/src/features/catalog/services/product-image.api.ts:110`) y viven en el bucket privado: se borra `StorageKey` y cada `FileVariant.StorageKey` con `IObjectStorage`. Si tienen `PublicStorageKey`, también esa clave y `StorageKey.PublicVariantFor(publicKey, variant)` de cada variante con `IPublicObjectStorage` (las mismas que `FilePublication.UnpublishAsync`). `PublicStorageKey` se conserva como registro, como en `PurgeDetachedPaymentProof`. **Sólo se purgan archivos `Product`**: cualquier otro dueño (imagen legacy `User`, logo, banner, comprobante) se salta con log. | Una galería acepta cualquier imagen del tenant (`ProductImageResolver`), y sólo Catalog declara sonda. Purgar un logo o un comprobante por estar en una galería sería borrar algo que otro módulo usa sin sonda. |
| P8 | Índice nuevo `IX_product_gallery_images_file` sobre `file_id`. | La sonda consulta por archivo sin tenant, y la corren los tres procesadores de Storage por cada archivo (precedente `IX_order_payment_proofs_file`). |
| P9 | El nombre del evento vive en dos copias (`CatalogIntegrationEvents`, `StorageSubscribedEvents`) amarradas por una prueba de paridad en `Bootstrapper.UnitTests`. | Precedente `CitySynonymEventNamesParityTests`. |
| P10 | La lectura de `If-Match` de Catalog.Api se mueve a `CatalogIfMatch` y la usan categorías y productos. | `CategoryEndpoints.cs:136-165` ya era la copia del módulo; una segunda copia en el mismo assembly sobra. |
| P11 | Worker nuevo `ProductImageReleaseWorker`, cada 15 s, con su propio scope. | Mismo intervalo que `PaymentProofMoveWorker`; no se mezcla con el movimiento de comprobantes. |

## Review Focus

1. **El producto de prueba del owner: nunca usado y sin fotos.** 204, ningún evento de imágenes, ninguna sentencia que falle con arreglos vacíos. Lo amarra la Tarea 10 (`DeletingAnUnusedProductWithoutImagesWritesNoImageEvent`).
2. **Una foto compartida por dos productos.** Borrar uno no la purga mientras la otra galería la tenga. Lo amarran la Tarea 3 (`TrueWhileAnotherGalleryStillHasTheFile`) y la Tarea 4 (`AnImageStillReferencedIsKept`).
3. **Un borrador abierto en otra pestaña.** Su siguiente guardado responde 412 `concurrency.conflict`, nunca 500 ni resucita la línea. Lo amarra la Tarea 10 (`ADraftOpenInAnotherTabGets412`).
4. **Una solicitud `New` cuyo único producto se borró.** El detalle sigue respondiendo 200 y «Tomar» responde 422 `order.order.items_required` sin dejar el lease puesto. Lo amarra la Tarea 9 (`ARequestLeftWithoutLinesCannotBeTakenAndKeepsNoLease`).
5. **Un archivo que no es foto de producto dentro del evento** (logo, banner, comprobante o imagen legacy `User`): nunca se purga. Lo amarra la Tarea 4 (`ANonProductFileIsNeverPurged`).

---

### Task 1: Storage — `FileResource.PurgeReleasedProductImage`

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs` (después de `PurgeDetachedPaymentProof`, `:263-273`; guard nuevo junto a `RequirePaymentProof`, `:388-396`)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileResourceTests.cs`

**Interfaces:**
- Produces: `public void PurgeReleasedProductImage(DateTimeOffset occurredAt)` — exige `OwnerType == Product` y `Status == Available`; deja `Status = Purged`, `DeletedAt = UpdatedAt = occurredAt`; no toca `PublicStorageKey`. Rechazos: `StorageDomainException("storage.file.invalid_state", …)`.

- [ ] **Step 1: Write the failing tests** (arma el recurso con los helpers del archivo, `ownerType: FileOwnerType.Product`):
  - `PurgeReleasedProductImageMarksAnAvailableProductImagePurged` → `Status == Purged`, `DeletedAt == now`, `PublicStorageKey` igual al de antes.
  - `PurgeReleasedProductImageRequiresAvailable` (recurso en `PendingScan`) → `Code == "storage.file.invalid_state"`.
  - `PurgeReleasedProductImageRequiresAProductImage` (dueño `User`, `Available`) → `Code == "storage.file.invalid_state"`, `Status` sigue `Available`.
- [ ] **Step 2: Run, expect FAIL** (compile: el método no existe).
  `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileResourceTests"`
- [ ] **Step 3: Implement.** Guard `RequireProductImage()` con la forma de `RequirePaymentProof`. Comentario: la llama `ProductImageReleaseProcessor` después de borrar los objetos (spec 2026-11-08, «Liberación de imágenes»).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(storage): add purge for released product images`.

### Task 2: Catalog — evento de imágenes liberadas y el `PUT` que quita fotos

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/CatalogIntegrationEvents.cs`, `src/Modules/Catalog/Modules.Catalog.Application/IProductImageReleasePublisher.cs`, `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductImageReleasePublisher.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/UpdateProduct.cs:33-45` (constructor) y `:155-177` (alrededor de `product.Update`), `src/Modules/Catalog/Modules.Catalog.Infrastructure/CatalogInfrastructureExtensions.cs:35-37`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductImageReleaseApiTests.cs`

**Interfaces:**
- Produces: `public static class CatalogIntegrationEvents { public const string ProductImagesReleased = "catalog.product.images-released.v1"; }`; `public interface IProductImageReleasePublisher { void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt); }` — no hace nada con la colección vacía; deduplica los ids. La implementación agrega un `CatalogOutboxMessage` como `ProductExportEventPublisher`, con payload `record ImagesReleasedPayload(Guid tenantId, IReadOnlyList<Guid> fileIds)`.

- [ ] **Step 1: Write the failing tests** (sube imágenes como `ProductDetailsApiTests.UploadImageAsync`; si es privada, muévela a un helper compartido del proyecto; lee `platform.outbox_messages` por `event_name`):
  - `RemovingAPhotoWritesTheReleaseEventWithThatFileId` — galería `[A, B]`, `PUT` con `[A]` → una fila `catalog.product.images-released.v1`; `payload.tenantId == TenantId`, `payload.fileIds == [B]`.
  - `ReorderingTheGalleryWritesNoReleaseEvent` — `[A, B]` → `[B, A]` → cero filas del evento.
  - `AnUnchangedGalleryWritesNoReleaseEvent` — `[A, B]` → `[A, B]` (otro cambio, el nombre) → cero filas.
- [ ] **Step 2: Run, expect FAIL** (la primera: cero filas).
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductImageReleaseApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** En `UpdateProductHandler`: foto de `product.Gallery.Select(image => image.FileId)` antes de `product.Update`; después, `released` = esos ids que no están en `gallery`; `imageReleasePublisher.Publish(command.TenantId, released, now)` antes de `SaveChangesAsync` (misma transacción). Registro `services.AddScoped<IProductImageReleasePublisher, ProductImageReleasePublisher>()`.
- [ ] **Step 4: Run, expect PASS**, más `ProductImageApiTests` y `ProductWriteApiTests` filtrados (siguen verdes).
- [ ] **Step 5: Commit** — `feat(catalog): release removed gallery images through the outbox`.

### Task 3: Catalog — sonda de galería e índice por archivo

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductGalleryFileReferenceProbe.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/Migrations/<timestamp>_AddGalleryImageFileIndex.cs` (+ Designer y snapshot)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs:174-181` (`ConfigureGalleryImage`), `src/Modules/Catalog/Modules.Catalog.Infrastructure/CatalogInfrastructureExtensions.cs:38-39`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductGalleryFileReferenceProbeTests.cs`

**Interfaces:**
- Produces: `internal sealed class ProductGalleryFileReferenceProbe(CatalogDbContext dbContext) : IFileReferenceProbe` con `Source => "catalog"` y `HasReferencesAsync(fileId)` = existe alguna fila de `ProductGalleryImage` con ese `FileId`, de cualquier producto. Índice `IX_product_gallery_images_file` sobre `file_id`.

- [ ] **Step 1: Write the failing tests** (resuelve `GetServices<IFileReferenceProbe>()` y toma la de `Source == "catalog"`):
  - `TrueWhileAGalleryHasTheFile` → `true`.
  - `FalseForAFileInNoGallery` → `false`.
  - `TrueWhileAnotherGalleryStillHasTheFile` — la misma imagen en los productos P1 y P2; `PUT` de P1 sin ella → `true`.
  - `TheGalleryFileIndexExists` — `SELECT 1 FROM pg_indexes WHERE schemaname = 'catalog' AND indexname = 'IX_product_gallery_images_file'` devuelve una fila.
- [ ] **Step 2: Run, expect FAIL** (no hay sonda `catalog`; no hay índice).
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductGalleryFileReferenceProbeTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** `image.HasIndex(value => value.FileId).HasDatabaseName("IX_product_gallery_images_file")`; migración:
  `dotnet ef migrations add AddGalleryImageFileIndex --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations`
  Revisa que el `Up` sólo cree el índice. Registro `services.AddScoped<IFileReferenceProbe, ProductGalleryFileReferenceProbe>()` con comentario: Storage la consulta antes de purgar una foto liberada; también la ven `StagingCleanupProcessor`, `PaymentProofDetachProcessor` y `PaymentProofGuard`, y para ellos sólo puede retener, nunca borrar (spec, «Restricciones»).
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --filter "FullyQualifiedName~PaymentProofDetachTests|FullyQualifiedName~PaymentProofStagingCleanupTests" -- xUnit.MaxParallelThreads=4` (la sonda nueva entra en su host y no cambia nada).
- [ ] **Step 5: Commit** — `feat(catalog): add gallery file reference probe`.

### Task 4: Storage — `ProductImageReleaseProcessor`, worker y paridad del nombre

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/StorageSubscribedEvents.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/ProductImages/ProductImageReleaseProcessor.cs`, `src/Modules/Storage/Modules.Storage.Infrastructure/ProductImages/ProductImageReleaseWorker.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:68-72`
- Modify: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` (helpers y quitar el worker nuevo del host, como `PaymentProofMoveWorker` en `:427-438`)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/ProductImageReleaseTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ProductImageReleaseEventParityTests.cs`

**Interfaces:**
- Consumes: Task 1 (`PurgeReleasedProductImage`), Task 2 (`CatalogIntegrationEvents.ProductImagesReleased`).
- Produces: `public static class StorageSubscribedEvents { public const string ProductImagesReleased = "catalog.product.images-released.v1"; }`; `internal interface IProductImageReleaseProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }`; constantes `Consumer = "storage.product-image-release"`, `Reason = "product_image_released"`, lote de 20.

- [ ] **Step 1: Write the failing tests.** Harness: `AddImagesReleasedEventAsync(factory, tenantId, params Guid[] fileIds)`, `AddImagesReleasedPayloadAsync(factory, string json)`, `RunImageReleaseAsync(factory)`, `IsProcessedByImageReleaseAsync(factory, messageId)` y `VariantKeysAsync(connectionString, fileId)` (SQL sobre `storage.file_variants`). La imagen es `CreateAvailableAsync(client, factory, "Product", "foto.png", "image/png", await PngAsync(1600, 1200))`, que genera variantes.
  - `AReleasedImageLosesItsObjectAndVariantsAndIsPurged` → `Status == "Purged"`; `ObjectStorage.Exists(storageKey)` y cada clave de variante en `false`; procesado; una auditoría `storage.file.purged` con `ResourceId == fileId`, `Outcome == "product_image_released"`, `ActorType == "System"`.
  - `APublishedImageAlsoLosesItsPublicCopies` — publicada antes con `PUT /files/{id}/publication` → la clave pública y cada `StorageKey.PublicVariantFor` ya no existen en `PublicObjectStorage`.
  - `AnImageStillReferencedIsKept` — `factory.FileReferences.Reference(fileId)` → `Available`, objetos intactos, mensaje procesado.
  - `AReleaseIsAppliedOnlyOnce` — segundo `RunImageReleaseAsync` devuelve 0 y no borra nada más.
  - `WhenADeleteFailsNothingIsSavedAndTheMessageComesBack` — `ObjectStorage.FailingDeleteKey = storageKey` → sigue `Available` y sin procesar; quitas la falla y el siguiente lote lo purga.
  - `MalformedMessagesAreMarkedAndDoNotBlockALaterValidMessage` — payloads `"{"`, `{"tenantId":"x"}`, `{"tenantId":"<guid>"}` → procesados sin purgar; el válido de después se purga.
  - `AnImageOfAnotherTenantIsSkipped` — evento con otro `tenantId` → el archivo sigue `Available`.
  - `ANonProductFileIsNeverPurged` — archivo `User` (y uno `Tenant`) en el evento → `Available`, objetos intactos, mensaje procesado.
  - `WithoutProbesNothingIsPurgedAndTheMessageWaits` — procesador creado con `new List<IFileReferenceProbe>()` (como `PaymentProofDetachTests.cs:273`) → devuelve 0, sin procesar.
  - Paridad (`Bootstrapper.UnitTests`): `CatalogIntegrationEvents.ProductImagesReleased == StorageSubscribedEvents.ProductImagesReleased`, y las dos iguales al literal `"catalog.product.images-released.v1"`.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --filter "FullyQualifiedName~ProductImageReleaseTests" -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ProductImageReleaseEventParityTests"`
- [ ] **Step 3: Implement** con el esqueleto de `PaymentProofDetachProcessor.cs:36-331` (anti-join con el inbox, sin sondas no marca nada, cada archivo se guarda antes del siguiente y el último con el inbox, deterministas se marcan, transitorios hacen `ChangeTracker.Clear()`). Por cada `fileId` del payload:
  1. Carga con `.Include(file => file.Variants)` y `file.TenantId == payload.TenantId` en la consulta. Null → se salta.
  2. `OwnerType != Product` → log y se salta (P7). `Status != Available` → se salta (reintento idempotente).
  3. Alguna sonda responde `true` → log con su `Source` y se salta.
  4. `PurgeReleasedProductImage(now)` en memoria (un `StorageDomainException` se registra y se salta); después borra `StorageKey` y cada `variant.StorageKey` con `IObjectStorage`; si hay `PublicStorageKey`, esa clave y `StorageKey.PublicVariantFor(publicKey, variant)` de cada variante con `IPublicObjectStorage`; audita `storage.file.purged`.
  Parseo por nombre con `JsonDocument` (`tenantId`, `fileIds[]`), con las mismas cuatro excepciones deterministas que `DetachedPayload.TryParse`. Worker con la forma de `PaymentProofMoveWorker`, un solo procesador, `PollInterval = 15 s`. Registro: `AddScoped<IProductImageReleaseProcessor, ProductImageReleaseProcessor>()` y `AddHostedService<ProductImageReleaseWorker>()`.
- [ ] **Step 4: Run, expect PASS**, más `PaymentProofDetachTests` filtrado.
- [ ] **Step 5: Commit** — `feat(storage): purge product images released by the catalog`.

### Task 5: Quotations — el recálculo salta variantes borradas; el cambio de moneda queda en 422

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs:68-94`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPricingRecalculationTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationEditsApiTests.cs`

**Interfaces:**
- Produces: ningún tipo nuevo. Una línea cuya variante no devuelve `FindManyAsync` queda fuera de `lines` y de `scalesByVariant`, así que ni `Apply` ni el barrido del mínimo de compra la tocan (la forma de `OrderPricingRecalculation.cs:47-58`).

- [ ] **Step 1: Write the failing tests:**
  - Unitaria `ALineWhoseVariantIsGoneKeepsItsDiscount` (gemela de `OrderPricingRecalculationTests.ALineWhoseProductIsGoneKeepsItsDiscount`): línea `gone` agregada con 10 % y línea `kept` de 10 unidades; el lookup sólo conoce `kept` (escala 1-999 al 5 %) → `(10m, 5m)`. Hoy da `(0m, 5m)`.
  - Unitaria `TheMinimumPurchaseSweepLeavesAGoneLineAlone`: mínimo de 100 unidades que no se alcanza → `kept` queda en 0 % y `gone` sigue en 10 %.
  - Integración `SavingASentQuotationWithADeletedLineKeepsItsDiscount`: cotización enviada con una línea de 10 unidades (5 % por `DefaultScales`) y otra; `DELETE FROM catalog.products WHERE id = @id` por SQL (la cascada simula el borrado); save-all cambiando sólo `notes` → 200, la línea borrada conserva `discountPercentage == 5` y su `productCode`/`productName` de la foto.
  - Integración `ChangingCurrencyWithADeletedLineIsRejected` (P5): igual, pero el save-all cambia a una cuenta USD → 422 `quotation.item.product_not_found`; un `GET` devuelve la moneda y la `version` de antes. Esta prueba **pasa desde el primer intento**: caracteriza la decisión P5; pega su verde como evidencia.
- [ ] **Step 2: Run, expect FAIL** en las unitarias y en `SavingASentQuotation…` (descuento 0).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPricingRecalculationTests"`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationEditsApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** `scalesByVariant = products.ToDictionary(entry => entry.Key, entry => entry.Value.Scales)` y `lines` filtradas por `scalesByVariant.ContainsKey(item.VariantId)`. Reescribe el comentario de `:75-78`: la línea sin variante conserva su descuento (spec 2026-11-08, D-8), igual que en el pedido.
- [ ] **Step 4: Run, expect PASS** (las dos clases completas).
- [ ] **Step 5: Commit** — `fix(quotations): keep the discount of a line whose variant was deleted`.

### Task 6: Quotations — `Quotation.RemoveItemsOfDeletedVariants`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (junto a `RemoveItem`, `:609-615`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`

**Interfaces:**
- Produces: `public int RemoveItemsOfDeletedVariants(IReadOnlySet<Guid> variantIds, DateTimeOffset occurredAt)` — sólo en `Draft` (si no, `QuotationsDomainException("quotation.quotation.not_draft", …)`); quita las líneas cuya `VariantId` está en el set; si quitó alguna: `RecalculateTotals()`, `UpdatedAt = occurredAt`, `Version++`, `UpdatedBy` sin cambio (P2). Devuelve cuántas quitó.

- [ ] **Step 1: Write the failing tests:**
  - `RemovingDeletedVariantsDropsTheirLinesAndBumpsTheVersion` — borrador con A y B → devuelve 1; sólo queda A; `Version == antes + 1`; `UpdatedAt == now`; `UpdatedBy` igual; `Total` igual al de una cotización con sólo A.
  - `RemovingDeletedVariantsFromASentQuotationIsRejected` → `Code == "quotation.quotation.not_draft"`, las dos líneas siguen.
  - `RemovingVariantsThatAreNotThereChangesNothing` → devuelve 0 y `Version` no cambia.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationTests"`
- [ ] **Step 3: Implement.** XML doc: la usa el borrado de un producto (spec 2026-11-08, paso 3); no es una edición del asesor, por eso no cambia `UpdatedBy`.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(quotations): remove lines of deleted variants from a draft`.

### Task 7: Quotations — servicio `QuotationVariantDeletion`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/QuotationVariantDeletion.cs` (interfaz, servicio y el puerto `IDeletedVariantLineStore`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationVariantDeletionTests.cs`

**Interfaces:**
- Consumes: Task 5 (pasada que salta faltantes), Task 6.
- Produces:
  - `public interface IQuotationVariantDeletion { Task ApplyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, DateTimeOffset occurredAt, CancellationToken cancellationToken); }`
  - `public interface IDeletedVariantLineStore { Task<int> BackfillProductSnapshotsAsync(Guid tenantId, IReadOnlyDictionary<Guid, QuotationProductSnapshot> snapshots, CancellationToken cancellationToken); Task<IReadOnlyList<Quotation>> ListDraftsWithVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken); }` — la segunda devuelve agregados **rastreados** con líneas y partes.
  - `public sealed class QuotationVariantDeletion(IQuotationProductLookup productLookup, IDeletedVariantLineStore lineStore, IQuotationProductPricingLookup pricingLookup, IQuotationSettingsStore settingsStore, ITenantPriceRounding priceRounding, IQuotationsUnitOfWork unitOfWork) : IQuotationVariantDeletion`.

- [ ] **Step 1: Write the failing tests** (fakes locales: `RecordingDeletedVariantLineStore`, un lookup de precios que falla si le piden una variante borrada, un unit of work que cuenta guardados; el resto de `QuotationsTestDoubles.cs`):
  - `BackfillUsesTheTrimmedCatalogSnapshot` — `StubQuotationProductLookup` devuelve la variante con `Code "VS-1"` y un nombre de 205 caracteres → el store recibe `snapshots[variantId] == new QuotationProductSnapshot("VS-1", name)`: `Name.Length == 200`, termina en `"…"`.
  - `DraftsLoseTheLinesAndAreRepricedWithoutAskingForTheDeletedVariant` — borrador con A y B, 6 + 6 unidades, escalas con agrupación (1-9 al 0 %, 10-999 al 5 %), A al 5 % antes; se borra B → queda sólo A, al 0 %; `Version` +1; el lookup nunca recibió B; un guardado.
  - `WithoutDraftsNothingIsSaved` — el store no devuelve borradores → cero guardados; el backfill igual corrió.
  - `AnEmptyVariantListDoesNothing` → ni lookup, ni store, ni guardado.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationVariantDeletionTests"`
- [ ] **Step 3: Implement**, en este orden (P1): snapshots por variante desde `productLookup.FindManyAsync` → `BackfillProductSnapshotsAsync` → por cada borrador de `ListDraftsWithVariantsAsync`, `RemoveItemsOfDeletedVariants` y `QuotationPricingRecalculation.ApplyAsync(pricingLookup, settingsStore, priceRounding, tenantId, quotation, occurredAt, cancellationToken)` → `SaveChangesAsync` sólo si hubo borradores. Una variante que el lookup no devuelve no entra en el diccionario. XML doc: corre dentro de la transacción que le abre quien llama (el borrado de producto, por Bootstrapper); no abre ni confirma ninguna. **No se registra todavía:** el host de `Development` valida el contenedor al arrancar, y sin la implementación del store (Tarea 8) no levantaría ninguna prueba de integración.
- [ ] **Step 4: Run, expect PASS**, y `dotnet build` de la solución con 0 errores.
- [ ] **Step 5: Commit** — `feat(quotations): add the deleted-variant cleanup service`.

### Task 8: Quotations — `DeletedVariantLineStore` (SQL de la foto y borradores)

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DeletedVariantLineStore.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:62-70`, `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:58-64` (el doc dice «no hay backfill»), `src/Bootstrapper/QepServiceCollectionExtensions.cs:792` (registro del servicio de la Tarea 7, junto a `IStoreOrderGateway`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DeletedVariantLineStoreTests.cs`

**Interfaces:**
- Consumes: Task 7 (`IDeletedVariantLineStore`).
- Produces: `internal sealed class DeletedVariantLineStore(QuotationsDbContext dbContext) : IDeletedVariantLineStore`.

- [ ] **Step 1: Write the failing tests** (contra el esquema migrado; los documentos se arman con el harness y la foto se anula por SQL para simular una línea enviada antes del 2026-09-26):
  - `BackfillFillsSentAndOrderLinesWithoutSnapshot` — enviada con foto anulada y pedido con foto anulada → los dos con `product_code`/`product_name` del diccionario; devuelve 2.
  - `BackfillLeavesDraftsAndExistingSnapshotsAlone` — borrador (null) y enviada con foto `"OLD"` → el borrador sigue null y la enviada sigue `"OLD"`.
  - `BackfillNeverTouchesAnotherTenant` — la misma variante en una enviada de otro tenant con foto anulada → sigue null.
  - `ListDraftsReturnsOnlyTrackedDraftsOfTheTenantWithThoseVariants` — borrador con la variante, enviada con la variante, borrador sin ella y borrador de otro tenant → sólo el primero; quitarle una línea y `SaveChangesAsync` la persiste.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~DeletedVariantLineStoreTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Sin snapshots devuelve 0 sin consultar. Un `ExecuteSqlAsync` por tabla, con los tres arreglos como parámetros:

  ```sql
  UPDATE quotations.quotation_items qi
  SET product_code = s.code, product_name = s.name
  FROM quotations.quotations q,
       unnest({variantIds}::uuid[], {codes}::text[], {names}::text[]) AS s(variant_id, code, name)
  WHERE q.id = qi.quotation_id AND q.tenant_id = {tenantId} AND q.status <> 'Draft'
    AND qi.variant_id = s.variant_id AND qi.product_code IS NULL;

  UPDATE quotations.order_items oi
  SET product_code = s.code, product_name = s.name
  FROM quotations.orders o,
       unnest({variantIds}::uuid[], {codes}::text[], {names}::text[]) AS s(variant_id, code, name)
  WHERE o.id = oi.order_id AND o.tenant_id = {tenantId}
    AND oi.variant_id = s.variant_id AND oi.product_code IS NULL;
  ```

  `ListDraftsWithVariantsAsync`: `Include(Items)`, `Include(Parties)`, `AsSingleQuery()`, rastreado, `Status == Draft` y `Items.Any(item => ids.Contains(item.VariantId))`. Comentario: SQL por nombre sobre tablas propias; lo corre el borrado de producto en la transacción de Catalog. Corrige el doc de `QuotationItem.ProductCode`: las líneas viejas sin foto la reciben cuando su producto se borra. Registros: `services.AddScoped<IDeletedVariantLineStore, DeletedVariantLineStore>()` en Quotations.Infrastructure y `services.AddScoped<IQuotationVariantDeletion, QuotationVariantDeletion>()` en Bootstrapper, con el comentario de `IStoreOrderGateway` (el puerto que consume un adaptador; nadie más referencia Quotations.Application).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(quotations): backfill line snapshots and load drafts by variant`.

### Task 9: Bootstrapper — `CatalogProductDeletionCleanup` (transacción compartida y tienda)

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/IProductDeletionCleanup.cs`, `src/Bootstrapper/CatalogProductDeletionCleanup.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1194-1197` (junto a `IVariantUsageChecker`)
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs` (`SubmitAsync`, `:174-201`: parámetro opcional `IReadOnlyList<(Guid VariantId, int Quantity)>? lines`; una segunda variante `OtherVariant` creada como `Variant`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ProductDeletionCleanupTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/ProductDeletionStoreCleanupTests.cs`

**Interfaces:**
- Consumes: Task 7 (`IQuotationVariantDeletion`), Task 8.
- Produces: `public interface IProductDeletionCleanup { Task ApplyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, DateTimeOffset occurredAt, CancellationToken cancellationToken); }` — contrato: corre dentro de una transacción abierta con `ICatalogUnitOfWork.BeginTransactionAsync` y **antes** de quitar el producto; sin transacción lanza `InvalidOperationException`. Adaptador `internal sealed class CatalogProductDeletionCleanup(CatalogDbContext catalog, QuotationsDbContext quotations, IQuotationVariantDeletion quotationLines) : IProductDeletionCleanup`.

Las pruebas llaman al adaptador directo: en un scope, `ICatalogUnitOfWork.BeginTransactionAsync`, `IProductDeletionCleanup.ApplyAsync`, y `CommitAsync` (o no, en la de reversión).

- [ ] **Step 1: Write the failing tests.**
  Quotations (`ProductDeletionCleanupTests`):
  - `SentAndOrderLinesKeepTheirCodeAndTrimmedName` — producto con opciones y nombre de 200 caracteres (mueve `CreateShirtInTwoSizesAsync` de `QuotationVariantLinesApiTests` al harness con un parámetro de nombre); enviada y pedido con la foto anulada por SQL → tras confirmar, `product_code` es el de la variante y `product_name == SnapshotText.Fit("{nombre} – M", 200)`.
  - `ADraftWhoseGroupedTierDropsIsRepriced` — dos productos con escalas agrupables (1-9 al 0 %, 10-999 al 5 %), 6 + 6 unidades; se limpia B → el borrador tiene sólo A, `discountPercentage == 0`, `version` +1 y `total` igual a 6 × precio de A con su IVA.
  - `ADraftThatFallsUnderTheMinimumPurchaseIsRepriced` — `PUT …/quotations/settings` con `minimumUnits = 10` y `minimumTotals COP = 100000000`; A (5 unidades, 1-999 al 5 %) + B (6) → se limpia B → A al 0 %.
  - `NothingPersistsWhenTheCatalogTransactionIsNotCommitted` — mismo escenario de foto y borrador, sin `CommitAsync` → la foto sigue null y el borrador tiene sus dos líneas con su `version`. Es la prueba de que Quotations quedó enrolado.
  - `AnotherTenantIsUntouched` — los mismos documentos en otro tenant con la misma variante (id de variante sembrado por SQL en `quotation_items`) → sin cambios.
  - `ApplyingOutsideACatalogTransactionThrows` → `InvalidOperationException`.
  Store (`ProductDeletionStoreCleanupTests`):
  - `ANewRequestLosesTheLineAndKeepsTheTotalsTheBuyerSaw` — solicitud con `Variant` y `OtherVariant`; se limpia `OtherVariant` → una línea; `Subtotal`, `DiscountAmount`, `TaxAmount`, `Total` iguales; `Version` +1.
  - `AConvertedRequestIsUntouched` — tomada antes → líneas y `Version` iguales.
  - `TakingARequestThatLostALineCreatesTheOrderWithTheRestAndShowsTheDifference` → `TakeOkAsync` 200; el pedido tiene una línea; `StateAsync(id).TotalDifference` distinto de null.
  - `ARequestLeftWithoutLinesCannotBeTakenAndKeepsNoLease` — sólo con `OtherVariant` → `GET` del detalle 200; `TakeAsync` 422 `order.order.items_required`; `StateAsync(id)`: `Status == New`, `LeaseMemberId == null`.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ProductDeletionCleanupTests" -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~ProductDeletionStoreCleanupTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Primero el enrolamiento, que es lo único que ata a Quotations a la transacción:

  ```csharp
  var transaction = catalog.Database.CurrentTransaction
      ?? throw new InvalidOperationException("Product deletion cleanup must run inside the catalog transaction.");
  quotations.Database.SetDbConnection(catalog.Database.GetDbConnection(), contextOwnsConnection: false);
  await quotations.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
  ```

  Después `quotationLines.ApplyAsync(…)` y, en la conexión de Catalog:

  ```sql
  WITH removed AS (
      DELETE FROM store.order_request_lines l
      USING store.order_requests r
      WHERE r.id = l.request_id AND r.tenant_id = {tenantId} AND r.status = 'New'
        AND l.variant_id = ANY({ids})
      RETURNING l.request_id)
  UPDATE store.order_requests r SET version = r.version + 1
  WHERE r.id IN (SELECT request_id FROM removed);
  ```

  Con `variantIds` vacío no hace nada. XML doc con la forma de `CustomerMergeReferences`: corre sobre la conexión de Catalog, conoce por nombre `store.order_requests`/`order_request_lines` (`request_id`, `variant_id`, `status`, `version`) y las columnas de Quotations que toca su servicio; renombrar cualquiera compila y deja el `DELETE` en 500; lo amarran estas dos clases. Registro `services.AddScoped<IProductDeletionCleanup, CatalogProductDeletionCleanup>()`, con el mismo comentario de scope que `IVariantUsageChecker`.
- [ ] **Step 4: Run, expect PASS** (las dos clases).
- [ ] **Step 5: Commit** — `feat(bootstrapper): clean quotation and store lines of a deleted product`.

### Task 10: Catalog — `DELETE /products/{productId}`

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/DeleteProduct.cs`, `src/Modules/Catalog/Modules.Catalog.Api/CatalogIfMatch.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Api/ProductEndpoints.cs:53-60` (mapeo nuevo detrás del `PUT`) y su handler privado; `src/Modules/Catalog/Modules.Catalog.Api/CategoryEndpoints.cs:103,117,136-165` (usa `CatalogIfMatch`); `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs:209` y `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductRepository.cs:698` (`Remove`); `src/Bootstrapper/QepServiceCollectionExtensions.cs:210-212` (registro del handler); `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantTestDoubles.cs:11` (`FakeProductRepository.Remove` lanza `NotSupportedException`)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductDeletionApiTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ProductDeletionApiTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosProductDeletionApiTests.cs`

**Interfaces:**
- Consumes: Task 2 (`IProductImageReleasePublisher`), Task 9 (`IProductDeletionCleanup`).
- Produces: `public sealed record DeleteProductCommand(Guid TenantId, Guid ProductId, long ExpectedVersion) : ICommand<ProductDeletedResult>`; `public sealed record ProductDeletedResult(bool Deleted)`; `DeleteProductHandler(IProductRepository repository, IProductDeletionCleanup cleanup, IProductImageReleasePublisher imageReleasePublisher, ICatalogUnitOfWork unitOfWork, ICatalogAuditPublisher auditPublisher, IExecutionContext executionContext, IClock clock)`; `void IProductRepository.Remove(Product product)`; `internal static class CatalogIfMatch { long RequireVersion(HttpContext httpContext, string resource); }` (mensaje «…containing the loaded {resource} version…»).

- [ ] **Step 1: Write the failing tests.**
  Catalog (`ProductDeletionApiTests`, con los permisos de catálogo y storage de `ProductImageApiTests`):
  - `DeleteWithoutIfMatchIs428` → 428 `precondition.if_match_required`; el `GET` sigue 200.
  - `DeleteWithAStaleVersionIs412` → 412 `concurrency.conflict`; el `GET` sigue 200.
  - `DeleteOfAnUnknownProductOrOneOfAnotherTenantIs404` → 404 `catalog.product.not_found` en los dos casos (ruta del propio tenant).
  - `DeleteUnderAnotherTenantRouteIs403` y `DeleteWithoutManagePermissionIs403`.
  - `DeleteRemovesTheProductAndEverythingHangingFromIt` — producto con dos variantes por opción, precios, escalas, galería de dos fotos (una elegida por variante) y un cambio de precio por `PUT` → 204 sin cuerpo; cero filas del producto en `products`, `product_variants`, `product_prices`, `product_price_scales`, `product_options`, `product_variant_option_values`, `product_gallery_images` y `product_price_changes`; `GET` → 404.
  - `DeleteAcceptsAWeakEtag` — `If-Match: W/"<version>"` → 204.
  - `DeleteWritesTheImagesReleasedEventWithTheGalleryFileIds` → una fila; `fileIds` es el conjunto de las dos fotos.
  - `DeletingAnUnusedProductWithoutImagesWritesNoImageEvent` (Review Focus 1) → 204 y cero filas del evento.
  - `DeleteIsAudited` → un `catalog.product.deleted` con `resourceId == productId`.
  Quotations (`ProductDeletionApiTests`, por HTTP):
  - `DeletingAProductFreezesOldSentLinesAndRepricesDrafts` — enviada con foto anulada y borrador con el producto y otro → 204; la enviada tiene código y nombre; el borrador perdió la línea y subió de versión.
  - `AStaleVersionTouchesNothing` → 412; la enviada sigue sin foto y el borrador conserva la línea y su versión.
  - `AFailureAfterTheCleanupLeavesEverythingAsItWas` — trigger `BEFORE DELETE ON catalog.products` que hace `RAISE EXCEPTION` para ese id → 500; la enviada sigue sin foto, el borrador conserva la línea y su versión, ninguna fila `catalog.product.images-released.v1`, el `GET` del producto 200. El trigger se borra al final de la prueba.
  - `ADraftOpenInAnotherTabGets412` (Review Focus 3) — save-all del borrador con la `version` de antes del `DELETE` → 412 `concurrency.conflict`; el `GET` sigue sin la línea.
  Pos (`PosProductDeletionApiTests`, con `PosWorld` como `PosVariantApiTests`):
  - `ASaleKeepsItsLinesAfterTheProductIsDeleted` — venta cobrada → `DELETE` 204; `pos.sale_lines` de la venta con el mismo `product_code`, `product_name`, cantidades y totales; el `GET` de la venta igual que antes.
- [ ] **Step 2: Run, expect FAIL** (405: la ruta no existe).
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductDeletionApiTests" -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ProductDeletionApiTests" -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosProductDeletionApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Handler, en este orden (spec «Pasos de la transacción», P1): autoriza `ProductManage` → `FindAsync` (404 con `ProductNotFound.For`) → versión (412, el mismo texto que `UpdateProductHandler`) → ids de variantes y `fileIds` distintos de la galería → `BeginTransactionAsync` → `cleanup.ApplyAsync` → `repository.Remove(product)` → `imageReleasePublisher.Publish` → `auditPublisher.Publish(…, "catalog.product.deleted", product.Id.ToString(), "success", now)` → `SaveChangesAsync` → `CommitAsync`. Un cambio de versión entre la lectura y el commit sale 412 por `CatalogUnitOfWork` y revierte todo. Endpoint `group.MapDelete("/products/{productId:guid}", DeleteProductAsync)` con `.RequireAuthorization(CatalogPermissions.ProductManage)`, `.Produces(204)` y `ProducesProblem` 403, 404, 412 y 428; responde `Results.NoContent()`. Comentario en el endpoint: borrar es permanente aunque el producto esté en uso (modelo Shopify, spec D-5); `deletedVariantIds` del `PUT` sigue desactivando la variante usada y la diferencia es deliberada (decisión del owner 6). Registro `ICommandHandler<DeleteProductCommand, ProductDeletedResult>` → `DeleteProductHandler`.
- [ ] **Step 4: Run, expect PASS** (las tres clases), más `CategoryAdminApiTests` filtrado (el `If-Match` de categorías no cambió) y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`.
- [ ] **Step 5: Commit** — `feat(catalog): delete a product permanently`.

### Task 11: Documentación, arquitectura y corrida completa

**Files:**
- Modify: `CLAUDE.md` (gotcha nuevo junto al de `CustomerMergeReferences`), `README.md` (§ «Código y nombre del producto en cada línea» y § «Biblioteca de archivos (Cloudflare R2)»)

- [ ] **Step 1: Docs.** Gotcha en `CLAUDE.md`: `CatalogProductDeletionCleanup` escribe en `store.order_request_lines`/`order_requests` por nombre y enrola el `QuotationsDbContext` en la transacción de Catalog (`SetDbConnection` + `UseTransactionAsync`); renombrar una columna compila y deja el `DELETE` del producto en 500; lo amarran `ProductDeletionCleanupTests` y `ProductDeletionStoreCleanupTests`. README: la foto de las líneas se completa al borrar el producto (ya no es cierto que «no hay backfill»), y la fila del evento `catalog.product.images-released.v1` → `ProductImageReleaseWorker`, que sólo purga archivos `Product` sin referencias.
- [ ] **Step 2: Architecture tests** — `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (ninguna referencia nueva de Application a EF Core o Npgsql; ningún módulo nuevo, así que no hay `<Modulo>LayerTests` nuevo).
- [ ] **Step 3: Format** — `dotnet format --verify-no-changes` → sin cambios.
- [ ] **Step 4: Corrida completa, una vez, un proyecto a la vez**, con `$env:Seed__Enabled = "false"` y `-- xUnit.MaxParallelThreads=4`: Catalog, Quotations, Store, Pos, Storage, Bootstrapper.UnitTests y después el resto de los proyectos de `tests/`. Esperado: 0 fallas. Una prueba roja que pasaba antes de esta rama es regresión de este trabajo: se arregla en los archivos de la tarea dueña y se commitea como `fix(…)`.
- [ ] **Step 5: Commit** — `docs: describe product deletion and released product images` con `-- CLAUDE.md README.md`.
- [ ] **Step 6: Nota para el merge** (no se commitea en la rama): al mergear a `develop`, actualizar `docs/estado.md` (fila de Catalog: borrado permanente de producto; fila de Storage: purga de fotos liberadas; Registro con el commit del merge) y anotar el orden de despliegue **backend primero**, con la migración `AddGalleryImageFileIndex`.
