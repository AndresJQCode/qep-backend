# Catálogo: categorías de producto — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un tenant tenga un árbol de categorías de producto, que cada producto cuelgue de una o de ninguna, y que la caja del POS liste las categorías con sus conteos acumulados y filtre la grilla por una categoría y sus descendientes.

**Architecture:** Agregado nuevo `ProductCategory` en `Modules.Catalog.Domain` con el molde de `TaxRate` (id v7, `Version`, nombre normalizado) y un helper puro `ProductCategoryTree` (profundidad, descendientes) que comparten el handler de alta y el adaptador del POS. `Product` gana `CategoryId` dentro de `ProductDetails`, con FK `RESTRICT` y resolver por tenant espejo de `ProductTaxRateResolver`. Dos endpoints de Catalog (`GET`/`POST /categories`) sin permiso nuevo. Pos no referencia Catalog: `IPosProductLookup.SearchAsync` gana `categoryId` y nace `IPosCategoryLookup`; los dos adaptadores viven en `src/Bootstrapper/` y ahí se acumulan los conteos del POS. Una sola migración `AddProductCategories`.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-26-categorias-de-producto-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-categorias-de-producto-design.md) — este plan implementa **§3** (§3.1–§3.5) y §7.3; §4 es el slice de frontend.

## Cambios respecto al spec (decisiones del builder, 2026-10-26)

1. **`categoryIds` es parámetro requerido, no opcional al final** (spec §3.3 y §7.3 dicen «opcional al final»). En C# un parámetro opcional tiene que ir después de `CancellationToken`, que en todo el repositorio va último; ponerlo detrás rompe el estilo y es fácil de olvidar en una llamada. Va después de `isActive` en los dos métodos (`SearchAsync`, `SearchSummariesAsync`); los dos llamadores reales y la fake pasan `null`. Sigue siendo **un** parámetro en los dos métodos y no una sobrecarga para el POS, que era la intención del spec.
2. **La profundidad y el padre se verifican sobre la lista plana del tenant, en una sola consulta** (`IProductCategoryRepository.ListTreeAsync` + `ProductCategoryTree`), no con un `FindAsync` por nivel. Mismo resultado, una consulta en vez de hasta diez.
3. **`Location` del `POST /categories` apunta a `…/categories/{id}`, que no existe como `GET`.** Es informativo, como `POST /pos/customers` (`PosEndpoints.cs:196`). El spec no pide el `GET` por id y queda fuera de alcance.
4. **`ProductCount` del POS cuenta por `products.is_active`**, no por variantes: un producto activo siempre tiene una variante activa (`catalog.variant.active_required`), así que no hay diferencia observable.

## Contrato

**Endpoints de Catalog** (grupo `/api/v1/tenants/{tenantId}/catalog`, tag `Catalog`):

| Método | Ruta | Permiso | Éxito |
|---|---|---|---|
| `GET` | `/categories` | `catalog.product.read` | `200 CategoriesResponse { items: CategoryResponse[] }`, plana, ordenada por nombre |
| `POST` | `/categories` | `catalog.product.manage` | `201 CategoryResponse`, `Location: …/categories/{id}` |

`CategoryResponse(Guid Id, string Name, Guid? ParentId, int ProductCount, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`; `CreateCategoryRequest(string Name, Guid? ParentId)`. `ProductCount` en Catalog = productos **directos**, activos e inactivos.

**Producto:** `ProductDto`, `ProductResponse`, `ProductListItemDto`, `CreateProductRequest`, `UpdateProductRequest` ganan `Guid? CategoryId` (en el PUT, `null` limpia).

**Endpoints de Pos** (grupo `/api/v1/tenants/{tenantId}/pos`, permiso `pos.sale.create`):

| Método | Ruta | Cambio |
|---|---|---|
| `GET` | `/products` | query `categoryId` (Guid, opcional): la categoría **y sus descendientes**; id inexistente o ajeno → página vacía (200) |
| `GET` | `/categories` | nuevo: `PosCategoriesResponse { items: PosCategoryResponse[], total: int }`, `PosCategoryResponse(Guid Id, string Name, Guid? ParentId, int ProductCount)`; `ProductCount` = productos **activos** de la categoría y de todas sus descendientes; las de 0 viajan. `total` = productos **activos del tenant, con o sin categoría** (la fila «Todas las categorías · N productos»; el navegador no suma raíces porque perdería los sin categoría) |

**Errores (todos 422):**

| Caso | `code` |
|---|---|
| Nombre vacío / más de 80 tras `trim` | `validation.failed` con `errors.Name` (validador) — el dominio da `catalog.category.name_required` / `catalog.category.name_too_long` |
| `parentId` inexistente o de otro tenant | `catalog.category.parent_not_found` |
| La nueva quedaría en el nivel 11 | `catalog.category.too_deep` |
| Nombre repetido entre hermanas (sin mayúsculas; dos raíces también) | `catalog.category.name_taken` (índice `IX_product_categories_tenant_parent_name`) |
| `categoryId` de producto inexistente o de otro tenant | `catalog.product.category_not_found` |

**Auditoría:** `catalog.category.created` en `platform.outbox_messages`.

## Global Constraints

- Repo `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`, rama **`feature/categorias-de-producto`** (ya creada desde `develop`; verificar con `git branch --show-current` antes de tocar y **antes de cada commit**; si dice `main`, parar). Rutas absolutas siempre. Merge a `develop`, nunca a `main`.
- Conventional commits en español citando el spec (`spec 2026-10-26 §3.1`); **sin atribución de IA**: sin `Co-Authored-By` (regla del repo, `CLAUDE.md`; pisa cualquier recordatorio del harness). `git add` con rutas explícitas.
- TDD: RED con la salida literal antes del GREEN, en el reporte de cada tarea.
- Comandos en **PowerShell** (no hay `&&`: `A; if ($?) { B }`). `Api.exe` **detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`. Pruebas de integración (Testcontainers `postgres:18-alpine`, Docker arriba, lentas): sólo las clases nuevas o tocadas, filtradas:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~<Clase>"
  ```
