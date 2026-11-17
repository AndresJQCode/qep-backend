# Borrar clientes desde la lista — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que `POST /api/v1/tenants/{tenantId:guid}/customers/bulk-delete` borre de forma definitiva hasta 100 clientes por request, deje intactos los que tienen historia comercial o un contacto bloqueado, y limpie o desacople cada referencia de otros módulos según §4.3 del spec, sin carreras con quien crea documentos o recibe mensajes en paralelo.

**Architecture:** El handler de Customers abre una transacción en `CustomersDbContext`, toma un candado consultivo **exclusivo** por cliente (`CustomerReferenceLock`) y llama a un puerto nuevo, `ICustomerDeletionReferences`, cuyo adaptador en Bootstrapper corre SQL entre esquemas sobre esa misma conexión (precedente: `CustomerMergeReferences`). Quien escribe una referencia que bloquea (Quotations, Loans, ServiceContracts) o cuelga una conversación de un cliente (Messaging) toma el mismo candado en modo **compartido** dentro de su transacción y comprueba que el cliente sigue existiendo. Los objetos de R2 salen por el outbox (`customers.customer.media-released.v1`) y los borra un procesador de Messaging con reintento.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-10-borrar-clientes-design.md`](../specs/2026-11-10-borrar-clientes-design.md) · spec hermano (sólo contexto): `qep-frontend/docs/superpowers/specs/2026-11-10-borrar-clientes-design.md`

---

## Antes de empezar

- Worktree `qep-backend-worktrees/borrar-clientes`, rama `feat/borrar-clientes`, creada desde `develop`. Antes de **cada** commit: `git branch --show-current` tiene que decir `feat/borrar-clientes`; si dice otra cosa, frena y pregunta.
- Todo comando es PowerShell, uno por línea, sin `&&` (`A; if ($?) { B }`).
- Si `Api.exe` está corriendo, `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. **No lo detengas**: es del developer. Avisa y espera.
- Pruebas de integración: Docker arriba y, en la misma sesión, `$env:Seed__Enabled = "false"`. Un proyecto a la vez, con `--filter` a la clase tocada y `-- xUnit.MaxParallelThreads=4`. Las unitarias se corren en cada tarea; la corrida de integración completa de los proyectos tocados va una sola vez, en la Tarea 12.
- CodeGraph no estuvo disponible al escribir este plan (el MCP no conectó y el CLI no está instalado); las rutas y líneas salen de búsquedas con `rg` sobre `develop` del 2026-11-10. Si una línea se movió, busca el símbolo.

## Global Constraints

- Ruta exacta: `POST /api/v1/tenants/{tenantId:guid}/customers/bulk-delete`, dentro del grupo de `CustomerEndpoints.cs:29-31`. Header `X-Tenant-Id`. Sin `If-Match` (A-2).
- Permiso nuevo `customers.customer.delete` (constante `CustomersPermissions.CustomerDelete`), con su política en `AddAuthorization` y su `PermissionDefinition` con `RequiredModules: [ModuleKeys.Customers]`. Se concede sólo al rol de sistema `admin` (ver Discrepancias, D1).
- Cuerpo: `{ "customerIds": ["<guid>", …] }`. Validación 422 `validation.failed` con mapa `errors`: no vacío, máximo **100**, sin `Guid.Empty`. Los duplicados se ignoran (no son error).
- Respuesta 200: `{ "deleted": [guid], "blocked": [{ "customerId", "name", "reasons": [...] }], "notFound": [guid] }`. `reasons` ⊆ `quotations | orders | loans | serviceContracts | blockedContact`, **en ese orden**. Las tres listas siempre presentes, aunque vacías.
- Bloquea cualquier fila, en cualquier estado, en `quotations.quotations.client_id`, `quotations.orders.client_id`, `loans.loans.customer_id`, `billing.services.customer_id` o `billing.charges.customer_id`, o una conversación del cliente con `blocked_at IS NOT NULL`.
- Todo el lote es **una** transacción de base sobre la conexión de `CustomersDbContext`: un fallo inesperado deshace el lote entero (P7). R2 queda fuera (A-9).
- Auditoría: acción `customers.customer.deleted`, `resourceId` = id del cliente, outcome `success`, una por cliente borrado, por `ICustomersAuditPublisher`.
- Evento nuevo `customers.customer.media-released.v1`, payload camelCase `{ "tenantId": "<guid>", "customerId": "<guid>", "storageKeys": ["…"] }`, en `platform.outbox_messages` en la misma transacción, **sólo si hay claves**. Consumidor `messaging.customer-media-purge`.
- `Modules.<M>.Application` no referencia EF Core ni Npgsql (`tests/ArchitectureTests`). El SQL entre esquemas va en Infrastructure o en Bootstrapper.
- No se toca `EnsureByPhoneAsync` ni `EnsureAsync` (D-4).
- TDD: RED antes que GREEN, con la salida literal de ambos en el reporte de cada tarea.
- Commits: conventional commits, **sin `Co-Authored-By` ni atribución de IA**, siempre `git commit -m "…" -- <rutas>`.
- Comentarios de código en español, tuteando; el voseo se corrige sólo en las líneas que tocas.
- `docs/estado.md` **no** se toca en esta rama.

