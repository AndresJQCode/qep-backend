# Cotizaciones de proyecto (backend) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de presupuesto de pruebas del workspace (`templates/qep/CLAUDE.md`, owner 2026-11-12) mandan sobre el skill:** tres bloques, un implementador (Sonnet) por bloque, una revisión por bloque y una revisión final de toda la rama (subagente de solo lectura, sin el contexto de quien implementó).

**Goal:** Con el módulo `projects`, una cotización `kind = Project` lleva un alcance (fases e ítems con ids estables), un precio global o por fase y una sola línea del servicio con precio = total; se envía, se convierte y su pedido se cobra y factura como cualquiera, con las líneas bloqueadas.

**Architecture:** Todo en Quotations: `QuotationKind` y el value object `ProjectScope` (jsonb) en `Quotation` y `Order`; `Quotation.ReplaceProjectScope` mantiene la única línea; los recálculos de escalas se saltan en `Project`; las guardas de módulo van en los handlers de cotización. Tenancy sólo suma la clave `projects`.

**Tech Stack:** .NET 10, EF Core + Npgsql (jsonb), FluentValidation, xUnit v3, Testcontainers, Typst (`quotation.typ`).

**Spec:** `docs/superpowers/specs/2026-11-26-cotizaciones-de-proyecto-design.md` (revisado contra `4dc94b00c`). Contrato: §6 más la versión «después» de §12 (A–H), que el owner tiene que aprobar; si rechaza una, sólo cambia la tarea que la nombra.

## Global Constraints

- **Worktree y rama:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\cotizaciones-de-proyecto`, rama `feature/cotizaciones-de-proyecto`. Todo comando empieza con `Set-Location` a esa ruta. Nunca tocar `qep-backend` (el checkout de `develop`). `git branch --show-current` antes de cada commit.
- **Commits:** uno por tarea, conventional commits; el mensaje termina, tras una línea en blanco, con las dos líneas `Session: <título>` y `Co-Authored-By: Claude <modelo> <noreply@anthropic.com>` que pasa quien despacha. La rama **no toca `docs/estado.md`**.
- **TDD estricto:** RED antes que GREEN con la salida literal. Un RED que falla por compilación sólo necesita la salida de `dotnet build` (sin contenedores). La integración corre en GREEN, filtrada por nombre mientras se itera (`--filter "FullyQualifiedName~<Prueba>"`); la clase entera, una vez al final de cada bloque.
- **Corridas:** antes de una corrida de integración, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una ajena, se espera, no se detiene. Nunca dos corridas a la vez. Siempre en primer plano. `$env:Seed__Enabled = "false"` en la sesión. `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- **Comandos:** PowerShell. Build: `dotnet build Backend.slnx -c Debug 2>&1 | Select-Object -Last 30`. Unitarias: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~<Prueba>"`. Integración: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~<Prueba>" -- xUnit.MaxParallelThreads=4`.
- **Merge:** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos. Las pruebas de `tests/ArchitectureTests/` no se tocan; un aviso «Requiere autorización del owner» se le muestra al owner.
- **Migraciones:** `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`. Aditivas (expand): ninguna columna nueva sin default ni `NOT NULL` sin default.
- **Valores fijos del spec:** módulo `projects`, dependencias `[quotations, orders]`, fuera de `DefaultForNewTenants`, último de `All` (después de `wms`). Límites P6: 30 fases, 300 ítems (fases + sueltos), nombre de fase 1–200, texto de ítem 1–2000. Códigos: `quotation.project.service_invalid`, `quotation.project.items_not_allowed`, `quotation.project.incomplete`, `order.project.items_locked` (todos 422); `tenancy.module_not_enabled` (403); `validation.failed` (422).
- **Claves de `errors`:** `Project.ServiceVariantId`, `Project.PricingMode`, `Project.GlobalPrice`, `Project.PricesCurrency`, `Project.Phases`, `Project.Phases[i].Id|Name|Price`, `Project.Phases[i].Items[j].Id|Text`, `Project.Items`, `Project.Items[j].Id|Text`, `Project`, `Kind` (alta), `kind` (filtro de listas). `ApiExceptionHandler` agrupa por `PropertyName` tal cual: el validador hijo se cuelga con `RuleFor(e => e.Project!).SetValidator(...)` para que el prefijo salga `Project.`.
- **Mensajes:** los textos del historial y de las excepciones visibles en español colombiano tuteando; los códigos, enums (`Standard`, `Project`, `Global`, `PerPhase`) y rutas tal cual.

## Review Focus

1. **Cambiar de cuenta a otra moneda en una cotización `Project` guardada**, con y sin `project` en el cuerpo: nunca `quotation.quotation.repricing_incomplete`; sin `project`, precios en `null` y línea en 0; con `pricesCurrency` igual a la moneda nueva, aplica. Pruebas en Tarea 3 (`AProjectChangingCurrencyClearsItsPricesWithoutTheCatalog`) y Tarea 6 (`SavingWithAnAccountInAnotherCurrencyAppliesPricesTypedInIt`).
2. **Quitar la cuenta o elegir una empresa sin cuenta al editar**: la moneda sigue siendo la del documento, no la del tenant (§12-A). Prueba en Tarea 6 (`RemovingTheAccountKeepsTheDocumentCurrency`).
3. **Un cambio de la regla de redondeo del tenant después de convertir**: el pedido `Project` no cambia de precio al editarlo (OrderPricingRecalculation sale antes de `UsePriceRounding`). Prueba en Tarea 8 (`AProjectOrderIsNeverRepriced`).
4. **Borrar el producto del servicio con un borrador `Project` abierto**: el borrador conserva su línea y su total. Prueba en Tarea 10 (`DeletingTheServiceProductKeepsTheProjectDraftLine`).
5. **Un pod viejo durante el rollout**: no se prende `projects` hasta terminar; la migración es aditiva y el default `Standard` cubre los `INSERT` crudos (`ExportLoadSeeder`). Prueba en Tarea 4 (`ExistingRowsAreStandardAndARawInsertWithoutKindIsStandard`).

---

## Bloque 1 — Dominio, módulo y persistencia (Tareas 1 a 4, un implementador)

### Task 1: Clave de módulo `projects`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (`TenantModuleKeys.Projects`, `All`, `Dependencies`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (`ConfigureTenantModule`, `CK_tenant_modules_module_key`)
- Create: migración `AddProjectsModuleKey` en `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`; listas literales en `OperatorReadHandlerTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`, `OperatorConsoleApiTests.cs`, `SeedStartupTests.cs` (las cinco de `SeedEnablesEveryModule` y hermanas)

**Interfaces:**
- Produces: `public static readonly TenantModuleKey Projects = TenantModuleKey.Define("projects");` — lo usan las Tareas 7 y 8.

- [ ] **Step 1: Pruebas.** En `TenantModuleKeysTests`: `AllKeepsTheContractOrder` termina en `"wms", "projects"`; reemplaza `WmsIsLastDependsOnCatalogAndIsOffForNewTenants` por `WmsDependsOnCatalogAndIsOffForNewTenants` (sin la aserción de último) y agrega:
```csharp
[Fact]
public void ProjectsIsLastDependsOnQuotationsAndOrdersAndIsOffForNewTenants()
{
    Assert.Equal("projects", TenantModuleKeys.All[^1].Value);
    Assert.Equal([TenantModuleKeys.Quotations, TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Projects));
    Assert.DoesNotContain(TenantModuleKeys.Projects, TenantModuleKeys.DefaultForNewTenants);
}
```
  Suma `"projects"` a las listas literales de las otras pruebas (al final en las de orden de `All`; en orden alfabético, entre `"pos"` y `"purchases"`, en `SeedStartupTests`; y entre los apagados por defecto en los `Assert.All` de `TenantModulesApiTests`/`OperatorConsoleApiTests`).
- [ ] **Step 2: RED** — `dotnet build` falla con `CS0117: 'TenantModuleKeys' does not contain a definition for 'Projects'`.
- [ ] **Step 3: Implementa.** Clave con su `<summary>` (spec 2026-11-26), al final de `All`, `[Projects] = [Quotations, Orders]`. El `CHECK` del `DbContext` suma `'projects'` al final. Genera la migración y reescribe su cuerpo como `20261126021100_AddWmsModuleKey.cs`: `Up` = drop + add con la lista nueva; `Down` = `DELETE FROM tenancy.tenant_modules WHERE module_key = 'projects';` y la lista con `wms` como última.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"` en verde. Las de integración de Tenancy corren al final del bloque.
- [ ] **Step 5: Commit** — `feat(tenancy): add the projects module key`

