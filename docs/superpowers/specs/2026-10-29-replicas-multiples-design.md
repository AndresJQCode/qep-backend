# Réplicas múltiples de la API — diseño

Fecha: 2026-10-29, revisado 2026-11-13 contra `develop` `2ab5866c`. Estado: borrador para aprobación del owner. Reemplaza el plan
`docs/superpowers/plans/2026-09-24-replicas-multiples.md`, borrado en `8662d50c`: de sus seis tareas, la readiness con
base (tarea 1) y la IP real del cliente (tarea 2) ya están hechas, y dos de los cuatro workers que señalaba en la tarea 4
(`SessionRevocationWorker`, `OrphanUserCleanupWorker`) ya reclaman con `ClaimedOutboxConsumer`.

## Por qué

`k8s/prod-deployment.yaml` corre con `replicas: 1`. Un solo pod significa que cada despliegue, reinicio o caída del pod
deja a todos los tenants esperando el arranque, y que un pico de carga no tiene a dónde repartirse. La meta es
`replicas: 2` sin que la app se comporte distinto a como lo hace con una instancia.

La revisión del 2026-10-29 encontró tres cosas que hoy rompen o arriesgan eso, más dos reglas que hay que dejar
escritas. Todo lo que este documento afirma está verificado en el código salvo donde dice lo contrario.

## Qué se construye

### 1. El hub de Mensajería sin `negotiate` entre pods (D-1)

**El problema.** El cliente arma la conexión con `withUrl(url, { withCredentials, headers })`
(`qep-frontend/src/features/messaging/services/messaging-hub.ts:80-84`), sin `skipNegotiation` ni `transport`.
**Al 2026-11-13 sigue así** en `develop` y en `origin/develop` del frontend (el checkout local estaba en
`feature/shopify-token-expired-text`, verificado con `git show develop:…` y `origin/develop:…`): D-1 no está hecha y es
**prerrequisito duro del paso 3 de «Despliegue»**, no un seguimiento. Con dos pods y este cliente, el tiempo real falla a
medias desde el primer minuto. Así,
SignalR hace dos requests distintos: `POST …/negotiate`, que crea la conexión **en la memoria del pod que lo atiende**,
y después el `GET` del WebSocket con el `id` que devolvió el primero. `k8s/prod-ingress.yaml` no declara afinidad, así
que con dos pods el segundo request cae en el otro la mitad de las veces y ese pod no conoce el `id`. El síntoma es un
canal en tiempo real que a veces conecta y a veces no, sin error en el servidor que lo explique.

El reparto de eventos **ya funciona entre pods** y no se toca: `PgMessagingNotifier.cs:24` publica con `pg_notify`,
cada pod tiene su propia conexión en `LISTEN` (`MessagingEventsListener.cs:77-84`, `Pooling = false`) y
`MessagingHubEventSink.cs:23` entrega a los grupos de las conexiones de ese pod. Lo único que se rompe es el arranque
de la conexión.

**Decisión (D-1): el cliente salta el `negotiate` y usa sólo WebSockets** (`skipNegotiation: true`,
`transport: HttpTransportType.WebSockets`). Es el único caso en que la documentación de SignalR dice que no hacen falta
sesiones pegajosas: el `GET` del WebSocket es un solo request y la conexión vive en el pod que lo atendió.

Lo que hacía temer que esto no sirviera —que el navegador no puede poner headers en un WebSocket— no aplica:

- **CSRF.** El upgrade es un `GET`, y `RequireCsrfHeaderMiddleware` deja pasar los métodos seguros sin
  `X-Qep-Client` (`RequireCsrfHeaderMiddleware.cs:41-42, 58`). El header sólo lo necesitaba el `POST` del `negotiate`,
  que deja de existir.
- **Tenant.** Ya viaja en el query `tenantId` justamente por esto: `TenantRequestResolver.cs:33` lo acepta bajo
  `/api/v1/hubs/` (`MessagingRealtimeExtensions.cs:10-12`).
- **Sesión.** La cookie es `SameSite = Lax` (`SessionCookieWriter.cs:28`) y la SPA y la API son el mismo sitio, así que
  el navegador la manda en el handshake. `MessagingHub.OnConnectedAsync` sigue revalidando módulo y membresía.

**Lo que se pierde:** el respaldo a Server-Sent Events y long polling. Un cliente detrás de un proxy que bloquee
WebSockets queda sin tiempo real (la bandeja sigue funcionando con las consultas normales). Hoy el cliente ya intenta
WebSockets primero, así que en la práctica el tráfico real ya va por ahí.

**Alternativa descartada: afinidad por cookie en el ingress.** Un segundo `Ingress` sólo para `/api/v1/hubs/` con
`nginx.ingress.kubernetes.io/affinity: cookie` no toca el frontend y conserva los respaldos, pero agrega un manifiesto
al pipeline, depende de cómo se comporte el controlador del clúster (no verificable desde el repo) y la cookie de
afinidad apunta a un pod que el rollout puede haber borrado. Queda como plan B si la prueba de D-1 falla.

**Riesgo previo, no lo crea D-1:** ASP.NET Core acepta un WebSocket sin `negotiate` aunque el cliente no lo pida, así
que el handshake sin header ya es alcanzable hoy. El hub sólo escucha (no tiene métodos cliente→hub) y lo que entrega
son ids, pero una app hermana bajo `*.qcode.co` podría abrirlo con la cookie del usuario. Seguimiento aparte: validar
el `Origin` del handshake contra `Cors:AllowedOrigins`. *No verificado contra el servidor:* la prueba de D-1 lo deja
en evidencia.

**Reparto:** el cambio del cliente es un slice del repo `qep-frontend`, con su propio ID y su propio developer. Del
lado del backend sólo hay una prueba que amarra el contrato (ver «Pruebas»).

### 2. Un solo pod a la vez en migraciones y semilla (D-2)

**El problema.** Antes de `app.RunAsync()` (`Program.cs:431`), cada pod corre las 24 inicializaciones de base
(`Program.cs:331-424`, cada una un `MigrateAsync`; ServiceContracts, Loans, Store, Buyers y ApiAccess entraron después del
2026-10-29) y después `RunQepSeedAsync` (`Program.cs:429`). En producción `Seed__Enabled` es `"true"`
(`k8s/prod-configMap.yaml:144`). Sin bloqueo entre pods:

- **La semilla** consulta y después inserta (`IdentitySeeder.cs:30-39`, `CatalogSeeder.cs:40-48`). Dos pods sobre
  datos que aún no existen chocan contra un índice único y el que pierde tumba su arranque. Sólo el paso del tenant
  operador atrapa esa carrera (`QepSeedRunner.cs:132-147`); el usuario, el tenant, la numeración, el layout, el
  catálogo y las empresas no.
- **Geography** reconcilia departamentos y ciudades **en cada arranque**, no sólo la primera vez
  (`GeographyDatabaseInitializer.cs:16-19`, `GeographySeeder.cs:34-41`). El día que llegue un DIVIPOLA con ciudades
  nuevas, dos pods que arrancan juntos insertan las mismas y uno choca con el índice único de `DivipolaCode`
  (`GeographyDbContext.cs:112`).
- **Las migraciones** probablemente ya están serializadas por módulo, y eso no alcanza: cada módulo tiene su propia
  tabla de historial (`MigrationsHistoryTable`, una por contexto) y EF Core 10.0.13 con
  `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (`Directory.Packages.props:25, 40`) traen el candado de migración
  (`NpgsqlMigrationDatabaseLock`, `LOCK TABLE` sobre la tabla de historial). Ese candado protege **sólo la tabla de
  historial de cada módulo**: no la semilla, no la reconciliación de Geography, y no el orden entre módulos (dos pods
  pueden estar migrando módulos distintos a la vez mientras uno ya siembra sobre tablas que el otro todavía crea). *No
  verificado en el código fuente del proveedor*, sólo en las cadenas del binario: no se cuenta con eso.

**Cuándo arrancan dos pods a la vez.** Con `maxSurge: 1` y `maxUnavailable: 0`, un rollout crea los pods nuevos de a
uno, así que el deploy normal no produce la carrera. La producen un reinicio del nodo (con un solo nodo, los dos pods
vuelven juntos), los dos pods cayendo a la vez, o un `rollout restart` con el anterior todavía arrancando. Es raro, pero
el que pierde entra en reinicio y el que más arriesga es justo el día del cambio de datos.

**Lo que la revisión del 2026-11-13 corrigió.** La versión anterior de esta decisión pedía un `pg_advisory_lock` de
sesión sobre una conexión armada desde `ConnectionStrings:QepDatabase`. En producción esa cadena **pasa por PgBouncer en
modo `transaction`** (README § Configuración, línea 113; y el gotcha de `MessagingEventsListener` en `CLAUDE.md`: el
`LISTEN` por el pooler «no sobrevive y no da error»). Un lock de sesión por el pooler se toma en un backend y el
`pg_advisory_unlock` puede llegar a **otro**: o el candado queda tomado para siempre (el segundo pod nunca arranca) o
dos pods lo «obtienen» en backends distintos y no serializan nada. Las dos fallas son silenciosas.

**Decisión (D-2): un `pg_advisory_xact_lock` dentro de una transacción explícita que dura todo el bloque
migraciones + semilla, sobre una conexión directa a Postgres resuelta igual que la del listener.** El primer pod lo
toma, migra y siembra; el segundo espera en el `SELECT pg_advisory_xact_lock(...)` y, cuando lo obtiene, encuentra todo
hecho y pasa rápido.

- **Conexión del lock.** Se resuelve con la misma regla de `ListenerConnectionString.Resolve`
  (`Modules.Messaging.Infrastructure/Realtime/ListenerConnectionString.cs:20-31`): `ConnectionStrings:QepDatabaseListener`
  si existe, no está vacía y no es un token `#{…}#` sin resolver del pipeline; si no, `QepDatabase`. Esa clase es
  `internal` de `Modules.Messaging.Infrastructure`, así que Bootstrapper lleva **su propia copia** (`StartupLockConnectionString`
  o similar), amarrada por una prueba de paridad en `Bootstrapper.UnitTests` que corre los mismos casos contra las dos
  (presente / vacía / `#{…}#` / ausente), como `ShopifyWebhookTopicsParityTests`. Se abre con `Pooling = false`, como el
  listener (`MessagingEventsListener.cs:78`): la conexión es de un solo uso y se cierra al terminar el bloque.
- **Por qué `xact` y no sesión, aun con conexión directa.** El candado de transacción se libera solo al `COMMIT`,
  al `ROLLBACK` o al morir la conexión: no hay `unlock` que pueda llegar a otro backend ni quedar sin ejecutarse. Y es la
  única variante que **también** se comporta bien si la clave directa falta en un ambiente y el lock cae al pooler: en
  modo `transaction` PgBouncer sostiene el mismo backend mientras la transacción está abierta, así que el candado sigue
  serializando (el `LISTEN` no tiene esa suerte porque vive fuera de una transacción). La transacción envuelve sólo el
  `SELECT` del lock y la espera; las migraciones y la semilla corren en **sus** conexiones y transacciones del pool, como
  hoy. El lock no envuelve su trabajo en una transacción de 24 módulos: es un semáforo, no una unidad de trabajo.
- Si el pod muere, PostgreSQL cierra la sesión y suelta el candado; si una migración o la semilla lanzan, el `finally`
  hace `ROLLBACK` (o cierra la conexión) y el otro pod sigue.
- La espera respeta `app.Lifetime.ApplicationStopping`: un pod que Kubernetes mata mientras espera cancela el `SELECT`
  y no queda colgado.
