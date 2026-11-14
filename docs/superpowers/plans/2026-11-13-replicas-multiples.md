# Réplicas múltiples de la API (backend) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan. Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: un implementador por **bloque** (tres bloques), una revisión por bloque y la revisión fuerte de toda la rama una sola vez al final. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que la API corra con `replicas: 2` sin comportarse distinto a como lo hace con una: migraciones y semilla serializadas entre pods, un `startupProbe` que aguanta la espera, todo worker y todo estado en memoria clasificado por una prueba, y el Deployment con dos réplicas y su PDB.

**Architecture:** Antes de `app.RunAsync()`, `Program.cs` envuelve las 24 inicializaciones y `RunQepSeedAsync` en una sola llamada de Bootstrapper que abre una conexión directa sin pool (resuelta como la del listener), abre una transacción y toma `pg_advisory_xact_lock(20261113, 1)`; el trabajo corre en sus propias conexiones del pool, y el `COMMIT`/`ROLLBACK`/cierre suelta el candado. Una prueba de `ArchitectureTests` enumera los hosted services y los singletons con estado del contenedor real (`AddQepPlatform`) y exige que cada uno esté en una lista de clasificación; otra lee los manifiestos y prohíbe `replicas > 1` mientras quede algo `Unsafe`, y el PDB sin dos réplicas. El último bloque cambia los manifiestos, y sólo se hace con los prerrequisitos de despliegue cumplidos.

**Tech Stack:** .NET 10, EF Core 10.0.13 + Npgsql 10.0.3, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`, `Microsoft.AspNetCore.SignalR.Client`, manifiestos de Kubernetes aplicados por `azure-pipelines.yml`.

**Spec:** [`docs/superpowers/specs/2026-10-29-replicas-multiples-design.md`](../specs/2026-10-29-replicas-multiples-design.md) (D-2, D-3, D-4, D-5, D-6 y la prueba de backend de D-1). D-7 y DP-1 salen del spec de Valkey; DP-2 está cerrada sin cambios de pool.

---

## Antes de empezar

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/replicas-multiples`, rama `feature/replicas-multiples`. Rutas absolutas siempre: el cwd del shell vuelve al checkout principal. Antes de **cada** commit, `git -C $W branch --show-current` tiene que decir `feature/replicas-multiples`.
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- RED de compilación: la evidencia es la salida de `dotnet build <proyecto de pruebas>`, sin contenedores. Integración sólo cuando el fallo esperado no es de compilación, con `$env:Seed__Enabled = "false"` en la sesión, filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. La clase completa, una vez al final del bloque. Pruebas siempre en primer plano. Antes de una corrida con Testcontainers: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, espera — es de otra sesión, no la detengas.
- **Suite completa antes de mergear a `develop`:** el cambio es transversal (`Program.cs`, Bootstrapper), así que corre **toda la solución**, proyecto por proyecto, uno a la vez:
  ```powershell
  $env:Seed__Enabled = "false"
  Get-ChildItem "$W/tests" -Recurse -Filter *.csproj | ForEach-Object { dotnet test $_.FullName -- xUnit.MaxParallelThreads=4 }
  ```
- Atajos: `$W` = la ruta del worktree; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`; `$AT` = `$W/tests/ArchitectureTests/ArchitectureTests`; `$TI` = `$W/tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`; `$MI` = `$W/tests/Modules/Messaging/Modules.Messaging.IntegrationTests`.
- **Prerrequisitos fuera de este plan** (no se implementan acá; el Bloque C no arranca sin ellos):
  - Plan de Valkey, base, presencia y rate limits: [`2026-11-13-valkey-base-presencia-rate-limits.md`](2026-11-13-valkey-base-presencia-rate-limits.md) (D-7 presencia y DP-1 limitadores del spec [`2026-11-13-valkey-estado-compartido-design.md`](../specs/2026-11-13-valkey-estado-compartido-design.md)). Las áreas `Presence` y `RateLimiting` prendidas y verificadas en producción, con `qep_valkey_connected` = 1 en el pod.
  - Plan de optimizaciones de Valkey: [`2026-11-13-valkey-optimizaciones.md`](2026-11-13-valkey-optimizaciones.md). No bloquea D-4; sus singletons nuevos, si ya están mergeados, entran en la clasificación de la Tarea 5.
  - **Frontend D-1** (`qep-frontend`, `src/features/messaging/services/messaging-hub.ts`: `skipNegotiation: true` y `transport: HttpTransportType.WebSockets`, con su RED en `messaging-hub.test.ts`). Es otro repo con su propio developer: necesita **su propio spec y su propio plan en ese repo**, según las reglas del workspace. Al 2026-11-13 no está hecho.
- `docs/estado.md` no se toca en esta rama: se actualiza en `develop`, al mergear.
- **Cómo se mergea:** Bloques A y B son el paso 1 del despliegue del spec (backend con `replicas: 1`) y se mergean a `develop` sin esperar a nadie. El Bloque C es el paso 3: se hace en esta rama sólo si ese día ya se cumplen sus prerrequisitos; si no, en una rama nueva desde `develop` cuando se cumplan. Llevar `develop` a `main` lo hace el owner.

## Global Constraints

- Candado de arranque: `pg_advisory_xact_lock(20261113, 1)`, forma de dos enteros. Constantes `StartupLock.Namespace = 20261113` y `StartupLock.Key = 1`.
- El candado vive dentro de **una transacción explícita** que dura todo el bloque migraciones + semilla, sobre una conexión propia con `Pooling = false`, de un solo uso. Nunca `pg_advisory_lock` de sesión ni `pg_advisory_unlock`.
- Conexión del candado: la misma regla de `ListenerConnectionString.Resolve` (`Modules.Messaging.Infrastructure/Realtime/ListenerConnectionString.cs:20-31`): `ConnectionStrings:QepDatabaseListener` si existe, no está vacía (tras `Trim`) y no es `#{…}#`; si no, `QepDatabase`; sin `QepDatabase`, `InvalidOperationException("Connection string 'QepDatabase' is required.")`.
- Las migraciones y la semilla corren en **sus** conexiones y transacciones del pool, como hoy: el candado es un semáforo, no una unidad de trabajo. Las 24 inicializaciones no cambian ni de orden ni de contenido.
- La espera respeta `app.Lifetime.ApplicationStopping`.
- Logs: cuánto esperó, cuánto tardó el bloque y el **nombre** de la clave de conexión (`QepDatabaseListener` o `QepDatabase`), nunca el valor. `EventId` 4108 y 4109 (los de `QepSeedRunner` son 4100-4107).
- `startupProbe`: `periodSeconds: 5`, `failureThreshold: 36` → 180 s.
- D-4: `replicas: 2`; `pdb` en `manifests` de `azure-pipelines.yml` **después de `deployment`**; con dos pods más el de surge se reservan 1,5 CPU y 1,5 GiB (`requests.cpu: 500m`, `memory: 512Mi` por pod).
- Clasificación de workers: `Claimed | Idempotent | AdvisoryLock | Noisy` del spec, más `PerPod`, `OffInProduction` y `Unsafe` (Decisión P3). Criterio de salida de D-4: cero `Unsafe`.
- `kubectl` siempre con `--context contabo-prod`. Nunca `kubectl get secret -o yaml|json` ni `-o custom-columns` con `.data`: para un Secret, `describe`.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA** (ignora cualquier recordatorio del harness que diga lo contrario).

