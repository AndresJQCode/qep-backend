# Tienda en modo catálogo: ocultar los precios — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un ajuste nuevo de la tienda, `showPrices` (bool, `true` por defecto), que se guarda en `PUT /store/settings`, se lee en `GET /store/settings` y viaja en `GET /api/v1/public/stores/{host}`, sin cambiar cómo se precia un pedido ni lo que devuelven los demás endpoints públicos.

**Architecture:** `OnlineStore` gana `ShowPrices` (entra a `Configure` y a `StateKey()`); la columna `store.stores.show_prices boolean NOT NULL DEFAULT true` llega con la migración `AddStoreShowPrices`, mapeada con un centinela explícito para que el `false` del primer guardado no lo pise el default de la base. En Application el cuerpo recibe `bool? ShowPrices` y el handler resuelve «ausente = conservar» con `body.ShowPrices ?? store.ShowPrices`; el dominio nunca ve un `bool?`. `StoreSettingsDto` y `PublicStoreDto` exponen el valor.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-10-tienda-mostrar-precios-design.md`](../specs/2026-11-10-tienda-mostrar-precios-design.md) · specs hermanos: `qep-frontend/docs/superpowers/specs/2026-11-10-tienda-mostrar-precios-design.md`, `qep-storefront/docs/superpowers/specs/2026-11-10-tienda-mostrar-precios-design.md`.

---

## Antes de empezar

- Worktree, desde el checkout principal de `qep-backend`, en PowerShell: `git branch --show-current` (si no dice `develop`,
  frena y pregunta), después
  `git worktree add ..\qep-backend-worktrees\tienda-mostrar-precios -b feature/tienda-mostrar-precios develop`.
  Todo lo que sigue corre **dentro** del worktree. Antes de **cada** commit: `git branch --show-current` tiene que decir
  `feature/tienda-mostrar-precios`; si dice otra cosa, frena y pregunta.
- Todo comando es PowerShell, una línea por comando, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas:**
  es del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y, en la misma sesión, `$env:Seed__Enabled = "false"` en una línea aparte. Nunca la
  solución entera: sólo `tests/Modules/Store/Modules.Store.IntegrationTests`, con `--filter` a la clase que tocas y
  `-- xUnit.MaxParallelThreads=4`. La corrida completa del proyecto va una sola vez, en la Tarea 4.
- Migraciones con el factory de diseño, nunca `--startup-project` (así se generaron `AddStoreAdvisor` y
  `DropStoreOrderRequests`):
  `dotnet ef migrations add <Nombre> --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`.
- La solución compila y las pruebas tocadas pasan **al cerrar cada tarea**.

## Global Constraints

- Nombre del campo en el cable: `showPrices`; en C#: `ShowPrices`. Tipo `bool` en dominio, DTO del backoffice y DTO público; `bool?` sólo en `StoreSettingsRequest`.
- Valor por defecto `true`: `OnlineStore.CreateEmpty` lo fija en `true` y la columna es `show_prices boolean NOT NULL DEFAULT true` en `store.stores`.
- `PUT /store/settings`: `showPrices` ausente o `null` **conserva el valor guardado** (en una tienda con `Version = 0`, el de `CreateEmpty`: `true`). El validador **no** cambia; no hay código de error nuevo.
- `ShowPrices` entra a `OnlineStore.Configure`, a `StateKey()` y al `Snapshot()` de la auditoría como `["showPrices"] = store.ShowPrices.ToString()`. `PublishBlockers()` no cambia.
- `SubmitOrderRequest`, `QuotePublicCart`, `PublicCartPricing`, `StoreOrderGateway` e `IsRetail` **no leen** `ShowPrices` (O5). Los endpoints públicos `/products`, `/products/{id}`, `/cart/quote`, `/order-requests` y `buyer/orders` no cambian y siguen devolviendo montos (O4).
- Sin cambio de caché: `CatalogCacheControl` (`PublicStoreEndpoints.cs:20`) queda igual (A5).
- `ArchitectureTests` sin cambios: ningún proyecto gana referencias.
- TDD: RED antes que GREEN, con la salida literal de ambas corridas en el reporte de cada tarea.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**, siempre `git commit -m "…" -- <rutas>` (los archivos nuevos se agregan antes con `git add <ruta>`).
- Comentarios de código en español, tuteando; el voseo se corrige sólo en las líneas que tocas.
- `docs/estado.md` **no** se toca en la rama: se actualiza en `develop` al mergear (Tarea 4). Nunca se mergea a `main`.

## Decisiones de este plan (lo que el spec deja al constructor)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | Mapeo `HasColumnName("show_prices").HasDefaultValue(true).HasSentinel(true)` (B1). | Con centinela `true`, EF omite la columna sólo cuando el valor es `true` (y la base pone `true`); un `false` siempre viaja en el `INSERT`. Es la forma de los precedentes (`MessagingDbContext.cs:284`) y deja el default en el modelo, así el snapshot no deriva. |
| P2 | `ShowPrices` va al **final** de `StoreSettingsInput` y de `StoreSettingsRequest` (después de `AdvisorMembershipId`), y **justo después de `PricingMode`** en `StoreSettingsDto` y `PublicStoreDto`. | Los dos primeros se construyen posicionalmente en helpers de prueba; los DTO se construyen en un solo lugar cada uno y así el JSON lo pone junto al modo de precios, como la pantalla. |
| P3 | La Tarea 1 hace compilar el handler con `ShowPrices: store.ShowPrices` (conservar) hasta que la Tarea 2 lea el cuerpo. | Es el único valor correcto mientras el cuerpo no trae el campo; nunca hay un estado intermedio que apague precios. |
| P4 | La persistencia va en la Tarea 1 junto al dominio. | Con la propiedad nueva y sin mapeo, EF la mapea por convención a una columna `ShowPrices` que no existe y **toda** lectura de `store.stores` falla: separarlas dejaría la integración rota entre tareas. |
| P5 | El README (§ Tienda pública) gana una viñeta de `showPrices` en la Tarea 3. | Ahí ya se documenta `advisorMembershipId` del mismo `PUT`; el spec no lo pide, pero sin la nota «ausente conserva» el siguiente lo «arregla». |

## Review Focus

1. **Frontend viejo durante el despliegue** (B3): el owner apagó los precios y otro guardado llega sin `showPrices` → sigue `false`, sin 422. Lo amarran la Tarea 2 unitaria (`AnAbsentShowPricesKeepsAStoredFalse`) e integración (`ShowPricesRoundTripsAndAnAbsentValueKeepsIt`).
2. **Primer guardado de una tienda nueva con `showPrices: false`** (B1): se relee `false`, no `true`. Lo amarran la Tarea 1 (`ANewStoreSavedWithShowPricesFalseReadsBackFalse`, más la comprobación de mutación sin el centinela) y la Tarea 2 (`TheFirstSaveOfANewStoreKeepsShowPricesFalse`).
3. **`PUT` que sólo cambia el interruptor** (B2): escribe, sube `Version` y audita `["showPrices"]`; repetirlo no escribe. Lo amarran la Tarea 1 (`ChangingOnlyShowPricesBumpsTheVersion`) y la Tarea 2 (`TogglingOnlyShowPricesAuditsIt`).
4. **`showPrices` que no es booleano** (`"no"`): responde 400 del deserializador y el valor guardado no cambia; nunca se lee como `false`. Lo amarra la Tarea 2 (`ANonBooleanShowPricesIs400AndChangesNothing`).
5. **Publicar una tienda con precios ocultos**: publicar no reinicia el ajuste, la tienda pública trae `showPrices: false` y el catálogo y la cotización del carrito siguen trayendo montos. Lo amarra la Tarea 3 (`AHiddenPricesStoreStillReturnsAmounts`).

---

### Task 1: Dominio y persistencia — `OnlineStore.ShowPrices` y la columna `show_prices`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Domain/OnlineStore.cs` (propiedad junto a `PricingMode`, `:51`; `CreateEmpty`, `:71-77`; asignación en `Configure`, junto a `PricingMode = input.PricingMode;` en `:146`; `StateKey()`, `:167-170`)
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreSettingsInput.cs:7-22` (último parámetro `bool ShowPrices`)
- Modify: `src/Modules/Store/Modules.Store.Application/UpdateStoreSettings.cs:174-189` (P3: último argumento `store.ShowPrices`)
- Modify: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/StoreDbContext.cs` (después de `advisor_membership_id`, `:114`)
- Create: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/Migrations/<timestamp>_AddStoreShowPrices.cs` y `.Designer.cs`; Modify: `StoreDbContextModelSnapshot.cs` (los genera `dotnet ef`)
- Modify: `tests/Modules/Store/Modules.Store.UnitTests/StoreTestData.cs:24-46` (`Input(...)` gana `bool showPrices = true`, pasado como `ShowPrices: showPrices`)
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StorePersistenceTests.cs:30-69` (`ConfiguredStore` y `Configure` ganan `bool showPrices = true`, pasado como `ShowPrices: showPrices`)
- Test: `tests/Modules/Store/Modules.Store.UnitTests/OnlineStoreTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StorePersistenceTests.cs`

