# Anular un pedido (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un pedido `Pending` o `Approved` se pueda anular con un motivo obligatorio —queda `Cancelled` con quién, cuándo y por qué—, con un permiso propio `quotations.order.cancel` sólo para admin, y que el resumen del reporte de pedidos deje de contar los anulados.

**Architecture:** El agregado `Order` gana `Cancel` y tres propiedades nullable, mapeadas a tres columnas nuevas de `quotations.orders` con una migración generada por `dotnet ef`. Un caso de uso `CancelOrder`, calcado de `ApproveOrder`, cuelga de `POST /quotations/{quotationId}/order/cancel` con su política. El permiso se declara en el composition root y sólo lo recibe el rol `admin`, que vive en código: no hay migración de datos. `OrdersReportSource.SummarizeAsync` filtra `Cancelled` antes del join; el listado y la exportación no cambian de consulta.

**Tech Stack:** .NET 10 (SDK de `global.json`, 10.0.400), EF Core 10.0.11 + Npgsql 10.0.3, xUnit v3, Testcontainers (`postgres:18-alpine`; Docker corriendo para las pruebas de integración).

**Spec:** `qep-frontend/docs/superpowers/specs/2026-09-16-anular-pedido-design.md` (en el worktree `qep-frontend-worktrees/anular-pedido`). Este plan implementa **sólo** su sección «Backend (`qep-backend`)».

## Global Constraints

**Del spec** (valores copiados tal cual):

- Decisión 1: «Se anula desde `Pending` **y** `Approved`.»
- Decisión 2: «Anular **no borra**: el pedido pasa a `Cancelled` y guarda `CancelledAt`, `CancelledBy` y `CancellationReason`. `ApprovedAt`/`ApprovedBy` se conservan.»
- Decisión 3: «La cotización de origen **sigue `Converted`**.» `Quotation` no cambia.
- Decisión 4: «El motivo es **texto libre obligatorio**, máximo 500 (mismo límite que `Order.NotesMaxLength`).»
- Decisión 5: «Permiso nuevo **`quotations.order.cancel`**, asignado por defecto **sólo al rol admin**.»
- Decisión 6: «Reportes: el **resumen excluye** los anulados de conteos y totales; **listado y exportación los muestran** con estado "Anulado".»
- Códigos: `order.order.already_cancelled`, `order.order.cancellation_reason_required`, `order.order.cancellation_reason_too_long`.
- Ruta: `POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/cancel`, body `{ "reason": string }`, «Responde `200` con el pedido».
- Auditoría `quotation.order.cancelled`. «Sin evento de outbox (aprobar tampoco lo tiene).»
- Columnas: `cancelled_at`, `cancelled_by` (conversión nullable de `MemberId`), `cancellation_reason` (`varchar(500)`). Migración `yyyyMMddHHmmss_AddOrderCancellation`. «`status` ya es `varchar(20)`: `Cancelled` entra sin migración.»
- Los guards `order.order.not_pending` existentes «ya bloquean editar un anulado; no se tocan».
- Fuera de alcance: «el resumen de cotizaciones sigue contando la cotización como `Converted`».

**Del proceso:**

- Todo se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido`, rama **`feature/cancel-order`** (sin upstream; salió de `origin/develop` en `1556c16`). Cada bloque de comandos empieza con `Set-Location` a ese worktree. **Nunca** se commitea sobre `main` ni en el checkout principal `...\qep\qep-backend`.
- TDD estricto: RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff (como mínimo el resumen Superado/Con error/Omitido y el mensaje de cada falla).
- Todo comando va en **PowerShell**: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`. **Nunca** se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las pruebas corren en primer plano, **un proyecto por comando** y con `--filter "FullyQualifiedName~<Clase>"` mientras se itera. Las de integración levantan un contenedor por prueba y tardan: nunca la suite entera en un solo comando.
- Migración con el factory de diseño, nunca a mano ni con `--startup-project`: `dotnet ef migrations add AddOrderCancellation --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. `QuotationsDbContextModelSnapshot.cs` sólo cambia regenerado por ese comando. Las migraciones históricas no se tocan.
- Las pruebas de xUnit v3 pasan `TestContext.Current.CancellationToken` a toda llamada que acepte un `CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- Commits: Conventional Commits en español. **Sin atribución de IA ni trailer `Co-Authored-By`.** Cada commit en un solo comando con el guard de rama:
  `if ((git branch --show-current) -ne "feature/cancel-order") { throw "ABORT: rama equivocada" }; git add <rutas explícitas>; git commit -m "<mensaje>"`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, `git commit --amend` antes de seguir. Nunca `git add -A` ni `git add .`.
- Idioma: prosa, comentarios y `<summary>` en español neutro, tuteando; identificadores, códigos de error y mensajes de excepción en inglés, como el código de alrededor. Los comentarios nuevos explican el porqué y citan la decisión del spec.
- **Chequeo de formato** sobre los `.cs` que toca cada tarea (los de `Migrations/` no). `ENDOFLINE` y `CHARSET` son ruido previo del repo y se filtran:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-anular-pedido-format"
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

---

## Hallazgos contra el código (2026-09-16)

Verificados en `feature/cancel-order` = `origin/develop` = `1556c16`.

