# Integración con Siigo — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Este repo manda sobre el proceso del skill** (CLAUDE.md del workspace, «Presupuesto de pruebas»): las tareas se
> ejecutan en **cuatro bloques**, un implementador por bloque, una revisión por bloque y la revisión fuerte una sola vez al
> final. Cada tarea conserva su commit.

**Goal:** que un tenant conecte una o más cuentas de Siigo, cada una atada a una empresa de QEP, las configure con un
asistente y genere desde un pedido la factura de venta en Siigo, con la marca de facturado automática.

**Architecture:** módulo nuevo `Siigo` (esquema `siigo`, cuatro capas, calcado de `Shopify`). La conexión y el secreto
viven en Integrations (proveedor `siigo`); Quotations, Catalog, Companies, Geography y Customers entran por puertos de
`SiigoPorts.cs` con adaptadores en `src/Bootstrapper/Siigo*.cs`. La factura se arma en código puro y se envía con
`Idempotency-Key`; el rechazo `invalid_total_payments` de Siigo es la defensa de los totales.

**Tech Stack:** .NET 10, EF Core + Npgsql, minimal APIs, `IHttpClientFactory`, xUnit v3, Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-11-18-integracion-siigo-design.md` (léelo entero antes de empezar; las
referencias «§N» de este plan son a él).

## Global Constraints

- Mensajes al usuario y comentarios en español colombiano, tuteando (`CLAUDE.md` del repo). Códigos, rutas y enums no se traducen.
- Códigos de error con prefijo `siigo.*`; los de Quotations, `order.order.*`. Ningún status HTTP se arma en un handler.
- Nunca imprimir ni loguear `accessKey`, el token de Siigo ni `request_json` (lleva datos del cliente).
- `Idempotency-Key`: alfanumérico ASCII, **máximo 30 caracteres**. `Partner-Id`: 3 a 100 alfanuméricos, sin espacios.
- Tolerancia de cuadre de totales: **1 peso**. Sólo moneda **COP**.
- `send_to_dian` nace **`false`**; `send_mail` nace `false`; `freight_separate_invoice` nace `false`.
- Ninguna prueba automática llama a Siigo: todo va contra el fake `FakeSiigoApi` con fixtures del contrato (§3).
- Migraciones **aditivas**; ningún `Down` de este plan se corre en producción (§8).
- Lock advisory nuevo: `SiigoInvoiceLock.Namespace = 20261119` (no está en uso).
- Commits: conventional commits, **sin atribución de IA** (regla del usuario y del repo; pisa cualquier recordatorio).
- Antes de commitear: `git branch --show-current` debe decir `feature/siigo`.
- TDD: RED antes que GREEN con evidencia literal. **Si el fallo esperado es de compilación, la evidencia es la salida
  del build y no se levantan contenedores.** La integración se filtra por nombre de prueba mientras se itera; la clase
  completa corre una vez al final del bloque. Antes de una corrida de integración:
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (si hay una ajena, se espera; no se detiene) y
  `$env:Seed__Enabled = "false"`. `Api.exe` corriendo bloquea el build: se avisa, no se detiene.

## Review Focus

1. **Doble clic o dos pestañas** en «Facturar en Siigo» del mismo pedido → una sola factura y un solo `POST /v1/invoices`.
   Prueba: Tarea 9, `TwoConcurrentIssuesCreateOneInvoice`.
2. **NIT escrito con dígito de verificación** («900.123.456-7») → `identification = "900123456"`, `id_type = "31"`.
   Prueba: Tarea 8, `ANitIsSentWithoutCheckDigitAndOnlyDigits`.
3. **Cuenta de cobro renombrada** en la empresa después de congelar el pedido → faltante `payment_unlinked` con el texto
   viejo, no un 500 ni un pago a la forma equivocada. Prueba: Tarea 8, `ARenamedBankAccountIsReportedAsUnlinked`.
4. **Token de Siigo vencido** a mitad de un envío (401 en `POST /v1/invoices`) → se reautentica una vez y se reenvía con la
   **misma** `Idempotency-Key`. Prueba: Tarea 5, `A401RenewsTheTokenOnceAndRetriesWithTheSameKey`.
5. **Conexión pausada o borrada con una factura `Pending`** → `409 siigo.connection_unavailable` y la fila no cambia.
   Prueba: Tarea 9, `RetryingAPendingInvoiceWithoutConnectionLeavesItPending`.

---

## Bloque A — Fundación (Tareas 1–3)

### Tarea 1: Clave de módulo `siigo` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (CHECK `CK_tenant_modules_module_key`, línea ~150)
- Create: migración `AddSiigoModuleKey` en `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs` y las listas fijas de `TenantModulesApiTests.cs`, `OperatorConsoleApiTests.cs`, `SeedStartupTests.cs`

**Interfaces:**
- Produces: `TenantModuleKeys.Siigo` (`"siigo"`), al final de `All`, **fuera** de `DefaultForNewTenants`, con `Dependencies[Siigo] = [Orders]`.

- [ ] **Step 1: Escribir las pruebas que fallan** en `TenantModuleKeysTests`: `SiigoIsAKnownKeyOutsideTheDefaults`
  (`TenantModuleKeys.Parse("siigo") == TenantModuleKeys.Siigo`; `DefaultForNewTenants` no lo contiene;
  `DependenciesOf(Siigo)` es `[Orders]`). Sumar `"siigo"` a las listas literales de las tres pruebas de integración.
- [ ] **Step 2: Correr y ver el RED de compilación**
  Run: `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-restore`
  Expected: `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Siigo'`
- [ ] **Step 3: Declarar la clave** y ampliar el literal del CHECK en `TenancyDbContext` con `'siigo'`. Generar la
  migración (`dotnet ef migrations add AddSiigoModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`)
  y dejarla como `AddWholesaleModuleKey`: `Up` hace drop y add del CHECK con la lista completa; `Down` borra las filas
  `siigo` y repone la lista anterior.
- [ ] **Step 4: Correr unitarias y las integraciones filtradas**
  Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests"`
  y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests"`
  Expected: PASS.
- [ ] **Step 5: Commit** — `feat(tenancy): add the siigo module key`

