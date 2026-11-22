---
paths:
  - "src/BuildingBlocks/**"
  - "src/Bootstrapper/Valkey/**"
  - "src/Bootstrapper/Realtime/**"
  - "src/Bootstrapper/Observability/**"
  - "src/Modules/Identity/**"
  - "tests/Modules/Identity/**"
  - "src/Modules/Tenancy/**"
  - "tests/Modules/Tenancy/**"
  - "src/Modules/Platform/**"
  - "tests/Modules/Platform/**"
---

# Valkey, sesiones, permisos, timbres y tiempo real

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El listener de tiempo real no puede pasar por PgBouncer en modo `transaction`** (2026-11-01). `MessagingEventsListener` hace
  `LISTEN messaging_events` y ahí no sobrevive: el log dice «listening», el hub conecta y no llega ningún evento, sin error. `pg_notify`
  sí funciona por el pooler (`PgMessagingNotifier`). Por eso el listener lee `ConnectionStrings:QepDatabaseListener` (directo a
  `pg-platform-rw`) y cae a `QepDatabase` si falta, está vacía o es un `#{…}#` sin resolver (`ListenerConnectionString`). El log de
  arranque dice qué clave usó (`QepDatabaseListener` o `QepDatabase`), nunca el valor. **La causa está inferida, no medida:** se
  verifica tras desplegar con los bytes del WebSocket en el ingress; handshake más pings de 13 bytes y nada más significa que siguen
  sin llegar eventos.
- **El `AppHub` une cada conexión a un grupo personal del miembro, y ese grupo no se suscribe** (spec 2026-11-22 §9.1). `OnConnectedAsync` agrega la
  conexión a `RealtimeTopics.MemberGroupOf(tenant, miembro)` (`{tenant}:@member:{miembro}`) con la membresía de las claims; `IRealtimeMemberPublisher`
  (`PgRealtimePublisher`) entrega ahí por el mismo `pg_notify`, con `MemberId` en el sobre. Es infalsificable por construcción: ningún
  `IRealtimeTopicAuthorizer` puede declarar un prefijo que empiece por `@` (`NoAuthorizerPrefixStartsWithAt`), `Subscribe` y `Unsubscribe` ignoran o
  rechazan lo que empieza por `@`, y un pod viejo que lee el sobre sin `MemberId` lo manda a un grupo vacío. El grupo no revisa módulo ni permiso al
  conectar: quien publica ahí (alerta de entrante, campana) lo revisa antes. Los payloads son ids, nunca texto de un mensaje.
- **La presencia de la bandeja es compartida sólo con `Valkey:Areas:Presence`, y es condición para subir `replicas`** (spec 2026-11-13, D-2).
  `MessagingHub` escribe en `IMessagingPresence` y el reporte de uso lee por `IMessagingPresenceReader`; los registra `AddQepValkey`. Prendida,
  cada pod escribe a sus conectados en `qep:presence:{tenantId}` (sorted set, latido de 30 s en `PresenceHeartbeatWorker`, ventana de lectura de
  90 s, `EXPIRE 120`). Apagada, o con Valkey caído, vuelve a ser por pod: el registro local `MessagingPresence`, y con más de una réplica cada una
  ve sólo a sus conectados, a medias y sin error (con Valkey caído queda un `Warning` por lectura y `qep_valkey_fallbacks_total{area="presence"}`).
  Desconectar quita al miembro sólo si el pod no le tiene otra pestaña (`MessagingPresence.HasConnection`); si sigue conectado en otro pod, el
  latido de ese pod lo vuelve a poner en ≤ 30 s. Un camino nuevo que lea la presencia pasa por el lector, no por `Snapshot()`, que es sólo local.
- **`ValkeyFixedWindowRateLimiter.AttemptAcquireCore` tiene que seguir negando** (spec 2026-11-13, D-3).
  - `RateLimitingMiddleware` llama primero `AttemptAcquire` y sólo con un rechazo pasa a `AcquireAsync`. Si el camino
    síncrono concede, por ejemplo desde el respaldo de memoria, todas las políticas salen de Valkey en silencio y vuelven
    a contar por pod. Lo amarran `TheSynchronousAttemptDefersToTheAsyncPath` y los `*SharedAcrossTwoHosts` de
    `ExternalApiSurfaceTests`.
  - Bajo la ACL de producción (`-@scripting`) no hay Lua. El contador es `MULTI` + `INCR` + `EXPIRE … NX` + `EXEC`.
  - Una política nueva de ventana fija pasa por `SharedRateLimitPartitions.FixedWindow`, no por
    `RateLimitPartition.GetFixedWindowLimiter`. Si no, queda por pod y sin error.
  - Una partición que lleve el patrón de ruta va con `hashPartition: true`.
- **Todo camino nuevo que revoque o borre sesiones llama `RemoveAsync` de la caché antes y después del guardado** (spec 2026-11-13,
  D-4). Vale para Identity (`ISessionCache`) y para el comprador (`IBuyerSessionCache`): el `DEL` de antes cubre el request que lee
  durante la transacción, el de después al que leyó Postgres antes del commit. Si falta, con `Valkey:Areas:Sessions` prendido la sesión
  revocada se sigue sirviendo hasta 15 min, sin error. Un fallo se loguea en `Error` con el id de la sesión, nunca con el hash. Lo
  amarran `CachedSessionTests` (Identity) y `CachedBuyerSessionTests` (Buyers); los caminos de hoy son `SessionService`,
  `SessionRevocationWorker`, `OrphanUserCleanupWorker`, `BuyerSessions` y `DeleteBuyerAccount`.