1. **El rol `admin` no está persistido: no hay migración de permisos.** Los roles de fábrica se registran como `RoleDefinition` singleton en `QepServiceCollectionExtensions.cs:535-578` y `TenantRoleCatalog.ResolveAsync` (`TenantRoleCatalog.cs:73-85`) los arma desde `IRoleCatalog` con `IsSystem: true, Version: 0`. `"authorization".roles` sólo guarda roles custom: `Role.NormalizeKey` rechaza `admin`/`advisor`/`billing` con `authorization.role.key_reserved` (`Role.cs:144-149`, `SystemRoleKeys.cs:16-18`), `AuthorizationDbContext` no siembra datos (ningún `HasData`/`InsertData`), y la migración precedente lo dice: «Los roles de fábrica viven en código (QepServiceCollectionExtensions) y no pasan por acá» (`20260914194321_RenamePermissionsToOrders.cs:12`). Conclusión: agregar el permiso a la `RoleDefinition` de admin lo concede a todo admin en el deploy. Un rol custom no lo recibe, que es lo que pide la decisión 5. `AuthorizationPermissionsMigrationTests` no se toca.
2. **La auditoría no puede llevar el motivo.** `IQuotationAuditPublisher.Publish` (`IQuotationAuditPublisher.cs:7-9`) recibe `tenantId, actorId, action, resourceId, outcome, occurredAt`; el payload fija `changedFields` en `[]` (`QuotationAuditPublisher.cs:17-18`); `AuditProjectionWorker` lee sólo propiedades fijas (`AuditProjectionWorker.cs:100-116`) y `AuditEntry` no tiene columna para texto libre (sólo `ChangedFieldsJson`, `AuditEntry.cs:58`). Un campo extra en el payload se quedaría en el outbox y nunca llegaría a `audit.entries`. **Decisión de este plan:** la auditoría registra `quotation.order.cancelled` con el id del pedido, igual que `approved`, y el motivo queda en `orders.cancellation_reason`, que es permanente y viaja en la respuesta. Llevarlo a `audit.entries` exige cambiar el contrato del módulo Audit: queda fuera de este plan y se reporta como desvío del spec.
3. **El motivo se valida en el dominio, no con FluentValidation.** `ApproveOrderCommand` no tiene validador, y el spec fija códigos de dominio que el frontend mapea por `code`. Un `AbstractValidator` respondería `validation.failed` con el mapa `errors` (`ApiExceptionHandler.cs:58-65,143-144`) y los dos códigos de motivo serían inalcanzables por HTTP. Con `QuotationsDomainException` la respuesta es 422 `Business rule failed` con `code` = el código de dominio y **sin** `errors` (`ApiExceptionHandler.cs:145-146`). Ningún código necesita registro: el mapeo es por tipo de excepción. `CancelOrderCommand` **no** lleva validador.
4. **Enum, propiedades y mapeo tienen que viajar en el mismo commit.** Agregar `OrderStatus.Cancelled` rompe `ExportStatusLabelsTests.EveryOrderStatusHasTheLabelOfTheOrdersTable`, que recorre `Enum.GetValues<OrderStatus>()` (`ExportStatusLabelsTests.cs:50-55`). Agregar `Order.CancelledBy` (`MemberId?`, un `readonly record struct`, `MemberId.cs:10`) sin conversión rompe la construcción del modelo de EF, y `CancelledAt`/`CancellationReason` por convención dejan `QuotationsDbContextMappingTests.TheModelHasNoChangesPendingAMigration` en rojo. Por eso Task 1 junta dominio, etiqueta de exportación, mapeo y migración.
5. **La política va antes que el endpoint.** `RequireAuthorization(<permiso>)` sin `AddPolicy` responde 500, no 403 (comentario en `QepServiceCollectionExtensions.cs:1023-1024`). Task 2 registra la política; Task 3 la usa.
6. **Precedencia de errores en `POST /cancel`** (la misma que aprobar): permiso (403) → pedido de la cotización (404 `order.order.not_found`, `OrderNotFound.cs:9-10`) → membresía activa (403 `authorization.denied`, `QuotationAdvisorResolver.cs:23-29`) → dominio (422). Dentro del dominio, `already_cancelled` va antes que las reglas del motivo: anular dos veces con motivo en blanco responde `already_cancelled`.
7. **`cancelledBy` es un id de membresía**, igual que `approvedBy` y `convertedBy`: `ApproveOrder.cs:41-42` lo resuelve con `QuotationAdvisorResolver`, y `docs/integracion-cotizaciones-y-pedidos.md:106-107` lo documenta.
8. **Lo que ya funciona sin tocar nada.** Aprobar, sumar comprobantes, quitarlos o recalcular sobre un anulado responden `order.order.not_pending` (`Order.cs:115,160,217,248`). El filtro `status=Cancelled` del listado y de la exportación entra por `Enum.TryParse` (`OrderListing.cs:14-21`). El Excel de pedidos toma la etiqueta de `ExportStatusLabels.For(OrderStatus)` (`OrdersExportProcessor.cs:147`). El resumen del período anterior pasa por el mismo `SummarizeAsync` (`GetOrdersReportSummary.cs:33,65`), así que un solo filtro cubre los dos.
9. **Deuda que no se arregla acá.** `QuotationUserReferenceProbe` pide sumar «una columna nueva con `MemberId`» (`QuotationUserReferenceProbe.cs:16-20`), pero hoy ya omite `orders.approved_by`. `cancelled_by` queda en la misma situación: agregarlo es un cambio de retención de usuarios que el spec no pide. Se anota en el handoff como pendiente.

## Contrato HTTP resultante (para el plan del frontend)

`POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/cancel`, con `X-Qep-Client: web` como todo POST.

```ts
type CancelOrderRequest = { reason: string } // se recorta; 1 a 500 caracteres después del recorte

type OrderResponse = {
  id: string; orderNumber: string; quotationId: string;
  status: "Pending" | "Approved" | "Cancelled";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string;
  approvedAt: string | null; approvedBy: string | null;
  cancelledAt: string | null;        // ISO 8601 con offset, null si no se anuló
  cancelledBy: string | null;        // uuid de membership (no de usuario), null si no se anuló
  cancellationReason: string | null; // recortado
  ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  paymentProofs: { id: string; fileId: string; amount: number; uploadedAt: string }[];
}
```

| HTTP | `code` | Cuándo |
|---|---|---|
| 200 | — | Body `OrderResponse` con `status: "Cancelled"` |
| 403 | — (política) o `authorization.denied` | Sin `quotations.order.cancel` (tener `quotations.order.manage` no alcanza), otro tenant o sin membresía activa |
| 404 | `order.order.not_found` | La cotización no tiene pedido |
| 422 | `order.order.already_cancelled` | El pedido ya está `Cancelled` |
| 422 | `order.order.cancellation_reason_required` | `reason` ausente, `null`, vacío o sólo espacios |
| 422 | `order.order.cancellation_reason_too_long` | `reason` recortado de más de 500 caracteres |

Los tres 422 son **códigos de dominio** (ProblemDetails con `code`, **sin** `errors`), no `validation.failed`.

---

## File Structure

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderCancellation.cs` (+ `.Designer.cs`) | 1 | Tres columnas nullable en `quotations.orders` |
| `src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs` | 3 | `CancelOrderCommand` y `CancelOrderHandler` |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs` | 1 | `Cancelled` |
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` | 1 | `CancellationReasonMaxLength`, tres propiedades, `Cancel`, `NormalizeCancellationReason` |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs` | 1 | `Cancelled => "Anulado"` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | 1 | Mapeo de las tres columnas |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | 1 | Regenerado por `dotnet ef` |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs` | 2 | `OrderCancel` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | 2, 3 | Permiso, política, rol admin (2); registro del handler (3) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` | 3 | `OrderDto`, `OrderResponse`, `CancelOrderRequest` |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs` | 3 | `ToDto` con los tres campos |
| `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` | 3 | `POST /cancel` y `ToResponse` |
| `docs/integracion-cotizaciones-y-pedidos.md` | 3 | Estado, endpoint, DTO y códigos |
| `src/Bootstrapper/OrdersReportSource.cs` | 4 | El resumen filtra `Cancelled` |

**Pruebas**

| Archivo | Tarea |
|---|---|
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` | 1 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs` | 1 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` | 1 |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` | 2 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs` | 3 |
| `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs` | 4 |
| `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs` | 4 |

**No se tocan, a propósito:** `Quotation.cs` (decisión 3), los guards `not_pending` (spec), `IQuotationAuditPublisher` y el módulo Audit (hallazgo 2), `QuotationUserReferenceProbe` (hallazgo 9), las migraciones de Authorization (hallazgo 1), `OrderListing`/`OrdersExportProcessor` (hallazgo 8).

## Entrega

| Commit | Tarea |
|---|---|
| `docs(orders): plan de backend para anular un pedido` | (ya hecho) |
| `feat(orders): estado Cancelled y columnas de anulación del pedido` | 1 |
| `feat(authorization): permiso quotations.order.cancel sólo para admin` | 2 |
| `feat(orders): endpoint para anular un pedido` | 3 |
| `feat(reporting): el resumen de pedidos no cuenta los anulados` | 4 |