- **Clave.** Forma de dos enteros, `pg_advisory_xact_lock(20261113, 1)`: espacio de nombres nuevo `20261113`
  (constante propia `StartupLock.Namespace`, documentada como las otras) y una segunda clave constante, porque es un
  solo candado global. Los espacios de dos claves que ya existen son `TenantChangeLock` = 20261008
  (`Modules.Tenancy.Infrastructure/Persistence/TenantChangeLock.cs:14`), `AutomationsConversationLock` = 20261022
  (`Modules.Automations.Infrastructure/Persistence/AutomationsConversationLock.cs:14`) y `ShopifyTokenLock` = 20261112
  (`Modules.Integrations.Infrastructure/Persistence/ShopifyTokenLock.cs:12`). Los de una sola clave viven en otro espacio
  de PostgreSQL y no chocan con la forma de dos: `UserLifecycleLockKey` (`hashtext` de un correo,
  `TenancyUnitOfWork.cs:45`, `OrphanUserCleanupWorker.cs:191`), `CustomerReferenceLock` (`hashtextextended`,
  `BuildingBlocks.Infrastructure/CustomerReferenceLock.cs:29-35`), el sinónimo de ciudad de Shopify y Geography
  (`hashtextextended`, `ShopifyUnitOfWork.cs:65`, `GeographyUnitOfWork.cs:53`) y `PosSaleIdLock` (SHA-256 truncado a 64
  bits, `PosSaleIdLock.cs:25`). Un espacio nuevo que se agregue después se anota en el comentario de cada constante,
  como ya hacen `AutomationsConversationLock` y `ShopifyTokenLock`.
- Va en Bootstrapper (ya referencia las Infrastructure de los módulos, como `QepSeedRunner`), y `Program.cs` envuelve
  las líneas 331-429 en una sola llamada. Las inicializaciones no cambian ni de orden ni de contenido.
- Log de cuánto esperó y cuánto tardó el bloque, y **el nombre de la clave de conexión que usó** (`QepDatabaseListener` o
  `QepDatabase`, nunca el valor), para medir el `startupProbe` (§3) con datos y ver en producción si el lock va directo.

**Alternativa descartada: `pg_advisory_lock` de sesión sobre la conexión directa.** Funciona si y sólo si la clave
`QepDatabaseListener` está resuelta en todos los ambientes; con la caída a `QepDatabase` el candado pasa por el pooler y
falla en silencio como se describe arriba. Exige además `pg_advisory_unlock` explícito y una conexión viva durante todo
el bloque. El `xact` cubre los dos casos sin costo adicional.

**Alternativa descartada: un `Job` previo al deploy** que migre y siembre, con la API sin migrar al arrancar. Es el
patrón más limpio a largo plazo, pero exige un modo de la imagen que migre y salga, que la plantilla
`k8s/deploy.yml@k8sTemplates` aplique y espere un `Job` antes del Deployment (no se ve desde este repo), y borrarlo
antes de cada apply porque un `Job` es inmutable. D-2 son unas decenas de líneas y no cambia el pipeline. Si un día las
migraciones pasan del presupuesto del `startupProbe`, se revisa.

### 3. Presupuesto del `startupProbe` (D-3)

Hoy: `periodSeconds: 5`, `failureThreshold: 12` → **60 s** (`prod-deployment.yaml:48-53`). Kestrel empieza a escuchar
recién en `app.RunAsync()`, así que todo el bloque de §2 corre con `/health/live` sin responder. Con D-2, el segundo pod
suma a su arranque la espera por el primero: en el peor caso, el doble.

Si un pod no alcanza a arrancar en 60 s, el kubelet lo mata, vuelve a empezar y —con `maxUnavailable: 0`— el rollout
no avanza hasta `progressDeadlineSeconds` (no está en el manifiesto: vale el default de Kubernetes, 600 s).

**Decisión (D-3): `failureThreshold: 36` con `periodSeconds: 5` → 180 s.** El `startupProbe` sólo cuenta hasta el
primer éxito, así que un presupuesto más largo no demora nada cuando todo anda bien; sólo tarda más en matar un arranque
colgado. 180 s cubre dos arranques seguidos con margen. El número se ajusta con el log de D-2: si el bloque tarda más de
60 s en producción, 180 s ya no alcanza para dos y hay que volver a la alternativa del `Job`.

El comentario de `ExportLoadSeedWorker.cs` que cita «60 s como máximo (12 intentos cada 5 s)» se actualiza en el mismo
cambio.

### 4. Subir a dos réplicas y activar el PDB (D-4)

En un solo commit:

- `k8s/prod-deployment.yaml`: `replicas: 2`.
- `azure-pipelines.yml`: `pdb` en la lista de `manifests`, **después de `deployment`** (hoy la lista, `:49-55`,
  termina en `deployment`, `ingress`; sigue sin `pdb` al 2026-11-13). Lo pide el encabezado de `k8s/prod-pdb.yaml`: con
  `replicas: 1` el PDB (`minAvailable: 1`) bloquearía el drain del nodo, por eso nunca va sin las dos réplicas.
- README § health (línea 1353 y siguientes): deja de decir que el PDB «no se despliega».
- **Seguimiento del mismo paso:** el manifiesto no tiene `lifecycle.preStop` ni `terminationGracePeriodSeconds`. Con dos
  pods y `maxUnavailable: 0` el rollout empieza a sacar al viejo del `Service` mientras el ingress todavía le manda
  requests unos segundos; un `preStop: sleep 10` lo cubre. No bloquea D-4; va en el mismo commit si cabe.

Capacidad: con `requests.cpu: 500m` y `memory: 512Mi` por pod, dos pods más el de surge reservan **1,5 CPU y
1,5 GiB** durante el rollout. Se comprueba en el nodo antes de aplicar (ver «Verificación»).

### 5. Regla de migraciones con pods viejos y nuevos (D-5)

Durante un rollout, el pod nuevo migra mientras los viejos siguen atendiendo con el código anterior. Eso ya pasa hoy con
una réplica (`maxSurge: 1`); con dos, son dos pods viejos contra el esquema nuevo. La regla que se escribe en el README:

- **Toda migración es expand/contract.** Una versión agrega (columna nullable o con default, tabla, índice); el código
  viejo la ignora. Quitar, renombrar o volver `NOT NULL` algo que el código viejo escribe va en **otra** versión,
  cuando ningún pod viejo queda vivo.
- **Una migración que rompe al código viejo se despliega con corte**: estrategia `Recreate` o el backend a cero
  réplicas, como ya se hizo con las variantes (README § Variantes, «Despliegue», línea 1575). El PDB no lo impide: sólo
  frena desalojos, no un `scale` ni un `Recreate`.
- **El handoff de cada spec dice si su migración es aditiva.** Si no lo dice, se asume que no.

### 6. Inventario de workers verificado al implementar, no al diseñar (D-6)

