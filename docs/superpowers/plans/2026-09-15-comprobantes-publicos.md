# Comprobantes de pago públicos en el Excel de pedidos — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Con `Quotations:PaymentProofs:PublicLinks` encendida, cada comprobante de pago nuevo se copia al bucket público de R2 al adjuntarse, y el Excel de pedidos suma cuatro columnas —la cantidad y los tres primeros comprobantes— con un enlace «Ver» que se abre con un clic.

**Architecture:** Un puerto nuevo en `Modules.Quotations.Application` (`IPaymentProofPublisher`) con dos implementaciones en el Bootstrapper —la que copia con `IPublicObjectStorage` y la que no hace nada—, elegidas por DI según la clave. Los dos handlers que adjuntan comprobantes publican sólo los nuevos antes del dominio, guardan la clave en la columna nueva `public_storage_key` y borran las copias si el request falla después. El Excel lee los comprobantes con una consulta por lote y el writer de OpenXML aprende a escribir la fórmula `HYPERLINK` en streaming.

**Tech Stack:** .NET 10, EF Core 10.0.11 + Npgsql, DocumentFormat.OpenXml 3.1.1, xUnit v3 3.2.2, Testcontainers (`postgres:18-alpine`, Docker corriendo para las pruebas de integración).

**Spec:** `docs/superpowers/specs/2026-09-15-comprobantes-publicos-design.md`

## Global Constraints

**Del spec** (valores copiados tal cual):

- Clave nueva `Quotations:PaymentProofs:PublicLinks` (bool). `appsettings.json` y `appsettings.example.json` la traen en `"PaymentProofs": { "PublicLinks": false }`; producción la enciende en `k8s/prod-configMap.yaml` con el literal `Quotations__PaymentProofs__PublicLinks: "true"`, no con un token `#{...}#` (P1).
- Si está en `true` y falta `Storage:R2:PublicBucket` o `Storage:R2:PublicBaseUrl`, el arranque falla (`ValidateOnStart`) en **cualquier ambiente**. El validador vive en el Bootstrapper (P2).
- Puerto `IPaymentProofPublisher` en `Modules.Quotations.Application` con `Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)`, `Task DeleteAsync(string publicKey, CancellationToken cancellationToken)` y `string? UrlFor(string publicKey)`. Implementaciones `PublicPaymentProofPublisher` y `DisabledPaymentProofPublisher`, las dos en el Bootstrapper; DI registra una u otra según la clave (P3).
- `ConvertQuotationToOrderHandler` y `AddOrderPaymentProofsHandler` publican **sólo los comprobantes nuevos**, antes del `SaveChanges`. `UpdatedProofs` no copia nada (P4).
- Clave pública `payment-proofs/{Guid.CreateVersion7():N}{ext}`, con la extensión del `MimeType` (`application/pdf` → `.pdf`, `image/jpeg` → `.jpg`, `image/png` → `.png`), nunca del nombre. Se guarda la clave, no la URL, en `quotations.order_payment_proofs.public_storage_key`, `varchar(200)`, nullable, sin índice (P5).
- `FileResource.Publish` no se toca ni se llama (P6).
- Si una copia falla, falla el request y no se guarda nada. Si falla cualquier paso después de la primera copia —un rechazo del dominio incluido—, se borran las copias hechas con `DeleteAsync`, cada una en su propio try/catch y con `CancellationToken.None`, y se relanza la excepción original (P7).
- Sin backfill: los comprobantes anteriores, y los que se adjunten con la opción apagada, quedan privados (P8).
- La hoja «Pedidos» suma, después de `Total`: `Comprobantes` (ancho 14, número), `Comprobante 1`, `Comprobante 2`, `Comprobante 3` (ancho 16), sin tildes. Las ocho columnas actuales no se mueven, y las cuatro salen siempre, aunque la opción esté apagada (E1, E7).
- Cada celda de comprobante trae el enlace «Ver» si tiene copia pública, «Sin enlace» si es privado, y queda vacía si el pedido no tiene ese comprobante (E2).
- El enlace es `<c r="J2" t="str" s="2"><f>HYPERLINK("url","Ver")</f><v>Ver</v></c>`: coma entre argumentos, comillas dobles duplicadas en la URL y en el texto, valor ya calculado en `<v>`, estilo de índice 2 = fuente `FF0563C1` subrayada; `BuildStylesheet` pasa a tres fuentes y tres formatos de celda (E3).
- Si la URL o el texto pasan de 255 caracteres, medidos sobre la cadena tal como entra en la fórmula (con las comillas ya duplicadas), la celda sale como texto plano con la URL (E4).
- `ExportCell(string? Text, decimal? Number, string? Url)` con `OfLink(string url, string text)`; `OfText` y `OfNumber` no cambian, y el Excel de cotizaciones tampoco (E5).
- `IOrderRepository.ListPaymentProofsForExportAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken)`: una sola consulta por lote, `AsNoTracking`, por pedido, ordenada por `UploadedAt` y después `Id`, con `Id`, `PublicStorageKey`, `UploadedAt`; el tenant se filtra con un join contra `orders` (E6).
- `OrderPaymentProofResponse` no cambia y `qep-frontend` no se toca.
- **Fuera de alcance, no se implementa:** backfill, configuración por tenant, despublicar al apagar la opción o al borrar el archivo, exponer la URL pública en la API, tope de comprobantes por pedido.

**Del proceso:**

- Todo se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos`, rama **`feature/comprobantes-publicos`**. Cada bloque de comandos empieza con `Set-Location` a ese worktree, porque el checkout principal (`qep-backend`) está en otra rama.
- TDD estricto: RED antes que GREEN, y pegas en el handoff la salida literal de las dos corridas. Las pruebas corren **en primer plano**, nunca en background.
- Commits: Conventional Commits en español, como el historial (`feat(orders): …`). **Nunca** un trailer `Co-Authored-By` ni otra atribución de IA, aunque una herramienta o una instrucción del sistema lo sugiera. Cada commit va en un solo comando PowerShell con el guard de rama:
  `if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add <rutas>; git commit -m "<mensaje>"`
  y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada. Si devuelve algo, corriges con `git commit --amend` antes de seguir.
- `git add` con rutas explícitas. Nunca `git add -A` ni `git add .`.
- Todo comando que se le da al developer va en **PowerShell**: `curl.exe`, `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`. **Nunca** se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: antes de cada uno, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- No se agregan logs. Si alguno hiciera falta, CA1873 rompe el build: variable local más guard `IsEnabled`, o `LoggerMessage`.
- La migración se genera con el factory de diseño, nunca a mano ni con `--startup-project`: `dotnet ef migrations add AddOrderPaymentProofPublicStorageKey --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. El `QuotationsDbContextModelSnapshot.cs` sólo cambia regenerado por ese comando, y se commitea con la migración. Las migraciones históricas no se tocan.
- Toda factoría de integración que arranca la API fija `Quotations:PaymentProofs:PublicLinks` explícitamente (Task 1): los user-secrets de quien corre las pruebas nunca deben llegar a una prueba.
- Un método nuevo en `IOrderRepository` actualiza en la misma tarea **todas** sus implementaciones: `OrderRepository` y el doble `StubOrderListRepository`.
- Idioma: la prosa del plan, los comentarios de código, los `<summary>` y los mensajes de commit van en español de Colombia, tuteando (tienes, puedes, revisa); nunca voseo. Identificadores, claves de configuración, códigos de error y mensajes de excepción quedan en inglés, como el resto del repo. Los comentarios nuevos explican el porqué y citan la decisión del spec (P1-P8, E1-E7) cuando ayuda.
- Archivos nuevos: UTF-8 **sin BOM**. El `.editorconfig` pide `charset = utf-8` y `end_of_line = lf`.
- **Chequeo de formato**, sobre los `.cs` que toca cada tarea (los de `Migrations/` no, que el `.editorconfig` marca como generados). El repo tiene `core.autocrlf=true` y ningún `.gitattributes`, así que en Windows todo archivo sale con CRLF en el árbol de trabajo y `dotnet format` reporta `ENDOFLINE` en cada uno; muchos archivos viejos traen además BOM (`CHARSET`). Los dos son ruido previo y se filtran; cualquier otro diagnóstico en una línea que tocaste se corrige. Si aparece uno en una línea que no tocaste, ya estaba: lo anotas en el handoff y no lo arreglas en esta rama (va en un commit `style:` aparte). El comando se da completo en cada tarea.

---

## Hallazgos contra el código (2026-09-15)

Verificados en `feature/comprobantes-publicos` rebasada sobre `develop`, con el commit del spec («docs(orders): spec de comprobantes de pago públicos en el Excel») como único commit propio y el árbol limpio. El plan no fija el SHA de la base: la rama puede volver a rebasarse si `develop` avanza, así que cada comando que la necesita la calcula con `$base = git merge-base develop HEAD`.

1. **No existe un proyecto de pruebas del Bootstrapper**, y el spec pide pruebas unitarias del validador y de los publicadores, que son `internal` como todo adaptador de ahí. Task 1 crea `tests/Bootstrapper/Bootstrapper.UnitTests` con `InternalsVisibleTo` en `Bootstrapper.csproj`, lo suma a `Backend.slnx` y commitea su `packages.lock.json`. CI corre `dotnet restore Backend.slnx --locked-mode` y `dotnet test Backend.slnx`, así que lo toma solo. El `Dockerfile` copia sólo `src/` y restaura `src/Api/Api.csproj`: no cambia.
2. **`ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample`** se pone rojo en cuanto `QuotationsOptions` bindea `PaymentProofs:PublicLinks` sin que `appsettings.example.json` la tenga. Task 1 cambia los dos en el mismo commit, y esa falla es el RED de su Step 7.
3. **Ninguna factoría reemplaza `IPublicObjectStorage`**, y `R2PublicObjectStorage.CopyFromPrivateAsync` llama a S3 sin mirar `IsConfigured`. Task 3 suma un doble en memoria al `QepApiFactory` de Quotations, con un flag `publicPaymentProofLinks` en el constructor. Es flag y no `WithWebHostBuilder` porque los helpers del harness (`RegisterTenantAsync`, `CreateAvailablePaymentProofFileAsync`, `CreateSentQuotationAsync`) reciben el `QepApiFactory` concreto: con una factoría derivada hablarían con otro host.
4. **Hay 38 factorías que arrancan la API**, todas con la línea `builder.UseSetting("Notifications:EmailProvider", "log");`. Task 1 fija la clave nueva justo debajo con un script, y el conteo lo verifica.
5. **`OrderPaymentProof.Create` es `internal`** y el Domain no tiene `InternalsVisibleTo`: las pruebas pasan por `Order.Create` y `Order.AddPaymentProofs`.
6. **EF mapea `PublicStorageKey` por convención** en cuanto existe la propiedad, con la columna `PublicStorageKey`. La prueba de mapeo lo atrapa, y `QuotationsDbContextMappingTests.TheModelHasNoChangesPendingAMigration` hace de RED/GREEN de la migración sin abrir una base.
7. **`ExportCell` sólo se construye con `OfText`/`OfNumber`**: ningún `new ExportCell(` en `src` ni en `tests`. Sumarle un tercer parámetro posicional es seguro.
8. **`IOrderRepository` tiene exactamente dos implementaciones:** `OrderRepository` y `StubOrderListRepository` (`QuotationsTestDoubles.cs:538`).
9. **`ApproveOrderHandler` no mira el estado del pago**, y `Order.Approve` sólo exige `Pending`: se puede aprobar un pedido desde la API en una prueba y ejercer el rechazo `order.order.not_pending` de P7 de punta a punta.
10. **Una segunda conversión de la misma cotización** llega hasta `quotation.ConvertToOrder` y la corta `quotation.quotation.status_not_convertible` (`OrderApiTests.ConvertingAnAlreadyConvertedQuotationIsRejected`). Está después de publicar, así que también ejerce el rollback.
11. **`docs/integracion-cotizaciones-y-pedidos.md` no describe el Excel**: no se documentan columnas ahí. Pero su línea 121 dice que los comprobantes «no necesitan URL pública», y deja de ser cierto del lado del backend: Task 4 la corrige.
12. **La lista de validadores del README (`README.md:92-95`) está desactualizada**: le faltan `QuotationsOptionsValidator` y `SeedOptionsValidator`. Task 1 los suma junto con el nuevo.
13. **Ningún SQL crudo fuera de `Migrations/` inserta en `order_payment_proofs`** (el seeder de la carga sintética no siembra comprobantes), así que la columna nueva y nullable no rompe nada.
14. **El Excel de pedidos lee `OrderListItemDto`, que ya trae `Guid Id`**: el procesador arma el `OrderId` desde ahí para cruzarlo con los comprobantes del lote.

**Decisiones de este plan donde el spec deja margen:**

- **El publicador se elige al resolver, no al registrar.** `AddScoped<IPaymentProofPublisher>(provider => ...)` lee `IOptions<QuotationsOptions>` —ya validadas por `ValidateOnStart`—, mismo criterio que `IFileScanner` en `StorageInfrastructureExtensions.cs:44-50`. El spec cita el patrón de `ZenviaWhatsAppSender`/`LogWhatsAppSender`, que decide al registrar leyendo `IConfigurationSection`; decidir al resolver cumple lo mismo (DI registra una u otra según la clave y los handlers no leen configuración), y no depende de que un `UseSetting` de las pruebas ya sea visible cuando corre `AddQepPlatform`.
- **El tope de 255 (E4) se mide sobre la cadena ya escapada**, que es lo que Excel lee dentro de la fórmula. Para una URL sin comillas es exactamente `url.Length > 255`; con comillas cae a texto un poco antes, que es el lado seguro.
- **La fila E4 del spec se enmendó el 2026-09-15, después de revisar este plan,** para decir lo mismo: el tope se mide sobre la cadena tal como entra en la fórmula, con las comillas ya duplicadas. Medir la URL cruda dejaría pasar una con comillas que, ya escapada, pasa de 255, y Excel rechazaría esa fórmula; medida escapada, toda fórmula que se escribe es válida. La enmienda se commitea junto con este plan (Task 0).
- **El rollback de P7 vive en una sola clase de Application, `PaymentProofCopies`,** que usan los dos handlers. Así la regla —publicar en orden, anotar las claves, borrar best-effort— se prueba una vez con pruebas unitarias y no queda duplicada.
- **La tarea de dominio no agrega un código de error para una clave de más de 200 caracteres:** la genera el backend y mide 51. Inventar el código violaría «no inventar».

## Entrega

| Commit | Tarea |
| --- | --- |
| `docs(orders): plan de comprobantes de pago públicos`, con la enmienda de E4 del spec (si todavía no están commiteados) | 0 |
| `feat(orders): opción de comprobantes de pago públicos y su validación al arrancar` | 1 |
| `feat(orders): guardar la clave pública de cada comprobante de pago` | 2 |
| `feat(orders): publicador de comprobantes de pago en el bucket público` | 3 |
| `feat(orders): publicar los comprobantes nuevos al convertir y al sumarlos` | 4 |
| `feat(quotations): celdas de enlace en el Excel de las exportaciones` | 5 |
| `feat(orders): comprobantes de pago en el Excel de pedidos` | 6 |
| sólo si la verificación final pide cambios | 7 |

La rama no se publica ni se mergea desde este plan: el PR lo decide el developer.

---

## File Structure

**Crear**

| Archivo | Tarea | Responsabilidad |
| --- | --- | --- |
| `tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj` (+ `packages.lock.json`) | 1 | Proyecto de pruebas unitarias del composition root |
| `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofsOptionsValidatorTests.cs` | 1 | P2 contra el validador |
| `src/Bootstrapper/PaymentProofsOptionsValidator.cs` | 1 | Exige el bucket público con la opción encendida |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs` | 1, 3 | El interruptor en el host real: arranque (P2) y publicador registrado (P3) |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderPaymentProofPublicStorageKey.cs` (+ `.Designer.cs`) | 2 | Columna `public_storage_key` (generada) |
| `src/Modules/Quotations/Modules.Quotations.Application/IPaymentProofPublisher.cs` | 3 | El puerto |
| `src/Bootstrapper/PublicPaymentProofPublisher.cs` | 3 | Copia al bucket público |
| `src/Bootstrapper/DisabledPaymentProofPublisher.cs` | 3 | La opción apagada |
| `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs` | 3 | Dobles de `IFileResourceRepository` e `IPublicObjectStorage` |
| `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs` | 3 | Los dos publicadores |
| `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs` | 4 | Publicar los comprobantes nuevos y el rollback de P7 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs` | 4 | Esa clase |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs` | 4 | Los dos handlers de punta a punta |

**Modificar**

| Archivo | Tarea | Qué cambia |
| --- | --- | --- |
| `src/Bootstrapper/Bootstrapper.csproj` | 1 | `InternalsVisibleTo` para las pruebas |
| `Backend.slnx` | 1 | El proyecto de pruebas nuevo |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptions.cs` | 1 | Subsección `PaymentProofs` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después de `:452`) | 1, 3 | Registro del validador y del publicador |
| `src/Api/appsettings.json`, `src/Api/appsettings.example.json` | 1 | La clave en `false` |
| `k8s/prod-configMap.yaml` (después de `:66`) | 6 | La clave en `"true"`, cuando el Excel ya enlaza las copias |
| `README.md` (`:92-95`, `:103-125`, y antes de `### Plantilla de WhatsApp (Zenvia)`: `:944` hoy, `:946` después de Task 1) | 1, 6 | Clave, validadores y la sección de `payment-proofs/` |
| Las 38 factorías de integración (hallazgo 4) | 1 | Fijan la clave |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` | 1, 3 | Flag `publicPaymentProofLinks` y doble `InMemoryPublicObjectStorage` |
| `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`:186-187`, `:244-245`, `:282-285`) | 2 | La clave viaja al comprobante |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrderPaymentProof.cs` | 2 | Propiedad `PublicStorageKey` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`:424`) | 2 | Mapeo de la columna |
| `QuotationsDbContextModelSnapshot.cs` | 2 | Regenerado por `dotnet ef` |
| `tests/.../UnitTests/OrderTests.cs`, `QuotationsDbContextMappingTests.cs` | 2 | Pruebas de dominio y mapeo |
| `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs`, `AddOrderPaymentProofs.cs` | 4 | Publicar y hacer rollback |
| `tests/.../UnitTests/QuotationsTestDoubles.cs` | 4, 6 | `RecordingPaymentProofPublisher` y el método nuevo del stub |
| `docs/integracion-cotizaciones-y-pedidos.md` (`:121`) | 4 | La copia pública del backend |
| `src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs` | 5 | `ExportCell.Url` y `OfLink` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs` | 5 | Fórmula, estilo 2 y E4 |
| `tests/.../UnitTests/OpenXmlExportWorkbookWriterTests.cs` | 5 | Pruebas de la celda de enlace |
| `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs` | 6 | `OrderExportPaymentProof` y el método nuevo |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs` | 6 | La consulta por lote |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` | 6 | Columnas y celdas nuevas |
| `tests/.../UnitTests/OrdersExportProcessorTests.cs` | 6 | Filas con 0, 1, 3 y 4 comprobantes |
| `tests/.../IntegrationTests/ExportWorkbookReader.cs`, `OrderExportApiTests.cs` | 6 | Fórmulas, export de punta a punta y la consulta contra Postgres |

**No se tocan, a propósito:** `FileResource.Publish` (P6), `QuotationsExportProcessor` y sus pruebas (E5), `OrderPaymentProofResponse` y `OrderEndpoints` (la API no cambia), las migraciones históricas, `Dockerfile` y `ci.yml` (hallazgo 1).

---

