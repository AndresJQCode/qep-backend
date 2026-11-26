# Valkey como estado efímero compartido entre réplicas — diseño

Fecha: 2026-11-13. Estado: alcance completo aprobado por el owner el 2026-11-13; detalle para aprobación. Complementa
[`2026-10-29-replicas-multiples-design.md`](2026-10-29-replicas-multiples-design.md): resuelve su D-7 (presencia), su
DP-1 (limitadores por pod) y cierra DP-3 con la decisión «Valkey». Todo lo que este documento afirma está verificado en
el código del worktree `feature/replicas-multiples` al 2026-11-13, salvo donde dice *no verificado*.

## Por qué

Para subir a `replicas: 2` sin que la app se comporte distinto, el spec de réplicas encontró estado que vive en la
memoria del proceso y que con dos pods se parte en dos: la presencia de la bandeja (`MessagingPresence.cs:20-35`) y los
ocho limitadores de `AddRateLimiter` (`Program.cs:86-202`) más el de la API externa (`ApiKeyRateLimiter.cs:14-28`). Las
soluciones de ese spec eran, para cada caso, una tabla más en Postgres o aceptar el doble de intentos.

Aparte de lo que se rompe, hay lo que se duplica. Cada pod le pega a Postgres por cosas que no son datos del negocio:

- La sesión se busca por hash en `identity.sessions` en **cada** request autenticado
  (`SessionCookieAuthenticationHandler.cs:38` → `SessionService.ValidateAsync`, `SessionService.cs:55-68`), y las
  de comprador igual (`BuyerSessions.cs:39`).
- Los permisos se resuelven en cada request: membresía, estado del tenant, roles personalizados y módulos
  (`ExternalClaimsTransformation.cs:112` → `AuthorizationService.cs:31-58` → `MembershipDirectory.cs:15-29`,
  `TenantRoleCatalog.cs:17`, `TenantModules.cs:9`, que dice «sin caché» a propósito). Son tres o cuatro consultas por
  request, por pod.
- Los workers sondean. El del bot cada segundo (`AutomationInboxWorker.cs:59`, `Automations:Workers:InboxPollSeconds`
  = 1): 60 reclamos por minuto por pod, 120 con dos, casi todos vacíos. Las entregas de webhooks cada 3 s
  (`Messaging:Workers:DeliveryPollSeconds`), los medios cada 5, los programados cada 5, las campañas cada 10, los
  exports cada 5 (`ExportJobWorker.cs:16`), el publicador del outbox cada 15 (`OutboxPublisherWorker.cs:14`) y los
  **diez** consumidores de correo de Notifications cada 15 cada uno (`OutboxDeliveryWorker.cs:40` y sus diez subclases).
- `ApiKeyAuthenticationHandler.cs:71` manda un `UPDATE` por request de la API externa que casi siempre no cambia nada
  (`ApiKeyRepository.cs:42-50` filtra por `TouchInterval` = 1 min, `ApiKeyRepository.cs:11`).
- El cubo de envío de campañas es un `FOR UPDATE` por lote (`ConnectionSendBudget.cs:28-108`, Infrastructure).

**Decisión del owner, 2026-11-13: adoptar Valkey para todo el estado efímero compartido**, no sólo para lo que rompe
con dos réplicas. Es una pieza más en el clúster, y a cambio cada cosa de la lista de arriba pasa a ser una operación de
memoria compartida, idéntica desde cualquier pod. Postgres se queda con lo que es dato: filas, reclamos, leases,
candados y el fan-out en tiempo real.

### Qué decisión anterior reemplaza

Hasta hoy el stack decía **«no hay Redis»**, y lo dicen dos comentarios del código y un spec:

- `Modules.Messaging.Application/ConnectionSendBudget.cs:12`: «Es un token bucket en Postgres —no hay Redis en el
  stack— que comparten todas las réplicas».
- `Modules.Messaging.Infrastructure/Realtime/PgMessagingNotifier.cs:8`: «Spec 2026-10-14, decisión 6: el fanout entre
  pods es `pg_notify`, sin Redis». *Ese spec no está en `docs/superpowers/specs/`* (los cuatro del 2026-10-14 son
  plantillas, Instagram, marcar no leída y versión desplegada); la decisión se conoce por el comentario.
- Spec `2026-10-11-contactos-plantillas-campanas-design.md:605`, P5: «Vive en Postgres (no hay Redis en el stack)».

Lo que esta decisión reemplaza es la **premisa** («no hay Redis»), no las dos soluciones que salieron de ella:

- El fan-out por `pg_notify` + `LISTEN` **se queda** (ver «Fuera de alcance»): funciona entre pods, el spec de réplicas
  lo verificó, y su pendiente en producción es la cadena directa `QepDatabaseListener`, no Valkey.
- El cubo en Postgres **se queda como respaldo** de D-6, no se borra.

Los dos comentarios se reescriben para apuntar a este documento. El de P5 no se toca: los specs son históricos.

## Qué se construye

Todo lo nuevo va detrás de un interruptor maestro `Valkey:Enabled` y uno por área (`Valkey:Areas:*`, ver D-1), apagados
por defecto. Con todo apagado el código se comporta exactamente como hoy; con Valkey caído, cada área cae a lo que hace
hoy. **Regla que manda sobre todas las áreas: ningún camino de seguridad falla abierto.** Si Valkey no responde, la
sesión y los permisos se leen de Postgres, y el limitador vuelve al de memoria del pod; nunca «se deja pasar».

Convención de claves: `qep:{área}:{…}`, en minúsculas, con los GUID en formato `D`. Toda clave lleva TTL; nada en Valkey
es permanente ni hay nada que migrar.

### 1. Infraestructura y cliente (D-1)

**Valkey lo despliega el owner en el repo de plataforma `migracion-k8s`**, no en este: la infraestructura del clúster
vive allá (regla de memoria «Repo de plataforma migracion-k8s»). Lo que este repo necesita de ese despliegue es una
cadena de conexión y, para la verificación, el namespace y el nombre del pod (DECISIÓN-PENDIENTE DP-V4).

**Configuración.**

- `ConnectionStrings:Valkey`, en el formato de StackExchange.Redis (`host:6379,password=…,abortConnect=false`).
  Lleva contraseña, así que va en el Secret, no en el ConfigMap: una entrada
  `ConnectionStrings__Valkey: "#{VALKEY_CONNECTION_STRING}#"` en `k8s/prod-secret.yaml`, con el mismo patrón y el mismo
  comentario que `ConnectionStrings__QepDatabase`. El nombre de la variable del grupo `Backend-prod` es una propuesta;
  el owner lo fija al crearla. En local va por user-secrets, como `QepDatabase` (README § Secretos de usuario), y
  `appsettings.example.json` la documenta junto a la otra.
