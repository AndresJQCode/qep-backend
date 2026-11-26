# WMS-B — módulo `Wms`, bodegas y datos maestros (backend) · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El módulo backend `Wms` nace con su esquema, clave de módulo, permisos y los datos maestros de la fase 1: bodegas con su árbol de ubicaciones (virtuales incluidas), propietario `Self`, catálogo de unidades, perfiles logísticos de variante con conversiones e identificadores, y las etiquetas Code 128 de ubicación.

**Architecture:** Un módulo `src/Modules/Wms/*` con las cuatro capas de `Loans`/`Pipeline` y el Domain partido en namespaces (`Inventory`, `Warehousing`, …) que `WmsLayerTests` vigila. Agregados `Warehouse` (+ árbol de `Location`), `Owner`, `UnitOfMeasure`, `VariantLogisticsProfile` (+ `UomConversion`) e `Identifier`. Catalog y Geography entran por puertos de `Modules.Wms.Application` con adaptadores en `Bootstrapper`; el ledger no existe todavía y sus consumidores (saldo de una variante o ubicación) quedan detrás de dos probes que hoy responden `false`. Todo lo que WMS-D va a consumir (`WmsDbContext`, `IWmsUnitOfWork`, `WmsAuthorization`, `IWmsAuditRecorder`, repositorios, enums y códigos) se fija aquí con nombre exacto.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`, `qcode-pdf` (Typst) para etiquetas.

**Spec:** [`docs/superpowers/specs/2026-10-31-wms-arquitectura-design.md`](../specs/2026-10-31-wms-arquitectura-design.md). Es autoridad: §4.1, §5.2, §8, §10, §11, §16 (D-01..D-03 cerradas), §19, §20.1, §24.1–24.3, §25, §26. Lo que el spec deja abierto y este plan decide está en «Decisiones del plan»; el spec se corrige al mergear.

**Fuera de alcance:** el ledger, saldos, idempotencia y trigger (WMS-D); la evaluación de `member_scopes` (fase 3; la tabla nace vacía en `InitialWms`, S18/D-07); el worker de sincronización de `Barcode`/`Code` por polling (§25); el frontend (plan propio desde § Contrato HTTP); `docs/estado.md` (se toca en `develop` al mergear); la entrada de `CLAUDE.md` sobre el trigger de inmutabilidad (WMS-D).

---

## Antes de empezar

- **Los comandos que se le entregan al developer son PowerShell:** sin `&&` (`A; if ($?) { B }`), una línea por comando, `$env:VAR = "valor"` en línea aparte.
- **`Api.exe` detenido** antes de `dotnet build`, `dotnet test` o `dotnet ef`: `Stop-Process -Name Api -ErrorAction SilentlyContinue`. Si corre como `dotnet Api.dll`, buscarlo por línea de comando.
- **Integración exige Docker (Testcontainers)** y, en esta máquina, `$env:Seed__Enabled = "false"` en línea aparte antes del `dotnet test` de un `*.IntegrationTests` (el harness también lo fija, pero el host de otras pruebas no).
- **Por tarea se corre sólo la clase tocada, con `--filter FullyQualifiedName~<Clase>`.** La suite completa agota puertos en Windows y no se corre nunca por tarea; en la Task 15 se corren los proyectos afectados, uno por uno.
- **Worktree y rama:** `../qep-backend-worktrees/wms-b` (hermano del repo, nunca en `/tmp`), rama `feature/wms-b` desde `develop` (`23ae43b8` o posterior). `git branch --show-current` justo antes de cada commit; si dice `main`, frena. El worktree necesita su propio índice de CodeGraph (`gentle-ai codegraph init --cwd <worktree>`), nunca el del checkout principal.
- **El spec llega sin commitear** (`??` en `git status`): entra en el primer commit junto con este plan.
- **Commits:** Conventional Commits en inglés, **sin** `Co-Authored-By` ni atribución de IA aunque el harness la sugiera (regla del repo).
- **`packages.lock.json`:** cada proyecto nuevo trae el suyo y agregar una referencia cambia los de quienes dependen de `Api.csproj` (todos los `*.IntegrationTests`). Tras tocar referencias: `dotnet restore Backend.slnx --force-evaluate` y se commitean **todos** los locks modificados; el `Dockerfile` restaura con `--locked-mode` y muere con `NU1004` si falta uno.
- **Migraciones con el factory de diseño:** `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- **`TreatWarningsAsErrors` + analizadores `10.0-recommended`:** `ArgumentNullException.ThrowIfNull` en todo parámetro de referencia público, colecciones expuestas como `IReadOnlyList`/`IReadOnlyCollection`, `StringComparison.Ordinal` explícito, `CA1873` (argumento costoso en un log) necesita variable local.
- **Comentarios en el código: el por qué, en español, tuteando.** Identificadores, nombres de archivo y mensajes de excepción en inglés. Textos visibles al usuario (sólo `DisplayName`/`Description` de los `PermissionDefinition`) en español colombiano.
- **Las citas al código van por símbolo.** Los pocos números de línea que quedan en este plan (`Backend.slnx`, `Api.csproj`, `QepServiceCollectionExtensions.cs`, `Program.cs`, las pruebas de Tenancy) se verificaron el 2026-11-01 sobre `develop` en `67240839`; en cualquier otro commit se busca el símbolo que acompaña a cada cita, nunca el número.
- Nunca imprimir secretos.

## Global Constraints

- Módulo `Wms`, esquema `wms`, clave `wms`; **última** en `TenantModuleKeys.All`, `Dependencies[Wms] = [Catalog]`, fuera de `DefaultForNewTenants` (§1.1, §11).
- Permisos `wms.<recurso>.<acción>` con `RequiredModules: [ModuleKeys.Wms]`; `admin` los recibe todos, ningún otro rol de sistema (§11.5, RF-100). `WmsAuthorization` llama además a `TenantModuleGuard` (defensa en profundidad, patrón `Loans`) y expone desde ya la firma de S18 con `ownerId`/`warehouseId` opcionales que se ignoran hasta fase 3.
- Rutas bajo `/api/v1/tenants/{tenantId:guid}/wms`; doble capa de tenant; **403 `authorization.denied`, nunca 404**, para otro tenant (§24.1).
- `Modules.Wms.Application` referencia sólo `BuildingBlocks.*` y `Modules.Tenancy.Application`; nada de EF Core, Npgsql ni otro módulo. El Domain no referencia ningún módulo. Ninguna capa referencia Catalog, Geography, Quotations, Suppliers, Storage, Identity ni Audit (salvo Infrastructure → Audit, para la proyección).
- Dentro del Domain: `Inventory` no referencia otro namespace del módulo; `Warehousing` sólo `Inventory`; `Inbound`, `Internal`, `Control`, `Outbound` nunca entre sí (§4.1).
- Sin FKs hacia otros esquemas; FKs internas `ON DELETE RESTRICT`; columnas snake_case a mano; enums como texto con su `CK_*`; `version bigint` como concurrency token → 412 `concurrency.conflict` (§11.2, §11.3).
- Traducción de índices únicos **por nombre de constraint** en `WmsUnitOfWork`, con constantes del `WmsDbContext` (§1.3).
- Paginación `WmsPaging`: default 50, tope 200 (§11.6). Enums por nombre, nunca número. Ids UUID v7 del servidor.
- Auditoría atómica en cada mutación por `IWmsAuditRecorder` (`source = "wms"`), en la misma transacción que el cambio; nunca el `IAuditRecorder` compartido (§1.1, RF-102).
- Ubicaciones virtuales `SUPPLIER`, `CUSTOMER`, `ADJUSTMENT`, `TRANSIT` y la zona `RECEIVING` con el muelle `REC-01` nacen con la bodega y no se borran (RF-003, §8). Las cuatro virtuales son **de solo lectura por `PUT`**: cualquier `PUT` sobre una responde 422 `wms.location.virtual_immutable`; no se desactivan, no se bloquean ni se mueven (S22). No admiten `allows_storage`. `Location.AllowsNegative` es una propiedad calculada (`Role is Supplier or Customer or Adjustment`), sin columna; `TRANSIT` es virtual y **no** admite negativo (S1).
- Un `Owner` `Self` por tenant (§5.2, §10.1, RF-005). Catálogo de UoM por tenant con `UND, KG, G, L, ML, M` sembradas (RF-008). `base_uom_precision` 0..6 (§16.1).
- `tracking` no cambia con saldo > 0 sin documento de identificación: `wms.profile.tracking_change_requires_identification` (RF-010; en este corte el saldo siempre es 0).
- Conversiones sembradas desde `ProductVariant.PackagingUnits` al crear el perfil (ADR-08, RF-007). Perfil por defecto `UND`, precisión 0, `None` (ADR-07).
- `identifiers` único por `(tenant_id, value)` (§25); al crear el perfil se copian `Barcode` como `Variant/is_primary` y `Code` como `Internal`.
- Las respuestas son BFF: la forma la decide la pantalla y el motivo se escribe en el DTO (`WarehouseDto.LocationCount`, `VariantProfileDto.HasStock`, `scannable.etag`).

## Review Focus

1. **`CompositionRootTests` por conteo y por registro.** Un handler no registrado en `QepServiceCollectionExtensions` responde 500 en producción y la prueba `EveryCommandAndQueryHasItsHandlerRegistered` es lo único que lo frena; y el conteo de permisos pasa de 72 a 88 o `PermissionDiscoveryFinds…` se pone roja. Task 3 `TheWmsPermissionsRequireTheWmsModuleAndGoOnlyToAdmin` y el renombre a `…EightyEightConstants`; cada tarea de handlers termina corriendo `CompositionRootTests` entero.
2. **Lock files y el `docker build`.** Seis proyectos nuevos y referencias nuevas en `Api`, `Bootstrapper` y `ArchitectureTests`; en local el `restore` normal regenera sin protestar y el `Dockerfile` con `--locked-mode` muere con `NU1004`. Task 1 regenera con `--force-evaluate` y Task 15 verifica con `dotnet restore Backend.slnx --locked-mode`.
3. **La migración de Tenancy encadena tras `loans`.** El `CHECK` nuevo tiene que partir del que termina en `'loans'` y el `Down` tiene que borrar las filas `wms` antes de reponer el `CHECK` viejo; cinco listas alfabéticas de `SeedStartupTests` y el `15` de `TenantModulesApiTests` cambian. Task 2 `WmsIsLastDependsOnCatalogAndIsOffForNewTenants` y `AddWmsModuleKeyUpAndDownRoundTrip`.
4. **`path` desnormalizado al mover o renombrar un nodo.** Mover un rack bajo otra zona, o cambiar el código de una zona, tiene que reescribir el `path` de todos sus descendientes, y mover un nodo bajo su propio descendiente tiene que fallar; si no, las búsquedas por `path` y el recorrido de picking mienten sin error. Task 4 `MovingANodeRewritesTheDescendantPaths`, `RenamingAZoneRewritesTheDescendantPaths`, `MovingANodeUnderItsOwnDescendantIsRejected`; Task 9 `MovingALocationUpdatesTheChildrenPaths`.
5. **El `etag` de `scannable` es el contrato de cache de la app.** Si no cambia cuando cambia una ubicación (código, estado, padre) la app trabaja con bins viejos; si cambia sin motivo, descarga la bodega entera en cada arranque. Task 9 `TheScannableEtagIsStableUntilALocationChanges` y `IfNoneMatchWithTheCurrentEtagIs304`.

---

## Decisiones del plan (donde el spec deja margen)

| # | Decisión | Por qué |
| --- | --- | --- |
| P-1 | **Los identificadores `Location/CODE128` no se escriben en este corte.** `identifiers` es único por `(tenant_id, value)` (§25) y el código de ubicación es único **por bodega** (§8): la segunda bodega repetiría `SUPPLIER`, `REC-01`, etc. La app resuelve bins contra `scannable` de su bodega (§8). Se registra como corrección pendiente del spec §25. | Invariante irrealizable tal como está escrita. |
| P-2 | `Self` y las seis UoM del sistema se crean **en el primer acceso** del tenant (`IWmsProvisioner.EnsureAsync`, `INSERT … ON CONFLICT DO NOTHING`), como `PipelineProvisioner`: Tenancy no avisa al activar un módulo. **Sólo el provisioner escribe el `Self`**; `IOwnerRepository.GetSelfAsync` lanza `InvalidOperationException` si falta (S5). | §5.2 decía «al activar el módulo»; no hay evento. |
| P-3 | Hay `DELETE /warehouses/{id}/locations/{locationId}` (no está en §24.3): 204 para una hoja no virtual sin saldo; 422 `wms.location.virtual_immutable`, `wms.location.has_children` o `wms.location.has_stock`. | «Las virtuales no se borran» (§8) necesita un camino de borrado que lo pruebe. |
| P-4 | Códigos de bodega y ubicación: `Trim` + mayúsculas, `[A-Z0-9._-]{1,32}`; de UoM `[A-Z0-9]{1,8}`. Inválido → `wms.warehouse.code_invalid`, `wms.location.code_invalid`, `wms.uom.code_invalid`. | Etiqueta Code 128 y cache por código exacto. |
| P-5 | Jerarquía de `kind`: rango `Zone < Aisle < Rack < Level < Bin`, profundidad libre pero siempre descendente; `Virtual` sólo en la raíz; `Dock` en la raíz **o bajo una `Zone`** (el muelle `REC-01` nace bajo `RECEIVING`, así que «sólo en la raíz» era irrealizable); `Bin` y `Dock` sin hijos. `Virtual` y los roles `Transit/Adjustment/Supplier/Customer` no se crean por API (`wms.location.kind_not_allowed`, `wms.location.role_not_allowed`). | §8 fija el orden y la profundidad libre; lo demás es lo mínimo para que el árbol tenga sentido. |
| P-6 | Códigos nuevos además de §24.2: `wms.warehouse.not_found`, `wms.location.not_found`, `wms.owner.not_found`, `wms.warehouse.city_unknown`, `wms.location.parent_invalid`, `wms.location.kind_not_allowed`, `wms.location.virtual_immutable`, `wms.location.has_children`, `wms.location.capacity_invalid`, `wms.owner.self_immutable`, `wms.owner.has_stock`, `wms.uom.code_taken`, `wms.uom.unknown`, `wms.uom.precision_invalid`, `wms.profile.variant_unknown`, `wms.profile.conversion_duplicate`, `wms.profile.conversion_factor_invalid`, `wms.profile.measure_invalid`, `wms.identifier.value_taken`, `wms.identifier.value_invalid`, `wms.label.render_failed`, más los `*_invalid` de P-4. | Se listan para que el spec los incorpore. |
| P-7 | `IWmsAuditRecorder.Record` recibe `WmsActorType { Human, System, ApiKey }` (enum del módulo) y `IReadOnlyDictionary<string, string?> changedFields`. `WmsAuditRecorder` lo escribe con `JsonSerializer.Serialize(changedFields)` en `changed_fields`, que es `jsonb`: el valor por defecto de `AuditEntry.ChangedFieldsJson` es el arreglo `"[]"` y las entradas del Wms llevan siempre un **objeto** (`{"code":…}`), y es el primer recorder que serializa el diccionario él mismo en vez de recibir el string ya armado por el handler, como hacen `GeographyAuditRecorder` y `ShopifyAuditRecorder` (S23). `ApiKey` se mapea a `AuditActorType.Integration`. | Application no puede referenciar `Modules.Audit.Domain`; §10.3 quiere `owner_id` dentro de `changedFields`. |
| P-8 | Perfil sin fila: `GET /variant-profiles` lo devuelve calculado en memoria con `version: 0` y conversiones sembradas desde `PackagingUnits`; el `PUT` con `version: 0` lo crea. `GetOrCreateDefaultAsync` (para WMS-D) sí persiste y audita como `System`. | Un GET no escribe, con **una sola excepción**: los GET de listado llaman a `IWmsProvisioner.EnsureAsync` (precedente `PipelineProvisioner`, S5). El formulario necesita ver el default sin que exista fila. |
| P-11 | **RF-004 (etiquetas Code 128) es fase 1 en el spec y opcional en este plan** (Task 13, al final). Es la única tarea que sale del módulo hacia `qcode-pdf` y nada de WMS-B ni de WMS-D depende de ella; si el tiempo no alcanza, se anota en el handoff como desviación y entra en un corte posterior (S24). | Desacopla el cierre del corte de un servicio externo. |
| P-12 | `WmsOutboxMessage` es una **proyección de escritura sobre `platform.outbox_messages`** con `ExcludeFromMigrations`, copia de `PipelineOutboxRecord`: el módulo no tiene outbox propio ni tabla nueva, y WMS-D publica sus eventos por esa misma proyección sin rediseñarla (S19). | Un outbox por módulo duplicaría el dispatcher de Platform. |
| P-9 | Etiquetas: una por página de 60×30 mm, Code 128 por `@preview/tiaoma` (validado contra `qcode-pdf` el 2026-10-31 con el MCP `validate_typst`: compila). La configuración del servicio se reutiliza (`Quotations:Pdf`) desde un adaptador en Bootstrapper; el módulo no conoce la clave. | Mismo servicio, misma clave, sin secreto nuevo en k8s. |
| P-10 | El harness de integración copia el de **Loans** (contenedor por prueba, `SeedTenantAsync` con módulos), no el de Pipeline (plantilla clonada). | Es el más reciente y el más simple de leer. |