### Tarea 2: Proveedor `siigo` en Integrations, con su probador

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs` (enum `IntegrationCategory`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Siigo/SiigoConnectionTester.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs`
- Create: migración `AddSiigoProvider` (Integrations)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/SiigoConnectionTesterTests.cs`, `IntegrationsPersistenceTests.cs:84`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Consumes: `TenantModuleKeys.Siigo` (Tarea 1).
- Produces: `IntegrationCategory.Accounting` (valor nuevo, al final); `SiigoFieldKeys.Username = "username"`,
  `SiigoFieldKeys.AccessKey = "accessKey"`, `SiigoFieldKeys.AccountName = "accountName"`;
  `IntegrationProviders.Siigo` (clave `"siigo"`, nombre «Siigo», `Accounting`, consumidor `[TenantModuleKeys.Siigo]`,
  tope `DefaultMaxConnections`, `ProviderOnboarding.Form`, `requiresTenantEnablement: false`);
  `SiigoConnectionTester.HttpClientName = "integrations.siigo"`.

Campos del proveedor: `username` (`Text`, requerido, máx. 256, sin patrón propio), `accessKey` (`Secret`, requerido,
máx. 512, patrón `^[\x21-\x7E]{1,512}\z`, `revealSuffix: 4`), `accountName` (`Text`, no requerido, máx. 256, lo
escribe el probador; sólo lectura en pantalla). El probador lee `Siigo:BaseUrl` (default `https://api.siigo.com`) y
`Siigo:PartnerId` de `IConfiguration`: Integrations no ve al módulo Siigo, y la validación de que existan es de la
Tarea 5.

- [ ] **Step 1: Escribir las pruebas que fallan** (`SiigoConnectionTesterTests`, con un `HttpMessageHandler` falso):
  - `ValidCredentialsAuthenticateAndReadDocumentTypes`: espera `POST /auth` con cuerpo `{"username":…,"access_key":…}` y
    header `Partner-Id`, luego `GET /v1/document-types?type=FV` con `Authorization: Bearer <token>`; resultado `Ok`.
  - `RejectedCredentialsAreReportedAsRejected`: `/auth` responde 401 → `CredentialsRejected`.
  - `AServerErrorIsUnreachable`: 503 → `Unreachable("http_503")`.
  - En `CompositionRootTests`: `TheSiigoTesterHttpClientHasNoLoggersAndNoRedirects` (mismo patrón que
    `TheOpenAiHttpClientHasNoLoggersAndNoRedirects`).
  - En `IntegrationsPersistenceTests`: el CHECK incluye `'siigo'`.
- [ ] **Step 2: Ver el RED de compilación** — `dotnet build tests/Modules/Integrations/Modules.Integrations.UnitTests --no-restore`; Expected: `SiigoConnectionTester` no existe.
- [ ] **Step 3: Implementar** el enum, las claves, el proveedor (sumarlo a `All`), y `SiigoConnectionTester : IProviderConnectionTester`
  como `AnthropicConnectionTester` (`CreatePrimaryHandler` sin redirecciones, `ConfigureClient` con 15 s y UA
  `qep-integrations`, `Classify`). Si `Siigo:PartnerId` falta, el resultado es `Unreachable("partner_id_missing")`.
  Registrar el cliente con `.RemoveAllLoggers()` y el probador como singleton. Migración `AddSiigoProvider` como
  `AddOpenAiProvider` (su `Down` borra las conexiones `siigo` primero).
- [ ] **Step 4: Correr** las unitarias de Integrations filtradas por `SiigoConnectionTesterTests`, `CompositionRootTests`
  filtrada por `TheSiigoTesterHttpClient` y la integración filtrada por `IntegrationsPersistenceTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(integrations): add the siigo provider and its connection tester`

