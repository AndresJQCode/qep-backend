# Facturar un pedido (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un pedido `Approved` se pueda marcar como `Invoiced` —guardando quién y cuándo— y que esa marca se pueda revertir a `Approved`, con un permiso propio `quotations.order.invoice` para admin y facturación; un facturado no se anula ni se edita, y sigue contando como venta en el resumen.

**Architecture:** El agregado `Order` gana `Invoice`, `RevertInvoicing`, dos propiedades nullable y un guard nuevo en `Cancel`; las propiedades se mapean a dos columnas nuevas de `quotations.orders` con una migración generada por `dotnet ef`, y `QuotationUserReferenceProbe` suma `invoiced_by`. Dos casos de uso, `InvoiceOrder` y `RevertOrderInvoicing`, calcados de `ApproveOrder`, cuelgan de `POST /orders/{orderId}/invoice` y `POST /orders/{orderId}/uninvoice` con la política del permiso nuevo. El permiso se declara en el composition root y lo reciben los roles `admin` y `billing`, que viven en código: no hay migración de datos. `OrdersReportSource` no cambia: ya sólo excluye `Cancelled`.

**Tech Stack:** .NET 10 (SDK de `global.json`, 10.0.400), EF Core 10.0.11 + Npgsql 10.0.3, xUnit v3, Testcontainers (`postgres:18-alpine`; Docker corriendo para las pruebas de integración).

**Spec:** `docs/superpowers/specs/2026-10-05-facturar-pedido-design.md` (en este mismo worktree). Este plan implementa **sólo** su sección «Backend (`qep-backend`)».

## Global Constraints

**Del spec** (valores copiados tal cual):

- Alcance: «"Facturado" es **sólo un cambio de estado** del pedido. QEP no emite ni anula facturas, no llama a Siigo ni a ningún otro sistema, y no genera documento.»
- Decisión 1: «Se factura **sólo desde `Approved`**.»
- Decisión 2: «`Invoiced` **no se anula ni se edita**. Para corregir un error se revierte (decisión 4).»
- Decisión 3: «Al facturar se guarda **quién y cuándo** (`InvoicedAt`, `InvoicedBy`). El request no lleva cuerpo.»
- Decisión 4: «**Revertir facturación** (`Invoiced → Approved`): limpia `InvoicedAt`/`InvoicedBy`. La historia queda en la auditoría.»
- Decisión 5: «Permiso nuevo **`quotations.order.invoice`**, asignado a **admin y billing**. Cubre facturar **y** revertir.»
- Decisión 6: «**De momento**, facturar **no exige** que el pedido esté pagado, igual que aprobar (`Order.Approve` no mira `PaymentStatus`).»
- Decisión 7: «Reportes: un facturado **cuenta como venta** en el resumen. El listado y la exportación lo muestran como "Facturado".»
- Códigos: `order.order.not_approved`, `order.order.not_invoiced`, `order.order.already_invoiced`.
- `Order.Cancel`: «si el estado es `Invoiced` → `order.order.already_invoiced`. Este guard va antes que el de `already_cancelled` y antes de validar el motivo.»
- Rutas: `POST /api/v1/tenants/{tenantId}/orders/{orderId}/invoice` y `POST /api/v1/tenants/{tenantId}/orders/{orderId}/uninvoice`, «Los dos usan `RequireAuthorization(OrdersPermissions.OrderInvoice)`», «Respuestas: `200` con `OrderResponse`, `403`, `404`, `422`.»
- Auditoría: `quotation.order.invoiced` / `quotation.order.invoice_reverted`. «No hay validador, porque no hay texto libre. Tampoco hay evento de outbox: aprobar no lo tiene.»
- Columnas: `invoiced_at` e `invoiced_by` «con conversión nullable de `MemberId`». Migración `yyyyMMddHHmmss_AddOrderInvoicing`. «`status` ya es `varchar(20)` sin check constraint, así que `Invoiced` entra sin migrar la columna.»
- `PermissionDefinition` «módulo Quotations, riesgo "medium"».
- `ExportStatusLabels`: «`Invoiced → "Facturado"`».
- Los guards `Pending` existentes «ya bloquean un facturado, así que no se tocan».

**Del proceso:**

- Todo se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido`, rama **`feature/facturar-pedido`** (sin upstream; salió de `origin/develop` en `d9bc133`). Cada bloque de comandos empieza con `Set-Location` a ese worktree. **Nunca** se trabaja ni se commitea en el checkout principal `...\qep\qep-backend`: ahí hay otra sesión activa.
- TDD estricto: RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff (como mínimo el resumen Superado/Con error/Omitido y el mensaje de cada falla).
- Todo comando va en **PowerShell**: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`. **Nunca** se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las pruebas corren **en primer plano**. Por tarea se corren **sólo las clases de prueba que la tarea toca** (`--filter "FullyQualifiedName~<Clase>"`, un proyecto por comando) más `tests/ArchitectureTests/ArchitectureTests`. La suite completa corre **una sola vez**, en la Task 5.
- Migración con el factory de diseño, nunca a mano ni con `--startup-project`: `dotnet ef migrations add AddOrderInvoicing --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. `QuotationsDbContextModelSnapshot.cs` sólo cambia regenerado por ese comando. Las migraciones históricas no se tocan.
- Las pruebas de xUnit v3 pasan `TestContext.Current.CancellationToken` a toda llamada que acepte un `CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- Commits: Conventional Commits en español. **Sin atribución de IA ni trailer `Co-Authored-By`**, aunque el harness lo pida. Cada commit en un solo comando con el guard de rama y rutas explícitas:
  `if ((git branch --show-current) -ne "feature/facturar-pedido") { throw "ABORT" }; git add <rutas explícitas>; git commit -m "<mensaje>"`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, `git commit --amend` antes de seguir. Nunca `git add -A` ni `git add .`.