---

### Task 0: Rama, herramientas y línea base

**Files:** ninguno.

**Interfaces:**
- Consumes: nada.
- Produces: la rama comprobada y la evidencia de que el build y el modelo de EF arrancan limpios.

- [ ] **Step 1: Comprobar rama, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
git branch --show-current
git status --short
git fetch origin
git rev-list --left-right --count origin/develop...HEAD
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: rama `feature/cancel-order`; `git status` vacío; el `rev-list` da `0	1` (el commit de este plan) o `N	1` si `develop` avanzó; `Get-Process` sin salida; `docker info` da una versión; `dotnet ef` da `10.0.x`. Si la rama no es `feature/cancel-order`, **para y pregunta**. Si el primer número es mayor que 0, `develop` avanzó: re-verifica las líneas de los hallazgos antes de seguir, y si alguna cambió, **para y pregunta**.

- [ ] **Step 2: Restore, build y modelo sin cambios pendientes**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; `No changes have been made to the model since the last migration.` Si el modelo ya tiene cambios pendientes, **para y pregunta**: la migración de Task 1 arrastraría deriva ajena.

---

### Task 1: Dominio, etiqueta de exportación y persistencia de la anulación

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs:1-15`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:18-19,83-84,127-128,316`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs:28-33`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:364-369`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderCancellation.cs` (+ `.Designer.cs`, generados)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` (regenerado)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `ExportStatusLabelsTests.cs:27-36`, `QuotationsDbContextMappingTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `OrderStatus.Cancelled`
  - `public const int Order.CancellationReasonMaxLength = 500;`
  - `public DateTimeOffset? Order.CancelledAt { get; }`, `public MemberId? Order.CancelledBy { get; }`, `public string? Order.CancellationReason { get; }`
  - `public void Order.Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)` — lanza `QuotationsDomainException` con `order.order.already_cancelled`, `order.order.cancellation_reason_required` u `order.order.cancellation_reason_too_long`
  - `ExportStatusLabels.For(OrderStatus.Cancelled) == "Anulado"`
  - Columnas `quotations.orders.cancelled_at` (`timestamp with time zone`), `cancelled_by` (`uuid`), `cancellation_reason` (`character varying(500)`), las tres nullable.

- [ ] **Step 1: Escribir las pruebas de dominio que fallan**

Agrega al final de `OrderTests.cs`, antes de la llave que cierra la clase:

```csharp
    // Spec 2026-09-16 (anular un pedido), decisiones 1 y 2: se anula desde Pending, y queda
    // quién, cuándo y por qué.
    [Fact]
    public void CancelFromPendingRecordsWhoWhenAndWhy()
    {
        var order = NewOrder();
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancelledBy);
        Assert.Null(order.CancellationReason);
        var cancelledBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Cancel(cancelledBy, "El cliente desistió", later);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(later, order.CancelledAt);
        Assert.Equal(cancelledBy, order.CancelledBy);
        Assert.Equal("El cliente desistió", order.CancellationReason);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Decisiones 1 y 2: un pedido aprobado por error también se anula, y anularlo no reescribe
    // quién lo revisó ni cuándo.
    [Fact]
    public void CancelFromApprovedKeepsTheApproval()
    {
        var order = NewOrder();
        var approvedBy = new MemberId(Guid.CreateVersion7());
        order.Approve(approvedBy, Now);

        order.Cancel(ConvertedBy, "Aprobado por error", Now.AddDays(1));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(approvedBy, order.ApprovedBy);
        Assert.Equal(Now, order.ApprovedAt);
        Assert.Equal(3, order.Version);
    }

    // Anular dos veces reescribiría quién, cuándo y por qué se anuló.
    [Fact]
    public void CancelTwiceIsRejectedAndKeepsTheFirstCancellation()
    {
        var order = NewOrder();
        order.Cancel(ConvertedBy, "Primera vez", Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(new MemberId(Guid.CreateVersion7()), "Segunda vez", Now.AddDays(1)));

        Assert.Equal("order.order.already_cancelled", error.Code);
        Assert.Equal("Primera vez", order.CancellationReason);
        Assert.Equal(Now, order.CancelledAt);
        Assert.Equal(ConvertedBy, order.CancelledBy);
    }

    // Decisión 4: el motivo es obligatorio. Ya anulado gana already_cancelled (hallazgo 6).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CancelRequiresAReason(string? reason)
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now));

        Assert.Equal("order.order.cancellation_reason_required", error.Code);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(order.CancelledAt);
    }

    [Fact]
    public void CancelRejectsAReasonLongerThanTheLimit()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, new string('a', Order.CancellationReasonMaxLength + 1), Now));

        Assert.Equal("order.order.cancellation_reason_too_long", error.Code);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    // El límite se mide sobre el motivo recortado, mismo criterio que las notas.
    [Fact]
    public void CancelTrimsTheReasonBeforeMeasuringIt()
    {
        var order = NewOrder();
        var reason = new string('a', Order.CancellationReasonMaxLength);

        order.Cancel(ConvertedBy, $"  {reason}  ", Now);

        Assert.Equal(reason, order.CancellationReason);
    }

    // Los guards existentes ya cubren un anulado: no es Pending, así que nada de lo que sólo se
    // permite en Pending pasa (spec, «Dominio»).
    [Fact]
    public void ACancelledOrderRejectsEveryPendingOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Cancel(ConvertedBy, "El cliente desistió", Now);
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

        Assert.Equal("order.order.not_pending", addProofs.Code);
        Assert.Equal("order.order.not_pending", recalculate.Code);
        Assert.Equal("order.order.not_pending", removeProof.Code);
        Assert.Equal("order.order.not_pending", approve.Code);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
```

En `ExportStatusLabelsTests.cs`, reemplaza el diccionario de `EveryOrderStatusHasTheLabelOfTheOrdersTable` (líneas 31-35) por:

```csharp
            new Dictionary<OrderStatus, string>
            {
                [OrderStatus.Pending] = "Pendiente",
                [OrderStatus.Approved] = "Aprobado",
                // Decisión 6 del spec 2026-09-16: la exportación muestra los anulados.
                [OrderStatus.Cancelled] = "Anulado",
            },
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExportStatusLabelsTests"
```

Esperado: FAIL de compilación, `CS0117: 'OrderStatus' no contiene una definición para 'Cancelled'` y `CS1061: 'Order' no contiene una definición para 'Cancel'` (o sus equivalentes en inglés).

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
/// </summary>
public enum OrderStatus
{
    Pending,
    Approved,
    Cancelled
}
```

En `Order.cs`, después de `public const int NotesMaxLength = 500;` (línea 19):

```csharp
    /// <summary>Mismo límite que <see cref="NotesMaxLength"/> (spec 2026-09-16, decisión 4).</summary>
    public const int CancellationReasonMaxLength = 500;
```

Después de `public MemberId? ApprovedBy { get; private set; }` (línea 83):

```csharp
    /// <summary>Cuándo se anuló, quién y por qué (spec 2026-09-16). Null mientras el pedido no se
    /// anula. Van aparte de <see cref="ApprovedAt"/>/<see cref="ApprovedBy"/>, que se conservan:
    /// un pedido aprobado por error y después anulado tiene que seguir diciendo quién lo
    /// aprobó.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public MemberId? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }
```