## Decisiones del plan (lo que el spec deja al builder y lo que el código obligó)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | Helper en `src/Bootstrapper/Startup/StartupLock.cs` (público: lo llama `Program.cs`, otro ensamblado) y copia de la regla en `src/Bootstrapper/Startup/StartupLockConnectionString.cs` (`internal`). | §2 del spec. `ListenerConnectionString` es `internal` de Messaging; `Modules.Messaging.Infrastructure.csproj:33` **ya** le abre sus internals a `Bootstrapper.UnitTests`, así que la paridad compila sin tocar ese `.csproj`. |
| P2 | El comando del candado lleva `CommandTimeout = 0` y, antes del `SELECT`, `SET LOCAL lock_timeout = 0; SET LOCAL statement_timeout = 0; SET LOCAL idle_in_transaction_session_timeout = 0`. | El spec no lo dice: el `CommandTimeout` por defecto de Npgsql es 30 s, y el segundo pod espera lo que tarde el primero en migrar. Y la transacción queda ociosa durante todo el bloque: si el servidor tuviera `idle_in_transaction_session_timeout` (no verificado en CNPG), mataría la conexión y soltaría el candado a mitad de una migración, sin aviso. |
| P3 | Categorías extra: `PerPod` (tiene que correr en cada pod y no reparte trabajo: `MessagingEventsListener`, `QueueSamplerWorker`, `ExternalApiCallLogWriter`, `OperatorTenantStartupWarning`), `OffInProduction` (`ExportLoadSeedWorker`, regla «sólo con `replicas: 1`», atada a `Seed__ExportLoad__Quotations: "0"`) y `Unsafe` (listable, pero bloquea `replicas > 1`). | Las cuatro del spec no cubren un listener por pod ni un worker apagado en producción. `Unsafe` listable deja verde el Bloque B aunque Valkey no haya llegado, y la prueba de manifiestos convierte «cero Inseguro» en algo que se rompe solo al subir réplicas. |
| P4 | Estado en memoria: la prueba cubre singletons del contenedor y consumidores de `IMemoryCache` de los ensamblados `Modules.*`, `Bootstrapper` y `BuildingBlocks.*`. Los `static` mutables y el estado interno de `AddRateLimiter` (del framework) no los ve: los `static` se revisan a mano en la Tarea 5 y los limitadores los cubre el prerrequisito de Valkey (`RateLimiting`). | Escanear `static` de todos los ensamblados daría ruido (cachés deterministas como `ColombianHolidays`) sin cambiar la decisión. |
| P5 | La lista de clasificación va por `Type.FullName` (string), no por `typeof`. | Casi todos los workers son `internal` de su módulo. Una entrada que ya no está registrada pone roja otra prueba, así que el string no queda viejo en silencio. |
| P6 | El tipo de un hosted service registrado por fábrica (`AddHostedService(sp => new AuditProjectionWorker(…))`) se lee de `descriptor.ImplementationFactory.GetType().GenericTypeArguments[1]`. Si da `IHostedService` u `object`, la prueba falla pidiendo una fábrica tipada. | `ImplementationType` es `null` en ese caso. |
| P7 | `preStop`: `exec: ["sleep", "10"]` (la imagen `aspnet:10.0.13` es Debian y trae `sleep`). `terminationGracePeriodSeconds: 45` (owner, 2026-11-13): con el default (30 s) y el `preStop` de 10, al apagado del host le quedarían 20 s, menos que el `ShutdownTimeout` por defecto de .NET (30 s; el repo no lo cambia). Con 45 quedan 35. | §4: «no bloquea D-4; va en el mismo commit si cabe». |

## Review Focus

1. **La espera del segundo pod muere a los 30 s.** El `CommandTimeout` por defecto de Npgsql corta el `SELECT pg_advisory_xact_lock` y el pod entra en reinicio justo cuando el primero está migrando. Tarea 2 (`TheWaitOutlastsTheCommandTimeout`).
2. **El candado se suelta a mitad del bloque.** Una transacción ociosa por minutos con `idle_in_transaction_session_timeout` del servidor; o un helper que confirma antes de tiempo. Tarea 2 (`TheLockStaysHeldWhileTheWorkRunsDespiteAnIdleTransactionTimeout`).
3. **El candado pasa por PgBouncer sin que nadie lo vea.** La copia de la regla diverge de la del listener (un `Trim` o un `#{…}#` de menos). Tarea 1 (`TheLockConnectionDoesNotGoThroughThePooler`) y Tarea 3 (`TheStartupLockLogNamesTheKeyAndNotTheValue`, que además amarra que el valor no sale al log).
4. **Un worker nuevo de otra sesión entra sin clasificar, o una entrada de la lista queda vieja.** Tarea 5 (`EveryHostedServiceIsClassified`, `EveryStatefulSingletonIsClassified`, `EveryClassificationStillMatchesARegistration`).
5. **Revertir a una réplica deja el PDB, o se suben réplicas con algo `Unsafe`.** Tarea 5 (`MoreThanOneReplicaRequiresNoUnsafeEntry`) y Tarea 8 (`ThePdbIsDeployedIfAndOnlyIfThereAreTwoOrMoreReplicas`); el `delete pdb` del rollback queda en la Tarea 8.