- Sección `Valkey` (`ValkeyOptions`, `SectionName = "Valkey"`): `Enabled` (bool, default `false`),
  `Areas` con un bool por área (`Presence`, `RateLimiting`, `Sessions`, `Permissions`, `SendBudget`, `Doorbells`,
  `ApiKeyTouch`; todos `false`), y `OperationTimeoutMs` (default 100: una operación que tarde más que eso cuenta
  como Valkey caído para esa llamada y dispara el respaldo). La clase entra a `appsettings.example.json` o
  `ConfigurationExampleTests` se pone roja.
- `ValkeyOptionsValidator` con `ValidateOnStart`, como `ShopifyAppOptionsValidator`: en `Production` la cadena es
  obligatoria **aunque todas las áreas estén apagadas** (el paso 1 del despliegue la crea antes que el 2), y un valor
  que empiece con `#{` cuenta como ausente (`ShopifyAppOptionsValidator.cs:15, 19-20`; el precedente de
  `Store__Storefront__SharedSecret`, que no lo trata, deja un pod arrancando con el token literal). Ningún mensaje del
  validador lleva el valor.

**Cliente.** `StackExchange.Redis` entra a `Directory.Packages.props` (versión central, como todo) y se regeneran los
`packages.lock.json` con `dotnet restore --force-evaluate`, que se commitean junto con el props: el `Dockerfile` corre
`--locked-mode` (`Dockerfile:18`) y sin los locks el build de la imagen muere con `NU1004`. Son **152** archivos de lock
en el worktree (uno por `.csproj`), no los 74 que dice el gotcha de `CLAUDE.md`: la cifra quedó vieja y se corrige en el
mismo cambio.

Un solo `IConnectionMultiplexer` singleton, creado en Bootstrapper con `ConnectionMultiplexer.ConnectAsync` y
`AbortOnConnectFail = false`: si Valkey no está al arrancar, el pod arranca igual y el cliente reconecta solo. El log de
arranque dice qué clave usó (`ConnectionStrings:Valkey`) y si conectó, **nunca el valor** (precedente:
`ListenerConnectionString`). Cada operación lleva el `OperationTimeoutMs` por `CancellationToken`.

**Readiness: un gauge y un log, no la sonda.** `/health/ready` hoy mira sólo la base (`QepHealthChecks.cs:31-38`,
etiqueta `ready`). Valkey **no** entra a esa etiqueta: todas las áreas tienen respaldo, así que un pod sin Valkey sigue
sirviendo, y sacarlo del `Service` sería peor que lo que evita. En su lugar: un gauge `qep_valkey_connected` (0/1) en el
`Meter` de observabilidad de Bootstrapper, un contador `qep_valkey_fallbacks_total{area}` que suma cada vez que un área
cae al respaldo, y un log Warning al perder la conexión e Information al recuperarla (`ConnectionFailed` /
`ConnectionRestored` del multiplexer). Una alerta sobre `qep_valkey_connected == 0` durante más de 5 minutos entra al
dashboard junto con las de colas.

**Local y pruebas.** `compose.yaml` gana un servicio `valkey` (imagen `valkey/valkey:9.1.3-alpine`, puerto `6379`, sin
contraseña, sin volumen: nada que persistir). Las pruebas de integración usan Testcontainers: hoy sólo existe
`Testcontainers.PostgreSql` 4.14.0 (`Directory.Packages.props:49`); se agrega `Testcontainers.Redis` en la misma
versión con la imagen `valkey/valkey:9.1.3-alpine` (`RedisBuilder` acepta otra imagen; *no verificado en esta máquina* que
su estrategia de espera —un `PING`— responda igual con Valkey; si no, el plan B es `ContainerBuilder` genérico con
espera por puerto). Un contenedor por clase de prueba, como los de Postgres; la regla de `CLAUDE.md` de una corrida de
integración a la vez sigue valiendo.

**Dónde vive el código (capas).** `ArchitectureTests` exige que `Modules.<Modulo>.Application` no referencie EF Core ni
Npgsql (`ApplicationDoesNotReferencePersistenceLibraries`, en cada `<Modulo>LayerTests`). La regla para Valkey es más
estricta: **`StackExchange.Redis` lo referencia sólo Bootstrapper.** Ni Application ni Infrastructure de ningún módulo
lo ven. Cada área define su puerto en el Application que lo necesita (algunos ya existen: `IMessagingPresence`,
`IConnectionSendBudget`, `ISessionService`, `IAuthorizationService`) y el adaptador con Valkey va en
`src/Bootstrapper/Valkey/`, con un adaptador nulo (o el de hoy) cuando el área está apagada. Es el mismo reparto que
`PlatformUsageSource`, `CustomerMergeReferences` y los puertos de Shopify. Una prueba nueva de arquitectura lo amarra
(«Pruebas», P-1). Lo compartido entre adaptadores —el multiplexer, el timeout, el contador de fallbacks, el prefijo de
claves— es un `ValkeyGateway` interno de Bootstrapper; no se crea un `IEphemeralStore` genérico en BuildingBlocks:
cada área usa una primitiva distinta (sorted set, contador, hash con Lua, pub/sub) y un puerto genérico sólo serviría
para filtrar la API de Redis hacia los módulos.

### 2. Presencia de la bandeja (D-2)

Resuelve D-7 del spec de réplicas por una tercera vía: ni la tabla `messaging.hub_presence` ni retirar «Conectados».

- **Primitiva:** un sorted set por tenant, `qep:presence:{tenantId}`, miembro = `memberId`, score = segundos Unix del
  último latido. `EXPIRE 120` en cada latido, para que un tenant sin nadie conectado desaparezca solo.
- **Escritura.** `MessagingPresence` conserva su diccionario local (`connectionId → (tenantId, memberId)`), que sigue
  alimentando el hub (`MessagingHub.cs:58, 63`). Un hosted service de Bootstrapper, cada **30 s**, toma la foto local
  y manda un `ZADD` por tenant con todos sus miembros conectados a este pod (en un batch), más
  `ZREMRANGEBYSCORE -inf (ahora − 90)` para que el set no crezca. Al conectar, el `ZADD` del miembro sale de inmediato
  (no espera el latido) para que «Conectados» lo muestre al momento. Al desconectar, si este pod no tiene otra conexión
  del mismo miembro, `ZREM`; si el miembro sigue conectado por otro pod, el latido de ese pod lo vuelve a poner en ≤ 30 s.
  Un pod muerto por `SIGKILL` no quita a nadie: los saca la ventana de lectura.
- **Lectura.** `PlatformUsageSource.cs:42` deja de leer `presence.Snapshot()` y pasa por un puerto nuevo de
  Messaging.Application, `IMessagingPresenceReader.ListConnectedAsync(tenantIds, ct)`, que hace
  `ZRANGEBYSCORE (ahora − 90) +inf` por cada tenant de las filas que ya trae la consulta (no hace falta un índice de
  tenants: la consulta ya los lista todos). `IMessagingPresence` conserva `Connected` y `Disconnected`; `Snapshot()` se
  queda para el respaldo.
- **Respaldo:** diccionario local, como hoy, con un log Warning por llamada fallida. El reporte muestra sólo los
  conectados a este pod; el DTO (`PlatformConnectedMemberDto`) no cambia.