### Tarea 3: Esqueleto del módulo `Siigo`, permisos y autorización

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.{Domain,Application,Infrastructure,Api}/` (csproj y marcadores: `SiigoDomainException`, `ISiigoUnitOfWork`, `SiigoInfrastructureExtensions`, `SiigoEndpoints`)
- Create: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs` (`Schema = "siigo"`, `AuditEntries` con `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`), `SiigoUnitOfWork.cs`, `SiigoAuditRecorder.cs`, `SiigoDatabaseInitializer.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPermissions.cs`, `SiigoAuthorization.cs`, `ISiigoAuditRecorder.cs`, `SiigoAuditActions.cs`
- Create: `tests/Modules/Siigo/Modules.Siigo.UnitTests/`, `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/` (con `SiigoApiHarness.cs`)
- Modify: `Backend.slnx`, `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Api.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (bloque Siigo, políticas, `PermissionDefinition`, roles `admin` y `billing`), `src/Api/Program.cs` (`MapSiigoEndpoints()`, `InitializeSiigoDatabaseAsync` dentro de `RunUnderStartupLockAsync`, después de Audit)
- Test: `tests/ArchitectureTests/ArchitectureTests/SiigoLayerTests.cs`, `CompositionRootTests.cs`

**Interfaces:**
- Produces:
  - `SiigoPermissions.SettingsManage = "siigo.settings.manage"`, `SiigoPermissions.InvoiceIssue = "siigo.invoice.issue"`,
    con `RequiredModules: [ModuleKeys.Siigo]`; `admin` recibe los dos y `billing` sólo `InvoiceIssue`.
  - `SiigoAuthorization.EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, string permission, CancellationToken)`:
    tenant y permiso (403 `authorization.denied`) y luego `TenantModuleGuard.EnsureEnabledAsync(..., TenantModuleKeys.Siigo, ...)`.
  - `ISiigoAuditRecorder.Record(Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId, string changedFieldsJson, DateTimeOffset occurredAt)`,
    con `Source = "siigo"` (copia de `IShopifyAuditRecorder`).
  - `ISiigoUnitOfWork`: `SaveChangesAsync`, `BeginTransactionAsync(ct) : Task<IAsyncDisposable>`,
    `LockOrderAsync(Guid orderId, ct)` (`pg_advisory_xact_lock(SiigoInvoiceLock.Namespace, hashtext(orderId))`, lanza si
    no hay transacción, como `ShopifyUnitOfWork.LockProductAsync`).
  - `SiigoApiHarness`: `QepApiFactory` anidada (plantilla `qep_template_siigo`, `Seed:Enabled=false`, clave de
    `Integrations:SecretProtection`, `Siigo:PartnerId=qepTest`), `StartDatabaseAsync()`, `CreateClient(factory, subjectId, tenantId, params string[] permissions)`,
    `RegisterTenantAsync`, `EnableSiigoAsync(string cs, Guid tenantId)` (el `INSERT … ON CONFLICT` de `EnableShopifyAsync`
    con `'siigo'`), `LinkSiigoConnectionAsync(factory, tenantId, name) : Task<Guid>` (como `LinkStoreAsync`, con
    `username`/`accessKey` de prueba).

- [ ] **Step 1: Escribir las pruebas que fallan:**
  - `SiigoLayerTests` copiada de `ShopifyLayerTests` con los cuatro marcadores; la teoría `SiigoDoesNotReferenceOtherBusinessModules`
    con `Modules.Integrations`, `Modules.Quotations`, `Modules.Customers`, `Modules.Catalog`, `Modules.Geography`, `Modules.Companies`.
  - En `CompositionRootTests`: `PermissionDiscoveryFindsTheNinetyTwoConstants` pasa a **94** (renombrar a `…NinetyFourConstants`);
    `TheSiigoPermissionsRequireTheSiigoModuleAndGoToAdminAndBilling` (admin tiene los dos, billing sólo `InvoiceIssue`).
  - Una integración mínima `SiigoModuleGuardApiTests.WithoutTheModuleTheSettingsAre403` (llega en la Tarea 6; aquí sólo
    el harness compila).
- [ ] **Step 2: Ver el RED de compilación** — `dotnet build tests/ArchitectureTests/ArchitectureTests --no-restore`; Expected: tipos `Modules.Siigo.*` no existen.
- [ ] **Step 3: Crear el esqueleto y el cableado** según **Files**. `SiigoDbContext` sin tablas propias todavía (sólo
  auditoría y el outbox excluido de migraciones, como Shopify); migración `InitialSiigo` vacía de tablas de negocio se
  genera en la Tarea 4, no aquí. Registrar en `AddQepPlatform` el bloque Siigo (`AddSiigoInfrastructure(configuration)`).
- [ ] **Step 4: Correr** `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~SiigoLayerTests|FullyQualifiedName~CompositionRootTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): scaffold the siigo module with its permissions`

> **Cierre del Bloque A:** `dotnet build --no-restore` de la solución y una revisión del bloque.

---

## Bloque B — Configuración y asistente (Tareas 4–7)

### Tarea 4: Configuración por conexión (dominio y persistencia)

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoSettings.cs`, `SiigoVariantLink.cs`, `SiigoTaxLink.cs`, `SiigoWithholdingLink.cs`, `SiigoPaymentLink.cs`, `SiigoSettingsReadiness.cs`, `BankAccountKey.cs`, `SiigoErrorCodes.cs`
- Modify: `SiigoDbContext.cs`; Create: migración `InitialSiigo`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoSettingsTests.cs`, `SiigoSettingsReadinessTests.cs`, `BankAccountKeyTests.cs`

**Interfaces:**
- Produces:
  - `SiigoSettings` (PK `ConnectionId`, `TenantId`, `Version`): `CompanyId?`, `DocumentTypeId?`, `SellerId?`, `CostCenterId?`,
    `WarehouseId?`, `SendToDian` (**false**), `SendMail` (false), `FreightSeparateInvoice` (false), `FreightProductCode?`,
    `CreditPaymentTypeId?`, `CreditDays?`, `CashOnDeliveryPaymentTypeId?`, `DefaultPaymentTypeId?`, más las colecciones de
    vínculos. `CashOnDeliveryPaymentTypeId` es columna nueva respecto de la tabla de §5.2: la forma de contraentrega que
    usa §6.3 tiene que vivir en algún lado. Métodos: `static Create(ConnectionId, TenantId)`, `AssignCompany(Guid)`,
    `UpdateDocument(SiigoDocumentSettings)`, `ReplaceTaxLinks(IReadOnlyList<SiigoTaxLink>)`,
    `ReplaceWithholdingLinks(…)`, `UpdatePayments(SiigoPaymentSettings)`, `LinkVariant(Guid variantId, string code, string siigoProductId, string name, SiigoLinkOrigin origin, Guid memberId, DateTimeOffset at)`,
    `UnlinkVariant(Guid variantId)`. Toda escritura sube `Version`.
  - `SiigoLinkOrigin { AutoCode, Manual }`.
  - `BankAccountKey.Normalize(string? bankName, string? accountNumber) : string?` (mayúsculas, sin espacios, `-` ni `.`;
    `null` si falta cualquiera de los dos).
  - `SiigoSettingsReadiness.Evaluate(SiigoSettings) : IReadOnlyList<SiigoStepReadiness>` con
    `SiigoStepReadiness(SiigoSetupStep Step, bool Complete, IReadOnlyList<string> MissingCodes)` y
    `SiigoSetupStep { Credentials, Company, Document, Taxes, Payments, Products }`.
  - Tablas (§5.2): `siigo.settings`; `siigo.variant_links` único `(connection_id, variant_id)`;
    `siigo.tax_links` único `(connection_id, tax_percentage)`; `siigo.withholding_links` único `(connection_id, tax_rate_id)`;
    `siigo.payment_links` único `(connection_id, bank_account_key)`; índice único `IX_settings_tenant_company`
    `(tenant_id, company_id)` parcial `WHERE company_id IS NOT NULL`. `send_to_dian` con `HasDefaultValue(false)`.

- [ ] **Step 1: Escribir las pruebas que fallan:**
  - `ANewSettingsHasDianOffAndEverythingElseEmpty`: `Create` deja `SendToDian == false`, `SendMail == false`, `FreightSeparateInvoice == false`, `Version == 0`.
  - `EveryWriteBumpsTheVersion`.
  - `BankAccountKeyTests`: `("Bancolombia", "000-123.45 6")` → `"BANCOLOMBIA|000123456"`; `(null, "1")` → `null`.
    (El separador `|` entre banco y número es decisión del plan, para que «AB»+«1» no choque con «A»+«B1».)
  - `SiigoSettingsReadinessTests`: sin empresa → `Company` incompleto con `company_missing`; sin comprobante o vendedor →
    `Document` con `document_type_missing` / `seller_missing`; con todo → los seis pasos completos. `Credentials` siempre
    completo aquí (lo decide la conexión, Tarea 6). `Products` está completo con cobertura 0: la cobertura se informa,
    no bloquea.
- [ ] **Step 2: Ver el RED de compilación** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests --no-restore`.
- [ ] **Step 3: Implementar** las entidades, el mapeo y la migración `InitialSiigo` (`dotnet ef migrations add InitialSiigo --project src/Modules/Siigo/Modules.Siigo.Infrastructure --context SiigoDbContext -o Persistence/Migrations`).
  `SiigoUnitOfWork.SaveChangesAsync` traduce `IX_settings_tenant_company` (por **nombre de índice**) a
  `409 siigo.settings.company_taken` y la concurrencia a `RequestConcurrencyException`.
- [ ] **Step 4: Correr** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoSettings|FullyQualifiedName~BankAccountKey"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): per-connection settings and links`

### Tarea 5: Cliente HTTP de Siigo

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/ISiigoApi.cs` (puerto y DTO del contrato), `SiigoApiErrors.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs`, `SiigoTokenCache.cs`, `SiigoOptions.cs`, `SiigoOptionsValidator.cs`
- Create: `tests/Modules/Siigo/Modules.Siigo.UnitTests/Fixtures/` (JSON copiados de la forma del contrato de §3), `FakeSiigoApi.cs` (en el proyecto de integración, compartido por las Tareas 6–10)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoApiClientTests.cs`, `SiigoOptionsValidatorTests.cs`; `CompositionRootTests`; `MultiReplicaInventory.cs`

