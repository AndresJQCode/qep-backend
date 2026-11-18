# Productos desde Shopify: plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Traer el catálogo de una tienda de Shopify a QEP con un trabajo asíncrono, emparejar las líneas de pedido por id de
variante antes que por SKU, mantener lo importado con los webhooks `products/*` y medir la cobertura del catálogo.

**Architecture:** El módulo Shopify guarda el vínculo (`shopify.product_links`) y el trabajo (`shopify.product_imports`), y
escribe en Catalog sólo por un puerto (`IShopifyCatalogWriter`) cuyo adaptador de Bootstrapper enrola un `CatalogDbContext` de
un scope hijo en la transacción abierta de Shopify: producto, opciones y vínculos se confirman juntos o no se confirman. Catalog
expone un camino de sistema (`IExternalProductWriter`, precedente `IExternalOrderWriter` de Quotations) que reusa los agregados
y las reglas del `POST`/`PUT` sin `IExecutionContext`. La imagen va en una segunda pasada, fuera de la transacción, por un camino
de servidor nuevo de Storage.

**Tech Stack:** .NET 10, EF Core + Npgsql, Admin API GraphQL de Shopify (versión `Shopify:App:ApiVersion`), xUnit +
Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-productos-desde-shopify-design.md` (§10 P1–P10 son vinculantes). Apilado sobre
`feature/reglas-empresa` (plan `..\reglas-empresa\docs\superpowers\plans\2026-11-17-reglas-de-empresa.md`).

## Global Constraints

- **Dónde se escribe:** sólo en el worktree
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\productos-shopify` (rama
  `feature/productos-shopify`), con rutas absolutas. Las rutas de este plan son relativas a esa raíz. Nunca en `...\qep-backend`.
- **Bloques (owner, 2026-11-12):** tres bloques, un implementador por bloque, una revisión por bloque; la revisión fuerte de toda
  la rama corre una vez, al final. Cada tarea conserva **su propio commit**. Las tareas encadenadas sobre los mismos archivos van
  en el mismo bloque.
  - **Bloque A — Catalog y Storage** (Tareas 1–4): el camino de sistema de Catalog, la protección de lo importado y la subida de
    servidor. No toca Shopify.
  - **Bloque B — Núcleo de Shopify** (Tareas 5–10): persistencia, emparejamiento por variante, cliente GraphQL de productos,
    puertos y adaptadores, mapeo y sincronización de un producto. Consume las interfaces de A.
  - **Bloque C — Trabajo, API, webhooks y despliegue** (Tareas 11–14). Consume las de B.
- **Pruebas escritas, no ejecutadas.** TDD: la prueba primero. El RED es la salida literal de `dotnet build` con el error de
  compilación (`CS0117`, `CS1061`, `CS7036`, `CS0246`…), copiada en el reporte del bloque. Si el fallo esperado es de
  comportamiento, la prueba queda escrita y su RED/GREEN lo corre el owner al final. **En esta rama no se corre `dotnet test`.**
- **Verificación de cada tarea:** desde la raíz del worktree, en PowerShell: `dotnet build Backend.slnx; $LASTEXITCODE` → `0` y
  `0 Error(s)`. Nada de `| Select-Object -Last` sin mirar `$LASTEXITCODE`. Si `Api.exe` (o `dotnet Api.dll`) corre, el build falla
  por archivo bloqueado: avisa, no lo detengas.
- **Al final (no por tarea):** el owner corre una vez las pruebas de los proyectos tocados: `Modules.Catalog.*`,
  `Modules.Storage.*`, `Modules.Shopify.*`, `Modules.Integrations.*`, `Bootstrapper.UnitTests` y `ArchitectureTests`, uno a la
  vez, con `-- xUnit.MaxParallelThreads=4` y `$env:Seed__Enabled = "false"`.
- **Commits:** conventional commits en inglés, **sin atribución de IA ni `Co-Authored-By`**. Antes de cada commit,
  `git -C <worktree> branch --show-current` tiene que decir `feature/productos-shopify`.
- **Idioma:** comentarios y documentación en español colombiano, tuteando. Identificadores, códigos de error, columnas y
  topics no se traducen.
- **Capas:** Shopify no referencia Catalog, Storage ni Integrations (`ShopifyLayerTests`): todo entra por `ShopifyPorts.cs` con
  adaptador en `src/Bootstrapper/`. Catalog.Application no referencia EF ni Npgsql (`CatalogLayerTests`).
- **Errores de base:** se traducen en el `UnitOfWork` de Infrastructure **por nombre de índice**, nunca sólo por `23505`.
- **Migraciones aditivas** (spec §8), generadas con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- **Códigos nuevos** (y ningún otro): `shopify.products.import_running` (409), `shopify.products.scope_missing` (422, con
  `reauthorizeUrl`), `shopify.products.company_required` (422), `shopify.products.link_not_found` (404),
  `catalog.product.managed_externally` (422 con `errors`).
- **Valores fijos de este plan:** página de `10` productos con `variants(first: 50)`; lease de `2 min`; `4` intentos; imagen
  hasta `10 MiB`, pedida como JPG de hasta `2048×2048`; `50` issues en el `GET`; `20` en `missingSample`; candado de producto
  `pg_advisory_xact_lock(20261118, hashtext('{connectionId}:{shopifyProductId}'))` (20261117 ya es de `CompanyDefaultTaxRates`).
- **`docs/estado.md` no se toca** en esta rama.

## Decisiones de este plan (lo que el spec dejó abierto)

- **D1. El webhook relee el producto por GraphQL siempre** (revierte el criterio de §6 «sólo con más de 100 variantes»): un solo
  parser, variantes completas, y el id de la imagen en el mismo espacio que la importación (`MediaImage`). Del payload REST sólo
  se leen `id` y `updated_at` (orden y `delete`). Costo: una consulta por webhook más la de la tienda.
- **D2. Columnas extra**, todas necesarias para una regla del spec: `product_imports.cursor` (reanudar tras un lease vencido sin
  recontar), `product_imports.requested_by_subject_id` (P3: la auditoría firma con el usuario; `requested_by` es membresía),
  `product_links.shopify_image_id` y `product_links.image_file_id` (§6 «imagen»: sólo se baja de nuevo si cambió, y sólo se
  reemplaza la foto que puso la importación).
- **D3. Unidades de los contadores:** `total`/`done`/`created` cuentan **productos**; `matched`, `without_sku` y `skipped` cuentan
  **variantes** (una falla de producto suma todas sus variantes a `skipped`). Lo dice el DTO.
- **D4. Tasa por producto** (Catalog la guarda por producto, Shopify `taxable` es por variante): 19 % si alguna variante es
  `taxable`, si no Exento; mezcla → 19 % e issue `mixed_taxable`. La tasa se asigna sólo al crear; un `update` no la toca (es
  editable en QEP, S-2). 19 % = la activa más antigua con `Type == "IVA"`, `Percentage == 19`, sin base ni ciudad; Exento = ídem
  con `Percentage == 0`.
- **D5. IVA sumado al precio** con `taxesIncluded = false` sólo si la empresa cobra IVA **y** la moneda es COP (misma regla que
  `ShopifyPricing.UnitPriceWithVat`, D-S27: fuera de COP los pedidos no suman IVA y el precio quedaría inflado). Redondeo con
  `ITenantPriceRounding.GetAsync(tenant, moneda)`.
- **D6. `reauthorizeUrl`** se arma con `StartShopifyInstallForTenantCommand` (Integrations), que exige `redirectUri`: el `POST`
  de importación y el `GET` de cobertura aceptan `redirectUri` opcional; sin él, el 422 lleva `reauthorizeUrl: null`. Pendiente
  del owner/trabajo 3 confirmar que el SPA lo manda.
- **D7. Protegido en un importado** (§5.5): nombre, `optionIds`, alta y baja de variantes (incluido `isActive` de una variante y
  `deletedVariantIds`), `code`, `barcode` y precios de lista. Se rechaza en el handler del `PUT` (necesita el producto
  guardado; un validador de FluentValidation no lo ve) con `CatalogDomainException` + `FieldErrors`, que sale 422 con `code` y
  `errors`. `ProductDto` gana `externalSource` para que la pantalla bloquee los campos (BFF).
- **D8. Un producto inactivo se actualiza** (Catalog `Update` exige activo): el escritor externo hace `Activate` → `Update` →
  `Deactivate` en el mismo guardado cuando el estado final es inactivo.
- **D9. Webhooks:** conexión no operativa → `Ignored` `connection_not_operational`; operativa sin token, `Throttled` o
  `Unavailable` → excepción (la entrega se reintenta); `Rejected` → `Ignored` `token_expired`; `AccessDenied` → `Ignored`
  `scope_missing`; rechazo de Catalog, moneda o empresa → `Processed` + log + métrica (P8).
- **D10. Sin cola en `QueueMetricsSource`**: el spec no la pide; queda como seguimiento.

## Review Focus

1. **Webhook e importación del mismo producto a la vez** dejan un producto y un juego de vínculos: el candado de producto
   serializa y `IX_product_links_connection_shopify_variant` es la red. Lo pina `AWebhookAndTheImportOfTheSameProductLeaveOneProduct`
   (Tarea 10, integración).
2. **Un `products/update` sin cambios** (Shopify lo manda por inventario) no sube la versión del producto: un asesor con el
   formulario abierto recibiría 412. Lo pina `AnUpdateWithoutChangesDoesNotTouchTheProduct` (Tarea 10).
3. **Un rechazo de Catalog dentro de la transacción compartida** la deja abortada en Postgres; si se siguiera usando, el issue
   no se guardaría y la importación entera caería. Lo pina `ACatalogRejectionRollsBackAndTheImportGoesOn` (Tarea 11).