## Decisiones de este plan (lo que el spec deja al constructor)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | **Concurrencia por candado consultivo de transacción** con clave `customers.customer-reference:{tenantId:N}:{customerId:N}` pasada por `hashtextextended(key, 0)`. El borrado toma `pg_advisory_xact_lock` (exclusivo) por cliente, **ordenados por id**, antes de leer bloqueos. Los escritores toman `pg_advisory_xact_lock_shared` y después `SELECT EXISTS (… customers.customers …)` en su propia transacción. | Las referencias son Guids sin FK entre módulos y cada módulo escribe por su conexión: un `FOR UPDATE` en `customers` desde el lookup (que va por otra conexión) se suelta antes del `INSERT`. El candado consultivo vale entre conexiones y dura hasta el commit de cada lado. Ordenar evita interbloqueos entre dos borrados. |
| P2 | Los escritores toman el candado **en su unit of work** (`QuotationsUnitOfWork`, `LoansUnitOfWork`, `ServiceContractsUnitOfWork`), mirando el `ChangeTracker`: entidad `Added` o con `ClientId`/`CustomerId` modificado. Sin transacción abierta, el UoW abre una alrededor del `SaveChanges`. | Un solo punto por módulo cubre todos los caminos (crear cotización, cambiar cliente, pedido manual, Shopify y tienda por `IExternalOrderWriter`, préstamo, servicio) sin tocar cada handler. |
| P3 | Cliente desaparecido al guardar: el código de "no existe" que **ya** lanza el módulo: `quotation.quotation.client_not_found` (`QuotationCustomerEligibility.cs:22-26`), `LoansErrorCodes.CustomerNotFound` (`LoanRules.cs:107`), `ServiceContractsErrorCodes.CustomerNotFound` (`CreateService.cs:101-104`). | No se inventan códigos. |
| P4 | Messaging toma el candado compartido por un puerto `IMessagingCustomerReferenceLock` antes de escribir `customer_id` en una conversación (ingesta entrante, coexistencia, `ConversationOpener`, cambio de cliente, apertura de campaña). Si el cliente ya no existe: el webhook responde `DeliveryOutcome.RetryLater` (el reintento crea un cliente nuevo, D-4); el cambio de cliente y la conversación nueva fallan con el código de "no existe" que ya usan; la campaña marca al destinatario `Skipped` con `unreachable`. | A-12. `RetryLater` ya existe (`WebhookDeliveryProcessor.cs:15`) y la transacción de la ingesta se deshace entera. |
| P5 | El chequeo `blockedContact` lee `messaging.conversations` del cliente con `FOR UPDATE`. | Un «Bloquear» en paralelo espera y, al terminar el borrado, choca por concurrencia (412) en vez de perderse con la conversación (D-9). |
| P6 | Un id que existe pero es un fusionado (`merged_into_id` no nulo) va a `notFound`. Los fusionados **en** un cliente borrado se borran con él (A-6). | La lista no muestra fusionados; el frontend nunca los manda a propósito. |
| P7 | Un error inesperado deshace **todo** el lote (500, o 412 si es carrera por `CustomersWriteRaces`). No hay campo extra en la respuesta. | §5 lo deja al constructor; cortar el lote no cambia el contrato del frontend. |
| P8 | La foto de `data_consents` (`customer_name` varchar(160), `customer_phone_e164` varchar(16), ambas nulas) se llena **sólo al borrar**. La FK se quita (no se cambia a `SET NULL`: `customer_id` es `NOT NULL` y es la prueba de a quién se le preguntó). | §7 lo deja al constructor. Mismos largos que `customers.customers.name` y `phone_e164`. |
| P9 | La foto de la auditoría va en `changedFields` (columna `audit.entries.changed_fields`, jsonb arreglo), como cadenas `clave=valor`: `name=`, `phoneE164=`, `identification=<tipo> <número>`, y conteos `conversations=`, `messages=`, `deals=`, `posSales=`, `campaignRecipients=`, `dataConsents=`, `merged=`. Una clave sin valor no se escribe. | La tabla de auditoría no tiene columna de detalle (ver Discrepancias, D3). `AuditProjectionWorker.cs:101-104` acepta cualquier arreglo JSON. |
| P10 | `campaign_recipients.customer_id` pasa a nullable **sin tocar** `IX_campaign_recipients_campaign_customer`: PostgreSQL trata los `NULL` como distintos, y el `ON CONFLICT (campaign_id, customer_id)` del congelado sigue valiendo para ids no nulos. Al borrar: los `Pending` del cliente pasan a `Skipped` con `failure_code = 'unreachable'`; en todos, `customer_id`, `conversation_id` y `message_id` (si apuntan a lo borrado) quedan en `NULL`. | A-10, A-7. `unreachable` es el motivo existente de «sin ficha» (`CampaignDispatch.cs` `LateSkipReasonAsync`). |
| P11 | `audience_customer_ids`: se quita el id en `Draft`/`Scheduled` y sube `version` (como la fusión), **salvo** si dejaría la audiencia vacía (`CK_campaigns_audience` exige tags + clientes > 0): ahí el id se queda colgante. | Ver Discrepancias, D5. El congelado ya ignora ids que no existen. |
| P12 | Objetos de R2: el adaptador junta `message_media.storage_key` y `scheduled_messages.media_storage_key` de las conversaciones borradas **antes** de borrarlas; el handler escribe un evento por cliente con claves. Un procesador de Messaging (`CustomerMediaPurgeProcessor`, worker cada 15 s, lote de 20, patrón `CoexistenceEventsProcessor` + `ClaimedOutboxConsumer`) borra con `IMessagingMediaStore.DeleteAsync` y marca su inbox sólo si todas salieron. El nombre del evento vive en dos copias amarradas por una prueba de paridad. | A-9. Messaging no referencia Customers (`MessagingLayerTests`). |
| P13 | Pipeline y POS **no** toman el candado. Un negocio o una venta creados en la carrera quedan con un id colgante. | No es anti-objetivo del spec; se anota en §8 del spec (Tarea 12). |

## Review Focus

1. **El caso del owner: 100 contactos `Incomplete` de coexistencia, cada uno con una conversación de cientos de mensajes y adjuntos.** Un request, 200, sin N+1: el adaptador hace un número fijo de sentencias por lote, no por cliente. Lo amarra la Tarea 9 (`RemovingAHundredCustomersRunsAFixedNumberOfStatements`).
2. **Un contacto que escribe mientras se borra.** El mensaje termina en una conversación nueva con un cliente nuevo; nunca colgado del borrado. Lo amarra la Tarea 6 (`AnInboundDuringDeletionLandsOnANewCustomer`).
3. **Una campaña en `Draft` cuyo único destinatario es el cliente borrado.** El borrado no da 500 por `CK_campaigns_audience`. Lo amarra la Tarea 9 (`ADraftCampaignWhoseOnlyAudienceIsTheCustomerKeepsTheId`).
4. **Un `Bloquear` en otra pestaña durante el borrado.** O el cliente sale en `blocked` con `blockedContact`, o el bloqueo responde 412; nunca se pierde en silencio. Lo amarra la Tarea 9 (`TheBlockedContactCheckLocksTheConversations`).
5. **El mismo id dos veces, un id de otro tenant y un fusionado en la misma selección.** Duplicado ignorado, los otros dos en `notFound`, 200. Lo amarra la Tarea 8 (`DuplicatesAreIgnoredAndForeignOrMergedIdsAreNotFound`).

