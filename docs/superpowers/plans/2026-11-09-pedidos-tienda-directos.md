# Pedidos de la tienda directos, sin bandeja intermedia — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `POST /api/v1/public/stores/{host}/order-requests` cree un `Order` real (`Source = Store`, `PaymentPending`) con asesor y cliente resueltos sin intervención humana, idempotente por `clientRequestId`, y que la bandeja «Desde la tienda» (entidad, tablas, endpoints, handlers, DTOs, numeración `T-xxxx`) desaparezca.

**Architecture:** El handler público conserva su orden de rechazos (tienda → forma → moneda → consentimiento → reintento → lugares/teléfonos → Turnstile → tope diario → cuenta de cobro → precio) y después resuelve asesor (ajuste nuevo o owner del tenant, por el puerto `IStoreMemberDirectory`), cliente (por documento, por `IStoreCustomerDirectory`, con caída a incompleto al estilo Shopify) y crea el pedido por `IStoreOrders` → `StoreOrderGateway` con clave `store-checkout:{clientRequestId}`. Lo que Store guarda después del pedido (auditoría anónima, fila de `store.order_consents`, evento de alta de cuenta) va en un solo `SaveChanges` de Store. `Modules.Store.Application` no gana referencias: todo lo de Tenancy, Customers y Quotations entra por puertos con adaptadores en Bootstrapper.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`](../specs/2026-11-09-pedidos-tienda-directos-design.md) · specs hermanos: `qep-frontend/docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`, `qep-storefront/docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`.

---

## Antes de empezar

- Worktree `qep-backend-worktrees/pedidos-tienda-directos`, rama `feat/store-direct-orders` creada desde `develop`. Antes de **cada** commit: `git branch --show-current` tiene que decir `feat/store-direct-orders`; si dice otra cosa, frena y pregunta.
- Todo comando es PowerShell, una línea por comando, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas:** es del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y, en la misma sesión, `$env:Seed__Enabled = "false"`. Nunca la solución entera: un proyecto a la vez, con `--filter` a la clase que tocas y `-- xUnit.MaxParallelThreads=4`. La corrida completa por proyecto va una sola vez, en la Tarea 12.
- Migraciones con el factory de diseño, nunca `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- La solución tiene que compilar y sus pruebas pasar **al cerrar cada tarea**: las tareas 5 a 7 adaptan de forma mínima `TakeOrderRequestHandler` y sus pruebas, que la Tarea 9 borra.

## Global Constraints

- Ruta, cuerpo (`PublicOrderRequestBody`), filtros, `RequestSizeLimitAttribute` y limitadores de `POST /api/v1/public/stores/{host}/order-requests` **no cambian**. La ruta no se renombra a `/orders`.
- Recibo `201` con `PublicOrderRequestReceiptDto(Id, Number, Total, Currency, BuyerSessionRejected)`: `Id` = id del `Order`, `Number` = `Order.OrderNumber`, `Total` = `Order.Total`, `Currency` = moneda del pedido. El reintento idempotente también responde `201`.
- Clave de idempotencia: exactamente `"store-checkout:{clientRequestId}"` (`StoreOrderGateway.ExternalKeyFor(Guid clientRequestId)`), sobre el índice existente `IX_orders_tenant_external_key` `(tenant_id, source, external_key)`. `Order.ExternalReference` queda **`null`** para los pedidos de la tienda.
- Asesor: `OnlineStore.AdvisorMembershipId` (nullable) si está configurado **y** sigue siendo membresía activa del tenant; si no, `Tenant.OwnerMembershipId`. La tienda nunca rechaza un pedido por falta de asesor. Al caer al owner por un asesor configurado que ya no está activo se deja un `Warning` en el log sin datos del visitante.
- Cliente: se resuelve sólo por tipo y número de documento (nunca por teléfono). Existente completo → se usa sin tocar. Existente incompleto → `CompleteAsync` llena sólo lo que está en blanco; si Customers no puede completarlo, el pedido sigue con el incompleto. Inexistente → `CreateAsync` completo con `actorUserId = null` y `source = "store"`; si Customers lo rechaza por `ValidationException`, `customers.customer.classification_not_found`, `customers.customer.city_not_found` o `customers.customer.document_type_not_allowed` → `CreateIncompleteAsync(tenantId, name, phone, email, country)`. Documento de un cliente inactivo o fusionado (D8) → incompleto nuevo **sin documento**. Nunca se sobrescriben datos existentes.
- Direcciones: `EnsureAddressesAsync` con la de facturación y, si vino, la de envío (misma construcción que `TakeOrderRequestHandler.AddressInputs`). Para un incompleto que Customers rechaza (`customers.customer.incomplete`), se registra y el pedido sigue.
- Tope diario: `StoreOptions.DailyRequestCap` (500) sobre **todos** los pedidos `Source = Store` del tenant en 24 h, anulados incluidos. Código `store.order_request.daily_cap` (429).
- Códigos que se conservan en el camino público: `validation.failed`, `store.public.not_found`, `store.public.currency_invalid`, `store.public.visitor_ip_required`, `store.public.body_too_large`, `store.public.body_invalid`, `store.order_request.consent_required`, `captcha_invalid`, `country_not_sold`, `city_invalid`, `document_type_not_allowed`, `phone_invalid`, `line_unavailable`, `daily_cap`, `store.captcha.unavailable`; pasa a ser público `store.order_request.currency_not_in_store` (422). **Código nuevo, único:** `store.settings.advisor_not_found` (422). No se renombra el prefijo `store.order_request.*`.
- Los errores del motor de pedidos en `IStoreOrders.CreateAsync` (`quotation.*`, `order.billing.account_required`) se traducen a `store.order_request.line_unavailable` con el original como `InnerException` y su código en el log, como ya hace `QuoteAsync`.
- Auditoría: Store publica `store.order.created` sobre recurso `order` con el `OrderId`, por `PublishAnonymous`. Quotations audita `quotation.order.created` con `PublishByIntegration`. Historial del pedido: entrada `Created` con `memberId = null` y texto `"Creado desde la tienda."`.
- Consentimiento: tabla `store.order_consents (order_id PK, tenant_id, accepted_at, visitor_ip, policy_url NULL, policy_set_at NULL)`, escrita en el mismo `SaveChanges` de Store que la auditoría. No se lee desde ninguna pantalla.
- Evento `store.buyer-signup-requested.v1`: `OrderRequestId` → `OrderId` en `StoreBuyerSignupRequestedPayload`, `BuyerSignupRequestedMessage`, `BuyerProcessedSignup`; columna `buyers.processed_signups.order_request_id` → `order_id`. Idempotencia del worker por `OrderId`.
- Eliminaciones (§4.10 del spec): bandeja completa en Store (Domain, Application, Infrastructure, Api), `StorePermissions.OrderManage`, `StoreOptions.ConversionLease`, `IOrderStoreRequestLookup`/`OrderStoreRequestRef`/`OrderStoreRequestResponse`/`OrderDetailResponse.StoreRequest` en Quotations, `OrderStoreRequestLookup` y el bloque de `store.order_request_lines` de `CatalogProductDeletionCleanup` en Bootstrapper. Migración `DropStoreOrderRequests` con `DROP TABLE` de `store.order_request_lines`, `store.order_requests`, `store.request_counters` (sin copia, D6).
- `Modules.Store.Application` sigue sin referenciar Customers, Geography, Quotations ni Buyers, y no gana `Modules.Tenancy.Domain` más allá de lo que ya usa (`StoreLayerTests`).
- TDD: RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA**, siempre `git commit -m "…" -- <rutas>`.
- Comentarios de código en español, tuteando; el voseo se corrige sólo en las líneas que tocas.
- `docs/estado.md` **no** se toca en esta rama (se actualiza en `develop` al mergear).

