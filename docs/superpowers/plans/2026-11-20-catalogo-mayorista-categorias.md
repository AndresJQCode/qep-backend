# Categorías en el catálogo del mayorista — plan

> **Para agentes:** SUB-SKILL REQUERIDA: `superpowers:subagent-driven-development` o `superpowers:executing-plans`. Los pasos usan `- [ ]`. Las reglas de ejecución del `CLAUDE.md` de la raíz del workspace mandan sobre el skill: 2 bloques con un solo implementador, una revisión por bloque, integración filtrada por nombre.

**Objetivo:** que el distribuidor lea el árbol de categorías que su lista de precios le deja ver y filtre el catálogo del mayorista por `categoryId`.

**Arquitectura:** `WholesaleOffers` (Wholesale.Application) resuelve el árbol visible y valida el filtro; Purchases expone un puerto y un endpoint nuevos y suma `categoryId` al catálogo existente; `PurchasesWholesaleCatalog` (Bootstrapper) adapta. Aditivo: sin `categoryId` el catálogo responde igual que antes.

**Stack:** .NET 10, xUnit, módulos `Modules.Wholesale.*`, `Modules.Purchases.*`, `Bootstrapper`.

**Spec:** `docs/superpowers/specs/2026-11-20-catalogo-mayorista-categorias.md`

## Restricciones globales

- Ruta nueva: `GET /api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog/categories`, permiso `purchases.order.manage`, mismas reglas de proveedor que el catálogo (404 `suppliers.supplier.not_found`, 422 `purchases.order.supplier_not_linked`, 403 `wholesale.relationship.inactive`).
- Respuesta: `{ items: [{ id, name, parentId }] }`, plano, ordenado por nombre dentro de cada padre (lo da `IWholesaleCatalogReader.ListCategoriesAsync`; no se reordena).
- Código nuevo: `wholesale.category.not_visible`, 422, sin decir si la categoría es ajena, borrada o no permitida. **Decisión pendiente del owner** (alternativa: página vacía); se implementa el 422 y se marca.
- `categoryId` se intersecta con la visibilidad: nunca amplía lo que la lista permite. `Total` respeta el filtro.
- `SupplierCatalogItemDto` no cambia. Sin `productCount`.
- `Modules.Wholesale.Application` no referencia Catalog: el recorte de descendientes se hace sobre `WholesaleCategoryNode`, no con `ProductCategoryTree` (el spec lo nombraba; no aplica por capas).

## Revisión de foco

- Lista `Categories` con un hijo suelto y su ancestro fuera: el hijo sale con `parentId = null`, el ancestro no aparece.
- `categoryId` de otro tenant o inexistente: 422 igual que uno no permitido (no filtra existencia).
- `categoryId` hijo de una categoría permitida: es válido (los descendientes se ven).
- Lista `AllActive` y `categoryId` válido: filtra, no 422.
- Árbol con ciclo o padre faltante en los datos: no se cuelga (conjunto de visitados).

---

## Bloque 1 — Wholesale + Purchases (un implementador)

### Task 1: `WholesaleOffers` — árbol visible y filtro