### Task 0: Rama, herramientas y baseline

**Files:**
- Ninguno de código. Commitea este plan si todavía no lo está.

**Interfaces:**
- Consumes: nada.
- Produces: `$env:TEMP\qep-comprobantes-baseline-failed.txt` con las pruebas que ya fallan, por nombre. Es la referencia de Task 7.

- [ ] **Step 1: Comprobar rama, árbol y herramientas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
git branch --show-current
git status --short
$base = git merge-base develop HEAD
git log --oneline "$base..HEAD"
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado:
- la rama es `feature/comprobantes-publicos`;
- `git status` vacío, o sólo el plan (`?? docs/superpowers/plans/2026-09-15-comprobantes-publicos.md`) y la enmienda de E4 del spec (` M docs/superpowers/specs/2026-09-15-comprobantes-publicos-design.md`);
- el `git log` desde la base trae el commit del spec, `docs(orders): spec de comprobantes de pago públicos en el Excel`, y encima el del plan si ya está commiteado; nada más. Si aparece otro commit, tu `develop` local está detrás de la base sobre la que se rebasó la rama: **para y pregunta**;
- `Get-Process` sin salida;
- `docker info` devuelve una versión;
- `dotnet ef` devuelve `10.0.11` (instalado global).

Si la rama es otra, **para y pregunta**.

- [ ] **Step 2: Restore, build y modelo sin cambios pendientes**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`, y `No changes have been made to the model since the last migration.` Si hay cambios pendientes en el modelo antes de empezar, **para y pregunta**: Task 2 depende de que el snapshot esté al día.

- [ ] **Step 3: Baseline de la suite completa, por nombre**

Con Docker corriendo. Tarda decenas de minutos (un Postgres por prueba de integración); corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$baseline = Join-Path $env:TEMP "qep-comprobantes-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $baseline
Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-comprobantes-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-comprobantes-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff.

- [ ] **Step 4: Commitear el plan y la enmienda del spec, si hace falta**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
git status --short -- docs/superpowers/plans/2026-09-15-comprobantes-publicos.md docs/superpowers/specs/2026-09-15-comprobantes-publicos-design.md
```

Si la salida muestra alguno de los dos (`??` o ` M`):

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add docs/superpowers/plans/2026-09-15-comprobantes-publicos.md docs/superpowers/specs/2026-09-15-comprobantes-publicos-design.md; git commit -m "docs(orders): plan de comprobantes de pago públicos" -m "Enmienda la fila E4 del spec: el tope de 255 caracteres se mide sobre la cadena tal como entra en la fórmula, con las comillas ya duplicadas."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Si el spec no apareció en la salida, su enmienda ya estaba commiteada: quita el segundo `-m`. Esperado: el commit creado y el `Select-String` sin salida. Si el primer comando no mostró nada, los dos ya estaban commiteados y sigues.

---

### Task 1: La opción `Quotations:PaymentProofs:PublicLinks` y su validación al arrancar

Configuración (P1), validador del Bootstrapper (P2), el proyecto de pruebas del Bootstrapper que no existía (hallazgo 1), la clave fijada en todas las factorías de integración y el README. Todavía nadie lee la clave para publicar: eso llega en Tasks 3 y 4. El ConfigMap de producción la enciende recién en Task 6, cuando el Excel ya enlaza las copias.

**Files:**
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj` (y su `packages.lock.json`, generado)
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofsOptionsValidatorTests.cs`
- Create: `src/Bootstrapper/PaymentProofsOptionsValidator.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs`
- Modify: `src/Bootstrapper/Bootstrapper.csproj:1-4`
- Modify: `Backend.slnx:88-90`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptions.cs:18-19` y el final del archivo (`PdfOptions` termina en `:67`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:452`
- Modify: `src/Api/appsettings.json:34-39`, `src/Api/appsettings.example.json:81-84`
- Modify: `README.md:92-95,125`
- Modify: las 38 factorías del hallazgo 4, incluido `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:675`
- Test: `PaymentProofsOptionsValidatorTests`, `PaymentProofPublicLinksHostTests`, `ArchitectureTests.ConfigurationExampleTests`

**Interfaces:**
- Consumes: `StorageOptions`/`R2Options` (`Modules.Storage.Infrastructure`, públicas), `QuotationsOptions` (`Modules.Quotations.Infrastructure`).
- Produces:
  - `public sealed class PaymentProofsOptions { public bool PublicLinks { get; init; } }` y `QuotationsOptions.PaymentProofs` (`PaymentProofsOptions`), en `Modules.Quotations.Infrastructure`. Task 3 la lee al registrar el publicador (`options.Value.PaymentProofs.PublicLinks`).
  - `internal sealed class PaymentProofsOptionsValidator(IOptions<StorageOptions> storageOptions) : IValidateOptions<QuotationsOptions>`, en `namespace Bootstrapper`, registrado con `AddSingleton`.
  - El proyecto `Bootstrapper.UnitTests` (namespace `Bootstrapper.UnitTests`), que ve los `internal` del Bootstrapper. Task 3 le suma pruebas.
  - `PaymentProofPublicLinksHostTests` con su helper privado `MessagesOf(Exception)`. Task 3 le suma pruebas.
  - En `QuotationsApiHarness.QepApiFactory`, el bloque exacto que Task 3 reemplaza:

```csharp
            // Fijado, nunca heredado, mismo criterio que Notifications:EmailProvider: con la opción
            // prendida en los user-secrets de quien corre las pruebas y sin bucket público,
            // PaymentProofsOptionsValidator no deja arrancar el host, y todas las pruebas de este
            // proyecto mueren antes de su aserción (spec 2026-09-15, P2).
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
```

- [ ] **Step 1: El proyecto de pruebas del Bootstrapper**

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <!-- Los adaptadores entre módulos y los validadores de opciones del composition root son
         internal (InternalsVisibleTo en Bootstrapper.csproj). Los módulos llegan transitivos por
         esta referencia. -->
    <ProjectReference Include="..\..\..\src\Bootstrapper\Bootstrapper.csproj" />
  </ItemGroup>
</Project>
```

En `src/Bootstrapper/Bootstrapper.csproj`, reemplaza:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```

por:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- Los adaptadores y validadores de acá son internal; sus pruebas unitarias los construyen
         directo (spec 2026-09-15). -->
    <InternalsVisibleTo Include="Bootstrapper.UnitTests" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```

En `Backend.slnx`, reemplaza:

```xml
  <Folder Name="/tests/ArchitectureTests/">
    <Project Path="tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj" />
  </Folder>
```

por:

```xml
  <Folder Name="/tests/ArchitectureTests/">
    <Project Path="tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj" />
  </Folder>
  <Folder Name="/tests/Bootstrapper/">
    <Project Path="tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj" />
  </Folder>
```

- [ ] **Step 2: Generar el lock file del proyecto nuevo**

CI y el `Dockerfile` restauran en `--locked-mode`: sin su `packages.lock.json` commiteado, el proyecto nuevo rompe CI con `NU1004`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet restore tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj
dotnet restore Backend.slnx --locked-mode
git status --short
```

Esperado: los dos restores sin errores, y `git status` con exactamente esto (el orden puede variar):

```
 M Backend.slnx
 M src/Bootstrapper/Bootstrapper.csproj
?? tests/Bootstrapper/
```

Y `tests/Bootstrapper/Bootstrapper.UnitTests/packages.lock.json` existe. Si aparece modificado otro `packages.lock.json`, **para y pregunta**: el grafo de otro proyecto no debería moverse.

- [ ] **Step 3: Escribir las pruebas del validador (RED)**

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofsOptionsValidatorTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Infrastructure;

namespace Bootstrapper.UnitTests;

/// <summary>
/// P2 (spec 2026-09-15): con <c>Quotations:PaymentProofs:PublicLinks</c> encendida, el bucket
/// público es obligatorio en cualquier ambiente. Sin él la opción quedaría prendida sin que se
/// publique un solo comprobante, y sin ningún error.
/// </summary>
public sealed class PaymentProofsOptionsValidatorTests
{
    private const string PublicBucket = "qep-public";
    private const string PublicBaseUrl = "https://assets-qep.example.co";

    // P1: apagada, todo sigue como antes, haya o no bucket público.
    [Fact]
    public void PublicLinksOffIsValidWithoutAPublicBucket()
    {
        var result = ValidatorWith(publicBucket: string.Empty, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(false));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PublicLinksOnWithThePublicBucketIsValid()
    {
        var result = ValidatorWith(PublicBucket, PublicBaseUrl).Validate(null, WithPublicLinks(true));

        Assert.True(result.Succeeded);
    }

    // El mensaje dice qué falta y por qué: es lo que lee quien encuentra el pod caído.
    [Fact]
    public void PublicLinksOnWithoutThePublicBucketFailsNamingBothKeys()
    {
        var result = ValidatorWith(publicBucket: string.Empty, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(true));

        Assert.True(result.Failed);
        Assert.Contains(
            "Storage:R2:PublicBucket is required when Quotations:PaymentProofs:PublicLinks is true",
            result.FailureMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "Storage:R2:PublicBaseUrl is required when Quotations:PaymentProofs:PublicLinks is true",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    // StorageOptionsValidator ya exige las dos juntas; éste nombra sólo la que falta.
    [Fact]
    public void PublicLinksOnWithOnlyTheBucketFailsNamingTheBaseUrl()
    {
        var result = ValidatorWith(PublicBucket, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(true));

        Assert.True(result.Failed);
        Assert.Contains("Storage:R2:PublicBaseUrl is required", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage:R2:PublicBucket is required", result.FailureMessage, StringComparison.Ordinal);
    }

    private static PaymentProofsOptionsValidator ValidatorWith(string publicBucket, string publicBaseUrl) =>
        new(Options.Create(new StorageOptions
        {
            R2 = new R2Options { PublicBucket = publicBucket, PublicBaseUrl = publicBaseUrl },
        }));

    private static QuotationsOptions WithPublicLinks(bool publicLinks) =>
        new() { PaymentProofs = new PaymentProofsOptions { PublicLinks = publicLinks } };
}
```

- [ ] **Step 4: Escribir la prueba de arranque (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El interruptor <c>Quotations:PaymentProofs:PublicLinks</c> en el host real (spec 2026-09-15):
/// prendido sin bucket público, la API no arranca (P2).
/// </summary>
public sealed class PaymentProofPublicLinksHostTests
{
    // P2: prendida sin bucket público, la opción no publicaría nada y nadie se enteraría. Los dos
    // valores de Storage se fijan vacíos para que no los traigan los user-secrets de quien corre la
    // prueba.
    [Fact]
    public async Task PublicLinksWithoutAPublicBucketStopsTheApiFromStarting()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "true");
            builder.UseSetting("Storage:R2:PublicBucket", string.Empty);
            builder.UseSetting("Storage:R2:PublicBaseUrl", string.Empty);
        });

        // ThrowsAny: ValidateOnStart lanza durante el arranque, y WebApplicationFactory puede
        // entregarla envuelta.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Storage:R2:PublicBucket is required when Quotations:PaymentProofs:PublicLinks is true",
            StringComparison.Ordinal));
    }

    private static List<string> MessagesOf(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return messages;
    }
}
```

- [ ] **Step 5: Correrlas y verlas fallar**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofPublicLinksHostTests"
```

Esperado (RED):
- el build falla con `error CS0246: No se encontró el tipo o el nombre del espacio de nombres 'PaymentProofsOptionsValidator'` (o `The type or namespace name 'PaymentProofsOptionsValidator' could not be found`) y `'PaymentProofsOptions'`, más `CS0117` porque `QuotationsOptions` no tiene `PaymentProofs`;
- la prueba de integración compila y falla con `Assert.ThrowsAny() Failure: No exception was thrown`: la clave todavía no se bindea a nada y la API arranca.

Pega las dos salidas.

- [ ] **Step 6: La subsección de opciones**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptions.cs`, reemplaza:

```csharp
    public PdfOptions Pdf { get; init; } = new();
}
```

por:

```csharp
    public PdfOptions Pdf { get; init; } = new();

    public PaymentProofsOptions PaymentProofs { get; init; } = new();
}
```

y agrega al final del archivo, después de `PdfOptions`:

```csharp

/// <summary>
/// Los comprobantes de pago de los pedidos (spec 2026-09-15). Con <see cref="PublicLinks"/> en true,
/// cada comprobante nuevo se copia al bucket público al adjuntarse y el Excel de pedidos lo enlaza.
/// Es por ambiente y no por tenant (P1): `appsettings.json` la trae en false y producción la
/// enciende en su ConfigMap. Encendida, exige `Storage:R2:PublicBucket` y `Storage:R2:PublicBaseUrl`;
/// lo revisa `PaymentProofsOptionsValidator`, en el Bootstrapper, que es el único proyecto que ve
/// las dos opciones (P2).
///
/// Riesgo aceptado por el owner el 2026-09-14: un comprobante suele traer nombre, cédula y número de
/// cuenta, y quien tenga la URL lo abre sin sesión. La clave aleatoria impide adivinarla; no controla
/// quién la tiene. Apagarla deja de publicar y de mostrar enlaces, pero no despublica lo ya copiado.
/// </summary>
public sealed class PaymentProofsOptions
{
    public bool PublicLinks { get; init; }
}
```

- [ ] **Step 7: La clave sin documentar pone rojo `ArchitectureTests`**

`QuotationsOptions` ya bindea `Quotations:PaymentProofs:PublicLinks` y `appsettings.example.json` todavía no la trae (hallazgo 2). Es el RED de la clave en el ejemplo: el Step 9 la documenta y el Step 10 la ve en verde.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/ArchitectureTests/ArchitectureTests --no-restore
```

Esperado (RED): falla `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample` con el mensaje de la prueba («Estas claves las bindea una clase de options y no están en src/Api/appsettings.example.json, …») terminado en `no se entera de que existen: Quotations:PaymentProofs:PublicLinks`, sin ninguna otra clave en la lista, y ninguna otra prueba falla salvo las que ya estaban en el baseline de Task 0. El build de este proyecto no incluye `Bootstrapper.UnitTests`, que sigue sin compilar hasta el Step 8. Pega la salida.

- [ ] **Step 8: El validador**

Crea `src/Bootstrapper/PaymentProofsOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

// Falla rápido al arrancar (ValidateOnStart), igual que QuotationsOptionsValidator: con la opción
// encendida y sin bucket público, ningún comprobante tendría copia pública y el Excel de pedidos
// saldría sin un solo enlace, sin error y sin log (spec 2026-09-15, P2). Vive acá y no en
// Quotations.Infrastructure porque ése no referencia Storage: el composition root es el único
// proyecto que ve las dos opciones. Vale en cualquier ambiente, no sólo en producción.
internal sealed class PaymentProofsOptionsValidator(IOptions<StorageOptions> storageOptions)
    : IValidateOptions<QuotationsOptions>
{
    public ValidateOptionsResult Validate(string? name, QuotationsOptions options)
    {
        if (!options.PaymentProofs.PublicLinks)
        {
            return ValidateOptionsResult.Success;
        }

        var r2 = storageOptions.Value.R2;
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(r2.PublicBucket))
        {
            failures.Add(Required("Storage:R2:PublicBucket"));
        }

        if (string.IsNullOrWhiteSpace(r2.PublicBaseUrl))
        {
            failures.Add(Required("Storage:R2:PublicBaseUrl"));
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static string Required(string key) =>
        $"{key} is required when Quotations:PaymentProofs:PublicLinks is true: without it no payment "
        + "proof gets a public copy and the orders Excel has no links.";
}
```

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();

        // El tick del worker de exportaciones. Scoped: ExportJobWorker abre un scope por job para
```

por:

```csharp
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();
        // P2 (spec 2026-09-15): con Quotations:PaymentProofs:PublicLinks encendida, el bucket
        // público es obligatorio. Vive acá porque es el único lugar que ve QuotationsOptions y
        // StorageOptions a la vez. Se suma al QuotationsOptionsValidator de
        // AddQuotationsInfrastructure, que ya declara ValidateOnStart: AddSingleton y no TryAdd,
        // porque con TryAdd este segundo validador no se registraría.
        services.AddSingleton<IValidateOptions<QuotationsOptions>, PaymentProofsOptionsValidator>();

        // El tick del worker de exportaciones. Scoped: ExportJobWorker abre un scope por job para
```

- [ ] **Step 9: La clave en `appsettings.json` y en el ejemplo**

En `src/Api/appsettings.json`, reemplaza:

```json
    "WhatsApp": {
      "TemplateId": "d0e79149-ed61-4784-9883-d86807f46ce5"
    }
  },
```

por:

```json
    "WhatsApp": {
      "TemplateId": "d0e79149-ed61-4784-9883-d86807f46ce5"
    },
    "PaymentProofs": {
      "PublicLinks": false
    }
  },
```

En `src/Api/appsettings.example.json`, reemplaza:

```json
      "ApiKey": "<user-secrets: qcode-pdf-api-key>"
    }
```

por:

```json
      "ApiKey": "<user-secrets: qcode-pdf-api-key>"
    },
    "PaymentProofs": {
      "PublicLinks": false
    }
```

- [ ] **Step 10: Correr las pruebas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofPublicLinksHostTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-restore
```

Esperado: `Superado: 4` en `Bootstrapper.UnitTests`, `Superado: 1` en la de arranque y `ArchitectureTests` completo en verde (con `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample` incluida, que en el Step 7 estaba roja), todos con `Con error: 0`. Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 11: Fijar la clave en las 38 factorías**

Inserta `builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");` justo debajo de cada `builder.UseSetting("Notifications:EmailProvider", "log");` de `tests/`, con la misma sangría, conservando el BOM de cada archivo (si lo tiene) y su fin de línea:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$anchor = 'builder.UseSetting("Notifications:EmailProvider", "log");'
$pattern = '(?m)^([ ]*)builder\.UseSetting\("Notifications:EmailProvider", "log"\);(\r?\n)'
$insertion = '$0${1}builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");${2}'
$files = @(Get-ChildItem -Path tests -Recurse -Filter *.cs |
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
git diff --numstat -- tests/Modules
```

Esperado: `38`, y 38 líneas `1	0	tests/Modules/...` en el `numstat`, una por archivo. Si el número no es 38 o algún archivo muestra más de una línea insertada, **para y pregunta**. `CompositionRootTests.MinimalConfiguration` no se toca: arma la configuración en memoria y nunca arranca la API.

- [ ] **Step 12: El porqué, en el harness de Quotations**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, reemplaza:

```csharp
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
```

por:

```csharp
            builder.UseSetting("Notifications:EmailProvider", "log");

            // Fijado, nunca heredado, mismo criterio que Notifications:EmailProvider: con la opción
            // prendida en los user-secrets de quien corre las pruebas y sin bucket público,
            // PaymentProofsOptionsValidator no deja arrancar el host, y todas las pruebas de este
            // proyecto mueren antes de su aserción (spec 2026-09-15, P2).
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
```

- [ ] **Step 13: README**

La mención de producción no va acá: el ConfigMap enciende la opción en Task 6, y el README la dice en ese mismo commit.

En `README.md`, reemplaza:

```markdown
Las claves obligatorias no se deducen de ese archivo sino de los validadores que
corren con `ValidateOnStart` (`StorageOptionsValidator`,
`NotificationsOptionsValidator`, `SessionOptionsValidator`,
`AuditOptionsValidator`): si algo falta, la API **no arranca**.
```

por:

```markdown
Las claves obligatorias no se deducen de ese archivo sino de los validadores que
corren con `ValidateOnStart` (`StorageOptionsValidator`,
`NotificationsOptionsValidator`, `SessionOptionsValidator`,
`AuditOptionsValidator`, `QuotationsOptionsValidator`, `SeedOptionsValidator` y
`PaymentProofsOptionsValidator`): si algo falta, la API **no arranca**.
```

Y en la tabla de claves, agrega esta fila debajo de la de `Storage:ClamAv:Host` / `Port` / `TimeoutSeconds` (la última):

```markdown
| `Quotations:PaymentProofs:PublicLinks`                 | `false` en `appsettings.json`                                                                 | Con `true`, cada comprobante de pago nuevo se copia al bucket público al adjuntarse y el Excel de pedidos lo enlaza. **Exige `Storage:R2:PublicBucket` y `Storage:R2:PublicBaseUrl` en cualquier ambiente**: sin ellos la API no arranca. Apagarla no despublica lo ya copiado |
```

- [ ] **Step 14: Build completo, formato y una prueba que arranca el harness**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build --filter "FullyQualifiedName~OrderApiTests.ConvertCreatesTheOrderAndLeavesTheQuotationConverted"
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`; la prueba de `OrderApiTests` con `Superado: 1` (el harness sigue arrancando con la clave fijada); el último bloque sin salida, o sólo con líneas que no tocaste (anótalas en el handoff).

- [ ] **Step 15: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$factories = @(git diff --name-only -- tests/Modules)
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add Backend.slnx README.md src/Api/appsettings.json src/Api/appsettings.example.json src/Bootstrapper/Bootstrapper.csproj src/Bootstrapper/PaymentProofsOptionsValidator.cs src/Bootstrapper/QepServiceCollectionExtensions.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptions.cs tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj tests/Bootstrapper/Bootstrapper.UnitTests/packages.lock.json tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofsOptionsValidatorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs $factories; git commit -m "feat(orders): opción de comprobantes de pago públicos y su validación al arrancar" -m "Quotations:PaymentProofs:PublicLinks, apagada por defecto. Con la opción encendida y sin bucket público la API no arranca. Suma el proyecto de pruebas del Bootstrapper y fija la clave en todas las factorías de integración."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío (salvo `bin/`/`obj/`, que el `.gitignore` ya excluye) y el `Select-String` sin salida.

---

### Task 2: La clave pública en el comprobante y en la base

`OrderPaymentProofInput` y `OrderPaymentProof` ganan `PublicStorageKey`, EF la mapea a `public_storage_key` (`varchar(200)`, nullable, sin índice) y la migración se genera con `dotnet ef`. Nadie la llena todavía: eso es Task 4.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs:186-187,244-245,282-285`
- Modify (reemplazo entero): `src/Modules/Quotations/Modules.Quotations.Domain/OrderPaymentProof.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:424`
- Create (generados): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddOrderPaymentProofPublicStorageKey.cs` y `.Designer.cs`
- Modify (regenerado): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`

**Interfaces:**
- Consumes: nada de tareas anteriores.
- Produces:
  - `public sealed record OrderPaymentProofInput(Guid FileId, decimal Amount, string? PublicStorageKey = null);` (Task 4 lo construye con la clave; Task 6, en las pruebas).
  - `OrderPaymentProof.PublicStorageKey` (`string?`, `{ get; private set; }`), fijado en `Create` e inmutable después (Task 6 lo lee en la consulta del Excel).
  - `internal static OrderPaymentProof Create(OrderPaymentProofId id, OrderId orderId, Guid fileId, string? publicStorageKey, decimal amount, MemberId uploadedBy, DateTimeOffset uploadedAt)`.
  - La columna `quotations.order_payment_proofs.public_storage_key`.

- [ ] **Step 1: Pruebas de dominio**

Agrega al final de la clase `OrderTests` (`tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`), antes de su llave de cierre:

```csharp

    // Spec 2026-09-15, P5: cada comprobante guarda la clave de su copia pública, que le llega ya
    // armada desde el handler.
    [Fact]
    public void CreateKeepsThePublicStorageKeyOfEachProof()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 60_000m, "payment-proofs/a.pdf"),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 40_000m, "payment-proofs/b.png"),
        ]);

        Assert.Equal(
            ["payment-proofs/a.pdf", "payment-proofs/b.png"],
            order.PaymentProofs.Select(proof => proof.PublicStorageKey));
    }

