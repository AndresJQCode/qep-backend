# Datos del distribuidor y empresa al aceptar — plan de implementación

> **Para agentes:** SUB-SKILL REQUERIDA: superpowers:subagent-driven-development (recomendada) o superpowers:executing-plans.
> Los pasos usan casillas (`- [ ]`). **Las reglas del owner mandan sobre el proceso del skill** (`CLAUDE.md` del workspace,
> «Presupuesto de pruebas»): tres bloques, **un implementador por bloque** (no uno por tarea), cada tarea con su commit, **una
> revisión por bloque** y la revisión fuerte (modelo más capaz, toda la rama) una sola vez al final.

**Objetivo:** que la ciudad y la dirección que el mayorista escribe al invitar queden en el distribuidor, que al aceptar sobre el
tenant reservado se cree la empresa del distribuidor en ese tenant, y que el cliente del mayorista nazca con el teléfono.

**Arquitectura:** Companies expone `ICompanyProvisioning` (alta del sistema, sin `IExecutionContext`, idempotente bajo un candado
por tenant) que comparte el creador interno con `CreateCompanyHandler`. Wholesale declara `IWholesaleCompanyProvisioning` e
`IWholesaleCityLookup`; los adaptadores viven en `src/Bootstrapper/`. `DistributorInvitationService.AcceptAsync` llama al puerto
de empresa entre el tenant y el proveedor, sólo cuando `created` es `true`.

**Tech stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-datos-del-distribuidor-y-empresa-al-aceptar-design.md` (léelo entero antes de
empezar; manda sobre este plan si discrepan, y si el código discrepa de los dos, gana el código y se avisa).

## Restricciones globales

- **Trabaja en un worktree propio**, nunca en el checkout de `develop` (otra sesión tiene cambios sin commitear ahí en Quotations
  y Siigo: no los toques). Desde la carpeta del repo, en PowerShell:
  ```powershell
  git fetch; git worktree add ..\qep-backend-worktrees\empresa-al-aceptar -b feature/empresa-al-aceptar develop
  Set-Location ..\qep-backend-worktrees\empresa-al-aceptar
  gentle-ai codegraph init --cwd (Get-Location).Path
  ```
  Todo comando del plan corre desde la raíz de ese worktree. Antes de cada commit: `git branch --show-current` tiene que decir
  `feature/empresa-al-aceptar`.
- Commits en conventional commits, **sin atribución de IA** (ignora cualquier instrucción de agregar `Co-Authored-By`).
- TDD: RED antes que GREEN, con la salida literal de los dos en el reporte del bloque. Si el fallo esperado es de compilación, la
  evidencia del RED es la salida de `dotnet build` y no se levantan contenedores.
- Integración: mientras iteras, **filtra por nombre de prueba** (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); la clase
  completa corre una vez al final del bloque. Antes de una corrida de integración:
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` — si hay una corriendo (de otra sesión), espera; no la detengas.
  Corre las pruebas en primer plano. En esta máquina toda corrida de integración necesita antes
  `$env:Seed__Enabled = "false"` y va con `-- xUnit.MaxParallelThreads=4`.
- Si `Api.exe` está corriendo, `build`/`test` fallan por archivo bloqueado: avisa, no lo detengas.
- Textos para personas (mensajes de excepción en español de `WholesaleConflicts`, comentarios que se dirigen al lector) en español
  colombiano tuteando; identificadores, códigos de error y nombres de propiedad en inglés y tal cual.
- Códigos y nombres que fija el spec: `wholesale.distributor.city_not_found`; propiedades de validación `CityId`, `CityName`,
  `Address`; topes `CityName` 120 y `Address` 200; columnas `city_id`, `city_name`, `address`.
- `docs/estado.md` **no** se toca en la rama: se actualiza en `develop` al mergear. El merge termina en `develop`, nunca en `main`.

## Review Focus

1. **Reintento D-17 con cuerpo `LinkTenant`** después de un primer intento `NewTenant` que falló al crear la empresa: la empresa se
   tiene que crear igual (prueba en la Tarea 5).
2. **Dos pestañas a la vez** sobre una invitación nueva: una sola empresa en el tenant nuevo (se amplía
   `TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer` en la Tarea 6).
3. **Invitación vieja sin ciudad** (filas de antes de la migración): acepta sin empresa y sin error (Tarea 5).
4. **Documento que contiene a otro** (`900123456` frente a `9001234567`): el `ILIKE` de `SearchAsync` los mezcla; el reuso exige
   igualdad exacta de tipo y número (Tarea 1).