### 3. Limitadores de tasa compartidos (D-3)

Resuelve DP-1 del spec de réplicas: los contadores pasan a Valkey y los límites quedan como están, sin dividirlos por
pod ni atarlos a `replicas`.

Las ocho políticas de `AddRateLimiter` (`Program.cs:86-202`) —siete de ventana fija y la de concurrencia del webhook— y
el limitador por clave. Pasan a Valkey las siete ventanas fijas más el límite por clave:

| Política | Partición | Límite | Pasa a Valkey |
| --- | --- | --- | --- |
| `Public` (`:97-101`) | IP | 120/min | Sí |
| `Credentials` (`:105-116`) | patrón de ruta + IP | 10/min | Sí |
| `Telemetry` (`:120-131`) | IP | 600/min | Sí |
| `Webhook` (`:135-148`) | una sola | concurrencia 64, cola 256 (`MessagingWebhookOptions.cs:11, 13`) | **No** |
| `External` (`:152-163`) | IP | `ApiAccess:RateLimit:PerIpPerMinute` = 600 | Sí |
| `StorefrontRead` (`:167-176`) | IP del visitante | 300/min | Sí |
| `StorefrontSubmit` (`:177-186`) | IP del visitante | 5 cada 10 min | Sí |
| `StorefrontCredentials` (`:190-200`) | patrón de ruta + IP del visitante | 10/min | Sí |
| `ApiKeyRateLimiter` (`ApiKeyRateLimiter.cs:14-28`) | id de clave | `ApiAccess:RateLimit:PerKeyPerMinute` = 300 | Sí |

`Webhook` es un limitador de **concurrencia** y protege al pod: dos pods, el doble de capacidad, es lo buscado (spec de
réplicas, DP-1). Se queda en memoria.

- **Primitiva:** ventana fija compartida. Clave `qep:rl:{política}:{partición}:{inicioDeVentana}` donde
  `inicioDeVentana` es `floor(ahoraUnix / ventana) × ventana`. Una transacción sin Lua —la ACL de producción quita
  `@scripting` (DP-V4)—: `MULTI`, `INCR` (`INCRBY` con más de un permiso), `EXPIRE clave (ventana + 1) NX`, `EXEC`;
  devuelve el contador. Un solo viaje por request, atómico entre pods, y la clave muere sola al cerrar la ventana. El permiso se niega si el contador supera `PermitLimit`. La partición entra
  a la clave con un hash corto (SHA-256 truncado) cuando lleva un patrón de ruta, para que la clave no cargue texto
  libre.
- **Punto de extensión.** `System.Threading.RateLimiting` permite un `RateLimiter` propio por partición:
  `RateLimitPartition.Get(key, factory)` devuelve lo que la fábrica construya. Se escribe
  `ValkeyFixedWindowRateLimiter : RateLimiter` (en Bootstrapper) que en `AcquireAsyncCore` corre la transacción. Su
  `AttemptAcquireCore` —el camino síncrono— **siempre niega, sin contar nada y sin metadata**: el
  `RateLimitingMiddleware` de ASP.NET llama primero `AttemptAcquire` y sólo espera `AcquireAsync` cuando ése niega
  (verificado contra el IL de ASP.NET Core 10.0.11). Si el camino síncrono concediera, toda política saldría de Valkey en
  silencio. `ApiKeyRateLimiter` ya construye con `PartitionedRateLimiter.Create` y llama `AcquireAsync`
  (`ApiKeyRateLimiter.cs:20-32`): cambia la fábrica, no el llamador. Las siete políticas de ventana fija de `Program.cs`
  pasan a construirse desde una fábrica de Bootstrapper (`SharedRateLimitPartitions`) que recibe la política y las
  opciones y decide Valkey o memoria según el flag; los números no se mueven de donde están.
- **`Retry-After`.** `ExternalRateLimitRejection.WriteAsync` (`Program.cs:91`) lo calcula hoy del lease del limitador
  de memoria; con Valkey, el lease trae el segundo en que cierra la ventana (`inicioDeVentana + ventana − ahora`).
- **Respaldo:** el limitador de ventana fija de memoria con las mismas opciones, por pod, construido perezosamente la
  primera vez que Valkey falla para esa partición. Nunca «permitir». Con Valkey caído se vuelve exactamente a hoy: el
  doble de intentos con dos pods, durante lo que dure la caída, y el contador de fallbacks lo muestra. **Con Valkey
  intermitente** (unas operaciones pasan y otras vencen el tope) cada request cuenta en Valkey o en el respaldo de su pod,
  así que un cliente puede llegar hasta (N + 1) × límite con N pods en vez de N × límite: acotado, nunca sin límite.

### 4. Caché de sesión (D-4)

- **Qué se cachea.** La validación de `SessionCookieAuthenticationHandler.cs:38` → `SessionService.ValidateAsync`
  (`SessionService.cs:55-68`), y la de comprador en `BuyerSessions.TryResolveAsync` (`BuyerSessions.cs:39`). La clave es
  el **hash** del token, que ya es lo que se guarda en la fila (`Session.TokenHash`, `BuyerTokenCodec.Hash`): el token
  crudo nunca toca Valkey. Claves `qep:sess:{hash}` (Identity) y `qep:sess:buyer:{hash}` (Buyers). El valor es
  pequeño: `userId` (o `accountId` + `host`), `lastSeenAt` y el vencimiento efectivo (el menor entre la vida absoluta y
  la inactividad: `QepSessionOptions`, 30 y 7 días; `BuyerSession`, 30 y 7).
- **TTL = min(lo que le queda a la sesión, 15 min).** Y una entrada cuyo `lastSeenAt` tenga más de `TouchThreshold`
  (5 min, `SessionService.cs:19`; `BuyerSession.TouchThreshold`) se trata como fallo: se va a Postgres, que toca la fila
  como hoy, y se reescribe la entrada. Así el seguimiento de inactividad no pierde precisión y la lectura a Postgres
  baja de «cada request» a «una cada 5 minutos por sesión».
- **Fallo de caché:** se lee Postgres como hoy y, si la sesión es válida, `SET … EX ttl`. Una sesión inválida **no se
  cachea**: un hash desconocido no gana nada y un negativo cacheado alargaría un logout.
- **Error de Valkey (timeout, desconexión):** se lee Postgres. Esto importa para lo que sigue: mientras Valkey no
  responde, ninguna entrada vieja se puede servir.
