# Catálogo de permisos con entidad y acción — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GET /api/v1/tenants/{tenantId}/authorization/catalog` describe la entidad, la acción y los nombres visibles de cada permiso, y qué acciones son columna de la matriz de `/roles`.

**Architecture:** Cada `PermissionDefinition` declara su forma (`Shape`: entidad, nombre de la entidad, acción) donde nace, en `QepServiceCollectionExtensions.cs`, igual que `RequiredModules`. Los nombres de acciones y módulos, y la lista de acciones columna, viven en una sola tabla de Authorization (`PermissionVocabulary`). Pruebas de arquitectura sobre las definiciones registradas impiden un permiso sin forma, una entidad que repite acción o un nombre faltante.

**Tech Stack:** .NET 10, minimal APIs, xUnit v3, Testcontainers (integración).

**Spec:** `docs/superpowers/specs/2026-11-16-permission-catalog-entity-action-design.md`

## Global Constraints

- Los campos de la respuesta se **suman**; los cinco de hoy (`permission`, `displayName`, `description`, `category`, `riskLevel`) no cambian de nombre ni de tipo.
- Acciones columna, en este orden: `read` «Ver», `manage` «Gestionar», `delete` «Borrar», `import` «Importar».
- `tenancy.settings.update` → `action: "manage"`. `pos.sale.create`, `pos.sale.void`, `pos.sale.discount`, `pos.sale.customer`, `pos.register.operate` → acción propia, **no** `manage`.
- La acción describe lo que hace el permiso; no copia el verbo de la clave.
- Los reportes son entidades del módulo `Reporting` con acción `read`, no una columna.
- Etiquetas en español neutro.
- TDD: RED antes que GREEN, con la salida literal de los dos. Un RED de compilación se prueba con la salida del build.
- Integración filtrada por nombre de prueba, nunca dos corridas a la vez (`Get-Process testhost, dotnet` antes de lanzar). Si `Api.exe` está corriendo, no se detiene: se avisa.
- Commits convencionales, sin atribución de IA.

## Review Focus

1. Un permiso sin metadata (el fallback `uncategorized` de `RoleCatalog`, `Shape` nulo) no rompe el endpoint: sale con `entity` = la clave, `entityLabel` = su `displayName`, `action` = `"other"`, `actionLabel` = su `displayName`. Prueba en Task 3.
2. Una entidad con permisos en dos `category` distintas partiría su fila en dos grupos: prueba de arquitectura en Task 1.
3. Las claves de dos partes (`advisorship.read`, `advisorship.invite`, `advisorship.manage`) quedan con forma igual que las demás: cubierto por la prueba de completitud de Task 1.
4. `operator.*` no llega como casilla, pero igual lleva forma; la prueba de completitud no tiene excepciones.
5. `CatalogVersion` cambia si cambia la forma de un permiso: prueba unitaria en Task 3.

## Archivos