4. **Producto importado borrado a mano en QEP** (`DELETE` de Catalog): los vínculos quedan colgando. El siguiente webhook o la
   importación los trata como ausentes, los borra y crea de nuevo. Lo pina `StaleLinksToADeletedProductAreReplaced` (Tarea 10).
5. **Scopes viejos:** una tienda sin `read_products` no intenta registrar `products/*` ni pasa a `NeedsAttention` (P10), y su
   importación responde 422 con URL. Lo pinan `ProductTopicsAreSkippedWithoutReadProducts` (Tarea 13) y
   `ImportWithoutReadProductsIs422WithReauthorizeUrl` (Tarea 12).

---

### Task 0: Rebase sobre `feature/reglas-empresa`

- [ ] **Step 1:** `git -C <worktree> rebase feature/reglas-empresa` (rama local). Los commits propios son el del spec y el de
  este plan; no debe haber conflictos.
- [ ] **Step 2:** Verifica que existan `Company.ChargesVat`, `ShopifyBillingAccountRef(..., bool ChargesVat = true)`,
  `IShopifyBillingAccounts.FindCompanyAsync` → `ShopifyBillingCompanyRef(Guid CompanyId, bool ChargesVat)` y
  `ICompanyDefaultTaxRates` (Tareas 1, 9 y 13 de reglas-empresa). Si falta alguna, **para y avisa**: esta rama no las crea.
- [ ] **Step 3:** `dotnet build Backend.slnx; $LASTEXITCODE` → `0`. Sin commit (el rebase no crea uno).

---

## Bloque A — Catalog y Storage

### Task 1: `Product.ExternalSource`

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/Product.cs` (propiedad, `Create` `:165`, métodos nuevos).
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` (mapeo de `products`).
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/Migrations/<ts>_AddProductExternalSource.cs` (+ Designer, snapshot).
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs:18` (`ProductDto`) y `ProductMapping.cs`.
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductTests.cs`, `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductApiTests.cs`.

**Interfaces:**
- Produces: `public string? ExternalSource { get; private set; }`; `public const int ExternalSourceMaxLength = 20;`;
  `public const string ShopifySource = "shopify";` en `Product`.
- Produces: el `Create` con opciones gana `string? externalSource = null` (último parámetro).
- Produces: `public void ReleaseExternal(DateTimeOffset occurredAt)` (pone `null`, `Version++`; sin efecto si ya era `null`, sin
  subir versión) y `public bool ReplaceGalleryImage(Guid? previousFileId, Guid newFileId, DateTimeOffset occurredAt)`
  (Tarea 3 la usa; reglas en Tarea 3).
- Produces: `ProductDto` con `string? ExternalSource` (después de `VisibleInStore`).

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void AProductCreatedFromShopifyKeepsItsSource()      // Create(..., externalSource: "shopify") → ExternalSource "shopify", Version 1
[Fact] void ReleasingClearsTheSourceAndBumpsTheVersion()     // ReleaseExternal → null, Version 2
[Fact] void ReleasingAnOwnProductChangesNothing()            // ExternalSource null → Version sigue 1
[Fact] void ASourceLongerThan20IsRejected()                  // CatalogDomainException "catalog.product.external_source_too_long"
// ProductApiTests
[Fact] Task TheDetailReturnsExternalSourceNull()            // POST normal → GET → externalSource == null
```

- [ ] **Step 2: RED** — `dotnet build`: `CS1739`/`CS1061` por `externalSource`/`ExternalSource`. Copia la salida.
- [ ] **Step 3: Implementa.** Columna `external_source varchar(20) NULL`. El código `external_source_too_long` es interno (sólo
  lo dispara el camino de sistema), no entra en la lista de códigos públicos.
- [ ] **Step 4: Migración** — `dotnet ef migrations add AddProductExternalSource --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations`. Espera un solo `AddColumn` nullable. Cabecera: «aditiva».
- [ ] **Step 5: Build** → `0`. **Commit** — `feat(catalog): mark products created from an external source`.

### Task 2: `catalog.product.managed_externally` en el `PUT`

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/ExternallyManagedProducts.cs`.
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/UpdateProduct.cs` (después de la comprobación de versión, `:68-73`).
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ExternallyManagedProductsTests.cs`,
  `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductManagedExternallyApiTests.cs` (`SharedCatalogHost`; siembra el
  producto con `ExternalSource = 'shopify'` por SQL).

**Interfaces:**
- Produces: `internal static class ExternallyManagedProducts { public const string Code = "catalog.product.managed_externally"; public static void EnsureAllowed(Product product, UpdateProductCommand command); }`.

Reglas (D7): con `ExternalSource is null` no hace nada. Si no, junta en un `Dictionary<string,string[]>` cada campo que cambia y
lanza `CatalogDomainException(Code, …, fieldErrors)` si hay alguno: `Name` (comparado recortado, ordinal), `OptionIds` (lista
distinta), `DeletedVariantIds` (no vacía), `Variants` (una variante sin `Id`, o una guardada que falta), y por fila
`Variants[i].Code`, `Variants[i].Barcode` (normalizados con `ProductVariant.NormalizeCode/NormalizeBarcode`),
`Variants[i].IsActive` (distinto del guardado; `null` no cuenta) y `Variants[i].Pricing.Prices` (sólo si `Pricing` viene y los
precios difieren; `null` hereda y no cuenta). Mensaje de cada campo en español: «Este dato lo administra Shopify.»

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] void AnOwnProductAcceptsAnyChange()
[Fact] void RenamingAnImportedProductPointsAtName()          // errors.Keys == ["Name"]
[Fact] void ChangingAPriceOfAnImportedVariantPointsAtTheRow() // errors.Keys == ["Variants[1].Pricing.Prices"]
[Fact] void ScalesCostsAndTaxRateStayEditable()               // cambia TaxRateId, Scales, Costs, CategoryId, Description → no lanza
[Fact] void AddingOrDeletingAVariantIsRejected()             // Id null → "Variants"; DeletedVariantIds [x] → "DeletedVariantIds"
// integración
[Fact] Task PutRenamingAnImportedProductIs422ManagedExternally() // 422, code == "catalog.product.managed_externally", errors.Name
[Fact] Task PutChangingTheTaxRateOfAnImportedProductIs200()
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0103` por `ExternallyManagedProducts`). Copia la salida.
- [ ] **Step 3: Implementa** y llama `ExternallyManagedProducts.EnsureAllowed(product, command)` en el handler.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(catalog): reject edits of fields managed by shopify`.

### Task 3: Camino de sistema de Catalog (`IExternalProductWriter`)

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/IExternalProductWriter.cs` (contratos) y `ExternalProductWriter.cs`.
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/Product.cs` (`ReplaceGalleryImage`).
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs` (si hace falta una lectura de tasas, ver abajo)
  y el registro de Catalog (`CatalogApplicationExtensions` o equivalente: `AddScoped<IExternalProductWriter, ExternalProductWriter>()`).
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductTests.cs` (galería), `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ExternalProductWriterTests.cs` (host propio por clase, resuelve el servicio del contenedor dentro de un scope).

**Interfaces (Produces):**

```csharp
public sealed record ExternalOptionDraft(string Name, IReadOnlyList<string> Values);
// Key: el id de la variante de Shopify como texto; vuelve en el resultado para mapear.
public sealed record ExternalVariantDraft(string Key, string Code, string? Barcode, IReadOnlyList<string> OptionValues, decimal Price);
public sealed record ExternalProductDraft(
    Guid TenantId, string Source, string Name, bool IsActive, Guid? TaxRateId, string Currency,
    IReadOnlyList<ExternalOptionDraft> Options, IReadOnlyList<ExternalVariantDraft> Variants, Guid ActorId, DateTimeOffset OccurredAt);
public sealed record ExternalProductResult(
    Guid ProductId, long Version, IReadOnlyDictionary<string, Guid> VariantIdsByKey, IReadOnlyList<string> DroppedBarcodeKeys, bool Changed);
public sealed record ExternalDefaultTaxRates(Guid? Vat19Id, Guid? ExemptId);