---

## Contrato HTTP

Base `/api/v1/tenants/{tenantId}/wms`. Tenant en la ruta **y** en `X-Tenant-Id`; mutaciones con `X-Qep-Client: web`. Errores `ProblemDetails` con `code`. `T?` viaja como `null`, nunca ausente. Enums por nombre. `version` es `long`.

**Enums:** `LocationKind = Zone|Aisle|Rack|Level|Bin|Dock|Virtual` · `LocationRole = Receiving|Storage|Picking|Shipping|Quarantine|Returns|Transit|Adjustment|Supplier|Customer` · `LocationStatus = Active|Blocked|Counting` · `TrackingPolicy = None|Lot|Serial` · `OwnerKind = Self|Client`.

| Método y ruta | Permiso | OK | Errores propios |
| --- | --- | --- | --- |
| `POST /warehouses` `{ code, name, cityId? }` | `wms.warehouse.manage` | 201 `WarehouseDto` | 422 `wms.warehouse.code_taken`, `wms.warehouse.code_invalid`, `wms.warehouse.city_unknown` |
| `GET /warehouses?includeInactive=` | `wms.document.read` | 200 `{ items: WarehouseDto[] }` | |
| `PUT /warehouses/{id}` `{ name, cityId?, isActive, version }` | `wms.warehouse.manage` | 200 `WarehouseDto` | 404 `wms.warehouse.not_found`, 412 |
| `GET /warehouses/{id}/locations` | `wms.document.read` | 200 `{ items: LocationDto[] }` (plano, con `parentId` y `path`, ordenado por `path`) | 404 |
| `POST /warehouses/{id}/locations` `{ parentId?, code, kind, role, allowsStorage, allowsPicking, maxWeightKg?, maxVolumeM3?, maxUnits?, putawayPriority, storageClasses? }` | `wms.warehouse.manage` | 201 `LocationDto` | 422 `wms.location.code_taken`, `code_invalid`, `parent_invalid`, `kind_not_allowed`, `role_not_allowed` |
| `PUT /warehouses/{id}/locations/{locationId}` `{ …mismo + status, version }` | `wms.warehouse.manage` | 200 `LocationDto` | 404 `wms.location.not_found`, 412, 422 `wms.location.virtual_immutable` (**cualquier** `PUT` sobre una de las cuatro virtuales, aunque no cambie nada: son de solo lectura, S22) y los mismos del `POST`. No hay `isActive`: una ubicación se bloquea (`status`) o se borra |
| `DELETE /warehouses/{id}/locations/{locationId}` | `wms.warehouse.manage` | 204 | 422 `wms.location.virtual_immutable`, `wms.location.has_children`, `wms.location.has_stock` |
| `GET /warehouses/{id}/locations/scannable` (`If-None-Match` opcional) | `wms.document.read` | 200 `{ items: [{ id, code, path, role }], etag }` + header `ETag`; 304 si coincide | |
| `POST /warehouses/{id}/locations/labels` `{ locationIds: uuid[] }` | `wms.warehouse.manage` | 200 `application/pdf` | 422 `wms.location.not_found` (un id ajeno a la bodega), `wms.label.render_failed` |
| `GET /owners` | `wms.owner.manage` | 200 `{ items: OwnerDto[] }` (el `Self` siempre primero) | |
| `POST /owners` `{ name, identification?, contactEmail? }` | `wms.owner.manage` | 201 `OwnerDto` (`kind = Client`) | |
| `PUT /owners/{id}` `{ name, identification?, contactEmail?, isActive, version }` | `wms.owner.manage` | 200 `OwnerDto` | 404 `wms.owner.not_found`, 412, 422 `wms.owner.self_immutable` (desactivar el `Self`), `wms.owner.has_stock` (desactivar un `Client` con saldo; en este corte el probe siempre responde `false`) |
| `GET /uoms` | `wms.profile.manage` | 200 `{ items: [{ code, name, precision, isSystem }] }` | |
| `POST /uoms` `{ code, name, precision }` | `wms.profile.manage` | 201 `UomDto` | 422 `wms.uom.code_taken`, `code_invalid`, `precision_invalid` |
| `GET /variant-profiles?variantIds=a,b,…` (hasta 200) | `wms.profile.manage` | 200 `{ items: VariantProfileDto[] }` (ids desconocidos se omiten) | 422 `validation.failed` si > 200 o vacío |
| `PUT /variant-profiles/{variantId}` `{ baseUom, baseUomPrecision, tracking, tracksExpiry, weightKg?, volumeM3?, lengthCm?, widthCm?, heightCm?, storageClass?, displayUom?, conversions: [{ code, factor, barcode? }], version }` | `wms.profile.manage` | 200 `VariantProfileDto` | 412, 422 `wms.profile.variant_unknown`, `wms.uom.unknown`, `wms.uom.precision_exceeded` (precisión > la de la UoM base), `wms.profile.tracking_change_requires_identification`, `wms.profile.conversion_duplicate`, `wms.profile.conversion_factor_invalid`, `wms.profile.measure_invalid`, `wms.identifier.value_taken` |

**DTOs** (`Modules.Wms.Application/WmsDtos.cs`):

```
WarehouseDto { id, code, name, cityId, cityName, isActive, version, locationCount }   // locationCount: no virtuales
LocationDto  { id, warehouseId, parentId, code, path, kind, role, isVirtual, allowsStorage, allowsPicking,
               maxWeightKg, maxVolumeM3, maxUnits, putawayPriority, storageClasses[], status, version }
ScannableLocationsDto { items: [{ id, code, path, role }], etag }
OwnerDto { id, kind, name, identification, contactEmail, isActive, version }
UomDto { code, name, precision, isSystem }
UomConversionDto { id, code, factor, barcode }
VariantProfileDto { variantId, productId, productName, variantCode, variantName, barcode, baseUom, baseUomPrecision, tracking,
                    tracksExpiry, weightKg, volumeM3, lengthCm, widthCm, heightCm, storageClass, displayUom,
                    conversions: UomConversionDto[], hasStock, version }   // version 0 = sin fila; hasStock bloquea tracking (RF-010)
```

`path` = `{warehouseCode}/{code de cada ancestro}/{code}`: `BOG/RECEIVING/REC-01`, `BOG/SUPPLIER`.

---

### Task 1: Worktree, proyectos y `WmsLayerTests`

Scaffold completo compilable: seis proyectos, registros en solución y referencias, las clases mínimas que las pruebas de arquitectura ancoran, lock files. RF: ninguno (§4.1, §11.1, §19).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Modules.Wms.Domain.csproj` (copia de `src/Modules/Pipeline/Modules.Pipeline.Domain/Modules.Pipeline.Domain.csproj`)
- Create: `src/Modules/Wms/Modules.Wms.Application/Modules.Wms.Application.csproj` (copia del de Pipeline; `InternalsVisibleTo` `Modules.Wms.UnitTests`)
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Modules.Wms.Infrastructure.csproj` (copia del de Pipeline; `InternalsVisibleTo` `Modules.Wms.UnitTests` y `Modules.Wms.IntegrationTests`; más adelante `<EmbeddedResource Include="Pdf\location-label.typ" />`)
- Create: `src/Modules/Wms/Modules.Wms.Api/Modules.Wms.Api.csproj` (copia del de Pipeline)
- Create: `tests/Modules/Wms/Modules.Wms.UnitTests/Modules.Wms.UnitTests.csproj` (copia de `tests/Modules/Loans/Modules.Loans.UnitTests/Modules.Loans.UnitTests.csproj`, con las tres referencias a `Modules.Wms.*`)
- Create: `tests/Modules/Wms/Modules.Wms.IntegrationTests/Modules.Wms.IntegrationTests.csproj` (copia de `tests/Modules/Loans/Modules.Loans.IntegrationTests/Modules.Loans.IntegrationTests.csproj`)
- Create: `src/Modules/Wms/Modules.Wms.Domain/WmsErrorCodes.cs`, `WmsDomainException.cs`
- Create: `src/Modules/Wms/Modules.Wms.Application/WmsPermissions.cs` (**vacía** en esta tarea: es sólo el ancla de `WmsLayerTests`; las 16 constantes llegan en Task 3 junto con sus `PermissionDefinition`, porque `CompositionRootTests` exige definición y política por cada constante que encuentra)
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs`, `WmsDatabaseInitializer.cs`, `Persistence/WmsDbContext.cs`, `Persistence/WmsDbContextFactory.cs`
- Create: `src/Modules/Wms/Modules.Wms.Api/WmsEndpoints.cs`
- Create: `tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs`
- Modify: `Backend.slnx` (dos carpetas después de las de Loans, líneas 156-165), `src/Api/Api.csproj` (Api + Infrastructure, junto a Loans líneas 21-22), `src/Bootstrapper/Bootstrapper.csproj` (Application + Infrastructure, junto a Loans líneas 20-21), `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` (las cuatro capas, junto a Loans líneas 42-45)

**Interfaces:**
- Produces: `Modules.Wms.Domain.WmsDomainException(string code, string message)` y `(string code, string message, Exception inner)`, hereda `BuildingBlocks.Domain.DomainException`. `WmsErrorCodes` (`public static class`) con todas las constantes de §24.2 que aplican a este corte y las de P-6, valor = el código literal (`public const string WarehouseCodeTaken = "wms.warehouse.code_taken";` etc.). Nombres que las tareas siguientes citan por símbolo: `WarehouseNotFound`, `WarehouseCodeTaken`, `WarehouseCodeInvalid`, `WarehouseCityUnknown`, `LocationNotFound`, `LocationCodeTaken`, `LocationCodeInvalid`, `LocationParentInvalid`, `LocationKindNotAllowed`, `LocationRoleNotAllowed`, `LocationVirtualImmutable` (S22), `LocationHasChildren`, `LocationHasStock`, `LocationCapacityInvalid`, `OwnerNotFound`, `OwnerSelfImmutable`, `OwnerSelfExists`, `OwnerHasStock`, `UomCodeTaken`, `UomCodeInvalid`, `UomUnknown`, `UomPrecisionInvalid`, `UomPrecisionExceeded`, `ProfileVariantUnknown`, `ProfileTrackingChangeRequiresIdentification`, `ProfileConversionDuplicate`, `ProfileConversionFactorInvalid`, `ProfileMeasureInvalid`, `IdentifierValueTaken`, `IdentifierValueInvalid`, `LabelRenderFailed`.
- Produces: `WmsDbContext(DbContextOptions<WmsDbContext>) : DbContext`, `public const string Schema = "wms"`; por ahora sólo `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)` y la proyección `ConfigureOutboxProjection` de `WmsOutboxMessage` (copia de `PipelineOutboxRecord`: `Id`, `EventName`, `PayloadJson`, `OccurredAt`, tabla `outbox_messages` de `platform`, `ExcludeFromMigrations`). `internal DbSet<AuditEntry> AuditEntries`, `internal DbSet<WmsOutboxMessage> Outbox`.
- Produces: `WmsDbContextFactory : IDesignTimeDbContextFactory<WmsDbContext>` (copia de `LoansDbContextFactory`, esquema `wms`); `WmsDatabaseInitializer.InitializeWmsDatabaseAsync(this IServiceProvider, CancellationToken)`; `WmsInfrastructureExtensions.AddWmsInfrastructure(this IServiceCollection, IConfiguration)` que registra sólo el `DbContext` (lo demás llega en Task 6); `WmsEndpoints.MapWmsEndpoints(this IEndpointRouteBuilder)` que crea `MapGroup("/api/v1/tenants/{tenantId:guid}/wms").WithTags("Wms")` sin rutas todavía.

- [ ] **Step 1: Crear el worktree y la rama**

```powershell
git -C C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend worktree add ..\qep-backend-worktrees\wms-b -b feature/wms-b develop
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\wms-b
git branch --show-current
```

Esperado: `feature/wms-b`. Copiar al worktree el spec y este plan (en el checkout principal están sin trackear) y commitear: `docs(wms): add architecture spec and WMS-B plan`.

- [ ] **Step 2: Escribir `WmsLayerTests` (RED)**

Copia de `tests/ArchitectureTests/ArchitectureTests/PipelineLayerTests.cs` con las anclas `typeof(WmsErrorCodes)`, `typeof(WmsPermissions)`, `typeof(WmsInfrastructureExtensions)`, `typeof(WmsEndpoints)` y estas pruebas además de las seis heredadas:

```csharp
[Theory]
[InlineData("Modules.Catalog")]
[InlineData("Modules.Geography")]
[InlineData("Modules.Quotations")]
[InlineData("Modules.Suppliers")]
[InlineData("Modules.Storage")]
[InlineData("Modules.Identity")]
public void NoLayerReferencesTheModulesBehindThePorts(string forbiddenPrefix) { /* las cuatro capas, como Pipeline */ }

[Fact]
public void ApplicationDoesNotReferenceAudit() =>
    Assert.DoesNotContain(ReferenceNamesOf(Application), name => name is not null && name.StartsWith("Modules.Audit", StringComparison.Ordinal));

