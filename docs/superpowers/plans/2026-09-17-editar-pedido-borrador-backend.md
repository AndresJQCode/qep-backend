# Editar pedido como borrador — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que "Editar pedido" guarde todos sus cambios (productos, comprobantes, notas) en **una sola escritura atómica** con `If-Match: <Order.Version>`, con el estado de pago derivado en el servidor, y que exista un **cálculo previo** que devuelva el documento recalculado sin persistir nada.

**Architecture:** `Order` gana tres piezas que no exigen "al menos un comprobante" (`AttachPaymentProofs`, `CorrectPaymentProofs`, `UpdateNotes`). Un helper de aplicación, `OrderItemEdits`, aplica la lista deseada de productos sobre `Quotation` en el orden altas → cambios → bajas. Los dos repositorios ganan `FindUntrackedAsync` (`AsNoTracking`). `SaveOrderEditsHandler` (comando, tracked, un `SaveChangesAsync`) y `PreviewOrderEditsHandler` (query, untracked, sin escribir) comparten contrato (`IOrderEdits`) y validador base (`OrderEditsValidator<T>`). Cuelgan de `PUT /quotations/{quotationId}/order` y `POST /quotations/{quotationId}/order/preview`, y responden `OrderDetailResponse` compuesto con `QuotationResponseComposer`. `OrderDto`/`OrderResponse` suman `Version`. Los endpoints actuales no se tocan.

**Tech Stack:** .NET 10 (SDK de `global.json`), EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`; Docker corriendo para las pruebas de integración).

**Spec:** `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-frontend/docs/superpowers/specs/2026-09-17-editar-pedido-borrador-design.md`. Este plan implementa **sólo** su sección «Backend (`qep-backend`)».

## Global Constraints

**Del spec** (valores copiados tal cual):

- Decisión 1: «**Todos** los cambios de la pantalla (productos, comprobantes, notas) viven en un borrador local y se persisten sólo con "Guardar pedido", previa confirmación.»
- Decisión 2: «Un **comando atómico** nuevo recibe el **estado deseado completo** y el backend calcula la diferencia.»
- Decisión 3: «un endpoint de **cálculo previo** devuelve el documento completo recalculado (líneas, descuentos, impuestos, retención, total, neto, estado de pago) **sin persistir**.»
- Decisión 4: «El cálculo previo carga el agregado **sin rastreo** (`AsNoTracking`) y aplica los mismos métodos de dominio que el guardado.»
- Decisión 5: «**El servidor deriva `paymentStatus`** en el comando nuevo; el cliente ya no lo manda.»
- Decisión 6: «Concurrencia optimista con **`If-Match: <Order.Version>`**; conflicto → `412`.»
- Decisión 8: «Los endpoints actuales **no se tocan**; la pantalla de edición deja de usarlos.»
- Decisión 9 (fuera de alcance): «elegir por línea una escala de precio distinta de la que corresponde a la cantidad.»
- Cuerpo: `{ "items": [{ "productId", "quantity" }], "proofs": { "add": [{ "fileId", "amount" }], "update": [{ "proofId", "amount", "newFileId" }], "removeIds": [] }, "notes": "string|null" }`.
- «`items` es la lista **completa** deseada. La clave es `productId`.» «`proofs.update` sólo lleva los comprobantes que cambian; los no mencionados se conservan.» «`notes` reemplaza el campo entero (`null` lo limpia)».
- «En el cálculo previo, `add[].fileId` se omite (los archivos todavía no se subieron) y `update[].newFileId` se ignora: sólo importan los montos.»
- Validación: «`items` no vacío; `productId` no vacío; `quantity > 0`; sin `productId` repetido.» «`add[].amount > 0`, `add[].fileId` no vacío (sólo en guardado).» «`update[].proofId` no vacío y sin repetir; `amount > 0`; `newFileId` no es `Guid.Empty`.» «Un `proofId` no puede estar en `update` y en `removeIds` a la vez.» «Ningún `fileId` repetido entre `add` y `update[].newFileId`.» «`notes` máximo 500 (`Order.NotesMaxLength`).»
- Orden de productos: «**altas, cambios, bajas**. Es el inverso de `BatchUpdateQuotationItems.cs` a propósito».
- Conflicto: «`order.Version != ExpectedVersion` → `RequestConcurrencyException("concurrency.conflict")` (→ `412`).» Pedido no `Pending` → `order.order.not_pending`.
- Auditoría «`quotation.order.item_added|item_updated|item_removed` por cada cambio» y «`quotation.order.payment_proofs_added` / `payment_proof_removed` como hoy». Historial `Edited` (`ItemAdded` / `ItemQuantityChanged` / `ItemRemoved`).
- «`order.RecalculatePaymentStatus(quotation.Total, now)` **una sola vez**.» «Un único `SaveChangesAsync`.»
- «Sin ningún cambio real: responde `200` con el estado actual, sin historial ni auditoría y sin subir `Version`.»
- Preview: «Sin historial, sin auditoría, sin outbox, sin `SaveChangesAsync`.» «Errores de dominio (…) se devuelven como `422` con el mismo código que devolvería el guardado.»
- Rutas: `group.MapPut("/", SaveOrderEditsAsync)` y `group.MapPost("/preview", PreviewOrderEditsAsync)` en el grupo `.../quotations/{quotationId}/order`, ambos con `Produces<OrderDetailResponse>`. «`OrderResponse` suma `version`.» «Handlers registrados a mano en `QepServiceCollectionExtensions.cs`.»

**Del repo y del proceso:**

- Checkout: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`, rama **`develop`**. Cada bloque de comandos empieza con `Set-Location` a esa ruta. **Nunca** se commitea sobre `main`: antes de cada commit, `git branch --show-current` debe decir `develop`; si dice otra cosa, **para y pregunta**.
- TDD estricto: RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff (resumen Superado/Con error/Omitido y el mensaje de cada falla).
- Comandos en **PowerShell**: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`. **Nunca** se pipea `dotnet build` ni `dotnet test`: el pipe enmascara el exit code.
- `Api.exe` corriendo bloquea `build` y `test`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las pruebas de integración exigen Docker (Testcontainers) y la suite completa tarda decenas de minutos: **siempre filtradas**, p. ej. `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"`.
- `Modules.Quotations.Application` no referencia EF Core ni Npgsql (lo verifica `tests/ArchitectureTests`). `AsNoTracking` vive en Infrastructure. La traducción de errores de base vive en Infrastructure (`QuotationsUnitOfWork`), no en Application.
- xUnit v3: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- Commits: Conventional Commits en español. **Sin atribución de IA ni trailer `Co-Authored-By`.** Con rutas explícitas, porque otra sesión puede tener cosas en stage en el mismo checkout:
  `if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add <rutas>; git commit -m "<mensaje>" -- <rutas>`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada. Nunca `git add -A` ni `git add .`.
- **Nunca imprimir el valor de un secreto.** Ninguna tarea necesita leer `user-secrets` ni `.env`; si algo lo pidiera, se cuenta (`Select-String -SimpleMatch "Clave" | Measure-Object`), nunca se muestra.
- Idioma: prosa, comentarios y `<summary>` en español neutro, tuteando; identificadores, códigos de error y mensajes de excepción en inglés.
- **Chequeo de formato** sobre los `.cs` que toca cada tarea (`ENDOFLINE` y `CHARSET` son ruido previo y se filtran):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs')
$report = Join-Path $env:TEMP "qep-editar-pedido-format"
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

## Hallazgos contra el código (2026-09-17)

Verificados en `develop` = `85ff062`.

1. **Los repositorios están separados.** `IQuotationRepository.FindAsync` (`IQuotationRepository.cs:9-10`) y `IOrderRepository.FindByQuotationIdAsync` (`IOrderRepository.cs:29-30`) son interfaces distintas, así que `FindUntrackedAsync` son **dos** métodos. Sus dobles (`StubQuotationRepository`, `StubQuotationListRepository`, `StubOrderListRepository` en `QuotationsTestDoubles.cs:138,305,538`) dejan de compilar al agregarlos: van en la misma tarea.
2. **`Order.Version` sube en cada mutación.** `RemovePaymentProof` (`Order.cs:303`), `RecalculatePaymentStatus` (`Order.cs:272`) y `AddPaymentProofs` (`Order.cs:239`) hacen `Version++` cada uno. Un guardado toca varias piezas, así que la versión nueva **no** es `+1`: el frontend usa la que vuelve (cuerpo o `ETag`). Como `RecalculatePaymentStatus` siempre sube la versión, "sin cambios reales" se detecta **antes** de llamarlo.
3. **`AddPaymentProofs` exige al menos un comprobante** (`Order.cs:208-213`, `order.order.payment_proof_required`) y además pisa `PaymentStatus` con el valor del cliente (`Order.cs:236`). No sirve para el guardado: se agregan piezas nuevas y el método viejo **no se toca** (decisión 8).
4. **`UpdateAmount`/`UpdateFile` son `internal` a Domain** (`OrderPaymentProof.cs:93,113`): la corrección tiene que vivir en `Order`.
5. **El resultado ya existe con otro nombre.** `OrderDetailDto(OrderDto Order, QuotationDto Quotation)` (`GetOrderById.cs:24`) es exactamente el par que pide el spec, y el endpoint lo compone igual que `GetOrderByIdAsync` (`OrderEndpoints.cs:172-187`). Se usa ése, no `OrderItemsAddedResult`.
6. **If-Match ausente responde `428`, no `412`.** `RoleEndpoints.UpdateAsync` (`src/Api/RoleEndpoints.cs:98-103`) lanza `PreconditionRequiredException("precondition.if_match_required", …)`, que `ApiExceptionHandler.cs:141-142` mapea a `428`; `TryParseVersion` (`RoleEndpoints.cs:161-177`) acepta `"3"`, `3` y `W/"3"`, y exige `> 0`. Es `private` y ya está copiado en `TenantSettingsEndpoints.cs:56` y `MembershipEndpoints.cs:95`: `Modules.Quotations.Api` no puede referenciar `Api`, así que lleva su propia copia (tercera).
7. **Los handlers de pedido no tienen pruebas unitarias.** `AddOrderItemsHandler`, `AddOrderPaymentProofsHandler` y `RemoveOrderPaymentProofHandler` se prueban sólo por HTTP (`OrderItemEditApiTests.cs`, `OrderApiTests.cs`). Probar los dos handlers nuevos en unitario exigiría dobles nuevos de `IQuotationFileLookup`, `IOrderPaymentProofEventPublisher`, `IQuotationProductLookup` y `IQuotationProductPricingLookup`. Por eso el handler, su endpoint y su registro viajan juntos en una tarea con prueba de integración RED (tareas 7 y 8). Lo que sí tiene lógica propia testeable en unitario —piezas de `Order`, diferencia de productos, validador— va antes, en tareas propias.
8. **El resolver de archivos consulta la base** (`OrderPaymentProofResolver.cs:47-49`, `IsPaymentProofFileInUseAsync`). En el guardado se resuelve **antes** de mutar, como `AddOrderPaymentProofs.cs:104-122`. Consecuencia conocida: quitar un comprobante y volver a adjuntar su mismo archivo `PaymentProof` en el mismo request responde `order.payment_proof.file_not_available` (la base todavía lo ve adjunto). Se anota; no se resuelve acá.
9. **`OrderPaymentProof.Create` rechaza `Guid.Empty`** (`OrderPaymentProof.cs:72-77`). El preview necesita sumar los montos de los comprobantes nuevos para derivar el estado de pago, así que los agrega en memoria con un `fileId` sintético (`Guid.CreateVersion7()`).
10. **`RefreshCustomerTaxProfile` es no-op sobre una `Converted`** (`Quotation.cs:785`), pero `UpdateQuotationItemHandler` y `RemoveQuotationItemHandler` lo llaman igual, por los pedidos `Pending` cuya cotización quedó en `Draft`/`Sent` (comentario en `Quotation.cs:359-363`). Los dos handlers nuevos lo llaman, una vez.
11. **Los validadores se registran por escaneo** (`QepServiceCollectionExtensions.cs:402`, `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>`), que sólo toma clases públicas no abstractas: la base genérica abstracta no se registra, las dos concretas sí. Los handlers se registran a mano (`QepServiceCollectionExtensions.cs:365-370`).
12. **Altas antes que bajas también evita posiciones repetidas.** `AddItemCore` asigna `_items.Count + 1` (`Quotation.cs:407`); bajar primero y subir después repetiría la posición de la última línea que queda.
13. **`Quotation.Version` también es token de concurrencia** (`QuotationsDbContext.cs:124`): si otra persona tocó la cotización entre la lectura y el guardado, `QuotationsUnitOfWork.cs:33-39` ya lo traduce a `412 concurrency.conflict`.