**El problema.** Este documento clasificó los hosted services que existían el 2026-10-29. Hay otras sesiones
construyendo módulos en paralelo (Loans, Buyers, Store, ServiceContracts, ApiAccess entraron después), así que la
cantidad y la lista de workers **cambian entre el diseño y la implementación**, y van a seguir cambiando después. Un
conteo o una tabla fija en este spec queda vieja sin que nada avise.

**Decisión (D-6): la lista de este spec es evidencia del día que se escribió, no la fuente de verdad.** Al implementar:

1. **Reenumerar** todos los `AddHostedService` de `src/` (y cualquier `IHostedService`/`BackgroundService` registrado
   por otra vía) en la rama en la que se implementa, el mismo día. El número se escribe en el plan con la fecha y el
   commit; no se copia de aquí.
2. **Clasificar cada uno** en una de tres categorías, con la evidencia (archivo y mecanismo):
   - **Seguro con N pods:** reclama trabajo con `FOR UPDATE SKIP LOCKED`, lease (`claimed_until`/`locked_until`),
     advisory lock, o es idempotente por diseño (un `DELETE` por corte, un `UPSERT`).
   - **Ruidoso:** la segunda instancia choca con una clave única y sólo deja log o un tick perdido, sin dato malo.
     Se anota como seguimiento.
   - **Inseguro:** la doble ejecución produce un efecto externo duplicado (correo, WhatsApp, cobro, llamada a una
     API externa, rotación de token) o una fila duplicada. **Bloquea** subir réplicas hasta tener reclamo o lock.
3. **Prueba de arquitectura que fija el inventario:** en `tests/ArchitectureTests/` se agrega una prueba que enumera
   todos los hosted services registrados por el contenedor real y exige que cada tipo esté en una lista explícita
   de clasificación (una constante en el proyecto de pruebas: tipo → `Claimed | Idempotent | AdvisoryLock | Noisy`).
   Un worker nuevo que nadie clasificó pone la prueba en rojo con un mensaje que dice qué hacer. Así la regla
   sobrevive a las sesiones que vienen: quien agrega un worker tiene que decir cómo convive con otro pod, en el
   mismo commit.
4. **La misma verificación aplica al estado en memoria:** antes de subir réplicas se vuelve a buscar
   `IMemoryCache`, singletons con estado, `static` mutables, `ConcurrentDictionary`, `SemaphoreSlim` y `Channel<T>`
   usados entre requests. `MessagingPresence` (presencia de la bandeja, por proceso) ya es un bloqueador conocido que
   este spec no tenía el 2026-10-29: se resuelve en D-7.

**Criterio de salida del paso 3 de «Despliegue»:** cero workers en «Inseguro», la prueba de arquitectura en verde, y
el inventario del plan con fecha del mismo día del merge.

### 7. La presencia de la bandeja es por proceso (D-7)

**El problema.** `MessagingPresence` (`Modules.Messaging.Application/MessagingPresence.cs:20-35`) es un
`ConcurrentDictionary<connectionId, (tenantId, memberId)>` registrado como singleton
(`QepServiceCollectionExtensions.cs:1169`). Lo alimenta `MessagingHub` en `OnConnectedAsync` / `OnDisconnectedAsync`
(`Modules.Messaging.Api/Realtime/MessagingHub.cs:58, 63`) y lo lee `PlatformUsageSource` (`src/Bootstrapper/PlatformUsageSource.cs:42,
60-72`) para la columna «Conectados» de `GET …/operator/usage`. Con dos pods cada uno ve sólo a los que conectaron con él:
el reporte muestra la mitad de los asesores conectados, sin error. El comentario de la clase ya lo dice («sólo vale
mientras la API corra con una réplica»).

**Resuelto el 2026-11-13 por el spec de Valkey.** Las dos opciones que se evaluaron acá —(a) una tabla
`messaging.hub_presence` con latido cada 30 s, (b) retirar «Conectados» del DTO y del frontend— quedaron descartadas: el
owner aprobó ese día adoptar Valkey para **todo** el estado efímero compartido, y la presencia va por un sorted set por
tenant (`qep:presence:{tenantId}`, latido cada 30 s, lectura de los últimos 90 s), con el diccionario local como
respaldo. El detalle, las pruebas y el orden de despliegue están en
[`2026-11-13-valkey-estado-compartido-design.md`](2026-11-13-valkey-estado-compartido-design.md), D-2.

**DP-3: cerrada, decisión «Valkey»** (owner, 2026-11-13). Entra por el despliegue del spec de Valkey, no por el paso 1
de este (ver «Despliegue»).

## Decisiones pendientes del owner

### DECISIÓN-PENDIENTE DP-1: los limitadores de tasa son por pod

**Resuelta el 2026-11-13 por el spec de Valkey.** Ninguna de las tres opciones de abajo: los contadores de ventana fija
de las ocho políticas y del limitador por clave pasan a Valkey (`qep:rl:{política}:{partición}:{ventana}`, `INCR` +
`EXPIRE` atómicos), así que el límite es global de verdad y los números se quedan donde están, sin atarlos a `replicas`.
`Webhook` sigue por pod, a propósito. Si Valkey no responde, cada pod cae a su limitador de memoria —exactamente la
situación que describe la tabla— y nunca «permite». Detalle, pruebas y respaldo en
[`2026-11-13-valkey-estado-compartido-design.md`](2026-11-13-valkey-estado-compartido-design.md), D-3. El análisis
que sigue se conserva como registro de por qué hacía falta.

Al 2026-11-13 son **ocho políticas** en `AddRateLimiter` (`Program.cs:86-203`) más el limitador por clave de la API
externa, todos en la memoria del proceso. Con dos pods y reparto parejo:

| Política | Hoy | Con 2 pods | Efecto |
| --- | --- | --- | --- |
| `Public` (`:97-101`) | 120/min por IP | hasta 240 | Scraping del OpenAPI y del inicio de Shopify; bajo riesgo |
| `Credentials` (`:105-116`) | 10/min por IP y patrón de ruta | hasta 20 | **Seguridad:** el doble de intentos de contraseña |
| `Telemetry` (`:120-131`) | 600/min por IP | hasta 1200 | Métricas del cliente; bajo riesgo |
| `Webhook` (`:135-148`) | concurrencia 64, cola 256 por pod (`MessagingWebhookOptions.cs:11, 13`) | el doble en total | Es lo buscado: protege a cada pod |
| `External` (`:152-163`) | 600/min por IP (`ApiAccessOptions.PerIpPerMinute`) | hasta 1200 | Frena claves inventadas; bajo riesgo |
| `StorefrontRead` (`:167-176`) | 300/min por IP del visitante | hasta 600 | Lectura de la tienda; bajo riesgo |
| `StorefrontSubmit` (`:177-186`) | 5 cada 10 min por IP del visitante | hasta 10 | **Seguridad:** anti-spam de pedidos de la tienda pública |
| `StorefrontCredentials` (`:190-200`) | 10/min por IP y patrón de ruta | hasta 20 | **Seguridad:** el doble de intentos sobre cuentas de compradores |
| `ApiKeyRateLimiter` (singleton, `src/Bootstrapper/ApiKeys/ApiKeyRateLimiter.cs`; `QepServiceCollectionExtensions.cs:94`) | 300/min por clave (`ApiAccessOptions.PerKeyPerMinute`, `ApiAccessOptions.cs:18`) | hasta 600 | Contrato con los integradores: el límite anunciado deja de cumplirse |

Opciones para los cuatro marcados como seguridad o contrato (`Credentials`, `StorefrontSubmit`, `StorefrontCredentials`,
`ApiKeyRateLimiter`):

1. **Bajar el `PermitLimit` a la mitad por pod** (5, 2 o 3, 5 y 150), con un comentario que ate cada número a
   `replicas: 2`. El total vuelve a ~hoy. Contra: el reparto no es exacto (un atacante con suerte saca algo más, uno
   legítimo puede chocar antes), `StorefrontSubmit` no se divide en entero (2 por pod deja 4 en total, 3 deja 6), y los
   cuatro números quedan atados a la cantidad de réplicas. `PerKeyPerMinute` y `PerIpPerMinute` son opciones de
   configuración: el cambio va en `prod-configMap.yaml`, no en código.
2. **Límite en el ingress** (`nginx.ingress.kubernetes.io/limit-rpm`). Global de verdad, pero se aplica por objeto
   `Ingress`: hay que separar las rutas de credenciales en uno propio, y en ingress-nginx el contador es por réplica del
   controlador. No verificado contra el clúster. No sirve para `ApiKeyRateLimiter`, que particiona por clave.
3. **Aceptarlo.** El doble de intentos por IP y por ruta, con las contraseñas hasheadas y la sesión opaca; el doble de
   pedidos por IP en la tienda; y 600/min por clave en la API externa, que hoy el 429 anuncia como 300.

Recomendación: 1 para los cuatro, con el comentario atado a `replicas`; el resto se deja como está. Lo decide el owner.

### DP-2 (cerrada, 2026-11-13): conexiones a PostgreSQL

Cada pod abre, además del pool de Npgsql, una conexión permanente sin pool para `LISTEN` (`MessagingEventsListener.cs:78`)
y, con D-2, una transitoria para el lock (las dos directas a Postgres, fuera del pooler). Los 52 `AddHostedService`
(2026-11-13) no tienen conexiones propias: toman del pool al sondear.

Medido el 2026-11-13 sin leer ningún valor secreto (de las cadenas sólo se listaron los **nombres** de sus claves):

| Dato | Valor | Fuente |
| --- | --- | --- |
| Cadenas `QepDatabase` y `QepDatabaseListener` | sólo `Host, Port, Database, Username, Password`: ningún parámetro de pool | `qep-backend-secret` |
| Pool de Npgsql por pod | 100 (default, sin `Maximum Pool Size`) | Npgsql |
| Servidor | CNPG `database/pg-platform`, 2 instancias, `max_connections=200`, compartido con otras apps | `Cluster` de CNPG |
| Pooler | `pg-platform-pooler-rw`, 2 instancias, modo `transaction`, `default_pool_size=25`, `reserve_pool_size=10`, `max_client_conn=1000` | `Pooler` de CNPG |

**Conclusión: pasar a dos réplicas no cambia las conexiones a Postgres que pasan por el pooler.** PgBouncer abre como
mucho `25 + 10` conexiones de servidor por par base-usuario **por instancia del pooler**, sin importar cuántos pods
del backend haya detrás. Lo que se duplica son las conexiones de cliente hacia PgBouncer (hasta 100 por pod, muy lejos
de `max_client_conn`) y las directas: dos `LISTEN` en régimen y dos locks transitorios al arrancar. No se fija
`Maximum Pool Size`. Si algún día aparecen esperas por conexión, el cuello de botella es el `default_pool_size` del
pooler (en `migracion-k8s`), no el pool de Npgsql. Queda sin medir el pico actual de `pg_stat_activity`; no bloquea,
porque el aporte de este cambio a `max_connections` son las dos o cuatro conexiones directas.

## Lo que ya funciona con varias réplicas

Reenumerado el 2026-11-13 sobre `develop` `2ab5866c`: **52 `AddHostedService`** en `src/`, ninguno en «Inseguro» según
el criterio de D-6. Por D-6, esta tabla es evidencia con fecha, no la fuente de verdad: se reenumera al implementar.

