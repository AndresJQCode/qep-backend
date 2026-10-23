# Proveedores (directorio) — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un módulo `Suppliers` nuevo con el directorio de proveedores del tenant: CRUD sin borrado, activar y
desactivar, lista paginada con búsqueda, estado y categoría, exportación a Excel por correo, auditoría, permisos y
clave de módulo `suppliers`.

**Architecture:** Cuatro proyectos `Modules.Suppliers.{Domain,Application,Infrastructure,Api}` que copian la forma de
`Companies` (agregado con colecciones `OwnsMany`, UnitOfWork que traduce errores de PostgreSQL, publicador de auditoría
al outbox) y toman de `Customers` la paginación con `Total`, la identidad (`IdentificationType`, país y carril doble de
ciudad, `PhoneE164`) y la exportación por correo (builder ClosedXML, storage R2, evento al outbox y worker en
`Notifications`). Application sólo referencia `Modules.Tenancy.*`; Geography entra por un puerto con adaptador en
`Bootstrapper`. Nada se comparte con `Customers` ni `Companies`: se copia (spec D-11).

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, ClosedXML, libphonenumber-csharp, xUnit v3
+ Testcontainers.

**Spec:** `../qep-frontend/docs/superpowers/specs/2026-10-23-proveedores-design.md` (léela entera; §5 es el modelo,
§6 el contrato, §7 los códigos). El frontend tiene su plan en
`../qep-frontend/docs/superpowers/plans/2026-10-23-proveedores-frontend.md` y depende de la sección «Contrato para el
frontend» al final de este plan.

**Rama:** `feature/proveedores` desde `develop`, en el worktree `.claude/worktrees/proveedores` (ya creado:
`git worktree add .claude/worktrees/proveedores -b feature/proveedores develop`). Antes de **cada** tarea y de **cada**
commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea.
- Conventional commits en español con el slice `SUP-NN` en el asunto, **sin atribución de IA** (sin `Co-Authored-By`;
  la regla del repo pisa cualquier recordatorio del harness).
- No corras la suite completa: cada comando lleva `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de
  integración. `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `SuppliersAuthorization.EnsureAuthorized`
  en el handler antes de tocar un puerto. Otro tenant → 403 `authorization.denied`, nunca 404. Todo método de puerto y de
  repositorio recibe `tenantId`.
- Permiso nuevo = **dos mitades** en `QepServiceCollectionExtensions`: `PermissionDefinition` y `AddPolicy`. Sin la
  política el síntoma es 500. Los handlers se registran a mano; sin registro, 500.
- `Modules.Suppliers.Application` referencia sólo `Modules.Tenancy.*` entre los módulos de negocio
  (`SuppliersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`). Geography entra por
  `ISupplierGeographyLookup`; el adaptador vive en `src/Bootstrapper/`.
- Texto libre en un comando → validador de FluentValidation (422 `validation.failed` con `errors` en PascalCase, con
  índice en listas: `BankAccounts[2].Currency`) aunque el dominio valide. Ningún status a mano: lo mapea
  `src/Api/ApiExceptionHandler.cs`.