public interface IExternalProductWriter
{
    Task<ExternalProductResult> CreateAsync(ExternalProductDraft draft, CancellationToken ct);
    // existing: variante guardada por Key (la que ya vinculó Shopify). Las guardadas que no están en existing y tampoco en el
    // borrador se borran si nadie las usa y se desactivan si alguna línea sí (IVariantUsageChecker), como el PUT.
    Task<ExternalProductResult> UpdateAsync(Guid productId, ExternalProductDraft draft, IReadOnlyDictionary<string, Guid> existing, CancellationToken ct);
    Task<bool> SetActiveAsync(Guid tenantId, Guid productId, bool active, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct);
    Task ReleaseAsync(Guid tenantId, Guid productId, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct);
    Task<bool> ReplaceImageAsync(Guid tenantId, Guid productId, Guid? previousFileId, Guid newFileId, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct);
    Task<ExternalDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken ct);
}
```

Reglas:
- **Sin `IExecutionContext` y sin transacción propia**: el llamador puede tenerla abierta (enrolada); nunca llama
  `BeginTransactionAsync`. Guarda con `ICatalogUnitOfWork.SaveChangesAsync`. Las reglas de dominio salen como
  `CatalogDomainException` (el adaptador las traduce).
- **Opciones por nombre** (§5.3): compara recortado y `OrdinalIgnoreCase` contra la biblioteca (`IOptionDefinitionRepository.ListAsync`);
  si no existe la crea activa (`OptionDefinition.Create(..., isActive: true, ...)`), si está inactiva `SetActive(true)`; cada valor
  igual (`AddValue`, `SetValueActive`). Un borrador sin opciones (o con una sola variante y la opción `Title`/`Default Title`, que
  ya descarta el mapeo de Shopify) crea con el `Create` sin opciones.
- **Códigos y barras**: `ProductVariantReferences.EnsureCodesAvailableAsync` como el `POST`. Un código de barras que ya existe en
  el tenant (`FindExistingBarcodesAsync`) se omite y su `Key` vuelve en `DroppedBarcodeKeys`.
- **Precio**: crear → `Pricing.Prices = { [Currency] = Price }`. Actualizar → por variante guardada conserva escalas, empaques,
  costos y precios de otras monedas, y reemplaza sólo `Prices[Currency]`.
- **Historial de precios y auditoría**: `ProductPriceChangeDetector.Detect(product, variants, draft.ActorId, now)` y
  `ICatalogAuditPublisher.Publish(tenant, ActorId, "catalog.product.created"|"catalog.product.updated", …)` (P3: `Guid.Empty` es el
  sistema, precedente `ShopifyCityCorrectionJob.cs:61`).
- **Sin cambios no se guarda** (Review Focus 2): `UpdateAsync` compara nombre, opciones, variantes (código, barras, valores,
  precio de la moneda, activa) y estado; si nada cambia devuelve `Changed = false` sin tocar `Version`.
- **Inactivo** (D8): `Activate` → `Update` → `Deactivate` en un guardado si el estado final es inactivo.
- **`ReplaceGalleryImage`** (dominio): si `previousFileId` está en la galería, el nuevo toma su posición; si es `null` y la
  galería está vacía, entra primero; en cualquier otro caso no toca nada y devuelve `false`. `ReplaceImageAsync` publica
  `IProductImageReleasePublisher` con el archivo reemplazado, o con el **nuevo** si no entró (si no, queda un archivo `Available`
  sin dueño para siempre).
- **Tasas por defecto** (D4): sobre `ITaxRateRepository.ListAsync` del tenant, activas, `Type == TaxCountries.IvaType`,
  `MinimumBase is null && CityId is null`, la más antigua por `CreatedAt` y luego `Id`.

- [ ] **Step 1: Pruebas**

```csharp
// ProductTests
[Fact] void ReplacingTheImportedImageKeepsItsPosition()      // galería [a, b], previous a → [n, b]
[Fact] void TheImportedImageRemovedByTheAdvisorIsNotReplaced() // galería [b], previous a → false, galería [b]
// ExternalProductWriterTests
[Fact] Task CreatesAProductWithOptionsFoundByNameIgnoringCase() // biblioteca "Talla" inactiva, borrador " talla " → misma opción, activa
[Fact] Task CreatesMissingOptionValues()                     // "Color" sin "Rojo" → valor nuevo
[Fact] Task ABarcodeTakenInTheTenantIsDroppedAndReported()   // DroppedBarcodeKeys == ["2"], variante sin barcode
[Fact] Task AnUpdateKeepsScalesPackagingCostsAndOtherCurrencies()
[Fact] Task AnUpdateWithoutChangesDoesNotBumpTheVersion()    // Changed false, Version igual
[Fact] Task AnUpdateOfAnInactiveProductLeavesItInactive()
[Fact] Task AVariantMissingFromTheDraftIsDeletedOrDeactivatedByUsage()
[Fact] Task ReplacingAnImageThatDidNotEnterReleasesTheNewFile() // outbox con catalog.product.images-released.v1 y el fileId nuevo
[Fact] Task DefaultRatesPickTheOldestActive19AndExempt()
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246` por `IExternalProductWriter`). Copia la salida.
- [ ] **Step 3: Implementa** reusando `ProductVariantReferences`, `ProductOptionLibrary`, `ProductTaxRateResolver` y
  `IVariantUsageChecker`. No dupliques las reglas del `PUT`: si una pieza de `UpdateProductHandler` hace falta, extráela a un
  `internal static` compartido.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(catalog): add a system path to write external products`.

### Task 4: Subida de servidor en Storage

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IStorageSystemUploads.cs` y `StorageSystemUploads.cs:37-114`.
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageSystemUploadsTests.cs`,
  `tests/Modules/Storage/Modules.Storage.IntegrationTests/StorageSystemUploadsApiTests.cs`.

**Interfaces:**
- Produces: `Task<SystemStoredFile> StoreAsync(Guid tenantId, FileOwnerType ownerType, Guid ownerId, string name, string mimeType, byte[] content, long maxSizeBytes, CancellationToken ct);`
  con `public sealed record SystemStoredFile(Guid FileId, SystemUploadOutcome Outcome);`.

Reglas: `FileUploadPolicy.ValidateDeclaration(name, mimeType, content.Length, maxSizeBytes)`; rechaza un `ownerType` de
`SystemOwnedFiles` (`ArgumentException`: la subida de servidor es para dueños visibles en la consola); crea el `PendingUpload` como
`OpenAsync`, sube con `IObjectStorage.UploadAsync(StorageKey.StagingFor(tenantId, id), content, mime, ct)` y completa por
`UploadCompletion` con `actorId: null`. **P1 verificado en el código:** el barrido de staging sólo toca `PendingUpload`
(`StagingCleanupProcessor.cs:193`), comprobantes (`:229`) y adjuntos de Soporte (`:105`); un archivo `Product` `Available` no se
barre, y su purga tras una liberación consulta `ProductGalleryFileReferenceProbe`. No hace falta sonda nueva; la prueba lo amarra.

- [ ] **Step 1: Pruebas**

```csharp
[Fact] Task StoringAProductImageLeavesItAvailable()          // StoreAsync(Product, productId, "foto.jpg", "image/jpeg", bytesJpegValidos) → Available
[Fact] Task StoringAnInvalidImageIsRejected()                // bytes en cero → Outcome Rejected (cuarentena)
[Fact] void StoringASystemOwnedTypeThrows()                   // FileOwnerType.SupportTicket → ArgumentException
[Fact] Task TheStagingSweepLeavesAnAvailableProductFile()     // archivo Product Available de hace 3 días → sigue tras el barrido
```

Las imágenes de prueba deben decodificar (ImageSharp pone en cuarentena los bytes en cero).