---

### Task 1: Permiso `customers.customer.delete`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersPermissions.cs` (junto a `CustomerMerge`, `:33`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin` `:1641-1653`; `PermissionDefinition` junto a la de `CustomerMerge` `:1990-1996`; política junto a `:2601-2603`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Produces: `CustomersPermissions.CustomerDelete = "customers.customer.delete"`. Etiqueta «Borrar clientes», descripción «Permite borrar clientes de forma definitiva. No se puede deshacer.», grupo `"Customers"`, riesgo `"high"`.

- [ ] **Step 1: Write the failing tests**
  - `PermissionDiscoveryFindsTheSeventyFourConstants` → renómbrala `…SeventyFiveConstants`, `Assert.Equal(75, …)` y su comentario.
  - `OnlyAdminCarriesTheCustomerDeletePermission` (forma de `AdminAndAdvisorCarryTheCustomerMergePermission`, `:296-311`): `admin` lo tiene; `advisor`, `billing` y `cashier` no.
- [ ] **Step 2: Run, expect FAIL** (no compila: la constante no existe).
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
- [ ] **Step 3: Implement** las tres mitades (constante, `PermissionDefinition`, política) y el grant en `admin`, con comentario «Spec 2026-11-10 D-8: borrar es definitivo; sólo admin (el owner del tenant siempre lo es, `Membership.cs:529`)».
- [ ] **Step 4: Run, expect PASS** (incluye `EveryPermissionConstantDeclaresItsModules` y `EveryRegisteredPermissionStringIsUnique`).
- [ ] **Step 5: Commit** — `feat(customers): add customers.customer.delete permission`.

### Task 2: `CustomerReferenceLock`

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Infrastructure/CustomerReferenceLock.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/CustomerReferenceLockTests.cs`

**Interfaces:**
- Produces (sin dependencia de EF; `FormattableString` es del BCL):
  - `public static string KeyFor(Guid tenantId, Guid customerId)` → `$"customers.customer-reference:{tenantId:N}:{customerId:N}"`.
  - `public static FormattableString Exclusive(Guid tenantId, Guid customerId)` → `SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))`.
  - `public static FormattableString Shared(Guid tenantId, Guid customerId)` → `SELECT pg_advisory_xact_lock_shared(hashtextextended({key}, 0))`.
  - `public static FormattableString Exists(Guid tenantId, Guid customerId)` → `SELECT EXISTS (SELECT 1 FROM customers.customers WHERE tenant_id = {tenantId} AND id = {customerId}) AS "Value"` (para `SqlQuery<bool>`).
  - El comentario de la clase dice: P1, que lee `customers.customers` por nombre, y que el escritor lo corre **dentro** de su transacción.

- [ ] **Step 1: Write the failing tests** (Testcontainers, como `CustomerMergeReferencesTests`; réplica mínima `customers.customers(id, tenant_id)`):
  - `TheKeyIsStableAndScopedByTenant` → `KeyFor(t, c) == "customers.customer-reference:<t:N>:<c:N>"`; otro tenant da otra clave.
  - `ASharedHolderMakesTheExclusiveWait` — conexión A: `BEGIN`, `Shared`; conexión B: `BEGIN`, `Exclusive` en segundo plano → no termina en 300 ms; A `COMMIT` → B termina.
  - `ExistsSeesTheDeletionCommittedWhileWaiting` — B tiene `Exclusive`, borra la fila y hace `COMMIT` mientras A espera en `Shared`; luego `Exists` en A → `false`.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomerReferenceLockTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(platform): add customer reference advisory lock`.

### Task 3: Quotations toma el candado al guardar

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs:82-86`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/CustomerReferenceLockApiTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces: `private async Task HoldCustomerReferencesAsync(CancellationToken)` en el UoW: ids distintos de `ClientId != Guid.Empty` de toda `Quotation`/`Order` `Added` o con `ClientId` modificado; por cada uno, ordenado, `Shared` y `Exists`; si falta → `QuotationsDomainException("quotation.quotation.client_not_found", …)` (el tipo que ya usa `QuotationCustomerEligibility`). Sin transacción abierta, `SaveChangesAsync` abre una, guarda y confirma.

- [ ] **Step 1: Write the failing tests** (candado exclusivo tomado a mano en una `NpgsqlConnection` del contenedor con `CustomerReferenceLock.Exclusive`):
  - `CreatingAQuotationWaitsForTheDeletionAndThenFails` — exclusivo tomado; `POST` de cotización en segundo plano → sin respuesta a los 500 ms; `DELETE FROM customers.customers WHERE id = …` y `COMMIT` → 422 `quotation.quotation.client_not_found`; cero filas en `quotations.quotations` para ese cliente.
  - `CreatingAManualOrderWaitsForTheDeletionAndThenFails` — igual con el pedido manual.
  - `ChangingTheQuotationClientToADeletedCustomerFails` — mismo patrón con el cambio de cliente (`ChangeQuotationClient`).
  - `WithoutADeletionNothingChanges` — sin candado, crear cotización → 201 como hoy.
- [ ] **Step 2: Run, expect FAIL** (la primera responde 201 sin esperar).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~CustomerReferenceLockApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement** según Interfaces. Comentario: P1/P2, y que el `IExternalOrderWriter` (Shopify, tienda) pasa por aquí.
- [ ] **Step 4: Run, expect PASS**, más `--filter "FullyQualifiedName~CreateQuotation|FullyQualifiedName~CreateOrder|FullyQualifiedName~ConvertQuotation"` en verde.
- [ ] **Step 5: Commit** — `feat(quotations): hold the customer reference lock when saving a client`.

### Task 4: Loans y ServiceContracts toman el candado al guardar

**Files:**
- Modify: `src/Modules/Loans/Modules.Loans.Infrastructure/Persistence/LoansUnitOfWork.cs:12-16`
- Modify: `src/Modules/ServiceContracts/Modules.ServiceContracts.Infrastructure/Persistence/ServiceContractsUnitOfWork.cs:18-22`
- Test: `tests/Modules/Loans/Modules.Loans.IntegrationTests/CustomerReferenceLockApiTests.cs`, `tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests/CustomerReferenceLockApiTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces: la misma regla que la Tarea 3 sobre `Loan.CustomerId` y `ServiceContract.CustomerId` (`Added` o modificado), con `LoansDomainException(LoansErrorCodes.CustomerNotFound, …)` y `ServiceContractsDomainException(ServiceContractsErrorCodes.CustomerNotFound, …)`. Ninguno de los dos UoW abre transacción hoy: la abre el propio `SaveChangesAsync` cuando hace falta.