**Archivos:**
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Domain/WholesaleErrorCodes.cs` (junto a `ProductNotVisible`, línea ~56)
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/WholesaleOffers.cs`
- Probar: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleOffersTests.cs` (usa `FakeCatalogReader.AddCategory`, `UseCategories`, `SeedActive`)

**Interfaces:**
- Produce: `WholesaleErrorCodes.CategoryNotVisible = "wholesale.category.not_visible"`.
- Produce: `Task<IReadOnlyList<WholesaleCategoryNode>> WholesaleOffers.ListVisibleCategoriesAsync(Guid wholesalerTenantId, Guid buyerTenantId, CancellationToken cancellationToken)`.
- Cambia: `WholesaleOffers.SearchAsync(Guid wholesalerTenantId, Guid buyerTenantId, string? search, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken)` — `categoryId` nuevo, tras `search`.

- [ ] **Paso 1: pruebas que fallan** (nombres y aserciones, con `_catalog.AddCategory` y la fábrica `Offers()`):
  - `VisibleCategoriesOfAnAllActiveListAreTheWholeTree`: tres categorías (raíz, hijo, otra raíz) → devuelve las tres, en el orden del lector.
  - `VisibleCategoriesOfACategoriesListAreItsRootsAndDescendants`: lista con `[hijo]` de `raíz > hijo > nieto`, más `otra` → devuelve `hijo` con `ParentId == null` y `nieto`; `raíz` y `otra` ausentes.
  - `VisibleCategoriesWithoutRelationshipIsRelationshipInactive`: `RequestForbiddenException` con `WholesaleErrorCodes.RelationshipInactive`.
  - `SearchWithAVisibleCategoryPassesItToTheReader`: lista `AllActive`, `categoryId = hijo` → `_catalog.SearchCalls.Single().categoryIds` es `[hijo]` y los productos fuera de su camino no vienen.
  - `SearchWithACategoryOutsideTheListIs422NotVisible`: lista `Categories [a]`, `categoryId = b` → `WholesaleDomainException` con `WholesaleErrorCodes.CategoryNotVisible`, y `_catalog.SearchCalls` vacío.
  - `SearchWithAForeignOrUnknownCategoryIs422NotVisible`: `Guid.CreateVersion7()` con lista `AllActive` → mismo código.
  - `SearchWithADescendantOfAListedCategoryIsAllowed`: lista `Categories [raíz]`, `categoryId = nieto` → pasa y devuelve sólo los productos del nieto.
  - `SearchWithoutCategoryIdBehavesAsBefore`: los `categoryIds` que llegan al lector son `terms.VisibleCategoryIds` (compatibilidad).
- [ ] **Paso 2:** correr `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~WholesaleOffersTests"`. Esperado: no compila (RED por compilación; sin contenedores).
- [ ] **Paso 3: implementar** en `WholesaleOffers`:
  - `ListVisibleCategoriesAsync`: `ResolveTermsAsync`, luego `catalog.ListCategoriesAsync`; con `VisibleCategoryIds == null` devuelve todo; si no, el conjunto = cada id de la lista **más sus descendientes** (BFS por `ParentId` con conjunto de visitados), conservando el orden del lector, y todo nodo cuyo `ParentId` no esté en el conjunto sale con `ParentId = null` (`node with { ParentId = null }`). Un helper privado `static` en el mismo archivo.
  - `SearchAsync`: con `categoryId` no nulo, validar contra `ListVisibleCategoriesAsync`-equivalente (reusar el mismo cálculo privado, sin segunda llamada a `ResolveTermsAsync`); si no está, `throw new WholesaleDomainException(WholesaleErrorCodes.CategoryNotVisible, "La categoría ya no está disponible para ti.")`; si está, pasar `[categoryId]` como `categoryIds` a `SearchSellableAsync` (el lector expande descendientes). Sin `categoryId`, `terms.VisibleCategoryIds` como hoy.
  - Actualizar los llamadores existentes de `SearchAsync` en pruebas y en `PurchasesWholesaleCatalog` (Task 2) al parámetro nuevo.
- [ ] **Paso 4:** misma orden del paso 2. Esperado: PASS, todo `WholesaleOffersTests` en verde.
- [ ] **Paso 5: commit** `feat(wholesale): visible category tree and category filter for the buyer's catalog`.

### Task 2: Purchases — puerto, adaptador, handler y endpoint

**Archivos:**
- Modificar: `src/Modules/Purchases/Modules.Purchases.Application/WholesalePorts.cs` (`IWholesaleCatalog`)
- Modificar: `src/Bootstrapper/PurchasesWholesaleCatalog.cs`
- Modificar: `src/Modules/Purchases/Modules.Purchases.Application/ListSupplierCatalog.cs` (query y handler)
- Crear: `src/Modules/Purchases/Modules.Purchases.Application/ListSupplierCatalogCategories.cs`
- Modificar: `src/Modules/Purchases/Modules.Purchases.Api/PurchaseOrderEndpoints.cs` (junto a la línea 111)
- Probar: `tests/Modules/Purchases/Modules.Purchases.UnitTests/SupplierCatalogTests.cs`, `LinkedPurchasesFakes.cs` (`FakeWholesaleCatalog`), y un caso de integración en `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/`

**Interfaces:**
- Consume: Task 1.
- Produce: `record WholesaleCategory(Guid Id, string Name, Guid? ParentId)` en `WholesalePorts.cs`.
- Produce en `IWholesaleCatalog`: `Task<IReadOnlyList<WholesaleCategory>> ListCategoriesAsync(Guid buyerTenantId, Guid wholesalerTenantId, CancellationToken cancellationToken)`; y `SearchAsync(Guid buyerTenantId, Guid wholesalerTenantId, string? search, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken)`.
- Produce: `record ListSupplierCatalogCategoriesQuery(Guid TenantId, Guid SupplierId) : IQuery<SupplierCatalogCategoriesDto>`; `record SupplierCatalogCategoriesDto(IReadOnlyList<SupplierCatalogCategoryDto> Items)`; `record SupplierCatalogCategoryDto(Guid Id, string Name, Guid? ParentId)`; `ListSupplierCatalogCategoriesHandler` con las mismas dependencias y guardas que `ListSupplierCatalogHandler`.
- Cambia: `ListSupplierCatalogQuery(Guid TenantId, Guid SupplierId, string? Search, int Page, int PageSize, Guid? CategoryId = null)`; el endpoint lee `Guid? categoryId = null` de la query string.