| Archivo | Qué |
| --- | --- |
| `src/Modules/Authorization/Modules.Authorization.Application/PermissionShape.cs` (nuevo) | `PermissionShape` |
| `src/Modules/Authorization/Modules.Authorization.Application/PermissionVocabulary.cs` (nuevo) | columnas, nombres de acción y de módulo |
| `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs` | `Shape` en `PermissionDefinition`; hash |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | `Shape = new(...)` en las 85 definiciones |
| `src/Api/AuthorizationCatalogEndpoints.cs` | respuesta |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | completitud |
| `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCatalogTests.cs` | hash |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` | contrato |

Bloques (regla del workspace): **Bloque 1** = Tasks 1 y 2 (sin contenedores). **Bloque 2** = Task 3. Una revisión por bloque.

---

### Task 1: Forma y vocabulario, con las pruebas de completitud en rojo

**Files:**
- Create: `src/Modules/Authorization/Modules.Authorization.Application/PermissionShape.cs`
- Create: `src/Modules/Authorization/Modules.Authorization.Application/PermissionVocabulary.cs`
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs` (record `PermissionDefinition`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PermissionShape(string Entity, string EntityLabel, string Action);`
  - En `PermissionDefinition`: `public PermissionShape? Shape { get; init; }` (propiedad, no parámetro posicional: los ~10 `new PermissionDefinition(...)` de las pruebas unitarias no cambian).
  - `public static class PermissionVocabulary` con
    - `IReadOnlyList<string> ColumnActions` = `["read", "manage", "delete", "import"]`
    - `IReadOnlyDictionary<string, string> ActionLabels` (todas las acciones usadas; `read` «Ver», `manage` «Gestionar», `delete` «Borrar», `import` «Importar», más las de «Otros»: `merge` «Fusionar», `void` «Anular», …)
    - `IReadOnlyDictionary<string, string> CategoryLabels` (toda `Category` registrada; partir de la tabla de `qep-frontend/src/features/roles/roles-page.tsx` `PERMISSION_CATEGORY_LABEL` y completar las que falten: Messaging, Integrations, Pipeline, Ai, Automations, Billing, Loans, …)
    - `string LabelOfAction(string action)` y `string LabelOfCategory(string category)`; sin entrada devuelven la clave.

- [ ] **Step 1: Escribir las pruebas de arquitectura (fallan por compilación)**

En `CompositionRootTests`, sobre `RegisteredPermissionDefinitions()`:

```csharp
[Fact]
public void EveryRegisteredPermissionDeclaresItsShape()
{
    var missing = RegisteredPermissionDefinitions()
        .Where(d => d.Shape is null
            || string.IsNullOrWhiteSpace(d.Shape.Entity)
            || string.IsNullOrWhiteSpace(d.Shape.EntityLabel)
            || string.IsNullOrWhiteSpace(d.Shape.Action))
        .Select(d => d.Permission).Order(StringComparer.Ordinal).ToArray();
    Assert.True(missing.Length == 0, "Sin Shape: " + string.Join(", ", missing));
}

[Fact] public void NoEntityRepeatsAnAction()            // agrupa por (Entity, Action), Count() > 1 → falla con la lista
[Fact] public void EachEntityLivesInOneCategory()       // agrupa por Entity, Category distintas > 1 → falla
[Fact] public void EveryActionAndCategoryHasALabel()    // toda Shape.Action en ActionLabels, toda Category en CategoryLabels
[Fact] public void EveryEntityHasOneLabel()             // agrupa por Entity, EntityLabel distintos > 1 → falla
```

- [ ] **Step 2: Correr y ver el RED de compilación**

Run: `dotnet build tests/ArchitectureTests/ArchitectureTests`
Expected: `error CS1061` sobre `Shape` / `CS0103` sobre `PermissionVocabulary`.

- [ ] **Step 3: Crear `PermissionShape`, `PermissionVocabulary` y la propiedad `Shape`**

- [ ] **Step 4: Correr y ver el RED de comportamiento**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL en `EveryRegisteredPermissionDeclaresItsShape` (lista los 85 permisos) y en `EveryActionAndCategoryHasALabel` si falta alguna categoría; el resto pasa.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Authorization tests/ArchitectureTests
git commit -m "feat(authorization): permission shape and vocabulary for the roles matrix"
```

### Task 2: Forma de las 85 definiciones

**Files:**
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (cada `services.AddSingleton(new PermissionDefinition(...))`)
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/PermissionVocabulary.cs` (etiquetas que falten)

**Interfaces:**
- Consumes: `PermissionShape`, `PermissionVocabulary` (Task 1).

- [ ] **Step 1: Agregar `{ Shape = new(entity, entityLabel, action) }` a cada definición**

Convención de `Entity`: `módulo.entidad` en minúsculas. Fijados por el spec:
- `tenancy.settings.read` → `("tenancy.settings", "Configuración", "read")`; `tenancy.settings.update` → `(…, "manage")`.
- `advisorship.read` → `("tenancy.members", "Miembros", "read")`, `advisorship.manage` → `(…, "manage")`, `advisorship.invite` → `(…, "invite")`; `advisorship.roles.manage` → `("tenancy.roles", "Roles", "manage")`.
- `reporting.orders.read` → `("reporting.orders", "Pedidos", "read")`, y así cada reporte.
- `pos.sale.read` → `("pos.sale", "Ventas", "read")`; `pos.sale.create|void|discount|customer` → acciones `create`, `void`, `discount`, `customer`; `pos.register.read` → `("pos.register", "Cajas", "read")`, `pos.register.operate` → `operate`.
- `catalog.product.cost.read|manage` → `("catalog.product_cost", "Costos", "read"|"manage")`.
- El resto: la acción dice lo que hace; un verbo de escritura que es editar datos va como `manage`, uno que no (subir, fusionar, revocar, moderar, purgar) queda con su propia acción. `storage.file.upload` → `upload` (decisión del builder, marcada para el owner).

- [ ] **Step 2: Completar `ActionLabels` y `CategoryLabels` con lo que pidan las pruebas**

- [ ] **Step 3: Correr y ver el GREEN**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: PASS, las 5 nuevas incluidas.

- [ ] **Step 4: Commit**

```bash
git add src/Bootstrapper src/Modules/Authorization
git commit -m "feat(authorization): declare entity and action for every permission"
```

### Task 3: El contrato del catálogo

**Files:**
- Modify: `src/Api/AuthorizationCatalogEndpoints.cs` (`AuthorizationCatalogResponse`, `PermissionCatalogItemResponse`, el `Select` de permisos)
- Modify: `src/Modules/Authorization/Modules.Authorization.Application/RoleCatalog.cs` (`ComputeCatalogVersion`)
- Test: `tests/Modules/Authorization/Modules.Authorization.UnitTests/RoleCatalogTests.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs`

**Interfaces:**
- Consumes: `PermissionDefinition.Shape`, `PermissionVocabulary` (Tasks 1–2).
- Produces (contrato HTTP que consume `qep-frontend`):
  - `public sealed record CatalogActionResponse(string Key, string Label);`
  - `AuthorizationCatalogResponse(string CatalogVersion, IReadOnlyCollection<RoleCatalogItemResponse> Roles, IReadOnlyCollection<PermissionCatalogItemResponse> Permissions, IReadOnlyCollection<CatalogActionResponse> Actions)`
  - `PermissionCatalogItemResponse(string Permission, string DisplayName, string Description, string Category, string RiskLevel, string CategoryLabel, string Entity, string EntityLabel, string Action, string ActionLabel)`

- [ ] **Step 1: Prueba unitaria del hash (RED)**

`TheCatalogVersionChangesWithThePermissionShape`: dos `RoleCatalog` con la misma definición, una con `Shape = new("a.b", "B", "read")` y otra con `Action = "manage"` → `CatalogVersion` distinto.

- [ ] **Step 2: Prueba de integración del contrato (RED)**

Sumar a `CatalogPermissionPayload` los campos nuevos y un `CatalogActionPayload(string Key, string Label)` a `CatalogPayload`. Prueba `TheCatalogDescribesEntityAndActionOfEachPermission`:

```csharp
Assert.Equal(["read", "manage", "delete", "import"], catalog!.Actions.Select(a => a.Key));
Assert.Equal(["Ver", "Gestionar", "Borrar", "Importar"], catalog.Actions.Select(a => a.Label));
var merge = catalog.Permissions.Single(p => p.Permission == "customers.customer.merge");
Assert.Equal(("customers.customer", "merge", "Fusionar", "Clientes"), (merge.Entity, merge.Action, merge.ActionLabel, merge.CategoryLabel));
Assert.Equal("manage", catalog.Permissions.Single(p => p.Permission == "tenancy.settings.update").Action);
Assert.DoesNotContain(catalog.Permissions.Single(p => p.Permission == "pos.sale.void").Action, catalog.Actions.Select(a => a.Key));
Assert.All(catalog.Permissions, p => Assert.False(string.IsNullOrWhiteSpace(p.EntityLabel)));
```

(El tenant de prueba necesita los módulos `customers` y `pos` habilitados para que esos permisos lleguen; copiar el arreglo de `TheCatalogNamesThePosPermissionsAndTheCashierRole`.)

- [ ] **Step 3: Correr y ver el RED**

Run: `dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --filter "FullyQualifiedName~RoleCatalogTests"` → FAIL.
Run: `dotnet build tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests` → error de compilación de la respuesta (RED de compilación, sin contenedores).

- [ ] **Step 4: Implementar**

- `ComputeCatalogVersion` agrega `|Entity|EntityLabel|Action` de cada `Shape` (vacío si nulo).
- El endpoint arma `Actions` desde `PermissionVocabulary.ColumnActions` + `LabelOfAction`, y cada permiso con `LabelOfCategory(Category)` y su `Shape`. `Shape` nulo → `Entity = Permission`, `EntityLabel = DisplayName`, `Action = "other"`, `ActionLabel = DisplayName` (Review Focus 1).

- [ ] **Step 5: Correr y ver el GREEN**

Run: `dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests` → PASS.
Run (contenedores; mirar antes `Get-Process testhost, dotnet`): `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests" -- xUnit.MaxParallelThreads=4` → PASS, incluidas las pruebas viejas de la clase.

- [ ] **Step 6: Commit**

```bash
git add src/Api src/Modules/Authorization tests/Modules
git commit -m "feat(authorization): catalog exposes entity, action and matrix columns"
```

### Cierre

- [ ] Antes de mergear a `develop`: `dotnet test` de `Modules.Authorization.UnitTests`, `ArchitectureTests` y `Modules.Tenancy.IntegrationTests` (este último, sólo si el owner lo pide: memoria «integración completa sólo si la pido»).
- [ ] `docs/estado.md` se actualiza en `develop`, al mergear.