- [ ] **Step 1: Write the failing tests**: `CreatingALoanWaitsForTheDeletionAndThenFails` y `CreatingAServiceWaitsForTheDeletionAndThenFails`, con el patrón de la Tarea 3 (respuesta 422 con el código del módulo y cero filas en `loans.loans` / `billing.services` / `billing.customer_profiles` para ese cliente).
- [ ] **Step 2: Run, expect FAIL**, un proyecto a la vez:
  `dotnet test tests/Modules/Loans/Modules.Loans.IntegrationTests --filter "FullyQualifiedName~CustomerReferenceLockApiTests" -- xUnit.MaxParallelThreads=4`
  `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~CustomerReferenceLockApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS**, más `CreateLoan*` y `CreateService*` filtrados.
- [ ] **Step 5: Commit** — `feat(loans,billing): hold the customer reference lock when saving a customer`.

### Task 5: Messaging — destinatario de campaña sin cliente

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/CampaignRecords.cs:22` (`Guid? CustomerId`), `src/Modules/Messaging/Modules.Messaging.Application/CampaignDispatch.cs:21` (`ClaimedRecipient.CustomerId` → `Guid?`) y `LateSkipReasonAsync` (~`:290-298`), `src/Modules/Messaging/Modules.Messaging.Infrastructure/Campaigns/CampaignDispatchStore.cs:199` y `:279-282`
- Create: migración `MakeCampaignRecipientCustomerOptional` (+ Designer y snapshot)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CampaignRecipientWithoutCustomerTests.cs`

**Interfaces:**
- Produces: `campaign_recipients.customer_id uuid NULL`; `LateSkipReasonAsync` devuelve `CampaignFailureCodes.Unreachable` con `CustomerId is null` sin llamar a `FindContactAsync`; el abridor de conversación nunca recibe un `null`.

- [ ] **Step 1: Write the failing tests**:
  - `TwoRecipientsWithoutCustomerFitInOneCampaign` — dos filas con `customer_id NULL` en la misma campaña → sin violar `IX_campaign_recipients_campaign_customer`.
  - `AClaimedRecipientWithoutCustomerIsSkippedAsUnreachable` — destinatario `Pending` con `customer_id NULL` en campaña `Sending`; corre el despachador → `Skipped`, `failure_code = 'unreachable'`, ninguna conversación creada.
- [ ] **Step 2: Run, expect FAIL** (`23502 not_null_violation`).
  `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CampaignRecipientWithoutCustomerTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Migración:
  `dotnet ef migrations add MakeCampaignRecipientCustomerOptional --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`
  Revisa que el `Up` sólo cambie la nulabilidad. Comentario en `CampaignRecipientRecord.CustomerId`: P10.
- [ ] **Step 4: Run, expect PASS**, más `--filter "FullyQualifiedName~Campaign"` del mismo proyecto.
- [ ] **Step 5: Commit** — `feat(messaging): allow campaign recipients without a customer`.

### Task 6: Messaging toma el candado al colgar una conversación de un cliente

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerReferenceLock.cs`, `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingCustomerReferenceLock.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs` (antes de los `COALESCE(customer_id, …)`, `:73`, `:147`), `Webhook/CoexistenceIngestion.cs` (`:158`, `:197`, `:211`, dentro de la transacción de `:41`), `Webhook/WebhookDeliveryProcessor.cs` y `WebhookDeliveryProcessor.Instagram.cs` (traducir a `RetryLater`), `Persistence/ConversationOpener.cs` (`:41`, `:50`, `:87`, `:108`; abre transacción si no hay), `Modules.Messaging.Application/ConversationAssignment.cs:175-195` (`ChangeConversationCustomerHandler`)
- Modify: el registro del puerto en `MessagingInfrastructureExtensions`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CustomerDeletionRaceTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces: `public interface IMessagingCustomerReferenceLock { Task<bool> HoldAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken); }` — exige `MessagingDbContext.Database.CurrentTransaction` (si no, `InvalidOperationException`); corre `Shared` y devuelve `Exists`. `internal sealed class CustomerVanishedException : Exception` en Infrastructure, que el procesador traduce a `DeliveryOutcome.RetryLater`. En el cambio de cliente y en la conversación nueva, `false` lanza el código de "no existe" que esos handlers ya usan para el cliente (búscalo en el handler; no lo inventes).

- [ ] **Step 1: Write the failing tests** (candado exclusivo a mano, como la Tarea 3; el fixture de Meta se copia de una forma real de `messaging.webhook_deliveries`, regla del `CLAUDE.md`):
  - `AnInboundDuringDeletionLandsOnANewCustomer` — cliente C con conversación; exclusivo tomado; se procesa un entrante de su teléfono en segundo plano → espera; se borran a mano la conversación y C y `COMMIT` → la entrega queda para reintento; al reprocesarla, el mensaje cuelga de una conversación nueva cuyo `customer_id` ≠ C y existe en `customers.customers`.
  - `ACoexistenceEchoDuringDeletionLandsOnANewCustomer` — igual con un eco de coexistencia.
  - `ChangingTheConversationCustomerToADeletedOneFails` — exclusivo tomado; `PUT` del cambio de cliente → espera; borrado y `COMMIT` → el 4xx existente; la conversación sigue con su cliente anterior.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CustomerDeletionRaceTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Comentario en cada llamada: A-12 y P4.
- [ ] **Step 4: Run, expect PASS**, más `--filter "FullyQualifiedName~Webhook|FullyQualifiedName~Coexistence|FullyQualifiedName~ConversationAssignment"`.
- [ ] **Step 5: Commit** — `feat(messaging): hold the customer reference lock when attaching a conversation`.

### Task 7: Customers — `data_consents` sin FK y con foto

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerDataConsent.cs` (dos propiedades nulas `CustomerName`, `CustomerPhoneE164`, sólo lectura desde el dominio), `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`:34` quita la constante; `:428-431` corrige el comentario; mapeo de las dos columnas)
- Create: migración `DetachDataConsentsFromCustomers` (+ Designer y snapshot)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/DataConsentDetachMigrationTests.cs`

