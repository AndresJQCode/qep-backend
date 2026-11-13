# Valkey: sesión, permisos, presupuesto de envío, timbres y toque de claves — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan. Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: las tareas van en **3 bloques, un implementador por bloque**, una revisión por bloque y la revisión fuerte (toda la rama) una sola vez al final. RED sin contenedores cuando el fallo esperado es de compilación; integración filtrada por nombre mientras se itera; la clase completa una vez al final del bloque; la suite completa sólo antes de mergear a `develop`, limitada a los proyectos de prueba de los módulos tocados; nunca dos corridas de integración a la vez; pruebas en primer plano; `$env:Seed__Enabled = "false"`. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prender, detrás de sus flags, las cinco áreas de Valkey que no rompen con dos réplicas pero le quitan carga a Postgres —caché de sesión (D-4), caché de permisos (D-5), presupuesto de envío (D-6), timbres de workers (D-7) y toque de `last_used_at` (D-8)— sin que ningún camino de seguridad falle abierto, y dejar listo el paso 3 del despliegue para esas áreas.

**Architecture:** Cada área define su puerto en el Application que lo necesita (`ISessionCache`, `IBuyerSessionCache`, `ITenantAccessVersion`, `IQueueDoorbell`, `IApiKeyTouchGate`; `IConnectionSendBudget` e `IAuthorizationService` ya existen) y el adaptador con Valkey vive en `src/Bootstrapper/Valkey/`, sobre el `ValkeyGateway` del plan base, con un nulo (o la implementación de hoy) cuando el área está apagada, y respaldo en cada llamada cuando `ValkeyGateway.TryAsync` no tiene éxito. Sin Lua (ACL de producción): sesión y permisos son `GET`/`MGET`/`SET`/`DEL`/`INCR`; el cubo de envío es `WATCH`/`MULTI` con reintento acotado y Postgres de respaldo; los timbres son pub/sub en `qep:wake:{cola}`. Las revocaciones de sesión y los cambios de permisos invalidan **antes** del guardado y **otra vez** después del commit.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, StackExchange.Redis (sólo en Bootstrapper), xUnit v3, Testcontainers.PostgreSql y `ValkeyTestServer` del plan base (`ContainerBuilder` genérico, con o sin la ACL de producción), `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-13-valkey-estado-compartido-design.md`](../specs/2026-11-13-valkey-estado-compartido-design.md) — secciones D-4 a D-8, «Pruebas» P-5 a P-9, «Despliegue» paso 3 y DP-V4.

**Depende de:** [`2026-11-13-valkey-base-presencia-rate-limits.md`](2026-11-13-valkey-base-presencia-rate-limits.md) (D-1). Ninguna tarea de este plan arranca hasta que las de D-1 de ese plan estén en la rama.

---

## Antes de empezar

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/replicas-multiples`, rama `feature/replicas-multiples` (o la rama de implementación que fije el owner tras el plan base). Rutas absolutas siempre: el cwd del shell vuelve al checkout principal. Antes de **cada** commit, `git -C $W branch --show-current` tiene que decir la rama esperada.
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- RED de compilación: la evidencia es la salida de `dotnet build <proyecto de pruebas>`, sin contenedores. Integración sólo en GREEN, con `$env:Seed__Enabled = "false"` en la sesión, filtrada (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. Antes de una corrida larga: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra, espera.
- Atajos: `$W` = la ruta del worktree; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`; `$IdI` = `$W/tests/Modules/Identity/Modules.Identity.IntegrationTests`; `$IdU` = `.../Identity/Modules.Identity.UnitTests`; `$ByI`/`$ByU` = los de `Modules/Buyers`; `$TI`/`$TU` = los de `Modules/Tenancy`; `$AzU` = `.../Authorization/Modules.Authorization.UnitTests`; `$MI`/`$MU` = los de `Modules/Messaging`; `$NI`/`$NU` = los de `Modules/Notifications`; `$AuI` = `.../Automations/Modules.Automations.IntegrationTests`; `$AkI` = `.../ApiAccess/Modules.ApiAccess.IntegrationTests`.

### Lo que este plan consume del plan base (D-1)

Nombres reales del plan base (su Tarea 2 y su Tarea 4). Todo vive en `src/Bootstrapper/Valkey/` y es `internal`:

```csharp
internal enum ValkeyArea { Presence, RateLimiting, Sessions, Permissions, SendBudget, Doorbells, ApiKeyTouch }
internal static class ValkeyAreas { public static string TagOf(ValkeyArea area); } // "sessions", "permissions", "send_budget", "doorbells", "api_key_touch"
internal static class ValkeyKeys { public const string Prefix = "qep:"; /* + Presence, RateLimit, ShortHash */ }
internal readonly record struct ValkeyAttempt<T>(bool Succeeded, T? Value);
internal sealed partial class ValkeyGateway
{
    public bool IsEnabled(ValkeyArea area);  // Enabled && Areas.<area>
    public Task<ValkeyAttempt<T>> TryAsync<T>(ValkeyArea area, Func<IDatabase, Task<T>> operation, CancellationToken cancellationToken);
    // sin conexión: (false, default) sin esperar y cuenta el respaldo; con conexión: WaitAsync(OperationTimeoutMs);
    // cualquier excepción salvo la cancelación del llamador → ValkeyMetrics.RecordFallback(area) y (false, default)
}
internal sealed class ValkeyMetrics { public void RecordFallback(ValkeyArea area); } // qep.valkey.connected, qep.valkey.fallbacks{area}
public static IServiceCollection AddQepValkey(this IServiceCollection services, IConfiguration configuration); // ValkeyServiceCollectionExtensions.cs
internal static class ValkeyTestServer   // $BU/Valkey/ValkeyTestServer.cs: ContainerBuilder genérico, valkey/valkey:9.1.3-alpine
{
    internal static Task<IContainer> StartAsync(bool withProductionAcl, CancellationToken ct);
    internal static string ConnectionString(IContainer container, bool asQep);
}
```

Reglas que salen de ahí:

- **Cada adaptador habla con Valkey sólo por `TryAsync`** (las transacciones con `db.CreateTransaction()` dentro de la operación). Un `Succeeded = false` es «Valkey no respondió»: el adaptador toma el respaldo de su área sin contar otra vez el fallback (ya lo contó el gateway).
- **Qué clase se registra lo decide `AddQepValkey` al arrancar** con `IsEnabled(ValkeyArea.X)` (V3 del plan base): área apagada = exactamente las clases de hoy (o los nulos). Cada tarea agrega su área a `ValkeyServiceCollectionExtensions.cs`.
- **El gateway no expone pub/sub.** La Tarea 5 le agrega lo que los timbres necesitan.
- **Pruebas con Valkey en un proyecto de módulo:** copia mínima de `ValkeyTestServer` en ese proyecto, como el plan base hace con `$MI/ValkeyTestServer.cs` (sólo `StartAsync` y la cadena). Pruebas de ACL: `StartAsync(withProductionAcl: true)` y `ConnectionString(…, asQep: true)`.
- **Ya los hace el plan base y no se repiten:** el paquete `StackExchange.Redis` y los `packages.lock.json`; los dos comentarios «no hay Redis» (`ConnectionSendBudget.cs:12`, `PgMessagingNotifier.cs:8`, su Tarea 3); y `Valkey__Enabled: "true"` en el paso 2, con todas las áreas apagadas.

## Global Constraints