5. **Ciudad inexistente al invitar:** 422 `wholesale.distributor.city_not_found`, no una invitación que después no se puede aceptar
   (Tarea 4).

---

## Bloque A — Companies (un implementador; revisión al final del bloque)

### Tarea 1: `ICompanyProvisioning` con creador compartido y candado

**Archivos:**
- Crear: `src/Modules/Companies/Modules.Companies.Application/CompanyCreation.cs` (creador interno)
- Crear: `src/Modules/Companies/Modules.Companies.Application/CompanyProvisioning.cs` (puerto, datos, candado e implementación)
- Modificar: `src/Modules/Companies/Modules.Companies.Application/CreateCompany.cs` (el handler usa `CompanyCreation`)
- Modificar: `src/Modules/Companies/Modules.Companies.Application/ICompaniesUnitOfWork.cs`
- Modificar: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/CompaniesUnitOfWork.cs`
- Modificar: `src/Modules/Companies/Modules.Companies.Infrastructure/CompaniesInfrastructureExtensions.cs` (registrar
  `ICompanyProvisioning`)
- Crear: `tests/Modules/Companies/Modules.Companies.UnitTests/CompaniesTestDoubles.cs` (mover ahí `InMemoryCompanyRepository` y
  `NoOpUnitOfWork` de `CreateCompanyDefaultTaxRatesTests.cs`, ampliados)
- Crear: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyProvisioningTests.cs`

**Interfaces que produce** (en `Modules.Companies.Application`, públicas):

```csharp
public sealed record ProvisionedCompanyData(
    string Name, string Country, string DocumentType, string TaxId,
    Guid? CityId, string? CityName, string? Address, string? Email, string? Phone);

public interface ICompanyProvisioning
{
    /// Devuelve el id de la empresa creada o de la que ya tenía ese tipo y número de documento (activa o inactiva).
    Task<Guid> EnsureCompanyAsync(Guid tenantId, Guid actorUserId, ProvisionedCompanyData data, CancellationToken cancellationToken);
}

public static class CompanyProvisioningLock { public const int Namespace = 20261132; }

public interface ICompaniesTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken cancellationToken); }

// ICompaniesUnitOfWork suma:
Task<ICompaniesTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
Task LockProvisioningAsync(Guid tenantId, CancellationToken cancellationToken); // exige transacción abierta
```

Antes de fijar `20261132`, corre `rg "Namespace = 2026|LockNamespace = 2026" src` y confirma que nadie la usa (los candados de dos
claves comparten espacio; ver el comentario de `EmailChallengeIssuanceLock`). Si está tomada, usa la siguiente libre.

- [ ] **Paso 1: mueve los dobles y escribe las pruebas que fallan**

Mueve `InMemoryCompanyRepository` y `NoOpUnitOfWork` a `CompaniesTestDoubles.cs` (internos, sin cambiar su comportamiento para
`CreateCompanyDefaultTaxRatesTests`). Amplía el doble de unidad de trabajo para que anote en orden `"begin"`, `"lock:{tenantId}"`,
`"save"`, `"commit"` en una lista `Log`, y el repositorio para que `SearchAsync` filtre `taxId` por **contención sin distinguir
mayúsculas** (como el `ILIKE` real) y `status` nulo = todas. En `CompanyProvisioningTests` (mismo armado de dependencias que
`CreateCompanyDefaultTaxRatesTests`: geografía falsa con una ciudad conocida, tasas falsas que anotan llamadas):

```csharp
[Fact] Task CreatesTheCompanyWithTheGivenDataAndAuditsWithTheActor()
//  EnsureCompanyAsync(tenant, actor, Data("CO","NIT","900123456", cityId: KnownCity)) → una empresa en el repo con Name, TaxId,
//  Country "CO", DocumentType Nit, CityId, Address, Email, Phone; ChargesVat true; una entrada de auditoría
//  "companies.company.created" con actorId = actor.
[Fact] Task TakesTheLockInsideTheTransactionBeforeLookingAndCommitsAfterSaving()
//  Log == ["begin", $"lock:{tenant}", "save", "commit"]
[Fact] Task ReusesAnExistingCompanyWithTheSameTypeAndNumberWithoutUpdatingIt()
//  repo con una empresa NIT 900123456 de otro nombre → devuelve su id, el repo sigue con 1, el nombre no cambió, no hay "save".
[Fact] Task ReusesAnInactiveCompanyToo()
[Fact] Task DoesNotReuseACompanyWhoseNumberOnlyContainsTheRequestedOne()
//  repo con NIT 9001234567 → crea otra para 900123456 (2 en el repo).
[Fact] Task DoesNotReuseTheSameNumberWithAnotherDocumentType()
//  repo con CC 900123456 → crea la NIT.
[Fact] Task AColombianCompanyWithoutCityIsRejectedWithTheCityIdField()
//  cityId null → ValidationException con un error de PropertyName "CityId"; repo vacío; sin "commit".
[Fact] Task AnUnknownCityIsCompaniesCityNotFound()
//  CompaniesDomainException con Code "companies.company.city_not_found".
[Fact] Task SeedsTheDefaultRatesAfterCommitForAColombianCompany()
//  las tasas falsas reciben EnsureColombianVatAsync(tenant, actor) una vez, y en el Log va después de "commit".
[Fact] Task DoesNotSeedRatesForACompanyOutsideColombia()
//  Data("ES","NIF","B12345678", cityName: "Madrid") → sin llamada a las tasas.
```

- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Companies/Modules.Companies.UnitTests`. Esperado: falla de compilación
  (`ICompanyProvisioning`, `ProvisionedCompanyData`, `BeginTransactionAsync` no existen). Guarda la salida.

- [ ] **Paso 3: extrae `CompanyCreation`**

`internal sealed class CompanyCreation` (sin DI: el handler y `CompanyProvisioning` la construyen con sus dependencias inyectadas:
repositorio, unidad de trabajo, auditoría, geografía, reloj, `IValidator<CreateCompanyCommand>`, `ICompanyTaxRateLookup`,
`ICompanyDefaultTaxRates`, `ILogger`). Dos métodos, con el cuerpo que hoy está en `CreateCompanyHandler.HandleAsync` después de
`EnsureAuthorized`, partido en el punto del commit:

```csharp
// normaliza el país, valida, resuelve ciudad, valida retenciones, Company.Create, Add, audita con actorId y SaveChangesAsync.
Task<(Company Company, CompanyCityRef? City, IReadOnlyList<...> WithholdingRates)> CreateAsync(
    CreateCompanyCommand command, Guid actorId, CancellationToken cancellationToken);
// CompanyDefaultTaxRateSeeding.Applies + TryEnsureAsync, como hoy.
Task SeedDefaultsAsync(Company company, Guid actorId, CancellationToken cancellationToken);
```

El tipo de las retenciones es el que hoy devuelve `taxRateLookup.FindManyAsync`. `CreateCompanyHandler` queda: `EnsureAuthorized`
→ `CreateAsync(command, executionContext.SubjectId)` → `SeedDefaultsAsync` → `ToDto` como hoy. Conserva los comentarios del
handler en el lugar donde quede cada paso.

- [ ] **Paso 4: implementa `CompanyProvisioning` y la unidad de trabajo**

`public sealed class CompanyProvisioning(...) : ICompanyProvisioning`. `EnsureCompanyAsync`: `await using` la transacción →
`LockProvisioningAsync(tenantId)` → `SearchAsync(tenantId, null, null, data.TaxId.Trim(), status: null)` y filtra en memoria
`DocumentType == CompanyDocumentTypes.Parse(...)` y `TaxId == data.TaxId.Trim()` (ordinal); si hay, devuelve el id → si no, arma
`CreateCompanyCommand(tenantId, data.Name, [], data.TaxId, data.Country, data.DocumentType, data.CityId, data.CityName,
data.Phone, data.Email, data.Address)` y llama `CreateAsync(command, actorUserId)` → `CommitAsync` → `SeedDefaultsAsync` →
devuelve el id. Si el tipo no se reconoce, `CompanyDocumentTypes.Parse` falla igual que el validador: valida antes de buscar
(`CompanyDocumentTypes.TryParse`; si no, deja que `CreateAsync` dé la `ValidationException`). En `CompaniesUnitOfWork`, copia el
patrón de `SiigoUnitOfWork.BeginTransactionAsync`/`LockOrderAsync` (incluida la `InvalidOperationException` sin transacción) con
`SELECT pg_advisory_xact_lock({CompanyProvisioningLock.Namespace}, hashtext({tenantId.ToString()}))`. Registra
`services.AddScoped<ICompanyProvisioning, CompanyProvisioning>();` en `AddCompaniesInfrastructure`, como `CustomerProvisioning` en
Customers. Documenta en el XML del puerto por qué no hay permiso (spec §2) y por qué el candado (no hay índice único, EMP-08).

- [ ] **Paso 5: GREEN** —
  ```powershell
  dotnet test tests/Modules/Companies/Modules.Companies.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompaniesLayerTests"
  ```
  Esperado: todo verde, incluidas las `CreateCompanyDefaultTaxRatesTests` existentes.

- [ ] **Paso 6: integración del handler (red del refactor)** —
  `dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --filter "FullyQualifiedName~CompanyWriteApiTests|FullyQualifiedName~CompanyDefaultTaxRatesApiTests" -- xUnit.MaxParallelThreads=4`.
  Esperado: verde.

- [ ] **Paso 7: commit**
  ```powershell
  git add src/Modules/Companies tests/Modules/Companies
  git commit -m "feat(companies): add system company provisioning with tenant lock"
  ```

**Cierre del bloque A:** clase completa `dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests -- xUnit.MaxParallelThreads=4`
y una revisión del bloque (un lente: `review-reliability`).

---

## Bloque B — Invitar con ciudad y dirección (un implementador; revisión al final del bloque)

### Tarea 2: `Distributor` con ciudad y dirección, y su migración

**Archivos:**
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Domain/Distributor.cs`
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleDbContext.cs` (junto a `phone`, línea ~166)
- Crear: migración `AddDistributorCityAndAddress` en `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorTests.cs`,
  `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleDbContextMappingTests.cs`

**Interfaces que produce:**

```csharp
// Distributor
public Guid? CityId { get; private set; }
public string? CityName { get; private set; }
public string? Address { get; private set; }
public const int CityNameMaxLength = 120;   // = Company.CityNameMaxLength
public const int AddressMaxLength = 200;    // = CompanyContactInfo.AddressMaxLength
/// La ciudad que pide el país para crear su empresa: CityId en "CO", CityName afuera.
public bool HasCompanyCity { get; }
// Invite suma, al final y opcionales (para no tocar las llamadas existentes):
public static Distributor Invite(..., DateTimeOffset now, Guid? cityId = null, string? cityName = null, string? address = null)
```

- [ ] **Paso 1: pruebas que fallan** en `DistributorTests`:
  `InviteKeepsCityAndAddressTrimmed` (`cityName: "  Madrid "`, `address: " Calle 1 "` → `"Madrid"`, `"Calle 1"`),
  `InviteTurnsBlankCityNameAndAddressIntoNull`, `InviteTreatsAnEmptyCityIdAsNull` (`Guid.Empty` → `null`),
  `HasCompanyCityFollowsTheCountry` (CO con `CityId` → true; CO sin `CityId` y con `CityName` → false; ES con `CityName` → true;
  ES sin nada → false). En `WholesaleDbContextMappingTests`, que `city_id`, `city_name` (120) y `address` (200) estén mapeadas y
  sean nullable (mismo estilo que las asserts de `phone` en ese archivo).
- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` (falla: `CityId` no existe).
- [ ] **Paso 3: implementa** propiedades, `Invite` y mapeo; genera la migración:
  ```powershell
  dotnet ef migrations add AddDistributorCityAndAddress --project src/Modules/Wholesale/Modules.Wholesale.Infrastructure --context WholesaleDbContext -o Persistence/Migrations
  ```
  Revisa que el `Up` sólo agregue tres columnas nullable (expansión pura, sin backfill) y que el `Down` las quite.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~DistributorTests|FullyQualifiedName~WholesaleDbContextMappingTests"`.