**Interfaces:**
- Produces: columnas `customers.data_consents.customer_name varchar(160) NULL` y `customer_phone_e164 varchar(16) NULL`; sin `FK_data_consents_customers_customer_id`.

- [ ] **Step 1: Write the failing tests**:
  - `TheConsentForeignKeyIsGone` — `SELECT 1 FROM pg_constraint WHERE conname = 'FK_data_consents_customers_customer_id'` → cero filas.
  - `AConsentSurvivesItsCustomer` — consentimiento por la API existente; `DELETE FROM customers.customers` del cliente → la fila de `data_consents` sigue.
- [ ] **Step 2: Run, expect FAIL** (`23001 restrict_violation`).
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~DataConsentDetachMigrationTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** `dotnet ef migrations add DetachDataConsentsFromCustomers --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations`. La FK la agregó a mano `AddCustomerDataConsents.cs:55-61`, así que el `Up` lleva a mano `DropForeignKey(name: "FK_data_consents_customers_customer_id", schema: "customers", table: "data_consents")` más las dos columnas. El `Down` la vuelve a crear y lleva el comentario: falla si ya hay consentimientos de clientes borrados; nunca se corre (mismo criterio que `AddCustomerDataConsents`).
- [ ] **Step 4: Run, expect PASS**, más `--filter "FullyQualifiedName~DataConsent"`.
- [ ] **Step 5: Commit** — `feat(customers): keep data consents after their customer is deleted`.

### Task 8: Customers — caso de uso `BulkDeleteCustomers`

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/BulkDeleteCustomers.cs` (comando, validador, handler, resultado), `src/Modules/Customers/Modules.Customers.Application/ICustomerDeletionReferences.cs`, `src/Modules/Customers/Modules.Customers.Application/ICustomerDeletionStore.cs`, `src/Modules/Customers/Modules.Customers.Application/CustomersIntegrationEvents.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerAuditActions.cs` (`Deleted = "customers.customer.deleted"`), `ICustomersAuditPublisher.cs` (sobrecarga con `changedFields`), `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersAuditPublisher.cs:40-70` (pasa la lista en vez de `[]`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/BulkDeleteCustomersTests.cs` (dobles en `CustomerWriteDoubles.cs`)

**Interfaces:**
- Produces:
  - `public sealed record BulkDeleteCustomersCommand(Guid TenantId, IReadOnlyList<Guid> CustomerIds) : ICommand<BulkDeleteCustomersResult>`; `MaxCustomers = 100`.
  - `public sealed record BulkDeleteCustomersResult(IReadOnlyList<Guid> Deleted, IReadOnlyList<BlockedCustomerDto> Blocked, IReadOnlyList<Guid> NotFound)`; `public sealed record BlockedCustomerDto(Guid CustomerId, string Name, IReadOnlyList<string> Reasons)`.
  - `public static class CustomerDeletionBlockReasons { Quotations = "quotations", Orders = "orders", Loans = "loans", ServiceContracts = "serviceContracts", BlockedContact = "blockedContact"; IReadOnlyList<string> Ordered }`.
  - `public sealed record CustomerDeletionCounts(int Conversations, int Messages, int Deals, int PosSales, int CampaignRecipients)`; `public sealed record CustomerDeletionOutcome(IReadOnlyDictionary<Guid, CustomerDeletionCounts> Counts, IReadOnlyDictionary<Guid, IReadOnlyList<string>> StorageKeys)`.
  - `ICustomerDeletionReferences`: `Task LockAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)`; `Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> FindBlockersAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)` (sólo los bloqueados, motivos en el orden de `Ordered`); `Task<CustomerDeletionOutcome> RemoveAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)`. Todos corren en la conexión y la transacción del `CustomersDbContext` del scope; sin ids no consultan.
  - `ICustomerDeletionStore`: `Task<IReadOnlyList<Customer>> FindDeletableAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)` (del tenant y sin `MergedIntoId`); `Task<int> SnapshotConsentsAsync(Guid tenantId, IReadOnlyCollection<(Guid CustomerId, string Name, string? PhoneE164)> customers, CancellationToken)`; `Task<IReadOnlyDictionary<Guid, int>> DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken)` (borra también los fusionados en ellos; devuelve, por cliente borrado, cuántos fusionados se llevó: el `merged=` de la auditoría).
  - `CustomersIntegrationEvents.CustomerMediaReleased = "customers.customer.media-released.v1"`.
  - `ICustomersAuditPublisher.Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt, IReadOnlyCollection<string> changedFields)`.

- [ ] **Step 1: Write the failing tests**:
  - Validador: `EmptyIsRejected`, `MoreThanAHundredIsRejected` (101), `AnEmptyGuidIsRejected` → error en `CustomerIds`; `ExactlyAHundredIsAccepted`; `DuplicatesAreAccepted`.
  - `WithoutThePermissionItIsForbidden` → `RequestForbiddenException`, el doble de referencias nunca se llama.
  - `EachReasonBlocksAndNothingIsRemoved` (teoría por cada motivo) → `Blocked == [{ id, name, [motivo] }]`, `RemoveAsync` no recibe ese id.
  - `ReasonsKeepTheContractOrder` — doble devuelve `["orders", "quotations"]` mal ordenado → respuesta `["quotations", "orders"]`.
  - `DuplicatesAreIgnoredAndForeignOrMergedIdsAreNotFound` — `[a, a, otroTenant, fusionado]` → `Deleted == [a]`, `NotFound == [otroTenant, fusionado]`.
  - `AMixIsSplitInRequestOrder` — `[b1 (bloqueado), d1, n1, d2]` → `Deleted == [d1, d2]`, `Blocked` con `b1`, `NotFound == [n1]`.
  - `LocksAreTakenBeforeBlockersAreRead` — el doble registra el orden de llamadas: `LockAsync` (con todos los ids distintos), luego `FindBlockersAsync`, luego `RemoveAsync`, luego `SaveChanges` y commit.
  - `EachDeletedCustomerIsAuditedWithItsSnapshot` — una entrada `customers.customer.deleted` por borrado con `changedFields` que contiene `name=Ana Pérez`, `phoneE164=+573001112233`, `identification=CC 123` y `conversations=2`.
  - `AMediaEventIsWrittenOnlyForCustomersWithKeys` — dos borrados, uno con claves → un evento con su `customerId` y sus `storageKeys`.
