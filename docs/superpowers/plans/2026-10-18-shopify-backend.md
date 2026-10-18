# Integración con Shopify (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que los pedidos de una tienda Shopify enlazada entren solos a QEP como pedidos `Source = Shopify`
(cliente completo o incompleto, líneas, envío, contraentrega), con bandeja de espera, conciliación y
sincronización manual.

**Architecture:** Módulo nuevo `Shopify` (Domain/Application/Infrastructure/Api, esquema `shopify`) que guarda
el espejo de pedidos, recibe el webhook, concilia por GraphQL e importa; Integrations hace el OAuth, la
instalación, el enlace y el registro de webhooks; Quotations, Customers y Tenancy ganan lo mínimo. Todo lo que
Shopify necesita de otro módulo entra por puertos propios con adaptador en `src/Bootstrapper`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core + Npgsql (PostgreSQL 18), FluentValidation,
xUnit v3 + Testcontainers, `IHttpClientFactory` con `HttpMessageHandler` falso en pruebas.

**Spec:** `docs/superpowers/specs/2026-10-15-integracion-shopify-pedidos-design.md` (con el delta del flete
del 2026-10-18). Se leen juntos: los §, H#, D-S# y V-# de este plan son de ese spec.

## Global Constraints

- Rama `feature/shopify` del worktree `.claude/worktrees/shopify`. Antes de **cada** commit:
  `git branch --show-current` debe decir `feature/shopify`. Nunca `main`, nunca merge a `main`, nunca `git stash`.
- Commits en español, conventional commits, **sin atribución de IA** (sin `Co-Authored-By`).
- TDD obligatorio: RED con la salida literal de la falla antes del GREEN. Pruebas **por proyecto tocado**,
  nunca la solución entera: `dotnet test <proyecto.csproj> --filter "<filtro>" --blame-hang-timeout 5m`.
- `Api.exe`/`Api` corriendo bloquea `build`, `test` y `ef`: detenerlo antes.
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política del permiso, **y** el handler
  revalida tenant y permiso antes del repositorio (`<Modulo>Authorization.EnsureAuthorized`); otro tenant → 403.
  Todo método de repositorio recibe `tenantId`.
- Sin permisos nuevos (D-S14): `integrations.connection.read|manage`, `quotations.order.read|manage`,
  `tenancy.settings.update`. Si algún paso necesitara uno, son **dos mitades**: constante en
  `<Modulo>Permissions` y política en `AddAuthorization` (`QepServiceCollectionExtensions`); sin la política el
  síntoma es 500.
- Rutas de `/shopify/*` y `/integrations/shopify/*` por tenant: `TenantModuleGuard.EnsureEnabledAsync(…,
  TenantModuleKeys.Shopify)` → 403 `tenancy.module_not_enabled`.
- Texto libre en un comando → validador de FluentValidation (422 `validation.failed` con `errors`) aunque el
  dominio valide. Ningún status HTTP a mano: el mapeo es `src/Api/ApiExceptionHandler.cs`.
- Errores de base se traducen en Infrastructure **por nombre de índice**, no sólo por `23505`.
- Auditoría y outbox en la misma transacción que el cambio. Shopify tiene su propio `IShopifyAuditRecorder`
  sobre `audit.entries` proyectada en `ShopifyDbContext` (`ExcludeFromMigrations`), como
  `MessagingAuditRecorder`; nunca el `IAuditRecorder` ligado a `TenancyDbContext`.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- Un `FromSql` crudo lista **todas** las columnas mapeadas: al agregar columnas a `quotations.orders`,
  `grep -rn "FromSql" src/Modules/Quotations` y extender los que lean esa tabla.
- `Directory.Packages.props` no se toca; si un paso agrega paquete, `dotnet restore --force-evaluate` y los
  `packages.lock.json` van en el mismo commit. Cada `.csproj` nuevo trae su `packages.lock.json`.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma; colecciones de tamaño fijo completas en
  cero (`summary`); enums por nombre.
- Textos al usuario y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual.
- Secretos: el client secret y los tokens nunca en DTO, log, auditoría ni salida de consola;
  `dotnet user-secrets list` sólo contando (`| Select-String … | Measure-Object`).
- `.mcp.json`, `Directory.Packages.props`, `global.json`: no se tocan.

## Review Focus

1. **El mismo pedido llega dos veces a la vez** (webhook `orders/create` y la conciliación en paralelo, o el
   worker cae entre crear el pedido y marcar el espejo): debe existir un solo pedido. Prueba en Task 2
   (`CreateAsync_WithSameExternalKey_ReturnsExistingOrder`) y en Task 17
   (`Reimport_AfterMirrorRowDeleted_DoesNotDuplicateOrder`).
2. **Cuerpo del webhook con firma válida pero JSON inesperado** (campos nulos, `line_items` vacío, `sku`
   nulo, `customer: null`): la entrega no se queda en reintentos eternos ni tumba el worker; termina procesada
   con `last_error` o la fila `Waiting` con `product_unmatched`. Pruebas en Task 10
   (`Parse_OrderWithoutCustomerAndNullSku_ProducesSnapshot`) y Task 11 (`Worker_MalformedPayload_MarksProcessedWithError`).
3. **Tienda desinstalada y reinstalada en otro tenant** antes de `shop/redact`: la ruta vieja bloquea
   `shop_already_linked` con el nombre del otro tenant, y el 403/422 no revela más. Prueba en Task 8
   (`Link_ShopLinkedToOtherTenant_Returns422WithTenantName`).
4. **Precio de Shopify con decimales en COP** (`19999.50`, impuestos no incluidos): el pedido no da 500 por
   `freight_amount_invalid`/pesos enteros; las líneas y el envío se redondean como dice §6.2.2. Prueba en Task 14
   (`Shipping_CopChargedWithCents_RoundsToWholePesos`).
5. **`state` de OAuth reutilizado o canje repetido por doble clic del callback**: el segundo `complete` da
   `install_not_found` y no crea una segunda fila ni consume otro `code`. Prueba en Task 7
   (`Complete_SameStateTwice_SecondReturnsInstallNotFound`).

---

## File Structure

```text
src/Modules/Shopify/
  Modules.Shopify.Domain/        ShopifyStore.cs, ShopifyOrderMirror.cs, ShopifyImportState.cs,
                                 ShopifyWaitReasons.cs, ShopifyWarnings.cs, ShopifyDomainException.cs
  Modules.Shopify.Application/   ShopifyPorts.cs (todos los puertos hacia otros módulos),
                                 ShopifyOrderSnapshot.cs, ShopifyOrderImporter.cs, ShopifyShippingMapper.cs,
                                 ShopifyCustomerResolver.cs, ColombiaProvinceCodes.cs, ShopifyCashOnDeliveryGateways.cs,
                                 ReconcileStore.cs, ShopifyStores.cs (GET/PUT tienda), ShopifyOrders.cs (lista,
                                 summary, retry, review), ShopifyWebhookTopics.cs, ShopifyDtos.cs,
                                 ShopifyAuthorization.cs, IShopifyUnitOfWork.cs, IShopifyAuditRecorder.cs
  Modules.Shopify.Infrastructure/ Persistence/{ShopifyDbContext,ShopifyDbContextFactory,ShopifyUnitOfWork,
                                 ShopifyAuditRecorder,WebhookDeliveries,Migrations}, Parsing/{RestOrderParser,
                                 GraphQlOrderParser}, Admin/ShopifyGraphQlClient.cs,
                                 Workers/{ShopifyWebhookDeliveryWorker,ShopifyReconcileWorker},
                                 ShopifyWebhookOptions.cs, ShopifyInfrastructureExtensions.cs
  Modules.Shopify.Api/           ShopifyEndpoints.cs, ShopifyWebhookEndpoints.cs
src/Modules/Integrations/…       Shopify/ (proveedor, instalaciones, OAuth, enlace, gateway)
src/Bootstrapper/Shopify*.cs     adaptadores de los puertos de Shopify
tests/Modules/Shopify/Modules.Shopify.{UnitTests,IntegrationTests}
tests/ArchitectureTests/ArchitectureTests/ShopifyLayerTests.cs
```

Shorthand en los comandos: `QU` = `tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj`,
`QI` = `…/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj`, y así `CU`/`CI`
(Customers), `IU`/`II` (Integrations), `TU`/`TI` (Tenancy), `SU`/`SI` (Shopify), `BU` =
`tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj`, `AT` =
`tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`. Todo `dotnet test` lleva
`--blame-hang-timeout 5m`.

**Necesitan Shopify real para cerrarse** (se construyen con fakes y pruebas de contrato; la verificación
manual está en la Task 17 y en el handoff): Tasks 7, 8, 10, 11 y 15 → V-1, V-2, V-3, V-4, V-5, V-6, V-7, V-8,
V-9, V-10.

---