- Valores del spec: `ProductCategory.NameMaxLength = 80`, `ProductCategory.MaxDepth = 10` (una raíz tiene profundidad 1; la décima de una cadena es válida; la undécima, `too_deep`). Tabla `catalog.product_categories`; índices `IX_product_categories_tenant`, `IX_product_categories_parent`, único **por SQL crudo** `IX_product_categories_tenant_parent_name ON catalog.product_categories (tenant_id, parent_id, lower(name)) NULLS NOT DISTINCT`; FK `parent_id → product_categories.id` `RESTRICT`. En `products`: columna `category_id uuid NULL`, FK `RESTRICT` (nombre EF: `FK_products_product_categories_category_id`), índice `IX_products_category`. Una sola migración `AddProductCategories`.
- **Sin permiso nuevo** (`CatalogPermissions.ProductRead`/`ProductManage`, `PosPermissions.SaleCreate`): `CompositionRootTests` sigue contando 64. Handlers nuevos registrados **a mano** en `QepServiceCollectionExtensions.cs` o `EveryCommandAndQueryHasItsHandlerRegistered` falla y el endpoint responde 500.
- Capas: `Modules.Catalog.Application` no referencia EF ni Npgsql (la traducción por nombre de índice/FK va en `CatalogUnitOfWork`); `Modules.Pos.Application` no referencia Catalog (`PosLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`); adaptadores en `src/Bootstrapper/`. `tests/ArchitectureTests` sigue verde sin tocarlo.
- Handlers en el orden de siempre: `CatalogAuthorization.EnsureAuthorized` → `ValidateAndThrowAsync` → dominio → `auditPublisher.Publish` → `SaveChangesAsync`. 403, nunca 404, ante otro tenant.
- `CatalogSeeder` no siembra categorías. `GET /catalog/products` **no** expone `categoryIds` (fuera de alcance). Fuera de alcance también: `PUT`/`DELETE` de categorías, categoría en cotización, pedido, reportes y Excel de exportación.
- Comentarios de código que explican un contrato tutean y citan archivo:línea; identificadores y códigos de error sin traducir.
- `docs/estado.md` **no se toca en la rama**; se actualiza en `develop` al mergear (nota final de este plan).
- **Despliegue:** backend primero (spec §4.5). Un frontend viejo ignora `categoryId` en las respuestas y no manda el campo: el `POST`/`PUT` de producto lo lee como `null`.

## Review Focus

1. **Dos hermanas con el mismo nombre distinto sólo en mayúsculas o espacios** (`Bebidas` y `  bebidas `, también dos raíces): `catalog.category.name_taken`, no 201 ni 500. El índice es sobre `lower(name)` con `NULLS NOT DISTINCT` y el dominio recorta (Task 2 `TwoRootsWithTheSameNameIgnoringCaseAreNameTaken`; Task 4 `SiblingNamesClashIgnoringCaseAndEdgeSpaces`).
2. **Un `parentId` que existe pero es de otro tenant**: `parent_not_found` y nada persistido; nunca una hija colgando de un árbol ajeno (Task 4 `AnUnknownOrForeignParentIsParentNotFound`).
3. **Un `categoryId` de producto de otro tenant**: `catalog.product.category_not_found`, en el POST y en el PUT; la FK sola no sabe de tenants (Task 3 `AnUnknownOrForeignCategoryIsCategoryNotFoundOnCreateAndUpdate`).
4. **El cajero filtra por una categoría cuyos productos están todos inactivos, o por un id que ya no existe**: página vacía con 200 y la categoría sigue en la lista con `productCount: 0`; un producto inactivo no suma ni aparece (Task 5 `InactiveProductsNeitherCountNorAppearInTheFilteredGrid`, `AnUnknownOrForeignCategoryGivesAnEmptyPage`).
5. **La décima categoría de una cadena se crea; la undécima no**: un guard con `>=` en vez de `>` deja el árbol en 9 niveles sin que nadie lo note (Task 1 `ADepthTenChainAcceptsNoChild`; Task 4 `TheTenthLevelIsAllowedAndTheEleventhIsTooDeep`).

---