- [ ] **Step 2: RED** — `dotnet build` (`CS1061` por `StoreAsync`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(storage): store server-side uploads for visible owners`.

**Fin del Bloque A:** una revisión del bloque.

---

## Bloque B — Núcleo de Shopify

### Task 5: Persistencia (vínculos, importaciones, tienda) y timbre

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyProductLink.cs`, `ShopifyProductImport.cs` (con su issue),
  `ShopifyProductCodes.cs`.
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyStore.cs` (`ProductsSyncedAt`), `ShopifyDomainException.cs`
  (`ShopifyErrorCodes`).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/IShopifyProductRepositories.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyProductRepositories.cs`.
- Modify: `ShopifyDbContext.cs` (tres `Configure…` nuevos y la columna de `stores`), `ShopifyUnitOfWork.cs` (traducciones y
  `LockProductAsync`), `src/Modules/Shopify/Modules.Shopify.Application/IShopifyUnitOfWork.cs`, `ShopifyInfrastructureExtensions.cs`.
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/QueueDoorbell.cs` (`DoorbellQueues.ShopifyProducts = "shopify_products"`,
  en `All`, `SafetyNet` 30 s) y `src/BuildingBlocks/BuildingBlocks.Infrastructure/QueueDoorbellInterceptor.cs:27-34`
  (`["shopify.product_imports"] = DoorbellQueues.ShopifyProducts`).
- Create: `…/Persistence/Migrations/<ts>_AddProductSync.cs` (+ Designer, snapshot).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductImportTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyPersistenceTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/Valkey/QueueDoorbellInterceptorTests.cs`.

**Interfaces (Produces):**
- `enum ShopifyProductLinkOrigin { Imported, MatchedBySku }` (texto en base).
- `ShopifyProductLink`: `Id`, `TenantId`, `ConnectionId`, `long ShopifyProductId`, `long ShopifyVariantId`, `Guid ProductId`,
  `Guid VariantId`, `Origin`, `DateTimeOffset? ShopifyUpdatedAt`, `long? ShopifyImageId`, `Guid? ImageFileId`, `CreatedAt`,
  `UpdatedAt`; `static Create(tenantId, connectionId, shopifyProductId, shopifyVariantId, productId, variantId, origin, shopifyUpdatedAt, now)`,
  `RecordSync(DateTimeOffset shopifyUpdatedAt, DateTimeOffset now)`, `RecordImage(long? shopifyImageId, Guid? fileId, DateTimeOffset now)`.
- `enum ShopifyProductImportMode { All, Missing }`, `enum ShopifyProductImportStatus { Pending, Running, Done, Failed }`.
- `ShopifyProductImport`: columnas de §5.2 más `Cursor` y `RequestedBySubjectId` (D2); `static Request(tenantId, connectionId, mode, Guid requestedBy, Guid requestedBySubjectId, now)`;
  `Start(int total)`; `RecordPage(ShopifyProductTally tally, string? nextCursor)`; `Complete(DateTimeOffset now)`;
  `Fail(string code, DateTimeOffset now)`. `record ShopifyProductTally(int Products, int Created, int Matched, int WithoutSku, int Skipped)`.
- `ShopifyProductImportIssue(Guid Id, Guid ImportId, long ShopifyProductId, string Title, string Code, DateTimeOffset CreatedAt)`;
  `Title` recortado con `SnapshotText.Fit` a 255, `Code` hasta 64.
- `ShopifyProductCodes`: issues `partial_match`, `sku_in_other_product`, `barcode_taken`, `image_failed`, `tax_rate_missing`,
  `mixed_taxable`, `concurrent_write`; fallas `token_expired`, `scope_missing`, `unreachable`, `currency_unsupported`,
  `company_required`. (Un rechazo de Catalog guarda el código de Catalog tal cual.)
- `ShopifyErrorCodes.ProductsImportRunning`, `ProductsScopeMissing`, `ProductsCompanyRequired`, `ProductsLinkNotFound` (los de
  Global Constraints).
- `ShopifyStore.ProductsSyncedAt` (`DateTimeOffset?`) y `MarkProductsSynced(DateTimeOffset now)` (no sube `Version`: no es
  configuración del asesor).
- Repositorios: `IShopifyProductLinkRepository` (`FindByShopifyProductAsync(connectionId, shopifyProductId)`,
  `FindByShopifyVariantsAsync(tenantId, connectionId, IReadOnlyCollection<long>)` → `IReadOnlyDictionary<long, ShopifyProductLink>`,
  `FindByProductAsync(tenantId, connectionId, productId)`, `FindByVariantsAsync(connectionId, IReadOnlyCollection<Guid>)`,
  `ListLinkedShopifyVariantIdsAsync(connectionId)` → `IReadOnlySet<long>`, `Add`, `Remove`) e
  `IShopifyProductImportRepository` (`FindAsync(tenantId, id)`, `FindLatestAsync(tenantId, connectionId)`, `Add`,
  `AddIssue`, `ListIssuesAsync(importId, int limit)`).
- `IShopifyUnitOfWork.LockProductAsync(Guid connectionId, long shopifyProductId, CancellationToken ct)` con
  `ShopifyProductLock.Namespace = 20261118` (exige transacción abierta, como `LockCitySynonymAsync`).

Índices (nombres exactos, constantes en `ShopifyDbContext`):
`IX_product_links_connection_shopify_variant` único `(connection_id, shopify_variant_id)`;
`IX_product_links_connection_variant` único `(connection_id, variant_id)`;
`IX_product_links_connection_shopify_product` `(connection_id, shopify_product_id)`;
`IX_product_links_connection_product` `(connection_id, product_id)`;
`IX_product_imports_connection_live` único parcial `(connection_id)` con `status IN ('Pending','Running')`;
`IX_product_imports_status_lease` `(status, lease_until)`; `IX_product_imports_connection_created` `(connection_id, created_at)`;
`IX_product_import_issues_import` `(import_id, created_at)`. FK de issue a import con `ON DELETE CASCADE`; sin FK a Catalog
(referencia blanda, como `custom_line_mappings`).

Traducción en `ShopifyUnitOfWork`: los dos únicos de `product_links` → `RequestConcurrencyException("concurrency.conflict", …)`
(el perdedor relee); `IX_product_imports_connection_live` → `ResourceConflictException(ShopifyErrorCodes.ProductsImportRunning, …)`.

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] void ARequestedImportIsPendingWithZeroCounters()
[Fact] void RecordPageAddsTheTallyAndKeepsTheCursor()        // done += Products, cursor = next
[Fact] void FailKeepsTheCountersAndSetsTheCode()             // Fail("unreachable") → Failed, FinishedAt
// integración (ShopifyPersistenceTests)
[Fact] Task ASecondLiveImportOfTheStoreIs409ImportRunning()  // dos Pending misma conexión → ResourceConflictException code import_running
[Fact] Task ADoneImportDoesNotBlockANewOne()
[Fact] Task TheSameShopifyVariantLinkedTwiceIsAConcurrencyConflict()
[Fact] Task ProductsSyncedAtRoundTrips()
// QueueDoorbellInterceptorTests
[InlineData("shopify.product_imports", DoorbellQueues.ShopifyProducts)]
```

- [ ] **Step 2: RED** — `dotnet build` (`CS0246`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Migración** — `dotnet ef migrations add AddProductSync --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations`. Espera tres `CreateTable` y un `AddColumn` nullable (`stores.products_synced_at`). Cabecera: «aditiva».
- [ ] **Step 5: Build** → `0`. **Commit** — `feat(shopify): persist product links and product imports`.

### Task 6: Emparejar líneas por id de variante

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderSnapshot.cs:68` (`ShopifyLine` gana `long? VariantId = null`, al final).
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/RestOrderParser.cs:78` (`variant_id`) y
  `GraphQlOrderParser.cs:85` (`variant.id`, GID `ProductVariant`; `LineFields` ya pide `variant { id }`, `ShopifyGraphQlClient.cs:43`).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyLineResolver.cs:18-96`.
- Modify: los dobles que construyen el resolver (`tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyImporterTestDoubles.cs`, `ShopifyEditTestWorld.cs`).
- Test: `OrderParsersTests.cs`, `ShopifyCustomLineImportTests.cs` (o `ShopifyLineResolverTests.cs` nuevo), `ShopifyEditPlannerTests.cs`.

**Interfaces:**
- Consumes: `IShopifyProductLinkRepository.FindByShopifyVariantsAsync` (Tarea 5).
- Produces: `ShopifyLineResolver(IShopifyProductLookup products, IShopifyCustomLineMappingRepository customLines, IShopifyProductLinkRepository links)`;
  la firma de `ResolveAsync` no cambia, así `ShopifyOrderImporter.cs:139-143` y `ShopifyEditPlanner.cs:280-283` no se tocan.

Regla (§4): para una línea que no es personalizada y trae `VariantId`, primero el vínculo `(connectionId, VariantId)` →
`FindByVariantIdsAsync`; con vínculo, la variante de QEP decide (inactiva → `product_unmatched` con su SKU, **sin** caer al SKU);
sin vínculo, el SKU como hoy. Una consulta de vínculos por pedido (todas las líneas juntas).

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void RestLinesCarryTheVariantId()                     // fixture orders/create: VariantId == variant_id del fixture
[Fact] void GraphQlLinesCarryTheVariantIdFromTheGid()        // "gid://shopify/ProductVariant/42" → 42
[Fact] void AnOldPayloadWithoutVariantIdStillDeserializes()  // JSON guardado sin la llave → VariantId null
[Fact] Task ALinkedVariantWinsOverTheSku()                   // SKU casa con A, vínculo apunta a B → línea con B
[Fact] Task ALinkedButInactiveVariantIsUnmatchedWithoutFallingBackToTheSku()
[Fact] Task ALineWithoutSkuMatchesByItsLink()                // Sku null, vínculo → línea creada
[Fact] Task TheEditPlannerUsesTheLinkToo()                   // ShopifyEditPlanner con vínculo: sin ProductUnmatched
```

- [ ] **Step 2: RED** — `dotnet build` (`CS7036` por el constructor). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): match order lines by linked variant before sku`.

### Task 7: Cliente GraphQL de productos

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyGraphQlTransport.cs` (extraído de
  `ShopifyGraphQlClient.cs:227-351`: `SendAsync`, `ReadCost`, `PaceAsync`, `Host`, clasificación).
- Modify: `ShopifyGraphQlClient.cs` (usa el transporte; su comportamiento no cambia: `AccessDenied` → `Unavailable`).
- Create: `…/Admin/ShopifyProductsClient.cs`, `…/Parsing/GraphQlProductParser.cs`, `…/Parsing/ShopifyProductPayloadParser.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Application/IShopifyProductsSource.cs` y `ShopifyProductSnapshot.cs`.
- Modify: `ShopifyInfrastructureExtensions.cs` (transporte y cliente scoped).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyGraphQlClientTests.cs` (sin cambios, tiene que seguir compilando),
  `ShopifyProductsClientTests.cs`, `ProductParsersTests.cs`, fixtures en `Fixtures/` (`products-page.json`, `product-many-variants.json`).

**Interfaces (Produces):**

```csharp
public enum ShopifyProductStatus { Active, Draft, Archived }
public sealed record ShopifyProductOption(string Name, IReadOnlyList<string> Values);
public sealed record ShopifyVariantSnapshot(long Id, string? Sku, string? Barcode, decimal Price, bool Taxable, IReadOnlyList<string> OptionValues);
public sealed record ShopifyProductImage(long Id, Uri Url);
public sealed record ShopifyProductSnapshot(long Id, string Title, ShopifyProductStatus Status, DateTimeOffset UpdatedAt,
    IReadOnlyList<ShopifyProductOption> Options, IReadOnlyList<ShopifyVariantSnapshot> Variants, ShopifyProductImage? FeaturedImage);
public sealed record ShopifyShopInfo(string CurrencyCode, bool TaxesIncluded, int ProductsCount);
public sealed record ShopifyProductsPage(IReadOnlyList<ShopifyProductSnapshot> Products, string? NextCursor);
public sealed record ShopifyVariantKey(long VariantId, string? Sku, string ProductTitle);
public sealed record ShopifyVariantKeysPage(IReadOnlyList<ShopifyVariantKey> Variants, string? NextCursor);
public enum ShopifyProductsSourceFailure { Throttled, Rejected, AccessDenied, Unavailable }
public sealed class ShopifyProductsSourceException(ShopifyProductsSourceFailure failure, TimeSpan? retryAfter, string? detail = null) : Exception;
public interface IShopifyProductsSource
{
    Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken ct);
    Task<ShopifyProductsPage> FetchProductsAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken ct);
    Task<ShopifyProductSnapshot?> ReadProductAsync(ShopifyConnectionRef connection, long productId, CancellationToken ct);
    Task<ShopifyVariantKeysPage> ListVariantKeysAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken ct);
}
// Del webhook REST sólo lo que decide el orden (D1). updated_at ausente → null.
public sealed record ShopifyProductWebhookRef(long Id, DateTimeOffset? UpdatedAt);
public interface IShopifyProductPayloadParser { ShopifyProductWebhookRef ParseRef(string payload); }
```