- **Todo camino que revoca borra la entrada (`DEL`), antes del `SaveChanges` y otra vez después del commit.** El
  `DEL` previo cubre el caso en que el proceso muera entre el commit y el `DEL` posterior; el posterior cubre el
  request concurrente que repobló la entrada entre el `DEL` previo y el commit. Los caminos, enumerados en el código:
  - Identity: logout (`AuthSessionEndpoints.cs:119` → `SessionService.RevokeAsync`), restablecer contraseña
    (`PasswordReset.cs:131` → `RevokeAllForUserAsync`, que ya carga las filas con su hash), `SessionRevocationWorker`
    (membresía suspendida o quitada; carga `dbContext.Sessions` con hash, `SessionRevocationWorker.cs:139-145`) y
    `OrphanUserCleanupWorker`, que **borra** las filas (`OrphanUserCleanupWorker.cs:199-202`): borrar también es revocar.
  - Buyers: recuperación de contraseña (`BuyerPasswordRecovery.cs:118` → `RevokeAllAsync`), cambio de contraseña
    (`ChangeBuyerPassword.cs:63` → `RevokeOthersAsync`) y borrar la cuenta (`DeleteBuyerAccount.cs`, que quita la
    cuenta y con ella sus sesiones). Los dos primeros pasan por `IBuyerSessionRepository.RevokeActiveAsync`, un
    `UPDATE` masivo que hoy no devuelve nada: pasa a devolver los `token_hash` revocados (`RETURNING`) para poder
    borrarlos. El borrado de cuenta lista los hashes antes de quitar la cuenta. `BuyerHousekeepingWorker` sólo purga
    filas ya revocadas o vencidas (`BuyerHousekeepingWorker.cs:131-133`): esas entradas ya no existen o mueren por TTL.
  - El puerto es `ISessionCache` en `Modules.Identity.Application` e `IBuyerSessionCache` en
    `Modules.Buyers.Application` (`GetAsync`, `SetAsync`, `RemoveAsync(hashes)`), con el adaptador de Valkey y uno nulo
    en Bootstrapper. El decorador de lectura envuelve `ISessionService` (registrado en
    `IdentityInfrastructureExtensions.cs:51`) y `BuyerSessions`; la escritura del `DEL` va en los caminos de arriba, no
    en el agregado.
- **Riesgo, escrito para el owner.** Si un `DEL` falla (Valkey devuelve error justo ahí) y Valkey sigue sirviendo
  lecturas, una sesión revocada se acepta **hasta 15 minutos** más (o hasta 5, si entra el toque). Hoy ese plazo es cero.
  Se mitiga con el doble `DEL`, con que el error se loguea en **Error** con el id de la sesión (no el hash), y con que
  una caída completa de Valkey no abre nada (las lecturas van a Postgres). El acceso al tenant no depende de esto:
  `MembershipDirectory.FindActiveRolesAsync` sigue negando a una membresía suspendida en el request siguiente, y con
  D-5 el bump de versión del tenant lo hace inmediato. Lo que una sesión revocada y aún cacheada permite es lo que no
  pide tenant (`/auth/me`) y los tenants donde la persona sigue activa. Lo amarra P-5.

### 5. Caché de permisos (D-5)

- **Qué se cachea.** El resultado de `IAuthorizationService.ResolvePermissionsAsync(userId, tenantId)`
  (`AuthorizationService.cs:31-58`), que es lo que `ExternalClaimsTransformation.cs:112` convierte en claims: la lista de
  permisos ya enmascarada por módulos y por tenant operador, o `null` («sin membresía activa»). Se cachean las dos
  cosas: un `null` cacheado 60 s es un acceso negado que tarda hasta un minuto en volver a abrirse tras reactivar, y ese
  es el lado seguro.
- **Clave y TTL:** `qep:perm:{userId}:{tenantId}`, `EX 60`. El valor guarda la lista y la **versión del tenant con la
  que se construyó**.
- **Versión del tenant:** `qep:tenantver:{tenantId}`, un contador (`INCR`, `EX` de 30 días refrescado en cada `INCR`:
  si se pierde, la lectura lo trata como 0 y toda entrada con versión > 0 se descarta, que es el lado seguro). La
  lectura hace `MGET perm, tenantver` en un viaje: si la versión guardada en la entrada no coincide con la actual, la
  entrada se descarta. Al construir una entrada, la versión se lee **antes** de resolver en Postgres, y se guarda esa:
  un cambio que ocurra en el medio sube el contador y la entrada nace vieja.
- **Quién sube la versión (`INCR`, antes del `SaveChanges` y otra vez tras el commit, como el `DEL` de D-4):**
  - Authorization: `CreateRoleHandler`, `UpdateRoleHandler`, `DeleteRoleHandler` (`RoleCommands.cs:111, 155, 265`).
  - Tenancy, membresías: `UpdateMemberRolesHandler`, `SuspendMemberHandler`, `RemoveMemberHandler`,
    `ReactivateMemberHandler` (rutas en `MembershipEndpoints.cs:39-62`), y la activación de una invitación
    (`MembershipActivationService`, `InvitationService`; rutas en `src/Api/InvitationEndpoints.cs:31, 41` y el
    auto-accept de `/auth/session`). `InviteMemberHandler` crea una membresía pendiente, que no concede nada: no sube.
  - Tenancy, consola de operador (`OperatorEndpoints.cs:35, 52`): `ChangeTenantModulesHandler` y
    `ChangeTenantStatusHandler` (un tenant que deja de estar `Active` niega todo, `MembershipDirectory.cs:24-28`).
    `ChangeTenantProvider` no toca permisos: no sube. El alta de tenant (`TenantRegistrationService`,
    `GrantDefaultModulesOnSignup`) no tiene entradas que invalidar; subir no estorba.
  - El puerto es `ITenantAccessVersion.BumpAsync(tenantId)` en `Modules.Tenancy.Application`
    (Authorization.Application ya referencia Tenancy.Application: `AuthorizationService.cs:1`). Adaptador y nulo en
    Bootstrapper; el decorador `CachedAuthorizationService : IAuthorizationService` también.
- **Respaldo:** Postgres, como hoy. Y si el `INCR` falla con Valkey sirviendo lecturas, una suspensión o un cambio de
  rol tarda **hasta 60 s** en verse (hoy, cero). Mismo tratamiento que en D-4: doble `INCR`, log en Error con el
  tenant, y una caída completa no abre nada. El owner acepta ese minuto al aprobar el alcance; si no lo acepta, el área
  se queda apagada y lo demás no cambia.
- `TenantRoleCatalog` conserva su memo por scope (`TenantRoleCatalog.cs:17`): con fallo de caché, un request que
  resuelve tres veces sigue consultando una.

### 6. Presupuesto de envío de campañas (D-6)

- **Hoy:** `IConnectionSendBudget` (`Modules.Messaging.Application/ConnectionSendBudget.cs:17-26`) con la implementación
  en Postgres (`Modules.Messaging.Infrastructure/Campaigns/ConnectionSendBudget.cs:28-108`), llamada desde
  `CampaignDispatch.cs:195` (`AcquireAsync`) y `:397` (`ThrottleAsync` tras un 130429).
- **Primitiva:** un hash `qep:sendbudget:{connectionId}` con los mismos campos de la fila (`tokens`, `rate`,
  `refilled_at`, `throttled_until`, `updated_at`), y **dos scripts Lua** que reproducen, operación por operación, el
  SQL de `AcquireAsync` (`:42-81`: subida del ritmo, relleno acotado por el final de la pausa, concesión, `RetryAfter`)
  y de `ThrottleAsync` (`:95-107`: mitad del ritmo con la subida pendiente aplicada, pausa de 30 s, sólo si no hay
  pausa vigente). El `now` sigue entrando por parámetro, nunca el reloj de Valkey, por la misma razón que hoy no es el de
  Postgres (`:21`). `EXPIRE 3600` en cada operación: una conexión que no manda en una hora pierde el hash y la siguiente
  campaña arranca con el cubo lleno al ritmo objetivo, igual que el primer uso de hoy (`:34-40`).