- [ ] **Paso 1: pruebas que fallan** (unitarias, con `FakeWholesaleCatalog` ampliado con `Categories` y `Forbidden`):
  - `CategoriesOfANonLinkedSupplierIs422`, `CategoriesOfAnotherTenantsSupplierIs404`, `CategoriesNeedOrderManage`, `CategoriesWithInactiveRelationshipIs403` (reusar el patrón de las pruebas del catálogo).
  - `CategoriesComeInTheOrderTheWholesalerGives`: el handler no reordena.
  - `CatalogPassesCategoryIdToTheWholesaleCatalog`: `Searches` registra `categoryId`.
  - `CatalogWithoutCategoryIdPassesNull`.
  - Integración (por nombre, una prueba): `GET …/catalog/categories` con distribuidor activo y lista `Categories` devuelve sólo lo visible, y `…/catalog?categoryId=<ajena>` responde 422 con `wholesale.category.not_visible`. Seguir el arnés de `PurchasesApiHarness`/`WholesaleApiHarness` de las pruebas de catálogo existentes.
- [ ] **Paso 2:** `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~SupplierCatalogTests"`. Esperado: no compila (RED por compilación).
- [ ] **Paso 3: implementar** firmas arriba. `PurchasesWholesaleCatalog.ListCategoriesAsync` llama `offers.ListVisibleCategoriesAsync(wholesalerTenantId, buyerTenantId, ct)` y mapea; `SearchAsync` reenvía `categoryId`. El endpoint nuevo `MapGet("/{supplierId:guid}/catalog/categories", …)` en el mismo grupo, con `.RequireAuthorization(PurchasesPermissions.OrderManage)`, `Produces<SupplierCatalogCategoriesDto>()` y 403/404/422. Declarar `422` también en el catálogo (ya está). Los errores de Wholesale pasan tal cual (mismo código en Purchases).
- [ ] **Paso 4:** misma orden del paso 2 → PASS; luego la integración filtrada: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~<NombreDeLaPrueba>"`. Antes de lanzarla: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otro, esperar. Primer plano.
- [ ] **Paso 5: commit** `feat(purchases): wholesaler category tree and categoryId filter in the supplier catalog`.

---

## Bloque 2 — Verificación y documentación

### Task 3: suite del módulo, arquitectura y estado

**Archivos:** `docs/estado.md` (en `develop`, al mergear, nunca en la rama de feature); este plan y el spec quedan como están.

- [ ] **Paso 1:** `dotnet build` de la solución → sin errores ni advertencias nuevas.
- [ ] **Paso 2:** una vez, en fila: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`, `…/Purchases.UnitTests`, `tests/ArchitectureTests` (validar la ruta con `Test-Path` antes). Esperado: todo verde. La integración de `Purchases` y `Wholesale` completa corre **una vez**, con `-- xUnit.MaxParallelThreads=4`, sólo antes de mergear a `develop`.
- [ ] **Paso 3:** revisión fuerte única de la rama (modelo más capaz).
- [ ] **Paso 4:** ya en `develop`, anotar en `docs/estado.md` la ruta nueva, `categoryId`, `wholesale.category.not_visible` y la decisión 422 vs. página vacía como pendiente del owner. Commit `docs: wholesaler categories in the purchase catalog`.

## Autorrevisión

- Cobertura del spec: ruta nueva (T2), visibilidad `AllActive`/`Categories` con huérfano en `parentId = null` (T1), filtro con descendientes e intersección (T1), 422 (T1/T2), compatibilidad sin `categoryId` (T1/T2), 403 en las dos rutas (T1/T2), sin cambios en `SupplierCatalogItemDto` (T2).
- Nombres consistentes entre tareas: `ListVisibleCategoriesAsync`, `ListCategoriesAsync`, `CategoryNotVisible`, `SupplierCatalogCategoriesDto`.
- Desviación del spec: el recorte no usa `ProductCategoryTree` (Wholesale no referencia Catalog); usa un BFS local sobre `WholesaleCategoryNode`.