// §4.1: la regla interna del Domain. Mira tipos de campos, propiedades, parámetros, retornos, bases e interfaces
// (no cuerpos de método): es lo que decide si un namespace «conoce» a otro.
[Theory]
[InlineData("Inventory", new string[] { })]
[InlineData("Warehousing", new[] { "Inventory" })]
[InlineData("Inbound", new[] { "Inventory", "Warehousing" })]
[InlineData("Internal", new[] { "Inventory", "Warehousing" })]
[InlineData("Control", new[] { "Inventory", "Warehousing" })]
[InlineData("Outbound", new[] { "Inventory", "Warehousing" })]
public void DomainAreasOnlyDependDownwards(string area, string[] allowed)
{
    var areas = new[] { "Inventory", "Warehousing", "Inbound", "Internal", "Control", "Outbound" };
    var forbidden = areas.Where(other => other != area && !allowed.Contains(other)).Select(other => $"Modules.Wms.Domain.{other}").ToArray();
    foreach (var type in Domain.GetTypes().Where(type => type.Namespace == $"Modules.Wms.Domain.{area}"))
    {
        Assert.DoesNotContain(ReferencedTypesOf(type), referenced => forbidden.Contains(referenced.Namespace));
    }
}
```

`ReferencedTypesOf(Type)` recorre `BaseType`, `GetInterfaces()`, campos, propiedades, parámetros y retornos de métodos/constructores (`BindingFlags` públicos y no públicos, `DeclaredOnly`), expandiendo argumentos genéricos y elementos de arreglo. La prueba de `Inventory` pasa vacía al principio; se vuelve significativa en Task 4.

- [ ] **Step 3: Correr `WmsLayerTests` y ver que no compila**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"
```

Esperado: error de compilación (`Modules.Wms.*` no existe).

- [ ] **Step 4: Crear los seis proyectos y las clases ancla; registrar referencias**

`WmsErrorCodes`, `WmsDomainException`, `WmsPermissions` (clase estática vacía), `WmsDbContext` con las dos proyecciones, `WmsDbContextFactory`, `WmsDatabaseInitializer`, `WmsInfrastructureExtensions`, `WmsEndpoints`. `Backend.slnx`, `Api.csproj`, `Bootstrapper.csproj`, `ArchitectureTests.csproj`.

- [ ] **Step 5: Regenerar lock files y compilar**

```powershell
dotnet restore Backend.slnx --force-evaluate
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Error(s)`. `git status` muestra `packages.lock.json` nuevos (6) y modificados (Api, Bootstrapper, ArchitectureTests y cada `*.IntegrationTests`).

- [ ] **Step 6: Correr `WmsLayerTests` (GREEN)**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"
```

Esperado: todas pasan.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat(wms): scaffold the Wms module projects and layer tests"
```

---

### Task 2: Clave de módulo `wms` en Tenancy

RF: §1.1, §11 (clave fuera de `DefaultForNewTenants`, dependencia `catalog`).

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (después de `Loans`, línea 77; `All` línea 79-80; `Dependencies` línea 87+)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:120-122` (el `CHECK`)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddWmsModuleKey.cs` (+ Designer y snapshot, generados)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs:13,95-100`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/OperatorReadHandlerTests.cs:68`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:105,107,113,151`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs:144,177,239,266,346`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/OperatorConsoleApiTests.cs:347`

**Interfaces:**
- Produces: `TenantModuleKeys.Wms` (`Define("wms")`), última en `All`; `DependenciesOf(Wms) == [Catalog]`; `DefaultForNewTenants` sin cambios.

- [ ] **Step 1: Ajustar las pruebas unitarias (RED)**

En `TenantModuleKeysTests`: agregar `"wms"` al final de la lista de `AllKeepsTheContractOrder`; quitar la aserción `All[^1]` de `LoansIsLastDependsOnCustomersAndIsOffForNewTenants` (renombrar a `LoansDependsOnCustomersAndIsOffForNewTenants`) y agregar:

```csharp
// Spec WMS 2026-10-31 §1.1: inventario de variantes del catálogo; se prende por tenant.
[Fact]
public void WmsIsLastDependsOnCatalogAndIsOffForNewTenants()
{
    Assert.Equal("wms", TenantModuleKeys.All[^1].Value);
    Assert.Equal([TenantModuleKeys.Catalog], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Wms));
    Assert.DoesNotContain(TenantModuleKeys.Wms, TenantModuleKeys.DefaultForNewTenants);
}
```

`OperatorReadHandlerTests:68`: `"wms"` al final.

- [ ] **Step 2: Correr y ver RED**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"
```

Esperado: falla por `TenantModuleKeys.Wms` inexistente.

- [ ] **Step 3: Agregar la clave, la dependencia y el `CHECK`**

`TenantModuleKey.cs`: `public static readonly TenantModuleKey Wms = TenantModuleKey.Define("wms");` con `<summary>` citando el spec; `All = [..., Loans, Wms]`; `[Wms] = [Catalog]`. `TenancyDbContext.cs:122`: el `IN (...)` termina en `'loans','wms'`.

- [ ] **Step 4: Generar la migración**

```powershell
Stop-Process -Name Api -ErrorAction SilentlyContinue
dotnet ef migrations add AddWmsModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Reescribir `Up`/`Down` a mano con la forma exacta de `20261031235621_AddLoansModuleKey.cs`: `Up` = `DropCheckConstraint` + `AddCheckConstraint` con la lista que termina en `'loans','wms'`; `Down` = `DELETE FROM tenancy.tenant_modules WHERE module_key = 'wms';` + `Drop` + `Add` con la lista que termina en `'loans'`. Verificar con `git diff -- src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs` que el snapshot sólo cambia el `CHECK`; cualquier otra diferencia es un modelo desincronizado que hay que entender antes de seguir.

- [ ] **Step 5: Ajustar las pruebas de integración de Tenancy**

`TenantModulesApiTests`: `"wms"` al final de la lista ordenada (105); `"wms"` en los dos `is not (...)`/`is (...)` de módulos no por defecto (107, 113); `Assert.Equal(16, modules.Modules.Count)` (151). `SeedStartupTests`: `"wms"` al final de las cinco listas alfabéticas (va después de `"suppliers"`). `OperatorConsoleApiTests:347`: `"wms"` en el `is not (...)`.

- [ ] **Step 6: Correr unitarias e integración de Tenancy (GREEN)**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~SeedStartupTests|FullyQualifiedName~OperatorConsoleApiTests"
```

Esperado: todas pasan (la integración migra desde cero y ejercita el `Up`).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat(tenancy): add the wms module key"
```

---

### Task 3: Permisos `wms.*`, políticas, rol `admin` y `CompositionRootTests`

RF-100. Se registran los 16 permisos de §11.5 aunque sus casos de uso lleguen en cortes posteriores: `CompositionRootTests` exige política por cada constante.

**Files:**
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsPermissions.cs` (las 16 constantes)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — usings (junto a `using Modules.Loans.Infrastructure;` línea 59 y el alias `LoansPermissions` línea 65: agregar `using WmsPermissions = Modules.Wms.Application.WmsPermissions;`), `AddWmsInfrastructure(configuration)` después de `AddLoansInfrastructure` (línea 1068), 16 `PermissionDefinition` después del bloque de Loans (termina línea 2214), 16 entradas en el rol `admin` después de `ApiAccessPermissions.ApiKeyManage` (línea 1555, mover la coma), 16 `.AddPolicy` después del de `ApiAccessPermissions.ApiKeyManage` (líneas 2604-2606, mover el `;`)
- Modify: `src/Api/Program.cs` — `using Modules.Wms.Api; using Modules.Wms.Infrastructure;` (junto a 48-49); `app.MapWmsEndpoints();` después de `app.MapLoansEndpoints();` (269); `await app.Services.InitializeWmsDatabaseAsync(app.Lifetime.ApplicationStopping);` después de `InitializeApiAccessDatabaseAsync` (353-354) y antes de `RunQepSeedAsync` (358), con el comentario «después de Audit (escribe en audit.entries). Sin FKs a otros esquemas»
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:177-198,573-589`

**Interfaces:**
- Produces: `WmsPermissions` (`public static class`, sólo `const string`): `WarehouseManage = "wms.warehouse.manage"`, `OwnerManage = "wms.owner.manage"`, `ProfileManage = "wms.profile.manage"`, `DocumentRead = "wms.document.read"`, `StockRead = "wms.stock.read"`, `StockExport = "wms.stock.export"`, `AdjustmentPropose = "wms.adjustment.propose"`, `AdjustmentApprove = "wms.adjustment.approve"`, `PurchaseOrderManage = "wms.purchase_order.manage"`, `TransferManage = "wms.transfer.manage"`, `TransferDispatch = "wms.transfer.dispatch"`, `ReceiptPerform = "wms.receipt.perform"`, `PutawayPerform = "wms.putaway.perform"`, `DiscrepancyResolve = "wms.discrepancy.resolve"`, `CountPerform = "wms.count.perform"`, `CountApprove = "wms.count.approve"`.
- `PermissionDefinition(permission, displayName, description, "Bodegas", riskLevel, RequiredModules: [ModuleKeys.Wms])`. `riskLevel`: `"low"` los `*.read`; `"high"` `adjustment.approve`, `count.approve`, `discrepancy.resolve`, `stock.export`; `"medium"` el resto. Textos en español colombiano tuteando no aplica (son descripciones, p. ej. `"Ver bodegas y documentos de inventario"` / `"Permite consultar bodegas, ubicaciones, documentos y tareas del inventario."`).

- [ ] **Step 1: Ajustar `CompositionRootTests` (RED)**

Renombrar `PermissionDiscoveryFindsTheSeventyThreeConstants` → `PermissionDiscoveryFindsTheEightyNineConstants`, `Assert.Equal(89, …)`, y sumar al comentario y al `<summary>` «+ los dieciséis `wms.*` del spec WMS 2026-10-31 §11.5». Agregar, después de `TheLoansPermissionsRequireTheLoansModuleAndGoToAdminAndBilling`:

```csharp
/// <summary>Spec WMS 2026-10-31 §11.5: dieciséis permisos con [wms] (la máscara los quita con el módulo apagado), categoría
/// Bodegas, todos en admin y en ningún otro rol de sistema: el resto es rol custom del tenant.</summary>
[Fact]
public void TheWmsPermissionsRequireTheWmsModuleAndGoOnlyToAdmin()
{
    using var provider = BuildPlatformServices().BuildServiceProvider();
    var catalog = provider.GetRequiredService<IRoleCatalog>();
    var definitions = RegisteredPermissionDefinitions().ToDictionary(definition => definition.Permission, StringComparer.Ordinal);
    var permissions = typeof(WmsPermissions).GetFields().Select(field => (string)field.GetRawConstantValue()!).ToArray();

    Assert.Equal(16, permissions.Length);
    foreach (var permission in permissions)
    {
        Assert.Equal("Bodegas", definitions[permission].Category);
        Assert.Equal([ModuleKeys.Wms], definitions[permission].RequiredModules!.ToArray());
        Assert.Contains(permission, catalog.PermissionsFor("admin"));
        foreach (var role in new[] { "advisor", "cashier", "billing" })
        {
            Assert.DoesNotContain(permission, catalog.PermissionsFor(role));
        }
    }
}
```

- [ ] **Step 2: Correr y ver RED**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: `PermissionDiscoveryFindsTheEightyNineConstants` (cuenta 73) y `TheWmsPermissions…` (0 constantes) fallan.

- [ ] **Step 3: Escribir las 16 constantes y registrar definiciones, políticas, rol admin, infraestructura y `Program.cs`**

- [ ] **Step 4: Correr `CompositionRootTests` entero (GREEN)**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: todas pasan (también `TheContainerValidatesAndBuilds…`, que ahora resuelve `WmsDbContext`).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(wms): register the wms permissions, policies and module wiring"
```

---

### Task 4: Dominio `Warehousing` — `Warehouse` y el árbol de `Location`

