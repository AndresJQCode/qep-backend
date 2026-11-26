# Compras, fase 1 (orden de compra a proveedor externo) — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un módulo `Purchases` nuevo con órdenes de compra a un proveedor externo del directorio (`Supplier`): borrador
editable, enviar, confirmar a mano con ajustes por línea, recibir, cancelar, lista paginada, PDF, auditoría, permisos y
clave de módulo `purchases`.

**Architecture:** Cuatro proyectos `Modules.Purchases.{Domain,Application,Infrastructure,Api}` con la forma de `Loans`
(agregado con líneas e historial, `If-Match` en el endpoint, auditoría atómica en `audit.entries` desde el `DbContext` del
módulo) y la del directorio de `Suppliers` (paginación con `Total`, `SupplierPaging`). Application sólo referencia
`Modules.Tenancy.*`: proveedor, catálogo y nombres de miembros entran por puertos `IPurchases*` con adaptadores en
`src/Bootstrapper/`. El número `OC-0001` sale de un contador por tenant (`UPDATE … RETURNING`, copia de
`PosSaleNumberGenerator`) dentro de la transacción del alta. El PDF se arma contra `qcode-pdf` con sección y clave propias
(`Purchases:Pdf`, precedente `Support:Pdf`) y una plantilla Typst propia.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers, `qcode-pdf` (Typst).

**Spec:** `docs/superpowers/specs/2026-11-17-compras-y-mayorista-design.md` (§«Módulo `purchases`», «Proveedor externo»,
«Permisos», «Errores»). El alcance y el contrato HTTP de esta fase los fijó el orquestador y se copian abajo tal cual
(«Contrato HTTP»); donde el spec y el contrato difieren, manda el contrato. El plan del frontend cita esa sección.

**Rama:** `feature/compras-y-mayorista`, en el worktree `qep-backend-worktrees/compras-mayorista` (ya existe). Antes de
**cada** tarea y de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para.
Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea. Si el RED esperado es de
  compilación, la evidencia es la salida de `dotnet build`/`dotnet test` sin levantar contenedores.
- Conventional commits, **sin atribución de IA** (sin `Co-Authored-By`; la regla del repo pisa cualquier recordatorio del harness).
- Presupuesto de pruebas (workspace, owner 2026-11-12): tres bloques (A: tareas 1-3, B: 4-5, C: 6-8), un implementador y una
  revisión por bloque. Mientras iteras, integración filtrada **por nombre de prueba**
  (`--filter "FullyQualifiedName~<Prueba>"`); la clase completa una vez al final de su bloque. Antes de una corrida de
  integración, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`: si hay otra, espera; nunca la detengas. Corridas
  en primer plano. La suite de los proyectos tocados, sólo al final (Task 8).
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: avísale al developer, no lo detengas tú si no lo lanzaste.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `PurchasesAuthorization.EnsureAuthorized` en
  el handler antes de tocar un puerto. Otro tenant → 403 `authorization.denied`. Todo método de puerto y repositorio recibe
  `tenantId`.
- Permiso nuevo = constante + rol `admin` + `PermissionDefinition` (con `Shape`) + `AddPolicy` en
  `QepServiceCollectionExtensions`. Sin la política el síntoma es 500. Los handlers se registran a mano; sin registro, 500.
- `Modules.Purchases.Application` referencia sólo `Modules.Tenancy.*` entre los módulos de negocio (`PurchasesLayerTests`).
- Ningún status HTTP a mano: 404 `ResourceNotFoundException`, 409 `ResourceConflictException`, 412
  `RequestConcurrencyException`, 428 `PreconditionRequiredException`, 422 `DomainException`/`ValidationException`
  (`src/Api/ApiExceptionHandler.cs:162-189`).
- Montos: precisión `(14,2)`, cantidades `(10,2)` (las de `QuotationsDbContext.cs:115-123`, `:371`). Redondeo de cada
  subtotal e impuesto de línea a 2 decimales, `MidpointRounding.AwayFromZero` (`DocumentTotals.cs:89`). **Costos sin IVA; el
  IVA se suma en los totales** (decisión del orquestador; va en el `<summary>` del DTO).
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma. Enums por nombre (`Draft`, `Rejected`). Comentarios y
  textos al usuario en español colombiano tuteando; identificadores, códigos y rutas tal cual.
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/Purchases/Modules.Purchases.Infrastructure --context PurchasesDbContext -o Persistence/Migrations`.
  Todas aditivas (expand/contract): esquema nuevo `purchases`, y en Tenancy sólo se ensancha el `CHECK`.
- Cada csproj nuevo lleva su `packages.lock.json` (`dotnet restore --force-evaluate` del proyecto). Sin paquetes nuevos:
  `Directory.Packages.props`, `global.json` y `.mcp.json` no se tocan.
- `docs/estado.md` **no se toca** en esta rama: se actualiza en `develop` al mergear.
- YAGNI de fase 1: sin `LinkedOrderId`, `LineStatus.Added`, `CreatedInCatalog`, outbox de integración, WhatsApp ni nada de
  `wholesale`. Ninguna columna para fases futuras.

## Decisiones de este plan (no del owner ni del contrato)

Cada una se puede revertir sin tocar el contrato. Las marcadas (P) son decisión pendiente del owner.

| # | Decisión | Por qué |
|---|---|---|
| D-1 | 409 de estado: el handler compara con `order.AllowedActions()` **antes** de llamar al dominio y lanza `ResourceConflictException` (`not_draft` para editar/borrar, `invalid_transition` para send/confirm/receive/cancel). El dominio repite la guarda con `PurchasesDomainException` del mismo código (422) como última red | El mapeo es por tipo y el dominio no ve `BuildingBlocks.Application`; mismo patrón que `CampaignErrors.NotEditable()` (`Campaigns.cs:278`) |
| D-2 | El contrato reemplaza `purchases.order.not_cancellable` del spec por `invalid_transition` | El contrato manda |
| D-3 | `supplierId` inexistente en el tenant → 404 `suppliers.supplier.not_found` (código existente de `SupplierNotFound`) | El contrato no lo cubre; no se inventa un código (P) |
| D-4 | `send` aparece en `allowedActions` aunque el proveedor esté inactivo (la pantalla lo explica con `supplier.isActive`; el `POST` responde 422). Que sin `purchases.order.manage` llegue `[]` ya es contrato (adenda) | La regla de estado no depende de otro módulo |
| D-5 | Totales: una línea usa los valores confirmados si `ConfirmedQuantity` no es nulo (así una orden confirmada y luego cancelada sigue mostrando lo confirmado); `requestedTotal` siempre con los pedidos | Generaliza «confirmados si status ≥ Confirmed» sin un caso especial para `Cancelled` |
| D-6 | La línea exige que la variante exista en el catálogo del tenant **en cualquier estado** (activa o no) | Comprar para un producto aún no publicado es legítimo (P) |
| D-7 | `PUT` reemplaza las líneas (ids nuevos) y vuelve a tomar código, nombre y tasa actuales del catálogo; exige proveedor activo igual que el alta | Es «igual que POST» en el contrato |
| D-8 | Variantes repetidas en `lines` → 422 `validation.failed` en `Lines[i].VariantId`; tope 200 líneas; `notes` ≤ 2000 | Sin tope, un cuerpo enorme pasa |
| D-9 | `confirmedQuantity` ≥ 0 (0 = `Rejected`) y `confirmedUnitCost` > 0 siempre, también en la rechazada | La pantalla precarga el costo pedido |
| D-10 | Borrar un `Draft` es definitivo y deja un hueco en el consecutivo | Igual que cualquier contador con `UPDATE … RETURNING` |
| D-11 | `search` busca en `number` (ILIKE) **o** en el nombre del proveedor (ids por `IPurchasesSupplierLookup.SearchIdsAsync`, tope 200) | El nombre vive en otro esquema; no se copia a la orden |
| D-12 | `status` desconocido en la lista → 422 `validation.failed` con `errors["Status"]` (validador del query), no un código nuevo | El contrato ya prevé el 422 de validación |
| D-13 | Orden de la lista: `created_at DESC, id DESC`; `pageSize` por defecto 50, tope 200 (copia de `SupplierPaging`) | «Igual que `GET /suppliers`» |
| D-14 | (PDF en cualquier estado ya es contrato.) Sección `Purchases:Pdf` y clave propia `PURCHASES_PDF_API_KEY` (precedente `SupportPdfOptions.cs:4-6`) | Compartir la clave mezcla el tráfico en los logs de `qcode-pdf`. Darla de alta en `Backend-prod` es del owner (P) |
| D-15 | Las líneas de compra **no** bloquean borrar un producto (`CatalogVariantUsageChecker` no cambia): guardan código, nombre y tasa | Editar ese borrador después responde `line_not_in_catalog`, que es lo correcto |
| D-16 | Auditoría atómica en `audit.entries` (camino de `LoansAuditPublisher`), acciones `purchases.order.{created,updated,deleted,sent,confirmed,received,cancelled}`, `resourceType` `purchase_order` | «Sin eventos de outbox» en fase 1 |
| D-18 | Las cuentas de `counts` salen de una sola consulta `GROUP BY status` con los mismos filtros de `supplierId` y `search`, en el repositorio; la pantalla nunca suma | BFF, adenda del contrato |
| D-19 | `POST /preview` y `POST /` comparten el armado de líneas (`PurchaseOrderLineBuilder`) y la aritmética del dominio (`PurchaseOrderMath`): el total de la vista previa y el del alta no pueden divergir | Una sola regla de redondeo |
| D-17 | La moneda se valida contra `Currencies` (catálogo global, `Currencies.cs:24-29`): fuera de él → 422 `purchases.order.invalid_currency` desde el handler, no desde el validador | El contrato da el código |

## Review Focus

1. **Dos altas a la vez en el mismo tenant.** Dos `POST /` simultáneos reciben `OC-0001` y `OC-0002`, nunca el mismo
   número ni un 500 de índice único. Prueba: `TwoConcurrentCreatesGetConsecutiveNumbers` (Task 6).
2. **Un alta rechazada no gasta número.** `POST /` con una variante de otro tenant → 422 `line_not_in_catalog`; la
   siguiente alta válida sale `OC-0001`. Prueba: `ARejectedCreateDoesNotSpendANumber` (Task 6).