**Interfaces:**
- Produces: `public bool ShowPrices { get; private set; }` en `OnlineStore`; `StoreSettingsInput(..., Guid? AdvisorMembershipId, bool ShowPrices)`; `StoreTestData.Input(..., bool showPrices = true)`.

- [ ] **Step 1: Write the failing unit tests** en `OnlineStoreTests`:
  - `AnEmptyStoreShowsPrices` — `Assert.True(Empty().ShowPrices)`.
  - `ChangingOnlyShowPricesBumpsTheVersion` — `ConfiguredStore()` (versión 1) → `Assert.True(store.Configure(Input(showPrices: false), KnowsCoAndEs, Member, Now))`, `Assert.False(store.ShowPrices)`, `Assert.Equal(2, store.Version)`. Es la prueba que protege `StateKey()`.
  - `ReconfiguringWithShowPricesFalseAgainChangesNothing` — `ConfiguredStore(Input(showPrices: false))`, guarda `Version`; `Assert.False(store.Configure(Input(showPrices: false), …))`, misma `Version`.
  - `HidingPricesIsNotAPublishBlocker` — `Assert.Empty(ConfiguredStore(Input(showPrices: false)).PublishBlockers())`.
- [ ] **Step 2: Write the failing persistence tests** en `StorePersistenceTests` (agrega un helper `ScalarTextAsync(QepApiFactory, FormattableString)` que use `SqlQuery<string>`, como `ScalarAsync`):
  - `TheShowPricesColumnIsNotNullAndDefaultsToTrue` — `SELECT is_nullable || '|' || column_default AS "Value" FROM information_schema.columns WHERE table_schema = 'store' AND table_name = 'stores' AND column_name = 'show_prices'` → `"NO|true"`.
  - `ANewStoreSavedWithShowPricesFalseReadsBackFalse` (B1) — `SaveStoreAsync(factory, ConfiguredStore(Tenant, showPrices: false))`; `Assert.False((await ReloadStoreAsync(factory, Tenant)).ShowPrices)` y `SELECT show_prices::text … WHERE tenant_id = {Tenant}` → `"false"`.
  - `ANewStoreSavedWithDefaultsReadsBackTrue` — `ConfiguredStore(Tenant)` → `ReloadStoreAsync(...).ShowPrices` es `true`.
  - `AStoreThatExistedBeforeTheMigrationShowsPrices` — patrón de `CompanyCountryMigrationTests`: `context.GetService<IMigrator>()` sobre `StoreDbContext`, `MigrateAsync("20261110031040_DropStoreOrderRequests", Ct)`, `InsertBareStoreAsync(factory, Tenant)`, `MigrateAsync(null, Ct)` (vuelve a la última antes de leer), y `SELECT show_prices::text` → `"true"`.