- **Ningún camino de seguridad falla abierto.** Error o timeout de Valkey en sesión o permisos ⇒ se lee Postgres. Nunca se da por válida una sesión ni se conceden permisos por un fallo de Valkey.
- **Sin Lua**: ningún `EVAL`/`EVALSHA`/`SCRIPT` (ACL `qep`: `~qep:* &qep:* +@all -@dangerous -@scripting`). Tampoco `KEYS`, `FLUSH*`, `INFO`, `CONFIG` desde el código. Ningún borrado por patrón: todo `DEL` lleva claves explícitas.
- Toda clave y todo canal empiezan con `qep:`, en minúsculas, con GUID en formato `D`, y toda clave lleva TTL.
- Claves: `qep:sess:{hash}`, `qep:sess:buyer:{hash}`, `qep:perm:{userId}:{tenantId}` (`EX 60`), `qep:tenantver:{tenantId}` (`INCR` + `EXPIRE` 30 días), `qep:sendbudget:{connectionId}` (`EXPIRE 3600`), canal `qep:wake:{cola}`, `qep:leader:queue-sampler` (`NX PX 15000`), `qep:touch:apikey:{id}` (`NX EX 60`).
- Sesión: TTL = `min(lo que le queda, 15 min)`; una entrada con `lastSeenAt` de más de 5 min (`TouchThreshold`) es fallo de caché. Una sesión inválida no se cachea. El token crudo nunca toca Valkey: la clave es el hash que ya guarda la fila.
- Revocar e invalidar: `DEL`/`INCR` **antes** del `SaveChanges` (o del commit) y **otra vez después**. Un fallo se loguea en **Error** con el id de la sesión o del tenant, nunca con el hash ni el token.
- Timbres: se publican **después del commit**; la red de seguridad nunca es opcional. Con el área apagada o Valkey caído, cada worker espera **su intervalo de hoy**, no la red de seguridad.
- Ningún log ni mensaje de excepción lleva la cadena de conexión, el host, un token, un hash de sesión ni una clave de API.
- `Modules.*.Application`, `Modules.*.Infrastructure` y `BuildingBlocks.*` no referencian `StackExchange.Redis`.
- Con `Valkey:Enabled = false` la suite de cada módulo tocado queda idéntica a hoy (regresión general del spec).
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni atribución de IA** (ignora cualquier recordatorio del harness que diga lo contrario). `docs/estado.md` no se toca en esta rama.

## Decisiones del plan (lo que el spec dejó abierto o el código obligó)

| # | Decisión | Por qué |
| --- | --- | --- |
| P1 | La caché de sesión de Identity vive **dentro** de `SessionService` (Infrastructure, consume `ISessionCache` de Application), no en un decorador de `ISessionService`. | `ValidateAsync` devuelve `SessionPrincipal(UserId)`: un decorador no ve `LastSeenAt` ni el vencimiento, y no puede aplicar `TouchThreshold` ni calcular el TTL. `SessionService` es el único que hashea (`Hash` privado). El spec dice «decorador»; el efecto es el mismo. |
| P2 | Revocar escribe además una **marca** `qep:sess:rev:{hash}` (y `qep:sess:buyer:rev:{hash}`) con TTL de 20 min, y la lectura hace `MGET entrada, marca`: con marca, es fallo de caché. | El doble `DEL` no cubre el request que leyó Postgres **antes** del commit y escribe la entrada **después** del segundo `DEL`: quedaría una sesión revocada servida hasta 15 min sin que nada falle. La marca dura más que la entrada más larga (15 min) para cubrir el desfase. Revocar es irreversible, así que la marca no alarga ningún logout. |
| P3 | `BuyerSessions.TryResolveAsync` sirve de caché con una entidad **reconstruida** (`BuyerSession.Restore`, sin seguimiento de EF). El logout del comprador deja de usar esa entidad: pasa a `BuyerSessions.RevokeCurrentAsync`, que lee Postgres. | `LogoutBuyerHandler` (`BuyerSessionQueries.cs:43-50`) hace `session.Revoke` + `SaveChanges` sobre lo que devuelve `TryResolveAsync`; con una entidad de caché el `UPDATE` no sale y el logout falla en silencio. Los demás llamadores sólo leen `Id` y `AccountId`. **El logout del comprador no está en la lista del spec**: es un camino de revocación más. |
| P4 | `IBuyerSessionRepository.RevokeActiveAsync` devuelve `IReadOnlyList<RevokedBuyerSession>` (`id`, `token_hash`) con SQL crudo `UPDATE … RETURNING`. | El spec pide los hashes; el id va para el log de Error. `ExecuteUpdateAsync` no tiene `RETURNING`. |
| P5 | `CachedAuthorizationService` decora a la clase concreta `AuthorizationService`, que Bootstrapper ya registra (`QepServiceCollectionExtensions.cs:1654-1656`). Valor: JSON `{"v":<versión>,"p":[…]|null}`. Si la lectura de la versión falla, ese request no cachea. | La versión se lee antes de resolver (spec); sin versión no hay con qué invalidar después. |
| P6 | D-6 usa **transacción optimista** (`ITransaction.AddCondition`, que es `WATCH`/`MULTI`/`EXEC`): lee el hash, calcula en C# (`SendBudgetMath`, port operación por operación del SQL), escribe con condición sobre el campo `version` (o `KeyNotExists` en el primer uso). Hasta **3** intentos; agotados o con error, esa operación va a Postgres y cuenta un fallback. | Sin Lua no hay script atómico. `@transaction` no está en `@dangerous`. Si el owner prefiere no sumar complejidad por «la menor ganancia de la lista» (spec), el área se queda apagada y el Bloque C sale sin la Tarea 9: es la única tarea que se puede quitar sin tocar las demás. |
| P7 | Fallback del cubo: `MessagingInfrastructureExtensions` registra además la implementación de Postgres como servicio con clave `ConnectionSendBudgetKeys.Postgres`. | La clase de Postgres es `internal` y Bootstrapper no tiene `InternalsVisibleTo` en Messaging.Infrastructure; no hay precedente de decoradores en el repo. |
| P8 | `IQueueDoorbell.Ring(queue)` es **sin espera** (`PUBLISH` con `FireAndForget`) y la espera es por oyente: `Listen(queue)` devuelve un `IDoorbellListener` con su propio `SemaphoreSlim(0, 1)`. | El spec pone un semáforo por cola, pero `outbox` tiene una decena de consumidores en el mismo proceso: un semáforo por cola despertaría a uno solo. Sin espera, el timbre nunca alarga el commit del productor. |
| P9 | `WaitAsync(poll, safetyNet, ct)`: conectado y área prendida ⇒ espera hasta `safetyNet` o un aviso; si no ⇒ `Task.Delay(poll)`, con `poll` = el intervalo de hoy. | El spec dice que el nulo espera la red de seguridad: con el área apagada el bot pasaría de 1 s a 15 s, y eso contradice «todo apagado se comporta como hoy». |
| P10 | Quién timbra: un **interceptor de EF** (`QueueDoorbellInterceptor`, `DbCommandInterceptor` + `IDbTransactionInterceptor`) que reconoce `INSERT INTO <tabla>` de las tablas de cola, en EF y en SQL crudo por `Database.ExecuteSql*`, y timbra al confirmar la transacción (o al instante sin transacción). Sólo `campaign` (iniciar/reanudar, que es `UPDATE`) timbra a mano. | Los productores son decenas (outbox lo escriben ~20 `DbContext`), y todas las inserciones de cola pasan por `ExecuteSqlAsync` o por `SaveChanges` (verificado: `InboundIngestion.cs:179-182, 247-248`, `WebhookDeliveries.cs:24` de Messaging y Shopify, `OutboundMessages.cs:292`). Un productor nuevo queda cubierto sin acordarse. Lo que confirma una transacción ajena (`UseTransaction` sobre una conexión de otro `DbContext`) no timbra: lo cubre la red de seguridad. |
| P11 | Sólo `CampaignDispatchWorker` pasa un «próximo vencimiento conocido» (el `RetryAfter` que ya calcula, `CampaignDispatchWorker.cs:111`). Los demás no lo calculan hoy: un reintento con backoff más corto que la red de seguridad espera hasta la red. | Calcularlo en cada worker es trabajo nuevo que el spec no detalla; la red de seguridad lo acota. |
| P12 | Pruebas por cola (P-8): una teoría sobre el interceptor que cubre las ocho tablas, más dos de punta a punta con el worker real (`automation` y `outbox`). | Los harness de Notifications y otros sacan los hosted services (`NotificationsDeliveryHarness.cs:306`); ocho pruebas de punta a punta con worker vivo cuestan ocho arranques de host. |
| P13 | Los consumidores de correo de Notifications son **trece** hosted services concretos (`NotificationsInfrastructureExtensions.cs:46-62`), no diez: el spec contó `BuyerEmailDeliveryWorker`, que es abstracto, como uno. Pasan a un solo `NotificationsDeliveryWorker`. | Conteo del código al 2026-11-13. |
| P14 | El líder del muestreador de colas (`qep:leader:queue-sampler`) se gobierna con `Areas.Doorbells`. | El spec no dice qué flag lo prende. Con el área apagada los dos pods muestrean, como hoy. |