3. **La tasa del producto cambia después de guardar la línea.** El detalle sigue con la tasa vieja (snapshot); un `PUT` del
   borrador toma la nueva (D-7). Prueba: `TheLineKeepsItsTaxRateUntilTheDraftIsSaved` (Task 6).
4. **Confirmar con el cuerpo incompleto o con ids ajenos.** Falta una línea, una repetida o un `lineId` de otra orden → 422
   `confirm_lines_mismatch` y la orden queda `Sent` con la misma `version`. Prueba: `ConfirmWithMismatchedLinesIs422AndChangesNothing` (Task 6).
5. **Proveedor desactivado entre el alta y el envío.** `send` → 422 `supplier_inactive` y la orden sigue `Draft`;
   `cancel` sí funciona. Prueba: `SendingToADeactivatedSupplierIs422AndLeavesTheDraft` (Task 6).

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Purchases/Modules.Purchases.Domain/{Modules.Purchases.Domain.csproj, PurchaseOrder.cs, PurchaseOrderId.cs, PurchaseOrderLine.cs, PurchaseOrderHistoryEntry.cs, PurchaseOrderStatus.cs, PurchaseOrderLineStatus.cs, PurchaseOrderAction.cs, PurchaseOrderTotals.cs, PurchasesDomainException.cs, PurchasesErrorCodes.cs}` | Crear | Agregado, estados, totales |
| `src/Modules/Purchases/Modules.Purchases.Application/{….csproj, PurchasesPermissions.cs, PurchasesAuthorization.cs, PurchasesDtos.cs, PurchaseOrderComposer.cs, PurchaseOrderPaging.cs, PurchaseOrderWriteRules.cs, PurchasesConflicts.cs, PurchaseOrderLineBuilder.cs, CreatePurchaseOrder.cs, PreviewPurchaseOrder.cs, UpdatePurchaseOrder.cs, DeletePurchaseOrder.cs, GetPurchaseOrder.cs, ListPurchaseOrders.cs, TransitionPurchaseOrder.cs, ConfirmPurchaseOrder.cs, GetPurchaseOrderPdf.cs, PurchaseOrderPdfDocument.cs, Ports.cs, PurchasesAuditActions.cs}` | Crear | Casos de uso, puertos, contrato |
| `src/Modules/Purchases/Modules.Purchases.Infrastructure/{….csproj, PurchasesInfrastructureExtensions.cs, PurchasesDatabaseInitializer.cs, Persistence/PurchasesDbContext.cs, Persistence/PurchasesDbContextFactory.cs, Persistence/PurchaseOrderRepository.cs, Persistence/PurchaseOrderNumberGenerator.cs, Persistence/PurchasesUnitOfWork.cs, Persistence/PurchasesAuditPublisher.cs, Persistence/Migrations/*_InitialPurchases.cs, Pdf/PurchasesPdfOptions.cs, Pdf/PurchasesPdfOptionsValidator.cs, Pdf/PurchaseOrderPdfRenderer.cs, Pdf/purchase-order.typ}` | Crear | Persistencia, número, PDF |
| `src/Modules/Purchases/Modules.Purchases.Api/{….csproj, PurchaseOrderEndpoints.cs}` | Crear | Rutas del contrato |
| `src/Bootstrapper/{PurchasesSupplierLookup.cs, PurchasesCatalogLookup.cs, PurchasesMemberDirectory.cs, PurchasesUserReferenceProbe.cs}` | Crear | Adaptadores |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers (junto a Loans, `:434-477`), validadores (`:1324`), `AddPurchasesInfrastructure` (`:1343`), sonda (`:1730`), adaptadores (`:1735`), rol admin (`:2207`), definiciones (después de `:3166`), políticas (después de `:3678`) |
| `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Api.csproj`, `Backend.slnx`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` | Modificar | Referencias y solución |
| `src/Api/Program.cs` | Modificar | `app.MapPurchaseOrderEndpoints();` junto a `:395-399`; `InitializePurchasesDatabaseAsync` después de Store (`:515`) |
| `src/Api/appsettings.json` (`:162` patrón), `k8s/prod-configMap.yaml` (`:110` patrón), `k8s/prod-secret.yaml` (`:44` patrón) | Modificar | `Purchases:Pdf` |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`, `…Infrastructure/Persistence/TenancyDbContext.cs:151`, `…/Migrations/*_AddPurchasesModuleKey.cs` | Modificar / Crear | Clave `purchases` |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/{TenantModuleKeysTests.cs, OperatorReadHandlerTests.cs}`, `…IntegrationTests/{TenantModulesApiTests.cs, OperatorConsoleApiTests.cs, SeedStartupTests.cs}` | Modificar | Listas literales de claves |
| `tests/Modules/Purchases/Modules.Purchases.UnitTests/{….csproj, PurchaseOrderTests.cs, PurchaseOrderTotalsTests.cs, PurchaseOrderWriteRulesTests.cs, PurchaseOrderComposerTests.cs, PurchaseOrderLineBuilderTests.cs, PurchaseOrderPdfMapperTests.cs, PurchasesPdfOptionsValidatorTests.cs}` | Crear | Dominio, validación, armado |
| `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/{….csproj, PurchasesApiHarness.cs, SharedPurchasesHost.cs, PurchaseOrderWriteApiTests.cs, PurchaseOrderListApiTests.cs, PurchaseOrderTransitionApiTests.cs, PurchasesAccessApiTests.cs, PurchaseOrderPdfApiTests.cs, PurchasesUserReferenceProbeTests.cs}` | Crear | API contra PostgreSQL |
| `tests/ArchitectureTests/ArchitectureTests/{PurchasesLayerTests.cs, CompositionRootTests.cs, MultiReplicaInventory.cs}` | Crear / Modificar | Capas, 89 permisos, singleton del PDF |
| `README.md` (`:854-856` módulos; tabla de endpoints junto a `:1345`), `CLAUDE.md:305-306` | Modificar | Documentación |

**Comandos de prueba** (PowerShell, desde la raíz del worktree):

```powershell
dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~<Prueba>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~<Prueba>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

---

## Contrato HTTP

Copia literal del contrato fijado por el orquestador. No se cambia en este plan; las precisiones del backend van en la
subsección siguiente y no lo contradicen.

### Contrato (verbatim)

Base `/api/v1/tenants/{tenantId}/purchases/orders`.
Lectura: `purchases.order.read`. Escritura: `purchases.order.manage`. Escrituras sobre una orden existente llevan `If-Match` con `version` (412 si no coincide, 428 si falta), igual que el resto de la API.

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /` `?status=&supplierId=&search=&page=&pageSize=` | — | sobre de paginación igual al de `GET /suppliers`, ítems `PurchaseOrderSummary` |
| `GET /{id}` | — | `PurchaseOrderDetail` |
| `POST /` | `{ supplierId, currency, notes?, lines: [{ variantId, quantity, unitCost }] }` | 201 `PurchaseOrderDetail` (Draft) |
| `PUT /{id}` | igual que POST | `PurchaseOrderDetail` (sólo Draft) |
| `DELETE /{id}` | — | 204 (sólo Draft) |
| `POST /{id}/send` | — | `PurchaseOrderDetail` |
| `POST /{id}/confirm` | `{ lines: [{ lineId, confirmedQuantity, confirmedUnitCost }] }` (todas las líneas) | `PurchaseOrderDetail` |
| `POST /{id}/receive` | — | `PurchaseOrderDetail` |
| `POST /{id}/cancel` | — | `PurchaseOrderDetail` |
| `GET /{id}/pdf` | — | `application/pdf` (cualquier estado) |
| `POST /preview` | igual que POST `/` | `{ lines: [{ variantId, code, name, taxRatePercent, subtotal }], totals: { subtotal, tax, total } }` — no escribe; permiso `purchases.order.manage`; mismas validaciones y códigos que POST |

Adenda (2026-11-17, tras el plan del frontend):
- La respuesta de `GET /` agrega `counts: { Draft, Sent, Confirmed, Received, Cancelled, all }` con los filtros aplicados salvo `status`.
- `allowedActions` ya considera los permisos del miembro: sin `purchases.order.manage` llega vacío. El frontend no vuelve a chequear permisos para los botones.
- La nueva orden y la edición piden `POST /preview` (con debounce) para pintar subtotal, IVA y total; el navegador nunca suma.

`PurchaseOrderSummary`: `{ id, number, status, confirmedWithChanges, supplier: { id, name }, currency, total, createdAt, lineCount }`

`PurchaseOrderDetail`:
```
{ id, number, status, confirmedWithChanges, version,
  supplier: { id, name, isActive },
  currency, notes, createdBy: { memberId, name }, createdAt,
  lines: [{ id, variantId, code, name, quantity, confirmedQuantity|null,
            unitCost, confirmedUnitCost|null, taxRatePercent,
            lineStatus: 'Pending'|'Unchanged'|'Changed'|'Rejected', subtotal }],
  totals: { subtotal, tax, total },          // confirmados si status>=Confirmed, si no los pedidos
  requestedTotal,                           // total original pedido
  history: [{ status, at, actorName }],
  allowedActions: ('edit'|'delete'|'send'|'confirm'|'receive'|'cancel')[] }
```
Todo cálculo (subtotales, IVA, totales, allowedActions) lo hace el backend (BFF). Montos como decimales JSON.

Errores (ProblemDetails con `code`):
- 409 `purchases.order.not_draft`, `purchases.order.invalid_transition`
- 422 `purchases.order.empty`, `purchases.order.line_not_in_catalog`, `purchases.order.supplier_inactive`, `purchases.order.invalid_currency`, `purchases.order.confirm_lines_mismatch`
- 404 `purchases.order.not_found`
- 422 de validación (FluentValidation) con mapa `errors` para cantidades/costos negativos o cero.

### Precisiones del backend

- Sobre de la lista: `{ items, total, page, pageSize, counts }` (forma de `SuppliersResponse`, `SuppliersDtos.cs:75`, más
  `counts`); `counts` trae las seis claves siempre, también en cero, con `supplierId` y `search` aplicados y sin `status`; `all`
  es la suma de las cinco. `page` desde 1,
  `pageSize` 50 por defecto y tope 200; orden `createdAt` descendente (D-13). `status` es uno de
  `Draft|Sent|Confirmed|Received|Cancelled` sin importar mayúsculas; otro valor → 422 `validation.failed` con `errors.Status`
  (D-12). `search` busca en número y nombre del proveedor (D-11).
- `PurchaseOrderSummary.total`: misma regla que `totals.total` del detalle (D-5).
- `history` va en orden cronológico, desde el `Draft` del alta; `actorName` es el nombre visible de la membresía o
  `"Sin nombre"`.
- `allowedActions` por estado (con `purchases.order.manage`; sin él, `[]`, D-4): `Draft` → `edit, delete, send, cancel`;
  `Sent` → `confirm, cancel`; `Confirmed` → `receive, cancel`; `Received` y `Cancelled` → `[]`. Siempre en ese orden.
- `lineStatus`: `Pending` sin confirmar; `Rejected` con `confirmedQuantity` 0; `Unchanged` si cantidad y costo confirmados
  son iguales a los pedidos; si no, `Changed`. `confirmedWithChanges` es `true` si alguna línea no quedó `Unchanged`.
- `subtotal` de línea = cantidad × costo (confirmados si existen), redondeado a 2 decimales, **sin IVA**. `totals.tax` = suma
  de `round(subtotal × taxRatePercent / 100, 2)` por línea; `totals.total` = `subtotal + tax`.
- Claves del 422 de validación: `Lines[0].Quantity`, `Lines[0].UnitCost`, `Lines[1].VariantId` (repetida), `Notes`,
  `Lines[0].ConfirmedQuantity`, `Lines[0].ConfirmedUnitCost`. `lines: []` no es error de validación: es 422
  `purchases.order.empty`.
- `supplierId` que no existe en el tenant → 404 `suppliers.supplier.not_found` (D-3).
- `send`, `receive` y `cancel` van sin cuerpo; las cinco escrituras sobre una orden existente (`PUT`, `DELETE`, `send`,
  `confirm`, `receive`, `cancel`) exigen `If-Match` (`"7"` o `W/"7"`, como `CatalogIfMatch`).
- `GET /{id}/pdf` responde el archivo con `Content-Disposition: attachment; filename=OC-0001.pdf`, en cualquier estado (D-14).
- `POST /preview` responde 200 y corre las mismas reglas y en el mismo orden que `POST /` (validación, moneda, proveedor, `empty`,
  `line_not_in_catalog`), sin transacción, sin número y sin auditoría. Las líneas vuelven en el orden del cuerpo; `subtotal` de
  línea sin IVA; `taxRatePercent` es la tasa actual del producto (la que el alta guardaría).

---

## Bloque A — dominio, clave de módulo y permisos (sin contenedores salvo Tenancy)

### Task 1: Proyectos del módulo y agregado `PurchaseOrder`

**Files:**
- Create: los cuatro csproj (copiar de `src/Modules/Loans/Modules.Loans.*/*.csproj` cambiando nombres; Application con
  `InternalsVisibleTo Modules.Purchases.UnitTests`), entradas en `Backend.slnx` (carpeta `/src/Modules/Purchases/` y
  `/tests/Modules/Purchases/`, junto a las de Loans `:167-175`), `Bootstrapper.csproj` (Application + Infrastructure, junto a
  `:20-21`), `Api.csproj` (Api + Infrastructure, junto a `:21-22`), `ArchitectureTests.csproj` (los cuatro, junto a `:49-52`)
- Create: todos los archivos de `Modules.Purchases.Domain` del mapa; `PurchasesPermissions.cs` vacío-con-constantes (Task 3 la
  usa), `PurchasesInfrastructureExtensions.cs` y `PurchaseOrderEndpoints.cs` como clases estáticas vacías para que la prueba de
  capas corra desde ya
- Create: `tests/Modules/Purchases/Modules.Purchases.UnitTests/{Modules.Purchases.UnitTests.csproj (copia de Loans.UnitTests), PurchaseOrderTests.cs, PurchaseOrderTotalsTests.cs}`
- Create: `tests/ArchitectureTests/ArchitectureTests/PurchasesLayerTests.cs` (copia de `LoansLayerTests.cs` con anclas
  `PurchaseOrder`, `PurchasesPermissions`, `PurchasesInfrastructureExtensions`, `PurchaseOrderEndpoints`)

**Interfaces:**
- Produces (namespace `Modules.Purchases.Domain`):
  - `readonly record struct PurchaseOrderId(Guid Value)` con `New()` (`Guid.CreateVersion7()`).
  - `enum PurchaseOrderStatus { Draft, Sent, Confirmed, Received, Cancelled }`,
    `enum PurchaseOrderLineStatus { Pending, Unchanged, Changed, Rejected }`,
    `enum PurchaseOrderAction { Edit, Delete, Send, Confirm, Receive, Cancel }`.
  - `static class PurchasesErrorCodes` con `NotFound = "purchases.order.not_found"`, `NotDraft`, `InvalidTransition`, `Empty`,
    `LineNotInCatalog`, `SupplierInactive`, `InvalidCurrency`, `ConfirmLinesMismatch` (los ocho códigos del contrato).
  - `sealed class PurchasesDomainException(string code, string message) : DomainException`.
  - `sealed record PurchaseOrderLineDraft(Guid VariantId, string Code, string Name, decimal Quantity, decimal UnitCost, decimal TaxRatePercent)`.
  - `sealed record PurchaseOrderLineConfirmation(Guid LineId, decimal ConfirmedQuantity, decimal ConfirmedUnitCost)`.
  - `sealed record PurchaseOrderTotals(decimal Subtotal, decimal Tax, decimal Total)`.
  - `static class PurchaseOrderMath` (D-19, la única aritmética del módulo): `decimal Subtotal(decimal quantity, decimal unitCost)`
    (redondeo a 2, `AwayFromZero`), `decimal Tax(decimal subtotal, decimal taxRatePercent)` (ídem),
    `PurchaseOrderTotals Sum(IEnumerable<(decimal Subtotal, decimal Tax)> lines)`. La usan `PurchaseOrderLine`, `Totals()` y la
    vista previa.
  - `sealed class PurchaseOrderLine` (archivo `PurchaseOrderLine.cs`; `PurchaseOrderMath` vive en `PurchaseOrderTotals.cs`): `Guid Id`, `Guid VariantId`, `string Code`, `string Name`, `decimal Quantity`,
    `decimal UnitCost`, `decimal? ConfirmedQuantity`, `decimal? ConfirmedUnitCost`, `decimal TaxRatePercent`;
    `PurchaseOrderLineStatus Status { get; }`, `decimal Subtotal { get; }` (efectivo, D-5), `decimal RequestedSubtotal { get; }`,
    `decimal Tax { get; }`, `decimal RequestedTax { get; }`.
  - `sealed record PurchaseOrderHistoryEntry(PurchaseOrderStatus Status, DateTimeOffset OccurredAt, Guid MemberId)` (con
    `int Sequence` para el orden estable en base).
  - `sealed class PurchaseOrder`: `PurchaseOrderId Id`, `Guid TenantId`, `string Number`, `Guid SupplierId`, `string Currency`,
    `string? Notes`, `PurchaseOrderStatus Status`, `bool ConfirmedWithChanges`, `Guid CreatedByMemberId`,
    `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`, `long Version`, `IReadOnlyList<PurchaseOrderLine> Lines`,
    `IReadOnlyList<PurchaseOrderHistoryEntry> History`; `const int NotesMaxLength = 2000`, `MaxLines = 200`.
    - `static PurchaseOrder Create(Guid tenantId, string number, Guid supplierId, string currency, string? notes, IReadOnlyList<PurchaseOrderLineDraft> lines, Guid memberId, DateTimeOffset now)` → `Draft`, `Version = 1`, una entrada `Draft` en el historial.
    - `void Replace(Guid supplierId, string currency, string? notes, IReadOnlyList<PurchaseOrderLineDraft> lines, DateTimeOffset now)` — sólo `Draft`, ids de línea nuevos.
    - `void Send(Guid memberId, DateTimeOffset now)`, `void Confirm(IReadOnlyList<PurchaseOrderLineConfirmation> lines, Guid memberId, DateTimeOffset now)`, `void Receive(Guid memberId, DateTimeOffset now)`, `void Cancel(Guid memberId, DateTimeOffset now)`.
    - `void EnsureDeletable()` (sólo `Draft`, si no `not_draft`).
    - `IReadOnlyList<PurchaseOrderAction> AllowedActions()` (por estado, en el orden de las precisiones del contrato).
    - `PurchaseOrderTotals Totals()` y `PurchaseOrderTotals RequestedTotals()`.
    - Toda mutación sube `Version` en 1, fija `UpdatedAt` y (las transiciones) agrega una entrada al historial. La moneda se
      guarda `Trim().ToUpperInvariant()` y sólo se valida la forma (3 letras): el catálogo lo valida Application (D-17).

- [ ] **Step 1: Crea proyectos, referencias y entradas de la solución; `dotnet build src/Api` compila con el módulo vacío.**

- [ ] **Step 2: Escribe las pruebas de dominio que fallan**

```csharp
// PurchaseOrderTests.cs
CreateStartsAsDraftWithVersionOneAndADraftHistoryEntry      // Status Draft; Version 1; History == [(Draft, now, memberId)]
CreateWithoutLinesThrowsEmpty                                // code "purchases.order.empty"
CreateTrimsNotesAndTurnsBlankIntoNull                        // "  " → null; " ok " → "ok"
CreateUppercasesTheCurrency                                  // "cop" → "COP"
ReplaceOnADraftSwapsLinesAndBumpsVersion                     // Version 2; Lines ids distintos a los anteriores
ReplaceOutsideDraftThrowsNotDraft                            // tras Send → "purchases.order.not_draft"
EnsureDeletableOutsideDraftThrowsNotDraft
SendMovesDraftToSentAndAppendsHistory
ConfirmWithEveryLineUnchangedLeavesConfirmedWithChangesFalse
ConfirmWithAChangedCostMarksTheLineChangedAndTheOrderWithChanges
ConfirmWithZeroQuantityMarksTheLineRejected                  // Status Rejected; ConfirmedWithChanges true
ConfirmWithAMissingDuplicatedOrForeignLineThrowsMismatch     // [Theory] tres casos → "purchases.order.confirm_lines_mismatch"; Status sigue Sent y Version no cambia
ReceiveOnlyFromConfirmed                                     // desde Sent → "purchases.order.invalid_transition"
CancelFromDraftSentAndConfirmedSucceeds                      // [Theory]
CancelFromReceivedOrCancelledThrowsInvalidTransition         // [Theory]
SendFromSentThrowsInvalidTransition
AllowedActionsFollowTheState                                 // [Theory] Draft → [Edit, Delete, Send, Cancel]; Sent → [Confirm, Cancel]; Confirmed → [Receive, Cancel]; Received/Cancelled → []
// PurchaseOrderTotalsTests.cs
TotalsAddTaxOnTopOfTheCostWithoutTax                         // 3 × 10.00 al 19 % y 1 × 5.55 al 0 % → (35.55, 5.70, 41.25)
LineTaxIsRoundedAwayFromZeroPerLine                          // PurchaseOrderMath.Tax(0.25m, 10m) == 0.03m
SumOfAnEmptyListIsZero                                       // PurchaseOrderMath.Sum([]) == (0, 0, 0)
TotalsUseConfirmedValuesOnceConfirmedAndRequestedStaysOriginal // pedido 2 × 10, confirmado 1 × 12 → Totals.Subtotal 12; RequestedTotals.Subtotal 20
ARejectedLineAddsNothingToTheConfirmedTotals
ACancelledAfterConfirmOrderKeepsTheConfirmedTotals           // D-5
```

- [ ] **Step 3: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrder"`
Expected: FAIL de compilación (`error CS0246: The type or namespace name 'PurchaseOrder' could not be found`).

- [ ] **Step 4: Implementa el dominio** con las firmas de arriba (la forma de `Loan.cs` para versión e historial).

- [ ] **Step 5: Corre y verifica que pasan**, más la prueba de capas

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrder"`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PurchasesLayerTests"`
Expected: PASS (las de dominio y las 5 de capas).

- [ ] **Step 6: Commit**

```powershell
git add Backend.slnx src/Modules/Purchases src/Bootstrapper/Bootstrapper.csproj src/Api/Api.csproj tests/Modules/Purchases tests/ArchitectureTests
git commit -m "feat(purchases): purchase order aggregate and module projects"
```

---

### Task 2: Clave de módulo `purchases` en Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs`: campo `Purchases = Define("purchases")` después de
  `Support` con `<summary>` «Spec 2026-11-17 (compras): órdenes de compra a proveedores. Depende de `Suppliers` y `Catalog`
  (la línea es una variante del catálogo, el proveedor sale del directorio) y queda fuera de `DefaultForNewTenants`: se prende
  por tenant»; `All` suma `Purchases` al final; `Dependencies` suma `[Purchases] = [Suppliers, Catalog]` con comentario.
- Modify: `…/Persistence/TenancyDbContext.cs:151` (suma `'purchases'` al final del `CHECK`).
- Create (ef): `…/Persistence/Migrations/*_AddPurchasesModuleKey.cs` con la forma exacta de `20261114230746_AddSupportModuleKey.cs`:
  `Up` recrea `CK_tenant_modules_module_key` con la lista completa (incluida `copilot`); `Down` hace
  `DELETE FROM tenancy.tenant_modules WHERE module_key = 'purchases';` y recrea el `CHECK` sin la clave.
- Test: `TenantModuleKeysTests.cs` (`:10-14` lista; `SupportIsLastHasNoDependenciesAndIsOffForNewTenants` `:131-136`),
  `OperatorReadHandlerTests.cs:68`, `TenantModulesApiTests.cs:102` (lista), `:104` y `:110` (los dos filtros `is not`/`is`),
  `:146` (`19` → `20`), `OperatorConsoleApiTests.cs:195` (`(7, 19, …)` → `(7, 20, …)`) y `:336` (filtro), `SeedStartupTests.cs`
  `:217, :250, :312, :339, :419` (listas alfabéticas: `purchases` va después de `pos` y antes de `quotations`; la de `:250` no
  lleva `pos`).

**Interfaces:**
- Produces: `TenantModuleKeys.Purchases` (`"purchases"`), `All.Count == 20`, `All[^1] == Purchases`,
  `DependenciesOf(Purchases) == [Suppliers, Catalog]`, `DefaultForNewTenants` sin cambios (7).

- [ ] **Step 1: Escribe las pruebas que fallan**

En `TenantModuleKeysTests.cs`: `AllKeepsTheContractOrder` suma `"purchases"` al final; `SupportIsLastHasNoDependenciesAndIsOffForNewTenants`
pasa a `SupportHasNoDependenciesAndIsOffForNewTenants` sin la aserción de `All[^1]`; nueva:

```csharp
[Fact]
public void PurchasesIsLastDependsOnSuppliersAndCatalogAndIsOffForNewTenants()
{
    Assert.Equal("purchases", TenantModuleKeys.All[^1].Value);
    Assert.Equal([TenantModuleKeys.Suppliers, TenantModuleKeys.Catalog], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Purchases));
    Assert.DoesNotContain(TenantModuleKeys.Purchases, TenantModuleKeys.DefaultForNewTenants);
}
```
Las listas literales del bloque **Files** suman `"purchases"`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests"`
Expected: FAIL de compilación (`'TenantModuleKeys' does not contain a definition for 'Purchases'`).

- [ ] **Step 3: Implementa** el campo, `All`, la dependencia, el `CHECK` y genera la migración:
`dotnet ef migrations add AddPurchasesModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`.
Edita `Up`/`Down` a la forma de la de Support (el scaffold sólo cambia el snapshot).

- [ ] **Step 4: Corre y verifica que pasan**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"`
Expected: PASS.
Run (integración, una sola corrida al final del bloque A, ver Step 4 de Task 3).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Tenancy tests/Modules/Tenancy
git commit -m "feat(tenancy): add purchases module key"
```

---

### Task 3: Permisos `purchases.order.read` y `purchases.order.manage`

**Files:**
- Modify: `src/Modules/Purchases/Modules.Purchases.Application/PurchasesPermissions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`: rol `admin` (junto a Suppliers `:2206-2208`), definiciones
  (después de las de Suppliers `:3146-3166`), políticas (después de `:3673-3678`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (`:319-348` → 89; prueba nueva junto a
  `TheLoansPermissionsRequireTheLoansModuleAndGoToAdminAndBilling` `:885`)

**Interfaces:**
- Produces: `PurchasesPermissions.OrderRead = "purchases.order.read"`, `PurchasesPermissions.OrderManage = "purchases.order.manage"`.
- `PermissionDefinition(OrderRead, "Ver órdenes de compra", "Permite ver las órdenes de compra y descargar su PDF.", "Compras", "low", RequiredModules: [ModuleKeys.Purchases]) { Shape = new("purchases.order", "Órdenes de compra", "read") }` y
  `PermissionDefinition(OrderManage, "Gestionar órdenes de compra", "Permite crear, editar, enviar, confirmar, recibir y cancelar órdenes de compra.", "Compras", "medium", RequiredModules: [ModuleKeys.Purchases]) { Shape = new("purchases.order", "Órdenes de compra", "manage") }`.
- Rol `admin` suma los dos con el comentario «Spec 2026-11-17 (compras): las órdenes de compra las administra quien administra la
  empresa (P-3, propuesta)». El owner del tenant ya los tiene (`Membership.cs:529`, el owner siempre es admin). `advisor`,
  `billing` y `cashier` no.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public void ThePurchasesPermissionsRequireThePurchasesModuleAndGoOnlyToAdmin()
// Para OrderRead y OrderManage: Category == "Compras"; RequiredModules == [ModuleKeys.Purchases];
// catalog.PermissionsFor("admin") los contiene; ("advisor"), ("billing") y ("cashier") no.
```
`PermissionDiscoveryFindsTheEightySevenConstants` pasa a `…TheEightyNineConstants` con `Assert.Equal(89, …)` y una línea más en
el comentario («+ los dos de compras (purchases.order.read|manage, spec 2026-11-17)»).

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`
Expected: FAIL de compilación (`'PurchasesPermissions' does not contain a definition for 'OrderRead'`).

- [ ] **Step 3: Implementa** las constantes, las dos definiciones, las dos políticas (`AddPermissionRequirement`) y el rol.

- [ ] **Step 4: Corre y verifica que pasan** (`CompositionRootTests` completo: también `EveryPermissionConstantDeclaresItsModules`
y `EveryRegisteredPermissionStringIsUnique`). Expected: PASS. Cierre del bloque A, integración de Tenancy (una corrida, sin otra
en curso):

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests"`
Expected: PASS. `TenancySeeder` ya itera `All`, así que el tenant semilla queda con `purchases`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Application/PurchasesPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests
git commit -m "feat(purchases): purchases.order.read and manage permissions"
```

**Revisión del bloque A.**

---

## Bloque B — Application e Infrastructure

### Task 4: Application — contrato, validación, puertos y handlers

**Files:**
- Create: todos los archivos de `Modules.Purchases.Application` del mapa salvo `GetPurchaseOrderPdf.cs` y
  `PurchaseOrderPdfDocument.cs` (Task 7)
- Modify: `QepServiceCollectionExtensions.cs`: handlers junto a los de Loans (`:434-477`, comentario «Purchases, spec 2026-11-17.
  A mano: un handler olvidado responde 500»), `services.AddScoped<PurchaseOrderComposer>()`,
  `AddValidatorsFromAssemblyContaining<CreatePurchaseOrderValidator>()` junto a `:1324`
- Test: `tests/Modules/Purchases/Modules.Purchases.UnitTests/{PurchaseOrderWriteRulesTests.cs, PurchaseOrderComposerTests.cs, PurchaseOrderLineBuilderTests.cs}`;
  `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` debe seguir en verde

**Interfaces:**
- Consumes: Domain de Task 1; `PurchasesPermissions` de Task 3; de Tenancy: `IExecutionContext`, `IMembershipDirectory.FindActiveMembershipIdAsync`, `Currencies`, `IClock`.
- Produces:
  - `PurchasesDtos.cs` (contrato, `<summary>` BFF en cada uno; en `PurchaseOrderDetailDto` dice que los costos van sin IVA y el IVA
    se suma en los totales):
    `PurchaseOrderSupplierRefDto(Guid Id, string Name)`, `PurchaseOrderSupplierDto(Guid Id, string Name, bool IsActive)`,
    `PurchaseOrderMemberDto(Guid MemberId, string Name)`,
    `PurchaseOrderLineDto(Guid Id, Guid VariantId, string Code, string Name, decimal Quantity, decimal? ConfirmedQuantity, decimal UnitCost, decimal? ConfirmedUnitCost, decimal TaxRatePercent, string LineStatus, decimal Subtotal)`,
    `PurchaseOrderTotalsDto(decimal Subtotal, decimal Tax, decimal Total)`,
    `PurchaseOrderHistoryDto(string Status, DateTimeOffset At, string ActorName)`,
    `PurchaseOrderDetailDto(Guid Id, string Number, string Status, bool ConfirmedWithChanges, long Version, PurchaseOrderSupplierDto Supplier, string Currency, string? Notes, PurchaseOrderMemberDto CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<PurchaseOrderLineDto> Lines, PurchaseOrderTotalsDto Totals, decimal RequestedTotal, IReadOnlyList<PurchaseOrderHistoryDto> History, IReadOnlyList<string> AllowedActions)` (acciones en minúscula: `"edit"`…),
    `PurchaseOrderSummaryDto(Guid Id, string Number, string Status, bool ConfirmedWithChanges, PurchaseOrderSupplierRefDto Supplier, string Currency, decimal Total, DateTimeOffset CreatedAt, int LineCount)`,
    `PurchaseOrderStatusCountsDto(int Draft, int Sent, int Confirmed, int Received, int Cancelled, int All)` (propiedades
    PascalCase a propósito: el contrato pide las claves `Draft`…`Cancelled` con el nombre del estado y `all` en minúscula, así que
    llevan `[JsonPropertyName]` explícito — `"Draft"`, …, `"all"` — y el `<summary>` lo explica),
    `PurchaseOrdersResponse(IReadOnlyCollection<PurchaseOrderSummaryDto> Items, int Total, int Page, int PageSize, PurchaseOrderStatusCountsDto Counts)`,
    `PurchaseOrderPreviewLineDto(Guid VariantId, string Code, string Name, decimal TaxRatePercent, decimal Subtotal)`,
    `PurchaseOrderPreviewDto(IReadOnlyList<PurchaseOrderPreviewLineDto> Lines, PurchaseOrderTotalsDto Totals)`,
    `PurchaseOrderLineInput(Guid VariantId, decimal Quantity, decimal UnitCost)`,
    `PurchaseOrderWriteRequest(Guid SupplierId, string Currency, string? Notes, IReadOnlyList<PurchaseOrderLineInput> Lines)` (un solo record para POST y PUT),
    `PurchaseOrderLineConfirmationInput(Guid LineId, decimal ConfirmedQuantity, decimal ConfirmedUnitCost)`,
    `ConfirmPurchaseOrderRequest(IReadOnlyList<PurchaseOrderLineConfirmationInput> Lines)`.
  - Comandos y queries (planos, para que `errors` diga `Lines[0].Quantity` y no `Body.Lines…`):
    `CreatePurchaseOrderCommand(Guid TenantId, Guid SupplierId, string Currency, string? Notes, IReadOnlyList<PurchaseOrderLineInput> Lines) : ICommand<PurchaseOrderDetailDto>, IPurchaseOrderWriteCommand`,
    `UpdatePurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion, Guid SupplierId, string Currency, string? Notes, IReadOnlyList<PurchaseOrderLineInput> Lines) : ICommand<PurchaseOrderDetailDto>, IPurchaseOrderWriteCommand`,
    `PreviewPurchaseOrderCommand(Guid TenantId, Guid SupplierId, string Currency, string? Notes, IReadOnlyList<PurchaseOrderLineInput> Lines) : ICommand<PurchaseOrderPreviewDto>, IPurchaseOrderWriteCommand` (comando y no query: exige `OrderManage` y corre las reglas de escritura; no escribe),
    `DeletePurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion) : ICommand<bool>`,
    `TransitionPurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion, PurchaseOrderAction Action) : ICommand<PurchaseOrderDetailDto>` (sólo `Send`, `Receive`, `Cancel`; un handler para los tres),
    `ConfirmPurchaseOrderCommand(Guid TenantId, Guid OrderId, long ExpectedVersion, IReadOnlyList<PurchaseOrderLineConfirmationInput> Lines) : ICommand<PurchaseOrderDetailDto>`,
    `GetPurchaseOrderQuery(Guid TenantId, Guid OrderId) : IQuery<PurchaseOrderDetailDto>`,
    `ListPurchaseOrdersQuery(Guid TenantId, string? Status, Guid? SupplierId, string? Search, int Page, int PageSize) : IQuery<PurchaseOrdersResponse>`.
  - `PurchaseOrderWriteRules : AbstractValidator<IPurchaseOrderWriteCommand>`: `Notes` ≤ 2000; `Lines` ≤ 200; por línea
    `Quantity > 0`, `UnitCost > 0`, `VariantId` no vacío; variante repetida → error en `Lines[i].VariantId` de la segunda (D-8).
    `CreatePurchaseOrderValidator` / `UpdatePurchaseOrderValidator` / `PreviewPurchaseOrderValidator` la incluyen. `ConfirmPurchaseOrderValidator`:
    `ConfirmedQuantity >= 0`, `ConfirmedUnitCost > 0` (D-9). `ListPurchaseOrdersValidator`: `Status` nulo/vacío o
    `Enum.TryParse<PurchaseOrderStatus>(…, ignoreCase: true)` (D-12).
  - `PurchaseOrderPaging` copia de `SupplierPaging` (`ListSuppliers.cs:25-40`: default 50, max 200).
  - `PurchasesAuthorization.EnsureAuthorized(IExecutionContext, Guid tenantId, string permission)` copia de `LoansAuthorization`.
  - `static class PurchasesConflicts`: `NotFound(Guid id)` → `ResourceNotFoundException(PurchasesErrorCodes.NotFound, …)`;
    `NotDraft()` / `InvalidTransition()` → `ResourceConflictException`; `Stale()` → `RequestConcurrencyException("concurrency.conflict", …)`;
    `EnsureAllowed(PurchaseOrder order, PurchaseOrderAction action)` (D-1: `Edit`/`Delete` → `NotDraft`, el resto → `InvalidTransition`);
    `EnsureVersion(PurchaseOrder order, long expected)`.
  - `PurchasesAuditActions`: `Created = "purchases.order.created"`, `Updated`, `Deleted`, `Sent`, `Confirmed`, `Received`, `Cancelled`.
  - `Ports.cs`:
    - `IPurchaseOrderRepository { Task<PurchaseOrder?> GetAsync(Guid tenantId, PurchaseOrderId id, CancellationToken ct); void Add(PurchaseOrder order); void Remove(PurchaseOrder order); Task<(IReadOnlyList<PurchaseOrder> Items, int Total)> SearchAsync(Guid tenantId, PurchaseOrderStatus? status, Guid? supplierId, string? numberSearch, IReadOnlyCollection<Guid>? supplierIdsMatchingSearch, int page, int pageSize, CancellationToken ct); Task<IReadOnlyDictionary<PurchaseOrderStatus, int>> CountByStatusAsync(Guid tenantId, Guid? supplierId, string? numberSearch, IReadOnlyCollection<Guid>? supplierIdsMatchingSearch, CancellationToken ct); }`
      (`CountByStatusAsync` aplica los mismos filtros que `SearchAsync` salvo `status`; un estado sin filas no viene y el handler lo
      completa en 0, D-18)
    - `IPurchaseOrderNumberGenerator { Task<long> NextAsync(Guid tenantId, CancellationToken ct); }` (exige transacción abierta).
    - `IPurchasesUnitOfWork { Task SaveChangesAsync(CancellationToken ct); Task<IPurchasesTransaction> BeginTransactionAsync(CancellationToken ct); }`, `IPurchasesTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken ct); }` (copia de `ISupportUnitOfWork.cs:12-31`).
    - `IPurchasesAuditPublisher.Publish(Guid tenantId, Guid actorId, string action, string resourceType, string resourceId, string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt)` (copia de `ILoansAuditPublisher`).
    - `sealed record PurchasesSupplierRef(Guid Id, string Name, string IdentificationType, string IdentificationNumber, bool IsActive)`;
      `IPurchasesSupplierLookup { Task<PurchasesSupplierRef?> FindAsync(Guid tenantId, Guid supplierId, CancellationToken ct); Task<IReadOnlyDictionary<Guid, PurchasesSupplierRef>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> supplierIds, CancellationToken ct); Task<IReadOnlyList<Guid>> SearchIdsAsync(Guid tenantId, string search, int take, CancellationToken ct); }`.
    - `sealed record PurchasesVariantRef(Guid VariantId, string Code, string Name, decimal TaxRatePercent)`;
      `IPurchasesCatalogLookup { Task<IReadOnlyDictionary<Guid, PurchasesVariantRef>> FindVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct); }`.
    - `IPurchasesMemberDirectory { Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct); }`.
  - `PurchaseOrderComposer` (scoped): `Task<PurchaseOrderDetailDto> DetailAsync(PurchaseOrder order, CancellationToken ct)` y
    `Task<IReadOnlyList<PurchaseOrderSummaryDto>> SummariesAsync(Guid tenantId, IReadOnlyList<PurchaseOrder> orders, CancellationToken ct)`:
    resuelve proveedores y nombres en lote (sin N+1), `"Sin nombre"` si falta el miembro, `allowedActions` con
    `order.AllowedActions()` filtrado por `executionContext.HasPermission(OrderManage)` (D-4).
  - Handlers (todos: autorizar → cargar o 404 → versión (412) → `EnsureAllowed` (409) → reglas (422) → mutar → auditar →
    guardar → `DetailAsync`):
    - `PurchaseOrderLineBuilder` (scoped, D-19): `Task<IReadOnlyList<PurchaseOrderLineDraft>> BuildAsync(Guid tenantId, IPurchaseOrderWriteCommand command, CancellationToken ct)`
      corre, en este orden, `Currencies.IsSupported` o 422 `invalid_currency`; proveedor (`null` → 404
      `suppliers.supplier.not_found`, inactivo → 422 `supplier_inactive`); `lines` vacía → 422 `empty`; variantes por
      `FindVariantsAsync`, alguna ausente → 422 `line_not_in_catalog`; devuelve los borradores en el orden del cuerpo con código,
      nombre y tasa actuales.
    - `CreatePurchaseOrderHandler`: `OrderManage`; validador; miembro por `FindActiveMembershipIdAsync` (sin membresía → 403
      `authorization.denied`, como `QuotationAdvisorResolver`); `PurchaseOrderLineBuilder.BuildAsync`; **después** abre la
      transacción, pide el número, `PurchaseOrder.Create` con `$"OC-{sequence:D4}"`, audita, guarda y confirma (un 422 nunca
      gasta número).
    - `PreviewPurchaseOrderHandler`: `OrderManage`; validador; `PurchaseOrderLineBuilder.BuildAsync`; arma el DTO con
      `PurchaseOrderMath`. Sin repositorio, sin unidad de trabajo, sin auditoría y sin membresía.
    - `UpdatePurchaseOrderHandler`: `EnsureAllowed(Edit)` y `PurchaseOrderLineBuilder.BuildAsync` antes de `Replace` (D-7).
    - `DeletePurchaseOrderHandler`: `EnsureAllowed(Delete)`, `Remove`, audita `Deleted`.
    - `TransitionPurchaseOrderHandler`: `Send` además vuelve a mirar el proveedor (inactivo o ausente → 422 `supplier_inactive`);
      `Receive`, `Cancel` sin reglas extra.
    - `ConfirmPurchaseOrderHandler`: `EnsureAllowed(Confirm)`; el conjunto de `lineId` tiene que ser exactamente el de la orden
      (sin faltantes, repetidos ni ajenos) o 422 `confirm_lines_mismatch`, antes de mutar.
    - `GetPurchaseOrderHandler`, `ListPurchaseOrdersHandler`: `OrderRead`. La lista normaliza con `PurchaseOrderPaging` y, con
      `search`, pide `SearchIdsAsync(tenantId, search, 200)` (D-11); `Counts` sale de `CountByStatusAsync` con las seis claves y
      `All` = suma (D-18).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// PurchaseOrderWriteRulesTests.cs
AValidCommandPasses
ZeroOrNegativeQuantityFailsOnTheRow                    // [Theory] 0 y -1 → "Lines[0].Quantity"
ZeroOrNegativeUnitCostFailsOnTheRow                    // "Lines[0].UnitCost"
ARepeatedVariantFailsOnTheSecondRow                    // "Lines[1].VariantId"
MoreThan200LinesFail; NotesOver2000Fail
AnEmptyLineListIsNotAValidationError                   // lo decide el handler con purchases.order.empty
ConfirmAcceptsZeroQuantityAndRejectsNegative           // 0 pasa; -1 → "Lines[0].ConfirmedQuantity"
ConfirmRejectsZeroCost                                 // "Lines[0].ConfirmedUnitCost"
ListRejectsAnUnknownStatusAndAcceptsAnyCase            // "paused" → "Status"; "sent" pasa
// PurchaseOrderComposerTests.cs (puertos falsos en memoria)
DetailCarriesSupplierCreatorHistoryNamesAndTotals      // createdBy.name, history[0].actorName, totals, requestedTotal
AMissingMemberIsSinNombre
AllowedActionsAreEmptyWithoutTheManagePermission       // HasPermission(OrderManage) false → []
AllowedActionsAreLowercaseInTheContractOrder           // Draft → ["edit","delete","send","cancel"]
SummariesResolveSuppliersInOneCall                     // el lookup falso cuenta 1 llamada a FindManyAsync para 3 órdenes
// PurchaseOrderLineBuilderTests.cs (puertos falsos)
TheRulesRunInTheContractOrder                          // moneda inválida + proveedor inactivo → invalid_currency; proveedor inactivo + lines [] → supplier_inactive
LinesComeBackInBodyOrderWithTheCurrentCatalogData
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrderWriteRulesTests|FullyQualifiedName~PurchaseOrderComposerTests|FullyQualifiedName~PurchaseOrderLineBuilderTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** Application y registra los ocho handlers, el composer, el builder y los validadores en el Bootstrapper.

- [ ] **Step 4: Corre y verifica que pasan**, más:

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered|FullyQualifiedName~PurchasesLayerTests"`
Expected: PASS. (Los puertos todavía no tienen adaptador: ninguna prueba resuelve el contenedor completo en este paso.)

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Purchases
git commit -m "feat(purchases): purchase order use cases, validation and ports"
```

---

### Task 5: Infrastructure y adaptadores

**Files:**
- Create: `Modules.Purchases.Infrastructure/{PurchasesInfrastructureExtensions.cs (reemplaza el vacío), PurchasesDatabaseInitializer.cs, Persistence/PurchasesDbContext.cs, Persistence/PurchasesDbContextFactory.cs, Persistence/PurchaseOrderRepository.cs, Persistence/PurchaseOrderNumberGenerator.cs, Persistence/PurchasesUnitOfWork.cs, Persistence/PurchasesAuditPublisher.cs, Persistence/Migrations/*_InitialPurchases.cs}`
- Create: `src/Bootstrapper/{PurchasesSupplierLookup.cs, PurchasesCatalogLookup.cs, PurchasesMemberDirectory.cs, PurchasesUserReferenceProbe.cs}`
- Modify: `QepServiceCollectionExtensions.cs`: `services.AddPurchasesInfrastructure(configuration);` junto a `:1343`; la sonda junto a
  `:1730`; los tres adaptadores junto a `:1735-1736`
- Modify: `src/Api/Program.cs`: `await services.InitializePurchasesDatabaseAsync(cancellationToken);` después de Store (`:513-515`),
  con comentario «Después de Audit (escribe en audit.entries). Sin FKs a otros esquemas: proveedor, variante y membresías son
  referencias blandas»
- Test: `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/{….csproj (copia de Loans.IntegrationTests), PurchasesApiHarness.cs, SharedPurchasesHost.cs, PurchasesUserReferenceProbeTests.cs}`

**Interfaces:**
- Consumes: puertos de Task 4.
- Produces:
  - `PurchasesDbContext` (esquema `purchases`, historial de migraciones `__ef_migrations_history` en `purchases`):
    `purchases.purchase_orders` (`id`, `tenant_id`, `number` varchar(20), `supplier_id`, `currency` char(3), `notes`
    varchar(2000), `status` varchar(16) como texto, `confirmed_with_changes`, `created_by_member_id`, `created_at`, `updated_at`,
    `version` token de concurrencia); únicos `IX_purchase_orders_tenant_number (tenant_id, number)`; índices
    `(tenant_id, created_at DESC, id DESC)`, `(tenant_id, status)`, `(tenant_id, supplier_id)`.
    `purchases.purchase_order_lines` (`id`, `order_id` FK `ON DELETE CASCADE`, `ordinal`, `variant_id`, `code` varchar(64),
    `name` varchar(300), `quantity` (10,2), `unit_cost` (14,2), `confirmed_quantity` (10,2) null, `confirmed_unit_cost` (14,2)
    null, `tax_rate_percent` (5,2)). `purchases.purchase_order_history` (`order_id` FK cascade, `sequence`, `status`,
    `occurred_at`, `member_id`; PK `(order_id, sequence)`). `purchases.order_number_counters` (`tenant_id` PK, `next_value`
    bigint). `audit.entries` como proyección `ExcludeFromMigrations` (`LoansDbContext.cs:21-26`).
  - `PurchaseOrderNumberGenerator`: copia de `PosSaleNumberGenerator.cs` sobre `purchases.order_number_counters`, con la guarda de
    transacción de `SupportTicketNumberGenerator.cs:17-21`.
  - `PurchaseOrderRepository`: `GetAsync` con `Include` de líneas e historial, filtrando por `tenant_id`; `SearchAsync` con
    `EF.Functions.ILike` escapado (copia `LikeSearch.cs` de Loans) sobre `number`, en OR con `supplier_id IN (ids)`; `CountAsync`
    antes de paginar; orden D-13. Las líneas de la página se cargan con la orden (para `lineCount` y `total`).
    `CountByStatusAsync`: el mismo `FilteredQuery` sin `status`, `GroupBy(status).Select(key, Count())` en una ida.
  - `PurchasesUnitOfWork`: `DbUpdateConcurrencyException` → 412 `concurrency.conflict`; único `IX_purchase_orders_tenant_number`
    → `InvalidOperationException` (no debería pasar con el contador; si pasa es un bug, no un 422).
  - `PurchasesAuditPublisher`: copia de `LoansAuditPublisher.cs` con `Source = "purchases"`.
  - `AddPurchasesInfrastructure(IServiceCollection, IConfiguration)` y `InitializePurchasesDatabaseAsync(this IServiceProvider, CancellationToken)` (forma de Loans).
  - Bootstrapper:
    - `PurchasesSupplierLookup(SuppliersDbContext)`: proyección `(Id, Name, IdentificationType wire, Number, IsActive)` filtrada por
      tenant; `SearchIdsAsync` con `ILike` escapado sobre `Name` y `BusinessName`, `Take(take)`.
    - `PurchasesCatalogLookup(IProductRepository, IOptionDefinitionRepository, ITaxRateRepository)`: `FindVariantsByIdsAsync` +
      `CatalogVariantReads.Select`, `LoadValueNamesAsync`, `DisplayNameOf`, `LoadTaxPercentagesAsync` (sin tasa → 0, como
      `PosProductLookup.cs:136`); no filtra por activo (D-6).
    - `PurchasesMemberDirectory(TenancyDbContext)`: nombres visibles de membresías del tenant (`DisplayName`), sin estado.
    - `PurchasesUserReferenceProbe(PurchasesDbContext, IMembershipDirectory)`: `Source => "purchases"`; retiene si alguna
      membresía es `created_by_member_id` o `member_id` del historial (forma de `LoansUserReferenceProbe.cs`).
  - Harness: `PurchasesApiHarness` (constantes y `StartDatabaseAsync`, `QepApiFactory(connectionString, Action<IServiceCollection>? configureServices)`
    copiados de `LoansApiHarness.cs`); `NewTenantAsync(factory, IEnumerable<TenantModuleKey>? modules = null) → PurchasesTestTenant(Guid TenantId, Guid SubjectId, Guid MemberId)`
    que siembra tenant con módulos (`All` por defecto), usuario y membresía activa `admin` con nombre «Ana Compras»;
    `CreateManager(factory, tenant)` (`purchases.order.*`, `suppliers.supplier.*`, `catalog.product.read|manage`, `catalog.tax_rate.manage`
    por `X-Permissions`), `CreateReader(factory, tenant)` (sólo `purchases.order.read`); `CreateSupplierAsync(client, tenantId, name)`
    (país `US` con `cityName`, sin ciudad DIVIPOLA), `DeactivateSupplierAsync`; `CreateTaxRateAsync` y
    `CreateVariantAsync(client, tenantId, code, name, Guid? taxRateId) → Guid variantId` (copia de `StoreApiHarness.cs:401-438`,
    leyendo `variants[0].id`); `OrderUrl(tenantId, orderId?)`; `WithIfMatch(HttpRequestMessage, long version)`.
    `SharedPurchasesHost : IAsyncLifetime` copia de `SharedSupportHost.cs` (una base y un host por clase).

- [ ] **Step 1: Escribe la prueba que falla**

```csharp
// PurchasesUserReferenceProbeTests.cs (host propio: lee toda la base)
TheCreatorOfAPurchaseOrderIsRetained           // inserta la orden por PurchasesDbContext con created_by_member_id = membresía del usuario → probe "purchases" true; otro usuario → false
```

- [ ] **Step 2: Corre y verifica que falla**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~TheCreatorOfAPurchaseOrderIsRetained"`
Expected: FAIL de compilación (`PurchasesDbContext` no existe).

- [ ] **Step 3: Implementa** todo lo anterior y genera la migración:
`dotnet ef migrations add InitialPurchases --project src/Modules/Purchases/Modules.Purchases.Infrastructure --context PurchasesDbContext -o Persistence/Migrations`.

- [ ] **Step 4: Verifica**

Run: `dotnet ef migrations has-pending-model-changes --project src/Modules/Purchases/Modules.Purchases.Infrastructure --context PurchasesDbContext`
Expected: `No changes have been made to the model since the last migration.`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~TheCreatorOfAPurchaseOrderIsRetained"`
Expected: PASS.
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~PurchasesLayerTests|FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MultiReplicaInventoryTests"`
Expected: PASS (sin hosted services ni singletons nuevos en esta tarea).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Infrastructure src/Bootstrapper src/Api/Program.cs tests/Modules/Purchases Backend.slnx
git commit -m "feat(purchases): persistence, numbering and cross-module adapters"
```

**Revisión del bloque B.**

---

## Bloque C — API, PDF y documentación

### Task 6: Endpoints y pruebas de API

**Files:**
- Modify: `src/Modules/Purchases/Modules.Purchases.Api/PurchaseOrderEndpoints.cs` (vacío desde Task 1), y crea
  `PurchasesIfMatch.cs` (copia literal de `CatalogIfMatch.cs`, con el aviso de «si cambias cómo se lee el header, cambia todas»)
- Modify: `src/Api/Program.cs`: `app.MapPurchaseOrderEndpoints();` junto a `:395-399`
- Create: `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/{PurchaseOrderWriteApiTests.cs, PurchaseOrderListApiTests.cs, PurchaseOrderTransitionApiTests.cs, PurchasesAccessApiTests.cs}` (todas con `IClassFixture<SharedPurchasesHost>` y un tenant nuevo por prueba)

**Interfaces:**
- Consumes: handlers de Task 4, harness de Task 5.
- Produces: `MapPurchaseOrderEndpoints(this IEndpointRouteBuilder)`, grupo `/api/v1/tenants/{tenantId:guid}/purchases/orders`,
  tag `Purchases`, rutas del contrato. `POST /` → `Results.Created($"/api/v1/tenants/{tenantId}/purchases/orders/{id}", detail)`;
  `POST /preview` → 200 `PurchaseOrderPreviewDto` (`OrderManage`; ruta literal declarada antes de `/{id:guid}`);
  `DELETE` → 204; las escrituras sobre una orden leen `PurchasesIfMatch.RequireVersion(httpContext, "purchase order")` antes de
  despachar. Lecturas con `RequireAuthorization(PurchasesPermissions.OrderRead)`, escrituras con `OrderManage`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// PurchaseOrderWriteApiTests.cs
CreateReturns201WithLocationNumberAndADraftDetail         // number "OC-0001"; status "Draft"; version 1; createdBy.name "Ana Compras"; lines con code/name del catálogo; lineStatus "Pending"; allowedActions ["edit","delete","send","cancel"]
CreateSnapshotsTheTaxRateAndAddsTaxOnTopOfTheCost         // 2 × 100 al 19 % → totals (200, 38, 238); requestedTotal 238
TwoConcurrentCreatesGetConsecutiveNumbers                 // Review Focus 1: Task.WhenAll → {"OC-0001","OC-0002"}
ARejectedCreateDoesNotSpendANumber                        // Review Focus 2: variante de otro tenant → 422 line_not_in_catalog; la siguiente → OC-0001
CreateWithoutLinesIs422Empty
CreateWithAnUnknownVariantIs422LineNotInCatalog
CreateWithAnInactiveSupplierIs422SupplierInactive
CreateWithAnUnknownSupplierIs404SupplierNotFound          // D-3: code "suppliers.supplier.not_found"
CreateWithAnUnsupportedCurrencyIs422InvalidCurrency       // "XXX"
CreateWithZeroQuantityOrCostIs422WithTheFieldMap          // errors contiene "Lines[0].Quantity" y "Lines[1].UnitCost"
UpdateReplacesTheDraftAndBumpsTheVersion                  // PUT con If-Match 1 → version 2, líneas nuevas
TheLineKeepsItsTaxRateUntilTheDraftIsSaved                // Review Focus 3: UPDATE catalog.tax_rates SET percentage = 5; GET sigue 19; PUT → 5
UpdateWithAStaleIfMatchIs412AndWithoutItIs428             // "concurrency.conflict" / "precondition.if_match_required"
UpdateOrDeleteOutsideDraftIs409NotDraft                   // tras send
DeleteADraftIs204AndThenGetIs404NotFound                  // "purchases.order.not_found"
EveryWriteIsAudited                                       // audit.entries con purchases.order.created, .updated y .deleted sobre el id
PreviewMatchesTheTotalsTheCreateWouldStore                // mismo cuerpo: preview.totals == detail.totals del POST siguiente; lines con code, name, taxRatePercent y subtotal sin IVA
PreviewWritesNothing                                      // tras preview: GET / total 0, sin filas en audit.entries ni en purchases.order_number_counters; el POST siguiente sale OC-0001
PreviewRunsTheSameRulesAsCreate                           // [Theory] lines [] → empty; variante ajena → line_not_in_catalog; proveedor inactivo → supplier_inactive; "XXX" → invalid_currency; cantidad 0 → 422 con errors "Lines[0].Quantity"
PreviewNeedsTheManagePermission                           // lector → 403
// PurchaseOrderTransitionApiTests.cs
SendConfirmReceiveWalksTheHappyPathAndTheHistory          // history: Draft, Sent, Confirmed, Received con actorName; allowedActions final []
ConfirmWithChangesMarksLinesAndUsesConfirmedTotals        // línea A igual, B costo distinto, C cantidad 0 → Unchanged/Changed/Rejected; confirmedWithChanges true; totals con confirmados; requestedTotal con pedidos
ConfirmWithMismatchedLinesIs422AndChangesNothing          // Review Focus 4: [Theory] falta una, repetida, lineId ajeno → 422 confirm_lines_mismatch; GET: status Sent, misma version
SendingToADeactivatedSupplierIs422AndLeavesTheDraft       // Review Focus 5: luego cancel → 200 Cancelled
InvalidTransitionsAre409                                  // receive en Draft; send en Sent; cancel en Received → purchases.order.invalid_transition
CancelFromDraftSentAndConfirmedSucceeds                   // [Theory]
// PurchaseOrderListApiTests.cs
ListIsPagedNewestFirstWithTotal                           // 3 órdenes, pageSize 2 → items 2 (OC-0003, OC-0002), total 3
ListFiltersByStatusIgnoringCaseAndRejectsUnknown          // "sent" filtra; "paused" → 422 validation.failed con errors.Status
ListFiltersBySupplierAndSearchesNumberAndSupplierName     // search "0002" y search por parte del nombre del proveedor
ListSummaryCarriesSupplierTotalAndLineCount               // supplier {id,name}; total con IVA; lineCount
ListCapsPageSizeAt200
CountsIgnoreStatusButHonorTheOtherFilters                 // 2 Draft, 1 Sent, 1 Cancelled de un proveedor + 1 Draft de otro; ?status=Sent&supplierId=A → items 1; counts {Draft 2, Sent 1, Confirmed 0, Received 0, Cancelled 1, all 4}
CountsComeWithEveryKeyInZeroForAnEmptyTenant              // JSON con las seis claves exactas "Draft","Sent","Confirmed","Received","Cancelled","all" en 0
// PurchasesAccessApiTests.cs
AReaderCanReadButNotWrite                                 // GET 200; POST/PUT/send → 403; detail.allowedActions []
AnotherTenantsRouteIs403AuthorizationDenied               // cliente del tenant A sobre la ruta del tenant B
WithThePurchasesModuleOffEveryEndpointIs403               // tenant sembrado sin purchases: GET /, GET /{id}, POST / → 403
WithoutAnActiveMembershipCreateIs403                      // subject sin membresía en el tenant → 403 authorization.denied
```

- [ ] **Step 2: Corre y verifica que fallan** (una prueba, por nombre)

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~CreateReturns201WithLocationNumberAndADraftDetail"`
Expected: FAIL (`Expected: Created, Actual: NotFound`: no hay endpoints).

- [ ] **Step 3: Implementa** `PurchaseOrderEndpoints` (forma de `LoansEndpoints.cs`: lista con query en `:` como
  `SupplierEndpoints.cs:19-25`, `If-Match` como `LoansEndpoints.cs:295`) y el `Map` en `Program.cs`.

- [ ] **Step 4: Corre y verifica que pasan.** Mientras iteras, una prueba por nombre. Al terminar, cada clase una vez, en fila:

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~PurchaseOrderWriteApiTests"`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~PurchaseOrderTransitionApiTests"`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~PurchaseOrderListApiTests|FullyQualifiedName~PurchasesAccessApiTests"`
Expected: PASS en las tres.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases/Modules.Purchases.Api src/Api/Program.cs tests/Modules/Purchases
git commit -m "feat(purchases): purchase order endpoints"
```

---

### Task 7: PDF de la orden de compra

**Files:**
- Create: `Modules.Purchases.Application/{PurchaseOrderPdfDocument.cs, GetPurchaseOrderPdf.cs}`
- Create: `Modules.Purchases.Infrastructure/Pdf/{PurchasesPdfOptions.cs, PurchasesPdfOptionsValidator.cs, PurchaseOrderPdfRenderer.cs, purchase-order.typ}` (recurso incrustado, como `formal-reply.typ`)
- Modify: `PurchasesInfrastructureExtensions.cs` (opciones con `ValidateOnStart` y renderer singleton por fábrica, como
  `SupportInfrastructureExtensions.cs:59-68`), `PurchaseOrderEndpoints.cs` (`GET /{id}/pdf`), `QepServiceCollectionExtensions.cs`
  (handler), `src/Api/appsettings.json` (`"Purchases": { "Pdf": { "BaseUrl": "https://qcode-pdf.qcode.co" } }`),
  `k8s/prod-configMap.yaml` (`Purchases__Pdf__BaseUrl: "#{PURCHASES_PDF_BASE_URL}#"` junto a `:110`), `k8s/prod-secret.yaml`
  (`Purchases__Pdf__ApiKey: "#{PURCHASES_PDF_API_KEY}#"` junto a `:44`, con el comentario de Support adaptado)
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entrada junto a `:269`:
  `new("Modules.Purchases.Application.IPurchaseOrderPdfRenderer", "Modules.Purchases.Infrastructure.Pdf.PurchaseOrderPdfRenderer", "Modules/Purchases/Modules.Purchases.Infrastructure/PurchasesInfrastructureExtensions.cs:<línea> — HttpClient propio y opciones, sin estado")`)
- Modify: `SharedPurchasesHost` / `QepApiFactory` del harness: registra `FakePurchaseOrderPdfRenderer` (devuelve `"%PDF-1.7 fake"` y guarda
  el último documento) en lugar del real
- Test: `tests/Modules/Purchases/Modules.Purchases.UnitTests/{PurchaseOrderPdfMapperTests.cs, PurchasesPdfOptionsValidatorTests.cs}`,
  `tests/Modules/Purchases/Modules.Purchases.IntegrationTests/PurchaseOrderPdfApiTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PurchaseOrderPdfLine(string Code, string Name, string Quantity, string UnitCost, string TaxRate, string Subtotal)`;
    `sealed record PurchaseOrderPdfDocument(string TenantName, string Number, string StatusLabel, string IssuedOn, string SupplierName, string SupplierIdentification, string Currency, string? Notes, IReadOnlyList<PurchaseOrderPdfLine> Lines, string Subtotal, string Tax, string Total, bool ShowsConfirmedValues)`
    (camelCase en el JSON: es el contrato con la plantilla).
  - `interface IPurchaseOrderPdfRenderer { Task<byte[]> RenderAsync(PurchaseOrderPdfDocument document, CancellationToken ct); }`.
  - `static class PurchaseOrderPdfMapper { PurchaseOrderPdfDocument Map(PurchaseOrderDetailDto detail, PurchasesSupplierRef supplier, string tenantName, TenantRegionalSettings? regional, DateOnly today); }`:
    montos con `Currencies.Get(currency).Symbol`/`.Decimals` y `RegionalNumberFormat.ToNumberFormatInfo(regional?.NumberFormat)`,
    fecha con `RegionalDateFormat.Pattern(regional?.DateFormat)`; usa los valores confirmados donde la línea los tenga (D-5);
    `StatusLabel`: Borrador, Enviada, Confirmada, Recibida, Cancelada.
  - `GetPurchaseOrderPdfQuery(Guid TenantId, Guid OrderId) : IQuery<PurchaseOrderPdfFile>`,
    `sealed record PurchaseOrderPdfFile(byte[] Content, string FileName)` (`$"{number}.pdf"`). Handler con `OrderRead`, tenant por
    `ITenantDirectory.GetDisplayNameAsync` y `GetRegionalSettingsAsync`, día por `ITenantClock`.
  - `PurchasesPdfOptions { const SectionName = "Purchases:Pdf"; BaseUrl = "https://qcode-pdf.qcode.co"; ApiKey = ""; TimeoutSeconds = 30 }`
    y su validador: copia de `SupportPdfOptions.cs` / `SupportPdfOptionsValidator.cs` con los nombres de Purchases.
  - `PurchaseOrderPdfRenderer`: forma de `QCodePdfRenderer.RenderAsync` (`{ source, data, filename }`, `X-Api-Key`); un no-2xx lanza
    `UpstreamServiceException("purchases.order.pdf_render_failed", …)` (502), sin el cuerpo de la respuesta en el mensaje al cliente.
    **Este código no está en el contrato:** si el owner no lo acepta, queda como 500 (P).
  - Endpoint: `Results.File(file.Content, "application/pdf", file.FileName)` con `OrderRead`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// PurchaseOrderPdfMapperTests.cs
MapFormatsMoneyWithTheTenantSeparatorsAndTheCurrencySymbol   // COP, formato "1.234,56" → "$ 238" (COP sin decimales); USD → "US$ 12,50"
MapUsesConfirmedValuesWhenTheLineHasThem
MapLabelsTheStatusInSpanish                                  // Draft → "Borrador"
// PurchasesPdfOptionsValidatorTests.cs
ProductionWithoutApiKeyFails; ProductionWithAnUnresolvedPipelineTokenFails; ABaseUrlThatIsNotHttpsFails   // copia de las de Support
// PurchaseOrderPdfApiTests.cs
PdfReturnsApplicationPdfWithTheNumberAsFileName               // 200, Content-Type application/pdf, filename OC-0001.pdf, cuerpo empieza con "%PDF"
PdfIsAvailableInEveryStatus                                   // Draft y Cancelled
PdfOfAnotherTenantsRouteIs403; PdfOfAnUnknownOrderIs404
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrderPdfMapperTests|FullyQualifiedName~PurchasesPdfOptionsValidatorTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** mapper, handler, opciones, validador, renderer, plantilla Typst (encabezado con la empresa y el número,
  bloque del proveedor, tabla Código/Producto/Cantidad/Costo/IVA/Subtotal, totales, notas; lee `json(sys.inputs.data)` como
  `quotation.typ`), la configuración y la entrada de `MultiReplicaInventory`.

- [ ] **Step 4: Corre y verifica que pasan**

Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests --filter "FullyQualifiedName~PurchaseOrderPdfMapperTests|FullyQualifiedName~PurchasesPdfOptionsValidatorTests"`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests --filter "FullyQualifiedName~PurchaseOrderPdfApiTests"`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests"`
Expected: PASS. El render real contra `qcode-pdf` queda pendiente de la clave (D-14).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Purchases src/Bootstrapper/QepServiceCollectionExtensions.cs src/Api/appsettings.json k8s tests/Modules/Purchases tests/ArchitectureTests
git commit -m "feat(purchases): purchase order pdf"
```

---

### Task 8: Documentación y verificación final

**Files:**
- Modify: `README.md:854-856` (lista de módulos con `purchases` al final y «`purchases` exige `suppliers` y `catalog`; no viene con el
  signup y se prende por tenant»), tabla de endpoints: fila nueva después de la de Suppliers (`:1345`) con rutas, filtros, errores y
  permisos del contrato; sección de configuración con `Purchases:Pdf:*`
- Modify: `CLAUDE.md:305-306` («Los veintitrés módulos construidos» → «veinticuatro», suma `Purchases`; `PurchasesLayerTests`)

- [ ] **Step 1: Escribe la documentación.** Sin prueba: es texto.

- [ ] **Step 2: Verificación de cierre** (en fila, sin otra corrida de integración en curso):

Run: `dotnet build Backend.slnx` → Expected: `0 Error(s)` y `0 Warning(s)`.
Run: `dotnet format --verify-no-changes --include src/Modules/Purchases tests/Modules/Purchases src/Bootstrapper/Purchases*.cs`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.UnitTests`
Run: `dotnet test tests/Modules/Purchases/Modules.Purchases.IntegrationTests -- xUnit.MaxParallelThreads=4`
Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests`
Expected: PASS en todas. La integración de Tenancy ya corrió en el bloque A; no se repite salvo que algo de Tenancy cambie después.

- [ ] **Step 3: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs(purchases): document the purchases module"
```

**Revisión del bloque C, y después la revisión fuerte de toda la rama.**

---

## Pendientes del owner (no bloquean el código)

- **P-3** (spec): resuelta 2026-11-17 con la propuesta (owner y admin). Este plan los da a `admin`, y el owner los
  tiene por ser admin (`Membership.cs:529`).
- D-3, D-6, D-14 (alta de `PURCHASES_PDF_API_KEY` y `PURCHASES_PDF_BASE_URL` en `Backend-prod`; sin la clave el pod no arranca en
  Production) y el código `purchases.order.pdf_render_failed` de Task 7.
- Antes de `main`: generar un PDF real contra `qcode-pdf` (con notas, con 50 líneas, con una línea rechazada).
- Deploy: backend antes que frontend (la clave de módulo y las rutas tienen que existir cuando el SPA las pida).