### Task 1: Clave de módulo `shopify` en los módulos por defecto

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:36-71`
- Create: migración `AddShopifyModuleKey` en `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/` (copia de `20261013163342_AddPipelineModuleKey.cs`: reemplaza el `CHECK` de `tenant_modules.module_key`)
- Modify: `README.md:656,761` («seis» → «siete»), `CLAUDE.md` (gotcha de `Pos`: «concede seis módulos sin `pos`» → siete, con `shopify`; lista de módulos: dieciséis → diecisiete construidos más `Platform`, H26)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `TenantModuleDefaultsTests.cs`, `TenantRegistrationServiceTests.cs`

**Interfaces:**
- Produces: `TenantModuleKeys.Shopify` (`"shopify"`), último de `All`, dentro de `DefaultForNewTenants`, `DependenciesOf(Shopify) == [Orders]`.

- [ ] **Step 1: RED.** Renombrar `DefaultForNewTenantsIsEverythingButPosMessagingAndPipeline` a
  `DefaultForNewTenantsAreTheSixBaseModulesPlusShopify` con
  `Assert.Equal(["catalog","customers","companies","quotations","orders","reporting","shopify"], …)`; agregar
  `ShopifyIsLastAndDependsOnOrders` (`Assert.Same(TenantModuleKeys.Shopify, TenantModuleKeys.All[^1])`,
  `Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Shopify))`) y en
  `TenantRegistrationServiceTests` `RegisterOwnerTenant_GrantsShopify`.
- [ ] **Step 2:** `dotnet test TU --filter "FullyQualifiedName~TenantModule|FullyQualifiedName~TenantRegistrationService"` → FAIL (`Shopify` no existe).
- [ ] **Step 3:** Agregar la clave y su dependencia; generar la migración con el factory de diseño; el
  `CHECK` nuevo lista las diez claves en el orden de `All`. Sin backfill (D-S20).
- [ ] **Step 4:** Mismo comando → PASS; además `dotnet test TI --filter "FullyQualifiedName~TenantChangePersistence"` → PASS (la migración aplica).
- [ ] **Step 5:** Commit `feat(tenancy): módulo shopify en los módulos por defecto del signup`.

### Task 2: El camino externo recibe envío y es idempotente

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:362-398` (`CreateExternal`), propiedades nuevas
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs` y su implementación en `Modules.Quotations.Infrastructure/Persistence/`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:478-490,589` y `QuotationsUnitOfWork.cs` (índice nuevo)
- Create: migración `AddOrderExternalKey`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` (detalle: `shipping.externalShippingTitle`) y su composición (`OrderResponseComposer.cs`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` (o el archivo donde vivan las pruebas de `CreateExternal`), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExternalOrderServiceTests.cs`, `OrdersMigrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ExternalOrderDraft(…mismos 12…, ShippingDetails? Shipping = null,
      string? ExternalKey = null, string? ExternalShippingTitle = null);
  public sealed record ExternalOrderResult(Guid OrderId, string OrderNumber, decimal Total, decimal NetTotal,
      string Currency, bool AlreadyExisted = false);
  Order.CreateExternal(…, OrderSource source, string externalReference, ShippingDetails shipping,
      string? externalKey, string? externalShippingTitle)
  Order.ExternalKey (string?, ≤ 128), Order.ExternalShippingTitle (string?, ≤ 120)
  Task<Order?> IOrderRepository.FindByExternalKeyAsync(Guid tenantId, OrderSource source, string externalKey, CancellationToken)
  ```
  Código nuevo `order.order.external_key_taken` (traducción de `IX_orders_tenant_external_key`).

- [ ] **Step 1: RED unitario.** `CreateExternal_WithChargedCopShipping_AddsFreightToTotal` (envío `Charged`
  15000 al 19 % → `FreightSubtotal == 12605.04m`, `FreightTaxAmount == 2394.96m`, `Total == productos + 15000`);
  `CreateExternal_CashOnDeliveryWithPaidStatus_Throws` (código `order.order.cash_on_delivery_pending_only`);
  `CreateExternal_StorePickupWithShipping_Throws` (`order.order.shipping_not_allowed_on_pickup`);
  `CreateExternal_ExternalKeyTooLong_Throws` (`order.order.external_key_too_long`, 129 caracteres).
- [ ] **Step 2:** `dotnet test QU --filter "FullyQualifiedName~CreateExternal"` → FAIL (no compila: firma).
- [ ] **Step 3:** `CreateExternal` pasa `shipping` a `Build` (que ya llama `Validated` y
  `EnsureCashOnDeliveryIsPending`); normaliza `externalKey` (recorta, ≤ 128) y `externalShippingTitle`
  (recorta, ≤ 120, se trunca sin error: es un dato informativo de la tienda).
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración** en `ExternalOrderServiceTests`:
  `CreateAsync_WithShipping_PersistsShippingAndTotals`,
  `CreateAsync_WithSameExternalKey_ReturnsExistingOrder` (segunda llamada → `AlreadyExisted == true`, mismo
  `OrderId`, un solo `order.created` en el outbox, un solo historial),
  `CreateAsync_ConcurrentSameExternalKey_CreatesOneOrder` (dos `Task.WhenAll`; una gana, la otra devuelve el
  existente), `GetOrder_ExposesExternalShippingTitle`. En `OrdersMigrationTests`: pedidos viejos quedan con
  `external_key` y `external_shipping_title` nulos.
- [ ] **Step 6:** `dotnet test QI --filter "FullyQualifiedName~ExternalOrderService|FullyQualifiedName~OrdersMigration"` → FAIL.
- [ ] **Step 7:** Columnas `external_key varchar(128)` y `external_shipping_title varchar(120)`, índice único
  parcial `IX_orders_tenant_external_key (tenant_id, source, external_key) WHERE external_key IS NOT NULL`;
  `QuotationsUnitOfWork` lo traduce por nombre a `order.order.external_key_taken`. `ExternalOrderService.CreateAsync`:
  con clave, `FindByExternalKeyAsync` antes de numerar → si existe, devuelve su resultado con
  `AlreadyExisted: true`; si el `SaveChanges` lanza `order.order.external_key_taken`, deshace la transacción,
  vuelve a buscar en un `DbContext` limpio (`ChangeTracker.Clear()`) y devuelve el existente. Revisar
  `FromSql` sobre `quotations.orders`. El detalle agrega `ExternalShippingTitle` al objeto `shipping` con nota
  BFF (§6.2.5).
- [ ] **Step 8:** Mismo comando → PASS. Correr además `dotnet test QI --filter "FullyQualifiedName~Order"` → PASS (los `new ExternalOrderDraft(...)` existentes compilan por los opcionales).
- [ ] **Step 9:** Commit `feat(orders): envío e idempotencia en el pedido externo`.

### Task 3: «Reconocer este nombre»

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Carrier.cs` (`AddExternalName`)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/RecognizeOrderCarrier.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (ruta), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handler y validador)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/CarrierTests.cs`, `RecognizeOrderCarrierHandlerTests.cs` (nuevo), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderShippingApiTests.cs`

**Interfaces:**
- Consumes: `Order.ExternalShippingTitle` (Task 2), `Order.SetShipping`, `ICarrierRepository.FindAsync`.
- Produces: `void Carrier.AddExternalName(string name, DateTimeOffset occurredAt)`;
  `RecognizeOrderCarrierCommand(Guid TenantId, Guid OrderId, Guid CarrierId, bool RememberName) : ICommand<OrderDetailResponse>`;
  `POST /api/v1/tenants/{tenantId:guid}/orders/{orderId:guid}/carrier-recognition` con cuerpo
  `{ carrierId, rememberName }`, política `quotations.order.manage`. Códigos nuevos
  `order.order.carrier_recognition_unavailable`.

- [ ] **Step 1: RED.** `AddExternalName_NewName_AppendsAndBumpsVersion`; `AddExternalName_AlreadyOwnNormalized_IsNoOp`
  («COORDINADORA  express» sobre externo «Coordinadora Express» → `Version` igual); `AddExternalName_TwentyFirst_Throws`
  (`quotations.carrier.too_many_external_names`). Handler: `Recognize_WithoutSettingsUpdateAndRemember_Throws403`
  (`RequestForbiddenException`, sin tocar el repositorio), `Recognize_OrderNotPending_Throws` (`order.order.not_pending`),
  `Recognize_OrderWithoutExternalTitle_Throws` (`order.order.carrier_recognition_unavailable`),
  `Recognize_InactiveCarrier_Throws` (`order.order.carrier_not_available`),
  `Recognize_CashOnDeliveryWithNonCollectingCarrier_Throws` (`order.order.cash_on_delivery_not_supported`),
  `Recognize_Remember_AddsTitleAndKeepsFreight` (modo, valor, tasa y contraentrega iguales; carrier fijado).
- [ ] **Step 2:** `dotnet test QU --filter "FullyQualifiedName~AddExternalName|FullyQualifiedName~RecognizeOrderCarrier"` → FAIL.
- [ ] **Step 3:** `AddExternalName` reutiliza `Validate(Name, [..ExternalNames, name])`. El handler, en este
  orden: `EnsureAuthorized(…, OrderManage)`; si `RememberName`, también `TenancyPermissions.SettingsUpdate`;
  módulo `orders`; validador (`CarrierId` no vacío); carga pedido y transportadora; `SetShipping(current with
  { CarrierId, CarrierName })`; historial con `OrderChangeSummary` del cambio de envío y auditoría
  `quotation.order.shipping_changed`; con `RememberName`, `AddExternalName(order.ExternalShippingTitle)` y
  auditoría `quotations.carrier.updated`; una transacción; responde el detalle entero.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración** en `OrderShippingApiTests`: `CarrierRecognition_Remember_NextLookupMatches`
  (después `GET /carriers` trae el nombre en `externalNames`), `CarrierRecognition_NameOwnedByOtherCarrier_Returns422NameTaken`,
  `CarrierRecognition_OtherTenant_Returns403`. Los pedidos se siembran con `IExternalOrderWriter` resuelto del
  `Services` del harness.