- [ ] **Step 2: Run, expect FAIL** (compile).
  `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~BulkDeleteCustomersTests"`
- [ ] **Step 3: Implement.** Orden del handler: `CustomersAuthorization.EnsureAuthorized(…, CustomerDelete)` → `ValidateAndThrowAsync` → ids distintos en orden de llegada → `BeginTransactionAsync` → `LockAsync` → `FindDeletableAsync` (lo que falta, a `NotFound`) → `FindBlockersAsync` → `SnapshotConsentsAsync` de los borrables → `RemoveAsync` → `DeleteAsync` → auditoría y evento por cliente → `SaveChangesAsync` → `CommitAsync`. El validador copia la cadena de `SetProductsStoreVisibilityValidator` (`SetProductsStoreVisibility.cs:28-42`) sin la regla de distintos, con mensajes en español. El evento se agrega como `CustomersOutboxMessage` desde un `ICustomerMediaReleasePublisher` (copia de `ProductImageReleasePublisher.cs:8-31`), implementado en `Modules.Customers.Infrastructure/Persistence/CustomerMediaReleasePublisher.cs`; agrégalo a la lista de Files de esta tarea al crearlo.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(customers): add bulk delete use case`.

### Task 9: Adaptadores — `CustomerDeletionReferences` y `CustomerDeletionStore`

**Files:**
- Create: `src/Bootstrapper/CustomerDeletionReferences.cs`, `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerDeletionStore.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `:1229`), `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDeletionAcrossModulesTests.cs`

**Interfaces:**
- Consumes: Task 2 (`Exclusive`), Task 5 (columna nula), Task 7 (columnas de foto), Task 8 (puertos).
- Produces: `internal sealed class CustomerDeletionReferences(CustomersDbContext customers) : ICustomerDeletionReferences`, todo envuelto en `CustomersWriteRaces.RunAsync`. Exige `CurrentTransaction` (si no, `InvalidOperationException`).
  - `LockAsync`: `Exclusive` por id, ids ordenados.
  - `FindBlockersAsync`: una consulta por motivo con `= ANY(ids)` y `tenant_id`; la de `blockedContact` es `SELECT … FROM messaging.conversations WHERE … FOR UPDATE` sobre **todas** las conversaciones de los clientes (P5) y marca los que tengan `blocked_at IS NOT NULL`.
  - `RemoveAsync`, sentencias por lote (nunca por cliente ni por mensaje), en este orden: ids de conversaciones (`conv`) → claves de `message_media.storage_key` (unida a `messages`) y `scheduled_messages.media_storage_key` no nulas, agrupadas por cliente → conteos → `automations.http_calls` y `automations.run_units` por `run_id` de `conversation_runs` de `conv` → `automations.conversation_runs` → `messaging.automation_inbox` por `conv` → `campaign_recipients` (P10: primero `Pending` → `Skipped`/`unreachable`, después los `NULL`) → `campaigns.audience_customer_ids` (P11) → `pipeline.deal_stage_history`, `deal_quotations`, `deal_orders` y `triage_decisions` por `deal_id` de los negocios del cliente, `triage_decisions` por `customer_id` o `conversation_id = ANY(conv)`, y `pipeline.deals` → `messaging.conversations` (los `CASCADE` se llevan `messages`, `message_media` y `scheduled_messages`) → `pos.sales.customer_id = NULL` → `billing.customer_profiles` → `geography.unresolved_city_occurrences` → `shopify.orders.qep_customer_id = NULL`, **última** (la prueba de atomicidad rompe la última, como en la fusión).
  - `CustomerDeletionStore`: `SnapshotConsentsAsync` con un `UPDATE … FROM unnest(…)` sobre `data_consents` del tenant; `DeleteAsync` con `ExecuteDelete` sobre `customers` donde `id = ANY(ids) OR merged_into_id = ANY(ids)` (direcciones, notas y etiquetas por `CASCADE`).

- [ ] **Step 1: Write the failing tests** (resuelven el adaptador real del contenedor, contra el esquema migrado; sembrado por la API donde exista y por SQL con las columnas de cada `DbContext` donde no; cada prueba en su tenant):
  - `EveryReferenceEndsAsTheSpecSays` — cliente C con dirección, nota, etiqueta, consentimiento, fusionado F en C, conversación con 3 mensajes y un adjunto, mensaje programado con medio, corrida del chatbot con unidad, fila en `automation_inbox`, destinatario `Sent` y otro `Pending` en campañas distintas, campaña `Draft` con `[C, X]`, negocio con historial y decisión de triage, venta POS, perfil de cobro sin servicio, ocurrencia de ciudad sin resolver y pedido de Shopify → tras `RemoveAsync` + `DeleteAsync` y commit: C y F no existen; dirección, nota y etiqueta no existen; el consentimiento existe con `customer_name` y `customer_phone_e164` de C; la conversación, los mensajes, `message_media`, el programado, la corrida, la unidad, la fila del inbox, el negocio, su historial y la decisión no existen; los dos destinatarios existen con `customer_id`, `conversation_id` y `message_id` en `NULL`, el `Sent` sigue `Sent` y el `Pending` quedó `Skipped`/`unreachable`; la campaña `Draft` tiene `[X]` y `version` + 1; la venta existe con `customer_id NULL` y `customer_name` intacto; el perfil y la ocurrencia no existen; el pedido de Shopify tiene `qep_customer_id NULL`; `StorageKeys[C]` contiene las dos claves.
  - `ADraftCampaignWhoseOnlyAudienceIsTheCustomerKeepsTheId` — campaña `Draft` con `[C]` y sin tags → sin error; `audience_customer_ids == [C]`.
  - `EachBlockerIsFound` (teoría: cotización, pedido, préstamo, servicio, cobro, conversación bloqueada) → el motivo esperado; con cotización **y** pedido → `["quotations", "orders"]`.
  - `TheBlockedContactCheckLocksTheConversations` — tras `FindBlockersAsync` sin commit, un `UPDATE messaging.conversations SET blocked_at = now()` en otra conexión no termina en 300 ms.
  - `AFailureInTheLastStatementLeavesEverythingAsItWas` — disparador que rompe el `UPDATE shopify.orders` (patrón de `CustomerMergeAcrossModulesApiTests.AFailureInTheLastUpdateLeavesEverythingAsItWas`) → excepción y todas las filas del primer caso intactas.
  - `RemovingAHundredCustomersRunsAFixedNumberOfStatements` — 100 clientes con una conversación de 50 mensajes cada uno; un `DbCommandInterceptor` cuenta comandos: el número no depende de la cantidad de clientes (compáralo con el de 1 cliente).