## Desvíos del spec

1. **Resultado `OrderDetailDto`** en vez de `OrderItemsAddedResult` (hallazgo 5). Mismo contenido.
2. **`428 precondition.if_match_required`** cuando falta o es inválido `If-Match` (hallazgo 6). El `PUT` declara `ProducesProblem(428)` además de `403/404/412/422`.
3. **Preview y comprobantes nuevos:** los montos de `add` **sí** entran en el cálculo (sin ellos el estado de pago del preview estaría mal). En la respuesta del preview esos comprobantes aparecen con `id` y `fileId` sintéticos que no existen: el frontend pinta los suyos del borrador y no debe usar esos ids.
4. **`order.version` del preview es la guardada**, no la que dejaron las mutaciones en memoria: si el frontend la leyera del preview para el `If-Match`, recibiría `412`.
5. **Validador del preview:** el spec nombra sólo `SaveOrderEditsValidator`; el preview tiene `PreviewOrderEditsValidator` con las mismas reglas salvo las de archivos (base compartida `OrderEditsValidator<T>`).
6. **Falla a mitad:** la prueba usa un `removeIds` con un `proofId` ajeno y no un `fileId` inexistente. Los archivos se resuelven antes de mutar (hallazgo 8), así que un `fileId` inexistente falla antes de tocar nada y no prueba atomicidad; un `proofId` ajeno falla **después** de aplicar productos en memoria. El caso `fileId` inexistente se cubre igual, con su propio assert, en la misma prueba.
7. **Notas solas no se auditan.** No existe acción de auditoría para notas: hoy viajan dentro de `quotation.order.payment_proofs_added`. Un guardado que sólo cambia notas escribe el pedido y sube la versión, sin entrada de auditoría. **Pregunta abierta** para el owner: ¿acción nueva `quotation.order.notes_updated`?
8. **Una entrada de `update` cuenta como cambio** aunque traiga el mismo monto y sin archivo: el spec dice que el cliente sólo manda los que cambian.
9. **Una auditoría `quotation.order.payment_proof_removed` por comprobante quitado**, igual que hoy (un `DELETE` por comprobante), y **una** `payment_proofs_added` si hubo altas o correcciones.
10. **Orden de las tareas:** handler + endpoint + registro juntos (tareas 7 y 8), no en tareas separadas como sugería el orden propuesto; motivo en el hallazgo 7.

## Contrato HTTP resultante (para el plan del frontend)

`PUT /api/v1/tenants/{tenantId}/quotations/{quotationId}/order` con `If-Match: "<order.version>"` y `X-Qep-Client: web`.
`POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/preview` con `X-Qep-Client: web`, mismo cuerpo.

```ts
type SaveOrderEditsRequest = {
  items: { productId: string; quantity: number }[]              // lista completa deseada
  proofs?: {
    add?: { fileId?: string | null; amount: number }[]           // fileId obligatorio en PUT, ignorado en preview
    update?: { proofId: string; amount: number; newFileId?: string | null }[]
    removeIds?: string[]
  }
  notes?: string | null                                          // reemplaza; null/ausente la borra
}
// 200 en los dos: OrderDetailResponse = { order: OrderResponse & { version: number }, quotation: QuotationResponse }
// PUT además responde el header ETag: "<version>"
```

| HTTP | `code` | Cuándo |
|---|---|---|
| 200 | — | `PUT`: guardado (o nada que guardar, con la misma `version`). `POST /preview`: documento recalculado, sin persistir |
| 403 | — (política) o `authorization.denied` | Sin `quotations.order.manage`, otro tenant o sin membresía activa |
| 404 | `quotation.quotation.not_found` / `order.order.not_found` | La cotización no existe o no tiene pedido |
| 412 | `concurrency.conflict` | Sólo `PUT`: `If-Match` distinto de `order.version`, o alguien guardó entre la lectura y la escritura |
| 428 | `precondition.if_match_required` | Sólo `PUT`: sin `If-Match` o no numérico |
| 422 | `validation.failed` (con `errors`) | Reglas del validador |
| 422 | código de dominio (sin `errors`) | `order.order.not_pending`, `quotation.item.product_not_found`, `product_inactive`, `product_price_unavailable`, `order.payment_proof.not_found`, `file_not_found`, `file_not_available`, `file_type_not_allowed`, `file_too_large`, `order.order.notes_too_long`, … |

---

## File Structure

**Crear**

| Archivo | Tarea | Responsabilidad |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Application/OrderItemEdits.cs` | 3 | Aplica la lista deseada de productos sobre `Quotation` (altas → cambios → bajas) y devuelve qué cambió |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderEdits.cs` | 5 | `IOrderEdits`, `OrderEditProofs`, `OrderEditProofAddition`, `OrderEditsValidator<T>` |
| `src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs` | 5, 7 | `SaveOrderEditsCommand`, `SaveOrderEditsValidator` (5); `SaveOrderEditsHandler` (7) |
| `src/Modules/Quotations/Modules.Quotations.Application/PreviewOrderEdits.cs` | 5, 8 | `PreviewOrderEditsQuery`, `PreviewOrderEditsValidator` (5); `PreviewOrderEditsHandler` (8) |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemEditsTests.cs` | 3 | Diferencia de productos |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/SaveOrderEditsValidatorTests.cs` | 5 | Validadores de guardado y preview |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs` | 4, 6, 7, 8 | Repositorio sin rastreo, `version`, `PUT`, `POST /preview` |

**Modificar**

| Archivo | Tarea | Qué cambia |
|---|---|---|
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` | 1, 2 | `AttachPaymentProofs`, `CorrectPaymentProofs`, `EnsurePending` (1); `UpdateNotes` (2) |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` | 1, 2 | Pruebas de las piezas |
| `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs` | 4 | `FindUntrackedAsync` |
| `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs` | 4 | `FindUntrackedAsync` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs` | 4 | Implementación `AsNoTracking` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs` | 4 | Implementación `AsNoTracking` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` | 4 | Los tres dobles implementan el método nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs` | 6, 7 | `Version` en `OrderDto`/`OrderResponse` (6); `SaveOrderEditsRequest` y sus partes (7) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs` | 6 | `ToDto` con `Version` |
| `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` | 6, 7, 8 | `ToResponse` con `Version` (6); `PUT /` + `TryParseVersion` + `ToEdits` (7); `POST /preview` (8) |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | 7, 8 | Registro de los dos handlers |
| `docs/integracion-cotizaciones-y-pedidos.md` | 9 | Endpoints y códigos nuevos |

**No se tocan, a propósito:** `Order.AddPaymentProofs`, `AddOrderItems.cs`, `UpdateQuotationItem.cs`, `RemoveQuotationItem.cs`, `AddOrderPaymentProofs.cs`, `RemoveOrderPaymentProof.cs` y sus endpoints (decisión 8); `Quotation.cs` (los métodos `*AfterConversion` ya alcanzan); `QuotationsUnitOfWork.cs` (ya traduce la concurrencia, hallazgo 13); migraciones (no hay columnas nuevas).

## Entrega

| Commit | Tarea |
|---|---|
| `feat(orders): piezas del pedido para sumar y corregir comprobantes sin exigir uno` | 1 |
| `feat(orders): editar las notas del pedido informando si cambiaron` | 2 |
| `feat(orders): aplicar la lista deseada de productos de un pedido` | 3 |
| `feat(orders): leer cotización y pedido sin rastreo` | 4 |
| `feat(orders): contrato y validación de la edición de pedido` | 5 |
| `feat(orders): versión del pedido en la respuesta` | 6 |
| `feat(orders): guardar la edición de un pedido en una sola operación` | 7 |
| `feat(orders): cálculo previo de la edición de un pedido` | 8 |
| `docs(orders): contrato de guardar y previsualizar la edición de pedido` | 9 |

---

### Task 0: Rama, herramientas y línea base

**Files:** ninguno.

**Interfaces:**
- Consumes: nada.
- Produces: la rama comprobada y la evidencia de que el build arranca limpio.

- [ ] **Step 1: Comprobar rama, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git status --short
git fetch origin
git rev-list --left-right --count origin/develop...HEAD
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
```

Esperado: rama `develop`; `git status` vacío (o sólo este plan sin commitear); `rev-list` `0	0` o `0	N`; `Get-Process` sin salida; `docker info` da una versión. Si la rama no es `develop`, **para y pregunta**. Si `origin/develop` avanzó (primer número > 0), re-verifica las líneas de los hallazgos; si alguna cambió, **para y pregunta**.

- [ ] **Step 2: Restore y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`.

---

### Task 1: `Order` suma y corrige comprobantes sin exigir uno

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:304` (después de `RemovePaymentProof`) y `:325` (después de `AddProofs`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs:653` (antes de la llave que cierra la clase)

**Interfaces:**
- Consumes: `OrderPaymentProofInput`, `OrderPaymentProofAmountUpdate`, `OrderPaymentProof.Create/UpdateAmount/UpdateFile` (existentes).
- Produces:
  - `public void Order.AttachPaymentProofs(IReadOnlyCollection<OrderPaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)`
  - `public void Order.CorrectPaymentProofs(IReadOnlyCollection<OrderPaymentProofAmountUpdate> updates, DateTimeOffset occurredAt)`
  - `private void Order.EnsurePending(string message)` — lanza `order.order.not_pending`
  - Colección vacía: no cambia nada, ni `Version`. Ninguno toca `PaymentStatus` ni `Notes`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Agrega al final de `OrderTests.cs`, antes de la llave que cierra la clase:

```csharp

    // Spec 2026-09-17 (editar pedido como borrador): el guardado atómico suma comprobantes sin
    // pisar el estado de pago —lo deriva el servidor una sola vez, decisión 5— ni las notas.
    [Fact]
    public void AttachPaymentProofsAddsTheProofsWithoutTouchingPaymentStatusOrNotes()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PartialPaymentReceived, notes: "Entregar el lunes");
        var fileId = Guid.CreateVersion7();
        var uploadedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.AttachPaymentProofs([new OrderPaymentProofInput(fileId, 30_000m, "payment-proofs/a.pdf")], uploadedBy, later);

        Assert.Equal(2, order.PaymentProofs.Count);
        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(30_000m, added.Amount);
        Assert.Equal(uploadedBy, added.UploadedBy);
        Assert.Equal("payment-proofs/a.pdf", added.PublicStorageKey);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.Equal("Entregar el lunes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // A diferencia de AddPaymentProofs, un guardado que sólo toca productos no trae comprobantes:
    // no es un error y no sube la versión.
    [Fact]
    public void AttachPaymentProofsWithNothingToAttachChangesNothing()
    {
        var order = NewOrder();

        order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1));

        Assert.Single(order.PaymentProofs);
        Assert.Equal(Now, order.UpdatedAt);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void AttachPaymentProofsRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    [Fact]
    public void CorrectPaymentProofsUpdatesTheAmountAndReplacesTheFile()
    {
        var order = NewOrder();
        var proof = Assert.Single(order.PaymentProofs);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        order.CorrectPaymentProofs(
            [new OrderPaymentProofAmountUpdate(proof.Id, 80_000m, newFileId, "payment-proofs/b.pdf")],
            later);

        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal("payment-proofs/b.pdf", proof.PublicStorageKey);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Todos los ids se buscan antes de corregir el primero: uno ajeno no deja la mitad corregida.
    [Fact]
    public void CorrectPaymentProofsRejectsAProofThatIsNotOnThisOrderBeforeCorrectingAny()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        var proof = Assert.Single(order.PaymentProofs);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs(
                [
                    new OrderPaymentProofAmountUpdate(proof.Id, 1m),
                    new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 2m),
                ],
                Now.AddDays(1)));

        Assert.Equal("order.payment_proof.not_found", error.Code);
        Assert.Equal(100_000m, proof.Amount);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void CorrectPaymentProofsWithNothingToCorrectChangesNothing()
    {
        var order = NewOrder();

        order.CorrectPaymentProofs([], Now.AddDays(1));

        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void CorrectPaymentProofsRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"
```

Esperado: FAIL de compilación, `CS1061: 'Order' no contiene una definición para 'AttachPaymentProofs'` y `CS1061: 'Order' no contiene una definición para 'CorrectPaymentProofs'` (o sus equivalentes en inglés).

- [ ] **Step 3: Implementar las piezas**

En `Order.cs`, después del método `RemovePaymentProof` (tras la línea 304):

```csharp

    /// <summary>
    /// Suma comprobantes nuevos sin tocar el estado de pago ni las notas (spec 2026-09-17, editar
    /// pedido como borrador). Pieza de <see cref="AddPaymentProofs"/> para el guardado atómico: un
    /// guardado que sólo cambia productos no trae comprobantes, y exigir «al menos uno»
    /// (<c>order.order.payment_proof_required</c>) lo rechazaría. Sin comprobantes no cambia nada,
    /// ni la versión. El estado de pago lo deriva el caso de uso con
    /// <see cref="RecalculatePaymentStatus"/>, una sola vez (decisión 5).
    /// </summary>
    public void AttachPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofInput> proofs, MemberId uploadedBy, DateTimeOffset occurredAt)
    {
        EnsurePending("Payment proofs can only be added to a pending order.");
        if (proofs.Count == 0)
        {
            return;
        }

        foreach (var proof in proofs)
        {
            _paymentProofs.Add(OrderPaymentProof.Create(
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt));
        }

        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Corrige monto y, si viene, archivo de comprobantes ya cargados (spec 2026-09-17). Misma
    /// corrección que hace <see cref="AddPaymentProofs"/> con <c>updatedProofs</c>, sin exigir
    /// comprobantes nuevos ni pisar el estado de pago. Todos los ids se buscan antes de corregir el
    /// primero: uno ajeno no deja la mitad corregida en memoria. Sin correcciones no cambia nada.
    /// </summary>
    public void CorrectPaymentProofs(
        IReadOnlyCollection<OrderPaymentProofAmountUpdate> updates, DateTimeOffset occurredAt)
    {
        EnsurePending("Payment proofs can only be corrected on a pending order.");
        if (updates.Count == 0)
        {
            return;
        }

        var targets = updates
            .Select(update => (
                Update: update,
                Proof: _paymentProofs.FirstOrDefault(candidate => candidate.Id == update.ProofId)
                    ?? throw new QuotationsDomainException(
                        "order.payment_proof.not_found",
                        $"Payment proof '{update.ProofId}' was not found on this order.")))
            .ToArray();

        foreach (var (update, proof) in targets)
        {
            proof.UpdateAmount(update.Amount);
            if (update.NewFileId is { } newFileId)
            {
                proof.UpdateFile(newFileId, update.NewPublicStorageKey);
            }
        }

        UpdatedAt = occurredAt;
        Version++;
    }
```

Después del método privado `AddProofs` (tras la línea que hoy es 325, antes de `NormalizeOrderNumber`):

```csharp

    // Las piezas nuevas (spec 2026-09-17) comparten el guard. Los métodos que ya existían conservan
    // su chequeo en línea y su mensaje: la decisión 8 deja intactos los endpoints que los usan.
    private void EnsurePending(string message)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException("order.order.not_pending", message);
        }
    }
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @("src/Modules/Quotations/Modules.Quotations.Domain/Order.cs", "tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): piezas del pedido para sumar y corregir comprobantes sin exigir uno" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el `Select-String` sin salida.

---

### Task 2: `Order.UpdateNotes` informa si cambiaron

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (después de `CorrectPaymentProofs`, Task 1)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs` (al final de la clase)

**Interfaces:**
- Consumes (Task 1): `Order.EnsurePending(string)`.
- Produces: `public bool Order.UpdateNotes(string? notes, DateTimeOffset occurredAt)` — `true` si cambió; recorta; `null`/blanco la borra; `order.order.notes_too_long` sobre 500 recortados; `order.order.not_pending` fuera de `Pending`. Sin cambio no sube `Version`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Agrega al final de `OrderTests.cs`, antes de la llave que cierra la clase:

```csharp

    // Spec 2026-09-17: notes reemplaza el campo entero, y el guardado necesita saber si cambió para
    // responder «sin cambios» sin subir la versión.
    [Fact]
    public void UpdateNotesReplacesTheTrimmedNotesAndReportsTheChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");
        var later = Now.AddDays(1);

        var changed = order.UpdateNotes("  Entregar el martes  ", later);

        Assert.True(changed);
        Assert.Equal("Entregar el martes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void UpdateNotesWithTheSameNotesReportsNoChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(" Entregar el lunes ", Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void UpdateNotesClearsThemWithNullOrBlank(string? notes)
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(notes, Now.AddDays(1));

        Assert.True(changed);
        Assert.Null(order.Notes);
    }

    [Fact]
    public void UpdateNotesRejectsNotesLongerThanTheLimit()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes(new string('a', Order.NotesMaxLength + 1), Now.AddDays(1)));

        Assert.Equal("order.order.notes_too_long", error.Code);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void UpdateNotesRejectsAnOrderThatIsNotPending()
    {
        var order = NewOrder();
        order.Approve(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", Now.AddDays(1)));

        Assert.Equal("order.order.not_pending", error.Code);
    }
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"
```

Esperado: FAIL de compilación, `CS1061: 'Order' no contiene una definición para 'UpdateNotes'`.

- [ ] **Step 3: Implementar**

En `Order.cs`, después de `CorrectPaymentProofs`:

```csharp

    /// <summary>
    /// Reemplaza las notas enteras (spec 2026-09-17): <c>null</c> o blanco las borra, mismo criterio
    /// que al crear el pedido. Devuelve si cambiaron: el guardado atómico responde «sin cambios» sin
    /// subir la versión, y comparar acá evita repetir la normalización en el caso de uso.
    /// </summary>
    public bool UpdateNotes(string? notes, DateTimeOffset occurredAt)
    {
        EnsurePending("The notes can only be edited on a pending order.");
        var normalized = NormalizeNotes(notes);
        if (string.Equals(normalized, Notes, StringComparison.Ordinal))
        {
            return false;
        }

        Notes = normalized;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderTests"
```