## Review Focus

1. **Sesión revocada que vuelve a la caché.** Un request que leyó Postgres antes del commit del logout escribe la entrada después del segundo `DEL`. La marca de P2 tiene que ganar. Tarea 1 (`ARepopulateAfterTheRevokeIsIgnored`) y, la que mide el riesgo, `ARevokedSessionIsNotServedWhileValkeyIsDown`.
2. **Logout de comprador con la sesión en caché.** El `UPDATE` tiene que salir y la cookie vieja dar 401. Tarea 2 (`LogoutWithACachedSessionRevokesTheRow`).
3. **Permisos viejos tras un cambio de rol en carrera.** Una entrada construida con la versión leída antes del cambio se descarta. Tarea 3 (`AnEntryBuiltWithAnOlderTenantVersionIsDiscarded`, `AResolveRacingARoleChangeIsDiscardedByTheSecondBump`).
4. **Timbre perdido.** Un aviso que llega mientras el worker drena (no espera) no se pierde, y un aviso de `outbox` despierta a **todos** los oyentes del proceso. Tarea 5 (`ARingDuringTheDrainIsNotLost`, `OneRingWakesEveryListenerOfTheQueue`).
5. **Área de timbres apagada = intervalo de hoy.** Con `Doorbells = false` el bot sigue reclamando cada 1 s, no cada 15. Tarea 5 (`WithTheAreaOffTheListenerWaitsTodaysInterval`).
6. **Doble envío por carrera en el cubo.** Dos `AcquireAsync` simultáneos sobre la misma conexión nunca conceden más que el cubo. Tarea 9 (`ConcurrentAcquiresNeverGrantMoreThanTheBucket`).

---

## Bloque A — sesión y permisos (Tareas 1–3, un implementador)

Son los dos caminos de seguridad. Al final del bloque: `CachedSessionTests`, `CachedBuyerSessionTests` y `CachedAuthorizationTests` completas, más `SessionRevocationTests`, `OrphanUserCleanupTests`, `PasswordAuthApiTests`, `BuyerSessionApiTests`, `BuyerAccountDeletionApiTests`, `BuyerPasswordRecoveryApiTests`, `RoleApiTests`, `MembershipApiTests`, `MembershipLifecycleApiTests`, `InvitationApiTests` y `OperatorConsoleApiTests` con el área apagada, una vez cada clase.

### Tarea 1: caché de sesión de Identity (D-4)

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Application/ISessionCache.cs`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Persistence/SessionService.cs:55-122`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Messaging/SessionRevocationWorker.cs:47, 129-161`
- Modify: `src/Modules/Identity/Modules.Identity.Infrastructure/Messaging/OrphanUserCleanupWorker.cs:184-215` (el `RemoveRange` de `:202` y el commit de esa transacción)
- Create: `src/Bootstrapper/Valkey/ValkeySessionCache.cs`, `src/Bootstrapper/Valkey/NullSessionCaches.cs` (nulos de Identity y Buyers)
- Modify: `src/Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs` (`ValkeyArea.Sessions`)
- Test: nuevo `$BU/Valkey/ValkeySessionCacheTests.cs`; nuevo `$IdI/CachedSessionTests.cs`; ajustar los dobles de `$IdU` que construyen `SessionService`

**Interfaces — Consumes:** `ValkeyGateway.TryAsync/IsEnabled(ValkeyArea.Sessions)`, `ValkeyTestServer` (plan base).

**Interfaces — Produces:**
```csharp
// Modules.Identity.Application
public sealed record CachedSession(Guid SessionId, Guid UserId, DateTimeOffset LastSeenAt, DateTimeOffset ValidUntil); // ValidUntil = min(ExpiresAt, LastSeenAt + IdleTimeout)
public sealed record RevokedSession(Guid SessionId, string TokenHash);
public interface ISessionCache
{
    Task<CachedSession?> GetAsync(string tokenHash, CancellationToken cancellationToken);   // null: ausente, marcada revocada o Valkey con error
    Task SetAsync(string tokenHash, CachedSession session, CancellationToken cancellationToken); // TTL = min(ValidUntil - now, 15 min); nunca lanza
    Task RemoveAsync(IReadOnlyCollection<RevokedSession> sessions, CancellationToken cancellationToken); // marca + DEL; nunca lanza; Error con SessionId si falla
}

