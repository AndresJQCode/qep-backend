# Divisiones y ciudades por país — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Endpoints `país → regiones → ciudades` iguales para todos los países, con Colombia (DIVIPOLA existente) y España (INE, tablas nuevas).

**Architecture:** Módulo Geography. Tablas nuevas `geography.regions` y `geography.region_cities` sólo para países fuera de Colombia; los handlers despachan por país: `CO` lee `departments`/`cities`, el resto lee las tablas nuevas, un país sin catálogo devuelve `[]`. Siembra idempotente desde JSON embebido, como `GeographySeeder`.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-20-geografia-por-pais-design.md`

**Worktree:** `../qep-backend-worktrees/geografia-por-pais`, rama `feature/geografia-por-pais`. Todo comando corre ahí.

## Global Constraints

- `/api/v1/departments` y `/api/v1/cities` no cambian (ni forma ni comportamiento).
- Ninguna ciudad española entra a `geography.cities`; ningún consumidor de `CityId` se toca.
- Endpoints con `RequireAuthorization()` a secas, tag `Geography`, sin tenant ni permiso.
- País sin catálogo → `200 []`. Región inexistente o de otro país → `404` con `ResourceNotFoundException("geography.region.not_found", …)`.
- País: `Trim().ToUpperInvariant()` antes de comparar.
- `regionKind` viaja como nombre de enum: `Department` (CO), `Province` (ES).
- Listas ordenadas por `Name`, `StringComparer.OrdinalIgnoreCase`.
- Comentarios en español, tuteo. Commits conventional, sin atribución de IA.
- Pruebas de integración filtradas por nombre mientras se itera; nunca dos corridas de integración a la vez (`Get-Process testhost, dotnet` antes).

## Review Focus

1. País en minúsculas o con espacios (`" es "`) → mismo resultado que `ES` (prueba en Task 3).
2. `regionId` de Colombia pedido con `/countries/ES/...` y viceversa → 404, no la lista del otro país (Task 3).
3. País sin catálogo (`US`) → `200 []` en `/regions`; en `/regions/{id}/cities` → 404, porque esa región no existe (Task 3).
4. Segundo arranque no duplica regiones ni municipios y renombra si el JSON cambió (Task 2).
5. JSON con código duplicado o municipio de una provincia inexistente → falla de arranque con `InvalidOperationException`, no siembra a medias (Task 1).

---

### Task 1: Dominio y datos de España

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Domain/RegionId.cs`, `RegionCityId.cs`, `Region.cs`, `RegionCity.cs`, `CountryCatalogs.cs`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/Data/es-regions.json`, `es-cities.json`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/RegionDataParser.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/RegionTests.cs`, `CountryCatalogsTests.cs`, `RegionDataParserTests.cs`

**Interfaces — Produces:**
- `readonly record struct RegionId(Guid Value)` y `RegionCityId(Guid Value)`, con `New()` = `Guid.CreateVersion7()` (copia de `DepartmentId`).
- `enum RegionKind { Department, Province }`
- `Region.Create(RegionId id, string countryCode, string code, string name)`; props `Id, CountryCode, Code, Name`; `Rename(string name)`.
- `RegionCity.Create(RegionCityId id, RegionId regionId, string code, string name)`; props `Id, RegionId, Code, Name`; `Rename(string name)`.
- Validaciones con `GeographyDomainException`: `geography.region.country_invalid` (no son 2 letras A-Z), `geography.region.code_required`, `geography.region.name_required`, `geography.region_city.code_required`, `geography.region_city.name_required`. Nombre y código se recortan.
- `static class CountryCatalogs`: `const string Colombia = "CO"`, `const string Spain = "ES"`; `IReadOnlyList<CountryCatalog> All` con `record CountryCatalog(string Country, RegionKind RegionKind)` = `[("CO", Department), ("ES", Province)]`; `CountryCatalog? Find(string? country)` normaliza y busca; `string Normalize(string country)`.
- `RegionDataParser.ParseRegions(Stream) : IReadOnlyList<RegionRecord>` y `ParseCities(Stream) : IReadOnlyList<RegionCityRecord>`; `record RegionRecord(string Code, string Name)`, `record RegionCityRecord(string Code, string Name, string RegionCode)`.
- JSON: `es-regions.json` = `[{ "code": "28", "name": "Madrid" }, …]`; `es-cities.json` = `[{ "code": "28079", "name": "Madrid", "regionCode": "28" }, …]`.

- [ ] **Step 1: Generar los JSON de España**
  Provincias: las 52 de la "Relación de provincias" del INE (`https://www.ine.es/daco/daco42/codmun/cod_provincia.htm`), código de 2 dígitos, nombre oficial tal cual (`Alicante/Alacant`). Municipios: `https://www.ine.es/daco/daco42/codmun/diccionario25.xlsx`; `code` = `CPRO`+`CMUN` (5 dígitos, sin `DC`), `regionCode` = `CPRO`, `name` = `NOMBRE`. Conviértelo con un script en el scratchpad (no en el repo). Anota en el resumen del commit la URL y el número de municipios que salió.
  Verifica: 52 provincias; cada `regionCode` existe en provincias; sin códigos repetidos.