- Idioma: prosa, comentarios y `<summary>` en español, tuteando (nunca voseo); identificadores, códigos de error y mensajes de excepción en inglés, como el código de alrededor. Los comentarios nuevos explican el porqué y citan la decisión del spec.
- **Chequeo de formato** sobre los `.cs` que toca cada tarea (los de `Migrations/` no). `ENDOFLINE` y `CHARSET` son ruido previo del repo y se filtran:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-facturar-pedido-format"
Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
dotnet format Backend.slnx --verify-no-changes --no-restore --include $files --report $report
$reportFile = Join-Path $report "format-report.json"
if (Test-Path $reportFile) {
    (Get-Content $reportFile -Raw | ConvertFrom-Json) | ForEach-Object {
        $document = $_
        $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
            ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
    }
}
```

Esperado: sin salida en el último bloque, o sólo líneas que no tocaste (se anotan en el handoff).

## Review Focus

Los cinco casos que el spec implica y que ninguna prueba nombrada por él cubre, del más probable al menos probable. Cada uno tiene su prueba en la tarea dueña del código:

1. **El handler revalida con el permiso equivocado.** Las pruebas de API conceden `approve` además de `invoice` (hace falta aprobar antes de facturar), así que un `InvoiceOrderHandler` que llamara `EnsureAuthorized(..., OrderApprove)` pasaría todas. Lo esperable: con sólo `quotations.order.approve` el handler responde `authorization.denied` y no toca el pedido; con sólo `quotations.order.invoice`, factura y revierte. → Task 3, `OrderInvoicingHandlerTests`.
2. **Anular un facturado con el motivo vacío.** Si el guard nuevo quedara después de `NormalizeCancellationReason`, la respuesta sería `cancellation_reason_required` y el frontend mostraría «escribe un motivo» sobre un pedido que de todas formas no se puede anular. Lo esperable: `already_invoiced` con motivo presente, `null` o en blanco. → Task 1, `CancelAnInvoicedOrderIsRejectedBeforeLookingAtTheReason`; Task 3, `InvoiceAndRevertRejectTheWrongStateWithTheirDomainCodes`.
3. **El ciclo facturar → revertir → volver a facturar o anular.** Revertir tiene que dejar un `Approved` limpio: refacturar guarda el quién y cuándo nuevos, y anular funciona. → Task 1, `ARevertedOrderCanBeInvoicedAgainOrCancelled`; Task 3, `InvoicingRevertedLeavesTheOrderApprovedAndCancellable`.
4. **Facturar un pedido con el pago pendiente.** La decisión 6 es explícitamente provisional («De momento»), y alguien puede «arreglarla» de pasada. Lo esperable hoy: un pedido `PaymentPending` aprobado se factura y conserva su `paymentStatus`. → Task 1, `InvoiceDoesNotRequireTheOrderToBePaid`; Task 3, `InvoiceAnApprovedOrderReturnsItInvoicedWithWhoAndWhen`.
5. **Un facturado deja de contar en el resumen de pedidos.** `OrdersReportSource.cs:71` excluye `Cancelled`; reescribir ese filtro como «sólo `Approved`» sacaría las ventas facturadas sin que ninguna prueba existente se entere. → Task 3, `OrdersReportSummaryApiTests.SummaryCountsAnInvoicedOrderAsASale` (prueba de guarda).

---

## Hallazgos contra el código (2026-10-05)

Verificados en `feature/facturar-pedido` = `origin/develop` (`d9bc133`) + los tres commits del spec.

1. **Los pedidos ya se direccionan por su id.** `OrderEndpoints.cs:24-26` agrupa todo lo que se le hace a un pedido bajo `/api/v1/tenants/{tenantId:guid}/orders`, y `approve`/`cancel` cuelgan de `/{orderId:guid}/…` (`:80-95`). `ApproveOrderHandler` busca con `FindByIdAsync` y responde `OrderNotFound.ById` (`ApproveOrder.cs:40-42`, `OrderNotFound.cs:121-122`). Las rutas del spec encajan sin cambiar nada.
2. **Los roles de fábrica viven en código: no hay migración de permisos.** `RoleDefinition` de admin (`QepServiceCollectionExtensions.cs:606-613`) y de billing (`:689-692`). Agregar el permiso a esas dos listas lo concede en el deploy; un rol custom no lo recibe. Mismo criterio que el precedente de anular (`docs/superpowers/plans/2026-09-16-anular-pedido-backend.md`, hallazgo 1).
3. **La etiqueta de exportación viaja en el commit del dominio, no al final.** `ExportStatusLabelsTests.EveryOrderStatusHasTheLabelOfTheOrdersTable` recorre `Enum.GetValues<OrderStatus>()` (`ExportStatusLabelsTests.cs:52-57`), y `ExportStatusLabels.For(OrderStatus)` tira en la rama por defecto (`ExportStatusLabels.cs:97`). Agregar `OrderStatus.Invoiced` sin la etiqueta deja el commit en rojo. Lo mismo `Order.InvoicedBy` (`MemberId?`) sin su conversión: rompe la construcción del modelo de EF. Por eso Task 1 junta dominio, etiqueta, mapeo, migración y sonda. **Desvío del orden de «Entrega» del spec** («dominio y persistencia → permiso → handlers y endpoints → etiquetas»): las etiquetas pasan al primer commit.
4. **`revertedBy` no llega a la auditoría como membresía.** `IQuotationAuditPublisher.Publish` recibe `actorId` y los handlers le pasan `executionContext.SubjectId`, el **usuario** (`ApproveOrder.cs:50-56`), no la membresía. El spec dice que `revertedBy` «sólo viaja a la auditoría»; en la práctica quien revirtió queda registrado como el usuario actor, igual que en aprobar y anular. El handler resuelve la membresía igual (`QuotationAdvisorResolver.ResolveAsync`) porque eso es lo que exige una membresía **activa** (403 `authorization.denied` si no la hay) y se la pasa a `Order.RevertInvoicing`, que no la guarda. Se conserva la firma del spec. Un parámetro sin usar no rompe el build: `.editorconfig` no sube `IDE0060` y no hay analizadores de terceros.
5. **Precedencia de errores en `/invoice` y `/uninvoice`** (la misma que aprobar): política del endpoint (403) → pedido del tenant (404 `order.order.not_found`) → membresía activa (403 `authorization.denied`, `QuotationAdvisorResolver`) → dominio (422). Dentro de `Cancel`, `already_invoiced` va primero, antes de `already_cancelled` y del motivo.
6. **Los 422 son códigos de dominio.** `QuotationsDomainException` responde 422 con `code` y **sin** `errors` (mapeo central en `src/Api/ApiExceptionHandler.cs`, por tipo de excepción). Ningún código se registra en otro lado.
7. **Lo que ya funciona sin tocar nada.** Aprobar, sumar/quitar/corregir comprobantes, recalcular, editar notas y agregar productos sobre un facturado responden `order.order.not_pending` (`Order.cs:128,201,258,289,416`, `AddOrderItems.cs:64`, `PreviewOrderEdits.cs:66`, `SaveOrderEdits.cs:73`, `RemoveQuotationItem.cs:43`, `UpdateQuotationItem.cs:54`). El filtro `status=Invoiced` del listado y de la exportación entra por `Enum.TryParse` (`OrderListing.cs:14-25`). El resumen del reporte sólo excluye `Cancelled` (`OrdersReportSource.cs:71`).
8. **La sonda de usuarios ya cubre `approved_by` y `cancelled_by`** (`QuotationUserReferenceProbe.cs:16-21,48-52`), con una prueba por columna en `OrphanUserCleanupTests` (`ApprovingAnOrderKeepsTheUser`, `CancellingAnOrderKeepsTheUser`). `invoiced_by` se suma igual.
9. **Línea de `AuthorizationCatalogApiTests` distinta a la del spec.** El spec cita `:95-102`; en el código la lista de billing está en `:94-95` y la de admin en `:85-90`. Se usan las del código.
10. **El contrato documentado no trae `version`.** `OrderResponse` lleva `long Version` (`OrdersDtos.cs:138`) y `docs/integracion-cotizaciones-y-pedidos.md:127-136` no lo lista. Gana el código: Task 4 lo agrega al tocar ese bloque.
11. **El spec no fija el texto del permiso.** El catálogo muestra `DisplayName` y `Description`. Este plan usa «Facturar pedidos» / «Permite marcar como facturado un pedido aprobado y revertir esa marca.», en el mismo registro que «Aprobar pedidos» y «Anular pedidos». Se anota en el handoff para que el owner lo confirme.

## Contrato HTTP resultante (para el plan del frontend)

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/invoice` y `POST /api/v1/tenants/{tenantId}/orders/{orderId}/uninvoice`, sin cuerpo, con `X-Qep-Client: web` como todo POST.

```ts
type OrderResponse = {
  id: string; orderNumber: string; quotationId: string;
  status: "Pending" | "Approved" | "Cancelled" | "Invoiced";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string;
  approvedAt: string | null; approvedBy: string | null;
  cancelledAt: string | null; cancelledBy: string | null; cancellationReason: string | null;
  invoicedAt: string | null; // ISO 8601 con offset; null si no está facturado (revertir lo limpia)
  invoicedBy: string | null; // uuid de membership (no de usuario); null si no está facturado
  ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  version: number;
  paymentProofs: { id: string; fileId: string; amount: number; uploadedAt: string }[];
}
```

| Ruta | HTTP | `code` | Cuándo |
|---|---|---|---|
| `/invoice` | 200 | — | Body `OrderResponse` con `status: "Invoiced"`, `invoicedAt`/`invoicedBy` llenos y `approvedAt`/`approvedBy` intactos |
| `/uninvoice` | 200 | — | Body `OrderResponse` con `status: "Approved"` e `invoicedAt`/`invoicedBy` en `null` |
| las dos | 403 | — (política) o `authorization.denied` | Sin `quotations.order.invoice` (tener `quotations.order.approve` no alcanza), otro tenant o sin membresía activa |
| las dos | 404 | `order.order.not_found` | El `orderId` no existe en el tenant |
| `/invoice` | 422 | `order.order.not_approved` | El pedido está `Pending`, `Cancelled` o ya `Invoiced` |
| `/uninvoice` | 422 | `order.order.not_invoiced` | El pedido no está `Invoiced` |
| `/cancel` | 422 | `order.order.already_invoiced` | El pedido está `Invoiced`, con o sin motivo |

Los 422 son **códigos de dominio** (ProblemDetails con `code`, **sin** `errors`).

---