Después del método `Approve` (tras la línea 127):

```csharp

    /// <summary>
    /// Anula el pedido (spec 2026-09-16). Desde <see cref="OrderStatus.Pending"/> y también desde
    /// <see cref="OrderStatus.Approved"/> (decisión 1): sólo desde Pending dejaría sin salida un
    /// pedido aprobado por error. No borra nada (decisión 2) y no toca la cotización, que sigue
    /// <c>Converted</c> (decisión 3).
    ///
    /// Anular dos veces se rechaza antes de mirar el motivo: reescribiría quién, cuándo y por qué
    /// se anuló. Todo se valida antes de cambiar un solo campo.
    /// </summary>
    public void Cancel(MemberId cancelledBy, string? reason, DateTimeOffset occurredAt)
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new QuotationsDomainException(
                "order.order.already_cancelled",
                "The order is already cancelled.");
        }

        var normalizedReason = NormalizeCancellationReason(reason);

        Status = OrderStatus.Cancelled;
        CancelledBy = cancelledBy;
        CancelledAt = occurredAt;
        CancellationReason = normalizedReason;
        UpdatedAt = occurredAt;
        Version++;
    }
```

Después de `NormalizeNotes` (antes de la llave que cierra la clase, línea 317):

```csharp

    // Decisión 4: obligatorio, a diferencia de las notas. El límite se mide recortado.
    private static string NormalizeCancellationReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new QuotationsDomainException(
                "order.order.cancellation_reason_required",
                "A reason is required to cancel an order.");
        }

        var trimmed = reason.Trim();
        return trimmed.Length > CancellationReasonMaxLength
            ? throw new QuotationsDomainException(
                "order.order.cancellation_reason_too_long",
                $"The cancellation reason cannot exceed {CancellationReasonMaxLength} characters.")
            : trimmed;
    }
```

En `ExportStatusLabels.cs`, el switch de `OrderStatus` (líneas 28-33) queda:

```csharp
    public static string For(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "Pendiente",
        OrderStatus.Approved => "Aprobado",
        OrderStatus.Cancelled => "Anulado",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The order status has no export label."),
    };
```

- [ ] **Step 4: Correr las pruebas de dominio y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~ExportStatusLabelsTests"
```

Esperado: PASS, 0 con error. (No se commitea todavía: el modelo de EF está roto hasta el Step 8 — hallazgo 4.)

- [ ] **Step 5: Escribir la prueba de mapeo que falla**

Agrega en `QuotationsDbContextMappingTests.cs`, antes de `TheModelHasNoChangesPendingAMigration`:

```csharp
    /// <summary>
    /// Spec 2026-09-16: las tres columnas de la anulación, nullable (un pedido vivo no las tiene)
    /// y con el nombre en snake_case que fija el mapeo a mano. `cancelled_by` necesita la
    /// conversión de <see cref="MemberId"/>: sin ella EF no puede mapear el struct.
    /// </summary>
    [Fact]
    public void OrderCancellationMapsToNullableSnakeCaseColumns()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var order = model.FindEntityType(typeof(Order))!;

        var cancelledAt = order.FindProperty(nameof(Order.CancelledAt))!;
        var cancelledBy = order.FindProperty(nameof(Order.CancelledBy))!;
        var reason = order.FindProperty(nameof(Order.CancellationReason))!;

        Assert.Equal("cancelled_at", cancelledAt.GetColumnName());
        Assert.Equal("cancelled_by", cancelledBy.GetColumnName());
        Assert.Equal("cancellation_reason", reason.GetColumnName());
        Assert.True(cancelledAt.IsNullable);
        Assert.True(cancelledBy.IsNullable);
        Assert.True(reason.IsNullable);
        Assert.Equal(Order.CancellationReasonMaxLength, reason.GetMaxLength());
    }
```

- [ ] **Step 6: Correr las pruebas de mapeo y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: FAIL en todas las pruebas de la clase con `InvalidOperationException` al construir el modelo, del estilo `The property 'Order.CancelledBy' could not be mapped because it is of type 'Nullable<MemberId>'`.

- [ ] **Step 7: Mapear las columnas**

En `QuotationsDbContext.cs`, justo después del bloque de `ApprovedBy` (tras la línea 369):

```csharp
        // Spec 2026-09-16: quién, cuándo y por qué se anuló. Nullables, porque un pedido vivo no
        // tiene nada que guardar acá; misma conversión nullable que approved_by.
        order.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        order.Property(value => value.CancelledBy)
            .HasColumnName("cancelled_by")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new MemberId(value.Value) : null);
        order.Property(value => value.CancellationReason)
            .HasColumnName("cancellation_reason")
            .HasMaxLength(Order.CancellationReasonMaxLength);
```

- [ ] **Step 8: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddOrderCancellation --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
```

Esperado: `Done.`; `git status` muestra `?? ..._AddOrderCancellation.cs`, `?? ..._AddOrderCancellation.Designer.cs` y ` M QuotationsDbContextModelSnapshot.cs`, nada más. Abre `<ts>_AddOrderCancellation.cs` y comprueba que `Up` tiene **sólo** estas tres operaciones (el orden puede variar) y `Down` sus tres `DropColumn`:

```csharp
            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                schema: "quotations",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);
```

Si aparece cualquier otra operación (un `AlterColumn`, un índice, otra tabla), hay deriva previa: **para y pregunta**. Si está bien, reemplaza la línea `/// <inheritdoc />` que precede a `public partial class AddOrderCancellation : Migration` por este comentario (mismo criterio que `20260906191344_AddSaleApproval.cs:8-17`):

```csharp
    /// <summary>
    /// Quién anuló el pedido, cuándo y por qué (spec 2026-09-16).
    ///
    /// Nullables y sin backfill: ningún pedido existente está anulado. `status` ya es
    /// varchar(20), así que `Cancelled` entra sin tocar esa columna.
    /// </summary>
```

- [ ] **Step 9: Correr las pruebas unitarias de Quotations y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: PASS del proyecto completo (incluidas `OrderCancellationMapsToNullableSnakeCaseColumns` y `TheModelHasNoChangesPendingAMigration`); `No changes have been made to the model since the last migration.`

- [ ] **Step 10: Probar la migración contra PostgreSQL**

El host de pruebas migra todo a la última al arrancar, así que una prueba de integración de pedidos ejerce la migración nueva y el `INSERT` a mano de `InsertLegacyOrderAsync`, que no nombra las columnas nuevas:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderApiTests.ConvertCreatesTheOrderAndLeavesTheQuotationConverted|FullyQualifiedName~OrderApiTests.ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted|FullyQualifiedName~OrdersMigrationTests"
```

Esperado: PASS, 0 con error.

- [ ] **Step 11: Chequeo de formato y commit**

Corre **el chequeo de formato** (Global Constraints). Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
$migrations = git ls-files --others --exclude-standard -- "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddOrderCancellation*.cs"
if ((git branch --show-current) -ne "feature/cancel-order") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Domain/OrderStatus.cs src/Modules/Quotations/Modules.Quotations.Domain/Order.cs src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs $migrations tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs; git commit -m "feat(orders): estado Cancelled y columnas de anulación del pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: el `Select-String` sin salida y `git status` vacío.

---

### Task 2: Permiso `quotations.order.cancel` sólo para admin

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs:5-6`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:566` (rol admin), `:787-792` (definición), `:1029-1030` (política)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs:44-84`

