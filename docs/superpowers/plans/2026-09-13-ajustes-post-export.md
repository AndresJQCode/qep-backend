# Ajustes posteriores a la exportación asíncrona — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cerrar los cinco pendientes de la exportación asíncrona: un solo correo por mensaje aunque haya dos procesos, workers de correo que sobreviven a un timeout de Infobip, el Excel con los estados en español, la carga sintética para medir un año de exportación en producción, y el voseo de los errores del listado de ventas (`describeSalesFailure`).

**Architecture:** Los cinco workers de Notifications pasan a una base abstracta, `OutboxDeliveryWorker`, que reclama cada mensaje en `notifications.inbox_messages` con un `INSERT … ON CONFLICT … DO UPDATE … RETURNING attempts` (lease de 2 min, tope de 3 intentos) antes de enviar, aísla cada mensaje en su propio `try` y sólo sale del loop si el proceso se apaga. Las etiquetas del Excel salen de un mapa en `Modules.Quotations.Application`. La carga sintética es un `BackgroundService` de Bootstrapper que, después del arranque, crea el tenant `carga-export` por el dominio y siembra clientes, cotizaciones y ventas con SQL masivo en una transacción.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql 10, xUnit v3, Testcontainers (Docker corriendo para las pruebas de integración), PostgreSQL 18. Frontend: React 19, Vitest, bun.

**Spec:** [`docs/superpowers/specs/2026-09-13-ajustes-post-export-design.md`](../specs/2026-09-13-ajustes-post-export-design.md). Es la autoridad y la tienes que leer antes de la primera tarea. Donde el spec deja un detalle abierto, este plan lo decide desde el código y lo dice en «Decisiones que tomó el plan».

## Global Constraints