- **Respaldo:** la implementación de Postgres **no se borra**. El adaptador de Bootstrapper intenta Valkey y, ante error,
  llama a la de Postgres para esa operación. Mientras dure la caída los dos cubos divergen: lo peor es una ráfaga de
  hasta dos veces `MessagesPerSecond` durante un segundo, que un 130429 frena como hoy. La fila de
  `messaging.connection_send_budgets` se queda, con su migración.
- **Qué gana:** quita un `FOR UPDATE` por lote del despacho de campañas de Postgres y lo pasa a una operación de
  memoria. Es la menor ganancia de la lista; va por el alcance completo que aprobó el owner y porque la primitiva
  (hash + Lua) es la misma que usan D-3 y D-7.

### 7. Timbres para los workers (D-7)

Los reclamos, las filas y los leases **se quedan en Postgres**. Lo que cambia es cómo se entera un worker de que hay
trabajo: hoy sondea; con esto, espera un aviso y sondea sólo como red de seguridad.

- **Primitiva:** pub/sub. Canal `qep:wake:{cola}`, mensaje vacío (o el id, que el worker ignora). Los productores
  publican **después del commit**, nunca dentro de la transacción: un aviso de algo que luego se deshizo cuesta un
  reclamo vacío, pero un commit sin aviso cuesta esperar la red de seguridad. Pub/sub no guarda nada: un aviso publicado
  cuando ningún worker escucha se pierde, y por eso la red de seguridad no es opcional.
- **Puerto:** `IQueueDoorbell` en `BuildingBlocks.Application` —`RingAsync(cola)` para el productor y
  `WaitAsync(cola, máximo, ct)` para el worker, que vuelve cuando llega un aviso o pasa el máximo—, con el adaptador de
  Valkey en Bootstrapper (un `ISubscriber`, un canal por cola, un `SemaphoreSlim(0, 1)` por cola para que varios avisos
  seguidos cuenten como uno) y uno nulo cuyo `WaitAsync` es `Task.Delay(máximo)`: con el área apagada o Valkey caído,
  el worker sondea al intervalo de seguridad, que es lo que hace hoy con otro número.
- **Worker:** cambia el `PeriodicTimer.WaitForNextTickAsync` por `WaitAsync(cola, min(red de seguridad, próximo
  vencimiento conocido))`, y tras un aviso drena hasta que un lote vuelva vacío. El «próximo vencimiento conocido» es lo
  que el worker ya sabe por su propio trabajo: un reintento con backoff, un destinatario diferido por `RetryAfter`, un
  mensaje programado para dentro de 40 s. Nada de eso llega por un commit, así que el timbre no lo cubre.
- **Colas, con el intervalo de hoy y la red de seguridad propuesta:**

  | Cola | Worker (sondeo hoy) | Productor que timbra | Red de seguridad |
  | --- | --- | --- | --- |
  | `automation` | `AutomationInboxWorker.cs:59` (1 s) | quien inserta en `messaging.automation_inbox` (ingesta, arranque manual, señal `ReleaseRun`) | 15 s |
  | `webhook` | `WebhookDeliveryWorker.cs:71` (3 s) | el endpoint del webhook al guardar la entrega | 15 s |
  | `shopify_webhook` | `ShopifyWebhookDeliveryWorker.cs:52` (15 s) | el endpoint del webhook de Shopify | 30 s |
  | `media` | `MediaCopyWorker.cs:40` (5 s) | la ingesta al insertar `message_media` | 30 s |
  | `scheduled` | `ScheduledMessageWorker.cs:56` (5 s) | crear o reprogramar un mensaje | **5 s, sin cambio**: su disparador es el reloj, no un commit |
  | `campaign` | `CampaignDispatchWorker.cs:40` (10 s) | iniciar o reanudar una campaña | 30 s (los diferidos por presupuesto usan el vencimiento conocido) |
  | `export` | `ExportJobWorker.cs:41` (5 s) | encolar un export | 30 s |
  | `outbox` | `OutboxPublisherWorker.cs:21` (15 s) y los consumidores de `platform.outbox_messages` | toda unidad de trabajo que guarde filas de outbox (ver abajo) | 30 s |

  Los consumidores del outbox de plataforma —los diez de Notifications, `SessionRevocationWorker` (15 s),
  `OrphanUserCleanupWorker` (15 s), `QuotationEventsWorker` (10 s), `CitySynonymEventsWorker` (10 s),
  `CoexistenceEventsWorker` (10 s), `BuyerSignupRequestedWorker` (15 s), `AuditProjectionWorker` (15 s)— escuchan
  todos `qep:wake:outbox`: un commit con outbox despierta a todos, cada uno reclama lo suyo y los que no tienen nada
  vuelven a dormir. Quién timbra: la unidad de trabajo que detecta en su `ChangeTracker` filas de outbox agregadas y
  llama `RingAsync` tras el `SaveChanges`. Son varias unidades de trabajo (Tenancy, Audit, Integrations, Buyers y las
  demás que escriben outbox); el builder las enumera con `grep IOutboxWriter` y las de los módulos con outbox propio.
  El bot pasa de 60 reclamos vacíos por minuto por pod a 4, y responde apenas llega el entrante en vez de hasta un
  segundo después.

- **Dos arreglos que no necesitan Valkey y van en el mismo slice:**
  - **Un solo bucle para los consumidores de correo.** Las diez subclases de `OutboxDeliveryWorker` (`BuyerEmail`,
    `CustomerExport`, `EmailVerification`, `ExistingAccount`, `Invitation`, `PasswordReset`, `ProductExport`,
    `QuotationsExportFailed`, `QuotationsExportReady`, `SupplierExport`) son diez hosted services con diez
    `PeriodicTimer` de 15 s: diez consultas de candidatos cada 15 s por pod. Pasan a un hosted service que recorre las
    diez definiciones en secuencia por tick, con el mismo `DrainAsync`, el mismo reclamo por mensaje y el mismo
    aislamiento (`OutboxDeliveryWorker.cs:97-108`); `OutboxDeliveryWorkerTests` sigue probando `DrainAsync` por
    definición. (El encargo decía «trece»; son diez al 2026-11-13.)
  - **Un líder para el muestreador de colas.** `QueueSamplerWorker` (`QueueSamplerWorker.cs:18, 42-61`) lee seis colas
    cada 15 s en cada pod: con dos pods, doce consultas por tick por el mismo dato. Antes de cada vuelta,
    `SET qep:leader:queue-sampler <pod> NX PX 15000`: el que lo consigue muestrea, el otro salta la vuelta. Sin Valkey
    (área apagada o caída) los dos muestrean, como hoy; el dato es el mismo, sólo cuesta el doble.