- [ ] **Step 6:** `dotnet test QI --filter "FullyQualifiedName~OrderShippingApi"` → FAIL, implementar la ruta, → PASS.
- [ ] **Step 7:** Commit `feat(orders): reconocer el nombre de envío de una tienda`.

### Task 4: Customers crea compradores desde el sistema

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (`CreateIncompleteFromExternalSale`)
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs`
- Modify: `ICustomerRepository.cs` (+ implementación), `ICustomersAuditPublisher.cs` (+ `CustomersAuditPublisher.cs`), `CustomersDbContext.cs` (índice), migración `AddCustomerEmailIndex`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerProvisioningTests.cs` (nuevo), `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerProvisioningIntegrationTests.cs` (nuevo)

**Interfaces:**
- Produces:
  ```csharp
  Customer.CreateIncompleteFromExternalSale(CustomerId id, Guid tenantId, string name, string? phone,
      string? email, string? country, DateTimeOffset now, IPhoneNumberNormalizer phoneNormalizer)
  public sealed record ProvisionedCustomer(Guid Id, bool IsComplete, bool IsActive, bool Created);
  public sealed record CompleteCustomerData(string Name, string IdentificationType, string IdentificationNumber,
      string? Phone, string? Email, string Address, string Country, Guid? CityId, string? CityName, Guid ClassificationId);
  public interface ICustomerProvisioning {
      Task<ProvisionedCustomer> CreateCompleteAsync(Guid tenantId, CompleteCustomerData data, CancellationToken ct);
      Task<ProvisionedCustomer> CreateIncompleteAsync(Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken ct);
  }
  Task<CustomerId?> ICustomerRepository.FindIdByEmailAsync(Guid tenantId, string email, CancellationToken ct)
  void ICustomersAuditPublisher.PublishByIntegration(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
  ```

- [ ] **Step 1: RED.** `CreateIncompleteFromExternalSale_NoName_UsesShopifyDefault` (nombre `"Cliente de Shopify"`
  lo decide el llamador: el dominio exige no vacío → probar que vacío lanza y que con nombre queda
  `Incomplete`, `Cuc == null`, `PhoneE164` normalizado, `Email` recortado);
  `CreateComplete_IssuesCucAfterClassificationAndCity` (doble de `ICucGenerator` registra el orden de llamadas);
  `CreateComplete_IdentificationTakenRace_ReturnsExisting` (el unit of work falso lanza
  `customers.customer.identification_taken` → busca y devuelve `Created == false`);
  `CreateComplete_AuditsWithIntegrationActor`.
- [ ] **Step 2:** `dotnet test CU --filter "FullyQualifiedName~ExternalSale|FullyQualifiedName~CustomerProvisioning"` → FAIL.
- [ ] **Step 3:** `CustomerProvisioning` repite el orden de `CreateCustomerHandler` sin `IExecutionContext`
  (validar con `CustomerWriteRules` armando un `CreateCustomerCommand`, `WithRetention = false`,
  `VatSurplus = false`), y la auditoría `customers.customer.created` por `PublishByIntegration` con
  `AuditActorType.Integration`. Índice `IX_customers_tenant_email` sobre `(tenant_id, lower(email))` por SQL
  en la migración (EF no modela la expresión); `FindIdByEmailAsync` compara `lower(email) = lower(@email)`.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración:** `FindIdByEmail_IgnoresCaseAndOtherTenant`, `CreateComplete_PersistsWithCuc`,
  `CreateIncomplete_PersistsIncomplete`. `dotnet test CI --filter "FullyQualifiedName~CustomerProvisioning"` → FAIL → implementar → PASS.
- [ ] **Step 6:** Commit `feat(customers): alta de compradores desde una integración`.

### Task 5: Proveedor `shopify` y configuración de la app

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs:6-30` (`Commerce`, `ShopifyOAuth`), `IntegrationProviders.cs` (`ShopifyFieldKeys`, `Shopify`, `All`), `IntegrationsErrorCodes.cs:44-54` (`AppUninstalled = "app_uninstalled"`, `WebhooksNotRegistered = "webhooks_not_registered"`)
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAppOptions.cs`, `ShopifyAppOptionsValidator.cs`; `src/Modules/Integrations/Modules.Integrations.Application/Shopify/IShopifyAppSettings.cs`
- Create: migración `AddShopifyProvider` (CHECK de `provider_key`, copia de `20261014225009_AddInstagramProvider.cs`)
- Modify: `src/Api/appsettings.json` (`Shopify:App:ApiVersion = "2026-10"`, `Scopes = "read_orders,read_customers"`), `appsettings.example.json`, `appsettings.Development.json` (`AllowedRedirectOrigins: ["http://localhost:3002"]`), `GetIntegrationsCatalog.cs` (onboarding `ShopifyOAuth` sin `authorizeUrl`: lo arma el inicio A)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs`, `ShopifyAppOptionsValidatorTests.cs` (nuevo), `tests/ArchitectureTests/ArchitectureTests/ConfigurationExampleTests.cs`

**Interfaces:**
- Produces: `IntegrationProviders.Shopify` (clave `"shopify"`, `ConsumingModules [TenantModuleKeys.Shopify]`,
  `MaxConnections 20`, campos `shopDomain`/`shopName` públicos, `shopId`/`scopes`/`webhookSubscriptionIds`
  internos, `accessToken` secreto interno); `ShopifyFieldKeys` con esas seis constantes;
  `IShopifyAppSettings { string? ClientId; string? ClientSecret; string ApiVersion; IReadOnlyList<string> Scopes;
  bool IsRedirectAllowed(Uri); Uri? WebhookCallbackUrl; bool IsConfigured }`.

- [ ] **Step 1: RED.** `Shopify_IsVisibleOnlyWithShopifyModule`, `Shopify_AllFieldsAreBackendOwned`;
  validador: `Production_WithoutClientSecret_Fails`, `Production_HttpCallback_Fails`,
  `AnyEnvironment_ApiVersion2026_05_Fails` (forma `^\d{4}-(01|04|07|10)$`), `Development_Empty_Passes`.
- [ ] **Step 2:** `dotnet test IU --filter "FullyQualifiedName~IntegrationCatalog|FullyQualifiedName~ShopifyAppOptions"` → FAIL.
- [ ] **Step 3:** Implementar; registrar `ShopifyAppOptions` con `ValidateOnStart` en
  `IntegrationsInfrastructureExtensions`, como `MetaInstagramOptions`.
- [ ] **Step 4:** Mismo comando → PASS; `dotnet test AT --filter "FullyQualifiedName~ConfigurationExample"` → PASS;
  `dotnet test II --filter "FullyQualifiedName~IntegrationsPersistence"` → PASS (CHECK).
- [ ] **Step 5:** Commit `feat(integrations): proveedor shopify y configuración de la app`.

### Task 6: Instalación: inicios A y B

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/ShopifyInstall.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/{ShopifyHmac.cs,ShopifyShopDomain.cs,StartShopifyInstall.cs,IShopifyInstallRepository.cs}`
- Modify: `IntegrationsDbContext.cs` (tabla `integrations.shopify_installs`, índice único `IX_shopify_installs_state`), migración `AddShopifyInstalls`, repositorio en `Persistence/ShopifyInstallRepository.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Api/IntegrationsEndpoints.cs` (ruta por tenant) y un mapeo anónimo nuevo `MapShopifyInstallEndpoints(RateLimiterPolicies.Public)` llamado desde `src/Api/Program.cs` junto a `MapIntegrationsEndpoints`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ShopifyHmacTests.cs`, `StartShopifyInstallHandlerTests.cs`; `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ShopifyInstallApiTests.cs`

**Interfaces:**
- Produces: `static bool ShopifyHmac.VerifyQuery(IReadOnlyDictionary<string,string> query, string secret)`
  (todos menos `hmac`, ordenados ordinal, `k=v` con `&`, HMAC-SHA256 hex, `CryptographicOperations.FixedTimeEquals`);
  `static bool ShopifyHmac.VerifyBody(ReadOnlySpan<byte> body, string base64Signature, string secret)` (lo usa Task 11);
  `ShopifyShopDomain.TryNormalize(string?, out string)` (minúsculas, `^[a-z0-9][a-z0-9-]*\.myshopify\.com$`, ≤ 64);
  `ShopifyInstall` (`Id`, `ShopDomain`, `State`, `RedirectUri`, `InitiatedTenantId?`, `InitiatedBy?`, `ShopId?`,
  `ShopName?`, `Scopes?`, `ProtectedToken?`, `KeyId?`, `CreatedAt`, `ExpiresAt`; `CanBeExchanged(now)`,
  `MarkExchanged(...)` que mueve `ExpiresAt` a +7 días);
  comandos `StartShopifyInstallForTenantCommand(TenantId, Shop, RedirectUri)` y
  `StartShopifyInstallCommand(Shop, Hmac, Timestamp, Host, RedirectUri)` → `ShopifyAuthorizeResponse(string AuthorizeUrl)`.

- [ ] **Step 1: RED.** `VerifyQuery_KnownVector_True` (vector del ejemplo de la documentación de Shopify:
  `code=0907a61c0c8d55e99db179b68161bc00&shop=some-shop.myshopify.com&state=0.6784241404160823&timestamp=1337178173`
  con secreto `hush` → hmac `700e2dadb827fcc8609e9d5ce208b2e9cdaab9df07390d2cbca10d7c328fc4bf`), `VerifyQuery_Tampered_False`,
  `VerifyBody_KnownVector_True` (calculado en la prueba con `HMACSHA256` sobre bytes fijos); `TryNormalize`
  con `MI-Tienda.myshopify.com` → `mi-tienda.myshopify.com`, `evil.com`, 65 caracteres → false.
  Handlers: `StartForTenant_ModuleOff_Throws403`, `StartForTenant_RedirectNotAllowed_ThrowsValidation`
  (`errors["redirectUri"]`), `StartForTenant_BuildsAuthorizeUrlWithoutPerUser` (sin `grant_options[]`,
  `state` de 43 caracteres base64url, vence a +15 min), `StartAnonymous_BadHmac_Throws` (`integrations.shopify.hmac_invalid`).
- [ ] **Step 2:** `dotnet test IU --filter "FullyQualifiedName~ShopifyHmac|FullyQualifiedName~StartShopifyInstall"` → FAIL.
- [ ] **Step 3:** Implementar. `integrations.shopify.hmac_invalid` se lanza como `RequestUnauthorizedException`
  (ya mapeada a 401 en `src/Api/ApiExceptionHandler.cs:147`; prueba `StartB_BadHmac_Returns401` en el paso 5). `install_not_found` → `ResourceNotFoundException`; `install_tenant_mismatch` →
  `RequestForbiddenException`.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración:** `StartA_Returns200WithAuthorizeUrl_AndStoresRow`, `StartA_WithoutPermission_Returns403`,
  `StartB_ValidHmac_StoresRowWithoutTenant`, `StartB_BadHmac_Returns401`, `StartB_IsAnonymousAndWithoutCsrfHeader` (con el stub apagado,
  como el webhook). `dotnet test II --filter "FullyQualifiedName~ShopifyInstallApi"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(integrations): inicio de la instalación de Shopify`.

### Task 7: Canje del `code` y lectura de la instalación *(V-1, V-3, V-7: Shopify real)*

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/{IShopifyAdminGateway.cs,CompleteShopifyInstall.cs,GetShopifyInstall.cs}`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/ShopifyAdminGateway.cs` (cliente nombrado `IHttpClientFactory`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/CompleteShopifyInstallHandlerTests.cs`, `ShopifyAdminGatewayTests.cs`; `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/FakeShopifyHandler.cs`, `ShopifyInstallApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IShopifyAdminGateway {
      Task<ShopifyTokenGrant> ExchangeCodeAsync(string shop, string code, CancellationToken ct);   // AccessToken, Scopes
      Task<ShopifyShopInfo> GetShopAsync(string shop, string accessToken, CancellationToken ct);    // Id, Name, Domain
      Task<IReadOnlyList<ShopifyWebhookSubscription>> ListWebhooksAsync(string shop, string token, CancellationToken ct);
      Task<ShopifyWebhookSubscription> CreateWebhookAsync(string shop, string token, string topic, Uri callback, CancellationToken ct);
  }
  // Errores tipados: ShopifyGatewayException(Kind: Rejected | Unreachable | Invalid, int? Status)
  CompleteShopifyInstallCommand(Code, Shop, State, Hmac, Timestamp, RedirectUri) → ShopifyInstallCompletedResponse(
      Guid InstallId, string ShopDomain, string ShopName, DateTimeOffset ExpiresAt, Guid? InitiatedTenantId,
      LinkedTenantResponse? LinkedTenant)
  GetShopifyInstallQuery(Guid InstallId) → ShopifyInstallResponse(ShopDomain, ShopName, ExpiresAt,
      InitiatedTenantId, IReadOnlyList<ShopifyInstallTenantResponse> Tenants)   // TenantId, Name, CanLink, ModuleEnabled
  ```

