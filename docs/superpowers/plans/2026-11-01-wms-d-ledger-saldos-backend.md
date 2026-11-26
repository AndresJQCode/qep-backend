# WMS-D · Ledger de inventario y saldos — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El núcleo de inventario del módulo `Wms`: un ledger inmutable de movimientos con origen y destino, saldos materializados en la misma transacción, idempotencia por `Idempotency-Key` (header en las rutas **IK**, clave derivada en el servidor en las rutas **SK**), ajustes manuales, carga inicial por Excel, reverso de movimientos, consulta y exportación de saldos, reconciliación nocturna y particionamiento mensual.

**Architecture:** Sobre los proyectos `Modules.Wms.*` que entrega WMS-B. El área `Inventory` del dominio (que por regla de `WmsLayerTests` no conoce ninguna otra área: sus referencias a owner, bodega y ubicación son `Guid` blandos) define `StockKey`, `LedgerEntry`/`LedgerBatch` y las reglas puras; el documento `Adjustment` vive en `Control`. `InventoryLedger` (Infrastructure) es el **único escritor** de `wms.stock_movements` y `wms.stock_balances`: ordena las claves, inserta los movimientos y hace el upsert del saldo con `ON CONFLICT … NULLS NOT DISTINCT`. La idempotencia es un filtro de endpoint que lee el header y un servicio scoped que inserta el recibo en la misma transacción. Las lecturas van por SQL sobre `stock_balances`/`wms.v_stock_summary`, nunca por el ledger. Workers: coalescencia de `balance.changed`, reconciliación nocturna, particiones y purgas.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10.0.13 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (PostgreSQL 18), FluentValidation, ClosedXML 0.105.1, `System.Diagnostics.Metrics`, xUnit v3, Testcontainers.PostgreSql 4.14.0 (`postgres:18-alpine`), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-10-31-wms-arquitectura-design.md`](../specs/2026-10-31-wms-arquitectura-design.md), §5, §6, §7 (sólo modelo de datos), §11.2–11.4, §16.1, §19, §20.2, §21, §24.1–24.3, §26 (ADR-02/03/04/05/06/08/10). Es autoridad: sus decisiones no se reabren. Las decisiones de la ronda 2 de revisión (S1–S26, 2026-11-01) ya están incorporadas al spec y a este plan; donde el spec todavía no las refleje, gana la decisión y se anota en «Decisiones del plan». **Plan previo:** [`2026-11-01-wms-b-modulo-bodegas-backend.md`](2026-11-01-wms-b-modulo-bodegas-backend.md); sus interfaces se usan por nombre.

**Fuera de alcance:** recepciones, putaway, traslados, conteos y novedades (WMS-E en adelante); casos de uso de reservas (fase 3: aquí nacen sólo la tabla y la columna `reserved`); API de seriales (sólo tabla y escritura por el ledger); la cola móvil `wms_receipts` (WMS-G); la novedad `LedgerDrift` como documento (el agregado `Discrepancy` no existe aún: la reconciliación escribe `balance_checks` y sube el gauge); el frontend; `docs/estado.md` (se toca en `develop` al mergear).

---

## Antes de empezar

- **Precondición:** `develop` ya tiene mergeado WMS-B. Si `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` no existe, este plan no arranca.
- Worktree y rama: `git worktree add ../qep-backend-worktrees/wms-d -b feature/wms-d develop` desde el checkout principal; todo lo demás corre dentro de `../qep-backend-worktrees/wms-d`. `git branch --show-current` justo antes de cada commit; si dice `main` o `develop`, frena.
- **Los comandos son PowerShell**: sin `&&`; `A; if ($?) { B }`; `$env:VAR = "valor"` en línea aparte.
- **`Api.exe` detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`: `Stop-Process -Name Api -ErrorAction SilentlyContinue`.
- **Pruebas de integración:** Docker arriba y `$env:Seed__Enabled = "false"` en línea aparte antes de `dotnet test`. El harness de WMS-B levanta un contenedor por prueba (`OpenAsync`), no una plantilla clonada.
- **Por tarea se corren sólo las clases tocadas, con `--filter "FullyQualifiedName~<Clase>"`.** Nunca la suite completa.
- **Nunca reescribas un `.cs` con `Set-Content`/`Out-File`**: rompe tildes y deja BOM. Se edita con la herramienta de edición.
- Migraciones: `dotnet ef migrations add AddInventoryLedger --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations`.
- Commits: conventional commits en inglés, **sin** `Co-Authored-By` ni atribución de IA, aunque el harness la sugiera.
- Nunca imprimir secretos.

## Interfaces de WMS-B que este plan consume (nombres exactos de su Task 6)

Se usan tal cual y no se redefinen. Lo que este plan **agrega** a un archivo de WMS-B se marca en la tarea correspondiente como modificación.

| Pieza | Forma fijada por WMS-B |
| --- | --- |
| Proyectos | `Modules.Wms.{Domain,Application,Infrastructure,Api}`; `tests/Modules/Wms/Modules.Wms.{UnitTests,IntegrationTests}`; `tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs` con `DomainAreasOnlyDependDownwards` (`Inventory` → nada; `Warehousing` → `Inventory`; `Control` → `Inventory`, `Warehousing`) |
| Dominio existente | `Modules.Wms.Domain.Inventory`: `UomCode(string Value)`, `TrackingPolicy { None, Lot, Serial }`, `UnitOfMeasure`. `Modules.Wms.Domain.Warehousing`: `WarehouseId`/`LocationId`/`OwnerId` (`readonly record struct (Guid Value)`, `New()`), `LocationRole`, `LocationStatus { Active, Blocked, Counting }`, `LocationCodes.Adjustment = "ADJUSTMENT"` …, `Location` (`Id`, `TenantId`, `WarehouseId`, `Code`, `Path`, `Role`, `IsVirtual`, `AllowsStorage`, `AllowsPicking`, `Status`, `Version: long`, y la propiedad calculada **`AllowsNegative => Role is Supplier or Customer or Adjustment`** (S1): `TRANSIT` es virtual pero **no** admite negativo), `Warehouse` (`VirtualLocation(LocationRole)`, `FindLocation(LocationId)`, `Version: long`), `Owner` (`Id`, `Kind`, `Name`), `VariantLogisticsProfile` (`BaseUom: UomCode`, `BaseUomPrecision` 0..6, `Tracking`, `TracksExpiry`, `Conversions: IReadOnlyList<UomConversion { Code, Factor }>`, `Version: long`). `WmsDomainException(code, message)` y `(code, message, inner)`; `WmsErrorCodes` con §24.2 + P-6 (`wms.warehouse.not_found`, `wms.location.not_found`, `wms.owner.not_found`, `OwnerHasStock = "wms.owner.has_stock"`, `LocationVirtualImmutable = "wms.location.virtual_immutable"`, …). `IdentifierKind { Variant, Presentation, Lot, LogisticUnit }` **sin `Location`** (S7): las ubicaciones se resuelven por código dentro de la bodega |
| `IWmsUnitOfWork` | `Task<int> SaveChangesAsync(CancellationToken)`. **Este plan le agrega** `BeginTransactionAsync` y `FlushAsync` (T4: escribe en la transacción abierta sin confirmarla, para los lotes nuevos que la FK del saldo necesita en la base) y `TryLockDocumentAsync` (T5) |
| `WmsUnitOfWork` + `UniqueViolation.Is(DbUpdateException, params string[] constraintNames)` + `internal static class ForeignKeyViolation { static bool Is(Exception exception, string constraintName) }` (23503/23001) | traducción por nombre con las constantes `public const string` de `WmsDbContext` (`WarehouseCodeIndex`, `LocationCodeIndex`, `LocationVirtualRoleIndex`, `LocationParentForeignKey`, `OwnerSelfIndex`, `UomPrimaryKey`, `ConversionCodeIndex`, `IdentifierValueIndex`). Las constantes nuevas de este plan siguen ese estilo y se traducen en el mismo método; las FKs internas del ledger (`adjustment_lines → adjustments`, `reservations`/`lots` → owners) se reconocen con `ForeignKeyViolation.Is`, no con una copia. `CheckViolation` (T4) es la única pieza nueva y recibe `PostgresException` porque el upsert no pasa por `SaveChanges` |
| `IWmsAuditRecorder` | `void Record(Guid tenantId, Guid actorId, WmsActorType actorType, string action, string resourceType, string resourceId, IReadOnlyDictionary<string, string?> changedFields, DateTimeOffset occurredAt)`; `enum WmsActorType { Human, System, ApiKey }`; `WmsAuditActions` con `Resources` (`Warehouse = "wms.warehouse"`, …). Los handlers de WMS-B pasan `executionContext.SubjectId` como `actorId` y `Resources.X` como `resourceType` |
| `public sealed class WmsAuthorization(ITenantModules tenantModules)` — scoped, registrada en `AddWmsInfrastructure` — con `public Task EnsureAsync(IExecutionContext ctx, Guid tenantId, string permission, Guid? ownerId = null, Guid? warehouseId = null, CancellationToken ct = default)` y `public static RequestForbiddenException Denied()` | **No es estática** (S18). Los handlers la reciben por constructor junto con `IExecutionContext`; ninguno recibe `ITenantModules`. Los dos opcionales se ignoran hasta fase 3 (`wms.member_scopes` nace vacía en `InitialWms`, con `DbSet<MemberScope> MemberScopes`); este plan los pasa cuando los tiene a mano (`ownerId` del ajuste, `warehouseId` del filtro) y nunca depende de ellos. Toda llamada de este plan es `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.X, ct: cancellationToken)`; el filtro de idempotencia usa la estática `WmsAuthorization.Denied()` para el 403 de tenant |
| `IWmsProvisioner.EnsureAsync(Guid tenantId, CancellationToken ct)` | crea `Owner Self` y las UoM del sistema con `INSERT … ON CONFLICT DO NOTHING` en transacción propia (S5, patrón `PipelineProvisioner`). **Sólo el provisioner escribe el `Self`**; todo handler de este plan que necesite el `Self` llama primero `EnsureAsync` y después `GetSelfAsync` |
| `WmsCaller(IMembershipDirectory, IExecutionContext)` | `Task<Guid?> FindAsync(Guid tenantId, ct)`, `Task<Guid> RequireAsync(Guid tenantId, ct)` (membresía activa o 403); scoped. **Es el `actor_id` del movimiento** |
| `WmsNotFound` | `Warehouse(Guid)`, `Location(Guid)`, `Owner(Guid)` en `WmsSupport.cs`; **este plan agrega** `Adjustment`, `Movement`, `Lot`, `ImportPreview` (T8/T9/T10) |
| `WmsVersions.Ensure(long current, long? received)` | 412 `concurrency.conflict`. Toda `version` de este plan es `long` |
| `WmsPaging` | `DefaultPageSize = 50`, `MaxPageSize = 200`, `NormalizePage`, `NormalizePageSize` |
| `WmsPermissions` | `StockRead`, `StockExport`, `AdjustmentPropose`, `AdjustmentApprove`, `DocumentRead` (y las demás 11), con `PermissionDefinition` y política |
| `IWarehouseRepository` | `GetAsync(Guid tenantId, WarehouseId, ct)` (con `Locations`, rastreado), `GetLocationAsync(Guid tenantId, LocationId, ct)` → `Location?`, `GetVirtualLocationAsync(Guid tenantId, WarehouseId, LocationRole, ct)` → `Location?`. **No tiene** búsqueda por código: `FindLocationByCodeAsync` se agrega en T10 |
| `IOwnerRepository` | `FindAsync(Guid tenantId, OwnerId, ct)` → `Owner?`; `GetSelfAsync(Guid tenantId, ct)` → `Owner`, **lanza `InvalidOperationException` si el `Self` no existe** (S5): no provisiona. Por eso el orden fijo en los handlers es `IWmsProvisioner.EnsureAsync` → `GetSelfAsync` |
| `IVariantProfileRepository.GetOrCreateDefaultAsync(Guid tenantId, Guid variantId, ct)` | crea y audita como `System`; lanza `wms.profile.variant_unknown`; el llamador guarda. Si dos peticiones crean el perfil a la vez, la que pierde recibe **412 `concurrency.conflict`** al guardar (WMS-B lo traduce en `WmsUnitOfWork`): **WMS-D lo trata como «recargar»** (se reintenta y ya encuentra el perfil), nunca como un error del movimiento ni como 500 |
| `IVariantStockProbe.HasStockAsync(Guid tenantId, Guid variantId, ct)` / `ILocationStockProbe.HasStockAsync(Guid tenantId, Guid locationId, ct)` / `IOwnerStockProbe.HasStockAsync(Guid tenantId, Guid ownerId, ct)` | `NoVariantStockProbe`/`NoLocationStockProbe`/`NoOwnerStockProbe` en `NoStockProbes.cs`; **T15 reemplaza las tres** en la misma tarea (`OwnerStockProbe` real respalda `wms.owner.has_stock`) |
| `IWmsCatalogReader.GetVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, ct)` | → `IReadOnlyList<WmsVariantSnapshot>`; `WmsVariantSnapshot(Guid VariantId, Guid ProductId, string ProductName, string VariantName, string Code, string? Barcode, IReadOnlyList<int> PackagingUnits, bool IsActive)`; adaptador `src/Bootstrapper/WmsCatalogReader.cs(IProductRepository, IOptionDefinitionRepository)`. **T10 agrega** `FindVariantsByCodesAsync` al puerto y al adaptador |
| `WmsDbContext` | `Schema = "wms"`; `DbSet<Warehouse> Warehouses`, `Locations`, `Owners`, `UnitsOfMeasure`, `VariantProfiles`, `Identifiers`, `MemberScopes` (vacía hasta fase 3); `internal DbSet<AuditEntry> AuditEntries`, `internal DbSet<WmsOutboxMessage> Outbox` (proyección de `platform.outbox_messages`: `Id`, `EventName`, `PayloadJson`, `OccurredAt`); última migración `*_InitialWms`; columnas snake_case: `wms.locations (id, tenant_id, warehouse_id, code, path, role, status, is_virtual, allows_storage, allows_picking, …)`, `wms.warehouses (id, tenant_id, code, …)`, `wms.owners (id, tenant_id, kind, name, …)` |
| `WmsApiHarness` (copia de `LoansApiHarness`) | `const string TenantId, SubjectId, OtherTenantId, OtherSubjectId`; `WmsUrl(string tenantId = TenantId)`; `StartDatabaseAsync()`; `SeedTenantAsync(factory, tenantId, modules)`; `CreateClient(factory, subjectId, tenantId, params string[] permissions)`; `AllPermissions`; `CreateManager(factory)`; `CreateReader(factory)`; `OpenAsync(modules = null)` → `(PostgreSqlContainer Database, QepApiFactory Factory)`; `BodyAsync(response)`; `CodeOfAsync(response)`; `CountAuditEntriesAsync(factory, action, resourceId)`; `CreateVariantAsync(factory, code, barcode, packagingUnits, name)` → `Guid`; `AnyCityIdAsync(factory)`; `QepApiFactory(connectionString, Action<IServiceCollection>? configureServices = null)`; WMS-B la declara `internal static partial class`. **No existen** `RegisterTenantAsync`, `EnableWmsAsync`, `ProblemAsync`, `ScalarAsync`, `ExecuteAsync`, `CreateWarehouseAsync`, `ListLocationsAsync`: los que hacen falta nacen en `WmsApiHarness.Inventory.cs` (T3). **Este plan modifica `WmsApiHarness.cs` una vez, en T6**: agrega a `QepApiFactory.ConfigureWebHost` los `UseSetting("Wms:Workers:*")` que neutralizan los workers en pruebas, desde el primer worker que existe (`WmsHousekeepingWorker`) |
| Registro | handlers y validadores de `Modules.Wms.Application` se registran a mano en el bloque Wms de `QepServiceCollectionExtensions`; `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` lo vigila |

**Supuestos que quedan abiertos (se verifican en T1 y se adaptan sin cambiar el diseño):** (a) WMS-B declara `WmsApiHarness` como `partial`; si el merge no lo trajo así, el primer paso de T3 es agregarle `partial`; (b) si WMS-B no conservó `SeedCallerMembershipAsync` de `LoansApiHarness`, T3 lo copia a `WmsApiHarness.Inventory.cs` (el `WmsCaller.RequireAsync` del `post` lo necesita); (c) si `Modules.Wms.Domain.csproj` no tiene `InternalsVisibleTo`, T1 se lo agrega hacia `Modules.Wms.Infrastructure` y los dos proyectos de pruebas.

## Global Constraints

- «La unidad de inventario es la **variante**, nunca el producto.» Toda clave lleva `variant_id`.
- «El saldo está **siempre en la unidad base** de la variante (D-03).» `quantity`, `on_hand`, `reserved` en `numeric(18,6)`; el movimiento guarda `captured_uom`, `captured_qty`, `conversion_factor`; «el servidor recalcula el factor desde el perfil y rechaza si no cuadra (`wms.uom.factor_mismatch`)». `BaseUomPrecision` va de 0 a 6.
- Clave del saldo: `(tenant_id, owner_id, location_id, variant_id, lot_id, status)`, `UNIQUE … NULLS NOT DISTINCT`; `status ∈ { Available, Received, Quarantine, Blocked, Damaged, InTransit }`.
- `on_hand(k) ≥ 0` salvo que la ubicación admita negativo: `stock_balances.allows_negative boolean NOT NULL`, copiado por el ledger de `Location.AllowsNegative` (`SUPPLIER`, `CUSTOMER`, `ADJUSTMENT`; `TRANSIT` no) (S1); «Σ on_hand sobre todas las ubicaciones incluidas las virtuales = 0». Los `CHECK`, con una sola redacción: `CHECK (on_hand >= 0 OR allows_negative)` y `CHECK (reserved >= 0 AND (allows_negative OR reserved <= on_hand))`.
- «La aplicación no tiene método de Update/Delete sobre `stock_movements`»; trigger `BEFORE UPDATE OR DELETE` que lanza `wms.ledger.immutable`; el `Down` de la migración del ledger queda **vacío con comentario** (S11). El trigger no cubre `TRUNCATE`/`DROP`/`DETACH`: el rol de la aplicación no recibe `TRUNCATE` sobre `wms.stock_movements` (configuración del rol en la plataforma, no una migración).
- «Corrección = movimiento compensatorio (`reason = Reversal`, `reverses_movement_id`)», misma `batch_id` que el original. `reverses_movement_id` es **referencia blanda** (sin FK: PostgreSQL no admite FK hacia una tabla particionada salvo por la clave completa) acompañada de `reverses_occurred_at timestamptz` para ubicar la partición (S4). Ninguna FK apunta a `stock_movements`.
- Upsert síncrono con el SQL de §6.3; «el ledger ordena las claves de un batch por `(location_id, variant_id, lot_id, status)`»; «lotes de hasta 500 movimientos por transacción» (RNF-07).
- **Ventana de `occurredAt`** (S12): el servidor acepta `occurredAt ∈ [now − 30 días, now + 5 min]`; fuera, 422 `wms.movement.occurred_at_out_of_range`. Nunca se desprende (`DETACH`) una partición de un mes dentro de esa ventana; la `DEFAULT` existe como red.
- **Una sola regla de idempotencia** (S2). Rutas **IK** (mutaciones sin documento versionado: `POST /adjustments`, `POST /imports/initial-balances/commit`, `POST /stock/movements/{id}/reverse`): header `Idempotency-Key` obligatorio, 428 `wms.idempotency_key_required` si falta; repetición con el mismo `request_hash` → **la respuesta guardada con 200**; otro `request_hash` → 409 `wms.idempotency_key_reused`. Ruta **SK** (acción sobre un documento versionado; en este plan sólo `POST /adjustments/{id}/post`): la clave la deriva el servidor, `"adjustment:{id}:post:{version}"`, se guarda en `operation_receipts` igual y el header se ignora si viene. El filtro compara `executionContext.TenantId` con el `tenantId` de la ruta **antes** de buscar el recibo y responde 403 si difieren (nunca revela si la clave existe). Recibo en `wms.operation_receipts (tenant_id, idempotency_key, request_hash, response_json, created_at)`, PK `(tenant_id, idempotency_key)`, en la misma transacción; purga a **45 días** (S13: sesión offline máxima 30 + margen).
- `stock_movements` «particionada por rango de `occurred_at` (mensual)», PK `(id, occurred_at)`, límites de partición en UTC (`AT TIME ZONE 'UTC'`), partición `DEFAULT` + alerta `wms_partition_missing`; índices `(tenant_id, variant_id, occurred_at DESC)`, `(tenant_id, document_type, document_id)`, `(from_location_id, occurred_at)`, `(to_location_id, occurred_at)`, `(batch_id)`.
- `stock_balances`: índices `(tenant_id, variant_id, status)`, `(location_id)`; los dos `CHECK` de arriba; «filas con `on_hand = 0 AND reserved = 0` se podan por worker».
- `reservations`: índice parcial `(tenant_id, owner_id, location_id, variant_id, lot_id, status) WHERE status = 'Open'`; `(demand_type, demand_id)`.
- Lecturas «sólo sobre `stock_balances`»; `available` y `pickable` se calculan **una sola vez en SQL**: `wms.v_stock_summary` (S10) con columnas exactamente `tenant_id, owner_id, warehouse_id, variant_id, on_hand, received, available, pickable, reserved, blocked, in_transit`, agrupada por `(tenant, owner, warehouse, variant)`, que usa `l.is_virtual` y `l.allows_picking` del join a `locations`; `expected` se compone aparte (0 en WMS-D). «Los saldos de las virtuales se escriben pero no se exponen como existencia».
- Paginación `page`/`pageSize` (50/200, precedente de Catalog/Customers/Suppliers; Loans usa 25/100), respuesta `{ items, page, pageSize, totalCount }`; `totals` en `GET /stock`; `pickable` en `StockRowDto` y en `/stock/summary`; enums por nombre; `occurredAt` lo manda el cliente, `recordedAt` el servidor; `version` es `long`; 403 siempre `authorization.denied`, nunca 404 para «existe pero no es tuyo». Con `RequiredModules: [Wms]`, el módulo apagado responde **403 sin cuerpo** (la máscara de entitlements quita el permiso antes del handler; `tenancy.module_not_enabled` es defensa en profundidad) (S17).
- Motivos de ajuste: `InitialLoad, CountVariance, Damage, Loss, Found, Correction, Other` (RF-023).
- Carga inicial: columnas `code, location, lot, expiration, quantity, uom`; «coincidencia exacta por `variant.Code`, rechazo de ambigüedades y un solo batch de movimientos» (RF-024). La ubicación se resuelve **por código dentro de la bodega**, nunca por `identifiers` (S7). El preview responde **200 con issues por fila** y `canCommit`; más de 500 filas → 422 `wms.import.too_many_rows` en el preview; el commit con issues → 422 (S21).
- Auditoría atómica por `IWmsAuditRecorder` con `ownerId` dentro de `changedFields` (§10.3; el recorder serializa `changed_fields` como objeto JSON, S23); outbox `wms.adjustment.posted.v1`, `wms.balance.changed.v1` «agregado por `(owner, warehouse, variant)` con `available`, coalescido por worker» y `wms.export-ready.v1` (S15), todos como `WmsOutboxMessage` proyectado sobre `platform.outbox_messages` (S19).
- Meter `Qep.Wms` con `wms_movements_total`, `wms_confirm_duration_ms`, `wms_balance_drift`, `wms_partition_missing` (RNF-11).
- `Modules.Wms.Application` no referencia EF Core ni Npgsql; `Modules.Wms.Domain` no referencia otro módulo; `Modules.Wms.Domain.Inventory` no referencia otra área (`DomainAreasOnlyDependDownwards`).
- Textos visibles (mensajes de problem, correo de exportación): español colombiano, tuteando. Identificadores y commits en inglés; el «por qué» de los comentarios en español, como el resto del repo.

## Decisiones donde el spec dejaba margen