### 8. Toque de `last_used_at` de las claves de API (D-8)

`ApiKeyAuthenticationHandler.cs:71` llama `TouchLastUsedAsync` en cada request; el `UPDATE` (`ApiKeyRepository.cs:42-50`)
sólo escribe si pasó `TouchInterval` (1 min, `:11`), pero el viaje a Postgres se hace siempre. Con Valkey:
`SET qep:touch:apikey:{id} 1 NX EX 60` antes del `UPDATE`; si la clave ya existía (el `SET` devuelve falso), no se va a
Postgres. Si Valkey falla, se hace el `UPDATE` como hoy. El puerto es un `IApiKeyTouchGate` en
`Modules.ApiAccess.Application` con el nulo que siempre dice «sí»; el manejo de errores del handler
(`ApiKeyAuthenticationHandler.cs:68-78`: un contador no tumba un envío) no cambia.

## Decisiones pendientes del owner

### DP-V1 (cerrada, 2026-11-13): persistencia de Valkey

**Ninguna**: el `valkey-config` desplegado lleva `save ""` y `appendonly no`. Todo lo que guarda este diseño se
reconstruye solo: la presencia en 30 s, los contadores en una ventana, la sesión y los permisos en el siguiente request,
el cubo en el primer envío, los timbres no se guardan. Un reinicio de Valkey equivale a un minuto de respaldo. Una sola
instancia, sin réplica ni Sentinel (owner, 2026-11-13): la replicación es asíncrona, así que un failover pierde lo mismo
que un reinicio, y los respaldos tienen que existir igual. Guardar en Valkey algo que no se reconstruya reabre esta
decisión.

### DP-V2 (cerrada, 2026-11-13): memoria y política de desalojo

Desplegado: `maxmemory 512mb`, `maxmemory-policy allkeys-lru`, `maxclients 1000`, límite del contenedor 768 Mi. Con los
volúmenes de hoy sobra por mucho (una sesión son ~100 bytes, un permiso ~1 KiB). Todo tiene TTL, así que el desalojo
sólo actúa si algo crece de más, y perder la clave menos usada es un fallo de caché, no un error.

### DECISIÓN-PENDIENTE DP-V3: ¿la readiness depende de Valkey?

**No** (D-1). Todas las áreas tienen respaldo y un pod sin Valkey sigue sirviendo correctamente, sólo más caro; la
alerta sobre el gauge avisa. Si el owner prefiere que el pod salga del `Service` sin Valkey, es agregar la etiqueta
`ready` al chequeo: una línea, pero cambia el modo de falla de «degradado» a «sin servicio».

### DP-V4: dónde queda Valkey en el clúster (desplegado; falta el usuario del backend)

Desplegado por el owner en `migracion-k8s` el 2026-11-13: namespace `valkey`, `Deployment` y `Service` `valkey`
(`valkey.valkey.svc.cluster.local:6379`), imagen `valkey/valkey:9.1.3-alpine` con `redis_exporter` en `9121`. La
autenticación es por ACL (`aclfile`, el usuario `default` deshabilitado), con los usuarios que nombra `valkey-secret`:
`ADMIN_PASSWORD`, `EXPORTER_PASSWORD`, `PROBE_PASSWORD` (claves vistas con `describe`, sin valores).

**Usuario del backend.** Se da de alta con el procedimiento de `k8s/platform/valkey/README.md` («Dar de alta una
app»): usuario `qep`, línea `user qep on ><password> ~qep:* &qep:* +@all -@dangerous -@scripting +info` (el `+info` se
sumó el 2026-11-17: sin él el cliente sondea la réplica con un `SET` fuera de `~qep:*` cada 60 s y `ValkeyACLDenied` dispara
por ruido propio; `CONFIG` se apaga en el `CommandMap` del cliente), y la etiqueta
`valkey.platform/access=true` en el namespace `prod-qep-backend` (sin ella, la `NetworkPolicy` da timeout). La cadena,
en formato de StackExchange.Redis (no la URL `redis://` que el README pone para ioredis), va a `VALKEY_CONNECTION_STRING`
en `Backend-prod`: `valkey.valkey.svc.cluster.local:6379,user=qep,password=…,abortConnect=false`. Es prerrequisito del
paso 2 del despliegue, no del desarrollo: en local, Valkey corre sin contraseña ni ACL.

Lo que esa ACL le impone al diseño:

- **Sin Lua.** La plataforma bloquea `@scripting` por defecto (las RCE de Valkey/Redis pasaron por el motor de Lua) y
  este diseño no lo pide: el contador de rate limit es `INCR` más `EXPIRE … NX` en un `MULTI`, y el cubo de envío (D-6)
  usa `WATCH`/`MULTI` o se queda en Postgres. Si el builder encuentra un área que de verdad necesite Lua, es decisión
  del owner pedir `+@scripting`, no del builder.
- **Todo con prefijo `qep:`**, también lo que StackExchange.Redis hace por su cuenta: su canal de configuración
  (`__Booksleeve_MasterChanged`) y su clave de desempate (`__Booksleeve_TieBreak`) dan `NOPERM`. Se apagan con
  `ConfigurationChannel = ""` y `TieBreaker = ""` en las opciones del multiplexer.
- **`INFO` y `CONFIG` están en `@dangerous`.** El cliente los usa al conectar para detectar el servidor; *no verificado*
  que tolere el `NOPERM` sin quedar desconectado. Lo cubre una prueba de integración con un Valkey de Testcontainers que
  cargue la misma ACL (P-1), antes del primer despliegue.

Las sondas de «Verificación» usan `-n valkey` y `deploy/valkey`.

## Fuera de alcance

- **El fan-out en tiempo real** sigue por `pg_notify` + `LISTEN` (`PgMessagingNotifier.cs:24`, `MessagingEventsListener`):
  funciona entre pods y su pendiente de producción es la cadena directa `QepDatabaseListener`, no esto. El backplane de
  SignalR sobre Redis (`AddStackExchangeRedis`) queda anotado como opción futura si el `LISTEN` directo no se puede
  sostener; hoy no hace falta.
- **Leases, reclamos y `SKIP LOCKED`** siguen en Postgres: son el estado de las colas, no su aviso.
- **Advisory locks** (`TenantChangeLock`, `PosSaleIdLock`, `AutomationsConversationLock`, `CustomerReferenceLock`,
  los de las unidades de trabajo y el de arranque de D-2 del spec de réplicas) siguen en Postgres: un candado de
  transacción sólo tiene sentido en la transacción.
- **Las cachés `IMemoryCache` de 60 s** (`WebhookRouting.cs:12`, `AutomationsModuleStatus.cs:14`) se quedan locales:
  el retraso de un minuto ya está aceptado con un pod y no empeora con dos.
- **`ExternalApiCallLogChannel`** (`src/Bootstrapper/ApiKeys/`) es un canal en memoria hacia un escritor del mismo
  pod: no es estado compartido.