| Qué | Evidencia |
| --- | --- |
| Sesión | Cookie opaca buscada por hash en `identity.sessions` en cada request (`SessionCookieAuthenticationHandler.cs:12`, `ISessionRepository.FindByTokenHashAsync`) |
| Sin llaves por pod | No hay `AddDataProtection`, `AddAntiforgery` ni `AddSession` en `src/` |
| CSRF | Header `X-Qep-Client`, sin estado (`RequireCsrfHeaderMiddleware.cs:38-39`) |
| Outbox de Tenancy | `FOR UPDATE SKIP LOCKED` (`OutboxProcessor.cs:27`) |
| Exportaciones | `FOR UPDATE SKIP LOCKED` con lease (`ExportJobQueue.cs:46`) |
| Medios y campañas de Messaging | `SKIP LOCKED` (`MediaCopyWorker.cs`, `CampaignDispatchStore.cs`) |
| Webhooks de Meta | Cola con lease (`WebhookDeliveryWorker.cs:16, 30`) y dedup por `IX_webhook_deliveries_body_sha256` (`MessagingDbContext.cs:625`) |
| Webhooks de Shopify | Lease (`ShopifyWebhookDeliveryWorker.cs`) y dedup por `IX_webhook_deliveries_webhook_id` (`ShopifyDbContext.cs:26`) |
| Bot | Lease en `AutomationInboxWorker` y candado por conversación con `pg_try_advisory_xact_lock` |
| Consumidores de outbox | `ClaimedOutboxConsumer`: `SessionRevocationWorker`, `OrphanUserCleanupWorker`, `QuotationEventsProcessor` (Pipeline), `CitySynonymEventsProcessor` (Shopify), `CustomerMediaPurgeProcessor` (Messaging, `CustomerMediaPurgeProcessor.cs:34`) |
| Correos | El reclamo es de `OutboxDeliveryWorker` con lease de 2 min (`OutboxDeliveryWorker.cs:44`; `InvitationDeliveryWorker.cs:14` y los demás `*DeliveryWorker` de Notifications, incluidos los de compradores) |
| Recordatorios de contratos de servicio | `ServiceContractsReminderWorker`: reclamo por `(tenant, día)` con `INSERT … ON CONFLICT … DO UPDATE` y lease de 5 min (`ServiceContracts…/Reminders/TenantRunStore.cs:12, 24-27`); un envío por cliente y día por `IX_customer_messages_tenant_customer_day` (`CustomerMessageStore.cs`), `DailyDigestStore` y `IX_charges_service_due` (`ServiceContractsUnitOfWork.cs`) |
| Generación de cuotas de préstamos | `LoansGenerationWorker`: mismo reclamo con lease sobre `loans.tenant_runs` (`Loans…/Generation/TenantRunStore.cs:12, 24-26`) y `IX_installments_loan_period` (`LoansUnitOfWork.cs`) |
| Altas de compradores | `BuyerSignupRequestedWorker`: lease en `buyers.inbox_messages` (`BuyersInboxClaims.cs:25-31`) y `PK_processed_signups` (`BuyerSignupRequestedWorker.cs`) |
| Coexistencia e historial | `CoexistenceEventsWorker` (`ON CONFLICT (connection_id) DO NOTHING`, `CoexistenceEventsProcessor.cs:61`) y `HistorySyncRequestWorker` (`SKIP LOCKED` + `claimed_until` de 2 min, `HistorySyncRequestProcessor.cs:96-100`) |
| Mensajes programados | `ScheduledMessageWorker`: reclamo por fila con lease y recuperación de leases vencidos (`ScheduledMessageWorker.cs:12-18`, `ScheduledMessagePorts.cs`) |
| Shopify | `ShopifyCityCorrectionWorker` (lease creciente por intento, `ShopifyCityCorrectionWorker.cs:14, 33`), `ShopifyReconcileWorker` (lease por tienda vía `IShopifyReconcileLeases`, `ShopifyReconcileWorker.cs:15-18`), `ShopifyTokenRefreshWorker` (`pg_try_advisory_xact_lock(20261112, hashtext(id))`, `IntegrationsUnitOfWork.cs:33`) |
| Aprovisionadores por tenant | `PipelineProvisioner` y `OptionLibraryProvisioner`: `INSERT … ON CONFLICT (tenant_id) DO NOTHING` (`PipelineProvisioner.cs:30`, `OptionLibraryProvisioner.cs:44`) |
| Tiempo real | `pg_notify` + `LISTEN` por pod (§1), **si** la conexión del listener es directa (ver «Verificación», antes del paso 3) |
| IP del cliente | `UseForwardedHeaders` primero (`Program.cs:212`), redes confiables en `prod-configMap.yaml:42-43` |
| Readiness | `/health/ready` corre `SELECT 1` (`DatabaseReadinessCheck.cs:25`) |
| Workers vs. esquema | Los hosted services arrancan en `app.RunAsync()` (`Program.cs:431`), después de migrar y sembrar |
| Archivos | En R2; el Deployment no monta volúmenes |

## Fuera de alcance (seguimiento)

Inofensivos con dos pods: producen ruido en el log o trabajo repetido, no datos malos.

- **`AuditProjectionWorker`** (`AuditProjectionWorker.cs:51-85`), **`PaymentProofMoveProcessor`**
  (`PaymentProofMoveProcessor.cs:74-81, 172`), **`PaymentProofDetachProcessor`** (`PaymentProofDetachProcessor.cs:96`, mismo
  `PaymentProofMoveWorker`) y **`ProductImageReleaseProcessor`** (`ProductImageReleaseProcessor.cs:89-91`) leen pendientes
  por anti-join contra su inbox, sin reclamar. Los dos pods pueden tomar el mismo mensaje; efecto e inbox van en el mismo
  `SaveChanges`, así que el que pierde choca con la clave del inbox y se deshace entero. En Audit el resto del lote de ese
  tick también falla (las entidades quedan rastreadas en el contexto) y se reintenta en el siguiente. Arreglo: pasarlos a
  `ClaimedOutboxConsumer`.
- **Barridos periódicos en los dos pods:** `QuotationExpirationWorker` (cada `ExpirationSweepMinutes` = 60; `Version`
  es token de concurrencia, `QuotationsDbContext.cs:160`, así que el segundo choca y no duplica historial),
  `StagingCleanupWorker` (cada `StagingCleanupMinutes` = 60), `PaymentProofOrphanCleanupWorker` (cada 24 h),
  `WebhookPurgeWorker`, `BuyerHousekeepingWorker` (`BuyerHousekeepingWorker.cs:134-138`), `ShopifyInstallSweeper`
  (`ShopifyInstallSweeper.cs:86`), `ApiKeyRequestPurgeWorker` (`ApiKeyRequestPurgeWorker.cs:61`) y la purga de
  `ScheduledMessageWorker`: cada uno es un `DELETE` por corte, idempotente; el segundo pod borra cero filas. En las dos
  limpiezas de Storage, borrar en R2 una clave que ya no existe no falla; *no verificado* si la auditoría puede quedar
  dos veces. Arreglo: `pg_try_advisory_xact_lock` por pasada.