**Interfaces:**
- Consumes: nada.
- Produces: `public const string OrdersPermissions.OrderCancel = "quotations.order.cancel";` con su `PermissionDefinition`, su política de autorización del mismo nombre, y concedido al rol `admin` y a ningún otro.

Sin migración de datos: hallazgo 1.

- [ ] **Step 1: Escribir la prueba del catálogo que falla**

En `AuthorizationCatalogApiTests.cs`, reemplaza el `<summary>` y el cuerpo de `TheCatalogNamesTheOrderPermissions` desde `CatalogPermissionPayload[] expected =` hasta `Assert.Equal(["quotations.order.read"], OrderPermissionsOf(catalog, "billing"));` por:

```csharp
        CatalogPermissionPayload[] expected =
        [
            new("quotations.order.cancel", "Anular pedidos",
                "Permite anular un pedido pendiente o aprobado, con un motivo obligatorio.", "Quotations", "high"),
            new("quotations.order.manage", "Gestionar pedidos",
                "Permite convertir una cotización enviada en pedido, con sus comprobantes de pago.", "Quotations", "medium"),
            new("quotations.order.read", "Leer pedidos",
                "Permite consultar el pedido convertido de una cotización.", "Quotations", "low"),
            new("reporting.orders.read", "Reporte de pedidos",
                "Permite consultar y exportar el reporte de pedidos convertidos del tenant.", "Reporting", "low"),
        ];
        Assert.Equal(
            expected,
            catalog.Permissions
                .Where(permission => permission.Permission.Contains("order", StringComparison.Ordinal))
                .OrderBy(permission => permission.Permission, StringComparer.Ordinal));
        Assert.DoesNotContain(
            catalog.Permissions, permission => permission.Permission.Contains("sale", StringComparison.Ordinal));
        // Spec 2026-09-16, decisión 5: anular es sólo de admin. Asesor y facturación quedan igual.
        Assert.Equal(
            ["quotations.order.cancel", "quotations.order.manage", "quotations.order.read", "reporting.orders.read"],
            OrderPermissionsOf(catalog, "admin"));
        Assert.Equal(
            ["quotations.order.manage", "quotations.order.read", "reporting.orders.read"],
            OrderPermissionsOf(catalog, "advisor"));
        Assert.Equal(["quotations.order.read"], OrderPermissionsOf(catalog, "billing"));
```

Y en el `<summary>` de la prueba agrega al final, antes de `</summary>`: `Anular (spec 2026-09-16) es permiso propio y sólo de admin.`

- [ ] **Step 2: Correr la prueba y ver que falla**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests.TheCatalogNamesTheOrderPermissions"
```

Esperado: FAIL en el primer `Assert.Equal` (`Assert.Equal() Failure: Collections differ`), porque el catálogo no trae `quotations.order.cancel`.

- [ ] **Step 3: Declarar el permiso, su política y dárselo a admin**

`OrdersPermissions.cs` completo:

```csharp
namespace Modules.Quotations.Application;

public static class OrdersPermissions
{
    public const string OrderRead = "quotations.order.read";
    public const string OrderManage = "quotations.order.manage";

    /// <summary>Anular un pedido (spec 2026-09-16, decisión 5). Aparte de <see cref="OrderManage"/>:
    /// quien edita pedidos no tiene por qué poder deshacer uno aprobado.</summary>
    public const string OrderCancel = "quotations.order.cancel";
}
```

En `QepServiceCollectionExtensions.cs`, en la `RoleDefinition` de `admin`, después de `OrdersPermissions.OrderManage,` (línea 566):

```csharp
                // Sólo admin (spec 2026-09-16, decisión 5): anular deshace también un pedido ya
                // aprobado. El rol vive en código, así que no hay migración de datos.
                OrdersPermissions.OrderCancel,
```

Después de la `PermissionDefinition` de `OrdersPermissions.OrderManage` (tras la línea 792):

```csharp
        // High y sólo en admin, mismo criterio que TaxRateManage: revierte un pedido que otra
        // persona ya aprobó.
        services.AddSingleton(new PermissionDefinition(
            OrdersPermissions.OrderCancel,
            "Anular pedidos",
            "Permite anular un pedido pendiente o aprobado, con un motivo obligatorio.",
            "Quotations",
            "high"));
```

Después de la política de `OrdersPermissions.OrderManage` (tras la línea 1030), dentro de la misma cadena:

```csharp
            .AddPolicy(
                OrdersPermissions.OrderCancel,
                policy => AddPermissionRequirement(policy, OrdersPermissions.OrderCancel))
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthorizationCatalogApiTests|FullyQualifiedName~RoleApiTests"
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS en los tres, 0 con error. `RoleApiTests` va para confirmar que un rol custom todavía se crea y valida contra el catálogo con un permiso más.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
if ((git branch --show-current) -ne "feature/cancel-order") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/OrdersPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs; git commit -m "feat(authorization): permiso quotations.order.cancel sólo para admin"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task 3: Caso de uso, endpoint y contrato de la anulación

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:5-23` (`OrderDto`), `:76-90` (`OrderResponse`), y un record nuevo `CancelOrderRequest`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs:7-21`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs:61-66,281-311`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:353-355`
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:27,50,95-100,155`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs`

**Interfaces:**
- Consumes (Task 1): `Order.Cancel(MemberId, string?, DateTimeOffset)`, `Order.CancelledAt`, `Order.CancelledBy`, `Order.CancellationReason`, `OrderStatus.Cancelled`. (Task 2): `OrdersPermissions.OrderCancel` y su política.
- Produces:
  - `public sealed record CancelOrderCommand(Guid TenantId, Guid QuotationId, string? Reason) : ICommand<OrderDto>;`
  - `public sealed class CancelOrderHandler : ICommandHandler<CancelOrderCommand, OrderDto>`
  - `public sealed record CancelOrderRequest(string? Reason);`
  - `OrderDto` y `OrderResponse` con `DateTimeOffset? CancelledAt, Guid? CancelledBy, string? CancellationReason` justo después de `ApprovedBy`.
  - `POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/cancel` → 200 `OrderResponse` (contrato completo arriba, «Contrato HTTP resultante»).
  - Auditoría `quotation.order.cancelled` con `resourceId` = id del pedido.

- [ ] **Step 1: Escribir las pruebas de API que fallan**

En `OrderApiTests.cs`, después de `OrderItemsUrl` (línea 22):

```csharp

    private static string OrderCancelUrl(Guid tenantId, Guid quotationId) =>
        $"{OrderUrl(tenantId, quotationId)}/cancel";

    // Spec 2026-09-16, decisión 5: anular exige su propio permiso; el resto de la siembra y de la
    // lectura sigue usando los de gestión.
    private static readonly string[] CancellerPermissions =
        [.. ManagerPermissions, OrdersPermissions.OrderCancel];
```