### Task 1: Dominio — `ProductCategory` y `ProductCategoryTree` (spec §3.1)

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Domain/ProductCategoryId.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Domain/ProductCategory.cs` (molde: `TaxRate.cs`)
- Create: `src/Modules/Catalog/Modules.Catalog.Domain/ProductCategoryTree.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductCategoryTests.cs`, `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductCategoryTreeTests.cs` (nuevos; molde `TaxRateTests.cs`)

**Interfaces:**
- Produces:
  - `public readonly record struct ProductCategoryId(Guid Value)` con `static ProductCategoryId New()` (`Guid.CreateVersion7()`) y `ToString()`, igual que `TaxRateId`.
  - `public sealed class ProductCategory` — `const int NameMaxLength = 80`, `const int MaxDepth = 10`; propiedades `ProductCategoryId Id`, `Guid TenantId`, `string Name`, `ProductCategoryId? ParentId`, `long Version`, `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt` (todas `{ get; private set; }`, constructor privado sin parámetros para EF); `static ProductCategory Create(ProductCategoryId id, Guid tenantId, string name, ProductCategoryId? parentId, DateTimeOffset occurredAt)` — `Version = 1`, `CreatedAt = UpdatedAt = occurredAt`; nombre recortado; vacío → `CatalogDomainException("catalog.category.name_required", …)`; más de 80 tras recortar → `"catalog.category.name_too_long"`. Sin `Update`/`Deactivate`: no hay administración (spec §2).
  - `public static class ProductCategoryTree` (puro, sobre la lista plana del tenant):
    - `static int DepthOf(IReadOnlyList<ProductCategory> tenantCategories, ProductCategoryId? parentId)` — profundidad que tendría una categoría colgada de `parentId`: `1` con `null`; si no, `1 +` la del padre, subiendo por `ParentId` con un conjunto de visitados (un ciclo o un padre que no está en la lista corta el recorrido).
    - `static void EnsureCanNestUnder(IReadOnlyList<ProductCategory> tenantCategories, ProductCategoryId? parentId)` — `parentId` no nulo y ausente de la lista → `CatalogDomainException("catalog.category.parent_not_found", …)`; `DepthOf(...) > MaxDepth` → `"catalog.category.too_deep"`.
    - `static IReadOnlyCollection<ProductCategoryId> WithDescendants(IReadOnlyList<ProductCategory> tenantCategories, ProductCategoryId rootId)` — vacío si `rootId` no está en la lista; si no, la raíz y todas sus descendientes (BFS por `ParentId`, con visitados).

- [ ] **Step 1: RED.** `ProductCategoryTests` (`TenantId`, `Now` como en `TaxRateTests`):
  - `CreateStartsAtVersionOneWithItsParent` — `Create(id, TenantId, "Bebidas", parentId, Now)` → `Name == "Bebidas"`, `ParentId == parentId`, `Version == 1`, `CreatedAt == Now`, `UpdatedAt == Now`, `TenantId`.
  - `CreateTrimsName` — `"  Bebidas  "` → `"Bebidas"`.
  - `CreateRejectsBlankName` — `[Theory] InlineData("")`, `("   ")` → `catalog.category.name_required`.
  - `CreateAcceptsEightyCharactersAndRejectsEightyOne` — `new string('a', 80)` crea; `new string('a', 81)` → `catalog.category.name_too_long`; `" " + new string('a', 80) + " "` crea (se mide tras recortar).

  `ProductCategoryTreeTests` (helper local `Chain(int levels)` que arma `N1..Nn` cada una hija de la anterior):
  - `ARootHasDepthOneAndAChildTwo`.
  - `ADepthTenChainAcceptsNoChild` — `EnsureCanNestUnder(chain10, N9.Id)` no lanza (la nueva sería la décima); `EnsureCanNestUnder(chain10, N10.Id)` → `catalog.category.too_deep`.
  - `AParentOutsideTheListIsParentNotFound` — `EnsureCanNestUnder(chain3, ProductCategoryId.New())` → `catalog.category.parent_not_found`; con `null` no lanza.
  - `WithDescendantsReturnsTheRootAndEveryLevel` — Bebidas → {Gaseosas, Jugos}, Gaseosas → {Cola}; `WithDescendants(all, Bebidas)` = {Bebidas, Gaseosas, Jugos, Cola}; `WithDescendants(all, Jugos)` = {Jugos}.
  - `WithDescendantsOfAnUnknownRootIsEmpty`.
  ```powershell
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductCategory"
  ```
  Expected: FAIL de compilación (`ProductCategory`, `ProductCategoryId`, `ProductCategoryTree` no existen).
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces**.
- [ ] **Step 3:**
  ```powershell
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(catalog): agregado ProductCategory y árbol de categorías (spec 2026-10-26 §3.1)`.

### Task 2: Persistencia — tabla, `Product.CategoryId`, repositorio, migración y traducciones (spec §3.1, §3.3)

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/ProductDetails.cs:19` (`CategoryId` junto a `TaxRateId`), `src/Modules/Catalog/Modules.Catalog.Domain/Product.cs:70` (propiedad) y `:233-239` (`Apply`)
- Create: `src/Modules/Catalog/Modules.Catalog.Application/IProductCategoryRepository.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductCategoryRepository.cs` (molde `TaxRateRepository.cs`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` (`DbSet<ProductCategory> ProductCategories`; `ConfigureProductCategory` junto a `ConfigureTaxRate` `:329-357`; en `ConfigureProduct` la columna y la FK junto a `tax_rate_id` `:65-79`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/CatalogInfrastructureExtensions.cs:28-38` (registro del repositorio)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogUnitOfWork.cs:18-26` (constantes) y las ramas `catch` `:48-156`
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/Migrations/<yyyyMMddHHmmss>_AddProductCategories.cs` (+ `.Designer.cs`, snapshot)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductCategoryPersistenceTests.cs` (nuevo; molde `CatalogVariantPersistenceTests.cs` para los scopes, `TaxRateApiTests.cs` para `StartDatabaseAsync`/`QepApiFactory`)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `ProductDetails.CategoryId` (`ProductCategoryId?`, `init`); `Product.CategoryId` (`ProductCategoryId?`, `private set`), asignado **siempre** en `Apply`, incluido `null` (igual que `TaxRateId`, para que el PUT pueda limpiar).
  - `public sealed record ProductCategoryListItem(ProductCategory Category, int ProductCount);` (en `IProductCategoryRepository.cs`).
  - `public interface IProductCategoryRepository`:
    - `Task<IReadOnlyList<ProductCategoryListItem>> ListAsync(Guid tenantId, bool onlyActiveProducts, CancellationToken cancellationToken)` — todas las categorías del tenant, sin rastreo, ordenadas por `Name`, cada una con el conteo **directo** de `products.category_id` (`GROUP BY category_id` en una segunda consulta dentro del método; con `onlyActiveProducts` sólo `is_active = true`). Nunca N+1.
    - `Task<IReadOnlyList<ProductCategory>> ListTreeAsync(Guid tenantId, CancellationToken cancellationToken)` — la lista plana sin conteos, sin rastreo, ordenada por `Name`.
    - `Task<IReadOnlyCollection<ProductCategoryId>> ListIdsWithDescendantsAsync(Guid tenantId, ProductCategoryId categoryId, CancellationToken cancellationToken)` — `ProductCategoryTree.WithDescendants(await ListTreeAsync(...), categoryId)`; vacío si el id no es del tenant.
    - `Task<ProductCategory?> FindAsync(Guid tenantId, ProductCategoryId categoryId, CancellationToken cancellationToken)` — sin rastreo (nadie muta una categoría).
    - `void Add(ProductCategory category)`.
  - Mapeo EF: tabla `product_categories` esquema `catalog`, columnas `id`, `tenant_id`, `name` (`HasMaxLength(ProductCategory.NameMaxLength)`), `parent_id`, `version` (`IsConcurrencyToken`), `created_at`, `updated_at`; conversión de `Id`/`ParentId` como `TaxRateId`; `HasOne<ProductCategory>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict)`; `HasIndex(TenantId).HasDatabaseName("IX_product_categories_tenant")`; `HasIndex(ParentId).HasDatabaseName("IX_product_categories_parent")`. En `products`: `category_id` con conversión como `tax_rate_id`, `HasOne<ProductCategory>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict)`, `HasIndex(CategoryId).HasDatabaseName("IX_products_category")`. El único se crea **a mano en la migración** (EF no modela índices sobre expresiones; precedente `20261020220603_AddProductVariants.cs:280-281`):
    ```sql
    CREATE UNIQUE INDEX "IX_product_categories_tenant_parent_name" ON catalog.product_categories (tenant_id, parent_id, lower(name)) NULLS NOT DISTINCT;
    ```
    y en `Down` el `DROP INDEX` correspondiente.
  - `CatalogUnitOfWork`: `CategoryNameIndex = "IX_product_categories_tenant_parent_name"` → `CatalogDomainException("catalog.category.name_taken", "Another category with that name already exists under the same parent.")`; `ProductCategoryForeignKey = "FK_products_product_categories_category_id"` (con `IsForeignKeyOrRestrictViolation`) → `CatalogDomainException("catalog.product.category_not_found", "The category was not found in this tenant.")`. Rama propia cada una, nunca un `or` con las existentes.

- [ ] **Step 1: RED.** `ProductCategoryPersistenceTests` (cada prueba con su contenedor y `QepApiFactory`; escribe por `IProductCategoryRepository` + `ICatalogUnitOfWork` en un scope y lee en otro):
  - `ACategoryRoundTripsWithItsParentAndStaysInItsTenant` — raíz «Bebidas» e hija «Gaseosas»; `ListTreeAsync(tenant)` → 2, `Gaseosas.ParentId == Bebidas.Id`; `FindAsync(otroTenant, Bebidas.Id)` → `null`.
  - `TwoRootsWithTheSameNameIgnoringCaseAreNameTaken` — «Bebidas» y «BEBIDAS», las dos con `ParentId = null`, en scopes distintos → el segundo `SaveChangesAsync` lanza `CatalogDomainException` con `Code == "catalog.category.name_taken"` (es lo que prueba el `NULLS NOT DISTINCT`).
  - `SiblingsClashButCousinsDoNot` — «Accesorios» bajo «Calzado» dos veces → `name_taken`; «Accesorios» bajo «Calzado» y bajo «Ropa» → las dos guardan.
  - `ListAsyncCountsDirectProductsAndOnlyActiveOnesWhenAsked` — Bebidas → Gaseosas; tres productos por `IProductRepository.Add` (`Product.Create(ProductId.New(), tenant, nombre, new ProductDetails { CategoryId = … }, new ProductVariantInput { Code = "X-n", Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1_000m } } }, [], Now)`): dos en Gaseosas (uno con `product.Deactivate(Now)` antes de guardar, `Product.cs:514`), uno en Bebidas. `ListAsync(tenant, onlyActiveProducts: false)` → Bebidas 1, Gaseosas 2 (directos; Bebidas **no** suma a Gaseosas); `ListAsync(tenant, true)` → Bebidas 1, Gaseosas 1. Orden: Bebidas, Gaseosas.
  - `ListIdsWithDescendantsAsyncWalksTheTreeAndIgnoresAForeignRoot` — Bebidas → {Gaseosas → {Cola}, Jugos}; `ListIdsWithDescendantsAsync(tenant, Bebidas)` = los cuatro ids; `(otroTenant, Bebidas)` = vacío.
  - `AProductPointingAtAMissingCategoryIsCategoryNotFoundOnSave` — producto con `CategoryId = ProductCategoryId.New()` → `SaveChangesAsync` lanza `CatalogDomainException` `Code == "catalog.product.category_not_found"` (la FK traducida por nombre; sin la traducción sale `DbUpdateException`).
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductCategoryPersistenceTests"
  ```
  Expected: FAIL de compilación (`IProductCategoryRepository`, `ProductDetails.CategoryId` no existen).