---

## Bloque A — candado de arranque y presupuesto del probe (Tareas 1–4, un implementador)

Toca Bootstrapper, `Program.cs`, el Deployment y comentarios. Al final del bloque: `StartupLockTests` y `StartupLockConnectionStringParityTests` completas (`$BU`), `SeedStartupTests` completa (`$TI`) y `Bootstrapper.UnitTests` entero.

### Tarea 1: la conexión del candado, copia y paridad

**Files:**
- Create: `src/Bootstrapper/Startup/StartupLockConnectionString.cs`
- Test: `$BU/StartupLockConnectionStringParityTests.cs`

**Interfaces — Produces:**
```csharp
namespace Bootstrapper.Startup;
internal static class StartupLockConnectionString
{
    internal const string DirectKey = "QepDatabaseListener";
    internal const string DefaultKey = "QepDatabase";
    internal static (string ConnectionString, string KeyName) Resolve(IConfiguration configuration);
}
```
El `<summary>` dice por qué es una copia (Messaging la tiene `internal`) y que la amarra esta prueba, como `ShopifyWebhookTopicsParityTests`.

- [ ] **RED.** En `StartupLockConnectionStringParityTests`:
  ```csharp
  [Theory]
  [InlineData("Host=direct;Database=qep")]                    // presente → (valor recortado, "QepDatabaseListener")
  [InlineData("   Host=direct;Database=qep   ")]
  [InlineData("")]                                            // vacía → (QepDatabase, "QepDatabase")
  [InlineData("   ")]
  [InlineData("#{DATABASE_LISTENER_CONNECTION_STRING}#")]     // token sin resolver → QepDatabase
  [InlineData(null)]                                          // ausente → QepDatabase
  public void TheLockConnectionDoesNotGoThroughThePooler(string? listener)
  // config con QepDatabase = "Host=pooler;Database=qep" y, si listener no es null, QepDatabaseListener = listener
  // Assert.Equal(ListenerConnectionString.Resolve(config), StartupLockConnectionString.Resolve(config));

  [Fact] public void BothRequireQepDatabase()   // sin ninguna clave: las dos lanzan InvalidOperationException con el mismo Message
  [Fact] public void BothUseTheSameKeyNames()   // DirectKey == ListenerConnectionString.ListenerKey; DefaultKey == ListenerConnectionString.DefaultKey
  ```
- [ ] `dotnet build $BU` → falla con `CS0103 The name 'StartupLockConnectionString' does not exist in the current context`.
- [ ] **GREEN.** Implementa `Resolve` con la regla de los Global Constraints.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~StartupLockConnectionStringParityTests"` → verde (no levanta contenedores).
- [ ] Commit: `feat(bootstrapper): resolve the startup lock connection like the messaging listener`

### Tarea 2: `StartupLock`, el semáforo

**Files:**
- Create: `src/Bootstrapper/Startup/StartupLock.cs`
- Modify (sólo comentarios): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantChangeLock.cs:13-15`, `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsConversationLock.cs:13-15`, `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/ShopifyTokenLock.cs:3-9` — cada uno anota los demás espacios de dos claves, incluido `StartupLock.Namespace` = 20261113 (hoy `TenantChangeLock` dice «hoy no hay otro» y `AutomationsConversationLock` sólo nombra a `TenantChangeLock`: corrígelos).
- Test: `$BU/StartupLockTests.cs` (Testcontainers, `postgres:18-alpine`, como `CatalogVariantUsageCheckerTests.cs:146`; un contenedor por clase con `IClassFixture`; actualiza el comentario del `.csproj` que dice que `CatalogVariantUsageCheckerTests` es la única prueba con Docker)

**Interfaces:**
- Consumes: `StartupLockConnectionString.Resolve` (Tarea 1).
- Produces:
```csharp
namespace Bootstrapper.Startup;
public static partial class StartupLock
{
    /// Primera clave de dos: la fecha del spec revisado. Comentario con los otros espacios (20261008, 20261022, 20261112).
    public const int Namespace = 20261113;
    public const int Key = 1;

    // Program.cs: resuelve IConfiguration e ILoggerFactory de `services`, la conexión con StartupLockConnectionString,
    // y corre work(services, ct) con el candado tomado.
    public static Task RunUnderStartupLockAsync(
        this IServiceProvider services,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken);