- **Worktree:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\ajustes-post-export`, rama `feature/ajustes-post-export`. Todas las rutas del backend son relativas a esa carpeta.
- **Guard de rama en cada commit.** Todo `git add` y `git commit` va en un bloque de **Git Bash** que empieza con `test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }`. El bloque se corre entero, en una sola invocación. Agregas rutas explícitas, nunca `git add -A` ni `git add .`.
- **Commits:** conventional commits en español, **sin trailer `Co-Authored-By` ni atribución de IA**. Si tu entorno te sugiere el trailer, ignóralo. Después de cada commit, `git log -1 --format=%B | grep -ci "co-authored-by"` tiene que dar `0`; si da otra cosa, corriges con `git commit --amend` antes de seguir.
- **TDD con evidencia:** RED antes que GREEN. En el handoff pegas la salida literal de ambos (las líneas del fallo y el resumen `Passed!`/`Failed!`).
- **Pruebas en primer plano.** Nunca las lances en background para esperarlas.
- **`Api.exe` no se detiene nunca.** Si `Get-Process -Name Api -ErrorAction SilentlyContinue` lo muestra y un `build`/`test`/`ef` falla por archivo bloqueado, paras y le pides al developer que lo cierre.
- **Formato:** sólo `dotnet format Backend.slnx --verify-no-changes --include <archivos tocados>`. Nunca un `dotnet format` que modifique, y nunca sin `--include`: sobre todo el repo marca ruido de CRLF que no es tuyo. Los archivos de migración generados no van en el `--include`.
- **Paquetes:** este plan no agrega ninguno. `dotnet restore --locked-mode` tiene que pasar en cada commit. Si igual cambiara un `packages.lock.json`, lo regeneras con `dotnet restore --force-evaluate` y lo commiteas junto con el `Directory.Packages.props`.
- **Migraciones:** con el factory de diseño, nunca con `--startup-project`: `dotnet ef migrations add <Nombre> --project src/Modules/Notifications/Modules.Notifications.Infrastructure --context NotificationsDbContext -o Persistence/Migrations`.
- **Regresión por nombre.** La suite completa se compara contra `%TEMP%\qep-ajustes-post-export-expected-failed.txt`, que Task 0 arma desde `%TEMP%\qep-export-asincrono-baseline-failed.txt` sin `ArchitectureTests.CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered`: son **16** fallas conocidas (9 de Reporting y 7 de `Customers.IntegrationTests.CustomerStatusAndImportApiTests`). Los `.trx` se leen con `Get-ChildItem -LiteralPath` y `Get-Content -LiteralPath`, porque los nombres llevan `[N]`.
- **Comandos para el developer:** en PowerShell (`curl.exe`, `$env:VAR = "..."`, `A; if ($?) { B }`). `kubectl` siempre con `--context contabo-prod`: el contexto activo de esa máquina es un EKS ajeno.
- **Secretos:** nunca imprimes un valor. Nada de `kubectl get secret -o yaml/json`; para ver claves, `kubectl describe secret`.
- **Idioma:** la prosa y los comentarios de código tutean en español colombiano (tienes, corre, revisa), nunca voseo. Los identificadores van en inglés. El copy de UI, en español neutro tuteando.
- **Constantes del reclamo (spec, Sección 1):** lease `2 min`, máximo de intentos `3` (un reclamo que devuelve `attempts > 3` es envenenado), lote `20`, tick `3 s`. El `HttpClient` de Infobip corta a los `30 s`.
- **Etiquetas del Excel (spec, Sección 2):** `Draft`→`Borrador`, `Sent`→`Enviada`, `Voided`→`Anulada`, `Expired`→`Vencida`, `Pending`→`Pendiente`, `Approved`→`Aprobada`, `FullPaymentReceived`→`Pago total`, `PartialPaymentReceived`→`Pago parcial`, `PaymentPending`→`Pago pendiente`. La API sigue mandando el nombre del enum.
- **Carga sintética:** `Seed:ExportLoad:Quotations` (int, `0` la apaga y es el default); tenant `carga-export` con id `01900000-0000-7000-8000-000000000004`; nunca dentro de `RunQepSeedAsync`; idempotente si el tenant ya tiene cotizaciones.

---

## Decisiones que tomó el plan

Todas se verificaron contra el código de `9f869ff` (= `origin/develop` `037718a` + el commit del spec).

1. **Los workers se registran por tipo.** Hoy van con factorías (`NotificationsInfrastructureExtensions.cs:43-66`), así que el descriptor no lleva `ImplementationType` y el harness de pruebas no tiene cómo sacarlos. Pasan a `AddHostedService<T>()`, igual que `ExportJobWorker` (`QuotationsInfrastructureExtensions.cs:44`) y `OutboxPublisherWorker` (`TenancyInfrastructureExtensions.cs:46`). El harness los saca con el mismo criterio que `runExportWorker` (`QuotationsApiHarness.cs:708-718`).
2. **La costura de pruebas es `internal Task DrainAsync(CancellationToken)`** en la base, con `InternalsVisibleTo` hacia `Modules.Notifications.IntegrationTests`. Es el precedente de `ExportJobWorker.cs:62`.
3. **El render sigue dentro del `try` del envío.** La base expone `SendAsync(context, notification, Func<EmailMessage> render, stoppingToken)`: así una excepción al armar el correo sigue terminando en `Failed`, como hoy (`InvitationDeliveryWorker.cs:105-116`), y el filtro nuevo del `catch` vive en un solo lugar.
4. **El mensaje envenenado se registra con `tenant_id` y `recipient_id` en `Guid.Empty`** y `failure_reason = "delivery_attempts_exhausted"`. Lo que está roto puede ser justamente el payload, así que la base no lo lee. `notifications.notifications` no tiene FK, y ninguna consulta por tenant va a ver esa fila.
5. **`HttpClient` sin `IHttpClientFactory`.** El repo no lo usa en ningún lado: `QuotationsInfrastructureExtensions.cs:97-99` y `QCodePdfRenderer.cs:18` lo dejan escrito. `InfobipEmailChannel.CreateHttpClient()` devuelve un `new HttpClient { Timeout = 30 s }`. No entra ningún paquete ni cambia ningún lock file.
6. **El mapa de etiquetas va por enum.** Los procesadores reciben el DTO con el nombre del enum en texto (`QuotationMapping.cs:76`, `SaleMapping.cs:35-36`) y lo vuelven a enum con `Enum.Parse<T>` para etiquetarlo. El `switch` tira ante un valor sin etiqueta, y la prueba recorre `Enum.GetValues`.
7. **Las columnas de la carga sintética** salen de los mapeos: `QuotationsDbContext.cs:46-441`, `CustomersDbContext.cs:28-252`, `CatalogDbContext.cs:33-129`. Estas son las decisiones:
   - **Clientes:** 1 por cada 25 cotizaciones, clasificación `CLI`, NIT `800000000 + n`, sin retención ni excedente de IVA. La dirección principal apunta a la primera ciudad por `divipola_code`, y el CUC lleva el departamento de esa ciudad.
   - **Cotizaciones:**
     - Fechas repartidas en los últimos 364 días, con `valid_until = creación + 15 días`: `Sent` si sigue vigente y `Expired` si no.
     - `sent_at` = creación + 1 h, y `updated_at = sent_at`.
     - Tres líneas con productos del catálogo del tenant, al precio `price_base_cop` y con el IVA de su tasa.
     - Moneda `COP` y una cuenta de cobro completa.
     - `payment_method`: `Transferencia` en las pares y `NULL` en las impares.
   - **Ventas:** el 30 % (`n % 10 < 3`) queda convertido un día después del envío, en `Pending`/`PaymentPending`.
   - **Contadores:** los tres quedan en el siguiente al último sembrado.
8. **`billing_company_id` es un GUID fijo sintético.** Fuera del mapeo no lo lee nadie: `rg "BillingAccount\??\.CompanyId|billing_company_id" src` sólo encuentra `QuotationsDbContext.cs:128`. Sin cuenta de cobro, en cambio, el listado mostraría las cotizaciones como incompletas y las ventas no tendrían de dónde salir.
9. **La membresía de la carga usa el origen `seed-export-load`.** No es `registration`, porque no es la del dueño de un registro. Para crearla, `TenancySeeder` gana dos métodos parametrizados por tenant, y los dos que ya existen delegan en ellos.
10. **El SQL masivo corre en una `NpgsqlConnection` propia**, con la cadena `QepDatabase`. Toca cuatro esquemas, y en Bootstrapper Npgsql ya llega transitivo (`src/Bootstrapper/packages.lock.json:108`). Los números de cotización y venta se rellenan con `lpad(n, greatest(4, length(n)), '0')`: `lpad` a secas **trunca** los que pasan de 4 dígitos, y con 50 000 cotizaciones hay más de 9 999 en un año.
11. **Hay que loguear la duración del job.** El spec pide leer «duración, filas y tamaño» en los logs del job, pero `ExportJobWorker` sólo loguea fallas (`ExportJobWorker.cs:23-31`). El plan agrega una línea `Information` con el resultado y los milisegundos de cada job.
    - Las filas ya llegan en el correo y el tamaño se lee del archivo descargado.
    - No se toca `ExportJobRunner`: `Modules.Quotations.Application` no referencia logging, y agregarlo movería lock files.
12. **La limpieza va después de apagar.** El spec dice limpiar y después apagar el interruptor. El plan invierte el orden: con el interruptor prendido, cualquier reinicio del pod entre los dos pasos vuelve a sembrar un tenant que quedó vacío.
13. **El harness de Quotations fija `Seed:ExportLoad:Quotations = "0"`.** Un número en los user-secrets de quien corre las pruebas haría sembrar cada host del proyecto. Es el mismo criterio que `Notifications:EmailProvider` en `QuotationsApiHarness.cs:672-675`.
14. **Frontend: la función `describeSalesFailure` entera.** Tiene cinco textos con voseo (`sales.api.ts:112,117,121,136,140`), y A9 nombra la función, no una línea. Pasan los cinco a tuteo, junto con las dos pruebas que verifican esos textos: `sales-list-page.test.tsx:239` y `sale-detail-page.test.tsx:164,179`. El voseo del resto del frontend queda para el barrido aparte.

## Entrega

| Commit | Repo | Tareas |
| --- | --- | --- |
| `fix(notifications): un solo envío por mensaje y timeout del proveedor sin matar el worker` | backend | 1–6 |
| `feat(quotations): el Excel muestra los estados en español` | backend | 7–8 |
| `feat(seed): carga sintética para medir la exportación` | backend | 9–13 |
| `docs(quotations): la medición de la exportación de un año` | backend | 14, **después de medir en producción** |
| `fix(sales): tuteo en los errores del listado de ventas` | frontend | 15 |

Las tareas intermedias de cada grupo terminan en **stage**, con guard y rutas explícitas. La última del grupo commitea.

---

## File Structure

**Backend — crear**

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs` | El reclamo: una sentencia que devuelve los intentos o `null` |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/<ts>_AddInboxClaims.cs` (+ `.Designer.cs`) | `processed_at` nullable, `claimed_until`, `attempts` |
| `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs` | Base: loop, candidatos, reclamo, aislamiento, cancelación, envenenado; `DeliveryContext` |
| `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs` | Etiqueta en español de cada estado para el Excel |
| `src/Bootstrapper/Seeding/ExportLoadSeedOptions.cs` | `Seed:ExportLoad:Quotations` |
| `src/Bootstrapper/Seeding/ExportLoadSeeder.cs` | Tenant, dueño, catálogo y el SQL masivo; `ExportLoadSeedResult` |
| `src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs` | `BackgroundService` que siembra después del arranque |
| `ops/export-load-cleanup.sql` | Borra sólo el tenant `carga-export` |

**Backend — modificar**

| Archivo | Cambio |
| --- | --- |
| `…/Notifications.Infrastructure/Persistence/NotificationInboxMessage.cs:3-12` | Columnas del reclamo |
| `…/Notifications.Infrastructure/Persistence/NotificationsDbContext.cs:55-63` | Mapeo del reclamo |
| `…/Notifications.Infrastructure/Persistence/Migrations/NotificationsDbContextModelSnapshot.cs` | Regenerado |
| `…/Notifications.Infrastructure/Messaging/*DeliveryWorker.cs` (los cinco) | Sobre la base |
| `…/Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs:43-84` | Registro por tipo; `HttpClient` con timeout |
| `…/Notifications.Infrastructure/Channels/InfobipEmailChannel.cs:19-24` | `RequestTimeout`, `CreateHttpClient()` |
| `…/Notifications.Infrastructure/Modules.Notifications.Infrastructure.csproj:3-5` | `InternalsVisibleTo` a IntegrationTests |
| `…/Quotations.Application/QuotationsExportProcessor.cs:103-114` | Estado etiquetado |
| `…/Quotations.Application/SalesExportProcessor.cs:26-31,106-118` | Estado y Pago etiquetados |
| `…/Quotations.Infrastructure/Exports/ExportJobWorker.cs:23-80` | Log de duración por job |
| `src/Bootstrapper/Seeding/SeedOptions.cs`, `SeedOptionsValidator.cs` | `ExportLoad` y su validación |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs:468-474` | Registro del worker de la carga |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs:24-81` | Tenant y membresía parametrizados |
| `src/Api/appsettings.example.json:31-34` | `Seed:ExportLoad:Quotations` |
| `k8s/prod-configMap.yaml:67-71` | `Seed__ExportLoad__Quotations: "0"` |
| `README.md:295-337` | Clave y procedimiento de medición/limpieza |
| `docs/superpowers/specs/2026-09-12-export-asincrono-design.md` | Línea en D8 (commit 2) y riesgo de memoria (commit 4) |

**Backend — pruebas**

| Archivo | Qué |
| --- | --- |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs` | Crear: columnas del reclamo |
| `tests/Modules/Notifications/Modules.Notifications.UnitTests/InfobipEmailChannelTests.cs` | Crear: timeout y lease |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs` | Crear: base, factory con interruptor, canal que graba, helpers SQL |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InboxClaimsTests.cs` | Crear: ganar, perder, retomar, procesado, concurrente |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs` | Crear: el interruptor |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs` | Crear: duplicados, timeout, lease, envenenado, aislamiento |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs` | Crear: los cinco eventos y los dos `Failed` sin envío |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs` | Crear |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs:39`, `SalesExportProcessorTests.cs:37-39` | Etiquetas |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs:227`, `SaleExportApiTests.cs:170-171` | Etiquetas |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:675` | Fija `Seed:ExportLoad:Quotations` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs` | Crear: arranque, siembra, listados, exports, contadores, worker, limpieza |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RecordingLogger.cs` | Crear |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs` | Log de duración |

**No se tocan, a propósito:** `InvitationNotificationTests.cs` y `QuotationsExportNotificationTests.cs`, que tienen que seguir verdes sin cambios. Tampoco los workers de Tenancy, Identity y Audit, que el spec deja fuera de alcance, ni `ExportJobRunner`.

---

### Task 0: Rama, worktree y baseline

**Files:** ninguno.

**Interfaces:**
- Consumes: nada.
- Produces: `%TEMP%\qep-ajustes-post-export-expected-failed.txt` con las 16 fallas esperadas.

- [ ] **Step 1: Comprobar el worktree**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\ajustes-post-export
git branch --show-current
git status --short
git log --oneline -2
git rev-list --left-right --count origin/develop...HEAD
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
```

Esperado:
- la rama es `feature/ajustes-post-export`;
- `git status` muestra a lo sumo este plan sin trackear;
- `9f869ff` arriba de `037718a`, y el `rev-list` en `0 1`;
- ningún proceso `Api`;
- una versión de Docker.

Si la rama es otra, el primer número del `rev-list` es mayor que 0 o hay un `Api` corriendo, **paras y le preguntas al developer**.

- [ ] **Step 2: Restaurar y compilar**

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
```

Esperado: `Build succeeded.` con `0 Error(s)`.

- [ ] **Step 3: Armar el baseline por nombre**

```powershell
$source = Join-Path $env:TEMP "qep-export-asincrono-baseline-failed.txt"
Test-Path -LiteralPath $source
$expected = Get-Content -LiteralPath $source |
    Where-Object { $_ -ne "ArchitectureTests.CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered" }
$expected | Set-Content -Encoding utf8 -LiteralPath (Join-Path $env:TEMP "qep-ajustes-post-export-expected-failed.txt")
$expected.Count
$expected
```

Esperado: `True` y `16`, con 9 nombres de `Modules.Reporting.IntegrationTests` y 7 de `CustomerStatusAndImportApiTests`. Si el archivo no existe o el conteo no es 16, **paras y preguntas**: sin baseline no hay forma de medir regresión por nombre.

---

## Commit 1 — `fix(notifications): un solo envío por mensaje y timeout del proveedor sin matar el worker`

### Task 1: El inbox gana las columnas del reclamo

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationInboxMessage.cs:3-12`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationsDbContext.cs:55-63`
- Create (generado): `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/<timestamp>_AddInboxClaims.cs` y `.Designer.cs`
- Modify (generado): `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/NotificationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `NotificationInboxMessage` con `string Consumer { get; init; }`, `Guid MessageId { get; init; }`, `DateTimeOffset? ProcessedAt { get; set; }`, `DateTimeOffset? ClaimedUntil { get; set; }` e `int Attempts { get; set; }`.
  - Columnas de `notifications.inbox_messages`: `consumer`, `message_id`, `processed_at` (nullable), `claimed_until timestamptz null` y `attempts integer not null default 1`.

Los workers de hoy siguen escribiendo su fila de inbox con EF después de este cambio: `ProcessedAt = clock.UtcNow` sigue compilando contra `DateTimeOffset?`, y `Attempts` en `0` es el centinela de EF, así que omite la columna y la base pone el `1`.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// El inbox con reclamo (spec 2026-09-13, Sección 1), contra el modelo de EF y no contra una base.
/// El reclamo es SQL crudo (InboxClaims) que nombra las columnas a mano: un nombre que EF pusiera por
/// convención rompería el reclamo sin que el compilador lo vea. Mismo criterio que
/// QuotationsDbContextMappingTests.
/// </summary>
public sealed class NotificationsDbContextMappingTests
{
    [Fact]
    public void TheInboxCarriesTheClaimColumns()
    {
        // El factory de diseño arma el contexto sin abrir conexión: construir el modelo no la necesita.
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var inbox = model.FindEntityType(typeof(NotificationInboxMessage));

        Assert.NotNull(inbox);
        Assert.Equal("inbox_messages", inbox.GetTableName());
        Assert.Equal("notifications", inbox.GetSchema());
        Assert.Equal(
            ["attempts", "claimed_until", "consumer", "message_id", "processed_at"],
            inbox.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
    }

    // processed_at en null es "reclamado y sin terminar". attempts nace en 1 para que las filas que ya
    // existen —todas procesadas— queden con un intento sin que la migración tenga que tocarlas.
    [Fact]
    public void ProcessedAtAndTheLeaseAreNullableAndAttemptsDefaultsToOne()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var inbox = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(NotificationInboxMessage))!;

        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ProcessedAt))!.IsNullable);
        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ClaimedUntil))!.IsNullable);
        var attempts = inbox.FindProperty(nameof(NotificationInboxMessage.Attempts))!;
        Assert.False(attempts.IsNullable);
        Assert.Equal(1, attempts.GetDefaultValue());
    }
}
```

- [ ] **Step 2: Correrla y verla fallar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~NotificationsDbContextMappingTests"`

Esperado: el build falla con `error CS0117: 'NotificationInboxMessage' does not contain a definition for 'ClaimedUntil'` (y lo mismo para `Attempts`).

- [ ] **Step 3: Implementar la entidad y el mapeo**

En `NotificationInboxMessage.cs`, reemplaza el comentario y la clase `NotificationInboxMessage` (líneas 3-12). `OutboxRecord` queda igual:

```csharp
// Guarda de idempotencia y reclamo por consumidor: una fila por (consumidor, id de mensaje de
// outbox). Esa es la PK, y por eso hay un solo ganador. Mientras ProcessedAt es null, el mensaje está
// reclamado y sin terminar: ClaimedUntil es el lease, y cuando vence otro tick puede retomarlo
// (InboxClaims). Attempts cuenta cuántas veces se reclamó, para cortar un mensaje envenenado.
internal sealed class NotificationInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}
```

En `NotificationsDbContext.cs`, reemplaza `ConfigureInbox` (líneas 55-63):

```csharp
    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<NotificationInboxMessage>();
        inbox.ToTable("inbox_messages", "notifications");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque InboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        // Default 1 en la base: las filas que ya existen quedan con un intento, sin tocarlas.
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }
```

- [ ] **Step 4: Correr la prueba y verla pasar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~NotificationsDbContextMappingTests"`

Esperado: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 5: Generar la migración y revisarla**

```powershell
dotnet ef migrations add AddInboxClaims --project src/Modules/Notifications/Modules.Notifications.Infrastructure --context NotificationsDbContext -o Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/Modules/Notifications/Modules.Notifications.Infrastructure --context NotificationsDbContext
```

Esperado: se crean `<timestamp>_AddInboxClaims.cs` y su `.Designer.cs`, y el segundo comando dice `No changes have been made to the model since the last migration.` El `Up` generado tiene que contener exactamente estos tres cambios, sin tocar `notifications.notifications`:

```csharp
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "processed_at",
                schema: "notifications",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "notifications",
                table: "inbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_until",
                schema: "notifications",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true);
```

Si aparece otra operación, el modelo cambió en algo que no es esta tarea: **paras**.

- [ ] **Step 6: Los workers de hoy siguen escribiendo su inbox**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests`

Esperado: `Passed!  - Failed: 0, Passed: 3`. Son `InvitationNotificationTests` y las dos de `QuotationsExportNotificationTests`, sin cambios: la base arranca con la migración nueva y el `INSERT` de EF omite `attempts`.

- [ ] **Step 7: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationInboxMessage.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationsDbContext.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationInboxMessage.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationsDbContext.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/*_AddInboxClaims.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/*_AddInboxClaims.Designer.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/Migrations/NotificationsDbContextModelSnapshot.cs \
  tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs
git status --short
```

---

### Task 2: El reclamo en SQL — `InboxClaims`

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Modules.Notifications.Infrastructure.csproj:3-5`
- Create: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InboxClaimsTests.cs`

**Interfaces:**
- Consumes: las columnas de Task 1.
- Produces:
  - `internal static class InboxClaims` con `static Task<int?> TryClaimAsync(NotificationsDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)`, que devuelve los intentos contando éste, o `null`.
  - En el harness de pruebas:
    - `NotificationsDeliveryHarness.StartDatabaseAsync()`;
    - `FindInboxAsync(string connectionString, string consumer, Guid messageId) → Task<InboxRow?>`;
    - `PutInboxAsync(string connectionString, string consumer, Guid messageId, DateTimeOffset? processedAt, DateTimeOffset? claimedUntil, int attempts)`;
    - `record InboxRow(DateTimeOffset? ProcessedAt, DateTimeOffset? ClaimedUntil, int Attempts)`;
    - `sealed class NotificationsApiFactory(string connectionString)`, que Task 3 amplía.

- [ ] **Step 1: Crear el harness**

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas del reclamo y de los workers de correo (spec 2026-09-13,
/// Sección 1). Los dos archivos que ya existían —InvitationNotificationTests y
/// QuotationsExportNotificationTests— conservan su factoría propia a propósito: el spec pide que
/// sigan verdes sin cambios.
/// </summary>
internal static class NotificationsDeliveryHarness
{
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

    public static async Task<InboxRow?> FindInboxAsync(string connectionString, string consumer, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT processed_at, claimed_until, attempts FROM notifications.inbox_messages
            WHERE consumer = @consumer AND message_id = @messageId
            """,
            connection);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new InboxRow(
            reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2));
    }

    /// <summary>Deja la fila del inbox como la habría dejado un worker anterior: por ejemplo, uno que
    /// reclamó y murió antes de guardar.</summary>
    public static async Task PutInboxAsync(
        string connectionString,
        string consumer,
        Guid messageId,
        DateTimeOffset? processedAt,
        DateTimeOffset? claimedUntil,
        int attempts)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.inbox_messages (consumer, message_id, processed_at, claimed_until, attempts)
            VALUES (@consumer, @messageId, @processedAt, @claimedUntil, @attempts)
            ON CONFLICT (consumer, message_id) DO UPDATE
                SET processed_at = EXCLUDED.processed_at,
                    claimed_until = EXCLUDED.claimed_until,
                    attempts = EXCLUDED.attempts
            """,
            connection);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("messageId", messageId);
        command.Parameters.Add(new NpgsqlParameter("processedAt", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)processedAt ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("claimedUntil", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)claimedUntil ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("attempts", attempts);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

internal sealed record InboxRow(DateTimeOffset? ProcessedAt, DateTimeOffset? ClaimedUntil, int Attempts);

internal sealed class NotificationsApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        // Fijado, nunca heredado: con "infobip" y sus claves ausentes, el validador de Notifications
        // falla al arrancar y todas las pruebas del archivo mueren antes de su aserción.
        builder.UseSetting("Notifications:EmailProvider", "log");
    }
}
```

- [ ] **Step 2: Escribir las pruebas que fallan**

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InboxClaimsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Infrastructure.Persistence;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El reclamo por mensaje (spec 2026-09-13, A1) contra Postgres de verdad. Lo que se prueba es la
/// sentencia: la PK da un solo ganador, y el lease decide cuándo se puede retomar un mensaje.
/// No hace falta una fila en el outbox, porque el inbox no tiene FK hacia él.
/// </summary>
public sealed class InboxClaimsTests
{
    private const string Consumer = "notifications.test-consumer";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFirstClaimWinsWithOneAttemptAndALease()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();

        Assert.Equal(1, await ClaimAsync(factory, messageId, Now));

        Assert.Equal(
            new InboxRow(null, Now + Lease, 1),
            await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    [Fact]
    public async Task WhileTheLeaseIsAliveASecondClaimGetsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        await ClaimAsync(factory, messageId, Now);

        Assert.Null(await ClaimAsync(factory, messageId, Now.AddMinutes(1)));

        Assert.Equal(1, (await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))?.Attempts);
    }

    // Un worker que reclamó y murió: vencido el lease, otro lo retoma y el intento cuenta.
    [Fact]
    public async Task AClaimWithTheLeaseExpiredIsRetakenAndCountsTheAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        await ClaimAsync(factory, messageId, Now);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        Assert.Equal(2, await ClaimAsync(factory, messageId, later));

        Assert.Equal(
            new InboxRow(null, later + Lease, 2),
            await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
    }

    [Fact]
    public async Task AProcessedMessageIsNeverClaimedAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: Now, claimedUntil: Now + Lease, attempts: 1);

        Assert.Null(await ClaimAsync(factory, messageId, Now.AddDays(1)));
    }

    // Dos réplicas al mismo instante: la segunda espera el commit de la primera y el ON CONFLICT ve
    // un lease vivo.
    [Fact]
    public async Task TwoConcurrentClaimsHaveExactlyOneWinner()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());
        var messageId = Guid.CreateVersion7();

        var results = await Task.WhenAll(ClaimAsync(factory, messageId, Now), ClaimAsync(factory, messageId, Now));

        Assert.Equal(1, results.Count(result => result == 1));
        Assert.Equal(1, results.Count(result => result is null));
    }

    private static async Task<int?> ClaimAsync(NotificationsApiFactory factory, Guid messageId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        return await InboxClaims.TryClaimAsync(
            dbContext, Consumer, messageId, now, Lease, TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 3: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InboxClaimsTests"`

Esperado: el build falla con `error CS0103: The name 'InboxClaims' does not exist in the current context`.

- [ ] **Step 4: Implementar el reclamo y abrir los internals**

En `Modules.Notifications.Infrastructure.csproj`, reemplaza el primer `ItemGroup` (líneas 3-5):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Notifications.UnitTests" />
    <!-- El reclamo, la base de los workers y su DrainAsync se prueban contra Postgres
         (spec 2026-09-13, Sección 1). Mismo precedente que Quotations con ExportJobWorker. -->
    <InternalsVisibleTo Include="Modules.Notifications.IntegrationTests" />
  </ItemGroup>
```

`src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// El reclamo de un mensaje del outbox por un consumidor (spec 2026-09-13, A1). Es una sola
/// sentencia, fuera de toda transacción explícita: así se commitea sola, y el lease ya se ve desde
/// otros procesos antes de que este mande el correo. Mismo criterio que
/// ExportJobQueue.ClaimNextAsync.
///
/// La PK (consumer, message_id) garantiza un solo ganador. El INSERT gana si no hay fila. El
/// ON CONFLICT sólo actualiza —y sólo entonces devuelve algo— si la fila existente no está procesada
/// y su lease venció. En cualquier otro caso no devuelve nada: otra réplica lo tiene, o ya se terminó.
/// </summary>
internal static class InboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        NotificationsDbContext dbContext,
        string consumer,
        Guid messageId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(lease);
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO notifications.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {leaseUntil}, 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {leaseUntil},
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres lo
            // rechaza para un INSERT ... RETURNING. Mismo criterio que CucGenerator.NextBatchAsync.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
```

- [ ] **Step 5: Correrlas y verlas pasar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InboxClaimsTests"`

Esperado: `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 6: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InboxClaimsTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/InboxClaims.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Modules.Notifications.Infrastructure.csproj \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InboxClaimsTests.cs
git status --short
```

---

### Task 3: El interruptor del harness y el registro por tipo

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs:1-9,43-66`
- Modify: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs`, que reemplaza la factoría de Task 2 y suma los helpers
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs`

**Interfaces:**
- Consumes: `NotificationsDeliveryHarness` de Task 2.
- Produces:
  - `NotificationsApiFactory(string connectionString, IEmailChannel? emailChannel = null, bool runDeliveryWorkers = false)`.
  - `NotificationsDeliveryHarness.IsDeliveryWorker(Type? type) → bool`.
  - `RegisterOwnerAsync(NotificationsApiFactory factory) → Task<(Guid TenantId, Guid OwnerUserId, string Email)>`.
  - `InsertOutboxAsync(string connectionString, string eventName, string payload, DateTimeOffset? occurredAt = null) → Task<Guid>`.
  - `NotificationsForAsync(string connectionString, Guid recipientId, string templateRef) → Task<IReadOnlyList<NotificationRow>>`.
  - `WaitForNotificationAsync(string connectionString, Guid recipientId, string templateRef) → Task<NotificationRow?>`, con un plazo de 30 s.
  - `record NotificationRow(string Status, string RecipientAddress, string? FailureReason)`.
  - `sealed class RecordingEmailChannel : IEmailChannel`, con `Func<int, Task>? OnSend` e `IReadOnlyCollection<EmailMessage> Sent`.

- [ ] **Step 1: Ampliar el harness**

En `NotificationsDeliveryHarness.cs`:

1. Reemplaza el bloque de `using` por:

```csharp
using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
```

2. Dentro de `NotificationsDeliveryHarness`, después de `PutInboxAsync`, agrega:

```csharp
    // Los cinco por nombre hasta que Task 5 los pase a todos a OutboxDeliveryWorker; ahí esta lista
    // se reemplaza por la base.
    private static readonly Type[] DeliveryWorkerTypes =
    [
        typeof(InvitationDeliveryWorker),
        typeof(CustomerExportDeliveryWorker),
        typeof(ProductExportDeliveryWorker),
        typeof(QuotationsExportReadyDeliveryWorker),
        typeof(QuotationsExportFailedDeliveryWorker),
    ];

    public static bool IsDeliveryWorker(Type? type) => type is not null && DeliveryWorkerTypes.Contains(type);

    // register-tenant es la única forma de tener un usuario con correo en identity.users sin la vuelta
    // de Google: el stub toma el correo del header X-Email. Mismo mecanismo que
    // QuotationsExportNotificationTests.RegisterOwnerAsync.
    public static async Task<(Guid TenantId, Guid OwnerUserId, string Email)> RegisterOwnerAsync(
        NotificationsApiFactory factory)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Notifications Delivery Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisteredTenantDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId, email);
    }

    /// <summary>El evento se escribe directo en el outbox, como lo deja el módulo productor.</summary>
    public static async Task<Guid> InsertOutboxAsync(
        string connectionString, string eventName, string payload, DateTimeOffset? occurredAt = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, CAST(@payload AS jsonb), @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("eventName", eventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("occurredAt", occurredAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task<IReadOnlyList<NotificationRow>> NotificationsForAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT status, recipient_address, failure_reason FROM notifications.notifications
            WHERE recipient_id = @recipientId AND template_ref = @templateRef
            ORDER BY created_at
            """,
            connection);
        command.Parameters.AddWithValue("recipientId", recipientId);
        command.Parameters.AddWithValue("templateRef", templateRef);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<NotificationRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new NotificationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    // Sondeo con plazo, igual que InvitationNotificationTests: el tick es del worker, no de la prueba.
    public static async Task<NotificationRow?> WaitForNotificationAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await NotificationsForAsync(connectionString, recipientId, templateRef);
            if (rows.Count > 0)
            {
                return rows[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private sealed record RegisteredTenantDto(Guid TenantId, Guid OwnerUserId);
```

3. Debajo de `InboxRow`, agrega el record y el canal:

```csharp
internal sealed record NotificationRow(string Status, string RecipientAddress, string? FailureReason);

/// <summary>El canal de correo de las pruebas: cuenta los envíos y puede demorarse o fallar a pedido.</summary>
internal sealed class RecordingEmailChannel : IEmailChannel
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private int _calls;

    /// <summary>Lo que pasa en cada envío antes de contarlo: esperar para abrir una carrera, o
    /// fallar. Recibe el número de llamada, empezando en 1.</summary>
    public Func<int, Task>? OnSend { get; init; }

    /// <summary>Sólo los envíos que terminaron bien.</summary>
    public IReadOnlyCollection<EmailMessage> Sent => _sent;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (OnSend is { } onSend)
        {
            await onSend(call);
        }

        _sent.Enqueue(message);
    }
}
```

4. Reemplaza la clase `NotificationsApiFactory` entera por:

```csharp
internal sealed class NotificationsApiFactory(
    string connectionString,
    IEmailChannel? emailChannel = null,
    bool runDeliveryWorkers = false)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        // Fijado, nunca heredado: con "infobip" y sus claves ausentes, el validador de Notifications
        // falla al arrancar y todas las pruebas del archivo mueren antes de su aserción.
        builder.UseSetting("Notifications:EmailProvider", "log");
        builder.ConfigureServices(services =>
        {
            if (emailChannel is not null)
            {
                services.RemoveAll<IEmailChannel>();
                services.AddSingleton<IEmailChannel>(emailChannel);
            }

            // Los cinco workers de correo sondean cada 3 s por su cuenta. En una prueba que llama a
            // DrainAsync competirían con ella y la volverían no determinista. Mismo interruptor que
            // runExportWorker en QuotationsApiHarness: sólo lo prende la prueba que los necesita.
            if (!runDeliveryWorkers)
            {
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && NotificationsDeliveryHarness.IsDeliveryWorker(descriptor.ImplementationType))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            }
        });
    }
}
```

- [ ] **Step 2: Escribir las pruebas del interruptor**

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>El interruptor del harness: sin él, cada prueba que llama a DrainAsync competiría con los
/// cinco workers del host.</summary>
public sealed class DeliveryWorkersHarnessTests
{
    [Fact]
    public async Task TheTestHostLeavesTheDeliveryWorkersOutByDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());

        Assert.Empty(factory.Services.GetServices<IHostedService>()
            .Where(service => IsDeliveryWorker(service.GetType())));
    }

    [Fact]
    public async Task TheSwitchPutsTheFiveWorkersBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), runDeliveryWorkers: true);

        Assert.Equal(5, factory.Services.GetServices<IHostedService>()
            .Count(service => IsDeliveryWorker(service.GetType())));
    }
}
```

- [ ] **Step 3: Correrlas y ver fallar la primera**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkersHarnessTests"`

Esperado: `TheTestHostLeavesTheDeliveryWorkersOutByDefault` falla con `Assert.Empty() Failure: Collection was not empty` y cinco elementos. El registro por factoría deja el descriptor sin `ImplementationType`, así que el harness no los encuentra. `TheSwitchPutsTheFiveWorkersBack` pasa: es la guarda de que el interruptor no se los lleve de más.

- [ ] **Step 4: Registrar los workers por tipo**

En `NotificationsInfrastructureExtensions.cs`, reemplaza las líneas 43-66 (los cinco `AddHostedService` con factoría y sus comentarios) por:

```csharp
        // Por tipo y no por factoría: así el descriptor lleva ImplementationType y el harness de
        // pruebas puede sacarlos (spec 2026-09-13). Mismo registro que ExportJobWorker. El de
        // invitaciones recibe IOptions<NotificationsOptions> por constructor, porque arma el enlace
        // con InvitationUrl; los de exportación no, porque su enlace ya viene prefirmado en el evento.
        services.AddHostedService<InvitationDeliveryWorker>();
        services.AddHostedService<CustomerExportDeliveryWorker>();
        services.AddHostedService<ProductExportDeliveryWorker>();
        // La exportación asíncrona de Quotations (spec 2026-09-12, D12): un worker por evento.
        services.AddHostedService<QuotationsExportReadyDeliveryWorker>();
        services.AddHostedService<QuotationsExportFailedDeliveryWorker>();
```

Saca `using Microsoft.Extensions.Logging;` de la línea 4, que queda sin uso. `Microsoft.Extensions.Options` sigue en uso en `AddEmailChannel`.

- [ ] **Step 5: Correr las pruebas del proyecto**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests`

Esperado: `Passed!  - Failed: 0, Passed: 10`. Son las 3 de antes, las 5 del reclamo y las 2 del interruptor. Las dos factorías viejas siguen corriendo los workers hospedados, porque no tienen interruptor.

- [ ] **Step 6: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersHarnessTests.cs
git status --short
```

---

### Task 4: La base `OutboxDeliveryWorker`, ejercitada sobre el worker de «exportación fallida»

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs`, que se reescribe entero
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs`

**Interfaces:**
- Consumes: `InboxClaims.TryClaimAsync` (Task 2), `NotificationsApiFactory`, `RecordingEmailChannel` y los helpers del harness (Task 3).
- Produces:
  - `internal abstract partial class OutboxDeliveryWorker(IServiceScopeFactory scopeFactory, ILogger logger) : BackgroundService`, con:
    - las constantes `internal const int BatchSize = 20`, `internal const int MaxAttempts = 3` e `internal const string AttemptsExhaustedReason = "delivery_attempts_exhausted"`;
    - `internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3)` y `internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2)`;
    - los miembros abstractos `protected abstract string Consumer { get; }`, `EventName` y `TemplateRef`, y `protected abstract Task<Notification> DeliverAsync(OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)`;
    - `protected static Task SendAsync(DeliveryContext context, Notification notification, Func<EmailMessage> render, CancellationToken stoppingToken)`;
    - `internal Task DrainAsync(CancellationToken stoppingToken)`.
  - `internal sealed record DeliveryContext(IEmailChannel Channel, IUserDirectory UserDirectory, IClock Clock)`.

- [ ] **Step 1: Abrir la costura, sin cambiar comportamiento**

En `QuotationsExportFailedDeliveryWorker.cs`:
- renombra `private async Task ProcessBatchAsync(CancellationToken cancellationToken)` (línea 49) a `internal async Task DrainAsync(CancellationToken cancellationToken)`;
- cambia la llamada de la línea 35 a `await DrainAsync(stoppingToken);`.

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~QuotationsExportNotificationTests"`

Esperado: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 2: Escribir las pruebas que fallan**

Las pruebas usan literales (`3`, `"delivery_attempts_exhausted"`) y no las constantes de la base. Así compilan antes de que la base exista, y el RED que ves es de comportamiento.

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El reclamo, el lease y el aislamiento de los workers de correo (spec 2026-09-13, Sección 1). Se
/// ejercitan sobre el worker de "exportación fallida", el de payload más corto: la mecánica es de la
/// base común, así que vale igual para los cinco. Los workers hospedados no corren
/// (NotificationsApiFactory): cada prueba llama a DrainAsync o arranca el suyo.
/// </summary>
public sealed class OutboxDeliveryWorkerTests
{
    private const string EventName = "quotations.export-failed.v1";
    private const string Consumer = "notifications.quotations-export-failed-email";
    private const string TemplateRef = QuotationsExportFailedEmailTemplate.TemplateRef;

    [Fact]
    public async Task TwoConcurrentDrainsSendTheMessageOnce()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel
        {
            // Abre la ventana entre leer los candidatos y guardar: sin reclamo, los dos mandan.
            OnSend = _ => Task.Delay(TimeSpan.FromMilliseconds(500)),
        };
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        await InsertOutboxAsync(database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        await Task.WhenAll(
            NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken),
            NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken));

        Assert.Single(channel.Sent);
        var notification = Assert.Single(
            await NotificationsForAsync(database.GetConnectionString(), ownerUserId, TemplateRef));
        Assert.Equal("Sent", notification.Status);
    }

    // A4: un timeout del proveedor es una falla de envío, no un apagado. Corre el loop de verdad
    // (StartAsync), porque lo que se rompía era el loop.
    [Fact]
    public async Task AProviderTimeoutFailsTheMessageAndTheLoopKeepsGoing()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel
        {
            // Lo que tira HttpClient cuando vence su Timeout: una cancelación que no es el apagado.
            OnSend = call => call == 1
                ? Task.FromException(new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                    new TimeoutException()))
                : Task.CompletedTask,
        };
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var (secondTenantId, secondUserId, _) = await RegisterOwnerAsync(factory);
        var timedOut = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        var worker = NewWorker(factory);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var failed = await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, TemplateRef);
            Assert.Equal("Failed", failed?.Status);
            Assert.StartsWith("The request was canceled", failed?.FailureReason, StringComparison.Ordinal);
            var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, timedOut);
            Assert.NotNull(inbox?.ProcessedAt);

            // Un mensaje nuevo después del timeout: el mismo worker lo manda en un tick siguiente.
            await InsertOutboxAsync(
                database.GetConnectionString(), EventName, FailedPayload(secondTenantId, secondUserId));
            var sent = await WaitForNotificationAsync(database.GetConnectionString(), secondUserId, TemplateRef);
            Assert.Equal("Sent", sent?.Status);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AClaimWhoseLeaseExpiredIsRetakenAndCountsTheAttempt()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));
        // Un worker que lo reclamó y murió antes de guardar: sin processed_at y con el lease vencido.
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: null, claimedUntil: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 1);

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(channel.Sent);
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);
        Assert.Equal(2, inbox?.Attempts);
        Assert.NotNull(inbox?.ProcessedAt);
    }

    // Tres reclamos sin terminar: el cuarto no envía, registra el fallo y cierra el mensaje.
    [Fact]
    public async Task AMessageClaimedThreeTimesWithoutFinishingIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: null, claimedUntil: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 3);

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(channel.Sent);
        // Sin destinatario: lo que está roto puede ser justamente el payload, así que no se lee.
        var poisoned = Assert.Single(
            await NotificationsForAsync(database.GetConnectionString(), Guid.Empty, TemplateRef));
        Assert.Equal(("Failed", "delivery_attempts_exhausted"), (poisoned.Status, poisoned.FailureReason));
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);
        Assert.Equal(4, inbox?.Attempts);
        Assert.NotNull(inbox?.ProcessedAt);
    }

    [Fact]
    public async Task AnUnreadablePayloadDoesNotStopTheRestOfTheBatch()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        // El roto va primero en el lote: sin aislamiento, su excepción abortaba todo lo que venía detrás.
        var broken = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, "{}", DateTimeOffset.UtcNow.AddMinutes(-1));
        await InsertOutboxAsync(database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(channel.Sent);
        Assert.Equal(
            "Sent",
            Assert.Single(await NotificationsForAsync(database.GetConnectionString(), ownerUserId, TemplateRef)).Status);
        // El roto queda reclamado y sin terminar: vencido el lease se reintenta, y al tercero se corta.
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, broken);
        Assert.Equal(1, inbox?.Attempts);
        Assert.Null(inbox?.ProcessedAt);
    }

    private static string FailedPayload(Guid tenantId, Guid subjectId) =>
        JsonSerializer.Serialize(new { tenantId, subjectId, kind = "Quotations" });

    // La misma clase que registra el DI, construida a mano: mismo constructor, sin el PeriodicTimer.
    private static QuotationsExportFailedDeliveryWorker NewWorker(NotificationsApiFactory factory) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<QuotationsExportFailedDeliveryWorker>>());
}
```

- [ ] **Step 3: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~OutboxDeliveryWorkerTests"`

Esperado: `Failed: 5`, cada una por lo que hoy está roto.

| Prueba | Falla esperada | Por qué |
| --- | --- | --- |
| `TwoConcurrentDrainsSendTheMessageOnce` | `Microsoft.EntityFrameworkCore.DbUpdateException` con `23505: duplicate key value violates unique constraint "PK_inbox_messages"` | Los dos enviaron, y el segundo choca al guardar. |
| `AProviderTimeoutFailsTheMessageAndTheLoopKeepsGoing` | A los ~30 s, `Assert.Equal() Failure` con `Expected: "Failed"` y `Actual: null` | El loop murió en silencio. |
| `AClaimWhoseLeaseExpiredIsRetakenAndCountsTheAttempt` | `Assert.Single() Failure: The collection was empty` | El anti-join no mira el lease. |
| `AMessageClaimedThreeTimesWithoutFinishingIsFailedWithoutSending` | `Assert.Single() Failure: The collection was empty` | No hay notificación de envenenado. |
| `AnUnreadablePayloadDoesNotStopTheRestOfTheBatch` | `System.Collections.Generic.KeyNotFoundException` desde `DrainAsync` | — |

- [ ] **Step 4: Escribir la base**

`src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// Lo que comparten los workers que consumen un evento del outbox de plataforma y mandan un correo
/// (spec 2026-09-13, A6): el loop, los candidatos, el reclamo, el aislamiento por mensaje y la
/// cancelación. Cada worker concreto declara su consumidor, su evento y su plantilla, y arma su
/// notificación en <see cref="DeliverAsync"/>.
///
/// Garantía: dos réplicas nunca envían el mismo mensaje a la vez. Cada mensaje se reclama en el inbox
/// propio con un lease (<see cref="InboxClaims"/>) antes de enviar. Queda un residual, el mismo de
/// antes: si el proceso muere entre el envío y el guardado, el correo sale de nuevo cuando vence el
/// lease. Infobip no recibe una clave de idempotencia, así que eso no se puede cerrar del todo.
/// </summary>
internal abstract partial class OutboxDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
{
    internal const int BatchSize = 20;

    /// <summary>Reclamos sin terminar que se toleran. El siguiente registra la notificación como
    /// fallida y no envía.</summary>
    internal const int MaxAttempts = 3;

    internal const string AttemptsExhaustedReason = "delivery_attempts_exhausted";

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Por encima del timeout del HttpClient de Infobip (30 s), con margen para el render y
    /// el guardado. Si venciera durante un envío, otra réplica retomaría el mensaje.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    protected abstract string Consumer { get; }

    protected abstract string EventName { get; }

    protected abstract string TemplateRef { get; }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery tick of {Consumer} failed.")]
    private static partial void LogTickFailed(ILogger logger, string consumer, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} failed in {Consumer}; it is retried when its lease expires.")]
    private static partial void LogMessageFailed(ILogger logger, Guid messageId, string consumer, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} was claimed {Attempts} times by {Consumer} without finishing; it is recorded as failed and not sent.")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid messageId, int attempts, string consumer);

    /// <summary>
    /// Arma la notificación del mensaje y, si corresponde, manda el correo con
    /// <see cref="SendAsync"/>. Una excepción acá, fuera del envío —un payload ilegible, un
    /// GetEmailAsync que falla—, deja el reclamo vivo: se reintenta al vencer el lease.
    /// </summary>
    protected abstract Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            // Sólo el apagado sale del loop (A4). Un timeout del proveedor ya no llega hasta acá
            // (ver SendAsync), y cualquier otra cancelación se loguea como un tick fallido.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, Consumer, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // `internal` y no `private`: es el punto de entrada que deja probar el reclamo y el aislamiento sin
    // el PeriodicTimer de por medio (OutboxDeliveryWorkerTests, vía InternalsVisibleTo). Mismo
    // precedente que ExportJobWorker.DrainAsync.
    internal async Task DrainAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<NotificationsDbContext>();
        var context = new DeliveryContext(
            services.GetRequiredService<IEmailChannel>(),
            services.GetRequiredService<IUserDirectory>(),
            services.GetRequiredService<IClock>());

        // Candidatos: sin fila en el inbox, o con una reclamada y sin terminar cuyo lease venció.
        var now = context.Clock.UtcNow;
        var candidates = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer
                && entry.MessageId == record.Id
                && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        foreach (var record in candidates)
        {
            try
            {
                await ProcessAsync(dbContext, context, record, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: al vencer el lease se reintenta, y pasado MaxAttempts se
                // corta. Ya no bloquea al resto del lote.
                LogMessageFailed(logger, record.Id, Consumer, exception);
            }
            finally
            {
                // Un mensaje que falló a mitad no le deja entidades trackeadas al siguiente. Mismo
                // precedente que OrphanUserCleanupWorker.
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Manda el correo y marca la notificación. Cualquier falla de envío la deja en Failed y el
    /// mensaje no se reintenta, que es la semántica de siempre. La diferencia está en el filtro: un
    /// TaskCanceledException del HttpClient sin apagado también entra acá, en vez de subir y matar el
    /// loop. Sólo el apagado sale sin marcar nada, y el lease devuelve el mensaje a la cola.
    /// </summary>
    protected static async Task SendAsync(
        DeliveryContext context,
        Notification notification,
        Func<EmailMessage> render,
        CancellationToken stoppingToken)
    {
        try
        {
            await context.Channel.SendAsync(render(), stoppingToken);
            notification.MarkSent(context.Clock.UtcNow);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            notification.MarkFailed(exception.Message, context.Clock.UtcNow);
        }
    }

    private async Task ProcessAsync(
        NotificationsDbContext dbContext,
        DeliveryContext context,
        OutboxRecord record,
        CancellationToken stoppingToken)
    {
        var attempts = await InboxClaims.TryClaimAsync(
            dbContext, Consumer, record.Id, context.Clock.UtcNow, Lease, stoppingToken);
        if (attempts is null)
        {
            // Otra réplica lo tiene, o ya se procesó.
            return;
        }

        Notification notification;
        if (attempts > MaxAttempts)
        {
            LogAttemptsExhausted(logger, record.Id, attempts.Value, Consumer);
            // Sin tenant ni destinatario: lo que falla puede ser el payload, así que no se lee.
            notification = Notification.CreateEmail(
                Guid.Empty, Guid.Empty, string.Empty, TemplateRef, context.Clock.UtcNow);
            notification.MarkFailed(AttemptsExhaustedReason, context.Clock.UtcNow);
        }
        else
        {
            notification = await DeliverAsync(record, context, stoppingToken);
        }

        // Notificación y processed_at en un solo SaveChanges.
        var entry = await dbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == Consumer && candidate.MessageId == record.Id,
            stoppingToken);
        entry.ProcessedAt = context.Clock.UtcNow;
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(stoppingToken);
    }
}

/// <summary>Lo que un worker concreto necesita para armar y mandar su correo, resuelto en el scope del
/// tick.</summary>
internal sealed record DeliveryContext(IEmailChannel Channel, IUserDirectory UserDirectory, IClock Clock);
```

- [ ] **Step 5: Pasar el worker de «exportación fallida» a la base**

Reemplaza `QuotationsExportFailedDeliveryWorker.cs` entero:

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-failed.v1`: a quien pidió la exportación le avisa que no le va a llegar el
// archivo y que puede pedirlo de nuevo. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class QuotationsExportFailedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportFailedDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.quotations-export-failed-email";

    protected override string EventName => "quotations.export-failed.v1";

    protected override string TemplateRef => QuotationsExportFailedEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await context.UserDirectory.GetEmailAsync(export.SubjectId, stoppingToken);
        var notification = Notification.CreateEmail(
            export.TenantId, export.SubjectId, email ?? string.Empty, TemplateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => QuotationsExportFailedEmailTemplate.Render(recipient, export.Kind),
            stoppingToken);
        return notification;
    }

    private static FailedPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new FailedPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty);
    }

    private sealed record FailedPayload(Guid TenantId, Guid SubjectId, string Kind);
}
```

`string recipient = email;` con tipo explícito es a propósito: el análisis de nulabilidad no siempre lleva al lambda el estado «no nulo» de `email`, y con `TreatWarningsAsErrors` un CS8604 rompe el build.

- [ ] **Step 6: Correr las pruebas y verlas pasar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests`

Esperado: `Passed!  - Failed: 0, Passed: 15`: las 10 de antes más estas 5. `QuotationsExportNotificationTests.TheFailedEventDeliversTheFailedEmail` sigue verde sin cambios: su factoría corre el worker hospedado, ahora sobre la base.

- [ ] **Step 7: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportFailedDeliveryWorker.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/OutboxDeliveryWorkerTests.cs
git status --short
```

---

### Task 5: Los otros cuatro workers pasan a la base

**Files:**
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs`
- Modify, reescritos enteros:
  - `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/InvitationDeliveryWorker.cs`
  - `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs`
  - `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs`
  - `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs`
- Modify: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs`, que cambia `DeliveryWorkerTypes` e `IsDeliveryWorker`

**Interfaces:**
- Consumes: `OutboxDeliveryWorker`, `DeliveryContext` y `SendAsync` (Task 4); el harness (Task 3).
- Produces: los cinco workers derivan de `OutboxDeliveryWorker`. Conservan sus consumidores, eventos, plantillas, payloads y constructores. `IsDeliveryWorker(Type?)` pasa a mirar la base.

Esto es un refactor, así que la prueba no se escribe en rojo: **caracteriza** el comportamiento de hoy. Tiene que pasar antes de tocar los workers y seguir pasando después. Si falla antes del cambio, la prueba está mal, no el código: **paras** y la corriges.

- [ ] **Step 1: Escribir la caracterización**

`tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs`:

```csharp
using System.Text.Json;
using Modules.Notifications.Application;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Lo que cada uno de los cinco workers hace hoy, fijado antes de pasarlos a OutboxDeliveryWorker
/// (spec 2026-09-13, A6): su evento, su plantilla, un solo correo por mensaje y los dos caminos que
/// terminan en Failed sin enviar. Corre con los workers hospedados de verdad, así que no depende de
/// ninguna costura interna y vale igual antes y después del refactor.
/// </summary>
public sealed class DeliveryWorkersCharacterizationTests
{
    public static TheoryData<string, string> Events => new()
    {
        { "tenancy.membership-invited.v1", InvitationEmailTemplate.TemplateRef },
        { "customers.export-ready.v1", CustomerExportEmailTemplate.TemplateRef },
        { "catalog.product-export-ready.v1", ProductExportEmailTemplate.TemplateRef },
        { "quotations.export-ready.v1", QuotationsExportReadyEmailTemplate.TemplateRef },
        { "quotations.export-failed.v1", QuotationsExportFailedEmailTemplate.TemplateRef },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public async Task EachWorkerDeliversItsEventOnceToItsRecipient(string eventName, string templateRef)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(database.GetConnectionString(), eventName, PayloadFor(eventName, tenantId, ownerUserId));

        Assert.Equal(
            new NotificationRow("Sent", email, null),
            await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, templateRef));
        // Idempotente: los ticks siguientes no vuelven a mandar el mismo mensaje.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Single(await NotificationsForAsync(database.GetConnectionString(), ownerUserId, templateRef));
        Assert.Single(channel.Sent, message => message.ToAddress == email);
    }

    // Un evento encolado antes del token de invitación no tiene link que armar.
    [Fact]
    public async Task AnInvitationWithoutTokenIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "tenancy.membership-invited.v1",
            JsonSerializer.Serialize(new { userId = ownerUserId, tenantId = new { value = tenantId } }));

        Assert.Equal(
            new NotificationRow("Failed", email, "invitation_token_unavailable"),
            await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, InvitationEmailTemplate.TemplateRef));
        Assert.DoesNotContain(channel.Sent, message => message.ToAddress == email);
    }

    [Fact]
    public async Task ARecipientWithoutEmailIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var unknownUserId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "customers.export-ready.v1",
            PayloadFor("customers.export-ready.v1", Guid.CreateVersion7(), unknownUserId));

        Assert.Equal(
            new NotificationRow("Failed", string.Empty, "recipient_email_unavailable"),
            await WaitForNotificationAsync(database.GetConnectionString(), unknownUserId, CustomerExportEmailTemplate.TemplateRef));
        Assert.Empty(channel.Sent);
    }

    // Los payloads tal como los escriben los productores: Tenancy anida el tenantId en "value".
    private static string PayloadFor(string eventName, Guid tenantId, Guid subjectId) => eventName switch
    {
        "tenancy.membership-invited.v1" => JsonSerializer.Serialize(
            new { userId = subjectId, tenantId = new { value = tenantId }, token = "invitation-token" }),
        "customers.export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            downloadUrl = "https://r2.test/exports/clientes.xlsx?X-Amz-Signature=abc",
            fileName = "clientes.xlsx",
            customerCount = 3,
            expiresAt = DateTimeOffset.UtcNow.AddHours(24),
        }),
        "catalog.product-export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            downloadUrl = "https://r2.test/exports/productos.xlsx?X-Amz-Signature=abc",
            fileName = "productos.xlsx",
            productCount = 3,
            expiresAt = DateTimeOffset.UtcNow.AddHours(24),
        }),
        "quotations.export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            kind = "Quotations",
            downloadUrl = "https://r2.test/exports/cotizaciones.xlsx?X-Amz-Signature=abc",
            fileName = "cotizaciones-2026-09-13-1530.xlsx",
            rowCount = 3,
            expiresAt = DateTimeOffset.UtcNow.AddHours(24),
        }),
        "quotations.export-failed.v1" => JsonSerializer.Serialize(new { tenantId, subjectId, kind = "Sales" }),
        _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "No payload for this event."),
    };
}
```

- [ ] **Step 2: Correrla antes del refactor**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~DeliveryWorkersCharacterizationTests"`

Esperado: `Passed!  - Failed: 0, Passed: 7`. Pegas la salida en el handoff como línea base del refactor.

- [ ] **Step 3: Reescribir `InvitationDeliveryWorker.cs`**

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de membresía invitada y entrega el email de invitación.
// El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class InvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<InvitationDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.invitation-email";

    protected override string EventName => "tenancy.membership-invited.v1";

    protected override string TemplateRef => InvitationEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var (userId, tenantId, token) = ParsePayload(record.PayloadJson);
        var email = await context.UserDirectory.GetEmailAsync(userId, stoppingToken);
        var notification = Notification.CreateEmail(
            tenantId, userId, email ?? string.Empty, TemplateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            // Un evento anterior al token de invitación (encolado antes del despliegue) no tiene link
            // que armar. Se marca fallido en vez de tirar: una excepción lo reintentaría hasta cortarlo
            // por intentos, y el resultado sería el mismo tres ticks más tarde.
            notification.MarkFailed("invitation_token_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        string invitationToken = token;
        await SendAsync(
            context,
            notification,
            () => InvitationEmailTemplate.Render(
                recipient, InvitationLink.Compose(options.Value.InvitationUrl, invitationToken)),
            stoppingToken);
        return notification;
    }

    private static (Guid UserId, Guid TenantId, string? Token) ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var userId = root.GetProperty("userId").GetGuid();
        var tenantId = root.GetProperty("tenantId").GetProperty("value").GetGuid();
        // TryGetProperty y no GetProperty: los mensajes encolados antes del despliegue del token no lo
        // traen, y esos se resuelven como fallo marcado, no como mensaje envenenado.
        var token = root.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        return (userId, tenantId, token);
    }
}
```

- [ ] **Step 4: Reescribir `CustomerExportDeliveryWorker.cs`**

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de exportación de clientes lista y entrega el email con el
// enlace de descarga. El enlace ya viene prefirmado en el payload: este módulo no conoce Storage ni
// sabe firmar nada. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class CustomerExportDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CustomerExportDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.customer-export-email";

    protected override string EventName => "customers.export-ready.v1";

    protected override string TemplateRef => CustomerExportEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await context.UserDirectory.GetEmailAsync(export.SubjectId, stoppingToken);
        var notification = Notification.CreateEmail(
            export.TenantId, export.SubjectId, email ?? string.Empty, TemplateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => CustomerExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.CustomerCount, export.ExpiresAt),
            stoppingToken);
        return notification;
    }

    private static ExportPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ExportPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("customerCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ExportPayload(
        Guid TenantId,
        Guid SubjectId,
        string DownloadUrl,
        string FileName,
        int CustomerCount,
        DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 5: Reescribir `ProductExportDeliveryWorker.cs`**

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de exportación del catálogo lista y entrega el email con el
// enlace de descarga, que ya viene prefirmado en el payload. El reclamo, el lote y la cancelación son
// de OutboxDeliveryWorker.
internal sealed class ProductExportDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ProductExportDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.product-export-email";

    protected override string EventName => "catalog.product-export-ready.v1";

    protected override string TemplateRef => ProductExportEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await context.UserDirectory.GetEmailAsync(export.SubjectId, stoppingToken);
        var notification = Notification.CreateEmail(
            export.TenantId, export.SubjectId, email ?? string.Empty, TemplateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => ProductExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.ProductCount, export.ExpiresAt),
            stoppingToken);
        return notification;
    }

    private static ExportPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ExportPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("productCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ExportPayload(
        Guid TenantId,
        Guid SubjectId,
        string DownloadUrl,
        string FileName,
        int ProductCount,
        DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 6: Reescribir `QuotationsExportReadyDeliveryWorker.cs`**

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-ready.v1` y le manda a quien pidió la exportación el correo con el enlace,
// que ya viene prefirmado: este módulo no conoce Storage. Un worker por evento, igual que clientes y
// productos. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class QuotationsExportReadyDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportReadyDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.quotations-export-ready-email";

    protected override string EventName => "quotations.export-ready.v1";

    protected override string TemplateRef => QuotationsExportReadyEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await context.UserDirectory.GetEmailAsync(export.SubjectId, stoppingToken);
        var notification = Notification.CreateEmail(
            export.TenantId, export.SubjectId, email ?? string.Empty, TemplateRef, context.Clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => QuotationsExportReadyEmailTemplate.Render(
                recipient, export.Kind, export.DownloadUrl, export.FileName, export.RowCount, export.ExpiresAt),
            stoppingToken);
        return notification;
    }

    private static ReadyPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ReadyPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty,
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ReadyPayload(
        Guid TenantId,
        Guid SubjectId,
        string Kind,
        string DownloadUrl,
        string FileName,
        int RowCount,
        DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 7: El harness pasa a mirar la base**

En `NotificationsDeliveryHarness.cs`, reemplaza el comentario, el array `DeliveryWorkerTypes` e `IsDeliveryWorker` por:

```csharp
    // Los cinco derivan de OutboxDeliveryWorker (spec 2026-09-13, A6): un worker de correo nuevo queda
    // afuera de las pruebas sin que nadie tenga que acordarse de agregarlo acá.
    public static bool IsDeliveryWorker(Type? type) =>
        type is not null && typeof(OutboxDeliveryWorker).IsAssignableFrom(type);
```

- [ ] **Step 8: Correr todo Notifications**

```powershell
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests
```

Esperado:
- en UnitTests, `Failed: 0`;
- en IntegrationTests, `Passed!  - Failed: 0, Passed: 22`:
  - las 3 que ya existían, sin cambios;
  - 5 del reclamo;
  - 2 del interruptor;
  - 5 de la base;
  - 7 de caracterización.

Revisa además que ningún worker haya quedado con `ProcessBatchAsync`, `LogTickFailed` ni `NotificationInboxMessage`:

```powershell
Select-String -Path src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/*DeliveryWorker.cs -Pattern "ProcessBatchAsync|LogTickFailed|NotificationInboxMessage" | Where-Object { $_.Path -notmatch "OutboxDeliveryWorker" }
```

Esperado: sin salida.

- [ ] **Step 9: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/InvitationDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/InvitationDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs \
  tests/Modules/Notifications/Modules.Notifications.IntegrationTests/DeliveryWorkersCharacterizationTests.cs
git status --short
```

---

### Task 6: El `HttpClient` de Infobip corta a los 30 s, y commit 1

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Channels/InfobipEmailChannel.cs:15-24`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs`, en `AddEmailChannel` (líneas 71-84 antes de Task 3)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/InfobipEmailChannelTests.cs`

**Interfaces:**
- Consumes: `OutboxDeliveryWorker.Lease` (Task 4).
- Produces: `InfobipEmailChannel.RequestTimeout` (`internal static readonly TimeSpan`, 30 s) e `InfobipEmailChannel.CreateHttpClient()` (`internal static HttpClient`).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Notifications/Modules.Notifications.UnitTests/InfobipEmailChannelTests.cs`:

```csharp
using Modules.Notifications.Infrastructure.Channels;
using Modules.Notifications.Infrastructure.Messaging;

namespace Modules.Notifications.UnitTests;

/// <summary>El timeout del canal de Infobip (spec 2026-09-13, A5) y su relación con el lease del reclamo.</summary>
public sealed class InfobipEmailChannelTests
{
    // 30 s y no los 100 s implícitos de new HttpClient().
    [Fact]
    public void TheHttpClientGivesUpAfterThirtySeconds()
    {
        using var client = InfobipEmailChannel.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    // Si el lease venciera antes que el timeout, otra réplica retomaría un mensaje que todavía se está
    // enviando, y saldrían dos correos.
    [Fact]
    public void TheClaimLeaseOutlastsTheProviderTimeout()
    {
        Assert.True(
            OutboxDeliveryWorker.Lease > InfobipEmailChannel.RequestTimeout,
            $"Lease {OutboxDeliveryWorker.Lease} must be longer than the Infobip timeout {InfobipEmailChannel.RequestTimeout}.");
    }
}
```

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~InfobipEmailChannelTests"`

Esperado: el build falla con `error CS0117: 'InfobipEmailChannel' does not contain a definition for 'CreateHttpClient'` (y lo mismo para `RequestTimeout`).

- [ ] **Step 3: Implementar**

En `InfobipEmailChannel.cs`, justo después de `private readonly InfobipOptions settings = options.Value.Infobip;` (línea 24), agrega:

```csharp

    /// <summary>
    /// Timeout explícito (spec 2026-09-13, A5): el implícito de HttpClient es de 100 s, y el lease del
    /// reclamo (OutboxDeliveryWorker.Lease) tiene que quedar por encima con margen.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Sin IHttpClientFactory, mismo criterio que ZenviaWhatsAppSender: el canal es singleton y es el
    // dueño de su cliente durante toda la vida del proceso.
    internal static HttpClient CreateHttpClient() => new() { Timeout = RequestTimeout };
```

En `NotificationsInfrastructureExtensions.cs`, dentro de `AddEmailChannel`, reemplaza `new HttpClient(),` por `InfobipEmailChannel.CreateHttpClient(),`. El bloque queda así:

```csharp
            services.AddSingleton<IEmailChannel>(sp =>
                new InfobipEmailChannel(
                    InfobipEmailChannel.CreateHttpClient(),
                    sp.GetRequiredService<IOptions<NotificationsOptions>>()));
```

- [ ] **Step 4: Correrlas y verlas pasar**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests`

Esperado: `Passed!  - Failed: 0`, incluidas las 2 de `InfobipEmailChannelTests` y las 2 de `NotificationsDbContextMappingTests`.

- [ ] **Step 5: Verificación completa del commit**

Con Docker corriendo:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
$run = Join-Path $env:TEMP "qep-ajustes-post-export-run"
Remove-Item -LiteralPath $run -Recurse -Force -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $run
$failed = @(Get-ChildItem -LiteralPath $run -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw -LiteralPath $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique)
$expected = Get-Content -LiteralPath (Join-Path $env:TEMP "qep-ajustes-post-export-expected-failed.txt")
Compare-Object -ReferenceObject $expected -DifferenceObject $failed | Format-Table -AutoSize
"fallan: $($failed.Count) (esperadas: $($expected.Count))"
git status --short -- "*packages.lock.json" Directory.Packages.props
```

Esperado:
- `restore` sin errores y `Build succeeded.` con `0 Error(s)`;
- ArchitectureTests en `Failed: 0`;
- `Compare-Object` **sin salida** y `fallan: 16 (esperadas: 16)`;
- el último `git status` vacío, porque no cambió ningún lock.

El `dotnet test` de la suite sale con código distinto de 0 por las 16 conocidas; eso es lo esperado. Si `Compare-Object` muestra una línea `=>`, es una regresión: **paras** y la investigas. Si muestra una `<=`, una falla conocida ahora pasa: la reportas en el handoff, pero no bloquea.

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Notifications/Modules.Notifications.Infrastructure/Channels/InfobipEmailChannel.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/InfobipEmailChannelTests.cs
```

- [ ] **Step 6: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Notifications/Modules.Notifications.Infrastructure/Channels/InfobipEmailChannel.cs \
  src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs \
  tests/Modules/Notifications/Modules.Notifications.UnitTests/InfobipEmailChannelTests.cs
git status --short
git commit -m "fix(notifications): un solo envío por mensaje y timeout del proveedor sin matar el worker" \
  -m "Los cinco workers de correo pasan a una base común, OutboxDeliveryWorker, que reclama cada mensaje en el inbox propio con un lease de 2 minutos antes de enviar: dos réplicas ya no mandan el mismo correo. Un timeout de Infobip queda como envío fallido en vez de detener el loop, y un mensaje que falla fuera del envío deja de bloquear el lote y se corta al tercer reclamo. El HttpClient de Infobip corta a los 30 s. La migración AddInboxClaims deja processed_at nullable y agrega claimed_until y attempts."
git log -1 --format=%B | grep -ci "co-authored-by"
git log --oneline -3
```

Esperado: `git status` antes del commit muestra todo lo de Tasks 1-6 en staging y nada sin agregar bajo `src/Modules/Notifications` ni `tests/Modules/Notifications`. El `grep -ci` da `0`.

---

## Commit 2 — `feat(quotations): el Excel muestra los estados en español`

### Task 7: El mapa de etiquetas

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs`

**Interfaces:**
- Consumes: `QuotationStatus` (`Draft, Sent, Voided, Expired`), `SaleStatus` (`Pending, Approved`) y `SalePaymentStatus` (`FullPaymentReceived, PartialPaymentReceived, PaymentPending`), en `Modules.Quotations.Domain`.
- Produces: `public static class ExportStatusLabels`, con `string For(QuotationStatus)`, `string For(SaleStatus)` y `string For(SalePaymentStatus)`. Un valor sin etiqueta tira `ArgumentOutOfRangeException`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las etiquetas del Excel (spec 2026-09-13, A7): las de las tablas de los listados del frontend. Cada
/// prueba recorre todos los valores del enum, no sólo los de la tabla: un estado nuevo sin etiqueta la
/// pone en rojo, en vez de llegar al Excel en inglés sin que nadie se entere.
/// </summary>
public sealed class ExportStatusLabelsTests
{
    // quote-status-badge.tsx
    [Fact]
    public void EveryQuotationStatusHasTheLabelOfTheQuotesTable() =>
        AssertLabels(
            new Dictionary<QuotationStatus, string>
            {
                [QuotationStatus.Draft] = "Borrador",
                [QuotationStatus.Sent] = "Enviada",
                [QuotationStatus.Voided] = "Anulada",
                [QuotationStatus.Expired] = "Vencida",
            },
            ExportStatusLabels.For);

    // sale-list.ts, SALE_STATUS_LABELS
    [Fact]
    public void EverySaleStatusHasTheLabelOfTheSalesTable() =>
        AssertLabels(
            new Dictionary<SaleStatus, string>
            {
                [SaleStatus.Pending] = "Pendiente",
                [SaleStatus.Approved] = "Aprobada",
            },
            ExportStatusLabels.For);

    // sale-list.ts, SALE_PAYMENT_STATUS_LABELS
    [Fact]
    public void EveryPaymentStatusHasTheLabelOfTheSalesTable() =>
        AssertLabels(
            new Dictionary<SalePaymentStatus, string>
            {
                [SalePaymentStatus.FullPaymentReceived] = "Pago total",
                [SalePaymentStatus.PartialPaymentReceived] = "Pago parcial",
                [SalePaymentStatus.PaymentPending] = "Pago pendiente",
            },
            ExportStatusLabels.For);

    private static void AssertLabels<TStatus>(
        IReadOnlyDictionary<TStatus, string> expected, Func<TStatus, string> label)
        where TStatus : struct, Enum =>
        Assert.Equal(
            expected.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)),
            Enum.GetValues<TStatus>().Order().Select(status => (status, label(status))));
}
```

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportStatusLabelsTests"`

Esperado: el build falla con `error CS0103: The name 'ExportStatusLabels' does not exist in the current context`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cómo se nombra cada estado en el Excel de las exportaciones (spec 2026-09-13, A7). Son las etiquetas
/// de las tablas de los listados (qep-frontend: quote-status-badge.tsx y sale-list.ts), porque el
/// archivo es "lo que estoy viendo, entero" y tiene que decir lo mismo que la pantalla de la que sale.
///
/// Sólo para el archivo, que es lo que lee una persona. La API sigue mandando el nombre del enum (A8):
/// es contrato, y el diccionario de la pantalla lo tiene el frontend.
///
/// La rama por defecto tira a propósito: un estado nuevo sin etiqueta rompe acá, y
/// ExportStatusLabelsTests recorre los tres enums para que eso se vea en CI y no en producción.
/// </summary>
public static class ExportStatusLabels
{
    public static string For(QuotationStatus status) => status switch
    {
        QuotationStatus.Draft => "Borrador",
        QuotationStatus.Sent => "Enviada",
        QuotationStatus.Voided => "Anulada",
        QuotationStatus.Expired => "Vencida",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The quotation status has no export label."),
    };

    public static string For(SaleStatus status) => status switch
    {
        SaleStatus.Pending => "Pendiente",
        SaleStatus.Approved => "Aprobada",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The sale status has no export label."),
    };

    public static string For(SalePaymentStatus status) => status switch
    {
        SalePaymentStatus.FullPaymentReceived => "Pago total",
        SalePaymentStatus.PartialPaymentReceived => "Pago parcial",
        SalePaymentStatus.PaymentPending => "Pago pendiente",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The payment status has no export label."),
    };
}
```

- [ ] **Step 4: Correrlas y verlas pasar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ExportStatusLabelsTests"`

Esperado: `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 5: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/ExportStatusLabels.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportStatusLabelsTests.cs
git status --short
```

---

### Task 8: Los procesadores escriben las etiquetas, y commit 2

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs:103-114`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs:26-31,106-118`
- Modify (pruebas):
  - `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs:39`
  - `tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs:37-39`
  - `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs:227`
  - `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs:170-171`
- Modify (doc): `docs/superpowers/specs/2026-09-12-export-asincrono-design.md`, al final de D8, justo antes de `### D9 — Subida a Storage con clave estable` (línea 172)

**Interfaces:**
- Consumes: `ExportStatusLabels.For` (Task 7). `QuotationListItemDto.Status`, `SaleListItemDto.Status` y `SaleListItemDto.PaymentStatus` traen el nombre del enum en texto (`QuotationMapping.cs:76`, `SaleMapping.cs:35-36`).
- Produces: en el Excel de cotizaciones, «Estado» etiquetado. En el de ventas, «Estado» etiquetado y «Pago» con la forma de pago o, si no hay, la etiqueta del estado de pago. Las columnas y su orden no cambian.

- [ ] **Step 1: Cambiar las pruebas para que fallen**

En `QuotationsExportProcessorTests.cs`, línea 39:

```csharp
        // La etiqueta de la tabla, no el nombre del enum (spec 2026-09-13, A7).
        Assert.Equal("Borrador", row[4].Text);
```

En `SalesExportProcessorTests.cs`, líneas 37-39:

```csharp
        // Sin forma de pago, la columna cae a la etiqueta del estado del pago, igual que la tabla
        // (spec 2026-09-13, A7).
        Assert.Equal("Pago pendiente", row[4].Text);
        Assert.Equal("Pendiente", row[5].Text);
```

En `QuotationExportApiTests.cs`, línea 227:

```csharp
        Assert.Equal("Borrador", first[4]);
```

En `SaleExportApiTests.cs`, líneas 170-171:

```csharp
        // La API sigue mandando el enum (A8); el archivo, la etiqueta de la tabla (A7).
        Assert.Equal("Pending", items[0].Status);
        Assert.Equal(items[0].PaymentMethod ?? "Pago pendiente", first[4]);
        Assert.Equal("Pendiente", first[5]);
```

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsExportProcessorTests|FullyQualifiedName~SalesExportProcessorTests"`

Esperado: `Failed: 2`.
- `WritesTheListColumnsInTheirOrder` falla con `Assert.Equal() Failure: Strings differ`, esperando `"Borrador"` y recibiendo `"Draft"`.
- `WritesTheSalesListColumnsInTheirOrder` falla esperando `"Pago pendiente"` y recibiendo `"PaymentPending"`.

- [ ] **Step 3: Implementar**

En `QuotationsExportProcessor.cs`, dentro de `ToCells`, reemplaza la línea 111 (`ExportCell.OfText(row.Status),`) por:

```csharp
        // La etiqueta de la tabla (spec 2026-09-13, A7). El DTO trae el nombre del enum, que es
        // contrato de la API (QuotationMapping.cs:76); acá se vuelve al enum sólo para etiquetarlo.
        ExportCell.OfText(ExportStatusLabels.For(Enum.Parse<QuotationStatus>(row.Status))),
```

En `SalesExportProcessor.cs`, reemplaza el comentario de `Columns` (líneas 26-31) por:

```csharp
    /// <summary>
    /// Las de la tabla de ventas en su orden (sale-table.tsx: Venta, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago o, mientras llegue vacía, la etiqueta del
    /// estado del pago. Los estados van con la etiqueta de la pantalla (spec 2026-09-13, A7).
    /// </summary>
```

Y dentro de `ToCells`, reemplaza las líneas 114-115 por:

```csharp
        // El DTO trae los nombres de los enums, que son contrato de la API (SaleMapping.cs:35-36);
        // acá se vuelven al enum sólo para etiquetarlos.
        ExportCell.OfText(row.PaymentMethod
            ?? ExportStatusLabels.For(Enum.Parse<SalePaymentStatus>(row.PaymentStatus))),
        ExportCell.OfText(ExportStatusLabels.For(Enum.Parse<SaleStatus>(row.Status))),
```

En `docs/superpowers/specs/2026-09-12-export-asincrono-design.md`, justo antes de la línea `### D9 — Subida a Storage con clave estable`, agrega:

```markdown
> **Complemento (2026-09-13).** Las columnas de estado del Excel —«Estado» y el respaldo de «Pago»—
> usan las etiquetas en español de las tablas de los listados, no el nombre del enum. La API sigue
> mandando el enum. Ver [2026-09-13-ajustes-post-export-design.md](2026-09-13-ajustes-post-export-design.md)
> (A7, A8).

```

- [ ] **Step 4: Correrlas y verlas pasar**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationExportApiTests|FullyQualifiedName~SaleExportApiTests"
```

Esperado: `Failed: 0` en los dos.

- [ ] **Step 5: Verificación completa del commit**

Con Docker corriendo:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
$run = Join-Path $env:TEMP "qep-ajustes-post-export-run"
Remove-Item -LiteralPath $run -Recurse -Force -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $run
$failed = @(Get-ChildItem -LiteralPath $run -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw -LiteralPath $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique)
$expected = Get-Content -LiteralPath (Join-Path $env:TEMP "qep-ajustes-post-export-expected-failed.txt")
Compare-Object -ReferenceObject $expected -DifferenceObject $failed | Format-Table -AutoSize
"fallan: $($failed.Count) (esperadas: $($expected.Count))"
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs
```

Esperado: igual que en Task 6. `Build succeeded.` con `0 Error(s)`, ArchitectureTests en `Failed: 0`, `Compare-Object` sin salida y `fallan: 16 (esperadas: 16)`. Una línea `=>` es una regresión: **paras**.

- [ ] **Step 6: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs \
  src/Modules/Quotations/Modules.Quotations.Application/SalesExportProcessor.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.UnitTests/SalesExportProcessorTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleExportApiTests.cs \
  docs/superpowers/specs/2026-09-12-export-asincrono-design.md
git status --short
git commit -m "feat(quotations): el Excel muestra los estados en español" \
  -m "Las columnas Estado de cotizaciones y ventas, y el respaldo de Pago cuando no hay forma de pago, usan las etiquetas de las tablas de los listados en vez del nombre del enum. La API sigue mandando el enum. Una prueba recorre los tres enums para que un estado nuevo no llegue al archivo en inglés."
git log -1 --format=%B | grep -ci "co-authored-by"
```

Esperado: `0`.

---

## Commit 3 — `feat(seed): carga sintética para medir la exportación`

### Task 9: El interruptor `Seed:ExportLoad:Quotations` y su validación

**Files:**
- Create: `src/Bootstrapper/Seeding/ExportLoadSeedOptions.cs`
- Modify: `src/Bootstrapper/Seeding/SeedOptions.cs:22`, que agrega una propiedad después de `OwnerEmail`
- Modify: `src/Bootstrapper/Seeding/SeedOptionsValidator.cs:11-38`
- Modify: `src/Api/appsettings.example.json:31-34`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:675`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`, que Tasks 10, 11 y 13 amplían
- Test (existente): `tests/ArchitectureTests/ArchitectureTests/ConfigurationExampleTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `public sealed class ExportLoadSeedOptions { public int Quotations { get; set; } }`
  - `SeedOptions.ExportLoad` (`ExportLoadSeedOptions`, nunca null)
  - los mensajes de validación `Seed:ExportLoad:Quotations cannot be negative.` y `Seed:OwnerEmail is required when Seed:ExportLoad:Quotations is greater than 0.`
  - en las pruebas: la clase `ExportLoadSeedTests`, con los helpers `OwnerEmail` (constante `"carga@qcode.co"`) y `MessagesOf(Exception)`

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La carga sintética para medir la exportación (spec 2026-09-13, Sección 2). Vive en este proyecto
/// porque es el que sabe leer los listados y correr los exports sobre lo sembrado.
/// </summary>
public sealed class ExportLoadSeedTests
{
    private const string OwnerEmail = "carga@qcode.co";

    // Prendida sin email, la carga le daría admin a nadie sobre un tenant al que nadie puede entrar.
    // Mismo criterio que Seed:Enabled (SeedStartupTests).
    [Fact]
    public async Task TheLoadSwitchWithoutOwnerEmailFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "5");
            builder.UseSetting("Seed:OwnerEmail", string.Empty);
        });

        // ThrowsAny: ValidateOnStart lanza durante el arranque, y WebApplicationFactory puede
        // entregarla envuelta.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Seed:OwnerEmail is required when Seed:ExportLoad:Quotations is greater than 0",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANegativeLoadFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "-1");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Seed:ExportLoad:Quotations cannot be negative", StringComparison.Ordinal));
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

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"`

Esperado: `Failed: 2`, las dos con `Assert.ThrowsAny() Failure: No exception was thrown`. Hoy nadie bindea la clave, así que el host arranca igual.

- [ ] **Step 3: Implementar la opción y el validador**

`src/Bootstrapper/Seeding/ExportLoadSeedOptions.cs`:

```csharp
namespace Bootstrapper.Seeding;

/// <summary>
/// La carga sintética para medir la exportación (spec 2026-09-13, A10): un tenant propio,
/// <c>carga-export</c>, con <see cref="Quotations"/> cotizaciones, sembrado por ExportLoadSeedWorker
/// después del arranque. No depende de <see cref="SeedOptions.Enabled"/>: se prende sólo para medir y
/// se apaga después.
/// </summary>
public sealed class ExportLoadSeedOptions
{
    /// <summary>Cuántas cotizaciones sembrar. 0 —el valor por defecto— la apaga.</summary>
    public int Quotations { get; set; }
}
```

En `SeedOptions.cs`, después de la propiedad `OwnerEmail` (línea 22), agrega:

```csharp

    /// <summary>
    /// La carga sintética de la exportación. También le concede admin a <see cref="OwnerEmail"/>,
    /// sobre su propio tenant, así que prenderla exige el email igual que <see cref="Enabled"/>.
    /// </summary>
    public ExportLoadSeedOptions ExportLoad { get; set; } = new();
```

En `SeedOptionsValidator.cs`, reemplaza el método `Validate` entero (líneas 11-38):

```csharp
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        if (options.ExportLoad.Quotations < 0)
        {
            return ValidateOptionsResult.Fail("Seed:ExportLoad:Quotations cannot be negative.");
        }

        // Las dos semillas le conceden admin a OwnerEmail: cualquiera de las dos prendida lo exige.
        var requiredBy = options.Enabled
            ? "Seed:Enabled is true"
            : options.ExportLoad.Quotations > 0
                ? "Seed:ExportLoad:Quotations is greater than 0"
                : null;
        if (requiredBy is null)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.OwnerEmail))
        {
            return ValidateOptionsResult.Fail($"Seed:OwnerEmail is required when {requiredBy}.");
        }

        // Se normaliza con la misma regla del dominio de Identity, no con una propia: si el email no
        // pasa acá, tampoco va a pasar cuando el seeder cree el usuario, y el arranque es mejor lugar
        // para enterarse que el medio de la siembra.
        try
        {
            User.NormalizeEmail(options.OwnerEmail);
        }
        catch (IdentityDomainException)
        {
            return ValidateOptionsResult.Fail(
                $"Seed:OwnerEmail '{options.OwnerEmail}' is not a valid email address.");
        }

        return ValidateOptionsResult.Success;
    }
```

El mensaje de `Seed:Enabled` queda idéntico al de hoy (`Seed:OwnerEmail is required when Seed:Enabled is true.`), así que `SeedStartupTests` no cambia.

En `QuotationsApiHarness.cs`, dentro de `QepApiFactory.ConfigureWebHost` y justo después de `builder.UseSetting("Notifications:EmailProvider", "log");` (línea 675), agrega:

```csharp

            // Fijado, nunca heredado: con un número en los user-secrets de quien corre las pruebas,
            // cada host de este proyecto sembraría la carga de exportación. Las pruebas de la carga lo
            // prenden con WithWebHostBuilder, que se aplica después y gana.
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
```

- [ ] **Step 4: Correr las pruebas: las de arranque pasan y la del ejemplo falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado:
- `ExportLoadSeedTests` queda en `Passed!  - Failed: 0, Passed: 2`.
- `EveryBoundConfigurationKeyIsDocumentedInTheExample` **falla** con `Estas claves las bindea una clase de options y no están en src/Api/appsettings.example.json … : Seed:ExportLoad:Quotations`. Es el RED de la documentación.

- [ ] **Step 5: Documentar la clave en el ejemplo**

En `src/Api/appsettings.example.json`, reemplaza el bloque `"Seed"` (líneas 31-34):

```json
  "Seed": {
    "Enabled": false,
    "OwnerEmail": "<env: email que recibe el rol admin del tenant sembrado>",
    "ExportLoad": {
      "Quotations": 0
    }
  },
```

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"`

Esperado: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 6: Las pruebas de la semilla de arranque siguen verdes**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"`

Esperado: `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 7: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Bootstrapper/Seeding/ExportLoadSeedOptions.cs src/Bootstrapper/Seeding/SeedOptions.cs src/Bootstrapper/Seeding/SeedOptionsValidator.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Bootstrapper/Seeding/ExportLoadSeedOptions.cs \
  src/Bootstrapper/Seeding/SeedOptions.cs \
  src/Bootstrapper/Seeding/SeedOptionsValidator.cs \
  src/Api/appsettings.example.json \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
git status --short
```

---

### Task 10: El seeder de la carga — tenant por el dominio y SQL masivo

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs:24-81`
- Create: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`, que suma tres pruebas y un helper

**Interfaces:**
- Consumes:
  - `IdentitySeeder.SeedUserAsync(this IServiceProvider, string email, CancellationToken) → Task<Guid>`
  - `CatalogSeeder.SeedCatalogAsync(this IServiceProvider, Guid tenantId, CancellationToken)`
  - `IClock`, la cadena `ConnectionStrings:QepDatabase`
- Produces:
  - `TenancySeeder.SeedTenantAsync(this IServiceProvider services, Guid tenantId, string slug, string displayName, CancellationToken cancellationToken = default) → Task`
  - `TenancySeeder.SeedAdminMembershipAsync(this IServiceProvider services, Guid tenantId, Guid userId, string origin, CancellationToken cancellationToken = default) → Task<Guid>`, que devuelve el id de la membresía, es decir, el `MemberId`
  - `public static class ExportLoadSeeder`, con:
    - `static readonly Guid TenantId` (`01900000-0000-7000-8000-000000000004`) y `static readonly Guid BillingCompanyId` (`…0005`);
    - las constantes `TenantSlug = "carga-export"`, `TenantDisplayName`, `MembershipOrigin = "seed-export-load"`, `QuotationsPerCustomer = 25` e `ItemsPerQuotation = 3`;
    - `SeedExportLoadAsync(this IServiceProvider services, string ownerEmail, int quotations, CancellationToken cancellationToken = default) → Task<ExportLoadSeedResult>`.
  - `public sealed record ExportLoadSeedResult(bool Seeded, int Customers, int Quotations, int Items, int Sales, TimeSpan Duration)`, con `static Skipped`

- [ ] **Step 1: Escribir las pruebas que fallan**

En `ExportLoadSeedTests.cs`, reemplaza el bloque de `using` por:

```csharp
using System.Net.Http.Json;
using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

Y agrega estas tres pruebas y el helper `ScalarAsync` dentro de la clase, antes de `MessagesOf`:

```csharp
    [Fact]
    public async Task TheSeedFillsItsOwnTenantWithConsistentRowsAndNoSideEffects()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var outboxBefore = await ScalarAsync<long>(connectionString, "SELECT count(*) FROM platform.outbox_messages");

        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);

        Assert.True(result.Seeded);
        // Un cliente cada 25 cotizaciones, tres líneas por cotización, el 30 % convertido.
        Assert.Equal((8, 200, 600, 60), (result.Customers, result.Quotations, result.Items, result.Sales));
        Assert.Equal("carga-export", await ScalarAsync<string>(
            connectionString, "SELECT slug FROM tenancy.tenants WHERE id = @tenant"));
        Assert.Equal("seed-export-load", await ScalarAsync<string>(
            connectionString,
            "SELECT origin FROM tenancy.memberships WHERE tenant_id = @tenant AND state = 'Active' AND 'admin' = ANY(roles)"));
        // Enviadas vigentes o vencidas: nada que el barrido de vencimiento tenga que tocar hoy.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotations
            WHERE tenant_id = @tenant
              AND NOT ((status = 'Sent' AND valid_until >= (now() AT TIME ZONE 'UTC')::date)
                    OR (status = 'Expired' AND valid_until < (now() AT TIME ZONE 'UTC')::date))
            """));
        // Cada total es la suma de sus líneas, ninguna está "editada después de enviar", y todas caen
        // en los últimos 12 meses.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotations AS quotation
            WHERE quotation.tenant_id = @tenant
              AND (quotation.total <> (
                       SELECT sum(item.subtotal + item.tax_amount)
                       FROM quotations.quotation_items AS item
                       WHERE item.quotation_id = quotation.id)
                   OR quotation.net_total <> quotation.total
                   OR quotation.updated_at <> quotation.sent_at
                   OR quotation.created_at < now() - interval '365 days')
            """));
        // Ni outbox, ni auditoría, ni historial: el SQL masivo no pasa por los handlers.
        Assert.Equal(outboxBefore, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM platform.outbox_messages"));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenant"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM quotations.quotation_history AS history
            JOIN quotations.quotations AS quotation ON quotation.id = history.quotation_id
            WHERE quotation.tenant_id = @tenant
            """));

        // Idempotente: una segunda corrida ve cotizaciones en el tenant y no siembra.
        var again = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        Assert.False(again.Seeded);
        Assert.Equal(200L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task TheListsAndTheExportsReadTheLoad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        var ownerUserId = await ScalarAsync<Guid>(
            database.GetConnectionString(), "SELECT id FROM identity.users WHERE email = @email", ("email", OwnerEmail));
        using var client = CreateClient(
            factory, ownerUserId.ToString(), ExportLoadSeeder.TenantId.ToString(), ManagerPermissions);

        var quotations = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(ExportLoadSeeder.TenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(quotations);
        Assert.Equal(result.Quotations, quotations.Total);
        Assert.All(quotations.Items, item =>
        {
            Assert.NotNull(item.ClientName);
            Assert.Equal(OwnerEmail, item.AdvisorEmail);
            // Líneas, vigencia y cuenta de cobro: lo que hace que una venta haya podido salir de ahí.
            Assert.True(item.IsComplete);
        });

        var sales = await client.GetFromJsonAsync<SalesPageResponse>(
            $"/api/v1/tenants/{ExportLoadSeeder.TenantId}/sales", TestContext.Current.CancellationToken);
        Assert.NotNull(sales);
        Assert.Equal(result.Sales, sales.Total);
        Assert.All(sales.Items, item =>
        {
            Assert.NotNull(item.ClientName);
            Assert.Equal(OwnerEmail, item.AdvisorEmail);
            Assert.Equal("PaymentPending", item.PaymentStatus);
        });

        // Los procesadores de verdad sobre un año entero, igual que un pedido desde la pantalla.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var quotationsJob = await EnqueueExportJobAsync(
            factory, ExportLoadSeeder.TenantId, ownerUserId, ExportJobKind.Quotations,
            ExportJobFilters.Serialize(new QuotationsExportFilters(null, null, null, today.AddYears(-1), today, null, null)));
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));
        Assert.Equal(result.Quotations, (await FindExportJobAsync(factory, quotationsJob)).RowCount);

        var salesJob = await EnqueueExportJobAsync(
            factory, ExportLoadSeeder.TenantId, ownerUserId, ExportJobKind.Sales,
            ExportJobFilters.Serialize(new SalesExportFilters(null, null, null, null, today.AddYears(-1), today, null, null)));
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));
        Assert.Equal(result.Sales, (await FindExportJobAsync(factory, salesJob)).RowCount);
    }

    // Los tres contadores quedan en el siguiente al último sembrado: el primer alta real del tenant no
    // choca contra un índice único.
    [Fact]
    public async Task TheCountersContinueAfterTheLoad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var result = await factory.Services.SeedExportLoadAsync(
            OwnerEmail, 200, TestContext.Current.CancellationToken);
        var ownerUserId = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM identity.users WHERE email = @email", ("email", OwnerEmail));
        using var client = CreateClient(
            factory, ownerUserId.ToString(), ExportLoadSeeder.TenantId.ToString(), ManagerPermissions);
        var year = DateTime.UtcNow.Year;
        var quotationsThisYear = await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND quotation_number LIKE @prefix",
            ("prefix", $"QUO-{year}-%"));
        var seededClientId = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM customers.customers WHERE tenant_id = @tenant ORDER BY cuc LIMIT 1");

        var quotation = await CreateQuotationAsync(client, ExportLoadSeeder.TenantId, seededClientId);
        Assert.Equal($"QUO-{year}-{quotationsThisYear + 1:D4}", quotation.QuotationNumber);

        var customerId = await CreateActiveCustomerAsync(client, ExportLoadSeeder.TenantId);
        var cuc = await ScalarAsync<string>(
            connectionString, "SELECT cuc FROM customers.customers WHERE id = @id", ("id", customerId));
        Assert.EndsWith($"{result.Customers + 1:D6}", cuc, StringComparison.Ordinal);

        // Ventas: un contador por año, cada uno en el siguiente al último sembrado.
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM (
                SELECT extract(year FROM converted_at AT TIME ZONE 'UTC')::int AS year, count(*) + 1 AS expected
                FROM quotations.sales
                WHERE tenant_id = @tenant
                GROUP BY 1) AS seeded
            FULL JOIN (
                SELECT year, next_value FROM quotations.sale_number_counters WHERE tenant_id = @tenant) AS counter
              ON counter.year = seeded.year
            WHERE counter.next_value IS DISTINCT FROM seeded.expected
            """));
    }

    // @tenant siempre apunta al tenant de la carga; los demás parámetros van por nombre.
    private static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@tenant", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("tenant", ExportLoadSeeder.TenantId);
        }

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
```

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"`

Esperado: el build falla con `error CS0234: The type or namespace name 'ExportLoadSeeder' does not exist…` o con `CS1061: 'IServiceProvider' does not contain a definition for 'SeedExportLoadAsync'`.

- [ ] **Step 3: Parametrizar `TenancySeeder`**

En `TenancySeeder.cs`, reemplaza `SeedTenantAsync` y `SeedOwnerMembershipAsync` (líneas 24-81) por:

```csharp
    public static Task SeedTenantAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default) =>
        services.SeedTenantAsync(SeedTenantId, SeedTenantSlug, SeedTenantDisplayName, cancellationToken);

    /// <summary>
    /// Crea el tenant por id si no existe, por el dominio: rigen las mismas reglas de slug que en un
    /// registro. La usan la semilla de arranque y la carga de exportación (spec 2026-09-13), cada una
    /// con su id.
    /// </summary>
    public static async Task SeedTenantAsync(
        this IServiceProvider services,
        Guid tenantId,
        string slug,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var id = new TenantId(tenantId);
        if (await dbContext.Tenants.AnyAsync(tenant => tenant.Id == id, cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(Tenant.Create(
            id,
            slug,
            displayName,
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Crea la membresía del owner, ya en <c>Active</c>. Usa
    /// <see cref="Membership.RegistrationOrigin"/> y no un origen propio porque esta membresía
    /// **es** la del owner del tenant, el mismo caso que <c>TenantRegistrationService</c>: así
    /// hereda la protección del agregado, que impide suspenderla, quitarla o dejarla sin el rol
    /// admin. La contrapartida es que tampoco se puede quitar por la API — correcto para un
    /// tenant cuya única salida es borrar la base y volver a sembrarlo.
    /// </summary>
    public static async Task SeedOwnerMembershipAsync(
        this IServiceProvider services,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await services.SeedAdminMembershipAsync(
            SeedTenantId, ownerUserId, Membership.RegistrationOrigin, cancellationToken);
    }

    /// <summary>
    /// Crea una membresía admin ya en <c>Active</c> si ese usuario no tiene una en ese tenant, y
    /// devuelve su id: es el <c>MemberId</c> al que apuntan advisor_id, created_by y converted_by.
    /// </summary>
    public static async Task<Guid> SeedAdminMembershipAsync(
        this IServiceProvider services,
        Guid tenantId,
        Guid userId,
        string origin,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenant = new TenantId(tenantId);
        var existing = await dbContext.Memberships.SingleOrDefaultAsync(
            membership => membership.TenantId == tenant && membership.UserId == userId,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Id.Value;
        }

        var created = Membership.CreateActive(
            MembershipId.New(),
            userId,
            tenant,
            ["admin"],
            origin,
            DateTimeOffset.UtcNow);
        dbContext.Memberships.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created.Id.Value;
    }
```

- [ ] **Step 4: Escribir el seeder**

`src/Bootstrapper/Seeding/ExportLoadSeeder.cs`:

```csharp
using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Identity.Infrastructure.Seed;
using Modules.Tenancy.Infrastructure.Seed;
using Npgsql;

namespace Bootstrapper.Seeding;

/// <summary>
/// La carga sintética para medir la exportación en producción (spec 2026-09-13, A10).
///
/// El tenant, el usuario, la membresía y el catálogo se crean por el dominio, con los mismos seeders
/// de la semilla de arranque. Clientes, cotizaciones, líneas y ventas van en SQL masivo, en una sola
/// transacción: no pasan por los handlers, así que no generan outbox, auditoría, correos ni WhatsApp.
///
/// Es idempotente: si el tenant ya tiene cotizaciones, no siembra. Si falla a mitad, la transacción no
/// deja nada, y la próxima corrida siembra de cero sobre el tenant y el catálogo que ya existen.
/// </summary>
public static class ExportLoadSeeder
{
    /// <summary>Fijo, para que la limpieza (ops/export-load-cleanup.sql) sepa a quién borrar. No choca
    /// con el de desarrollo (...0001), el sujeto de desarrollo (...0002) ni origen-botanico
    /// (...0003).</summary>
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000004");

    /// <summary>
    /// La empresa de la cuenta de cobro de las cotizaciones sembradas. No existe en companies: fuera
    /// del mapeo, nada lee billing_company_id. La cuenta completa está para que el listado muestre las
    /// cotizaciones como completas y las ventas tengan de dónde salir.
    /// </summary>
    public static readonly Guid BillingCompanyId = Guid.Parse("01900000-0000-7000-8000-000000000005");

    public const string TenantSlug = "carga-export";

    public const string TenantDisplayName = "Carga de exportación";

    public const string MembershipOrigin = "seed-export-load";

    public const int QuotationsPerCustomer = 25;

    public const int ItemsPerQuotation = 3;

    public static async Task<ExportLoadSeedResult> SeedExportLoadAsync(
        this IServiceProvider services,
        string ownerEmail,
        int quotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quotations);
        var startedAt = Stopwatch.GetTimestamp();

        await services.SeedTenantAsync(TenantId, TenantSlug, TenantDisplayName, cancellationToken);
        var ownerUserId = await services.SeedUserAsync(ownerEmail, cancellationToken);
        var advisorId = await services.SeedAdminMembershipAsync(
            TenantId, ownerUserId, MembershipOrigin, cancellationToken);
        await services.SeedCatalogAsync(TenantId, cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (await AlreadySeededAsync(connection, cancellationToken))
        {
            return ExportLoadSeedResult.Skipped;
        }

        var customers = Math.Max(1, quotations / QuotationsPerCustomer);
        var classificationId = Guid.CreateVersion7();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, ClassificationSql, cancellationToken,
            ("classification", classificationId), ("tenant", TenantId), ("now", now));
        var seededCustomers = await ExecuteAsync(connection, transaction, CustomersSql, cancellationToken,
            ("tenant", TenantId), ("classification", classificationId), ("customers", customers), ("now", now));
        if (seededCustomers != customers)
        {
            throw new InvalidOperationException(
                $"Export load seed expected {customers} customers and inserted {seededCustomers}: geography.cities has no rows.");
        }

        await ExecuteAsync(connection, transaction, AddressesSql, cancellationToken,
            ("tenant", TenantId), ("now", now));
        await ExecuteAsync(connection, transaction, CucCounterSql, cancellationToken,
            ("tenant", TenantId), ("customers", customers));

        await ExecuteAsync(connection, transaction, LoadTablesSql, cancellationToken);
        await ExecuteAsync(connection, transaction, LoadQuotationsSql, cancellationToken,
            ("tenant", TenantId), ("quotations", quotations), ("customers", customers), ("now", now));
        await ExecuteAsync(connection, transaction, LoadItemsSql, cancellationToken,
            ("tenant", TenantId), ("itemsPerQuotation", ItemsPerQuotation));

        var seededQuotations = await ExecuteAsync(connection, transaction, QuotationsSql, cancellationToken,
            ("tenant", TenantId), ("advisor", advisorId), ("today", today), ("billingCompany", BillingCompanyId));
        if (seededQuotations != quotations)
        {
            throw new InvalidOperationException(
                $"Export load seed expected {quotations} quotations and inserted {seededQuotations}: the load tenant has no active products.");
        }

        var seededItems = await ExecuteAsync(connection, transaction, ItemsSql, cancellationToken);
        var seededSales = await ExecuteAsync(connection, transaction, SalesSql, cancellationToken,
            ("tenant", TenantId), ("advisor", advisorId), ("now", now));
        await ExecuteAsync(connection, transaction, QuotationCountersSql, cancellationToken,
            ("tenant", TenantId));
        await ExecuteAsync(connection, transaction, SaleCountersSql, cancellationToken,
            ("tenant", TenantId));

        await transaction.CommitAsync(cancellationToken);

        return new ExportLoadSeedResult(
            true, seededCustomers, seededQuotations, seededItems, seededSales, Stopwatch.GetElapsedTime(startedAt));
    }

    private static async Task<bool> AlreadySeededAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM quotations.quotations WHERE tenant_id = @tenant)", connection);
        command.Parameters.AddWithValue("tenant", TenantId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string ClassificationSql = """
        INSERT INTO customers.client_classifications (id, tenant_id, name, prefix, is_active, version, created_at, updated_at)
        VALUES (@classification, @tenant, 'Carga de exportación', 'CLI', true, 1, @now, @now)
        """;

    // El CUC es {prefijo}{departamento DIVIPOLA}{consecutivo de 6}, con el departamento de la ciudad de
    // la dirección principal. greatest(6, …) porque lpad trunca lo que pasa del ancho.
    private const string CustomersSql = """
        WITH city AS (
            SELECT left(divipola_code, 2) AS department
            FROM geography.cities
            ORDER BY divipola_code
            LIMIT 1
        )
        INSERT INTO customers.customers (
            id, tenant_id, cuc, name, business_name, identification_type, identification_number, is_active,
            phone, email, classification_id, with_retention, vat_surplus, version, created_at, updated_at)
        SELECT gen_random_uuid(), @tenant,
               'CLI' || city.department || lpad(n::text, greatest(6, length(n::text)), '0'),
               'Cliente de carga ' || n, NULL, 'Nit', (800000000 + n)::text, true,
               NULL, NULL, @classification, false, false, 1, @now, @now
        FROM generate_series(1, @customers) AS n
        CROSS JOIN city
        """;

    // GET /customers y el detalle exigen una dirección principal (Customer.RequirePrincipalAddress).
    private const string AddressesSql = """
        INSERT INTO customers.customer_addresses (
            id, customer_id, name, address, phone, city_id, is_principal, created_at, updated_at)
        SELECT gen_random_uuid(), customer.id, 'Principal', 'Calle ' || right(customer.cuc, 6) || ' # 10-20', NULL,
               (SELECT id FROM geography.cities ORDER BY divipola_code LIMIT 1), true, @now, @now
        FROM customers.customers AS customer
        WHERE customer.tenant_id = @tenant
        """;

    // Cada contador guarda el próximo número a emitir (CucGenerator.NextBatchAsync).
    private const string CucCounterSql = """
        INSERT INTO customers.cuc_counters (tenant_id, next_value)
        VALUES (@tenant, @customers + 1)
        ON CONFLICT (tenant_id) DO UPDATE
            SET next_value = greatest(cuc_counters.next_value, EXCLUDED.next_value)
        """;

    // Tablas de trabajo que se descartan con el commit: calcular una vez fechas, números y totales, y
    // después insertar en las tablas de verdad.
    private const string LoadTablesSql = """
        CREATE TEMP TABLE load_quotations (
            n integer PRIMARY KEY,
            id uuid NOT NULL,
            client_id uuid NOT NULL,
            created_at timestamptz NOT NULL,
            year integer NOT NULL,
            quotation_number varchar(20) NOT NULL,
            sent_at timestamptz NOT NULL,
            valid_until date NOT NULL,
            converted boolean NOT NULL
        ) ON COMMIT DROP;
        CREATE TEMP TABLE load_items (
            id uuid NOT NULL,
            quotation_id uuid NOT NULL,
            position integer NOT NULL,
            product_id uuid NOT NULL,
            quantity numeric(10,2) NOT NULL,
            unit_price numeric(14,2) NOT NULL,
            tax integer NOT NULL,
            tax_amount numeric(14,2) NOT NULL,
            subtotal numeric(14,2) NOT NULL,
            created_at timestamptz NOT NULL
        ) ON COMMIT DROP
        """;

    // Fechas repartidas en los últimos 364 días. El número es QUO-{año UTC}-{consecutivo del año}: D4 es
    // un mínimo, y greatest(4, …) evita que lpad trunque a partir de 10 000. La vigencia es la de la app,
    // alta + 15 días. El 30 % se convierte en venta.
    private const string LoadQuotationsSql = """
        INSERT INTO load_quotations (n, id, client_id, created_at, year, quotation_number, sent_at, valid_until, converted)
        SELECT numbered.n, gen_random_uuid(), seeded_customers.id, numbered.created_at, numbered.year,
               'QUO-' || numbered.year || '-'
                   || lpad(numbered.sequence::text, greatest(4, length(numbered.sequence::text)), '0'),
               least(numbered.created_at + interval '1 hour', @now),
               (numbered.created_at AT TIME ZONE 'UTC')::date + 15,
               numbered.n % 10 < 3
        FROM (
            SELECT dated.n,
                   dated.created_at,
                   extract(year FROM dated.created_at AT TIME ZONE 'UTC')::int AS year,
                   row_number() OVER (
                       PARTITION BY extract(year FROM dated.created_at AT TIME ZONE 'UTC')
                       ORDER BY dated.created_at, dated.n) AS sequence
            FROM (
                SELECT n,
                       @now - make_interval(
                           days => ((n::bigint * 7919) % 364)::int,
                           mins => ((n::bigint * 37) % 1440)::int) AS created_at
                FROM generate_series(1, @quotations) AS n
            ) AS dated
        ) AS numbered
        JOIN (
            SELECT id, row_number() OVER (ORDER BY cuc) AS position
            FROM customers.customers
            WHERE tenant_id = @tenant
        ) AS seeded_customers ON seeded_customers.position = ((numbered.n - 1) % @customers) + 1
        """;

    // Las fórmulas del dominio sin descuento: el precio incluye IVA, así que el impuesto sale de adentro
    // de la línea (tax = round(line * t / (100 + t), 2)) y el subtotal es la base. round(x, 2) de
    // Postgres redondea alejando del cero, igual que la app.
    private const string LoadItemsSql = """
        INSERT INTO load_items (id, quotation_id, position, product_id, quantity, unit_price, tax, tax_amount, subtotal, created_at)
        SELECT gen_random_uuid(), line.quotation_id, line.position, line.product_id, line.quantity, line.unit_price,
               line.tax,
               round(line.line_total * line.tax / (100 + line.tax), 2),
               line.line_total - round(line.line_total * line.tax / (100 + line.tax), 2),
               line.created_at
        FROM (
            SELECT quotation.id AS quotation_id,
                   quotation.created_at,
                   slot AS position,
                   product.id AS product_id,
                   (1 + (quotation.n + slot) % 5)::numeric AS quantity,
                   product.unit_price,
                   product.tax,
                   round((1 + (quotation.n + slot) % 5)::numeric * product.unit_price, 2) AS line_total
            FROM load_quotations AS quotation
            CROSS JOIN generate_series(1, @itemsPerQuotation) AS slot
            JOIN (
                SELECT candidate.id,
                       coalesce(candidate.price_base_cop, 100000.00) AS unit_price,
                       coalesce(rate.percentage, 0) AS tax,
                       row_number() OVER (ORDER BY candidate.code) - 1 AS position,
                       count(*) OVER () AS total
                FROM catalog.products AS candidate
                LEFT JOIN catalog.tax_rates AS rate ON rate.id = candidate.tax_rate_id
                WHERE candidate.tenant_id = @tenant AND candidate.is_active
            ) AS product ON product.position = (quotation.n * @itemsPerQuotation + slot) % product.total
        ) AS line
        """;

    // Sin retención ni excedente de IVA (los flags del cliente en false): tax_amount es la suma de las
    // líneas y net_total es igual a total. Las vencidas se siembran ya Expired y las vigentes Sent, para
    // que el barrido de vencimiento no escriba historial ni auditoría. updated_at = sent_at: más tarde
    // contaría como "editada después de enviar".
    private const string QuotationsSql = """
        INSERT INTO quotations.quotations (
            id, tenant_id, quotation_number, client_id, advisor_id, status, currency, payment_method,
            subtotal, tax_percentage, tax_amount, discount_amount, total,
            billing_uses_business_name, is_store_pickup, bills_to_final_consumer,
            customer_with_retention, customer_vat_surplus, retention_amount, net_total, notes,
            created_by, updated_by, created_at, updated_at, sent_at, valid_until, pdf_file_id, version,
            billing_company_id, billing_bank_name, billing_account_number, billing_account_currency)
        SELECT quotation.id, @tenant, quotation.quotation_number, quotation.client_id, @advisor,
               CASE WHEN quotation.valid_until < @today THEN 'Expired' ELSE 'Sent' END,
               'COP',
               CASE WHEN quotation.n % 2 = 0 THEN 'Transferencia' END,
               totals.subtotal,
               CASE WHEN totals.subtotal > 0 THEN round(totals.tax_amount / totals.subtotal * 100, 2) ELSE 0 END,
               totals.tax_amount, 0, totals.subtotal + totals.tax_amount,
               false, false, false,
               false, false, 0, totals.subtotal + totals.tax_amount, NULL,
               @advisor, NULL, quotation.created_at, quotation.sent_at, quotation.sent_at, quotation.valid_until,
               NULL, 1,
               @billingCompany, 'Banco de la carga', '000000000000', 'COP'
        FROM load_quotations AS quotation
        JOIN (
            SELECT quotation_id, sum(subtotal) AS subtotal, sum(tax_amount) AS tax_amount
            FROM load_items
            GROUP BY quotation_id
        ) AS totals ON totals.quotation_id = quotation.id
        """;

    private const string ItemsSql = """
        INSERT INTO quotations.quotation_items (
            id, quotation_id, product_id, quantity, unit_price, discount_percentage, discount_amount,
            subtotal, tax_percentage, tax_amount, position, created_at, updated_at)
        SELECT id, quotation_id, product_id, quantity, unit_price, 0, 0,
               subtotal, tax, tax_amount, position, created_at, created_at
        FROM load_items
        """;

    // Convertida un día después del envío, dentro de la vigencia. VEN-{año UTC de la conversión}-{n}.
    // PaymentPending porque cualquier otro estado de pago exige comprobantes en Storage.
    private const string SalesSql = """
        INSERT INTO quotations.sales (
            id, tenant_id, sale_number, quotation_id, status, payment_status, notes, converted_at, converted_by,
            approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
        SELECT gen_random_uuid(), @tenant,
               'VEN-' || sale.year || '-' || lpad(sale.sequence::text, greatest(4, length(sale.sequence::text)), '0'),
               sale.quotation_id, 'Pending', 'PaymentPending', NULL, sale.converted_at, @advisor,
               NULL, NULL, NULL, sale.converted_at, sale.converted_at, 1
        FROM (
            SELECT converted.quotation_id,
                   converted.converted_at,
                   extract(year FROM converted.converted_at AT TIME ZONE 'UTC')::int AS year,
                   row_number() OVER (
                       PARTITION BY extract(year FROM converted.converted_at AT TIME ZONE 'UTC')
                       ORDER BY converted.converted_at, converted.n) AS sequence
            FROM (
                SELECT id AS quotation_id, n, least(sent_at + interval '1 day', @now) AS converted_at
                FROM load_quotations
                WHERE converted
            ) AS converted
        ) AS sale
        """;

    private const string QuotationCountersSql = """
        INSERT INTO quotations.quotation_number_counters (tenant_id, year, next_value)
        SELECT @tenant, year, count(*) + 1
        FROM load_quotations
        GROUP BY year
        ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = greatest(quotation_number_counters.next_value, EXCLUDED.next_value)
        """;

    private const string SaleCountersSql = """
        INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)
        SELECT @tenant, extract(year FROM converted_at AT TIME ZONE 'UTC')::int, count(*) + 1
        FROM quotations.sales
        WHERE tenant_id = @tenant
        GROUP BY 2
        ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = greatest(sale_number_counters.next_value, EXCLUDED.next_value)
        """;
}

/// <summary>Lo que dejó una corrida de la carga. <see cref="Skipped"/> es "el tenant ya tenía
/// cotizaciones".</summary>
public sealed record ExportLoadSeedResult(
    bool Seeded,
    int Customers,
    int Quotations,
    int Items,
    int Sales,
    TimeSpan Duration)
{
    public static ExportLoadSeedResult Skipped { get; } = new(false, 0, 0, 0, 0, TimeSpan.Zero);
}
```

Si el analizador marca los `const` debajo de los métodos (SA1202/SA1203 u otro orden de miembros), los subes arriba de `SeedExportLoadAsync` sin cambiar su contenido.

- [ ] **Step 5: Correrlas y verlas pasar**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CatalogSeedTests"
```

Esperado: `ExportLoadSeedTests` en `Passed!  - Failed: 0, Passed: 5` (las 2 de Task 9 y estas 3). `SeedStartupTests` y `CatalogSeedTests` en `Failed: 0`: el refactor de `TenancySeeder` no les cambia nada.

Si falla una sentencia, el mensaje de Npgsql trae la columna o el tipo. Corriges el SQL contra el mapeo (`QuotationsDbContext.cs`, `CustomersDbContext.cs`) y nunca la prueba.

- [ ] **Step 6: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs src/Bootstrapper/Seeding/ExportLoadSeeder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs \
  src/Bootstrapper/Seeding/ExportLoadSeeder.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
git status --short
```

---

### Task 11: El worker que siembra después del arranque

**Files:**
- Create: `src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:474`, que agrega una línea después de `AddSingleton<IValidateOptions<SeedOptions>, SeedOptionsValidator>()`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`, que suma dos pruebas

**Interfaces:**
- Consumes: `ExportLoadSeeder.SeedExportLoadAsync` (Task 10), `IOptions<SeedOptions>` (Task 9) e `IHostApplicationLifetime`.
- Produces: `public sealed partial class ExportLoadSeedWorker : BackgroundService`. Es público para que la prueba lo encuentre con `OfType<ExportLoadSeedWorker>()` y espere su `ExecuteTask`: Bootstrapper no tiene `InternalsVisibleTo`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `ExportLoadSeedTests.cs`, agrega al bloque de `using`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
```

Y agrega estas dos pruebas en la clase:

```csharp
    // Después del arranque, nunca dentro: el startupProbe le da al pod 60 s como máximo.
    [Fact]
    public async Task WithTheSwitchOnTheHostSeedsOnItsOwnAfterStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:ExportLoad:Quotations", "20");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

        var worker = factory.Services.GetServices<IHostedService>().OfType<ExportLoadSeedWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(20L, await ScalarAsync<long>(
            database.GetConnectionString(), "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant"));
    }

    [Fact]
    public async Task WithTheSwitchAtZeroTheHostSeedsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var worker = factory.Services.GetServices<IHostedService>().OfType<ExportLoadSeedWorker>().Single();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0L, await ScalarAsync<long>(
            database.GetConnectionString(), "SELECT count(*) FROM tenancy.tenants WHERE id = @tenant"));
    }
```

- [ ] **Step 2: Correrlas y verlas fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"`

Esperado: el build falla con `error CS0246: The type or namespace name 'ExportLoadSeedWorker' could not be found`.

- [ ] **Step 3: Escribir el worker y registrarlo**

`src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bootstrapper.Seeding;

/// <summary>
/// Corre la carga sintética de la exportación (spec 2026-09-13, A10) cuando
/// <c>Seed:ExportLoad:Quotations</c> es mayor que 0.
///
/// Arranca después de que la API está en pie, nunca dentro de RunQepSeedAsync. El startupProbe le da
/// al pod 60 s como máximo (prod-deployment.yaml: 12 intentos cada 5 s), y sembrar decenas de miles de
/// filas dentro del arranque haría que Kubernetes lo matara. Con maxUnavailable: 0, además, el deploy
/// nunca quedaría listo.
///
/// Público y no internal para que las pruebas lo encuentren entre los IHostedService y esperen su
/// ExecuteTask: Bootstrapper no le abre sus internals a nadie.
/// </summary>
public sealed partial class ExportLoadSeedWorker(
    IServiceProvider services,
    IOptions<SeedOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ExportLoadSeedWorker> logger) : BackgroundService
{
    // Ruidoso a propósito, como la semilla de arranque: concede admin, y prendido tiene que verse.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Export load seed is ENABLED: seeding {Quotations} quotations into tenant '{TenantSlug}' and granting the admin role to '{OwnerEmail}'. Set Seed:ExportLoad:Quotations back to 0 after measuring.")]
    private static partial void LogStarting(ILogger logger, int quotations, string tenantSlug, string ownerEmail);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Export load seed finished: {Customers} customers, {Quotations} quotations, {Items} items and {Sales} sales in {ElapsedMilliseconds} ms.")]
    private static partial void LogFinished(
        ILogger logger, int customers, int quotations, int items, int sales, long elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Export load seed skipped: tenant '{TenantSlug}' already has quotations.")]
    private static partial void LogSkipped(ILogger logger, string tenantSlug);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export load seed failed; nothing was committed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var quotations = options.Value.ExportLoad.Quotations;
        if (quotations <= 0)
        {
            return;
        }

        if (!await WaitForStartedAsync(stoppingToken))
        {
            return;
        }

        var ownerEmail = options.Value.OwnerEmail!;
        LogStarting(logger, quotations, ExportLoadSeeder.TenantSlug, ownerEmail);
        try
        {
            var result = await services.SeedExportLoadAsync(ownerEmail, quotations, stoppingToken);
            if (!result.Seeded)
            {
                LogSkipped(logger, ExportLoadSeeder.TenantSlug);
                return;
            }

            // Variables locales y no expresiones en la llamada: CA1873 marca los argumentos que se
            // evalúan aunque el nivel esté apagado.
            var customers = result.Customers;
            var seededQuotations = result.Quotations;
            var items = result.Items;
            var sales = result.Sales;
            var elapsedMilliseconds = (long)result.Duration.TotalMilliseconds;
            LogFinished(logger, customers, seededQuotations, items, sales, elapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado a mitad de la siembra: la transacción no se commiteó, y el próximo arranque
            // siembra de cero.
        }
        catch (Exception exception)
        {
            // Nunca tumba el host: una excepción que sale de un BackgroundService detiene la aplicación
            // (BackgroundServiceExceptionBehavior.StopHost), y la API no puede caerse por una carga de
            // prueba.
            LogFailed(logger, exception);
        }
    }

    private async Task<bool> WaitForStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Si la aplicación ya arrancó, Register invoca el callback en el acto.
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
```

En `QepServiceCollectionExtensions.cs`, justo después de `services.AddSingleton<IValidateOptions<SeedOptions>, SeedOptionsValidator>();` (línea 474), agrega:

```csharp
        // La carga sintética de la exportación (spec 2026-09-13): no hace nada con
        // Seed:ExportLoad:Quotations en 0, que es el default.
        services.AddHostedService<ExportLoadSeedWorker>();
```

- [ ] **Step 4: Correrlas y verlas pasar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"`

Esperado: `Passed!  - Failed: 0, Passed: 7`.

- [ ] **Step 5: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs \
  src/Bootstrapper/QepServiceCollectionExtensions.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
git status --short
```

---

### Task 12: El worker de exportaciones loguea cuánto tardó cada job

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs:1-4,23-31,62-80`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RecordingLogger.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs`, que suma una prueba

**Interfaces:**
- Consumes: `ExportJobRunner.RunNextAsync → ExportJobRunOutcome`.
- Produces:
  - un log `Information` «`Export job run finished as {Outcome} in {ElapsedMilliseconds} ms.`» por cada job que no es `NoJob`;
  - en las pruebas, `internal sealed class RecordingLogger<T> : ILogger<T>` con `IReadOnlyList<RecordedLogEntry> Entries` y `record RecordedLogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> State, string Message)`.

Es la duración que pide la medición (spec 2026-09-13, «Medición», paso 4). Las filas llegan en el correo y el tamaño se lee del archivo descargado (Decisiones, punto 11).

- [ ] **Step 1: Escribir la prueba que falla**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RecordingLogger.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Modules.Quotations.IntegrationTests;

internal sealed record RecordedLogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> State, string Message);

/// <summary>Un ILogger que guarda cada entrada con sus valores estructurados, para afirmar qué se
/// loguea sin depender del formato del mensaje.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<RecordedLogEntry> _entries = [];

    public IReadOnlyList<RecordedLogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        _entries.Add(new RecordedLogEntry(
            logLevel,
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            formatter(state, exception)));
    }
}
```

En `ExportJobWorkerTests.cs`, agrega la prueba antes de `WaitForStatusAsync`:

```csharp
    // La medición de la exportación (spec 2026-09-13) lee la duración de cada job en el log del pod.
    [Fact]
    public async Task DrainAsyncLogsHowLongEachJobTook()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));
        await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        var logger = new RecordingLogger<ExportJobWorker>();

        await new ExportJobWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), logger)
            .DrainAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries, candidate => candidate.Level == LogLevel.Information);
        Assert.Equal("Completed", entry.State["Outcome"]?.ToString());
        Assert.IsType<long>(entry.State["ElapsedMilliseconds"]);
    }
```

- [ ] **Step 2: Correrla y verla fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportJobWorkerTests.DrainAsyncLogsHowLongEachJobTook"`

Esperado: `Assert.Single() Failure: The collection did not contain any matching items`, porque hoy el drenado no loguea nada en `Information`.

- [ ] **Step 3: Implementar**

En `ExportJobWorker.cs`, agrega `using System.Diagnostics;` como primera línea. Después de `LogPurged` (línea 31), agrega:

```csharp

    // La duración de cada job, para medir la exportación en producción (spec 2026-09-13). Las filas ya
    // llegan en el correo y el tamaño se lee del archivo descargado.
    [LoggerMessage(Level = LogLevel.Information, Message = "Export job run finished as {Outcome} in {ElapsedMilliseconds} ms.")]
    private static partial void LogRunFinished(ILogger logger, ExportJobRunOutcome outcome, long elapsedMilliseconds);
```

Y reemplaza el cuerpo del `while` de `DrainAsync` (líneas 64-79) por:

```csharp
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var startedAt = Stopwatch.GetTimestamp();
            var outcome = await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
                .RunNextAsync(cancellationToken);

            if (outcome == ExportJobRunOutcome.NoJob)
            {
                return;
            }

            var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            LogRunFinished(logger, outcome, elapsedMilliseconds);

            if (outcome == ExportJobRunOutcome.LeaseLost)
            {
                LogLeaseLost(logger);
            }
        }
```

- [ ] **Step 4: Correr las pruebas del worker**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportJobWorkerTests"`

Esperado: `Passed!  - Failed: 0, Passed: 4`.

- [ ] **Step 5: Formato y stage** (Git Bash)

```powershell
dotnet format Backend.slnx --verify-no-changes --include src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RecordingLogger.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs
```

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Exports/ExportJobWorker.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/RecordingLogger.cs \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportJobWorkerTests.cs
git status --short
```

---

### Task 13: La limpieza, el ConfigMap y el README, y commit 3

**Files:**
- Create: `ops/export-load-cleanup.sql`
- Modify: `k8s/prod-configMap.yaml:67-71`
- Modify: `README.md:295-337`, que cambia la tabla y suma una subsección antes de `## API implementada`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs`, que suma una prueba y un helper

**Interfaces:**
- Consumes:
  - de Task 10: `ExportLoadSeeder.TenantId` y `SeedExportLoadAsync`;
  - del harness: `RegisterTenantAsync`, `CreateActiveCustomerAsync`, `CreateQuotationAsync` y `EnqueueExportJobAsync`.
- Produces: `ops/export-load-cleanup.sql`, que se corre con `psql -v ON_ERROR_STOP=1 -f`.

El orden de la limpieza lo fijan las FK:
1. `sales → quotations` es RESTRICT.
2. `customers → client_classifications` es RESTRICT, por la FK compuesta.
3. `products → tax_rates` es RESTRICT.

Todo lo demás cae en cascada: ítems, partes, historial, PDFs, comprobantes, direcciones, escalas y cambios de precio.

- [ ] **Step 1: Escribir la prueba que falla**

En `ExportLoadSeedTests.cs`, agrega la prueba y el helper dentro de la clase:

```csharp
    // La limpieza borra la carga y nada más: otro tenant con datos propios queda intacto, y el usuario
    // dueño —la cuenta real de quien midió— también.
    [Fact]
    public async Task TheCleanupScriptRemovesOnlyTheLoadTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = otherClient;
        var otherCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var otherQuotation = await CreateQuotationAsync(otherClient, otherTenantId, otherCustomerId);
        await factory.Services.SeedExportLoadAsync(OwnerEmail, 50, TestContext.Current.CancellationToken);
        await EnqueueExportJobAsync(factory, ExportLoadSeeder.TenantId, Guid.CreateVersion7());

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                await File.ReadAllTextAsync(CleanupScriptPath(), TestContext.Current.CancellationToken),
                connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        string[] tenantTables =
        [
            "quotations.sales", "quotations.quotations", "quotations.quotation_number_counters",
            "quotations.sale_number_counters", "quotations.export_jobs", "customers.customers",
            "customers.client_classifications", "customers.cuc_counters", "catalog.products",
            "catalog.tax_rates", "tenancy.memberships",
        ];
        foreach (var table in tenantTables)
        {
            Assert.Equal(0L, await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM {table} WHERE tenant_id = @tenant"));
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM tenancy.tenants WHERE id = @tenant"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE id = @id", ("id", otherQuotation.Id)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM identity.users WHERE email = @email", ("email", OwnerEmail)));
    }

    // Se busca hacia arriba desde bin/, mismo criterio que ConfigurationExampleTests.
    private static string CleanupScriptPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ops", "export-load-cleanup.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"ops/export-load-cleanup.sql not found above {AppContext.BaseDirectory}.");
    }
```

- [ ] **Step 2: Correrla y verla fallar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests.TheCleanupScriptRemovesOnlyTheLoadTenant"`

Esperado: `System.IO.FileNotFoundException: ops/export-load-cleanup.sql not found above …`.

- [ ] **Step 3: Escribir el script**

`ops/export-load-cleanup.sql`:

```sql
-- Borra la carga sintética de la exportación (spec 2026-09-13): SÓLO el tenant carga-export, en el
-- orden que exigen las relaciones.
--
-- Córrelo DESPUÉS de dejar Seed__ExportLoad__Quotations en "0" y desplegar. Con el interruptor
-- prendido, cualquier reinicio del pod vuelve a sembrar el tenant que este script deja vacío.
--
-- No borra:
--   * el usuario dueño, que es la cuenta real de quien midió;
--   * las filas de audit.entries, porque el log es inmutable;
--   * los correos registrados en notifications.notifications ni los eventos del outbox;
--   * los .xlsx de R2, que los borra la regla de lifecycle de exports/.
--
--   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f ops/export-load-cleanup.sql
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM tenancy.tenants
        WHERE id = '01900000-0000-7000-8000-000000000004' AND slug = 'carga-export') THEN
        RAISE EXCEPTION 'El tenant carga-export no existe con el id esperado: no se borra nada.';
    END IF;
END
$$;

-- sales -> quotations es RESTRICT: primero las ventas. Sus comprobantes caen en cascada.
DELETE FROM quotations.sales WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
-- Ítems, partes, historial y PDFs caen en cascada con la cotización.
DELETE FROM quotations.quotations WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.quotation_number_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.sale_number_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM quotations.export_jobs WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

-- Las direcciones caen en cascada con el cliente. La clasificación es RESTRICT, así que va después.
DELETE FROM customers.customers WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM customers.client_classifications WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM customers.cuc_counters WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

-- Escalas y cambios de precio caen en cascada con el producto. La tasa es RESTRICT, así que va después.
DELETE FROM catalog.products WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM catalog.tax_rates WHERE tenant_id = '01900000-0000-7000-8000-000000000004';

DELETE FROM tenancy.memberships WHERE tenant_id = '01900000-0000-7000-8000-000000000004';
DELETE FROM tenancy.tenants WHERE id = '01900000-0000-7000-8000-000000000004';

COMMIT;
```

- [ ] **Step 4: Correrla y verla pasar**

Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~ExportLoadSeedTests"`

Esperado: `Passed!  - Failed: 0, Passed: 8`.

- [ ] **Step 5: El ConfigMap**

En `k8s/prod-configMap.yaml`, después de `Seed__OwnerEmail: "#{SEED_OWNER_EMAIL}#"` (línea 71), agrega:

```yaml
  # Carga sintética para medir la exportación (spec 2026-09-13). Con un número mayor que 0 siembra,
  # después del arranque, el tenant "carga-export" con esa cantidad de cotizaciones, y le da admin a
  # Seed__OwnerEmail. Repite el default de la imagen a propósito, igual que
  # Registration__PublicTenantSignupEnabled: es el interruptor que se prende para medir, y tenerlo acá
  # hace que prenderlo y apagarlo sean un diff de una línea que queda en la historia. Después de medir
  # vuelve a "0" y RECIÉN AHÍ se corre ops/export-load-cleanup.sql
  # (README § Carga sintética para medir la exportación).
  Seed__ExportLoad__Quotations: "0"
```

- [ ] **Step 6: El README**

En `README.md`, reemplaza la tabla de la sección `## Semilla de arranque` (líneas 303-306) por:

```markdown
| Clave                          | Por defecto | Qué hace                                                                      |
| ------------------------------ | ----------- | ----------------------------------------------------------------------------- |
| `Seed__Enabled`                | `false`     | Interruptor de la semilla de arranque. Apagado, no se siembra nada            |
| `Seed__OwnerEmail`             | sin valor   | Email que recibe la membresía con rol `admin`                                 |
| `Seed__ExportLoad__Quotations` | `0`         | Cotizaciones de la [carga sintética](#carga-sintética-para-medir-la-exportación). `0` la apaga |
```

Y justo antes de `## API implementada` (línea 339), agrega:

````markdown
### Carga sintética para medir la exportación

Con `Seed:ExportLoad:Quotations` mayor que 0, la aplicación siembra, **después** de arrancar, el
tenant **Carga de exportación** (`carga-export`). Lleva:

- esa cantidad de cotizaciones repartidas en los últimos 12 meses;
- un cliente cada 25 cotizaciones;
- el 30 % convertidas en venta;
- el catálogo de la semilla;
- una membresía `admin` para `Seed:OwnerEmail`.

Existe para medir en el pod real cuánto cuesta exportar un año (spec
`docs/superpowers/specs/2026-09-13-ajustes-post-export-design.md`). No depende de `Seed:Enabled`,
pero también exige `Seed:OwnerEmail`.

- **No corre dentro del arranque.** El `startupProbe` le da al pod 60 s, y sembrar decenas de miles
  de filas ahí haría que Kubernetes lo matara. La siembra termina con la línea
  `Export load seed finished: …` en el log.
- **Va con SQL masivo en una transacción.** No pasa por los handlers, así que no deja outbox,
  auditoría, correos ni WhatsApp.
- **Es idempotente.** Si el tenant ya tiene cotizaciones, no siembra.

Para probarla en local:

```powershell
$env:Seed__ExportLoad__Quotations = "2000"
$env:Seed__OwnerEmail = "<tu-email>"
dotnet run --project src/Api --launch-profile http
```

En producción se prende y se apaga con `Seed__ExportLoad__Quotations` en `k8s/prod-configMap.yaml`,
que se despliega desde `main`:

1. Pon el número (por ejemplo `"50000"`), commitea y despliega.
2. Espera la línea del final:

   ```powershell
   kubectl --context contabo-prod -n prod-qep-backend logs deploy/qep-backend --since=30m | Select-String "Export load seed"
   ```

3. Entra con tu cuenta, cambia al tenant **Carga de exportación** y exporta desde la pantalla.
4. **Primero apaga:** vuelve a `"0"`, commitea y despliega. Con el interruptor prendido, cualquier
   reinicio del pod vuelve a sembrar un tenant vacío.
5. **Después limpia**, con la conexión de administración que ya usas para la base de producción:

   ```powershell
   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f ops/export-load-cleanup.sql
   ```

   El script borra sólo el tenant `carga-export`, en el orden que piden las FK, y aborta si el tenant
   no está. No saques la contraseña del Secret con `kubectl get secret`: imprime los valores.

> [!WARNING]
> Mientras la carga está sembrada, los datos sintéticos conviven con los de desarrollo en la misma
> base, aislados por tenant. Además, la carga le concede `admin` a `Seed:OwnerEmail` sobre ese
> tenant.
````

- [ ] **Step 7: Verificación completa del commit**

Con Docker corriendo:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
$run = Join-Path $env:TEMP "qep-ajustes-post-export-run"
Remove-Item -LiteralPath $run -Recurse -Force -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $run
$failed = @(Get-ChildItem -LiteralPath $run -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw -LiteralPath $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique)
$expected = Get-Content -LiteralPath (Join-Path $env:TEMP "qep-ajustes-post-export-expected-failed.txt")
Compare-Object -ReferenceObject $expected -DifferenceObject $failed | Format-Table -AutoSize
"fallan: $($failed.Count) (esperadas: $($expected.Count))"
git status --short -- "*packages.lock.json" Directory.Packages.props
dotnet format Backend.slnx --verify-no-changes --include tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
```

Esperado:
- `Build succeeded.` con `0 Error(s)`;
- ArchitectureTests en `Failed: 0`, con `ConfigurationExampleTests` incluida;
- `Compare-Object` sin salida y `fallan: 16 (esperadas: 16)`;
- ningún lock cambiado.

Una línea `=>` es una regresión: **paras**.

- [ ] **Step 8: Commit** (Git Bash)

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add ops/export-load-cleanup.sql \
  k8s/prod-configMap.yaml \
  README.md \
  tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
git status --short
git commit -m "feat(seed): carga sintética para medir la exportación" \
  -m "Con Seed:ExportLoad:Quotations mayor que 0, un BackgroundService siembra después del arranque el tenant carga-export: tenant, dueño y catálogo por el dominio, y clientes, cotizaciones con sus líneas y ventas con SQL masivo en una transacción, sin outbox ni auditoría. Queda apagado por defecto y es idempotente. Incluye el validador, la clave en el ConfigMap y el ejemplo, ops/export-load-cleanup.sql, y un log con la duración de cada job de exportación para leer la medición."
git log -1 --format=%B | grep -ci "co-authored-by"
```

Esperado:
- antes del commit, `git status` muestra en staging todo lo de Tasks 9-13, incluidos `ExportLoadSeedOptions.cs`, `ExportLoadSeeder.cs`, `ExportLoadSeedWorker.cs`, `TenancySeeder.cs`, `ExportJobWorker.cs`, `RecordingLogger.cs`, `QuotationsApiHarness.cs` y `appsettings.example.json`;
- el `grep -ci` da `0`.

**Despliegue:** los tres commits del backend salen juntos a `develop` y después a `main`. La migración `AddInboxClaims` corre al arrancar y sólo agrega columnas, así que el pod viejo y el nuevo pueden solaparse durante el deploy: el viejo sigue escribiendo `processed_at`, y `attempts` toma el default.

---

## Commit 4 — `docs(quotations): la medición de la exportación de un año`

### Task 14: Medir en producción y dejar los números en el spec (manual)

Esta tarea no se hace en la sesión que implementa los commits 1-3: la corre el developer en producción, cuando esos commits ya están desplegados desde `main`. Todo lo que se ejecuta contra el clúster es **de sólo lectura** (`top`, `logs`, `get`, `describe`). El único cambio es el valor del ConfigMap, que viaja por el pipeline como cualquier otro commit a `main`.

**Files:**
- Modify: `docs/superpowers/specs/2026-09-12-export-asincrono-design.md`, el primer punto de `## Riesgos y pendientes` («**Memoria medida en local, falta un export real.**», línea 329)

**Interfaces:**
- Consumes:
  - de Task 11, la línea `Export load seed finished: …`;
  - de Task 12, `Export job run finished as {Outcome} in {ElapsedMilliseconds} ms.`;
  - `ops/export-load-cleanup.sql`, de Task 13.
- Produces: el riesgo reemplazado por los números medidos.

- [ ] **Step 1: Prender la carga (developer)**

En `k8s/prod-configMap.yaml`, cambia `Seed__ExportLoad__Quotations: "0"` por `"50000"`, commitea y despliega por el camino normal hacia `main`. Ese cambio **no** va en `feature/ajustes-post-export`. Después:

```powershell
kubectl --context contabo-prod -n prod-qep-backend get pod
kubectl --context contabo-prod -n prod-qep-backend logs deploy/qep-backend --since=30m | Select-String "Export load seed"
```

Esperado: el pod `Running` con `RESTARTS 0`, y dos líneas de log: `Export load seed is ENABLED: seeding 50000 quotations …` y, unos minutos después, `Export load seed finished: 2000 customers, 50000 quotations, 150000 items and 15000 sales in <N> ms.`. Si aparece `Export load seed failed`, **paras**: el error está en esa misma línea del log.

- [ ] **Step 2: Tomar la línea base y dejar el muestreo corriendo**

En una terminal aparte, antes de exportar:

```powershell
kubectl --context contabo-prod -n prod-qep-backend top pod
$samples = Join-Path $env:TEMP "qep-export-medicion-top.txt"
1..180 | ForEach-Object {
    "$(Get-Date -Format o) $(kubectl --context contabo-prod -n prod-qep-backend top pod --no-headers)" |
        Add-Content -LiteralPath $samples
    Start-Sleep -Seconds 5
}
```

Son 15 minutos de muestras, cada 5 s. El primer `top` es la memoria y la CPU en reposo.

- [ ] **Step 3: Exportar un año de cotizaciones y otro de ventas**

Mientras corre el muestreo:
1. Entra con tu cuenta y cambia al tenant **Carga de exportación**.
2. En Cotizaciones, filtra por el último año —desde hoy menos un año hasta hoy— y exporta.
3. Cuando llegue el correo, haz lo mismo en Ventas.

```powershell
kubectl --context contabo-prod -n prod-qep-backend logs deploy/qep-backend --since=30m | Select-String "Export job run finished|Export job tick failed"
kubectl --context contabo-prod -n prod-qep-backend get pod
kubectl --context contabo-prod -n prod-qep-backend describe pod | Select-String "Restart Count|Last State|OOMKilled"
```

Esperado: dos líneas `Export job run finished as Completed in <N> ms.`, `RESTARTS 0` y ningún `OOMKilled`. Si el pod se reinició o aparece `OOMKilled`, el riesgo **no** se cierra: anotas lo que pasó con los mismos datos y lo discutes con el developer antes de seguir.

- [ ] **Step 4: Anotar filas, tamaño y pico**

- **Filas:** salen del correo, que dice cuántas trae el archivo.
- **Tamaño:** del archivo descargado.

  ```powershell
  (Get-Item -LiteralPath "<ruta del cotizaciones-….xlsx descargado>").Length
  (Get-Item -LiteralPath "<ruta del ventas-….xlsx descargado>").Length
  ```

- **Memoria y CPU pico:** son el máximo de las muestras de `$samples` en la ventana de cada export. Esa ventana va del pedido hasta la línea `Export job run finished` de ese job.

- [ ] **Step 5: Apagar primero y limpiar después (developer)**

1. Vuelve a `Seed__ExportLoad__Quotations: "0"`, commitea y despliega. Con el interruptor prendido, cualquier reinicio del pod vuelve a sembrar el tenant que la limpieza deja vacío.
2. Con la conexión de administración de siempre (nunca saques la contraseña del Secret con `kubectl get secret`):

   ```powershell
   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f ops/export-load-cleanup.sql
   psql -h <host> -p <puerto> -U <usuario> -d <base> -c "SELECT count(*) FROM tenancy.tenants WHERE slug = 'carga-export'"
   ```

   Esperado: el script termina en `COMMIT` y la cuenta da `0`.

- [ ] **Step 6: Reemplazar el riesgo en el spec anterior**

En `docs/superpowers/specs/2026-09-12-export-asincrono-design.md`, reemplaza el punto entero que empieza con `- **Memoria medida en local, falta un export real.**` (líneas 329-332) por esta plantilla. Cada `<…>` es un valor medido en los Steps 1-4:

```markdown
- **Memoria medida en producción (<fecha de la medición>).** Un año del tenant sintético
  `carga-export` (spec 2026-09-13), exportado desde la pantalla, en el pod real (1 réplica, límite
  1Gi, 500m de CPU), con el zip en streaming (D8):

  | Export | Filas | Duración del job | Archivo | Memoria del pod, reposo → pico | CPU pico | Reinicios |
  | --- | --- | --- | --- | --- | --- | --- |
  | Cotizaciones | <filas> | <ms> ms | <MB> MB | <Mi> Mi → <Mi> Mi | <m>m | 0 |
  | Ventas | <filas> | <ms> ms | <MB> MB | <Mi> Mi → <Mi> Mi | <m>m | 0 |

  La siembra de 50.000 cotizaciones tardó <ms> ms. La medición no es un banco de pruebas: con una
  réplica, la API atendió en el mismo pod mientras tanto. La carga se borró con
  `ops/export-load-cleanup.sql` después de apagar el interruptor.
```

- [ ] **Step 7: Commit** (Git Bash)

Si `feature/ajustes-post-export` todavía no se mergeó, el commit va ahí. Si ya se mergeó, crea `docs/medicion-exportacion` desde `origin/develop` (`git switch -c docs/medicion-exportacion origin/develop`) y pon ese nombre en el guard.

```bash
test "$(git branch --show-current)" = "feature/ajustes-post-export" || { echo ABORT; exit 1; }
git add docs/superpowers/specs/2026-09-12-export-asincrono-design.md
git status --short
git commit -m "docs(quotations): la medición de la exportación de un año" \
  -m "Reemplaza el riesgo de memoria no medida del spec de la exportación asíncrona con los números de un año de cotizaciones y de ventas del tenant sintético carga-export, medidos en el pod de producción."
git log -1 --format=%B | grep -ci "co-authored-by"
```

Esperado: `0`.

---

## Frontend — `fix(sales): tuteo en los errores del listado de ventas`

### Task 15: `describeSalesFailure` pasa a tuteo

**Repo:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend`. Su `develop` local va un commit adelante de `origin/develop` (`6b4d018`, un cambio de `CLAUDE.md` sin publicar). Por eso la rama sale de `origin/develop`, y así ese commit no viaja con este.

**Files** (en el worktree nuevo):
- Modify: `src/features/sales/services/sales.api.ts:112,117,121,136,140`, los cinco textos de `describeSalesFailure`
- Test: `src/features/sales/pages/sales-list-page.test.tsx:239`
- Test: `src/features/sales/pages/sale-detail-page.test.tsx:164,179`

**Interfaces:**
- Consumes: nada del backend.
- Produces: `describeSalesFailure` devuelve los mismos casos con el texto en tuteo (Decisiones, punto 14):

| Línea | Hoy | Queda |
| --- | --- | --- |
| 112 | `'No pudimos conectarnos con el servidor. Revisá tu conexión e intentá de nuevo.'` | `'No pudimos conectarnos con el servidor. Revisa tu conexión e intenta de nuevo.'` |
| 117 | `'No tenés permiso para ver las ventas de este espacio de trabajo.'` | `'No tienes permiso para ver las ventas de este espacio de trabajo.'` |
| 121 | `'Esta venta ya no existe. Volvé al listado.'` | `'Esta venta ya no existe. Vuelve al listado.'` |
| 136 | `'Revisá los filtros aplicados y volvé a intentar.'` | `'Revisa los filtros aplicados y vuelve a intentar.'` |
| 140 | `'El servidor tuvo un problema al traer las ventas. Intentá de nuevo en un momento.'` | `'El servidor tuvo un problema al traer las ventas. Intenta de nuevo en un momento.'` |

Los demás textos de la función («El estado por el que estás filtrando no existe.», etc.) ya tutean y no cambian.

- [ ] **Step 1: Crear el worktree y la rama**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git fetch origin
git worktree add ..\qep-frontend-worktrees\tuteo-ventas -b fix/tuteo-ventas origin/develop
Set-Location ..\qep-frontend-worktrees\tuteo-ventas
git branch --show-current
git log --oneline -1
bun install
Select-String -Path src\features\sales -Pattern "Revisá tu conexión|tenés permiso para ver las ventas|Volvé al listado|Revisá los filtros aplicados|traer las ventas\. Intentá" -Recurse
```

Esperado:
- la rama es `fix/tuteo-ventas`;
- el `log` muestra `08c6158`, que es `origin/develop`;
- `bun install` termina sin errores;
- `Select-String` encuentra exactamente ocho líneas:
  - `sales.api.ts`: 112, 117, 121, 136 y 140;
  - `sales-list-page.test.tsx`: 239;
  - `sale-detail-page.test.tsx`: 164 y 179.

Si aparece otra dentro de `src\features\sales`, la actualizas también en el Step 2. Las coincidencias de otras features son del barrido aparte y no se tocan.

- [ ] **Step 2: Cambiar las pruebas para que fallen**

En `src/features/sales/pages/sales-list-page.test.tsx`, línea 239:

```tsx
        'No tienes permiso para ver las ventas de este espacio de trabajo.',
```

En `src/features/sales/pages/sale-detail-page.test.tsx`, líneas 164 y 179, el texto pasa a `'Esta venta ya no existe. Vuelve al listado.'` en las dos.

- [ ] **Step 3: Correrlas y verlas fallar**

Run: `bun run test src/features/sales/pages/sales-list-page.test.tsx src/features/sales/pages/sale-detail-page.test.tsx`

Esperado: `explains a permission failure instead of a generic error` falla con `Unable to find an element with the text: No tienes permiso para ver las ventas de este espacio de trabajo.`.

Si la prueba de `sale-detail-page.test.tsx` sigue pasando, es porque recibe el mensaje desde un doble (la línea 164 es el valor del doble) y no depende del texto de `describeSalesFailure`. En ese caso la actualización solo mantiene el doble al día con el copy real. Pega la salida tal cual, sin forzar un RED que no existe.

- [ ] **Step 4: Cambiar los cinco textos**

En `src/features/sales/services/sales.api.ts`, dentro de `describeSalesFailure`:

```ts
    return 'No pudimos conectarnos con el servidor. Revisa tu conexión e intenta de nuevo.'
```

```ts
      return 'No tienes permiso para ver las ventas de este espacio de trabajo.'
```

```ts
      return 'Esta venta ya no existe. Vuelve al listado.'
```

```ts
      return 'Revisa los filtros aplicados y vuelve a intentar.'
```

```ts
      return 'El servidor tuvo un problema al traer las ventas. Intenta de nuevo en un momento.'
```

Van en las líneas 112, 117, 121, 136 y 140, en ese orden. Ubícalas por contenido si se corrieron.

- [ ] **Step 5: Verificar**

```powershell
bun run test src/features/sales
bun run test
bun run lint
bun run build
Select-String -Path src\features\sales\services\sales.api.ts -Pattern "Revisá|tenés|Volvé|volvé|Intentá|intentá"
```

Esperado:
- las pruebas de `src/features/sales` pasan;
- la suite completa de Vitest pasa, salvo la falla conocida de `editar.test.tsx` (el timeout del toast de WhatsApp), que ya estaba en el baseline;
- `oxlint` sale sin errores;
- `tsc -b && vite build` termina bien;
- el `Select-String` final no devuelve nada.

- [ ] **Step 6: Commit** (Git Bash, en el worktree `qep-frontend-worktrees/tuteo-ventas`)

```bash
test "$(git branch --show-current)" = "fix/tuteo-ventas" || { echo ABORT; exit 1; }
git add src/features/sales/services/sales.api.ts \
  src/features/sales/pages/sales-list-page.test.tsx \
  src/features/sales/pages/sale-detail-page.test.tsx
git status --short
git commit -m "fix(sales): tuteo en los errores del listado de ventas" \
  -m "describeSalesFailure hablaba de vos en cinco mensajes: sin conexión, sin permiso, venta inexistente, filtros inválidos y error del servidor. Pasan a tuteo, igual que el resto del copy nuevo. El voseo de las demás features queda para el barrido aparte (spec 2026-09-13 del backend, A9)."
git log -1 --format=%B | grep -ci "co-authored-by"
```

Esperado: `0`. La rama no se publica. El push y el PR a `develop` los decide el developer.

---

## Cobertura del spec

| Punto del spec | Tarea |
| --- | --- |
| A1: reclamo en el inbox con lease y la PK como único ganador | 1, 2, 4 |
| A2 y A3: descartados | — (no se implementan) |
| A4: el loop sale sólo por apagado; el `catch` interno ya no excluye `OperationCanceledException` salvo con apagado | 4 (`ExecuteAsync`, `SendAsync`) |
| A5: `HttpClient` de Infobip de 30 s, con el lease por encima | 6 |
| A6: base común `OutboxDeliveryWorker` | 4, 5 |
| Constantes: lease de 2 min, 3 intentos, lote de 20, tick de 3 s | 4 |
| Candidatos, reclamo con `IClock`, envío y `processed_at` en un solo `SaveChanges` | 2, 4 |
| Mensaje envenenado (`attempts > 3`) | 4 |
| Aislamiento por mensaje con `ChangeTracker.Clear()` | 4 |
| Migración: `processed_at` nullable, `claimed_until`, `attempts` con default 1 | 1 |
| `DrainAsync` internal, `InternalsVisibleTo` y el interruptor del harness | 2, 3, 4 |
| Pruebas: duplicados, timeout, lease vencido, envenenado, aislamiento | 4 |
| Pruebas: `InvitationNotificationTests` y `QuotationsExportNotificationTests` en verde sin cambios | 1, 4, 5, 6 |
| A7 y A8: etiquetas del Excel y la API en inglés | 7, 8 |
| La prueba de `Enum.GetValues` sobre los tres enums | 7 |
| La línea que se agrega a D8 del spec anterior | 8 |
| A9: voseo, sólo `describeSalesFailure` (sus cinco textos) | 15 |
| A10: interruptor, validador, tenant propio, `BackgroundService`, SQL masivo, 30 % en venta, 12 meses, idempotencia, 50.000 por defecto al medir | 9, 10, 11, 13, 14 |
| Limpieza, `ops/export-load-cleanup.sql` y regla de lifecycle de R2 | 13, 14 |
| Medición en producción y reemplazo del riesgo | 12, 14 |
| Verificación: TDD, suite por nombre contra el baseline, ArchitectureTests y `restore --locked-mode` | 0, 6, 8, 13 |
| Fuera de alcance: los otros workers, el barrido de voseo, unificar etiquetas del frontend, la clave de idempotencia ante Infobip | — (no se tocan) |
