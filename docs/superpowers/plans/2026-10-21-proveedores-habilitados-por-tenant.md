# Proveedores habilitados por tenant — Implementation Plan (1 de 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El operador de la plataforma habilita o deshabilita Zenvia por tenant desde la consola; deshabilitado, el tenant no lo ofrece en Integraciones ni lo puede usar.

**Architecture:** La habilitación vive en **Tenancy**, junto a los módulos: es un entitlement por tenant, la consola de operador y su historial (`tenant_changes`) ya están ahí, e Integrations ya consume `Modules.Tenancy.Application` (`ITenantModules`). Tenancy guarda claves de proveedor de una lista cerrada (`TenantProviderKeys`), sin conocer el catálogo; Integrations marca qué proveedores la requieren y lee la habilitación por un puerto nuevo, `ITenantProviders`. El frontend agrega la sección «Proveedores» a la ficha del tenant y el estado «Deshabilitado por la plataforma» a Integraciones.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, FluentValidation, xUnit + Testcontainers (backend); React 19, TanStack Query/Router, Vitest + Testing Library (frontend).

**Spec:** `docs/superpowers/specs/2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md` — decisiones D2, D3, D12, D13, D14, D15; §4.5, §5.1, §5.3, §6 (consola e Integraciones). Este plan no toca el envío de cotizaciones (planes 2 y 3).

**Worktrees:** backend en `../qep-backend-canal-cotizaciones`, frontend en `../qep-frontend-canal-cotizaciones`, los dos en la rama `feature/envio-cotizaciones-por-canal` desde `develop`. Rutas de este plan relativas a cada worktree.

## Global Constraints

- TDD obligatorio (CLAUDE.md de los dos repos): RED antes que GREEN, con la salida literal de ambos.
- Conventional commits, en español en el backend como el historial, **sin atribución de IA** (CLAUDE.md de los dos repos).
- Frontend: `bun`, nunca npm. Pruebas con `VITE_API_BASE_URL= bun run test …` (el `.env.local` apunta al túnel). `bun run build` y `bun run lint` verdes al cerrar cada tarea de frontend.
- Copy de UI en español neutro, tuteando. Identificadores y comentarios en inglés en el frontend; el backend comenta en español como el código existente.
- Clave de proveedor: `zenvia`, la misma de `IntegrationProviders.Zenvia.Key`.
- Motivos por dirección: activar = `contract`, `courtesy`, `correction`; desactivar = `nonpayment`, `cancellation`, `correction` (`TenantChangeVocabulary.ModuleActivationReasons` / `ModuleDeactivationReasons`). Nota ≤ 300.
- Tenants nuevos sin fila de proveedor ⇒ deshabilitado (D2). El registro no escribe nada.
- Backfill (D14): `zenvia` activo en cada tenant con al menos una fila en `integrations.connections` con `provider_key = 'zenvia'`, en cualquier estado.
- Código de error de Integrations para un proveedor deshabilitado: `integrations.provider.not_enabled`, 403.
- El permiso del operador para cambiar proveedores es `operator.modules.manage` (`OperatorPermissions.ModulesManage`): mismo rol, misma consola, y un permiso nuevo obligaría a tocar roles y la máscara sin ganar nada.

## Review Focus

1. **Proveedor deshabilitado con conexiones existentes:** el tenant las sigue viendo en Integraciones, marcadas, y puede borrarlas; no puede crear, editar, probar, pausar ni reanudar (Task 4, Task 6).
2. **Deshabilitar lo que no tiene fila / habilitar lo activo:** responde `tenancy.providers.no_changes` (422), sin escribir historial (Task 3).
3. **Motivo de la otra dirección** (`nonpayment` al habilitar, `contract` al deshabilitar): `tenancy.providers.reason_not_allowed` (Task 3).
4. **Historial filtrado por módulo** (`?module=`): no debe traer lotes de proveedor, igual que hoy no trae cambios de estado del tenant (Task 3).
5. **Stub de desarrollo** (`ITenantProviders.FindEnabledAsync` en `null` porque el tenant no está en `tenancy.tenants`): todo habilitado, como `IsVisibleFor(null)` (Task 4).

---

## Backend