- [ ] **Paso 5: commit** — `git commit -m "feat(wholesale): store distributor city and address"` (con los archivos de arriba).

### Tarea 3: invitar valida y guarda ciudad y dirección; el detalle las devuelve

**Archivos:**
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/InviteDistributor.cs` (comando, handler, `WrittenFields`)
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorWriteRules.cs` (`InviteDistributorValidator`,
  `DistributorTextLimits`)
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/Ports.cs` (`IWholesaleCityLookup`)
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Domain/WholesaleErrorCodes.cs`,
  `src/Modules/Wholesale/Modules.Wholesale.Application/WholesaleConflicts.cs`
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorDtos.cs`, `DistributorComposer.cs`
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Api/WholesaleEndpoints.cs` (`DistributorInviteRequest` y el mapeo al comando)
- Crear: `src/Bootstrapper/WholesaleCityLookup.cs`; registrar en `QepServiceCollectionExtensions.cs` junto a los demás puertos de
  Wholesale (línea ~2496)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorWriteRulesTests.cs`, `DistributorHandlersTests.cs`,
  `DistributorComposerTests.cs`, `DistributorFakes.cs`, `DistributorWorld.cs`
- Test (cuerpos crudos de invitar que ahora necesitan ciudad):
  `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/WholesaleApiHarness.cs` (`InviteAsync`, línea ~365),
  `DistributorApiTests.cs` (líneas ~29 y ~70), `DistributorWhatsAppNoticeApiTests.cs` (línea ~328),
  `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/Linked/LinkedPurchasesHarness.cs` (línea ~132)

**Interfaces:**
- Consume: `Distributor.Invite(..., cityId, cityName, address)` y los topes de la Tarea 2.
- Produce:

```csharp
// InviteDistributorCommand y DistributorInviteRequest suman al final:
Guid? CityId = null, string? CityName = null, string? Address = null
// Ports.cs
public sealed record WholesaleCityRef(Guid Id, string Name);
public interface IWholesaleCityLookup { Task<WholesaleCityRef?> FindAsync(Guid cityId, CancellationToken cancellationToken); }
// WholesaleErrorCodes
public const string DistributorCityNotFound = "wholesale.distributor.city_not_found";
// WholesaleConflicts
public static WholesaleDomainException CityNotFound()  // mensaje: "La ciudad no existe."
// DistributorDtos.cs
public sealed record DistributorCityDto(Guid? Id, string Name);
// DistributorDetailDto suma después de InvitationUrl:
DistributorCityDto? City = null, string? Address = null
```

- [ ] **Paso 1: pruebas que fallan**

`DistributorWriteRulesTests` (validador; usa el armado de comando que ya tiene el archivo):
`AColombianInvitationNeedsTheCityId` (sin `CityId` → error en `CityId`), `AnEmptyCityIdIsMissing` (`Guid.Empty` → error en
`CityId`), `AnInvitationOutsideColombiaNeedsTheCityName` (ES sin `CityName` → error en `CityName`), `TheCityNameHasAtMost120`,
`TheAddressIsOptionalAndHasAtMost200`, `AColombianInvitationDoesNotNeedTheCityName`, `ABadCountryOnlyFailsOnCountry` (país `"C"`
sin ciudad → errores sólo en `Country`, como `CompanyWriteRules`).

`DistributorHandlersTests` (handler de invitar; agrega `FakeCityLookup` a `DistributorFakes` con un diccionario de ciudades y una
lista `Calls`, y pásalo en `DistributorWorld`):
`InviteWithAnUnknownCityIsCityNotFound` (→ `WholesaleDomainException` con `Code == "wholesale.distributor.city_not_found"`, nada
guardado), `InviteSavesCityAndAddress` (el distribuidor guardado tiene `CityId`, `Address`; la auditoría nombra `cityId`,
`cityName`, `address`), `InviteOutsideColombiaDoesNotLookUpTheCity` (ES → `FakeCityLookup.Calls` vacío).

`DistributorComposerTests`: `TheDetailCarriesTheResolvedColombianCity` (`City == new DistributorCityDto(id, "Medellín")`),
`TheDetailCarriesTheTypedCityOutsideColombia` (`City == new(null, "Madrid")`), `TheDetailWithoutCityHasNullCity`.

- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` (falla de compilación).
- [ ] **Paso 3: implementa**
  - Validador: replica las reglas de ciudad de `CompanyWriteRules` (`src/Modules/Companies/Modules.Companies.Application/CompanyWriteRules.cs:95-115`)
    con los mismos nombres de propiedad y el mismo `When` por país válido; mensaje `"The city is required."`. No incluyas ni
    referencies Companies (lo prohíbe `WholesaleLayerTests`). Comentario: por qué se replica y qué archivo hay que cambiar junto.
  - Handler: después de `DistributorTerms.EnsureValidAsync` y antes de `IdentificationExistsAsync`, si el país normalizado es `CO`,
    `cities.FindAsync(command.CityId!.Value)` o `throw WholesaleConflicts.CityNotFound()`; actualiza el orden del XML del handler
    (`… 422 empresa → 422 ciudad → 409 documento …`). Pasa ciudad y dirección a `Distributor.Invite`; suma `"cityId"`,
    `"cityName"`, `"address"` a `WrittenFields`.
  - Composer: inyecta `IWholesaleCityLookup`; en `DetailAsync`, `City` = la ciudad resuelta si hay `CityId` (si el lookup devuelve
    `null`, `new(CityId, UnknownName)`), `new(null, CityName)` si hay `CityName`, si no `null`. `SummariesAsync` no cambia. En el
    XML del DTO, el motivo BFF (la pantalla pinta el nombre sin conocer Geography).
  - `WholesaleCityLookup` (Bootstrapper): copia de `CompanyGeographyLookup.FindCityAsync` que devuelve `WholesaleCityRef(city.Id,
    city.Name)`; sin departamento.
  - Endpoint: pasa `request.CityId`, `request.CityName`, `request.Address` al comando.
  - Harness de integración: `WholesaleApiHarness.InviteAsync` manda `cityId = await MedellinAsync(client)` cuando `country` es `CO`
    y `cityName = "Madrid"` si no, y `address = "Calle 10 # 40-20"`. `MedellinAsync` ya existe en el harness
    (`private`, línea ~441): hazlo `public`, que lo usan también la Tarea 4 y la 6. Barre los cuerpos crudos listados arriba
    (`rg "identificationType\s*=" tests --glob "*.cs"` para no dejar ninguno): con `CO` agregan `cityId`; con otro país, `cityName`.