## File Structure

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderInvoicing.cs` (+ `.Designer.cs`) | 1 | Dos columnas nullable en `quotations.orders` |
| `src/Modules/Quotations/Modules.Quotations.Application/InvoiceOrder.cs` | 3 | `InvoiceOrderCommand` e `InvoiceOrderHandler` |
| `src/Modules/Quotations/Modules.Quotations.Application/RevertOrderInvoicing.cs` | 3 | `RevertOrderInvoicingCommand` y `RevertOrderInvoicingHandler` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderInvoicingHandlerTests.cs` | 3 | La mitad del permiso que revalida el handler |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs` | 1 | `Invoiced` |
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` | 1 | `InvoicedAt`, `InvoicedBy`, `Invoice`, `RevertInvoicing`, guard en `Cancel` |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs` | 1 | `Invoiced => "Facturado"` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | 1 | Mapeo de las dos columnas |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | 1 | Regenerado por `dotnet ef` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs` | 1 | `invoiced_by` retiene al usuario |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs` | 2 | `OrderInvoice` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | 2, 3 | Permiso, política, roles admin y billing (2); registro de los dos handlers (3) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` | 3 | `OrderDto` y `OrderResponse` con `InvoicedAt`/`InvoicedBy` |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs` | 3 | `ToDto` con los dos campos |
| `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` | 3 | `POST /invoice`, `POST /uninvoice` y `ToResponse` |
| `docs/integracion-cotizaciones-y-pedidos.md` | 4 | Estados, endpoints, DTO y códigos |

**Pruebas**

| Archivo | Tarea |
|---|---|
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` | 1 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs` | 1 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` | 1 |
| `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs` | 1 |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` | 2 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderInvoicingHandlerTests.cs` | 3 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs` | 3 |
| `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs` | 3 |

**No se tocan, a propósito:** `OrdersReportSource.cs` (decisión 7), los guards `not_pending` (spec, «Dominio»), `IQuotationAuditPublisher` y el módulo Audit (hallazgo 4), las migraciones de Authorization (hallazgo 2), `OrderListing`/`OrdersExportProcessor` (hallazgo 7).

## Entrega

| Commit | Tarea |
|---|---|
| `docs(quotations): plan backend de facturar un pedido` | (ya hecho) |
| `feat(quotations): estado Invoiced y columnas de facturación del pedido` | 1 |
| `feat(quotations): permiso quotations.order.invoice para admin y facturación` | 2 |
| `feat(quotations): endpoints para facturar y revertir la facturación de un pedido` | 3 |
| `docs(quotations): contrato de facturar un pedido` | 4 |

---

### Task 0: Rama, herramientas y línea base

**Files:** ninguno.

**Interfaces:**
- Consumes: nada.
- Produces: la rama comprobada y la evidencia de que el build y el modelo de EF arrancan limpios.

- [ ] **Step 1: Comprobar rama, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
git branch --show-current
git status --short
git fetch origin
git rev-list --left-right --count origin/develop...HEAD
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: rama `feature/facturar-pedido`; `git status` vacío; el `rev-list` da `0	4` (tres commits del spec y el de este plan) o `N	4` si `develop` avanzó; `Get-Process` sin salida; `docker info` da una versión; `dotnet ef` da `10.0.x`. Si la rama no es `feature/facturar-pedido`, **para y pregunta**. Si el primer número es mayor que 0, `develop` avanzó: re-verifica las líneas de los hallazgos antes de seguir, y si alguna cambió, **para y pregunta**.

- [ ] **Step 2: Restore, build y modelo sin cambios pendientes**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; `No changes have been made to the model since the last migration.` Si el modelo ya tiene cambios pendientes, **para y pregunta**: la migración de Task 1 arrastraría deriva ajena.

---

### Task 1: Dominio, etiqueta, persistencia y sonda de la facturación

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs:1-19`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:96` (propiedades), `:151-158` (guard en `Cancel`), `:168` (métodos nuevos)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs:92-98`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:443`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderInvoicing.cs` (+ `.Designer.cs`, generados)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` (regenerado)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs:16-21,48-52`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `ExportStatusLabelsTests.cs:31-37`, `QuotationsDbContextMappingTests.cs`, `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `OrderStatus.Invoiced`
  - `public DateTimeOffset? Order.InvoicedAt { get; }`, `public MemberId? Order.InvoicedBy { get; }`
  - `public void Order.Invoice(MemberId invoicedBy, DateTimeOffset occurredAt)` — lanza `QuotationsDomainException` con `order.order.not_approved`
  - `public void Order.RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)` — lanza `QuotationsDomainException` con `order.order.not_invoiced`
  - `Order.Cancel` lanza `order.order.already_invoiced` sobre un `Invoiced`, antes que cualquier otra regla
  - `ExportStatusLabels.For(OrderStatus.Invoiced) == "Facturado"`
  - Columnas `quotations.orders.invoiced_at` (`timestamp with time zone`) e `invoiced_by` (`uuid`), las dos nullable
  - `QuotationUserReferenceProbe` retiene al usuario cuya membresía está en `orders.invoiced_by`

- [ ] **Step 1: Escribir las pruebas de dominio que fallan**

Agrega al final de `OrderTests.cs`, antes de la llave que cierra la clase:

```csharp

    // Spec 2026-10-05 (facturar un pedido), decisiones 1 y 3: se factura un aprobado, queda quién
    // y cuándo, y la aprobación no se toca.
    [Fact]
    public void InvoiceAnApprovedOrderRecordsWhoAndWhenAndKeepsTheApproval()
    {
        var order = NewOrder();
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);
        var invoicedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Invoice(invoicedBy, later);

        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(later, order.InvoicedAt);
        Assert.Equal(invoicedBy, order.InvoicedBy);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(3, order.Version);
    }

    // Decisión 6: de momento facturar no mira el pago, igual que aprobar.
    [Fact]
    public void InvoiceDoesNotRequireTheOrderToBePaid()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);
        order.Approve(ConvertedBy, Now);

        order.Invoice(ConvertedBy, Now.AddDays(1));

        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    // Decisión 1: sólo desde Approved. Desde Pending se saltaría la revisión; un anulado ya no es
    // una venta.
    [Fact]
    public void InvoiceRejectsAPendingOrACancelledOrder()
    {
        var pending = NewOrder();
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromPending = Assert.Throws<QuotationsDomainException>(() =>
            pending.Invoice(ConvertedBy, Now));
        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.Invoice(ConvertedBy, Now));

        Assert.Equal("order.order.not_approved", fromPending.Code);
        Assert.Equal("order.order.not_approved", fromCancelled.Code);
        Assert.Equal(OrderStatus.Pending, pending.Status);
        Assert.Null(pending.InvoicedAt);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedBy);
    }

    // Facturar dos veces reescribiría quién y cuándo se facturó.
    [Fact]
    public void InvoiceTwiceIsRejectedAndKeepsTheFirstInvoicing()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Invoice(new MemberId(Guid.CreateVersion7()), Now.AddDays(1)));

        Assert.Equal("order.order.not_approved", error.Code);
        Assert.Equal(ConvertedBy, order.InvoicedBy);
        Assert.Equal(Now, order.InvoicedAt);
        Assert.Equal(3, order.Version);
    }

    // Decisión 4: revertir vuelve a Approved y limpia las marcas. La aprobación queda.
    [Fact]
    public void RevertInvoicingReturnsToApprovedWithoutTheMarks()
    {
        var order = NewOrder();
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);
        order.Invoice(ConvertedBy, Now.AddDays(1));
        var later = Now.AddDays(2);

        order.RevertInvoicing(new MemberId(Guid.CreateVersion7()), later);

        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(4, order.Version);
    }

    // Review Focus 3: el pedido revertido es un Approved limpio. Se vuelve a facturar con el quién y
    // el cuándo nuevos, o se anula.
    [Fact]
    public void ARevertedOrderCanBeInvoicedAgainOrCancelled()
    {
        var reinvoiced = NewOrder();
        reinvoiced.Approve(ConvertedBy, Now);
        reinvoiced.Invoice(ConvertedBy, Now);
        reinvoiced.RevertInvoicing(ConvertedBy, Now.AddDays(1));
        var secondBiller = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(2);

        reinvoiced.Invoice(secondBiller, later);

        Assert.Equal(OrderStatus.Invoiced, reinvoiced.Status);
        Assert.Equal(secondBiller, reinvoiced.InvoicedBy);
        Assert.Equal(later, reinvoiced.InvoicedAt);

        var cancelled = NewOrder();
        cancelled.Approve(ConvertedBy, Now);
        cancelled.Invoice(ConvertedBy, Now);
        cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1));

        cancelled.Cancel(ConvertedBy, "Facturado por error", later);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedAt);
    }

    [Fact]
    public void RevertInvoicingRejectsAnOrderThatIsNotInvoiced()
    {
        var pending = NewOrder();
        var approved = NewOrder();
        approved.Approve(ConvertedBy, Now);
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromPending = Assert.Throws<QuotationsDomainException>(() =>
            pending.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromApproved = Assert.Throws<QuotationsDomainException>(() =>
            approved.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_invoiced", fromPending.Code);
        Assert.Equal("order.order.not_invoiced", fromApproved.Code);
        Assert.Equal("order.order.not_invoiced", fromCancelled.Code);
        Assert.Equal(OrderStatus.Approved, approved.Status);
        Assert.Equal(2, approved.Version);
    }

    // Decisión 2: un facturado no se anula. El guard va antes que already_cancelled y que las reglas
    // del motivo (spec, «Dominio»): con el motivo vacío la respuesta sigue siendo already_invoiced.
    [Theory]
    [InlineData("El cliente desistió")]
    [InlineData(null)]
    [InlineData("   ")]
    public void CancelAnInvoicedOrderIsRejectedBeforeLookingAtTheReason(string? reason)
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now.AddDays(1)));

        Assert.Equal("order.order.already_invoiced", error.Code);
        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancellationReason);
    }

    // Los guards existentes ya cubren un facturado: no es Pending, así que nada de lo que sólo se
    // permite en Pending pasa (spec, «Dominio»). Tampoco se vuelve a aprobar.
    [Fact]
    public void AnInvoicedOrderRejectsEveryPendingOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Approve(ConvertedBy, Now);
        order.Invoice(ConvertedBy, Now);
        var later = Now.AddDays(1);

        var addProofs = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                OrderPaymentStatus.FullPaymentReceived,
                null,
                ConvertedBy,
                later));
        var recalculate = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(200_000m, later));
        var removeProof = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, later));
        var approve = Assert.Throws<QuotationsDomainException>(() =>
            order.Approve(ConvertedBy, later));
        var attach = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, later));
        var correct = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], later));
        var notes = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", later));

        Assert.Equal("order.order.not_pending", addProofs.Code);
        Assert.Equal("order.order.not_pending", recalculate.Code);
        Assert.Equal("order.order.not_pending", removeProof.Code);
        Assert.Equal("order.order.not_pending", approve.Code);
        Assert.Equal("order.order.not_pending", attach.Code);
        Assert.Equal("order.order.not_pending", correct.Code);
        Assert.Equal("order.order.not_pending", notes.Code);
        Assert.Equal(OrderStatus.Invoiced, order.Status);
        Assert.Equal(3, order.Version);
    }
```

En `ExportStatusLabelsTests.cs`, reemplaza el diccionario de `EveryOrderStatusHasTheLabelOfTheOrdersTable` (líneas 31-37) por:

```csharp
            new Dictionary<OrderStatus, string>
            {
                [OrderStatus.Pending] = "Pendiente",
                [OrderStatus.Approved] = "Aprobado",
                // Decisión 6 del spec 2026-09-16: la exportación muestra los anulados.
                [OrderStatus.Cancelled] = "Anulado",
                // Decisión 7 del spec 2026-10-05: y los facturados.
                [OrderStatus.Invoiced] = "Facturado",
            },
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExportStatusLabelsTests"
```

Esperado: FAIL de compilación, `CS0117: 'OrderStatus' no contiene una definición para 'Invoiced'` y `CS1061: 'Order' no contiene una definición para 'Invoice'` (o sus equivalentes en inglés).

- [ ] **Step 3: Implementar el dominio y la etiqueta**

`OrderStatus.cs` completo:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido nace <see cref="Pending"/> y otra persona lo revisa antes de aprobarlo: quien
/// convierte la cotización y quien da el visto bueno son roles distintos, y el estado es lo que
/// hace visible ese paso intermedio.
///
/// Los pedidos anteriores a esta separación quedaron en <see cref="Approved"/>: se crearon cuando
/// convertir **era** aprobar, y reescribirlos diría que alguien los revisó.
///
/// <see cref="Cancelled"/> (spec 2026-09-16) es terminal y se llega desde los otros dos: el pedido
/// no se borra, queda con quién, cuándo y por qué se anuló.
///
/// <see cref="Invoiced"/> (spec 2026-10-05) sólo se alcanza desde <see cref="Approved"/> y deja
/// constancia de que la factura se hizo fuera de QEP; revertirlo lo devuelve a <see cref="Approved"/>.
/// </summary>
public enum OrderStatus
{
    Pending,
    Approved,
    Cancelled,
    Invoiced
}
```