| Tema | Decisión | Por qué |
| --- | --- | --- |
| Ids en `Inventory` | `StockKey`, `LedgerEntry`, `StockMovement`, `Lot`, `SerialUnit`, `Reservation` llevan `Guid` para owner, bodega y ubicación (referencias blandas, como `variant_id`); los tipos fuertes `OwnerId`/`LocationId`/`WarehouseId` viven en `Warehousing` y `Inventory` no puede conocerlos | `WmsLayerTests.DomainAreasOnlyDependDownwards` de WMS-B: `Inventory` no depende de nada |
| Dónde vive `Adjustment` | `Modules.Wms.Domain.Control` (puede usar `Inventory` y `Warehousing`), con ids fuertes | Es un documento de control de inventario, no una primitiva del ledger |
| Idempotencia: filtro o servicio | **Las dos cosas, con un solo filtro en dos modos** (S2). `IdempotencyKeyFilter` corre en toda ruta IK o SK: primero compara `executionContext.TenantId` con el `tenantId` de la ruta y lanza `WmsAuthorization.Denied()` si difieren; luego obtiene la clave (modo **header**: `Idempotency-Key`, 428 si falta; modo **servidor**: `"{documento}:{id}:{acción}:{version}"`, header ignorado), calcula `request_hash`, y si ya hay recibo devuelve la respuesta guardada o 409; si no, deja `(key, hash)` en `IdempotencyScope` (scoped). El handler llama `IOperationReceipts.Record(response)` antes de `SaveChangesAsync`, así el recibo commitea con los movimientos. Una carrera de dos requests iguales la cierra la PK: la UoW la traduce a `OperationReplayedException`, el `await using` del handler deshace la transacción y el filtro relee el recibo | El filtro no puede escribir en la transacción del handler; el handler no debería leer headers; comprobar el tenant antes de `FindAsync` evita que otro tenant sondee claves ajenas |
| `request_hash` | Modo header: SHA-256 sobre `"{método} {ruta}\n" + JSON del DTO ya enlazado`, serializado con `JsonSerializerDefaults.Web`. Modo servidor: el mismo hash con cuerpo `null`: la `version` ya viaja en la clave, así que una repetición de la misma acción sobre la misma versión **siempre** reproduce la respuesta guardada y una ruta SK nunca responde 409 | El DTO enlazado tiene orden de propiedades fijo: dos cuerpos equivalentes con propiedades en otro orden dan el mismo hash (Review Focus 3). En SK el cliente no mandó clave: un 409 `idempotency_key_reused` no tendría sentido para él |
| El reverso es IK, no SK | `POST /stock/movements/{id}/reverse` (RF-025) exige header `Idempotency-Key`: un movimiento es inmutable y no tiene `version`, así que no hay clave de servidor que derivar. La única ruta SK de este plan es `POST /adjustments/{id}/post` con `"adjustment:{id}:post:{version}"`. No existe `/adjustments/{id}/reverse`. Reversar dos veces es 422 `wms.movement.already_reversed` (§24.2: «Reversar un movimiento ya reversado»), el mismo código que reversar un `Reversal` | Corrección del coordinador a S2 (2026-11-01): S2 enumera las acciones sobre documentos versionados; el reverso no es una de ellas |
| Saldo insuficiente con `available` | El upsert de una clave **sin `allows_negative`** con delta negativo es `UPDATE … WHERE … AND on_hand + @delta >= reserved RETURNING on_hand, reserved` (S26); cero filas → el ledger lee `on_hand, reserved` de esa clave (0 si no hay fila): si `on_hand + delta < 0` lanza `StockInsufficientException` con `available = on_hand − reserved`; si `>= 0` pero `< reserved`, `WmsDomainException(StockReserved)`. Los `CHECK` son la garantía de último recurso: como el upsert corre por `ExecuteSqlAsync`/`SqlQuery` y **no pasa por `SaveChanges`**, el `PostgresException` llega directo (nunca envuelto en `DbUpdateException`), y es el **ledger** quien lo captura con `SqlState == "23514"` y lo mapea por `ConstraintName` (sin extensiones) | El `CHECK` aborta la transacción y ya no se puede leer el saldo para la extensión; el `UPDATE` toma el mismo bloqueo de fila que el `ON CONFLICT DO UPDATE`; una traducción en `WmsUnitOfWork.SaveChangesAsync` nunca se ejecutaría para este SQL |
| Deltas por clave | El batch se **netea por clave** antes del upsert: una clave que aparece como origen y destino se escribe una vez | Menos filas bloqueadas y un orden total trivial |
| Actor del movimiento | `actor_id` = membresía activa (`WmsCaller.RequireAsync`), como fijó WMS-B; la auditoría sigue llevando `SubjectId`, como todos los handlers de WMS-B; `actor_type` se guarda con el enum de dominio `LedgerActorType`, espejo de `WmsActorType` amarrado por prueba | `Inventory` no puede ver `WmsActorType` (Application) |
| `balance.changed` | Tabla `wms.balance_change_queue (tenant_id, owner_id, warehouse_id, variant_id, queued_at)` con PK en las cuatro columnas; el ledger la alimenta con **un solo `INSERT … ON CONFLICT (…) DO UPDATE SET queued_at = EXCLUDED.queued_at` multi-fila**, claves distintas ordenadas por `(TenantId, OwnerId, WarehouseId, VariantId)` (S26), en la misma transacción y como última escritura del lote; `BalanceChangePublisher` cada 10 s hace `DELETE … RETURNING`, calcula el resumen por clave desde `v_stock_summary` y publica un evento por clave | Un barrido por `updated_at` necesita una marca de agua y pierde cambios con relojes distintos; la cola es exacta y vacía en reposo. `DO NOTHING` no bloquea una fila ya commiteada: el publicador podía sacarla mientras un escritor seguía sin confirmar, leer el saldo sin ese cambio y perder el evento para siempre, porque la reconciliación no reencola. Con `DO UPDATE` la fila queda bloqueada hasta el commit y `SKIP LOCKED` la deja para la siguiente vuelta; el costo aceptado es serializar a los escritores de una misma `(tenant, dueño, bodega, variante)` desde el encolado hasta confirmar. Varios `INSERT` sueltos en orden de aparición reabrirían el deadlock que el orden de `StockKeyComparer` cierra: dos batches cruzados tomarían los bloqueos de la cola en orden opuesto |
| `POST /stock/export` | Como Suppliers: el Excel se arma en el request (tope 50 000 filas), se sube por `IWmsExportStorage` como `FileOwnerType.WmsExport` (módulo `wms` en `FileOwnerModules.ByOwnerType`, S14), se publica **`wms.export-ready.v1`** —nunca `suppliers.export-ready.v1`— y lo consume `WmsExportDeliveryWorker` con `WmsExportEmailTemplate` en Notifications, modelados sobre `SupplierExportDeliveryWorker`/`SupplierExportEmailTemplate` (S15). Devuelve 202 `{ fileId, fileName, rowCount, expiresAt }` | Es el precedente que el brief pide calcar; el job encolado de Quotations (`ExportJob`) queda como camino de upgrade si 50 k filas pasan de los segundos del request. Reutilizar el evento de Suppliers mandaría el correo con el asunto y la plantilla de proveedores |
| Partición faltante | Función SQL `wms.ensure_movement_partition(date)` creada en la migración y llamada por el worker, con límites en UTC (`AT TIME ZONE 'UTC'`, S26). Una fila fuera de todo rango cae en `DEFAULT` y **se queda ahí**: el trigger de inmutabilidad impide moverla. Si `DEFAULT` ya tiene filas del mes que se intenta crear, PostgreSQL rechaza el `CREATE TABLE … PARTITION OF`; el worker lo loguea y el gauge sigue en 1. El worker **sólo crea**: nunca hace `DETACH`, y menos de un mes dentro de la ventana de `occurredAt` (S12) | Mover filas sería un `DELETE` del ledger; la red de seguridad no pierde nada y la alerta obliga a mirar. `occurred_at` es `timestamptz` y el servidor lo compara en UTC: un límite en la zona de la sesión partiría un mes en dos |
| `Down` de `AddInventoryLedger` | **Vacío, con comentario** (S11): no borra tablas, vista, función ni trigger. El `Down` que EF genera se elimina a mano. Consecuencia: tras un `Down`, volver a aplicar `Up` falla en el primer `CreateTable` que EF genera; el rollback es revertir el código y dejar el esquema. La prueba de migración termina en `*_InitialWms` y **no llama a la API** después | El ledger es prueba de negocio; `AddCustomerDataConsents` **sí** borra en su `Down` y por eso no se copia. Hacer todo el `Up` idempotente por SQL crudo perdería las definiciones que EF genera para ocho tablas |
| `occurredAt` en ajustes | `ProposeAdjustmentRequest.OccurredAt?` (null = la hora del `post`), guardado en `Adjustment.OccurredAt?` y validado contra la ventana de S12 en `Adjustment.Propose` (vía `LedgerEntryRules.EnsureOccurredAtInWindow`) **y otra vez por el ledger al postear**: un ajuste propuesto con fecha física que ya salió de la ventana responde 422 al postear y hay que proponerlo de nuevo | S12 obliga a validar `occurredAt` en el servidor y el único cliente de WMS-D que puede fecharlo es el ajuste; la segunda validación es la del ledger, que no confía en el documento |
| `reverses_occurred_at` | `LedgerEntry.ReversesOccurredAt` y `StockMovement.ReversesOccurredAt` viajan junto a `ReversesMovementId`; T9 escribe los dos desde el movimiento original | S4: sin FK hacia una tabla particionada, la fecha es lo que permite localizar la partición del original sin recorrerlas todas |
| Vista de filas | Además de `v_stock_summary` (columnas fijadas por S10), la migración crea `wms.v_stock_rows`: una fila por clave de saldo unida a `locations`, con `available`, `pickable`, `is_virtual`, `allows_picking`, `location_role`, `location_code`, `location_path`; `v_stock_summary` se define **sobre** `v_stock_rows` con `GROUP BY (tenant_id, owner_id, warehouse_id, variant_id)`. `GET /stock` lee `v_stock_rows`; `/stock/summary`, `BalanceChangePublisher` y el export leen `v_stock_summary` | La expresión de `available` sigue viviendo una sola vez en SQL (§5.3) aunque `v_stock_summary` ya no tenga granularidad de fila |
| `wms.location.counting` | Es **422** por `WmsDomainException` (regla de dominio), no 409 (S20). El spec §24.2 se corrige a 422 | El código lo lanza `LedgerEntryRules`, una regla pura: `DomainException` → 422 por `ApiExceptionHandler` |
| Preview de carga inicial con issues | Responde **200** con `rows[].issues` y `canCommit = false` (S21); el commit sobre ese preview → 422 `wms.import.invalid_file` (el mismo código del archivo inservible; §24.2 ya lo asigna a este caso y no existe `preview_not_committable`); un archivo sin columnas, vacío, > 5 MB o ilegible → 422 `wms.import.invalid_file`; **más de 500 filas → 422 `wms.import.too_many_rows` en el preview** (constante nueva en `WmsErrorCodes`, nueva también en §24.2) | `LedgerBatch.MaxEntries = 500` y la carga es un solo batch: una hoja más grande no puede commitearse nunca, así que se rechaza antes de resolver códigos |
| `Owner Self` | Nunca se crea desde este plan: el handler llama `IWmsProvisioner.EnsureAsync(tenantId)` y después `IOwnerRepository.GetSelfAsync` (S5). Los `GET` de este plan no lo necesitan (filtran por `ownerId` opcional) y por eso **no** escriben | «Un GET no escribe» tiene como única excepción los listados que WMS-B documenta; los de saldos no están entre ellos |
| Métricas | Instrumentos `qep.wms.movements` (counter, tag `reason`), `qep.wms.confirm.duration` (histograma, ms), `qep.wms.balance.drift` y `qep.wms.partition.missing` (gauges observables) en el meter `Qep.Wms` | Es la convención del repo (`qep.messaging.inbound.lag`); Prometheus los expone como `qep_wms_movements_total`, `qep_wms_balance_drift`, etc., que son los nombres del spec |
| `CHECK` de `reserved` | `CHECK (reserved >= 0 AND (allows_negative OR reserved <= on_hand))`, nombrado `CK_stock_balances_reserved`; el de `on_hand` es `CHECK (on_hand >= 0 OR allows_negative)`, `CK_stock_balances_on_hand_non_negative` (S1) | `SUPPLIER` con `on_hand = -60` y `reserved = 0` rompería `reserved <= on_hand` tal cual; y `TRANSIT`, aunque virtual, no admite negativo: por eso la columna es `allows_negative` y no `location_is_virtual` |
| Estado de la virtual `ADJUSTMENT` | El movimiento de ajuste usa el **mismo `status`** de la línea en los dos extremos | Σ por clave de estado también cierra en cero; no hay que elegir un estado «de la virtual» |
| Ambigüedad de código en la carga inicial | Coincidencia por `Code` **ordinal exacto**. Si no hay exacta y hay dos o más variantes que casan sin distinguir mayúsculas, `wms.import.ambiguous_code`; cero o una (inexacta) → `wms.import.unknown_code`. Nunca se asigna por aproximación | El índice `IX_product_variants_tenant_code` hace imposible la ambigüedad exacta; la inexacta sí existe |
| Misma clave en dos filas del Excel | Issue `wms.import.duplicate_row` en la segunda fila, `canCommit = false` | Dos filas iguales son casi siempre un error de la hoja; sumarlas en silencio escondería el doble conteo |
| Preview de carga inicial | Tabla `wms.import_previews (id, tenant_id, requested_by, warehouse_id, owner_id, rows_json, can_commit, created_at, expires_at)`; vence a los 30 min; la purga el worker de limpieza | Varias réplicas no comparten memoria |
| `ownerId` en requests | Opcional; `null` = `Owner Self` (`GetSelfAsync`). Si viene, `FindAsync` o `WmsNotFound.Owner` | D-10: el campo existe, la UI lo oculta |
| Reverso y post concurrentes | `pg_try_advisory_xact_lock(WmsLockNamespace, hashtext(id))` en el reverso (id del movimiento), en el `post` (id del ajuste) y en el commit de importación (id del preview); ocupado → 409 `wms.document.busy`. `TryLockDocumentAsync` es **sólo para documentos** (S26) | §11.3 |
| Numeración de ajustes | `AdjustmentRepository.NextNumberAsync` toma un lock **bloqueante** propio, `pg_advisory_xact_lock(WmsLockNamespace, hashtext('adj-seq:' + tenantId))`, dentro de la transacción que `Propose` abre con `await using var tx = await uow.BeginTransactionAsync(ct)` (S26); exige transacción abierta | Un try-lock aquí haría que dos `Propose` simultáneos —incluidos los dos de `TwoSimultaneousRequestsWithTheSameKeyLeaveOneAdjustment`— devolvieran 409 en vez de esperar un instante y numerar en serie |
| Transacciones explícitas | Los tres flujos que escriben al ledger (`Propose`/`Post`, reverso, commit de importación) abren la transacción con `await using var tx = await uow.BeginTransactionAsync(ct)` como primera sentencia tras autorizar (S26) | Si `SaveChangesAsync` lanza `OperationReplayedException`, el `await using` deshace la transacción al salir del handler y el filtro relee el recibo con una transacción ya cerrada; un `BeginTransactionAsync` sin `await using` dejaría la conexión abortada para esa relectura |

## Review Focus

1. **Deadlock por orden de claves con lotes mixtos.** Un batch con la misma variante en dos lotes y dos ubicaciones, posteado a la vez que otro que toca las mismas claves en orden inverso, nunca termina en `40P01`: las dos transacciones escriben en el orden de `StockKeyComparer`, con `lot_id` nulo antes que cualquier lote. Task 1 `StockKeyComparerTests.NullLotSortsBeforeAnyLotAndTheOrderIsTotal`; Task 4 `InventoryLedgerPostingTests.TwoBatchesOverTheSameMixedLotKeysInOppositeOrderNeverDeadlock`.
2. **Partición faltante al cambiar de mes.** Un movimiento con `occurred_at` del mes siguiente, antes de que exista su partición, se inserta igual: cae en `stock_movements_default`, el saldo se actualiza y la historia lo lista. Task 3 `LedgerPartitionTests.AMovementOutsideEveryMonthlyPartitionLandsInTheDefaultPartition`; Task 14 `PartitionMaintenanceTests.TheGaugeReportsOneWhileNextMonthHasNoPartition`.
3. **Cuerpos JSON equivalentes con otro orden de propiedades.** `{ "version": 1, "note": "x" }` y `{ "note": "x", "version": 1 }` con la misma clave son la misma operación: la segunda devuelve la respuesta guardada, no 409. Task 8 `IdempotencyTests.AnEquivalentBodyWithAnotherPropertyOrderReplaysInsteadOfConflicting`.
4. **Saldo de virtuales filtrándose a `totals` y a `summary`.** Después de un ajuste positivo de 10, `ADJUSTMENT` tiene `-10`; `totals.onHand` es 10, `summary.onHand` es 10 y la lista no trae la fila virtual salvo que se pida `includeVirtual=true`. Task 7 `StockQueryTests.VirtualLocationsNeverReachTotalsNorSummary`.
5. **El mismo código en dos filas del Excel.** Dos filas con el mismo `code`, `location` y `lot` no se suman: la segunda sale con `wms.import.duplicate_row` y `canCommit = false`; dos filas con el mismo código y ubicaciones distintas sí entran, cada una con su movimiento. Task 10 `InitialBalanceImportTests.ARepeatedKeyIsAnIssueAndADifferentLocationIsNot`.
6. **Una clave de idempotencia ajena no se sondea.** Otro tenant que repite una `Idempotency-Key` existente contra la ruta del primero recibe 403 `authorization.denied` antes de que el filtro busque el recibo: nunca ve ni la respuesta guardada ni un 409 que confirme que la clave existe. Task 8 `IdempotencyTests.AnotherTenantReplayingAKeyIs403`.
7. **El guard del upsert respeta lo reservado.** Con `on_hand = 10` y `reserved = 4`, sacar 8 de una ubicación sin `allows_negative` falla con `wms.stock.reserved` y deja el saldo en 10; sacar 12 falla con `wms.stock.insufficient` y `available = 6`. Task 4 `InventoryLedgerPostingTests.MovingIntoTheReservedPortionFailsWithReservedAndLeavesTheBalance`.

---

## Mapa de archivos

**Domain:** `Modules.Wms.Domain/Inventory/`: `StockStatus.cs`, `MovementReason.cs`, `LedgerActorType.cs`, `StockKey.cs` (+ `StockKeyComparer`), `LedgerEntry.cs` (+ `LedgerActor`, `LedgerCapture`, `LedgerDocument`), `LedgerBatch.cs`, `LedgerVariantRules.cs`, `LedgerLocationInfo.cs`, `LedgerEntryRules.cs`, `LedgerDeltas.cs`, `StockMovement.cs`, `StockBalance.cs`, `LotId.cs`, `Lot.cs`, `SerialUnit.cs`, `Reservation.cs`, `ReservationStatus.cs`, `StockInsufficientException.cs`, `OperationReceipt.cs`. `Modules.Wms.Domain/Control/`: `Adjustment.cs`, `AdjustmentLine.cs`, `AdjustmentReason.cs`, `AdjustmentStatus.cs`, `InvalidTransitionException.cs`, `ImportPreview.cs`, `BalanceCheck.cs`. Se amplía `WmsErrorCodes.cs`.

**Application:** `Modules.Wms.Application/Inventory/`: `IInventoryLedger.cs`, `ILotRepository.cs`, `IMovementReader.cs`, `IStockReadQueries.cs`, `IOperationReceipts.cs`, `IdempotencyScope.cs`, `IIdempotentRequest.cs`, `RequestHasher.cs`, `OperationReplayedException.cs`, `IBalanceChangeQueue.cs`, `IBalanceReconciliation.cs`, `IPartitionMaintenance.cs`, `IStockExportBuilder.cs`, `IWmsExportStorage.cs`, `IWmsEventPublisher.cs`, `WmsWorkerOptions.cs`, `InventoryDtos.cs`, `GetStock.cs`, `GetStockSummary.cs`, `ListStockMovements.cs`, `ExportStock.cs`, `ReverseMovement.cs`. `Modules.Wms.Application/Control/`: `IAdjustmentRepository.cs`, `IImportPreviewStore.cs`, `IInitialBalanceSheetReader.cs`, `InitialBalanceRowRules.cs`, `AdjustmentDtos.cs`, `AdjustmentPosting.cs`, `ProposeAdjustment.cs`, `ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs`, `ListAdjustments.cs`, `PreviewInitialBalances.cs`, `CommitInitialBalances.cs`. Se amplían `WmsPorts.cs` (`IWmsUnitOfWork`, `IWmsCatalogReader`, `IWarehouseRepository`, `WmsAuditActions` + `Resources`) y `WmsSupport.cs` (`WmsNotFound`).

**Infrastructure:** `Persistence/InventoryLedger.cs`, `InventoryLedgerSql.cs`, `StockReadQueries.cs`, `LotRepository.cs`, `AdjustmentRepository.cs`, `OperationReceipts.cs`, `ImportPreviewStore.cs`, `BalanceChangeQueue.cs`, `BalanceReconciliation.cs`, `PartitionMaintenance.cs`, `WmsEventPublisher.cs`, `StockProbes.cs`, `CheckViolation.cs`, `Migrations/<ts>_AddInventoryLedger.cs`; `Excel/ClosedXmlInitialBalanceSheetReader.cs`, `Excel/ClosedXmlStockExportBuilder.cs`; `Workers/BalanceChangePublisher.cs`, `BalanceReconciliationWorker.cs`, `PartitionMaintenanceWorker.cs`, `WmsHousekeepingWorker.cs`; `WmsMetrics.cs`. Se amplían `WmsDbContext.cs`, `WmsUnitOfWork.cs`, `WarehouseRepository.cs`, `WmsInfrastructureExtensions.cs`, `NoStockProbes.cs` (se retira).

**Api:** `IdempotencyKeyFilter.cs`, `StockEndpoints.cs`, `AdjustmentEndpoints.cs`, `ImportEndpoints.cs`; se amplía `WmsEndpoints.cs`.

**Bootstrapper:** `WmsExportStorage.cs`; se amplían `WmsCatalogReader.cs`, `CatalogVariantUsageChecker.cs`, `QepServiceCollectionExtensions.cs`. **Storage:** `FileOwnerType.WmsExport` en `FileResourceEnums.cs` y su fila `wms` en `FileOwnerModules.ByOwnerType` (si WMS-B no la trajo junto con `WmsEvidence`). **Notifications:** `Application/WmsExportEmailTemplate.cs`, `Infrastructure/Messaging/WmsExportDeliveryWorker.cs`.

**Pruebas:** `Modules.Wms.UnitTests/Inventory/*` (`StockKeyComparerTests`, `LedgerBatchTests`, `LedgerEntryRulesTests`, `LedgerDeltasTests`, `RequestHasherTests`, `OperationReceiptTests`), `Modules.Wms.UnitTests/Control/*` (`AdjustmentTests`, `InitialBalanceRowRulesTests`), `Modules.Wms.IntegrationTests/*` (`WmsApiHarness.Inventory.cs`, `WmsLedgerSeeding.cs`, `InventoryLedgerMigrationTests`, `LedgerImmutabilityTests`, `LedgerPartitionTests`, `InventoryLedgerPostingTests`, `WmsUnitOfWorkTests`, `StockQueryTests`, `AdjustmentApiTests`, `IdempotencyTests`, `ConcurrentAdjustmentTests`, `ReverseMovementApiTests`, `InitialBalanceImportTests`, `StockExportApiTests`, `BalanceChangePublisherTests`, `BalanceReconciliationTests`, `PartitionMaintenanceTests`, `StockProbeTests`), `Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs` y `WmsCatalogReaderTests.cs` (de WMS-B; cambia su constructor), `Notifications.UnitTests/WmsExportEmailTemplateTests.cs`, `Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs`, `ArchitectureTests/WmsLayerTests.cs`, `ArchitectureTests/CompositionRootTests.cs`.

---

### Task 1: Dominio `Inventory` — enums, `StockKey` y su orden total

**Cubre:** RF-020, RNF-03 (orden de claves), RNF-13 (`owner_id` en la clave).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Inventory/StockStatus.cs`, `MovementReason.cs`, `LedgerActorType.cs`, `StockKey.cs`, `StockMovement.cs`, `StockBalance.cs`, `LotId.cs`, `Lot.cs`, `SerialUnit.cs`, `Reservation.cs`, `ReservationStatus.cs`, `StockInsufficientException.cs`, `OperationReceipt.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs`, `src/Modules/Wms/Modules.Wms.Domain/Modules.Wms.Domain.csproj` (`InternalsVisibleTo` hacia `Modules.Wms.Infrastructure`, `Modules.Wms.UnitTests`, `Modules.Wms.IntegrationTests`, si no lo tiene)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/StockKeyComparerTests.cs`