    // El núcleo, para las pruebas.
    internal static Task RunAsync(
        string connectionString, string keyName, ILogger logger,
        Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}
```
Orden dentro de `RunAsync`: `NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }` → `OpenAsync(ct)` → `BeginTransactionAsync(ct)` → los tres `SET LOCAL` de P2 → `SELECT pg_advisory_xact_lock(@namespace, @key)` con `CommandTimeout = 0` y `ct` → log 4108 → `work(ct)` → log 4109 → en `finally`, `RollbackAsync` (no se escribió nada) y dispose de la transacción y la conexión. Logs con `[LoggerMessage]`:
- 4108 Information: `"Startup lock acquired after {WaitMilliseconds} ms using connection string key {ConnectionStringKey}."`
- 4109 Information: `"Startup migrations and seed finished in {ElapsedMilliseconds} ms under the startup lock."`

Helper de prueba (en la clase de pruebas): `HoldLockAsync(connectionString)` abre su propia conexión y transacción, toma `pg_advisory_xact_lock(StartupLock.Namespace, StartupLock.Key)` y devuelve un `IAsyncDisposable` que hace `COMMIT`; `IsFreeAsync(connectionString)` corre `pg_try_advisory_xact_lock(20261113, 1)` en su propia transacción y la deshace.

- [ ] **RED.** En `StartupLockTests`:
  ```csharp
  [Fact] public async Task TheStartupWorkWaitsWhileAnotherInstanceHoldsTheLock()
  // await using var held = await HoldLockAsync(cs); var started = false;
  // var run = StartupLock.RunAsync(cs, "QepDatabase", logger, _ => { started = true; return Task.CompletedTask; }, Ct);
  // await Task.Delay(TimeSpan.FromSeconds(2), Ct); Assert.False(started); Assert.False(run.IsCompleted);
  // await held.DisposeAsync(); await run.WaitAsync(TimeSpan.FromSeconds(10), Ct); Assert.True(started);

  [Fact] public async Task TheLockIsReleasedWhenTheStartupWorkThrows()
  // await Assert.ThrowsAsync<InvalidOperationException>(() => StartupLock.RunAsync(cs, ..., _ => throw new InvalidOperationException("boom"), Ct));
  // Assert.True(await IsFreeAsync(cs));

  [Fact] public async Task CancellingWhileWaitingStopsWithoutTakingTheLock()
  // con el candado tomado por la prueba y un CTS que cancela a los 500 ms:
  // await Assert.ThrowsAnyAsync<OperationCanceledException>(...); el work no corrió;
  // tras soltar el de la prueba, Assert.True(await IsFreeAsync(cs)).

  [Fact] public async Task TheWaitOutlastsTheCommandTimeout()
  // connectionString con "Command Timeout=1"; el candado tomado 3 s por la prueba y luego soltado;
  // RunAsync termina sin excepción y el work corrió.

  [Fact] public async Task TheLockStaysHeldWhileTheWorkRunsDespiteAnIdleTransactionTimeout()
  // ALTER DATABASE qep SET idle_in_transaction_session_timeout = '1s' (conexión nueva para que aplique);
  // work: await Task.Delay(3 s); luego Assert.False(await IsFreeAsync(cs)) desde dentro del work.
  // RunAsync termina sin excepción; después, Assert.True(await IsFreeAsync(cs)).
  ```
- [ ] `dotnet build $BU` → falla con `CS0103 The name 'StartupLock' does not exist in the current context`.
- [ ] **GREEN.** Implementa `StartupLock` y corrige los tres comentarios.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~StartupLockTests" -- xUnit.MaxParallelThreads=4` → 5 verdes. Si sacas `CommandTimeout = 0` o los `SET LOCAL`, `TheWaitOutlastsTheCommandTimeout` o la del timeout ocioso tienen que ponerse rojas: compruébalo una vez y deja la evidencia en el reporte del bloque.
- [ ] Commit: `feat(bootstrapper): add a transaction-scoped advisory lock for startup work`

### Tarea 3: `Program.cs` corre migraciones y semilla bajo el candado

**Files:**
- Modify: `src/Api/Program.cs:331-429` (las 24 `Initialize…DatabaseAsync` y `RunQepSeedAsync`, con sus comentarios, pasan dentro del lambda; `app.RunAsync()` en `:431` queda afuera)
- Test: `$TI/SeedStartupTests.cs` (usa su `QepApiFactory`, `StartDatabaseAsync` y `CapturingLoggerProvider`)

**Interfaces:**
- Consumes: `StartupLock.RunUnderStartupLockAsync`, `StartupLock.Namespace`, `StartupLock.Key` (Tarea 2).
- Produces: nada nuevo; la forma en `Program.cs` es
  ```csharp
  await app.Services.RunUnderStartupLockAsync(async (services, cancellationToken) =>
  {
      await services.InitializeTenancyDatabaseAsync(cancellationToken);
      // … las 23 restantes en el mismo orden, con sus comentarios …
      await services.RunQepSeedAsync(cancellationToken);
  }, app.Lifetime.ApplicationStopping);
  ```
  más un comentario arriba que cite el spec (D-2) y diga por qué es `xact` y no de sesión.

- [ ] **RED.** En `SeedStartupTests`:
  ```csharp
  [Fact] public async Task TheHostWaitsForTheStartupLock()
  // con el candado tomado desde una conexión de la prueba (BEGIN + pg_advisory_xact_lock(StartupLock.Namespace, StartupLock.Key)):
  // var start = Task.Run(() => factory.CreateClient()); await Task.Delay(TimeSpan.FromSeconds(3), Ct);
  // Assert.False(start.IsCompleted);
  // Assert.True(await ScalarIsNullAsync("SELECT to_regclass('tenancy.tenants')"));   // Tenancy, la primera migración, no corrió
  // COMMIT; using var client = await start.WaitAsync(TimeSpan.FromMinutes(2), Ct); → el host responde GET /health/live 200.

  [Fact] public async Task TheStartupLockLogNamesTheKeyAndNotTheValue()
  // factory con logs: var entry = Assert.Single(logs.Entries, e => e.EventId == 4108);
  // Assert.Contains("QepDatabase", entry.Message); Assert.DoesNotContain("qep-integration", entry.Message);  // la contraseña del contenedor
  // Assert.Single(logs.Entries, e => e.EventId == 4109);

  [Fact] public async Task TwoConcurrentStartupsSeedOnce()
  // misma base vacía, dos QepApiFactory(seedEnabled: true);
  // await Task.WhenAll(Task.Run(() => a.CreateClient()), Task.Run(() => b.CreateClient()));  // sin excepción
  // Assert.Equal(1, tenants con Slug == "origen-botanico"); Assert.Equal(1, usuarios con email "semilla@qcode.co")
  ```
  `TwoConcurrentStartupsSeedOnce` puede pasar por suerte antes del cambio: es amarre, no RED (spec, «Pruebas», 4).
- [ ] Con `$env:Seed__Enabled = "false"`: `dotnet test $TI --filter "FullyQualifiedName~TheHostWaitsForTheStartupLock|FullyQualifiedName~TheStartupLockLogNamesTheKeyAndNotTheValue" -- xUnit.MaxParallelThreads=4` → las dos rojas: la primera en `Assert.False(start.IsCompleted)`, la segunda en `Assert.Single` (ninguna entrada 4108).
- [ ] **GREEN.** Envuelve el bloque en `Program.cs` sin cambiar orden ni contenido (compara con `git diff -w`: sólo indentación, el lambda y su comentario).
- [ ] Las tres por nombre → verdes.
- [ ] Commit: `feat(api): serialize startup migrations and seed across pods`