Esperado: PASS, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @("src/Modules/Quotations/Modules.Quotations.Domain/Order.cs", "tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): editar las notas del pedido informando si cambiaron" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 3: Diferencia de productos (altas → cambios → bajas)

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderItemEdits.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemEditsTests.cs`

**Interfaces:**
- Consumes: `OrderItemAddition(Guid ProductId, decimal Quantity)` (`AddOrderItems.cs:8`), `QuotationProductPricingResolver.ResolveAsync` (`QuotationProductPricingResolver.cs:17-44`), `Quotation.AddItemAfterConversion` / `UpdateItemQuantityAfterConversion` / `RemoveItemAfterConversion` (`Quotation.cs:369,442,464`).
- Produces:
  - `internal enum OrderItemEditKind { Added, QuantityChanged, Removed }`
  - `internal sealed record OrderItemEdit(OrderItemEditKind Kind, Guid ProductId, string? ProductName, decimal? PreviousQuantity, decimal? Quantity)` — `ProductName` es null en las bajas (no se resuelve precio para quitar).
  - `internal static Task<IReadOnlyList<OrderItemEdit>> OrderItemEdits.ApplyAsync(Quotation quotation, IReadOnlyCollection<OrderItemAddition> desired, IQuotationProductPricingLookup pricingLookup, Guid tenantId, MemberId updatedBy, DateTimeOffset occurredAt, CancellationToken cancellationToken)`

- [ ] **Step 1: Escribir las pruebas que fallan**

`OrderItemEditsTests.cs` completo:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-09-17 (editar pedido como borrador): el guardado recibe la lista **completa** de
/// productos deseada y el backend calcula la diferencia contra la cotización, por
/// <c>productId</c>, en el orden altas → cambios → bajas.
/// </summary>
public sealed class OrderItemEditsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId UpdatedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static QuotationProductPricingRef Product(
        Guid productId, string name, decimal unitPriceCop, bool isActive = true) =>
        new(productId, TenantId, name, isActive, unitPriceCop, null, [], null);

    // Convertida por el camino real, con líneas sin impuesto ni descuento: el total es la suma de
    // precio por cantidad y las aserciones no dependen de redondeos.
    private static Quotation ConvertedQuotation(params (Guid ProductId, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), UpdatedBy,
            new DateOnly(2026, 9, 30), null, null, QuotationParties.Empty, BillingAccount,
            false, false, UpdatedBy, Now);
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            quotation.AddItem(QuotationItemId.New(), productId, quantity, unitPrice, 0m, 0, UpdatedBy, Now);
        }

        quotation.ConvertToOrder(UpdatedBy, Now);
        return quotation;
    }

    private static Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Quotation quotation, StubPricingCatalog catalog, params OrderItemAddition[] desired) =>
        OrderItemEdits.ApplyAsync(
            quotation, desired, catalog, TenantId, UpdatedBy, Now.AddDays(1),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AddsProductsThatAreNotInTheQuotationPricedFromTheCatalog()
    {
        var existing = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(added, "Difusor", 50_000m));

        var edits = await ApplyAsync(
            quotation, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(added, 2m));

        var edit = Assert.Single(edits);
        Assert.Equal(new OrderItemEdit(OrderItemEditKind.Added, added, "Difusor", null, 2m), edit);
        Assert.Equal(2, quotation.Items.Count);
        Assert.Equal(200_000m, quotation.Total);
    }

    [Fact]
    public async Task RepricesAProductWhoseQuantityChanged()
    {
        var productId = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((productId, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(productId, 3m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.QuantityChanged, productId, "Vela de soja", 1m, 3m),
            Assert.Single(edits));
        Assert.Equal(3m, Assert.Single(quotation.Items).Quantity);
        Assert.Equal(300_000m, quotation.Total);
    }

    [Fact]
    public async Task RemovesProductsLeftOutOfTheDesiredList()
    {
        var kept = Guid.CreateVersion7();
        var removed = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((kept, 1m, 100_000m), (removed, 2m, 50_000m));
        var catalog = new StubPricingCatalog(Product(kept, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(kept, 1m));

        Assert.Equal(
            new OrderItemEdit(OrderItemEditKind.Removed, removed, null, 2m, null),
            Assert.Single(edits));
        Assert.Equal(kept, Assert.Single(quotation.Items).ProductId);
        Assert.Empty(catalog.Lookups);
    }

    // El orden del spec: bajar primero dispararía quotation.item.last_item_required a mitad de un
    // reemplazo total.
    [Fact]
    public async Task ReplacingEveryProductAddsBeforeRemovingSoTheLastItemRuleDoesNotFire()
    {
        var previous = Guid.CreateVersion7();
        var replacement = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((previous, 1m, 100_000m));
        var catalog = new StubPricingCatalog(Product(replacement, "Difusor", 50_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(replacement, 1m));

        Assert.Equal(
            new[] { OrderItemEditKind.Added, OrderItemEditKind.Removed },
            edits.Select(edit => edit.Kind).ToArray());
        Assert.Equal(replacement, Assert.Single(quotation.Items).ProductId);
        Assert.Equal(50_000m, quotation.Total);
    }

    [Fact]
    public async Task LeavesTheQuotationUntouchedWhenNothingChanges()
    {
        var productId = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((productId, 2m, 100_000m));
        var versionBefore = quotation.Version;
        var catalog = new StubPricingCatalog(Product(productId, "Vela de soja", 100_000m));

        var edits = await ApplyAsync(quotation, catalog, new OrderItemAddition(productId, 2m));

        Assert.Empty(edits);
        Assert.Equal(versionBefore, quotation.Version);
        Assert.Empty(catalog.Lookups);
    }

    [Fact]
    public async Task RejectsAnInactiveNewProductWithTheCodeOfTheOtherEndpoints()
    {
        var existing = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var quotation = ConvertedQuotation((existing, 1m, 100_000m));
        var catalog = new StubPricingCatalog(
            Product(existing, "Vela de soja", 100_000m), Product(inactive, "Difusor", 50_000m, isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApplyAsync(quotation, catalog, new OrderItemAddition(existing, 1m), new OrderItemAddition(inactive, 1m)));

        Assert.Equal("quotation.item.product_inactive", error.Code);
        Assert.Equal(existing, Assert.Single(quotation.Items).ProductId);
    }

    private sealed class StubPricingCatalog(params QuotationProductPricingRef[] products)
        : IQuotationProductPricingLookup
    {
        public List<Guid> Lookups { get; } = [];

        public Task<QuotationProductPricingRef?> FindAsync(
            Guid tenantId, Guid productId, CancellationToken cancellationToken)
        {
            Lookups.Add(productId);
            return Task.FromResult<QuotationProductPricingRef?>(
                products.FirstOrDefault(product => product.Id == productId));
        }

        public Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductPricingRef>>(
                products
                    .Where(product => productIds.Contains(product.Id))
                    .ToDictionary(product => product.Id));
    }
}
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderItemEditsTests"
```

Esperado: FAIL de compilación, `CS0103: El nombre 'OrderItemEdits' no existe en el contexto actual` y `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'OrderItemEdit'`.

- [ ] **Step 3: Implementar**

`OrderItemEdits.cs` completo:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal enum OrderItemEditKind
{
    Added,
    QuantityChanged,
    Removed
}

/// <summary>Un cambio ya aplicado sobre la cotización, para el historial y la auditoría del
/// guardado. <paramref name="ProductName"/> es null en una baja: quitar no resuelve precio, y el
/// nombre lo busca el caso de uso con <c>IQuotationProductLookup</c>, como
/// <c>RemoveQuotationItemHandler</c>.</summary>
internal sealed record OrderItemEdit(
    OrderItemEditKind Kind,
    Guid ProductId,
    string? ProductName,
    decimal? PreviousQuantity,
    decimal? Quantity);

/// <summary>
/// Lleva las líneas de la cotización de un pedido pendiente a la lista **completa** deseada (spec
/// 2026-09-17, decisión 2). La clave es <c>productId</c>: el dominio ya prohíbe dos líneas del mismo
/// producto (<c>quotation.item.duplicate_product</c>).
///
/// El orden es **altas, cambios, bajas**, el inverso de <see cref="BatchUpdateQuotationItemsHandler"/>
/// a propósito: en un reemplazo total, bajar primero dispararía
/// <c>quotation.item.last_item_required</c> a mitad del comando. Altas y bajas nunca comparten
/// <c>productId</c>, así que no se pisan.
///
/// Lo usan el guardado (agregado rastreado) y el cálculo previo (sin rastreo): mismos métodos de
/// dominio para los dos (decisión 4). No escribe historial ni auditoría: eso es sólo del guardado.
/// </summary>
internal static class OrderItemEdits
{
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Quotation quotation,
        IReadOnlyCollection<OrderItemAddition> desired,
        IQuotationProductPricingLookup pricingLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Foto de antes de mutar: las altas agregan líneas a quotation.Items mientras se recorre.
        var current = quotation.Items.ToDictionary(item => item.ProductId);
        var desiredProductIds = desired.Select(line => line.ProductId).ToHashSet();
        var edits = new List<OrderItemEdit>();

        foreach (var line in desired.Where(line => !current.ContainsKey(line.ProductId)))
        {
            // Rechaza inexistente, inactivo o sin precio en la moneda, con los mismos códigos que
            // POST /order/items.
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.AddItemAfterConversion(
                QuotationItemId.New(), line.ProductId, line.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Added, line.ProductId, pricing.Name, null, line.Quantity));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.ProductId, out var item) || item.Quantity == line.Quantity)
            {
                continue;
            }

            // Antes de que UpdateItemQuantityAfterConversion la pise: el historial dice de cuánto a
            // cuánto. La escala y el impuesto se vuelven a resolver, igual que en
            // UpdateQuotationItemHandler.
            var previousQuantity = item.Quantity;
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.UpdateItemQuantityAfterConversion(
                item.Id, line.Quantity, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.QuantityChanged, line.ProductId, pricing.Name, previousQuantity, line.Quantity));
        }

        foreach (var item in current.Values.Where(item => !desiredProductIds.Contains(item.ProductId)).ToArray())
        {
            quotation.RemoveItemAfterConversion(item.Id, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Removed, item.ProductId, null, item.Quantity, null));
        }

        return edits;
    }
}
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderItemEditsTests"
```

Esperado: PASS, 6 superadas, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @("src/Modules/Quotations/Modules.Quotations.Application/OrderItemEdits.cs", "tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderItemEditsTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): aplicar la lista deseada de productos de un pedido" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 4: `FindUntrackedAsync` en los dos repositorios

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs:10` (después de `FindAsync`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs:30` (después de `FindByQuotationIdAsync`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs:22`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs:30`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:152,309,551`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`

**Interfaces:**
- Consumes (Task 2): `Order.UpdateNotes` (la prueba muta el agregado sin rastreo).
- Produces:
  - `Task<Quotation?> IQuotationRepository.FindUntrackedAsync(Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken)` — con `Items` y `Parties`.
  - `Task<Order?> IOrderRepository.FindUntrackedAsync(Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken)` — con `PaymentProofs`.

- [ ] **Step 1: Escribir la prueba que falla**

`OrderEditsApiTests.cs` completo (las tareas 6, 7 y 8 le suman pruebas):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-09-17: editar un pedido como borrador. El guardado atómico
/// (<c>PUT /quotations/{id}/order</c> con <c>If-Match</c>) y el cálculo previo
/// (<c>POST /quotations/{id}/order/preview</c>), contra Postgres real.
/// </summary>
public sealed class OrderEditsApiTests
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // Decisión 4: el cálculo previo muta el agregado en memoria, así que la lectura no puede dejar
    // nada en el change tracker que un SaveChangesAsync del mismo scope llegue a persistir.
    [Fact]
    public async Task FindUntrackedLoadsBothAggregatesWithoutTrackingThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var quotations = scope.ServiceProvider.GetRequiredService<IQuotationRepository>();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var quotationId = new QuotationId(quotation.Id);

        var loadedQuotation = await quotations.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);
        var loadedOrder = await orders.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);

        Assert.NotNull(loadedQuotation);
        Assert.NotNull(loadedOrder);
        Assert.Single(loadedQuotation.Items);
        Assert.Single(loadedOrder.PaymentProofs);
        Assert.Empty(dbContext.ChangeTracker.Entries());

        loadedOrder.UpdateNotes("Borrador que no se guarda", DateTimeOffset.UtcNow);
        Assert.Equal(0, await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        // El filtro de tenant es parte de la consulta, como en FindAsync.
        Assert.Null(await quotations.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
        Assert.Null(await orders.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
    }

    private sealed record ProblemPayload(string Code);
}
```

- [ ] **Step 2: Correr la prueba y ver que falla**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
```

Esperado: FAIL de compilación, `CS1061: 'IQuotationRepository' no contiene una definición para 'FindUntrackedAsync'` y lo mismo para `'IOrderRepository'`. (`ProblemPayload` sin usar no rompe: las tareas siguientes lo usan.)

- [ ] **Step 3: Implementar**

En `IQuotationRepository.cs`, después de `FindAsync` (línea 10):

```csharp

    /// <summary>
    /// Igual que <see cref="FindAsync"/> —con líneas y partes— pero sin rastreo (spec 2026-09-17,
    /// decisión 4): el cálculo previo de «Editar pedido» muta el agregado en memoria, y así nada de
    /// eso puede persistirse aunque algo del mismo scope llame a <c>SaveChangesAsync</c>.
    /// </summary>
    Task<Quotation?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);
```

En `IOrderRepository.cs`, después de `FindByQuotationIdAsync` (línea 30):

```csharp

    /// <summary>Igual que <see cref="FindByQuotationIdAsync"/> —con comprobantes— pero sin rastreo:
    /// lo usa el cálculo previo de «Editar pedido» (spec 2026-09-17, decisión 4).</summary>
    Task<Order?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);
```

En `QuotationRepository.cs`, después de `FindAsync` (tras la línea 22):

```csharp

    // Mismos Include que FindAsync —el cálculo previo pinta las líneas y recalcula con ellas—, sin
    // tracking a propósito (spec 2026-09-17, decisión 4).
    public Task<Quotation?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Quotations
            .AsNoTracking()
            .Include(quotation => quotation.Items)
            .Include(quotation => quotation.Parties)
            .SingleOrDefaultAsync(
                quotation => quotation.TenantId == tenantId && quotation.Id == quotationId,
                cancellationToken);
```

En `OrderRepository.cs`, después de `FindByQuotationIdAsync` (tras la línea 30):

```csharp

    // Con comprobantes: el cálculo previo suma sus montos para derivar el estado de pago.
    public Task<Order?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .AsNoTracking()
            .Include(order => order.PaymentProofs)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.QuotationId == quotationId,
                cancellationToken);
```

En `QuotationsTestDoubles.cs`, `StubQuotationRepository`, después de `FindAsync` (tras la línea 152):

```csharp

    public Task<Quotation?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotation);