## Decisiones de este plan (lo que el spec deja al constructor)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | El handler conserva el nombre `SubmitOrderRequestHandler` y `SubmitOrderRequestCommand`; cambia el cuerpo, no el registro en DI ni el endpoint. | Un renombre no cambia comportamiento y multiplica el diff. |
| P2 | `StoreRequestBilling`, `StoreRequestShipping`, `StorePlace` y `StoreConsent` se **conservan** como value objects del handler (construcción de partes, direcciones y consentimiento). | Ya validan tipo de documento por país, teléfonos y carriles de ciudad; aplanarlos repetiría esa lógica. |
| P3 | Dos migraciones de Store: `AddStoreAdvisor` (Tarea 3) y `AddStoreOrderConsents` (Tarea 4); la de borrado `DropStoreOrderRequests` va aparte (Tarea 9). | Cada una acompaña a la tarea que la necesita para compilar y pasar sus pruebas. |
| P4 | `IStoreCustomerDirectory.CreateAsync` devuelve un resultado tipado (`StoreCustomerCreation` con `Outcome` `Created | ExistingByDocument | DocumentOwnerUnavailable | NotCompletable`) en vez de lanzar: el adaptador decide qué excepciones de Customers son «no completable» (las tres documentadas más `document_type_not_allowed`, como `ShopifyCustomerDirectory.NotCompletableCodes`) y cuáles se propagan. | El handler no referencia Customers y necesita distinguir D3 (caer a incompleto) de D8 (incompleto sin documento) sin un código `store.*` que luego se borra. |
| P5 | `IStoreCustomerDirectory.CompleteAsync` no lanza por «no completable»: ante `ValidationException` o los códigos de P4 devuelve al cliente tal como está (incompleto); ante `customers.customer.identification_taken` relee por documento y devuelve al dueño. `RequestConcurrencyException` se propaga (412 `concurrency.conflict`): el visitante reintenta y el reintento es idempotente. | §4.5 pasos 3 y 4. |
| P6 | `EnsureAddressesAsync` gana la salida `StoreAddressOutcome.Skipped`: cuando Customers responde `customers.customer.incomplete`, el adaptador devuelve `Skipped` para todas y el handler sólo lo registra. | §4.5 paso 6: la dirección de envío ya viaja en la parte `Shipping` del pedido. |
| P7 | Los métodos del directorio que sólo servían al diálogo (`FindCandidatesAsync`, `KnownAsync`, `FindEligibleAsync`) se borran en la Tarea 11, una vez que la Tarea 9 quitó a sus únicos consumidores. | Mantener el orden «todo compila al cerrar cada tarea». |
| P8 | El reintento idempotente **no** vuelve a emitir el alta de cuenta cuando el pedido existe y la cuenta no (R4, aceptado). | El spec lo deja opcional; no se agrega. |
| P9 | La columna física `buyers.processed_signups.order_request_id` **sí** se renombra a `order_id` con migración `RenameProcessedSignupOrderId` (editada a mano a `RenameColumn`, nunca drop+add). | Un nombre que miente en la base cuesta más que una migración de una línea. |
| P10 | `StoreOrderCreated` gana `Currency`; el recibo lo toma de ahí (la moneda de la cuenta de cobro). | §4.2: la moneda del recibo es la del pedido. |
| P11 | Cuando `IStoreOrders.CreateAsync` devuelve `AlreadyExisted = true` (perdió la carrera de la clave), el handler responde ese recibo **sin** escribir auditoría, consentimiento ni evento: el ganador ya los escribió. | Evita una segunda fila de consentimiento sobre la misma PK y un segundo alta. |
| P12 | El conteo del tope diario entra a `IOrderRepository` como `CountBySourceSinceAsync(tenantId, OrderSource, since)` sobre `Order.ConvertedAt`, y al gateway como `CountStoreOrdersSinceAsync`. | `ConvertedAt` es la fecha de creación de todo pedido (es la que ordena el listado). |
| P13 | Las pruebas de integración siembran la membresía owner del tenant si el harness aún no la crea: `StoreApiHarness.SeedTenantAsync` tiene que dejar `Tenant.OwnerMembershipId` apuntando a una fila de `tenancy.memberships` activa, para que `advisorName` y el fallback se puedan afirmar. | El fallback lee `Tenant.OwnerMembershipId`; el nombre sale de `NamesAsync`. |

## Review Focus

1. **Reintento con el mismo `clientRequestId` y otro cuerpo** (otra moneda, otra línea): devuelve el primer recibo, un solo pedido, sin segundo alta de cuenta. Lo amarra la Tarea 8 (`ARetryNamingAnotherCurrencyGetsTheFirstOrderInItsOwnCurrency`, `TheSameClientRequestIdReturnsTheSameOrder`).
2. **Cuatro envíos simultáneos del mismo `clientRequestId`**: todos `201`, un solo pedido, una sola fila de `store.order_consents`, un solo evento de alta. Lo amarra la Tarea 8 (`TwoSimultaneousSubmissionsOfTheSameIdCreateOneOrder`).
3. **Asesor configurado y luego quitado o suspendido**: el pedido nace con el owner, `201`, y hay un `Warning`. Lo amarran la Tarea 8 unitaria (`AConfiguredAdvisorThatIsNoLongerActiveFallsBackToTheOwnerWithAWarning`) e integración (`AnAdvisorRemovedAfterBeingConfiguredFallsBackToTheOwner`).
4. **Documento de un cliente inactivo**: cliente incompleto nuevo sin documento, pedido creado, la ficha inactiva no se toca. Lo amarra la Tarea 8 (`ADocumentOfAnInactiveCustomerCreatesANewIncompleteCustomerWithoutDocument`).
5. **Un pedido anulado sigue contando para el tope diario**: con cap 2, dos pedidos (uno anulado) y el tercero es 429. Lo amarran la Tarea 5 (`CountBySourceSinceCountsCancelledStoreOrders`) y la Tarea 8 (`TheDailyCapCountsCancelledOrders`).

---

### Task 1: Dominio — `OnlineStore.AdvisorMembershipId`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Domain/OnlineStore.cs` (propiedad junto a `PricingMode`, `:51`; asignación en `Configure`, `:128-151`; `StateKey()`, `:163-166`)
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreSettingsInput.cs:7-21`
- Modify: `tests/Modules/Store/Modules.Store.UnitTests/StoreTestData.cs:26-45` (`Input(...)` gana `Guid? advisorMembershipId = null`)
- Test: `tests/Modules/Store/Modules.Store.UnitTests/OnlineStoreTests.cs`

**Interfaces:**
- Produces: `public Guid? AdvisorMembershipId { get; private set; }` en `OnlineStore`; `StoreSettingsInput` gana el último parámetro `Guid? AdvisorMembershipId`. `PublishBlockers()` no cambia.

- [ ] **Step 1: Write the failing tests** en `OnlineStoreTests`:
  - `ConfiguringTheAdvisorStoresItAndBumpsTheVersion` — `ConfiguredStore()` (versión 1) → `Configure(Input(advisorMembershipId: Member), …)` devuelve `true`, `AdvisorMembershipId == Member`, `Version == 2`.
  - `ReconfiguringTheSameAdvisorChangesNothing` — tienda con `Member`; `Configure(Input(advisorMembershipId: Member))` devuelve `false`, `Version` igual.
  - `ClearingTheAdvisorBumpsTheVersion` — tienda con `Member`; `Configure(Input())` devuelve `true`, `AdvisorMembershipId is null`.
  - `AnAdvisorIsNotAPublishBlocker` — `Empty()` con `Input(advisorMembershipId: null)` configurado → `PublishBlockers()` vacío (mismo input que `ConfiguredStore`).
- [ ] **Step 2: Run, expect FAIL** (compilación: `Input` no acepta el parámetro; la propiedad no existe).
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~OnlineStoreTests"`
- [ ] **Step 3: Implement.** Propiedad, asignación `AdvisorMembershipId = input.AdvisorMembershipId;` dentro del bloque de asignaciones, y `AdvisorMembershipId` como un elemento más de `StateKey()`. Comentario en la propiedad: «Asesor de los pedidos de la tienda (spec 2026-11-09, D2); null = el owner del tenant, resuelto al crear el pedido».
- [ ] **Step 4: Run, expect PASS**, más `--filter "FullyQualifiedName~StoreSettingsHandlerTests|FullyQualifiedName~UpdateStoreSettingsValidatorTests"` (siguen en verde).
- [ ] **Step 5: Commit** — `feat(store): add the advisor membership to the online store`.

### Task 2: Puerto de membresías — activa y owner del tenant

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreMemberDirectory.cs`
- Modify: `src/Bootstrapper/StoreMemberDirectory.cs`
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreApiHarness.cs:47-88` (`SeedTenantAsync`, sólo si no deja al owner con fila; ver P13)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreMemberDirectoryTests.cs` (nuevo)

**Interfaces:**
- Produces, en `IStoreMemberDirectory`:
  - `Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);` — `true` sólo si la membresía es de ese tenant y su `State == MembershipState.Active`.
  - `Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken);` — `Tenant.OwnerMembershipId.Value`; lanza `InvalidOperationException` si el tenant no existe (no hay tienda sin tenant).
- El adaptador lee `TenancyDbContext.Memberships` y `TenancyDbContext.Tenants` con `AsNoTracking()`, como `NamesAsync`.

- [ ] **Step 1: Write the failing tests** (`StoreApiHarness.OpenAsync()`, resuelve `IStoreMemberDirectory` de un scope):
  - `AnActiveMembershipOfTheTenantIsActive` — `SeedMemberAsync(factory, "ana@test.co", "Ana", "advisor")` → `true`.
  - `ARemovedMembershipIsNotActive` — `SeedMemberAsync(…, removed: true)` → `false`.
  - `AMembershipOfAnotherTenantIsNotActive` — `SeedTenantAsync(factory, OtherTenantId)` + miembro en `OtherTenantId`, consultado con `Tenant` → `false`.
  - `TheOwnerMembershipIsTheTenantsOwner` — `FindOwnerMembershipIdAsync(Tenant)` == `tenancy.Tenants.Single(…).OwnerMembershipId.Value` leído del `TenancyDbContext`.
- [ ] **Step 2: Run, expect FAIL** (compilación: los métodos no existen).
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreMemberDirectoryTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement** los dos métodos en `StoreMemberDirectory`. Si `SeedTenantAsync` no crea la fila de la membresía owner, agrégala ahí (P13) con `Membership.CreateActive(ownerMembershipId, …)` y rol `admin`.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(store): expose active membership and tenant owner through the member directory`.

### Task 3: Ajustes — `advisorMembershipId`, `advisorName`, `advisor_not_found` y migración `AddStoreAdvisor`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreErrorCodes.cs:24` (nueva constante)
- Modify: `src/Modules/Store/Modules.Store.Application/StoreDtos.cs:10-29` (`StoreSettingsRequest`), `:38-62` (`StoreSettingsDto`)
- Modify: `src/Modules/Store/Modules.Store.Application/UpdateStoreSettings.cs:102-118` (constructor: `IStoreMemberDirectory members`), `:160-178` (validación y `StoreSettingsInput`), `:294-318` (`Snapshot`)
- Modify: `src/Modules/Store/Modules.Store.Application/StoreSettingsComposer.cs:9-14` (constructor: `IStoreMemberDirectory members`), `:34-72`
- Modify: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/StoreDbContext.cs:114-120` (columna)
- Create: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/Migrations/<timestamp>_AddStoreAdvisor.cs` (+ Designer y snapshot)
- Modify: `tests/Modules/Store/Modules.Store.UnitTests/StoreSettingsTestDoubles.cs` (fake `IStoreMemberDirectory` nuevo, `SettingsRig`, `Body(...)` gana `Guid? advisorMembershipId = null`)
- Test: `tests/Modules/Store/Modules.Store.UnitTests/StoreSettingsHandlerTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreSettingsApiTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreErrorMappingApiTests.cs:23-39`