    // P8: con la opción apagada, y en los comprobantes de antes, no hay copia pública.
    [Fact]
    public void CreateLeavesThePublicStorageKeyNullWhenThereIsNone()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);

        Assert.Null(Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    [Fact]
    public void AddPaymentProofsKeepsThePublicStorageKeyOfTheNewProofs()
    {
        var order = NewOrder(paymentStatus: OrderPaymentStatus.PaymentPending, proofs: []);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, "payment-proofs/c.jpg")],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal("payment-proofs/c.jpg", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    // P4: corregir el monto no toca el archivo, así que tampoco su copia pública.
    [Fact]
    public void CorrectingAnAmountKeepsThePublicStorageKey()
    {
        var order = NewOrder(
            paymentStatus: OrderPaymentStatus.PartialPaymentReceived,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m, "payment-proofs/d.pdf")]);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            OrderPaymentStatus.FullPaymentReceived,
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        Assert.Equal("payment-proofs/d.pdf", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }
```

- [ ] **Step 2: Prueba de mapeo**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, agrega este método justo antes del `/// <summary>` de `TheModelHasNoChangesPendingAMigration` (el que empieza con «El modelo y el último snapshot describen la misma base»):

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

- [ ] **Step 3: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
```

Esperado (RED): errores de compilación como `error CS1729: 'OrderPaymentProofInput' no contiene un constructor que tome 3 argumentos` (o `does not contain a constructor that takes 3 arguments`) y `error CS1061: 'OrderPaymentProof' no contiene una definición para 'PublicStorageKey'`. Pega las líneas.

- [ ] **Step 4: El dominio**

Reemplaza entero `src/Modules/Quotations/Modules.Quotations.Domain/OrderPaymentProof.cs` por:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Un comprobante de pago adjuntado durante la conversión de una cotización en pedido (US-14), o
/// sumado después mientras el pedido sigue pendiente (<see cref="Order.AddPaymentProofs"/>).
/// Entidad hija de <see cref="Order"/>: el archivo, su copia pública y quién lo subió no cambian
/// una vez creado, pero el monto sí puede corregirse (a pedido, 2026-09) —ver
/// <see cref="UpdateAmount"/>— si alguien lo tipeó mal.
/// </summary>
public sealed class OrderPaymentProof
{
    private OrderPaymentProof()
    {
    }

    private OrderPaymentProof(
        OrderPaymentProofId id,
        OrderId orderId,
        Guid fileId,
        string? publicStorageKey,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        Id = id;
        OrderId = orderId;
        FileId = fileId;
        PublicStorageKey = publicStorageKey;
        Amount = amount;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    public OrderPaymentProofId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    /// <summary>Referencia blanda al archivo en el módulo Storage — mismo mecanismo que
    /// <see cref="Quotation.PdfFileId"/>. Que el archivo exista, sea del tenant, ya haya
    /// terminado de subir y sea uno de los tipos aceptados (PDF/JPG/PNG, hasta 10 MB) lo valida
    /// la aplicación con <c>IQuotationFileLookup</c> antes de construir el pedido.</summary>
    public Guid FileId { get; private set; }

    /// <summary>La clave de la copia del archivo en el bucket público de R2
    /// (<c>payment-proofs/{guid}.{ext}</c>), o null si el comprobante es privado: se adjuntó con
    /// <c>Quotations:PaymentProofs:PublicLinks</c> apagada, o antes de que la opción existiera
    /// (spec 2026-09-15, P5 y P8). Se guarda la clave y no la URL: la URL se arma al exportar con
    /// el dominio público vigente, así que cambiar el dominio no rompe nada. Igual que
    /// <see cref="FileId"/>, no cambia después de crearse.</summary>
    public string? PublicStorageKey { get; private set; }

    /// <summary>Monto que cubre este comprobante específico — cada archivo puede tener el suyo
    /// (US-14).</summary>
    public decimal Amount { get; private set; }

    public MemberId UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    internal static OrderPaymentProof Create(
        OrderPaymentProofId id,
        OrderId orderId,
        Guid fileId,
        string? publicStorageKey,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        if (fileId == Guid.Empty)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_required",
                "The payment proof file is required.");
        }

        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        return new OrderPaymentProof(id, orderId, fileId, publicStorageKey, amount, uploadedBy, uploadedAt);
    }

    /// <summary>Corrige el monto de un comprobante ya cargado (a pedido, 2026-09) — sólo desde
    /// <see cref="Order.AddPaymentProofs"/>, que es quien decide si el pedido admite el cambio
    /// (sólo <see cref="OrderStatus.Pending"/>). El archivo, su copia pública y quién lo subió no
    /// se tocan: es una corrección puntual del importe, no otro comprobante.</summary>
    internal void UpdateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        Amount = amount;
    }
}
```

En `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs`, reemplaza las **dos** apariciones —en `AddPaymentProofs`, `:186-187`, y en `AddProofs`, `:244-245`— de la línea de abajo. Son idénticas byte a byte, así que un reemplazo simple falla por no único: reemplaza las dos de una vez (`replace_all`). El conteo del final de este step confirma que fueron dos.

```csharp
                OrderPaymentProofId.New(), Id, proof.FileId, proof.Amount, uploadedBy, occurredAt));
```

por:

```csharp
                OrderPaymentProofId.New(), Id, proof.FileId, proof.PublicStorageKey, proof.Amount, uploadedBy,
                occurredAt));
```

Y reemplaza el record del final (`:282-285`):

```csharp
/// <summary>Un comprobante de pago tal como lo manda el cliente, sin id: <see cref="Order"/>
/// asigna un <see cref="OrderPaymentProofId"/> nuevo a cada uno — mismo criterio que
/// <c>PriceScaleInput</c> en Catalog.</summary>
public sealed record OrderPaymentProofInput(Guid FileId, decimal Amount);
```

por:

```csharp
/// <summary>Un comprobante de pago tal como lo manda el cliente, sin id: <see cref="Order"/>
/// asigna un <see cref="OrderPaymentProofId"/> nuevo a cada uno — mismo criterio que
/// <c>PriceScaleInput</c> en Catalog. <c>PublicStorageKey</c> es la clave de la copia pública que
/// el handler ya hizo (spec 2026-09-15, P5), o null si el comprobante queda privado. Va última y con
/// default para no romper a quien lo construye posicionalmente.</summary>
public sealed record OrderPaymentProofInput(Guid FileId, decimal Amount, string? PublicStorageKey = null);
```

Comprueba que no quedó ninguna llamada vieja:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
(Select-String -Path src/Modules/Quotations/Modules.Quotations.Domain/Order.cs -SimpleMatch -Pattern "proof.FileId, proof.PublicStorageKey, proof.Amount").Count
```

Esperado: `2`.

- [ ] **Step 5: Correr las pruebas: dominio en verde, mapeo en rojo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OrderTests|FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: todas las de `OrderTests` pasan, y fallan dos de `QuotationsDbContextMappingTests`. `OrderPaymentProofPublicStorageKeyMapsToANullableColumnWithoutIndex` falla con `Assert.Equal() Failure: Strings differ` (`Expected: "public_storage_key"`, `Actual: "PublicStorageKey"`), porque EF ya mapea la propiedad por convención (hallazgo 6). `TheModelHasNoChangesPendingAMigration` falla con `Assert.False() Failure`. Pega la salida.

- [ ] **Step 6: El mapeo de EF**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`, dentro de `ConfigureOrderPaymentProof`, reemplaza:

```csharp
        proof.Property(value => value.UploadedAt).HasColumnName("uploaded_at");
        proof.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_payment_proofs_order");
```

por:

```csharp
        proof.Property(value => value.UploadedAt).HasColumnName("uploaded_at");
        // La clave de la copia pública (spec 2026-09-15, P5): nullable, porque los comprobantes
        // privados no tienen, y sin índice, porque nadie busca por ella. La clave mide 51
        // caracteres (`payment-proofs/` + 32 hex + extensión); 200 deja margen.
        proof.Property(value => value.PublicStorageKey)
            .HasColumnName("public_storage_key")
            .HasMaxLength(200);
        proof.HasIndex(value => value.OrderId).HasDatabaseName("IX_order_payment_proofs_order");
```

- [ ] **Step 7: Mapeo en verde, migración pendiente en rojo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: `OrderPaymentProofPublicStorageKeyMapsToANullableColumnWithoutIndex` pasa y **sólo** `TheModelHasNoChangesPendingAMigration` falla (`Assert.False() Failure`): el modelo tiene una columna que el snapshot no. Es el RED de la migración. Pega la salida.

- [ ] **Step 8: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddOrderPaymentProofPublicStorageKey --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
$migration = Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*_AddOrderPaymentProofPublicStorageKey.cs" | Where-Object { $_.Name -notlike "*.Designer.cs" }
Get-Content $migration.FullName
git status --short -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
```

Esperado: `Done.`, y el `Up`/`Down` generados son exactamente esto, sin ninguna otra operación:

```csharp
            migrationBuilder.AddColumn<string>(
                name: "public_storage_key",
                schema: "quotations",
                table: "order_payment_proofs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
```

```csharp
            migrationBuilder.DropColumn(
                name: "public_storage_key",
                schema: "quotations",
                table: "order_payment_proofs");
```

`git status` muestra `?? …_AddOrderPaymentProofPublicStorageKey.cs`, `?? …_AddOrderPaymentProofPublicStorageKey.Designer.cs` y ` M …/QuotationsDbContextModelSnapshot.cs`. Si la migración trae cualquier otra operación (un `DropTable`, un `RenameColumn`, otra columna), el modelo tenía deriva: la quitas con `dotnet ef migrations remove --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext` y **paras y preguntas**. La migración no se edita a mano.

- [ ] **Step 9: Todo en verde**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
```

Esperado: `No changes have been made to the model since the last migration.` y la suite unitaria de Quotations completa con `Con error: 0`. Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 10: Build y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet build Backend.slnx --no-restore
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`, y el último bloque sin salida (o sólo líneas que no tocaste, anotadas en el handoff).

- [ ] **Step 11: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$migrationFiles = @(git ls-files --others --exclude-standard -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations)
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Domain/Order.cs src/Modules/Quotations/Modules.Quotations.Domain/OrderPaymentProof.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs $migrationFiles; git commit -m "feat(orders): guardar la clave pública de cada comprobante de pago" -m "Columna nullable quotations.order_payment_proofs.public_storage_key, fijada al crear el comprobante e inmutable después. Migración AddOrderPaymentProofPublicStorageKey generada con el factory de diseño."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `$migrationFiles` con los dos archivos de la migración, `git status --short` vacío y el `Select-String` sin salida.

---

### Task 3: El puerto y los dos publicadores

`IPaymentProofPublisher` en Application; `PublicPaymentProofPublisher` y `DisabledPaymentProofPublisher` en el Bootstrapper; el registro que elige uno u otro según la clave (P3). El harness de Quotations gana el doble del bucket público y el flag `publicPaymentProofLinks`. Todavía ningún handler publica: eso es Task 4.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IPaymentProofPublisher.cs`
- Create: `src/Bootstrapper/PublicPaymentProofPublisher.cs`
- Create: `src/Bootstrapper/DisabledPaymentProofPublisher.cs`
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs`
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (el bloque que Task 1 dejó después de `:452`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (`QepApiFactory`, `:654-732`, y el final del archivo, `:790-792`, contados después de Task 1)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs`

**Interfaces:**
- Consumes (Task 1): `QuotationsOptions.PaymentProofs.PublicLinks`; el bloque del harness que fija la clave en `"false"` (se reemplaza en el Step 1); `PaymentProofPublicLinksHostTests` y su `MessagesOf`.
- Produces:
  - `public interface IPaymentProofPublisher` en `Modules.Quotations.Application`: `Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)`, `Task DeleteAsync(string publicKey, CancellationToken cancellationToken)`, `string? UrlFor(string publicKey)`. Task 4 y Task 6 lo consumen.
  - `internal sealed class PublicPaymentProofPublisher(IFileResourceRepository repository, IPublicObjectStorage publicObjectStorage) : IPaymentProofPublisher` e `internal sealed class DisabledPaymentProofPublisher : IPaymentProofPublisher`, en `namespace Bootstrapper`.
  - En el harness: `QepApiFactory(string connectionString, bool runExportWorker = false, bool publicPaymentProofLinks = false)`, su propiedad `PublicObjectStorage` (`InMemoryPublicObjectStorage`, con `IsConfigured` igual al flag), y la clase anidada pública `QuotationsApiHarness.InMemoryPublicObjectStorage` con `const string BaseUrl = "https://assets.qep.test"`, `bool IsConfigured { get; init; }`, `int? FailingCopyAttempt { get; set; }`, `IReadOnlyDictionary<string, string> Copies` (clave pública → clave privada) y `List<string> DeletedKeys`. Tasks 4 y 6 los usan.

- [ ] **Step 1: El doble del bucket público en el harness**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`:

Reemplaza:

```csharp
    public sealed class QepApiFactory(string connectionString, bool runExportWorker = false)
        : WebApplicationFactory<Program>
    {
```

por:

```csharp
    public sealed class QepApiFactory(
        string connectionString, bool runExportWorker = false, bool publicPaymentProofLinks = false)
        : WebApplicationFactory<Program>
    {
        // Copia del flag para ConfigureWebHost. Si ese método leyera el parámetro, que además
        // inicializa PublicObjectStorage, el compilador avisaría CS9124 (parámetro capturado y usado
        // en un inicializador), y con TreatWarningsAsErrors el build falla.
        private readonly bool _publicPaymentProofLinks = publicPaymentProofLinks;

```

Reemplaza:

```csharp
        public InMemoryObjectStorage ObjectStorage { get; } = new();
```

por:

```csharp
        public InMemoryObjectStorage ObjectStorage { get; } = new();

        /// <summary>Doble de <c>IPublicObjectStorage</c> (spec 2026-09-15): el publicador real de
        /// comprobantes copia entre buckets de R2, que en una prueba no existen. Anota las copias y
        /// los borrados para que la prueba los vea. Queda configurado sólo con
        /// <c>publicPaymentProofLinks</c>, como el adaptador real sólo lo está con bucket público.</summary>
        public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new() { IsConfigured = publicPaymentProofLinks };
```

Reemplaza el bloque que dejó Task 1:

```csharp
            // Fijado, nunca heredado, mismo criterio que Notifications:EmailProvider: con la opción
            // prendida en los user-secrets de quien corre las pruebas y sin bucket público,
            // PaymentProofsOptionsValidator no deja arrancar el host, y todas las pruebas de este
            // proyecto mueren antes de su aserción (spec 2026-09-15, P2).
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
```

por:

```csharp
            // Fijado, nunca heredado, mismo criterio que Notifications:EmailProvider: con la opción
            // prendida en los user-secrets de quien corre las pruebas y sin bucket público,
            // PaymentProofsOptionsValidator no deja arrancar el host, y todas las pruebas de este
            // proyecto mueren antes de su aserción (spec 2026-09-15, P2). Las pruebas de los
            // comprobantes públicos la prenden con publicPaymentProofLinks, que además fija el bucket
            // público que el validador exige.
            builder.UseSetting(
                "Quotations:PaymentProofs:PublicLinks", _publicPaymentProofLinks ? "true" : "false");
            if (_publicPaymentProofLinks)
            {
                builder.UseSetting("Storage:R2:PublicBucket", "test-public-bucket");
                builder.UseSetting("Storage:R2:PublicBaseUrl", InMemoryPublicObjectStorage.BaseUrl);
            }
```

Reemplaza:

```csharp
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);
```

por:

```csharp
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);

                // El publicador real de comprobantes copia al bucket público de R2 por este puerto
                // (spec 2026-09-15); acá las copias quedan en memoria, donde la prueba las ve. El
                // adaptador real llamaría a S3 aunque el bucket no esté configurado.
                services.RemoveAll<IPublicObjectStorage>();
                services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
```

Y al final del archivo reemplaza:

```csharp
        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();
    }
}
```

por:

```csharp
        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();
    }

    /// <summary>
    /// El bucket público en memoria (spec 2026-09-15). Anota cada copia y cada borrado de los
    /// comprobantes de pago, y puede fallar a propósito en una copia para ejercer el rollback de P7.
    /// <see cref="GetUrl"/> arma la URL con <see cref="BaseUrl"/>, como R2PublicObjectStorage con
    /// Storage:R2:PublicBaseUrl.
    /// </summary>
    public sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
    {
        public const string BaseUrl = "https://assets.qep.test";

        private readonly Dictionary<string, string> _copies = new(StringComparer.Ordinal);
        private int _copyAttempts;

        /// <summary>El intento de copia (desde 1, contando todos los del host) que falla; null si
        /// ninguno.</summary>
        public int? FailingCopyAttempt { get; set; }

        /// <summary>Las copias que siguen en el bucket: clave pública → clave privada de origen.</summary>
        public IReadOnlyDictionary<string, string> Copies => _copies;

        public List<string> DeletedKeys { get; } = [];

        /// <summary>Como R2PublicObjectStorage, configurado sólo con bucket público: la factoría lo
        /// prende con <c>publicPaymentProofLinks</c>, que además fija el bucket. Apagado, lo que lo
        /// consulta —la URL pública de las imágenes de producto, por ejemplo— se porta como en CI,
        /// sin bucket público.</summary>
        public bool IsConfigured { get; init; }

        public Task CopyFromPrivateAsync(
            string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            _copyAttempts++;
            if (_copyAttempts == FailingCopyAttempt)
            {
                throw new InvalidOperationException("Simulated failure copying to the public bucket.");
            }

            _copies[publicKey] = privateKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            _copies.Remove(publicKey);
            DeletedKeys.Add(publicKey);
            return Task.CompletedTask;
        }

        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";
    }
}
```

- [ ] **Step 2: Los dobles de Storage para las pruebas del Bootstrapper**

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs`:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los puertos de Storage que usan los adaptadores del composition root. A mano y sin
// librería de mocking, como el resto del repositorio: anotan lo que reciben.