RF-001, RF-002, RF-003, §8, §5.2 (invariantes del agregado `Warehouse`).

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Warehousing/WarehouseId.cs`, `LocationId.cs`, `LocationKind.cs`, `LocationRole.cs`, `LocationStatus.cs`, `LocationCapacity.cs`, `Location.cs`, `Warehouse.cs`, `LocationCodes.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/WarehouseTests.cs`, `LocationTreeTests.cs`

**Interfaces:**
- Produces: `readonly record struct WarehouseId(Guid Value) { static New() => new(Guid.CreateVersion7()); }` y `LocationId` igual.
- `enum LocationKind { Zone, Aisle, Rack, Level, Bin, Dock, Virtual }`, `enum LocationRole { Receiving, Storage, Picking, Shipping, Quarantine, Returns, Transit, Adjustment, Supplier, Customer }`, `enum LocationStatus { Active, Blocked, Counting }`.
- `readonly record struct LocationCapacity(decimal? MaxWeightKg, decimal? MaxVolumeM3, int? MaxUnits)`; negativos → `wms.location.capacity_invalid` (agregar a `WmsErrorCodes`; sumarlo a P-6).
- `static class LocationCodes { const string Supplier = "SUPPLIER", Customer = "CUSTOMER", Adjustment = "ADJUSTMENT", Transit = "TRANSIT", ReceivingZone = "RECEIVING", ReceivingDock = "REC-01"; static string Normalize(string code, string invalidCode) /* P-4 */; static bool IsVirtualRole(LocationRole) }`.
- `sealed class Location` con `Id`, `TenantId`, `WarehouseId`, `ParentId: LocationId?`, `Code`, `Path`, `Kind`, `Role`, `IsVirtual`, `AllowsStorage`, `AllowsPicking`, `Capacity`, `PutawayPriority`, `StorageClasses: IReadOnlyList<string>`, `Status`, `Version: long`, `CreatedAt`, `UpdatedAt`; setters privados, mutación sólo desde `Warehouse`. Más la propiedad **calculada, sin columna** `public bool AllowsNegative => Role is LocationRole.Supplier or LocationRole.Customer or LocationRole.Adjustment;` (S1): es lo que WMS-D copia a `stock_balances.allows_negative` en el upsert. `TRANSIT` es virtual y da `false`. EF la ignora sola (no tiene setter); si el mapeo la toma, `Ignore(location => location.AllowsNegative)`.
- `sealed class Warehouse`: `const int CodeMaxLength = 32, NameMaxLength = 120`; `Id`, `TenantId`, `Code`, `Name`, `CityId: Guid?`, `IsActive`, `Version`, `CreatedAt`, `UpdatedAt`, `Locations: IReadOnlyList<Location>`.
  - `static Warehouse Create(WarehouseId id, Guid tenantId, string code, string name, Guid? cityId, DateTimeOffset now)` — crea las cuatro virtuales (`Kind = Virtual`, `IsVirtual = true`, `AllowsStorage = false`, `AllowsPicking = false`, `Role` = su rol) y la zona `RECEIVING` (`Zone`, `Receiving`) con el muelle `REC-01` (`Dock`, `Receiving`, hijo de la zona, `AllowsStorage = true`).
  - `void Update(string name, Guid? cityId, bool isActive, DateTimeOffset now)` — `Version++`.
  - `Location AddLocation(LocationId id, LocationId? parentId, string code, LocationKind kind, LocationRole role, bool allowsStorage, bool allowsPicking, LocationCapacity capacity, int putawayPriority, IReadOnlyList<string> storageClasses, DateTimeOffset now)`.
  - `void UpdateLocation(LocationId id, LocationId? parentId, string code, LocationKind kind, LocationRole role, bool allowsStorage, bool allowsPicking, LocationCapacity capacity, int putawayPriority, IReadOnlyList<string> storageClasses, LocationStatus status, DateTimeOffset now)` — si el nodo es virtual lanza `wms.location.virtual_immutable` **antes de comparar nada** (S22: las virtuales son de solo lectura por `PUT`, también con un cuerpo idéntico al actual); en las demás recalcula `Path` del nodo y de sus descendientes si cambió `parentId` o `code`; `Version++` del nodo (los descendientes sólo cambian `Path`, no `Version`).
  - `void RemoveLocation(LocationId id)` — virtual → `wms.location.virtual_immutable`; con hijos → `wms.location.has_children`; el saldo lo verifica el handler.
  - `Location VirtualLocation(LocationRole role)` — la virtual de ese rol (lanza `InvalidOperationException` si el rol no es virtual).
  - `Location? FindLocation(LocationId id)`.
- Reglas de `AddLocation`/`UpdateLocation`: `Kind == Virtual` → `wms.location.kind_not_allowed`; `LocationCodes.IsVirtualRole(role)` → `wms.location.role_not_allowed`; código duplicado en la bodega (ordinal, tras normalizar) → `wms.location.code_taken` (el índice único también lo frena); `parentId` inexistente, igual a sí mismo, descendiente del nodo, o un `Bin`/`Dock` → `wms.location.parent_invalid`; `kind` ∈ {`Dock`} con padre, o `kind` ∈ {`Aisle`,`Rack`,`Level`,`Bin`} sin padre, o rango del padre ≥ rango del hijo → `wms.location.parent_invalid` (P-5); `allowsStorage` o `allowsPicking` en una `Zone` se aceptan (una `Zone > Bin` chica cabe); `putawayPriority < 0` → `wms.location.capacity_invalid`.

- [ ] **Step 1: Escribir `WarehouseTests` (RED)**

```csharp
[Fact] public void CreatingAWarehouseCreatesTheFourVirtualsAndTheReceivingDock()
// Locations: SUPPLIER, CUSTOMER, ADJUSTMENT, TRANSIT (IsVirtual, Kind Virtual, AllowsStorage false, Path "BOG/SUPPLIER"…),
// RECEIVING (Zone, Receiving, Path "BOG/RECEIVING"), REC-01 (Dock, Receiving, ParentId = RECEIVING, Path "BOG/RECEIVING/REC-01", AllowsStorage true)
[Fact] public void TheCodeIsNormalizedToUpperCaseAndValidated()   // " bog " → "BOG"; "bo g" → wms.warehouse.code_invalid
[Fact] public void RemovingAVirtualLocationIsRejected()            // wms.location.virtual_immutable
[Fact] public void RemovingTheReceivingZoneWithItsDockIsRejected() // wms.location.has_children
[Fact] public void RemovingAnEmptyBinWorks()
[Fact] public void UpdateBumpsTheVersion()
```

- [ ] **Step 2: Escribir `LocationTreeTests` (RED)**

```csharp
[Fact] public void AZoneAisleRackLevelBinChainBuildsThePath()           // "BOG/A/01/02/03/B1"
[Fact] public void AZoneBinChainIsAllowed()                             // profundidad libre
[Fact] public void AKindAboveItsParentIsRejected()                      // Rack bajo Bin → parent_invalid
[Fact] public void ADockOrAVirtualCannotHaveChildren()                  // Bin bajo REC-01 → parent_invalid
[Fact] public void AVirtualKindCannotBeCreatedThroughTheApi()           // kind_not_allowed
[Fact] public void AVirtualRoleCannotBeCreatedThroughTheApi()           // Transit → role_not_allowed
[Fact] public void ADuplicateCodeInTheWarehouseIsRejected()             // "a-01" y "A-01" → code_taken
[Fact] public void MovingANodeRewritesTheDescendantPaths()              // rack 01 de zona A a zona B → bins "BOG/B/01/…"
[Fact] public void RenamingAZoneRewritesTheDescendantPaths()
[Fact] public void MovingANodeUnderItsOwnDescendantIsRejected()         // parent_invalid
[Fact] public void UpdatingAVirtualLocationIsRejectedEvenWithoutChanges() // UpdateLocation(TRANSIT, mismos valores) → virtual_immutable; status Blocked → virtual_immutable (S22)
[Fact] public void OnlySupplierCustomerAndAdjustmentAllowNegative()       // AllowsNegative true en las tres; false en TRANSIT, RECEIVING, REC-01 y un bin (S1)
[Fact] public void NegativeCapacityIsRejected()                         // capacity_invalid
```

- [ ] **Step 3: Correr y ver RED**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~WarehouseTests|FullyQualifiedName~LocationTreeTests"
```

- [ ] **Step 4: Implementar el agregado**

`Path` se recalcula con un recorrido desde el nodo cambiado (BFS por `ParentId`) sobre la lista en memoria: las tablas son chicas (§11.2) y el agregado carga la bodega entera.

- [ ] **Step 5: GREEN y `WmsLayerTests`**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~WarehouseTests|FullyQualifiedName~LocationTreeTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"
```

- [ ] **Step 6: Commit** — `feat(wms): add the Warehouse aggregate with its location tree`

---

### Task 5: Dominio — `Owner`, `UnitOfMeasure`, `VariantLogisticsProfile`, `UomConversion`, `Identifier`

RF-005, RF-006, RF-007, RF-008, RF-009 (variante), RF-010, §16.1, §25.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Domain/Inventory/UomCode.cs`, `Inventory/TrackingPolicy.cs`, `Inventory/UnitOfMeasure.cs`
- Create: `src/Modules/Wms/Modules.Wms.Domain/Warehousing/OwnerId.cs`, `OwnerKind.cs`, `Owner.cs`, `VariantLogisticsProfile.cs`, `UomConversion.cs`, `ProfileChanges.cs`, `Identifier.cs`, `IdentifierKind.cs`, `IdentifierSymbology.cs`
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/OwnerTests.cs`, `UnitOfMeasureTests.cs`, `VariantLogisticsProfileTests.cs`

**Interfaces:**
- `readonly record struct UomCode(string Value)`: `static UomCode Parse(string raw)` (P-4, `wms.uom.code_invalid`), `ToString() => Value`. `enum TrackingPolicy { None, Lot, Serial }`.
- `sealed class UnitOfMeasure { Guid TenantId; UomCode Code; string Name; int Precision; bool IsSystem; DateTimeOffset CreatedAt; static Create(Guid tenantId, UomCode code, string name, int precision, bool isSystem, DateTimeOffset now); static IReadOnlyList<UnitOfMeasure> SystemDefaults(Guid tenantId, DateTimeOffset now) /* UND 0 "Unidad", KG 3 "Kilogramo", G 0 "Gramo", L 3 "Litro", ML 0 "Mililitro", M 2 "Metro" */; const int MinPrecision = 0, MaxPrecision = 6, NameMaxLength = 40 }`; precisión fuera de rango → `wms.uom.precision_invalid`.
- `readonly record struct OwnerId(Guid Value)`, `enum OwnerKind { Self, Client }`.
- `sealed class Owner { Id, TenantId, Kind, Name, Identification: string?, ContactEmail: string?, IsActive, Version, CreatedAt, UpdatedAt; const int NameMaxLength = 120, IdentificationMaxLength = 40; static CreateSelf(OwnerId, Guid tenantId, string name, DateTimeOffset now); static CreateClient(OwnerId, Guid tenantId, string name, string? identification, string? contactEmail, DateTimeOffset now); void Update(string name, string? identification, string? contactEmail, bool isActive, DateTimeOffset now) }` — `Self` con `isActive = false` → `wms.owner.self_immutable`.
- `sealed class UomConversion { Guid Id; string Code; decimal Factor; string? Barcode; const int CodeMaxLength = 32 }` (entidad hija, creada por el perfil).
- `sealed record ProfileChanges(UomCode BaseUom, int BaseUomPrecision, TrackingPolicy Tracking, bool TracksExpiry, decimal? WeightKg, decimal? VolumeM3, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, string? StorageClass, UomCode? DisplayUom, IReadOnlyList<(string Code, decimal Factor, string? Barcode)> Conversions)`.
- `sealed class VariantLogisticsProfile { Guid TenantId; Guid VariantId; Guid ProductId; UomCode BaseUom; int BaseUomPrecision; TrackingPolicy Tracking; bool TracksExpiry; decimal? WeightKg, VolumeM3, LengthCm, WidthCm, HeightCm; string? StorageClass; UomCode? DisplayUom; IReadOnlyList<UomConversion> Conversions; long Version; CreatedAt; UpdatedAt; static CreateDefault(Guid tenantId, Guid variantId, Guid productId, IReadOnlyList<int> packagingUnits, DateTimeOffset now) /* UND, 0, None; una conversión "EMPAQUE x{n}" con Factor n por cada PackagingUnit */; static Create(Guid tenantId, Guid variantId, Guid productId, ProfileChanges changes, DateTimeOffset now); void Update(ProfileChanges changes, bool hasStock, DateTimeOffset now) }`.
  - Reglas: `Tracking` cambia con `hasStock` → `wms.profile.tracking_change_requires_identification`; `BaseUomPrecision` fuera de 0..6 → `wms.uom.precision_invalid`; medidas negativas → `wms.profile.measure_invalid`; `Factor <= 0` → `wms.profile.conversion_factor_invalid`; códigos de conversión repetidos (ordinal, tras `Trim`+mayúsculas) → `wms.profile.conversion_duplicate`. La comprobación «precisión del perfil ≤ precisión de la UoM» (`wms.uom.precision_exceeded`) la hace el handler, que tiene el catálogo.
- `enum IdentifierKind { Variant, Presentation, Lot, LogisticUnit }` (sin `Location`: el código de ubicación es único por bodega y la app lo resuelve contra `scannable`, S7/P-1; `CK_identifiers_kind` lista los mismos cuatro), `enum IdentifierSymbology { Ean13, Ean8, Upc, Code128, Qr, Gtin, Sscc, Internal }`.
- `sealed class Identifier { Guid Id; Guid TenantId; IdentifierKind Kind; IdentifierSymbology Symbology; string Value; Guid TargetId; Guid? ConversionId; bool IsPrimary; DateTimeOffset CreatedAt; static Create(Guid id, Guid tenantId, IdentifierKind kind, IdentifierSymbology symbology, string value, Guid targetId, Guid? conversionId, bool isPrimary, DateTimeOffset now); static IReadOnlyList<Identifier> ForVariant(Guid tenantId, Guid variantId, string code, string? barcode, DateTimeOffset now) /* Internal(code) + Variant/Gtin(barcode, IsPrimary) si hay barcode */ }`. Valor vacío → `wms.identifier.value_invalid` (sumar a P-6).

- [ ] **Step 1: Escribir las pruebas (RED)**

```csharp
// OwnerTests
[Fact] public void TheSelfOwnerCannotBeDeactivated()            // self_immutable
[Fact] public void AClientOwnerCanBeDeactivated()
// UnitOfMeasureTests
[Fact] public void TheSystemDefaultsAreTheSixOfTheSpec()        // UND,KG,G,L,ML,M con 0,3,0,3,0,2 e IsSystem
[Fact] public void PrecisionOutsideZeroToSixIsRejected()         // 7 y -1 → precision_invalid
[Fact] public void TheCodeIsNormalizedAndValidated()            // "und" → UND; "U N" → code_invalid
// VariantLogisticsProfileTests
[Fact] public void TheDefaultProfileIsUndNoneWithConversionsFromPackagingUnits()  // [12, 144] → "EMPAQUE x12"=12, "EMPAQUE x144"=144
[Fact] public void ChangingTrackingWithStockRequiresIdentification()            // hasStock true → tracking_change_requires_identification
[Fact] public void ChangingTrackingWithoutStockIsAllowed()
[Fact] public void DuplicateConversionCodesAreRejected()                         // "caja" y "CAJA"
[Fact] public void ANonPositiveFactorIsRejected()
[Fact] public void NegativeMeasuresAreRejected()
[Fact] public void ForVariantBuildsTheInternalAndPrimaryIdentifiers()           // Code → Internal; Barcode → Variant/Gtin IsPrimary
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~OwnerTests|FullyQualifiedName~UnitOfMeasureTests|FullyQualifiedName~VariantLogisticsProfileTests"`

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `WmsLayerTests`** (la regla de namespaces ya es significativa: `UnitOfMeasure` y `UomCode` viven en `Inventory`; `VariantLogisticsProfile` en `Warehousing` los usa, y no al revés).

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~OwnerTests|FullyQualifiedName~UnitOfMeasureTests|FullyQualifiedName~VariantLogisticsProfileTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests"
```

- [ ] **Step 5: Commit** — `feat(wms): add owners, units of measure, variant profiles and identifiers`

---

### Task 6: Persistencia — `WmsDbContext`, migración `InitialWms`, `WmsUnitOfWork`, recorder, provisioner, repositorios, puertos

RF-102 (recorder), §11.2 (tablas e índices), §1.3 (traducción por nombre), P-2, P-7.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/WmsPorts.cs` (repositorios, `IWmsUnitOfWork`, `IWmsAuditRecorder`, `WmsActorType`, `WmsAuditActions`, `IWmsProvisioner`, `IVariantStockProbe`, `ILocationStockProbe`, `IOwnerStockProbe`, `IWmsCatalogReader` + `WmsVariantSnapshot`, `IWmsGeographyReader` + `WmsCityRef`)
- Create: `src/Modules/Wms/Modules.Wms.Application/WmsSupport.cs` (`WmsAuthorization`, `WmsCaller`, `WmsNotFound`, `WmsVersions`), `WmsPaging.cs`
- Create: `src/Modules/Wms/Modules.Wms.Domain/Warehousing/MemberScope.cs` (entidad sin comportamiento: `Guid Id, Guid TenantId, Guid MemberId, Guid? OwnerId, Guid? WarehouseId, DateTimeOffset CreatedAt`; fase 3 le pone reglas. Existe sólo para que `wms.member_scopes` nazca en `InitialWms`, S18/D-07)
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsDbContext.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/WmsUnitOfWork.cs`, `UniqueViolation.cs` (copia de Loans: sólo reconoce `23505`), `ForeignKeyViolation.cs` (nuevo: `23503` y `23001`, ver Interfaces), `WmsAuditRecorder.cs`, `WmsProvisioner.cs`, `WarehouseRepository.cs`, `OwnerRepository.cs`, `UnitOfMeasureRepository.cs`, `VariantProfileRepository.cs`, `IdentifierRepository.cs`, `NoStockProbes.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Persistence/Migrations/<timestamp>_InitialWms.cs` (generada)
- Modify: `src/Modules/Wms/Modules.Wms.Infrastructure/WmsInfrastructureExtensions.cs` (registros scoped de todo lo anterior; `WmsCaller` scoped)
- Create: `src/Bootstrapper/WmsCatalogReader.cs`, `src/Bootstrapper/WmsGeographyReader.cs` (los adaptadores de los dos puertos; sin ellos `VariantProfileRepository` no resuelve y `TheContainerValidatesAndBuilds…` se pone roja)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (`AddScoped<IWmsCatalogReader, WmsCatalogReader>()` y `AddScoped<IWmsGeographyReader, WmsGeographyReader>()` junto a los adaptadores de Loans, líneas 1317-1318)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsApiHarness.cs`, `WmsPersistenceTests.cs`