**Interfaces:**
- Produces:
  - `enum StockStatus { Available, Received, Quarantine, Blocked, Damaged, InTransit }`
  - `enum MovementReason { Receipt, Putaway, InternalMove, TransferDispatch, TransferReceipt, Pick, Ship, Adjustment, InitialLoad, Reversal, StatusChange, ReturnToSupplier, WriteOff }`
  - `enum LedgerActorType { Human, System, ApiKey }` — espejo de `WmsActorType` (Application); la prueba `LedgerActorTypeMirrorsWmsActorType` compara los nombres.
  - `public readonly record struct StockKey(Guid OwnerId, Guid LocationId, Guid VariantId, Guid? LotId, StockStatus Status)` — `Guid` blandos a propósito (`Inventory` no ve `Warehousing`). `public sealed class StockKeyComparer : IComparer<StockKey>` con `Instance`, orden por `LocationId`, `VariantId`, `LotId` (`null` primero, luego por `Guid`), `Status` (valor del enum), y por último `OwnerId` para que el orden sea total.
  - `public sealed class StockMovement` sin setters públicos; factoría `internal static StockMovement Record(Guid id, Guid tenantId, StockKey from, StockKey to, decimal quantity, string? capturedUom, decimal? capturedQty, decimal? conversionFactor, MovementReason reason, string? documentType, Guid? documentId, Guid? documentLineId, Guid batchId, string idempotencyKey, Guid actorId, LedgerActorType actorType, string? deviceId, DateTimeOffset occurredAt, DateTimeOffset recordedAt, Guid? reversesMovementId, DateTimeOffset? reversesOccurredAt, string? overrideReason, Guid? serialUnitId)`. Propiedades: las columnas de §5.2 (sin bodega: se deduce de la ubicación, y la tabla no la guarda) más `FromLocationId`, `FromStatus`, `ToLocationId`, `ToStatus`, `OwnerId`, `OverrideReason`, `ReversesOccurredAt` (S4: referencia blanda al original, sin FK; `reversesMovementId` y `reversesOccurredAt` van juntos o ninguno, `ArgumentException` si no).
  - `public sealed class StockBalance` sólo lectura (`Id, TenantId, OwnerId, WarehouseId, LocationId, VariantId, LotId, Status, OnHand, Reserved, Version, UpdatedAt, AllowsNegative`), constructor privado: nadie la instancia fuera de EF. `AllowsNegative` es la copia de `Location.AllowsNegative` que el ledger escribe en el upsert (S1); no hay `LocationIsVirtual`.
  - `public readonly record struct LotId(Guid Value)` con `New()`; `public sealed class Lot`: `Id: LotId`, `TenantId`, `OwnerId: Guid`, `VariantId`, `Code` (≤ 64), `ExpirationDate: DateOnly?`, `ManufacturedAt: DateOnly?`, `CreatedAt`; `static Lot Create(Guid tenantId, Guid ownerId, Guid variantId, string code, DateOnly? expirationDate, DateOnly? manufacturedAt, bool expiryRequired, DateTimeOffset now)` → `WmsDomainException(WmsErrorCodes.LotExpiryRequired)` si `expiryRequired && expirationDate is null`; `LotCodeInvalid` si el código (tras `Trim`) está vacío o pasa de 64.
  - `public sealed class SerialUnit` (`Id`, `TenantId`, `OwnerId: Guid`, `VariantId`, `Serial`, `CurrentLocationId: Guid`, `Status: StockStatus`, `LotId: LotId?`) con `internal void MoveTo(Guid locationId, StockStatus status)`.
  - `enum ReservationStatus { Open, Consumed, Released, Expired }`; `public sealed class Reservation` (`Id`, `TenantId`, `OwnerId`, `VariantId`, `WarehouseId`, `LocationId` (todos `Guid`), `LotId: LotId?`, `Quantity`, `DemandType`, `DemandId`, `DemandLineId?`, `Status`, `ExpiresAt?`, `CreatedAt`) **sin factorías públicas** (fase 3).
  - `public sealed class OperationReceipt` (`TenantId`, `IdempotencyKey` ≤ 128, `RequestHash` (64 hex), `ResponseJson`, `CreatedAt`); `static Create(Guid tenantId, string key, string hash, string responseJson, DateTimeOffset now)`; clave vacía o > 128 → `ArgumentException`.
  - `public sealed class StockInsufficientException(string locationCode, Guid variantId, string? lotCode, decimal available) : DomainException(WmsErrorCodes.StockInsufficient, "No hay suficiente saldo en la ubicación de origen."), IHasProblemExtensions` con `ProblemExtensions = { ["locationCode"], ["variantId"], ["lotCode"], ["available"] }`.
  - Constantes nuevas en `WmsErrorCodes` (las que WMS-B ya tenga no se duplican): `StockInsufficient = "wms.stock.insufficient"`, `StockReserved = "wms.stock.reserved"`, `StockStatusInvalid = "wms.stock.status_invalid"`, `IdempotencyKeyRequired = "wms.idempotency_key_required"`, `IdempotencyKeyReused = "wms.idempotency_key_reused"`, `LedgerImmutable = "wms.ledger.immutable"`, `LotRequired = "wms.lot.required"`, `LotNotAllowed = "wms.lot.not_allowed"`, `LotExpiryRequired = "wms.lot.expiry_required"`, `LotCodeInvalid = "wms.lot.code_invalid"`, `LotNotFound = "wms.lot.not_found"`, `SerialRequired = "wms.serial.required"`, `UomPrecisionExceeded = "wms.uom.precision_exceeded"`, `UomFactorMismatch = "wms.uom.factor_mismatch"`, `LocationBlocked = "wms.location.blocked"`, `LocationCounting = "wms.location.counting"` (422 por `WmsDomainException`, S20), `MovementAlreadyReversed = "wms.movement.already_reversed"`, `MovementNotFound = "wms.movement.not_found"`, `MovementQuantityInvalid = "wms.movement.quantity_invalid"`, `MovementSameKey = "wms.movement.same_key"`, `MovementOccurredAtOutOfRange = "wms.movement.occurred_at_out_of_range"` (S12, nuevo en §24.2), `BatchTooLarge = "wms.batch.too_large"`, `DocumentInvalidTransition = "wms.document.invalid_transition"`, `DocumentBusy = "wms.document.busy"`, `AdjustmentNotFound = "wms.adjustment.not_found"`, `AdjustmentLineInvalid = "wms.adjustment.line_invalid"`, `ImportAmbiguousCode = "wms.import.ambiguous_code"`, `ImportUnknownCode = "wms.import.unknown_code"`, `ImportUnknownLocation = "wms.import.unknown_location"`, `ImportDuplicateRow = "wms.import.duplicate_row"`, `ImportInvalidFile = "wms.import.invalid_file"`, `ImportTooManyRows = "wms.import.too_many_rows"` (S21, nuevo en §24.2), `ImportPreviewNotFound = "wms.import.preview_not_found"`, `ImportPreviewExpired = "wms.import.preview_expired"`, `ExportEmpty = "wms.export.empty"`, `ExportTooManyRows = "wms.export.too_many_rows"`. `OwnerHasStock` y `LocationVirtualImmutable` ya vienen de WMS-B.

- [ ] **Step 1: Crear el worktree y la rama, y confirmar los tres supuestos abiertos**

```powershell
git -C C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend worktree add ..\qep-backend-worktrees\wms-d -b feature/wms-d develop
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-d
git branch --show-current
Select-String -Path tests\Modules\Wms\Modules.Wms.IntegrationTests\WmsApiHarness.cs -Pattern "static partial class|SeedCallerMembershipAsync"
Select-String -Path src\Modules\Wms\Modules.Wms.Domain\Modules.Wms.Domain.csproj -Pattern "InternalsVisibleTo"
```
Expected: `feature/wms-d`; anotar qué apareció de cada `Select-String` (decide los pasos de T1/T3).

- [ ] **Step 2: Escribir la prueba del comparador**

```csharp
public sealed class StockKeyComparerTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid LocationA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid LocationB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Variant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid LotX = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid LotY = Guid.Parse("00000000-0000-0000-0000-000000000020");

    [Fact]
    public void NullLotSortsBeforeAnyLotAndTheOrderIsTotal()
    {
        StockKey[] keys =
        [
            new(Owner, LocationB, Variant, LotY, StockStatus.Available),
            new(Owner, LocationA, Variant, LotX, StockStatus.Received),
            new(Owner, LocationA, Variant, null, StockStatus.Available),
            new(Owner, LocationA, Variant, LotX, StockStatus.Available),
        ];
        var forward = keys.Order(StockKeyComparer.Instance).ToArray();
        var backward = keys.Reverse().Order(StockKeyComparer.Instance).ToArray();

        Assert.Equal(forward, backward);
        Assert.Equal(
            [(LocationA, (Guid?)null, StockStatus.Available), (LocationA, LotX, StockStatus.Available), (LocationA, LotX, StockStatus.Received), (LocationB, LotY, StockStatus.Available)],
            forward.Select(key => (key.LocationId, key.LotId, key.Status)).ToArray());
    }

    [Fact]
    public void EqualKeysCompareAsZero() =>
        Assert.Equal(0, StockKeyComparer.Instance.Compare(
            new(Owner, LocationA, Variant, LotX, StockStatus.Blocked), new(Owner, LocationA, Variant, LotX, StockStatus.Blocked)));

    [Fact]
    public void TheInsufficientStockExceptionCarriesTheKeyAndTheAvailableQuantity()
    {
        var exception = new StockInsufficientException("A-01-02", Variant, "L-2609", 2m);
        Assert.Equal("wms.stock.insufficient", exception.Code);
        Assert.Equal(
            new Dictionary<string, object?> { ["locationCode"] = "A-01-02", ["variantId"] = Variant, ["lotCode"] = "L-2609", ["available"] = 2m },
            exception.ProblemExtensions);
    }

    [Fact]
    public void LedgerActorTypeMirrorsWmsActorType() =>
        Assert.Equal(Enum.GetNames<WmsActorType>(), Enum.GetNames<LedgerActorType>());
}
```

- [ ] **Step 3: Correr y ver que falla**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~StockKeyComparerTests"
```
Expected: error de compilación (`StockKey` no existe).

- [ ] **Step 4: Implementar los tipos de la sección Interfaces**

`StockKeyComparer.Compare` compara en el orden fijado; para `LotId` usa `Nullable.Compare` (null es menor). `StockMovement.Record` valida sólo lo estructural (`quantity > 0`, `from != to`): las reglas con perfil van en Task 2.

- [ ] **Step 5: Correr y ver que pasa** — mismo comando más `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"` (`DomainAreasOnlyDependDownwards` para `Inventory` tiene que seguir verde). Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Wms/Modules.Wms.Domain tests/Modules/Wms/Modules.Wms.UnitTests
git commit -m "feat(wms): add inventory ledger domain types and stock key ordering"
```

---

### Task 2: `LedgerEntry`, `LedgerBatch`, reglas por perfil y neteo de deltas

**Cubre:** RF-020, RF-025 (compensación), RNF-01 (doble partida por construcción), RNF-07 (tope de 500).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Inventory/LedgerEntry.cs`, `LedgerBatch.cs`, `LedgerVariantRules.cs`, `LedgerLocationInfo.cs`, `LedgerEntryRules.cs`, `LedgerDeltas.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/LedgerBatchTests.cs`, `LedgerEntryRulesTests.cs`, `LedgerDeltasTests.cs`

**Interfaces:**
- Consumes: Task 1; `TrackingPolicy` (ya en `Inventory`).
- Produces:
  - `public sealed record LedgerActor(Guid Id, LedgerActorType Type, string? DeviceId)`
  - `public sealed record LedgerCapture(string Uom, decimal Quantity, decimal Factor)`
  - `public sealed record LedgerDocument(string Type, Guid Id, Guid? LineId)`
  - `public sealed record LedgerEntry(StockKey From, StockKey To, decimal Quantity, LedgerCapture? Captured, MovementReason Reason, LedgerDocument? Document, DateTimeOffset OccurredAt, Guid? ReversesMovementId = null, DateTimeOffset? ReversesOccurredAt = null, string? OverrideReason = null, Guid? SerialUnitId = null)` — `ReversesMovementId` y `ReversesOccurredAt` van juntos o ninguno (`ArgumentException`); el constructor lanza `WmsDomainException(MovementQuantityInvalid)` si `Quantity <= 0`, `MovementSameKey` si `From == To`, y exige `From.VariantId == To.VariantId && From.OwnerId == To.OwnerId && From.LotId == To.LotId` (`MovementSameKey` con otro mensaje).
  - `public sealed class LedgerBatch` con `MaxEntries = 500`; `LedgerBatch(Guid tenantId, Guid batchId, string idempotencyKey, LedgerActor actor, IReadOnlyList<LedgerEntry> entries)` lanza `BatchTooLarge` por encima de 500 y `ArgumentException` con cero entradas.
  - `public sealed record LedgerVariantRules(string BaseUom, int BaseUomPrecision, TrackingPolicy Tracking, IReadOnlyDictionary<string, decimal> ConversionFactors)` — lo que el ledger necesita del perfil, sin conocer `VariantLogisticsProfile` (vive en `Warehousing`); el `InventoryLedger` lo construye (`BaseUom.Value`, `Conversions` → `Code → Factor`). **WMS-B no normaliza el código de una conversión**: lo guarda tal como llega (la presentación sembrada desde Catalog es `EMPAQUE x12`, con `x` minúscula), así que las claves **no** están en mayúsculas. El diccionario se arma con `StringComparer.OrdinalIgnoreCase` y la captura se compara sin distinguir mayúsculas; si no, una captura en mayúsculas (`EMPAQUE X12`, p. ej. una fila de la carga inicial) falla con `wms.uom.factor_mismatch` en cada presentación sembrada.
  - `public sealed record LedgerLocationInfo(Guid Id, Guid WarehouseId, string Code, bool AllowsNegative, bool IsBlocked, bool IsCounting)` — ídem para `Location` (`AllowsNegative` = `Location.AllowsNegative`, S1).
  - `public static class LedgerEntryRules` con `const int MaxPastDays = 30`, `const int MaxFutureMinutes = 5`, `EnsureOccurredAtInWindow(DateTimeOffset occurredAt, DateTimeOffset now)` (→ `WmsDomainException(MovementOccurredAtOutOfRange, "La fecha del movimiento tiene que estar entre hace 30 días y dentro de 5 minutos.")`, S12) y `Validate(LedgerEntry entry, LedgerVariantRules variant, LedgerLocationInfo from, LedgerLocationInfo to, DateTimeOffset now)`:
    - ventana: `EnsureOccurredAtInWindow(entry.OccurredAt, now)`;
    - precisión: `decimal.Round(quantity, variant.BaseUomPrecision) != quantity` → `UomPrecisionExceeded`;
    - `Tracking == Lot` y `entry.From.LotId is null` → `LotRequired`; `Tracking == None` y lote presente → `LotNotAllowed`;
    - `Tracking == Serial` y (`SerialUnitId is null` o `Quantity != 1`) → `SerialRequired`;
    - captura: factor esperado = `1` si `Uom == BaseUom`, si no `ConversionFactors[Uom]` (ausente → mismatch); `Captured.Factor != esperado` o `Captured.Quantity * Captured.Factor != Quantity` → `UomFactorMismatch`;
    - `from.IsBlocked || to.IsBlocked` → `LocationBlocked`; `IsCounting` → `LocationCounting` (422: es `WmsDomainException`, S20), **salvo** `Reason ∈ { Adjustment, Reversal }`;
    - `from.Id != entry.From.LocationId` o `to.Id != entry.To.LocationId` → `ArgumentException`.
  - `public static class LedgerDeltas` con `IReadOnlyList<(StockKey Key, decimal Delta)> Net(IReadOnlyList<LedgerEntry> entries)`: suma `-Quantity` al `From` y `+Quantity` al `To`, agrupa por clave, **omite las claves con delta 0** y devuelve ordenado por `StockKeyComparer`.
  - `public static LedgerEntry LedgerEntry.Reverse(LedgerEntry original, Guid originalMovementId, DateTimeOffset originalOccurredAt, DateTimeOffset occurredAt, string? overrideReason)`: `From`/`To` intercambiados, `Reason = Reversal`, `ReversesMovementId = originalMovementId`, `ReversesOccurredAt = originalOccurredAt` (S4), misma captura y documento.

- [ ] **Step 1: Escribir las pruebas**

`LedgerBatchTests`: `AnEntryWithZeroQuantityIsRejected` (`wms.movement.quantity_invalid`), `AnEntryFromAKeyToItselfIsRejected` (`wms.movement.same_key`), `ABatchAboveFiveHundredEntriesIsRejected` (`wms.batch.too_large`), `ReversingAnEntrySwapsTheKeysAndPointsToTheOriginalWithItsDate` (`ReversesMovementId` y `ReversesOccurredAt` son los del original), `AReversalReferenceWithoutItsDateIsRejected` (`ArgumentException`).

`LedgerEntryRulesTests` (arma `LedgerVariantRules` y `LedgerLocationInfo` a mano; `now` fijo): `ADecimalQuantityOnAZeroPrecisionProfileIsRejected` (`wms.uom.precision_exceeded`, 1.5 con precisión 0), `AThreeDecimalQuantityOnASixPrecisionProfileIsAccepted`, `ALotTrackedVariantWithoutLotIsRejected`, `AnUntrackedVariantWithLotIsRejected`, `ASerialMovementMustMoveExactlyOne`, `ACaptureWhoseFactorDiffersFromTheProfileIsRejected` (`CAJA = 12`, captura `3 CAJA × 10 = 30` → `wms.uom.factor_mismatch`), `ACaptureInAnUnknownUomIsRejected`, `ACaptureInADifferentCaseMatchesTheStoredConversion` (conversión guardada `EMPAQUE x12 = 12`, captura `1 EMPAQUE X12 × 12` → sin error), `ABlockedLocationRejectsEveryReason`, `ACountingLocationAcceptsOnlyAdjustmentAndReversal` (Theory sobre los 13 motivos; el rechazo es `WmsDomainException` con `wms.location.counting`), `AnOccurredAtOlderThanThirtyDaysIsRejected` (`now − 30 d − 1 s` → `wms.movement.occurred_at_out_of_range`), `AnOccurredAtWithinFiveMinutesAheadIsAcceptedAndBeyondIsNot` (`now + 5 min` pasa; `now + 5 min + 1 s` no).

`LedgerDeltasTests`: `TheSumOfAllDeltasIsZero` (tres entradas encadenadas), `AKeyThatIsBothSourceAndTargetIsWrittenOnce` (REC→A 10 y A→B 4 → A tiene delta +6 y aparece una vez), `AKeyWithNetZeroIsOmitted`, `DeltasComeOutInComparerOrder`.

- [ ] **Step 2: Correr y ver que falla**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~LedgerBatchTests|FullyQualifiedName~LedgerEntryRulesTests|FullyQualifiedName~LedgerDeltasTests"
```
Expected: no compila.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: todas PASS.
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wms/Modules.Wms.Domain tests/Modules/Wms/Modules.Wms.UnitTests
git commit -m "feat(wms): add ledger batch rules and per-key delta netting"
```

---

### Task 3: Migración `AddInventoryLedger`, trigger de inmutabilidad, particiones, vista y harness de inventario

**Cubre:** RF-020, RNF-01, RNF-08, RNF-10, ADR-02/03/04/05/10.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Control/AdjustmentReason.cs`, `AdjustmentStatus.cs`, `AdjustmentLine.cs`, `Adjustment.cs` (sólo la forma persistida; el comportamiento llega en Task 8), `ImportPreview.cs`, `BalanceCheck.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsDbContext.cs` (configuraciones y constantes nuevas)
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<ts>_AddInventoryLedger.cs` (generada y luego editada), `Persistence/InventoryLedgerSql.cs`
- Create: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.Inventory.cs`
- Modify: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.cs` (sólo `partial`, si falta) y su `.csproj` (`<PackageReference Include="Npgsql" />` si no llega transitivo)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/InventoryLedgerMigrationTests.cs`, `LedgerImmutabilityTests.cs`, `LedgerPartitionTests.cs`

**Interfaces:**
- Produces (constantes nuevas en `WmsDbContext`, estilo de las de WMS-B): `StockBalanceKeyIndex = "UQ_stock_balances_key"`, `StockBalanceNonNegativeCheck = "CK_stock_balances_on_hand_non_negative"`, `StockBalanceReservedCheck = "CK_stock_balances_reserved"`, `OperationReceiptPrimaryKey = "PK_operation_receipts"`, `LotCodeIndex = "IX_lots_owner_variant_code"`, `SerialUnitIndex = "IX_serial_units_variant_serial"`, `ReservationOpenIndex = "IX_reservations_open_key"`, `AdjustmentNumberIndex = "IX_adjustments_tenant_number"`.
- Produces (`internal static class InventoryLedgerSql`): `CreateStockMovements`, `CreateImmutabilityTrigger`, `CreateEnsurePartitionFunction`, `CreateBalanceChangeQueue`, `CreateStockRowsView`, `CreateStockSummaryView`, `EnsurePartition(DateOnly month)` → `"SELECT wms.ensure_movement_partition('{month:yyyy-MM-01}')"`.
- Entidades persistidas nuevas (`Control`): `enum AdjustmentReason { InitialLoad, CountVariance, Damage, Loss, Found, Correction, Other }`; `enum AdjustmentStatus { Proposed, Approved, Posted, Rejected }`; `Adjustment` (`Id: Guid`, `TenantId`, `OwnerId: OwnerId`, `WarehouseId: WarehouseId`, `Number`, `Reason`, `Status`, `Note`, `OccurredAt: DateTimeOffset?` (fecha física del ajuste, S12; `null` = la hora del `post`), `ProposedBy`, `ProposedAt`, `ApprovedBy?`, `ApprovedAt?`, `PostedAt?`, `RejectedAt?`, `RejectedBy?`, `RejectNote?`, `BatchId?`, `Version: long`, `Lines: IReadOnlyList<AdjustmentLine>`); `AdjustmentLine(Guid Id, LocationId LocationId, Guid VariantId, LotId? LotId, StockStatus Status, decimal QuantityDelta, string? CapturedUom, decimal? CapturedQty, int Position)`; `ImportPreview(Guid Id, Guid TenantId, Guid RequestedBy, WarehouseId WarehouseId, OwnerId OwnerId, string RowsJson, bool CanCommit, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)`; `BalanceCheck(Guid Id, Guid TenantId, Guid WarehouseId, Guid OwnerId, Guid LocationId, Guid VariantId, Guid? LotId, StockStatus Status, decimal LedgerOnHand, decimal BalanceOnHand, Guid? LastMovementId, DateTimeOffset CheckedAt)`.
- Mapeo EF: `StockMovement` → `ToTable("stock_movements", Schema, t => t.ExcludeFromMigrations())`, `HasKey(Id, OccurredAt)`, enums como `string`, decimales `HasPrecision(18, 6)`; **sin `DbSet`** (Task 16 lo vigila). `StockBalance` → `stock_balances` migrable con `HasIndex(…).IsUnique().AreNullsDistinct(false).HasDatabaseName(StockBalanceKeyIndex)`, columna `allows_negative boolean NOT NULL`, los dos `CHECK` por `HasCheckConstraint(StockBalanceNonNegativeCheck, "on_hand >= 0 OR allows_negative")` y `HasCheckConstraint(StockBalanceReservedCheck, "reserved >= 0 AND (allows_negative OR reserved <= on_hand)")` (S1), `id uuid` PK con `HasDefaultValueSql("gen_random_uuid()")`; también sin `DbSet`. `Lot` → `lots` (`UNIQUE (tenant_id, owner_id, variant_id, code)` = `LotCodeIndex`); `SerialUnit` → `serial_units` (`UNIQUE (tenant_id, variant_id, serial)`, índice `(current_location_id)`); `Reservation` → `reservations` (`ReservationOpenIndex` con `HasFilter("status = 'Open'")` y `(demand_type, demand_id)`); `OperationReceipt` → `operation_receipts` (PK compuesta nombrada `PK_operation_receipts`, `response_json jsonb`, `request_hash char(64)`); `BalanceCheck` → `balance_checks`; `ImportPreview` → `import_previews` (`rows_json jsonb`); `Adjustment` → `adjustments` (`AdjustmentNumberIndex` único `(tenant_id, number)`, `Version` `IsConcurrencyToken`, ids fuertes con `HasConversion` como en WMS-B) y `adjustment_lines` (`UNIQUE (adjustment_id, position)`, FK `ON DELETE CASCADE`, navegación con `PropertyAccessMode.Field`). `balance_change_queue` por SQL, sin entidad.
- Produces (`WmsApiHarness.Inventory.cs`, `internal static partial class WmsApiHarness`):
  - `Task<WarehouseDto> CreateWarehouseAsync(HttpClient manager, string code = "BOG", string tenantId = TenantId)` — `POST {WmsUrl(tenantId)}/warehouses { code, name: code }`.
  - `Task<IReadOnlyList<LocationDto>> ListLocationsAsync(HttpClient client, Guid warehouseId, string tenantId = TenantId)` — `GET …/warehouses/{id}/locations` → `.Items`.
  - `Task<LocationDto> CreateBinAsync(HttpClient manager, Guid warehouseId, string zoneCode, string binCode, bool allowsPicking = true, string tenantId = TenantId)` — crea la zona (`Zone`/`Storage`) si no existe y el bin (`Bin`/`Picking` si `allowsPicking`, si no `Storage`; `allowsStorage: true`).
  - `Task<T> ScalarAsync<T>(PostgreSqlContainer database, string sql, params (string Name, object? Value)[] parameters)` y `Task<int> ExecuteAsync(PostgreSqlContainer database, string sql, params (string Name, object? Value)[] parameters)` con `NpgsqlConnection(database.GetConnectionString())`.
  - `Task<Guid> InsertRawMovementAsync(PostgreSqlContainer database, DateTimeOffset occurredAt)` — inserta una fila con todos los `NOT NULL` (`tenant_id = Guid.Parse(TenantId)`, `owner_id`, `variant_id`, ubicaciones `Guid.CreateVersion7()`, `from_status/to_status = 'Available'`, `quantity = 1`, `reason = 'Adjustment'`, `batch_id`, `idempotency_key = id.ToString()`, `actor_id`, `actor_type = 'System'`, `recorded_at = now()`) y devuelve `id`.
  - `Task<Guid> SeedCallerMembershipAsync(QepApiFactory factory, string tenantId = TenantId, string subjectId = SubjectId)` — copia de `LoansApiHarness.SeedCallerMembershipAsync` **sólo si WMS-B no la trajo**.

- [ ] **Step 1: Escribir el harness de inventario y las pruebas (insertan por SQL crudo; el ledger aún no existe)**

```csharp
public sealed class LedgerImmutabilityTests
{
    [Fact]
    public async Task UpdatingAMovementFailsWithTheImmutableCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var id = await InsertRawMovementAsync(database, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "UPDATE wms.stock_movements SET quantity = 1 WHERE id = @id", ("id", id)));

        Assert.Equal("wms.ledger.immutable", exception.MessageText);
    }

    [Fact]
    public async Task DeletingAMovementFailsWithTheImmutableCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var id = await InsertRawMovementAsync(database, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "DELETE FROM wms.stock_movements WHERE id = @id", ("id", id)));

        Assert.Equal("wms.ledger.immutable", exception.MessageText);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
    }

    // El Down está vacío a propósito (S11): bajar a InitialWms deja la tabla, la fila y el trigger. La prueba termina abajo
    // y no llama a la API después (el Up no se puede volver a aplicar sobre las tablas que EF creó).
    [Fact]
    public async Task TheDownMigrationKeepsTheTriggerAndTheRows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var id = await InsertRawMovementAsync(database, DateTimeOffset.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<WmsDbContext>().GetService<IMigrator>();
        var initialWms = scope.ServiceProvider.GetRequiredService<WmsDbContext>().Database
            .GetMigrations().Single(m => m.EndsWith("_InitialWms", StringComparison.Ordinal));

        await migrator.MigrateAsync(initialWms);

        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_stock_movements_immutable'"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
        Assert.NotNull(await ScalarAsync<string?>(database, "SELECT to_regclass('wms.stock_balances')::text"));
        Assert.NotNull(await ScalarAsync<string?>(database, "SELECT to_regclass('wms.v_stock_summary')::text"));
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "DELETE FROM wms.stock_movements WHERE id = @id", ("id", id)));
        Assert.Equal("wms.ledger.immutable", exception.MessageText);
    }
}

public sealed class LedgerPartitionTests
{
    [Fact]
    public async Task TheMigrationCreatesTheDefaultTheCurrentAndTheNextMonthPartitions()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var now = DateTimeOffset.UtcNow;
        var next = now.AddMonths(1);