- [ ] **Step 1: RED.** `Complete_UnknownState_ThrowsInstallNotFound`, `Complete_ExpiredRow_ThrowsInstallExpired`,
  `Complete_ShopMismatch_ThrowsInstallNotFound`, `Complete_ScopesMissing_Throws` (`integrations.shopify.scopes_missing`
  con concedido `read_orders`), `Complete_ExchangeRejected_ThrowsCodeExchangeFailed`,
  `Complete_Success_StoresProtectedTokenAndExtendsSevenDays` (`ISecretProtector` con AAD `installId + "accessToken"`;
  el token no aparece en `ToString()` del comando ni de la respuesta),
  `Complete_SameStateTwice_SecondReturnsInstallNotFound` (Review Focus 5; el gateway se llama una vez),
  `Complete_ExistingRoute_ReturnsLinkedTenant`. Gateway: `ExchangeCode_PostsClientIdSecretCode`,
  `GetShop_Sends_X_Shopify_Access_Token_ToGraphQlVersionedUrl`, `Status401_MapsToRejected`, `Status503_MapsToUnreachable`.
- [ ] **Step 2:** `dotnet test IU --filter "FullyQualifiedName~CompleteShopifyInstall|FullyQualifiedName~ShopifyAdminGateway"` → FAIL.
- [ ] **Step 3:** Implementar. `state` se consume dentro de la transacción con un `UPDATE … WHERE state = @s AND
  access_token_protected IS NULL` (o borrando el `state`): el segundo canje no encuentra fila. URL GraphQL:
  `https://{shop}/admin/api/{ApiVersion}/graphql.json`. `GetShopifyInstall`: sólo autenticación; 404 si no
  existe o venció; `CanLink` = el usuario tiene `integrations.connection.manage` en ese tenant (por
  `IMembershipDirectory` y el catálogo de roles que ya usa Integrations), `ModuleEnabled` = `ITenantModules`.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración** con `FakeShopifyHandler` (registrado sobre el cliente nombrado, como
  `FakeMetaGraphHandler`): `Complete_ThenGetInstall_ListsUserTenantsWithFlags`, `GetInstall_Unknown_Returns404`.
  `dotnet test II --filter "FullyQualifiedName~ShopifyInstallApi"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(integrations): canje del código de Shopify`.

### Task 8: Enlace, registro de webhooks, probador y barrido *(V-2: Shopify real)*

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/Shopify/{LinkShopifyStore.cs,ShopifyWebhookTopics.cs}`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Shopify/{ShopifyConnectionTester.cs,ShopifyInstallSweeper.cs}`
- Modify: `Verification/ConnectionTesterRegistry.cs`, `SecretProtection/ConnectionSecretRekeyWorker.cs` (incluye `shopify_installs`), `IntegrationsEndpoints.cs` (`POST /integrations/shopify/link`, `GET /integrations/shopify/installs/{installId}` y `POST /integrations/shopify/install/complete` si no quedaron en la Task 7)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/LinkShopifyStoreHandlerTests.cs`, `ShopifyConnectionTesterTests.cs`; `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ShopifyLinkApiTests.cs`, `ConnectionSecretLeakTests.cs`, `ConnectionSecretRekeyWorkerTests.cs`

**Interfaces:**
- Consumes: Tasks 5-7.
- Produces: `ShopifyWebhookTopics.Registered = ["orders/create","orders/updated","orders/paid","orders/cancelled","app/uninstalled"]`
  (en Integrations.Application, D-S19; GraphQL `ORDERS_CREATE`… lo traduce el gateway);
  `LinkShopifyStoreCommand(TenantId, InstallId, Name?) → ConnectionResponse` (201); la conexión guarda
  `webhookSubscriptionIds` separado por coma y la ruta `("shopify", shopDomain, null, tenantId, connectionId)`.

- [ ] **Step 1: RED.** `Link_ModuleOff_Throws403`, `Link_InstallFromOtherTenant_ThrowsTenantMismatch`,
  `Link_Expired_Throws`, `Link_LimitReached_Throws` (`integrations.connection.limit_reached` con 20),
  `Link_RegistersOnlyMissingTopics` (gateway con `orders/create` ya existente → 4 creaciones),
  `Link_WebhookRejected_LeavesNeedsAttentionWithCode` (`webhooks_not_registered`; la conexión se crea igual y
  el handler responde la conexión), `Link_SameTenantRelink_ReactivatesWithNewToken`,
  `Link_ShopLinkedToOtherTenant_Returns422WithTenantName` (Review Focus 3; `shop_already_linked`),
  `Link_DeletesInstallAndAuditsOnboardingVariant` (`onboarding:shopify_oauth` con `A` o `B`).
  Probador: `Test_200_RefreshesShopNameAndRecreatesMissingTopics`, `Test_401_And402_CredentialsRejected`,
  `Test_5xx_ProviderUnreachable`.
- [ ] **Step 2:** `dotnet test IU --filter "FullyQualifiedName~LinkShopifyStore|FullyQualifiedName~ShopifyConnectionTester"` → FAIL.
- [ ] **Step 3:** Implementar en una transacción de Integrations; la carrera de la ruta la cierra
  `IX_connection_routes_provider_external` (ya traducido). `ShopifyInstallSweeper`: `BackgroundService` cada
  hora que borra `expires_at < now`.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración:** `Link_CreatesConnectionRouteAndWebhooks`, `Link_OtherTenantSameShop_Returns422`;
  en `ConnectionSecretLeakTests` un caso con sentinela en el token de Shopify (ninguna respuesta, log ni
  auditoría lo contiene); en `ConnectionSecretRekeyWorkerTests`, `Rekey_IncludesShopifyInstalls`.
  `dotnet test II --filter "FullyQualifiedName~ShopifyLink|FullyQualifiedName~ConnectionSecretLeak|FullyQualifiedName~ConnectionSecretRekey"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(integrations): enlace de tiendas de Shopify y registro de webhooks`.

### Task 9: Esqueleto del módulo Shopify y su persistencia

**Files:**
- Create: los cuatro proyectos de `src/Modules/Shopify/` con sus `packages.lock.json`; `tests/Modules/Shopify/Modules.Shopify.UnitTests/` y `Modules.Shopify.IntegrationTests/` (copias de los `.csproj` de Pos); `Backend.slnx` (seis entradas)
- Create: `Modules.Shopify.Domain/{ShopifyStore.cs,ShopifyOrderMirror.cs,ShopifyImportState.cs,ShopifyWaitReasons.cs,ShopifyWarnings.cs,ShopifyDomainException.cs}`
- Create: `Modules.Shopify.Infrastructure/Persistence/{ShopifyDbContext.cs,ShopifyDbContextFactory.cs,ShopifyUnitOfWork.cs,ShopifyAuditRecorder.cs}`, `ShopifyDatabaseInitializer.cs`, `ShopifyInfrastructureExtensions.cs`, migración `InitialShopify`
- Modify: `src/Api/Api.csproj` (referencias a `Modules.Shopify.Api` e `.Infrastructure`), `src/Bootstrapper/QepServiceCollectionExtensions.cs:735` (`AddShopifyInfrastructure`)
- Create: `tests/ArchitectureTests/ArchitectureTests/ShopifyLayerTests.cs`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyOrderMirrorTests.cs`, `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyPersistenceTests.cs`

