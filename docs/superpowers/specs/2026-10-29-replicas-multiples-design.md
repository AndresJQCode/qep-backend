# Réplicas múltiples de la API — diseño

Fecha: 2026-10-29. Estado: borrador para aprobación del owner. Reemplaza el plan
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
(`qep-frontend/src/features/messaging/services/messaging-hub.ts:68-76`), sin `skipNegotiation` ni `transport`. Así,
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

**El problema.** Antes de `app.RunAsync()` (`Program.cs:301`), cada pod corre las 19 inicializaciones de base
(`Program.cs:226-295`, cada una un `MigrateAsync`) y después `RunQepSeedAsync` (`Program.cs:299`). En producción
`Seed__Enabled` es `"true"` (`k8s/prod-configMap.yaml:139`). Sin bloqueo entre pods:

- **La semilla** consulta y después inserta (`IdentitySeeder.cs:30-39`, `CatalogSeeder.cs:40-48`). Dos pods sobre
  datos que aún no existen chocan contra un índice único y el que pierde tumba su arranque. Sólo el paso del tenant
  operador atrapa esa carrera (`QepSeedRunner.cs:132-147`); el usuario, el tenant, la numeración, el layout, el
  catálogo y las empresas no.
- **Geography** reconcilia departamentos y ciudades **en cada arranque**, no sólo la primera vez
  (`GeographyDatabaseInitializer.cs:16-19`, `GeographySeeder.cs:34-41`). El día que llegue un DIVIPOLA con ciudades
  nuevas, dos pods que arrancan juntos insertan las mismas y uno choca con el índice único de `DivipolaCode`
  (`GeographyDbContext.cs:112`).