**Interfaces:**
- Consumes: Task 1 (`StoreSettingsInput.AdvisorMembershipId`), Task 2 (`IsActiveMemberAsync`, `NamesAsync`).
- Produces: `StoreErrorCodes.AdvisorNotFound = "store.settings.advisor_not_found"`; `StoreSettingsRequest` gana `Guid? AdvisorMembershipId` (último); `StoreSettingsDto` gana `Guid? AdvisorMembershipId` y `string? AdvisorName` (después de `FeaturedCategories`, antes de `PublishBlockers`). `UpdateStoreSettingsHandler`: si `body.AdvisorMembershipId is { } id && !await members.IsActiveMemberAsync(tenantId, id)` → `StoreDomainException(AdvisorNotFound, …)` (422), después de `EnsureCategoriesAsync` y antes de armar el input. `Snapshot` suma `["advisorMembershipId"]`. El composer resuelve `AdvisorName` con `NamesAsync(tenantId, [id])` (null si no hay id o no aparece). Columna `advisor_membership_id uuid NULL` en `store.stores`.

- [ ] **Step 1: Write the failing tests.**
  - Unitarias (`StoreSettingsHandlerTests`, con un `FakeMembers : IStoreMemberDirectory` en los dobles que tiene `HashSet<Guid> Active` y `Dictionary<Guid,string> Names`; `ResolveCallerAsync` y `FindOwnerMembershipIdAsync` lanzan `NotSupportedException`):
    - `SavingAnActiveAdvisorStoresItAndReturnsItsName` — `Body(advisorMembershipId: Member)`, `Active = {Member}`, `Names[Member] = "Ana"` → `saved.AdvisorMembershipId == Member`, `saved.AdvisorName == "Ana"`, auditoría con `"advisorMembershipId"` en `ChangedFields`.
    - `AnInactiveOrForeignAdvisorIs422AdvisorNotFound` — `Body(advisorMembershipId: OtherMember)` sin estar en `Active` → `CodeOfAsync` == `StoreErrorCodes.AdvisorNotFound`, `rig.Log.Entries` vacío.
    - `ClearingTheAdvisorAuditsTheField` — tienda sembrada con asesor; `Body()` → `AdvisorMembershipId` null y `"advisorMembershipId"` en la auditoría.
  - Integración (`StoreSettingsApiTests`):
    - `SavingTheAdvisorRoundTripsWithItsName` — `SeedMemberAsync(fixture.Factory, "luis@test.co", "Luis Gómez", "advisor")`; `PUT` con `advisorMembershipId` → `200`, `settings["advisorMembershipId"]` igual, `settings["advisorName"] == "Luis Gómez"`; `GET` lo repite.
    - `ARemovedAdvisorIs422AdvisorNotFound` — miembro `removed: true` → `422`, código `store.settings.advisor_not_found`.
  - `StoreErrorMappingApiTests`: fila `(new StoreDomainException(StoreErrorCodes.AdvisorNotFound, "advisor"), StoreErrorCodes.AdvisorNotFound, HttpStatusCode.UnprocessableEntity)`.
- [ ] **Step 2: Run, expect FAIL** (compilación en las unitarias; 500/`42703` en integración si se compila a medias).
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~StoreSettingsHandlerTests"`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreSettingsApiTests|FullyQualifiedName~StoreErrorMappingApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Código, request, DTO, handler, composer, columna `store.Property(value => value.AdvisorMembershipId).HasColumnName("advisor_membership_id");` (sin FK a `tenancy`: los módulos no se cruzan por FK), y la migración:
  `dotnet ef migrations add AddStoreAdvisor --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`
  Revisa que el `Up` sólo agregue la columna. Comentario en el DTO: el nombre viaja porque la pantalla de ajustes lo pinta sin consultar membresías (BFF).
- [ ] **Step 4: Run, expect PASS**, más `UpdateStoreSettingsValidatorTests` y `StorePersistenceTests` filtrados.
- [ ] **Step 5: Commit** — `feat(store): configure the advisor assigned to store orders`.

### Task 4: `store.order_consents` — entidad, puerto y migración `AddStoreOrderConsents`

**Files:**
- Create: `src/Modules/Store/Modules.Store.Domain/StoreOrderConsent.cs`
- Create: `src/Modules/Store/Modules.Store.Application/IStoreOrderConsents.cs`
- Create: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/StoreOrderConsentRepository.cs`
- Modify: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/StoreDbContext.cs` (`DbSet<StoreOrderConsent> OrderConsents`, `ConfigureOrderConsent`), `src/Modules/Store/Modules.Store.Infrastructure/StoreInfrastructureExtensions.cs:30-36`
- Create: `src/Modules/Store/Modules.Store.Infrastructure/Persistence/Migrations/<timestamp>_AddStoreOrderConsents.cs` (+ Designer y snapshot)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/StorePersistenceTests.cs`

**Interfaces:**
- Produces: `public sealed class StoreOrderConsent` con `Guid OrderId`, `Guid TenantId`, `StoreConsent Consent` (owned: `accepted_at`, `visitor_ip` (45), `policy_url` (500, null), `policy_set_at` (null)) y `public static StoreOrderConsent Create(Guid orderId, Guid tenantId, StoreConsent consent)`; tabla `store.order_consents`, PK `order_id` (`PK_order_consents`), índice `IX_order_consents_tenant` sobre `tenant_id`. `public interface IStoreOrderConsents { void Add(StoreOrderConsent consent); Task<StoreOrderConsent?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken); }` (el `Find` sólo lo usan las pruebas). Guarda con `IStoreUnitOfWork.SaveChangesAsync`.

- [ ] **Step 1: Write the failing tests** (`StorePersistenceTests`):
  - `AnOrderConsentRoundTrips` — `Add` + `SaveChangesAsync`; `FindAsync` devuelve igual `Consent` (fecha, IP, política, `PolicySetAt`).
  - `AnOrderConsentWithoutPolicyRoundTripsWithNulls` — `StoreConsent(Now, ip, null, null)`.
  - `TheOrderConsentIsUniquePerOrder` — segundo `Add` con el mismo `OrderId` → `DbUpdateException` cuya `UniqueViolation.Is(exception, "PK_order_consents")` (o la excepción que traduzca `StoreUnitOfWork`; no se agrega traducción: el handler nunca escribe dos veces, P11).
- [ ] **Step 2: Run, expect FAIL** (compilación).
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StorePersistenceTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Entidad, puerto, repositorio, mapeo (`OwnsOne(value => value.Consent, …)` con los nombres de columna de arriba y `Navigation(...).IsRequired()`), registro `services.AddScoped<IStoreOrderConsents, StoreOrderConsentRepository>()`, y la migración:
  `dotnet ef migrations add AddStoreOrderConsents --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`
  Comentario en la entidad: D9, consentimiento del comprador invitado; el de una cuenta vive en `buyers.consents`; plazo de conservación pendiente del owner.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(store): persist the guest consent per order`.

### Task 5: Quotations — clave por `clientRequestId`, pedido sin referencia, historial del sistema y conteo del tope

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:467-496` (`CreateFromStore`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderChangeSummary.cs:17-18`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IStoreOrderGateway.cs:48-65` (`StoreOrderDraft`), `:80-90` (interfaz), `:110-111`, `:150-228`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs:44-47`, `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs:85-90`
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreOrders.cs:19-30`, `:47-71`
- Modify: `src/Bootstrapper/StoreOrdersAdapter.cs:74-120`
- Modify: `src/Modules/Store/Modules.Store.Application/TakeOrderRequest.cs:112-176` (adaptación mínima: `orders.FindExistingAsync(tenantId, request.ClientRequestId, …)`, draft sin `RequestId`/`RequestNumber`/`TakerUserId`, con `request.ClientRequestId`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (el doble de `IOrderRepository`, si existe, gana `CountBySourceSinceAsync`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderStoreFactoryTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderChangeSummaryTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/StoreOrderGatewayTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/StoreOrdersAdapterTests.cs`

**Interfaces:**
- Produces (Quotations.Application):
  - `StoreOrderDraft(Guid TenantId, Guid ClientRequestId, Guid ClientId, Guid AdvisorMembershipId, bool IsRetail, QuotationBillingAccountRequest BillingAccount, StoreOrderParty Billing, StoreOrderParty Shipping, bool BillingWithRetention, bool BillingVatSurplus, IReadOnlyList<StoreQuoteLine> Lines, string? Notes)`.
  - `public static string ExternalKeyFor(Guid clientRequestId) => $"store-checkout:{clientRequestId}";`
  - `Task<StoreOrderResult?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);`
  - `Task<int> CountStoreOrdersSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);` → `orderRepository.CountBySourceSinceAsync(tenantId, OrderSource.Store, since, ct)`.
  - `IOrderRepository.CountBySourceSinceAsync(Guid tenantId, OrderSource source, DateTimeOffset since, CancellationToken cancellationToken)` → `COUNT` de `Orders` con `TenantId`, `Source` y `ConvertedAt >= since`, sin filtrar estado.
  - `Order.CreateFromStore(OrderId id, Guid tenantId, string orderNumber, OrderHeader header, QuotationParties parties, QuotationBillingAccount billingAccount, IReadOnlyCollection<OrderLineInput> lines, string? notes, MemberId createdBy, DateTimeOffset occurredAt, string externalKey)` — `externalReference: null`, sin `ThrowIfNullOrWhiteSpace(requestNumber)`.
  - `OrderChangeSummary.CreatedFromStore()` → `"Creado desde la tienda."`.
  - En `StoreOrderGateway.CreateAsync`: historial `OrderHistoryEntry.Create(…, OrderHistoryEventType.Created, memberId: null, OrderChangeSummary.CreatedFromStore(), now)`; auditoría `auditPublisher.PublishByIntegration(draft.TenantId, "quotation.order.created", order.Id.ToString(), "success", now)`.