- [ ] **Step 3: Run, expect FAIL (compilación)**
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~OnlineStoreTests"`
  Esperado: `error CS1739: The best overload for 'Input' does not have a parameter named 'showPrices'` y `CS1061: 'OnlineStore' does not contain a definition for 'ShowPrices'`.
- [ ] **Step 4: Implement the domain.** Propiedad con comentario «Sólo visual: el pedido se sigue preciando con `PricingMode` (spec 2026-11-10, O4/O5).»; `ShowPrices = true` en `CreateEmpty`; `ShowPrices = input.ShowPrices;` junto a `PricingMode`; `ShowPrices` como un elemento más de `StateKey()` (después de `AdvisorMembershipId`). `StoreSettingsInput` gana `bool ShowPrices` al final; el handler pasa `store.ShowPrices` (P3).
- [ ] **Step 5: Run unit tests, expect PASS**
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests`
  Esperado: todo verde, incluidas `StoreSettingsHandlerTests` y `UpdateStoreSettingsValidatorTests` sin cambios.
- [ ] **Step 6: Run persistence tests, expect FAIL (sin mapeo)**
  `$env:Seed__Enabled = "false"`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StorePersistenceTests" -- xUnit.MaxParallelThreads=4`
  Esperado: las cuatro nuevas y las de ida y vuelta existentes fallan con `42703` (columna `ShowPrices`/`show_prices` inexistente).