**Interfaces:**
- Produces:
  - `ShopifyImportState { Waiting, Imported, Cancelled, Ignored }` (texto en la base, `CHECK`).
  - `ShopifyWaitReasons` = las ocho constantes de §6.2 (`store_not_configured`, `advisor_inactive`,
    `billing_account_missing`, `currency_mismatch`, `customer_inactive`, `product_unmatched`,
    `freight_tax_rate_missing`, `order_rejected`); `ShopifyWarnings` = `customer_incomplete`,
    `shipping_address_unresolved`, `total_difference`, `carrier_unrecognized`, `cash_on_delivery_unmatched`.
  - `ShopifyStore` (§6.1; `IsConfigured => AdvisorMembershipId is not null && Billing is not null`;
    `DocumentAttributeKey = "Documento"`, `DocumentTypeAttributeKey = "Tipo de documento"`,
    `DefaultIdentificationType = "CC"` por defecto; `Version`).
  - `ShopifyOrderMirror` con `ApplySnapshot(snapshot json, shopifyUpdatedAt, …) : bool` (false si es más viejo),
    `MarkWaiting(reason, detailJson)`, `MarkImported(customerId, orderId, totalDifference, warningsJson)`,
    `AttachCustomer(Guid)`, `MarkIgnored()`, `MarkCancelled(detailJson)`, `ReviewWarnings(now)`; `Imported`
    nunca vuelve a `Waiting`.
  - Tablas de §9 y §5.4 (incluidas `last_error` y `warnings_reviewed_at`), índices de §9, único
    `IX_shopify_orders_connection_order`, único `IX_webhook_deliveries_webhook_id`.

- [ ] **Step 1: RED.** `ShopifyLayerTests` (copia de `PosLayerTests` + `ShopifyDoesNotReferenceOtherBusinessModules`:
  prohibido `Modules.Integrations.*`, `Modules.Quotations.*`, `Modules.Customers.*`, `Modules.Catalog.*`,
  `Modules.Geography.*`; permitido Tenancy y Audit); `ShopifyOrderMirrorTests`:
  `ApplySnapshot_Older_DoesNothing`, `Imported_CannotGoBackToWaiting`, `ReviewWarnings_WithoutWarnings_Throws`
  (`shopify.order.no_warnings`).
- [ ] **Step 2:** `dotnet test AT --filter "FullyQualifiedName~ShopifyLayer"` y `dotnet test SU --filter "FullyQualifiedName~ShopifyOrderMirror"` → FAIL (no compila).
- [ ] **Step 3:** Crear proyectos, entidades, contexto y migración con el factory de diseño; inicializador como
  `IntegrationsDatabaseInitializer`; `dotnet restore --force-evaluate` sólo si cambia el grafo y commitear los
  locks nuevos.
- [ ] **Step 4:** Los dos comandos → PASS; `dotnet test SI --filter "FullyQualifiedName~ShopifyPersistence"`
  (`MigrationsApply_AndUniqueIndexesExist`) → PASS; `dotnet test AT --filter "FullyQualifiedName~CompositionRoot"` → PASS.
- [ ] **Step 5:** Commit `feat(shopify): módulo y esquema shopify`.

### Task 10: Snapshot normalizado y los dos parsers *(V-4, V-6, V-8, V-9: fixtures reales)*

**Files:**
- Create: `Modules.Shopify.Application/ShopifyOrderSnapshot.cs`; `Modules.Shopify.Infrastructure/Parsing/{RestOrderParser.cs,GraphQlOrderParser.cs}`
- Create: `tests/Modules/Shopify/Modules.Shopify.UnitTests/Fixtures/rest/*.json`, `Fixtures/graphql/*.json` (armados con la forma documentada; cada archivo dice en un comentario `_source: "synthetic, replace per V-9"`)
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/OrderParsersTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ShopifyOrderSnapshot(long OrderId, string Name, long? CustomerId, DateTimeOffset CreatedAt,
      DateTimeOffset UpdatedAt, DateTimeOffset? CancelledAt, string? CancelReason, string FinancialStatus,
      string Currency, decimal TotalPrice, bool TaxesIncluded, bool Test, string? Note,
      IReadOnlyList<string> PaymentGateways, ShopifyBuyer Buyer, ShopifyAddress? ShippingAddress,
      IReadOnlyDictionary<string,string> Attributes, IReadOnlyList<ShopifyLine> Lines,
      IReadOnlyList<ShopifyShippingLine> ShippingLines);
  ShopifyLine(string? Sku, decimal Quantity, decimal OriginalUnitPrice, decimal DiscountAmount)
  ShopifyShippingLine(string? Code, string? Title, decimal DiscountedPrice, decimal TaxAmount, bool IsRemoved)
  ShopifyBuyer(string? FirstName, string? LastName, string? Email, string? Phone)
  ShopifyAddress(string? Name, string? Address1, string? Address2, string? City, string? Province,
      string? ProvinceCode, string? CountryCode, string? Phone)
  static ShopifyOrderSnapshot RestOrderParser.Parse(JsonElement order)
  static ShopifyOrderSnapshot GraphQlOrderParser.Parse(JsonElement node)
  ```
  Montos en moneda de la tienda (`shop_money`/`shopMoney`); `id` de GraphQL `gid://shopify/Order/123` → `123`.

- [ ] **Step 1: RED.** `Rest_And_GraphQl_SameOrder_ProduceEqualSnapshots` (un par de fixtures por caso:
  `taxes-included`, `taxes-excluded`, `discounts`, `pickup`, `shipping-charged`, `shipping-free-by-discount`,
  `shipping-removed-line`, `cash-on-delivery`, `test-order`, `cancelled`, `no-document`, `no-city`);
  `Parse_OrderWithoutCustomerAndNullSku_ProducesSnapshot` (Review Focus 2: `CustomerId == null`, `Sku == null`);
  `Parse_Attributes_ReadsNoteAttributes` (REST `note_attributes`, GraphQL `customAttributes`).
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~OrderParsers"` → FAIL.
- [ ] **Step 3:** Implementar con `System.Text.Json` sobre `JsonElement`, tolerante a ausentes (`TryGetProperty`),
  estricto con `id`, `name`, `updated_at` (si faltan → `ShopifyPayloadException`).
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5:** Commit `feat(shopify): normalizar pedidos REST y GraphQL`.

### Task 11: Webhook firmado y worker de entregas *(V-2, V-5: Shopify real)*