- **Una `BuyerSession` que devuelve `BuyerSessions.TryResolveAsync` no se guarda.** Con la caché prendida es una entidad reconstruida
  (`BuyerSession.Restore`) que EF no sigue: un `Revoke` o un `Touch` sobre ella y un `SaveChanges` no mandan ningún `UPDATE`, y el
  logout queda «hecho» con la fila viva. Revocar pasa por `BuyerSessions.RevokeCurrentAsync`, que lee la fila. Lo amarra
  `CachedBuyerSessionTests.LogoutWithACachedSessionRevokesTheRow`.
- **Todo cambio que altere los permisos efectivos de un tenant llama `ITenantAccessVersion.BumpAsync` antes y después del guardado**
  (spec 2026-11-13, D-5). Roles, membresías (suspender, reactivar, quitar, activar, aceptar invitación), módulos y estado del tenant ya
  lo hacen. Si un camino nuevo no lo llama, la caché sirve los permisos de antes hasta 60 s: un miembro suspendido sigue entrando, sin
  error. **Desde el 2026-11-17 la caché existe también con el área apagada**: `MemoryPermissionCache` (por pod) decora a
  `AuthorizationService` igual que la de Valkey, y la sesión tiene `MemorySessionCache` (15 min, con marca de revocación). Dos
  consecuencias: (1) una prueba con auth real que cambie roles, módulos o estado del tenant por SQL o por `DbContext` directo, después
  de que esa persona ya hizo un request al tenant, llama `BumpAsync` o ve el 200 de antes (`RealAuthenticationApiTests` lo hace en
  `SetTenantStatusAsync` y en el apagado de Catalog); (2) con más de una réplica las dos áreas de Valkey tienen que estar prendidas,
  y `MultiReplicaInventory.SharedStateSwitches` lo exige. Lo amarran `CachedAuthorizationTests` (Tenancy), `MemoryPermissionCacheTests`
  y `MemorySessionCacheTests` (Bootstrapper.UnitTests) y `RequestQueryBudgetTests` (Tenancy), que cuenta los comandos SQL de un
  request real: `GET /memberships?search=` eran 7 + N miembros (19 en producción, 3,27 s con la base en otro nodo) y ahora son 3 con la
  caché caliente. Un lector nuevo de una lista de personas pide los correos por `IUserDirectory.GetEmailsAsync`, en lote, nunca
  `GetEmailAsync` en un bucle. Dos exposiciones nuevas mientras las áreas de Valkey estén apagadas, donde antes el efecto era
  inmediato: (3) durante un `RollingUpdate` con `maxSurge: 1` (`k8s/prod-deployment.yaml`) conviven dos pods por minutos, y una
  suspensión o un cambio de roles aplicado en uno llega al otro en ≤ 60 s, y una revocación de sesión en ≤ ~5 min (el umbral de
  toque de `SessionService` vuelve fallo toda entrada más vieja); (4) un runbook que cambie por SQL `tenancy.tenant_modules`, roles,
  membresías o `identity.sessions` —prender `pos` con la fila de `tenant_modules`, por ejemplo— tiene efecto en el pod que ya corre
  en ≤ 60 s (permisos) o ≤ 5 min (sesiones), no en el siguiente request.
- **Una tabla de cola nueva entra al mapa de `QueueDoorbellInterceptor`** (`BuildingBlocks.Infrastructure`, `Tables`), con su cola en
  `DoorbellQueues`, y su worker espera con `doorbell.Listen(...)`. Sin eso el worker sólo se entera en la red de seguridad, no al
  insertar, y nada falla. El `DbContext` que mapee una de esas tablas lleva el interceptor: lo amarra
  `CompositionRootTests.EveryDbContextThatMapsADoorbellTableHasTheInterceptor`. Lo que no es un `INSERT` (iniciar o reanudar una
  campaña es un `UPDATE`) timbra a mano después del commit (`Campaigns.cs`).
- **Un consumidor nuevo de `platform.outbox_messages` escucha `DoorbellQueues.Outbox`**, con `DoorbellQueues.SafetyNet(DoorbellQueues.Outbox)`,
  como `OutboxPublisherWorker` o `NotificationsDeliveryWorker`. Un timbre de `outbox` despierta a todos los oyentes del proceso; un
  consumidor que siga con su `Task.Delay` de antes funciona, pero no se entera del timbre.
- **Un comando nuevo contra Valkey tiene que pasar la ACL de producción** (`~qep:* &qep:* +@all -@dangerous -@scripting +info`,
  DP-V4; `+info` desde 2026-11-17): sin Lua (`EVAL`, `SCRIPT`), sin `KEYS`, `FLUSH*` ni `CONFIG`, y toda clave o canal con prefijo
  `qep:`. `INFO` se permite sólo para el cliente, nunca desde el código: sin él, StackExchange.Redis detecta la réplica con un
  `SET <guid> "" PX 1` fuera de `~qep:*` en cada chequeo de 60 s, y `ValkeyACLDenied` dispara todo el tiempo por ruido propio;
  `CONFIG` sí se apaga en el `CommandMap` (`ValkeyConnection.BuildOptions`). Lo amarra `TheClientSendsNothingTheAclDenies`. El gateway traga
  un `NOPERM` como respaldo del área, así que en producción no se ve como error: se ve como
  `qep_valkey_fallbacks_total{area=…}` subiendo. Las primitivas de presencia y limitadores las cubre `ValkeyAclTests`; sesiones,
  permisos, timbres, líder del muestreador y compuerta del toque, `ValkeyAclCompatibilityTests` (las dos en `Bootstrapper.UnitTests`),
  que exige el efecto en Valkey **y** cero respaldos con los adaptadores reales. Un área nueva suma la suya. Si falla, es un hallazgo: la ACL no se relaja.