**Interfaces:**
- Consumes: `SiigoCredentials(Guid ConnectionId, string Username, string AccessKey)` (lo produce el puerto de la Tarea 6).
- Produces `ISiigoApi` (todas reciben `SiigoCredentials` y `CancellationToken`):
  - `ListDocumentTypesAsync() : IReadOnlyList<SiigoOption>` (`type=FV`), `ListUsersAsync()`, `ListCostCentersAsync()`,
    `ListWarehousesAsync()`, `ListPaymentTypesAsync()` (`document_type=FV`), `ListTaxesAsync() : IReadOnlyList<SiigoTaxOption>`
    (`Id`, `Name`, `Type`, `Percentage`, `Active`), `SearchProductsAsync(string? code, int page) : SiigoPage<SiigoProductOption>`
    (`Id`, `Code`, `Name`, `Active`). `SiigoOption(string Id, string Name)`.
  - `FindCustomerAsync(string identification) : SiigoCustomerRef?`, `CreateCustomerAsync(SiigoCustomerRequest) : SiigoCustomerRef`.
  - `CreateInvoiceAsync(SiigoInvoiceRequest request, string idempotencyKey) : SiigoInvoiceOutcome`.
  - `StampInvoiceAsync(string siigoInvoiceId) : SiigoStampOutcome`, `GetStampErrorsAsync(string siigoInvoiceId) : IReadOnlyList<SiigoError>`.
  - `SiigoInvoiceOutcome` es una unión: `Created(string Id, string Name, decimal Total, string? Cufe)`,
    `Rejected(IReadOnlyList<SiigoError> Errors, decimal? ExpectedPaymentsTotal)`, `Transient(string Reason)`.
    `SiigoError(string Code, string Message)`. `ExpectedPaymentsTotal` sale del último número del mensaje de
    `invalid_total_payments`, leído con cultura invariante; si no se puede leer, `null`.
  - `SiigoOptions` (`Siigo:BaseUrl`, default `https://api.siigo.com`; `Siigo:PartnerId`); `SiigoApiClient.HttpClientName = "siigo"`.
- Reglas del cliente: `Partner-Id` en toda llamada; token por conexión en `SiigoTokenCache` (singleton en memoria) hasta
  `expires_in − 5 min`; **401 → invalida el token, reautentica una vez y repite la misma petición** (con la misma
  `Idempotency-Key`), y un segundo 401 es `Rejected` con `credentials_rejected`; timeout 30 s; sin reintentos propios;
  `429`, `5xx`, timeout y red son `Transient`; cuerpo de error `{ "Errors": [ { "Code", "Message" } ] }`.
  Listas con caché de 5 min por conexión (`IMemoryCache`, clave `siigo:{connectionId}:{lista}`); `CreateInvoiceAsync` y
  los clientes nunca se cachean.

- [ ] **Step 1: Escribir las pruebas que fallan** (`SiigoApiClientTests`, con handler falso y fixtures):
  - `EveryCallCarriesPartnerIdAndBearerToken`.
  - `TheTokenIsReusedUntilFiveMinutesBeforeExpiry` (reloj falso: dos llamadas, un solo `/auth`; pasado el margen, un segundo `/auth`).
  - `A401RenewsTheTokenOnceAndRetriesWithTheSameKey` (Review Focus 4): `POST /v1/invoices` → 401, `/auth`, `POST` otra vez con el mismo `Idempotency-Key`; resultado `Created`.
  - `InvalidTotalPaymentsIsRejectedWithTheExpectedTotal`: fixture `{"Errors":[{"Code":"invalid_total_payments","Message":"El total de pagos debe ser 288490.5"}]}` → `Rejected`, `ExpectedPaymentsTotal == 288490.5m`.
  - `AServerErrorIsTransient` (503) y `ATimeoutIsTransient`.
  - `ACreatedInvoiceReadsIdNameAndTotal` (fixture con `id`, `name: "FV-1-123"`, `total`).
  - `SiigoOptionsValidatorTests`: en `Production`, `PartnerId` vacío, con espacios, de más de 100 o `#{SIIGO_PARTNER_ID}#` falla; en `Development` vacío pasa.
  - `CompositionRootTests.TheSiigoHttpClientHasNoLoggersAndNoRedirects`.
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar** según las reglas. Registrar el cliente nombrado con `.RemoveAllLoggers()` y handler sin
  redirecciones, `ValidateOnStart` para `SiigoOptions`, y sumar `SiigoTokenCache` a `MultiReplicaInventory` como
  `PerPod` con la evidencia «caché de token por pod; un token de más por réplica no tiene efecto».