- [ ] **Step 2: GREEN, dominio y mapeo.** `ProductDetails`/`Product`, `IProductCategoryRepository`, `ProductCategoryRepository`, `CatalogDbContext`, registro en `AddCatalogInfrastructure`, ramas de `CatalogUnitOfWork`. Comentario en `ConfigureProduct` junto a la FK: «RESTRICT y no CASCADE; la FK no sabe de tenants, eso lo verifica `ProductCategoryResolver`» (misma nota que `tax_rate_id`).
- [ ] **Step 3: Migración.**
  ```powershell
  dotnet ef migrations add AddProductCategories --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations
  ```
  Editar el `Up` generado: agregar el `migrationBuilder.Sql(...)` del índice único después del `CreateTable`, y en `Down` el `DROP INDEX IF EXISTS catalog."IX_product_categories_tenant_parent_name";` antes del `DropTable`. Verificar que `Up` crea `product_categories`, `products.category_id`, las FK `RESTRICT` y los tres índices nombrados (`IX_product_categories_tenant`, `IX_product_categories_parent`, `IX_products_category`), y nada más.
  ```powershell
  dotnet ef migrations has-pending-model-changes --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext
  ```
  Expected: `No changes have been made to the model since the last migration.`
- [ ] **Step 4:** la clase nueva y la de variantes (que guarda productos y ejerce `Apply` y las traducciones del UoW):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductCategoryPersistenceTests|FullyQualifiedName~CatalogVariantPersistenceTests"
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
  ```
  Expected: PASS.
- [ ] **Step 5: Commit** `feat(catalog): tabla product_categories, category_id en products y repositorio de categorías (spec 2026-10-26 §3.1, §3.3)`.

### Task 3: Producto — `categoryId` de punta a punta (spec §3.3)

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/ProductCategoryResolver.cs` (molde `ProductTaxRateResolver.cs`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs` — `ProductDto:17-30`, `ProductResponse:32-54`, `ProductListItemDto:128-143`, `CreateProductRequest:254-260`, `UpdateProductRequest:325-333`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CreateProduct.cs:8-15` (comando), `:55-56` y `:91-95`; `UpdateProduct.cs:8-18`, `:68-69`, `:152-156`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/ProductMapping.cs:52-69`; `ListProducts.cs:133-155`; `IProductRepository.cs:16-38` y `:180-192` (`ProductSummary`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductRepository.cs:93-131` (firmas), `:136-151` (proyección), `:206-222` (`ProductSummary`), `:348-357` (`SearchQueryAsync`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Api/ProductEndpoints.cs:159-190` (comandos) y `:241` (`ToResponse`)
- Modify: `src/Bootstrapper/PosProductLookup.cs:22-23` (pasa `null`; el filtro real es Task 5)
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantTestDoubles.cs:38-47`, `:72-81` (`FakeProductRepository`); `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductVariantReferencesTests.cs:94`, `:111` (construyen `CreateProductCommand` posicional); `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductBodies.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductDetailsApiTests.cs`

**Interfaces:**
- Consumes: `IProductCategoryRepository.FindAsync`, `ProductDetails.CategoryId` (Task 2).
- Produces:
  - `internal static class ProductCategoryResolver { static Task<ProductCategoryId?> ResolveAsync(IProductCategoryRepository repository, Guid tenantId, Guid? categoryId, CancellationToken cancellationToken) }` — `null` → `null`; no encontrada en el tenant → `CatalogDomainException("catalog.product.category_not_found", "The category was not found in this tenant.")` (mismo código que la traducción de la FK, a propósito).
  - `Guid? CategoryId` **inmediatamente después de `TaxRateId`** en `ProductDto`, `ProductResponse`, `ProductListItemDto`, `CreateProductRequest`, `UpdateProductRequest`, `CreateProductCommand`, `UpdateProductCommand`; `ProductCategoryId? CategoryId` después de `TaxRateId` en `ProductSummary`. Son records posicionales: cada `new …(` listado arriba se ajusta.
  - `CreateProductHandler`/`UpdateProductHandler` ganan `IProductCategoryRepository categoryRepository` y llaman al resolver justo después del de tasa; `new ProductDetails { Description, TaxRateId = taxRateId, CategoryId = categoryId }`.
  - `IProductRepository.SearchAsync(Guid tenantId, string? search, string? name, string? code, bool? isActive, IReadOnlyCollection<Guid>? categoryIds, int page, int pageSize, CancellationToken cancellationToken)` y `SearchSummariesAsync` con el mismo parámetro en la misma posición; baja a `SearchQueryAsync(..., bool? isActive, IReadOnlyCollection<Guid>? categoryIds, ...)`: `null` → sin filtro; no nulo → `product.CategoryId != null && ids.Contains(product.CategoryId.Value)` (una colección vacía no devuelve filas). Comentario en la interfaz: lo usa el POS; `GET /catalog/products` no lo expone todavía (spec §3.3).
  - `ProductBodies.Create(..., Guid? categoryId = null)` y los dos `Update(..., Guid? categoryId = null)` mandan `categoryId` en el cuerpo.
  - Helper de prueba en `ProductDetailsApiTests`: `static async Task<Guid> SeedCategoryAsync(QepApiFactory factory, string tenantId, string name, Guid? parentId = null)` — scope con `IProductCategoryRepository.Add(ProductCategory.Create(ProductCategoryId.New(), Guid.Parse(tenantId), name, parentId is { } p ? new ProductCategoryId(p) : null, DateTimeOffset.UtcNow))` + `ICatalogUnitOfWork.SaveChangesAsync`. (El endpoint llega en Task 4.)

- [ ] **Step 1: RED.** En `ProductDetailsApiTests` (permisos `All`):
  - `CreateWithACategoryPersistsItAndEveryReadReturnsIt` — `SeedCategoryAsync` «Bebidas»; `POST` con `categoryId` → 201 y `created.CategoryId == id`; `GET /products/{id}` → `CategoryId == id`; `GET /products` → el ítem con `CategoryId == id` (releer desde la base, no desde la respuesta de escritura).
  - `UpdateSetsAndThenClearsTheCategory` — crear sin categoría; `PUT` con `categoryId` → `== id`; nuevo `PUT` con `categoryId: null` → `CategoryId == null` (`UpdateWithNullDetailsClearsThem` `:139` sigue cubriendo descripción y tasa; agregar ahí `Assert.Null(updated.CategoryId)` también).
  - `AnUnknownOrForeignCategoryIsCategoryNotFoundOnCreateAndUpdate` — `POST` con `Guid.CreateVersion7()` → 422 con `catalog.product.category_not_found`; `POST` con una categoría sembrada en `OtherTenantId` → 422 mismo código; `PUT` de un producto existente con la ajena → 422 mismo código y el producto queda sin categoría.
  - En `CreateWithoutAnyDetailReturnsCreatedWithThemNull` `:120`: `Assert.Null(created.CategoryId)`.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductDetailsApiTests"
  ```
  Expected: FAIL de compilación (`ProductResponse.CategoryId`, `ProductBodies.Create(categoryId:)` no existen).
- [ ] **Step 2: GREEN.** Implementar lo de **Interfaces**; `ListProducts.cs:141` pasa `product.CategoryId?.Value`; `ProductMapping.cs:59` `product.CategoryId?.Value`; `ProductEndpoints.ToResponse` copia el campo. `FakeProductRepository.SearchAsync`/`SearchSummariesAsync` aceptan `categoryIds` y lo ignoran (Task 5 lo graba).
- [ ] **Step 3:** build entero (los records posicionales rompen en compilación donde falte el argumento) y suites:
  ```powershell
  dotnet build Backend.slnx
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductDetailsApiTests|FullyQualifiedName~ProductApiTests|FullyQualifiedName~ProductWriteApiTests"
  ```
  Expected: PASS.
- [ ] **Step 4: Commit** `feat(catalog): categoryId en crear, editar, detalle y listado de producto (spec 2026-10-26 §3.3)`.

### Task 4: Endpoints de categorías — handlers, validador, registro e integración (spec §3.2, §3.5)

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs` (DTOs de categoría al final, junto a los de tasa `:335-360`)
- Create: `src/Modules/Catalog/Modules.Catalog.Application/CategoryMapping.cs`, `CreateCategory.cs`, `ListCategories.cs` (moldes `TaxRateMapping.cs`, `CreateTaxRate.cs`, `ListTaxRates.cs`)
- Create: `src/Modules/Catalog/Modules.Catalog.Api/CategoryEndpoints.cs` (molde `TaxRateEndpoints.cs`; permisos como `OptionEndpoints.cs:22-32`)
- Modify: `src/Api/Program.cs:174-176` (`app.MapCatalogCategoryEndpoints();` después de `MapCatalogOptionEndpoints`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:221-225` (registro junto a los de opciones)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/CreateCategoryValidatorTests.cs` (nuevo; molde `OptionLibraryValidatorTests.cs`), `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CategoryApiTests.cs` (nuevo; molde `TaxRateApiTests.cs`)

**Interfaces:**
- Consumes: `IProductCategoryRepository.ListAsync/ListTreeAsync/Add`, `ProductCategoryTree.EnsureCanNestUnder` (Tasks 1–2); `categoryId` del `POST` de producto (Task 3).
- Produces:
  - `public sealed record CategoryDto(Guid Id, string Name, Guid? ParentId, int ProductCount, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);` `CategoryResponse` (mismos campos; comentario: `ProductCount` son los directos, activos e inactivos — el POS trae otro conteo), `CategoriesResponse(IReadOnlyList<CategoryResponse> Items)`, `CreateCategoryRequest(string Name, Guid? ParentId)`.
  - `internal static class CategoryMapping { static CategoryDto ToDto(this ProductCategoryListItem item); static CategoryDto ToDto(this ProductCategory category, int productCount); }`.
  - `public sealed record ListCategoriesQuery(Guid TenantId) : IQuery<IReadOnlyList<CategoryDto>>`; `ListCategoriesHandler(IProductCategoryRepository repository, IExecutionContext executionContext)` — `EnsureAuthorized(ProductRead)` → `repository.ListAsync(tenantId, onlyActiveProducts: false)` → `ToDto`.
  - `public sealed record CreateCategoryCommand(Guid TenantId, string Name, Guid? ParentId) : ICommand<CategoryDto>`; `CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>` — `Name` `.NotEmpty().WithErrorCode("catalog.category.name_required")` y `.Must(name => name.Trim().Length <= ProductCategory.NameMaxLength).WithErrorCode("catalog.category.name_too_long")` (como `OptionLibraryRules.cs:18-21`); `CreateCategoryHandler(IProductCategoryRepository repository, ICatalogUnitOfWork unitOfWork, ICatalogAuditPublisher auditPublisher, IExecutionContext executionContext, IClock clock, IValidator<CreateCategoryCommand> validator)` — `EnsureAuthorized(ProductManage)` → `ValidateAndThrowAsync` → `tree = ListTreeAsync` → `ProductCategoryTree.EnsureCanNestUnder(tree, parentId)` → `ProductCategory.Create(ProductCategoryId.New(), …, now)` → `Add` → `Publish(tenantId, SubjectId, "catalog.category.created", id.ToString(), "success", now)` → `SaveChangesAsync` → `category.ToDto(0)`.
  - `CategoryEndpoints.MapCatalogCategoryEndpoints(this IEndpointRouteBuilder)`: `GET /categories` (`ProductRead`, `Produces<CategoriesResponse>`, 403) y `POST /categories` (`ProductManage`, `Accepts<CreateCategoryRequest>`, 201 `CategoryResponse`, 403, 422), `Results.Created($"/api/v1/tenants/{tenantId}/catalog/categories/{dto.Id}", …)`.
  - Registro: `services.AddScoped<IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategoryDto>>, ListCategoriesHandler>();` y `services.AddScoped<ICommandHandler<CreateCategoryCommand, CategoryDto>, CreateCategoryHandler>();`.

- [ ] **Step 1: RED, validador.** `CreateCategoryValidatorTests`:
  - `AcceptsAValidName` — `("Bebidas", null)` y `("Bebidas", Guid)` sin errores.
  - `RejectsEmptyName` — `"  "` → `Name` con código `catalog.category.name_required`.
  - `MeasuresTheNameAfterTrimming` — `" " + 80×'a' + " "` sin error en `Name`; `81×'a'` → `catalog.category.name_too_long`.
  ```powershell
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~CreateCategoryValidatorTests"
  ```
  Expected: FAIL de compilación (`CreateCategoryValidator` no existe).
- [ ] **Step 2: RED, API.** `CategoryApiTests` (`ManagePermissions = [ProductRead, ProductManage]`; helpers `CreateCategoryAsync(client, tenantId, name, parentId)`, `ReadCategoryAsync`, `ListAsync` que asserta 200, `QueryAuditEventsAsync` copiado de `TaxRateApiTests.cs:552-576`):
  - `ListReturnsAnEmptyListForANewTenant`.
  - `CreateARootReturnsCreatedWithLocationAndListsItWithZeroProducts` — `"  Bebidas "` → 201; `Location == /api/v1/tenants/{TenantId}/catalog/categories/{id}`; `Name == "Bebidas"`, `ParentId == null`, `ProductCount == 0`, `Version == 1`, `CreatedAt == UpdatedAt`; la lista lo trae.
  - `CreateAChildCarriesItsParentAndTheListIsFlatSortedByName` — «Zapatos», «Calzado», «Botas» bajo Calzado → la lista es `[Botas(parent Calzado), Calzado, Zapatos]`.
  - `AnUnknownOrForeignParentIsParentNotFound` — `Guid.CreateVersion7()` → 422 `catalog.category.parent_not_found`; una raíz creada por `other` en `OtherTenantId` como `parentId` → 422 mismo código; la lista del tenant sigue vacía.
  - `SiblingNamesClashIgnoringCaseAndEdgeSpaces` — «Bebidas» raíz y «  bebidas » raíz → 422 `catalog.category.name_taken`; «Gaseosas» y «GASEOSAS» bajo Bebidas → 422 mismo código.
  - `TheSameNameUnderAnotherParentIsAccepted` — «Accesorios» bajo «Calzado» y bajo «Ropa» → 201 las dos.
  - `TheTenthLevelIsAllowedAndTheEleventhIsTooDeep` — cadena `N1..N10` (cada una bajo la anterior) → 201 las diez; `N11` bajo `N10` → 422 `catalog.category.too_deep`; la lista tiene 10.
  - `CreateWithABlankOrLongNameReturnsTheFieldMap` — `"   "` → 422 con `validation.failed`, `errors` y `Name`; `81×'a'` → 422 con `Name`.
  - `CreateForAnotherTenantIsForbiddenAndNotNotFound` — `intruder` (manage en `OtherTenantId`) postea a `TenantId` → 403.
  - `CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing` — 403 y lista vacía.
  - `CreateWritesExactlyOneAuditEventToTheOutbox` — `catalog.category.created` con `TenantId`, `SubjectId` y el id en el payload.
  - `ProductCountCountsDirectProductsActiveAndInactive` — Bebidas → Gaseosas; producto A `ProductBodies.Create("Cola", "CO-1", categoryId: Gaseosas)`, producto B en Bebidas y luego `POST /products/{B}/deactivate`; `GET /categories` → Bebidas `ProductCount == 1`, Gaseosas `ProductCount == 1` (directos; nada se acumula).
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CategoryApiTests"
  ```
  Expected: FAIL de compilación (`CategoryResponse`, `CategoriesResponse` no existen).
- [ ] **Step 3: GREEN.** Implementar lo de **Interfaces**, mapear en `Program.cs`, registrar los dos handlers.
- [ ] **Step 4:**
  ```powershell
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CategoryApiTests"
  ```
  Expected: PASS (`CompositionRootTests` sigue viendo 64 permisos y todos los handlers registrados).
- [ ] **Step 5: Commit** `feat(catalog): GET y POST de categorías de producto (spec 2026-10-26 §3.2)`.

### Task 5: POS — categorías y filtro por categoría con descendientes (spec §3.4)

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/IPosCategoryLookup.cs`, `ListPosCategories.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Application/IPosProductLookup.cs:18-19`, `SearchPosProducts.cs:12-13` y `:42-43`, `PosDtos.cs` (DTOs de categoría junto a `PosCatalogProductResponse:105-110`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/IProductRepository.cs:170-173` (`CountActiveAsync` junto a `AnyWithTaxRateAsync`), `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/ProductRepository.cs` (implementación)
- Modify: `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs:54-58` (ruta nueva debajo), `:170-178`
- Create: `src/Bootstrapper/PosCategoryLookup.cs`; Modify: `src/Bootstrapper/PosProductLookup.cs:12-44`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:287-289` (handler) y `:948` (lookup)
- Modify: `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs:285-363` (`FakeProductLookup`; agregar `FakeCategoryLookup`), `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosSaleApiTests.cs:233-237` (`GatedLookup`), `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantTestDoubles.cs` (`FakeProductRepository` graba la búsqueda), `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs:243-278`
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogCategoryTestDoubles.cs`, `PosCategoryLookupTests.cs`; `tests/Modules/Pos/Modules.Pos.UnitTests/PosCategoriesHandlerTests.cs`; `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCategoryApiTests.cs`
- Test también: `tests/Bootstrapper/Bootstrapper.UnitTests/PosProductLookupTests.cs`, `tests/Modules/Pos/Modules.Pos.UnitTests/ProductAndPreviewHandlersTests.cs`

**Interfaces:**
- Consumes: `IProductCategoryRepository.ListAsync(tenantId, true)`, `ListIdsWithDescendantsAsync` (Task 2); `IProductRepository.SearchAsync(..., categoryIds, ...)` (Task 3); `POST /catalog/categories` para sembrar (Task 4).
- Produces:
  - `Task<int> IProductRepository.CountActiveAsync(Guid tenantId, CancellationToken cancellationToken)` — `COUNT(*)` de `products` del tenant con `is_active = true`, sin cargar filas. `FakeProductRepository` lo implementa contando sus productos activos del tenant.
  - `public sealed record PosCategoryRef(Guid Id, string Name, Guid? ParentId, int ProductCount);` `public sealed record PosCategoryList(IReadOnlyList<PosCategoryRef> Items, int Total);` `public interface IPosCategoryLookup { Task<PosCategoryList> ListAsync(Guid tenantId, CancellationToken cancellationToken); }` — comentario: `ProductCount` ya acumulado (activos, con descendientes) y `Total` = activos del tenant con o sin categoría; los calcula el adaptador, nunca el navegador (spec §3.4, §7.1).
  - `IPosProductLookup.SearchAsync(Guid tenantId, string? search, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken)`.
  - `SearchPosProductsQuery(Guid TenantId, string? Search, int Page, int PageSize, Guid? CategoryId = null)` (al final con default: los seis `new SearchPosProductsQuery(TenantId, null, 1, 40)` de las unitarias no cambian); el handler pasa `query.CategoryId`; **sin regla nueva** en el validador.
  - `public sealed record PosCategoryResponse(Guid Id, string Name, Guid? ParentId, int ProductCount);` `public sealed record PosCategoriesResponse(IReadOnlyList<PosCategoryResponse> Items, int Total);` (comentario BFF: `Total` es lo que pinta «Todas las categorías · N productos», incluidos los sin categoría; sumar las raíces en el cliente los perdería). `public sealed record ListPosCategoriesQuery(Guid TenantId) : IQuery<PosCategoriesResponse>`; `ListPosCategoriesHandler(IPosCategoryLookup categories, IExecutionContext executionContext)` — `PosAuthorization.EnsureAuthorized(executionContext, tenantId, PosPermissions.SaleCreate)` → mapea 1:1, `Total` incluido.
  - `PosEndpoints`: `group.MapGet("/categories", ListCategoriesAsync).RequireAuthorization(PosPermissions.SaleCreate).Produces<PosCategoriesResponse>().ProducesProblem(403)`; `SearchProductsAsync` gana `Guid? categoryId = null` y lo pasa.
  - `PosProductLookup(IProductRepository products, IOptionDefinitionRepository options, ITaxRateRepository taxRates, IProductImageLookup images, IProductCategoryRepository categories)`: con `categoryId` nulo → `categoryIds: null`; si no, `ids = await categories.ListIdsWithDescendantsAsync(tenantId, new ProductCategoryId(categoryId.Value), ct)`; **vacío → devuelve `([], 0)` sin consultar productos** (id inexistente o ajeno: página vacía, no 404); si no, pasa `ids.Select(id => id.Value).ToArray()`.
  - `internal sealed class PosCategoryLookup(IProductCategoryRepository categories, IProductRepository products) : IPosCategoryLookup` — `categories.ListAsync(tenantId, onlyActiveProducts: true)`; acumula en memoria: `rollup(c) = directo(c) + Σ rollup(hijas)` (hijas por `ParentId`; recursión con memo o recorrido de hojas a raíces); devuelve en el orden del repositorio (por nombre), **incluidas las de 0**; `Total = await products.CountActiveAsync(tenantId, ct)` (una consulta más, nunca la suma de las raíces).
  - Registro: `services.AddScoped<IQueryHandler<ListPosCategoriesQuery, PosCategoriesResponse>, ListPosCategoriesHandler>();` y `services.AddScoped<IPosCategoryLookup, PosCategoryLookup>();`.
  - Dobles: `FakeProductRepository` gana `IReadOnlyCollection<Guid>? LastCategoryIds` y `int SearchCalls` (los graba `SearchAsync`); `FakeProductCategoryRepository(params (ProductCategory Category, int ActiveCount)[] rows) : IProductCategoryRepository` en `CatalogCategoryTestDoubles.cs` (`ListAsync` con `onlyActiveProducts: true` devuelve los `ActiveCount`, con `false` lanza `NotSupportedException`; `ListTreeAsync`, `ListIdsWithDescendantsAsync` vía `ProductCategoryTree`, `FindAsync`; `Add` lanza); `FakeProductLookup.SearchAsync` acepta `categoryId`, lo guarda en `Guid? LastCategoryId` y no filtra; `FakeCategoryLookup : IPosCategoryLookup` con `List<(Guid TenantId, PosCategoryRef Category)> Categories` y `Dictionary<Guid, int> Totals` por tenant. `PosProductLookupTests.Lookup(...)` gana `FakeProductCategoryRepository? categories = null` (vacío por defecto).
  - `PosApiHarness.CreateProductAsync(..., IReadOnlyDictionary<string, decimal>? prices = null, Guid? categoryId = null)` manda `categoryId`; nuevo `PosApiHarness.CreateCategoryAsync(HttpClient client, Guid tenantId, string name, Guid? parentId = null)` → `POST /catalog/categories` con `tenant.Seeder` (ya tiene `ProductManage`), devuelve el id.

- [ ] **Step 1: RED, adaptadores.** `PosCategoryLookupTests` (Bootstrapper.UnitTests):
  - `ActiveCountsRollUpFromLeavesToRoots` — Bebidas(2) → {Gaseosas(3) → {Cola(1)}, Jugos(1)}, Calzado(0) → `Bebidas 7`, `Gaseosas 4`, `Cola 1`, `Jugos 1`, `Calzado 0` (las cinco viajan, con `ParentId`).
  - `TheTotalCountsEveryActiveProductCategorizedOrNot` — `FakeProductRepository` con 2 productos activos con categoría, 1 activo sin categoría y 1 inactivo (`Deactivate`) del tenant, más 1 activo de otro tenant → `Total == 3`, aunque la suma de las raíces sea 2.
  - `AnotherTenantsCategoriesAreNotListed`.

  `PosProductLookupTests`:
  - `SearchWithACategoryPassesTheCategoryAndItsDescendants` — repo de categorías Bebidas → Gaseosas; `SearchAsync(TenantId, null, Bebidas, 1, 40, Ct)` → `products.LastCategoryIds` = {Bebidas, Gaseosas}.
  - `SearchWithoutACategoryPassesNull` — `LastCategoryIds == null`.
  - `SearchWithAnUnknownCategoryIsEmptyWithoutQueryingProducts` — `Guid.CreateVersion7()` → `Items` vacío, `Total == 0`, `products.SearchCalls == 0`.
  ```powershell
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PosCategoryLookupTests|FullyQualifiedName~PosProductLookupTests"
  ```
  Expected: FAIL de compilación (`PosCategoryLookup`, `IPosCategoryLookup`, el parámetro `categoryId` no existen).
- [ ] **Step 2: RED, handlers.** `PosCategoriesHandlerTests` (Pos.UnitTests; `PosFixtures` y el molde de `ProductAndPreviewHandlersTests`):
  - `ListMapsTheLookupOneToOne` — dos categorías en el fake y `Totals[TenantId] = 9` → `Items` con `Id`, `Name`, `ParentId`, `ProductCount` iguales y `Total == 9`.
  - `ListWithoutSaleCreateIsForbidden` — contexto sin `pos.sale.create` → `RequestForbiddenException` (como `SaleReadAndVoidHandlersTests.cs:58`).
  En `ProductAndPreviewHandlersTests`: `SearchForwardsTheCategoryToTheLookup` — `new SearchPosProductsQuery(TenantId, null, 1, 40, categoryId)` → `products.LastCategoryId == categoryId`.
  ```powershell
  dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosCategoriesHandlerTests|FullyQualifiedName~ProductAndPreviewHandlersTests"
  ```
  Expected: FAIL de compilación.
- [ ] **Step 3: RED, API.** `PosCategoryApiTests` (Pos.IntegrationTests; `PosWorld.ArrangeAsync` + `CreateCategoryAsync` con `world.Tenant.Seeder`: Bebidas → Gaseosas; productos vía `CreateProductAsync(..., categoryId:)`: «Cola» en Gaseosas, «Agua» en Bebidas, «Vieja» en Gaseosas desactivada con `POST /catalog/products/{id}/deactivate` por el seeder; `world.Jabon` queda sin categoría; cajero por `InviteCashierAsync(factory, database, world.Tenant, CashierPermissions)`):
  - `TheCashierListsCategoriesWithActiveRollupsAndFiltersTheGrid` — `cashier.Client` `GET {Url}/categories` → 200; Bebidas `ProductCount == 2` (Agua + Cola), Gaseosas `1`, con `ParentId`; `Total == 5` (Agua, Cola y los tres de `PosWorld` sin categoría —Shampoo, Avena, Jabón—; «Vieja», inactiva, no cuenta: la suma de raíces daría 2); el mismo cajero `GET {Url}/products?categoryId={Gaseosas}` → 200 con Cola (spec §3.5: `cashier` lee ambos sin `catalog.product.read`); un cliente con sólo `pos.sale.read` → 403 en `/categories`.
  - `ProductsFilterByCategoryIncludesDescendantsAndCombinesWithSearch` — `?categoryId={Bebidas}` → `Total == 2`, nombres {Cola, Agua}, sin Jabón; `?categoryId={Gaseosas}` → sólo Cola; `?search=ag&categoryId={Bebidas}` → sólo Agua.
  - `InactiveProductsNeitherCountNorAppearInTheFilteredGrid` — «Vieja» no está en `?categoryId={Gaseosas}` ni suma en `/categories`; una categoría «Vacía» (raíz sin productos) sale con `0`.
  - `AnUnknownOrForeignCategoryGivesAnEmptyPage` — `?categoryId={Guid.CreateVersion7()}` → 200, `Items` vacío, `Total == 0`; una categoría creada en un segundo tenant (`RegisterTenantAsync` otra vez) → 200 vacío; y `/categories` del primero no la lista.
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosCategoryApiTests"
  ```
  Expected: FAIL de compilación (`PosCategoriesResponse`, `CreateCategoryAsync` no existen).
- [ ] **Step 4: GREEN.** Implementar lo de **Interfaces**, actualizar `GatedLookup` y `FakeProductLookup` a la firma nueva, registrar handler y lookup.
- [ ] **Step 5:**
  ```powershell
  dotnet build Backend.slnx
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosCategoryApiTests|FullyQualifiedName~PosVariantApiTests|FullyQualifiedName~PosSaleApiTests"
  ```
  Expected: PASS (`PosLayerTests` y `CompositionRootTests` siguen verdes).
- [ ] **Step 6: Commit** `feat(pos): categorías de la caja y filtro de productos por categoría con descendientes (spec 2026-10-26 §3.4)`.

### Task 6: Documentación del contrato en el README

**Files:**
- Modify: `README.md:819-820` (tabla de rutas: fila de `catalog/categories`), `:1279` (sección «Catálogo: opciones, variantes y galería»: párrafo de categorías), `:1636` y la tabla de `:1629-1644` (POS: `categoryId` en `GET /products` y fila `GET /categories`)

- [ ] **Step 1:** Fila nueva debajo de `catalog/options`: `| /api/v1/tenants/{tenantId}/catalog/categories | GET, POST | catalog.product.read / .manage |`. En «Catálogo», un párrafo: árbol por tenant, una categoría por producto (`categoryId`, `null` limpia en el PUT), nombre único entre hermanas sin mayúsculas (dos raíces también), profundidad máxima 10, los cinco códigos de error, `productCount` del catálogo = directos activos e inactivos. En POS: `GET /products` menciona `categoryId` (con descendientes; id ajeno = página vacía) y fila `GET /categories` (`pos.sale.create`, «categorías con el conteo de productos activos acumulado, incluidas las de 0, y `total` de activos del tenant con o sin categoría»).
- [ ] **Step 2:** Verificar que no se tocó `docs/estado.md` (`git status --short docs/`).
- [ ] **Step 3: Commit** `docs(catalog): categorías de producto en el README (spec 2026-10-26 §3)`.

### Task 7: Cierre

**Files:** ninguno nuevo (handoff en el reporte).

- [ ] **Step 1: Build, unitarias y arquitectura.**
  ```powershell
  dotnet build Backend.slnx
  dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests
  dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests
  dotnet ef migrations has-pending-model-changes --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext
  ```
  Expected: build sin warnings nuevos; todo PASS; sin cambios pendientes de modelo.
- [ ] **Step 2: Integración filtrada** (las clases nuevas y las que tocan producto; la suite entera tarda decenas de minutos y sólo corre si el owner la pide):
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductCategoryPersistenceTests|FullyQualifiedName~CategoryApiTests|FullyQualifiedName~ProductDetailsApiTests|FullyQualifiedName~ProductApiTests|FullyQualifiedName~ProductWriteApiTests|FullyQualifiedName~CatalogVariantPersistenceTests"
  dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosCategoryApiTests|FullyQualifiedName~PosVariantApiTests|FullyQualifiedName~PosSaleApiTests"
  ```
  Expected: PASS.
- [ ] **Step 3: Handoff** con: salidas RED/GREEN de cada tarea, commits, la verificación de rama de cada commit, y para el plan de frontend: `categoryId` en todas las respuestas de producto, `GET/POST /catalog/categories`, `GET /pos/categories` (con `total`) y `categoryId` en `GET /pos/products`, con los cinco códigos de error.

---

**Nota final (no es tarea de la rama):** al mergear a `develop`, actualizar `docs/estado.md` —fila del spec `2026-10-26 categorías de producto`: «Construido (backend)», con el commit— y el Registro, en el mismo merge o en un `docs:` inmediatamente después (regla de `CLAUDE.md`, «`docs/estado.md` se actualiza en `develop`, al mergear»). Despliegue: backend antes que frontend (spec §4.5).