Antes de `private static async Task<OrderResponse> ReadOrderAsync` (línea 958):

```csharp
    // Spec 2026-09-16: anular desde Pending deja quién, cuándo y por qué, lo persiste, no toca la
    // cotización (decisión 3) y lo audita. Los nombres de los campos se leen del JSON crudo: son el
    // contrato que consume el frontend.
    [Fact]
    public async Task CancelAPendingOrderReturnsItCancelledWithWhoWhenAndWhy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var converted = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, quotation.Id),
            new CancelOrderRequest("  El cliente desistió  "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Cancelled", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("cancelledAt").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("cancelledBy").ValueKind);
            Assert.Equal("El cliente desistió", root.GetProperty("cancellationReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("approvedAt").ValueKind);
        }

        var order = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(order);
        Assert.Equal(converted.Id, order.Id);
        Assert.NotNull(order.CancelledBy);

        var fetched = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Cancelled", fetched.Status);
        Assert.Equal(order.CancelledAt, fetched.CancelledAt);
        Assert.Equal(order.CancelledBy, fetched.CancelledBy);
        Assert.Equal("El cliente desistió", fetched.CancellationReason);

        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Converted", fetchedQuotation.Status);

        var auditMessages = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        var cancelled = Assert.Single(
            auditMessages, message => ActionOf(message) == "quotation.order.cancelled");
        Assert.Equal(order.Id.ToString(), EntityIdOf(cancelled));
    }

    // Decisiones 1 y 2: un aprobado también se anula, y conserva quién lo aprobó y cuándo.
    [Fact]
    public async Task CancelAnApprovedOrderKeepsTheApproval()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var approved = await ReadOrderAsync(await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken));

        var cancelled = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, quotation.Id),
            new CancelOrderRequest("Aprobado por error"),
            TestContext.Current.CancellationToken));

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.ApprovedAt);
        Assert.Equal(approved.ApprovedAt, cancelled.ApprovedAt);
        Assert.Equal(approved.ApprovedBy, cancelled.ApprovedBy);
        Assert.Equal("Aprobado por error", cancelled.CancellationReason);
    }

    // Decisión 5: gestionar pedidos no alcanza para anularlos.
    [Fact]
    public async Task CancelWithOnlyTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, quotation.Id),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("Pending", order.Status);
    }

    [Fact]
    public async Task CancelForAQuotationWithoutAnOrderIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, quotation.Id),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_found", body, StringComparison.Ordinal);
    }

    // Los tres 422 son códigos de dominio, no validation.failed (hallazgo 3): el frontend los
    // mapea por `code`. Un solo pedido para los cuatro requests: los rechazados no lo modifican.
    [Fact]
    public async Task CancelRejectsAMissingOrTooLongReasonAndASecondCancellation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var url = OrderCancelUrl(tenantId, quotation.Id);

        var withoutReason = await client.PostAsJsonAsync(
            url, new { }, TestContext.Current.CancellationToken);
        var blankReason = await client.PostAsJsonAsync(
            url, new CancelOrderRequest("   "), TestContext.Current.CancellationToken);
        var tooLong = await client.PostAsJsonAsync(
            url, new CancelOrderRequest(new string('a', 501)), TestContext.Current.CancellationToken);
        (await client.PostAsJsonAsync(
            url, new CancelOrderRequest("Primera vez"), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var twice = await client.PostAsJsonAsync(
            url, new CancelOrderRequest("Segunda vez"), TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(withoutReason, "order.order.cancellation_reason_required");
        await AssertDomainRejectionAsync(blankReason, "order.order.cancellation_reason_required");
        await AssertDomainRejectionAsync(tooLong, "order.order.cancellation_reason_too_long");
        await AssertDomainRejectionAsync(twice, "order.order.already_cancelled");
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("Primera vez", order.CancellationReason);
    }

    private static async Task AssertDomainRejectionAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(json.RootElement.TryGetProperty("errors", out _));
    }

```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderApiTests.Cancel"
```

Esperado: FAIL de compilación, `CS0246: No se encontró el tipo o el nombre del espacio de nombres 'CancelOrderRequest'` y `CS1061: 'OrderResponse' no contiene una definición para 'CancelledBy'` (o equivalentes en inglés).

- [ ] **Step 3: Sumar los campos al DTO, a la respuesta y al mapeo**

En `OrdersDtos.cs`, en `OrderDto`, después de `Guid? ApprovedBy,`:

```csharp
    /// <summary>Cuándo se anuló, quién (id de membership) y por qué (spec 2026-09-16). Null
    /// mientras el pedido no se anula.</summary>
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
```

En `OrderResponse`, después de `Guid? ApprovedBy,`:

```csharp
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
```

Después del record `AddOrderItemsRequest`:

```csharp

/// <summary>Anular un pedido (spec 2026-09-16). El motivo viaja nullable a propósito: ausente,
/// vacío o largo lo rechaza el dominio con su propio código (order.order.cancellation_reason_*),
/// que es lo que el frontend mapea — no hay validador que lo convierta en validation.failed.</summary>
public sealed record CancelOrderRequest(string? Reason);
```

En `OrderMapping.cs`, `ToDto` queda:

```csharp
    public static OrderDto ToDto(this Order order) => new(
        order.Id.Value,
        order.OrderNumber,
        order.QuotationId.Value,
        order.Status.ToString(),
        order.PaymentStatus.ToString(),
        order.Notes,
        order.ConvertedAt,
        order.ConvertedBy.Value,
        order.ApprovedAt,
        order.ApprovedBy?.Value,
        order.CancelledAt,
        order.CancelledBy?.Value,
        order.CancellationReason,
        order.RitualCollectionSyncId,
        order.CreatedAt,
        order.UpdatedAt,
        order.PaymentProofs.Select(ToDto).ToArray());
```

- [ ] **Step 4: Crear el caso de uso**

`src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record CancelOrderCommand(Guid TenantId, Guid QuotationId, string? Reason)
    : ICommand<OrderDto>;