// Bootstrapper/Valkey
internal sealed class ValkeySessionCache : ISessionCache   // "qep:sess:{hash}", marca "qep:sess:rev:{hash}"
internal static class SessionCacheTtl { public static readonly TimeSpan MaxEntry = TimeSpan.FromMinutes(15); public static readonly TimeSpan RevocationMarker = TimeSpan.FromMinutes(20); }
internal sealed class NullSessionCache : ISessionCache     // Get → null; el resto, nada
```
`GetAsync` hace un solo `MGET qep:sess:{hash} qep:sess:rev:{hash}`. `RemoveAsync`: por sesión, `SET qep:sess:rev:{hash} 1 EX 1200` y `DEL qep:sess:{hash}` en un batch. El reloj del adaptador es `TimeProvider` (singleton).

`SessionService`:
- `ValidateAsync`: hash → `cache.GetAsync`. Acierto si `now < ValidUntil` y `now - LastSeenAt <= TouchThreshold` ⇒ `SessionPrincipal(UserId)` sin tocar la base. Si no, el camino de hoy; si la sesión es válida, `cache.SetAsync` con la foto **después** del posible `Touch`.
- `RevokeAsync` y `RevokeAllForUserAsync`: `cache.RemoveAsync` antes del `SaveChangesAsync` y otra vez después.

`SessionRevocationWorker` recibe `ISessionCache` por constructor: `RemoveAsync` antes de `MarkProcessedAsync`/`SaveChangesAsync` y después. `OrphanUserCleanupWorker`: tras el `RemoveRange` y antes del commit, y después del commit (lo resuelve de `message.Services`).

- [ ] **RED (adaptador).** En `ValkeySessionCacheTests` (contenedor real, sin doble del multiplexer):
  ```csharp
  [Fact] public async Task ASetEntryIsReadBack()                    // Set(hash, s) → Get(hash) == s
  [Fact] public async Task TheTtlNeverExceedsFifteenMinutes()       // ValidUntil = now + 30 d → TTL de "qep:sess:{hash}" <= 900 s
  [Fact] public async Task TheTtlFollowsAnEarlierValidUntil()       // ValidUntil = now + 2 min → TTL <= 120 s
  [Fact] public async Task RemoveDeletesTheEntry()                  // Remove → Get == null; KeyExists("qep:sess:{hash}") == false
  [Fact] public async Task ARepopulateAfterTheRevokeIsIgnored()     // Remove, luego Set (el lector atrasado) → Get == null
  [Fact] public async Task WithValkeyDownGetReturnsNullAndCountsAFallback() // contenedor parado → Get == null; qep.valkey.fallbacks{area="sessions"} == 1
  [Fact] public async Task TheKeyIsTheHashNeverTheToken()           // ninguna clave del servidor contiene el token crudo
  ```
- [ ] **RED (de punta a punta).** En `CachedSessionTests` (`$IdI`, host con `Valkey:Enabled = true`, `Areas:Sessions = true`, y un `DbCommandInterceptor` de prueba que cuenta los comandos cuyo texto contiene `identity.sessions`):
  ```csharp
  [Fact] public async Task TheSecondRequestDoesNotReadTheDatabase()     // login, GET /auth/me dos veces → comandos sobre identity.sessions en el segundo == 0
  [Fact] public async Task TouchStillHappensAfterFiveMinutes()          // FakeClock +6 min → last_seen_at de la fila avanzó
  [Fact] public async Task LogoutRemovesTheEntry()                      // entrada existe antes; tras POST logout no existe; cookie vieja → 401
  [Fact] public async Task PasswordResetRemovesEveryEntryOfTheUser()    // dos sesiones; tras el reset, ninguna entrada; las dos cookies → 401
  [Fact] public async Task SessionRevocationWorkerRemovesTheEntries()   // suspender la membresía, SessionRevocationWorker.TickAsync → sin entradas; 401
  [Fact] public async Task OrphanCleanupRemovesTheEntries()             // usuario huérfano con sesión cacheada; tras el worker → sin entrada; 401
  [Fact] public async Task WithValkeyDownASessionIsValidatedFromPostgres() // contenedor parado → /auth/me 200
  [Fact] public async Task ARevokedSessionIsNotServedWhileValkeyIsDown()  // entrada sembrada, contenedor parado, revocada en Postgres → 401
  [Fact] public async Task AnInvalidSessionIsNotCached()                // cookie desconocida → 401 y ninguna clave "qep:sess:*" nueva (contar por la clave del hash calculado, no por SCAN)
  ```
- [ ] Corre `dotnet build $BU` y `dotnet build $IdI` → fallan con `CS0246 'ISessionCache'` / `'ValkeySessionCache' could not be found`.
- [ ] **GREEN.** Implementa las firmas y el registro en `AddQepValkey` (`IsEnabled(ValkeyArea.Sessions)` → `ValkeySessionCache` singleton; si no, `NullSessionCache`). Corrige los dobles de `$IdU` que construyen `SessionService` (un `NullSessionCache` de prueba o un falso en memoria).
- [ ] Corre `dotnet test $BU --filter "FullyQualifiedName~ValkeySessionCacheTests"` y `dotnet test $IdI --filter "FullyQualifiedName~CachedSessionTests" -- xUnit.MaxParallelThreads=4` → verde. Luego `dotnet test $IdU`.
- [ ] Commit: `feat(identity): cache session validation in valkey and invalidate on every revoke path`

### Tarea 2: caché de sesión de Buyers (D-4)

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs:34-49` (`IBuyerSessionRepository`, nuevo `IBuyerSessionCache`)
- Modify: `src/Modules/Buyers/Modules.Buyers.Infrastructure/Persistence/BuyerSessionRepository.cs:25-36`
- Modify: `src/Modules/Buyers/Modules.Buyers.Domain/BuyerSession.cs` (`Restore`)
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerSessions.cs:31-75`
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerSessionQueries.cs:38-53` (logout)
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/DeleteBuyerAccount.cs:45-71`
- Create: `src/Bootstrapper/Valkey/ValkeyBuyerSessionCache.cs`; nulo en `NullSessionCaches.cs`
- Test: `$ByU/BuyerSessionsTests.cs`, `$ByU/BuyerSessionTests.cs`, `$ByU/BuyersTestDoubles.cs`; nuevo `$ByI/CachedBuyerSessionTests.cs`

**Interfaces — Consumes:** lo de la Tarea 1 en Bootstrapper (`SessionCacheTtl`, el patrón de marca).

**Interfaces — Produces:**
```csharp
// Modules.Buyers.Application (Ports.cs)
public sealed record CachedBuyerSession(Guid SessionId, Guid AccountId, string StoreHost, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt);
public sealed record RevokedBuyerSession(Guid SessionId, string TokenHash);
public interface IBuyerSessionCache
{
    Task<CachedBuyerSession?> GetAsync(string tokenHash, CancellationToken cancellationToken);
    Task SetAsync(string tokenHash, CachedBuyerSession session, CancellationToken cancellationToken);
    Task RemoveAsync(IReadOnlyCollection<RevokedBuyerSession> sessions, CancellationToken cancellationToken);
}
// IBuyerSessionRepository
Task<IReadOnlyList<RevokedBuyerSession>> RevokeActiveAsync(Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

// Modules.Buyers.Domain
public static BuyerSession Restore(Guid id, Guid accountId, string storeHost, string tokenHash,
    DateTimeOffset createdAt, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt); // sin revocar; para lectura, nunca se guarda

// BuyerSessions
public Task<BuyerSession?> TryResolveAsync(string host, string? rawToken, CancellationToken cancellationToken); // misma firma; con caché
public Task<bool> RevokeCurrentAsync(string host, string? rawToken, string reason, CancellationToken cancellationToken); // lee Postgres; false si no había sesión válida
```
Claves `qep:sess:buyer:{hash}` y marca `qep:sess:buyer:rev:{hash}`. En un acierto, `TryResolveAsync` exige `IsValidFor(host, now)` sobre la entidad restaurada y `now - LastSeenAt <= BuyerSession.TouchThreshold`; si no, va a Postgres. `RevokeActiveAsync` es `UPDATE buyers.sessions SET revoked_at = …, revoked_reason = … WHERE account_id = … AND revoked_at IS NULL AND (… id <> keep) RETURNING id, token_hash`. `SaveAndRevokeAsync` hace `RemoveAsync` dentro del callback de `SaveChangesAsync(afterSave, …)`, justo después del `RETURNING` (antes del commit), y otra vez al volver. `DeleteBuyerAccount` lista con `ListActiveAsync(accountId)` antes de `accounts.Remove`, y llama `RemoveAsync` antes y después del guardado. `LogoutBuyerHandler` pasa a `sessions.RevokeCurrentAsync(storefront.Host, command.Token, BuyerSession.RevokedReasons.Logout, …)`, que hace `RemoveAsync` antes y después del guardado.

- [ ] **RED.** En `BuyerSessionsTests` (unitaria, con un falso en memoria de `IBuyerSessionCache`):
  ```csharp
  [Fact] public async Task ACachedSessionIsServedWithoutTheRepository()      // repo.FindByTokenHashAsync no se llama
  [Fact] public async Task ACachedSessionOfAnotherHostIsNotServed()          // host distinto → null
  [Fact] public async Task ACachedSessionPastTheTouchThresholdGoesToTheDatabase()
  [Fact] public async Task RevokeAllRemovesTheReturnedHashesBeforeAndAfterSaving() // registro: ["remove","commit","remove"]
  ```
  En `CachedBuyerSessionTests` (`$ByI`):
  ```csharp
  [Fact] public async Task TheSecondRequestDoesNotReadTheDatabase()
  [Fact] public async Task LogoutWithACachedSessionRevokesTheRow()       // revoked_at no nulo en la fila; cookie vieja → 401 store.buyer.session_invalid
  [Fact] public async Task PasswordRecoveryRemovesEveryEntry()
  [Fact] public async Task ChangePasswordRemovesTheOtherEntriesAndKeepsTheCurrent() // la otra cookie → 401; la actual → 200
  [Fact] public async Task DeletingTheAccountRemovesItsEntries()
  [Fact] public async Task ARevokedBuyerSessionIsNotServedWhileValkeyIsDown()
  ```
- [ ] Corre `dotnet build $ByU` → falla con `CS0246 'IBuyerSessionCache'` y `CS0117 'BuyerSession' does not contain a definition for 'Restore'`.
- [ ] **GREEN.** Implementa. `BuyerSessions` recibe `IBuyerSessionCache` por constructor; `BuyersTestDoubles` gana el falso.
- [ ] Corre `dotnet test $ByU` y `dotnet test $ByI --filter "FullyQualifiedName~CachedBuyerSessionTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(buyers): cache buyer sessions in valkey and return revoked hashes`

### Tarea 3: caché de permisos y versión del tenant (D-5)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantAccessVersion.cs`
- Modify (bump antes y después del guardado): `src/Modules/Authorization/Modules.Authorization.Application/RoleCommands.cs:111-150, 155-186, 265-294`; `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberRoles.cs:143`, `SuspendMember.cs:90`, `RemoveMember.cs:91`, `ReactivateMember.cs:68`, `MembershipActivationService.cs:53` (un bump por tenant de cada membresía aceptada), `InvitationService.cs:116` (sólo el camino que acepta, no el de vencida), `ChangeTenantModules.cs:119` (alrededor de `scope.CommitAsync`, sobre `TargetTenantId`), `ChangeTenantStatus.cs:91` (sobre `TargetTenantId`)
- Create: `src/Bootstrapper/Valkey/CachedAuthorizationService.cs`, `src/Bootstrapper/Valkey/ValkeyTenantAccessVersion.cs` (y su nulo)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1654-1656`
- Test: `$TU/MembershipHandlerTestDoubles.cs`, `$TU/OperatorTestDoubles.cs`, `$TU/InvitationServiceTests.cs`, `$TU/ChangeTenant*HandlerTests.cs`, `$AzU/RoleCommandsTests.cs`; nuevo `$TI/CachedAuthorizationTests.cs`

**Interfaces — Consumes:** `ValkeyGateway.TryAsync/IsEnabled(ValkeyArea.Permissions)`.

**Interfaces — Produces:**
```csharp
// Modules.Tenancy.Application
public interface ITenantAccessVersion
{
    /// INCR "qep:tenantver:{tenantId}" + EXPIRE 30 d en un MULTI, por TryAsync(ValkeyArea.Permissions). Nunca lanza; sin éxito, log Error con el tenant.
    Task BumpAsync(Guid tenantId, CancellationToken cancellationToken);
}

// Bootstrapper/Valkey
internal sealed class CachedAuthorizationService(AuthorizationService inner, ValkeyGateway gateway /*, logger */) : IAuthorizationService
// AuthorizeAsync: el mismo cuerpo que AuthorizationService, sobre el ResolvePermissionsAsync cacheado.
// ResolvePermissionsAsync: MGET perm, tenantver → si la entrada existe y su "v" == versión actual (ausente = 0), la devuelve;
//   si no: lee la versión (ya la tiene del MGET), resuelve inner, SET perm {"v":versión,"p":…} EX 60.
//   MGET sin éxito (TryAsync) → inner sin cachear.
internal sealed class ValkeyTenantAccessVersion : ITenantAccessVersion
internal sealed class NullTenantAccessVersion : ITenantAccessVersion
```
Registro en `AddQepValkey`: `AddScoped<AuthorizationService>()`; `IAuthorizationService` → `CachedAuthorizationService` con `IsEnabled(ValkeyArea.Permissions)`, si no, el mismo `AuthorizationService`. `ITenantAccessVersion` → Valkey o nulo con la misma regla. `CreateRoleHandler` también sube (spec), aunque un rol nuevo no tenga miembros. `InviteMemberHandler` y `ChangeTenantProvider` no suben.

- [ ] **RED (unitarias, orden).** Un `RecordingTenantAccessVersion` que anota en la misma lista que el doble de la unidad de trabajo:
  ```csharp
  // RoleCommandsTests
  [Theory] Create/Update/DeleteRoleBumpsTheTenantVersionBeforeAndAfterSaving   // ["bump:{tenant}", "save", "bump:{tenant}"]
  // Membership: UpdateMemberRoles, SuspendMember, RemoveMember, ReactivateMember → la misma secuencia
  [Fact] public async Task AcceptingInvitationsBumpsEveryAcceptedTenant()        // dos tenants → bump de los dos antes y después
  [Fact] public async Task AnExpiredInvitationDoesNotBump()
  [Fact] public async Task ChangeTenantModulesBumpsTheTargetAroundTheCommit()     // ["bump:{target}","commit","bump:{target}"]
  [Fact] public async Task ChangeTenantStatusBumpsTheTarget()
  ```
- [ ] **RED (integración).** En `CachedAuthorizationTests` (`$TI`, Valkey con `Areas:Permissions = true`; «resolver» es `IAuthorizationService.ResolvePermissionsAsync` resuelto de un scope del host, porque el stub de auth no pasa por él):
  ```csharp
  [Fact] public async Task TheSecondResolveUsesTheCache()                         // contar comandos sobre tenancy.memberships: 0 en el segundo
  [Fact] public async Task AnEntryBuiltWithAnOlderTenantVersionIsDiscarded()      // INCR a mano de qep:tenantver → el resolve vuelve a Postgres
  [Fact] public async Task AResolveRacingARoleChangeIsDiscardedByTheSecondBump()  // sembrar entrada con v = actual + 1 (la del primer bump), cambiar el rol → resolve nuevo ve el permiso nuevo
  [Fact] public async Task ANullResultIsCached()                                  // sin membresía → null; clave existe con "p":null
  // una por camino, forma «resolver, cambiar por la API, resolver → el cambio se ve sin esperar el TTL»:
  [Fact] UpdatingARoleIsSeenImmediately / CreatingARole… / DeletingARole…
  [Fact] UpdatingMemberRoles… / SuspendingAMemberDeniesImmediately / RemovingAMember… / ReactivatingAMember…
  [Fact] AcceptingAnInvitationGrantsImmediately                                   // el null cacheado de antes se descarta
  [Fact] ChangingTenantModulesMasksImmediately / SuspendingTheTenantDeniesImmediately
  [Fact] public async Task WithValkeyDownPermissionsComeFromPostgres()
  ```
- [ ] Corre `dotnet build $AzU` y `dotnet build $TU` → fallan con `CS7036` (falta el argumento `tenantAccessVersion` en los constructores de los handlers) y `CS0246 'ITenantAccessVersion'`.
- [ ] **GREEN.** Implementa; corrige los dobles.
- [ ] Corre `dotnet test $AzU`, `dotnet test $TU` y `dotnet test $TI --filter "FullyQualifiedName~CachedAuthorizationTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(authorization): cache resolved permissions per tenant version in valkey`
- [ ] **Cierre del bloque.** Con `Valkey:Enabled = false` (el default), las clases listadas en el encabezado del bloque, una vez cada una; con el área prendida, `CachedSessionTests`, `CachedBuyerSessionTests` y `CachedAuthorizationTests` completas. Revisión del bloque (una).

---

## Bloque B — timbres, arreglos sin Valkey y toque de claves (Tareas 4–7, un implementador)

Al final del bloque: `QueueDoorbellTests`, `QueueDoorbellInterceptorTests`, `OutboxDeliveryWorkerTests` (unitarias e integración), `DeliveryWorkersHarnessTests`, `QueueSamplerWorkerTests`, `ApiKeyTouchGateTests` y las clases de los workers tocados (`ShopifyWebhookWorkerTests`, las de `AutomationsApiHarness`, `StatusIngestionTests`) una vez cada una.

### Tarea 4: un solo bucle para los correos y líder del muestreador (arreglos sin Valkey de D-7)

No depende de ningún área: si el plan base se despliega antes, esta tarea puede ir con su paso 2 (el spec la pone ahí).

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs:26, 71-91` (deja de ser `BackgroundService`; conserva `DrainAsync`)
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/NotificationsDeliveryWorker.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsInfrastructureExtensions.cs:46-62` (las trece clases como singleton; un `AddHostedService<NotificationsDeliveryWorker>()`)
- Modify: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/NotificationsDeliveryHarness.cs:295-310` (`IsDeliveryWorker` pasa a reconocer el hosted nuevo)
- Create: `src/Bootstrapper/Valkey/QueueSamplerLeadership.cs`
- Modify: `src/Bootstrapper/Observability/QueueSamplerWorker.cs:13-61`
- Test: `$NU/OutboxDeliveryWorkerTests.cs`, `$NI/DeliveryWorkersHarnessTests.cs`, `$BU/QueueSamplerWorkerTests.cs`

**Interfaces — Produces:**
```csharp
internal sealed partial class NotificationsDeliveryWorker(IEnumerable<OutboxDeliveryWorker> definitions, ILogger<NotificationsDeliveryWorker> logger) : BackgroundService
// por tick: await definition.DrainAsync(ct) de cada definición, en secuencia; una que lanza se loguea con su Consumer y sigue la siguiente.
internal async Task TickAsync(CancellationToken cancellationToken);   // el punto de entrada de las pruebas

internal sealed class QueueSamplerLeadership   // Bootstrapper/Valkey
{
    // SET "qep:leader:queue-sampler" <Environment.MachineName> NX PX 15000 por TryAsync(ValkeyArea.Doorbells). true si lo consiguió,
    // o si !IsEnabled(ValkeyArea.Doorbells), o si TryAsync no tuvo éxito (los dos muestrean, como hoy).
    public Task<bool> TryLeadAsync(CancellationToken cancellationToken);
}
// QueueSamplerWorker recibe QueueSamplerLeadership; ExecuteAsync salta SampleAsync en la vuelta que no lidera.
```
- [ ] **RED.**
  ```csharp
  // $NU
  [Fact] public async Task NotificationsDeliveryWorkerDrainsEveryDefinitionInOneTick()  // 13 definiciones falsas → cada DrainAsync llamado una vez
  [Fact] public async Task AFailingDefinitionDoesNotStopTheOthers()
  // $BU (contenedor de Valkey; dos workers con IQueueMetricsSource que cuenta lecturas)
  [Fact] public async Task OnlyTheLeaderSamplesTheQueues()        // una vuelta de cada uno → lecturas == QueueNames.All.Count (no el doble)
  [Fact] public async Task WithTheAreaOffBothSample()             // lecturas == 2 × QueueNames.All.Count
  [Fact] public async Task WithValkeyDownBothSample()
  ```
  Y en `NotificationsInfrastructureExtensions`: una prueba de composición `OnlyOneNotificationsHostedServiceIsRegistered`.
- [ ] Corre `dotnet build $NU` y `dotnet build $BU` → fallan con `CS0246 'NotificationsDeliveryWorker'` / `'QueueSamplerLeadership'`.
- [ ] **GREEN.** Implementa. Barre los harness que sacan hosted services por tipo (`rg "IsDeliveryWorker|ImplementationType" tests`) y ajústalos.
- [ ] Corre `dotnet test $NU`, `dotnet test $BU --filter "FullyQualifiedName~QueueSamplerWorkerTests"` y `dotnet test $NI --filter "FullyQualifiedName~DeliveryWorkersHarnessTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `perf(notifications): drain the email consumers from one hosted loop and elect a queue sampler leader`

### Tarea 5: puerto de timbres y adaptador (D-7)

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Application/QueueDoorbell.cs` (puerto, colas, redes de seguridad, bucle)
- Create: `src/Bootstrapper/Valkey/ValkeyQueueDoorbell.cs`, `NullQueueDoorbell` en el mismo archivo
- Modify: `src/Bootstrapper/Valkey/ValkeyGateway.cs` (pub/sub, ver abajo) y `ValkeyServiceCollectionExtensions.cs` (`ValkeyArea.Doorbells`)
- Test: nuevo `$BU/Valkey/QueueDoorbellTests.cs`; `$BU/Valkey/ValkeyGatewayTests.cs` gana `PublishWithoutConnectionCountsAFallbackAndDoesNotThrow` y `TrySubscribeWithoutConnectionReturnsFalse`

**Interfaces — Produces:**
```csharp
// BuildingBlocks.Application
public static class DoorbellQueues
{
    public const string Automation = "automation", Webhook = "webhook", ShopifyWebhook = "shopify_webhook", Media = "media",
        Scheduled = "scheduled", Campaign = "campaign", Export = "export", Outbox = "outbox";
    public static TimeSpan SafetyNet(string queue);  // automation 15 s, webhook 15 s, shopify_webhook 30 s, media 30 s,
                                                     // scheduled 5 s, campaign 30 s, export 30 s, outbox 30 s
}
public interface IQueueDoorbell
{
    void Ring(string queue);                       // PUBLISH "qep:wake:{queue}" sin esperar; nunca lanza
    IDoorbellListener Listen(string queue);        // un oyente por bucle de worker
}
public interface IDoorbellListener : IDisposable
{
    /// Conectado y área prendida: vuelve con true al primer aviso o con false al pasar safetyNet.
    /// Si no: Task.Delay(poll) y false. Un aviso que llegó mientras no se esperaba hace volver al instante.
    Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken);
}
public static class DoorbellPolling
{
    /// Corre tick; mientras devuelva true (puede haber más) lo repite sin esperar; si no, espera con el oyente.
    public static Task RunAsync(IDoorbellListener listener, TimeSpan poll, TimeSpan safetyNet,
        Func<CancellationToken, Task<bool>> tick, CancellationToken stoppingToken);
}
```
El plan base no expone pub/sub: esta tarea se lo agrega a `ValkeyGateway`, con su misma regla (sin conexión no espera y cuenta el respaldo):
```csharp
// ValkeyGateway (agregado)
public bool IsConnected { get; }                                         // multiplexer no nulo y conectado
public void Publish(ValkeyArea area, RedisChannel channel);              // PUBLISH con CommandFlags.FireAndForget; sin conexión o con excepción → RecordFallback(area); nunca lanza
public Task<bool> TrySubscribeAsync(ValkeyArea area, RedisChannel channel, Action handler, CancellationToken cancellationToken);
// GetSubscriber().SubscribeAsync con OperationTimeoutMs; false sin conexión o con error (RecordFallback)
```
El canal se arma siempre con `ValkeyKeys.Prefix` (`RedisChannel.Literal(ValkeyKeys.Prefix + "wake:" + queue)`), nunca con patrón. `ValkeyQueueDoorbell` (singleton): una suscripción por cola, creada con el primer `Listen` y reintentada en el siguiente `WaitAsync` si falló; su handler libera el semáforo de **cada** oyente vivo de esa cola (si ya está liberado, no suma: `SemaphoreSlim(0, 1)` y se ignora `SemaphoreFullException`). `!gateway.IsConnected` o sin suscripción ⇒ `Delay(poll)`. StackExchange.Redis vuelve a suscribir solo al reconectar; los avisos de la ventana caída se pierden y los cubre `poll`. Sin `@dangerous` ni Lua: `PUBLISH`/`SUBSCRIBE` (`@pubsub`), canal cubierto por `&qep:*`.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task ARingWakesAWaitingWorkerBeforeTheSafetyNet()     // safetyNet 30 s, Ring a los 100 ms → vuelve true en < 1 s
  [Fact] public async Task WithoutARingTheWaitReturnsAtTheSafetyNet()       // safetyNet 300 ms → false en [300 ms, 1 s)
  [Fact] public async Task ARingDuringTheDrainIsNotLost()                   // Ring sin nadie esperando, luego WaitAsync → true al instante
  [Fact] public async Task ManyRingsCountAsOne()                            // 5 Ring, WaitAsync → true; el siguiente WaitAsync espera hasta safetyNet
  [Fact] public async Task OneRingWakesEveryListenerOfTheQueue()            // 3 oyentes de outbox → los 3 vuelven true
  [Fact] public async Task ARingFromAnotherProcessWakesTheListener()        // dos ValkeyQueueDoorbell sobre el mismo contenedor
  [Fact] public async Task WithValkeyDownWaitIsTheTodaysPoll()              // contenedor parado → false a los ~poll, no a safetyNet
  [Fact] public async Task WithTheAreaOffTheListenerWaitsTodaysInterval()   // NullQueueDoorbell: poll 1 s, safetyNet 15 s → vuelve a ~1 s
  [Fact] public async Task TheChannelIsPrefixed()                           // un SUBSCRIBE de prueba a "qep:wake:automation" recibe el Ring
  ```