**Interfaces (las que WMS-D consume; nombres exactos; más `Location.AllowsNegative` de Task 4):**
- `public interface IWmsUnitOfWork { Task<int> SaveChangesAsync(CancellationToken cancellationToken); }`
- `public enum WmsActorType { Human, System, ApiKey }`
- `public interface IWmsAuditRecorder { void Record(Guid tenantId, Guid actorId, WmsActorType actorType, string action, string resourceType, string resourceId, IReadOnlyDictionary<string, string?> changedFields, DateTimeOffset occurredAt); }`
- `internal sealed class WmsAuditRecorder(WmsDbContext dbContext) : IWmsAuditRecorder` — `AuditEntry.Create(tenantId, actorId, Map(actorType), action, resourceType, resourceId, outcome: "success", changedFieldsJson: JsonSerializer.Serialize(changedFields), source: "wms", occurredAt)` y `dbContext.AuditEntries.Add(entry)`; `Map`: `Human → AuditActorType.Human`, `System → System`, `ApiKey → Integration`. `changed_fields` es `jsonb`: el diccionario se guarda como **objeto** JSON, no como el arreglo `"[]"` por defecto (S23, P-7). Una prueba de `WmsPersistenceTests` lee `changed_fields->>'code'` para fijarlo.
- `public static class WmsAuditActions { public static class Resources { Warehouse = "wms.warehouse", Location = "wms.location", Owner = "wms.owner", Profile = "wms.variant_profile", Uom = "wms.uom" } WarehouseCreated = "wms.warehouse.created", WarehouseUpdated = "wms.warehouse.updated", LocationCreated = "wms.location.created", LocationUpdated = "wms.location.updated", LocationDeleted = "wms.location.deleted", OwnerCreated = "wms.owner.created", OwnerUpdated = "wms.owner.updated", ProfileCreated = "wms.profile.created", ProfileUpdated = "wms.profile.updated", UomCreated = "wms.uom.created" }`
- `public sealed class WmsAuthorization(ITenantModules tenantModules)` — scoped, registrada en `AddWmsInfrastructure`. `public Task EnsureAsync(IExecutionContext ctx, Guid tenantId, string permission, Guid? ownerId = null, Guid? warehouseId = null, CancellationToken ct = default)`: `ctx.TenantId.Value != tenantId || !ctx.HasPermission(permission)` → `throw Denied()`; luego `TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Wms, ct)`. **`ownerId` y `warehouseId` se ignoran hasta fase 3** (S18: la firma es la definitiva para que WMS-D y los cortes siguientes no la cambien; `member_scopes` nace vacía y no se lee). `public static RequestForbiddenException Denied()` con el mensaje `"The subject cannot perform this wms operation for this tenant."`. Los handlers reciben `WmsAuthorization` por constructor además de `IExecutionContext` (que siguen necesitando para `SubjectId`), en vez del par `IExecutionContext, ITenantModules` de Loans. Decidido (ronda 2, 2026-11-01): la firma de S18 no recibe `ITenantModules`, así que `WmsAuthorization` es inyectable y no la estática de Loans; el spec §10.2 ya lo refleja.
- `public sealed class WmsCaller(IMembershipDirectory, IExecutionContext)` — copia de `PipelineCaller` (`FindAsync`, `RequireAsync`). WMS-D lo usa para el `actor_id` del movimiento.
- `internal static class WmsNotFound { Warehouse(Guid) → ResourceNotFoundException(WmsErrorCodes.WarehouseNotFound, …); Location(Guid); Owner(Guid) }`; `internal static class WmsVersions { static void Ensure(long current, long? received) }` (copia de `PipelineVersions`).
- `public static class WmsPaging { DefaultPageSize = 50; MaxPageSize = 200; NormalizePage; NormalizePageSize }` (copia de `ReportPaging`).
- `public interface IWarehouseRepository { void Add(Warehouse warehouse); Task<Warehouse?> GetAsync(Guid tenantId, WarehouseId id, CancellationToken ct) /* con Locations, rastreado */; Task<IReadOnlyList<Warehouse>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken ct) /* con Locations */; Task<Location?> GetLocationAsync(Guid tenantId, LocationId id, CancellationToken ct); Task<Location?> GetVirtualLocationAsync(Guid tenantId, WarehouseId warehouseId, LocationRole role, CancellationToken ct); void RemoveLocation(Location location); }`
- `public interface IOwnerRepository { void Add(Owner owner); Task<Owner?> FindAsync(Guid tenantId, OwnerId id, CancellationToken ct); Task<Owner> GetSelfAsync(Guid tenantId, CancellationToken ct) /* lanza InvalidOperationException si falta: sólo IWmsProvisioner escribe el Self (S5); el llamador debe haber provisionado antes */; Task<IReadOnlyList<Owner>> ListAsync(Guid tenantId, CancellationToken ct) /* Self primero, luego por nombre */; }`
- `public interface IUnitOfMeasureRepository { void Add(UnitOfMeasure uom); Task<UnitOfMeasure?> FindAsync(Guid tenantId, UomCode code, CancellationToken ct); Task<IReadOnlyList<UnitOfMeasure>> ListAsync(Guid tenantId, CancellationToken ct) /* sistema primero, luego por código */; }`
- `public interface IVariantProfileRepository { void Add(VariantLogisticsProfile profile); Task<VariantLogisticsProfile?> FindAsync(Guid tenantId, Guid variantId, CancellationToken ct); Task<IReadOnlyList<VariantLogisticsProfile>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); Task<VariantLogisticsProfile> GetOrCreateDefaultAsync(Guid tenantId, Guid variantId, CancellationToken ct) /* P-8: crea con CreateDefault + Identifier.ForVariant y audita ProfileCreated como System; lanza wms.profile.variant_unknown si Catalog no la conoce; el llamador guarda */; }`
- `public interface IIdentifierRepository { void AddRange(IEnumerable<Identifier> identifiers); Task<IReadOnlyList<Identifier>> ListByTargetAsync(Guid tenantId, Guid targetId, CancellationToken ct); void RemoveRange(IEnumerable<Identifier> identifiers); }`
- `public interface IWmsProvisioner { Task EnsureAsync(Guid tenantId, CancellationToken ct); }` — **el único escritor del `Self`** (`Owner.CreateSelf(…, name: "Propio")`) y de las seis UoM (`SystemDefaults`), con `INSERT … ON CONFLICT DO NOTHING` en SQL crudo y transacción propia (copia de `PipelineProvisioner`, S5); el `ON CONFLICT` del owner es `ON CONFLICT (tenant_id) WHERE kind = 'Self' DO NOTHING`. Lo llaman los handlers de owners, UoM y perfiles tras autorizar (los GET de listado incluidos: única excepción a «un GET no escribe», P-8).
- `public interface IVariantStockProbe { Task<bool> HasStockAsync(Guid tenantId, Guid variantId, CancellationToken ct); }`, `public interface ILocationStockProbe { Task<bool> HasStockAsync(Guid tenantId, Guid locationId, CancellationToken ct); }` y `public interface IOwnerStockProbe { Task<bool> HasStockAsync(Guid tenantId, Guid ownerId, CancellationToken ct); }` — `NoStockProbes.cs`: `internal sealed class NoVariantStockProbe`, `NoLocationStockProbe`, `NoOwnerStockProbe` que devuelven `false` (WMS-D las reemplaza por el ledger; las tres se registran scoped y las pruebas las sustituyen con `configureServices`).
- `public sealed record WmsVariantSnapshot(Guid VariantId, Guid ProductId, string ProductName, string VariantName, string Code, string? Barcode, IReadOnlyList<int> PackagingUnits, bool IsActive);` `public interface IWmsCatalogReader { Task<IReadOnlyList<WmsVariantSnapshot>> GetVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); }` (ids desconocidos o de otro tenant no aparecen).
- `public sealed record WmsCityRef(Guid CityId, string Name);` `public interface IWmsGeographyReader { Task<IReadOnlyDictionary<Guid, WmsCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken ct); }`
- `internal sealed class WmsCatalogReader(IProductRepository products, IOptionDefinitionRepository options) : IWmsCatalogReader` (Bootstrapper) — `FindVariantsByIdsAsync(tenantId, ids.Select(id => new VariantId(id)).ToArray(), ct)`; nombre visible con `CatalogVariantReads.LoadValueNamesAsync(options, tenantId, products, ct)` + `CatalogVariantReads.DisplayNameOf(product, variant, names)`; `IsActive = CatalogVariantReads.IsSellable(product, variant)`; `PackagingUnits = variant.PackagingUnits`; `ProductName = product.Name`.
- `internal sealed class WmsGeographyReader(ICityRepository cities) : IWmsGeographyReader` (Bootstrapper) — `ICityRepository.ListByIdsAsync` recibe `IReadOnlyCollection<CityId>`, así que el `Select` se materializa: `cities.ListByIdsAsync(cityIds.Distinct().Select(id => new CityId(id)).ToArray(), ct)`, exactamente como `SupplierGeographyLookup.FindCitiesAsync`; con cero ids devuelve el diccionario vacío sin consultar; proyecta a `WmsCityRef(city.Id.Value, city.Name)`.
- `WmsDbContext`: `public DbSet<Warehouse> Warehouses; DbSet<Location> Locations; DbSet<Owner> Owners; DbSet<UnitOfMeasure> UnitsOfMeasure; DbSet<VariantLogisticsProfile> VariantProfiles; DbSet<Identifier> Identifiers; DbSet<MemberScope> MemberScopes;` y las constantes `public const string WarehouseCodeIndex = "IX_warehouses_tenant_code", LocationCodeIndex = "IX_locations_warehouse_code", LocationVirtualRoleIndex = "IX_locations_warehouse_virtual_role", LocationParentForeignKey = "FK_locations_parent", OwnerSelfIndex = "IX_owners_tenant_self", UomPrimaryKey = "PK_units_of_measure", ConversionCodeIndex = "IX_uom_conversions_variant_code", IdentifierValueIndex = "IX_identifiers_tenant_value"`.
- `internal static class ForeignKeyViolation { public static bool Is(Exception exception, string constraintName) }` — `true` si `exception` es `PostgresException` o un `DbUpdateException` cuya `InnerException` lo es, con `SqlState` `PostgresErrorCodes.ForeignKeyViolation` (`23503`) **o** `PostgresErrorCodes.RestrictViolation` (`23001`) y `ConstraintName == constraintName` (ordinal). Existe porque `UniqueViolation` (copia de Loans) sólo reconoce `23505`, y borrar el padre de una FK `ON DELETE RESTRICT` da `23001`, no `23503` (precedente `CustomersUnitOfWork.IsForeignKeyOrRestrictViolationOf`). WMS-D la reutiliza para sus FKs internas.
- `WmsUnitOfWork.SaveChangesAsync`: `DbUpdateConcurrencyException` → `RequestConcurrencyException("concurrency.conflict", …)`; por nombre con `UniqueViolation.Is`: `WarehouseCodeIndex` → `WmsDomainException(WarehouseCodeTaken)`, `LocationCodeIndex` → `LocationCodeTaken`, `OwnerSelfIndex` → `OwnerSelfExists`, `UomPrimaryKey` → `UomCodeTaken`, `ConversionCodeIndex` → `ProfileConversionDuplicate`, `IdentifierValueIndex` → `IdentifierValueTaken`; con `ForeignKeyViolation.Is(exception, LocationParentForeignKey)` → `LocationHasChildren`.

**Tablas (`OnModelCreating`, columnas snake_case):**

| Tabla | Columnas | Claves/índices |
| --- | --- | --- |
| `warehouses` | `id, tenant_id, code varchar(32), name varchar(120), city_id uuid null, is_active, version bigint, created_at, updated_at` | PK `id`; único `IX_warehouses_tenant_code (tenant_id, code)` |
| `locations` | `id, tenant_id, warehouse_id, parent_id null, code varchar(32), path varchar(256), kind varchar(16), role varchar(16), is_virtual, allows_storage, allows_picking, max_weight_kg numeric(12,3) null, max_volume_m3 numeric(12,6) null, max_units int null, putaway_priority int, storage_classes text[], status varchar(16), version bigint, created_at, updated_at` | FK `FK_locations_warehouse` → `warehouses` RESTRICT; FK `FK_locations_parent` → `locations` RESTRICT; único `IX_locations_warehouse_code (warehouse_id, code)`; único parcial `IX_locations_warehouse_virtual_role (warehouse_id, role) WHERE is_virtual`; `IX_locations_warehouse_role (warehouse_id, role)`; `IX_locations_path (path)` con `.HasMethod("btree").HasOperators("text_pattern_ops")`; `CK_locations_kind`, `CK_locations_role`, `CK_locations_status`, `CK_locations_virtual_storage` (`NOT is_virtual OR NOT allows_storage`) |
| `owners` | `id, tenant_id, kind varchar(16), name varchar(120), identification varchar(40) null, contact_email varchar(254) null, is_active, version bigint, created_at, updated_at` | único parcial `IX_owners_tenant_self (tenant_id) WHERE kind = 'Self'`; `IX_owners_tenant (tenant_id)`; `CK_owners_kind` |
| `units_of_measure` | `tenant_id, code varchar(8), name varchar(40), precision int, is_system, created_at` | PK `(tenant_id, code)` con nombre `PK_units_of_measure`; `CK_units_of_measure_precision` (0..6) |
| `variant_profiles` | `tenant_id, variant_id, product_id, base_uom varchar(8), base_uom_precision int, tracking varchar(16), tracks_expiry, weight_kg numeric(12,3) null, volume_m3 numeric(12,6) null, length_cm/width_cm/height_cm numeric(10,2) null, storage_class varchar(32) null, display_uom varchar(8) null, version bigint, created_at, updated_at` | PK `(tenant_id, variant_id)`; `CK_variant_profiles_tracking`; `CK_variant_profiles_precision` (0..6) |
| `uom_conversions` | `id, tenant_id, variant_id, code varchar(32), factor numeric(18,6), barcode varchar(64) null, created_at` | FK compuesta `(tenant_id, variant_id)` → `variant_profiles` CASCADE; único `IX_uom_conversions_variant_code (tenant_id, variant_id, code)`; `CK_uom_conversions_factor` (`factor > 0`) |
| `identifiers` | `id, tenant_id, kind varchar(16), symbology varchar(16), value varchar(128), target_id, conversion_id null, is_primary, created_at` | único `IX_identifiers_tenant_value (tenant_id, value)`; `IX_identifiers_tenant_target (tenant_id, target_id)`; `CK_identifiers_kind` (`Variant, Presentation, Lot, LogisticUnit`, S7), `CK_identifiers_symbology` |
| `member_scopes` | `id, tenant_id, member_id, owner_id uuid null, warehouse_id uuid null, created_at` | PK `id`; `IX_member_scopes_tenant_member (tenant_id, member_id)`; `CK_member_scopes_target` (`owner_id IS NOT NULL OR warehouse_id IS NOT NULL`). **Nace vacía y nadie la lee en este corte** (S18, D-07); sin FK a `owners`/`warehouses` todavía, fase 3 decide si la lleva. Decidido (ronda 2): el spec §10.2 y D-07 ya fijan estas claves; fase 3 puede cambiarlas por migración porque la tabla está vacía. |