```

`StubQuotationListRepository`, después de `FindAsync` (tras la línea 309):

```csharp

    public Task<Quotation?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotations.FirstOrDefault());
```

`StubOrderListRepository`, después de `FindByQuotationIdAsync` (tras la línea 551):

```csharp

    public Task<Order?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Quotation.Id == quotationId)?.Order);
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS en los tres, 0 con error.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @(
    "src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs",
    "src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs",
    "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs",
    "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs",
    "tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): leer cotización y pedido sin rastreo" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 5: Contrato de aplicación y validadores

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrderEdits.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/PreviewOrderEdits.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SaveOrderEditsValidatorTests.cs`

**Interfaces:**
- Consumes: `OrderItemAddition` (`AddOrderItems.cs:8`), `OrderPaymentProofUpdateRequest` (`OrdersDtos.cs:39-40`), `OrderPaymentProofUpdateRequestValidator` (`AddOrderPaymentProofs.cs:66-77`, internal), `OrderDetailDto` (`GetOrderById.cs:24`), `Order.NotesMaxLength`.
- Produces:
  - `public sealed record OrderEditProofAddition(Guid? FileId, decimal Amount);`
  - `public sealed record OrderEditProofs(IReadOnlyList<OrderEditProofAddition> Add, IReadOnlyList<OrderPaymentProofUpdateRequest> Update, IReadOnlyList<Guid> RemoveIds)` con `public static readonly OrderEditProofs None`
  - `public interface IOrderEdits { IReadOnlyList<OrderItemAddition> Items { get; } OrderEditProofs Proofs { get; } string? Notes { get; } }`
  - `public abstract class OrderEditsValidator<TEdits> : AbstractValidator<TEdits> where TEdits : IOrderEdits` con `protected OrderEditsValidator(bool requireFileIds)`
  - `public sealed record SaveOrderEditsCommand(Guid TenantId, Guid QuotationId, long ExpectedVersion, IReadOnlyList<OrderItemAddition> Items, OrderEditProofs Proofs, string? Notes) : ICommand<OrderDetailDto>, IOrderEdits;`
  - `public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>`
  - `public sealed record PreviewOrderEditsQuery(Guid TenantId, Guid QuotationId, IReadOnlyList<OrderItemAddition> Items, OrderEditProofs Proofs, string? Notes) : IQuery<OrderDetailDto>, IOrderEdits;`
  - `public sealed class PreviewOrderEditsValidator : OrderEditsValidator<PreviewOrderEditsQuery>`

- [ ] **Step 1: Escribir las pruebas que fallan**

`SaveOrderEditsValidatorTests.cs` completo:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-09-17, «Validación (<c>SaveOrderEditsValidator</c>)». El preview comparte
/// las reglas salvo las de archivos: todavía no se subieron.</summary>
public sealed class SaveOrderEditsValidatorTests
{
    private static readonly Guid ProductId = Guid.CreateVersion7();

    private readonly SaveOrderEditsValidator _save = new();
    private readonly PreviewOrderEditsValidator _preview = new();

    private static SaveOrderEditsCommand Command(
        IReadOnlyList<OrderItemAddition>? items = null,
        OrderEditProofs? proofs = null,
        string? notes = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            3,
            items ?? [new OrderItemAddition(ProductId, 1m)],
            proofs ?? OrderEditProofs.None,
            notes);

    private static PreviewOrderEditsQuery Query(OrderEditProofs proofs) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), [new OrderItemAddition(ProductId, 1m)], proofs, null);

    private static void AssertFailsOn(FluentValidation.Results.ValidationResult result, string propertyName)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == propertyName);
    }

    [Fact]
    public void AcceptsACompleteEdit()
    {
        var proofs = new OrderEditProofs(
            [new OrderEditProofAddition(Guid.CreateVersion7(), 150_000m)],
            [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 90_000m, Guid.CreateVersion7())],
            [Guid.CreateVersion7()]);

        var result = _save.Validate(Command(proofs: proofs, notes: "Entregar el lunes"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RejectsAnEmptyItemList() =>
        AssertFailsOn(_save.Validate(Command(items: [])), "Items");

    [Fact]
    public void RejectsARepeatedProduct() =>
        AssertFailsOn(
            _save.Validate(Command(items: [new OrderItemAddition(ProductId, 1m), new OrderItemAddition(ProductId, 2m)])),
            "Items");

    [Fact]
    public void RejectsAnEmptyProductId() =>
        AssertFailsOn(_save.Validate(Command(items: [new OrderItemAddition(Guid.Empty, 1m)])), "Items[0].ProductId");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveQuantity(int quantity) =>
        AssertFailsOn(_save.Validate(Command(items: [new OrderItemAddition(ProductId, quantity)])), "Items[0].Quantity");

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RejectsANewProofWithANonPositiveAmount(int amount) =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [new OrderEditProofAddition(Guid.CreateVersion7(), amount)], [], []))),
            "Proofs.Add[0].Amount");

    // «add[].fileId no vacío (sólo en guardado)».
    [Fact]
    public void RequiresTheFileOfANewProofOnlyWhenSaving()
    {
        var proofs = new OrderEditProofs([new OrderEditProofAddition(null, 150_000m)], [], []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs.Add[0].FileId");
        Assert.True(_preview.Validate(Query(proofs)).IsValid);
    }

    [Fact]
    public void RejectsAnEmptyFileIdOfANewProofWhenSaving() =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [new OrderEditProofAddition(Guid.Empty, 150_000m)], [], []))),
            "Proofs.Add[0].FileId");

    [Fact]
    public void RejectsUpdatingTheSameProofTwice()
    {
        var proofId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs(
            [],
            [new OrderPaymentProofUpdateRequest(proofId, 1m), new OrderPaymentProofUpdateRequest(proofId, 2m)],
            []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs.Update");
    }

    [Fact]
    public void RejectsAnEmptyReplacementFile() =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [], [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1m, Guid.Empty)], []))),
            "Proofs.Update[0].NewFileId");

    [Fact]
    public void RejectsUpdatingAndRemovingTheSameProof()
    {
        var proofId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs([], [new OrderPaymentProofUpdateRequest(proofId, 1m)], [proofId]);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs");
        AssertFailsOn(_preview.Validate(Query(proofs)), "Proofs");
    }

    [Fact]
    public void RejectsTheSameFileAsANewProofAndAsAReplacement()
    {
        var fileId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs(
            [new OrderEditProofAddition(fileId, 1m)],
            [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1m, fileId)],
            []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs");
    }

    [Fact]
    public void RejectsNotesLongerThanTheLimit() =>
        AssertFailsOn(_save.Validate(Command(notes: new string('a', Order.NotesMaxLength + 1))), "Notes");
}
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~SaveOrderEditsValidatorTests"
```

Esperado: FAIL de compilación, `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'SaveOrderEditsValidator'` (y `PreviewOrderEditsValidator`, `SaveOrderEditsCommand`, `OrderEditProofs`, …).

- [ ] **Step 3: Implementar**

`OrderEdits.cs` completo:

```csharp
using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Un comprobante nuevo del borrador (spec 2026-09-17). <paramref name="FileId"/> es null
/// en el cálculo previo: los archivos se suben recién al guardar.</summary>
public sealed record OrderEditProofAddition(Guid? FileId, decimal Amount);

/// <summary>Los comprobantes del borrador. <see cref="Update"/> sólo lleva los que cambian; los no
/// mencionados se conservan.</summary>
public sealed record OrderEditProofs(
    IReadOnlyList<OrderEditProofAddition> Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest> Update,
    IReadOnlyList<Guid> RemoveIds)
{
    public static readonly OrderEditProofs None = new([], [], []);
}

/// <summary>El estado deseado completo de «Editar pedido» (spec 2026-09-17, decisión 2): mismo
/// cuerpo para el guardado y el cálculo previo, así que las reglas se escriben una vez.</summary>
public interface IOrderEdits
{
    IReadOnlyList<OrderItemAddition> Items { get; }

    OrderEditProofs Proofs { get; }

    string? Notes { get; }
}

/// <summary>
/// Las reglas de la sección «Validación» del spec 2026-09-17. Abstracta a propósito: el escaneo de
/// validadores del composition root sólo registra las dos concretas. <paramref name="requireFileIds"/>
/// separa el guardado del cálculo previo, que todavía no tiene archivos (el <c>newFileId</c> sí se
/// valida en los dos: si llega, no puede ser <see cref="Guid.Empty"/>).
/// </summary>
public abstract class OrderEditsValidator<TEdits> : AbstractValidator<TEdits>
    where TEdits : IOrderEdits
{
    protected OrderEditsValidator(bool requireFileIds)
    {
        RuleFor(edits => edits.Items)
            .Must(items => items.Count > 0)
            .WithMessage("At least one product is required.");
        RuleForEach(edits => edits.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.ProductId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        RuleFor(edits => edits.Items)
            .Must(items => !HasDuplicates(items.Select(line => line.ProductId)))
            .WithMessage("The same product cannot appear more than once.");

        RuleForEach(edits => edits.Proofs.Add).ChildRules(addition =>
        {
            addition.RuleFor(value => value.Amount).GreaterThan(0m);
            if (requireFileIds)
            {
                addition.RuleFor(value => value.FileId).NotNull().NotEqual(Guid.Empty);
            }
        });
        RuleForEach(edits => edits.Proofs.Update).SetValidator(new OrderPaymentProofUpdateRequestValidator());
        RuleFor(edits => edits.Proofs.Update)
            .Must(updates => !HasDuplicates(updates.Select(update => update.ProofId)))
            .WithMessage("The same payment proof cannot be updated more than once in a request.");
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !proofs.Update.Select(update => update.ProofId).Intersect(proofs.RemoveIds).Any())
            .WithMessage("A payment proof cannot be updated and removed in the same request.");
        // Mismo motivo que AddOrderPaymentProofsValidator (revisión final, I2): un archivo, un adjunto.
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !HasDuplicates(proofs.Add
                .Where(addition => addition.FileId is not null)
                .Select(addition => addition.FileId!.Value)
                .Concat(proofs.Update
                    .Where(update => update.NewFileId is not null)
                    .Select(update => update.NewFileId!.Value))))
            .WithMessage("The same file cannot be attached more than once in a request.");

        RuleFor(edits => edits.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .When(edits => edits.Notes is not null);
    }

    private static bool HasDuplicates<TValue>(IEnumerable<TValue> values)
    {
        var seen = new HashSet<TValue>();
        return values.Any(value => !seen.Add(value));
    }
}
```

`SaveOrderEdits.cs` (la Task 7 le agrega el handler):

```csharp
using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda de una vez todo lo que «Editar pedido» cambió en su borrador —productos, comprobantes y
/// notas— (spec 2026-09-17, decisiones 1 y 2). <paramref name="ExpectedVersion"/> es el
/// <c>If-Match</c>: el <c>Order.Version</c> que la pantalla cargó (decisión 6).
/// </summary>
public sealed record SaveOrderEditsCommand(
    Guid TenantId,
    Guid QuotationId,
    long ExpectedVersion,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes) : ICommand<OrderDetailDto>, IOrderEdits;

public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>
{
    public SaveOrderEditsValidator()
        : base(requireFileIds: true)
    {
    }
}
```

`PreviewOrderEdits.cs` (la Task 8 le agrega el handler):

```csharp
using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El documento que quedaría si se guardara el borrador de «Editar pedido», sin persistir nada (spec
/// 2026-09-17, decisión 3). Mismo cuerpo que <see cref="SaveOrderEditsCommand"/> salvo archivos.
/// </summary>
public sealed record PreviewOrderEditsQuery(
    Guid TenantId,
    Guid QuotationId,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes) : IQuery<OrderDetailDto>, IOrderEdits;

public sealed class PreviewOrderEditsValidator : OrderEditsValidator<PreviewOrderEditsQuery>
{
    public PreviewOrderEditsValidator()
        : base(requireFileIds: false)
    {
    }
}
```

> `SaveOrderEdits.cs` y `PreviewOrderEdits.cs` nacen sólo con `using BuildingBlocks.Application;`. Las tareas 7 y 8 agregan `using FluentValidation;`, `using Modules.Quotations.Domain;` y `using Modules.Tenancy.Application;` al sumar el handler.

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~SaveOrderEditsValidatorTests"
```

Esperado: PASS, 0 con error. Si una prueba falla sólo por el `PropertyName` (p. ej. FluentValidation lo arma distinto de `Proofs.Add[0].FileId`), anota el literal real en el handoff y corrige **la prueba**, no la regla.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @(
    "src/Modules/Quotations/Modules.Quotations.Application/OrderEdits.cs",
    "src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs",
    "src/Modules/Quotations/Modules.Quotations.Application/PreviewOrderEdits.cs",
    "tests/Modules/Quotations/Modules.Quotations.UnitTests/SaveOrderEditsValidatorTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): contrato y validación de la edición de pedido" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 6: `version` en la respuesta del pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:27` (`OrderDto`) y `:102` (`OrderResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs:23`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs:333`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`

**Interfaces:**
- Consumes: `Order.Version`.
- Produces: `long Version` en `OrderDto` y `OrderResponse`, justo después de `UpdatedAt` (JSON `version`). Viaja en toda respuesta de pedido: `GET`, convertir, aprobar, anular, comprobantes y detalle.

- [ ] **Step 1: Escribir la prueba que falla**

En `OrderEditsApiTests.cs`, antes de `private sealed record ProblemPayload`:

```csharp
    // Decisión 6: la versión viaja al frontend para mandarla en If-Match. Aprobar sube la versión
    // exactamente una vez (Order.Approve), así que la segunda lectura es predecible.
    [Fact]
    public async Task TheOrderResponseCarriesTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        Assert.True(order.Version >= 1);

        var approve = await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var body = await approve.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal(order.Version + 1, json.RootElement.GetProperty("version").GetInt64());
        }

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(order.Version + 1, detail.Order.Version);
    }

    /// <summary>Una cotización enviada con un producto de 100.000 COP sin impuesto, convertida con el
    /// pago pendiente y sin comprobantes: el total es 100.000 y el estado de pago lo decide cada
    /// prueba.</summary>
    private static async Task<(QuotationResponse Quotation, OrderResponse Order, Guid ProductId)> CreatePendingOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (quotation, order, productId);
    }
```

- [ ] **Step 2: Correr la prueba y ver que falla**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests.TheOrderResponseCarriesTheVersion"
```

Esperado: FAIL de compilación, `CS1061: 'OrderResponse' no contiene una definición para 'Version'`.

- [ ] **Step 3: Implementar**

En `OrdersDtos.cs`, `OrderDto`, después de `DateTimeOffset UpdatedAt,` (línea 27):

```csharp
    /// <summary>El token de concurrencia del pedido (spec 2026-09-17, decisión 6): el frontend lo
    /// manda en <c>If-Match</c> al guardar la edición. Sube con cada cambio del pedido, no
    /// necesariamente de a uno.</summary>
    long Version,
```

En `OrderResponse`, después de `DateTimeOffset UpdatedAt,` (línea 102):

```csharp
    long Version,
```

En `OrderMapping.cs`, después de `order.UpdatedAt,` (línea 23):

```csharp
        order.Version,
```

En `OrderEndpoints.cs`, `ToResponse`, después de `order.UpdatedAt,` (línea 333):

```csharp
        order.Version,
```

- [ ] **Step 4: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests.TheOrderResponseCarriesTheVersion|FullyQualifiedName~OrderApiTests.CancelAPendingOrderReturnsItCancelledWithWhoWhenAndWhy"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: build `0 Errores` (si algún otro proyecto construía `OrderDto`/`OrderResponse` posicionalmente fallaría acá; hoy sólo lo hacen `OrderMapping.cs:7` y `OrderEndpoints.cs:318`); PASS en las dos corridas.

- [ ] **Step 5: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @(
    "src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs",
    "src/Modules/Quotations/Modules.Quotations.Application/OrderMapping.cs",
    "src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs",
    "tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): versión del pedido en la respuesta" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 7: Guardar la edición en una sola operación (`PUT /order`)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs` (agrega el handler al final)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:82` (request, después de `CancelOrderRequest`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs:117` (mapeo), `:289` (handler HTTP), `:339` (helpers)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:370`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`

**Interfaces:**
- Consumes (Task 1-6): `Order.AttachPaymentProofs`, `Order.CorrectPaymentProofs`, `Order.UpdateNotes`, `OrderItemEdits.ApplyAsync`, `SaveOrderEditsCommand`, `IValidator<SaveOrderEditsCommand>`, `OrderDto.Version`. Existentes: `OrderPaymentProofResolver.ResolveAsync`, `PaymentProofCopies`, `QuotationAdvisorResolver.ResolveAsync`, `QuotationChangeSummary.ItemAdded/ItemQuantityChanged/ItemRemoved`, `PreconditionRequiredException`, `RequestConcurrencyException`.
- Produces:
  - `public sealed class SaveOrderEditsHandler : ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>`
  - `public sealed record OrderEditItemRequest(Guid ProductId, decimal Quantity);`
  - `public sealed record OrderEditProofAddRequest(Guid? FileId, decimal Amount);`
  - `public sealed record OrderEditProofsRequest(IReadOnlyList<OrderEditProofAddRequest>? Add, IReadOnlyList<OrderPaymentProofUpdateRequest>? Update, IReadOnlyList<Guid>? RemoveIds);`
  - `public sealed record SaveOrderEditsRequest(IReadOnlyList<OrderEditItemRequest>? Items, OrderEditProofsRequest? Proofs, string? Notes);`
  - `PUT /api/v1/tenants/{tenantId}/quotations/{quotationId}/order` → 200 `OrderDetailResponse` + `ETag`; 403/404/412/422/428.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `OrderEditsApiTests.cs`, antes de `CreatePendingOrderAsync`:

```csharp
    // Spec 2026-09-17, pruebas: productos + comprobantes + notas en una transacción, versión nueva.
    // Reemplazo total del producto a propósito: bajar antes de subir dispararía last_item_required.
    [Fact]
    public async Task SaveAppliesProductsProofsAndNotesTogetherAndReturnsTheNewVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        var replacementId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(replacementId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, 20_000m)], null, null),
                "Entregar el lunes"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(replacementId, Assert.Single(saved.Quotation.Items).ProductId);
        Assert.Equal(50_000m, saved.Quotation.Total);
        Assert.Equal("Entregar el lunes", saved.Order.Notes);
        Assert.Equal(20_000m, Assert.Single(saved.Order.PaymentProofs).Amount);
        Assert.Equal("PartialPaymentReceived", saved.Order.PaymentStatus);
        Assert.True(saved.Order.Version > order.Version);
        Assert.Equal($"\"{saved.Order.Version}\"", response.Headers.ETag?.Tag);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(replacementId, Assert.Single(fetched.Quotation.Items).ProductId);
        Assert.Equal(saved.Order.Version, fetched.Order.Version);
        Assert.Equal("PartialPaymentReceived", fetched.Order.PaymentStatus);

        var auditActions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();
        Assert.Contains("quotation.order.item_added", auditActions);
        Assert.Contains("quotation.order.item_removed", auditActions);
        Assert.Contains("quotation.order.payment_proofs_added", auditActions);
    }

    // Decisión 5: el cliente ya no manda paymentStatus; comprobantes que cubren el total lo dejan
    // FullPaymentReceived.
    [Fact]
    public async Task SaveDerivesThePaymentStatusOnTheServer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, quotation.Total)], null, null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("FullPaymentReceived", (await ReadDetailAsync(response)).Order.PaymentStatus);
    }

    // Paso 5 del spec: quitar y corregir en el mismo guardado, con el evento detached en el outbox.
    [Fact]
    public async Task SaveRemovesAndCorrectsProofsAndRecalculatesThePaymentStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var keptFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var removedFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null,
                [new OrderPaymentProofRequest(keptFileId, 60_000m), new OrderPaymentProofRequest(removedFileId, 40_000m)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var kept = Assert.Single(order.PaymentProofs, proof => proof.FileId == keptFileId);
        var removed = Assert.Single(order.PaymentProofs, proof => proof.FileId == removedFileId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(null, [new OrderPaymentProofUpdateRequest(kept.Id, 50_000m)], [removed.Id]),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        var remaining = Assert.Single(saved.Order.PaymentProofs);
        Assert.Equal(kept.Id, remaining.Id);
        Assert.Equal(50_000m, remaining.Amount);
        Assert.Equal("PartialPaymentReceived", saved.Order.PaymentStatus);
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.payment-proofs-detached.v1"));
    }

    // Atomicidad: el proofId ajeno falla después de aplicar los productos en memoria, y nada
    // persiste. El fileId inexistente falla antes de mutar, con su propio código.
    [Fact]
    public async Task AFailureHalfwayPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var unknownProof = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest(null, null, [Guid.CreateVersion7()]),
                "No se guarda"));
        var unknownFile = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(Guid.CreateVersion7(), 10_000m)], null, null),
                "No se guarda"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownProof.StatusCode);
        Assert.Equal("order.payment_proof.not_found", (await ReadProblemAsync(unknownProof)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownFile.StatusCode);
        Assert.Equal("order.payment_proof.file_not_found", (await ReadProblemAsync(unknownFile)).Code);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(1m, Assert.Single(fetched.Quotation.Items).Quantity);
        Assert.Null(fetched.Order.Notes);
        Assert.Empty(fetched.Order.PaymentProofs);
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    // Decisión 6: If-Match viejo → 412; sin If-Match → 428, mismo contrato que /roles.
    [Fact]
    public async Task AStaleOrMissingIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, "Primera");
        (await PutEditsAsync(client, tenantId, quotation.Id, order.Version, body)).EnsureSuccessStatusCode();

        var stale = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version, body with { Notes = "Segunda" });
        var missing = await PutEditsAsync(client, tenantId, quotation.Id, null, body with { Notes = "Tercera" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ReadProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ReadProblemAsync(missing)).Code);
        Assert.Equal("Primera", (await GetDetailAsync(client, tenantId, order.Id)).Order.Notes);
    }

    [Fact]
    public async Task AnApprovedOrderIsNotEditable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, _, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var approve = await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", null, TestContext.Current.CancellationToken);
        approve.EnsureSuccessStatusCode();
        var approved = await approve.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(approved);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, approved.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_pending", (await ReadProblemAsync(response)).Code);
    }

    // «Sin ningún cambio real: responde 200 con el estado actual, sin historial ni auditoría y sin
    // subir Version.»
    [Fact]
    public async Task SavingWithoutChangesKeepsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(order.Version, (await ReadDetailAsync(response)).Order.Version);
        Assert.Equal(order.Version, (await GetDetailAsync(client, tenantId, order.Id)).Order.Version);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count);
    }

    private static async Task<HttpResponseMessage> PutEditsAsync(
        HttpClient client, Guid tenantId, Guid quotationId, long? version, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderUrl(tenantId, quotationId))
        {
            Content = JsonContent.Create(body),
        };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    // Misma lectura que OrderApiTests.ActionOf (OrderApiTests.cs:1163-1167): el payload del evento de
    // auditoría lleva la acción en `action`.
    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private static async Task<ProblemPayload> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
```

Esperado: FAIL de compilación, `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'SaveOrderEditsRequest'` (y `OrderEditItemRequest`, `OrderEditProofsRequest`, `OrderEditProofAddRequest`).

- [ ] **Step 3: Agregar el request y ver el RED de comportamiento**

En `OrdersDtos.cs`, después de `CancelOrderRequest` (línea 82):

```csharp

/// <summary>Una línea del estado deseado de «Editar pedido» (spec 2026-09-17).</summary>
public sealed record OrderEditItemRequest(Guid ProductId, decimal Quantity);

/// <summary>Un comprobante nuevo del borrador. <paramref name="FileId"/> se omite en
/// <c>POST /order/preview</c>: los archivos se suben recién al guardar.</summary>
public sealed record OrderEditProofAddRequest(Guid? FileId, decimal Amount);

/// <summary>Los comprobantes del borrador. Ausentes o null equivalen a vacíos.</summary>
public sealed record OrderEditProofsRequest(
    IReadOnlyList<OrderEditProofAddRequest>? Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest>? Update,
    IReadOnlyList<Guid>? RemoveIds);

/// <summary>
/// El estado deseado completo de «Editar pedido» (spec 2026-09-17, decisión 2): mismo cuerpo para
/// <c>PUT /order</c> y <c>POST /order/preview</c>. <c>Items</c> es la lista completa;
/// <c>Notes</c> reemplaza el campo entero y null lo limpia.
/// </summary>
public sealed record SaveOrderEditsRequest(
    IReadOnlyList<OrderEditItemRequest>? Items,
    OrderEditProofsRequest? Proofs,
    string? Notes);
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
```

Esperado: compila; FAIL en las siete pruebas nuevas con `Assert.Equal() Failure` — esperado `OK`/`PreconditionFailed`/`UnprocessableEntity`, recibido `MethodNotAllowed` (el grupo ya tiene `GET`/`POST` en `/`, pero no `PUT`), o `EnsureSuccessStatusCode` fallando con 405. `FindUntrackedLoadsBothAggregatesWithoutTrackingThem` y `TheOrderResponseCarriesTheVersion` siguen en PASS.

- [ ] **Step 4: Implementar el handler**

En `SaveOrderEdits.cs`, agrega debajo de `using BuildingBlocks.Application;` las líneas `using FluentValidation;`, `using Modules.Quotations.Domain;` y `using Modules.Tenancy.Application;` (en ese orden). Al final del archivo:

```csharp

/// <summary>
/// Pasos 1 a 9 del spec 2026-09-17: todo sobre los agregados rastreados y un único
/// <c>SaveChangesAsync</c>, así que una falla en cualquier paso no deja nada a medio guardar.
/// Los endpoints viejos (<c>/order/items</c>, <c>/order/proofs</c>, <c>/items/{id}</c>) no se tocan
/// (decisión 8).
/// </summary>
public sealed class SaveOrderEditsHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationCustomerLookup customerLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SaveOrderEditsCommand> validator)
    : ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        SaveOrderEditsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);
        var order = await orderRepository.FindByQuotationIdAsync(
            command.TenantId, quotation.Id, cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        // Decisión 6: con guardado de estado completo, «el último que guarda gana» pisaría en
        // silencio lo que otra persona cambió después de que esta pantalla cargó.
        if (order.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The order changed after it was loaded.");
        }

        // Mismo motivo que UpdateQuotationItemHandler: retención/excedente al día antes de recalcular.
        var customer = await customerLookup.FindAsync(command.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        // Los archivos se validan contra Storage antes de tocar nada, igual que AddOrderPaymentProofsHandler.
        foreach (var addition in command.Proofs.Add)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, orderRepository, command.TenantId, addition.FileId!.Value, exceptProofId: null,
                cancellationToken);
        }

        foreach (var update in command.Proofs.Update)
        {
            if (update.NewFileId is { } newFileId)
            {
                await OrderPaymentProofResolver.ResolveAsync(
                    fileLookup, orderRepository, command.TenantId, newFileId,
                    new OrderPaymentProofId(update.ProofId), cancellationToken);
            }
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var removedProofIds = command.Proofs.RemoveIds.Select(id => new OrderPaymentProofId(id)).ToArray();
        var replacedProofIds = command.Proofs.Update
            .Where(update => update.NewFileId is not null)
            .Select(update => new OrderPaymentProofId(update.ProofId))
            .ToHashSet();
        // D19 (spec 2026-09-16): archivo y clave de lo que se quita o reemplaza, leídos ANTES de
        // mutar, porque RemovePaymentProof y UpdateFile los pierden.
        var releaseCandidates = order.PaymentProofs
            .Where(proof => removedProofIds.Contains(proof.Id) || replacedProofIds.Contains(proof.Id))
            .Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey))
            .ToArray();

        var copies = new PaymentProofCopies(paymentProofPublisher);
        try
        {
            var itemEdits = await OrderItemEdits.ApplyAsync(
                quotation, command.Items, pricingLookup, command.TenantId, updatedBy, now, cancellationToken);

            foreach (var proofId in removedProofIds)
            {
                order.RemovePaymentProof(proofId, now);
            }

            var corrections = new List<OrderPaymentProofAmountUpdate>(command.Proofs.Update.Count);
            foreach (var update in command.Proofs.Update)
            {
                string? newPublicStorageKey = null;
                if (update.NewFileId is { } newFileId)
                {
                    newPublicStorageKey = await copies.PublishReplacementAsync(
                        command.TenantId, newFileId, cancellationToken);
                }

                corrections.Add(new OrderPaymentProofAmountUpdate(
                    new OrderPaymentProofId(update.ProofId), update.Amount, update.NewFileId, newPublicStorageKey));
            }

            order.CorrectPaymentProofs(corrections, now);

            var attachedInputs = await copies.PublishAsync(
                command.TenantId,
                command.Proofs.Add
                    .Select(addition => new OrderPaymentProofRequest(addition.FileId!.Value, addition.Amount))
                    .ToArray(),
                cancellationToken);
            order.AttachPaymentProofs(attachedInputs, updatedBy, now);

            var notesChanged = order.UpdateNotes(command.Notes, now);
            var proofsChanged = removedProofIds.Length > 0 || corrections.Count > 0 || attachedInputs.Length > 0;

            // Paso 9: sin cambio real no hay historial, auditoría, recálculo ni escritura, y la
            // versión queda igual (RecalculatePaymentStatus siempre la sube).
            if (itemEdits.Count == 0 && !proofsChanged && !notesChanged)
            {
                return new OrderDetailDto(order.ToDto(), quotation.ToDto());
            }

            await RecordItemEditsAsync(command.TenantId, quotation, order, itemEdits, updatedBy, now, cancellationToken);

            for (var index = 0; index < removedProofIds.Length; index++)
            {
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.payment_proof_removed",
                    order.Id.ToString(), "success", now);
            }

            if (corrections.Count > 0 || attachedInputs.Length > 0)
            {
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, "quotation.order.payment_proofs_added",
                    order.Id.ToString(), "success", now);
            }

            var attached = PaymentProofCopies.AttachedFrom(attachedInputs)
                .Concat(PaymentProofCopies.AttachedFromReplacements(corrections))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            var detached = PaymentProofCopies.DetachedFrom(
                releaseCandidates,
                order.PaymentProofs.Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey)));
            if (detached.Length > 0)
            {
                paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
            }

            // Pasos 7 y 8: el estado de pago lo deriva el servidor una sola vez (decisión 5), y todo
            // se escribe junto.
            order.RecalculatePaymentStatus(quotation.Total, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return new OrderDetailDto(order.ToDto(), quotation.ToDto());
    }

    // Historial Edited y auditoría quotation.order.item_* por cada cambio, como los endpoints de hoy.
    // El nombre de una baja se busca en una sola ida al catálogo, igual que BatchUpdateQuotationItemsHandler.
    private async Task RecordItemEditsAsync(
        Guid tenantId,
        Quotation quotation,
        Order order,
        IReadOnlyList<OrderItemEdit> edits,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removedProductIds = edits
            .Where(edit => edit.Kind == OrderItemEditKind.Removed)
            .Select(edit => edit.ProductId)
            .ToArray();
        var removedProducts = removedProductIds.Length == 0
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(tenantId, removedProductIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.Kind == OrderItemEditKind.Added)
            {
                summary = QuotationChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value);
                action = "quotation.order.item_added";
            }
            else if (edit.Kind == OrderItemEditKind.QuantityChanged)
            {
                summary = QuotationChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value);
                action = "quotation.order.item_updated";
            }
            else
            {
                var productName = removedProducts.TryGetValue(edit.ProductId, out var product)
                    ? product.Name
                    : "un producto";
                summary = QuotationChangeSummary.ItemRemoved(productName);
                action = "quotation.order.item_removed";
            }

            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(), quotation.Id, QuotationHistoryEventType.Edited,
                updatedBy, summary, now));
            // Como en las demás acciones quotation.order.*, la entidad auditada es el pedido.
            auditPublisher.Publish(
                tenantId, executionContext.SubjectId, action, order.Id.ToString(), "success", now);
        }
    }
}
```

- [ ] **Step 5: Mapear el endpoint y registrar el handler**

En `OrderEndpoints.cs`, después del mapeo de `/items` (tras la línea 117, antes de `return endpoints;`):

```csharp

        // Guardar «Editar pedido» de una vez (spec 2026-09-17): el estado deseado completo, con
        // If-Match de Order.Version. Mismo contrato de precondición que PATCH /roles: sin If-Match
        // 428, versión vieja 412. Responde el detalle compuesto, igual que GetOrderByIdAsync.
        group.MapPut("/", SaveOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Después de `AddOrderItemsAsync` (tras la línea 289):

```csharp

    private static async Task<IResult> SaveOrderEditsAsync(
        Guid tenantId,
        Guid quotationId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded order version is required.");
        }

        var (items, proofs) = ToEdits(request);
        var detail = await dispatcher.SendAsync(
            new SaveOrderEditsCommand(tenantId, quotationId, expectedVersion, items, proofs, request.Notes),
            cancellationToken);

        httpContext.Response.Headers.ETag = $"\"{detail.Order.Version}\"";
        return Results.Ok(new OrderDetailResponse(
            ToResponse(detail.Order),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }

    // Ausentes o null equivalen a vacíos: así el validador y los handlers nunca ven colecciones null.
    private static (IReadOnlyList<OrderItemAddition> Items, OrderEditProofs Proofs) ToEdits(
        SaveOrderEditsRequest request) =>
        (
            (request.Items ?? [])
                .Select(item => new OrderItemAddition(item.ProductId, item.Quantity))
                .ToArray(),
            new OrderEditProofs(
                (request.Proofs?.Add ?? [])
                    .Select(addition => new OrderEditProofAddition(addition.FileId, addition.Amount))
                    .ToArray(),
                request.Proofs?.Update ?? [],
                request.Proofs?.RemoveIds ?? []));
```

Antes de la llave que cierra la clase (tras `ToResponse`, línea 339 original):

```csharp

    // Copia de RoleEndpoints.TryParseVersion (src/Api): este proyecto no puede referenciar Api, y
    // TenantSettingsEndpoints y MembershipEndpoints ya llevan la suya. Acepta "3", 3 y W/"3".
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
```

En `QepServiceCollectionExtensions.cs`, después del registro de `RemoveOrderPaymentProofHandler` (tras la línea 370):

```csharp
        services.AddScoped<
            ICommandHandler<SaveOrderEditsCommand, OrderDetailDto>,
            SaveOrderEditsHandler>();
```

- [ ] **Step 6: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: build `0 Advertencia(s)`, `0 Errores`; PASS en `OrderEditsApiTests` (9 pruebas) y en `ArchitectureTests`.

- [ ] **Step 7: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @(
    "src/Modules/Quotations/Modules.Quotations.Application/SaveOrderEdits.cs",
    "src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs",
    "src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs",
    "src/Bootstrapper/QepServiceCollectionExtensions.cs",
    "tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): guardar la edición de un pedido en una sola operación" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 8: Cálculo previo (`POST /order/preview`)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/PreviewOrderEdits.cs` (agrega el handler al final)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (mapeo después del `PUT`, handler HTTP después de `SaveOrderEditsAsync`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después del registro de `SaveOrderEditsHandler`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`

**Interfaces:**
- Consumes (Task 1-7): `IQuotationRepository.FindUntrackedAsync`, `IOrderRepository.FindUntrackedAsync`, `OrderItemEdits.ApplyAsync`, piezas de `Order`, `PreviewOrderEditsQuery`, `IValidator<PreviewOrderEditsQuery>`, `ToEdits`, `SaveOrderEditsRequest`.
- Produces:
  - `public sealed class PreviewOrderEditsHandler : IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>`
  - `POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/order/preview` → 200 `OrderDetailResponse` con `order.version` = la guardada; 403/404/422.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `OrderEditsApiTests.cs`, antes de `PutEditsAsync`:

```csharp
    // Spec 2026-09-17, pruebas: el preview devuelve totales recalculados y un GET posterior muestra el
    // pedido sin cambios. Los montos de los comprobantes nuevos entran en el estado de pago (desvío 3).
    [Fact]
    public async Task PreviewReturnsTheRecalculatedDocumentWithoutPersistingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 3m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(null, 300_000m)], null, null),
                "Borrador"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadDetailAsync(response);
        Assert.Equal(3m, Assert.Single(preview.Quotation.Items).Quantity);
        Assert.Equal(300_000m, preview.Quotation.Total);
        Assert.Equal("FullPaymentReceived", preview.Order.PaymentStatus);
        Assert.Equal("Borrador", preview.Order.Notes);
        Assert.Single(preview.Order.PaymentProofs);
        Assert.Equal(order.Version, preview.Order.Version);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(1m, Assert.Single(fetched.Quotation.Items).Quantity);
        Assert.Equal(quotation.Total, fetched.Quotation.Total);
        Assert.Equal("PaymentPending", fetched.Order.PaymentStatus);
        Assert.Null(fetched.Order.Notes);
        Assert.Empty(fetched.Order.PaymentProofs);
        Assert.Equal(order.Version, fetched.Order.Version);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count);
    }

    // «Errores de dominio (…) se devuelven como 422 con el mismo código que devolvería el guardado.»
    [Fact]
    public async Task PreviewReportsTheSameDomainErrorAsSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = new SaveOrderEditsRequest(
            [new OrderEditItemRequest(productId, 1m), new OrderEditItemRequest(Guid.CreateVersion7(), 1m)],
            null,
            null);

        var preview = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/preview", body, TestContext.Current.CancellationToken);
        var save = await PutEditsAsync(client, tenantId, quotation.Id, order.Version, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, save.StatusCode);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(preview)).Code);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(save)).Code);
    }

    [Fact]
    public async Task PreviewOnAnApprovedOrderIsNotPending()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, _, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/preview",
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_pending", (await ReadProblemAsync(response)).Code);
    }
