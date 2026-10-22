# Cliente identificado en POS — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el cajero busque o dé de alta rápida (nombre, documento, teléfono opcional) al cliente de una venta POS y
que `POST /pos/sales` lo guarde en el snapshot de la venta, sin que una venta a Consumidor final cambie en nada.

**Architecture:** Permiso nuevo `pos.sale.customer` que exige `pos` **y** `customers` (el enmascarado lo apaga solo). Pos
habla con Customers por un puerto (`IPosCustomerLookup`) con adaptador en Bootstrapper: las lecturas van por
`ICustomerRepository` y el alta por `ICustomerProvisioning`, como `ShopifyCustomerDirectory`. Dos endpoints nuevos
(`GET`/`POST /pos/customers`), `customerId` opcional en la venta (entra a la huella sólo si viene) y un índice parcial.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `../qep-frontend/docs/superpowers/specs/2026-10-14-pos-cliente-identificado-design.md` (léelo entero, incluida
la sección «Cambios del owner (2026-10-22)»). El frontend tiene su propio plan en
`../qep-frontend/docs/superpowers/plans/2026-10-22-pos-cliente-identificado-frontend.md` y depende del contrato de la
sección «Contrato para el frontend» al final de este plan.

**Rama:** `feature/pos-cliente-identificado` desde `develop`, en el worktree `.claude/worktrees/pos-cliente-identificado`
(`git worktree add .claude/worktrees/pos-cliente-identificado -b feature/pos-cliente-identificado develop`). Antes de
**cada** tarea y de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`,
para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea.
- Conventional commits en español, con el slice `POSC-NN` en el asunto, **sin atribución de IA** (sin `Co-Authored-By`;
  la regla del repo pisa cualquier recordatorio del harness).
- No corras la suite completa: cada comando lleva `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de
  integración. `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `PosAuthorization.EnsureAuthorized` en el
  handler antes de tocar un puerto. Otro tenant → 403, nunca 404. Todo método de puerto y de repositorio recibe `tenantId`.
- Permiso nuevo = **dos mitades** en `QepServiceCollectionExtensions`: `PermissionDefinition` y `AddPolicy`. Sin la
  política el síntoma es 500. Los handlers se registran a mano; sin registro, 500.
- `Modules.Pos.Application` no referencia Customers (`PosLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`).
  Todo lo de Customers entra por `IPosCustomerLookup`; el adaptador vive en `src/Bootstrapper/`.
- Texto libre en un comando → validador de FluentValidation (422 `validation.failed` con `errors`) aunque el dominio
  valide. Ningún status a mano: lo mapea `src/Api/ApiExceptionHandler.cs`.
- La huella de una venta **sin** `customerId` no cambia ni un byte (spec, decisión 6).
- Sin FK de `pos.sales` a `customers` (spec, decisión 8). Migración con el factory de diseño:
  `dotnet ef migrations add AddPosSaleCustomerIndex --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations`.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma.
- Textos para personas y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual.
- `docs/estado.md` **no se toca** en esta rama: se actualiza en `develop` al mergear.
- `Directory.Packages.props`, `global.json`, `.mcp.json`: no se tocan. Sin paquetes nuevos.

## Desviaciones del spec (verificadas contra el código el 2026-10-22)

La autoridad es el código. Estas son las diferencias entre el spec y lo que este plan construye; ninguna cambia una
decisión del spec. B1, B3 y B7 las aprobó el owner el 2026-10-22; B2 se resolvió con su respuesta de agregar `source`.

| # | Spec dice | El código dice | El plan hace |
|---|---|---|---|
| B1 | El adaptador pide una página mayor a `SearchAsync` y filtra activos con documento | Messaging y Shopify crean muchos incompletos **sin documento**; los 20-60 más recientes pueden ser todos de WhatsApp y el selector quedaría vacío sin búsqueda | Método nuevo `ICustomerRepository.SearchSelectableAsync` que filtra en SQL con el mismo orden de `SearchAsync` (Task 2). **Aprobado por el owner 2026-10-22** |
| B2 | El adaptador crea con `ICustomerRepository` + `ICustomersUnitOfWork` y audita con `source: "pos"` | El precedente de altas desde otro módulo es `ICustomerProvisioning` (`CustomerProvisioning.cs`); `ICustomersAuditPublisher.Publish` no tiene campo `source` | `ICustomerProvisioning.CreateFromRegisterAsync` con el **cajero como actor**, y `source` opcional en `ICustomersAuditPublisher.Publish` y en `CreateFromRegisterAsync` (hoy el payload fija `"customers"`; precedente: `PosAuditPublisher` ya publica `"pos"`): el alta de la caja audita `source: "pos"` y los demás llamadores no cambian (Task 1 y Task 1b). **Aprobado por el owner 2026-10-22** (agregar el campo) |
| B3 | 422 `pos.customer.identification_taken` «con `{ existing }`»; la caja lo dice si `existing` está inactivo | `ApiExceptionHandler` sólo publica `code`, `traceId` y `errors` (`ApiExceptionHandler.cs:58-75`); `PosCustomerRef` no tiene `IsActive` | Interfaz `IHasProblemExtensions` en BuildingBlocks; el ref y su DTO llevan `IsActive` (Task 5). **Aprobado por el owner 2026-10-22** (`IHasProblemExtensions` en BuildingBlocks / `ApiExceptionHandler`, `ApiError.extensions` en el frontend) |
| B4 | `PosSale.Create` recibe un `PosCustomerSnapshot?` | `PosCustomerSnapshot` es de Application y `PosSale` es Domain | Record de dominio `PosSaleCustomer`; el handler mapea (Task 7) |
| B5 | Pasos del handler «6» y «8», repetición en «paso 4» | `CreatePosSale.cs:90-181`: repetición = paso 2, líneas = 4, descuento = 5, `PosSale.Create` = 6 | El cliente es el paso 5b, entre el descuento (`:133-137`) y `PosSale.Create` (`:141-147`) |
| B6 | Líneas citadas: roles `:914-919`/`:1025-1039`, definiciones `:1315-1356`, políticas `:1706-1723`, adaptadores `:693-695`, `PosCustomerResponse` en `PosDtos.cs:127` | Hoy: admin `QepServiceCollectionExtensions.cs:1118-1125`, cashier `:1242-1256`, definiciones `:1532-1573`, políticas `:1963-1980`, adaptadores `:873-877`, `PosCustomerResponse` en `PosDtos.cs:161` | Este plan cita las líneas de hoy |
| B7 | Spec sin texto fijado: sufijo «La venta vuelve a Consumidor final.» de los cuatro `pos.sale.customer_*`, «Ese documento ya existe. Búscalo en la lista.» para la carrera de dos cajas, y nombre y descripción visibles del permiso | El plan propone nombre «Elegir o crear el cliente de una venta» y descripción «Permite buscar el cliente de una venta del punto de venta o darlo de alta rápida.» (Task 3); los mensajes los pinta el frontend | Se mantienen tal cual. **Aprobado por el owner 2026-10-22** |

## Review Focus

1. **Venta a Consumidor final durante el despliegue.** Un reintento en vuelo con el cuerpo viejo (sin `customerId`) tiene
   la misma huella que antes del cambio y devuelve 200 con la venta ya creada, nunca `pos.sale.id_conflict`. Prueba:
   `WithoutACustomerTheFingerprintIsTheLiteralFromBeforeTheChange` (Task 6).
2. **Reintento de una venta con cliente después de desactivar al cliente.** La repetición se reconoce (paso 2) antes de
   mirar al cliente: 200 con la venta, no 422 `customer_inactive`. Prueba: `ARepeatWithACustomerIsRecognizedBeforeTheCustomerChecks` (Task 7).
3. **Tenant con POS pero sin Clientes.** `pos.sale.customer` se enmascara: 403 en `GET` y `POST /pos/customers`, 403
   `pos.sale.customer_not_allowed` en una venta con `customerId`, y la venta sin cliente sigue funcionando. Prueba:
   `WithoutTheCustomersModuleTheCustomerPathIs403AndPlainSalesStillWork` (Task 5).
4. **Alta rápida con el documento de un cliente desactivado.** 422 `pos.customer.identification_taken` con
   `existing.isActive = false`, sin crear nada. Prueba: `QuickCreateWithTheDocumentOfAnInactiveCustomerReturnsItAsExisting` (Task 5).
5. **Selector sin búsqueda en un tenant lleno de contactos de WhatsApp.** Los 20 que vuelven son activos con documento,
   aunque los 60 clientes más nuevos no tengan documento. Prueba: `SelectableSearchSkipsTheNewestWithoutDocument` (Task 2).

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` | Modificar | `CreateFromRegister`, junto a `CreateIncomplete` (`:327-356`) |
| `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs` | Modificar | `RegisterCustomerData`, `CreateFromRegisterAsync` |
| `src/Modules/Customers/Modules.Customers.Application/ICustomersAuditPublisher.cs`, `…/Infrastructure/Persistence/CustomersAuditPublisher.cs` | Modificar | `source` opcional en `Publish` (Task 1b) |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs` | Modificar | `SearchSelectableAsync` |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` | Modificar | Implementación sobre `FilteredQuery` (`:32-60`) |
| `src/BuildingBlocks/BuildingBlocks.Domain/IHasProblemExtensions.cs` | Crear | Extensiones extra del ProblemDetails |
| `src/Api/ApiExceptionHandler.cs` | Modificar | Publica `IHasProblemExtensions` (después de `errors`, `:61-75`) |
| `src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs` | Modificar | `SaleCustomer` |
| `src/Modules/Pos/Modules.Pos.Application/IPosCustomerLookup.cs` | Crear | Puerto y records |
| `src/Modules/Pos/Modules.Pos.Application/PosCustomers.cs` | Crear | Query/command, validadores, handlers, excepción con `existing` |
| `src/Modules/Pos/Modules.Pos.Application/PosDtos.cs` | Modificar | `PosCustomerOptionResponse`, `PosCustomerOptionsResponse` (el request de alta va en `PosEndpoints.cs`) |
| `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs` | Modificar | `CustomerId` en comando, validador y paso 5b |
| `src/Modules/Pos/Modules.Pos.Application/PosSaleFingerprint.cs` | Modificar | Cuarto elemento sólo con cliente |
| `src/Modules/Pos/Modules.Pos.Domain/PosSaleInputs.cs` | Modificar | `PosSaleCustomer` |
| `src/Modules/Pos/Modules.Pos.Domain/PosSale.cs` | Modificar | `Create(..., PosSaleCustomer? customer = null)` (`:75-128`) |
| `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs` | Modificar | Índice parcial en `ConfigureSale` (`:138-145`) |
| `…/Persistence/Migrations/*_AddPosSaleCustomerIndex.cs` | Crear (ef) | El índice |
| `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs` | Modificar | Dos rutas, `CustomerId` en `CreatePosSaleRequest` (`:238-242`) |
| `src/Bootstrapper/PosCustomerLookup.cs` | Crear | Adaptador Customers → Pos |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers, adaptador, permiso (dos mitades), roles |
| `README.md` | Modificar | Tabla «POS: caja y ventas» (`:1598-1611`) |
| `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerProvisioningTests.cs` | Modificar | Fábrica y alta |
| `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWhatsAppDirectoryRaceTests.cs` (`NoAudit`), `CustomerProvisioningTests.cs` (`RecordingAudit`), `EngagementTestBed.cs` (`RecordingAudit`) | Modificar | Firma de `Publish` con `string? source = null` (Task 1b) |
| `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWhatsAppDirectoryRaceTests.cs`, `EngagementTestBed.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs` | Modificar | Dobles de `ICustomerRepository` / `ICustomerProvisioning` con los métodos nuevos (`throw new NotSupportedException()`) |
| `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerSelectableSearchTests.cs` | Crear | `SearchSelectableAsync` contra PostgreSQL |
| `tests/Bootstrapper/Bootstrapper.UnitTests/PosCustomerLookupTests.cs` | Crear | Adaptador |
| `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs` | Modificar | `FakeCustomerLookup`, `PosTestBed.Customers` |
| `tests/Modules/Pos/Modules.Pos.UnitTests/PosCustomerHandlersTests.cs` | Crear | Handlers de `/pos/customers` |
| `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs`, `CreatePosSaleHandlerTests.cs`, `PosSaleTests.cs` | Modificar | Huella, handler, dominio |
| `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs` | Modificar | `SaleCustomer` en `CashierPermissions`, `DisableModuleAsync`, `CustomersUrl` |
| `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCustomerApiTests.cs` | Crear | Endpoints y venta con cliente |
| `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs` | Modificar | El índice |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | Modificar | Módulos y roles del permiso |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` | Modificar | El catálogo (`:113-160`) |

**Comandos de prueba:**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests"
```