- [ ] Corre `dotnet build $BU` → falla con `CS0246 'IQueueDoorbell'`.
- [ ] **GREEN.** Implementa.
- [ ] Corre `dotnet test $BU --filter "FullyQualifiedName~QueueDoorbellTests"` → verde.
- [ ] Commit: `feat(platform): add valkey-backed queue doorbells with polling fallback`

### Tarea 6: quién timbra y quién escucha (D-7)

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Infrastructure/QueueDoorbellInterceptor.cs`
- Modify: Bootstrapper — registro del interceptor en cada `DbContext` de módulo (`services.ConfigureDbContext<TContext>((sp, options) => options.AddInterceptors(sp.GetRequiredService<QueueDoorbellInterceptor>()))`; si en EF Core 10 no compone con el `AddDbContext` del módulo, cambia esos `AddDbContext` a la sobrecarga `(sp, options)`)
- Modify (escucha con `DoorbellPolling.RunAsync`, `poll` = el intervalo de hoy, `safetyNet` = `DoorbellQueues.SafetyNet`): `AutomationInboxWorker.cs:57-75`, `WebhookDeliveryWorker.cs:71`, `ShopifyWebhookDeliveryWorker.cs:52`, `MediaCopyWorker.cs:40`, `ScheduledMessageWorker.cs:56`, `CampaignDispatchWorker.cs:40, 111` (pasa su `RetryAfter` como tope), `ExportJobWorker.cs:41`, y en `outbox`: `OutboxPublisherWorker.cs:21`, `NotificationsDeliveryWorker` (Tarea 4), `SessionRevocationWorker.cs:102`, `OrphanUserCleanupWorker.cs:141`, `QuotationEventsWorker.cs:18`, `CitySynonymEventsWorker.cs:20`, `CoexistenceEventsWorker.cs:17`, `BuyerSignupRequestedWorker.cs:57`, `AuditProjectionWorker.cs:32`. Confirma la lista con `rg "platform.outbox_messages|\.Outbox\b" src --glob "*Worker.cs"` el día del cambio y anota la fecha en el commit.
- Modify: los handlers que inician o reanudan una campaña (`rg "CampaignStatus.Running" src/Modules/Messaging`) → `doorbell.Ring(DoorbellQueues.Campaign)` tras el guardado.
- Test: nuevo `$BU/Valkey/QueueDoorbellInterceptorTests.cs`; una prueba de punta a punta en `$AuI` y una en `$NI`

**Interfaces — Consumes:** Tarea 5.

**Interfaces — Produces:**
```csharp
// BuildingBlocks.Infrastructure (singleton)
public sealed class QueueDoorbellInterceptor(IQueueDoorbell doorbell) : DbCommandInterceptor, IDbTransactionInterceptor
// Tablas → cola (texto exacto "INSERT INTO <schema>.<tabla>"):
//   platform.outbox_messages → outbox; messaging.automation_inbox → automation; messaging.webhook_deliveries → webhook;
//   shopify.webhook_deliveries → shopify_webhook; messaging.message_media → media; messaging.scheduled_messages → scheduled;
//   quotations.export_jobs → export.
// NonQueryExecuted(Async) con resultado > 0 y ReaderExecuted(Async): si el DbContext no tiene transacción, Ring al instante;
// si la tiene, la cola queda pendiente para ese DbContext y suena en TransactionCommitted(Async); TransactionRolledBack la descarta.
```
- [ ] **RED.**
  ```csharp
  // QueueDoorbellInterceptorTests ($BU, Postgres real + un IQueueDoorbell que registra)
  [Theory] InsertIntoEachDoorbellTableRingsItsQueue(table, queue)     // las siete tablas, por EF o por ExecuteSql según el productor real
  [Fact] public async Task AnInsertInsideATransactionRingsOnlyAfterCommit()   // antes del commit: ningún Ring; después: uno
  [Fact] public async Task ARolledBackInsertDoesNotRing()
  [Fact] public async Task AnInsertThatInsertedNothingDoesNotRing()            // ON CONFLICT DO NOTHING con 0 filas
  [Fact] public async Task EveryDbContextThatMapsADoorbellTableHasTheInterceptor() // recorre los DbContext del contenedor de DI
  // $AuI, con el worker real y Doorbells prendida
  [Fact] public async Task AnInboundMessageWakesTheBotBeforeTheSafetyNet()    // InboxPollSeconds = 60 → el bot responde en < 5 s
  // $NI, con el hosted real
  [Fact] public async Task AnOutboxRowWakesTheEmailConsumersBeforeTheSafetyNet()
  ```
- [ ] Corre `dotnet build $BU` → falla con `CS0246 'QueueDoorbellInterceptor'`.
- [ ] **GREEN.** Interceptor, registro y los workers (cada uno conserva su manejo de errores dentro del `tick`; el `tick` devuelve `true` sólo si el lote vino lleno).
- [ ] Corre `dotnet test $BU --filter "FullyQualifiedName~QueueDoorbellInterceptorTests"`, luego las dos de punta a punta por nombre → verde.
- [ ] Commit: `feat(platform): ring queue doorbells after commit and wake workers before the safety net`

### Tarea 7: toque de `last_used_at` (D-8)

**Files:**
- Modify: `src/Modules/ApiAccess/Modules.ApiAccess.Application/ApiAccessPorts.cs` (nuevo `IApiKeyTouchGate`)
- Modify: `src/Bootstrapper/ApiKeys/ApiKeyAuthenticationHandler.cs:68-78`
- Create: `src/Bootstrapper/Valkey/ValkeyApiKeyTouchGate.cs` (y su nulo)
- Test: nuevo `$AkI/ApiKeyTouchGateTests.cs`

**Interfaces — Produces:**
```csharp
public interface IApiKeyTouchGate
{
    /// SET "qep:touch:apikey:{keyId}" 1 NX EX 60: true si la puso (toca ir a Postgres), false si ya existía.
    /// Nulo y error de Valkey: true.
    Task<bool> ShouldTouchAsync(Guid keyId, CancellationToken cancellationToken);
}
```
El handler llama `TouchLastUsedAsync` sólo si `ShouldTouchAsync` dio `true`, dentro del mismo `try/catch` de hoy.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task TheSecondRequestInAMinuteDoesNotUpdate()   // dos requests con la clave → UPDATE sobre api_access.api_keys contados == 1
  [Fact] public async Task AfterSixtySecondsItUpdatesAgain()           // borrar la clave de Valkey (simula el TTL) + FakeClock +61 s → 2 UPDATE y last_used_at movido
  [Fact] public async Task WithValkeyDownTheUpdateRunsAsToday()        // contenedor parado → 1 UPDATE por request (el filtro de TouchInterval sigue)
  ```