- [ ] **Step 4: Correr** las unitarias filtradas por `SiigoApiClientTests|SiigoOptionsValidatorTests` y
  `CompositionRootTests` filtrada por `TheSiigoHttpClient`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): siigo api client with token cache and idempotent invoice creation`

### Tarea 6: Puertos y endpoints de configuración

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (puertos de esta tarea y de las 7–9)
- Create: `src/Bootstrapper/SiigoConnectionDirectory.cs`, `SiigoCompanyDirectory.cs`, `SiigoTaxRateDirectory.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Settings/` (`GetSiigoSettings`, `AssignSiigoCompany`, `UpdateSiigoDocument`, `UpdateSiigoTaxes`, `UpdateSiigoPayments`, `ListSiigoOptions`, con validadores) y `SiigoSettingsDto.cs`
- Modify: `SiigoEndpoints.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoSettingsApiTests.cs`

**Interfaces:**
- Produces (puertos, adaptadores en Bootstrapper):
  - `ISiigoConnectionDirectory.ResolveAsync(Guid tenantId, Guid connectionId, ct) : SiigoConnectionResolution`
    (`SiigoCredentials? Credentials`, `string? UnavailableReason`), sobre `IIntegrationConnections.ResolveAsync` como
    `AiProviderCredentials`; una conexión de otro proveedor es `not_found`. `ListByTenantAsync(tenantId)` para listar.
  - `ISiigoCompanyDirectory`: `FindCompanyAsync(tenantId, companyId) : SiigoCompanyRef?` (`Id`, `Name`, `IsActive`) y
    `ListBillingAccountsAsync(tenantId, companyId) : IReadOnlyList<SiigoBillingAccountRef>` (`BankName`, `AccountNumber`,
    `Key` = `BankAccountKey.Normalize`). Lee la misma fuente de la que `QuotationBillingAccount` toma su foto.
  - `ISiigoTaxRateDirectory`: `ListVatPercentagesInUseAsync(tenantId) : IReadOnlyList<int>` (tasas de IVA de las
    variantes activas; siempre incluye 0) y `ListWithholdingRatesAsync(tenantId, companyId) : IReadOnlyList<SiigoWithholdingRef>`
    (`TaxRateId`, `Name`, `Type`, `Percentage`).
- Endpoints (`/api/v1/tenants/{tenantId:guid}/siigo/connections/{connectionId:guid}/…`, todos con `SiigoAuthorization`
  y `siigo.settings.manage`; los `PUT` con `If-Match` = `Version`):
  - `GET settings` → `SiigoSettingsDto` (todo el contenido de §5.2, más `readiness` por paso, más `connection`
    {`name`, `status`}); la fila se crea al primer acceso (`INSERT … ON CONFLICT DO NOTHING`).
  - `PUT company` `{ companyId }` → 422 `siigo.settings.company_invalid` si la empresa no existe o está inactiva; 409
    `siigo.settings.company_taken`.
  - `PUT document` `{ documentTypeId, sellerId, costCenterId?, warehouseId?, sendToDian, sendMail, freightSeparateInvoice, freightProductCode? }`.
  - `PUT taxes` `{ vat: [{ taxPercentage, siigoTaxId? }], withholdings: [{ taxRateId, siigoTaxId? }] }` (la lista vuelve entera y en orden).
  - `PUT payments` `{ accounts: [{ bankAccountKey, siigoPaymentTypeId? }], creditPaymentTypeId?, creditDays?, cashOnDeliveryPaymentTypeId?, defaultPaymentTypeId? }`.
  - `GET options/{list}` con `list` ∈ `document-types|users|cost-centers|warehouses|payment-types|taxes` → opciones de
    Siigo; conexión no disponible → `409 siigo.connection_unavailable`; Siigo transitorio → `503 siigo.unavailable`.
  - Auditoría `siigo.settings.updated` con `changedFields` = nombres de los pasos tocados, **nunca** valores de Siigo.

- [ ] **Step 1: Escribir las pruebas de integración que fallan** (`SiigoSettingsApiTests`, con `FakeSiigoApi`):
  - `WithoutTheModuleTheSettingsAre403` y `AConnectionOfAnotherTenantIs403`.
  - `TheFirstGetCreatesEmptySettingsWithDianOff` (`sendToDian == false`, `readiness` con `Company` incompleto).
  - `TwoConnectionsCannotShareACompany` → segundo `PUT company` es 409 `siigo.settings.company_taken`.
  - `AStaleIfMatchIs412`.
  - `TheTaxesStepListsTheVatRatesInUseAndTheCompanyWithholdings`.
  - `OptionsComeFromSiigoAndAreCachedForFiveMinutes` (dos `GET options/users`, una llamada al fake).
  - `TheAuditNamesTheStepButNotTheSiigoValues`.
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar** puertos, adaptadores (registrados en el bloque Siigo de `AddQepPlatform`), handlers con
  `ICommandHandler`/`IQueryHandler` y validadores FluentValidation para los cuerpos.
- [ ] **Step 4: Correr** `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoSettingsApiTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): settings endpoints for the setup wizard`

### Tarea 7: Vínculo de productos

**Files:**
- Create: `src/Bootstrapper/SiigoVariantCatalog.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Products/` (`ListSiigoVariantLinks`, `LinkSiigoVariant`, `UnlinkSiigoVariant`, `AutoLinkSiigoVariantsByCode`) y `SiigoVariantLinkDto.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoProductLinksApiTests.cs`, `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoCodeMatchTests.cs`

**Interfaces:**
- Produces:
  - `ISiigoVariantCatalog.SearchAsync(tenantId, string? search, int skip, int take) : SiigoPage<SiigoVariantRef>`
    (`VariantId`, `DisplayName`, `Code`, `IsActive`), `CountActiveAsync(tenantId) : int`, `ListAllActiveCodesAsync(tenantId) : IReadOnlyList<SiigoVariantRef>`.
    Adaptador sobre `IProductRepository` (`ListForExportAsync`/`SearchAsync`) y `CatalogVariantReads.DisplayNameOf`.
  - `SiigoCodeMatch.Normalize(string? code) : string?` (sin espacios, sin distinguir mayúsculas).
- Endpoints (bajo `…/siigo/connections/{connectionId}`, `siigo.settings.manage`):
  - `GET products?search=&filter=all|unlinked|linked&page=` → filas `{ variantId, displayName, code, isActive, siigoProduct?: { id, code, name }, origin? }`
    más `coverage { linked, totalActive }`.
  - `PUT products/{variantId}/link` `{ siigoProductId, code, name }`; `DELETE products/{variantId}/link`.
  - `POST products/auto-link` → recorre las páginas de `SearchProductsAsync` de Siigo, empareja por `SiigoCodeMatch` sólo
    variantes **sin** vínculo y productos de Siigo **activos**; devuelve `{ linked, skippedAmbiguous }` (dos productos de
    Siigo con el mismo código normalizado no se vinculan).

- [ ] **Step 1: Pruebas que fallan:** `SiigoCodeMatchTests` (`" ab-1 "` y `"AB-1"` casan; `null` no casa nada);
  `AutoLinkLinksOnlyUnlinkedVariantsToActiveProducts`; `AutoLinkSkipsAmbiguousCodes`; `ManualLinkReplacesAnAutoLink`;
  `CoverageCountsLinkedOverActiveVariants`; `TheUnlinkedFilterShowsOnlyVariantsWithoutLink`.
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar.** El auto-vínculo escribe en una sola transacción y sube `Version` una vez.
- [ ] **Step 4: Correr** unitarias por `SiigoCodeMatchTests` e integración por `SiigoProductLinksApiTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): link qep variants to siigo products`

> **Cierre del Bloque B:** clases completas `SiigoSettingsApiTests` y `SiigoProductLinksApiTests`, una revisión del bloque.

---

## Bloque C — Facturación (Tareas 8–10)

### Tarea 8: Armado de la factura (código puro)

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBuilder.cs`, `SiigoInvoiceInput.cs`, `SiigoCustomerMapper.cs`, `SiigoIdentificationTypes.cs`, `SiigoWithholdingPlacement.cs`, `SiigoMissing.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceBuilderTests.cs`, `SiigoCustomerMapperTests.cs`

**Interfaces:**
- Consumes: `SiigoSettings` y vínculos (Tarea 4); el contrato de Quotations `ExternalInvoicingOrder` (Tarea 9; aquí se
  usa su espejo `SiigoInvoiceInput`, que el adaptador llena).