```

- [ ] **Step 2: Correr las pruebas y ver que fallan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests.Preview"
```

Esperado: compila; FAIL en las tres con `Assert.Equal() Failure`, recibido `NotFound` (ninguna ruta atiende `POST /order/preview`). En `PreviewReportsTheSameDomainErrorAsSaving` la primera aserción que falla es la del preview.

- [ ] **Step 3: Implementar el handler**

En `PreviewOrderEdits.cs`, agrega debajo de `using BuildingBlocks.Application;` las líneas `using FluentValidation;`, `using Modules.Quotations.Domain;` y `using Modules.Tenancy.Application;` (en ese orden). Al final del archivo:

```csharp

/// <summary>
/// Aplica en memoria los pasos 4, 5 y 7 del guardado sobre agregados leídos **sin rastreo**
/// (decisión 4): mismos métodos de dominio, así que los errores salen con el mismo código. Sin
/// historial, auditoría, outbox ni <c>SaveChangesAsync</c>.
///
/// Los comprobantes nuevos se agregan con un <c>fileId</c> sintético: el dominio no admite
/// <see cref="Guid.Empty"/> y sus montos tienen que contar para el estado de pago. El reemplazo de
/// archivo se ignora (sólo importan los montos). La versión que vuelve es la guardada: la pantalla
/// nunca debe mandar en <c>If-Match</c> una versión que sólo existió en este cálculo.
/// </summary>
public sealed class PreviewOrderEditsHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewOrderEditsQuery> validator)
    : IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        PreviewOrderEditsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var quotation = await quotationRepository.FindUntrackedAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(query.QuotationId);
        var order = await orderRepository.FindUntrackedAsync(
            query.TenantId, quotation.Id, cancellationToken)
            ?? throw OrderNotFound.For(query.QuotationId);

        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        var storedVersion = order.Version;

        var customer = await customerLookup.FindAsync(query.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var now = clock.UtcNow;

        await OrderItemEdits.ApplyAsync(
            quotation, query.Items, pricingLookup, query.TenantId, updatedBy, now, cancellationToken);

        foreach (var proofId in query.Proofs.RemoveIds)
        {
            order.RemovePaymentProof(new OrderPaymentProofId(proofId), now);
        }

        order.CorrectPaymentProofs(
            query.Proofs.Update
                .Select(update => new OrderPaymentProofAmountUpdate(new OrderPaymentProofId(update.ProofId), update.Amount))
                .ToArray(),
            now);
        order.AttachPaymentProofs(
            query.Proofs.Add
                .Select(addition => new OrderPaymentProofInput(Guid.CreateVersion7(), addition.Amount))
                .ToArray(),
            updatedBy,
            now);
        order.UpdateNotes(query.Notes, now);
        order.RecalculatePaymentStatus(quotation.Total, now);

        return new OrderDetailDto(order.ToDto() with { Version = storedVersion }, quotation.ToDto());
    }
}
```