- [ ] Corre `dotnet build $AkI` → falla con `CS0246 'IApiKeyTouchGate'`.
- [ ] **GREEN.** Implementa y registra en `AddQepValkey` con `IsEnabled(ValkeyArea.ApiKeyTouch)`.
- [ ] Corre `dotnet test $AkI --filter "FullyQualifiedName~ApiKeyTouchGateTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `perf(api-access): skip the last-used update while a valkey touch gate is set`
- [ ] **Cierre del bloque.** Las clases del encabezado una vez cada una, con el área prendida y con `Valkey:Enabled = false`. Revisión del bloque.

---

## Bloque C — presupuesto de envío, compatibilidad con la ACL, documentación y despliegue (Tareas 8–10, un implementador)

### Tarea 8 (antes que la 9, porque prueba todas): cada área bajo la ACL de producción

**Files:**
- Test: nuevo `$BU/Valkey/ValkeyAclCompatibilityTests.cs` (`ValkeyTestServer.StartAsync(withProductionAcl: true)` y `ConnectionString(…, asQep: true)`, como `ValkeyAclTests` del plan base)

- [ ] **RED → GREEN en un paso** (prueba de caracterización; si falla, es un hallazgo, no se «arregla» relajando la ACL):
  ```csharp
  [Fact] public async Task SessionCacheWorksUnderTheProductionAcl()     // Set/Get/Remove sin NOPERM
  [Fact] public async Task TenantVersionWorksUnderTheProductionAcl()    // INCR + EXPIRE en MULTI, MGET
  [Fact] public async Task DoorbellsWorkUnderTheProductionAcl()         // SUBSCRIBE/PUBLISH en qep:wake:*
  [Fact] public async Task TouchGateAndLeaderWorkUnderTheProductionAcl()// SET NX EX / NX PX
  [Fact] public async Task OptimisticTransactionsWorkUnderTheProductionAcl() // WATCH/MULTI/EXEC con AddCondition (para la Tarea 9)
  ```
  Corre `dotnet test $BU --filter "FullyQualifiedName~ValkeyAclCompatibilityTests"`.
- [ ] Commit: `test(platform): pin every valkey area to the production acl`

### Tarea 9: presupuesto de envío en Valkey (D-6) — se puede omitir (P6)

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConnectionSendBudget.cs` (nuevo `ConnectionSendBudgetKeys`; el comentario «no hay Redis» ya lo reescribe el plan base)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:132` (registro con clave)
- Create: `src/Bootstrapper/Valkey/ValkeyConnectionSendBudget.cs`, `src/Bootstrapper/Valkey/SendBudgetMath.cs`
- Test: nuevo `$BU/Valkey/ValkeySendBudgetTests.cs`

**Interfaces — Consumes:** `IConnectionSendBudget`, `SendGrant` (`ConnectionSendBudget.cs:8, 17-26`), `IOptions<MessagingCampaignOptions>` (`MessagesPerSecond`, `MaxMessagesPerSecond`).

**Interfaces — Produces:**
```csharp
public static class ConnectionSendBudgetKeys { public const string Postgres = "postgres"; } // Messaging.Application