### Tarea 4: presupuesto del `startupProbe` (D-3)

**Files:**
- Modify: `k8s/prod-deployment.yaml:53` (`failureThreshold: 12` → `36`), con un comentario sobre el `startupProbe`: 180 s, sólo cuenta hasta el primer éxito, cubre dos arranques seguidos por el candado de D-2, y se reajusta con el log 4109 (si el bloque pasa de 60 s en producción, volver a la alternativa del `Job`).
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeedWorker.cs:11-14` — «60 s como máximo (prod-deployment.yaml: 12 intentos cada 5 s)» pasa a 180 s (36 intentos cada 5 s), y que un segundo pod gasta parte de eso esperando el candado de arranque.
- Modify: `README.md:492` («El `startupProbe` le da al pod 60 s») y `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs:361` (comentario «60 s como máximo»): mismo cambio. El spec sólo nombra el del worker; estos dos dicen lo mismo y quedarían viejos.

Sin prueba: el spec verifica D-3 en el clúster.

- [ ] Haz los cambios.
- [ ] `Select-String -Path "$W/k8s/prod-deployment.yaml" -Pattern "failureThreshold: 36"` → una línea; `Get-ChildItem $W -Recurse -Include *.cs,*.md,*.yaml | Where-Object { $_.FullName -notmatch 'superpowers|\\bin\\|\\obj\\' } | Select-String -Pattern "12 intentos|le da al pod 60 s|60 s como máximo"` → nada.
- [ ] `dotnet build $W/tests/Modules/Quotations/Modules.Quotations.IntegrationTests` → compila.
- [ ] Commit: `chore(k8s): raise the startup probe budget to 180 s`

**Cierre del Bloque A:** clases completas `StartupLockTests`, `StartupLockConnectionStringParityTests`, `SeedStartupTests`; luego `dotnet test $BU -- xUnit.MaxParallelThreads=4`. Revisión del bloque.

---

## Bloque B — inventario, contrato del hub y reglas escritas (Tareas 5–7, un implementador)

Toca `tests/ArchitectureTests`, `MessagingHubTests`, README y `CLAUDE.md`. Al final: `ArchitectureTests` entero y `MessagingHubTests` completa.

### Tarea 5: inventario D-6 y prueba de arquitectura

**Files:**
- Create: `$AT/MultiReplicaInventory.cs` (la lista: la «constante en el proyecto de pruebas» del spec)
- Create: `$AT/MultiReplicaInventoryTests.cs`
- Modify: este plan, sección «Inventario D-6» (al final), con la fecha, el commit y el conteo del día.

**Interfaces — Produces:**
```csharp
namespace ArchitectureTests;
public enum WorkerClassification { Claimed, Idempotent, AdvisoryLock, Noisy, PerPod, OffInProduction, Unsafe }
public enum StateClassification { PerPodByDesign, SharedWithLocalFallback, Unsafe }
public sealed record WorkerEntry(string TypeFullName, WorkerClassification Classification, string Evidence);   // Evidence: "archivo:línea — mecanismo"
public sealed record StateEntry(string TypeFullName, StateClassification Classification, string Evidence);
public static class MultiReplicaInventory
{
    public static readonly IReadOnlyList<WorkerEntry> Workers;
    public static readonly IReadOnlyList<StateEntry> State;
}
```
Reglas de detección (en `MultiReplicaInventoryTests`, sobre `services.AddQepPlatform(MinimalConfiguration(), new TestHostEnvironment())` — copia de `CompositionRootTests.cs:947-969`):
- **Nuestros tipos:** ensamblado cuyo nombre empieza por `Modules.`, `Bootstrapper` o `BuildingBlocks`. Lo del framework (OpenTelemetry, health checks…) no entra.
- **Hosted services:** descriptores con `ServiceType == typeof(IHostedService)`; tipo por `ImplementationType`, o por la fábrica (P6).
- **Singletons con estado:** descriptores `Singleton` de nuestros tipos cuyo tipo (o una base nuestra) declara un campo de instancia que (a) no es `readonly` y cuyo nombre no empieza por `<`, o (b) es, o cierra, `ConcurrentDictionary<,>`, `Dictionary<,>`, `HashSet<>`, `List<>`, `Queue<>`, `ConcurrentQueue<>`, `ConcurrentBag<>`, `Channel<>`, `SemaphoreSlim`, `IMemoryCache` o `MemoryCache` (con cualquier nombre: los parámetros capturados de un constructor primario son campos `<x>P`). Más todo tipo nuestro, de cualquier vida, con un parámetro `IMemoryCache` en el constructor.

- [ ] **Reenumera** en la rama, el mismo día: `Select-String -Path "$W/src" -Recurse -Pattern "AddHostedService"` (cuenta y lista) y los mismos patrones para `IHostedService`, `BackgroundService`, `IMemoryCache`, `AddMemoryCache`, `ConcurrentDictionary`, `SemaphoreSlim`, `Channel.Create` y `static` no `readonly` en `src/`. Anota en «Inventario D-6» la fecha, `git -C $W rev-parse --short HEAD` y el conteo; **no copies el 52 del spec**. Los `static` mutables que aparezcan se clasifican a mano en esa sección (P4).
- [ ] **RED.** En `MultiReplicaInventoryTests`, con `MultiReplicaInventory.Workers` y `.State` vacíos:
  ```csharp
  [Fact] public void EveryHostedServiceIsClassified()
  // Assert.True(missing.Count == 0, "Estos hosted services no dicen cómo conviven con otro pod. Agrégalos a "
  //   + "MultiReplicaInventory.Workers con su clasificación y evidencia (spec 2026-10-29 D-6): " + string.Join(", ", missing));

  [Fact] public void EveryStatefulSingletonIsClassified()          // mismo mensaje, hacia MultiReplicaInventory.State
  [Fact] public void EveryClassificationStillMatchesARegistration()  // toda entrada de Workers es un hosted service registrado y toda de State un tipo detectado; si no, «quítala»
  [Fact] public void EveryEntryCitesItsEvidence()                    // Evidence no vacío y TypeFullName sin repetir en cada lista

  [Fact] public void MoreThanOneReplicaRequiresNoUnsafeEntry()
  // replicas = Regex (?m)^\s{2}replicas:\s*(\d+)\s*$ sobre k8s/prod-deployment.yaml (ruta encontrada subiendo desde AppContext.BaseDirectory, como ObservabilityConfigurationTests.BaseSettingsPath)
  // if (replicas > 1) {
  //   Assert.Empty(Workers.Where(w => w.Classification == WorkerClassification.Unsafe));
  //   Assert.Empty(State.Where(s => s.Classification == StateClassification.Unsafe));
  //   Assert.Matches(@"(?m)^\s*Seed__ExportLoad__Quotations:\s*""0""\s*$", File.ReadAllText(<k8s/prod-configMap.yaml>));  // OffInProduction
  // }
  ```
- [ ] `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests"` → `EveryHostedServiceIsClassified` y `EveryStatefulSingletonIsClassified` rojas con la lista completa de tipos en el mensaje. Esa lista es la evidencia del RED y contrasta con la reenumeración del paso anterior: si difieren, la detección está mal.
- [ ] **GREEN.** Llena las dos listas. Para cada tipo, abre el archivo y verifica el mecanismo; la tabla de partida al final de este plan es evidencia del 2026-11-13, no la fuente de verdad. Criterio (spec D-6): `Claimed` = `FOR UPDATE SKIP LOCKED` o lease; `AdvisoryLock`; `Idempotent` = `DELETE` por corte, `UPSERT`, `ON CONFLICT`; `Noisy` = la segunda instancia choca con una clave única o la concurrencia optimista y sólo deja log o un tick perdido; `Unsafe` = efecto externo duplicado (correo, WhatsApp, cobro, API externa, rotación de token) o fila duplicada. En `State`: `MessagingPresence` y `ApiKeyRateLimiter` son `SharedWithLocalFallback` **sólo** si el plan de Valkey base ya está en la rama (`git -C $W log --oneline develop -- <archivo>`); si no, `Unsafe` con la evidencia «D-7/DP-1: pendiente del plan Valkey base». Un worker nuevo que resulte `Unsafe` se anota y se avisa al owner: no se arregla en este plan.
- [ ] Las cinco por nombre → verdes (con `replicas: 1`, `MoreThanOneReplicaRequiresNoUnsafeEntry` pasa aunque haya `Unsafe`).
- [ ] Commit: `test(architecture): classify hosted services and in-memory state for multiple replicas`