- [ ] **Step 2: Run, expect FAIL** (compile: no hay adaptador).
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerDeletionAcrossModulesTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** El comentario de la clase sigue al de `CustomerMergeReferences`: qué esquemas y columnas lee por nombre, que renombrarlas compila y responde 500 (`42703`/`42P01`) sin escribir nada, y qué pruebas lo amarran.
- [ ] **Step 4: Run, expect PASS**, más `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomerMergeReferencesTests"` (la columna nula de la Tarea 5 no rompe su réplica).
- [ ] **Step 5: Commit** — `feat(customers): remove a deleted customer's references across modules`.

### Task 10: Messaging — purga de medios en R2

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSubscribedEvents.cs` (o la constante en el archivo de eventos suscritos que ya exista en Messaging), `src/Modules/Messaging/Modules.Messaging.Infrastructure/CustomerDeletion/CustomerMediaPurgeProcessor.cs`, `src/Modules/Messaging/Modules.Messaging.Infrastructure/CustomerDeletion/CustomerMediaPurgeWorker.cs`
- Modify: `MessagingInfrastructureExtensions` (registro), el harness de integración de Messaging (quitar el worker del host, como se hace con los demás workers)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/CustomerMediaPurgeTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/CustomerMediaReleasedEventParityTests.cs`

**Interfaces:**
- Consumes: Task 8 (`CustomersIntegrationEvents.CustomerMediaReleased`, payload).
- Produces: `MessagingSubscribedEvents.CustomerMediaReleased = "customers.customer.media-released.v1"`; `internal interface ICustomerMediaPurgeProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }`; `Consumer = "messaging.customer-media-purge"`, lote de 20, worker cada 15 s. Reclama con `ClaimedOutboxConsumer` y marca el inbox de Messaging como `CoexistenceEventsProcessor`.

- [ ] **Step 1: Write the failing tests**:
  - `EveryKeyOfTheEventIsDeleted` — objetos subidos con `IMessagingMediaStore`; evento con sus claves → ya no existen; mensaje procesado.
  - `AFailedDeleteIsRetried` — doble de `IMessagingMediaStore` que falla la primera vez → primera corrida: no procesado; segunda: borrado y procesado.
  - `AMissingKeyIsNotAnError` — clave que nunca existió → procesado.
  - `TheEventNameMatchesInBothModules` (paridad) → las dos constantes son iguales.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~CustomerMediaPurgeTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Comentario: A-9; el borrado ya confirmó, así que una falla de R2 sólo deja objetos que se reintentan.
- [ ] **Step 4: Run, expect PASS**, y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomerMediaReleasedEventParityTests"`.
- [ ] **Step 5: Commit** — `feat(messaging): purge a deleted customer's media from storage`.

### Task 11: Endpoint `POST …/customers/bulk-delete`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (ruta en el grupo de `:29-31`; corrige el comentario «No hay DELETE» de `:138-139`), el archivo de DTOs de Customers.Application donde vivan los `*Request` (`BulkDeleteCustomersRequest(IReadOnlyList<Guid>? CustomerIds)`)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerBulkDeleteApiTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 8, 9.
- Produces: `.MapPost("/bulk-delete", …)` con `.RequireAuthorization(CustomersPermissions.CustomerDelete)`, `.Accepts<BulkDeleteCustomersRequest>("application/json")`, `.Produces<BulkDeleteCustomersResult>(200)`, `.ProducesProblem(403)`, `.ProducesProblem(422)`. Comando `new BulkDeleteCustomersCommand(tenantId, request.CustomerIds ?? [])`. El DTO de respuesta lleva el comentario BFF: las tres listas siempre vienen, para que la pantalla diga qué pasó con cada fila sin cruzar ids.

- [ ] **Step 1: Write the failing tests** (`CustomersApiHarness.CreateClient(factory, SubjectId, TenantId, …permisos)`):
  - `WithoutThePermissionItIs403` — sólo `CustomerRead`/`CustomerManage` → 403, el cliente sigue.
  - `TheResponseHasTheContractShape` — un borrable, uno con cotización (sembrado por SQL o el harness de pedidos) y un id inventado → 200; JSON con `deleted == [a]`, `blocked == [{ customerId: b, name: "<nombre de b>", reasons: ["quotations"] }]`, `notFound == [x]`; `GET` de `a` → 404; `b` sigue.
  - `AnEmptyListIs422WithTheErrorsMap` → 422 `validation.failed` con `errors.CustomerIds`.
  - `OneHundredAndOneIdsIs422` → 422.
  - `TheDeletionIsAudited` — tras procesar el outbox, una fila en `audit.entries` con `action = 'customers.customer.deleted'`, `resource_id = a`, `outcome = 'success'` y `changed_fields` que contiene `name=…`.
  - `AQuotationCommittedWhileWaitingBlocksTheDeletion` — otra conexión: `BEGIN`, `CustomerReferenceLock.Shared` de C, `INSERT` mínimo en `loans.loans` (columnas obligatorias según `LoansDbContext.cs`); `POST bulk-delete` en segundo plano → sin respuesta a los 500 ms; `COMMIT` → 200 con C en `blocked` con `["loans"]`.
