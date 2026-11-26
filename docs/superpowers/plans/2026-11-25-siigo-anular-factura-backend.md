# Siigo: liberar el pedido cuando la factura se anuló en Siigo (backend) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este repo mandan sobre el proceso del skill** (bloques, una revisión por bloque; ver «Global Constraints»).

**Goal:** Un endpoint por pedido, «Ya la anulé en Siigo», que verifica en Siigo que toda factura viva del pedido está anulada (o borrada, si Siigo lo dice con un código reconocido), las pasa a `Annulled` y le quita al pedido la marca de facturado; ante cualquier duda rechaza sin cambiar nada.

**Architecture:** Estado nuevo `SiigoInvoiceStatus.Annulled`, no vigente (`IsLive`, índice parcial y `SiigoOpenInvoiceCheck`). Una lectura nueva del cliente (`GetInvoiceAsync` → `SiigoInvoiceLookup`) que falla cerrado. `ConfirmSiigoAnnulmentHandler` verifica con el candado del pedido, guarda las filas y, después del commit, libera el pedido por un puerto nuevo de Quotations (`RevertExternalInvoicingAsync`). `SiigoInvoiceDto` suma `canConfirmAnnulment` y `orderUnmarkFailed`.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, CQ propio, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-25-siigo-anular-factura-design.md` (enfoque aprobado por el owner el 2026-11-25). Léelo junto con este plan; las secciones citadas (§1 a §7) son de ese spec.

## Global Constraints

- **Worktree y rama:** `feature/siigo-anular-factura`, en `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\siigo-anular-factura`. Nada se edita en el checkout de `develop`. Todo subagente hace `Set-Location` a ese worktree antes de cualquier script o comando de git. Antes de cada commit: `git branch --show-current` (en ese momento) y revisar que ningún `.cs` quedó en 0 bytes. Edita con las herramientas de edición, no con `Set-Content` (rompe las tildes).
- **Commits:** convencionales, uno por tarea, **sin** `Co-Authored-By` ni atribución de IA (regla del owner: ignora cualquier trailer que te pidan). Sin push.
- **TDD:** RED antes que GREEN, con la salida literal de ambos. Un RED que falla por compilación se prueba con `dotnet build` del proyecto de pruebas, sin contenedores; la integración corre en GREEN.
- **Bloques:** dos bloques, un implementador por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una sola vez, al final.
- **Integración:** mientras iteras, filtrada por nombre de prueba (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); la clase completa una vez al final de cada bloque. Antes de una corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una de otra sesión, espera, **nunca** la detengas. Nunca dos corridas de integración a la vez. Los subagentes corren las pruebas en **primer plano**. Cada corrida de integración va precedida de, en línea aparte, `$env:Seed__Enabled = "false"`. Si `Api.exe` está corriendo, avisa; no lo detengas si no lo lanzaste tú.
- **Gate de merge a `develop`:** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos. La suite completa no se corre.
- **Comandos:** Windows PowerShell (`A; if ($?) { B }`). Textos y comentarios en español colombiano tuteando; identificadores, códigos de error y nombres de enum sin traducir.
- **No inventar:** todo código, campo y ruta sale del spec o de este plan. **Nunca** se escribe un código de error de Siigo para «factura no existe»: `SiigoApiErrors.InvoiceNotFoundCodes` nace **vacío** (spec §2, verificación pendiente).
- **`docs/estado.md` no se toca** en la rama.
- **Registro legal:** nada se borra de `siigo.invoices`; el `Down` de la migración no se corre (comentario que lo diga). Ningún seed ni prueba prende `send_to_dian`.
- **Enums por nombre:** `SiigoInvoiceStatus.Annulled` y `SiigoCallOperation.InvoiceRead` van **al final** de su enum.
- **Valores fijos:** ruta `POST /api/v1/tenants/{tenantId:guid}/orders/{orderId:guid}/siigo-invoice/annulment`, sin cuerpo, permiso `siigo.invoice.issue`. Códigos HTTP nuevos (422, `details = { kind, number }`): `siigo.invoice.annulment_unverifiable`, `siigo.invoice.not_annulled`, `siigo.invoice.annulment_unclear`. Códigos de fila: `siigo.invoice.annulled_in_siigo`, `siigo.invoice.deleted_in_siigo`. Auditoría nueva: `siigo.invoice.annulled` (`outcome` = `annulled` | `deleted`, `fromStatus`). Auditoría de Quotations: `quotation.order.invoice_reverted` con `provider` y `number`. Proveedor: `siigo`.
- **No fuga:** el cuerpo de `GET /v1/invoices/{id}` no se loguea ni sale en una respuesta (`SiigoNoLeakTests`); `error_detail` sigue guardando códigos, no mensajes.
- **SQL entre módulos:** `SiigoOpenInvoiceCheck` lee `status`, `order_id`, `tenant_id` por nombre (`.claude/rules/sql-entre-modulos.md`).
- **Pruebas de integración de Siigo:** cada una levanta su host (`StartDatabaseAsync` + `ArrangeAsync`); se factura con `IssueRowsThroughBatchAsync`. La factura por defecto del doble es `FV-1-123` con id `d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f`.

## Review Focus

1. **Un 404 pelado** (ruta o `BaseUrl` mal armada) nunca libera el pedido: tiene que salir `annulment_unclear` y dejar la falla en `call_failures`. Pruebas en Task 4 (`ABare404IsUnrecognizedAndLeavesItsReport`) y Task 5 (`ABare404FromSiigoReleasesNothing`).
2. **Pedido con flete, sólo una anulada en Siigo:** ninguna fila cambia y el pedido sigue marcado. Task 5 (`WithTheFreightStillLiveInSiigoNothingChanges`).
3. **Doble pulsación o repetir después del éxito:** 200 con las mismas filas, sin otra llamada de anulación ni otra auditoría. Task 5 (`RepeatingAfterSuccessIsIdempotentAndDoesNotCallSiigo`).
4. **La marca del pedido falla después del commit:** filas `Annulled`, `orderUnmarkFailed` y `canConfirmAnnulment` en `true`, y repetir libera sin llamar a Siigo. Task 6 (`AFailedUnmarkIsOfferedAgainAndRetriedWithoutCallingSiigo`).
5. **Una tanda vieja con un ítem `ToConfirm` cuya fila terminó `Annulled`:** leer la tanda no da 500. Task 1 (`AToConfirmWhoseInvoiceWasAnnulledLooksFailedWithTheRowCode`).

---

## Bloque 1 — Estado `Annulled` y liberación del pedido (Tareas 1 a 3, un implementador)

### Task 1: Dominio de la factura anulada

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoInvoiceStatus.cs`, `SiigoInvoice.cs`, `SiigoErrorCodes.cs`, `SiigoInvoiceBatch.cs` (`SiigoInvoiceBatchItem.Effective`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceTests.cs`, `SiigoBatchItemEffectiveStateTests.cs`

**Interfaces:**
- Produces:
  - `SiigoInvoiceStatus.Annulled` (al final; XML doc del enum actualizado: tampoco es vigente).
  - `SiigoInvoice.IsLive` → `Status is not (Rejected or Failed or Annulled)`.
  - `bool SiigoInvoice.IsAnnulmentVerifiable` → `Status is Created or Stamped or StampRejected && SiigoInvoiceId is not null`.
  - `void SiigoInvoice.MarkAnnulled(bool deleted, DateTimeOffset now)`: sin `IsAnnulmentVerifiable` lanza `SiigoDomainException(SiigoErrorCodes.InvoiceStateInvalid, …)`; si no, `Status = Annulled`, `ErrorCode = deleted ? InvoiceDeletedInSiigo : InvoiceAnnulledInSiigo`, `ErrorDetail = null`, `ExpectedPaymentsTotal = null`, `SendingSince = null`, `UpdatedAt = now`. Conserva `SiigoInvoiceId`, `Number`, `Cufe`, `SiigoTotal`.
  - `SiigoErrorCodes`: `InvoiceAnnulmentUnverifiable = "siigo.invoice.annulment_unverifiable"`, `InvoiceNotAnnulled = "siigo.invoice.not_annulled"`, `InvoiceAnnulmentUnclear = "siigo.invoice.annulment_unclear"`, `InvoiceAnnulledInSiigo = "siigo.invoice.annulled_in_siigo"`, `InvoiceDeletedInSiigo = "siigo.invoice.deleted_in_siigo"`, con XML doc (status y `details`).
  - `Effective`: brazo `SiigoInvoiceStatus.Annulled => new SiigoBatchItemState(Failed, invoice.ErrorCode, null, false, false, null)`.
- [ ] **Step 1: Pruebas que fallan** (filas de `SiigoInvoiceTests.NewPending` llevadas a `Created` con `MarkCreated`):
  - `SiigoInvoiceTests.ACreatedInvoiceIsAnnulledKeepingItsSiigoData`: `MarkCreated("id-1","FV-1-1",100m,null,Now)`, `MarkAnnulled(false, Now+1m)` → `Status == Annulled`, `IsLive == false`, `ErrorCode == "siigo.invoice.annulled_in_siigo"`, `Number == "FV-1-1"`, `SiigoInvoiceId == "id-1"`, `HasPdf == false`, `CanStamp == false`, `UpdatedAt == Now+1m`.
  - `ADeletedInvoiceCarriesTheDeletedCode`: `MarkAnnulled(true, …)` → `ErrorCode == "siigo.invoice.deleted_in_siigo"`.
  - `AStampRejectedInvoiceIsAnnulledAndLosesTheDianError`: tras `MarkStampRejected("x","y",…)`, `MarkAnnulled(false, …)` → `ErrorDetail == null`.
  - `OnlyAVerifiableInvoiceCanBeAnnulled` (`[Theory]`): `Pending`, `Unknown` (vía `ExpireIfStale`), `Rejected`, `Failed`, `Annulled` y una `Created` por `ResolveExists` (sin id) lanzan `SiigoDomainException` con `Code == "siigo.invoice.state_invalid"`; `IsAnnulmentVerifiable` en `false` en todas.
  - `SiigoBatchItemEffectiveStateTests.AToConfirmWhoseInvoiceWasAnnulledLooksFailedWithTheRowCode`: ítem `ToConfirm` enlazado a una fila que después quedó creada con id (`MarkCreated`) y anulada (`MarkAnnulled(false, …)`) → `Status == Failed`, `ErrorCode == "siigo.invoice.annulled_in_siigo"`, `CanResend`/`CanResolve` en `false`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`MarkAnnulled`, `Annulled`). Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces». Actualiza el XML doc de `SiigoInvoice` (la vigencia excluye `Annulled`).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceTests|FullyQualifiedName~SiigoBatchItemEffectiveStateTests|FullyQualifiedName~SiigoInvoiceAwaitingConfirmationTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): add the annulled invoice status"`.

### Task 2: Índice parcial, migración y lectores que no pasan por `IsLive`

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs` (filtro de `IX_invoices_tenant_order_kind`), `src/Bootstrapper/SiigoOpenInvoiceCheck.cs`, `src/Bootstrapper/SiigoBatchItemInvoker.cs` (`Map`), `src/Modules/Quotations/Modules.Quotations.Application/IExternalInvoiceStatus.cs` (XML doc)
- Create: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/Migrations/<timestamp>_AddSiigoInvoiceAnnulled.cs` (+ `.Designer.cs` y snapshot, generados)
- Modify: `.claude/rules/siigo.md` (viñeta «Una factura viva bloquea…»: todo estado menos `Rejected`, `Failed` y `Annulled`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoDbContextMappingTests.cs`; create `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceAnnulmentApiTests.cs`

**Interfaces:**
- Consumes: `SiigoInvoiceStatus.Annulled` (Task 1).
- Produces: filtro `status NOT IN ('Rejected', 'Failed', 'Annulled')` en el índice y en el `WHERE` de `SiigoOpenInvoiceCheck`; en `Map`, `nameof(SiigoInvoiceStatus.Annulled)` en el brazo de `Rejected`/`Failed`. `SiigoInvoiceAnnulmentApiTests` es la clase de integración de esta feature (Tasks 5 y 6 la amplían) y declara el helper `private static Task AnnulBySqlAsync(InvoicingWorld world)` (`UPDATE siigo.invoices SET status = 'Annulled', error_code = 'siigo.invoice.annulled_in_siigo' WHERE status IN ('Created','Stamped','StampRejected')`).
- [ ] **Step 1: Pruebas que fallan:**
  - `SiigoDbContextMappingTests.TheCurrentInvoiceIndexLeavesOutAnnulledRows`: el índice `IX_invoices_tenant_order_kind` tiene filtro `status NOT IN ('Rejected', 'Failed', 'Annulled')`.
  - `SiigoInvoiceAnnulmentApiTests.AnAnnulledRowNoLongerBlocksTheManualMarkNorANewInvoice`: factura creada por tanda; `UPDATE quotations.orders SET invoiced_at = NULL, invoiced_by = NULL, invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL WHERE id = @id` y `AnnulBySqlAsync`; `POST …/orders/{id}/invoice` (marca manual) **no** responde 409 `order.order.external_invoice_pending`; en otro mundo igual, con `FV-1-124`/`id-2` en el doble, `IssueRowsThroughBatchAsync` crea una fila `Created` nueva (dos filas `Order` en la tabla, una `Annulled`).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~TheCurrentInvoiceIndexLeavesOutAnnulledRows"` falla en el filtro. (La de integración se corre en GREEN.)
- [ ] **Step 3: Implementar** el filtro, la SQL y el brazo de `Map`; actualiza los XML doc de `SiigoOpenInvoiceCheck` e `IExternalInvoiceStatus` y la viñeta de `siigo.md`.
- [ ] **Step 4: Generar la migración** — `dotnet ef migrations add AddSiigoInvoiceAnnulled --project src/Modules/Siigo/Modules.Siigo.Infrastructure --context SiigoDbContext`. `Up` sólo hace `DropIndex` + `CreateIndex` del índice con el filtro nuevo; XML doc de la clase: aditiva (spec §7). `Down` generado con el comentario «No se corre: registro legal».
- [ ] **Step 5: GREEN** — unitarias: `--filter "FullyQualifiedName~SiigoDbContextMappingTests"` (incluye `TheModelHasNoChangesPendingAMigration`). Integración: `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~AnAnnulledRowNoLongerBlocks|FullyQualifiedName~TheRealStatusAdapterSeesOpenInvoices"`.
- [ ] **Step 6: Commit** — `git commit -m "feat(siigo): treat annulled invoices as not current in the index and the open check"`.

### Task 3: Quotations libera la marca de un proveedor externo

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (junto a `InvoiceExternally`), `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs`, `ExternalOrderInvoicingService.cs`, `OrderChangeSummary.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (`ISiigoOrderSource`), `src/Bootstrapper/SiigoOrderSource.cs`
- Modify (dobles que implementan los puertos): `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoSavedInvoiceQueueReadTests.cs` (`Orders`), `SendSiigoInvoiceWhatsAppHandlerTests.cs` (`MarkedOrders`), `IssueSiigoInvoiceHandlerTests.cs` (`OrderSource`), `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoBatchItemInvokerTests.cs` (`OrderMarks`), `SiigoOrderSourceTests.cs` (`FakeInvoicing`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderExternalInvoicingTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`

**Interfaces:**
- Produces:
  - `bool Order.RevertExternalInvoicing(string provider, string number, MemberId revertedBy, DateTimeOffset occurredAt)`: con `IsInvoiced` y mismo `InvoiceProvider` y `InvoiceNumber` (ordinal) limpia `InvoicedAt`, `InvoicedBy`, `InvoiceProvider`, `InvoiceNumber`, `InvoiceExternalId`, `UpdatedAt = occurredAt`, `Version++` y devuelve `true`; en cualquier otro caso no cambia nada y devuelve `false`.
  - `OrderChangeSummary.ExternalInvoiceAnnulled(string provider, string number)` → `Trim($"Se anuló en {ProviderDisplayName(provider)} la factura {number}; el pedido quedó sin facturar.")`.
  - `Task<bool> IExternalOrderInvoicing.RevertExternalInvoicingAsync(Guid tenantId, Guid orderId, string provider, string number, Guid memberId, CancellationToken)`: carga con tracking (`OrderNotFound.ById` si no está), llama al dominio; con `true` agrega historial `OrderHistoryEventType.InvoicingReverted` con el resumen de arriba, `PublishWithDetails(…, "quotation.order.invoice_reverted", …, { ["provider"], ["number"] })` y guarda. Reintenta una vez ante `RequestConcurrencyException` (mismo patrón que `MarkInvoicedAsync`).
  - `Task<bool> ISiigoOrderSource.UnmarkInvoicedAsync(Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken)`; `SiigoOrderSource` reenvía con `Provider`.
- [ ] **Step 1: Pruebas que fallan** en `OrderExternalInvoicingTests`:
  - `RevertingTheSameExternalMarkClearsItAndBumpsTheVersion`: `InvoiceExternally("siigo","FV-1-1","id-1",…)`, `RevertExternalInvoicing("siigo","FV-1-1",…)` → `true`, `IsInvoiced == false`, `InvoiceProvider`, `InvoiceNumber`, `InvoiceExternalId` nulos, versión +1.
  - `RevertingAnotherMarkLeavesTheOrderAsItWas` (`[Theory]`: sin marca, marca manual con `Invoice(…)`, `siigo` con `FV-1-2`) → `false`, versión y marca sin cambio.
  - `RevertExternalInvoicingWritesTheHistoryAndTheAuditOnlyWhenItReleases` (estilo de `MarkInvoicedWritesTheHistoryAndTheAudit…`): con la marca → un historial `InvoicingReverted` con «Se anuló en Siigo la factura FV-1-1; el pedido quedó sin facturar.» y una auditoría `quotation.order.invoice_reverted` con `provider = siigo` y `number = FV-1-1`; la segunda llamada → `false`, sin historial ni auditoría nuevos.
  - `SiigoOrderSourceTests.UnmarkPassesTheSiigoProvider`: `FakeInvoicing` anota `(provider, number)` → `("siigo", "FV-1-1")` y devuelve su resultado.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` y `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Esperado: no compilan.
- [ ] **Step 3: Implementar** lo de «Interfaces» y completar los dobles (devuelven `false` o lanzan `NotSupportedException` si la prueba no lo usa).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderExternalInvoicingTests"`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SiigoOrderSourceTests|FullyQualifiedName~SiigoBatchItemInvokerTests"`; `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`.
- [ ] **Step 5: Commit** — `git commit -m "feat(quotations): release an order whose external invoice was annulled"`.

### Cierre del bloque 1

- [ ] `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~Siigo|FullyQualifiedName~Quotations"`.
- [ ] Integración, en fila: `SiigoInvoiceAnnulmentApiTests` completa y `--filter "FullyQualifiedName~OrderExternalInvoicingApiTests"` (Quotations.IntegrationTests).
- [ ] Una revisión del bloque (lente `review-reliability`) sobre el diff del bloque.

---

## Bloque 2 — Lectura en Siigo, caso de uso y BFF (Tareas 4 a 6, un implementador)

### Task 4: `GetInvoiceAsync` en el cliente de Siigo

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/ISiigoApi.cs`, `SiigoApiErrors.cs`, `src/Modules/Siigo/Modules.Siigo.Domain/SiigoCallOperation.cs`, `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoApiClientTests.cs`

**Interfaces:**
- Produces:
  - `SiigoCallOperation.InvoiceRead` (al final, con XML doc).
  - `public static IReadOnlySet<string> SiigoApiErrors.InvoiceNotFoundCodes` — **vacío**, con un comentario que remita a «Verificación pendiente» del spec y diga que se llena sólo con la respuesta real de Siigo.
  - En `ISiigoApi.cs`:
    ```csharp
    public abstract record SiigoInvoiceLookup
    {
        private SiigoInvoiceLookup() { }
        public sealed record Found(bool Annulled, string? StampStatus) : SiigoInvoiceLookup;
        public sealed record NotFound : SiigoInvoiceLookup;
        public sealed record Unrecognized(int? Status) : SiigoInvoiceLookup;
    }
    ```
    y `Task<SiigoInvoiceLookup> ISiigoApi.GetInvoiceAsync(SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)`.
  - Implementación: `CallAsync(GET, $"v1/invoices/{Uri.EscapeDataString(id)}", …, _timeouts.Default)`; `ReportFailureAsync(…, SiigoCallOperation.InvoiceRead, reply, null, siigoInvoiceId, …)`; transitorio → `ServiceUnavailableException(siigo.unavailable)`; credenciales → `ResourceConflictException(siigo.connection_unavailable)`; no 2xx: errores todos de servicio → `UpstreamServiceException(siigo.invoice.siigo_service)`, 404 con errores no vacíos y todos en `InvoiceNotFoundCodes` → `NotFound`, resto → `Unrecognized(status)`; 2xx: `id` igual y `annulled` `JsonValueKind.True`/`False` → `Found(…, stamp.status)`; si no → `Unrecognized(200)` y la falla va a `call_failures` como `invalid_response` con el id de Siigo (extiende `ReportInvalidResponseAsync` con `string? siigoInvoiceId = null`).
- [ ] **Step 1: Pruebas que fallan** en `SiigoApiClientTests` (stub HTTP del archivo):
  - `AnAnnulledInvoiceIsFoundAnnulledWithItsStampStatus`: `200 {"id":"id-1","annulled":true,"stamp":{"status":"Draft"}}` → `Found(true, "Draft")`; nada en el log de fallas.
  - `ANotAnnulledInvoiceIsFoundNotAnnulled`: `annulled:false` → `Found(false, …)`.
  - `AnAnswerWithoutAnnulledOrWithAnotherIdIsUnrecognizedAndLeavesItsReport` (`[Theory]`: sin `annulled`, `"annulled":"true"` como texto, `id` distinto, cuerpo no JSON) → `Unrecognized`, y una falla `InvoiceRead` con razón `invalid_response` y el id de Siigo.
  - `ABare404IsUnrecognizedAndLeavesItsReport`: `404 {"Errors":[{"Code":"some_code","Message":"x"}]}` y `404` vacío → `Unrecognized(404)`; falla `InvoiceRead`, razón `http_404`.
  - `AServiceErrorOnTheInvoiceReadSaysSiigoIsDown`: `400 documents_service` → lanza `UpstreamServiceException` con `Code == "siigo.invoice.siigo_service"`.
  - `ATimeoutOnTheInvoiceReadIsUnavailable`: `503` → `ServiceUnavailableException` `siigo.unavailable`.
  - `TheNotFoundCodesStartEmpty`: `Assert.Empty(SiigoApiErrors.InvoiceNotFoundCodes)` (si alguien la llena, que sea a propósito y con la evidencia).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`GetInvoiceAsync`, `SiigoInvoiceLookup`). Agrega `GetInvoiceAsync` a los proxies de `ISiigoApi` de las pruebas si lo exige la compilación (lanzan, como hoy).
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~SiigoApiClientTests|FullyQualifiedName~SiigoMetricsTests|FullyQualifiedName~SiigoTimeoutBudgetTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): read an invoice in siigo and fail closed on unknown answers"`.

### Task 5: `POST …/siigo-invoice/annulment`

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/ConfirmSiigoAnnulment.cs` (comando, excepción y handler)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoAuditActions.cs`, `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro junto a `ResolveSiigoInvoiceHandler`)
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceAnnulmentApiTests.cs`

**Interfaces:**
- Consumes: Task 1 (`MarkAnnulled`, `IsAnnulmentVerifiable`, códigos), Task 3 (`ISiigoOrderSource.UnmarkInvoicedAsync`), Task 4 (`GetInvoiceAsync`).
- Produces:
  - `SiigoAuditActions.InvoiceAnnulled = "siigo.invoice.annulled"`.
  - `public sealed record ConfirmSiigoAnnulmentCommand(Guid TenantId, Guid OrderId) : ICommand<IReadOnlyList<SiigoInvoiceDto>>`.
  - `public sealed class SiigoAnnulmentRejectedException(string code, SiigoInvoiceKind kind, string? number) : DomainException(code, …), IHasProblemExtensions` con `ProblemExtensions["details"] = new { kind = kind.ToString(), number }`.
  - `ConfirmSiigoAnnulmentHandler` con los pasos 1 a 10 del spec §3, en ese orden (dependencias: `IExecutionContext`, `ITenantModules`, `IMembershipDirectory`, `ISiigoOrderSource`, `ISiigoInvoiceRepository`, `ISiigoUnitOfWork`, `ISiigoAuditRecorder`, `ISiigoConnectionDirectory`, `ISiigoApi`, `IClock`, `ISiigoInvoiceBatchRepository`, `ILogger<…>`). `Found(true, "Accepted")` → `annulment_unclear`. La liberación del pedido va en un `TryUnmarkAsync` con `catch (Exception) when (!cancellationToken.IsCancellationRequested)` y `[LoggerMessage]` warning con sólo el tipo de la excepción. Esta tarea devuelve las filas con `SiigoInvoiceDto.From(row, now)`; los flags llegan en Task 6.
  - Endpoint `invoice.MapPost("/annulment", …)` con `.Produces<IReadOnlyList<SiigoInvoiceDto>>()` y problemas 403, 404, 409, 422, 502, 503.
- Helpers de la clase de pruebas: `AnnulledInSiigo(string id)` = `200 {"id":id,"annulled":true,"stamp":{"status":"Draft"}}` en la ruta `/v1/invoices/{id}` del doble; `PostAnnulmentAsync(world)`.
- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceAnnulmentApiTests`:
  - `AnInvoiceAnnulledInSiigoReleasesTheOrderAndItCanBeInvoicedAgain`: factura creada; el doble contesta anulada → 200, fila `Order` `Annulled` con `errorCode = siigo.invoice.annulled_in_siigo`; el pedido tiene `invoiced = false` e `invoiceProvider` nulo; `audit.entries` tiene `siigo.invoice.annulled` con `"outcome":"annulled"` y `"fromStatus":"Created"`, y `quotation.order.invoice_reverted`; una tanda nueva con `FV-1-124` crea otra `Created` con otra `idempotency_key`.
  - `WithTheFreightStillLiveInSiigoNothingChanges` (`ArrangeAsync(database, freightSeparate: true, freight: true)`): la del pedido anulada, la de flete `annulled:false` → 422 `siigo.invoice.not_annulled` con `details.kind = "Freight"`; las dos filas siguen `Created` y el pedido marcado. Con las dos anuladas → 200 y las dos `Annulled`.
  - `NotAnnulledInSiigoIs422AndChangesNothing`: `annulled:false` → 422 `not_annulled`, `details.kind = "Order"`, `details.number = "FV-1-123"`; fila y pedido intactos.
  - `ABare404FromSiigoReleasesNothing`: 404 vacío → 422 `siigo.invoice.annulment_unclear`; una fila en `siigo.call_failures` con `operation = 'InvoiceRead'`; nada cambia.
  - `AnAnnulledButDianAcceptedAnswerIsUnclear`: `annulled:true` con `stamp.status = "Accepted"` → 422 `annulment_unclear`.
  - `AnInvoiceResolvedByHandOrStillPendingCannotBeVerified`: (a) 503 en la creación → `Pending` → 422 `annulment_unverifiable`; (b) Unknown resuelta `exists` con número (sin id) → 422 `annulment_unverifiable`; en los dos casos el doble no recibe ningún `GET /v1/invoices/…`.
  - `AnOrderInAnOpenBatchIs409`: `INSERT`/estado de un ítem `Pending` del pedido (como las pruebas de tandas) → 409 `siigo.invoice_batch.order_in_progress`; ningún `GET` a Siigo.
  - `SiigoDownIs503AndChangesNothing`: `GET` de la factura con 503 → 503 `siigo.unavailable`.
  - `RepeatingAfterSuccessIsIdempotentAndDoesNotCallSiigo`: dos `POST` → 200 los dos, un solo `GET /v1/invoices/{id}` en el doble, una sola auditoría `siigo.invoice.annulled`.
  - `NothingToAnnulIs422AndNoRowsIs404`: con sólo una `Rejected` → 422 `siigo.invoice.state_invalid`; pedido sin facturas de Siigo → 404 `siigo.invoice.not_found`.
  - `AManualMarkPutInBetweenIsNotTouched`: por SQL, la marca del pedido pasa a manual (`invoice_provider`, `invoice_number`, `invoice_external_id` en `NULL`) → 200, fila `Annulled`, el pedido sigue `invoiced = true` y no hay `quotation.order.invoice_reverted`.
  - `WithoutThePermissionItIs403` (`X-Permissions` sin `siigo.invoice.issue`).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` no compila si usas tipos nuevos; si compila, la corrida filtrada responde 404/405 en la ruta. Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoInvoiceAnnulmentApiTests"`; luego `--filter "FullyQualifiedName~SiigoNoLeakTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): confirm an invoice annulled in siigo and release the order"`.

### Task 6: `canConfirmAnnulment`, `orderUnmarkFailed` y documentación

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceDto.cs`, `SiigoOrderMark.cs`, `GetSiigoInvoices.cs`, `ConfirmSiigoAnnulment.cs`
- Modify: `README.md` (§ «Integración con Siigo», párrafo «Generar la factura»: la ruta `/annulment`, los dos flags y el estado `Annulled`), `.claude/rules/siigo.md` (viñeta nueva: anulación verificada, todo o nada, `InvoiceNotFoundCodes` vacía hasta verificar, Siigo primero y el pedido después)
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceAnnulmentApiTests.cs`

**Interfaces:**
- Consumes: Task 5.
- Produces:
  - `SiigoInvoiceDto` suma al final `bool CanConfirmAnnulment = false, bool OrderUnmarkFailed = false`; `From(…, bool canConfirmAnnulment = false, bool orderUnmarkFailed = false)`; XML doc con el porqué (spec §5).
  - `internal static bool SiigoOrderMark.CanConfirmAnnulment(SiigoInvoice row, IReadOnlyList<SiigoInvoice> rows, bool queued, bool orderUnmarkFailed)`: `false` si `queued` o si `row.Kind != Order`; `row.IsLive` → todas las vivas de `rows` cumplen `IsAnnulmentVerifiable`; `row.Status == Annulled` → `orderUnmarkFailed`.
  - `internal static Task<bool> SiigoOrderMark.IsStillMarkedAfterAnnulmentAsync(ISiigoOrderSource orders, SiigoInvoice row, ILogger logger, CancellationToken)`: sólo fila `Order` `Annulled` con número; usa `IsConfirmedAsync` (5 s; falla → `false` y warning).
  - `GetSiigoInvoicesHandler` y la respuesta de `ConfirmSiigoAnnulmentHandler` calculan los dos flags (la respuesta lee la tanda abierta como `SiigoSavedInvoice`: si la lectura falla, `queued = true`). En el reintento (sin filas vivas), el handler llama `UnmarkInvoicedAsync` sin ningún `GET` a Siigo.
- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceAnnulmentApiTests`:
  - `TheListOffersTheAnnulmentOnlyWhenEveryLiveInvoiceCanBeVerified`: `Created` → fila `Order` con `canConfirmAnnulment = true`, la de flete (con `freight: true`) en `false`; la de flete `Pending` (503 en su envío) → `false` en la del pedido; con un ítem de tanda abierto → `false`; tras anular → `false` y `orderUnmarkFailed = false`.
  - `AFailedUnmarkIsOfferedAgainAndRetriedWithoutCallingSiigo`: anulada con éxito, luego por SQL se vuelve a poner la marca `siigo`/`FV-1-123` en el pedido (simula la marca que falló) → la lista dice `orderUnmarkFailed = true` y `canConfirmAnnulment = true`; `POST …/annulment` → 200, el pedido `invoiced = false`, y el doble no recibió otro `GET /v1/invoices/{id}`.
  - `TheAnnulmentResponseCarriesTheFlags`: la respuesta del primer `POST` trae `canConfirmAnnulment = false` y `orderUnmarkFailed = false`.
- [ ] **Step 2: RED** — la corrida filtrada falla (`canConfirmAnnulment` no existe en el JSON). Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces», el README y la viñeta de `siigo.md`.
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"` y `--filter "FullyQualifiedName~TheListOffersTheAnnulment|FullyQualifiedName~AFailedUnmarkIsOffered|FullyQualifiedName~TheAnnulmentResponseCarries"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): tell the order screen when to offer the annulment"`.

### Cierre del bloque 2

- [ ] `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo.
- [ ] Integración, en fila y por clase completa: `SiigoInvoiceAnnulmentApiTests`, `SiigoInvoiceApiTests`, `SiigoInvoiceBatchApiTests`, `SiigoNoLeakTests`.
- [ ] Una revisión del bloque (lente `review-risk`: libera un documento legal según una respuesta externa) sobre el diff del bloque.

---

## Revisión final y merge a `develop`

- [ ] Revisión fuerte, una vez, sobre toda la rama (modelo más capaz), con el spec y el «Review Focus» a la vista.
- [ ] `Set-Location` al worktree y `.\scripts\test-feature.ps1`, en verde y leyendo sus avisos.
- [ ] Merge a `develop` (nunca a `main`). `docs/estado.md` se actualiza en `develop`, en el merge o en un commit `docs:` inmediatamente después, con la fila de esta feature y las verificaciones pendientes del spec.
- [ ] Handoff: la migración es aditiva (spec §7); el botón de `qep-frontend` se publica después del rollout del backend.