### Task 2: Value object `ProjectScope`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ProjectScope.cs` (con `QuotationKind`, `ProjectPricingMode`, `ProjectPhase`, `ProjectItem`, `ProjectScopeLimits`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ProjectScopeTests.cs`

**Interfaces:**
- Produces:
  - `public enum QuotationKind { Standard, Project }`; `public enum ProjectPricingMode { Global, PerPhase }`
  - `public sealed record ProjectItem(Guid Id, string Text)`
  - `public sealed record ProjectPhase(Guid Id, string Name, decimal? Price, IReadOnlyList<ProjectItem> Items)`
  - `public sealed record ProjectScope(Guid? ServiceVariantId, ProjectPricingMode PricingMode, decimal? GlobalPrice, IReadOnlyList<ProjectPhase> Phases, IReadOnlyList<ProjectItem> Items)` con:
    - `public static ProjectScope Empty { get; }` — `Global`, sin servicio, listas vacías.
    - `public static ProjectScope Create(Guid? serviceVariantId, ProjectPricingMode mode, decimal? globalPrice, IReadOnlyList<ProjectPhase> phases, IReadOnlyList<ProjectItem> items)` — lanza `ArgumentException` si se rompe una regla de §6.3 (el validador de la Tarea 5 da el 422 de campo antes; esto es la red del dominio, sin código de contrato nuevo).
    - `public decimal? Total { get; }` — `Global`: `GlobalPrice`; `PerPhase`: suma si ninguna fase tiene `null`, si no `null`.
    - `public bool IsComplete => ServiceVariantId is not null && Total is not null;`
    - `public ProjectScope RoundedWith(PriceRounding rounding)` — cada precio por separado con `rounding.Apply`.
    - `public ProjectScope WithoutPrices()` — `GlobalPrice` y todo `Price` en `null`.
    - `public ProjectScope WithPricesOf(ProjectScope saved)` — esta estructura con los precios de `saved`: `GlobalPrice` el guardado y cada fase el de la fase guardada con el mismo `Id` (o `null`); si el modo cambió, los del otro modo quedan en `null` (§12-H).
    - `public bool HasPrices => GlobalPrice is not null || Phases.Any(p => p.Price is not null);`
    - `public int ItemCount => Items.Count + Phases.Sum(p => p.Items.Count);`
    - Igualdad por valor de las listas: `Equals`/`GetHashCode` propios sobre la serialización o `SequenceEqual` (los records no comparan listas).
  - `public static class ProjectScopeLimits { public const int Phases = 30; public const int Items = 300; public const int PhaseNameMaxLength = 200; public const int ItemTextMaxLength = 2000; }`

- [ ] **Step 1: Pruebas** (`ProjectScopeTests`):
  - `GlobalTotalIsTheGlobalPrice` — `Create(id, Global, 1000m, [fase con Price null], [ítem])`.`Total == 1000m`.
  - `PerPhaseTotalIsTheSumOrNullWhileAPriceIsMissing` — fases 600 y 400 ⇒ 1000; con una `null` ⇒ `null`.
  - `PerPhaseNeedsAPhaseAndNoLooseItems` — sin fases o con ítem suelto ⇒ `ArgumentException`.
  - `GlobalRejectsPhasePricesAndPerPhaseRejectsAGlobalPrice`.
  - `IdsAreUniqueAcrossPhasesAndItems` — un ítem con el id de una fase ⇒ `ArgumentException`.
  - `LimitsArePinned` — 31 fases, 301 ítems (repartidos en fases en `PerPhase`), nombre de 201, texto de 2001, texto vacío o `"  "` ⇒ `ArgumentException`; `"-"` se acepta.
  - `NegativePricesAreRejected`.
  - `RoundingAppliesToEachPhasePrice` — `new PriceRounding(0, PriceRoundingMode.<el de COP en PriceRoundingDefaults>)` sobre 100,4 y 200,4 ⇒ fases 100 y 200, total 300.
  - `WithPricesOfMatchesPhasesById` — fase guardada `a`=500, cuerpo con `a` y `b` nueva ⇒ `a`=500, `b`=`null`, total `null`.
  - `TwoScopesWithTheSameContentAreEqual`.