/// <summary>
/// Anula un pedido (spec 2026-09-16), calcado de <see cref="ApproveOrderHandler"/>: mismo orden
/// —permiso, pedido, membresía de quien actúa, dominio, auditoría— y la misma unidad de trabajo.
///
/// Permiso propio (<see cref="OrdersPermissions.OrderCancel"/>) y no el de gestión: anular deshace
/// también un pedido que otra persona ya aprobó (decisión 5).
///
/// Sin validador a propósito: las reglas del motivo son del dominio y responden con sus códigos
/// (<c>order.order.cancellation_reason_required</c>/<c>_too_long</c>), no con
/// <c>validation.failed</c>. Sin evento de outbox, igual que aprobar.
///
/// La auditoría registra la acción y el pedido, no el motivo: el payload de auditoría no tiene
/// dónde llevar texto libre (sólo <c>changedFields</c>). El motivo queda en el propio pedido.
/// </summary>
public sealed class CancelOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CancelOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        CancelOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderCancel);

        var order = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        // CancelledBy es un id de membresía, como ApprovedBy: el módulo nunca guarda el usuario.
        var cancelledBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Cancel(cancelledBy, command.Reason, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.cancelled",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
```

En `QepServiceCollectionExtensions.cs`, después del registro de `ApproveOrderHandler` (tras la línea 355):

```csharp
        services.AddScoped<
            ICommandHandler<CancelOrderCommand, OrderDto>,
            CancelOrderHandler>();
```

- [ ] **Step 5: Exponer el endpoint**

En `OrderEndpoints.cs`, después del `MapPost("/approve", …)` (tras la línea 66):

```csharp

        // Anular (spec 2026-09-16): desde Pending o Approved, con motivo obligatorio. Política
        // propia y no OrderManage — ver CancelOrderHandler.
        group.MapPost("/cancel", CancelOrderAsync)
            .RequireAuthorization(OrdersPermissions.OrderCancel)
            .Accepts<CancelOrderRequest>("application/json")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Después de `ApproveOrderAsync` (tras la línea 292):

```csharp

    private static async Task<IResult> CancelOrderAsync(
        Guid tenantId,
        Guid quotationId,
        CancelOrderRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var order = await dispatcher.SendAsync(
            new CancelOrderCommand(tenantId, quotationId, request.Reason),
            cancellationToken);

        return Results.Ok(ToResponse(order));
    }
```

`ToResponse` queda:

```csharp
    private static OrderResponse ToResponse(OrderDto order) => new(
        order.Id,
        order.OrderNumber,
        order.QuotationId,
        order.Status,
        order.PaymentStatus,
        order.Notes,
        order.ConvertedAt,
        order.ConvertedBy,
        order.ApprovedAt,
        order.ApprovedBy,
        order.CancelledAt,
        order.CancelledBy,
        order.CancellationReason,
        order.RitualCollectionSyncId,
        order.CreatedAt,
        order.UpdatedAt,
        order.PaymentProofs
            .Select(proof => new OrderPaymentProofResponse(
                proof.Id, proof.FileId, proof.Amount, proof.UploadedAt))
            .ToArray());
```

- [ ] **Step 6: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderApiTests.Cancel"
```

Esperado: build `0 Errores`; PASS de las cinco pruebas `Cancel*`. Si `CancelWithOnlyTheManagePermissionIsForbidden` da 500 y no 403, la política de Task 2 no está registrada (hallazgo 5).

- [ ] **Step 7: Regresión de pedidos**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderApiTests|FullyQualifiedName~OrderContractApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
```

Esperado: PASS, 0 con error.

- [ ] **Step 8: Documentar el contrato**

En `docs/integracion-cotizaciones-y-pedidos.md`:

Línea 27, `Order.status:        Pending → Approved` pasa a:

```
Order.status:        Pending → Approved
                     Pending | Approved → Cancelled (con motivo; la cotización sigue Converted)
```

Después de la fila `POST /quotations/{id}/order/items` (línea 51):

```
| `POST` | `/quotations/{id}/order/cancel` | `CancelOrderRequest` | 200 `OrderResponse` en `Cancelled`. Desde `Pending` o `Approved`; exige `quotations.order.cancel` (sólo admin). Conserva `approvedAt`/`approvedBy` |
```

En el bloque `ts`, antes de `type OrderResponse = {`:

```ts
type CancelOrderRequest = { reason: string }; // obligatorio, se recorta, máx. 500

```

y `type OrderResponse` queda:

```ts
type OrderResponse = {
  id: string; orderNumber: string; quotationId: string; status: "Pending" | "Approved" | "Cancelled";
  paymentStatus: string; notes: string | null;
  convertedAt: string; convertedBy: string;
  approvedAt: string | null; approvedBy: string | null;
  cancelledAt: string | null; cancelledBy: string | null; cancellationReason: string | null;
  ritualCollectionSyncId: string | null;
  createdAt: string; updatedAt: string;
  paymentProofs: { id, fileId, amount, uploadedAt }[];
};
```

La línea que sigue al bloque (`advisorId`/`createdBy`/`updatedBy`/`convertedBy` son ids de **membership**…) pasa a nombrar también `approvedBy`/`cancelledBy`:

```
`advisorId`/`createdBy`/`updatedBy`/`convertedBy`/`approvedBy`/`cancelledBy` son ids de **membership** (Tenancy), no el
```

En la tabla de códigos, la fila `order.order.not_pending` pasa a decir `El pedido ya está `Approved` o `Cancelled`: …`, y después de ella:

```
| `order.order.already_cancelled` | 422 | `POST /order/cancel` sobre un pedido ya `Cancelled` |
| `order.order.cancellation_reason_required` | 422 | `POST /order/cancel` con `reason` ausente, vacío o en blanco. Código de dominio, sin `errors` |
| `order.order.cancellation_reason_too_long` | 422 | `POST /order/cancel` con `reason` de más de 500 caracteres ya recortado. Código de dominio, sin `errors` |
```

- [ ] **Step 9: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
if ((git branch --show-current) -ne "feature/cancel-order") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/CancelOrder.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs docs/integracion-cotizaciones-y-pedidos.md tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs; git commit -m "feat(orders): endpoint para anular un pedido"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task 4: El resumen del reporte de pedidos no cuenta los anulados

**Files:**
- Modify: `src/Bootstrapper/OrdersReportSource.cs:66-69`
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs`, `OrdersReportApiTests.cs`

**Interfaces:**
- Consumes (Task 3): `POST .../order/cancel` con `CancelOrderRequest`; (Task 2) `OrdersPermissions.OrderCancel`; (Task 1) `OrderStatus.Cancelled`.
- Produces: `GET /reports/orders/summary` sin pedidos `Cancelled` en `orderCount`, `subtotal`, `taxAmount`, `total`, `monthly`, `byAdvisor`, `byClient` ni en `previous`. `GET /reports/orders` sin cambios: los devuelve con `status: "Cancelled"`.

- [ ] **Step 1: Escribir las pruebas**

En `OrdersReportSummaryApiTests.cs`, agrega `using Modules.Quotations.Application;` después de `using System.Net.Http.Json;`, y después de `SummaryAddsUpTheTenantsOrdersAndRanksAdvisorAndClient`:

```csharp

    /// <summary>
    /// Spec 2026-09-16, decisión 6: un pedido anulado no es una venta. Dos pedidos del mismo
    /// cliente y asesor, uno anulado: el resumen cuenta uno solo en el total, la serie y los dos
    /// rankings. Filtrar en la consulta y no en memoria es lo que esta prueba cubre contra
    /// PostgreSQL.
    /// </summary>
    [Fact]
    public async Task SummaryLeavesOutACancelledOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var kept = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, kept);
        var cancelled = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, cancelled);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/quotations/{cancelled.Id}/order/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.OrderCount);
        Assert.Equal(kept.Subtotal, summary.Subtotal);
        Assert.Equal(kept.TaxAmount, summary.TaxAmount);
        Assert.Equal(kept.Total, summary.Total);
        Assert.Equal(1, Assert.Single(summary.Monthly).Count);
        Assert.Equal(1, Assert.Single(summary.ByAdvisor).Count);
        Assert.Equal(1, Assert.Single(summary.ByClient).Count);
    }