### Task 1: Dominio de la habilitación de proveedores

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantProvider.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChange.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantChangeVocabulary.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantProviderTests.cs`

**Interfaces:**
- Produces:
  - `public static class TenantProviderKeys { public const string Zenvia = "zenvia"; public static readonly IReadOnlyList<string> All = [Zenvia]; public static bool IsKnown(string? key); }` — ordinal, sin ignorar mayúsculas.
  - `public sealed class TenantProvider { TenantId TenantId; string ProviderKey; TenantModuleStatus Status; DateTimeOffset EnabledAt; DateTimeOffset StatusChangedAt; static TenantProvider Create(TenantId, string providerKey, DateTimeOffset now); void Activate(DateTimeOffset); void Deactivate(DateTimeOffset); }` — reutiliza `TenantModuleStatus` (`active`/`inactive`); `Create` nace `Active`; `Activate`/`Deactivate` sobre el mismo estado lanzan `TenantDomainException` con `TenantProviderChange.NoChangesCode`.
  - `public static class TenantProviderChange { const string NoChangesCode = "tenancy.providers.no_changes"; const string ReasonNotAllowedCode = "tenancy.providers.reason_not_allowed"; static void EnsureReasonAllowed(TenantModuleStatus to, ChangeReason reason); }` — usa los mismos conjuntos de motivos que los módulos.
  - `TenantChangeKind.Provider = 3`, texto `"provider"`.
  - `TenantChange.ProviderKey` (`string?`) y `static TenantChange ForProvider(TenantId, Guid batchId, string providerKey, TenantModuleStatus? fromStatus, TenantModuleStatus toStatus, ChangeReason, string? note, Guid actorUserId, DateTimeOffset)`. `ModuleKey` queda `null` en estas filas.

- [ ] **Step 1: Write the failing tests** — `TenantProviderTests`:
  - `CreateStartsActive`: `TenantProvider.Create(tenant, "zenvia", now)` → `Status == Active`, `EnabledAt == StatusChangedAt == now`.
  - `DeactivatingTwiceIsNoChange`: segundo `Deactivate` lanza con `Code == "tenancy.providers.no_changes"`.
  - `ReasonsFollowTheModuleDirections`: `EnsureReasonAllowed(Active, Nonpayment)` lanza `tenancy.providers.reason_not_allowed`; `EnsureReasonAllowed(Inactive, Contract)` lanza; `Correction` pasa en las dos.
  - `ForProviderWritesTheProviderKeyAndKind`: `Kind == Provider`, `ProviderKey == "zenvia"`, `ModuleKey == null`, `FromStatus == null` cuando `fromStatus` es null, `ToStatus == "active"`.
  - `KindTextRoundTrips`: `ToText(TenantChangeKind.Provider) == "provider"` y `ParseKind("provider") == Provider`.
  - `OnlyZenviaIsKnown`: `IsKnown("zenvia")` true; `IsKnown("Zenvia")`, `IsKnown("whatsapp-cloud")`, `IsKnown(null)` false.
- [ ] **Step 2: Run** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter TenantProviderTests` — Expected: FAIL (tipos inexistentes, no compila).
- [ ] **Step 3: Implement** los tipos de **Interfaces** en los archivos listados.
- [ ] **Step 4: Run** el mismo comando — Expected: PASS; y `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests` completo verde.
- [ ] **Step 5: Commit** — `feat(tenancy): dominio de proveedores habilitados por tenant`.

