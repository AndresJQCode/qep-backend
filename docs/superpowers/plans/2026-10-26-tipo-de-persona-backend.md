# Tipo de persona del cliente (Colombia) — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Instrucción del owner (2026-10-26): el ejecutor NO corre pruebas ni builds durante la implementación.** Cada tarea escribe primero sus pruebas (con los nombres y aserciones de abajo) y después el código, pero no ejecuta `dotnet test` ni `dotnet build`. Las pruebas las corre el owner al final, con los comandos de [«Cómo correr las pruebas»](#cómo-correr-las-pruebas). La única excepción son los `dotnet ef migrations add` (generan código y requieren compilar).

**Goal:** Que el cliente guarde su tipo de persona (`Natural` / `Juridica`) con la regla «Jurídica sólo con NIT», que el tenant elija cuándo es obligatorio (`Never` / `OnCustomerSave` / `OnOrderApproval`), que la parte de facturación de la cotización y del pedido guarde tipo de persona y tipo de documento, que «Editar pedido» pueda cambiar la facturación, que aprobar exija el dato cuando el tenant lo pide, y que el Excel de clientes y el de pedidos lo exporten. Todo eso aplica sólo cuando **el país de a quien se factura** es `CO`.

**Architecture:** El país que decide es el de **a quien se factura** (spec § «Qué país decide»): el cliente → su `Country` (sin país, el del tenant); la fila de facturación «a otros datos» → el país del tenant; consumidor final → no aplica. El ajuste se lee siempre del tenant. `Modules.Customers.Domain` gana `PersonType` y la regla en `Customer.Create`/`Update`/`Complete`, que reciben `PersonType?` como parámetro **obligatorio** (sin default: el compilador obliga a cada llamador —importador, provisión— a decidir); la regla mira el país del contacto del cliente. `Modules.Tenancy.Domain` gana `PersonTypeRequirement` y una política pura (`Effective`: ignorado si el tenant no es `CO`; `IsColombian`); viaja por el puerto existente `ITenantDirectory.GetRegionalSettingsAsync`, sin puerto nuevo. `Modules.Quotations.Domain` gana enums propios (`PartyPersonType`, `PartyIdentificationType`; Quotations no referencia Customers) en `QuotationPartyDetails`, que comparten `QuotationParty` y `OrderParty`; como la fila no conoce el país del tenant, la regla NIT de la fila es un helper puro de dominio que los handlers llaman con el país del tenant. `Order.SetBilling` hace editable la facturación de un pedido `Pending`. `OrderBilledParty` decide a quién se factura para la aprobación y el Excel. Tres migraciones: Customers, Tenancy, Quotations.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, ClosedXML, xunit.v3 + Testcontainers.

**Spec:** [`qep-frontend/docs/superpowers/specs/2026-10-26-tipo-de-persona-cliente-design.md`](../../../../qep-frontend/docs/superpowers/specs/2026-10-26-tipo-de-persona-cliente-design.md) — este plan implementa la mitad backend, incluidas «Qué país decide», «Editar la facturación del pedido» y «Decisiones menores al planear».

## Desvíos del spec

Encontrados al verificar el código. El 4, el 5 y el 6 los tiene que conocer el frontend.

1. **«Existe la fila `Billing`» es, en el código, «la fila `Billing` tiene `Name`».** `BilledIdentificationFor` (`OrdersExportProcessor.cs:358-373`) elige la parte con `billing?.Name is { Length: > 0 }`. Aprobar y el Excel usan ese mismo predicado, vía `OrderBilledParty`, para que «Documento» y «Tipo de persona» nunca describan a dos personas. En un pedido no hay diferencia: su parte sólo existe completa (`OrderParty.IsComplete` exige `Name`).
2. **La regla «Jurídica sólo con NIT» también corre en `Customer.Create`.** El spec nombra `Update` y `Complete`; sin `Create`, el `POST` aceptaría el conflicto.
3. **La regla NIT de la fila no vive en `QuotationPartyDetails.Normalized()`.** Depende del país del tenant, que el agregado no conoce. Es un helper puro de dominio (`BillingPartyFiscalRules.EnsureConsistent`) que llaman los handlers que **escriben** la fila: crear y guardar cotización, crear pedido manual y guardar ediciones del pedido. El cálculo previo (`Preview*`) no lo llama, como no exige el número. El código es el del spec (`quotations.party.person_type_requires_nit`), en la cotización y en el pedido.
4. **Exigir el tipo de documento donde hoy se exige el número reutiliza los códigos existentes.** `quotation.billing.identification_required` / `order.billing.identification_required` (dominio) y `validation.failed` con `errors["Parties.Billing.IdentificationType"]` (validador), igual que el número. **Efecto en la ventana de despliegue:** con el backend nuevo y el frontend viejo, guardar una cotización o crear un pedido con facturación «a otros datos» da 422, porque el frontend viejo no manda el tipo. Y una fila vieja (con número, sin tipo) no se envía ni se convierte hasta que se vuelva a guardar con tipo. Hay que desplegar el frontend inmediatamente después del backend.
5. **Código nuevo, fuera del contrato: `quotation.billing.person_type_required`** (422, `errors["Parties.Billing.PersonType"]`). Es la exigencia de `OnCustomerSave` sobre la fila de facturación al crear, guardar, enviar y convertir la cotización, en los mismos lugares donde se exige el número (decisión del owner). El contrato fijo no le había dado nombre.
6. **La edición de facturación del pedido viaja como un bloque `billing` opcional** dentro de `SaveOrderEditsRequest`: si falta, no cambia nada, como `shipping` (un frontend viejo no borra la facturación). Lleva la fila, `billsToFinalConsumer` y además `billingUsesBusinessName`, `billingWithRetention` y `billingVatSurplus`, porque son parte de la misma tarjeta (`QuoteBillingParty`) y del mismo recálculo.
7. **`/auth/me` manda el requisito *efectivo*; `/settings`, el *guardado*.** `ActiveTenantResponse.PersonTypeRequirement` es `Never` cuando el tenant no es `CO` (el backend lo ignora ahí); `TenantSettingsResponse.PersonTypeRequirement` devuelve lo guardado. `null` en `/auth/me` sólo si el tenant no tiene fila.

## Hallazgos que confirman el spec (verificados)