- Produces (Store.Application `IStoreOrders`):
  - `StoreOrderDraft(Guid TenantId, Guid ClientRequestId, Guid ClientId, Guid AdvisorMembershipId, bool IsRetail, StoreBillingAccount BillingAccount, StoreOrderPartyInput Billing, StoreOrderPartyInput Shipping, bool BillingWithRetention, bool BillingVatSurplus, IReadOnlyList<StoreOrderLineInput> Lines, string? Notes)`.
  - `StoreOrderCreated(Guid OrderId, string OrderNumber, decimal Total, string Currency, Guid ClientId, Guid AdvisorMembershipId, bool AlreadyExisted)`.
  - `Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);`
  - `Task<int> CountCreatedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);`
  - `CreateAsync` traduce `QuotationsDomainException` a `StoreDomainException(StoreErrorCodes.LineUnavailable, …, inner)` con el mismo `LogQuoteRejected` (o uno hermano `LogCreateRejected`, EventId 6002) que `QuoteAsync`.

- [ ] **Step 1: Write the failing tests.**
  - `OrderStoreFactoryTests`: `NewStoreOrder` usa la firma nueva con `"store-checkout:abc"`; `CreateFromStoreSetsSourceReferenceAndKey` pasa a afirmar `(OrderSource.Store, OrderPaymentStatus.PaymentPending, null, "store-checkout:abc")` para `(Source, PaymentStatus, ExternalReference, ExternalKey)`.
  - `OrderChangeSummaryTests`: `CreatedFromStoreNamesNoRequest` → `"Creado desde la tienda."`.
  - `StoreOrderGatewayTests` (integración): `Draft(...)` pierde `takerUserId` y `requestId` pasa a `clientRequestId`; en `CreateMakesAPendingDeliveredStoreOrderForAnIncompleteCustomer` el historial dice `"Creado desde la tienda."` y `created.MemberId is null`; en `CreatePublishesOrderCreatedAndAudits` `actorType == "Integration"` y el payload **no** contiene `ownerUserId`; `CreateIsIdempotentByRequest` → `CreateIsIdempotentByClientRequestId` (`draft with { ClientRequestId = … }` para el tercero); nuevas: `TheExternalKeyCarriesTheClientRequestId` (`order.ExternalKey == $"store-checkout:{clientRequestId}"`, `order.ExternalReference is null`), `FindExistingFindsByClientRequestId`, `CountBySourceSinceCountsCancelledStoreOrders` (dos pedidos de tienda, uno anulado por la API de pedidos —usa el helper de anulación de `QuotationsApiHarness` o la ruta que usan las pruebas de pedidos—, un manual: `CountStoreOrdersSinceAsync(tenant, Now - 1 día) == 2`; con `since` futuro, `0`).
  - `StoreOrdersAdapterTests`: `AQuotationEngineRejectionOnCreateBecomesLineUnavailableAndLogsItsCode` (gateway que lanza `QuotationsDomainException("quotation.item.product_inactive", …)` en `CreateAsync` → `StoreDomainException` con `LineUnavailable`, `InnerException` igual, log `Warning` con el código y sin el mensaje). El `ThrowingGateway` del archivo gana los métodos nuevos (`CountStoreOrdersSinceAsync` → `NotSupportedException`).
- [ ] **Step 2: Run, expect FAIL** (compilación en los tres proyectos).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderStoreFactoryTests|FullyQualifiedName~OrderChangeSummaryTests|FullyQualifiedName~StoreOrderGatewayTests"`
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~StoreOrdersAdapterTests"`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~StoreOrderGatewayTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement** en el orden Quotations → Store port → adaptador → `TakeOrderRequestHandler` (sólo lo necesario para compilar: `FindExistingAsync(tenantId, request.ClientRequestId, …)` y el draft nuevo con `request.ClientRequestId`). Actualiza el comentario de `Order.CreateFromStore` y el de `IStoreOrders.CreateAsync` («los errores del motor se traducen: el visitante anónimo nunca ve `quotation.*`»).
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreTakeApiTests|FullyQualifiedName~StoreOrderDetailApiTests" -- xUnit.MaxParallelThreads=4`. Donde una de esas pruebas afirme `ExternalReference == "T-0001"`, el texto viejo del historial, `MemberId == advisor` o el actor humano de la auditoría, cambia la aserción al valor nuevo (ambas clases se borran en la Tarea 9).
- [ ] **Step 5: Commit** — `feat(quotations): key store orders by client request id and record them as system-created`.

### Task 6: Customers → Store — resolución por documento y caída a incompleto

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs:140-141` (interfaz) y `:306-307` (implementación): `Guid actorUserId` → `Guid? actorUserId`; la auditoría `Completed` usa `PublishByIntegration` cuando es `null` (misma regla que `CreateCompleteAsync`)
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:326-327` (`FakeCustomerProvisioning.CompleteFromStoreAsync` con `Guid?`) y cualquier otro doble de `ICustomerProvisioning` (`rg "ICustomerProvisioning" tests`)
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreCustomerDirectory.cs`
- Modify: `src/Bootstrapper/StoreCustomerDirectory.cs`
- Modify: `src/Modules/Store/Modules.Store.Application/TakeOrderRequest.cs:128-156` (adaptación mínima al resultado tipado y al actor nullable)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/StoreCustomerDirectoryDocumentTests.cs`, `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerProvisioningTests.cs`

**Interfaces:**
- Produces (Store.Application):
  - `public enum StoreCustomerCreationOutcome { Created, ExistingByDocument, DocumentOwnerUnavailable, NotCompletable }`
  - `public sealed record StoreCustomerCreation(StoreCustomerRef? Customer, StoreCustomerCreationOutcome Outcome);` — `Customer` es null sólo con `DocumentOwnerUnavailable` y `NotCompletable`.
  - `Task<StoreCustomerRef?> FindByDocumentAsync(Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken);` — sólo activos y sin fusionar (la misma búsqueda por documento de `FindCandidatesAsync`, con `TryKey`).
  - `Task<StoreCustomerCreation> CreateAsync(Guid tenantId, Guid? actorUserId, StoreNewCustomer data, CancellationToken cancellationToken);` — `provisioning.CreateCompleteAsync(tenantId, data, ct, actorUserId, "store")`; `Created` → `Created`; `Created = false` y el dueño activo → `ExistingByDocument`; dueño inactivo/fusionado → `DocumentOwnerUnavailable`; `ValidationException` o `CustomersDomainException` con código en `{classification_not_found, city_not_found, document_type_not_allowed}` → `NotCompletable`. Todo lo demás se propaga.
  - `Task<StoreCustomerRef> CreateIncompleteAsync(Guid tenantId, string name, string? phone, string? email, string? country, CancellationToken cancellationToken);` → `provisioning.CreateIncompleteAsync` y relectura con `ToRef`.
  - `Task<StoreCustomerRef> CompleteAsync(Guid tenantId, Guid? actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken);` — P5: no completable → relee y devuelve al cliente como está; `identification_taken` → relee por documento y devuelve al dueño (si no hay dueño activo, devuelve al cliente como está); `customers.customer.not_found` → `InvalidOperationException` (el handler lo encontró un instante antes).
  - `StoreAddressOutcome` gana `Skipped`; `EnsureAddressesAsync` devuelve `Skipped` para todas ante `CustomersDomainException` con `customers.customer.incomplete` (P6).
  - `NotFound()` (`store.order_request.customer_not_found`) deja de lanzarse en `CreateAsync`/`CompleteAsync`; `FindEligibleAsync`, `FindCandidatesAsync` y `KnownAsync` quedan como están hasta la Tarea 11.