- [ ] **Paso 4: GREEN** —
  ```powershell
  dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WholesaleLayerTests|FullyQualifiedName~CompositionRootTests"
  ```
- [ ] **Paso 5: prueba de integración nueva** en `DistributorApiTests`:
  `InviteWithAnUnknownCityIs422CityNotFound` (cityId = `Guid.NewGuid()` → 422 y el cuerpo contiene
  `wholesale.distributor.city_not_found`), `InviteWithoutTheCityIs422OnCityId` (sin `cityId` → 422 con `"CityId"`),
  `TheDetailCarriesCityAndAddress` (después de invitar con el harness, `GET` del detalle → `city.name` empieza por `"Medell"` y
  `address == "Calle 10 # 40-20"`). Corre filtrado:
  `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~InviteWithAnUnknownCity|FullyQualifiedName~InviteWithoutTheCity|FullyQualifiedName~TheDetailCarriesCityAndAddress" -- xUnit.MaxParallelThreads=4`.
- [ ] **Paso 6: commit** — `git commit -m "feat(wholesale): capture and validate distributor city and address on invite"`.

**Cierre del bloque B:** clases completas `DistributorApiTests` y `DistributorWhatsAppNoticeApiTests`
(`--filter "FullyQualifiedName~DistributorApiTests|FullyQualifiedName~DistributorWhatsAppNoticeApiTests"`), y una revisión del
bloque (un lente: `review-reliability`).