/// <summary>Los archivos que siembra la prueba, por id. Sólo <see cref="GetAsync"/>: es lo único que
/// el publicador de comprobantes lee.</summary>
internal sealed class InMemoryFileResourceRepository(params FileResource[] resources)
    : IFileResourceRepository
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

/// <summary>El bucket público: anota cada copia (clave pública → clave privada de origen) y cada
/// borrado. <see cref="GetUrl"/> arma la URL con <see cref="BaseUrl"/>, como R2PublicObjectStorage con
/// Storage:R2:PublicBaseUrl.</summary>
internal sealed class RecordingPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(
        string privateKey, string publicKey, CancellationToken cancellationToken)
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
}
```

- [ ] **Step 3: Pruebas de los publicadores (RED)**

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs`:

```csharp
using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los dos publicadores de comprobantes de pago (spec 2026-09-15, P3 a P6): el que copia al bucket
/// público con una clave aleatoria y el que no hace nada con la opción apagada.
/// </summary>
public sealed class PaymentProofPublisherTests
{
    private const string PdfKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // P5: la copia va bajo payment-proofs/ con una clave aleatoria —no el id del archivo, que viaja
    // en el navegador— y sale de la clave privada del archivo.
    [Fact]
    public async Task CopiesThePrivateObjectUnderPaymentProofsWithARandomKey()
    {
        var file = AvailableFile(TenantId, "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var key = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(key);
        Assert.Matches(PdfKeyPattern, key);
        Assert.DoesNotContain(
            file.Id.Value.ToString("N", CultureInfo.InvariantCulture), key, StringComparison.Ordinal);
        var copy = Assert.Single(storage.Copies);
        Assert.Equal(key, copy.Key);
        Assert.Equal(file.StorageKey, copy.Value);
    }

    // Clave nueva en cada publicación, igual que el PDF de la cotización: dos comprobantes con el
    // mismo archivo no comparten copia.
    [Fact]
    public async Task EachPublicationGetsItsOwnKey()
    {
        var file = AvailableFile(TenantId, "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var first = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
        var second = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(2, storage.Copies.Count);
    }

    // P5: la extensión sale del MimeType, no del nombre, que puede traer ".jpeg", mayúsculas o nada.
    [Theory]
    [InlineData("application/pdf", "comprobante.PDF", ".pdf")]
    [InlineData("image/jpeg", "foto.jpeg", ".jpg")]
    [InlineData("IMAGE/PNG", "captura", ".png")]
    public async Task TheExtensionComesFromTheMimeType(string mimeType, string name, string extension)
    {
        var file = AvailableFile(TenantId, mimeType, name);
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(file), new RecordingPublicObjectStorage());

        var key = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(key);
        Assert.StartsWith("payment-proofs/", key, StringComparison.Ordinal);
        Assert.EndsWith(extension, key, StringComparison.Ordinal);
    }

    // El resolver ya revisó la frontera de tenant, pero se revisa igual: mismo código para "no
    // existe" y "es de otro tenant", y sin copia.
    [Fact]
    public async Task AFileOfAnotherTenantIsRejectedAsNotFoundWithoutCopying()
    {
        var file = AvailableFile(Guid.CreateVersion7(), "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
        Assert.Empty(storage.Copies);
    }

    [Fact]
    public async Task AMissingFileIsRejectedAsNotFound()
    {
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(), new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
    }

    [Fact]
    public async Task AFileThatHasNotFinishedUploadingIsRejected()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User, "comprobante.pdf",
            "application/pdf", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_available", error.Code);
        Assert.Empty(storage.Copies);
    }

    // P7: el rollback borra la copia del bucket público.
    [Fact]
    public async Task DeleteRemovesThePublicCopy()
    {
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(), storage);

        await publisher.DeleteAsync("payment-proofs/abc.pdf", TestContext.Current.CancellationToken);

        Assert.Equal(["payment-proofs/abc.pdf"], storage.DeletedKeys);
    }

    // P5: la URL se arma al exportar con el dominio público; no se guarda.
    [Fact]
    public void UrlForBuildsThePublicUrlOfTheKey()
    {
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(), new RecordingPublicObjectStorage());

        Assert.Equal(
            $"{RecordingPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf",
            publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    // P1: apagada, no se copia nada y el Excel no tiene URL, aunque el comprobante tenga una copia
    // de cuando la opción estaba encendida.
    [Fact]
    public async Task TheDisabledPublisherCopiesNothingAndGivesNoUrl()
    {
        var publisher = new DisabledPaymentProofPublisher();

        var key = await publisher.PublishAsync(
            TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        await publisher.DeleteAsync("payment-proofs/abc.pdf", TestContext.Current.CancellationToken);

        Assert.Null(key);
        Assert.Null(publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    private static FileResource AvailableFile(Guid tenantId, string mimeType, string name)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, Guid.CreateVersion7(), FileOwnerType.User, name, mimeType, 1024,
            $"staging/{Guid.CreateVersion7():N}", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.Promote($"files/tenants/{tenantId:N}/{Guid.CreateVersion7():N}", Now);
        return file;
    }
}
```

- [ ] **Step 4: Pruebas del registro en el host real (RED)**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs`, reemplaza:

```csharp
using Microsoft.AspNetCore.Hosting;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

por:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

Reemplaza:

```csharp
/// prendido sin bucket público, la API no arranca (P2).
```

por:

```csharp
/// prendido sin bucket público, la API no arranca (P2), y según su valor el composition root
/// registra el publicador de comprobantes que copia o el que no hace nada (P3).
```

Y agrega estas dos pruebas justo antes de `private static List<string> MessagesOf(Exception exception)`:

```csharp
    // P3: apagada —el default de las factorías—, el composition root registra el publicador que no
    // hace nada: sin URL aunque la clave exista.
    [Fact]
    public async Task WithPublicLinksOffThePublisherGivesNoUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IPaymentProofPublisher>();

        Assert.Null(publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    // P3: encendida, el publicador arma la URL con el bucket público.
    [Fact]
    public async Task WithPublicLinksOnThePublisherBuildsThePublicUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        await using var scope = factory.Services.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IPaymentProofPublisher>();

        Assert.Equal(
            $"{InMemoryPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf",
            publisher.UrlFor("payment-proofs/abc.pdf"));
    }

```

- [ ] **Step 5: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore
```

Esperado (RED): el primer build falla con `error CS0246` por `PublicPaymentProofPublisher` y `DisabledPaymentProofPublisher`; el segundo, con `error CS0246` por `IPaymentProofPublisher`. El harness compila: el doble y el flag son de la prueba. Pega las líneas.

- [ ] **Step 6: El puerto**

Crea `src/Modules/Quotations/Modules.Quotations.Application/IPaymentProofPublisher.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>
/// La copia pública de un comprobante de pago, para que el Excel de pedidos lo enlace (spec
/// 2026-09-15). Puerto y no configuración en el handler, mismo criterio que
/// <see cref="IWhatsAppSender"/>: el composition root registra la implementación que publica o la
/// que no hace nada según <c>Quotations:PaymentProofs:PublicLinks</c> (P3), y los handlers no leen
/// configuración. Las dos viven en el Bootstrapper, el único proyecto que ve Quotations y Storage.
///
/// Apagar la opción deja de publicar y de mostrar enlaces, pero no despublica lo que ya se copió.
/// </summary>
public interface IPaymentProofPublisher
{
    /// <summary>Copia el archivo al bucket público y devuelve la clave pública, o null si la
    /// opción está apagada.</summary>
    Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Borra una copia pública. Sólo para el rollback de P7, cuando el request falla
    /// después de copiar.</summary>
    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    /// <summary>La URL pública de una clave, o null si la opción está apagada.</summary>
    string? UrlFor(string publicKey);
}
```

- [ ] **Step 7: Los dos publicadores**

Crea `src/Bootstrapper/PublicPaymentProofPublisher.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Publica un comprobante de pago copiándolo al bucket público de R2, con
/// <c>Quotations:PaymentProofs:PublicLinks</c> encendida (spec 2026-09-15, P5). Mismo criterio que
/// <see cref="QuotationPdfStorage.PublishAsync"/>: clave nueva y aleatoria, que no se deduce de
/// ningún id que viaje en el navegador.
///
/// No pasa por <c>FileResource.Publish</c> a propósito (P6): esa regla —sólo imágenes— protege el
/// endpoint de publicación de Storage, y relajarla dejaría a cualquiera con <c>FilePublish</c>
/// publicar un PDF desde la API. Por lo mismo el <c>FileResource</c> no se entera de esta copia: si
/// alguien lo borra (<c>SoftDeleteFileHandler</c>), la copia pública queda en el bucket. Despublicar
/// es trabajo aparte.
/// </summary>
internal sealed class PublicPaymentProofPublisher(
    IFileResourceRepository repository,
    IPublicObjectStorage publicObjectStorage) : IPaymentProofPublisher
{
    /// <summary>
    /// Prefijo propio en el bucket público, aparte de <c>quotations/</c>: sobre éste **no debe haber
    /// regla de lifecycle**. El de los PDF de cotización puede tener una; confundirlos borraría
    /// comprobantes cuyos enlaces siguen en Excels ya enviados.
    /// </summary>
    private const string Prefix = "payment-proofs";

    // La extensión sale del MimeType y no del nombre, que puede traer ".jpeg", mayúsculas o nada. Son
    // los tres tipos que OrderPaymentProofResolver deja pasar.
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
        };

    public async Task<string?> PublishAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);

        // OrderPaymentProofResolver ya lo validó antes de llegar acá, pero la frontera de tenant se
        // revisa igual, con sus mismos códigos: "no existe" y "es de otro tenant" no se distinguen
        // desde afuera.
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_found",
                $"File '{fileId}' was not found in this tenant.");
        }

        if (resource.Status != FileResourceStatus.Available)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_available",
                "The payment proof file has not finished uploading yet.");
        }

        var publicKey = $"{Prefix}/{Guid.CreateVersion7():N}{ExtensionOf(resource.MimeType)}";
        await publicObjectStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
        return publicKey;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        publicObjectStorage.DeleteAsync(publicKey, cancellationToken);

    public string? UrlFor(string publicKey) => publicObjectStorage.GetUrl(publicKey);

    private static string ExtensionOf(string mimeType) =>
        ExtensionsByMimeType.TryGetValue(mimeType, out var extension)
            ? extension
            : throw new QuotationsDomainException(
                "order.payment_proof.file_type_not_allowed",
                "The payment proof must be a PDF, JPG or PNG file.");
}
```

Crea `src/Bootstrapper/DisabledPaymentProofPublisher.cs`:

```csharp
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Con <c>Quotations:PaymentProofs:PublicLinks</c> apagada (spec 2026-09-15, P1 y P3): no copia
/// nada, y los comprobantes quedan privados como hasta ahora. <see cref="UrlFor"/> devuelve null
/// aunque el comprobante tenga una copia de cuando la opción estaba encendida: apagarla deja de
/// mostrar los enlaces en el Excel, sin despublicar lo que ya se copió.
/// </summary>
internal sealed class DisabledPaymentProofPublisher : IPaymentProofPublisher
{
    public Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public string? UrlFor(string publicKey) => null;
}
```

- [ ] **Step 8: El registro que elige uno u otro**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();
        // P2 (spec 2026-09-15): con Quotations:PaymentProofs:PublicLinks encendida, el bucket
```

por:

```csharp
        services.AddScoped<IQuotationFileLookup, QuotationFileLookup>();
        // Los comprobantes de pago públicos (spec 2026-09-15, P3): el publicador que copia al bucket
        // público o el que no hace nada, según Quotations:PaymentProofs:PublicLinks. Se decide al
        // resolver, con las opciones ya validadas por ValidateOnStart, mismo criterio que
        // IFileScanner en AddStorageInfrastructure. Scoped porque IFileResourceRepository lo es.
        services.AddScoped<PublicPaymentProofPublisher>();
        services.AddScoped<IPaymentProofPublisher>(provider =>
            provider.GetRequiredService<IOptions<QuotationsOptions>>().Value.PaymentProofs.PublicLinks
                ? provider.GetRequiredService<PublicPaymentProofPublisher>()
                : new DisabledPaymentProofPublisher());
        // P2 (spec 2026-09-15): con Quotations:PaymentProofs:PublicLinks encendida, el bucket
```

- [ ] **Step 9: Correr las pruebas (GREEN)**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~PaymentProofPublicLinksHostTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-restore
```

Esperado: `Superado: 15` en `Bootstrapper.UnitTests` (4 del validador, 11 de los publicadores con los tres casos de la teoría), `Superado: 3` en `PaymentProofPublicLinksHostTests` y `ArchitectureTests` en verde. Esta última incluye `QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`: el puerto sólo usa tipos del BCL. Todos con `Con error: 0`. Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 10: Build y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet build Backend.slnx --no-restore
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`, y el último bloque sin salida (o sólo líneas que no tocaste, anotadas en el handoff).

- [ ] **Step 11: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IPaymentProofPublisher.cs src/Bootstrapper/PublicPaymentProofPublisher.cs src/Bootstrapper/DisabledPaymentProofPublisher.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs tests/Bootstrapper/Bootstrapper.UnitTests/PaymentProofPublisherTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PaymentProofPublicLinksHostTests.cs; git commit -m "feat(orders): publicador de comprobantes de pago en el bucket público" -m "Puerto IPaymentProofPublisher con dos implementaciones en el Bootstrapper: la que copia a payment-proofs/ con una clave aleatoria y la que no hace nada. DI elige una u otra según Quotations:PaymentProofs:PublicLinks."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 4: Publicar los comprobantes nuevos al convertir y al sumarlos

`PaymentProofCopies` publica los comprobantes nuevos en orden y guarda las claves para el rollback (P4, P7). Los dos handlers la usan entre la resolución de los archivos y el dominio, y envuelven dominio, auditoría y `SaveChanges` en el try cuyo catch borra las copias. La guía de integración deja de decir que los comprobantes no tienen URL pública.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:42-124` (la clase del handler)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs:53-112` (la clase del handler)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (final del archivo)
- Modify: `docs/integracion-cotizaciones-y-pedidos.md:121`

**Interfaces:**
- Consumes: `IPaymentProofPublisher` (Task 3); `OrderPaymentProofInput(Guid FileId, decimal Amount, string? PublicStorageKey = null)` (Task 2); en el harness, `QepApiFactory(..., publicPaymentProofLinks: true)` y `factory.PublicObjectStorage` con `Copies`, `DeletedKeys` y `FailingCopyAttempt` (Task 3).
- Produces:
  - `internal sealed class PaymentProofCopies(IPaymentProofPublisher publisher)` con `Task<OrderPaymentProofInput[]> PublishAsync(Guid tenantId, IReadOnlyCollection<OrderPaymentProofRequest> proofs, CancellationToken cancellationToken)` y `Task RollbackAsync()`.
  - Los dos handlers reciben un parámetro nuevo `IPaymentProofPublisher paymentProofPublisher` (DI lo resuelve; no hay pruebas que los construyan a mano).
  - En `QuotationsTestDoubles.cs`: `internal sealed class RecordingPaymentProofPublisher(bool enabled = true) : IPaymentProofPublisher`, con `const string BaseUrl = "https://assets-qep.example.co"`, `static string KeyFor(Guid fileId)` (`$"payment-proofs/{fileId:N}.pdf"`), `int? FailingPublishCall`, `string? FailingDeleteKey`, `List<string> PublishedKeys`, `List<string> DeletedKeys`, `List<CancellationToken> DeleteTokens`, y `UrlFor` = `$"{BaseUrl}/{key}"`, o null con `enabled: false`. Task 6 lo usa en las pruebas del procesador.

- [ ] **Step 1: El doble del publicador para las pruebas unitarias**

Agrega al final de `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs`:

```csharp

/// <summary>
/// El publicador de comprobantes (spec 2026-09-15): anota qué se publicó y qué se borró, y con qué
/// token. La clave sale del id del archivo para que la prueba la pueda predecir; la real es
/// aleatoria. Con <c>enabled</c> en false se porta como la opción apagada.
/// </summary>
internal sealed class RecordingPaymentProofPublisher(bool enabled = true) : IPaymentProofPublisher
{
    public const string BaseUrl = "https://assets-qep.example.co";

    private int _publishCalls;

    /// <summary>La llamada a <see cref="PublishAsync"/> (desde 1) que falla; null si ninguna.</summary>
    public int? FailingPublishCall { get; set; }

    /// <summary>La clave cuyo borrado falla, para probar que el rollback sigue con las demás.</summary>
    public string? FailingDeleteKey { get; set; }

    public List<string> PublishedKeys { get; } = [];

    public List<string> DeletedKeys { get; } = [];

    public List<CancellationToken> DeleteTokens { get; } = [];

    public static string KeyFor(Guid fileId) => $"payment-proofs/{fileId:N}.pdf";

    public Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        _publishCalls++;
        if (_publishCalls == FailingPublishCall)
        {
            return Task.FromException<string?>(new InvalidOperationException("Simulated copy failure."));
        }

        if (!enabled)
        {
            return Task.FromResult<string?>(null);
        }

        var key = KeyFor(fileId);
        PublishedKeys.Add(key);
        return Task.FromResult<string?>(key);
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        DeleteTokens.Add(cancellationToken);
        if (publicKey == FailingDeleteKey)
        {
            return Task.FromException(new InvalidOperationException("Simulated delete failure."));
        }

        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public string? UrlFor(string publicKey) => enabled ? $"{BaseUrl}/{publicKey}" : null;
}
```

