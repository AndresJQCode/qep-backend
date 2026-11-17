# Valkey: base, presencia y limitadores compartidos — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan. Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: tres bloques, **un implementador por bloque** y **una revisión por bloque** (la fuerte, de toda la rama, una sola vez al final); RED de compilación sin contenedores; integración filtrada por nombre mientras iteras y la clase completa una vez al final del bloque; suite completa sólo antes de mergear a `develop` y limitada a los proyectos tocados; nunca dos corridas de integración a la vez; pruebas en primer plano; `$env:Seed__Enabled = "false"`. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el backend tenga un cliente de Valkey listo y observable (D-1), que la presencia de la bandeja se vea igual desde cualquier pod (D-2) y que las siete ventanas fijas de `Program.cs` y el límite por clave de la API externa cuenten en Valkey y no por pod (D-3), todo detrás de flags apagados y con respaldo a lo de hoy.

**Architecture:** `StackExchange.Redis` lo referencia **sólo Bootstrapper**. Ahí viven `ValkeyConnection` (el multiplexer singleton, conectado en `StartAsync` con `AbortOnConnectFail = false`), `ValkeyGateway` (timeout por operación, contador de respaldos, corto circuito si no hay conexión), `ValkeyMetrics` y los adaptadores de cada área en `src/Bootstrapper/Valkey/`. La presencia gana un puerto de lectura en `Messaging.Application` (`IMessagingPresenceReader`) con una versión local y otra de Valkey (sorted set por tenant + latido de 30 s). Los limitadores pasan por `SharedRateLimitPartitions`, que devuelve la partición de memoria de hoy o un `ValkeyFixedWindowRateLimiter : RateLimiter` (`INCR` + `EXPIRE … NX` en un `MULTI`, sin Lua) con respaldo perezoso al de memoria.