- Códigos, longitudes y normalización del núcleo de identidad **idénticos** a `Customer` (spec §5): `Name` 160,
  `BusinessName` 160, `IdentificationNumber` 32, `Phone` 32, `Email` 254, `Address` 200, `CityName` 120; país alpha-2 en
  mayúsculas; `CO` exige `CityId` y limpia `CityName`, otro país exige `CityName` y limpia `CityId`.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/Suppliers/Modules.Suppliers.Infrastructure --context SuppliersDbContext -o Persistence/Migrations`.
- Paquetes: ClosedXML y libphonenumber-csharp ya están en `Directory.Packages.props` (`:8`, `:20`); cada csproj nuevo
  necesita su `packages.lock.json` (`dotnet restore --force-evaluate`, CLAUDE.md:189-198). Sin versiones nuevas.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma. Textos y comentarios en español colombiano
  tuteando; códigos, rutas, enums y permisos tal cual.
- `docs/estado.md` **no se toca** en esta rama: se actualiza en `develop` al mergear.
- `Directory.Packages.props`, `global.json`, `.mcp.json`: no se tocan.

## Desviaciones del spec (verificadas contra el código el 2026-10-23)

La autoridad es el código. Ninguna cambia una decisión del spec.

| # | Spec dice | El código dice | El plan hace |
|---|---|---|---|
| B1 | `PhoneE164` «con el mismo normalizador (`IPhoneNumberNormalizer`, copia del puerto)» | El puerto es de `Modules.Customers.Domain` y la implementación es `internal` de Customers.Infrastructure (`LibPhoneNumberNormalizer.cs`) | Puerto propio `ISupplierPhoneNormalizer` en `Suppliers.Domain` y `LibPhoneNumberSupplierNormalizer` en `Suppliers.Infrastructure` referenciando `libphonenumber-csharp` (Task 1, Task 5) |
| B2 | `suppliers` «al final de `All`» | `TenantModuleKeysTests.AutomationsIsLastAndDependsOnMessaging` (`:66`) asegura `All[^1] == Automations` | La prueba pasa a `SuppliersIsLastWithoutDependencies` y la de Automations se queda sin el `[^1]` (Task 2) |
| B3 | `billing` recibe `suppliers.*` | `billing` (`QepServiceCollectionExtensions.cs:1254-1280`) hoy no tiene **ningún** permiso de Companies; el spec no decía que los tuviera | Se agregan los dos de suppliers a `billing` tal cual D-14; Companies no cambia |
| B4 | Pruebas de API «como las de Companies» | `CompaniesApiHarness` no siembra tenant ni admite `configureServices`; sin fila en `tenancy.tenants` **no hay máscara de módulos** (`DevelopmentAuthenticationHandler.cs:48-58`) y `TenantClock` tira `tenancy.tenant.not_found`, así que ni «módulo apagado» ni exportar se pueden probar con él | `SuppliersApiHarness` copia el de **Customers** (`CustomersApiHarness.cs:56-92` `SeedTenantAsync`, `:474-500` factory con `configureServices`) (Task 6) |
| B5 | «La cuarta copia» del worker de exportación | `DeliveryWorkersHarnessTests.cs:28` asegura `Assert.Equal(5, …)` workers; `DeliveryWorkersCharacterizationTests.PayloadFor` (`:91-125`) tira `ArgumentOutOfRange` con un evento desconocido | 6 en el harness y un brazo nuevo en `PayloadFor` (Task 8) |
| B6 | Migración de Tenancy «recrea el CHECK» | Además hay listas literales de claves en `TenantModuleKeysTests.cs:13`, `OperatorReadHandlerTests.cs:68`, `TenantModulesApiTests.cs:105,107,113`, `OperatorConsoleApiTests.cs:347`, `SeedStartupTests.cs:144,177,239,266,346` | Todas suman `suppliers` (Task 2) |
| B7 | `GET /categories` «máx. 200» | No hay precedente; `CustomerPaging.Max = 200` es el tope de página | `Take(200)` sobre `DISTINCT category ORDER BY category` en el repositorio |

## Review Focus

1. **Documento repetido contra un proveedor inactivo.** `POST /` con el (tipo, número) de un proveedor desactivado →
   422 `suppliers.supplier.identification_taken`, no 201 ni 500. Prueba: `CreatingWithTheDocumentOfAnInactiveSupplierIs422IdentificationTaken` (Task 6).
2. **Carrera de dos creaciones iguales.** Dos `POST /` simultáneos con el mismo documento: uno 201, el otro 422
   `identification_taken` (el índice único es la única barrera; no hay pre-chequeo). Prueba:
   `TwoConcurrentCreatesWithTheSameDocumentLeaveOneSupplier` (Task 6).
3. **`PUT` que cambia de país.** Un proveedor `CO` con `cityId` que pasa a `US` con `cityName`: la respuesta trae
   `city: null`, `department: null`, `cityName: "Miami"`, y la fila no conserva `city_id`. Prueba:
   `UpdatingFromColombiaToAnotherCountryClearsTheCityId` (Task 6).
4. **Módulo apagado con permiso en el rol.** Tenant sembrado con todos los módulos menos `suppliers`: `GET /` → 403, y
   `GET /api/v1/tenants/{id}/modules` lo reporta `enabled: false`. Prueba: `WithTheModuleOffEveryEndpointIs403` (Task 6).
5. **Exportación con filtro que no deja filas.** `POST /export?status=inactive` en un tenant sin inactivos → 422
   `suppliers.export.empty`, sin fila en el outbox ni archivo subido. Prueba: `ExportWithNoRowsIs422EmptyAndPublishesNothing` (Task 7).

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Suppliers/Modules.Suppliers.Domain/{Modules.Suppliers.Domain.csproj, Supplier.cs, SupplierId.cs, SupplierIdentification.cs, IdentificationType.cs, SupplierContactInfo.cs, SupplierBankAccount.cs, SupplierContact.cs, ISupplierPhoneNormalizer.cs, SuppliersDomainException.cs}` | Crear | Agregado y reglas (spec §5) |
| `src/Modules/Suppliers/Modules.Suppliers.Application/{….csproj, SuppliersPermissions.cs, SuppliersAuthorization.cs, SuppliersDtos.cs, SupplierMapping.cs, SupplierNotFound.cs, SupplierStatusFilter.cs, SupplierPaging.cs, SupplierWriteRules.cs, CreateSupplier.cs, UpdateSupplier.cs, GetSupplier.cs, ListSuppliers.cs, ListSupplierCategories.cs, ActivateSupplier.cs, DeactivateSupplier.cs, ExportSuppliers.cs, ISupplierRepository.cs, ISuppliersUnitOfWork.cs, ISuppliersAuditPublisher.cs, ISupplierGeographyLookup.cs, ISupplierExportBuilder.cs, ISupplierExportStorage.cs, ISupplierExportEventPublisher.cs, SupplierAuditActions.cs}` | Crear | Casos de uso, puertos, contrato |
| `src/Modules/Suppliers/Modules.Suppliers.Infrastructure/{….csproj, SuppliersInfrastructureExtensions.cs, SuppliersDatabaseInitializer.cs, Persistence/SuppliersDbContext.cs, Persistence/SuppliersDbContextFactory.cs, Persistence/SuppliersOutboxMessage.cs, Persistence/SupplierRepository.cs, Persistence/SuppliersUnitOfWork.cs, Persistence/SuppliersAuditPublisher.cs, Persistence/SupplierExportEventPublisher.cs, Phones/LibPhoneNumberSupplierNormalizer.cs, Excel/ClosedXmlSupplierExportBuilder.cs, Persistence/Migrations/*_InitialSuppliers.cs}` | Crear | Persistencia, outbox, Excel, teléfono |
| `src/Modules/Suppliers/Modules.Suppliers.Api/{….csproj, SupplierEndpoints.cs}` | Crear | Rutas (spec §6.1) |
| `src/Bootstrapper/SupplierGeographyLookup.cs`, `src/Bootstrapper/SupplierExportStorage.cs` | Crear | Adaptadores (copias de `CompanyGeographyLookup.cs`, `CustomerExportStorage.cs`) |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers (después de `:247`), validadores (`:785-792`), `AddSuppliersInfrastructure` (después de `:799`), adaptadores (después de `:980`, `:961`), roles admin `:1098-1198` y billing `:1254-1280`, definiciones (después de `:1409`), políticas (después de `:1911`) |
| `src/Bootstrapper/Bootstrapper.csproj` (`:16-17`), `src/Api/Api.csproj` (`:17-18`), `Backend.slnx` (`:35-39`, `:150-152`) | Modificar | Referencias y solución |
| `src/Api/Program.cs` | Modificar | `MapSupplierEndpoints()` después de `:175`; `InitializeSuppliersDatabaseAsync` después de `:235` (después de Geography `:230-231`) |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (`:57-62`), `…Infrastructure/Persistence/TenancyDbContext.cs` (`:112-114`), `…Infrastructure/Persistence/Migrations/*_AddSuppliersModuleKey.cs` | Modificar / Crear (ef) | Clave `suppliers` |
| `src/Modules/Notifications/Modules.Notifications.Application/SupplierExportEmailTemplate.cs`, `…Infrastructure/Messaging/SupplierExportDeliveryWorker.cs`, `…Infrastructure/NotificationsInfrastructureExtensions.cs` (`:46-51`) | Crear / Modificar | Correo de exportación |
| `README.md` (`:649-651` módulos; tabla de endpoints nueva junto a la de Empresas), `CLAUDE.md:269` | Modificar | Documentación |
| `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/{….csproj, SupplierTests.cs, SupplierBankAccountTests.cs, SupplierContactTests.cs, SupplierWriteRulesTests.cs, SupplierStatusFilterTests.cs}` | Crear | Dominio y validación |
| `tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests/{….csproj, SuppliersApiHarness.cs, SupplierApiTests.cs, SupplierWriteApiTests.cs, SupplierListApiTests.cs, SupplierActivationApiTests.cs, SupplierExportApiTests.cs}` | Crear | API contra PostgreSQL |
| `tests/ArchitectureTests/ArchitectureTests/{SuppliersLayerTests.cs, CompositionRootTests.cs, ArchitectureTests.csproj}` | Crear / Modificar | Capas, 63 permisos, roles |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/{TenantModuleKeysTests.cs, OperatorReadHandlerTests.cs}`, `…IntegrationTests/{TenantModulesApiTests.cs, OperatorConsoleApiTests.cs, SeedStartupTests.cs}` | Modificar | Listas literales de claves (B6) |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/SupplierExportEmailTemplateTests.cs`, `…IntegrationTests/{DeliveryWorkersCharacterizationTests.cs, DeliveryWorkersHarnessTests.cs}` | Crear / Modificar | Worker y plantilla |

**Comandos de prueba:**