        var partitions = await ScalarAsync<string[]>(database,
            "SELECT array_agg(c.relname ORDER BY c.relname) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'stock_movements'");

        Assert.Contains("stock_movements_default", partitions);
        Assert.Contains($"stock_movements_y{now:yyyy}m{now:MM}", partitions);
        Assert.Contains($"stock_movements_y{next:yyyy}m{next:MM}", partitions);
    }

    [Fact]
    public async Task AMovementOutsideEveryMonthlyPartitionLandsInTheDefaultPartition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var farFuture = DateTimeOffset.UtcNow.AddMonths(6);

        var id = await InsertRawMovementAsync(database, farFuture);

        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements_default WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE id = @id", ("id", id)));
    }

    // Los límites están en UTC (S26): una fila a las 23:30 UTC del último día del mes cae en ese mes, no en el siguiente.
    [Fact]
    public async Task PartitionBoundsAreUtc()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var now = DateTimeOffset.UtcNow;
        var lastInstantOfMonth = new DateTimeOffset(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 30, 0, TimeSpan.Zero);

        var id = await InsertRawMovementAsync(database, lastInstantOfMonth);

        Assert.Equal(1L, await ScalarAsync<long>(database, $"SELECT count(*) FROM wms.stock_movements_y{now:yyyy}m{now:MM} WHERE id = @id", ("id", id)));
    }
}
```

`InventoryLedgerMigrationTests.TheLedgerMigrationCreatesTheTablesTheViewsAndTheQueue`: patrón `QuotationSettingsMigrationTests` para resolver el migrador: migrar a `*_InitialWms`, luego a `*_AddInventoryLedger`, comprobar con `to_regclass` que `wms.stock_movements`, `wms.stock_balances`, `wms.operation_receipts`, `wms.adjustments`, `wms.balance_change_queue`, `wms.v_stock_rows` y `wms.v_stock_summary` existen, que `information_schema.columns` tiene `stock_balances.allows_negative` y **no** `location_is_virtual`, y `stock_movements.reverses_occurred_at`; **terminar con `MigrateAsync(null)`** (aquí sí: la prueba sólo subió). No hay prueba de ida y vuelta: el `Down` es vacío por diseño y `TheDownMigrationKeepsTheTriggerAndTheRows` es la que lo amarra.

- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LedgerImmutabilityTests|FullyQualifiedName~LedgerPartitionTests|FullyQualifiedName~InventoryLedgerMigrationTests"
```
Expected: FAIL (`relation "wms.stock_movements" does not exist`).

- [ ] **Step 3: Configurar las entidades en `WmsDbContext` y generar la migración**

```powershell
dotnet ef migrations add AddInventoryLedger --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
```

- [ ] **Step 4: Editar `Up`: al principio, `migrationBuilder.Sql` con el SQL de `InventoryLedgerSql`**

```sql
CREATE TABLE IF NOT EXISTS wms.stock_movements (
    id uuid NOT NULL, tenant_id uuid NOT NULL, owner_id uuid NOT NULL, variant_id uuid NOT NULL,
    lot_id uuid NULL, serial_unit_id uuid NULL,
    from_location_id uuid NOT NULL, from_status text NOT NULL, to_location_id uuid NOT NULL, to_status text NOT NULL,
    quantity numeric(18,6) NOT NULL CONSTRAINT "CK_stock_movements_quantity" CHECK (quantity > 0),
    captured_uom text NULL, captured_qty numeric(18,6) NULL, conversion_factor numeric(18,6) NULL,
    reason text NOT NULL, document_type text NULL, document_id uuid NULL, document_line_id uuid NULL,
    batch_id uuid NOT NULL, idempotency_key text NOT NULL,
    actor_id uuid NOT NULL, actor_type text NOT NULL, device_id text NULL,
    occurred_at timestamptz NOT NULL, recorded_at timestamptz NOT NULL,
    reverses_movement_id uuid NULL, reverses_occurred_at timestamptz NULL, override_reason text NULL,
    CONSTRAINT "PK_stock_movements" PRIMARY KEY (id, occurred_at),
    CONSTRAINT "CK_stock_movements_reversal_ref" CHECK ((reverses_movement_id IS NULL) = (reverses_occurred_at IS NULL))
) PARTITION BY RANGE (occurred_at);
-- Sin FK hacia reverses_movement_id: PostgreSQL no admite FK a una tabla particionada salvo por la clave completa (S4).
CREATE TABLE IF NOT EXISTS wms.stock_movements_default PARTITION OF wms.stock_movements DEFAULT;
CREATE INDEX IF NOT EXISTS "IX_stock_movements_tenant_variant_occurred" ON wms.stock_movements (tenant_id, variant_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS "IX_stock_movements_tenant_document" ON wms.stock_movements (tenant_id, document_type, document_id);
CREATE INDEX IF NOT EXISTS "IX_stock_movements_from_location" ON wms.stock_movements (from_location_id, occurred_at);
CREATE INDEX IF NOT EXISTS "IX_stock_movements_to_location" ON wms.stock_movements (to_location_id, occurred_at);
CREATE INDEX IF NOT EXISTS "IX_stock_movements_batch" ON wms.stock_movements (batch_id);
CREATE INDEX IF NOT EXISTS "IX_stock_movements_reverses" ON wms.stock_movements (reverses_movement_id) WHERE reverses_movement_id IS NOT NULL;

CREATE OR REPLACE FUNCTION wms.reject_ledger_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'wms.ledger.immutable' USING HINT = 'Stock movements are append-only; post a Reversal instead.'; END $$;
DROP TRIGGER IF EXISTS trg_stock_movements_immutable ON wms.stock_movements;
CREATE TRIGGER trg_stock_movements_immutable BEFORE UPDATE OR DELETE ON wms.stock_movements
    FOR EACH ROW EXECUTE FUNCTION wms.reject_ledger_mutation();

-- Límites en UTC (S26): occurred_at es timestamptz y un límite en la zona de la sesión partiría un mes en dos.
CREATE OR REPLACE FUNCTION wms.ensure_movement_partition(month date) RETURNS void LANGUAGE plpgsql AS $$
DECLARE first_day date := date_trunc('month', month)::date;
        part_name text := format('stock_movements_y%sm%s', to_char(date_trunc('month', month), 'YYYY'), to_char(date_trunc('month', month), 'MM'));
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'wms' AND c.relname = part_name) THEN
    EXECUTE format('CREATE TABLE wms.%I PARTITION OF wms.stock_movements FOR VALUES FROM (%L) TO (%L)',
                   part_name,
                   (first_day::timestamp AT TIME ZONE 'UTC'),
                   ((first_day + interval '1 month')::timestamp AT TIME ZONE 'UTC'));
  END IF;
END $$;
SELECT wms.ensure_movement_partition((now() AT TIME ZONE 'UTC')::date);
SELECT wms.ensure_movement_partition(((now() AT TIME ZONE 'UTC') + interval '1 month')::date);

CREATE TABLE IF NOT EXISTS wms.balance_change_queue (
    tenant_id uuid NOT NULL, owner_id uuid NOT NULL, warehouse_id uuid NOT NULL, variant_id uuid NOT NULL,
    queued_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_balance_change_queue" PRIMARY KEY (tenant_id, owner_id, warehouse_id, variant_id));
```
Y **al final** de `Up` (después de que EF cree `stock_balances`), las dos vistas. `v_stock_rows` es la única definición de `available`/`pickable` y toma `is_virtual` y `allows_picking` del join a `locations` (S1); `v_stock_summary` se define sobre ella con las columnas exactas de S10:
```sql
CREATE VIEW wms.v_stock_rows AS
SELECT b.id, b.tenant_id, b.owner_id, b.warehouse_id, b.location_id, b.variant_id, b.lot_id, b.status, b.on_hand, b.reserved,
       CASE WHEN b.status = 'Available' AND NOT l.is_virtual THEN b.on_hand - b.reserved ELSE 0 END AS available,
       CASE WHEN b.status = 'Available' AND NOT l.is_virtual AND l.allows_picking THEN b.on_hand - b.reserved ELSE 0 END AS pickable,
       l.is_virtual, l.allows_picking, l.role AS location_role, l.code AS location_code, l.path AS location_path,
       b.allows_negative, b.version, b.updated_at
  FROM wms.stock_balances b JOIN wms.locations l ON l.id = b.location_id;

CREATE VIEW wms.v_stock_summary AS
SELECT r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id,
       sum(CASE WHEN NOT r.is_virtual THEN r.on_hand ELSE 0 END)                                                        AS on_hand,
       sum(CASE WHEN NOT r.is_virtual AND r.status = 'Received' THEN r.on_hand ELSE 0 END)                              AS received,
       sum(r.available)                                                                                                 AS available,
       sum(r.pickable)                                                                                                  AS pickable,
       sum(CASE WHEN NOT r.is_virtual THEN r.reserved ELSE 0 END)                                                       AS reserved,
       sum(CASE WHEN NOT r.is_virtual AND r.status IN ('Blocked', 'Quarantine', 'Damaged') THEN r.on_hand ELSE 0 END)   AS blocked,
       sum(CASE WHEN r.location_role = 'Transit' THEN r.on_hand ELSE 0 END)                                             AS in_transit
  FROM wms.v_stock_rows r
 GROUP BY r.tenant_id, r.owner_id, r.warehouse_id, r.variant_id;
```
`Down`: **vacío** (S11). Se borra lo que EF generó (`DropTable`, etc.) y queda sólo este comentario: «El ledger es prueba de negocio: el `Down` no borra movimientos, saldos, vistas ni quita el trigger. Un rollback es revertir el código y dejar el esquema. `AddCustomerDataConsents` sí borra en su `Down` y por eso no es el precedente.» Consecuencias que se aceptan y se documentan en T17: después de un `Down` el `Up` no se puede volver a aplicar sin intervención manual (el `CreateTable` de EF no es idempotente), y por eso la prueba del `Down` termina abajo sin llamar a la API. El SQL crudo del `Up` sigue siendo idempotente (`IF NOT EXISTS`, `OR REPLACE`, `DROP TRIGGER IF EXISTS`) para que un `Up` interrumpido se pueda repetir. El trigger no cubre `TRUNCATE`/`DROP`/`DETACH`: el rol de la aplicación no recibe `TRUNCATE` sobre `wms.stock_movements` (configuración del rol en la plataforma, no una migración; se anota en el README).

Si el designer no acepta `AreNullsDistinct(false)`, crear el índice único de `stock_balances` por `migrationBuilder.Sql("CREATE UNIQUE INDEX \"UQ_stock_balances_key\" ON wms.stock_balances (tenant_id, owner_id, location_id, variant_id, lot_id, status) NULLS NOT DISTINCT")`, como `AddProductCategories` en Catalog, y dejar el `HasIndex` fuera del modelo con un comentario.

- [ ] **Step 5: Correr y ver que pasa** — mismo comando del Step 2. Expected: PASS.
- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Wms tests/Modules/Wms
git commit -m "feat(wms): add partitioned immutable ledger schema, balances and stock summary view"
```

---

### Task 4: `InventoryLedger`, único escritor, con métricas y traducción por nombre

**Cubre:** RF-020, RNF-01, RNF-03, RNF-07, RNF-11, ADR-03.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/IInventoryLedger.cs`, `ILotRepository.cs`, `IBalanceChangeQueue.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedger.cs`, `LotRepository.cs`, `BalanceChangeQueue.cs`, `CheckViolation.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/WmsMetrics.cs`
- Modify: `WmsPorts.cs` (`IWmsUnitOfWork` + `BeginTransactionAsync`), `WmsUnitOfWork.cs` (transacción y traducciones), `WmsInfrastructureExtensions.cs` (registros)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/InventoryLedgerPostingTests.cs`, `WmsLedgerSeeding.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `IWarehouseRepository.GetLocationAsync`, `IVariantProfileRepository.GetOrCreateDefaultAsync`, `IClock`.
- Produces:
  - `public interface IInventoryLedger { Task<LedgerPosting> PostAsync(LedgerBatch batch, CancellationToken cancellationToken); }` con `public sealed record LedgerPosting(Guid BatchId, IReadOnlyList<Guid> MovementIds)`. **No commitea**: los movimientos quedan trackeados y los upserts corren por SQL en la misma conexión/transacción del `WmsDbContext`; el handler cierra con `IWmsUnitOfWork.SaveChangesAsync`. Exige `dbContext.Database.CurrentTransaction != null` (`InvalidOperationException` si no).
  - `IWmsUnitOfWork.BeginTransactionAsync(CancellationToken)` → `Task<IAsyncDisposable>`; `SaveChangesAsync` (sigue devolviendo `Task<int>`) commitea la transacción abierta (copia de `AutomationsUnitOfWork.SaveChangesAsync`).
  - `public interface ILotRepository { Task<Lot?> FindAsync(Guid tenantId, Guid ownerId, Guid variantId, string code, CancellationToken ct); Task<IReadOnlyDictionary<Guid, Lot>> GetManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct); void Add(Lot lot); }`
  - `public interface IBalanceChangeQueue { Task EnqueueAsync(IReadOnlyCollection<BalanceChangeKey> keys, CancellationToken ct); Task<IReadOnlyList<BalanceChangeKey>> DequeueAllAsync(int max, CancellationToken ct); }` con `public sealed record BalanceChangeKey(Guid TenantId, Guid OwnerId, Guid WarehouseId, Guid VariantId)`. `EnqueueAsync` desduplica, ordena por `(TenantId, OwnerId, WarehouseId, VariantId)` y manda **un solo** `INSERT … VALUES (…), (…) ON CONFLICT (tenant_id, owner_id, warehouse_id, variant_id) DO UPDATE SET queued_at = EXCLUDED.queued_at` (S26); con cero claves no ejecuta nada.
  - `internal sealed class WmsMetrics(IMeterFactory)` singleton: `MeterName = "Qep.Wms"`; `RecordMovements(MovementReason reason, int count)` → counter `qep.wms.movements`; `RecordConfirmDuration(double ms)` → histograma `qep.wms.confirm.duration` (`unit: "ms"`, límites `[10, 50, 150, 500, 1000, 2000, 5000]`); `SetBalanceDrift(long keys)` y `SetPartitionMissing(bool)` → gauges observables `qep.wms.balance.drift` y `qep.wms.partition.missing` que publican el último valor fijado (sin tags).
  - `internal static class CheckViolation { static bool Is(PostgresException exception, params string[] constraintNames) }` (`SqlState == PostgresErrorCodes.CheckViolation` —`"23514"`— y `ConstraintName` en la lista, comparación ordinal). Recibe `PostgresException` y no `DbUpdateException` porque el upsert corre por `ExecuteSqlAsync`/`SqlQuery` y la excepción llega sin envolver (S26); una rama en `SaveChangesAsync` no se ejecutaría nunca para este SQL.
  - **El ledger** envuelve cada upsert en `catch (PostgresException e) when (CheckViolation.Is(e, StockBalanceNonNegativeCheck, StockBalanceReservedCheck))` y traduce: `StockBalanceNonNegativeCheck` → `WmsDomainException(StockInsufficient, …, e)` (sin extensiones: último recurso); `StockBalanceReservedCheck` → `WmsDomainException(StockReserved, …, e)`.
  - **`AProfileBeingCreatedAlreadyExistsAsync` tiene que ser seguro dentro de la transacción explícita (I3 de la revisión final de WMS-B).** Hoy esa consulta de `WmsUnitOfWork` asume que `SaveChanges` es dueño de su transacción; el ledger corre dentro de `BeginTransactionAsync` y llama a `GetOrCreateDefaultAsync`, así que un 23505 sobre `IX_identifiers_tenant_value` o `PK_variant_profiles` deja la transacción abortada (`25P02`) y la consulta de la rama `catch` falla y escapa como 500 en lugar de 412 o 422. Esta tarea lo arregla: cuando `dbContext.Database.CurrentTransaction` no es nulo, `SaveChangesAsync` crea un savepoint (`CreateSavepointAsync`) antes de guardar y, al capturar el 23505, hace `RollbackToSavepointAsync` **antes** de consultar, sin depender del savepoint automático de EF; la alternativa equivalente es hacer esa consulta por una conexión nueva. Sin transacción explícita el comportamiento es el de hoy.
  - `WmsUnitOfWork` traduce, junto a las ramas de WMS-B: `UniqueViolation.Is(e, LotCodeIndex)` → `RequestConcurrencyException("concurrency.conflict")`; `PostgresException` (directa o interna) con `MessageText == "wms.ledger.immutable"` → `WmsDomainException(LedgerImmutable)` más `LogError` «ledger mutation attempted» (§24.2: se loguea como incidente). No traduce los `CHECK` de `stock_balances`: no pasan por ahí.

- [ ] **Step 1: Escribir el seeding y las pruebas**

`WmsLedgerSeeding.cs` (`internal static class`):
- `Task<LedgerSetup> ArrangeAsync(PostgreSqlContainer database, QepApiFactory factory)`: `CreateManager(factory)`; `SeedCallerMembershipAsync(factory)`; `CreateWarehouseAsync(manager, "BOG")`; `ListLocationsAsync` → ids de `SUPPLIER`, `ADJUSTMENT`, `TRANSIT`, `REC-01`; `CreateBinAsync(manager, warehouseId, "A", "A-01-02")`; `CreateVariantAsync(factory, "V1", null, [12])`; `OwnerId` Self = `ScalarAsync<Guid>(database, "SELECT id FROM wms.owners WHERE tenant_id = @t AND kind = 'Self'")` (el `Self` existe porque el primer request del manager provisiona; si no, `GET /owners`). Devuelve `LedgerSetup(Guid TenantId, HttpClient Manager, Guid WarehouseId, Guid OwnerId, Guid Supplier, Guid Adjustment, Guid Transit, Guid Receiving, Guid BinA, Guid VariantId)`.
- `Task<LedgerPosting> PostAsync(QepApiFactory factory, LedgerBatch batch)`: scope nuevo, `BeginTransactionAsync`, `IInventoryLedger.PostAsync`, `SaveChangesAsync`.
- `LedgerEntry Entry(LedgerSetup s, Guid from, StockStatus fromStatus, Guid to, StockStatus toStatus, decimal qty, MovementReason reason = Adjustment, Guid? lot = null)`; `LedgerBatch Batch(LedgerSetup s, params LedgerEntry[] entries)` con `LedgerActor(Guid.Parse(SubjectId), LedgerActorType.Human, null)` e `idempotencyKey = Guid.NewGuid().ToString()`.
- `Task<decimal> OnHandAsync(PostgreSqlContainer database, Guid locationId, Guid variantId, StockStatus status, Guid? lotId = null)` (0 si no hay fila).

`InventoryLedgerPostingTests`:
- `APostedBatchWritesOneMovementAndTwoBalancesWhoseSumIsZero`: `SUPPLIER/Available → REC-01/Received` 60 → `on_hand(REC-01, Received) == 60`, `on_hand(SUPPLIER) == -60`, `SELECT sum(on_hand) FROM wms.stock_balances WHERE tenant_id = @t` == 0, una fila en `wms.stock_movements` con `recorded_at >= occurred_at`, una fila en `balance_change_queue`.
- `MovingMoreThanTheSourceHoldsFailsWithAvailableAndLeavesNothingWritten`: saldo 10 en `A-01-02`; batch de 12 hacia `REC-01` → `StockInsufficientException` con `available == 10` y `locationCode == "A-01-02"`; después `on_hand(A-01-02) == 10` y el conteo de movimientos no cambió.
- `MovingFromAnEmptyKeyFailsWithAvailableZero`.
- `MovingIntoTheReservedPortionFailsWithReservedAndLeavesTheBalance` (Review Focus 7): saldo 10 en `A-01-02`; `ExecuteAsync(database, "UPDATE wms.stock_balances SET reserved = 4 WHERE location_id = @a AND variant_id = @v", …)` (las reservas como caso de uso llegan en WMS-F; aquí la columna se siembra por SQL, como hace T13 con `on_hand`); batch de 8 → `WmsDomainException` con `Code == "wms.stock.reserved"`, `on_hand` sigue 10 y `reserved` 4; batch de 12 → `StockInsufficientException` con `available == 6`.
- `ASupplierLocationGoesNegativeAndTransitDoesNot`: `SUPPLIER → REC-01` 5 deja `on_hand(SUPPLIER) == -5` con `allows_negative = true` en la fila; `TRANSIT → REC-01` 5 sin saldo previo → `StockInsufficientException` (S1: `TRANSIT` es virtual y no admite negativo).
- `TheProfileRulesApplyBeforeWriting`: perfil con `Tracking = Lot` (por `PUT /variant-profiles/{id}` de WMS-B con `version: 0`) y entrada sin lote → `WmsDomainException` `wms.lot.required`, sin filas nuevas.
- `AnOccurredAtOutsideTheWindowIsRejectedBeforeWriting`: entrada con `OccurredAt = now − 31 d` → `WmsDomainException` `wms.movement.occurred_at_out_of_range`, sin filas nuevas.
- `TwoBatchesOverTheSameMixedLotKeysInOppositeOrderNeverDeadlock`: saldos previos de 100 en `(A-01-02, L1)`, `(A-01-02, L2)`, `(REC-01, L1)`, `(REC-01, L2)` (perfil `Lot`, lotes creados con `ILotRepository.Add` en el seeding) **y además** en `(A-01-02, V2)` y `(REC-01, V2)` con una segunda variante `V2` sin lote; batch 1 mueve `A→REC` L1, `REC→A` L2 y `A→REC` V2; batch 2 mueve `REC→A` L1, `A→REC` L2 y `REC→A` V2 con las entradas en orden inverso; `Task.WhenAll` de 20 repeticiones de ambos → ninguna `PostgresException` con `SqlState == "40P01"`, la suma final por clave es la esperada y `balance_change_queue` tiene exactamente dos filas (`V1` y `V2`). Esta prueba vigila el orden de los saldos, no el de la cola: los dos batches comparten su primer saldo y nunca llegan juntos a la cola, así que un `EnqueueAsync` en orden de aparición la deja en verde (mutación del 2026-11-02). El orden de la cola lo vigila `TwoBatchesThatShareOnlyTheirQueueKeysNeverDeadlock`: sin saldos compartidos, dos sesiones retienen las filas de la cola hasta que cada batch espera por la suya, y en orden de aparición da `40P01` (S26). La pérdida del evento la vigilan `APublisherDequeuingWhileAWriterIsUncommittedLeavesTheKeyQueuedAfterTheCommit` y `AWriterWaitingOnThePublishersDeleteQueuesTheKeyAgain`.
- `AProfileCollisionInsideAnExplicitTransactionStillTranslatesTo412` (I3): dentro de `BeginTransactionAsync`, `GetOrCreateDefaultAsync` de una variante sin perfil; antes de guardar, otra conexión inserta por SQL el perfil de esa variante con sus identificadores; `SaveChangesAsync` lanza `RequestConcurrencyException` (412) y no una `PostgresException` `25P02`. Variante: la misma colisión por `IX_identifiers_tenant_value` (otra variante con un identificador igual) → `wms.identifier.value_taken`, también sin `25P02`. Y la transacción sigue utilizable para el rollback del llamador.
- `TheMovementsCounterIsIncrementedPerReason`: `MeterListener` filtrado por `ReferenceEquals(instrument.Meter.Scope, factory.Services.GetRequiredService<IMeterFactory>())` (patrón `InboundLagMetricTests`): tras postear 2 entradas `Adjustment`, la suma con tag `reason = "Adjustment"` es 2.

- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~InventoryLedgerPostingTests"
```
Expected: no compila (`IInventoryLedger`).

- [ ] **Step 3: Implementar `InventoryLedger.PostAsync`**

Algoritmo (lo único que las firmas no determinan):
0. **412 de `GetOrCreateDefaultAsync` = recargar.** Un perfil que falta se crea y se guarda en un scope y una transacción propios, como `IWmsProvisioner`; si eso devuelve 412 `concurrency.conflict` (otra petición lo creó primero), no se convierte en un error del movimiento: el ledger relee y encuentra el perfil. Dentro de la transacción del lote el 412 sólo aparecería al confirmar, sin nada que reintentar. Por eso quien llama no puede tener sin confirmar una escritura de perfil o identificador antes de `PostAsync`.
1. Ubicaciones distintas del batch → `GetLocationAsync(tenantId, new LocationId(id))` una vez cada una → `LedgerLocationInfo(id, WarehouseId.Value, Code, AllowsNegative, Status == Blocked, Status == Counting)`; variantes distintas → `GetOrCreateDefaultAsync` → `LedgerVariantRules(BaseUom.Value, BaseUomPrecision, Tracking, Conversions.ToDictionary(c => c.Code, c => c.Factor, StringComparer.OrdinalIgnoreCase))`; `LedgerEntryRules.Validate(entry, variant, from, to, clock.UtcNow)` por entrada (incluye la ventana de `occurredAt`, S12). Lotes referenciados → `ILotRepository.GetManyAsync` (para el `lotCode` de la excepción y `LotNotFound` si falta).
2. `LedgerDeltas.Net(entries)`; por cada `(key, delta)` en ese orden: si `delta < 0 && !location.AllowsNegative` → `UPDATE wms.stock_balances SET on_hand = on_hand + @delta, version = version + 1, updated_at = now() WHERE tenant_id = @t AND owner_id = @o AND location_id = @l AND variant_id = @v AND lot_id IS NOT DISTINCT FROM @lot AND status = @s AND on_hand + @delta >= reserved RETURNING on_hand, reserved` (S26); cero filas → `SELECT on_hand, reserved …` (0, 0 si no hay fila): si `onHand + delta < 0` → `throw new StockInsufficientException(location.Code, variantId, lotCode, onHand - reserved)`; si no (`>= 0` pero `< reserved`) → `throw new WmsDomainException(WmsErrorCodes.StockReserved, "Parte de ese saldo está reservado por otra demanda.")`. En otro caso, el `INSERT … ON CONFLICT (…) DO UPDATE …` literal de §6.3, con `allows_negative = location.AllowsNegative` y `warehouse_id` de la ubicación. Cada sentencia va dentro de `try { … } catch (PostgresException e) when (CheckViolation.Is(e, StockBalanceNonNegativeCheck, StockBalanceReservedCheck)) { throw Translate(e); }` (último recurso, sin extensiones). Todo por `dbContext.Database.SqlQuery<T>`/`ExecuteSqlAsync` interpolados (parámetros, nunca concatenación) sobre la conexión del contexto.
3. `Set<StockMovement>().AddRange(...)` con `Id = Guid.CreateVersion7()`, `RecordedAt = clock.UtcNow`, `ReversesMovementId`/`ReversesOccurredAt` de la entrada; con `SerialUnitId`, `SerialUnit.MoveTo(to.LocationId, to.Status)` sobre la entidad cargada.
4. **Después** de todos los upserts, como última escritura del lote, `IBalanceChangeQueue.EnqueueAsync(keys)` con las `BalanceChangeKey(tenantId, owner, warehouse, variant)` distintas del batch: un solo `INSERT` multi-fila, ordenado por `(TenantId, OwnerId, WarehouseId, VariantId)` (S26). Nunca un `INSERT` por clave en orden de aparición: dos batches cruzados tomarían los bloqueos de la cola en orden opuesto. **Un solo `PostAsync` por transacción:** un segundo lote tomaría saldos después de haber bloqueado la cola y rompería el orden global; lo que deba aplicarse junto va en un solo `LedgerBatch`. T8, T9 y T10 cumplen esto.
5. `WmsMetrics.RecordMovements` por motivo; la duración la mide el handler que confirma (Task 8), no el ledger.

- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wms tests/Modules/Wms
git commit -m "feat(wms): implement the inventory ledger as the single writer of movements and balances"
```

