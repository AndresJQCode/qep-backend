# Distribuidor: país y tipo de documento — plan backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wholesale guarda `Country` e `IdentificationType` del distribuidor, los valida al invitar, los usa al crear su cliente y publica `defaultCountry` en las opciones.

**Architecture:** Dos columnas nuevas en `wholesale.distributors` con backfill `CO`/`NIT`. La validación del tipo contra el país reusa `DocumentTypesByCountry.IsAllowed` (BuildingBlocks). El default sale de `ITenantDirectory.GetRegionalSettingsAsync`, ya usado por `DistributorWhatsAppNotice`.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-18-distribuidor-pais-documento-design.md`

## Global Constraints

- Rama propia `feature/distribuidor-pais-documento` desde `develop` (worktree `qep-backend-worktrees/dist-pais`); nunca `main`.
- Los siete códigos son los de `DocumentTypesByCountry.All`: `NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID`.
- País vacío o un tipo fuera del país es 422 de validación, con claves `Country` e `IdentificationType`. Un país vacío no cae en default.
- No se toca `Eligible` ni el módulo Suppliers.
- El código de error `wholesale.distributor.identification_taken` no cambia.
- TDD: RED con evidencia literal antes de GREEN. El RED de compilación no levanta contenedores.
- Integración filtrada por nombre (`--filter "FullyQualifiedName~Nombre"`), nunca dos corridas a la vez (`Get-Process testhost, dotnet -ErrorAction SilentlyContinue` antes de lanzar). Suite de Wholesale completa una sola vez antes de mergear.
- Commits convencionales, sin atribución de IA. `docs/estado.md` se actualiza en `develop` al mergear.
- Hay cambios ajenos sin commitear en el checkout principal (`ClaudeModelClient.cs`, `merge.md`): no se tocan.

## Review Focus

- Mismo número con otro tipo no es duplicado (CC `123` y NIT `123`).
- Distribuidores previos a la migración leen `CO`/`NIT` y siguen siendo únicos.
- Tipo válido en otro país (NIF con `CO`) → 422 en `IdentificationType`, no 500.
- `country` en minúscula o con espacios (`" co "`) se normaliza a `CO` antes de validar y guardar.
- Reintento de aceptación con documento ya tomado reutiliza el cliente del **mismo tipo**.

---

## Bloque 1 — dominio, validación y persistencia

### Task 1: Dominio y validador

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Domain/Distributor.cs` (propiedades + `Invite`)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/InviteDistributor.cs` (`InviteDistributorCommand`)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorWriteRules.cs` (`InviteDistributorValidator`)
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorWriteRulesTests.cs`, `DistributorTests.cs`, `DistributorWorld.cs` (helper de fixtures)

**Interfaces:**
- Produces: `Distributor.Country: string`, `Distributor.IdentificationType: string` (setters privados); `Distributor.Invite(..., string country, string identificationType, string identificationNumber, ...)` — ambos van **antes** de `identificationNumber`; `InviteDistributorCommand(Guid TenantId, string Email, string BusinessName, string Country, string IdentificationType, string IdentificationNumber, string ContactName, Guid PriceListId, Guid AdvisorId, Guid BillingCompanyId, string CorrelationId, string? Phone = null, Guid? WhatsAppConnectionId = null)`.

- [ ] **Step 1: Write the failing tests** en `DistributorWriteRulesTests`: `InviteRequiresACountryAndATypeAllowedForIt` con estas aserciones (usando `Failed(Invite(...))`): `Invite()` por defecto (`CO`, `NIT`) sin errores; `country: "es", identificationType: "nif"` sin errores; `country: ""` → `["Country"]`; `identificationType: ""` → `["IdentificationType"]`; `country: "CO", identificationType: "NIF"` → `["IdentificationType"]`; `country: "FR", identificationType: "TAX_ID"` sin errores; `country: "FR", identificationType: "NIT"` → `["IdentificationType"]`. En `DistributorTests`: `InviteNormalizesCountryAndType` (`" co "`/`"nit"` → `"CO"`/`"NIT"`). Actualizar el helper `Invite(...)` de ambos archivos y `DistributorWorld` con los parámetros nuevos (defaults `"CO"`, `"NIT"`).
- [ ] **Step 2: Run to verify RED** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` falla por compilación (parámetros inexistentes). Pegar la salida del build como evidencia.
- [ ] **Step 3: Implement** — `Distributor.Invite` recorta y pasa a mayúsculas ambos y exige no vacíos (`ArgumentException.ThrowIfNullOrWhiteSpace`); `InviteDistributorValidator` suma `RuleFor(Country)` (no vacío, máximo 2 caracteres tras recortar) y `RuleFor(IdentificationType).Must((command, type) => DocumentTypesByCountry.IsAllowed(type, command.Country))` con mensaje "The identification type is not valid for the country.". `IsAllowed` ya recorta y compara sin mayúsculas. Se agrega `DocumentTypesByCountry` desde `BuildingBlocks.Domain.Identification` (Application ya referencia BuildingBlocks).
- [ ] **Step 4: Run to verify GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~DistributorWriteRulesTests|FullyQualifiedName~DistributorTests"` → todas pasan.
- [ ] **Step 5: Commit** — `feat(wholesale): validate the distributor country and document type`.