- [ ] **Step 1: Write the failing tests.**
  - `StoreCustomerDirectoryDocumentTests` (con `FakeCustomerRepository` y `FakeCustomerProvisioning` de `ShopifyAdaptersTestDoubles`, extendidos si falta algo: `FakeCustomerProvisioning` gana `Exception? CreateCompleteThrows` y `Exception? CompleteFromStoreThrows`, y `FakeCustomerRepository` tiene que devolver por `FindAsync` a los clientes que le siembras, también a los que crea el fake de provisión —agrega un `Func<Guid, Customer>` o registra el cliente creado):
    - `FindByDocumentFindsAnActiveCustomer` → `Id` del español sembrado; `FindByDocumentIgnoresAnInactiveOwner` (cliente desactivado) → `null`.
    - `CreateReturnsCreatedForANewDocument` → `Outcome == Created`.
    - `CreateReturnsExistingByDocumentWhenAnotherActiveCustomerOwnsIt` (`Created = false`, dueño activo) → `ExistingByDocument` con el `Id` del dueño.
    - `CreateReturnsDocumentOwnerUnavailableWhenTheOwnerIsInactive` → `DocumentOwnerUnavailable`, `Customer is null`.
    - `CreateReturnsNotCompletableForTheDocumentedCodes` (`Theory`: `ValidationException`, `classification_not_found`, `city_not_found`, `document_type_not_allowed`) → `NotCompletable`; `CreatePropagatesOtherErrors` (`CustomersDomainException("customers.customer.identification_type_invalid")`) → se propaga.
    - `CompleteReturnsTheCustomerUnchangedWhenCustomersCannotCompleteIt` (lanza `city_not_found`) → `IsComplete == false`, sin excepción.
    - `EnsureAddressesSkipsAllForAnIncompleteCustomer` (lanza `customers.customer.incomplete`) → `[Skipped, Skipped]`.
    - Los dos tests existentes del archivo se conservan hasta la Tarea 11.
  - `CustomerProvisioningTests`: `CompleteFromStoreWithoutActorAuditsAsIntegration` — `actorUserId: null` → la auditoría `Completed` sale por `PublishByIntegration` (mira cómo el archivo afirma el actor en `CreateCompleteAsync` sin actor y copia la forma).
- [ ] **Step 2: Run, expect FAIL** (compilación).
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~StoreCustomerDirectoryDocumentTests"`
  `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerProvisioningTests"`
- [ ] **Step 3: Implement.** Customers primero, después el puerto y el adaptador, y por último `TakeOrderRequestHandler`: `CreateAsync` → `switch (creation.Outcome)`: `Created`/`ExistingByDocument` siguen como hoy (`ExistingByDocument` incompleto se completa), `DocumentOwnerUnavailable` lanza el `CustomerNotFound()` del propio handler (conserva el 422 de «Tomar» hasta su borrado), `NotCompletable` lanza `StoreDomainException(StoreErrorCodes.ValidationFailed, …)`. `caller.UserId` sigue viajando como actor.
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyAdaptersTests"` y `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreTakeApiTests" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** — `feat(store): resolve customers by document with an incomplete fallback`.

### Task 7: Buyers — el alta de cuenta viaja con `OrderId`

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/StoreIntegrationEvents.cs:15-32` (`OrderRequestId` → `OrderId`; doc: «idempotente por `OrderId`»)
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs:359-372` (sólo el nombre del argumento; sigue pasando `request.Id` hasta la Tarea 8)
- Modify: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Signup/BuyerSignupRequestedMessage.cs:11-33`, `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/BuyerInboxMessage.cs:17-26`, `src/Modules/Buyers/Modules.Buyers.Infrastructure/Signup/BuyerSignupRequestedWorker.cs:146-176`, `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/BuyersDbContext.cs:161-168`, `src/Modules/Buyers/Modules.Buyers.Infrastructure/BuyerHousekeepingWorker.cs:176` (comentario)
- Create: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/Migrations/<timestamp>_RenameProcessedSignupOrderId.cs` (+ Designer y snapshot)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs:29-63`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerSignupRequestedWorkerTests.cs`, `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerHousekeepingTests.cs:299,386`

**Interfaces:**
- Produces: `StoreBuyerSignupRequestedPayload(Guid OrderId, Guid TenantId, Guid ClientRequestId, …)`, `BuyerSignupRequestedMessage(Guid OrderId, …)`, `BuyerProcessedSignup.OrderId`, columna `buyers.processed_signups.order_id`. `BuyerParityTests.TheSignupEventContractMatches` sigue comparando los nombres de propiedad de los dos records, así que un lado renombrado y el otro no falla.

- [ ] **Step 1: Write the failing tests.** En `BuyerParityTests`, el payload se arma con el parámetro `OrderId` y se afirma `payload.OrderId == message.OrderId`. En `BuyerSignupRequestedWorkerTests`, `Payload(Guid orderId, …)` serializa `orderId`; `"""{"orderId":"not-a-guid"}"""`; la consulta de `ASignupThatLosesTheEmailRaceStillMarksItsMessageProcessed` pasa a `WHERE order_id = $1`. En `BuyerHousekeepingTests`, los `INSERT` usan `order_id`.
- [ ] **Step 2: Run, expect FAIL** (compilación en Bootstrapper.UnitTests; `42703` en Buyers).
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~BuyerParityTests"`
  `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter "FullyQualifiedName~BuyerSignupRequestedWorkerTests|FullyQualifiedName~BuyerHousekeepingTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Renombres, `HasColumnName("order_id")`, y la migración:
  `dotnet ef migrations add RenameProcessedSignupOrderId --project src/Modules/Buyers/Modules.Buyers.Infrastructure --context BuyersDbContext -o Persistence/Migrations`
  Si el scaffold generó `DropColumn` + `AddColumn`, reemplázalo a mano por `migrationBuilder.RenameColumn(name: "order_request_id", schema: "buyers", table: "processed_signups", newName: "order_id")` y su inverso en `Down`; la PK conserva el nombre `PK_processed_signups`.
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Modules/Store/Modules.Store.UnitTests` completo (compila con el argumento renombrado).
- [ ] **Step 5: Commit** — `refactor(buyers): key the checkout signup by order id`.