---

## Bloque C — Empresa y teléfono al aceptar (un implementador; revisión al final del bloque)

### Tarea 4: puerto de empresa, adaptador y teléfono del cliente

**Archivos:**
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/Ports.cs`
- Crear: `src/Bootstrapper/WholesaleCompanyProvisioning.cs`; registrar junto a `WholesaleCustomerProvisioning`
  (`QepServiceCollectionExtensions.cs:2501`)
- Modificar: `src/Bootstrapper/WholesaleCustomerProvisioning.cs`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/WholesaleAdaptersTests.cs` (llamadas de las líneas ~128, 135,
  158, 184), `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorFakes.cs` (`FakeCustomerProvisioning`)

**Interfaces:**
- Consume: `ICompanyProvisioning`, `ProvisionedCompanyData` (Tarea 1).
- Produce:

```csharp
public sealed record WholesaleCompanyDraft(
    string Name, string Country, string DocumentType, string TaxId,
    Guid? CityId, string? CityName, string? Address, string? Email, string? Phone);
public interface IWholesaleCompanyProvisioning
{
    Task<Guid> EnsureCompanyAsync(Guid distributorTenantId, Guid actorUserId, WholesaleCompanyDraft draft, CancellationToken cancellationToken);
}
// IWholesaleCustomerProvisioning.EnsureCustomerAsync suma `string? phone` justo antes del CancellationToken.
```

- [ ] **Paso 1: pruebas que fallan** en `WholesaleAdaptersTests` (adaptadores reales contra el contenedor; mira si la clase usa
  host compartido y respeta la regla de `pruebas-y-entorno-local.md`):
  `TheCustomerIsBornWithThePhone` (`EnsureCustomerAsync(..., "ana@distri.co", "+573001234567", Ct)` → `customers.customers.phone_e164
  = '+573001234567'` para ese tenant y documento), `TheCompanyAdapterCreatesOnceAndReuses` (dos llamadas con el mismo borrador NIT
  `900123456` en Medellín sobre un tenant sembrado → el mismo id y `count(*) = 1` en `companies.companies` de ese tenant). Actualiza
  las cuatro llamadas existentes con `phone: null`.
- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` (falla de compilación).
- [ ] **Paso 3: implementa** el puerto, `WholesaleCompanyProvisioning` (traduce el borrador a `ProvisionedCompanyData` campo por
  campo y llama `EnsureCompanyAsync`; sus excepciones salen tal cual; XML con el porqué, como `WholesaleCustomerProvisioning`), y el
  teléfono en `WholesaleCustomerProvisioning` (cuarto argumento de `RegisterCustomerData`). En el servicio de aceptar, por ahora,
  pasa `distributor.Phone` en la llamada al cliente (la empresa entra en la Tarea 5). Actualiza `FakeCustomerProvisioning` para
  anotar el teléfono.
- [ ] **Paso 4: GREEN** — build de la solución de pruebas tocadas y
  `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~TheCustomerIsBornWithThePhone|FullyQualifiedName~TheCompanyAdapterCreatesOnceAndReuses" -- xUnit.MaxParallelThreads=4`.