---

### Task 5: Advisory lock por documento en la unidad de trabajo

**Cubre:** RNF-03, §11.3.

**Files:**
- Modify: `WmsPorts.cs` (`IWmsUnitOfWork`), `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsUnitOfWork.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsUnitOfWorkTests.cs`

**Interfaces:**
- Produces: `Task<bool> TryLockDocumentAsync(Guid documentId, CancellationToken)` en `IWmsUnitOfWork` (exige transacción abierta; `pg_try_advisory_xact_lock(WmsLockNamespace, hashtext(@id))` con `internal const int WmsUnitOfWork.WmsLockNamespace = 7401`, distinto de los de Automations, Identity y Pos). Es **sólo para documentos** (ajuste, movimiento a reversar, preview): la numeración de ajustes usa su propio lock bloqueante en `AdjustmentRepository` (T8, S26) y comparte únicamente la constante del namespace.

- [ ] **Step 1: Prueba** — `WmsUnitOfWorkTests.TheDocumentLockIsExclusiveWhileTheTransactionIsOpen`: dos scopes; el primero abre transacción y toma el lock de un `Guid`; el segundo recibe `false`; al disponer el primero sin guardar, el segundo recibe `true`. `TryLockWithoutATransactionThrows` (`InvalidOperationException`).
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WmsUnitOfWorkTests"
```
Expected: no compila (`TryLockDocumentAsync`).

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add a document advisory lock to the unit of work"`.

---

### Task 6: Idempotencia — recibo, filtro, hash y purga

**Cubre:** RNF-02, ADR-06, D-11.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/IOperationReceipts.cs`, `IdempotencyScope.cs`, `IIdempotentRequest.cs`, `RequestHasher.cs`, `OperationReplayedException.cs`, `WmsWorkerOptions.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/OperationReceipts.cs`, `Workers/WmsHousekeepingWorker.cs`; `src/Modules/Wms/Modules.Wms.Api/IdempotencyKeyFilter.cs`
- Modify: `WmsUnitOfWork.cs`, `WmsInfrastructureExtensions.cs`, `src/Api/appsettings.json` (sección `Wms:Workers`), `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.cs` (en `QepApiFactory.ConfigureWebHost`: `UseSetting("Wms:Workers:BalanceChangePublishSeconds", "3600")`, `("Wms:Workers:ReconciliationSweepMinutes", "600")`, `("Wms:Workers:PartitionSweepHours", "24")`, `("Wms:Workers:HousekeepingHours", "24")` — desde el primer worker, para que ninguna prueba de T6–T14 corra con los defaults de producción; cada prueba de worker llama su `RunOnceAsync`/`PublishOnceAsync` a mano)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/RequestHasherTests.cs`, `OperationReceiptTests.cs`

**Interfaces:**
- Produces:
  - `public interface IIdempotentRequest;` marcador para los DTOs de cuerpo de las rutas **IK**. `public interface IVersionedRequest { long Version { get; } }` para los cuerpos de las rutas **SK** que llevan versión.
  - `public static class RequestHasher { public static string Hash(string method, string path, object? body); }` → SHA-256 hex en minúsculas de `$"{method} {path}\n{JsonSerializer.Serialize(body, Web)}"`; `body == null` serializa `null`.
  - `public enum IdempotencyKeySource { Header, Server }`; `public static class ServerIdempotencyKeys { public static string For(string document, Guid id, string action, long version) }` → `"{document}:{id:D}:{action}:{version}"`. En este plan el único uso es `("adjustment", id, "post", version)`.
  - `public sealed class IdempotencyScope { public Guid TenantId; public string? Key; public string? RequestHash; public IdempotencyKeySource Source; public bool IsActive => Key is not null; }` scoped.
  - `public interface IOperationReceipts { Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken ct); void Record<TResponse>(TResponse response); Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct); }` — `Record` lanza `InvalidOperationException` si el scope no está activo (una ruta IK/SK sin filtro es un error de cableado) y agrega la fila con `JsonSerializer.Serialize(response, Web)`. `FindAsync` es `AsNoTracking` (se vuelve a llamar después de una transacción deshecha).
  - `public sealed class OperationReplayedException(Guid tenantId, string key) : Exception`.
  - `public sealed class IdempotencyKeyFilter(IdempotencyKeySource source, string? document, string? action) : IEndpointFilter` (resuelve `IOperationReceipts`, `IdempotencyScope` e `IExecutionContext` desde `context.HttpContext.RequestServices`). Orden fijo (S2):
    1. `tenantId` desde `context.HttpContext.GetRouteValue("tenantId")`; si `executionContext.TenantId is null` o difiere → `throw WmsAuthorization.Denied()` (403 `authorization.denied`), **antes** de tocar `operation_receipts`: nunca revela si la clave existe.
    2. Clave. Modo `Header`: lee `Idempotency-Key`; vacío o > 128 → `PreconditionRequiredException(WmsErrorCodes.IdempotencyKeyRequired, "Esta operación necesita el header Idempotency-Key.")`. Modo `Server`: `ServerIdempotencyKeys.For(document, (Guid)GetRouteValue("id"), action, version)` con `version` del primer argumento `IVersionedRequest` (`InvalidOperationException` si no hay: cableado roto); el header se ignora aunque venga.
    3. Hash. Modo `Header`: `RequestHasher.Hash(method, path, primer argumento IIdempotentRequest o null)`. Modo `Server`: `RequestHasher.Hash(method, path, null)` — la versión ya está en la clave.
    4. Recibo existente con el mismo hash → `Results.Content(receipt.ResponseJson, "application/json", statusCode: 200)`; con otro hash → `ResourceConflictException(IdempotencyKeyReused, "Ya usaste esta Idempotency-Key con otro cuerpo.")` (sólo puede pasar en modo `Header`); sin recibo, fija el scope (`TenantId`, `Key`, `RequestHash`, `Source`) y `await next(context)`.
    5. `catch (OperationReplayedException)` → relee el recibo (la transacción del handler ya se deshizo por su `await using`) y aplica la regla 4.
    Extensiones: `RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder)` = `.AddEndpointFilter(new IdempotencyKeyFilter(IdempotencyKeySource.Header, null, null))`; `RouteHandlerBuilder UseServerIdempotencyKey(this RouteHandlerBuilder, string document, string action)` = `.AddEndpointFilter(new IdempotencyKeyFilter(IdempotencyKeySource.Server, document, action))`.
  - `public sealed class WmsWorkerOptions { const string SectionName = "Wms:Workers"; int BalanceChangePublishSeconds = 10; int ReconciliationHourUtc = 3; int ReconciliationSweepMinutes = 30; int PartitionSweepHours = 24; int HousekeepingHours = 24; int ReceiptRetentionDays = 45; int PreviewRetentionMinutes = 30; }` con `[Range]` y `AddOptions<…>().Bind(…).ValidateOnStart()` (patrón `LoansOptions`). `ReceiptRetentionDays = 45` (S13): sesión offline máxima de 30 días más margen; un evento más viejo que la ventana de S12 ya se rechazó con `wms.movement.occurred_at_out_of_range`, así que no hay un código aparte.
  - `WmsHousekeepingWorker`: cada `HousekeepingHours` purga recibos (`ReceiptRetentionDays`), previews vencidos (Task 10) y `DELETE FROM wms.stock_balances WHERE on_hand = 0 AND reserved = 0`.
  - `WmsUnitOfWork`: `UniqueViolation.Is(e, OperationReceiptPrimaryKey)` → `OperationReplayedException`.

- [ ] **Step 1: Pruebas unitarias**

```csharp
public sealed class RequestHasherTests
{
    private sealed record Body(long Version, string? Note) : IIdempotentRequest;

    [Fact]
    public void TwoEqualBodiesHashTheSame() =>
        Assert.Equal(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/a", new Body(1, "x")));

    [Fact]
    public void ADifferentPathOrBodyHashesDifferently()
    {
        Assert.NotEqual(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/b", new Body(1, "x")));
        Assert.NotEqual(RequestHasher.Hash("POST", "/a", new Body(1, "x")), RequestHasher.Hash("POST", "/a", new Body(2, "x")));
    }

    [Fact]
    public void TheHashIsSixtyFourLowercaseHexCharacters() =>
        Assert.Matches("^[0-9a-f]{64}$", RequestHasher.Hash("POST", "/a", null));
}
```
`OperationReceiptTests.AKeyLongerThan128IsRejected`, `AnEmptyKeyIsRejected`.

- [ ] **Step 2: Correr y ver que falla** — `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~RequestHasherTests|FullyQualifiedName~OperationReceiptTests"`.
- [ ] **Step 3: Implementar; registrar `IdempotencyScope` (scoped), `IOperationReceipts`, `IdempotencyKeyFilter` y el worker; agregar `"Wms": { "Workers": { … } }` a `appsettings.json` con los defaults.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando del Step 2, más `dotnet build tests/Modules/Wms/Modules.Wms.IntegrationTests` (el harness cambió y tiene que compilar). Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add idempotency receipts, endpoint filter and housekeeping worker"`.

---

### Task 7: Lectura de saldos — `GET /stock`, `GET /stock/summary`, `GET /stock/movements`

**Cubre:** RF-021, RF-022, RNF-05, RNF-06.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/InventoryDtos.cs`, `IStockReadQueries.cs`, `GetStock.cs`, `GetStockSummary.cs`, `ListStockMovements.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/StockReadQueries.cs`; `src/Modules/Wms/Modules.Wms.Api/StockEndpoints.cs`
- Modify: `WmsEndpoints.cs`, `WmsInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs` (handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockQueryTests.cs`

**Interfaces:**
- Consumes: `IWmsCatalogReader.GetVariantsAsync` (`productName`, `variantCode`, `barcode`); `IVariantProfileRepository.ListAsync` (`baseUom`, `displayUom`).
- Produces (DTOs de §24.3):
  - `StockRowDto(Guid OwnerId, string OwnerName, Guid WarehouseId, string WarehouseCode, Guid LocationId, string LocationCode, string LocationPath, Guid VariantId, string ProductName, string VariantCode, string? Barcode, Guid? LotId, string? LotCode, DateOnly? ExpirationDate, string Status, decimal OnHand, decimal Reserved, decimal Available, decimal Pickable, string BaseUom, string? DisplayUom, decimal? DisplayQty, DateTimeOffset UpdatedAt)` — `Pickable` (S10) es `available` sólo si la ubicación tiene `allows_picking`; comentario en el DTO: «la app de picking filtra por esto y la pantalla no conoce `allows_picking`».
  - `StockTotalsDto(decimal OnHand, decimal Reserved, decimal Available)`; `StockPageDto(IReadOnlyList<StockRowDto> Items, int Page, int PageSize, int TotalCount, StockTotalsDto Totals)` — comentario: «`totals` viaja porque sumar la página da un total que depende de la página».
  - `StockSummaryDto(decimal OnHand, decimal Received, decimal Available, decimal Pickable, decimal Reserved, decimal Blocked, decimal InTransit, decimal Expected)` — `Expected = 0` en este corte (RF-026 es fase 2) y **se compone en el handler**, no en la vista (S10), con comentario.
  - `MovementEndpointDto(string LocationCode, string Status)`; `MovementDto(Guid Id, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string Reason, decimal Quantity, string BaseUom, string? CapturedUom, decimal? CapturedQty, decimal? ConversionFactor, MovementEndpointDto From, MovementEndpointDto To, Guid VariantId, string ProductName, string VariantCode, string? LotCode, string? Serial, string? DocumentType, Guid? DocumentId, string? DocumentNumber, Guid BatchId, Guid? ReversesMovementId, Guid ActorId, string? ActorName, string ActorType, string? DeviceId)` — `ActorName` y `DocumentNumber` van `null` en este corte (comentario en el DTO). `MovementPageDto(IReadOnlyList<MovementDto> Items, string? NextCursor)` — `NextCursor = null` cuando no hay más; `public sealed record MovementCursor(DateTimeOffset OccurredAt, Guid Id)` con `Encode`/`TryDecode` copiados literalmente de `Modules.Messaging.Application.ConversationCursor` (base64url de ticks UTC + id); el validador de la query convierte un cursor que no decodifica en `validation.failed` con la clave `Cursor`. SQL: `WHERE (occurred_at, id) < (@occurredAt, @id) ORDER BY occurred_at DESC, id DESC LIMIT @limit + 1` (la fila extra decide `NextCursor`), que recorre la PK `(id, occurred_at)` por el índice `IX_stock_movements_tenant_occurred` o el que T3 defina sobre `(tenant_id, occurred_at DESC, id DESC)`; sin `COUNT(*)``.
  - `GetStockQuery(Guid TenantId, Guid? WarehouseId, Guid? OwnerId, Guid? VariantId, Guid? LocationId, Guid? LotId, string? Status, bool OnlyAvailable, bool IncludeVirtual, string? Search, int Page, int PageSize) : IQuery<StockPageDto>`; `GetStockSummaryQuery(Guid TenantId, Guid? WarehouseId, Guid? OwnerId, Guid? VariantId) : IQuery<StockSummaryDto>`; `ListStockMovementsQuery(Guid TenantId, Guid? VariantId, Guid? LocationId, Guid? LotId, string? Serial, string? DocumentType, Guid? DocumentId, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int Limit) : IQuery<MovementPageDto>` (decisión del owner 2026-11-01: la historia va por cursor, no por página; `Limit` 1..200, default 50; `Cursor` nulo = desde el más reciente). Los tres handlers reciben `WmsAuthorization` e `IExecutionContext` por constructor y llaman `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.StockRead, warehouseId: query.WarehouseId, ct: ct)`; un `Status` fuera del enum → `WmsDomainException(StockStatusInvalid)`. Ningún `GET` de este plan llama al provisioner: no necesitan el `Self` (S5).
  - `IStockReadQueries` (SQL crudo): `Task<StockSearchResult> SearchAsync(StockFilter filter, CancellationToken)` sobre **`wms.v_stock_rows`** unida a `wms.warehouses`, `wms.owners`, `wms.lots`, con `StockSearchResult(IReadOnlyList<StockBalanceRow> Rows, int Total, StockTotalsDto Totals)`; `Task<StockSummaryRow?> SummarizeAsync(Guid tenantId, Guid? warehouseId, Guid? ownerId, Guid? variantId, CancellationToken)` sobre **`wms.v_stock_summary`** (`SELECT sum(on_hand), sum(received), sum(available), sum(pickable), sum(reserved), sum(blocked), sum(in_transit) … WHERE …`; `StockSummaryRow` tiene esas siete columnas y el handler le suma `Expected = 0`); `Task<MovementSearchResult> ListMovementsAsync(MovementFilter filter, CancellationToken)`. `StockFilter`/`MovementFilter`/`StockBalanceRow`/`MovementRow`/`StockSummaryRow` son records de Application sin datos de Catalog (los pega el handler). `search` casa **sólo** contra `location_code ILIKE` y `lot.code ILIKE` en este corte (el buscador de producto manda `variantId`; comentario en el handler).
  - Regla de las virtuales: en `SearchAsync`, `WHERE NOT is_virtual` salvo `IncludeVirtual`; `totals` **siempre** excluye virtuales (`sum(CASE WHEN NOT is_virtual …)`); `summary` ya excluye virtuales en la vista salvo `in_transit`, que suma `location_role = 'Transit'` a propósito (virtual: por eso va aparte). Las fórmulas de `received`, `blocked`, `available` y `pickable` viven en la vista y no se repiten en C#.
  - Un filtro opcional que no viene **se omite del texto SQL**, nunca se manda `NULL` (CLAUDE.md, `OrdersReportSource`).
  - `StockEndpoints.Map(RouteGroupBuilder group)`: `GET /stock`, `GET /stock/summary`, `GET /stock/movements`, con `RequireAuthorization(WmsPermissions.StockRead)`; `WmsEndpoints.MapWmsEndpoints` lo llama.

- [ ] **Step 1: Pruebas** (`StockQueryTests`, siembra con `WmsLedgerSeeding.PostAsync`)
  - `TheStockPageCarriesTotalsOfTheWholeFilterNotOfThePage`: 3 claves con 10, 20 y 30; `pageSize=2` → `items.Count == 2`, `totalCount == 3`, `totals.onHand == 60`.
  - `VirtualLocationsNeverReachTotalsNorSummary`: tras `ADJUSTMENT → A-01-02` 10: `GET /stock` → 1 ítem, `totals.onHand == 10`; `?includeVirtual=true` → 2 ítems; `GET /stock/summary` → `onHand == 10`.
  - `TheSummarySplitsReceivedBlockedAndInTransit`: `REC-01/Received` 5, `REC-01/Quarantine` 2, `A-01-02/Available` 10, `TRANSIT/InTransit` 3 → `{ onHand: 17, received: 5, available: 10, pickable: 10, blocked: 2, inTransit: 3, reserved: 0, expected: 0 }`.
  - `PickableFollowsAllowsPicking`: `CreateBinAsync(manager, warehouseId, "B", "B-01-01", allowsPicking: false)`; 7 en `B-01-01/Available` y 10 en `A-01-02/Available` → la fila de `B-01-01` trae `available == 7` y `pickable == 0`; `summary.pickable == 10` y `summary.available == 17`.
  - `TheMovementHistoryIsCursorPagedNewestFirstAndFilteredByLocation`: 5 movimientos sobre `A-01-02` y 1 sobre `B-01-01`; `?locationId=A-01-02&limit=2` → 2 ítems, el primero el más reciente, `nextCursor != null`; seguir con `?cursor=<nextCursor>&limit=2` → los 2 siguientes sin repetir ni saltar; la tercera página → 1 ítem y `nextCursor == null`. `AGarbageCursorIs422ValidationFailed`: `?cursor=zzz` → 422 `validation.failed`, `errors.Cursor`.
  - `AnotherTenantGetsForbiddenNeverNotFound`: `CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions)` contra `WmsUrl(TenantId)` → 403 `authorization.denied` (`CodeOfAsync`).
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~StockQueryTests"
```
Expected: no compila (`StockRowDto`).

- [ ] **Step 3: Implementar queries, handlers, SQL y endpoints; registrar handlers.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add stock, summary and movement history queries"`.

---

### Task 8: Ajustes — propose/approve/post/reject, IK y concurrencia

**Cubre:** RF-023, RNF-02, RNF-03, RNF-04, RNF-07 (`wms_confirm_duration_ms`), RNF-11.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Control/InvalidTransitionException.cs`; `src/Modules/Wms/Modules.Wms.Application/Control/IAdjustmentRepository.cs`, `AdjustmentDtos.cs`, `AdjustmentPosting.cs`, `ProposeAdjustment.cs`, `ApproveAdjustment.cs`, `PostAdjustment.cs`, `RejectAdjustment.cs`, `ListAdjustments.cs`; `src/Modules/Wms/Modules.Wms.Application/Inventory/IWmsEventPublisher.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/AdjustmentRepository.cs`, `WmsEventPublisher.cs`; `src/Modules/Wms/Modules.Wms.Api/AdjustmentEndpoints.cs`
- Modify: `Control/Adjustment.cs` (comportamiento), `WmsPorts.cs` (`WmsAuditActions` y `Resources`), `WmsSupport.cs` (`WmsNotFound.Adjustment`), `WmsEndpoints.cs`, registros
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Control/AdjustmentTests.cs`; `tests/Modules/Wms/Modules.Wms.IntegrationTests/AdjustmentApiTests.cs`, `IdempotencyTests.cs`, `ConcurrentAdjustmentTests.cs`