- [ ] **Step 4: Mapear el endpoint y registrar el handler**

En `OrderEndpoints.cs`, justo después del mapeo del `PUT` (Task 7):

```csharp

        // Cálculo previo (spec 2026-09-17, decisión 3): mismo cuerpo que el PUT, sin archivos y sin
        // persistir. POST y no GET porque lleva el borrador entero en el cuerpo.
        group.MapPost("/preview", PreviewOrderEditsAsync)
            .RequireAuthorization(OrdersPermissions.OrderManage)
            .Accepts<SaveOrderEditsRequest>("application/json")
            .Produces<OrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Después de `SaveOrderEditsAsync`:

```csharp

    private static async Task<IResult> PreviewOrderEditsAsync(
        Guid tenantId,
        Guid quotationId,
        SaveOrderEditsRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var (items, proofs) = ToEdits(request);
        var detail = await dispatcher.QueryAsync(
            new PreviewOrderEditsQuery(tenantId, quotationId, items, proofs, request.Notes),
            cancellationToken);

        return Results.Ok(new OrderDetailResponse(
            ToResponse(detail.Order),
            await composer.ComposeAsync(tenantId, detail.Quotation, cancellationToken)));
    }
```

En `QepServiceCollectionExtensions.cs`, después del registro de `SaveOrderEditsHandler`:

```csharp
        services.AddScoped<
            IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>,
            PreviewOrderEditsHandler>();