### Task 2: Persistencia, unicidad por tipo y migración

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleDbContext.cs:117-153` (columnas e índice)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/DistributorRepository.cs:18` y `Ports.cs:141` (`IDistributorRepository`)
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/InviteDistributor.cs` (llamada a la existencia + `Distributor.Invite` + `WrittenFields`)
- Create: migración `Add…DistributorCountryAndType` en `…Infrastructure/Persistence/Migrations/`
- Test: `DistributorHandlersTests.cs`, `WholesaleDbContextMappingTests.cs`, `…IntegrationTests/DistributorApiTests.cs`, `DistributorFakes.cs`

**Interfaces:**
- Consumes: Task 1 (`InviteDistributorCommand`, `Distributor.Invite`).
- Produces: `IDistributorRepository.IdentificationExistsAsync(Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken): Task<bool>`.

- [ ] **Step 1: Write the failing tests** — `DistributorHandlersTests`: `InviteAllowsTheSameNumberWithAnotherType` (existente CC `123`, invitar NIT `123` → ok) y `InviteRejectsTheSameTypeAndNumber` (→ `identification_taken`); el fake de `IDistributorRepository` en `DistributorFakes` compara por tipo. `WholesaleDbContextMappingTests`: las dos columnas son requeridas con máximo 2 y 16, y el índice único es `(TenantId, IdentificationType, IdentificationNumber)`. `DistributorApiTests`: `InviteStoresCountryAndTypeAndReturnsThem` (integración, la respuesta lleva `country` e `identificationType`).
- [ ] **Step 2: Run to verify RED** — build de unitarias falla por la firma de `IdentificationExistsAsync` (evidencia: salida del build). La integración no se corre en RED.
- [ ] **Step 3: Implement** — propiedades `Country` (`HasMaxLength(2)`) e `IdentificationType` (`HasMaxLength(16)`) requeridas; índice `HasIndex(TenantId, IdentificationType, IdentificationNumber).IsUnique()` reemplaza al de dos columnas; `IdentificationExistsAsync` filtra además por tipo; `InviteDistributorHandler` pasa `command.IdentificationType.Trim()` a la existencia (en mayúsculas) y `command.Country`, `command.IdentificationType` a `Distributor.Invite`; `WrittenFields` suma `"country"`, `"identificationType"`. Generar la migración con el mismo comando que usó `AddWholesaleInbox` (proyecto de Infraestructura de Wholesale, startup `src/Api`); editar el `AddColumn` para `defaultValue: "CO"` y `"NIT"` (así los existentes quedan rellenos) y el índice nuevo; el `Down` restaura el índice viejo.
- [ ] **Step 4: Run to verify GREEN** — unitarias de Wholesale filtradas a `DistributorHandlersTests|WholesaleDbContextMappingTests`; luego `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~InviteStoresCountryAndTypeAndReturnsThem"`.
- [ ] **Step 5: Commit** — `feat(wholesale): store the distributor country and type with a per-type unique index`.

## Bloque 2 — contrato HTTP, opciones y cliente

### Task 3: DTOs, endpoint y `defaultCountry`

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorDtos.cs` (`DistributorSummaryDto`, `DistributorDetailDto`, `DistributorOptionsDto`)
- Modify: `…Application/DistributorComposer.cs:41,80`, `GetDistributorOptions.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Api/WholesaleEndpoints.cs:261,340` (`DistributorInviteRequest` y el mapeo)
- Test: `DistributorComposerTests.cs`, `DistributorHandlersTests.cs` (opciones), `DistributorApiTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `DistributorSummaryDto` y `DistributorDetailDto` con `string Country, string IdentificationType` justo antes de `IdentificationNumber`; `DistributorOptionsDto.DefaultCountry: string` (último parámetro); `DistributorInviteRequest(string Email, string BusinessName, string Country, string IdentificationType, string IdentificationNumber, string ContactName, Guid PriceListId, Guid AdvisorId, Guid BillingCompanyId, string? Phone = null, Guid? WhatsAppConnectionId = null)`.