- [ ] **Step 2: Run, expect FAIL** (404: la ruta no existe).
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerBulkDeleteApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(customers): expose bulk delete endpoint`.

### Task 12: Documentación y verificación final

**Files:**
- Modify: `CLAUDE.md` (gotchas: `CustomerDeletionReferences` escribe en ocho esquemas por nombre y quién lo amarra; el gotcha de `data_consents` deja de decir «nada la borra» y suma la foto al borrar; `CustomerReferenceLock` y qué escritores lo toman, P1–P4, P13)
- Modify: `docs/superpowers/specs/2026-11-10-borrar-clientes-design.md` §8 (columnas encontradas y P13; las discrepancias de abajo) y §3.1 D-8 si el owner confirma D1

- [ ] **Step 1: Arquitectura.** `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (en particular `CustomersLayerTests`, `MessagingLayerTests`, `QuotationsLayerTests`, `LoansLayerTests`, `ServiceContractsLayerTests`, `CompositionRootTests`).
- [ ] **Step 2: Unitarias.** `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests` → PASS.
- [ ] **Step 3: Integración, una vez, un proyecto a la vez**, en este orden: Customers, Messaging, Quotations, Loans, ServiceContracts, Pipeline (`CustomerMergeAcrossModulesApiTests` sigue verde con la columna nula). Por ejemplo:
  `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests -- xUnit.MaxParallelThreads=4`
  Pega el resumen literal de cada uno.
- [ ] **Step 4: Prueba manual** contra el backend local con el stub (`$env:Authentication__UseDevelopmentStub = "true"` en línea aparte). El cuerpo va a `body.json` (`{ "customerIds": ["<guid>"] }`) y:
  `curl.exe -s -X POST "http://localhost:5000/api/v1/tenants/<tenantId>/customers/bulk-delete" -H "X-Tenant-Id: <tenantId>" -H "X-Subject-Id: <subjectId>" -H "X-Permissions: customers.customer.delete" -H "Content-Type: application/json" -d "@body.json"`
  Esperado: 200 con las tres listas.
- [ ] **Step 5: Commit** — `docs: document customer deletion cross-module writes`.

---

## Spec discrepancies

| # | El spec dice | El código dice | Qué hace el plan |
| --- | --- | --- | --- |
| D1 | D-8 y §5: «roles owner y admin». | No hay rol de sistema `owner` (`SystemRoleKeys.cs:14-25`: admin, advisor, billing, cashier). El owner es una membresía que siempre conserva `admin` (`Membership.cs:529`). | Concede sólo a `admin`; el owner lo recibe por ahí. Corregir el spec. |
| D2 | §4.3 y A-11: tabla `messaging.conversation_events`. | No existe: los eventos son filas de `messaging.messages` con `details` (`ConversationEvents.cs:8-21`). | Los de conversaciones borradas se van por `CASCADE`; los `CustomerChanged` de conversaciones que sobreviven quedan en `details` como historia. Sin código. |
| D3 | A-8: auditoría «con la foto (nombre, teléfono, documento) y los conteos». | `audit.entries` no tiene columna de detalle; sólo `changed_fields` (jsonb arreglo, pensado para nombres de campo). | P9: la foto va en `changed_fields` como `clave=valor`. Si el owner no lo acepta, hace falta una columna nueva en Audit (otro trabajo). |
| D4 | A-10: conservar destinatarios para que «las estadísticas de campañas pasadas no cambian». | `CampaignQueries.cs:93-112` cuenta `Delivered`/`Read` uniendo `messages` por `message_id` y `Replied` por `conversation_id`. Al borrar las conversaciones (D-5) esos tres bajan; `Total`, `Sent`, `Failed`, `Skipped` y `Pending` no. | Implementa el spec tal cual y lo deja como **decisión pendiente del owner**: o se acepta la baja, o se congelan esos estados en `campaign_recipients` antes de borrar (cambio de esquema de Messaging). |
| D5 | §4.3: sacar el id de `audience_customer_ids` en `Draft`/`Scheduled`. | `CK_campaigns_audience` exige `cardinality(tag_ids) + cardinality(customer_ids) > 0`: vaciar la audiencia da 500. | P11: si la dejaría vacía, el id se queda. Decisión pendiente del owner (alternativas: borrar o cancelar esa campaña). |
| D6 | §4.3 no lista `billing.customer_messages.customer_id`, `automations.conversation_runs`/`run_units`/`http_calls`, `messaging.automation_inbox`, `pipeline.deal_stage_history`, ni `deals.origin_conversation_id`. | Existen (`ServiceContractsDbContext.cs:186-214`; `AutomationsDbContext.cs:207-283`; `MessagingDbContext.cs:606-625`; `PipelineDbContext.cs:157`, `:131`). | `customer_messages` sólo existe con cobros, que ya bloquean: sin acción. Las de Automations, el inbox y el historial de negocios se borran con lo suyo. `origin_conversation_id` de un negocio de **otro** cliente queda colgante como historia (criterio A-11). Se anotan en §8. |
| D7 | §4.3: `data_consents` «FK sin `RESTRICT`». | `customer_id` es `NOT NULL` y la FK la creó la migración a mano. | P8: se quita la FK; la fila conserva el id. |
| D8 | §2 «Borrar desde el detalle: no objetivo»; `CustomerEndpoints.cs:138-139` dice «No hay DELETE» a propósito. | — | La Tarea 11 corrige ese comentario: la decisión del owner del 2026-11-10 lo reemplaza. |

## Self-review

- **Cobertura del spec:** §4.1 → Tareas 8 y 11; §4.2 → Tarea 9 (`EachBlockerIsFound`); §4.3, fila por fila → Tarea 9 (`EveryReferenceEndsAsTheSpecSays`) y Tarea 5; §4.4 → Tarea 6 (cliente nuevo); §5 concurrencia con bloqueos → Tareas 2-4 y 11; §5 Mensajería → Tarea 6; atomicidad → Tarea 9 (`AFailureInTheLastStatement…`); módulos y arquitectura → Tarea 12; rendimiento → Tarea 9; migraciones → Tareas 5 y 7; permiso → Tarea 1; A-9 → Tarea 10; §6 pruebas mínimas → Tareas 8, 9 y 11.
- **Tipos:** `CustomerReferenceLock.{KeyFor, Exclusive, Shared, Exists}`, `ICustomerDeletionReferences.{LockAsync, FindBlockersAsync, RemoveAsync}`, `ICustomerDeletionStore.{FindDeletableAsync, SnapshotConsentsAsync, DeleteAsync}`, `CustomerDeletionOutcome`, `CustomersIntegrationEvents.CustomerMediaReleased` y `MessagingSubscribedEvents.CustomerMediaReleased` se usan con el mismo nombre en todas las tareas.