```

- [ ] **Step 5: Correr las pruebas y ver que pasan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: build limpio; PASS en `OrderEditsApiTests` (12 pruebas) y `ArchitectureTests`.

- [ ] **Step 6: Chequeo de formato y commit**

Corre **el chequeo de formato**. Después:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @(
    "src/Modules/Quotations/Modules.Quotations.Application/PreviewOrderEdits.cs",
    "src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs",
    "src/Bootstrapper/QepServiceCollectionExtensions.cs",
    "tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "feat(orders): cálculo previo de la edición de un pedido" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 9: Documento de integración y verificación final

**Files:**
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:52-53` (tabla de endpoints) y `:162` (códigos)

**Interfaces:**
- Consumes: el contrato de las tareas 6-8.
- Produces: la guía del frontend al día.

- [ ] **Step 1: Documentar**

En la tabla «Endpoints», después de la fila de `POST /quotations/{id}/order/cancel` (línea 53):

```markdown
| `PUT` | `/quotations/{id}/order` | `SaveOrderEditsRequest` + header `If-Match: "<order.version>"` | 200 `OrderDetailResponse` + `ETag`. Guarda de una vez productos (lista completa), comprobantes (`add`/`update`/`removeIds`) y notas; `paymentStatus` lo deriva el servidor. Sin cambios reales responde 200 con la misma `version`. Sin `If-Match` → 428; versión vieja → 412 |
| `POST` | `/quotations/{id}/order/preview` | `SaveOrderEditsRequest` (sin `fileId` en `add`) | 200 `OrderDetailResponse` recalculado **sin persistir**. `order.version` es la guardada; los comprobantes nuevos vuelven con `id`/`fileId` sintéticos que no se deben usar |
```

En «Códigos de error propios del módulo», después de la fila de `order.order.not_pending` (línea 162):

```markdown
| `concurrency.conflict` | 412 | `PUT /order` con un `If-Match` que ya no es la `version` del pedido: otra persona lo cambió. Recargar |
| `precondition.if_match_required` | 428 | `PUT /order` sin `If-Match` o con un valor que no es un número positivo |
```

- [ ] **Step 2: Verificación final**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderEditsApiTests|FullyQualifiedName~OrderItemEditApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~OrderApiTests"
```

Esperado: todo PASS, 0 con error. `OrderItemEditApiTests` y `OrderApiTests` confirman que los endpoints viejos siguen iguales (decisión 8) con `Order.cs` y `OrderResponse` cambiados. `OrderApiTests` tarda: corre solo.

- [ ] **Step 3: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
$paths = @("docs/integracion-cotizaciones-y-pedidos.md")
if ((git branch --show-current) -ne "develop") { throw "ABORT: rama equivocada" }; git add $paths; git commit -m "docs(orders): contrato de guardar y previsualizar la edición de pedido" -- $paths
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git status --short
```

Esperado: `Select-String` sin salida; `git status` sin cambios de este trabajo.

- [ ] **Step 4: Handoff**

Anota: salida literal de cada RED y GREEN; los `PropertyName` reales si difirieron (Task 5); y como pendientes para el owner el desvío 7 (auditoría de notas) y el hallazgo 8 (quitar y re-adjuntar el mismo archivo en un request).