En `Order.cs`, después de `public string? CancellationReason { get; private set; }` (línea 96):

```csharp

    /// <summary>Cuándo se marcó como facturado y quién (spec 2026-10-05, decisión 3). Null mientras
    /// el pedido no está <see cref="OrderStatus.Invoiced"/>: revertir la facturación las limpia
    /// (decisión 4) y la historia queda en la auditoría. QEP no emite la factura; esto sólo deja
    /// constancia de que se hizo afuera.</summary>
    public DateTimeOffset? InvoicedAt { get; private set; }

    public MemberId? InvoicedBy { get; private set; }
```

En `Cancel` (líneas 151-158), el guard nuevo va primero; el método empieza así:

```csharp
    public void Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)
    {
        // Spec 2026-10-05, decisión 2: la factura ya existe fuera de QEP y anular el pedido no la
        // anula. Va antes que already_cancelled y que el motivo: un facturado nunca se anula, así que
        // pedir el motivo primero mandaría a corregir algo que no destraba nada.
        if (Status == OrderStatus.Invoiced)
        {
            throw new QuotationsDomainException(
                "order.order.already_invoiced",
                "An invoiced order cannot be cancelled; revert the invoicing first.");
        }

        if (Status == OrderStatus.Cancelled)
        {
```

(El resto de `Cancel` queda igual.) Y en el `<summary>` de `Cancel`, después de `se anuló. Todo se valida antes de cambiar un solo campo.`, agrega la línea `/// Un pedido <see cref="OrderStatus.Invoiced"/> no se anula: primero se revierte la facturación.`

Después del método `Cancel` (tras la línea 168):

```csharp

    /// <summary>
    /// Marca el pedido como facturado (spec 2026-10-05). Es sólo un cambio de estado: QEP no emite
    /// ni anula facturas ni llama a ningún sistema; la factura se hace afuera y acá queda quién la
    /// marcó y cuándo (decisión 3).
    ///
    /// Sólo desde <see cref="OrderStatus.Approved"/> (decisión 1): desde Pending se saltaría la
    /// revisión, y facturar dos veces reescribiría quién y cuándo se facturó. No mira
    /// <see cref="PaymentStatus"/> (decisión 6), igual que <see cref="Approve"/>. No toca
    /// <see cref="ApprovedAt"/>/<see cref="ApprovedBy"/>.
    /// </summary>
    public void Invoice(MemberId invoicedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Approved)
        {
            throw new QuotationsDomainException(
                "order.order.not_approved",
                "Only an approved order can be invoiced.");
        }

        Status = OrderStatus.Invoiced;
        InvoicedBy = invoicedBy;
        InvoicedAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Revierte la facturación (spec 2026-10-05, decisión 4): el pedido vuelve a
    /// <see cref="OrderStatus.Approved"/> sin <see cref="InvoicedAt"/>/<see cref="InvoicedBy"/>. Es
    /// para corregir una marca equivocada; no anula nada fuera de QEP. La aprobación no se toca.
    ///
    /// <paramref name="revertedBy"/> no se guarda en el agregado: la historia queda en la auditoría,
    /// que registra al usuario que actúa. El caso de uso lo resuelve igual, porque eso es lo que
    /// exige una membresía activa antes de tocar el pedido.
    /// </summary>
    public void RevertInvoicing(MemberId revertedBy, DateTimeOffset occurredAt)
    {
        if (Status != OrderStatus.Invoiced)
        {
            throw new QuotationsDomainException(
                "order.order.not_invoiced",
                "Only an invoiced order can have its invoicing reverted.");
        }

        Status = OrderStatus.Approved;
        InvoicedBy = null;
        InvoicedAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }
```

En `ExportStatusLabels.cs`, el switch de `OrderStatus` (líneas 92-98) queda:

```csharp
    public static string For(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "Pendiente",
        OrderStatus.Approved => "Aprobado",
        OrderStatus.Cancelled => "Anulado",
        OrderStatus.Invoiced => "Facturado",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The order status has no export label."),
    };
```

- [ ] **Step 4: Correr las pruebas de dominio y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExportStatusLabelsTests"
```

Esperado: PASS, 0 con error. (No se commitea todavía: el modelo de EF está roto hasta el Step 7 — hallazgo 3.)

- [ ] **Step 5: Escribir la prueba de mapeo que falla**

En `QuotationsDbContextMappingTests.cs`, después de `OrderCancellationMapsToNullableSnakeCaseColumns`:

```csharp

    /// <summary>
    /// Spec 2026-10-05: las dos columnas de la facturación, nullable (un pedido sin facturar no las
    /// tiene, y revertir las vuelve a null) y con el nombre en snake_case que fija el mapeo a mano.
    /// `invoiced_by` necesita la conversión de <see cref="MemberId"/>: sin ella EF no puede mapear
    /// el struct.
    /// </summary>
    [Fact]
    public void OrderInvoicingMapsToNullableSnakeCaseColumns()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(Order))!;

        var invoicedAt = order.FindProperty(nameof(Order.InvoicedAt))!;
        var invoicedBy = order.FindProperty(nameof(Order.InvoicedBy))!;

        Assert.Equal("invoiced_at", invoicedAt.GetColumnName());
        Assert.Equal("invoiced_by", invoicedBy.GetColumnName());
        Assert.True(invoicedAt.IsNullable);
        Assert.True(invoicedBy.IsNullable);
    }