- [ ] **Paso 5: commit** — `git commit -m "feat(wholesale): add company provisioning port and pass the phone to the customer"`.

### Tarea 5: el accept crea la empresa en el tenant reservado

**Archivos:**
- Modificar: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorInvitationService.cs`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorInvitationServiceTests.cs`, `DistributorFakes.cs`,
  `DistributorWorld.cs` (`SeedInvited` suma `Guid? cityId`, `string? cityName`, `string? address`; por defecto, una ciudad fija
  para `CO` y `"Madrid"` para otro país, para que las pruebas existentes sigan creando empresa)

**Interfaces:**
- Consume: `IWholesaleCompanyProvisioning`, `WholesaleCompanyDraft` (Tarea 4); `Distributor.HasCompanyCity`, `CityId`, `CityName`,
  `Address` (Tarea 2).
- Produce: el constructor de `DistributorInvitationService` suma `IWholesaleCompanyProvisioning companyProvisioning` (después de
  `customerProvisioning`) e `ILogger<DistributorInvitationService> logger` (al final).

- [ ] **Paso 1: pruebas que fallan** (agrega `FakeCompanyProvisioning` con `Calls` de `(Guid Tenant, Guid Actor,
  WholesaleCompanyDraft Draft)` y que puede lanzar una excepción configurada):

```csharp
[Fact] Task NewTenantCreatesTheCompanyInTheReservedTenantWithTheDistributorData()
//  Calls == 1; Tenant == reserved; Actor == _user; Draft == new("Distribuciones 900123456", "CO", "NIT", "900123456",
//  <cityId sembrada>, null, <address sembrada>, "ana@distri.co", null)
[Fact] Task TheCompanyGetsTheAccountEmailAndTheNoticePhone()
//  SeedInvited(phone: "+573001234567", ...) y aceptar con "otra@cuenta.co" → Draft.Email "otra@cuenta.co", Draft.Phone "+573001234567"
[Fact] Task LinkTenantOnAnExistingTenantDoesNotCreateACompany()
[Fact] Task ARetryOverTheReservedTenantCreatesTheCompanyEvenWithALinkBody()
//  armado como AReservedTenantThatExistsWinsOverTheRequestedMode (línea ~459): el reservado existe y es del usuario; AcceptAsync(new LinkTenantChoice(otro))
//  → Calls == 1 con Tenant == reserved
[Fact] Task AnInvitationWithoutTheCityItsCountryNeedsAcceptsWithoutACompany()
//  SeedInvited(cityId: null) en CO → Calls vacío, Status Active, el cliente igual se crea
[Fact] Task ACompanyFailureLeavesTheInvitationPendingAndNothingElseWritten()
//  FakeCompanyProvisioning lanza ValidationException → la excepción sale; _suppliers.Calls y _customers.Calls vacíos;
//  UnitOfWork.Saves == 0; Status Invited
[Fact] Task TheCustomerGetsTheNoticePhone()
```

Agrega `Assert.Empty(_companies.Calls)` a `AssertNothingWritten()`.

- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` (falla: el constructor no recibe el
  puerto). Después de agregar el parámetro sin usarlo, `dotnet test … --filter "FullyQualifiedName~DistributorInvitationServiceTests"`
  tiene que fallar en las asserts nuevas (guarda esa salida también).
- [ ] **Paso 3: implementa** en `AcceptAsync`, entre `ResolveTargetAsync` y `EnsureLinkedSupplierAsync`:
  si `created && distributor.HasCompanyCity` → `companyProvisioning.EnsureCompanyAsync(target, userId, draft)` con el mismo correo
  que el cliente (extrae la expresión `string.IsNullOrWhiteSpace(userEmail) ? distributor.Email : userEmail.Trim().ToLowerInvariant()`
  a una variable y úsala en los dos); si `created` y no tiene ciudad → `LogInformation` con `distributor.Id` y nada más (usa
  `[LoggerMessage]`, por CA1873). Actualiza el XML de la clase: el orden pasa a `5) destino; 6) empresa en el tenant reservado;
  7) proveedor; 8) cliente; 9) …`, y explica que la condición es `created` y no el modo del cuerpo (D-17).
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`.
- [ ] **Paso 5: commit** — `git commit -m "feat(wholesale): create the distributor company when accepting into the reserved tenant"`.