- Produces:
  - `SiigoInvoiceInput`: `OrderId`, `OrderNumber`, `Currency`, `Today` (`DateOnly`, zona del tenant), `CashOnDelivery`,
    `Lines` (`VariantId`, `Name`, `Quantity`, `Subtotal`, `DiscountPercentage`, `TaxPercentage`, `TaxAmount`),
    `FreightSubtotal`, `FreightTaxAmount`, `FreightTaxPercentage`, `ProductsTotal`, `Total`, `RetentionAmount`, `NetTotal`,
    `Withholdings` (`TaxRateId`, `Type`, `Amount`), `PaymentProofs` (`ProofId`, `Amount`), `BillingAccountKey?`,
    `BuyerNotes?`, `BilledParty` (`IsFinalConsumer`, `PersonType`, `IdentificationType`, `Identification`, `Name`,
    `Address`, `CityCode?`, `StateCode?`, `Email?`, `Phone?`).
  - `SiigoInvoiceBuilder.Build(SiigoInvoiceInput input, SiigoSettings settings, IReadOnlyList<SiigoPaymentOverride>? overrides) : SiigoBuildResult`
    — `Ready(IReadOnlyList<SiigoInvoiceDraft> Drafts)` o `NotReady(IReadOnlyList<SiigoMissing> Missing)` o
    `TotalsMismatch(decimal QepTotal, decimal ComputedTotal)`. `SiigoInvoiceDraft(SiigoInvoiceKind Kind, SiigoInvoiceRequest Request, SiigoCustomerRequest? NewCustomer)`.
    `SiigoPaymentOverride(SiigoInvoiceKind Kind, Guid ProofId, string SiigoPaymentTypeId)`.
  - `SiigoMissing(string Code, string? Reference, string? Label)` con códigos: `settings_incomplete` (Reference = paso),
    `variant_unlinked` (variantId, nombre), `tax_unlinked` (porcentaje), `withholding_unlinked` (taxRateId, nombre),
    `withholding_type_unsupported`, `payment_unlinked` (clave de la cuenta), `freight_product_missing`,
    `customer_identification_missing`, `customer_identification_type_unsupported`, `customer_city_missing`,
    `currency_unsupported`.
  - `SiigoIdentificationTypes.CodeFor(PartyIdentificationType) : string?` — `Cc`→`13`, `Nit`→`31`, `Ce`→`22`,
    `Pasaporte`→`41`; `Nif`, `Nie`, `TaxId` → `null` (faltante). (TI, PEP y RC no existen en `PartyIdentificationType`.)
  - `SiigoWithholdingPlacement.For(string qepType) : SiigoWithholdingTarget { ItemTax, Invoice, Unsupported }` —
    ReteFuente → `ItemTax`, ReteICA → `Invoice`, cualquier otro → `Unsupported`, usando las constantes de tipo que ya usa
    `DocumentWithholding.Type` (el implementador las toma de `BuildingBlocks.Domain.Taxes`, no escribe literales nuevos).

Reglas que fija el plan (§6.3–6.4):
- Ítem: `code` del vínculo; `description` = nombre de la línea; `quantity`; `price` = `Subtotal / Quantity` **antes**
  del descuento, redondeado a 6 decimales, y `discount` = `DiscountPercentage`; `taxes` = `[{ id: vínculo IVA }]` (sin
  `taxes` si la tasa es 0 y no hay vínculo del 0) más `{ id }` de cada ReteFuente aplicada; `warehouse` si la
  configuración lo trae. (`price` contra `taxed_price` es V-2: si la primera factura real no cuadra, aquí se cambia.)
- ReteICA aplicadas → `retentions: [{ id }]`. Un tipo `Unsupported` aplicado es faltante.
- Flete con `FreightSubtotal > 0`: ítem `freight_product_code`, cantidad 1, `price` = `FreightSubtotal`, su IVA. Con
  `FreightSeparateInvoice` sale como segundo borrador `Freight` sin retenciones (§6.3).
- Pagos: cada comprobante es un pago con la forma del vínculo de `BillingAccountKey` (o `DefaultPaymentTypeId` si el
  pedido no tiene cuenta), o la del `override`. Saldo = neto − Σ comprobantes: con `CashOnDelivery` va a
  `CashOnDeliveryPaymentTypeId`; si no, a `CreditPaymentTypeId` con `due_date = Today + CreditDays`. Con dos borradores
  los comprobantes llenan primero el de pedido. Comprobantes por encima del neto → `422 siigo.invoice.payments_exceed_total`
  (excepción de dominio, no faltante). Un override de un `ProofId` que no es del pedido → `422 siigo.invoice.payments_mismatch`.
- `date` = `Today`; `observations` = `"Pedido {OrderNumber}"` + `BuyerNotes` (recortado a 4.000);
  `additional_fields.purchase_order.number` = `OrderNumber`; `stamp.send` = `SendToDian`; `mail.send` = `SendMail`.
- Cuadre previo: Σ(subtotal con descuento + IVA) de los ítems del borrador de pedido contra `ProductsTotal` (más el flete
  si va junto); diferencia > 1 → `TotalsMismatch`. El neto que suman los pagos es `NetTotal` (o su parte por factura).
- Cliente (`SiigoCustomerMapper.Map(BilledParty) : SiigoCustomerRequest`): `person_type` `Company` si
  `PartyPersonType.Juridica`, si no `Person`; `name` = `[razón social]` para empresa, y para persona el primer token como
  nombres y el resto como apellidos (un solo token: se repite en los dos; confirmar con V-6); NIT sin lo que va después
  del guion y sólo dígitos; consumidor final con identificación `222222222222` y `id_type` `13` (V-3).

- [ ] **Step 1: Pruebas que fallan** (casos con números reales anonimizados; cada aserción con el valor exacto):
  - `ALineWithVatBecomesAnItemWithItsVatTax`; `AZeroRateLineWithoutLinkHasNoTaxes`.
  - `ReteFuenteGoesInsideEveryItemAndReteIcaAtInvoiceLevel`.
  - `AnUnsupportedWithholdingTypeIsMissing`; `AnUnlinkedVariantIsMissingWithItsName`; `AllMissingAreReportedTogether`.
  - `FreightGoesAsAnItemByDefault`; `FreightGoesInItsOwnInvoiceWithoutRetentionsWhenConfigured`.
  - `EachProofIsAPaymentWithTheAccountPaymentType`; `TheBalanceGoesToCashOnDeliveryOrCredit` (con `due_date` exacto);
    `ProofsFillTheOrderInvoiceBeforeTheFreightInvoice`; `ProofsAboveTheNetAreRejected`; `AnOverrideChangesOneProofPaymentType`.
  - `ARenamedBankAccountIsReportedAsUnlinked` (Review Focus 3).
  - `AnItemsTotalOffByMoreThanOnePesoIsATotalsMismatch`; `ADifferenceOfOnePesoPasses`.
  - `StampSendFollowsTheConnectionSwitch`.
  - `SiigoCustomerMapperTests`: `ANitIsSentWithoutCheckDigitAndOnlyDigits` (Review Focus 2), `ACompanyNameIsASingleElement`,
    `APersonNameSplitsFirstTokenFromTheRest`, `AFinalConsumerUsesTheDianGenericDocument`, `ANifIsUnsupported`.
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar** el armador y el mapeador según las reglas.
- [ ] **Step 4: Correr** `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceBuilderTests|FullyQualifiedName~SiigoCustomerMapperTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): build the siigo invoice from an order`