- [ ] **Step 2: Pruebas que fallan**
  - `RegionTests`: `CreateTrimsNameAndCode`, `CreateRejectsCountryThatIsNotTwoLetters` (`"ESP"`, `"e1"`, `""` → código `geography.region.country_invalid`), `CreateRejectsBlankName`, `RenameRejectsBlankName`; idem `RegionCity` con `code_required`/`name_required`.
  - `CountryCatalogsTests`: `FindIsCaseAndSpaceInsensitive` (`" es "` → `Province`), `FindReturnsNullForCountryWithoutCatalog` (`"US"`, `null`, `""`), `ColombiaIsDepartment`.
  - `RegionDataParserTests` (sobre streams en memoria): `ParsesRegionsAndCities`, `DuplicateRegionCodeThrows` (`InvalidOperationException`), `DuplicateCityCodeThrows`, `BlankNameThrows`; y `EmbeddedSpainFilesAreConsistent`: abre los dos recursos embebidos, 52 provincias, toda ciudad con provincia existente, contiene `("28079","Madrid","28")` y `("08019","Barcelona","08")`.

- [ ] **Step 3: Correr y ver el RED** — `dotnet build tests/Modules/Geography/Modules.Geography.UnitTests`. Esperado: error de compilación (`Region`, `CountryCatalogs`, `RegionDataParser` no existen). Esa salida es la evidencia; no hace falta más.

- [ ] **Step 4: Implementar** las firmas de arriba. `RegionDataParser` sigue a `DivipolaDataParser` (mismas `JsonOptions`, `InvalidOperationException`, comentario de por qué). Los JSON entran solos por `<EmbeddedResource Include="Seed\Data\*.json" />`.

- [ ] **Step 5: GREEN** — `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~Region|FullyQualifiedName~CountryCatalogs"`. Esperado: todas pasan.

- [ ] **Step 6: Commit** — `feat(geography): add regions, region cities and Spain INE data`

---

### Task 2: Persistencia, migración y siembra

**Files:**
- Modify: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/GeographyDbContext.cs` (DbSets + `ConfigureRegion`, `ConfigureRegionCity`)
- Create: `src/Modules/Geography/Modules.Geography.Application/IRegionRepository.cs`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/RegionRepository.cs`
- Create: `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/RegionSeeder.cs`
- Modify: `src/Modules/Geography/Modules.Geography.Infrastructure/GeographyInfrastructureExtensions.cs` (registrar repositorio)
- Modify: `src/Modules/Geography/Modules.Geography.Infrastructure/GeographyDatabaseInitializer.cs` (correr `RegionSeeder` después de `GeographySeeder`)
- Create: migración `AddRegions` en `Persistence/Migrations`
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/RegionSeedTests.cs`

**Interfaces:**
- Consumes: Task 1 (`Region`, `RegionCity`, `RegionDataParser`, `CountryCatalogs.Spain`).
- Produces:
  ```csharp
  public interface IRegionRepository
  {
      Task<IReadOnlyList<Region>> ListByCountryAsync(string countryCode, CancellationToken ct);
      Task<Region?> FindAsync(RegionId id, CancellationToken ct);
      Task<IReadOnlyList<RegionCity>> ListCitiesAsync(RegionId regionId, CancellationToken ct);
  }
  ```
- Tablas: `geography.regions` (`id`, `country_code` varchar(2), `code` varchar(10), `name` varchar(120); único `IX_regions_country_code` sobre `(country_code, code)`); `geography.region_cities` (`id`, `region_id` FK `ON DELETE RESTRICT`, `code` varchar(10), `name` varchar(120); único `(region_id, code)`; índice `region_id`). Nombres de columna en snake_case como el resto del contexto.
- `RegionSeeder.SeedAsync(CancellationToken)`: upsert de `ES` por `(country_code, code)` y por `(region, code)`, `Rename` si cambió el nombre, un solo `SaveChangesAsync`. No borra filas que salgan del archivo (mismo criterio que DIVIPOLA; dilo en el comentario).

- [ ] **Step 1: Prueba que falla** — `RegionSeedTests` con el harness (`StartDatabaseAsync`, `QepApiFactory`):
  - `SpainIsSeededOnStartup`: 52 regiones `ES`; municipios = número del Step 1 de Task 1; existe Madrid `28` con ciudad `28079`.
  - `ReseedingDoesNotDuplicateRegionsOrCities`: corre `InitializeGeographyDatabaseAsync` otra vez; mismos conteos.
  - `ReseedingRestoresARenamedRegion`: cambia por SQL el nombre de `28`, re-siembra, vuelve a `Madrid`.
  - `ColombiaIsNotCopiedToRegions`: cero filas con `country_code = 'CO'`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.IntegrationTests`: no compila (`Regions` no existe en el contexto).