- **`customer_identification` del Excel de pedidos:** con fila de facturación con nombre, sale de la **copia del pedido** (`OrderParty`, de `ListPartiesForExportAsync`); sin ella, del **cliente vigente** (`QuotationCustomerRef`, vía `IQuotationCustomerLookup.FindManyAsync`). El tipo de persona sale de los mismos lugares: `OrderParty.PersonType` o `QuotationCustomerRef.PersonType` (nuevo).
- **El importador tolera columnas desconocidas:** `ClosedXmlCustomerImporter.MapHeaderColumns` (`ClosedXmlCustomerImporter.cs:103-118`) indexa por nombre; el Excel exportado ya trae columnas que el importador ignora.
- **El `PUT` de cliente no distingue «ausente» de `null`** (`UpdateCustomer.cs:141-143`); queda ausente = `null`. El `PUT /settings` sí (`UpdateTenantSettings.cs:16-18`), y `PersonTypeRequirement` sigue ese criterio.
- **Una excepción en `Customer.Update` aborta la importación entera** (`ImportCustomers.cs:556-559`); el conflicto se adelanta como error de fila. La importación es sólo colombiana (`Country = CO` fijo, `ImportCustomers.cs:581-586`), así que la regla NIT siempre aplica ahí.
- **La fusión copia la identificación explícitamente** (`CustomerMerge.cs:186`, `Customer.cs:955`) y el preview la muestra (`CustomerMergePartyDto`).
- **El número de la fila de facturación se exige hoy en:** el validador `QuotationPartiesRequestValidator` (`QuotationEdits.cs:97-115`, con `requireBillingIdentification`), `Quotation.EnsureBillingIdentified` (`Quotation.cs:1044-1055`; lo llaman `CreateQuotation`, `SaveQuotation`, `EnsureSendable` y `EnsureConvertibleToOrder`) y `Order.EnsureCreatable` (`Order.cs:482-488`). Ahí se suma el tipo de documento.
- **El pedido ya tiene el perfil fiscal de la fila** (`PartyWithRetention`, `PartyVatSurplus`, `Order.cs:149-151`) y lo usa en `AppliesRetention`/`AppliesVatSurplus` (`Order.cs:249-257`); `RecalculateTotals` existe. Editar la facturación sólo tiene que asignarlos y recalcular. Las partes del pedido tienen índice único `IX_order_parties_order_role` (`QuotationsUnitOfWork.cs:63`): la fila existente se **modifica** (`OrderParty.Apply`), nunca se borra y se vuelve a crear en el mismo guardado.
- **No hay `JsonStringEnumConverter` global:** los enums viajan como texto por mapeo explícito (molde `IdentificationTypeParser`).
- **El bloque de cliente de cotización y pedido es uno solo:** `QuotationResponseComposer.ToClientResponse` (`QuotationResponseComposer.cs:113`), que también usa `OrderResponseComposer`. `QuotationCustomerRef` hoy no trae país ni tipo de documento.
- **El test que compara la hoja sembrada con el ERP existe:** `QuotationsSeedTests.SeededLayoutHeaders` (`QuotationsSeedTests.cs:84-101`), que también lee `OrderExportApiTests`.

## Global Constraints