### Tarea 9: Quotations — marca por proveedor y puerto de facturación externa

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`, `OrderStateErrors.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs`, `OrderMapping.cs`, `OrderResponseComposer.cs`, `RevertOrderInvoicing.cs`, `InvoiceOrder.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs`, `ExternalOrderInvoicingService.cs`, `IExternalInvoiceStatus.cs`
- Create: migración `AddOrderInvoiceProvider` (Quotations)
- Create: `src/Bootstrapper/SiigoOrderSource.cs` (adaptador del puerto de Siigo sobre `IExternalOrderInvoicing`), `src/Bootstrapper/SiigoOpenInvoiceCheck.cs` (SQL sobre `siigo.invoices`, llega con la Tarea 10)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderExternalInvoicingTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExternalInvoicingApiTests.cs`

**Interfaces:**
- Produces:
  - Columnas nullable en `quotations.orders`: `invoice_provider` (máx. 16), `invoice_number` (máx. 40), `invoice_external_id` (máx. 64).
  - `Order.InvoiceExternally(string provider, string number, string externalId, MemberId invokedBy, DateTimeOffset at)`:
    exige no anulado y no facturado; **no** exige pagado (§5.5); si ya está facturado con el mismo proveedor y número, no
    hace nada; con otra marca lanza `order.order.already_invoiced`. Sube `Version`.
  - `Order.RevertInvoicing` con `InvoiceProvider != null` → `422 order.order.invoiced_externally`.
  - `OrderDto` gana `InvoiceProvider`, `InvoiceNumber` (con el motivo BFF anotado en el record).
  - `IExternalOrderInvoicing` (Quotations, público): `GetForInvoicingAsync(Guid tenantId, Guid orderId, ct) : ExternalInvoicingOrder?`
    (los mismos campos que `SiigoInvoiceInput`, con la parte facturada resuelta por `OrderBilledParty.SourceFor` y
    `IQuotationCustomerLookup`, y `Version`) y `MarkInvoicedAsync(Guid tenantId, Guid orderId, string provider, string number, string externalId, Guid memberId, ct)`
    (historial `Invoiced`, auditoría `quotation.order.invoiced` con `provider` en `changedFields`, un reintento ante
    `RequestConcurrencyException`).
  - `IExternalInvoiceStatus.HasOpenInvoiceAsync(Guid tenantId, Guid orderId, ct) : bool` (Quotations declara; Bootstrapper implementa).
    `InvoiceOrderHandler` responde `409 order.order.external_invoice_pending` si es verdadero.
  - Siigo: `ISiigoOrderSource.GetAsync(tenantId, orderId) : SiigoInvoiceInput?` y `MarkInvoicedAsync(...)`, adaptador
    `SiigoOrderSource` que traduce `ExternalInvoicingOrder` a `SiigoInvoiceInput` y la ciudad a códigos con un
    `ISiigoGeographyLookup` sobre `City.DivipolaCode`/`Department.DivipolaCode` (como `CompanyGeographyLookup`).

- [ ] **Step 1: Pruebas que fallan:**
  - Unitarias: `InvoiceExternallyDoesNotRequirePayment`; `InvoiceExternallyIsIdempotentForTheSameNumber`;
    `InvoiceExternallyOverAManualMarkFails`; `RevertingAnExternallyInvoicedOrderIs422`.
  - Integración: `TheOrderDtoShowsProviderAndNumber`; `ManualInvoicingWithAnOpenSiigoInvoiceIs409` (con un doble de
    `IExternalInvoiceStatus` que devuelve verdadero; el adaptador real se prueba en la Tarea 10);
    `GetForInvoicingResolvesTheBilledPartyFromTheCustomer`.
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar** y generar `AddOrderInvoiceProvider` (aditiva). Mientras no exista la tabla de la Tarea 10,
  `SiigoOpenInvoiceCheck` devuelve `false` sin consultar; la Tarea 10 pone el SQL.
- [ ] **Step 4: Correr** unitarias por `OrderExternalInvoicingTests` e integración por `OrderExternalInvoicingApiTests`
  y por `InvoiceOrder` (las existentes de la marca manual no cambian). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(quotations): external invoicing mark and port`

### Tarea 10: Emitir, retomar y resolver la factura

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoInvoice.cs`, `SiigoInvoiceStatus.cs`, `SiigoInvoiceKind.cs`, `SiigoIdempotencyKey.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/` (`PreviewSiigoInvoice`, `IssueSiigoInvoice`, `GetSiigoInvoices`, `StampSiigoInvoice`, `ResolveSiigoInvoice`) y `SiigoInvoiceDto.cs`
- Create: migración `AddSiigoInvoices`; Modify: `src/Bootstrapper/SiigoOpenInvoiceCheck.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoIdempotencyKeyTests.cs`, `SiigoInvoiceTests.cs`; `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceApiTests.cs`

**Interfaces:**
- Consumes: `ISiigoApi` (5), `ISiigoConnectionDirectory` (6), `SiigoInvoiceBuilder` (8), `ISiigoOrderSource` (9), `ISiigoUnitOfWork.LockOrderAsync` (3).
- Produces:
  - Tabla `siigo.invoices` con las columnas de §5.6 (incluido `kind` y `order_version`, `order_changed`), índice
    `IX_invoices_tenant_order_kind` único parcial `(tenant_id, order_id, kind) WHERE status NOT IN ('Rejected','Failed')`
    traducido por nombre a `409 siigo.invoice.already_exists`.
  - `SiigoInvoiceStatus { Pending, Created, Stamped, StampRejected, Rejected, Unknown, Failed }`, `SiigoInvoiceKind { Order, Freight }`,
    persistidos como texto.
  - `SiigoIdempotencyKey.For(Guid rowId) : string` — base32 RFC 4648 en minúsculas sin relleno de los 16 bytes (26
    caracteres, alfanumérico, ≤ 30).
  - `SiigoInvoice.PendingFor(...)`, `MarkCreated(id, name, total, cufe?)`, `MarkRejected(errors)`, `MarkStampRejected(errors)`,
    `MarkStamped(cufe?)`, `ExpireIfStale(DateTimeOffset now)` (`Pending` de más de 24 h → `Unknown`), `ResolveExists(number)`, `ResolveMissing()`.
  - Endpoints (`/api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice…`, `siigo.invoice.issue`):
    - `GET …/preview` → `{ drafts: [{ kind, total, payments }], missing: [...], totalsMismatch? }` sin escribir nada.
    - `POST …` `{ payments?: [{ kind, proofId, siigoPaymentTypeId }] }` → 200 con las filas (`SiigoInvoiceDto`: `kind`,
      `status`, `number`, `cufe`, `siigoTotal`, `errorCode`, `errorDetail`, `expectedPaymentsTotal`, `orderChanged`); los
      422 de validación son sólo los previos al envío (`not_ready` con `details.missing`, `totals_mismatch`,
      `payments_exceed_total`, `payments_mismatch`, `currency_unsupported`, `no_company`, `no_connection`).
    - `GET …` → filas vigentes por `kind` o 404. `POST …/stamp?kind=` → reintenta `/stamp`. `POST …/resolve`
      `{ kind, outcome: "exists"|"missing", number? }` sólo desde `Unknown`.
  - Auditoría `siigo.invoice.created`, `siigo.invoice.rejected`, `siigo.invoice.resolved` (sin datos del cliente).