- [ ] **Step 3: Implementar** contexto, repositorio, seeder y registro.
- [ ] **Step 4: Migración** — `dotnet ef migrations add AddRegions --project src/Modules/Geography/Modules.Geography.Infrastructure --context GeographyDbContext -o Persistence/Migrations`. Revisa que sólo cree las dos tablas e índices (nada sobre `departments`/`cities`).
- [ ] **Step 5: GREEN** — `dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~RegionSeedTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 6: Commit** — `feat(geography): persist and seed regions by country`

---

### Task 3: Consultas y endpoints por país

**Files:**
- Create: `src/Modules/Geography/Modules.Geography.Application/ListCountries.cs`, `ListRegions.cs`, `ListRegionCities.cs`
- Modify: `src/Modules/Geography/Modules.Geography.Application/GeographyDtos.cs`
- Modify: `src/Modules/Geography/Modules.Geography.Api/GeographyEndpoints.cs`
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/CountryGeographyApiTests.cs`

**Interfaces:**
- Consumes: `CountryCatalogs`, `IRegionRepository` (Tasks 1–2), `IDepartmentRepository.ListAllAsync/FindAsync`, `ICityRepository.ListByDepartmentAsync`.
- Produces:
  - `ListCountriesQuery : IQuery<IReadOnlyList<CountryCatalogDto>>`; `CountryCatalogDto(string Code, string RegionKind)`.
  - `ListRegionsQuery(string Country) : IQuery<IReadOnlyList<RegionOptionDto>>`; `RegionOptionDto(Guid Id, string Code, string Name)`.
  - `ListRegionCitiesQuery(string Country, Guid RegionId) : IQuery<IReadOnlyList<RegionCityOptionDto>>`; `RegionCityOptionDto(Guid Id, string Code, string Name)`.
  - Para `CO`: `Id`/`Code` = `Department.Id`/`DivipolaCode` y `City.Id`/`DivipolaCode`, `Name` = `City.Name`. Región: `FindAsync` de departamento; null → 404.
  - Para otro país con catálogo: `IRegionRepository`; región null **o** con `CountryCode` distinto → 404.
  - País sin catálogo: `ListRegions` → `[]`; `ListRegionCities` → 404 (no hay región que exista).
  - Rutas: `GET /api/v1/countries`, `GET /api/v1/countries/{country}/regions`, `GET /api/v1/countries/{country}/regions/{regionId:guid}/cities`. Responses `CountryResponse(string Code, string RegionKind)`, `RegionResponse(Guid Id, string Code, string Name)`, `RegionCityResponse(Guid Id, string Code, string Name)`. En `RegionOptionDto` va el comentario BFF: por qué un país sin catálogo da `[]` (el frontend cae al texto libre con una sola regla) y por qué `CO` devuelve el id de DIVIPOLA (es el `CityId` que se guarda).

- [ ] **Step 1: Pruebas que fallan** — `CountryGeographyApiTests`:
  - `CountriesWithoutAuthIsUnauthorized` (401).
  - `CountriesListsColombiaAndSpain`: `[("CO","Department"),("ES","Province")]`.
  - `ColombiaRegionsAreTheDivipolaDepartments`: 33, contiene `("05","ANTIOQUIA")`, ids iguales a los de `/api/v1/departments`.
  - `ColombiaCitiesUseTheDivipolaCityIds`: Antioquia → 125, contiene `("05001","MEDELLÍN")`, ids iguales a `/api/v1/cities?departmentId=`.
  - `SpainRegionsAreTheFiftyTwoProvinces` y `SpainCitiesOfMadridContainMadrid` (`28079`).
  - `CountryIsCaseAndSpaceInsensitive`: `/countries/%20es%20/regions` = `/countries/ES/regions`.
  - `CountryWithoutCatalogReturnsEmptyRegions`: `US` → 200 `[]`.
  - `ColombianRegionUnderSpainIsNotFound` y `SpanishRegionUnderColombiaIsNotFound`: 404 con `code = "geography.region.not_found"`.
  - `UnknownRegionIsNotFound`: `Guid.NewGuid()` bajo `ES` → 404.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.IntegrationTests`: no compila (`RegionResponse` no existe).
- [ ] **Step 3: Implementar** queries, handlers, DTOs y endpoints.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~CountryGeographyApiTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Cierre del bloque** — proyecto completo de Geography (unitarias + integración) y `tests/ArchitectureTests`, uno a la vez. Esperado: todo verde, incluidas `GeographyApiTests` sin cambios.
- [ ] **Step 6: Commit** — `feat(geography): list regions and cities by country`
- [ ] **Step 7: README** — sección de Geography: las tres rutas nuevas y de dónde salen los datos de España. Commit `docs(geography): document regions by country`.