Ids fuertes con `HasConversion(id => id.Value, value => new WarehouseId(value))` como `LoanId`; `UomCode` igual (`value => new UomCode(value)`); `StorageClasses` como `PrimitiveCollection` `text[]`; navegaciones `Warehouse.Locations` y `VariantLogisticsProfile.Conversions` con `UsePropertyAccessMode(PropertyAccessMode.Field)`.

- [ ] **Step 1: Escribir `WmsApiHarness`**

Copia de `tests/Modules/Loans/Modules.Loans.IntegrationTests/LoansApiHarness.cs`, declarada `internal static partial class WmsApiHarness` (el plan WMS-D agrega `WmsApiHarness.Inventory.cs` con sus helpers sin tocar este archivo), con: constantes `TenantId`, `SubjectId`, `OtherTenantId`, `OtherSubjectId`; `WmsUrl(tenantId = TenantId)`; `StartDatabaseAsync`; `SeedTenantAsync(factory, tenantId, modules)` (slug `wms-api-tests`); `CreateClient(factory, subjectId, tenantId, params permissions)`; `public static readonly string[] AllPermissions` con las 16 constantes; `CreateManager(factory)` = `AllPermissions`; `CreateReader(factory)` = `DocumentRead`; `OpenAsync(modules)`; `BodyAsync`; `CodeOfAsync(response)`; `CountAuditEntriesAsync(factory, action, resourceId)`; `QepApiFactory` sin el bloque del worker de Loans; y:

```csharp
/// <summary>Un producto por la API de Catalog con una variante con código, código de barras y empaques: lo que el perfil
/// logístico copia y siembra. Usa un cliente propio con catalog.product.manage.</summary>
public static async Task<Guid> CreateVariantAsync(QepApiFactory factory, string code, string? barcode, int[] packagingUnits, string name = "Producto")
// CreateClient(factory, SubjectId, TenantId, "catalog.product.manage") y POST /api/v1/tenants/{TenantId}/catalog/products con el cuerpo de
// PosApiHarness.CreateProductAsync: `barcode` va en la variante junto a `code` (ProductVariantRequest.Barcode) y `packagingUnits`
// va DENTRO de `pricing` (ProductPricingRequest.PackagingUnits), donde el harness de Pos pone `Array.Empty<int>()`; devuelve el id
// de la variante (= id del producto, VariantId.cs: la variante por defecto comparte el Guid).

/// <summary>Una ciudad real: GeographySeeder siembra el DIVIPOLA en cada arranque. Copia de CompaniesApiHarness.EnsureCityIdAsync.</summary>
public static async Task<Guid> AnyCityIdAsync(QepApiFactory factory)   // GET /api/v1/departments → GET /api/v1/cities?departmentId= → cities[0].Id
```

- [ ] **Step 2: Escribir `WmsPersistenceTests` (RED)**

```csharp
[Fact] public async Task TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes()
// consulta pg_tables (8 tablas, member_scopes incluida y vacía) y pg_indexes del esquema wms: IX_warehouses_tenant_code,
// IX_locations_warehouse_code, IX_locations_warehouse_virtual_role, IX_owners_tenant_self, IX_uom_conversions_variant_code,
// IX_identifiers_tenant_value, PK_units_of_measure, IX_member_scopes_tenant_member
[Fact] public async Task TheProvisionerCreatesOneSelfOwnerAndSixUnitsAndIsIdempotent()   // EnsureAsync dos veces → 1 owner Self, 6 uoms
[Fact] public async Task GetSelfAsyncThrowsWhenTheTenantWasNeverProvisioned()           // InvalidOperationException (S5): el repositorio no escribe
[Fact] public async Task TheRecorderWritesTheChangedFieldsAsAJsonObject()               // Record({ ["code"]="BOG" }) + SaveChanges → audit.entries.changed_fields->>'code' = 'BOG' y jsonb_typeof = 'object' (S23)
[Fact] public async Task DeletingAParentLocationIsTranslatedToHasChildren()             // Locations.Remove(RECEIVING) con REC-01 vivo + SaveChanges → WmsDomainException wms.location.has_children (23001 por nombre de FK)
[Fact] public async Task ASecondSelfOwnerIsTranslatedToSelfExists()                     // Owners.Add(CreateSelf) + SaveChanges → WmsDomainException wms.owner.self_exists
[Fact] public async Task ADuplicateWarehouseCodeIsTranslatedToCodeTaken()
[Fact] public async Task AStaleVersionIsTranslatedToConcurrencyConflict()               // RequestConcurrencyException
[Fact] public async Task GetOrCreateDefaultAsyncCreatesTheProfileTheConversionsAndTheIdentifiers()
// variante con barcode "7701234567890", code "SKU-1", packagingUnits [12]: perfil UND/0/None, 1 conversión EMPAQUE x12=12,
// identifiers Internal "SKU-1" y Gtin "7701234567890" IsPrimary, audit wms.profile.created con actor System
```

Las pruebas resuelven `WmsDbContext`, `IWmsProvisioner`, `IWmsUnitOfWork`, `IVariantProfileRepository` desde `factory.Services.CreateAsyncScope()`.

- [ ] **Step 3: RED** — compila pero falla (sin entidades mapeadas ni migración).

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WmsPersistenceTests"
```

- [ ] **Step 4: Mapear, generar la migración, implementar UoW/recorder/provisioner/repositorios, registrar**

```powershell
dotnet ef migrations add InitialWms --project src/Modules/Wms/Modules.Wms.Infrastructure --context WmsDbContext -o Persistence/Migrations
```

Revisar la migración: los índices parciales llevan `filter:`; `text_pattern_ops` aparece en `Annotation("Npgsql:IndexOperators", …)`; ninguna FK apunta fuera de `wms`; `member_scopes` está en el `Up` aunque nadie la escriba. `AddWmsInfrastructure` registra lo propio del módulo (`WmsAuthorization`, `WmsCaller`, UoW, recorder, provisioner, repositorios y las tres `No*StockProbe`, todo scoped); los dos adaptadores (`WmsCatalogReader`, `WmsGeographyReader`) se registran en Bootstrapper en esta misma tarea. `GetOrCreateDefaultAsyncCreates…` usa una variante real creada con `CreateVariantAsync`.

- [ ] **Step 5: GREEN + `WmsLayerTests` + `CompositionRootTests`**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WmsPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WmsLayerTests|FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 6: Commit** — `feat(wms): add persistence, unit of work, audit recorder and provisioner`

---

### Task 7: Pruebas de acceso, prueba del adaptador de Catalog y la lista de bodegas

§1.3 (puertos con adaptador en Bootstrapper), §24.1 (403), §1.1.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/ListWarehouses.cs` (`ListWarehousesQuery`, `ListWarehousesHandler`, sólo para tener una ruta que probar; el resto de bodegas en Task 8)
- Create: `src/Modules/Wms/Modules.Wms.Application/WmsDtos.cs` (nace aquí con `WarehouseDto` y `WarehousesDto`; las tareas siguientes le agregan sus requests y DTOs) y `src/Modules/Wms/Modules.Wms.Application/Warehousing/WarehouseDtoMapper.cs` (`internal static class WarehouseDtoMapper { static WarehouseDto ToDto(Warehouse warehouse, string? cityName) }`; `LocationCount` = ubicaciones con `IsVirtual == false`)
- Modify: `src/Modules/Wms/Modules.Wms.Api/WmsEndpoints.cs` (`MapGet("/warehouses", …)` con `RequireAuthorization(WmsPermissions.DocumentRead)`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (el handler de la lista, a mano y con ruta completa como los de Loans, líneas 394-437)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WmsAccessApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/WmsCatalogReaderTests.cs`

**Interfaces:**
- Consumes: `WmsCatalogReader`, `WmsGeographyReader` y `WmsAuthorization` de Task 6.
- Produces: `public sealed record WarehouseDto(Guid Id, string Code, string Name, Guid? CityId, string? CityName, bool IsActive, long Version, int LocationCount);` (comentario en el DTO: `LocationCount` excluye las virtuales porque la lista de bodegas muestra «ubicaciones configuradas», y las cuatro virtuales siempre están); `public sealed record WarehousesDto(IReadOnlyList<WarehouseDto> Items)`; `WarehouseDtoMapper.ToDto`.
- `public sealed record ListWarehousesQuery(Guid TenantId, bool IncludeInactive) : IQuery<WarehousesDto>`; `ListWarehousesHandler(IExecutionContext, WmsAuthorization, IWarehouseRepository, IWmsGeographyReader)` — `authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken)`; `CityName` resuelto en lote con `FindCitiesAsync`.

- [ ] **Step 1: Escribir `WmsAccessApiTests` (RED)**

```csharp
[Fact] public async Task WithoutThePermissionTheWarehousesAre403()            // X-Permissions "tenancy.settings.read"
[Fact] public async Task AnotherTenantInTheRouteIs403Never404()               // OtherTenant + authorization.denied
[Fact] public async Task WithTheModuleOffTheWarehousesAre403()
// OpenAsync(modules: TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Wms)) + CreateManager → 403 (la máscara quita el permiso)
[Fact] public async Task WithTheModuleOnAnEmptyTenantListsNoWarehouses()      // 200 { items: [] }
```

- [ ] **Step 2: Escribir `WmsCatalogReaderTests` (RED)** — con `CatalogVariantTestDoubles` de `tests/Bootstrapper/Bootstrapper.UnitTests/`: una variante con opciones devuelve `VariantName` «Producto – M / Azul», `Barcode`, `Code`, `PackagingUnits`; un id desconocido no aparece; producto inactivo → `IsActive = false`.

- [ ] **Step 3: RED**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WmsCatalogReaderTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WmsAccessApiTests"
```

- [ ] **Step 4: Implementar handler, ruta y registro** (si `WmsCatalogReaderTests` encuentra un defecto del adaptador de Task 6, se corrige aquí)

- [ ] **Step 5: GREEN + `CompositionRootTests` + `WmsLayerTests`**

```powershell
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WmsCatalogReaderTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WmsAccessApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~WmsLayerTests"
```

- [ ] **Step 6: Commit** — `feat(wms): list warehouses and prove the module access rules`

---

### Task 8: Bodegas — crear y editar

RF-001, RF-003, RF-102.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/CreateWarehouse.cs`, `UpdateWarehouse.cs`
- Modify: `src/Modules/Wms/Modules.Wms.Application/WmsDtos.cs` (sólo los dos requests de bodega; `WarehouseDto`/`WarehousesDto` y `WarehouseDtoMapper` ya existen desde Task 7)
- Modify: `src/Modules/Wms/Modules.Wms.Api/WmsEndpoints.cs` → mover las rutas de bodegas a `src/Modules/Wms/Modules.Wms.Api/WarehouseEndpoints.cs` (`internal static class WarehouseEndpoints { static void Map(RouteGroupBuilder group) }`, como `DealEndpoints` cuelga de `PipelineEndpoints`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (dos handlers + `AddValidatorsFromAssemblyContaining<CreateWarehouseValidator>()` junto a la línea 1051)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/WarehouseApiTests.cs`, `tests/Modules/Wms/Modules.Wms.UnitTests/CreateWarehouseValidatorTests.cs`

**Interfaces:**
- `public sealed record CreateWarehouseRequest(string Code, string Name, Guid? CityId);` `public sealed record CreateWarehouseCommand(Guid TenantId, string Code, string Name, Guid? CityId) : ICommand<WarehouseDto> { static From(Guid tenantId, CreateWarehouseRequest request) }`; `CreateWarehouseValidator : AbstractValidator<CreateWarehouseCommand>` (`Code` no vacío ≤ 32; `Name` no vacío ≤ 120).
- `public sealed record UpdateWarehouseRequest(string Name, Guid? CityId, bool IsActive, long Version);` `UpdateWarehouseCommand(Guid TenantId, Guid WarehouseId, string Name, Guid? CityId, bool IsActive, long Version) : ICommand<WarehouseDto> { static From(Guid tenantId, Guid warehouseId, UpdateWarehouseRequest request) }`; `UpdateWarehouseValidator`. **Los comandos van aplanados** (S25): el request nunca se anida dentro del comando, para que `ValidationException.Errors[].PropertyName` —que `ApiExceptionHandler` agrupa tal cual en el mapa `errors`— sea `Code`/`Name` y no `Request.Code`. Es el patrón de todos los comandos de WMS-B (Tasks 9–12).
- Consumes: `WarehouseDto`, `WarehousesDto` y `WarehouseDtoMapper.ToDto` de Task 7.
- Handlers: `CreateWarehouseHandler(IExecutionContext, WmsAuthorization, IWarehouseRepository, IWmsGeographyReader, IWmsUnitOfWork, IWmsAuditRecorder, IClock, IValidator<CreateWarehouseCommand>)`: autoriza `WarehouseManage` → valida → si `CityId` no resuelve, `wms.warehouse.city_unknown` → `Warehouse.Create(WarehouseId.New(), …)` → `Add` → `Record(tenantId, SubjectId, Human, WarehouseCreated, Resources.Warehouse, id, { ["code"]=…, ["name"]=…, ["cityId"]=… }, now)` → `SaveChangesAsync` → DTO. `UpdateWarehouseHandler`: `GetAsync` o `WmsNotFound.Warehouse`; `WmsVersions.Ensure`; `Update`; audita `WarehouseUpdated` con los campos cambiados.
- Rutas: `POST /warehouses` → 201 con `Location` header `/api/v1/tenants/{tenantId}/wms/warehouses/{id}`; `PUT /warehouses/{id:guid}`.

- [ ] **Step 1: Pruebas (RED)**

```csharp
// CreateWarehouseValidatorTests: código vacío, código > 32, nombre vacío → inválido; válido pasa.
// WarehouseApiTests
[Fact] public async Task CreatingAWarehouseReturns201WithItsVirtualsAndAudits()
// POST { code: "bog", name: "Bogotá" } → 201, Code "BOG", LocationCount 2 (RECEIVING y REC-01; las virtuales no cuentan), Version 1;
// GET /warehouses/{id}/locations (Task 9: por ahora consulta wms.locations por Npgsql) tiene 6 filas, 4 is_virtual; audit wms.warehouse.created = 1
[Fact] public async Task ADuplicateCodeIs422CodeTaken()
[Fact] public async Task AnInvalidCodeIs422CodeInvalid()                 // "BO G"
[Fact] public async Task AnUnknownCityIs422CityUnknown()                 // cityId aleatorio
[Fact] public async Task AKnownCityComesBackWithItsName()                // AnyCityIdAsync; cityName no vacío
[Fact] public async Task UpdatingWithAStaleVersionIs412()
[Fact] public async Task UpdatingRenamesDeactivatesAndAudits()           // isActive false; GET sin includeInactive no lo trae, con includeInactive sí
[Fact] public async Task AnUnknownWarehouseIs404()                        // wms.warehouse.not_found
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~CreateWarehouseValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WarehouseApiTests"
```

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `CompositionRootTests`**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~CreateWarehouseValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~WarehouseApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 5: Commit** — `feat(wms): create and update warehouses with their virtual locations`

---

### Task 9: Ubicaciones — árbol, alta, edición, borrado y `scannable`