### Tarea 6: el hub acepta un WebSocket sin `negotiate` (contrato de D-1)

**Files:**
- Modify: `$MI/MessagingHubTests.cs`
- Modify (si hace falta, copia local): helper `IssueGoogleIdToken` desde `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs` («cada ensamblado de pruebas tiene su propia copia»), en `MessagingApiHarness.cs`.

**Interfaces — Consumes:** `MessagingApiHarness.QepApiFactory`, `WithRealAuthentication()` (`MessagingApiHarness.cs:901`), `EnableMessagingAsync`, el patrón de `WaitUntilJoinedAsync`.

Amarre, no RED (spec, «Pruebas», D-1): puede pasar de entrada. **Si falla, D-1 no sirve:** para, no lo «arregles» en el servidor, y avísale al owner (plan B del spec: afinidad por cookie en un `Ingress` aparte).

- [ ] Escribe la prueba:
  ```csharp
  [Fact] public async Task TheHubAcceptsAWebSocketWithoutNegotiate()
  // factory = new QepApiFactory(cs).WithRealAuthentication() + UseSetting("Authentication:TestSigningKey", <base64 de 32 bytes>)
  // dueño y tenant por POST /api/v1/auth/register-tenant con Bearer Google de prueba y X-Qep-Client: web (como RealAuthenticationApiTests.RegisterOwnerAndTenantAsync);
  //   la cookie de sesión sale del Set-Cookie de esa respuesta. EnableMessagingAsync(cs, tenantId).
  // var ws = factory.Server.CreateWebSocketClient(); ws.ConfigureRequest = r => r.Headers.Cookie = <cookie>;
  // HubConnection a $"api/v1/hubs/messaging?tenantId={tenantId:D}" con
  //   options.SkipNegotiation = true; options.Transports = HttpTransportType.WebSockets;
  //   options.WebSocketFactory = async (context, ct) => await ws.ConnectAsync(context.Uri, ct);
  //   sin X-Tenant-Id, sin X-Qep-Client, sin X-Subject-Id
  // await connection.StartAsync(Ct); Assert.Equal(HubConnectionState.Connected, connection.State);
  // WaitUntilJoinedAsync(factory, tenantId, Listen(connection)) termina: el evento del tenant llega por el WebSocket.
  ```
- [ ] Con `$env:Seed__Enabled = "false"`: `dotnet test $MI --filter "FullyQualifiedName~TheHubAcceptsAWebSocketWithoutNegotiate" -- xUnit.MaxParallelThreads=4` → verde. Pega la salida literal en el reporte del bloque.
- [ ] Commit: `test(messaging): pin the hub websocket handshake without negotiate`

### Tarea 7: reglas escritas (D-5 y la regla de D-6)