- **Las migraciones** probablemente ya están serializadas: cada módulo tiene su propia tabla de historial
  (`MigrationsHistoryTable`, una por contexto) y el ensamblado de `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3
  (`Directory.Packages.props:40`) trae `NpgsqlMigrationDatabaseLock` y el texto `LOCK TABLE`. *No verificado en el
  código fuente del proveedor*, sólo en las cadenas del binario: no se cuenta con eso.

**Cuándo arrancan dos pods a la vez.** Con `maxSurge: 1` y `maxUnavailable: 0`, un rollout crea los pods nuevos de a
uno, así que el deploy normal no produce la carrera. La producen un reinicio del nodo (con un solo nodo, los dos pods
vuelven juntos), los dos pods cayendo a la vez, o un `rollout restart` con el anterior todavía arrancando. Es raro, pero
el que pierde entra en reinicio y el que más arriesga es justo el día del cambio de datos.

**Decisión (D-2): un `pg_advisory_lock` de sesión, en una conexión propia, alrededor de todo el bloque
migraciones + semilla.** El primer pod lo toma, migra y siembra; el segundo espera y, cuando lo obtiene, encuentra todo
hecho y pasa rápido.

- Conexión dedicada a partir de `ConnectionStrings:QepDatabase`, con `Pooling = false` como el listener
  (`MessagingEventsListener.cs:79`): el lock es de sesión y una conexión que vuelve al pool no garantiza soltarlo en el
  momento. Si el pod muere, PostgreSQL suelta el lock al cerrarse la sesión.
- `pg_advisory_unlock` explícito en un `finally`, también cuando una migración o la semilla lanzan.
- La espera respeta `app.Lifetime.ApplicationStopping`: un pod que Kubernetes mata mientras espera no queda colgado.
- La clave es una constante propia y documentada. El builder verifica que no choque con las claves que ya se usan
  (`TenantChangeLock`, `PosSaleIdLock`, `UserLifecycleLockKey` —que usa `hashtext`— y las de los unit of work).
- Va en Bootstrapper (ya referencia las Infrastructure de los módulos, como `QepSeedRunner`), y `Program.cs` envuelve
  las líneas 226-299 en una sola llamada. Las inicializaciones no cambian ni de orden ni de contenido.
- Log de cuánto esperó y cuánto tardó el bloque, para medir el `startupProbe` (§3) con datos.

**Alternativa descartada: un `Job` previo al deploy** que migre y siembre, con la API sin migrar al arrancar. Es el
patrón más limpio a largo plazo, pero exige un modo de la imagen que migre y salga, que la plantilla
`k8s/deploy.yml@k8sTemplates` aplique y espere un `Job` antes del Deployment (no se ve desde este repo), y borrarlo
antes de cada apply porque un `Job` es inmutable. D-2 son unas decenas de líneas y no cambia el pipeline. Si un día las
migraciones pasan del presupuesto del `startupProbe`, se revisa.

### 3. Presupuesto del `startupProbe` (D-3)

Hoy: `periodSeconds: 5`, `failureThreshold: 12` → **60 s** (`prod-deployment.yaml:46-51`). Kestrel empieza a escuchar
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
- `azure-pipelines.yml`: `pdb` en la lista de `manifests`, **después de `deployment`** (hoy la lista termina en
  `deployment`, `ingress`). Lo pide el encabezado de `k8s/prod-pdb.yaml`: con `replicas: 1` el PDB (`minAvailable: 1`)
  bloquearía el drain del nodo, por eso nunca va sin las dos réplicas.
- README § health (líneas 1054-1058): deja de decir que el PDB «no se despliega».

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

## Decisiones pendientes del owner

### DECISIÓN-PENDIENTE DP-1: los limitadores de tasa son por pod

Los tres viven en la memoria del proceso (`Program.cs:72-118`). Con dos pods y reparto parejo:

| Política | Hoy | Con 2 pods | Efecto |
| --- | --- | --- | --- |
| `Public` (`:75-85`) | 120/min por IP | hasta 240 | Scraping del OpenAPI y del inicio de Shopify; bajo riesgo |
| `Credentials` (`:89-98`) | 10/min por IP y ruta | hasta 20 | **Seguridad:** el doble de intentos de contraseña |
| `Webhook` (`:104-117`) | concurrencia por pod | el doble en total | Es lo buscado: protege a cada pod |

Opciones para `Credentials`:

1. **Bajar `PermitLimit` a 5 por pod.** Una línea; el total vuelve a ~10. Contra: el reparto no es exacto (un atacante
   con suerte saca algo más, uno legítimo puede chocar antes) y el número queda atado a la cantidad de réplicas.
2. **Límite en el ingress** (`nginx.ingress.kubernetes.io/limit-rpm`). Global de verdad, pero se aplica por objeto
   `Ingress`: hay que separar las rutas de credenciales en uno propio, y en ingress-nginx el contador es por réplica del
   controlador. No verificado contra el clúster.
3. **Aceptarlo.** 20 intentos por minuto por IP y por ruta, con las contraseñas hasheadas y la sesión opaca.

Recomendación: 1, con un comentario que lo ate a `replicas`. Lo decide el owner.

### DECISIÓN-PENDIENTE DP-2: conexiones a PostgreSQL

Cada pod abre, además del pool de Npgsql, una conexión permanente sin pool para `LISTEN` (`MessagingEventsListener.cs:79`)
y, con D-2, una transitoria para el lock. Los 34 `AddHostedService` no tienen conexiones propias: toman del pool al
sondear. El tope del pool sale de la cadena de conexión, que vive en el Secret (`ConnectionStrings__QepDatabase`) y
**no se puede leer desde el repo**; sin `Maximum Pool Size`, Npgsql usa 100 por pod. `max_connections` del servidor
tampoco está en el repo, ni dónde corre la base de producción.

Hace falta que el owner confirme `max_connections` y el pico actual de conexiones antes del paso 3, y decida si fija
`Maximum Pool Size` en la cadena (en el Secret, nunca en el ConfigMap). Con dos pods el peor caso es el doble.

## Lo que ya funciona con varias réplicas

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
| Consumidores de outbox | `ClaimedOutboxConsumer`: `SessionRevocationWorker`, `OrphanUserCleanupWorker`, `QuotationEventsProcessor` (Pipeline), `CitySynonymEventsProcessor` (Shopify) |
| Correos | El reclamo es de `OutboxDeliveryWorker` (`InvitationDeliveryWorker.cs:14`) |
| Tiempo real | `pg_notify` + `LISTEN` por pod (§1) |
| IP del cliente | `UseForwardedHeaders` primero (`Program.cs:127`), redes confiables en `prod-configMap.yaml:42-43` |
| Readiness | `/health/ready` corre `SELECT 1` (`DatabaseReadinessCheck.cs:25`) |
| Workers vs. esquema | Los hosted services arrancan en `app.RunAsync()` (`Program.cs:301`), después de migrar y sembrar |
| Archivos | En R2; el Deployment no monta volúmenes |

## Fuera de alcance (seguimiento)

Inofensivos con dos pods: producen ruido en el log o trabajo repetido, no datos malos.

- **`AuditProjectionWorker`** (`AuditProjectionWorker.cs:51-85`) y **`PaymentProofMoveProcessor`**
  (`PaymentProofMoveProcessor.cs:74-81, 172`) leen pendientes por anti-join contra su inbox, sin reclamar. Los dos pods
  pueden tomar el mismo mensaje; efecto e inbox van en el mismo `SaveChanges`, así que el que pierde choca con la clave
  del inbox y se deshace entero. En Audit el resto del lote de ese tick también falla (las entidades quedan rastreadas
  en el contexto) y se reintenta en el siguiente. Arreglo: pasarlos a `ClaimedOutboxConsumer`.
- **Barridos periódicos en los dos pods:** `QuotationExpirationWorker` (cada `ExpirationSweepMinutes` = 60; `Version`
  es token de concurrencia, `QuotationsDbContext.cs:160`, así que el segundo choca y no duplica historial),
  `StagingCleanupWorker` (cada `StagingCleanupMinutes` = 60), `PaymentProofOrphanCleanupWorker` (cada 24 h) y
  `WebhookPurgeWorker` (un `DELETE`, idempotente). En las dos limpiezas de Storage, borrar en R2 una clave que ya no
  existe no falla; *no verificado* si la auditoría puede quedar dos veces. Arreglo: `pg_try_advisory_lock` por pasada.
- **`InstagramTokenRefreshWorker`** (diario): los dos pods pueden refrescar el mismo token. *No verificado* si Meta
  invalida el anterior al refrescar; si lo hiciera, gana el último que guarda y no se pierde nada.
- **`ConnectionSecretRekeyWorker`** y **`CustomerPhoneBackfillWorker`**: idempotentes por diseño; un lote que choca se
  salta y se cuenta (`ConnectionSecretRekeyWorker.cs:24-26`).
- **Cachés en memoria de 60 s** (`WebhookRouting.cs:12`, `AutomationsModuleStatus.cs:14`): no se invalidan en proceso,
  así que el retraso de hasta un minuto ya existe con un pod. Con dos, cada uno tiene el suyo, con el mismo máximo.
- **`ExportLoadSeedWorker`:** apagado en producción (`Seed__ExportLoad__Quotations: "0"`, `prod-configMap.yaml:153`).
  Prendido en dos pods sembraría dos veces; regla: sólo con `replicas: 1`.
- **`Origin` del handshake del hub** (§1, riesgo previo).

## Pruebas (TDD)

RED antes que GREEN, con evidencia literal. Integración contra PostgreSQL de Testcontainers, como las de semilla
existentes (`SeedStartupTests`, `CatalogSeedTests`). Recuerda `$env:Seed__Enabled = "false"` en esta máquina para las
que no siembran.

**Backend, D-2** — el lock se prueba de forma determinista, no confiando en ganar una carrera:

1. **RED** `TheStartupWorkWaitsWhileAnotherInstanceHoldsTheLock`: la prueba toma la clave desde su propia conexión,
   lanza el bloque de arranque y comprueba que no termina en un plazo corto y que no corrió nada (la tabla de historial
   de un módulo todavía no existe). Suelta la clave y el bloque termina. Sin el lock, termina enseguida: es la RED.
2. **RED** `TheLockIsReleasedWhenTheStartupWorkThrows`: el bloque lanza; después, `pg_try_advisory_lock` desde otra
   conexión devuelve `true`.
3. **RED** `CancellingWhileWaitingStopsWithoutTakingTheLock`: con la clave tomada por la prueba, cancelar el token
   termina en `OperationCanceledException`.
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

**D-3, D-4 y D-5** son manifiestos y documentación: se verifican en el clúster, no con pruebas.

## Despliegue

En este orden, cada paso desplegado y verificado antes del siguiente:

1. **Backend con `replicas: 1`:** D-2 (lock), D-3 (`startupProbe`), comentario de `ExportLoadSeedWorker` y la regla
   D-5 en el README. Con una réplica nada cambia de comportamiento; sirve para medir el bloque de arranque con el log
   nuevo.
2. **Frontend:** D-1. Funciona igual con una réplica.
3. **Backend:** D-4 (`replicas: 2` + `pdb` en el pipeline), con DP-1 y DP-2 resueltas o aceptadas por el owner.

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