**Files:**
- Create: `Modules.Shopify.Api/ShopifyWebhookEndpoints.cs` (`Route = "/api/webhooks/shopify"`); `Modules.Shopify.Application/ReceiveShopifyWebhook.cs`; `Modules.Shopify.Infrastructure/Persistence/WebhookDeliveries.cs`; `Workers/ShopifyWebhookDeliveryWorker.cs`; `ShopifyWebhookOptions.cs` (`MaxBodyBytes = 1048576`)
- Create: `Modules.Shopify.Application/IShopifyAppSecret.cs` (puerto: `string? ClientSecret`), adaptador en `src/Bootstrapper/ShopifyAppSecret.cs` sobre `IShopifyAppSettings` (Task 5)
- Modify: `src/Api/Program.cs:186` (`app.MapShopifyWebhook(RateLimiterPolicies.Webhook);`)
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyWebhookDeliveryProcessorTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyApiHarness.cs`, `ShopifyWebhookApiTests.cs`

**Interfaces:**
- Consumes: `ShopifyHmac.VerifyBody` está en Integrations.Application, que Shopify no puede referenciar: la
  verificación del cuerpo se reimplementa en `Modules.Shopify.Application/ShopifyBodySignature.cs` con los
  mismos vectores de la Task 6 (dos copias de 10 líneas antes que un acoplamiento prohibido).
- Produces: `IShopifyConnectionDirectory.FindByShopDomainAsync(string shopDomain, ct) → ShopifyConnectionRef?`
  (`TenantId`, `ConnectionId`, `ShopId`, `IsOperational` = conexión `Active` y módulo `shopify` activo,
  `AccessToken` sólo si operativa) — puerto en `ShopifyPorts.cs`, adaptador en Task 12; procesador
  `ShopifyWebhookDeliveryProcessor.ProcessAsync(delivery)`.

- [ ] **Step 1: RED unitario:** `UnknownShop_MarksIgnoredUnknownShop`, `AppUninstalled_ReportsCredentialsRejectedWithAppUninstalled`,
  `OrdersTopic_PausedConnection_UpsertsMirrorWithoutImport`, `OrdersTopic_Operational_UpsertsAndImports`
  (importador falso), `CustomersRedact_AuditsOnly`, `ShopRedact_DeletesMirrorAndDeliveriesOfThatShop`,
  `Worker_MalformedPayload_MarksProcessedWithError` (Review Focus 2: `ShopifyPayloadException` → procesada con
  `last_error`, sin reintento).
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~ShopifyWebhookDeliveryProcessor"` → FAIL.
- [ ] **Step 3:** Endpoint anónimo, `RequireRateLimiting(policy)`, cuerpo acotado (413), firma `X-Shopify-Hmac-Sha256`
  inválida → 401 sin encolar; `INSERT … ON CONFLICT (webhook_id) DO NOTHING` y 200 antes de procesar. Worker:
  lote 50, leases y 8 intentos como `WebhookDeliveryWorker` (H15). `IConnectionHealthReporter` entra por un
  puerto propio `IShopifyConnectionHealth` (adaptador Task 12).
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración:** `ShopifyApiHarness` (prende `shopify`, enlaza una tienda insertando conexión y
  ruta por los servicios de Integrations, crea asesor, empresa con cuenta COP, clasificación, tasa del envío y
  productos, y firma cuerpos con el secreto de prueba); `Webhook_Signed_Returns200AndRow`,
  `Webhook_Unsigned_Returns401NoRow`, `Webhook_Duplicate_OneRow`, `Webhook_TooLarge_Returns413`,
  `Webhook_IsExemptFromCsrf` (stub apagado). `dotnet test SI --filter "FullyQualifiedName~ShopifyWebhookApi"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(shopify): webhook firmado y worker de entregas`.

### Task 12: Puertos de Shopify y sus adaptadores en Bootstrapper