RF-002, RF-003, §8, RF-102.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/ListLocations.cs`, `CreateLocation.cs`, `UpdateLocation.cs`, `DeleteLocation.cs`, `GetScannableLocations.cs`, `LocationDtoMapper.cs`, `LocationRules.cs` (parseo de `kind`/`role`/`status` por nombre, `TryParse` sin `ignoreCase`)
- Create: `src/Modules/Wms/Modules.Wms.Api/LocationEndpoints.cs`
- Modify: `WmsDtos.cs`, `QepServiceCollectionExtensions.cs` (cinco handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/LocationApiTests.cs`, `tests/Modules/Wms/Modules.Wms.UnitTests/CreateLocationValidatorTests.cs`

**Interfaces:**
- `LocationRequest(Guid? ParentId, string Code, string Kind, string Role, bool AllowsStorage, bool AllowsPicking, decimal? MaxWeightKg, decimal? MaxVolumeM3, int? MaxUnits, int PutawayPriority, IReadOnlyList<string>? StorageClasses)`; `UpdateLocationRequest(… mismos + string Status, long Version)`.
- Comandos **aplanados** (S25, patrón de Task 8): `CreateLocationCommand(Guid TenantId, Guid WarehouseId, Guid? ParentId, string Code, string Kind, string Role, bool AllowsStorage, bool AllowsPicking, decimal? MaxWeightKg, decimal? MaxVolumeM3, int? MaxUnits, int PutawayPriority, IReadOnlyList<string>? StorageClasses) : ICommand<LocationDto> { static From(Guid tenantId, Guid warehouseId, LocationRequest request) }`; `UpdateLocationCommand(Guid TenantId, Guid WarehouseId, Guid LocationId, … los mismos campos …, string Status, long Version) : ICommand<LocationDto> { static From(Guid tenantId, Guid warehouseId, Guid locationId, UpdateLocationRequest request) }`; `DeleteLocationCommand(Guid TenantId, Guid WarehouseId, Guid LocationId) : ICommand<bool>`; `ListLocationsQuery(Guid TenantId, Guid WarehouseId) : IQuery<LocationsDto>`; `GetScannableLocationsQuery(Guid TenantId, Guid WarehouseId) : IQuery<ScannableLocationsDto>`.
- Validadores sobre el comando (`RuleFor(command => command.Code)`, nunca `command.Request.Code`): `Kind` ∈ nombres de `LocationKind`, `Role` ∈ `LocationRole`, `Status` ∈ `LocationStatus`, `Code` ≤ 32 no vacío, `StorageClasses` cada una ≤ 32 (`RuleForEach` → clave `StorageClasses[0]`), `PutawayPriority ≥ 0`. Las claves de `errors` que la prueba lee: `Code`, `Kind`, `PutawayPriority`.
- Handlers: `(IExecutionContext, WmsAuthorization, IWarehouseRepository, …)` como en Task 8; el `Update` que toca una virtual recibe `wms.location.virtual_immutable` del agregado sin mirar el cuerpo (S22).
- `LocationDto(Guid Id, Guid WarehouseId, Guid? ParentId, string Code, string Path, string Kind, string Role, bool IsVirtual, bool AllowsStorage, bool AllowsPicking, decimal? MaxWeightKg, decimal? MaxVolumeM3, int? MaxUnits, int PutawayPriority, IReadOnlyList<string> StorageClasses, string Status, long Version)`; `LocationsDto(IReadOnlyList<LocationDto> Items)` ordenado por `Path` ordinal; `ScannableLocationDto(Guid Id, string Code, string Path, string Role)`; `ScannableLocationsDto(IReadOnlyList<ScannableLocationDto> Items, string Etag)`.
- `Etag` = `"\"" + hex de los 8 primeros bytes de SHA-256 sobre $"{id:N}:{version}" de cada ubicación no virtual, ordenadas por id, unidas con ';' + "\""`. El endpoint pone `Response.Headers.ETag` y responde `304` si `If-None-Match` coincide (comparación ordinal, aceptando `W/`).
- `DeleteLocationHandler`: `ILocationStockProbe.HasStockAsync` → `wms.location.has_stock`; `warehouse.RemoveLocation(id)`; `repository.RemoveLocation(location)`; audita `LocationDeleted`.
- Rutas en `LocationEndpoints.Map(group)`: `GET /warehouses/{id:guid}/locations`, `POST …/locations`, `GET …/locations/scannable` (antes que `/{locationId:guid}`), `PUT …/locations/{locationId:guid}`, `DELETE …/locations/{locationId:guid}`.

- [ ] **Step 1: Pruebas (RED)**

```csharp
[Fact] public async Task ListingReturnsTheFlatTreeWithParentIdsAndPathsOrderedByPath()
[Fact] public async Task CreatingAZoneAndABinBuildsThePathAndAudits()           // POST zona A (Zone/Storage) y bin A-01 (Bin/Picking, parentId zona) → "BOG/A/A-01"
[Fact] public async Task ADuplicateCodeInTheWarehouseIs422CodeTaken()
[Fact] public async Task AVirtualKindOrRoleIs422()                              // kind Virtual → kind_not_allowed; role Transit → role_not_allowed
[Fact] public async Task AParentFromAnotherWarehouseIs422ParentInvalid()
[Fact] public async Task MovingALocationUpdatesTheChildrenPaths()               // PUT rack con otro parentId → GET lista muestra bins con el path nuevo y Version del bin sin cambiar
[Fact] public async Task UpdatingAVirtualLocationIs422()                        // PUT TRANSIT con el cuerpo idéntico al GET → 422 virtual_immutable; PUT TRANSIT status Blocked → 422 igual (S22); Version sin cambiar
[Fact] public async Task AnInvalidBodyIs422ValidationFailedWithTheField()        // code "" y putawayPriority -1 → errors.Code y errors.PutawayPriority (sin prefijo Request., S25)
[Fact] public async Task DeletingAVirtualIs422AndDeletingAnEmptyBinIs204()      // y el bin ya no aparece en la lista; audit wms.location.deleted
[Fact] public async Task DeletingAZoneWithChildrenIs422HasChildren()
[Fact] public async Task TheScannableListExcludesVirtualsAndCarriesTheEtag()    // 2 ítems en una bodega nueva (RECEIVING, REC-01); header ETag == body.etag
[Fact] public async Task TheScannableEtagIsStableUntilALocationChanges()        // dos GET iguales; tras POST de un bin cambia; tras PUT status Blocked de ese bin (no de una virtual) cambia
[Fact] public async Task IfNoneMatchWithTheCurrentEtagIs304()
[Fact] public async Task AnotherTenantsWarehouseIs403OnLocations()              // outsider → authorization.denied
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~CreateLocationValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LocationApiTests"
```

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `CompositionRootTests`**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~CreateLocationValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LocationApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 5: Commit** — `feat(wms): manage the location tree and the scannable cache`

---

### Task 10: Propietarios

RF-005 (`Self`), §10.1, P-2.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/ListOwners.cs`, `CreateOwner.cs`, `UpdateOwner.cs`
- Create: `src/Modules/Wms/Modules.Wms.Api/OwnerEndpoints.cs`
- Modify: `WmsDtos.cs`, `QepServiceCollectionExtensions.cs` (tres handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/OwnerApiTests.cs`

**Interfaces:**
- `CreateOwnerRequest(string Name, string? Identification, string? ContactEmail)`; `UpdateOwnerRequest(string Name, string? Identification, string? ContactEmail, bool IsActive, long Version)`. Comandos **aplanados** (S25): `CreateOwnerCommand(Guid TenantId, string Name, string? Identification, string? ContactEmail) : ICommand<OwnerDto> { static From(Guid tenantId, CreateOwnerRequest request) }`; `UpdateOwnerCommand(Guid TenantId, Guid OwnerId, string Name, string? Identification, string? ContactEmail, bool IsActive, long Version) : ICommand<OwnerDto> { static From(Guid tenantId, Guid ownerId, UpdateOwnerRequest request) }`; `ListOwnersQuery(Guid TenantId) : IQuery<OwnersDto>`; validadores sobre el comando (`RuleFor(command => command.ContactEmail)`): `Name` ≤ 120 no vacío; `Identification` ≤ 40; `ContactEmail` ≤ 254 y `EmailAddress()` cuando viene → clave `ContactEmail` en `errors`.
- `OwnerDto(Guid Id, string Kind, string Name, string? Identification, string? ContactEmail, bool IsActive, long Version)`; `OwnersDto(IReadOnlyList<OwnerDto> Items)`.
- Handlers `(IExecutionContext, WmsAuthorization, IWmsProvisioner, IOwnerRepository, …)`: los tres llaman `IWmsProvisioner.EnsureAsync` tras autorizar (`OwnerManage`); `ListOwners` es un GET que escribe sólo por el provisioner (P-8, S5) y luego lee con `ListAsync`, nunca crea el `Self` por su cuenta. `UpdateOwnerHandler` recibe además `IOwnerStockProbe`: si el owner es `Client`, está activo y el comando trae `IsActive == false`, consulta `HasStockAsync(tenantId, ownerId)` y con `true` lanza `WmsDomainException(OwnerHasStock)` **antes** de `Update` (el `Self` ya lo frena el agregado con `self_immutable`). Auditan `OwnerCreated`/`OwnerUpdated` con `["ownerId"] = id` en `changedFields` (§10.3).

- [ ] **Step 1: Pruebas (RED)**

```csharp
[Fact] public async Task ListingANewTenantReturnsOnlyTheSelfOwner()      // kind Self, name "Propio", primero
[Fact] public async Task CreatingAClientOwnerReturns201AndAudits()
[Fact] public async Task DeactivatingTheSelfOwnerIs422SelfImmutable()
[Fact] public async Task DeactivatingAClientWorksAndAStaleVersionIs412()
[Fact] public async Task DeactivatingAClientWithStockIs422()                // configureServices sustituye IOwnerStockProbe por uno que devuelve true → wms.owner.has_stock; el owner sigue activo
[Fact] public async Task AnInvalidEmailIs422ValidationFailedWithTheField() // errors.ContactEmail (clave sin prefijo Request., S25)
[Fact] public async Task AnUnknownOwnerIs404()
```