**Interfaces:**
- Produces:
  - `Adjustment.Propose(Guid tenantId, OwnerId owner, WarehouseId warehouse, AdjustmentReason reason, string? note, DateTimeOffset? occurredAt, IReadOnlyList<AdjustmentLine> lines, Guid proposedBy, DateTimeOffset now)` (1..500 líneas, `QuantityDelta != 0` → `AdjustmentLineInvalid`, `Note` ≤ 500; `occurredAt` presente → `LedgerEntryRules.EnsureOccurredAtInWindow(occurredAt, now)`, S12); `Approve(Guid by, DateTimeOffset now)` sólo desde `Proposed`; `Reject(Guid by, string note, DateTimeOffset now)` desde `Proposed`/`Approved`; `MarkPosted(Guid batchId, DateTimeOffset now)` sólo desde `Approved`; cada transición `Version++`. Transición inválida → `InvalidTransitionException(string from, string action) : DomainException(WmsErrorCodes.DocumentInvalidTransition, …), IHasProblemExtensions { from, action }`.
  - `public static class AdjustmentPosting { public static IReadOnlyList<LedgerEntry> EntriesFor(Adjustment adjustment, Location adjustmentLocation, DateTimeOffset occurredAt, MovementReason reason) }`: delta > 0 → `StockKey(owner, ADJUSTMENT, variant, lot, status) → StockKey(owner, line.Location, …)`; delta < 0 → al revés con `|delta|`; `Document = ("Adjustment", adjustment.Id, line.Id)`; captura si viene (`Factor = |delta| / capturedQty`). Los ids fuertes se desenvuelven con `.Value`.
  - `IAdjustmentRepository { Task<Adjustment?> GetAsync(Guid tenantId, Guid id, CancellationToken); void Add(Adjustment); Task<AdjustmentPage> ListAsync(Guid tenantId, AdjustmentStatus? status, Guid? warehouseId, int page, int pageSize, CancellationToken); Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken); }` — número `"ADJ-{yyyyMMdd}-{NNNN}"` por `SELECT count(*) + 1` del día **después** de `SELECT pg_advisory_xact_lock({WmsUnitOfWork.WmsLockNamespace}, hashtext({"adj-seq:" + tenantId}))` (bloqueante, S26); exige `Database.CurrentTransaction != null` (`InvalidOperationException` si no): el lock vive hasta el commit de la transacción que `Propose` abrió, así dos propuestas simultáneas numeran en serie en vez de chocar. El choque de `AdjustmentNumberIndex` → `RequestConcurrencyException` (en la UoW) queda como último recurso. **No** usa `TryLockDocumentAsync`: un try-lock aquí devolvería 409 al segundo `Propose`.
  - `IWmsEventPublisher { void PublishAdjustmentPosted(Adjustment adjustment, DateTimeOffset occurredAt); void PublishBalanceChanged(BalanceChangedPayload payload, DateTimeOffset occurredAt); void PublishExportReady(Guid tenantId, Guid subjectId, Guid fileId, string downloadUrl, string fileName, int rowCount, DateTimeOffset expiresAt, DateTimeOffset occurredAt); }` → `WmsOutboxMessage` en `platform.outbox_messages`. Payload `wms.adjustment.posted.v1`: `{ tenantId, adjustmentId, number, warehouseId, ownerId, reason, batchId, lineCount, postedAt }` (record con nombres en minúscula, como `SupplierExportEventPublisher`). `BalanceChangedPayload(Guid TenantId, Guid OwnerId, Guid WarehouseId, Guid VariantId, decimal OnHand, decimal Reserved, decimal Available, DateTimeOffset ChangedAt)`.
  - En `WmsAuditActions` (modificación de `WmsPorts.cs`): `Resources.Adjustment = "wms.adjustment"`, `Resources.Movement = "wms.stock_movement"`, `Resources.Import = "wms.import"`, `Resources.Export = "wms.export"`; `AdjustmentProposed = "wms.adjustment.proposed"`, `AdjustmentApproved = "wms.adjustment.approved"`, `AdjustmentPosted = "wms.adjustment.posted"`, `AdjustmentRejected = "wms.adjustment.rejected"`, `StockImported = "wms.stock.imported"`, `MovementReversed = "wms.movement.reversed"`, `StockExported = "wms.stock.exported"`. Toda llamada: `Record(tenantId, executionContext.SubjectId, WmsActorType.Human, action, Resources.X, id.ToString(), changedFields, now)` con `changedFields` que **siempre** incluye `["ownerId"]` (§10.3) y, según el caso, `["number"]`, `["reason"]`, `["batchId"]`, `["lineCount"]`, `["note"]`.
  - Requests (Api): `AdjustmentLineRequest(Guid LocationId, Guid VariantId, string? LotCode, DateOnly? ExpirationDate, string Status, decimal QuantityDelta, string? CapturedUom, decimal? CapturedQty)`; `ProposeAdjustmentRequest(Guid WarehouseId, Guid? OwnerId, string Reason, string? Note, DateTimeOffset? OccurredAt, IReadOnlyList<AdjustmentLineRequest> Lines, bool AutoApprove) : IIdempotentRequest`; `AdjustmentVersionRequest(long Version) : IVersionedRequest` (cuerpo de la ruta SK `post` y de `approve`); `RejectAdjustmentRequest(long Version, string Note)`.
  - Commands: `ProposeAdjustmentCommand(Guid TenantId, Guid WarehouseId, Guid? OwnerId, string Reason, string? Note, DateTimeOffset? OccurredAt, IReadOnlyList<AdjustmentLineRequest> Lines, bool AutoApprove) : ICommand<AdjustmentDto>`; `ApproveAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version)`; `PostAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version)`; `RejectAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version, string Note)`; `ListAdjustmentsQuery(Guid TenantId, string? Status, Guid? WarehouseId, int Page, int PageSize)`. Validadores FluentValidation: `Reason` del catálogo, `Status` del enum, `Lines` 1..500, `Note` ≤ 500, `QuantityDelta != 0`. El `IdempotencyKey` del batch lo toma el handler de `IdempotencyScope.Key`.
  - `AdjustmentDto(Guid Id, string Number, Guid WarehouseId, string WarehouseCode, Guid OwnerId, string Reason, string Status, string? Note, DateTimeOffset? OccurredAt, IReadOnlyList<AdjustmentLineDto> Lines, Guid? BatchId, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset? PostedAt, DateTimeOffset? RejectedAt, string? RejectNote, long Version)`; `AdjustmentLineDto(Guid Id, Guid LocationId, string LocationCode, Guid VariantId, string ProductName, string VariantCode, Guid? LotId, string? LotCode, string Status, decimal QuantityDelta, string? CapturedUom, decimal? CapturedQty)`; `AdjustmentPageDto(Items, Page, PageSize, TotalCount)`.
  - Handlers de escritura: reciben por constructor `IExecutionContext`, `WmsAuthorization`, `IWmsProvisioner`, `IWmsUnitOfWork`, `IdempotencyScope`, `IOperationReceipts`, `IWmsAuditRecorder`, `IClock`, los repositorios que usan y, los que escriben al ledger, `IInventoryLedger`, `WmsCaller`, `IWmsEventPublisher` y `WmsMetrics`.
  - Flujo de `Propose`: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.AdjustmentPropose, warehouseId: command.WarehouseId, ct: ct)` o, si no lo tiene, el mismo con `AdjustmentApprove`; **`await using var tx = await uow.BeginTransactionAsync(ct)`** (S26: la numeración toma un lock de transacción y el `await using` deshace todo si `SaveChangesAsync` lanza `OperationReplayedException`); owner: `ownerId == null` → `await provisioner.EnsureAsync(tenantId, ct)` y luego `GetSelfAsync` (S5); si viene, `FindAsync(new OwnerId(ownerId))` → `WmsNotFound.Owner`; bodega `GetAsync` → `WmsNotFound.Warehouse`; cada `LocationId` con `warehouse.FindLocation` (ajena → `WmsNotFound.Location`); `lotCode` presente → `ILotRepository.FindAsync` o `Lot.Create(…, expiryRequired: profile.TracksExpiry)` + `Add`; `NextNumberAsync` (lock bloqueante dentro de `tx`); `Adjustment.Propose(…, command.OccurredAt, …)` (ventana S12 → 422 `wms.movement.occurred_at_out_of_range`); `autoApprove && HasPermission(AdjustmentApprove)` → `Approve` en el mismo guardado; auditoría (una por transición); `receipts.Record(dto)`; `SaveChangesAsync` (commitea `tx`); 201.
  - Flujo de `Post`: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.AdjustmentApprove, ct: ct)`; `Stopwatch`; **`await using var tx = await uow.BeginTransactionAsync(ct)`**; `TryLockDocumentAsync(id)` falso → `ResourceConflictException(DocumentBusy, "Alguien más está posteando este ajuste. Intenta de nuevo en un momento.")`; `GetAsync` → `WmsNotFound.Adjustment`; `WmsVersions.Ensure(adjustment.Version, command.Version)`; `GetVirtualLocationAsync(tenantId, warehouse, LocationRole.Adjustment)`; `occurredAt = adjustment.OccurredAt ?? clock.UtcNow`; `AdjustmentPosting.EntriesFor(…, occurredAt, MovementReason.Adjustment)`; `actorId = await caller.RequireAsync(tenantId)`; `IInventoryLedger.PostAsync(new LedgerBatch(tenantId, Guid.CreateVersion7(), scope.Key!, new LedgerActor(actorId, LedgerActorType.Human, null), entries))` — `scope.Key` es la clave de servidor `"adjustment:{id}:post:{version}"` que dejó el filtro (S2); el ledger vuelve a validar la ventana de `occurredAt`; `MarkPosted`; auditoría `AdjustmentPosted`; `PublishAdjustmentPosted`; `receipts.Record(dto)`; `SaveChangesAsync`; `WmsMetrics.RecordConfirmDuration`.
  - Rutas (`AdjustmentEndpoints.Map(group)`): `POST /adjustments` (**IK**: `RequireIdempotencyKey()`, `RequireAuthorization(WmsPermissions.AdjustmentPropose)`), `POST /adjustments/{id:guid}/approve`, `POST /adjustments/{id:guid}/post` (**SK**: `UseServerIdempotencyKey("adjustment", "post")`; el header, si viene, se ignora), `POST /adjustments/{id:guid}/reject` (los tres con `AdjustmentApprove`), `GET /adjustments` (`DocumentRead`).

- [ ] **Step 1: Pruebas unitarias** (`AdjustmentTests`): `ProposeRejectsZeroDeltaLines`, `PostingRequiresApproval` (`wms.document.invalid_transition`, extensiones `from = "Proposed"`, `action = "post"`), `RejectingAPostedAdjustmentIsInvalid`, `EveryTransitionBumpsTheVersion`, `PostingEntriesComeFromAndGoToTheAdjustmentLocationByDeltaSign` (línea +10 y línea −4 → dos entradas con los extremos correctos y el mismo `status` en ambos lados).

- [ ] **Step 2: Pruebas de integración** (todas arrancan con `OpenAsync()` + `WmsLedgerSeeding.ArrangeAsync`; el cliente con permiso de proponer sin aprobar es `CreateClient(factory, SubjectId, TenantId, WmsPermissions.AdjustmentPropose, WmsPermissions.DocumentRead)`)

`AdjustmentApiTests`:
- `ProposingWithAutoApproveAndThePermissionReturnsApproved` (201, `status == "Approved"`, `CountAuditEntriesAsync(factory, "wms.adjustment.proposed", id) == 1` y lo mismo con `approved`).
- `ProposingWithAutoApproveWithoutThePermissionStaysProposed`.
- `PostingMovesStockAndPublishesTheEvent`: línea +10 en `A-01-02` → 200, `batchId != null`, `OnHandAsync(A-01-02) == 10`, `OnHandAsync(ADJUSTMENT) == -10`, una fila en `platform.outbox_messages` con `event_name = 'wms.adjustment.posted.v1'` cuyo payload trae `adjustmentId` y `batchId`.
- `PostingAProposedAdjustmentIs422InvalidTransition` con extensiones `{ from: "Proposed", action: "post" }`.
- `AStaleVersionIs412`.
- `ANewLotIsCreatedOnProposeAndExpiryIsRequiredWhenTheProfileTracksIt` (`wms.lot.expiry_required`).
- `AnOccurredAtOlderThan30DaysIs422`: `POST /adjustments` con `occurredAt = DateTimeOffset.UtcNow.AddDays(-31)` → 422 `wms.movement.occurred_at_out_of_range`, cero ajustes; con `AddDays(-29)` → 201 y el `post` escribe el movimiento con ese `occurred_at` (S12).
- `AProposalWithoutOwnerUsesTheSelfOwnerProvisionedOnTheFly`: tenant recién sembrado sin ningún request previo a `/owners`; `POST /adjustments` sin `ownerId` → 201 y `dto.ownerId` es el `Self` de `wms.owners` (S5: el handler llamó al provisioner antes de `GetSelfAsync`).
- `ListingFiltersByStatusAndWarehouse`.

`IdempotencyTests` (sobre `POST /adjustments` —IK— y `POST /adjustments/{id}/post` —SK—):
- `WithoutTheHeaderItIs428` (`POST /adjustments`, `wms.idempotency_key_required`).
- `TheSameKeyAndBodyReplayTheStoredResponseWithoutASecondAdjustment`: dos POST iguales → 201 y luego **200** con el mismo JSON (`Assert.Equal(firstJson, secondJson)`), `count(*) FROM wms.adjustments == 1`, `count(*) FROM wms.operation_receipts == 1`.
- `TheSameKeyWithAnotherBodyIs409` (`wms.idempotency_key_reused`).
- `AnEquivalentBodyWithAnotherPropertyOrderReplaysInsteadOfConflicting`: el segundo request se manda con `StringContent` y las propiedades en otro orden → 200 y un solo ajuste.
- `TwoSimultaneousRequestsWithTheSameKeyLeaveOneAdjustment`: `Task.WhenAll` de dos POST idénticos → `{ 201, 200 }`, un ajuste con un solo `number` (el segundo `Propose` esperó el lock de numeración y después chocó con la PK del recibo, que el filtro convirtió en la respuesta guardada).
- `AnotherTenantReplayingAKeyIs403` (Review Focus 6): tras un `POST /adjustments` del tenant A con clave `K`, `CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions)` repite el mismo request contra `WmsUrl(TenantId)` con `K` → 403 `authorization.denied`, sin cuerpo de ajuste; `count(*) FROM wms.operation_receipts == 1`.
- `APostReplayedWithTheSameVersionReturnsTheStoredResponseWithoutAHeader`: ajuste aprobado con `version = v`; `POST …/post { version: v }` **sin** `Idempotency-Key` → 200 con `batchId`; repetir exactamente → 200 con el mismo JSON (no 412), un solo `batch_id` en `wms.stock_movements`, y `SELECT idempotency_key FROM wms.operation_receipts WHERE tenant_id = @t` contiene `$"adjustment:{id}:post:{v}"`; un tercer intento con header `Idempotency-Key: otra` → la misma respuesta 200 (el header se ignora, S2).

`ConcurrentAdjustmentTests`:
- `TwoPostsTakingEightFromTenEndWithOneSuccessOneInsufficientAndTwoLeft`: saldo 10 en `A-01-02` (ajuste +10 posteado); dos ajustes aprobados de −8; `Task.WhenAll` de los dos `post` → `{ 200, 422 }` en cualquier orden, el 422 con `code == "wms.stock.insufficient"` y `available == 2`, `OnHandAsync(A-01-02) == 2`, el ajuste que falló sigue `Approved`.
- `TwoSimultaneousProposalsGetConsecutiveNumbers`: `Task.WhenAll` de dos `POST /adjustments` con claves distintas → dos 201 con `number` distintos y consecutivos del día (`…-0001`, `…-0002`), ningún 409 ni 412 (S26: el lock de numeración es bloqueante).

- [ ] **Step 3: Correr y ver que falla**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~AdjustmentTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~AdjustmentApiTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~ConcurrentAdjustmentTests"
```

- [ ] **Step 4: Implementar agregado, handlers, repositorio, publisher, endpoints; registrar.**
- [ ] **Step 5: Correr y ver que pasa** — los dos comandos del Step 3. Expected: PASS.
- [ ] **Step 6: Commit** — `git commit -m "feat(wms): add manual adjustments with idempotent posting through the ledger"`.

---

### Task 9: Reverso de un movimiento

**Cubre:** RF-025, RNF-02, RNF-04.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/ReverseMovement.cs`, `IMovementReader.cs`
- Modify: `StockReadQueries.cs` (implementa `IMovementReader`), `StockEndpoints.cs`, `WmsSupport.cs` (`WmsNotFound.Movement`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/ReverseMovementApiTests.cs`

**Interfaces:**
- Produces: `IMovementReader { Task<StockMovement?> GetAsync(Guid tenantId, Guid movementId, CancellationToken); Task<bool> IsReversedAsync(Guid tenantId, Guid movementId, CancellationToken); }` (lectura con `Set<StockMovement>().AsNoTracking()` dentro de `StockReadQueries`: el ledger sigue siendo el único escritor). `ReverseMovementRequest(string? Note) : IIdempotentRequest`; `ReverseMovementCommand(Guid TenantId, Guid MovementId, string? Note) : ICommand<MovementDto>`. `IMovementReader.GetAsync` busca por `id` (la PK `(id, occurred_at)` da índice por prefijo) e `IsReversedAsync` consulta `EXISTS (… WHERE reverses_movement_id = @id)` sobre `IX_stock_movements_reverses`. Handler: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.AdjustmentApprove, ct: ct)`; **`await using var tx = await uow.BeginTransactionAsync(ct)`**; `TryLockDocumentAsync(movementId)` → `DocumentBusy`; `GetAsync` → `WmsNotFound.Movement`; `IsReversedAsync` o `movement.Reason == Reversal` → `WmsDomainException(MovementAlreadyReversed, "Este movimiento ya fue reversado.")` (422; §24.2 corregido: antes `not_reversible`); reconstruye la `LedgerEntry` original desde el movimiento y aplica `LedgerEntry.Reverse(original, movementId, movement.OccurredAt, clock.UtcNow, note)` — el compensatorio lleva `ReversesMovementId` **y** `ReversesOccurredAt` (S4) y su propio `OccurredAt = now`, que siempre cae en la ventana de S12; `PostAsync` con `batchId = original.BatchId`, `idempotencyKey = scope.Key!` y `actorId = caller.RequireAsync`; auditoría `MovementReversed` (`Resources.Movement`, `changedFields { ownerId, reversesMovementId, batchId }`); `receipts.Record(dto)`; `SaveChangesAsync`; 201. Ruta `POST /stock/movements/{id:guid}/reverse` (**IK**: `RequireIdempotencyKey()`; un movimiento no tiene `version`, así que no hay clave de servidor que derivar).

- [ ] **Step 1: Pruebas** — `ReversingAMovementRestoresTheBalancesAndSharesTheBatch` (saldo vuelve a 0 en `A-01-02`, el nuevo movimiento tiene `reason == "Reversal"`, `reversesMovementId == original`, mismo `batchId`, y por SQL `reverses_occurred_at` igual al `occurred_at` del original), `ReversingTwiceIs422AlreadyReversed`, `ReversingAReversalIs422AlreadyReversed`, `ReversingWithoutTheHeaderIs428`, `ReversingWithTheSameKeyReplays` (200 y un solo movimiento nuevo).
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~ReverseMovementApiTests"
```
- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add compensating reversal of a stock movement"`.

---

### Task 10: Carga inicial por Excel — preview y commit

**Cubre:** RF-024, RNF-02, RNF-04.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Control/IInitialBalanceSheetReader.cs`, `IImportPreviewStore.cs`, `InitialBalanceRowRules.cs`, `PreviewInitialBalances.cs`, `CommitInitialBalances.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Excel/ClosedXmlInitialBalanceSheetReader.cs`, `Persistence/ImportPreviewStore.cs`; `src/Modules/Wms/Modules.Wms.Api/ImportEndpoints.cs`
- Modify: `WmsPorts.cs` (`IWmsCatalogReader` + `FindVariantsByCodesAsync`; `IWarehouseRepository` + `FindLocationByCodeAsync`), `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WarehouseRepository.cs`, `src/Bootstrapper/WmsCatalogReader.cs` (el constructor de WMS-B es `WmsCatalogReader(IProductRepository products, IOptionDefinitionRepository options)`; aquí gana `CatalogDbContext catalog`), `tests/Bootstrapper/Bootstrapper.UnitTests/WmsCatalogReaderTests.cs` (de WMS-B: se le pasa un `CatalogDbContext` construido con `new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql("Host=localhost;Database=unused").Options` —no conecta; sus casos sólo ejercen `GetVariantsAsync`—; sin este cambio no compila), `WmsSupport.cs` (`WmsNotFound.Lot`, `WmsNotFound.ImportPreview`), `Modules.Wms.Infrastructure.csproj` (`<PackageReference Include="ClosedXML" />` + `dotnet restore` y commit de los `packages.lock.json` que cambien), `WmsHousekeepingWorker.cs` (purga de previews)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/InitialBalanceImportTests.cs` (+ helper que arma el `.xlsx` con ClosedXML en memoria), `tests/Modules/Wms/Modules.Wms.UnitTests/Control/InitialBalanceRowRulesTests.cs`

**Interfaces:**
- Produces:
  - `IWmsCatalogReader.FindVariantsByCodesAsync(Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken)` → `IReadOnlyList<WmsVariantSnapshot>` de todas las variantes cuyo `upper(code)` esté en `upper(codes)`. Adaptador: `WmsCatalogReader(IProductRepository products, IOptionDefinitionRepository options, CatalogDbContext catalog)` resuelve los ids con `catalog.Database.SqlQuery<Guid>($"SELECT v.id AS \"Value\" FROM catalog.product_variants v JOIN catalog.products p ON p.id = v.product_id WHERE p.tenant_id = {tenantId} AND upper(v.code) = ANY({upperCodes})")`, y luego reutiliza `GetVariantsAsync(ids)` para armar los snapshots (`IProductRepository.FindVariantByCodeAsync` es igualdad exacta y no sirve para detectar la ambigüedad sin distinguir mayúsculas). La búsqueda contra el esquema real la prueba `InitialBalanceImportTests`, no la unitaria.
  - `IWarehouseRepository.FindLocationByCodeAsync(Guid tenantId, WarehouseId warehouseId, string code, CancellationToken)` → `Location?` (código normalizado con `LocationCodes.Normalize`, comparación ordinal; `WarehouseRepository` lo implementa sobre `Locations`).
  - `IInitialBalanceSheetReader { InitialBalanceSheet Parse(Stream content, CancellationToken); }`; `InitialBalanceSheet(bool HasExpectedColumns, IReadOnlyList<InitialBalanceSheetRow> Rows)`; `InitialBalanceSheetRow(int Row, string? Code, string? Location, string? Lot, string? Expiration, string? Quantity, string? Uom)` — texto crudo por cabecera (`code, location, lot, expiration, quantity, uom`, sin distinguir mayúsculas), filas vacías omitidas, archivo ilegible → `HasExpectedColumns = false` (patrón `ClosedXmlCustomerImporter`).
  - `IImportPreviewStore { void Add(ImportPreview); Task<ImportPreview?> GetAsync(Guid tenantId, Guid id, CancellationToken); Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken); }`.
  - `PreviewInitialBalancesCommand(Guid TenantId, Guid WarehouseId, Guid? OwnerId, string FileName, long Length, Stream Content) : ICommand<InitialBalancePreviewDto>`; `InitialBalancePreviewDto(Guid PreviewId, DateTimeOffset ExpiresAt, long WarehouseVersion, IReadOnlyList<InitialBalancePreviewRowDto> Rows, bool CanCommit)`; `InitialBalancePreviewRowDto(int Row, string Code, Guid? VariantId, string? ProductName, string LocationCode, Guid? LocationId, string? LotCode, DateOnly? ExpirationDate, decimal Quantity, string? Uom, IReadOnlyList<string> Issues)`. Issues: `wms.import.unknown_code`, `wms.import.ambiguous_code`, `wms.import.unknown_location` (código inexistente en la bodega vía `FindLocationByCodeAsync`, virtual, o sin `AllowsStorage`), `wms.lot.required`, `wms.lot.not_allowed`, `wms.lot.expiry_required`, `wms.uom.precision_exceeded`, `wms.uom.factor_mismatch` (uom que no es base ni conversión), `wms.movement.quantity_invalid` (no numérica o ≤ 0), `wms.import.duplicate_row`. `CanCommit = Rows.Count > 0 && Rows.All(r => r.Issues.Count == 0)`. **El preview con issues responde 200** (S21): los issues son datos de la fila, no un error del request. Sin columnas, vacío, ilegible o > 5 MB → `WmsDomainException(ImportInvalidFile)`; **más de `LedgerBatch.MaxEntries` (500) filas → `WmsDomainException(ImportTooManyRows, "La hoja tiene más de 500 filas; divídela en varias cargas.")`** antes de resolver códigos (S21: la carga es un solo batch y nunca podría commitearse). La `Quantity` guardada ya está **en base**. Handler del preview: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.AdjustmentPropose, warehouseId: command.WarehouseId, ct: ct)`; `ownerId == null` → `provisioner.EnsureAsync` y `GetSelfAsync` (S5); guarda el preview con `SaveChangesAsync` sin transacción explícita (una sola fila).
  - `InitialBalanceRowRules.Classify(InitialBalanceSheetRow row, IReadOnlyList<WmsVariantSnapshot> candidates, Location? location, LedgerVariantRules? variant, bool tracksExpiry, ISet<(Guid VariantId, Guid LocationId, string? LotCode)> seenKeys)` → `InitialBalancePreviewRowDto`: pura, es lo que prueba la unitaria.
  - `CommitInitialBalancesRequest(Guid PreviewId, long Version) : IIdempotentRequest` (`Version` de la bodega; distinta de la actual → 412 vía `WmsVersions.Ensure`); `CommitInitialBalancesCommand(Guid TenantId, Guid PreviewId, long Version) : ICommand<InitialBalanceCommitDto>`; `InitialBalanceCommitDto(Guid AdjustmentId, Guid BatchId, int MovementCount)`. Handler: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.AdjustmentPropose, ct: ct)` (§24.2 y §24.3: `propose` para proponer **e importar**; el ajuste de la carga nace aprobado y posteado por el mismo camino); preview → `WmsNotFound.ImportPreview`; vencido → `ImportPreviewExpired`; `!CanCommit` → `ImportInvalidFile`; **`await using var tx = await uow.BeginTransactionAsync(ct)`** + `TryLockDocumentAsync(previewId)`; el owner ya quedó resuelto en el preview (`ImportPreview.OwnerId`); crea lotes faltantes; `NextNumberAsync`; `Adjustment.Propose(reason: InitialLoad, occurredAt: null)` + `Approve` + `PostAsync` con `MovementReason.InitialLoad`, `occurredAt = clock.UtcNow` y `idempotencyKey = scope.Key!` + `MarkPosted`; auditoría `StockImported` (`Resources.Import`, id del preview, `changedFields { ownerId, warehouseId, adjustmentId, rowCount }`) y `AdjustmentPosted`; `receipts.Record(dto)`; `SaveChangesAsync`; 201.
  - Rutas (`ImportEndpoints.Map(group)`): `POST /imports/initial-balances/preview` (multipart `IFormFile file` + query `warehouseId`, `ownerId?`; `.DisableAntiforgery()`; permiso `AdjustmentPropose`), `POST /imports/initial-balances/commit` (IK; `AdjustmentApprove`).

- [ ] **Step 1: Pruebas**

Unitaria `InitialBalanceRowRulesTests`: `AnExactCodeWinsOverCaseInsensitiveCandidates`, `TwoCaseInsensitiveCandidatesWithoutAnExactMatchAreAmbiguous`, `NoCandidateIsUnknown`, `ACajaUomIsConvertedToBaseWithTheProfileFactor` (`3 CAJA`, factor 12 → 36), `AnUnknownUomIsAFactorMismatch`, `ARepeatedKeyIsADuplicateRow`, `AVirtualLocationIsUnknownForImport`.

Integración `InitialBalanceImportTests`:
- `ThePreviewResolvesCodesLocationsAndLotsAndStoresThePreview` (dos filas válidas → `canCommit == true`, `previewId` existe en `wms.import_previews`).
- `AnAmbiguousCodeAndAnUnknownLocationBlockTheCommit` (variantes `abc` y `ABC` con `CreateVariantAsync` y fila `Abc`; ubicación `ZZ-99`) → issues `wms.import.ambiguous_code` y `wms.import.unknown_location`, `canCommit == false`; el commit responde 422 `wms.import.invalid_file`.
- `ARepeatedKeyIsAnIssueAndADifferentLocationIsNot`.
- `TheCommitPostsOneInitialLoadBatchAndIsIdempotent`: commit → 201 `{ movementCount: 2 }`, `wms.adjustments` con `reason = 'InitialLoad'` y `status = 'Posted'`, dos movimientos con `reason = 'InitialLoad'` y el mismo `batch_id`, saldos correctos; repetir con la misma clave → 200 y nada nuevo.
- `AnExpiredPreviewIs422`: `ExecuteAsync(database, "UPDATE wms.import_previews SET expires_at = now() - interval '1 minute'")` → `wms.import.preview_expired`.
- `AFileWithMoreThan500RowsIs422TooManyRows`: hoja de 501 filas válidas → 422 `wms.import.too_many_rows`, ninguna fila en `wms.import_previews`; con 500 → 200 y `canCommit == true` (S21).
- `APreviewWithIssuesIs200AndTheCommitIs422`: la hoja de `AnAmbiguousCodeAndAnUnknownLocationBlockTheCommit` responde **200** (no 422) con `canCommit == false`; sólo el commit es 422 `wms.import.invalid_file` (S21).
- `TheCommitWithoutTheHeaderIs428`.

- [ ] **Step 2: Correr y ver que falla**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~InitialBalanceRowRulesTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WmsCatalogReaderTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~InitialBalanceImportTests"
```
Expected: la unitaria y la de integración no compilan; `WmsCatalogReaderTests` sigue verde hasta que el constructor cambie en el Step 3 (ahí se rompe y se arregla en la misma tarea).

- [ ] **Step 3: Implementar lector ClosedXML, reglas, handlers, store, adaptador de Catalog (y su prueba unitaria de WMS-B), repositorio y endpoints.**
- [ ] **Step 4: Correr y ver que pasa** — los tres comandos del Step 2. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add initial balance import with preview and idempotent commit"`.