```

- [ ] **Step 6: Correr las pruebas de mapeo y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: FAIL en todas las pruebas de la clase con `InvalidOperationException` al construir el modelo, del estilo `The property 'Order.InvoicedBy' could not be mapped because it is of type 'Nullable<MemberId>'`.

- [ ] **Step 7: Mapear las columnas y generar la migración**

En `QuotationsDbContext.cs`, justo después del bloque de `CancellationReason` (tras la línea 443, `.HasMaxLength(Order.CancellationReasonMaxLength);`):

```csharp
        // Spec 2026-10-05: quién y cuándo se marcó como facturado. Nullables, porque un pedido sin
        // facturar no tiene nada que guardar acá y revertir las vuelve a null; misma conversión
        // nullable que approved_by.
        order.Property(value => value.InvoicedAt).HasColumnName("invoiced_at");
        order.Property(value => value.InvoicedBy)
            .HasColumnName("invoiced_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
```

Genera la migración:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddOrderInvoicing --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
```

Esperado: `Done.`; `git status` muestra `?? ..._AddOrderInvoicing.cs`, `?? ..._AddOrderInvoicing.Designer.cs` y ` M QuotationsDbContextModelSnapshot.cs`, nada más. Abre `<ts>_AddOrderInvoicing.cs` y comprueba que `Up` tiene **sólo** estas dos operaciones (el orden puede variar) y `Down` sus dos `DropColumn`:

```csharp
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invoiced_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invoiced_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);
```

Si aparece cualquier otra operación (un `AlterColumn`, un índice, otra tabla), hay deriva previa: **para y pregunta**. Si está bien, reemplaza la línea `/// <inheritdoc />` que precede a `public partial class AddOrderInvoicing : Migration` por este comentario (mismo criterio que `20260917033333_AddOrderCancellation.cs:8-13`):

```csharp
    /// <summary>
    /// Quién marcó el pedido como facturado y cuándo (spec 2026-10-05).
    ///
    /// Nullables y sin backfill: ningún pedido existente está facturado. `status` ya es
    /// varchar(20) sin check constraint, así que `Invoiced` entra sin tocar esa columna.
    /// </summary>
```

- [ ] **Step 8: Correr las pruebas unitarias tocadas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExportStatusLabelsTests|FullyQualifiedName~QuotationsDbContextMappingTests"
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: PASS, 0 con error (incluidas `OrderInvoicingMapsToNullableSnakeCaseColumns` y `TheModelHasNoChangesPendingAMigration`); `No changes have been made to the model since the last migration.`

- [ ] **Step 9: Escribir la prueba de la sonda que falla**

En `OrphanUserCleanupTests.cs`, después de `CancellingAnOrderKeepsTheUser` (termina en la línea 289):

```csharp

    // Spec 2026-10-05: invoiced_by también retiene. El facturador es una tercera membresía y la
    // aprobación la da el asesor, así que lo único que apunta al facturador es invoiced_by.
    [Fact]
    public async Task InvoicingAnOrderKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var advisor = await InviteAsync(ownerClient, tenantId, NewEmail());
        var biller = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, advisor.Id);
        await ActivateMembershipAsync(connectionString, biller.Id);
        await SeedOrderAsync(
            factory,
            Guid.Parse(tenantId),
            convertedByMembershipId: advisor.Id,
            approvedByMembershipId: advisor.Id,
            invoicedByMembershipId: biller.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, biller.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, biller.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, biller.UserId));
    }
```

En el mismo archivo, `SeedOrderAsync` recibe el parámetro nuevo y factura después de aprobar. Su comentario y su firma (líneas 766-775) quedan:

```csharp
    // Por el DbContext, misma razón que SeedQuotationAsync: lo que importa acá es approved_by,
    // cancelled_by o invoiced_by, no el resto del contrato de pedidos. La cotización subyacente
    // existe sólo porque orders.quotation_id tiene FK — su contenido no se ejercita.
    private static async Task SeedOrderAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid convertedByMembershipId,
        Guid? approvedByMembershipId = null,
        Guid? cancelledByMembershipId = null,
        Guid? invoicedByMembershipId = null)
```

y, entre el `if` de `approvedByMembershipId` y el de `cancelledByMembershipId`:

```csharp

        // Facturar exige Approved (spec 2026-10-05, decisión 1): quien pida invoicedBy tiene que
        // pedir también approvedBy.
        if (invoicedByMembershipId is { } invoicedBy)
        {
            order.Invoice(new MemberId(invoicedBy), occurredAt);
        }
```

- [ ] **Step 10: Correr la prueba de la sonda y ver que falla**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~OrphanUserCleanupTests.InvoicingAnOrderKeepsTheUser"
```

Esperado: FAIL con `Assert.Equal() Failure: Values differ — Expected: 1, Actual: 0`: la sonda no mira `invoiced_by`, así que el worker borra al facturador. La migración nueva se aplica al arrancar el host de pruebas: si en cambio falla con un error de Npgsql o de migración, **para y revisa** el Step 7.

- [ ] **Step 11: Sumar `invoiced_by` a la sonda**

En `QuotationUserReferenceProbe.cs`, el `<remarks>` (líneas 16-21) queda:

```csharp
/// <remarks>
/// Cubre cada columna mapeada con <see cref="MemberId"/> en <see cref="QuotationsDbContext"/>:
/// <c>quotations.advisor_id</c>, <c>created_by</c> y <c>updated_by</c>;
/// <c>quotation_history.member_id</c>; <c>orders.converted_by</c>, <c>orders.approved_by</c>,
/// <c>orders.cancelled_by</c> y <c>orders.invoiced_by</c>; y
/// <c>order_payment_proofs.uploaded_by</c>. Una columna nueva con <see cref="MemberId"/> tiene que
/// sumarse acá, o el usuario que la referencia se borra igual.
/// </remarks>
```

y la consulta de pedidos (líneas 48-52):

```csharp
                await dbContext.Orders.AnyAsync(
                    order => order.ConvertedBy == member ||
                        order.ApprovedBy == member ||
                        order.CancelledBy == member ||
                        order.InvoicedBy == member,
                    cancellationToken) ||
```

- [ ] **Step 12: Correr las clases tocadas y arquitectura, y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~OrphanUserCleanupTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS en los dos, 0 con error. `OrphanUserCleanupTests` entero porque `SeedOrderAsync` cambió de firma y lo usan otras dos pruebas.

- [ ] **Step 13: Chequeo de formato y commit**

Corre **el chequeo de formato** (Global Constraints). Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
$migrations = git ls-files --others --exclude-standard -- "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddOrderInvoicing*.cs"
if ((git branch --show-current) -ne "feature/facturar-pedido") { throw "ABORT" }; git add src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs src/Modules/Quotations/Modules.Quotations.Domain/Order.cs src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationUserReferenceProbe.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs $migrations tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs; git commit -m "feat(quotations): estado Invoiced y columnas de facturación del pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: el `Select-String` sin salida y `git status` vacío.

---

### Task 2: Permiso `quotations.order.invoice` para admin y facturación

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs:15`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:613` (rol admin), `:692` (rol billing), `:865` (definición), `:1118` (política)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs:45-50,63-95`

**Interfaces:**
- Consumes: nada.
- Produces: `public const string OrdersPermissions.OrderInvoice = "quotations.order.invoice";` con su `PermissionDefinition` (Quotations, `medium`), su política de autorización del mismo nombre, y concedido a los roles `admin` y `billing` y a ningún otro.

Sin migración de datos: hallazgo 2.

- [ ] **Step 1: Escribir la prueba del catálogo que falla**

En `AuthorizationCatalogApiTests.cs`, dentro de `TheCatalogNamesTheOrderPermissions`, reemplaza el arreglo `expected` (líneas 63-75) por:

```csharp
        CatalogPermissionPayload[] expected =
        [
            new("quotations.order.approve", "Aprobar pedidos",
                "Permite dar el visto bueno a un pedido pendiente.", "Quotations", "medium"),
            new("quotations.order.cancel", "Anular pedidos",
                "Permite anular un pedido pendiente o aprobado, con un motivo obligatorio.", "Quotations", "high"),
            new("quotations.order.invoice", "Facturar pedidos",
                "Permite marcar como facturado un pedido aprobado y revertir esa marca.", "Quotations", "medium"),
            new("quotations.order.manage", "Gestionar pedidos",
                "Permite convertir una cotización enviada en pedido, con sus comprobantes de pago.", "Quotations", "medium"),
            new("quotations.order.read", "Leer pedidos",
                "Permite consultar el pedido convertido de una cotización.", "Quotations", "low"),
            new("reporting.orders.read", "Reporte de pedidos",
                "Permite consultar y exportar el reporte de pedidos convertidos del tenant.", "Reporting", "low"),
        ];
```

y las aserciones de admin y billing (líneas 83-95, desde el comentario `// Spec 2026-09-16, decisión 5` hasta `OrderPermissionsOf(catalog, "billing"));`) por:

```csharp
        // Spec 2026-09-16, decisión 5: anular es sólo de admin. Aprobar es de admin y facturación,
        // nunca del asesor: quien registra el pedido no es quien lo revisa. Facturar (spec
        // 2026-10-05, decisión 5) es de admin y facturación, y cubre también revertir.
        Assert.Equal(
            [
                "quotations.order.approve", "quotations.order.cancel", "quotations.order.invoice",
                "quotations.order.manage", "quotations.order.read", "reporting.orders.read",
            ],
            OrderPermissionsOf(catalog, "admin"));
        Assert.Equal(
            ["quotations.order.manage", "quotations.order.read", "reporting.orders.read"],
            OrderPermissionsOf(catalog, "advisor"));
        Assert.Equal(
            ["quotations.order.approve", "quotations.order.invoice", "quotations.order.read"],
            OrderPermissionsOf(catalog, "billing"));
```

En el `<summary>` de la prueba (líneas 45-50), después de `aprobar también es propio, de admin y facturación.` agrega: `Facturar (spec 2026-10-05) es propio, de admin y facturación.`

- [ ] **Step 2: Correr la prueba y ver que falla**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests.TheCatalogNamesTheOrderPermissions"
```

Esperado: FAIL en el primer `Assert.Equal` (`Assert.Equal() Failure: Collections differ`), porque el catálogo no trae `quotations.order.invoice`.

- [ ] **Step 3: Declarar el permiso, su política y dárselo a admin y facturación**

En `OrdersPermissions.cs`, después de `public const string OrderCancel = "quotations.order.cancel";` (línea 15):

```csharp

    /// <summary>Marcar un pedido aprobado como facturado y revertir esa marca (spec 2026-10-05,
    /// decisión 5). Un solo permiso para las dos cosas: con uno aparte para revertir, facturación
    /// dependería de un admin para corregir su propio error.</summary>
    public const string OrderInvoice = "quotations.order.invoice";
```

En `QepServiceCollectionExtensions.cs`, en la `RoleDefinition` de `admin`, después de `OrdersPermissions.OrderCancel,` (línea 613):

```csharp
                // Facturar y revertir la facturación (spec 2026-10-05, decisión 5): admin y
                // facturación. El rol vive en código, así que no hay migración de datos.
                OrdersPermissions.OrderInvoice,
```

En la `RoleDefinition` de `billing`, después de `OrdersPermissions.OrderApprove,` (línea 692):

```csharp
                // Marcar como facturado lo que ya facturó afuera, y revertir la marca si se
                // equivocó (spec 2026-10-05, decisión 5). Es sólo el estado del pedido: la factura
                // no la emite QEP.
                OrdersPermissions.OrderInvoice,
```

Después de la `PermissionDefinition` de `OrdersPermissions.OrderCancel` (tras la línea 865, `"high"));`):

```csharp
        // Medium, igual que aprobar: sólo cambia el estado del pedido, no emite ni anula nada fuera
        // de QEP (spec 2026-10-05).
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderInvoice,
            "Facturar pedidos",
            "Permite marcar como facturado un pedido aprobado y revertir esa marca.",
            "Quotations",
            "medium"));
```

Después de la política de `OrdersPermissions.OrderCancel` (tras la línea 1118), dentro de la misma cadena:

```csharp
            .AddPolicy(
                OrdersPermissions.OrderInvoice,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderInvoice))
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS en los dos, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
if ((git branch --show-current) -ne "feature/facturar-pedido") { throw "ABORT" }; git add src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs; git commit -m "feat(quotations): permiso quotations.order.invoice para admin y facturación"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task 3: Casos de uso, endpoints y contrato de facturar y revertir

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/InvoiceOrder.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/RevertOrderInvoicing.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:24` (`OrderDto`), `:134` (`OrderResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs:20`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs:95,412,414-435`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:371`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderInvoicingHandlerTests.cs` (nuevo), `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs`, `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs`

**Interfaces:**
- Consumes (Task 1): `Order.Invoice(MemberId, DateTimeOffset)`, `Order.RevertInvoicing(MemberId, DateTimeOffset)`, `Order.InvoicedAt`, `Order.InvoicedBy`, `OrderStatus.Invoiced`, el guard `already_invoiced` de `Order.Cancel`. (Task 2): `OrdersPermissions.OrderInvoice` y su política.
- Produces:
  - `public sealed record InvoiceOrderCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;`
  - `public sealed class InvoiceOrderHandler : ICommandHandler<InvoiceOrderCommand, OrderDto>`
  - `public sealed record RevertOrderInvoicingCommand(Guid TenantId, Guid OrderId) : ICommand<OrderDto>;`
  - `public sealed class RevertOrderInvoicingHandler : ICommandHandler<RevertOrderInvoicingCommand, OrderDto>`
  - `OrderDto` y `OrderResponse` con `DateTimeOffset? InvoicedAt, Guid? InvoicedBy` justo después de `CancellationReason`.
  - `POST /api/v1/tenants/{tenantId}/orders/{orderId}/invoice` y `.../uninvoice` → 200 `OrderResponse` (contrato completo arriba, «Contrato HTTP resultante»).
  - Auditoría `quotation.order.invoiced` y `quotation.order.invoice_reverted`, con `resourceId` = id del pedido.