### Task 8: El checkout crea el pedido

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs:181-485` (handler completo; validador y records del cuerpo no cambian)
- Modify: `src/Modules/Store/Modules.Store.Application/StoreAuditActions.cs` (`OrderCreated = "store.order.created"`, `Resources.Order = "order"`)
- Modify: `tests/Modules/Store/Modules.Store.UnitTests/StoreSettingsTestDoubles.cs` (`FakeDomains.FindByHostAsync` devuelve el dominio sembrado; `FakeCatalog.SellableVariantsAsync` devuelve lo sembrado; `FakeUnitOfWork` sigue con un solo `SaveChangesAsync`)
- Create: `tests/Modules/Store/Modules.Store.UnitTests/CheckoutRig.cs`, `tests/Modules/Store/Modules.Store.UnitTests/SubmitOrderRequestHandlerTests.cs`
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderRequestApiTests.cs` (reescrita), `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs:252-560` (cuenta `quotations.orders` en vez de `store.order_requests`; el payload lleva `orderId`), `tests/Modules/Store/Modules.Store.IntegrationTests/StoreOrderDetailApiTests.cs` (sólo `TheOrdersListFiltersByStoreSource`, ahora con un envío público; la prueba del `storeRequest` se borra acá)
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreApiHarness.cs` / `PublicStoreFixture.cs` (helpers: `OrderRowsAsync`, `LoadOrderAsync(orderId)` vía `IOrderRepository`, `CustomerRowsAsync`, `ConsentAsync(orderId)`)

**Interfaces:**
- Consumes: Task 2 (`IsActiveMemberAsync`, `FindOwnerMembershipIdAsync`), Task 4 (`IStoreOrderConsents.Add`, `StoreOrderConsent.Create`), Task 5 (`IStoreOrders.FindExistingAsync/CountCreatedSinceAsync/CreateAsync`, `StoreOrderDraft`, `StoreOrderCreated.Currency`), Task 6 (`FindByDocumentAsync`, `CreateAsync` → `StoreCustomerCreation`, `CreateIncompleteAsync`, `CompleteAsync`, `EnsureAddressesAsync` con `Skipped`), Task 7 (`StoreBuyerSignupRequestedPayload.OrderId`).
- Produces: constructor nuevo del handler:
  `SubmitOrderRequestHandler(StorefrontResolver resolver, IStoreGeography geography, IStorePhoneNormalizer phones, ICaptchaVerifier captcha, PublicCartPricing pricing, IStoreOrders orders, IStoreMemberDirectory members, IStoreCustomerDirectory customers, IStoreOrderConsents consents, IStoreUnitOfWork unitOfWork, IStoreAuditPublisher auditPublisher, IClock clock, StoreOptions options, IValidator<PublicOrderRequestBody> validator, IStoreBuyerAccounts buyers, IStoreOutbox outbox, ILogger<SubmitOrderRequestHandler> logger)`.
  Métodos privados con firma fija (las pruebas unitarias los ejercen por el `HandleAsync`): `Task<Guid> ResolveAdvisorAsync(OnlineStore store, CancellationToken ct)`; `Task<StoreCustomerRef> ResolveCustomerAsync(Guid tenantId, StoreRequestBilling billing, CancellationToken ct)`; `Task EnsureAddressesAsync(Guid tenantId, StoreCustomerRef customer, StoreRequestBilling billing, StoreRequestShipping? shipping, CancellationToken ct)`; `Task RecordAfterOrderAsync(OnlineStore store, StoreOrderCreated order, PublicOrderRequestBody body, StoreConsent consent, PendingSignup? signup, DateTimeOffset now, CancellationToken ct)`. `BillingData`, `AddressInputs`, `BillingParty`, `ShippingParty` se traen de `TakeOrderRequestHandler` como estáticos privados sobre `StoreRequestBilling`/`StoreRequestShipping` (en la Tarea 9 desaparecen de allá). `StoreOrderDraft.ClientRequestId = body.ClientRequestId`, `IsRetail = store.PricingMode == StorePricingMode.RetailOnly`, `Notes = body.Note`. `Receipt(StoreOrderCreated o) => new(o.OrderId, o.OrderNumber, o.Total, o.Currency)`.
  Logs nuevos (`LoggerMessage`, sin datos del visitante): `LogAdvisorFallback(Warning, "Store advisor {MembershipId} of tenant {TenantId} is no longer active; the store order goes to the owner.")`, `LogCustomerCreatedIncomplete(Information, "...tenant {TenantId} ... outcome {Outcome}")`, `LogAddressesSkipped(Information, "... order {OrderId} ...")`.

- [ ] **Step 1: Write the failing unit tests.** `CheckoutRig` arma el handler real sobre fakes: `FakeDomains` sembrado con `StoreDomain` del host `acme.lulocrm.com` → `TenantId`; `FakeStores` con `ConfiguredStore()` publicada (`Publish(Member, Now)`), `FakeTenants`, `FakeTenantModules.AllEnabled()`; `FakeGeography` (una ciudad `CityId` en `DepartmentId`); `FakePhones` (`ToE164` devuelve `"+57" + dígitos` si el país es `CO`, null para `"12345"`); `FakeCaptcha` (`Passes`, `Calls`); `FakeCatalog` con `SellableVariantsAsync` que devuelve la variante sembrada; `FakeOrders : IStoreOrders` (`QuoteAsync` devuelve una `StoreQuoteView` fija de total 20 000; `Existing` para `FindExistingAsync`; `Count` para `CountCreatedSinceAsync`; `CreateAsync` guarda el draft en `Drafts` y devuelve `StoreOrderCreated("PED-0001", total 21 000, "COP", draft.ClientId, draft.AdvisorMembershipId, AlreadyExisted)`; `CreateThrows` para simular el motor); `FakeMembers` de la Tarea 3, extendido con `Guid Owner` para `FindOwnerMembershipIdAsync`; `FakeCustomers : IStoreCustomerDirectory` (`ByDocument`, `CreateOutcome`, `CreatedIncomplete`, `Completed`, `AddressOutcomes`, listas `CreateCalls/CompleteCalls/IncompleteCalls/AddressCalls`); `FakeConsents` (lista); `FakeAudit` con `PublishAnonymous` que registra; `FakeOutbox` (lista de `(EventName, Payload)`); `FakeBuyers` (`HashPassword` → `"hash"`, `ReplaceProfileAsync` → `true`); `RecordingLogger`. `Body(...)` como en `PublicOrderRequestApiTests` pero tipado (`PublicOrderRequestBody`).
  Pruebas en `SubmitOrderRequestHandlerTests` (cada una afirma el orden con `rig.Log.Entries` donde aplica):
  - `ARejectedCurrencyWritesNothingAndCallsNoCaptcha` → `store.public.currency_invalid`, `Captcha.Calls == 0`, `Orders.Drafts` vacío.
  - `ARetryReturnsTheExistingOrderWithoutCaptchaCustomerOrWrites` — `Orders.Existing` sembrado → recibo con `OrderId`/`Number`/`Total`/`Currency` del existente, `Created == false`, `Captcha.Calls == 0`, `CreateCalls` vacío, `Consents` vacío, `Outbox` vacío.
  - `TheDailyCapIsCheckedOnStoreOrders` — `Orders.Count = 500` → `RequestRateLimitedException` con `store.order_request.daily_cap`; `CreateCalls` vacío.
  - `AnUnavailableLineIs422BeforeTouchingTheCustomer` — `FakeCatalog` sin la variante → `line_unavailable`, `CreateCalls` y `IncompleteCalls` vacíos, `Captcha.Calls == 1`. (`currency_not_in_store` no tiene prueba unitaria: `OnlineStore.Sells` y `BillingAccountFor` leen la misma lista y toda `StoreCurrency` lleva cuenta, así que el caso es inalcanzable por el dominio; el chequeo queda como guarda defensiva y su status lo fija `StoreErrorMappingApiTests`.)
  - `AConfiguredActiveAdvisorIsUsed` — tienda con `AdvisorMembershipId = Member`, `Active = {Member}` → `Drafts[0].AdvisorMembershipId == Member`, sin `Warning`.
  - `AConfiguredAdvisorThatIsNoLongerActiveFallsBackToTheOwnerWithAWarning` — `Active = {}`, `Owner = OtherMember` → `OtherMember`, un log `Warning` que contiene el `MembershipId` y no contiene el email del visitante.
  - `WithoutAConfiguredAdvisorTheOwnerIsUsed` — sin `Warning`.
  - `AnExistingCompleteCustomerIsUsedUntouched` — `ByDocument = complete` → `Drafts[0].ClientId == customer.Id`, `CreateCalls`, `CompleteCalls`, `IncompleteCalls` vacíos; `WithRetention`/`VatSurplus` del cliente llegan al draft.
  - `AnExistingIncompleteCustomerIsCompleted` — `ByDocument = incomplete` → un `CompleteCalls` con `actorUserId == null` y los datos de facturación.
  - `AMissingCustomerIsCreatedComplete` — `CreateOutcome = Created` → `CreateCalls[0].ActorUserId == null`, `ClassificationId == null`.
  - `WhenCustomersRejectsTheCompleteProfileAnIncompleteIsCreatedAndTheOrderFollows` — `CreateOutcome = NotCompletable` → `IncompleteCalls[0] == (name, phone, email, country)`, `Drafts.Count == 1`.
  - `ADocumentOfAnInactiveCustomerCreatesANewIncompleteWithoutDocument` — `CreateOutcome = DocumentOwnerUnavailable` → igual que la anterior; `CompleteCalls` vacío.
  - `ARaceOnTheDocumentUsesTheWinnerAndCompletesIt` — `CreateOutcome = ExistingByDocument` con cliente incompleto → `CompleteCalls.Count == 1`.
  - `BillingAndADifferentShippingMakeTwoAddresses` → `AddressCalls[0].Count == 2`; `WithoutShippingThereIsOneAddress` → `1`; `SkippedAddressesDoNotStopTheOrder` (`AddressOutcomes = [Skipped]`) → `Drafts.Count == 1`, un log `Information`.
  - `TheOrderDraftCarriesTheKeyAndTheParties` → `Drafts[0].ClientRequestId == body.ClientRequestId`, `IsRetail == true`, `Billing.IdentificationNumber == "123456"`, `Shipping.IdentificationNumber is null`, `Shipping.Email == billing email`, `Notes == body.Note`.
  - `AfterTheOrderStoreRecordsConsentAuditAndSignupInOneSave` — `createAccount` → `Consents` con `OrderId` del pedido, `Consent.Ip == visitorIp`, `PolicyUrl == store.DataPolicyUrl`; `Audit` con `(store.order.created, "order", orderId)`; `Outbox` con `store.buyer-signup-requested.v1` y payload `OrderId == orderId`; `UnitOfWork.Saves == 1`.
  - `ALostKeyRaceReturnsTheWinnerAndWritesNothingInStore` — `AlreadyExisted = true` → recibo del ganador, `Consents` vacío, `Outbox` vacío, `Saves == 0`.
  - `TheReceiptIsTheOrder` → `(Id, Number, Total, Currency) == ("<orderId>", "PED-0001", 21_000m, "COP")`.
- [ ] **Step 2: Run, expect FAIL** (compilación: constructor nuevo).
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~SubmitOrderRequestHandlerTests"`
- [ ] **Step 3: Implement the handler** con el orden de §4.3 del spec (pasos 1-15). Las lecturas de `requests`/`numbers`/`BeginTransactionAsync` desaparecen; la única escritura de Store es `RecordAfterOrderAsync` → `unitOfWork.SaveChangesAsync`. `SubmitOrderRequestValidatorTests` sigue como está (sólo valida el cuerpo).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Write the failing integration tests** — `PublicOrderRequestApiTests` reescrita (conserva `Rig`, `Body`, `Billing`, `Shipping`, `SubmitAsync`, `AssertRejectedAsync`; `RequestsAsync` → `OrdersAsync` sobre `quotations.orders`; `LoadAsync` → `LoadOrderAsync(orderId)` por `IOrderRepository.FindByIdAsync`; `AssertNextNumberIsFirstAsync` → el siguiente `201` tiene un `number` que termina en `0001`):
  - `ASubmissionCreatesAStoreOrderWithAdvisorCustomerAndConsent` — `201`; `receipt.number` termina en `0001`; `receipt.total` == `order.Total`; `order.Source == Store`, `PaymentStatus == PaymentPending`, `AdvisorId == OwnerMembershipId` del tenant, `ExternalKey == $"store-checkout:{clientRequestId}"`, `ExternalReference is null`; un cliente nuevo en `customers.customers` con `identification_number = '123456'`; `store.order_consents` con `order_id == receipt.id`, `visitor_ip == "198.51.100.23"`, `policy_url == "https://demo.test/politica"`; `audit.entries` con `store.order.created` y `resource_id == receipt.id` (usa `CountAuditEntriesAsync`).
  - `AConfiguredAdvisorReceivesTheOrder` — `SeedMemberAsync` + `PUT` de ajustes con `advisorMembershipId` (antes de publicar: `OpenRigAsync(tweak: (fixture, body) => body["advisorMembershipId"] = …)`; siembra el miembro en el `tweak` con `.GetAwaiter().GetResult()` o siembra antes y pasa el id) → `order.AdvisorId == advisor`.
  - `AnAdvisorRemovedAfterBeingConfiguredFallsBackToTheOwner` — configurado, luego `Membership.Remove` por `TenancyDbContext` → `201`, `AdvisorId == owner`.
  - `TheSameClientRequestIdReturnsTheSameOrder` — mismo `id` y `number`, `OrdersAsync == 1`.
  - `ARetryNamingAnotherCurrencyGetsTheFirstOrderInItsOwnCurrency` (`TwoCurrencies`, `eurPrice: true`) → `currency == "EUR"`, `OrdersAsync == 1`.
  - `TwoSimultaneousSubmissionsOfTheSameIdCreateOneOrder` — cuatro envíos → todos `201`, un `number`, `OrdersAsync == 1`, `SELECT count(*) FROM store.order_consents == 1`.
  - `AnExistingCompleteCustomerIsReusedAndNotModified` — `SeedCustomerAsync` con documento `CC 123456` y nombre `"Lucía Gómez"` → `order.ClientId == customerId`, el nombre en `customers.customers` sigue `"Lucía Gómez"`, `CustomerRowsAsync` sin cambio.
  - `ADocumentOfAnInactiveCustomerCreatesANewIncompleteCustomerWithoutDocument` — `SeedCustomerAsync` + `DeactivateCustomerAsync` → `201`; `customers.customers` +1; el nuevo tiene `identification_number IS NULL` y `completeness = 'Incomplete'` (comprueba el nombre real de la columna en `CustomersDbContext`); el inactivo no cambió.
  - `ABillingAndShippingAddressGoToTheCustomerBook` — con `shipping` → dos filas en la libreta del cliente (`customers.customer_addresses` o la tabla que use Customers; confírmala en `CustomersDbContext`).
  - `ASubmissionWithAStaleLineIs422AndCreatesNoCustomer` → `line_unavailable`, `OrdersAsync == 0`, `CustomerRowsAsync` sin cambio.
  - `TheDailyCapCountsCancelledOrders` — cap 2; primer pedido, anúlalo por `POST /api/v1/tenants/{tenant}/orders/{id}/cancel` (o la ruta de anulación que usen las pruebas de Quotations) con `fixture.Settings.Admin`; segundo `201`; tercero `429 daily_cap`.
  - `CreateAccountEmitsTheSignupWithTheOrderId` — `createAccount` → una fila `store.buyer-signup-requested.v1` con `payload->>'orderId' == receipt.id`.
  - `AQuotationEngineErrorOnCreateIsLineUnavailable` — opcional si encuentras un camino reproducible por la API (p. ej. desactivar la variante entre el precio y la creación no es posible en serie); si no, queda cubierto por `StoreOrdersAdapterTests`.
  - Se conservan (adaptadas a `OrdersAsync`): `WithoutPolicyTheConsentIsStillRequired`, `AFailedCaptchaWritesNothingAndConsumesNoNumber`, `TurnstileEnabledWithoutAKeyFailsClosed`, `WithoutConsentIs422`, `APhoneWithoutCountryCodeThatDoesNotNormalizeIs422`, `ABodyOver64KbIs413`, `ASubmissionInACurrencyOutsideTheStoreWritesNothing`, `AMissingCurrencyIs422WithTheStoreCode`, `ABillingCountryTheStoreDoesNotSellToIs422`, `AShippingCountryTheStoreDoesNotSellToIs422`, `ACityOfAnotherDepartmentIs422`, `AnUnknownCityIs422`, `AForeignBlockWithDivipolaIdsIs422`, `AForeignBillingWithACityTextIsAccepted` (afirma `order.Billing.Country == "ES"`, `CityName == "Madrid"`), `ASpanishNifIsAccepted` (afirma `order.Billing.IdentificationType == PartyIdentificationType.Nif`), `AColombianTypeForSpainIs422AndWritesNothing`, `ASubmissionWithoutEmailIs422WithTheField`, `AMissingVisitorIpIs422AndDoesNotDrainOtherVisitors`, `ASubmissionWithoutTheKeyIs401`, `TheSubmitLimiterAnswers429WithRetryAfterAndBucketsAreIsolatedPerVisitor`, `TheVisitorIpHeaderIsIgnoredWithoutTheKey`, `ThePublicRoutesIgnoreTheSessionCookie`. Se borran: `ASubmissionFreezesTheRepricedLinesAndConsent`, `AStoreWithoutPolicyFreezesTheConsentWithoutPolicy` (reemplazadas por la de consentimiento sobre `store.order_consents`, con una variante `AStoreWithoutPolicyRecordsTheConsentWithoutPolicy`), `ThePublicEndpointNeverCreatesACustomer`, `ASubmissionStoresItsCurrency` (→ `order.Currency == "EUR"`), `ASubmissionFreezesBillingAndShipping` (→ `order.Shipping.Name == "Luis Gómez"`).
  - `PublicOrderBuyerAccountApiTests`: `store.order_requests` → `quotations.orders`; `payload["orderRequestId"]` → `payload["orderId"]`; la lectura de `row_to_json` de `:252` pasa a `store.order_consents`.
  - `StoreOrderDetailApiTests`: queda sólo `TheOrdersListFiltersByStoreSource`, armada con `PublicStoreFixture` y un envío público (sin `StoreInboxRig`).