- [ ] **Step 2: Pruebas unitarias de `PaymentProofCopies` (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las copias públicas de los comprobantes de un request (spec 2026-09-15, P4 y P7): qué se publica,
/// con qué clave llega al dominio, y qué borra el rollback cuando el request falla después de copiar.
/// </summary>
public sealed class PaymentProofCopiesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    // Cada comprobante nuevo sale con la clave de su copia, en el orden del request.
    [Fact]
    public async Task PublishesEachProofAndHandsItsKeyToTheDomain()
    {
        var publisher = new RecordingPaymentProofPublisher();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var inputs = await new PaymentProofCopies(publisher).PublishAsync(
            TenantId, [new(first, 60_000m), new(second, 40_000m)], TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new OrderPaymentProofInput(first, 60_000m, RecordingPaymentProofPublisher.KeyFor(first)),
                new OrderPaymentProofInput(second, 40_000m, RecordingPaymentProofPublisher.KeyFor(second)),
            ],
            inputs);
    }

    // P1: apagada, no hay copia, el comprobante queda privado y el rollback no tiene nada que borrar.
    [Fact]
    public async Task WithTheOptionOffTheProofsGoWithoutKeyAndTheRollbackDeletesNothing()
    {
        var publisher = new RecordingPaymentProofPublisher(enabled: false);
        var copies = new PaymentProofCopies(publisher);

        var inputs = await copies.PublishAsync(
            TenantId, [new(Guid.CreateVersion7(), 1m)], TestContext.Current.CancellationToken);
        await copies.RollbackAsync();

        Assert.Null(Assert.Single(inputs).PublicStorageKey);
        Assert.Empty(publisher.DeletedKeys);
    }

    // P7: si el request falla después de copiar, el rollback borra cada copia hecha, sin el token del
    // request, que puede ser justo el que se canceló.
    [Fact]
    public async Task RollbackDeletesEveryCopyMadeWithoutTheRequestToken()
    {
        var publisher = new RecordingPaymentProofPublisher();
        var copies = new PaymentProofCopies(publisher);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await copies.PublishAsync(TenantId, [new(first, 1m), new(second, 2m)], request.Token);

        await copies.RollbackAsync();

        Assert.Equal(
            [RecordingPaymentProofPublisher.KeyFor(first), RecordingPaymentProofPublisher.KeyFor(second)],
            publisher.DeletedKeys);
        Assert.All(publisher.DeleteTokens, token => Assert.False(token.CanBeCanceled));
    }

    // P7: una copia que falla sube su excepción, y las anteriores quedan anotadas para el rollback.
    [Fact]
    public async Task ACopyThatFailsLeavesTheEarlierCopiesToTheRollback()
    {
        var publisher = new RecordingPaymentProofPublisher { FailingPublishCall = 2 };
        var copies = new PaymentProofCopies(publisher);
        var first = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => copies.PublishAsync(
            TenantId, [new(first, 1m), new(Guid.CreateVersion7(), 2m)], TestContext.Current.CancellationToken));
        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(first)], publisher.DeletedKeys);
    }

    // Best-effort: un borrado que falla no deja las demás copias sin borrar, y RollbackAsync no lanza
    // —si lanzara, taparía la excepción original del request—.
    [Fact]
    public async Task ADeleteThatFailsDoesNotStopTheOthers()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var publisher = new RecordingPaymentProofPublisher
        {
            FailingDeleteKey = RecordingPaymentProofPublisher.KeyFor(first),
        };
        var copies = new PaymentProofCopies(publisher);
        await copies.PublishAsync(
            TenantId, [new(first, 1m), new(second, 2m)], TestContext.Current.CancellationToken);

        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(second)], publisher.DeletedKeys);
    }
}
```

- [ ] **Step 3: Pruebas de integración de los handlers (RED)**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La copia pública de los comprobantes al adjuntarlos (spec 2026-09-15, P4 y P7), de punta a punta:
/// convertir y sumar comprobantes con la opción encendida y apagada, una copia que falla, un rechazo
/// del dominio después de copiar, y corregir sólo montos. La respuesta de la API no expone la clave
/// (OrderPaymentProofResponse no cambia), así que se lee de la base.
/// </summary>
public sealed class OrderPaymentProofPublicationApiTests
{
    private const string PublicKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";

    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    private static string OrderProofsUrl(Guid tenantId, Guid quotationId) =>
        $"{OrderUrl(tenantId, quotationId)}/proofs";

    // P4: con la opción encendida, cada comprobante de la conversión tiene su copia y su clave queda
    // guardada.
    [Fact]
    public async Task ConvertingWithPublicLinksOnCopiesEachProofAndStoresItsKey()
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

        var keys = await PublicKeysAsync(factory, order.Id);
        Assert.Equal(2, keys.Length);
        Assert.All(keys, key => Assert.Matches(PublicKeyPattern, key));
        Assert.Equal(
            factory.PublicObjectStorage.Copies.Keys.Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
    }

    // P1: apagada, no se copia nada y el comprobante queda privado. Ya pasa antes de esta tarea:
    // protege el camino de siempre.
    [Fact]
    public async Task ConvertingWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Null(Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // P4: sumar comprobantes a un pedido pendiente también los copia.
    [Fact]
    public async Task AddingProofsWithPublicLinksOnCopiesTheNewProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.Matches(PublicKeyPattern, key);
        Assert.Equal(key, Assert.Single(factory.PublicObjectStorage.Copies).Key);
    }

    // P1: apagada, sumar comprobantes tampoco copia nada y el nuevo queda privado. Ya pasa antes de
    // esta tarea: protege el camino de siempre.
    [Fact]
    public async Task AddingProofsWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // P4: corregir un monto no cambia el archivo: no copia nada ni toca la clave que ya había.
    [Fact]
    public async Task CorrectingOnlyAmountsCopiesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var keyBefore = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.PublicObjectStorage.Copies);
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.Equal(keyBefore, Assert.Single(await PublicKeysAsync(factory, order.Id)));
    }

    // P7: si la segunda copia falla, el request falla, el pedido no se crea y la primera copia se borra.
    [Fact]
    public async Task AConversionWhoseCopyFailsCreatesNoOrderAndDeletesTheCopiesMade()
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
        Assert.Matches(PublicKeyPattern, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        var getOrder = await client.GetAsync(OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getOrder.StatusCode);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Sent", fetched.Status);
    }

    // P7: lo mismo al sumar comprobantes: el pedido queda como estaba.
    [Fact]
    public async Task AddingProofsWhoseCopyFailsAddsNothingAndDeletesTheCopiesMade()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Matches(PublicKeyPattern, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Empty(order.PaymentProofs);
        Assert.Equal("PaymentPending", order.PaymentStatus);
    }

    // P7: el rechazo del dominio llega después de copiar —acá, sumar a un pedido que otra persona
    // acaba de aprobar— y la copia de ese request se borra. La del comprobante original queda.
    [Fact]
    public async Task ADomainRejectionAfterCopyingDeletesThatCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId);
        var firstKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.order.not_pending", problem?.Code);
        Assert.NotEqual(firstKey, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Equal(firstKey, Assert.Single(factory.PublicObjectStorage.Copies).Key);
        Assert.Equal(firstKey, Assert.Single(await PublicKeysAsync(factory, order.Id)));
    }

    // P7 en la conversión: una segunda conversión de la misma cotización la corta el dominio
    // (status_not_convertible) después de copiar su comprobante, y esa copia se borra.
    [Fact]
    public async Task ASecondConversionDeletesTheCopyItMade()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId);
        var firstKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.status_not_convertible", problem?.Code);
        Assert.NotEqual(firstKey, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Equal(firstKey, Assert.Single(factory.PublicObjectStorage.Copies).Key);
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        return await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
    }

    private static async Task<OrderResponse> ConvertAsync(
        HttpClient client, Guid tenantId, Guid quotationId, string paymentStatus, params Guid[] proofFileIds)
    {
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotationId),
            new ConvertQuotationToOrderRequest(
                paymentStatus,
                null,
                proofFileIds.Select(fileId => new OrderPaymentProofRequest(fileId, 10_000m)).ToArray()),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // La respuesta no expone la clave: se lee de la base.
    private static async Task<string?[]> PublicKeysAsync(QepApiFactory factory, Guid orderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderId(orderId);
        return await dbContext.OrderPaymentProofs
            .AsNoTracking()
            .Where(proof => proof.OrderId == id)
            .Select(proof => proof.PublicStorageKey)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private sealed record ProblemDto(string? Code);
}
```

- [ ] **Step 4: Correrlas y verlas fallar**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado (RED):
- el build de unitarias falla con `error CS0246` por `PaymentProofCopies`;
- de las 9 de integración falla todo menos las dos con la opción apagada, `ConvertingWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty` y `AddingProofsWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty`: los handlers todavía no publican, así que las claves quedan null (`Assert.All() Failure`, `Assert.Matches() Failure`), no hay copias (`Assert.Single() Failure: The collection was empty`) y las conversiones con copia fallida responden `Created`/`OK` donde se esperaba `InternalServerError`.

Pega las dos salidas.

- [ ] **Step 5: `PaymentProofCopies`**

Crea `src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Las copias públicas de los comprobantes nuevos de un request (spec 2026-09-15, P4 y P7). La usan
/// <see cref="ConvertQuotationToOrderHandler"/> y <see cref="AddOrderPaymentProofsHandler"/>: cada
/// uno publica antes de tocar el dominio —<see cref="OrderPaymentProof"/> recibe la clave al
/// crearse— y, si algo falla después de la primera copia, llama a <see cref="RollbackAsync"/> y
/// relanza. Ese "algo" incluye un rechazo del dominio, como <c>order.order.not_pending</c>: así el
/// handler no duplica las reglas del dominio para decidir si publicar.
/// </summary>
internal sealed class PaymentProofCopies(IPaymentProofPublisher publisher)
{
    private readonly List<string> _publicKeys = [];

    /// <summary>Publica cada comprobante en orden y devuelve los inputs del dominio con su clave
    /// (null con la opción apagada). Si una copia falla, las anteriores quedan anotadas para el
    /// rollback.</summary>
    public async Task<OrderPaymentProofInput[]> PublishAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderPaymentProofRequest> proofs,
        CancellationToken cancellationToken)
    {
        var inputs = new List<OrderPaymentProofInput>(proofs.Count);
        foreach (var proof in proofs)
        {
            var publicKey = await publisher.PublishAsync(tenantId, proof.FileId, cancellationToken);
            if (publicKey is not null)
            {
                _publicKeys.Add(publicKey);
            }

            inputs.Add(new OrderPaymentProofInput(proof.FileId, proof.Amount, publicKey));
        }

        return inputs.ToArray();
    }

    /// <summary>Borra las copias hechas, best-effort, mismo criterio que el rollback de
    /// <c>PublishFileHandler</c> (SetFilePublication.cs): cada una en su propio try, para que una
    /// que falle no deje las demás, y con <see cref="CancellationToken.None"/>, porque el request que
    /// se canceló es justo el que dejó las copias.</summary>
    public async Task RollbackAsync()
    {
        foreach (var publicKey in _publicKeys)
        {
            try
            {
                await publisher.DeleteAsync(publicKey, CancellationToken.None);
            }
            catch
            {
                // Best-effort: una copia que no se pudo borrar queda huérfana en el bucket público,
                // y relanzar desde acá taparía la excepción original del request.
            }
        }
    }
}
```

- [ ] **Step 6: El handler de conversión**

En `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs`, reemplaza desde `public sealed class ConvertQuotationToOrderHandler(` hasta el final del archivo por:

```csharp
public sealed class ConvertQuotationToOrderHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ConvertQuotationToOrderCommand> validator)
    : ICommandHandler<ConvertQuotationToOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        ConvertQuotationToOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // US-18 rest: revalidar CUC/activo al aprobar, por si el estado del cliente cambió
        // después de enviar la cotización.
        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        var convertedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var orderNumber = OrderNumberFormatter.Format(now.Year, sequence);
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Las copias públicas de los comprobantes (spec 2026-09-15, P4 y P7) van antes del dominio,
        // porque OrderPaymentProof recibe la clave al crearse. Desde la primera copia, cualquier
        // falla —un rechazo de ConvertToOrder incluido— borra las copias y relanza: un pedido que no
        // se guardó no puede dejar comprobantes publicados.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        Order order;
        try
        {
            var proofs = await copies.PublishAsync(
                command.TenantId, command.PaymentProofs, cancellationToken);

            // Pasar la cotización a Converted y crear el pedido en la misma unidad de trabajo
            // (modelo-datos-cotizaciones.md §3): si guardar falla, no queda ninguna de las dos cosas.
            // ConvertToOrder valida las precondiciones antes de mutar. El historial (Approved) y la
            // auditoría (quotation.quotation.approved) no cambian de nombre: son contrato, no el
            // nombre del estado.
            quotation.ConvertToOrder(convertedBy, now);
            order = Order.Create(
                OrderId.New(),
                command.TenantId,
                orderNumber,
                quotation.Id,
                paymentStatus,
                command.Notes,
                convertedBy,
                proofs,
                now);

            orderRepository.Add(order);
            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Approved,
                convertedBy,
                QuotationChangeSummary.ConvertedToOrder(order.OrderNumber),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.quotation.approved",
                quotation.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return order.ToDto();
    }
}
```

- [ ] **Step 7: El handler que suma comprobantes**

En `src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs`, reemplaza desde `public sealed class AddOrderPaymentProofsHandler(` hasta el final del archivo por:

```csharp
public sealed class AddOrderPaymentProofsHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderPaymentProofsCommand> validator)
    : ICommandHandler<AddOrderPaymentProofsCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        AddOrderPaymentProofsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        var uploadedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Sólo los comprobantes nuevos se publican (spec 2026-09-15, P4): corregir un monto
        // (UpdatedProofs) no cambia el archivo. Antes del dominio y con rollback, igual que al
        // convertir (P7): si otra persona acaba de aprobar el pedido, AddPaymentProofs lo rechaza
        // con order.order.not_pending después de copiar, y esas copias se borran.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        try
        {
            var proofs = await copies.PublishAsync(
                command.TenantId, command.PaymentProofs, cancellationToken);

            order.AddPaymentProofs(
                proofs,
                paymentStatus,
                command.Notes,
                uploadedBy,
                now,
                command.UpdatedProofs
                    .Select(update => new OrderPaymentProofAmountUpdate(
                        new OrderPaymentProofId(update.ProofId), update.Amount))
                    .ToArray());

            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.order.payment_proofs_added",
                order.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        return order.ToDto();
    }
}
```

- [ ] **Step 8: Correr las pruebas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderPaymentProofPublicationApiTests|FullyQualifiedName~OrderApiTests|FullyQualifiedName~OrderContractApiTests"
```

Esperado: la suite unitaria de Quotations completa con `Con error: 0` (con las 5 de `PaymentProofCopiesTests`), y en integración las 9 nuevas de `OrderPaymentProofPublicationApiTests` más `OrderApiTests` y `OrderContractApiTests` enteras en verde: con la opción apagada, convertir y sumar comprobantes se porta como antes. Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 9: La guía de integración**

En `docs/integracion-cotizaciones-y-pedidos.md`, reemplaza:

```markdown
No hace falta publicar (paso 5 de esa guía) — estos archivos no necesitan URL pública.
```

por:

```markdown
No hace falta publicar (paso 5 de esa guía). Con `Quotations:PaymentProofs:PublicLinks` encendida,
el backend copia cada comprobante nuevo al bucket público al convertir o al sumar comprobantes, para
que el Excel de pedidos lo enlace; el frontend no hace nada distinto, y la respuesta de la API no
cambia.
```

- [ ] **Step 10: Build y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet build Backend.slnx --no-restore
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`, y el último bloque sin salida (o sólo líneas que no tocaste, anotadas en el handoff).

- [ ] **Step 11: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/PaymentProofCopies.cs src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs src/Modules/Quotations/Modules.Quotations.Application/AddOrderPaymentProofs.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/PaymentProofCopiesTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderPaymentProofPublicationApiTests.cs docs/integracion-cotizaciones-y-pedidos.md; git commit -m "feat(orders): publicar los comprobantes nuevos al convertir y al sumarlos" -m "Cada comprobante nuevo se copia al bucket público antes del dominio y su clave queda en el comprobante. Si algo falla después de copiar, un rechazo del dominio incluido, las copias se borran y el request falla. Corregir montos no copia nada."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 5: Celdas de enlace en el writer de Excel

`ExportCell` gana `Url` y `OfLink` (E5). `OpenXmlExportWorkbook` escribe el enlace como fórmula `HYPERLINK` con el valor ya calculado y el estilo 2, escapa las comillas y cae a texto plano pasados los 255 caracteres (E3, E4). Nadie usa `OfLink` todavía: el Excel de cotizaciones no cambia, y el de pedidos lo usa en Task 6.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs:27-36`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs:31-32,247-268,340-361`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs:235-275`

**Interfaces:**
- Consumes: nada de tareas anteriores.
- Produces: `public readonly record struct ExportCell(string? Text, decimal? Number, string? Url)` con `OfText(string?)`, `OfNumber(decimal)` y `OfLink(string url, string text)`. Task 6 lo usa.

- [ ] **Step 1: Pruebas del writer (RED)**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs`, reemplaza:

```csharp
    private sealed record CellSnapshot(string Text, bool IsNumber, uint? StyleIndex);
```

por las pruebas nuevas y el record ampliado:

```csharp
    // Spec 2026-09-15, E3: el enlace es la fórmula HYPERLINK con el valor ya calculado —se ve bien
    // antes de que Excel recalcule, y en visores que no calculan— y el estilo de enlace.
    [Fact]
    public void ALinkCellIsAHyperlinkFormulaWithItsCachedValueAndTheLinkStyle()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/payment-proofs/abc.pdf", "Ver"),
            ExportCell.OfNumber(1m)]);

        var sheet = Read(workbook.Complete());

        var link = sheet.Rows[1][1];
        Assert.Equal("HYPERLINK(\"https://assets.qep.test/payment-proofs/abc.pdf\",\"Ver\")", link.Formula);
        Assert.Equal("Ver", link.Text);
        Assert.Equal(CellValues.String, link.Type);
        Assert.Equal(2u, link.StyleIndex);
        Assert.True(sheet.StyleTwoIsALink);
        Assert.Null(sheet.Rows[1][0].Formula);
        Assert.Null(sheet.Rows[1][0].StyleIndex);
    }

    // E3: las comillas dobles se escapan duplicándolas, en la URL y en el texto; el valor calculado
    // queda sin escapar.
    [Fact]
    public void QuotesInTheUrlAndTheTextAreDoubled()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/a\"b.pdf", "Ver \"1\""),
            ExportCell.OfNumber(1m)]);

        var link = Read(workbook.Complete()).Rows[1][1];

        Assert.Equal("HYPERLINK(\"https://assets.qep.test/a\"\"b.pdf\",\"Ver \"\"1\"\"\")", link.Formula);
        Assert.Equal("Ver \"1\"", link.Text);
    }

    // E4: 255 es el tope de Excel para una cadena dentro de una fórmula. Hasta ahí, enlace.
    [Fact]
    public void AUrlOf255CharactersIsStillALink()
    {
        var url = UrlOfLength(255);
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([ExportCell.OfText("PED-2026-0001"), ExportCell.OfLink(url, "Ver"), ExportCell.OfNumber(1m)]);

        var link = Read(workbook.Complete()).Rows[1][1];

        Assert.Equal($"HYPERLINK(\"{url}\",\"Ver\")", link.Formula);
        Assert.Equal("Ver", link.Text);
    }

    // E4: una más y la celda lleva la URL como texto plano, sin estilo de enlace, para que se vea en
    // vez de perderse.
    [Fact]
    public void AUrlLongerThan255CharactersIsWrittenAsPlainText()
    {
        var url = UrlOfLength(256);
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([ExportCell.OfText("PED-2026-0001"), ExportCell.OfLink(url, "Ver"), ExportCell.OfNumber(1m)]);

        var cell = Read(workbook.Complete()).Rows[1][1];

        Assert.Null(cell.Formula);
        Assert.Equal(url, cell.Text);
        Assert.Equal(CellValues.InlineString, cell.Type);
        Assert.Null(cell.StyleIndex);
    }

    // Una celda de enlace y el tercer formato de celda no pueden dejar el archivo inválido.
    [Fact]
    public void AFileWithALinkPassesTheOpenXmlValidator()
    {
        using var workbook = new OpenXmlExportWorkbookWriter().Create("Pedidos", Columns);
        workbook.AppendRow([
            ExportCell.OfText("PED-2026-0001"),
            ExportCell.OfLink("https://assets.qep.test/payment-proofs/abc.pdf", "Ver"),
            ExportCell.OfNumber(1m)]);

        using var document = SpreadsheetDocument.Open(workbook.Complete(), isEditable: false);
        var errors = new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken)
            .Select(error => $"{error.ErrorType} {error.Part?.Uri} {error.Path?.XPath}: {error.Description}")
            .ToArray();

        Assert.Empty(errors);
    }

    private static string UrlOfLength(int length)
    {
        const string Prefix = "https://assets.qep.test/payment-proofs/";
        return Prefix + new string('a', length - Prefix.Length);
    }

    private sealed record CellSnapshot(
        string Text, bool IsNumber, uint? StyleIndex, string? Formula, CellValues? Type);
```

Reemplaza:

```csharp
    private sealed record SheetSnapshot(
        string Name,
        IReadOnlyList<IReadOnlyList<CellSnapshot>> Rows,
        IReadOnlyList<double> Widths,
        bool HeaderIsFrozen,
        bool StyleOneIsBold);
```

por:

```csharp
    private sealed record SheetSnapshot(
        string Name,
        IReadOnlyList<IReadOnlyList<CellSnapshot>> Rows,
        IReadOnlyList<double> Widths,
        bool HeaderIsFrozen,
        bool StyleOneIsBold,
        bool StyleTwoIsALink);
```

En `Read`, reemplaza:

```csharp
                .Select(cell => new CellSnapshot(
                    cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty,
                    cell.DataType?.Value == CellValues.Number,
                    cell.StyleIndex?.Value))
```

por:

```csharp
                .Select(cell => new CellSnapshot(
                    cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty,
                    cell.DataType?.Value == CellValues.Number,
                    cell.StyleIndex?.Value,
                    cell.CellFormula?.Text,
                    cell.DataType?.Value))
```

y reemplaza:

```csharp
        var headerFont = stylesheet.Fonts!.Elements<Font>().ElementAt((int)headerFontId);

        return new SheetSnapshot(
            sheet.Name!.Value!,
            rows,
            widths,
            pane?.State?.Value == PaneStateValues.Frozen && pane.TopLeftCell?.Value == "A2",
            headerFont.Bold is not null);
```

por:

```csharp
        var headerFont = stylesheet.Fonts!.Elements<Font>().ElementAt((int)headerFontId);
        // El formato 2 es el del enlace (E3): fuente subrayada y azul de Office.
        var linkFontId = stylesheet.CellFormats!.Elements<CellFormat>().ElementAtOrDefault(2)?.FontId?.Value;
        var linkFont = linkFontId is { } fontId
            ? stylesheet.Fonts!.Elements<Font>().ElementAtOrDefault((int)fontId)
            : null;

        return new SheetSnapshot(
            sheet.Name!.Value!,
            rows,
            widths,
            pane?.State?.Value == PaneStateValues.Frozen && pane.TopLeftCell?.Value == "A2",
            headerFont.Bold is not null,
            linkFont?.Underline is not null
                && string.Equals(linkFont.Color?.Rgb?.Value, "FF0563C1", StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
```

Esperado (RED): `error CS0117: 'ExportCell' no contiene una definición para 'OfLink'` (o `does not contain a definition for 'OfLink'`). Pega las líneas.

- [ ] **Step 3: `ExportCell` con enlace**

En `src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs`, reemplaza:

```csharp
/// <summary>
/// Una celda: texto o número, nunca los dos. Las fechas viajan como texto ISO a propósito; los
/// importes como número, porque quien abre el archivo los suma y filtra.
/// </summary>
public readonly record struct ExportCell(string? Text, decimal? Number)
{
    public static ExportCell OfText(string? value) => new(value ?? string.Empty, null);

    public static ExportCell OfNumber(decimal value) => new(null, value);
}
```

por:

```csharp
/// <summary>
/// Una celda: texto, número o enlace, nunca dos a la vez. Las fechas viajan como texto ISO a
/// propósito; los importes como número, porque quien abre el archivo los suma y filtra. El enlace
/// (spec 2026-09-15, E5) lo usa el Excel de pedidos para los comprobantes de pago: <c>Url</c> es el
/// destino y <c>Text</c> lo que se ve.
/// </summary>
public readonly record struct ExportCell(string? Text, decimal? Number, string? Url)
{
    public static ExportCell OfText(string? value) => new(value ?? string.Empty, null, null);

    public static ExportCell OfNumber(decimal value) => new(null, value, null);

    public static ExportCell OfLink(string url, string text) => new(text, null, url);
}
```

- [ ] **Step 4: Compila, y las pruebas del enlace fallan en sus aserciones**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore --filter "FullyQualifiedName~OpenXmlExportWorkbookWriterTests"
```

Esperado (RED, ya en las aserciones): el writer todavía no conoce `Url` y escribe el enlace como texto con su `Text`. Fallan cuatro de las cinco nuevas: `ALinkCellIsAHyperlinkFormulaWithItsCachedValueAndTheLinkStyle`, `QuotesInTheUrlAndTheTextAreDoubled` y `AUrlOf255CharactersIsStillALink` con `Assert.Equal() Failure: Strings differ` y `Actual: null` (la celda no tiene `Formula`), y `AUrlLongerThan255CharactersIsWrittenAsPlainText` con `Strings differ` porque la celda dice `Ver` en vez de la URL. `AFileWithALinkPassesTheOpenXmlValidator` y las que ya existían pasan: sin fórmula ni tercer estilo, el archivo sigue siendo válido. Pega la salida.

- [ ] **Step 5: El writer**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs`:

Reemplaza:

```csharp
    // Índice 1 de CellFormats: la fuente en negrita de BuildStylesheet.
    private const uint HeaderStyleIndex = 1;
```

por:

```csharp
    // Índice 1 de CellFormats: la fuente en negrita de BuildStylesheet.
    private const uint HeaderStyleIndex = 1;

    // Índice 2 de CellFormats: la fuente azul y subrayada de un enlace (spec 2026-09-15, E3).
    private const uint LinkStyleIndex = 2;

    // El color de los enlaces de Office, en ARGB.
    private const string LinkColor = "FF0563C1";

    // El tope de Excel para una cadena dentro de una fórmula (E4).
    private const int MaxFormulaStringLength = 255;
```

Reemplaza el comentario y el método `ToCell` enteros:

```csharp
    // Texto inline y no la tabla de strings compartidos: la tabla se arma en memoria hasta el
    // final, que es justo lo que el streaming evita.
    private static Cell ToCell(ExportCell value, string reference, uint? styleIndex)
    {
        var cell = value.Number is { } number
            ? new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) }
            : new Cell
            {
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(RemoveInvalidXmlChars(value.Text ?? string.Empty))
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            };
        cell.CellReference = reference;
        if (styleIndex is { } style)
        {
            cell.StyleIndex = style;
        }

        return cell;
    }
```

por:

```csharp
    // Texto inline y no la tabla de strings compartidos: la tabla se arma en memoria hasta el
    // final, que es justo lo que el streaming evita. Por lo mismo el enlace es una fórmula y no un
    // hipervínculo de relación (spec 2026-09-15, E3): ése vive en <hyperlinks>, después de
    // <sheetData>, y en sheet1.xml.rels, y el zip admite una sola entrada abierta a la vez, así que
    // habría que guardar todos los enlaces en memoria hasta el final.
    private static Cell ToCell(ExportCell value, string reference, uint? styleIndex)
    {
        Cell cell;
        var style = styleIndex;
        if (value.Number is { } number)
        {
            cell = new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) };
        }
        else if (value.Url is { } url && HyperlinkFormula(url, value.Text ?? string.Empty) is { } formula)
        {
            // <v> lleva el valor ya calculado: el archivo se ve bien antes de que Excel recalcule, y
            // en visores que no calculan.
            cell = new Cell
            {
                DataType = CellValues.String,
                CellFormula = new CellFormula(formula),
                CellValue = new CellValue(RemoveInvalidXmlChars(value.Text ?? string.Empty)),
            };
            style ??= LinkStyleIndex;
        }
        else
        {
            // E4: una URL que no entra en la fórmula sale como texto plano, para que se vea en vez de
            // perderse.
            cell = TextCell(value.Url ?? value.Text);
        }

        cell.CellReference = reference;
        if (style is { } appliedStyle)
        {
            cell.StyleIndex = appliedStyle;
        }

        return cell;
    }

    private static Cell TextCell(string? text) =>
        new()
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(RemoveInvalidXmlChars(text ?? string.Empty))
            {
                Space = SpaceProcessingModeValues.Preserve,
            }),
        };

    // HYPERLINK("url","texto"). En el XML los argumentos van separados con coma sin importar la
    // configuración regional: Excel la muestra con el separador de quien abre el archivo. Las
    // comillas dobles se escapan duplicándolas. Null si alguna de las dos cadenas pasa el tope de
    // Excel (E4); se mide ya escapada, que es lo que Excel lee dentro de la fórmula.
    private static string? HyperlinkFormula(string url, string text)
    {
        var escapedUrl = EscapeFormulaString(RemoveInvalidXmlChars(url));
        var escapedText = EscapeFormulaString(RemoveInvalidXmlChars(text));
        return escapedUrl.Length > MaxFormulaStringLength || escapedText.Length > MaxFormulaStringLength
            ? null
            : $"HYPERLINK(\"{escapedUrl}\",\"{escapedText}\")";
    }

    private static string EscapeFormulaString(string value) =>
        value.Replace("\"", "\"\"", StringComparison.Ordinal);
```

Y reemplaza el comentario y el método `BuildStylesheet` enteros:

```csharp
    // Lo mínimo que Excel acepta sin quejarse: dos fuentes (normal y negrita), los dos rellenos
    // que la especificación exige, un borde vacío y dos formatos de celda.
    private static Stylesheet BuildStylesheet() =>
        new(
            new Fonts(new Font(), new Font(new Bold())) { Count = 2U },
```

por:

```csharp
    // Lo mínimo que Excel acepta sin quejarse: tres fuentes (normal, negrita y la azul subrayada de
    // los enlaces), los dos rellenos que la especificación exige, un borde vacío y tres formatos de
    // celda (normal, cabecera y enlace).
    private static Stylesheet BuildStylesheet() =>
        new(
            new Fonts(
                new Font(),
                new Font(new Bold()),
                new Font(new Underline(), new Color { Rgb = HexBinaryValue.FromString(LinkColor) }))
            {
                Count = 3U,
            },
```

y, en el mismo método, reemplaza:

```csharp
            new CellFormats(
                new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U },
                new CellFormat { FontId = 1U, FillId = 0U, BorderId = 0U, ApplyFont = true })
            {
                Count = 2U,
            });
```

por:

```csharp
            new CellFormats(
                new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U },
                new CellFormat { FontId = 1U, FillId = 0U, BorderId = 0U, ApplyFont = true },
                new CellFormat { FontId = 2U, FillId = 0U, BorderId = 0U, ApplyFont = true })
            {
                Count = 3U,
            });
```

- [ ] **Step 6: Correr las pruebas (GREEN)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
```

Esperado: la suite unitaria de Quotations completa con `Con error: 0`: las 5 nuevas de `OpenXmlExportWorkbookWriterTests`, las que ya tenía (la cabecera sigue en negrita con el estilo 1), y `QuotationsExportProcessorTests` y `OrdersExportProcessorTests` sin cambios (E5). Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 7: Build y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet build Backend.slnx --no-restore
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`, y el último bloque sin salida (o sólo líneas que no tocaste, anotadas en el handoff).

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IExportWorkbookWriter.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Excel/OpenXmlExportWorkbookWriter.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OpenXmlExportWorkbookWriterTests.cs; git commit -m "feat(quotations): celdas de enlace en el Excel de las exportaciones" -m "ExportCell.OfLink escribe la fórmula HYPERLINK con el valor ya calculado y el estilo de enlace, compatible con el streaming. Las comillas se escapan y una URL de más de 255 caracteres sale como texto plano. El Excel de cotizaciones no cambia."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 6: Los comprobantes en el Excel de pedidos

`IOrderRepository.ListPaymentProofsForExportAsync` lee los comprobantes de un lote en una sola consulta (E6). `OrdersExportProcessor` suma las cuatro columnas y arma cada celda con `IPaymentProofPublisher.UrlFor` (E1, E2, E7). El README documenta la operación de `payment-proofs/`, y producción enciende la opción en su ConfigMap: es la tarea en la que el Excel enlaza las copias, así que encenderla antes publicaría comprobantes que ningún Excel muestra.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs:18,97-98`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs:239-240`
- Modify (reemplazo entero): `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:538-644` (`StubOrderListRepository`)
- Modify (reemplazo entero): `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`
- Modify (reemplazo entero): `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs:164,176,331`
- Modify: `README.md` (antes de `### Plantilla de WhatsApp (Zenvia)`, `:946` después de Task 1)
- Modify: `k8s/prod-configMap.yaml:66`

**Interfaces:**
- Consumes: `OrderPaymentProof.PublicStorageKey` (Task 2); `IPaymentProofPublisher.UrlFor` (Task 3); `RecordingPaymentProofPublisher` con `BaseUrl` y `enabled` (Task 4); el flag `publicPaymentProofLinks`, `factory.PublicObjectStorage` e `InMemoryPublicObjectStorage.BaseUrl` (Task 3); `ExportCell.OfLink` y `ExportCell.Url` (Task 5).
- Produces:
  - `public sealed record OrderExportPaymentProof(OrderPaymentProofId Id, string? PublicStorageKey, DateTimeOffset UploadedAt);` en `Modules.Quotations.Application` (`IOrderRepository.cs`).
  - `Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)` en `IOrderRepository`, `OrderRepository` y `StubOrderListRepository`. Un pedido sin comprobantes, o de otro tenant, no aparece en el diccionario.
  - `OrdersExportProcessor(IOrderRepository repository, IQuotationCustomerLookup customerLookup, IQuotationAdvisorLookup advisorLookup, IPaymentProofPublisher paymentProofPublisher, IExportWorkbookWriter writer, IExportFileStorage storage, IClock clock)`, con las constantes públicas `ProofLinkText = "Ver"` y `PrivateProofText = "Sin enlace"`.
  - `StubOrderListRepository.PaymentProofRequests` (`List<IReadOnlyCollection<OrderId>>`).
  - `ExportWorkbookSheet.Formulas` (`IReadOnlyList<IReadOnlyList<string?>>`).

- [ ] **Step 1: El stub del repositorio y las pruebas del procesador (RED)**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs`, dentro de `StubOrderListRepository`, reemplaza:

```csharp
    private IEnumerable<OrderWithQuotation> Matching(IReadOnlyCollection<Guid>? clientIds) =>
```

por:

```csharp
    /// <summary>Con qué pedidos se pidieron comprobantes, en orden: una vez por lote, no por pedido
    /// (spec 2026-09-15, E6).</summary>
    public List<IReadOnlyCollection<OrderId>> PaymentProofRequests { get; } = [];

    // Los comprobantes que el dominio tiene cargados, en el orden en que se agregaron. El orden por
    // fecha de subida e id es SQL y lo prueba OrderExportApiTests contra Postgres.
    public Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        PaymentProofRequests.Add(orderIds);
        return Task.FromResult<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>>(
            rows
                .Where(row => orderIds.Contains(row.Order.Id) && row.Order.PaymentProofs.Count > 0)
                .ToDictionary(
                    row => row.Order.Id,
                    row => (IReadOnlyList<OrderExportPaymentProof>)row.Order.PaymentProofs
                        .Select(proof => new OrderExportPaymentProof(proof.Id, proof.PublicStorageKey, proof.UploadedAt))
                        .ToArray()));
    }

    private IEnumerable<OrderWithQuotation> Matching(IReadOnlyCollection<Guid>? clientIds) =>