- [ ] **Step 1: Escribir las pruebas unitarias del handler que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderInvoicingHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Facturar y revertir tienen permiso propio (<c>quotations.order.invoice</c>, spec 2026-10-05,
/// decisión 5) y no el de aprobar. Las pruebas de API conceden los dos —hay que aprobar antes de
/// facturar—, así que un handler que revalidara con <c>quotations.order.approve</c> pasaría todas;
/// estas fijan la mitad del handler (Review Focus 1). La de la política del endpoint la cubre
/// <c>OrderApiTests</c>.
/// </summary>
public sealed class OrderInvoicingHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly MemberId BillerId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvoiceWithOnlyTheApprovePermissionIsForbiddenAndLeavesTheOrderApproved()
    {
        var row = NewApprovedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.approve"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new InvoiceOrderCommand(TenantId, row.Order.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Approved, row.Order.Status);
        Assert.Null(row.Order.InvoicedAt);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task InvoiceWithTheInvoicePermissionRecordsTheMembershipAndAudits()
    {
        var row = NewApprovedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var invoiced = await handler.HandleAsync(
            new InvoiceOrderCommand(TenantId, row.Order.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Invoiced", invoiced.Status);
        Assert.Equal(Now, invoiced.InvoicedAt);
        Assert.Equal(BillerId.Value, invoiced.InvoicedBy);
        Assert.Equal(["quotation.order.invoiced"], audit.Actions);
    }

    [Fact]
    public async Task RevertWithOnlyTheApprovePermissionIsForbiddenAndLeavesTheOrderInvoiced()
    {
        var row = NewInvoicedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewRevertHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.approve"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new RevertOrderInvoicingCommand(TenantId, row.Order.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Invoiced, row.Order.Status);
        Assert.Equal(BillerId, row.Order.InvoicedBy);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task RevertWithTheInvoicePermissionReturnsTheOrderApprovedAndAudits()
    {
        var row = NewInvoicedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewRevertHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var reverted = await handler.HandleAsync(
            new RevertOrderInvoicingCommand(TenantId, row.Order.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Approved", reverted.Status);
        Assert.Null(reverted.InvoicedAt);
        Assert.Null(reverted.InvoicedBy);
        Assert.Equal(["quotation.order.invoice_reverted"], audit.Actions);
    }

    private static InvoiceOrderHandler NewInvoiceHandler(
        OrderWithQuotation row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now));

    private static RevertOrderInvoicingHandler NewRevertHandler(
        OrderWithQuotation row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now));

    private static OrderWithQuotation NewApprovedRow()
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            Guid.CreateVersion7(),
            AdvisorId,
            new DateOnly(2026, 10, 30),
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

        var order = Order.Create(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            quotation.Id,
            OrderPaymentStatus.FullPaymentReceived,
            notes: null,
            AdvisorId,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now);
        order.Approve(AdvisorId, Now);

        return new OrderWithQuotation(order, quotation);
    }

    private static OrderWithQuotation NewInvoicedRow()
    {
        var row = NewApprovedRow();
        row.Order.Invoice(BillerId, Now);
        return row;
    }

    /// <summary>Al revés de <see cref="StubExecutionContext"/>: concede sólo lo que se le nombra.
    /// Lo que estas pruebas miran es que un permiso vecino (aprobar) no alcanza, y eso no se puede
    /// decir negando uno.</summary>
    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
    }
}
```

- [ ] **Step 2: Escribir las pruebas de API que fallan**

En `OrderApiTests.cs`, después de `CancellerPermissions` (línea 39):

```csharp

    private static string OrderInvoiceUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/invoice";

    private static string OrderUninvoiceUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/uninvoice";

    // Spec 2026-10-05, decisión 5: facturar y revertir exigen su propio permiso. Lleva también el de
    // aprobar porque sólo se factura un aprobado, y la prueba tiene que llegar hasta ahí.
    private static readonly string[] InvoicerPermissions =
        [.. ApproverPermissions, OrdersPermissions.OrderInvoice];
```

Después de `CancelRejectsAMissingOrTooLongReasonAndASecondCancellation` y antes de `private static async Task AssertDomainRejectionAsync` (línea 1396):

```csharp
    // Spec 2026-10-05: facturar un aprobado deja quién y cuándo, conserva la aprobación, no exige el
    // pago completo (decisión 6, Review Focus 4) y queda auditado. Los nombres de los campos se leen
    // del JSON crudo: son el contrato que consume el frontend.
    [Fact]
    public async Task InvoiceAnApprovedOrderReturnsItInvoicedWithWhoAndWhen()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Invoiced", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("invoicedAt").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("invoicedBy").ValueKind);
            Assert.Equal("PaymentPending", root.GetProperty("paymentStatus").GetString());
        }

        var order = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(order);
        Assert.Equal(approved.Id, order.Id);
        Assert.Equal(approved.ApprovedAt, order.ApprovedAt);
        Assert.Equal(approved.ApprovedBy, order.ApprovedBy);
        Assert.Equal(approved.Version + 1, order.Version);

        var fetched = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, order.QuotationId), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Invoiced", fetched.Status);
        Assert.Equal(order.InvoicedAt, fetched.InvoicedAt);
        Assert.Equal(order.InvoicedBy, fetched.InvoicedBy);

        var invoiced = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == "quotation.order.invoiced");
        Assert.Equal(order.Id.ToString(), EntityIdOf(invoiced));
    }

    // Decisión 4: revertir vuelve a Approved, limpia las marcas y conserva la aprobación. Ya sin
    // facturar, el pedido se puede anular (Review Focus 3).
    [Fact]
    public async Task InvoicingRevertedLeavesTheOrderApprovedAndCancellable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. InvoicerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Approved", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("invoicedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("invoicedBy").ValueKind);
        }

        var reverted = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(reverted);
        Assert.Equal(approved.ApprovedAt, reverted.ApprovedAt);
        Assert.Equal(approved.ApprovedBy, reverted.ApprovedBy);

        var revertedAudit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == "quotation.order.invoice_reverted");
        Assert.Equal(approved.Id.ToString(), EntityIdOf(revertedAudit));

        var cancelled = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, approved.Id),
            new CancelOrderRequest("Facturado por error"),
            TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled", cancelled.Status);
    }

    // Decisión 5: aprobar no alcanza para facturar ni para revertir. Mismo usuario, otro juego de
    // permisos: el stub de desarrollo los toma del header.
    [Fact]
    public async Task InvoiceAndRevertWithOnlyTheApprovePermissionAreForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        using var approver = CreateClient(
            factory, ownerUserId.ToString(), tenantId.ToString(), ApproverPermissions);

        var invoice = await approver.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, invoice.StatusCode);

        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var revert = await approver.PostAsync(
            OrderUninvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, revert.StatusCode);
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, approved.QuotationId), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("Invoiced", order.Status);
    }

    // Un id de pedido que no existe en este tenant: mismo 404 que GET /orders/{orderId}.
    [Fact]
    public async Task InvoiceAndRevertAnUnknownOrderAreNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var unknown = Guid.CreateVersion7();

        var invoice = await client.PostAsync(
            OrderInvoiceUrl(tenantId, unknown), content: null, TestContext.Current.CancellationToken);
        var revert = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, unknown), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, invoice.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revert.StatusCode);
        Assert.Contains(
            "order.order.not_found",
            await invoice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains(
            "order.order.not_found",
            await revert.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // Cada 422 con su código de dominio (sin `errors`): el frontend los mapea por `code`. Un solo
    // pedido recorre los estados; los rechazos no lo modifican. Anular un facturado responde
    // already_invoiced aunque el motivo venga en blanco (Review Focus 2), y los guards Pending de
    // aprobar y agregar productos ya cubren un facturado (spec, «Dominio»).
    [Fact]
    public async Task InvoiceAndRevertRejectTheWrongStateWithTheirDomainCodes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. InvoicerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken));
        var approveUrl = $"{OrderByIdUrl(tenantId, created.Id)}/approve";

        var invoicePending = await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        var revertPending = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        (await client.PostAsync(approveUrl, content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var revertApproved = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var invoiceTwice = await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        var cancelInvoiced = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);
        var cancelInvoicedWithBlankReason = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("   "),
            TestContext.Current.CancellationToken);
        var approveInvoiced = await client.PostAsync(
            approveUrl, content: null, TestContext.Current.CancellationToken);
        var secondProductId = await CreateProductWithScalesAsync(client, tenantId);
        var addItemsToInvoiced = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(invoicePending, "order.order.not_approved");
        await AssertDomainRejectionAsync(revertPending, "order.order.not_invoiced");
        await AssertDomainRejectionAsync(revertApproved, "order.order.not_invoiced");
        await AssertDomainRejectionAsync(invoiceTwice, "order.order.not_approved");
        await AssertDomainRejectionAsync(cancelInvoiced, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(cancelInvoicedWithBlankReason, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(approveInvoiced, "order.order.not_pending");
        await AssertDomainRejectionAsync(addItemsToInvoiced, "order.order.not_pending");
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("Invoiced", order.Status);
        Assert.Null(order.CancellationReason);
    }

```

Después de `ReadOrderAsync` (el helper que sigue a `AssertDomainRejectionAsync`):

```csharp

    /// <summary>Un pedido recién convertido con el pago pendiente y ya aprobado: el punto de partida
    /// de facturar. El pago pendiente es a propósito (decisión 6): facturar no lo mira.</summary>
    private static async Task<OrderResponse> CreateApprovedOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken));

        return await ReadOrderAsync(await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/approve",
            content: null,
            TestContext.Current.CancellationToken));
    }