internal sealed record SendBudgetState(double Tokens, double Rate, long RefilledAtMicros, long? ThrottledUntilMicros, long UpdatedAtMicros, long Version);
internal static class SendBudgetMath
{
    public const int StepSeconds = 30;
    // Port del SQL de ConnectionSendBudget.AcquireAsync (Infrastructure, :42-81): primer uso = cubo lleno al ritmo cap;
    // subida por tramos de 30 s desde max(updated_at, throttled_until); relleno desde max(refilled_at, throttled_until) acotado al ritmo nuevo;
    // concesión 0 en pausa; RetryAfter igual que el RETURNING.
    public static (SendBudgetState Next, SendGrant Grant) Acquire(SendBudgetState? current, double cap, int requested, long nowMicros);
    // Port de ThrottleAsync (:95-107): sin estado → tokens 0, rate max(1, cap/2), pausa now+30 s; con pausa vigente → sin cambio (null).
    public static SendBudgetState? Throttle(SendBudgetState? current, double cap, long nowMicros);
}
internal sealed class ValkeyConnectionSendBudget(ValkeyGateway gateway, [FromKeyedServices(ConnectionSendBudgetKeys.Postgres)] IConnectionSendBudget postgres,
    IOptions<MessagingCampaignOptions> options) : IConnectionSendBudget