Consultas (sin fragmentos ni alias; filtro `"status:active OR status:draft"`):
- `ShopQuery`: `shop { currencyCode taxesIncluded } productsCount(query: $query, limit: null) { count }`.
- `ProductsQuery`: `products(first: 10, after: $after, query: $query, sortKey: ID) { pageInfo { hasNextPage endCursor } nodes { ProductFields } }`.
- `ProductFields`: `id title status updatedAt options { name optionValues { name } } featuredMedia { id preview { image { url(transform: { preferredContentType: JPG, maxWidth: 2048, maxHeight: 2048 }) } } } variants(first: 50) { pageInfo { hasNextPage endCursor } nodes { id sku barcode price taxable selectedOptions { name value } } }`.
- `ProductQuery($id)` y `ProductVariantsQuery($id, $after)` con `variants(first: 50, after: $after)`: las variantes que no caben se
  unen antes de parsear, con la misma guarda de cursor que `AppendPagesAsync` (cursor repetido → `Unavailable`).
- `VariantKeysQuery`: `productVariants(first: 250, after: $after, query: "product_status:active OR product_status:draft") { pageInfo { … } nodes { id sku product { title } } }`.

Clasificación del transporte: 429/`THROTTLED` → `Throttled`; 401/402 → `Rejected`; 403 o `ACCESS_DENIED` → `AccessDenied` (el
cliente de pedidos lo sigue mandando a `Unavailable`); resto → `Unavailable`. Nunca registra token, cuerpo ni mensaje de red.
Mapeo de `selectedOptions` a `OptionValues` en el orden de `options`. `price` es texto decimal invariante.

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void TheProductQueryAsksForEveryFieldTheParserReads()   // contrato: cada campo leído aparece en ProductFields
[Fact] void ParsesAProductWithOptionsInOptionOrder()          // selectedOptions desordenadas → OptionValues en el orden de options
[Fact] void ParsesTheFeaturedImageIdFromTheMediaGid()         // gid://shopify/MediaImage/9 → 9
[Fact] Task AProductWithMoreThan50VariantsIsReadWhole()       // 2 páginas de variantes → 70
[Fact] Task ARepeatedVariantsCursorIsUnavailable()
[Fact] Task AccessDeniedIsItsOwnFailure()                     // errors[0].extensions.code ACCESS_DENIED → AccessDenied
[Fact] Task UnauthorizedIsRejected()                          // 401 → Rejected
[Fact] void TheWebhookRefReadsIdAndUpdatedAt()
[Fact] Task TheOrdersClientStillMapsAccessDeniedToUnavailable() // en ShopifyGraphQlClientTests
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.** La extracción del transporte es mecánica: `ShopifyGraphQlClientTests` no se edita salvo la prueba nueva.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): read products from the admin graphql api`.

### Task 8: Puertos y adaptadores de Bootstrapper

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs` (al final; `ShopifyConnectionRef` `:36-46`;
  `IShopifyProductLookup` `:223-234`).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyScopes.cs`.
- Create: `src/Bootstrapper/ShopifyCatalogWriter.cs`, `ShopifyProductImageStore.cs`, `ShopifyReauthorization.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyImageFetcher.cs` (HttpClient `shopify.cdn`).
- Modify: `src/Bootstrapper/ShopifyConnectionDirectory.cs:96` (llena `Scopes`), `ShopifyProductLookup.cs` (método nuevo),
  `QepServiceCollectionExtensions.cs` (registros, junto a los demás `Shopify*`).
- Test: `tests/ArchitectureTests/ArchitectureTests/ShopifyLayerTests.cs`, `CompositionRootTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyProductScopeParityTests.cs` (en la Tarea 13, cuando exista la copia de
  Integrations), `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyCatalogWriterTests.cs`.

**Interfaces (Produces):**

```csharp
// ShopifyConnectionRef: los scopes concedidos (ShopifyFieldKeys.Scopes, separados por coma). Vacío si el adaptador no los sabe.
public IReadOnlyCollection<string> Scopes { get; init; } = [];
// ShopifyScopes: write_x cuenta como read_x (misma regla que EnsureScopes, CompleteShopifyInstall.cs:244-257).
public static class ShopifyScopes { public const string ReadProducts = "read_products"; public static bool CanReadProducts(IEnumerable<string> granted); }

// IShopifyProductLookup gana: los códigos de esa lista que ya existen en el tenant (IProductRepository.FindExistingCodesAsync).
Task<IReadOnlySet<string>> FindExistingCodesAsync(Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct);
// y: productos activos del tenant (IProductRepository.CountActiveAsync), para catalogEmpty de la cobertura.
Task<int> CountActiveAsync(Guid tenantId, CancellationToken ct);

public sealed record ShopifyCatalogOption(string Name, IReadOnlyList<string> Values);
public sealed record ShopifyCatalogVariant(long ShopifyVariantId, string Code, string? Barcode, IReadOnlyList<string> OptionValues, decimal Price);
public sealed record ShopifyCatalogProduct(Guid TenantId, string Name, bool IsActive, Guid? TaxRateId, string Currency,
    IReadOnlyList<ShopifyCatalogOption> Options, IReadOnlyList<ShopifyCatalogVariant> Variants, Guid ActorId, DateTimeOffset OccurredAt);
// RejectedCode: el código de la regla de Catalog que lo rechazó (P7). Con él, ProductId es Guid.Empty y la transacción ya no sirve.
public sealed record ShopifyCatalogWrite(Guid ProductId, IReadOnlyDictionary<long, Guid> VariantIds, IReadOnlyList<long> DroppedBarcodes, bool Changed, string? RejectedCode);
public sealed record ShopifyDefaultTaxRates(Guid? Vat19Id, Guid? ExemptId);

/// Escribe en Catalog DENTRO de la transacción abierta de Shopify (sin ella lanza InvalidOperationException): cada llamada abre
/// un scope hijo, enrola su CatalogDbContext (SetDbConnection + UseTransactionAsync, precedente ShopifyOrderCorrectionWriter) y
/// llama IExternalProductWriter. Traduce CatalogDomainException a RejectedCode; el resto se propaga.
public interface IShopifyCatalogWriter
{
    Task<ShopifyCatalogWrite> CreateAsync(ShopifyCatalogProduct product, CancellationToken ct);
    Task<ShopifyCatalogWrite> UpdateAsync(Guid productId, ShopifyCatalogProduct product, IReadOnlyDictionary<long, Guid> existing, CancellationToken ct);
    Task SetActiveAsync(Guid tenantId, Guid productId, bool active, Guid actorId, CancellationToken ct);
    Task ReleaseAsync(Guid tenantId, Guid productId, Guid actorId, CancellationToken ct);
    Task<ShopifyDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken ct);   // sin transacción
}

public sealed record ShopifyFetchedImage(byte[] Content, string ContentType);
/// Sólo https://cdn.shopify.com, sin redirecciones, 10 s, tope 10 MiB (lee hasta tope + 1 y corta); image/jpeg|png|webp.
/// null con cualquier falla (se anota image_failed); nunca registra la URL con su query.
public interface IShopifyImageFetcher { Task<ShopifyFetchedImage?> FetchAsync(Uri url, CancellationToken ct); }

public sealed record ShopifyImageAttach(Guid? FileId, string? FailureCode);
/// Scope propio por llamada: StoreAsync de Storage (FileOwnerType.Product, ownerId = productId) y luego
/// IExternalProductWriter.ReplaceImageAsync. Un concurrency.conflict reintenta una vez.
public interface IShopifyProductImageStore
{
    Task<ShopifyImageAttach> AttachAsync(Guid tenantId, Guid productId, ShopifyFetchedImage image, Guid? previousFileId, Guid actorId, CancellationToken ct);
}

/// D6: StartShopifyInstallForTenantCommand con el dominio de la conexión; corre en el request (mismo permiso).
public interface IShopifyReauthorization { Task<string> CreateUrlAsync(Guid tenantId, string shopDomain, string redirectUri, CancellationToken ct); }