- [ ] **Step 2: RED** — `dotnet build` falla por `ProjectScope` inexistente.
- [ ] **Step 3: Implementa** con las firmas de arriba.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ProjectScopeTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): add the project scope value object`

### Task 3: `Quotation` y `Order` de proyecto

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (`Create`, `AddItem`, `UpdateItemQuantity`, `RemoveItem`, `ApplyBillingAccount`, `RequiresShipping`, `EnsureSendable`, `EnsureConvertibleToOrder`, `CanBeConvertedToOrder`, `UpdateDetails` sin cambio de firma)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs` (`ChangeUnitPrice`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`FromQuotation`, `AddItem`, `AddGift`, `UpdateItemQuantity`, `ChangeItemUnitPrice`, `RemoveItem`, `RequiresShipping`)
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ProjectErrors.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`, `OrderTests.cs`

**Interfaces:**
- Consumes: Tarea 2.
- Produces:
  - `public static class ProjectErrors { public const string ServiceInvalid = "quotation.project.service_invalid"; public const string ItemsNotAllowed = "quotation.project.items_not_allowed"; public const string Incomplete = "quotation.project.incomplete"; public const string ItemsLocked = "order.project.items_locked"; }`
  - `Quotation.Kind` (`QuotationKind`), `Quotation.ProjectScope` (`ProjectScope?`, `null` en `Standard`), `Quotation.ProjectPricingMode` (`ProjectPricingMode?`, espejo).
  - `Quotation.Create(…mismos parámetros…, QuotationKind kind = QuotationKind.Standard)` — con `Project`: `ProjectScope = ProjectScope.Empty`.
  - `public sealed record ProjectServiceLine(Guid VariantId, Guid ProductId, int TaxPercentage, ProductKind Kind, SalesUnit SalesUnit);` (en `ProjectScope.cs`)
  - `public void ReplaceProjectScope(ProjectScope scope, ProjectServiceLine? service, MemberId updatedBy, DateTimeOffset occurredAt)` — `EnsureEditable`; `InvalidOperationException` si no es `Project` o si `scope.ServiceVariantId` cambió y `service` es `null`/de otra variante; guarda `scope.RoundedWith(PriceRounding)`; deja una sola línea según §5 (`AddItemCore(QuotationItemId.New(), …, quantity 1, total ?? 0, 0, service.TaxPercentage, …)` o `ChangeUnitPrice(total ?? 0)`); `Touch` sólo si algo cambió.
  - `internal void QuotationItem.ChangeUnitPrice(decimal unitPrice, bool chargesVat, string currency, PriceRounding rounding, DateTimeOffset occurredAt)` — como `OrderItem.ChangeUnitPrice`.
  - `Order.Kind`, `Order.ProjectScope` (copiados en `FromQuotation`); `private void EnsureLinesEditable()` que lanza `QuotationsDomainException(ProjectErrors.ItemsLocked, …)` en `Project`, llamado al inicio de `AddItem`, `AddGift`, `UpdateItemQuantity`, `ChangeItemUnitPrice`, `RemoveItem`.

- [ ] **Step 1: Pruebas** (helpers existentes de `QuotationTests`/`OrderTests` para armar documentos):
  - `AProjectQuotationStartsWithAnEmptyScopeAndNoLines`.
  - `ReplacingTheScopeWithAServiceLeavesOneLineAtTheTotal` — línea: variante del servicio, `Quantity == 1`, `UnitPrice == 1000`, `DiscountPercentage == 0`, `Kind == ProductKind.Service`.
  - `ChangingTheServiceSwapsTheLine` y `KeepingTheServiceOnlyChangesThePrice` (mismo `Id` de línea).
  - `NoServiceMeansNoLineAndNoTotalMeansPriceZero`.
  - `PhasePricesAreRoundedWithTheDocumentRule`.
  - `ManualLinesAreRejectedOnAProject` — `AddItem`, `UpdateItemQuantity`, `RemoveItem` ⇒ código `quotation.project.items_not_allowed`.
  - `AProjectChangingCurrencyClearsItsPricesWithoutTheCatalog` — `UpdateDetails` con cuenta USD y `repricing: null` sobre un `Project` COP con total 1000 ⇒ sin excepción, `Currency == "USD"`, `ProjectScope.Total == null`, línea en 0.
  - `AProjectNeverRequiresShipping` — sin líneas y con línea `Service`.
  - `SendingOrConvertingAnIncompleteProjectSaysIncomplete` — sin servicio: `EnsureConvertibleToOrder` lanza `quotation.project.incomplete` (no `items_required`); con servicio y total `null`: `EnsureSendable` lanza el mismo; `CanBeConvertedToOrder == false`.
  - `OrderFromAProjectCopiesKindScopeAndLine` — mismos ids de fase e ítem, `UnitPrice` igual.
  - `AProjectOrderLocksItsLines` — `AddItem`, `AddGift`, `UpdateItemQuantity`, `ChangeItemUnitPrice`, `RemoveItem` ⇒ `order.project.items_locked`; `RequiresShipping == false`.
- [ ] **Step 2: RED** — `dotnet build` (miembros inexistentes).
- [ ] **Step 3: Implementa.** En `ApplyBillingAccount`, rama `Project` al cambiar de moneda: regla `LegacyFor(currency)` como hoy, cada línea `ChangeUnitPrice(0m, …)`, `ProjectScope = ProjectScope.WithoutPrices()`, sin exigir `repricing`; el envío se limpia igual. `RequiresShipping => Kind != QuotationKind.Project && (…regla de hoy…)` en los dos agregados. El chequeo de proyecto de `EnsureConvertibleToOrder` va justo después del de estado.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationTests|FullyQualifiedName~OrderTests|FullyQualifiedName~ProjectScopeTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): add project quotations and orders to the domain`