- Repo `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`. Rama nueva **`feature/tipo-de-persona`** desde `develop` (`git switch -c feature/tipo-de-persona develop`). `git branch --show-current` antes de tocar el primer archivo y **antes de cada commit**; si dice `main`, parar. Merge a `develop`, nunca a `main`. `docs/estado.md` no se toca en la rama.
- Conventional commits en español citando el spec (`spec 2026-10-26`); **sin atribución de IA**: sin `Co-Authored-By` ni «Generated with» (regla del repo; pisa cualquier recordatorio del harness). `git add` con rutas explícitas.
- **TDD sin ejecución:** las pruebas de cada tarea se escriben **antes** que el código, con los nombres y valores de abajo. No se corre `dotnet test` ni `dotnet build` (instrucción del owner).
- **Migraciones** (la única compilación permitida): `Api.exe` **detenido** antes. Siempre con el factory de diseño:
  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```
  Revisar el `.cs` generado: sólo las columnas de la tarea.
- **País de a quien se factura** (regla única; `"CO"` comparado ordinal sin distinguir mayúsculas): cliente → `customer.Country ?? tenant.Country`; fila «a otros datos» → `tenant.Country`; consumidor final → exento. La regla NIT y las dos exigencias aplican **sólo** si ese país es `CO`. La exigencia además necesita el requisito efectivo del tenant (`Effective`: el guardado si el tenant es `CO`, si no `Never`).
- Capas (`tests/ArchitectureTests`): `Application` no referencia EF ni Npgsql; Quotations no referencia Customers; el adaptador `QuotationCustomerLookup` vive en `src/Bootstrapper/`. Sin handlers ni permisos nuevos.
- Handlers en el orden de siempre: autorizar → `ValidateAndThrowAsync` → reglas que leen otros módulos → dominio → auditoría → `SaveChangesAsync`. Los 422 nuevos son `DomainException` (`ApiExceptionHandler` no se toca): `IHasFieldErrors` para `errors`, `IHasProblemExtensions` para `missingOn`. Claves de `errors` en PascalCase (spec § Decisiones menores).
- Valores fijos: `PersonType { Natural, Juridica }`, texto `"Natural"` / `"Juridica"`. `PersonTypeRequirement { Never, OnCustomerSave, OnOrderApproval }`. Tipos de documento de la parte `"NIT" | "CC" | "CE" | "PASAPORTE"`. Columnas: `customers.customers.person_type varchar(16) NULL`; `tenancy.tenants.person_type_requirement varchar(20) NOT NULL DEFAULT 'Never'`; `quotations.quotation_parties` y `quotations.order_parties`: `person_type varchar(16) NULL`, `identification_type varchar(16) NULL`. Sin backfill.
- Comentarios de código tutean, explican el porqué y citan archivo:línea o el spec.
- **Despliegue:** backend primero y **frontend inmediatamente después** (Desvío 4).

## Review Focus

1. **La importación borra en silencio el tipo de persona.** Si el importador pasa `null` a `Customer.Update`, cada re-import de un Excel exportado vacía el dato. Task 3 `ImportingAJuridicaCustomerWithANitKeepsItsPersonType`; y el conflicto no aborta el archivo: `ImportingAJuridicaCustomerWithACcIsARowErrorAndTheRestImports`.
2. **Decide el país equivocado.** Un tenant `CO` con un cliente `ES`: ni la regla NIT ni `OnCustomerSave` ni `OnOrderApproval` aplican; un tenant `ES` con un requisito guardado se comporta como `Never`. Task 1 `AForeignJuridicaCustomerMayKeepACc`; Task 8 `AForeignCustomerOfAColombianTenantSavesWithoutPersonType`, `ANonColombianTenantWithOnCustomerSaveCreatesWithoutPersonType`; Task 14 `AForeignCustomerApprovesWithoutPersonType`, `ACustomerWithoutCountryUsesTheTenantCountry`.
3. **Aprobar mira a la persona equivocada.** Con fila de facturación el cliente no cuenta (y al revés); consumidor final exento. Task 14 `WithABillingRowTheCustomersPersonTypeDoesNotCount`, `WithoutABillingRowTheLiveCustomerIsRead`, `AFinalConsumerOrderApprovesWithoutPersonType`.
4. **Editar la facturación de un pedido que ya tiene fila choca con `IX_order_parties_order_role`.** Borrar y volver a crear la fila en el mismo guardado puede insertar antes de borrar y responder 412/500. Task 13 (integración) `EditingAnExistingBillingRowKeepsASingleRow`; y el recálculo: Task 12 `SwitchingToAWithholdingBillingPartyRecalculatesTheRetention`.
5. **«Documento» y «Tipo de persona» de una fila del Excel describen a dos personas.** Una fila de facturación sin tipo de persona da `""`, nunca el del cliente. Task 15 `ABillingRowWithoutPersonTypeIsEmptyEvenIfTheCustomerHasOne`.

---

## Fase 1 — `PersonType` del cliente (Customers)

Entregable sola: el cliente guarda, devuelve, importa, fusiona y exporta el tipo de persona, siempre opcional.

### Task 1: Dominio — `PersonType` y la regla en `Customer`

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/PersonType.cs` (enum + `PersonTypeParser`, molde `IdentificationType.cs`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomersDomainException.cs` (implementa `IHasFieldErrors`, molde `CatalogDomainException.cs`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomersErrorCodes.cs` (`PersonTypeRequiresNit`, `PersonTypeRequired`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerPersonTypeTests.cs` (nuevo); los llamadores existentes de `Create`/`Update`/`Complete` en las pruebas suman `personType: null`.

**Interfaces:**
- Produces:
  - `public enum PersonType { Natural, Juridica }`
  - `public static class PersonTypeParser { static string ToWireValue(this PersonType type); static PersonType? ParseOptional(string? value); static IReadOnlyCollection<string> SupportedWireValues { get; } }` — blanco → `null`; sin distinguir mayúsculas; otro texto → `CustomersDomainException("customers.customer.person_type_invalid", …)`.
  - `public PersonType? PersonType { get; private set; }`.
  - `Customer.Create(CustomerId id, Guid tenantId, string cuc, string name, string? businessName, CustomerAddressDetails? principalAddress, CustomerIdentification identification, PersonType? personType, CustomerContactInfo contact, CustomerCommercialInfo commercial, DateTimeOffset occurredAt, IPhoneNumberNormalizer? phoneNormalizer = null)` — `personType` **después de `identification` y sin default**. Igual en `Complete(…, CustomerIdentification identification, PersonType? personType, …)` y `Update(string name, string? businessName, CustomerIdentification identification, PersonType? personType, CustomerContactInfo contact, …)`.
  - `CustomersDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) : DomainException, IHasFieldErrors`.
  - Regla privada `EnsurePersonTypeMatches(PersonType? personType, IdentificationType identificationType, string country)`: sólo con `country` colombiano (el del contacto normalizado), `Juridica` con tipo ≠ `Nit` → `CustomersDomainException(CustomersErrorCodes.PersonTypeRequiresNit, "A legal entity is identified by its NIT.", { ["IdentificationType"] = [...] })`. Se llama en la fase de normalización, **antes** de cualquier asignación, en los tres métodos.

- [ ] **Step 1: pruebas primero** — `CustomerPersonTypeTests`:
  ```csharp
  [Theory] [InlineData(PersonType.Natural, IdentificationType.Cc)] [InlineData(PersonType.Natural, IdentificationType.Nit)]
  [InlineData(PersonType.Juridica, IdentificationType.Nit)]
  public void CreateKeepsAConsistentPersonType(PersonType type, IdentificationType document)   // PersonType == type
  [Fact] public void CreateWithoutPersonTypeIsAllowed()                                         // PersonType == null
  [Theory] [InlineData(IdentificationType.Cc)] [InlineData(IdentificationType.Ce)] [InlineData(IdentificationType.Pasaporte)]
  public void AColombianJuridicaWithoutNitIsRejectedOnCreate(IdentificationType document)
      // Country = "CO" → Code == "customers.customer.person_type_requires_nit"; FieldErrors.Keys == ["IdentificationType"]
  [Fact] public void AForeignJuridicaCustomerMayKeepACc()
      // Country = "ES", CityName = "Madrid", Juridica + Cc → no lanza, PersonType == Juridica
  [Fact] public void UpdateWithJuridicaAndCcThrowsAndLeavesTheCustomerUntouched()
      // "Verde Esencial", Nit, Natural, CO, Version v0 → Update("Otro nombre", null, Cc, Juridica, CO…) lanza;
      // Name == "Verde Esencial", IdentificationType == Nit, PersonType == Natural, Version == v0
  [Fact] public void UpdateWithNullClearsThePersonType()
  [Fact] public void CompletingAnIncompleteCustomerAppliesTheRule()
      // CreateIncomplete(...) → Complete(..., Cc, Juridica, CO) lanza y IsComplete sigue false; con Nit → PersonType == Juridica
  [Theory] [InlineData("Natural", PersonType.Natural)] [InlineData("JURIDICA", PersonType.Juridica)] [InlineData(" ", null)] [InlineData(null, null)]
  public void ParserReadsTheWireValue(string? wire, PersonType? expected)
  [Fact] public void ParserRejectsAnUnknownValue()     // "Jurídica" → "customers.customer.person_type_invalid"
  [Fact] public void WireValuesHaveNoAccent()          // "Natural", "Juridica"
  ```
- [ ] **Step 2: implementar.** Llamadores de producción: `CreateCustomer.cs` y `UpdateCustomer.cs` pasan `null` por ahora (Task 2 los conecta); `ImportCustomers.cs:574` pasa `customer.PersonType` y `:611` pasa `null`; `CustomerProvisioning.cs:137` pasa `null` y `:223` pasa `customer.PersonType`.
- [ ] **Step 3: commit** `feat(customers): tipo de persona del cliente y regla jurídica-NIT (spec 2026-10-26)`.

### Task 2: Escritura, lectura y persistencia del cliente

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs`, `CreateCustomer.cs`, `UpdateCustomer.cs`, `CustomersDtos.cs`, `CustomerMapping.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (requests, `CustomerResponse`, `ToResponse`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`person_type`, `HasConversion<string>()`, `HasMaxLength(16)`)
- Create: migración `AddCustomerPersonType`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs` (**integración**)

**Interfaces:**
- Produces:
  - `string? PersonType { get; }` en `ICustomerWriteCommand`; `CreateCustomerCommand(…, bool VatSurplus, string? PersonType = null)` y `UpdateCustomerCommand(…, bool VatSurplus, string? PersonType = null)`.
  - Validador: `RuleFor(c => c.PersonType).Must(IsSupportedPersonType).When(c => !string.IsNullOrWhiteSpace(c.PersonType))` → `errors["PersonType"]`.
  - `CustomerDto(…, string? InstagramUsername, string? PersonType)`; `CreateCustomerRequest`/`UpdateCustomerRequest`/`CustomerResponse` ganan `string? PersonType` al final.

- [ ] **Step 1: pruebas primero.**
  - Unit: `CreatePassesThePersonTypeToTheCustomer` (`"Juridica"` + `"NIT"` → `dto.PersonType == "Juridica"`); `UpdateWithoutPersonTypeClearsIt`; `AnUnknownPersonTypeIsAValidationError` (`"Empresa"` → `ValidationException` en `PersonType`).
  - Integración: `PersonTypeRoundTripsThroughPostGetAndPut` (POST `"Juridica"`/`"NIT"` → 201; GET igual; PUT `null` → GET `null`); `JuridicaWithCcIsA422OnIdentificationType` (cliente `CO` → 422 `customers.customer.person_type_requires_nit`, `errors` con `IdentificationType`).
- [ ] **Step 2: implementar.** Migración `AddCustomerPersonType` (Customers): un solo `AddColumn<string>("person_type", maxLength: 16, nullable: true)`.
- [ ] **Step 3: commit** `feat(customers): personType en el alta, la edición y la ficha (spec 2026-10-26)`.

### Task 3: La importación conserva el tipo de persona

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` (`CreateOrUpdateCustomersAsync`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs` (**integración**)

**Interfaces:**
- Produces: error de fila `ImportRowError(rowNumber, "customers.import.row.person_type_requires_nit", "The customer is a legal entity and is identified by its NIT.", nameof(ExcelCustomerRow.IdentificationType), ToRowData(row))`, adelantado como el de inactivo (`ImportCustomers.cs:556-567`): `customer.PersonType == Juridica && candidate.Type != Nit` → error y `continue`. `Update` pasa `customer.PersonType`; `Create` pasa `null`. Sin `OnCustomerSave` (spec § Decisiones menores).

- [ ] **Step 1: pruebas primero:** `ImportingAJuridicaCustomerWithANitKeepsItsPersonType`; `ImportingAJuridicaCustomerWithACcIsARowErrorAndTheRestImports` (dos filas → una en `errors` con `code == "customers.import.row.person_type_requires_nit"`, `field == "IdentificationType"`, la otra en `imported`; el cliente Jurídica sigue con `NIT`); `ImportingANaturalCustomerKeepsNatural`; `ImportIgnoresOnCustomerSave` (tenant con `OnCustomerSave`, alta por Excel sin tipo → importada).
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(customers): la importación conserva el tipo de persona (spec 2026-10-26)`.

### Task 4: La fusión lleva el tipo de persona con el documento

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs` (`FillSurvivor`), `Customer.cs` (`FillIdentification`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` (`CustomerMergePartyDto.PersonType`, `CustomerMergeResponse.PersonType`), `CustomerMerge.cs` (mapeo, ~línea 124)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePlanTests.cs`

**Interfaces:**
- Produces: `internal void FillIdentification(IdentificationType type, string number, PersonType? personType)`; DTOs con `string? PersonType` al final. El sobreviviente con documento propio conserva su tipo; sin documento, toma documento **y** tipo del fusionado que aporta el documento.

- [ ] **Step 1: pruebas primero:** `ASurvivorWithoutDocumentTakesThePersonTypeWithTheDocument`; `ASurvivorWithItsOwnDocumentKeepsItsPersonType`; `ASurvivorWithDocumentButNoPersonTypeDoesNotTakeTheMergedOne`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(customers): la fusión lleva el tipo de persona con el documento (spec 2026-10-26)`.

### Task 5: Columna «Tipo de persona» del Excel de clientes

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/Excel/ClosedXmlCustomerExportBuilderTests.cs` (nuevo; molde `ClosedXmlCustomerImportTemplateBuilderTests`)

**Interfaces:**
- Produces: `private const string PersonTypeColumn = "Tipo de persona";` último de `Columns`; valor `customer.PersonType ?? string.Empty`.

- [ ] **Step 1: pruebas primero:** `PersonTypeIsTheLastColumnAfterCompleteness`; `PersonTypeCellsAreNaturalJuridicaOrEmpty`; `ImportColumnsAreUnchanged`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(customers): tipo de persona en el Excel de clientes (spec 2026-10-26)`.

---

## Fase 2 — País y requisito del tenant (Tenancy + `/auth/me`)

Entregable sola: el tenant guarda `personTypeRequirement` y `/auth/me` publica `country` y el requisito efectivo.

### Task 6: `PersonTypeRequirement` en los ajustes del tenant

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/PersonTypeRequirement.cs` (enum + `PersonTypeRequirementPolicy`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs`; `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateTenantSettings.cs`, `TenantSettingsDto.cs`, `TenantMappings.cs`; `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs`; `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs`
- Create: migración `AddTenantPersonTypeRequirement`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs`, `UpdateTenantSettingsHandlerTests.cs`; `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantSettingsApiTests.cs` (**integración**). Los llamadores de `UpdateSettings` en `TenantTests.cs`, `TenantStatusTests.cs` y `TenantDefaultCurrencyTests.cs` suman el argumento.

**Interfaces:**
- Produces:
  - `public enum PersonTypeRequirement { Never, OnCustomerSave, OnOrderApproval }`
  - `public static class PersonTypeRequirementPolicy { const string ColombiaCountryCode = "CO"; static bool IsColombian(string? country); static PersonTypeRequirement Effective(string? tenantCountry, PersonTypeRequirement stored); static PersonTypeRequirement Parse(string value); }` — `Effective` devuelve `stored` sólo si el tenant es `CO`; `Parse` inválido → `TenantDomainException("tenancy.settings.person_type_requirement.invalid", …)`. Customers y Quotations.Application lo usan para la regla del país (Global Constraints); Quotations.Domain, que no ve Tenancy, lleva su propia comparación con `"CO"`.
  - `Tenant.PersonTypeRequirement = Never`; `UpdateSettings(…, string country, PersonTypeRequirement personTypeRequirement, DateTimeOffset occurredAt)` con `TrackChange`.
  - `UpdateTenantSettingsCommand(…, string CorrelationId, string? PersonTypeRequirement = null)` — `null` conserva el guardado; `MaximumLength(20)`.
  - `TenantSettingsDto(…, TenantLogoDto? Logo, string PersonTypeRequirement)` (guardado); `UpdateTenantSettingsRequest(…, string? PersonTypeRequirement = null)`; `TenantSettingsResponse(…, string PersonTypeRequirement)`.
  - Columna `person_type_requirement varchar(20) NOT NULL`, `HasConversion<string>()`, `defaultValue: "Never"` en la migración.

- [ ] **Step 1: pruebas primero:**
  - `TenantTests`: `PersonTypeRequirementStartsAtNever`; `UpdateSettingsChangesThePersonTypeRequirementAndTracksIt`; `EffectiveIsNeverOutsideColombia` — `("CO", OnCustomerSave) → OnCustomerSave`, `("co", OnOrderApproval) → OnOrderApproval`, `("ES", OnCustomerSave) → Never`, `(null, OnOrderApproval) → Never`; `IsColombianIgnoresCase` — `"co"` true, `"ES"`/`null` false.
  - `UpdateTenantSettingsHandlerTests`: `ANullPersonTypeRequirementKeepsTheStoredOne`; `AnUnknownPersonTypeRequirementIsRejected` (`"Always"`); `ANonColombianTenantStillStoresTheValue`.
  - Integración: `PersonTypeRequirementRoundTripsAndDefaultsToNever`.
- [ ] **Step 2: implementar.** Migración `AddTenantPersonTypeRequirement` (Tenancy); si EF no pone el `defaultValue`, agregarlo a mano.
- [ ] **Step 3: commit** `feat(tenancy): ajuste «tipo de persona obligatorio» (spec 2026-10-26)`.

### Task 7: `country` y `personTypeRequirement` en `/auth/me`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs`, `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs`, `src/Api/AuthSessionEndpoints.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs` (**integración**)

**Interfaces:**
- Produces:
  - `TenantRegionalSettings(string TimeZone, string DateFormat, string DefaultCurrency, string NumberFormat, string Country = "CO", PersonTypeRequirement PersonTypeRequirement = PersonTypeRequirement.Never)` con `public PersonTypeRequirement EffectivePersonTypeRequirement => PersonTypeRequirementPolicy.Effective(Country, PersonTypeRequirement);`. `TenantDirectory` pasa los seis explícitos.
  - `ActiveTenantResponse(…, IReadOnlyList<string> CurrenciesInUse, string? Country, string? PersonTypeRequirement)` — `regional?.Country`, `regional?.EffectivePersonTypeRequirement.ToString()`.

- [ ] **Step 1: pruebas primero:** `SessionCarriesTheTenantCountryAndEffectivePersonTypeRequirement`; `ANonColombianTenantPublishesNever`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(auth): país y requisito de tipo de persona en /auth/me (spec 2026-10-26)`.

---

## Fase 3 — `OnCustomerSave` en el cliente

### Task 8: El alta y la edición exigen el tipo de persona del cliente colombiano

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerPersonTypeRequirement.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs`, `UpdateCustomer.cs` (inyectan `ITenantDirectory`, como `ExportCustomers.cs:81`)
- Modify: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteDoubles.cs` (fake `TenantDirectory` con `Regional` configurable)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs` (**integración**, una prueba)

**Interfaces:**
- Produces: `internal static class CustomerPersonTypeRequirement { static Task EnsureSatisfiedAsync(ITenantDirectory directory, Guid tenantId, string customerCountry, string? personType, CancellationToken ct); }` — lanza `CustomersDomainException(CustomersErrorCodes.PersonTypeRequired, "The tenant requires the customer's person type.", { ["PersonType"] = [...] })` sólo si: efectivo `OnCustomerSave` **y** `IsColombian(customerCountry)` (el `Country` del comando, que el validador ya exige) **y** `personType` en blanco. Se llama después del validador y antes de pedir el CUC, en los dos caminos del `PUT`. POS, provisión e importación no lo llaman (spec § Decisiones menores).

- [ ] **Step 1: pruebas primero** (unit):
  - `OnCustomerSaveRejectsAColombianCreateWithoutPersonType` → `customers.customer.person_type_required`, `FieldErrors.Keys == ["PersonType"]`, sin `Add` ni CUC pedido.
  - `OnCustomerSaveRejectsAnUpdateAndACompletionWithoutPersonType` (sobre completo e incompleto; el incompleto sigue incompleto).
  - `OnCustomerSaveAcceptsAPersonType`.
  - `NeverAndOnOrderApprovalDoNotRequireIt` (`[InlineData(Never)]`, `[InlineData(OnOrderApproval)]`).
  - `AForeignCustomerOfAColombianTenantSavesWithoutPersonType` — tenant `CO`/`OnCustomerSave`, comando `Country = "ES"`, `CityName = "Madrid"` → guarda.
  - `ANonColombianTenantWithOnCustomerSaveCreatesWithoutPersonType` — tenant `ES`, cliente `CO` → guarda (el ajuste no rige).
  - Integración: `OnCustomerSaveIsA422OnPersonType`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(customers): exigir tipo de persona con OnCustomerSave (spec 2026-10-26)`.

---

## Fase 4 — Parte de facturación de la cotización (Quotations)

Entregable sola: la fila `Billing` guarda y devuelve tipo de persona y tipo de documento, con la regla NIT y las exigencias donde hoy se exige el número; la conversión los copia; el bloque de cliente trae país, tipo de persona y tipo de documento.

### Task 9: Dominio — campos de la parte y regla NIT

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/PartyFiscalTypes.cs` (`PartyPersonType`, `PartyIdentificationType`, `PartyFiscalTypesParser`)
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/BillingPartyFiscalRules.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs`, `QuotationParty.cs`, `OrderParty.cs`, `Quotation.cs` (`BillingLacksIdentification`), `Order.cs` (`EnsureCreatable`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationsDomainException.cs` (constructor con `fieldErrors`, implementa `IHasFieldErrors`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`, `OrderTests.cs`, `BillingPartyFiscalRulesTests.cs` (nuevo)

**Interfaces:**
- Produces:
  - `public enum PartyPersonType { Natural, Juridica }`; `public enum PartyIdentificationType { Nit, Cc, Ce, Pasaporte }`.
  - `PartyFiscalTypesParser`: `ToWireValue` para los dos; `ParsePersonType(string?)`, `ParseIdentificationType(string?)` — blanco → `null`; inválido → `QuotationsDomainException("quotations.party.person_type_invalid" | "quotations.party.identification_type_invalid", …)`. Comentario: duplica `IdentificationTypeParser` de Customers porque Quotations no lo referencia.
  - `QuotationPartyDetails.PersonType` (`PartyPersonType?`), `.IdentificationType` (`PartyIdentificationType?`), `init`. `QuotationParty`/`OrderParty` los guardan; `Apply` en `Shipping` usa `details with { IdentificationNumber = null, PersonType = null, IdentificationType = null }`; `ToDetails` los copia (así `Order.FromQuotation` los lleva).
  - `public static class BillingPartyFiscalRules { public static void EnsureConsistent(QuotationPartyDetails? billing, string? tenantCountry); }` — sólo con `tenantCountry` colombiano, `Juridica` con `IdentificationType` no nulo y ≠ `Nit` → `QuotationsDomainException("quotations.party.person_type_requires_nit", "A legal entity is identified by its NIT.", { ["Parties.Billing.IdentificationType"] = [...] })`.
  - `Quotation.BillingLacksIdentification` y la regla de `Order.EnsureCreatable` (`Order.cs:482-488`) suman `|| billing.IdentificationType is null`: mismos códigos `quotation.billing.identification_required` / `order.billing.identification_required` (Desvío 4).

- [ ] **Step 1: pruebas primero:**
  - `BillingPartyFiscalRulesTests`: `AColombianJuridicaWithACcIsRejected` (`"CO"` → `quotations.party.person_type_requires_nit`, `FieldErrors.Keys == ["Parties.Billing.IdentificationType"]`); `ANonColombianTenantMayBillAJuridicaWithACc` (`"ES"` → no lanza); `AJuridicaWithoutIdentificationTypeIsNotAConflict`; `NoBillingRowIsNotAConflict`.
  - `QuotationTests`: `ABillingPartyKeepsPersonTypeAndIdentificationType`; `AShippingPartyNeverKeepsPersonTypeOrIdentificationType`; `ABillingRowWithANumberButNoIdentificationTypeIsNotIdentified` → `EnsureBillingIdentified`, `EnsureSendable` y `EnsureConvertibleToOrder` lanzan `quotation.billing.identification_required`.
  - `OrderTests`: `ConvertingCarriesTheBillingPartysPersonTypeAndIdentificationType` (`Natural`/`Ce`); `CreateManualRejectsABillingPartyWithoutIdentificationType` → `order.billing.identification_required`. Ajustar los builders existentes que arman una fila de facturación completa para que lleven `IdentificationType = Nit`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(quotations): tipo de persona y de documento en la parte de facturación (spec 2026-10-26)`.

### Task 10: Contrato y persistencia de la parte; bloque de cliente

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (`QuotationPartyRequest`, `QuotationPartyDto`, `QuotationPartyResponse`, `QuotationClientResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationEdits.cs` (`QuotationPartiesRequestValidator`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationMapping.cs` (`ToDomain`, línea 157) y los mapeos de partes (`OrderResponseComposer.cs:103` y el `ToDto` de partes)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs`, `SaveQuotation.cs`, `OrderDraftBuilder.cs` (alta manual del pedido): llaman `BillingPartyFiscalRules.EnsureConsistent` con el país del tenant
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCustomerLookup.cs` (`QuotationCustomerRef`), `QuotationResponseComposer.cs` (`ToClientResponse`, línea 113)
- Modify: `src/Bootstrapper/QuotationCustomerLookup.cs` (llena los tres campos nuevos, ~línea 117)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (las dos tablas, junto a `identification_number`, líneas 318 y 705)
- Create: migración `AddPartyPersonTypeAndIdentificationType` (Quotations)
- Test: el archivo de pruebas unitarias que ya cubre `QuotationPartiesRequestValidator` (o `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPartiesRequestValidatorTests.cs` si no existe); `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs`, `OrderApiTests.cs` (**integración**)

**Interfaces:**
- Produces:
  - `QuotationPartyRequest(…, Guid? CityId, string? PersonType = null, string? IdentificationType = null)`; `QuotationPartyDto`/`QuotationPartyResponse` ganan `string? PersonType, string? IdentificationType` al final (`Shipping` siempre `null`).
  - Validador: con la misma condición que el número (`requireBillingIdentification && !BillsToFinalConsumer && Billing.Name` no vacío), `RuleFor(p => p.Billing!.IdentificationType).NotEmpty()`; y `Must(valor soportado)` para los dos campos cuando vienen → `errors["Parties.Billing.IdentificationType"]` / `["Parties.Billing.PersonType"]`.
  - `QuotationCustomerRef(…, bool IsComplete = true, string? PersonType = null, string? Country = null, string? IdentificationType = null)`; el adaptador pasa `customer.PersonType?.ToWireValue()`, `customer.Country`, `customer.IdentificationType?.ToWireValue()`.
  - `QuotationClientResponse(…, IReadOnlyCollection<QuotationClientAddressResponse> Addresses, string? Country, string? PersonType, string? IdentificationType)` — llega a cotización y pedido por el mismo `ToClientResponse`.
  - `CreateQuotationHandler`, `SaveQuotationHandler` y el alta manual llaman `BillingPartyFiscalRules.EnsureConsistent(parties.Billing, regional?.Country)` (vía `ITenantDirectory.GetRegionalSettingsAsync`) junto a `EnsureBillingIdentified`; el cálculo previo no.
  - Columnas `person_type`/`identification_type varchar(16) NULL`, `HasConversion<string>()`, en `quotation_parties` y `order_parties`.

- [ ] **Step 1: pruebas primero:**
  - Unit (validador): `ABillingRowWithANameRequiresTheIdentificationType` → error en `Billing.IdentificationType`; `ThePreviewDoesNotRequireIt` (`requireBillingIdentification: false`); `AnUnknownIdentificationTypeIsRejected` (`"RUT"`).
  - Integración `QuotationApiTests`: `BillingPartyPersonTypeAndIdentificationTypeRoundTrip` (`"Juridica"`/`"NIT"` en `Billing`; `Shipping` `null`/`null`); `JuridicaBillingWithCcIsA422` (tenant `CO` → `quotations.party.person_type_requires_nit`, `errors` con `Parties.Billing.IdentificationType`); `TheClientBlockCarriesCountryPersonTypeAndIdentificationType` (cliente `CO`, `Juridica`, `NIT` → `client.country == "CO"`, `client.personType == "Juridica"`, `client.identificationType == "NIT"`).
  - Integración `OrderApiTests`: `ConvertedOrderCarriesTheBillingPartyFiscalTypes`; `TheOrderClientBlockCarriesCountryPersonTypeAndIdentificationType`.
- [ ] **Step 2: implementar.** Migración `AddPartyPersonTypeAndIdentificationType`: cuatro `AddColumn` nullable.
- [ ] **Step 3: commit** `feat(quotations): personType e identificationType en partes y bloque de cliente (spec 2026-10-26)`.

### Task 11: `OnCustomerSave` sobre la fila de facturación de la cotización

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/BillingPersonTypeRequirement.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs`, `SaveQuotation.cs`, `SendQuotation.cs`, `ConvertQuotationToOrder.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/BillingPersonTypeRequirementTests.cs` (nuevo, sobre el helper con `StubTenantDirectory`); `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs` (**integración**, una prueba)

**Interfaces:**
- Produces: `internal static class BillingPersonTypeRequirement { static Task EnsureForQuotationAsync(ITenantDirectory directory, Guid tenantId, Quotation quotation, CancellationToken ct); }` — lanza `QuotationsDomainException("quotation.billing.person_type_required", "The tenant requires the billing party's person type.", { ["Parties.Billing.PersonType"] = [...] })` sólo si: efectivo `OnCustomerSave` (implica tenant `CO`, que es el país de la fila), `!quotation.BillsToFinalConsumer`, `quotation.Billing?.Name` no vacío y `Billing.PersonType is null`. Se llama donde se llama `EnsureBillingIdentified`/`EnsureSendable`/`EnsureConvertibleToOrder` (Desvío 5). Facturar con los datos del cliente no se exige acá: lo cubre el cliente (Task 8).

- [ ] **Step 1: pruebas primero:** `OnCustomerSaveRequiresTheBillingRowsPersonType`; `ACustomerBilledQuotationIsNotChecked` (sin fila); `AFinalConsumerQuotationIsNotChecked`; `NeverAndOnOrderApprovalDoNotRequireIt`; `ANonColombianTenantIsNotChecked`. Integración: `SendingWithOnCustomerSaveAndABillingRowWithoutPersonTypeIsA422` → `quotation.billing.person_type_required`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(quotations): OnCustomerSave exige el tipo de persona de la facturación (spec 2026-10-26)`.

---

## Fase 5 — «Editar pedido» cambia la facturación

Entregable sola: un pedido `Pending` cambia su fila de facturación, consumidor final y perfil fiscal desde el guardado de ediciones, con recálculo.

### Task 12: Dominio — `Order.SetBilling`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderBillingEditTests.cs` (nuevo; molde `OrderShippingTests.cs`)

**Interfaces:**
- Produces: `public bool SetBilling(QuotationPartyDetails? billing, bool billsToFinalConsumer, bool billingUsesBusinessName, bool? partyWithRetention, bool? partyVatSurplus, MemberId updatedBy, DateTimeOffset occurredAt)` — `EnsurePending("The billing can only change while the order is pending.")` (`order.order.not_pending`); misma consistencia que el alta (`EnsureBillingIsConsistent`: consumidor final con fila o con razón social → `order.billing.final_consumer_conflict`); fila completa (`order.billing.party_incomplete`), identificada con número **y** tipo (`order.billing.identification_required`) y con perfil fiscal (`order.billing.tax_profile_required`) — las reglas de facturación de `EnsureCreatable`, extraídas a un método que comparten. La fila existente se **modifica** con `OrderParty.Apply`; sin fila previa se crea; `billing == null` la quita. Asigna `BillsToFinalConsumer`, `BillingUsesBusinessName`, `PartyWithRetention`/`PartyVatSurplus` (`null` sin fila, como `Order.cs:84-85`), `RecalculateTotals()` y `Touch`. Devuelve `false` (sin `Touch`) si nada cambió.

- [ ] **Step 1: pruebas primero:**
  - `SwitchingToAWithholdingBillingPartyRecalculatesTheRetention` — pedido COP facturado al cliente sin retención; `SetBilling(fila completa Nit, false, false, partyWithRetention: true, partyVatSurplus: false, …)` → `AppliesRetention == true`, `RetentionAmount > 0`, `Version` +1.
  - `SwitchingToTheFinalConsumerDropsTheRowAndTheRetention` — con fila → `SetBilling(null, true, false, null, null, …)` → `Billing == null`, `BillsToFinalConsumer`, `RetentionAmount == 0`.
  - `EditingTheExistingRowKeepsItsId` — el `Id` de `order.Billing` no cambia.
  - `AFinalConsumerWithARowIsAConflict` → `order.billing.final_consumer_conflict`.
  - `ARowWithoutIdentificationTypeIsRejected` → `order.billing.identification_required`.
  - `ANonPendingOrderCannotChangeItsBilling` → `order.order.not_pending`.
  - `TheSameBillingIsANoOp` → `false`, `Version` igual.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(orders): el pedido pendiente puede cambiar su facturación (spec 2026-10-26)`.

### Task 13: Contrato — `billing` en el guardado de ediciones

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` (`SaveOrderEditsRequest`, `OrderBillingEditRequest`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs` (comando, validador, handler)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (línea 404, paso al comando)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderChangeSummary.cs` (`BillingChanged`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SaveOrderEditsValidatorTests.cs` (unit); `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs` (**integración**)

**Interfaces:**
- Produces:
  - `public sealed record OrderBillingEditRequest(QuotationPartyRequest? Party, bool BillsToFinalConsumer = false, bool BillingUsesBusinessName = false, bool? BillingWithRetention = null, bool? BillingVatSurplus = null);`
  - `SaveOrderEditsRequest(…, ShippingRequest? Shipping = null, OrderBillingEditRequest? Billing = null)` — **ausente deja la facturación** (Desvío 6).
  - `SaveOrderEditsCommand(…, ShippingRequest? Shipping = null, OrderBillingEditRequest? Billing = null)`; el validador aplica a `Billing.Party` las mismas reglas de número y tipo de documento que `QuotationPartiesRequestValidator` con `requireBillingIdentification: true` (claves `Billing.Party.IdentificationNumber` / `Billing.Party.IdentificationType`, el prefijo que arma FluentValidation para el comando).
  - Handler: tras `RefreshCustomerTaxProfile` y en la misma escritura que `SetShipping`: `BillingPartyFiscalRules.EnsureConsistent(party, tenantCountry)` y `order.SetBilling(...)`; si cambió, entrada de historial con `OrderChangeSummary.BillingChanged(...)` (molde `ShippingChanged`). El 422 de aprobar con `missingOn: "BillingParty"` se resuelve por acá.

- [ ] **Step 1: pruebas primero:**
  - Unit (validador): `ABillingPartyWithANameRequiresNumberAndIdentificationType`; `AnAbsentBillingIsValid`.
  - Integración (`OrderEditsApiTests`): `EditingTheBillingPartySavesItWithPersonTypeAndRecalculates` — pedido `Pending`; guardado con `billing.party` `Juridica`/`NIT` y `billingWithRetention: true` → 200, detalle con la fila nueva y `retentionAmount` recalculado. `EditingAnExistingBillingRowKeepsASingleRow` — dos guardados seguidos con filas distintas → 200 y una sola parte `Billing` (cubre `IX_order_parties_order_role`). `AnAbsentBillingLeavesItUntouched`. `JuridicaWithCcIsA422` → `quotations.party.person_type_requires_nit`. `AnApprovedOrderCannotChangeItsBilling` → `order.order.not_pending`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(orders): editar la facturación desde «Editar pedido» (spec 2026-10-26)`.

---

## Fase 6 — `OnOrderApproval` (aprobar el pedido)

### Task 14: Aprobar exige el tipo de persona de a quien se factura

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OrderBilledParty.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/BilledPersonTypeRequired.cs` (molde `CustomerIdentificationTaken.cs`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ApproveOrder.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (`BilledIdentificationFor` pasa a usar `OrderBilledParty`, sin cambiar su salida)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubTenantDirectory` con `Regional` configurable)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ApproveOrderHandlerTests.cs`; `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs` (**integración**, una prueba: cubre el adaptador)

**Interfaces:**
- Produces:
  - `public enum BilledPartySource { FinalConsumer, BillingParty, Customer }`; `OrderBilledParty.SourceFor(bool billsToFinalConsumer, OrderParty? billing)` — `FinalConsumer`; `BillingParty` si `billing?.Name is { Length: > 0 }` (Desvío 1); si no, `Customer`.
  - `BilledPersonTypeRequiredException : DomainException, IHasProblemExtensions` — `Code = "order.order.billed_person_type_required"`, `ProblemExtensions["missingOn"] = "BillingParty" | "Customer"`.
  - `ApproveOrderHandler(…, IClock clock, ITenantDirectory tenantDirectory, IQuotationCustomerLookup customerLookup)`. Sólo con el pedido `Pending` (si no, sigue `order.order.not_pending`) y efectivo `OnOrderApproval`: `BillingParty` (país = tenant, `CO` por el efectivo) con `order.Billing.PersonType` nulo → `missingOn: "BillingParty"`; `Customer` → cliente **vigente** con `FindAsync(tenantId, order.ClientId)`, país `customer.Country ?? tenant.Country`; si es `CO` y no tiene tipo (o no resuelve) → `missingOn: "Customer"`; `FinalConsumer` → exento. Sin auditoría ni historial al fallar.

- [ ] **Step 1: pruebas primero** (unit; `NewHandler` gana directorio y lookup con defaults `CO`/`Never`, para que las dos pruebas existentes no cambien):
  - `OnOrderApprovalRejectsACustomerWithoutPersonType` → `missingOn == "Customer"`, `Status == Pending`, sin auditoría.
  - `WithoutABillingRowTheLiveCustomerIsRead` — cliente `"Natural"` → aprueba.
  - `WithABillingRowTheCustomersPersonTypeDoesNotCount` — fila sin tipo + cliente `"Juridica"` → `missingOn == "BillingParty"`; fila `Juridica` + cliente sin tipo → aprueba.
  - `AFinalConsumerOrderApprovesWithoutPersonType`.
  - `AForeignCustomerApprovesWithoutPersonType` — cliente `Country = "ES"` sin tipo → aprueba.
  - `ACustomerWithoutCountryUsesTheTenantCountry` — cliente `Country = null` sin tipo, tenant `CO` → `missingOn == "Customer"`.
  - `NeverAndOnCustomerSaveDoNotCheck`; `ANonColombianTenantWithOnOrderApprovalApprovesWithoutPersonType`; `AnApprovedOrderStillAnswersNotPending`.
  - Integración: `ApproveWithOnOrderApprovalIsA422UntilTheCustomerHasAPersonType` (422 `missingOn == "Customer"`; `PUT` del cliente con `"Natural"` → 200).
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(orders): aprobar exige el tipo de persona de a quien se factura (spec 2026-10-26)`.

---

## Fase 7 — Excel de pedidos

### Task 15: Columna de catálogo `customer_person_type`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` (al final, después de `variant`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (`RowsFor`: celda al final, después de `variante`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs`, `OrdersExportProcessorTests.cs`

**Interfaces:**
- Produces: `new("customer_person_type", "Tipo de persona", 16, DefaultVisible: false)` último de `Columns`. `BilledPersonTypeFor(Order, OrderParty?, QuotationCustomerRef?)` con `OrderBilledParty.SourceFor`: `FinalConsumer` → `"Natural"`; `BillingParty` → `billing.PersonType?.ToWireValue() ?? ""`; `Customer` → `customer?.PersonType ?? ""`. Sin filtro de país: el Excel muestra lo guardado. `OrdersExportProcessor.Columns` sigue en 43.

- [ ] **Step 1: pruebas primero:**
  - Catálogo: `HasTheFiftySixColumnsOfTheSpecInItsOrder` (renombrar la de 55); `CustomerPersonTypeIsAppendedAfterVariantHiddenByDefault` (`Columns[55]`); ajustar `VariantIsAppendedAtTheEndHiddenByDefault` (`IndexOf("variant") == 54`, ya no es la última).
  - Processor (`PersonTypeVisibleFirst()`, molde `CoordinadoraCityVisibleFirst`, celda en índice 0): `PersonTypeIsTheCustomersWhenThereIsNoBillingRow`; `PersonTypeIsTheBillingRowsWhenItHasAName`; `ABillingRowWithoutPersonTypeIsEmptyEvenIfTheCustomerHasOne`; `AFinalConsumerIsNatural`; `PersonTypeIsRepeatedOnEveryLine`; `WithoutALayoutThatShowsItPersonTypeIsNotWritten`.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(orders): columna «Tipo de persona» en el Excel de pedidos (spec 2026-10-26)`.

### Task 16: Layout sembrado con la columna visible

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Seed/QuotationsSeeder.cs` (`OrdersExportColumns`, después de `company_tax_id`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsSeedTests.cs` (**integración**; `SeededLayoutHeaders` y la de colisión no usan base)

**Interfaces:**
- Produces: `OrdersExportColumnSetting.Catalog("customer_person_type", "Tipo de persona", visible: true),` entre `company_tax_id` y `.. HiddenCatalogKeys()`. No se toca el «sólo crear» de `SeedOrdersExportLayoutAsync`.

- [ ] **Step 1: pruebas primero:** `SeededLayoutHeaders` con `"Tipo de persona"` entre `"Nit"` y `"Total facturado"`; `OrdersExportPersonTypeColumnDoesNotCollideWithSeededHeaders`; en `SeedConfiguresTheOrdersExportLayoutOfTheSeedTenant`, la columna con encabezado `"Tipo de persona"` es `customer_person_type` y va justo después de `"Nit"`. `OrderExportApiTests` sigue verde sin tocarlo.
- [ ] **Step 2: implementar.**
- [ ] **Step 3: commit** `feat(orders): el layout sembrado trae «Tipo de persona» visible (spec 2026-10-26)`.

---

## Cómo correr las pruebas

Las corre el owner al final (o por fase). **PowerShell**, desde la raíz del repo.

**Antes de empezar:**
```powershell
Get-Process Api -ErrorAction SilentlyContinue | Stop-Process -Force   # Api.exe bloquea build, test y ef
docker info > $null; if ($?) { "Docker OK" }                          # las de integración lo necesitan
$env:Seed__Enabled = "false"                                          # que user-secrets no siembre en las fábricas
```

**Unitarias por fase** (rápidas, sin Docker):
```powershell
# Fases 1 y 3 (Customers)
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerPersonTypeTests|FullyQualifiedName~CustomerWriteHandlersTests|FullyQualifiedName~CustomerMergePlanTests|FullyQualifiedName~ClosedXmlCustomerExportBuilderTests"
# Fase 2 (Tenancy)
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantTests|FullyQualifiedName~UpdateTenantSettingsHandlerTests"
# Fases 4 a 7 (Quotations)
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~BillingPartyFiscalRulesTests|FullyQualifiedName~QuotationTests|FullyQualifiedName~OrderTests|FullyQualifiedName~QuotationPartiesRequestValidator|FullyQualifiedName~BillingPersonTypeRequirementTests|FullyQualifiedName~OrderBillingEditTests|FullyQualifiedName~SaveOrderEditsValidatorTests|FullyQualifiedName~ApproveOrderHandlerTests|FullyQualifiedName~OrdersExportColumnCatalogTests|FullyQualifiedName~OrdersExportProcessorTests"
```
Después, las suites unitarias completas (los cambios de firma tocan dobles de otras clases):
```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
```

**Arquitectura:**
```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests
```

**Integración** (Testcontainers `postgres:18-alpine`, lentas: sólo las clases tocadas):
```powershell
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerStatusAndImportApiTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantSettingsApiTests|FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~TenantDefaultCurrencyTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationApiTests|FullyQualifiedName~OrderApiTests|FullyQualifiedName~OrderEditsApiTests|FullyQualifiedName~ManualOrderApiTests|FullyQualifiedName~RetailQuotationApiTests|FullyQualifiedName~QuotationsSeedTests|FullyQualifiedName~OrderExportApiTests"
```
`ManualOrderApiTests` y `RetailQuotationApiTests` entran porque ahora el tipo de documento de la fila es obligatorio donde lo es el número: las pruebas existentes que facturan «a otros datos» tienen que mandarlo (si alguna falla con `identification_required`, es eso).

**Qué es pasar:** cada comando termina con `Correctas! - Con error: 0` (o `Passed! - Failed: 0`) y `$LASTEXITCODE -eq 0`. Un `Omitido` distinto de 0 en integración suele ser Docker apagado. Un `error CS…` antes del resumen es un fallo de compilación de la tarea que cambió la firma, no de la prueba. Un error de archivo bloqueado es un `Api.exe` vivo.

**Evidencia RED** (si el owner la quiere): `git stash` de la implementación de una tarea dejando sus pruebas, correr el filtro de la tarea (esperado: falla de compilación o de aserción), `git stash pop`.

## Nota final (al mergear a `develop`)

Actualizar `docs/estado.md` en `develop`. Despliegue: backend y **frontend inmediatamente después** (Desvío 4); las tres migraciones son aditivas y sin backfill. Después del deploy, el owner borra la fila del layout de `origen-botanico` para que la semilla la recree con «Tipo de persona».