```powershell
dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

---

### Task 1 (SUP-01): Proyectos del módulo y dominio `Supplier`

**Files:**
- Create: los cuatro csproj (copiar de `src/Modules/Companies/Modules.Companies.*/*.csproj`, cambiando nombres;
  Application con `InternalsVisibleTo Modules.Suppliers.UnitTests`; Infrastructure suma `libphonenumber-csharp` y
  `ClosedXML` como `Modules.Customers.Infrastructure.csproj:18-19`), `Backend.slnx` (carpeta `/src/Modules/Suppliers/`
  junto a `:35-39`; tests junto a `:150-152`), `Bootstrapper.csproj` (Application + Infrastructure, junto a `:16-17`),
  `Api.csproj` (Api + Infrastructure, junto a `:17-18`), `ArchitectureTests.csproj` (los cuatro, junto a `:30`, `:35-37`)
- Create: todos los archivos de `Modules.Suppliers.Domain` del mapa
- Create: `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/Modules.Suppliers.UnitTests.csproj` (copia de
  `Modules.Companies.UnitTests.csproj`, refs `:13-15` a Suppliers), `SupplierTests.cs`, `SupplierBankAccountTests.cs`,
  `SupplierContactTests.cs`
- Create: `tests/ArchitectureTests/ArchitectureTests/SuppliersLayerTests.cs` (copia de `CompaniesLayerTests.cs` con
  anclas `Supplier`, `ListSuppliersQuery` —llega en Task 4; hasta entonces ancla `SuppliersPermissions`—,
  `SuppliersInfrastructureExtensions`, `SupplierEndpoints`; las dos últimas se crean vacías en esta tarea para que el
  proyecto compile y la prueba de capas corra desde el principio)

**Interfaces:**
- Produces (Domain, namespace `Modules.Suppliers.Domain`):
  - `readonly record struct SupplierId(Guid Value)` como `CompanyId`.
  - `enum IdentificationType { Nit, Cc, Ce, Pasaporte }` + `static class IdentificationTypeParser { Parse(string?) ; ToWire(IdentificationType) ; SupportedWireValues }` copiado de `Modules.Customers.Domain/IdentificationType.cs` con código `suppliers.supplier.identification_type_invalid`.
  - `sealed record SupplierIdentification(IdentificationType Type, string Number)` con `Create(IdentificationType, string?)`, `NumberMaxLength = 32`, códigos `identification_number_required/_too_long`.
  - `SupplierContactInfo` (copia de `CustomerContactInfo`): `Phone`, `Email`, `Address` **opcionales**, `Country` obligatorio alpha-2; códigos `phone_too_long`, `email_invalid`, `address_too_long`, `country_invalid`.
  - `sealed record SupplierBankAccount { BankName; AccountNumber; Currency }` copia de `CompanyBankAccount` (120/32/3, `MaxPerSupplier = 20`, `DeduplicationKey()`), códigos `suppliers.supplier.bank_name_required/_too_long`, `account_number_required/_too_long`, `currency_invalid`.
  - `sealed record SupplierContact { Name; Role; Phone; Email }` con `Create(name, role, phone, email)`: `Name` 120 obligatorio, `Role` 80, `Phone` 32, `Email` 254 (misma plausibilidad que el correo del proveedor); `MaxPerSupplier = 10`; códigos `contact_name_required`, `contact_name_too_long`, `contact_role_too_long`, `contact_phone_too_long`, `contact_email_invalid`.
  - `interface ISupplierPhoneNormalizer { string? ToE164(string? phone, string? country); }` (B1).
  - `sealed class Supplier`: propiedades de spec §5 (`Id, TenantId, Name, BusinessName, Identification, Contact, CityId, CityName, PhoneE164, PaymentTermDays, Category, Notes, BankAccounts, Contacts, IsActive, Version, CreatedAt, UpdatedAt`); `const ColombiaCountryCode = "CO"`, `NameMaxLength = 160`, `BusinessNameMaxLength = 160`, `CityNameMaxLength = 120`, `CategoryMaxLength = 80`, `NotesMaxLength = 2000`, `PaymentTermMaxDays = 365`.
    - `static Supplier Create(Guid tenantId, SupplierWriteData data, ISupplierPhoneNormalizer phones, DateTimeOffset now)` y `void Update(SupplierWriteData data, ISupplierPhoneNormalizer phones, DateTimeOffset now)`, donde `sealed record SupplierWriteData(string? Name, string? BusinessName, string? IdentificationType, string? IdentificationNumber, string? Country, Guid? CityId, string? CityName, string? Phone, string? Email, string? Address, int PaymentTermDays, string? Category, string? Notes, IReadOnlyList<SupplierBankAccount> BankAccounts, IReadOnlyList<SupplierContact> Contacts)` vive en Domain. Normaliza **todo** antes de asignar nada (como `Customer.Update`, `Customer.cs:689-725`).
    - `void Deactivate(DateTimeOffset now)` / `void Activate(DateTimeOffset now)` con `already_inactive` / `already_active`.
    - Reglas de ubicación como `Customer.EnsureValidLocation` (`Customer.cs:949-969`): `city_required`, `city_name_required`.
    - Colecciones: `bank_accounts_too_many`, `bank_account_duplicated`, `contacts_too_many`. `payment_term_invalid` fuera de 0..365.
  - `SuppliersDomainException(string code, string message) : DomainException`.

- [ ] **Step 1: Crea los proyectos, referencias y entradas de la solución; `dotnet build src/Api` tiene que compilar con el módulo vacío.**

- [ ] **Step 2: Escribe las pruebas de dominio que fallan** (xUnit, `[Fact]`/`[Theory]`), una por regla de spec §5. Nombres y aserciones:

```csharp
// SupplierTests.cs
CreateTrimsNamesUppercasesCountryAndLowercasesEmail            // "  Ferretería " → "Ferretería"; "co" → "CO"; "A@B.CO" → "a@b.co"
CreateWithoutPhoneEmailOrAddressSucceeds                        // D-4: los tres null → Supplier con Contact.Phone null y PhoneE164 null
CreateInColombiaWithoutCityIdThrowsCityRequired                 // code == "suppliers.supplier.city_required"
CreateOutsideColombiaWithoutCityNameThrowsCityNameRequired      // "suppliers.supplier.city_name_required"
CreateInColombiaClearsCityNameAndOutsideClearsCityId            // CO + cityName → CityName null; US + cityId → CityId null
CreateWithPaymentTermOutOfRangeThrows                           // -1 y 366 → "suppliers.supplier.payment_term_invalid"; 0 y 365 pasan
CreateWithTwentyOneBankAccountsThrowsTooMany                    // "suppliers.supplier.bank_accounts_too_many"
CreateWithDuplicatedBankAccountThrows                           // misma clave con banco en otra caja → "suppliers.supplier.bank_account_duplicated"
CreateWithElevenContactsThrowsTooMany                           // "suppliers.supplier.contacts_too_many"
CreateWithZeroBankAccountsAndZeroContactsSucceeds               // D-8, D-9
UpdateThatFailsLeavesTheAggregateUntouched                      // Update con nombre vacío tira "name_required" y Name/UpdatedAt no cambian
UpdateRecomputesPhoneE164WithTheNewCountry                      // normalizador falso devuelve "+57…" para CO y null para "ZZ"
DeactivateTwiceThrowsAlreadyInactive; ActivateAnActiveSupplierThrowsAlreadyActive
IdentificationTypeParserAcceptsWireValuesIgnoringCase          // "nit","CC","Ce","PASAPORTE" → enum; "RUT" → "suppliers.supplier.identification_type_invalid"
IdentificationNumberKeepsDotsAndDashesAndRejectsOver32         // "900.123.456-7" se conserva; 33 chars → "identification_number_too_long"
// SupplierBankAccountTests.cs: copia de CompanyBankAccountTests con los códigos suppliers.*
// SupplierContactTests.cs: nombre obligatorio (contact_name_required), 121 chars (contact_name_too_long), correo inválido (contact_email_invalid), role/phone null válidos
```

- [ ] **Step 3: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests --filter "FullyQualifiedName~SupplierTests"`
Expected: FAIL de compilación (tipos inexistentes).

- [ ] **Step 4: Implementa el dominio** con las firmas de arriba. Copia `Customer.cs` para la identidad y ubicación,
  `CompanyBankAccount.cs` para las cuentas; `SupplierContact` sigue la forma de `SupplierBankAccount`.

- [ ] **Step 5: Corre y verifica que pasan**, más `SuppliersLayerTests`:

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~SuppliersLayerTests"`
Expected: PASS (5 pruebas).

- [ ] **Step 6: Commit**

```bash
git add Backend.slnx src/Modules/Suppliers src/Bootstrapper/Bootstrapper.csproj src/Api/Api.csproj tests/Modules/Suppliers tests/ArchitectureTests
git commit -m "feat(suppliers): SUP-01 proyectos del módulo y agregado Supplier"
```

---

### Task 2 (SUP-02): Clave de módulo `suppliers` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (`:57-62`: campo `Suppliers` después de
  `Automations` con `<summary>` «Spec 2026-10-23, D-12: directorio de proveedores. Último de la lista, sin
  dependencias y fuera de `DefaultForNewTenants`: se prende por tenant»; `All` suma `Suppliers` al final; `Dependencies`
  y `DefaultForNewTenants` **no cambian**)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:112-114` (suma
  `'suppliers'` al CHECK)
- Create (ef): `…/Persistence/Migrations/*_AddSuppliersModuleKey.cs` — misma forma que
  `20261021233952_AddAutomationsModuleKey.cs`: `Up` recrea `CK_tenant_modules_module_key` con la lista completa;
  `Down` hace `DELETE FROM tenancy.tenant_modules WHERE module_key = 'suppliers';` y recrea el CHECK sin la clave
- Modify: `README.md:649-651` (lista de módulos y dependencias: `suppliers` sin dependencias, fuera del default)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs` (`:13` lista; `:66` B2),
  `OperatorReadHandlerTests.cs:68`, `…IntegrationTests/TenantModulesApiTests.cs:105,107,113`,
  `OperatorConsoleApiTests.cs:347`, `SeedStartupTests.cs:144,177,239,266,346` (listas alfabéticas: `suppliers` va
  después de `shopify`)

**Interfaces:**
- Produces: `TenantModuleKeys.Suppliers` (`"suppliers"`), `All.Count == 12`, `DependenciesOf(Suppliers) == []`,
  `DefaultForNewTenants` sin cambios (7).

- [ ] **Step 1: Escribe las pruebas que fallan**

En `TenantModuleKeysTests.cs`: `AllKeepsTheContractOrder` (`:13`) suma `"suppliers"` al final;
`AutomationsIsLastAndDependsOnMessaging` (`:66`) pasa a `AutomationsDependsOnMessaging` sin la aserción de `All[^1]`;
nueva `SuppliersIsLastWithoutDependenciesAndOutsideTheDefaults`:
```csharp
Assert.Equal(TenantModuleKeys.Suppliers, TenantModuleKeys.All[^1]);
Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Suppliers));
Assert.DoesNotContain(TenantModuleKeys.Suppliers, TenantModuleKeys.DefaultForNewTenants);
```
Las listas literales de B6 suman `"suppliers"`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests"`
Expected: FAIL de compilación (`TenantModuleKeys.Suppliers`).

- [ ] **Step 3: Implementa** el campo, `All`, el CHECK del DbContext y genera la migración:
`dotnet ef migrations add AddSuppliersModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`.
Edita `Up`/`Down` a la forma de la de Automations (el scaffold sólo cambia el snapshot). Actualiza el README.

- [ ] **Step 4: Corre y verifica que pasan**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"`
Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests"`
Expected: PASS. El sembrado (`TenancySeeder.cs:193`) ya itera `All`, así que el tenant semilla queda con `suppliers`.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Tenancy README.md tests/Modules/Tenancy
git commit -m "feat(tenancy): SUP-02 clave de módulo suppliers"
```

---

### Task 3 (SUP-03): Permisos `suppliers.supplier.read` y `.manage`

**Files:**
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Application/SuppliersPermissions.cs` (creado vacío en Task 1)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: admin (después de `:1120`), billing (dentro de
  `:1254-1280`), definiciones (después de `:1409`), políticas (después de `:1911`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (`:184` → 63; comentarios `:172-183`;
  prueba nueva después de `:436`)

**Interfaces:**
- Produces: `SuppliersPermissions.Read = "suppliers.supplier.read"`, `SuppliersPermissions.Manage = "suppliers.supplier.manage"`.
- `PermissionDefinition(SuppliersPermissions.Read, "Ver proveedores", "Permite ver el directorio de proveedores y exportarlo.", "Suppliers", "low", RequiredModules: [ModuleKeys.Suppliers])` y
  `PermissionDefinition(SuppliersPermissions.Manage, "Gestionar proveedores", "Permite crear, editar, activar y desactivar proveedores.", "Suppliers", "medium", RequiredModules: [ModuleKeys.Suppliers])`.
- `admin` y `billing` suman los dos (D-14, B3); `advisor` y `cashier` no. Comentario en `billing`: «Spec 2026-10-23 D-14:
  facturación paga a los proveedores, así que ve y gestiona el directorio». Sin migración: los roles de sistema viven en código.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public void TheSuppliersPermissionsRequireTheSuppliersModuleAndGoToAdminAndBilling()
// Para Read y Manage: RequiredModules == [ModuleKeys.Suppliers]; Category == "Suppliers";
// catalog.PermissionsFor("admin") y ("billing") los contienen; ("advisor") y ("cashier") no.
```
`PermissionDiscoveryFindsTheSixtyOneConstants` pasa a `…TheSixtyThreeConstants` con `Assert.Equal(63, …)` y una línea
más en el comentario («+2 suppliers, spec 2026-10-23»).

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL de compilación (`SuppliersPermissions.Read`).

- [ ] **Step 3: Implementa** las constantes, las dos definiciones, las dos políticas (`AddPermissionRequirement`) y los dos roles.

- [ ] **Step 4: Corre y verifica que pasan** (`CompositionRootTests` completo: también `EveryPermissionConstantDeclaresItsModules` y `EveryRegisteredPermissionStringIsUnique`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Suppliers/Modules.Suppliers.Application/SuppliersPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests
git commit -m "feat(suppliers): SUP-03 permisos suppliers.supplier.read y manage"
```

---

### Task 4 (SUP-04): Application — contrato, validación, puertos y handlers

**Files:**
- Create: todos los archivos de `Modules.Suppliers.Application` del mapa menos los tres de exportación
  (`ExportSuppliers.cs`, `ISupplierExport*.cs` son de Task 7)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: handlers después de `:247` (comentario «Suppliers,
  spec 2026-10-23»), `AddValidatorsFromAssemblyContaining<CreateSupplierValidator>()` junto a `:787`
- Test: `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/SupplierWriteRulesTests.cs`, `SupplierStatusFilterTests.cs`;
  `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` (`:37`) ya existe y debe seguir en verde

**Interfaces:**
- Consumes: Domain de Task 1.
- Produces:
  - `SuppliersDtos.cs` (spec §6.2): `SupplierCityDto(Guid Id, string DivipolaCode, string Name)`, `SupplierDepartmentDto(…)`,
    `SupplierBankAccountPayload(string BankName, string AccountNumber, string Currency)`,
    `SupplierContactPayload(string Name, string? Role, string? Phone, string? Email)`,
    `SupplierResponse(Guid Id, string Name, string? BusinessName, string IdentificationType, string IdentificationNumber, string? Phone, string? Email, string? Address, string Country, SupplierCityDto? City, SupplierDepartmentDto? Department, string? CityName, int PaymentTermDays, string? Category, string? Notes, IReadOnlyList<SupplierBankAccountPayload> BankAccounts, IReadOnlyList<SupplierContactPayload> Contacts, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`,
    `SupplierListBankAccount(string BankName, string AccountNumber)`,
    `SupplierListItemResponse(Guid Id, string Name, string? BusinessName, string IdentificationType, string IdentificationNumber, string? Phone, string? City, string? Category, int PaymentTermDays, IReadOnlyList<SupplierListBankAccount> BankAccounts, bool IsActive)` (`City` = nombre DIVIPOLA o `CityName`),
    `SuppliersResponse(IReadOnlyCollection<SupplierListItemResponse> Items, int Total, int Page, int PageSize)`,
    `SupplierCategoriesResponse(IReadOnlyList<string> Items)`,
    `SupplierWriteRequest(string Name, string? BusinessName, string IdentificationType, string IdentificationNumber, string Country, Guid? CityId, string? CityName, string? Phone, string? Email, string? Address, int PaymentTermDays, string? Category, string? Notes, IReadOnlyList<SupplierBankAccountPayload> BankAccounts, IReadOnlyList<SupplierContactPayload> Contacts)` — **un solo record** para POST y PUT (el spec dice `Create… = Update…`), con `<summary>` que lo justifica.
  - `interface ISupplierWriteCommand` con esas mismas propiedades **planas**; `CreateSupplierCommand(Guid TenantId, string Name, …, IReadOnlyList<SupplierContactPayload> Contacts) : ICommand<SupplierResponse>, ISupplierWriteCommand` y `UpdateSupplierCommand(Guid TenantId, Guid SupplierId, string Name, …) : …`, cada uno con `static From(Guid tenantId, [Guid supplierId,] SupplierWriteRequest request)`. Planos y no con un `Body` anidado para que las claves de `errors` sean `Name`, `BankAccounts[2].Currency`, como en Clientes (`CreateCustomerCommand`), y no `Body.Name`. `SupplierWriteRules : AbstractValidator<ISupplierWriteCommand>` con las reglas de `CustomerWriteRules` (`Modules.Customers.Application/CustomerWriteRules.cs`) adaptadas: `Phone`/`Address` opcionales con tope; `PaymentTermDays` `InclusiveBetween(0, 365)`; `Category` ≤ 80; `Notes` ≤ 2000; `BankAccounts` ≤ 20 con `RuleForEach` (120/32/3 y `Currencies.IsSupported` como `CompanyWriteRules.cs:81-117`) y la clave de deduplicación anclada a `BankAccounts[i].AccountNumber`; `Contacts` ≤ 10 con `RuleForEach` (Name obligatorio ≤120, Role ≤80, Phone ≤32, Email `EmailAddress()` ≤254). `CreateSupplierValidator`/`UpdateSupplierValidator` la incluyen con `Include()`.
  - `GetSupplierQuery(Guid TenantId, Guid SupplierId) : IQuery<SupplierResponse>`.
  - `ListSuppliersQuery(Guid TenantId, string? Search, string? Status, string? Category, int Page, int PageSize) : IQuery<SupplierPage>`; `SupplierPage(IReadOnlyList<SupplierListItemResponse> Items, int Total, int Page, int PageSize)`; `SupplierPaging` copia de `CustomerPaging` (`ListCustomers.cs:51-75`: default 50, max 200).
  - `ListSupplierCategoriesQuery(Guid TenantId) : IQuery<IReadOnlyList<string>>`.
  - `ActivateSupplierCommand(Guid TenantId, Guid SupplierId) : ICommand<SupplierResponse>`, `DeactivateSupplierCommand(…)`.
  - `SupplierStatusFilter { Active, Inactive }` + `SupplierStatusFilterParser.Parse(string?)` copia de `CompanyStatusFilter.cs` con código `suppliers.supplier.status_filter_invalid`.
  - `SuppliersAuthorization.EnsureAuthorized(IExecutionContext, Guid tenantId, string permission)` copia de `CompaniesAuthorization` (403 `authorization.denied`).
  - `SupplierNotFound` → `ResourceNotFoundException("suppliers.supplier.not_found", …)`.
  - `SupplierAuditActions`: `Created = "suppliers.supplier.created"`, `Updated`, `Activated`, `Deactivated`, `Exported`.
  - Puertos: `ISupplierRepository { GetAsync(tenantId, SupplierId, ct); Add(Supplier); Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int page, int pageSize, ct); Task<IReadOnlyList<string>> ListCategoriesAsync(Guid tenantId, int take, ct); Task<IReadOnlyList<Supplier>> ListForExportAsync(Guid tenantId, string? search, SupplierStatusFilter? status, string? category, int skip, int take, ct) }`;
    `ISuppliersUnitOfWork { Task SaveChangesAsync(ct) }`;
    `ISuppliersAuditPublisher.Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)`;
    `ISupplierGeographyLookup { Task<SupplierCityRef?> FindCityAsync(Guid cityId, ct); Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, ct) }` con `SupplierCityRef(Guid CityId, string CityDivipolaCode, string CityName, Guid DepartmentId, string DepartmentDivipolaCode, string DepartmentName)` (copia de `ICompanyGeographyLookup.cs`).
  - Handlers: `CreateSupplierHandler` autoriza con `Manage`, resuelve la ciudad **sólo si `Country == "CO"`** (`CreateCustomer.cs:67-76`) → 422 `suppliers.supplier.city_not_found`, `Supplier.Create`, `Add`, audita `Created`, `SaveChangesAsync`, responde con `SupplierMapping.ToResponse(supplier, cityRef)`. `UpdateSupplierHandler` igual con `Update` y `Updated`. `Get`/`List`/`ListCategories` autorizan con `Read`; la lista resuelve ciudades en lote con `FindCitiesAsync` (sin N+1) y normaliza la paginación con `SupplierPaging`. `Activate`/`Deactivate` autorizan con `Manage` y auditan.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// SupplierWriteRulesTests.cs (copia la forma de CustomerWriteRulesTests / CompanyWriteRulesTests)
AValidColombianCommandPasses                                   // cityId, sin phone/email/address
AValidForeignCommandPasses                                     // country "US", cityName, cityId null
MissingCityIdInColombiaFailsOnCityId                           // error en "CityId" (mira CustomerWriteRulesTests para la forma)
MissingCityNameOutsideColombiaFailsOnCityName
PaymentTermOutsideZeroTo365Fails                               // -1 y 366
TwentyOneBankAccountsFail; DuplicatedBankAccountFailsOnTheRowAccountNumber  // "BankAccounts[1].AccountNumber"
UnsupportedCurrencyFailsOnTheRowCurrency                       // "XXX" → "BankAccounts[0].Currency"
ElevenContactsFail; ContactWithoutNameFailsOnTheRowName        // "Contacts[0].Name"
ContactWithInvalidEmailFailsOnTheRowEmail
// SupplierStatusFilterTests.cs
ParseAcceptsActiveAndInactiveIgnoringCaseAndBlankIsNull; ParseRejectsAnythingElseWithStatusFilterInvalid
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests --filter "FullyQualifiedName~SupplierWriteRulesTests|FullyQualifiedName~SupplierStatusFilterTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** Application completo y registra los siete handlers y el validador en el Bootstrapper.

- [ ] **Step 4: Corre y verifica que pasan**, más:

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered|FullyQualifiedName~SuppliersLayerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Suppliers/Modules.Suppliers.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Suppliers
git commit -m "feat(suppliers): SUP-04 contrato, validación y handlers del directorio"
```

---

### Task 5 (SUP-05): Infrastructure — DbContext, migración, repositorio, outbox, teléfono y adaptadores

**Files:**
- Create: `Modules.Suppliers.Infrastructure/{SuppliersInfrastructureExtensions.cs (reemplaza el vacío), SuppliersDatabaseInitializer.cs, Persistence/SuppliersDbContext.cs, Persistence/SuppliersDbContextFactory.cs, Persistence/SuppliersOutboxMessage.cs, Persistence/SupplierRepository.cs, Persistence/SuppliersUnitOfWork.cs, Persistence/SuppliersAuditPublisher.cs, Phones/LibPhoneNumberSupplierNormalizer.cs, Persistence/Migrations/*_InitialSuppliers.cs}`
- Create: `src/Bootstrapper/SupplierGeographyLookup.cs` (copia de `CompanyGeographyLookup.cs`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: `services.AddSuppliersInfrastructure(configuration);` junto a `:799`; `services.AddScoped<ISupplierGeographyLookup, SupplierGeographyLookup>();` junto a `:980`
- Modify: `src/Api/Program.cs`: `await app.InitializeSuppliersDatabaseAsync();` después de `:235` (después de Geography)
- Test: la persistencia se prueba en Task 6 por la API. Esta tarea verifica con `SuppliersLayerTests` y una migración limpia.

**Interfaces:**
- Consumes: puertos de Task 4; `ISupplierPhoneNormalizer` de Task 1.
- Produces:
  - `SuppliersDbContext`: `ToTable("suppliers", "suppliers")`; columnas snake_case como `CompaniesDbContext.ConfigureCompany` (`:19-75`); `Version` como token de concurrencia (`:54-56`); índice único `IX_suppliers_tenant_identification` sobre `(tenant_id, identification_type, identification_number)` **sin filtro parcial** (D-5); índices GIN trigram sobre `name`, `business_name`, `identification_number` (`HasMethod("gin").HasOperators("gin_trgm_ops")`, `CompaniesDbContext.cs:64-67`); índice sobre `(tenant_id, category)`; `OwnsMany` `suppliers.supplier_bank_accounts` como `:89-122` y `suppliers.supplier_contacts` con la misma técnica (shadow `ordinal`); FK manual a `geography.cities` con `Restrict` como `20260831004523_AddCompanyCity.cs:52-60`; outbox `platform.outbox_messages` excluido de migraciones (`:124-137`). La migración incluye `CREATE EXTENSION IF NOT EXISTS pg_trgm` (`20260829102842_AddCompaniesTrigramIndexes.cs:17`).
  - `SupplierRepository`: `SearchAsync` con `FilteredQuery` (`CustomerRepository.cs:32-79`: `EF.Functions.ILike` con escape `\`), `search` en OR sobre `Name`, `BusinessName`, `Identification.Number`; `status` → `IsActive`; `category` → `EF.Functions.ILike(Category, categoría escapada)` sin comodines (igualdad sin mayúsculas); `CountAsync` antes de paginar; orden `Name`, `Id`. `ListCategoriesAsync`: `Where(Category != null).Select(Category).Distinct().OrderBy(c => c).Take(take)`. `ListForExportAsync` ordena `Name`, `Id` con `Skip/Take`.
  - `SuppliersUnitOfWork`: `DbUpdateConcurrencyException` → 412 `concurrency.conflict` (`CompaniesUnitOfWork.cs:30-36`); violación de único **por nombre de índice** `IX_suppliers_tenant_identification` → `SuppliersDomainException("suppliers.supplier.identification_taken", …)` (`CustomersUnitOfWork.cs:57-70`); FK `FK_suppliers_cities_city_id` → `suppliers.supplier.city_not_found`.
  - `SuppliersAuditPublisher`: payload de `CompaniesAuditPublisher.cs:22-54` con `resourceType: "supplier"`, `source: "suppliers"`.
  - `LibPhoneNumberSupplierNormalizer : ISupplierPhoneNormalizer`, copia de `LibPhoneNumberNormalizer.ToE164`, región `"ZZ"` sin país; nunca lanza. Registrado `AddSingleton`.
  - `AddSuppliersInfrastructure`: `AddDbContext` con `QepDatabase`, historial `__ef_migrations_history` en esquema `suppliers`; repo, UoW, audit publisher scoped; normalizador singleton.
  - `InitializeSuppliersDatabaseAsync(this WebApplication)` → `MigrateAsync`.

- [ ] **Step 1: Implementa** todo lo anterior y genera la migración:
`dotnet ef migrations add InitialSuppliers --project src/Modules/Suppliers/Modules.Suppliers.Infrastructure --context SuppliersDbContext -o Persistence/Migrations`.
Edita la migración para agregar `pg_trgm` y la FK manual a `geography.cities`.

- [ ] **Step 2: Verifica** que `dotnet build src/Api` compila, `SuppliersLayerTests` pasa
(`ApplicationDoesNotReferencePersistenceLibraries` sigue en verde) y que `dotnet ef migrations list --project … --context SuppliersDbContext` lista `InitialSuppliers` y no marca cambios pendientes
(`dotnet ef migrations has-pending-model-changes --project src/Modules/Suppliers/Modules.Suppliers.Infrastructure --context SuppliersDbContext` → «No changes»).

- [ ] **Step 3: Commit**

```bash
git add src/Modules/Suppliers/Modules.Suppliers.Infrastructure src/Bootstrapper src/Api/Program.cs
git commit -m "feat(suppliers): SUP-05 persistencia, outbox y adaptadores del módulo"
```

---

### Task 6 (SUP-06): Endpoints y pruebas de API

**Files:**
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Api/SupplierEndpoints.cs` (vacío desde Task 1)
- Modify: `src/Api/Program.cs`: `app.MapSupplierEndpoints();` después de `:175`
- Create: `tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests/{Modules.Suppliers.IntegrationTests.csproj (copia de Companies: sólo `src/Api/Api.csproj` + Mvc.Testing, Testcontainers.PostgreSql, xunit.v3), SuppliersApiHarness.cs, SupplierApiTests.cs, SupplierWriteApiTests.cs, SupplierListApiTests.cs, SupplierActivationApiTests.cs}`

**Interfaces:**
- Consumes: handlers de Task 4.
- Produces (spec §6.1), grupo `/api/v1/tenants/{tenantId:guid}/suppliers`, tag `Suppliers`:
  - `GET /` → `SuppliersResponse` (`RequireAuthorization(SuppliersPermissions.Read)`; query `search`, `status`, `category`, `page`, `pageSize`).
  - `GET /categories` → `SupplierCategoriesResponse` (Read).
  - `GET /{supplierId:guid}` → `SupplierResponse` (Read).
  - `POST /` ← `SupplierWriteRequest` → 201 `Created($"/api/v1/tenants/{tenantId}/suppliers/{id}", response)` (Manage).
  - `PUT /{supplierId:guid}` ← `SupplierWriteRequest` → 200 (Manage).
  - `POST /{supplierId:guid}/deactivate`, `POST /{supplierId:guid}/activate` → 200 `SupplierResponse` (Manage).
  - `SuppliersApiHarness` (B4): constantes como `CompaniesApiHarness.cs:20-23`; `SeedTenantAsync(connectionString, tenantId, modules = TenantModuleKeys.All)` copiado de `CustomersApiHarness.cs:56-92`; `CreateClient(factory, subjectId, tenantId, params string[] permissions)` con `X-Subject-Id`, `X-Tenant-Id`, `X-Permissions`; `CreateManager` (Read + Manage), `CreateReader` (Read); `EnsureCityIdAsync` (`CompaniesApiHarness.cs:91-111`); `CreateSupplierAsync(client, tenantId, overrides)` que manda un cuerpo válido colombiano; `ValidationFieldsAsync`; `QepApiFactory(connectionString, Action<IServiceCollection>? configureServices = null)` (`CustomersApiHarness.cs:474-500`).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// SupplierWriteApiTests.cs
CreateReturns201WithLocationAndTheFullDetail                       // Location termina en /suppliers/{id}; city y department resueltos; isActive true; bankAccounts y contacts en orden
CreateWithoutPhoneEmailAndAddressReturns201WithNulls               // D-4
CreateOutsideColombiaKeepsCityNameAndReturnsNullCityAndDepartment
CreateWithUnknownCityIdIs422CityNotFound
CreateWithAnUnknownIdentificationTypeIs422ValidationFailedOnIdentificationType   // errors contiene "IdentificationType"
CreateWithADuplicatedBankAccountIs422OnTheRow                      // errors contiene "BankAccounts[1].AccountNumber"
CreateWithAContactWithoutNameIs422OnTheRow                         // "Contacts[0].Name"
CreateWithTheSameDocumentIs422IdentificationTaken
CreatingWithTheDocumentOfAnInactiveSupplierIs422IdentificationTaken   // Review Focus 1
TwoConcurrentCreatesWithTheSameDocumentLeaveOneSupplier            // Review Focus 2: Task.WhenAll de dos POST; un 201 y un 422; GET / total == 1
UpdateReplacesTheResourceAndClearsOptionalsSentAsNull             // PUT con phone/email/address/category/notes null y listas vacías → todos null y []
UpdatingFromColombiaToAnotherCountryClearsTheCityId               // Review Focus 3 (verifica además en SQL que city_id IS NULL)
UpdateOfAnotherTenantsSupplierIs403AuthorizationDenied
AReaderCannotCreateOrUpdate                                        // 403
// SupplierApiTests.cs
GetReturnsTheDetail; GetUnknownIs404NotFound; GetFromAnotherTenantIs403AuthorizationDenied
WithTheModuleOffEveryEndpointIs403                                 // Review Focus 4: SeedTenantAsync(modules: All sin Suppliers); GET /, GET /{id}, POST / → 403; GET /api/v1/tenants/{id}/modules reporta suppliers enabled false
// SupplierListApiTests.cs
ListIsPagedWithTotalAndOrderedByName                               // 3 proveedores, pageSize 2 → items 2, total 3, page 1; página 2 trae 1
ListFiltersBySearchOverNameBusinessNameAndDocument                 // tres proveedores distintos, cada uno encontrado por uno de los tres campos; con escape de "%"
ListFiltersByStatusAndRejectsAnUnknownValue                        // active / inactive / "paused" → 422 suppliers.supplier.status_filter_invalid
ListFiltersByCategoryIgnoringCase                                  // "ferretería" encuentra "Ferretería"
ListCapsPageSizeAt200AndDefaultsTo50                               // pageSize=999 → pageSize 200 en la respuesta
ListItemCarriesCityNameAndBankAccountsWithoutCurrency              // city "Bogotá, D.C." o cityName; bankAccounts [{bankName, accountNumber}]
CategoriesReturnsDistinctSortedValuesInUse
// SupplierActivationApiTests.cs
DeactivateThenActivateRoundTripsAndAuditsBothActions               // isActive cambia; SELECT en platform.outbox_messages con payload action suppliers.supplier.deactivated y .activated (ver CustomerExportApiTests.cs:268-284 para leer el outbox)
DeactivateTwiceIs422AlreadyInactive; ActivateAnActiveSupplierIs422AlreadyActive
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests --filter "FullyQualifiedName~SupplierWriteApiTests"`
Expected: FAIL (404 en todas las rutas: no hay endpoints).

- [ ] **Step 3: Implementa** `SupplierEndpoints.MapSupplierEndpoints` (forma de `CompanyEndpoints.cs`, lista con query de
`CustomerEndpoints.cs:155-187`) y el `Map` en `Program.cs`.

- [ ] **Step 4: Corre y verifica que pasan** las cuatro clases.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Suppliers/Modules.Suppliers.Api src/Api/Program.cs tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests Backend.slnx
git commit -m "feat(suppliers): SUP-06 endpoints del directorio y pruebas de API"
```

---

### Task 7 (SUP-07): Exportación a Excel por correo

**Files:**
- Create: `Modules.Suppliers.Application/{ISupplierExportBuilder.cs, ISupplierExportStorage.cs, ISupplierExportEventPublisher.cs, ExportSuppliers.cs}`
- Create: `Modules.Suppliers.Infrastructure/{Excel/ClosedXmlSupplierExportBuilder.cs, Persistence/SupplierExportEventPublisher.cs}`
- Create: `src/Bootstrapper/SupplierExportStorage.cs` (copia de `CustomerExportStorage.cs`, misma clave `exports/tenants/{tenantId:N}/{yyyy/MM}/{guid:N}.xlsx`)
- Modify: `SuppliersInfrastructureExtensions.cs` (builder y publicador, como `CustomersInfrastructureExtensions.cs:43,47`), `QepServiceCollectionExtensions.cs` (handler junto a los de Task 4; `AddScoped<ISupplierExportStorage, SupplierExportStorage>()` junto a `:961`), `SupplierEndpoints.cs` (`POST /export`), `SuppliersDtos.cs` (`ExportSuppliersResponse(string FileName, int SupplierCount, DateTimeOffset ExpiresAt)`)
- Test: `tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests/SupplierExportApiTests.cs`

**Interfaces:**
- Consumes: `ISupplierRepository.ListForExportAsync` (Task 4), harness con `configureServices` (Task 6).
- Produces:
  - `ExportSuppliersCommand(Guid TenantId, string? Search, string? Status, string? Category) : ICommand<ExportSuppliersResult>`; `ExportSuppliersResult(string FileName, int SupplierCount, DateTimeOffset ExpiresAt)`.
  - `ExportSuppliersHandler` con el flujo de `ExportCustomers.cs`: autoriza con `Read`; lotes de 500; más de 50 000 → 422 `suppliers.export.too_many_rows`; cero → 422 `suppliers.export.empty`; build → upload → `Publish` del evento → audita `Exported` con `success:{n}` → `SaveChangesAsync`.
  - `ISupplierExportBuilder.Build(IReadOnlyList<SupplierResponse>, TenantCalendar, ct) → SupplierExportFile(byte[] Content, string FileName)`; `ClosedXmlSupplierExportBuilder` con hoja «Proveedores», archivo `proveedores-{yyyyMMdd-HHmmss}.xlsx` y las columnas de spec §6.4 en este orden: `Nombre, Razón social, Tipo de documento, Documento, Teléfono, Correo, Dirección, País, Departamento, Ciudad, Categoría, Plazo (días), Cuentas, Contactos, Estado`; `Cuentas` = una celda con «Banco – Número (Moneda)» por línea (`\n`, celda con `Style.Alignment.WrapText`), `Contactos` = «Nombre, Cargo, Teléfono, Correo» por línea omitiendo los vacíos; `Estado` «Activo»/«Inactivo».
  - `ISupplierExportStorage.UploadAsync(Guid tenantId, string fileName, byte[] content, ct) → SupplierExportUpload(string DownloadUrl, DateTimeOffset ExpiresAt)`.
  - `ISupplierExportEventPublisher.Publish(Guid tenantId, Guid subjectId, string downloadUrl, string fileName, int supplierCount, DateTimeOffset expiresAt, DateTimeOffset occurredAt)`; evento **`suppliers.export-ready.v1`** con payload camelCase `{tenantId, subjectId, downloadUrl, fileName, supplierCount, expiresAt}` al outbox del módulo.
  - `POST /export` → 202 `ExportSuppliersResponse`, `RequireAuthorization(SuppliersPermissions.Read)`, filtros por query.

- [ ] **Step 1: Escribe las pruebas que fallan** (copia la forma de `CustomerExportApiTests.cs`, con `CapturingExportStorage` por `configureServices`, `:249-266`):

```csharp
ExportReturns202WithCountAndPublishesTheReadyEvent                 // 3 proveedores → supplierCount 3; SELECT event_name FROM platform.outbox_messages contiene "suppliers.export-ready.v1"; el storage capturó un .xlsx cuyo nombre empieza por "proveedores-"
ExportAppliesTheListFilters                                        // ?status=active&category=Ferretería → sólo esos
ExportWithNoRowsIs422EmptyAndPublishesNothing                      // Review Focus 5: sin filas en el outbox ni archivo capturado
ExportByAReaderIsAllowedAndWithoutReadIs403
TheWorkbookHasTheFifteenColumnsAndOneRowPerSupplier                // abre el byte[] con ClosedXML y compara la fila de encabezado literal; la celda Cuentas de un proveedor con dos cuentas tiene dos líneas
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests --filter "FullyQualifiedName~SupplierExportApiTests"`
Expected: FAIL (404 en `/export`).

- [ ] **Step 3: Implementa** puertos, handler, builder, publicador, adaptador, registros y endpoint.

- [ ] **Step 4: Corre y verifica que pasan**; también `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` y `SuppliersLayerTests`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Suppliers src/Bootstrapper tests/Modules/Suppliers
git commit -m "feat(suppliers): SUP-07 exportación de proveedores a Excel por correo"
```

---

### Task 8 (SUP-08): Correo de exportación en Notifications

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/SupplierExportEmailTemplate.cs` (copia de `CustomerExportEmailTemplate.cs`)
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/SupplierExportDeliveryWorker.cs` (copia de `CustomerExportDeliveryWorker.cs`)
- Modify: `…Infrastructure/NotificationsInfrastructureExtensions.cs:46-51` (`AddHostedService<SupplierExportDeliveryWorker>()`)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/SupplierExportEmailTemplateTests.cs` (copia de `CustomerExportEmailTemplateTests.cs`), `…IntegrationTests/DeliveryWorkersCharacterizationTests.cs` (`Events` `:15-22`, `PayloadFor` `:91-125`), `…IntegrationTests/DeliveryWorkersHarnessTests.cs:28` (5 → 6)

**Interfaces:**
- Consumes: evento `suppliers.export-ready.v1` con payload `{tenantId, subjectId, downloadUrl, fileName, supplierCount, expiresAt}` (Task 7).
- Produces: `SupplierExportEmailTemplate.TemplateRef = "suppliers.export-ready.v1"`;
  `static EmailMessage Render(string recipient, string downloadUrl, string fileName, int count, DateTimeOffset expiresAt, TimeZoneInfo tz)` con asunto «Tu exportación de proveedores está lista», cuerpo con «{n} proveedores» / «1 proveedor», enlace con la URL codificada en el `href` y cruda en el texto, vencimiento `dd/MM/yyyy HH:mm` en la zona del tenant.
  `SupplierExportDeliveryWorker : OutboxDeliveryWorker` con `Consumer = "notifications.supplier-export-email"`, `EventName = "suppliers.export-ready.v1"`, `TemplateRef = SupplierExportEmailTemplate.TemplateRef`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`SupplierExportEmailTemplateTests`: `RenderPutsTheLinkAndTheExpiryInBothBodies` («42 proveedores», sin «UTC», URL cruda en texto), `RenderEscapesTheLinkInsideTheHtmlHref` (`&amp;X-Amz-Signature=`), `RenderUsesTheSingularForOneSupplier` («1 proveedor»), `RenderShowsTheExpiryOnTheTenantsDay`.
`DeliveryWorkersCharacterizationTests.Events` suma `("suppliers.export-ready.v1", SupplierExportEmailTemplate.TemplateRef)` y `PayloadFor` un brazo con `supplierCount: 3`. `DeliveryWorkersHarnessTests:28` → `Assert.Equal(6, …)`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~SupplierExportEmailTemplateTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** plantilla, worker y registro.

- [ ] **Step 4: Corre y verifica que pasan**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkersCharacterizationTests|FullyQualifiedName~DeliveryWorkersHarnessTests"`
Expected: PASS (`EachWorkerDeliversItsEventOnceToItsRecipient` corre también para el evento nuevo).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Notifications tests/Modules/Notifications
git commit -m "feat(notifications): SUP-08 correo de exportación de proveedores"
```

---

### Task 9 (SUP-09): Documentación y verificación final

**Files:**
- Modify: `README.md` (tabla de endpoints «Proveedores» junto a la de Empresas, con los 8 de spec §6.1 y sus permisos; sección de módulos ya tocada en Task 2), `CLAUDE.md:269` (suma `Suppliers` a los módulos con `LayerTests`)

- [ ] **Step 1: Escribe la documentación.** Además, en el repo hermano `../qep-docs` (fuera de este worktree) agrega la
  fila de spec §2 a `docs/07-governance/open-decisions.md`: `OD-00NN | Product / ERP | ¿Terceros como capability
  transversal? | Diferida | docs/04-products/erp/README.md` con la nota «se evalúa cuando exista el segundo rol fuera de
  ventas que comparte documento con clientes (cuentas por pagar o exógena); hoy `suppliers` es entidad propia (spec
  2026-10-23)». Antes de commitear allá, `git -C ../qep-docs branch --show-current`: si dice `main`, deja el cambio sin
  commit y avísale al owner.

- [ ] **Step 2: Verificación de la rama** (sin suite completa):

```powershell
dotnet format --verify-no-changes --include src/Modules/Suppliers src/Bootstrapper/SupplierGeographyLookup.cs src/Bootstrapper/SupplierExportStorage.cs src/Modules/Notifications src/Modules/Tenancy tests/Modules/Suppliers tests/Modules/Notifications tests/ArchitectureTests
dotnet build src/Api --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests
dotnet test tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkers"
```
Expected: todo PASS, `format` sin cambios. Levanta el backend local y comprueba a mano con el stub (`X-Permissions: suppliers.supplier.read,suppliers.supplier.manage`) que `POST`, `GET /`, `GET /categories` y `POST /export` responden como el contrato de abajo.

- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs(suppliers): SUP-09 endpoints de proveedores y módulo en el README"
```

---

## Contrato para el frontend

Todo bajo `/api/v1/tenants/{tenantId}/suppliers`, con `X-Qep-Client: web` en `POST` y `PUT`. Módulo `suppliers`
(clave nueva en `GET /api/v1/tenants/{id}/modules`, sin dependencias, fuera del default). Permisos
`suppliers.supplier.read` y `suppliers.supplier.manage`, categoría `Suppliers`.

| Ruta | Permiso | Cuerpo / query | Respuesta |
|---|---|---|---|
| `GET /?search&status&category&page&pageSize` | read | `search` ILIKE sobre nombre, razón social y documento; `status` `active`\|`inactive` (ausente = ambos); `category` igualdad sin mayúsculas; `page` ≥ 1 (default 1); `pageSize` default 50, tope 200 | 200 `{ items: [{ id, name, businessName, identificationType, identificationNumber, phone, city, category, paymentTermDays, bankAccounts: [{ bankName, accountNumber }], isActive }], total, page, pageSize }`, orden por nombre |
| `GET /categories` | read | — | 200 `{ items: string[] }` distintas, ordenadas, máx. 200 |
| `GET /{id}` | read | — | 200 `SupplierResponse` (abajo) |
| `POST /` | manage | `SupplierWriteRequest` (abajo) | 201 + `Location`, `SupplierResponse` |
| `PUT /{id}` | manage | `SupplierWriteRequest`; `null` limpia; las listas que llegan son las que quedan | 200 `SupplierResponse` |
| `POST /{id}/deactivate`, `POST /{id}/activate` | manage | — | 200 `SupplierResponse` |
| `POST /export?search&status&category` | read | mismos filtros, sin paginación | 202 `{ fileName, supplierCount, expiresAt }`; el archivo llega por correo |

`SupplierResponse`: `{ id, name, businessName, identificationType: "NIT"|"CC"|"CE"|"PASAPORTE", identificationNumber,
phone, email, address, country, city: { id, divipolaCode, name } | null, department: { id, divipolaCode, name } | null,
cityName, paymentTermDays, category, notes, bankAccounts: [{ bankName, accountNumber, currency }], contacts: [{ name,
role, phone, email }], isActive, createdAt, updatedAt }`. Opcionales en `null`, nunca ausentes.

`SupplierWriteRequest`: `{ name, businessName, identificationType, identificationNumber, country, cityId, cityName,
phone, email, address, paymentTermDays, category, notes, bankAccounts: [...], contacts: [...] }`. Con `country: "CO"` va
`cityId` y `cityName` se ignora; con otro país va `cityName` y `cityId` se ignora. `bankAccounts` y `contacts` siempre
presentes (vacíos valen).

| Código | Status | Dónde | Cuándo |
|---|---|---|---|
| `validation.failed` | 422 | `POST`, `PUT` | `errors` en PascalCase sin prefijo: `Name`, `IdentificationType`, `IdentificationNumber`, `Country`, `CityId`, `CityName`, `Phone`, `Email`, `Address`, `PaymentTermDays`, `Category`, `Notes`, `BankAccounts`, `BankAccounts[2].Currency`, `Contacts`, `Contacts[0].Name`… (el frontend traduce índices como `companies.api.ts:178-209`) |
| `suppliers.supplier.identification_taken` | 422 | `POST`, `PUT` | documento ya registrado en el tenant, activo o inactivo |
| `suppliers.supplier.city_not_found` | 422 | `POST`, `PUT` | `cityId` desconocido |
| `suppliers.supplier.status_filter_invalid` | 422 | `GET /`, `POST /export` | `status` fuera de `active`/`inactive` |
| `suppliers.supplier.already_active` / `already_inactive` | 422 | activar / desactivar | repetido |
| `suppliers.supplier.not_found` | 404 | `GET /{id}`, `PUT`, activar, desactivar | id inexistente en el tenant |
| `suppliers.export.empty` / `suppliers.export.too_many_rows` | 422 | `POST /export` | sin filas / más de 50 000 |
| `concurrency.conflict` | 412 | `PUT`, activar, desactivar | versión vieja |
| `authorization.denied` | 403 | todo | sin permiso, módulo apagado (enmascarado) u otro tenant |

Despliegue: backend primero. Un frontend viejo ignora la clave `suppliers` de `/modules`.