```

Reemplaza entero `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs` por:

```csharp
using System.Globalization;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El procesador de pedidos: las columnas de la tabla de pedidos en su orden (hallazgo 2 del plan),
/// lectura por lotes con el filtro del listado, y los mismos fallos definitivos que cotizaciones.
/// Desde el spec 2026-09-15, también las cuatro columnas de los comprobantes de pago.
/// </summary>
public sealed class OrdersExportProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task WritesTheOrdersListColumnsInTheirOrder()
    {
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", paymentMethod: null)), writer);

        await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Pedidos", writer.SheetName);
        // Las ocho de la tabla en su orden y, después de Total, las cuatro de los comprobantes
        // (spec 2026-09-15, E1).
        Assert.Equal(
            ["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total",
                "Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            writer.Columns.Select(column => column.Header));
        var row = Assert.Single(writer.Rows);
        Assert.Equal("PED-2026-0001", row[0].Text);
        Assert.Equal("Ferretería El Tornillo", row[1].Text);
        Assert.Equal("asesora@qcode.co", row[2].Text);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[3].Text);
        // Sin forma de pago, la columna cae a la etiqueta del estado del pago, igual que la tabla
        // (spec 2026-09-13, A7).
        Assert.Equal("Pago pendiente", row[4].Text);
        Assert.Equal("Pendiente", row[5].Text);
        Assert.Equal(0m, row[7].Number);
    }

    [Fact]
    public async Task PagoShowsThePaymentMethodWhenThereIsOne()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", "Transferencia")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Transferencia", Assert.Single(writer.Rows)[4].Text);
    }

    // E2: sin comprobantes, la cantidad es cero y las tres celdas quedan vacías.
    [Fact]
    public async Task AnOrderWithoutProofsCountsZeroAndLeavesTheProofCellsEmpty()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(0m, row[8].Number);
        Assert.Equal([string.Empty, string.Empty, string.Empty], row.Skip(9).Select(cell => cell.Text));
        Assert.All(row.Skip(9), cell => Assert.Null(cell.Url));
    }

    // E2 y E3: un comprobante con copia pública es el enlace «Ver» a su URL pública.
    [Fact]
    public async Task AProofWithAPublicCopyIsAVerLinkToItsPublicUrl()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, ["payment-proofs/a.pdf"])), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(1m, row[8].Number);
        Assert.Equal(UrlOf("payment-proofs/a.pdf"), row[9].Url);
        Assert.Equal("Ver", row[9].Text);
        Assert.Equal(string.Empty, row[10].Text);
        Assert.Equal(string.Empty, row[11].Text);
    }

    // E1: tres comprobantes llenan las tres columnas, en el orden en que llegan del repositorio.
    [Fact]
    public async Task ThreeProofsFillTheThreeColumnsInOrder()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow(
                    "PED-2026-0001", null, ["payment-proofs/a.pdf", "payment-proofs/b.pdf", "payment-proofs/c.pdf"])),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(3m, row[8].Number);
        Assert.Equal(
            [UrlOf("payment-proofs/a.pdf"), UrlOf("payment-proofs/b.pdf"), UrlOf("payment-proofs/c.pdf")],
            row.Skip(9).Select(cell => cell.Url));
        Assert.All(row.Skip(9), cell => Assert.Equal("Ver", cell.Text));
    }

    // E1: el cuarto comprobante no tiene columna, pero la cantidad lo cuenta.
    [Fact]
    public async Task AFourthProofOnlyShowsInTheCount()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow(
                    "PED-2026-0001",
                    null,
                    ["payment-proofs/a.pdf", "payment-proofs/b.pdf", "payment-proofs/c.pdf", "payment-proofs/d.pdf"])),
                writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(12, row.Count);
        Assert.Equal(4m, row[8].Number);
        Assert.Equal(
            [UrlOf("payment-proofs/a.pdf"), UrlOf("payment-proofs/b.pdf"), UrlOf("payment-proofs/c.pdf")],
            row.Skip(9).Select(cell => cell.Url));
    }

    // E2: un comprobante privado —de antes de la opción, o adjuntado con ella apagada (P8)— dice
    // «Sin enlace»: que no haya enlace no es lo mismo que no haya comprobante.
    [Fact]
    public async Task AProofWithoutAPublicCopySaysSinEnlace()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, [null, "payment-proofs/b.pdf"])), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(2m, row[8].Number);
        Assert.Equal("Sin enlace", row[9].Text);
        Assert.Null(row[9].Url);
        Assert.Equal(UrlOf("payment-proofs/b.pdf"), row[10].Url);
        Assert.Equal(string.Empty, row[11].Text);
    }

    // P1 y E7: con la opción apagada no hay URL aunque el comprobante tenga copia, y las cuatro
    // columnas salen igual.
    [Fact]
    public async Task WithTheOptionOffEveryProofSaysSinEnlace()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, ["payment-proofs/a.pdf"])),
                writer,
                publisher: new RecordingPaymentProofPublisher(enabled: false))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(12, writer.Columns.Count);
        var row = Assert.Single(writer.Rows);
        Assert.Equal("Sin enlace", row[9].Text);
        Assert.Null(row[9].Url);
    }

    [Fact]
    public async Task ReadsInBatchesOfAThousandUntilAShortBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        var result = await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.ExportCalls);
        Assert.Equal(ExportJobLimits.BatchSize + 1, result.RowCount);
    }

    // E6: los comprobantes se piden una vez por lote, con los pedidos de ese lote, igual que los
    // nombres y los correos.
    [Fact]
    public async Task ReadsThePaymentProofsOncePerBatchWithTheOrdersOfThatBatch()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.PaymentProofRequests.Count);
        Assert.Equal(ExportJobLimits.BatchSize, repository.PaymentProofRequests[0].Count);
        // El lote corto trae el de número más bajo: el listado va de mayor a menor.
        Assert.Equal(
            rows.Single(row => row.Order.OrderNumber == "PED-2026-0001").Order.Id,
            Assert.Single(repository.PaymentProofRequests[1]));
    }

    // Keyset (D8): el lote siguiente arranca después del último pedido del anterior. Todas del
    // mismo instante: el número desempata, de mayor a menor, como en el listado.
    [Fact]
    public async Task EachBatchStartsAfterTheLastRowOfThePreviousOne()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}", paymentMethod: null))
            .ToArray();
        var repository = new StubOrderListRepository(rows);

        await NewProcessor(repository).ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new OrderExportCursor?[] { null, new OrderExportCursor(Now, "PED-2026-0002") },
            repository.ExportCursors);
    }

    [Fact]
    public async Task UploadsAsPedidosUnderTheJob()
    {
        var storage = new RecordingExportFileStorage();
        var job = NewJob();

        var result = await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)), storage: storage)
            .ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("pedidos-2026-09-12-1530.xlsx", result.FileName);
        Assert.Equal(job.Id, storage.Upload!.JobId);
    }

    [Fact]
    public async Task FiltersWithWhatTheRequestStored()
    {
        var repository = new StubOrderListRepository(NewRow("PED-2026-0001", null));
        var job = NewJob(new OrdersExportFilters(ClientId, AdvisorId.Value, "approved", "fullpaymentreceived", From, To, null, "PED"));

        await NewProcessor(repository).ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedOrderExportSearch(
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, From, To, "PED"),
            repository.LastExportSearch);
    }

    [Fact]
    public async Task NoOrdersWhenItRunsIsDefinitive()
    {
        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubOrderListRepository()).ProcessAsync(NewJob(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredPaymentStatusThatNoLongerExistsIsDefinitive()
    {
        var job = NewJob(new OrdersExportFilters(null, null, null, "Refunded", From, To, null, null));

        await Assert.ThrowsAsync<ExportJobDefinitiveException>(() =>
            NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001", null)))
                .ProcessAsync(job, TestContext.Current.CancellationToken));
    }

    private static string UrlOf(string publicKey) => $"{RecordingPaymentProofPublisher.BaseUrl}/{publicKey}";

    private static ExportJob NewJob(OrdersExportFilters? filters = null) =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            TenantId,
            Guid.CreateVersion7(),
            ExportJobKind.Orders,
            ExportJobFilters.Serialize(filters ?? new OrdersExportFilters(null, null, null, null, From, To, null, null)),
            Now);

    // Un comprobante por clave, en ese orden; null es un comprobante privado. Pago pendiente siempre:
    // el dominio lo admite con comprobantes o sin ellos, y así la columna Pago no cambia.
    private static OrderWithQuotation NewRow(
        string orderNumber, string? paymentMethod, IReadOnlyList<string?>? publicKeys = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var proofs = (publicKeys ?? [])
            .Select(publicKey => new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m, publicKey))
            .ToArray();
        var order = Order.Create(
            OrderId.New(), TenantId, orderNumber, quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, proofs, Now);
        return new OrderWithQuotation(order, quotation);
    }

    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        RecordingPaymentProofPublisher? publisher = null) =>
        new(repository,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            publisher ?? new RecordingPaymentProofPublisher(),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
}
```

- [ ] **Step 2: El lector del `.xlsx` y las pruebas de integración (RED)**

Reemplaza entero `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs` por:

```csharp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Modules.Quotations.IntegrationTests;

/// <param name="Formulas">La fórmula de cada celda, o null si no tiene. El enlace de un comprobante
/// de pago es una fórmula HYPERLINK (spec 2026-09-15, E3), y en <c>Rows</c> sólo se ve su valor ya
/// calculado.</param>
internal sealed record ExportWorkbookSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<bool>> NumericCells,
    IReadOnlyList<IReadOnlyList<string?>> Formulas);

/// <summary>
/// Abre el .xlsx que subió el worker con el SDK de OpenXML. Reabrir el archivo y leer celdas es la
/// convención del repo (CustomerExportApiTests): mirar sólo el status dejaría pasar un archivo con
/// las columnas corridas.
/// </summary>
internal static class ExportWorkbookReader
{
    public static ExportWorkbookSheet Read(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single();
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();

        return new ExportWorkbookSheet(
            sheet.Name!.Value!,
            rows.Select(row => (IReadOnlyList<string>)row.Elements<Cell>()
                .Select(cell => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty)
                .ToArray()).ToArray(),
            rows.Select(row => (IReadOnlyList<bool>)row.Elements<Cell>()
                .Select(cell => cell.DataType?.Value == CellValues.Number)
                .ToArray()).ToArray(),
            rows.Select(row => (IReadOnlyList<string?>)row.Elements<Cell>()
                .Select(cell => cell.CellFormula?.Text)
                .ToArray()).ToArray());
    }
}
```

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`, dentro de `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail`, reemplaza:

```csharp
        Assert.Equal(["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total"], sheet.Rows[0]);
```

por:

```csharp
        Assert.Equal(
            ["Pedido", "Cliente", "Asesor", "Fecha", "Pago", "Estado", "Moneda", "Total",
                "Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            sheet.Rows[0]);
```

y, en la misma prueba, reemplaza:

```csharp
        Assert.Equal(items[0].Total, decimal.Parse(first[7], CultureInfo.InvariantCulture));
```

por:

```csharp
        Assert.Equal(items[0].Total, decimal.Parse(first[7], CultureInfo.InvariantCulture));
        // Sin comprobantes (spec 2026-09-15, E2): la cantidad en cero y las tres celdas vacías, que
        // igual salen (E7).
        Assert.True(sheet.NumericCells[1][8]);
        Assert.Equal("0", first[8]);
        Assert.Equal([string.Empty, string.Empty, string.Empty], first.Skip(9));
```

Después, reemplaza:

```csharp
    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);
```

por las pruebas nuevas, sus helpers y el record de siempre:

```csharp
    // Spec 2026-09-15, de punta a punta con la opción encendida: el comprobante que se subió primero
    // es el enlace «Ver» a su copia pública, uno privado dice «Sin enlace» y el tercero queda vacío.
    // El privado se simula borrando su clave en la base: es lo que tienen los comprobantes de antes
    // de la opción (P8).
    [Fact]
    public async Task TheOrdersWorkbookLinksEachPublicProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderWithProofsAsync(client, factory, tenantId, proofCount: 1);
        var firstProofId = Assert.Single(order.PaymentProofs).Id;
        var withSecond = await AddProofAsync(client, factory, tenantId, order.QuotationId);
        var secondProofId = Assert.Single(withSecond.PaymentProofs, proof => proof.Id != firstProofId).Id;
        await ClearPublicStorageKeyAsync(factory, secondProofId);
        var firstKey = await PublicStorageKeyOfAsync(factory, firstProofId);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        Assert.Equal(
            ["Comprobantes", "Comprobante 1", "Comprobante 2", "Comprobante 3"],
            sheet.Rows[0].Skip(8));
        var row = sheet.Rows[1];
        Assert.Equal(order.OrderNumber, row[0]);
        Assert.True(sheet.NumericCells[1][8]);
        Assert.Equal("2", row[8]);
        Assert.Equal(
            $"HYPERLINK(\"{InMemoryPublicObjectStorage.BaseUrl}/{firstKey}\",\"Ver\")",
            sheet.Formulas[1][9]);
        Assert.Equal("Ver", row[9]);
        Assert.Null(sheet.Formulas[1][10]);
        Assert.Equal("Sin enlace", row[10]);
        Assert.Equal(string.Empty, row[11]);
    }

    // E6 contra Postgres: una sola lectura por lote, por pedido y en el orden de las columnas —fecha de
    // subida, y el id como desempate entre los que llegaron en el mismo request—, y sólo del tenant: la
    // tabla de comprobantes no tiene tenant y el filtro va por el join con `orders`. Un pedido sin
    // comprobantes no aparece.
    [Fact]
    public async Task PaymentProofsForTheExportComeInUploadOrderAndOnlyFromTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        using var __ = otherClient;
        var order = await CreateOrderWithProofsAsync(client, factory, tenantId, proofCount: 2);
        var withThird = await AddProofAsync(client, factory, tenantId, order.QuotationId);
        var withoutProofs = await CreateOrderAsync(client, factory, tenantId);
        var otherOrder = await CreateOrderWithProofsAsync(otherClient, factory, otherTenantId, proofCount: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var proofs = await scope.ServiceProvider.GetRequiredService<IOrderRepository>()
            .ListPaymentProofsForExportAsync(
                tenantId,
                [new OrderId(order.Id), new OrderId(withoutProofs.Id), new OrderId(otherOrder.Id)],
                TestContext.Current.CancellationToken);

        var read = Assert.Single(proofs);
        Assert.Equal(new OrderId(order.Id), read.Key);
        // Los dos de la conversión comparten la fecha de subida: los ordena el id, que Postgres
        // compara como el texto canónico del uuid.
        var sameRequest = order.PaymentProofs
            .Select(proof => proof.Id)
            .OrderBy(id => id.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var added = Assert.Single(
            withThird.PaymentProofs, proof => order.PaymentProofs.All(existing => existing.Id != proof.Id)).Id;
        Assert.Equal(sameRequest.Append(added), read.Value.Select(proof => proof.Id.Value));
    }

    /// <summary>Un pedido convertido hoy con <paramref name="proofCount"/> comprobantes (pago
    /// parcial), en un mismo request: comparten la fecha de subida.</summary>
    private static async Task<OrderResponse> CreateOrderWithProofsAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, int proofCount)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var proofs = new List<OrderPaymentProofRequest>();
        for (var index = 0; index < proofCount; index++)
        {
            proofs.Add(new OrderPaymentProofRequest(
                await CreateAvailablePaymentProofFileAsync(client, factory, tenantId), 10_000m));
        }

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PartialPaymentReceived", null, proofs),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    /// <summary>Suma un comprobante a un pedido pendiente: su fecha de subida es posterior a la de
    /// los que ya tenía.</summary>
    private static async Task<OrderResponse> AddProofAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid quotationId)
    {
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order/proofs",
            new AddOrderPaymentProofsRequest(
                "PartialPaymentReceived", [new OrderPaymentProofRequest(fileId, 5_000m)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // Directo en la base: así queda un comprobante de antes de la opción (P8), sin copia pública.
    private static async Task ClearPublicStorageKeyAsync(QepApiFactory factory, Guid proofId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderPaymentProofId(proofId);
        var updated = await dbContext.OrderPaymentProofs
            .Where(proof => proof.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(proof => proof.PublicStorageKey, (string?)null),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }

    private static async Task<string> PublicStorageKeyOfAsync(QepApiFactory factory, Guid proofId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderPaymentProofId(proofId);
        var key = await dbContext.OrderPaymentProofs
            .AsNoTracking()
            .Where(proof => proof.Id == id)
            .Select(proof => proof.PublicStorageKey)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(key);
        return key;
    }

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);
```

- [ ] **Step 3: Correrlas y verlas fallar**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore
```

Esperado (RED):
- unitarias: `error CS0246` por `OrderExportPaymentProof`, y `error CS1729: 'OrdersExportProcessor' no contiene un constructor que tome 7 argumentos` (o `does not contain a constructor that takes 7 arguments`);
- integración: `error CS1061: 'IOrderRepository' no contiene una definición para 'ListPaymentProofsForExportAsync'` (o `does not contain a definition for 'ListPaymentProofsForExportAsync'`).

Pega las líneas.

- [ ] **Step 4: El método del repositorio**

En `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs`, reemplaza:

```csharp
public sealed record OrderExportCursor(DateTimeOffset ConvertedAt, string OrderNumber);
```

por:

```csharp
public sealed record OrderExportCursor(DateTimeOffset ConvertedAt, string OrderNumber);

/// <summary>
/// Lo mínimo de un comprobante de pago que el Excel de pedidos necesita (spec 2026-09-15, E6): cuál
/// es, para el orden, y su copia pública, si tiene. Sin monto ni archivo: la hoja no los muestra.
/// </summary>
public sealed record OrderExportPaymentProof(
    OrderPaymentProofId Id, string? PublicStorageKey, DateTimeOffset UploadedAt);
```

y reemplaza el final de la interfaz:

```csharp
    void Add(Order order);
}
```

por:

```csharp
    /// <summary>Los comprobantes de un lote de pedidos del Excel (spec 2026-09-15, E6), en una sola
    /// consulta: por pedido, ordenados por fecha de subida y después por id, así «Comprobante 1» es
    /// el primero que se subió. Un pedido sin comprobantes, o de otro tenant, no aparece en el
    /// diccionario.</summary>
    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken);

    void Add(Order order);
}
```

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs`, reemplaza:

```csharp
    public void Add(Order order) => dbContext.Orders.Add(order);
}
```

por:

```csharp
    // Una consulta por lote del Excel (spec 2026-09-15, E6), no una por pedido. La tabla de
    // comprobantes no tiene tenant: el filtro va por el join con `orders`, que sí lo tiene. El orden
    // —fecha de subida, y el id como desempate entre los que llegaron en el mismo request— es el de
    // las columnas del Excel. Se agrupa en memoria, donde GroupBy conserva ese orden.
    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>();
        }

        var ids = orderIds.ToArray();
        var rows = await (
                from proof in dbContext.OrderPaymentProofs.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on proof.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                orderby proof.UploadedAt, proof.Id
                select new { proof.OrderId, proof.Id, proof.PublicStorageKey, proof.UploadedAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.OrderId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderExportPaymentProof>)group
                    .Select(row => new OrderExportPaymentProof(row.Id, row.PublicStorageKey, row.UploadedAt))
                    .ToArray());
    }

    public void Add(Order order) => dbContext.Orders.Add(order);
}
```

- [ ] **Step 5: El procesador**