- [ ] **Step 2: RED**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~OwnerApiTests"
```

Esperado: no compila (los comandos y `OwnerEndpoints` no existen).

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `CompositionRootTests`**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~OwnerApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 5: Commit** — `feat(wms): list, create and update owners with the provisioned Self`

---

### Task 11: Unidades de medida

RF-008, §16.1.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Inventory/ListUoms.cs`, `CreateUom.cs`
- Create: `src/Modules/Wms/Modules.Wms.Api/UomEndpoints.cs`
- Modify: `WmsDtos.cs`, `QepServiceCollectionExtensions.cs` (dos handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/UomApiTests.cs`

**Interfaces:**
- `CreateUomRequest(string Code, string Name, int Precision)`; comando **aplanado** (S25): `CreateUomCommand(Guid TenantId, string Code, string Name, int Precision) : ICommand<UomDto> { static From(Guid tenantId, CreateUomRequest request) }`; `ListUomsQuery(Guid TenantId) : IQuery<UomsDto>`; `UomDto(string Code, string Name, int Precision, bool IsSystem)`; `UomsDto(IReadOnlyList<UomDto> Items)`. Validador sobre el comando: `Code` ≤ 8 no vacío, `Name` ≤ 40 no vacío, `Precision` 0..6 → claves `Code`, `Name`, `Precision`.
- Ambos handlers `(IExecutionContext, WmsAuthorization, IWmsProvisioner, IUnitOfMeasureRepository, …)`: autorizan `ProfileManage`, `IWmsProvisioner.EnsureAsync` (el GET escribe sólo por el provisioner, P-8). `CreateUom` audita `UomCreated`.

- [ ] **Step 1: Pruebas (RED)**

```csharp
[Fact] public async Task ListingANewTenantReturnsTheSixSystemUnitsFirst()   // UND,KG,G,L,ML,M con sus precisiones e isSystem true
[Fact] public async Task CreatingACustomUnitReturns201LowercaseNormalizedAndAudits() // "caja" → "CAJA", isSystem false
[Fact] public async Task ADuplicateCodeIs422CodeTaken()                       // "UND"
[Fact] public async Task PrecisionSevenIs422ValidationFailed()               // errors.Precision
```

- [ ] **Step 2: RED**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~UomApiTests"
```

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `CompositionRootTests`**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~UomApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 5: Commit** — `feat(wms): list and create units of measure`

---

### Task 12: Perfiles logísticos de variante

RF-006, RF-007, RF-009 (variante), RF-010, ADR-07, ADR-08, §25, P-8.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/GetVariantProfiles.cs`, `UpdateVariantProfile.cs`, `VariantProfileDtoMapper.cs`
- Create: `src/Modules/Wms/Modules.Wms.Api/VariantProfileEndpoints.cs`
- Modify: `WmsDtos.cs`, `QepServiceCollectionExtensions.cs` (dos handlers)
- Test: `tests/Modules/Wms/Modules.Wms.IntegrationTests/VariantProfileApiTests.cs`, `tests/Modules/Wms/Modules.Wms.UnitTests/UpdateVariantProfileValidatorTests.cs`

**Interfaces:**
- `UomConversionRequest(string Code, decimal Factor, string? Barcode)`; `UpdateVariantProfileRequest(string BaseUom, int BaseUomPrecision, string Tracking, bool TracksExpiry, decimal? WeightKg, decimal? VolumeM3, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, string? StorageClass, string? DisplayUom, IReadOnlyList<UomConversionRequest> Conversions, long Version)`.
- `GetVariantProfilesQuery(Guid TenantId, IReadOnlyList<Guid> VariantIds) : IQuery<VariantProfilesDto>` (validador: 1..200 ids; el endpoint parte `variantIds` por coma); comando **aplanado** (S25): `UpdateVariantProfileCommand(Guid TenantId, Guid VariantId, string BaseUom, int BaseUomPrecision, string Tracking, bool TracksExpiry, decimal? WeightKg, decimal? VolumeM3, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, string? StorageClass, string? DisplayUom, IReadOnlyList<UomConversionRequest> Conversions, long Version) : ICommand<VariantProfileDto> { static From(Guid tenantId, Guid variantId, UpdateVariantProfileRequest request) }`. Validador sobre el comando: `Tracking` ∈ nombres de `TrackingPolicy`, `BaseUomPrecision` 0..6, `Conversions` ≤ 50 y `RuleForEach(command => command.Conversions)` con `Code` no vacío ≤ 32 y `Factor > 0` → claves `Tracking`, `BaseUomPrecision`, `Conversions[0].Code`, `Conversions[0].Factor`.
- `UomConversionDto(Guid Id, string Code, decimal Factor, string? Barcode)`; `VariantProfileDto(Guid VariantId, Guid ProductId, string ProductName, string VariantCode, string VariantName, string? Barcode, string BaseUom, int BaseUomPrecision, string Tracking, bool TracksExpiry, decimal? WeightKg, decimal? VolumeM3, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, string? StorageClass, string? DisplayUom, IReadOnlyList<UomConversionDto> Conversions, bool HasStock, long Version)`; `VariantProfilesDto(IReadOnlyList<VariantProfileDto> Items)`.
- `GetVariantProfilesHandler(IExecutionContext, WmsAuthorization, IWmsProvisioner, IWmsCatalogReader, IVariantProfileRepository, IVariantStockProbe)`: snapshots de Catalog (los desconocidos se omiten) + perfiles guardados; para los sin fila, `VariantLogisticsProfile.CreateDefault(...)` en memoria con `Version = 0` (las conversiones por defecto llevan `Id = Guid.Empty`); `HasStock` por probe.
- `UpdateVariantProfileHandler(…, IUnitOfMeasureRepository, IIdentifierRepository, IWmsUnitOfWork, IWmsAuditRecorder, IClock, IValidator<…>)`: autoriza `ProfileManage` → provisiona → valida → snapshot o `wms.profile.variant_unknown` → `BaseUom`/`DisplayUom` existen o `wms.uom.unknown`; `BaseUomPrecision > uom.Precision` → `wms.uom.precision_exceeded` → `Version == 0` y sin fila: `Create` + `Identifier.ForVariant` + audita `ProfileCreated`; con fila: `WmsVersions.Ensure` + `Update(changes, hasStock)` + audita `ProfileUpdated` → identificadores `Presentation` (`Symbology = Gtin`, `ConversionId`) se reemplazan por los `Barcode` de las conversiones nuevas → `SaveChangesAsync`.
- Rutas: `GET /variant-profiles?variantIds=`, `PUT /variant-profiles/{variantId:guid}`.

- [ ] **Step 1: Pruebas (RED)**

```csharp
// UpdateVariantProfileValidatorTests: Tracking fuera del enum, precisión 7, conversión con código vacío (PropertyName "Conversions[0].Code", S25),
// más de 50 conversiones → inválido.
// VariantProfileApiTests (CreateVariantAsync(code "SKU-1", barcode "7701234567890", packagingUnits [12, 144]))
[Fact] public async Task GettingAnUnsavedProfileReturnsTheDefaultWithConversionsFromPackagingUnits()
// baseUom UND, precision 0, tracking None, conversions ["EMPAQUE x12"=12, "EMPAQUE x144"=144], version 0, hasStock false, productName/variantCode/barcode del catálogo
[Fact] public async Task UnknownVariantIdsAreOmitted()
[Fact] public async Task MoreThanTwoHundredIdsIs422()
[Fact] public async Task TheFirstPutCreatesTheProfileTheIdentifiersAndAudits()
// version 0 → 200 version 1; wms.identifiers tiene Internal "SKU-1" y Gtin "7701234567890" is_primary; audit wms.profile.created
[Fact] public async Task ChangingTrackingWithoutStockIsAllowed()               // None → Lot con hasStock false (probe) → 200
[Fact] public async Task ChangingTrackingWithStockIs422()                      // configureServices sustituye IVariantStockProbe por uno que devuelve true → tracking_change_requires_identification
[Fact] public async Task AnUnknownBaseUomIs422UomUnknown()
[Fact] public async Task PrecisionAboveTheUnitIs422PrecisionExceeded()         // UND con precisión 2
[Fact] public async Task AConversionBarcodeTakenByAnotherVariantIs422ValueTaken() // el barcode de otra variante ya copiado como Variant
[Fact] public async Task AStaleVersionIs412()
[Fact] public async Task AVariantOfAnotherTenantIs422VariantUnknown()
[Fact] public async Task AnEmptyConversionCodeIs422ValidationFailedWithTheIndexedField() // errors["Conversions[0].Code"] (S25)
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~UpdateVariantProfileValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~VariantProfileApiTests"
```

- [ ] **Step 3: Implementar**

- [ ] **Step 4: GREEN + `CompositionRootTests`**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~UpdateVariantProfileValidatorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~VariantProfileApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

- [ ] **Step 5: Commit** — `feat(wms): read and update variant logistics profiles`

---

### Task 13 (opcional, al final): Etiquetas Code 128 de ubicación

RF-004, §23.3, P-9, **P-11 (S24)**: el spec marca RF-004 como «fase 1, opcional en WMS-B». Si el tiempo no alcanza, se deja fuera y se anota en el handoff como desviación; nada de lo anterior ni de WMS-D depende de esto.

**Files:**
- Create: `src/Modules/Wms/Modules.Wms.Application/Warehousing/RenderLocationLabels.cs`
- Create: `src/Modules/Wms/Modules.Wms.Infrastructure/Pdf/location-label.typ` (embebido en el csproj), `Pdf/LocationLabelRenderer.cs`
- Create: `src/Bootstrapper/WmsPdfGateway.cs`
- Modify: `WmsPorts.cs` (`IWmsLabelRenderer`, `IWmsPdfGateway`, `LocationLabel`), `LocationEndpoints.cs`, `WmsInfrastructureExtensions.cs`, `QepServiceCollectionExtensions.cs` (handler + gateway singleton con `HttpClient { Timeout = 30 s }` e `IOptions<QuotationsOptions>`, como `AddPdfRenderer`)
- Test: `tests/Modules/Wms/Modules.Wms.UnitTests/LocationLabelRendererTests.cs`, `tests/Modules/Wms/Modules.Wms.IntegrationTests/LocationLabelApiTests.cs`

**Interfaces:**
- `public sealed record LocationLabel(string Code, string Path, string WarehouseCode);` `public interface IWmsLabelRenderer { Task<byte[]> RenderLocationLabelsAsync(IReadOnlyList<LocationLabel> labels, CancellationToken ct); }`
- `public interface IWmsPdfGateway { Task<byte[]> RenderAsync(string source, object data, string fileName, CancellationToken ct); }` — `WmsPdfGateway` en Bootstrapper hace el `POST {Quotations:Pdf:BaseUrl}/pdf` con `X-Api-Key` (copia de `QCodePdfRenderer.RenderAsync` sin `assets`); no-2xx → `WmsDomainException(LabelRenderFailed, …)`.
- `LocationLabelRenderer(IWmsPdfGateway gateway) : IWmsLabelRenderer` lee el `.typ` embebido y manda `data = { labels: [{ code, path, warehouseCode }] }`, `fileName = "etiquetas-{warehouseCode}.pdf"`.
- `RenderLocationLabelsCommand(Guid TenantId, Guid WarehouseId, IReadOnlyList<Guid> LocationIds) : ICommand<LocationLabelsPdf>`; `LocationLabelsPdf(byte[] Content, string FileName)`; validador 1..500 ids; un id que no es de la bodega → `wms.location.not_found` (422 vía `WmsDomainException`, no 404: es el cuerpo el que está mal). Ruta `POST /warehouses/{id:guid}/locations/labels` → `Results.File(content, "application/pdf", fileName)`.
- Plantilla (validada contra `qcode-pdf` el 2026-10-31):

```typst
#import "@preview/tiaoma:0.3.0": barcode
#set page(width: 60mm, height: 30mm, margin: 3mm)
#set text(font: "Liberation Sans")
#let d = json(sys.inputs.data)
#for item in d.labels [
  #align(center)[#barcode(item.code, "Code128", width: 50mm, height: 14mm)]
  #align(center)[#text(size: 9pt, weight: "bold")[#item.code]]
  #align(center)[#text(size: 7pt)[#item.path]]
  #pagebreak(weak: true)
]
```

- [ ] **Step 1: Pruebas (RED)**

```csharp
// LocationLabelRendererTests: un gateway falso captura (source, data, fileName); source contiene "@preview/tiaoma" y "Code128";
// data serializa dos labels; fileName "etiquetas-BOG.pdf".
// LocationLabelApiTests: configureServices sustituye IWmsLabelRenderer por uno que devuelve "%PDF-1.7" en bytes;
// POST con los ids de RECEIVING y REC-01 → 200 application/pdf; un id de otra bodega → 422 wms.location.not_found; sin permiso → 403.
```

- [ ] **Step 2: RED**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~LocationLabelRendererTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LocationLabelApiTests"
```

- [ ] **Step 3: Implementar** (el handler recibe `IExecutionContext, WmsAuthorization, IWarehouseRepository, IWmsLabelRenderer, IValidator<…>`; `WmsLayerTests` sigue prohibiendo que el módulo vea `Quotations`: la clave llega por el gateway de Bootstrapper)

- [ ] **Step 4: GREEN + `CompositionRootTests` + `WmsLayerTests`**

```powershell
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --filter "FullyQualifiedName~LocationLabelRendererTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --filter "FullyQualifiedName~LocationLabelApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~WmsLayerTests"
```

- [ ] **Step 5: Prueba manual contra el servicio real** (una vez, con la clave de `Quotations:Pdf:ApiKey` del developer en user-secrets; nunca imprimirla): `POST …/labels` desde Scalar y abrir el PDF. Si `@preview/tiaoma` no resuelve en producción, el síntoma es 422 `wms.label.render_failed` con `details` del compilador; anotarlo en el handoff.

- [ ] **Step 6: Commit** — `feat(wms): render Code 128 location labels through qcode-pdf`

---

### Task 14: README y comentarios de módulo

**Files:**
- Modify: `README.md` — sección nueva «Módulo Wms (bodegas)» antes de «Verificación»: qué es (fase 1 de `docs/superpowers/specs/2026-10-31-wms-arquitectura-design.md`), clave `wms` y cómo se prende (fila en `tenancy.tenant_modules`), los 16 permisos y que sólo `admin` los trae, las virtuales que nacen con la bodega y no se borran, el `Self` y las UoM que aparecen en el primer acceso, el contrato de `scannable` (`etag`/`If-None-Match`), las etiquetas por `qcode-pdf` con la misma clave de cotizaciones, y P-1 (los identificadores de ubicación no se escriben).

- [ ] **Step 1: Escribir la sección** (≤ 60 líneas, en el tono del resto del README).
- [ ] **Step 2: Commit** — `docs(wms): describe the Wms module in the README`

---

### Task 15: Verificación final y handoff

- [ ] **Step 1: Formato sobre lo nuevo**

```powershell
dotnet format Backend.slnx --include src/Modules/Wms tests/Modules/Wms tests/ArchitectureTests/ArchitectureTests/WmsLayerTests.cs src/Bootstrapper/WmsCatalogReader.cs src/Bootstrapper/WmsGeographyReader.cs src/Bootstrapper/WmsPdfGateway.cs
git status --short
git diff --stat
```

**Antes de commitear, mirar qué tocó.** El `dotnet format` de este repo quita el BOM y la baseline tiene miles de diagnósticos `ENDOFLINE` por CRLF: si `git status --short` lista algo fuera de los archivos nuevos del Wms (otro módulo, `Bootstrapper`, un `.csproj`) o `git diff --stat` muestra archivos con cientos de líneas cambiadas sin cambio de código, es churn de BOM/CRLF y **no se commitea**: `git checkout -- <esos archivos>` y se repite el `format` sólo con `--include` de lo nuevo. Un diff de BOM se reconoce porque la única línea cambiada es la primera. Commitear lo que quede: `style(wms): apply dotnet format`.

- [ ] **Step 2: Restore en modo bloqueado y build**

```powershell
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: sin `NU1004`, `0 Error(s)`. Si el restore falla, `dotnet restore Backend.slnx --force-evaluate` y commitear los locks.

- [ ] **Step 3: Proyectos afectados, uno por uno**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Modules/Wms/Modules.Wms.UnitTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Wms/Modules.Wms.IntegrationTests --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build
```

Esperado: todo verde. Una roja en Tenancy que no nombre `wms`/módulos se compara por nombre contra `develop` antes de atribuirla a esta rama.

- [ ] **Step 4: Arranque local y humo**

```powershell
Stop-Process -Name Api -ErrorAction SilentlyContinue
dotnet run --project src/Api
```

Con el stub (`$env:Authentication__UseDevelopmentStub = "true"` y `--no-launch-profile` si hace falta) y `X-Permissions` con los `wms.*`: prender `wms` al tenant del stub (`INSERT` en `tenancy.tenant_modules`), `POST /warehouses`, `GET …/locations`, `GET …/scannable`, `GET /owners`, `GET /uoms`, `GET /variant-profiles?variantIds=<una del catálogo>`.

- [ ] **Step 5: Handoff**

Empujar la rama y entregar la URL de compare contra `develop`. El handoff lista: el conteo de permisos (89), la migración de Tenancy, P-1 y P-3 como correcciones pendientes del spec, si Task 13 quedó dentro (y si no, la desviación de RF-004 según P-11), y que `docs/estado.md` se actualiza al mergear a `develop`. El frontend (`features/wms`, `tenant-modules.ts` con `wms` al final y la etiqueta «Bodegas», `ModuleGate modules={['wms','catalog']}`, sidebar **sin** `module` porque los permisos ya van enmascarados) es plan propio y se escribe desde § Contrato HTTP.

---

## Self-review

- **Cobertura del spec (corte WMS-B):** RF-001 T8 · RF-002 T4/T9 · RF-003 T4/T8 · RF-004 T13 · RF-005 (Self) T5/T6/T10 · RF-006 T5/T12 · RF-007 T5/T6/T12 · RF-008 T5/T6/T11 · RF-009 (variante) T5/T6/T12; (ubicación) P-1 · RF-010 T5/T12 (con el probe) · RF-100 T3 · RF-102 T6 y cada mutación · §4.1 regla de namespaces T1 · §11.2 índices T6 · §24.1 403 T7 · `scannable` T9 · clave de módulo T2. Fuera: ledger, scopes, sincronización por polling, frontend (declarado).
- **Tipos consistentes entre tareas:** `WarehouseId`/`LocationId`/`OwnerId`/`UomCode` (T4/T5) son los que mapea T6 y usan T8–T12; `Location.AllowsNegative` (T4, S1) es lo que WMS-D copia al saldo; `MemberScope` (T6) sólo existe para `wms.member_scopes`; `WarehouseDto`/`WarehousesDto`/`WarehouseDtoMapper` nacen en T7 y T8 los consume; `WmsAuthorization` (T6, firma S18) reemplaza al par `IExecutionContext, ITenantModules` en todos los handlers de T7–T13; `IWmsCatalogReader.GetVariantsAsync` y `WmsVariantSnapshot` (T6, con su adaptador) son los que prueba T7 y consume T12; `IVariantStockProbe`/`ILocationStockProbe`/`IOwnerStockProbe` (T6) los usan T12, T9 y T10; `ForeignKeyViolation.Is` (T6) traduce `FK_locations_parent`; `IWmsAuditRecorder.Record` con `WmsActorType` (T6, P-7/S23) en todos los handlers; los comandos de T8–T12 van aplanados (S25) y sus validadores leen el comando; `WmsErrorCodes` nace en T1 con la lista completa (incluye `LocationVirtualImmutable`, `OwnerHasStock`, `LocationCapacityInvalid`, `IdentifierValueInvalid`).
- **Cada tarea tiene RED con `--filter`, GREEN con `--filter` y commit**; las de integración fijan `$env:Seed__Enabled = "false"` en línea aparte. Ningún paso dice sólo «implementar» sin que la lista de Interfaces de esa tarea diga qué.
- **Review Focus → pruebas:** 1 → T3 y el `CompositionRootTests` de cada tarea; 2 → T1 paso 5, T15 paso 2; 3 → T2 pasos 4–6; 4 → T4 `MovingANodeRewritesTheDescendantPaths`, `RenamingAZoneRewritesTheDescendantPaths`, `MovingANodeUnderItsOwnDescendantIsRejected`, T9 `MovingALocationUpdatesTheChildrenPaths`; 5 → T9 `TheScannableEtagIsStableUntilALocationChanges`, `IfNoneMatchWithTheCurrentEtagIs304`.
- **Proporción:** firmas, nombres de prueba y aserciones; cuerpos sólo para la plantilla Typst y la prueba de namespaces, que el implementador no deduce de la firma.