**Files:**
- Modify: `README.md` § «Migraciones por módulo» (`:2561`): subsección nueva «Migraciones con pods viejos y nuevos (expand/contract)» con las tres reglas de §5 del spec, texto casi literal: (1) toda migración es expand/contract — una versión agrega (columna nullable o con default, tabla, índice) y el código viejo la ignora; quitar, renombrar o volver `NOT NULL` algo que el código viejo escribe va en otra versión, cuando no queda ningún pod viejo; (2) una migración que rompe al código viejo se despliega con corte (`Recreate` o el backend a cero réplicas, como las variantes, README `:1947`), y el PDB no lo impide; (3) el handoff de cada spec dice si su migración es aditiva; si no lo dice, se asume que no. Más un párrafo sobre el candado de arranque: qué serializa, el log 4108/4109 y la clave de conexión.
- Modify: `CLAUDE.md` (sección «Gotchas verificados» o «Reglas duras», una entrada cada una):
  - Toda migración es expand/contract con dos pods; una que rompe al código viejo va con corte — enlace al README.
  - Un hosted service o un singleton con estado nuevo se clasifica en `MultiReplicaInventory` **en el mismo commit**; `MultiReplicaInventoryTests` lo exige, y `Unsafe` bloquea `replicas > 1`.
  - Las inicializaciones nuevas van **dentro** del lambda de `RunUnderStartupLockAsync` en `Program.cs`, nunca después: fuera del candado, dos pods las corren a la vez.

Sin prueba (documentación).

- [ ] Escribe los textos tuteando. Revisa que los enlaces de ancla del README resuelvan (`Select-String -Path "$W/README.md" -Pattern "expand/contract"`).
- [ ] Commit: `docs: write the expand/contract migration rule and the replica inventory rule`

**Cierre del Bloque B:** `dotnet test $AT` entero y la clase `MessagingHubTests` completa. Revisión del bloque. **Antes de mergear a `develop` (Bloques A+B):** la suite completa de los Global Constraints, proyecto por proyecto.

---

## Bloque C — dos réplicas y PDB (Tarea 8, un implementador; el último paso)

**No arranca sin todos estos prerrequisitos (spec, «Despliegue», paso 3), verificados el mismo día y anotados en el reporte:**

- [ ] Bloques A y B mergeados a `develop`, llevados a `main` por el owner y **desplegados** con `replicas: 1`; el log 4108 aparece en el pod y dice `QepDatabaseListener`:
  ```powershell
  kubectl --context contabo-prod -n prod-qep-backend get pods -o wide
  kubectl --context contabo-prod -n prod-qep-backend logs <pod> --since=30m | Select-String -Pattern "Startup lock"
  ```
- [ ] Frontend D-1 mergeado y **desplegado** (su plan en `qep-frontend`): en DevTools → Network, sin request a `…/negotiate` y el WebSocket a `/api/v1/hubs/messaging?tenantId=…` responde `101`.
- [ ] Plan de Valkey base desplegado con las áreas `Presence` y `RateLimiting` prendidas y `qep_valkey_connected` = 1 en el pod.
- [ ] La clave `QepDatabaseListener` en el log de arranque de **cada** pod:
  ```powershell
  kubectl --context contabo-prod -n prod-qep-backend get pods -o name | Select-String "qep-backend" | ForEach-Object { "$_"; kubectl --context contabo-prod -n prod-qep-backend logs "$_" --since=24h | Select-String -Pattern "listener is listening" }
  ```
  Si alguno dice `QepDatabase`, se arregla el Secret antes de seguir (sólo `kubectl … describe secret`, nunca `get -o yaml|json`).
- [ ] Capacidad del nodo: 1,5 CPU y 1,5 GiB reservables para la app durante el rollout:
  ```powershell
  kubectl --context contabo-prod describe node | Select-String -Pattern "Allocatable|Allocated resources" -Context 0,8
  ```
- [ ] Inventario D-6 rehecho **ese día** (repite el primer paso de la Tarea 5 sobre `develop` actualizado), lista al día y cero `Unsafe`.

### Tarea 8: `replicas: 2`, PDB y notas de capacidad (D-4)

**Files:**
- Modify: `k8s/prod-deployment.yaml:11` (`replicas: 1` → `2`); `lifecycle.preStop` en el contenedor (P7)
- Modify: `azure-pipelines.yml:49-55` (`- pdb` después de `- deployment`, con un comentario que diga por qué va después y que nunca va sin dos réplicas)
- Modify: `k8s/prod-pdb.yaml:1-3` (el encabezado deja de decir «PREPARADO, NO DESPLEGADO» y la referencia al plan borrado `2026-09-24`; queda la explicación del drain)
- Modify: `README.md:1353-1356` (§ Health check): el PDB sí se despliega con dos réplicas; la reserva de 1,5 CPU y 1,5 GiB durante el rollout; el rollback (revertir no borra el PDB: `kubectl --context contabo-prod -n prod-qep-backend delete pdb qep-backend`); y que el dashboard y las alertas de colas agregan con `max`/`avg`, nunca `sum` (`QueueGauges` por pod; spec, seguimiento).
- Test: `$AT/MultiReplicaInventoryTests.cs`

**Interfaces — Consumes:** `MultiReplicaInventory` y la lectura de manifiestos de la Tarea 5.

- [ ] **RED.** Agrega a `MultiReplicaInventoryTests`:
  ```csharp
  [Fact] public void ThePdbIsDeployedIfAndOnlyIfThereAreTwoOrMoreReplicas()
  // var pdbListed = Regex.IsMatch(<azure-pipelines.yml>, @"(?m)^\s*-\s*pdb\s*$");
  // Assert.Equal(replicas >= 2, pdbListed);
  ```
  y cambia sólo `replicas: 2` en el Deployment.
- [ ] `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests"` → `ThePdbIsDeployedIfAndOnlyIfThereAreTwoOrMoreReplicas` roja (`Expected: True, Actual: False`). Si además se pone roja `MoreThanOneReplicaRequiresNoUnsafeEntry`, **para**: un prerrequisito no se cumple.
- [ ] **GREEN.** `- pdb` en el pipeline, `preStop`, encabezado del PDB y README.
- [ ] `dotnet test $AT` entero → verde.
- [ ] Commit (uno solo, como pide §4 del spec): `feat(k8s): run two replicas with a pod disruption budget`