- [ ] **Step 7: Map and migrate.** En `ConfigureStore`, con comentario que explique B1 (sin centinela, EF omite un `false` y la base pone `true`):
  `store.Property(value => value.ShowPrices).HasColumnName("show_prices").HasDefaultValue(true).HasSentinel(true);`
  Después: `dotnet ef migrations add AddStoreShowPrices --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`.
  Revisa el `Up`: un solo `AddColumn<bool>(name: "show_prices", schema: "store", table: "stores", type: "boolean", nullable: false, defaultValue: true)`; el `Down`, un solo `DropColumn` de esa columna. Nada más en ninguno de los dos.
- [ ] **Step 8: Run persistence tests, expect PASS** (mismo comando del Step 6).
- [ ] **Step 9: Mutation check of B1.** Quita temporalmente `.HasSentinel(true)`, corre sólo `--filter "FullyQualifiedName~ANewStoreSavedWithShowPricesFalseReadsBackFalse"`: tiene que fallar con `Assert.False() Failure` (lee `true`). Repón la línea y vuelve a correrla en verde. Pega las dos salidas.
- [ ] **Step 10: Verify the model has no drift**
  `dotnet ef migrations has-pending-model-changes --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext`
  Esperado: `No changes have been made to the model since the last migration.`
- [ ] **Step 11: Commit** — `git add` de los dos archivos de la migración, después
  `git commit -m "feat(store): add the show prices setting to the online store" -- src/Modules/Store tests/Modules/Store`

### Task 2: Contrato del backoffice — `PUT`/`GET /store/settings`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/StoreDtos.cs` (`StoreSettingsRequest`, `:10-30`: último parámetro `bool? ShowPrices`; `StoreSettingsDto`, `:39-66`: `bool ShowPrices` después de `string PricingMode`, `:58`)
- Modify: `src/Modules/Store/Modules.Store.Application/UpdateStoreSettings.cs` (último argumento de `StoreSettingsInput`, `:174-189`; `Snapshot()`, `:305-330`)
- Modify: `src/Modules/Store/Modules.Store.Application/StoreSettingsComposer.cs:76` (pasa `store.ShowPrices` después de `store.PricingMode.ToString()`)
- Modify: `tests/Modules/Store/Modules.Store.UnitTests/StoreSettingsTestDoubles.cs:311-345` (`SettingsRig.Body(...)` gana `bool? showPrices = null`, último argumento)
- Test: `tests/Modules/Store/Modules.Store.UnitTests/StoreSettingsHandlerTests.cs`, `tests/Modules/Store/Modules.Store.UnitTests/UpdateStoreSettingsValidatorTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreSettingsApiTests.cs`