---

### Task 11: Exportación de saldos a Excel por outbox

**Cubre:** RF-030 (parcial: sólo saldos; la exportación de movimientos queda pendiente), RNF-14.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/ExportStock.cs`, `IStockExportBuilder.cs`, `IWmsExportStorage.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Excel/ClosedXmlStockExportBuilder.cs`; `src/Bootstrapper/WmsExportStorage.cs`; `src/Modules/Notifications/Modules.Notifications.Application/WmsExportEmailTemplate.cs`; `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/WmsExportDeliveryWorker.cs`
- Modify: `WmsEventPublisher.cs` (`PublishExportReady`), `StockEndpoints.cs`, `QepServiceCollectionExtensions.cs` (`IWmsExportStorage`), `NotificationsInfrastructureExtensions.cs` (`AddHostedService<WmsExportDeliveryWorker>()`), `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs` (fila `"wms.export-ready.v1"`), y **sólo si WMS-B no lo trajo**: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs` (`FileOwnerType.WmsExport`) y `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs` (`[FileOwnerType.WmsExport] = TenantModuleKey.Wms` en `ByOwnerType`, S14)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockExportApiTests.cs`, `tests/Modules/Notifications/Modules.Notifications.UnitTests/WmsExportEmailTemplateTests.cs`

**Interfaces:**
- Produces: `ExportStockRequest` = los filtros de `GetStockQuery` sin paginación; `ExportStockCommand(Guid TenantId, …filtros…) : ICommand<StockExportAcceptedDto>`; `StockExportAcceptedDto(Guid FileId, string FileName, int RowCount, DateTimeOffset ExpiresAt)`; `IStockExportBuilder { StockExportFile Build(IReadOnlyList<StockRowDto> rows, TenantCalendar calendar, CancellationToken); }` con `StockExportFile(byte[] Content, string FileName)` (`saldos-{yyyyMMdd-HHmm}.xlsx`; columnas: Bodega, Ubicación, Propietario, Producto, Código, Código de barras, Lote, Vence, Estado, Disponible, Reservado, En mano, Unidad); `IWmsExportStorage { Task<WmsExportUpload> UploadAsync(Guid tenantId, Guid fileId, string fileName, byte[] content, CancellationToken); }`, `WmsExportUpload(string DownloadUrl, DateTimeOffset ExpiresAt)`; adaptador `WmsExportStorage` = copia de `SupplierExportStorage` con `FileOwnerType.WmsExport` (módulo `wms`, S14), clave `exports/tenants/{tenantId:N}/wms/{fileId:N}.xlsx` y `AbsoluteUri`. Handler: `await authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.StockExport, warehouseId: …, ct: ct)`; lee por lotes de 500 con `IStockReadQueries.SearchAsync` hasta `MaxExportRows = 50_000` (`ExportTooManyRows`), cero filas → `ExportEmpty`; sube; `PublishExportReady` (**`wms.export-ready.v1`**, nunca `suppliers.export-ready.v1`, S15; payload `{ tenantId, subjectId, fileId, downloadUrl, fileName, rowCount, expiresAt }`); auditoría `StockExported` (`Resources.Export`, `fileId`, `changedFields { ownerId, rowCount, fileName }`); `SaveChangesAsync`; 202. `WmsExportDeliveryWorker` (`Modules.Notifications.Infrastructure/Messaging/`, modelado sobre `SupplierExportDeliveryWorker`): `Consumer => "notifications.wms-export-email"`, `EventName => "wms.export-ready.v1"`, `TemplateRef => WmsExportEmailTemplate.TemplateRef`; `WmsExportEmailTemplate` (`Modules.Notifications.Application`, modelada sobre `SupplierExportEmailTemplate`) con `TemplateRef = "wms.export-ready.v1"` y asunto «Tu exportación de saldos está lista». La columna «Disponible» sale de `StockRowDto.Available` y se agrega «Para picking» (`Pickable`, S10).

- [ ] **Step 1: Pruebas** — `StockExportApiTests.ExportingAcceptsUploadsAndQueuesTheEmail` (202 con `fileId`, una fila `wms.export-ready.v1` en el outbox con `fileId` y `downloadUrl`, auditoría `wms.stock.exported`; `IWmsExportStorage` se sustituye por un doble vía `new QepApiFactory(cs, configureServices: services => services.AddScoped<IWmsExportStorage, FakeExportStorage>())`), `ExportingWithoutRowsIs422Empty`, `ExportingNeedsTheExportPermission` (403 con `CreateReader`). `WmsExportEmailTemplateTests` calcada de `SupplierExportEmailTemplateTests`. `DeliveryWorkersCharacterizationTests` suma la fila del evento.
- [ ] **Step 2: Correr y ver que falla**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~WmsExportEmailTemplateTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~StockExportApiTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkersCharacterizationTests"
```
Expected: las dos primeras no compilan; la caracterización falla porque no hay worker para `wms.export-ready.v1`.

- [ ] **Step 3: Implementar.**
- [ ] **Step 4: Correr y ver que pasa** — los tres comandos del Step 2. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): export stock to excel and deliver the link by email through the outbox"`.

---

### Task 12: `BalanceChangePublisher` — `wms.balance.changed.v1` coalescido

**Cubre:** RNF-14, §11.4.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Workers/BalanceChangePublisher.cs`
- Modify: `IStockReadQueries` (+ `SummarizeByVariantAsync`), `WmsInfrastructureExtensions.cs` (los `UseSetting("Wms:Workers:*")` del harness ya existen desde T6; esta tarea no toca `WmsApiHarness.cs`)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/BalanceChangePublisherTests.cs`

**Interfaces:**
- Produces: `BalanceChangePublisher : BackgroundService` con `PeriodicTimer(BalanceChangePublishSeconds)`; `internal Task<int> PublishOnceAsync(CancellationToken)`: en un scope, `await using var tx = await uow.BeginTransactionAsync(ct)`, `IBalanceChangeQueue.DequeueAllAsync(500)`, por clave `IStockReadQueries.SummarizeByVariantAsync(tenantId, ownerId, warehouseId, variantId)` (una fila de `v_stock_summary`) → `BalanceChangedPayload`, `IWmsEventPublisher.PublishBalanceChanged`, `SaveChangesAsync` (una transacción por vuelta: si falla, las claves vuelven porque el `DELETE … RETURNING` no commiteó). Esa transacción tiene que ser corta: un escritor que encola una clave recién sacada espera el commit del publicador con sus saldos bloqueados. Payload: `{ tenantId, ownerId, warehouseId, variantId, onHand, reserved, available, changedAt }`.

- [ ] **Step 1: Prueba** — `ThreeMovementsOfTheSameVariantProduceOneEvent`: tres ajustes posteados sobre `V1` en `BOG` → `PublishOnceAsync` devuelve 1, un `wms.balance.changed.v1` con `available` igual al saldo final, la cola queda vacía; una segunda vuelta publica 0.
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~BalanceChangePublisherTests"
```

- [ ] **Step 3: Implementar y registrar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): coalesce balance changes into wms.balance.changed.v1 events"`.

---

### Task 13: `BalanceReconciliationWorker` y gauge de drift

**Cubre:** RF-029 (parte de este corte), RNF-01, RNF-11.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/IBalanceReconciliation.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/BalanceReconciliation.cs`, `Workers/BalanceReconciliationWorker.cs`
- Modify: `WmsInfrastructureExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/BalanceReconciliationTests.cs`

**Interfaces:**
- Produces: `IBalanceReconciliation { Task<IReadOnlyList<Guid>> ListWarehousesAsync(CancellationToken); Task<int> ReconcileWarehouseAsync(Guid warehouseId, CancellationToken); }` → número de claves con drift. SQL: recompone `on_hand` desde `wms.stock_movements` unida a `wms.locations` por `from_location_id`/`to_location_id` (sólo las de esa bodega), `FULL OUTER JOIN` con `wms.stock_balances` con `IS NOT DISTINCT FROM` en `lot_id`, inserta un `balance_checks` por clave con `COALESCE(ledger, 0) <> COALESCE(balance, 0)`; **no corrige**. `BalanceReconciliationWorker`: cada `ReconciliationSweepMinutes` mira si `clock.UtcNow.Hour == ReconciliationHourUtc` y no corrió hoy (marca en memoria, como `ExportJobWorker._nextPurgeAt`); `internal Task<long> RunOnceAsync(CancellationToken)` recorre bodegas, suma los drifts y llama `WmsMetrics.SetBalanceDrift(total)`.

- [ ] **Step 1: Prueba** — `ASeededDriftIsRecordedAndNotCorrected`: saldo 10 por ajuste; `ExecuteAsync(database, "UPDATE wms.stock_balances SET on_hand = 7 WHERE location_id = @a", ("a", binA))`; `RunOnceAsync` → 1; una fila en `wms.balance_checks` con `ledger_on_hand = 10`, `balance_on_hand = 7`; el saldo sigue en 7; el gauge `qep.wms.balance.drift` publica 1 (`MeterListener` + `RecordObservableInstruments()`). `AConsistentWarehouseWritesNothing`.
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~BalanceReconciliationTests"
```
Expected: no compila (`IBalanceReconciliation`).

- [ ] **Step 3: Implementar y registrar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): add nightly ledger-to-balance reconciliation with drift gauge"`.

---

### Task 14: `PartitionMaintenanceWorker` y gauge `wms_partition_missing`

**Cubre:** RNF-08, ADR-10.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/IPartitionMaintenance.cs`; `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/PartitionMaintenance.cs`, `Workers/PartitionMaintenanceWorker.cs`
- Modify: `WmsInfrastructureExtensions.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/PartitionMaintenanceTests.cs`

**Interfaces:**
- Produces: `IPartitionMaintenance { Task EnsureAsync(DateOnly month, CancellationToken); Task<bool> ExistsAsync(DateOnly month, CancellationToken); }` (`InventoryLedgerSql.EnsurePartition` y consulta a `pg_class`); worker: al arrancar y cada `PartitionSweepHours`, con `hoy = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)` (UTC, como los límites de la función), `EnsureAsync(hoy)` y `EnsureAsync(hoy + 1 mes)`; luego `SetPartitionMissing(!await ExistsAsync(next))`; un fallo del `CREATE` (por ejemplo `DEFAULT` con filas de ese mes) se loguea con `LogError` y deja el gauge en 1. `internal Task RunOnceAsync(CancellationToken)`. **El worker sólo crea: no tiene camino de `DETACH` ni `DROP`** (S12: nunca se desprende una partición de un mes dentro de la ventana de `occurredAt`, y fuera de ella es una decisión operativa, no del worker); `IPartitionMaintenance` no expone ningún método que quite particiones.

- [ ] **Step 1: Prueba** — `TheGaugeReportsOneWhileNextMonthHasNoPartition`: `DROP TABLE wms.stock_movements_y…` del mes siguiente; `ExistsAsync(next)` → `false`; se inserta con `InsertRawMovementAsync` una fila fechada el mes siguiente (cae en `DEFAULT`), así `RunOnceAsync` falla al recrear la partición y el gauge publica 1; `RunOnceIsIdempotentAndRecreatesAMissingPartitionWhenDefaultIsClean` (sin esa fila, la recrea y el gauge publica 0); `ThePartitionMaintenanceExposesNoDetach` (reflexión: `typeof(IPartitionMaintenance).GetMethods()` son exactamente `EnsureAsync` y `ExistsAsync`).
- [ ] **Step 2: Correr y ver que falla**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~PartitionMaintenanceTests"
```
Expected: no compila (`IPartitionMaintenance`).

- [ ] **Step 3: Implementar y registrar.**
- [ ] **Step 4: Correr y ver que pasa** — mismo comando. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(wms): create next month's ledger partition ahead of time and expose a missing-partition gauge"`.

---

### Task 15: Sondas de saldo reales y `CatalogVariantUsageChecker` con el WMS

**Cubre:** RF-010 (dato `hasStock`), P-3 de WMS-B (`wms.location.has_stock`), §17 (borrar una variante con movimientos).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/StockProbes.cs` (`VariantStockProbe`, `LocationStockProbe`, `OwnerStockProbe`)
- Modify (guarda de I4): `src/Modules/Wms/Modules.Wms.Domain/Warehousing/VariantLogisticsProfile.cs` (`Update`), `WmsErrorCodes.cs`, `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (`IVariantStockProbe.HasStockForManyAsync`), `GetVariantProfiles.cs`; tests `VariantLogisticsProfileTests.cs`, `VariantProfileApiTests.cs`
- Modify: `WmsInfrastructureExtensions.cs` (reemplaza `NoVariantStockProbe`/`NoLocationStockProbe`/`NoOwnerStockProbe` por las tres reales **en esta misma tarea**; se borra `NoStockProbes.cs`), `src/Bootstrapper/CatalogVariantUsageChecker.cs` (dos `UNION` más y el `<summary>`), `tests/Bootstrapper/Bootstrapper.UnitTests/CatalogVariantUsageCheckerTests.cs`
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/StockProbeTests.cs`

**Interfaces:**
- Produces:
  - **Guarda de unidad base y factores con saldo (I4, pendiente de confirmación del owner).** WMS-B sólo protege `Tracking` con `hasStock`; el saldo se guarda en la unidad base, así que con saldo cambiar `BaseUom`, bajar `BaseUomPrecision` o cambiar el factor de una conversión **existente** (por `Code`, sin distinguir mayúsculas) reinterpreta los saldos en silencio. `VariantLogisticsProfile.Update(changes, hasStock, now)` rechaza con 422 `wms.profile.base_uom_change_requires_identification` (código nuevo en `WmsErrorCodes`, también en el spec §24.2 como pendiente) cuando `hasStock` y cambia `BaseUom`, baja `BaseUomPrecision` o cambia el `Factor` de una conversión que ya existía. Agregar una conversión nueva, quitar una o cambiar su código de barras sigue permitido. RF-010 sólo nombra el tracking: si el owner quiere otra regla, se relaja aquí.
  - **Sonda en lote.** `IVariantStockProbe.HasStockForManyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken) → IReadOnlySet<Guid>` con **una** consulta (`SELECT DISTINCT b.variant_id … WHERE b.variant_id = ANY(@ids)`, mismo filtro de no virtual y saldo distinto de cero que `HasStockAsync`); `GetVariantProfiles` la usa para armar `hasStock` de hasta 200 variantes en una consulta y no en 200.
  - `VariantStockProbe.HasStockAsync` = `EXISTS (SELECT 1 FROM wms.stock_balances b JOIN wms.locations l ON l.id = b.location_id WHERE b.tenant_id = @t AND b.variant_id = @v AND NOT l.is_virtual AND (b.on_hand <> 0 OR b.reserved <> 0))` (la virtualidad sale de `locations`, S1: `stock_balances` ya no la guarda); `LocationStockProbe.HasStockAsync` = lo mismo por `b.location_id`; `OwnerStockProbe.HasStockAsync` = lo mismo por `b.owner_id` (respalda `wms.owner.has_stock` al desactivar un propietario). En el checker: `UNION SELECT m.variant_id FROM wms.stock_movements m WHERE m.tenant_id = {tenantId} AND m.variant_id = ANY({ids}) UNION SELECT b.variant_id FROM wms.stock_balances b WHERE b.tenant_id = {tenantId} AND b.variant_id = ANY({ids})`. La réplica mínima de `CreateLineTablesAsync` suma `CREATE SCHEMA wms; CREATE TABLE wms.stock_movements (tenant_id uuid NOT NULL, variant_id uuid NOT NULL); CREATE TABLE wms.stock_balances (tenant_id uuid NOT NULL, variant_id uuid NOT NULL);`. Las dos `TheCatalogUsageQueryFindsTheBackfilledVariant` (`VariantLinesMigrationTests` en Quotations, `SaleLinesVariantMigrationTests` en Pos) resuelven el checker real contra el esquema migrado: con los `UNION` nuevos siguen verdes sólo si el esquema `wms` existe en esas bases (lo crea `InitialWms` al migrar todo); se corren aquí para verlo.

- [x] **Step 1: Pruebas** — `CatalogVariantUsageCheckerTests.CheckerFindsVariantUsedOnlyByAWmsMovementOrBalance` (una variante por tabla, en el tenant y en otro tenant). `StockProbeTests.AVariantWithPhysicalStockHasStockAndOneWithOnlyVirtualDoesNot`, `TheBatchProbeAnswersTwoHundredVariantsInOneQuery` (cuenta las sentencias contra `wms.stock_balances` con un `DbCommandInterceptor`: una sola; el conjunto devuelto contiene sólo las variantes con saldo), `VariantLogisticsProfileTests.WithStockChangingTheBaseUomOrAnExistingFactorIsRejected` (Theory: `BaseUom`, precisión a la baja, factor de una conversión existente → `wms.profile.base_uom_change_requires_identification`; una conversión nueva, una quitada y un código de barras distinto pasan) y `VariantProfileApiTests.WithStockChangingTheBaseUomIs422` (sonda con saldo, `PUT` con otra unidad base → 422 con ese código; sin saldo → 200), `ALocationWithStockCannotBeDeleted` (`DELETE /warehouses/{id}/locations/{binA}` → 422 `wms.location.has_stock`), `AnOwnerWithStockCannotBeDeactivated` (ajuste posteado para un owner creado por `POST /owners`; su desactivación por la API de WMS-B → 422 `wms.owner.has_stock`; un owner sin saldo sí se desactiva), `TheRealCheckerSeesAWmsMovementAgainstTheMigratedSchema` (resuelve `IVariantUsageChecker` del contenedor tras un ajuste posteado).
- [x] **Step 2: Correr y ver que falla**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariantUsageCheckerTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~StockProbeTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~VariantLinesMigrationTests.TheCatalogUsageQueryFindsTheBackfilledVariant"
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~SaleLinesVariantMigrationTests.TheCatalogUsageQueryFindsTheBackfilledVariant"
```
Expected: las dos primeras fallan (la unitaria por el `UNION` que falta, la de sondas porque `NoStockProbes` responde `false`); las dos de migración están verdes antes del cambio y tienen que seguirlo después.

- [x] **Step 3: Implementar.**
- [x] **Step 4: Correr y ver que pasa** — los cuatro comandos del Step 2. Expected: PASS.
- [x] **Step 5: Commit** — `git commit -m "feat(wms): back the stock probes with the ledger and count ledger rows as variant usage"`.

---

### Task 16: Pruebas de arquitectura y del composition root

**Cubre:** RNF-12, §19 (`WmsLayerTests`).

**Files:**
- Modify: `tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs`, `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

- [x] **Step 1: Agregar pruebas**
  - `WmsLayerTests.TheDbContextExposesNoSetForLedgerTables`: `typeof(WmsDbContext).GetProperties(Public | NonPublic | Instance)` no tiene ninguna de tipo `DbSet<StockMovement>` ni `DbSet<StockBalance>`.
  - `WmsLayerTests.OnlyTheLedgerConstructsMovements`: `StockMovement.Record` es `internal` y `Modules.Wms.Application` **no** está entre los `InternalsVisibleToAttribute` de `Modules.Wms.Domain` (sólo Infrastructure y las dos pruebas).
  - `DomainAreasOnlyDependDownwards` de WMS-B queda como está: `Inventory` sigue sin referencias y `Control` (ajustes) sólo mira `Inventory` y `Warehousing`.
  - `CompositionRootTests.TheContainerValidatesAndBuildsTheWmsWorkers`: patrón `TheContainerValidatesAndBuildsTheLoansGenerationWorker` para `BalanceChangePublisher`, `BalanceReconciliationWorker`, `PartitionMaintenanceWorker`, `WmsHousekeepingWorker`, `WmsExportDeliveryWorker`.
- [x] **Step 2: Correr** — `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~CompositionRootTests"`. Expected: PASS (si `EveryCommandAndQueryHasItsHandlerRegistered` falla, falta un registro de Tasks 7–11).
- [x] **Step 3: Commit** — `git commit -m "test(wms): pin the ledger as the single writer and build the wms workers from the container"`.

---

### Task 17: Documentación, verificación por proyecto y cierre de rama

**Cubre:** ADR-02 («se documenta en el README»), RNF-12.

**Files:**
- Modify: `CLAUDE.md` (sección «Gotchas verificados», al final), `README.md` (subsección «Inventario (WMS): ledger y saldos» junto a lo que WMS-B haya dejado en «API implementada»), este plan (checkboxes)

- [x] **Step 1: Entradas de `CLAUDE.md`** (una viñeta cada una, en el tono del archivo):
  - **«Limpiar» `wms.stock_movements` con SQL falla a propósito**: `trg_stock_movements_immutable` lanza `wms.ledger.immutable` ante `UPDATE`/`DELETE`, también en pruebas y en local; la corrección es un `Reversal` por la API. **El `Down` de `AddInventoryLedger` está vacío a propósito**: no quita tablas, vistas ni el trigger, y después de bajarlo el `Up` no se vuelve a aplicar solo (el `CreateTable` de EF no es idempotente): el rollback es revertir el código y dejar el esquema. Un `TRUNCATE` sí pasa (no es DML por fila): el rol de la aplicación no lo tiene en producción; en local no lo uses fuera de una base desechable.
  - **La clave de `stock_balances` es `UNIQUE … NULLS NOT DISTINCT`**: dos filas con `lot_id` nulo y el resto igual chocan; un `INSERT` a mano lleva `lot_id = NULL` para una variante sin lote y `allows_negative` y `warehouse_id` copiados de la ubicación; `on_hand < 0` sólo pasa el `CHECK` si `allows_negative`, que es `true` para `SUPPLIER`, `CUSTOMER` y `ADJUSTMENT` y **`false` para `TRANSIT`** aunque sea virtual. `stock_balances` no guarda si la ubicación es virtual: eso se lee de `locations.is_virtual` (las vistas y las sondas ya hacen el join).
  - **El guard del upsert es `on_hand + delta >= reserved`, y sus `CHECK` no pasan por `SaveChanges`**: el ledger escribe con `ExecuteSqlAsync`, así que un `23514` llega como `PostgresException` suelto y lo traduce `InventoryLedger` por `ConstraintName`; una rama en `WmsUnitOfWork.SaveChangesAsync` nunca se ejecutaría. Sacar más de `on_hand − reserved` es `wms.stock.insufficient` si deja `on_hand` negativo y `wms.stock.reserved` si sólo invade lo reservado.
  - **`occurredAt` tiene ventana**: `[now − 30 días, now + 5 min]`; fuera, 422 `wms.movement.occurred_at_out_of_range`, también al postear un ajuste propuesto hace tiempo con fecha física. Por eso `operation_receipts` se purga a 45 días y ninguna partición de un mes dentro de la ventana se desprende.
  - **`stock_movements` está particionada por mes de `occurred_at`, con límites en UTC**: la PK es `(id, occurred_at)`; una fila fuera de rango cae en `stock_movements_default` y se queda ahí (el trigger impide moverla); `PartitionMaintenanceWorker` crea la del mes siguiente y `qep_wms_partition_missing = 1` dice que no pudo; si `DEFAULT` ya tiene filas de ese mes, `CREATE TABLE … PARTITION OF` falla y se resuelve a mano en ventana. Un `SELECT` sin `occurred_at` en el `WHERE` recorre todas las particiones; `reverses_movement_id` por eso viaja con `reverses_occurred_at` y sin FK.
  - **El ledger netea y ordena las claves por `StockKeyComparer`**: un camino nuevo que escriba `stock_balances` fuera de `InventoryLedger` (no hay `DbSet`; `WmsLayerTests` lo vigila) o que tome bloqueos en otro orden reabre los deadlocks que `TwoBatchesOverTheSameMixedLotKeysInOppositeOrderNeverDeadlock` cierra. `Inventory` referencia owner, bodega y ubicación por `Guid`: no «mejores» eso con `OwnerId`/`LocationId`, que viven en `Warehousing` y `DomainAreasOnlyDependDownwards` lo prohíbe.
  - **Las rutas IK y SK devuelven la respuesta guardada con 200 aunque el original fue 201**: el recibo se escribe con `IOperationReceipts.Record` **antes** de `SaveChangesAsync`; un handler IK/SK que no lo llame pasa las pruebas de éxito y deja la operación sin idempotencia. `request_hash` sale del DTO enlazado, no del cuerpo crudo: el orden de las propiedades no importa, un campo nuevo en el DTO sí cambia el hash de un reintento viejo. `POST /adjustments/{id}/post` es **SK**: la clave es `"adjustment:{id}:post:{version}"` y el header se ignora; repetir el `post` con la versión vieja devuelve el 200 guardado, no 412. El filtro compara el tenant del contexto con el de la ruta **antes** de buscar el recibo: no lo «simplifiques» moviendo la búsqueda arriba, porque otro tenant podría sondear claves ajenas.
  - **Los handlers que escriben al ledger abren la transacción con `await using var tx = await uow.BeginTransactionAsync(ct)`**: si `SaveChangesAsync` lanza `OperationReplayedException`, el `await using` deshace la transacción y el filtro relee el recibo; sin el `await using` la relectura corre sobre una transacción abortada y responde 500 donde tocaba 200. Y `Propose` la abre aunque no escriba movimientos: la numeración `ADJ-…` toma `pg_advisory_xact_lock` (bloqueante) y sin transacción no hay lock.