- **Dividir los límites por pod o moverlos al ingress** (opciones 1 y 2 de DP-1): D-3 los hace globales de verdad.

## Pruebas (TDD)

RED antes que GREEN, con evidencia literal de ambos. Las de integración levantan un Valkey de Testcontainers por clase;
las de respaldo paran el contenedor (`StopAsync`) o apuntan a un puerto cerrado y miden el comportamiento, no el log.
Las unitarias de los adaptadores corren contra un `IConnectionMultiplexer` real del contenedor, no contra un doble: lo
que se prueba es el script y la clave. Recuerda `$env:Seed__Enabled = "false"` en esta máquina.

- **P-1, arquitectura (RED antes del paquete).** `EphemeralStoreLayerTests` en `tests/ArchitectureTests`: ningún
  ensamblado `Modules.*.Application`, `Modules.*.Infrastructure` ni `BuildingBlocks.*` referencia
  `StackExchange.Redis`; sólo `Bootstrapper`. Y en cada `<Modulo>LayerTests`, el prefijo `StackExchange.Redis` entra a
  `ApplicationDoesNotReferencePersistenceLibraries` junto a EF y Npgsql. Es RED sólo si alguien lo referencia mal; se
  escribe primero igual, para que exista antes de que haya tentación.
- **P-2, D-1.** `ValkeyOptionsValidatorTests`: en `Production` sin cadena falla; con `#{VALKEY_CONNECTION_STRING}#`
  falla; fuera de `Production` sin cadena pasa. `ConfigurationExampleTests` en verde con la sección nueva. Y por
  `QepApiFactory`: el host arranca con `Valkey:Enabled = true` y la cadena apuntando a un puerto cerrado
  (`TheHostStartsWithoutValkey`).
- **P-3, D-2.** `ValkeyPresenceTests`: `TwoProcessesSeeEachOthersMembers` (dos instancias del publicador contra el mismo
  contenedor, un tenant, un miembro en cada una → el lector devuelve dos);
  `AMemberWithoutHeartbeatDisappearsAfterNinetySeconds` (reloj controlado);
  `DisconnectingTheLastLocalConnectionRemovesTheMember`; respaldo: `WithValkeyDownTheReaderReturnsTheLocalRegistry`.
  `PlatformUsageApiTests` sigue en verde con el área apagada.
- **P-4, D-3.** `ValkeyRateLimiterTests`: `TwoLimitersOnTheSameKeyShareTheWindow` (dos instancias, `PermitLimit` 10:
  5 y 5 pasan, el 11.º no); `TheWindowResetsAtTheBoundary`; `RetryAfterPointsToTheEndOfTheWindow`; respaldo:
  `WithValkeyDownTheLocalLimiterStillRejects` (el 11.º en el mismo pod se niega, nunca se permite). Y en
  `ApiAccess.IntegrationTests`, el 429 por clave con dos hosts contra el mismo Valkey.
- **P-5, D-4.** `CachedSessionTests` (Identity.IntegrationTests): `TheSecondRequestDoesNotReadTheDatabase` (contar
  comandos con un interceptor de EF o con el log de `Database.Command`); `TouchStillHappensAfterFiveMinutes`;
  **una prueba por camino de revocación** que lee la entrada antes y comprueba que no existe después: logout,
  `PasswordReset`, `SessionRevocationWorker.TickAsync`, `OrphanUserCleanupWorker`; en Buyers:
  `BuyerPasswordRecovery`, `ChangeBuyerPassword`, `DeleteBuyerAccount`. Respaldo:
  `WithValkeyDownASessionIsValidatedFromPostgres` y, la que mide el riesgo, `ARevokedSessionIsNotServedWhileValkeyIsDown`
  (entrada sembrada, contenedor parado, la sesión revocada en Postgres → 401).
- **P-6, D-5.** `CachedAuthorizationTests`: `TheSecondResolveUsesTheCache`; **una prueba por camino que sube la
  versión** (los tres handlers de roles, los cuatro de membresía, la activación de invitación, `ChangeTenantModules`,
  `ChangeTenantStatus`) con la forma «resolver, cambiar, resolver de nuevo → el permiso nuevo o negado aparece sin
  esperar el TTL»; `AnEntryBuiltWithAnOlderTenantVersionIsDiscarded`; respaldo:
  `WithValkeyDownPermissionsComeFromPostgres`. Las pruebas de API de Authorization y de `MembershipEndpoints` siguen
  en verde con el área apagada.
- **P-7, D-6.** `ValkeySendBudgetTests`, calco de las pruebas actuales del cubo (`CampaignDispatchTests` y las unitarias
  de `ConnectionSendBudget`) contra el hash: concesión, relleno acotado por la pausa, mitad del ritmo en 130429, una
  sola bajada por pausa, subida de 1 mps cada 30 s. Y `TheTwoImplementationsAgree`: la misma secuencia de `now` y
  pedidos contra Postgres y contra Valkey da los mismos `SendGrant`. Respaldo: `WithValkeyDownTheBudgetUsesPostgres`.
- **P-8, D-7.** `QueueDoorbellTests`: `ARingWakesAWaitingWorkerBeforeTheSafetyNet` (espera de 30 s, timbre a los
  100 ms → vuelve en < 1 s); `WithoutARingTheWaitReturnsAtTheSafetyNet`; `WithValkeyDownWaitIsADelay`. Por cola, una
  prueba de integración que inserte por el camino real y mida que el worker reclamó sin esperar el intervalo
  (`AutomationInboxTests`, `WebhookDeliveryWorkerTests`, …). Para los dos arreglos sin Valkey:
  `NotificationsDeliveryWorkerDrainsTheTenDefinitionsInOneTick` y `OnlyTheLeaderSamplesTheQueues` (dos
  `QueueSamplerWorker` contra el mismo Valkey, una sola lectura por tick).
- **P-9, D-8.** `ApiKeyTouchGateTests`: `TheSecondRequestInAMinuteDoesNotUpdate` (contar `UPDATE`);
  `AfterSixtySecondsItUpdatesAgain`; respaldo: `WithValkeyDownTheUpdateRunsAsToday`.
- **Regresión general:** la suite de cada módulo tocado con `Valkey:Enabled = false` tiene que quedar idéntica a hoy. Es
  la prueba de que el interruptor apagado es «como antes», y corre una vez por bloque, al final (regla de presupuesto de
  pruebas de `CLAUDE.md`).

## Despliegue

En este orden, cada paso desplegado y verificado antes del siguiente:

1. **Owner, en `migracion-k8s`, antes de arrancar la implementación** (decisión del owner, 2026-11-13): Valkey
   desplegado (DP-V1, DP-V2, DP-V4 decididas) y la variable `VALKEY_CONNECTION_STRING` (o el nombre que fije) en el
   grupo `Backend-prod`. El plan de implementación no se escribe hasta que este paso esté hecho y la «Verificación»
   de abajo tenga namespace y pod reales: el desarrollo local corre contra `compose.yaml` y las pruebas contra
   Testcontainers, pero el primer despliegue del backend (paso 2) ya encuentra Valkey en el clúster.