**Files:**
- Modify: `Modules.Shopify.Application/ShopifyPorts.cs`
- Create: `src/Bootstrapper/{ShopifyConnectionDirectory.cs,ShopifyCustomerDirectory.cs,ShopifyProductLookup.cs,ShopifyGeographyLookup.cs,ShopifyShippingLookup.cs,ShopifyOrderWriter.cs,ShopifyMemberDirectory.cs,ShopifyBillingAccounts.cs,ShopifyConnectionHealth.cs}`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro), `src/Bootstrapper/Bootstrapper.csproj` (referencia a `Modules.Shopify.Application`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Produces (todo en `ShopifyPorts.cs`; tipos propios, nunca de otros módulos):
  ```csharp
  IShopifyConnectionDirectory { FindByShopDomainAsync; Task<IReadOnlyList<ShopifyConnectionRef>> ListOperationalAsync(ct); }
  IShopifyCustomerDirectory { Task<ShopifyCustomerRef?> FindByDocumentAsync(Guid tenantId, string type, string number, ct);
      FindByPhoneAsync(Guid tenantId, string e164, ct); FindByEmailAsync(Guid tenantId, string email, ct);
      FindByIdAsync(Guid tenantId, Guid id, ct);
      Task<ShopifyCustomerRef> CreateCompleteAsync(Guid tenantId, ShopifyCompleteCustomer data, ct);
      Task<ShopifyCustomerRef> CreateIncompleteAsync(Guid tenantId, string? name, string? phone, string? email, string? country, ct); }
      // ShopifyCustomerRef(Guid Id, bool IsComplete, bool IsActive)
  IShopifyProductLookup { Task<IReadOnlyDictionary<string, ShopifyProductRef>> FindByCodesAsync(Guid tenantId, IReadOnlyCollection<string> skus, ct); }
      // ShopifyProductRef(Guid Id, string Code, bool IsActive, int TaxPercentage)
  IShopifyGeographyLookup { Task<ShopifyCityRef?> FindCityAsync(string departmentName, string cityName, ct); }
      // ShopifyCityRef(Guid DepartmentId, Guid CityId)
  IShopifyShippingLookup { Task<ShopifyCarrierRef?> MatchCarrierAsync(Guid tenantId, string title, ct);
      Task<int?> GetFreightTaxPercentageAsync(Guid tenantId, ct); }   // ShopifyCarrierRef(Guid Id, string Name, bool SupportsCashOnDelivery)
  IShopifyOrderWriter { Task<ShopifyOrderWriteResult> CreateAsync(ShopifyOrderDraft draft, ct);
      Task<ShopifyOrderOutcome> SyncPaymentStatusAsync(Guid tenantId, Guid orderId, string financialStatus, ct);
      Task<ShopifyOrderOutcome> CancelAsync(Guid tenantId, Guid orderId, Guid byMembershipId, string reason, ct); }
      // ShopifyOrderOutcome { Applied, Ignored(code), AlreadyInvoiced, AlreadyCancelled }; ShopifyOrderWriteResult(Guid OrderId, string OrderNumber, decimal Total, bool AlreadyExisted) | Rejected(string Code)
  IShopifyMemberDirectory { Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, ct); Task<string?> NameAsync(...); }
  IShopifyBillingAccounts { Task<ShopifyBillingAccountRef?> FindAsync(Guid tenantId, Guid companyId, string bank, string number, ct); }
  IShopifyConnectionHealth { Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, ct); Task ReportRejectedAsync(...); }
  ```
  `ShopifyOrderDraft` lleva los datos de `ExternalOrderDraft` con tipos propios (partes, líneas, envío como
  `ShopifyShippingDraft(bool IsStorePickup, Guid? CarrierId, string? CarrierName, string? ChargeMode,
  decimal FreightAmount, int FreightTaxPercentage, bool CashOnDelivery)`), `ExternalKey`, `ExternalShippingTitle`.

- [ ] **Step 1: RED** (dobles de los repositorios de cada módulo, como `PosProductLookupTests`):
  `ShippingLookup_MatchesNameAndExternalNameNormalized` («  coordinadora   EXPRESS » contra externo
  «Coordinadora Express»), `ShippingLookup_IgnoresInactiveCarriers`, `ShippingLookup_InactiveRate_ReturnsNull`,
  `ProductLookup_ReadsTaxPercentageByRateId`, `OrderWriter_MapsExternalKeyTakenToExisting`,
  `OrderWriter_DomainException_ReturnsRejectedWithCode`, `OrderWriter_Cancel_AlreadyInvoiced_ReturnsOutcome`,
  `OrderWriter_Sync_NotPendingOrWithProofs_ReturnsIgnored`, `CustomerDirectory_UnknownDocumentType_IsNotCalled`
  (el adaptador recibe ya el tipo normalizado), `ConnectionDirectory_ModuleOff_IsNotOperational`.
- [ ] **Step 2:** `dotnet test BU --filter "FullyQualifiedName~ShopifyAdapters"` → FAIL.
- [ ] **Step 3:** Implementar; `financial_status` → `OrderPaymentStatus` en el adaptador (`paid` →
  `FullPaymentReceived`, `partially_paid` → `PartialPaymentReceived`, resto → `PaymentPending`; `refunded`,
  `partially_refunded`, `voided` → `Ignored` sin llamar). `MatchCarrierAsync` usa `CarrierName.Normalize`.
- [ ] **Step 4:** Mismo comando → PASS; `dotnet test AT` filtrado a `ShopifyLayer|IntegrationsLayer|CompositionRoot` → PASS.
- [ ] **Step 5:** Commit `feat(shopify): puertos hacia pedidos, clientes, catálogo y transportadoras`.

### Task 13: Importador: precondiciones y cliente *(V-4: Shopify real)*

**Files:**
- Create: `Modules.Shopify.Application/{ShopifyOrderImporter.cs,ShopifyCustomerResolver.cs,ColombiaProvinceCodes.cs,ShopifyDocumentTypes.cs}`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/{ShopifyPreconditionsTests.cs,ShopifyCustomerResolverTests.cs,ColombiaProvinceCodesTests.cs}`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ColombiaProvinceCodesGeographyTests.cs`

**Interfaces:**
- Consumes: puertos de Task 12, `ShopifyOrderMirror`/`ShopifyStore` (Task 9), snapshot (Task 10).
- Produces: `Task<ShopifyImportOutcome> ShopifyOrderImporter.ImportAsync(ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken ct)`
  (sólo evalúa y escribe la fila; el commit es del llamador); `ShopifyDocumentTypes.Normalize(string?) → string?`
  (`NIT|CC|CE|PASAPORTE` sin mayúsculas, si no `null`, D-S22); `ColombiaProvinceCodes.DepartmentNameFor(string code) → string?`.

- [ ] **Step 1: RED.** Precondiciones, una por motivo en el orden de §6.2, verificando que la primera gana y
  el `wait_detail`: `NoAdvisor_StoreNotConfigured`, `InactiveAdvisor_AdvisorInactive`,
  `MissingAccount_BillingAccountMissing`, `UsdOrderCopAccount_CurrencyMismatch` (`{"orderCurrency":"USD","billingCurrency":"COP"}`),
  `FoundInactiveCustomer_CustomerInactive`, `TwoUnknownSkus_ProductUnmatchedListsBoth` (`{"skus":["A-1","B-2"]}`
  y se evaluaron todas las líneas), `TestOrder_Ignored`, `CancelledBeforeImport_Ignored`.
  Cliente: `Search_Order_DocumentThenSeenShopifyIdThenPhoneThenEmail`, `DocumentClassificationCity_CreatesComplete`,
  `DocumentWithoutClassification_CreatesIncompleteWithDocumentWarning`, `NoName_IncompleteNamedClienteDeShopify`,
  `UnknownDocumentType_UsesStoreDefault`, `CustomerAttachedBeforeOrderWrite` (la fila tiene `qep_customer_id`
  aunque el escritor falle). `ColombiaProvinceCodes_Has33Entries` (incluye `DC` → «Bogotá, D.C.» con el
  nombre que tenga la semilla de Geography).
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~ShopifyPreconditions|FullyQualifiedName~ShopifyCustomerResolver|FullyQualifiedName~ColombiaProvinceCodes"` → FAIL.
- [ ] **Step 3:** Implementar. El anotado del cliente (D-S24) lo hace el importador llamando
  `IShopifyUnitOfWork.SaveChangesAsync` después de `row.AttachCustomer(id)`.
- [ ] **Step 4:** Mismo comando → PASS; `dotnet test SI --filter "FullyQualifiedName~ColombiaProvinceCodesGeography"`
  (`EveryCode_ResolvesToSeededDepartment`) → PASS.
- [ ] **Step 5:** Commit `feat(shopify): precondiciones y cliente del pedido importado`.

### Task 14: Importador: precios, envío, pedido, avisos y cambios posteriores *(V-6, V-8, V-10: Shopify real)*

**Files:**
- Create: `Modules.Shopify.Application/{ShopifyPricing.cs,ShopifyShippingMapper.cs,ShopifyCashOnDeliveryGateways.cs,ShopifyOrderChanges.cs}`
- Modify: `ShopifyOrderImporter.cs`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/{ShopifyPricingTests.cs,ShopifyShippingMapperTests.cs,ShopifyImportResultTests.cs,ShopifyOrderChangesTests.cs}`

**Interfaces:**
- Produces: `ShopifyPricing.UnitPriceWithVat(decimal original, bool taxesIncluded, int rate, string currency)`;
  `ShopifyPricing.DiscountPercentage(decimal discount, decimal original, decimal qty)` (4 decimales);
  `ShopifyShippingResult ShopifyShippingMapper.Map(snapshot, string currency, ShopifyCarrierRef? carrier, int? freightRate)`
  → `(ShopifyShippingDraft Draft, string? UnrecognizedTitle, bool CashOnDeliveryUnmatched, string? WaitReason)`;
  `ShopifyCashOnDeliveryGateways.Matches(IEnumerable<string>)` con los cuatro valores de §6.2.2 normalizados;
  `Task ShopifyOrderChanges.ApplyAsync(ShopifyStore, ShopifyOrderMirror, ShopifyOrderSnapshot, ct)` (§6.3).

- [ ] **Step 1: RED.** Precios: `Cop_TaxesExcluded_GrossesUpWithProductRate` (100000 al 19 % → 119000),
  `Usd_TaxesExcluded_KeepsPrice` (D-S27), `Discount_FourDecimals`. Envío: `Pickup_NoFreightNoCod`
  (D-S28, aunque la pasarela sea contraentrega), `NoLines_None`, `RemovedLineIgnored`, `Zero_Free`,
  `Charged_TaxesIncluded_KeepsAmountWithTenantRate`, `Charged_TaxesExcluded_GrossesUp`,
  `Shipping_CopChargedWithCents_RoundsToWholePesos` (Review Focus 4: 15000.50 → 15001, sin excepción),
  `Charged_CopWithoutRate_WaitsFreightTaxRateMissing` (`{"shippingTitle":"Envío","amount":15000}`),
  `TwoLines_SumsAndUsesFirstTitle` (D-S30), `UnknownTitle_WarnsCarrierUnrecognized`,
  `EmptyTitle_NoWarning`, `CodGateway_SetsCashOnDelivery`, `CodWithNonCollectingCarrier_WarnsUnmatched`,
  `NeverOnDelivery`. Resultado: `Imported_SetsTotalDifferenceAndWarnings` (aviso `total_difference` sólo si
  > 1 %), `CodPaid_CreatesPendingThenSyncs` (D-S26), `RejectedCode_WaitsOrderRejected`,
  `AlreadyExisted_MarksImportedWithoutSecondWrite`. Cambios: `Imported_NewFinancialStatus_Syncs`,
  `Sync_Ignored_RecordsLastError`, `Imported_Cancelled_CancelsWithReason` («Cancelado en Shopify: customer»),
  `Imported_CancelledButInvoiced_MarksCancelledInvoicedBeforeCancel`, `Waiting_Cancelled_Ignored`.
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~ShopifyPricing|FullyQualifiedName~ShopifyShippingMapper|FullyQualifiedName~ShopifyImportResult|FullyQualifiedName~ShopifyOrderChanges"` → FAIL.
- [ ] **Step 3:** Implementar. Notas = `"Shopify " + Name` + salto + nota del comprador + dirección cruda si
  `shipping_address_unresolved`; `paymentMethod` = pasarelas unidas con `", "`, recortadas a 50.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5:** Commit `feat(shopify): precios, envío y contraentrega del pedido importado`.

### Task 15: Conciliación y «Sincronizar ahora» *(V-5, V-9: Shopify real)*

**Files:**
- Create: `Modules.Shopify.Application/{IShopifyOrdersSource.cs,ReconcileStore.cs,SyncShopifyStore.cs}`; `Modules.Shopify.Infrastructure/Admin/ShopifyGraphQlClient.cs`; `Workers/ShopifyReconcileWorker.cs`; opciones `Shopify:Reconcile:IntervalMinutes = 10`
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ReconcileStoreTests.cs`, `ShopifyGraphQlClientTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifySyncApiTests.cs`

**Interfaces:**
- Produces: `IShopifyOrdersSource.FetchUpdatedSinceAsync(ShopifyConnectionRef, DateTimeOffset since, string? cursor, ct) → ShopifyOrdersPage(IReadOnlyList<ShopifyOrderSnapshot>, string? NextCursor)`
  (query `orders(first: 50, after:, sortKey: UPDATED_AT, query: "updated_at:>='…'")`);
  `ReconcileStore.RunAsync(ShopifyConnectionRef, ct) → ShopifySyncResult(int Fetched, int Imported, int Waiting, int Cancelled, int Ignored, long DurationMs)`;
  `SyncShopifyStoreCommand(TenantId, ConnectionId)` → `ShopifySyncResult` (409 `shopify.store.sync_in_progress`).

- [ ] **Step 1: RED.** `Reconcile_UsesLastReconciledMinusFiveMinutes`, `Reconcile_Paginates`,
  `Reconcile_Throttled_WaitsRetryAfterThenGivesUpAfterThree`, `Reconcile_401_ReportsRejectedAndStops`,
  `Reconcile_RetriesWaitingOldestFirstCap200`, `Reconcile_LeaseHeld_Skips`, `Reconcile_SetsLastReconciledAt`;
  cliente: `Throttled_ErrorInBody_MapsToThrottled` (`extensions.code == "THROTTLED"`), `Status402_MapsToRejected`.
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~ReconcileStore|FullyQualifiedName~ShopifyGraphQlClient"` → FAIL.
- [ ] **Step 3:** Lease con `UPDATE shopify.stores SET reconcile_lease_until = now()+interval WHERE connection_id = @id
  AND (reconcile_lease_until IS NULL OR reconcile_lease_until < now())` (si el `UPDATE` afecta 0 filas, ya corre).
  La espera del `THROTTLED` usa `TimeProvider` para que la prueba no duerma.
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración** con handler HTTP falso: `Sync_ImportsAndReturnsCounts`,
  `Sync_WhileRunning_Returns409`, `Sync_WithoutOrderManage_Returns403`, `Sync_ModuleOff_Returns403`.
  `dotnet test SI --filter "FullyQualifiedName~ShopifySyncApi"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(shopify): conciliación periódica y sincronización manual`.

### Task 16: API de tiendas y de la bandeja

**Files:**
- Create: `Modules.Shopify.Application/{ShopifyStores.cs,ShopifyOrders.cs,ShopifyDtos.cs,ShopifyAuthorization.cs}`; `Modules.Shopify.Api/ShopifyEndpoints.cs`
- Modify: `src/Api/Program.cs` (`app.MapShopifyEndpoints();`), `QepServiceCollectionExtensions.cs` (handlers y validadores)
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyStoreHandlersTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/{ShopifyStoresApiTests.cs,ShopifyInboxApiTests.cs}`

**Interfaces:**
- Produces las rutas de §7 bajo `/api/v1/tenants/{tenantId:guid}/shopify`: `GET /stores`, `GET|PUT /stores/{connectionId}`
  (`If-Match`, 428/412), `GET /orders?state=&connectionId=&withWarnings=&page=&pageSize=` (tope 50, `state`
  por defecto `Waiting`, acepta varios), `GET /orders/summary` → `{ waiting, withWarnings, cancelledAfterInvoice, importedToday }`,
  `POST /orders/{id}/retry`, `POST /orders/{id}/warnings/review`. `UpdateShopifyStoreCommand(TenantId, ConnectionId,
  AdvisorMembershipId, BillingCompanyId, BillingBankName, BillingAccountNumber, BillingCurrency,
  DefaultClassificationId?, DocumentAttributeKey, DocumentTypeAttributeKey, DefaultIdentificationType, ExpectedVersion)`.

- [ ] **Step 1: RED.** `Put_AdvisorNotMember_Throws` (`shopify.store.advisor_not_member`),
  `Put_BillingNotFound_Throws`, `Put_ClassificationOtherTenant_Throws`, `Put_VersionMismatch_Throws412`,
  `Put_EmptyAttributeKey_ValidationErrors` (`errors["documentAttributeKey"]`), `Retry_NotWaiting_Throws`
  (`shopify.order.not_waiting`), `Review_ClearsFromWithWarnings`.
- [ ] **Step 2:** `dotnet test SU --filter "FullyQualifiedName~ShopifyStoreHandlers"` → FAIL.
- [ ] **Step 3:** Implementar. La tienda vacía se crea en la primera lectura (§6.1). `summary` siempre con las
  cuatro claves (nota BFF). Lista con `storeName`, `customerName` y `customerEmail` del snapshot, y `qepOrderNumber`
  por un puerto de lectura nuevo en esta tarea, `IShopifyOrderNumbers.FindAsync(Guid tenantId,
  IReadOnlyCollection<Guid> orderIds, ct) → IReadOnlyDictionary<Guid,string>`, con adaptador
  `src/Bootstrapper/ShopifyOrderNumbers.cs` sobre `IOrderRepository` (una consulta por página).
- [ ] **Step 4:** Mismo comando → PASS.
- [ ] **Step 5: RED integración:** `Stores_List_ShowsWaitingCountAndIsConfigured`, `Stores_Put_Then_Get_RoundTrips`,
  `Orders_DefaultStateWaiting`, `Orders_OtherTenant_Returns403`, `Summary_EmptyTenant_AllZeros`,
  `Retry_ResolvedProduct_Imports`. `dotnet test SI --filter "FullyQualifiedName~ShopifyStoresApi|FullyQualifiedName~ShopifyInboxApi"` → FAIL → PASS.
- [ ] **Step 6:** Commit `feat(shopify): configuración de tiendas y bandeja de pedidos`.

### Task 17: Flujo de punta a punta, documentación y handoff *(cierra con Shopify real: V-1 a V-10)*

**Files:**
- Create: `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyImportFlowTests.cs`, `ShopifySecretLeakTests.cs`
- Modify: `README.md` (sección «Integración con Shopify»: configuración, secretos, módulo por defecto, flujo), `CLAUDE.md` (gotchas: el webhook de Shopify sólo se prueba con el stub apagado; un pedido externo repetido lo frena `IX_orders_tenant_external_key`; `ShopifyLayerTests` prohíbe referencias a módulos de negocio), `k8s/prod-configMap.yaml`, `k8s/prod-secret.yaml` (`Shopify__App__ClientSecret: "#{SHOPIFY_APP_CLIENT_SECRET}#"`), `azure-pipelines.yml` si sustituye variables explícitamente
- Create: `docs/superpowers/plans/2026-10-18-shopify-handoff.md` (lo de §10.5 y la tabla de §16 con «Cómo se cierra»)

- [ ] **Step 1: RED integración** (webhook firmado → worker → pedido, con el harness de Task 11):
  `Import_WithDocument_CreatesCompleteCustomerWithCuc`, `Import_WithoutDocument_IncompleteAndPendingOrder`,
  `SecondOrder_SameBuyer_ReusesCustomer` (por `shopify_customer_id`, por teléfono y por email),
  `Import_NoQuotation_NotInQuotationsList`, `Import_PublishesOrderCreatedWithShopifySource`,
  `Import_ChargedFreight_InTotalAndExcel` (Excel: `Valor Flete` = 15000, `Documento` vacío para incompleto),
  `Import_UnrecognizedCarrier_ThenRecognize_NextOrderMatches` (usa la ruta de Task 3),
  `Reimport_AfterMirrorRowDeleted_DoesNotDuplicateOrder` (Review Focus 1),
  `ModuleOff_StoresMirrorWithoutImporting_ThenReenableImports`, `Cancel_OnPendingApprovedInvoicedAndWaiting`,
  `Payment_WithAndWithoutProofs`. `ShopifySecretLeakTests`: token sentinela ausente de todas las respuestas
  de `/shopify/*`, logs y `audit.entries`.
- [ ] **Step 2:** `dotnet test SI --filter "FullyQualifiedName~ShopifyImportFlow|FullyQualifiedName~ShopifySecretLeak"` → FAIL donde falte cableado; corregir en el componente dueño.
- [ ] **Step 3:** → PASS. Corrida final acotada por módulo tocado (no la solución):
  `dotnet test TU`, `TI`, `QU`, `QI`, `CU`, `CI`, `IU`, `II`, `SU`, `SI`, `BU`, `AT` con `--blame-hang-timeout 5m`,
  más `dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/Modules.Pipeline.IntegrationTests.csproj --filter "FullyQualifiedName~Order"`
  y `dotnet format --verify-no-changes` (README § Verificación).
- [ ] **Step 4:** Documentación y handoff. El handoff lista para el owner, en PowerShell cuando hay comandos:
  1. Crear la app pública **no embebida** en el Dev Dashboard; App URL `https://qep.qcode.co/shopify/install`;
     redirect URLs `https://qep.qcode.co/shopify/callback` y `http://localhost:3002/shopify/callback`; scopes
     `read_orders,read_customers`; los tres webhooks de cumplimiento a `https://qep-api.qcode.co/api/webhooks/shopify` (el host del
     ingress, `k8s/prod-ingress.yaml:24`, que es también `Shopify:App:WebhookCallbackUrl`).
  2. Pedir **acceso a datos protegidos de clientes** (V-3).
  3. Cargar `SHOPIFY_APP_CLIENT_ID` y `SHOPIFY_APP_CLIENT_SECRET` (secreta) en `Backend-prod`, y en local
     `dotnet user-secrets set "Shopify:App:ClientId" …` / `ClientSecret`, verificando con
     `dotnet user-secrets list --project src/Api | Select-String -Pattern "Shopify:App" | Measure-Object`.
  4. Tienda de desarrollo: instalar desde QEP (inicio A) y desde la tienda (inicio B, V-7); confirmar V-1 y
     que se registraron los cinco webhooks (V-2).
  5. Pedidos de prueba y qué mirar en cada uno: pendiente que luego se paga (V-5), recogida (V-6),
     contraentrega (V-8: anotar `payment_gateway_names` y `financial_status`), envío cobrado y gratis por
     descuento (V-9: guardar el payload crudo de `shopify.webhook_deliveries` como fixture real), cada
     transportadora del checkout (V-10), dirección colombiana con documento en el checkout (V-4).
  6. Prender `shopify` al tenant piloto desde la consola de operador.
- [ ] **Step 5:** Commit `docs(shopify): README, despliegue y handoff de la integración`.

---

## Self-review (hecha al escribir)

- Cobertura: §4 → Tasks 5-8; §4.4 → 1; §5 → 9-11, 15; §6.1 → 9, 16; §6.2-§6.2.4 → 2, 12-14; §6.2.5 → 2, 3;
  §6.3 → 14; §6.4 → 15, 16; §6.5 → 2-4; §7 → 6-8, 16; §9 → 2, 4, 6, 9; §10 → 5, 17; §12 → 6-8, 11, 17;
  §13 → todas; §16 → 17 (handoff). §8 es el plan del frontend.
- Nombres cruzados: `ExternalOrderDraft.Shipping/ExternalKey/ExternalShippingTitle` (2) ← `ShopifyOrderWriter` (12);
  `Carrier.AddExternalName` (3); `ShopifyWebhookTopics.Registered` (8); `ShopifyCustomerRef`, `ShopifyCarrierRef`,
  `ShopifyShippingDraft` (12) ← 13, 14.
