# Comprobantes de pago v2: temporal, procesar y mover al bucket público — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un comprobante de pago subido como `PaymentProof` espera en `staging/` (las imágenes ya reducidas a WebP), se copia al bucket público al adjuntarse a un pedido, y un worker de Storage borra el temporal y registra el movimiento; dos barridos limpian lo que nadie adjuntó y los huérfanos de `payment-proofs/`.

**Architecture:** Storage gana un dueño nuevo (`FileOwnerType.PaymentProof`), un puerto de procesamiento de imágenes, un listado por prefijo en `IPublicObjectStorage`, un inbox propio (`storage.inbox_messages`) y cuatro procesadores: el movimiento (consume `quotations.order.payment-proofs-attached.v1`) y, en el mismo worker, el retiro de D19 (consume `quotations.order.payment-proofs-detached.v1`, purga lo que un pedido suelta), el barrido de staging extendido y la reconciliación del bucket público. Quotations escribe esos dos eventos en el outbox de la misma unidad de trabajo que el pedido —el de retiro al reemplazar o quitar un comprobante, en vez del borrado best-effort que trajo `develop`—, acepta WebP, y responde dos sondas nuevas de BuildingBlocks (`IFileReferenceProbe`, `IPublicObjectReferenceProbe`) con el mismo diseño que `IUserReferenceProbe`. En `qep-frontend`, `uploadQuoteFile` manda `ownerType: 'PaymentProof'`.

**Tech Stack:** .NET 10 (SDK de `global.json`), EF Core 10.0.11 + Npgsql 10.0.3, AWSSDK.S3 4.0.100.2 (Cloudflare R2), SixLabors.ImageSharp 3.1.12, xUnit v3 3.2.2, Testcontainers.PostgreSql 4.14.0 (`postgres:18-alpine`, Docker corriendo para las pruebas de integración). Frontend: React 19 + Vitest (`bun run test`).

**Spec:** `docs/superpowers/specs/2026-09-16-comprobantes-publicos-v2-design.md` (y, para lo que v2 conserva, `docs/superpowers/specs/2026-09-15-comprobantes-publicos-design.md`).

## Global Constraints

**Del spec** (valores copiados tal cual; D1–D14 son las filas de su tabla de decisiones y D15–D19 las de su sección «Enmiendas»: D15–D18 en el commit `df294d7` y D19 en `da80432`):

- D1: «Los PDF se mueven tal cual; sólo las imágenes se recomprimen y pasan a WebP.»
- D2: «Aplica sólo a comprobantes de pago, con `FileOwnerType.PaymentProof = 5`.»
- D3: «El comprobante se mueve al público **al adjuntarlo** al pedido.»
- D4: «Entre la subida y el adjunto, el archivo espera en `staging/` y **nunca se promueve** a `files/`.»
- D5: «El endpoint de descarga de Storage devuelve la URL pública cuando el comprobante ya se movió.»
- D6: «La limpieza corre como workers dentro de la API, no como API más `CronJob`.»
- D7: «Imágenes: lado mayor ≤ 2000 px, WebP calidad 80, sin agrandar.»
- D8: «La imagen se procesa **al completar la subida**; al adjuntar sólo se mueve.»
- D9: «Al adjuntar se **copia** antes de guardar el pedido; el temporal se borra **después**, por un evento de outbox que consume Storage.»
- D10: «`FileResource` gana un método propio para registrar el movimiento de un comprobante; `Publish` no se toca.»
- D11: «`StagingCleanupWorker` también borra comprobantes `Available`, sin mover, con más de 24 h y **sin referencia** de ningún módulo.»
- D12: «Un worker de reconciliación recorre sólo `payment-proofs/` del bucket público, salta lo de menos de 24 h, corre una vez al día y arranca en modo solo-registrar.»
- D13: «Los comprobantes `User` (los de v1 y los de la transición) no cambian. Sin migración ni backfill.»
- D14: «Se despliega primero el backend y después el frontend.»
- D15: «`SoftDeleteFileHandler` y `UnpublishFileHandler` rechazan un `PaymentProof` que algún pedido referencia (`IFileReferenceProbe`) con `storage.file.invalid_state`. `PublishFileHandler` rechaza siempre un `PaymentProof`, con el mismo código: un comprobante sólo llega al público por el movimiento de la sección 2.»
- D16: «Un comprobante ya movido no se puede adjuntar a otro pedido. `QuotationFileLookup` lo informa como no disponible (`IsAvailable = false` si es `PaymentProof` y ya tiene `PublicStorageKey`), y `OrderPaymentProofResolver` lo rechaza con el código que ya usa, `order.payment_proof.file_not_available`.»
- D17: «Una migración de Quotations agrega índices sobre `order_payment_proofs.file_id` y `order_payment_proofs.public_storage_key`.»
- D18: «`FileUserReferenceProbe` cuenta también los archivos `PaymentProof` de un usuario, no sólo los `User`.»
- D19: «Reemplazar el archivo de un comprobante (`UpdatedProofs[].NewFileId`) o quitarlo (`RemoveOrderPaymentProof`) **borra el archivo que el pedido deja de usar**, esté en `staging/` o ya movido al bucket público. En la misma transacción que el pedido, Quotations escribe `quotations.order.payment-proofs-detached.v1` con `tenantId`, `orderId` y, por cada archivo soltado, `fileId` y la `publicStorageKey` que tenía ese comprobante (null si no tenía copia); lo escribe también con `Quotations:PaymentProofs:PublicLinks=false`, y el borrado best-effort de la copia vieja después de guardar desaparece. El archivo de reemplazo cuenta como comprobante nuevo: entra en `quotations.order.payment-proofs-attached.v1` con su clave. Storage consume el evento con su inbox, en el mismo worker que el movimiento y después de él. Por cada archivo `PaymentProof` `Available` que ninguna `IFileReferenceProbe` retiene: borra el objeto público si tiene `PublicStorageKey`; si no, el de `staging/` y la copia que trae el evento; después, en un solo `SaveChanges`, `FileResource.PurgeDetachedPaymentProof`, la auditoría `storage.file.purged` con motivo `payment_proof_detached` y el inbox. Uno retenido, ya purgado o en otro estado se salta. De un archivo `User` sólo borra la copia de ese adjunto y audita `storage.public_object.purged` / `payment_proof_detached`.»
- D19, convivencia (del spec): «Si un comprobante se adjunta y se reemplaza enseguida, Storage puede soltarlo antes de moverlo: la purga de un archivo sin mover borra su temporal y la copia que el adjunto alcanzó a hacer. `PaymentProofMoveWorker`, al llegar después, lo salta sin fallar —ya no está `Available`— y marca su mensaje.» «Una copia que quede sin dueño por ese camino es un huérfano de `payment-proofs/`, y la recoge la reconciliación de la sección 4 (D12).» D15 y D16 no cambian: «un archivo purgado no está `Available`, así que adjuntarlo de nuevo responde `order.payment_proof.file_not_available`».
- Consecuencias aceptadas de las enmiendas: «Con `Quotations:PaymentProofs:PublicLinks=false`, un `PaymentProof` adjunto no tiene clave pública, no genera evento y se queda en `staging/`.» «Storage no tiene inbox: la sección 2 necesita una tabla de inbox nueva y su migración».
- `CompleteUpload` con un `PaymentProof` limpio: `image/jpeg`, `image/png`, `image/webp` → «Se redimensiona a lado mayor ≤ 2000 px, sin agrandar, y se codifica WebP calidad 80 con ImageSharp […]. El resultado **reemplaza** al objeto en `staging/`, y el `FileResource` actualiza tipo (`image/webp`), extensión del nombre (`.webp`), tamaño y checksum.» `application/pdf` → «Queda tal cual.» En los dos casos: «sin promoción a `files/`, sin miniatura, y el recurso pasa a `Available` con `MarkClean`».
- «Si la imagen no se puede procesar, cuarentena y `storage.file.rejected` / `image_processing_failed`».
- «`OrderPaymentProofResolver` acepta además `image/webp`.» «Sólo se suma `image/webp → .webp` a la tabla de extensiones.» «El temporal **no** se borra acá.»
- Evento: «en la **misma transacción** se escribe en el outbox `quotations.order.payment-proofs-attached.v1` con `tenantId`, `orderId` y, por cada comprobante nuevo, `fileId` y `publicStorageKey`.»
- Worker de movimiento, en este orden: «**borra el objeto de `staging/`** […] el paso es repetible»; después «**en un solo `SaveChanges`**, llama a `FileResource.MoveToPublic(publicStorageKey, occurredAt)` y marca el inbox». «Si el borrado falla, no se guarda nada y el mensaje vuelve en el tick siguiente.»
- «`MoveToPublic` sólo es válido para `OwnerType == PaymentProof` y `Status == Available`, acepta cualquier tipo del comprobante (PDF incluido), registra `PublicStorageKey` y `PublishedAt`, y es idempotente con la misma clave. Los comprobantes `User` se ignoran.»
- Descarga: «si el recurso es `PaymentProof`, tiene `PublicStorageKey` y no se pidió variante, devuelve `IPublicObjectStorage.GetUrl(PublicStorageKey)`. Todo lo demás firma como hoy. La auditoría `storage.file.downloaded` se registra igual.»
- Barrido: «con el mismo reloj y `StagingRetentionHours`, suma una segunda búsqueda: `PaymentProof`, `Available`, sin `PublicStorageKey` y con `CreatedAt` anterior al corte»; consulta `IFileReferenceProbe`; «si está referenciado, lo deja»; «si no, borra el objeto de `staging/`, marca el recurso como eliminado y audita `storage.file.purged` con motivo `payment_proof_not_attached`».
- Reconciliación: recorre «**sólo** el prefijo `payment-proofs/` del bucket público, paginando»; salta los de menos de `Storage:PaymentProofOrphanCleanup:MinimumAgeHours` (24); referenciado «si lo tiene algún `FileResource.PublicStorageKey` (Storage) o algún `OrderPaymentProof.PublicStorageKey` (Quotations, por `IPublicObjectReferenceProbe`)»; con `DryRun = true` «sólo registra en el log la clave que borraría; con `false` la borra y audita `storage.public_object.purged`»; «corre cada `Storage:PaymentProofOrphanCleanup:IntervalHours` (24)».
- «`DryRun` va en `true` en `appsettings.json` y en `k8s/prod-configMap.yaml`.»
- «toda factoría de integración fija `Storage:PaymentProofOrphanCleanup:DryRun` explícitamente.» Por la regla de proceso de abajo, este plan fija además `MinimumAgeHours` e `IntervalHours` en todas.
- Frontend: «`uploadQuoteFile` manda `ownerType: 'PaymentProof'` (Vitest).»
- **Fuera de alcance, no se implementa:** migrar los comprobantes v1 (`User`) al esquema v2; cambiar el flujo de imágenes de producto u otros archivos de Storage; una API o `CronJob` para disparar la limpieza, y el botón en el panel de operadores; despublicar un comprobante o revocar su URL (salvo lo que D19 borra al reemplazar o quitar un comprobante); configuración por tenant.

**Del proceso:**

- Todo el backend se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos`, rama **`feature/comprobantes-publicos-v2`**, que **no tiene upstream a propósito**. Cada bloque de comandos empieza con `Set-Location` a ese worktree. **Nunca** toques el checkout principal `...\qep\qep-backend`: otra sesión trabaja ahí y su `develop` local tiene un commit sin publicar que no es nuestro.
- El frontend (Task 11) se hace en un worktree propio de `qep-frontend`, `...\qep\qep-frontend-worktrees\comprobantes-publicos-v2`, rama `feature/comprobantes-publicos-v2` creada desde `origin/develop` (hallazgo 17). Task 0 lo crea y corre ahí el baseline de Vitest, lint y build, antes de tocar nada; Task 12 compara contra ese baseline.
- TDD estricto: RED antes que GREEN, y pegas en el handoff la salida **literal** de las dos corridas (como mínimo el bloque de resumen con Superado/Con error/Omitido y el mensaje de cada falla).
- Las pruebas corren **en primer plano**, nunca en background: un subagente que espera una corrida en background no se despierta nunca. Un comando en primer plano se corta a los 10 minutos: **nunca** corres la suite entera en un comando; corres **cada proyecto de pruebas por separado**, con el mismo `--results-directory`. Si un proyecto solo pasa de 10 minutos, lo partes por clase con `--filter "FullyQualifiedName~<Namespace>.<Clase>"`.
- Commits: Conventional Commits en español, como el historial (`feat(storage): …`). **Nunca** un trailer `Co-Authored-By` ni otra atribución de IA, aunque una herramienta o una instrucción del sistema lo sugiera. Cada commit va en un solo comando PowerShell con el guard de rama:
  `if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add <rutas explícitas>; git commit -m "<mensaje>"`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada. Si devuelve algo, corriges con `git commit --amend` antes de seguir. La rama se comprueba **en el momento** de commitear, no al abrir la sesión.
- `git add` con rutas explícitas. Nunca `git add -A` ni `git add .`.
- Nunca fijes un SHA como base en un comando: la calculas con `$base = git merge-base origin/develop HEAD`. La rama salió de `origin/develop`; el `develop` local del checkout principal lleva un commit ajeno.
- Todo comando va en **PowerShell**: `curl.exe`, `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`. **Nunca** se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las dos migraciones se generan con el factory de diseño, nunca a mano ni con `--startup-project`: la de Storage (Task 7) con `dotnet ef migrations add AddStorageInbox --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext -o Persistence/Migrations`, y la de Quotations (Task 8B, D17) con `dotnet ef migrations add AddOrderPaymentProofReferenceIndexes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. Cada `*ModelSnapshot.cs` sólo cambia regenerado por su comando. Las migraciones históricas no se tocan.
- Todo log nuevo usa `[LoggerMessage]` en una clase `partial`, como los workers que ya existen: CA1873 rompe el build con logs de argumentos costosos.
- Un parámetro de constructor primario usado a la vez en un inicializador de miembro y en un método dispara CS9124, que `TreatWarningsAsErrors` convierte en error: en ese caso se copia a un campo.
- Toda factoría de integración que arranca la API fija **cada** clave de configuración nueva explícitamente (los user-secrets de quien corre las pruebas nunca deben llegar a una prueba). Todo miembro nuevo de una interfaz actualiza **todas** sus implementaciones, dobles incluidos, en la misma tarea.
- Las pruebas de xUnit v3 pasan `TestContext.Current.CancellationToken` a toda llamada que acepte un `CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- Idioma: la prosa del plan, los comentarios de código, los `<summary>` y los mensajes de commit van en español de Colombia, tuteando (tienes, puedes, revisa); nunca voseo. Identificadores, claves de configuración, códigos de error y mensajes de excepción quedan en inglés. Los comentarios nuevos explican el porqué y citan la decisión del spec (D1–D19) cuando ayuda.
- Archivos nuevos: UTF-8 **sin BOM** (el `.editorconfig` pide `charset = utf-8` y `end_of_line = lf`).
- **Chequeo de formato**, sobre los `.cs` que toca cada tarea (los de `Migrations/` no, que el `.editorconfig` marca como generados). El repo tiene `core.autocrlf=true` y ningún `.gitattributes`, así que `dotnet format` reporta `ENDOFLINE` en cada archivo y muchos viejos traen además `CHARSET` (BOM): los dos son ruido previo y se filtran. Cualquier otro diagnóstico en una línea que tocaste se corrige; uno en una línea que no tocaste se anota en el handoff y no se arregla en esta rama. El comando es siempre este (lo llamamos **«el chequeo de formato»** en cada tarea):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-v2-format"
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

Esperado: el último bloque sin salida, o sólo con líneas que no tocaste (anótalas en el handoff).

---

## Nombres que fija este plan

El spec llama «nombres propuestos» a los de abajo; éstos son los definitivos y todas las tareas los usan igual.

| Qué | Nombre |
| --- | --- |
| Dueño nuevo | `FileOwnerType.PaymentProof = 5` |
| Reemplazar el contenido procesado | `FileResource.ReplaceContentWithProcessedImage(string mimeType, string extension, string checksum, long sizeBytes, DateTimeOffset occurredAt)` |
| Registrar el movimiento (D10) | `FileResource.MoveToPublic(string publicStorageKey, DateTimeOffset occurredAt)` |
| Purgar uno no adjuntado (D11) | `FileResource.PurgeUnattachedPaymentProof(DateTimeOffset occurredAt)` |
| Puerto de imágenes | `IPaymentProofImageProcessor` + `ProcessedPaymentProofImage` (Storage.Application); `ImageSharpPaymentProofImageProcessor` (Storage.Infrastructure/Imaging) |
| Listado del bucket público | `IPublicObjectStorage.ListAsync(string prefix, string? continuationToken, CancellationToken)` → `PublicObjectPage(IReadOnlyList<PublicStoredObject> Objects, string? ContinuationToken)`, `PublicStoredObject(string Key, DateTimeOffset LastModified)` |
| Evento | `quotations.order.payment-proofs-attached.v1`, payload `{ tenantId, orderId, proofs: [{ fileId, publicStorageKey }] }`: los comprobantes nuevos y los archivos de reemplazo con copia |
| Evento de retiro (D19) | `quotations.order.payment-proofs-detached.v1`, payload `{ tenantId, orderId, proofs: [{ fileId, publicStorageKey \| null }] }` |
| Puerto del evento | `IOrderPaymentProofEventPublisher.PublishAttached(Guid tenantId, OrderId orderId, IReadOnlyCollection<AttachedPaymentProof> proofs, DateTimeOffset occurredAt)`, `AttachedPaymentProof(Guid FileId, string PublicStorageKey)`; desde Task 9D también `PublishDetached(Guid tenantId, OrderId orderId, IReadOnlyCollection<DetachedPaymentProof> proofs, DateTimeOffset occurredAt)`, `DetachedPaymentProof(Guid FileId, string? PublicStorageKey)`; implementación `OrderPaymentProofEventPublisher` |
| Qué entra en cada evento | `PaymentProofCopies.AttachedFrom(IEnumerable<OrderPaymentProofInput>)` y `PaymentProofCopies.AttachedFromReplacements(IEnumerable<OrderPaymentProofAmountUpdate>)` (Task 6); `PaymentProofCopies.DetachedFrom(IEnumerable<DetachedPaymentProof> candidates, IEnumerable<Guid> remainingFileIds)` (Task 9D) — la firma y la regla de esta fila quedaron así sólo hasta la ronda de arreglo de Task 9D; ver spec, enmienda de implementación 5 |
| Purgar uno soltado (D19) | `FileResource.PurgeDetachedPaymentProof(DateTimeOffset occurredAt)` |
| Retiro en Storage (D19) | `IPaymentProofDetachProcessor.ProcessPendingAsync`, `PaymentProofDetachProcessor` (consumidor `storage.payment-proof-detach`, motivo de auditoría `payment_proof_detached`); lo corre `PaymentProofMoveWorker` en el mismo tick, después del movimiento |
| Inbox de Storage | `StorageInboxMessage` en `storage.inbox_messages`, `StorageDbContext.Inbox`, migración `AddStorageInbox` |
| Movimiento | `IPaymentProofMoveProcessor.ProcessPendingAsync`, `PaymentProofMoveProcessor` (consumidor `storage.payment-proof-move`), `PaymentProofMoveWorker` |
| Barrido de staging | `IStagingCleanupProcessor.CleanupAsync`, `StagingCleanupProcessor`; `StagingCleanupWorker` queda como temporizador |
| Sondas | `IFileReferenceProbe`, `IPublicObjectReferenceProbe` (BuildingBlocks.Application); Quotations: `OrderPaymentProofFileReferenceProbe`, `OrderPaymentProofPublicObjectReferenceProbe`; Storage: `FilePublicObjectReferenceProbe` |
| Auditoría de sistema | `IStorageAuditPublisher.PublishSystem(Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt)` |
| Reconciliación | `IPaymentProofOrphanCleanupProcessor.CleanupAsync` → `PaymentProofOrphanCleanupResult(int Listed, int Recent, int Referenced, int Orphans, int Deleted)`, `PaymentProofOrphanCleanupProcessor`, `PaymentProofOrphanCleanupWorker` |
| Opciones | `StorageOptions.PaymentProofOrphanCleanup` (`PaymentProofOrphanCleanupOptions`: `MinimumAgeHours = 24`, `IntervalHours = 24`, `DryRun = true`) |
| Guarda de D15 | `PaymentProofGuard.EnsureNotReferencedAsync(FileResource, IEnumerable<IFileReferenceProbe>, CancellationToken)` y `PaymentProofGuard.EnsureNotPaymentProof(FileResource)` (Storage.Application, `internal static`) |
| Índices de D17 | `IX_order_payment_proofs_file` (`file_id`) e `IX_order_payment_proofs_public_key` (`public_storage_key`); migración `AddOrderPaymentProofReferenceIndexes` |

---

## Hallazgos contra el código (2026-09-16)

Verificados primero en `feature/comprobantes-publicos-v2` sobre `origin/develop` en `03d3758`, con el árbol limpio, y revisados con las enmiendas D15–D18: los hallazgos 13, 15, 16 y 20 quedan **resueltos** por D15, D16, D17 y D18, y las tareas 1B, 7B, 8B y 9B los implementan. El 2026-09-16 la rama se **rebasó** sobre `origin/develop` en `528d368` (spec `a8a5424`, `df294d7` y `da80432`; plan `01a56fe` y su versión rebasada), y cada número de línea de abajo se volvió a verificar contra ese código. El hallazgo 22 queda **resuelto** por D19, y las tareas 6, 9C y 9D lo implementan.

1. **El outbox es una sola tabla de plataforma y no hay mapeo de nombres que aprender.** Cada módulo escribe en `platform.outbox_messages` (propiedad de Tenancy) con su propia proyección de escritura: Quotations mapea `QuotationsOutboxMessage` en `QuotationsDbContext.cs:500-513` y la escriben publicadores de Infrastructure con el nombre del evento como constante (`ExportJobEventPublisher.cs:12-49`, `QuotationAuditPublisher.cs:9-26`). Application no ve el `DbContext`, así que el evento pasa por un puerto (`IOrderPaymentProofEventPublisher`), igual que `IExportEventPublisher`. Un evento sin `IIntegrationEventHandler` no reintenta para siempre: `IntegrationEventDispatcher.cs:25-50` no lanza si nadie coincide y `OutboxProcessor.cs:47-51` marca `processed_at`. Los consumidores leen la tabla por `event_name` y **no** miran `processed_at`.
2. **Storage no tiene inbox.** `StorageDbContext.cs:9-17` mapea sólo `FileResources` y la proyección `Outbox`. El patrón a seguir es Identity: `IdentityInboxMessage.cs:5-12`, `IdentityDbContext.cs` (`ConfigureInbox`, PK `(consumer, message_id)` sobre `identity.inbox_messages`), y el anti-join de `SessionRevocationWorker.cs:62-68` y `OrphanUserCleanupWorker.cs:93-99`, con inbox y efecto en el mismo `SaveChanges` (`OrphanUserCleanupWorker.cs:173-179`) y `ChangeTracker.Clear()` cuando un mensaje falla (`:111-118`). Task 7 suma `storage.inbox_messages` con su migración. Para **leer** el outbox, Storage reusa su proyección `StorageOutboxMessage` (`StorageDbContext.cs:81-95`): EF no deja mapear un segundo tipo a la misma tabla sin table splitting.
3. **Las sondas de referencia se registran en la infraestructura de cada módulo y se consumen enumeradas.** `IUserReferenceProbe` (`BuildingBlocks.Application/IUserReferenceProbe.cs:18-24`) se registra con `AddScoped` en `StorageInfrastructureExtensions.cs:41`, `QuotationsInfrastructureExtensions.cs:51` y `TenancyInfrastructureExtensions.cs:41`, y `OrphanUserCleanupWorker.cs:91` las pide con `GetServices<IUserReferenceProbe>()`. `IFileReferenceProbe` e `IPublicObjectReferenceProbe` copian ese diseño, `Source` incluido.
4. **`IObjectStorage.UploadAsync` no devuelve checksum.** Devuelve `Task` (`IObjectStorage.cs:49-50`); el checksum sale de `StatAsync` → `StoredObject(long SizeBytes, string Checksum)` (`IObjectStorage.cs:33,53`), que en R2 es el ETag sin comillas (`R2ObjectStorage.cs:64-76`) y en los dobles un SHA-256 hex (`StorageFlowTests.cs:284-296`, `QuotationsApiHarness.cs:779-788`). Task 4 sube el WebP y vuelve a leer `StatAsync`.
5. **Dobles de los dos puertos de almacenamiento.** `IObjectStorage`: `StorageFlowTests.cs:255` (privado), `QuotationsApiHarness.cs:760`, `ReportingApiHarness.cs:507`. `IPublicObjectStorage`: `R2PublicObjectStorage.cs:8`, `QuotationsApiHarness.cs:825` y `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:36`. Ninguna factoría de Storage reemplaza `IPublicObjectStorage`. `ListAsync` (Task 3) actualiza esas tres implementaciones.
6. **Hay 38 factorías que arrancan la API**, todas con `builder.UseSetting("Notifications:EmailProvider", "log");` (conteo con `grep -rln`). Task 4 crea la número 39 (`PaymentProofStorageHarness.cs`) con la misma línea, y Task 10 fija las tres claves nuevas (`Storage:PaymentProofOrphanCleanup:DryRun`, `MinimumAgeHours` e `IntervalHours`) debajo de esa línea en las 39.
7. **Los workers no se pueden probar sin extraer su lógica.** `StagingCleanupWorker.cs:44-67` tiene el barrido adentro del `BackgroundService` y su primer tick llega a los 60 min. El precedente es procesador `internal` + worker que sólo temporiza (`QuotationExpirationWorker.cs:11-40`), con `InternalsVisibleTo` para las pruebas de integración (`Modules.Quotations.Infrastructure.csproj:3-8`) y la prueba invocándolo directo (`QuotationExpirationApiTests.cs:136-137`). `Modules.Storage.Infrastructure.csproj:3-5` hoy sólo expone internals a `Modules.Storage.UnitTests`; Task 7 suma `Modules.Storage.IntegrationTests`.
8. **El endpoint de subida ya acepta cualquier nombre definido del enum** (`StorageEndpoints.cs:90-97`), y `owner_type` se persiste por nombre en `varchar(20)` (`StorageDbContext.cs:30-33`): `PaymentProof` mide 12, así que el valor nuevo no pide migración.
9. **Todos los hosted services corren en los hosts de prueba**, salvo el de exportaciones, que el harness de Quotations saca a mano (`QuotationsApiHarness.cs:742-755`). Con `PaymentProofMoveWorker` consultando cada 3 s, el `InMemoryObjectStorage` de Quotations (un `Dictionary`, `:762`) se tocaría desde dos hilos: Task 7 lo pasa a `ConcurrentDictionary`. El harness de Storage saca el worker de movimiento y corre el procesador a mano.
10. **La auditoría de Storage sólo sabe de personas.** `StorageAuditPublisher.cs:21-31` fija `actorType = "Human"` y exige `tenantId`. La proyección de Audit acepta `tenantId` nulo y `actorType = "System"` (`AuditProjectionWorker.cs:92-99`), y el repo usa `Guid.Empty` como actor de sistema (`QuotationExpirationProcessor.cs:26-28`). Task 9 suma `PublishSystem`. `audit.entries.outcome` mide 30 (`AuditDbContext.cs:57`): `payment_proof_not_attached` mide 26.
11. **`ResizeMode.Max` de ImageSharp también agranda**, así que «sin agrandar» (D7) necesita el chequeo explícito del lado mayor. `ImageSharpVariantGenerator.cs:24-67` fija los códigos que se reusan (`storage.image.invalid`, `storage.image.dimensions_too_large`) y el tope de 40 000 000 píxeles de entrada.
12. **AWSSDK.S3 4.0.100.2**: `ListObjectsV2Response.S3Objects` queda en `null` cuando no hay objetos, y `S3Object.LastModified` es `DateTime?` (verificado en `AWSSDK.S3.xml` del paquete). `R2PublicObjectStorage.ListAsync` cubre las dos cosas.
13. **Resuelto por D15 (Task 9B). Borrar o despublicar un comprobante movido borraba su copia pública.** `SoftDeleteFileHandler` (`SoftDeleteFile.cs:35-44`) y `UnpublishFileHandler` (`SetFilePublication.cs:98-111`) borran el objeto de `PublicStorageKey`; con v2 esa clave es la que enlaza el Excel. `PublishFileHandler` (`SetFilePublication.cs:32-43`) valida `FileResource.Publish` antes de copiar, y `Publish` sólo acepta imágenes (`FileResource.cs:248-256`): un comprobante movido en PDF falla ahí con `storage.file.public_image_required`, sin copiar nada, y sólo uno de imagen (WebP) llegaría a `CopyFromPrivateAsync` desde el temporal ya borrado. `README.md:963-964` dice que borrar el archivo no toca su copia, que sólo es cierto para v1. `qep-frontend` no llama a ninguno de los tres endpoints (`git grep` en `origin/develop`). Con D15, los dos primeros rechazan un `PaymentProof` referenciado por un pedido y `PublishFileHandler` rechaza siempre un `PaymentProof`, los tres con `storage.file.invalid_state` (422, `ApiExceptionHandler.cs:145-146`), antes de tocar el bucket; Task 9B corrige también el README.
14. **Con `Quotations:PaymentProofs:PublicLinks` apagada, un `PaymentProof` adjunto se queda en `staging/` mientras el pedido lo use.** `DisabledPaymentProofPublisher.cs:13-14` devuelve `null`, no hay clave, no hay evento de adjunto y la sonda de Quotations lo retiene del barrido. Si el pedido lo suelta, el evento de retiro sale igual y Storage borra el temporal (D19, Tasks 9C y 9D). Producción la tiene encendida (`k8s/prod-configMap.yaml:73`). Es coherente con D4 y el spec lo acepta como consecuencia de las enmiendas; el barrido itera con `Skip` sobre los retenidos para que no tapen a los demás (Task 9).
15. **Resuelto por D16 (Task 7B). Adjuntar a otro pedido un `PaymentProof` ya movido fallaba con 500 en R2**, porque el publicador copia desde `resource.StorageKey` (`PublicPaymentProofPublisher.cs:63-64`) y ese temporal ya no existe (el doble en memoria de las pruebas no falla, así que ahí respondía 200). El resolver corre antes que la copia (`ConvertQuotationToOrder.cs:77`; `AddOrderPaymentProofs.cs:82` para un comprobante nuevo y `:90` para un archivo de reemplazo) y ya rechaza lo no disponible (`OrderPaymentProofResolver.cs:36-41`); con D16, `QuotationFileLookup.FindAsync` (`src/Bootstrapper/QuotationFileLookup.cs:26-38`) informa `IsAvailable = false` para un `PaymentProof` con `PublicStorageKey`. El frontend siempre sube un archivo nuevo por comprobante.
16. **Resuelto por D17 (Task 8B). `order_payment_proofs.file_id` y `public_storage_key` no tenían índice** (`QuotationsDbContext.cs:407-431`, con sólo `IX_order_payment_proofs_order`): las dos sondas de Quotations recorrerían la tabla en cada barrido. Dos pruebas de `QuotationsDbContextMappingTests` fijaban ese estado y Task 8B las actualiza: `OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints` (`:121`, `Assert.Single` sobre los índices del comprobante) y `OrderPaymentProofPublicStorageKeyMapsToANullableColumnWithoutIndex` (`:137-149`).
17. **`qep-frontend` está en `develop`, dos commits por delante de `origin/develop`** (`1dacc54`, `34aa8d2`) y con 260 archivos marcados como modificados que sólo difieren en fin de línea (`git diff --ignore-all-space --ignore-cr-at-eol` vacío). Por eso Task 11 trabaja en un worktree desde `origin/develop`. `uploadQuoteFile` sólo lo usan los dos hooks de comprobantes (`use-convert-quote-to-order.ts:36`, `use-add-order-payment-proofs.ts:31`); su comentario todavía nombra «el PDF de envío», que el backend ya ignora (`QuotationsDtos.cs:199`).
18. **`ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample`** (`ConfigurationExampleTests.cs:31-48`) se pone rojo en cuanto `StorageOptions` bindea las claves nuevas sin que `appsettings.example.json` las traiga. Es el RED de Task 10.
19. **`FileResource` no tiene token de concurrencia.** Si un comprobante de más de 24 h se adjunta justo entre la sonda y el `SaveChanges` del barrido, el barrido lo marca `Purged` y el worker de movimiento lo salta: el Excel sigue funcionando con la copia pública y la descarga desde la app falla. Es la «carrera aceptada» de la sección 3 del spec, un paso más allá.
20. **Resuelto por D18 (Task 1B). `FileUserReferenceProbe` sólo contaba archivos `User`** (`FileUserReferenceProbe.cs:17-20`). Un `PaymentProof` guarda el id del usuario en `OwnerId` (el frontend sube con `ownerId` = el usuario), y sin D18 dejaría de retenerlo en `OrphanUserCleanupWorker`. No hay pruebas directas de la sonda: la cubre `OrphanUserCleanupTests.OwningAFileKeepsTheUser` (`tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:130-149`, que siembra con `SeedFileAsync`, `:367-382`), y Task 1B la extiende con un comprobante.
21. **El nombre de un archivo mide hasta 260** (`StorageDbContext.cs:34`, `FileUploadPolicy.cs:33-38`). Pasar de `.png` a `.webp` puede superar el tope por uno: `ReplaceContentWithProcessedImage` recorta la base, nunca la extensión.
22. **Resuelto por D19 (Tasks 6, 9C y 9D; conflicto C1 del pre-flight). `develop` agregó reemplazar y quitar comprobantes** (`1439897`, en la base desde el rebase). `AddOrderPaymentProofsHandler` (`AddOrderPaymentProofs.cs:56-178`) resuelve cada `UpdatedProofs[].NewFileId` (`:86-93`), captura la clave vieja antes de mutar (`:111-118`), publica el reemplazo con `PaymentProofCopies.PublishReplacementAsync` (`:125-138`, `PaymentProofCopies.cs:46-56`) y, después de guardar, borra la clave vieja best-effort (`:163-174`). `Order.AddPaymentProofs` llama a `OrderPaymentProof.UpdateFile(fileId, publicStorageKey)` (`Order.cs:182-185`, `OrderPaymentProof.cs:113-124`), que pisa `FileId` y `PublicStorageKey`. `RemoveOrderPaymentProofHandler` (`RemoveOrderPaymentProof.cs:16-58`) llama a `Order.RemovePaymentProof` (`Order.cs:246-263`) y no borra nada; el mapeo en cascada de `order_payment_proofs` (`QuotationsDbContext.cs:435-438`) borra la fila. Sin D19, en v2 el archivo de reemplazo no entraba en el evento de adjuntos (nunca se movía) y el borrado best-effort de Quotations dejaba al `FileResource` movido apuntando a un objeto borrado. Ninguna prueba construye estos handlers a mano (`git grep "new AddOrderPaymentProofsHandler\|new RemoveOrderPaymentProofHandler" -- tests` vacío): se registran por tipo en `QepServiceCollectionExtensions.cs:365-367`. Las pruebas de `develop` que los ejercen son `OrderApiTests.AddOrderPaymentProofsReplacesTheFileOfAnExistingProof`, `RemoveOrderPaymentProofRemovesAnExistingProof`, `RemoveOrderPaymentProofRejectsAnUnknownProof` y `RemoveOrderPaymentProofOnAnApprovedOrderIsRejected` (`OrderApiTests.cs:834-956`), todas con `publicPaymentProofLinks` apagado y archivos `User`: D19 no cambia lo que afirman.

**Decisiones de este plan donde el spec deja margen:**

- **`occurredAt` de `MoveToPublic` es el `occurred_at` del mensaje**, es decir, cuando se guardó el pedido: desde ahí el comprobante es público (la copia ya existía). Reintentar el mensaje no mueve la fecha.
- **El worker de movimiento salta, sin fallar, un `fileId` que no es `PaymentProof`, que no es del tenant del evento, que no está `Available` o que ya tiene clave pública**, y marca el inbox igual. Un mensaje que no puede aplicarse nunca no debe reintentarse cada 3 s para siempre. La misma regla cubre la carrera de D19: un comprobante que su pedido soltó y Storage purgó antes de moverlo ya no está `Available` (Task 9C lo prueba).
- **La auditoría de los barridos va por `PublishSystem`**, con `actorType = "System"` y `actorId = Guid.Empty` (hallazgo 10). La purga de un objeto huérfano va con `tenantId = null`, `resourceType = "public_object"`, `resourceId` = la clave y `outcome = "success"`; la de un comprobante no adjuntado, con `resourceType = "file"` y `outcome = "payment_proof_not_attached"`.
- **Storage registra su propia `IPublicObjectReferenceProbe`** para `FileResource.PublicStorageKey`, así la reconciliación sólo recorre sondas y no mezcla una consulta propia con las de otros módulos.
- **`IntervalHours` va de 1 a 1193.** `PeriodicTimer` no acepta períodos de más de `uint.MaxValue - 1` ms (≈ 1193 h) y con uno mayor el worker tumbaría el host. `MinimumAgeHours` sólo tiene que ser mayor que cero.
- **La reconciliación espera un intervalo completo antes de su primera corrida** (`while (await timer.WaitForNextTickAsync(...))`, como `StagingCleanupWorker`) y no hace nada si el bucket público no está configurado. Así un reinicio de pod no dispara un recorrido del bucket, y ningún host de prueba sale a R2.
- **La reconciliación audita y guarda después de cada borrado**, no al final: si el proceso muere a mitad del recorrido, lo ya borrado queda auditado.
- **El procesador de imágenes descarta EXIF, ICC y XMP**, como el de miniaturas: una foto de celular trae GPS y el comprobante termina en un bucket público (D3).
- **Tasks 1, 2 y 3 tienen RED de compilación**: agregan API nueva (métodos de dominio, una clase y un miembro de interfaz) y no hay comportamiento previo que contradecir. Task 2 suma además un RED de aserción para el descarte de EXIF, ICC y XMP: la implementación entra primero sin esas tres líneas. Task 1B y desde Task 4 cada tarea tiene al menos un RED de aserción.
- **Las enmiendas van en tareas propias con sufijo B** (1B, 7B, 8B y 9B), justo después de la tarea de la que dependen, para no renumerar las referencias cruzadas entre tareas. D18 (1B) necesita `FileOwnerType.PaymentProof` (Task 1); D16 (7B), el movimiento real (Task 7); D17 (8B) va antes de Task 9 para que las dos sondas de Quotations (Tasks 9 y 10) nazcan con su índice; D15 (9B) necesita `IFileReferenceProbe` (Task 9).
- **D15 se ejerce por los handlers, no por el dominio.** La referencia la sabe Quotations, y `FileResource` no puede preguntar a una sonda: la guarda vive en `Modules.Storage.Application` (`PaymentProofGuard`) y `SoftDeleteFileHandler` y `UnpublishFileHandler` reciben `IEnumerable<IFileReferenceProbe>`, igual que `StagingCleanupProcessor`. Un `PaymentProof` referenciado se rechaza esté movido o no: el spec no distingue.
- **D19 va en dos tareas con sufijo C y D, después de 9B**, para no renumerar: Task 9C (Storage) necesita el inbox de Task 7 e `IFileReferenceProbe` y `PublishSystem` de Task 9; Task 9D (Quotations) escribe el evento que 9C consume. Storage va primero para que cada commit quede coherente: hasta 9D nadie escribe el evento y el borrado best-effort de `develop` sigue en su lugar; 9D lo quita en el mismo commit en que empieza a publicar. El archivo de reemplazo entra en el evento de adjuntos desde Task 6, porque es el mismo evento.
- **El retiro corre en `PaymentProofMoveWorker`, en el mismo tick y después del movimiento, cada procesador en su scope.** Un worker aparte podría tocar el mismo `FileResource` a la vez que el movimiento en la misma réplica (hallazgo 19: `FileResource` no tiene token de concurrencia), y el harness de Storage ya saca ese worker del host. La carrera que queda —el retiro antes del movimiento, por un tick fallido o por otra réplica— la cubren las dos reglas de D19: la purga de uno sin mover borra el temporal y la copia del evento, y el movimiento ya salta lo que no está `Available` (Task 7).
- **La fecha de la purga de D19 es `clock.UtcNow`**, como el barrido, no el `occurred_at` del mensaje: registra cuándo se borró el objeto, no cuándo se soltó.
- **`PurgeDetachedPaymentProof` conserva `PublicStorageKey`** como registro de dónde estuvo el comprobante. El objeto ya está borrado cuando se guarda, un recurso `Purged` no se descarga (`EnsureDownloadable`) ni se adjunta (D16), y `FilePublicObjectReferenceProbe` (Task 10) lo cuenta como referenciado sin efecto: la reconciliación no lista un objeto que ya no existe.
- **Quotations sólo suelta los archivos que el pedido deja de usar** (`PaymentProofCopies.DetachedFrom`, contra los `FileId` que quedan después de mutar): si otro comprobante del mismo pedido usa el mismo archivo, no hay evento para ese archivo. Los de otros pedidos los retiene la sonda, en Storage. Esta regla por `FileId` quedó reemplazada en la ronda de arreglo de Task 9D por una regla por `PublicStorageKey` de cada adjunto; ver spec, enmienda de implementación 5.
- **Un archivo `User` soltado sólo pierde la copia de ese adjunto**, sin preguntar a las sondas: `PublicPaymentProofPublisher` genera una clave aleatoria por adjunto (`PublicPaymentProofPublisher.cs:63`), así que ningún otro comprobante la usa, y el original privado no se toca (D13). Si un `PaymentProof` retenido, o ya movido con otra clave, deja una copia sin dueño, es un huérfano para la reconciliación (D12).
- **Task 9C tiene RED de aserción en dos fases**, como Task 2: el procesador entra primero sin las líneas que borran la copia de un comprobante soltado antes de moverse, y la prueba de la carrera lo ve.

## Entrega

| Commit | Tarea |
| --- | --- |
| Ninguno: el spec (`a8a5424`, `df294d7`, `da80432`) y el plan (`01a56fe`, `docs(storage): plan v2 rebasado sobre develop y con D19`) ya están commiteados | 0 |
| `feat(storage): comprobantes de pago como dueño propio en el dominio` | 1 |
| `feat(storage): los comprobantes de pago retienen a quien los subió` | 1B |
| `feat(storage): procesar imágenes de comprobantes a WebP` | 2 |
| `feat(storage): listar el bucket público por prefijo` | 3 |
| `feat(storage): dejar los comprobantes procesados en staging al completar la subida` | 4 |
| `feat(orders): aceptar comprobantes de pago en WebP` | 5 |
| `feat(orders): evento de comprobantes adjuntados en el outbox` | 6 |
| `feat(storage): mover los comprobantes adjuntados al bucket público` | 7 |
| `feat(orders): un comprobante ya movido no se adjunta a otro pedido` | 7B |
| `feat(storage): URL pública para descargar un comprobante movido` | 8 |
| `feat(orders): índices de las sondas de comprobantes` | 8B |
| `feat(storage): purgar los comprobantes que nadie adjuntó` | 9 |
| `feat(storage): proteger la copia pública de un comprobante adjunto` | 9B |
| `feat(storage): purgar el comprobante que un pedido suelta` | 9C |
| `feat(orders): soltar el archivo al reemplazar o quitar un comprobante` | 9D |
| `feat(storage): reconciliar los huérfanos de payment-proofs/` | 10 |
| `feat(quotes): subir los comprobantes de pago como PaymentProof` (en `qep-frontend`) | 11 |
| sólo si la verificación final pide cambios | 12 |

Ninguna rama se publica ni se mergea desde este plan. El orden de despliegue es D14: backend primero; la rama del frontend se mergea recién cuando el backend nuevo está en producción.

---

## File Structure

**Crear (backend)**

| Archivo | Tarea | Responsabilidad |
| --- | --- | --- |
| `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs` | 1, 9C | Los métodos de dominio nuevos; la purga de D19 desde 9C |
| `src/Modules/Storage/Modules.Storage.Application/IPaymentProofImageProcessor.cs` | 2 | Puerto y resultado del procesamiento (D7) |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Imaging/ImageSharpPaymentProofImageProcessor.cs` | 2 | ImageSharp: 2000 px, sin agrandar, WebP 80 |
| `tests/Modules/Storage/Modules.Storage.UnitTests/ImageSharpPaymentProofImageProcessorTests.cs` | 2 | Tamaños, calidad y corruptos |
| `tests/Modules/Storage/Modules.Storage.UnitTests/R2PublicObjectStorageTests.cs` | 3 | El listado contra el cliente S3 |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` | 4, 7, 9, 9C, 10 | Factoría, dobles de los dos buckets y helpers |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofUploadTests.cs` | 4 | `CompleteUpload` con `PaymentProof` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPaymentProofResolverTests.cs` | 5 | Tipos aceptados |
| `src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs` | 6, 9D | Puerto de los eventos de adjunto (D9) y de retiro (D19) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs` | 6, 9D | Escribe los dos eventos en el outbox |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageInboxMessage.cs` | 7 | Guarda de idempotencia |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations/<ts>_AddStorageInbox.cs` (+ `.Designer.cs`) | 7 | `storage.inbox_messages` (generada) |
| `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs` | 7 | Borrar el temporal y registrar el movimiento |
| `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs` | 7, 9C | Temporizador de 3 s; desde 9C corre también el retiro, después del movimiento |
| `tests/Modules/Storage/Modules.Storage.UnitTests/StorageDbContextMappingTests.cs` | 7 | Mapeo del inbox y modelo sin migración pendiente |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofMoveTests.cs` | 7 | El procesador de movimiento |
| `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationFileLookupTests.cs` | 7B | Un comprobante movido no está disponible (D16) |
| `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs` | 8, 9, 9B | Dobles de los puertos de Application |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderPaymentProofReferenceIndexes.cs` (+ `.Designer.cs`) | 8B | Los dos índices de D17 (generada) |
| `tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs` | 8 | URL pública sólo para un movido (D5) |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDownloadTests.cs` | 8 | La descarga de punta a punta |
| `src/BuildingBlocks/BuildingBlocks.Application/IFileReferenceProbe.cs` | 9 | Sonda de archivos (D11) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofFileReferenceProbe.cs` | 9 | Quotations: `OrderPaymentProof.FileId` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupProcessor.cs` | 9 | Los dos barridos de staging |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStagingCleanupTests.cs` | 9 | El barrido |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs` | 9, 10 | Las dos sondas de Quotations contra Postgres |
| `src/Modules/Storage/Modules.Storage.Application/PaymentProofGuard.cs` | 9B | La guarda de D15 |
| `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs` | 9B | Borrar, despublicar y publicar un comprobante (D15) |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofFileManagementApiTests.cs` | 9B | `DELETE /files/{id}` de un comprobante movido |
| `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofDetachProcessor.cs` | 9C | Borrar y purgar lo que un pedido suelta (D19) |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDetachTests.cs` | 9C | El procesador de retiro y la carrera con el movimiento |
| `src/BuildingBlocks/BuildingBlocks.Application/IPublicObjectReferenceProbe.cs` | 10 | Sonda de claves públicas (D12) |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FilePublicObjectReferenceProbe.cs` | 10 | Storage: `FileResource.PublicStorageKey` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofPublicObjectReferenceProbe.cs` | 10 | Quotations: `OrderPaymentProof.PublicStorageKey` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupProcessor.cs` | 10 | La reconciliación |
| `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupWorker.cs` | 10 | Temporizador de `IntervalHours` |
| `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofOrphanCleanupTests.cs` | 10 | Corrida real y en seco |

**Modificar (backend)**

| Archivo | Tarea | Qué cambia |
| --- | --- | --- |
| `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs:19-28` | 1 | `PaymentProof = 5` |
| `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs` | 1, 9C | Tres métodos, un helper y la constante `MaxNameLength` (1); `PurgeDetachedPaymentProof` (9C) |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileUserReferenceProbe.cs:1-21` | 1B | Cuenta también los `PaymentProof` (D18) |
| `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:130-149,367-382` | 1B | Un comprobante retiene al usuario |
| `src/Modules/Storage/Modules.Storage.Application/IPublicObjectStorage.cs` | 3 | `ListAsync` y sus records |
| `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2PublicObjectStorage.cs` | 3 | `ListAsync` con `ListObjectsV2` |
| `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:36-60` | 3 | `ListAsync` en el doble |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` | 3, 7, 9D | `ListAsync`; `ConcurrentDictionary`, `ownerType` y helper de imagen; el bucket público en memoria, concurrente |
| `src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs` | 4 | Rama de `PaymentProof` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:37-54` | 4, 7, 9, 9C, 10 | Registros nuevos |
| `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs:13-48` | 5, 7B | `image/webp`; mensaje de no disponible |
| `src/Bootstrapper/QuotationFileLookup.cs:26-38` | 7B | `IsAvailable = false` para un comprobante movido (D16) |
| `src/Bootstrapper/PublicPaymentProofPublisher.cs:8-19,31-39,73-78` | 5, 9B, 9D | `image/webp → .webp` (5); el resumen de la clase con D15 (9B) y D19 (9D) |
| `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs:55-58` | 5 | Fila `.webp` |
| `docs/integracion-cotizaciones-y-pedidos.md:110-128` | 5, 7, 8, 9D | WebP; `PaymentProof` y el movimiento (7); la URL de descarga (8); reemplazar y quitar (9D) |
| `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs:70-77` | 6, 9D | `AttachedFrom` y `AttachedFromReplacements` (6); `DetachedFrom` (9D) |
| `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:42-132` | 6 | Escribe el evento |
| `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs:56-178` | 6, 9D | Evento de adjuntos con los reemplazos (6); evento de retiro y sin el borrado best-effort (9D) |
| `src/Modules/Quotations/Modules.Quotations.Application/RemoveOrderPaymentProof.cs:16-58` | 9D | Evento de retiro (D19) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:43,51` | 6, 9, 10 | Publicador y sondas |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs` | 6, 9D | `AttachedFrom`, `AttachedFromReplacements` y `DetachedFrom` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` | 6, 7, 7B, 9D | Evento; movimiento de punta a punta; adjuntar uno movido; reemplazar y quitar (D19) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:425-431` | 8B | Dos índices (D17) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | 8B | Regenerado por `dotnet ef` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs:117-149` | 8B | Los índices del comprobante |
| `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs` | 9B | Rechaza un comprobante referenciado (D15) |
| `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs` | 9B | Publicar rechaza un comprobante; despublicar, uno referenciado (D15) |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageDbContext.cs:11-17` | 7 | `Inbox` |
| `StorageDbContextModelSnapshot.cs` | 7 | Regenerado por `dotnet ef` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Modules.Storage.Infrastructure.csproj:3-5` | 7 | `InternalsVisibleTo` de integración |
| `src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs` | 8 | URL pública para un movido |
| `src/Modules/Storage/Modules.Storage.Application/IStorageAuditPublisher.cs` | 9 | `PublishSystem` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageAuditPublisher.cs` | 9 | `PublishSystem` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupWorker.cs` | 9 | Sólo temporiza |
| `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptions.cs:17-24` | 10 | `PaymentProofOrphanCleanup` |
| `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptionsValidator.cs:28-31` | 10 | Rangos |
| `tests/Modules/Storage/Modules.Storage.UnitTests/StorageOptionsValidatorTests.cs` | 10 | Rangos y default |
| `src/Api/appsettings.json:22-33`, `src/Api/appsettings.example.json:51-71` | 10 | Las tres claves |
| `k8s/prod-configMap.yaml:53` | 10 | `DryRun: "true"` literal |
| Las 39 factorías (hallazgo 6) | 10 | Fijan `DryRun`, `MinimumAgeHours` e `IntervalHours` |
| `README.md:963-964` | 9B, 9D | Borrar y despublicar un comprobante (D15); reemplazar o quitar borra el archivo (D19, un bullet nuevo debajo) |
| `README.md:123,950,982` | 10 | Claves y la sección de comprobantes |

**Frontend (`qep-frontend`, Task 11)**

| Archivo | Qué cambia |
| --- | --- |
| `src/features/quotes/services/quote-file-upload.ts:1-19,72` | `ownerType: 'PaymentProof'` y el comentario |
| `src/features/quotes/services/quote-file-upload.test.ts:64-79` | La prueba del cuerpo de la sesión |

El worktree y el baseline de Vitest, lint y build se crean en Task 0 (Step 6); la suite completa se vuelve a correr en Task 12 (Step 4).

**No se tocan, a propósito:** `FileResource.Publish` (D10), `FileUploadPolicy` (ya acepta `.webp`), `OrderPaymentProofResponse` y los endpoints de Quotations (D19 cambia los handlers, no las rutas ni las respuestas), las migraciones históricas, `Dockerfile` y `ci.yml` (no hay proyectos nuevos ni paquetes nuevos: ImageSharp llega transitivo a los proyectos de prueba y ningún `packages.lock.json` cambia).

---

### Task 0: Rama, herramientas y baseline

**Files:**
- Ninguno. El spec y este plan ya están commiteados (pre-flight C5): Step 7 sólo lo comprueba.

**Interfaces:**
- Consumes: nada.
- Produces:
  - `$env:TEMP\qep-comprobantes-v2-baseline-failed.txt` con las pruebas de backend que ya fallan, por nombre. Es la referencia de Task 12.
  - El worktree `...\qep\qep-frontend-worktrees\comprobantes-publicos-v2` (rama `feature/comprobantes-publicos-v2` desde `origin/develop`, dependencias instaladas), que usan Tasks 11 y 12.
  - `$env:TEMP\qep-comprobantes-v2-frontend-baseline.json` (reporte JSON de Vitest) y `$env:TEMP\qep-comprobantes-v2-frontend-baseline-failed.txt` con las pruebas de frontend que ya fallan, por nombre, más el exit code de `bun run lint` y de `bun run build` anotado en el handoff. Es la referencia de Task 12, Step 4.

- [ ] **Step 1: Comprobar rama, base, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
git branch --show-current
git status --short
git fetch origin
$base = git merge-base origin/develop HEAD
git log --oneline "$base..HEAD"
git rev-parse --abbrev-ref --symbolic-full-name "@{u}"
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
bun --version
```

Esperado:
- la rama es `feature/comprobantes-publicos-v2`. Si es otra, **para y pregunta**;
- `git status` vacío;
- la base (`git merge-base origin/develop HEAD`) es `528d368` o un commit posterior de `origin/develop` que ya contiene a `1439897`. Si es anterior, la rama no está rebasada: **para y pregunta**;
- el `git log` desde la base trae exactamente cinco commits de documentación, en este orden: `docs(orders): spec de comprobantes de pago v2 (temporal, procesar y mover al público)`, `docs(orders): enmiendas D15-D18 al spec de comprobantes v2`, `docs(storage): plan de implementación de comprobantes públicos v2`, `docs(orders): enmienda D19 al spec de comprobantes v2 (reemplazo y retiro)` y `docs(storage): plan v2 rebasado sobre develop y con D19`. Si aparece otro commit, **para y pregunta**;
- `git rev-parse ... @{u}` falla con `no upstream configured`: es a propósito, no lo corrijas;
- `Get-Process` sin salida; `docker info` devuelve una versión; `dotnet ef` devuelve `10.0.11`; `bun` devuelve una versión.

- [ ] **Step 2: Estado del checkout de `qep-frontend`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git branch --show-current
git fetch origin
git log --oneline origin/develop..develop
git diff --ignore-all-space --ignore-cr-at-eol --stat
git worktree list
```

Esperado (2026-09-16): rama `develop`; el `log` muestra `1dacc54` y `34aa8d2` (sin publicar, no son nuestros); el `diff` que ignora espacios y fines de línea sale vacío; ningún worktree `comprobantes-publicos-v2`. Anota la rama y la salida en el handoff. Si la rama es `main`, **para y pregunta**. Este paso no toca nada: Step 6 crea el worktree propio en el que trabajan Tasks 11 y 12.

- [ ] **Step 3: Restore, build y modelos sin migraciones pendientes**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`, y dos veces `No changes have been made to the model since the last migration.` Si Storage ya tiene cambios pendientes, **para y pregunta**: Task 7 depende de que su snapshot esté al día.

- [ ] **Step 4: Baseline por nombre, un proyecto por comando**

Con Docker corriendo. Corre **cada línea como un comando aparte**, en primer plano, y espera a que termine antes de la siguiente. Los de integración levantan un Postgres por prueba y tardan minutos.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$baseline = Join-Path $env:TEMP "qep-comprobantes-v2-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Audit/Modules.Audit.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Audit/Modules.Audit.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Platform/Modules.Platform.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-baseline")
```

Si una línea se corta a los 10 minutos (lo más probable: `Modules.Quotations.IntegrationTests`), vuelve a correr ese proyecto partido por clase, una clase por comando, con `--filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.<Clase>"`, usando los nombres de archivo de `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/*Tests.cs`.

- [ ] **Step 5: Lista de fallas previas, por nombre**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$baseline = Join-Path $env:TEMP "qep-comprobantes-v2-baseline"
Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-comprobantes-v2-baseline-failed.txt")
(Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse).Count
Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-baseline-failed.txt")
```

Esperado: al menos 26 archivos `.trx` (más si partiste algún proyecto) y la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff.

- [ ] **Step 6: Worktree de `qep-frontend` y su baseline de Vitest, lint y build**

`qep-frontend` no tiene CI de pruebas: sin esta referencia, una falla de la suite en Task 12 no se puede atribuir ni a esta rama ni a `origin/develop`. Los scripts son los del `package.json` de `origin/develop`: `test` es `vitest run` (así que `bun run test` ya equivale al `bun run test --run` del `CLAUDE.md`), `lint` es `oxlint` y `build` es `tsc -b && vite build`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git fetch origin
git worktree add --no-track -b feature/comprobantes-publicos-v2 ..\qep-frontend-worktrees\comprobantes-publicos-v2 origin/develop
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
git branch --show-current
git log --oneline -1
bun install --frozen-lockfile
```

Esperado: la rama es `feature/comprobantes-publicos-v2`; el último commit es el de `origin/develop` (`c890028` el 2026-09-16, o el que haya después del `fetch`); `bun install` sin errores. No toques el checkout principal de `qep-frontend`: tiene commits sin publicar y archivos con fines de línea cambiados que no son nuestros.

La suite entera, en primer plano y sin pipe. El reporter `default` deja la salida de siempre en la consola y el `json` escribe el archivo que se compara por nombre:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
$frontendBaseline = Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline.json"
Remove-Item -Force $frontendBaseline -ErrorAction SilentlyContinue
bun run test --reporter=default --reporter=json "--outputFile.json=$frontendBaseline"
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
bun run lint
"lint exit: $LASTEXITCODE"
bun run build
"build exit: $LASTEXITCODE"
```

La lista de fallas previas, por nombre. Un archivo que ni siquiera carga no trae `assertionResults`: se anota como `SUITE <ruta desde src/>`:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
$report = Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline.json") -Raw | ConvertFrom-Json
@(
    $report.testResults | ForEach-Object { $_.assertionResults } |
        Where-Object { $_.status -eq "failed" } |
        ForEach-Object { $_.fullName }
    $report.testResults |
        Where-Object { $_.status -eq "failed" -and @($_.assertionResults).Count -eq 0 } |
        ForEach-Object { "SUITE " + (($_.name -replace '\\', '/') -replace '^.*?/src/', 'src/') }
) | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline-failed.txt")
"Pruebas: {0}, con error: {1}" -f $report.numTotalTests, $report.numFailedTests
Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline-failed.txt")
```

Esperado: el JSON existe; el total de pruebas es mayor que cero; la lista de fallas previas, posiblemente vacía; y los dos exit codes. Pega todo en el handoff: un lint o un build con exit distinto de `0` en `origin/develop` es deuda previa, y Task 12 sólo exige no empeorarla. Si `bun run test` no escribe el JSON (por ejemplo, porque la versión de Vitest no acepta `--outputFile.json`), **para y pregunta** antes de cambiar el comando.

- [ ] **Step 7: Comprobar que el plan y el spec ya están commiteados**

No hay nada que commitear en esta tarea (pre-flight C5): el spec va en `a8a5424`, `df294d7` y `da80432`, y el plan en `01a56fe` y en su versión rebasada con D19.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
git status --short -- docs/superpowers
$base = git merge-base origin/develop HEAD
git log --format=%B "$base..HEAD" | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: las dos salidas vacías. Si `git status` muestra el plan o el spec modificados, **para y pregunta**: alguien los cambió después de commitearlos.

---

### Task 1: El dueño `PaymentProof` y sus tres métodos de dominio

`FileOwnerType.PaymentProof = 5` (D2), el reemplazo del contenido por la imagen procesada (D7, D8), el movimiento (D10) y la purga de uno no adjuntado (D11). Todavía nadie los llama: eso llega en Tasks 4, 7 y 9. El endpoint de subida acepta `PaymentProof` apenas existe el valor (hallazgo 8); Task 4 lo prueba de punta a punta.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs:25-27`
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs` (la constante `MaxNameLength` antes del constructor, `:13`; los métodos después de `PurgeAbandonedUpload`, `:168-174`, y el helper antes de `RequireStatus`, `:288`)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs` (nuevo)

**Interfaces:**
- Consumes: nada nuevo.
- Produces:
  - `FileOwnerType.PaymentProof = 5`.
  - `public void ReplaceContentWithProcessedImage(string mimeType, string extension, string checksum, long sizeBytes, DateTimeOffset occurredAt)`: exige `PaymentProof` y `PendingScan`; `sizeBytes <= 0` lanza `storage.file.empty`; cambia `MimeType`, la extensión de `Name` (recorta la base para no pasar de 260), `SizeBytes`, `Checksum` y `UpdatedAt`; no toca `StorageKey` ni `Status`. Task 4.
  - `public void MoveToPublic(string publicStorageKey, DateTimeOffset occurredAt)`: exige `PaymentProof` y `Available`; clave vacía → `storage.file.public_key_required`; misma clave → no cambia nada; otra clave con una ya puesta → `storage.file.invalid_state`. Task 7.
  - `public void PurgeUnattachedPaymentProof(DateTimeOffset occurredAt)`: exige `PaymentProof`, `Available` y sin `PublicStorageKey`; deja `Purged` con `DeletedAt`. Task 9.
  - Todos los rechazos de dueño o estado usan el código existente `storage.file.invalid_state`.

- [ ] **Step 1: Escribir las pruebas (RED)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs`:

```csharp
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Los comprobantes de pago v2 en el dominio (spec 2026-09-16): el dueño propio (D2), el reemplazo
/// por la imagen procesada al completar la subida (D7, D8), el movimiento al bucket público (D10) y
/// la purga del que nadie adjuntó (D11).
/// </summary>
public sealed class PaymentProofFileResourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    // D2: el valor se persiste por nombre (StorageDbContext), así que el nombre es contrato.
    [Fact]
    public void PaymentProofIsTheFifthOwnerType()
    {
        Assert.Equal(5, (int)FileOwnerType.PaymentProof);
        Assert.Equal("PaymentProof", FileOwnerType.PaymentProof.ToString());
    }

    // D7: tipo, extensión del nombre, tamaño y checksum; la clave de staging no cambia (D4).
    [Fact]
    public void AProcessedImageReplacesTypeNameSizeAndChecksum()
    {
        var proof = PendingScanProof("comprobante.png", "image/png");

        proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "webp-checksum", 1200, Now.AddMinutes(1));

        Assert.Equal("image/webp", proof.MimeType);
        Assert.Equal("comprobante.webp", proof.Name);
        Assert.Equal(1200, proof.SizeBytes);
        Assert.Equal("webp-checksum", proof.Checksum);
        Assert.Equal("staging/tenants/a/b", proof.StorageKey);
        Assert.Equal(FileResourceStatus.PendingScan, proof.Status);
        Assert.Equal(Now.AddMinutes(1), proof.UpdatedAt);
    }

    [Fact]
    public void TheNewExtensionIsNormalized()
    {
        var proof = PendingScanProof("foto.JPG", "image/jpeg");

        proof.ReplaceContentWithProcessedImage("image/webp", "WEBP", "checksum", 900, Now);

        Assert.Equal("foto.webp", proof.Name);
    }

    // El nombre vale hasta 260 caracteres: se recorta la base, nunca la extensión.
    [Fact]
    public void ALongNameIsTrimmedToFitTheNewExtension()
    {
        var proof = PendingScanProof(new string('a', 256) + ".png", "image/png");

        proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now);

        Assert.Equal(new string('a', 255) + ".webp", proof.Name);
    }

    [Fact]
    public void OnlyAPaymentProofCanBeReplaced()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            file.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void ReplacingRequiresPendingScan()
    {
        var proof = AvailableProof("comprobante.png", "image/png");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void AnEmptyProcessedImageIsRejected()
    {
        var proof = PendingScanProof("comprobante.png", "image/png");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 0, Now));

        Assert.Equal("storage.file.empty", error.Code);
    }

    // D1 y D10: un PDF también se mueve, y el objeto de staging sigue siendo su clave privada.
    [Fact]
    public void MoveToPublicRecordsTheKeyAndKeepsTheStagingKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
        Assert.Equal(Now, proof.PublishedAt);
        Assert.True(proof.IsPublic);
        Assert.Equal("staging/tenants/a/b", proof.StorageKey);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
    }

    // PaymentProofMoveWorker puede reintentar un mensaje: la segunda vez no cambia nada.
    [Fact]
    public void MoveToPublicIsIdempotentWithTheSameKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        proof.MoveToPublic("payment-proofs/abc.pdf", Now.AddHours(1));

        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
        Assert.Equal(Now, proof.PublishedAt);
        Assert.Equal(Now, proof.UpdatedAt);
    }

    [Fact]
    public void MoveToPublicRejectsADifferentKeyOnceMoved()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.MoveToPublic("payment-proofs/def.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
    }

    // D13: un archivo User nunca se mueve.
    [Fact]
    public void MoveToPublicRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            file.MoveToPublic("payment-proofs/abc.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Null(file.PublicStorageKey);
    }

    [Fact]
    public void MoveToPublicRequiresAnAvailableResource()
    {
        var proof = PendingScanProof("comprobante.pdf", "application/pdf");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.MoveToPublic("payment-proofs/abc.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void MoveToPublicRequiresAKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        var error = Assert.Throws<StorageDomainException>(() => proof.MoveToPublic(" ", Now));

        Assert.Equal("storage.file.public_key_required", error.Code);
    }

    // D11.
    [Fact]
    public void AnUnattachedProofCanBePurged()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.PurgeUnattachedPaymentProof(Now);

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now, proof.DeletedAt);
    }

    // Uno movido tiene su copia enlazada en un Excel: el barrido nunca lo purga.
    [Fact]
    public void AMovedProofCannotBePurged()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        var error = Assert.Throws<StorageDomainException>(() => proof.PurgeUnattachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
    }

    [Fact]
    public void PurgingRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() => file.PurgeUnattachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    private static FileResource PendingScanProof(string name, string mimeType)
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            name, mimeType, 4096, "staging/tenants/a/b", Now);
        proof.CompleteUpload("original-checksum", 4096, Now);
        return proof;
    }

    private static FileResource AvailableProof(string name, string mimeType)
    {
        var proof = PendingScanProof(name, mimeType);
        proof.MarkClean(Now);
        return proof;
    }
}
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofFileResourceTests"
```

Esperado (RED de compilación, ver «Decisiones»): `error CS0117: 'FileOwnerType' no contiene una definición para 'PaymentProof'` (o `'FileOwnerType' does not contain a definition for 'PaymentProof'`) y `CS1061` por `ReplaceContentWithProcessedImage`, `MoveToPublic` y `PurgeUnattachedPaymentProof`. Pega la salida.

- [ ] **Step 3: El valor del enum**

En `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs`, reemplaza:

```csharp
    // CAT-05: un archivo puede pertenecer a un producto del catálogo. Antes quedaba guardado como
    // User, porque el endpoint caía en silencio a ese valor cuando el string no parseaba.
    Product = 4
}
```

por:

```csharp
    // CAT-05: un archivo puede pertenecer a un producto del catálogo. Antes quedaba guardado como
    // User, porque el endpoint caía en silencio a ese valor cuando el string no parseaba.
    Product = 4,

    // Spec 2026-09-16, D2: un comprobante de pago de un pedido. Espera en staging/ hasta que se
    // adjunta (D4) y ahí se mueve al bucket público (D3). Los comprobantes subidos antes de v2
    // siguen siendo User y no cambian (D13).
    PaymentProof = 5
}
```

- [ ] **Step 4: Los tres métodos**

En `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs`, reemplaza:

```csharp
    private FileResource()
    {
    }
```

por:

```csharp
    // Mismo tope que FileUploadPolicy y que la columna name (StorageDbContext): un nombre que lo pase
    // no se podría guardar.
    private const int MaxNameLength = 260;

    private FileResource()
    {
    }
```

Reemplaza:

```csharp
    public void PurgeAbandonedUpload(DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingUpload);
        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }
```

por:

```csharp
    public void PurgeAbandonedUpload(DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingUpload);
        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    // Spec 2026-09-16, D7 y D8: la imagen de un comprobante se reemplaza por su versión procesada
    // antes de quedar Available, en la misma clave de staging (D4). El nombre conserva la base y
    // toma la extensión del contenido nuevo, para que una descarga desde la app baje con la que
    // corresponde.
    public void ReplaceContentWithProcessedImage(
        string mimeType,
        string extension,
        string checksum,
        long sizeBytes,
        DateTimeOffset occurredAt)
    {
        RequirePaymentProof();
        RequireStatus(FileResourceStatus.PendingScan);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        if (sizeBytes <= 0)
        {
            throw new StorageDomainException(
                "storage.file.empty",
                "The uploaded object is empty or missing.");
        }

        var normalizedExtension = "." + extension.TrimStart('.').ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(Name);
        var maxBaseLength = MaxNameLength - normalizedExtension.Length;
        Name = (baseName.Length > maxBaseLength ? baseName[..maxBaseLength] : baseName)
            + normalizedExtension;
        MimeType = mimeType;
        Checksum = checksum;
        SizeBytes = sizeBytes;
        UpdatedAt = occurredAt;
    }

    // Spec 2026-09-16, D9 y D10: registra que el comprobante ya vive en el bucket público. No es
    // Publish, que sólo acepta imágenes y protege el endpoint de publicación: acá entra un PDF (D1).
    // Idempotente con la misma clave, porque PaymentProofMoveWorker puede reintentar un mensaje.
    public void MoveToPublic(string publicStorageKey, DateTimeOffset occurredAt)
    {
        RequirePaymentProof();
        RequireStatus(FileResourceStatus.Available);
        if (string.IsNullOrWhiteSpace(publicStorageKey))
        {
            throw new StorageDomainException(
                "storage.file.public_key_required",
                "A public storage key is required.");
        }

        if (PublicStorageKey is not null)
        {
            if (string.Equals(PublicStorageKey, publicStorageKey, StringComparison.Ordinal))
            {
                return;
            }

            throw new StorageDomainException(
                "storage.file.invalid_state",
                "The payment proof was already moved to another public key.");
        }

        PublicStorageKey = publicStorageKey;
        PublishedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    // Spec 2026-09-16, D11: un comprobante que nadie adjuntó se purga como una subida abandonada.
    // Uno ya movido no: su copia pública está enlazada en un Excel.
    public void PurgeUnattachedPaymentProof(DateTimeOffset occurredAt)
    {
        RequirePaymentProof();
        RequireStatus(FileResourceStatus.Available);
        if (PublicStorageKey is not null)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "A payment proof already moved to the public bucket cannot be purged.");
        }

        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }
```

Y reemplaza:

```csharp
    private void RequireStatus(FileResourceStatus expected)
```

por:

```csharp
    private void RequirePaymentProof()
    {
        if (OwnerType is not FileOwnerType.PaymentProof)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                $"Expected a {FileOwnerType.PaymentProof} resource but it was {OwnerType}.");
        }
    }

    private void RequireStatus(FileResourceStatus expected)
```

- [ ] **Step 5: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
```

Esperado: el proyecto entero con `Con error: 0`, y las 16 de `PaymentProofFileResourceTests` entre las superadas (las de `FileResourceTests` siguen verdes). Pega el resumen literal.

- [ ] **Step 6: El chequeo de formato**

Corre «el chequeo de formato» de Global Constraints. Esperado: sin salida, o sólo líneas que no tocaste.

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs src/Modules/Storage/Modules.Storage.Domain/FileResource.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs; git commit -m "feat(storage): comprobantes de pago como dueño propio en el dominio" -m "FileOwnerType.PaymentProof, el reemplazo por la imagen procesada, el movimiento al bucket público y la purga del comprobante que nadie adjuntó (spec 2026-09-16, D2, D7, D10 y D11)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 1B: Un comprobante de pago retiene a quien lo subió (D18)

Enmienda D18: el frontend sube los comprobantes con `ownerId` = el usuario autenticado, y `FileUserReferenceProbe` sólo cuenta los archivos `User` (hallazgo 20). Sin este cambio, en cuanto el frontend mande `PaymentProof` (Task 11), `OrphanUserCleanupWorker` podría borrar a un usuario que todavía es dueño de comprobantes. La sonda no tiene pruebas propias: la cubre `OrphanUserCleanupTests`, que se extiende.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileUserReferenceProbe.cs:1-21` (archivo completo)
- Test: `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:130-149,367-382`

**Interfaces:**
- Consumes: `FileOwnerType.PaymentProof` (Task 1).
- Produces: `FileUserReferenceProbe.HasReferencesAsync(Guid userId, CancellationToken cancellationToken)` responde `true` si el usuario es `OwnerId` de algún archivo `User` **o** `PaymentProof`, en cualquier estado. `OrphanUserCleanupTests.SeedFileAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId, FileOwnerType ownerType = FileOwnerType.User)`. Ninguna tarea posterior depende de esto.

- [ ] **Step 1: Escribir la prueba (RED)**

En `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs`, reemplaza:

```csharp
    /// <summary>
    /// Reentrega: se borra la fila del inbox para que el worker reclame el mensaje otra vez.
```

por:

```csharp
    // Spec 2026-09-16, D18: el frontend sube los comprobantes con ownerId = el usuario. Pasarlos a
    // PaymentProof no puede dejar de retener a quien los subió.
    [Fact]
    public async Task OwningAPaymentProofKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedFileAsync(
            factory, Guid.Parse(tenantId), ownerUserId: member.UserId, FileOwnerType.PaymentProof);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    /// <summary>
    /// Reentrega: se borra la fila del inbox para que el worker reclame el mensaje otra vez.
```

Y reemplaza:

```csharp
    private static async Task SeedFileAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.FileResources.Add(FileResource.CreatePendingUpload(
            FileResourceId.New(),
            tenantId,
            ownerUserId,
            FileOwnerType.User,
```

por:

```csharp
    private static async Task SeedFileAsync(
        QepApiFactory factory, Guid tenantId, Guid ownerUserId, FileOwnerType ownerType = FileOwnerType.User)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.FileResources.Add(FileResource.CreatePendingUpload(
            FileResourceId.New(),
            tenantId,
            ownerUserId,
            ownerType,
```

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-restore --filter "FullyQualifiedName~OrphanUserCleanupTests.OwningAPaymentProofKeepsTheUser|FullyQualifiedName~OrphanUserCleanupTests.OwningAFileKeepsTheUser"
```

Esperado (RED de aserción), `Con error: 1, Superado: 1`: `OwningAPaymentProofKeepsTheUser` falla con `Assert.Equal() Failure: Values differ` → `Expected: 1`, `Actual: 0` (el worker borró al usuario); `OwningAFileKeepsTheUser` pasa. Pega la salida.

- [ ] **Step 2: La sonda**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileUserReferenceProbe.cs` por:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.Persistence;

/// <summary>
/// Storage retiene a un usuario mientras sea dueño de algún archivo
/// (<see cref="FileOwnerType.User"/> o <see cref="FileOwnerType.PaymentProof"/> + <c>owner_id</c>).
/// Cualquier estado cuenta, incluso borrado lógico o purgado: la fila sigue nombrando al dueño, y
/// el purgado físico es de otro proceso.
/// </summary>
/// <remarks>
/// Spec 2026-09-16, D18: el frontend sube los comprobantes de pago con <c>ownerId</c> = el usuario
/// que los carga. Pasarlos de <c>User</c> a <c>PaymentProof</c> no puede dejar de retenerlo.
/// </remarks>
internal sealed class FileUserReferenceProbe(StorageDbContext dbContext) : IUserReferenceProbe
{
    public string Source => "storage";

    // Con ||, no con un patrón `is ... or ...`: un árbol de expresión no acepta patrones.
    public Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.FileResources.AnyAsync(
            file => (file.OwnerType == FileOwnerType.User || file.OwnerType == FileOwnerType.PaymentProof)
                && file.OwnerId == userId,
            cancellationToken);
}
```

- [ ] **Step 3: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-restore --filter "FullyQualifiedName~OrphanUserCleanupTests"
```

Esperado: `OrphanUserCleanupTests` entera con `Con error: 0` (las 7 de antes y la nueva). Pega el resumen literal.

- [ ] **Step 4: El chequeo de formato**

Corre «el chequeo de formato». Esperado: sin salida, o sólo líneas que no tocaste.

- [ ] **Step 5: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FileUserReferenceProbe.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs; git commit -m "feat(storage): los comprobantes de pago retienen a quien los subió" -m "FileUserReferenceProbe cuenta también los archivos PaymentProof del usuario, así OrphanUserCleanupWorker no borra a quien todavía es dueño de comprobantes (spec 2026-09-16, D18)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 2: El puerto de procesamiento de imágenes y su implementación con ImageSharp

D7: lado mayor ≤ 2000 px, sin agrandar, WebP calidad 80. Mismo tope de píxeles de entrada y mismos códigos de error que el generador de miniaturas (hallazgo 11). Se registra en DI en Task 4, cuando `CompleteUploadHandler` lo pide.

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/IPaymentProofImageProcessor.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/Imaging/ImageSharpPaymentProofImageProcessor.cs`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/ImageSharpPaymentProofImageProcessorTests.cs` (nuevo)

**Interfaces:**
- Consumes: nada de tareas anteriores.
- Produces (Task 4 los usa):
  - `public interface IPaymentProofImageProcessor { bool Supports(string mimeType); Task<ProcessedPaymentProofImage> ProcessAsync(byte[] content, CancellationToken cancellationToken); }` en `Modules.Storage.Application`.
  - `public sealed record ProcessedPaymentProofImage(byte[] Content, string MimeType, string Extension, int Width, int Height);` con `MimeType = "image/webp"` y `Extension = ".webp"`.
  - `internal sealed class ImageSharpPaymentProofImageProcessor : IPaymentProofImageProcessor` en `Modules.Storage.Infrastructure.Imaging`, con `internal const int MaxLongSide = 2000` e `internal const int Quality = 80`. `Supports` es verdadero para `image/jpeg`, `image/png` e `image/webp`. El WebP sale sin `ExifProfile`, `IccProfile` ni `XmpProfile`. Lanza `StorageDomainException` `storage.image.invalid` o `storage.image.dimensions_too_large`.

- [ ] **Step 1: Escribir las pruebas (RED)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/ImageSharpPaymentProofImageProcessorTests.cs`:

```csharp
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Modules.Storage.UnitTests;

/// <summary>
/// La imagen de un comprobante de pago (spec 2026-09-16, D7): lado mayor de hasta 2000 px, sin
/// agrandar, WebP con pérdida y calidad 80, y sin EXIF, ICC ni XMP; una imagen corrupta se rechaza
/// con el mismo código que usa la miniatura.
/// </summary>
public sealed class ImageSharpPaymentProofImageProcessorTests
{
    private readonly ImageSharpPaymentProofImageProcessor _processor = new();

    [Theory]
    [InlineData(3000, 1500, 2000, 1000)]
    [InlineData(1500, 3000, 1000, 2000)]
    public async Task ALargeImageIsReducedToALongSideOf2000(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        var processed = await _processor.ProcessAsync(
            await PngAsync(width, height), TestContext.Current.CancellationToken);

        var info = Image.Identify(processed.Content);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
        Assert.Equal(expectedWidth, processed.Width);
        Assert.Equal(expectedHeight, processed.Height);
    }

    // D7: sin agrandar. ResizeMode.Max de ImageSharp sí agrandaría esta imagen.
    [Fact]
    public async Task ASmallImageIsNotEnlarged()
    {
        var processed = await _processor.ProcessAsync(
            await PngAsync(800, 600), TestContext.Current.CancellationToken);

        var info = Image.Identify(processed.Content);
        Assert.Equal(800, info.Width);
        Assert.Equal(600, info.Height);
    }

    // D7: el resultado es exactamente la codificación WebP con pérdida y calidad 80 de la misma
    // imagen, y no la de calidad 100. La imagen es ruidosa para que las dos calidades difieran.
    [Fact]
    public async Task TheResultIsLossyWebpAtQuality80()
    {
        var source = await PngAsync(640, 480, noisy: true);

        var processed = await _processor.ProcessAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal("image/webp", processed.MimeType);
        Assert.Equal(".webp", processed.Extension);
        Assert.Equal("RIFF"u8.ToArray(), processed.Content[..4]);
        Assert.Equal("WEBP"u8.ToArray(), processed.Content[8..12]);
        Assert.Equal(await EncodeAsync(source, quality: 80), processed.Content);
        Assert.NotEqual(await EncodeAsync(source, quality: 100), processed.Content);
    }

    // Una foto de celular trae la ubicación en el EXIF, y el comprobante termina en un bucket público
    // (D3): el WebP sale sin EXIF, ICC ni XMP. La fuente sólo lleva EXIF con GPS, porque armar a mano
    // un perfil ICC válido es frágil; el resultado se revisa igual para los tres.
    [Fact]
    public async Task TheResultCarriesNoExifIccOrXmpProfile()
    {
        var source = await JpegWithGpsAsync();
        // Sin esto la prueba pasaría en falso si el JPEG no guardara el EXIF.
        Assert.NotNull(Image.Identify(source).Metadata.ExifProfile);

        var processed = await _processor.ProcessAsync(source, TestContext.Current.CancellationToken);

        var metadata = Image.Identify(processed.Content).Metadata;
        Assert.Null(metadata.ExifProfile);
        Assert.Null(metadata.IccProfile);
        Assert.Null(metadata.XmpProfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACorruptImageIsRejectedAsInvalid(bool withPngSignature)
    {
        byte[] corrupt = withPngSignature
            ? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "not really a png"u8.ToArray()]
            : "not an image at all"u8.ToArray();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            _processor.ProcessAsync(corrupt, TestContext.Current.CancellationToken));

        Assert.Equal("storage.image.invalid", error.Code);
    }

    // D1: un PDF queda tal cual; sólo las imágenes se procesan.
    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/pdf", false)]
    public void SupportsOnlyTheProofImageTypes(string mimeType, bool expected)
    {
        Assert.Equal(expected, _processor.Supports(mimeType));
    }

    private static async Task<byte[]> PngAsync(int width, int height, bool noisy = false)
    {
        using var image = new Image<Rgba32>(width, height, Color.CornflowerBlue);
        if (noisy)
        {
            // Un patrón determinista, sin Random: la prueba tiene que dar lo mismo siempre.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image[x, y] = new Rgba32(
                        (byte)(x * 31 ^ y * 17), (byte)(x * 7 + y * 13), (byte)(x ^ y), 255);
                }
            }
        }

        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> JpegWithGpsAsync()
    {
        using var image = new Image<Rgba32>(320, 240, Color.CornflowerBlue);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(4u, 1u), new Rational(36u, 1u), new Rational(0u, 1u) });
        image.Metadata.ExifProfile = exif;

        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> EncodeAsync(byte[] png, int quality)
    {
        using var image = Image.Load(png);
        await using var output = new MemoryStream();
        await image.SaveAsWebpAsync(
            output,
            new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = quality },
            TestContext.Current.CancellationToken);
        return output.ToArray();
    }
}
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~ImageSharpPaymentProofImageProcessorTests"
```

Esperado (RED de compilación): `error CS0246: No se encontró el tipo o el nombre del espacio de nombres 'ImageSharpPaymentProofImageProcessor'` (o `The type or namespace name 'ImageSharpPaymentProofImageProcessor' could not be found`). Pega la salida.

- [ ] **Step 3: El puerto**

Crea `src/Modules/Storage/Modules.Storage.Application/IPaymentProofImageProcessor.cs`:

```csharp
namespace Modules.Storage.Application;

/// <summary>
/// Reduce y recodifica la imagen de un comprobante de pago al completar la subida (spec 2026-09-16,
/// D7 y D8). Es un puerto propio y no <see cref="IImageVariantGenerator"/> porque el resultado
/// **reemplaza** al original en staging/ en vez de sumarse como variante.
/// </summary>
public interface IPaymentProofImageProcessor
{
    /// <summary>Si el tipo es una imagen que se procesa: JPG, PNG o WebP. Un PDF queda tal cual
    /// (D1).</summary>
    bool Supports(string mimeType);

    /// <summary>La imagen procesada. Lanza <c>StorageDomainException</c> con
    /// <c>storage.image.invalid</c> o <c>storage.image.dimensions_too_large</c> cuando no se puede
    /// procesar.</summary>
    Task<ProcessedPaymentProofImage> ProcessAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>El contenido que reemplaza al original, con el tipo y la extensión que le corresponden.</summary>
public sealed record ProcessedPaymentProofImage(
    byte[] Content,
    string MimeType,
    string Extension,
    int Width,
    int Height);
```

- [ ] **Step 4: La implementación**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/Imaging/ImageSharpPaymentProofImageProcessor.cs`:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Modules.Storage.Infrastructure.Imaging;

// Spec 2026-09-16, D7: a 2000 px de lado mayor y calidad 80 el texto de un comprobante sigue
// legible, y el archivo pesa una fracción del original. Mismo tope de píxeles de entrada y mismos
// códigos de error que ImageSharpVariantGenerator: una imagen corrupta falla igual al subirla, sea
// comprobante o no.
internal sealed class ImageSharpPaymentProofImageProcessor : IPaymentProofImageProcessor
{
    internal const int MaxLongSide = 2000;
    internal const int Quality = 80;
    private const long MaximumSourcePixels = 40_000_000;

    private static readonly string[] SupportedMimeTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly WebpEncoder Encoder = new()
    {
        FileFormat = WebpFileFormatType.Lossy,
        Quality = Quality,
    };

    public bool Supports(string mimeType) =>
        SupportedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessedPaymentProofImage> ProcessAsync(
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = Image.Identify(content)
                ?? throw InvalidImage();
            if ((long)info.Width * info.Height > MaximumSourcePixels)
            {
                throw new StorageDomainException(
                    "storage.image.dimensions_too_large",
                    "The image dimensions exceed the processing limit.");
            }

            using var image = Image.Load(content);
            image.Mutate(context => context.AutoOrient());

            // Sin agrandar (D7): ResizeMode.Max también sube una imagen chica hasta el tope, así que
            // sólo se redimensiona la que lo pasa.
            if (Math.Max(image.Width, image.Height) > MaxLongSide)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxLongSide, MaxLongSide),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            await using var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, Encoder, cancellationToken);
            return new ProcessedPaymentProofImage(
                output.ToArray(), "image/webp", ".webp", image.Width, image.Height);
        }
        catch (ImageFormatException)
        {
            // Base de UnknownImageFormatException e InvalidImageContentException: un formato
            // desconocido y un contenido roto son, para quien sube, la misma imagen inválida.
            throw InvalidImage();
        }
    }

    private static StorageDomainException InvalidImage() =>
        new(
            "storage.image.invalid",
            "The uploaded image could not be decoded.");
}
```

- [ ] **Step 5: Correrlas (GREEN del tamaño y la calidad, RED de los metadatos)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~ImageSharpPaymentProofImageProcessorTests"
```

Esperado (RED de aserción del descarte de metadatos), `Superado: 10`, `Con error: 1`: sólo falla `TheResultCarriesNoExifIccOrXmpProfile`, con `Assert.Null() Failure: Value is not null` sobre el `ExifProfile` (el encoder de WebP de ImageSharp 3.1.12 escribe el EXIF que trae la imagen). Si esa prueba **pasa** acá, sin las líneas del Step 6, **para y pregunta**: no estaría probando nada. Si `ACorruptImageIsRejectedAsInvalid(withPngSignature: True)` falla con una excepción que no es `ImageFormatException` (por ejemplo `EndOfStreamException`), **para y pregunta** en vez de ampliar el `catch` a ciegas: pega el tipo y la traza. Lo mismo si `TheResultIsLossyWebpAtQuality80` falla en la comparación byte a byte con la codificación de calidad 80: pega el tamaño de los dos arreglos y pregunta antes de relajar la prueba. Pega la salida.

- [ ] **Step 6: Descartar EXIF, ICC y XMP (GREEN)**

En `src/Modules/Storage/Modules.Storage.Infrastructure/Imaging/ImageSharpPaymentProofImageProcessor.cs`, reemplaza:

```csharp
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            await using var output = new MemoryStream();
```

por:

```csharp
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            // Una foto de celular trae GPS y datos del equipo en el EXIF, y el comprobante termina en
            // un bucket público (D3): se descartan, igual que en la miniatura.
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;

            await using var output = new MemoryStream();
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~ImageSharpPaymentProofImageProcessorTests"
```

Esperado: `Superado: 11`, `Con error: 0`. Pega la salida.

- [ ] **Step 7: El chequeo de formato**

Corre «el chequeo de formato». Esperado: sin salida.

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Application/IPaymentProofImageProcessor.cs src/Modules/Storage/Modules.Storage.Infrastructure/Imaging/ImageSharpPaymentProofImageProcessor.cs tests/Modules/Storage/Modules.Storage.UnitTests/ImageSharpPaymentProofImageProcessorTests.cs; git commit -m "feat(storage): procesar imágenes de comprobantes a WebP" -m "Lado mayor de hasta 2000 px sin agrandar, WebP con pérdida calidad 80 y sin EXIF, ICC ni XMP (spec 2026-09-16, D3 y D7)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 3: El listado por prefijo en `IPublicObjectStorage`

D12 necesita recorrer `payment-proofs/` con la fecha de modificación de cada objeto. `IPublicObjectStorage` gana `ListAsync`, paginado con el token de continuación de S3, y sus tres implementaciones se actualizan en la misma tarea (hallazgo 5).

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IPublicObjectStorage.cs:1-15` (archivo completo)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2PublicObjectStorage.cs:1-41` (archivo completo)
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs:59-60`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:867-869`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/R2PublicObjectStorageTests.cs` (nuevo)

**Interfaces:**
- Consumes: nada de tareas anteriores.
- Produces (Tasks 4 y 10 los usan):
  - `Task<PublicObjectPage> ListAsync(string prefix, string? continuationToken, CancellationToken cancellationToken)` en `IPublicObjectStorage`.
  - `public sealed record PublicObjectPage(IReadOnlyList<PublicStoredObject> Objects, string? ContinuationToken);` — `ContinuationToken` es null en la última página.
  - `public sealed record PublicStoredObject(string Key, DateTimeOffset LastModified);`
  - `R2PublicObjectStorage.ListAsync` lanza `ArgumentException` con un prefijo vacío y deja afuera los objetos sin `LastModified`.

- [ ] **Step 1: Escribir las pruebas (RED)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/R2PublicObjectStorageTests.cs`:

```csharp
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Modules.Storage.Infrastructure;
using Modules.Storage.Infrastructure.ObjectStorage;

namespace Modules.Storage.UnitTests;

/// <summary>
/// El listado del bucket público por prefijo (spec 2026-09-16, D12) contra el cliente S3, sin red:
/// qué bucket y prefijo pide, cómo sigue la paginación y cómo trata las dos rarezas de AWSSDK.S3 v4
/// (colecciones en null y fechas opcionales).
/// </summary>
public sealed class R2PublicObjectStorageTests
{
    [Fact]
    public async Task ListingMapsAPageOfTheRequestedPrefix()
    {
        var modified = new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc);
        using var client = new ListingS3Client
        {
            Response = new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "payment-proofs/a.pdf", LastModified = modified },
                    new S3Object { Key = "payment-proofs/sin-fecha.pdf" },
                ],
                IsTruncated = true,
                NextContinuationToken = "next-page",
            },
        };

        var page = await StorageWith(client).ListAsync(
            "payment-proofs/", "this-page", TestContext.Current.CancellationToken);

        Assert.NotNull(client.Captured);
        Assert.Equal("qep-public", client.Captured.BucketName);
        Assert.Equal("payment-proofs/", client.Captured.Prefix);
        Assert.Equal("this-page", client.Captured.ContinuationToken);
        // Sin fecha no se puede probar que sea viejo, y la reconciliación sólo borra lo viejo.
        var stored = Assert.Single(page.Objects);
        Assert.Equal("payment-proofs/a.pdf", stored.Key);
        Assert.Equal(new DateTimeOffset(modified), stored.LastModified);
        Assert.Equal("next-page", page.ContinuationToken);
    }

    // AWSSDK.S3 v4 deja S3Objects en null cuando la página no trae objetos.
    [Fact]
    public async Task AnEmptyLastPageHasNoObjectsAndNoToken()
    {
        using var client = new ListingS3Client { Response = new ListObjectsV2Response() };

        var page = await StorageWith(client).ListAsync(
            "payment-proofs/", continuationToken: null, TestContext.Current.CancellationToken);

        Assert.Empty(page.Objects);
        Assert.Null(page.ContinuationToken);
        Assert.NotNull(client.Captured);
        Assert.Null(client.Captured.ContinuationToken);
    }

    // Sin prefijo se recorrería el bucket entero, con las imágenes de producto y los PDF de
    // cotización incluidos (D12).
    [Fact]
    public async Task ABlankPrefixIsRejectedWithoutCallingR2()
    {
        using var client = new ListingS3Client();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            StorageWith(client).ListAsync(" ", continuationToken: null, TestContext.Current.CancellationToken));

        Assert.Null(client.Captured);
    }

    private static R2PublicObjectStorage StorageWith(IAmazonS3 client) =>
        new(client, Options.Create(new StorageOptions
        {
            R2 = new R2Options
            {
                Bucket = "qep-private",
                PublicBucket = "qep-public",
                PublicBaseUrl = "https://assets-qep.example.co",
            },
        }));

    // Doble a mano, como R2ObjectStorageTests: hereda del cliente real y se queda con el request.
    private sealed class ListingS3Client : AmazonS3Client
    {
        public ListingS3Client()
            : base(
                new BasicAWSCredentials("key", "secret"),
                new AmazonS3Config
                {
                    ServiceURL = "https://example.invalid",
                    ForcePathStyle = true,
                    AuthenticationRegion = "auto",
                })
        {
        }

        public ListObjectsV2Response Response { get; init; } = new();

        public ListObjectsV2Request? Captured { get; private set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(Response);
        }
    }
}
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~R2PublicObjectStorageTests"
```

Esperado (RED de compilación): `error CS1061: 'R2PublicObjectStorage' no contiene una definición para 'ListAsync'` (o `does not contain a definition for 'ListAsync'`). Pega la salida.

- [ ] **Step 3: El puerto**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/IPublicObjectStorage.cs` por:

```csharp
namespace Modules.Storage.Application;

public interface IPublicObjectStorage
{
    bool IsConfigured { get; }

    Task CopyFromPrivateAsync(
        string privateKey,
        string publicKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    string GetUrl(string publicKey);

    /// <summary>
    /// Una página de los objetos del bucket público bajo <paramref name="prefix"/>, con su fecha de
    /// modificación (spec 2026-09-16, D12). <paramref name="continuationToken"/> es el de la página
    /// anterior, o null para la primera; la última página trae null.
    /// </summary>
    Task<PublicObjectPage> ListAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken);
}

public sealed record PublicObjectPage(
    IReadOnlyList<PublicStoredObject> Objects,
    string? ContinuationToken);

public sealed record PublicStoredObject(string Key, DateTimeOffset LastModified);
```

- [ ] **Step 4: La implementación de R2**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2PublicObjectStorage.cs` por:

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.ObjectStorage;

internal sealed class R2PublicObjectStorage(IAmazonS3 client, IOptions<StorageOptions> options)
    : IPublicObjectStorage
{
    private R2Options Settings => options.Value.R2;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Settings.PublicBucket) &&
        !string.IsNullOrWhiteSpace(Settings.PublicBaseUrl);

    public Task CopyFromPrivateAsync(
        string privateKey, string publicKey, CancellationToken cancellationToken) =>
        client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Settings.Bucket,
            SourceKey = privateKey,
            DestinationBucket = Settings.PublicBucket,
            DestinationKey = publicKey,
        }, cancellationToken);

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        IsConfigured
            ? client.DeleteObjectAsync(Settings.PublicBucket, publicKey, cancellationToken)
            : Task.CompletedTask;

    public string GetUrl(string publicKey)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Public image storage is not configured.");
        }

        return $"{Settings.PublicBaseUrl.TrimEnd('/')}/{publicKey}";
    }

    public async Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken)
    {
        // Sin prefijo se recorrería el bucket entero, con las imágenes de producto y los PDF de
        // cotización incluidos (spec 2026-09-16, D12).
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var response = await client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = Settings.PublicBucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            },
            cancellationToken);

        // AWSSDK.S3 v4 deja las colecciones en null cuando la respuesta no trae elementos, y
        // LastModified es opcional. Sin fecha no se puede probar que un objeto sea viejo, y la
        // reconciliación sólo borra lo viejo: se deja afuera en vez de inventarle una.
        var objects = (response.S3Objects ?? [])
            .Where(entry => entry.LastModified is not null)
            .Select(entry => new PublicStoredObject(entry.Key, ToUtc(entry.LastModified!.Value)))
            .ToArray();

        return new PublicObjectPage(
            objects,
            response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());
}
```

- [ ] **Step 5: El doble del Bootstrapper**

En `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs`, reemplaza:

```csharp
    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";
}
```

por:

```csharp
    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    // El publicador de comprobantes no lista el bucket: eso es de la reconciliación de Storage.
    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 6: El doble del harness de Quotations**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, reemplaza:

```csharp
        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";
    }
}
```

por:

```csharp
        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

        /// <summary>Las copias vigentes bajo el prefijo, en una sola página. La reconciliación de
        /// Storage no corre en estas pruebas (su intervalo es de horas): existe porque el puerto lo
        /// pide.</summary>
        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(new PublicObjectPage(
                _copies.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(key => new PublicStoredObject(key, DateTimeOffset.UtcNow))
                    .ToArray(),
                ContinuationToken: null));
    }
}
```

- [ ] **Step 7: Correr las pruebas y compilar la solución (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build --filter "FullyQualifiedName~R2PublicObjectStorageTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
```

Esperado: build con `0 Advertencia(s)` y `0 Errores` (compila también los dos dobles); `Superado: 3` en `R2PublicObjectStorageTests`; `Bootstrapper.UnitTests` con `Con error: 0`. Pega las salidas.

- [ ] **Step 8: El chequeo de formato**

Corre «el chequeo de formato». Esperado: sin salida.

- [ ] **Step 9: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Application/IPublicObjectStorage.cs src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/R2PublicObjectStorage.cs tests/Modules/Storage/Modules.Storage.UnitTests/R2PublicObjectStorageTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs; git commit -m "feat(storage): listar el bucket público por prefijo" -m "IPublicObjectStorage.ListAsync, paginado y con la fecha de modificación de cada objeto, para la reconciliación de payment-proofs/ (spec 2026-09-16, D12)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 4: `CompleteUpload` deja el comprobante procesado en `staging/`

Sección 1 del spec. Un `PaymentProof` limpio no se promueve ni lleva miniatura (D4); si es imagen se reemplaza por el WebP procesado (D7, D8) y un PDF queda tal cual (D1); los dos pasan a `Available` con `MarkClean`. Una imagen que no se puede procesar va a cuarentena con `image_processing_failed`. Suma el harness de integración de Storage que usan las Tasks 7 a 10.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs:1-152` (archivo completo)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:43`
- Create: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofUploadTests.cs` (nuevo)

**Interfaces:**
- Consumes: `FileOwnerType.PaymentProof`, `FileResource.ReplaceContentWithProcessedImage(string mimeType, string extension, string checksum, long sizeBytes, DateTimeOffset occurredAt)` (Task 1); `IPaymentProofImageProcessor`, `ProcessedPaymentProofImage`, `ImageSharpPaymentProofImageProcessor` (Task 2); `IPublicObjectStorage.ListAsync`, `PublicObjectPage`, `PublicStoredObject` (Task 3).
- Produces (Tasks 7 a 10 los usan):
  - `CompleteUploadHandler` con el parámetro `IPaymentProofImageProcessor paymentProofImageProcessor` (después de `imageVariantGenerator`).
  - En `Modules.Storage.IntegrationTests` (todo `internal`):
    - `PaymentProofStorageHarness` con `TenantId`, `SubjectId`, `FilesUrl`, `StartDatabaseAsync()`, `CreateClient(StorageApiFactory)`, `UploadAsync(HttpClient client, StorageApiFactory factory, string ownerType, string name, string mimeType, byte[] payload)` → `UploadedFile`, `CompleteAsync(HttpClient client, Guid fileId)` → `HttpResponseMessage`, `CreateAvailableAsync(...)` (mismos parámetros que `UploadAsync`) → `UploadedFile`, `PngAsync(int width, int height)`, `Pdf()`, `ReadFileAsync(string connectionString, Guid fileId)` → `FileRow`.
    - `StorageApiFactory(string connectionString)` con `ObjectStorage` (`InMemoryObjectStorage`) y `PublicObjectStorage` (`InMemoryPublicObjectStorage`).
    - `InMemoryObjectStorage` con `FailingDeleteKey`, `Keys`, `Upload`, `Read`, `Exists`, `Remove`.
    - `InMemoryPublicObjectStorage` con `BaseUrl = "https://assets.qep.test"`, `PageSize` (2), `ListedPrefixes`, `DeletedKeys`, `Put(string key, DateTimeOffset lastModified)`, `Exists`.
    - Records `UploadedFile(Guid FileId, string StagingKey)`, `FileRow(string Status, string OwnerType, string MimeType, string Name, long SizeBytes, string StorageKey, string? PublicStorageKey, string? Checksum, long VariantCount)`, `UploadSessionPayload`, `FilePayload`, `FileVariantPayload`, `ProblemPayload(string? Code)`.

- [ ] **Step 1: El harness de integración de Storage**

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Testcontainers.PostgreSql;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de los comprobantes de pago v2 (spec 2026-09-16): la
/// subida, el movimiento al bucket público, la descarga, el barrido de staging y la reconciliación.
/// Aparte de StorageFlowTests porque éstas necesitan los dos buckets en memoria y, desde Task 7,
/// correr a mano los procesadores de los workers.
/// </summary>
internal static class PaymentProofStorageHarness
{
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-000000000002");

    private const string Permissions = "storage.file.upload,storage.file.read,storage.file.delete";

    public static string FilesUrl { get; } = $"/api/v1/tenants/{TenantId}/files";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static HttpClient CreateClient(StorageApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", Permissions);
        return client;
    }

    /// <summary>Abre la sesión de subida y simula que R2 acepta el PUT firmado. Devuelve el id y la
    /// clave de staging.</summary>
    public static async Task<UploadedFile> UploadAsync(
        HttpClient client,
        StorageApiFactory factory,
        string ownerType,
        string name,
        string mimeType,
        byte[] payload)
    {
        using var response = await client.PostAsJsonAsync(
            FilesUrl,
            new { ownerId = SubjectId, ownerType, name, mimeType, sizeBytes = payload.Length },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        factory.ObjectStorage.Upload(session.StorageKey, payload);
        return new UploadedFile(session.FileResourceId, session.StorageKey);
    }

    public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid fileId) =>
        client.PostAsync(
            $"{FilesUrl}/{fileId}/complete", content: null, TestContext.Current.CancellationToken);

    /// <summary>Sube y completa, y exige 200.</summary>
    public static async Task<UploadedFile> CreateAvailableAsync(
        HttpClient client,
        StorageApiFactory factory,
        string ownerType,
        string name,
        string mimeType,
        byte[] payload)
    {
        var uploaded = await UploadAsync(client, factory, ownerType, name, mimeType, payload);
        using var response = await CompleteAsync(client, uploaded.FileId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return uploaded;
    }

    public static async Task<byte[]> PngAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.CornflowerBlue);
        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    public static byte[] Pdf() => "%PDF-1.7\ncomprobante"u8.ToArray();

    /// <summary>La fila de storage.file_resources y cuántas variantes tiene, leída con SQL para no
    /// depender de lo que expone la API.</summary>
    public static async Task<FileRow> ReadFileAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT status, owner_type, mime_type, name, size_bytes, storage_key, public_storage_key, checksum,
                   (SELECT count(*) FROM storage.file_variants WHERE file_resource_id = @id)
            FROM storage.file_resources
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", fileId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new FileRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8));
    }
}

internal sealed record UploadedFile(Guid FileId, string StagingKey);

internal sealed record FileRow(
    string Status,
    string OwnerType,
    string MimeType,
    string Name,
    long SizeBytes,
    string StorageKey,
    string? PublicStorageKey,
    string? Checksum,
    long VariantCount);

internal sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

internal sealed record FilePayload(
    Guid Id,
    string OwnerType,
    string Name,
    string MimeType,
    long SizeBytes,
    string Status,
    IReadOnlyList<FileVariantPayload> Variants);

internal sealed record FileVariantPayload(string Name);

internal sealed record ProblemPayload(string? Code);

/// <summary>El host de la API con los dos buckets en memoria.</summary>
internal sealed class StorageApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public InMemoryObjectStorage ObjectStorage { get; } = new();

    public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        // Fijados, nunca heredados de appsettings.json ni de los user-secrets de quien corre las
        // pruebas: mismo criterio que StorageFlowTests.
        builder.UseSetting("Notifications:EmailProvider", "log");
        builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage>(ObjectStorage);
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
        });
    }
}

/// <summary>El bucket privado en memoria. Concurrente porque los hosted services del host corren en
/// otros hilos mientras la prueba lee.</summary>
internal sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>La clave cuyo borrado falla, para ejercer el orden de D9 (Task 7); null si ninguna.</summary>
    public string? FailingDeleteKey { get; set; }

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

    public Task<Uri> CreatePresignedUploadUrlAsync(
        string key, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<StoredObject?>(_objects.TryGetValue(key, out var content)
            ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (string.Equals(key, FailingDeleteKey, StringComparison.Ordinal))
        {
            return Task.FromException(
                new InvalidOperationException("Simulated failure deleting from the private bucket."));
        }

        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PromoteAsync(
        string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
    {
        _objects[destinationKey] = _objects[sourceKey].ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects[key].ToArray());

    public Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        _objects[key] = content.ToArray();
        return Task.CompletedTask;
    }

    public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();

    public byte[] Read(string key) => _objects[key].ToArray();

    public bool Exists(string key) => _objects.ContainsKey(key);

    public void Remove(string key) => _objects.TryRemove(key, out _);
}

/// <summary>El bucket público en memoria, con fecha de modificación por objeto y paginación forzada
/// de a <see cref="PageSize"/>. El token de continuación es la última clave de la página, como el de
/// S3: borrar lo ya listado no corre las páginas siguientes.</summary>
internal sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets.qep.test";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _objects = new(StringComparer.Ordinal);

    public int PageSize { get; set; } = 2;

    public List<string> ListedPrefixes { get; } = [];

    public List<string> DeletedKeys { get; } = [];

    public bool IsConfigured => true;

    public void Put(string key, DateTimeOffset lastModified) => _objects[key] = lastModified;

    public bool Exists(string key) => _objects.ContainsKey(key);

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
    {
        _objects[publicKey] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        _objects.TryRemove(publicKey, out _);
        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken)
    {
        ListedPrefixes.Add(prefix);
        var remaining = _objects
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(entry => continuationToken is null
                || string.CompareOrdinal(entry.Key, continuationToken) > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var page = remaining
            .Take(PageSize)
            .Select(entry => new PublicStoredObject(entry.Key, entry.Value))
            .ToArray();
        var next = remaining.Length > PageSize ? page[^1].Key : null;
        return Task.FromResult(new PublicObjectPage(page, next));
    }
}
```

- [ ] **Step 2: Escribir las pruebas (RED)**

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofUploadTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Completar la subida de un comprobante de pago v2 (spec 2026-09-16, sección 1): la imagen se
/// procesa y reemplaza al original en staging/, el PDF queda intacto, ninguno se promueve a files/
/// ni lleva miniatura, y un archivo User sigue el camino de siempre (D13).
/// </summary>
public sealed class PaymentProofUploadTests
{
    // D2, D4, D7 y D8. El endpoint acepta el ownerType nuevo.
    [Fact]
    public async Task AnImageProofIsProcessedToWebpAndStaysInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(
            client, factory, "PaymentProof", "comprobante.png", "image/png", await PngAsync(2400, 1200));

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("PaymentProof", file.OwnerType);
        Assert.Equal("Available", file.Status);
        Assert.Equal("image/webp", file.MimeType);
        Assert.Equal("comprobante.webp", file.Name);
        Assert.Empty(file.Variants);
        Assert.DoesNotContain(factory.ObjectStorage.Keys, key => key.StartsWith("files/", StringComparison.Ordinal));

        var stored = factory.ObjectStorage.Read(uploaded.StagingKey);
        Assert.Equal("RIFF"u8.ToArray(), stored[..4]);
        Assert.Equal("WEBP"u8.ToArray(), stored[8..12]);
        var info = Image.Identify(stored);
        Assert.Equal(2000, info.Width);
        Assert.Equal(1000, info.Height);
        Assert.Equal(stored.LongLength, file.SizeBytes);

        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal(uploaded.StagingKey, row.StorageKey);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), row.Checksum);
        Assert.Equal(0L, row.VariantCount);
    }

    // D1: el PDF queda tal cual, también en staging/.
    [Fact]
    public async Task APdfProofStaysUntouchedInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("Available", file.Status);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal("comprobante.pdf", file.Name);
        Assert.Empty(file.Variants);
        Assert.DoesNotContain(factory.ObjectStorage.Keys, key => key.StartsWith("files/", StringComparison.Ordinal));
        Assert.Equal(Pdf(), factory.ObjectStorage.Read(uploaded.StagingKey));
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal(uploaded.StagingKey, row.StorageKey);
    }

    // Sección 1: «cuarentena y storage.file.rejected / image_processing_failed». Ya pasa antes de
    // esta tarea, por la miniatura; protege el camino nuevo.
    [Fact]
    public async Task AnImageProofThatCannotBeProcessedIsQuarantined()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        byte[] corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "not really a png"u8.ToArray()];
        var uploaded = await UploadAsync(client, factory, "PaymentProof", "comprobante.png", "image/png", corrupt);

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("storage.image.invalid", problem?.Code);
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal("Quarantined", row.Status);
        Assert.Equal("image/png", row.MimeType);
    }

    // D13: una imagen User se sigue promoviendo con su miniatura. Ya pasa antes de esta tarea.
    [Fact]
    public async Task AUserImageIsStillPromotedWithItsThumbnail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(client, factory, "User", "producto.png", "image/png", await PngAsync(640, 320));

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal("producto.png", file.Name);
        Assert.Equal("thumbnail", Assert.Single(file.Variants).Name);
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.StartsWith("files/tenants/", row.StorageKey, StringComparison.Ordinal);
        Assert.False(factory.ObjectStorage.Exists(uploaded.StagingKey));
    }
}
```

- [ ] **Step 3: Correrlas y verlas fallar**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofUploadTests"
```

Esperado (RED de aserción), `Con error: 2, Superado: 2`:
- `AnImageProofIsProcessedToWebpAndStaysInStaging` falla con `Assert.Equal() Failure: Strings differ` → `Expected: "image/webp"`, `Actual: "image/png"`;
- `APdfProofStaysUntouchedInStaging` falla con `Assert.DoesNotContain() Failure: Item found in collection` (el PDF se promovió a `files/`);
- las otras dos pasan: protegen caminos que ya existen.

Pega la salida.

- [ ] **Step 4: La rama de `PaymentProof` en `CompleteUpload`**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs` por:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record CompleteUploadCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<FileResourceDto>;

public sealed class CompleteUploadHandler(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IFileContentInspector contentInspector,
    IImageVariantGenerator imageVariantGenerator,
    IPaymentProofImageProcessor paymentProofImageProcessor,
    IFileScanner scanner,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<CompleteUploadCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        CompleteUploadCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileUpload);

        var resource = await LoadAsync(command.TenantId, command.FileResourceId, cancellationToken);

        var stored = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
            ?? throw new PreconditionRequiredException(
                "storage.object.missing",
                "The object has not been uploaded to storage yet.");

        var now = clock.UtcNow;
        var declaredSizeBytes = resource.SizeBytes;
        resource.CompleteUpload(stored.Checksum, stored.SizeBytes, now);

        if (stored.SizeBytes != declaredSizeBytes || stored.SizeBytes > FileUploadPolicy.MaxSizeBytes)
        {
            resource.Quarantine(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.size_invalid",
                "The uploaded object exceeds the maximum allowed size.");
        }

        var content = await objectStorage.DownloadAsync(resource.StorageKey, cancellationToken);
        if (!contentInspector.Matches(resource.Name, resource.MimeType, content))
        {
            resource.Quarantine(now);
            auditPublisher.Publish(
                resource.TenantId,
                executionContext.SubjectId,
                "storage.file.rejected",
                resource.Id.ToString(),
                "content_type_mismatch",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new StorageDomainException(
                "storage.file.content_mismatch",
                "The file content does not match its declared type.");
        }

        string? promotedStagingKey = null;
        var verdict = await scanner.ScanAsync(content, cancellationToken);
        if (verdict is FileScanResult.Clean && resource.OwnerType is FileOwnerType.PaymentProof)
        {
            await KeepPaymentProofInStagingAsync(resource, content, now, cancellationToken);
        }
        else if (verdict is FileScanResult.Clean)
        {
            var stagingKey = resource.StorageKey;
            var finalKey = StorageKey.FinalFor(resource.TenantId, resource.Id, resource.CreatedAt);
            IReadOnlyList<GeneratedFileVariant> variants;
            try
            {
                variants = imageVariantGenerator.Supports(resource.MimeType)
                    ? await imageVariantGenerator.GenerateAsync(content, cancellationToken)
                    : [];
            }
            catch (StorageDomainException)
            {
                await QuarantineUnprocessableImageAsync(resource, now, cancellationToken);
                throw;
            }
            await objectStorage.PromoteAsync(
                stagingKey, finalKey, stored.Checksum, cancellationToken);
            foreach (var variant in variants)
            {
                var variantKey = StorageKey.VariantFor(
                    finalKey, variant.Name, variant.Extension);
                await objectStorage.UploadAsync(
                    variantKey,
                    variant.Content,
                    variant.MimeType,
                    cancellationToken);
                resource.AddVariant(
                    variant.Name,
                    variantKey,
                    variant.MimeType,
                    variant.Width,
                    variant.Height,
                    variant.Content.LongLength);
            }
            resource.Promote(finalKey, now);
            promotedStagingKey = stagingKey;
        }
        else
        {
            resource.Quarantine(now);
        }

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.uploaded",
            resource.Id.ToString(),
            verdict is FileScanResult.Clean ? "success" : "quarantined",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (promotedStagingKey is not null)
        {
            await objectStorage.DeleteAsync(promotedStagingKey, cancellationToken);
        }
        return resource.ToDto();
    }

    // Spec 2026-09-16, D4 y D8: un comprobante no se promueve a files/ ni lleva miniatura; espera en
    // staging/ hasta que se adjunta a un pedido. Si es imagen se reemplaza ahí mismo por su versión
    // procesada (D7), así la conversión sólo tiene que moverlo; un PDF queda tal cual (D1).
    private async Task KeepPaymentProofInStagingAsync(
        FileResource resource,
        byte[] content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (paymentProofImageProcessor.Supports(resource.MimeType))
        {
            ProcessedPaymentProofImage processed;
            try
            {
                processed = await paymentProofImageProcessor.ProcessAsync(content, cancellationToken);
            }
            catch (StorageDomainException)
            {
                await QuarantineUnprocessableImageAsync(resource, now, cancellationToken);
                throw;
            }

            await objectStorage.UploadAsync(
                resource.StorageKey, processed.Content, processed.MimeType, cancellationToken);
            // El checksum es el que calcula el almacenamiento sobre lo que quedó guardado, igual que
            // al completar cualquier subida: se vuelve a leer en vez de calcularlo acá.
            var replaced = await objectStorage.StatAsync(resource.StorageKey, cancellationToken)
                ?? throw new PreconditionRequiredException(
                    "storage.object.missing",
                    "The object has not been uploaded to storage yet.");
            resource.ReplaceContentWithProcessedImage(
                processed.MimeType, processed.Extension, replaced.Checksum, replaced.SizeBytes, now);
        }

        resource.MarkClean(now);
    }

    private async Task QuarantineUnprocessableImageAsync(
        FileResource resource,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        resource.Quarantine(now);
        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.rejected",
            resource.Id.ToString(),
            "image_processing_failed",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<FileResource> LoadAsync(
        Guid tenantId, Guid fileResourceId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(
            new FileResourceId(fileResourceId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");

        if (resource.TenantId != tenantId)
        {
            // No filtrar la existencia entre tenants.
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        return resource;
    }
}
```

- [ ] **Step 5: Registrar el procesador**

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddSingleton<IImageVariantGenerator, ImageSharpVariantGenerator>();
```

por:

```csharp
        services.AddSingleton<IImageVariantGenerator, ImageSharpVariantGenerator>();
        // Spec 2026-09-16, D7: sin estado, una instancia por proceso alcanza.
        services.AddSingleton<IPaymentProofImageProcessor, ImageSharpPaymentProofImageProcessor>();
```

- [ ] **Step 6: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado: el proyecto entero con `Con error: 0` (las 4 de `PaymentProofUploadTests` más `StorageFlowTests` y `FileOwnerFilterApiTests`, que no deben cambiar). Pega el resumen literal.

- [ ] **Step 7: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Application/CompleteUpload.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofUploadTests.cs; git commit -m "feat(storage): dejar los comprobantes procesados en staging al completar la subida" -m "Un PaymentProof limpio no se promueve ni lleva miniatura: la imagen se reemplaza por su WebP y el PDF queda tal cual, los dos Available en staging/ (spec 2026-09-16, D1, D4, D7 y D8)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 5: Quotations acepta comprobantes WebP

Con Task 4, toda imagen de un `PaymentProof` llega a Quotations como `image/webp`. `OrderPaymentProofResolver` la acepta y `PublicPaymentProofPublisher` suma `image/webp → .webp`.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs:13-15,43-48`
- Modify: `src/Bootstrapper/PublicPaymentProofPublisher.cs:31-39,73-78`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs:55-58`
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:127`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPaymentProofResolverTests.cs` (nuevo)

**Interfaces:**
- Consumes: nada nuevo (`OrderPaymentProofResolver.ResolveAsync(IQuotationFileLookup lookup, Guid tenantId, Guid fileId, CancellationToken cancellationToken)`, `IQuotationFileLookup` y `QuotationFileRef(Guid FileId, Guid TenantId, string MimeType, long SizeBytes, bool IsAvailable)` ya existen).
- Produces: el resolver acepta `application/pdf`, `image/jpeg`, `image/png` e `image/webp`; el publicador genera claves `payment-proofs/{guid:N}.webp`. Task 7 lo usa de punta a punta.

- [ ] **Step 1: Escribir las pruebas (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPaymentProofResolverTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los tipos de comprobante que acepta un pedido. Desde v2 (spec 2026-09-16) toda imagen de un
/// comprobante llega procesada a WebP (D7), así que WebP se suma a los tres de US-14.
/// </summary>
public sealed class OrderPaymentProofResolverTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task AcceptsTheProofTypes(string mimeType)
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, mimeType, 1024, IsAvailable: true));

        var exception = await Record.ExceptionAsync(() =>
            OrderPaymentProofResolver.ResolveAsync(lookup, TenantId, fileId, TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RejectsAnotherImageType()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, "image/gif", 1024, IsAvailable: true));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            OrderPaymentProofResolver.ResolveAsync(lookup, TenantId, fileId, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_type_not_allowed", error.Code);
    }

    private sealed class SingleFileLookup(QuotationFileRef file) : IQuotationFileLookup
    {
        public Task<QuotationFileRef?> FindAsync(
            Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationFileRef?>(file.FileId == fileId ? file : null);

        public Task<string> CreateDownloadUrlAsync(
            Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

En `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs`, reemplaza:

```csharp
    [InlineData("IMAGE/PNG", "captura", ".png")]
```

por:

```csharp
    [InlineData("IMAGE/PNG", "captura", ".png")]
    // Spec 2026-09-16, D7: la imagen de un comprobante v2 llega procesada a WebP.
    [InlineData("image/webp", "comprobante.webp", ".webp")]
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofResolverTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofPublisherTests"
```

Esperado (RED de aserción):
- `OrderPaymentProofResolverTests`: `Con error: 1, Superado: 4`; `AcceptsTheProofTypes(mimeType: "image/webp")` falla con `Assert.Null() Failure: Value is not null` (la excepción es `QuotationsDomainException` con `order.payment_proof.file_type_not_allowed`);
- `PaymentProofPublisherTests`: falla sólo `TheExtensionComesFromTheMimeType(mimeType: "image/webp", …)`, con `Modules.Quotations.Domain.QuotationsDomainException : The payment proof must be a PDF, JPG or PNG file.`

Pega las dos salidas.

- [ ] **Step 3: El resolver**

En `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs`, reemplaza:

```csharp
    // US-14: "Acepta PDF, JPG, PNG, hasta 10 MB por archivo".
    private static readonly string[] AllowedMimeTypes =
        ["application/pdf", "image/jpeg", "image/png"];
```

por:

```csharp
    // US-14: "Acepta PDF, JPG, PNG, hasta 10 MB por archivo". WebP desde el 2026-09-16: Storage
    // procesa a WebP toda imagen de un comprobante v2 al completar la subida (spec, D7 y D8).
    private static readonly string[] AllowedMimeTypes =
        ["application/pdf", "image/jpeg", "image/png", "image/webp"];
```

Y reemplaza:

```csharp
                "The payment proof must be a PDF, JPG or PNG file.");
```

por:

```csharp
                "The payment proof must be a PDF, JPG, PNG or WEBP file.");
```

- [ ] **Step 4: El publicador**

En `src/Bootstrapper/PublicPaymentProofPublisher.cs`, reemplaza:

```csharp
    // La extensión sale del MimeType y no del nombre, que puede traer ".jpeg", mayúsculas o nada. Son
    // los tres tipos que OrderPaymentProofResolver deja pasar.
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
        };
```

por:

```csharp
    // La extensión sale del MimeType y no del nombre, que puede traer ".jpeg", mayúsculas o nada. Son
    // los tipos que OrderPaymentProofResolver deja pasar; WebP es la imagen procesada de un
    // comprobante v2 (spec 2026-09-16, D7).
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
        };
```

Y reemplaza:

```csharp
                "The payment proof must be a PDF, JPG or PNG file.");
```

por:

```csharp
                "The payment proof must be a PDF, JPG, PNG or WEBP file.");
```

- [ ] **Step 5: La guía de integración**

En `docs/integracion-cotizaciones-y-pedidos.md`, reemplaza:

```markdown
- Comprobante de pago: `application/pdf`, `image/jpeg` o `image/png`, hasta 10 MB.
```

por:

```markdown
- Comprobante de pago: `application/pdf`, `image/jpeg`, `image/png` o `image/webp`, hasta 10 MB.
```

- [ ] **Step 6: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
```

Esperado: los dos proyectos con `Con error: 0`; `OrderPaymentProofResolverTests` con 5 superadas y `TheExtensionComesFromTheMimeType` con sus 4 filas superadas. Pega los resúmenes.

- [ ] **Step 7: El chequeo de formato**

Corre «el chequeo de formato». Esperado: sin salida.

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs src/Bootstrapper/PublicPaymentProofPublisher.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPaymentProofResolverTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs docs/integracion-cotizaciones-y-pedidos.md; git commit -m "feat(orders): aceptar comprobantes de pago en WebP" -m "El resolver acepta image/webp y el publicador copia con extensión .webp: es la imagen procesada de un comprobante v2 (spec 2026-09-16, D7)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 6: El evento `quotations.order.payment-proofs-attached.v1` en el outbox

D9, paso 2: al convertir y al sumar comprobantes, en la misma unidad de trabajo que el pedido, Quotations escribe un evento con `tenantId`, `orderId` y, por cada comprobante nuevo **con copia pública**, `fileId` y `publicStorageKey`. Desde el rebase sobre `develop` (hallazgo 22) un comprobante corregido con `UpdatedProofs[].NewFileId` también cambia de archivo, y D19 lo cuenta como comprobante nuevo: su archivo de reemplazo entra en el mismo evento con la clave que le dio `PaymentProofCopies.PublishReplacementAsync`. Todavía nadie lo consume: eso es Task 7. Un comprobante sin clave (opción apagada) no tiene nada que mover y no entra en el evento; si ningún comprobante tiene clave, no hay evento. El borrado best-effort de la clave vieja que `develop` hace después de guardar (`AddOrderPaymentProofs.cs:163-174`) **no** se toca acá: lo quita Task 9D, en el mismo commit en que Quotations empieza a escribir el evento de retiro de D19.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs:70-77`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:49-50,125-132`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs:61-62,151-155`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:43`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs` (dos pruebas al final)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` (seis pruebas)

**Interfaces:**
- Consumes: `OrderPaymentProofInput(Guid FileId, decimal Amount, string? PublicStorageKey = null)` y `PaymentProofCopies` (v1); `OrderPaymentProofAmountUpdate(OrderPaymentProofId ProofId, decimal Amount, Guid? NewFileId = null, string? NewPublicStorageKey = null)` (`Order.cs:333-337`) y `OrderPaymentProofUpdateRequest(Guid ProofId, decimal Amount, Guid? NewFileId = null)` (`OrdersDtos.cs:34-35`), de `develop`; `QuotationsApiHarness.OutboxMessagesAsync(WebApplicationFactory<Program> factory, string eventName)` (`QuotationsApiHarness.cs:555-565`).
- Produces:
  - `public interface IOrderPaymentProofEventPublisher { void PublishAttached(Guid tenantId, OrderId orderId, IReadOnlyCollection<AttachedPaymentProof> proofs, DateTimeOffset occurredAt); }` y `public sealed record AttachedPaymentProof(Guid FileId, string PublicStorageKey);` en `Modules.Quotations.Application`.
  - `internal sealed class OrderPaymentProofEventPublisher(QuotationsDbContext dbContext)` con `internal const string AttachedEventName = "quotations.order.payment-proofs-attached.v1"`.
  - Payload JSON (contrato que Task 7 lee por nombre): `{ "tenantId": Guid, "orderId": Guid, "proofs": [ { "fileId": Guid, "publicStorageKey": string } ] }`. `CorrelationId` = id del pedido; `OccurredAt` = el `now` del handler.
  - `public static AttachedPaymentProof[] PaymentProofCopies.AttachedFrom(IEnumerable<OrderPaymentProofInput> inputs)` y `public static AttachedPaymentProof[] PaymentProofCopies.AttachedFromReplacements(IEnumerable<OrderPaymentProofAmountUpdate> updates)`, públicos dentro de la clase `internal` (pre-flight C6), como los demás miembros de `PaymentProofCopies`. Task 9D les suma `DetachedFrom`.

- [ ] **Step 1: Escribir las pruebas de integración (RED)**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`, reemplaza:

```csharp
using System.Net.Http.Json;
```

por:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
```

Reemplaza:

```csharp
    private const string PublicKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";
```

por:

```csharp
    private const string PublicKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";

    // El contrato con Storage (spec 2026-09-16, D9), escrito a mano: si alguien lo cambia de un solo
    // lado, estas pruebas lo ven.
    private const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
```

Reemplaza:

```csharp
    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

por:

```csharp
    // D9 (spec 2026-09-16): convertir con copias públicas deja, con el pedido, un evento con cada
    // comprobante nuevo y la clave de su copia.
    [Fact]
    public async Task ConvertingWithPublicLinksOnWritesTheAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var order = await ConvertAsync(
            client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId, secondFileId);

        var message = Assert.Single(await OutboxMessagesAsync(factory, AttachedEventName));
        var payload = JsonSerializer.Deserialize<AttachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload.TenantId);
        Assert.Equal(order.Id, payload.OrderId);
        Assert.Equal(
            new[] { firstFileId, secondFileId }.Order(),
            payload.Proofs.Select(proof => proof.FileId).Order());
        Assert.Equal(
            (await PublicKeysAsync(factory, order.Id)).Select(key => key!).Order(StringComparer.Ordinal),
            payload.Proofs.Select(proof => proof.PublicStorageKey).Order(StringComparer.Ordinal));
    }

    // D9: sumar comprobantes escribe otro evento, sólo con el comprobante nuevo.
    [Fact]
    public async Task AddingProofsWithPublicLinksOnWritesAnEventWithOnlyTheNewProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", firstFileId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = await OutboxMessagesAsync(factory, AttachedEventName);
        Assert.Equal(2, messages.Count);
        var added = JsonSerializer.Deserialize<AttachedEventPayload>(messages[1].PayloadJson, Json);
        Assert.NotNull(added);
        Assert.Equal(order.Id, added.OrderId);
        var proof = Assert.Single(added.Proofs);
        Assert.Equal(secondFileId, proof.FileId);
        Assert.Contains(proof.PublicStorageKey, await PublicKeysAsync(factory, order.Id));
    }

    // D9 y D19: el archivo de reemplazo de un comprobante corregido (UpdatedProofs[].NewFileId, de
    // develop) es un comprobante nuevo para Storage, así que entra en el evento con la clave de su copia.
    [Fact]
    public async Task ReplacingAProofFileWithPublicLinksOnWritesAnEventWithTheReplacement()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", firstFileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var replacementFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, replacementFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = await OutboxMessagesAsync(factory, AttachedEventName);
        Assert.Equal(2, messages.Count);
        var replaced = JsonSerializer.Deserialize<AttachedEventPayload>(messages[1].PayloadJson, Json);
        Assert.NotNull(replaced);
        Assert.Equal(order.Id, replaced.OrderId);
        var proof = Assert.Single(replaced.Proofs);
        Assert.Equal(replacementFileId, proof.FileId);
        Assert.Equal(Assert.Single(await PublicKeysAsync(factory, order.Id)), proof.PublicStorageKey);
    }

    // Sin copia pública no hay nada que mover: la opción apagada no escribe el evento. Ya pasa antes
    // de esta tarea.
    [Fact]
    public async Task ConvertingWithPublicLinksOffWritesNoAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Empty(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    // Corregir un monto no adjunta nada: no hay evento nuevo.
    [Fact]
    public async Task CorrectingOnlyAmountsWritesNoNewAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    // D9, «misma transacción»: un request que falla antes de guardar no deja evento. Ya pasa antes de
    // esta tarea.
    [Fact]
    public async Task AConversionWhoseCopyFailsWritesNoAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

Y reemplaza:

```csharp
    private sealed record ProblemDto(string? Code);
}
```

por:

```csharp
    private sealed record ProblemDto(string? Code);

    private sealed record AttachedEventPayload(Guid TenantId, Guid OrderId, IReadOnlyList<AttachedEventProof> Proofs);

    private sealed record AttachedEventProof(Guid FileId, string PublicStorageKey);
}
```

- [ ] **Step 2: Escribir la prueba unitaria (RED)**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs`, reemplaza:

```csharp
        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(second)], publisher.DeletedKeys);
    }
}
```

por:

```csharp
        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(second)], publisher.DeletedKeys);
    }

    // D9 (spec 2026-09-16): sólo un comprobante con copia pública tiene algo que mover.
    [Fact]
    public void OnlyTheProofsWithAPublicKeyAreAttached()
    {
        var withKey = Guid.CreateVersion7();
        var withoutKey = Guid.CreateVersion7();

        var attached = PaymentProofCopies.AttachedFrom(
        [
            new OrderPaymentProofInput(withKey, 10_000m, "payment-proofs/abc.webp"),
            new OrderPaymentProofInput(withoutKey, 5_000m),
        ]);

        Assert.Equal([new AttachedPaymentProof(withKey, "payment-proofs/abc.webp")], attached);
    }

    // D9 y D19: de las correcciones, sólo un archivo de reemplazo con copia pública tiene algo que mover.
    // Corregir sólo el monto no cambia el archivo.
    [Fact]
    public void OnlyTheReplacementFilesWithAPublicKeyAreAttached()
    {
        var replacedWithKey = Guid.CreateVersion7();
        var replacedWithoutKey = Guid.CreateVersion7();

        var attached = PaymentProofCopies.AttachedFromReplacements(
        [
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 10_000m, replacedWithKey, "payment-proofs/def.webp"),
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 5_000m, replacedWithoutKey),
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 7_000m),
        ]);

        Assert.Equal([new AttachedPaymentProof(replacedWithKey, "payment-proofs/def.webp")], attached);
    }
}
```

- [ ] **Step 3: Correrlas y verlas fallar**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests.ConvertingWithPublicLinksOnWritesTheAttachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AddingProofsWithPublicLinksOnWritesAnEventWithOnlyTheNewProof|FullyQualifiedName~OrderPaymentProofPublicationApiTests.ReplacingAProofFileWithPublicLinksOnWritesAnEventWithTheReplacement|FullyQualifiedName~OrderPaymentProofPublicationApiTests.ConvertingWithPublicLinksOffWritesNoAttachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.CorrectingOnlyAmountsWritesNoNewAttachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AConversionWhoseCopyFailsWritesNoAttachedEvent"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofCopiesTests"
```

Esperado:
- integración (RED de aserción), `Con error: 4, Superado: 2`: `ConvertingWithPublicLinksOnWritesTheAttachedEvent` y `CorrectingOnlyAmountsWritesNoNewAttachedEvent` con `Assert.Single() Failure: The collection was empty`; `AddingProofsWithPublicLinksOnWritesAnEventWithOnlyTheNewProof` y `ReplacingAProofFileWithPublicLinksOnWritesAnEventWithTheReplacement` con `Assert.Equal() Failure: Values differ` → `Expected: 2`, `Actual: 0`. Pasan las dos que protegen caminos que ya existen;
- unitarias (RED de compilación): `error CS0117: 'PaymentProofCopies' no contiene una definición para 'AttachedFrom'`, lo mismo para `'AttachedFromReplacements'`, y `CS0246` por `AttachedPaymentProof`.

Pega las dos salidas.

- [ ] **Step 4: El puerto**

Crea `src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Avisa que un pedido acaba de adjuntar comprobantes con copia pública (spec 2026-09-16, D9). Lo
/// consume Storage (<c>PaymentProofMoveWorker</c>) para borrar el temporal y registrar el
/// movimiento. Se escribe en el outbox de la misma unidad de trabajo que el pedido: si guardar
/// falla, el evento tampoco existe. Mismo mecanismo que <see cref="IExportEventPublisher"/>.
/// </summary>
public interface IOrderPaymentProofEventPublisher
{
    /// <summary><c>quotations.order.payment-proofs-attached.v1</c>.</summary>
    void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt);
}

/// <summary>Un comprobante recién adjuntado y la clave de su copia en el bucket público.</summary>
public sealed record AttachedPaymentProof(Guid FileId, string PublicStorageKey);
```

- [ ] **Step 5: La implementación**

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Acumula el evento en la proyección de outbox de QuotationsDbContext, para que commitee en la misma
// transacción que el pedido (spec 2026-09-16, D9). Lo consume PaymentProofMoveWorker, en Storage, que
// lee el payload por nombre: los campos son contrato.
internal sealed class OrderPaymentProofEventPublisher(QuotationsDbContext dbContext)
    : IOrderPaymentProofEventPublisher
{
    internal const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    public void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = AttachedEventName,
            PayloadJson = JsonSerializer.Serialize(new AttachedPayload(
                tenantId,
                orderId.Value,
                proofs
                    .Select(proof => new AttachedProofPayload(proof.FileId, proof.PublicStorageKey))
                    .ToArray())),
            // La correlación es el pedido: soporte llega del log del worker de Storage a la fila de
            // orders sin adivinar.
            CorrelationId = orderId.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre
    // con JsonDocument.
    private sealed record AttachedPayload(
        Guid tenantId,
        Guid orderId,
        IReadOnlyCollection<AttachedProofPayload> proofs);

    private sealed record AttachedProofPayload(Guid fileId, string publicStorageKey);
}
```

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IExportEventPublisher, ExportJobEventPublisher>();
```

por:

```csharp
        services.AddScoped<IExportEventPublisher, ExportJobEventPublisher>();
        // Spec 2026-09-16, D9: el aviso a Storage de que un pedido adjuntó comprobantes públicos.
        services.AddScoped<IOrderPaymentProofEventPublisher, OrderPaymentProofEventPublisher>();
```

- [ ] **Step 6: `AttachedFrom`**

En `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs`, reemplaza:

```csharp
                // Best-effort: una copia que no se pudo borrar queda huérfana en el bucket público,
                // y relanzar desde acá taparía la excepción original del request.
            }
        }
    }
}
```

por:

```csharp
                // Best-effort: una copia que no se pudo borrar queda huérfana en el bucket público,
                // y relanzar desde acá taparía la excepción original del request.
            }
        }
    }

    /// <summary>Los comprobantes que quedaron con copia pública, para el evento de D9 (spec
    /// 2026-09-16). Uno sin clave —la opción apagada— no tiene nada que mover.</summary>
    public static AttachedPaymentProof[] AttachedFrom(IEnumerable<OrderPaymentProofInput> inputs) =>
        inputs
            .Where(input => input.PublicStorageKey is not null)
            .Select(input => new AttachedPaymentProof(input.FileId, input.PublicStorageKey!))
            .ToArray();

    /// <summary>Los archivos de reemplazo que quedaron con copia pública (spec 2026-09-16, D9 y D19):
    /// para Storage son comprobantes nuevos. Una corrección sólo de monto no trae archivo.</summary>
    public static AttachedPaymentProof[] AttachedFromReplacements(
        IEnumerable<OrderPaymentProofAmountUpdate> updates) =>
        updates
            .Where(update => update.NewFileId is not null && update.NewPublicStorageKey is not null)
            .Select(update => new AttachedPaymentProof(update.NewFileId!.Value, update.NewPublicStorageKey!))
            .ToArray();
}
```

- [ ] **Step 7: Los dos handlers**

En `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs`, reemplaza:

```csharp
    IPaymentProofPublisher paymentProofPublisher,
    IOrderNumberGenerator numberGenerator,
```

por:

```csharp
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IOrderNumberGenerator numberGenerator,
```

y reemplaza:

```csharp
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.quotation.approved",
                quotation.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
```

por:

```csharp
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.quotation.approved",
                quotation.Id.ToString(),
                "success",
                now);
            // D9 (spec 2026-09-16): en la misma unidad de trabajo que el pedido, así el evento sólo
            // existe si el pedido se guardó, y Storage nunca borra un temporal que nadie adjuntó.
            var attached = PaymentProofCopies.AttachedFrom(proofs);
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
```

En `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs` (el de `develop`, con `UpdatedProofs[].NewFileId`: `proofs` y `updatedProofs` son las dos variables que arma el `try` antes de `order.AddPaymentProofs`, `:122-146`), reemplaza:

```csharp
    IPaymentProofPublisher paymentProofPublisher,
    IMembershipDirectory membershipDirectory,
```

por:

```csharp
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IMembershipDirectory membershipDirectory,
```

y reemplaza:

```csharp
                "quotation.order.payment_proofs_added",
                order.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
```

por:

```csharp
                "quotation.order.payment_proofs_added",
                order.Id.ToString(),
                "success",
                now);
            // D9 y D19 (spec 2026-09-16): los comprobantes nuevos y los archivos de reemplazo; corregir
            // sólo un monto no mueve nada.
            var attached = PaymentProofCopies.AttachedFrom(proofs)
                .Concat(PaymentProofCopies.AttachedFromReplacements(updatedProofs))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
```

- [ ] **Step 8: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: `Modules.Quotations.UnitTests` entero con `Con error: 0`; `OrderPaymentProofPublicationApiTests` con `Superado: 15`, `Con error: 0` (las 9 de v1 y las 6 nuevas). Pega los resúmenes.

- [ ] **Step 9: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 10: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs; git commit -m "feat(orders): evento de comprobantes adjuntados en el outbox" -m "Convertir y sumar comprobantes escriben quotations.order.payment-proofs-attached.v1 en la misma transacción que el pedido, con cada comprobante nuevo y cada archivo de reemplazo que tiene copia pública (spec 2026-09-16, D9 y D19)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 7: Storage mueve los comprobantes adjuntados

D9, paso 4: `PaymentProofMoveWorker` consume el evento con un inbox nuevo de Storage. Por cada `PaymentProof` todavía sin mover, **primero** borra el objeto de `staging/` y **después**, en un solo `SaveChanges`, llama a `MoveToPublic` y marca el inbox. La prueba de punta a punta corre en el host de Quotations, donde el worker corre solo.

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageInboxMessage.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageDbContext.cs:11-17,80`
- Create (generados): `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations/<timestamp>_AddStorageInbox.cs` y `.Designer.cs`; Modify (regenerado): `StorageDbContextModelSnapshot.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/Modules.Storage.Infrastructure.csproj:3-5`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:10-11,54`
- Modify: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` (usings, helpers y el worker fuera del host)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:1-18,339-353,383-386,762,790-794,816`
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:116,121-124`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageDbContextMappingTests.cs` (nuevo)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofMoveTests.cs` (nuevo)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` (cuatro pruebas)

**Interfaces:**
- Consumes: `FileResource.MoveToPublic(string publicStorageKey, DateTimeOffset occurredAt)` (Task 1); el harness de Storage (Task 4); el evento y su payload (Task 6); `image/webp → .webp` (Task 5).
- Produces:
  - `internal sealed class StorageInboxMessage { string Consumer; Guid MessageId; DateTimeOffset ProcessedAt; }` y `internal DbSet<StorageInboxMessage> StorageDbContext.Inbox`, tabla `storage.inbox_messages`, PK `(consumer, message_id)`.
  - `internal interface IPaymentProofMoveProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }` (devuelve cuántos mensajes quedaron procesados) en `Modules.Storage.Infrastructure.PaymentProofs`.
  - `internal sealed partial class PaymentProofMoveProcessor` con `internal const string Consumer = "storage.payment-proof-move"` e `internal const string AttachedEvent = "quotations.order.payment-proofs-attached.v1"`.
  - `internal sealed partial class PaymentProofMoveWorker` (3 s, corre al arrancar).
  - `InternalsVisibleTo Modules.Storage.IntegrationTests`.
  - En el harness de Storage: `NewPublicKey(string extension)`, `AddAttachedEventAsync(StorageApiFactory factory, params (Guid FileId, string PublicStorageKey)[] proofs)` → `Guid` (id del mensaje), `RunMoveAsync(StorageApiFactory)` → `int`, `IsProcessedByMoveAsync(StorageApiFactory, Guid messageId)` → `bool`. Tasks 8, 9, 9B, 9C y 10 los usan.
  - En el harness de Quotations: `CreateAvailableFileAsync(..., string ownerType = "User")`, `CreateAvailablePaymentProofImageAsync(HttpClient client, QepApiFactory factory, Guid tenantId)` e `InMemoryObjectStorage.Exists(string key)`.

- [ ] **Step 1: Escribir la prueba del mapeo (RED)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/StorageDbContextMappingTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.UnitTests;

/// <summary>
/// El modelo de EF de Storage contra el snapshot, sin abrir una base (mismo criterio que
/// QuotationsDbContextMappingTests): el inbox nuevo (spec 2026-09-16, D9) y que ningún cambio de
/// modelo quede sin migración.
/// </summary>
public sealed class StorageDbContextMappingTests
{
    [Fact]
    public void TheInboxMapsToItsTableWithConsumerAndMessageAsKey()
    {
        using var context = new StorageDbContextFactory().CreateDbContext([]);

        var inbox = context.Model.FindEntityType(typeof(StorageInboxMessage));

        Assert.NotNull(inbox);
        Assert.Equal("inbox_messages", inbox.GetTableName());
        Assert.Equal("storage", inbox.GetSchema());
        var key = inbox.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Equal(["Consumer", "MessageId"], key.Properties.Select(property => property.Name));
    }

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new StorageDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageDbContextMappingTests"
```

Esperado (RED de compilación): `error CS0246: … 'StorageInboxMessage' …`. Pega la salida.

- [ ] **Step 2: El inbox en el modelo**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageInboxMessage.cs`:

```csharp
namespace Modules.Storage.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor (spec 2026-09-16, D9): un mensaje del outbox de plataforma
// que un consumidor de Storage ya procesó. (consumer, message_id) es única. Mismo diseño que
// IdentityInboxMessage.
internal sealed class StorageInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageDbContext.cs`, reemplaza:

```csharp
    internal DbSet<StorageOutboxMessage> Outbox => Set<StorageOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureFileResource(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }
```

por:

```csharp
    internal DbSet<StorageOutboxMessage> Outbox => Set<StorageOutboxMessage>();

    internal DbSet<StorageInboxMessage> Inbox => Set<StorageInboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureFileResource(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureInbox(modelBuilder);
    }

    // Spec 2026-09-16, D9: el inbox de los consumidores de Storage. Tabla propia del esquema storage,
    // igual que identity.inbox_messages.
    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<StorageInboxMessage>();
        inbox.ToTable("inbox_messages", "storage");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageDbContextMappingTests"
```

Esperado (RED de aserción), `Con error: 1, Superado: 1`: `TheModelHasNoChangesPendingAMigration` falla con `Assert.False() Failure` → `Expected: False`, `Actual: True`. Pega la salida.

- [ ] **Step 3: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddStorageInbox --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext -o Persistence/Migrations
git status --short -- src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations
Get-ChildItem src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations -Filter "*_AddStorageInbox.cs" | Select-String -Pattern 'name: "inbox_messages"', 'schema: "storage"', 'PK_inbox_messages'
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageDbContextMappingTests"
```

Esperado: `Done.`; `git status` con dos `??` (`<timestamp>_AddStorageInbox.cs` y `.Designer.cs`) y ` M StorageDbContextModelSnapshot.cs`; el `Select-String` encuentra las tres cadenas en la migración; y la prueba con `Superado: 2`, `Con error: 0`. Si la migración toca `file_resources`, `file_variants` o `outbox_messages`, **para y pregunta**: el snapshot estaba desactualizado. Pega las salidas.

- [ ] **Step 4: Las pruebas de punta a punta en el host de Quotations (RED)**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, reemplaza:

```csharp
using System.Net.Http.Json;
using System.Security.Cryptography;
```

por:

```csharp
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
```

Reemplaza:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
```

por:

```csharp
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Testcontainers.PostgreSql;
```

Reemplaza:

```csharp
    public static async Task<Guid> CreateAvailableFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId,
        string mimeType, byte[] payload, string fileName)
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.NewGuid(),
                ownerType = "User",
```

por:

```csharp
    public static async Task<Guid> CreateAvailableFileAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId,
        string mimeType, byte[] payload, string fileName, string ownerType = "User")
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.NewGuid(),
                ownerType,
```

Reemplaza:

```csharp
            client, factory, tenantId, "application/pdf", "%PDF-1.7\nproof"u8.ToArray(), "proof.pdf");
```

por:

```csharp
            client, factory, tenantId, "application/pdf", "%PDF-1.7\nproof"u8.ToArray(), "proof.pdf");

    /// <summary>Spec 2026-09-16: un comprobante v2 (<c>PaymentProof</c>) de imagen. Storage lo deja
    /// en WebP y en staging/ al completarlo (D7, D8), y lo mueve al público cuando se adjunta (D9).
    /// </summary>
    public static async Task<Guid> CreateAvailablePaymentProofImageAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        using var image = new Image<Rgba32>(64, 48, Color.CornflowerBlue);
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, TestContext.Current.CancellationToken);
        return await CreateAvailableFileAsync(
            client, factory, tenantId, "image/png", png.ToArray(), "comprobante.png", ownerType: "PaymentProof");
    }
```

Reemplaza:

```csharp
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
```

por:

```csharp
        // Concurrente desde el 2026-09-16: PaymentProofMoveWorker corre en el host cada 3 s y borra
        // temporales mientras la prueba sube y lee (hallazgo 9 del plan).
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
```

Reemplaza:

```csharp
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.Remove(key);
```

por:

```csharp
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.TryRemove(key, out _);
```

Y reemplaza:

```csharp
        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();
```

por:

```csharp
        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();

        public bool Exists(string key) => _objects.ContainsKey(key);
```

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`, reemplaza:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
```

por:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
```

Reemplaza:

```csharp
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

por:

```csharp
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

Reemplaza:

```csharp
    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

por:

```csharp
    // D8, D9 y D3 de punta a punta (spec 2026-09-16): la imagen de un PaymentProof llega procesada, se
    // copia al público al convertir y PaymentProofMoveWorker —que corre solo en el host— borra el
    // temporal y registra el movimiento.
    [Fact]
    public async Task ConvertingWithAPaymentProofImageMovesItToThePublicBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var stagingKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        await AssertMovedAsync(database.GetConnectionString(), factory, order.Id, fileId, stagingKey);
    }

    [Fact]
    public async Task AddingAPaymentProofImageMovesItToThePublicBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var stagingKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertMovedAsync(database.GetConnectionString(), factory, order.Id, fileId, stagingKey);
    }

    // D9, paso 3: si algo falla antes de guardar, el rollback borra las copias públicas y los
    // temporales siguen en staging/. Ya pasa antes de esta tarea.
    [Fact]
    public async Task AConversionWhoseCopyFailsLeavesThePaymentProofsInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var firstStagingKey = await StorageKeyOfAsync(database.GetConnectionString(), firstFileId);
        var secondStagingKey = await StorageKeyOfAsync(database.GetConnectionString(), secondFileId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Matches("^payment-proofs/[0-9a-f]{32}\\.webp$", Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        Assert.True(factory.ObjectStorage.Exists(firstStagingKey));
        Assert.True(factory.ObjectStorage.Exists(secondStagingKey));
    }

    // D13: un comprobante User se copia como en v1, pero Storage no lo mueve: el original sigue en el
    // bucket privado y el archivo no gana clave pública.
    [Fact]
    public async Task AUserProofIsCopiedButNeverMoved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var privateKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Matches(PublicKeyPattern, Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.True(await WaitForMoveInboxAsync(database.GetConnectionString()));
        Assert.Null(await PublicStorageKeyOfAsync(database.GetConnectionString(), fileId));
        Assert.True(factory.ObjectStorage.Exists(privateKey));
    }

    private static async Task AssertMovedAsync(
        string connectionString, QepApiFactory factory, Guid orderId, Guid fileId, string stagingKey)
    {
        var publicKey = Assert.Single(await PublicKeysAsync(factory, orderId));
        Assert.NotNull(publicKey);
        Assert.Matches("^payment-proofs/[0-9a-f]{32}\\.webp$", publicKey);
        Assert.Equal(stagingKey, factory.PublicObjectStorage.Copies[publicKey]);
        Assert.Equal(publicKey, await WaitForMovedKeyAsync(connectionString, fileId));
        Assert.False(factory.ObjectStorage.Exists(stagingKey));
    }

    private static async Task<string> StorageKeyOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT storage_key FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string?> PublicStorageKeyOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT public_storage_key FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    // PaymentProofMoveWorker corre solo en el host de pruebas, cada 3 s: se espera con plazo, igual
    // que WaitForEmailStatusAsync.
    private static async Task<string?> WaitForMovedKeyAsync(string connectionString, Guid fileId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await PublicStorageKeyOfAsync(connectionString, fileId) is { } key)
            {
                return key;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task<bool> WaitForMoveInboxAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM storage.inbox_messages WHERE consumer = 'storage.payment-proof-move'",
                connection);
            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests.ConvertingWithAPaymentProofImageMovesItToThePublicBucket|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AddingAPaymentProofImageMovesItToThePublicBucket|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AConversionWhoseCopyFailsLeavesThePaymentProofsInStaging|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AUserProofIsCopiedButNeverMoved"
```

Esperado (RED de aserción), `Con error: 3, Superado: 1`, después de unos 30 s de espera por prueba:
- las dos de imagen fallan con `Assert.Equal() Failure: Values differ` → `Expected: "payment-proofs/….webp"`, `Actual: null` (nadie mueve el comprobante);
- `AUserProofIsCopiedButNeverMoved` falla con `Assert.True() Failure` (nadie consume el evento, el inbox sigue vacío);
- `AConversionWhoseCopyFailsLeavesThePaymentProofsInStaging` pasa.

Pega la salida.

- [ ] **Step 5: El harness de Storage y las pruebas del procesador (RED)**

En `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`, reemplaza:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
```

por:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.PaymentProofs;
using Modules.Storage.Infrastructure.Persistence;
```

Reemplaza:

```csharp
    /// <summary>La fila de storage.file_resources y cuántas variantes tiene, leída con SQL para no
```

por:

```csharp
    public static string NewPublicKey(string extension) =>
        $"payment-proofs/{Guid.CreateVersion7():N}{extension}";

    /// <summary>Escribe en el outbox de plataforma el evento que Quotations escribe al adjuntar
    /// (spec 2026-09-16, D9), con el nombre y los campos del contrato escritos a mano: si Quotations
    /// o Storage los cambian por separado, estas pruebas lo ven. Devuelve el id del mensaje.</summary>
    public static async Task<Guid> AddAttachedEventAsync(
        StorageApiFactory factory, params (Guid FileId, string PublicStorageKey)[] proofs)
    {
        var id = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = id,
            EventName = "quotations.order.payment-proofs-attached.v1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                tenantId = TenantId,
                orderId = Guid.CreateVersion7(),
                proofs = proofs
                    .Select(proof => new { fileId = proof.FileId, publicStorageKey = proof.PublicStorageKey })
                    .ToArray(),
            }),
            CorrelationId = id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Un lote del worker de movimiento, a mano: el hosted service no corre en este host
    /// (ver <see cref="StorageApiFactory"/>).</summary>
    public static async Task<int> RunMoveAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>()
            .ProcessPendingAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<bool> IsProcessedByMoveAsync(StorageApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        return await dbContext.Inbox.AnyAsync(
            entry => entry.Consumer == "storage.payment-proof-move" && entry.MessageId == messageId,
            TestContext.Current.CancellationToken);
    }

    /// <summary>La fila de storage.file_resources y cuántas variantes tiene, leída con SQL para no
```

Y reemplaza:

```csharp
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
        });
```

por:

```csharp
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);

            // El worker de movimiento consulta el outbox cada 3 s: competiría con el lote que la
            // prueba corre a mano (RunMoveAsync) y la volvería no determinista.
            var moveWorkers = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PaymentProofMoveWorker))
                .ToList();
            foreach (var descriptor in moveWorkers)
            {
                services.Remove(descriptor);
            }
        });
```

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofMoveTests.cs`:

```csharp
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El movimiento de un comprobante adjuntado (spec 2026-09-16, D9, paso 4): primero se borra el
/// temporal y después, en un solo SaveChanges, MoveToPublic y el inbox. Un PDF se mueve igual (D1) y
/// un archivo User se ignora (D13).
/// </summary>
public sealed class PaymentProofMoveTests
{
    [Fact]
    public async Task AnAttachedImageProofIsMovedAndItsStagingObjectDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.png", "image/png", await PngAsync(640, 480));
        var publicKey = NewPublicKey(".webp");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));

        var processed = await RunMoveAsync(factory);

        Assert.Equal(1, processed);
        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.Equal("Available", row.Status);
        Assert.Equal(proof.StagingKey, row.StorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // D1: un PDF se mueve tal cual.
    [Fact]
    public async Task AnAttachedPdfProofIsMovedAsIs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));

        Assert.Equal(1, await RunMoveAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.Equal("application/pdf", row.MimeType);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D13: un comprobante User conserva su original privado; el mensaje se marca igual.
    [Fact]
    public async Task AUserFileIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        var privateKey = (await ReadFileAsync(database.GetConnectionString(), file.FileId)).StorageKey;
        var messageId = await AddAttachedEventAsync(factory, (file.FileId, NewPublicKey(".pdf")));

        Assert.Equal(1, await RunMoveAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), file.FileId);
        Assert.Null(row.PublicStorageKey);
        Assert.True(factory.ObjectStorage.Exists(privateKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // D9: si el borrado del temporal falla, no se guarda nada y el mensaje vuelve en el tick siguiente.
    [Fact]
    public async Task WhenDeletingTheStagingObjectFailsNothingIsSavedAndTheMessageComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.Null((await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(await IsProcessedByMoveAsync(factory, messageId));
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D9: si un tick anterior borró el temporal y no llegó a guardar, el reintento borra una clave que
    // ya no existe —no falla— y guarda.
    [Fact]
    public async Task AStagingObjectAlreadyDeletedDoesNotStopTheMove()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.ObjectStorage.Remove(proof.StagingKey);

        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
    }

    // El inbox: un mensaje ya procesado no se vuelve a aplicar.
    [Fact]
    public async Task AProcessedMessageIsNotAppliedTwice()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado (RED de compilación): `error CS0234: El tipo o el nombre del espacio de nombres 'PaymentProofs' no existe en el espacio de nombres 'Modules.Storage.Infrastructure'` y `CS0122` (inaccesible por su nivel de protección) sobre `StorageDbContext.Outbox`/`Inbox`. Pega la salida.

- [ ] **Step 6: Exponer los internals a las pruebas de integración**

En `src/Modules/Storage/Modules.Storage.Infrastructure/Modules.Storage.Infrastructure.csproj`, reemplaza:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Storage.UnitTests" />
  </ItemGroup>
```

por:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Storage.UnitTests" />
    <!-- Los procesadores de los workers son internal: las pruebas de integración los invocan directo
         para no depender del temporizador (spec 2026-09-16), mismo criterio que
         IQuotationExpirationProcessor en Quotations. -->
    <InternalsVisibleTo Include="Modules.Storage.IntegrationTests" />
  </ItemGroup>
```

- [ ] **Step 7: El procesador**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofMoveProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-09-16, D9, paso 4. Consume del outbox de plataforma el evento que Quotations escribe al
// adjuntar comprobantes, con el mismo esqueleto que OrphanUserCleanupWorker: anti-join contra el inbox
// propio con clave (consumidor, id de mensaje), y efecto e inbox en el mismo SaveChanges.
//
// El orden importa. Primero se borra el temporal —que ya tiene su copia pública— y recién después se
// guarda el movimiento con el inbox. Borrar una clave que ya no existe no falla, así que el paso se
// puede repetir: si el borrado falla no se guarda nada y el mensaje vuelve; si falla el guardado, el
// tick siguiente repite el borrado (sin efecto) y guarda. Al revés, un borrado fallido dejaría un
// huérfano en staging/ para siempre: el inbox ya estaría marcado y el barrido sólo mira comprobantes
// sin mover.
internal sealed partial class PaymentProofMoveProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IClock clock,
    ILogger<PaymentProofMoveProcessor> logger) : IPaymentProofMoveProcessor
{
    internal const string Consumer = "storage.payment-proof-move";
    internal const string AttachedEvent = "quotations.order.payment-proofs-attached.v1";
    private const int BatchSize = 20;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof move failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == AttachedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var record in pending)
        {
            try
            {
                await MoveAsync(record, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla (un borrado en R2, un conflicto con otra réplica) no frena a los
                // demás: se descarta lo rastreado y, sin inbox, vuelve en el tick siguiente.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    private async Task MoveAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        var payload = AttachedPayload.Parse(record.PayloadJson);
        foreach (var proof in payload.Proofs)
        {
            var fileId = new FileResourceId(proof.FileId);
            var resource = await dbContext.FileResources
                .FirstOrDefaultAsync(file => file.Id == fileId, cancellationToken);
            if (!IsWaitingToMove(resource, payload.TenantId))
            {
                continue;
            }

            await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
            // occurredAt es el del mensaje, cuando se guardó el pedido: desde ahí el comprobante es
            // público, y reintentar no mueve la fecha.
            resource.MoveToPublic(proof.PublicStorageKey, record.OccurredAt);
        }

        dbContext.Inbox.Add(new StorageInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Se saltea, y el mensaje se marca igual, lo que nunca va a poder moverse: un archivo que no existe,
    // de otro tenant, un comprobante User (D13), uno que ya no está Available o uno ya movido. Un
    // mensaje así reintentado cada 3 s no arreglaría nada.
    private static bool IsWaitingToMove([NotNullWhen(true)] FileResource? resource, Guid tenantId) =>
        resource is not null
        && resource.TenantId == tenantId
        && resource.OwnerType is FileOwnerType.PaymentProof
        && resource.Status is FileResourceStatus.Available
        && resource.PublicStorageKey is null;

    private sealed record AttachedPayload(Guid TenantId, IReadOnlyList<AttachedProof> Proofs)
    {
        // Por nombre, igual que los demás consumidores del outbox: el payload lo escribe
        // OrderPaymentProofEventPublisher, en Quotations.
        public static AttachedPayload Parse(string payloadJson)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            return new AttachedPayload(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("proofs")
                    .EnumerateArray()
                    .Select(proof => new AttachedProof(
                        proof.GetProperty("fileId").GetGuid(),
                        proof.GetProperty("publicStorageKey").GetString() ?? string.Empty))
                    .ToArray());
        }
    }

    private sealed record AttachedProof(Guid FileId, string PublicStorageKey);
}
```

- [ ] **Step 8: El worker y los registros**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D9. Cada 3 s, como los demás consumidores del outbox: la sección 2 del spec cuenta
// con que entre guardar el pedido y borrar el temporal pasen segundos. Cada tick corre en su propio
// scope para que el DbContext esté fresco.
internal sealed partial class PaymentProofMoveWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentProofMoveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof move tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
using Modules.Storage.Infrastructure.ObjectStorage;
using Modules.Storage.Infrastructure.Persistence;
```

por:

```csharp
using Modules.Storage.Infrastructure.ObjectStorage;
using Modules.Storage.Infrastructure.PaymentProofs;
using Modules.Storage.Infrastructure.Persistence;
```

y reemplaza:

```csharp
        services.AddHostedService<StagingCleanupWorker>();
```

por:

```csharp
        services.AddHostedService<StagingCleanupWorker>();
        // Spec 2026-09-16, D9: borra el temporal de cada comprobante adjuntado y registra el movimiento.
        services.AddScoped<IPaymentProofMoveProcessor, PaymentProofMoveProcessor>();
        services.AddHostedService<PaymentProofMoveWorker>();
```

- [ ] **Step 9: Correr las pruebas de Storage (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado: los dos proyectos con `Con error: 0`; `PaymentProofMoveTests` con 6 superadas y `StorageDbContextMappingTests` con 2. Pega los resúmenes.

- [ ] **Step 10: Correr las de punta a punta (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: `Superado: 19`, `Con error: 0` (las 15 de Task 6 y las 4 nuevas). Pega el resumen.

- [ ] **Step 11: La guía de integración**

En `docs/integracion-cotizaciones-y-pedidos.md`, reemplaza:

```markdown
1. `POST /files` → `{ ownerId, ownerType: "User", name, mimeType, sizeBytes }` → trae `uploadUrl`.
```

por:

```markdown
1. `POST /files` → `{ ownerId, ownerType, name, mimeType, sizeBytes }` → trae `uploadUrl`.
   `ownerType` es `"PaymentProof"` para un comprobante de pago y `"User"` para el PDF de envío.
```

y reemplaza:

```markdown
No hace falta publicar (paso 5 de esa guía). Con `Quotations:PaymentProofs:PublicLinks` encendida,
el backend copia cada comprobante nuevo al bucket público al convertir o al sumar comprobantes, para
que el Excel de pedidos lo enlace; el frontend no hace nada distinto, y la respuesta de la API no
cambia.
```

por:

```markdown
No hace falta publicar (paso 5 de esa guía). Un comprobante `PaymentProof` no se promueve: espera en
`staging/` y, si es imagen, `complete` ya lo deja en WebP de hasta 2000 px, así que el `mimeType` y la
extensión del `name` de su respuesta cambian. Con `Quotations:PaymentProofs:PublicLinks` encendida, el
backend copia cada comprobante nuevo al bucket público al convertir o al sumar comprobantes, para que
el Excel de pedidos lo enlace, y segundos después Storage borra el temporal. Un comprobante `User`
sigue como antes. La respuesta de los endpoints de pedidos no cambia.
```

Lo que devuelve `POST /files/{id}/download-url` para un comprobante movido no se documenta acá: lo construye Task 8, y Task 8 lo documenta (pre-flight C3).

- [ ] **Step 12: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext
```

Esperado: `0 Advertencia(s)`, `0 Errores` y `No changes have been made to the model since the last migration.` Después corre «el chequeo de formato» (excluye `Migrations/`); esperado sin salida.

- [ ] **Step 13: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$migrations = @(git ls-files --others --exclude-standard -- src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations)
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageInboxMessage.cs src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageDbContext.cs src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/Migrations/StorageDbContextModelSnapshot.cs $migrations src/Modules/Storage/Modules.Storage.Infrastructure/Modules.Storage.Infrastructure.csproj src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveProcessor.cs src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageDbContextMappingTests.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofMoveTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs docs/integracion-cotizaciones-y-pedidos.md; git commit -m "feat(storage): mover los comprobantes adjuntados al bucket público" -m "PaymentProofMoveWorker consume quotations.order.payment-proofs-attached.v1 con un inbox nuevo de Storage: primero borra el temporal y después registra MoveToPublic y el inbox en un solo SaveChanges. Los comprobantes User se ignoran (spec 2026-09-16, D9 y D13)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: antes del commit `$migrations` trae los dos archivos de la migración; `git status --short` vacío y el `Select-String` sin salida.

---

### Task 7B: Un comprobante ya movido no se adjunta a otro pedido (D16)

Enmienda D16 (hallazgo 15): desde Task 7 un `PaymentProof` adjunto se mueve y su temporal se borra. Si alguien lo adjunta a otro pedido, `PublicPaymentProofPublisher` copiaría desde esa clave borrada y en R2 el request terminaría en 500. `QuotationFileLookup` lo informa como no disponible y `OrderPaymentProofResolver`, que corre antes de cualquier copia (`ConvertQuotationToOrder.cs:77`; `AddOrderPaymentProofs.cs:82` para un comprobante nuevo y `:90` para el archivo de reemplazo de `develop`), lo rechaza con el código que ya usa. No se inventa un código. Un archivo purgado por D19 (Task 9C) tampoco está `Available`, así que el mismo chequeo lo rechaza.

**Files:**
- Modify: `src/Bootstrapper/QuotationFileLookup.cs:10-38`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs:36-41`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationFileLookupTests.cs` (nuevo)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` (una prueba)

**Interfaces:**
- Consumes: `FileResource.MoveToPublic(string publicStorageKey, DateTimeOffset occurredAt)` (Task 1); `CreateAvailablePaymentProofImageAsync`, `WaitForMovedKeyAsync`, `AssertMovedAsync` como ancla, y `PaymentProofMoveWorker` corriendo en el host de Quotations (Task 7); `InMemoryFileResourceRepository` de `Bootstrapper.UnitTests` (ya existe, `StorageTestDoubles.cs:11-31`).
- Produces: `QuotationFileLookup.FindAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)` devuelve `QuotationFileRef.IsAvailable = false` para un `PaymentProof` con `PublicStorageKey`; para todo lo demás, `Status == Available` como antes. La respuesta al adjuntarlo es 422 `order.payment_proof.file_not_available`. La firma no cambia.

- [ ] **Step 1: La prueba unitaria del adaptador (RED)**

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationFileLookupTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo ve Quotations la disponibilidad de un archivo de Storage para adjuntarlo a un pedido. Desde la
/// enmienda D16 del spec 2026-09-16, un comprobante ya movido al bucket público no está disponible:
/// su temporal ya no existe y la copia al público no tendría de dónde salir.
/// </summary>
public sealed class QuotationFileLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APaymentProofNotYetMovedIsAvailable()
    {
        var proof = AvailablePaymentProof();

        var found = await LookupWith(proof).FindAsync(
            TenantId, proof.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found.IsAvailable);
    }

    [Fact]
    public async Task AMovedPaymentProofIsNotAvailable()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic("payment-proofs/abc.webp", Now);

        var found = await LookupWith(proof).FindAsync(
            TenantId, proof.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.False(found.IsAvailable);
        Assert.Equal("image/webp", found.MimeType);
    }

    // Sólo un PaymentProof: una imagen User publicada también tiene PublicStorageKey, y su original
    // sigue en el bucket privado (D13).
    [Fact]
    public async Task APublishedUserImageIsStillAvailable()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User, "captura.png",
            "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        image.CompleteUpload("checksum", 1024, Now);
        image.Promote($"files/tenants/{TenantId:N}/{Guid.CreateVersion7():N}", Now);
        image.Publish($"tenants/{TenantId:N}/media/captura/original.png", Now);

        var found = await LookupWith(image).FindAsync(
            TenantId, image.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found.IsAvailable);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof, "comprobante.webp",
            "image/webp", 1024, $"staging/tenants/{TenantId:N}/{Guid.CreateVersion7():N}", Now);
        proof.CompleteUpload("checksum", 1024, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static QuotationFileLookup LookupWith(FileResource resource) =>
        new(
            new InMemoryFileResourceRepository(resource),
            new UnusedObjectStorage(),
            Options.Create(new QuotationsOptions()));

    // FindAsync no firma ni lee bytes: cualquier llamada al bucket privado es un error de la prueba.
    private sealed class UnusedObjectStorage : IObjectStorage
    {
        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PromoteAsync(
            string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore --filter "FullyQualifiedName~QuotationFileLookupTests"
```

Esperado (RED de aserción), `Con error: 1, Superado: 2`: `AMovedPaymentProofIsNotAvailable` falla con `Assert.False() Failure` → `Expected: False`, `Actual: True`. Pega la salida.

- [ ] **Step 2: La prueba de punta a punta (RED)**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`, reemplaza:

```csharp
    private static async Task AssertMovedAsync(
```

por:

```csharp
    // D16 (spec 2026-09-16): un comprobante ya movido no se adjunta a otro pedido. Su temporal ya no
    // existe; en R2 la copia fallaría con 500. Se rechaza antes de copiar, con el código de siempre.
    [Fact]
    public async Task AnAlreadyMovedPaymentProofCannotBeAttachedToAnotherOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var firstQuotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, firstQuotation.Id, "FullPaymentReceived", fileId);
        Assert.NotNull(await WaitForMovedKeyAsync(database.GetConnectionString(), fileId));
        var secondQuotation = await NewSentQuotationAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, secondQuotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.file_not_available", problem?.Code);
        // Sólo la copia de la primera conversión: la segunda no copió nada.
        Assert.Single(factory.PublicObjectStorage.Copies);
    }

    private static async Task AssertMovedAsync(
```

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests.AnAlreadyMovedPaymentProofCannotBeAttachedToAnotherOrder"
```

Esperado (RED de aserción), `Con error: 1`: falla con `Assert.Equal() Failure: Values differ` → `Expected: UnprocessableEntity`, `Actual: OK` (el doble del bucket público no falla al copiar desde una clave borrada, así que la segunda conversión pasa y copia). Si falla antes, en `Assert.NotNull` de `WaitForMovedKeyAsync`, el worker de Task 7 no movió el comprobante: **para y pregunta**. Pega la salida.

- [ ] **Step 3: El adaptador y el mensaje del resolver**

En `src/Bootstrapper/QuotationFileLookup.cs`, reemplaza:

```csharp
/// No decide nada: las reglas (PDF vs. comprobante, tamaño máximo, tenant, disponibilidad) son
/// de <c>OrderPaymentProofResolver</c>, en Application.
/// </summary>
```

por:

```csharp
/// No decide las reglas (PDF vs. comprobante, tamaño máximo, tenant, disponibilidad): son de
/// <c>OrderPaymentProofResolver</c>, en Application. Sólo traduce qué significa "disponible" en
/// Storage, y desde el spec 2026-09-16 (D16) un comprobante ya movido no lo está.
/// </summary>
```

Y reemplaza:

```csharp
                resource.SizeBytes,
                resource.Status == FileResourceStatus.Available);
    }
```

por:

```csharp
                resource.SizeBytes,
                IsAvailableToAttach(resource));
    }

    // Spec 2026-09-16, D16: un PaymentProof con PublicStorageKey ya se movió al bucket público y su
    // temporal se borró, así que PublicPaymentProofPublisher no tendría de dónde copiarlo para otro
    // pedido. Sólo un PaymentProof: una imagen User publicada conserva su original privado (D13).
    private static bool IsAvailableToAttach(FileResource resource) =>
        resource.Status == FileResourceStatus.Available
        && !(resource.OwnerType == FileOwnerType.PaymentProof && resource.PublicStorageKey is not null);
```

En `src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs`, reemplaza:

```csharp
        if (!file.IsAvailable)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_available",
                "The payment proof file has not finished uploading yet.");
        }
```

por:

```csharp
        // Dos casos con el mismo código: la subida no terminó, o el comprobante ya se movió al
        // bucket público con otro pedido (spec 2026-09-16, D16).
        if (!file.IsAvailable)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_available",
                "The payment proof file is not available: it has not finished uploading or it was already attached to another order.");
        }
```

Ninguna prueba compara ese mensaje (`git grep "finished uploading" -- tests` sin resultados); `PublicPaymentProofPublisher` conserva el suyo, porque ahí sólo llega un archivo que no terminó de subir.

- [ ] **Step 4: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: los tres con `Con error: 0`; `QuotationFileLookupTests` con 3 superadas; `OrderPaymentProofPublicationApiTests` con `Superado: 20` (las 19 de Task 7 y la nueva). Pega los resúmenes.

- [ ] **Step 5: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Bootstrapper/QuotationFileLookup.cs src/Modules/Quotations/Modules.Quotations.Application/OrderPaymentProofResolver.cs tests/Bootstrapper/Bootstrapper.UnitTests/QuotationFileLookupTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs; git commit -m "feat(orders): un comprobante ya movido no se adjunta a otro pedido" -m "QuotationFileLookup informa como no disponible un PaymentProof con clave pública, y el resolver lo rechaza con order.payment_proof.file_not_available antes de copiar desde un temporal que ya no existe (spec 2026-09-16, D16)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 8: La descarga de un comprobante movido es su URL pública

D5 y sección 3: si el recurso es `PaymentProof`, tiene `PublicStorageKey` y no se pidió variante, `IssueDownloadUrlHandler` devuelve `IPublicObjectStorage.GetUrl(PublicStorageKey)`. Todo lo demás firma como hoy y la auditoría `storage.file.downloaded` se registra igual. Cambio aceptado: el comprobante movido se **abre** en una pestaña en vez de bajarse con su nombre.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs:1-66` (archivo completo)
- Create: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs`
- Modify: `docs/integracion-cotizaciones-y-pedidos.md` (el párrafo de comprobantes que dejó Task 7; pre-flight C3)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs` (nuevo)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDownloadTests.cs` (nuevo)

**Interfaces:**
- Consumes: `FileResource.MoveToPublic(...)` (Task 1); `IPublicObjectStorage.ListAsync` (Task 3, para que el doble compile); el harness de Storage con `CreateAvailableAsync`, `NewPublicKey`, `AddAttachedEventAsync`, `RunMoveAsync`, `InMemoryPublicObjectStorage.BaseUrl` (Tasks 4 y 7).
- Produces:
  - `IssueDownloadUrlHandler(IFileResourceRepository repository, IObjectStorage objectStorage, IPublicObjectStorage publicObjectStorage, IStorageUnitOfWork unitOfWork, IStorageAuditPublisher auditPublisher, IExecutionContext executionContext, IClock clock)`.
  - En `Modules.Storage.UnitTests` (`internal`): `InMemoryFileResourceRepository`, `SigningObjectStorage` (`SignedBaseUrl = "https://r2.test"`), `FixedPublicObjectStorage` (`BaseUrl = "https://assets-qep.example.co"`), `CountingStorageUnitOfWork`, `RecordingStorageAuditPublisher` (`Actions`), `AllowAllExecutionContext`, `FixedClock`. Task 9 le suma `PublishSystem` a `RecordingStorageAuditPublisher`.

- [ ] **Step 1: La prueba de punta a punta (RED)**

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDownloadTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La descarga de un comprobante desde la app (spec 2026-09-16, D5 y sección 3): antes de moverse se
/// firma el temporal como cualquier archivo; ya movido, la URL es la pública, porque el temporal ya
/// no existe.
/// </summary>
public sealed class PaymentProofDownloadTests
{
    [Fact]
    public async Task TheDownloadUrlOfAMovedProofIsItsPublicUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));

        using var response = await client.PostAsync(
            $"{FilesUrl}/{proof.FileId}/download-url", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var download = await response.Content.ReadFromJsonAsync<DownloadUrlPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal($"{InMemoryPublicObjectStorage.BaseUrl}/{publicKey}", download?.Url);
    }

    // Ya pasa antes de esta tarea: protege el camino de siempre mientras el comprobante espera.
    [Fact]
    public async Task TheDownloadUrlOfAProofNotYetMovedIsSignedFromStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        using var response = await client.PostAsync(
            $"{FilesUrl}/{proof.FileId}/download-url", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var download = await response.Content.ReadFromJsonAsync<DownloadUrlPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal($"https://r2.test/{proof.StagingKey}", download?.Url);
    }

    private sealed record DownloadUrlPayload(string Url);
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofDownloadTests"
```

Esperado (RED de aserción), `Con error: 1, Superado: 1`: `TheDownloadUrlOfAMovedProofIsItsPublicUrl` falla con `Assert.Equal() Failure: Strings differ` → `Expected: "https://assets.qep.test/payment-proofs/…"`, `Actual: "https://r2.test/staging/tenants/…"`. Pega la salida.

- [ ] **Step 2: Las pruebas unitarias y sus dobles (RED)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

// Dobles de los puertos de Storage.Application, a mano y sin librería de mocking, como el resto del
// repositorio: devuelven lo sembrado y anotan lo que reciben.

internal sealed class InMemoryFileResourceRepository(params FileResource[] resources) : IFileResourceRepository
{
    public void Add(FileResource resource) => throw new NotSupportedException();

    public Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken) =>
        Task.FromResult(resources.FirstOrDefault(resource => resource.Id == id));

    public Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket privado: sólo firma descargas, con la clave a la vista para que la prueba vea
/// qué se firmó.</summary>
internal sealed class SigningObjectStorage : IObjectStorage
{
    public const string SignedBaseUrl = "https://r2.test";

    public Task<Uri> CreatePresignedUploadUrlAsync(
        string key, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"{SignedBaseUrl}/{key}?signature=test"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task PromoteAsync(
        string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket público: sólo arma URLs.</summary>
internal sealed class FixedPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CountingStorageUnitOfWork : IStorageUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class RecordingStorageAuditPublisher : IStorageAuditPublisher
{
    public List<string> Actions { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);
}

internal sealed class AllowAllExecutionContext(Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => true;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
```

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs`:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// D5 (spec 2026-09-16): la URL pública sólo para un PaymentProof ya movido; el resto se firma como
/// siempre, incluida una imagen User publicada, y la auditoría se registra en los dos casos.
/// </summary>
public sealed class IssueDownloadUrlHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMovedPaymentProofGetsItsPublicUrl()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);
        var audit = new RecordingStorageAuditPublisher();

        var result = await HandlerFor(proof, audit).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal($"{FixedPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf", result.Url);
        Assert.Equal(["storage.file.downloaded"], audit.Actions);
    }

    [Fact]
    public async Task APaymentProofNotYetMovedIsSignedFromStaging()
    {
        var proof = AvailablePaymentProof();
        var audit = new RecordingStorageAuditPublisher();

        var result = await HandlerFor(proof, audit).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal($"{SigningObjectStorage.SignedBaseUrl}/{proof.StorageKey}?signature=test", result.Url);
        Assert.Equal(["storage.file.downloaded"], audit.Actions);
    }

    // Sólo un PaymentProof: una imagen de producto publicada también tiene PublicStorageKey, y su
    // descarga desde la app sigue siendo el original firmado.
    [Fact]
    public async Task APublishedUserImageIsStillSigned()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/producto", Now);
        image.CompleteUpload("checksum", 2048, Now);
        image.Promote($"files/tenants/{TenantId:N}/producto", Now);
        image.Publish($"tenants/{TenantId:N}/media/producto/original.png", Now);

        var result = await HandlerFor(image, new RecordingStorageAuditPublisher()).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(
            $"{SigningObjectStorage.SignedBaseUrl}/files/tenants/{TenantId:N}/producto?signature=test",
            result.Url);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        proof.CompleteUpload("checksum", 2048, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static IssueDownloadUrlHandler HandlerFor(
        FileResource resource, RecordingStorageAuditPublisher audit) =>
        new(
            new InMemoryFileResourceRepository(resource),
            new SigningObjectStorage(),
            new FixedPublicObjectStorage(),
            new CountingStorageUnitOfWork(),
            audit,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~IssueDownloadUrlHandlerTests"
```

Esperado (RED de compilación): `error CS1729: 'IssueDownloadUrlHandler' no contiene un constructor que tome 7 argumentos` (o `does not contain a constructor that takes 7 arguments`). Pega la salida.

- [ ] **Step 3: El handler**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs` por:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

// Es un comando (no una consulta) porque emitir una URL de descarga reevalúa la autorización
// y registra una entrada de auditoría — tiene efecto commiteado en una unidad de trabajo.
public sealed record IssueDownloadUrlCommand(
    Guid TenantId,
    Guid FileResourceId,
    string? Variant = null)
    : ICommand<DownloadUrlDto>;

public sealed class IssueDownloadUrlHandler(
    IFileResourceRepository repository,
    IObjectStorage objectStorage,
    IPublicObjectStorage publicObjectStorage,
    IStorageUnitOfWork unitOfWork,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<IssueDownloadUrlCommand, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> HandleAsync(
        IssueDownloadUrlCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileRead);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Sólo un recurso disponible se puede descargar (invariante de la capacidad).
        resource.EnsureDownloadable();

        string url;
        if (string.IsNullOrWhiteSpace(command.Variant)
            && resource.OwnerType is FileOwnerType.PaymentProof
            && resource.PublicStorageKey is { } publicKey)
        {
            // Spec 2026-09-16, D5: un comprobante movido vive en el bucket público y su temporal ya no
            // existe. Se abre en una pestaña en vez de bajarse con su nombre, porque un objeto público
            // no lleva Content-Disposition por request: es el cambio aceptado en la sección 3, y es lo
            // mismo que hace el enlace «Ver» del Excel.
            url = publicObjectStorage.GetUrl(publicKey);
        }
        else
        {
            var storageKey = string.IsNullOrWhiteSpace(command.Variant)
                ? resource.StorageKey
                : resource.GetVariant(command.Variant).StorageKey;
            // Con el nombre original del recurso, que firma `Content-Disposition: attachment`. Sin
            // el, R2 sirve el objeto sin disposicion y el navegador **abre** el PDF o la imagen en
            // una pestana en vez de bajarlos: el endpoint se llama "download-url" y hasta ahora no
            // descargaba nada. Una variante conserva el nombre del original -- lo que cambia es el
            // tamano, no que archivo es.
            var signed = await objectStorage.CreatePresignedDownloadUrlAsync(
                storageKey, resource.Name, cancellationToken);
            url = signed.AbsoluteUri;
        }

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.downloaded",
            resource.Id.ToString(),
            string.IsNullOrWhiteSpace(command.Variant)
                ? "success"
                : $"success:{command.Variant}",
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DownloadUrlDto(url);
    }
}
```

- [ ] **Step 4: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado: los dos proyectos con `Con error: 0`; `IssueDownloadUrlHandlerTests` con 3 superadas y `PaymentProofDownloadTests` con 2. `StorageFlowTests.UploadScanDownloadDeleteFlowWithObjectStorageDoubleAndAudit` sigue verde: un archivo User se firma como antes. Pega los resúmenes.

- [ ] **Step 5: La guía de integración**

La URL pública de descarga se documenta en el commit que la construye (pre-flight C3). En `docs/integracion-cotizaciones-y-pedidos.md`, reemplaza:

```markdown
el Excel de pedidos lo enlace, y segundos después Storage borra el temporal. Un comprobante `User`
sigue como antes. La respuesta de los endpoints de pedidos no cambia.
```

por:

```markdown
el Excel de pedidos lo enlace, y segundos después Storage borra el temporal. Desde ahí
`POST /files/{id}/download-url` de ese comprobante devuelve la URL pública, que el navegador **abre**
en vez de descargar con el nombre original. Un comprobante `User` sigue como antes. La respuesta de los
endpoints de pedidos no cambia.
```

- [ ] **Step 6: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Application/IssueDownloadUrl.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs tests/Modules/Storage/Modules.Storage.UnitTests/IssueDownloadUrlHandlerTests.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDownloadTests.cs docs/integracion-cotizaciones-y-pedidos.md; git commit -m "feat(storage): URL pública para descargar un comprobante movido" -m "Un PaymentProof con clave pública y sin variante pedida devuelve la URL del bucket público; todo lo demás se sigue firmando y la auditoría no cambia (spec 2026-09-16, D5)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 8B: Índices de `order_payment_proofs` para las sondas (D17)

Enmienda D17 (hallazgo 16): las dos sondas de Quotations que llegan en Tasks 9 y 10 buscan en `order_payment_proofs` por `file_id` (`IFileReferenceProbe`, en cada barrido de staging) y por `public_storage_key` (`IPublicObjectReferenceProbe`, en cada reconciliación). Va antes de Task 9 para que las dos sondas nazcan con su índice. La migración se **genera** con `dotnet ef`, nunca a mano.

**Files:**
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs:117-149`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:425-431`
- Create (generados): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrderPaymentProofReferenceIndexes.cs` y `.Designer.cs`; Modify (regenerado): `QuotationsDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: nada de tareas anteriores. `QuotationsDbContextFactory` y `QuotationsDbContextMappingTests.TheModelHasNoChangesPendingAMigration` (`:157-163`) ya existen.
- Produces: `IX_order_payment_proofs_file` sobre `file_id` e `IX_order_payment_proofs_public_key` sobre `public_storage_key`, en el modelo y en la base. Tasks 9 y 10 citan los nombres en los comentarios de sus sondas.

- [ ] **Step 1: Las pruebas del mapeo (RED)**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, reemplaza:

```csharp
        Assert.Equal("IX_order_payment_proofs_order", Assert.Single(proof.GetIndexes()).GetDatabaseName());
```

por:

```csharp
        Assert.Equal(
            ["IX_order_payment_proofs_file", "IX_order_payment_proofs_order", "IX_order_payment_proofs_public_key"],
            proof.GetIndexes().Select(index => index.GetDatabaseName()!).Order(StringComparer.Ordinal));
```

Y reemplaza:

```csharp
    /// <summary>
    /// La clave de la copia pública de un comprobante (spec 2026-09-15, P5). Nullable, porque los
    /// privados no tienen, y sin índice, porque nadie busca por ella. Sin el mapeo a mano EF la
    /// llamaría "PublicStorageKey", y el error lo vería recién la migración.
    /// </summary>
    [Fact]
    public void OrderPaymentProofPublicStorageKeyMapsToANullableColumnWithoutIndex()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        var property = proof.FindProperty(nameof(OrderPaymentProof.PublicStorageKey))!;

        Assert.Equal("public_storage_key", property.GetColumnName());
        Assert.True(property.IsNullable);
        Assert.Equal(200, property.GetMaxLength());
        Assert.DoesNotContain(proof.GetIndexes(), index => index.Properties.Contains(property));
    }
```

por:

```csharp
    /// <summary>
    /// La clave de la copia pública de un comprobante (spec 2026-09-15, P5). Nullable, porque los
    /// privados no tienen. Sin el mapeo a mano EF la llamaría "PublicStorageKey", y el error lo vería
    /// recién la migración.
    /// </summary>
    [Fact]
    public void OrderPaymentProofPublicStorageKeyMapsToANullableColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var proof = model.FindEntityType(typeof(OrderPaymentProof))!;
        var property = proof.FindProperty(nameof(OrderPaymentProof.PublicStorageKey))!;

        Assert.Equal("public_storage_key", property.GetColumnName());
        Assert.True(property.IsNullable);
        Assert.Equal(200, property.GetMaxLength());
    }

    /// <summary>
    /// Spec 2026-09-16, D17: Storage consulta esta tabla en cada barrido, por archivo
    /// (IFileReferenceProbe) y por clave pública (IPublicObjectReferenceProbe). Un índice por columna,
    /// con nombre fijo: un nombre por convención no lo ve el compilador, lo ve la próxima migración.
    /// </summary>
    [Fact]
    public void OrderPaymentProofsHaveAnIndexForEachReferenceProbe()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var indexes = model.FindEntityType(typeof(OrderPaymentProof))!.GetIndexes().ToArray();

        var byFile = Assert.Single(indexes, index => index.GetDatabaseName() == "IX_order_payment_proofs_file");
        Assert.Equal([nameof(OrderPaymentProof.FileId)], byFile.Properties.Select(property => property.Name));
        Assert.False(byFile.IsUnique);

        var byPublicKey = Assert.Single(
            indexes, index => index.GetDatabaseName() == "IX_order_payment_proofs_public_key");
        Assert.Equal(
            [nameof(OrderPaymentProof.PublicStorageKey)], byPublicKey.Properties.Select(property => property.Name));
        Assert.False(byPublicKey.IsUnique);
    }
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado (RED de aserción), `Con error: 2`: `OrdersMapToTheirRenamedTablesColumnsIndexesAndConstraints` con `Assert.Equal() Failure: Collections differ` (sólo trae `IX_order_payment_proofs_order`) y `OrderPaymentProofsHaveAnIndexForEachReferenceProbe` con `Assert.Single() Failure: The collection did not contain any matching items`. Las demás pasan, `TheModelHasNoChangesPendingAMigration` incluida. Pega la salida.

- [ ] **Step 2: Los índices en el modelo (RED de la migración pendiente)**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`, reemplaza:

```csharp
        // La clave de la copia pública (spec 2026-09-15, P5): nullable, porque los comprobantes
        // privados no tienen, y sin índice, porque nadie busca por ella. La clave mide 51
        // caracteres (`payment-proofs/` + 32 hex + extensión); 200 deja margen.
        proof.Property(value => value.PublicStorageKey)
            .HasColumnName("public_storage_key")
            .HasMaxLength(200);
        proof.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_payment_proofs_order");
```

por:

```csharp
        // La clave de la copia pública (spec 2026-09-15, P5): nullable, porque los comprobantes
        // privados no tienen. La clave mide 51 caracteres (`payment-proofs/` + 32 hex + extensión);
        // 200 deja margen.
        proof.Property(value => value.PublicStorageKey)
            .HasColumnName("public_storage_key")
            .HasMaxLength(200);
        proof.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_payment_proofs_order");
        // Spec 2026-09-16, D17: Storage pregunta por esta tabla en cada barrido. El de staging busca
        // por archivo (IFileReferenceProbe) y la reconciliación del bucket público por clave
        // (IPublicObjectReferenceProbe). Sin estos índices, cada pregunta recorre la tabla entera.
        proof.HasIndex(value => value.FileId).HasDatabaseName("IX_order_payment_proofs_file");
        proof.HasIndex(value => value.PublicStorageKey).HasDatabaseName("IX_order_payment_proofs_public_key");
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado (RED de aserción), `Con error: 1`: sólo `TheModelHasNoChangesPendingAMigration`, con `Assert.False() Failure` → `Expected: False`, `Actual: True`. Las dos del Step 1 ya pasan. Pega la salida.

- [ ] **Step 3: Generar la migración (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddOrderPaymentProofReferenceIndexes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_AddOrderPaymentProofReferenceIndexes.cs" | Select-String -Pattern 'IX_order_payment_proofs_file', 'IX_order_payment_proofs_public_key', 'CreateIndex', 'AddColumn', 'AlterColumn', 'DropIndex', 'CreateTable'
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: `Done.`; `git status` con dos `??` (`<timestamp>_AddOrderPaymentProofReferenceIndexes.cs` y `.Designer.cs`) y ` M QuotationsDbContextModelSnapshot.cs`; el `Select-String` encuentra los dos nombres de índice y `CreateIndex` (en `Up`) y `DropIndex` (en `Down`), y **ninguna** línea con `AddColumn`, `AlterColumn` ni `CreateTable`; la prueba con `Con error: 0`. Si la migración toca otra tabla u otra columna, **para y pregunta**: el snapshot estaba desactualizado. Pega las salidas.

- [ ] **Step 4: La migración contra Postgres**

Las pruebas de integración de Quotations aplican las migraciones al arrancar el host. Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: `Modules.Quotations.UnitTests` entero con `Con error: 0`; `OrderPaymentProofPublicationApiTests` con `Superado: 20`, `Con error: 0`. Pega los resúmenes.

- [ ] **Step 5: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: `0 Advertencia(s)`, `0 Errores` y `No changes have been made to the model since the last migration.` Después corre «el chequeo de formato» (excluye `Migrations/`); esperado sin salida.

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$migrations = @(git ls-files --others --exclude-standard -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations)
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs $migrations tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs; git commit -m "feat(orders): índices de las sondas de comprobantes" -m "IX_order_payment_proofs_file e IX_order_payment_proofs_public_key, para que el barrido de staging y la reconciliación del bucket público no recorran order_payment_proofs en cada pregunta (spec 2026-09-16, D17)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: antes del commit `$migrations` trae los dos archivos de la migración; `git status --short` vacío y el `Select-String` sin salida.

---

### Task 9: El barrido purga los comprobantes que nadie adjuntó

D11 y sección 3: `StagingCleanupWorker`, con el mismo reloj y `StagingRetentionHours`, suma una segunda búsqueda: `PaymentProof`, `Available`, sin `PublicStorageKey` y con `CreatedAt` anterior al corte. Cada uno se consulta contra `IFileReferenceProbe` (BuildingBlocks; Quotations la implementa); el referenciado se deja, y el que no, se borra de `staging/`, se marca `Purged` y se audita `storage.file.purged` con `payment_proof_not_attached`. El barrido sale del `BackgroundService` a un procesador para poder probarlo (hallazgo 7).

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Application/IFileReferenceProbe.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofFileReferenceProbe.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:51`
- Modify: `src/Modules/Storage/Modules.Storage.Application/IStorageAuditPublisher.cs:1-17` (archivo completo)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageAuditPublisher.cs:1-54` (archivo completo)
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupProcessor.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupWorker.cs:1-68` (archivo completo)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:54`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs` (`RecordingStorageAuditPublisher`)
- Modify: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` (usings, sonda de prueba y helpers)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStagingCleanupTests.cs` (nuevo)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs` (nuevo)

**Interfaces:**
- Consumes: `FileResource.PurgeUnattachedPaymentProof(DateTimeOffset occurredAt)` (Task 1); el harness de Storage (Tasks 4 y 7); el índice `IX_order_payment_proofs_file` (Task 8B).
- Produces:
  - `public interface IFileReferenceProbe { string Source { get; } Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken); }` en `BuildingBlocks.Application`. Tasks 9B, 9C y 10 la usan.
  - `internal sealed class OrderPaymentProofFileReferenceProbe(QuotationsDbContext dbContext) : IFileReferenceProbe` con `Source => "quotations"`.
  - `void IStorageAuditPublisher.PublishSystem(Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt)` (actor `System`, `Guid.Empty`). Tasks 9C y 10 lo usan.
  - `internal interface IStagingCleanupProcessor { Task CleanupAsync(CancellationToken cancellationToken); }` y `internal sealed partial class StagingCleanupProcessor` en `Modules.Storage.Infrastructure.ObjectStorage`.
  - En el harness de Storage: `StorageApiFactory.FileReferences` (`FixedFileReferenceProbe` con `Reference(Guid fileId)`), `BackdateAsync(string connectionString, Guid fileId, TimeSpan age)`, `RunStagingCleanupAsync(StorageApiFactory)`, `AuditEventsAsync(StorageApiFactory)` → `IReadOnlyList<AuditPayload>` y el record `AuditPayload(Guid? TenantId, string ActorType, string Action, string ResourceType, string ResourceId, string Outcome)`. Tasks 9C y 10 usan `AuditEventsAsync`; Tasks 9B y 9C usan `FileReferences`.
  - `PaymentProofReferenceProbeTests` con sus helpers privados `NewSentQuotationAsync` y `ConvertAsync`. Task 10 le suma una prueba.

- [ ] **Step 1: La sonda y su prueba contra Postgres (RED)**

Crea `src/BuildingBlocks/BuildingBlocks.Application/IFileReferenceProbe.cs`:

```csharp
namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo declara que todavía referencia un archivo de Storage. Storage la consulta antes de
/// purgar un comprobante de pago que sigue en staging/ (spec 2026-09-16, D11) y no lo purga mientras
/// alguna sonda responda <c>true</c>: un comprobante adjunto cuyo movimiento todavía no se procesó
/// está referenciado aunque no tenga clave pública.
/// </summary>
/// <remarks>
/// Mismo diseño que <see cref="IUserReferenceProbe"/>: vive en BuildingBlocks para que Storage decida
/// sin referenciar a los módulos de negocio, y cada módulo registra la suya sin referenciar a Storage.
/// </remarks>
public interface IFileReferenceProbe
{
    /// <summary>Nombre del módulo que responde, para el log de por qué se retuvo el archivo.</summary>
    string Source { get; }

    Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken);
}
```

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs`:

```csharp
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las sondas que Quotations le responde a Storage (spec 2026-09-16), contra Postgres y resueltas del
/// contenedor real, como las consulta Storage: sin su registro, los barridos de Storage no ven los
/// pedidos.
/// </summary>
public sealed class PaymentProofReferenceProbeTests
{
    // D11: el archivo de un comprobante adjunto está referenciado; uno que ningún pedido usa, no.
    [Fact]
    public async Task TheFileReferenceProbeSeesOnlyAttachedProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var attachedFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var looseFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, quotation.Id, attachedFileId);

        await using var scope = factory.Services.CreateAsyncScope();
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IFileReferenceProbe>(),
            candidate => candidate.Source == "quotations");
        Assert.True(await probe.HasReferencesAsync(attachedFileId, TestContext.Current.CancellationToken));
        Assert.False(await probe.HasReferencesAsync(looseFileId, TestContext.Current.CancellationToken));
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        return await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
    }

    private static async Task<OrderResponse> ConvertAsync(
        HttpClient client, Guid tenantId, Guid quotationId, Guid proofFileId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofReferenceProbeTests"
```

Esperado (RED de aserción): `Con error: 1`; `TheFileReferenceProbeSeesOnlyAttachedProofs` falla con `Assert.Single() Failure: The collection did not contain any matching items` (nadie registra la sonda). Pega la salida.

- [ ] **Step 2: El harness y las pruebas del barrido (RED)**

En `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`, reemplaza:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
```

por:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
```

Reemplaza:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.PaymentProofs;
```

por:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.ObjectStorage;
using Modules.Storage.Infrastructure.PaymentProofs;
```

Reemplaza:

```csharp
    private const string Permissions = "storage.file.upload,storage.file.read,storage.file.delete";
```

por:

```csharp
    private const string Permissions = "storage.file.upload,storage.file.read,storage.file.delete";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
```

Reemplaza:

```csharp
    public static string NewPublicKey(string extension) =>
```

por:

```csharp
    /// <summary>Corre hacia atrás la creación de un archivo, directo en la base: la retención es de
    /// horas y el reloj del host no se corre por prueba.</summary>
    public static async Task BackdateAsync(string connectionString, Guid fileId, TimeSpan age)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET created_at = @createdAt WHERE id = @id", connection);
        command.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow - age);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Un tick del barrido de staging, a mano: el worker espera StagingCleanupMinutes antes
    /// del primero.</summary>
    public static async Task RunStagingCleanupAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStagingCleanupProcessor>()
            .CleanupAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Las auditorías que Storage dejó en el outbox de plataforma.</summary>
    public static async Task<IReadOnlyList<AuditPayload>> AuditEventsAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        var payloads = await dbContext.Outbox
            .AsNoTracking()
            .Where(message => message.EventName == "platform.audit.recorded.v1")
            .Select(message => message.PayloadJson)
            .ToListAsync(TestContext.Current.CancellationToken);
        return payloads
            .Select(payload => JsonSerializer.Deserialize<AuditPayload>(payload, Json)!)
            .ToArray();
    }

    public static string NewPublicKey(string extension) =>
```

Reemplaza:

```csharp
internal sealed record ProblemPayload(string? Code);
```

por:

```csharp
internal sealed record ProblemPayload(string? Code);

internal sealed record AuditPayload(
    Guid? TenantId,
    string ActorType,
    string Action,
    string ResourceType,
    string ResourceId,
    string Outcome);

/// <summary>La sonda de otro módulo, a mano: retiene los archivos que la prueba marca. Se suma a las
/// sondas reales del host (GetServices las devuelve todas).</summary>
internal sealed class FixedFileReferenceProbe : IFileReferenceProbe
{
    private readonly ConcurrentDictionary<Guid, bool> _referenced = new();

    public string Source => "test";

    public void Reference(Guid fileId) => _referenced[fileId] = true;

    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_referenced.ContainsKey(fileId));
}
```

Reemplaza:

```csharp
    public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
```

por:

```csharp
    public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new();

    public FixedFileReferenceProbe FileReferences { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
```

Y reemplaza:

```csharp
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);

```

por:

```csharp
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
            services.AddSingleton<IFileReferenceProbe>(FileReferences);

```

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStagingCleanupTests.cs`:

```csharp
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El barrido de staging (spec 2026-09-16, D11 y sección 3): sigue purgando las subidas abandonadas,
/// y ahora también el comprobante Available, sin mover y viejo que ningún módulo referencia. Respeta
/// el adjunto que espera su movimiento, el reciente, el movido y los archivos User.
/// </summary>
public sealed class PaymentProofStagingCleanupTests
{
    private static readonly TimeSpan OlderThanTheRetention = TimeSpan.FromHours(25);

    // Lo de siempre, que el procesador nuevo no puede romper.
    [Fact]
    public async Task AnAbandonedUploadIsStillPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var abandoned = await UploadAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), abandoned.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), abandoned.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(abandoned.StagingKey));
    }

    [Fact]
    public async Task AnUnattachedProofOlderThanTheRetentionIsPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(proof.FileId.ToString(), audit.ResourceId);
        Assert.Equal("file", audit.ResourceType);
        Assert.Equal("payment_proof_not_attached", audit.Outcome);
        Assert.Equal("System", audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // Adjunto, con el evento de D9 todavía sin procesar: la sonda lo retiene.
    [Fact]
    public async Task AnAttachedProofWaitingToBeMovedIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);
        factory.FileReferences.Reference(proof.FileId);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task ARecentUnattachedProofIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    [Fact]
    public async Task AMovedProofIsNotPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
    }

    // D13: un archivo User disponible no es asunto del barrido.
    [Fact]
    public async Task AUserFileIsNotSwept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), file.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        var row = await ReadFileAsync(database.GetConnectionString(), file.FileId);
        Assert.Equal("Available", row.Status);
        Assert.True(factory.ObjectStorage.Exists(row.StorageKey));
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado (RED de compilación): `error CS0246: … 'IStagingCleanupProcessor' …`. Pega la salida.

- [ ] **Step 3: La auditoría de sistema**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/IStorageAuditPublisher.cs` por:

```csharp
namespace Modules.Storage.Application;

// Auditoría operativa (ADR 0019, camino de outbox): acumula un evento de auditoría para
// commitear con la operación de archivo en la misma unidad de trabajo; el worker de
// proyección del módulo Audit lo escribe en audit.entries. Storage usa el camino de outbox
// (y no el IAuditRecorder atómico, que está ligado al DbContext de un productor) porque sus
// operaciones son operativas, no críticas-de-seguridad-síncronas.
public interface IStorageAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);

    // Spec 2026-09-16: lo que hace un proceso sin persona detrás, como los barridos de Storage.
    // actorType System y actorId vacío, el sentinela de sistema del repositorio
    // (QuotationExpirationProcessor). tenantId es null cuando lo afectado no es de un tenant, como
    // un objeto huérfano del bucket público.
    void PublishSystem(
        Guid? tenantId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}
```

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageAuditPublisher.cs` por:

```csharp
using System.Text.Json;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Persistence;

// Auditoría operativa (ADR 0019): acumula un evento platform.audit.recorded.v1 en la
// proyección de outbox de StorageDbContext para que commitee atómico con la operación de
// archivo. El worker de proyección del módulo Audit lo escribe en audit.entries.
internal sealed class StorageAuditPublisher(StorageDbContext dbContext) : IStorageAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt) =>
        Add(
            JsonSerializer.Serialize(new AuditEventPayload(
                tenantId,
                actorId,
                "Human",
                action,
                "file",
                resourceId,
                outcome,
                [],
                "storage",
                occurredAt)),
            occurredAt);

    public void PublishSystem(
        Guid? tenantId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt) =>
        Add(
            JsonSerializer.Serialize(new SystemAuditEventPayload(
                tenantId,
                Guid.Empty,
                "System",
                action,
                resourceType,
                resourceId,
                outcome,
                [],
                "storage",
                occurredAt)),
            occurredAt);

    private void Add(string payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });

    private sealed record AuditEventPayload(
        Guid tenantId,
        Guid actorId,
        string actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        string source,
        DateTimeOffset occurredAt);

    // tenantId nullable: AuditProjectionWorker lo acepta ausente o null.
    private sealed record SystemAuditEventPayload(
        Guid? tenantId,
        Guid actorId,
        string actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        string source,
        DateTimeOffset occurredAt);
}
```

En `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs`, reemplaza:

```csharp
    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);
}
```

por:

```csharp
    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);

    public void PublishSystem(
        Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);
}
```

- [ ] **Step 4: El procesador del barrido y el worker**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupProcessor.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.ObjectStorage;

internal interface IStagingCleanupProcessor
{
    Task CleanupAsync(CancellationToken cancellationToken);
}

// El barrido de staging/, fuera del BackgroundService para poder probarlo sin esperar al
// temporizador, mismo criterio que QuotationExpirationProcessor. Dos búsquedas con el mismo reloj y
// la misma retención: las subidas abandonadas de siempre y, desde el spec 2026-09-16 (D11), los
// comprobantes de pago que siguen en staging/ sin que nadie los adjunte.
internal sealed partial class StagingCleanupProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IStorageAuditPublisher auditPublisher,
    IEnumerable<IFileReferenceProbe> probes,
    IClock clock,
    IOptions<StorageOptions> options,
    ILogger<StagingCleanupProcessor> logger) : IStagingCleanupProcessor
{
    private const int BatchSize = 100;

    private readonly IReadOnlyList<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} kept in staging: still referenced by {Source}.")]
    private static partial void LogProofRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} purged from staging: no module references it.")]
    private static partial void LogProofPurged(ILogger logger, Guid fileId);

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddHours(-options.Value.StagingRetentionHours);
        await PurgeAbandonedUploadsAsync(cutoff, cancellationToken);
        await PurgeUnattachedPaymentProofsAsync(cutoff, cancellationToken);
    }

    private async Task PurgeAbandonedUploadsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var abandoned = await dbContext.FileResources
            .Where(file => file.Status == FileResourceStatus.PendingUpload)
            .Where(file => file.CreatedAt < cutoff)
            .OrderBy(file => file.CreatedAt)
            .Take(BatchSize)
            .ToArrayAsync(cancellationToken);

        foreach (var file in abandoned)
        {
            await objectStorage.DeleteAsync(file.StorageKey, cancellationToken);
            file.PurgeAbandonedUpload(clock.UtcNow);
        }
        if (abandoned.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PurgeUnattachedPaymentProofsAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Un comprobante retenido sigue cumpliendo el filtro en la vuelta siguiente, así que se saltean
        // los ya vistos: sin esto, cien retenidos viejos (por ejemplo, con
        // Quotations:PaymentProofs:PublicLinks apagada, donde nunca se mueven) taparían para siempre a
        // los huérfanos más nuevos. Los purgados salen del filtro al guardar cada lote.
        var retained = 0;
        while (true)
        {
            var batch = await dbContext.FileResources
                .Where(file => file.OwnerType == FileOwnerType.PaymentProof)
                .Where(file => file.Status == FileResourceStatus.Available)
                .Where(file => file.PublicStorageKey == null)
                .Where(file => file.CreatedAt < cutoff)
                .OrderBy(file => file.CreatedAt)
                .ThenBy(file => file.Id)
                .Skip(retained)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);

            foreach (var file in batch)
            {
                var retainedBy = await FindRetainingSourceAsync(file.Id.Value, cancellationToken);
                if (retainedBy is not null)
                {
                    // Un comprobante adjunto cuyo evento de D9 todavía no se procesó.
                    LogProofRetained(logger, file.Id.Value, retainedBy);
                    retained++;
                    continue;
                }

                // Mismo orden que el movimiento (D9): primero el objeto, después la fila. Si guardar
                // falla, el tick siguiente borra una clave que ya no existe y guarda.
                await objectStorage.DeleteAsync(file.StorageKey, cancellationToken);
                var now = clock.UtcNow;
                file.PurgeUnattachedPaymentProof(now);
                auditPublisher.PublishSystem(
                    file.TenantId,
                    "storage.file.purged",
                    "file",
                    file.Id.ToString(),
                    "payment_proof_not_attached",
                    now);
                LogProofPurged(logger, file.Id.Value);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (batch.Length < BatchSize)
            {
                return;
            }
        }
    }

    // Secuencial y cortando en la primera que retiene, igual que OrphanUserCleanupWorker.
    private async Task<string?> FindRetainingSourceAsync(Guid fileId, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(fileId, cancellationToken))
            {
                return probe.Source;
            }
        }

        return null;
    }
}
```

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupWorker.cs` por:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure.ObjectStorage;

// Temporizador del barrido de staging/. La lógica vive en StagingCleanupProcessor (spec 2026-09-16,
// D11), que las pruebas de integración invocan directo. Cada tick corre en su propio scope para que
// el DbContext esté fresco.
internal sealed partial class StagingCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<StagingCleanupWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Storage staging cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.StagingCleanupMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IStagingCleanupProcessor>();
                await processor.CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
    }
}
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddHostedService<StagingCleanupWorker>();
```

por:

```csharp
        // Spec 2026-09-16, D11: el barrido sale del worker para poder probarlo.
        services.AddScoped<IStagingCleanupProcessor, StagingCleanupProcessor>();
        services.AddHostedService<StagingCleanupWorker>();
```

- [ ] **Step 5: La sonda de Quotations**

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofFileReferenceProbe.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Quotations retiene un archivo mientras algún comprobante de pago lo referencia (spec 2026-09-16,
/// D11), sea cual sea el estado del pedido. Storage la consulta antes de purgar un comprobante que
/// sigue en staging/: si responde true, el comprobante está adjunto y sólo falta que se procese su
/// movimiento. La consulta usa el índice <c>IX_order_payment_proofs_file</c> (D17).
/// </summary>
internal sealed class OrderPaymentProofFileReferenceProbe(QuotationsDbContext dbContext)
    : IFileReferenceProbe
{
    public string Source => "quotations";

    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        dbContext.OrderPaymentProofs.AnyAsync(proof => proof.FileId == fileId, cancellationToken);
}
```

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IUserReferenceProbe, QuotationUserReferenceProbe>();
```

por:

```csharp
        services.AddScoped<IUserReferenceProbe, QuotationUserReferenceProbe>();
        // Sonda que Storage consulta antes de purgar un comprobante en staging (spec 2026-09-16, D11).
        services.AddScoped<IFileReferenceProbe, OrderPaymentProofFileReferenceProbe>();
```

- [ ] **Step 6: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofReferenceProbeTests"
```

Esperado: los tres con `Con error: 0`; `PaymentProofStagingCleanupTests` con 6 superadas y `PaymentProofReferenceProbeTests` con 1. Pega los resúmenes.

- [ ] **Step 7: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)` y `0 Errores`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/BuildingBlocks/BuildingBlocks.Application/IFileReferenceProbe.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofFileReferenceProbe.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs src/Modules/Storage/Modules.Storage.Application/IStorageAuditPublisher.cs src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/StorageAuditPublisher.cs src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupProcessor.cs src/Modules/Storage/Modules.Storage.Infrastructure/ObjectStorage/StagingCleanupWorker.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStagingCleanupTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs; git commit -m "feat(storage): purgar los comprobantes que nadie adjuntó" -m "El barrido de staging también purga los PaymentProof Available, sin mover y viejos que ninguna IFileReferenceProbe retiene, y los audita como storage.file.purged / payment_proof_not_attached. Quotations responde la sonda con sus comprobantes (spec 2026-09-16, D11)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 9B: Borrar, despublicar y publicar un comprobante (D15)

Enmienda D15 (hallazgo 13): `SoftDeleteFileHandler` y `UnpublishFileHandler` borran el objeto de `PublicStorageKey`, que en un comprobante movido es la copia que enlaza el Excel: la evidencia del pago. Con D15 los dos rechazan un `PaymentProof` que algún módulo referencia (`IFileReferenceProbe`, Task 9), y `PublishFileHandler` rechaza siempre un `PaymentProof`, los tres con `storage.file.invalid_state` y **antes** de tocar el bucket. Un `PaymentProof` sin referencia y cualquier otro archivo siguen como hoy. Corrige también `README.md:963-964`.

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/PaymentProofGuard.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs:1-59` (archivo completo)
- Modify: `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs:1-115` (archivo completo)
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs` (dos dobles al final)
- Modify: `README.md:963-964`
- Modify: `src/Bootstrapper/PublicPaymentProofPublisher.cs:14-19` (el resumen de la clase; pre-flight C4)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs` (nuevo)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofFileManagementApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IFileReferenceProbe` (Task 9); `FileResource.MoveToPublic` (Task 1); el resumen de `PublicPaymentProofPublisher` que dejó Task 5 (`:8-19`, sin cambios en esa tarea); del harness de Storage `CreateAvailableAsync`, `Pdf`, `NewPublicKey`, `AddAttachedEventAsync`, `RunMoveAsync`, `ReadFileAsync`, `FilesUrl`, `ProblemPayload`, `InMemoryPublicObjectStorage.Put`/`Exists`/`DeletedKeys` (Tasks 4 y 7) y `StorageApiFactory.FileReferences` (Task 9); de los dobles unitarios `InMemoryFileResourceRepository`, `CountingStorageUnitOfWork`, `RecordingStorageAuditPublisher`, `AllowAllExecutionContext` y `FixedClock` (Tasks 8 y 9).
- Produces:
  - `internal static class PaymentProofGuard` en `Modules.Storage.Application`: `Task EnsureNotReferencedAsync(FileResource resource, IEnumerable<IFileReferenceProbe> probes, CancellationToken cancellationToken)` (no hace nada si el recurso no es `PaymentProof`; si alguna sonda responde `true`, lanza `StorageDomainException` `storage.file.invalid_state`) y `void EnsureNotPaymentProof(FileResource resource)` (lanza el mismo código para cualquier `PaymentProof`).
  - `SoftDeleteFileHandler(IFileResourceRepository repository, IStorageUnitOfWork unitOfWork, IPublicObjectStorage publicStorage, IEnumerable<IFileReferenceProbe> fileReferenceProbes, IStorageAuditPublisher auditPublisher, IExecutionContext executionContext, IClock clock)`.
  - `UnpublishFileHandler(IFileResourceRepository repository, IStorageUnitOfWork unitOfWork, IPublicObjectStorage publicStorage, IEnumerable<IFileReferenceProbe> fileReferenceProbes, IStorageAuditPublisher auditPublisher, IExecutionContext executionContext, IClock clock)`.
  - `PublishFileHandler` no cambia de firma. Los tres se registran en `QepServiceCollectionExtensions.cs:108-115` sin cambios: el contenedor resuelve `IEnumerable<IFileReferenceProbe>` con las sondas registradas.
  - En `Modules.Storage.UnitTests` (`internal`): `RecordingPublicObjectStorage` (`Copies`, `DeletedKeys`) y `StubFileReferenceProbe(bool referenced)` (`Asked`).

- [ ] **Step 1: La prueba de punta a punta (RED)**

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofFileManagementApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Borrar un comprobante movido desde la API de Storage (spec 2026-09-16, D15): si un pedido lo
/// referencia, su copia pública es la evidencia del pago que enlaza el Excel y no se toca; si nadie
/// lo referencia, se borra como cualquier archivo publicado.
/// </summary>
public sealed class PaymentProofFileManagementApiTests
{
    [Fact]
    public async Task DeletingAMovedProofThatAnOrderReferencesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        factory.FileReferences.Reference(fileId);

        using var response = await client.DeleteAsync(
            $"{FilesUrl}/{fileId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("storage.file.invalid_state", problem?.Code);
        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.True(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
    }

    // Lo de siempre: sin referencia, borrar un archivo publicado borra su copia. Ya pasa antes de esta
    // tarea.
    [Fact]
    public async Task DeletingAMovedProofThatNoOrderReferencesWorksAsBefore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);

        using var response = await client.DeleteAsync(
            $"{FilesUrl}/{fileId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Deleted", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
    }

    // Un comprobante v2 ya movido, con su copia en el bucket público en memoria.
    private static async Task<(Guid FileId, string PublicKey)> MovedProofAsync(
        HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return (proof.FileId, publicKey);
    }
}
```

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofFileManagementApiTests"
```

Esperado (RED de aserción), `Con error: 1, Superado: 1`: `DeletingAMovedProofThatAnOrderReferencesIsRejected` falla con `Assert.Equal() Failure: Values differ` → `Expected: UnprocessableEntity`, `Actual: OK`; la otra pasa. Pega la salida.

- [ ] **Step 2: Las pruebas unitarias de los tres handlers (RED)**

En `tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs`, reemplaza:

```csharp
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
```

por:

```csharp
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>El bucket público que anota copias (clave pública → clave privada) y borrados, para ver
/// qué tocó un handler (spec 2026-09-16, D15).</summary>
internal sealed class RecordingPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
    {
        Copies[publicKey] = privateKey;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>La sonda de otro módulo, con una respuesta fija, y los archivos por los que le preguntaron.</summary>
internal sealed class StubFileReferenceProbe(bool referenced) : IFileReferenceProbe
{
    public string Source => "test";

    public List<Guid> Asked { get; } = [];

    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken)
    {
        Asked.Add(fileId);
        return Task.FromResult(referenced);
    }
}
```

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-09-16, D15: borrar o despublicar un comprobante que algún pedido referencia se rechaza sin
/// tocar su copia pública, y publicar un comprobante se rechaza siempre. Un comprobante sin referencia
/// y un archivo User siguen como antes.
/// </summary>
public sealed class PaymentProofFileManagementTests
{
    private const string PublicKey = "payment-proofs/abc.webp";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            DeleteHandler(proof, storage, unitOfWork, new StubFileReferenceProbe(referenced: true))
                .HandleAsync(new SoftDeleteFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task DeletingAnUnreferencedPaymentProofWorksAsBefore()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();

        var result = await DeleteHandler(
                proof, storage, new CountingStorageUnitOfWork(), new StubFileReferenceProbe(referenced: false))
            .HandleAsync(new SoftDeleteFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.True(result.Deleted);
        Assert.Equal([PublicKey], storage.DeletedKeys);
        Assert.Null(proof.PublicStorageKey);
        Assert.Equal(FileResourceStatus.Deleted, proof.Status);
    }

    [Fact]
    public async Task UnpublishingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            UnpublishHandler(proof, storage, unitOfWork, new StubFileReferenceProbe(referenced: true))
                .HandleAsync(new UnpublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishingAnUnreferencedPaymentProofWorksAsBefore()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();

        await UnpublishHandler(
                proof, storage, new CountingStorageUnitOfWork(), new StubFileReferenceProbe(referenced: false))
            .HandleAsync(new UnpublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal([PublicKey], storage.DeletedKeys);
        Assert.Null(proof.PublicStorageKey);
    }

    // Un comprobante sólo llega al público por el movimiento (sección 2). Una imagen, para que el
    // rechazo no sea el de «sólo imágenes» de FileResource.Publish.
    [Fact]
    public async Task PublishingAPaymentProofIsAlwaysRejected()
    {
        var proof = AvailablePaymentProof();
        var storage = new RecordingPublicObjectStorage();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            new PublishFileHandler(
                    new InMemoryFileResourceRepository(proof),
                    new CountingStorageUnitOfWork(),
                    storage,
                    new RecordingStorageAuditPublisher(),
                    new AllowAllExecutionContext(TenantId),
                    new FixedClock(Now))
                .HandleAsync(new PublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.Copies);
        Assert.Null(proof.PublicStorageKey);
    }

    // D13: la guarda es sólo para comprobantes. A una imagen User no se le pregunta a ninguna sonda.
    [Fact]
    public async Task AUserImageIsDeletedWithoutAskingTheProbes()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/producto", Now);
        image.CompleteUpload("checksum", 2048, Now);
        image.Promote($"files/tenants/{TenantId:N}/producto", Now);
        var imageKey = $"tenants/{TenantId:N}/media/producto/original.png";
        image.Publish(imageKey, Now);
        var storage = new RecordingPublicObjectStorage();
        var probe = new StubFileReferenceProbe(referenced: true);

        var result = await DeleteHandler(image, storage, new CountingStorageUnitOfWork(), probe)
            .HandleAsync(new SoftDeleteFileCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken);

        Assert.True(result.Deleted);
        Assert.Equal([imageKey], storage.DeletedKeys);
        Assert.Empty(probe.Asked);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.webp", "image/webp", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        proof.CompleteUpload("checksum", 2048, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static FileResource MovedPaymentProof()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic(PublicKey, Now);
        return proof;
    }

    private static SoftDeleteFileHandler DeleteHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            storage,
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));

    private static UnpublishFileHandler UnpublishHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            storage,
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofFileManagementTests"
```

Esperado (RED de compilación): `error CS1729: 'SoftDeleteFileHandler' no contiene un constructor que tome 7 argumentos` y lo mismo para `UnpublishFileHandler` (o `does not contain a constructor that takes 7 arguments`). Pega la salida.

- [ ] **Step 3: La guarda**

Crea `src/Modules/Storage/Modules.Storage.Application/PaymentProofGuard.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

// Spec 2026-09-16, D15. La copia pública de un comprobante movido es la que enlaza el Excel de
// pedidos: la evidencia del pago. Los endpoints genéricos de Storage no la pueden borrar mientras un
// pedido lo referencie, y un comprobante nunca se publica por ellos: sólo llega al público por el
// movimiento de la sección 2. Vive en Application y no en FileResource porque quién referencia un
// archivo lo sabe cada módulo, por IFileReferenceProbe.
internal static class PaymentProofGuard
{
    public static async Task EnsureNotReferencedAsync(
        FileResource resource,
        IEnumerable<IFileReferenceProbe> probes,
        CancellationToken cancellationToken)
    {
        if (resource.OwnerType is not FileOwnerType.PaymentProof)
        {
            return;
        }

        // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
        foreach (var probe in probes)
        {
            if (await probe.HasReferencesAsync(resource.Id.Value, cancellationToken))
            {
                throw new StorageDomainException(
                    "storage.file.invalid_state",
                    $"The payment proof is still referenced by {probe.Source} and cannot be deleted or unpublished.");
            }
        }
    }

    public static void EnsureNotPaymentProof(FileResource resource)
    {
        if (resource.OwnerType is FileOwnerType.PaymentProof)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "A payment proof reaches the public bucket only when it is attached to an order.");
        }
    }
}
```

- [ ] **Step 4: Los tres handlers**

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs` por:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record SoftDeleteFileCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<SoftDeleteResult>;

public sealed class SoftDeleteFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SoftDeleteFileCommand, SoftDeleteResult>
{
    public async Task<SoftDeleteResult> HandleAsync(
        SoftDeleteFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileDelete);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        var now = clock.UtcNow;
        if (resource.PublicStorageKey is { } publicKey)
        {
            await publicStorage.DeleteAsync(publicKey, cancellationToken);
            foreach (var variant in resource.Variants)
            {
                await publicStorage.DeleteAsync(
                    StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
            }
            resource.Unpublish(now);
        }
        // Borrado lógico; el objeto se retiene hasta que pase la ventana de retención.
        resource.SoftDelete(now);

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.deleted",
            resource.Id.ToString(),
            "success",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SoftDeleteResult(true);
    }
}
```

Reemplaza el contenido completo de `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs` por:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record PublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed record UnpublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed class PublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<PublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        PublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);
        if (!publicStorage.IsConfigured)
        {
            throw new StorageDomainException(
                "storage.public.not_configured",
                "Public image storage is not configured.");
        }

        var resource = await LoadAsync(repository, command.TenantId, command.FileId, cancellationToken);
        // Spec 2026-09-16, D15: un comprobante sólo llega al público por el movimiento. Por acá
        // copiaría desde su temporal, que después de moverse ya no existe.
        PaymentProofGuard.EnsureNotPaymentProof(resource);
        resource.EnsureDownloadable();
        var publicKey = resource.PublicStorageKey ?? StorageKey.PublicFor(
            resource.TenantId, resource.Id, resource.Name);
        var now = clock.UtcNow;
        // Validar todos los invariantes de publicación antes de crear cualquier objeto público.
        resource.Publish(publicKey, now);
        var copiedKeys = new List<string>();

        try
        {
            await publicStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
            copiedKeys.Add(publicKey);
            foreach (var variant in resource.Variants)
            {
                var variantKey = StorageKey.PublicVariantFor(publicKey, variant);
                await publicStorage.CopyFromPrivateAsync(variant.StorageKey, variantKey, cancellationToken);
                copiedKeys.Add(variantKey);
            }
        }
        catch
        {
            foreach (var key in copiedKeys)
            {
                try { await publicStorage.DeleteAsync(key, CancellationToken.None); }
                catch { /* best-effort rollback; retrying publish is safe */ }
            }
            throw;
        }

        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return resource.ToDto(publicStorage);
    }

    internal static async Task<FileResource> LoadAsync(
        IFileResourceRepository repository, Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new ResourceNotFoundException("storage.file.not_found", "The file resource was not found.");
        }
        return resource;
    }
}

public sealed class UnpublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    IPublicObjectStorage publicStorage,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<UnpublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        UnpublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);
        var resource = await PublishFileHandler.LoadAsync(
            repository, command.TenantId, command.FileId, cancellationToken);

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        if (resource.PublicStorageKey is { } publicKey)
        {
            await publicStorage.DeleteAsync(publicKey, cancellationToken);
            foreach (var variant in resource.Variants)
            {
                await publicStorage.DeleteAsync(
                    StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
            }
            resource.Unpublish(clock.UtcNow);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "storage.file.unpublished",
                resource.Id.ToString(), "success", clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return resource.ToDto(publicStorage);
    }
}
```

- [ ] **Step 5: El README y el resumen del publicador**

En `README.md`, reemplaza:

```markdown
- **Nada se despublica solo:** apagar la opción deja de publicar y de mostrar enlaces, pero las
  copias ya hechas siguen en el bucket, y borrar el archivo en Storage tampoco toca su copia.
```

por:

```markdown
- **Nada se despublica solo:** apagar la opción deja de publicar y de mostrar enlaces, pero las
  copias ya hechas siguen en el bucket, y borrar un comprobante `User` en Storage tampoco toca su
  copia. Un comprobante `PaymentProof` que algún pedido referencia **no** se puede borrar ni
  despublicar: `DELETE /files/{id}` y `DELETE /files/{id}/publication` responden 422
  `storage.file.invalid_state` sin tocar el bucket, porque su copia pública es la que enlaza el
  Excel. `PUT /files/{id}/publication` rechaza siempre un `PaymentProof`, con el mismo código: sólo
  llega al público al adjuntarse a un pedido.
```

El resumen de `PublicPaymentProofPublisher` dice que el `FileResource` nunca se entera de la copia y que borrarlo la deja en el bucket; desde Task 7 y esta tarea eso sólo es cierto para un comprobante `User` (pre-flight C4). En `src/Bootstrapper/PublicPaymentProofPublisher.cs`, reemplaza:

```csharp
/// publicar un PDF desde la API. Por lo mismo el <c>FileResource</c> no se entera de esta copia: si
/// alguien lo borra (<c>SoftDeleteFileHandler</c>), la copia pública queda en el bucket. Despublicar
/// es trabajo aparte.
/// </summary>
```

por:

```csharp
/// publicar un PDF desde la API. Por lo mismo, para un comprobante <c>User</c> (v1) el
/// <c>FileResource</c> no se entera de esta copia: si alguien lo borra (<c>SoftDeleteFileHandler</c>),
/// la copia pública queda en el bucket. Un <c>PaymentProof</c> (spec 2026-09-16) sí: Storage registra
/// la clave cuando lo mueve (D10), y no deja borrarlo ni despublicarlo mientras un pedido lo
/// referencie (D15).
/// </summary>
```

- [ ] **Step 6: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; los tres proyectos con `Con error: 0`; `PaymentProofFileManagementTests` con 6 superadas y `PaymentProofFileManagementApiTests` con 2. `StorageFlowTests.UploadScanDownloadDeleteFlowWithObjectStorageDoubleAndAudit` sigue verde (un archivo `User` se borra como antes) y `CompositionRootTests` también (el contenedor resuelve la lista de sondas). Pega los resúmenes.

- [ ] **Step 7: El chequeo de formato**

Corre «el chequeo de formato». Esperado: sin salida, o sólo líneas que no tocaste (los dos handlers ya existían: anota sin arreglar lo que no sea de las líneas nuevas).

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Application/PaymentProofGuard.cs src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs tests/Modules/Storage/Modules.Storage.UnitTests/StorageApplicationTestDoubles.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofFileManagementApiTests.cs README.md src/Bootstrapper/PublicPaymentProofPublisher.cs; git commit -m "feat(storage): proteger la copia pública de un comprobante adjunto" -m "Borrar o despublicar un PaymentProof que algún pedido referencia se rechaza con storage.file.invalid_state antes de tocar el bucket, y publicar un PaymentProof se rechaza siempre: sólo llega al público por el movimiento (spec 2026-09-16, D15)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 9C: Storage purga el comprobante que un pedido suelta (D19)

Enmienda D19, lado de Storage. `PaymentProofDetachProcessor` consume `quotations.order.payment-proofs-detached.v1` con el inbox de Task 7 y el mismo esqueleto que el movimiento. Por cada archivo: un `PaymentProof` `Available` que ninguna `IFileReferenceProbe` retiene pierde su objeto —el público si ya se movió; si no, el de `staging/` y la copia que trae el evento— y, en un solo `SaveChanges`, queda `Purged` con `FileResource.PurgeDetachedPaymentProof`, auditado como `storage.file.purged` / `payment_proof_detached`, junto con el inbox. Un archivo `User` sólo pierde la copia de ese adjunto (D13). Lo corre `PaymentProofMoveWorker`, en el mismo tick y después del movimiento. Todavía nadie escribe el evento: eso es Task 9D, así que las pruebas lo escriben a mano, como las de Task 7. El movimiento ya salta un comprobante que no está `Available` (`IsWaitingToMove`, Task 7): esta tarea lo prueba para la carrera de D19 sin cambiarlo.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs` (después de `PurgeUnattachedPaymentProof`, de Task 1)
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs` (cuatro pruebas antes de `PendingScanProof`)
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofDetachProcessor.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs` (el cuerpo del `try`, de Task 7)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs` (después del registro de `IPaymentProofMoveProcessor`, de Task 7)
- Modify: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` (tres helpers después de `IsProcessedByMoveAsync`)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDetachTests.cs` (nuevo)

**Interfaces:**
- Consumes: `FileOwnerType.PaymentProof`, `FileResource.MoveToPublic` y el helper `RequirePaymentProof` (Task 1); `StorageInboxMessage`, `StorageDbContext.Inbox`/`Outbox`, `IPaymentProofMoveProcessor`, `PaymentProofMoveWorker` y la regla `IsWaitingToMove` de `PaymentProofMoveProcessor` (Task 7); `IFileReferenceProbe` e `IStorageAuditPublisher.PublishSystem(Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt)` (Task 9); del harness de Storage `TenantId`, `CreateClient`, `CreateAvailableAsync`, `Pdf`, `ReadFileAsync`, `NewPublicKey`, `AddAttachedEventAsync`, `RunMoveAsync`, `IsProcessedByMoveAsync`, `InMemoryObjectStorage.FailingDeleteKey`/`Exists`, `InMemoryPublicObjectStorage.Put`/`Exists`/`DeletedKeys` (Tasks 4 y 7), `StorageApiFactory.FileReferences` y `AuditEventsAsync` (Task 9).
- Produces:
  - `public void FileResource.PurgeDetachedPaymentProof(DateTimeOffset occurredAt)`: exige `PaymentProof` y `Available` (`storage.file.invalid_state` si no); acepta uno movido o no; deja `Purged` con `DeletedAt` y `UpdatedAt`; conserva `PublicStorageKey`.
  - `internal interface IPaymentProofDetachProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }` y `internal sealed partial class PaymentProofDetachProcessor` en `Modules.Storage.Infrastructure.PaymentProofs`, con `internal const string Consumer = "storage.payment-proof-detach"`, `internal const string DetachedEvent = "quotations.order.payment-proofs-detached.v1"` e `internal const string Reason = "payment_proof_detached"`.
  - Contrato del payload que lee por nombre (Task 9D lo escribe): `{ "tenantId": Guid, "orderId": Guid, "proofs": [ { "fileId": Guid, "publicStorageKey": string | null } ] }`.
  - `PaymentProofMoveWorker` corre, en cada tick y en scopes separados, primero `IPaymentProofMoveProcessor` y después `IPaymentProofDetachProcessor`.
  - En el harness de Storage: `AddDetachedEventAsync(StorageApiFactory factory, params (Guid FileId, string? PublicStorageKey)[] proofs)` → `Guid`, `RunDetachAsync(StorageApiFactory)` → `int` e `IsProcessedByDetachAsync(StorageApiFactory, Guid messageId)` → `bool`. Task 10 no los usa.

- [ ] **Step 1: Las pruebas del dominio (RED)**

En `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs`, reemplaza:

```csharp
    private static FileResource PendingScanProof(string name, string mimeType)
```

por:

```csharp
    // D19: un comprobante movido que se reemplaza o se quita de su pedido se purga. Su copia pública era
    // la única, y el procesador ya la borró.
    [Fact]
    public void AMovedProofCanBePurgedWhenItsOrderLetsItGo()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        proof.PurgeDetachedPaymentProof(Now.AddHours(1));

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now.AddHours(1), proof.DeletedAt);
        Assert.Equal(Now.AddHours(1), proof.UpdatedAt);
        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
    }

    // D19: uno que todavía espera en staging/ también.
    [Fact]
    public void AStagedProofCanBePurgedWhenItsOrderLetsItGo()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.PurgeDetachedPaymentProof(Now);

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now, proof.DeletedAt);
        Assert.Null(proof.PublicStorageKey);
    }

    // Un segundo mensaje por el mismo archivo no lo vuelve a purgar: el procesador lo salta antes.
    [Fact]
    public void PurgingALetGoProofRequiresAnAvailableResource()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.PurgeDetachedPaymentProof(Now);

        var error = Assert.Throws<StorageDomainException>(() => proof.PurgeDetachedPaymentProof(Now.AddHours(1)));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(Now, proof.DeletedAt);
    }

    // D13: el archivo User de un comprobante v1 nunca se purga por esto.
    [Fact]
    public void PurgingALetGoProofRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() => file.PurgeDetachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(FileResourceStatus.Available, file.Status);
    }

    private static FileResource PendingScanProof(string name, string mimeType)
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofFileResourceTests"
```

Esperado (RED de compilación; el de aserción llega en el Step 4): `error CS1061: 'FileResource' no contiene una definición para 'PurgeDetachedPaymentProof'` (o `does not contain a definition for 'PurgeDetachedPaymentProof'`). Pega la salida.

- [ ] **Step 2: El método de dominio (GREEN del dominio)**

En `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs`, reemplaza:

```csharp
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "A payment proof already moved to the public bucket cannot be purged.");
        }

        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }
```

por:

```csharp
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "A payment proof already moved to the public bucket cannot be purged.");
        }

        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    // Spec 2026-09-16, D19: un comprobante que su pedido soltó —se reemplazó o se quitó— se purga, esté
    // movido o no. Quien llama ya borró su objeto (el público si se movió, el de staging/ si no).
    // PublicStorageKey se conserva como registro de dónde estuvo: un recurso Purged no se descarga ni se
    // vuelve a adjuntar.
    public void PurgeDetachedPaymentProof(DateTimeOffset occurredAt)
    {
        RequirePaymentProof();
        RequireStatus(FileResourceStatus.Available);
        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofFileResourceTests"
```

Esperado: `Superado: 20`, `Con error: 0` (las 16 de Task 1 y las 4 nuevas). Pega la salida.

- [ ] **Step 3: El harness y las pruebas del procesador (RED)**

En `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`, reemplaza:

```csharp
            entry => entry.Consumer == "storage.payment-proof-move" && entry.MessageId == messageId,
            TestContext.Current.CancellationToken);
    }
```

por:

```csharp
            entry => entry.Consumer == "storage.payment-proof-move" && entry.MessageId == messageId,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Escribe en el outbox el evento que Quotations escribe al reemplazar o quitar un
    /// comprobante (spec 2026-09-16, D19), con el nombre y los campos del contrato escritos a mano, igual
    /// que <see cref="AddAttachedEventAsync"/>. Una clave null es un comprobante sin copia. Devuelve el id
    /// del mensaje.</summary>
    public static async Task<Guid> AddDetachedEventAsync(
        StorageApiFactory factory, params (Guid FileId, string? PublicStorageKey)[] proofs)
    {
        var id = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = id,
            EventName = "quotations.order.payment-proofs-detached.v1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                tenantId = TenantId,
                orderId = Guid.CreateVersion7(),
                proofs = proofs
                    .Select(proof => new { fileId = proof.FileId, publicStorageKey = proof.PublicStorageKey })
                    .ToArray(),
            }),
            CorrelationId = id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Un lote del retiro, a mano: en producción lo corre PaymentProofMoveWorker, que no corre
    /// en este host (ver <see cref="StorageApiFactory"/>).</summary>
    public static async Task<int> RunDetachAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentProofDetachProcessor>()
            .ProcessPendingAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<bool> IsProcessedByDetachAsync(StorageApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        return await dbContext.Inbox.AnyAsync(
            entry => entry.Consumer == "storage.payment-proof-detach" && entry.MessageId == messageId,
            TestContext.Current.CancellationToken);
    }
```

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDetachTests.cs`:

```csharp
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El retiro de un comprobante que su pedido soltó (spec 2026-09-16, D19): uno movido pierde su copia
/// pública y uno en staging/ su temporal y la copia que alcanzó a hacer el adjunto; los dos quedan
/// Purged y auditados. Se respeta el que otro pedido todavía usa, cada mensaje se aplica una vez, un
/// borrado fallido no guarda nada, un archivo User conserva su original y el movimiento salta lo que ya
/// se purgó.
/// </summary>
public sealed class PaymentProofDetachTests
{
    [Fact]
    public async Task AMovedProofIsPurgedAndItsPublicObjectDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        var messageId = await AddDetachedEventAsync(factory, (fileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Purged", row.Status);
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(fileId.ToString(), audit.ResourceId);
        Assert.Equal("file", audit.ResourceType);
        Assert.Equal("payment_proof_detached", audit.Outcome);
        Assert.Equal("System", audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // D19 con Quotations:PaymentProofs:PublicLinks apagada: sin copia, sólo el temporal.
    [Fact]
    public async Task AStagedProofWithoutACopyIsPurgedWithItsStagingObject()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
    }

    // La carrera de D19: el comprobante se suelta antes de que se procese su adjunto. Se borran el
    // temporal y la copia que el adjunto alcanzó a hacer, y el movimiento, que llega después, lo salta
    // sin fallar y marca su mensaje.
    [Fact]
    public async Task AProofDetachedBeforeItsMoveLosesItsCopyAndTheMoveSkipsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        var attachedId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        await AddDetachedEventAsync(factory, (proof.FileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));

        Assert.Equal(1, await RunMoveAsync(factory));

        row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.True(await IsProcessedByMoveAsync(factory, attachedId));
    }

    // Otro pedido todavía lo usa: no se borra nada y el mensaje se marca igual.
    [Fact]
    public async Task AProofStillReferencedIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        factory.FileReferences.Reference(fileId);
        var messageId = await AddDetachedEventAsync(factory, (fileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.True(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // El inbox no repite un mensaje, y un segundo mensaje por un archivo ya purgado no borra ni audita
    // de nuevo.
    [Fact]
    public async Task ADetachIsAppliedOnlyOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        await AddDetachedEventAsync(factory, (fileId, publicKey));
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal(0, await RunDetachAsync(factory));
        await AddDetachedEventAsync(factory, (fileId, publicKey));
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // Mismo orden que D9: si un borrado falla no se guarda nada y el mensaje vuelve en el tick siguiente.
    [Fact]
    public async Task WhenADeleteFailsNothingIsSavedAndTheMessageComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;

        Assert.Equal(0, await RunDetachAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(await IsProcessedByDetachAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D13 y D19: un comprobante User conserva su original privado; sólo se borra la copia de ese
    // adjunto, que era única.
    [Fact]
    public async Task AUserFileKeepsItsOriginalAndLosesOnlyTheCopyOfThatAttachment()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        var privateKey = (await ReadFileAsync(database.GetConnectionString(), file.FileId)).StorageKey;
        var copyKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(copyKey, DateTimeOffset.UtcNow);
        await AddDetachedEventAsync(factory, (file.FileId, copyKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), file.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(privateKey));
        Assert.False(factory.PublicObjectStorage.Exists(copyKey));
        var audit = Assert.Single(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
        Assert.Equal(copyKey, audit.ResourceId);
        Assert.Equal("public_object", audit.ResourceType);
        Assert.Equal("payment_proof_detached", audit.Outcome);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // Un comprobante v2 ya movido, con su copia en el bucket público en memoria.
    private static async Task<(Guid FileId, string PublicKey)> MovedProofAsync(
        HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return (proof.FileId, publicKey);
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado (RED de compilación): `error CS0246: … 'IPaymentProofDetachProcessor' …`. Pega la salida.

- [ ] **Step 4: El procesador, sin borrar todavía la copia de uno sin mover (RED de aserción)**

Como en Task 2, la implementación entra primero incompleta: le faltan las líneas que borran la copia que trae el evento cuando el comprobante no se movió, y la prueba de la carrera lo tiene que ver.

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofDetachProcessor.cs`:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofDetachProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-09-16, D19. Consume del outbox de plataforma el evento que Quotations escribe al reemplazar o
// quitar comprobantes, con el mismo esqueleto que PaymentProofMoveProcessor: anti-join contra el inbox
// propio, y purga, auditoría e inbox en el mismo SaveChanges.
//
// Mismo orden que D9 y por la misma razón: primero se borran los objetos y después se guarda. Borrar una
// clave que ya no existe no falla, así que si el guardado falla el tick siguiente repite los borrados
// (sin efecto) y guarda. Al revés, un borrado fallido con el inbox ya marcado dejaría expuesto para
// siempre el comprobante que D19 quiere borrar.
internal sealed partial class PaymentProofDetachProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IPublicObjectStorage publicObjectStorage,
    IEnumerable<IFileReferenceProbe> probes,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    ILogger<PaymentProofDetachProcessor> logger) : IPaymentProofDetachProcessor
{
    internal const string Consumer = "storage.payment-proof-detach";
    internal const string DetachedEvent = "quotations.order.payment-proofs-detached.v1";
    internal const string Reason = "payment_proof_detached";
    private const int BatchSize = 20;

    private readonly IReadOnlyList<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof detach failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Detached payment proof {FileId} kept: still referenced by {Source}.")]
    private static partial void LogProofRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Detached payment proof {FileId} purged.")]
    private static partial void LogProofPurged(ILogger logger, Guid fileId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == DetachedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var record in pending)
        {
            try
            {
                await DetachAsync(record, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla no frena a los demás: se descarta lo rastreado (la purga y su
                // auditoría) y, sin inbox, vuelve en el tick siguiente.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    private async Task DetachAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        var payload = DetachedPayload.Parse(record.PayloadJson);
        var now = clock.UtcNow;
        foreach (var proof in payload.Proofs)
        {
            var fileId = new FileResourceId(proof.FileId);
            var resource = await dbContext.FileResources
                .FirstOrDefaultAsync(file => file.Id == fileId, cancellationToken);
            if (resource is null || resource.TenantId != payload.TenantId)
            {
                continue;
            }

            if (resource.OwnerType is not FileOwnerType.PaymentProof)
            {
                await DeleteAttachmentCopyAsync(payload.TenantId, proof.PublicStorageKey, now, cancellationToken);
                continue;
            }

            // Ya purgado (por otro mensaje), borrado o en cuarentena: no hay nada que borrar, y reintentar
            // no lo cambiaría.
            if (resource.Status is not FileResourceStatus.Available)
            {
                continue;
            }

            var retainedBy = await FindRetainingSourceAsync(resource.Id.Value, cancellationToken);
            if (retainedBy is not null)
            {
                // Otro comprobante todavía lo usa. Una copia que quede sin dueño la recoge la
                // reconciliación de payment-proofs/ (D12).
                LogProofRetained(logger, resource.Id.Value, retainedBy);
                continue;
            }

            if (resource.PublicStorageKey is { } movedKey)
            {
                // Movido: la copia pública es su única copia (D9).
                await publicObjectStorage.DeleteAsync(movedKey, cancellationToken);
            }
            else
            {
                // Sin mover: el temporal sigue en staging/.
                await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
            }

            resource.PurgeDetachedPaymentProof(now);
            auditPublisher.PublishSystem(
                resource.TenantId,
                "storage.file.purged",
                "file",
                resource.Id.ToString(),
                Reason,
                now);
            LogProofPurged(logger, resource.Id.Value);
        }

        dbContext.Inbox.Add(new StorageInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // D13 y D19: de un comprobante User sólo se borra la copia pública de ese adjunto.
    // PublicPaymentProofPublisher le da a cada adjunto una clave aleatoria propia, así que ningún otro
    // comprobante la usa; el original privado no se toca.
    private async Task DeleteAttachmentCopyAsync(
        Guid tenantId, string? copyKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (copyKey is null)
        {
            return;
        }

        await publicObjectStorage.DeleteAsync(copyKey, cancellationToken);
        auditPublisher.PublishSystem(
            tenantId,
            "storage.public_object.purged",
            "public_object",
            copyKey,
            Reason,
            now);
    }

    // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
    private async Task<string?> FindRetainingSourceAsync(Guid fileId, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(fileId, cancellationToken))
            {
                return probe.Source;
            }
        }

        return null;
    }

    private sealed record DetachedPayload(Guid TenantId, IReadOnlyList<DetachedProof> Proofs)
    {
        // Por nombre, igual que en PaymentProofMoveProcessor: el payload lo escribe
        // OrderPaymentProofEventPublisher, en Quotations. publicStorageKey viene null cuando el
        // comprobante no tenía copia.
        public static DetachedPayload Parse(string payloadJson)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            return new DetachedPayload(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("proofs")
                    .EnumerateArray()
                    .Select(proof => new DetachedProof(
                        proof.GetProperty("fileId").GetGuid(),
                        proof.GetProperty("publicStorageKey").GetString()))
                    .ToArray());
        }
    }

    private sealed record DetachedProof(Guid FileId, string? PublicStorageKey);
}
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IPaymentProofMoveProcessor, PaymentProofMoveProcessor>();
```

por:

```csharp
        services.AddScoped<IPaymentProofMoveProcessor, PaymentProofMoveProcessor>();
        // Spec 2026-09-16, D19: borra y purga lo que un pedido suelta. Lo corre PaymentProofMoveWorker,
        // después del movimiento.
        services.AddScoped<IPaymentProofDetachProcessor, PaymentProofDetachProcessor>();
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs`, reemplaza:

```csharp
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
            }
```

por:

```csharp
            try
            {
                await using (var moveScope = scopeFactory.CreateAsyncScope())
                {
                    await moveScope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>()
                        .ProcessPendingAsync(stoppingToken);
                }

                // Spec 2026-09-16, D19: el retiro, en el mismo tick y después del movimiento, con su propio
                // DbContext. Así, en una réplica, el movimiento y el retiro de un mismo archivo nunca corren
                // a la vez (FileResource no tiene token de concurrencia); la carrera entre réplicas la
                // cubre PaymentProofDetachProcessor.
                await using var detachScope = scopeFactory.CreateAsyncScope();
                await detachScope.ServiceProvider.GetRequiredService<IPaymentProofDetachProcessor>()
                    .ProcessPendingAsync(stoppingToken);
            }
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofDetachTests"
```

Esperado (RED de aserción), `Con error: 1, Superado: 6`: `AProofDetachedBeforeItsMoveLosesItsCopyAndTheMoveSkipsIt` falla con `Assert.False() Failure` → `Expected: False`, `Actual: True`, en `factory.PublicObjectStorage.Exists(publicKey)` (el temporal ya se borró y el recurso quedó `Purged`; la copia no). Si falla otra prueba, o ésta falla en otra línea, **para y pregunta**. Pega la salida.

- [ ] **Step 5: Borrar la copia de uno sin mover (GREEN)**

En `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofDetachProcessor.cs`, reemplaza:

```csharp
                // Sin mover: el temporal sigue en staging/.
                await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
            }
```

por:

```csharp
                // Sin mover: el temporal sigue en staging/ y, si el adjunto alcanzó a copiarse, su copia
                // pública también existe aunque el movimiento nunca la registró. Es la carrera de D19: el
                // pedido lo soltó antes de que PaymentProofMoveWorker procesara el adjunto, que después
                // lo salta porque ya no está Available.
                await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
                if (proof.PublicStorageKey is { } copyKey)
                {
                    await publicObjectStorage.DeleteAsync(copyKey, cancellationToken);
                }
            }
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: los tres con `Con error: 0`; `PaymentProofFileResourceTests` con 20 superadas, `PaymentProofDetachTests` con 7 y `PaymentProofMoveTests` con 6 (el movimiento no cambió); `OrderPaymentProofPublicationApiTests` con `Superado: 20`: el worker del host de Quotations ya corre el retiro, pero todavía nadie escribe el evento. Pega los resúmenes.

- [ ] **Step 6: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: `0 Advertencia(s)`, `0 Errores` y `ArchitectureTests` con `Con error: 0`. Después corre «el chequeo de formato»; esperado sin salida.

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Storage/Modules.Storage.Domain/FileResource.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileResourceTests.cs src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofDetachProcessor.cs src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofMoveWorker.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofDetachTests.cs; git commit -m "feat(storage): purgar el comprobante que un pedido suelta" -m "PaymentProofDetachProcessor consume quotations.order.payment-proofs-detached.v1 con el inbox de Storage: borra la copia pública de un PaymentProof movido, o su temporal y la copia del adjunto si no se movió, y en un solo SaveChanges lo marca Purged, lo audita como payment_proof_detached y marca el inbox. De un archivo User sólo borra la copia de ese adjunto. Lo corre PaymentProofMoveWorker después del movimiento (spec 2026-09-16, D19)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 9D: Quotations suelta el archivo al reemplazar o quitar un comprobante (D19)

Enmienda D19, lado de Quotations (hallazgo 22). En la misma unidad de trabajo que el pedido, `AddOrderPaymentProofsHandler` (al reemplazar con `UpdatedProofs[].NewFileId`) y `RemoveOrderPaymentProofHandler` escriben `quotations.order.payment-proofs-detached.v1` con cada archivo que el pedido deja de usar y la clave que tenía ese comprobante, también con `Quotations:PaymentProofs:PublicLinks` apagada. `AddOrderPaymentProofsHandler` deja de borrar la clave vieja después de guardar (`AddOrderPaymentProofs.cs:163-174` en `develop`): desde acá la borra Storage (Task 9C), con reintento. El bucket público en memoria del harness de Quotations pasa a ser concurrente, porque el retiro borra copias desde el hilo del worker mientras la prueba lee.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs` (de Task 6)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs` (de Task 6)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs` (al final, después de `AttachedFromReplacements`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs:107-118,163-176` y el bloque de Task 6 antes de `SaveChangesAsync`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/RemoveOrderPaymentProof.cs:1-58` (archivo completo)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (`InMemoryPublicObjectStorage`)
- Modify: `src/Bootstrapper/PublicPaymentProofPublisher.cs` (el resumen que dejó Task 9B)
- Modify: `README.md` (un bullet debajo del que dejó Task 9B)
- Modify: `docs/integracion-cotizaciones-y-pedidos.md` (el párrafo de comprobantes que dejó Task 8)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs` (una prueba al final)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` (ocho pruebas)

**Interfaces:**
- Consumes: `IOrderPaymentProofEventPublisher`, `OrderPaymentProofEventPublisher`, `PaymentProofCopies.AttachedFrom`/`AttachedFromReplacements` y los records de payload de las pruebas (Task 6); `CreateAvailablePaymentProofImageAsync`, `InMemoryObjectStorage.Exists`, `StorageKeyOfAsync`, `WaitForMovedKeyAsync` y `AssertMovedAsync` como ancla (Tasks 7 y 7B); `PaymentProofDetachProcessor` corriendo en `PaymentProofMoveWorker` del host de Quotations (Task 9C); `OrderPaymentProof.FileId`/`PublicStorageKey`/`Id`, `Order.PaymentProofs` y `Order.RemovePaymentProof(OrderPaymentProofId proofId, DateTimeOffset occurredAt)` (`develop`, hallazgo 22).
- Produces:
  - `void IOrderPaymentProofEventPublisher.PublishDetached(Guid tenantId, OrderId orderId, IReadOnlyCollection<DetachedPaymentProof> proofs, DateTimeOffset occurredAt)` y `public sealed record DetachedPaymentProof(Guid FileId, string? PublicStorageKey)` en `Modules.Quotations.Application`.
  - `internal const string OrderPaymentProofEventPublisher.DetachedEventName = "quotations.order.payment-proofs-detached.v1"`; payload `{ "tenantId": Guid, "orderId": Guid, "proofs": [ { "fileId": Guid, "publicStorageKey": string | null } ] }`, `CorrelationId` = id del pedido, `OccurredAt` = el `now` del handler. Es el contrato que Task 9C lee.
  - `public static DetachedPaymentProof[] PaymentProofCopies.DetachedFrom(IEnumerable<DetachedPaymentProof> candidates, IEnumerable<Guid> remainingFileIds)`. La ronda de arreglo de este task cambió esta firma y su regla; ver spec, enmienda de implementación 5.
  - `RemoveOrderPaymentProofHandler(IOrderRepository orderRepository, IQuotationRepository quotationRepository, IQuotationsUnitOfWork unitOfWork, IQuotationAuditPublisher auditPublisher, IOrderPaymentProofEventPublisher paymentProofEvents, IExecutionContext executionContext, IClock clock)`. Se registra por tipo (`QepServiceCollectionExtensions.cs:365-367`) y ninguna prueba lo construye a mano.
  - `QuotationsApiHarness.InMemoryPublicObjectStorage.DeletedKeys` pasa a `ConcurrentQueue<string>` (todas las pruebas que lo leen usan `Assert.Empty`, `Assert.Single` o `Assert.Contains`, verificado con `git grep "PublicObjectStorage.DeletedKeys" -- tests`).

- [ ] **Step 1: Las pruebas de integración (RED)**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, reemplaza:

```csharp
        private readonly Dictionary<string, string> _copies = new(StringComparer.Ordinal);
```

por:

```csharp
        // Concurrente desde D19 (spec 2026-09-16): PaymentProofDetachProcessor borra copias desde el hilo
        // de PaymentProofMoveWorker mientras la prueba lee.
        private readonly ConcurrentDictionary<string, string> _copies = new(StringComparer.Ordinal);
```

Reemplaza:

```csharp
        public List<string> DeletedKeys { get; } = [];
```

por:

```csharp
        public ConcurrentQueue<string> DeletedKeys { get; } = new();
```

Y reemplaza:

```csharp
            _copies.Remove(publicKey);
            DeletedKeys.Add(publicKey);
```

por:

```csharp
            _copies.TryRemove(publicKey, out _);
            DeletedKeys.Enqueue(publicKey);
```

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`, reemplaza:

```csharp
    private const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";
```

por:

```csharp
    private const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    // D19: el evento de retiro, también escrito a mano.
    private const string DetachedEventName = "quotations.order.payment-proofs-detached.v1";
```

Reemplaza:

```csharp
    private static async Task AssertMovedAsync(
```

por:

```csharp
    // D19 (spec 2026-09-16): reemplazar el archivo de un comprobante escribe, con el pedido, el archivo
    // viejo y la clave de su copia.
    [Fact]
    public async Task ReplacingAProofFileWritesTheDetachedEventWithTheOldFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var oldFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", oldFileId);
        var oldKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, newFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload.TenantId);
        Assert.Equal(order.Id, payload.OrderId);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(oldFileId, detached.FileId);
        Assert.Equal(oldKey, detached.PublicStorageKey);
    }

    // D19: quitar un comprobante también.
    [Fact]
    public async Task RemovingAProofWritesTheDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, quotation.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(order.Id, payload.OrderId);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(fileId, detached.FileId);
        Assert.Equal(publicKey, detached.PublicStorageKey);
    }

    // D19 con la opción apagada: el evento sale igual, sin clave, para que Storage borre el temporal.
    [Fact]
    public async Task RemovingAProofWithPublicLinksOffWritesTheDetachedEventWithoutKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, quotation.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(fileId, detached.FileId);
        Assert.Null(detached.PublicStorageKey);
    }

    // Corregir sólo un monto no suelta ningún archivo. Ya pasa antes de esta tarea.
    [Fact]
    public async Task CorrectingOnlyAmountsWritesNoDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, DetachedEventName));
    }

    // D19, «misma transacción»: un retiro que el dominio rechaza no deja evento. Ya pasa antes de esta
    // tarea.
    [Fact]
    public async Task ARejectedRemovalWritesNoDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, quotation.Id)}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.not_found", problem?.Code);
        Assert.Empty(await OutboxMessagesAsync(factory, DetachedEventName));
    }

    // D19 de punta a punta: reemplazar el archivo de un comprobante movido borra su copia pública —la
    // única que tenía— y lo deja Purged. El reemplazo se mueve como cualquier comprobante nuevo.
    [Fact]
    public async Task AReplacedPaymentProofImageIsPurgedWithItsPublicCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var oldFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", oldFileId);
        var oldKey = await WaitForMovedKeyAsync(database.GetConnectionString(), oldFileId);
        Assert.NotNull(oldKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, newFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Purged", await WaitForStatusAsync(database.GetConnectionString(), oldFileId, "Purged"));
        Assert.Contains(oldKey, factory.PublicObjectStorage.DeletedKeys);
        // Sólo queda la copia del reemplazo.
        Assert.Single(factory.PublicObjectStorage.Copies);
        Assert.NotNull(await WaitForMovedKeyAsync(database.GetConnectionString(), newFileId));
    }

    // D19 de punta a punta: quitar un comprobante movido, lo mismo.
    [Fact]
    public async Task ARemovedPaymentProofImageIsPurgedWithItsPublicCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = await WaitForMovedKeyAsync(database.GetConnectionString(), fileId);
        Assert.NotNull(publicKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, quotation.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Purged", await WaitForStatusAsync(database.GetConnectionString(), fileId, "Purged"));
        Assert.Contains(publicKey, factory.PublicObjectStorage.DeletedKeys);
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // D13 y D19: un comprobante User conserva su original privado, pero la copia pública de ese adjunto
    // se borra al quitarlo. Antes de D19, quitar un comprobante no borraba nada.
    [Fact]
    public async Task ARemovedUserProofLosesItsPublicCopyAndKeepsItsOriginal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var privateKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.NotNull(publicKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, quotation.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await WaitForDeletedKeyAsync(factory, publicKey));
        Assert.Equal("Available", await StatusOfAsync(database.GetConnectionString(), fileId));
        Assert.True(factory.ObjectStorage.Exists(privateKey));
    }

    private static async Task<string?> StatusOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT status FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    // El retiro lo corre PaymentProofMoveWorker cada 3 s en el host: se espera con plazo, como
    // WaitForMovedKeyAsync. Devuelve el último estado leído.
    private static async Task<string?> WaitForStatusAsync(string connectionString, Guid fileId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = await StatusOfAsync(connectionString, fileId);
            if (status == expected)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return status;
    }

    private static async Task<bool> WaitForDeletedKeyAsync(QepApiFactory factory, string publicKey)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (factory.PublicObjectStorage.DeletedKeys.Contains(publicKey))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task AssertMovedAsync(
```

Y reemplaza:

```csharp
    private sealed record AttachedEventProof(Guid FileId, string PublicStorageKey);
}
```

por:

```csharp
    private sealed record AttachedEventProof(Guid FileId, string PublicStorageKey);

    private sealed record DetachedEventPayload(Guid TenantId, Guid OrderId, IReadOnlyList<DetachedEventProof> Proofs);

    private sealed record DetachedEventProof(Guid FileId, string? PublicStorageKey);
}
```

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests.ReplacingAProofFileWritesTheDetachedEventWithTheOldFile|FullyQualifiedName~OrderPaymentProofPublicationApiTests.RemovingAProofWritesTheDetachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.RemovingAProofWithPublicLinksOffWritesTheDetachedEventWithoutKey|FullyQualifiedName~OrderPaymentProofPublicationApiTests.CorrectingOnlyAmountsWritesNoDetachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.ARejectedRemovalWritesNoDetachedEvent|FullyQualifiedName~OrderPaymentProofPublicationApiTests.AReplacedPaymentProofImageIsPurgedWithItsPublicCopy|FullyQualifiedName~OrderPaymentProofPublicationApiTests.ARemovedPaymentProofImageIsPurgedWithItsPublicCopy|FullyQualifiedName~OrderPaymentProofPublicationApiTests.ARemovedUserProofLosesItsPublicCopyAndKeepsItsOriginal"
```

Esperado (RED de aserción), `Con error: 6, Superado: 2`:
- `ReplacingAProofFileWritesTheDetachedEventWithTheOldFile`, `RemovingAProofWritesTheDetachedEvent` y `RemovingAProofWithPublicLinksOffWritesTheDetachedEventWithoutKey` con `Assert.Single() Failure: The collection was empty` (nadie escribe el evento);
- `AReplacedPaymentProofImageIsPurgedWithItsPublicCopy` y `ARemovedPaymentProofImageIsPurgedWithItsPublicCopy`, después de unos 30 s, con `Assert.Equal() Failure: Strings differ` → `Expected: "Purged"`, `Actual: "Available"`;
- `ARemovedUserProofLosesItsPublicCopyAndKeepsItsOriginal`, después de unos 30 s, con `Assert.True() Failure` (quitar no borra nada todavía);
- `CorrectingOnlyAmountsWritesNoDetachedEvent` y `ARejectedRemovalWritesNoDetachedEvent` pasan.

Si una de imagen falla antes, en `Assert.NotNull` del primer `WaitForMovedKeyAsync`, el worker de Task 7 no movió el comprobante: **para y pregunta**. Pega la salida.

- [ ] **Step 2: La prueba unitaria (RED)**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs`, reemplaza:

```csharp
        Assert.Equal([new AttachedPaymentProof(replacedWithKey, "payment-proofs/def.webp")], attached);
    }
}
```

por:

```csharp
        Assert.Equal([new AttachedPaymentProof(replacedWithKey, "payment-proofs/def.webp")], attached);
    }

    // D19 (spec 2026-09-16): se suelta sólo el archivo que el pedido dejó de usar. Si otro comprobante
    // del mismo pedido lo sigue usando, no hay nada que borrar.
    [Fact]
    public void OnlyTheFilesTheOrderNoLongerUsesAreDetached()
    {
        var replaced = new DetachedPaymentProof(Guid.CreateVersion7(), "payment-proofs/abc.webp");
        var stillUsed = new DetachedPaymentProof(Guid.CreateVersion7(), "payment-proofs/def.webp");
        var withoutKey = new DetachedPaymentProof(Guid.CreateVersion7(), null);

        var detached = PaymentProofCopies.DetachedFrom(
            [replaced, stillUsed, withoutKey],
            [stillUsed.FileId, Guid.CreateVersion7()]);

        Assert.Equal([replaced, withoutKey], detached);
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~PaymentProofCopiesTests"
```

Esperado (RED de compilación): `error CS0246: … 'DetachedPaymentProof' …` y `CS0117: 'PaymentProofCopies' no contiene una definición para 'DetachedFrom'`. Pega la salida.

- [ ] **Step 3: El puerto**

En `src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs`, reemplaza:

```csharp
    /// <summary><c>quotations.order.payment-proofs-attached.v1</c>.</summary>
    void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt);
}
```

por:

```csharp
    /// <summary><c>quotations.order.payment-proofs-attached.v1</c>.</summary>
    void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.payment-proofs-detached.v1</c> (spec 2026-09-16, D19): los archivos
    /// que el pedido dejó de usar al reemplazar o quitar comprobantes. Lo consume
    /// <c>PaymentProofDetachProcessor</c>, en Storage, que los borra y los marca purgados.</summary>
    void PublishDetached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<DetachedPaymentProof> proofs,
        DateTimeOffset occurredAt);
}
```

Y reemplaza:

```csharp
public sealed record AttachedPaymentProof(Guid FileId, string PublicStorageKey);
```

por:

```csharp
public sealed record AttachedPaymentProof(Guid FileId, string PublicStorageKey);

/// <summary>Un archivo que el pedido dejó de usar y la clave de la copia pública que tenía ese
/// comprobante, o null si no tenía (la opción apagada, D19).</summary>
public sealed record DetachedPaymentProof(Guid FileId, string? PublicStorageKey);
```

- [ ] **Step 4: La implementación**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs`, reemplaza:

```csharp
    internal const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";
```

por:

```csharp
    internal const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    internal const string DetachedEventName = "quotations.order.payment-proofs-detached.v1";
```

Reemplaza:

```csharp
    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre
    // con JsonDocument.
```

por:

```csharp
    // D19: mismo esquema que el de adjuntos, con publicStorageKey null cuando el comprobante no tenía
    // copia. Lo consume PaymentProofDetachProcessor, en Storage, que también lo lee por nombre.
    public void PublishDetached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<DetachedPaymentProof> proofs,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DetachedEventName,
            PayloadJson = JsonSerializer.Serialize(new DetachedPayload(
                tenantId,
                orderId.Value,
                proofs
                    .Select(proof => new DetachedProofPayload(proof.FileId, proof.PublicStorageKey))
                    .ToArray())),
            CorrelationId = orderId.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre
    // con JsonDocument.
```

Y reemplaza:

```csharp
    private sealed record AttachedProofPayload(Guid fileId, string publicStorageKey);
}
```

por:

```csharp
    private sealed record AttachedProofPayload(Guid fileId, string publicStorageKey);

    private sealed record DetachedPayload(
        Guid tenantId,
        Guid orderId,
        IReadOnlyCollection<DetachedProofPayload> proofs);

    // System.Text.Json escribe el null: el consumidor distingue «sin copia» de un campo que falta.
    private sealed record DetachedProofPayload(Guid fileId, string? publicStorageKey);
}
```

- [ ] **Step 5: `DetachedFrom`**

En `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs`, reemplaza:

```csharp
            .Select(update => new AttachedPaymentProof(update.NewFileId!.Value, update.NewPublicStorageKey!))
            .ToArray();
}
```

por:

```csharp
            .Select(update => new AttachedPaymentProof(update.NewFileId!.Value, update.NewPublicStorageKey!))
            .ToArray();

    /// <summary>Los archivos que el pedido dejó de usar, para el evento de D19 (spec 2026-09-16):
    /// <paramref name="candidates"/> se leen antes de mutar el pedido y
    /// <paramref name="remainingFileIds"/> después. Un archivo que otro comprobante del mismo pedido
    /// sigue usando no se suelta; los de otros pedidos los retiene la sonda de Storage.</summary>
    public static DetachedPaymentProof[] DetachedFrom(
        IEnumerable<DetachedPaymentProof> candidates, IEnumerable<Guid> remainingFileIds)
    {
        var remaining = remainingFileIds.ToHashSet();
        return candidates.Where(candidate => !remaining.Contains(candidate.FileId)).ToArray();
    }
}
```

- [ ] **Step 6: Los dos handlers**

En `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs`, reemplaza:

```csharp
        // La clave vieja de cada comprobante que se reemplaza: capturada ANTES de mutar el
        // agregado, porque después de AddPaymentProofs ya no queda forma de leerla. Sólo se borra
        // si el guardado termina saliendo bien — best-effort, fuera del try de arriba: un archivo
        // público huérfano no es motivo para fallar un request que sí guardó.
        var oldPublicKeysToReplace = command.UpdatedProofs
            .Where(update => update.NewFileId is not null)
            .Select(update => order.PaymentProofs
                .FirstOrDefault(proof => proof.Id.Value == update.ProofId)
                ?.PublicStorageKey)
            .Where(publicKey => publicKey is not null)
            .Cast<string>()
            .ToArray();
```

por:

```csharp
        // D19 (spec 2026-09-16): el archivo y la clave de cada comprobante que se reemplaza, capturados
        // ANTES de mutar el agregado, porque UpdateFile los pisa. Quotations ya no borra la copia vieja
        // después de guardar: la borra Storage al consumir el evento, junto con el archivo, y reintenta
        // si falla. Un ProofId que no es de este pedido no aporta nada acá: AddPaymentProofs lo rechaza.
        var replacedFiles = command.UpdatedProofs
            .Where(update => update.NewFileId is not null)
            .Select(update => order.PaymentProofs
                .FirstOrDefault(proof => proof.Id.Value == update.ProofId))
            .OfType<OrderPaymentProof>()
            .Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey))
            .ToArray();
```

Reemplaza:

```csharp
                .Concat(PaymentProofCopies.AttachedFromReplacements(updatedProofs))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
```

por:

```csharp
                .Concat(PaymentProofCopies.AttachedFromReplacements(updatedProofs))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            // D19: en la misma transacción, los archivos que el pedido dejó de usar. Storage los borra —del
            // bucket público si ya se movieron, de staging/ si no— y los marca purgados. Sale también con
            // la opción apagada: un PaymentProof sin copia igual tiene su temporal.
            var detached = PaymentProofCopies.DetachedFrom(
                replacedFiles, order.PaymentProofs.Select(proof => proof.FileId));
            if (detached.Length > 0)
            {
                paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
```

Y reemplaza:

```csharp
        foreach (var oldPublicKey in oldPublicKeysToReplace)
        {
            try
            {
                await paymentProofPublisher.DeleteAsync(oldPublicKey, CancellationToken.None);
            }
            catch
            {
                // Best-effort, mismo criterio que PaymentProofCopies.RollbackAsync: el archivo
                // viejo queda huérfano en el bucket público, pero el pedido ya guardó el cambio.
            }
        }

        return order.ToDto();
```

por:

```csharp
        return order.ToDto();
```

Reemplaza el contenido completo de `src/Modules/Quotations/Modules.Quotations.Application/RemoveOrderPaymentProof.cs` por:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Quita un comprobante ya cargado (a pedido, 2026-09-15) — para el caso de haber cargado uno
/// equivocado, no para corregirlo (eso ya lo cubre <see cref="AddOrderPaymentProofsCommand"/> vía
/// <c>UpdatedProofs</c>). Sólo mientras el pedido sigue <see cref="OrderStatus.Pending"/> — ver
/// <see cref="Order.RemovePaymentProof"/>. Desde D19 (spec 2026-09-16) el archivo que el pedido deja de
/// usar se borra: lo hace Storage, al consumir el evento que se escribe con el pedido.
/// </summary>
public sealed record RemoveOrderPaymentProofCommand(
    Guid TenantId, Guid QuotationId, Guid ProofId) : ICommand<OrderDto>;

public sealed class RemoveOrderPaymentProofHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveOrderPaymentProofCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        RemoveOrderPaymentProofCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);

        var order = await orderRepository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var now = clock.UtcNow;
        var proofId = new OrderPaymentProofId(command.ProofId);
        // D19: el archivo y su clave, leídos antes de quitarlo. Si el comprobante no es de este pedido,
        // RemovePaymentProof lanza order.payment_proof.not_found y no se publica nada.
        var removed = order.PaymentProofs.FirstOrDefault(proof => proof.Id == proofId);
        order.RemovePaymentProof(proofId, now);
        // El estado del pago cambia con lo que quede cargado -- mismo motivo que
        // AddOrderItemsHandler recalcula tras sumar un producto: el agregado no tiene el total de
        // la cotización a mano.
        order.RecalculatePaymentStatus(quotation.Total, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.payment_proof_removed",
            order.Id.ToString(),
            "success",
            now);
        // D19: en la misma transacción que el pedido. `removed` no es null: RemovePaymentProof ya habría
        // lanzado. Si otro comprobante del pedido usa el mismo archivo, no se suelta.
        var detached = PaymentProofCopies.DetachedFrom(
            [new DetachedPaymentProof(removed!.FileId, removed.PublicStorageKey)],
            order.PaymentProofs.Select(proof => proof.FileId));
        if (detached.Length > 0)
        {
            paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
```

- [ ] **Step 7: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrderApiTests"
```

Esperado: los tres con `Con error: 0`; `PaymentProofCopiesTests` con la prueba nueva entre las superadas; `OrderPaymentProofPublicationApiTests` con `Superado: 28` (las 20 de Tasks 6 a 8B y las 8 nuevas); `OrderApiTests` entero, que incluye las cuatro pruebas de reemplazar y quitar de `develop` (hallazgo 22). Pega los resúmenes.

- [ ] **Step 8: El resumen del publicador, el README y la guía de integración**

En `src/Bootstrapper/PublicPaymentProofPublisher.cs` (pre-flight C4: el resumen que dejó Task 9B no sabe de D19), reemplaza:

```csharp
/// la clave cuando lo mueve (D10), y no deja borrarlo ni despublicarlo mientras un pedido lo
/// referencie (D15).
/// </summary>
```

por:

```csharp
/// la clave cuando lo mueve (D10), y no deja borrarlo ni despublicarlo mientras un pedido lo
/// referencie (D15). Desde D19, reemplazar o quitar un comprobante de un pedido hace que Storage borre
/// la copia de ese adjunto y, en un <c>PaymentProof</c> que nadie más usa, el archivo entero: este
/// publicador sólo borra sus copias en el rollback de un request que falló.
/// </summary>
```

En `README.md`, reemplaza:

```markdown
  Excel. `PUT /files/{id}/publication` rechaza siempre un `PaymentProof`, con el mismo código: sólo
  llega al público al adjuntarse a un pedido.
```

por:

```markdown
  Excel. `PUT /files/{id}/publication` rechaza siempre un `PaymentProof`, con el mismo código: sólo
  llega al público al adjuntarse a un pedido.
- **Reemplazar o quitar un comprobante borra su archivo** (spec 2026-09-16, D19): corregir el archivo
  con `updatedProofs[].newFileId` o quitar el comprobante con `DELETE /order/proofs/{proofId}` escribe
  `quotations.order.payment-proofs-detached.v1` con el pedido, y segundos después Storage borra la
  copia pública de ese adjunto. Si es un `PaymentProof` que ningún otro comprobante usa, borra además
  el archivo —del bucket público si ya se movió, de `staging/` si no— y lo marca `Purged`, auditado
  como `storage.file.purged` / `payment_proof_detached`. El original privado de un comprobante `User`
  no se toca. Un Excel ya enviado con la URL vieja muestra un enlace roto: es a propósito.
```

En `docs/integracion-cotizaciones-y-pedidos.md`, reemplaza:

```markdown
en vez de descargar con el nombre original. Un comprobante `User` sigue como antes. La respuesta de los
endpoints de pedidos no cambia.
```

por:

```markdown
en vez de descargar con el nombre original. Un comprobante `User` sigue como antes. La respuesta de los
endpoints de pedidos no cambia.

Reemplazar el archivo de un comprobante (`updatedProofs[].newFileId`) o quitarlo
(`DELETE /order/proofs/{proofId}`) borra, segundos después, el archivo que el pedido deja de usar: su
URL pública deja de abrir y un `PaymentProof` quitado ya no se puede volver a adjuntar
(`order.payment_proof.file_not_available`). Para corregir, sube un archivo nuevo.
```

- [ ] **Step 9: Build completo y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
```

Esperado: `0 Advertencia(s)`, `0 Errores`, y los dos proyectos con `Con error: 0` (`CompositionRootTests` resuelve `RemoveOrderPaymentProofHandler` con su parámetro nuevo). Después corre «el chequeo de formato»: `AddOrderPaymentProofs.cs` y `RemoveOrderPaymentProof.cs` ya existían, así que un diagnóstico en una línea que no tocaste se anota y no se arregla.

- [ ] **Step 10: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IOrderPaymentProofEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofEventPublisher.cs src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs src/Modules/Quotations/Modules.Quotations.Application/RemoveOrderPaymentProof.cs src/Bootstrapper/PublicPaymentProofPublisher.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs README.md docs/integracion-cotizaciones-y-pedidos.md; git commit -m "feat(orders): soltar el archivo al reemplazar o quitar un comprobante" -m "Reemplazar un archivo con updatedProofs y quitar un comprobante escriben quotations.order.payment-proofs-detached.v1 en la misma transacción que el pedido, con cada archivo que el pedido deja de usar y su clave pública. El borrado best-effort de la copia vieja después de guardar desaparece: lo hace Storage al consumir el evento (spec 2026-09-16, D19)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 10: La reconciliación de `payment-proofs/` en el bucket público

D12 y sección 4: `PaymentProofOrphanCleanupWorker` recorre sólo `payment-proofs/`, paginando; salta lo que tiene menos de `MinimumAgeHours`; un objeto está referenciado si lo tiene algún `FileResource.PublicStorageKey` o algún `OrderPaymentProof.PublicStorageKey` (las dos por `IPublicObjectReferenceProbe`); con `DryRun` sólo registra, sin él borra y audita `storage.public_object.purged`. Las tres claves nuevas van a los dos `appsettings`, `DryRun: "true"` literal al ConfigMap, y las tres (`DryRun`, `MinimumAgeHours` e `IntervalHours`) se fijan en las 39 factorías: los user-secrets de quien corre las pruebas nunca deben llegar a una prueba, tampoco con un intervalo o una edad.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptions.cs:19,26`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptionsValidator.cs:28-31`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageOptionsValidatorTests.cs:88-92`
- Modify: `src/Api/appsettings.json:26-27`, `src/Api/appsettings.example.json:55-56`
- Modify: `k8s/prod-configMap.yaml:53`
- Modify: las 39 factorías del hallazgo 6 (tres líneas cada una)
- Create: `src/BuildingBlocks/BuildingBlocks.Application/IPublicObjectReferenceProbe.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FilePublicObjectReferenceProbe.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofPublicObjectReferenceProbe.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (después de la sonda de Task 9)
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupProcessor.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupWorker.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:41` y después del worker de movimiento
- Modify: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs` (flag `DryRun`, sonda de prueba, helper)
- Modify: `README.md:123,950,982` (las líneas 963-964 ya las corrigió Task 9B, y Task 9D sumó debajo el bullet de D19; las anclas de abajo son de contenido)
- Test: `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofOrphanCleanupTests.cs` (nuevo)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs` (una prueba)
- Test: `tests/ArchitectureTests/ArchitectureTests/ConfigurationExampleTests.cs` (sin cambios; es el RED de las claves, hallazgo 18)

**Interfaces:**
- Consumes: `IPublicObjectStorage.ListAsync`, `PublicObjectPage`, `PublicStoredObject` (Task 3); `InMemoryPublicObjectStorage` con `Put`, `Exists`, `DeletedKeys`, `ListedPrefixes` y `PageSize` (Task 4); `NewPublicKey`, `AddAttachedEventAsync`, `RunMoveAsync` (Task 7); `IStorageAuditPublisher.PublishSystem`, `AuditEventsAsync`, `FixedFileReferenceProbe` y `PaymentProofReferenceProbeTests` con sus helpers (Task 9); el índice `IX_order_payment_proofs_public_key` (Task 8B); las pruebas de `OrderPaymentProofPublicationApiTests` hasta Task 9D (28) y la sección del README que corrigieron Tasks 9B y 9D.
- Produces:
  - `public sealed class PaymentProofOrphanCleanupOptions { int MinimumAgeHours = 24; int IntervalHours = 24; bool DryRun = true; }` y `StorageOptions.PaymentProofOrphanCleanup`.
  - Mensajes de validación: `Storage:PaymentProofOrphanCleanup:MinimumAgeHours must be greater than zero.` y `Storage:PaymentProofOrphanCleanup:IntervalHours must be between 1 and 1193.`
  - `public interface IPublicObjectReferenceProbe { string Source { get; } Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken); }` en `BuildingBlocks.Application`.
  - `internal sealed class FilePublicObjectReferenceProbe(StorageDbContext)` (`Source => "storage"`) y `internal sealed class OrderPaymentProofPublicObjectReferenceProbe(QuotationsDbContext)` (`Source => "quotations"`).
  - `internal interface IPaymentProofOrphanCleanupProcessor { Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken); }`, `internal sealed record PaymentProofOrphanCleanupResult(int Listed, int Recent, int Referenced, int Orphans, int Deleted)`, `PaymentProofOrphanCleanupProcessor` (con `internal const string Prefix = "payment-proofs/"`) y `PaymentProofOrphanCleanupWorker`, en `Modules.Storage.Infrastructure.PaymentProofs`.
  - En el harness de Storage: `StorageApiFactory(string connectionString, bool orphanCleanupDryRun = true)`, `StorageApiFactory.PublicReferences` (`FixedPublicObjectReferenceProbe` con `Reference(string publicStorageKey)`) y `RunOrphanCleanupAsync(StorageApiFactory)` → `PaymentProofOrphanCleanupResult`.

- [ ] **Step 1: Las pruebas del validador (RED)**

En `tests/Modules/Storage/Modules.Storage.UnitTests/StorageOptionsValidatorTests.cs`, reemplaza el final del archivo:

```csharp
        Assert.True(_validator.Validate(name: null, options).Failed);
    }
}
```

por:

```csharp
        Assert.True(_validator.Validate(name: null, options).Failed);
    }

    // Spec 2026-09-16, D12: sin configurar nada, la reconciliación sólo registra, una vez al día y
    // sobre lo que tiene más de 24 h.
    [Fact]
    public void TheOrphanCleanupStartsAsADryRunByDefault()
    {
        var cleanup = new StorageOptions().PaymentProofOrphanCleanup;

        Assert.True(cleanup.DryRun);
        Assert.Equal(24, cleanup.MinimumAgeHours);
        Assert.Equal(24, cleanup.IntervalHours);
    }

    // IntervalHours tiene techo porque PeriodicTimer no acepta más de uint.MaxValue - 1 ms (≈ 1193 h).
    [Theory]
    [InlineData(0, 24)]
    [InlineData(24, 0)]
    [InlineData(24, 1194)]
    public void OrphanCleanupHoursOutOfRangeFail(int minimumAgeHours, int intervalHours)
    {
        var options = new StorageOptions
        {
            PaymentProofOrphanCleanup = new PaymentProofOrphanCleanupOptions
            {
                MinimumAgeHours = minimumAgeHours,
                IntervalHours = intervalHours,
            },
            R2 = ValidR2(),
        };

        Assert.True(_validator.Validate(name: null, options).Failed);
    }

    [Fact]
    public void OrphanCleanupHoursAtTheLimitsAreValid()
    {
        var options = new StorageOptions
        {
            PaymentProofOrphanCleanup = new PaymentProofOrphanCleanupOptions
            {
                MinimumAgeHours = 1,
                IntervalHours = 1193,
            },
            R2 = ValidR2(),
        };

        Assert.True(_validator.Validate(name: null, options).Succeeded);
    }

    private static R2Options ValidR2() =>
        new()
        {
            AccountId = "acct",
            AccessKeyId = "key",
            SecretAccessKey = "secret",
            Bucket = "qep",
        };
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageOptionsValidatorTests"
```

Esperado (RED de compilación): `error CS0117: 'StorageOptions' no contiene una definición para 'PaymentProofOrphanCleanup'` y `CS0246` por `PaymentProofOrphanCleanupOptions`. Pega la salida.

- [ ] **Step 2: Las opciones**

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptions.cs`, reemplaza:

```csharp
    public int StagingCleanupMinutes { get; init; } = 60;
```

por:

```csharp
    public int StagingCleanupMinutes { get; init; } = 60;

    public PaymentProofOrphanCleanupOptions PaymentProofOrphanCleanup { get; init; } = new();
```

y reemplaza:

```csharp
public sealed class ClamAvOptions
```

por:

```csharp
// Spec 2026-09-16, D12: la reconciliación de payment-proofs/ en el bucket público. Borra objetos cuya
// URL puede estar en un Excel ya enviado, así que arranca en modo solo-registrar y DryRun se apaga a
// mano, después de revisar en los logs de producción que lo marcado como huérfano realmente lo es.
public sealed class PaymentProofOrphanCleanupOptions
{
    // Al adjuntar se copia antes de guardar el pedido (D9): un objeto recién copiado todavía no tiene
    // quién lo referencie, y no es huérfano.
    public int MinimumAgeHours { get; init; } = 24;

    public int IntervalHours { get; init; } = 24;

    public bool DryRun { get; init; } = true;
}

public sealed class ClamAvOptions
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageOptionsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-restore --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado (RED de aserción):
- `StorageOptionsValidatorTests`: `Con error: 3`, las tres filas de `OrphanCleanupHoursOutOfRangeFail` con `Assert.True() Failure` → `Expected: True`, `Actual: False`; el resto pasa;
- `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample` falla con el mensaje de la prueba terminado en `no se entera de que existen: Storage:PaymentProofOrphanCleanup:DryRun, Storage:PaymentProofOrphanCleanup:IntervalHours, Storage:PaymentProofOrphanCleanup:MinimumAgeHours`, sin otras claves.

Pega las dos salidas.

- [ ] **Step 3: La validación y los dos `appsettings`**

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptionsValidator.cs`, reemplaza:

```csharp
        if (options.StagingCleanupMinutes <= 0)
        {
            failures.Add("Storage:StagingCleanupMinutes must be greater than zero.");
        }
```

por:

```csharp
        if (options.StagingCleanupMinutes <= 0)
        {
            failures.Add("Storage:StagingCleanupMinutes must be greater than zero.");
        }

        var orphanCleanup = options.PaymentProofOrphanCleanup;
        if (orphanCleanup.MinimumAgeHours <= 0)
        {
            failures.Add("Storage:PaymentProofOrphanCleanup:MinimumAgeHours must be greater than zero.");
        }

        // PeriodicTimer no acepta períodos de más de uint.MaxValue - 1 ms (≈ 1193 h): con uno mayor
        // PaymentProofOrphanCleanupWorker lanzaría al arrancar y tumbaría el host.
        if (orphanCleanup.IntervalHours is < 1 or > 1193)
        {
            failures.Add("Storage:PaymentProofOrphanCleanup:IntervalHours must be between 1 and 1193.");
        }
```

En `src/Api/appsettings.json`, reemplaza:

```json
    "StagingCleanupMinutes": 60,
    "ClamAv": {
```

por:

```json
    "StagingCleanupMinutes": 60,
    "PaymentProofOrphanCleanup": {
      "MinimumAgeHours": 24,
      "IntervalHours": 24,
      "DryRun": true
    },
    "ClamAv": {
```

En `src/Api/appsettings.example.json`, reemplaza:

```json
    "StagingCleanupMinutes": 60,
    "R2": {
```

por:

```json
    "StagingCleanupMinutes": 60,
    "PaymentProofOrphanCleanup": {
      "MinimumAgeHours": 24,
      "IntervalHours": 24,
      "DryRun": true
    },
    "R2": {
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore --filter "FullyQualifiedName~StorageOptionsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-restore
```

Esperado (GREEN): `StorageOptionsValidatorTests` con `Superado: 11`, `Con error: 0`; `ArchitectureTests` entero con `Con error: 0`. Pega las salidas.

- [ ] **Step 4: El ConfigMap de producción**

En `k8s/prod-configMap.yaml`, reemplaza:

```yaml
  Storage__R2__Endpoint: "#{STORAGE_R2_ENDPOINT}#"
```

por:

```yaml
  Storage__R2__Endpoint: "#{STORAGE_R2_ENDPOINT}#"
  # Reconciliación de payment-proofs/ en el bucket público (spec 2026-09-16, D12). Literal y no token,
  # y repite el valor por defecto de la imagen a propósito, igual que
  # Registration__PublicTenantSignupEnabled: el worker borra objetos cuya URL puede estar en un Excel
  # ya enviado, así que arranca registrando en el log lo que borraría ("Payment proof orphan cleanup
  # (dry run) would delete ..."). Se pasa a "false" a mano, en un commit propio, después de revisar
  # esos logs y confirmar que lo marcado como huérfano realmente lo es.
  Storage__PaymentProofOrphanCleanup__DryRun: "true"
```

- [ ] **Step 5: Fijar las tres claves en las 39 factorías**

Inserta estas tres líneas justo debajo de cada `builder.UseSetting("Notifications:EmailProvider", "log");` de `tests/`, con la misma sangría, conservando el BOM de cada archivo (si lo tiene) y su fin de línea:

```csharp
builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
```

`24` y `24` son los valores por defecto de `PaymentProofOrphanCleanupOptions` (Step 2): fijarlos no cambia ninguna prueba, sólo impide que un `IntervalHours` fuera de rango en los user-secrets tumbe todos los hosts por `ValidateOnStart`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$anchor = 'builder.UseSetting("Notifications:EmailProvider", "log");'
$pattern = '(?m)^([ ]*)builder\.UseSetting\("Notifications:EmailProvider", "log"\);(\r?\n)'
$insertion = '$0' +
    '${1}builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");${2}' +
    '${1}builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");${2}' +
    '${1}builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");${2}'
$files = @(Get-ChildItem -Path tests -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -SimpleMatch -List -Pattern $anchor |
    ForEach-Object { $_.Path })
foreach ($path in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($path)
    $updated = [regex]::Replace($text, $pattern, $insertion)
    [System.IO.File]::WriteAllText($path, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))
}
$files.Count
$files | ForEach-Object { git diff --numstat -- $_ }
foreach ($key in "DryRun", "MinimumAgeHours", "IntervalHours") {
    $pinned = @(Get-ChildItem -Path tests -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -SimpleMatch -List -Pattern """Storage:PaymentProofOrphanCleanup:$key""")
    "{0}: {1}" -f $key, $pinned.Count
}
```

Esperado: `39`; 39 líneas `3	0	tests/...`, una por archivo (entre ellas `PaymentProofStorageHarness.cs`, `QuotationsApiHarness.cs` y `OrphanUserCleanupTests.cs`); y `DryRun: 39`, `MinimumAgeHours: 39`, `IntervalHours: 39`. Si algún número no es 39 o algún archivo muestra otra cantidad de líneas insertadas, **para y pregunta**. `CompositionRootTests.MinimalConfiguration` no se toca: arma la configuración en memoria y nunca arranca la API.

- [ ] **Step 6: El `DryRun` del harness de Storage, por prueba**

En `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`, reemplaza:

```csharp
internal sealed class StorageApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public InMemoryObjectStorage ObjectStorage { get; } = new();
```

por:

```csharp
internal sealed class StorageApiFactory(string connectionString, bool orphanCleanupDryRun = true)
    : WebApplicationFactory<Program>
{
    // Copia del flag para ConfigureWebHost, mismo criterio que el QepApiFactory de Quotations: así el
    // parámetro no queda capturado en un método y en un inicializador a la vez (CS9124).
    private readonly bool _orphanCleanupDryRun = orphanCleanupDryRun;

    public InMemoryObjectStorage ObjectStorage { get; } = new();
```

Reemplaza:

```csharp
        builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
```

por:

```csharp
        // Fijado, nunca heredado (spec 2026-09-16): las pruebas de la reconciliación eligen la corrida
        // real con orphanCleanupDryRun: false; todas las demás corren en seco. MinimumAgeHours e
        // IntervalHours quedan fijos en 24, en las dos líneas de abajo.
        builder.UseSetting(
            "Storage:PaymentProofOrphanCleanup:DryRun", _orphanCleanupDryRun ? "true" : "false");
```

Reemplaza:

```csharp
    public FixedFileReferenceProbe FileReferences { get; } = new();
```

por:

```csharp
    public FixedFileReferenceProbe FileReferences { get; } = new();

    public FixedPublicObjectReferenceProbe PublicReferences { get; } = new();
```

- [ ] **Step 7: La sonda de claves públicas y su prueba de Quotations (RED)**

Crea `src/BuildingBlocks/BuildingBlocks.Application/IPublicObjectReferenceProbe.cs`:

```csharp
namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo declara que todavía referencia un objeto del bucket público. Storage la consulta
/// antes de borrar un objeto de <c>payment-proofs/</c> que parece huérfano (spec 2026-09-16, D12) y
/// no lo borra mientras alguna sonda responda <c>true</c>: su URL puede estar en un Excel ya enviado.
/// </summary>
/// <remarks>
/// Mismo diseño que <see cref="IUserReferenceProbe"/> y <see cref="IFileReferenceProbe"/>. Storage
/// registra la suya para <c>FileResource.PublicStorageKey</c> y Quotations la suya para
/// <c>OrderPaymentProof.PublicStorageKey</c>, lo que cubre los comprobantes de v1 y de v2.
/// </remarks>
public interface IPublicObjectReferenceProbe
{
    /// <summary>Nombre del módulo que responde, para el log de por qué se conservó el objeto.</summary>
    string Source { get; }

    Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken);
}
```

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofReferenceProbeTests.cs`, reemplaza:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
```

por:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
```

y reemplaza:

```csharp
    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

por:

```csharp
    // D12: la clave pública de un comprobante adjunto está referenciada; una que nadie guardó, no.
    [Fact]
    public async Task ThePublicObjectReferenceProbeSeesTheKeysOfAttachedProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, fileId);

        await using var scope = factory.Services.CreateAsyncScope();
        var orderId = new OrderId(order.Id);
        var publicKey = await scope.ServiceProvider.GetRequiredService<QuotationsDbContext>()
            .OrderPaymentProofs
            .AsNoTracking()
            .Where(proof => proof.OrderId == orderId)
            .Select(proof => proof.PublicStorageKey)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(publicKey);
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IPublicObjectReferenceProbe>(),
            candidate => candidate.Source == "quotations");
        Assert.True(await probe.HasReferencesAsync(publicKey, TestContext.Current.CancellationToken));
        Assert.False(await probe.HasReferencesAsync(
            $"payment-proofs/{Guid.CreateVersion7():N}.pdf", TestContext.Current.CancellationToken));
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofReferenceProbeTests"
```

Esperado (RED de aserción): `Con error: 1, Superado: 1`; `ThePublicObjectReferenceProbeSeesTheKeysOfAttachedProofs` falla con `Assert.Single() Failure: The collection did not contain any matching items`. Pega la salida.

- [ ] **Step 8: Las dos sondas**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FilePublicObjectReferenceProbe.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Storage.Infrastructure.Persistence;

/// <summary>
/// Storage retiene un objeto público mientras algún archivo lo tenga como
/// <c>PublicStorageKey</c> (spec 2026-09-16, D12): un comprobante v2 ya movido. Cualquier estado
/// cuenta, igual que en <see cref="FileUserReferenceProbe"/>.
/// </summary>
internal sealed class FilePublicObjectReferenceProbe(StorageDbContext dbContext)
    : IPublicObjectReferenceProbe
{
    public string Source => "storage";

    public Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken) =>
        dbContext.FileResources.AnyAsync(
            file => file.PublicStorageKey == publicStorageKey,
            cancellationToken);
}
```

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofPublicObjectReferenceProbe.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Quotations retiene un objeto público mientras algún comprobante de pago guarde su clave (spec
/// 2026-09-16, D12): es la copia que enlaza el Excel de pedidos, de v1 o de v2. La consulta usa el
/// índice <c>IX_order_payment_proofs_public_key</c> (D17).
/// </summary>
internal sealed class OrderPaymentProofPublicObjectReferenceProbe(QuotationsDbContext dbContext)
    : IPublicObjectReferenceProbe
{
    public string Source => "quotations";

    public Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken) =>
        dbContext.OrderPaymentProofs.AnyAsync(
            proof => proof.PublicStorageKey == publicStorageKey,
            cancellationToken);
}
```

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IFileReferenceProbe, OrderPaymentProofFileReferenceProbe>();
```

por:

```csharp
        services.AddScoped<IFileReferenceProbe, OrderPaymentProofFileReferenceProbe>();
        // Sonda que Storage consulta antes de borrar un objeto huérfano de payment-proofs/ (D12).
        services.AddScoped<IPublicObjectReferenceProbe, OrderPaymentProofPublicObjectReferenceProbe>();
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IUserReferenceProbe, FileUserReferenceProbe>();
```

por:

```csharp
        services.AddScoped<IUserReferenceProbe, FileUserReferenceProbe>();
        // Spec 2026-09-16, D12: un objeto público que un archivo tiene como PublicStorageKey no es
        // huérfano.
        services.AddScoped<IPublicObjectReferenceProbe, FilePublicObjectReferenceProbe>();
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofReferenceProbeTests"
```

Esperado (GREEN): `Superado: 2`, `Con error: 0`. Pega la salida.

- [ ] **Step 9: El harness y las pruebas de la reconciliación (RED)**

En `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs`, reemplaza:

```csharp
    public static string NewPublicKey(string extension) =>
```

por:

```csharp
    /// <summary>Una corrida de la reconciliación, a mano: el worker espera IntervalHours antes de la
    /// primera.</summary>
    public static async Task<PaymentProofOrphanCleanupResult> RunOrphanCleanupAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentProofOrphanCleanupProcessor>()
            .CleanupAsync(TestContext.Current.CancellationToken);
    }

    public static string NewPublicKey(string extension) =>
```

Reemplaza:

```csharp
    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_referenced.ContainsKey(fileId));
}
```

por:

```csharp
    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_referenced.ContainsKey(fileId));
}

/// <summary>La sonda de claves públicas de otro módulo (en producción, Quotations), a mano.</summary>
internal sealed class FixedPublicObjectReferenceProbe : IPublicObjectReferenceProbe
{
    private readonly ConcurrentDictionary<string, bool> _referenced = new(StringComparer.Ordinal);

    public string Source => "test";

    public void Reference(string publicStorageKey) => _referenced[publicStorageKey] = true;

    public Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken) =>
        Task.FromResult(_referenced.ContainsKey(publicStorageKey));
}
```

Y reemplaza:

```csharp
            services.AddSingleton<IFileReferenceProbe>(FileReferences);
```

por:

```csharp
            services.AddSingleton<IFileReferenceProbe>(FileReferences);
            services.AddSingleton<IPublicObjectReferenceProbe>(PublicReferences);
```

Crea `tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofOrphanCleanupTests.cs`:

```csharp
using Modules.Storage.Infrastructure.PaymentProofs;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La reconciliación de payment-proofs/ (spec 2026-09-16, D12 y sección 4). Con DryRun en false
/// borra el huérfano viejo y respeta el referenciado —por un archivo movido o por un pedido— y el
/// reciente, sin listar otros prefijos. Con DryRun en true no borra nada.
/// </summary>
public sealed class PaymentProofOrphanCleanupTests
{
    [Fact]
    public async Task ARealRunDeletesOnlyOldUnreferencedPaymentProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString(), orphanCleanupDryRun: false);
        using var client = CreateClient(factory);
        var old = DateTimeOffset.UtcNow.AddHours(-48);
        var orphan = NewPublicKey(".pdf");
        var recent = NewPublicKey(".pdf");
        var referencedByAnOrder = NewPublicKey(".webp");
        var moved = await MovedProofKeyAsync(client, factory);
        factory.PublicObjectStorage.Put(orphan, old);
        factory.PublicObjectStorage.Put(recent, DateTimeOffset.UtcNow.AddHours(-1));
        factory.PublicObjectStorage.Put(referencedByAnOrder, old);
        factory.PublicObjectStorage.Put(moved, old);
        factory.PublicObjectStorage.Put("tenants/abc/media/def/original.png", old);
        factory.PublicObjectStorage.Put("quotations/tenants/abc/cotizacion.pdf", old);
        factory.PublicReferences.Reference(referencedByAnOrder);

        var result = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 4, Recent: 1, Referenced: 2, Orphans: 1, Deleted: 1),
            result);
        Assert.Equal([orphan], factory.PublicObjectStorage.DeletedKeys);
        Assert.False(factory.PublicObjectStorage.Exists(orphan));
        Assert.True(factory.PublicObjectStorage.Exists(recent));
        Assert.True(factory.PublicObjectStorage.Exists(referencedByAnOrder));
        Assert.True(factory.PublicObjectStorage.Exists(moved));
        Assert.True(factory.PublicObjectStorage.Exists("tenants/abc/media/def/original.png"));
        Assert.True(factory.PublicObjectStorage.Exists("quotations/tenants/abc/cotizacion.pdf"));
        // Cuatro objetos de a dos por página: dos páginas, las dos de payment-proofs/.
        Assert.Equal(["payment-proofs/", "payment-proofs/"], factory.PublicObjectStorage.ListedPrefixes);
        var audit = Assert.Single(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
        Assert.Equal(orphan, audit.ResourceId);
        Assert.Equal("public_object", audit.ResourceType);
        Assert.Equal("System", audit.ActorType);
        Assert.Null(audit.TenantId);
    }

    // D12: arranca en modo solo-registrar; nada se borra hasta apagar DryRun a mano.
    [Fact]
    public async Task ADryRunDeletesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var orphan = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(orphan, DateTimeOffset.UtcNow.AddHours(-48));

        var result = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 1, Recent: 0, Referenced: 0, Orphans: 1, Deleted: 0),
            result);
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(factory.PublicObjectStorage.Exists(orphan));
        Assert.DoesNotContain(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
    }

    // Un comprobante v2 ya movido: su FileResource guarda la clave, y la sonda de Storage lo retiene.
    private static async Task<string> MovedProofKeyAsync(HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return publicKey;
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
```

Esperado (RED de compilación): `error CS0246: … 'PaymentProofOrphanCleanupResult' …` y `… 'IPaymentProofOrphanCleanupProcessor' …`. Pega la salida.

- [ ] **Step 10: El procesador, el worker y sus registros**

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupProcessor.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofOrphanCleanupProcessor
{
    Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>Lo que hizo una corrida: los objetos listados bajo el prefijo, los que se saltearon por
/// recientes o referenciados, los huérfanos y los que efectivamente se borraron.</summary>
internal sealed record PaymentProofOrphanCleanupResult(
    int Listed,
    int Recent,
    int Referenced,
    int Orphans,
    int Deleted);

// Spec 2026-09-16, D12 y sección 4. Recorre sólo payment-proofs/ del bucket público y borra lo que
// ningún módulo referencia. Los objetos de ahí pueden estar enlazados en un Excel ya enviado, así que
// se salta lo reciente (al adjuntar se copia antes de guardar el pedido, D9) y, con DryRun, sólo se
// registra. Qué referencia un objeto lo decide cada módulo por IPublicObjectReferenceProbe: este
// procesador no conoce a Quotations.
internal sealed partial class PaymentProofOrphanCleanupProcessor(
    IPublicObjectStorage publicObjectStorage,
    IEnumerable<IPublicObjectReferenceProbe> probes,
    IStorageAuditPublisher auditPublisher,
    IStorageUnitOfWork unitOfWork,
    IClock clock,
    IOptions<StorageOptions> options,
    ILogger<PaymentProofOrphanCleanupProcessor> logger) : IPaymentProofOrphanCleanupProcessor
{
    // Con la barra: sin ella también entraría "payment-proofs-viejos/". Mismo prefijo que
    // PublicPaymentProofPublisher. Nunca tenants/.../media (imágenes de producto) ni quotations/.
    internal const string Prefix = "payment-proofs/";

    private readonly IReadOnlyList<IPublicObjectReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof orphan cleanup skipped: the public bucket is not configured.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment proof orphan cleanup (dry run) would delete {Key}, last modified {LastModified}.")]
    private static partial void LogWouldDelete(ILogger logger, string key, DateTimeOffset lastModified);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment proof orphan cleanup deleted {Key}, last modified {LastModified}.")]
    private static partial void LogDeleted(ILogger logger, string key, DateTimeOffset lastModified);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof orphan cleanup finished: {Listed} listed, {Recent} recent, {Referenced} referenced, {Orphans} orphans, {Deleted} deleted (dry run: {DryRun}).")]
    private static partial void LogFinished(
        ILogger logger, int listed, int recent, int referenced, int orphans, int deleted, bool dryRun);

    public async Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        // Sin bucket público no hay nada que recorrer, y el adaptador llamaría a R2 igual.
        if (!publicObjectStorage.IsConfigured)
        {
            LogNotConfigured(logger);
            return new PaymentProofOrphanCleanupResult(0, 0, 0, 0, 0);
        }

        var settings = options.Value.PaymentProofOrphanCleanup;
        var cutoff = clock.UtcNow.AddHours(-settings.MinimumAgeHours);
        int listed = 0, recent = 0, referenced = 0, orphans = 0, deleted = 0;
        string? continuationToken = null;
        do
        {
            var page = await publicObjectStorage.ListAsync(Prefix, continuationToken, cancellationToken);
            foreach (var stored in page.Objects)
            {
                // Defensa: si el adaptador devolviera algo fuera del prefijo, no se toca.
                if (!stored.Key.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                listed++;
                if (stored.LastModified > cutoff)
                {
                    recent++;
                    continue;
                }

                if (await IsReferencedAsync(stored.Key, cancellationToken))
                {
                    referenced++;
                    continue;
                }

                orphans++;
                if (settings.DryRun)
                {
                    LogWouldDelete(logger, stored.Key, stored.LastModified);
                    continue;
                }

                await publicObjectStorage.DeleteAsync(stored.Key, cancellationToken);
                // Auditado y guardado por objeto, no al final: si el proceso muere a mitad del
                // recorrido, lo ya borrado queda auditado.
                auditPublisher.PublishSystem(
                    tenantId: null,
                    "storage.public_object.purged",
                    "public_object",
                    stored.Key,
                    "success",
                    clock.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                LogDeleted(logger, stored.Key, stored.LastModified);
                deleted++;
            }

            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        LogFinished(logger, listed, recent, referenced, orphans, deleted, settings.DryRun);
        return new PaymentProofOrphanCleanupResult(listed, recent, referenced, orphans, deleted);
    }

    private async Task<bool> IsReferencedAsync(string publicStorageKey, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(publicStorageKey, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
```

Crea `src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupWorker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D6 y D12: la reconciliación corre dentro de la API, cada IntervalHours. El primer
// tick llega después de un intervalo completo, como StagingCleanupWorker: un reinicio de pod no
// dispara un recorrido del bucket, y ningún host de pruebas sale a R2.
internal sealed partial class PaymentProofOrphanCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<PaymentProofOrphanCleanupWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof orphan cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(options.Value.PaymentProofOrphanCleanup.IntervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofOrphanCleanupProcessor>();
                await processor.CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
    }
}
```

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddHostedService<PaymentProofMoveWorker>();
```

por:

```csharp
        services.AddHostedService<PaymentProofMoveWorker>();
        // Spec 2026-09-16, D12: la reconciliación de payment-proofs/, en modo solo-registrar hasta
        // que alguien apague Storage:PaymentProofOrphanCleanup:DryRun.
        services.AddScoped<IPaymentProofOrphanCleanupProcessor, PaymentProofOrphanCleanupProcessor>();
        services.AddHostedService<PaymentProofOrphanCleanupWorker>();
```

- [ ] **Step 11: Correrlas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofReferenceProbeTests|FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: los tres con `Con error: 0`; `PaymentProofOrphanCleanupTests` con 2 superadas; las 30 de Quotations (28 de `OrderPaymentProofPublicationApiTests` + 2 de `PaymentProofReferenceProbeTests`) superadas. Pega los resúmenes.

- [ ] **Step 12: README**

En `README.md`, reemplaza:

```markdown
| `Storage:StagingCleanupMinutes`                        | `60`                                                                                          | Período del barrido de staging. Debe ser positivo                                                                   |
```

por:

```markdown
| `Storage:StagingCleanupMinutes`                        | `60`                                                                                          | Período del barrido de staging. Debe ser positivo                                                                   |
| `Storage:PaymentProofOrphanCleanup:MinimumAgeHours`    | `24`                                                                                          | Edad mínima de un objeto de `payment-proofs/` para que la reconciliación lo considere. Debe ser positiva             |
| `Storage:PaymentProofOrphanCleanup:IntervalHours`      | `24`                                                                                          | Período de la reconciliación de `payment-proofs/`. Entre 1 y 1193                                                   |
| `Storage:PaymentProofOrphanCleanup:DryRun`             | `true` en `appsettings.json` y en `k8s/prod-configMap.yaml`                                   | Con `true` la reconciliación sólo escribe en el log lo que borraría. Se pasa a `false` a mano, después de revisar esos logs en producción |
```

Reemplaza:

```markdown
público** con la clave aleatoria `payment-proofs/{guid}.{pdf|jpg|png}`, y el Excel de pedidos lo
```

por:

```markdown
público** con la clave aleatoria `payment-proofs/{guid}.{pdf|jpg|png|webp}`, y el Excel de pedidos lo
```

Y reemplaza:

```markdown
### Plantilla de WhatsApp (Zenvia)
```

por:

```markdown
### Comprobantes de pago v2: temporal, WebP y movimiento

Desde el 2026-09-16 el frontend sube los comprobantes con `ownerType: "PaymentProof"`
([spec](docs/superpowers/specs/2026-09-16-comprobantes-publicos-v2-design.md)). Uno así:

1. **No se promueve a `files/`**: al completar la subida queda `Available` en `staging/`. Si es JPG,
   PNG o WebP se reemplaza ahí mismo por un WebP de lado mayor ≤ 2000 px y calidad 80, sin agrandar
   y sin EXIF; un PDF queda tal cual.
2. **Al adjuntarse a un pedido** se copia al bucket público como en v1 y, en la misma transacción que
   el pedido, Quotations escribe `quotations.order.payment-proofs-attached.v1` en el outbox.
3. **`PaymentProofMoveWorker`** (cada 3 s) consume ese evento con el inbox `storage.inbox_messages`:
   primero borra el temporal y después registra el movimiento en `FileResource.PublicStorageKey`.
   Desde ahí la descarga desde la app devuelve la URL pública, que el navegador **abre** en vez de
   bajar con el nombre original. En el mismo tick, después del movimiento, consume también
   `quotations.order.payment-proofs-detached.v1` (D19, ver «Reemplazar o quitar un comprobante
   borra su archivo», arriba).
4. **El barrido de staging** (cada `Storage:StagingCleanupMinutes`) purga el comprobante que sigue
   sin mover después de `Storage:StagingRetentionHours` si ningún módulo lo referencia, y lo audita
   como `storage.file.purged` / `payment_proof_not_attached`.
5. **`PaymentProofOrphanCleanupWorker`** (cada `Storage:PaymentProofOrphanCleanup:IntervalHours`)
   recorre sólo `payment-proofs/` del bucket público y borra lo que tiene más de `MinimumAgeHours` y
   no referencia ningún `FileResource` ni `OrderPaymentProof`, auditándolo como
   `storage.public_object.purged`. **Arranca con `DryRun=true`** en `appsettings.json` y en el
   ConfigMap: sólo escribe `Payment proof orphan cleanup (dry run) would delete …` en el log. Se pasa
   a `false` a mano, en un commit propio, después de revisar esos logs en producción.

Un comprobante ya movido no se puede adjuntar a otro pedido (422 `order.payment_proof.file_not_available`),
y uno que un pedido referencia no se borra ni se despublica desde la API de Storage (ver «Nada se
despublica solo», arriba). Reemplazarlo o quitarlo del pedido sí lo borra (D19). `FileUserReferenceProbe`
cuenta también los `PaymentProof`: quien subió un comprobante no se borra como usuario huérfano.

Borrar o despublicar desde la API de Storage un `PaymentProof` movido que ya ningún pedido referencia
lo deja **sin ninguna copia**: su temporal se borró al moverlo y la copia pública se va con la
operación. D15 lo permite porque ya no es la evidencia de ningún pedido; el frontend no llama a esos
endpoints.

Los comprobantes `User` (los de v1) no cambian, salvo que reemplazarlos o quitarlos de un pedido borra
la copia pública de ese adjunto. Con `Quotations:PaymentProofs:PublicLinks=false` no hay copia ni
evento de adjunto, así que un `PaymentProof` adjunto se queda en `staging/`, retenido del barrido por
la sonda de Quotations; si el pedido lo suelta, el evento de retiro sale igual y Storage borra el
temporal. Una regla de lifecycle sobre `staging/` en el bucket privado borraría comprobantes que
todavía no se adjuntaron o no se movieron: no debe haber ninguna.

### Plantilla de WhatsApp (Zenvia)
```

- [ ] **Step 13: Build completo, arquitectura y chequeo de formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; los dos proyectos con `Con error: 0`. Después corre «el chequeo de formato»: en las 39 factorías sólo cambiaron las tres líneas del Step 5, así que cualquier diagnóstico que no sea `ENDOFLINE`/`CHARSET` en otra línea ya estaba (anótalo, no lo arregles).

- [ ] **Step 14: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$changedTests = @(git diff --name-only -- tests)
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add README.md k8s/prod-configMap.yaml src/Api/appsettings.json src/Api/appsettings.example.json src/BuildingBlocks/BuildingBlocks.Application/IPublicObjectReferenceProbe.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptions.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageOptionsValidator.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs src/Modules/Storage/Modules.Storage.Infrastructure/Persistence/FilePublicObjectReferenceProbe.cs src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupProcessor.cs src/Modules/Storage/Modules.Storage.Infrastructure/PaymentProofs/PaymentProofOrphanCleanupWorker.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderPaymentProofPublicObjectReferenceProbe.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofOrphanCleanupTests.cs $changedTests; git commit -m "feat(storage): reconciliar los huérfanos de payment-proofs/" -m "PaymentProofOrphanCleanupWorker recorre sólo payment-proofs/ del bucket público, salta lo reciente y lo que alguna IPublicObjectReferenceProbe referencia, y con DryRun sólo registra. DryRun arranca en true en appsettings y en el ConfigMap, y todas las factorías de integración fijan las tres claves nuevas (spec 2026-09-16, D6 y D12)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `$changedTests` trae las 39 factorías, `StorageOptionsValidatorTests.cs` y `PaymentProofReferenceProbeTests.cs`; `git status --short` vacío y el `Select-String` sin salida.

---

### Task 11: `qep-frontend` sube los comprobantes como `PaymentProof`

El cambio de una línea del spec, en un worktree propio de `qep-frontend` desde `origin/develop` (hallazgo 17). **D14: esta rama se mergea recién cuando el backend de Tasks 1–10 está desplegado**; con el backend viejo, el endpoint rechazaría `PaymentProof` con `storage.file.owner_type_invalid`.

**Files (en `qep-frontend`):**
- Modify: `src/features/quotes/services/quote-file-upload.ts:4,16-18,72`
- Modify: `src/features/quotes/services/quote-file-upload.test.ts:64,74`

**Interfaces:**
- Consumes: `FileOwnerType.PaymentProof` aceptado por `POST /api/v1/tenants/{tenantId}/files` (Tasks 1 y 4); el worktree de `qep-frontend` y el baseline de Vitest (Task 0, Step 6).
- Produces: `uploadQuoteFile(tenantId, ownerId, file)` manda `ownerType: 'PaymentProof'`. Su firma no cambia.

- [ ] **Step 1: Comprobar el worktree de Task 0**

El worktree y sus dependencias los creó Task 0, Step 6, y ahí quedó el baseline de la suite. No lo vuelvas a crear.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
git branch --show-current
git status --short
git log --oneline -1
Test-Path (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline-failed.txt")
```

Esperado: la rama es `feature/comprobantes-publicos-v2`; `git status` vacío; el último commit es el mismo que anotaste en Task 0, Step 6; y `True`. Si el worktree no existe o el baseline falta, vuelve a Task 0, Step 6 antes de tocar código. No toques el checkout principal de `qep-frontend`: tiene commits sin publicar y archivos con fines de línea cambiados que no son nuestros.

- [ ] **Step 2: La prueba (RED)**

En `src/features/quotes/services/quote-file-upload.test.ts`, reemplaza:

```ts
  it('opens the session with ownerId = the user id and ownerType "User"', async () => {
```

por:

```ts
  // Spec de backend 2026-09-16 (D2): los comprobantes se suben como PaymentProof, que el backend
  // deja en su ruta temporal y mueve al bucket público al adjuntarlos al pedido.
  it('opens the session with ownerId = the user id and ownerType "PaymentProof"', async () => {
```

y reemplaza:

```ts
      ownerType: 'User',
```

por:

```ts
      ownerType: 'PaymentProof',
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
bun run test src/features/quotes/services/quote-file-upload.test.ts
```

Esperado (RED de aserción): falla `opens the session with ownerId = the user id and ownerType "PaymentProof"` con `AssertionError: expected { …(5) } to deep equal { …(5) }` y el diff `- "ownerType": "PaymentProof"` / `+ "ownerType": "User"`; las demás pruebas del archivo pasan. Pega la salida.

- [ ] **Step 3: La implementación**

En `src/features/quotes/services/quote-file-upload.ts`, reemplaza:

```ts
 * Subir el PDF de envío o un comprobante de pago es tres pasos, no cuatro — a diferencia
```

por:

```ts
 * Subir un comprobante de pago es tres pasos, no cuatro — a diferencia
```

Reemplaza:

```ts
 * `ownerType` es `'User'` para estos archivos (a diferencia de `'Product'` en catalog):
 * `ownerId` es el id del usuario autenticado (`useSession().userId`), no el de la
 * cotización ni el del pedido.
```

por:

```ts
 * `ownerType` es `'PaymentProof'` (a diferencia de `'Product'` en catalog): desde el
 * 2026-09-16 el backend deja estos archivos en su ruta temporal, reduce las imágenes a WebP
 * y los mueve al bucket público al adjuntarlos al pedido. Sólo los comprobantes usan esta
 * función. `ownerId` es el id del usuario autenticado (`useSession().userId`), no el de la
 * cotización ni el del pedido.
```

Y reemplaza:

```ts
        ownerType: 'User',
```

por:

```ts
        ownerType: 'PaymentProof',
```

- [ ] **Step 4: Pruebas, lint y formato (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
bun run test src/features/quotes
bun run lint
bunx prettier --check src/features/quotes/services/quote-file-upload.ts src/features/quotes/services/quote-file-upload.test.ts
bun run build
```

Esperado: las pruebas de `src/features/quotes` en verde, o sólo con fallas que ya están en `qep-comprobantes-v2-frontend-baseline-failed.txt` (Task 0; el repo no tiene CI de pruebas, y la suite entera se compara en Task 12, Step 4); `oxlint` sin errores; `All matched files use Prettier code style!`; `bun run build` termina sin errores de `tsc`. Pega las salidas.

- [ ] **Step 5: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
if ((git branch --show-current) -ne "feature/comprobantes-publicos-v2") { throw "ABORT: rama equivocada" }; git add src/features/quotes/services/quote-file-upload.ts src/features/quotes/services/quote-file-upload.test.ts; git commit -m "feat(quotes): subir los comprobantes de pago como PaymentProof" -m "El backend deja estos archivos en staging, reduce las imágenes a WebP y los mueve al bucket público al adjuntarlos (spec de backend 2026-09-16, D2). Se mergea después de desplegar el backend (D14)."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida. La rama no se publica desde este plan.

---

### Task 12: Verificación final

Nada de código nuevo salvo lo que esta verificación pida arreglar (en ese caso, un commit `fix(...)` con el mismo guard).

**Files:**
- Ninguno, salvo correcciones.

**Interfaces:**
- Consumes: `$env:TEMP\qep-comprobantes-v2-baseline-failed.txt`, `$env:TEMP\qep-comprobantes-v2-frontend-baseline-failed.txt` y los exit codes de lint y build de `origin/develop` (Task 0) y todas las tareas anteriores, las B, C y D incluidas.
- Produces: el handoff final.

- [ ] **Step 1: Restore y build desde cero**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Storage/Modules.Storage.Infrastructure --context StorageDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
git status --short
```

Esperado: restore en `--locked-mode` sin `NU1004` (ningún `packages.lock.json` debía cambiar); build con `0 Advertencia(s)` y `0 Errores`; dos veces `No changes have been made to the model since the last migration.`; `git status` vacío.

- [ ] **Step 2: La suite, un proyecto por comando**

Mismo procedimiento que Task 0, Step 4, con otra carpeta. Corre cada línea como un comando aparte, en primer plano:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Remove-Item -Recurse -Force (Join-Path $env:TEMP "qep-comprobantes-v2-final") -ErrorAction SilentlyContinue
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Audit/Modules.Audit.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Audit/Modules.Audit.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Authorization/Modules.Authorization.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Platform/Modules.Platform.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Storage/Modules.Storage.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos; dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --no-build --logger trx --results-directory (Join-Path $env:TEMP "qep-comprobantes-v2-final")
```

Si una línea se corta a los 10 minutos, parte ese proyecto por clase como en Task 0.

- [ ] **Step 3: Comparar las fallas con el baseline, por nombre**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$final = Join-Path $env:TEMP "qep-comprobantes-v2-final"
$failed = @(Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique)
$baseline = @(Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-baseline-failed.txt") -ErrorAction SilentlyContinue)
"Nuevas fallas:"
$failed | Where-Object { $_ -notin $baseline }
"Fallas del baseline que ya pasan:"
$baseline | Where-Object { $_ -notin $failed }
```

Esperado: `Nuevas fallas:` sin nada debajo. Si aparece alguna, **para**: corrígela en un commit `fix(...)` o pregunta si no es de esta rama. Pega la salida.

- [ ] **Step 4: La suite completa del frontend, lint y build, contra el baseline**

En el worktree de `qep-frontend`, con la rama de Task 11. Mismos comandos que Task 0, Step 6, en primer plano y sin pipe:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
git branch --show-current
git status --short
$frontendFinal = Join-Path $env:TEMP "qep-comprobantes-v2-frontend-final.json"
Remove-Item -Force $frontendFinal -ErrorAction SilentlyContinue
bun run test --reporter=default --reporter=json "--outputFile.json=$frontendFinal"
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
bun run lint
"lint exit: $LASTEXITCODE"
bun run build
"build exit: $LASTEXITCODE"
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\comprobantes-publicos-v2
$report = Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-final.json") -Raw | ConvertFrom-Json
$failed = @(
    $report.testResults | ForEach-Object { $_.assertionResults } |
        Where-Object { $_.status -eq "failed" } |
        ForEach-Object { $_.fullName }
    $report.testResults |
        Where-Object { $_.status -eq "failed" -and @($_.assertionResults).Count -eq 0 } |
        ForEach-Object { "SUITE " + (($_.name -replace '\\', '/') -replace '^.*?/src/', 'src/') }
) | Sort-Object -Unique
$baseline = @(Get-Content (Join-Path $env:TEMP "qep-comprobantes-v2-frontend-baseline-failed.txt") -ErrorAction SilentlyContinue)
"Pruebas: {0}, con error: {1}" -f $report.numTotalTests, $report.numFailedTests
"Nuevas fallas:"
$failed | Where-Object { $_ -notin $baseline }
"Fallas del baseline que ya pasan:"
$baseline | Where-Object { $_ -notin $failed }
```

Esperado: la rama es `feature/comprobantes-publicos-v2` y `git status` vacío; `Nuevas fallas:` sin nada debajo; el total de pruebas no menor que el de Task 0; `lint exit` y `build exit` en `0`, o con el mismo exit distinto de cero que anotaste en Task 0 y sin errores nuevos en los archivos de Task 11. Si aparece una falla nueva o un error de lint o de `tsc` en `quote-file-upload.ts` o su prueba, **para**: corrígelo en un commit `fix(quotes): …` con el guard de rama de Task 11, o pregunta si no es de esta rama. Pega las salidas.

- [ ] **Step 5: Formato de toda la rama y atribución de los commits**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$base = git merge-base origin/develop HEAD
git log --oneline "$base..HEAD"
git log --format=%B "$base..HEAD" | Select-String -SimpleMatch "Co-Authored-By"
$files = @(git diff --name-only "$base..HEAD" -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' -and (Test-Path $_) }
$report = Join-Path $env:TEMP "qep-comprobantes-v2-format-final"
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

Esperado: el `log` con los cinco commits de documentación —el spec (`a8a5424`, `df294d7` y `da80432`) y el plan (`01a56fe` y `docs(storage): plan v2 rebasado sobre develop y con D19`)— y los 16 de Tasks 1–10 con sus B, C y D (1, 1B, 2, 3, 4, 5, 6, 7, 7B, 8, 8B, 9, 9B, 9C, 9D y 10): 21 commits, más los `fix` que hayan hecho falta. Task 0 no commitea nada (pre-flight C5). El `Select-String` sin salida; el bloque de formato sin salida o sólo con líneas previas ya anotadas.

- [ ] **Step 6: Verificaciones manuales que las pruebas no cubren**

Anota el resultado de cada una en el handoff; ninguna se corre contra producción sin que el developer lo pida:

1. **Lifecycle del bucket privado.** `npx wrangler r2 bucket lifecycle list <bucket-privado>`: no debe haber una regla sobre `staging/` (README, sección de comprobantes v2). Y `npx wrangler r2 bucket lifecycle list <bucket-publico>`: ninguna sobre `payment-proofs/`.
2. **Orden de despliegue (D14).** El backend va primero; la rama `feature/comprobantes-publicos-v2` de `qep-frontend` se mergea sólo con el backend nuevo en producción.
3. **`DryRun`.** Después de desplegar, esperar al menos una corrida (24 h) y revisar en los logs del pod las líneas `Payment proof orphan cleanup (dry run) would delete …` y `Payment proof orphan cleanup finished: …`. Apagar `Storage__PaymentProofOrphanCleanup__DryRun` es un commit aparte, fuera de este plan.
4. **D15 en un entorno con R2.** Si el developer lo pide, `DELETE /files/{id}` de un comprobante adjunto y movido responde 422 `storage.file.invalid_state` y su URL pública sigue abriendo.
5. **D19 en un entorno con R2.** Si el developer lo pide, quitar de un pedido pendiente un comprobante movido (`DELETE /order/proofs/{proofId}`) deja, segundos después, su URL pública respondiendo 404 y su fila de `storage.file_resources` en `Purged`, con una entrada `storage.file.purged` / `payment_proof_detached` en `audit.entries`.

- [ ] **Step 7: Handoff**

Pega en el handoff: la lista de commits de las dos ramas, el resumen de cada proyecto de pruebas (Superado/Con error/Omitido), las dos comparaciones contra el baseline (backend, Step 3; frontend, Step 4), el resultado de Step 6 y cualquier diagnóstico de formato previo que hayas anotado. Suma, tal cual, el riesgo aceptado del pre-flight C2: «Borrar o despublicar desde la API de Storage un `PaymentProof` movido que ningún pedido referencia lo deja sin ninguna copia (su temporal ya no existe); D15 lo permite y el frontend no llama a esos endpoints.» Ninguna rama se publica desde este plan.

---

## Autorrevisión contra el spec

| Decisión o prueba del spec | Tarea |
| --- | --- |
| D1 — PDF tal cual, imágenes a WebP | 2 (`Supports`), 4 (`APdfProofStaysUntouchedInStaging`), 7 (`AnAttachedPdfProofIsMovedAsIs`) |
| D2 — `FileOwnerType.PaymentProof = 5` | 1, 4 (endpoint), 11 (frontend) |
| D3 — se mueve al adjuntar | 6 (evento en los dos handlers, con los archivos de reemplazo), 7 |
| D4 — espera en `staging/`, nunca a `files/` | 4 |
| D5 — descarga con la URL pública | 8 |
| D6 — workers dentro de la API | 7, 9, 10 (`BackgroundService` + procesador) |
| D7 — ≤ 2000 px, WebP 80, sin agrandar | 2, 4 |
| D8 — se procesa al completar la subida | 4 |
| D9 — copia antes, borra después por outbox | 6 (evento en la misma transacción), 7 (orden borrar → guardar) |
| D10 — `MoveToPublic` propio, `Publish` intacto | 1, 7 |
| D11 — barrido de `Available` sin mover y sin referencia | 1 (`PurgeUnattachedPaymentProof`), 9 |
| D12 — reconciliación de `payment-proofs/`, 24 h, diaria, en seco | 3 (listado), 10 |
| D13 — `User` sin cambios | 4 (`AUserImageIsStillPromotedWithItsThumbnail`), 7 (`AUserFileIsIgnored`, `AUserProofIsCopiedButNeverMoved`), 8 (`APublishedUserImageIsStillSigned`), 9 (`AUserFileIsNotSwept`), 9C (`AUserFileKeepsItsOriginalAndLosesOnlyTheCopyOfThatAttachment`), 9D (`ARemovedUserProofLosesItsPublicCopyAndKeepsItsOriginal`) |
| D14 — backend primero | 11 (la rama del frontend espera), 12 (Step 6) |
| D15 — borrar y despublicar rechazan un `PaymentProof` referenciado; publicar rechaza siempre un `PaymentProof` | 9B (`PaymentProofGuard`; `DeletingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy`, `UnpublishingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy`, `PublishingAPaymentProofIsAlwaysRejected`, sin referencia como antes; `DeletingAMovedProofThatAnOrderReferencesIsRejected` de punta a punta; README) |
| D16 — un comprobante movido no se adjunta a otro pedido | 7B (`QuotationFileLookupTests.AMovedPaymentProofIsNotAvailable`; `AnAlreadyMovedPaymentProofCannotBeAttachedToAnotherOrder`: 422 `order.payment_proof.file_not_available` y sin copia nueva) |
| D17 — índices sobre `order_payment_proofs.file_id` y `public_storage_key` | 8B (`OrderPaymentProofsHaveAnIndexForEachReferenceProbe`, `TheModelHasNoChangesPendingAMigration` RED/GREEN, migración generada `AddOrderPaymentProofReferenceIndexes`) |
| D18 — `FileUserReferenceProbe` cuenta los `PaymentProof` | 1B (`OrphanUserCleanupTests.OwningAPaymentProofKeepsTheUser`; cualquier estado, así que un purgado por D19 también retiene) |
| D19 — reemplazar o quitar borra el archivo que el pedido suelta | 6 (el reemplazo entra en el evento de adjuntos: `ReplacingAProofFileWithPublicLinksOnWritesAnEventWithTheReplacement`, `OnlyTheReplacementFilesWithAPublicKeyAreAttached`), 9C (`PurgeDetachedPaymentProof` con sus 4 unitarias; `AMovedProofIsPurgedAndItsPublicObjectDeleted`, auditoría `payment_proof_detached`), 9D (evento de retiro en los dos handlers y sin el borrado best-effort: `ReplacingAProofFileWritesTheDetachedEventWithTheOldFile`, `RemovingAProofWritesTheDetachedEvent`, `OnlyTheFilesTheOrderNoLongerUsesAreDetached`; de punta a punta: `AReplacedPaymentProofImageIsPurgedWithItsPublicCopy`, `ARemovedPaymentProofImageIsPurgedWithItsPublicCopy`; README, guía de integración y resumen de `PublicPaymentProofPublisher`) |
| D19 — misma transacción que el pedido | 9D (`CorrectingOnlyAmountsWritesNoDetachedEvent`, `ARejectedRemovalWritesNoDetachedEvent`) |
| D19 — la carrera: retiro antes del movimiento | 9C (`AProofDetachedBeforeItsMoveLosesItsCopyAndTheMoveSkipsIt`, RED de aserción en dos fases; el movimiento lo salta por la regla de Task 7) |
| D19 — con `PublicLinks=false` | 9C (`AStagedProofWithoutACopyIsPurgedWithItsStagingObject`), 9D (`RemovingAProofWithPublicLinksOffWritesTheDetachedEventWithoutKey`) |
| D19 — retenido, una sola vez, orden borrar → guardar | 9C (`AProofStillReferencedIsKept`, `ADetachIsAppliedOnlyOnce`, `WhenADeleteFailsNothingIsSavedAndTheMessageComesBack`) |
| D19 — D15 y D16 no cambian | 9C no pasa por los handlers de 9B; un `Purged` no está `Available`, así que la regla de 7B lo rechaza con `order.payment_proof.file_not_available` |
| D19 — «Pruebas de D19» del spec | Unitarias: 9C (dominio), 6 y 9D (`PaymentProofCopies`). Quotations: 6 y 9D. Punta a punta: 9D. Storage: 9C |
| Pre-flight C1–C7 | C1: hallazgo 22, D19 y Tasks 6, 9C y 9D. C2: README de Task 10 y handoff de Task 12, Step 7. C3: Task 7, Step 11 → Task 8, Step 5. C4: Task 9B, Step 5 y Task 9D, Step 8. C5: Task 0, Step 7, «Entrega» y Task 12, Step 5. C6: Task 6, Interfaces. C7: «File Structure», Task 1 Files y Task 10 Files |
| Unit: el procesador reduce, no agranda, WebP 80, rechaza corrupta | 2 (y `TheResultCarriesNoExifIccOrXmpProfile`, RED sin las líneas del Step 6) |
| Unit: `MoveToPublic` sólo `PaymentProof` `Available`, acepta PDF, idempotente | 1 |
| Unit: `OrderPaymentProofResolver` acepta `image/webp` | 5 |
| Unit: la tabla de extensiones suma `.webp` | 5 |
| Unit: `IssueDownloadUrlHandler` URL pública sólo para un `PaymentProof` movido | 8 |
| Integración: imagen WebP, `Available`, en `staging/`, sin variantes; PDF intacto | 4 |
| Integración: convertir y sumar copian y escriben el evento; el worker mueve y borra el temporal | 6 (evento), 7 (de punta a punta y procesador) |
| Integración: una falla antes de guardar borra la copia y deja el temporal | 6 (sin evento), 7 (`AConversionWhoseCopyFailsLeavesThePaymentProofsInStaging`) |
| Integración: el barrido borra el no adjuntado viejo y respeta el adjuntado | 9 |
| Integración: reconciliación real borra el huérfano viejo, respeta referenciado y reciente, no lista otros prefijos; en seco no borra nada | 10 |
| Integración: los `User` siguen v1 | 4, 7, 9 (arriba) |
| Integración: toda factoría fija `DryRun` | 10, Step 5 (39 factorías, con `MinimumAgeHours` e `IntervalHours` también fijadas y contadas) |
| Frontend: `uploadQuoteFile` manda `PaymentProof` (Vitest) | 11; la suite entera, lint y build contra el baseline de 0 (Step 6) en 12 (Step 4) |
| Sección 3: auditoría `storage.file.downloaded` igual | 8 (unitarias) |
| Sección 3: `storage.file.purged` / `payment_proof_not_attached` | 9 |
| Sección 4: `storage.public_object.purged` y `DryRun` en `appsettings.json` y ConfigMap | 10 |
| Enmiendas: `PublicLinks=false` deja el `PaymentProof` adjunto en `staging/`; Storage necesita inbox y migración | 9 (retenido por la sonda, iteración con `Skip`), 7 (`AddStorageInbox`) |
| Fuera de alcance (migrar v1, otros archivos, API/CronJob, despublicar, por tenant) | No se implementa. D15 no despublica nada: sólo impide borrar la copia de un comprobante adjunto. D19 es la excepción decidida por el owner: la copia de un comprobante reemplazado o quitado se borra (9C, 9D) |