/// P8: qep.shopify.product_sync.failures {reason} → qep_shopify_product_sync_failures_total. Meter "Qep.Shopify", sin tenant.
public interface IShopifyProductSyncMetrics { void RecordFailure(string reason); }
```

`ShopifyMetrics` (Infrastructure, singleton, `IMeterFactory`) implementa la métrica, como `MessagingMetrics`.

- [ ] **Step 1: Pruebas**

```csharp
// ShopifyLayerTests: ya cubre que Application no vea Catalog/Storage/Integrations; suma
[Fact] void TheCatalogWriterAndImageStoreLiveInBootstrapper()   // implementaciones de IShopifyCatalogWriter/IShopifyProductImageStore en el assembly de Bootstrapper
// CompositionRootTests
[Fact] void TheProductPortsAreRegistered()                       // resuelve IShopifyCatalogWriter, IShopifyProductImageStore, IShopifyReauthorization, IShopifyProductsSource
// ShopifyCatalogWriterTests (integración, host propio)
[Fact] Task WithoutAnOpenShopifyTransactionItThrows()
[Fact] Task ARolledBackShopifyTransactionLeavesNoProduct()       // Create dentro de tx, sin commit → catalog.products sin la fila
[Fact] Task ACatalogRuleComesBackAsRejectedCode()                // nombre de 201 → RejectedCode "catalog.product.name_too_long"
[Fact] void ScopesWithWriteProductsCanReadProducts()             // unidad de ShopifyScopes
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.** `ShopifyCatalogWriter` necesita `ShopifyDbContext` (como `ShopifyOrderCorrectionWriter`) e
  `IServiceScopeFactory`; después de cada llamada el scope hijo se dispone y nadie más usa ese `CatalogDbContext`.
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): add catalog, image and reauthorization ports`.

### Task 9: Mapeo y emparejamiento (puro)

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductMapper.cs` y `ShopifyProductMatcher.cs`.
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductMapperTests.cs`, `ShopifyProductMatcherTests.cs`.

**Interfaces (Produces):**

```csharp
public sealed record ShopifyPricingContext(string Currency, bool TaxesIncluded, bool CompanyChargesVat, PriceRounding Rounding, ShopifyDefaultTaxRates Rates);
public sealed record ShopifyMappedProduct(ShopifyCatalogProduct Product, int WithoutSku, IReadOnlyList<string> IssueCodes);
public static class ShopifyProductMapper
{
    public const string NoSkuPrefix = "shp-";
    public static ShopifyMappedProduct Map(ShopifyProductSnapshot product, ShopifyPricingContext pricing, Guid tenantId, Guid actorId, DateTimeOffset now);
    public static string CodeOf(ShopifyVariantSnapshot variant);   // SKU recortado, o "shp-{id}"
}
public enum ShopifyMatchKind { None, All, Partial, Relink }
// QepProductId: el producto de QEP elegido (P6: el de más variantes casadas; empate, el de la primera en orden de Shopify).
public sealed record ShopifyMatch(ShopifyMatchKind Kind, Guid? QepProductId, IReadOnlyDictionary<long, Guid> Matched,
    IReadOnlyList<long> PartialMatch, IReadOnlyList<long> InOtherProduct);
public static class ShopifyProductMatcher
{
    // byCode: FindByCodesAsync de los SKU; importedProducts: productos de QEP con vínculo Imported en esta conexión (P9).
    public static ShopifyMatch Match(ShopifyProductSnapshot product, IReadOnlyDictionary<string, ShopifyProductRef> byCode, IReadOnlySet<Guid> importedProducts);
}
```

Reglas de mapeo (§5.3, D4, D5): estado `Active` → activo, `Draft` → inactivo (`Archived` lo filtra quien llama); una variante
con una sola opción `Title` y valor `Default Title` → sin opciones; nombres de opción y valores recortados; `Code` = SKU
recortado o `shp-{id}` (suma `WithoutSku`); barras recortado o `null`; tasa: alguna `taxable` → `Vat19Id`, si no `ExemptId`;
mezcla → issue `mixed_taxable`; tasa elegida `null` → issue `tax_rate_missing`; precio
`TaxesIncluded || !CompanyChargesVat || !ShopifyPricing.IsCop(Currency) || tasa null ? price : Rounding.Apply(price * (100 + pct) / 100m)`
con `pct` = 19 o 0.

Reglas de emparejamiento (§5.3, P6, P9): una variante casa si su SKU recortado es el `Code` de una variante de QEP (las sin SKU
nunca casan por SKU). `Relink` si el producto elegido está en `importedProducts` (P9); `All` si todas casan en el elegido;
`Partial` si casan algunas (las demás en `PartialMatch`); las que casan en **otro** producto van a `InOtherProduct` (P6) y nunca
se vinculan; `None` si ninguna casa.

- [ ] **Step 1: Pruebas** (todas unidad)

```csharp
[Fact] void ASingleDefaultTitleVariantHasNoOptions()
[Fact] void OptionsKeepShopifyOrderAndTrimmedNames()
[Fact] void AVariantWithoutSkuGetsShpCodeAndCounts()          // Id 4455 → "shp-4455", WithoutSku 1
[Fact] void ATaxableProductTakesThe19Rate()
[Fact] void ANonTaxableProductTakesExempt()
[Fact] void MixedTaxableTakes19AndIsReported()                // IssueCodes ["mixed_taxable"]
[Fact] void TaxesExcludedAddsVatForACompanyWithVatInCop()      // 10000, 19 %, COP, regla 0 decimales → 11900
[Fact] void TaxesExcludedDoesNotAddVatForACompanyWithoutVat()  // → 10000
[Fact] void TaxesExcludedDoesNotAddVatOutsideCop()             // USD → precio tal cual
[Fact] void WithoutRatesTheProductHasNoRateAndIsReported()     // IssueCodes ["tax_rate_missing"], precio tal cual
[Fact] void DraftMapsToInactive()
[Fact] void AllVariantsMatchingOneProductIsAll()
[Fact] void SomeVariantsMatchingIsPartial()                    // PartialMatch con las demás
[Fact] void AVariantOfAnotherQepProductIsNeverLinked()         // P6: InOtherProduct
[Fact] void TheProductWithMoreMatchesIsChosen()
[Fact] void AMatchOnAnImportedProductOfTheConnectionIsRelink() // P9
[Fact] void VariantsWithoutSkuNeverMatchBySku()
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): map and match shopify products`.

### Task 10: Sincronizar un producto (`ShopifyProductSync`)

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductSync.cs`.
- Modify: `ShopifyInfrastructureExtensions.cs` (`AddShopifyProductSync`, ver Tarea 11) — aquí sólo el `AddScoped<ShopifyProductSync>()`.
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductSyncTests.cs` (dobles en memoria de puertos y repositorios),
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyProductSyncFlowTests.cs` (host propio por prueba: toca workers).

**Interfaces:**
- Consumes: Tareas 5, 8 y 9.
- Produces:

```csharp
public enum ShopifySyncTrigger { Import, Webhook }
public sealed record ShopifyProductSyncContext(ShopifyConnectionRef Connection, ShopifyPricingContext Pricing, Guid ActorId, ShopifySyncTrigger Trigger);
public enum ShopifyProductOutcomeKind { Created, Updated, Matched, Relinked, Unchanged, Deactivated, Skipped, Ignored }
public sealed record ShopifyProductOutcome(ShopifyProductOutcomeKind Kind, ShopifyProductTally Tally, IReadOnlyList<string> IssueCodes);
public sealed class ShopifyProductSync
{
    Task<ShopifyProductOutcome> ApplyAsync(ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken ct);
    Task<ShopifyProductOutcome> DeleteAsync(ShopifyProductSyncContext context, long shopifyProductId, CancellationToken ct);
}
```

Algoritmo de `ApplyAsync` (uno por producto; la escritura entera en una transacción de Shopify):
1. `BeginTransactionAsync` + `LockProductAsync(connectionId, product.Id)`.
2. Vínculos del producto (`FindByShopifyProductAsync`). Si su producto de QEP ya no existe (`FindByVariantIdsAsync` vacío para
   sus variantes) se borran y se sigue como si no hubiera (Review Focus 4).
3. Con vínculos y `ShopifyUpdatedAt >= product.UpdatedAt` → `Unchanged` (§6, orden; vale también para la importación).
4. Con vínculos `Imported`: `Archived`/`Draft` y estado → `UpdateAsync` con el borrador (estado incluido); variantes nuevas y
   quitadas por `existing`; vínculos nuevos `Imported`, quitados se borran; `RecordSync`. `Changed == false` → `Unchanged`.
5. Con vínculos `MatchedBySku`: sólo las variantes nuevas cuyo SKU casa en **ese** producto se vinculan (`Matched`); nunca se
   escribe el producto (S-2).
6. Sin vínculos: `Archived` → `Ignored`. Si no, `ShopifyProductMatcher.Match`: `All`/`Partial` → vínculos `MatchedBySku`
   (`Partial` suma las demás a `skipped` con `partial_match`); `Relink` (P9) → borra los vínculos viejos de ese producto de QEP,
   vincula `Imported`, `SetActiveAsync(true)` y sigue por el paso 4; `None` → `CreateAsync` con `Product.ShopifySource` y vínculos
   `Imported`. `InOtherProduct` suma a `skipped` con `sku_in_other_product`.
7. `RejectedCode` de Catalog → dispone la transacción (rollback), `DiscardPendingChanges()` y devuelve `Skipped` con el código
   y todas las variantes en `skipped` (P7). Si el código es `catalog.option.name_taken` (carrera con otro producto que creó la
   misma opción) reintenta el producto **una** vez; después, `concurrent_write`.
8. Guarda y confirma. Después del commit, fuera de la transacción: si `FeaturedImage?.Id` difiere del `ShopifyImageId`
   guardado (o el producto es nuevo), `IShopifyImageFetcher.FetchAsync` + `IShopifyProductImageStore.AttachAsync(previousFileId:
   ImageFileId guardado)` y `RecordImage` en todos los vínculos del producto (otro guardado). Falla → issue `image_failed`.
- `DeleteAsync`: vínculos `Imported` → `SetActiveAsync(false)` (nunca borra; los vínculos quedan, §6); `MatchedBySku` o sin
  vínculos → `Ignored`.