- [ ] **Step 6: Run, expect FAIL** por aserciones (ya compila).
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicOrderRequestApiTests|FullyQualifiedName~PublicOrderBuyerAccountApiTests|FullyQualifiedName~StoreOrderDetailApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 7: Fix what the integration run reveals** (harness, siembras, nombres de columnas) sin cambiar el contrato.
- [ ] **Step 8: Run, expect PASS.**
- [ ] **Step 9: Commit** — `feat(store): create the order directly from the public checkout`.

### Task 9: Borrar la bandeja — Store, DI, opciones y migración `DropStoreOrderRequests`

**Files:**
- Delete (Domain): `StoreOrderRequest.cs`, `StoreOrderRequestLine.cs`, `StoreOrderRequestStatus.cs`, `StoreDiscardReason.cs`
- Delete (Application): `IStoreOrderRequestRepository.cs`, `IStoreRequestNumberGenerator.cs`, `ListOrderRequests.cs`, `GetOrderRequestSummary.cs`, `GetOrderRequest.cs`, `GetCustomerCandidates.cs`, `TakeOrderRequest.cs`, `DiscardOrderRequest.cs`, `OrderRequestRowComposer.cs`, `StoreInboxDtos.cs`, `StoreReadModels.cs`, `StoreNotFound.cs`
- Modify (Application): `StoreErrorCodes.cs:33-40` (borrar `OrderRequestNotFound`, `NotNew`, `BeingTaken`, `OrderRequestConflict`, `DuplicateClientRequest`, `CustomerNotFound`, `CustomerDocumentTaken`; conservar `CurrencyNotInStore`), `StoreAuditActions.cs` (borrar `RequestReceived`, `RequestConverted`, `RequestDiscarded`, `Resources.OrderRequest`), `StorePermissions.cs` (borrar `OrderManage` y su comentario), `StoreAuthorization.cs` (comentario de la bandeja), `StoreOptions.cs:11-12,34-37` (borrar `ConversionLease`), `SubmitOrderRequest.cs:84` (`StoreOrderRequest.NoteMaxLength` → constante propia `NoteMaxLength = 1000` en el validador)
- Delete (Infrastructure): `Persistence/StoreOrderRequestRepository.cs`, `Persistence/StoreRequestNumberGenerator.cs`
- Modify (Infrastructure): `StoreDbContext.cs` (borrar `OrderRequests`, `HasShippingProperty`, `ConfigureOrderRequest`, `ConfigurePlace`, `ConfigureLines`, `ConfigureRequestCounter`, `StoreRequestCounter`, el bucle de `SyncDerivedColumns` sobre `StoreOrderRequest`), `StoreUnitOfWork.cs` (borrar `ClientRequestIndex`, la rama de `StoreOrderRequest` en `DbUpdateConcurrencyException` y la de `DuplicateClientRequest`), `StoreInfrastructureExtensions.cs:32-33`
- Create: `Persistence/Migrations/<timestamp>_DropStoreOrderRequests.cs` (+ Designer y snapshot)
- Modify (Api): `StoreEndpoints.cs:61-131` (grupo `inbox` y sus métodos), comentario `:13-14`
- Modify (Bootstrapper): `QepServiceCollectionExtensions.cs:1384` (`OrderRequestRowComposer`), `:1414-1431` (cinco registros), `src/Bootstrapper/StoreCustomerDirectory.cs` (los `catch` que traducían a `CustomerDocumentTaken`/`OrderRequestConflict`/`NotFound()` se quitan; `RequestConcurrencyException` se propaga; `customers.customer.not_found` en `EnsureAddressesAsync` → `InvalidOperationException`)
- Modify: `src/Api/appsettings.json:58`, `src/Api/appsettings.example.json:101` (quitar `ConversionLease`)
- Delete (tests): `StoreInboxApiTests.cs`, `StoreInboxRig.cs`, `StoreTakeApiTests.cs`, `DiscardOrderRequestHandlerTests.cs`, `DiscardOrderRequestValidatorTests.cs`, `StoreInboxFilterTests.cs`, `StoreOrderRequestTests.cs`
- Modify (tests): `StoreTestData.cs:14,56-93` (borrar `Lease`, `Billing`, `Shipping`, `Lines`, `Consent`, `Request` salvo lo que use `SubmitOrderRequestHandlerTests`/`CheckoutRig`), `StoreOptionsTests.cs:20,111-117`, `StoreErrorMappingApiTests.cs:27-28,35-36` (filas de `NotNew` y `OrderRequestNotFound`; agrega una de `RequestRateLimitedException(StoreErrorCodes.DailyCap, …)` → 429 si no existe), `StorePersistenceTests.cs` (borrar todo lo de solicitudes, contador y `TheBillingPhoneIndexExists`; agregar `TheRequestTablesAreGone`), `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:633-634` (quitar la aserción de `OrderManage`), `StoreApiHarness.cs` (`SeedCallerMembershipAsync` sólo si nadie más lo usa)