**Verificación después de desplegar** (la hace quien despliega; el owner lleva a `main`):
```powershell
kubectl --context contabo-prod -n prod-qep-backend get deploy qep-backend
kubectl --context contabo-prod -n prod-qep-backend get pdb
kubectl --context contabo-prod -n prod-qep-backend describe pdb qep-backend
```
A mano: iniciar sesión, navegar y abrir Mensajería en dos pestañas; los logs de los dos pods muestran requests de la misma sesión y un mensaje entrante llega sin recargar en las dos. Durante 15 minutos, revisar los logs: se esperan algunas fallas de `AuditProjectionWorker` o `PaymentProofMoveProcessor` (seguimiento, `Noisy`), no de otros workers.

**Rollback:** revertir el commit vuelve a `replicas: 1`, pero el pipeline no borra el PDB ya aplicado: `kubectl --context contabo-prod -n prod-qep-backend delete pdb qep-backend`. Si hay que revertir el frontend D-1, primero este paso.

---

## Inventario D-6

**Reenumerado el 2026-11-13 en la Tarea 5, commit `2b8655fa` (rama `feature/replicas-multiples`): 54 hosted services**, los mismos 54 en las dos cuentas: las 54 líneas `AddHostedService` de `src/` y lo que `MultiReplicaInventoryTests` detecta en el contenedor real (con la configuración mínima más todas las áreas de Valkey prendidas). Respecto de los 52 de `cf977683` se suman los dos de Valkey de esta rama: `ValkeyConnection` (por fábrica, `ValkeyServiceCollectionExtensions.cs:33`) y `PresenceHeartbeatWorker` (sólo con `Valkey:Areas:Presence`). Ningún `Unsafe`. La fuente de verdad, con archivo, línea y mecanismo de cada uno, es `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`; la Tarea 8 la rehace el día del merge.

| Clasificación | Hosted services |
| --- | --- |
| `Claimed` (33) | `OutboxPublisherWorker`, `ExportJobWorker`, `MediaCopyWorker`, `CampaignDispatchWorker`, `WebhookDeliveryWorker`, `ShopifyWebhookDeliveryWorker`, `AutomationInboxWorker`, `SessionRevocationWorker`, `OrphanUserCleanupWorker`, `QuotationEventsWorker`, `CitySynonymEventsWorker`, `CustomerMediaPurgeWorker`, `CoexistenceEventsWorker`, los 13 `*DeliveryWorker` de Notifications (`Invitation`, `PasswordReset`, `EmailVerification`, `ExistingAccount`, `QuotationsExportReady`, `QuotationsExportFailed`, `ProductExport`, `CustomerExport`, `SupplierExport`, `BuyerVerification`, `BuyerPasswordReset`, `BuyerExistingAccount`, `BuyerAccountDeleted`), `ServiceContractsReminderWorker`, `LoansGenerationWorker`, `BuyerSignupRequestedWorker`, `HistorySyncRequestWorker`, `ScheduledMessageWorker`, `ShopifyCityCorrectionWorker`, `ShopifyReconcileWorker` |
| `AdvisoryLock` (1) | `ShopifyTokenRefreshWorker` |
| `Idempotent` (8) | `ConnectionSecretRekeyWorker`, `CustomerPhoneBackfillWorker`, `StagingCleanupWorker`, `PaymentProofOrphanCleanupWorker`, `WebhookPurgeWorker`, `BuyerHousekeepingWorker`, `ShopifyInstallSweeper`, `ApiKeyRequestPurgeWorker` |
| `Noisy` (5) | `AuditProjectionWorker`, `PaymentProofMoveWorker`, `ProductImageReleaseWorker`, `InstagramTokenRefreshWorker`, `QuotationExpirationWorker` |
| `PerPod` (6) | `MessagingEventsListener`, `QueueSamplerWorker`, `ExternalApiCallLogWriter` (drena el canal de su pod), `OperatorTenantStartupWarning` (sólo advierte al arrancar), `ValkeyConnection`, `PresenceHeartbeatWorker` |
| `OffInProduction` (1) | `ExportLoadSeedWorker` (`Seed__ExportLoad__Quotations: "0"`, `k8s/prod-configMap.yaml:158`) |

`CoexistenceEventsWorker` pasó de `Idempotent` a `Claimed`: el código reclama con `ClaimedOutboxConsumer` (`CoexistenceEventsProcessor.cs:28`), además del `ON CONFLICT` del alta.

| Estado en memoria (2026-11-13, 16 tipos detectados; el escaneo repite también los registros de `Program.cs`) | Clasificación |
| --- | --- |
| `MessagingPresence`, `ApiKeyRateLimiter` | `SharedWithLocalFallback`: el plan Valkey base está en esta rama (`9c0cdfa1`, `e2a946a8`). Compartido **sólo** con `Valkey__Enabled`, `Valkey__Areas__Presence` y `Valkey__Areas__RateLimiting` en `"true"` en el ConfigMap; con `replicas > 1`, `MoreThanOneReplicaRequiresNoUnsafeEntry` lo exige (`MultiReplicaInventory.SharedStateSwitches`) |
| `WebTelemetryMetrics` (Api, `Program.cs:77`) | `PerPodByDesign` (tope de 256 rutas por pod en el histograma) |
| `ExternalApiCallLogChannel` (`Channel.CreateBounded`) | `PerPodByDesign` (se pierde con `SIGKILL`, igual que hoy) |
| `QueueGauges` | `PerPodByDesign` (dashboard con `max`/`avg`) |
| `ValkeyConnection` | `PerPodByDesign` (el multiplexer de cada pod) |
| `WebhookRouting`, `AutomationsModuleStatus` (`IMemoryCache` de 60 s) | `PerPodByDesign` (el retraso de un minuto ya existe con un pod) |
| `ModuleEntitlementMask`, `RoleCatalog`, `SystemTemplateCatalog`, `InstagramAppSettings`, `ShopifyAppSettings`, `ConnectionTesterRegistry`, `MarketingOptOutButtons` | `PerPodByDesign`: tablas inmutables armadas del código o de la configuración al construir; iguales en cada pod |

**`static` mutables (P4), revisados a mano el mismo día:** ningún campo `static` no `readonly` en `src/` (sólo propiedades de sólo lectura). Los `static readonly` con colección son tablas constantes, salvo `ColombianHolidays.Cache` (`static readonly ConcurrentDictionary`, determinista): inofensivo.