```
Hash `qep:sendbudget:{connectionId}` con `tokens`, `rate`, `refilled_at`, `throttled_until` (vacío si no hay), `updated_at` (microsegundos Unix, como Postgres guarda `timestamptz`) y `version`. Cada operación: `HGETALL` → cálculo → transacción con `Condition.HashEqual(key, "version", v)` (o `Condition.KeyNotExists`), `HSET` + `EXPIRE 3600`, `version + 1`. Hasta 3 intentos, cada uno por `TryAsync(ValkeyArea.SendBudget)`; `Succeeded = false` ⇒ `postgres` para esa operación (el gateway ya contó el fallback); 3 conflictos seguidos ⇒ `postgres` y `ValkeyMetrics.RecordFallback(ValkeyArea.SendBudget)`. El `now` siempre del parámetro.

- [ ] **RED.**
  ```csharp
  [Fact] public async Task AFirstAcquireGetsAFullBucketAtTheTargetRate()   // cap 20, pide 50 → Granted 20, Rate 20
  [Fact] public async Task RefillIsBoundedByTheEndOfThePause()
  [Fact] public async Task A130429HalvesTheRateAndPausesThirtySeconds()   // Rate 10; Acquire en la pausa → 0 y RetryAfter = fin de pausa − now
  [Fact] public async Task SeveralThrottlesInOnePauseCountOnce()
  [Fact] public async Task TheRateClimbsOneEveryThirtySeconds()
  [Fact] public async Task ConcurrentAcquiresNeverGrantMoreThanTheBucket() // 10 tareas × 5 sobre cap 20 en el mismo now → suma == 20
  [Fact] public async Task TheTwoImplementationsAgree()                   // misma secuencia de (now, pedido|throttle) contra Postgres y Valkey:
      // Granted igual; Rate y RetryAfter con tolerancia 1e-6 s. Secuencia: t0 Acquire 50; t0+0.5 s Acquire 50; t0+1 s Throttle;
      // t0+10 s Acquire 5; t0+31 s Acquire 50; t0+61 s Acquire 50; t0+120 s Acquire 50 (nows en milisegundos enteros).
  [Fact] public async Task WithValkeyDownTheBudgetUsesPostgres()          // contenedor parado → la fila de messaging.connection_send_budgets cambió
  [Fact] public async Task TheHashExpiresInAnHour()                       // TTL <= 3600
  ```
- [ ] Corre `dotnet build $BU` → falla con `CS0246 'ValkeyConnectionSendBudget'`.
- [ ] **GREEN.** Implementa y registra en `AddQepValkey` con `IsEnabled(ValkeyArea.SendBudget)`; con el área apagada queda el registro de hoy.
- [ ] Corre `dotnet test $BU --filter "FullyQualifiedName~ValkeySendBudgetTests"` y `dotnet test $MI --filter "FullyQualifiedName~ConnectionSendBudgetTests|FullyQualifiedName~CampaignDispatchTests" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(messaging): keep the campaign send budget in valkey with postgres fallback`

### Tarea 10: documentación y paso 3 del despliegue

**Files:**
- Modify: `CLAUDE.md` del repo (sección «Gotchas verificados»)
- Modify: `k8s/prod-configMap.yaml` (un commit por área, **sólo cuando el owner lo pida**)

- [ ] Agrega a `CLAUDE.md` las reglas que no se ven leyendo el código, cada una con su prueba: todo camino nuevo que revoque o borre sesiones (de Identity o de comprador) llama `RemoveAsync` antes y después del guardado (`CachedSessionTests`, `CachedBuyerSessionTests`); todo cambio que altere permisos efectivos de un tenant llama `ITenantAccessVersion.BumpAsync` antes y después (`CachedAuthorizationTests`); una entidad `BuyerSession` de `TryResolveAsync` no se guarda (`LogoutWithACachedSessionRevokesTheRow`); una tabla de cola nueva entra al mapa de `QueueDoorbellInterceptor` (`EveryDbContextThatMapsADoorbellTableHasTheInterceptor`); un consumidor nuevo de `platform.outbox_messages` escucha `DoorbellQueues.Outbox`. Commit: `docs: record valkey invalidation rules`.
- [ ] **Paso 3 (owner).** `Valkey__Enabled: "true"` ya lo puso el paso 2 del plan base. Después de `Presence` y `RateLimiting` (su Tarea 10), en este orden y con un día de logs sin fallbacks inesperados entre uno y otro: `Valkey__Areas__Sessions`, `Valkey__Areas__Permissions`, `Valkey__Areas__SendBudget` (sólo si la Tarea 9 entró), `Valkey__Areas__Doorbells`, `Valkey__Areas__ApiKeyTouch`, cada uno `"true"` en `k8s/prod-configMap.yaml`, un commit por área: `chore(k8s): enable valkey <area> area`. Verificación por área, la de «Verificación» del spec, con `--context contabo-prod` y nunca `get secret -o yaml|json`:
  ```powershell
  kubectl --context contabo-prod -n prod-qep-backend get configmap qep-backend-config -o jsonpath='{.data}' | Select-String -Pattern "Valkey__"
  kubectl --context contabo-prod -n prod-qep-backend logs <pod> --since=1h | Select-String -Pattern "fallback"
  ```
  `Sessions`: cerrar sesión y repetir con la cookie vieja → 401 al instante. `Permissions`: suspender un miembro con su sesión abierta → 403 en el request siguiente. `Doorbells`: la edad de la cola `automation` baja en el dashboard. `ApiKeyTouch` y `SendBudget`: el contador `qep.valkey.fallbacks` del plan base con `area="api_key_touch"` o `"send_budget"` no sube en condiciones normales. Rollback por área: el flag a `"false"` y redeploy.
- [ ] **Cierre del bloque y de la rama.** `ValkeySendBudgetTests` y `ValkeyAclCompatibilityTests` completas; luego, antes de mergear a `develop`, la suite de los proyectos de prueba de los módulos tocados (Identity, Buyers, Tenancy, Authorization, Messaging, Notifications, Automations, Quotations, Shopify, ApiAccess, Audit, Pipeline y `Bootstrapper.UnitTests`), uno a la vez, con `Valkey:Enabled = false` y otra vez con todas las áreas prendidas. Como el interceptor toca todos los `DbContext` (transversal), corre también `ArchitectureTests`. Revisión del bloque y revisión fuerte de toda la rama.