```

En `OrdersReportSummaryApiTests.cs`, después de `SummaryLeavesOutACancelledOrder` (termina en la línea 112):

```csharp

    /// <summary>
    /// Spec 2026-10-05, decisión 7: un facturado es una venta concretada y cuenta en el resumen.
    /// Prueba de guarda (Review Focus 5): <c>OrdersReportSource</c> sólo excluye <c>Cancelled</c>, y
    /// un filtro reescrito como «sólo Approved» la pone en rojo.
    /// </summary>
    [Fact]
    public async Task SummaryCountsAnInvoicedOrderAsASale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderApprove, OrdersPermissions.OrderInvoice]);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var order = await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);
        (await client.PostAsync(
            $"/api/v1/tenants/{tenant.TenantId}/orders/{order.Id}/approve",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"/api/v1/tenants/{tenant.TenantId}/orders/{order.Id}/invoice",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.OrderCount);
        Assert.Equal(quotation.Subtotal, summary.Subtotal);
        Assert.Equal(quotation.TaxAmount, summary.TaxAmount);
        Assert.Equal(quotation.Total, summary.Total);
        Assert.Equal(1, Assert.Single(summary.Monthly).Count);
    }
```

- [ ] **Step 3: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderInvoicingHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderApiTests.Invoic"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportSummaryApiTests.SummaryCountsAnInvoicedOrderAsASale"
```

Esperado: los dos primeros FAIL de compilación, `CS0246: No se encontró el tipo o el nombre del espacio de nombres 'InvoiceOrderCommand'` y `CS1061: 'OrderResponse' no contiene una definición para 'InvoicedAt'` (o equivalentes en inglés). El tercero compila y FAIL con `HttpRequestException` de `EnsureSuccessStatusCode` (`404 (Not Found)`): la ruta `/invoice` todavía no existe.

- [ ] **Step 4: Sumar los campos al DTO, a la respuesta y al mapeo**

En `OrdersDtos.cs`, en `OrderDto`, después de `string? CancellationReason,` (línea 24):

```csharp
    /// <summary>Cuándo se marcó como facturado y quién (id de membership, spec 2026-10-05). Null
    /// mientras el pedido no está facturado; revertir la facturación los vuelve a null.</summary>
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
```

En `OrderResponse`, después de `string? CancellationReason,` (línea 134):

```csharp
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
```

En `OrderMapping.cs`, después de `order.CancellationReason,` (línea 20):

```csharp
        order.InvoicedAt,
        order.InvoicedBy?.Value,
```

En `OrderEndpoints.cs`, en `ToResponse`, después de `order.CancellationReason,` (línea 427):

```csharp
        order.InvoicedAt,
        order.InvoicedBy,
```

- [ ] **Step 5: Crear los dos casos de uso y registrarlos**

`src/Modules/Quotations/Modules.Quotations.Application/InvoiceOrder.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record InvoiceOrderCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// Marca un pedido aprobado como facturado (spec 2026-10-05), calcado de
/// <see cref="ApproveOrderHandler"/>: mismo orden —permiso, pedido, membresía de quien actúa,
/// dominio, auditoría— y la misma unidad de trabajo.
///
/// Es sólo un cambio de estado: QEP no emite la factura ni llama a ningún sistema externo; deja
/// constancia de quién la marcó y cuándo. Permiso propio
/// (<see cref="OrdersPermissions.OrderInvoice"/>) y no el de aprobar: de fábrica lo tienen admin y
/// facturación (decisión 5).
///
/// Sin validador, porque no hay texto libre, y sin evento de outbox, igual que aprobar.
/// </summary>
public sealed class InvoiceOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<InvoiceOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        InvoiceOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderInvoice);

        // Por el id del pedido, igual que aprobar: facturación llega desde el listado de pedidos.
        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // InvoicedBy es un id de membresía, como ApprovedBy: el módulo nunca guarda el usuario.
        var invoicedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Invoice(invoicedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.invoiced",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
```

`src/Modules/Quotations/Modules.Quotations.Application/RevertOrderInvoicing.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record RevertOrderInvoicingCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// Revierte la facturación de un pedido (spec 2026-10-05, decisión 4): vuelve a <c>Approved</c>
/// sin <c>InvoicedAt</c>/<c>InvoicedBy</c>. Es para corregir una marca equivocada; no anula nada
/// fuera de QEP. Calcado de <see cref="InvoiceOrderHandler"/>, con el mismo permiso
/// (<see cref="OrdersPermissions.OrderInvoice"/>): con uno aparte, facturación dependería de un
/// admin para corregir su propio error (decisión 5).
///
/// La historia —quién facturó y quién revirtió— queda en la auditoría
/// (<c>quotation.order.invoiced</c> y <c>quotation.order.invoice_reverted</c>), no en el pedido.
/// </summary>
public sealed class RevertOrderInvoicingHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RevertOrderInvoicingCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        RevertOrderInvoicingCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderInvoice);

        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // Exige una membresía activa, igual que facturar. El agregado no la guarda (hallazgo 4 del
        // plan): quién revirtió lo registra la auditoría con el usuario que actúa.
        var revertedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.RevertInvoicing(revertedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.invoice_reverted",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
```

En `QepServiceCollectionExtensions.cs`, después del registro de `CancelOrderHandler` (tras la línea 371):

```csharp
        services.AddScoped<
            ICommandHandler<InvoiceOrderCommand, OrderDto>,
            InvoiceOrderHandler>();
        services.AddScoped<
            ICommandHandler<RevertOrderInvoicingCommand, OrderDto>,
            RevertOrderInvoicingHandler>();
```

- [ ] **Step 6: Exponer los endpoints**

En `OrderEndpoints.cs`, después del `MapPost("/{orderId:guid}/cancel", …)` (tras la línea 95):