- [ ] **Step 1: Write the failing tests** — composer: ambos DTOs devuelven `Country`/`IdentificationType` del distribuidor. Opciones: `OptionsReturnTheTenantCountry` (settings `ES` → `"ES"`) y `OptionsFallBackToCOWithoutRegionalSettings` (settings `null` → `"CO"`). API: `OptionsCarryDefaultCountry`, e `InviteWithAnInvalidTypeForTheCountryAnswers422OnIdentificationType` (`country: "CO", identificationType: "NIF"` → 422 con la clave `IdentificationType` en `errors`).
- [ ] **Step 2: Run to verify RED** — build de unitarias falla por los parámetros nuevos (evidencia: salida del build).
- [ ] **Step 3: Implement** — `GetDistributorOptionsHandler` inyecta `ITenantDirectory` y resuelve `(await tenants.GetRegionalSettingsAsync(new TenantId(query.TenantId), cancellationToken))?.Country ?? "CO"`; reusar la constante de `DistributorWhatsAppNotice` moviéndola a un lugar compartido del módulo (`DistributorDefaults.Country`). El mapeo del endpoint pasa los dos campos nuevos.
- [ ] **Step 4: Run to verify GREEN** — unitarias filtradas a `DistributorComposerTests|DistributorHandlersTests`; integración filtrada a `OptionsCarryDefaultCountry` y `InviteWithAnInvalidTypeForTheCountryAnswers422OnIdentificationType`.
- [ ] **Step 5: Commit** — `feat(wholesale): expose the distributor country, type and default country`.

### Task 4: El cliente del distribuidor nace con su tipo

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/Ports.cs:281` (`IWholesaleCustomerProvisioning.EnsureCustomerAsync`)
- Modify: `…Application/DistributorInvitationService.cs:161`
- Modify: `src/Bootstrapper/WholesaleCustomerProvisioning.cs` (quitar la constante `"NIT"`)
- Test: `DistributorInvitationServiceTests.cs`, `…IntegrationTests/WholesaleAdaptersTests.cs`, `InvitationAcceptanceApiTests.cs`

**Interfaces:**
- Consumes: `Distributor.IdentificationType` (Task 1).
- Produces: `EnsureCustomerAsync(Guid wholesalerTenantId, Guid actorUserId, string businessName, string identificationType, string identificationNumber, string email, CancellationToken cancellationToken): Task<Guid>`.

- [ ] **Step 1: Write the failing tests** — servicio: `AcceptCreatesTheCustomerWithTheDistributorType` (el fake registra el tipo recibido: un distribuidor `ES`/`NIF` lo pasa como `"NIF"`). Adaptador: `EnsureCustomerUsesTheGivenType` (un cliente creado con `CE` queda `CE`) y `EnsureCustomerReusesTheCustomerOfTheSameTypeOnly` (mismo número con otro tipo crea otro cliente).
- [ ] **Step 2: Run to verify RED** — build de unitarias falla por la firma (evidencia: salida del build).
- [ ] **Step 3: Implement** — el servicio pasa `distributor.IdentificationType`; el adaptador usa el parámetro en `RegisterCustomerData` y en `IdentificationTypeParser.Parse(identificationType)` de la rama `IdentificationTaken`. Verificar antes con `rg "IdentificationTypeParser" src/Modules/Customers` que el parser de Clientes acepta los siete códigos; si no acepta alguno, **parar y avisar al owner** (es una decisión pendiente, no se inventa un mapeo).
- [ ] **Step 4: Run to verify GREEN** — unitarias filtradas a `DistributorInvitationServiceTests`; integración filtrada a `EnsureCustomerUsesTheGivenType|EnsureCustomerReusesTheCustomerOfTheSameTypeOnly`.
- [ ] **Step 5: Commit** — `feat(wholesale): create the distributor customer with its own document type`.

## Cierre

- [ ] Suite de `Modules.Wholesale.UnitTests` completa y `ArchitectureTests` (`dotnet test tests/ArchitectureTests`); integración de Wholesale completa **una vez** (`-- xUnit.MaxParallelThreads=4`), tras verificar que no corre otro `testhost`.
- [ ] Revisión de toda la rama una sola vez (modelo más capaz).
- [ ] Merge a `develop` (nunca `main`), y actualizar `docs/estado.md` en `develop`.
- [ ] Avisar al owner: el backend va **primero**; el frontend lee tolerante la ausencia de los campos.