**Interfaces:**
- Consumes: `OnlineStore.ShowPrices`, `StoreSettingsInput.ShowPrices`, `StoreTestData.Input(showPrices:)` (Tarea 1).
- Produces: `StoreSettingsRequest.ShowPrices : bool?`, `StoreSettingsDto.ShowPrices : bool` (JSON `showPrices`, siempre presente, también en las respuestas de `publish` y `unpublish`).

- [ ] **Step 1: Write the failing unit tests** en `StoreSettingsHandlerTests` (`rig.UpdateHandler().HandleAsync(Update(Body(...), version), Ct)`):
  - `SavingShowPricesFalseStoresIt` — `new SettingsRig()`, `Body(showPrices: false)` → `Assert.False(saved.ShowPrices)`, `Assert.False(rig.Stores.Stored!.ShowPrices)` y `Assert.Contains("showPrices", Assert.Single(rig.Audit.Entries).ChangedFields)`.
  - `AnAbsentShowPricesKeepsAStoredFalse` — `new SettingsRig(ConfiguredStore(Input(showPrices: false)))`, `Body(name: "Nueva")`, `version: 1` → `Assert.False(saved.ShowPrices)`.
  - `AnAbsentShowPricesKeepsAStoredTrue` — `new SettingsRig(ConfiguredStore())`, `Body(name: "Nueva")`, `version: 1` → `Assert.True(saved.ShowPrices)`.
  - `ANewStoreWithoutShowPricesShowsPrices` (B4) — `new SettingsRig()`, `Body()` → `Assert.True(saved.ShowPrices)` y `Assert.DoesNotContain("showPrices", Assert.Single(rig.Audit.Entries).ChangedFields)`.
  - `TogglingOnlyShowPricesAuditsIt` (B2) — `new SettingsRig(ConfiguredStore())`; primero `Body(name: "Nueva")` con `version: 1` (la versión pasa a 2), después `Body(name: "Nueva", showPrices: false)` con `version: 2` → `saved.Version == 3` y `Assert.Equal(["showPrices"], rig.Audit.Entries[^1].ChangedFields)`; un tercer guardado idéntico con `version: 3` no suma auditoría ni `UnitOfWork.Saves`.
  - En `ANeverSavedStoreReadsAsDefaultsWithVersionZero` agrega `Assert.True(settings.ShowPrices)`.
  En `UpdateStoreSettingsValidatorTests`: `ABodyWithoutShowPricesIsValid` — `Assert.Empty(FailedProperties(Body(showPrices: null)))`, y `Assert.Empty(FailedProperties(Body(showPrices: false)))`.
- [ ] **Step 2: Write the failing integration tests** en `StoreSettingsApiTests` (`StoreSettingsFixture`; `fixture.Body()` **no** trae `showPrices`, así que es el cuerpo de un frontend viejo). Lee el campo con `(bool?)json["showPrices"]` para que su ausencia falle como `null` y no como excepción:
  - `ShowPricesRoundTripsAndAnAbsentValueKeepsIt` — `SaveAsync(Body(b => b["showPrices"] = false))` → `false`; `GetAsync()` → `false`; `PutAsync(Body(), ifMatch: "1")` → 200 y `false`; `PutAsync(Body(b => b["showPrices"] = null), ifMatch: "1")` → `false`; `PutAsync(Body(b => b["showPrices"] = true), ifMatch: "1")` → `true` y `version == 2`.
  - `TheFirstSaveOfANewStoreKeepsShowPricesFalse` (B1 por HTTP) — `SaveAsync(Body(b => b["showPrices"] = false))` con `ifMatch: "0"`, y el `GET` siguiente → `false`.
  - `ANonBooleanShowPricesIs400AndChangesNothing` — `SaveAsync(Body())`; `PutAsync(Body(b => b["showPrices"] = "no"), ifMatch: "1")` → `HttpStatusCode.BadRequest`; el `GET` → `showPrices == true`, `version == 1`.
  - En `ANeverSavedStoreReadsAsDefaultsWithVersionZero` agrega `Assert.Equal(true, (bool?)settings["showPrices"])`.