```csharp

        // Facturar (spec 2026-10-05): sólo un cambio de estado, sin cuerpo. QEP no emite la
        // factura; deja constancia de quién la marcó y cuándo. Política propia y no OrderApprove
        // — ver InvoiceOrderHandler.
        collection.MapPost("/{orderId:guid}/invoice", InvoiceOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderInvoice)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Revertir la facturación (decisión 4): vuelve a Approved. Mismo permiso que facturar.
        collection.MapPost("/{orderId:guid}/uninvoice", RevertOrderInvoicingAsync)
            .RequireAuthorization(OrdersPermissions.OrderInvoice)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Después de `CancelOrderAsync` (tras la línea 412):

```csharp

    private static async Task<IResult> InvoiceOrderAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new InvoiceOrderCommand(tenantId, orderId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> RevertOrderInvoicingAsync(
        Guid tenantId,
        Guid orderId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new RevertOrderInvoicingCommand(tenantId, orderId),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }
```

- [ ] **Step 7: Correr las pruebas nuevas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --filter "FullyQualifiedName~OrderInvoicingHandlerTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderApiTests.Invoic"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --filter "FullyQualifiedName~OrdersReportSummaryApiTests.SummaryCountsAnInvoicedOrderAsASale"
```

Esperado: build `0 Advertencia(s)` y `0 Errores`; PASS de las cuatro unitarias, las cinco `OrderApiTests.Invoic*` y la del resumen. Si `InvoiceAndRevertWithOnlyTheApprovePermissionAreForbidden` da 500 y no 403, la política de Task 2 no está registrada.

- [ ] **Step 8: Correr las clases tocadas enteras y arquitectura**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderApiTests"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --filter "FullyQualifiedName~OrdersReportSummaryApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en los tres, 0 con error. `OrderApiTests` entero porque `OrderResponse` cambió de forma y todas sus pruebas lo deserializan.

- [ ] **Step 9: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
if ((git branch --show-current) -ne "feature/facturar-pedido") { throw "ABORT" }; git add src/Modules/Quotations/Modules.Quotations.Application/InvoiceOrder.cs src/Modules/Quotations/Modules.Quotations.Application/RevertOrderInvoicing.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderInvoicingHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs; git commit -m "feat(quotations): endpoints para facturar y revertir la facturación de un pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task 4: Documentar el contrato

**Files:**
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:27-28,42,75-76,127-136,143,191,197`

**Interfaces:**
- Consumes (Task 3): las rutas `/invoice` y `/uninvoice`, `OrderResponse` con `invoicedAt`/`invoicedBy`, los tres códigos nuevos.
- Produces: el contrato que lee el frontend, alineado con el código.

Sin prueba: es documentación. La verificación es que cada código y ruta nuevos aparezcan (Step 2).

- [ ] **Step 1: Editar el documento**

Líneas 27-28 (bloque de estados), `Order.status` pasa a:

```
Order.status:        Pending → Approved → Invoiced
                     Invoiced → Approved (revertir la facturación; no anula nada fuera de QEP)
                     Pending | Approved → Cancelled (con motivo; la cotización sigue Converted)
```

Línea 42, `—aprobar, anular, comprobantes, productos, editar y el cálculo previo— va por` pasa a:

```
—aprobar, anular, facturar, revertir la facturación, comprobantes, productos, editar y el cálculo
previo— va por
```

(La línea 43, `` `/orders/{orderId}/…`, porque quien llega desde el listado… ``, queda igual.)

Después de la fila `POST /orders/{orderId}/cancel` (línea 76):

```
| `POST` | `/orders/{orderId}/invoice` | — (sin body) | 200 `OrderResponse` en `Invoiced`, con `invoicedAt`/`invoicedBy`. Sólo desde `Approved`; no exige el pago completo. Exige `quotations.order.invoice` (admin y facturación: `quotations.order.approve` no alcanza). Es sólo el estado: QEP no emite la factura |
| `POST` | `/orders/{orderId}/uninvoice` | — (sin body) | 200 `OrderResponse` de vuelta en `Approved`, con `invoicedAt`/`invoicedBy` en `null`. Sólo desde `Invoiced`; mismo permiso que facturar. No anula nada fuera de QEP |
```

En el bloque `ts`, `type OrderResponse` (líneas 127-136) queda:

```ts
type OrderResponse = {
  id: string; orderNumber: string; quotationId: string;
  status: "Pending" | "Approved" | "Cancelled" | "Invoiced";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string;
  approvedAt: string | null; approvedBy: string | null;
  cancelledAt: string | null; cancelledBy: string | null; cancellationReason: string | null;
  invoicedAt: string | null; invoicedBy: string | null; // null si no está facturado
  ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  version: number; // el If-Match de PUT /orders/{orderId}
  paymentProofs: { id, fileId, amount, uploadedAt }[];
};
```

Línea 143:

```
`advisorId`/`createdBy`/`updatedBy`/`convertedBy`/`approvedBy`/`cancelledBy`/`invoicedBy` son ids de **membership** (Tenancy), no el
```

Línea 191, la fila `order.order.not_pending` empieza ahora `` | `order.order.not_pending` | 422 | El pedido ya está `Approved`, `Invoiced` o `Cancelled`: `` (el resto de la fila queda igual).

Después de la fila `order.order.cancellation_reason_too_long` (línea 197):

```
| `order.order.already_invoiced` | 422 | `POST /orders/{orderId}/cancel` sobre un pedido `Invoiced`, con o sin motivo: primero se revierte la facturación |
| `order.order.not_approved` | 422 | `POST /orders/{orderId}/invoice` sobre un pedido que no está `Approved` (`Pending`, `Cancelled` o ya `Invoiced`) |
| `order.order.not_invoiced` | 422 | `POST /orders/{orderId}/uninvoice` sobre un pedido que no está `Invoiced` |
```

- [ ] **Step 2: Verificar y commitear**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
foreach ($term in @("/orders/{orderId}/invoice", "/orders/{orderId}/uninvoice", "order.order.already_invoiced", "order.order.not_approved", "order.order.not_invoiced", "invoicedAt", "Invoiced → Approved")) { "{0}: {1}" -f $term, (Select-String -Path docs/integracion-cotizaciones-y-pedidos.md -SimpleMatch $term | Measure-Object).Count }
if ((git branch --show-current) -ne "feature/facturar-pedido") { throw "ABORT" }; git add docs/integracion-cotizaciones-y-pedidos.md; git commit -m "docs(quotations): contrato de facturar un pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: cada término con conteo mayor que 0; sin salida en las dos últimas.

---

### Task 5: Verificación completa del backend

**Files:** ninguno (sólo lectura y ejecución).

**Interfaces:**
- Consumes: Tasks 1 a 4 commiteadas.
- Produces: la evidencia literal para el handoff.

- [ ] **Step 1: Restore, build y modelo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
git status --short
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: `git status` vacío; build con `0 Advertencia(s)` y `0 Errores`; `No changes have been made to the model since the last migration.`

- [ ] **Step 2: La suite completa, una sola vez, en primer plano**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test Backend.slnx --no-build
```

Esperado: PASS de todos los proyectos, 0 con error. Tarda: las de integración levantan un contenedor por prueba. Si algo falla, compara **por nombre de prueba** contra la misma corrida en un worktree limpio de `origin/develop`: si también falla ahí, se anota en el handoff como previa; si no, se corrige antes de cerrar (con su RED y GREEN, y su commit).

- [ ] **Step 3: Formato, commits y residuos**

Corre **el chequeo de formato** con la lista de archivos de toda la rama:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\facturar-pedido
$base = git merge-base origin/develop HEAD
$files = git diff --name-only $base HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-facturar-pedido-format"
Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
dotnet format Backend.slnx --verify-no-changes --no-restore --include $files --report $report
$reportFile = Join-Path $report "format-report.json"
if (Test-Path $reportFile) {
    (Get-Content $reportFile -Raw | ConvertFrom-Json) | ForEach-Object {
        $document = $_
        $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
            ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
    }
}
git log --oneline "$base..HEAD"
git log --format=%B "$base..HEAD" | Select-String -SimpleMatch "Co-Authored-By"
git branch --show-current
```

Esperado: formato sin diagnósticos propios; ocho commits (los tres del spec, el del plan y los cuatro de la tabla «Entrega»); el `Select-String` sin salida; rama `feature/facturar-pedido`.

- [ ] **Step 4: Handoff**

Entrega al developer, en este orden: la salida literal de RED y GREEN de cada tarea; la salida de los Steps 1-3 de esta tarea; los hallazgos 3 (las etiquetas viajan en el primer commit, no al final), 4 (`revertedBy` no llega a la auditoría como membresía) y 11 (texto del permiso elegido por el plan) como puntos a confirmar; y el contrato de la sección «Contrato HTTP resultante», que es la entrada del frontend. Recuerda el orden de despliegue del spec: **primero el backend, después el frontend**. La rama **no** se mergea ni se publica desde este plan.

---

## Self-review

**Cobertura del spec (sección Backend):**

| Requisito | Tarea |
|---|---|
| `OrderStatus.Invoiced` con su línea en el comentario | 1 |
| `InvoicedAt`/`InvoicedBy`; `Invoice` con `not_approved`, `Version++`, sin tocar la aprobación | 1 |
| `RevertInvoicing` con `not_invoiced`, limpia las marcas, `revertedBy` no se guarda | 1 (hallazgo 4) |
| `Cancel`: `already_invoiced` antes de `already_cancelled` y del motivo | 1 (y HTTP en 3) |
| Guards `Pending` sin tocar, y cubiertos para un facturado | 1 (`AnInvoicedOrderRejectsEveryPendingOnlyChange`), 3 (aprobar y agregar productos por HTTP) |
| `InvoiceOrder.cs` y `RevertOrderInvoicing.cs` calcados de `ApproveOrder.cs`, en el orden del spec, sin validador ni outbox | 3 |
| Auditoría `quotation.order.invoiced` / `quotation.order.invoice_reverted` | 3 (unitarias y API) |
| Registro a mano de los dos handlers | 3 |
| `POST /invoice` y `/uninvoice` con `RequireAuthorization(OrderInvoice)`; 200/403/404/422 | 3 |
| `invoicedAt`/`invoicedBy` en `OrderDto`, `OrderMapping`, `OrderResponse` y `ToResponse` | 3 |
| Permiso: constante, `PermissionDefinition` medium, política, admin y billing, `AuthorizationCatalogApiTests` | 2 |
| Sin migración de datos de roles | Hallazgo 2 |
| Columnas `invoiced_at`/`invoiced_by` y migración `AddOrderInvoicing` | 1 |
| `QuotationUserReferenceProbe` suma `invoiced_by`, con su prueba de huérfanos | 1 |
| `ExportStatusLabels` `Invoiced → "Facturado"` y su prueba | 1 (hallazgo 3) |
| `OrdersReportSource` no cambia; el facturado cuenta como venta | 3 (prueba de guarda) |
| Pruebas `OrderTests`: aprobado, pendiente/anulado, dos veces, revertir, revertir no facturado, anular facturado, cuatro rechazos | 1 |
| Pruebas `OrderApiTests`: 200 con campos, revertir y después anular, 403 con `approve`, 404, 422 por código, auditoría | 3 |
| `QuotationsDbContextMappingTests`, `ExportStatusLabelsTests`, `AuthorizationCatalogApiTests`, prueba de huérfanos | 1, 2 |
| Contrato en `docs/integracion-cotizaciones-y-pedidos.md` | 4 |

**Placeholders:** `<ts>` en el nombre de la migración es el timestamp que genera `dotnet ef`, no un hueco a completar a mano. No hay TBD ni pasos sin código.

**Consistencia de nombres:** `Order.Invoice(MemberId, DateTimeOffset)`, `Order.RevertInvoicing(MemberId, DateTimeOffset)`, `InvoicedAt`/`InvoicedBy`, `OrdersPermissions.OrderInvoice`, `InvoiceOrderCommand(TenantId, OrderId)`/`InvoiceOrderHandler`, `RevertOrderInvoicingCommand(TenantId, OrderId)`/`RevertOrderInvoicingHandler`, `quotation.order.invoiced`/`quotation.order.invoice_reverted` y los códigos `order.order.not_approved`/`not_invoiced`/`already_invoiced` se usan con el mismo nombre y tipo en las Tasks 1 a 4. En `OrderDto` y `OrderResponse` los dos campos van en la misma posición (después de `CancellationReason`), y `OrderMapping.ToDto` y `OrderEndpoints.ToResponse` los pasan en ese orden.

**Review Focus:** los cinco casos tienen su prueba en la tarea dueña: 1 → Task 3 `OrderInvoicingHandlerTests`; 2 → Task 1 `CancelAnInvoicedOrderIsRejectedBeforeLookingAtTheReason` y Task 3 `InvoiceAndRevertRejectTheWrongStateWithTheirDomainCodes`; 3 → Task 1 `ARevertedOrderCanBeInvoicedAgainOrCancelled` y Task 3 `InvoicingRevertedLeavesTheOrderApprovedAndCancellable`; 4 → Task 1 `InvoiceDoesNotRequireTheOrderToBePaid` y Task 3 `InvoiceAnApprovedOrderReturnsItInvoicedWithWhoAndWhen`; 5 → Task 3 `SummaryCountsAnInvoicedOrderAsASale`.