- `DroppedBarcodes` no vacío → issue `barcode_taken` (el producto entra igual).

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] Task ANewProductIsCreatedAndLinkedAsImported()
[Fact] Task AnOlderSnapshotThanTheLinkIsUnchanged()           // UpdatedAt anterior → Unchanged, el escritor no se llama
[Fact] Task AnUpdateWithoutChangesDoesNotTouchTheProduct()    // Changed false → Unchanged (Review Focus 2)
[Fact] Task MatchedBySkuProductsAreNeverWritten()             // vínculos MatchedBySku + nombre distinto → UpdateAsync nunca
[Fact] Task ANewVariantOfAMatchedProductIsLinkedBySku()
[Fact] Task ArchivedWithoutLinksIsIgnored()
[Fact] Task ArchivedWithImportedLinksDeactivates()
[Fact] Task ARecreatedProductIsRelinkedAndReactivated()        // P9
[Fact] Task ACatalogRejectionSkipsTheWholeProduct()            // RejectedCode → Skipped, tally.Skipped == variantes, nada guardado
[Fact] Task AnOptionNameRaceIsRetriedOnce()
[Fact] Task DeleteDeactivatesImportedAndKeepsLinks()
[Fact] Task AFailedImageIsAnIssueAndTheProductStays()
[Fact] Task TheSameImageIsNotDownloadedAgain()
// integración
[Fact] Task AWebhookAndTheImportOfTheSameProductLeaveOneProduct() // dos ApplyAsync en paralelo, scopes distintos → 1 producto, N vínculos
[Fact] Task StaleLinksToADeletedProductAreReplaced()            // DELETE del producto en Catalog → ApplyAsync crea otro y re-vincula
[Fact] Task TheImportedImageLandsInTheGallery()                 // fetcher falso con JPEG válido → gallery[0] = fileId, links.image_file_id
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): sync a shopify product into the catalog`.

**Fin del Bloque B:** una revisión del bloque.

---

## Bloque C — Trabajo, API, webhooks y despliegue

### Task 11: El trabajo de importación y su worker

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductImportProcessor.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyProductImportQueue.cs`,
  `…/Workers/ShopifyProductImportWorker.cs`.
- Modify: `ShopifyInfrastructureExtensions.cs` (`AddShopifyProductSync`: procesador, cola, fetcher, métricas, worker) y su
  llamada en `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después de los adaptadores de la Tarea 8).
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entrada nueva, junto a `:109-110`).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductImportProcessorTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyProductImportFlowTests.cs` (quita el worker y llama
  `DrainAsync`; doble de `IShopifyProductsSource` y de `IShopifyImageFetcher` por `ConfigureTestServices`).

**Interfaces:**
- Produces: `IShopifyProductImportQueue` (`Task<Guid?> TryClaimNextAsync(DateTimeOffset now, CancellationToken ct)` —
  `FOR UPDATE SKIP LOCKED` sobre `status IN ('Pending','Running') AND (lease_until IS NULL OR lease_until < now)`, pone
  `Running`, `lease_until = now + 2 min`, `attempts + 1`, en un `UPDATE` que se confirma solo; `Task RenewAsync(Guid id, DateTimeOffset until, CancellationToken ct)`).
- Produces: `ShopifyProductImportProcessor.ProcessAsync(Guid importId, CancellationToken ct)` y
  `ShopifyProductImportWorker.DrainAsync(CancellationToken ct)` (`internal`, como `ExportJobWorker`). Constantes:
  `ProductPageSize = 10`, `Lease = 2 min`, `MaxAttempts = 4`.

Algoritmo (cada producto en **su propio scope**, porque el `CatalogDbContext` enrolado no sirve tras el commit):
1. Conexión (`FindAsync`): sin token → `Fail("token_expired")`; sin `ShopifyScopes.CanReadProducts` → `scope_missing`. Tienda
   sin `Billing` → `company_required` (P4; la empresa pudo quitarse después del `POST`). Empresa por
   `IShopifyBillingAccounts.FindCompanyAsync` → `ChargesVat`.
2. Primera vez (`Cursor` nulo y `Total == 0`): `ReadShopAsync`; moneda que no está en `Currencies.IsSupported` →
   `Fail("currency_unsupported")` **antes de crear nada** (P2); `Start(ProductsCount)`. Tasas por defecto y redondeo una vez por
   corrida.
3. Página por página desde `Cursor`: en modo `Missing` salta los productos cuyas variantes ya están todas en
   `ListLinkedShopifyVariantIdsAsync`; `Archived` no llega (filtro). Cada producto → `ShopifyProductSync.ApplyAsync(Trigger
   Import, ActorId = RequestedBySubjectId)`; los `IssueCodes` → `AddIssue`. Después de la página: `RecordPage(tally, next)`,
   guardar, `RenewAsync`.
4. Fin: `Complete(now)` y `store.MarkProductsSynced(now)` en el mismo guardado.
5. Fallas de la fuente: `Rejected` → `token_expired`; `AccessDenied` → `scope_missing`; `Throttled`/`Unavailable` → si
   `attempts >= 4`, `unreachable`; si no, `RenewAsync(now + (RetryAfter ?? 30 s × attempts))` y vuelve a la cola sin perder
   `Cursor` ni contadores. Una excepción inesperada sigue la misma escalera.

MultiReplicaInventory: `new("Modules.Shopify.Infrastructure.Workers.ShopifyProductImportWorker", WorkerClassification.Claimed,
"Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyProductImportQueue.cs:<línea> — FOR UPDATE SKIP LOCKED con lease_until de 2 min por importación")`.
El worker espera con `doorbell.Listen(DoorbellQueues.ShopifyProducts)` y `DoorbellPolling.RunAsync` (precedente `ExportJobWorker`).

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] Task AnUnsupportedCurrencyFailsBeforeCreatingAnything()   // "ARS" → Failed currency_unsupported, escritor sin llamadas
[Fact] Task ACompanyRemovedAfterTheRequestFails()                 // store.Billing null → company_required
[Fact] Task RejectedIsTokenExpiredAndAccessDeniedIsScopeMissing()
[Fact] Task ThrottledRetriesKeepingCursorAndCounters()           // attempts 1 → lease = now + RetryAfter, Cursor igual
[Fact] Task TheFourthUnavailableAttemptFailsUnreachable()
[Fact] Task MissingModeSkipsFullyLinkedProducts()
[Fact] Task DoneMarksTheStoreAsProductsSynced()
// integración
[Fact] Task AnImportOfTwoPagesCreatesTheProductsAndCountsThem()  // 12 productos, total 12, done 12, created 12
[Fact] Task ACatalogRejectionRollsBackAndTheImportGoesOn()       // Review Focus 3: nombre de 201 → issue con el código de Catalog, el resto entra
[Fact] Task AnExpiredTokenFailsTheImport()                      // fuente lanza Rejected → Failed token_expired
// ArchitectureTests: la entrada nueva de MultiReplicaInventory la exige MultiReplicaInventoryTests.
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): run product imports in a leased worker`.

### Task 12: La API (BFF)

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductImports.cs` (comandos, consultas, handlers, DTO),
  `ShopifyProductCoverage.cs`, `ShopifyProductLinks.cs`, `ShopifyProductsScopeMissingException.cs`.
- Create: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyProductEndpoints.cs` (y su `Map…` en el registro de endpoints de Shopify).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyAuditActions.cs` (`ProductImportRequested =
  "shopify.products.import_requested"`, `ProductUnlinked = "shopify.products.unlinked"`, tipo de recurso `shopify_product_import`).
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyProductImportHandlersTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyProductsApiTests.cs`.

**Interfaces (contrato de §7):**

```
POST   /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/products/import       integrations.connection.manage
       { "mode": "All" | "Missing", "redirectUri": string? }  → 202 { importId }
GET    …/stores/{connectionId}/products/import                                       integrations.connection.read → 200 dto | null
GET    …/stores/{connectionId}/products/coverage?redirectUri=                         integrations.connection.read
DELETE /api/v1/tenants/{tenantId}/shopify/products/{productId:guid}/link             integrations.connection.manage → 204
```

```csharp
// D3 en el comentario: Total/Done/Created son productos; Matched/WithoutSku/Skipped, variantes.
public sealed record ShopifyProductImportDto(Guid Id, string Mode, string Status, int Total, int Done, int Created, int Matched,
    int WithoutSku, int Skipped, string? FailureCode, IReadOnlyList<ShopifyProductImportIssueDto> Issues);