**Interfaces:**
- Produces: migración `DropStoreOrderRequests` con `Up`: `migrationBuilder.DropTable("order_request_lines", "store"); DropTable("order_requests", "store"); DropTable("request_counters", "store");` y `Down` que lanza `NotSupportedException("The store inbox was removed (spec 2026-11-09, D6).")`.

- [ ] **Step 1: Write the failing tests** en `StorePersistenceTests`: `TheRequestTablesAreGone` — `SELECT count(*) FROM information_schema.tables WHERE table_schema = 'store' AND table_name IN ('order_requests','order_request_lines','request_counters')` == `0`; `TheStoreModelHasNoRequestEntity` — `StoreDbContext.Model.FindEntityType("Modules.Store.Domain.StoreOrderRequest") is null`.
- [ ] **Step 2: Run, expect FAIL** (`3` tablas; el tipo existe).
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StorePersistenceTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Delete and adjust** en el orden Api → Bootstrapper → Application → Infrastructure → Domain → pruebas, compilando con `dotnet build` al final de cada capa. Migración:
  `dotnet ef migrations add DropStoreOrderRequests --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext -o Persistence/Migrations`
  Revisa que el `Up` sólo borre las tres tablas (los índices y CHECK caen con ellas) y reemplaza el `Down` generado por el `NotSupportedException`.
- [ ] **Step 4: Run, expect PASS**: `dotnet test tests/Modules/Store/Modules.Store.UnitTests`, `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StoreLayerTests|FullyQualifiedName~CompositionRootTests"`, y en integración `--filter "FullyQualifiedName~StorePersistenceTests|FullyQualifiedName~StoreErrorMappingApiTests|FullyQualifiedName~StoreAccessApiTests" -- xUnit.MaxParallelThreads=4`. `rg -n "order_request|OrderRequest" src --glob '!**/Migrations/**' --glob '!**/bin/**' --glob '!**/obj/**'` sólo puede devolver `PublicOrderRequestBody`, `PublicOrderRequestReceiptDto`, `SubmitOrderRequest*`, `PublicOrderRequestBodyValidator`, la ruta `/order-requests` y los códigos `store.order_request.*`.
- [ ] **Step 5: Commit** — `refactor(store)!: remove the store order request inbox`.

### Task 10: Borrar la solicitud del detalle del pedido y de la limpieza de Catalog

**Files:**
- Delete: `src/Modules/Quotations/Modules.Quotations.Application/IOrderStoreRequestLookup.cs`, `src/Bootstrapper/OrderStoreRequestLookup.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/ProductDeletionStoreCleanupTests.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:300-323` (quitar `StoreRequest` y `OrderStoreRequestResponse`), `OrderResponseComposer.cs:13-19,52-55,68-80`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:1386-1387`, `src/Bootstrapper/CatalogProductDeletionCleanup.cs:11-30` (comentario), `:57-74` (borrar el `ExecuteSqlAsync` de la tienda; `ApplyCoreAsync` queda sólo con `quotationLines.ApplyAsync`)
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs` (borrar las tres pruebas de `StoreRequest` y `RecordingOrderStoreRequestLookup`; conserva una que afirme que el detalle de un pedido `Store` compone sin lookup extra), `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:1422` (`NoOrderStoreRequestLookup`), `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs` si lo referencia, README `:1015-1017`

**Interfaces:**
- Produces: `OrderDetailResponse` sin `StoreRequest` (último parámetro `int[] AvailableFloors`); `OrderResponseComposer(IQuotationCustomerLookup, IQuotationAdvisorLookup, IQuotationProductLookup, IQuotationCompanyLookup)`.

- [ ] **Step 1: Write the failing test** en `OrderResponseComposerTests`: `TheDetailResponseHasNoStoreRequestProperty` — `typeof(OrderDetailResponse).GetProperty("StoreRequest") is null`.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderResponseComposerTests"`
- [ ] **Step 3: Delete and adjust.** El comentario de `CatalogProductDeletionCleanup` deja de nombrar `store.*`: el pedido de la tienda ya es un `Order`, que `DeletedVariantLineStore` cubre.
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ProductDeletionCleanupTests|FullyQualifiedName~OrderDetail" -- xUnit.MaxParallelThreads=4` y `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductDeletion" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** — `refactor(quotations)!: drop the store request from the order detail`.

### Task 11: Puertos sin uso y aserción de arquitectura

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreCustomerDirectory.cs` (borrar `StoreCustomerCandidate`, `FindCandidatesAsync`, `KnownAsync`, `FindEligibleAsync`), `src/Bootstrapper/StoreCustomerDirectory.cs` (sus implementaciones, `MaxPhoneMatches`, `Phone`, `Document`), `src/Modules/Store/Modules.Store.Application/IStoreMemberDirectory.cs` (`ResolveCallerAsync` si ya no lo usa nadie: `rg ResolveCallerAsync src`), `src/Bootstrapper/StoreMemberDirectory.cs` (ídem; si se quita, el constructor deja `IExecutionContext` e `IMembershipDirectory`)
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/StoreCustomerDirectoryDocumentTests.cs` (borrar `ACandidateWithANifIsFoundByItsDocument` y `AKnownKeyWithANifMatchesByDocument`; `FindByDocumentFindsAnActiveCustomer` pasa a usar el NIF para conservar la razón del archivo)
- Modify: `tests/ArchitectureTests/ArchitectureTests/StoreLayerTests.cs`

- [ ] **Step 1: Write the failing test** en `StoreLayerTests`: `ApplicationDoesNotReferenceTenancyDomainTypesForTheOwner` — hoy `Modules.Store.Application` referencia `Modules.Tenancy.Domain` (`StoreAuthorization` usa `TenantModuleKeys`), así que la aserción por assembly no distingue; en su lugar: ningún tipo de `Modules.Store.Application` tiene un campo, propiedad, parámetro de constructor ni de método cuyo tipo sea `Modules.Tenancy.Domain.Tenant` o `Modules.Tenancy.Domain.Membership` (recorre `typeof(StorePermissions).Assembly.GetTypes()` con reflexión). Falla si alguien inyecta el agregado en vez del puerto.
- [ ] **Step 2: Run, expect PASS en verde desde el inicio** (es una guarda, no una regresión): documenta en el reporte que se ejecutó y el conteo de tipos revisados; para verla roja una vez, agrega temporalmente un parámetro `Tenant` a un record de prueba y revierte.
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StoreLayerTests"`
- [ ] **Step 3: Delete the unused members** y compila; `rg -n "FindCandidatesAsync|KnownAsync|FindEligibleAsync|StoreCustomerCandidate" src tests` tiene que quedar vacío.
- [ ] **Step 4: Run, expect PASS**: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`, `dotnet test tests/ArchitectureTests/ArchitectureTests`.
- [ ] **Step 5: Commit** — `refactor(store): drop the customer dialog ports`.

### Task 12: README, corrida completa y cierre

**Files:**
- Modify: `README.md:1000-1017` (sección «Tienda pública»: quitar bandeja/«Tomar»/«Descartar» y `quotations.order.manage`; el pedido se crea en el checkout con asesor configurado u owner; el detalle ya no trae `storeRequest`), `README.md:1040-1060` (tabla de códigos: `store.order_request.currency_not_in_store` como 422 público; `store.settings.advisor_not_found` 422), `README.md:1170` (recibo de ejemplo con `"number": "PED-2026-0001"` o el formato de numeración por defecto del repo), `README.md:1183` (fila del evento: «en la misma transacción de Store posterior al pedido; idempotente por `orderId`»), y el párrafo de «Ajustes» con `advisorMembershipId`/`advisorName`.

- [ ] **Step 1: Edit the README** con los cambios de arriba. No toques `docs/estado.md`.
- [ ] **Step 2: Run the full suites, one project at a time** (Docker arriba, `$env:Seed__Enabled = "false"`):
  `dotnet build`
  `dotnet test tests/ArchitectureTests/ArchitectureTests`
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`
  `dotnet test tests/Modules/Store/Modules.Store.UnitTests`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`
  `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`
  `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests -- xUnit.MaxParallelThreads=4`
  Expected: todo en verde; pega el resumen de cada corrida en el reporte.
- [ ] **Step 3: Verify the migrations apply over a database at the previous head**: `dotnet ef migrations list --project src/Modules/Store/Modules.Store.Infrastructure --context StoreDbContext` muestra `AddStoreAdvisor`, `AddStoreOrderConsents`, `DropStoreOrderRequests` después de `MakeStoreConsentPolicyOptional`; lo mismo para Buyers con `RenameProcessedSignupOrderId`.
- [ ] **Step 4: Commit** — `docs(readme): describe direct store orders and the advisor setting`.
- [ ] **Step 5: Handoff.** Resume en el reporte: la rama, los cuatro commits con migración, y la nota para el merge: **al mergear a `develop`, actualizar `docs/estado.md`** (fila de la tienda pública: pedidos directos construidos, bandeja eliminada; registro con el merge, y «orden de salida: backend → frontend → storefront»).

---

## Nota final (no es una tarea de la rama)

`docs/estado.md` se actualiza en `develop`, al mergear, en el mismo merge o en un commit `docs:` inmediato: fila «Tienda pública» → pedidos creados directamente desde el checkout (`SubmitOrderRequestHandler`, migraciones `AddStoreAdvisor`, `AddStoreOrderConsents`, `DropStoreOrderRequests`, `RenameProcessedSignupOrderId`), bandeja eliminada, ajuste de asesor; compañeros `qep-frontend` y `qep-storefront` con sus specs del 2026-11-09; orden de salida backend → frontend → storefront.