```

En `OrdersReportApiTests.cs`, agrega `using Modules.Quotations.Application;` después de `using System.Net.Http.Json;`, y después de `ListReturnsTheConvertedOrderWithItsQuotationAdvisorAndClient`:

```csharp

    /// <summary>
    /// Spec 2026-09-16, decisión 6: el listado sí muestra un pedido anulado, con su estado. Es la
    /// otra mitad de <c>OrdersReportSummaryApiTests.SummaryLeavesOutACancelledOrder</c>: el filtro
    /// del resumen no puede colarse en la consulta del listado.
    /// </summary>
    [Fact]
    public async Task ListStillReturnsACancelledOrderWithItsStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var order = await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/quotations/{quotation.Id}/order/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal("Cancelled", item.Status);
    }
```

- [ ] **Step 2: Correr las pruebas y ver el resultado esperado**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~SummaryLeavesOutACancelledOrder|FullyQualifiedName~ListStillReturnsACancelledOrderWithItsStatus"
```

Esperado: `SummaryLeavesOutACancelledOrder` FAIL con `Assert.Equal() Failure: Values differ — Expected: 1, Actual: 2`. `ListStillReturnsACancelledOrderWithItsStatus` **PASS**: es una prueba de guarda (el listado ya los devuelve) y se deja así a propósito; anótalo en el handoff.

- [ ] **Step 3: Filtrar los anulados en el resumen**

En `OrdersReportSource.cs`, el join de `SummarizeAsync` (líneas 66-69) queda:

```csharp
        // Un pedido anulado no es una venta (spec 2026-09-16, decisión 6): no suma en el total, la
        // serie ni los rankings, y tampoco en el período anterior, que pasa por este mismo método.
        // Se filtra antes del join para que llegue a la base como WHERE. El listado (BuildQuery) no
        // lo filtra: ahí el anulado se ve con su estado.
        var joined = from order in FilterOrders(criteria)
                         .Where(order => order.Status != OrderStatus.Cancelled)
                     join quotation in FilterQuotations(criteria)
                         on order.QuotationId equals quotation.Id
                     select new { order, quotation };
```

- [ ] **Step 4: Correr las pruebas de reportes y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --filter "FullyQualifiedName~OrdersReportSummaryApiTests|FullyQualifiedName~OrdersReportApiTests"
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
if ((git branch --show-current) -ne "feature/cancel-order") { throw "ABORT: rama equivocada" }; git add src/Bootstrapper/OrdersReportSource.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs; git commit -m "feat(reporting): el resumen de pedidos no cuenta los anulados"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: sin salida en las dos últimas.

---

### Task 5: Verificación completa del backend

**Files:** ninguno (sólo lectura y ejecución).

**Interfaces:**
- Consumes: Tasks 1 a 4 commiteadas.
- Produces: la evidencia literal para el handoff.

- [ ] **Step 1: Restore, build y modelo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
git status --short
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: `git status` vacío; build con `0 Advertencia(s)` y `0 Errores`; `No changes have been made to the model since the last migration.`

- [ ] **Step 2: Pruebas unitarias y de arquitectura, un proyecto por comando**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build
dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: PASS en los cinco, 0 con error.

- [ ] **Step 3: Pruebas de integración de los módulos tocados, un proyecto por comando**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --filter "FullyQualifiedName~AuthorizationCatalogApiTests|FullyQualifiedName~RoleApiTests|FullyQualifiedName~AuthorizationPermissionsMigrationTests"
```

Esperado: PASS, 0 con error. Si un proyecto pasa de 10 minutos y el comando se corta, pártelo por clase con `--filter "FullyQualifiedName~<Clase>"` y corre cada clase en su propio comando. Si algo falla, corre esa misma prueba en un worktree limpio de `origin/develop`: si también falla ahí, se anota en el handoff como previa; si no, se corrige antes de cerrar.

- [ ] **Step 4: Formato, commits y residuos**

Corre **el chequeo de formato** con la lista de archivos de toda la rama:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\anular-pedido
$base = git merge-base origin/develop HEAD
$files = git diff --name-only $base HEAD -- '*.cs' | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-anular-pedido-format"
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

Esperado: formato sin diagnósticos propios; cinco commits (`docs(orders): plan…`, y los cuatro de la tabla «Entrega»); el `Select-String` sin salida; rama `feature/cancel-order`.

- [ ] **Step 5: Handoff**

Entrega al developer, en este orden: la salida literal de RED y GREEN de cada tarea; la salida de los Steps 1-4 de esta tarea; los hallazgos 2 (auditoría sin motivo, desvío del spec) y 9 (`QuotationUserReferenceProbe` no cubre `approved_by` ni `cancelled_by`) como pendientes a decidir; y el contrato HTTP de la sección «Contrato HTTP resultante», que es la entrada del plan del frontend. La rama **no** se mergea ni se publica desde este plan.

---

## Self-review

**Cobertura del spec (sección Backend):**

| Requisito | Tarea |
|---|---|
| `OrderStatus.Cancelled` | 1 |
| `Order.Cancel` con los tres códigos, recorte, `Version++`, sin tocar `ApprovedAt`/`ApprovedBy` | 1 |
| Guards `not_pending` sin tocar, y cubiertos para un anulado | 1 (prueba `ACancelledOrderRejectsEveryPendingOnlyChange`) |
| `Quotation` no cambia | 3 (prueba de `Converted` tras anular) |
| `CancelOrder.cs` calcado de `ApproveOrder.cs`, auditoría `quotation.order.cancelled`, sin outbox | 3 (el motivo en auditoría: hallazgo 2) |
| `POST /cancel` con `RequireAuthorization(OrderCancel)`, body `{ reason }`, 200 | 3 |
| `cancelledAt`, `cancelledBy`, `cancellationReason` en DTO, mapeo y respuesta | 3 |
| Permiso, definición, política, sólo admin, `AuthorizationCatalogApiTests` | 2 |
| Rol admin persistido o no | Hallazgo 1: no; sin migración |
| Columnas y migración `AddOrderCancellation` | 1 |
| Resumen excluye anulados; listado los devuelve | 4 |
| `ExportStatusLabels` `Cancelled → "Anulado"` y su prueba | 1 |
| Pruebas `OrderTests`: Pending, Approved, dos veces, vacío, 501, recortado, los cuatro rechazos | 1 |
| Pruebas `OrderApiTests`: 200 con campos, 403 con `manage`, 404, 422 por código, auditoría | 3 |
| Reporte: resumen no suma, listado sí devuelve | 4 |

**Placeholders:** `<ts>` en el nombre de la migración es el timestamp que genera `dotnet ef`, no un hueco a completar a mano. No hay TBD ni pasos sin código.

**Consistencia de nombres:** `Order.CancellationReasonMaxLength`, `Order.Cancel(MemberId, string?, DateTimeOffset)`, `CancelledAt`/`CancelledBy`/`CancellationReason`, `OrdersPermissions.OrderCancel`, `CancelOrderCommand(TenantId, QuotationId, Reason)`, `CancelOrderHandler`, `CancelOrderRequest(Reason)` y `quotation.order.cancelled` se usan con el mismo nombre y tipo en las Tasks 1 a 4. En `OrderDto` y `OrderResponse` los tres campos van en la misma posición (después de `ApprovedBy`), y `OrderMapping.ToDto` y `OrderEndpoints.ToResponse` los pasan en ese orden.