### Task 4: Mapeo EF y migración de Quotations

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`ConfigureQuotation`, configuración de `Order`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs` y `OrderRepository.cs` (`FindIdsRequiringShippingAsync`)
- Create: migración `AddProjectQuotations` en `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ProjectQuotationsMigrationTests.cs`

**Interfaces:**
- Consumes: Tareas 2 y 3.
- Produces: columnas `quotations.quotations.kind text not null default 'Standard'` (`CK_quotations_kind`), `project_scope jsonb null`, `project_pricing_mode text null` (`CK_quotations_project_pricing_mode`, `IN ('Global','PerPhase')`); `quotations.orders.kind text not null default 'Standard'` (`CK_orders_kind`), `project_scope jsonb null`. Sin índice nuevo.

- [ ] **Step 1: Pruebas** (`ProjectQuotationsMigrationTests`, base propia con `StartEmptyDatabaseAsync`, como `OrderDateMigrationTests`; al final `MigrateAsync(null)`):
  - `ExistingRowsAreStandardAndARawInsertWithoutKindIsStandard` — migra hasta `AddOrdersExportLayoutPhoneDialCode`, inserta una cotización y un pedido con SQL crudo (columnas `NOT NULL` como `OrdersMigrationTests`), migra a la última, `SELECT kind, project_scope` ⇒ `Standard`, `NULL`; un `INSERT` crudo nuevo sin `kind` ⇒ `Standard`.
  - `TheChecksRejectAnUnknownKindOrMode` — `UPDATE … SET kind = 'Other'` y `project_pricing_mode = 'Hourly'` ⇒ `PostgresException` `23514`.
  - `AProjectScopeRoundTripsThroughJsonb` — guarda por el `DbContext` una cotización `Project` con dos fases e ítems, la relee en otro scope y compara con `Assert.Equal(scope, reloaded.ProjectScope)`; `project_pricing_mode` legible por SQL.
- [ ] **Step 2: RED** — `dotnet build` en verde pero la prueba falla por columnas inexistentes: un RED de integración, filtrado a la clase.
- [ ] **Step 3: Implementa.** `Kind` con `HasConversion<string>().HasMaxLength(20).HasDefaultValue(QuotationKind.Standard)` (patrón de `kind` de las líneas, línea 362); `ProjectScope` jsonb con convertidor y comparador JSON (`JsonSerializerDefaults.Web`, como `ShopifyDbContext.JsonConverterFor<T>`/`JsonComparerFor<T>`); los `CHECK` en `ToTable(…, table => table.HasCheckConstraint(...))`. `FindIdsRequiringShippingAsync` de los dos repositorios suma `&& x.Kind != QuotationKind.Project`. Genera la migración; revisa que sólo agregue lo de arriba.
- [ ] **Step 4: GREEN** — integración filtrada `FullyQualifiedName~ProjectQuotationsMigrationTests`.
- [ ] **Step 5: Commit** — `feat(quotations): persist the project kind and scope`

**Fin del bloque 1:** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests" -- xUnit.MaxParallelThreads=4`, después `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`. Revisión del bloque.

---

## Bloque 2 — Cotizaciones de proyecto por HTTP (Tareas 5 a 7, un implementador)