**Tech Stack:** .NET 10, StackExchange.Redis, Valkey 9.1.3, `System.Threading.RateLimiting`, xUnit v3, Testcontainers (`ContainerBuilder` genérico y `Testcontainers.PostgreSql` 4.14.0), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-13-valkey-estado-compartido-design.md`](../specs/2026-11-13-valkey-estado-compartido-design.md) — secciones D-1, D-2, D-3, DP-V4, P-1 a P-4, «Despliegue» pasos 2 y 3 (`Presence`, `RateLimiting`). D-4..D-8 van en otro plan.

---

## Antes de empezar

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/replicas-multiples`, rama `feature/replicas-multiples`. Rutas absolutas siempre: el cwd del shell vuelve al checkout principal. Antes de **cada** commit, `git -C $W branch --show-current` tiene que decir esa rama. En la misma carpeta de planes hay planes hermanos: no los toques.
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- Docker Desktop arriba: las pruebas de Valkey usan Testcontainers con `valkey/valkey:9.1.3-alpine` en puerto aleatorio. El Valkey local del developer (`localhost:6379`, sin contraseña) no se toca ni se usa en pruebas; si ya corre, no levantes el servicio nuevo de `compose.yaml` (choca el puerto).
- El developer ya tiene el user-secret `ConnectionStrings:Valkey`. Las harness corren en `Development` y lo heredan; con `Valkey:Enabled = false` (default) no conecta nada. Comprueba sin imprimir valores que no haya flags de Valkey en sus secretos: `dotnet user-secrets list --project $W/src/Api | Select-String -Pattern "^Valkey" | Measure-Object` → `Count : 0`. Si no da 0, avisa y no sigas.
- RED de compilación: la evidencia es la salida de `dotnet build <proyecto de pruebas>`. Integración sólo en GREEN, con `$env:Seed__Enabled = "false"`, filtrada (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. Antes de una corrida larga: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, espera.
- Atajos: `$W` = la ruta del worktree; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`; `$AT` = `$W/tests/ArchitectureTests/ArchitectureTests`; `$MU`/`$MI` = `$W/tests/Modules/Messaging/Modules.Messaging.UnitTests` / `.IntegrationTests`; `$TI` = `$W/tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`.

## Global Constraints

- Cadena: `ConnectionStrings:Valkey`, formato StackExchange.Redis. Producción: `valkey.valkey.svc.cluster.local:6379,user=qep,password=…,abortConnect=false`, en `k8s/prod-secret.yaml` como `ConnectionStrings__Valkey: "#{VALKEY_CONNECTION_STRING}#"` (la variable ya existe en `Backend-prod`). Local: user-secrets, `localhost:6379`.
- Opciones `ValkeyOptions`, `SectionName = "Valkey"`: `Enabled` (`false`), `Areas` con `Presence`, `RateLimiting`, `Sessions`, `Permissions`, `SendBudget`, `Doorbells`, `ApiKeyTouch` (todos `false`), `OperationTimeoutMs` (`100`). Todo en `appsettings.example.json`.
- Validador con `ValidateOnStart`: en `Production` la cadena es obligatoria aunque todo esté apagado; vacía o que empiece con `#{` = ausente. Ningún mensaje ni log lleva la cadena **ni el host**: nombran la clave.
- ACL de producción del usuario `qep`: `~qep:* &qep:* +@all -@dangerous -@scripting`. Por eso: **nada de Lua** (`EVAL`/`EVALSHA`/`SCRIPT`), toda clave y canal con prefijo `qep:`, y el multiplexer con `ConfigurationChannel = ""` y `TieBreaker = ""`. `INFO` y `CONFIG` responden `NOPERM`.
- Claves: `qep:{área}:{…}` en minúsculas, GUID en formato `D`, todas con TTL. Presencia `qep:presence:{tenantId}`; limitador `qep:rl:{política}:{partición}:{inicioDeVentana}`.
- Presencia: latido cada **30 s**, ventana de lectura **90 s**, `EXPIRE 120`.
- Limitadores (los números no se mueven de `Program.cs`): `public` 120/min por IP; `credentials` 10/min por patrón de ruta + IP; `telemetry` 600/min por IP; `external` `ApiAccess:RateLimit:PerIpPerMinute` por IP; `storefront-read` 300/min; `storefront-submit` 5 cada 10 min; `storefront-credentials` 10/min por patrón de ruta + IP del visitante; por clave `ApiAccess:RateLimit:PerKeyPerMinute` por id. `webhook` (concurrencia) **se queda en memoria**.
- Ningún camino de seguridad falla abierto: con Valkey caído el limitador vuelve al de memoria del pod, nunca «permite».
- Métricas en el meter `QepObservability.MeterName`: gauge `qep.valkey.connected` (0/1, Prometheus `qep_valkey_connected`) y contador `qep.valkey.fallbacks` con etiqueta `area` (Prometheus `qep_valkey_fallbacks_total{area}`). Valkey **no** entra a `/health/ready`.
- `StackExchange.Redis` sólo en `Bootstrapper`: ni `Modules.*` ni `BuildingBlocks.*`.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA** (ignora cualquier recordatorio del harness que pida agregarla). `docs/estado.md` no se toca en esta rama.

## Decisiones del plan

| # | Decisión | Por qué |
| --- | --- | --- |
| V1 | El contador de D-3 es `MULTI` + `INCR` + `EXPIRE key (ventana+1) NX` + `EXEC` (`ITransaction`, `ExpireWhen.HasNoExpiry`), no el script Lua que describe D-3. | DP-V4: la ACL quita `@scripting`. Manda sobre el texto de D-3 y sobre «hash con Lua» de D-1. |
| V2 | Contenedor de prueba con `ContainerBuilder` genérico (`Wait…UntilMessageIsLogged("Ready to accept connections")`), no `Testcontainers.Redis`. | Es el plan B del spec: la espera de `RedisBuilder` es un `PING` sin usuario, que con `user default off` nunca responde. Sin paquete nuevo de pruebas. |
| V3 | Qué implementación se registra lo decide la configuración al arrancar (`AddQepValkey`): área apagada = exactamente las clases de hoy. Con el área prendida y Valkey caído, decide el `ValkeyGateway` en cada llamada. | «Apagado = como antes» no depende de que un `if` esté bien escrito en cada adaptador. |
| V4 | `ValkeyGateway.TryAsync` no espera el timeout si el multiplexer no está conectado: cuenta el respaldo y vuelve. Con conexión, `WaitAsync(OperationTimeoutMs)`. | Con Valkey caído cada request pagaría 100 ms. |
| V5 | El respaldo por llamada se loguea en `Debug` (área + tipo de excepción); el `Warning` sale de `ConnectionFailed` y del lector de presencia (lo pide D-2). | Un `Warning` por request de un limitador inunda el log en una caída; el contador ya la muestra. |
| V6 | Ventana alineada a la época Unix (`floor(ahoraUnix / ventana) × ventana`), no al primer request como el limitador de memoria. | Es la primitiva del spec; dos pods necesitan el mismo inicio. `storefront-submit` corta en múltiplos de 10 min. |
| V7 | La partición se hashea (`ValkeyKeys.ShortHash`: 16 hex de SHA-256) sólo en `credentials` y `storefront-credentials`, las que llevan patrón de ruta. | Spec D-3. |
| V8 | La prueba de 429 por clave con dos hosts va en `$MI/ExternalApiSurfaceTests`, no en `ApiAccess.IntegrationTests`. | Las pruebas del límite por clave y por IP ya viven ahí (`TheKeyLimitAnswers429WithCodeAndRetryAfter`) porque necesitan los endpoints de Messaging. |
| V9 | La etiqueta `valkey.platform/access: "true"` entra a `k8s/prod-namespace.yaml`. | DP-V4 la exige y el pipeline aplica ese manifiesto (`azure-pipelines.yml`, lista `manifests`). |
| V10 | El paso 2 del despliegue pone `Valkey__Enabled: "true"` con todas las áreas en `false`. | La verificación del paso 2 pide `connected` y `qep_valkey_connected = 1`, y con `Enabled = false` el cliente no conecta («Rollback»). |

## Review Focus

1. **La ACL real tumba el cliente.** Con `user default off` y `-@dangerous`, el `INFO`/`CONFIG` del handshake, el canal `__Booksleeve_MasterChanged` o la clave de desempate dejan el multiplexer desconectado o lanzando `NOPERM`. Tarea 2 (`TheMultiplexerWorksUnderTheProductionAcl`), primera prueba de integración del plan.
2. **La cadena o el host se filtran al log.** Las excepciones de StackExchange.Redis traen `host:puerto` en el mensaje. Tarea 4 (`TheStartupLogNamesTheKeyAndNeverTheValue`): con `password=SENTINEL-valkey-pw` y un host inventado, ningún log lleva ni el sentinel ni el host.
3. **Valkey lento, no caído.** Un `CLIENT PAUSE` deja la operación colgada y el request esperando. Tarea 4 (`APausedValkeyFallsBackWithinTheTimeout`): vuelve en menos de 500 ms, sin éxito, y suma `qep.valkey.fallbacks{area}`.
4. **Particiones que nunca se liberan.** Si `IdleDuration` devuelve siempre `null`, el limitador particionado no desaloja nunca y cada IP vista queda en memoria; si lo devuelve con conteos en el respaldo local, los pierde. Tarea 7 (`AnIdleLimiterReportsItsIdleDuration`).
5. **Un miembro con dos pestañas o en dos pods.** Cerrar una pestaña no lo saca; si lo saca otro pod, vuelve en el siguiente latido. Tarea 6 (`DisconnectingTheLastLocalConnectionRemovesTheMember` y `AMemberStillConnectedElsewhereReturnsAfterTheNextHeartbeat`).

---

## Bloque A — D-1: paquete, cliente, opciones y observabilidad (Tareas 1–4, un implementador)

Al final del bloque: `dotnet test $AT`; `dotnet test $BU --filter "FullyQualifiedName~Valkey"`; `dotnet test $TI --filter "FullyQualifiedName~ReadinessApiTests" -- xUnit.MaxParallelThreads=4`; `dotnet restore $W/src/Api/Api.csproj --locked-mode` sin error. Revisión del bloque.

### Tarea 1: regla de arquitectura (P-1)

**Files:**
- Create: `$AT/EphemeralStoreLayerTests.cs`
- Modify: los 18 `$AT/*LayerTests.cs` que tienen `ApplicationDoesNotReferencePersistenceLibraries`

**Interfaces — Produces:** nada de producción.

- [ ] **Prueba.** En `EphemeralStoreLayerTests`, descubre por patrón de archivo en `AppContext.BaseDirectory` (como `CompositionRootTests.cs:994`) `Modules.*.dll` y `BuildingBlocks.*.dll`:
  ```csharp
  [Fact] public void NoModuleOrBuildingBlockReferencesStackExchangeRedis()
  // Assert.Empty(assemblies.Where(a => a.GetReferencedAssemblies().Any(r => r.Name?.StartsWith("StackExchange.Redis", Ordinal) == true)))
  [Fact] public void TheDiscoveryFindsTheModulesAndBuildingBlocks()   // > 80 ensamblados y al menos un BuildingBlocks.*
  ```
  En cada `ApplicationDoesNotReferencePersistenceLibraries` suma `|| name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)`.
- [ ] Corre `dotnet test $AT --filter "FullyQualifiedName~EphemeralStoreLayerTests|FullyQualifiedName~ApplicationDoesNotReferencePersistenceLibraries"` → verde (nadie referencia el paquete todavía; se escribe antes de que haya tentación, P-1).
- [ ] Commit: `test(architecture): forbid StackExchange.Redis outside the bootstrapper`

### Tarea 2: paquete, lock files, multiplexer y la prueba de la ACL

**Files:**
- Modify: `Directory.Packages.props` (`StackExchange.Redis`), `src/Bootstrapper/Bootstrapper.csproj` (`PackageReference`), los `packages.lock.json` que cambien
- Create: `src/Bootstrapper/Valkey/ValkeyConnection.cs`
- Create: `$BU/Valkey/ValkeyTestServer.cs`, `$BU/Valkey/ValkeyAclTests.cs`
- Modify: `compose.yaml`; `$AT/EphemeralStoreLayerTests.cs`

**Interfaces — Produces:**
```csharp
internal sealed partial class ValkeyConnection(IConfiguration configuration, IOptions<ValkeyOptions> options, ILogger<ValkeyConnection> logger)
    : IHostedService, IAsyncDisposable
{
    internal const string ConnectionStringName = "Valkey";              // ConnectionStrings:Valkey
    public IConnectionMultiplexer? Multiplexer { get; }                  // null: apagado, sin cadena o aún no conectó
    internal static ConfigurationOptions BuildOptions(string connectionString);
    // Parse + AbortOnConnectFail = false + ConfigurationChannel = "" + TieBreaker = ""
}

// $BU/Valkey/ValkeyTestServer.cs
internal static class ValkeyTestServer
{
    internal const string Image = "valkey/valkey:9.1.3-alpine";
    internal const string QepPassword = "qep-acl-test";              // valor de prueba, no es un secreto
    internal static Task<IContainer> StartAsync(bool withProductionAcl, CancellationToken ct);
    internal static string ConnectionString(IContainer container, bool asQep);   // "{host}:{port}[,user=qep,password=…],abortConnect=false"
}
```
Con `withProductionAcl`, el contenedor monta (`WithResourceMapping`) un `users.acl` con exactamente:
```
user default off
user qep on >qep-acl-test ~qep:* &qep:* +@all -@dangerous -@scripting
```
y arranca con `valkey-server --aclfile /etc/valkey/users.acl`. `ValkeyOptions` todavía no existe: en esta tarea crea `ValkeyConnection` **sin** constructor primario ni `IHostedService`, sólo con la constante y `BuildOptions`; el constructor, `Multiplexer`, `StartAsync`/`StopAsync` y los eventos llegan en la Tarea 4.

- [ ] **RED.** `ValkeyAclTests`, un contenedor por clase con la ACL:
  ```csharp
  [Fact] public async Task TheMultiplexerWorksUnderTheProductionAcl()
  // mux = await ConnectionMultiplexer.ConnectAsync(ValkeyConnection.BuildOptions(ConnectionString(c, asQep: true)))
  // Assert.True(mux.IsConnected)
  // db.StringIncrementAsync("qep:probe") == 1; KeyExpireAsync("qep:probe", 61s, ExpireWhen.HasNoExpiry) == true y la 2.ª vez false
  // una ITransaction con INCR + EXPIRE NX → ExecuteAsync() == true
  // SortedSetAddAsync("qep:presence:probe", "m", 1) == true
  // PublishAsync(RedisChannel.Literal("qep:wake:probe"), "") no lanza
  [Fact] public async Task TheAclIsReallyLoaded()     // prueba que la anterior no pasa por vacía
  // StringSetAsync("other:x", "1") → RedisServerException con "NOPERM"
  // ScriptEvaluateAsync("return 1") → RedisServerException con "NOPERM"
  ```
  En `EphemeralStoreLayerTests` agrega `[Fact] BootstrapperReferencesStackExchangeRedis()` (ancla de que la regla mira algo real).
- [ ] `dotnet build $BU` → `CS0246 The type or namespace name 'ValkeyConnection' could not be found` (y `ConnectionMultiplexer`).
- [ ] **GREEN.** Agrega `StackExchange.Redis` a `Directory.Packages.props` con la última 2.x estable (`dotnet package search StackExchange.Redis --exact-match`; anota la versión en el commit) y la referencia en `Bootstrapper.csproj`. Regenera y comprueba:
  ```powershell
  dotnet restore $W/Backend.slnx --force-evaluate
  git -C $W status --short -- "*packages.lock.json" | Measure-Object
  dotnet restore $W/src/Api/Api.csproj --locked-mode
  ```
  El último no puede dar `NU1004`. Un `NU1903` nuevo rompe el build por `TreatWarningsAsErrors`: avisa, no lo suprimas sin el análisis que pide `CLAUDE.md` (gotcha de ImageSharp). Implementa `BuildOptions`. En `compose.yaml`, servicio `valkey`: `image: valkey/valkey:9.1.3-alpine`, `ports: ["6379:6379"]`, sin contraseña ni volumen. **Si con sólo `ConfigurationChannel`/`TieBreaker` la primera prueba falla** por `INFO`/`CONFIG`, agrega en `BuildOptions` `CommandMap = CommandMap.Create(new HashSet<string> { "INFO", "CONFIG" }, available: false)` y dilo en el commit; no pidas permisos a la ACL (eso es del owner).
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ValkeyAclTests"` → verde; `dotnet test $AT --filter "FullyQualifiedName~EphemeralStoreLayerTests"` → verde.
- [ ] Commit (props, csproj, **todos** los locks cambiados, compose, código y pruebas juntos): `build: add StackExchange.Redis and the Valkey multiplexer options`

### Tarea 3: opciones, validador, ejemplo, Secret y documentación

**Files:**
- Create: `src/Bootstrapper/Valkey/ValkeyOptions.cs`, `src/Bootstrapper/Valkey/ValkeyOptionsValidator.cs`
- Modify: `src/Api/appsettings.example.json:2-4` y una sección `Valkey`
- Modify: `k8s/prod-secret.yaml` (después de `ConnectionStrings__QepDatabaseListener`), `k8s/prod-namespace.yaml` (V9)
- Modify: `CLAUDE.md:190` («74» → «152»), README (tabla de configuración junto a `ConnectionStrings:QepDatabaseListener`, `README.md:113`; § Ejecución local, el `user-secrets set` de `ConnectionStrings:Valkey` con `localhost:6379`)
- Modify: los comentarios «no hay Redis» de `Modules.Messaging.Application/ConnectionSendBudget.cs:12` y `Modules.Messaging.Infrastructure/Realtime/PgMessagingNotifier.cs:8` (apuntan al spec de Valkey; el de P5 del spec viejo no se toca)
- Test: `$BU/Valkey/ValkeyOptionsValidatorTests.cs`

**Interfaces — Produces:**
```csharp
public sealed class ValkeyOptions
{
    public const string SectionName = "Valkey";
    public bool Enabled { get; set; }
    public ValkeyAreaOptions Areas { get; set; } = new();
    public int OperationTimeoutMs { get; set; } = 100;
}
public sealed class ValkeyAreaOptions
{
    public bool Presence { get; set; } public bool RateLimiting { get; set; } public bool Sessions { get; set; }
    public bool Permissions { get; set; } public bool SendBudget { get; set; } public bool Doorbells { get; set; } public bool ApiKeyTouch { get; set; }
}
internal sealed class ValkeyOptionsValidator(IHostEnvironment environment, IConfiguration configuration) : IValidateOptions<ValkeyOptions>;
// En todo ambiente: OperationTimeoutMs > 0. En Production: ConnectionStrings:Valkey presente, no vacía y sin prefijo "#{".
```
Entrada del Secret, con un comentario al estilo de `ConnectionStrings__QepDatabase` (lleva contraseña del usuario ACL `qep`; `ValkeyOptionsValidator` la exige en `Production` aunque todas las áreas estén apagadas; variable secreta `VALKEY_CONNECTION_STRING` del grupo `Backend-prod`):
```yaml
  ConnectionStrings__Valkey: "#{VALKEY_CONNECTION_STRING}#"
```

- [ ] **RED.**
  ```csharp
  [Fact] public void ProductionWithoutTheConnectionStringFails()        // Failed; mensaje contiene "ConnectionStrings:Valkey"
  [Fact] public void ProductionWithAnUnresolvedPipelineTokenFails()     // "#{VALKEY_CONNECTION_STRING}#" → Failed; el mensaje NO contiene "#{"
  [Fact] public void ProductionFailsEvenWithEveryAreaOff()              // Enabled = false, sin cadena → Failed
  [Fact] public void DevelopmentWithoutTheConnectionStringPasses()
  [Fact] public void ProductionWithAConnectionStringPasses()            // "valkey:6379,user=qep,password=SENTINEL-pw" → Succeeded
  [Fact] public void ANonPositiveTimeoutFails()                         // OperationTimeoutMs = 0 en Development → Failed
  [Fact] public void NoFailureMessageCarriesTheValue()                  // ningún FailureMessage contiene "SENTINEL-pw"
  ```
- [ ] `dotnet build $BU` → `CS0246 'ValkeyOptionsValidator' could not be found`.
- [ ] **GREEN.** Implementa. Registra opciones y validador en Bootstrapper con `AddOptions<ValkeyOptions>().Bind(…).ValidateOnStart()` y `AddSingleton<IValidateOptions<ValkeyOptions>, ValkeyOptionsValidator>()` (el resto del registro va en la Tarea 4). Completa `appsettings.example.json`:
  ```json
  "ConnectionStrings": { "QepDatabase": "…", "Valkey": "<user-secrets: localhost:6379>" },
  "Valkey": { "Enabled": false, "Areas": { "Presence": false, "RateLimiting": false, "Sessions": false, "Permissions": false, "SendBudget": false, "Doorbells": false, "ApiKeyTouch": false }, "OperationTimeoutMs": 100 }
  ```
  Namespace: `valkey.platform/access: "true"` en `labels`, con una línea de comentario (sin ella la `NetworkPolicy` de Valkey da timeout).
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ValkeyOptionsValidatorTests"` → verde; `dotnet test $AT --filter "FullyQualifiedName~ConfigurationExampleTests"` → verde.
- [ ] Commit: `feat(valkey): add options, startup validation and the production secret entry`

### Tarea 4: gateway, conexión, métricas, logs y arranque sin Valkey

**Files:**
- Modify: `src/Bootstrapper/Valkey/ValkeyConnection.cs` (constructor, `StartAsync`, `StopAsync`, eventos)
- Create: `src/Bootstrapper/Valkey/ValkeyGateway.cs`, `ValkeyArea.cs`, `ValkeyKeys.cs`, `ValkeyMetrics.cs`, `ValkeyServiceCollectionExtensions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (llama `AddQepValkey`)
- Modify: README § Observabilidad (las dos métricas y que la alerta vive en `migracion-k8s`)
- Test: `$BU/Valkey/ValkeyGatewayTests.cs`, `$TI/ReadinessApiTests.cs`

**Interfaces — Consumes:** `ValkeyConnection.BuildOptions`, `ValkeyOptions` (Tareas 2–3). **Produces** (lo usan las Tareas 5–8 y el plan de D-4..D-8):
```csharp
internal enum ValkeyArea { Presence, RateLimiting, Sessions, Permissions, SendBudget, Doorbells, ApiKeyTouch }
internal static class ValkeyAreas { public static string TagOf(ValkeyArea area); }
// "presence", "rate_limiting", "sessions", "permissions", "send_budget", "doorbells", "api_key_touch"

internal static class ValkeyKeys
{
    public const string Prefix = "qep:";
    public static RedisKey Presence(Guid tenantId);                                            // qep:presence:{tenantId:D}
    public static RedisKey RateLimit(string policy, string partition, long windowStartUnixSeconds); // qep:rl:{policy}:{partition}:{start}, en minúsculas
    public static string ShortHash(string value);                                             // 16 hex minúsculas de SHA-256(UTF-8)
}

internal readonly record struct ValkeyAttempt<T>(bool Succeeded, T? Value);

internal sealed partial class ValkeyGateway(ValkeyConnection connection, IOptions<ValkeyOptions> options, ValkeyMetrics metrics, ILogger<ValkeyGateway> logger)
{
    public bool IsEnabled(ValkeyArea area);   // Enabled && Areas.<area>
    public Task<ValkeyAttempt<T>> TryAsync<T>(ValkeyArea area, Func<IDatabase, Task<T>> operation, CancellationToken cancellationToken);
    // sin multiplexer o !IsConnected → (false, default) sin esperar; si no, operation(db).WaitAsync(OperationTimeoutMs, ct);
    // cualquier excepción salvo la cancelación del llamador → metrics.RecordFallback(area), log Debug (área + tipo), (false, default)
}

internal sealed class ValkeyMetrics   // gauge qep.valkey.connected (0/1), contador qep.valkey.fallbacks{area}
{
    public ValkeyMetrics(IMeterFactory meterFactory, ValkeyConnection connection);
    public void RecordFallback(ValkeyArea area);
}

public static IServiceCollection AddQepValkey(this IServiceCollection services, IConfiguration configuration);
// opciones + validador (Tarea 3), ValkeyConnection singleton + AddHostedService(sp => connection), ValkeyMetrics, ValkeyGateway, TimeProvider.System (TryAdd)
```
`ValkeyConnection.StartAsync`: `Enabled = false` → log Information «disabled», sin conectar. Sin cadena → Warning nombrando `ConnectionStrings:Valkey`, sin conectar. Si no, `ConnectionMultiplexer.ConnectAsync(BuildOptions(...))` y un Information con la clave y `connected: {bool}`. Suscribe `ConnectionFailed` (Warning con `FailureType`, nunca `EndPoint` ni `Exception.Message`) y `ConnectionRestored` (Information). `ValkeyMetrics` usa `IMeterFactory.Create(QepObservability.MeterName)`, como `QueueGauges`.

- [ ] **RED.** `ValkeyGatewayTests` (contenedor sin ACL por clase; el `Recorder` de `QueueGaugesTests` como molde para las métricas; un logger de captura local a la clase):
  ```csharp
  [Fact] public async Task ADisabledAreaIsNotEnabled()              // Enabled = true, Areas.Presence = false → IsEnabled(Presence) == false
  [Fact] public async Task WithValkeyDisabledNothingConnects()      // Enabled = false y cadena válida → tras StartAsync, Multiplexer == null
  [Fact] public async Task AnOperationRunsAgainstValkey()          // TryAsync(Presence, db => db.StringIncrementAsync("qep:t:"+Guid)) → (true, 1)
  [Fact] public async Task AClosedPortFallsBackAndCounts()          // cadena "127.0.0.1:1" → (false, _) y qep.valkey.fallbacks == 1 con area "presence"
  [Fact] public async Task APausedValkeyFallsBackWithinTheTimeout() // Review Focus 3: CLIENT PAUSE 2000 ALL desde otra conexión;
                                                                     // Stopwatch < 500 ms, Succeeded == false, fallback contado
  [Fact] public async Task TheConnectedGaugeFollowsTheConnection()   // 1 conectado; tras container.StopAsync, 0 en ≤ 10 s (sondeo)
  [Fact] public async Task TheStartupLogNamesTheKeyAndNeverTheValue() // Review Focus 2: cadena "valkey-sentinel-host.invalid:6379,password=SENTINEL-valkey-pw";
                                                                     // algún log contiene "ConnectionStrings:Valkey"; ninguno "SENTINEL-valkey-pw" ni "valkey-sentinel-host"
  [Fact] public void KeysArePrefixedAndLowercase()                  // RateLimit("credentials", "ABC", 60) == "qep:rl:credentials:abc:60";
                                                                     // Presence(g) == $"qep:presence:{g:D}"; ShortHash("x").Length == 16
  ```
  En `ReadinessApiTests`:
  ```csharp
  [Fact] public async Task TheHostStartsWithoutValkey()
  // UseSetting("Valkey:Enabled","true"), Areas:Presence y Areas:RateLimiting "true", ConnectionStrings:Valkey "127.0.0.1:1,connectTimeout=500"
  // GET /health/ready == 200 y GET /health/live == 200
  ```
- [ ] `dotnet build $BU` → `CS0246 'ValkeyGateway' could not be found`.
- [ ] **GREEN.** Implementa y llama `services.AddQepValkey(configuration)` en `AddQepPlatform`.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ValkeyGatewayTests"` → verde; `dotnet test $TI --filter "FullyQualifiedName~TheHostStartsWithoutValkey" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(valkey): add the gateway, connection lifecycle and metrics`
- [ ] **Fin del bloque A:** las corridas del encabezado del bloque. Revisión del bloque.

---

## Bloque B — D-2: presencia compartida (Tareas 5–6, un implementador)

Depende del bloque A. Al final: `dotnet test $MU`; `dotnet test $BU --filter "FullyQualifiedName~ValkeyPresenceTests"`; `dotnet test $MI --filter "FullyQualifiedName~PlatformUsageApiTests" -- xUnit.MaxParallelThreads=4` (área apagada, sin cambios); `dotnet test $AT --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CompositionRootTests"`. Revisión del bloque.

### Tarea 5: puerto de lectura y respaldo local

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPresence.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPresenceReader.cs`
- Modify: `src/Bootstrapper/PlatformUsageSource.cs:20-42` (lee del puerto nuevo; actualiza su comentario de `:18`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1167-1169` (el registro de presencia pasa a `AddQepValkey`)
- Test: `$MU/MessagingPresenceTests.cs` (nuevo o el existente)

**Interfaces — Produces:**
```csharp
public interface IMessagingPresenceReader
{
    /// Tenant → miembros conectados (sin repetir), sólo de los tenants pedidos. Un tenant sin nadie no aparece.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListConnectedAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
}
public sealed class LocalMessagingPresenceReader(IMessagingPresence presence) : IMessagingPresenceReader;  // filtra Snapshot()

// MessagingPresence (la clase local; IMessagingPresence no cambia)
public bool TryRemove(string connectionId, out Guid tenantId, out Guid memberId);
public bool HasConnection(Guid tenantId, Guid memberId);
```
`Disconnected` delega en `TryRemove`. El comentario de `IMessagingPresence` deja de decir «sólo vale mientras la API corra con una réplica»: lo local es el respaldo.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task TheLocalReaderReturnsOnlyTheRequestedTenants()   // dos tenants conectados, pide uno → una clave
  [Fact] public async Task TheLocalReaderDeduplicatesTabs()                 // dos conexiones del mismo miembro → un miembro
  [Fact] public void TryRemoveReturnsTheOwnerOfTheConnection()              // y false con un id desconocido
  [Fact] public void HasConnectionSeesTheOtherTab()                         // quita una de dos → true; quita la segunda → false
  ```
- [ ] `dotnet build $MU` → `CS0246 'IMessagingPresenceReader' could not be found`.
- [ ] **GREEN.** Implementa. `PlatformUsageSource` llama `ListConnectedAsync(rows.Select(r => r.TenantId).ToArray(), ct)`; el DTO no cambia. Registro por ahora: `MessagingPresence` singleton, `IMessagingPresence` → la misma instancia, `IMessagingPresenceReader` → `LocalMessagingPresenceReader`.
- [ ] `dotnet test $MU --filter "FullyQualifiedName~MessagingPresence"` → verde.
- [ ] Commit: `refactor(messaging): read inbox presence through a reader port`

### Tarea 6: presencia en Valkey (P-3)

**Files:**
- Create: `src/Bootstrapper/Valkey/Presence/ValkeyMessagingPresence.cs`, `ValkeyMessagingPresenceReader.cs`, `PresenceHeartbeatWorker.cs`, `ValkeyPresence.cs` (constantes)
- Modify: `src/Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs`
- Modify: `CLAUDE.md` (gotcha «La presencia de la bandeja es por proceso…»: con `Valkey:Areas:Presence` es compartida; sin Valkey vuelve a ser por pod) y el comentario del registro
- Test: `$BU/Valkey/ValkeyPresenceTests.cs`

**Interfaces — Consumes:** `ValkeyGateway`, `ValkeyKeys.Presence`, `MessagingPresence.TryRemove/HasConnection`, `LocalMessagingPresenceReader`. **Produces:**
```csharp
internal static class ValkeyPresence
{
    public static readonly TimeSpan HeartbeatInterval;  // 30 s
    public static readonly TimeSpan ReadWindow;         // 90 s
    public static readonly TimeSpan KeyTtl;             // 120 s
}
internal sealed class ValkeyMessagingPresence(MessagingPresence local, ValkeyGateway gateway, TimeProvider time) : IMessagingPresence;
// Connected: local + ZADD del miembro (score = segundos Unix) y EXPIRE 120, sin esperar (no bloquea el hub).
// Disconnected: local.TryRemove; si !local.HasConnection(tenant, miembro) → ZREM, sin esperar. Snapshot: el local.
internal sealed class PresenceHeartbeatWorker(MessagingPresence local, ValkeyGateway gateway, TimeProvider time) : BackgroundService
{
    internal Task BeatAsync(CancellationToken cancellationToken);
    // por tenant del Snapshot, en un IBatch: ZADD de todos sus miembros, EXPIRE 120, ZREMRANGEBYSCORE -inf (ahora − 90) exclusivo
}
internal sealed partial class ValkeyMessagingPresenceReader(ValkeyGateway gateway, LocalMessagingPresenceReader fallback, TimeProvider time, ILogger<ValkeyMessagingPresenceReader> logger)
    : IMessagingPresenceReader;
// ZRANGEBYSCORE (ahora − 90) exclusivo .. +inf por tenant, en un batch. Sin éxito → Warning (sin ids ni host) y fallback.
```
Registro con `Areas.Presence` prendido (V3): `MessagingPresence` singleton, `IMessagingPresence` → `ValkeyMessagingPresence`, `LocalMessagingPresenceReader` singleton, `IMessagingPresenceReader` → `ValkeyMessagingPresenceReader`, `AddHostedService<PresenceHeartbeatWorker>()`. Apagado: lo de la Tarea 5.

- [ ] **RED.** Contenedor sin ACL por clase; un tenant `Guid.NewGuid()` por prueba; reloj manual (un `TimeProvider` de prueba como `ManualTimeProvider` de `CampaignDispatchDoubles.cs:144`); las escrituras sin espera se comprueban sondeando hasta 2 s.
  ```csharp
  [Fact] public async Task TwoProcessesSeeEachOthersMembers()
  // dos ValkeyMessagingPresence con dos gateways (dos multiplexers) → Connected(c1, t, m1) y Connected(c2, t, m2) → reader: t → {m1, m2}
  [Fact] public async Task AMemberWithoutHeartbeatDisappearsAfterNinetySeconds()   // reloj +91 s sin latido → t ausente
  [Fact] public async Task TheHeartbeatKeepsTheMemberAndExpiresTheKey()           // +60 s, BeatAsync, +60 s → presente; TTL de la clave en (0, 120]
  [Fact] public async Task DisconnectingTheLastLocalConnectionRemovesTheMember()  // Review Focus 5: dos pestañas; quita una → sigue; quita la otra → ausente
  [Fact] public async Task AMemberStillConnectedElsewhereReturnsAfterTheNextHeartbeat() // Review Focus 5: m en pod A y B; Disconnected en A → ausente;
                                                                                        // BeatAsync de B → presente
  [Fact] public async Task WithValkeyDownTheReaderReturnsTheLocalRegistry()       // container.StopAsync → reader == Snapshot local filtrado; fallback "presence" contado
  ```
- [ ] `dotnet build $BU` → `CS0246 'ValkeyMessagingPresence' could not be found`.
- [ ] **GREEN.** Implementa.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ValkeyPresenceTests"` → verde.
- [ ] Commit: `feat(valkey): share inbox presence across pods`
- [ ] **Fin del bloque B:** las corridas del encabezado del bloque. Revisión del bloque.

---

## Bloque C — D-3: limitadores compartidos (Tareas 7–8, un implementador)

Depende del bloque A. Al final: `dotnet test $BU --filter "FullyQualifiedName~ValkeyRateLimiterTests|FullyQualifiedName~SharedRateLimitPartitionsTests"`; `dotnet test $MI --filter "FullyQualifiedName~ExternalApiSurfaceTests" -- xUnit.MaxParallelThreads=4`; `dotnet test $TI --filter "FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~WebTelemetryApiTests" -- xUnit.MaxParallelThreads=4` (área apagada, sin cambios). Revisión del bloque.

### Tarea 7: `ValkeyFixedWindowRateLimiter` (P-4)

**Files:**
- Create: `src/Bootstrapper/Valkey/RateLimiting/ValkeyFixedWindowRateLimiter.cs`, `ValkeyRateLimitLease.cs`
- Test: `$BU/Valkey/ValkeyRateLimiterTests.cs`

**Interfaces — Consumes:** `ValkeyGateway.TryAsync`, `ValkeyKeys.RateLimit`. **Produces:**
```csharp
internal sealed class ValkeyFixedWindowRateLimiter(
    ValkeyGateway gateway, string policy, string partition, FixedWindowRateLimiterOptions options, TimeProvider time) : RateLimiter
{
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken);
    // w = (long)options.Window.TotalSeconds; now = time.GetUtcNow().ToUnixTimeSeconds(); start = now / w * w
    // ITransaction: INCR clave; EXPIRE clave (w + 1) NX → contador
    // contador <= PermitLimit → lease adquirido; si no → rechazado con RetryAfter = max(1, start + w − now) s
    // sin éxito → respaldo: FixedWindowRateLimiter(options) creado una sola vez (Interlocked) → su AcquireAsync
    protected override RateLimitLease AttemptAcquireCore(int permitCount);   // siempre el respaldo (el middleware no lo usa)
    public override TimeSpan? IdleDuration { get; }
    // sin respaldo: tiempo desde el último Acquire (time.GetElapsedTime); con respaldo: null si el respaldo da null, si no el menor
    public override RateLimiterStatistics? GetStatistics();                    // las del respaldo, o null
}
internal sealed class ValkeyRateLimitLease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease;
// MetadataNames = [MetadataName.RetryAfter.Name] cuando hay retryAfter; TryGetMetadata la devuelve
```

- [ ] **RED.** Contenedor sin ACL por clase; `policy` = `"test-" + Guid` por prueba para que no compartan clave; reloj manual que también implemente `GetTimestamp`.
  ```csharp
  [Fact] public async Task TwoLimitersOnTheSameKeyShareTheWindow()
  // dos instancias (dos gateways), PermitLimit 10, ventana 60 s: 5 + 5 adquiridos, el 11.º (en cualquiera) rechazado
  [Fact] public async Task TheWindowResetsAtTheBoundary()             // 10 adquiridos, reloj al inicio de la siguiente ventana → adquirido
  [Fact] public async Task RetryAfterPointsToTheEndOfTheWindow()      // reloj en start + 15 s, rechazo → RetryAfter == 45 s
  [Fact] public async Task TheKeyExpiresAfterTheWindow()              // TTL de la clave en (0, 61]
  [Fact] public async Task WithValkeyDownTheLocalLimiterStillRejects()
  // tras StopAsync, en el mismo limitador: 10 adquiridos, el 11.º rechazado (nunca adquirido); fallback "rate_limiting" contado
  [Fact] public async Task AnIdleLimiterReportsItsIdleDuration()      // Review Focus 4: Acquire, reloj +15 s → IdleDuration >= 15 s;
                                                                       // con Valkey caído y 1 permiso usado en el respaldo → null
  [Fact] public async Task TheRateLimitUsesNoScript()                  // corre los Acquire contra el contenedor con la ACL de producción (ValkeyTestServer, asQep) → adquiridos
  ```
- [ ] `dotnet build $BU` → `CS0246 'ValkeyFixedWindowRateLimiter' could not be found`.
- [ ] **GREEN.** Implementa.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~ValkeyRateLimiterTests"` → verde.
- [ ] Commit: `feat(valkey): add a shared fixed-window rate limiter`

### Tarea 8: fábrica de particiones, `Program.cs` y el límite por clave

**Files:**
- Create: `src/Bootstrapper/Valkey/RateLimiting/SharedRateLimitPartitions.cs`
- Modify: `src/Api/Program.cs:86-202` (las siete políticas de ventana fija; `webhook` no cambia)
- Modify: `src/Bootstrapper/ApiKeys/ApiKeyRateLimiter.cs:13-28`
- Modify: `src/Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:94`
- Create: `$MI/ValkeyTestServer.cs` (copia mínima de la de `$BU`: sólo `StartAsync(withProductionAcl: false)` y la cadena)
- Test: `$BU/Valkey/SharedRateLimitPartitionsTests.cs`, `$MI/ExternalApiSurfaceTests.cs`

**Interfaces — Consumes:** `ValkeyFixedWindowRateLimiter`, `ValkeyGateway.IsEnabled(ValkeyArea.RateLimiting)`, `ValkeyKeys.ShortHash`. **Produces:**
```csharp
public sealed class SharedRateLimitPartitions   // constructor internal; se registra con fábrica en AddQepValkey
{
    public const string ApiKeyPolicy = "apikey";
    public RateLimitPartition<string> FixedWindow(string policy, string partitionKey, FixedWindowRateLimiterOptions options, bool hashPartition = false);
    // área apagada → RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => options) (lo de hoy)
    // prendida → RateLimitPartition.Get(partitionKey, _ => new ValkeyFixedWindowRateLimiter(gateway, policy,
    //            hashPartition ? ShortHash(partitionKey) : partitionKey, options, time))
}
// ApiKeyRateLimiter
public ApiKeyRateLimiter(IOptions<ApiAccessOptions> options, SharedRateLimitPartitions partitions);
// PartitionedRateLimiter.Create<Guid, string>(keyId => partitions.FixedWindow(ApiKeyPolicy, keyId.ToString("D"), opts)); AcquireAsync no cambia
```
En `Program.cs`, cada `RateLimitPartition.GetFixedWindowLimiter(partitionKey: X, factory: _ => opts)` pasa a `Shared(httpContext).FixedWindow(RateLimiterPolicies.<Política>, X, opts)`, con `hashPartition: true` en `Credentials` y `StorefrontCredentials`, y `static SharedRateLimitPartitions Shared(HttpContext c) => c.RequestServices.GetRequiredService<SharedRateLimitPartitions>();`. `ExternalRateLimitRejection` no cambia: lee `MetadataName.RetryAfter` del lease, que el de Valkey trae.

- [ ] **RED.**
  ```csharp
  // SharedRateLimitPartitionsTests
  [Fact] public void WithTheAreaOffThePartitionIsTheInMemoryLimiter()   // Factory(key) is FixedWindowRateLimiter
  [Fact] public void WithTheAreaOnThePartitionIsTheValkeyLimiter()      // Factory(key) is ValkeyFixedWindowRateLimiter
  [Fact] public async Task ARoutePartitionIsHashedInTheKey()            // hashPartition: true → la clave en Valkey no contiene el patrón de ruta y sí ShortHash
  ```
  En `ExternalApiSurfaceTests`, `ArrangeAsync` gana `string? valkey = null` (si viene: `Valkey:Enabled`, `Valkey:Areas:RateLimiting` en `"true"` y `ConnectionStrings:Valkey`); el segundo host es `f.Factory.WithWebHostBuilder(_ => { })`; un contenedor de Valkey por prueba:
  ```csharp
  [Fact] public async Task TheKeyLimitIsSharedAcrossTwoHosts()
  // perKey 2: host A 200, host B 200, host A → 429 "external.rate_limited", Retry-After en [1, 60]
  [Fact] public async Task TheIpLimitIsSharedAcrossTwoHosts()
  // perIp 2, clave inventada: A, B, A → la 3.ª 429 "external.rate_limited" con Retry-After en [1, 60]
  ```
- [ ] `dotnet build $BU` → `CS0246 'SharedRateLimitPartitions' could not be found`.
- [ ] **GREEN.** Implementa.
- [ ] `dotnet test $BU --filter "FullyQualifiedName~SharedRateLimitPartitionsTests"` → verde; `dotnet test $MI --filter "FullyQualifiedName~SharedAcrossTwoHosts|FullyQualifiedName~TheKeyLimitAnswers429WithCodeAndRetryAfter|FullyQualifiedName~TheIpLimitStopsInventedKeysWith429AndCode" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(valkey): count fixed-window rate limits in Valkey across pods`
- [ ] **Fin del bloque C:** las corridas del encabezado del bloque. Revisión fuerte de toda la rama (bloques A–C).

Antes de mergear a `develop`: el cambio es **transversal** (Bootstrapper, `Program.cs` y los lock files de casi todos los proyectos), así que por la regla del workspace corre toda la solución, **proyecto por proyecto, uno a la vez**, con `-- xUnit.MaxParallelThreads=4` en los de integración y `$env:Seed__Enabled = "false"`; nunca `dotnet test` sobre la solución entera (agota memoria y puertos). El merge y `docs/estado.md` se actualizan en `develop`, no en esta rama.

---

## Despliegue (después del merge; lo lleva a `main` el owner)

Prerrequisitos ya cumplidos (DP-V4): Valkey en `valkey.valkey.svc.cluster.local:6379`, usuario ACL `qep`, `VALKEY_CONNECTION_STRING` en `Backend-prod`. Los dos arreglos sin Valkey de D-7 que el spec pone en el paso 2 son del otro plan: el paso 2 no los espera.

### Tarea 9: paso 2 — cliente conectado, áreas apagadas

**Files:** `k8s/prod-configMap.yaml` (rama corta desde `develop`)

- [ ] Agrega `Valkey__Enabled: "true"` (V10) con un comentario: conecta el cliente para verificarlo; las áreas se prenden de a una. Ninguna `Valkey__Areas__*`.
- [ ] Commit: `chore(k8s): connect the Valkey client with every area off`
- [ ] Tras el despliegue del owner, verifica (nunca `get secret -o yaml|json`):
  ```powershell
  kubectl --context contabo-prod -n prod-qep-backend describe secret qep-backend-secret | Select-String -Pattern "ConnectionStrings__Valkey"
  kubectl --context contabo-prod get namespace prod-qep-backend --show-labels | Select-String -Pattern "valkey.platform/access=true"
  kubectl --context contabo-prod -n prod-qep-backend logs <pod> --since=10m | Select-String -Pattern "Valkey"
  ```
  Esperado: la clave con su tamaño en bytes; la etiqueta; una línea con `ConnectionStrings:Valkey` y `connected: True`, ninguna con el host ni `fallback`. En Grafana, `qep_valkey_connected` = 1. La alerta `qep_valkey_connected == 0` por más de 5 min la agrega el owner en `migracion-k8s`.

### Tarea 10: paso 3 — `Presence` y luego `RateLimiting`

**Files:** `k8s/prod-configMap.yaml`, un commit por área, con un día de logs sin respaldos inesperados entre los dos.

- [ ] `Valkey__Areas__Presence: "true"`. Commit `chore(k8s): turn on shared inbox presence`. Verifica: con una sesión abierta en Mensajería, «Conectados» de la consola del operador lo muestra; `qep_valkey_fallbacks_total{area="presence"}` no sube. (`ZRANGE` con `valkey-cli` necesita un usuario del ACL con permiso sobre `qep:*`; la `PROBE_PASSWORD` del pod de Valkey sirve sólo si su usuario lo tiene — DECISIÓN-PENDIENTE del owner, como dice «Verificación» del spec.)
- [ ] `Valkey__Areas__RateLimiting: "true"`. Commit `chore(k8s): turn on shared rate limits`. Verifica: once `POST` a una ruta `credentials` desde la misma IP en un minuto → el 11.º da 429; en `/api/v1/external/` el `Retry-After` apunta al cierre de la ventana; `qep_valkey_fallbacks_total{area="rate_limiting"}` no sube.
- [ ] Rollback de cualquiera: el flag a `false` y redeploy; las claves mueren por TTL.

---

## Autorrevisión

- **Cobertura del spec.** D-1: paquete y 152 locks → T2; `CLAUDE.md` «74» → T3; multiplexer, `AbortOnConnectFail`, ACL (`ConfigurationChannel`, `TieBreaker`, sin Lua, `qep:`) → T2, T7 (`TheRateLimitUsesNoScript`); opciones, validador, `#{`, ejemplo, Secret → T3; gauge, contador, logs sin valor ni host, readiness intacta → T4; capas → T1; Testcontainers → T2 (V2); `compose.yaml` → T2; comentarios «no hay Redis» → T3. D-2: sorted set, latido, 90/120 s, `ZADD` inmediato, `ZREM` condicionado, lector con respaldo y Warning, `PlatformUsageSource`, DTO intacto → T5, T6. D-3: siete políticas de ventana fija y la de clave, `webhook` en memoria, clave `qep:rl:…`, hash de partición, `Retry-After`, respaldo perezoso nunca abierto → T7, T8. P-1 → T1; P-2 → T3, T4; P-3 → T6 (+ `PlatformUsageApiTests` en el fin del bloque B); P-4 → T7, T8 (V8). Despliegue 2 y 3 (`Presence`, `RateLimiting`) → T9, T10.
- **Nombres.** `ValkeyConnection.BuildOptions`, `ValkeyGateway.TryAsync/IsEnabled`, `ValkeyAttempt<T>`, `ValkeyArea`/`ValkeyAreas.TagOf`, `ValkeyKeys.Presence/RateLimit/ShortHash`, `ValkeyMetrics.RecordFallback`, `AddQepValkey`, `IMessagingPresenceReader.ListConnectedAsync`, `LocalMessagingPresenceReader`, `MessagingPresence.TryRemove/HasConnection`, `ValkeyPresence`, `PresenceHeartbeatWorker.BeatAsync`, `ValkeyFixedWindowRateLimiter`, `ValkeyRateLimitLease`, `SharedRateLimitPartitions.FixedWindow/ApiKeyPolicy` se usan igual en todas las tareas.
- **Review Focus.** Los cinco tienen prueba nombrada en su tarea dueña (T2, T4 ×2, T6, T7).
- **Proporción.** Diez tareas (ocho de código en tres bloques, dos de manifiesto); sin cuerpos de método salvo la regla del contador y de `IdleDuration`, que la firma no decide.