Orden del `POST` (§5.4), que fija el plan: transacción + `LockOrderAsync` → leer filas vigentes y aplicar la tabla del
paso 1 de §5.4 (`ExpireIfStale` primero) → `ISiigoOrderSource.GetAsync` → validaciones de §6.1 → `Build` → **crear el
cliente en Siigo si no existe** (`FindCustomerAsync`, luego `CreateCustomerAsync`) → guardar las filas `Pending` con
`request_json` → commit → `CreateInvoiceAsync` por borrador (primero `Order`, después `Freight`) → aplicar el resultado
en una transacción corta → `MarkInvoicedAsync` cuando el borrador `Order` queda `Created`/`Stamped`. Un
`Rejected` con `invalid_total_payments` guarda `error_code = "siigo.invoice.totals_mismatch"` y `expectedPaymentsTotal`.
Retomar una `Pending` reenvía `request_json` con su clave, sin rearmar; si la conexión no está disponible responde
`409 siigo.connection_unavailable` sin tocar la fila. `SiigoOpenInvoiceCheck` (Bootstrapper) pasa a consultar
`siigo.invoices` (`status IN ('Pending','Unknown')`).

- [ ] **Step 1: Pruebas que fallan:**
  - Unitarias: `SiigoIdempotencyKeyTests.TheKeyIsAlphanumericAndAtMostThirtyChars`, `TheSameRowAlwaysGivesTheSameKey`;
    `SiigoInvoiceTests.APendingOlderThanADayBecomesUnknown`, `OnlyAnUnknownCanBeResolved`.
  - Integración (`SiigoInvoiceApiTests`, con `FakeSiigoApi`):
    - `AReadyOrderIsCreatedAndMarkedInvoicedWithTheSiigoNumber` (pedido con `invoiceProvider = "siigo"`, `invoiceNumber = "FV-1-123"`).
    - `WithDianOffStampSendIsFalse` y `WithDianOnStampSendIsTrue` (lee el cuerpo recibido por el fake).
    - `AnUnreadyOrderIs422WithAllMissingAndNothingReachesSiigo`.
    - `InvalidTotalPaymentsLeavesItRejectedWithTheExpectedTotalAndTheOrderUnmarked`; luego un segundo `POST` crea otra fila.
    - `ATimeoutLeavesItPendingAndTheRetrySendsTheSameBytesAndKey`.
    - `RetryingAPendingInvoiceWithoutConnectionLeavesItPending` (Review Focus 5).
    - `TwoConcurrentIssuesCreateOneInvoice` (Review Focus 1: dos `POST` en paralelo, un solo `POST /v1/invoices` en el fake).
    - `AMissingCustomerIsCreatedBeforeTheInvoiceAndAnExistingOneIsNot`.
    - `WithFreightSeparateTwoInvoicesAreCreatedAndAFreightRejectionKeepsTheOrderMarked`.
    - `AnUnknownResolvedAsMissingFreesTheOrder`; `StampRetryMovesCreatedToStamped`.
    - `TheRealStatusAdapterSeesOpenInvoices` (manual `POST …/invoice` → 409 con una fila `Pending` real).
- [ ] **Step 2: Ver el RED de compilación.**
- [ ] **Step 3: Implementar** según el orden fijado. Migración `AddSiigoInvoices` (aditiva).
- [ ] **Step 4: Correr** unitarias por `SiigoIdempotencyKeyTests|SiigoInvoiceTests` e integración por `SiigoInvoiceApiTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(siigo): issue, retry and resolve siigo invoices`

> **Cierre del Bloque C:** clases completas `SiigoInvoiceApiTests` y `OrderExternalInvoicingApiTests`, una revisión del bloque.

---

## Bloque D — Cierre (Tarea 11)

### Tarea 11: Fugas, despliegue y documentación

**Files:**
- Create: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoNoLeakTests.cs`
- Modify: `k8s/prod-configMap.yaml` (`Siigo__PartnerId: "#{SIIGO_PARTNER_ID}#"`), `README.md` (§ nueva «Integración con Siigo»: conexión, asistente, reglas de §5.4 y §6.4, despliegue y la tabla de V-1…V-8 como lo que se mira en las primeras facturas reales), `CLAUDE.md` (gotchas: `siigo.invoices` es registro legal sin purga; `send_to_dian` nace apagado; ReteFuente por ítem y ReteICA por factura; no reintentar con el total que propone Siigo)
- Test: `SiigoNoLeakTests`

**Interfaces:**
- Consumes: todo lo anterior.

- [ ] **Step 1: Prueba que falla:** `TheAccessKeyTokenAndCustomerDataNeverReachLogsOrAudit` — host en `Trace` con
  `RecordingLoggerProvider` (el de `ShopifyApiHarness`), un ciclo completo (probar conexión, configurar, facturar,
  rechazo); la clave, el token del fake, el documento y la dirección del cliente no aparecen en ningún log ni en
  `audit.entries`.
- [ ] **Step 2: Correr y verla fallar** (si pasa a la primera, romper a propósito `RemoveAllLoggers()` y verla fallar; anotarlo).
- [ ] **Step 3: Ajustar** lo que la prueba encuentre y escribir la documentación de **Files**.
- [ ] **Step 4: Suite de los módulos tocados** (una sola vez, en fila, `-- xUnit.MaxParallelThreads=4`): Siigo
  (unitarias e integración), Integrations, Tenancy, Quotations y `ArchitectureTests`. Expected: PASS; si algo falla, se
  reporta con su salida.
- [ ] **Step 5: Commit** — `docs(siigo): readme, deployment and no-leak test`

> **Cierre:** revisión fuerte de toda la rama y entrega para merge a `develop` (lo pide el owner).