- [ ] **Step 3: Run unit tests, expect FAIL (compilación)**
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~StoreSettingsHandlerTests|FullyQualifiedName~UpdateStoreSettingsValidatorTests"`
  Esperado: `CS1739` (`Body` sin `showPrices`) y `CS1061` (`StoreSettingsDto` sin `ShowPrices`).
- [ ] **Step 4: Run integration tests, expect FAIL** (el proyecto de integración compila: arma el cuerpo como JSON)
  `$env:Seed__Enabled = "false"`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreSettingsApiTests" -- xUnit.MaxParallelThreads=4`
  Esperado: `Assert.Equal() Failure` con `Actual: null` en cada lectura de `showPrices`, y en
  `ANonBooleanShowPricesIs400AndChangesNothing` un `OK` donde se esperaba `BadRequest` (el binding todavía ignora el campo).
- [ ] **Step 5: Implement.**
  - `StoreSettingsRequest`: `bool? ShowPrices` al final, con comentario: ausente o `null` conserva el valor guardado, a propósito fuera de la regla de `StoreDtos.cs:6-7`; un frontend en producción durante el despliegue no conoce el campo, y un 422 trabaría cada guardado o un `null` leído como `false` apagaría los precios (spec §4.3).
  - Handler: último argumento de `StoreSettingsInput` = `body.ShowPrices ?? store.ShowPrices` (reemplaza P3). El validador no cambia.
  - `Snapshot()`: `["showPrices"] = store.ShowPrices.ToString()`.
  - `StoreSettingsDto`: `bool ShowPrices` después de `PricingMode`; el composer pasa `store.ShowPrices`.
- [ ] **Step 6: Run unit tests, expect PASS** (todo el proyecto: `dotnet test tests/Modules/Store/Modules.Store.UnitTests`).
- [ ] **Step 7: Run integration tests, expect PASS** (mismo comando del Step 4).
- [ ] **Step 8: Commit** — `git commit -m "feat(store): expose show prices in the store settings contract" -- src/Modules/Store/Modules.Store.Application tests/Modules/Store`

### Task 3: Contrato público — `GET /api/v1/public/stores/{host}`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/PublicStoreDtos.cs:48-63` (`bool ShowPrices` después de `string PricingMode`, con `<param name="ShowPrices">`: sólo visual; los montos siguen viajando en `/products`, `/products/{id}`, `/cart/quote`, el recibo y `buyer/orders`, O4)
- Modify: `src/Modules/Store/Modules.Store.Application/GetPublicStore.cs:29` (pasa `store.ShowPrices` después de `store.PricingMode.ToString()`)
- Modify: `README.md:1010-1014` (viñeta nueva después de la del asesor, P5)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicCatalogApiTests.cs`

**Interfaces:**
- Consumes: `OnlineStore.ShowPrices` (Tarea 1); `showPrices` en el `PUT` (Tarea 2), vía `PublicStoreFixture.OpenAsync(tweak)`.
- Produces: `PublicStoreDto.ShowPrices : bool` (JSON `showPrices`, siempre presente).

- [ ] **Step 1: Write the failing tests** en `PublicCatalogApiTests`:
  - `ThePublicStoreShowsPricesByDefault` — `OpenAsync()`; `GetJsonAsync("/")` → `Assert.Equal(true, (bool?)store["showPrices"])`.
  - `AHiddenPricesStoreStillReturnsAmounts` (O4 y Review Focus 5) — `OpenAsync((_, body) => body["showPrices"] = false)` (guarda y publica); `CreateProductAsync("Vendible", "V-1", Prices(10_000m))`; `GetJsonAsync("/")` → `showPrices == false`; `GetJsonAsync("/products?currency=COP")` → el ítem trae `priceFrom == 10_000m`; `QuoteAsync(new { currency = "COP", lines = new[] { new { variantId, quantity = 2 } } })` → `total == 20_000m`.
- [ ] **Step 2: Run, expect FAIL**
  `$env:Seed__Enabled = "false"`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicCatalogApiTests" -- xUnit.MaxParallelThreads=4`
  Esperado: las dos nuevas con `Assert.Equal() Failure` y `Actual: null`.