### Tarea 6: punta a punta y documentación

**Archivos:**
- Test: `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/InvitationAcceptanceApiTests.cs`
- Modificar: `README.md` (fila de `/wholesale/distributors`, línea ~1387, y la sección de la invitación de mayorista)
- Modificar: `.claude/rules/mayorista-compras.md`

- [ ] **Paso 1: pruebas** (después de la Tarea 5 deberían pasar; si alguna falla, es un hallazgo real — diagnostícalo, no ajustes la
  prueba):
  - Amplía `TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer`: también `count(*) = 1` en `companies.companies` con
    `tenant_id = accepted[0].TenantId` (el assert pasa a una cuádrupla).
  - `NewTenantCreatesTheCompanyAndSeedsTheColombianVat`: aceptar `NewTenant` → una empresa en el tenant nuevo con `name =`
    razón social, `tax_id = '900123456'`, `city_id` = Medellín, `address = 'Calle 10 # 40-20'`; y al menos una fila en
    `catalog.tax_rates` de ese tenant con `percentage = 19` (cubre el riesgo del `QuotationsDbContext`, spec §3).
  - `ARetriedAcceptDoesNotDuplicateTheCompany`: aceptar, luego repetir el `POST` con el mismo usuario → 200 y sigue habiendo 1.
  - `AnOldInvitationWithoutCityAcceptsWithoutACompany`: la API ya no deja invitar sin ciudad, así que invita con el harness y
    simula la fila vieja con `UPDATE wholesale.distributors SET city_id = NULL, address = NULL WHERE id = …`;
    aceptar → 200, tenant creado y 0 empresas.
  - `TheWholesalerCustomerIsBornWithThePhone`: invitar con `phone` (como `DistributorWhatsAppNoticeApiTests`) y aceptar →
    `phone_e164` del cliente en el tenant del mayorista.

  Revisa la regla de host compartido de `pruebas-y-entorno-local.md` para cada una (el `UPDATE` es por id: puede ir en el host
  compartido). Corre filtrado por nombre hasta que estén verdes.
- [ ] **Paso 2: documentación** — README: el `POST` de distribuidores lleva `cityId` (CO) o `cityName` (otro país) y `address`, el
  422 `wholesale.distributor.city_not_found`, el detalle devuelve `city` y `address`, y aceptar sobre el tenant reservado crea la
  empresa. Regla `mayorista-compras.md`: un punto nuevo — «La empresa del distribuidor nace en el accept, sólo si `created`
  (también en un reintento D-17 con cuerpo `LinkTenant`), idempotente por tipo y número exactos bajo
  `CompanyProvisioningLock`; una invitación sin la ciudad de su país acepta sin empresa». Corrige el punto de `NewTenant` que dice
  que no lee nombre ni slug del cuerpo: el código sí los lee (`WholesaleInvitationEndpoints.cs:323-327`).
- [ ] **Paso 3: commit** — `git commit -m "test(wholesale): cover the company created on accept end to end"` y, aparte,
  `git commit -m "docs(wholesale): document distributor city, address and company on accept"`.

**Cierre del bloque C:** clase completa `InvitationAcceptanceApiTests` y `WholesaleAdaptersTests`, y una revisión del bloque (un
lente: `review-reliability`).

---

## Antes de mergear a `develop`

- [ ] Suites completas, **una a la vez** (mira antes `Get-Process testhost, dotnet`), con `$env:Seed__Enabled = "false"`:
  ```powershell
  dotnet test tests/Modules/Companies/Modules.Companies.UnitTests
  dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
  dotnet test tests/ArchitectureTests/ArchitectureTests
  dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests -- xUnit.MaxParallelThreads=4
  dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests -- xUnit.MaxParallelThreads=4
  dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~Linked" -- xUnit.MaxParallelThreads=4
  ```
  (Purchases sólo por el harness de invitar que cambió.) Customers no cambia de código: no se corre.
- [ ] Revisión fuerte de toda la rama (modelo más capaz). Toca integración entre módulos y un candado nuevo: corre las 4R.
- [ ] Merge a `develop` (nunca a `main`) y, en `develop`, actualiza `docs/estado.md` (fila de Mayorista y Registro) en el mismo
  merge o en un commit `docs:` inmediato.
- [ ] Avisa al owner: el frontend (`qep-frontend`) necesita su spec para el formulario de invitar (ciudad y dirección) y el detalle;
  hasta que salga, **el formulario actual no puede invitar en Colombia** (decisión pendiente 4 del spec: desplegar juntos).