- **`InstagramTokenRefreshWorker`** corre al arrancar y después cada 24 h (`InstagramTokenRefreshWorker.cs:21, 54, 84`),
  **sin candado**: los dos pods pueden refrescar el mismo token a la vez. El que pierde falla por `Version` al guardar y
  reintenta en el siguiente tick; no hay doble envío ni dato malo, pero sí dos llamadas a Meta y un error en el log en
  cada arranque simultáneo. *No verificado* si Meta invalida el anterior al refrescar; si lo hiciera, gana el último
  que guarda y no se pierde nada. Arreglo: `pg_try_advisory_xact_lock` por conexión, como `ShopifyTokenRefreshWorker`.
- **`ConnectionSecretRekeyWorker`** y **`CustomerPhoneBackfillWorker`**: idempotentes por diseño; un lote que choca se
  salta y se cuenta (`ConnectionSecretRekeyWorker.cs:24-26`).
- **Correos dos veces, un residual que ya existe:** `OutboxDeliveryWorker` reclama con lease de 2 min
  (`OutboxDeliveryWorker.cs:22-24, 44`); si un envío tarda más que el lease, el otro consumidor lo retoma y el correo sale
  dos veces. Infobip no recibe clave de idempotencia, así que no se cierra; con un pod ya pasa, con dos es más probable
  porque el que retoma no es el que está enviando. Se vigila en el log, no bloquea.
- **Leases de 5 min de `ServiceContracts` y `Loans`** (`TenantRunStore.Lease` en los dos módulos): una corrida de tenant
  que pase de 5 min la retoma el otro pod y el trabajo se hace dos veces; no hay duplicados gracias a los índices únicos
  de la tabla de arriba, sólo trabajo repetido. Si el log muestra retomas, se sube el lease.
- **Métricas de colas por pod:** `QueueSamplerWorker` alimenta `QueueGauges` (`QueueGauges.cs:26-30`,
  `CreateObservableGauge`) y cada pod publica la suya con el mismo valor de base. El dashboard de Grafana (repo
  `migracion-k8s`) tiene que agregar con `max` o `avg`, nunca `sum`, o el backlog se ve el doble; las alertas de
  `QepQueueMetricsMissing` igual.
- **`ExternalApiCallLogChannel`** (`ExternalApiCallLogChannel.cs:21`, `Channel.CreateBounded` por pod): lo que no se
  alcanzó a escribir se pierde con un `SIGKILL`. Igual que hoy; con dos pods, dos colas.
- **Cachés en memoria de 60 s** (`WebhookRouting.cs:8`, `AutomationsModuleStatus.cs:14`): no se invalidan en proceso,
  así que el retraso de hasta un minuto ya existe con un pod. Con dos, cada uno tiene el suyo, con el mismo máximo.
- **`ExportLoadSeedWorker`:** apagado en producción (`Seed__ExportLoad__Quotations: "0"`, `prod-configMap.yaml:158`).
  Prendido en dos pods sembraría dos veces; regla: sólo con `replicas: 1`.
- **`preStop` y `terminationGracePeriodSeconds`** del manifiesto (§4).
- **`Origin` del handshake del hub** (§1, riesgo previo).

## Pruebas (TDD)

RED antes que GREEN, con evidencia literal. Integración contra PostgreSQL de Testcontainers, como las de semilla
existentes (`SeedStartupTests`, `CatalogSeedTests`). Recuerda `$env:Seed__Enabled = "false"` en esta máquina para las
que no siembran.

**Backend, D-2** — el lock se prueba de forma determinista, no confiando en ganar una carrera:

1. **RED** `TheStartupWorkWaitsWhileAnotherInstanceHoldsTheLock`: la prueba toma la clave desde su propia conexión,
   lanza el bloque de arranque y comprueba que no termina en un plazo corto y que no corrió nada (la tabla de historial
   de un módulo todavía no existe). Suelta la clave y el bloque termina. Sin el lock, termina enseguida: es la RED.
2. **RED** `TheLockIsReleasedWhenTheStartupWorkThrows`: el bloque lanza; después, `pg_try_advisory_xact_lock(20261113, 1)`
   desde otra conexión (en su propia transacción) devuelve `true`.
3. **RED** `CancellingWhileWaitingStopsWithoutTakingTheLock`: con la clave tomada por la prueba, cancelar el token
   termina en `OperationCanceledException`.
3b. **RED** `TheLockConnectionDoesNotGoThroughThePooler` (`Bootstrapper.UnitTests`, paridad): con una `IConfiguration` que
   trae `QepDatabaseListener` presente, vacía, `#{DATABASE_LISTENER_CONNECTION_STRING}#` o ausente, la copia de
   Bootstrapper devuelve la misma `(ConnectionString, KeyName)` que `ListenerConnectionString.Resolve` (expuesta a la
   prueba con `InternalsVisibleTo`, como ya hacen otras `internal` de Messaging). Sin la copia, no compila: es la RED.
4. **Regresión** `TwoConcurrentStartupsSeedOnce`: dos arranques concurrentes con semilla prendida sobre una base vacía
   terminan sin excepción y con un solo tenant `origen-botanico` y un solo usuario dueño. Puede pasar por suerte antes
   del arreglo, así que no sirve de RED; se queda como amarre.
5. `TheHostWaitsForTheStartupLock` (por `QepApiFactory`): con la clave tomada, el host no arranca hasta soltarla.
   Amarra que `Program.cs` usa el lock y no sólo que el helper existe.

**Backend, D-1** — amarre del contrato, no RED: `TheHubAcceptsAWebSocketWithoutNegotiate` en `MessagingHubTests`, con
`SkipNegotiation = true`, `Transports = HttpTransportType.WebSockets` y el `WebSocketFactory` de
`factory.Server.CreateWebSocketClient()`, **con el stub de auth apagado** (cookie real, middleware de CSRF registrado) y
el tenant sólo en el query. Las pruebas actuales del hub usan long polling (`MessagingHubTests.cs:42`). Si esta falla,
D-1 no sirve y se pasa al plan B.

**Frontend, D-1** (slice de `qep-frontend`): RED en `messaging-hub.test.ts`: `withUrl` recibe `skipNegotiation: true`
y `transport: HttpTransportType.WebSockets`.

**Backend, D-7:** las pruebas de la presencia viven en el spec de Valkey (P-3: `TwoProcessesSeeEachOthersMembers`,
`AMemberWithoutHeartbeatDisappearsAfterNinetySeconds`, `DisconnectingTheLastLocalConnectionRemovesTheMember` y el
respaldo con Valkey caído). Acá no queda ninguna.