- [x] **Step 2: README**: qué es el ledger y por qué no se edita (y que el rol de la aplicación no recibe `TRUNCATE` sobre `wms.stock_movements`: configuración de la plataforma, no migración); la tabla de endpoints de §24.3 implementados aquí (`/stock`, `/stock/summary`, `/stock/movements`, `/stock/export`, `/adjustments*`, `/imports/initial-balances/*`, `/stock/movements/{id}/reverse`) con permiso y si es **IK** (header) o **SK** (clave de servidor; sólo `/adjustments/{id}/post`); el contrato de `Idempotency-Key` (428/200/409, retención 45 días); la ventana de `occurredAt`; los workers y sus opciones `Wms:Workers:*`; las métricas `qep.wms.*` y sus nombres en Prometheus; el procedimiento de partición faltante (crear, nunca desprender); el formato del Excel de carga inicial (columnas, **tope de 500 filas por archivo**, preview 200 con issues, commit 422 si hay issues); que `POST /stock/export` entrega por correo (`wms.export-ready.v1`).
- [x] **Step 3: Verificación por proyecto (no la suite completa)**

```powershell
Stop-Process -Name Api -ErrorAction SilentlyContinue
dotnet restore Backend.slnx
dotnet build --no-restore
dotnet format --verify-no-changes --include src/Modules/Wms src/Bootstrapper src/Modules/Notifications tests/Modules/Wms tests/Bootstrapper tests/ArchitectureTests
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --no-build
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build --filter "FullyQualifiedName~CatalogVariantUsageCheckerTests|FullyQualifiedName~WmsCatalogReaderTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build --filter "FullyQualifiedName~WmsExportEmailTemplateTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build --filter "FullyQualifiedName~DeliveryWorkersCharacterizationTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~VariantLinesMigrationTests.TheCatalogUsageQueryFindsTheBackfilledVariant"
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --no-build --filter "FullyQualifiedName~SaleLinesVariantMigrationTests.TheCatalogUsageQueryFindsTheBackfilledVariant"
git status --short
```
Expected: build sin warnings nuevos; todas las pruebas listadas PASS; `git status` sin `.cs` de 0 bytes ni `packages.lock.json` sin commitear (si cambió alguno, va en este commit). `dotnet format` se mide contra el baseline del repo (CRLF): sólo cuentan los archivos tocados.
- [x] **Step 4: Commit** — `git commit -m "docs(wms): document the immutable ledger, partitions and idempotency contract"`.
- [ ] **Step 5: Cierre**: `git log --oneline develop..feature/wms-d`; la rama queda lista para revisión y merge a `develop` (nunca a `main`); `docs/estado.md` se actualiza en `develop` al mergear. **Corre después de Task 18** (D-12, 2026-11-03): la rama no se cierra sin la dimensión `handling_unit_id`.

---

### Task 18: Dimensión `handling_unit_id` en la clave del saldo y en el movimiento (D-12)

> **Agregada el 2026-11-03**, con Tasks 1–17 ya implementadas en la rama. Spec §5.1, §6.1, §6.3, §16.2, WMS-ADR-13.

**Cubre:** D-12 (sólo la dimensión), RNF-01, RNF-03, RNF-13 (el mismo argumento: la clave no se reescribe con datos vivos).

**Por qué aquí y no después.** El owner decidió el 2026-11-03 que la unidad logística (caja o palet, LPN) es una
dimensión del saldo. La clave única de `stock_balances` y la tabla particionada `stock_movements` existen sólo en
`feature/wms-d`, sin merge a `develop` ni datos en producción: hoy cambiarlas es una columna y un índice; después del
merge sería reescribir la clave con stock vivo (spec §15).

**Qué entra en WMS-D y qué no.** Entra **sólo la dimensión**, y siempre en `NULL`: columnas, clave única, `StockKey` y su
orden, upsert y débito del ledger, reverso, reconciliación, `balance_checks` y la vista por clave. **No** entran —van con
el corte que confirme recepciones (WMS-G) y con el putaway (WMS-H), a ratificar al planearlos—: el agregado
`HandlingUnit` y la tabla `wms.handling_units`, las FKs hacia ella, el índice parcial de contenido, la verificación de
«una unidad, una ubicación» (`wms.handling_unit.split_location`), el bloqueo de la fila de la unidad, las rutas de §24.4,
la etiqueta `handling-unit-label.typ`, los campos `handlingUnitId` de `StockRowDto`/`MovementDto`, la columna en
`reservations` (fase 3) y en `adjustment_lines`. Todo eso es aditivo (columnas nullable, tablas nuevas) y no toca la
clave. Ningún código de error nuevo nace en esta tarea.

**Lo que ya existe y cambia** (estado de la rama en `470088d8`):
- `src/Modules/Wms/Modules.Wms.Domain/Inventory/StockKey.cs:7` — `StockKey(Guid OwnerId, Guid LocationId, Guid VariantId, Guid? LotId, StockStatus Status)`; `StockKeyComparer` (`:13-40`) ordena ubicación, variante, lote, estado, dueño.
- `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedgerModel.cs:93-96` — `UQ_stock_balances_key` sobre `(TenantId, OwnerId, LocationId, VariantId, LotId, Status)` con `AreNullsDistinct(false)`.
- `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedger.cs` — `DebitAsync` filtra por `lot_id IS NOT DISTINCT FROM` (`:232`, `:247`) y `UpsertAsync` hace `ON CONFLICT (tenant_id, owner_id, location_id, variant_id, lot_id, status)` (`:269`).
- `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedgerSql.cs` — `CreateStockMovements` (sin columnas de unidad) y `CreateStockRowsView` (`v_stock_rows` sin `handling_unit_id`). Su comentario lo fija: **estas constantes son historia de `AddInventoryLedger`; un cambio va en una migración nueva**.
- `src/Modules/Wms/Modules.Wms.Domain/Inventory/StockMovement.cs` (`Record`, `:112-125`: guarda un solo `LotId` y las ubicaciones/estados de cada lado) y `StockBalance.cs`.
- `src/Modules/Wms/Modules.Wms.Application/Inventory/ReverseMovement.cs:116-117` — rearma las dos claves desde el movimiento guardado: sin la unidad, el reverso de un movimiento con unidad compensaría saldos sueltos.
- `src/Modules/Wms/Modules.Wms.Application/Control/AdjustmentPosting.cs:23-24` — construye claves sin unidad (correcto: los ajustes de WMS-D son siempre sueltos).
- `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/BalanceReconciliation.cs:76-109` — agrupa ledger y saldos por `(tenant, owner, location, variant, lot, status)` y escribe `balance_checks`; `src/Modules/Wms/Modules.Wms.Domain/Control/BalanceCheck.cs` guarda `LotId`.
- No cambian: `LedgerDeltas` (netea por `StockKey`, hereda la unidad), `BalanceChangeQueue` (por variante), `StockProbes` (suman), `StockReadQueries.FindNonZeroBalanceKeysAsync` (la carga inicial sigue siendo suelta), `v_stock_summary` (agrupa por variante), `IdentifierKind.LogisticUnit` (ya existe desde WMS-B).

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Domain/Inventory/StockKey.cs`, `StockMovement.cs`, `StockBalance.cs`, `LedgerEntry.cs` (sólo el comentario: la unidad puede cambiar entre extremos, como el estado), `src/Modules/Wms/Modules.Wms.Domain/Control/BalanceCheck.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/Inventory/ReverseMovement.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/InventoryLedger.cs`, `InventoryLedgerModel.cs`, `BalanceReconciliation.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/HandlingUnitDimensionSql.cs`, `Migrations/<ts>_AddHandlingUnitDimension.cs` (+ `.Designer.cs`, snapshot)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/Inventory/StockKeyComparerTests.cs`, `LedgerEntryRulesTests.cs` (ya cubre `LedgerEntry.Reverse`), `tests/Modules/Wms/Modules.Wms.IntegrationTests/InventoryLedgerMigrationTests.cs`, `InventoryLedgerPostingTests.cs`, `ReverseMovementApiTests.cs`, `BalanceReconciliationTests.cs`

**Interfaces:**
- `public readonly record struct StockKey(Guid OwnerId, Guid LocationId, Guid VariantId, Guid? LotId, StockStatus Status, Guid? HandlingUnitId = null)` — al final y con default para que los llamadores de WMS-D sigan compilando sin cambios.
- `StockKeyComparer`: ubicación, variante, lote (`null` primero), **unidad (`null` primero, `Nullable.Compare`)**, estado, dueño. Spec §6.3.
- `StockMovement`: `FromHandlingUnitId`, `ToHandlingUnitId` (`Guid?`), llenados por `Record` desde `from.HandlingUnitId` y `to.HandlingUnitId`; columnas `from_handling_unit_id`, `to_handling_unit_id`. `StockBalance.HandlingUnitId`; columna `handling_unit_id`. `BalanceCheck.HandlingUnitId`.
- `LedgerEntry` **no** exige la misma unidad en los dos extremos (spec §6.1: recibir dentro de un palet, almacenarlo entero o sacar sueltas cambian la unidad); sigue exigiendo misma variante, dueño y lote.
- Guard interno mientras no exista el agregado: `InventoryLedger.PostAsync` lanza `InvalidOperationException("Handling units are not supported until the HandlingUnit aggregate exists.")` si alguna clave de una entrada **sin** `ReversesMovementId` trae `HandlingUnitId`. No es un código de negocio (ningún endpoint de WMS-D puede producirla) y lo retira el corte que crea las unidades.

- [x] **Step 1: RED — orden y clave en unitarias**
  - `StockKeyComparerTests.NullHandlingUnitSortsBeforeAnyUnitAndTheOrderIsTotal`: cuatro claves que sólo difieren en la unidad (`null`, `U1`, `U2`) y en el estado; el orden directo y el inverso coinciden y la de `null` va primero dentro de la misma `(ubicación, variante, lote)`.
  - `StockKeyComparerTests.KeysThatDifferOnlyInHandlingUnitAreDifferent`: `Compare ≠ 0` y `!=` entre la clave suelta y la misma con `U1`.
  - Reverso: `LedgerEntry.Reverse` de una entrada `(A, Received, null) → (B, Available, U1)` devuelve `(B, Available, U1) → (A, Received, null)`.

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~StockKeyComparerTests|FullyQualifiedName~LedgerEntryRulesTests"
```
Expected: error de compilación (`StockKey` no tiene `HandlingUnitId`).

- [x] **Step 2: GREEN — dominio.** `StockKey` con el parámetro final, comparador, `StockMovement`/`StockBalance`/`BalanceCheck` con sus propiedades. Mismo comando: PASS, y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"` sigue verde (`Inventory` sigue con `Guid` blandos: la unidad tampoco es un tipo fuerte).

- [x] **Step 3: RED — esquema e integración**
  - `InventoryLedgerMigrationTests.TheBalanceKeyIncludesTheHandlingUnitWithNullsNotDistinct`: lee `indexdef` de `pg_indexes` para `UQ_stock_balances_key` y exige `handling_unit_id` entre las columnas y `NULLS NOT DISTINCT`.
  - `InventoryLedgerMigrationTests.MovementsCarryFromAndToHandlingUnit`: `information_schema.columns` de `wms.stock_movements` (y de una partición mensual, que hereda) tiene `from_handling_unit_id` y `to_handling_unit_id` nullable; `wms.v_stock_rows` expone `handling_unit_id`.
  - `InventoryLedgerPostingTests.TheSameKeyLooseAndInsideAUnitAreTwoBalances`: siembra a mano (SQL, como `WmsLedgerSeeding`) un saldo suelto y otro de la misma `(dueño, ubicación, variante, lote, estado)` con `handling_unit_id = U1`; no chocan, y un ajuste posteado por la API (suelto) suma sólo al suelto.
  - `InventoryLedgerPostingTests.APostedBatchWithAHandlingUnitIsRejectedUntilTheAggregateExists`: el guard interno de arriba.
  - `ReverseMovementApiTests.TheReversalRestoresTheHandlingUnitOfEachSide`: sembrado un movimiento `(REC-01, Received, NULL) → (A-01-02, Available, U1)` con su saldo, el reverso por la API compensa `(A-01-02, Available, U1)` y no el saldo suelto de `A-01-02`. Es el único camino de WMS-D que escribe una unidad no nula, porque copia la del original: **el guard exceptúa las entradas con `ReversesMovementId`** (la unidad que traen ya la aceptó el ledger, o la sembró la prueba), y el comentario del guard lo dice.
  - `BalanceReconciliationTests.TwoBalancesThatDifferOnlyInHandlingUnitDoNotDrift`: ledger y saldos sembrados con la misma clave suelta y en `U1`; la reconciliación no escribe `balance_checks`. Y si uno de los dos difiere, la fila de `balance_checks` lleva su `handling_unit_id`.

```powershell
Stop-Process -Name Api -ErrorAction SilentlyContinue
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~InventoryLedgerMigrationTests|FullyQualifiedName~InventoryLedgerPostingTests|FullyQualifiedName~ReverseMovementApiTests|FullyQualifiedName~BalanceReconciliationTests"
```
Expected: FAIL (la columna no existe; `42703` en los sembrados).

- [x] **Step 4: GREEN — migración `AddHandlingUnitDimension`**

```powershell
dotnet ef migrations add AddHandlingUnitDimension --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
```

  - En `InventoryLedgerModel`: `HandlingUnitId` en `StockBalance` (`handling_unit_id`) y en el índice `UQ_stock_balances_key` **entre `LotId` y `Status`**, con `AreNullsDistinct(false)`; `FromHandlingUnitId`/`ToHandlingUnitId` en `StockMovement`; `HandlingUnitId` en `BalanceCheck`. EF genera el `AddColumn` de `stock_balances` y `balance_checks` y el drop/create del índice único (tabla vacía en todo ambiente: no hay datos que reescribir).
  - `stock_movements` es `ExcludeFromMigrations`: sus columnas van por SQL crudo en `HandlingUnitDimensionSql` (nuevo; **no** se edita `InventoryLedgerSql`, que es historia de `AddInventoryLedger`): `ALTER TABLE wms.stock_movements ADD COLUMN IF NOT EXISTS from_handling_unit_id uuid NULL, ADD COLUMN IF NOT EXISTS to_handling_unit_id uuid NULL;` (en una tabla particionada se propaga a todas las particiones, incluida `DEFAULT`, y a las que cree después `ensure_movement_partition`).
  - `CREATE OR REPLACE VIEW wms.v_stock_rows` con `b.handling_unit_id` **al final** de la lista: PostgreSQL sólo deja agregar columnas al final en un `OR REPLACE`, y `v_stock_summary` depende de esta vista.
  - `Down` vacío y comentado, por la misma razón que `AddInventoryLedger` (S11).
  - Sin FKs: `wms.handling_units` no existe todavía; las agrega el corte que la crea (las columnas están en `NULL`, así que validar la FK es inmediato).
  - `InventoryLedger`: `UpsertAsync` inserta `handling_unit_id` y su `ON CONFLICT` lleva `(tenant_id, owner_id, location_id, variant_id, lot_id, handling_unit_id, status)`; `DebitAsync` suma `AND handling_unit_id IS NOT DISTINCT FROM {key.HandlingUnitId}::uuid` en el `UPDATE` y en la relectura; el guard interno al entrar a `PostAsync`.
  - `ReverseMovement` arma las claves con `movement.FromHandlingUnitId`/`ToHandlingUnitId`.
  - `BalanceReconciliation`: `handling_unit_id` en las dos ramas del `UNION ALL` (`to_handling_unit_id` y `from_handling_unit_id`), en los `GROUP BY`, en el `JOIN` con `IS NOT DISTINCT FROM` y en el `INSERT` a `balance_checks`.

  Mismo comando del Step 3: PASS. Además, sin regresiones en lo ya construido:

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~AdjustmentApiTests|FullyQualifiedName~InitialBalanceImportTests|FullyQualifiedName~StockQueryTests|FullyQualifiedName~LedgerImmutabilityTests|FullyQualifiedName~LedgerPartitionTests|FullyQualifiedName~StockProbeTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CatalogVariantUsageCheckerTests"
```
Expected: PASS.

- [x] **Step 5: Docs.** `CLAUDE.md`, en la entrada de la clave de `stock_balances`: la clave incluye `handling_unit_id` (`NULL` = suelta, también `NULLS NOT DISTINCT`) y un `INSERT` a mano lo lleva en `NULL`; `CatalogVariantUsageChecker` no cambia. README: una línea en la sección del ledger.

- [ ] **Step 6: Commit** — `git commit -m "feat(wms): add the handling unit dimension to the balance key and the movement"`.

---

## Self-review

**Cobertura del spec (secciones del alcance):** §5.1 dimensiones y `StockKey` → T1; §5.2 agregados `Lot`, `SerialUnit`, `Reservation`, `StockMovement`/`StockBalance` sin repositorio de escritura → T1/T3/T16; §5.3 fórmulas (`available`, `blocked`, `received`, `in_transit`, suma cero) → T3 (vista) y T7; §5.4 perfil, captura y factor → T2; §6.1/6.2 fila con origen y destino, inmutabilidad y reverso → T1/T3/T9; §6.3 upsert, `CHECK`, orden de claves → T3/T4; §6.4 idempotencia → T6/T8; §6.5 reconciliación → T13; §6.6 (recepción) queda para WMS-E, pero sus primitivas (lote al vuelo, `Received`, `Quarantine`) existen; §7 tabla `reservations` y `reserved` → T3; §11.2 índices, particiones, recibos → T3; §11.3 advisory lock → T5/T8/T9/T10; §11.4 outbox → T8/T11/T12; §16.1 → T2; §19 → `InventoryLedgerTests` repartida en `LedgerBatchTests`/`LedgerDeltasTests`/`LedgerEntryRulesTests` (T2) más `InventoryLedgerPostingTests` (T4); `LedgerImmutabilityTests` T3; `IdempotencyTests` y `ConcurrentAdjustmentTests` T8; `BalanceReconciliationTests` T13; `CatalogVariantUsageCheckerTests` T15; `WmsLayerTests` T16; §20.2 RF-020..025, 029 (parcial), 030 (**parcial**: sólo el export de saldos, T11; el de movimientos queda pendiente, §24.3 no define su ruta) → T4–T13; RF-026/027/028 son fases 2–3 y quedan fuera; §24.1–24.3 → T7–T11; ADR-02/03/04/05/06/08/10 → T1–T6, T14.

**Reconciliación con WMS-B (Task 6 final):** `WmsAuthorization` es una clase scoped inyectada con `IExecutionContext` y `EnsureAsync(ctx, tenantId, permission, ownerId?, warehouseId?, ct)` en T7/T8/T9/T10/T11; `WmsAuthorization.Denied()` estática en el filtro (T6); `IWmsProvisioner.EnsureAsync(tenantId, ct)` antes de `GetSelfAsync`, que lanza si falta (T8/T10); `Location.AllowsNegative` calculada → `LedgerLocationInfo.AllowsNegative` → `stock_balances.allows_negative` (T2/T3/T4); `IOwnerStockProbe` reemplazada junto con las otras dos en T15; `IdentifierKind` sin `Location` (T10 resuelve por código); `wms.member_scopes`/`DbSet<MemberScope>` existen y no se tocan; `WmsErrorCodes` de B trae `OwnerHasStock`, `LocationVirtualImmutable`, `LocationCapacityInvalid`, `IdentifierValueInvalid` y este plan no los redefine; `ForeignKeyViolation.Is(Exception, string)` se reutiliza para las FKs internas (T3/T4), `UniqueViolation` para índices, y `CheckViolation(PostgresException)` es la única pieza nueva, en el ledger (T4); `IWmsAuditRecorder.Record(…, WmsActorType, …, string resourceId, changedFields, …)` con `ownerId` en `changedFields` en T8/T9/T10/T11; `SaveChangesAsync` → `Task<int>` (T4); `IWmsCatalogReader.GetVariantsAsync` → `IReadOnlyList` (T7/T10) y `FindVariantsByCodesAsync` como adición al puerto y al adaptador `WmsCatalogReader(IProductRepository, IOptionDefinitionRepository)` que gana `CatalogDbContext`, con `WmsCatalogReaderTests` como Modify (T10); `IWmsGeographyReader.FindCitiesAsync` no se usa aquí; `WmsCaller.RequireAsync` como `actor_id` (T8/T9/T10); `IOwnerRepository.FindAsync` (T8); `FindLocationByCodeAsync` como adición explícita a `WmsPorts.cs` y `WarehouseRepository.cs` (T10); precisión 0..6 (Global Constraints, T2); harness `partial` de Loans con `OpenAsync`/`CreateManager`/`CodeOfAsync`/`CountAuditEntriesAsync`/`CreateVariantAsync`, `WmsApiHarness.Inventory.cs` con lo que falta (T3) y la única modificación de `WmsApiHarness.cs` en T6 (`UseSetting("Wms:Workers:*")`); `WmsNotFound.Adjustment/Movement/Lot/ImportPreview` en `WmsSupport.cs` (T8/T9/T10); `version` `long` + `WmsVersions.Ensure` (T8/T10); `Inventory` con `Guid` blandos y `Adjustment` en `Control` por `DomainAreasOnlyDependDownwards` (T1/T3).

**Decisiones de la ronda 2 aplicadas:** S1 (T1/T3/T4/T15), S2 (T6/T8/T9/T10: IK en `POST /adjustments`, commit de importación y reverso; SK sólo en `post`; tenant antes del recibo), S4 (T1/T2/T3/T9), S5 (T8/T10), S7 (T10), S10 (T3/T7/T11/T12), S11 (T3/T17), S12 (T2/T4/T8/T14), S13 (T6), S14/S15 (T11), S17 (Global Constraints), S18 (todas las llamadas), S19 (T8), S20 (T2), S21 (T10), S23 (T8), S26 (T3/T4/T6/T8/T9/T10). Corrección posterior del coordinador: el reverso es IK y su código es `wms.movement.already_reversed`.

**Consistencia de tipos:** `StockKey(Guid, Guid, Guid, Guid?, StockStatus)` igual en T1, T2, T4 (`LedgerDeltas.Net`), T8 (`AdjustmentPosting`) y T9; `IInventoryLedger.PostAsync(LedgerBatch, CancellationToken) → LedgerPosting` en T4, T8, T9, T10; `LedgerBatch(Guid, Guid, string, LedgerActor, IReadOnlyList<LedgerEntry>)` en T2/T4/T8/T9/T10; `IOperationReceipts.Record<T>(T)` en T6/T8/T9/T10/T11; `IWmsEventPublisher` con sus tres métodos declarados en T8 y usados en T11/T12; `WmsMetrics.SetBalanceDrift(long)` y `SetPartitionMissing(bool)` definidos en T4 y usados en T13/T14; `StockRowDto` de T7 reutilizado en T11; `IWmsUnitOfWork.BeginTransactionAsync` nace en T4 y `TryLockDocumentAsync` en T5 (sólo documentos; la numeración usa su lock bloqueante en T8); `LedgerVariantRules`/`LedgerLocationInfo(…, AllowsNegative, …)` (T2) los construye `InventoryLedger` (T4) y los reutiliza `InitialBalanceRowRules` (T10); `LedgerEntryRules.Validate(entry, variant, from, to, now)` y `EnsureOccurredAtInWindow` (T2) los llaman T4 y `Adjustment.Propose` (T8); `LedgerEntry.Reverse(original, id, originalOccurredAt, now, note)` (T2) en T9; `IBalanceChangeQueue.EnqueueAsync(IReadOnlyCollection<BalanceChangeKey>)` (T4) lo consume sólo el ledger; `IdempotencyScope.Key` (T6) es la clave del batch en T8/T9/T10; `IVersionedRequest` (T6) lo implementa `AdjustmentVersionRequest` (T8); `StockSummaryRow` (T7) alimenta `/stock/summary` y `SummarizeByVariantAsync` (T12); `ILotRepository.FindAsync(Guid tenantId, Guid ownerId, …)` en T4/T8/T10.

**Ronda de revisión 1 de T10/T11 (rulings del coordinador):** el commit de la carga exige `wms.adjustment.propose` (I-1) y responde `wms.import.invalid_file` con issues (I-2); el archivo exportado se registra como `FileResource` de dueño `WmsExport` en Storage, se descarga por `download-url` y el módulo apagado lo bloquea; la fila vence a las 24 h (`storage.file.expired`) porque el bucket borra `exports/` a los dos días (I-3); los filtros del export viajan en el cuerpo (I-4); el lector mide el zip antes de ClosedXML con tope comprimido de 5 MB, 20 MB descomprimidos y 100 entradas (I-5); un lote con dos fechas de vencimiento es `wms.import.lot_expiry_conflict` (I-6); una clave con saldo es `wms.import.stock_exists`, **pendiente de confirmación del owner** (I-7, P-12); RF-030 queda parcial (I-8).

**Verificación de pasos:** cada tarea tiene comando RED y GREEN con `--filter` por clase; las de integración (T3, T4, T5, T7, T8, T9, T10, T11, T12, T13, T14, T15) llevan `$env:Seed__Enabled = "false"` en línea aparte antes del `dotnet test`; cada tarea cierra con su commit convencional sin atribución.

**Review Focus:** los siete riesgos tienen prueba en T1/T4, T3/T14, T8, T7, T10, T8 (`AnotherTenantReplayingAKeyIs403`) y T4 (`MovingIntoTheReservedPortionFailsWithReservedAndLeavesTheBalance`).

**D-12 (2026-11-03, Task 18):** de la unidad logística, WMS-D lleva sólo la dimensión (`StockKey.HandlingUnitId`, `UQ_stock_balances_key`, `from_`/`to_handling_unit_id`, upsert, débito, reverso, reconciliación, `v_stock_rows`), siempre en `NULL` salvo lo que copie un reverso; el agregado `HandlingUnit`, la tabla, las FKs, la regla «una unidad, una ubicación», los códigos `wms.handling_unit.*` (propuestos en §24.2), las rutas y la etiqueta quedan para WMS-G/H. La regla 1 (unidades mixtas) la ratificó el owner el 2026-11-03: se permiten, y no afecta esta tarea.

**Proporción:** el plan es más corto que el spec; los bloques de código son pruebas, SQL de migración y firmas.