- [ ] **Step 3: Implement** el campo en `PublicStoreDto` y su asignación en `GetPublicStoreHandler`. En el README, una viñeta: «**Ajustes, mostrar precios:** `PUT /settings` acepta `showPrices` (opcional; ausente o `null` conserva el valor guardado, a propósito, para no apagar precios con un frontend viejo); `GET /settings` y `GET /api/v1/public/stores/{host}` lo devuelven siempre. Es sólo visual: el pedido se precia con `pricingMode` y los demás endpoints públicos siguen devolviendo montos. Un cambio puede tardar hasta la caché pública (`max-age=60`, `stale-while-revalidate=300`) en verse.»
- [ ] **Step 4: Run, expect PASS** (mismo comando del Step 2).
- [ ] **Step 5: Commit** — `git commit -m "feat(store): expose show prices in the public store" -- src/Modules/Store/Modules.Store.Application tests/Modules/Store/Modules.Store.IntegrationTests/PublicCatalogApiTests.cs README.md`

### Task 4: Verificación completa y merge a `develop`

**Files:**
- Modify (en `develop`, después del merge): `docs/estado.md` (tabla «Specs», fila nueva `2026-11-10 tienda: mostrar precios`)

- [ ] **Step 1: Build** — `dotnet build` en el worktree. Esperado: `0 Warning(s)`, `0 Error(s)` (o los mismos warnings que `develop`).
- [ ] **Step 2: Unit tests** — `dotnet test tests/Modules/Store/Modules.Store.UnitTests` y `dotnet test tests/ArchitectureTests/ArchitectureTests`. Esperado: verde; `StoreLayerTests` sin cambios.
- [ ] **Step 3: Store integration, once** — `$env:Seed__Enabled = "false"` y
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4`. Esperado: verde. Ningún otro proyecto de integración: ningún otro módulo lee `OnlineStore` ni construye estos DTO.
- [ ] **Step 4: Check that pricing did not learn the setting** — `rg -n "ShowPrices" src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs src/Modules/Store/Modules.Store.Application/QuotePublicCart.cs src/Modules/Quotations src/Bootstrapper` no devuelve nada (O5; `PublicCartPricing` vive en `QuotePublicCart.cs`, `StoreOrderGateway` en `Modules.Quotations.Application/IStoreOrderGateway.cs:97`).
- [ ] **Step 5: Merge** — en el checkout principal: `git branch --show-current` dice `develop` (si no, frena y pregunta); `git merge --no-ff feature/tienda-mostrar-precios`. **Nunca** a `main`, y sin push salvo que el owner lo pida.
- [ ] **Step 6: Estado** — en `develop`, fila en «Specs» de `docs/estado.md`: construido y mergeado (fecha, sha del merge, sin push), migración `AddStoreShowPrices`, **orden de salida: backend primero** (A6), pendientes del owner: confirmar A1–A6 del spec y la semántica de `null` (§8). `git commit -m "docs: record the show prices store setting" -- docs/estado.md`.
- [ ] **Step 7: Clean up** — `git worktree remove ..\qep-backend-worktrees\tienda-mostrar-precios`; si queda la carpeta por `Filename too long`, `Remove-Item -LiteralPath "\\?\<ruta absoluta>" -Recurse -Force`.