### Task 5: Contrato: cuerpos, respuestas y validador

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (`CreateQuotationRequest`, `PreviewNewQuotationRequest`, `SaveQuotationRequest`, `PreviewQuotationRequest`, `QuotationDto`, `QuotationResponse`, `QuotationListItemResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` (`OrderDto`, `OrderResponse`, `OrderListItemResponse`)
- Modify: `QuotationEdits.cs` (`IQuotationEdits.Project`, `QuotationEditsValidator`), `CreateQuotation.cs`, `PreviewNewQuotation.cs`, `SaveQuotation.cs`, `PreviewQuotation.cs` (records), `QuotationMapping.cs`, `OrderMapping.cs`, `QuotationResponseComposer.cs`, `OrderResponseComposer.cs`, `ListQuotations.cs`/`ListOrders.cs` (`…ListItemDto`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` y `OrderEndpoints.cs` (mapeo de cuerpo y de filas)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ProjectScopeContract.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ProjectScopeRequestValidatorTests.cs`, `ProjectScopeContractTests.cs`

**Interfaces:**
- Consumes: Tareas 2 y 3.
- Produces (en `ProjectScopeContract.cs`):
  - `public sealed record ProjectItemRequest(Guid Id, string? Text)`; `public sealed record ProjectPhaseRequest(Guid Id, string? Name, decimal? Price, IReadOnlyList<ProjectItemRequest>? Items)`
  - `public sealed record ProjectScopeRequest(Guid? ServiceVariantId, string? PricingMode, decimal? GlobalPrice, IReadOnlyList<ProjectPhaseRequest>? Phases, IReadOnlyList<ProjectItemRequest>? Items, string? PricesCurrency)` con `public ProjectScope ToDomain()` (listas `null` = vacías).
  - `public sealed record ProjectItemDto(Guid Id, string Text)`, `ProjectPhaseDto(Guid Id, string Name, decimal? Price, IReadOnlyList<ProjectItemDto> Items)`, `ProjectScopeDto(Guid? ServiceVariantId, string PricingMode, decimal? GlobalPrice, IReadOnlyList<ProjectPhaseDto> Phases, IReadOnlyList<ProjectItemDto> Items, string PricesCurrency, decimal? Total)` con `public static ProjectScopeDto? From(ProjectScope? scope, string currency)`. La respuesta HTTP usa el mismo record (camelCase por defecto ⇒ forma de §6.3).
  - `public sealed class ProjectScopeRequestValidator : AbstractValidator<ProjectScopeRequest>` (reglas de §6.3 y P6; topes en `Phases` e `Items` del record raíz; `PricesCurrency` obligatoria si hay algún precio).
  - `IQuotationEdits.ProjectScopeRequest? Project => null;` (miembro por defecto, como `BillingCompanyId`); los cuatro records de alta/guardado/preview suman `ProjectScopeRequest? Project = null` al final; `CreateQuotationCommand` y `PreviewNewQuotationQuery` suman además `string? Kind = null`; los `*Request` HTTP suman `Kind` (sólo alta) y `Project`.
  - DTO/respuestas, al final y con default: `QuotationDto`/`QuotationResponse`: `string Kind = "Standard"`, `ProjectScopeDto? Project = null`; `QuotationListItemDto`/`QuotationListItemResponse`: `string Kind`, `string? PricingMode`; `OrderDto`/`OrderResponse`: `string Kind = "Standard"`, `ProjectScopeDto? Project = null` (§12-B: dentro de `order`); `OrderListItemDto`/`OrderListItemResponse`: `string Kind`.
  - `QuotationEditsValidator`: `Kind` con valor distinto de `Standard`/`Project` (sin distinguir mayúsculas) ⇒ error en `Kind`; `Project` presente con `Kind` ausente o `Standard` en el alta ⇒ error en `Project` (el `PUT` lo decide la Tarea 6, que conoce el documento); `RuleFor(e => e.Project!).SetValidator(new ProjectScopeRequestValidator()).When(e => e.Project is not null)`.

- [ ] **Step 1: Pruebas.** `ProjectScopeRequestValidatorTests` (sobre `SaveQuotationValidator` para fijar el prefijo):
  - `PhaseNameAndItemTextErrorsAreKeyedByPosition` — nombre vacío ⇒ `Project.Phases[0].Name`; texto de 2001 ⇒ `Project.Items[0].Text`; texto de fase ⇒ `Project.Phases[0].Items[1].Text`.
  - `TheItemLimitIsReportedUnderProjectItemsAlsoPerPhase` — `PerPhase`, 3 fases con 101 ítems cada una ⇒ una sola falla con `PropertyName == "Project.Items"`; 31 fases ⇒ `Project.Phases`.
  - `ADashIsAValidText` — `"-"` en nombre y texto ⇒ válido (§12-F).
  - `PricesNeedTheirCurrency` — `GlobalPrice = 1` sin `PricesCurrency` ⇒ `Project.PricesCurrency`; sin precios, sin moneda ⇒ válido.
  - `ModeRules` — `PricingMode = "Hourly"` ⇒ `Project.PricingMode`; `PerPhase` sin fases o con ítem suelto ⇒ `Project.PricingMode`; `Global` con precio de fase ⇒ `Project.Phases[0].Price`; precio negativo ⇒ `Project.GlobalPrice`.
  - `DuplicateIdsAreKeyedOnTheRepeatedId` — ítem con el id de una fase ⇒ `Project.Items[0].Id`.
  - `AnUnknownKindOrAProjectOnAStandardCreateIsAFieldError` (sobre `CreateQuotationValidator`) ⇒ `Kind`; `Project` sin `Kind` ⇒ `Project`.
  - `ProjectScopeContractTests.TheResponseCarriesTheDocumentCurrencyAndTheTotal` — `ProjectScopeDto.From(scope, "COP")` ⇒ `PricesCurrency == "COP"`, `Total` del scope, `PricingMode == "PerPhase"`; `From(null, …) == null`.
- [ ] **Step 2: RED** — `dotnet build`.
- [ ] **Step 3: Implementa** con las firmas de arriba; los compositores y mapeos llenan `Kind`, `Project` (con `quotation.Currency`/`order.Currency`) y `PricingMode` de la fila (`quotation.ProjectPricingMode?.ToString()`).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ProjectScopeRequestValidatorTests|FullyQualifiedName~ProjectScopeContractTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): add the project scope to the quotation and order contract`

### Task 6: Alta, guardado y vista previa de una cotización `Project`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ProjectScopeApplication.cs`
- Modify: `QuotationEditsApplication.cs`, `QuotationDraftBuilder.cs` (`QuotationDraftInput.Kind`, `IsOnlyTheClient`), `QuotationPricingRecalculation.cs`, `CreateQuotation.cs`, `PreviewNewQuotation.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPricingRecalculationTests.cs`; `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ProjectQuotationsApiTests.cs` (nueva, `IClassFixture<SharedQuotationsHost>`; prende `projects` por tenant con `QuotationsApiHarness.EnableModuleAsync`, que sólo toca la fila del tenant)

**Interfaces:**
- Consumes: Tareas 3 y 5.
- Produces:
  - `QuotationDraftInput(…, QuotationKind Kind = QuotationKind.Standard)`; `CreateQuotationHandler`/`PreviewNewQuotationHandler` la llenan desde `Kind` (`"Project"` ⇒ `Project`).
  - `internal static class ProjectScopeApplication { public static Task ApplyAsync(Quotation quotation, ProjectScopeRequest? request, bool currencyChanged, IQuotationProductPricingLookup pricingLookup, ITenantPriceRounding priceRounding, Guid tenantId, MemberId updatedBy, DateTimeOffset now, CancellationToken cancellationToken); }` — regla de moneda (§6.5 + §12-A/H): `request` `null` ⇒ conserva (el agregado ya limpió si cambió la moneda); con `HasPrices` y `PricesCurrency` ≠ `quotation.Currency` ⇒ `currencyChanged ? scope.WithoutPrices() : scope.WithPricesOf(saved)`; resuelve el servicio sólo si cambió (`FindVariantAsync`: `null`, otro tenant, `!IsActive` o `Kind != ProductKind.Service` ⇒ `QuotationsDomainException(ProjectErrors.ServiceInvalid, …)`); `quotation.UsePriceRounding(await priceRounding.GetAsync(tenantId, quotation.Currency, ct), now)` y `ReplaceProjectScope`.
  - `QuotationEditsApplication.ApplyAsync`, rama `Project`: `edits.Items.Count > 0` ⇒ `ProjectErrors.ItemsNotAllowed` antes de tocar nada; `repricing = null`; `UpdateDetails` con `isRetail: false`, `globalScaleFloor: null`; sin `ShippingResolver`; sin `QuotationItemEdits`; `ProjectScopeApplication.ApplyAsync` después de `UpdateDetails`. En `Standard` con `edits.Project` ⇒ `ValidationException([new ValidationFailure("Project", …)])`.
  - `QuotationPricingRecalculation.ApplyAsync`: `return` en `Project` justo después de `UsePriceRounding`.

- [ ] **Step 1: Pruebas.**
  - Unitaria `AProjectSkipsScalesFloorAndMinimumPurchase` (`QuotationPricingRecalculationTests`): con un lookup que falla si se llama, `ApplyAsync` sobre un `Project` no lo llama y la línea conserva su precio.
  - Integración (`ProjectQuotationsApiTests`; servicio con `kind = "Service"` como `OrderWithholdingsApiTests.cs:560`):
    - `CreatingAProjectBuildsOneServiceLineAtTheTotal` — `POST /quotations` con `kind: "Project"`, dos fases `PerPhase` 600.000 y 400.000 COP ⇒ 201, `kind == "Project"`, `project.total == 1_000_000`, `project.pricesCurrency == "COP"`, una línea de cantidad 1 y precio 1.000.000, `requiresShipping == false`, ids iguales a los enviados.
    - `APreviewAcceptsDashTextsAndSavesNothing` — `POST /quotations/preview` con `"-"` ⇒ 200 y ningún número de cotización gastado.
    - `ManualLinesOnAProjectAreRejected` — alta con `items` ⇒ 422 `quotation.project.items_not_allowed`.
    - `AnInactiveOrPhysicalServiceIsInvalidOnlyWhenItChanges` — físico ⇒ 422 `quotation.project.service_invalid`; desactivar el servicio después y guardar otro texto ⇒ 200.
    - `SavingWithAnAccountInAnotherCurrencyAppliesPricesTypedInIt` — `PUT` con cuenta USD y `pricesCurrency: "USD"` ⇒ `currency == "USD"`, precios aplicados; con `pricesCurrency: "COP"` ⇒ precios `null`, `total == null`, línea 0.
    - `RemovingTheAccountKeepsTheDocumentCurrency` — documento USD, `PUT` sin cuenta y `pricesCurrency: "USD"` ⇒ sigue USD y aplica (§12-A).
    - `AStaleTabGets412BeforePrices` — `If-Match` viejo ⇒ 412 `concurrency.conflict`.
    - `AProjectBlockOnAStandardQuotationIsAFieldError` — ⇒ 422 `validation.failed`, `errors.Project`.
    - `RetailFloorAndShippingAreIgnoredOnAProject` — cuerpo con `isRetail: true`, `globalScaleFloor: 6` y envío ⇒ `isRetail == false`, `globalScaleFloor == null`, sin envío.
- [ ] **Step 2: RED** — `dotnet build` (por `QuotationDraftInput.Kind`), luego la integración filtrada falla por comportamiento.
- [ ] **Step 3: Implementa** con las firmas de arriba. `IsOnlyTheClient` devuelve `false` si `Kind == Project` o `Project` no es `null`. El historial del guardado suma «Alcance actualizado» / «Precio del proyecto actualizado» cuando cambia el alcance o el total (`QuotationChangeSummary`).
- [ ] **Step 4: GREEN** — unitaria y `FullyQualifiedName~ProjectQuotationsApiTests` filtradas.
- [ ] **Step 5: Commit** — `feat(quotations): create, save and preview project quotations`

### Task 7: Guardas de módulo, listas, envío y conversión

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ProjectModuleGuard.cs`
- Modify: los handlers de §7 del spec (`GetQuotation.cs`, `ListQuotationHistory.cs`, `SaveQuotation.cs`, `PreviewQuotation.cs`, `ChangeQuotationClient.cs`, `AddQuotationItem.cs`, `BatchUpdateQuotationItems.cs`, `UpdateQuotationItem.cs`, `RemoveQuotationItem.cs`, `SendQuotation.cs`, `MarkQuotationSent.cs`, `ExportQuotationPdf.cs`, `VoidQuotation.cs`, `ConvertQuotationToOrder.cs`, `PreviewQuotationConversion.cs`, `CreateQuotation.cs`, `PreviewNewQuotation.cs`, `ListQuotations.cs`, `ExportQuotations.cs`, `QuotationsExportProcessor.cs`), `QuotationListing.cs`, `IQuotationRepository.cs` + `QuotationRepository.cs` (`SearchAsync`, `ListForExportAsync`), `QuotationEndpoints.cs` (`kind` en lista y export)
- Test: `ProjectQuotationsApiTests.cs`; unitarias que construyen handlers (`BatchUpdateQuotationItems`, `SendQuotation`, `MarkQuotationSent`, `ExportQuotationPdf`, `ListQuotations`: una por cada `new …Handler(`) se ajustan al constructor

**Interfaces:**
- Consumes: Tareas 1, 3, 5, 6.
- Produces:
  - `internal static class ProjectModuleGuard { public static Task EnsureAsync(ITenantModules modules, Guid tenantId, QuotationKind kind, CancellationToken cancellationToken); public static Task<bool> IsEnabledAsync(ITenantModules modules, Guid tenantId, CancellationToken cancellationToken); }` — `EnsureAsync` llama `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Projects, …)` sólo con `Project`. Va después del permiso y de cargar (404 primero).
  - `public enum QuotationKindFilter { Standard, Project, All }`; `QuotationListing.ParseKind(string? kind) : QuotationKindFilter` — vacío ⇒ `Standard`; otro ⇒ `ValidationException([new ValidationFailure("kind", "Use Standard, Project or All.")])`.
  - `ListQuotationsQuery(…, string? Kind = null)`, `ExportQuotationsCommand(…, string? Kind = null)`, `QuotationsExportFilters(…, string? Kind = null)` (job viejo ⇒ `Standard`). `SearchAsync`/`ListForExportAsync` suman `QuotationKindFilter kind` antes de `page`/`after`. `All` con el módulo apagado ⇒ `Standard`; `Project` apagado ⇒ 403.
  - En los handlers de líneas (`AddQuotationItem`, `BatchUpdateQuotationItems`, `UpdateQuotationItem`, `RemoveQuotationItem`), tras la guarda: `Project` ⇒ `ProjectErrors.ItemsNotAllowed` antes de leer el catálogo.

- [ ] **Step 1: Pruebas** (`ProjectQuotationsApiTests`):
  - `WithTheModuleOffEveryProjectQuotationRouteIs403` — `[Theory]` sobre `GET`, `GET …/history`, `PUT`, `POST …/preview`, `PUT …/client`, `POST …/items`, `DELETE …/items/{id}`, `POST …/send`, `POST …/mark-sent`, `POST …/pdf`, `POST …/void`, `POST …/order`, `GET …/order/preview`, `POST /quotations` con `kind: "Project"`, `GET /quotations?kind=Project` ⇒ 403 `tenancy.module_not_enabled`; un `Standard` del mismo tenant sigue en 200.
  - `ListsDefaultToStandardAndRowsCarryKindAndMode` — sin `kind` no sale el proyecto; `kind=Project` lo trae con `pricingMode == "PerPhase"`; `kind=All` trae los dos y el `Standard` con `pricingMode == null`; `kind=All` con el módulo apagado trae sólo `Standard`; `kind=Other` ⇒ 422 `errors.kind`.
  - `TheExportFollowsTheKindFilter` — job con `kind=Project` sólo exporta el proyecto (patrón `RunExportJobAsync`).
  - `ItemRoutesOnAProjectAreRejected` — `POST …/items`, `…/items/batch`, `PUT …/items/{id}` ⇒ 422 `quotation.project.items_not_allowed`.
  - `SendingOrConvertingWithoutServiceOrTotalIsIncomplete` — `mark-sent` y `POST …/order` ⇒ 422 `quotation.project.incomplete`.
  - `ConvertingCopiesKindScopeAndLine` — el detalle del pedido trae `order.kind == "Project"`, `order.project` con los mismos ids, `order.requiresShipping == false`, una línea igual; `GET /orders/{id}` responde igual con el módulo apagado.
- [ ] **Step 2: RED** — `dotnet build` (firmas nuevas de `SearchAsync`/`ITenantModules` en constructores), luego integración filtrada.
- [ ] **Step 3: Implementa** con las firmas de arriba.
- [ ] **Step 4: GREEN** — filtradas por nombre.
- [ ] **Step 5: Commit** — `feat(quotations): guard project quotations by the projects module`

**Fin del bloque 2:** clase entera `FullyQualifiedName~ProjectQuotationsApiTests` y `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`. Revisión del bloque.

---

## Bloque 3 — Pedidos, PDF y caminos laterales (Tareas 8 a 10, un implementador)

### Task 8: Pedido `Project`: candado, sin repreciar y filtro `kind`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ProjectOrderLines.cs`
- Modify: `SaveOrderEdits.cs`, `PreviewOrderEdits.cs`, `AddOrderItems.cs`, `OrderPricingRecalculation.cs`, `ListOrders.cs`, `OrderListing.cs`, `ExportOrders.cs`, `OrdersExportProcessor.cs`, `IOrderRepository.cs` + `OrderRepository.cs` (`SearchAsync`, `Filtered`, lectura del export), `OrderEndpoints.cs` (`kind` en lista y export)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPricingRecalculationTests.cs`, `ProjectOrderLinesTests.cs`; integración `ProjectOrdersApiTests.cs` (nueva, `IClassFixture<SharedQuotationsHost>`)

**Interfaces:**
- Consumes: Tareas 3, 5, 7.
- Produces:
  - `internal static class ProjectOrderLines { public static bool KeepsTheLine(Order order, IReadOnlyList<OrderItemAddition> items, IReadOnlyList<OrderItemAddition>? gifts); }` — en `Project`: `gifts` con elementos ⇒ `ProjectErrors.ItemsLocked`; `items` vacío ⇒ `true`; exactamente la línea (variante, cantidad y `UnitPrice` `null` o igual) ⇒ `true`; otra cosa ⇒ `ProjectErrors.ItemsLocked` (§12-C). En `Standard`, `false`.
  - `SaveOrderEdits`/`PreviewOrderEdits` en `Project`: se saltan `OrderItemEdits` y `OrderGiftEdits`, `SetIsRetail`, `SetGlobalScaleFloor` y el `ShippingResolver`; la entrega, facturación, retenciones, comprobantes, notas y fecha siguen igual.
  - `AddOrderItemsHandler`: `Project` ⇒ `ProjectErrors.ItemsLocked` antes del catálogo.
  - `OrderPricingRecalculation.ApplyAsync`: `return` al inicio en `Project` (antes de `UsePriceRounding`).
  - `ListOrdersQuery(…, string? Kind = null)`, `ExportOrdersCommand(…, string? Kind = null)`, `OrdersExportFilters(…, string? Kind = null)`; `OrderListing.ParseKind(string? kind) : QuotationKind?` — vacío o `All` ⇒ `null`; otro inválido ⇒ `ValidationException` con `"kind"` (§12-D). `SearchAsync` y el filtro del export suman `QuotationKind? kind`.

- [ ] **Step 1: Pruebas.**
  - Unitaria `AProjectOrderIsNeverRepriced` (`OrderPricingRecalculationTests`): `ITenantPriceRounding` con otra regla y lookup que falla si se llama ⇒ ni la regla ni el precio cambian.
  - `ProjectOrderLinesTests`: `EmptyOrTheSameLineKeepsIt`, `AnotherQuantityVariantOrPriceIsLocked`, `GiftsWithElementsAreLockedAndAnEmptyListIsNot`, `AStandardOrderIsNotAffected`.
  - Integración (`ProjectOrdersApiTests`, pedido convertido de un proyecto):
    - `EditingAProjectOrderWithItsLineAndNoGiftsSavesTheRest` — `PUT` con `items: [{variantId, quantity: 1}]`, sin `gifts`, notas nuevas y `isRetail: true` ⇒ 200, notas cambian, `isRetail == false`, total igual.
    - `ChangingLinesOrGiftsOfAProjectOrderIsLocked` — otra cantidad, otra variante, `gifts` con un elemento, `POST /orders/{id}/items` ⇒ 422 `order.project.items_locked`.
    - `ProjectOrdersKeepWorkingWithTheModuleOff` — con `projects` apagado: `GET /orders/{id}`, `GET /orders`, `PUT` (notas), un comprobante (`POST …/proofs`) y `POST …/pdf` ⇒ 2xx.
    - `TheOrdersListFiltersByKind` — sin `kind` trae los dos; `kind=Project` sólo el proyecto con `kind == "Project"`; `kind=Other` ⇒ 422 `errors.kind`; el export con `kind=Standard` no trae el proyecto.
- [ ] **Step 2: RED** — `dotnet build`, luego integración filtrada.
- [ ] **Step 3: Implementa** con las firmas de arriba.
- [ ] **Step 4: GREEN** — unitarias e integración filtradas.
- [ ] **Step 5: Commit** — `feat(orders): lock the lines of project orders and filter orders by kind`

### Task 9: Sección «Alcance» en el PDF de cotización y de pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs`, `QuotationPdfDocumentMapper.cs` (`From`, `FromOrder`), `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs`, `OrderPdfDocumentMapperTests.cs`, `QuotationTemplateTests.cs`

**Interfaces:**
- Consumes: Tareas 2 y 3.
- Produces: `public sealed record QuotationPdfScopeItem(string Text)`, `public sealed record QuotationPdfScopePhase(string Name, decimal? Price, IReadOnlyList<QuotationPdfScopeItem> Items)`, `public sealed record QuotationPdfScope(bool ShowPhasePrices, IReadOnlyList<QuotationPdfScopePhase> Phases, IReadOnlyList<QuotationPdfScopeItem> Items)`; `QuotationPdfDocument(…, QuotationPdfScope? Scope = null)` al final (viaja en `data.scope`, `none` en `Standard`). Sin ids.

- [ ] **Step 1: Pruebas.**
  - `AProjectQuotationCarriesItsScopeWithoutIds` y `AProjectOrderCarriesItsScope` — `ShowPhasePrices == true` sólo en `PerPhase`; textos y orden iguales; un `Standard` lleva `Scope == null`.
  - `QuotationTemplateTests.TheTemplateCompilesWithAScopeAndTypstSyntaxInItsTexts` — un texto de ítem `#eval("1+1")` y `$x$` compila y el PDF contiene el texto literal (mismo patrón que `TheTemplateCompilesWithTypstSyntaxInSectionText`); `TheTemplateNeverEvaluatesData` sigue en verde.
- [ ] **Step 2: RED** — `dotnet build`.
- [ ] **Step 3: Implementa.** En `quotation.typ`, después de la tabla de líneas: «Alcance» si `data.scope != none`; cada fase con su nombre (y `importe(precio)` si `showPhasePrices`), sus ítems como lista, y los ítems sueltos al final. Cada texto se inserta como cadena (`[#texto]` sobre el valor JSON), nunca con `eval`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~PdfDocumentMapperTests|FullyQualifiedName~QuotationTemplateTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): print the project scope in quotation and order pdfs`

### Task 10: Borrar el producto del servicio no toca un borrador `Project`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DeletedVariantLineStore.cs` (`BackfillProductSnapshotsAsync`, `ListDraftsWithVariantsAsync`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ProjectQuotationsApiTests.cs`

**Interfaces:**
- Consumes: Tareas 4 y 6.
- Produces: el `UPDATE` de `quotation_items` congela también los borradores `Project` (`AND (q.status <> 'Draft' OR q.kind = 'Project')`); `ListDraftsWithVariantsAsync` sólo devuelve `Kind == QuotationKind.Standard`.

- [ ] **Step 1: Prueba** `DeletingTheServiceProductKeepsTheProjectDraftLine` — borrador `Project` con servicio y total; `DELETE …/catalog/products/{id}` del servicio ⇒ el borrador conserva la línea con `productName` congelado, el total y su versión; un borrador `Standard` con otro producto borrado pierde la línea como hoy. Corre en host propio si el borrado toca algo global (ver `ProductDeletionCleanupTests`).
- [ ] **Step 2: RED** — integración filtrada falla (la línea desaparece).
- [ ] **Step 3: Implementa** los dos cambios de SQL/LINQ.
- [ ] **Step 4: GREEN** — `FullyQualifiedName~DeletingTheServiceProductKeepsTheProjectDraftLine` y `FullyQualifiedName~ProductDeletionCleanupTests`.
- [ ] **Step 5: Commit** — `fix(quotations): keep the service line of a project draft when its product is deleted`

**Fin del bloque 3:** clases enteras `ProjectOrdersApiTests` y `ProjectQuotationsApiTests`, unitarias de Quotations, y `.\scripts\test-feature.ps1` (una corrida, leyendo los avisos). Revisión fuerte de toda la rama por un subagente de solo lectura. El merge a `develop` y la fila de `docs/estado.md` van después, en `develop`.

---

## Self-review

- **Cobertura del spec:** módulo (T1); modelo y migraciones (T2, T4); reglas del dominio de §5 (T3, T6, T8, T10); §6.3–6.5 (T5, T6, T7); §6.6 (T7 conversión, T8 pedido); PDF (T9); guardas de §7 (T7); `requiresShipping` (T3, T4, verificado en T6/T7); §12 A (T6), B (T5, T7), C (T8), D (T8), E–G (T5), H (T2, T6).
- **Fuera de alcance a propósito:** el copiloto (P7) y los reportes (P5) no cambian; `minimumPurchase` y los pisos siguen en la respuesta de un proyecto (la pantalla de proyectos no los pinta).
- **Tipos:** `QuotationKind`, `ProjectScope`, `ProjectServiceLine`, `ProjectErrors`, `ProjectScopeRequest`, `ProjectScopeDto`, `QuotationKindFilter`, `ProjectModuleGuard`, `ProjectScopeApplication`, `ProjectOrderLines` se usan con el mismo nombre en todas las tareas.