public sealed record ShopifyProductImportIssueDto(long ShopifyProductId, string Title, string Code);
public sealed record ShopifyProductCoverageDto(int ShopifyVariants, int Matched, bool CatalogEmpty, IReadOnlyList<ShopifyMissingVariantDto> MissingSample);
public sealed record ShopifyMissingVariantDto(string? Sku, string Title);
// 422 shopify.products.scope_missing con extensión reauthorizeUrl (IHasProblemExtensions; null sin redirectUri, D6).
public sealed class ShopifyProductsScopeMissingException(string? reauthorizeUrl) : DomainException, IHasProblemExtensions;
```

Orden del `POST`: `ShopifyAuthorization.EnsureAsync(…, ConnectionManage)` → validador (`mode` en `All|Missing`, ignorando
mayúsculas; `errors["mode"]`) → conexión (`ShopifyStoreSupport.ConnectionAsync`, 403) → no operativa o sin token → 422
`shopify.store.not_operational` → tienda sin `Billing` → 422 `company_required` → sin `read_products` → 422 `scope_missing` con
`IShopifyReauthorization.CreateUrlAsync` si vino `redirectUri` → membresía activa (`FindActiveMembershipIdAsync`, si no 403) →
`Request(...)`, auditoría `Human`, guardar (el índice da el 409; el interceptor timbra la cola) → 202.
Cobertura: mismas guardas de conexión y scope; recorre todas las páginas de `ListVariantKeysAsync`; `matched` = variante con
vínculo o con SKU recortado en `FindExistingCodesAsync` (lotes de 500); `catalogEmpty` = `IShopifyProductLookup.CountActiveAsync == 0` (Tarea 8); `missingSample` las 20 primeras sin par, en el orden de Shopify. No guarda nada.
`DELETE …/link`: vínculos del producto en **cualquier** tienda del tenant (el detalle de Catalog no conoce la conexión; ajuste
del spec por el frontend, H13) (ninguno → 404 `link_not_found`); en una transacción: borra los vínculos y
`IShopifyCatalogWriter.ReleaseAsync`; auditoría `Human`.

- [ ] **Step 1: Pruebas**

```csharp
// unidad
[Fact] Task ImportWithoutCompanyIs422CompanyRequired()
[Fact] Task ImportWithoutReadProductsIs422WithReauthorizeUrl()    // extensión reauthorizeUrl == la URL del doble
[Fact] Task ImportWithoutRedirectUriHasANullReauthorizeUrl()
[Fact] Task WriteProductsCountsAsReadProducts()
[Fact] Task AnInvalidModeIs422ValidationFailed()                  // errors["mode"]
// integración
[Fact] Task PostImportReturns202AndTheGetShowsItPending()
[Fact] Task ASecondPostWhileOneIsLiveIs409ImportRunning()
[Fact] Task GetReturnsNullWithoutImports()
[Fact] Task TheGetReturnsAtMost50Issues()
[Fact] Task CoverageCountsLinkedAndSkuMatchedVariants()           // 3 variantes: 1 vínculo, 1 SKU, 1 sin par → matched 2, missingSample [1]
[Fact] Task UnlinkReleasesTheProductAndWebhooksIgnoreIt()         // 204; external_source null; ApplyAsync después crea uno nuevo
[Fact] Task UnlinkOfAnUnlinkedProductIs404()
[Fact] Task AnotherTenantGets403NotFound()                        // tenant de la ruta ajeno → 403 authorization.denied
```

- [ ] **Step 2: RED** — `dotnet build`. Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): expose product import, coverage and unlink endpoints`.

### Task 13: Webhooks de productos y registro por scopes

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/ShopifyWebhookTopics.cs:11-12` (`Registered` con
  `products/create`, `products/update`, `products/delete`; `ProductTopics`; `ForScopes(IEnumerable<string> granted)`) y
  `ShopifyWebhookRegistration.EnsureAsync` (`:44-81`, parámetro `IReadOnlyCollection<string> grantedScopes` antes del token de
  cancelación; itera `ForScopes`).
- Modify: llamadores `LinkShopifyStore.cs:133` (`install.Scopes`) y
  `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyConnectionTester.cs:68` (`Fields[ShopifyFieldKeys.Scopes]`).
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyWebhookTopics.cs` (constantes `ProductsCreate`,
  `ProductsUpdate`, `ProductsDelete`, `IsProducts`), `ShopifyWebhookDeliveries.cs:45` (`ProductsNotSynced =
  "products_not_synced"`, `ConnectionNotOperational = "connection_not_operational"`, `TokenExpired`, `ScopeMissing`),
  `ShopifyWebhookDeliveryProcessor.cs:39-80` (rama nueva).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyProductWebhooks.cs`.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyWebhookTopicsParityTests.cs` (sin cambios: la parity sigue por
  reflexión), `ShopifyProductScopeParityTests.cs` (nuevo), `tests/Modules/Integrations/Modules.Integrations.UnitTests/ShopifyConnectionTesterTests.cs`,
  `LinkShopifyStoreHandlerTests.cs`, `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyWebhookDeliveryProcessorTests.cs`,
  `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyWebhookWorkerTests.cs`.

**Interfaces:**
- Produces: `public static IReadOnlyList<string> ForScopes(IEnumerable<string> granted)` (Integrations): `Registered` sin los
  de productos si no hay `read_products` ni `write_products` (P10). Copia de la regla de `ShopifyScopes.CanReadProducts`,
  amarradas por `ShopifyProductScopeParityTests`.
- Produces: `ShopifyProductWebhooks.ApplyAsync(ShopifyConnectionRef connection, ShopifyWebhookDelivery delivery, CancellationToken ct) → Task<ShopifyDeliveryResult>`.

Reglas (§6, D1, D9, P8): tienda sin `ProductsSyncedAt` → `Ignored(products_not_synced)`; conexión no operativa →
`Ignored(connection_not_operational)`; `ParseRef`; `delete` → `ShopifyProductSync.DeleteAsync`. `create`/`update`: si el vínculo
guardado tiene `ShopifyUpdatedAt >= ref.UpdatedAt` → `Processed` sin llamar a Shopify; si no, `ReadShopAsync` +
`ReadProductAsync` (null → `Processed`: lo borraron) y `ApplyAsync(Trigger Webhook, ActorId = Guid.Empty)`. Moneda no soportada
o tienda sin empresa → `Processed` con log y métrica (`currency_unsupported`, `company_required`). `Skipped` → log (id de
producto y código, **nunca el payload**) y `RecordFailure(code)`. Fallas de fuente según D9. Un `[LoggerMessage]` por caso
(`CA1873`).

- [ ] **Step 1: Pruebas**

```csharp
[Fact] void ProductTopicsAreSkippedWithoutReadProducts()          // ForScopes(["read_orders"]) sin products/*
[Fact] void WriteProductsRegistersProductTopics()
[Fact] Task TheTesterDoesNotMarkNeedsAttentionWithoutReadProducts() // gateway sin llamadas para products/*
[Fact] void TheScopeRuleMatchesOnBothSides()                      // ShopifyProductScopeParityTests: misma respuesta para 6 casos
[Fact] Task ProductWebhooksAreIgnoredBeforeTheFirstImport()       // products_not_synced
[Fact] Task AnOlderUpdateDoesNotReadShopify()                     // updated_at viejo → Processed, fuente sin llamadas
[Fact] Task ARejectedProductIsLoggedAndCountedWithoutPayload()    // log sin el cuerpo; métrica reason = código de Catalog
[Fact] Task AThrottledReadIsRetried()                             // la entrega lanza → queda para reintento
[Fact] Task DeleteDeactivatesAnImportedProduct()                  // integración por el worker de entregas
```

- [ ] **Step 2: RED** — `dotnet build` (`CS7036` en `EnsureAsync`). Copia la salida.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Build** → `0`. **Commit** — `feat(shopify): sync products from webhooks`.

### Task 14: Scopes, configuración y documentación

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAppOptions.cs:19`
  (`DefaultScopes = "read_orders,write_orders,read_customers,read_products"` y su `summary`).
- Modify: `k8s/prod-configMap.yaml:143` (`Shopify__App__Scopes: "read_orders,write_orders,read_customers,read_products"`).
- Modify: `README.md` § Integración con Shopify (tabla `:4829`, «Despliegue» y verificaciones) y `CLAUDE.md` (gotchas).
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ShopifyAppOptionsValidatorTests.cs` (o la que fije los defaults).

- [ ] **Step 1: Prueba** — `[Fact] void TheDefaultScopesIncludeReadProducts()`.
- [ ] **Step 2: RED** — es de comportamiento (constante): queda escrita, sin RED de compilación.
- [ ] **Step 3: Implementa** y documenta:
  - README: `read_products` en la tabla; **antes de desplegar**, agregarlo en los scopes de la app del Partner Dashboard; una
    tienda instalada antes recibe 422 `scope_missing` al importar y no recibe `products/*` hasta reautorizar (el canje con el
    scope nuevo los registra, P10). Migraciones aditivas (Catalog `AddProductExternalSource`, Shopify `AddProductSync`).
    Verificaciones contra Shopify real: `productsCount(limit: null)`, `featuredMedia.preview.image.url(transform:)` en JPG,
    filtro `product_status` de `productVariants`, tienda con variantes sin SKU y con `taxesIncluded = false` (§9).
  - CLAUDE.md, gotchas nuevos (cortos): (1) todo camino que escriba en Catalog desde Shopify pasa por `ShopifyProductSync` con
    el candado `20261118` y el `CatalogDbContext` enrolado; un rechazo aborta la transacción y se reintenta en otra; (2) un
    producto con `external_source` rechaza nombre, opciones, variantes, códigos y precios en el `PUT`
    (`catalog.product.managed_externally`): un camino nuevo de edición aplica `ExternallyManagedProducts`; (3) la imagen que no
    entra en la galería se libera explícitamente, o queda un archivo `Available` para siempre.
- [ ] **Step 4: Build** → `0`. **Commit** — `chore(shopify): request read_products and document the product sync`.

**Fin del Bloque C:** una revisión del bloque y, después, la revisión fuerte de toda la rama. Luego el owner corre las pruebas de
los proyectos tocados (Global Constraints).

---

## Self-review

- **Cobertura del spec:** §3 → T5; §4 → T6; §5.1 → T8 (`ShopifyScopes`), T12, T14; §5.2 → T5, T11; §5.3 → T9, T10; §5.4 → T3,
  T8; §5.5 → T1, T2, T12 (desvincular); §6 → T10, T13; §7 → T12; §8 → T1, T5, T14; §9 → pruebas de cada tarea; P1 → T4 (sin
  sonda: verificado), T8, T10; P2 → T11; P3 → T3, T11, T13; P4 → T9, T11, T12; P5 → T0; P6, P9 → T9, T10; P7 → T10; P8 → T8, T13;
  P10 → T13.
- **Tipos:** `ShopifyProductTally` (T5) lo usan T10 y T11; `ShopifyCatalogProduct`/`ShopifyCatalogWrite` (T8) los produce T9 y
  los consume T10; `ShopifyPricingContext` (T9) lo arma T11 y T13; `IShopifyProductsSource` (T7) lo usan T11, T12 y T13.
- **Fuera de alcance a propósito:** la pantalla (trabajo 3), cola en métricas (D10), cachear la cobertura.