Reemplaza entero `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` por:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de pedidos en el worker (D7, D8). Mismo esquema que
/// <see cref="QuotationsExportProcessor"/>: lotes de mil con keyset y el filtro del listado
/// (<see cref="OrderListing"/> y el <c>Filtered</c> de OrderRepository), streaming y subida con el id
/// del job. El lote de lectura y escritura lo comparten los dos en <see cref="ExportBatchLoop"/>.
///
/// Suma los comprobantes de pago de cada pedido (spec 2026-09-15): se leen con una consulta por lote
/// (E6), y cada uno es el enlace a su copia pública, «Sin enlace» si es privado, o una celda vacía
/// si el pedido no lo tiene (E2).
/// </summary>
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
{
    public const string SheetName = "Pedidos";

    public const string FilePrefix = "pedidos";

    /// <summary>El texto de la celda de un comprobante con copia pública (E2).</summary>
    public const string ProofLinkText = "Ver";

    /// <summary>La celda de un comprobante privado (E2): que no haya enlace no es lo mismo que no
    /// haya comprobante, y una celda vacía no puede significar las dos cosas.</summary>
    public const string PrivateProofText = "Sin enlace";

    /// <summary>
    /// Las de la tabla de pedidos en su orden (order-table.tsx: Pedido, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago o, mientras llegue vacía, la etiqueta del
    /// estado del pago. Los estados van con la etiqueta de la pantalla (spec 2026-09-13, A7).
    ///
    /// Después de Total, las cuatro de los comprobantes (spec 2026-09-15, E1): la cantidad y los tres
    /// primeros. Van al final para que las ocho de la tabla no se muevan, y salen siempre, aunque la
    /// opción de publicar esté apagada (E7): la forma del archivo no depende del ambiente.
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Pedido", 18),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Fecha", 34),
        new("Pago", 24),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
        new("Comprobantes", 14),
        new("Comprobante 1", 16),
        new("Comprobante 2", 16),
        new("Comprobante 3", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Orders;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<OrdersExportFilters>(job);
        var (status, paymentStatus) = ParseStatuses(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = await ExportBatchLoop.WriteAllAsync<OrderWithQuotation, OrderExportCursor>(
            workbook,
            (after, limit, ct) => repository.ListForExportAsync(
                job.TenantId,
                filters.ClientId,
                clientIds,
                advisorId,
                status,
                paymentStatus,
                filters.ConvertedFrom,
                filters.ConvertedTo,
                filters.OrderNumber,
                after,
                limit,
                ct),
            async (batch, ct) =>
            {
                var rows = await OrderListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, ct);
                // E6: los comprobantes del lote en una sola ida, igual que los nombres y los correos.
                var proofs = await repository.ListPaymentProofsForExportAsync(
                    job.TenantId, batch.Select(row => row.Order.Id).ToArray(), ct);
                return rows.Select(row => ToCells(row, ProofsOf(proofs, row.Id)));
            },
            row => new OrderExportCursor(row.Order.ConvertedAt, row.Order.OrderNumber),
            cancellationToken);

        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no orders matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, rowCount, upload.DownloadUrl, upload.ExpiresAt);
    }

    // El request ya validó el estado y la forma de pago; si igual no se pueden leer —el enum
    // cambió entre el pedido y el proceso—, reintentar no lo arregla.
    private static (OrderStatus? Status, OrderPaymentStatus? PaymentStatus) ParseStatuses(OrdersExportFilters filters)
    {
        try
        {
            return (OrderListing.ParseStatus(filters.Status), OrderListing.ParsePaymentStatus(filters.PaymentStatus));
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<OrderExportPaymentProof> ProofsOf(
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>> proofs, Guid orderId) =>
        proofs.TryGetValue(new OrderId(orderId), out var found) ? found : [];

    private ExportCell[] ToCells(OrderListItemDto row, IReadOnlyList<OrderExportPaymentProof> proofs) =>
    [
        ExportCell.OfText(row.OrderNumber),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, mismo criterio que cotizaciones.
        ExportCell.OfText(row.ConvertedAt.ToString("O", CultureInfo.InvariantCulture)),
        // El DTO trae los nombres de los enums, que son contrato de la API (OrderMapping.cs);
        // acá se vuelven al enum sólo para etiquetarlos.
        ExportCell.OfText(row.PaymentMethod
            ?? ExportStatusLabels.For(Enum.Parse<OrderPaymentStatus>(row.PaymentStatus))),
        ExportCell.OfText(ExportStatusLabels.For(Enum.Parse<OrderStatus>(row.Status))),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
        // La cantidad cuenta todos, también los que no tienen columna (E1).
        ExportCell.OfNumber(proofs.Count),
        ProofCell(proofs, 0),
        ProofCell(proofs, 1),
        ProofCell(proofs, 2),
    ];

    // E2: el enlace «Ver» si tiene copia pública y la opción está encendida, «Sin enlace» si no, y
    // vacía si el pedido no tiene ese comprobante.
    private ExportCell ProofCell(IReadOnlyList<OrderExportPaymentProof> proofs, int index)
    {
        if (index >= proofs.Count)
        {
            return ExportCell.OfText(string.Empty);
        }

        var url = proofs[index].PublicStorageKey is { } publicKey
            ? paymentProofPublisher.UrlFor(publicKey)
            : null;
        return url is null ? ExportCell.OfText(PrivateProofText) : ExportCell.OfLink(url, ProofLinkText);
    }
}
```

- [ ] **Step 6: Correr las pruebas (GREEN)**

Con Docker corriendo:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-restore --filter "FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~QuotationExportApiTests|FullyQualifiedName~OrderPaymentProofPublicationApiTests"
```

Esperado: la suite unitaria de Quotations completa con `Con error: 0`, con las 15 de `OrdersExportProcessorTests` y `QuotationsExportProcessorTests` sin cambios (E5). En integración, `OrderExportApiTests` entera (con las dos nuevas), `QuotationExportApiTests` (el lector ampliado no cambia lo que ya leía) y `OrderPaymentProofPublicationApiTests`, en verde. Pega la salida literal de cada corrida (como mínimo el bloque de resumen de cada proyecto con Superado/Con error/Omitido).

- [ ] **Step 7: README y producción**

En `README.md`, justo antes de la línea `### Plantilla de WhatsApp (Zenvia)`, agrega:

```markdown
### Comprobantes de pago públicos (`payment-proofs/`)

Con `Quotations:PaymentProofs:PublicLinks=true`, cada comprobante de pago **nuevo** —al convertir
una cotización en pedido o al sumarle comprobantes— se copia del bucket privado al **bucket
público** con la clave aleatoria `payment-proofs/{guid}.{pdf|jpg|png}`, y el Excel de pedidos lo
enlaza en las columnas «Comprobante 1» a «Comprobante 3», con la cantidad total en «Comprobantes».
La base guarda la clave, no la URL: la URL se arma al exportar con `Storage:R2:PublicBaseUrl`, así
que cambiar el dominio no rompe los enlaces. Los comprobantes de antes, y los que se adjunten con la
opción apagada, quedan privados y dicen «Sin enlace»: no hay backfill. Producción la enciende en
`k8s/prod-configMap.yaml`.

- **Riesgo aceptado por el owner (2026-09-14):** quien tenga la URL abre el comprobante sin sesión
  y sin revisar el tenant, y no se puede revocar si el Excel se reenvía. La clave aleatoria impide
  adivinarla; no controla quién la tiene.
- **Sobre `payment-proofs/` no va ninguna regla de lifecycle.** El prefijo `quotations/` del mismo
  bucket (los PDF que se mandan por WhatsApp) sí puede tener una; confundirlos borraría
  comprobantes cuyos enlaces siguen en Excels ya enviados.
- **Nada se despublica solo:** apagar la opción deja de publicar y de mostrar enlaces, pero las
  copias ya hechas siguen en el bucket, y borrar el archivo en Storage tampoco toca su copia.
- La copia conserva el `Content-Type` del original (`CopyObject` usa `MetadataDirective = COPY` por
  defecto), así que un PDF se abre en el navegador en vez de descargarse.
- Un Excel bajado de internet abre en **Vista protegida**, y ahí ningún enlace responde hasta que
  se toca «Habilitar edición». Es comportamiento de Office, igual para cualquier enlace.

```

En `k8s/prod-configMap.yaml`, reemplaza:

```yaml
  Quotations__WhatsApp__TemplateId: "#{QUOTATIONS_WHATSAPP_TEMPLATE_ID}#"
```

por:

```yaml
  Quotations__WhatsApp__TemplateId: "#{QUOTATIONS_WHATSAPP_TEMPLATE_ID}#"
  # Copia pública de cada comprobante de pago nuevo, para que el Excel de pedidos lo enlace (spec
  # 2026-09-15). Literal y no token: no es un secreto ni cambia por despliegue. Exige
  # Storage__R2__PublicBucket y Storage__R2__PublicBaseUrl, de arriba: sin ellos
  # PaymentProofsOptionsValidator no deja arrancar el pod. Riesgo aceptado por el owner el
  # 2026-09-14: quien tenga la URL abre el comprobante sin sesión, y no se puede revocar si el Excel
  # se reenvía.
  Quotations__PaymentProofs__PublicLinks: "true"
```

- [ ] **Step 8: Build y formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
dotnet build Backend.slnx --no-restore
$files = @(git diff --name-only HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
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

Esperado: build con `0 Advertencia(s)` y `0 Errores`, y el último bloque sin salida (o sólo líneas que no tocaste, anotadas en el handoff).

- [ ] **Step 9: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add README.md k8s/prod-configMap.yaml src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportWorkbookReader.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs; git commit -m "feat(orders): comprobantes de pago en el Excel de pedidos" -m "La hoja Pedidos suma Comprobantes y Comprobante 1 a 3 después de Total: el enlace Ver a la copia pública, Sin enlace si es privado, o vacía. Los comprobantes se leen con una consulta por lote, filtrada por tenant con un join contra orders. Producción enciende Quotations:PaymentProofs:PublicLinks en su ConfigMap."
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío y el `Select-String` sin salida.

---

### Task 7: Verificación final

La verificación de `README.md` § «Verificación» (`restore`, `format`, `build`, `test`) sobre la rama entera, comparada por nombre contra el baseline de Task 0, más los controles propios de este cambio. Si algo falla se arregla con su propio ciclo RED/GREEN y un commit `fix(orders): …`; si no, esta tarea no commitea nada.

**Files:**
- Ninguno, salvo lo que la verificación obligue a corregir.

**Interfaces:**
- Consumes: todo lo anterior y `$env:TEMP\qep-comprobantes-baseline-failed.txt` (Task 0).
- Produces: la evidencia literal para el handoff.

- [ ] **Step 1: Restore, build y modelo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Esperado: restore sin errores (con el lock file del proyecto nuevo), build con `0 Advertencia(s)` y `0 Errores`, y `No changes have been made to the model since the last migration.`

- [ ] **Step 2: Formato de la solución, filtrado a lo que tocó la rama**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$base = git merge-base develop HEAD
$root = (Get-Location).Path
$branchFiles = @(git diff --name-only $base HEAD -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' } |
    ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $root $_)) }
$report = Join-Path $env:TEMP "qep-comprobantes-format"
Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
dotnet format Backend.slnx --verify-no-changes --no-restore --report $report
$reportFile = Join-Path $report "format-report.json"
if (Test-Path $reportFile) {
    (Get-Content $reportFile -Raw | ConvertFrom-Json) |
        Where-Object { $branchFiles -contains [System.IO.Path]::GetFullPath($_.FilePath) } |
        ForEach-Object {
            $document = $_
            $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
                ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
        }
}
```

`dotnet format` corre sobre la solución entera, sin `--include`, como pide README § «Verificación»: tarda varios minutos, y su salida de consola trae el ruido de `ENDOFLINE`/`CHARSET` de todo el repo (ver Global Constraints). Lo que cuenta es el último bloque, que deja sólo los diagnósticos de los `.cs` que la rama cambió desde `$base` (el reporte trae rutas absolutas; `git diff`, relativas, y por eso se normalizan las dos). Esperado: sin salida, o sólo las líneas que ya anotaste como previas en los handoffs de las tareas. CI no corre `format`.

- [ ] **Step 3: Controles propios del cambio**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$factories = @(Get-ChildItem -Path tests -Recurse -Filter *.cs | Select-String -SimpleMatch -List -Pattern 'builder.UseSetting("Notifications:EmailProvider", "log");' | ForEach-Object { $_.Path })
$missing = @($factories | Where-Object { -not (Select-String -Path $_ -SimpleMatch -Quiet -Pattern '"Quotations:PaymentProofs:PublicLinks"') })
"Factorías: $($factories.Count); sin la clave: $($missing.Count)"; $missing
$base = git merge-base develop HEAD
git log "$base..HEAD" --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git diff --name-only $base HEAD -- src/Modules/Storage src/Modules/Quotations/Modules.Quotations.Api
git diff --stat $base HEAD -- src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs
```

Esperado:
- `Factorías: 38; sin la clave: 0`, sin ninguna ruta debajo: todas las factorías fijan la clave. En `QuotationsApiHarness` el valor sale del flag y la clave queda en la línea siguiente al `UseSetting(`, por eso se busca la clave en el archivo y no en la línea.
- el `Select-String` de `Co-Authored-By`, sin salida;
- los dos `git diff` finales, sin salida, contados desde la base (el `develop` sobre el que se rebasó la rama, así que lo que `develop` cambió en esos archivos no aparece): Storage (`FileResource.Publish`, P6), `OrderEndpoints`, `OrderPaymentProofResponse` y el Excel de cotizaciones (E5) quedaron intactos.

- [ ] **Step 4: Suite completa, por nombre contra el baseline**

Con Docker corriendo. Tarda decenas de minutos; corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$final = Join-Path $env:TEMP "qep-comprobantes-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $final
$expected = @(Get-Content (Join-Path $env:TEMP "qep-comprobantes-baseline-failed.txt"))
$actual = @(Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName }
} | Sort-Object -Unique)
"Fallas nuevas:"; $actual | Where-Object { $expected -notcontains $_ }
"Arregladas desde el baseline:"; $expected | Where-Object { $actual -notcontains $_ }
```

Esperado: **"Fallas nuevas:" vacío**. Pega los dos bloques y el total `Superado`/`Con error` de cada proyecto, incluido `Bootstrapper.UnitTests`, que CI corre por estar en `Backend.slnx`.

- [ ] **Step 5: Si algo falló**

Por cada falla nueva o diagnóstico de formato en una línea que tocó la rama:
1. Reproduce la falla con la prueba que la muestra (si es de formato, con el reporte del Step 2) y pega la salida (RED).
2. Corrige en el archivo de la tarea que la introdujo, sin cambiar el comportamiento que fija el spec.
3. Vuelve a correr esa prueba y el Step 4 entero (GREEN), y pega las salidas.
4. Revisa la lista de archivos cambiados —tienen que ser sólo los que corregiste— y commitéalos con sus rutas explícitas:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
git status --short
```

Si la lista trae algo que no corregiste, **para y pregunta**. Si no, cambia `<cada ruta que corregiste>` por esas rutas, escritas una por una y separadas por espacio; nunca el árbol entero ni una lista armada con `git diff`:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
if ((git branch --show-current) -ne "feature/comprobantes-publicos") { throw "ABORT: rama equivocada" }; git add <cada ruta que corregiste>; git commit -m "fix(orders): corregir lo que encontró la verificación final de comprobantes públicos"
git status --short
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` vacío después del commit y el `Select-String` sin salida.

Si una falla nueva no la explica este cambio (por ejemplo, una prueba de otro módulo que depende de los user-secrets de tu máquina), **para y pregunta** en vez de tocar código ajeno.

- [ ] **Step 6: Verificación manual contra R2 (la hace el developer)**

Las pruebas usan un doble del bucket público, así que dos «Contras aceptados» del spec sólo se ven contra Cloudflare. La hace el developer en un ambiente con la opción encendida, después de desplegar, y anota el resultado en el handoff.

1. Adjunta un comprobante PDF a un pedido (al convertir o con «Sumar comprobantes»), exporta el listado de pedidos y copia la URL del enlace «Ver» del Excel (clic derecho → editar hipervínculo, o la fórmula de la celda).
2. Revisa que se abre sin sesión y con el tipo del original:

```powershell
curl.exe -sI "https://<dominio-publico>/payment-proofs/<clave>.pdf"
```

Esperado: status `200` (HTTP/1.1 o HTTP/2) y `Content-Type: application/pdf`.

3. Revisa que sobre `payment-proofs/` no haya ninguna regla de lifecycle en el bucket público:

```powershell
npx wrangler r2 bucket lifecycle list <bucket-publico>
```

Esperado: ninguna regla cuyo prefijo sea `payment-proofs/` o vacío (una regla sin prefijo aplica a todo el bucket).

- [ ] **Step 7: Los commits de la rama**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\comprobantes-publicos
$base = git merge-base develop HEAD
git log --oneline "$base..HEAD"
```

Esperado, del más nuevo al más viejo y contados desde la base (el `develop` sobre el que se rebasó la rama): las seis tareas, el plan y el spec, más un `fix(orders)` arriba si el Step 5 hizo falta. Si el plan se commiteó antes de Task 0, su mensaje puede ser otro. Lo que no puede aparecer es un commit ajeno a esta rama: si aparece, tu `develop` local está detrás de la base, y **paras y preguntas**.

```
<sha> feat(orders): comprobantes de pago en el Excel de pedidos
<sha> feat(quotations): celdas de enlace en el Excel de las exportaciones
<sha> feat(orders): publicar los comprobantes nuevos al convertir y al sumarlos
<sha> feat(orders): publicador de comprobantes de pago en el bucket público
<sha> feat(orders): guardar la clave pública de cada comprobante de pago
<sha> feat(orders): opción de comprobantes de pago públicos y su validación al arrancar
<sha> docs(orders): plan de comprobantes de pago públicos
<sha> docs(orders): spec de comprobantes de pago públicos en el Excel
```

La rama no se publica ni se mergea desde este plan.

---

## Autorrevisión contra el spec

| Sección del spec | Dónde |
| --- | --- |
| P1 clave por ambiente, `false` por defecto, `"true"` literal en producción | Task 1, Steps 6 y 9; Task 6, Step 7 (ConfigMap) |
| P2 validador del Bootstrapper, en cualquier ambiente | Task 1, Steps 3-4 y 8 (`PaymentProofsOptionsValidatorTests`, `PublicLinksWithoutAPublicBucketStopsTheApiFromStarting`) |
| P3 puerto en Application, dos implementaciones, DI según la clave | Task 3, Steps 6-8 (`WithPublicLinksOff…`/`WithPublicLinksOn…`) |
| P4 sólo los comprobantes nuevos, antes del `SaveChanges` | Task 4, Steps 6-7 (`CorrectingOnlyAmountsCopiesNothing`, `AddingProofsWithPublicLinksOnCopiesTheNewProof`) |
| P5 clave `payment-proofs/{guid:N}{ext}` por `MimeType`, columna `varchar(200)` nullable sin índice, se guarda la clave | Task 2 (columna y mapeo), Task 3 (`CopiesThePrivateObject…`, `TheExtensionComesFromTheMimeType`) |
| P6 `FileResource.Publish` no se toca | Task 3 (el publicador no lo llama), Task 7, Step 3 |
| P7 copia que falla, rollback best-effort con `CancellationToken.None`, rechazo del dominio incluido | Task 4 (`PaymentProofCopiesTests`, `AConversionWhoseCopyFails…`, `AddingProofsWhoseCopyFails…`, `ADomainRejectionAfterCopying…`, `ASecondConversion…`) |
| P8 sin backfill | Global Constraints; Task 4 (`ConvertingWithPublicLinksOff…`, `AddingProofsWithPublicLinksOff…`); Task 6 simula un comprobante privado |
| E1 cuatro columnas después de Total | Task 6 (`WritesTheOrdersListColumnsInTheirOrder`, `AFourthProofOnlyShowsInTheCount`, export de punta a punta) |
| E2 «Ver», «Sin enlace» o vacía | Task 6 (`AProofWithAPublicCopy…`, `AProofWithoutAPublicCopySaysSinEnlace`, `AnOrderWithoutProofs…`) |
| E3 fórmula `HYPERLINK` con valor calculado, `t="str"`, estilo 2 azul subrayado, comillas escapadas | Task 5 (`ALinkCellIsAHyperlinkFormula…`, `QuotesInTheUrlAndTheTextAreDoubled`, validador OpenXML) |
| E4 más de 255, medidos ya escapados → texto plano con la URL | Task 5 (`AUrlOf255CharactersIsStillALink`, `AUrlLongerThan255Characters…`) |
| E5 `ExportCell.OfLink`; cotizaciones no cambia | Task 5; Task 7, Step 3 |
| E6 una consulta por lote, `UploadedAt` y después `Id`, tenant por join | Task 6 (`ReadsThePaymentProofsOncePerBatch…`, `PaymentProofsForTheExportComeInUploadOrderAndOnlyFromTheTenant`) |
| E7 las columnas salen con la opción apagada | Task 6 (`WithTheOptionOffEveryProofSaysSinEnlace`, export de punta a punta en la factoría por defecto) |
| Dominio: `PublicStorageKey` fijado en `Create`, inmutable, acepta null | Task 2 (`OrderTests`) |
| Pruebas unitarias del Bootstrapper | Task 1 crea el proyecto (hallazgo 1); Tasks 1 y 3 |
| Integración con doble de `IPublicObjectStorage` | Task 3 (harness), Tasks 4 y 6 |
| Toda factoría fija `Quotations:PaymentProofs:PublicLinks` | Task 1, Steps 11-12; Task 7, Step 3 |
| Documentación: README y guía de integración | Task 1 (clave, validadores), Task 6 (sección `payment-proofs/`), Task 4 (guía, hallazgo 11) |
| Contras aceptados: borrado en Storage, lifecycle, `Content-Type` | Comentario de `PublicPaymentProofPublisher` (Task 3), README (Task 6), verificación manual (Task 7, Step 6) |
| «Vista protegida» | README (Task 6) |
| Fuera de alcance | Global Constraints; ninguna tarea los implementa |