---

### Task 1 (POSC-01): `Customer.CreateFromRegister` y el alta en `ICustomerProvisioning`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (después de `CreateIncomplete`, `:327-356`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs` (interfaz `:37-53`, clase `:55-200`)
- Modify (dobles): `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs:231` (`FakeCustomerProvisioning`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerProvisioningTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Customer.cs — spec «Dominio de Customers»: la ficha que da de alta la caja.
  public static Customer CreateFromRegister(
      CustomerId id, Guid tenantId, string name, CustomerIdentification identification, string? phone,
      DateTimeOffset now, IPhoneNumberNormalizer phoneNormalizer);
  // CustomerProvisioning.cs
  public sealed record RegisterCustomerData(string Name, string IdentificationType, string IdentificationNumber, string? Phone);
  // ICustomerProvisioning (y CustomerProvisioning):
  Task<ProvisionedCustomer> CreateFromRegisterAsync(
      Guid tenantId, Guid actorId, RegisterCustomerData data, CancellationToken cancellationToken);
  ```
- Reglas de `CreateFromRegister`: `Completeness = Incomplete`, `IsActive = true`, `Cuc = null`, `Country = ColombiaCountryCode`
  (`"CO"`, el default del formulario web), sin clasificación, ciudad, email ni dirección; nombre por `NormalizeName`
  (≤ 160); identificación por `identification.Normalized()` (mismos códigos que `Create`); teléfono recortado, vacío →
  `null`, más de `CustomerContactInfo.PhoneMaxLength` (32) → `CustomersDomainException("customers.customer.phone_too_long")`;
  `PhoneE164 = phone is null ? null : phoneNormalizer.ToE164(phone, "CO")`. `Version = 1`.
- Reglas de `CreateFromRegisterAsync`: `CustomerMapping.ToIdentification(type, number)` (lanza
  `customers.customer.identification_type_invalid`), `repository.Add`, `auditPublisher.Publish(tenantId, actorId,
  CustomerAuditActions.Created, id, "success", now)` (hay persona detrás: no es `PublishByIntegration`) y
  `unitOfWork.SaveChangesAsync`. **No** se traga `customers.customer.identification_taken`: la carrera sube al llamador
  (spec, «Errores y casos borde»), a diferencia de `CreateCompleteAsync`. Devuelve `ProvisionedCustomer(id, IsComplete: false, IsActive: true, Created: true)`.
- El `source` de la auditoría lo agrega la Task 1b; esta tarea audita con el `source` por defecto del módulo.
- Actualiza el `<summary>` de `ICustomerProvisioning` (`:33-36`): ya no es sólo «sin persona detrás»; la caja da altas con actor.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `CustomerTests.cs`:
```csharp
[Fact] public void CreateFromRegisterIsAnActiveIncompleteWithDocumentAndNoCuc()
// Customer.CreateFromRegister(id, Tenant, "  Laura Gómez ", new() { Type = IdentificationType.Cc, Number = " 1020304050 " }, " 3001234567 ", Now, normalizer)
// → Name "Laura Gómez"; IdentificationType Cc; IdentificationNumber "1020304050"; Completeness Incomplete; IsActive;
//   Cuc null; Country "CO"; Phone "3001234567"; PhoneE164 = lo que devuelve el doble para ("3001234567","CO");
//   ClassificationId null; Address null; Email null; Addresses vacía; Version 1
[Fact] public void CreateFromRegisterWithoutPhoneHasNoPhoneE164()          // phone "  " → Phone null, PhoneE164 null
[Fact] public void CreateFromRegisterRejectsABadName()   // "  " → customers.customer.name_required; 161 caracteres → customers.customer.name_too_long (NormalizeName, Customer.cs:819-826)
[Fact] public void CreateFromRegisterRejectsAnEmptyOrTooLongDocument()     // "" → identification_number_required; 33 → identification_number_too_long
[Fact] public void CreateFromRegisterRejectsAPhoneLongerThan32()           // 33 caracteres → customers.customer.phone_too_long
[Fact] public void ARegisterCustomerCanBeCompletedLater()                  // CreateFromRegister + Complete(...) con otra identificación → IsComplete, identificación nueva
```
En `CustomerProvisioningTests.cs` (con los dobles del archivo):
```csharp
[Fact] public async Task CreateFromRegisterAddsAnIncompleteAndAuditsTheCashierAsActor()
// CreateFromRegisterAsync(Tenant, cashierId, new("Laura Gómez", "CC", "1020304050", null)) → repositorio con 1 cliente incompleto;
// el publisher registró (Tenant, cashierId, CustomerAuditActions.Created, id, "success") por Publish, no por PublishByIntegration;
// resultado (IsComplete false, IsActive true, Created true)
[Fact] public async Task CreateFromRegisterLetsTheIdentificationRaceThrough()
// la unidad de trabajo lanza CustomersDomainException("customers.customer.identification_taken") → la misma excepción sale
[Fact] public async Task CreateFromRegisterRejectsAnUnknownDocumentType()  // "RUT" → customers.customer.identification_type_invalid, nada agregado
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerTests|FullyQualifiedName~CustomerProvisioningTests"`
Expected: FAIL de compilación (`CreateFromRegister`, `CreateFromRegisterAsync`, `RegisterCustomerData` no existen).

- [ ] **Step 3: Implementa la fábrica, el record y el método** con las reglas de Interfaces. `FakeCustomerProvisioning`
  (Shopify) implementa el método nuevo con `throw new NotSupportedException()`.

- [ ] **Step 4: Corre y verifica que pasan** (mismo comando, más `Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyAdaptersTests"` para el doble).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers/Modules.Customers.UnitTests tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs
git commit -m "feat(customers): POSC-01 alta de ficha incompleta con documento desde la caja"
```

---

### Task 1b (POSC-09): `source` opcional en la auditoría de Clientes

Aprobado por el owner el 2026-10-22 (respuesta 3): las fichas creadas desde la caja se auditan con `source: "pos"`.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomersAuditPublisher.cs` (`Publish`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersAuditPublisher.cs` (`Publish`, `Add`; el record `AuditEventPayload` ya tiene `source`, hoy fijo en `"customers"`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs` (`ICustomerProvisioning.CreateFromRegisterAsync` y su implementación)
- Modify (dobles): `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWhatsAppDirectoryRaceTests.cs` (`NoAudit`), `CustomerProvisioningTests.cs` (`RecordingAudit`), `EngagementTestBed.cs` (`RecordingAudit`), y `FakeCustomerProvisioning` en `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs`
- Test: `CustomerProvisioningTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCustomerApiTests.cs` (la parte de integración vive en la Task 5, que depende de esta)

**Interfaces:**
- Precedente: el payload `platform.audit.recorded.v1` ya lleva `source` y `PosAuditPublisher.cs:21` publica `"pos"`; en Clientes está fijo en `"customers"`. `PublishByIntegration` no cambia.
- Produces:
  ```csharp
  // ICustomersAuditPublisher.Publish — source opcional al final: null = el módulo que audita ("customers"), como hoy.
  void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome,
      DateTimeOffset occurredAt, string? source = null);
  // ICustomerProvisioning.CreateFromRegisterAsync (Task 1) suma el parámetro y lo pasa a Publish.
  Task<ProvisionedCustomer> CreateFromRegisterAsync(
      Guid tenantId, Guid actorId, RegisterCustomerData data, string? source, CancellationToken cancellationToken);
  ```
- `CustomersAuditPublisher.Add` recibe `string? source` y escribe `source ?? "customers"` en el payload. Los llamadores existentes (`CreateCustomer`, `UpdateCustomer`, importación, etc.) no se tocan: el parámetro es opcional y sale el mismo JSON de hoy.
- Los dobles que implementan `ICustomersAuditPublisher` suman `string? source = null` a `Publish` (un implementador de una interfaz con parámetro opcional debe declararlo); `RecordingAudit` de `CustomerProvisioningTests` guarda el `source` recibido.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `CustomerProvisioningTests.cs`:
```csharp
[Fact] public async Task CreateFromRegisterAuditsWithTheGivenSource()
// CreateFromRegisterAsync(Tenant, cashierId, data, "pos", ct) → RecordingAudit registró source "pos"
[Fact] public async Task CreateFromRegisterWithoutSourceAuditsWithTheDefault()
// source null → RecordingAudit registró source null (el publisher real lo resuelve a "customers")
[Fact] public async Task OtherProvisioningPathsKeepAuditingWithoutSource()
// CreateCompleteAsync / CreateIncompleteAsync existentes → source null; sus pruebas actuales no cambian
```
Prueba del publisher real (integración de Clientes, junto a `CustomerProvisioningIntegrationTests`):
```csharp
[Fact] public async Task TheAuditPayloadCarriesTheGivenSourceAndDefaultsToCustomers()
// Publish(..., source: "pos") → platform.outbox_messages payload->>'source' = 'pos'; Publish(...) sin source → 'customers'
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerProvisioningTests"`
Expected: FAIL de compilación (el parámetro `source` no existe).

- [ ] **Step 3: Implementa** el parámetro en la interfaz, el publisher y `CreateFromRegisterAsync`; actualiza los dobles y los llamadores de `CreateFromRegisterAsync` de la Task 1 (`null` donde no haya origen). La Task 4 pasa `"pos"` desde `PosCustomerLookup`.

- [ ] **Step 4: Corre y verifica que pasan** (`Customers.UnitTests`, `Customers.IntegrationTests --filter "FullyQualifiedName~CustomerProvisioningIntegrationTests"` y `Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyAdaptersTests"`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs
git commit -m "feat(customers): POSC-09 source opcional en la auditoría de Clientes"
```

---

### Task 2 (POSC-02): `ICustomerRepository.SearchSelectableAsync`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs` (junto a `SearchAsync`, `:25-35`)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (junto a `SearchAsync`, `:105-190`)
- Modify (dobles): `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWhatsAppDirectoryRaceTests.cs`,
  `CustomerProvisioningTests.cs`, `EngagementTestBed.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerSelectableSearchTests.cs` (con `CustomersApiHarness`)

**Interfaces:**
- Produces:
  ```csharp
  /// Desviación B1 del plan POS: los que la caja puede elegir — activos y con documento — filtrados en la consulta y no
  /// después, porque los incompletos de WhatsApp (sin documento) son los más nuevos y llenarían la página.
  Task<IReadOnlyList<Customer>> SearchSelectableAsync(Guid tenantId, string? search, int take, CancellationToken cancellationToken);
  ```
- Implementación: `FilteredQuery(tenantId, LikePattern(search), null, null, null)` + `IsActive && IdentificationNumber != null`;
  el mismo orden que `SearchAsync` con `search` (similitud trigram de nombre + documento + CUC) y, sin `search`,
  `CreatedAt` descendente; luego `ThenBy(Name).ThenBy(Id)`, `Take(take)`. Sin `CountAsync`. El cuerpo del orden se
  extrae a un método privado compartido con `SearchAsync` sólo si EF lo sigue traduciendo (si no, se repite, como ya
  explica el comentario de `:28-31`).

- [ ] **Step 1: Escribe las pruebas que fallan** (siembra por SQL o por la API de clientes del harness)

```csharp
[Fact] public async Task SelectableSearchSkipsTheNewestWithoutDocument()
// 1 cliente completo viejo + 60 incompletos más nuevos sin documento (CreateIncomplete) → SearchSelectableAsync(t, null, 20) devuelve sólo el completo
[Fact] public async Task SelectableSearchExcludesInactiveCustomers()          // completo inactivo con "Laura" → búsqueda "Laura" no lo trae
[Fact] public async Task SelectableSearchMatchesNameDocumentOrCucAndKeepsTheTenant()
// "1020304050" encuentra por documento; parte del CUC encuentra; el mismo documento en otro tenant no aparece
[Fact] public async Task SelectableSearchIncludesAnIncompleteWithDocument()   // uno creado con CreateFromRegister aparece, IsComplete false
[Fact] public async Task SelectableSearchWithoutTextReturnsTheNewestFirstUpToTake() // 25 elegibles → 20, el más nuevo primero
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerSelectableSearchTests"`
Expected: FAIL de compilación (`SearchSelectableAsync` no existe).

- [ ] **Step 3: Implementa el método** y agrega `throw new NotSupportedException()` en los cuatro dobles de `ICustomerRepository`.

- [ ] **Step 4: Corre y verifica que pasan** (más `Customers.UnitTests` y `Bootstrapper.UnitTests` con filtro por las clases de los dobles).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTestDoubles.cs
git commit -m "feat(customers): POSC-02 búsqueda de clientes elegibles para la caja"
```

---

### Task 3 (POSC-03): Permiso `pos.sale.customer`

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs` (`:8-21`; el `<summary>` de `:3-7` dice «los seis»: pasa a «los siete», y que éste exige además `customers`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: admin `:1118-1125`, comentario y permisos de cashier
  `:1242-1256`, definición después de `:1567-1573`, política después de `:1979-1980`
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (`:401-428`),
  `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` (`:113-160`)

**Interfaces:**
- Produces: `PosPermissions.SaleCustomer = "pos.sale.customer"` con `<summary>` «Elegir o crear el cliente de una venta
  (spec 2026-10-14). Exige `pos` **y** `customers`: sin Clientes, el enmascarado lo quita y la caja vende a Consumidor final».
- Nombre visible del spec («Permiso nuevo»). `PermissionDefinition(PosPermissions.SaleCustomer, "Elegir o crear el cliente de una venta", "Permite buscar el cliente de una venta del punto de venta o darlo de alta rápida.", "Pos", "medium", RequiredModules: [ModuleKeys.Pos, ModuleKeys.Customers])`.
- `admin` suma `SaleCustomer` a los seis; `cashier` queda con `SaleRead`, `SaleCreate`, `SaleCustomer`, `RegisterOperate`.
  El comentario de `:1242-1245` cambia «el MVP no elige cliente» por «elige o crea el cliente de la venta por
  `/pos/customers`, sin el padrón de Clientes (spec 2026-10-14, decisión 4)». Sin migración: los roles de sistema se
  resuelven en tiempo de ejecución.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `CompositionRootTests.cs`:
```csharp
[Fact] public void ThePosCustomerPermissionRequiresPosAndCustomersAndGoesToAdminAndCashier()
// RequiredModules == [ModuleKeys.Pos, ModuleKeys.Customers] (en ese orden);
// catalog.PermissionsFor("admin") y ("cashier") lo contienen; ("advisor") y ("billing") no
```
`EveryPosPermissionRequiresExactlyThePosModule` (`:407`) no cambia: su lista no incluye el permiso nuevo, y el `<summary>`
pasa a decir «los seis de la caja; `pos.sale.customer` tiene su propia prueba».

En `AuthorizationCatalogApiTests.TheCatalogNamesThePosPermissionsAndTheCashierRole`: `expected` suma
`new("pos.sale.customer", "Elegir o crear el cliente de una venta", "Permite buscar el cliente de una venta del punto de venta o darlo de alta rápida.", "Pos", "medium")`
(en orden ordinal, entre `pos.sale.create` y `pos.sale.discount`), y el rol `cashier` pasa a
`["pos.register.operate", "pos.sale.create", "pos.sale.customer", "pos.sale.read"]`. Actualiza el `<summary>` (siete permisos).

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL de compilación (`PosPermissions.SaleCustomer`).

- [ ] **Step 3: Implementa** la constante, la definición, la política (`AddPermissionRequirement`) y los dos roles.

- [ ] **Step 4: Corre y verifica que pasan** (`CompositionRootTests` y `AuthorizationCatalogApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pos/Modules.Pos.Application/PosPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests tests/Modules/Tenancy
git commit -m "feat(pos): POSC-03 permiso pos.sale.customer para admin y cajero"
```

---

### Task 4 (POSC-04): Puerto `IPosCustomerLookup` y adaptador `PosCustomerLookup`

**Files:**
- Create: `src/Modules/Pos/Modules.Pos.Application/IPosCustomerLookup.cs`
- Create: `src/Bootstrapper/PosCustomerLookup.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:873-877` (registro junto a `PosProductLookup`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/PosCustomerLookupTests.cs` (dobles de `ICustomerRepository` e
  `ICustomerProvisioning` propios del archivo, como `PosProductLookupTests`)

**Interfaces:**
- Produces (Pos.Application):
  ```csharp
  public sealed record PosCustomerRef(
      Guid Id, string Name, string IdentificationType, string IdentificationNumber, bool IsComplete, bool IsActive);
  public sealed record PosCustomerSnapshot(
      Guid Id, string Name, string? IdentificationType, string? IdentificationNumber, bool IsActive);
  public sealed record NewPosCustomer(string Name, string IdentificationType, string IdentificationNumber, string? Phone);
  public interface IPosCustomerLookup
  {
      Task<IReadOnlyList<PosCustomerRef>> SearchAsync(Guid tenantId, string? search, int take, CancellationToken ct);
      Task<PosCustomerSnapshot?> FindAsync(Guid tenantId, Guid customerId, CancellationToken ct);
      Task<PosCustomerRef?> FindByIdentificationAsync(Guid tenantId, string type, string number, CancellationToken ct);
      Task<PosCustomerRef> CreateFromRegisterAsync(Guid tenantId, NewPosCustomer input, CancellationToken ct);
  }
  public static class PosCustomerRules
  {
      public const int SearchMaxLength = 120;
      public const int Take = 20;
      // Copia de IdentificationTypeParser.SupportedWireValues: Pos.Application no ve Customers. La prueba
      // PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule las compara.
      public static readonly IReadOnlyList<string> IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];
  }
  ```
- Adaptador `internal sealed class PosCustomerLookup(ICustomerRepository customers, ICustomerProvisioning provisioning, IExecutionContext executionContext) : IPosCustomerLookup`:
  `SearchAsync` → `customers.SearchSelectableAsync`; `FindAsync` → `customers.FindAsync` mapeado a snapshot (tipo con
  `ToWireValue()`, `null` si no hay); `FindByIdentificationAsync` → `IdentificationTypeParser.Parse` +
  `FindExistingIdentificationsAsync` + `FindAsync` (devuelve activos **e** inactivos); `CreateFromRegisterAsync` →
  `provisioning.CreateFromRegisterAsync(tenantId, executionContext.SubjectId, …, source: "pos")` (Task 1b) y relee con `FindAsync` para devolver
  el ref (nombre y número ya normalizados por el agregado).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public async Task SearchMapsTheSelectableCustomersToRefsWithTheWireType()   // Cc → "CC", IsComplete, IsActive true
[Fact] public async Task FindReturnsNullForAnotherTenantsCustomer()
[Fact] public async Task FindReportsInactiveAndMissingDocument()                    // incompleto de WhatsApp → IdentificationNumber null; inactivo → IsActive false
[Fact] public async Task FindByIdentificationReturnsAnInactiveOwnerToo()
[Fact] public async Task CreateFromRegisterPassesTheCashierAsActorAndReturnsTheNormalizedRef() // " 1020304050 " → "1020304050"; actor = SubjectId del contexto; source = "pos"
[Fact] public void TheIdentificationTypesMatchTheCustomersModule()
// PosCustomerRules.IdentificationTypes como conjunto == IdentificationTypeParser.SupportedWireValues (ordinal ignore case)
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PosCustomerLookupTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** puerto, records, reglas, adaptador y `services.AddScoped<IPosCustomerLookup, PosCustomerLookup>()`.

- [ ] **Step 4: Corre y verifica que pasan** (más `ArchitectureTests --filter "FullyQualifiedName~PosLayerTests"`).
Expected: PASS; `ApplicationOnlyReferencesTenancyAmongTheBusinessModules` sigue verde.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pos/Modules.Pos.Application/IPosCustomerLookup.cs src/Bootstrapper tests/Bootstrapper
git commit -m "feat(pos): POSC-04 puerto de clientes de la caja con adaptador sobre Customers"
```

---

### Task 5 (POSC-05): `GET` y `POST /pos/customers`

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/IHasProblemExtensions.cs`
- Modify: `src/Api/ApiExceptionHandler.cs:61-75`
- Create: `src/Modules/Pos/Modules.Pos.Application/PosCustomers.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Application/PosDtos.cs` (junto a `PosCustomerResponse`, `:161`)
- Modify: `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs` (rutas después de `/products/by-code`, `:60-65`; request al final)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:245-283` (handlers y validadores)
- Modify: `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs` (`FakeCustomerLookup`, `PosTestBed.Customers`)
- Modify: `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs` (`CashierPermissions` `:25-30` suma `SaleCustomer`;
  `DisableModuleAsync(database, tenantId, moduleKey)` generaliza `DisablePosAsync` `:124-128`)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosCustomerHandlersTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCustomerApiTests.cs`

**Interfaces:**
- Consumes: `IPosCustomerLookup`, `PosCustomerRules` (Task 4), `PosPermissions.SaleCustomer` (Task 3).
- Produces:
  ```csharp
  // BuildingBlocks.Domain — hermana de IHasFieldErrors: ApiExceptionHandler copia cada par en ProblemDetails.Extensions,
  // sin pisar code, traceId ni errors.
  public interface IHasProblemExtensions { IReadOnlyDictionary<string, object?> ProblemExtensions { get; } }

  // PosDtos.cs
  /// El selector de la caja (spec «Endpoints nuevos»): sin teléfono, correo, dirección ni clasificación, porque el
  /// cajero no tiene el padrón (decisión 4). IsActive viaja para el 422 de identification_taken: la caja dice por qué
  /// no puede elegir a un inactivo; en la búsqueda siempre es true.
  public sealed record PosCustomerOptionResponse(
      Guid Id, string Name, string IdentificationType, string IdentificationNumber, bool IsComplete, bool IsActive);
  /// Un selector, no una lista paginada: siempre hasta 20, sin total.
  public sealed record PosCustomerOptionsResponse(IReadOnlyList<PosCustomerOptionResponse> Items);

  // PosCustomers.cs
  public sealed record SearchPosCustomersQuery(Guid TenantId, string? Search) : IQuery<PosCustomerOptionsResponse>;
  public sealed record CreatePosCustomerCommand(
      Guid TenantId, string? Name, string? IdentificationType, string? IdentificationNumber, string? Phone)
      : ICommand<PosCustomerOptionResponse>;
  public sealed class PosCustomerIdentificationTakenException(PosCustomerOptionResponse existing)
      : DomainException("pos.customer.identification_taken", "Another customer already uses that identification."),
        IHasProblemExtensions;   // ProblemExtensions = { ["existing"] = existing }

  // PosEndpoints.cs
  public sealed record CreatePosCustomerRequest(string? Name, string? IdentificationType, string? IdentificationNumber, string? Phone);
  ```
- `SearchPosCustomersValidator`: `Search` ≤ `PosCustomerRules.SearchMaxLength`. Handler: `EnsureAuthorized(…, SaleCustomer)`,
  validar, `lookup.SearchAsync(tenant, search?.Trim() vacío → null, PosCustomerRules.Take)`.
- `CreatePosCustomerValidator`: `Name` no vacío ≤ 160; `IdentificationType` ∈ `PosCustomerRules.IdentificationTypes`
  (ordinal); `IdentificationNumber` no vacío ≤ 32; `Phone` ≤ 32 cuando viene. Las claves de `errors` salen como
  `Name`, `IdentificationType`, `IdentificationNumber`, `Phone`.
- Handler de alta: autorizar, validar, `FindByIdentificationAsync(tenant, type, number.Trim())` → si existe lanza
  `PosCustomerIdentificationTakenException` (activo o no); si no, `CreateFromRegisterAsync`. La carrera sale como el 422
  `customers.customer.identification_taken` que lanza la unidad de trabajo de Customers.
- Rutas: `GET /customers` → 200 `PosCustomerOptionsResponse`; `POST /customers` → 201 con `Location`
  `/api/v1/tenants/{tenantId}/pos/customers/{id}` (no existe un `GET` por id; el `Location` es informativo como en las
  otras altas del módulo) y cuerpo `PosCustomerOptionResponse`. Las dos con `RequireAuthorization(PosPermissions.SaleCustomer)`,
  `ProducesProblem(403)` y `ProducesProblem(422)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`PosCustomerHandlersTests.cs`:
```csharp
[Fact] public async Task SearchNeedsThePermissionAndTheTenant()                 // sin SaleCustomer → authorization.denied; otro tenant → authorization.denied
[Fact] public async Task SearchTrimsAndAsksForTwenty()                          // "  Laura " → lookup recibe ("Laura", 20); "   " → (null, 20)
[Fact] public async Task SearchRejectsMoreThan120Characters()                   // validation.failed con errors["Search"]
[Fact] public async Task CreateReturnsTheNewIncompleteRef()                     // lookup vacío → ref con IsComplete false, IsActive true
[Fact] public async Task CreateWithATakenDocumentThrowsWithTheExistingCustomer() // ProblemExtensions["existing"] es el ref; no se llamó CreateFromRegisterAsync
[Theory] [InlineData("Name")] [InlineData("IdentificationType")] [InlineData("IdentificationNumber")] [InlineData("Phone")]
public async Task CreateValidatesEachField(string field)                        // vacío / "RUT" / 33 caracteres / 33 caracteres → errors[field]
```
`PosCustomerApiTests.cs` (harness de Pos, tenant con `pos` prendido y `customers` del signup):
```csharp
[Fact] public async Task TheCashierSearchesOnlyActiveCustomersWithDocument()
// alta completa por la API de clientes + un incompleto de WhatsApp por SQL + uno desactivado → GET /pos/customers?search= trae sólo el completo; sin teléfono ni email en el JSON
[Fact] public async Task TheCashierQuickCreatesAnIncompleteCustomer()
// POST /pos/customers {"name":"Laura Gómez","identificationType":"CC","identificationNumber":"1020304050","phone":null}
// → 201 {id, name, identificationType:"CC", identificationNumber:"1020304050", isComplete:false, isActive:true};
//   GET /customers/{id} (permisos de admin) → completeness Incomplete; AuditCountAsync("customers.customer.created") == 1 con `source = 'pos'` (la consulta de `AuditCountAsync` ya filtra `payload->>'source' = 'pos'`: antes de la Task 1b daría 0)
[Fact] public async Task QuickCreateWithATakenDocumentReturnsTheExistingCustomer()
// 422, code "pos.customer.identification_taken", existing.id = el cliente existente, existing.isActive = true
[Fact] public async Task QuickCreateWithTheDocumentOfAnInactiveCustomerReturnsItAsExisting()  // existing.isActive = false; no se crea nada
[Fact] public async Task QuickCreateValidationErrorsComeByField()                // nombre vacío → 422 validation.failed con errors.Name
[Fact] public async Task WithoutTheCustomersModuleTheCustomerPathIs403AndPlainSalesStillWork()
// DisableModuleAsync(…, "customers") → GET y POST /pos/customers 403 (sólo el status: la política no garantiza código).
// Task 7 le suma la parte de la venta; acá la prueba termina en las dos rutas.
[Fact] public async Task BillingCannotUseThePosCustomerEndpoints()              // X-Permissions con los de billing → 403
```
Y una prueba del manejador central en el mismo archivo de integración:
`[Fact] public async Task TheTakenProblemKeepsCodeTraceIdAndAddsExisting()` → el JSON tiene `code`, `traceId` y `existing`, y no `errors`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosCustomerHandlersTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** la interfaz y su rama en `ApiExceptionHandler` (después de la de `IHasFieldErrors`, con
  `TryAdd` para no pisar claves), DTOs, comandos, validadores, handlers, rutas, registro de handlers y validadores.

- [ ] **Step 4: Corre y verifica que pasan** (`PosCustomerHandlersTests` y `PosCustomerApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks src/Api/ApiExceptionHandler.cs src/Modules/Pos src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Pos
git commit -m "feat(pos): POSC-05 búsqueda y alta rápida de clientes en /pos/customers"
```

---

### Task 6 (POSC-06): Huella con `customerId` sólo cuando viene

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs:10-15` (comando) y `PosSaleFingerprint.cs:8-49`
- Modify: `src/Modules/Pos/Modules.Pos.Api/PosEndpoints.cs:178-183,238-242`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs`

**Interfaces:**
- Produces: `CreatePosSaleCommand(Guid TenantId, Guid Id, Guid CashSessionId, IReadOnlyList<PosSaleLineRequest> Lines, IReadOnlyList<PosPaymentRequest> Payments, Guid? CustomerId = null)`
  (opcional al final: las pruebas existentes no cambian) y `CreatePosSaleRequest(..., Guid? CustomerId)` que el endpoint
  pasa tal cual. `PosSaleFingerprint.Compute` agrega `command.CustomerId.Value.ToString("D")` como **cuarto** elemento
  del arreglo canónico sólo si `CustomerId` tiene valor; el `<summary>` lo documenta (decisión 6).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// Valor calculado fuera de línea con el JSON canónico del Command() por defecto. El Step 2 lo confirma con el código SIN
// cambiar: si difiere, el literal es el que imprime el código viejo, nunca el nuevo.
[Fact] public void WithoutACustomerTheFingerprintIsTheLiteralFromBeforeTheChange()
// Assert.Equal("91c8970aedd0a55f6f42f3bda80eff7dce36cc5e6a940932b72b858e8fa5b23e", PosSaleFingerprint.Compute(Command()))
[Fact] public void ACustomerChangesTheFingerprintAndTwoCustomersDiffer()
// Command() vs Command(customerId: A) vs Command(customerId: B) → tres huellas distintas
[Fact] public void TheSameCustomerGivesTheSameFingerprint()
```
`Command(...)` del archivo suma `Guid? customerId = null`.

- [ ] **Step 2: Corre y verifica**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosSaleFingerprintTests"`
Expected: FAIL de compilación (parámetro `customerId`). Antes de tocar `PosSaleFingerprint`, comenta temporalmente las
dos pruebas nuevas con cliente y confirma que `WithoutACustomerTheFingerprintIsTheLiteralFromBeforeTheChange` pasa con
el código viejo; pega la salida en el reporte.

- [ ] **Step 3: Implementa** el parámetro, la propiedad del request y el cuarto elemento condicional.

- [ ] **Step 4: Corre y verifica que pasan** (`PosSaleFingerprintTests` y `CreatePosSaleHandlerTests`).
Expected: PASS, incluida la del literal.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pos tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleFingerprintTests.cs
git commit -m "feat(pos): POSC-06 customerId en la venta y en la huella sólo cuando viene"
```

---

### Task 7 (POSC-07): La venta guarda el cliente elegido

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosSaleInputs.cs` (record nuevo) y `PosSale.cs:75-128`
- Modify: `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs` (validador `:25-71`, constructor `:74-88`, paso 5b entre `:137` y `:139`)
- Modify: `tests/Modules/Pos/Modules.Pos.UnitTests/CreatePosSaleHandlerTests.cs:31-33` (`Handler(...)` pasa `bed.Customers`)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosSaleTests.cs`, `CreatePosSaleHandlerTests.cs`,
  `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCustomerApiTests.cs`

**Interfaces:**
- Consumes: `IPosCustomerLookup.FindAsync`, `PosCustomerSnapshot` (Task 4), `CreatePosSaleCommand.CustomerId` (Task 6).
- Produces:
  ```csharp
  // Pos.Domain — la foto del cliente al vender (spec, decisión 8: sin FK).
  public sealed record PosSaleCustomer(Guid Id, string Name, string IdentificationType, string IdentificationNumber);
  public static PosSale Create(PosSaleId id, string fingerprint, CashSession session, IReadOnlyList<PosSaleLineInput> lines,
      IReadOnlyList<PosPaymentInput> payments, DateTimeOffset at, PosSaleCustomer? customer = null);
  // null → PosFinalConsumer como hoy; con valor → CustomerId, CustomerName, CustomerIdentificationType, CustomerIdentificationNumber.
  ```
- Validador: `RuleFor(c => c.CustomerId).NotEqual(Guid.Empty).When(c => c.CustomerId is not null)` (un `Guid.Empty`
  es `validation.failed`, paso 1, nunca un `pos.*`).
- `CreatePosSaleHandler` recibe `IPosCustomerLookup customers`. Paso 5b, sólo con `CustomerId`:
  1. Sin `SaleCustomer` en el contexto → `RequestForbiddenException("pos.sale.customer_not_allowed", …)` (mismo molde que `DiscountNotAllowed`, `:246-247`).
  2. `FindAsync`: `null` → `PosDomainException("pos.sale.customer_not_found")`; `!IsActive` → `"pos.sale.customer_inactive"`;
     `IdentificationNumber is null` → `"pos.sale.customer_identification_missing"`.
  3. `new PosSaleCustomer(...)` y se pasa a `PosSale.Create`. El comentario del paso 1 (`:92-96`) suma que estos cuatro
     también salen después de la repetición.

- [ ] **Step 1: Escribe las pruebas que fallan**

`PosSaleTests.cs`:
```csharp
[Fact] public void ASaleWithACustomerKeepsItsSnapshot()        // Create(..., customer: new(id, "Laura Gómez", "CC", "1020304050")) → los cuatro campos
[Fact] public void ASaleWithoutACustomerIsTheFinalConsumer()   // CustomerId null, PosFinalConsumer.Name, tipo null, PosFinalConsumer.IdentificationNumber
```
`CreatePosSaleHandlerTests.cs` (`Seller` + `PosPermissions.SaleCustomer` = `CustomerSeller`):
```csharp
[Fact] public async Task ASaleWithAValidCustomerStoresTheSnapshotWithItsId()
[Fact] public async Task ASaleWithoutCustomerIdNeverAsksTheLookup()                   // FakeCustomerLookup.Calls vacío
[Fact] public async Task ACustomerWithoutThePermissionIs403BeforeAnyNumber()          // Seller → RequestForbiddenException pos.sale.customer_not_allowed; Numbers sin pedir
[Theory] [InlineData("missing", "pos.sale.customer_not_found")] [InlineData("inactive", "pos.sale.customer_inactive")]
[InlineData("noDocument", "pos.sale.customer_identification_missing")]
public async Task AnUnusableCustomerIsADefinitive422(string state, string code)       // y no gasta número
[Fact] public async Task ARepeatWithACustomerIsRecognizedBeforeTheCustomerChecks()
// primera venta con cliente → se desactiva en el doble → mismo comando → Created false, la venta original, sin 422
[Fact] public async Task AnEmptyCustomerIdIsAValidationFailure()                      // Guid.Empty → ValidationException
```
`PosCustomerApiTests.cs`:
```csharp
[Fact] public async Task ASaleToAQuickCreatedCustomerCarriesItInTheTicketAndTheList()
// POST /pos/customers → POST /pos/sales con customerId → 201 customer {name:"Laura Gómez", identificationType:"CC", identificationNumber:"1020304050"};
// GET /pos/sales → customerName "Laura Gómez"; pos.sales.customer_id = el id (SQL)
```
y completa `WithoutTheCustomersModuleTheCustomerPathIs403AndPlainSalesStillWork`: con `customers` apagado, una venta con
`customerId` → 403 `pos.sale.customer_not_allowed` y la misma venta sin `customerId` (otro `id`) → 201 a Consumidor final.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosSaleTests|FullyQualifiedName~CreatePosSaleHandlerTests"`
Expected: FAIL de compilación (`PosSaleCustomer`, parámetro `customer`, constructor del handler).

- [ ] **Step 3: Implementa** el record, el parámetro, la regla del validador, la dependencia y el paso 5b.

- [ ] **Step 4: Corre y verifica que pasan** (las dos clases unitarias y `PosCustomerApiTests`, `PosSaleApiTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pos tests/Modules/Pos
git commit -m "feat(pos): POSC-07 la venta guarda el cliente elegido y rechaza uno inservible"
```

---

### Task 8 (POSC-08): Índice parcial `IX_sales_tenant_customer`, README y verificación

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs:138-145`
- Create (ef): `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/*_AddPosSaleCustomerIndex.cs` (+ Designer y snapshot)
- Modify: `README.md:1598-1611` (tabla POS) y `:822` (fila de `/pos`: 14 operaciones y el permiso nuevo)
- Test: `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs`

**Interfaces:**
- Produces: `sale.HasIndex(value => new { value.TenantId, value.CustomerId }).HasFilter("customer_id IS NOT NULL").HasDatabaseName("IX_sales_tenant_customer")`,
  con un comentario que dice para qué queda (el filtro por cliente, fuera de alcance) y que no hay FK (decisión 8).
  La migración sólo crea el índice; `Down` lo quita. Sin columnas.
- README: filas `GET /customers` (`pos.sale.customer`, «Clientes elegibles de la caja: activos con documento, hasta 20,
  por nombre, documento o CUC») y `POST /customers` (`pos.sale.customer`, «Alta rápida: ficha incompleta con documento;
  422 `pos.customer.identification_taken` trae `existing`»); la fila `POST /sales` suma «`customerId` opcional».

- [ ] **Step 1: Escribe la prueba que falla**

```csharp
[Fact] public async Task TheSaleCustomerIndexIsPartialOnCustomerId()
// SELECT indexdef FROM pg_indexes WHERE schemaname='pos' AND indexname='IX_sales_tenant_customer'
// → contiene "(tenant_id, customer_id)" y "WHERE (customer_id IS NOT NULL)"
```

- [ ] **Step 2: Corre y verifica que falla**

Run: `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosPersistenceTests"`
Expected: FAIL (el índice no existe).

- [ ] **Step 3: Implementa** el índice en el modelo y genera la migración con el comando de Global Constraints. Revisa que
  el `Up` sólo tenga `CreateIndex` con `filter: "customer_id IS NOT NULL"`.

- [ ] **Step 4: Corre y verifica**

Run: `PosPersistenceTests`, `SaleLinesVariantMigrationTests` (las migraciones siguen subiendo y bajando),
`dotnet build` del repo y `dotnet format --verify-no-changes --include <archivos tocados en la rama>`.
Expected: PASS y build sin warnings nuevos.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Pos/Modules.Pos.Infrastructure README.md tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs
git commit -m "feat(pos): POSC-08 índice parcial por cliente en las ventas y README"
```

---

## Contrato para el frontend

Todo bajo `/api/v1/tenants/{tenantId}/pos`, con `X-Qep-Client: web` en los `POST`.

| Ruta | Permiso | Cuerpo / query | Respuesta |
|---|---|---|---|
| `GET /customers?search=` | `pos.sale.customer` | `search` opcional ≤ 120 | 200 `{ items: [{ id, name, identificationType, identificationNumber, isComplete, isActive }] }`, hasta 20; sin `search`, los más nuevos |
| `POST /customers` | `pos.sale.customer` | `{ name, identificationType: "NIT"\|"CC"\|"CE"\|"PASAPORTE", identificationNumber, phone: string\|null }` | 201 con el mismo objeto del selector (`isComplete: false`, `isActive: true`) |
| `POST /sales` | `pos.sale.create` | el cuerpo de hoy + `customerId: string \| null` | igual que hoy; `customer` trae el snapshot del elegido |

| Código | Status | Dónde | Cuándo |
|---|---|---|---|
| `validation.failed` | 422 | `POST /customers` | `errors` con `Name`, `IdentificationType`, `IdentificationNumber`, `Phone` |
| `pos.customer.identification_taken` | 422 | `POST /customers` | el documento ya es de un cliente; `existing` es el objeto del selector (puede venir con `isActive: false`) |
| `customers.customer.identification_taken` | 422 | `POST /customers` | carrera: otra caja creó el mismo documento entre el chequeo y el guardado |
| `pos.sale.customer_not_allowed` | 403 | `POST /sales` | `customerId` sin `pos.sale.customer` (p. ej. apagaron Clientes); sale **después** de la repetición |
| `pos.sale.customer_not_found` / `_inactive` / `_identification_missing` | 422 | `POST /sales` | el cliente ya no sirve; definitivos |
| (403 de la política) | 403 | las dos rutas nuevas | sin el permiso o con el módulo `customers` apagado (enmascarado); el cuerpo no trae un código garantizado, el handler responde `authorization.denied` si se llega a él |

Despliegue: backend primero. Un frontend viejo no manda `customerId` y su huella no cambia.