2. **Backend, con todas las áreas apagadas:** D-1 completo (paquete, locks, cliente, opciones, validador, gauge, log,
   `compose.yaml`, `appsettings.example.json`, la entrada en `prod-secret.yaml`), más los dos arreglos sin Valkey de
   D-7. Se verifica que el pod arranca, que el log dice `ConnectionStrings:Valkey` y conectado, y que
   `qep_valkey_connected` vale 1.
3. **Prender las áreas de a una, en este orden, cada una con su verificación:** `Presence` (D-2), `RateLimiting` (D-3),
   `Sessions` (D-4), `Permissions` (D-5), `Doorbells` (D-7), `ApiKeyTouch` (D-8). `SendBudget` (D-6) no se prende: el cubo de envío se queda en Postgres (`messaging.connection_send_budgets`) por decisión del owner del 2026-11-13 y el área no tiene adaptador. Los flags van en
   `prod-configMap.yaml` (`Valkey__Enabled: "true"`, `Valkey__Areas__Presence: "true"`, etc.), así que cada paso es un
   commit de un manifiesto. Entre paso y paso, un día de logs sin fallbacks inesperados.
4. **Recién entonces el paso 3 del spec de réplicas** (`replicas: 2` + PDB), con D-7 y DP-1 de ese spec cerradas por
   este.

Nada de esto es una migración de datos ni trae una migración de EF: la fila del cubo (D-6) ya existe.

## Verificación

En PowerShell. `kubectl` siempre con `--context contabo-prod`. **Nunca `get secret -o yaml` ni `-o json`**: imprimen los
valores. Para saber si la clave existe, `describe` muestra el nombre y los bytes, no el valor.

Después del paso 2:

```powershell
kubectl --context contabo-prod -n prod-qep-backend describe secret qep-backend-secret | Select-String -Pattern "ConnectionStrings__Valkey"
kubectl --context contabo-prod -n prod-qep-backend get pods -o wide
kubectl --context contabo-prod -n prod-qep-backend logs <pod> --since=10m | Select-String -Pattern "Valkey"
```

Lo que se espera en el log: una línea de arranque con `ConnectionStrings:Valkey` y `connected`; ninguna con `fallback`.
Las líneas exactas las fija el builder; el criterio es que nombren la clave y el área y nunca el valor ni el host.

Estado del servidor, sin exponer nada. `INFO` no imprime credenciales. El usuario `default` está deshabilitado, así que
`valkey-cli` necesita un usuario del ACL: la contraseña se lee de una variable de entorno **dentro** del pod, nunca se
escribe en el comando ni se imprime. Hoy el contenedor `valkey` sólo trae `PROBE_PASSWORD`; el nombre del usuario y si
puede correr `INFO` lo dice el `users.acl` de `migracion-k8s` (no leído aquí: contiene credenciales). Se completa al
crear el usuario del backend (DP-V4):

```powershell
kubectl --context contabo-prod -n valkey exec deploy/valkey -c valkey -- sh -c 'valkey-cli --user <usuario> --pass "$PROBE_PASSWORD" --no-auth-warning INFO clients'
kubectl --context contabo-prod -n valkey exec deploy/valkey -c valkey -- sh -c 'valkey-cli --user <usuario> --pass "$PROBE_PASSWORD" --no-auth-warning INFO keyspace'
kubectl --context contabo-prod -n valkey exec deploy/valkey -c valkey -- sh -c 'valkey-cli --user <usuario> --pass "$PROBE_PASSWORD" --no-auth-warning INFO memory' | Select-String -Pattern "used_memory_human|maxmemory_human|maxmemory_policy"
```

`connected_clients` tiene que ser al menos el número de pods del backend. Después de prender cada área, `INFO keyspace`
muestra claves con su TTL medio, y por área:

- `Presence`: con una sesión abierta en Mensajería, `valkey-cli ZRANGE qep:presence:<tenantId> 0 -1 WITHSCORES` lista
  el miembro; la consola del operador lo muestra en «Conectados» desde cualquier pod.
- `RateLimiting`: once `POST` a una ruta `Credentials` desde la misma IP en un minuto, repartidos entre los dos pods
  (cuando los haya), dan 429 en el once. Antes del paso 4, con un pod, vale verificar que el 429 llega y que
  `Retry-After` apunta al cierre de la ventana.
- `Sessions`: cerrar sesión y repetir un request con la cookie vieja da 401 al instante (es la prueba de que el `DEL`
  corre en producción). `qep_valkey_fallbacks_total{area="sessions"}` no sube en condiciones normales.
- `Permissions`: suspender un miembro y, con su sesión abierta, pedir un endpoint del tenant: 403 en el request
  siguiente, no en 60 s.
- `Doorbells`: el gauge de edad de la cola `automation` del dashboard tiene que bajar, y el pod deja de mostrar un
  reclamo por segundo si se sube `Microsoft.EntityFrameworkCore.Database.Command` a `Information` un rato.

Por último, qué áreas están prendidas (el ConfigMap no lleva secretos):

```powershell
kubectl --context contabo-prod -n prod-qep-backend get configmap qep-backend-config -o jsonpath='{.data}' | Select-String -Pattern "Valkey__"
```

## Rollback

Por área: el flag a `false` en `prod-configMap.yaml` y redeploy. No hay datos que migrar ni limpiar: todas las claves
tienen TTL y mueren solas; la fila del cubo (D-6) sigue vigente porque nunca dejó de serlo. Apagar `Sessions` o
`Permissions` vuelve a leer Postgres en el request siguiente.

Para retirar Valkey del todo: `Valkey:Enabled = false` deja el cliente sin conectar (el validador en `Production` sigue
pidiendo la cadena mientras el código exista); los dos arreglos sin Valkey de D-7 se quedan.

Revertir el paso 2 es revertir el commit: el paquete y los locks vuelven atrás juntos.

## Lo que queda al builder

- La forma exacta de `ValkeyGateway` (timeout, contador de fallbacks, carga de scripts, prefijo), y los nombres de los
  adaptadores nulos.
- Las unidades de trabajo que escriben outbox y tienen que timbrar `outbox` (D-7), enumeradas con `grep` el día del
  cambio, con fecha, como el inventario D-6 del spec de réplicas.
- Los `EventId` y el texto de los logs, con la regla de no imprimir ni la cadena ni el host.
- Cómo se cuenta «no fue a Postgres» en P-5, P-6 y P-9 (interceptor de EF o nivel de log del comando).
- Los scripts Lua de D-3 y D-6, con la prueba de paridad `TheTwoImplementationsAgree` como contrato.
- El reparto en slices: D-1 + arreglos sin Valkey; luego D-2 y D-3 juntas (son las que destraban el spec de réplicas);
  luego D-4 y D-5; luego D-6, D-7 y D-8. Cada slice con su revisión por bloque (regla de `CLAUDE.md`).
- Actualizar el gotcha de `CLAUDE.md` que dice «74 `packages.lock.json`» y los dos comentarios de «no hay Redis».