**D-3, D-4 y D-5** son manifiestos y documentación: se verifican en el clúster, no con pruebas.

## Despliegue

En este orden, cada paso desplegado y verificado antes del siguiente:

1. **Backend con `replicas: 1`:** D-2 (lock `xact` sobre la conexión directa, con su prueba de paridad
   `TheLockConnectionDoesNotGoThroughThePooler`), D-3 (`startupProbe`), comentario de `ExportLoadSeedWorker` y la regla
   D-5 en el README. Con una réplica nada cambia de comportamiento; sirve para medir el bloque de arranque con el log
   nuevo. **Prerrequisito que corre en paralelo:** el spec
   [`2026-11-13-valkey-estado-compartido-design.md`](2026-11-13-valkey-estado-compartido-design.md), con su propio
   despliegue (Valkey en `migracion-k8s`, el cliente con las áreas apagadas y luego cada área prendida de a una).
   D-7 (presencia) y DP-1 (limitadores) salen de ahí: sus áreas `Presence` y `RateLimiting` tienen que estar prendidas y
   verificadas en producción, todavía con una réplica, antes del paso 3.
2. **Frontend:** D-1. Funciona igual con una réplica. **Al 2026-11-13 no está hecho** (§1).
3. **Backend:** D-4 (`replicas: 2` + `pdb` en el pipeline). Prerrequisitos, todos, antes de aplicar:
   - D-1 mergeada y **desplegada** en el frontend (verificado en el navegador: sin `negotiate`).
   - La clave `QepDatabaseListener` verificada en el log de arranque de **cada** pod (ver «Verificación»).
   - DP-2 cerrada (2026-11-13, sin cambios de pool). DP-1 y DP-3 están cerradas por el spec de Valkey: lo que se exige es que
     las áreas `Presence` y `RateLimiting` estén prendidas (paso 3 de ese spec) y `qep_valkey_connected` valga 1 en
     el pod.
   - Inventario D-6 con fecha del mismo día y la prueba de arquitectura en verde.

## Verificación

En PowerShell. `kubectl` siempre con `--context contabo-prod` (el contexto activo por defecto es otro clúster). Nunca
`kubectl get secret -o yaml` ni `-o json`: imprimen los valores.

Después del paso 1, cuánto tardó el arranque y si el lock aparece en el log:

```powershell
kubectl --context contabo-prod -n prod-qep-backend get pods -o wide
kubectl --context contabo-prod -n prod-qep-backend get pod <pod> -o jsonpath='{.status.startTime}{"\n"}{range .status.conditions[*]}{.type}={.lastTransitionTime}{"\n"}{end}'
kubectl --context contabo-prod -n prod-qep-backend logs <pod> --since=30m | Select-String -Pattern "lock"
```

Después del paso 2, en el navegador (DevTools → Network, módulo de Mensajería): no hay request a `…/negotiate` y el
WebSocket a `/api/v1/hubs/messaging?tenantId=…` responde `101`. Un mensaje entrante llega sin recargar.

Antes del paso 3, capacidad del nodo (hacen falta 1,5 CPU y 1,5 GiB reservables para la app durante el rollout):

```powershell
kubectl --context contabo-prod describe node | Select-String -Pattern "Allocatable|Allocated resources" -Context 0,8
```

Antes del paso 3, **la conexión del listener de cada pod es directa.** El reparto de eventos entre pods (§1) depende de
que el Secret traiga `DATABASE_LISTENER_CONNECTION_STRING` resuelto (`k8s/prod-secret.yaml:29`): si falta, el listener
cae a `QepDatabase`, que pasa por PgBouncer, y **ese pod no recibe ningún evento sin error** (gotcha de `CLAUDE.md`). Con
un pod el síntoma era «no llega nada»; con dos, la mitad de los asesores lo ven y la otra mitad no. El log de arranque de
`MessagingEventsListener` (`MessagingEventsListener.cs:29`, «…using connection string key {ConnectionStringKey}») tiene que
decir `QepDatabaseListener`, nunca `QepDatabase`, en **cada** pod:

```powershell
kubectl --context contabo-prod -n prod-qep-backend get pods -o name | Select-String "qep-backend" | ForEach-Object {
  "$_"; kubectl --context contabo-prod -n prod-qep-backend logs "$_" --since=24h | Select-String -Pattern "listener is listening"
}
```

Si alguno dice `QepDatabase`, se arregla el Secret antes de subir réplicas; con D-2 el mismo log del lock dice qué clave usó.

Después del paso 3:

```powershell
kubectl --context contabo-prod -n prod-qep-backend get deploy qep-backend
kubectl --context contabo-prod -n prod-qep-backend get pdb
kubectl --context contabo-prod -n prod-qep-backend describe pdb qep-backend
```

Y a mano: iniciar sesión, navegar un rato y abrir Mensajería en dos pestañas; los logs de los dos pods tienen que
mostrar requests de la misma sesión. Durante 15 minutos, revisar los logs por fallas de `AuditProjectionWorker` o
`PaymentProofMoveProcessor`: se esperan algunas (seguimiento), no en otros workers.

## Rollback

- **Paso 3:** revertir el commit vuelve a `replicas: 1`, pero **el pipeline no borra el PDB que ya aplicó**, y con una
  réplica bloquea el drain del nodo. Hay que borrarlo a mano:
  `kubectl --context contabo-prod -n prod-qep-backend delete pdb qep-backend`.
- **Paso 2:** revertir el frontend. Si el paso 3 ya está, revertir primero el 3: sin `skipNegotiation` y con dos pods el
  tiempo real vuelve a fallar a medias.
- **Paso 1:** revertir el commit. El lock no deja nada en la base; un `startupProbe` más largo no afecta a nadie.

## Lo que queda al builder

- Nombre y forma del helper del lock y su clave, y cómo la prueba 1 detecta que «no corrió nada».
- Los `EventId` de los logs nuevos (los de `QepSeedRunner` usan el rango 4100-4107).
- El texto de la regla D-5 en el README y su ubicación.
- El reparto: backend y frontend van en slices propios, cada uno en su repo.