### Task 2: Persistencia, migración con backfill y puerto `ITenantProviders`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (DbSet `TenantProviders`, `ConfigureTenantProvider`, columna `provider_key` y CHECKs de `tenant_changes`)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantProviders.cs` (lectura) y `TenantProviderRepository.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantProviders.cs`
- Create: migración `AddTenantProviders` en `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/` (con `dotnet ef migrations add`, no a mano)
- Modify: registro en la extensión de Infrastructure de Tenancy (donde se registra `ITenantModules`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantProvidersPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `public interface ITenantProviders { Task<IReadOnlySet<string>?> FindEnabledAsync(Guid tenantId, CancellationToken ct); }` — claves con fila `active`; `null` si el tenant no está en `tenancy.tenants` (stub de desarrollo, mismo contrato que `ITenantModules.FindAsync`).
  - `public interface ITenantProviderRepository { void Add(TenantProvider provider); Task<IReadOnlyList<TenantProvider>> ListByTenantAsync(TenantId tenantId, CancellationToken ct); }` — con tracking, se commitea con `ITenancyUnitOfWork`.
- Tabla `tenancy.tenant_providers`: PK `(tenant_id, provider_key)`, `provider_key varchar(32)`, `status varchar(16)`, `enabled_at`, `status_changed_at`; FK a `tenancy.tenants` como `tenant_modules`; CHECKs `CK_tenant_providers_provider_key` = `provider_key IN ('zenvia')` y `CK_tenant_providers_status` = `status IN ('active','inactive')`.
- `tenant_changes`: columna `provider_key varchar(32) NULL`; `CK_tenant_changes_kind` pasa a `kind IN ('module','tenant_status','provider')`; nuevo `CK_tenant_changes_provider_key` = `(kind = 'provider') = (provider_key IS NOT NULL)` y `provider_key IS NULL OR provider_key IN ('zenvia')`. Revisar que el CHECK de `module_key` existente no exija `module_key` para `kind <> 'tenant_status'`; si lo hace, ajustarlo a `kind = 'module'`.
- Backfill en el `Up`, después de crear la tabla, con SQL guardado por `to_regclass('integrations.connections') IS NOT NULL` (el DbContext de Integrations puede no haber migrado en una base nueva): `INSERT INTO tenancy.tenant_providers (tenant_id, provider_key, status, enabled_at, status_changed_at) SELECT DISTINCT c.tenant_id, 'zenvia', 'active', now(), now() FROM integrations.connections c JOIN tenancy.tenants t ON t.id = c.tenant_id WHERE c.provider_key = 'zenvia' ON CONFLICT DO NOTHING`. Sin fila en `tenant_changes`: es una migración, no una decisión del operador. Confirmar nombres reales de columnas de `integrations.connections` en su snapshot antes de escribir el SQL.

- [ ] **Step 1: Write the failing tests** — `TenantProvidersPersistenceTests` (Testcontainers, mismo arnés que las pruebas de integración de Tenancy):
  - `EnabledReturnsOnlyActiveRows`: tenant con `zenvia` activo → `{"zenvia"}`; tras `Deactivate` y guardar → vacío.
  - `UnknownTenantIsNull`: `FindEnabledAsync(Guid.NewGuid())` → `null`.
  - `NewTenantHasNoProviders`: un tenant registrado por el flujo normal → conjunto vacío, no `null`.
  - `ProviderChangeRowsPersist`: `TenantChange.ForProvider(...)` se guarda y se relee con `ProviderKey == "zenvia"` y `Kind == Provider`.
  - `TheCheckRejectsAnUnknownProviderKey`: insertar `provider_key = 'otro'` por SQL crudo falla con violación del CHECK.
- [ ] **Step 2: Run** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter TenantProvidersPersistenceTests` — Expected: FAIL (no compila).
- [ ] **Step 3: Implement** configuración, repositorio, lectura y registro; generar la migración con `dotnet ef migrations add AddTenantProviders` contra el proyecto de Infrastructure de Tenancy y agregar el backfill al `Up`. El `Down` borra la tabla, la columna y vuelve los CHECKs; antes borra las filas `kind = 'provider'` de `tenant_changes`.
- [ ] **Step 4: Run** el mismo comando — Expected: PASS; después `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests` completo verde.
- [ ] **Step 5: Commit** — `feat(tenancy): tabla de proveedores por tenant con backfill de Zenvia`.

### Task 3: Consola de operador — detalle, cambio e historial

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ChangeTenantProvider.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/OperatorDtos.cs`, `OperatorTenantDetails.cs`, `IOperatorTenantReader.cs`, `OperatorInput.cs`, `ListTenantHistory.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OperatorTenantReader.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/OperatorEndpoints.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ChangeTenantProviderHandlerTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces (contrato HTTP que usa Task 5):
  - `OperatorTenantDetailDto` gana `IReadOnlyList<OperatorTenantProviderDto> Providers`, siempre todas las de `TenantProviderKeys.All`, en ese orden.
  - `public sealed record OperatorTenantProviderDto(string Key, string Status, DateTimeOffset? Since, string? LastReason)` — `Status` `active`, `inactive` o `none` (sin fila, `OperatorTenantModuleDto.NoRowStatus`); `Since` = `StatusChangedAt`.
  - `OperatorTenantSnapshot` gana `IReadOnlyList<TenantProviderState> Providers` y `IReadOnlyDictionary<string, ChangeReason> LastProviderReasons`; `public sealed record TenantProviderState(string Key, TenantModuleStatus Status, DateTimeOffset StatusChangedAt)`.
  - `public sealed record ChangeTenantProviderCommand(TenantId TenantId, TenantId TargetTenantId, string? Provider, string? Status, string? Reason, string? Note) : ICommand<OperatorTenantDetailDto>`.
  - `POST /api/v1/tenants/{tenantId}/operator/tenants/{targetTenantId}/providers/changes`, body `{ "provider": "zenvia", "status": "active"|"inactive", "reason": "...", "note": "..."|null }` → 200 `OperatorTenantDetailDto`. Mismos `Produces`/`ProducesProblem` que `modules/changes`.
  - `OperatorHistoryChangeDto` gana `string? ProviderKey` (al final, para no romper el orden posicional). Lote de proveedor: `Kind = "provider"`, `ModuleKey = null`.
- Handler: `OperatorAuthorization.EnsureAuthorized(..., OperatorPermissions.ModulesManage, ...)`, validador, `BeginTenantChangeScopeAsync(target)`, tenant 404 `tenancy.tenant.not_found`, plan igual que los módulos (sin fila + activar ⇒ `Create`; sin fila + desactivar ⇒ `no_changes`), `TenantChange.ForProvider` con un `batchId` nuevo, auditoría `tenancy.tenant_providers.changed` con `["zenvia:<from|none>-><to>", "reason:<reason>"]`, `SaveChanges`, commit, y responde `OperatorTenantDetails.LoadAsync`.
- Validador: `Provider` en `TenantProviderKeys.All` (mensaje `Unknown provider key.`), `Status` vía `OperatorInput.IsDirection`, `Reason` vía `OperatorInput.IsReason`, `Note` ≤ `TenantChange.NoteMaxLength`.
- `ListHistoryAsync` con `moduleKey` sólo devuelve lotes `kind = 'module'` que tocan ese módulo (excluye `provider` y `tenant_status`).

- [ ] **Step 1: Write the failing unit tests** — `ChangeTenantProviderHandlerTests` (dobles en memoria como `ChangeTenantModulesHandlerTests`):
  - `EnablingWithoutARowCreatesItAndLogsTheChange`: sin fila, `active`/`contract` → repo recibe `TenantProvider` activo; un `TenantChange` con `FromStatus == null`, `ToStatus == "active"`, `ProviderKey == "zenvia"`.
  - `DisablingAnActiveProvider`: fila activa, `inactive`/`nonpayment` → `Status == Inactive`, cambio `active -> inactive`.
  - `DisablingWithoutARowIsNoChange` → `tenancy.providers.no_changes`, sin cambios ni auditoría.
  - `EnablingWithANonpaymentReasonIsRefused` → `tenancy.providers.reason_not_allowed`.
  - `UnknownProviderFailsValidation`: `Provider = "whatsapp-cloud"` → `ValidationException` sobre `Provider`.
  - `RequiresModulesManage`: sin el permiso → la misma excepción que `ChangeTenantModulesHandler` en ese caso.
  - `AuditsTheChange`: `IAuditRecorder` recibe `"tenancy.tenant_providers.changed"` con `"zenvia:none->active"` y `"reason:contract"`.
- [ ] **Step 2: Run** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter ChangeTenantProviderHandlerTests` — Expected: FAIL.
- [ ] **Step 3: Write the failing API tests** en `OperatorConsoleApiTests`:
  - `TheDetailListsZenviaAsNeverEnabledForANewTenant`: `GET …/operator/tenants/{id}` → `providers == [{ key: "zenvia", status: "none", since: null, lastReason: null }]`.
  - `TheOperatorEnablesAndDisablesZenvia`: `POST …/providers/changes` activar → 200 con `providers[0].status == "active"` y `lastReason == "contract"`; desactivar → `inactive`.
  - `TheHistoryShowsTheProviderBatch`: tras activar, `GET …/history` trae un lote `kind == "provider"` con `changes[0].providerKey == "zenvia"`, `moduleKey == null`.
  - `TheModuleFilterLeavesProviderBatchesOut`: `GET …/history?module=catalog` no incluye el lote de proveedor.
  - `ANonOperatorTenantCannotChangeProviders`: desde un tenant no operador → 403.
- [ ] **Step 4: Run** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter OperatorConsoleApiTests` — Expected: FAIL en las cinco nuevas.
- [ ] **Step 5: Implement** comando, validador, handler, DTOs, lectura del snapshot, filtro del historial y endpoint.
- [ ] **Step 6: Run** los dos comandos — Expected: PASS; y los dos proyectos de Tenancy completos verdes.
- [ ] **Step 7: Commit** — `feat(tenancy): el operador habilita proveedores por tenant`.

### Task 4: Integrations respeta la habilitación

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs` (`IntegrationProvider`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs` (Zenvia la requiere)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsSupport.cs` (`ProviderVisibility`, `ConnectionLoader`, `ConnectionMapping`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs`, `GetIntegrationsCatalog.cs`, `ListConnections.cs`, `CreateConnection.cs`, `UpdateConnection.cs`, `TestConnection.cs`, `PauseConnection.cs`, `ResumeConnection.cs`, `IntegrationsDtos.cs`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ProviderEnablementTests.cs`, pruebas de API existentes de Integrations (agregar casos)

**Interfaces:**
- Consumes: `ITenantProviders` (Task 2), `TenantProviderKeys` (Task 1).
- Produces (contrato que usa Task 6):
  - `IntegrationProvider` gana `bool RequiresTenantEnablement` (parámetro opcional del constructor, default `false`) y `bool IsEnabledFor(IReadOnlySet<string>? enabledProviders)` = `!RequiresTenantEnablement || enabledProviders is null || enabledProviders.Contains(Key)`.
  - `ProviderResponse` (catálogo) gana `bool PlatformEnabled`. El catálogo incluye un proveedor visible por módulo y **no** habilitado sólo si el tenant tiene al menos una conexión suya, con `PlatformEnabled = false` (así la pantalla puede agrupar esas conexiones); sin conexiones, no aparece. Ajuste del autor del plan a la letra del spec §4.5 («no aparece en el catálogo»): para el tenant sin conexiones se cumple igual.
  - `ConnectionResponse` gana `bool ProviderEnabled`.
  - `ProviderVisibility.EnsureEnabledAsync(ITenantProviders, Guid tenantId, IntegrationProvider, CancellationToken)` lanza `RequestForbiddenException("integrations.provider.not_enabled", …)`.
  - Lo exigen: crear, editar, probar, pausar y reanudar (después de la visibilidad por módulo, para que un módulo apagado siga respondiendo `tenancy.module_not_enabled`). No lo exigen: leer una conexión, listarlas y borrarla.
  - `IntegrationConnections.ResolveAsync` y `ListActiveAsync` devuelven `null` / vacío para un proveedor no habilitado. `ListByProviderAsync` no cambia.
- `IntegrationProviders.Zenvia` se construye con `requiresTenantEnablement: true`.

- [ ] **Step 1: Write the failing unit tests** — `ProviderEnablementTests`:
  - `ZenviaRequiresEnablementAndNoOtherProviderDoes`: recorre `IntegrationProviders.All` (o la lista equivalente del catálogo).
  - `EveryProviderThatRequiresEnablementIsATenancyKey`: cada `Key` con `RequiresTenantEnablement` está en `TenantProviderKeys.All` (si no, el operador nunca podría habilitarlo).
  - `IsEnabledForFollowsTheSet`: `IsEnabledFor(null)` true; `IsEnabledFor({})` false para Zenvia y true para `whatsapp-cloud`; `IsEnabledFor({"zenvia"})` true.
- [ ] **Step 2: Run** `dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter ProviderEnablementTests` — Expected: FAIL.
- [ ] **Step 3: Write the failing API tests** en las pruebas de integración de Integrations (con `quotations` activo y sin fila de `zenvia`, salvo donde se diga):
  - `TheCatalogHidesZenviaWhenItIsNotEnabled`: `GET …/integrations/catalog` sin `zenvia`.
  - `TheCatalogOffersZenviaOnceEnabled`: con la fila activa → aparece con `platformEnabled == true`.
  - `ADisabledZenviaWithConnectionsStaysListed`: conexión creada con Zenvia habilitado, luego deshabilitado → catálogo con `zenvia` y `platformEnabled == false`; `GET …/connections` la trae con `providerEnabled == false`.
  - `CreatingAZenviaConnectionWhileDisabledIs403`: `POST …/connections` → 403 `integrations.provider.not_enabled`.
  - `EditingTestingPausingResumingAreRefusedWhileDisabled`: los cuatro → 403 con el mismo código.
  - `DeletingStillWorksWhileDisabled`: `DELETE` → 204.
  - `AModuleOffStillAnswersModuleNotEnabled`: con `quotations` apagado y Zenvia deshabilitado, crear → `tenancy.module_not_enabled` (el orden de los chequeos importa).
  - `ResolveSkipsADisabledProvider`: `IIntegrationConnections.ResolveAsync` sobre la conexión de Zenvia deshabilitado → `null`; `ListActiveAsync(tenant, "zenvia")` → vacío.
  - Preparar las pruebas **existentes** de Zenvia para que habiliten el proveedor en su arreglo (una fila activa en `tenancy.tenant_providers`); si no, fallarán por el cambio y eso no es un bug.
- [ ] **Step 4: Run** `dotnet test tests/Modules/Integrations` — Expected: FAIL en las nuevas.
- [ ] **Step 5: Implement** según **Interfaces**. Los handlers reciben `ITenantProviders` por constructor.
- [ ] **Step 6: Run** `dotnet test tests/Modules/Integrations` y `dotnet test Backend.slnx` — Expected: todo verde, incluidas Messaging y Shopify, que pasan por `IIntegrationConnections`.
- [ ] **Step 7: Commit** — `feat(integrations): Zenvia sólo para tenants con el proveedor habilitado`.

## Frontend

### Task 5: Consola de operador — sección «Proveedores» e historial

**Files:**
- Modify: `src/features/operator/types/operator.ts`, `src/features/operator/services/operator.api.ts`
- Create: `src/features/operator/components/provider-card.tsx`, `src/features/operator/components/change-confirmation-fields.tsx` (el bloque de motivo y nota, extraído de `module-change-dialog.tsx`), `src/features/operator/components/provider-change-dialog.tsx`
- Modify: `src/features/operator/components/module-change-dialog.tsx` (usa `ChangeConfirmationFields`), `src/features/operator/components/history-batch-card.tsx`, `src/features/operator/pages/operator-tenant-modules-page.tsx`, la ruta que la monta en `src/routes/_authenticated/operator/tenants/$targetTenantId/index.tsx`
- Test: `src/features/operator/services/operator.api.test.ts`, `src/features/operator/components/provider-change-dialog.test.tsx`, `src/features/operator/pages/operator-tenant-modules-page.test.tsx`, `src/features/operator/pages/operator-history-page.test.tsx`, `src/routes/_authenticated/operator/tenants/$targetTenantId/index.test.tsx`, fixtures en `src/test/operator.ts`

**Interfaces:**
- Consumes: contrato HTTP de Task 3.
- Produces:
  - `export const TENANT_PROVIDER_KEYS = ['zenvia'] as const`, `type TenantProviderKey`, `PROVIDER_LABELS: Record<TenantProviderKey, string> = { zenvia: 'Zenvia (WhatsApp)' }`, `isTenantProviderKey(value: string)`.
  - `interface OperatorTenantProvider { key: string; status: string; since: string | null; lastReason: string | null }`; `OperatorTenantDetail.providers: OperatorTenantProvider[]` (un backend viejo sin el campo se lee como `[]`).
  - `OperatorHistoryKind` suma `'provider'`; `OperatorHistoryChange.providerKey?: string | null`.
  - `changeTenantProvider(tenantId: string, targetTenantId: string, input: { provider: TenantProviderKey; status: TargetStatus; reason: ChangeReason; note: string | null }): Promise<OperatorTenantDetail>` → `POST …/operator/tenants/{target}/providers/changes`, con `X-Tenant-Id` y `X-Qep-Client` como `changeTenantModules`.
  - `ProviderChangeDialog` con props `{ direction: ModuleChangeDirection; provider: TenantProviderKey; onClose; onConfirm(confirmation: ChangeConfirmation); isSubmitting; error? }`; motivos de `MODULE_REASONS[direction]`.
- Ubicación: bajo la grilla de módulos, encabezado «Proveedores». Tarjeta como `ModuleCard` (switch con el estado del servidor, que nunca cambia solo; badge `MODULE_STATUS_LABELS`; `Desde` y `Último motivo`), sin «Depende de».
- Historial: un lote `provider` se titula «Cambio de proveedor» y cada cambio se describe con `PROVIDER_LABELS` (clave cruda si este build no la conoce).
- La respuesta del cambio reemplaza la caché del detalle, igual que el de módulos.

- [ ] **Step 1: Write the failing tests:**
  - `operator.api.test.ts` › `changeTenantProvider posts the change with the CSRF and tenant headers` (URL, método, body exacto, headers) y `reads a detail without providers as an empty list`.
  - `provider-change-dialog.test.tsx` › `offers only the activation reasons when enabling` (Contrató, Cortesía, Corrección), `offers only the deactivation reasons when disabling` (Falta de pago, Ya no continúa, Corrección), `sends the reason and the trimmed note`.
  - `operator-tenant-modules-page.test.tsx` › `shows Zenvia as never enabled with its switch off`, `asks for confirmation before enabling and applies the answer`, `keeps the switch where the server has it when the change fails`, `disables the switch without operator.modules.manage`.
  - `operator-history-page.test.tsx` › `describes a provider batch with the provider name`.
  - `index.test.tsx` de la ruta › carga, error y éxito de la sección con el detalle real del router.
- [ ] **Step 2: Run** `VITE_API_BASE_URL= bun run test src/features/operator 'src/routes/_authenticated/operator'` — Expected: FAIL en las nuevas.
- [ ] **Step 3: Implement** tipos, API, componentes y la sección; `module-change-dialog.test.tsx` debe seguir verde tras extraer `ChangeConfirmationFields`.
- [ ] **Step 4: Run** el mismo comando, `bun run lint` y `bun run build` — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(operator): enable and disable Zenvia per tenant`.

### Task 6: Integraciones — proveedor deshabilitado por la plataforma

**Files:**
- Modify: `src/features/integrations/types/integrations.ts` (y su schema `integrations.schema.ts`), `src/features/integrations/components/provider-catalog.tsx`, `src/features/integrations/components/connection-list.tsx`, `src/features/integrations/utils/describe-integration-failure.ts`
- Test: `src/features/integrations/types/integrations.schema.test.ts`, `src/features/integrations/components/provider-catalog.test.tsx`, `src/features/integrations/components/connection-list.test.tsx`, `src/features/integrations/utils/describe-integration-failure.test.ts`, `src/routes/_authenticated/integrations/index.test.tsx`, fixtures en `src/test/integrations.ts`

**Interfaces:**
- Consumes: contrato de Task 4.
- Produces:
  - `IntegrationProvider.platformEnabled: boolean` y `IntegrationConnection.providerEnabled: boolean`; ausentes (backend viejo) se leen `true`.
  - El catálogo de oferta («Conectar») no muestra un proveedor con `platformEnabled === false`.
  - En `ConnectionList`, una conexión con `providerEnabled === false` muestra la insignia «Deshabilitado por la plataforma» en lugar de su estado, una línea «Tu plan no incluye este proveedor. Puedes borrar la conexión; para volver a usarla, pide que lo habiliten.», y como única acción **Borrar**.
  - `describeIntegrationFailure` traduce `integrations.provider.not_enabled` a «Este proveedor no está habilitado para tu empresa.».

- [ ] **Step 1: Write the failing tests:**
  - schema › `reads platformEnabled and providerEnabled, defaulting to true when absent`.
  - `provider-catalog.test.tsx` › `does not offer a provider the platform disabled`.
  - `connection-list.test.tsx` › `marks a connection of a disabled provider and offers only Borrar` (sin Editar, Probar, Pausar ni Reanudar).
  - `describe-integration-failure.test.ts` › `explains a provider the platform did not enable`.
  - `integrations/index.test.tsx` › `lists a Zenvia connection as disabled by the platform` con catálogo y conexiones del backend nuevo.
- [ ] **Step 2: Run** `VITE_API_BASE_URL= bun run test src/features/integrations src/routes/_authenticated/integrations` — Expected: FAIL en las nuevas.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** el mismo comando, `VITE_API_BASE_URL= bun run test` completo, `bun run lint` y `bun run build` — Expected: PASS (una falla de una prueba ajena que pasa aislada se reporta por nombre, no se ignora).
- [ ] **Step 5: Commit** — `feat(integrations): show providers the platform disabled`.

## Cierre del plan

- [ ] `dotnet test Backend.slnx` verde y `VITE_API_BASE_URL= bun run test` verde en los worktrees.
- [ ] Probar a mano contra el backend local: habilitar Zenvia a Origen Botánico desde la consola, verlo en Integraciones, deshabilitarlo y ver la conexión marcada.
- [ ] Siguiente: plan 2 (canal de envío por conexión y retiro de la cuenta global), que consume `ITenantProviders` vía `IIntegrationConnections`.
