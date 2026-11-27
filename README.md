# QEP Backend

Backend de **QCode Enterprise Platform (QEP)** implementado como monolito
modular sobre .NET 10.

El alcance ejecutable actual incluye:

- el corte vertical de configuración de tenants, con lectura y actualización;
- ciclo de vida completo de memberships: invitación con aprovisionamiento de
  usuarios en Identity, listado, suspensión, remoción, reactivación y roles;
- registro público de tenants, sesión por cookie y catálogo de autorización;
- catálogo de productos con opciones, variantes y galería de fotos, con listado, alta, edición y
  desactivación;
- biblioteca de archivos sobre Cloudflare R2, con carga firmada, escaneo
  antimalware, variantes y publicación;
- notificaciones por email con proveedor conmutable;
- aislamiento por tenant y autorización basada en permisos;
- control de concurrencia optimista mediante `ETag` e `If-Match`;
- persistencia PostgreSQL con migraciones de Entity Framework Core;
- auditoría transaccional y publicación mediante Outbox/Inbox idempotente;
- trazas y métricas con OpenTelemetry;
- pruebas unitarias, de arquitectura e integración.

> [!WARNING]
> La autenticación por encabezados es exclusivamente para `Development`. No se
> debe desplegar en un ambiente compartido o productivo. Consulte
> [`docs/decisions/0001-development-auth-stub.md`](docs/decisions/0001-development-auth-stub.md).

## Requisitos

- .NET SDK `10.0.400` o un parche posterior compatible con `global.json` (la imagen compila con `10.0.402`, el
  que trae el runtime 10.0.13; instalarlo en local deja tu máquina igual que el build);
- Docker con Docker Compose.

Las pruebas de integración también requieren Docker, ya que crean una instancia
aislada de PostgreSQL mediante Testcontainers.

## Ejecución local

Desde este directorio:

```powershell
docker compose up -d
dotnet restore --locked-mode

# Requerido una sola vez: la cadena de conexión no vive en appsettings.json.
dotnet user-secrets set "ConnectionStrings:QepDatabase" "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev" --project src/Api

# Valkey (estado compartido entre pods). Con Valkey:Enabled = false, el default, no conecta nada; si ya tienes
# otro Valkey en 6379, no levantes el servicio valkey de compose.yaml.
dotnet user-secrets set "ConnectionStrings:Valkey" "localhost:6379" --project src/Api

dotnet run --project src/Api --launch-profile http
```

Los valores del ejemplo son los que crea [`compose.yaml`](compose.yaml). Si omite
ese paso, la API falla al iniciar con
`InvalidOperationException: Connection string 'QepDatabase' is required.` — un
error que dice exactamente qué falta, en lugar de un fallo de conexión contra una
base que no existe.

Al iniciar, la API aplica automáticamente las migraciones de los módulos
Tenancy e Identity. En `Development`, si aún no existen tenants, también crea el
tenant de demostración.

| Recurso                              | Dirección                               |
| ------------------------------------ | --------------------------------------- |
| API                                  | `http://localhost:5000`                 |
| Health check (liveness)              | `http://localhost:5000/health/live`     |
| Health check (readiness, con base)   | `http://localhost:5000/health/ready`    |
| Documento OpenAPI (solo Development) | `http://localhost:5000/openapi/v1.json` |
| PostgreSQL                           | `localhost:5432`                        |
| OTLP gRPC / HTTP                     | `localhost:4317` / `localhost:4318`     |
| Métricas del collector               | `http://localhost:8889/metrics`         |

Para detener la infraestructura:

```powershell
docker compose down
```

El volumen `qep-postgres` conserva los datos. Use `docker compose down -v`
únicamente cuando quiera eliminar también la base local.

## Configuración

La configuración base está en `src/Api/appsettings.json` y puede
sobrescribirse con variables de entorno, secretos de usuario o los mecanismos
estándar de configuración de ASP.NET Core.

[`src/Api/appsettings.example.json`](src/Api/appsettings.example.json) es el
**inventario completo de claves** que la aplicación lee, con sus valores por
defecto reales y un placeholder `<user-secrets: ...>` donde el valor es una
credencial. No lo carga nadie —está excluido del output en `Api.csproj`— y no
se copia sobre `appsettings.json`: se lee para saber qué existe. Los secretos
siguen yendo por [secretos de usuario](#secretos-de-usuario), nunca en un
`appsettings*.json`.

Las claves obligatorias no se deducen de ese archivo sino de los validadores que
corren con `ValidateOnStart` (`StorageOptionsValidator`,
`NotificationsOptionsValidator`, `SessionOptionsValidator`,
`AuditOptionsValidator`, `QuotationsOptionsValidator`, `SeedOptionsValidator`,
`PaymentProofsOptionsValidator`, `ForwardedHeadersSettingsValidator`,
`CorsSettingsValidator`, `OperatorTenantOptionsValidator`, `ValkeyOptionsValidator` y los dos de Integrations,
`SecretProtectionOptionsValidator` y `ZenviaOptionsValidator`): si algo
falta o está mal escrito, la API **no arranca**.

`ConnectionStrings:QepDatabase` **no está en `appsettings.json`**, a propósito:
lleva una contraseña, y la regla de este repositorio es que una credencial nunca
se compromete en un `appsettings*.json`. Se provee por secretos de usuario en
local y por variable de entorno en k8s
([`prod-secret.yaml`](k8s/prod-secret.yaml), no el ConfigMap: lleva contraseña).

| Clave                                                  | Valor local                                                                                   | Uso                                                                                                                 |
| ------------------------------------------------------ | --------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| `ConnectionStrings:QepDatabase`                        | **sin valor por defecto — requerido**                                                         | Conexión compartida por los módulos. Ausente ⇒ la API no inicia                                                     |
| `ConnectionStrings:QepDatabaseListener`                | opcional; sin definir cae a `QepDatabase`                                                     | Conexión del listener de tiempo real (`LISTEN messaging_events`). `LISTEN` necesita una conexión directa: en producción debe apuntar a `pg-platform-rw`, nunca al pooler (PgBouncer en modo `transaction` no mantiene el `LISTEN` y no da error). Ausente, vacía o con el token `#{…}#` sin resolver ⇒ usa `QepDatabase` |
| `ConnectionStrings:Valkey`                             | `localhost:6379` (user-secrets)                                                               | Cadena de StackExchange.Redis hacia Valkey (spec 2026-11-13). En producción, `prod-secret.yaml` con el usuario ACL `qep` y su contraseña. `ValkeyOptionsValidator` la exige en `Production` aunque `Valkey:Enabled` sea `false`; vacía o con el token `#{…}#` sin resolver cuenta como ausente |
| `Valkey:Enabled` / `Valkey:Areas:*`                    | `false`                                                                                       | Interruptor general del cliente y uno por área (`Presence`, `RateLimiting`, `Sessions`, `Permissions`, `SendBudget`, `Doorbells`, `ApiKeyTouch`, `LegalParameters`). Un área apagada usa exactamente las clases de antes. `SendBudget` no tiene efecto: el presupuesto de envío se queda en Postgres (decisión del owner, 2026-11-13) |
| `Valkey:OperationTimeoutMs`                            | `100`                                                                                         | Tope de cada operación contra Valkey; pasado, el área vuelve a su respaldo local. Debe ser mayor que cero |
| `OpenTelemetry:Endpoint`                               | `http://localhost:4317`                                                                       | Exportación OTLP de trazas y métricas                                                                               |
| `OTEL_SERVICE_NAME`                                    | sin definir (cae a `qep-api`)                                                                 | `service.name` del recurso; en k8s lo fija el Deployment                                                            |
| `ForwardedHeaders:KnownNetworks`                       | ausente (sólo el loopback del framework)                                                      | Redes CIDR desde las que se confía en `X-Real-IP` y `X-Forwarded-Proto`; `X-Forwarded-For` se ignora (ver [Pipeline HTTP](#pipeline-http-programcs)). De ahí sale la IP del cliente para el rate limiter `public`, la sesión y la auditoría del registro. Una red inválida ⇒ la API no inicia. En k8s: los nodos del ingress y el pod CIDR de Cilium |
| `Cors:AllowedOrigins`                                  | ausente (sin CORS); `appsettings.Development.json` trae `http://localhost:3002`                | Orígenes exactos desde los que el navegador llama a la API con la cookie de sesión. Cada uno `https://host[:puerto]`, sin barra final, path ni comodín; uno inválido ⇒ la API no inicia. Sólo en `Development` vale además `http://localhost[:puerto]`: es `bun dev` llamando directo con `VITE_API_BASE_URL=http://localhost:5000`, sin el proxy de Vite (que no reenvía el WebSocket del hub de Mensajería). Un túnel con host propio (`https://…`) se suma por user-secrets en `Cors:AllowedOrigins:1`. La defensa CSRF depende de que sea exacta: un comodín —ni siquiera `*.qcode.co`, donde corren otras apps— la desactiva (ver [Pipeline HTTP](#pipeline-http-programcs)). En k8s: `https://qep.qcode.co` |
| `Authentication:UseDevelopmentStub`                    | `true` en Development, pero **los dos perfiles de `launchSettings.json` lo fijan en `false`** | Stub de identidad por headers `X-*`. Fuera de Development, `true` aborta el arranque                                |
| `Authentication:Authority`                             | ausente (cae a `https://accounts.google.com`)                                                 | Emisor OIDC; sólo se define para pisar el de Google                                                                 |
| `Authentication:Audience`                              | ausente                                                                                       | Audiencia JWT; requerida fuera de Development salvo que se dé `Authentication:Google:ClientId`                      |
| `Authentication:Session:CookieName`                    | `qep_session`                                                                                 | Nombre de la cookie de sesión                                                                                       |
| `Authentication:Session:AbsoluteLifetimeDays`          | `30`                                                                                          | Vida máxima de la sesión. Debe ser positiva y ≥ `IdleTimeoutDays`                                                   |
| `Authentication:Session:IdleTimeoutDays`               | `7`                                                                                           | Expiración por inactividad                                                                                          |
| `Registration:PublicTenantSignupEnabled`               | `true` en `appsettings.json`                                                                  | Alta pública de tenants. **Ausente ⇒ `false`**: se lee con `GetValue<bool>`                                         |
| `Notifications:EmailProvider`                          | `log` en `appsettings.json`                                                                   | `log` o `infobip`. Con `infobip`, las tres claves `Notifications:Infobip:*` pasan a ser requeridas. **En `Production` `log` es rechazado**: el arranque falla en lugar de dar por entregada una invitación que nunca sale |
| `Notifications:InvitationUrl`                          | `http://localhost:3002/invitations`                                                           | Base absoluta del deep-link de invitación; el email lleva `{InvitationUrl}/{token}`                                 |
| `Notifications:AppBaseUrl`                             | `http://localhost:3002`                                                                       | Base del frontend para los links de cuenta (verificar, restablecer) y el de la invitación de mayorista, `{AppBaseUrl}/wholesale-invitations/{token}`. El mayorista no tiene variable propia |
| `Audit:SecurityRetentionDays`                          | `2555` (~7 años)                                                                              | Ventana de retención de auditoría de seguridad. Debe ser positiva                                                   |
| `Audit:OperationalRetentionDays`                       | `730` (2 años)                                                                                | Ventana de retención de auditoría operativa. Debe ser positiva                                                      |
| `Storage:PresignedUrlMinutes`                          | `5`                                                                                           | Vigencia de la URL firmada. Debe ser positiva                                                                       |
| `Storage:ExportUrlHours`                               | `24`                                                                                          | Vigencia del enlace de descarga de un reporte exportado. Entre 1 y 168 (SigV4 no firma mas de 7 dias)               |
| `Storage:StagingRetentionHours`                        | `24`                                                                                          | Retención de los objetos en staging. Debe ser positiva                                                              |
| `Storage:StagingCleanupMinutes`                        | `60`                                                                                          | Período del barrido de staging. Debe ser positivo                                                                   |
| `Storage:PaymentProofOrphanCleanup:MinimumAgeHours`    | `24`                                                                                          | Edad mínima de un objeto de `payment-proofs/` para que la reconciliación lo considere. Debe ser positiva             |
| `Storage:PaymentProofOrphanCleanup:IntervalHours`      | `24`                                                                                          | Período de la reconciliación de `payment-proofs/`. Entre 1 y 1193                                                   |
| `Storage:PaymentProofOrphanCleanup:DryRun`             | `true` en `appsettings.json` y en `k8s/prod-configMap.yaml`                                   | Con `true` la reconciliación sólo escribe en el log lo que borraría. Se pasa a `false` a mano, después de revisar esos logs en producción |
| `Storage:R2:PublicBucket` + `Storage:R2:PublicBaseUrl` | ausentes                                                                                      | Bucket público de lectura y su dominio. **Se configuran juntos o ninguno**; `PublicBaseUrl` debe ser HTTPS absoluta. Los usa la publicación de imágenes de producto **y** el logo del tenant (`PUT /settings/logo`); sin ellos, `PUT` responde `422 storage.public.not_configured` |
| `Storage:ClamAv:Enabled`                               | `false`                                                                                       | Escaneo de malware. Con `true`, `Host` no puede estar vacío                                                         |
| `Storage:ClamAv:Host` / `Port` / `TimeoutSeconds`      | `clamav` / `3310` / `30`                                                                      | Destino del escaneo. `Port` entre 1 y 65535                                                                         |
| `Quotations:PaymentProofs:PublicLinks`                 | `false` en `appsettings.json`                                                                 | Con `true`, cada comprobante de pago nuevo se copia al bucket público al adjuntarse y el Excel de pedidos lo enlaza. **Exige `Storage:R2:PublicBucket` y `Storage:R2:PublicBaseUrl` en cualquier ambiente**: sin ellos la API no arranca. Apagarla no despublica lo ya copiado |
| `Integrations:SecretProtection:ActiveKeyId`            | ausente (user-secrets en local)                                                               | Id de la llave con la que se cifran las credenciales de las conexiones (`^[a-z0-9]{1,32}$`). **En `Production` es obligatoria**, y su llave tiene que existir y ser de 32 bytes en base64: sin ella la API no arranca. Fuera de producción, sin ella la API arranca y crear o editar una conexión responde `503 integrations.secret_protection.unavailable`. Ver [Integraciones](#integraciones-conexiones-del-tenant) |
| `Integrations:SecretProtection:Keys:<id>`              | user-secrets                                                                                  | Llave AES-256 (32 bytes en base64). Es un secreto: en k8s va en el Secret, nunca en el ConfigMap |
| `Integrations:SecretProtection:RekeyIntervalMinutes`   | `60`                                                                                          | Cada cuánto el worker re-cifra con la llave activa, además de al arrancar. Entre 1 y 1440 |
| `Integrations:Zenvia:BaseUrl`                          | `https://api.zenvia.com`                                                                      | URL de la prueba de credenciales de Zenvia (`GET /v2/templates`). HTTPS absoluta. El envío de cotizaciones y pedidos usa la suya, `Quotations:WhatsApp:BaseUrl` |
| `Meta:App:AppId`, `Meta:App:ConfigId`                | ausentes (user-secrets en local)                                                              | La app de Meta de toda la plataforma (spec 2026-10-09). Públicos: viajan en el popup de Embedded Signup. **En `Production` son obligatorios** junto con los tres de abajo (`MetaAppOptionsValidator`); fuera, sin ellos `whatsapp-cloud` no sale en el catálogo y el webhook responde 403/401 |
| `Meta:App:GraphApiVersion`                           | `v24.0` (sólo fuera de `Production`)                                                          | Versión de Graph; patrón `^v\d+\.\d+$`. **También es obligatoria en `Production`** (`MetaAppOptionsValidator` exige las cinco claves: AppId, ConfigId, GraphApiVersion, AppSecret, WebhookVerifyToken); el ConfigMap de k8s la fija en `v24.0` |
| `Meta:App:AppSecret`, `Meta:App:WebhookVerifyToken`  | user-secrets                                                                                  | Secretos: el `AppSecret` canjea el `code` y firma el webhook; el token verifica la suscripción (aleatorio, ≥ 32 caracteres). En k8s van en el Secret |
| `Meta:App:WebhookOverrideCallbackUrl` | vacío | **Temporal.** URL https que la suscripción de una WABA le pide a Meta en vez de la URL de la app; vacío = sin override. Ver [Historial de coexistencia](#historial-de-coexistencia) |
| `Messaging:Webhook:MaxBodyBytes`                     | `4194304`                                                                                     | Tope del cuerpo del webhook (Meta manda hasta 3 MB); más → 413 |
| `Messaging:Webhook:ConcurrencyLimit` / `QueueLimit`  | `64` / `256`                                                                                  | Limitador `webhook`: concurrencia global con cola, no por IP |
| `Messaging:Workers:*`                                | `DeliveryPollSeconds 3`, `MediaPollSeconds 5`, `CampaignPollSeconds 10`, `ScheduledPollSeconds 5`, `PurgeIntervalHours 24`, `DeliveryRetentionDays 7` | Intervalos de los workers de entregas, medios, campañas, mensajes programados y purga |
| `Messaging:Campaigns:MessagesPerSecond`              | `20` (opcional; `1 <= valor <= MaxMessagesPerSecond`)                                         | Ritmo inicial y objetivo de envío de una campaña **por conexión**, compartido por todas las réplicas (cubo en Postgres, `messaging.connection_send_budgets`). Es adaptativo: un `130429` de Meta lo baja a la mitad y pausa la conexión 30 s; sin nuevos `130429` sube 1 mps cada 30 s hasta este valor |
| `Messaging:Campaigns:MaxMessagesPerSecond`           | `80` (opcional; hasta `1000`)                                                                 | Techo del ritmo: el throughput por defecto de Meta por número. Súbelo sólo si Meta subió el de tu número |
| `Messaging:MarketingOptOut:ButtonTexts`              | `["Detener promociones", "Stop promotions"]` (opcional)                                       | Textos (o payloads) del botón de respuesta rápida de una plantilla que apagan el marketing del cliente, con origen `button`. Sin mayúsculas ni tildes; un mensaje de texto escrito a mano nunca cuenta. Si se configura, **reemplaza** la lista |
| `Messaging:Search:StatementTimeoutMs`                | `2000`                                                                                        | Tope de la búsqueda en el historial; al pasarlo responde 422 en `q` |
| `ApiAccess:RateLimit:PerKeyPerMinute`                | `300`                                                          | API externa: peticiones por minuto por clave, después de autenticar. En memoria, ver «API externa». |
| `ApiAccess:RateLimit:PerIpPerMinute`                 | `600`                                                          | API externa: peticiones por minuto por IP, antes de autenticar. En memoria, ver «API externa». |

Ejemplo con variables de entorno:

```powershell
$env:ConnectionStrings__QepDatabase = "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev"
$env:OpenTelemetry__Endpoint = "http://localhost:4317"
```

En k8s no se define `OpenTelemetry:Endpoint`: el Collector se referencia con la
variable estándar `OTEL_EXPORTER_OTLP_ENDPOINT` (leída directamente por el
exportador OTLP), junto con `OTEL_SERVICE_NAME` y, opcionalmente,
`OTEL_RESOURCE_ATTRIBUTES` para `service.namespace`/`service.instance.id`.

### R2: regla de ciclo de vida de las respuestas HTTP de chatbots

**Configuración obligatoria fuera del código, antes de desplegar RC-B1b** (captura de respuestas del bloque HTTP,
spec [`2026-10-25-chatbot-reproduccion-de-corridas-design.md`](docs/superpowers/specs/2026-10-25-chatbot-reproduccion-de-corridas-design.md)
§ «Ciclo de vida de R2»). El cuerpo de cada respuesta se guarda en R2 bajo
`automations/http-responses/{tenantId}/{responseId}.json.gz` y **nada en el código lo borra**: la regla de ciclo de
vida es la única retención. Sin ella los cuerpos se acumulan sin límite y el backend responde
`automations.http_response.expired` a los 3 días aunque el objeto siga ahí.

**Va sólo en el bucket privado**, el de `Storage:R2:Bucket` (`STORAGE_R2_BUCKET` del grupo `Backend-prod`,
`k8s/prod-configMap.yaml:77`). El bucket público (`Storage:R2:PublicBucket`) no recibe nunca estos cuerpos (traen datos
de clientes y se sirven sólo por `GET …/response`, con tenant y permiso) y no lleva esta regla. Se repite en cada
ambiente que tenga su propio bucket privado y despliegue RC-B1b.

El bucket privado guarda también los adjuntos de Mensajería, el staging y los comprobantes de pago: **una regla sin
prefijo los borraría a los 3 días**. El prefijo lleva la barra final.

Panel de Cloudflare: R2 → el bucket privado → Settings → Object lifecycle rules → Add rule.

| Campo | Valor |
| --- | --- |
| Nombre | `automations-http-responses-3d` |
| Prefijo | `automations/http-responses/` |
| Acción | Delete objects, 3 días después de creado |

Nada más marcado. Alternativa por CLI, en PowerShell (wrangler pide su propio login; no se usa ninguna credencial de
`qep-backend-secret`). Confirma la sintaxis con `--help`, porque wrangler la cambió entre versiones:

```powershell
npx wrangler r2 bucket lifecycle add --help
npx wrangler r2 bucket lifecycle add <bucket-privado> automations-http-responses-3d automations/http-responses/ --expire-days 3
npx wrangler r2 bucket lifecycle list <bucket-privado>
```

`list` tiene que mostrar una sola regla nueva, con ese prefijo. La ventana de 3 días es fija en el código
(plan D-P8, `HttpResponseObjects.Window`, que llega con RC-B1b): si la regla cambia de días, el backend y el bucket
dejan de coincidir.

### Secretos de usuario

Las credenciales de proveedores externos son **secretos por ambiente**: nunca se
comprometen en `appsettings*.json`. En `Development`/`Local` se cargan desde los
secretos de usuario de `Api`. Copia los comandos y sólo reemplaza los valores
`<...>`; ejecútalos desde este directorio.

> Verifica que una clave existe **sin imprimir su valor**: `dotnet user-secrets list` muestra los valores, así que
> va siempre contando, por ejemplo
> `dotnet user-secrets list --project src/Api | Select-String -Pattern "Storage:R2:SecretAccessKey" | Measure-Object`
> (`Count 1`). No agregues estos valores a `appsettings.Development.json`. `SenderEmail` (Infobip) no es secreto
> y puede ir en configuración normal.

Google (login real, ADR 0014/0015):

```powershell
dotnet user-secrets set "Authentication:Google:ClientId" "<google-oauth-client-id>" --project src/Api
```

Infobip (email transaccional, ADR 0018 — solo con `Notifications:EmailProvider=infobip`):

```powershell
dotnet user-secrets set "Notifications:Infobip:BaseUrl" "<https://xxxxx.api.infobip.com>" --project src/Api
dotnet user-secrets set "Notifications:Infobip:ApiKey"  "<infobip-api-key>"                --project src/Api
```

> El prefijo `Infobip:` no es opcional: `NotificationsOptions` bindea la sección
> `Notifications` y las credenciales cuelgan de `Infobip`. Setear
> `Notifications:ApiKey` a secas no lo lee nadie, y el arranque falla con
> `Notifications:Infobip:ApiKey is required` sin pista de que el problema es el
> prefijo.

Zenvia (envío de la cotización y del pedido por WhatsApp) **no va en user-secrets**: no hay cuenta
global. Cada tenant configura la suya como conexión de Zenvia en Integraciones —token, número emisor,
ID de la plantilla de cotización y, opcional, el de la de pedido— (plan 2026-10-21). En local:

1. El operador habilita Zenvia para el tenant (consola de Plataforma, «Proveedores»).
2. Con `Integrations:SecretProtection:*` configurado (ver [Integraciones](#integraciones-conexiones-del-tenant)),
   se crea la conexión desde Integraciones con los datos de una cuenta de Zenvia real.

La cotización también puede salir por la conexión de WhatsApp Business (Meta) del tenant; ver
[Plantilla de WhatsApp](#plantilla-de-whatsapp-zenvia). Sin ningún canal, «Enviar» responde
`422 quotation.whatsapp.no_channel`; no existe un envío de mentira que registre el mensaje y lo dé por enviado.

Cloudflare R2 (object storage obligatorio, ADR 0020):

```powershell
dotnet user-secrets set "Storage:R2:AccountId"       "<account-id>"      --project src/Api
dotnet user-secrets set "Storage:R2:AccessKeyId"     "<access-key-id>"   --project src/Api
dotnet user-secrets set "Storage:R2:SecretAccessKey" "<secret-access-key>" --project src/Api
dotnet user-secrets set "Storage:R2:Bucket"          "<bucket>"          --project src/Api
# Endpoint opcional; si se omite se deriva como https://<AccountId>.r2.cloudflarestorage.com
dotnet user-secrets set "Storage:R2:Endpoint"        "https://<account-id>.r2.cloudflarestorage.com" --project src/Api

# Bucket público y su dominio. Van de a dos o ninguno; sin ellos, publicar un archivo
# responde 422 storage.public.not_configured y el imageUrl de producto es siempre null.
dotnet user-secrets set "Storage:R2:PublicBucket"    "<bucket-publico>"  --project src/Api
dotnet user-secrets set "Storage:R2:PublicBaseUrl"   "https://<dominio-publico>" --project src/Api
```

`PublicBaseUrl` se concatena con la clave del objeto **sin validación alguna**: si el dominio
no está conectado al bucket público en Cloudflare, la API responde `200` y devuelve una URL
que no carga. Verifique abriendo el `publicUrl` de la respuesta antes de darlo por bueno.

No existe fallback local. La validación de arranque exige `AccessKeyId`,
`SecretAccessKey`, `Bucket` y `Endpoint` o `AccountId` en todos los ambientes.
Las pruebas automatizadas sustituyen `IObjectStorage` por un test double en
memoria; ese adapter no forma parte de la aplicación.

Shopify (`Shopify:App:ClientId` y `Shopify:App:ClientSecret`, la app de la integración de pedidos): los comandos y
la verificación están en [§ Integración con Shopify](#integración-con-shopify).

### `QCODE_PDF_API_KEY` — el MCP de `qcode-pdf`

**La aplicación no lee esta variable.** No es un secreto de usuario como los de arriba: es la
credencial que presenta tu **cliente MCP** —el de Claude Code, vía [`.mcp.json`](.mcp.json)—
cuando le habla a `https://qcode-pdf.qcode.co/mcp`. Sirve para validar un template Typst contra
el compilador real (`validate_typst`) sin desplegar nada ni tener el binario instalado.

No la confundas con la que usa el backend para generar el PDF de una cotización: ésa es
`Quotations:Pdf:ApiKey`, va por user-secrets en local y por `prod-secret.yaml` en producción
(token `QUOTATIONS_PDF_API_KEY`). Son dos claves distintas a propósito.

**De dónde sale el valor.** `qcode-pdf` declara sus clientes en `k8s/prod-configMap.yaml`
(`api-keys.json`), uno por entrada. El de herramientas de desarrollo es **`qep-dev`**, y su
valor vive en el variable group **`Backend-prod`** de Azure DevOps, como
`QCODE_PDF_API_KEY_QEP_DEV`. Pedíselo a quien administre ese grupo; no está en este repo ni
debe estarlo.

Es clave propia y no la de `qep` porque el servicio resuelve el `clientId` por el **hash de la
clave**: compartiéndola, el tráfico de tus pruebas queda indistinguible del de un asesor
enviando una cotización de verdad en los logs y las métricas del servicio, y rotarla obligaría
a tocar producción.

**Dónde se configura.** Como variable de entorno de usuario de Windows, en cualquier PowerShell
(no necesita administrador):

```powershell
[Environment]::SetEnvironmentVariable("QCODE_PDF_API_KEY", "<clave-de-qep-dev>", "User")
```

Ese comando deja la clave en el historial de PowerShell (`ConsoleHost_history.txt`). Para
evitarlo, pídela sin eco:

```powershell
$k = Read-Host "clave qep-dev" -AsSecureString
[Environment]::SetEnvironmentVariable("QCODE_PDF_API_KEY",
  [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($k)), "User")
```

Después **cierra VS Code por completo y vuelve a abrirlo**: los procesos heredan el entorno al
arrancar, así que ni la ventana donde corriste el comando ni una sesión de Claude Code ya
abierta la ven. Para comprobar que quedó guardada sin imprimirla, en una PowerShell nueva:

```powershell
([Environment]::GetEnvironmentVariable("QCODE_PDF_API_KEY","User")).Length
```

`.mcp.json` está versionado y **no contiene el valor**: sólo la expansión `${QCODE_PDF_API_KEY}`,
que Claude Code resuelve contra tu entorno. Cada quien pone la suya.

## Identidad local

El tenant creado para desarrollo es:

```txt
01900000-0000-7000-8000-000000000001
```

Las solicitudes protegidas en `Development` deben incluir identificadores UUID
válidos:

```txt
X-Subject-Id: 01900000-0000-7000-8000-000000000002
X-Tenant-Id: 01900000-0000-7000-8000-000000000001
```

`X-Permissions` es opcional y acepta permisos separados por comas. Si se omite,
el stub concede los cinco permisos de Tenancy implementados:

```txt
X-Permissions: tenancy.settings.read,tenancy.settings.update,advisorship.invite,advisorship.read,advisorship.manage
```

Esto permite simular, por ejemplo, un usuario de solo lectura enviando
únicamente `tenancy.settings.read`.

Fuera de `Development`, la API utiliza JWT Bearer. El token debe contener:

- `sub`: identificador UUID del sujeto;
- `tenant_id`: identificador UUID del tenant;
- uno o más claims `permission`, según la operación.

También deben configurarse un `Authentication:Authority` y un
`Authentication:Audience` válidos.

## Activar y desactivar el modo de desarrollo (auth)

El modo de autenticación está **desacoplado del ambiente**. El interruptor es la
bandera de configuración `Authentication:UseDevelopmentStub`, resuelta en
`AddAuthentication` de
[`QepServiceCollectionExtensions`](src/Bootstrapper/QepServiceCollectionExtensions.cs):

| `Authentication:UseDevelopmentStub` | Esquema activo                                            | Cómo autentica                                                                                                                    |
| ----------------------------------- | --------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `true`                              | Stub por encabezados (`DevelopmentAuthenticationHandler`) | Lee `X-Subject-Id`, `X-Tenant-Id`, `X-Permissions` opcional. Sin proveedor real.                                                  |
| `false` (**por defecto**)           | JWT Bearer (Google, ADR 0014/0015)                        | Valida el token del proveedor; exige `Authentication:Google:ClientId` (o `Authentication:Audience`) y `Authentication:Authority`. |

Si la bandera no se define, su valor por defecto es **`true` solo en el ambiente
`Development`** y `false` en cualquier otro. Esto conserva el stub sin fricción en
las pruebas de integración (que corren en `Development` y no fijan la bandera),
mientras que la aplicación en ejecución usa el proveedor real.

### Ejecutar contra el proveedor real (comportamiento por defecto al iniciar)

Los perfiles `http` y `https` de
[`launchSettings.json`](src/Api/Properties/launchSettings.json) fijan
`Authentication__UseDevelopmentStub=false`, por lo que `dotnet run` autentica
contra Google:

```powershell
dotnet run --project src/Api --launch-profile http
```

Requiere el `Authentication:Google:ClientId` del proveedor. En `Development` se
carga desde los secretos de usuario:

```powershell
dotnet user-secrets set "Authentication:Google:ClientId" "<google-oauth-client-id>" --project src/Api
```

El frontend debe hacer login real con Google (`VITE_DEV_AUTH=false` en su `.env`,
ya configurado).

### Volver al stub por encabezados (para depurar sin Google)

Fije la bandera en `true` al ejecutar:

```powershell
$env:Authentication__UseDevelopmentStub = "true"
dotnet run --project src/Api --launch-profile http
```

> [!IMPORTANT]
> Con el proveedor real, cada solicitud protegida requiere un JWT válido con los
> claims `sub`, `tenant_id` y `permission`; los encabezados `X-*` del stub se
> ignoran.

## Semilla de arranque

Con `Seed:Enabled` en `true`, la aplicación deja el ambiente utilizable al arrancar:
crea el tenant **Origen botánico**, el usuario que lo administra, su membresía y el
catálogo de diecinueve productos con la tasa `IVA 19%`, y le deja configurados el formato de
número de pedido (`PW…`) y las columnas del Excel de pedidos de su ERP (ver
[homologación](#columnas-del-excel-de-pedidos-por-tenant-homologación)). Después crea el
[tenant operador](#tenant-operador) **QCode** (`qcode`, id `01900000-0000-7000-8000-000000000006`)
con todos los módulos del catálogo y una membresía `admin` para `Seed:OperatorOwnerEmail`. Pensada para el ambiente
desplegado durante el desarrollo, donde la base se borra y se vuelve a crear: después
de un borrado no hay ningún paso manual, alcanza con que la aplicación reinicie.

| Clave                          | Por defecto | Qué hace                                                                      |
| ------------------------------ | ----------- | ----------------------------------------------------------------------------- |
| `Seed__Enabled`                | `false`     | Interruptor de la semilla de arranque. Apagado, no se siembra nada            |
| `Seed__OwnerEmail`             | sin valor   | Email que recibe la membresía con rol `admin`                                 |
| `Seed__OperatorOwnerEmail`     | sin valor   | Email de QCode que recibe el `admin` del tenant operador. Sin valor no se crea QCode: sólo se advierte en el log |
| `Seed__ExportLoad__Quotations` | `0`         | Cotizaciones de la [carga sintética](#carga-sintética-para-medir-la-exportación). `0` la apaga |

El usuario se siembra **sólo con su email**, sin proveedor vinculado: el primer login
con Google lo vincula solo, porque `ProviderLinkingService` busca por email verificado.
No hace falta invitación ni registrar un tenant.

> [!WARNING]
> La semilla **crea un tenant y otorga el rol `admin`**. Es un mecanismo que concede
> privilegios, y a diferencia del stub de autenticación no puede negarse a arrancar
> fuera de `Development`, porque el ambiente desplegado corre como `Production`. Su
> única defensa es que nace apagada. **Al entregar el ambiente al cliente hay que
> borrar las dos claves del ConfigMap.**

`Seed__OperatorOwnerEmail` es una clave aparte a propósito y **no** reutiliza `Seed__OwnerEmail`:
en producción ése puede ser el email del cliente, y reutilizarlo lo haría operador de toda la
plataforma. Si viene, tiene que ser un email válido o el arranque falla, salvo que sea un token de
Azure DevOps sin reemplazar (`#{SEED_OPERATOR_OWNER_EMAIL}#`, el que trae el ConfigMap de
producción): ése vale lo mismo que ausente, y la semilla salta QCode con la advertencia `4107`. Si el
slug `qcode` ya es de otro tenant, la semilla no crea QCode, lo advierte con el id de ese tenant y
sigue arrancando.

Es idempotente: el tenant por id, el usuario por email, la membresía por el par
usuario-tenant y los productos por el código de su variante. Cada producto sembrado nace sin
opciones, con una sola variante —la variante por defecto, con el mismo id que el producto— que
lleva el `sku` del JSON como código y los precios, escalas y empaques. Correrla muchas veces —cada reinicio de pod
lo hace— no duplica nada. Tampoco actualiza: cambiar un precio ya sembrado es un `PUT`,
no una segunda corrida.

Tres campos del archivo de origen no se cargan porque el dominio no los tiene: el id de
origen, el peso neto y el peso bruto. La imagen tampoco: en el origen es una ruta, y la galería
del producto guarda archivos de la [biblioteca](#biblioteca-de-archivos-cloudflare-r2), que se
suben aparte. Los cuatro quedan en
[`catalog-products.json`](src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json)
bajo `notSeeded`, como referencia.

Para levantarla en local:

```powershell
$env:Seed__Enabled = "true"
$env:Seed__OwnerEmail = "<tu-email>"
dotnet run --project src/Api --launch-profile http
```

### Carga sintética para medir la exportación

Con `Seed:ExportLoad:Quotations` mayor que 0, la aplicación siembra, **después** de arrancar, el
tenant **Carga de exportación** (`carga-export`). Lleva:

- esa cantidad de cotizaciones repartidas en los últimos 12 meses;
- un cliente cada 25 cotizaciones;
- el 30 % convertidas en pedido;
- el catálogo de la semilla;
- una membresía `admin` para `Seed:OwnerEmail`.

Existe para medir en el pod real cuánto cuesta exportar un año (spec
`docs/superpowers/specs/2026-09-13-ajustes-post-export-design.md`). No depende de `Seed:Enabled`,
pero también exige `Seed:OwnerEmail`.

- **No corre dentro del arranque.** El `startupProbe` le da al pod 180 s (36 intentos cada 5 s), de
  los que un segundo pod gasta una parte esperando el candado de arranque, y sembrar decenas de miles
  de filas ahí haría que Kubernetes lo matara. La siembra termina con la línea
  `Export load seed finished: …` en el log.
- **Va con SQL masivo en una transacción.** No pasa por los handlers, así que la siembra no deja
  outbox, auditoría, correos ni WhatsApp.
- **Los vencimientos sí dejan rastro.** Las cotizaciones sembradas como `Sent` vencen en los días
  siguientes, y cada vencimiento escribe historial y un evento de auditoría del tenant
  `carga-export`: con 50 000 cotizaciones son unas 137 por día. El script de limpieza borra el
  historial, pero no la auditoría ni el outbox, que son un log inmutable. Por eso conviene apagar y
  limpiar apenas termines de medir.
- **Es idempotente.** Si el tenant ya tiene cotizaciones, no siembra. Por eso subir el número después
  de una siembra completa no hace nada: el log sólo dice `skipped`. Para sembrar otra cantidad,
  apágala y despliega, corre la limpieza y vuelve a prenderla con el número nuevo.

Para probarla en local:

```powershell
$env:Seed__ExportLoad__Quotations = "2000"
$env:Seed__OwnerEmail = "<tu-email>"
dotnet run --project src/Api --launch-profile http
```

Cuando termines, quita la variable de esa sesión de PowerShell. Si la dejas, un `dotnet test` que
corras desde la misma sesión siembra en cada host de pruebas que no fije la clave:

```powershell
Remove-Item Env:Seed__ExportLoad__Quotations
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

## Código postal de la entrega

La parte de **envío** de cotizaciones y pedidos tiene un `postalCode` opcional, en Colombia y fuera: se recorta, vacío es
`null` y el tope son 20 caracteres, sin formato por país (spec 2026-11-24).

- **Escritura:** `parties.shipping.postalCode` en las rutas que reciben partes. Omitirlo lo borra, como `regionId`. En
  facturación se descarta. Más largo es `422 quotations.party.postal_code_too_long` con
  `errors["Parties.Shipping.PostalCode"]`.
- **Lectura:** cada parte responde `postalCode` (en facturación siempre `null`), y la cotización y el pedido (`order`)
  traen `postalCodeSupported: true`, que es lo que el frontend mira para pintar el campo.
- La conversión lo copia al pedido, el PDF lo imprime en la ubicación tras la dirección y el envío a Shopify lo manda
  como `zip` (ver [El envío](#integración-con-shopify)). La libreta de direcciones del cliente todavía no lo tiene.

## Regiones y ciudades por país

El selector de ubicación del frontend lee un solo flujo, igual para cualquier país. Colombia guarda `CityId` y deriva su
departamento de la ciudad (nunca guarda una región). El resto de países guarda el nombre de la ciudad como texto
(`CityName`) y, **si el país tiene catálogo de regiones distinto de Colombia (hoy sólo España), también la región**: una
referencia blanda `region_id` a `geography.regions`, sin llave foránea entre esquemas.

- **Qué entidades la guardan:** el domicilio y las direcciones del cliente, la empresa facturadora, el proveedor y las
  partes de facturación y entrega de cotizaciones y pedidos (la copia al convertir también la lleva).
- **Escritura:** `regionId` opcional junto con `country` y `cityName`. Con Colombia (o sin país) es `422`; una región
  inexistente, de otro país o un id de departamento colombiano es `422 <módulo>.<entidad>.region_invalid`. Un `PUT` sin
  `regionId` la borra, igual que `cityName`.
- **Lectura:** el detalle responde `region: { id, name } | null`; las listas y los Excel no cambian. La vista del cliente
  dentro de la cotización y el pedido (`client`, `client.addresses[*]`) también la trae.
- **Datos existentes:** ninguna fila se migra; las anteriores quedan con `region_id = null`.
- El selector sigue leyendo el catálogo de abajo: la región elegida filtra las ciudades y ahora además viaja al guardar.

| Ruta | Respuesta |
| --- | --- |
| `GET /api/v1/countries` | `[{ code, name, regionKind }]`: el catálogo de países por nombre. `regionKind` es `Department` (`CO`), `Province` (`ES`) o `null` si el país no tiene catálogo de regiones: ahí la pantalla cae al texto libre. |
| `GET /api/v1/countries/{country}/regions` | `[{ id, code, name }]` por nombre. País sin catálogo: `200 []`. |
| `GET /api/v1/countries/{country}/regions/{regionId}/cities` | `[{ id, code, name }]` por nombre. Región que no es de ese país: `404 geography.region.not_found`. |

- **Colombia** sale de DIVIPOLA (`departments`, `cities`): los `id` son los mismos que sirven `/api/v1/departments`
  y `/api/v1/cities`, que no cambian.
- **Los demás países** salen de `geography.regions` y `geography.region_cities`, que `RegionSeeder` siembra desde
  `Seed/Data/<país>-regions.json` y `<país>-cities.json`. Un arranque normal no lee esas tablas: compara el SHA-256
  de los dos archivos con el de `geography.seed_state` y, si coincide, termina. Sólo si cambió (primer arranque, o un
  archivo nuevo) parsea, valida y aplica el país en una transacción con `INSERT … ON CONFLICT` sobre `unnest`, sin
  cargar filas ni usar el change tracker. Si cambias la **lógica** del importador y no los archivos, sube
  `RegionSeedHash.SeederVersion`, o las bases que ya coinciden no se enterarán. Hoy sólo España: 52 provincias y 8.132
  municipios de la «Relación de municipios y sus códigos por provincias» del INE (1 de enero de 2025; el artículo
  invertido del INE, «Rozas de Madrid, Las», se guarda como se conoce: «Las Rozas de Madrid»,
  `https://www.ine.es/daco/daco42/codmun/diccionario25.xlsx`). El código del municipio es `CPRO` + `CMUN`.
- **Un país nuevo** son sus dos JSON, una entrada en `RegionSeeder.EmbeddedSources` y otra en `CountryCatalogs.All`
  (una prueba unitaria falla si una de las dos listas se queda atrás).
  Esto no toca `Customer`, Coordinadora, Siigo ni los sinónimos: ninguna ciudad de afuera entra a `geography.cities`.

## Nombres de ciudad de Coordinadora

Las ciudades de `geography` son los municipios del DANE (`localities.json`), y se importan en cada
arranque sin depender de `Seed:Enabled`. En ese mismo arranque, cada ciudad toma además su nombre
como lo escribe la transportadora Coordinadora (`ABEJORRAL (ANT)` en vez de `ABEJORRAL`), que es lo
que lleva la columna `coordinadora_city` del Excel de pedidos. Sale de un snapshot embebido,
[`coordinadora-cities.json`](src/Modules/Geography/Modules.Geography.Infrastructure/Seed/Data/coordinadora-cities.json),
y no de una consulta en vivo: el arranque no puede depender de que `ws.coordinadora.com` responda.

Cada arranque reconcilia contra el archivo: la ciudad que está toma su nombre, y la que no queda
en `null` (104 de los 1122 municipios con el snapshot actual: Coordinadora no los lista o los
lista inactivos). Un código del
snapshot que no sea de ninguna ciudad se ignora al arrancar, pero la prueba unitaria
`EveryCodeOfTheEmbeddedCoordinadoraSnapshotIsASeededMunicipality` lo frena en CI.

Para regenerarlo cuando Coordinadora cambie su lista, desde la raíz del repo:

```powershell
$data = "src\Modules\Geography\Modules.Geography.Infrastructure\Seed\Data"
'{"jsonrpc":"2.0","method":"Cotizador.ciudades","params":{},"id":1}' | Set-Content -Encoding Ascii req.json
curl.exe -s https://ws.coordinadora.com/ags/1.5/server.php -H "Content-Type: application/json" -d "@req.json" -o coordinadora-raw.json
$raw = Get-Content coordinadora-raw.json -Raw -Encoding UTF8 | ConvertFrom-Json
$cities = $raw.result |
    Where-Object { $_.codigo -match '^[0-9]{5}000$' -and $_.estado -eq 'activo' } |
    ForEach-Object { [pscustomobject]@{ code = $_.codigo.Substring(0, 5); name = $_.nombre.Trim() } } |
    Sort-Object code
$entries = $cities | ForEach-Object {
    if ($_.name -match '[\x22\x5C]') { throw "Nombre con comilla o barra invertida: $($_.name)" }
    "  {`n    `"code`": `"$($_.code)`",`n    `"name`": `"$($_.name)`"`n  }"
}
$json = "[`n" + ($entries -join ",`n") + "`n]`n"
[System.IO.File]::WriteAllText("$PWD\$data\coordinadora-cities.json", $json, (New-Object System.Text.UTF8Encoding $false))
Remove-Item req.json, coordinadora-raw.json
$cities.Count
```

El filtro es la regla del snapshot, no un detalle del script:

- **Sólo municipios.** Coordinadora usa códigos de 8 dígitos: los 5 primeros son el código DIVIPOLA
  y el sufijo `000` es el municipio. Lo demás son centros poblados, que `geography` tampoco importa.
- **Sólo `estado` = `activo`.** Los inactivos se excluyen a propósito y su ciudad queda en `null`.
- **Sin México.** Sus códigos empiezan por `MX` y el patrón numérico los deja fuera.

El cuerpo va a archivo porque PowerShell rompe las comillas dobles al pasarlas a `curl.exe`. Se
escribe en UTF-8 sin BOM y con el mismo formato del archivo, así que el diff muestra sólo los
municipios que cambiaron. Después corre `Modules.Geography.UnitTests` antes de commitear.

## Sinónimos de ciudades

Cuando una importación trae un texto de ciudad que no casa con ningún municipio del departamento (`CALLI` en vez de `CALI`),
el pedido entra igual y el texto queda en una bandeja que el operador resuelve (spec `2026-10-26`). Confirmarlo como sinónimo
de una ciudad real hace que **los pedidos siguientes casen solos** y que los ya importados se corrijan. Hoy sólo Shopify
reporta a la bandeja; es el estado de hoy, no una regla: van a reportar más proveedores. Uno nuevo necesita su propio reporter
hacia el puerto de Geography (`IUnresolvedCityReporter`, con su `source`) y su propio consumidor de los eventos
`geography.city_synonym.*` con su worker que corrige, porque los de hoy viven en el módulo de Shopify. Las tablas de Geography ya
llevan `source`. Módulo `geography` (sinónimos y bandeja) y `shopify` (el trabajo que corrige).

**Orden de coincidencia.** Primero el nombre de la ciudad, luego el nombre oficial del DANE y, sólo si ninguno casa, el
sinónimo vivo del departamento (`CityRepository.MatchByNameAsync`). Un texto que ya es el nombre de una ciudad real no se
puede confirmar como sinónimo: 422 `geography.synonym.matches_city`.

**Consola del operador.** Todo bajo `/api/v1/tenants/{tenantId}/operator/cities`, con el tenant operador en la ruta
(`Platform:OperatorTenantId`) y doble capa: la política `operator.cities.read` o `operator.cities.manage` y el handler.

| Ruta | Permiso | Qué hace |
| --- | --- | --- |
| `GET summary` | read | Contadores: pendientes, descartados, sinónimos y pedidos esperando. |
| `GET unresolved`, `GET unresolved/{id}` | read | La bandeja y el detalle, con hasta tres sugerencias (similitud mínima 50). |
| `POST unresolved/{id}/impact` `{ cityId }` | read | Qué pasaría al confirmar; no escribe. |
| `POST unresolved/{id}/resolve` `{ cityId }` | manage | Crea el sinónimo y publica `resolved`. 409 si ya hay uno vivo. |
| `POST unresolved/{id}/discard`, `.../restore` | manage | Saca o devuelve una fila de la bandeja. |
| `GET synonyms` | read | La lista, con los contadores de lo corregido, pendiente y fallido. |
| `POST synonyms/{id}/impact` `{ cityId }` | read | Qué pasaría al corregirlo a otra ciudad. |
| `PUT synonyms/{id}` `{ cityId, expectedVersion }` | manage | Cambia la ciudad y publica `corrected`. 412 si la versión cambió. |
| `DELETE synonyms/{id}` | manage | Borrado lógico: deja de casar y no deshace lo corregido; no publica nada. |
| `POST synonyms/{id}/retry` | manage | Devuelve a `Pending` los trabajos `Failed` del sinónimo. |
| `POST backfill` | manage | Recorre los pedidos ya importados (ver abajo). |

Un `cityId` de otro departamento es 422 `geography.city.not_in_department`; los listados devuelven la `page` pedida, sin
recortarla.

**Eventos.** `resolved` y `corrected` salen por el outbox de plataforma en la misma transacción que el cambio, con el id del
sinónimo como `correlation_id`: `geography.city_synonym.resolved.v1` (con las ocurrencias de la fila: pedido, cliente y si a
cada uno sólo le faltaba la ciudad) y `geography.city_synonym.corrected.v1` (con la ciudad anterior y la nueva). Los nombres
de campo son contrato con Shopify: los escribe `GeographyEventPublisher` y los lee `CitySynonymEventPayloads`.

**Consumidor y worker.** `CitySynonymEventsWorker` llama cada 10 s al consumidor `shopify.city-synonym-events`, que reclama
los mensajes del outbox y siembra los trabajos de `shopify.city_correction_jobs` y marca el inbox en el mismo guardado: un
mensaje procesado nunca queda sin sus trabajos. Un mensaje que falla vuelve a los 5 s, 30 s y 5 minutos, y después cada 5
minutos: nunca se abandona. `resolved` siembra un trabajo por pedido (y uno por cliente que sólo le faltaba la ciudad);
`corrected` siembra una recorrección por cada corrección ya escrita con otra ciudad. `ShopifyCityCorrectionWorker` también
corre cada 10 s: toma lotes de 50 trabajos, los reclama uno a uno (un solo ganador entre réplicas) y los procesa cada uno en su
scope. Un trabajo aplica la **ciudad actual** del sinónimo, no la del evento; si el sinónimo ya se borró, o el pedido ya no es
editable (abierto, sin preparar y sin facturar; spec 2026-11-10), o ya tiene parte de envío, el trabajo termina `Skipped` con su
motivo (`synonym_deleted`, `order_not_pending` —el motivo persistido conserva su nombre—, `shipping_present`, …). Lo que lanza se reintenta al vencer su lease (10 s, 1, 5, 15 y 30 minutos); al quinto intento queda
`Failed` con la causa. La corrección escribe en Quotations y Customers, deja su huella en `shopify.city_corrections` y una
auditoría (`shopify.order.city_resolved`, `shopify.customer.city_resolved` y las `city_corrected` de la recorrección) en el tenant del pedido.

**Reintentar lo fallido.** El contador `failedCorrections` de `GET synonyms` dice cuántos hay. `POST synonyms/{id}/retry`
los devuelve a `Pending` con los intentos en cero y el worker los toma en su siguiente pasada; el endpoint devuelve el sinónimo
con los contadores frescos.

**Recorrido de la historia.** `POST backfill` revisa los pedidos de Shopify ya importados que siguen editables (abiertos, sin
preparar y sin facturar), sin parte de envío y con la dirección cruda en las notas, y reporta a la bandeja los que hoy no casan. Responde
`{ scanned, reported, alreadyReported, nowMatching, skipped }`. Es idempotente: un texto ya reportado cuenta como
`alreadyReported`, y correrlo otra vez es seguro.

Si el texto de un pedido ya casa por un **sinónimo vivo** (no por el nombre real de la ciudad), cuenta como `nowMatching` y además
siembra el trabajo `Resolve` de ese pedido, sin operador ni mensaje de origen: el evento de la confirmación no traía ese pedido y
nadie más lo iba a corregir. La siembra usa la misma regla del evento y del importador (`ResolveJobs`) y no repite un trabajo del
mismo (sinónimo, pedido), aunque el anterior siga pendiente. Si casa por el nombre real no se siembra nada: el pedido no necesita
un sinónimo.

El recorrido es **síncrono** y revisa cada fila `Imported` del espejo de todos los tenants: con un espejo grande puede toparse con
el 524 de Cloudflare. Córrelo una vez, justo después de desplegar.

### Riesgos conocidos

Decisiones aceptadas al cerrar el trabajo; ninguna se arregla sola.

- **Lease solapado y huella de cliente.** Si dos pasadas del worker se solapan sobre el mismo trabajo en una intercalación
  estrecha, se puede perder la huella de un cliente y su auditoría. La mitigación (registrar la ciudad pedida antes de leer el
  sinónimo, o releer `requested_city_ids` y reintentar una vez ante `NotEligible`) **no está implementada**. Si un cliente
  completado no tiene huella, no entra en «corregido» al cambiar el sinónimo: se corrige a mano.
- **Auditoría del pedido.** La entrada de una corrección de pedido lleva `resource_type = "order"`, no `"quotation"` como las
  auditorías de pedidos del propio Quotations: un filtro por tipo de recurso no las junta.
- **Reintento tras un commit entre módulos.** El worker escribe en Quotations y Customers en su propia unidad de trabajo y
  después guarda su fila. Si muere en medio, el reintento puede encontrar el pedido ya no editable y terminar
  `Skipped` con `order_not_pending` aunque la ciudad ya esté escrita. Es no atomicidad inherente a que sean módulos distintos.
- **`ShopifyClaimedOutboxConsumer` y `ShopifyInbox` son copia literal de las de Pipeline.** Un arreglo a la lógica de reclamo
  va en los dos sitios.
- **Impacto sin tope.** `POST synonyms/{id}/impact` lee todas las huellas del sinónimo, y la vista previa de `resolve` y su
  evento leen todas las ocurrencias de la fila, porque el contrato exige la lista completa de `ordersNotPending`. Es un límite
  conocido al volumen de hoy.
- **El recorrido no completa clientes.** `POST backfill` nunca siembra un cliente: la importación reutiliza clientes
  incompletos que ya existían (por ejemplo, uno nacido en WhatsApp, con versión 1), y un sinónimo no debe completarlos. El
  tenant los completa a mano.
- **Auditoría del recorrido y del reintento.** La del recorrido se escribe sólo si todo el recorrido termina bien. Y en
  `POST synonyms/{id}/retry` los trabajos `Failed` vuelven a `Pending` en una sentencia que se confirma sola, antes de guardar la
  auditoría: si ésta falla, los trabajos ya se reintentan sin su rastro.

## Numeración de documentos por tenant

El número de cotización y el de pedido salen de un formato **por tenant y por tipo de documento**,
guardado en `quotations.document_numbering_formats`. Sin fila para ese tenant, el formato es el de
siempre y no hay nada que hacer: `QUO-2026-0001` y `PED-2026-0001`.

Se configura con SQL, no con un endpoint ni una pantalla: la numeración es una decisión de negocio
que se toma al montar al cliente, y el permiso más cercano (`platform.*`) lo tiene el `admin` **del
tenant**, o sea el admin del cliente. Spec:
[`docs/superpowers/specs/2026-09-17-numeracion-configurable-design.md`](docs/superpowers/specs/2026-09-17-numeracion-configurable-design.md).

| Columna          | Qué vale                                                        |
| ---------------- | --------------------------------------------------------------- |
| `tenant_id`      | El tenant. Junto a `document_type` es la clave primaria          |
| `document_type`  | `order` o `quotation`                                           |
| `prefix`         | De 0 a 10 caracteres en `[A-Za-z0-9-]`. El guion va en el prefijo, si lo quieres |
| `include_year`   | `true` o `false`                                                 |
| `year_separator` | `''`, `-` o `/`. Sólo se usa si `include_year`                   |
| `min_digits`     | De 1 a 10. Relleno con ceros a la izquierda; es un mínimo, no un ancho |

| Caso                       | `prefix` | `include_year` | `year_separator` | `min_digits` | Resultado        |
| -------------------------- | -------- | -------------- | ---------------- | ------------ | ---------------- |
| Default actual (sin fila)  | `PED-`   | `true`         | `-`              | `4`          | `PED-2026-0001`  |
| Cliente que trae su serie  | `PW`     | `false`        | `''`             | `1`          | `PW234235`       |
| Con año y seis dígitos     | `PW-`    | `true`         | `-`              | `6`          | `PW-2026-000007` |

Los cuatro rangos son `CHECK` en la base: un `min_digits` de 11 o un `document_type` inventado no
entran, sin importar quién corra el SQL.

### Configurar un tenant

Los cuerpos van a archivo y se mandan con `-f`: PowerShell rompe las comillas al pasar SQL largo por
`-c`. La contraseña la pide `psql`; **no la saques con `kubectl get secret`, que imprime los valores**
(ver [Reglas duras](CLAUDE.md)).

1. **El formato.** Guarda esto como `numeracion.sql`, con el tenant y los valores que correspondan:

   ```sql
   \set tenant_id '00000000-0000-0000-0000-000000000000'

   INSERT INTO quotations.document_numbering_formats
          (tenant_id, document_type, prefix, include_year, year_separator, min_digits)
   VALUES (:'tenant_id', 'order', 'PW', false, '', 1)
   ON CONFLICT (tenant_id, document_type) DO UPDATE
   SET prefix = EXCLUDED.prefix, include_year = EXCLUDED.include_year,
       year_separator = EXCLUDED.year_separator, min_digits = EXCLUDED.min_digits;
   ```

   Repite el `INSERT` con `'quotation'` si también quieres cambiar las cotizaciones: son dos filas
   independientes, y configurar pedidos no toca cotizaciones.

2. **El consecutivo, si el cliente viene de otro sistema.** Va en el mismo `numeracion.sql` del
   paso 1, después del `INSERT` del formato, reutilizando su `\set tenant_id`. `:siguiente_numero`
   es **el próximo número que quieres que salga**, no el último que emitió el sistema viejo.
   `year = 0` es la fila del formato **sin** año; con año va el año (`2026`).

   ```sql
   INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
   VALUES (:'tenant_id', 0, 234235)
   ON CONFLICT (tenant_id, year) DO UPDATE
   SET next_value = GREATEST(quotations.order_number_counters.next_value, EXCLUDED.next_value);
   ```

   El `GREATEST` es la regla «el consecutivo sólo avanza» en una línea: correr el mismo SQL dos
   veces, o con un número menor, **no** retrocede el contador. Un número repetido chocaría contra
   `IX_orders_tenant_number` y el alta fallaría. La tabla de cotizaciones es
   `quotations.quotation_number_counters`, con las mismas columnas.

3. **Correrlo y verificar.**

   ```powershell
   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f numeracion.sql
   psql -h <host> -p <puerto> -U <usuario> -d <base> -c "SELECT * FROM quotations.document_numbering_formats WHERE tenant_id = '<tenant-id>'"
   psql -h <host> -p <puerto> -U <usuario> -d <base> -c "SELECT * FROM quotations.order_number_counters WHERE tenant_id = '<tenant-id>'"
   ```

   No hace falta reiniciar el pod: el formato se lee en cada emisión, así que el siguiente documento
   ya sale con el nuevo.

### Qué pasa cuando cambias un formato a mitad de camino

- **Cambiar el prefijo no reinicia nada.** El contador no depende del prefijo: pasar de `PED-` a
  `PW` deja la serie donde iba.
- **Pasar de con año a sin año (o al revés) cambia de fila de contador**, porque una es la del año y
  la otra es la de `year = 0`. Por eso el paso 2 existe: fija el siguiente número de la fila nueva.
- **Lo ya emitido no se reescribe.** No hay backfill ni renumeración: el formato nuevo aplica a lo
  que se emita de aquí en adelante.
- **Si el número no cabe en 20 caracteres**, la emisión falla con `422` y el código
  `order.order.number_too_long` o `quotation.quotation.number_too_long`. Ese consecutivo se pierde y
  queda un hueco en la serie. Diez de prefijo, más el año con separador, más diez dígitos no caben:
  haz la cuenta antes de configurar.

## Redondeo de precios

Cada tenant decide cómo se redondea su lista de precios, moneda por moneda (spec 2026-10-27). La
fórmula de la línea es **una sola** para todas las monedas y todos los tenants, el unitario primero:

```
lista     = R(precio de lista)
unitario  = R(lista × (1 − descuento/100))
línea     = Round2(cantidad × unitario)               // lo que se cobra, IVA adentro
descuento = Round2(cantidad × lista) − línea          // con IVA: lo guarda el POS
IVA       = Round2(línea × tasa / (100 + tasa))       // sólo COP discrimina IVA
base      = línea − IVA
```

`R` es la regla de la moneda: `(decimales, modo)`. El modo es `Nearest` (al más cercano, la mitad se
aleja de cero), `Up` (hacia arriba) o `Down` (hacia abajo). Los decimales van de `0` a los del
catálogo de monedas (COP 0, USD 2, EUR 2). **Por defecto**, una moneda sin configurar usa
`(decimales del catálogo, Nearest)`: para COP es exactamente lo de antes. Ejemplo de aceptación: USD
`(2, Up)`, 13,19 con 15 % da unitario 11,22; con 10 unidades, línea 112,20 y descuento 19,70.

**El descuento de cotizaciones y pedidos se guarda sin IVA**, igual que el subtotal de la línea. Con
`bruto = Round2(cantidad × lista cobrada)` y `t` la tasa de IVA de la línea:

```
descuento = (bruto − Round2(bruto × t / (100 + t))) − base     // base = Subtotal de la línea, sin IVA
```

El POS conserva el descuento con IVA de la fórmula de arriba.

La regla aplica sólo al precio con IVA incluido. Los valores sin IVA (los que lee el Excel del ERP),
los totales del encabezado y los del POS siguen a 2 decimales, al más cercano.

### Configurarla

`GET` y `PUT /api/v1/tenants/{tenantId}/settings/price-rounding`, con `SettingsRead` y
`SettingsUpdate`. La respuesta trae **todas** las monedas del catálogo, en su orden, también las que
usan el valor por defecto (`isDefault`), con `maxDecimals` ya resuelto. La sesión
(`ActiveTenantResponse.PriceRoundings`) la lleva igual. Sólo se guarda una fila por moneda no
predeterminada (`tenancy.tenant_price_roundings`); el cambio se audita como
`tenancy.price_rounding.updated`.

```json
{
  "currencies": [
    { "currency": "COP", "decimals": 0, "mode": "Nearest" },
    { "currency": "USD", "decimals": 2, "mode": "Up" }
  ]
}
```

### Qué documentos se recalculan y cuándo

- **Editables:** cotización `Draft`/`Sent` y pedido editable (`Open`, sin preparar y sin facturar)
  se recalculan con la regla vigente la próxima vez que alguien los edita. Abrirlos no los recalcula:
  ningún `GET` escribe, y no hay recálculo masivo al guardar la regla.
- **Lo demás no cambia:** cotización `Converted`, `Voided` o `Expired`; pedido `Archived`, `Cancelled`,
  preparado o facturado; venta de POS cerrada. Cada documento guarda con qué regla se calculó (dos
  columnas nullable; `null` cuenta como el valor por defecto de su moneda).
- **Conversión:** el pedido hereda la regla guardada de la cotización.
- **Pedidos externos (Shopify):** usan siempre el valor por defecto de su moneda, aun editados.
- **POS:** lee la regla en cada vista previa y en cada venta. Si cambia entre la vista previa y la
  venta, la venta responde `pos.sale.payment_insufficient` y el carrito se refresca.

### Riesgo con Siigo

La facturación masiva de Ritual manda a Siigo `round("Valor Unit" × (1 + IVA)) × cantidad`. Con COP en
`(0, Nearest)` QEP y Siigo cuadran. Si un admin pone COP en `Up` o `Down`, pueden diferir en un peso
por unidad.

## Módulos por tenant

Cada tenant tiene prendidos los módulos comerciales que contrató, en `tenancy.tenant_modules`: la
fila **activa** (`status = 'active'`) es el módulo. Una fila `inactive` es un módulo apagado que
conserva quién lo prendió y desde cuándo (`source`, `enabled_at`, `status_changed_at`). Las claves son
`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`, `pos`, `messaging`, `pipeline`, `shopify`, `automations`, `suppliers`, `billing`, `developer`, `loans`, `store`, `ai`, `support`, `purchases` y `wholesale`; `quotations` exige
`catalog`, `customers` y `companies`, `orders` exige lo mismo (no `quotations`: un pedido nace sin cotización, así que la consola de plataforma puede ocultar cotizaciones y el tenant sigue vendiendo por pedidos; la configuración de envíos, el contenido del PDF, los canales de WhatsApp y Zenvia los comparten ambos módulos y quedan disponibles con cualquiera de los dos), `pos` exige `catalog` y
`companies`, `shopify` exige `orders` y `automations` exige `messaging`, `loans` exige `customers`, `store` exige `orders` y `ai` exige `messaging`. `messaging` (la bandeja de WhatsApp, sin dependencias) no viene con el signup y se prende por tenant desde la consola de operador o con el SQL de respaldo. `pipeline` (el embudo de ventas) exige `customers`, no viene con el signup y se prende igual. `automations` (el constructor de chatbots) no viene con el signup y se prende por tenant desde la consola de operador. `suppliers` (el directorio de proveedores; sin dependencias) tampoco viene con el signup y se prende igual. `developer` (las claves de API de integración; sin dependencias) tampoco viene con el signup y se prende por tenant; a diferencia de los demás, su guarda falla cerrada: un tenant sin fila no pasa, ni siquiera con el stub de desarrollo. `support` (los tickets y PQRSF, [Soporte](#soporte); sin dependencias) tampoco viene con el signup y se prende igual. `purchases` (las órdenes de compra a proveedores) exige `suppliers` y `catalog`; no viene con el signup y se prende por tenant. `wholesale` (el mayorista: listas de precios y distribuidores invitados, ver [Mayorista](#mayorista-listas-de-precios-distribuidores-e-invitación)) exige `orders`; no viene con el signup y se prende por tenant. Sin `messaging`, `pipeline` no ofrece triage; sin `quotations`, no le llegan eventos. Un módulo sin su dependencia cuenta como apagado. Identidad, Tenancy, Authorization,
Storage, Platform, Geography, Audit, Notifications e Integrations son núcleo y no se apagan;
Integrations filtra su catálogo de proveedores por los módulos activos del tenant.

`developer` (las claves de API del tenant y la [API externa](#api-externa)) no viene con el signup: se prende por tenant desde la consola de
operador. Apagarlo hace que la API externa responda `403 tenancy.module_not_enabled` en el request siguiente y que la pantalla de
claves deje de estar disponible; las claves conservan su estado y vuelven a funcionar al prenderlo. Lo mismo vale para `messaging`, que
la API externa exige además.

Apagar un módulo descarta sus permisos en el request siguiente, por cookie y por el stub de
desarrollo (este último sólo cuando el tenant existe en `tenancy.tenants`). No borra datos ni corta
trabajos en vuelo. La SPA lo lee de `GET /api/v1/tenants/{tenantId}/modules` (autenticado, sin
permiso), que siempre devuelve todos los del catálogo con `enabled`, `contracted` y `missingDependencies`, y se
entera en hasta 5 minutos.

Un tenant del signup nace con los siete sin `pos`, `messaging` ni `pipeline` (los seis base más `shopify`) mientras `Entitlements:GrantDefaultModulesOnSignup`
esté en `true` (el default), y sin ninguno en `false`. El de la semilla nace con todos los del catálogo. Ni el
signup ni la semilla escriben historial: el origen queda en `source`.

### Consola de operador

La vía normal para prender y apagar módulos, y para inactivar o reactivar un tenant, es la **consola
de operador**: la sección «Plataforma» de la SPA, que sólo aparece para el tenant configurado en
`Platform:OperatorTenantId` (QCode). Cada operación queda en `tenancy.tenant_changes` con motivo,
nota opcional, quién y cuándo, agrupada por lote; el historial no se edita ni se borra. Un lote que
dejaría un módulo activo sin una dependencia se rechaza con
`422 tenancy.modules.inconsistent_dependencies`: la consola arma la cascada sola.

**Proveedores por tenant.** Un proveedor de Integraciones que exige habilitación (hoy sólo `zenvia`)
también lo prende y apaga la consola, con `POST …/operator/tenants/{targetTenantId}/providers/changes`
y el cuerpo `{ "provider": "zenvia", "status": "active" | "inactive", "reason": "…", "note": "…" | null }`.
Es uno por pedido y no un lote, porque un proveedor no tiene dependencias. Responde 200 con el detalle del
tenant, pide `operator.modules.manage` (no hay permiso propio) y acepta los mismos motivos que los módulos
según la dirección: al habilitar, `contract`, `courtesy` o `correction`; al deshabilitar, `nonpayment`,
`cancellation` o `correction`. Los errores de dominio son `422 tenancy.providers.reason_not_allowed`,
`422 tenancy.providers.no_changes` (habilitar lo ya activo, o deshabilitar lo inactivo o sin fila) y
`404 tenancy.tenant.not_found`; una clave de proveedor, dirección o motivo desconocidos son 422 de
validación. El estado vive en `tenancy.tenant_providers`: un tenant nuevo **no tiene fila** y por tanto
nace con Zenvia deshabilitado. La migración `AddTenantProviders` habilitó Zenvia a los tenants que ya
tenían una conexión de Zenvia (en cualquier estado), sin escribir historial.

El detalle (`GET …/operator/tenants/{id}`) trae `providers: [{ key, status, since, lastReason }]`, siempre
todos los del catálogo aunque no tengan fila: `status` es `active`, `inactive` o `none` (sin fila).
En `history`, un lote de proveedor llega con `kind: "provider"` y sus cambios llevan `providerKey` (con
`moduleKey: null`); `?module=` los deja fuera.

**Inactivar un tenant** lo saca del selector de sesión y todo request a ese tenant recibe 403,
incluso con la sesión ya abierta. No toca sus módulos: reactivarlo deja todo como estaba. Los
workers, el outbox y los enlaces públicos (PDF, comprobantes) de un tenant inactivo **siguen
funcionando** (DECISIÓN-PENDIENTE del spec 2026-10-08). El tenant operador no se puede inactivar.

**Reporte de uso.** `GET …/operator/usage?period=day|week|month` (mes por defecto; otro valor es `422 validation.failed` con la clave `Period`)
pide `operator.usage.read` y sólo responde en el tenant operador (otro tenant recibe 403). Devuelve `registered` (`day`, `week` y `month`, siempre
los tres), el `period` normalizado y una fila por tenant con sus conversaciones del periodo (`whatsApp`, `instagram`, `total`) y los
`connected` ahora mismo. Una conversación cuenta una vez por canal si tuvo al menos un mensaje entrante en vivo (no importado) desde el inicio del
periodo, con la semana desde el lunes y el día y el mes en hora de Bogotá; un tenant sin actividad aparece en cero y el operador no aparece.
Los conectados salen de la presencia del hub de la bandeja. Con `Valkey:Areas:Presence` prendido es compartida entre
pods (`ValkeyMessagingPresence`); apagado es por pod, y con más de una réplica el reporte ve sólo a los conectados al pod
que lo atiende.

### Tenant operador

El tenant operador es **QCode**, con id fijo `01900000-0000-7000-8000-000000000006` (slug `qcode`).
Lo crea la [semilla de arranque](#semilla-de-arranque) cuando tiene `Seed:OperatorOwnerEmail`, y
`appsettings.json` trae ese mismo id como valor por defecto de `Platform:OperatorTenantId`:

- Mientras QCode no esté sembrado, el id por defecto apunta a un tenant que no existe. Es
  inofensivo: nadie tiene membresía ahí, así que la consola no es alcanzable y el resto de la API
  funciona igual.
- Vaciar la clave (`Platform__OperatorTenantId: ""`) apaga la consola: los endpoints `/operator/*`
  responden 403 a todos. En `Production`, además, se registra una advertencia al arrancar.
- `Guid.Empty` tumba el arranque (`OperatorTenantOptionsValidator` con `ValidateOnStart`).
- El email llega por la variable del pipeline: el ConfigMap trae
  `Seed__OperatorOwnerEmail: "#{SEED_OPERATOR_OWNER_EMAIL}#"`, que Azure DevOps reemplaza al
  desplegar. Mientras la variable no exista, el token llega literal y vale lo mismo que ausente:
  `SeedOptionsValidator` no tumba el arranque y la semilla salta QCode con la advertencia `4107`.
  Sólo un token completo (`#{` + mayúsculas, dígitos o `_` + `}#`) cuenta así; cualquier otro valor
  que no sea email sí tumba el arranque.

> [!WARNING]
> `Seed:OperatorOwnerEmail` **tiene que ser una dirección interna de QCode que no use nadie más**.
> Quien la tenga administra toda la plataforma: ve todos los tenants, les prende y apaga módulos y
> los puede inactivar. Por eso la semilla se niega a sembrar QCode —y lo advierte en el log, sin
> tumbar el arranque— si el email es el mismo de `Seed:OwnerEmail` (advertencia `4104`) o si ya es
> de un usuario con membresía en otro tenant (advertencia `4105`). Si dos pods siembran a la vez,
> el que pierde la carrera lo registra (`4106`) y sigue arrancando.

En `Development`, con el [stub de autenticación](#activar-y-desactivar-el-modo-de-desarrollo-auth)
prendido, se puede actuar como operador sin sembrar nada: `X-Tenant-Id:
01900000-0000-7000-8000-000000000006` más `X-Permissions` con los `operator.*` que hagan falta
(`operator.tenants.read,operator.modules.manage,operator.tenants.manage`). El filtro de operador
sólo los conserva en ese tenant. El stub existe únicamente en `Development`: fuera de ahí la API se
niega a arrancar con él.

Para habilitarla en producción:

1. Desplegar el backend antes que el frontend. Sin la variable del pipeline, el despliegue no
   cambia nada visible: la semilla sólo advierte en cada arranque que el token no se reemplazó
   (`4107`).
2. Crear en Azure DevOps la variable del pipeline `SEED_OPERATOR_OWNER_EMAIL` con un email interno
   de QCode y volver a desplegar. El ConfigMap ya la referencia y ya trae `Seed__Enabled: "true"`;
   la semilla crea QCode con el id por defecto, así que no hace falta tocar
   `Platform__OperatorTenantId`.
3. Entrar con Google con ese email: el primer login vincula el usuario sembrado. El `admin` de QCode
   ve «Plataforma» (puede requerir recargar la SPA).

Si QCode ya existía con slug `qcode` —registrado por el signup—, el log del arranque lo dice con la
advertencia `4103` y el id de ese tenant. **Antes de apuntar la consola ahí, verifica quién es el
dueño de ese tenant**: cualquiera puede registrarse con el slug `qcode`, y el admin del tenant al que
apunte `Platform__OperatorTenantId` recibe poder sobre toda la plataforma. Si es el de QCode, en vez
del paso 2 agrega `Platform__OperatorTenantId: "<ese id>"` al ConfigMap.

**No uses la consola hasta que termine el rolling update de `AddOperatorConsole`.** Un pod viejo
cuenta como contratada cualquier fila, también una `inactive`, e ignora el estado del tenant: un
módulo apagado o un tenant inactivado mientras quedan pods viejos sigue disponible en los requests
que ellos atiendan.

Rollback: el `Down` de `AddOperatorConsole` borra las filas `inactive` (en el modelo anterior,
apagado = sin fila), pasa `source = 'operator'` a `'manual'` y **devuelve el acceso a los tenants
`Suspended`**, porque el código anterior ignora el estado del tenant.

### SQL de respaldo

El SQL queda para cuando la consola no está disponible. **No deja historial ni auditoría**: lo que
se cambie por aquí no aparece en el historial de la consola. Tampoco revisa dependencias: apagar
`customers` con `quotations` activo es posible, y `quotations` cuenta como apagado hasta que se
repare. Local, sin leer el connection string:

```powershell
# Ver los módulos de un tenant
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT module_key, status, status_changed_at, source, enabled_at, note FROM tenancy.tenant_modules m JOIN tenancy.tenants t ON t.id = m.tenant_id WHERE t.slug = 'origen-botanico' ORDER BY module_key;"

# Prender pos (no deja historial)
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note, status, status_changed_at) SELECT id, 'pos', now(), 'manual', 'Activado por QCode', 'active', now() FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now() WHERE tenancy.tenant_modules.status <> 'active';"

# Apagar orders (no deja historial)
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "UPDATE tenancy.tenant_modules m SET status = 'inactive', status_changed_at = now() FROM tenancy.tenants t WHERE t.id = m.tenant_id AND t.slug = 'origen-botanico' AND m.module_key = 'orders' AND m.status = 'active';"
```

**Después de desplegar `AddTenantModules`** (checklist del despliegue): un pod viejo puede crear
tenants sin filas durante el rolling update. La consulta sólo lista —un tenant sin filas también puede
ser legítimo— y la reparación se hace por slug, después de mirar cada uno:

```powershell
# Tenants sin módulos creados en el último día
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT t.id, t.slug, t.created_at FROM tenancy.tenants t WHERE NOT EXISTS (SELECT 1 FROM tenancy.tenant_modules m WHERE m.tenant_id = t.id) AND t.created_at >= now() - interval '1 day' ORDER BY t.created_at;"

# Reparar uno: los siete del signup
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT t.id, m.key, now(), 'manual', 'Alta durante el despliegue de AddTenantModules' FROM tenancy.tenants t CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting'),('shopify')) AS m(key) WHERE t.slug = 'slug-del-tenant' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
```

En producción, las mismas sentencias por el acceso a la base que QCode ya usa para operaciones
manuales. Efecto inmediato, sin reiniciar la API.

## Tienda pública

Cada tenant puede publicar una tienda en `{slug}.lulocrm.com` (spec `docs/superpowers/specs/2026-11-03-tienda-publica-design.md`,
plan `docs/superpowers/plans/2026-11-03-tienda-publica-backend.md`). Es el módulo `store`: exige `orders`, no viene con el signup y se
prende por tenant desde la consola de operador. Sin él, los ajustes y la vista previa de colores responden un `403` pelado, porque la
máscara de permisos del módulo apagado ya los descarta antes, y las rutas públicas responden `404 store.public.not_found`.

- **Backoffice:** `/api/v1/tenants/{tenantId}/store/settings` (ajustes, publicar y despublicar, vista previa de colores y cuentas de
  cobro). Todo pide `store.settings.manage` (sólo `admin` por defecto). No hay bandeja de solicitudes: el pedido nace en el checkout
  (spec `docs/superpowers/specs/2026-11-09-pedidos-tienda-directos-design.md`).
- **Ajustes, asesor de los pedidos:** `PUT /settings` acepta `advisorMembershipId` (opcional; `null` lo quita) y `GET /settings` lo
  devuelve junto con `advisorName`, el nombre visible de esa membresía. Cuando cambia, tiene que ser una membresía **activa del tenant**;
  si no, `422 store.settings.advisor_not_found` (reenviar el mismo valor no lo revalida, para no bloquear los demás ajustes si esa
  persona dejó el tenant). No se exige un permiso concreto al asesor: el pedido lo crea el sistema. No bloquea la
  publicación.
- **Ajustes, mostrar precios:** `PUT /settings` acepta `showPrices` (opcional; ausente o `null` conserva el valor guardado, a propósito,
  para no apagar precios con un frontend viejo); `GET /settings` y `GET /api/v1/public/stores/{host}` lo devuelven siempre. Es sólo
  visual: el pedido se precia con `pricingMode` y los demás endpoints públicos siguen devolviendo montos. Un cambio puede tardar hasta la
  caché pública (`max-age=60`, `stale-while-revalidate=300`) en verse.
- **Público:** `/api/v1/public/stores/{host}` (`GET /`, `/categories`, `/products`, `/products/{id}`, `/geography/departments`,
  `/geography/departments/{id}/cities`, `POST /cart/quote` y `POST /order-requests`). No lee la cookie de sesión ni pide
  `X-Qep-Client`: el que llama es el servidor del storefront (Astro), con `X-Qep-Storefront-Key` y la IP del visitante en
  `X-Qep-Visitor-Ip`. Los límites son por IP del visitante: 300 lecturas por minuto y 5 envíos cada 10 minutos, más un tope de 500
  pedidos de la tienda por tenant en 24 horas, anulados incluidos (`Store:DailyRequestCap`).
- **El pedido:** `POST /order-requests` crea directamente un pedido con origen `Store`, registrado como creado por el sistema. El asesor
  es el configurado en los ajustes si su membresía sigue activa; si no (nunca se configuró, o lo suspendieron o quitaron), el owner del
  tenant. El cliente se busca por documento: si existe se completa lo que tenga en blanco; si no, se crea completo o, si Customers lo
  rechaza, incompleto. El recibo trae el id y el número del pedido (el consecutivo de pedidos del tenant). El detalle
  (`GET /orders/{orderId}`) ya no trae `storeRequest`; `GET /orders?source=Store` los filtra. El consentimiento del visitante queda en
  `store.order_consents`, una fila por pedido.
- **Moneda y empresa (spec 2026-11-17 §4.3):** cada moneda de la tienda elige la empresa emisora; la cuenta bancaria es opcional.
  En `PUT /settings`, banco y número de cuenta van los dos o ninguno (`422 validation.failed` con un solo). Sin cuenta, el pedido sale
  en la moneda de la tienda con `billingAccount.bankName` y `accountNumber` en `null`, y la tienda no muestra instrucciones de pago.
  `GET /settings/billing-accounts` ofrece, además de cada cuenta, una fila por empresa activa sin cuenta (`bankName`, `accountNumber`
  y `currency` en `null`). Si la empresa no cobra IVA, el checkout cobra el precio de lista (líneas al 0 %).

### Secretos

Los dos son obligatorios **sólo en `Production`**, como los demás validadores del repositorio: sin ellos el arranque falla, y como
el arranque es de toda la API, **un secreto faltante en Production detiene la API entera, no sólo la tienda**. Fuera de Production, un
secreto ausente no impide arrancar: la tienda queda cerrada en ejecución (401 sin clave del storefront, 503 `store.captcha.unavailable`
sin clave de Turnstile). La clave del storefront, cuando está, se valida siempre y necesita al menos 32 caracteres. Van en user-secrets en local y en el almacén de secretos del despliegue, nunca en
`appsettings`:

```powershell
dotnet user-secrets set "Store:Storefront:SharedSecret" "<el mismo valor que el storefront>" --project src/Api
dotnet user-secrets set "Store:Turnstile:SecretKey" "<secret key de Cloudflare Turnstile>" --project src/Api
```

Para comprobar que existen sin verlos: `dotnet user-secrets list --project src/Api | Select-String -Pattern "Store:" | Measure-Object`.
Una clave del storefront vacía o en blanco no abre la tienda: toda llamada pública responde 401. `Store:Turnstile:Enabled=false` sólo se
acepta en `Development` y `Testing` (en Staging el arranque falla, para que no corra con un captcha que acepta cualquier token).

### Códigos de error de las rutas públicas

| Status | `code` | Cuándo |
| --- | --- | --- |
| 400 | `store.public.body_invalid` | El cuerpo de `POST /order-requests` no es JSON o llegó cortado. |
| 401 | `store.public.client_unauthorized` | Falta `X-Qep-Storefront-Key` o no coincide. |
| 404 | `store.public.not_found` | No hay tienda publicada en ese host. En un `GET` sale con `Cache-Control: public, max-age=60` (el max-age del catálogo, sin `stale-while-revalidate`); el resto de los errores no se cachea. |
| 404 | `store.public.product_not_found` / `store.public.department_not_found` | El producto o el departamento no existe o no se publica. |
| 413 | `store.public.body_too_large` | El cuerpo de `POST /order-requests` pasa de 64 KB. |
| 422 | `validation.failed` | Un campo no cumple su forma; el detalle viaja en `errors`. |
| 422 | `store.public.currency_invalid` | Falta `currency` o no es una moneda de la tienda. |
| 422 | `store.public.visitor_ip_required` | Un envío sin `X-Qep-Visitor-Ip` legible. |
| 422 | `store.order_request.consent_required` | No se aceptó la política de datos. |
| 422 | `store.order_request.captcha_invalid` | Turnstile rechazó el token. |
| 422 | `store.order_request.country_not_sold` | El país de facturación o envío no es de venta. |
| 422 | `store.order_request.city_invalid` | La ciudad no cumple su carril (ids en Colombia, texto afuera). |
| 422 | `store.order_request.phone_invalid` | El teléfono no se normaliza en el país de su bloque. |
| 422 | `store.order_request.line_unavailable` | Una línea del carrito ya no se puede vender. |
| 422 | `store.order_request.currency_not_in_store` | La tienda ya no tiene cuenta de cobro en esa moneda. |
| 429 | `store.order_request.daily_cap` | La tienda llegó a su tope diario de pedidos. |
| 429 | (sin cuerpo, con `Retry-After`) | La IP del visitante superó su límite. |
| 503 | `store.captcha.unavailable` | Turnstile no responde o su configuración está rota. |

Reenviar un envío con el mismo `clientRequestId` no es un error: responde 201 con el mismo pedido (la clave del pedido es
`store-checkout:{clientRequestId}`, única por tenant), sin volver a pedir captcha. Los códigos conservan el prefijo
`store.order_request.*` aunque ya no haya solicitud: renombrarlos obligaría a desplegar storefront y backend juntos.

En el backoffice, `PUT /settings` suma `422 store.settings.advisor_not_found` (ver «Ajustes, asesor de los pedidos»).

### Despliegue

**El backend va antes que el frontend y el storefront.** El módulo nace apagado en todos los tenants y las direcciones y partes de afuera
de Colombia no existen hasta que la tienda las crea. Los pedidos directos (spec del 2026-11-09) **sí rompen**: las migraciones
`DropStoreOrderRequests` (Store) y `RenameProcessedSignupOrderId` (Buyers) descartan la bandeja y sus solicitudes, y el frontend viejo
responde 404 en `/orders/store` hasta que se despliegue el suyo. El storefront viejo sigue andando: misma ruta y misma forma del recibo.
Orden: (1) backend con sus migraciones y los dos secretos (en Production deben estar **antes** de desplegar: sin ellos la API entera no arranca); (2) frontend (backoffice), que debe leer las direcciones y partes de afuera
antes de que se tome el primer pedido de otro país; (3) storefront, con la misma clave compartida; (4) prender `store` por tenant.

## Cuentas de comprador

El módulo `Buyers` (spec `docs/superpowers/specs/2026-11-07-cuentas-de-comprador-design.md`, plan `docs/superpowers/plans/2026-11-07-cuentas-de-comprador-backend.md`)
da cuenta al **comprador de la tienda pública**: se registra con email y contraseña, guarda su perfil de facturación y envío, y lo reutiliza en el
checkout. No es un usuario de la consola: no tiene `Identity`, ni tenant, ni permisos, y ninguna capa de Buyers referencia `Modules.Identity*`.
La cuenta es de la plataforma (el email es único en todo `buyers.accounts`), pero **la sesión es de un host**: un token emitido en la tienda A no sirve
en la B.

- **Quién lo llama:** sólo el BFF del storefront, de servidor a servidor. El backend no escribe cookies ni lee la del navegador.
- **Esquema `buyers`:** `accounts`, `profiles`, `sessions`, `email_tokens`, `consents`, `inbox_messages` y `processed_signups`. Los perfiles, las
  sesiones y los tokens cuelgan de la cuenta por cascada. `consents` **no** tiene clave foránea y nunca se borra (ver «Datos que se conservan y se purgan»).
- **Sesión:** el token opaco viaja en el header **`X-Qep-Buyer-Session`**; en base sólo está su hash. Vence a los 30 días absolutos o tras 7 sin uso.
- **Tokens de correo:** verificación 24 h, restablecer contraseña 1 h; de un solo uso.
- **Sin auditoría** en `audit.entries`: la tabla es por tenant y el comprador no pertenece a ninguno.

### Contrato HTTP para el storefront

Todas las rutas cuelgan de `/api/v1/public/stores/{host}/buyer` y heredan lo de la tienda pública:

- `X-Qep-Storefront-Key` obligatorio: sin ella `401 store.public.client_unauthorized`.
- `X-Qep-Visitor-Ip` con la IP del visitante (confiable sólo con la clave). Obligatorio donde la tabla dice «IP: sí»: sin él `422 store.public.visitor_ip_required`.
- **El BFF DEBE mandar `X-Qep-Visitor-Ip` en toda ruta `/buyer`,** también donde la tabla no lo exige: es la partición del límite por IP.
  Sin él, el límite cae a la IP del socket (`StorefrontClient.VisitorIp`), que detrás del BFF es la misma para todos los visitantes:
  uno solo agotaría el cupo de todos.
- `{host}` que no es una tienda publicada y activa: `404 store.public.not_found`.
- Errores como `application/problem+json` con `code` y `traceId`; los `422 validation.failed` llevan `errors` (claves PascalCase de FluentValidation,
  p. ej. `Password`, `Profile.Billing.Email`).
- Toda ruta que lee cuerpo lo lee a mano con tope de **64 KB**: pasado el tope `413 store.public.body_too_large`; JSON ilegible o `null`,
  `400 store.public.body_invalid`.
- `429` sin cuerpo al pasar el límite. «Credenciales» = 10 por minuto, por ruta y por IP del visitante (política `StorefrontCredentials`);
  «lectura» = el límite de lecturas de la tienda pública (300/min).
- **Toda respuesta de `/buyer/*` lleva `Cache-Control: no-store`**, también el `429`, el `400`/`413` y el `401` de la clave. Lo pone
  `BuyerNoStoreMiddleware` (Bootstrapper), registrado en `Program.cs` **antes** de `UseRateLimiter`.
- **Sesión:** header `X-Qep-Buyer-Session: <token>`. Ausente, vacío, desconocido, vencido, revocado, de una cuenta borrada o emitido en otro host:
  `401 store.buyer.session_invalid`. **Ese código, y sólo ese, es la señal para que el BFF borre la cookie.**

#### Tipos compartidos

```jsonc
// BuyerProfile: mismos campos y reglas que billing/shipping de POST /order-requests (PublicBillingInput / PublicShippingInput).
{
  "billing": {
    "name": "Ana Pérez", "identificationType": "CC", "identificationNumber": "1020304050",
    "phone": "3001234567", "email": "ana@correo.com", "address": "Cra 1 # 2-3",
    "country": "CO", "departmentId": "<guid>", "cityId": "<guid>", "cityName": null
  },
  "shipping": null,                 // o { "recipientName", "phone", "address", "country", "departmentId", "cityId", "cityName" }
  "shippingSameAsBilling": true     // true ⇔ shipping es null (si no, 422 en Shipping)
}
// Buyer
{ "email": "ana@correo.com", "profile": BuyerProfile | null }   // email = el de login, no el de facturación
```

El perfil **no** valida contra la tienda (país vendido, ciudad del departamento): eso lo sigue haciendo el pedido.

#### Rutas

| # | Método y ruta | Cuerpo | Éxito | Errores | Límite | IP | Turnstile | Sesión |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `POST /buyer/register` | `{ "email", "password", "acceptsDataPolicy": true, "turnstileToken", "profile": BuyerProfile \| null }` | `202` sin cuerpo | 422 `validation.failed` (`Email`, `Password`, `TurnstileToken`, `Profile.*`), 422 `store.buyer.consent_required`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 2 | `POST /buyer/email/verify` | `{ "token" }` | `204` | 422 `validation.failed` (`Token`), 422 `store.buyer.token_invalid` (inexistente, vencido o usado: no se distingue) | credenciales | no | no | no |
| 3 | `POST /buyer/email/resend` | `{ "email", "turnstileToken" }` | `202` sin cuerpo | 422 `validation.failed`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 4 | `POST /buyer/session` (login) | `{ "email", "password" }` | `200` `{ "sessionToken": "<43 chars base64url>", "expiresAt": "2026-12-07T15:04:05+00:00", "buyer": Buyer }` | 422 `validation.failed` (sólo vacíos), 401 `store.buyer.credentials_invalid` (email inexistente o contraseña mala, mismo cuerpo), 403 `store.buyer.email_not_verified` (contraseña correcta, sin verificar) | credenciales | opcional | no | no |
| 5 | `GET /buyer/session` | — | `200` `Buyer` | 401 `store.buyer.session_invalid` | lectura | no | no | sí |
| 6 | `DELETE /buyer/session` (logout) | — | `204` siempre (idempotente; sin sesión válida en ese host no hace nada) | — | lectura | no | no | opcional |
| 7 | `PUT /buyer/profile` | `BuyerProfile` | `200` `Buyer` | 401 `store.buyer.session_invalid`, 422 `validation.failed` | lectura | no | no | sí |
| 8 | `POST /buyer/password` (cambiar) | `{ "currentPassword", "newPassword" }` | `204`; revoca las **demás** sesiones, la actual sigue | 401 `store.buyer.session_invalid`, 422 `validation.failed` (`NewPassword`), 422 `store.buyer.password_incorrect` (`errors.CurrentPassword`) | credenciales | no | no | sí |
| 9 | `POST /buyer/password/forgot` | `{ "email", "turnstileToken" }` | `202` sin cuerpo | 422 `validation.failed`, 422 `store.buyer.captcha_invalid`, 503 `store.captcha.unavailable` | credenciales | sí | sí | no |
| 10 | `POST /buyer/password/reset` | `{ "token", "newPassword" }` | `204`; marca el email verificado y revoca **todas** las sesiones; no abre sesión | 422 `validation.failed` (`NewPassword`, `Token`), 422 `store.buyer.token_invalid` | credenciales | no | no | no |
| 11 | `POST /buyer/account/delete` | `{ "password" }` | `204`; borra cuenta, perfil, sesiones y tokens; conserva el consentimiento; envía «cuenta eliminada» | 401 `store.buyer.session_invalid`, 422 `store.buyer.password_incorrect` (`errors.Password`) | credenciales | no | no | sí |
| 12 | `GET /buyer/orders?page&pageSize` | — | `200` `{ "items": [BuyerOrderSummary], "page", "pageSize", "total" }`; `page` por defecto 1, `pageSize` por defecto 20; más recientes primero | 400 (`page` o `pageSize` no numéricos: falla el bind), 401 `store.buyer.session_invalid`, 422 `validation.failed` (`Page` < 1, `PageSize` fuera de 1–50) | lectura | sí | no | sí |
| 13 | `GET /buyer/orders/{orderId}` | — | `200` `BuyerOrderDetail` (`lines`, `cancelledAt`, `canCancel`, totales, partes y `buyerNotes`) | 401 `store.buyer.session_invalid`, 404 `order.order.not_found` (no existe, es de otro comprador o de otro tenant) | lectura | sí | no | sí |
| 14 | `POST /buyer/orders/{orderId}/cancel` | — | `200` `BuyerOrderDetail` ya anulado | 401 `store.buyer.session_invalid`, 404 `order.order.not_found`, 409 `order.order.not_cancellable_by_buyer` (el pedido ya no está `Open` con pago `PaymentPending`, sin comprobantes, sin preparar y sin facturar, o perdió una carrera de concurrencia) | lectura | sí | no | sí |

Notas:

- Orden de chequeos (1, 3, 9): IP, host (404), cuerpo (422), consentimiento (sólo 1), Turnstile, efecto. Un rechazo no escribe nada.
- Contraseña actual mala en cambiar contraseña o eliminar cuenta es `422 store.buyer.password_incorrect`, **no** 401: un 401 con sesión válida haría que el BFF
  borre la cookie.
- `expiresAt` es el vencimiento **absoluto**; la sesión además vence tras 7 días sin uso, así que la cookie puede durar más que la sesión: el BFF lo resuelve con el 401.
- 1, 3 y 9 responden `202` exista o no la cuenta (anti-enumeración). 1 con email ya **verificado** envía «ya tienes una cuenta» y no toca nada; con email **sin verificar**
  reemplaza contraseña y perfil, invalida verificaciones previas y reenvía la verificación.
- Login de cuenta sin verificar con contraseña correcta es `403` y **no** reenvía solo: el storefront ofrece reenviar (ruta 3, con Turnstile).
- El email se normaliza (trim y minúsculas) en toda entrada: registro, login, reenviar, olvidé y checkout caen en la misma cuenta.

#### Pedidos del comprador

Rutas 12 a 14. Un comprador ve y anula sus pedidos de **la tienda del host** de la petición; el tenant sale del host, nunca del cuerpo.

- `BuyerOrderSummary`: `id`, `number`, `createdAt`, `status` (`Open` \| `Archived` \| `Cancelled`), `paymentStatus`, `fulfillmentStatus` (`NotPrepared` \| `Prepared`), `invoiced`, `total`, `currency`, `lineCount`, `firstLineName`, `canCancel`, `isStorePickup` (en la lista para que la tienda muestre «Listo para recoger» o «Enviado» sin abrir el detalle). `BuyerOrderDetail` suma `lines` (`name`, `quantity`, `unitPrice`, `lineTotal`), `cancelledAt`, `subtotal`, `taxAmount`, `freightTotal` (`subtotal + taxAmount + freightTotal == total`), `carrierName`, `billedTo` (`FinalConsumer | BillingParty | Customer`), `billing` (sólo con `BillingParty`), `shipping` (sin correo ni documento; `null` con recogida) y `buyerNotes`. Con excedente de IVA, las líneas van sin IVA.
- **Sólo aparecen los pedidos enlazados a la cuenta** (`buyer_account_id`). Desde el spec 2026-11-15 el enlace también llega por el correo de
  facturación (puntos 3 y 4), así que un pedido hecho como invitado aparece en cuanto su comprador inicia sesión en esa tienda con una cuenta
  verificada del mismo correo; los anteriores al despliegue también (spec 2026-11-15 §3.4). Un pedido sin cuenta no se lista ni se puede abrir.
- **Cómo llega la cuenta al pedido** (`quotations.orders.buyer_account_id`, sin FK):
  1. **En el checkout.** Si llega `X-Qep-Buyer-Session`, Store resuelve la cuenta **antes** de crear el pedido, con o sin `updateProfile`. Una sesión que no
     sirve (vencida, de otro host, de cuenta borrada) o una falla de Buyers (queda un `Warning`) deja un pedido de invitado; sin `updateProfile` el recibo
     no se marca con `buyerSessionRejected`.
  2. **En el alta diferida de `createAccount`.** Cuando `BuyerSignupRequestedWorker` crea la cuenta o reemplaza una sin verificar, enlaza el pedido del
     mensaje (`IBuyerOrderLinker`, después del commit del alta y fuera de él). Si el email ya tenía una cuenta verificada no se enlaza nada: esa rama no
     toca la cuenta. El enlace sólo escribe si el pedido no tiene cuenta, así que nunca pisa la del checkout; si falla, queda un `Warning` y el pedido
     sin cuenta, sin reintento.
  3. **En el checkout, por el correo de facturación** (spec 2026-11-15 §3.1). Si la sesión no resolvió cuenta, Store busca la cuenta **verificada**
     del correo de facturación, normalizado como en Buyers (`BuyerEmail.TryNormalize`), y la pone en el pedido. La sesión gana siempre: con una
     sesión válida va su cuenta aunque el correo sea otro. Una cuenta sin verificar no enlaza, y una falla de la búsqueda deja el pedido de invitado
     (queda un `Warning`, sin el correo).
  4. **Al iniciar sesión** (spec 2026-11-15 §3.2). Después de confirmar la sesión y fuera de su transacción, `POST /buyer/session` enlaza los pedidos
     del tenant de la tienda con origen `Store`, sin cuenta, sin decisión del asesor y cuyo correo de facturación recortado y en minúsculas es el de
     la cuenta. Una sola sentencia que no sube la versión. Una falla (también una cancelación) se registra con el id de la cuenta y el ingreso
     responde igual.
- **La decisión del asesor manda** (`quotations.orders.buyer_account_set_by_member`). Cuando alguien de la consola cambia o quita la cuenta
  (`PUT`/`DELETE /orders/{id}/buyer-account`, ver «Cuenta de comprador del pedido» en § Pedidos), la marca queda en `true` y ningún camino
  automático (alta diferida, inicio de sesión) vuelve a tocar la cuenta de ese pedido.
- **La anulación del comprador es más estricta que la del equipo** (D5, spec 2026-11-10): sólo si `Status == Open`, `PaymentStatus == PaymentPending`, no hay ningún comprobante de pago registrado, el pedido no está preparado **y** no está facturado (`Order.CanBeCancelledByBuyer`), y es la misma regla que alimenta `canCancel`. Fuera de eso, `409 order.order.not_cancellable_by_buyer`. Perder una carrera de concurrencia responde ese mismo `409`, nunca `412`: la tienda no tiene versión que mandar.
- Orden de chequeos: IP del visitante (422 `store.public.visitor_ip_required`), host (404), paginación (422, sólo en la lista), sesión (401), pedido del comprador en ese tenant (404).
#### Cambios en `POST /order-requests`

Cuerpo: dos campos opcionales nuevos (todo lo demás igual).

```jsonc
{
  "...": "campos de hoy",
  "createAccount": { "password": "…", "acceptsDataPolicy": true },   // opcional; null o ausente = no crear
  "updateProfile": false                                              // opcional; default false
}
```

- `createAccount`: contraseña 10–128, si no `422 validation.failed` con `errors["CreateAccount.Password"]`; `acceptsDataPolicy` distinto de `true`,
  `422 validation.failed` con `errors["CreateAccount.AcceptsDataPolicy"]`. Se valida con el resto del cuerpo, **antes** de aceptar el pedido. Email de login =
  `billing.email`; perfil = `billing`, `shipping`, `shippingSameAsBilling = (shipping == null)`.
- Con `X-Qep-Buyer-Session` válido el pedido queda enlazado a la cuenta (ver «Pedidos del comprador»), con o sin `updateProfile`.
- `updateProfile: true` usa el header `X-Qep-Buyer-Session` (el BFF lo manda con la sesión). Si falta o no sirve, el pedido se acepta igual
  (`201`) y el recibo lleva `buyerSessionRejected: true`: no es un error. Con `createAccount` a la vez, `422 validation.failed` en `CreateAccount`.
- Recibo `201`: suma **`"buyerSessionRejected": bool`**, `true` sólo si se pidió `updateProfile` y la sesión no sirve (ausente, vencida, de otro host, de cuenta borrada).
  El pedido se acepta igual y el BFF borra la cookie.

```jsonc
{ "id": "<guid del pedido>", "number": "PED-2026-0001", "total": 10000, "currency": "COP", "buyerSessionRejected": false }
```

- Reintento con el mismo `clientRequestId`: el primer recibo, `buyerSessionRejected: false`, sin crear cuenta ni tocar el perfil.
- La cuenta se crea de forma asíncrona y durable (un mensaje del outbox que commitea en la transacción de Store posterior al pedido), así que puede tardar unos segundos en existir después del `201`.
  Sus errores (email existente, fallo al guardar) nunca cambian la respuesta.

#### Correos

Los envía Notifications; los enlaces apuntan al host de la tienda:

| Evento | Enlace |
| --- | --- |
| Verificación (registro, reenvío, checkout con email nuevo o sin verificar) | `https://{host}/cuenta/verificar?token=<token>` |
| «Ya tienes una cuenta» | `https://{host}/cuenta/ingresar` y `https://{host}/cuenta/olvide` |
| Restablecer contraseña | `https://{host}/cuenta/restablecer?token=<token>` |
| Cuenta eliminada | sin enlace |

### Eventos de outbox

Todos viven en `platform.outbox_messages`.

| Evento | Lo escribe | Lo consume | Lleva |
| --- | --- | --- | --- |
| `store.buyer-signup-requested.v1` | Store, en la **misma transacción de Store posterior al pedido** (`createAccount`); idempotente por `orderId` (`buyers.processed_signups`) | `BuyerSignupRequestedWorker` (Buyers) | email normalizado, perfil y **sólo `passwordHash`**, nunca la contraseña |
| `buyers.email-verification-requested.v1` | Buyers | `BuyerVerificationDeliveryWorker` (Notifications) | token de verificación en claro |
| `buyers.existing-account-notice.v1` | Buyers | `BuyerExistingAccountDeliveryWorker` | sin token |
| `buyers.password-reset-requested.v1` | Buyers | `BuyerPasswordResetDeliveryWorker` | token de restablecer en claro |
| `buyers.account-deleted.v1` | Buyers | `BuyerAccountDeletedDeliveryWorker` | sin token |

Cada consumidor lleva su propio inbox (el de Buyers es `buyers.inbox_messages`). Los cuatro correos comparten `BuyerEmailPayload`
(`AccountId`, `Email`, `StoreHost`, `StoreName`, `Token`).

### Cómo se conecta con Store

- **Alta desde el checkout.** Store hashea la contraseña al validar el pedido (`IStoreBuyerAccounts.HashPassword`, adaptador `StoreBuyerAccounts` en Bootstrapper) y
  escribe el evento de alta con sólo el `passwordHash`. Si el hash falla, el pedido se acepta sin alta. El alta la procesa `BuyerSignupRequestedWorker`; si el email ya
  tiene cuenta, el pedido no se entera.
- **`updateProfile`.** Store llama `IStoreBuyerAccounts.ReplaceProfileAsync`; el adaptador llama a `ReplaceBuyerProfileFromOrder`, que resuelve la sesión por host y
  reemplaza el perfil. Devuelve si la sesión sirvió; si no, el recibo lleva `buyerSessionRejected: true`.
- **Dependencias entre módulos.** Store no referencia Buyers (el adaptador vive en Bootstrapper) y `Notifications.Infrastructure` referencia
  `Buyers.Application` para leer `BuyerIntegrationEvents` y `BuyerEmailPayload`. Las reglas viven en `BuyersLayerTests` y `StoreLayerTests`, en
  `tests/ArchitectureTests/ArchitectureTests`.

### Datos que se conservan y se purgan

- **El consentimiento no se borra nunca.** `buyers.consents` guarda email, IP, host, versión y URL de la política (`BuyerDataPolicy.Version`) de cada aceptación, y
  sobrevive a la cuenta: no tiene clave foránea y la purga no lo toca. Borrar la cuenta lo conserva.
- **Las notificaciones del comprador se anonimizan al borrar la cuenta.** Cuando `BuyerAccountDeletedDeliveryWorker` cierra el mensaje de cuenta
  eliminada —haya salido el correo o haya fallado, porque ninguno de los dos se reintenta—, toda fila de `notifications.notifications` sin tenant,
  con `recipient_id` de la cuenta y plantilla `buyers.*` (ésta incluida) queda con `recipient_id` vacío (`Guid.Empty`) y `recipient_address` vacío,
  en el mismo guardado que el `processed_at`. Las filas no se borran: estado, plantilla y fechas siguen contando para las estadísticas de envío.
- **Ningún correo del comprador sale después del borrado.** En la misma transacción que borra la cuenta, Buyers borra de `platform.outbox_messages`
  los demás correos de esa cuenta (verificación, «ya tienes una cuenta», restablecer; por `payload->>'accountId'`). Y uno que ya estaba en vuelo
  lo frena Notifications: antes de enviar, si el outbox tiene un `buyers.account-deleted.v1` de la cuenta, no envía y guarda la fila ya
  anonimizada, `Failed` con `buyer_account_deleted`; si el borrado se confirma mientras sale el correo, la fila se anonimiza al guardarse.
- `BuyerHousekeepingWorker` corre cada hora y borra en masa:
  - cuentas **sin verificar** cuyo último registro o reenvío de la verificación (`updated_at`) tiene más de 7 días (perfil, sesiones y tokens se van
    por cascada);
  - sesiones revocadas, vencidas o sin uso en 7 días, y tokens usados o vencidos;
  - de `platform.outbox_messages` (llevan el hash de la contraseña o tokens en claro, y nada más las borraría): los 4 correos del comprador con
    `occurred_at` de hace más de **24 h**, sólo por edad; el alta (`store.buyer-signup-requested.v1`) sólo cuando `buyers.inbox_messages` tiene la fila
    procesada del consumidor `buyers.signup-requested` para ese mensaje;
  - `buyers.inbox_messages` procesados con más de **30 días** cuyo mensaje ya no está en el outbox, y `buyers.processed_signups` con más de 30 días.
  - Cada paso corre aislado: uno que falla se registra con el tipo de la excepción y los demás siguen.
- Workers: `BuyerSignupRequestedWorker` (poll de 15 s) y `BuyerHousekeepingWorker` en Buyers; en Notifications, un entregador por cada correo del comprador.

### Pendientes del owner

Datos legales y plazo de conservación de la política; y el nombre del remitente (hoy se usa sólo `Notifications:Infobip:SenderEmail`).
`BuyerDataPolicy.Version` es `1.0.0`, la versión que el owner publicó el 2026-11-08 en la página de la tienda.

## API implementada

Inventario completo de la superficie HTTP. Las secciones siguientes desarrollan
sólo la configuración del tenant y la invitación de memberships, con ejemplos
ejecutables; el resto se documenta en el spec de su slice y en el documento
OpenAPI (`/openapi/v1.json`, sólo en `Development`).

Los flujos que cruzan varios endpoints tienen guía propia en [`docs/`](docs/):

- [Imágenes de producto](docs/integracion-imagenes-de-producto.md) — subir a R2,
  publicar y asignar la portada, con los códigos de error que la UI debe distinguir.
- [Cotizaciones y pedidos](docs/integracion-cotizaciones-y-pedidos.md) — estados, rutas, formas de
  los DTOs (el pedido con sus datos propios y el pedido manual) y códigos de error.

| Grupo de rutas                                     | Operaciones                                                                                 | Autorización                                                                                 |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `/health/live`                                     | `GET`                                                                                       | anónimo                                                                                      |
| `/health/ready`                                    | `GET`                                                                                       | anónimo                                                                                      |
| `/api/v1/version`                                  | `GET`                                                                                       | anónimo, limitado por IP (`public`)                                                          |
| `/api/v1/auth/registration-policy`                 | `GET`                                                                                       | anónimo                                                                                      |
| `/api/v1/auth/register-tenant`                     | `POST`                                                                                      | token del proveedor OIDC                                                                     |
| `/api/v1/auth/session`                             | `POST`                                                                                      | token del proveedor OIDC                                                                     |
| `/api/v1/auth/me`, `/api/v1/auth/logout`           | `GET`, `POST`                                                                               | sólo autenticación                                                                           |
| `/api/v1/tenants/{tenantId}/authorization/me`      | `GET`                                                                                       | sólo autenticación (deliberado: pedir permiso para saber qué permisos se tienen es circular) |
| `/api/v1/tenants/{tenantId}/authorization/catalog` | `GET`                                                                                       | `advisorship.read`                                                                    |
| `/api/v1/tenants/{tenantId}/settings`              | `GET`, `PUT`                                                                                | `tenancy.settings.read` / `.update`                                                          |
| `/api/v1/tenants/{tenantId}/memberships`           | `POST`, `GET`, y `suspend`, `remove`, `reactivate`, `roles`, `profile` por membership   | `advisorship.invite` / `.read` / `.manage`                                            |
| `/api/v1/tenants/{tenantId}/catalog/products`      | `GET` (`includeVariants`), `POST`, `export`, `price-scales/copy`, y por producto `GET`, `PUT` (`expectedVersion`), `activate`, `deactivate` (ver [Catálogo](#catálogo-opciones-variantes-y-galería)) | `catalog.product.read` / `.manage`                                                           |
| `/api/v1/tenants/{tenantId}/catalog/options`       | `GET`, `POST`, y por opción `PUT`, `DELETE`                                                 | `catalog.product.read` / `.manage`                                                           |
| `/api/v1/tenants/{tenantId}/catalog/categories`    | `GET`, `POST`; por categoría `PUT` y `DELETE` (`If-Match`); `PUT pos-quick-access`           | `catalog.product.read` / `.manage`                                                           |
| `/api/v1/tenants/{tenantId}/catalog/price-lists`   | `GET` (todas, por nombre), `POST` (201 + `Location`), y por lista `GET`, `PUT` y `DELETE` (`If-Match`; 409 `catalog.price_list.in_use` si un canal o un distribuidor la usa), `overrides` (`GET` paginado con `search`; `PUT` por lote `{ set, remove }`, `If-Match`, hasta 500 filas). Ver [Listas de precios por canal](#listas-de-precios-por-canal) | `catalog.price_list.manage` |
| `/api/v1/tenants/{tenantId}/catalog/channel-prices` | `GET` y `PUT` (la lista de `Pos`, `Store` y `Quotations`; `null` = precio base; sin `If-Match`) | `catalog.price_list.manage` |
| `/api/v1/tenants/{tenantId}/files`                 | `GET`, `POST`, y `complete`, `metadata`, `download-url`, `publication`, borrado por archivo | `storage.file.read` / `.upload` / `.publish` / `.delete`                                     |
| `/api/v1/tenants/{tenantId}/customers`             | `identification-matches` (`GET`) y `anomalies` (`GET`); el resto del módulo está en sus secciones (ver [Clientes: documento repetido](#clientes-documento-repetido-coincidencias-y-anomalías)) | `customers.customer.read` |
| `/api/v1/tenants/{tenantId}/customers/{customerId}` | `merge-preview` (`GET`) y `merge` (`POST`, `If-Match`); ver [Clientes: fusión](#clientes-fusión) | `customers.customer.merge` |
| `/api/v1/tenants/{tenantId}/orders`                | `GET` (filtros, entre ellos `source` y `timeZone`), `POST` (pedido manual), `preview` (`POST`), `export` (`POST`, mismos filtros), y por pedido `GET`, `PUT` (`If-Match`), `preview`, `mark-paid`, `unmark-paid`, `prepare`, `unprepare`, `archive`, `unarchive`, `approve` (pedido `Wholesale`), `cancel`, `invoice`, `uninvoice`, `proofs` (`POST`, `DELETE`), `items` (`POST`), `send` (`POST`, cuerpo opcional); el `GET` de la lista acepta `wholesaleApproval`=`Pending`\|`Approved` y responde `pendingWholesaleApprovals` (ver [Pedidos](#pedidos-datos-propios-y-pedido-manual), [Enviar el pedido por WhatsApp](#enviar-el-pedido-por-whatsapp) y [Mayorista: órdenes vinculadas](#mayorista-órdenes-de-compra-vinculadas)) | `quotations.order.read` / `.manage` (`prepare`, `unprepare`, `archive`, `unarchive`, `approve`) / `.approve` (`mark-paid`, `unmark-paid`) / `.cancel` / `.invoice` |
| `/api/v1/tenants/{tenantId}/orders-settings`       | `GET`, `PUT` (los tres ajustes del pedido; ver [Pedidos](#pedidos-datos-propios-y-pedido-manual)) | `tenancy.settings.read` / `.update` |
| `/api/v1/tenants/{tenantId}/pos`                   | 14 operaciones: caja y ventas del punto de venta (ver [POS](#pos-caja-y-ventas))             | `pos.register.operate` / `pos.sale.read` / `.create` / `.void` / `.customer`                 |
| `/api/v1/tenants/{tenantId}/suppliers` | `GET` (`search`, `status`=`active`\|`inactive`, `category`, `page`, `pageSize` por defecto 50, tope 200; orden por nombre), `POST` (201 + `Location`), `categories` (`GET`, distintas y ordenadas, máx. 200), `export` (`POST`, mismos filtros sin paginación; 202 `{ fileName, supplierCount, expiresAt }` y el archivo llega por correo), y por proveedor `GET`, `PUT` (las listas `bankAccounts` y `contacts` que llegan son las que quedan), `activate` y `deactivate` (`POST`). Módulo `suppliers` (apagado por defecto). Errores propios: `suppliers.supplier.identification_taken`, `.city_not_found`, `.status_filter_invalid`, `.already_active`, `.already_inactive`, `.not_found`, `suppliers.export.empty`, `suppliers.export.too_many_rows` | `suppliers.supplier.read` (`GET`, `categories`, `export`) / `suppliers.supplier.manage` (`POST`, `PUT`, `activate`, `deactivate`) |
| `/api/v1/tenants/{tenantId}/purchases/orders` | `GET` (`status`=`Draft`\|`Sent`\|`Confirmed`\|`Received`\|`Cancelled` sin importar mayúsculas, `supplierId`, `search` en el número o el nombre del proveedor, `page`, `pageSize` por defecto 50, tope 200; de la más nueva a la más vieja; `counts` por estado más `all` con los mismos filtros salvo `status`), `POST` (201 + `Location`, en `Draft` con número `OC-0001` por tenant; un alta rechazada no gasta número), `preview` (`POST`, mismo cuerpo, no escribe; `purchases.order.manage`), y por orden `GET`, `pdf` (`GET`, `application/pdf`, `OC-0001.pdf`, en cualquier estado), `PUT` y `DELETE` (sólo `Draft`, `If-Match`), `send`, `confirm` (cuerpo `{ lines: [{ lineId, confirmedQuantity, confirmedUnitCost }] }` con cada línea una vez), `receive` y `cancel` (`POST`, `If-Match`). Costos sin IVA; el IVA se suma en `totals` con la tasa que la línea copió del catálogo al guardar el borrador. Módulo `purchases` (apagado por defecto). Errores propios: `purchases.order.not_found` (404), `.not_draft` y `.invalid_transition` (409), `.empty`, `.line_not_in_catalog`, `.supplier_inactive`, `.invalid_currency`, `.confirm_lines_mismatch` (422), `.pdf_render_failed` (502); proveedor inexistente: `suppliers.supplier.not_found` (404) | `purchases.order.read` (`GET`, `pdf`) / `purchases.order.manage` (`POST`, `preview`, `PUT`, `DELETE`, `send`, `confirm`, `receive`, `cancel`) |
| `/api/v1/tenants/{tenantId}/purchases/suppliers/{supplierId}/catalog` | `GET` (`search`, `page`, `pageSize` por defecto 50, tope 200): el catálogo del mayorista de un proveedor vinculado en su moneda, con `priceList { name, discountPercent }` y, por variante, base, descuento y precio de lista con IVA más `unitCost` sin IVA (ver [Mayorista: órdenes vinculadas](#mayorista-órdenes-de-compra-vinculadas)). `404 suppliers.supplier.not_found`, `422 purchases.order.supplier_not_linked`, `403 wholesale.relationship.inactive` | `purchases.order.manage` |
| `/api/v1/tenants/{tenantId}/wholesale/price-lists/preview` | `POST` (la lista sin guardar, `{ adjustmentPercent, visibility, categoryIds, exclusions }`: hasta 20 variantes con precio base y de lista y `totalProducts`; no escribe). Las listas mismas están en `/catalog/price-lists` desde el spec 2026-11-22. Módulo `wholesale` | `catalog.price_list.manage` + `wholesale.distributor.read` |
| `/api/v1/tenants/{tenantId}/wholesale/categories` | `GET` (las categorías del catálogo como árbol plano `{ id, name, parentId }`, para elegir la visibilidad sin `catalog.product.read`) | `catalog.price_list.manage` + `wholesale.distributor.read` |
| `/api/v1/tenants/{tenantId}/wholesale/distributors` | `GET` (`status`=`Invited`\|`Active`\|`Blocked`, `search`, `page`, `pageSize` por defecto 50, tope 200; `counts`), `options` (`GET`, listas, asesores, empresas facturadoras y `whatsAppConnections`), `POST` (invita: 201 + `Location`; `cityId` obligatorio con `country = "CO"` y `cityName` con otro país, `address` opcional, `422 wholesale.distributor.city_not_found` si la ciudad no existe; manda el correo y, con `phone`, el aviso por WhatsApp), y por distribuidor `GET` (`phone`, `city` `{ id, name }` o `null`, `address`, `invitation.whatsAppNotice`), `PUT` (lista, asesor, empresa y, invitado, `phone`, `whatsAppConnectionId` y los datos del distribuidor: `businessName`, `country`, `identificationType`, `identificationNumber`, `contactName`, `email`, `cityId`, `cityName`, `address`, todos opcionales; `If-Match`; ver [Editar un distribuidor](#editar-un-distribuidor)), `resend-invitation`, `block`, `unblock` (`POST`, `If-Match`) y `DELETE` (sólo `Invited`, `If-Match`) | `wholesale.distributor.read` (`GET`) / `wholesale.distributor.manage` (el resto) |
| `/api/v1/wholesale-invitations/{token}` | `GET` (anónimo, límite `public`), `email-code` (`POST`, anónimo, `credentials`), `session` (`POST`, bearer de Google o stub, límite `credentials`), `session-with-password` (`POST`, anónimo, `credentials`), `linkable-tenants` (`GET`, sesión QEP) y `accept` (`POST`, sesión QEP); ver [Mayorista](#mayorista-listas-de-precios-distribuidores-e-invitación) | — |
| `/api/v1/tenants/{tenantId}/operator/tenants`      | `GET`, y por tenant `GET`, `modules/changes` (`POST`), `providers/changes` (`POST`), `status` (`POST`, `If-Match`), `history` (`GET`) | `operator.tenants.read` / `operator.modules.manage` / `operator.tenants.manage`, sólo en el tenant operador |
| `/api/v1/tenants/{tenantId}/integrations`          | `catalog` (`GET`), `connections` (`GET`, `POST`), y por conexión `GET`, `PUT` (`If-Match`), `test` (`POST`), `pause` y `resume` (`POST`, `If-Match`), `DELETE` (`If-Match`) | `integrations.connection.read` / `.manage` |
| `/api/v1/tenants/{tenantId}/integrations/whatsapp/embedded-signup` | `POST` | `integrations.connection.manage` |
| `/api/v1/tenants/{tenantId}/messaging` | `conversations` (`GET`), por conversación `GET`, `messages` (`GET`, `POST`), `messages/audio` (`POST`, multipart), `read` (`POST`), `resolve` (`POST`, `If-Match`, cuerpo opcional `{ reasonId }` o `{ reasonName }`) y `reopen` (`POST`, `If-Match`), `automation/pause`, `automation/resume`, `automation/start` y `automation/continue` (`POST`, `If-Match`, `.manage`; ver [Asistente en la conversación](#asistente-en-la-conversación)); `messages/search` (`GET`); `media/{messageId}` (`GET`); `conversations/{id}/customer` (`PUT`, `If-Match`, cuerpo `{ customerId }`, `.manage`; ver [Identidad, clientes y asignación](#identidad-clientes-y-asignación-spec-2026-10-10)) | `messaging.conversation.read` / `.manage` |
| `/api/v1/tenants/{tenantId}/messaging/quick-replies` | `GET`, `POST`; por respuesta `PUT` (`version` en el cuerpo) y `DELETE` (`?version=`) | `messaging.conversation.read` (`GET`) / `messaging.quick_reply.manage` |
| `/api/v1/tenants/{tenantId}/messaging/resolution-reasons` | `GET`; por motivo `PATCH` (`If-Match`) y `DELETE` (archiva). Se crean al resolver | `messaging.conversation.read` (`GET`) / `messaging.resolution_reason.manage` |
| `/api/v1/tenants/{tenantId}/messaging/business-hours` | `GET`, `POST`; por horario `PUT` (`If-Match`), `DELETE` (`If-Match`), `duplicate` (`POST`) y `default` (`POST`, `If-Match`) (ver [Horarios de atención](#horarios-de-atención)) | `messaging.conversation.read` (`GET`) / `messaging.business_hours.manage` |
| `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management` | `GET`, `templates` (`POST`), por plantilla `DELETE`, `quotation` (`POST`), `sync` (`POST`), `wholesale-invitation-notice` (`POST`, cuerpo opcional `{ body }`) y `wholesale-invitation-notice/draft` (`GET`), la plantilla del aviso de invitación de mayorista (ver [Plantillas de WhatsApp desde QEP](#plantillas-de-whatsapp-desde-qep) y [Aviso por WhatsApp de la invitación](#aviso-por-whatsapp-de-la-invitación)); `appointment-reminder` (`POST`, cuerpo opcional `{ body }`) y `appointment-reminder/draft` (`GET`), la plantilla `qep_agenda_recordatorio` del recordatorio de cita de la Agenda | `messaging.template.manage` |
| `/api/webhooks/whatsapp` | `GET` (verificación), `POST` (firmado por Meta) | anónimo, limitador `webhook`, exento de CSRF |

Toda ruta con `{tenantId}` valida además el tenant en el handler y responde
**403, nunca 404**, cuando el recurso pertenece a otro tenant.

Excepción: en `/operator/*` un `targetTenantId` inexistente es **404**. El operador ve todos los
tenants por diseño, así que el 404 no le revela nada (spec 2026-10-08, D7).

### Health check

```http
GET /health/live
```

Es anónimo y responde `200 OK` con `{"status":"healthy"}`. **No toca la base**, a propósito:
es la `livenessProbe` y la `startupProbe` del Deployment, y si dependiera de PostgreSQL una
caída de la base haría que Kubernetes reiniciara todos los pods en cadena.

```http
GET /health/ready
```

Es anónimo y es la `readinessProbe`. Abre una conexión a `QepDatabase` y corre `SELECT 1` con un
límite de 2 s: responde `200 OK` (`Healthy`) si la base contesta y `503 Service Unavailable`
(`Unhealthy`) si no. El cuerpo es sólo el estado, sin el detalle del error. Un pod con 503 sale
del Service hasta que la base vuelva, sin reiniciarse.

`k8s/prod-pdb.yaml` (`PodDisruptionBudget`, `minAvailable: 1`) está preparado pero **no se
despliega**: `azure-pipelines.yml` no lo lista. Con `replicas: 1` bloquearía el drain del nodo.
Al escalar a 2 réplicas se agrega `pdb` a la lista de manifests del pipeline, después de
`deployment`, en el mismo cambio.

### Versión desplegada

```http
GET /api/v1/version
```

Anónimo y limitado por IP con la política `public` del rate limiter, como el documento OpenAPI.
Responde siempre `200 OK` con lo que el proceso sabe de sí mismo:

```json
{
  "buildId": "4812",
  "buildNumber": "20261014.3",
  "commit": "e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3",
  "environment": "Production",
  "startedAt": "2026-10-14T13:05:41.1234567+00:00"
}
```

`buildId` es el `Build.BuildId` de Azure DevOps, que es también el tag de la imagen desplegada
(`azure-pipelines.yml`); `buildNumber` es el `Build.BuildNumber` (`yyyyMMdd.r`, trae la fecha);
`commit` es el SHA completo. Los tres llegan al pod por `k8s/prod-configMap.yaml` como
`Deployment__*`, con tokens que el pipeline reemplaza, y los tres vienen en `null` cuando el
proceso no los recibió: en local, o si un token quedó sin resolver (el backend trata un valor que
empieza por `#{` como ausente, `DeploymentInfo.Normalize`). `environment` es el nombre del
`IHostEnvironment` y `startedAt` el arranque del proceso, que distingue "desplegué hace un minuto"
de "este pod lleva tres días". El mismo `buildNumber` viaja como `service.version` en el recurso de
OpenTelemetry.

Para verificar un despliegue desde PowerShell:

```powershell
curl.exe -s https://qep-api.qcode.co/api/v1/version
```

Si `buildId` no coincide con el tag de la imagen del Deployment, alguien aplicó manifiestos por
fuera del pipeline:

```powershell
kubectl -n prod-qep-backend get deploy qep-backend -o jsonpath='{.spec.template.spec.containers[0].image}'
```

`qep-frontend` dibuja esta respuesta junto a su propio commit en el menú de usuario y en el pie
del login (spec `docs/superpowers/specs/2026-10-14-version-desplegada-design.md`).

### Configuración del tenant

| Método   | Ruta                                        | Permiso                   |
| -------- | -------------------------------------------- | -------------------------- |
| `GET`    | `/api/v1/tenants/{tenantId}/settings`        | `tenancy.settings.read`   |
| `PUT`    | `/api/v1/tenants/{tenantId}/settings`        | `tenancy.settings.update` |
| `PUT`    | `/api/v1/tenants/{tenantId}/settings/logo`   | `tenancy.settings.update` |
| `DELETE` | `/api/v1/tenants/{tenantId}/settings/logo`   | `tenancy.settings.update` |

El `GET` devuelve la configuración y un encabezado `ETag` con su versión. El
`PATCH` exige enviar esa versión en `If-Match`; una versión desactualizada
produce `412 Precondition Failed`.

El logo se sube primero por la biblioteca de archivos (`POST /files` con
`ownerType: "Tenant"`, `PUT` a la URL firmada, `POST /files/{id}/complete` — ver
"Biblioteca de archivos" más abajo) y recién después se asigna con `PUT .../settings/logo`,
cuerpo `{ "fileId": "<guid>" }` y el mismo `If-Match` que el `PATCH`. Sólo PNG, JPEG o WEBP, hasta
2 MiB; `DELETE` lo quita, mismo `If-Match`. Los dos devuelven el `TenantSettingsResponse`
completo, con `version`/`ETag` nuevos. Exigen `Storage:R2:PublicBucket` y
`Storage:R2:PublicBaseUrl` configurados (ver la tabla de arriba); sin ellos, `PUT` responde
`422 storage.public.not_configured` y `GET` sigue devolviendo `logo.url: null` para un tenant que
ya tenía uno asignado.

Ejemplo completo en PowerShell:

```powershell
$tenantId = "01900000-0000-7000-8000-000000000001"
$headers = @{
  "X-Subject-Id" = "01900000-0000-7000-8000-000000000002"
  "X-Tenant-Id"  = $tenantId
}

$settings = Invoke-WebRequest `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/settings" `
  -Headers $headers

$settings.Content
$headers["If-Match"] = $settings.Headers.ETag

$body = @{
  displayName     = "QCode Enterprise"
  defaultCulture = "es-CO"
  timeZone       = "America/Bogota"
  dateFormat     = "dd/MM/yyyy"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Patch `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/settings" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```

Respuesta:

```json
{
  "tenantId": "01900000-0000-7000-8000-000000000001",
  "displayName": "QCode Enterprise",
  "defaultCulture": "es-CO",
  "timeZone": "America/Bogota",
  "dateFormat": "dd/MM/yyyy",
  "version": 2,
  "logo": null
}
```

Formatos de fecha admitidos: `yyyy-MM-dd`, `dd/MM/yyyy` y `MM/dd/yyyy`.

#### Idioma del producto (`es-419` / `es-ES`)

Spec: [2026-11-22 i18n](docs/superpowers/specs/2026-11-22-i18n-espanol-espana-y-latinoamerica-design.md). El idioma no es el
formato regional: `defaultCulture`, `numberFormat` y `dateFormat` no cambian con él. El catálogo es
`SupportedLanguages` (`BuildingBlocks.Domain.Localization`), y el diccionario de cada idioma lo tiene el frontend.

| Dónde | Campo | Qué es |
| ----- | ----- | ------ |
| `GET`/`PUT /tenants/{tenantId}/settings` | `language` | El idioma del tenant, para quien no eligió uno: `es-419` (por defecto) o `es-ES`. Si el `PUT` no lo trae, se conserva; un valor fuera del catálogo responde `422 tenancy.settings.language.invalid`. |
| `GET`/`PUT /auth/preferences` | `language` | Lo que eligió la persona en este tenant, o `null` para seguir al tenant. En el `PUT`: ausente o `null` conserva, `""` vuelve al del tenant, y un valor fuera del catálogo responde `422 identity.preference.language.invalid`. |
| `GET`/`PUT /auth/preferences` | `effectiveLanguage` | El diccionario que tiene que cargar la pantalla: el elegido, si no el del tenant. |
| `/auth/me`, `POST /auth/session` | `activeTenants[].language` | El idioma **efectivo** de la persona en cada tenant, disponible antes de la primera pintura. |
| `POST /auth/register-tenant`, `POST /auth/password/register-tenant` | `language` | Opcional; ausente o `""` es `es-419`. |

Los correos ya están en español neutro con tuteo, y solo cambian los términos que en España se dicen distinto: «cotización»
es «presupuesto» (correos de exportación, en el idioma efectivo de quien exportó) y «radicado» es «número de caso» (correo de apertura
de Soporte, en el idioma del tenant). El parámetro `radicado` del enlace de consulta no se traduce.

El PDF de cotización y de pedido y el Excel de cotizaciones van en el idioma del tenant: en `es-ES` dicen «Presupuesto» y
«Comercial», y los archivos se llaman `Presupuesto-N.pdf` y `presupuestos-….xlsx`. Pasar un tenant a `es-ES` regenera sus PDF guardados
en el siguiente export, porque el idioma entra a la huella; el despliegue no regenera ninguno.

### Invitación de memberships

| Método | Ruta                                     | Permiso                     |
| ------ | ---------------------------------------- | --------------------------- |
| `POST` | `/api/v1/tenants/{tenantId}/memberships` | `advisorship.invite` |

La operación obtiene o crea en Identity un usuario invitado, y después crea su
Membership en Tenancy con estado `Invited`, auditoría y el evento Outbox
`tenancy.membership-invited.v1`. La invitación vence después de 72 horas.

Además emite un **token de invitación** de 32 bytes en base64url. De ese token
sólo se persiste su SHA-256 en `memberships.invitation_token_hash`, con índice
único: el valor plano viaja únicamente dentro del evento de dominio, rumbo al
Outbox y al email. Quien lea la tabla no puede reconstruir un link válido.
Re-invitar rota el token, con lo que el link anterior deja de servir.

Ejemplo en PowerShell, reutilizando `$tenantId` y `$headers` del ejemplo
anterior:

```powershell
$body = @{
  email = "new.member@example.com"
  displayName = "Ana Pérez"
  advisorCode = 12
  roles = @("advisor")
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/memberships" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```

Respuesta `201 Created`:

```json
{
  "id": "01900000-0000-7000-8000-000000000010",
  "userId": "01900000-0000-7000-8000-000000000011",
  "displayName": "Ana Pérez",
  "advisorCode": 12,
  "tenantId": "01900000-0000-7000-8000-000000000001",
  "state": "Invited",
  "roles": ["advisor"],
  "invitedAt": "2026-07-05T21:00:00+00:00",
  "acceptedAt": null,
  "expiresAt": "2026-07-08T21:00:00+00:00"
}
```

Repetir secuencialmente la invitación para el mismo email y tenant devuelve la
Membership existente sin crear duplicados.

`displayName` es obligatorio: se guarda sin espacios a los costados y admite entre 1 y 150
caracteres. Sin él responde `422 validation.failed` con `errors.DisplayName`.

`advisorCode` es opcional: el código entero positivo con el que el sistema externo del tenant
(ERP, contabilidad) identifica a la persona. Se guarda como `integer`, así que `0012` y `12` son
el mismo. Un valor que no sea un entero mayor que cero responde `422 validation.failed` con
`errors.AdvisorCode`; uno que ya tenga otra membresía del tenant —incluida una quitada, que
conserva el suyo—, `422 tenancy.membership.advisor_code_taken`. Una quitada deja de bloquear
cuando su usuario se borra por no tener historia: `OrphanUserCleanupWorker` purga sus membresías
quitadas o vencidas antes de borrarlo (spec 2026-10-02), y desde ahí el código queda libre, también
para la misma persona si la vuelves a invitar.

Una invitación viva o una membresía activa ignoran el nombre y el código del cuerpo. Renovar una
invitación vencida o una membresía quitada reescribe el nombre y, si el cuerpo trae
`advisorCode`, lo reemplaza; sin `advisorCode` conserva el que la membresía ya tenía. Para
cambiárselos a un miembro —incluido borrar el código— está `PUT .../profile`.

Los errores usan `ProblemDetails` e incluyen `code` y `traceId`; los errores de
validación también incluyen un mapa `errors`.

| Estado | Significado                                                         |
| ------ | ------------------------------------------------------------------- |
| `401`  | No se pudo autenticar la solicitud                                  |
| `403`  | Falta el permiso o el tenant de la ruta no coincide con el contexto |
| `404`  | No existe el tenant solicitado                                      |
| `412`  | El `ETag` enviado ya no es la versión vigente                       |
| `422`  | Falló una validación o regla de dominio                             |
| `428`  | Falta un encabezado `If-Match` válido                               |

### Perfil del miembro: nombre y código de asesor

| Método | Ruta                                                              | Permiso              |
| ------ | ----------------------------------------------------------------- | --------------------- |
| `PUT`  | `/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile`   | `advisorship.manage` |

Cambia, en un solo request, el nombre con el que el tenant presenta a la persona —el que imprime
el PDF de cotización— y su código de asesor, el entero con el que la identifica el sistema externo
del tenant. Van juntos porque el diálogo los edita juntos: con dos requests, el segundo viajaría
con el `If-Match` viejo y respondería `412`. Vale en cualquier estado de la membresía y sobre la
propia: el owner, que entra por `register-tenant` sin nombre ni código, los carga desde acá.

Exige `If-Match` con la versión cargada, igual que `PUT .../roles`, y responde `200` con la fila
del roster y el `ETag` nuevo. Guardar sin cambios no sube la versión ni se audita; un cambio real
se audita como `tenancy.membership.profile_updated`.

```powershell
$body = @{ displayName = "Ana María Pérez"; advisorCode = 12 } | ConvertTo-Json
$putHeaders = $headers.Clone()
$putHeaders["If-Match"] = '"1"'

Invoke-RestMethod `
  -Method Put `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/memberships/$membershipId/profile" `
  -Headers $putHeaders `
  -ContentType "application/json" `
  -Body $body
```

`advisorCode` es opcional; `null` borra el código. Sin `If-Match` responde
`428 precondition.if_match_required`; con una versión vieja, `412`; con un nombre vacío o de más
de 150 caracteres, `422 validation.failed` con `errors.DisplayName`; con un código que no sea un
entero mayor que cero, `422 validation.failed` con `errors.AdvisorCode`; con un código que ya
tiene otra membresía del tenant —incluida una quitada que todavía no se purgó, que conserva el
suyo—, `422 tenancy.membership.advisor_code_taken`. El mismo código en otro tenant es válido.

El código de asesor llega al sistema externo por el Excel de pedidos: la columna `Cod. Asesor`,
después de `Email`, numérica y repetida en cada línea del pedido. Se resuelve al exportar desde la
membresía asesora del pedido con el código **de hoy** —si se lo cambian, los pedidos viejos
salen con el nuevo— y queda vacía si la membresía no tiene código.

### Facturar a otra persona: documento de identidad

Cuando una cotización se factura a otra persona —la parte de facturación (`parties.billing`) trae
`name` propio—, esa persona necesita su documento: sin él no se puede facturar (owner,
2026-09-26). Viaja como `identificationNumber` al lado de `name`, en el request de
`POST /quotations`, `PUT /quotations/{id}` y `POST /quotations/{id}/preview`, y en la respuesta
dentro de `parties[]` (rol `Billing`):

```json
{ "parties": { "billing": { "name": "Distribuciones Andinas S.A.S.", "identificationNumber": "901555444-1", "phone": "6015550000" }, "shipping": null } }
```

- Vacío o sólo espacios es `null`; se recorta y nada más (los puntos y guiones de un NIT quedan
  como se escribieron). Tope de 32 caracteres, el mismo que el documento de la ficha del cliente.
- Sin `name` propio no se pide: la factura sigue saliendo a nombre del cliente, con su documento.
- En la parte de entrega se **ignora** (el formulario lo manda siempre vacío): nunca se guarda ni
  se valida.
- Crear y guardar (`PUT`) sin el número responden `422 validation.failed` con
  `errors["Parties.Billing.IdentificationNumber"]`; el dominio lo respalda con
  `quotation.billing.identification_required`. El cálculo previo **no** lo exige: corre mientras
  la persona escribe.
- Enviar y convertir en pedido también lo exigen, con `quotation.billing.identification_required`
  (422). Es lo que ataja las cotizaciones guardadas antes de la columna, que tienen nombre y no
  número: se leen igual, pero no avanzan hasta que alguien lo cargue.
- El PDF lo imprime debajo del nombre en «Facturar a», y el Excel de pedidos lo lleva en
  `Documento de identidad` (ver abajo).

### Cotización en un paso: crear y calcular sin id

Desde el spec 2026-10-18 (`docs/superpowers/specs/2026-10-18-layout-documentos-design.md` §7) «Nueva cotización»
pinta el documento entero antes de que exista y lo crea de una vez. Son dos rutas bajo
`/api/v1/tenants/{tenantId}`, las dos con el permiso `quotations.quotation.manage` (el de siempre, sin permiso
nuevo) y el mismo cuerpo, `CreateQuotationRequest`:

| Método y ruta | Respuesta |
| --- | --- |
| `POST /quotations` | `201` + `Location` + `QuotationResponse`, en `Draft` con las líneas que traiga |
| `POST /quotations/preview` | `200` + `QuotationResponse` calculado **sin persistir**: `id` vacío (`00000000-…`), `quotationNumber: ""`, `version: 0` |

```json
{
  "clientId": "…",
  "validUntil": null,
  "paymentMethod": null,
  "notes": "…",
  "parties": { "billing": null, "shipping": { … }, "billingUsesBusinessName": false, "isStorePickup": false, "billsToFinalConsumer": false },
  "billingAccount": { "companyId": "…", "bankName": "…", "accountNumber": "…", "currency": "COP" },
  "shipping": { … },
  "items": [ { "variantId": "…", "quantity": 2 } ],
  "globalScaleFloor": null,
  "isRetail": false
}
```

- `items`, `globalScaleFloor` e `isRetail` son **opcionales**. Ausentes dan lo de antes: `{ clientId }` sigue
  creando un borrador vacío, en `version` 1 y con una sola fila `Created` en el historial. `items` lleva variante y
  cantidad, como el guardado (un producto sin opciones manda el id de su variante por defecto, que es el del
  producto); precio, escala, descuentos y tasa los resuelve el backend.
- Las reglas y los códigos son los del guardado (`PUT /quotations/{id}`): variante repetida (`errors.Items`), cantidad
  ≤ 0 o variante vacía (`errors["Items[i].VariantId"]`) dan `422 validation.failed`; detal con piso,
  `quotation.retail.floor_not_allowed`; una variante que no existe en el tenant, `quotation.item.product_not_found`; un modo de flete apagado,
  `quotation.quotation.freight_charge_mode_disabled`. La elegibilidad del cliente (`client_not_found`,
  `client_incomplete`, `client_cuc_missing`, `client_inactive`, en ese orden) aplica en las dos rutas. No se exigen
  líneas: eso lo pide la pantalla.
- **El cálculo previo no gasta número ni escribe nada**: ni consecutivo, ni historial, ni auditoría, ni outbox. Tampoco
  exige el documento de la facturación, porque corre mientras la persona escribe; crear sí lo exige.
- La vigencia ausente vuelve **resuelta** en las dos rutas: hoy del tenant + 15 días, la fecha que va a quedar.
- Crear con líneas corre en una transacción con el consecutivo: un 422 de una línea no gasta número. Deja lo mismo que
  crear y después guardar en el editor, sin la fila del encabezado: una fila `Created` y una `Edited` por línea en el
  historial, y `quotation.quotation.created` más un `quotation.quotation.item_added` por línea en la auditoría.
- **Despliegue: el backend va antes que el frontend** (D-L7). Un frontend nuevo contra un backend viejo crea
  cotizaciones sin líneas y sin error: el backend viejo ignora `items` y `/preview` le responde 405.

### Pedidos: datos propios y pedido manual

Desde el spec 2026-10-15 (`docs/superpowers/specs/2026-10-15-pedidos-sin-cotizacion-design.md`) un
pedido ya no es un apéndice de su cotización: `Order` lleva su propio cliente, asesor, cuenta de
cobro, partes (facturación y envío), líneas con la foto del producto y totales, calculados con las
mismas reglas que la cotización. `quotationId` es opcional y `source` dice de dónde nació:

- `Quotation`: `POST /quotations/{id}/order` **copia** la cotización al pedido. La cotización queda
  `Converted` y congelada; editar el pedido (`PUT /orders/{id}`, `POST /orders/{id}/items`)
  cambia `order_items`, nunca la cotización.
- `Manual`: `POST /orders` crea un pedido sin cotización desde la pantalla, con el mismo cuerpo que
  una cotización más los comprobantes (un `paymentStatus` en el cuerpo se ignora: el pago lo calcula
  el sistema); `POST /orders/preview` lo calcula sin
  guardarlo ni gastar número, y sin cuenta de cobro lo precia en la moneda por defecto del tenant. Crear
  exige la **empresa** emisora, no la cuenta (spec 2026-11-17 §4.2): `billingAccount` con `companyId` y
  `bankName`/`accountNumber` en `null` es una empresa sin cuenta, y el pedido sale en la moneda por
  defecto del tenant (el `currency` de ese cuerpo se ignora; elegir una cuenta es la forma de cambiarla).
  Banco y número van los dos o ninguno. Sin empresa sigue siendo `order.billing.account_required`.
  Exige cliente completo con CUC, como convertir. Permiso `quotations.order.manage`.
- Integraciones (`Shopify`): sin sesión HTTP, por `IExternalOrderWriter`; acepta un cliente
  incompleto y lleva `externalReference` (`#1001`).

Todos nacen `Open` y publican `quotations.order.created.v1` con `source` y `quotationId`
(nulo sin cotización). Pipeline lo consume: un pedido sin cotización gana el único negocio abierto
del cliente o crea uno ganado (`pipeline.deal_orders`). La lista (`GET /orders?source=`), el
detalle, el Excel y el reporte de pedidos leen del pedido. Los pedidos que existían antes los copió
la migración `AddOrderOwnData` desde su cotización.

#### Empresa sin cuenta e IVA por empresa

Spec `docs/superpowers/specs/2026-11-17-reglas-de-empresa-design.md`. Una empresa puede no tener cuentas
bancarias (`bankAccounts: []`) y dice si cobra IVA (`chargesVat`; ausente en el `POST` es `true`, ausente
en el `PUT` conserva el guardado).

- **Cuenta de cobro.** Cotizaciones y pedidos exigen la empresa para enviar, convertir y crear; la cuenta
  es opcional. Sin ella, `billingAccount.bankName` y `billingAccount.accountNumber` viajan en `null` en el
  detalle, la lista y las vistas previas, el PDF omite el bloque de pago y las celdas de forma de pago del
  Excel del ERP quedan vacías. «Pago: por definir» lo pinta el frontend.
- **IVA.** El documento congela `billingAccount.chargesVat` de su empresa al elegirla
  (`billing_company_charges_vat`). Con `false`, cada línea y el envío van al 0 %: el cliente paga el precio
  de lista y el envío nunca pide la tasa del tenant (`freight_tax_rate_missing`). La línea guarda la tasa
  del producto (`product_tax_percentage`) y la efectiva (`tax_percentage`), así que cambiar la empresa de un
  documento editable rehace las dos sin volver a Catalog; volver a una empresa con IVA con el envío cobrado
  sí pide la tasa del envío. Cambiar la empresa después no toca los documentos ya emitidos. En POS, la caja
  congela `company_charges_vat` al abrirse.
- **Tasas por defecto.** Crear una empresa colombiana que cobra IVA, o pasar una a «cobra IVA» en Colombia,
  siembra «IVA 19 %», «IVA 5 %» y «Exento» si el tenant no tiene ninguna tasa de IVA activa de `CO`, y fija
  la de 19 % como tasa del envío si estaba vacía (`CompanyDefaultTaxRates`, en Bootstrapper, bajo un
  advisory lock por tenant). Salta la que ya exista con el mismo nombre y porcentaje, activa o no. Corre
  después de guardar la empresa: si falla, queda un `Warning` y la empresa se guarda igual. Una prueba de
  integración que necesite el tenant sin tasa del envío la borra después de crear la empresa
  (`QuotationsApiHarness.ClearFreightTaxRateAsync`).

#### Cuenta de comprador del pedido

Spec 2026-11-15 §4 (`docs/superpowers/specs/2026-11-15-store-order-buyer-link-design.md`). Sólo pedidos con `source = Store`.

- El detalle (`GET /orders/{id}` y las respuestas de escritura que devuelven `OrderDetailResponse`) trae en la raíz `buyerAccount`:
  `{ "id", "email" }` o `null`. Es `null` sin cuenta y también si la cuenta se borró: borrarla no limpia `buyer_account_id` del pedido.
  El listado no lo trae.
- `PUT /api/v1/tenants/{tenantId}/orders/{orderId}/buyer-account` con `{ "email": "…" }` enlaza el pedido a la cuenta **verificada** de ese
  correo (las cuentas son de la plataforma, no del tenant). `DELETE` en la misma ruta la quita; sobre un pedido sin cuenta también vale y
  significa «no lo enlaces solo».
- Los dos: permiso `quotations.order.manage`, `If-Match` con la versión del pedido (sin él `428 precondition.if_match_required`, viejo
  `412 concurrency.conflict`), responden `200` con el `OrderDetailResponse` completo y el `ETag` nuevo. Suben la versión, ponen la marca
  `buyer_account_set_by_member` y auditan `quotation.order.buyer_account_changed` con el id anterior y el nuevo, nunca correos. Además
  dejan en el historial del pedido una entrada `BuyerAccountChanged` con el asesor como autor, cuyo texto sí nombra el correo anterior y
  el nuevo («Cambió la cuenta de comprador de … a …»); los enlaces automáticos no escriben historial. Un `PUT` con la misma cuenta y la
  marca ya puesta responde `200` sin cambiar nada, sin historial ni auditoría.
- Errores: `422 validation.failed` (`Email` vacío, inválido o de más de 254 caracteres), `422 order.buyer_account.not_found` (no hay cuenta
  con ese correo o no está verificada: un solo código para los dos), `422 order.order.source_invalid` (el pedido no es de la tienda), `403`
  (sin permiso o de otro tenant, nunca 404).

#### La entrega editable

Spec 2026-11-24 §10 y §11 (`docs/superpowers/specs/2026-11-24-perfiles-de-facturacion-backend-handoff.md`). En **todo origen**.

- `PUT /orders/{id}` y `POST /orders/{id}/preview` aceptan
  `delivery: { "isStorePickup": bool, "shipping": QuotationPartyRequest | null }`. **Ausente conserva** la entrega (un frontend
  viejo no la toca). `shipping: null` con envío es la dirección del cliente. Pasar a recoger quita la dirección y limpia el
  envío, contraentrega incluida. Al guardar, una parte propia debe venir completa; el cálculo previo la acepta a medias.
- El pedido responde `order.deliveryEditable`: `true` mientras esté abierto, sin preparar, sin facturar y, si es mayorista, sin
  aprobar.
- Un cambio deja en el historial una fila `PartiesChanged` y la auditoría `quotation.order.delivery_changed`.
- Errores (422): `order.order.already_invoiced`, `order.order.already_prepared` (facturado gana) y `order.order.not_editable`;
  `order.delivery.shipping_party_on_pickup`, `order.delivery.address_required` (de recoger a los datos de un cliente sin
  domicilio), `order.shipping.party_incomplete` (sólo al guardar), `quotations.party.region_invalid` con
  `errors["Delivery.Shipping.RegionId"]` y `quotations.party.postal_code_too_long` con
  `errors["Delivery.Shipping.PostalCode"]`.
- **Pedido de Shopify** (§11.1). Cuando, enviando, cambia la parte propia, se rechaza lo que el envío a la tienda no podría
  mandar, también en el cálculo previo: `order.delivery.shopify_party_required` (volver a la dirección del cliente),
  `order.delivery.shopify_region_required` (España sin región) y `order.delivery.shopify_region_unmapped`. **El código postal
  nunca es obligatorio** (owner, 2026-11-25).
- **Evento** `quotations.order.shipping-party-changed.v1`, payload `{ tenantId, orderId, source, occurredAt }`: al outbox en la
  transacción del pedido, sólo en un pedido de Shopify, enviando y con la parte propia cambiada. Lo consume Shopify, que encola
  el envío de la dirección (ver [Integración con Shopify](#integración-con-shopify)). Recoger no publica nada.

#### Los cuatro ejes del pedido

Desde el spec 2026-11-10 el pedido tiene cuatro ejes independientes, al estilo de Shopify. Viajan por
la API con su nombre:

| Eje | Campo | Valores | Quién lo mueve |
| --- | --- | --- | --- |
| Estado | `status` | `Open`, `Archived`, `Cancelled` | `archive`/`unarchive` (`.manage`), `cancel` (`.cancel`), el archivado automático |
| Pago | `paymentStatus` | `PaymentPending`, `PartialPaymentReceived`, `PendingVerification`, `FullPaymentReceived` | el sistema con los comprobantes; `mark-paid`/`unmark-paid` (`.approve`); Shopify |
| Envío | `fulfillmentStatus` | `NotPrepared`, `Prepared` | `prepare`/`unprepare` (`.manage`) |
| Facturado | `invoiced` (+ `invoicedAt`, `invoicedBy`) | marca | `invoice`/`uninvoice` (`.invoice`) |

- **El pago lo calcula el sistema.** Sin comprobantes es `PaymentPending`; con menos que el neto,
  `PartialPaymentReceived`; con lo que lo cubre (comparado como lo muestra la moneda: pesos enteros en
  COP), `PendingVerification` («Por verificar»). `FullPaymentReceived` («Pagado») sólo se alcanza con
  `mark-paid`, que deja `paidMarkedAt`/`paidMarkedBy`, o con el pago que sincroniza Shopify en un pedido
  sin comprobantes. Quitar un comprobante de un pedido pagado (por `DELETE …/proofs/{id}` o por
  `proofs.removeIds` del `PUT`), o corregir su monto (`proofs.update` del `PUT` o `updatedProofs` de
  `POST …/proofs`), exige `.approve`: con sólo `.manage` es `403`. `balance` es neto menos
  consignado (negativo es sobrante).
- **Editable** es `Open`, sin preparar y sin facturar (`Order.IsEditable`). Pagado sigue siendo editable;
  preparado congela el pedido. Fuera de eso, `PUT`, `preview`, `items` y `proofs` responden
  `422 order.order.not_editable`. Anular desde la consola tampoco deja un preparado
  (`order.order.already_prepared`) ni un archivado; la anulación que llega de Shopify sí gana sobre los
  dos y conserva la marca de preparado.
- **Facturar es una marca, no un estado**: `status` no cambia. Por defecto exige pagado
  (`order.order.not_paid`); no exige preparado.
- **El listado oculta los archivados** si no se pide `status`. Filtros nuevos: `fulfillmentStatus` e
  `invoiced` (`true`/`false`), también en la exportación.
- **Pedidos de proyecto** (spec 2026-11-26): cada pedido y cada fila traen `kind` (`Standard` \| `Project`) y el
  detalle, `order.project`. `GET /orders` y `export` aceptan `kind` (`Standard`, `Project` o `All`; sin valor, todos;
  otro es `422 validation.failed` con `errors.kind`). En un pedido `Project`, `PUT` y `preview` aceptan `items`
  ausente, vacío o con la línea tal cual, e ignoran `isRetail`, `globalScaleFloor` y `shipping`; cambiar la línea,
  mandar regalos o `POST …/items` es `422 order.project.items_locked`. No exigen el módulo `projects`.
- **Ajustes por tenant** en `GET`/`PUT /orders-settings` (`tenancy.settings.read` / `.update`; los tres
  campos son obligatorios en el `PUT`):

  | Campo | Por defecto | Efecto |
  | --- | --- | --- |
  | `requirePaymentProofToMarkPaid` | `false` | `mark-paid` exige al menos un comprobante (`order.order.payment_proof_required`) |
  | `requireFullPaymentToInvoice` | `true` | `invoice` exige pagado (`order.order.not_paid`) |
  | `autoArchive` | `false` | un pedido que queda pagado y preparado se archiva solo, con historial «Archivado automáticamente» |

- Cada acción deja historial (`PaidMarked`, `PaidUnmarked`, `Prepared`, `PreparationUndone`, `Archived`,
  `Unarchived`) y auditoría (`quotation.order.paid_marked`, `…paid_unmarked`, `…prepared`,
  `…preparation_undone`, `…archived`, `…unarchived`). `POST …/approve` ya no existe; el permiso
  `quotations.order.approve` conserva su nombre y ahora se llama «Marcar pedidos como pagados».
- **Migración `OrderStatusAxes`:** `Pending`, `Approved` e `Invoiced` pasan a `Open` (la marca de facturado
  queda); quien aprobó un pedido pagado queda como quien lo marcó pagado; un `FullPaymentReceived` que nadie
  aprobó vuelve a lo que digan sus comprobantes (`PaymentPending`, `PartialPaymentReceived` o
  `PendingVerification`, con el neto redondeado como lo muestra la moneda), salvo los de Shopify sin
  comprobantes, cuyo pago lo dice la tienda. **Es sin pérdida:** `approved_at` y `approved_by` se quedan como
  columnas históricas, y el estado y el pago de antes quedan respaldados en `legacy_status` y
  `legacy_payment_status`. Ninguna de las cuatro está en el modelo de EF; todas llevan un `COMMENT` que lo dice.
  Para revertir, baja a `AddOrderBuyerAccount` con `$env:ConnectionStrings__QepDatabase` apuntando a la base
  (`dotnet ef database update AddOrderBuyerAccount --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext`):
  cada pedido anterior vuelve exacto a su estado y su pago, y el `Down` borra lo que agregó el `Up`. Un pedido
  creado después no tiene respaldo y se traduce al esquema viejo (spec 2026-11-10 §8).

**Rollback.** El código anterior no sabe escribir las siete columnas que `AddOrderOwnData` deja
`NOT NULL` sin default: sin este paso, toda conversión falla con `23502`. Se corre **antes** de
arrancar el código viejo:

```sql
ALTER TABLE quotations.orders
    ALTER COLUMN client_id DROP NOT NULL,
    ALTER COLUMN advisor_id DROP NOT NULL,
    ALTER COLUMN currency DROP NOT NULL,
    ALTER COLUMN billing_company_id DROP NOT NULL,
    ALTER COLUMN billing_bank_name DROP NOT NULL,
    ALTER COLUMN billing_account_number DROP NOT NULL,
    ALTER COLUMN billing_account_currency DROP NOT NULL;
```

Con el código viejo, un pedido manual o externo da 500 al leerlo, y los pedidos que se conviertan
nacen sin datos propios. Antes de volver al código nuevo, esos pedidos se completan con el SQL del
pasos 4 y 5 de `AddOrderOwnData` acotado a `client_id IS NULL` y se restauran los `NOT NULL` (spec
2026-10-15 §10).

**Pagado y costo congelado.** `Order.PaidAt` (columna `paid_at`) es el instante en que el pedido llegó a pago completo (nulo si no está pagado); no viaja en ninguna respuesta de la API, es de uso interno del reporte de margen. Con él, el costo unitario de cada línea (`unit_cost`) se congela desde el costo de la variante en ese momento y no cambia si la variante se edita después. Toda vía a `FullPaymentReceived` pasa `OrderLineCosts` al dominio.

### Reporte de margen

`GET /api/v1/tenants/{tenantId}/reports/margin` y `GET …/reports/margin/summary` (permiso `reporting.margin.read`) leen el costo **congelado** en cada línea vendida (`unit_cost`), de `quotations.order_items` y `pos.sale_lines`, por producto y variante. Una línea sin costo no suma al margen y se informa aparte. `MarginReportSource` (Bootstrapper) es SQL crudo, como `OrdersReportSource`.

### Reporte de pedidos: pedidos y ventas POS

`GET /api/v1/tenants/{tenantId}/reports/orders` y `GET …/reports/orders/summary` leen **la unión** de
`quotations.orders` y `pos.sales` (spec 2026-10-23): una venta POS es una fila más, con `source = "Pos"`.
`OrdersReportSource` (Bootstrapper) la arma con SQL crudo y un `UNION ALL`, porque las dos tablas son de
`DbContext` distintos; todo se filtra, pagina y agrega en la base.

- **Filtro `source`**, en el listado y en el resumen: `Quotation`, `Manual`, `Shopify` o `Pos`. Sin él vienen
  los cuatro. Un valor desconocido (también `pos` en minúscula) es `422 validation.failed` con `errors.Source`.
- **`bySource`** en el resumen: una entrada por origen, siempre las cuatro y en ese orden, con conteo y total por
  moneda, en cero si no hay (con `source` puesto, las otras tres vienen en cero).
- **`withoutClient`** en el resumen: las ventas POS a consumidor final, que no tienen ficha de cliente. No entran
  a `byClient` ni a su fila «Otros», así que `byClient` + `withoutClient` = `totals`. En el listado esas filas
  traen `clientId = null`, `clientName = "Consumidor final"` y `clientCuc = null`.
- **Lo anulado no suma**: un pedido `Cancelled` y una venta `Voided` no cuentan en ningún agregado ni en el período
  anterior, pero se ven en el listado con su estado. El estado de una venta es `Completed` o `Voided`, y su
  estado de pago siempre `FullPaymentReceived`: por eso `paymentStatus` con cualquier otro valor
  (`PartialPaymentReceived`, `PaymentPending` o `PendingVerification`) la excluye.
- **El cajero entra como asesor**: `cashier_id` es una membresía, igual que el `advisor_id` del pedido. El filtro
  `advisorId`, el ranking por asesor y el alcance de quien no tiene `reporting.all_advisors.read` valen para los
  dos. No cambia ningún permiso.
- Una venta POS no tiene envío (`freightSubtotal` y `freightTaxAmount` en 0) ni cotización (`quotationId` nulo), y
  su fecha (`convertedAt`) es la de creación.

### Enviar el pedido por WhatsApp

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/send` le manda al cliente el PDF del pedido por
WhatsApp, como `POST /quotations/{id}/send` lo hace con la cotización. Contrato completo y códigos de
error en [`docs/integracion-cotizaciones-y-pedidos.md`](docs/integracion-cotizaciones-y-pedidos.md).

- **Permiso** `quotations.order.manage`. **Cuerpo opcional** `{ "recipient": "Customer" | "Billing", "connectionId": "…" }`
  (default `Customer`): el teléfono lo resuelve el backend — el del cliente, o el de la facturación
  propia del pedido (`order.order.billing_phone_missing` si no la tiene). `connectionId` es la conexión elegida
  cuando hay varias disponibles para pedidos (spec 2026-11-09 D8).
- **Responde el `OrderDetailResponse` entero**, con la entrada `Sent` en el historial.
- **No cambia el pedido**: ni estado ni `version`. Se manda abierto o archivado, pagado o no, preparado
  o facturado; un anulado responde `order.order.already_cancelled` antes de cualquier efecto externo. Deja historial
  (`OrderHistoryEventType.Sent`, que se guarda como texto: sin migración) y auditoría
  `quotation.order.sent`. No exige cliente completo: un pedido de Shopify puede tenerlo incompleto.
- **El PDF es el de la cotización** (`quotation.typ`, layout único por decisión del owner) con
  `kind: "Order"`: dice «Pedido» en el membrete y el pie, no imprime «Válida hasta» y el archivo se
  llama `Pedido-<número>.pdf`. Sale del detalle del pedido (`QuotationPdfDocumentMapper.FromOrder`),
  nunca de la cotización de origen, que puede no existir.
- **Sin caché ni migración**: se genera en cada envío y se guarda bajo `quotations/tenants/…/orders/`
  (lo limpia la misma regla de lifecycle); la copia pública sólo se publica para Zenvia, como en la cotización
  (Meta lee el PDF privado por su clave). Cachearlo pediría una tabla invalidada por `Order.Version`, y enviar
  un pedido es raro.
- **Sin registro propio de fallas**: el log de fallas de la API guarda la excepción, y `order.send.failed`
  nombra el paso.
- **Sale por el canal del tenant, como la cotización** (spec 2026-11-09, cierra DP-1 y DP-2). Los canales
  disponibles para pedidos (D6) son: Meta con la plantilla de sistema `qep_pedido` **APPROVED**
  (`MessageTemplateKind.Order`, se aprovisiona con `POST …/template-management/order`, ver
  [Plantillas de WhatsApp desde QEP](#plantillas-de-whatsapp-desde-qep)) y Zenvia con token, número, `templateId`
  **y** `orderTemplateId` (ver [Plantilla de WhatsApp](#plantilla-de-whatsapp-zenvia)). Una Zenvia sin
  `orderTemplateId` simplemente no está disponible para pedidos: `order.whatsapp.not_configured` ya no sale de la
  selección (sigue como chequeo defensivo del sender de Zenvia). La selección (D7) es la misma que la de la
  cotización, con prefijo `order.whatsapp.`: sin canal `no_channel`; varios sin elección guardada
  `channel_required`; elección guardada disponible → sale por ella (otro `connectionId` es `channel_already_set`);
  elección guardada **no disponible para pedidos** → se ignora; `connectionId` que no está en la lista →
  `channel_unavailable`. La elección es una por tenant y **compartida** con la cotización: un envío de pedido con
  `connectionId` la guarda igual (misma auditoría, misma transacción, nunca si el envío falla). La pantalla pregunta
  por los canales de pedido con `GET /quotations/whatsapp-channels?document=order`.
- **Por Meta** (D9): la fila `Order` aprobada se relee al enviar (`order.whatsapp.send_failed (template_not_approved)` si
  ya no está), el PDF viaja como encabezado de documento con el nombre `Pedido-<número>.pdf` y el mensaje queda en el
  hilo de Mensajería con las variables `nombre_contacto`, `numero_pedido`, `valor_total` (más `asesor`, `empresa` y
  `saludo` si la plantilla las usa).

### Catálogo: opciones, variantes y galería

Las **categorías de producto** (spec 2026-10-26) son un árbol por tenant, de profundidad máxima 10. Un producto
tiene a lo sumo una (`categoryId`, que viaja en todas las respuestas de producto; en el `PUT`, `null` la limpia).
El nombre (máx. 80) es único entre hermanas sin distinguir mayúsculas, también entre dos raíces. Errores:
`catalog.category.name_required`, `catalog.category.name_too_long`, `catalog.category.parent_not_found`,
`catalog.category.too_deep`, `catalog.category.name_taken` y, al asignarla a un producto,
`catalog.product.category_not_found`. `GET /catalog/categories` trae el `productCount` de cada una: sólo los
productos directos, activos e inactivos.

Spec 2026-10-26-categorias-pos-acceso-rapido. `PUT /catalog/categories/{id}` (`{ name, parentId }`) renombra y/o
mueve con todo su subárbol, y `DELETE /catalog/categories/{id}` (204) borra; los dos exigen `If-Match` con la
`version` (428 `precondition.if_match_required` sin él, 412 `concurrency.conflict` si es vieja) y responden 404
`catalog.category.not_found`. Mover debajo de sí misma o de una descendiente es 422 `catalog.category.cycle`, y
`catalog.category.too_deep` mira la hoja más honda del subárbol movido. Borrar una con productos (activos o no)
o con hijas es 422 `catalog.category.has_products` / `catalog.category.has_children`: se mueven primero. Las FK
`RESTRICT` siguen como última red, y `CatalogUnitOfWork` traduce su carrera a los mismos códigos.
`PUT /catalog/categories/pos-quick-access` (`{ categoryIds }`, sin `If-Match`) reemplaza la lista ordenada de chips
de la caja: el índice es la posición (`posQuickAccessPosition`, 1..N contiguas; `null` sin fijar), un id repetido
es 422 `catalog.category.quick_access_duplicate` y uno desconocido o ajeno 422 `catalog.category.not_found`; responde
la lista completa. Fijar no sube la `version` de la categoría. Borrar una fijada la saca y recompacta las demás.

Spec 2026-10-20 (`docs/superpowers/specs/2026-10-20-variantes-de-producto-design.md`). Un producto tiene
hasta **3 opciones** (Talla, Color…) y hasta **2.048 variantes**, una por combinación de valores, como
Shopify. El código, el código de barras, los precios, las escalas y los empaques son de **cada variante**;
el nombre, la descripción, el IVA y el estado siguen en el producto. Un producto sin opciones tiene
exactamente una variante, la **variante por defecto**, con el mismo id que el producto: así nacieron los
productos que existían antes (migración `AddProductVariants`) y así nace uno nuevo. La galería guarda
hasta **250 fotos** en orden —la primera es la principal— y cada variante puede apuntar a una.

Todas las rutas cuelgan de `/api/v1/tenants/{tenantId}/catalog`, con `catalog.product.read` para leer y
`catalog.product.manage` para escribir (sin permisos nuevos):

| Método y ruta | Para qué |
| --- | --- |
| `GET /options` | La biblioteca de opciones del tenant, con `canDelete` por opción y por valor. El primer `GET` siembra «Talla» y «Color» **inactivas** (una sola vez: una predefinida borrada no vuelve) |
| `POST /options` | Crea una opción activa con sus valores |
| `PUT /options/{optionId}` | Reemplaza nombre, estado y la lista de valores en orden; un valor guardado que no viene se borra si ninguna variante lo usa (si alguna lo usa, `409 catalog.option.in_use`) |
| `DELETE /options/{optionId}` | Borra una opción sin uso; en uso responde `409 catalog.option.in_use` y no cambia nada (se desactiva por el `PUT`) |
| `GET /products` | El listado sigue siendo por producto: `code` (el de la única variante, `null` con varias), `variantCount` y `priceRange` por moneda. Con `includeVariants=true` trae además `variants[]`, las vendibles, para el selector de cotizaciones y pedidos (página topada en 50). `search` encuentra por nombre, `Code` de variante o código de barras |
| `GET /products/{productId}` | El detalle: `options[]`, **todas** las `variants[]` en orden (con nombre visible, `canDelete` e `imageUrl` ya resueltos), `gallery[]` y `version` |
| `POST /products` | Crea producto, opciones, variantes y galería de una vez |
| `PUT /products/{productId}` | Reemplaza el recurso entero con `expectedVersion` (`412 concurrency.conflict` si cambió). Las variantes se emparejan por `id`; una guardada que no viene responde `422 catalog.variant.missing_in_request`: **omitir no borra**. Borrar es mandar su id en `deletedVariantIds`, y se borra si ninguna línea la usa o se desactiva si alguna sí |
| `POST /products/price-scales/copy` | Copia las escalas de una variante (`sourceVariantId`) a otras (`targetVariantIds`, hasta 200 variantes) |

- **Nombre visible**: el del producto en la variante por defecto; con opciones,
  `«{producto} – {valor1} / {valor2} / {valor3}»` (guion largo con espacios). Lo arma el backend y es lo
  que guardan las líneas, el PDF, el Excel y el POS.
- **Códigos únicos por tenant**: `Code` (≤ 60) entre todas las variantes, activas o no —desactivar no
  libera el código, índice `IX_product_variants_tenant_code`—; el código de barras (≤ 64) es opcional y
  único cuando existe (`IX_product_variants_tenant_barcode`).
- **Cambiar las opciones de un producto con ventas** conserva la variante: al agregar la primera opción,
  la variante por defecto se convierte en una de las combinaciones nuevas con su id, código, precios e
  historial; las demás nacen copiando su precio. Al quitar una opción, las combinaciones que quedan
  repetidas se desactivan.
- **En uso** = alguna línea de `quotations.quotation_items`, `quotations.order_items` o `pos.sale_lines`
  guarda su `variant_id`. Lo consulta `CatalogVariantUsageChecker` (Bootstrapper) dentro de la
  transacción del `PUT`. Borrar una variante sin uso borra en cascada su historial de precios.
- **Escalas por variante**: una escala sin agrupación cuenta sólo la cantidad de su variante (6 M y 6 L
  no suman 12); una con `allowGrouping` suma entre variantes, como antes sumaba entre productos.
- **Costo por variante**: `costs` es un mapa por código de moneda (p. ej. `{ "COP": 12000 }`), sin conversión entre monedas. En la respuesta de cada variante siempre está presente: `null` = el llamador no tiene `catalog.product_cost.read` (`manage` también cuenta como lectura) y `{}` = la variante no tiene costo. En el `POST/PUT /products`, `null` o ausente conserva el costo guardado y un objeto lo reemplaza (`{}` lo borra). Escribirlo exige `catalog.product_cost.manage`; sin él, 403 `catalog.product_cost.forbidden`. Cada monto es > 0 y con máximo 2 decimales; si no, 422 bajo `Variants[i].Costs.<CUR>`.
- **Historial de precios** por variante: `GET /reports/price-changes` y `/summary` aceptan `variantId`, y
  el resumen desglosa cada producto en `variants[]`, todas incluso en cero.
- **Excel de productos**: una fila por variante (activas e inactivas), con `Código`, `Código de barras`,
  `Producto` y `Variante` (vacía en la variante por defecto).

Los errores llegan por dos caminos, y el formulario lee los dos con el mismo mapa `errors`:

- **De forma** (lo que se ve en el cuerpo, sin ir a la base): `422 validation.failed`, con el mensaje bajo
  la clave del campo, por ejemplo `errors["Variants[2].Code"]`. Así salen más de 3 opciones o
  una repetida, más de 250 fotos o una repetida, ninguna variante o más de 2.048, el mismo id dos veces en
  `variants`, un `Code` o un código de barras repetido **dentro del cuerpo**, una variante sin un valor por
  opción, una foto de variante que no está en `gallery`, `Code` vacío o de más de 60, código de barras de
  más de 64 y los de precio, escala y empaque. En la opción: nombre o valor vacío, de más de 60 o repetido, y
  en el `PUT` un `isActive` ausente en la opción o en un valor (`errors["IsActive"]`, `errors["Values[i].IsActive"]`):
  omitirlo no desactiva.
- **Contra la base**: con su propio `code` y, cuando señala una fila, también `errors` con el campo:

| Código | Status | Cuándo |
| --- | --- | --- |
| `catalog.option.name_taken` | 422 | Ya existe una opción con ese nombre en el tenant (sin distinguir mayúsculas ni espacios de borde) |
| `catalog.option.not_found` | 404 / 422 | `404` en `/options/{optionId}`; `422` con `errors["OptionIds[i]"]` cuando el producto nombra una opción que no es del tenant |
| `catalog.option.in_use` | 409 | Borrar una opción, o quitar un valor, que alguna variante usa |
| `catalog.option.inactive` / `value_inactive` | 422 | Usar en un producto una opción o un valor inactivo que ese producto no usaba |
| `catalog.option.value_not_found` | 422 | Un valor que no es de esa opción (`errors["Variants[i].OptionValueIds[j]"]`), o un id de valor desconocido o repetido en el `PUT` de la opción |
| `catalog.variant.combination_duplicate` | 422 | Dos variantes activas con la misma combinación |
| `catalog.variant.active_required` | 422 | Ninguna variante activa |
| `catalog.product.code_taken` | 422 | El `Code` ya es de una variante de otro producto (`errors["Variants[i].Code"]`) |
| `catalog.variant.barcode_taken` | 422 | El código de barras ya lo usa otra variante del tenant: el índice es único en todo el tenant (`errors["Variants[i].Barcode"]`) |
| `catalog.variant.missing_in_request` | 422 | El `PUT` omite una variante guardada que no está en `deletedVariantIds` |
| `catalog.variant.duplicated_in_request` | 422 | Un id en `variants` y en `deletedVariantIds` a la vez, o repetido en `deletedVariantIds` |
| `catalog.variant.not_found` | 422 / 404 | Un id de variante que no es del producto; `404` en la copia de escalas |
| `concurrency.conflict` | 412 | `expectedVersion` vieja en el `PUT` del producto |

Los códigos de antes se conservan; `catalog.product.code_taken` ahora habla del `Code` de la variante.
El contrato completo para el frontend, con los cuerpos y las respuestas, está en
`docs/superpowers/plans/2026-10-20-variantes-handoff.md`.

**Despliegue: con ventana de corte, backup previo y junto con el frontend.** `AddProductVariants` (Catalog) es
destructiva: borra `catalog.products.code`, `image_file_id` y `packaging_units` y renombra `product_id` → `variant_id`
en `catalog.product_prices` y `catalog.product_price_scales`. `AddVariantToLines` (Quotations) y `AddVariantToSaleLines`
(Pos) dejan `variant_id` `NOT NULL` en las tres tablas de líneas. Las tres corren al arrancar el pod nuevo, y con el
`RollingUpdate` de `k8s/prod-deployment.yaml` (`maxSurge: 1`, `maxUnavailable: 0`) el pod viejo sigue atendiendo
contra el esquema nuevo y responde 500. Para esta versión: backup de la base (como mínimo `catalog`, `quotations` y
`pos`), backend a cero réplicas o estrategia `Recreate`, y backend y frontend en la misma ventana. **Rollback**, con el
código de esta versión y el backend detenido, en orden **Pos → Quotations → Catalog** (`AddPosCurrency`,
`AddOrderExternalKey`, `AddProductPrices`); el `Down` de Catalog pierde las variantes que no son la que vuelve, con sus
precios y escalas, los códigos de barras, la galería salvo la principal y la biblioteca de opciones (el historial de
precios se queda en el producto, sin la variante). Los comandos y el detalle están en el § 13 del handoff.

### Código y nombre del producto en cada línea

Una línea de cotización vende una **variante** (spec 2026-10-20 §4): guarda `variant_id` y el
`product_id` de su producto (los dos sin FK) y congela precio e IVA al agregarse. El **código y el
nombre** son el `Code` y el nombre visible de la variante (`Camiseta – M`; el del producto en la
variante por defecto), y siguen otra regla (owner, 2026-09-26):

- Mientras la cotización es **borrador**, se leen en vivo del catálogo: si el producto cambia, la
  cotización lo muestra.
- Cuando **sale del borrador**, cada línea guarda el código y el nombre que tenía su producto en ese
  momento (`quotation_items.product_code` / `product_name`), y desde ahí la cotización y su pedido
  ya no cambian con el catálogo. Sale del borrador al **enviarla** y también al **convertirla en
  pedido** sin haberla enviado. Al convertir, la foto viaja con la línea a `order_items`; un pedido
  manual o externo congela la suya al crearse.
- Lo congelado no se pisa nunca: ni un reenvío ni la conversión lo vuelven a leer. Sí se completan
  las líneas que todavía no lo tienen: las agregadas a una cotización ya enviada se congelan en el
  siguiente envío (o al convertir), y las que se suman a un pedido pendiente
  (`POST /orders/{id}/items`, `PUT /orders/{id}`) se congelan al sumarlas.
- Un producto que el catálogo ya no devuelve no frena el envío: esa línea queda sin foto y se sigue
  leyendo en vivo, como antes.
- El nombre visible se guarda hasta 200 caracteres: uno más largo (producto de 200 más los valores de
  opción) se recorta a 200 con «…» al congelarse (`SnapshotText.Fit`); el borrador, que lee en vivo, lo
  muestra entero. El POS guarda el suyo con la misma regla.
- Las líneas enviadas antes de este cambio no tienen foto y se siguen leyendo en vivo. No hay
  backfill masivo, porque Quotations no lee las tablas de Catalog: la foto se completa **al borrar el
  producto** (`DELETE /catalog/products/{productId}`), que congela código y nombre de las líneas que
  todavía no la tienen (cotizaciones que no son borrador y pedidos) en la misma transacción del borrado.
  Las demás se congelan solas si la cotización se reenvía o se convierte. En un borrador la línea del
  producto borrado desaparece y la cotización se recotiza.
- Una cotización anulada desde borrador no congela nada (ya no se usa ni en pedidos ni en el
  Excel). Ninguna transición devuelve una cotización a borrador.

La regla vive en `QuotationItemProductLabel` (cotización y PDF) y en su espejo del pedido,
`OrderItemProductLabel` (respuesta de pedido y `Cod. Producto` del Excel de pedidos). La forma
de la respuesta no cambió: `items[].productCode` y `items[].productName` siguen ahí; lo que cambia
es de dónde salen. Portada y escalas de la línea siguen siendo las de hoy.

### Transportadoras, envío y contraentrega

Spec 2026-10-17 (`docs/superpowers/specs/2026-10-17-flete-design.md`). Una cotización y un pedido pueden
llevar la transportadora, el valor del envío y la marca de contraentrega. **Nada de eso es obligatorio**:
ni para enviar la cotización, ni para crear o convertir un pedido, ni la transportadora para la
contraentrega.

**Transportadoras** (`CarrierEndpoints`, bajo `/api/v1/tenants/{tenantId}`; lectura con
`TenancyPermissions.SettingsRead`, escritura con `TenancyPermissions.SettingsUpdate`):

| Método | Ruta | Qué hace |
| --- | --- | --- |
| `GET` | `/carriers` | La tasa del IVA del envío, **todas** las transportadoras, activas e inactivas, en orden, y `freightChargeModes` |
| `POST` | `/carriers` | Crea una (`name`, `externalNames`, `supportsCashOnDelivery`); responde 201 con `ETag` |
| `PUT` | `/carriers/{carrierId}` | Edita; exige `If-Match` con la `version` cargada (428 sin él, 412 si cambió) |
| `POST` | `/carriers/{carrierId}/activate` y `/deactivate` | Prende o apaga |
| `PUT` | `/carriers/order` | Reordena con la lista completa de ids (`carrierIds`) |
| `PUT` | `/carriers/freight-tax-rate` | Fija la tasa del IVA del envío (`taxRateId`); con `taxRateId: null` la quita |
| `PUT` | `/carriers/freight-charge-modes` | Las opciones de «¿Quién paga el envío?» que quedan activas (`enabledModes`) y la por defecto (`defaultMode`, opcional); responde como `GET /carriers` |
| `GET` | `/shipping-options` | Para el cotizador y el pedido (permiso de leer cotizaciones): sólo las activas, la tasa, `freightChargeModes` y `defaultFreightChargeMode` |

- **Las transportadoras no se borran, se desactivan.** Un documento guarda la foto del nombre al elegirla y
  no pierde lo que ya eligió si la transportadora se apaga después; una desactivada no se puede elegir en
  un guardado nuevo.
- **El nombre y los nombres externos (`externalNames`, cómo la llama el ERP) comparten un solo índice
  único por tenant**, normalizado y **con las inactivas incluidas**. Un choque responde
  `quotations.carrier.name_taken`.
- **El IVA del envío** es una tasa de Catalog que el tenant elige una sola vez. Sólo se acepta una tasa
  **activa** (`quotations.freight_tax_rate.inactive`, o `.not_found` si no es del tenant). Vive aparte de
  `PUT /quotations/settings` para que guardar el mínimo de compra no pueda pisarla. La pantalla de tasas de
  impuesto se mueve de Catálogo a **Configuración** (cambio de `qep-frontend`; la API de tasas no cambió).
- **Quién paga el envío se activa por tenant** (§4.4). `freightChargeModes` viaja en `GET /carriers` y en
  `GET /shipping-options` como `[{ mode, isEnabled }]`, **siempre las tres** (`Charged`, `OnDelivery`,
  `Free`, en ese orden) aunque estén apagadas. Las tres nacen activas: sin fila o sin tocar, todas. Se fija
  con `PUT /carriers/freight-charge-modes` y `{ "enabledModes": ["Charged", "Free"] }`; las que no vienen se
  apagan, la lista vacía las apaga todas, un nombre repetido se tolera y uno desconocido es `422
  validation.failed` con `errors["EnabledModes[i]"]` (sin `enabledModes` también, en `errors["EnabledModes"]`).
  Elegir o cambiar a un modo apagado al crear, guardar o previsualizar responde
  `…freight_charge_mode_disabled`; el documento que ya lo tenía lo conserva mientras no lo cambie, y
  quitarlo (`null`) siempre se puede. No aplica a la conversión (copia lo de la cotización) ni a Shopify. Se
  guardan las apagadas en `tenant_quotation_settings.disabled_freight_charge_modes`, aparte del mínimo de
  compra, como la tasa.
- **La opción por defecto de quién paga** (§4.4.1). `defaultFreightChargeMode` viaja en `GET /carriers` y en
  `GET /shipping-options`: `null` (el valor inicial) o el nombre de una opción **activa**. Se fija en el mismo
  `PUT /carriers/freight-charge-modes` con `defaultMode`, que tiene tres estados: **omitido** conserva el
  actual, `null` lo quita y un nombre lo fija — `{ "enabledModes": ["Charged", "Free"], "defaultMode": "Free" }`.
  Un nombre que no está en `enabledModes` (o desconocido) es `422 validation.failed` con `errors["DefaultMode"]`.
  Apagar la opción que era la por defecto la quita en el mismo guardado y el mismo evento de auditoría. Lo
  aplica **el formulario**, no el backend (D-F19): ningún documento recibe el modo por defecto al crearse.
  Vive en `tenant_quotation_settings.default_freight_charge_mode`, con un `CHECK` que impide que sea una de las
  apagadas.
- Mutar una transportadora, la tasa o quién paga el envío deja fila de auditoría en la misma transacción
  (`quotations.carrier.created|updated|activated|deactivated|reordered`,
  `quotations.freight_tax_rate.updated`, `quotations.freight_charge_modes.updated`); repetir lo que ya
  estaba no audita.

**El objeto `shipping`.** Los cuerpos de crear, guardar y previsualizar cotización y pedido aceptan
`shipping: { carrierId, freightChargeMode, freightAmount, cashOnDelivery }`, y las respuestas devuelven
`shipping` con forma fija, también en recogida:
`{ isStorePickup, carrier: { id, name } | null, chargeMode, freightAmount, freightTaxPercentage, freightSubtotal, freightTaxAmount, cashOnDelivery }`.
- `freightChargeMode` viaja con el nombre del enum: `Charged` (se cobra al cliente y suma al total),
  `OnDelivery` (lo paga el cliente a la transportadora: el valor es informativo y **no suma**) o `Free`
  (valor 0). `null` es «sin envío registrado».
- `freightAmount` es el valor **con IVA incluido**. El backend lo desglosa con el mismo redondeo de una
  línea: $ 15.000 al 19 % da `freightSubtotal` 12.605,04 y `freightTaxAmount` 2.394,96. Fuera de COP el IVA
  no se discrimina y la tasa queda en 0.
- **`shipping` ausente en un guardado deja el envío como estaba**, salvo que la entrega pase a recogida
  en tienda: ahí se limpia. Una recogida que sí trae envío responde `shipping_not_allowed_on_pickup`.
- La tasa del IVA del envío se toma de la configuración del tenant al fijar el valor y se guarda como foto
  (`freightTaxPercentage`): cambiarla después no re-precia documentos ya cotizados.
- Los totales suman el envío en campos propios y el mínimo de compra **no** lo cuenta. El PDF imprime la
  transportadora, el envío y su IVA; los resúmenes de Reporting lo explican dentro del total.
- **Contraentrega.** Convertir copia el envío tal cual. Crear o convertir un pedido con contraentrega, o
  prenderla en uno, exige que no tenga comprobantes y que el pago siga en `PaymentPending`
  (`order.order.cash_on_delivery_pending_only`), y si
  hay transportadora elegida, que recaude (`cash_on_delivery_not_supported`). Con contraentrega, el PDF
  imprime «Forma de pago: Contraentrega» en lugar de la forma de pago escrita.
- Cambiar el envío de un pedido pendiente se audita como `quotation.order.shipping_changed`.
- Cambiar la facturación de un pedido pendiente se audita como `quotation.order.billing_changed`.

Los códigos de error nuevos, con lo que los corrige:
[guía de integración § Códigos de error](docs/integracion-cotizaciones-y-pedidos.md#códigos-de-error-propios-del-módulo).

### Columnas del Excel de pedidos por tenant (homologación)

Cada ERP importa por encabezado con su propia plantilla, así que el tenant puede renombrar,
reordenar y ocultar las 55 columnas del catálogo del Excel de pedidos y agregar hasta 40 columnas fijas
(`Tipo Doc` = `FV`, `Bodega` = `01`), desde su configuración. Una fija cuyo valor es un número
canónico en cultura invariante (`0.19`, `9999`, `-1`, `901851609`) sale como **número**, que un
Excel en `es-CO` muestra `0,19` y el ERP lee como cifra; el resto (`02`, `PM`, `1,5`, `+1`, vacío)
sale como el texto que se escribió (2026-09-26). Una misma columna del catálogo
puede ir más de una vez con encabezados distintos: el ERP puede leer el mismo dato bajo varios
nombres (la fecha del pedido en `FECHA`, `Bloq/act` y `Vencimiento`).

Las dos últimas del catálogo (2026-09-25) son `Fecha Pedido` (`order_date`: el día en que nació
el pedido, en la zona del tenant, como texto `yyyy-MM-dd`) y `Cliente` (`customer_name`: a nombre
de quién sale la factura — `Consumidor final`, el nombre de la parte de facturación propia, la
razón social si el pedido factura a ella, o el nombre de la ficha del cliente).

Detrás de todas (2026-09-26) va `Documento de identidad` (`customer_identification`): el número de
documento de la misma persona que nombra `Cliente`, con la misma precedencia — `222222222222` para
consumidor final, el número de la ficha del cliente con razón social o sin ella, y el
`identificationNumber` de la parte cuando la factura sale a nombre de una parte de facturación
propia. Una parte guardada antes de que existiera ese número lo deja **vacío**, nunca con el del
cliente, que es otra persona. `Documento` (`document`) sigue siendo el CUC.

Y detrás de ella (2026-09-26), `Banco y cuenta` (`bank_account`: el banco y el número de la cuenta
de facturación separados por un espacio —`BANCOLOMBIA 7542`—, vacía sin cuenta de facturación) y
`Total consignado` (`proof_amount_total`: la suma de **todos** los comprobantes del pedido, no sólo
los cinco con columna propia, como número; vacía si el pedido no tiene comprobantes, igual que
`V. Comprobante N`). `Banco`, `Cuenta` y `V. Comprobante N` no cambian.

Después (2026-09-26) va `Tasa IVA` (`tax_rate`): la tasa de IVA de cada línea como fracción y
como número —19 % sale `0.19`, 0 % sale `0`—, tomada de la foto que la línea guardó del producto
al agregarse (`OrderItem.TaxPercentage`), no de la tarifa de hoy. `IVA` (`tax`) sigue siendo el
monto. En un pedido en **USD** las dos salen en `0` (hotfix 2026-10-14): en dólares el IVA no se
discrimina —el precio se cobra completo, como si lo trajera adentro— y `Valor Unit sin IVA` es el
precio tal cual. Por lo mismo, en USD tampoco se resta la retención en la fuente aunque el cliente
la practique: `Retención` sale en `0` y el neto es igual al total.

Después (2026-09-26) va `NIT Empresa` (`company_tax_id`): el NIT de la empresa por la que se
factura el pedido, la misma que nombra `EMPRESA`, tal como está hoy en Companies. Con la misma
regla que las fijas: sólo dígitos (`901851609`) sale como número; con puntos o dígito de
verificación (`901851609-1`) sale como texto. Vacía sin cuenta de facturación o si la empresa no
resuelve.

Después (2026-10-02) va `Ciudad Coordinadora` (`coordinadora_city`): la ciudad de entrega como la
escribe la transportadora Coordinadora (`ABEJORRAL (ANT)`), para la guía que genera el ERP. Sigue
la misma precedencia que `Ciudad` —la ciudad de la parte de entrega propia si la hay, aunque no
tenga ciudad; si no, la del cliente— pero nunca cae al nombre del DANE: queda **vacía** si el
municipio no tiene nombre de Coordinadora (ver [Nombres de ciudad de
Coordinadora](#nombres-de-ciudad-de-coordinadora)) o si el cliente no tiene ciudad. `Ciudad` (`city`)
no cambia. **Nace oculta**: no aparece en ningún Excel, ni sin layout ni en un layout ya guardado,
hasta que el tenant la prenda desde su configuración.

Después (2026-10-03) van `Forma de pago 1` a `Forma de pago 5` (`payment_method_1` a
`payment_method_5`): el mismo texto que `Banco y cuenta` —`BANCOLOMBIA 7542`—, pero sólo si el
pedido tiene el comprobante N, en el mismo orden que `Fecha Pago N` y `V. Comprobante N`; sin ese
comprobante, **vacía**, igual que `V. Comprobante N`. Existen porque `bank_account` es del pedido:
repetida bajo dos encabezados llenaba la forma de pago 2 aunque hubiera una sola consignación.
`Banco y cuenta` (`bank_account`) no cambia, porque otro ERP puede estar leyéndola. También **nacen
ocultas**, por la misma razón que `coordinadora_city`.

La última (2026-10-05) es `Transportadora` (`carrier`): `Recoger en tienda` si la cotización del
pedido es de recogida (`Order.IsStorePickup`) y `Coordinadora` en cualquier otro caso, como
texto. Los dos textos son contrato del ERP del tenant. La recogida no cambia ninguna otra columna:
`Direccion`, `Ciudad` y `Telefono` siguen cayendo a los datos del cliente sin parte de envío propia.
También **nace oculta**, por la misma razón que `coordinadora_city`.

Con el spec de flete (2026-10-17) `Transportadora` deja de ser fija: sale el nombre de la transportadora
elegida en el pedido (la foto), `Coordinadora` si no eligió ninguna y `Recoger en tienda` en recogida. Y
el catálogo gana **cinco columnas** al final: `Valor Flete` (`freight_amount`, visible), `Flete sin IVA`
(`freight_without_tax`), `IVA Flete` (`freight_tax`), `Pago Flete` (`freight_charge_mode`: `Cobrado`,
`Gratis` o `Al cobro`) y `Contraentrega` (`cash_on_delivery`: `Sí` o `No`), las cuatro últimas **ocultas**.
`Valor Flete` sale vacío en recogida, sin modo y con `Al cobro` (ese dinero se le paga a la
transportadora, no es del documento), y `0` con `Gratis`. **Regla de choque:** `OrdersExportLayout.Effective`
nunca deja dos columnas visibles con el mismo encabezado; una columna nueva del catálogo cuyo encabezado
coincide con el de una visible del layout guardado se agrega **oculta**. Consecuencia: un `PUT` parcial que
omite la columna del catálogo y agrega una fija visible con su encabezado se acepta, con la del catálogo
oculta (el frontend siempre manda la lista completa).

Con las variantes de producto (spec 2026-10-20 §7) `Cod. Producto` (`product_code`) lleva el `Code` de la
**variante** que vendió la línea, y el catálogo gana una columna más al final, `Variante` (`variant`): el
nombre visible de esa variante (`Camiseta – M`; en un producto sin opciones, el nombre del producto, porque
la línea no distingue la variante por defecto sin consultar Catalog). Las dos salen de la foto de la línea,
no del catálogo de hoy: renombrar o recodificar la variante después no cambia un pedido ya emitido. `Variante`
**nace oculta** —ningún Excel que ya existe cambia, tampoco «MIGRACION 1»— y el tenant la prende desde su
configuración; su encabezado cae bajo la regla de choque de arriba. El PDF de la cotización no cambió de
plantilla: `productName` ya es ese nombre visible.

**Notas del cliente** (2026-11-14): el catálogo gana una columna más al final, `Notas del cliente`
(`buyer_notes`), que sale de `Order.BuyerNotes`: la nota que el comprador escribió en la tienda pública o en
Shopify. `Observaciones` (`notes`) sigue saliendo de `Notes`, que es del asesor (en un pedido de Shopify,
«Shopify #N» y, si aplica, la dirección cruda). **Nace oculta**, también sin layout guardado: un tenant sin
layout puede tener un ERP leyendo la hoja por defecto. Su encabezado no choca con ninguno de «MIGRACION 1».

**Tarifas de retención** (spec 2026-11-16): dos columnas más al final, para un sistema de facturación (Siigo) que
busca el impuesto por su tarifa y calcula el monto él mismo. `Tarifa Retefuente` (`withholding_retefuente_rate`)
lleva el porcentaje (`2.5`); `Tarifa ReteICA` (`withholding_reteica_rate`), también el porcentaje tal como está
guardado (`1.104` en Bogotá), **sin** pasarlo a por mil (owner, 2026-11-16). Es la tarifa efectiva —la editada por el asesor, si la editó—, por
pedido y repetida en cada fila; `0` si el pedido no tiene retención aplicada de ese tipo. **Nacen ocultas.** El
tenant las prende y les pone el encabezado que su import lee (por ejemplo `Retefuente`); si ya tenía visibles
`Retefuente` o `ReteICA` (los montos), tiene que ocultarlas o renombrarlas en el mismo guardado, o el `PUT` responde
`header_duplicated`. **Límite conocido:** con dos retenciones aplicadas del mismo tipo sale la tarifa de la primera
por orden del pedido; los selectores ya exigen una por tipo, así que sólo puede pasar con la foto del cliente.

**Aviso para operadores:** la hoja sembrada «MIGRACION 1» conserva su columna fija `valor flete`, vacía, y
por eso `Valor Flete` del catálogo queda oculta en ella. Para que el ERP reciba el valor real, en
Configuración › Excel de pedidos cambia la fija por la columna del catálogo «Valor Flete».

La semilla (`Seed:Enabled`) le crea al tenant sembrado el layout de la hoja de importación de su
ERP, «MIGRACION 1»: 47 columnas visibles, 20 de ellas fijas. Su «Nit» es el de la empresa de
facturación (`company_tax_id`) y no un NIT escrito a mano (2026-09-26). Su «IVA» es la tasa de cada línea
(`tax_rate`) y no un `0.19` fijo, porque hay productos con otra tarifa; `tax` queda oculto. El banco con su cuenta
va en «Forma de pago 1» y «Forma de pago 2», cada una con su comprobante (`payment_method_1` y
`payment_method_2`, desde el 2026-10-03; antes las dos leían `bank_account` y la 2 salía llena con
una sola consignación), y el total consignado (`proof_amount_total`) en «V. Consignacion (P7)»;
`bank` y `bank_account` quedan ocultos. Sólo crea: si el tenant ya tiene layout, no lo toca. Desde el 2026-09-26 su
`Documento (P5)` lleva el documento de identidad (`customer_identification`) y el CUC (`document`)
queda oculto. Desde el 2026-10-02 su «Ciudad (P4)» lleva la ciudad como la escribe Coordinadora
(`coordinadora_city`) —la transportadora es Coordinadora salvo en recogida— y `city` queda oculta. Desde el 2026-10-05 su
«Transportadora (P2)» es la columna `carrier` y no una fija: `Recoger en tienda` en los pedidos de recogida. Como el
layout ya guardado no se migra, el tenant que lo tenía de antes sigue con la fija `Coordinadora` hasta que se borre
su fila y la semilla la recree.
Un tenant cuyo layout ya existía conserva el de antes —no hay migración de layouts—
y lo cambia desde su pantalla de configuración.

| Método | Ruta                                                | Permiso                  |
| ------ | --------------------------------------------------- | ------------------------ |
| `GET`  | `/api/v1/tenants/{tenantId}/orders-export-layout`   | `tenancy.settings.read`  |
| `PUT`  | `/api/v1/tenants/{tenantId}/orders-export-layout`   | `tenancy.settings.update`|

El `GET` devuelve el layout **efectivo**: lo guardado en su orden más toda columna del catálogo
que no esté guardada, al final, con su nombre por defecto y visible u oculta según su
`defaultVisible`; sin nada guardado es el catálogo tal cual (el Excel de siempre) con `version: 1`
y ETag `"1"`. Cada columna viaja con `kind` (`Catalog` | `Fixed`), `key` y
`defaultHeader`/`defaultPosition`/`defaultVisible` (sólo las del catálogo; `null` en las fijas),
`header`, `value` (sólo las fijas) y `visible`. `defaultVisible` es `true` en todas menos en
`coordinadora_city` (2026-10-02), las `payment_method_N` (2026-10-03), `carrier` (2026-10-05),
`freight_without_tax`, `freight_tax`, `freight_charge_mode` y `cash_on_delivery` (2026-10-17),
`variant` (2026-10-20) y `buyer_notes` (2026-11-14).

El `PUT` reemplaza la lista entera con `If-Match` obligatorio (428 sin él; 412 con una versión
vieja, incluido el choque de dos primeros guardados). No exige el catálogo entero: lo que no
aparezca ni una vez se completa.
Con un encabezado vacío o de más de 64 caracteres, o un valor fijo de más de 128, responde
`422 validation.failed` con `errors.Columns[i].Header` / `.Value` / `.Kind`. Reglas del dominio,
con prefijo `quotations.orders_export_layout.`: `columns_invalid` (llave vacía o desconocida; una
repetida es válida desde el 2026-09-25), `header_duplicated` (dos **visibles** con el mismo
encabezado, sin distinguir mayúsculas), `all_hidden`, `too_many_fixed_columns` (más de 40). Audita
`quotations.orders_export_layout.updated` sólo si algo cambió. No hay `DELETE`: restaurar es un
`PUT` con el catálogo en su orden, nombres y visibilidad por defecto, sin fijas.

**Indicativo de los teléfonos** (spec 2026-11-25). El layout lleva un interruptor, `includePhoneDialCode`, que el `GET`
devuelve siempre (`true` sin nada guardado) y el `PUT` acepta opcional: ausente o `null` conserva el actual. Se guarda con las
mismas reglas que `sheetName` —permisos, `If-Match`, auditoría y no-op—, y cambiar sólo el interruptor es un cambio. Toda fila
que ya existía quedó en `true` (migración `AddOrdersExportLayoutPhoneDialCode`, aditiva). Sólo toca las cuatro columnas de
teléfono del catálogo —`phone`, `customer_phone`, `billing_phone` y `shipping_phone`—; las fijas salen como se escribieron.

| `includePhoneDialCode` | Teléfono guardado                     | Sale en el Excel |
| ---------------------- | ------------------------------------- | ---------------- |
| `true`                 | cualquiera                            | como está        |
| `false`                | `+573001234567`                       | `3001234567`     |
| `false`                | `+34612345678`                        | `612345678`      |
| `false`                | sin `+` (`3001234567`, `573001234567`) | como está       |
| `false`                | con `+` y no se puede partir          | como está        |

El número se parte con `libphonenumber` (`IPhoneNumberSplitter` de Customers, detrás del puerto `IQuotationPhoneNumbers`); lo que
no es un número válido no se puede partir.

`Observaciones` (`notes`) sale de las notas del pedido (`Order.Notes`, spec 2026-10-15), sin tope de
largo. Al convertir, las notas del pedido son un solo campo, nunca una concatenación: el `notes` del
cuerpo la reemplaza tal cual, `null` (o ausente) hereda las de la cotización y `""` la deja sin notas.

Un layout guardado se aplica en la siguiente exportación de pedidos; el de cotizaciones no cambia.

### POS: caja y ventas

Todas las rutas cuelgan de `/api/v1/tenants/{tenantId}/pos`. El detalle de cada contrato vive en
el spec del slice; esta tabla es el inventario.

| Método y ruta                      | Permiso                | Para qué                                                                         |
| ---------------------------------- | ---------------------- | -------------------------------------------------------------------------------- |
| `GET /register`                    | `pos.register.operate` | Contexto de la caja: cajero, empresas emisoras y la sesión abierta con su versión |
| `POST /sessions`                   | `pos.register.operate` | Abre la caja con la base inicial (`openingFloat`, obligatoria)                   |
| `POST /sessions/{sessionId}/close` | `pos.register.operate` | Cierra la caja con el conteo (`countedCash`, obligatorio); exige `If-Match`      |
| `GET /sessions`                    | `pos.sale.read`        | Lista paginada de sesiones de caja                                               |
| `GET /sessions/{sessionId}`        | `pos.sale.read`        | Detalle y arqueo de una sesión                                                   |
| `GET /products`                    | `pos.sale.create`      | Búsqueda paginada por nombre, `Code` o código de barras: productos con sus variantes vendibles; `categoryId` filtra por esa categoría y sus descendientes (un id ajeno o desconocido da página vacía) |
| `GET /categories`                  | `pos.sale.create`      | Categorías con el conteo de productos activos acumulado, incluidas las de 0, `quickAccessPosition` (chips de acceso rápido, `null` sin fijar; la lista sigue por nombre) y `total` de activos del tenant con o sin categoría |
| `GET /products/by-code`            | `pos.sale.create`      | El escáner: la variante por código de barras exacto y, si ninguna lo tiene, por su `Code` |
| `POST /sales/preview`              | `pos.sale.create`      | Calcula totales de un carrito sin guardar nada; cada línea es una variante (`variantId`) |
| `POST /sales`                      | `pos.sale.create`      | Crea la venta, con una línea por variante; idempotente por el `id` del cliente (201 la primera vez, 200 al repetir); `customerId` opcional |
| `GET /customers`                   | `pos.sale.customer`    | Clientes elegibles de la caja: activos con documento, hasta 20, por nombre, documento, CUC o teléfono (desde 7 dígitos, sin importar el formato) |
| `POST /customers`                  | `pos.sale.customer`    | Alta rápida: ficha incompleta con documento; 422 `pos.customer.identification_taken` trae `existing` |
| `GET /sales/{saleId}`              | `pos.sale.read`        | Detalle de una venta                                                             |
| `GET /sales`                       | `pos.sale.read`        | Lista paginada de ventas; `number` y `customerDocument` buscan por un pedazo     |
| `POST /sales/{saleId}/void`        | `pos.sale.void`        | Anula una venta, con motivo; sólo si su caja sigue abierta                       |

### Clientes: documento repetido, coincidencias y anomalías

Spec `2026-10-24-clientes-duplicados`. Tres reglas, cada una con su respuesta:

- **R1, mismo tipo y mismo número: se frena.** `POST`/`PUT /customers` responde 422
  `customers.customer.identification_taken` y ahora trae `existing: { id, name, identificationType,
  identificationNumber, cuc | null, isComplete, isActive }`, el cliente que ya tiene ese documento. `existing` puede faltar
  (carrera entre dos altas: el índice único gana y el dueño ya no se encuentra al releer).
- **R2, mismo número con otro tipo, o el mismo tipo con otro formato: se avisa, no se frena.**
  `GET …/customers/identification-matches?number=&type=&excludeCustomerId=` (`customers.customer.read`; `number` obligatorio,
  hasta 32; `type` opcional, ∈ `NIT`, `CC`, `CE`, `PASAPORTE`) devuelve hasta 20 `items` con `match` ∈ `SameIdentification`,
  `OtherType`, `SameTypeOtherFormat`. El formulario lo llama antes de guardar.
- **R3, mismo celular o WhatsApp: no se frena ni se avisa al guardar.** El cliente se distingue por el documento. Lo que sí
  hay es un reporte: `GET …/customers/anomalies?kind=&page=&pageSize=` (`customers.customer.read`; `kind` ∈ `SharedPhone`,
  `SharedIdentificationNumber`; `pageSize` hasta 50, 20 por defecto) devuelve `counts` (siempre los dos `kind`, incluso en cero),
  `groups` paginados (cada uno con su `key`, los `customers` y `truncated`), `total`, `page` y `pageSize`.

**La expresión normalizada.** R2 y `SharedIdentificationNumber` comparan el número sin signos y en mayúsculas:
`upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))`. Vive en `CustomerIdentificationSql.NormalizedNumber`
(consultas), en `CustomerIdentificationNormalizer` (C#) y **literal** en la migración `AddCustomerIdentificationNormalizedIndex`,
que crea `IX_customers_tenant_identification_normalized` sobre `(tenant_id, <expresión>) WHERE identification_number IS NOT NULL`.
No es único. Si la expresión de la consulta y la del índice difieren en un carácter, Postgres no usa el índice y la consulta
recorre la tabla; `TheMatchQueryUsesTheNormalizedIndex` lo amarra.

### Clientes: importación por Excel y etiquetas

`POST …/customers/import` (`customers.customer.import`) lee la primera hoja por **nombre de cabecera**, no por posición. Las trece
columnas de `CustomerImportColumns.Ordered` son obligatorias en la cabecera (un archivo sin alguna se rechaza entero); después van
las opcionales, que un archivo viejo puede no traer: «Empresa» y «Etiquetas». La plantilla (`GET …/customers/import/template`), el
archivo de filas a corregir y la exportación escriben las mismas cabeceras, en el mismo orden, así que un exportado se corrige y se
vuelve a subir.

- **Qué exige una fila que crea.** `Nombre` y `Telefono` siempre. Documento (tipo y número) y Departamento + Ciudad sólo si la
  política del tenant los pide al guardar (`CustomerFieldPolicy`); sin ellos el cliente nace `Incomplete`. `Direccion` es
  opcional (spec 2026-11-17): sin calle el cliente nace sin domicilio y sin libreta, como en el formulario. Una lista de
  contactos con sólo nombre y celular entra, con la política relajada.
- **`Etiquetas`** (spec 2026-11-17): varios nombres separados por `;` (`Evento macel; VIP`). Cada nombre casa sin distinguir
  mayúsculas con las etiquetas del tenant; la que no existe **se crea**, en gris y en tipo oración (`evento MACEL` →
  `Evento macel`, `TagNameFormat.SentenceCase`); la que existe conserva su nombre. La celda **suma y nunca quita**: en una fila
  de actualización (con `Cuc`) el cliente conserva las que tenía. Los errores son de fila, con `field: "Tags"` (el mismo `PropertyName` del validador):
  `customers.import.row.tag_name_too_long` (más de 40 caracteres), `customers.import.row.tag_limit_per_customer` (el cliente
  quedaría con más de 20) y `customers.import.row.tag_limit_reached` (crearla pasaría las 100 del tenant). Una fila con error de
  etiquetas no se importa, y una etiqueta nueva que sólo esa fila pedía no se crea. La auditoría del import lleva `tagsCreated=N`
  en su `resourceId`, y cada etiqueta creada publica `customers.tag.created` como la pantalla.

### Clientes: fusión

Spec `2026-10-24-fusion-de-clientes`. Junta clientes duplicados en uno; **no se puede deshacer**.

- **Rutas** (`customers.customer.merge`, riesgo `high`; de fábrica lo tienen `admin`, el rol de sistema, y `advisor`, el rol
  sembrado en cada tenant):
  `GET /customers/{survivorId}/merge-preview?mergedCustomerIds=…` (1 a 10 ids distintos, sin el que queda) y
  `POST /customers/{survivorId}/merge` con `If-Match: "<survivor.version>"` y `{ "mergedCustomerIds": [...] }`. Sin `If-Match`
  responde 428; con una versión vieja, 412 `concurrency.conflict` (la pantalla pide de nuevo la vista previa).
- **Reglas.** El que queda debe estar activo (`customers.merge.survivor_inactive`) y no puede ser incompleto si algún fusionado
  es completo (`customers.merge.survivor_incomplete`); nadie que ya esté fusionado entra de nuevo
  (`customers.merge.already_merged`); un id inexistente es `customers.merge.customer_not_found`. Un fusionado inactivo vale.
  El que queda sólo se llena donde está vacío (correo, razón social, teléfono, BSUID, IGSID y, si es incompleto, documento,
  país y dirección) y hereda el opt-out de marketing; nunca la clasificación ni el CUC. La respuesta del `POST` es el
  `CustomerResponse` del que queda más `droppedTags`.
- **Vista previa.** Devuelve `survivor`, `merged`, `documentsDiffer`, `filledFields` y `moves`, siempre con los ocho conteos y en
  este orden: `Quotations`, `Orders`, `Conversations`, `PosSales`, `Deals`, `Notes`, `Addresses`, `Tags`.
- **Qué guarda el fusionado.** Dos columnas nuevas en `customers.customers`, `merged_into_id` y `merged_at`, e `IsActive = false`.
  `merged_into_id` apunta siempre a un cliente no fusionado (la fusión reescribe los que ya apuntaban al fusionado): quien lee un
  fusionado sigue un solo salto. `IX_customers_tenant_merged_into` es parcial (`merged_into_id IS NOT NULL`) e
  `IX_customers_tenant_identification` pasó a ser único **parcial** (`merged_into_id IS NULL`): el documento de un fusionado
  queda libre para un cliente nuevo.
- **Qué se mueve, en una sola transacción.** Cotizaciones y pedidos (`client_id`), ventas del POS, negocios de Pipeline con sus
  cotizaciones, pedidos y decisiones de triage, conversaciones de Messaging, la audiencia de las campañas (las Draft/Scheduled
  suben su versión) y los pedidos espejo de Shopify; de Customers, notas, direcciones y etiquetas (las que sobran salen en
  `droppedTags`). El SQL de los otros esquemas vive en `CustomerMergeReferences` (Bootstrapper).
- **Qué se excluye y qué se redirige.** Un fusionado sale de `GET /customers`, de los buscadores, de las coincidencias y
  anomalías y de los reportes; `GET /customers/{id}` sí responde su ficha (`isActive: false`) con
  `mergedInto: { id, name } | null`. Cualquier escritura sobre un fusionado responde 422 `customers.customer.merged`. Su CUC,
  teléfono, BSUID e IGSID resuelven al que quedó, y los eventos tardíos de Quotations que traen su id caen en el negocio del
  que quedó. La importación responde `customers.import.row.customer_merged` con `mergedIntoCuc` cuando una fila trae el CUC de
  un fusionado. Crear un negocio o cambiar el cliente de una conversación con el id de un fusionado lo deja a nombre del que
  quedó.
- **Despliegue.** La migración `AddCustomerMerge` no tiene vuelta atrás limpia: su `Down` recrea
  `IX_customers_tenant_identification` sin filtro y falla si ya hay una fusión en la que el fusionado y el que queda comparten
  tipo y número de documento. Para bajarla en un ambiente con fusiones, primero hay que resolver esos pares a mano.

### Aceptación de la invitación

El email lleva `{Notifications:InvitationUrl}/{token}`. La pantalla que abre ese
link resuelve la invitación **antes** de pedir sesión, para poder decir a qué
organización invitan y con qué cuenta hay que entrar.

| Método | Ruta                                 | Autenticación |
| ------ | ------------------------------------ | ------------- |
| `GET`  | `/api/v1/invitations/{token}`        | Anónimo       |
| `POST` | `/api/v1/invitations/{token}/accept` | Sesión QEP    |

El `GET` es anónimo por necesidad —quien abre el link todavía no tiene sesión— y
por eso va limitado por IP con la política `Public`. Responde `200 OK`:

```json
{
  "tenantId": "01900000-0000-7000-8000-000000000001",
  "tenantName": "Verde Alba",
  "email": "new.member@example.com",
  "status": "pending"
}
```

`status` es el estado derivado (`MembershipViewStates.Of`), no la columna cruda:
el vencimiento es perezoso y una fila puede seguir `Invited` con la ventana ya
pasada. `Active` se dice **`accepted`** acá, porque `active` es el vocabulario
del filtro del roster y no el de este link. `suspended` y `removed` viajan tal
cual; el cliente trata como no aceptable todo lo que no reconozca.

El `POST` exige sesión y responde `204 No Content`. Es **idempotente** contra el
auto-accept del login: `POST /api/v1/auth/session` sigue aceptando todas las
membresías invitadas del usuario al iniciar sesión, y este camino se le suma en
lugar de reemplazarlo.

| Estado | Código de dominio                       | Significado                              |
| ------ | --------------------------------------- | ---------------------------------------- |
| `401`  | —                                       | No hay sesión                            |
| `403`  | `tenancy.invitation.user_mismatch`      | La sesión es de otra cuenta              |
| `404`  | `tenancy.invitation.not_found`          | Ningún hash coincide con el token        |
| `422`  | `tenancy.membership.invitation_expired` | La invitación venció                     |
| `422`  | `tenancy.membership.not_invited`        | El estado ya no admite aceptar           |

El `403` no revela de quién es la invitación: mismo criterio de no filtrar
identidades que el `403` de login. Y el `422` distingue vencimiento de estado no
aceptable porque sólo el primero se arregla pidiendo un link nuevo.

Ni este token ni el del mayorista quedan en el servidor: `platform.request_failures` y su línea de log guardan la ruta con
`SensitivePaths.Mask` (`/api/v1/invitations/***/accept`). La telemetría usa la misma regla (`SensitivePathTelemetry`): el
`url.path` de los spans de OpenTelemetry y el scope `RequestPath` de los logs del request.

### Listas de precios por canal

Spec `docs/superpowers/specs/2026-11-22-listas-de-precios-por-canal-design.md`. Una lista de Catalog tiene un ajuste con signo
sobre el precio base (−100 a 999,99 %, redondeado con la regla del tenant para cada moneda) y precios fijos por variante y
moneda, que ganan tal cual. Un precio fijo sólo reemplaza: sin precio base en la moneda, la variante no tiene precio en ningún
canal. «Precios por canal» asigna una lista a `Pos`, `Store` y `Quotations` (sin lista, el precio base); cada distribuidor del
mayorista tiene la suya. El canal se deriva del documento: la caja usa `Pos`; el catálogo, el carrito y los pedidos de la tienda
(también al editarlos) usan `Store`; cotizaciones, pedidos manuales y convertidos y regalos usan `Quotations`; un pedido de
Shopify conserva el precio de Shopify. Las escalas siguen en la variante y su descuento se calcula sobre el precio del canal,
donde el canal ya las aplicaba. Las líneas guardadas no cambian al cambiar una lista. Visibilidad, exclusiones y
`showPrices` de la lista sólo los usa el catálogo del distribuidor.

### Mayorista: listas de precios, distribuidores e invitación

Módulo `wholesale` (spec `docs/superpowers/specs/2026-11-17-compras-y-mayorista-design.md`, plan
`docs/superpowers/plans/2026-11-17-mayorista-fase-2-backend.md`, «Contrato HTTP»). El mayorista asigna a cada distribuidor
una lista de precios de Catalog (ver [Listas de precios por canal](#listas-de-precios-por-canal); su ajuste se aplica en la moneda
por defecto del mayorista, redondeado con su regla) e invita distribuidores por correo.
El correo lleva `{Notifications:AppBaseUrl}/wholesale-invitations/{token}?ev={prueba}` (72 horas; reenviar rota el token y el
link viejo pasa a `404`). `ev` es la prueba de buzón (spec `2026-11-24-invitacion-distribuidor-prueba-de-correo-design.md`): sólo
la lleva el link del correo; el de «Copiar», `invitationUrl` y el aviso de WhatsApp van sin ella.

| Método | Ruta                                                   | Autenticación                        | Límite        |
| ------ | ------------------------------------------------------ | ------------------------------------ | ------------- |
| `GET`  | `/api/v1/wholesale-invitations/{token}`                | Anónimo                              | `public`      |
| `POST` | `/api/v1/wholesale-invitations/{token}/email-code`     | Anónimo, `{ email }`, siempre `202`  | `credentials` |
| `POST` | `/api/v1/wholesale-invitations/{token}/session`        | Bearer de Google (o stub)            | `credentials` |
| `POST` | `/api/v1/wholesale-invitations/{token}/session-with-password` | Anónimo, `{ email, password, code?, termsVersion, emailProof? }` | `credentials` |
| `GET`  | `/api/v1/wholesale-invitations/{token}/linkable-tenants` | Sesión QEP                         | `public`      |
| `POST` | `/api/v1/wholesale-invitations/{token}/accept`         | Sesión QEP                           | `public`      |

Spec `docs/superpowers/specs/2026-11-19-onboarding-distribuidor-por-link-design.md`: el link identifica la invitación y el
correo no tiene que ser el invitado. Por sí solo no prueba el buzón; el del correo sí puede, con su `ev` (spec 2026-11-24).

- `email-code` manda un código de 6 dígitos al correo que escribió el distribuidor (vence a los 10 minutos, 5 intentos; uno
  nuevo cada 60 s como mucho y 5 por correo en 24 h móviles). Cada código nuevo invalida el anterior y sólo cuenta el más
  reciente. Responde `202` siempre: no dice si el correo tiene cuenta ni si la invitación sigue pendiente, y
  sólo emite con la invitación pendiente.
- `session-with-password` (sin `email`, el invitado): si ese correo ya tiene contraseña, es un login normal
  (`IPasswordLogin`: `401 identity.credentials.invalid`, o `403 identity.email.not_verified` con el correo sin verificar; el
  link no lo verifica, y `emailProof` se ignora). Si no tiene usuario o no tiene contraseña, exige un `code` válido de
  `email-code` **o** `emailProof`, el valor de `?ev=` del link del correo: sin ninguno de los dos es el mismo `401` que una
  contraseña errada, así que la respuesta no dice si el correo tiene cuenta ni si falló la prueba o el código. `emailProof`
  sólo vale con la invitación pendiente, la prueba vigente y el correo invitado (sin mayúsculas ni espacios de más); la anulan
  «Copiar», «Reenviar» (que manda una nueva) y cambiar el correo del distribuidor. Con el buzón probado o el código ya gastado
  exige `termsVersion` (`422` en `TermsVersion`; la vigente es `Terms:CurrentVersion`, y sin ella falla cerrado con
  `identity.terms.unavailable`), y entonces aprovisiona, crea la contraseña y registra los términos.
- `session` abre la sesión del correo **verificado** de Google (`403` si no está verificado), sea o no el invitado: usa el
  usuario que ya tiene esa cuenta de Google o ese correo y aprovisiona sólo si no hay ninguno
  (`WholesaleInvitationGoogleSession`).
- `accept` recibe `{ mode: "NewTenant", termsVersion? }` o `{ mode: "LinkTenant", tenantId }`. `NewTenant` crea el tenant del
  distribuidor con la razón social de la invitación como nombre y un slug que sugiere el servidor (con sufijo si está tomado;
  si se lo ganan en una carrera, reintenta hasta 3 veces), con `catalog, customers, companies, quotations, orders, reporting,
  suppliers, purchases`, aunque el alta pública esté apagada; un `displayName` o `slug` en el cuerpo se usan si vienen (el país es
  siempre el del distribuidor). Quien no aceptó
  la versión vigente de los términos manda `termsVersion` (`422` en `TermsVersion` sin ella). `LinkTenant` vincula un tenant
  donde la sesión es owner o tiene el rol de sistema `admin` en una membresía activa; prende `catalog`, `suppliers` y
  `purchases` si faltan. Sobre el tenant reservado (el que nace de la invitación, también en un reintento con cuerpo
  `LinkTenant`) crea además la **empresa del distribuidor** con su razón social, documento, ciudad y dirección, idempotente por
  tipo y número exactos (una invitación sin la ciudad que su país exige acepta sin empresa); vincular un tenant existente la crea sólo si ese
  tenant no tiene ninguna empresa. En los dos casos crea el proveedor vinculado en el distribuidor, el cliente en el mayorista (con el celular del
  aviso, la ciudad, la dirección y el contacto del distribuidor: con teléfono y ciudad nace completo, y el contacto es la etiqueta
  de su dirección principal si hay ciudad colombiana y calle; sin teléfono, o si Customers rechaza la ficha completa, nace incompleto
  como antes) y deja la relación `Active`; si el correo de la cuenta no es el del distribuidor, lo reemplaza en el mismo guardado y avisa a los dos
  correos. Repetirlo con el mismo usuario responde lo mismo, sin escribir.
- Errores: `404 wholesale.invitation.not_found`, `410 wholesale.invitation.expired`, `409 wholesale.invitation.already_accepted`,
  `.unavailable` (el mayorista no está activo, apagó `wholesale` o su empresa facturadora no sirve) y
  `wholesale.distributor.already_linked`; `403 wholesale.relationship.inactive` (la relación está bloqueada) y
  `wholesale.invitation.not_tenant_owner`; `401 identity.credentials.invalid`; `422 wholesale.invitation.self_link`,
  `identity.terms.unavailable`, `identity.terms.version_mismatch`, los de `register-tenant` y `validation.failed` (`Mode`,
  `TenantId`, `TermsVersion`, `Password`).

#### Editar un distribuidor

`PUT …/wholesale/distributors/{id}` (`If-Match`; cuerpo `{ priceListId, advisorId, billingCompanyId, phone?,
whatsAppConnectionId?, businessName?, country?, identificationType?, identificationNumber?, contactName?, email?, cityId?,
cityName?, address? }`). Decisión del owner 2026-11-24: mientras está `Invited` se editan los datos que se llenaron al invitar.

- Lista, asesor y empresa facturadora se editan en cualquier estado. `phone` y `whatsAppConnectionId` sólo cuentan invitado
  (afuera se ignoran) y `phone` ausente **borra** los dos.
- Los datos del distribuidor son opcionales: ausente o `null` **conserva** lo guardado. No se puede borrar el nombre ni la
  ciudad; la dirección se borra con `""`.
- Invitado, rigen las reglas de invitar: `422 validation.failed` con `errors` por campo (`Email`, `BusinessName`, `Country`,
  `IdentificationType`, `IdentificationNumber`, `ContactName`, `CityId`, `CityName`, `Address`),
  `422 wholesale.distributor.city_not_found` y `409 wholesale.distributor.identification_taken` contra los demás
  distribuidores. Al cambiar de país hay que mandar el tipo de documento y la ciudad válidos para el nuevo. Una invitación
  vieja sin ciudad no la exige mientras no se mande la ciudad ni cambie el país.
- Invitado, cambiar el tipo o el número de documento cuando la aceptación ya empezó (creando el tenant o vinculando uno
  existente) es `409 wholesale.distributor.acceptance_in_progress` y no escribe nada; los demás campos se siguen editando.
  La aceptación confirma esa marca antes de escribir en otros módulos y con ella sube `version`: un `PUT` con el `If-Match`
  anterior es `412 concurrency.conflict`. Un accept rechazado en sus validaciones no deja marca. La marca no se borra.
- El detalle devuelve `city` como `{ id, name, departmentId, departmentName }`, para que la edición preseleccione la cascada
  departamento → ciudad. Afuera de Colombia `id`, `departmentId` y `departmentName` son `null` y `name` es el texto escrito;
  `city` es `null` en una invitación sin ciudad. El `PUT` sigue recibiendo sólo `cityId` o `cityName`.
- `Active` o `Blocked`: un dato distinto del guardado es `409 wholesale.distributor.invalid_transition`. Reenviar los mismos
  valores se acepta (en Colombia `cityName` no cuenta, y afuera `cityId` tampoco).
- Cambiar el correo **no** reenvía la invitación ni rota el token: para que llegue al correo nuevo, usa `resend-invitation`.
- La auditoría (`wholesale.distributor.updated`) nombra los campos que cambiaron, nunca sus valores.

#### Aviso por WhatsApp de la invitación

Spec `docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md`, plan
`docs/superpowers/plans/2026-11-18-aviso-whatsapp-invitacion-backend.md`, «Contrato HTTP». Con `phone` (y la conexión que
resuelve el contrato) invitar y «Reenviar» escriben, además del correo, `wholesale.distributor-invitation-link.v1` (spec
2026-11-19 §2): un evento **con el token** de la invitación, que viaja sólo en el payload del outbox. Reemplaza a
`wholesale.distributor-invitation-notice.v1`, sin token, que ya no se escribe y sólo se drena. El mensaje de WhatsApp todavía
**sale sin el link**: dice que la invitación llegó al correo. Los dos los consume Mayorista con inbox propio
(`wholesale.inbox_messages`, consumidor `wholesale.invitation-whatsapp-notice`, curva 5 s / 30 s / 5 min) y lo manda por
`WholesaleInvitationNoticeSender` de Bootstrapper con la plantilla `qep_mayorista_invitacion` (`UTILITY`, `es`, variables
`nombre_contacto`, `mayorista`, `correo`). El fallo del aviso nunca frena ni cambia el correo.

- Campos nuevos: `POST` y `PUT` reciben `phone` (E.164 con el país del tenant) y `whatsAppConnectionId`; `options` suma
  `whatsAppConnections` (activas con la plantilla aprobada); lista y detalle suman `phone`, y el detalle
  `invitation.whatsAppNotice { status: Pending|Sent|Failed, failureCode, connectionLabel }` (`null` sin teléfono o tras un `PUT`
  que cambió teléfono o conexión).
- `failureCode`: `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>` y, en el reenvío,
  `whatsapp_unavailable`. Errores 422 `wholesale.distributor.whatsapp_connection_required` (varias listas, ninguna elegida) y
  `wholesale.distributor.whatsapp_unavailable` (ninguna lista, o la elegida no lo está).
- El resultado se aplica sólo a la `generation` vigente de la invitación y no sube `version`: un `PUT` o un «Reenviar» en el medio
  deja el aviso viejo sin efecto, y el `If-Match` leído antes sigue valiendo.
- Plantilla: `POST …/template-management/wholesale-invitation-notice` (cuerpo opcional `{ body }`, 201) y
  `GET …/wholesale-invitation-notice/draft`; `TemplateManagementDto` suma `wholesaleInvitationNoticeTemplate`.

### Mayorista: órdenes de compra vinculadas

Mayorista fase 3 (plan `docs/superpowers/plans/2026-11-17-mayorista-fase-3-backend.md`, «Contrato HTTP» y su adenda). Una orden de
compra a un proveedor vinculado (`mode: "Linked"`, fijado al guardar el borrador) se precia con la lista del mayorista y, al
enviarla, crea el pedido en el tenant mayorista (`source: "Wholesale"`, `externalReference` = número de la orden).

- **Catálogo:** `GET …/purchases/suppliers/{supplierId}/catalog` exige la relación `Active` entre los dos tenants y sólo muestra lo
  visible en la lista del distribuidor. El costo de la orden es el precio de lista sin IVA; la línea congela base, descuento y
  precio de lista con IVA.
- **Enviar** es idempotente por la clave `purchase-order:{comprador}:{orden}`: un reintento adopta el pedido que ya existe con sus
  líneas, sin revalidar. Si el motor de pedidos del mayorista lo rechaza, o su moneda ya no es la de la orden, `409
  purchases.order.wholesaler_rejected` (el código interno queda en el log).
- **Aprobar** (`POST /orders/{id}/approve`, `quotations.order.manage`) congela el pedido y publica
  `quotations.order.wholesale-approved.v1` con las líneas finales. Compras lo consume con su inbox (`purchases.inbox_messages`,
  consumidor `purchases.wholesale-order-events`) y confirma la orden línea por línea (`Unchanged`, `Changed`, `Rejected`,
  `Added`), creando en el catálogo propio, en la misma transacción, los productos que no encuentra por código.
- **Cancelar** una orden vinculada en `Sent` anula primero el pedido; aprobado o tocado, `409 purchases.order.not_cancellable`.
  Cancelar o borrar un borrador vinculado también anula el pedido que haya dejado un envío que no terminó de guardarse. La
  anulación del mayorista (`quotations.order.wholesale-cancelled.v1`) cancela la orden salvo si ya se recibió.
- **Proveedores** suma `linkedRelationshipStatus` (`Active`, `Blocked`, `Invited` o `null`) en la lista y el detalle.

## Arquitectura y persistencia

```txt
src/
  Api/                         # Host HTTP, manejo de errores, auth y registro
  Bootstrapper/                # Composición, autenticación y autorización
  BuildingBlocks/                  # Domain, Application, Infrastructure y Observability
  Modules/
    Audit/                         # Application, Domain e Infrastructure
    Authorization/                 # Application
    Catalog/                       # Api, Application, Domain e Infrastructure
    Identity/                      # Application, Domain e Infrastructure
    Notifications/                 # Application, Domain e Infrastructure
    Storage/                       # Api, Application, Domain e Infrastructure
    Tenancy/                       # Api, Application, Domain e Infrastructure
tests/
  ArchitectureTests/
  Modules/<Modulo>/
    Modules.<Modulo>.UnitTests/          # los siete módulos
    Modules.<Modulo>.IntegrationTests/   # Audit, Catalog, Notifications, Storage y Tenancy
```

Sólo `Catalog`, `Storage` y `Tenancy` tienen capa `Api`: los demás no exponen
endpoints propios. Los de sesión, registro y catálogo de autorización viven en
`src/Api`.

PostgreSQL separa los datos por esquemas, uno por módulo con estado propio:

- `tenancy`: tenants, memberships y proyección del historial de cambios;
- `identity`: usuarios, vínculos con proveedores y sesiones;
- `catalog`: productos;
- `storage`: recursos de archivo y sus variantes;
- `notifications`: notificaciones emitidas;
- `audit`: entradas de auditoría;
- `platform`: mensajes Outbox e Inbox.

Cada módulo con persistencia usa un `DbContext` independiente sobre
`QepDatabase` y su propia tabla de historial `__ef_migrations_history`, en su
esquema, evitando colisiones entre módulos. La excepción es **Tenancy**, que
registra el suyo en `platform` por ser el primero que se creó.

Una actualización efectiva de la configuración incrementa su versión y guarda,
en la misma unidad de trabajo, la auditoría y el evento
`tenancy.tenant-settings-updated.v1`. Un worker interno procesa el Outbox cada
dos segundos. El Inbox evita repetir los efectos si el mismo evento se vuelve a
entregar. Actualmente este despacho es interno al monolito y no utiliza un
broker externo.

El módulo Identity contiene dominio, persistencia —usuarios, vínculos con
proveedores y sesiones— y el servicio para obtener o aprovisionar usuarios
invitados, pero **no tiene capa `Api` propia**: los endpoints de sesión y
registro que lo consumen viven en `src/Api`. Tenancy consume ese mismo servicio
mediante un contrato de Application al procesar una invitación.

### Consistencia de la invitación entre módulos

Identity y Tenancy tienen `DbContext` y Unit of Work independientes, aunque
usen la misma base física. La invitación realiza dos confirmaciones:

1. Identity obtiene o crea el usuario por email;
2. Tenancy guarda Membership, Audit y Outbox en una segunda transacción.

Por tanto, la operación completa no es atómica entre módulos. Las restricciones
únicas sobre email y sobre `userId + tenantId`, junto con las búsquedas previas,
hacen reintentable una invitación secuencial y evitan duplicados. Sin embargo,
si falla la segunda confirmación puede quedar un usuario `Invited` sin
Membership hasta que la solicitud se reintente. Actualmente no existe
compensación, reintento automático ni manejo específico de invitaciones
concurrentes.

## Patrones técnicos y componentes

Resumen de los patrones y componentes que implementa el código actual, útil
como referencia rápida antes de tocar un módulo.

### Stack

.NET 10, ASP.NET Core Minimal APIs, EF Core 10 + Npgsql sobre PostgreSQL 18,
FluentValidation, xUnit v3 + Testcontainers.PostgreSql, OpenTelemetry (OTLP),
AWS SDK para S3 (usado contra Cloudflare R2 en Storage).

### Pipeline HTTP (`Program.cs`)

```
UseForwardedHeaders → UseExceptionHandler → CORS (con orígenes) → RequestFailureLoggingMiddleware
  → UseRateLimiter → CSRF (sin el stub) → UseAuthentication → UseAuthorization → endpoints
```

`UseForwardedHeaders` va primero para que todo lo que lee `RemoteIpAddress` —la
partición del rate limiter, la IP de la sesión y la de la auditoría del
registro— vea la IP del cliente y no la del nodo del ingress. Ver
`ForwardedHeaders:KnownNetworks` en [Configuración](#configuración).

La IP del cliente sale de `X-Real-IP`, no de `X-Forwarded-For`
(`ForwardedForHeaderName` en `AddQepForwardedHeaders`). Detrás de Cloudflare,
ingress-nginx manda `X-Forwarded-For: <lo que llegó>, <IP del borde de Cloudflare>`:
la entrada de más a la derecha es el borde, no el cliente, y las de la izquierda
las puede escribir cualquiera. `X-Real-IP` es el `$remote_addr` de nginx, que
resuelve `CF-Connecting-IP` sólo cuando el par es de Cloudflare
(`set_real_ip_from`), así que no se falsifica desde afuera. `X-Forwarded-For` se
ignora por completo.

CORS existe para la SPA en `https://qep.qcode.co`, que llama directo a
`https://qep-api.qcode.co`: mismo sitio —la cookie `SameSite=Lax` viaja— pero
otro origen. La política (`AddQepCors`) permite sólo los orígenes exactos de
`Cors:AllowedOrigins`, con credenciales, los métodos `GET`, `POST`, `PUT`,
`PATCH` y `DELETE`, los headers `Content-Type`, `Authorization`, `X-Qep-Client`,
`X-Tenant-Id` e `If-Match`, sin headers expuestos y con el preflight cacheado 10
minutos. Va antes de CSRF, autenticación y autorización para que el preflight se
conteste sin llegar a ellas y para que un 401, 403 o 422 lleve
`Access-Control-Allow-Origin`: sin él, el navegador le esconde el cuerpo del
error a la SPA. Con la lista vacía el middleware no se registra: registrado sin
orígenes no es neutro, contesta `204` a todo preflight.

La defensa CSRF (`RequireCsrfHeaderMiddleware`, header `X-Qep-Client: web`) se
apoya en esa lista: el navegador sólo manda un header custom desde otro origen
si el preflight pasó. Por eso nunca va un comodín, un origen reflejado ni
`SetIsOriginAllowed`; `CorsSettingsValidator` rechaza al arrancar `*`, `http` y
cualquier cosa que no sea un origen exacto.

`ApiExceptionHandler` (`IExceptionHandler`) centraliza el mapeo de excepciones
a `ProblemDetails` (RFC 7807):

| Excepción                                | Código HTTP                      |
| ---------------------------------------- | -------------------------------- |
| `ResourceNotFoundException`              | 404                              |
| `RequestForbiddenException`              | 403                              |
| `RequestConcurrencyException`            | 412                              |
| `PreconditionRequiredException`          | 428                              |
| `ValidationException` (FluentValidation) | 422, con mapa `errors` por campo |
| `DomainException`                        | 422                              |
| Cualquier otra                           | 500                              |

Toda respuesta de error incluye `code` y `traceId` en las extensiones del
`ProblemDetails`. No hay middleware propio de resolución de tenant: el tenant
se obtiene del claim `tenant_id` a través de `IExecutionContext`.

### Autenticación

El modo se decide en tiempo de ejecución, no está atado al ambiente (ver
sección [Activar y desactivar el modo de desarrollo](#activar-y-desactivar-el-modo-de-desarrollo-auth)):

- **Stub por encabezados** (`DevelopmentAuthenticationHandler`): arma el
  `ClaimsPrincipal` desde `X-Subject-Id`, `X-Tenant-Id`, `X-Permissions` y
  `X-Email`.
- **JWT Bearer real**: valida el token de Google OIDC (`Authority` =
  `accounts.google.com`, `Audience` = Google Client ID) con
  `MapInboundClaims = false` para preservar los claims crudos (`sub`,
  `email`).

### Arquitectura por capas, con reglas ejecutables

Monolito modular con Clean Architecture estricta por módulo. Los siete módulos
son `Audit`, `Authorization`, `Catalog`, `Identity`, `Notifications`, `Storage`
y `Tenancy`, y se componen de hasta cuatro assemblies: `Domain` →
`Application` → `Infrastructure` → `Api`. Un módulo sólo trae las capas que
necesita: `Authorization` es sólo `Application`, y únicamente `Catalog`,
`Storage` y `Tenancy` tienen `Api`. `tests/ArchitectureTests` contiene
un test xUnit por módulo que usa `Assembly.GetReferencedAssemblies()` para
verificar que `Domain` no referencia capas externas, `Application` no
referencia `Infrastructure`/`Api`, e `Infrastructure` no referencia `Api`. La
regla rompe el build si se viola; no es solo convención.

### CQ pattern propio (sin MediatR)

`ICommand<T>` / `IQuery<T>` como marker interfaces, `ICommandHandler` /
`IQueryHandler` como contrato de manejo, e `IRequestDispatcher`
(`RequestDispatcher` en `Bootstrapper`) que resuelve el handler por
reflection (`MakeGenericType` + `dynamic`) desde el contenedor de DI. Los
handlers se registran uno por uno en `QepServiceCollectionExtensions`; no hay
auto-registro por ensamblado.

### DDD

Entidades ricas (p. ej. `Tenant`), identificadores fuertemente tipados
(`TenantId`, `MembershipId` como records que envuelven un `Guid`),
invariantes validados dentro del dominio (cultura BCP 47, zona horaria IANA,
formato de slug) y eventos de dominio acumulados internamente
(`_domainEvents`) que se extraen con `PullDomainEvents()`.

### Concurrencia optimista

La columna `Version` está marcada `IsConcurrencyToken()` y se expone como
`ETag` / `If-Match` en los endpoints HTTP: `428` si falta el encabezado, `412`
si la versión enviada quedó desactualizada.

### Outbox e Inbox (ADR 0009)

Sin broker externo. `OutboxMessage` vive en `platform.outbox_messages`;
`OutboxProcessor` reclama un lote con `FOR UPDATE SKIP LOCKED` dentro de una
transacción, despacha cada mensaje y marca `processed_at`, o incrementa
`attempts` / `last_error` si falla. `OutboxPublisherWorker` es un
`BackgroundService` con `PeriodicTimer` (poll cada 2 segundos) que abre un
scope de DI nuevo en cada tick. `InboxMessage` (clave compuesta
`consumer` + `messageId`) da idempotencia del lado del consumidor.

### Repository + Unit of Work

`ITenantRepository`, `ITenancyUnitOfWork`, etc. abstraen EF Core fuera de la
capa Application.

### Multi-tenancy y autorización

El tenant se resuelve del claim JWT (no de header ni subdominio en runtime).
`IExecutionContext.TenantId` lo expone vía `HttpExecutionContext`, que lee el
`ClaimsPrincipal` actual. La autorización es por claims de permiso
(`RequireClaim(QepClaimTypes.Permission, permiso)`), con policies armadas
dinámicamente por módulo. El único rol de sistema es `admin` (`RoleDefinition` /
`RoleCatalog`, en código); los demás son filas de `authorization.roles` de cada
tenant, que él edita o borra desde `/authorization/roles`.

Cinco de esas filas (`advisor`, `billing`, `cashier`, `scheduling_manager` y
`scheduling_provider`) nacen de plantillas (`RoleTemplate`) que se siembran una sola
vez por tenant, con marca en `authorization.role_seeds`: en el alta (si falla, queda
un log `Error` y el alta sigue) y al arrancar, después de la semilla y bajo el candado
de arranque (si falla, el arranque falla). Un permiso nuevo en una plantilla sólo
llega a los tenants nuevos. Un rol no se puede borrar mientras lo tenga una membresía
`Invited`, `Active` o `Suspended` (`422 authorization.role.in_use`).

### Acceso cruzado entre módulos, controlado

`TenancyDbContext` mapea `AuditEntry` (que pertenece al módulo Audit) como
tabla externa (`ExcludeFromMigrations`, `ownsTable: false`) para que la
auditoría crítica se confirme en la misma transacción que la operación de
Tenancy (ADR 0019). Es una excepción deliberada al aislamiento estricto entre
módulos, a cambio de consistencia transaccional.

### Migraciones por módulo

Cada módulo mantiene su propia carpeta `Migrations/` y su propia tabla de
historial (`__ef_migrations_history` en su esquema). Se aplican de forma
secuencial en `Program.cs` después de `app.Build()`; el orden importa (por
ejemplo, Tenancy se inicializa antes que Audit porque
`DropAuditOwnership` transfiere la propiedad de una tabla entre ambos).

#### Migraciones con pods viejos y nuevos (expand/contract)

Durante un rollout, el pod nuevo migra mientras los viejos siguen atendiendo con el código anterior. Con una réplica
ya pasa (`maxSurge: 1`); con dos, son dos pods viejos contra el esquema nuevo. Por eso (spec
`2026-10-29-replicas-multiples-design.md`, D-5):

1. **Toda migración es expand/contract.** Una versión agrega —columna nullable o con default, tabla, índice— y el
   código viejo la ignora. Quitar, renombrar o volver `NOT NULL` algo que el código viejo escribe va en **otra**
   versión, cuando ya no queda ningún pod viejo vivo.
2. **Una migración que rompe al código viejo se despliega con corte:** estrategia `Recreate` o el backend a cero
   réplicas, como se hizo con las variantes ([Catálogo: opciones, variantes y galería](#catálogo-opciones-variantes-y-galería),
   «Despliegue»). El PDB no lo impide: sólo frena desalojos, no un `scale` ni un `Recreate`.
3. **El handoff de cada spec dice si su migración es aditiva.** Si no lo dice, asume que no.

**Precedente no aditivo: reglas de empresa (spec 2026-11-17, R5).** Las cinco migraciones (`AddCompanyChargesVat`,
`OptionalBillingAccountAndChargesVat`, `OptionalStoreBillingAccount`, `OptionalShopifyStoreBillingAccount`,
`AddCashSessionCompanyChargesVat`) se despliegan **con corte**. Las columnas nuevas son aditivas, pero relajar a nullable el
banco y el número de la cuenta de un pedido no lo es: un pod viejo materializa el pedido sin cuenta y el Excel del ERP
revienta, recalcula con IVA un documento sin IVA y rechaza `bankAccounts: []`. El `Down` de la de Quotations falla si ya hay
pedidos sin cuenta: no se corre después de emitir uno.

**El candado de arranque.** Las 26 inicializaciones (migraciones de cada módulo y siembras) y `RunQepSeedAsync` corren
dentro de `RunUnderStartupLockAsync` (`src/Api/Program.cs`, `src/Bootstrapper/Startup/StartupLock.cs`): una
transacción sobre una conexión propia, directa a Postgres y sin pool, que toma `pg_advisory_xact_lock(20261113, 1)`.
Así dos pods nunca migran ni siembran a la vez; el segundo espera al primero, y cuando le toca, las migraciones ya
están aplicadas y no hace nada. El candado es un semáforo, no una unidad de trabajo: las migraciones siguen en sus
propias conexiones del pool. La conexión se resuelve como la del listener de Mensajería —`QepDatabaseListener` si está,
no está vacía y no es un `#{…}#` sin resolver; si no, `QepDatabase`—: va directo por preferencia; si cae a
`QepDatabase` sigue serializando, porque el candado es de transacción y PgBouncer en modo `transaction` sostiene el
mismo backend mientras la transacción está abierta. Lo que se degrada es el costo: ocupa un backend del pooler durante
todo el bloque y queda sujeto a los timeouts del pooler, que pueden cortar la conexión a mitad del bloque. Los logs dicen cuánto esperó el pod (`EventId` 4108, con el **nombre** de la clave de
conexión, nunca su valor) y cuánto tardó el bloque (4109). Un 4110 (`Warning`) avisa que la conexión del candado se
perdió antes de soltarlo: si pasó a mitad del bloque, otro pod pudo empezar a migrar a la vez, y conviene revisar
el log de los dos. Una inicialización nueva va **dentro** del lambda de `RunUnderStartupLockAsync`, nunca después.

### Observabilidad

OpenTelemetry (único estándar, sin SDKs propietarios) para trazas y métricas,
configurado en
[`QepObservability`](src/BuildingBlocks/BuildingBlocks.Observability/QepObservability.cs).
Instrumentación activa:

| Instrumentación                                   | Aporta                                                                                           |
| ------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| `AddAspNetCoreInstrumentation` (traza + métrica)  | Span raíz por request y el histograma `http.server.request.duration` (p50/p95/p99 por ruta)      |
| `AddHttpClientInstrumentation`                    | Spans de llamadas salientes con `HttpClient`                                                     |
| `AddNpgsql` (Npgsql.OpenTelemetry)                | Un span hijo por comando SQL bajo el span del request; expone N+1 como spans idénticos repetidos |
| Meter `Npgsql`                                    | Conexiones busy/idle/waiting del pool (`db.client.connection.*`)                                 |
| `AddRuntimeInstrumentation`                       | GC, heap, threadpool, contención — distingue degradación de la app vs. de la base                |
| `ActivitySource`/`Meter` propios (`Qep.Platform`) | Instrumentación manual específica del dominio                                                    |

Exporta todo vía OTLP al endpoint de `OpenTelemetry:Endpoint` (o, si no está
configurado, al que resuelva la variable estándar `OTEL_EXPORTER_OTLP_ENDPOINT`
o el default del SDK). El sampling y el recorte de atributos sensibles ocurren
en el Collector, no en la app. En local, el colector corre como contenedor
aparte en `compose.yaml` (puertos `4317`/`4318` para OTLP, `8889` para
métricas).

`service.name` se toma de la variable `OTEL_SERVICE_NAME` (inyectada por el
Deployment de k8s); si no está presente cae al valor local `qep-api`. Nunca se
hardcodea para un ambiente real. El recurso también incluye
`deployment.environment` (el nombre del `IHostEnvironment` actual). Atributos
adicionales como `service.namespace` o `service.instance.id` los añade el SDK
automáticamente si el Deployment define `OTEL_RESOURCE_ATTRIBUTES`.

`db.statement`/`db.query.text` de Npgsql queda parametrizado por defecto (sin
valores literales de los parámetros); no requiere configuración adicional para
evitar filtrar datos sensibles o inflar cardinalidad.

Los logs van a stdout en JSON (`AddQepLogging` en `Program.cs`, formateador
`AddJsonConsole` con `ActivityTrackingOptions` de `TraceId`/`SpanId`/`ParentId`)
para que la infraestructura los indexe y Grafana pueda enlazar traza → log sin
un enricher aparte (no se usa Serilog).

#### Métricas de fluidez del navegador

Todo meter propio cuelga del prefijo `Qep.` y sale por OTLP sin tocar la lista de `QepObservability`. El meter
`Qep.Web` lo alimenta la SPA, que mide la navegación entre pantallas y los Web Vitals y los manda por lotes a:

```
POST /api/v1/tenants/{tenantId}/telemetry/web-vitals   → 204
{ "samples": [ { "metric": "navigation"|"lcp"|"inp"|"ttfb"|"fcp"|"cls", "route": "/quotations/$id", "value": 1.2 } ] }
```

Pide sesión del tenant (sin permiso propio) y el header `X-Qep-Client`. Hasta 50 muestras por lote; `value` entre 0 y 120
(segundos, salvo `cls`, que es un puntaje); `route` es obligatoria sólo en `navigation` y es la **plantilla** de la ruta
del router, nunca la URL (con ids saldría una serie por registro). Un lote inválido responde 422 `validation.failed` con
`errors`. Límite: 600 lotes por minuto por IP (política `telemetry`). La etiqueta `route` admite 256 valores distintos
por proceso: las primeras 256 rutas distintas que ve un proceso se quedan con su nombre y el resto sale como `other`.
El cuerpo pesa como máximo 16 KB; uno ilegible, mal tipado o más grande responde 422 con el error en `Samples`.

| Métrica                       | Unidad | Etiqueta | Qué mide                                            |
| ----------------------------- | ------ | -------- | --------------------------------------------------- |
| `qep.web.navigation.duration` | s      | `route`  | Lo que tarda una pantalla en mostrarse tras pedirla |
| `qep.web.vital`               | s      | `name`   | `lcp`, `inp`, `ttfb` y `fcp`                        |
| `qep.web.cls`                 | —      | —        | Cumulative Layout Shift                             |

Las colas asíncronas y el rezago de la mensajería salen de los meters `Qep.Platform` y `Qep.Messaging`.
`QueueSamplerWorker` lee cada 15 s, una por una, las seis colas (`webhook`, `automation`, `media`, `campaign`, `outbox` y
`shopify_webhook`) con SQL crudo y las publica como gauges. Una cola que falla se loguea y se salta; una lectura de más de
60 s deja de publicarse, así que un hueco en el panel significa que el muestreador (o esa cola) no está respondiendo, no que
la cola esté vacía.

| Métrica                     | Unidad | Etiqueta  | Qué mide                                                                                              |
| --------------------------- | ------ | --------- | ----------------------------------------------------------------------------------------------------- |
| `qep.queue.backlog`         | —      | `queue`   | Filas pendientes de la cola                                                                           |
| `qep.queue.retrying`        | —      | `queue`   | De esas, las que ya se intentaron al menos una vez y reintentan                                       |
| `qep.queue.oldest_age`      | s      | `queue`   | Edad de la pendiente más vieja que nadie ha intentado (`attempts = 0`)                                |
| `qep.messaging.inbound.lag` | s      | `channel` | Histograma: segundos entre la hora que Meta le pone a un mensaje entrante y el momento en que se guarda |
| `qep.webhook.discarded`     | —      | `provider`, `reason` | Contador: eventos de Meta que llegaron con firma válida y no se ingresaron a propósito     |
| `qep.webhook.last_received` | s      | `provider` | Hora Unix del último webhook de Meta aceptado (el `received_at` más reciente de `webhook_deliveries`) |

`oldest_age` ignora lo que ya reintenta (una fila sana en reintento largo o un outbox envenenado no son atraso); en
`automation` también las que espera una anterior de su misma conversación, y en `media` los medios importados con el
historial y los abandonados. `campaign` no tiene columna de edad, así que sólo publica `backlog` y `retrying`. En
`qep.messaging.inbound.lag` la etiqueta `channel` es `whatsapp` o `instagram`; un timestamp a futuro (reloj desfasado) cuenta cero.

`qep.webhook.discarded` (spec 2026-11-16) suma en el mismo punto donde `WebhookDeliveryProcessor` loguea un descarte, con
el mismo conteo, salvo una excepción: en Instagram, `unknown_route` no vuelve a contar los eventos que ya contaron como
`ignored` (el log sí los incluye). `provider` es `whatsapp` o `instagram`; `reason`, uno de `WebhookDiscardReasons`:
`unknown_route` (número o cuenta sin conexión), `without_user_id`, `paused`, `module_off`, `foreign_status` (estado de un
mensaje que QEP no mandó: coexistencia), `unhandled_field`, `foreign_event` (visto o borrado de Instagram sin mensaje),
`ignored` e `history_invalid` (mensaje del historial de coexistencia sin id o sin fecha). La alerta `QepWebhookDiscarding`
de `migracion-k8s` mira sólo `unknown_route` y `without_user_id` (`WebhookDiscardReasons.Alerting`), comparados con la misma
hora de la semana anterior; los demás son tráfico normal y sólo van al panel. Esas cuatro series (dos proveedores por dos
motivos) nacen en cero al crear `MessagingMetrics`: `increase()` toma la primera muestra como base, y sin la semilla el primer
lote después de cada reinicio no contaría. Un reintento de la entrega vuelve a contar, como el log.

`qep.webhook.last_received` la lee `QueueSamplerWorker` en la misma vuelta que las colas (sólo el pod líder) y caduca igual a los
60 s. Sale de la base, no del tráfico HTTP: sobrevive a un reinicio y sólo cuenta lo que pasó la firma, así que después de un
despliegue dice cuándo entró el último webhook de verdad. Un proveedor sin entregas no publica. El panel «Último webhook de
Meta» la muestra como «hace X»; no hay alerta de silencio (owner, 2026-11-16).

Ninguna lleva tenant ni datos de la persona. El dashboard de Grafana `QEP Backend` no vive en este repo: está en
`migracion-k8s/k8s/platform/dashboards/qep-backend-dashboard.yaml`.

El cliente de Valkey (spec 2026-11-13) publica tres métricas en el meter `Qep.Platform`. Valkey **no** entra a
`/health/ready`: si se cae, cada área vuelve a su respaldo local y la caída se ve acá, no sacando pods del Service.

| Métrica                | Prometheus                         | Etiqueta | Qué mide                                                                                       |
| ---------------------- | ---------------------------------- | -------- | ---------------------------------------------------------------------------------------------- |
| `qep.valkey.connected` | `qep_valkey_connected`             | —        | 1 si el cliente del pod está conectado; 0 si está apagado (`Valkey:Enabled`), sin cadena o caído |
| `qep.valkey.fallbacks` | `qep_valkey_fallbacks_total{area,reason}` | `area`, `reason` | Operaciones que no llegaron a Valkey (sin conexión, tope de `OperationTimeoutMs` o error) y usaron el respaldo del área: `presence`, `rate_limiting`, `sessions`, `permissions`, `send_budget`, `doorbells`, `api_key_touch`. `reason` es `NotConnected` (el multiplexer no estaba conectado), `TimeoutException` (pasó `OperationTimeoutMs`, 100 ms por defecto) o el tipo de la excepción (`RedisConnectionException`, `RedisServerException` con un `NOPERM` de la ACL…) |
| `qep.valkey.operation.duration` | `qep_valkey_operation_duration_seconds` (histograma) | `area`, `outcome` | Cuánto tarda cada operación pedida con `ValkeyGateway.TryAsync`, medida desde el pod. `outcome`: `ok`, `error`, `late` (pasó el tope y después respondió; se mide al terminar) y `late_error` (pasó el tope y después falló). No mide `Publish` ni `Subscribe`, y sin conexión no hay operación que medir |

La alerta sobre las dos primeras no vive en este repo: está en `migracion-k8s`, con las demás reglas de la plataforma.

**Limitadores compartidos (D-3, `Valkey:Areas:RateLimiting`).** Las siete políticas de ventana fija de `Program.cs` y el
límite por clave de la API externa pasan por `SharedRateLimitPartitions`. Con el área prendida cuentan en Valkey, en la
clave `qep:rl:{política}:{partición}:{inicioDeVentana}`. La ventana está alineada a la época Unix, así que todos los pods
cortan en el mismo segundo. Si Valkey no responde, cada partición vuelve al limitador de memoria de su pod: nunca deja
pasar sin límite. El limitador de concurrencia del webhook se queda en memoria siempre, porque protege al pod.

- **Claves con IP.** En las políticas sin hash (`public`, `telemetry`, `external`, las de la tienda menos credenciales) la
  IP del cliente va tal cual en el nombre de la clave. La clave vive como mucho la ventana más 1 s.
- **Claves con patrón de ruta.** `credentials` y `storefront-credentials` llevan el patrón de ruta, y entran hasheadas
  (`ShortHash`).
- **Métricas.** Con el área prendida, la métrica `aspnetcore.rate_limiting` de requests en cola cuenta **todos** los
  requests como encolados. El middleware los manda a `AcquireAsync`, porque el camino síncrono siempre niega: no es una
  cola real.

**Prender las áreas en producción (paso 3 del despliegue).** El orden, el criterio entre pasos y el rollback están en
[spec de Valkey § Despliegue](docs/superpowers/specs/2026-11-13-valkey-estado-compartido-design.md#despliegue) y
[§ Verificación](docs/superpowers/specs/2026-11-13-valkey-estado-compartido-design.md#verificación); acá va sólo lo que el código fijó.
Lo hace el owner, un área por commit
(`chore(k8s): enable valkey <area> area`, el flag en `"true"` en `k8s/prod-configMap.yaml`). Después de `Presence` y
`RateLimiting`: `Valkey__Areas__Sessions`, `Valkey__Areas__Permissions`, `Valkey__Areas__Doorbells`,
`Valkey__Areas__ApiKeyTouch` y `Valkey__Areas__LegalParameters` (la tabla de valores legales de Hr, spec 2026-11-22: contador
`qep:legalparams:ver` y tabla en `qep:legalparams:{ver}` con TTL de 1 h; apagada, copia del pod de 60 s). Con el área prendida, una migración o un SQL manual sobre `hr.legal_parameters*` no sube la versión: hace falta `INCR qep:legalparams:ver` (o un reinicio, que la sube al arrancar) para verlo antes de que venza la hora. El presupuesto de envío de campañas no está en la lista: se queda en Postgres
(`messaging.connection_send_budgets`) por decisión del owner del 2026-11-13.

**Con `Sessions` y `Permissions` apagadas hay caché igual, pero por pod** (2026-11-17). Antes, apagada el área era «cada request
lee Postgres», y con la base en otro nodo eso costaba cinco viajes en serie (sesión, membresía, estado del tenant, roles y
módulos) antes de que corra ningún handler. `MemorySessionCache` y `MemoryPermissionCache` (`src/Bootstrapper/Valkey/`) son las
mismas reglas que las de Valkey —15 min con marca de revocación; 60 s con la versión de acceso del tenant, que suben los mismos
casos de uso— en la memoria del pod. Con una réplica son equivalentes; con dos, un bump o una revocación en un pod no llega al
otro hasta que la entrada venza, y por eso `MultiReplicaInventory` exige las dos áreas prendidas para `replicas > 1`. Dentro de un
request, además, `MembershipDirectory` y `TenantModules` memoizan por scope: la membresía y los módulos se leen una vez aunque
los pidan la autenticación y el handler.

Mientras las áreas estén apagadas hay dos exposiciones que antes no existían, porque el efecto era inmediato:

- **Despliegue.** Con `RollingUpdate` y `maxSurge: 1` (`k8s/prod-deployment.yaml`) el pod viejo y el nuevo conviven por minutos.
  Una suspensión o un cambio de roles aplicado en uno llega al otro en ≤ 60 s, y una revocación de sesión en ≤ ~5 min: el umbral
  de toque de `SessionService` vuelve fallo toda entrada de más de cinco minutos.
- **Runbooks por SQL.** Un cambio a mano en `tenancy.tenant_modules`, en roles, en membresías o en `identity.sessions` —prender
  `pos` con la fila de `tenant_modules`, por ejemplo— no sube la versión de acceso: el pod que ya corre lo ve en ≤ 60 s
  (permisos) o ≤ 5 min (sesiones), no en el siguiente request.

**El chequeo es la métrica, no el log.** El gateway registra cada respaldo en `Debug` («Valkey fallback in area …») y
producción corre en `Information`: un `logs | Select-String "fallback"` sale vacío aunque haya respaldos. Lo que cuenta es
el contador `qep.valkey.fallbacks` (Prometheus `qep_valkey_fallbacks_total`, etiquetas `area` y `reason`). En Grafana, durante el día
que pasa entre un área y la siguiente:

```promql
sum by (reason) (increase(qep_valkey_fallbacks_total{area="<etiqueta>"}[1h]))
```

tiene que dar 0 o sin datos en condiciones normales (la serie no existe hasta el primer respaldo de esa área en la vida
del pod). Si da distinto de cero, `reason` dice por qué: `TimeoutException` es una operación que no respondió en
`OperationTimeoutMs` (100 ms) —desde el pod no se distingue si tardó Valkey, la red o el propio proceso—,
`NotConnected` una reconexión del cliente, y `RedisServerException` un comando que la ACL de producción rechaza. La serie
vieja sin `reason` sigue en Prometheus hasta que vence su retención.

Cuánto tardan de verdad las que pasan el tope se ve en el histograma:

```promql
sum by (le, area) (increase(qep_valkey_operation_duration_seconds_bucket{outcome=~"late.*"}[24h]))
sum by (area, outcome) (increase(qep_valkey_operation_duration_seconds_count[24h]))
histogram_quantile(0.999, sum by (le, area) (rate(qep_valkey_operation_duration_seconds_bucket[1h])))
```

Si las `late` caben casi todas bajo `le="0.15"` o `le="0.25"`, el tope está corto; si caen en segundos, el problema no se
arregla subiéndolo. La segunda consulta da la proporción real de operaciones que se pierden. Para confirmar qué áreas están prendidas, en PowerShell (el ConfigMap no lleva
secretos; nunca `get secret -o yaml` ni `-o json`):

```powershell
kubectl --context contabo-prod -n prod-qep-backend get configmap qep-backend-config -o jsonpath='{.data}' | Select-String -Pattern "Valkey__"
```

| Área          | Etiqueta `area` | Contador                         | Comportamiento que se verifica                                                                          |
| ------------- | --------------- | -------------------------------- | ------------------------------------------------------------------------------------------------------- |
| `Sessions`    | `sessions`      | `increase(...[1h])` = 0          | Cierra sesión y repite un request con la cookie vieja: 401 al instante (prueba que el `DEL` corre)      |
| `Permissions` | `permissions`   | `increase(...[1h])` = 0          | Suspende un miembro con su sesión abierta y pide un endpoint del tenant: 403 en el request siguiente     |
| `Doorbells`   | `doorbells`     | `increase(...[1h])` = 0          | La edad de la cola `automation` baja en el dashboard. Con dos pods, sólo uno publica las métricas de colas por vuelta: el que tiene `qep:leader:queue-sampler`, que también gobierna esta área (plan P14) |
| `ApiKeyTouch` | `api_key_touch` | `increase(...[1h])` = 0          | Sin comportamiento visible: el contador es la verificación                                               |
| `LegalParameters` | `legal_parameters` | `increase(...[1h])` = 0     | Cambia un valor legal desde la consola del operador y léelo desde otra réplica: el valor nuevo se ve al instante |

Si el contador sube sin que Valkey esté caído (`qep_valkey_connected` en 1), revisa primero la ACL del usuario `qep`:
el gateway convierte un `NOPERM` en respaldo. `ValkeyAclCompatibilityTests` dice qué comandos usa cada área.

**Flota mezclada (I-3).** Mientras un despliegue prende `Sessions` o `Permissions` (o tras un rollback seguido de volver a
prenderlas dentro del TTL), los pods con el área apagada no invalidan: usan `NullSessionCache` y `NullTenantAccessVersion`.
Un cierre de sesión o una suspensión que atendió un pod viejo puede seguir sirviéndose desde caché en un pod nuevo hasta
~5 min en sesiones (un acierto exige `LastSeenAt` dentro del `TouchThreshold` de 5 min) y hasta 60 s en permisos
(`EntryTtl`). El cambio no es instantáneo: espera a que termine el rollout y, si en esa ventana una revocación tiene que
ser inmediata, hazla cuando el rollout ya terminó.

**Antes de prender `Doorbells`** (decisiones del owner, no se resuelven en el código):

- **I-1, carga de despertares.** Cada insert confirmado en el outbox despierta a todos los oyentes del outbox de cada pod
  (13 definiciones de correo en un solo bucle más 8 consumidores: ~21 consultas de candidatos o reclamo por pod y por
  commit). Con tráfico sostenido puede superar la carga fija de polling de hoy. Mide primero la tasa de inserts del
  outbox o agrega un espaciado mínimo entre ticks disparados por timbre.
- **I-2, filas que vencen por tiempo.** Las filas que se vuelven reclamables por reloj (backoff de reintento, leases
  vencidos, filas escritas bajo la transacción de otro contexto) no timbran: esperan la red de seguridad en vez del poll
  de hoy (`automation` 1 s → 15 s, `webhook` 3 s → 15 s, `shopify_webhook` 15 s → 30 s, `media` 5 s → 30 s, `export`
  5 s → 30 s, `outbox` 15 s → 30 s; `DoorbellQueues.SafetyNet`). Caso concreto: con dos réplicas, un mensaje del bot
  reprogramado +5 s porque otro pod tiene el candado de la conversación responde entre 5 y 15 s más tarde.
- **I-4, líder del muestreador.** Sólo el pod que tiene `qep:leader:queue-sampler` publica los gauges de colas; las
  lecturas de los demás expiran a ~60 s y el liderazgo puede alternar porque su TTL (15 s) es igual al intervalo.
  Confirma que alertas como `QepQueueMetricsMissing` agregan entre pods (viven en el repo de plataforma) antes de
  prender `Doorbells` con dos réplicas.
- **Notifications.** Con `Doorbells` prendido, el drenado hasta vaciar quita el freno de hoy (~20 filas por definición
  cada 15 s). `SendAsync` marca un envío fallido como `Failed` sin reintento, así que una caída del proveedor quema todo
  el backlog de correos en segundos. Decide si agregas reintento o backoff antes de prender.

### Infraestructura local

`compose.yaml` levanta PostgreSQL 18, Valkey (`valkey/valkey:9.1.3-alpine`, en `6379`) y el `otel-collector`. No hay
broker de mensajería: el patrón Outbox cumple ese rol dentro del monolito. Si ya tienes otro Valkey en `6379`, no levantes
el del compose: chocan el puerto.

### Biblioteca de archivos (Cloudflare R2)

El módulo Storage acepta PDF, DOC, DOCX, XLS, XLSX, JPG, JPEG, WEBP y PNG, con
un máximo de 25 MB. El API crea una URL PUT firmada de cinco minutos para una
clave temporal bajo `staging/`; el navegador carga directamente a R2. Al
completar, el backend comprueba tamaño, extensión, MIME y firma binaria, ejecuta
el escáner configurado y promociona el objeto mediante copy + delete a `files/`.
Las cargas abandonadas se purgan automáticamente después de 24 horas.

Para JPG, JPEG, PNG y WEBP, la confirmación genera una variante `thumbnail` en
WebP (calidad 80, máximo 320×320, sin metadatos EXIF). La variante se guarda
bajo `variants/thumbnail.webp`, se registra en `storage.file_variants` y se
obtiene con `POST /files/{fileId}/download-url?variant=thumbnail`. Las imágenes
de más de 40 megapíxeles se rechazan para limitar el costo de decodificación.

El bucket privado debe permitir el preflight CORS del navegador. Se incluye
[`ops/r2-cors.example.json`](ops/r2-cors.example.json); antes de aplicarlo,
reemplaza sus orígenes por los dominios reales del frontend. Las credenciales y
el bucket se configuran bajo `Storage:R2`; nunca se entregan al cliente.
Para AWS CLI se incluye la variante envuelta en `CORSRules` en
[`ops/r2-cors.aws.json`](ops/r2-cors.aws.json).

**Fotos de producto liberadas.** Borrar un producto, o quitar una foto de su galería con el `PUT`,
escribe `catalog.product.images-released.v1` (`{ tenantId, fileIds }`, sólo si la lista no está vacía) en el
outbox, en la misma transacción. `ProductImageReleaseWorker` (Storage) lo consume con el inbox
`storage.inbox_messages` y, por cada archivo, consulta las `IFileReferenceProbe`: si otra galería (sonda de
Catalog) u otro dueño lo sigue referenciando, lo salta. Sólo purga archivos `Product` sin referencias, y
siempre borra primero los objetos (clave del archivo, copia pública y cada variante) y después marca
el `FileResource` como `Purged`, auditado como `storage.file.purged` / `product_image_released`. Para
la copia pública reutiliza la despublicación, así que una foto publicada pierde `PublicStorageKey` y
`PublishedAt`; es a propósito.

**Archivos de dueño de sistema.** Los tipos de `SystemOwnedFiles` (Storage.Domain; hoy `SupportTicket`, los adjuntos del formulario
público de Soporte) no existen para la consola: `GET /files` no los lista ni filtrando por ese dueño, toda ruta por id responde `404
storage.file.not_found` (el mismo de otro tenant) y `POST /files` con ese `ownerType` es `422 storage.file.owner_type_invalid`. Se crean,
completan y bajan sólo por `IStorageSystemUploads`, desde el módulo dueño. Ver [Formulario público de
Soporte](#formulario-público-de-soporte-fase-3).

**Archivos que piden un permiso extra.** Los tipos de `FileOwnerModules.RequiredPermissionByOwnerType` (hoy `WmsExport`, el Excel de
saldos de Bodegas, con `wms.stock.export`) exigen ese permiso además del `storage.file.*` de la ruta: sin él, `GET /files` no los lista y
toda ruta por id (descarga, publicación, despublicación, metadatos, completar, cancelar, borrar) responde `403 authorization.denied`.
Tampoco los ven Catalog ni Quotations: para ellos el id no existe. `WmsExport` además lo genera el servidor (`ServerGeneratedFiles`), así
que `POST /files` con ese `ownerType` es `422 storage.file.owner_type_invalid`.

### Reportes exportados (`exports/`)

La exportación del padrón de clientes (`POST /tenants/{tenantId}/customers/export`) y las de los
listados de cotizaciones y pedidos (`POST /tenants/{tenantId}/quotations/export`,
`POST /tenants/{tenantId}/orders/export`) no devuelven el archivo: lo suben bajo el prefijo
`exports/` del **bucket privado** y le mandan a quien la pidió un correo con una URL prefirmada.
Clientes arma el Excel dentro del request; cotizaciones y pedidos contestan `202` y lo encolan en
`quotations.export_jobs`, y `ExportJobWorker` lo arma en segundo plano con la clave
`exports/tenants/{tenantId}/jobs/{jobId}.xlsx` —un reintento pisa el mismo objeto—. La vigencia
del enlace es `Storage:ExportUrlHours` (24 h por defecto), propia y no
`Storage:PresignedUrlMinutes`: aquellas URLs las consume un navegador que ya está en pantalla, y
ésta espera en una bandeja de entrada.

**Estos objetos no los purga la aplicación.** `StagingCleanupWorker` se guía por filas de
`storage.file_resources`, y una exportación no crea ninguna. La limpieza es una **regla de
lifecycle del bucket**, configurada a mano en Cloudflare porque el repositorio no tiene
infraestructura como código para R2:

```powershell
npx wrangler r2 bucket lifecycle add <bucket-privado> expire-exports "exports/" --expire-days 2
npx wrangler r2 bucket lifecycle list <bucket-privado>   # para verificarla
```

El prefijo **no es opcional**: una regla sin él aplica a todo el bucket y se lleva puestos
`files/` y `staging/`. Y `--expire-days` tiene que cubrir con margen a `ExportUrlHours`, o un
enlace todavía vigente puede apuntar a un objeto ya borrado. Cloudflare ejecuta las reglas dentro
de las 24 h posteriores al vencimiento, así que el objeto vive **entre 2 y 3 días** con el valor
de arriba; el error siempre va para el lado seguro.

El análisis antimalware usa el protocolo `INSTREAM` de ClamAV. En producción
configura `Storage:ClamAv:Enabled=true`, junto con `Host`, `Port` y
`TimeoutSeconds`. Si ClamAV no responde, el archivo no se promociona. El modo
deshabilitado existe únicamente para desarrollo local y pruebas.

### Comprobantes de pago públicos (`payment-proofs/`)

Con `Quotations:PaymentProofs:PublicLinks=true`, cada comprobante de pago **nuevo** —al convertir
una cotización en pedido o al sumarle comprobantes— se copia del bucket privado al **bucket
público** con la clave aleatoria `payment-proofs/{guid}.{pdf|jpg|png|webp}`, y el Excel de pedidos lo
enlaza en las columnas «URL Comprobante 1» a «URL Comprobante 5», con la URL misma como texto
clicable. Al final de la hoja, después de `Cod. Asesor`, van `Banco` y `Cuenta` —la cuenta de
facturación del pedido— y, por comprobante, «V. Comprobante N» con su monto y «URL Comprobante N».
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
  copias ya hechas siguen en el bucket, y borrar un comprobante `User` en Storage tampoco toca su
  copia. Un comprobante `PaymentProof` que algún pedido referencia **no** se puede borrar ni
  despublicar: `DELETE /files/{id}` y `DELETE /files/{id}/publication` responden 422
  `storage.file.invalid_state` sin tocar el bucket, porque su copia pública es la que enlaza el
  Excel. `PUT /files/{id}/publication` rechaza siempre un `PaymentProof`, con el mismo código: sólo
  llega al público al adjuntarse a un pedido.
- **Reemplazar o quitar un comprobante borra su archivo** (spec 2026-09-16, D19): corregir el archivo
  con `updatedProofs[].newFileId` o quitar el comprobante con `DELETE /orders/{orderId}/proofs/{proofId}` escribe
  `quotations.order.payment-proofs-detached.v1` con el pedido, y segundos después Storage borra la
  copia pública de ese adjunto. Si es un `PaymentProof` que ningún otro comprobante usa, borra además
  el archivo —del bucket público si ya se movió, de `staging/` si no— y lo marca `Purged`, auditado
  como `storage.file.purged` / `payment_proof_detached`. El original privado de un comprobante `User`
  no se toca. Un Excel ya enviado con la URL vieja muestra un enlace roto: es a propósito.
- La copia conserva el `Content-Type` del original (`CopyObject` usa `MetadataDirective = COPY` por
  defecto), así que un PDF se abre en el navegador en vez de descargarse.
- Un Excel bajado de internet abre en **Vista protegida**, y ahí ningún enlace responde hasta que
  se toca «Habilitar edición». Es comportamiento de Office, igual para cualquier enlace.

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
5. **`PaymentProofOrphanCleanupWorker`** (unos 5 minutos después de arrancar y desde ahí cada
   `Storage:PaymentProofOrphanCleanup:IntervalHours`) recorre sólo `payment-proofs/` del bucket
   público y borra lo que tiene más de `MinimumAgeHours` y que ningún `FileResource` ni
   `OrderPaymentProof` referencia, auditándolo como `storage.public_object.purged`. La primera
   corrida no espera un intervalo completo porque el temporizador no se guarda: con deploys más
   seguidos que `IntervalHours` nunca correría. Cada réplica corre la suya, a la vez que las demás;
   se acepta porque borrar un objeto que ya no existe no falla, y en producción `DryRun` arranca en
   `true`. **Arranca con `DryRun=true`** en `appsettings.json` y en el
   ConfigMap: sólo escribe `Payment proof orphan cleanup (dry run) would delete …` en el log. Se pasa
   a `false` a mano, en un commit propio, después de revisar esos logs en producción. Si no hay
   ninguna sonda de referencias registrada no borra nada y lo avisa con un Warning; un objeto cuyo
   borrado falla se registra como Error y se reintenta en la corrida siguiente; si lo que falla es
   auditar un borrado ya hecho, el Error lo dice y no hay reintento.

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

Enviar una cotización (`POST /quotations/{id}/send`) manda un WhatsApp al cliente con el PDF
adjunto, a través de una plantilla aprobada por Meta. Sale por **una conexión del tenant**, de Zenvia o de
WhatsApp Business (Meta) (Integraciones, planes 2026-10-21): en Zenvia, cuál es la plantilla vigente lo dice
su campo `templateId`, y el número emisor, su `fromNumber`. No hay cuenta ni plantilla global.

**Qué canal usa (spec 2026-10-21 §4.2).** Los canales son las conexiones de Zenvia y de Meta del tenant que
están disponibles. Con uno solo, sale por él. Con varios, sale por la elección guardada del tenant
(`quotations.tenant_whatsapp_channels`, migración nueva de Quotations) o por el `connectionId` del cuerpo. Los cuatro
códigos, todos 422 que dejan la cotización como estaba y sin `SendFailed`:

| Código | Cuándo |
| --- | --- |
| `quotation.whatsapp.no_channel` | No hay canales disponibles |
| `quotation.whatsapp.channel_required` | Hay varios y ninguno elegido vigente; no viaja la lista, el cliente pide el `GET` |
| `quotation.whatsapp.channel_unavailable` | El `connectionId` no está entre los disponibles |
| `quotation.whatsapp.channel_already_set` | Ya hay una elección vigente y el `connectionId` es otro (el mismo id se acepta) |

Con varios canales y ninguno elegido, un envío con `connectionId` válido **guarda** la elección para todo el
tenant, pero sólo si el envío salió. Las fallas del envío en sí (`quotation.whatsapp.send_failed`,
`recipient_missing`, `quotation.quotation.billing_phone_missing`, `quotation.send.failed`) dejan la cotización
en borrador con `SendFailed` en el historial y no dicen por qué canal fue.

**Dos endpoints** (bajo `/api/v1/tenants/{tenantId}`):

- `GET /quotations/whatsapp-channels` lista los canales (Meta primero, después Zenvia, cada grupo por nombre)
  con `selectedConnectionId` (sólo si la elección sigue disponible) y `needsChoice`. Se lee con
  `quotations.quotation.manage`, `quotations.order.manage` (spec 2026-11-09 D8: quien manda pedidos pregunta por
  el canal) **o** `tenancy.settings.update` (decisión del owner DP-F1, 2026-10-25: quien elige el canal desde
  Configuración no necesariamente envía cotizaciones). Política `QuotationsPolicies.WhatsAppChannelsRead`, y el
  handler revalida con `QuotationsAuthorization.EnsureAuthorizedAny`. Acepta `?document=quotation|order` (default
  `quotation`; otro valor es `422 quotation.whatsapp.document_invalid`): con `order` la lista es la de canales
  disponibles para pedidos, y `selectedConnectionId` y `needsChoice` se calculan sobre ella.
- `PUT /quotations/whatsapp-channel` con `{ "connectionId": "…" }` guarda la elección y responde con la forma
  del `GET`. Exige `tenancy.settings.update`; `422 validation.failed` (`errors.connectionId`) si falta o es
  `Guid.Empty`, y `422 quotation.whatsapp.channel_unavailable` si no está disponible.

Las variables de la plantilla de Zenvia son las que `ZenviaWhatsAppSender` completa en runtime:

| Variable | De dónde sale |
| --- | --- |
| `documentUrl` | URL pública de la copia que publica `IQuotationPdfStorage.PublishAsync` (`SendQuotation.cs`); Meta no puede bajar una prefirmada de R2 |
| `fullname` | `Customer.Name` |
| `order_number` | `Quotation.QuotationNumber` |
| `total` | `Quotation.Total`, formateado `C0` en `es-CO` |
| `valid_until` | `Quotation.ValidUntil`, formateado `d 'de' MMMM 'de' yyyy` en `es-CO` |

**El pedido usa una segunda plantilla** (`POST /orders/{id}/send`, ver
[Enviar el pedido por WhatsApp](#enviar-el-pedido-por-whatsapp)), y el campo `orderTemplateId` de la
conexión apunta a ella. Sus variables son las de la cotización menos
`valid_until` —un pedido no vence—, con el documento como header `MEDIA_DOCUMENT`:

| Variable | De dónde sale |
| --- | --- |
| `documentUrl` | URL pública de la copia del PDF del pedido (`SendOrder.cs`) |
| `fullname` | `Customer.Name`, o el nombre de la facturación propia del pedido con `recipient: "Billing"` |
| `order_number` | `Order.OrderNumber` |
| `total` | `Order.Total`, con el mismo formato que la cotización |

> **Zenvia:** el texto de la plantilla del pedido, su `name` y su aprobación siguen siendo del owner. Por eso
> `orderTemplateId` es opcional en la conexión: sin él, la conexión no está disponible para pedidos (spec 2026-11-09 D6)
> y, si no queda otra, el endpoint responde `order.whatsapp.no_channel`. Se crea con el mismo procedimiento de abajo,
> cambiando `components.body.text` y quitando `valid_until` de `examples`, y su `id` se carga en la conexión de cada
> tenant. **Meta** no necesita nada de esto: la plantilla `qep_pedido` la aprovisiona QEP
> (`POST …/template-management/order`).

**Cambiar el texto de la plantilla no se hace acá: se crea una plantilla nueva en Zenvia y se
edita el `templateId` de la conexión de Zenvia del tenant con el `id` que devuelva.** Meta no permite
editar una plantilla aprobada, y en Zenvia la plantilla va atada al número emisor: cada tenant usa la
que creó con su propia cuenta. Si además cambian las variables, hay que tocar
`WhatsAppQuotationMessage` y `ZenviaWhatsAppSender`, y actualizar el `templateId` de las conexiones en
el mismo despliegue.

**Despliegue del envío por canal (spec 2026-10-21 §7.4).** Backend antes que frontend. Es una migración nueva
de Quotations (`tenant_whatsapp_channels`), **sin backfill** (plan, Decisión 15). Un tenant con Meta aprobado
más Zenvia pasa a `channel_required` hasta que alguien elija, desde el envío o con el `PUT` (un administrador
puede elegir antes del despliegue del frontend).

**Paso manual, justo después de desplegar el backend y antes de que alguien envíe:**

- [ ] Para cada tenant con Zenvia **y** una conexión de Meta con la plantilla de cotización `APPROVED`, elige el
  canal con el `PUT`. Sin esto, todo envío de ese tenant responde `quotation.whatsapp.channel_required` y el
  frontend actual no sabe contestarlo: «Enviar» queda roto hasta que se elija.
- [ ] Antes, mira si Origen Botánico tiene esa conexión de Meta: `GET .../quotations/whatsapp-channels` lista
  los canales disponibles, y si `needsChoice` es `false` no hay nada que hacer. Con un solo canal disponible
  tampoco hace falta el `PUT`.

```powershell
'{ "connectionId": "<id-de-la-conexion>" }' | Set-Content -Encoding ascii canal.json
curl.exe -s -X PUT "https://qep-api.qcode.co/api/v1/tenants/<tenantId>/quotations/whatsapp-channel" `
  -H "Content-Type: application/json" -H "Cookie: <cookie-de-sesion-de-un-administrador>" -H "X-Qep-Client: web" `
  -d "@canal.json"
```

El `PUT` exige `tenancy.settings.update` y responde el mismo documento que el `GET`. Un `connectionId`
que no esté disponible da `422 quotation.whatsapp.channel_unavailable`.

#### Crear una plantilla nueva

```bash
curl --location 'https://api.zenvia.com/v2/templates' --header 'X-API-TOKEN: <zenvia-api-token>' --header 'Content-Type: application/json' --data-raw '{
  "channel": "WHATSAPP",
  "name": "cotizacion_pdf_cliente",
  "locale": "es",
  "senderId": "<el fromNumber de la conexión de Zenvia del tenant>",
  "category": "UTILITY",
  "components": {
    "header": { "type": "MEDIA_DOCUMENT" },
    "body": {
      "type": "TEXT_TEMPLATE",
      "text": "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."
    },
    "footer": { "type": "TEXT_FIXED", "text": "Este mensaje fue generado automáticamente." }
  },
  "examples": {
    "documentUrl": "https://pdfobject.com/pdf/sample.pdf",
    "fullname": "Juan Perez",
    "order_number": "COT-000123",
    "total": "2.450.000 COP",
    "valid_until": "30 de septiembre de 2026"
  }
}'
```

Devuelve el `id` de la plantilla y `status: "WAITING_REVIEW"`. Meta responde en minutos; el
estado se consulta con `GET /v2/templates/{id}`, y un `REJECTED` trae el motivo en `comments`.

Reglas que no se deducen del schema y que rebotan la plantilla:

- **El PDF viaja como `documentUrl` dentro de `fields`**, no como un contenido aparte de tipo
  `file`. La misma clave se usa en `examples` (`imageUrl`/`videoUrl` para los otros medios).
- **`senderId` es el número emisor**, el mismo valor que el `fromNumber` de la conexión. El
  ejemplo con forma de UUID que trae la referencia de Zenvia es del parámetro genérico de
  filtrado, compartido por todos los canales. No existe un endpoint `/senders`: para leerlo,
  `GET /v2/templates/{id}` de una plantilla existente.
- **`name` sólo admite minúsculas, dígitos y guiones bajos.** Un espacio o una mayúscula rompen
  el envío a Meta. No se puede reusar el nombre de una plantilla existente, ni siquiera
  rechazada.
- **`examples` es obligatorio para WhatsApp**, con una clave por variable y ninguna vacía. Evitar
  `$`, `#` y `%` en los valores: Zenvia los lista como causa frecuente de rechazo. Sólo afecta a
  la revisión de Meta — el mensaje real se formatea en runtime y sí lleva el `$`.
- **El PDF de ejemplo debe responder `application/pdf` limpio.** Zenvia lo descarga para subirlo
  a Meta antes de mandar la plantilla a revisión. Con `application/pdf; qs=0.001` —lo que
  devuelve `w3.org`, por su negociación de contenido— el envío falla.

Un rechazo con el comentario `"An error occurred while sending the template for approval on
WhatsApp"` **no es un veredicto de Meta**: es Zenvia que no logró siquiera enviarla. Descarta
categoría, tono y contenido, y apunta al nombre, al `examples` o al archivo de ejemplo. Las
causas verificadas de este proyecto fueron el `$` en `examples.total` y el `Content-Type` del PDF.

Alternativa: la consola en `app.zenvia.com/home/templates` valida el nombre y sube el archivo de
ejemplo por su cuenta, así que sortea los tres últimos puntos.

#### En producción

No hay claves de Zenvia en el ConfigMap ni en el Secret: la cuenta es de cada tenant y viaja cifrada en su
conexión (`Integrations:SecretProtection`). Al desplegar el cambio que retiró la cuenta global (plan
2026-10-21):

1. **La conexión no se puede crear antes.** Producción no tiene todavía el módulo Integrations ni el campo
   `templateId`: llegan en el mismo despliegue que retira la cuenta global. Desde que el pod nuevo arranca
   hasta que existe la conexión, «Enviar» de Origen Botánico —hoy el único tenant que envía— responde
   `422 quotation.whatsapp.no_channel` (la cotización queda en borrador; no se pierde nada).
2. **Inmediatamente después**, con el frontend de Integraciones desplegado: el operador habilita Zenvia para
   Origen Botánico y se crea su conexión con el token, el número y el `templateId` de la cuenta que estaba en
   `QUOTATIONS_WHATSAPP_*` (se copian del grupo `Backend-prod` o de la consola de Zenvia, nunca con
   `kubectl get secret`).
3. **Después**, se borran del grupo `Backend-prod` las variables `QUOTATIONS_WHATSAPP_API_TOKEN`,
   `QUOTATIONS_WHATSAPP_FROM_NUMBER` y `QUOTATIONS_WHATSAPP_TEMPLATE_ID`, que ningún manifiesto lee, y se
   comprueba con `kubectl -n prod-qep-backend describe secret qep-backend-secret` (nombres, sin valores) que
   el Secret vivo ya no tenga `Quotations__WhatsApp__ApiToken` ni `…FromNumber`: quitar una clave del
   manifiesto no siempre la borra del Secret aplicado.

> **Decisión pendiente (owner):** aceptar la ventana del paso 1 o cerrarla con una siembra de una sola vez
> que cree la conexión desde `QUOTATIONS_WHATSAPP_*` al arrancar (por el protector de secretos, nunca con
> SQL plano). Hasta que se decida, vale el procedimiento de arriba.

Una conexión de Zenvia creada antes de este cambio (en un ambiente que ya tenía Integrations) no tiene
`templateId` y no cuenta como canal hasta que se edite.

### Generación del PDF (`qcode-pdf`)

El PDF de la cotización lo renderiza `qcode-pdf`, un servicio de Typst genérico y compartido
con otras aplicaciones de QCode: no conoce la cotización, así que el markup viaja entero en el
cuerpo de cada request. `QCodePdfRenderer` es su único cliente.

| Clave | Manifiesto | Token del pipeline |
| --- | --- | --- |
| `Quotations__Pdf__BaseUrl` | [`k8s/prod-configMap.yaml`](k8s/prod-configMap.yaml) | `QUOTATIONS_PDF_BASE_URL` |
| `Quotations__Pdf__ApiKey` | [`k8s/prod-secret.yaml`](k8s/prod-secret.yaml) | `QUOTATIONS_PDF_API_KEY` |
| `Support__Pdf__BaseUrl` | [`k8s/prod-configMap.yaml`](k8s/prod-configMap.yaml) | `QUOTATIONS_PDF_BASE_URL` |
| `Support__Pdf__ApiKey` | [`k8s/prod-secret.yaml`](k8s/prod-secret.yaml) | `QUOTATIONS_PDF_API_KEY` |
| `Purchases__Pdf__BaseUrl` | [`k8s/prod-configMap.yaml`](k8s/prod-configMap.yaml) | `QUOTATIONS_PDF_BASE_URL` |
| `Purchases__Pdf__ApiKey` | [`k8s/prod-secret.yaml`](k8s/prod-secret.yaml) | `QUOTATIONS_PDF_API_KEY` |

**Una sola clave para todo el backend de QEP** (decisión del owner, 2026-11-17). Cada módulo que genera PDF tiene su sección
(`Quotations:Pdf`, `Support:Pdf`, `Purchases:Pdf`) y su validador, para que ninguno dependa de la configuración de otro, pero en
producción las tres leen los mismos dos tokens. Las claves vivían todas en el mismo pod, así que separarlas no aislaba nada, y cada
variable más era otra forma de que el pod no arrancara. Lo que se pierde: `qcode-pdf` identifica al cliente por el hash de la clave, así
que en sus logs QEP es un solo cliente, y rotar la clave toca los tres módulos a la vez. `SUPPORT_PDF_*` y `PURCHASES_PDF_*` ya no se
leen; si existen en `Backend-prod`, se pueden borrar.

Cada validador exige HTTPS siempre y la clave en producción, y un token del pipeline sin resolver (`#{…}#`) cuenta como ausente: **sin
`QUOTATIONS_PDF_BASE_URL` o `QUOTATIONS_PDF_API_KEY` en `Backend-prod`, el pod entero de la API no arranca.**

La respuesta formal de Soporte (fase 4b) usa `Support:Pdf` y `SupportPdfRenderer`. La orden de compra
(`GET …/purchases/orders/{id}/pdf`) usa `Purchases:Pdf` (`TimeoutSeconds` por defecto 30), `PurchaseOrderPdfRenderer` y la plantilla
`purchase-order.typ` incrustada. Si `qcode-pdf` no devuelve el PDF de compras, la respuesta es `502 purchases.order.pdf_render_failed`,
sin el cuerpo del servicio.

**Sin la API key el pod no arranca en producción.** A diferencia de WhatsApp, acá no hay
fallback: `qcode-pdf` respondería 401 y todo envío moriría con `quotation.pdf.render_failed`.
`QuotationsOptionsValidator` la exige con `ValidateOnStart` porque descubrirlo cuando una
asesora aprieta *Enviar* es más caro que un despliegue que no levanta. Fuera de producción la
clave es opcional.

`BaseUrl` debe ser HTTPS absoluta **en todo ambiente**, incluido local: la cotización completa
—precios, cliente, totales— viaja en el cuerpo del POST.

### Contenido configurable del PDF

Spec `docs/superpowers/specs/2026-11-16-pdf-content-design.md`. El PDF de cotizaciones y pedidos tiene tres secciones que cada
tenant configura en `GET`/`PUT /api/v1/tenants/{tenantId}/quotations/settings/pdf-content` (`tenancy.settings.read` / `.update`):

| Sección | Dónde sale |
| --- | --- |
| `paymentNotice` | Debajo del recuadro de condiciones de pago, en rojo; sólo si el documento tiene cuenta para el pago |
| `terms` | Después de los totales |
| `deliveryProcess` | En página aparte, al final |

Cada una se prende, se marca para cotización y/o pedido, y lleva título (texto plano, ≤ 120) y texto enriquecido (HTML saneado
con el saneador compartido de `BuildingBlocks`, ≤ 20 000). Los dos aceptan variables `{{nombre}}` (`QuotationPdfVariables.All`:
`empresa`, `nit_empresa`, `cliente`, `numero`, `fecha`, `vigencia`, `asesor`, `total`); un token que no está en la lista es
`422 quotation.pdf_content.unknown_variable` al guardar, y una variable sin dato sale vacía en el PDF. Un tenant sin filas no
imprime ninguna sección.

- **La plantilla no lee HTML.** `RichTextHtmlReader` convierte el HTML guardado en un árbol de bloques, las variables se resuelven
  sobre sus textos, y `quotation.typ` lo pinta como cadenas: un texto con `#`, `$` o `[` sale literal.
- **El caché del PDF de la cotización se vence por contenido**: `quotation_pdfs.content_fingerprint` guarda el hash de las secciones
  que salieron; cambiarlas regenera el PDF en el siguiente export. Una fila sin fingerprint (anterior a la columna) se regenera.
- **Origen Botánico** (`slug = 'origen-botanico'`) conserva por la migración `AddPdfSections` el aviso de pago y el proceso de
  entrega que antes estaban fijos en la plantilla para todos los tenants; los demás tenants dejaron de imprimirlos.
- **Despliegue: al terminar el rollout se vence el caché una vez.** Un pod viejo no conoce `content_fingerprint`: si regenera el PDF
  de una cotización durante el rollout, lo imprime con la plantilla vieja (el aviso de pago y el proceso de entrega de Origen
  Botánico para todos los tenants) y deja en la fila el fingerprint que había escrito un pod nuevo, así que los pods nuevos siguen
  sirviendo ese PDF después del rollout. Cuando ya no quede ningún pod viejo, corre una vez:

  ```sql
  UPDATE quotations.quotation_pdfs SET content_fingerprint = NULL WHERE generated_at < '<fin del rollout>';
  ```

  La alternativa es desplegar esta versión con `Recreate` (sin pods viejos y nuevos a la vez).

### Integraciones (conexiones del tenant)

Cada tenant conecta sus propias cuentas de plataformas externas en **Ajustes → Integraciones**
(spec 2026-10-08). El catálogo base tiene **Zenvia (WhatsApp)**, visible para los
tenants con `quotations` activo; un proveedor nuevo es otra entrada de `IntegrationProviders`, su
probador y una migración que cambia el `CHECK` de `provider_key`. Varias conexiones por proveedor
(tope de 20), cada una con nombre. Cada consumidor las usa en su spec (Messaging ya lo hace con
`whatsapp-cloud`), por los puertos `IIntegrationConnections` e `IConnectionHealthReporter`.

Además de los módulos, un proveedor puede **exigir habilitación por tenant** (plan 2026-10-21; hoy sólo
`zenvia`), que se da desde la [consola de operador](#consola-de-operador). Cada proveedor del catálogo
trae `platformEnabled`: uno que la plataforma no habilitó para el tenant sólo se lista si el tenant ya
tiene conexiones suyas, y entonces con `platformEnabled: false`, para que la pantalla las agrupe. Cada
conexión trae `providerEnabled`. Crear, editar, probar, pausar o reanudar una conexión de un proveedor
deshabilitado responde `403 integrations.provider.not_enabled`; antes va la visibilidad por módulos, así
que con el módulo apagado la respuesta sigue siendo `tenancy.module_not_enabled`. Leer, listar y eliminar
siguen funcionando, para que el tenant vea y limpie lo que ya tenía. `IIntegrationConnections.ResolveAsync`
y `ListActiveAsync` saltan un proveedor deshabilitado, así que ningún consumidor envía por él.

Desde el spec 2026-10-21 hay además **API externa** (`external-api`, visible con `automations` activo; ver
[Bloque HTTP y proveedor `external-api`](#bloque-http-y-proveedor-external-api)): la usan los bloques HTTP de los chatbots.

Desde el spec 2026-10-09 el catálogo tiene dos proveedores: **Zenvia (WhatsApp)** y **WhatsApp Business
(Meta)** (`whatsapp-cloud`, visible con `messaging` activo, tope de 5). El segundo no se conecta por
formulario sino por **Embedded Signup**: el frontend abre el popup de Meta con `Meta:App:AppId` y
`Meta:App:ConfigId`, y al cerrar manda `POST …/integrations/whatsapp/embedded-signup` con el `code`
(vence a los 30 s). El backend canjea el `code` por un token de sistema, lee el número y, si **no** está
ya `CONNECTED` con `platform_type = CLOUD_API`, lo registra con el PIN de verificación en dos pasos que
mandó el formulario —o uno aleatorio de seis dígitos si no mandó— y lo guarda cifrado en el secreto interno
`registrationPin`. Un número que ya está en la Cloud API (con otra app) no se vuelve a registrar: el
`/register` gasta el límite de 10 en 72 h y cambia el registro mientras el número atiende; el PIN no se
guarda porque nadie lo comprobó, y la auditoría lleva `register:skipped`. Después suscribe la app a la WABA y lee el número verificado;
todo queda en una conexión `Active` con su fila en `integrations.connection_routes`, que es lo que
impide que un número quede conectado en dos tenants y lo que el webhook usa para enrutar. El probador
de `whatsapp-cloud` hace `GET /{phoneNumberId}` y refresca número, nombre y calidad; `190` deja
`token_expired`, `133010` deja `number_unregistered`, y un `account_update` de cuenta deshabilitada
deja `account_disabled`. Eliminar la conexión no llama a Meta (el número sigue registrado en la Cloud
API) y conserva las conversaciones de sólo lectura.

Al guardar, la credencial se prueba contra el proveedor (Zenvia: `GET /v2/templates` con
`X-API-TOKEN`); si la rechaza o no responde, no se guarda nada. Las credenciales son de sólo
escritura: ninguna respuesta, log, auditoría ni evento las lleva, y se guardan cifradas con
AES-256-GCM en `integrations.connection_secrets` (`nonce || ciphertext || tag`, AAD
`integrations.connection:{connectionId}:{fieldKey}`). La llave vive **fuera** de la base:

| Clave | Dónde vive en prod | Contenido |
| --- | --- | --- |
| `Integrations:SecretProtection:ActiveKeyId` | ConfigMap | id de llave (`^[a-z0-9]{1,32}$`); no es secreto |
| `Integrations:SecretProtection:Keys:<id>` | Secret, desde la variable secreta del grupo `Backend-prod` | 32 bytes en base64 |

#### Generar una llave en local

`dotnet user-secrets set` imprime la clave **y su valor**, y `list` imprime todos los valores: el
`set` va con `| Out-Null` y la verificación cuenta.

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Integrations:SecretProtection:Keys:k1" $key --project src/Api | Out-Null
Remove-Variable key, bytes, rng
dotnet user-secrets set "Integrations:SecretProtection:ActiveKeyId" "k1" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "SecretProtection:Keys:k1" | Measure-Object
```

El último comando tiene que dar `Count 1`.

#### Custodia de la llave de producción

- Para generarla sin imprimirla: el mismo bloque, pero `Set-Clipboard $key` reemplaza la primera línea
  `user-secrets set` y corre **antes** de `Remove-Variable`; se pega en la variable secreta y en la
  bóveda, y después se vacía el portapapeles (`Set-Clipboard -Value $null`).
- Fuente de verdad: la variable **secreta** `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` del grupo
  `Backend-prod` (Azure DevOps). Respaldo: una copia en la bóveda del owner. Perder las dos es perder
  todas las credenciales guardadas: cada tenant tendría que volver a pegar las suyas.
- No se edita con `kubectl`: el pipeline vuelve a aplicar `k8s/prod-secret.yaml` en cada deploy.
- Para ver que existe, sin su valor: `kubectl --context contabo-prod -n <ns> describe secret <nombre>`.
  Nunca `get -o yaml`, `-o json` ni `custom-columns` sobre `.data`.

#### Rotación (dos despliegues)

Nunca se retira una llave a la que todavía apunta algún secreto.

1. **Declarar**: variable `INTEGRATIONS_SECRET_PROTECTION_KEY_K2` en el grupo y línea
   `Integrations__SecretProtection__Keys__k2` en `prod-secret.yaml`, con `ActiveKeyId` todavía en `k1`.
   Desplegar.
2. **Activar**: `ActiveKeyId = k2` en el ConfigMap. Desplegar. `ConnectionSecretRekeyWorker` re-cifra al
   arrancar y cada `RekeyIntervalMinutes`, en lotes de 100; un `PUT` con un secreto nuevo también lo
   deja en `k2`.
3. **Contar** (sólo ids y conteos):

   ```sql
   SELECT key_id, count(*) FROM integrations.connection_secrets GROUP BY 1;
   ```

   Si quedan en `k1`, el log del worker dice cuántos se saltó (`n connection secrets could not be
   re-encrypted …`) y su línea final da `rekey finished: n re-encrypted, m skipped`. Con `m = 0` y
   filas en `k1`, es la ventana del rolling update: la corrida siguiente lo resuelve.
4. **Retirar** `k1` (variable y línea del Secret) sólo cuando el conteo da 0 en `k1`. Si la llave se
   filtró: las mismas fases y, además, pedirles a los tenants que roten sus tokens en el proveedor.

#### Orden de despliegue

1. Variable secreta `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` en `Backend-prod` (y su copia en la
   bóveda). Sin ella, el pod nuevo entra en crash-loop por `SecretProtectionOptionsValidator`: es a
   propósito.
2. Backend: código, migración `InitialIntegrations`, la línea del Secret y la del ConfigMap, en el mismo
   merge. La cuenta global de Zenvia (`Quotations__WhatsApp__*`) se retiró después, con el plan 2026-10-21:
   ver [Plantilla de WhatsApp § En producción](#en-producción).
3. Frontend, después. **Rollback del backend:** el frontend muestra la tarjeta y la API responde 404;
   nada se corrompe porque ningún consumidor depende del módulo.

### Mensajería (WhatsApp Cloud)

La bandeja de WhatsApp (spec 2026-10-09): lo que una persona escribe al número del tenant llega por el
webhook de Meta, se lee en `/messaging/conversations` y se responde desde ahí dentro de la ventana de
24 horas. Módulo `messaging` (se prende por tenant), permisos `messaging.conversation.read|manage` en
`admin` y `advisor`, esquema `messaging` (`conversations`, `messages`, `message_media`,
`webhook_deliveries`).

**Webhook.** `GET /api/webhooks/whatsapp` responde el `hub.challenge` si `hub.verify_token` coincide
con `Meta:App:WebhookVerifyToken`. `POST` valida `X-Hub-Signature-256` (HMAC-SHA256 del cuerpo crudo
con `Meta:App:AppSecret`) y guarda el cuerpo deduplicado por su SHA-256 en `webhook_deliveries`; nada
más pasa en el request. Un worker reclama cada entrega con un lease (8 intentos, ~1 h) y procesa:
`messages` entrantes (una conversación por `wa_id` y conexión, reabre si estaba resuelta, sube
`unread_count`), `statuses` (monótonos: `sent < delivered < read`; `failed` sólo sobre `sent`;
`played` cuenta como `read`), `account_update` (cuenta deshabilitada → la conexión queda en
`NeedsAttention`). Con la conexión `Paused` o el módulo apagado los entrantes se descartan y los
`statuses` sí se aplican. Las entregas procesadas se purgan a los 7 días. Los campos de coexistencia
(`history`, `smb_message_echoes`, `smb_app_state_sync`) se describen en
[Historial de coexistencia](#historial-de-coexistencia).

**Entrantes que Meta no entrega.** Un mensaje `type: "unsupported"` se guarda como `Unsupported` con el `type` y los
`errors` de Meta en `details`. Al leer, `unsupportedReason` lo traduce a español como `failureReason`
(`UnsupportedMessageReasons`): `131060` (la persona escribió pero el contenido no llegó; Meta no lo reenvía) y
`131051` (tipo que la Cloud API no entrega); cualquier otro código viaja `null`. Un `131051` de un mensaje importado del historial (`imported`) lleva otro texto: «Este
mensaje no se pudo importar del historial: WhatsApp no entrega este tipo. Puedes verlo en el celular.», porque no hay a quién
pedirle que reenvíe; el `131060` y los entrantes en vivo no cambian. La lista de conversaciones no lo
trae. Handoff al frontend: `docs/superpowers/plans/2026-10-14-mensaje-no-disponible-frontend.md`.

**Lista e hilo: paginación por cursor.** `GET …/conversations` devuelve `{ items, nextCursor, counts }`: el
cliente manda `nextCursor` tal cual en `?cursor=` para la página siguiente (`pageSize` 1–50, default 30) y
`null` significa que no hay más. El cursor es la posición `(last_activity_at, id)` de la última fila, así que una
conversación que sube al tope mientras se recorre la lista no repite ni salta filas, que es lo que pasaba con
`OFFSET`. Un cursor que no emitió el servidor da 422 `validation.failed` en `cursor`. No hay `total` ni `page`:
los números de las pestañas son `counts`. El hilo (`…/messages?before=&limit=`) ya usaba el id del mensaje más
viejo como cursor, con `hasMore`.

**Cotización enviada por Meta.** Cuando el canal de la cotización es una conexión de Meta, el mensaje queda en
el hilo del número del cliente como un `Template` con el cuerpo y el pie ya armados, y el PDF como media
propia (`kind: "Template"`, `mimeType: "application/pdf"`, `fileName: "Cotizacion-{número}.pdf"`, que baja de
`/messaging/media/{messageId}`). `QuotationTemplateOutbound` lo reclama y lo liquida por el mismo camino que los
demás salientes (`IOutboundMessages`, `OutboundSettlement`); puede quedar `Failed`, y `sentBy` es quien envió la
cotización. **No cambia el estado ni la asignación de la conversación** y no toca los no leídos.

**Envío.** `POST …/conversations/{id}/messages` con `{ clientId, text }` es idempotente por
`(conversación, clientId)`: repetir devuelve el mismo mensaje; un `Failed` se reenvía. Cada envío lleva
`biz_opaque_callback_data = "qep:{messageId}"`, que es como se correlaciona un acuse que llegue antes de
que el `wamid` esté guardado.

**Notas de voz.** `POST …/conversations/{id}/messages/audio` en `multipart/form-data` con `clientId`, `file` y
`replyTo` opcional (spec 2026-10-11). Mismas reglas que el texto: ventana, asignado, autoasignación, cita e
idempotencia por `clientId`. El archivo se valida **por contenido** (OGG con `OpusHead`; un WebM o un MP4 es 422 en
`file`), hasta 16 MB; el cuerpo tiene tope propio de 17 MB (413). Dentro del reclamo: R2 (`messaging/{tenantId}/{messageId}`,
bucket privado) → `POST /{phone_number_id}/media` → envío con `audio.voice = true`, que es lo que hace que llegue como
nota de voz. R2 caído es 422 `messaging.media.upload_failed` sin fila. La fila `Failed` guarda su `message_media`
(con `meta_media_id = ''` si Meta nunca dio un id), así que la nota se escucha desde otro dispositivo y el reintento
sobrescribe la misma clave.

**Imágenes y documentos del asesor.** En dos pasos (spec `messaging-media-reply`, 2026-10-13). Primero
`POST …/conversations/{id}/messages/media/upload-session` con `{ fileName, mimeType, sizeBytes }` devuelve 200 con
`uploadId`, `uploadUrl`, `expiresAt` y los `headers` que el navegador manda en el `PUT` directo a R2 (bucket privado,
`messaging/staging/{tenantId}/{conversationId}/{memberId}/{uploadId}{ext}`). Después `POST …/messages/media` con
`{ uploadId, clientId, fileName, caption?, replyTo? }` confirma: revisa tamaño y firma binaria, comprime la imagen, la
sube a Meta y envía; responde 201 cuando Meta contesta. Imagen jpeg/png hasta 25 MB, recomprimida siempre a JPEG q80 con
el lado mayor en 1600 px y sin EXIF (el original se borra); documento pdf, doc, docx, xls, xlsx, ppt, pptx o txt hasta 100 MB. `caption`
opcional hasta 1024. Mismas reglas que el texto: ventana, asignado, autoasignación, cita e idempotencia por `clientId`; un
`Failed` conserva el staging para el reintento. Errores 422: `messaging.media.upload_not_found`, `too_large`,
`unsupported_format` e `invalid_image`.

**Video y audio como archivo** (spec 2026-10-20). Mismas dos rutas. La tabla de formatos es `OutboundMediaPolicy` (extensión →
kind, mime canónico, tope de WhatsApp y de Instagram); la sesión de subida ya aplica la del canal, así que un formato que el
canal no recibe o un archivo que pasa su tope es 422 **antes** del `PUT`. El contenido lo confirma `OutboundMediaSniffer` (64
bytes de cabecera):

| Kind  | Extensión → mime | WhatsApp | Instagram | Firma |
| ----- | ---------------- | -------- | --------- | ----- |
| Video | `.mp4` → `video/mp4` | 16 MB | 25 MB | `ftyp` con marca `iso*`, `mp4*`, `avc1` o `M4V ` |
| Video | `.3gp` → `video/3gpp` | 16 MB | — | `ftyp` con marca `3gp*` |
| Audio | `.mp3` → `audio/mpeg` | 16 MB | 25 MB | `ID3` o trama MPEG (`FF Ex`, capa ≠ 00) |
| Audio | `.m4a` → `audio/mp4` | 16 MB | 25 MB | `ftyp` con marca `M4A `, `iso*` o `mp4*` |
| Audio | `.aac` → `audio/aac` | 16 MB | 25 MB | ADTS (`FF F1`/`FF F9`) |
| Audio | `.amr` → `audio/amr` | 16 MB | — | `#!AMR\n` o `#!AMR-WB\n` |
| Audio | `.ogg` → `audio/ogg` | 16 MB | — | `OggS` con `OpusHead` (Vorbis no) |
| Documento | `.ppt` → `application/vnd.ms-powerpoint` | 100 MB | — | OLE (`D0CF11E0A1B11AE1`, como `.doc`/`.xls`) |
| Documento | `.pptx` → `application/vnd.openxmlformats-officedocument.presentationml.presentation` | 100 MB | — | zip (`PK`, como `.docx`/`.xlsx`) |
| Documento | `.txt` → `text/plain` | 100 MB | — | UTF-8 válido sin bytes de control (sin firma; deja fuera binarios y UTF-16) |
| Sticker | `.webp` → `image/webp` | 100 KB estático, 500 KB animado | — | `RIFF`…`WEBP` con chunk `VP8 `, `VP8L` o `VP8X` |

**La tabla está en borrador hasta las verificaciones V-1..V-3 con Meta real** (las corre el owner; ver el spec): lo que Meta
no acepte se quita de `OutboundMediaPolicy` y de esta tabla. No hay transcodificación ni `ffmpeg`: `.mov`, `.webm`, `.avi`,
`.wav`, `.flac`, `.wma` y el `.ogg` Vorbis son `unsupported_format`. **Sticker** (2026-11-22): Meta sólo admite WebP como sticker, no como
imagen, así que `.webp` sale con `type: sticker`, sin `caption` (422 `validation.failed` en `caption`), sin recomprimir y sólo por WhatsApp.
El tope depende del contenido: 500 KB si el chunk `VP8X` trae la bandera de animación y 100 KB si no, y por eso el estático se mide al
leer el archivo (`media_too_large` al confirmar, no al pedir la sesión). El chatbot no lo manda: su flujo no tiene ese tipo. El video sale con `type: video` y su `caption`; el audio,
con `type: audio` **sin** `voice` (la nota de voz sigue con `voice: true`) y **sin texto**: un `caption` con un audio es 422
`validation.failed` en `caption`. El multipart a Meta lleva `video{ext}` o `audio{ext}`; el nombre real queda en
`message_media.file_name` y viaja en `media.fileName`, que en un audio adjunto **no es nulo** y en una nota de voz sí: así
distingue el frontend un archivo de audio de una nota. El backend no mira el códec: si Meta rechaza un MP4 HEVC, el mensaje
queda `Failed` con el código de Meta, como cualquier envío.

**Medios.** Los entrantes se copian a R2 (bucket privado, `messaging/{tenantId}/{messageId}`) con un
worker que verifica el SHA-256 de Meta; `GET …/messaging/media/{messageId}` los sirve con la sesión,
con `nosniff`, CSP `sandbox` y `attachment` para todo lo que no sea imagen, audio o video. Mientras no
está copiado responde 404 sin código. Responde `Accept-Ranges: bytes` y un `Range` de un solo tramo (`bytes=a-b`, `a-`,
`-n`) da **206** con `Content-Range: bytes a-b/total` y los mismos `nosniff` y CSP (spec 2026-10-20 D10: Safari no adelanta un
`<video>` sin él); el rango viaja a R2 en el `GET` (`IObjectStorage.OpenReadRangeAsync`), con un stat para el tamaño. Un rango
fuera del objeto es **416** con `Content-Range: bytes */total`; varios tramos o un valor que no se entiende se ignoran y se
sirve el archivo entero (200). El campo `sha256` de Meta se acepta en hex (64) o base64 (44); si
llega en otro formato el medio se guarda **sin verificar** y queda un warning `stored without
verification` por fila en el log.

**Respuestas rápidas.** Textos del tenant con un atajo (`horarios`, sin `/`) que el asesor inserta en el compositor
(spec 2026-10-12). Leerlas pide `messaging.conversation.read`; crearlas, editarlas y borrarlas,
`messaging.quick_reply.manage` (de fábrica sólo en `admin`). El atajo se guarda recortado y en minúsculas y cumple
`^[a-z0-9-]{2,32}$`; es único por tenant (`IX_quick_replies_tenant_shortcut` → 422 `messaging.quick_reply.shortcut_taken`).
El cuerpo va de 1 a 4096 caracteres y sólo admite `{nombre_contacto}`, `{asesor}`, `{empresa}` y `{saludo}`, más `{nombre}`,
el nombre viejo de `{nombre_contacto}`, que sigue entrando porque lo manda un SPA anterior. El `GET` devuelve la lista
entera por atajo y `variables.asesor` / `variables.empresa` ya resueltos para quien llama; `{nombre_contacto}` y `{saludo}`
los resuelve el frontend con la conversación y la hora. `PUT` y `DELETE` exigen la versión (412 si cambió).

**Motivos de resolución.** `POST …/resolve` acepta un cuerpo opcional `{ reasonId }` o `{ reasonName }`; sin cuerpo resuelve sin
motivo, como antes. `reasonName` busca un motivo vigente sin distinguir mayúsculas (recortado) y, si no existe, lo crea en la misma
transacción: el nombre va de 1 a 60 caracteres y cada tenant tiene hasta 100 vigentes. Si dos asesores crean a la vez el mismo nombre
nuevo, el segundo recibe 409 `messaging.resolution_reason.name_taken`: el cliente reintenta una vez el mismo request y el reintento
reutiliza el motivo. Borrar archiva (`deleted_at`): el motivo sale del catálogo, pero el historial y el resumen lo siguen nombrando;
renombrar cambia el nombre también en el historial pasado. Administrar el catálogo (`PATCH` / `DELETE`) pide
`messaging.resolution_reason.manage` (de fábrica en `admin` y `advisor`); después de desplegar hay que reiniciar la API y recargar la
sesión, como con todo permiso nuevo. Spec:
[`2026-10-15-mensajeria-motivo-de-resolucion-design.md`](docs/superpowers/specs/2026-10-15-mensajeria-motivo-de-resolucion-design.md).

**Búsqueda.** `GET …/messaging/messages/search?q=` busca por palabra en texto y leyendas con la
configuración `messaging.es_unaccent` (español sin acentos ni flexiones, prefijo con 3+ letras) y el
índice GIN `IX_messages_tenant_search`; el servidor arma la `tsquery` (nunca la persona) y corta a los
2 s con 422 en `q`.

**Redacción del query en la traza.** El canje del `code` de Embedded Signup lleva `client_secret` y
`code` en el query de la URL de Graph. La instrumentación de `HttpClient` de OpenTelemetry .NET
(`AddHttpClientInstrumentation`) redacta por defecto los **valores** del query en `url.full` (desde la 1.7;
aparecen como `Redacted`). Nunca se pone `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION`
en `true`.

#### Identidad, clientes y asignación (spec 2026-10-10)

- **Identidad.** La conversación se identifica por `(connection_id, user_id)` (BSUID); el teléfono (`wa_id`)
  puede faltar. Las filas viejas sin BSUID se adoptan con el primer entrante que traiga BSUID y teléfono. Se
  envía con `recipient` (BSUID); sólo una fila vieja sin BSUID sale con `to`.
- **Clientes.** Todo el que escribe queda atado a un cliente de QEP: si no existe, se crea con la ficha
  **incompleta** (sin CUC). El `PUT /customers/{id}` con todos los datos la completa y le emite el CUC.
  Quotations rechaza a un incompleto con `quotation.quotation.client_incomplete`; el reporte de clientes no
  lo cuenta.
- **Asignación.** `POST …/conversations/{id}/take|transfer|release` con `If-Match`. Sólo el asignado
  responde (`messaging.conversation.assigned_to_other`); responder una conversación sin asignar la toma;
  resolver no libera. Una conversación nueva de un cliente con dueño hereda el asignado si todavía puede
  responder. `GET /messaging/assignees` lista a quién transferir. `assignedTo.isMe` lo calcula el servidor.
- **Indicador de escritura.** `POST …/conversations/{id}/typing` (`.manage`, sin cuerpo, 204) le muestra «escribiendo» al contacto en WhatsApp e
  Instagram, best effort. Es no-op si el asesor no podría contestar ya mismo, y no cambia la conversación ni la toma. Sólo sale hacia el contacto: Meta no avisa por webhook que el contacto esté escribiendo.
- **Número compartido.** Dos clientes pueden tener el mismo WhatsApp (R3). Toda respuesta de **una** sola conversación (el detalle y las que devuelven su resumen) lleva
  `sharedPhoneCustomers: [{ id, name, isComplete, tags: [] }]` (hasta 10) con los otros clientes del mismo número; en el listado
  va siempre `[]`. Se lee por el puerto `IMessagingCustomerDirectory`, no por el esquema de Customers.
- **Cliente principal.** `PUT …/conversations/{id}/customer` con `If-Match` y `{ customerId }` cambia el cliente de la
  conversación (`.manage`): 428 sin `If-Match`, 412 con versión vieja, 422 `messaging.conversation.customer_not_found` o
  `validation.failed` (`errors.customerId`). Escribe el evento `CustomerChanged` en el hilo con `targetCustomer` y
  `previousCustomer` (`{ id, name }` o `null`); `target` y `previous` quedan en `null` porque son miembros, no clientes.
- **Iniciar una conversación** (`ConversationOpener`) busca, en este orden: (1) por BSUID del cliente, (2) la conversación con
  BSUID de la conexión que tiene ese `wa_id` (la que recibe las respuestas), (3) por `customer_id`, la más reciente, (4) la fila
  vieja sin BSUID con ese teléfono. El `customer_id` de la encontrada no cambia: cambiarlo es `PUT …/customer`.
- **Campañas.** El opt-out de marketing se lee **por número**: vale `true` si la ficha, o cualquier otro cliente del tenant con el mismo
  teléfono, tiene la bandera (una ficha sin teléfono sólo mira la suya). El worker no reclama destinatarios hasta que la campaña tiene `audience_frozen_at`. Al
  terminar de congelar, `MarkFrozenAsync` deduplica en una transacción con la campaña bloqueada (`FOR UPDATE`; `AddRecipientsAsync`
  inserta con `FOR SHARE` y sólo si la audiencia no está congelada): un solo envío por `wa_id`, y el resto queda `Skipped` con
  `failureCode: "duplicate_contact"`. Gana el cliente de la conversación de ese número en la conexión de la campaña y, si no,
  el más viejo (`customer_created_at`, columna que agregó la migración `AddCampaignRecipientCustomerCreatedAt`).
- **Eventos del sistema** (`direction: System`, `kind: Event`) en el hilo; no suben `unreadCount` ni cambian
  la foto de la lista.
- **Respuestas citadas.** `replyTo` al enviar (el `id` de **nuestro** mensaje) y `Message.replyTo` al leer.

#### Mensajes programados (spec 2026-11-09)

Un asesor deja **un** mensaje (texto, imagen, documento, video, audio o nota de voz) para que salga solo a la hora
elegida, mientras la ventana de 24 h siga abierta. Hay a lo sumo uno activo por conversación (`Scheduled`, `Sending` o
`Failed` ocupan el cupo). Rutas bajo `/api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}`; leer pide
`messaging.conversation.read` y el resto `messaging.conversation.manage`, por permiso (no por ser el asignado ni el autor):

| Ruta | Qué hace |
| --- | --- |
| `GET /scheduled-message` | `{ scheduledMessage }`: el activo o `null`. Trae `canRetry`, calculado por el backend |
| `POST /scheduled-messages` | Crea (JSON: `clientId`, `scheduledAt`, y `text` **o** `uploadId` + `fileName` con `caption`). `201`; un `clientId` repetido devuelve `200` con la fila ya guardada |
| `POST /scheduled-messages/audio` | Nota de voz, multipart (`clientId`, `scheduledAt`, `file`) |
| `PUT /scheduled-messages/{id}` | Edita `scheduledAt`, `text`, `caption`; lo omitido o `null` conserva lo guardado, `caption: ""` borra el pie. Sólo en `Scheduled` |
| `POST …/{id}/cancel` | Cancela (`204`). Cuerpo `{ expectedVersion }`. Sólo en `Scheduled` |
| `POST …/{id}/send-now` | «Enviar ya»: adelanta la hora y responde `202`; la salida la hace el worker en su próximo ciclo. Cuerpo `{ expectedVersion }`. Sólo en `Scheduled` |
| `POST …/{id}/retry` | Reintenta un `Failed` (`202`); sólo si `canRetry`. Cuerpo `{ expectedVersion }`. `422 messaging.scheduled.outside_window` si la ventana ya no alcanza y `422 messaging.contact.blocked` si el contacto está bloqueado |
| `POST …/{id}/discard` | Descarta un `Failed` (`204`). Cuerpo `{ expectedVersion }` |
| `GET …/{id}/media` | El adjunto, con `Range` |

La versión viaja en el cuerpo (`expectedVersion`), no en `If-Match`, como las campañas: una versión vieja responde `412`.

**Estados.** `Scheduled` → `Sending` (el worker lo reclama con un lease de 10 min; al reclamar el mensaje vuelve a mirar que el
lease siga siendo suyo y le queden al menos 30 s, y si no, no envía y deja la fila a la recuperación) → `Sent` o `Failed`; `Cancelled` sale de
`Scheduled`. `Sent` y `Cancelled` son finales. `FailureCode` guarda el motivo, con los códigos de siempre de Mensajería
(`messaging.window_closed`, contacto bloqueado, conexión no disponible, `messaging.message.rejected`…) más los propios de
abajo.

**Errores propios.** `422 messaging.scheduled.too_soon` (la hora va a menos de 1 min) y `outside_window` (pasa de
`último entrante + 24 h − 2 min`, o no hay entrante); `409 already_exists`, `not_editable` (editar, cancelar o «Enviar ya»
fuera de `Scheduled`) y `not_retriable` (reintentar o descartar fuera de `Failed`); `404 not_found`. En la fila:
`messaging.scheduled.media_missing`, `messaging.scheduled.unknown_outcome` y `messaging.message.not_retriable` (D8b).

**Decisiones que conviene no romper.**

- **D4, no toca la conversación.** Enviar un programado no cambia el estado ni la asignación, ni apaga el asistente. El
  mensaje queda con el autor del contenido y entra con `seen: 0` (sin el acuse de lectura que sí manda el envío directo).
- **D5, `{saludo}`.** Se resuelve **al enviar**, con la hora del tenant en ese momento, en el texto o en el pie. Los topes
  miden con el saludo más largo (13 caracteres).
- **D6, adjuntos.** Viven en `messaging/scheduled/{tenantId}/{id}{ext}` hasta que sale el mensaje, que los promueve a su
  clave final. Una fila terminal borra el suyo; si no puede, el worker lo barre (cada `PurgeIntervalHours`). El bucket **no
  debe tener regla de expiración** sobre `messaging/scheduled/`, o una de más de 2 días: un programado puede esperar casi 24 h
  y borrarle el archivo antes lo deja en `media_missing`.
- **D8b, `retry_disabled`.** Descartar un programado `Failed` desde su tarjeta quema el `clientId` de su mensaje fallido: el
  hilo ya no puede reenviarlo (`409 messaging.message.not_retriable`) y, si el ejecutor lo encuentra así, cierra el
  programado con ese mismo código sin reenviar.
- **D9, nunca reenvía solo.** Un `Sending` con el lease vencido (el pod murió) se **cierra**, no se reprograma: `Sent` si el
  mensaje ya salió, `message.rejected` sólo si Meta lo rechazó (la fila `Failed` guarda su código), y `unknown_outcome` si no
  hay rastro o la falla no la confirmó Meta (timeout, 5xx, red: `failure_code = -1`). Reenviar podría mandarlo dos veces.
  El ejecutor sigue la misma regla: sin respuesta de Meta el programado es `unknown_outcome`, nunca `message.rejected`
  (decisión del owner, 2026-11-10). Lo reintenta una persona con `retry`.

Configuración: `Messaging:Workers:ScheduledPollSeconds` (5) es el tick del worker, y la purga usa `PurgeIntervalHours`.

**Despliegue.** Backend primero: la migración `AddScheduledMessages` crea `messaging.scheduled_messages` y agrega
`retry_disabled` a `messaging.messages`; el frontend sale después.

**Bucket privado (`Storage:R2:Bucket`), antes de desplegar.** No hay nada que crear: lo que se revisa es que ninguna regla
existente borre los adjuntos programados. Los limpia el código (al terminar cada fila y con el barrido de
`PurgeIntervalHours`, 24 h por defecto); el bucket no tiene que hacerlo.

1. **Ninguna regla de lifecycle cubre `messaging/scheduled/`.** Ni una propia ni una más amplia: una regla sobre
   `messaging/` o sin prefijo también la alcanza. Si por política se quiere una red de seguridad, que expire a **más de 2
   días** (un programado espera hasta 24 h; antes de eso el envío falla con `messaging.scheduled.media_missing`).
2. **La regla de `messaging/staging/` sigue con su prefijo exacto** (`expire-messaging-staging`, 1 día; ver «Pendientes del
   owner antes de producción: imágenes y documentos del asesor»). El adjunto se sube a staging y se copia a
   `messaging/scheduled/` al programar, así que esa regla no lo toca después.
3. **El CORS del bucket privado** es el mismo de los adjuntos del asesor ([`ops/r2-cors.aws.json`](ops/r2-cors.aws.json)): la
   subida usa la misma sesión de staging. No cambia nada.

Para verificar, en PowerShell (wrangler pide su propio login):

```powershell
npx wrangler r2 bucket lifecycle list <bucket-privado>
```

En la salida, ningún prefijo puede ser `messaging/scheduled/`, `messaging/` ni vacío, salvo con más de 2 días.

#### Secretos en local

```powershell
dotnet user-secrets set "Meta:App:AppSecret" "<el app secret>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:WebhookVerifyToken" "<32+ caracteres aleatorios>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:AppId" "<app id>" --project src/Api | Out-Null
dotnet user-secrets set "Meta:App:ConfigId" "<config id>" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "Meta:App" | Measure-Object
```

El último comando tiene que dar `Count 4`. Nunca `list` sin el filtro y el conteo.

#### Probar el webhook en local

El cuerpo va a archivo (PowerShell rompe las comillas). La firma se calcula sobre los bytes exactos del
archivo y `curl.exe` los manda tal cual con `--data-binary` (`-d "@archivo"` quita los saltos de línea y la
firma dejaría de coincidir, con un 401):

```powershell
$bytes = [IO.File]::ReadAllBytes("$PWD\webhook.json")
$secret = Read-Host -AsSecureString "AppSecret"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secret))
$hmac = New-Object Security.Cryptography.HMACSHA256 (,[Text.Encoding]::UTF8.GetBytes($plain))
$signature = "sha256=" + (($hmac.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
Remove-Variable plain, secret, hmac
curl.exe -s -o NUL -w "%{http_code}" -X POST "http://localhost:5000/api/webhooks/whatsapp" -H "Content-Type: application/json" -H "X-Hub-Signature-256: $signature" --data-binary "@webhook.json"
```

El `webhook.json` de ejemplo lleva `"from_user_id": "CO.573001234567"` en el mensaje y
`"user_id": "CO.573001234567"` en el contacto. Sin `from_user_id` la ingesta salta el mensaje (spec
2026-10-10 §8.1); el teléfono es opcional.

Esperado: `200`. Sin el header: `401`. La entrega se procesa en los 3 s siguientes
(`Messaging:Workers:DeliveryPollSeconds`).

#### Historial de coexistencia

Spec `docs/superpowers/specs/2026-10-29-historial-coexistencia-whatsapp-design.md`. Cuando un tenant
conecta su número **por coexistencia** (el número sigue activo en la app del teléfono), QEP trae el
historial de hasta 180 días y los contactos de la agenda, y desde ahí también ve lo que se responde
desde la app.

- **Qué hace.** Después del commit de la conexión, QEP pide a Graph (`smb_app_data`) dos sincronizaciones,
  `smb_app_state_sync` (contactos) e `history`, y guarda cada `request_id` en la fila de sincronización de la
  conexión. Un worker hace el pedido, así que no depende de que el pod siga vivo. Las partes del historial
  llegan por el webhook y llenan los hilos **sin generar trabajo**: no disparan el bot, no crean negocios ni
  asignan asesor. Las conversaciones importadas entran **cerradas**, salvo las que cumplen las dos
  condiciones: el último mensaje es del contacto y llegó en las 24 h anteriores a la conexión (esas entran
  abiertas, con `history_managed = true`). Los contactos crean clientes incompletos. Los ecos
  (`smb_message_echoes`) guardan lo que el negocio responde desde la app.
- **De quién es cada mensaje del historial.** Meta no manda `to` en el historial. El hilo es el contacto: si el
  `from`, en dígitos, es el id del hilo, el mensaje es del contacto; si es otro, es del negocio. Sin `from` queda
  como entrante. La regla de abiertas y los no leídos dependen de esto.
- **Reparación de lo importado antes del 2026-10-30.** El parser anterior esperaba `to` y guardó como entrante lo
  que escribió el negocio. [`ops/2026-10-30-repair-history-direction.sql`](ops/2026-10-30-repair-history-direction.sql)
  lo corrige a mano en DBeaver, leyendo los payloads de `messaging.webhook_deliveries`, que se purgan a los 7 días.
  Corrige la dirección y el estado de los mensajes y la foto de la conversación. No toca el estado ni los no leídos
  de la conversación (decisión del owner). Por defecto termina en `ROLLBACK`, y correrlo dos veces no cambia nada.
- **Un eco a un número que QEP no conoce crea la conversación cerrada** (P-5, decisión del owner del
  2026-10-30). Que el negocio escriba desde la app no le deja nada pendiente a un asesor, así que no aparece en
  la bandeja de abiertas ni en el triage del embudo. Cuando el contacto escribe en vivo, la conversación se
  reabre por el camino de siempre, con su evento `Reopened`. Un eco a una conversación que ya existe no le
  cambia el estado. En el reporte, esa conversación (marca `started_by_echo`) no cuenta como sesión al crearse:
  su sesión la arranca el `Reopened`, así que el eco más el entrante dan una sola, y un periodo con sólo el eco
  da cero. Una conversación que abre un asesor desde QEP sí cuenta desde su creación, aunque el contacto no
  conteste.
- **El historial importado no cuenta en el reporte de mensajería** (decisión del owner del 2026-10-30). Lo
  importado lleva la marca `imported`, en `messaging.conversations` y en `messaging.messages`, y esa marca
  no se apaga nunca, a diferencia de `history_managed`. Su `created_at` es la hora de la importación, así que
  `MessagingReportSource` lo deja fuera de las sesiones, de los entrantes de arranque, de las respuestas y de
  las resoluciones que cuenta el bloque `chatbots`: el día de la conexión no se infla con contactos nuevos ni con sesiones sin responder. Lo que pasa en vivo
  después cuenta como siempre: una conversación importada arranca sesión con su primer entrante en vivo, o
  con su `Reopened` si se reabrió. Para saber si un contacto es nuevo o vuelve, un entrante importado sí
  cuenta, con su `occurred_at`: un contacto que ya escribía por la app no es nuevo por escribir en vivo. Los
  ecos no llevan la marca, porque son tráfico en vivo. La migración `AddImportedMarker` agrega las columnas
  `imported` y `started_by_echo`, y el índice parcial `IX_conversations_tenant_imported`.
- **`fromHistory` en el resumen de la conversación.** `ConversationSummary` lleva `fromHistory`: `true` si la
  conversación nació de la importación y el contacto todavía no ha escrito en vivo (`imported` y sin
  `last_inbound_at`). Con eso el compositor muestra «Esta conversación viene del historial; para responder usa
  una plantilla.» sin otra llamada. Viaja en todo lo que devuelve un `ConversationSummary`: el listado, el
  detalle y las respuestas de las acciones sobre la conversación. Los resultados de la búsqueda de mensajes no
  lo llevan, porque son `MessageHitDto`. No sale de `history_managed`:
  esa bandera se apaga cuando el asesor toma, transfiere o marca leída la conversación, y nada de eso abre la
  ventana de 24 h, así que la respuesta sigue exigiendo plantilla.
- **Regla de Meta: 24 h y una sola vez por tipo.** Meta acepta el pedido de cada tipo una sola vez y sólo
  dentro de las 24 h siguientes a la conexión. Pasado el plazo no hay segunda oportunidad: el estado queda
  `Expired` y la pantalla dice que hay que desconectar y volver a conectar. Un pedido rechazado por Graph
  queda `Failed`; mientras no venza el plazo se puede reintentar.
- **Campos del webhook a suscribir en el panel de la app de Meta:** `history`, `smb_message_echoes` y
  `smb_app_state_sync`. Sin la suscripción el historial no llega.
- **Endpoints.** `GET /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/history-sync`
  (permiso `integrations.connection.read`) devuelve el estado, el progreso, `canRetry` y `mustReconnect`.
  `POST …/history-sync/retry` (permiso `integrations.connection.manage`) reintenta lo que quedó `Pending` o
  `Failed`; sin cuerpo.
- **Orden de la puesta en marcha (addendum 2026-10-30 del spec).** (1) Este backend desplegado, con la migración
  `AddHistorySync` (Messaging: `history_managed` en las conversaciones y las tablas de la sincronización) y
  `AddImportedMarker` (la marca `imported` de conversaciones y mensajes), y con la variable
  `META_WEBHOOK_OVERRIDE_CALLBACK_URL` en `Backend-prod`; (2) los campos de arriba suscritos en el panel de la app;
  (3) el número de QCode por coexistencia. **No hace falta mover la URL de la app:** ver el override, abajo.
  **Despliégalo antes de conectar cualquier número por coexistencia:** una conexión hecha antes pide el
  historial, QEP lo descarta como `UnknownChange` y no hay segunda oportunidad.
- **Límite de tamaño (V-3).** Revisa `Messaging:Webhook:MaxBodyBytes` contra el tamaño de una parte real de
  historial; si queda corto, la parte se rechaza y se pierde.

**Override temporal del webhook por WABA (`Meta:App:WebhookOverrideCallbackUrl`).** QEP y lulo chat comparten
la app de Meta, y la URL del webhook de la app apunta a lulo. Para recibir en QEP una sola WABA sin mover
los demás números, el signup le pide a Meta otra URL para esa WABA. Es **temporal**: se va cuando el owner
pase la URL de la app a QEP.

- **Qué hace.** Con la clave puesta, el `POST /{wabaId}/subscribed_apps` del signup lleva en su cuerpo JSON
  `override_callback_uri` y `verify_token` (el `Meta:App:WebhookVerifyToken` de siempre). Meta hace el desafío
  `GET` contra esa URL antes de contestar. Vale para los dos caminos del signup, coexistencia y número nuevo, y
  para **toda la WABA**, no para un número. Sin la clave, o vacía, la llamada sale sin cuerpo, como siempre.
- **Cómo se configura.** En producción es la variable `META_WEBHOOK_OVERRIDE_CALLBACK_URL` del grupo `Backend-prod`,
  con el valor `https://qep-api.qcode.co/api/webhooks/whatsapp`. Si la variable no existe, el token queda literal y
  cuenta como ausente: el pod arranca sin override. El validador exige una URL https absoluta de hasta 200
  caracteres (el límite de Meta) y el token de verificación.
- **Si Meta la rechaza** (por ejemplo, falla el desafío), el signup responde `422
  integrations.whatsapp.registration_failed`, no guarda la conexión ni publica el evento del historial, y **no se
  reintenta sin override**: eso mandaría a lulo un historial que se pide una sola vez. Arregla la causa y repite el
  Embedded Signup.
- **Qué sigue llegando a lulo.** Según la documentación de Meta (revisada el 2026-10-30), el override aplica a
  `messages`, `history`, `smb_message_echoes` y `smb_app_state_sync`, entre otros, pero los webhooks de plantillas y de
  cuenta (`message_template_status_update`, `account_update`…) van siempre a la URL de la app. Mientras dure, los
  estados de las plantillas de esa WABA sólo se actualizan con el refresco del catálogo.
- **Cómo verificar.** `GET /{wabaId}/subscribed_apps` devuelve `override_callback_uri` en el elemento de la app. Las
  dos llamadas de abajo leen los secretos de un prompt y los pasan por archivo: el token nunca queda en la línea de
  comandos ni en el historial.
- **Cómo quitarlo.** Primero mueve la URL de la app a QEP. Después repite la suscripción **sin cuerpo** en cada WABA con
  override (Meta lo documenta como la forma de borrarlo) y confirma con el `GET` que `override_callback_uri` ya no
  aparece. Por último borra la clave de `k8s/prod-configMap.yaml` y la variable. Quitar la clave **no** limpia la WABA.
- **Una conexión creada por `lulo-migrator` no se suscribe:** no pasa por el signup, así que la clave no la toca. Un
  número migrado así necesita la llamada manual.

```powershell
$waba = "<WABA_ID>"
$callback = "https://qep-api.qcode.co/api/webhooks/whatsapp"
$graph = "https://graph.facebook.com/v24.0/$waba/subscribed_apps"
$access = [pscredential]::new("x", (Read-Host "Token de acceso de la WABA" -AsSecureString)).GetNetworkCredential().Password
$verify = [pscredential]::new("x", (Read-Host "Meta:App:WebhookVerifyToken" -AsSecureString)).GetNetworkCredential().Password
$dir = Join-Path $env:TEMP ("waba-" + [guid]::NewGuid())
New-Item -ItemType Directory $dir | Out-Null
try {
    # Sin BOM: Out-File y Set-Content de PowerShell 5.1 lo escriben, y Graph no lee ese JSON.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText("$dir\headers.txt", "Authorization: Bearer $access`r`nContent-Type: application/json`r`n", $utf8)
    [IO.File]::WriteAllText("$dir\body.json", (@{ override_callback_uri = $callback; verify_token = $verify } | ConvertTo-Json -Compress), $utf8)

    # Aplica el override. Para quitarlo, la misma línea sin -d.
    curl.exe -sS -X POST $graph -H "@$dir\headers.txt" -d "@$dir\body.json"

    # Verifica.
    curl.exe -sS $graph -H "@$dir\headers.txt"
}
finally {
    Remove-Item -Recurse -Force $dir
}
```

**Pendientes y límites conocidos.**

- **V-1 a V-6 siguen sin verificar contra Meta real:** forma de los tres webhooks (V-1, en especial si
  `history` trae el nombre del contacto), webhook que trae el archivo de un `media_placeholder` (V-2), tamaño
  de una parte frente al límite (V-3), que la ventana de 24 h no la abran los mensajes del historial (V-4),
  `TriageRules.IsPending` con `last_inbound_at` vacío (V-5) y que Graph no gaste la única oportunidad al
  rechazar un pedido (V-6). Las pruebas usan fixtures que toleran esas variaciones.
- **Qué apaga `history_managed`.** Mientras está prendida, cada parte del historial que llega recalcula si la
  conversación está abierta y cuántos no leídos tiene. La apaga todo lo que hace la ingesta en vivo o el asesor:
  un entrante en vivo, `Resolve` y `Reopen`, una respuesta enviada desde QEP, asignarla (a mano, por traspaso del
  bot o por la autoasignación al responder), tomarla o transferirla, un eco de lo que el negocio responde desde la
  app, y marcarla leída o no leída (también «no leído desde este mensaje»). **Un envío de campaña no la apaga.**
  Si agregas una acción del asesor sobre una conversación, decide qué hace con la bandera: si no la apaga, la
  siguiente parte del historial le pisa el estado o el contador.
- **Ventana residual de doble envío con varias réplicas.** El pedido a Graph se reclama con un lease; con más
  de una réplica queda una ventana pequeña en la que dos pods pueden enviar el mismo pedido.
- **Un archivo que nunca llega queda `Pending`.** Si Meta no entrega el medio de un `media_placeholder`, el
  mensaje sigue pendiente, sin reintentos propios.

#### Despliegue

1. Variables secretas `META_APP_SECRET` y `META_WEBHOOK_VERIFY_TOKEN` en `Backend-prod`;
   `META_APP_ID` y `META_CONFIG_ID` como variables normales. Sin las cinco claves,
   `MetaAppOptionsValidator` deja el pod en crash-loop: es a propósito.
2. Backend primero (migraciones `AddWhatsAppCloudProvider`, `AddConnectionRoutes`,
   `AddMessagingModuleKey`, `AddCustomerPhoneE164`, `InitialMessaging`, `AddCustomerCompleteness` y
   `AddBsuidAssignmentAndEvents`), después se verifica el webhook en Meta (hace el `GET` al guardarlo),
   después el frontend **de este slice**. `AddBsuidAssignmentAndEvents` va antes o junto con el primer
   despliegue de Messaging a producción: sus `CHECK` recorren `messaging.messages`, y eso sólo es barato
   mientras la tabla está vacía.
3. **Sólo con ese frontend arriba** se prende una conexión de WhatsApp en producción. El SPA viejo espera
   `classification` y `cuc` no nulos en el cliente; los clientes incompletos sólo aparecen cuando la
   ingesta corre, así que prender la conexión antes los deja entrar sin una pantalla que sepa dibujarlos.
   `AddCustomerCompleteness` deja `completeness` con `DEFAULT 'Complete'` para que el pod viejo, que sigue
   atendiendo mientras el nuevo arranca, pueda crear clientes; se quita en una migración posterior.
4. Prender `messaging` al tenant (consola de operador o el SQL de respaldo de «Módulos por tenant»).
   Lo que hace el owner del lado de Meta está en
   `docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`.

#### Pendientes del owner antes de producción: imágenes y documentos del asesor

El código está en `develop` desde `a5a79c0` (2026-10-13). Antes de llevarlo a `main` y desplegarlo:

1. **Regla de lifecycle para `messaging/staging/` en el bucket privado.** Un archivo que el navegador sube y nunca se
   confirma —o un envío que quedó `Failed` y nadie reintentó— se queda ahí. No hay worker que lo limpie: lo limpia la
   regla. El prefijo es obligatorio, igual que en `exports/`: sin él, la regla borra todo el bucket.

   ```powershell
   npx wrangler r2 bucket lifecycle add <bucket-privado> expire-messaging-staging "messaging/staging/" --expire-days 1
   npx wrangler r2 bucket lifecycle list <bucket-privado>   # para verificarla
   ```

   Ojo con el prefijo: **no** uses `staging/` a secas. Esa clave es de Storage y los comprobantes de pago viven ahí
   retenidos (ver «Comprobantes de pago v2»).
2. **Verificar el CORS del bucket privado.** El `PUT` va al mismo bucket que la biblioteca de archivos, así que la regla
   de [`ops/r2-cors.aws.json`](ops/r2-cors.aws.json) ya sirve si está aplicada: `PUT`, `Content-Type` **e**
   `If-None-Match`, con el origen exacto del SPA de producción. Compruébalo con una subida real desde la pantalla, no
   leyendo el archivo: un archivo versionado no configura nada.
3. **Desplegar el backend antes que el frontend.** No hay migración. El SPA de producción no usa estas rutas hasta que
   salga el slice del frontend, así que el backend puede ir solo.
4. **Probar en local con Meta real antes de `main`.** Una imagen y un documento grande (cerca de 100 MB), con y sin
   cita. El envío a Meta con `Content-Length` sólo está probado contra un doble.
5. **Saber qué esperar de la confirmación.** Cloudflare corta a los 100 s con un **524** aunque el ingress espere 240
   (`k8s/prod-ingress.yaml`). Con un documento grande la confirmación puede pasar ese tiempo: el servidor sigue y el
   mensaje sale. El frontend lo trata como «sigue enviando» y reintenta con el mismo `clientId`, que no duplica.
6. **Aceptar que los adjuntos no pasan por ClamAV.** A diferencia de la biblioteca de archivos, Messaging no escanea.
   Se decidió así al aceptar `.doc` y `.xls`, que pueden traer macros.
7. **Correr V-1..V-3 del spec de video y audio (2026-10-20) antes de `main`.** La tabla de «Video y audio como archivo»
   sale de la documentación pública de Meta y nadie la probó contra Meta real: WhatsApp con un `.mp4` H.264/AAC de ~15 MB,
   un `.3gp` y audio sin `voice` en `.mp3`, `.m4a`, `.aac`, `.amr` y `.ogg` Opus (V-1); Instagram con un `.mp4` y los
   tres audios por URL firmada (V-2); y qué hace WhatsApp con un `.mp4` HEVC del iPhone (V-3, define el copy del fallo).
   Mismo método que el paso 4. Lo que no pase se quita de `OutboundMediaPolicy` (y de su prueba) y de la tabla.

#### Tiempo real (SignalR)

Spec `qep-frontend/docs/superpowers/specs/2026-10-14-mensajeria-tiempo-real-design.md`. La bandeja deja de
depender sólo del polling: un hub avisa **qué cambió, por ids**, y la SPA invalida y relee por HTTP. El canal
avisa, no transporta: la autorización sigue en los `GET`.

| Qué | Valor |
| --- | --- |
| Hub | `/api/v1/hubs/messaging`, política `messaging.conversation.read`; al conectar revalida módulo `messaging` y membresía activa (`MessagingHub`). |
| Tenant | `X-Tenant-Id` o, **sólo bajo `/api/v1/hubs/`**, el query `tenantId`: el handshake WebSocket del navegador no lleva headers (`TenantRequestResolver`). |
| Eventos | `conversationChanged { conversationId }` y `inboundMessage { conversationId, messageId }` al grupo del tenant; `notify { conversationId, reason }` al miembro asignado, o al tenant si nadie la tiene. |
| Fanout | `pg_notify('messaging_events', …)` después del commit (`PgMessagingNotifier`); cada pod tiene un `MessagingEventsListener` en `LISTEN` que reparte a sus conexiones. Para el reparto de eventos, con dos réplicas no hace falta sticky session ni Valkey. El handshake del hub sí lo necesitaría: `negotiate` crea la conexión en la memoria del pod que lo atiende, así que con más de una réplica el frontend tiene que saltarlo y conectar sólo por WebSocket (`skipNegotiation: true`, spec `2026-10-29-replicas-multiples-design.md`, D-1). |
| Fallos | Un aviso que falla no reintenta la entrega ni deshace el envío: se registra y el polling relajado del frontend (60/30 s) lo recoge. |

Lo que se publica: asignar, transferir, liberar, resolver, reabrir, leído, no leído, saliente consolidado
(`Sent` o `Failed`), acuse aplicado y entrante insertado. Un reenvío de Meta no avisa. Instagram va por el mismo
camino: un DM es un entrante y suena igual; un `is_echo` (el negocio respondió desde la app) mueve la foto de la
lista sin ser un entrante, así que sólo avisa `conversationChanged`; el visto (`read`) y el borrado (`is_deleted`)
avisan `conversationChanged` como un acuse de WhatsApp.

**Runtime ≥ 10.0.13.** CVE-2026-70159 (DoS en SignalR, `SignalR.Common` ≤ 10.0.12) afecta al servidor, que
sale del runtime del `Dockerfile`, no de NuGet. Por eso la imagen se clava en `aspnet:10.0.13` y no en el flotante
`10.0`, y los paquetes `Microsoft.*` de `Directory.Packages.props` van en la misma línea 10.0.13.

**El hub global `/api/v1/hubs/app` es otro.** Mensajería sigue en su propio hub; el global (spec 2026-11-15) no es de ningún módulo,
lo usa hoy Soporte y se describe en [Tiempo real y notas internas de Soporte](#tiempo-real-y-notas-internas-de-soporte).

La preferencia de sonido viaja en `GET/PUT /api/v1/auth/preferences` como `messageSound` (default `true`). El
`PUT` acepta cada campo por separado y conserva el que no viene; un cuerpo vacío es 422
`identity.preference.empty`.
#### Plantillas de WhatsApp desde QEP

Spec: `docs/superpowers/specs/2026-10-14-gestor-de-plantillas-whatsapp-design.md`. El admin (permiso
`messaging.template.manage`, de fábrica sólo en `admin`) gestiona las plantillas de cada conexión `whatsapp-cloud`
bajo `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management`:

| Método y ruta | Qué hace |
| --- | --- |
| `GET /` | La pantalla: conexión, estado de la plantilla de cotización (`quotationTemplate`) y de la de pedido (`orderTemplate`, spec 2026-11-09 D4), última sincronización y todas las plantillas con su estado y motivo de rechazo. Si la conexión nunca se sincronizó, la primera lectura llena el espejo. |
| `POST /templates` | Crea una plantilla libre (texto, variables con nombre, pie, botones) y la manda a aprobar. |
| `POST /quotation` | Aprovisiona la plantilla de cotización con su PDF de ejemplo; reintenta si la anterior fue rechazada; adopta una `qep_cotizacion` igual creada a mano en Meta. |
| `POST /order` | Lo mismo para la plantilla de pedido `qep_pedido` (spec 2026-11-09 D1–D3): variables fijas más `numero_pedido` y `valor_total`, sin vigencia; una por conexión (`messaging.template.order_exists`), adopción de una `qep_pedido` compatible o `messaging.template.order_conflict`. Con ella aprobada, la conexión puede mandar pedidos. |
| `POST /billing` | Aprovisiona las dos plantillas de cobro (recordatorio y mora). Igual que `POST /quotation`, el cuerpo HTTP es **opcional**: sin él se crea el texto base de la definición; con él, el tenant manda su propio texto. |
| `GET /quotation/draft` | Borrador que abre la ventana de edición de la plantilla de cotización: texto inicial, variables disponibles con su etiqueta y si ya se puede editar. |
| `GET /order/draft` | Lo mismo para la plantilla de pedido. |
| `GET /billing/draft` | Lo mismo para las dos plantillas de cobro. |
| `DELETE /templates/{templateId}` | Borra en Meta (sólo ese idioma) y en el espejo; 422 `messaging.template.in_use_by_campaign` si una campaña programada o enviándose la usa. |
| `POST /sync` | Sincroniza el espejo contra Meta. |

**El espejo.** Vive en `messaging.message_templates` y lo alimentan la creación, el webhook
`message_template_status_update` (lo procesa `WebhookDeliveryProcessor`, sin auditoría) y el refresco. El catálogo de
envío (`GET /connections/{id}/templates`) lee del espejo las `APPROVED`; ya no hay caché de cinco minutos. Los
estados y motivos viajan con el nombre que Meta les da; el diccionario en español lo tiene el frontend. Dos eventos
del webhook no son estados: `REINSTATED` deja la fila en `APPROVED`, y `FLAGGED` (feedback negativo, pero la
plantilla sigue enviable) no toca la fila y sólo deja un log (`MessageTemplateStatuses.FromWebhookEvent`); un evento
desconocido se guarda crudo. El refresco no borra nada si el listado de Meta volvió incompleto (más de 10 páginas o un
`paging.next` que no se sigue: queda un warning), y no borra ni pisa una fila creada o actualizada después de empezar
a listar.

**La plantilla de cotización.** Se llama `qep_cotizacion` y su nombre va por versiones: un reintento sobre una
rechazada (o pausada, o deshabilitada) no recrea el mismo nombre —Meta lo bloquea un tiempo después de borrarlo—, sino
que crea `qep_cotizacion_v2`, `_v3`… **antes** de borrar la vieja, y si Meta dice que el nombre ya existe salta a la
versión siguiente con el mismo PDF, hasta veinte creaciones. El prefijo `qep_cotizacion_` está reservado: ninguna libre
lo puede usar. Hay a lo sumo una fila `Quotation` por conexión (`UX_message_templates_quotation`); el nombre vigente es
el de esa fila, no una constante. El PDF de ejemplo lo genera el backend (`QuotationTemplateDefinition.ExamplePdf()`).
Si el admin borra la `Quotation` desde la pantalla, la fila desaparece y el siguiente aprovisionamiento vuelve a
calcular `qep_cotizacion`, que Meta tiene en cuarentena: **la recuperación depende de reconocer ese error** como
`name_taken`. `TemplateGraphErrors` lo hace cuando `error_user_title` o `error_user_msg` dicen «already exist»,
«being deleted», o «deleted» junto a «language» o «template»; el cuerpo real está a confirmar (handoff de Meta). Si
no lo reconoce, sale `rejected_by_meta` y cada intento recalcula la versión 1. El tope de veinte es por esto mismo: sin
fila, el recorrido vuelve a empezar en la 1 y atraviesa otra vez cada nombre borrado en los últimos 30 días, más o menos lo
que dura el bloqueo; vale igual para las dos plantillas de cobro. Si un refresco trae de vuelta la plantilla borrada, llega como
`External` en `PENDING_DELETION`: una `External` que no esté viva no se adopta, no da `quotation_conflict` y no le presta el texto
al borrador. Su nombre sí cuenta para la versión siguiente.

**El texto lo elige el tenant** (spec 2026-10-31). `QuotationTemplateDefinition.BaseBody` y `BillingTemplateDefinition.BaseBody` son
sólo el texto inicial que ofrece el borrador; el tenant lo edita y lo que se envía sale de su fila, no de la definición. Las variables
llevan claves en español (`nombre_contacto`, `asesor`, `empresa`, `saludo`, `numero_cotizacion`, `valor_total`, `vigencia`, y en cobros
`servicio`, `valor`, `vencimiento`, `cantidad_cobros`, `total_vencido`). Sin pie, sólo salen las variables nuevas: las claves viejas
(`fullname`, `order_number`, `total`, `valid_until`) siguen valiendo para las plantillas ya aprobadas. En Automatizaciones,
`FlowValidator.CheckName` exime las variables fijas (`nombre_contacto`, `telefono`, `empresa`, `conexion`) de la regla de nombres propios,
porque llevan guion bajo.

**Versiones huérfanas.** Tres casos dejan una plantilla en Meta sin su fila, y el siguiente refresco la trae como
`External` para que el admin la borre desde la pantalla: el que pierde dos aprovisionamientos simultáneos (recibe
`quotation_exists`), un reintento cuyo borrado de la versión vieja falla por **cualquier** motivo (por el token no
se escribe nada y queda huérfana la nueva; por otra cosa la fila vieja se quita igual y queda huérfana la vieja, con
un warning), y un espejo atrasado, en el que una `qep_cotizacion` creada a mano y todavía no sincronizada hace que el
reintento cree `_v2` en vez de adoptarla. `POST /sync` antes de aprovisionar evita el último.

**La señal para Quotations.** `IQuotationTemplateReadiness.FindApprovedAsync(tenantId, connectionId)` devuelve el
nombre y el idioma de la `Quotation` en `APPROVED` de esa conexión, o `null`. Es lo que el consumidor Quotations
(spec pendiente) leerá por un adaptador en Bootstrapper, y el nombre con el que el sender de Meta enviará.

**La Resumable Upload** del PDF usa `Meta:App:AppId` (ya en el ConfigMap) y el token de la conexión. Meta devuelve el
id con firma (`upload:…?sig=…`) y la firma viaja como query del segundo request. **A confirmar en producción:** que ese
token sirva para `/{AppId}/uploads`; si no, el aprovisionamiento falla con `messaging.template.upload_failed` y hay
que pasar a un token de la app (handoff de Meta, «Notas a confirmar en producción»). Ninguna falla de la subida —ni
un 401, ni un `code` 190 o de permiso— reporta la conexión: `/{AppId}/uploads` es un endpoint de la app, no de la
WABA, así que la bandeja y las campañas siguen andando. Toda falla es `upload_failed` con un warning (tenant,
conexión, status, `code` de Graph y motivo, nunca el token).

**Despliegue.**

1. Migraciones `AddMessageTemplates` y `AddQuotationTemplateIndex` (Messaging). Sólo tocan
   `messaging.message_templates` y `messaging.template_mirror_syncs`, que nacen vacías.
2. Reinicio de la API y **recarga de sesión** de los admins: `messaging.template.manage` es un permiso nuevo y, como
   todo permiso nuevo, no aparece sin las dos cosas.
3. Suscribir `message_template_status_update` en la app de Meta (handoff, paso 5). Sin él, las aprobaciones y los
   rechazos sólo se ven al refrescar la pantalla.
4. Saber qué esperar del primer llenado. El espejo de cada conexión se llena solo con su primera lectura —la pantalla
   o el `GET /templates` de la bandeja o de las campañas—, así que el catálogo de envío no queda vacío tras el
   despliegue; es una llamada a Meta por conexión, también con `messaging.conversation.read`. Dos primeros llenados a
   la vez no fallan: el que pierde lee lo que llenó el otro (un `POST /sync` explícito que pierde sí da 412). El
   catálogo viejo hacía *single-flight* y recordaba 30 s una falla de Meta, y eso ya no existe: con Meta caído, cada
   destinatario de una campaña sobre una conexión nunca sincronizada espera su propio timeout una vez.
5. Avisar que las plantillas creadas en la consola de Meta **ya no aparecen solas a los 5 minutos**: entran con
   *Actualizar* (`GET /templates?refresh=true`) o con el `POST …/sync` del admin. El webhook
   `message_template_status_update` sólo cambia el estado de las que el espejo ya tiene; una plantilla que no tiene
   fila deja un log y la trae el siguiente refresco.

#### Asistente en la conversación

Cada conversación lleva `automation_status` (`None` | `Active` | `Paused`) y, mientras no sea `None`, `automation_started_at`,
`automation_paused_at`, `automation_paused_by_member_id` y `automation_pause_reason` (`AdvisorTook` | `AdvisorReplied` |
`AdvisorTransferred` | `Manual` | `Handoff`). `ConversationSummary.automation` es `null` cuando es `None`, el conteo `counts.automated`
suma las conversaciones con asistente (activo o en pausa) y `GET …/conversations?automation=active|paused|any|none` filtra por él.

`POST …/conversations/{id}/automation/pause` y `…/automation/resume` piden `messaging.conversation.manage` y `If-Match` (428 sin él,
412 si la versión cambió), y responden 200 con el `ConversationSummary`. Los 422: `messaging.conversation.not_open` (se revisa primero),
`messaging.automation.not_available` (la conversación no tiene asistente), `messaging.automation.already_paused` y
`messaging.automation.already_active`.

`POST …/conversations/{id}/automation/start` inicia el asistente desde el hilo (spec 2026-10-31). Pide `messaging.conversation.manage` y
`If-Match` (428 sin él, 412 si la versión cambió, también si la ingesta tocó la conversación mientras se decidía) y responde 200 con el
`ConversationSummary`. **Responde antes del envío:** deja la conversación `Active` con el evento `AutomationStarted` y encola una fila
manual en `automation_inbox` (sin mensaje; ver «Una fila de `automation_inbox` tiene tipo» en `CLAUDE.md`); la unidad corre en el worker, así que el primer mensaje del bot llega
después de la respuesta. La decisión es la de `ManualRunStarter` (la misma del motor). Los 422, en el orden en que se deciden: `messaging.conversation.not_open`,
`messaging.automation.has_advisor`, `messaging.contact.blocked`, `messaging.channel_unsupported` (sólo WhatsApp),
`messaging.automation.not_available` (el módulo no está activo para el tenant), `messaging.automation.already_active` (el asistente ya
atiende o un arranque está en cola), `messaging.automation.no_chatbot` (la conexión no tiene un chatbot activo) y
`messaging.window_closed` (pasaron 24 horas desde el último mensaje del cliente). Un 412 `concurrency.conflict` también sale si otro request tenía el
candado de la conversación mientras se decidía. `ConversationSummary.automationStart` es `Available` | `WindowClosed` | `NoChatbot` o `null` (BFF: el
encabezado decide si dibuja «Iniciar asistente» activo, apagado o nada); sólo se llena en las respuestas de una conversación, no en el
listado ni en la búsqueda.

`POST …/conversations/{id}/automation/continue` devuelve una pausa manual para que el asistente siga en el paso pendiente (spec
2026-11-01). Sin cuerpo; pide `messaging.conversation.manage` y `If-Match` (428 sin él, 412 si la versión cambió o si otro request tenía el
candado de la conversación) y responde 200 con el `ConversationSummary`. **No manda nada:** deja la conversación `Active` con el evento
`AutomationContinued` y la auditoría `messaging.conversation.automation_continued`, no escribe `automation_resumed_at` y no arranca ni
avanza ninguna corrida; sólo renueva el reloj de 24 h del barrido. El siguiente entrante sigue la misma corrida, en su versión y con
sus variables e intentos. Lo que el cliente escribió durante la pausa no se reprocesa. Los 422 son los de `automation/start` y
`messaging.automation.cannot_continue` (no hay paso que retomar: no cae a `resume`; se relee el resumen).

`ConversationSummary.automationContinue` va al lado de `automationStart`, con la misma regla de dónde se llena:

```jsonc
"automationContinue": { "mode": "PendingStep", "customerWroteDuringPause": false } // o null
```

| Estado | `mode` | Primera opción del modal | POST |
|---|---|---|---|
| `Paused`, `automationStart: Available`, se puede continuar | `PendingStep` | «Continuar donde iba» | `automation/continue` |
| `Paused`, `automationStart: Available`, no se puede | `FlowStart` | Empieza el flujo cuando el cliente escriba | `automation/resume` |
| Cualquier otro caso, o falló la evaluación | `automationContinue: null` | Sin modal de dos opciones | — |

Se puede continuar con pausa `Manual`, corrida `Waiting`, nadie devolvió la conversación después de que la corrida arrancó, el chatbot de la
conexión es el de la corrida y ningún evento `Taken`, `AutoTaken` o `Transferred` desde la pausa. `customerWroteDuringPause` es
`lastInboundAt > automationPausedAt`, en los dos modos: esos mensajes el asistente no los lee en ninguno.

Tomar, transferir y responder una conversación pausan el asistente (`AdvisorTook`, `AdvisorTransferred`, `AdvisorReplied`); soltarla lo
deja en pausa; reanudarlo libera al asesor asignado; resolver lo devuelve a `None`. Cada cambio deja el evento `AutomationPaused` /
`AutomationResumed` (con `pauseReason`) en el hilo y la auditoría `messaging.conversation.automation_paused` / `automation_resumed`. Los
eventos de un mismo cambio llevan `occurred_at` estrictamente creciente (+1 µs por posición) para que el hilo se lea en orden.

**Nada pone `Active` en producción todavía:** el motor del bot llega con el spec 4. Hoy sólo lo hace el harness de pruebas
(`MessagingApiHarness.ActivateAutomationAsync`).

#### Horarios de atención

Un tenant define horarios semanales y los asigna a sus conexiones de WhatsApp e Instagram (spec 2026-10-21 §5). Viven en
`messaging.business_hours_schedules` (`weekly` y `exceptions` en `jsonb`, `holiday_country` `'CO'` o `null`, nombre único por tenant sin
distinguir mayúsculas y un solo predeterminado por tenant) y `messaging.connection_business_hours` (un horario por conexión).

| Ruta (bajo `…/messaging/business-hours`) | `If-Match` | Respuesta | Errores propios |
| ---------------------------------------- | ---------- | --------- | --------------- |
| `GET /` | no | `items`, `timeZone` y `connections` (todas las conexiones del tenant, en cualquier estado, con su horario actual) | |
| `POST /` | no | 201 | 409 `messaging.business_hours.name_taken` |
| `PUT /{id}` | sí (412 / 428) | 200; reemplaza el horario y sus conexiones | 404 `not_found`, 409 `name_taken` |
| `DELETE /{id}` | sí (412 / 428) | 204 | 404 `not_found`, 422 `in_use` (tiene conexiones vivas) o `is_default` |
| `POST /{id}/duplicate` | no | 201, nombre «… (copia)», «… (copia 2)»… | 404 `not_found`, 409 `name_taken` |
| `POST /{id}/default` | sí (412 / 428) | 200; sube la versión de este y del predeterminado anterior | 404 `not_found`, 409 `default_conflict` |

Todos los códigos propios llevan el prefijo `messaging.business_hours.`. Escribir pide `messaging.business_hours.manage` (de fábrica sólo
en `admin`); leer, `messaging.conversation.read`. El 422 `validation.failed` trae el mapa `errors` con las claves `Name`, `Weekly`,
`Exceptions`, `HolidayCountry` y `ConnectionIds`, y anidadas `Weekly[i].Ranges[j].To`: una franja que cruza la medianoche se rechaza
(«divídela en dos días»). Las excepciones pasadas se podan al guardar. Auditoría: `messaging.business_hours.created`, `updated`,
`deleted`, `duplicated`, `default_set` y `connections_assigned`.

**Qué horario aplica a una conexión:** su asignación; si no tiene, el predeterminado del tenant; si tampoco hay, siempre abierta (D-H4).
Un festivo (`holidayCountry = "CO"`, Ley 51 de 1983, calculados por `ColombianHolidays`) cierra el día completo, salvo que exista una
excepción con franjas para esa fecha: la excepción gana. `GET …/messaging/connections` devuelve por conexión `businessHoursScheduleId`,
`businessHoursName` y `openNow`.

### Mensajería (Instagram)

Instagram es el **segundo canal de la misma bandeja** (spec `2026-10-14-mensajeria-instagram-design.md`): los DM que
una persona le escribe a la cuenta profesional del tenant llegan por un webhook propio, se leen en
`/messaging/conversations` con `channel: "Instagram"` y se responden desde ahí dentro de la ventana de 24 horas.
Mismo módulo `messaging`, mismos permisos, mismo esquema. Lo que cambia por canal está en la conexión (proveedor
`instagram` de Integrations, onboarding `InstagramLogin`) y en lo que la API de Instagram no tiene: plantillas,
campañas y conversación nueva responden 422 `messaging.channel_unsupported`; `GET …/connections/{id}/templates`
de una conexión de Instagram devuelve `[]`.

**Conectar (Integrations).** `GET /integrations/catalog` trae el proveedor con `onboarding.kind = "InstagramLogin"`,
el `appId` del caso de uso Instagram (distinto del de la app, `Meta:Instagram:AppId`), el `authorizeUrl` y los dos
`scopes` (`instagram_business_basic`, `instagram_business_manage_messages`). El SPA abre la ventana de Meta y manda el
`code` a `POST /integrations/instagram/connect { name, code, redirectUri }`; el backend canjea el `code` por el token
corto, lo cambia por el largo (60 días), lee `/me` (tiene que ser cuenta `BUSINESS` o `MEDIA_CREATOR`), suscribe la
cuenta al webhook (`POST /{igUserId}/subscribed_apps`) y guarda la conexión `Active` con `igUserId`, `username`,
`accountName` y `tokenExpiresAt`, más la ruta `(instagram, igUserId)` que usa el webhook. El `redirectUri` tiene que
ser de un origen de `Meta:Instagram:AllowedRedirectOrigins`. Un worker diario (`Integrations:Instagram`) refresca el
token cuando le quedan menos de 15 días; un `190` lo pasa a `NeedsAttention` (`token_expired`). Cada conexión se refresca
bajo un candado de base por conexión (`InstagramTokenLock`, owner 2026-11-14), así que con varias réplicas un solo pod llama
a Meta por token; el que no lo consigue la difiere a la siguiente corrida.

**Webhook.** `GET /api/webhooks/instagram` responde el `hub.challenge` con el mismo `Meta:App:WebhookVerifyToken`.
`POST` valida `X-Hub-Signature-256` con `Meta:Instagram:AppSecret` (y acepta también `Meta:App:AppSecret`, por si Meta
firmara con ése) y guarda la entrega en `webhook_deliveries` con `provider = 'instagram'`. El mismo worker la procesa
con el parser de Instagram (`object: "instagram"`, `entry[].id` = cuenta, `entry[].messaging[]`, `timestamp` en
**milisegundos**): la ruta es la cuenta profesional; `Paused` o módulo apagado descartan los mensajes y aplican igual
el visto y el borrado. Campos que hay que suscribir en el dashboard: `messages`, `messaging_seen`,
`messaging_postbacks`, `messaging_reactions`.

**Qué entra y cómo.** Un `message` crea la conversación por `(connection_id, user_id = IGSID)` con `channel =
'Instagram'`, `wa_id` siempre `null`; si es nueva, pide el perfil (`GET /{igsid}?fields=name,username,profile_pic`,
best effort) y asegura el cliente por `instagram_user_id` (incompleto, con `instagram_username`, sin teléfono). Los
adjuntos `image|video|audio|file` guardan `message_media.source_url` (la URL pública con vencimiento de Meta) y el
worker de medios los baja de ahí (sin token, hasta 25 MB, sin hash de referencia) a la misma clave de R2. Una
publicación o reel compartido (`share`, `ig_post`, `ig_reel`…) es el `kind` **`Share`** (14) con la URL en `text`.
Una reacción es `Reaction` con `wamid = "reaction:{mid}:{ts}"` y la cita al mensaje reaccionado; `unreact` se
ignora. Un `postback` es `Interactive`. Un `is_echo` (lo que el negocio respondió desde la app de Instagram, o desde
QEP: el `mid` lo deduplica) entra como saliente `Sent` sin autor, mueve la foto de la lista y no toca el contador.
`read` (`messaging_seen`) marca `Read` todos nuestros mensajes de esa conversación hasta ese `mid`. `is_deleted` deja
`details.deleted = true` y el mensaje viaja con `"deleted": true` (el texto se conserva).

**Responder.** `POST …/messages`: `text` hasta **1000** caracteres (422 en `text` si pasa); va a
`POST graph.instagram.com/{v}/{igUserId}/messages` con `recipient.id = IGSID` y el token de la cuenta; `replyTo` se
guarda pero no viaja (Instagram no cita). Imagen (jpeg/png), **sólo PDF** de documentos, video **sólo `.mp4`** y audio
`.mp3`, `.m4a` o `.aac` (spec 2026-10-20, en borrador hasta V-2), todo hasta 25 MB: la sesión de subida es la misma y ya
rechaza lo que Instagram no recibe; la confirmación, en vez de subir a Meta, deja el objeto en R2 y le da a Meta una URL
prefirmada de 10 minutos en `attachment.payload.url` con `type` `image`, `file`, `video` o `audio` (`caption` se guarda y se
muestra en QEP; no viaja, y en un audio es 422 como en WhatsApp). Nota de voz: mismo contrato
que WhatsApp, y el backend la **transcodifica a WAV** (PCM 16 bits, mono, 16 kHz, con Concentus, sin ffmpeg) antes de
guardarla y enviarla como `audio`; un WAV de más de 25 MB es `media_too_large`. Un `400` con `code 10` (subcódigo
`2534022`) es la ventana cerrada: `messaging.window_closed` y la fila `Failed` con `failure_code = 10`. Marcar leída
manda `sender_action: mark_seen` a la persona, best effort.

**Configuración.** `Meta:Instagram:AppId`, `Meta:Instagram:AppSecret` (secreto), `Meta:Instagram:GraphApiVersion`
y `Meta:Instagram:AllowedRedirectOrigins` (producción: `https://qep.qcode.co`; local: `http://localhost:3002`).
Pipeline: `META_INSTAGRAM_APP_ID` (normal) y `META_INSTAGRAM_APP_SECRET` (secreta) en `Backend-prod`. En
`Production`, `MetaInstagramOptionsValidator` exige las tres primeras.

#### Despliegue

1. Variables `META_INSTAGRAM_APP_ID` y `META_INSTAGRAM_APP_SECRET` en `Backend-prod`.
2. Backend primero (migraciones `AddInstagramProvider`, `AddInstagramChannel` y `AddCustomerInstagram`), después el
   webhook en Meta (hace el `GET` al guardarlo), después el frontend. Lo que hace el owner del lado de Meta y lo que
   queda por verificar con la primera cuenta real (V-2 a V-5, V-7 y V-8) está en
   `docs/superpowers/plans/2026-10-14-mensajeria-instagram-handoff-meta.md`.

### Embudo de ventas (Pipeline)

El embudo del asesor (spec 2026-10-13): negocios en un kanban por etapas, enlazados al chat y a la
cotización, con historial de etapas desde el día 1 y señal de estancado. Módulo `pipeline` (depende de
`customers`, se prende por tenant), permisos `pipeline.deal.read|manage` (`admin` y `advisor`) y
`pipeline.settings.manage` (sólo `admin`), todos de núcleo: cada handler revisa la capacidad y responde 403
`tenancy.module_not_enabled`. Esquema `pipeline` (`pipelines`, `stages`, `deals`, `deal_stage_history`,
`deal_quotations`, `reasons`, `triage_decisions`, `inbox_messages`).

**Primer acceso.** El embudo no existe hasta que alguien lo pide: el primer `GET` (o el primer evento de una
cotización) lo crea con sus semillas —Nuevo 10 %/3 d, Calificado 25 %/5 d, Cotizado 50 %/7 d (etapa de
cotización), Negociación 75 %/7 d, Ganado, Perdido— y los motivos de pérdida y de descarte. Tenancy no avisa
cuando se prende un módulo, así que no hay otro lugar donde engancharse.

**Rutas** bajo `/api/v1/tenants/{tenantId}/pipeline`:

| Método | Ruta | Permiso |
| --- | --- | --- |
| `GET` | `/board?owner=me\|all` | `pipeline.deal.read` |
| `GET` | `/deals/{dealId}` | `pipeline.deal.read` |
| `POST` | `/deals` | `pipeline.deal.manage` |
| `PATCH` | `/deals/{dealId}` | `pipeline.deal.manage` |
| `POST` | `/deals/{dealId}/move\|win\|lose\|reopen` | `pipeline.deal.manage` |
| `POST` | `/deals/{dealId}/quotations/{quotationId}/link\|primary` | `pipeline.deal.manage` |
| `GET` | `/owners` | `pipeline.deal.manage` |
| `GET` | `/customers/{customerId}/summary?conversationId=` | `pipeline.deal.read` |
| `POST` | `/triage/{conversationId}` (`create` \| `dismiss`) | `pipeline.deal.manage` |
| `GET`/`PUT` | `/settings/stages`, `/settings/loss-reasons`, `/settings/dismiss-reasons` | `pipeline.deal.read` / `pipeline.settings.manage` |

Toda escritura sobre un negocio lleva `version` (412 si cambió) y devuelve el negocio completo. Un negocio
creado por la API sólo puede tener como responsable a una membresía activa (`pipeline.deal.owner_not_member`);
el que crea un evento de cotización conserva al asesor de la cotización como responsable aunque ya esté
inactivo. El historial de etapas se ordena por un ordinal por negocio que asigna el dominio, no por la hora.
`GET /owners` devuelve las membresías `Active` del tenant que pueden ser dueñas de un negocio, ordenadas por nombre
(`[{ memberId, name }]`, con el correo cuando no hay nombre): el formulario sólo dibuja la lista y la regla de «dueño
válido» sigue en el backend. Un miembro activo sin nombre ni correo se lista como «Miembro sin nombre» en vez de
omitirlo, y si ninguna membresía califica la respuesta es `[]` con 200. Exige `pipeline.deal.manage`; con cualquier
403 el frontend oculta el selector.
El tablero trae todas las columnas, sumas por moneda, como máximo 200 tarjetas por columna con el conteo real, y
`Ganado`/`Perdido` sólo de los últimos 30 días. No hay lista de pendientes de triage: el estado de una conversación
lo da el bloque `triage` del resumen del cliente. Cada decisión de triage guarda el último entrante contra el que se tomó: con el índice único
`IX_triage_decisions_conversation_inbound`, si dos asesores deciden a la vez uno recibe
`pipeline.triage.already_decided` y queda un solo negocio. Un mensaje nuevo del cliente permite decidir otra vez.

**Eventos de Quotations.** Quotations escribe en `platform.outbox_messages`, en la misma transacción que el
cambio, `quotations.quotation.sent.v1` (también al reenviar), `quotations.quotation.converted.v1` y
`quotations.order.cancelled.v1`, con payload camelCase (`tenantId`, `quotationId`, `quotationNumber`,
`clientId`, `advisorMemberId`, `total`, `currency`, `occurredAt`; converted suma `orderId` y `orderNumber`;
cancelled lleva `tenantId`, `orderId`, `quotationId`, `occurredAt`). El payload es contrato: cambiarlo
rompe a Pipeline sin error de compilación. `QuotationEventsWorker` los consume cada 3 s con reclamo e
inbox propio (consumidor `pipeline.quotation-events`, reintentos a los 5 s, 30 s y cada 5 min). Un evento
de un tenant sin `pipeline` se marca procesado sin efecto: prender el módulo después no rellena el pasado.

**Despliegue.** Backend primero (migraciones `AddPipelineModuleKey` en Tenancy y `AddPipeline` en Pipeline),
después el frontend del embudo —kanban, panel del chat con triage, ajustes y la sección de negocios en la
ficha del cliente de la conversación—. Los permisos nuevos exigen reiniciar la API y recargar la sesión.
Prender `pipeline` al tenant desde la consola de operador; para el triage también hace falta `messaging`.

### Automatizaciones: constructor de chatbots

El constructor de chatbots de WhatsApp (spec 2026-10-21, planes backend 1 y 2): un admin arma un flujo de bloques en un
lienzo, lo valida y lo publica sobre una o varias conexiones, y el motor (ver [El motor](#el-motor-de-chatbots)) lo ejecuta contra
los clientes que escriben. Módulo `automations` (exige `messaging`, se prende por tenant),
permisos `automations.chatbot.read|manage` de núcleo y de fábrica sólo en `admin`: cada handler revisa el módulo y
responde 403 `tenancy.module_not_enabled`. Esquema `automations` con siete tablas: `chatbots`, `chatbot_versions`,
`chatbot_connections`, `chatbot_templates`, `conversation_runs`, `http_calls` y `simulations`. Los únicos son
`IX_chatbots_tenant_name` y `IX_chatbot_templates_tenant_name` sobre `(tenant_id, lower(name))` (por SQL en la migración:
EF no modela índices por expresión), `IX_chatbot_versions_chatbot_number` (1, 2, 3… por chatbot) y dos parciales:
`IX_chatbot_connections_active` (una conexión la atiende a lo sumo un chatbot publicado, `WHERE active`) e
`IX_conversation_runs_live` (una corrida viva por conversación, `WHERE status IN ('Running','Waiting')`).

**Rutas** bajo `/api/v1/tenants/{tenantId}/automations` (§4.4):

| Método | Ruta | Permiso |
| --- | --- | --- |
| `GET` | `/chatbots?search=` | `automations.chatbot.read` |
| `POST` | `/chatbots` (`source.kind`: `blank` \| `template` con `templateId` y `parameters` \| `duplicate` con `chatbotId`) | `automations.chatbot.manage` |
| `GET` | `/chatbots/{chatbotId}` (el editor: borrador, versión publicada, validación, conexiones elegibles, variables) | `automations.chatbot.read` |
| `PUT` | `/chatbots/{chatbotId}` (nombre, disparador, palabras clave, conexiones) | `automations.chatbot.manage` |
| `PUT` | `/chatbots/{chatbotId}/draft` (`{ flow, layout }`) | `automations.chatbot.manage` |
| `POST` | `/chatbots/{chatbotId}/validate` | `automations.chatbot.read` |
| `POST` | `/chatbots/{chatbotId}/publish\|pause\|resume` | `automations.chatbot.manage` |
| `POST` | `/chatbots/{chatbotId}/duplicate` | `automations.chatbot.manage` |
| `DELETE` | `/chatbots/{chatbotId}` | `automations.chatbot.manage` |
| `POST` | `/chatbots/{chatbotId}/save-as-template` | `automations.chatbot.manage` |
| `GET` | `/templates` (del sistema y del tenant) | `automations.chatbot.read` |
| `DELETE` | `/templates/{templateId}` (sólo las del tenant; una del sistema es 404) | `automations.chatbot.manage` |
| `GET` | `/chatbots/{chatbotId}/export` | `automations.chatbot.manage` |
| `POST` | `/chatbots/import` (`{ name, export, connectionMap }`) | `automations.chatbot.manage` |
| `GET` | `/connections` (las conexiones que el editor ofrece y qué chatbot ocupa cada una) | `automations.chatbot.read` |
| `GET` | `/external-apis` (las conexiones «API externa» del tenant, para mapear al importar) | `automations.chatbot.read` |

Toda escritura sobre un chatbot existente lleva `If-Match` (428 sin él, 412 si es viejo), menos duplicar y guardar como plantilla. Es **un solo**
`version` compartido entre `PUT /chatbots/{id}` y `PUT …/draft`: guardar el borrador lo mueve y el formulario de ajustes
tiene que mandar el que le devolvió el último guardado, y al revés.

- **Borrador.** El cuerpo de `PUT …/draft` tiene un tope de 800 KB medido en bytes antes de leerlo (413 por
  `Content-Length` o sin él, chunked); adentro, el flujo ≤ 512 KB y el layout ≤ 256 KB, también en bytes UTF-8. El
  `layout` no se valida (D-C2). Un flujo que no se puede leer es 422 `validation.failed` con `errors.flow` =
  `[código, detalle]`: el código es `flow.malformed` (JSON inválido, `schema` distinto de 1, un campo con otro tipo o fuera
  de su lista cerrada, un id de bloque que falta o se repite) o `flow.unknown_kind`, y el detalle es el texto del parser,
  para diagnosticar. La auditoría `automations.chatbot.draft_saved` se escribe a lo sumo una vez por minuto por chatbot.
- **Publicar.** Copia el borrador a una versión inmutable; `Draft` pasa a `Active` y ocupa todas sus conexiones. Con
  errores responde 422 `automations.chatbot.invalid` y `errors.flow` trae **sólo los errores** (no los avisos), cada uno
  como `"<código>: <mensaje>"` (R16).
- **Borrar** un chatbot es 409 `automations.chatbot.has_live_runs` sólo mientras alguna corrida viva esté atendida por el bot
  (`BotAttendance.IsAttending`: corrida viva y nadie humano tomó la conversación; una conversación que no existe no atiende). Una
  corrida viva que un asesor ya tomó no bloquea (spec 2026-11-03, D-7).
- **Exportar e importar.** El export lleva el borrador (flujo y layout), nombre, descripción, disparador, palabras clave y
las «API externa» que usa. El cuerpo de
  `POST /import` tiene un tope propio de 1 MB, para que un export con flujo y layout en sus topes se pueda volver a
  importar. `connectionMap` (≤ 50 pares) cambia las conexiones de «API externa» del tenant de origen por las del destino.
- **Plantillas del sistema.** `welcome-menu-handoff` y `outside-hours` son JSON embebidos en
  `Modules.Automations.Infrastructure/Seed/Templates/` y se cargan al arrancar: un archivo roto tumba el host, a propósito.
  `save-as-template` guarda el borrador sin conexiones de «API externa».

**Validación del flujo (`FlowValidator`).** Los códigos de `issues` son `start.no_first_node`,
`node.output_unconnected`, `node.output_unconnected_optional`, `node.unknown_target`, `node.unreachable`,
`buttons.too_many`, `buttons.label_too_long`, `buttons.id_invalid`, `list.row_id_invalid`, `list.too_many_rows`,
`list.row_title_too_long`, `list.row_description_too_long`, `list.channel_not_supported`, `text.too_long`, `text.empty`,
`http.connection_missing`, `http.connection_paused`, `http.body_not_json`, `http.path_invalid`, `http.no_sample_response`,
`condition.no_rules`, `variable.name_invalid`, `variable.never_set` y `business_hours.not_assigned`, más los de parseo
`flow.malformed` y `flow.unknown_kind`. La lista cerrada `fallback` de una pregunta es `repeat | otherOutput | handoff`
(R15). Como las versiones son inmutables, desde el plan 2 (P2-R8) también son **errores** lo que Meta o el motor no pueden
ejecutar: `buttons.empty` (cero botones), `list.no_rows` (lista fija sin filas), `list.dynamic_incomplete` (lista dinámica sin
colección, `idField` o `titleField`), `message.url_invalid` (URL de botón vacía o no https), `http.timeout_invalid`
(`timeoutSeconds` fuera de 1..10), `question.max_attempts_invalid` (`maxAttempts` fuera de 1..5), `question.pattern_invalid`
(validación `regex` con patrón vacío o que no compila) y `question.keywords_empty` (validación `keywords` sin lista);
`http.path_invalid` rechaza además un segmento `.` o `..`. `http.mapping_path_invalid` (P2-R39) es el `from` de un mapeo que
el ejecutor no sabe leer: antes se guardaba `null` sin avisar, así que un borrador viejo con un `from` malo ya no publica hasta
corregirlo. Y `trigger.keywords_empty` es del chatbot, no de un bloque: lo agrega el handler de publicar (`nodeId` nulo) cuando
el disparador es `Keyword` sin palabras.

**Gramáticas.** Hay dos, y no son la misma:

- *Referencia a variable* (`{cliente.firstName}`, `{data[0].id}`): nombre, y luego `.nombre` o `[n]`. Es lo que lee el
  motor (`TemplateText`, `JsonPath.Select`). El validador la detecta con un patrón algo más laxo (`VariableToken`, que acepta
  `{_x}` y `{a..b}`), así que hay referencias que pasan sin aviso y el motor deja **vacías** en tiempo de ejecución: un texto
  con `{` que no es variable se queda tal cual.
- *Ruta del mapeo de la respuesta* (`mapping.from`, P2-R36): además de la anterior, `$` (el cuerpo entero), `$[0]`, `$.data`,
  `[0].id` y `data.items[0].id`. Existe porque Clasic responde arreglos en la raíz (`[].id` en el Anexo C). `$` no nombra una
  variable: no vale como referencia.

**Interpolación en el bloque HTTP.** El cuerpo es JSON-aware (P2-R11): un `{variable}` dentro de un string se reemplaza por el
texto de la variable **escapado** para JSON; uno suelto (fuera de comillas) por su literal JSON (número, booleano, objeto,
arreglo, o string entre comillas), y una variable ausente es `null`. El cuerpo resultante se vuelve a parsear: si no es JSON, el
outcome es `body_not_json` **antes** de llamar. En la ruta, cada valor interpolado se escapa con `Uri.EscapeDataString`
(P2-R31): un `/`, `?`, `#` o `%` que venga de lo que escribió el cliente no cambia la ruta ni agrega una query. Se rechaza sin
llamar (outcome `url_rejected`) un valor que sea o contenga un segmento `.` o `..`, también codificado y en `/` o `\`
(`path_dot_segment`, P2-R32/R34) y uno vacío —una variable sin valor— o de sólo `/` y `\` (`path_value_empty`, P2-R46):
`/customers/{id}` sin id llegaría a la colección entera con un `PUT` o `PATCH`, y `%2F` también si un proxy lo decodifica.

**Contrato para el frontend (plan 3).** El editor manda **siempre todos los campos** de cada bloque: `""` (nunca `null`)
para un texto que todavía no se escribió, y `null` (nunca `""`) para ids y números sin fijar (`connectionId`, `reasonId`…);
las listas cerradas llevan su default. Los ids de botón y de fila los genera el editor. Las palabras
clave se guardan normalizadas (minúsculas, sin tildes) y así vuelven. Un solo `If-Match` para ajustes y borrador (arriba).

**Lo que Mensajería expone para el motor.** `IBotOutbound` (enviar como el bot, con las mismas guardas de estado que un
asesor y la misma regla de texto) e `IAutomationInbox` (la cola `messaging.automation_inbox` que llena el webhook y que el
motor reclama con lease de 30 s y testigo de intentos) son puertos de Mensajería que Automations consume por adaptadores en
Bootstrapper (`src/Bootstrapper/Automation*.cs`). La ingesta encola con `Messaging:Automation:Enqueue`, **prendido** por defecto
desde el plan 2 (`appsettings.json`): apagarlo deja al bot mudo sin error, porque el webhook ya no encola. En el hilo,
`MessageDto.sentByChatbot` (el chatbot autor) e `interactive` (los botones, la lista o el enlace de un interactivo saliente) son
aditivos: un cliente viejo los ignora.

#### El motor de chatbots

Plan 2 (spec 2026-10-21 §5, §7, §8). Un `BackgroundService` (`AutomationInboxWorker`) reclama **una fila por llamada** de
`messaging.automation_inbox` (así cada unidad arranca con el lease de 30 s entero) hasta `InboxBatchSize` por pasada, y
`RunProcessor` procesa cada una en su propio scope: toma el candado de la conversación (`TryLockConversationAsync`, siempre, antes
de leer la corrida viva, y sin esperarlo: ver abajo), decide qué le toca al bot y aplica los efectos del `FlowEngine`, un dominio puro (`Step(flow, settings,
state, input, now)` → estado nuevo + efectos). El motor **sólo responde** una conversación `Open`, sin asesor asignado y con
`automation_status` `Active` (o sin asistente y con disparador que aplique); `IBotOutbound` vuelve a verificarlo al enviar, porque
entre el claim y el envío un asesor puede haber tomado la conversación. Una corrida viva termina con **la versión con que
empezó** (D-C1): se lee su `chatbot_version_id`, no la publicada. La excepción es la palabra de reinicio (2026-10-23): si el
chatbot que atiende la conexión es el mismo de la corrida, está `Active` y publicó una versión distinta, la corrida termina
`Completed` y en la misma unidad arranca una nueva con la publicada (la conversación sigue `Active`). Con la misma versión, con el
chatbot pausado o con otro chatbot en la conexión, el reinicio se queda en la corrida vieja.

Cada fila de `automation_inbox` tiene un `kind` (spec 2026-11-03): `Engine` (los entrantes y los arranques manuales) o `ReleaseRun`, la
señal de que una persona resolvió, tomó, transfirió o devolvió la conversación (también la asignación automática al responder);
pausar **no** la encola. `RunProcessor` atiende `ReleaseRun` en su propia rama: cierra la corrida viva (`Interrupted`, motivo
`human_took_over`, outcome `interrupted`) sólo si arrancó antes de la fila (`run.StartedAt < item.CreatedAt`) y
`BotAttendance.HumanTookOver` sigue siendo verdadero; si no, la fila se cierra `skipped`. Nunca arranca, avanza ni envía. La métrica de
cola `automation` (`QueueMetricsSource`) cuenta también las `ReleaseRun` pendientes como backlog, a propósito: son trabajo real del
worker.

**Despliegue y rollback.** Antes de volver a un código que no conoce `kind`, cierra las señales pendientes:

```sql
UPDATE messaging.automation_inbox SET processed_at = now(), outcome = 'skipped'
 WHERE kind = 'ReleaseRun' AND processed_at IS NULL;
```

El código viejo lee una `ReleaseRun` pendiente como un arranque manual y, tras «Devolver al asistente», arrancaría el flujo y mandaría
el primer mensaje. Lo mismo pasa unos segundos en un despliegue gradual (un pod worker viejo que se solapa con el pod de la API
nuevo), así que el despliegue va en un momento tranquilo.

- **Reintento sin duplicar.** El `clientId` de cada envío es `DeterministicClientId.For(runId, baseStepNumber, effectIndex)`
  (P2-R5): `baseStepNumber` es el `StepNumber` persistido al empezar la unidad y `effectIndex` el ordinal del envío dentro de ella
  (contando las reentradas de HTTP y horario). El id de una corrida **nueva** sale del id del mensaje del inbox
  (`DeterministicClientId.RunIdFor`), así que un `Start` que se deshizo y se reintenta reusa corrida y `clientId`s, y el backend
  de Mensajería, que es idempotente por `clientId`, no manda dos veces. El `attempts` de la fila **no** entra: cambia en cada
  reintento. Además `conversation_runs.last_inbound_message_id` marca el último entrante atendido (P2-R24): si la fila se vuelve a
  entregar tras un commit, se completa con outcome `duplicate`.
- **Corridas que terminan mal** (`failure_code`, más un `Handoff` y la auditoría `automations.run.failed`):
  `automations.run.loop_detected` (el flujo volvió a un bloque sin esperar al cliente, P2-R13, o pasó el tope de reentradas),
  `automations.run.target_missing` (una salida apunta a un bloque que no existe en la versión guardada, P2-R14/R17) y
  `automations.run.version_missing` (la versión de una corrida viva ya no existe, P2-R6). Los de un envío rechazado son los
  códigos de Mensajería (`messaging.connection_unavailable`, P2-R21…). Abandono: una corrida `Waiting` sin actividad 24 h se
  cierra `Abandoned` (D-M13); una conversación pausada, resuelta o devuelta al asistente a mano se cierra `Interrupted` (D-M15).
- **Tope de reentradas: `EngineReentries.Max = 25` por unidad** (P2-R40). Un bloque HTTP u Horario devuelve el control al motor
  en la misma unidad; el validador permite un ciclo que pasa por ellos, y sin tope no terminaría nunca. El simulador usa el mismo.
- **Módulo `automations` apagado** (P2-R42). La ingesta sólo mira `messaging`, así que las filas siguen llegando al inbox; cada
  unidad mira el módulo del tenant (`IAutomationsModuleStatus`, caché por pod de 60 s como `WebhookRouting`) y, si está apagado,
  no contesta: la corrida viva termina `Interrupted`, una conversación con el asistente `Active` vuelve a `None` y la fila se cierra
  `skipped`. Apagar tarda hasta un minuto en verse.
- **El lease de 30 s no acota la unidad** (P2-R45). Peor caso: cada bloque HTTP puede esperar 20 s su adjunto (un presupuesto por
  bloque, compartido entre la url y el archivo) más 10 s de timeout, o sea 30 s, y una unidad admite 25 reentradas: 25 × 30 s =
  750 s, más hasta 15 s por envío. Con una sola espera de adjunto seguida de un timeout ya se gasta el lease entero. Por eso el
  candado de la conversación se toma con `pg_try_advisory_xact_lock` (la misma forma de dos claves): quien vuelve a reclamar la fila
  mientras la unidad original sigue no se queda bloqueado detrás, sino que la reprograma a 5 s sin cerrarla (resultado `locked`,
  que no se escribe). Si la original confirma antes, el reintento ve el marcador de P2-R24 y cierra `duplicate`. Cada reprogramación
  gasta un intento (lo suma el reclamo), así que una unidad que pasa de unos 45 s (lease de 30 s + tres reintentos de 5 s) ve su
  fila cerrada `failed` por Mensajería mientras termina: su trabajo queda hecho una vez (su `CompleteAsync`, con el testigo viejo,
  no cuenta) y sólo el outcome miente. **Límite conocido:** con la fila cerrada, el entrante siguiente de esa conversación ya se puede
  reclamar y también choca con el candado; si la unidad original sigue unos 25 s más (≈70 s en total), ese entrante agota sus
  intentos y el bot no lo atiende. El barrido de abandonadas tampoco espera el candado: una unidad en curso es un entrante.
- **Outcomes de la fila** (`automation_inbox.outcome`): `started`, `advanced`, `ended`, `skipped`, `interrupted`, `duplicate` y
  `run_failed` (la corrida terminó `Failed` y pasó a un asesor, P2-R46). `failed` es sólo el de Mensajería: una fila que agotó sus
  5 intentos.
- **Retención.** Las filas procesadas del inbox se purgan a los 7 días (`InboxRetentionDays`, P2-R7), `http_calls` a los 30
  (`HttpCallRetentionDays`) y las simulaciones vencen a las 2 h sin mensajes. Los barridos corren cada `AbandonSweepMinutes` (10)
  y las simulaciones en su propio bucle (`SimulationSweepMinutes`, 10).

Opciones, todas bajo `Automations:Workers` (`AutomationsWorkerOptions`, con defaults en código; `appsettings.json` los repite):

| Clave | Default | Qué hace |
| --- | --- | --- |
| `InboxPollSeconds` | 1 | El tick del worker del inbox |
| `InboxBatchSize` | 20 | Filas por pasada (una por `ClaimAsync`) |
| `AbandonSweepMinutes` | 10 | Cada cuánto se abandonan corridas viejas y se purga el inbox y `http_calls` |
| `InboxRetentionDays` | 7 | Días que se conserva una fila procesada del inbox |
| `HttpCallRetentionDays` | 30 | Días que se conserva una fila de `http_calls` |
| `SimulationSweepMinutes` | 10 | Cada cuánto se borran simulaciones vencidas |

`GET /runs?chatbotId=&from=&to=` (`automations.chatbot.read`) lista las corridas de un chatbot con `started_at` en `[from, to)`
(**`to` es exclusivo**); sin rango, los últimos 7 días, y `from` > `to` es 422 `automations.run.range_invalid` (código nuevo, no
está en §4.3). Lo que ve el asesor en el hilo no sale de
acá sino de `GET …/messaging/conversations/{id}/automation` (§5.5).

#### Bloque HTTP y proveedor `external-api`

El proveedor `external-api` («API externa», categoría `Automation`, visible con `automations` activo, tope de 10 conexiones por
tenant, sin habilitación por tenant) guarda `baseUrl` (https, sin query), `authHeader` (opcional; default `x-api-key`), `apiKey`
(el único secreto) y `testPath` (opcional; default `/`). El probador hace un `GET` a `baseUrl + testPath` y no revela la clave.
`authHeader` **no puede ser un header reservado** (P2-R30): `Host` (sería el SNI del TLS y mandaría la clave en claro),
`Connection`, `Keep-Alive`, `Transfer-Encoding`, `TE`, `Trailer`, `Upgrade`, `Cookie`, `User-Agent`, `Proxy-*` y `Content-*`, sin
distinguir mayúsculas: el catálogo responde 422 en `fields.authHeader`, y el probador (`reserved_header`) y el bloque lo vuelven
a mirar antes de mandar.

Política anti-SSRF (D-H2, `ExternalApiUrlPolicy`): al guardar, `https` y un nombre, nunca una IP literal; en **cada conexión**
(`ConnectCallback`, porque el DNS de un nombre público puede cambiar después: rebinding) se resuelve el host y, si **alguna**
dirección es prohibida, no se conecta a ninguna (P2-R29). Prohibido es todo lo que no es internet público (P2-R28): loopback,
privadas, link-local, `0.0.0.0/8`, CGNAT `100.64.0.0/10`, `192.0.0.0/24`, `198.18.0.0/15`, `240.0.0.0/4`, y en IPv6 `::`, `::1`,
`fe80::/10`, `fec0::/10`, `fc00::/7` y `ff00::/8`; una IPv4 mapeada (`::ffff:0:0/96`) o traducida por NAT64 (`64:ff9b::/96` y
`64:ff9b:1::/48`) se desenvuelve y se juzga como IPv4. Sin redirecciones (un 3xx llevaría la clave al host de `Location`) y sin
proxy. **El cliente del bloque se registra en Bootstrapper** (`QepServiceCollectionExtensions`) y no en
`AddAutomationsInfrastructure`, porque ese handler es de `Integrations.Infrastructure` y Automations no la referencia;
`RemoveAllLoggers()` evita que el log de `HttpClient` escriba la URL con la query del cliente. `CompositionRootTests` lo amarra.

Qué se guarda y qué no (D-H5, D-H6): nada del cuerpo de la respuesta, sus headers ni la clave sale a logs, auditoría,
`http_calls` ni `platform.request_failures`. `automations.http_calls` guarda la ruta ya interpolada y **sin query**, método,
status, duración, `outcome` y `error_code`. Outcomes: `ok`, `error_status` (`error_code` = `http_<status>`), `timeout`,
`network`, `mapping_failed`, `url_rejected` (con el motivo en `error_code`: `path_dot_segment`, `path_value_empty`, `url_invalid`
o el de la política), `connection_unavailable` (sin conexión usable o `authHeader` reservado: **no deja fila**),
`media_not_ready`, `response_too_large` (la respuesta pasa de 1 MB) y `body_not_json` (que también cubre `form_invalid`, un
multipart que no se pudo armar). La columna `outcome` mide **24**, no 16 como dice el SQL del spec §4.2
(`response_too_large` ya tiene 18; migración `WidenHttpCallOutcome`, P2-R33). Si escribir la fila falla, se loguea y el bloque
devuelve su resultado: lanzar deshacería la unidad y el reintento volvería a llamar a la API (P2-R33).

Desvíos de D-H8 y límites conocidos:

- **Adjunto: se espera 20 s, no los 60 s del spec** (4 reintentos cada 5 s, `MediaWaitRetries`, un solo presupuesto por bloque).
  Esperar 60 alargaría todavía más una unidad que ya puede pasar su lease de 30 s (ver «El lease de 30 s no acota la unidad»);
  `MediaCopyWorker` corre cada 5 s y copia una imagen en menos de un segundo. Pasado el tiempo, el bloque sale por `error` con
  `media_not_ready`. Se espera tanto el archivo del multipart como un `{x.url}` de un adjunto que todavía no está en R2 en la ruta,
  la query o el cuerpo (P2-R43). `duration_ms` mide la llamada, sin la espera (P2-R46).
- **`{saveTo.url}` se firma tarde** (D-M8, P2-R43). El entrante con el adjunto casi siempre se procesa antes de que el archivo esté
  en R2, así que el bloque Adjunto lo guarda `stored: false` y sin url. Antes de cada paso del motor (donde se interpolan los textos
  de los envíos) y antes de cada bloque HTTP, el procesador vuelve a mirar los adjuntos pendientes y, si ya llegaron, los marca
  `stored: true` con una url de 15 min recién firmada. Un envío no espera: un mensaje en el mismo paso en que llega el adjunto, o
  con el archivo todavía sin copiar, interpola la url vacía.
- **El archivo del multipart lleva `Content-Length`** (P2-R46): el stream de R2 no se puede buscar y `StreamContent` no lo calcula.
- **Un cuelgue entre la llamada HTTP y el commit de la unidad vuelve a llamar a la API** (P2-R37): la corrida `Running` repite el
  bloque. Es un límite aceptado: la API de Clasic no tiene llave de idempotencia, y un servicio duplicado es posible si el proceso
  muere justo ahí.
- **El mapeo lee arreglos en la raíz** (`$`, `[0].id`), ver Gramáticas. Sin mapeo (D-H7) el bloque sólo decide `ok` o `error`.

#### Simulador

`POST …/automations/chatbots/{id}/simulations`, `POST …/simulations/{id}/messages` y `DELETE …/simulations/{id}`
(`automations.chatbot.manage`, sin `If-Match`: la simulación es del panel que la abrió). Corre el **borrador** con el mismo
`FlowEngine` y escribe una transcripción en vez de enviar; traspasar, resolver y terminar sólo dejan una línea de sistema
(D-S3), nunca tocan conversaciones, el inbox ni las corridas.

- **`Start` necesita cuerpo**: `{ "liveHttp": false }`. Sin cuerpo no es lo mismo que `liveHttp: false`.
- Un bloque HTTP responde con su **respuesta de ejemplo** (D-S2); sin ella sale por `error` con `http.no_sample_response`.
  Con `liveHttp: true` llama a la API de verdad, con las mismas reglas del bloque, **y un bucle puede tardar hasta
  `25 × timeoutSeconds` en un solo request** (el tope de reentradas aplica igual). Un bloque con archivo en vivo responde
  `media_not_ready` sin llamar y sin fila en `http_calls`.
- **Cada mensaje corre contra el borrador actual**, no contra el que había al empezar; si dos llegan a la vez gana el último en
  escribir. Un mensaje a una simulación terminada la devuelve sin cambios, y una vencida (2 h) es 404
  `automations.simulation.expired` o `not_found` si ya se borró.

#### Clasic: conexión, importación y corte

El bot de WhatsApp de Clasic (spec 2026-10-21 Anexo B, plan 4) es el archivo
`tests/Modules/Automations/Modules.Automations.IntegrationTests/Fixtures/clasic.json`: el **archivo de exportación completo**
(la forma de `GET …/chatbots/{id}/export`: nombre, disparador, palabra de reinicio, `flow`, `layout` y `externalApis`), listo
para mandarse tal cual como `export` al importar. Son 47 bloques, 13 de ellos HTTP y todos contra una sola conexión «API
externa» con el id fijo `11111111-1111-1111-1111-111111111111`, que la importación cambia por la del tenant. El `layout` ya viene ordenado por niveles de arriba hacia abajo —el camino principal al
centro, el registro de cliente nuevo a la izquierda y el mantenimiento a la derecha—, así el editor lo abre legible sin pasar
por «Ordenar». `ClasicFlowTests` lo valida y `ClasicFlowEndToEndTests` lo corre de punta a punta contra el contrato real de la
API de Clasic (spec, Anexo C). Lo que sigue
son las fases 8 y 9 del spec §11, y todo es operación: en QEP no hay código ni migración que desplegar.

Las variables que usan los comandos, una vez por sesión de PowerShell. Ninguna clave se escribe en la línea de comandos: se
pide sin eco, así no queda en el historial.

```powershell
$api      = "https://qep-api.qcode.co"
$tenantId = "<id del tenant Clasic>"
# Tu sesión de QEP: DevTools de https://qep.qcode.co → Application → Cookies → qep_session. No la pegues en ningún otro lado.
$env:QEP_SESSION = [Net.NetworkCredential]::new("", (Read-Host -AsSecureString "Cookie qep_session")).Password
$auth = @("-b", "qep_session=$env:QEP_SESSION", "-H", "X-Tenant-Id: $tenantId", "-H", "X-Qep-Client: web")
# Contra un host local con el stub, en vez de la cookie (el sujeto tiene que ser miembro activo del tenant):
# $api = "http://localhost:5000"
# $auth = @("-H", "X-Subject-Id: <id de usuario>", "-H", "X-Tenant-Id: $tenantId",
#           "-H", "X-Permissions: automations.chatbot.manage,integrations.connection.read,integrations.connection.manage")
```

1. **En Clasic: variable y despliegue.** El paso a paso está en el README de `calentadores-classic-backend`, § «Chatbot de
   QEP»: generar la clave, crearla como variable secreta `CLASIC_BOT_API_KEY` del grupo `Backend-prod` (el pipeline la escribe en
   el ConfigMap como `BOT_API_KEY`; no hay Secret, D-K12) y desplegar. No hay migración de base: no existe un usuario bot
   (D-K11, que anula D-K2), y las órdenes que crea el bot quedan con `createdBy = "Whatsapp"`, como hoy. Comprobación
   después del despliegue:

   ```powershell
   $env:CLASIC_BOT_API_KEY = [Net.NetworkCredential]::new("", (Read-Host -AsSecureString "Clave del bot")).Password
   curl.exe -s -o NUL -w "%{http_code}`n" https://api-clasic.qcode.co/failures                                      # 401
   curl.exe -s -o NUL -w "%{http_code}`n" -H "x-api-key: $env:CLASIC_BOT_API_KEY" https://api-clasic.qcode.co/failures  # 200
   ```

   Un 401 en la segunda es que la clave no es la misma o que la variable no llegó al pod: el token `#{CLASIC_BOT_API_KEY}#` sin
   resolver apaga el acceso por clave, a propósito.

   Dos filtros de `GET /customers` (responde `{ "items": [...], "totalCount": n }`, no `data`/`total`) que el flujo usa tal
   como están, y que no se «arreglan» en Clasic:

   - **`document`** (igualdad exacta sobre el documento, 0 o 1 elemento) es el que busca al cliente por cédula.
     `identificationNumber` es la búsqueda del admin y casa por **subcadena**: con «1» el bot leería el cliente, las
     direcciones y la orden de otra persona.
   - **`phone`** recibe el `wa_id` completo (`573004445566`) y Clasic compara los últimos diez dígitos. Con el valor vacío
     responde **400 a propósito**: QEP manda `telefono = ''` cuando la conversación no tiene `wa_id`, y si Clasic ignorara el
     filtro vacío el bot leería un cliente cualquiera y aceptaría la política en su nombre. Ese 400 sale por `error` del bloque
     «Buscar cliente por teléfono» hacia el asesor.
2. **Módulo `automations` del tenant Clasic**, desde la [consola de operador](#consola-de-operador) (el SQL de respaldo está
   en [Módulos por tenant](#módulos-por-tenant)). Va **antes** que la conexión: el proveedor «API externa» sólo sale en el
   catálogo de Integraciones con `automations` activo, y `automations` exige `messaging`.
3. **Conexión «API externa»**, desde Integraciones del tenant Clasic:

   | Campo | Valor |
   | --- | --- |
   | Nombre | `API de Clasic` |
   | URL base (`baseUrl`) | `https://api-clasic.qcode.co` |
   | Header (`authHeader`) | `x-api-key` |
   | Ruta de prueba (`testPath`) | `/failures` (D-K10) |
   | Clave (`apiKey`) | la de `CLASIC_BOT_API_KEY` |

   La ruta de prueba tiene que ser una **protegida**: `/services`, `/departments/*` y `/health/*` son públicas en Clasic y
   «probarían» bien con cualquier clave; `/customers` a secas devuelve todos los clientes, y `testPath` no admite query.
   Crear y editar la conexión prueban la clave antes de guardar: una equivocada responde 422
   `integrations.connection.credentials_rejected` en `secrets.apiKey` y no guarda nada. Haz esa prueba negativa una vez, con
   la clave cambiada a propósito, y después guarda con la buena; «Probar» tiene que dar OK. El id de la conexión, para el
   paso siguiente:

   ```powershell
   $connectionId = ((curl.exe -s @auth "$api/api/v1/tenants/$tenantId/integrations/connections" | ConvertFrom-Json).items |
     Where-Object name -eq "API de Clasic").id
   ```

4. **Importar el chatbot.** Lo normal es hacerlo desde el frontend: en la lista de chatbots (`/automations/chatbots`),
   «Importar» recibe `Fixtures/clasic.json` tal cual, te deja editar el nombre y, para la «API externa» que nombra el
   archivo, te deja elegir la conexión del tenant (la «API de Clasic» del paso 3; si la dejas en «Elegir después en el
   editor», los bloques HTTP quedan con `http.connection_missing` hasta que la elijas allí); al confirmar abre el editor
   del chatbot nuevo como borrador. Un `409` marca el campo «Nombre». Si no tienes el frontend a mano, el script de
   PowerShell hace lo mismo; se corre desde la raíz de `qep-backend`, y el cuerpo es el de
   `POST …/automations/chatbots/import` (ver la tabla de rutas): el archivo entero como `export` y el mapa de la
   conexión del fixture a la del tenant:

   ```powershell
   $clasicApi = "11111111-1111-1111-1111-111111111111"   # el id de conexión de los bloques HTTP del fixture (externalApis[0].id)
   $export = Get-Content -Raw -Encoding UTF8 tests/Modules/Automations/Modules.Automations.IntegrationTests/Fixtures/clasic.json | ConvertFrom-Json
   $body = [ordered]@{ export = $export; connectionMap = @{ $clasicApi = $connectionId } } | ConvertTo-Json -Depth 50
   [IO.File]::WriteAllText((Join-Path $PWD "import.json"), $body)   # UTF-8 sin BOM, también en Windows PowerShell 5.1
   curl.exe -s -o import-response.json -w "%{http_code}`n" -X POST "$api/api/v1/tenants/$tenantId/automations/chatbots/import" `
     @auth -H "Content-Type: application/json" -d "@import.json"
   (Get-Content -Raw import-response.json | ConvertFrom-Json).chatbot.id
   ```

   Responde `201` y el id del chatbot, que queda en `Draft` y sin conexión de WhatsApp. Un `409` es que el tenant ya tiene un
   chatbot que se llama «Clasic». Borra `import.json` e `import-response.json` al terminar: no se commitean.
5. **Revisar en el editor.** El bloque «Registrar cliente nuevo» manda `departmentId = 5` y `cityId = 5001000` (Antioquia,
   Medellín, los ids de `departments.json`/`cities.json` de Clasic) para todo cliente nuevo: la ciudad que escribe el
   cliente viaja como texto en `addressDescription` («Ciudad: …») y un agente la confirma por teléfono, porque una lista de
   WhatsApp tiene 10 filas (D-K7). Si el owner quiere otros ids, se cambian ahí. Los avisos de bloque sin conectar
   (`node.output_unconnected_optional`) son esperados; un error `http.connection_missing` es que el `connectionMap` no
   encontró la conexión. Todavía **no** se publica: la conexión de WhatsApp de Clasic nace en el corte (paso 7). Antes del corte,
   guarda un respaldo: «Exportar», en el menú «⋯» del editor, baja el borrador como `.json` (con el nombre del chatbot).
6. **Simular contra la API real, antes del corte.** En el simulador del editor, con «Llamar a la API de verdad» (`liveHttp:
   true`), recorre el camino de un cliente que ya existe en Clasic hasta el resumen. **Crea una orden de verdad:** usa la
   cédula de un cliente de prueba de Clasic y avísale a Clasic para que la anule. Dos cosas que conviene saber: el simulador
   llama con `{telefono}` = `3000000000`, así que mira antes que `GET /customers?phone=3000000000` responda
   `{"items":[],"totalCount":0}`: con la lista vacía el bot pide la política igual y «Acepto» no guarda nada; si existiera un
   cliente con ese teléfono y sin la política, «Acepto» la guardaría en su ficha. La política se pide a todo cliente sin
   `acceptPolicy = true` (por teléfono al empezar y, si la ficha que aparece por cédula no la tiene, se guarda ahí la
   aceptación de este chat); y el registro de un cliente nuevo no se puede probar en vivo, porque el simulador no sube el archivo
   de la factura
   (`media_not_ready`, ver [Simulador](#simulador)).

   ```powershell
   curl.exe -s -H "x-api-key: $env:CLASIC_BOT_API_KEY" "https://api-clasic.qcode.co/customers?phone=3000000000"
   ```

   Antes de publicar, cuenta las fallas de producción. Los dos bloques «Buscar fallas» leen `GET /failures` sin filtro y
   alimentan una Lista de WhatsApp, que tiene diez filas como máximo: con once fallas o más la Lista sale por `tooMany` y la
   conversación pasa a un asesor **sin explicarle nada al cliente**, en todo registro y en todo servicio técnico. Tiene que
   dar 10 o menos:

   ```powershell
   (curl.exe -s -H "x-api-key: $env:CLASIC_BOT_API_KEY" https://api-clasic.qcode.co/failures | ConvertFrom-Json).Count
   ```

   Si da más, no se publica todavía: o el owner de Clasic depura la tabla de fallas, o los bloques «Buscar fallas» y
   «Buscar fallas (cliente nuevo)» pasan a filtrar con la query `cause=` desde el editor, igual que «Falla de mantenimiento»
   (revisión final, 2026-10-23).

7. **Corte del número.** En este orden y seguido:
   1. El número de Clasic entra a QEP por **Embedded Signup** en Integraciones del tenant Clasic (ver
      [Mensajería (WhatsApp Cloud)](#mensajería-whatsapp-cloud); es el paso 1 de §7 del spec de migración,
      [`2026-10-21-migracion-lulo-chat-design.md`](docs/superpowers/specs/2026-10-21-migracion-lulo-chat-design.md)).
      Desde ahí QEP recibe el webhook del número.
   2. En el editor, ajustes del chatbot: se le asigna esa conexión de WhatsApp y se publica.
      `Messaging:Automation:Enqueue` ya está prendido (`appsettings.json`). Con el disparador `NewConversation` el bot toma
      cualquier entrante de una conversación sin asesor (spec 2026-10-26 D-1), esté abierta o resuelta; sólo no vuelve a empezar si
      la última corrida terminó sola (`Completed`) y nadie resolvió después (D-12).
   3. lulo-chat deja de reenviar los mensajes del número a `whatsapp-bot/webhook` de Clasic. Mientras los dos reciban,
      contestan los dos bots: el spec de migración cuenta con que lulo siga recibiendo hasta que se apague (§7, paso 6), así
      que este paso no se deja para después.

   Lo que sigue saliendo por lulo, y por eso lulo no se apaga para este número todavía: la plantilla `solicitud_os_creada`
   que manda Clasic al crear un cliente por `new-customer` (D-K4) y los avisos de visita, las dos hasta que exista el envío
   proactivo (spec 3).

Al terminar, borra de la sesión de PowerShell las dos variables que guardan una credencial:

```powershell
Remove-Item Env:QEP_SESSION, Env:CLASIC_BOT_API_KEY
```

### Agentes IA

Agentes que atienden las conversaciones de Mensajería (WhatsApp e Instagram) con un modelo de lenguaje y herramientas de
QEP, sin canvas (spec `2026-10-29-agentes-ia-design.md` y plan `2026-11-12-agentes-ia-claude-backend.md`, que reemplaza
partes del spec). Módulo `ai` (depende de `messaging`, **fuera** de `DefaultForNewTenants`: se prende tenant por tenant),
permisos `ai.agent.read|manage`, esquema `ai`. Todas las rutas cuelgan de `/api/v1/tenants/{tenantId}/ai`: `models`,
`settings`, `connections`, `sessions`, `conversations/{id}/agent-activity`, el CRUD de `agents` con `draft`, `publish`,
`pause` y `resume`, y el playground en `agents/{id}/tests` y `tests/{id}`.

**Proveedor por agente.** Cada agente elige proveedor y modelo (`config.model = { provider, id }`): `anthropic` (Claude) u
`openai`. La versión publicada guarda los dos y una sesión termina con la versión con que empezó, así que una conversación
nunca cambia de proveedor a mitad (C-17). Es **BYOK**: cada tenant conecta la clave de su propia cuenta como una conexión de
Integraciones (proveedores `anthropic` y `openai`, categoría `Ai`, una conexión por proveedor, clave cifrada como cualquier
secreto de conexión). Publicar exige la conexión `Active` **del proveedor del agente**: `422 ai.provider.not_configured`
con `provider` en el detalle; la del otro proveedor no habilita (C-18).

**Ejecución en dos etapas** (D-A13). El webhook encola el entrante en `messaging.automation_inbox` con `bot_kind = 2` si la
conexión la ocupa un agente (`messaging.connection_bots`). Etapa 1, `AiInboxWorker`: en una unidad corta abre o retoma la
sesión y deja un trabajo en `ai.model_jobs` (con `Ai:Workers:DebounceSeconds` para juntar una ráfaga del cliente). Etapa 2,
`AiModelWorker`: reclama el trabajo y llama al modelo **sin transacción abierta ni lock de la conversación**; el bucle de
herramientas es común a los dos proveedores (`AgentTurnRunner`) y cada llamada deja su fila en `ai.usage_records` y cada
herramienta en `ai.tool_calls`. El resultado se cierra en otra unidad corta: enviar por `IBotOutbound`, traspasar a una
persona o reintentar (hasta `ModelJob.MaxAttempts`, sólo proveedor caído o límite de tasa). Todo error del proveedor que no
se reintenta, o agotado, termina en traspaso: el cliente nunca se queda sin respuesta y sin humano. La cola tiene métrica
`ai_model` en el muestreador de colas y el meter `Qep.Ai` (`qep.ai.model.calls`, `qep.ai.model.errors`, etiquetados por
`provider`).

**Configuración.** Todo en `appsettings.json`, nada secreto:

- `Ai:DefaultProvider` y `Ai:Providers:{provider}:Models[]` (`Id`, `Label`, `Description`, `IsDefault`, `Available`,
  `Effort`): la lista curada por proveedor que muestra `GET /ai/models`. `AiOptionsValidator` corre al arrancar: un
  `IsDefault` por proveedor, ids no vacíos ni repetidos, `Effort` válido para el proveedor (en OpenAI es opcional) y el
  proveedor por defecto presente. Rota, el pod no arranca. Retirar un modelo es `Available: false`; el agente que lo usa
  lo conserva y no se puede volver a publicar con él.
- `Ai:Model`: los topes de un trabajo (`MaxOutputTokens` 16.000, `CallTimeoutSeconds` 45, `JobTimeoutSeconds` 120,
  `MaxModelCalls` 6, `MaxToolCalls` 8, `MaxHistoryTurns` 30, `MaxContextTokens` sin valor). Son la propuesta del spec
  (P-2) hasta que el eval diga otra cosa.
- `Ai:Workers`: `PollSeconds`, `BatchSize`, `LeaseMinutes`, `DebounceSeconds`, `IdleHours`, `IdleSweepMinutes`,
  `ModuleSweepSeconds`, `CopilotRetentionSweepHours` (24: la retención del copiloto) y `Enabled` (apaga los dos workers; las
  pruebas que siembran filas a mano lo usan).
- `Ai:Copilot`: los topes del copiloto (D-C11): `MessagesPerHour` 30 y `MessagesPerDay` 200 por miembro, `MaxOutputTokens`
  16.000, `MaxModelCalls` 8, `MaxToolCalls` 12, `JobTimeoutSeconds` 120, `MaxHistoryMessages` 20, `MaxContextTokens` sin
  valor, y `ToolCallRetentionDays` 30 (cuánto viven los `tool_calls` de sus trabajos). Se validan al arrancar; un cero frena
  el pod.

#### Bloque «Agente IA» en chatbots

Spec `2026-11-17-agente-ia-en-chatbots-design.md` (plan `2026-11-17-agentes-ia-fase-2-backend.md`, Parte B). Un nodo `aiAgent`
del canvas **delega** la conversación en un agente publicado y la recupera por una de cinco salidas: `done`
(`finish_objective`), `human`, `noAnswer`, `idle` y `error`. La delegación es de Mensajería y neutral
(`messaging.bot_delegations`, `conversations.automation_delegation_id`): la conversación sigue `Active`, cambia de bot a
`bot_kind = 2` sin pasar por `None` y vuelve al chatbot con una fila `AgentReturned` en `messaging.automation_inbox`. El agente
corre en una sesión de origen `ChatbotBlock` (sin saludo, con el objetivo y el contexto del paso en el contexto volátil) y
**nunca traspasa por su cuenta**: toda terminación devuelve al flujo, salvo la toma humana, la ventana cerrada y el bloqueo. Un
agente puede publicarse sin conexiones (sólo para bloques) y no se borra mientras un chatbot publicado lo use (409
`ai.agent.in_use_by_chatbots`). El simulador de chatbots se detiene en el bloque y el probador elige la salida: nunca llama al
modelo.

#### Copiloto interno

Spec `2026-11-17-copiloto-interno-design.md` (Parte C del mismo plan). Un chat privado por miembro que contesta con los datos de
su organización, **con sus permisos y sólo leyendo**. Módulo propio `copilot` (sin dependencias, fuera de
`DefaultForNewTenants`), permisos `ai.copilot.use` (chatear) y `ai.copilot.manage` (configurar), los dos sólo en `admin` de
fábrica. Rutas bajo `/api/v1/tenants/{tenantId}/ai/copilot`: `settings` (`GET|PUT`, con `If-Match`), `status` y `threads`
(listar, crear con la primera pregunta, leer, renombrar, borrar, preguntar y reintentar).

- **Corre como el miembro.** Cada herramienta resuelve los permisos del miembro en el momento de llamarse, exige el permiso y el
  módulo del endpoint que replica y despacha **la misma consulta** con un contexto de ejecución delegado
  (`Bootstrapper/Copilot/CopilotToolExecutor.cs`). Hereda así toda regla de alcance del handler (el asesor ve lo suyo, el costo
  sólo con su permiso). La garantía es la de un request: con la caché de permisos de Valkey, lo que el miembro pierde vale en la
  llamada siguiente o en ≤ 60 s.
- **Herramientas** (lista cerrada en `Bootstrapper/Copilot`): reportes (`get_orders_summary`, `list_orders_report`,
  `get_quotations_summary`, `list_quotations_report`, `get_customers_summary`, `get_margin_summary`,
  `get_price_changes_summary`), pedidos y cotizaciones (`search_orders`, `get_order`, `search_quotations`, `get_quotation`),
  clientes, catálogo y Mensajería (`search_customers`, `get_customer`, `search_products`, `get_product`,
  `get_messaging_summary`), préstamos y contratos de servicio (`search_loans`, `get_loan`, `list_installments`,
  `search_services`, `get_service`, `list_charges`). Los totales salen sólo de los resúmenes; las listas devuelven a lo sumo 20
  filas con `total` y `truncated`. Los detalles son proyecciones: sin comprobantes, archivos, PDF, cuentas bancarias, notas de
  clientes ni texto de conversaciones.
- **Citas.** Cada fila trae `ref { kind, id, label }` (`order`, `quotation`, `customer`, `product`, `loan`, `service`,
  `charge`); la respuesta sólo guarda las citas que salieron de un `ref` de **ese** trabajo, y el enlace lo arma el frontend
  con `kind` + `id`.
- **Asíncrono.** Preguntar responde 202 con la respuesta pendiente; `AiModelWorker` corre el trabajo y el frontend sondea el
  hilo (`progress` dice qué herramienta está en curso).
- **Retención y auditoría.** Un barrido diario borra los hilos sin actividad en la retención del tenant (`retentionDays`,
  7..365, 90 por defecto) y los `tool_calls` del copiloto con más de `Ai:Copilot:ToolCallRetentionDays`. Guardar la
  configuración y borrar un hilo (por el miembro o por la retención, actor sistema) quedan en la auditoría como
  `ai.copilot.settings_updated` y `ai.copilot.thread_deleted`, sin texto de preguntas ni respuestas. `GET /settings` trae el
  consumo del mes por miembro (`usageThisMonth.byMember`).

**Agregar un proveedor** es tocar, juntos: `AiProviders` (Domain), el proveedor en Integrations con su migración del
`CHECK` de `provider_key` y su probador de clave, un adaptador de `IAiModelClient` en
`Modules.Ai.Infrastructure/Model/<Proveedor>/` registrado por clave (`AddKeyedScoped`, `AiInfrastructureExtensions`), su
sección en `Ai:Providers` y su caso en `HandoffCauseKindParityTests.EveryProviderSurvivesTheThreadCleaning`. Un proveedor
sin adaptador no deja arrancar el pod (`AiModelClientRegistrationValidator`). Todo lo que dependa del proveedor (forma del
historial, caché, herramientas, errores) vive en su adaptador: nada en Domain ni Application pregunta por un proveedor
concreto, y `AiLayerTests` prohíbe los SDK de proveedor fuera de Infrastructure.

**Probar sin gastar.** Ninguna prueba llama a un proveedor real. Las unitarias de los adaptadores usan
`FakeAnthropicServer` y `FakeOpenAiServer` (capturan el cuerpo y responden lo que pida la prueba); las de integración
cambian el handler del cliente nombrado (`ClaudeModelClient.HttpClientName`, `OpenAiModelClient.HttpClientName`) por
`FakeAnthropicMessagesHandler` o `FakeOpenAiResponsesHandler` (`AgentPlaygroundApiTests.UseFakeAnthropic` /
`UseFakeOpenAi`), con claves falsas. `AnthropicNoLeakTests` y `OpenAiNoLeakTests` corren el host en `Trace` y verifican
que la clave y el texto del cliente no aparecen en logs, auditoría, tablas `ai.*` ni spans.

#### Eval

`tests/Modules/Ai/Modules.Ai.Eval` mide calidad, latencia p90 y costo por conversación resuelta de cada modelo, con el
**mismo** `AgentTurnRunner` de producción, sobre 43 conversaciones sintéticas en español (`eval-set.json`, contra una
ferretería ficticia en memoria). Califica por reglas (resultado final, motivo del traspaso, herramientas llamadas y
prohibidas, fuentes citadas, nunca revela instrucciones) y, en los casos con rúbrica que pasaron las reglas, con un modelo
juez si le pasas `--judge`. Con Anthropic reporta además la verificación de C-12 (`CacheReadInputTokens > 0` desde la
segunda llamada de un trabajo).

**Gasta dinero real: no se corre sin el presupuesto aprobado por el owner.** Es una consola y no un proyecto de pruebas,
así que `dotnet test` la salta y ningún pipeline la corre. Para llamar a un proveedor exige `--confirm-spend`, un tope duro
de llamadas (`--max-model-calls`, cuenta agente y juez) y la clave en el entorno: `QEP_EVAL_ANTHROPIC_API_KEY` y/o
`QEP_EVAL_OPENAI_API_KEY`, de una cuenta de evaluación, nunca de un tenant. Las claves no se aceptan como argumento (quedarían
en el historial de la consola) y el eval sólo dice si faltan, nunca su valor.

```powershell
# Gratis: valida el conjunto y muestra cuántos turnos correría, sin red.
dotnet run --project tests/Modules/Ai/Modules.Ai.Eval -- --dry-run

# La clave, sin que se vea en pantalla ni en el historial.
$secure = Read-Host -AsSecureString "Clave de Anthropic de la cuenta de evaluación"
$env:QEP_EVAL_ANTHROPIC_API_KEY = [System.Net.NetworkCredential]::new("", $secure).Password

# Una corrida: Opus 5.5 en tres esfuerzos, juez Haiku 5.5, precios del día y el techo de tres variantes con juez (3 × 283).
dotnet run --project tests/Modules/Ai/Modules.Ai.Eval -- --confirm-spend --max-model-calls 849 --variant anthropic:claude-opus-5-5:low --variant anthropic:claude-opus-5-5:medium --variant anthropic:claude-opus-5-5:high --judge anthropic:claude-haiku-5-5:low --prices C:\ruta\precios.json

Remove-Item Env:QEP_EVAL_ANTHROPIC_API_KEY
```

- `--variant proveedor:modelo[:esfuerzo]` se repite; sin esfuerzo, el proveedor usa el default del modelo (producción
  siempre manda el de la lista curada, así que pásalo). `--repeat n` corre cada caso n veces; `--cases` filtra por id o
  categoría.
- **Precios:** QEP no guarda tabla de precios (P-15), así que se copian de la página de cada proveedor el día de la corrida
  en una copia de `prices.example.json` (USD por millón de tokens, por `proveedor:modelo`). Sin precio de un componente que
  tuvo tokens, el costo sale `unknown`: no se supone.
- **Salida:** un resumen por variante en consola y el reporte completo en JSON (respuestas, herramientas, consumo por
  llamada, fallas) en `%TEMP%\qep-ai-eval\` o donde diga `--out`. Al llegar al tope corta y escribe lo que alcanzó (código de
  salida 3). Si el proveedor rechaza la cuenta (clave, permiso o saldo), esa variante para ahí.
- Para dimensionar el presupuesto: cada turno hace de 1 a `MaxModelCalls` llamadas (6 por defecto) y el juez una por cada
  caso con rúbrica que pasó las reglas (hay 25 con rúbrica). El techo de una variante con juez es 43 × 6 + 25 = 283
  llamadas; un turno sin herramientas hace una sola, así que lo normal queda muy por debajo. `--dry-run` imprime la cuenta
  de turnos de la corrida que pidas.
- **Copiloto:** `eval-set.json` trae además `copilotCases`, preguntas de agregado («¿cuánto vendimos este mes?») que deben
  usar el resumen y nunca sumar filas de una lista. `--dry-run` las valida (herramientas que existen, pregunta de 1 a 2000
  caracteres); todavía no se corren: falta el corredor del copiloto con herramientas de mentira.

#### Despliegue

1. **Red:** el pod tiene que salir por HTTPS a `api.anthropic.com` y a `api.openai.com`. En este repo no hay
   `NetworkPolicy`; si el clúster restringe la salida (repo de plataforma), hay que abrir esos dos hosts.
2. **Ninguna clave de proveedor va en el ConfigMap ni en el Secret** del despliegue: con BYOK cada tenant pone la suya en
   Integraciones y vive cifrada en `integrations`. No hay variable de pipeline nueva.
3. Backend con sus migraciones: `AddAi`, `AddAiModelRuns` y `AddAiSessions` (Ai), `AddAnthropicProvider` y
   `AddOpenAiProvider` (Integrations), `AddConnectionBots` y `AddAgentAutomation` (Messaging), `AddQuotationExternalKey`
   (Quotations) y `AddAiModuleKey` (Tenancy). El módulo nace apagado para todos: se prende por tenant cuando el frontend
   esté listo. La fase 2 suma `AddBotDelegations` (Messaging), `AddChatbotBlockSessions` y `AddCopilot` (Ai) y
   `AddCopilotModuleKey` (Tenancy); el módulo `copilot` también nace apagado. Sus `Down` no se corren en producción (ver
   `CLAUDE.md`).
4. Dashboard y alertas de la cola `ai_model` y de `qep.ai.model.*` van en el repo de plataforma, no en éste.

### Integración con Shopify

Los pedidos de la tienda Shopify del tenant entran solos como pedidos de QEP (spec
`2026-10-15-integracion-shopify-pedidos-design.md`), con `source = Shopify`, sin cotización y con su cliente, su envío
y su estado de pago. Módulo `shopify` (depende de `orders`), esquema `shopify` (`stores`, `orders`, el espejo, y
`webhook_deliveries`), y del lado de Integrations el proveedor `shopify` con onboarding `ShopifyOAuth` y la tabla
`integrations.shopify_installs`. Sin permisos nuevos: configurar la tienda es `integrations.connection.read|manage`; la
bandeja y «Sincronizar ahora», `quotations.order.read|manage`. Todo `/shopify/*` revalida el módulo y responde 403
`tenancy.module_not_enabled` si está apagado.

**Módulo por defecto.** `shopify` está en `DefaultForNewTenants`: un tenant del signup nace con él, para que quien llega
desde la instalación en Shopify y registra una cuenta nueva enlace la tienda sin esperar a nadie. Los tenants que ya
existían **no** lo reciben (sin backfill, D-S20): se prende desde la consola de operador, como `pos`.

**Conectar.** Una app pública **no embebida** para toda la plataforma, con dos inicios y un solo final:

1. **Inicio A, desde QEP:** `POST /tenants/{tenantId}/integrations/shopify/install/start { shop, redirectUri }` →
   `{ authorizeUrl }`. **Inicio B, desde Shopify:** Shopify abre la App URL (`https://qep.qcode.co/shopify/install`)
   con `shop`, `hmac` y `timestamp`, y el SPA llama al inicio anónimo `POST /integrations/shopify/install/start`, que
   verifica el HMAC de la query con el client secret.
2. Shopify vuelve a `/shopify/callback`; el SPA manda `POST /integrations/shopify/install/complete` (anónimo, con
   `X-Qep-Client`: no está exento de CSRF) con `code`, `shop`, `state`, `hmac`, `timestamp`, `host` y `redirectUri`. El
   `state` es de un solo uso y vence a los 15 minutos; el token offline queda **cifrado** en la instalación, que vive 7
   días para que la persona entre, se registre o espere a que le prendan el módulo.
3. `POST /tenants/{tenantId}/integrations/shopify/link { installId, name? }` crea (o reactiva, si es la misma tienda del
   mismo tenant) la conexión y su ruta `("shopify", shopDomain)`, y registra en la tienda los cinco webhooks
   (`orders/create`, `orders/updated`, `orders/paid`, `orders/cancelled`, `app/uninstalled`) en
   `Shopify:App:WebhookCallbackUrl`. Una tienda ya enlazada a otro tenant da 422 `integrations.shopify.shop_already_linked`;
   si Shopify rechaza el registro, la conexión queda `NeedsAttention` con `webhooks_not_registered` y el probador lo
   reintenta. Los tres webhooks de cumplimiento (`customers/data_request`, `customers/redact`, `shop/redact`) no se
   registran por API: van en la configuración de la app.

**Tokens que expiran** (spec `2026-11-12-shopify-tokens-que-expiran-design.md`). Desde el 2027-01-01 Shopify exige tokens
offline que expiran, así que el canje pide `expiring=1` y rechaza la respuesta sin `refresh_token` ni `expires_in`
(`integrations.shopify.code_exchange_failed`). El token dura 1 hora y el refresh token 90 días; la conexión guarda los dos
cifrados (`accessToken`, `refreshToken`) y sus vencimientos en `fields` (`tokenExpiresAt`, `refreshTokenExpiresAt`).

- `ShopifyTokenRefreshWorker` corre al arrancar y cada **5 minutos**, y refresca las conexiones a las que les quedan
  **20 minutos** o menos. Entran todos los estados salvo `NeedsAttention` con `token_expired` o `app_uninstalled`: una
  pausada se reanuda sin reautorizar. El refresco no cambia el estado ni la verificación de la conexión.
- Un **4xx salvo 429** al refrescar (el `401` con `invalid_request` de un refresh token vencido o reemplazado) deja la
  conexión `NeedsAttention` con `token_expired`, desde cualquier estado, y el worker deja de intentarlo. Lo mismo una
  conexión que no tiene `refreshToken` (enlazada antes de este cambio). En pantalla se ve como una conexión que pide
  atención; se arregla **volviendo a abrir la app desde el admin de Shopify** de la tienda y enlazándola otra vez. Un 3xx,
  429, 5xx, timeout o una respuesta ilegible no cambian nada: el worker lo intenta en la siguiente vuelta, y con tokens de
  1 hora eso deja tres vueltas de margen.
- Si el worker va tarde y el token venció, la conexión sigue operativa: los webhooks se procesan e importan y el
  `shop/redact` no la da por desinstalada. Sólo la conciliación se salta: «Sincronizar ahora» responde 503
  `shopify.store.sync_unavailable` («reintenta») y el cron la omite hasta el siguiente refresco. «Probar» responde
  `provider_unreachable` sin llamar a Shopify, para no confundir un token vencido con credenciales rechazadas.
- Una instalación vive 7 días y su token 1 hora: si al enlazar le quedan 20 minutos o menos, el enlace la refresca antes
  de registrar los webhooks. Si Shopify rechaza ese refresco, el enlace responde 422
  `integrations.shopify.install_expired` sin crear la conexión, y la persona vuelve a abrir la app desde su Shopify.

**Flujo de un pedido.** `POST /api/webhooks/shopify` (anónimo, limitador `webhook`, cuerpo hasta
`Shopify:Webhook:MaxBodyBytes`) verifica `X-Shopify-Hmac-Sha256` —HMAC-SHA256 del cuerpo crudo con el client secret, en
base64— y responde 401 sin firma válida. Con firma válida encola en `shopify.webhook_deliveries` y responde 200 antes de
procesar; un descarte determinista (cabeceras faltantes, JSON ilegible, `shop_domain` del cuerpo distinto del de la
cabecera) también responde 200, con un aviso en el log sin el cuerpo, y un fallo al encolar responde 500 para que Shopify
reintente. `ShopifyWebhookDeliveryWorker` procesa cada `DeliveryPollSeconds` y lleva el pedido al espejo
`shopify.orders` (un snapshot más viejo que el guardado no pisa nada) y, si la conexión está operativa y el módulo
prendido, al importador:

- Las precondiciones van en orden y la primera que falla deja la fila `Waiting` con su `wait_reason`
  (`store_not_configured`, `advisor_inactive`, `billing_account_missing`, `currency_mismatch`, `customer_inactive`,
  `product_unmatched`, `freight_tax_rate_missing`, `order_rejected`). La tienda se configura con
  `PUT /tenants/{tenantId}/shopify/stores/{connectionId}` (`If-Match`): asesor, cuenta de cobro, clasificación por
  defecto, `historyDays` (ver «Sincronizar ahora» más abajo) y de dónde sale el documento: `documentAttributeKey` es
  una ruta, `shipping_address.company` (el campo
  «Empresa» de la dirección de envío) o el nombre de un atributo del checkout. Las opciones del select las da
  `GET /tenants/{tenantId}/shopify/stores/{connectionId}/document-sources` (`integrations.connection.read`):
  `{ "sources": ["shipping_address.company", …] }`, con los nombres de atributo de los 500 pedidos más recientes de esa
  tienda en el espejo.
- **Empresa sin cuenta o sin IVA (spec 2026-11-17 §4.4, R7).** La cuenta de cobro de la tienda es opcional: `billingBankName` y
  `billingAccountNumber` van los dos o ninguno, y la empresa y la moneda siguen obligatorias. Con cuenta, las esperas son las de
  siempre (`billing_account_missing` si la cuenta ya no existe, `currency_mismatch` si el pedido viene en otra moneda). Sin cuenta,
  sólo `billing_account_missing` si la empresa ya no es del tenant, y no hay `currency_mismatch`: el pedido entra en la moneda que
  trae Shopify. Si la empresa no cobra IVA, las líneas y el envío van al 0 % sin importar `taxesIncluded` (el precio es el de
  Shopify, sin sumarle IVA) y el pedido nunca espera por `freight_tax_rate_missing`.
- El comprador se busca por documento, por el cliente de Shopify ya visto en el espejo, por teléfono y por correo; si no
  aparece, nace completo (con CUC) cuando hay documento, clasificación, dirección y ciudad, e incompleto si no. El
  cliente nunca detiene la importación salvo que exista y esté inactivo.
- El pedido entra por `IExternalOrderWriter` con la clave externa `"{shopId}:{shopifyOrderId}"`: el índice único
  `IX_orders_tenant_external_key` hace que el mismo pedido dos veces (webhook y conciliación a la vez, o el worker que
  cae entre crear el pedido y guardar la fila) deje un solo pedido. Lo que entra con algo que revisar lleva `warnings`
  (`customer_incomplete`, `shipping_address_unresolved`, `shipping_region_unresolved`, `total_difference`,
  `carrier_unrecognized`, `cash_on_delivery_unmatched`); la bandeja los saca de «Con avisos» con «Marcar como revisado».
- **Dirección de envío de fuera de Colombia.** El pedido nace con su parte de envío: el país del snapshot, la ciudad escrita
  (`cityName`, máx. 120), el `zip` como `postalCode` y, en España, la región (`regionId`) traducida del `province_code` (ISO →
  INE). Sin región traducible la parte entra sin ella, con `shipping_region_unresolved`; se elige en «Editar pedido». Sin
  nombre, teléfono, dirección, correo del comprador o ciudad, o con alguno más largo que lo que guarda el pedido (ciudad 120,
  nombre y dirección 255, correo 254, teléfono 32), no hay parte:
  `shipping_address_unresolved` y la dirección cruda en las notas, como en Colombia. El cliente no cambia (el alta completa
  sigue siendo sólo de Colombia) y los pedidos ya importados no se reescriben.
- Después de importado, el resto de las ediciones de Shopify (líneas, envío, dirección, nota, atributos) llegan como
  **cambios** (ver «Ediciones del pedido» abajo), y además se sincronizan el pago (un pedido sin comprobantes, también preparado o archivado: un
  contraentrega se prepara y se cobra después; con comprobantes, o anulado o facturado, el rechazo queda en `last_error`;
  con `autoArchive`, un pedido que queda pagado y ya estaba preparado se archiva) y la cancelación (se anula en QEP a
  nombre del asesor, aunque esté preparado o archivado, y el historial avisa que hay un paquete que recuperar; uno ya
  facturado queda `Cancelled` con `invoiced_before_cancel` para anularlo a mano). Un pedido cancelado que llega sin fila del espejo
  pero con su pedido en QEP también se anula.
- `ShopifyReconcileWorker` corre cada `Shopify:Reconcile:IntervalMinutes` por tienda operativa: trae por GraphQL lo
  actualizado desde la última corrida (5 minutos de solape; la primera, desde el enlace: el cron no importa historial) y
  reintenta hasta 200 `Waiting`. Así, crear el producto que faltaba o prender el módulo otra vez surte efecto sin tocar
  nada más. `POST …/shopify/stores/{connectionId}/sync` hace lo mismo en el request; `POST …/shopify/orders/{id}/retry`,
  una sola fila.
- **Días de historial de «Sincronizar ahora»** (spec `2026-10-21-shopify-historial-dias-design.md`). Un pedido creado en
  Shopify **antes** de enlazar la tienda no entra nunca por la ventana normal, y el sync responde `fetched = 0` sin error.
  Para traerlo, la tienda guarda `historyDays` (columna `shopify.stores.history_days`, `null` o de 1 a 30, con `CHECK`):
  viaja siempre en `GET|PUT …/shopify/stores/{connectionId}` (`null` = sin historial) y el `PUT` lo reemplaza como al
  resto, así que un cuerpo sin `historyDays` lo apaga. Fuera de rango responde 422 `validation.failed` con
  `errors.historyDays`, y su cambio queda en `shopify.store.updated` por nombre. Con valor, **sólo** el `POST …/sync` trae
  desde `min(ventana normal, enlace − historyDays)` (`ReconcileStore.ComputeSince`): cuenta desde el enlace (la creación
  de la conexión), no desde hoy, y nunca acorta la ventana. Se aplica en **cada** pulsación, para importar lo que quedó
  `Waiting` o falló después de corregir su causa; releer lo ya importado no duplica ni cambia nada (el espejo acepta el
  snapshot igual y `IX_orders_tenant_external_key` deja un solo pedido). El cron no lo usa, y `last_reconciled_at` se
  mueve igual que siempre. Sin fecha de enlace, el historial se ignora.

**Ediciones del pedido** (spec `2026-11-14-shopify-sincronizar-ediciones-design.md`, plan
`2026-11-14-shopify-sincronizar-ediciones-backend.md`, decisiones D1–D22). Lo que se edita en Shopify sobre un pedido ya
importado llega a QEP como un **cambio**, y lo que el asesor corrige en QEP (dirección de envío, ciudad, nota del cliente y
atributos) viaja a Shopify.

- **Qué se lee.** La cantidad **vigente** de cada línea (`current_quantity` en REST, `currentQuantity` en GraphQL; si la llave
  falta, `quantity`, D2). Una línea con vigente 0 es una línea quitada y la importación la salta; el descuento en porcentaje
  se calcula sobre la cantidad **original**, porque `discount_allocations` reparte el de la línea original. También
  `billing_address`, cuya `company` es una fuente más del documento.
- **Versión aplicada y proyección.** `shopify.orders` guarda `applied_payload` (el snapshot con el que QEP creó o actualizó el
  pedido por última vez, o el que adoptó) y `applied_projection` (lo que QEP escribió en el pedido a partir de él, **leído del
  pedido después de escribir**, D4). Un snapshot nuevo se compara grupo por grupo contra la versión aplicada (Líneas, Envío,
  Dirección, Comprador, Nota, Atributos); para cada grupo que cambió en Shopify:
  - si el valor nuevo ya es el del pedido, es **eco** (típicamente la corrección que QEP mandó) y no es cambio;
  - si el pedido ya no es la proyección, lo tocó QEP: **conflicto** `qep_changed`, gana QEP;
  - si no, se aplica.

  Otros motivos de conflicto: `customer_changed` (el documento nuevo es de otro cliente; el pedido no cambia de cliente),
  `city_unresolved` (la ciudad va a la bandeja de ciudades, como al importar), `party_incomplete`, `store_pickup` y
  `store_pickup_changed` (D8: un pedido no cambia de recogida), y los motivos de envío del importador
  (`freight_tax_rate_missing`, …). El grupo Comprador es **informativo**: nunca toca el pedido ni el cliente (D17). Una fila
  importada antes de este cambio, sin versión aplicada, toma como base el `payload` que tenía y la proyección del pedido de
  ahora (D6): para esos pedidos nada cuenta como «lo tocó QEP», y una nota nueva de Shopify se aplica sin conflicto.
- **El cambio** (`shopify.order_edits`; el spec dice `order_changes` y la API, «changes», D1) se crea en
  `ShopifyOrderIngestion.ApplyAsync`, después del espejo y antes de la reacción de pago y cancelación (§6.3), y un snapshot con
  `cancelled_at` no calcula cambio: manda la cancelación (D7). Un pedido tiene a lo sumo un cambio abierto; el siguiente
  snapshot lo reemplaza (`Superseded`). Estados:

  | Estado | Cuándo |
  | --- | --- |
  | `Blocked` | el pedido no es editable (`order_not_editable`), su estado de pago no está en `editPaymentStatuses` (`payment_status_not_allowed`) o una línea no empareja con una variante (`product_unmatched`, D5: nunca se aplica a medias) |
  | `Pending` | se puede aplicar y la tienda está en `editSync = Manual`; o la tienda es `Automatic` y el cambio tiene algún grupo en conflicto (spec §3: el conflicto se ve en la bandeja y lo decide una persona); o Quotations rechazó la aplicación automática con otra regla, que queda en `statusReason` (por ejemplo `order.order.store_pickup`) |
  | `Applied` | se aplicó: solo (`Automatic`, sólo si todos sus grupos son `Apply` o `Informative`) o porque alguien lo aceptó |
  | `Rejected` / `Reviewed` | alguien lo rechazó, o marcó como revisado uno bloqueado; los dos adoptan el snapshot como versión aplicada sin tocar el pedido (D9), así que el siguiente diff no vuelve a proponerlo |
  | `Superseded` | llegó otro snapshot del mismo pedido |

  Aplicar baja por `IShopifyOrderWriter.ApplyEditAsync` → `IExternalOrderWriter.ApplyEditAsync` → `Order.ApplyExternalEdit`, con
  los precios de Shopify (`ShopifyPricing`, sin descuentos de catálogo), historial sin autor (una entrada por grupo) y auditoría
  `quotation.order.external_edit_applied` firmada por la integración; no recalcula el estado de pago (D18). En `Automatic`, el
  cambio se aplica en el mismo procesamiento, **después** del guardado de la ingesta, siempre que no tenga ningún grupo en
  conflicto: uno con conflicto queda `Pending` entero (tampoco se aplican sus grupos `Apply`) para que aparezca en «Cambios por
  revisar», porque es justo donde Shopify y el asesor no están de acuerdo. Si Quotations rechaza la aplicación automática, «no
  editable» lo deja `Blocked` y otra regla lo deja `Pending` con el código en `statusReason`. Un `Pending` que quedó de `Manual`
  (o de un 412) se aplica sólo cuando ese mismo snapshot vuelve a llegar (webhook o conciliación): pasar la tienda a
  `Automatic` no aplica los pendientes que ya había.
- **Lo que corrige el asesor viaja a Shopify.** `PUT …/shopify/orders/{id}/corrections` escribe el pedido de QEP con el
  `QuotationsDbContext` **enrolado en la transacción de Shopify** (`ShopifyOrderCorrectionWriter`) y, en la misma transacción,
  encola el envío en `shopify.order_pushes` con sólo los grupos que cambiaron (`ShippingAddress`, `Note`, `Attributes`). Un
  pedido tiene un solo envío `Pending` (`IX_order_pushes_mirror_pending`); encolar otro grupo mientras espera lo suma al mismo.
  La corrección **no** mueve la proyección (D3): el pedido queda distinto de ella, así que una edición posterior de Shopify sobre
  ese grupo es conflicto también después del eco, y gana lo del asesor. Los atributos viven en la fila del espejo
  (`qep_attributes`), no en el pedido. La nota del cliente es `Order.BuyerNotes` (`Notes` sigue con «Shopify #N» y la
  dirección cruda, y no viaja); `""` o sólo espacios es `null` (D22). La parte de envío sólo se corrige en Colombia (D21).
- **Las correcciones de ciudad del sistema también viajan.** Cuando un sinónimo corrige la ciudad de un pedido
  (`ShopifyCityCorrectionProcessor`, `Applied` o `AlreadyApplied`), la parte escrita pasa a la proyección (no la tocó el
  asesor) y se encola `[ShippingAddress]` en el mismo guardado de la huella. Si el asesor ya había editado la parte, el trabajo
  sale `Skipped` con `shipping_changed` y nada viaja: gana el asesor.
- **La dirección cambiada en «Editar pedido» también viaja** (spec 2026-11-24 §11.1, ver [La entrega
  editable](#la-entrega-editable)). `QuotationOrderEventsWorker` llama cada 10 s al consumidor
  `shopify.quotation-order-events`, que lee `quotations.order.shipping-party-changed.v1` del outbox y encola
  `[ShippingAddress]` (fusionado con el envío abierto; `Held` con `connection_inactive` si la conexión no está operativa). No
  escribe el pedido ni mueve la proyección: una edición posterior de Shopify sobre la dirección es conflicto y gana QEP.
  Pasar a recoger no viaja: `orderUpdate` no cambia el método de entrega.
- **El código postal es parte de la dirección.** La importación, las ediciones de Shopify y la corrección de ciudad por
  sinónimo ponen el `zip` en la parte (uno de más de 20 caracteres se omite), la huella (`shopify.city_corrections.postal_code`)
  y la proyección lo guardan, y la comparación lo mira; «Cambios por revisar» trae la fila `zip` en el grupo `ShippingAddress`.
  Un pedido sin código postal no toma como cambio un `zip` que Shopify no modificó. Un payload guardado antes de que el espejo
  leyera el zip no tiene la clave (distinto de `"zip": null`): de ese zip desconocido no sale ningún cambio.
- **El eco de una dirección de fuera de Colombia** se reconoce comparando lo que llega con lo que el envío de la parte del pedido
  mandaría (`ShopifyPushAddress`, la misma regla de los dos lados): si coincide, no hay cambio. Lo que no es eco se compara
  como en Colombia, con la regla de la importación (`ShopifyForeignAddress`): se aplica si el asesor no tocó la parte (sin
  región traducible, sin región) o es conflicto `qep_changed`. `city_unresolved` queda para una dirección sin ciudad o con una
  de más de 120 caracteres, y no va a la bandeja de ciudades. El cambio de **sólo** el país no genera fila de diferencia.
- **El envío.** `ShopifyOrderPushWorker` corre cada 10 s, en lotes de 50, y manda `orderUpdate` (GraphQL, `shopify.admin`) con
  los valores que el pedido tiene **al mandar**: la dirección con el nombre de QEP en `firstName` y la dirección en `address1`
  (`lastName` y `address2` vacíos, D12) y la `company` del **último** snapshot, porque `orderUpdate` reemplaza la dirección
  entera. En Colombia, la ciudad y el departamento con sus nombres de QEP, el `provinceCode` de `ColombiaProvinceCodes` y el
  `countryCode` del último snapshot si es Colombia (`CO` si no). Fuera de Colombia, el país y la ciudad escrita de la parte y el
  `provinceCode` de su región (`SpainProvinceCodes`, INE → ISO 3166-2; un país sin regiones va sin `provinceCode`), con las
  mismas reglas con las que se rechaza el guardado (`ShopifyShippingAddressRules`). El `zip` es el `postalCode` de la parte de
  QEP; sin él, el del último snapshot sólo si país y provincia de la parte son los del snapshot; si no, no viaja. La corrección
  del panel no recibe código postal: conserva el del pedido mientras no cambie el departamento y, si cambia, lo borra. La nota es
  `BuyerNotes` y los atributos,
  `qep_attributes` o los del último snapshot. Sólo manda los grupos
  del envío. Reintenta (8 intentos; esperas de 10 s, 1 min, 5 min, 15 min, 30 min, 1 h, 2 h y 4 h, o el `Retry-After` si es
  mayor) ante 429, `THROTTLED`, 5xx, red, 401, 402 y token vencido (`AccessToken` nulo, sin llamar a Shopify); termina
  `Failed` con `user_error` (`userErrors`; al log sólo van los campos, nunca el mensaje), `order_not_found`, `rejected` (otro
  4xx) o `retries_exhausted`.
- **Una corrección no se pierde por la conexión** (owner, 2026-11-14). El envío queda **retenido** (`Held`, el motivo en
  `failure_code`) en tres casos: la corrección se guardó con la conexión no operativa (no `Active` o el módulo apagado, la misma
  regla del worker) → `connection_inactive`; la conexión cayó después de encolar → `connection_missing`; Shopify respondió
  `access_denied` (403 o `ACCESS_DENIED`, por ejemplo sin `write_orders`) → `access_denied`, que también se arregla del lado de la
  conexión (el comercio vuelve a abrir la app). La corrección se guarda en QEP igual, y la respuesta del `PUT …/corrections` trae
  `shopifySync`: `Queued`, `Held` o `NothingToSend`; con `Held` la pantalla avisa que el cambio queda marcado para sincronizar
  cuando se active la conexión. Un retenido se comporta como un pendiente al fusionar (`IX_order_pushes_mirror_pending` cubre
  `Pending` y `Held`: un solo envío abierto por pedido) y sigue retenido. **El worker nunca reclama uno retenido**: lo sueltan sólo
  `POST …/orders/{id}/push` (un pedido) y `POST …/stores/{connectionId}/pushes/release` (todos los de la tienda, una sentencia por
  tenant y conexión), que exigen la conexión operativa y lo dejan `Pending` sin intentos ni lease. `GET …/stores` trae
  `heldPushCount` por tienda para el aviso «N pedidos pendientes de sincronizar».
  Shopify devuelve el `orders/updated` con el valor de QEP: es eco y se adopta sin crear cambio. Mientras manda, el envío queda
  tomado 2 minutos (holgado frente a los 10 s del timeout más las lecturas), para que otra réplica no lo mande dos veces. Toda
  corrección que se encola mientras el envío está pendiente le sube la versión, también con los mismos grupos: si el worker
  ya leyó el valor viejo, su `Sent` choca y el envío vuelve a salir con el último. Por la misma carrera, una corrección puede
  recibir un 412 si el worker cerró el envío entre la lectura y el guardado; reintentar funciona.

| Ruta (`/api/v1/tenants/{tenantId}/shopify`) | Permiso | Qué |
| --- | --- | --- |
| `GET /changes?status=&connectionId=&page=&pageSize=` | `quotations.order.read` | «Cambios por revisar», más nuevos primero; sin `status`, `Pending` y `Blocked`; `pageSize` ≤ 50 |
| `GET /orders/{id}/changes` | `quotations.order.read` | los cambios de una fila del espejo, todos los estados (404 `shopify.order.not_found`) |
| `POST /changes/{changeId}/accept` \| `/reject` \| `/review` | `quotations.order.manage` | 404 `shopify.edit.not_found`; 409 `shopify.edit.not_open`; `review` de un `Pending` 422 `shopify.edit.not_blocked`; `accept` que ya no se puede aplicar deja el cambio `Blocked` y responde 422 `shopify.edit.order_not_editable` \| `payment_status_not_allowed` \| `product_unmatched` (D16); otra regla del pedido responde 422 con el código de Quotations y el cambio sigue abierto; pedido inexistente 404 `shopify.order.not_found`; tienda borrada 422 `shopify.store.not_operational`; el pedido cambió entre el cálculo y la escritura, 412 `concurrency.conflict`. Un 409 sobre un cambio que un webhook acaba de reemplazar es «recarga el cambio» |
| `GET /qep-orders/{qepOrderId}` | `quotations.order.read` | el panel de Shopify del detalle del pedido: `canEdit`, `editBlockedReason` (`NotImported`, `Cancelled`, `Archived`, `Invoiced`, `Prepared`, D20), `shippingAddressSupported`, los atributos, el último envío y el cambio abierto (404 `shopify.order.not_found` si el pedido no vino de Shopify) |
| `PUT /orders/{id}/corrections` | `quotations.order.manage` | `If-Match` con la versión del **pedido de QEP**; sin él 428 `precondition.if_match_required`, versión vieja 412, fila no importada 422 `shopify.order.not_imported`, pedido no editable 422 `order.order.not_editable`, recogida con dirección 422 `order.order.store_pickup`, dirección fuera de Colombia 422 `shopify.order.address_not_supported`, cuerpo inválido 422 `validation.failed` con `errors`; la respuesta trae `shopifySync` (`Queued` \| `Held` \| `NothingToSend`) |
| `POST /orders/{id}/push` | `quotations.order.manage` | suelta el envío retenido del pedido (`Held` → `Pending`) y responde el panel; conexión aún no operativa 422 `shopify.store.not_operational`; sin envío retenido 409 `shopify.push.nothing_held`; fila o pedido inexistente 404 `shopify.order.not_found`; una corrección a la vez 412 |
| `POST /stores/{connectionId}/pushes/release` | `quotations.order.manage` | suelta todos los envíos retenidos de la tienda y responde `{ "released": n }`; conexión de otro tenant o inexistente 403 `authorization.denied`; no operativa 422 `shopify.store.not_operational`. Mismo permiso que «Sincronizar ahora» de la tienda: lo hace quien corrige pedidos |

**Límite conocido** (sin resolver a propósito). Dos snapshots del mismo pedido que tocan el mismo grupo y llegan intercalados
por milisegundos —B reemplaza a A antes de que la escritura de A en Quotations confirme, y el segundo guardado de A falla—
pueden dejar la proyección vieja, y la edición siguiente de ese grupo sale como un `qep_changed` falso que el asesor resuelve a
mano. La alternativa es un candado advisory por fila del espejo alrededor de la detección y la aplicación.

**La bandeja.** `GET /tenants/{tenantId}/shopify/orders` pagina el espejo (tope 50, más recientes primero) con
`state` (repetible o separado por comas; sin él, `Waiting`), `connectionId`, `withWarnings=true` (avisos sin revisar;
sin `state` no filtra por estado) y `cancelledAfterInvoice=true`: sólo las `Cancelled` cuyo `wait_detail` es
`{"reason":"invoiced_before_cancel"}`. Ese filtro implica `state=Cancelled`; con otro `state` responde 422
`validation.failed` con `errors.cancelledAfterInvoice`. Sin `connectionId`, el `total` de `withWarnings=true` y el de
`cancelledAfterInvoice=true` son iguales a los contadores de `GET …/shopify/orders/summary`, así que la sección pagina
en el servidor y nunca filtra en el navegador. `cancelledAfterInvoice=true` junto con `withWarnings=true` suma los dos
filtros (AND): los cancelados después de facturar que todavía tienen avisos sin revisar.

La sección **«Cambios por revisar»** es `GET …/shopify/changes` (ver «Ediciones del pedido»): los cambios `Pending` y
`Blocked`, y `GET …/shopify/orders/summary` trae `changesToReview` (los mismos, siempre presente) para su contador.

Cada fila trae `lastError` —texto libre para soporte y logs, que la pantalla no muestra— y `lastErrorCodes`, siempre
presente (`[]` sin error): los códigos de `lastError` partidos por `"; "`, en orden y sin repetir. Un código de
Quotations viaja tal cual; `invalid_payload: …` se vuelve `shopify.order.invalid_payload`; cualquier otra cosa (un
nombre de enum, un texto) es `shopify.order.sync_failed`. Hoy los valores posibles son `order.order.not_editable`,
`order.order.payment_has_proofs`, `order.order.not_found`, `tenancy.tenant.not_found`,
`shopify.order.invalid_payload` y `shopify.order.sync_failed`; el texto de cada uno lo tiene el diccionario del frontend
(`features/shopify`), y uno que no conozca se muestra con un mensaje genérico. La normalización está en
`ShopifyLastErrorCodes`.

**Líneas personalizadas** (spec `2026-11-13-shopify-lineas-personalizadas-design.md`). Una línea personalizada es la que el
comercio escribe a mano en el pedido de Shopify, sin producto: llega con `product_id` y `variant_id` en `null` y sólo trae su
`title` (Shopify lo limita a 255 caracteres). El importador la marca `isCustom` sólo si esas dos llaves vienen presentes y en
`null` (una llave ausente no cuenta) y la línea no trae SKU (una variante borrada también deja los ids en `null`, pero
conserva el SKU: es de producto) y la resuelve por **nombre exacto** —recortado, ordinal, sensible a mayúsculas y
emojis— contra las asociaciones de su tienda (`shopify.custom_line_mappings`: `connection_id + title → variant_id`); una
línea de producto real nunca casa por nombre, y una asociación nunca aplica fuera de su tienda. Sin asociación (o con una
variante inexistente o inactiva) el pedido queda `Waiting` con `product_unmatched` y su `wait_detail` es
`{"skus":[…],"customTitles":[…]}`, siempre con las dos llaves: `skus` lleva los SKU sin resolver (`null` para una línea sin
SKU) y `customTitles` los nombres que faltan por asociar. Los pedidos que ya estaban en espera antes del despliegue no traen
el nombre hasta que Shopify los vuelva a mandar (webhook `orders/updated` o conciliación).

Desde la bandeja, «Asociar a un producto» hace `POST /tenants/{tenantId}/shopify/stores/{connectionId}/custom-line-mappings
{ title, variantId }` (`quotations.order.manage`; 201 con la fila, 409 `shopify.custom_line.already_mapped` si el nombre ya
está asociado, 422 `shopify.custom_line.variant_not_found` o `variant_inactive`) y luego `POST …/shopify/orders/{id}/retry`.
`GET` de la misma ruta (`quotations.order.read`) lista las asociaciones por título con el nombre y el código de la variante,
y `DELETE …/custom-line-mappings/{id}` (204, también si ya no existe) la quita. Si Catalog borró la variante, el `GET` la
sigue mostrando con `productName` y `variantCode` en `null` e `isActive = false`, para poder quitarla.

**Configuración.**

| Clave | Dónde vive en prod | Qué |
| --- | --- | --- |
| `Shopify:App:ClientId` | ConfigMap, desde `SHOPIFY_APP_CLIENT_ID` | Client ID de la app en el Dev Dashboard |
| `Shopify:App:ClientSecret` | Secret, desde la variable **secreta** `SHOPIFY_APP_CLIENT_SECRET` | Firma los webhooks, verifica el HMAC de la instalación y canjea el `code` |
| `Shopify:App:ApiVersion` | ConfigMap, `2026-10` | Versión de la Admin API (`^\d{4}-(01\|04\|07\|10)$`) |
| `Shopify:App:Scopes` | ConfigMap, `read_orders,write_orders,read_customers,read_products` | Lo que pide la autorización (`write_orders` desde el 2026-11-14, `read_products` desde el 2026-11-17) |
| `Shopify:App:AllowedRedirectOrigins:N` | ConfigMap, `https://qep.qcode.co` | Orígenes del `redirect_uri` (local: `http://localhost:3002`, en `appsettings.Development.json`) |
| `Shopify:App:WebhookCallbackUrl` | ConfigMap, `https://qep-api.qcode.co/api/webhooks/shopify` | Lo que el enlace registra en cada tienda |
| `Shopify:Reconcile:IntervalMinutes` | ConfigMap, `10` | Periodo de la conciliación |
| `Shopify:Webhook:MaxBodyBytes`, `DeliveryPollSeconds` | defaults del código (`ShopifyWebhookOptions`: 1 MiB y 15 s); se pisan por configuración si hace falta | Tope del cuerpo (413) y sondeo del worker |

En `Production`, `ShopifyAppOptionsValidator` exige con `ValidateOnStart` `ClientId`, `ClientSecret`,
`WebhookCallbackUrl` (https) y al menos un origen; un `ClientId` o `ClientSecret` que llega como `#{…}#` (la variable
no está en `Backend-prod`) cuenta como ausente. Sin ellas el pod entra en crash-loop, a propósito. Fuera de
`Production`, sin `ClientId` y `ClientSecret` el proveedor sale del catálogo y el módulo arranca igual.

**`write_orders` (spec 2026-11-14).** La corrección del asesor (dirección de envío, nota del cliente y atributos) viaja a
Shopify con `orderUpdate`, que exige `write_orders`. Según el owner, los comercios ya enlazados lo tienen concedido; sumarlo a
`Shopify:App:Scopes` es para que una instalación nueva lo pida, y un canje nuevo sin él falla con `scopes_missing`. Falta
verificarlo (V-13): si una tienda no lo tuviera, sus envíos quedan retenidos (`Held`) con `access_denied`; el comercio tiene que
volver a abrir la app desde su Shopify para concederlo, y después alguien los suelta con `POST …/stores/{connectionId}/pushes/release`.

**Productos desde Shopify (spec 2026-11-17).** Traer el catálogo de una tienda a Catalog, mantenerlo con los webhooks y medir
cuánto ya tiene par en QEP. Rutas (BFF, todas revalidan tenant, permiso, módulo y conexión con 403):

| Ruta | Permiso | Qué |
| --- | --- | --- |
| `POST …/shopify/stores/{connectionId}/products/import` `{ mode: "All" \| "Missing", redirectUri? }` | `integrations.connection.manage` | 202 `{ importId }`; 409 `shopify.products.import_running`; 422 `shopify.store.not_operational`, `shopify.products.company_required` o `shopify.products.scope_missing` (con `reauthorizeUrl`, `null` sin `redirectUri`) |
| `GET …/shopify/stores/{connectionId}/products/import` | `integrations.connection.read` | La última importación (contadores e issues, hasta 50) o `null` |
| `GET …/shopify/stores/{connectionId}/products/coverage?redirectUri=` | `integrations.connection.read` | `{ shopifyVariants, matched, catalogEmpty, missingSample }`, consultado en el momento; 503 `shopify.store.sync_unavailable` si Shopify no responde |
| `DELETE …/shopify/products/{productId}/link` | `integrations.connection.manage` | 204: quita los vínculos del producto en cualquier tienda del tenant y lo deja propio de QEP; 404 `shopify.products.link_not_found` |

La importación la corre `ShopifyProductImportWorker` (cola `shopify.product_imports`, lease de 2 min, 4 intentos, timbre
`shopify_products`), página por página. Cuando termina `Done`, la tienda queda con `products_synced_at` y desde ahí los
`products/create|update|delete` crean, actualizan y desactivan; antes se ignoran con `products_not_synced`. Un producto que falla
en un webhook se loguea (id y código, nunca el cuerpo) y suma en `qep_shopify_product_sync_failures_total{reason}`.

**`read_products` (spec 2026-11-17).** Lo piden la importación, la cobertura y los `products/*`. Una tienda instalada antes no lo
tiene: importar y la cobertura le responden 422 `shopify.products.scope_missing` con la URL para reautorizar, y el registro de
webhooks no le pide los `products/*` (ni la pasa a `NeedsAttention` por ellos) hasta que el comercio vuelva a autorizar la app y
el canje guarde el scope nuevo; el siguiente enlace o probador los registra.

**Asistente de la tienda (onboarding, spec `2026-11-17-onboarding-tienda-shopify-design.md`).** Una tienda recién enlazada se
configura en cinco pasos (`company`, `advisor`, `buyers`, `products`, `history`) con una sola lectura que dice qué falta. Rutas
(todas revalidan tenant, permiso, módulo y conexión con 403):

| Ruta | Permiso | Qué |
| --- | --- | --- |
| `GET …/shopify/stores/{connectionId}/setup?redirectUri=` | `integrations.connection.manage` | `ShopifySetupDto`: los cinco pasos con `status`, `blockedBy` y `missing`, el `openStep`, la tienda con `settings` (el cuerpo del `PUT`, con su `version`), la empresa elegida y la sugerida, las empresas activas, el asesor, los compradores, los productos, el historial, `waiting` y `stats`. Crea la tienda si el módulo no la tenía |
| `POST …/shopify/stores/{connectionId}/setup/company` (`If-Match`) `{ companyId?, name?, taxId?, documentType?, country?, cityId?, cityName?, address?, phone?, email?, chargesVat?, bankAccount? }` | `integrations.connection.manage` (y `companies.company.manage` para crear) | Sin `companyId` crea la empresa por Companies (sus `errors`, su 403, la siembra de tasas) y la asigna: 201 `{ companyId, storeVersion }`. Con `companyId` sólo asigna: 200. 412 `concurrency.conflict` antes de crear si la versión no es la actual; si la asignación pierde después de crear, el 412 trae `details.companyId` y el reintento lo manda. 422 `shopify.store.billing_account_not_found`; 503 `shopify.store.sync_unavailable` si no hay moneda (ni cuenta, ni perfil, ni la guardada) |
| `POST …/shopify/stores/{connectionId}/products/coverage/accept` | `integrations.connection.manage` | 204: «Seguir así», el paso `products` queda hecho con la cobertura que hay |
| `POST …/shopify/stores/{connectionId}/sync?source=setup` | `quotations.order.manage` | La respuesta de siempre; además la tienda guarda el resumen de la corrida (`history.lastRun`). Otro `source` es 422 `errors.source` |

- **El perfil de la tienda** (`shopProfile`) es un campo **interno** de la conexión de Integrations: un JSON con el correo, el
  correo de contacto, la moneda, si los precios incluyen impuestos y la dirección de facturación de `ShopQuery`. Lo escriben el
  enlace (de mejor esfuerzo) y el probador. No sale en la respuesta de la conexión ni en logs: lleva datos personales del
  comerciante. Con él el asistente sugiere la empresa (la ciudad se resuelve en Geography sólo en `CO`) y la moneda.
- **«¿Cobras impuestos?»** se sugiere con `productVariants(first: 1, query: "taxable:true")` mientras el paso `company` no esté
  hecho y haya `read_products`; sin productos, sin scope o con Shopify caído es `null`.
- **La cobertura se guarda en la tienda** (`products_coverage`, con `measuredAt`) en cada `GET …/products/coverage`, y el asistente
  la mide sola (tope de 15 s) la primera vez que hay scope y no hubo importación. Ni la medición, ni «Seguir así», ni el resumen del
  historial suben la versión de la tienda.

**Ediciones en la tienda.** `GET|PUT …/shopify/stores/{connectionId}` llevan además `editSync` (`Manual`, por defecto, o
`Automatic`) y `editPaymentStatuses` (los estados de pago de QEP en que un cambio de Shopify se aplica; por defecto los cuatro:
`FullPaymentReceived`, `PartialPaymentReceived`, `PaymentPending`, `PendingVerification`). En el `PUT` los dos son opcionales y
`null` conserva lo guardado (D15), para que un frontend viejo no borre la configuración; `editPaymentStatuses` no puede venir
vacío ni repetido. Errores: `errors.editSync`, `errors.editPaymentStatuses` y `errors.editPaymentStatuses[i]`.

En local, los dos van por user-secrets. `set` imprime el valor, así que va con `| Out-Null`; el secreto se pide con
`Read-Host -MaskInput` para que no quede en el historial de PSReadLine, y la verificación cuenta. `-MaskInput` exige
PowerShell 7 (`pwsh`); en Windows PowerShell 5.1 el parámetro no existe y `$secret` queda vacío (la alternativa va
después del bloque):

```powershell
dotnet user-secrets set "Shopify:App:ClientId" "<client-id-de-la-app>" --project src/Api | Out-Null
$secret = Read-Host -MaskInput "Client secret de Shopify"
dotnet user-secrets set "Shopify:App:ClientSecret" $secret --project src/Api | Out-Null
Remove-Variable secret
dotnet user-secrets list --project src/Api | Select-String -Pattern "Shopify:App" | Measure-Object
```

En Windows PowerShell 5.1, cambia la segunda línea por estas dos:

```powershell
$secure = Read-Host -AsSecureString "Client secret de Shopify"
$secret = [System.Net.NetworkCredential]::new("", $secure).Password
```

y al final borra también `$secure` (`Remove-Variable secure`).

El último comando tiene que dar `Count 2`. Sin túnel, Shopify no puede entregar webhooks a tu máquina; la conciliación y
«Sincronizar ahora» traen los pedidos igual. Con túnel, `Shopify:App:WebhookCallbackUrl` es la URL del túnel más
`/api/webhooks/shopify`.

**Despliegue.**

1. Variables `SHOPIFY_APP_CLIENT_ID` (normal) y `SHOPIFY_APP_CLIENT_SECRET` (**secreta**) en `Backend-prod`. El pipeline
   no las nombra: la plantilla reemplaza cada `#{…}#` de `k8s/` con la variable del grupo que se llame igual.
2. Backend, con sus migraciones (`AddShopifyModuleKey` en Tenancy, `AddOrderExternalKey` en Quotations,
   `AddCustomerEmailIndex` en Customers, `AddShopifyProvider`, `AddShopifyInstalls` y `AddShopifyInstallRefreshToken` en Integrations, `InitialShopify` y
   `AddStoreShopDomain` en Shopify), y el frontend de Shopify en la misma ventana (el porqué va abajo). **Rollback del
   backend:** el SPA muestra rutas que responden 404; nada se corrompe.

   **`develop` no va a `main` sin el frontend de Shopify.** Las dos variables del paso 1 no alcanzan. Todo tenant nuevo
   nace con `shopify` (`DefaultForNewTenants`) y en `Production` la app está configurada, así que su catálogo de
   Integraciones trae la tarjeta Shopify con `onboarding.kind = "ShopifyOAuth"`. El frontend de `develop` (`c940537`,
   `provider-catalog.tsx`) manda todo `kind` que no sea `Form` ni `InstagramLogin` a `/integrations/whatsapp/new`: el
   «Conectar» de Shopify abre el Embedded Signup de Meta. Mientras el backend esté en producción sin el frontend de
   Shopify, cada tenant nuevo ve esa tarjeta rota, y pasa igual si `develop` llega a `main` por otra razón (un hotfix,
   otra feature). Por eso el frontend de Shopify se despliega junto con el backend, no después. La alternativa, que
   decide el owner, es sacar `shopify` de `DefaultForNewTenants` hasta que exista el frontend.
3. La app en el Dev Dashboard de Shopify, la verificación con una tienda de desarrollo y el primer tenant piloto: los
   pasos y lo que queda por verificar contra Shopify real (V-1 a V-10) están en
   `docs/superpowers/plans/2026-10-18-shopify-handoff.md`.
4. **Ediciones del pedido (spec 2026-11-14).** Las migraciones `AddStoreEditSync`, `AddOrderEdits` y `AddOrderPushes`
   (Shopify) corren con el backend, sin backfill: las filas importadas antes toman su base en la primera edición (D6). El
   worker de envíos (`ShopifyOrderPushWorker`) entra con el backend, y `Shopify:App:Scopes` pasa a
   `read_orders,write_orders,read_customers` en el ConfigMap.

   **Antes del ConfigMap, la app.** En el Dev Dashboard de Shopify, publica (*release*) una versión de la app cuyos scopes
   pidan `write_orders`. `CompleteShopifyInstall` exige ahora `write_orders` en lo que Shopify concede: si la versión
   publicada de la app no lo pide, toda instalación nueva o reenlace falla con `integrations.shopify.scopes_missing` en
   cuanto el ConfigMap llega a producción. El orden es: versión de la app con `write_orders`, después el ConfigMap.

   Quedan por verificar contra Shopify real:

   | # | Qué | Cómo se cierra |
   | --- | --- | --- |
   | V-11 | Que el webhook REST traiga `current_quantity` y la línea quitada, y que `orders/updated` llegue al editar un pedido (orderEdit) | Editar un pedido en la tienda de desarrollo y mirar `shopify.orders.payload` y `shopify.order_edits` |
   | V-12 | Que `orderUpdate` acepte `shippingAddress` con `provinceCode` de Colombia y `customAttributes`, y que el `orders/updated` de vuelta salga como eco. Además: (a) el teléfono de ida y vuelta: si Shopify guarda en E.164 el teléfono nacional que QEP mandó, el eco tiene que salir igual (QEP compara sin el indicativo de país, `ShopifyOrderProjection.SamePhone`); (b) que `note: null` borre la nota en Shopify y no se ignore (si la ignora, mandar `""`) | Corregir una ciudad en QEP, ver el envío `Sent` y la dirección en Shopify, y que no aparezca un cambio; mirar el teléfono que quedó en Shopify; borrar la nota del cliente en QEP y verla vacía en Shopify |
   | V-13 | Que las instalaciones existentes tengan `write_orders` | `currentAppInstallation.accessScopes` de cada tienda; un envío `Held` con `access_denied` también lo delata |
5. **Productos desde Shopify (spec 2026-11-17).** Migraciones aditivas, sin backfill: `AddProductExternalSource` (Catalog) y
   `AddProductSync` (Shopify: `product_links`, `product_imports`, `product_import_issues` y `stores.products_synced_at`). El
   worker de importaciones (`ShopifyProductImportWorker`) entra con el backend, y `Shopify:App:Scopes` pasa a
   `read_orders,write_orders,read_customers,read_products` en el ConfigMap.

   **Antes del ConfigMap, la app.** Igual que con `write_orders`: `CompleteShopifyInstall` exige todo scope configurado, así que
   primero se publica en el Partner Dashboard una versión de la app que pida `read_products` y después llega el ConfigMap. Al
   revés, toda instalación nueva o reenlace falla con `integrations.shopify.scopes_missing`. Las tiendas ya instaladas siguen
   funcionando sin `read_products`: reciben 422 `scope_missing` al importar y no reciben `products/*` hasta reautorizar.

   Quedan por verificar contra Shopify real (§9):

   | Qué | Cómo se cierra |
   | --- | --- |
   | `productsCount(limit: null)` devuelve el total de activos y borradores | Importar una tienda de desarrollo y comparar `total` con su lista de productos |
   | `featuredMedia.preview.image.url(transform:)` entrega un JPG de hasta 2048×2048 | Mirar el archivo que quedó en Storage para un producto con imagen |
   | El filtro `product_status` de `productVariants` (la cobertura) deja fuera los archivados | Archivar un producto y ver que la cobertura no lo cuente |
   | Una tienda con variantes sin SKU y con `taxesIncluded = false` | Importarla: códigos `shp-{id}` y precios con IVA sumado si la empresa cobra IVA y la moneda es COP |
6. **Asistente de la tienda (spec 2026-11-17 onboarding).** Una sola migración, **aditiva**, sin backfill: `AddStoreSetup`
   (Shopify: cuatro columnas nulas en `shopify.stores`, `products_coverage`, `products_coverage_accepted_at`,
   `setup_history_ran_at` y `setup_history_summary`). Un pod viejo las ignora. El perfil de la tienda no tiene migración: es un
   campo de la conexión, y una conexión enlazada antes lo recibe en el siguiente probador o reenlace (hasta entonces la sugerencia
   es sólo el nombre de la tienda).

   Quedan por verificar contra una tienda de desarrollo:

   | Qué | Cómo se cierra |
   | --- | --- |
   | `shop.billingAddress` (empresa, ciudad, `provinceCode` de Colombia, `countryCodeV2`, teléfono) llega como lo lee `ShopQuery` | Enlazar la tienda y mirar la sugerencia del `GET …/setup` |
   | `shop.contactEmail` existe y es el correo que el comercio ve como de contacto | El mismo `GET …/setup`: `company.suggested.email` |
   | `productVariants(first: 1, query: "taxable:true")` distingue una tienda con variantes gravadas de una sin | Marcar todas las variantes como no gravadas y ver `suggestedChargesVat: false`; marcar una y ver `true` |

### Integración con Siigo

Un pedido de QEP se factura en Siigo desde la pantalla, en segundo plano (spec `2026-11-18-integracion-siigo-design.md`): QEP arma la factura de venta
con las líneas, los impuestos, las retenciones y los comprobantes de pago del pedido, la crea en Siigo y marca el pedido como
facturado con su número (`FV-1-123`). Módulo `siigo` (depende de `orders`, no está en `DefaultForNewTenants`: se prende desde la
consola de operador, como `pos`), esquema `siigo` (`settings`, los vínculos de impuestos, formas de pago y productos, e
`invoices`), y del lado de Integrations el proveedor `siigo`. Recibos de caja y notas crédito quedan fuera (fase 2).

**Conexión.** El tenant crea la conexión `siigo` en Integrations con su usuario y su clave de acceso (cifrada, como las demás); el
probador la valida contra `/auth`. Cada conexión es una empresa de Siigo y se configura aparte, con `siigo.settings.manage` (de
fábrica `admin` y `billing`; crear, editar o desconectar la conexión exige `integrations.connection.manage`, sólo `admin`), bajo
`/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}`: tipo de documento, vendedor, centro de costo, bodega, empresa de QEP
que factura, impuestos, formas de pago y vínculos de productos. Los `PUT` llevan `If-Match` (sin él, 428; con una versión vieja,
412). El asistente propone cada paso con lo que lee de Siigo (`/options/{lista}`) y el auto-vínculo casa variantes y productos por
código, sin distinguir mayúsculas ni espacios.

**Generar la factura.** Toda emisión es una tanda (spec `2026-11-24-siigo-facturacion-en-segundo-plano-design.md`), también la de un
solo pedido: `POST /api/v1/tenants/{tenantId}/siigo/invoice-batches` con `orderIds` (de 1 a 50) y, sólo con un pedido, `payments`
(`kind`, `proofId`, `siigoPaymentTypeId`). Permiso `siigo.invoice.issue`, y el handler revalida tenant y permiso: 403, nunca 404.
Responde `202` y un worker factura cada pedido como la persona que pidió la tanda; el avance se lee con
`GET …/siigo/invoice-batches/{batchId}` y todas las tandas con `GET …/siigo/invoice-batches`. Un pedido que ya está en una tanda sin
terminar es `409 siigo.invoice_batch.order_in_progress`. La factura de un pedido se consulta y se atiende bajo
`/api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice` (`GET`, `/availability`, `/preview`, `/{invoiceId}/pdf`,
`/{invoiceId}/send-whatsapp`, `/stamp`, `/resolve` y `/annulment`); esa ruta no genera facturas. `GET …/preview` devuelve la factura
armada, los faltantes y la propuesta de pagos sin llamar a Siigo. Crear la factura espera a Siigo hasta 120 s; todo lo demás, 30 s.
Cada fila que devuelven `GET`, `/stamp` y `/resolve` trae `orderMarkFailed`: va en `true` en la factura del pedido que existe en
Siigo cuando el pedido no lleva su marca (o no se pudo leer) y ninguna tanda sin terminar lo tiene; otra tanda del pedido la completa
sin crear otra factura. La de flete siempre va en `false`. Mientras una tanda sin terminar tenga el pedido, `canResend` va en `false`,
en esas filas y en los ítems de las tandas.

- Un candado por pedido (`pg_advisory_xact_lock`) serializa el envío. Con una fila vigente `Pending` se reenvían **los mismos
  bytes con la misma `Idempotency-Key`**; `Created`, `Stamped`, `StampRejected` y `Unknown` se devuelven sin llamar a Siigo;
  `Rejected`, `Failed` y `Annulled` arman una fila nueva.
- Si no se sabe si Siigo creó la factura (timeout, red, 5xx, 429 o un error de servicio de Siigo), la fila queda por confirmar
  (`Pending`) y el ítem `ToConfirm`: una persona la reenvía con otra tanda o la resuelve (`POST …/resolve` con `exists` o `missing`;
  `missing` espera 10 minutos desde el último envío sin respuesta). No hay reintentos automáticos. Una `Pending` de más de 24 h pasa a
  `Unknown` y sólo se resuelve. Mientras una tanda sin terminar tenga el pedido, `missing` es
  `409 siigo.invoice_batch.order_in_progress`. La de flete no se resuelve mientras la del pedido siga abierta (`Pending` o
  `Unknown`): `422 siigo.invoice.state_invalid`.
- Si falta algo (variante sin vínculo, impuesto sin vínculo, cuenta sin forma de pago, cliente sin dato) el pedido sale `Skipped` en
  la tanda con `siigo.invoice.not_ready` y los códigos en `missing`, y nada sale a Siigo.
- Cada comprobante de pago del pedido es un pago de la factura; el saldo va a contraentrega o a crédito. Los `payments` suman el
  neto (con las retenciones ya descontadas).
- **Cuadre de totales.** Un pedido cuyos totales no cuadran con los de Siigo no se factura. Antes de enviar, el armador compara
  subtotal, IVA y total de los ítems con los del pedido (tolerancia de 1 peso: el pedido sale `Skipped` con
  `siigo.invoice.totals_mismatch`); en Siigo, si los pagos no suman, rechaza con `invalid_total_payments` y la fila queda
  `Rejected` con el total esperado en `expectedPaymentsTotal`. La excepción es una diferencia de redondeo (hasta 1 peso, sin
  envíos sin respuesta): el último pago se ajusta al total de Siigo y la factura se reenvía una sola vez con otra clave
  (`siigo.invoice.payments_adjusted` en la auditoría).
- «Enviar a la DIAN» nace **apagado** en cada conexión. Con el interruptor apagado la factura queda creada y sin emitir, y una
  persona la revisa en Siigo antes de prenderlo. Una factura emitida no se borra, y no hay empresa de pruebas de Siigo: es la única red.
- El flete va como un ítem de la factura del pedido o, si la conexión lo pide (`freight_separate_invoice`), en su propia factura.
- Mientras exista una factura viva, la marca manual y la anulación del pedido responden `409 order.order.external_invoice_pending`.
  Un pedido facturado en Siigo no se revierte con la acción manual (`422 order.order.invoiced_externally`): el pedido se libera cuando
  QEP verifica que la factura se anuló en Siigo (`/annulment`, abajo). Una factura aprobada por la DIAN se corrige con una nota
  crédito, que QEP no maneja.

**Anulada en Siigo** (spec `2026-11-25-siigo-anular-factura-design.md`). QEP no anula facturas: una persona la anula (o la borra) en
Siigo Nube y después llama `POST …/siigo-invoice/annulment` (sin cuerpo, permiso `siigo.invoice.issue`). QEP lee en Siigo cada factura
vigente del pedido y, **sólo si todas están anuladas** (la del pedido y la de flete), pasan a `Annulled` (con `errorCode`
`siigo.invoice.annulled_in_siigo` o `siigo.invoice.deleted_in_siigo`), el pedido pierde la marca de facturado y se puede volver a
facturar. Responde `200` con las filas actuales. Ante cualquier duda no cambia nada: `422` `siigo.invoice.annulment_unverifiable`
(una factura `Pending`, `Unknown` o resuelta a mano sin id de Siigo), `siigo.invoice.not_annulled` o
`siigo.invoice.annulment_unclear` (respuesta no reconocida, o anulada y aprobada por la DIAN a la vez), los tres con
`details = { kind, number }`; además `404 siigo.invoice.not_found`, `409 siigo.invoice_batch.order_in_progress` o
`siigo.connection_unavailable`, `502 siigo.invoice.siigo_service`, `503 siigo.unavailable` y `422 siigo.invoice.state_invalid` si no
hay nada que anular. Hasta verificar qué contesta Siigo con una factura **borrada**, borrarla no libera el pedido: sale como respuesta
no reconocida y queda en `call_failures` (operación `InvoiceRead`).

`GET …/siigo-invoice` y la respuesta de `/annulment` suman dos campos a cada fila (en `/stamp` y `/resolve` van en `false`):

- `canConfirmAnnulment`: sólo en la fila del pedido; la pantalla ofrece «Ya la anulé en Siigo» cuando va en `true`. Exige que ninguna
  tanda sin terminar tenga el pedido y que toda factura vigente se pueda verificar, o que haya que reintentar la marca.
- `orderUnmarkFailed`: la factura del pedido quedó `Annulled` pero el pedido sigue marcado con su número. Repetir la acción reintenta
  sólo la marca, sin llamar a Siigo.

**Enviar la factura por WhatsApp** (spec `2026-11-21-siigo-invoice-whatsapp-design.md`).
`POST /api/v1/tenants/{tenantId}/orders/{orderId}/siigo-invoice/{invoiceId}/send-whatsapp` (permiso `siigo.invoice.issue`, sin cuerpo,
`204`) manda el PDF de **una** factura al WhatsApp del cliente del pedido. Es el mismo PDF de la descarga (se pide a Siigo en cada envío y
viaja en memoria: Siigo no la guarda; el envío por Meta deja una copia privada en el hilo de Mensajería, ver más abajo). Una factura de otro pedido o tenant, o sin PDF (`Rejected`, `Pending`, resuelta a mano sin id), es `404
siigo.invoice.not_found` sin llamar a Siigo ni al canal; sin permiso o con el tenant de otra ruta es `403`, nunca `404`. El envío lo hace
Quotations (`IInvoiceWhatsAppDelivery`; Siigo sólo ve el puerto `ISiigoInvoiceWhatsApp`), así que el canal se resuelve como en el pedido
y los errores son `invoice.whatsapp.*`: `no_channel`, `channel_required`, `no_phone` (el
cliente del pedido no tiene teléfono; Quotations lo mira dentro del envío, así que el PDF ya se pidió a Siigo) y `send_failed`, más
`order.order.client_not_found` (el cliente del pedido no existe en el tenant); todos son errores de dominio `422`. `channel_unavailable` y `channel_already_set` no pueden
salir acá: el envío nunca pide un canal concreto. No
hay deduplicación: dos clics son dos mensajes y dos entradas de auditoría `siigo.invoice.whatsapp_sent` (sin teléfono, URL ni contenido).

`GET …/siigo-invoice` suma `canSendWhatsApp` a cada fila: la fila tiene PDF **y** hoy se podría entregar (el canal se resuelve igual que en
el envío y el cliente tiene teléfono). Se calcula una vez por petición, y si ninguna fila tiene PDF no se consulta. **Limitación conocida:**
un tenant con dos o más canales aptos para facturas y sin elección guardada (la elección se guarda al mandar una cotización o un pedido y
escoger canal) recibe `canSendWhatsApp = false` hasta que elija uno; mandar una factura nunca guarda la elección.

**Plantilla `Invoice`.** Cada canal necesita su plantilla de factura; sin ella no cuenta como canal para una factura.

- **Meta:** se crea desde QEP con `POST /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management/invoice`
  (permiso `messaging.template.manage`) y espera la aprobación de Meta. El PDF viaja como encabezado de la plantilla.
- **Zenvia:** crea la plantilla en Zenvia con las variables **exactamente** `fullname`, `invoice_number` y `total` y el encabezado
  `documentUrl`, y pon su id en `invoiceTemplateId` de **cada** conexión de Zenvia (Integrations). Zenvia descarga el documento por URL.

**Copia pública (sólo Zenvia).** Para Zenvia el PDF se copia al bucket público bajo `invoices/{guid}.pdf`, con una clave aleatoria que no
lleva tenant, pedido ni número de factura (la URL queda abierta a quien la tenga). Por Meta Quotations no publica ninguna copia, pero el envío deja una copia **privada** del adjunto en el hilo de la conversación de Mensajería (por tenant y con la retención de cualquier adjunto saliente, como en cotizaciones y pedidos); no es pública. Sin bucket
público el envío falla, sin caer a una URL firmada. La copia expira con una regla de lifecycle que se crea **a mano** en Cloudflare, y es
parte del despliegue:

```powershell
npx wrangler r2 bucket lifecycle add <bucket-publico> expire-invoices "invoices/" --expire-days 1
npx wrangler r2 bucket lifecycle list <bucket-publico>   # para verificarla
```

**Seguridad.** La clave de acceso y el token de Siigo no salen a logs, auditoría ni respuestas; `request_json` (con el documento y
la dirección del cliente) no lo devuelve ningún `GET`, y `error_detail` guarda los códigos de Siigo, no sus mensajes (salvo el
total esperado). El PDF, la URL pública y el teléfono del envío por WhatsApp tampoco salen a logs, `siigo.call_failures`, auditoría ni
respuestas. Lo amarra `SiigoNoLeakTests` con el host en `Trace`.

**Despliegue.**

1. `Siigo__PartnerId` va en el ConfigMap como `"#{SIIGO_PARTNER_ID}#"`: define `SIIGO_PARTNER_ID` en el pipeline **antes** de
   desplegar. En `Production` el pod no arranca sin él, y un token sin reemplazar cuenta como ausente.
2. Migraciones, todas aditivas y sin corte: `AddSiigoProvider` (Integrations), `InitialSiigo` y `AddSiigoInvoices` (Siigo),
   `AddOrderInvoiceProvider` (Quotations) y `AddSiigoModuleKey` (Tenancy). Sus `Down` no se corren en producción: el de
   Integrations borra las conexiones `siigo` con sus secretos, y `siigo.invoices` es el registro de un documento legal.
3. Prende el módulo al tenant desde la consola de operador, crea la conexión, recorre el asistente y deja «Enviar a la DIAN»
   apagado en las primeras facturas.

**Despliegue del envío por WhatsApp**, en este orden:

1. Migración de Messaging (el tipo de plantilla `Invoice`).
2. Crea y aprueba la plantilla `Invoice` en Meta (`template-management/invoice`) y en Zenvia, y pon `invoiceTemplateId` en cada conexión
   de Zenvia.
3. Regla de lifecycle de `invoices/` en el bucket público (comando de arriba).
4. Backend y después frontend (spec aparte en `qep-frontend`).

**Lo que se mira en las primeras facturas reales** (no hay empresa de pruebas: se resuelve con ellas, y con el interruptor
apagado ninguna puede salir mal sin que se vea):

| | Qué | Cómo se cierra |
| --- | --- | --- |
| V-1 | Límite de peticiones de Siigo | Mirar los 429 en las primeras facturas |
| V-2 | Redondeo por ítem con descuento y cuándo conviene `price` y cuándo `taxed_price` | Comparar el total de Siigo (`siigoTotal`) con el del pedido |
| V-3 | Documento del consumidor final | Facturar un pedido de consumidor final y ver que Siigo lo acepta |
| V-5 | Cuánto recuerda Siigo un `Idempotency-Key` | Decide si 24 h para `Unknown` es corto o largo |
| V-6 | Si una factura electrónica exige `fiscal_responsibilities` y `vat_responsible` en el cliente, y el formato de `person_type` | Mirar el rechazo con un cliente persona jurídica |
| V-7 | Código y texto de `invalid_total_payments` con y sin retenciones; base mínima (UVT) y redondeo de cada retención; ReteIVA y autorretención; borrado de una factura sin emitir por API | Con la primera factura real sin emitir (`stamp.send = false`) |
| V-8 | Cuándo exige Siigo la bodega en un ítem y si un servicio la rechaza | Facturar un producto con control de inventario y un servicio |

### Contratos de servicio (cobros recurrentes)

Spec `docs/superpowers/specs/2026-10-29-cobros-recurrentes.md` (el código gana ante cualquier discrepancia). Módulo
`Modules.ServiceContracts.{Domain,Application,Infrastructure,Api}` (hasta el 2026-10-30 se llamó `Billing`; el agregado
`ServiceContract` era `BillingService`), esquema `billing`, clave de módulo `billing` (`Dependencies = [Customers]`, fuera de
`DefaultForNewTenants`: nace apagado). **El renombre fue sólo de código y de pantalla:** esquema, clave, permisos
`billing.service.*`, rutas `/billing`, acciones de auditoría, códigos de error y la sección `Billing` de configuración siguen
como estaban, porque ya estaban desplegados.

**Permisos y roles.** `billing.service.read` (toda lectura de la lista de servicios, cobros y perfil) y `billing.service.manage` (todo
lo que escribe, y también la configuración). De fábrica los llevan `admin` y el rol sembrado `billing` («Facturación»); ningún otro. Ambos
declaran `RequiredModules: [billing]`: con el módulo apagado el editor de roles no los ofrece y la API responde un `403` pelado, sin
el código `tenancy.module_not_enabled` (decisión del owner, 2026-10-29: nunca se muestran opciones que el tenant no tiene).

**Rutas** (`/api/v1/tenants/{tenantId}/billing`, con `X-Tenant-Id`):

| Método | Ruta | Permiso | Qué hace |
| --- | --- | --- | --- |
| `GET` / `PUT` | `/customers/{customerId}/profile` | read / manage | Perfil de cobro: canal (`WhatsApp` o `Email`) y encargado. Sin `If-Match`: la última escritura gana |
| `GET` | `/assignees` | manage | Miembros activos que pueden ser encargados |
| `GET` / `POST` | `/services` | read / manage | Lista paginada con resumen por moneda / crea el servicio (y su perfil si el cliente no tiene). `description` es opcional: se recorta, en blanco es `null`, tope 1000 (422 `billing.service.description_too_long`) |
| `GET` | `/services/preview-charges` | manage | Cuántos cobros nacerían con esa fecha y ciclo, sin escribir |
| `GET` / `PUT` | `/services/{serviceId}` | read / manage | Detalle con cobros e historial de precios (`priceChanges`) / edita nombre, descripción, precio y moneda (`If-Match`). `description` reemplaza a la guardada: `null`, en blanco o ausente la borra, y nunca exige motivo. Cambiar el precio o la moneda exige `priceChangeReason` (422 `billing.service.price_reason_required` sin él) y deja una fila en `billing.service_price_changes` |
| `POST` | `/services/{serviceId}/pause`, `/resume`, `/cancel` | manage | Transiciones de estado (`If-Match`); cancelar cancela los cobros `Pending` |
| `GET` | `/charges`, `/charges/{chargeId}` | read | Cobros con cliente, encargado y último aviso |
| `POST` / `PUT` | `/charges/{chargeId}/pay`, `/charges/{chargeId}/payment` | manage | Marca pagado / corrige el pago |
| `GET` / `PUT` | `/settings` | manage | Conexión de WhatsApp con la que se avisa (`whatsAppConnectionId`) |
| `GET` / `POST` | `/import/template`, `/import/preview`, `/import` | manage | Importación desde Excel |

**Generación por período.** Cada período genera su propio cobro aunque el anterior siga sin pagar; marcar pagado no genera nada. Un
cobro nace cuando `due_date <= hoy del tenant + 5`. Las fechas son `firstDueDate + k × cycleMonths` con el día del ancla (31 ene →
28 feb → 31 mar). Crear un servicio con fecha pasada genera todos los períodos hasta hoy + 5; el worker genera los que falten. La
unicidad `(service_id, due_date)` hace la generación idempotente entre réplicas. Pausar no silencia los cobros ya existentes;
reanudar no cobra los períodos en pausa.

**Calendario y agrupación.** Pasos de aviso de un cobro: `due − 5`, `due`, `due + 3` y cada 7 días después. Si un día se perdió, sólo
se cubre el paso más reciente. **Un mensaje por cliente y día** (índice único `IX_customer_messages_tenant_customer_day`): `Overdue`
si algún cobro con paso hoy está vencido (lista todos los vencidos), si no `Reminder`. Un total por moneda, nunca convertido. Un
cliente **sin perfil de cobro** no recibe aviso; sus cobros van al email diario de cada miembro con `billing.service.manage`.

**Worker** (`ServiceContractsReminderWorker`, `Billing:SweepMinutes`): por tick recorre los tenants activos con el módulo prendido; desde
`Billing:SendHourLocal` en la zona horaria del tenant genera cobros, manda el mensaje del día a cada cliente y el email diario al
equipo, y cierra el día en `billing.tenant_runs`. La entrega es **al menos una vez**: si el proceso cae después de que el proveedor
aceptó el mensaje, el aviso puede llegar dos veces. Un fallo (`no_phone`, `no_email`, `channel_not_configured`,
`template_not_approved`, `connection_unavailable`, `send_failed`) queda en el mensaje y nunca cambia de canal.

**Configuración** (`src/Api/appsettings.json`, validada al arrancar):

```json
"Billing": { "SweepMinutes": 15, "SendHourLocal": 8 }
```

`SweepMinutes > 0` y `SendHourLocal` entre 0 y 23; si no, la API no arranca. No hay secretos propios: el correo sale por el canal de
Notifications y WhatsApp por la conexión `whatsapp-cloud` del tenant.

**Plantillas de WhatsApp.** Son dos, de categoría `UTILITY`, idioma `es`, con variables con nombre (`{{nombre_contacto}}`, `{{servicio}}`…): `qep_cobro_recordatorio` y
`qep_cobro_vencido`. El texto base está en la spec (§5) y el admin lo edita antes de enviarlo a Meta; las filas viejas con parámetros
posicionales (`{{1}}`…) siguen saliendo. QEP las crea en la cuenta de Meta del tenant con
`POST /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management/billing` (exige el módulo `billing`;
reintenta si quedó a medias). **Meta debe aprobarlas** antes de enviar: sin aprobación el aviso falla con
`template_not_approved`. Una vez aprobadas, el tenant elige la conexión en `PUT /billing/settings`.

**Importación desde Excel.** `.xlsx`/`.xls`, 10 MB, 500 filas. `preview` no escribe; `import` revalida y guarda las filas válidas (una
fila con error no frena a las demás). Columnas: `Cliente`, `Tipo de documento`, `Número de documento`, `Teléfono`, `Correo`, `País`,
`Servicio`, `Precio`, `Moneda`, `Ciclo`, `Próximo vencimiento`, `Canal de aviso`, `Encargado` (correo de un miembro con
`billing.service.manage`). El cliente se empareja por documento, o si no por teléfono, o si no por correo (el primero que aplica
decide); sin coincidencia se crea **incompleto**, sin código de cliente. Si ya tiene perfil, canal y encargado de la fila se ignoran. La
importación de **Clientes** dejó de exigir la clasificación: vacía, el cliente queda sin clasificación (y sin prefijo de CUC).
Los errores por fila se registran en el log con su código y el detalle.

**Habilitarlo.** Desde la consola del operador (ver «Módulos por tenant»), prender el módulo `billing` al tenant. Se prende sólo para
quien lo contrata; mientras esté apagado el worker lo ignora.

**Migraciones, en este orden de despliegue** (cada módulo aplica las suyas al arrancar, `Program.cs`): `Tenancy`
`AddBillingModuleKey` (el CHECK de `tenant_modules` admite `billing`), `Messaging` `AddBillingTemplateKinds` (tipos de plantilla de
cobro) y `ServiceContracts` `InitialBilling` (esquema `billing`). ServiceContracts se inicializa al final, después de Audit.

**Despliegue.** **El backend se despliega antes que el frontend** (`qep-frontend` consume estas rutas). Orden: (1) backend con el módulo, sin `billing` habilitado en ningún tenant: el worker no hace nada; (2) frontend;
(3) el operador habilita `billing` en el tenant piloto, que aprovisiona las plantillas, espera la aprobación de Meta y elige la
conexión en Configuración.

### Préstamos

Módulo `Modules.Loans.{Domain,Application,Infrastructure,Api}`, esquema `loans`, clave de módulo `loans` (`Dependencies = [Customers]`,
fuera de `DefaultForNewTenants`: nace apagado). Rutas bajo `/api/v1/tenants/{tenantId}/loans` (`X-Tenant-Id`), permisos
`loans.loan.read` y `loans.loan.manage`. Un préstamo es de sólo intereses: cada período de corte genera una cuota con el interés del
capital vigente, y los abonos a capital cambian el capital desde su fecha.

**Worker** (`LoansGenerationWorker`, `Loans:SweepMinutes`, 15 por defecto, mayor que cero o la API no arranca): por tick recorre los
tenants activos con el módulo prendido y convierte en cuota cada período ya cerrado. La cuota se genera el día siguiente a su corte.
La unicidad `(loan_id, period_start)` hace la generación idempotente entre réplicas.

**Migraciones:** `Tenancy` `AddLoansModuleKey` (el CHECK de `tenant_modules` admite `loans`) y `Loans` `InitialLoans` (esquema
`loans`).

**Avisos para quien opera:**

- **Orden de despliegue: primero backend y migración, después frontend.** El frontend consume estas rutas.
- **Un `INSERT` a mano en `loans.loans` debe llenar `outstanding_principal` y `last_generated_period_end`.** La base los admite
  sin valor por defecto de dominio, y el worker parte de ellos: con `last_generated_period_end` nulo genera desde el desembolso
  (duplicaría las cuotas importadas). `outstanding_principal` es el saldo que ve la pantalla, **no** la base del interés: el interés
  se calcula sobre el `balance_after` del último abono a capital anterior a cada tramo, o sobre `principal_amount` si no hay
  abonos (`LoanCapitalLedger.OpeningBalance`).
- **La carga inicial usa `is_imported = true`, `period_days` nulo y ningún tramo** (`installment_segments`), con `period_end` en
  fechas reales del calendario de corte: de ahí sale la fecha desde la que el worker sigue generando.
- **Fusión de clientes:** `CustomerMergeReferences` también mueve `loans.loans.customer_id` al cliente que queda, sin subir la
  versión del préstamo. Las cuotas no guardan cliente. La vista previa de la fusión no cuenta préstamos.
- **Retención de usuarios:** `LoansUserReferenceProbe` impide borrar a un usuario cuya membresía sea la encargada de un préstamo o
  haya marcado el pago de una cuota.

**Novasteel (carga inicial).** El script de carga se escribe cuando el owner entregue el Excel; todavía no existe. Las cuotas
importadas conservan el monto que trae el Excel: no se recalculan con la tasa ni con el capital del préstamo. Reglas de la carga:

- Todo abono a capital importado lleva `balance_after`: es la base del interés de los períodos siguientes.
- Toda cuota importada ya pagada lleva `paid_at`; sin él la API responde el año 0001 en `payment.paidAt`.
- Después de cargar se verifica que `loans.loans.outstanding_principal` sea igual al `balance_after` del último abono vigente de
  cada préstamo (o a `principal_amount` si no tiene abonos); si no coinciden, el saldo que ve la pantalla y el interés que cobra el
  worker divergen.

### Soporte

Módulo `Modules.Support.{Domain,Application,Infrastructure,Api}`, esquema `support`, clave de módulo `support` (sin dependencias,
fuera de `DefaultForNewTenants`: nace apagado y se prende por tenant). Es la fase 1 («núcleo») del spec
`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`: tickets creados a mano, bandeja con plazos en días hábiles,
estados, responsable, tipo y cliente con traza y auditoría, y los tipos de ticket del tenant. La fase 2 («Mensajería», ver
[Soporte y Mensajería](#soporte-y-mensajería-fase-2)) suma tickets desde una conversación, el seguimiento del hilo y las respuestas por
el hilo, la plantilla «Aviso de respuesta» y el correo. La fase 3 suma el formulario público ([Formulario público de
Soporte](#formulario-público-de-soporte-fase-3)); la encuesta llega después. Soporte no referencia Mensajería ni Customers: el calendario, los clientes y los miembros
entran por puertos con adaptador en Bootstrapper (`SupportBusinessCalendarSource`, `SupportCustomerLookup`, `SupportMemberDirectory`).

**Permisos y roles.** `support.ticket.read`, `support.ticket.manage` y `support.settings.manage` (`SupportPermissions`, categoría
«Soporte», los tres exigen el módulo `support`). De fábrica, `admin` tiene los tres; `advisor`, `read` y `manage`; `billing` y `cashier`, ninguno.
Las lecturas usan dos políticas que aceptan más de un permiso (`SupportPolicies`): `support.ticket.view` (`read` o `manage`) y
`support.settings.view` (`read`, `manage` o `settings.manage`). Quien ve tickets ve **todos** los del tenant: no hay «sólo los míos».
Con el módulo apagado, toda ruta responde 403.

**Rutas**, bajo `/api/v1/tenants/{tenantId}/support`. Un id de otro tenant en la ruta da `403 authorization.denied`; un ticket o tipo
que no es del tenant, 404. Todo `PUT` de un ticket, de un tipo o de los ajustes exige `If-Match` con la `version` (sin él, `428
precondition.if_match_required`; vieja, `412 concurrency.conflict`).

| Método y ruta | Política | Qué hace |
| --- | --- | --- |
| `GET /tickets` | `support.ticket.view` | Bandeja paginada con el resumen del tenant (`SupportTicketPageDto`) |
| `POST /tickets` | `support.ticket.manage` | Alta manual (`201` + `Location`, `SupportTicketDetailDto`) |
| `GET /tickets/{ticketId}` | `support.ticket.view` | Detalle con la traza y las transiciones permitidas |
| `PUT /tickets/{ticketId}/status` · `/assignee` · `/type` · `/customer` | `support.ticket.manage` | Cambia estado, responsable, tipo o cliente |
| `POST /tickets/{ticketId}/take` · `/extension` | `support.ticket.manage` | «Asignármelo» y la prórroga del plazo (fase 4a, `If-Match`) |
| `GET /assignees` | `support.ticket.view` | Miembros activos que pueden ser responsables (tienen `manage`) |
| `GET /ticket-types` | `support.ticket.view` | Todos los tipos, también los inactivos (el selector los marca), para el ticket |
| `GET` · `POST /settings/ticket-types` | `support.settings.manage` | Los mismos tipos para la tabla de ajustes, y alta de uno |
| `PUT /settings/ticket-types/{typeId}` | `support.settings.manage` | Edita un tipo (`If-Match`) |
| `DELETE /settings/ticket-types/{typeId}` | `support.settings.manage` | Borra un tipo que ningún ticket usa (fase 4a, `If-Match`, `204`) |
| `PUT /settings/ticket-types/order` | `support.settings.manage` | Reordena todos los tipos |
| `GET /settings` · `PUT /settings` | `support.settings.view` · `support.settings.manage` | Umbral de «por vencer» (con `ETag`) |

**Siembra perezosa.** Tenancy no avisa cuando se prende un módulo, así que la fila `support.tenant_settings` del tenant nace en su
primer acceso (`SupportProvisioner`, `INSERT … ON CONFLICT (tenant_id) DO NOTHING RETURNING`): sólo quien la inserta siembra los
ocho tipos, en este orden y todos activos y publicados: Petición general (15 días hábiles), Petición de documentos o información (10),
Consulta (30), Queja (15), Reclamo (15), Sugerencia (15), Felicitación (15) y Soporte técnico (sin término). Los siete primeros son
«legales»; los cinco primeros piden respuesta formal. Una consulta directa a `support.ticket_types` de un tenant que nunca abrió
Soporte devuelve cero filas: no es un error. Un tipo con tickets no se borra, se desactiva; uno sin tickets sí se borra desde la fase 4a
(ver [Gestión y plazos de Soporte](#gestión-y-plazos-de-soporte-fase-4a)). Un tipo sin término no vence y no
puede ser legal; el nombre es único por tenant sin distinguir mayúsculas (`409 support.ticket_type.name_taken`).

**Radicado.** `SOP-` más seis dígitos, por tenant (`SOP-000001` el primero). El contador es `next_ticket_number` de
`support.tenant_settings` y avanza en la misma transacción del alta: un alta que falla no gasta número.

**Estados.** `New`, `Open` («En gestión»), `WaitingOnCustomer`, `WaitingInternal`, `Resolved` y `Closed`, persistidos y enviados por
nombre. Las transiciones salen de la tabla del dominio (`SupportTicketStatuses`) y el detalle las manda en `AllowedTransitions`; una
fuera de la tabla es `422 support.ticket.transition_invalid`. Ningún estado vuelve a `New`, `Closed` es final y `Resolved` puede
reabrirse a `Open` (`ResolutionCount` cuenta las resoluciones). **Un ticket `Resolved` o `Closed` no se edita** (owner, 2026-11-15):
`PUT …/assignee`, `…/type` y `…/customer` responden `422 support.ticket.not_editable`, aunque el valor sea el mismo. Lo guarda el
agregado (`SupportTicket.Assign`, `ChangeType`, `LinkCustomer`), así que vale para cualquier camino nuevo; para editarlo hay que
reabrirlo (`Resolved` → `Open`). La fusión de clientes no pasa por ahí: mueve tickets en cualquier estado. **El expediente se cura
hasta resolver:** fijar o soltar un mensaje (`POST …/messages/{messageId}/pin` · `/release`) y «Seguir el hilo» (`PUT …/follow-thread`)
se permiten en `Resolved` y responden el mismo `422 support.ticket.not_editable` en `Closed`, también apagar un hilo que ya estaba
apagado, fijar uno ya fijado o soltar uno ya suelto (antes era `support.ticket.follow_closed`, que ya no existe). En fijar y soltar la
guarda va antes que cualquier otra regla: en un cerrado, el 422 sale aunque el ticket no tenga conversación o el mensaje no esté vinculado. Responder se permite en cualquier estado, y el worker del hilo, la
fusión y el borrado de clientes no pasan por esa guarda.

**Reloj de resolución.** Los términos cuentan **de lunes a viernes** (owner, 2026-11-15, spec §13.4): en un tenant cuyo país (ajustes
regionales) es `CO` se descuentan además los festivos nacionales de Colombia; en cualquier otro país, sólo lunes a viernes. **No usa
el horario de atención** de Mensajería: ni sus días, ni sus excepciones de fecha, ni su país de festivos (`SupportBusinessCalendar`, en
Bootstrapper). El plazo corre desde el día hábil **siguiente** a la radicación, en la zona horaria del tenant, y vence al
final del día hábil (`due_on`). Se detiene en `WaitingOnCustomer`, `Resolved` y `Closed` (no en `WaitingInternal`); al reanudar, el
vencimiento se corre por los días hábiles detenidos estrictamente entre la pausa y la reanudación (pausar y reanudar el mismo día
suma cero; no hay pausa parcial). Cambiar el tipo recalcula desde la radicación con el término nuevo más las pausas; editar un tipo no
mueve vencimientos ya calculados. El estado del plazo (`DueState`) es `NoTerm`, `OnTrack`, `DueSoon`, `Overdue`, `Paused` o `Finished`.

**Umbral de «por vencer».** `due_soon_business_days` en `support.tenant_settings`, 3 días hábiles por defecto, de 0 a 30; con 0 no
hay «por vencer» (un ticket pasa de `OnTrack` a `Overdue`). Lo usan el filtro `due=dueSoon`, el resumen de la bandeja y el
`DueState` del ticket. Guardarlo audita `support.settings.updated`.

**Bandeja** (`GET /tickets`). Filtros: `status` (uno o varios nombres separados por coma), `typeId`, `assignee` (`me`, `unassigned`
o el id de una membresía), `due` (`overdue`: reloj corriendo y vencido antes de hoy; `dueSoon`: reloj corriendo y vence entre hoy y
el umbral), `search`, `page` y `pageSize` (25 por defecto, hasta 100). **Un `search` que es un radicado se busca como radicado y
nada más**: no busca ese texto en el asunto ni en los nombres. Es radicado (`SupportTicketNumber.TryParse`) si, sin espacios a los
lados y quitando un `SOP-` inicial sin distinguir mayúsculas, queda sólo de dígitos ASCII que caben en un `long` y valen más de 0
(`SOP-000127`, `sop-127`, `127`). Un `0`, un `SOP-` sin dígitos o un número que no cabe en un `long` no es radicado y busca como
texto. Cualquier otro término busca en el asunto, en los nombres del solicitante (nombres, apellidos, razón social) y en el nombre de los clientes vinculados.
La búsqueda por cliente toma como mucho 200 clientes cuyo nombre contiene el término (`ISupportCustomerLookup.SearchLimit`, los
primeros por nombre y luego id): con un término muy amplio puede quedar fuera el ticket de algún cliente; el asunto y los nombres
del solicitante se buscan igual. El resumen (`Summary`) es del
tenant entero, no del filtro: los seis estados siempre, en el orden de arriba e incluso en cero, más los vencidos y los por vencer.

**Alta manual** (`POST /tickets`). Exige un cliente (`customerId`) **o** un solicitante con nombre (`FirstNames`, o `BusinessName`
con documento `NIT`); si no, `422 support.ticket.requester_required`. El celular del solicitante va en E.164 y el tipo de documento
tiene que valer para su país (por defecto, el del tenant). Vincular un cliente, al crear o con `PUT …/customer`, exige el módulo
`customers` prendido (`403 tenancy.module_not_enabled`) y un cliente activo del tenant (`422 support.ticket.customer_not_found` o
`support.ticket.customer_inactive`; un fusionado está inactivo). El canal de respuesta `Thread` no está disponible en un alta manual
(`422 support.ticket.reply_channel_unavailable`), `Email` exige correo y `WhatsAppTemplate` exige celular. **Con un cliente
vinculado, su contacto es el respaldo** (owner, 2026-11-14): el alta copia en la foto del solicitante el `email` del cliente en el
correo y su `phone_e164` (nunca el `phone` crudo) en el **fijo** (`landlinePhone`), sólo en los campos que `requester` dejó vacíos, y
después valida el canal contra esa foto. **El teléfono del cliente nunca va al celular** (owner, 2026-11-15): puede ser fijo, y una
plantilla de WhatsApp a un fijo la rechaza Meta y la respuesta queda `Failed`. Por eso `WhatsAppTemplate` sale sólo con el
`mobilePhone` que trae `requester` (o, en un ticket desde una conversación, el `wa_id`); sin él, `reply-options` dice `no_mobile` y el
asesor elige otro canal. Lo que trae `requester` siempre gana. Sin `requester`, la foto queda con sólo ese contacto y el país del
tenant; un valor del cliente que no cabe en la foto (correo de más de 254 o que no cumple el formato de correo que se le exige al de
`requester`, teléfono que no es E.164) no se copia. Sin correo en ninguno de los dos, `Email` responde ese 422; sin celular del
solicitante, `WhatsAppTemplate` también (`TicketRequesterNormalizer.WithCustomerContact`, amarrado por
`ALinkedCustomerEmailIsCopiedIntoAnEmptyRequesterForEmail`, `ALinkedCustomerWithoutContactAndNoRequesterContactIs422ForEmail` y
`ALinkedCustomerPhoneGoesToTheLandlineAndNeverToTheMobile`).
Vincular el cliente después, con `PUT …/customer`, no toca la foto ni el canal.

**Traza y auditoría.** La traza del ticket son sus eventos (`Created`, `StatusChanged`, `AssigneeChanged`, `TypeChanged`,
`CustomerChanged`), ordenados por un ordinal que asigna el agregado, no por hora. Cada cambio audita en la misma transacción:
`support.ticket.created`, `.status_changed`, `.assigned`, `.type_changed` y `.customer_changed`; en los tipos,
`support.ticket_type.created`, `.updated` y `.reordered`. Un cambio que no cambia nada no sube `version` ni audita. En esta fase no hay
notas internas.

**Clientes.** Cualquier ticket del cliente, en cualquier estado, bloquea su borrado (motivo `supportTickets`), y la fusión mueve
`support.tickets.customer_id` al cliente que queda, sin subir la `version` del ticket. La vista previa de la fusión no cuenta tickets.
Escribir `customer_id` toma el candado compartido del cliente (`CustomerReferenceLock`): un alta que llega mientras se borra su
cliente espera al borrado y falla con 422. Ese candado se toma antes del radicado, así la espera no frena las demás altas del tenant.

**Migraciones:** `Tenancy` `AddSupportModuleKey` (el CHECK de `tenant_modules` admite `support`; su `Down` borra las filas `support`
antes de volver al CHECK anterior), `Support` `InitialSupport` (esquema `support`: `tenant_settings`, `ticket_types`, `tickets` y
`ticket_events`) y `Support` `AddSupportProbeIndexesAndChecks` (los índices `IX_tickets_assignee` e `IX_ticket_events_actor` de la
sonda de usuarios, y los CHECK `CK_tickets_manual_medium` y `CK_ticket_events_kind`, armados con los nombres de sus enums).

**Avisos para quien opera:**

- **Orden de despliegue: primero backend y migración, después frontend.** El frontend consume estas rutas y el motivo de bloqueo
  `supportTickets` en el borrado de clientes.
- **Un `INSERT` a mano en `support.tickets` tiene que llenar todas las columnas `NOT NULL`** (el tipo va copiado en `type_name`,
  `term_is_legal` y `requires_formal_response`; `received_on`, `last_event_ordinal`, `version`) y un `number` libre del tenant: el
  índice único es `(tenant_id, number)` y el contador de `tenant_settings` no se entera.
- **Slugs reservados (fase 3).** Las rutas fijas del host del formulario público (`soporte.lulocrm.com/{tenantSlug}`, spec §9.2) no
  pueden ser slug de un tenant nuevo: `TenantSlugReservations.Reserved` (Tenancy.Domain) y `422 tenancy.slug.reserved` al registrar
  o crear. Un tenant que ya existía con uno de ellos sigue cargando, pero su formulario taparía una ruta del host: **antes de desplegar**
  se consulta producción y lo que salga se corrige a mano con el tenant:

  ```sql
  SELECT id, slug FROM tenancy.tenants
  WHERE slug IN ('api', 'app', 'assets', 'static', 'public', 'consultar', 'calificar', 'admin', 'www', 'soporte',
                 'support', 'pqrsf', 'qep', 'login', 'cuenta', 'health', 'status', 'robots', 'sitemap');
  ```

**Retención** (owner, 2026-11-15, spec §13.10; el abogado confirma): los tickets se conservan 10 años desde el cierre (Código de
Comercio art. 60; Ley 962 de 2005 art. 28); después se anonimizan los datos personales del solicitante y el ticket queda para
estadística. Los consentimientos se conservan mientras dure el tratamiento más ese mismo término. Todavía no hay purga: en esta fase
ningún ticket se borra.

#### Soporte y Mensajería (fase 2)

Plan `docs/superpowers/plans/2026-11-15-support-phase-2-messaging-backend.md` (spec §8.1 a §8.3 y §8.5). Soporte sigue sin referenciar
Mensajería ni Customers: los puertos de `Modules.Support.Application` (`ISupportConversationDirectory`, `ISupportThreadFeed`,
`ISupportConversationEvents`, `ISupportReplyChannels`, `ISupportReplyEmail`) tienen adaptador en Bootstrapper. Mensajería gana los
eventos del hilo `TicketOpened` y `TicketClosed` (con `TicketId`; el número se resuelve al leer), el emisor `ISupportReplyOutbound` y la
plantilla del sistema `SupportReply`.

**Rutas nuevas**, bajo `/api/v1/tenants/{tenantId}/support`. Toda acción que cambia el ticket exige `If-Match`.

| Método y ruta | Política | Qué hace |
| --- | --- | --- |
| `GET /conversations/{conversationId}/tickets` | `support.ticket.view` | Los tickets de la conversación y los del mismo cliente por otros canales, con `CanCreate` y su motivo |
| `POST /conversations/{conversationId}/tickets` | `support.ticket.manage` | Abre un ticket desde la conversación (exige `messaging`; `201`) |
| `GET /conversations/{conversationId}/unclassified` | `support.ticket.view` | Entrantes sin clasificar con sus tickets candidatos |
| `POST /conversations/{conversationId}/unclassified/{messageId}/assign` | `support.ticket.manage` | Lo asigna a uno (`{ ticketId, version }`) |
| `POST /conversations/{conversationId}/unclassified/{messageId}/dismiss` | `support.ticket.manage` | «De ninguno» |
| `GET /tickets/{ticketId}/messages?before=&take=` | `support.ticket.view` | Mensajes vinculados, del más nuevo al más viejo (50, hasta 100) |
| `POST /tickets/{ticketId}/messages/{messageId}/pin` · `/release` | `support.ticket.manage` | Fija o suelta un mensaje (`If-Match`) |
| `PUT /tickets/{ticketId}/follow-thread` | `support.ticket.manage` | Prende o apaga «Seguir el hilo» (`{ follow }`, `If-Match`) |
| `GET /tickets/{ticketId}/reply-options` | `support.ticket.view` | Qué canal puede usar el asesor y qué va a pasar (texto o plantilla) |
| `POST /tickets/{ticketId}/replies` | `support.ticket.manage` | Responde (`If-Match`; `201` con la fila, también si quedó `Failed`) |

**Seguimiento del hilo.** Los entrantes no se empujan desde la ingesta: `SupportThreadFollowWorker` lee por sondeo, con
`Support:Workers` en `appsettings.json` y defaults en código: `Enabled` (verdadero si falta: el worker se registra o no al arrancar),
`FollowIntervalSeconds` (5), `BatchSize` (200 entrantes por pasada) y `FeedLookbackHours` (24: cada pasada mira sólo los entrantes y
las filas sin clasificar que llegaron en esas horas, por `created_at`). Un entrante cuenta para un ticket no cerrado si cae en su ventana
`[follow_since, follow_off_at)`: con un candidato se vincula (`Linked`), con dos o más queda «sin clasificar», y una fila sin clasificar
pasa sola cuando le queda un único candidato. Un vínculo automático no sube la versión del ticket ni escribe evento; reabre a `Open` un
ticket `WaitingOnCustomer`. Lo que el worker acepta y no corrige:

- **Un entrante que siempre falla** se registra (sólo ids) y se salta en cada pasada; no frena el resto del lote.
- **Un ticket que apagó el seguimiento hace más de una hora** (`SupportThreadFeed.FollowOffGrace`) ya no hace recorrer su
  conversación: un entrante de antes del apagado que el worker no alcanzó a ver en esa hora queda sin vincular.
- **Un worker caído más de `FeedLookbackHours`** deja sin vincular los entrantes que llegaron mientras tanto (el asesor los fija a
  mano desde el hilo) y sin pasar solas las filas sin clasificar más viejas que eso (el asesor las asigna o las descarta).
- **Prender el seguimiento otra vez** abre una ventana nueva desde ese momento: lo que llegó con el interruptor apagado no se vincula,
  y un entrante de la ventana anterior que el worker todavía no procesaba (unos segundos de retraso) puede quedar fuera.
- **Una fila sin clasificar cuyos candidatos se cerraron todos** queda pendiente hasta que el asesor la descarte; no ocupa el lote.
- **Asignar desde «sin clasificar» mientras el worker la reasigna** puede responder `409 support.message.already_linked` al
  asesor: el mensaje ya quedó en el ticket.

Las pruebas de integración que necesitan controlar el worker lo sacan del host por tipo (`ImplementationType`), además de apagar la bandera.

**Respuestas.** Por el hilo dentro de la ventana de 24 h sale texto (o un adjunto, abajo); fuera, en WhatsApp sale la plantilla «Aviso
de respuesta» a la misma conversación y en Instagram es `422 support.reply.window_closed`. `WhatsAppTemplate` en un ticket sin
conversación manda la plantilla al celular de la foto por la conexión elegida, crea la conversación y el ticket empieza a seguirla.
`Email` va al correo de la foto. Responder no toma la conversación, no cambia su estado ni su asignación y no resta no leídos. Cada
respuesta es una fila de `support.ticket_replies` que se confirma `Pending` **antes** de llamar afuera; su `id` es el `clientId` que viaja
a Mensajería, idempotente por él. Un reintento con el mismo `clientId` devuelve la fila guardada sin mirar el `If-Match`; una `Pending` de
menos de 60 s se devuelve sin enviar (la está mandando otro request) y una más vieja se retoma con exactamente lo que guardó su fila: el
canal, la entrega, la conexión, el texto, el adjunto (`upload_id`, `file_name`; el del reintento se ignora) y, en una plantilla al
celular, el `target_wa_id` que se tomó de la foto al crearla (sin volver a revisar el celular, la conexión ni la aprobación de la
plantilla: Mensajería devuelve primero lo que ya salió con ese `clientId`). Si el canal la rechaza o ya no tiene ruta, queda `Failed`
con el motivo. La fila trae su `clientId` en el detalle y en el resultado, para que la pantalla retome una que quedó `Pending`, y
`hasAttachment` (`true` si se mandó o se intentó con archivo, leído del `upload_id` guardado), para que «Reintentar» de una fallida no la
reenvíe como texto solo, también después de recargar. Una
`Failed` trae `messageId` si Mensajería registró el mensaje (no se fija en el expediente), y un mensaje se fija sólo si salió por la
conversación del ticket. **El correo no es idempotente**: una `Pending` de correo de más de 60 s se
vuelve a mandar al retomarla, y el solicitante puede recibirlo dos veces.

**`failureCode` de una respuesta `Failed`** tiene dos orígenes, y la pantalla tiene que traducir los dos: un motivo de Mensajería o del
correo cuando el canal la rechazó o falló (`send_failed`, `window_closed`, `contact_blocked`, `connection_unavailable`,
`attachment_unavailable`…), o un código de error de Soporte cuando un retomado ya no tenía ruta (`support.ticket.no_conversation`,
`support.ticket.reply_channel_unavailable`).

**Códigos de falla.** Todo código que la pantalla lee de Soporte —el `failureCode` de una respuesta, el `ConsultAccess.FailureCode` de la
información con el PIN, el `reason` de `reply-options` y el `cannotCreateReason` de la pestaña de Soporte de una conversación— sale de
una sola lista, `SupportChannelReasons`
(`Modules.Support.Application/ISupportReplyChannels.cs`). Los que vienen de Mensajería son copia de `SupportReplyReasons`, amarrada por
`SupportFailureCodeParityTests`. El frontend traduce por esta lista.

| Código | Cuándo | Dónde se ve |
| --- | --- | --- |
| `window_closed` | La ventana de 24 h está cerrada | Respuesta, PIN, `reply-options` |
| `contact_blocked` | El contacto está bloqueado | Respuesta, `reply-options` |
| `template_not_approved` | La conexión no tiene aprobada «Aviso de respuesta» | Respuesta, `reply-options` |
| `template_text_invalid` | El texto no cabe en un parámetro de Meta | Respuesta |
| `recipient_invalid` | El nombre o el radicado no caben en la plantilla | Respuesta |
| `text_invalid` | El texto no cumple la regla del canal (tope de Instagram) | Respuesta, PIN |
| `connection_unavailable` | La conexión está pausada, borrada o pide atención | Respuesta, PIN, `reply-options` |
| `conversation_not_found` | La conversación ya no existe | Respuesta, PIN |
| `attachment_unavailable` | El adjunto no está disponible para esa conversación | Respuesta |
| `channel_unsupported` | Instagram no tiene plantillas | Respuesta |
| `customer_not_found` | Abrir la conversación chocó con un cliente borrado | Respuesta |
| `send_failed` | El canal intentó y no salió | Respuesta, PIN |
| `no_conversation` | El ticket no tiene conversación | `reply-options` |
| `messaging_disabled` | Mensajería está apagada en el tenant | `reply-options`, ficha de la conversación |
| `no_mobile` | La foto no tiene celular para la plantilla | `reply-options` |
| `no_email` | La foto no tiene correo | `reply-options` |
| `no_author` | Ningún miembro activo puede firmar el envío por Mensajería (plan F11) | PIN |
| `portal_only` | Un anónimo sólo se responde por la página de consulta | `reply-options` |
| `portal_unavailable` | La página de consulta sólo responde a anónimos | `reply-options` |
| `no_permission` | Quien mira no tiene `support.ticket.manage` (ya existía como literal; ahora vive en la lista) | Ficha de la conversación |
| `send_pending` | «Reenviar PIN» reclamó el envío y todavía no sabe cómo terminó; si el proceso muere en el medio, queda así | PIN |

**Adjuntos (sólo WhatsApp).** Una respuesta por el hilo de una conversación de WhatsApp, dentro de la ventana, puede llevar un archivo:
la pantalla lo sube con la sesión de subida de Mensajería
(`POST /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/messages/media/upload-session`, que exige
`messaging.conversation.manage`) y manda `uploadId` y `fileName` en la respuesta. En WhatsApp el texto de la respuesta va como pie (hasta
1024). Instagram no lleva pie en un adjunto y el texto se perdería, así que ahí no se adjunta: `canAttach = false` y un `uploadId` es
`422 support.reply.attachment_unavailable`. Sin el permiso de Mensajería, `reply-options` también responde `canAttach = false`. Un
`uploadId` de otra conversación o que subió otro asesor, vencido o ya usado, sin `fileName`, un tipo que el canal no acepta o un audio
(no lleva pie) es `422 support.reply.attachment_unavailable`.

**Plantilla «Aviso de respuesta».** `MessageTemplateKind.SupportReply`, nombre `qep_soporte_respuesta` (versiones `_v2`…, prefijo
reservado), idioma `es`, `UTILITY`, sólo cuerpo, con las variables fijas más `radicado` y `respuesta`. Se aprovisiona **por conexión**:
`POST /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management/support-reply` (cuerpo opcional `{ body }`) y
su borrador en `GET …/support-reply/draft`. Hasta que Meta la apruebe en una conexión, fuera de la ventana no sale nada por ella
(`template_not_approved`). El texto base termina en una frase fija y no en `{{respuesta}}`: Meta rechaza cuerpos que empiezan o terminan
con una variable.

**Migraciones:** `Support` `AddSupportMessaging` (conversación y ventana de seguimiento en `tickets`, `ticket_messages`,
`unclassified_messages`, `ticket_replies`) y `Messaging` `AddSupportReplyTemplateKind` (el CHECK de `message_templates.kind` e
`UX_message_templates_support_reply`).

#### Formulario público de Soporte (fase 3)

Plan `docs/superpowers/plans/2026-11-15-support-phase-3-public-form-backend.md` (spec §9); los documentos legales, plan
`docs/superpowers/plans/2026-11-15-support-portal-legal-documents-backend.md` (spec `2026-11-15-support-portal-legal-documents-design.md`).
La página es el portal `qep-frontend-support` (`soporte.lulocrm.com/{tenantSlug}`) y
llama directo desde el navegador, sin BFF, a un grupo **anónimo**: `/api/v1/public/support/{tenantSlug}`. Ningún handler del grupo lee el
contexto de ejecución ni la cookie de sesión, no hay CSRF (exención por prefijo, `SupportPublicPaths`), toda respuesta —también 404, 413
y 429— lleva `Cache-Control: no-store` y ningún cuerpo pasa de 64 KB (`413 support.public.body_too_large`; un JSON ilegible es `400
support.public.body_invalid`). Un slug que no es de nadie, un tenant inactivo y el módulo `support` apagado responden el mismo `404
support.public.not_found`, con el mismo cuerpo, en todas las rutas. **La página de consulta y la información con el PIN funcionan sin
publicar el formulario** (owner, 2026-11-15): `GET /consult`, `POST /tickets/lookup` y `POST /tickets/resend-pin` sólo exigen tenant
activo y módulo prendido (`SupportPublicResolver.ResolveForConsultAsync`), así todo solicitante tiene desde el primer día un enlace de
consulta que sirve. Publicar sólo abre el formulario: `GET /form`, la geografía, radicar y los adjuntos responden ese mismo 404 mientras
la versión vigente no esté publicada (`ResolveAsync`).

**Rutas públicas** (relativas al grupo). Los limitadores son por IP del visitante (`RemoteIpAddress`, ya corregida por `X-Real-IP` detrás
del ingress) y cada uno tiene su propia cubeta.

| Método y ruta | Cuerpo | Éxito | Turnstile | Límite por IP |
| --- | --- | --- | --- | --- |
| `GET /form` | — | Configuración, tipos publicados, campos por variante, tipos de documento por país, `legal`: los tres documentos vigentes y el texto de caracterización (abajo), y `organization`, la empresa que responde | No | 60 / min |
| `GET /consult` | — | `{ tenantName, logoUrl, accentColor, organization }` para dibujar la página de consulta; `accentColor` y `organization` son los de la versión vigente del formulario, publicada o no, y `null` sin ninguna | No | 60 / min |
| `GET /geography/departments` · `/departments/{id}/cities` | — | Departamentos y ciudades de Colombia | No | 60 / min |
| `POST /attachments` | `{ name, mimeType, sizeBytes }` | `201` `{ fileId, uploadKey, uploadUrl, headers }` | No | 20 / 10 min |
| `POST /attachments/{fileId}/complete` | `{ uploadKey }` | `200` `{ status }`: `available` o `rejected` | No | 20 / 10 min |
| `POST /tickets` | El radicado, con `attachments: [{ fileId, uploadKey }]` y `clientRequestId` opcionales | `201` `{ number, pin, receivedAt, dueOn, termBusinessDays, termExtended, termIsLegal, successMessage, pinEmailed, consultUrl }` | Sí (no en un reintento, abajo) | 5 / 10 min |
| `POST /tickets/lookup` | `{ number, pin, turnstileToken }` | `200` radicado, tipo, estado, radicación, plazo (`termIsLegal`, `termBusinessDays`, `termExtended`) y respuestas | Sí | 10 / min |
| `POST /tickets/resend-pin` | `{ number, turnstileToken }` | `202` vacío, siempre | Sí | 5 / 10 min |

**Documentos legales del portal** (spec 2026-11-15). `GET /form` trae en `legal` los tres documentos vigentes del tenant, cada uno
**saneado otra vez al servirlo** (`ISupportHtmlSanitizer`, lista blanca `p br strong em u s h2 h3 h4 ul ol li blockquote hr` y `a` con
`https`/`mailto`/`tel`): `privacyNotice` `{ version, contentHtml, authorizationQuestion, policyUrl }`, `anonymousNotice` y `terms` `{ version,
contentHtml }`, y `characterizationNotice`, un texto plano fijo e igual para todos los tenants que va al pie del paso de datos.
`authorizationQuestion` llega resuelta: la del tenant o «¿Autorizas a {nombre del tenant} para el tratamiento de los datos personales
suministrados en este formulario?». `policyUrl` `null` es que el paso no muestra el enlace a la política. Si al tenant le falta cualquiera
de los tres, o si uno guardado ya no pasa el saneador (sin texto visible, demasiado grande, demasiado anidado o inestable: sólo una fila
escrita a mano), `GET /form` responde el mismo `404 support.public.not_found` que con el formulario sin publicar. La página pinta
`contentHtml` como HTML y no lo vuelve a sanear. La salida del saneador se guarda en una caché acotada por pod, con clave SHA-256 del
contenido guardado: una fila cambiada a mano tiene otra clave y se vuelve a sanear.

**Variables de los documentos legales** (spec `2026-11-16-support-legal-document-variables-design.md`). Los tres documentos y la
pregunta de autorización del tenant aceptan `{{empresa}}`, `{{tipo_documento}}`, `{{documento_identificacion}}` (la empresa que
responde: `name`, `documentTypeCode` y `documentNumber` de `organization`) y `{{nombre_comercial}}` (el nombre del tenant), con espacios
opcionales adentro (`{{ empresa }}`). La lista vive en `SupportLegalVariables.All` y la consola la recibe en `variables` de
`GET …/settings/legal-documents`. Al publicar, todo `{{…}}` que no sea uno de esos nombres, en el HTML saneado o en la pregunta, es
`422 support.legal_document.unknown_variable` (`{{Empresa}}` y `{{}}` incluidos); lo guardado y lo que lee la consola conserva los
tokens. **Se resuelven en un solo lugar, `SupportServedLegal.ReadAsync`**, con la empresa de la versión publicada del formulario: en
`GET /form` y en la foto de cada aceptación, que guarda lo resuelto. En el HTML los valores van codificados (`&` → `&amp;`, `<` →
`&lt;`…); en la pregunta, tal cual, porque el portal la pinta como texto. La pregunta por defecto no tiene variables. Un token de la
empresa en un formulario sin empresa (`organization: null`), o un texto que resuelto pasa de 60 000 caracteres (HTML) o de 500
(pregunta), da el mismo `404 support.public.not_found`. Un token desconocido en una fila escrita a mano se sirve literal.

**La empresa que responde** (NIT del portal, spec 2026-11-15 §9.3, D-6 y D-8). El tenant elige en Ajustes › Formulario público una de sus
empresas de Companies (`organizationCompanyId` en `PUT /settings/public-form`), y el portal muestra `organization: { name,
documentTypeCode, documentNumber }` (`Company.Name`, `Company.DocumentType.ToWire()` y `Company.TaxId`) en `GET /form` (versión publicada)
y en `GET /consult` (versión vigente, publicada o no). **Es obligatoria para publicar y mientras está publicado** (`organization_required`: tampoco se puede guardar un formulario publicado sin empresa, porque las variables de los documentos la usan; despublicarlo sin empresa sí). Cambiarla a una que no
existe, es de otro tenant o está inactiva es `422 support.form.organization_unavailable`; una elegida que después se desactiva **se sigue
mostrando** y no frena editar el formulario (sólo aparece `organization_inactive` en `publishBlockers`, que impide volver a publicar uno
sin publicar). **Una empresa que alguna versión del formulario eligió no se puede borrar**: `support.form_versions.organization_company_id`
tiene FK real a `companies.companies` con `ON DELETE RESTRICT`, y `DELETE …/companies/{id}` responde `422 companies.company.in_use` (sí se
puede desactivar). Soporte la lee por el puerto `ISupportOrganizationDirectory`, cuyo adaptador vive en Bootstrapper; no referencia
Companies. `organization: null` = formulario publicado antes de este cambio o tenant que nunca eligió: el portal no pinta la línea.

**Radicar.** Con solicitante o anónimo. Los campos que se piden y cuáles son obligatorios los dice la versión vigente del formulario
(`SupportFormFields`); el tipo de documento sale de `DocumentTypesByCountry`; el celular se guarda en E.164; en Colombia la ciudad es de
Geography (`cityId`) y fuera, texto libre (`cityName`). Cada camino manda las versiones de sus documentos, las que la página cargó: con
solicitante `privacyNoticeVersion` y `termsVersion` (`anonymousNoticeVersion` en `null`, `acceptsDataPolicy: true`); anónimo
`anonymousNoticeVersion` y `termsVersion` (`privacyNoticeVersion` en `null`; `acceptsDataPolicy` se ignora). Los dos mandan
`acceptsTerms: true`. Si `formVersion` o alguna versión que aplica ya no es la vigente, `422 support.public.form_outdated` o
`support.public.legal_texts_outdated` y la página recarga. `acceptsDataPolicy` o `acceptsTerms` en falso, una versión que falta o una que
no aplica salen como **`422 validation.failed`** con el campo en `errors` (`AcceptsDataPolicy`, `AcceptsTerms`, `PrivacyNoticeVersion`,
`AnonymousNoticeVersion`, `TermsVersion`), no con un código propio: `ApiExceptionHandler` no publica el `ErrorCode` del validador. Todo lo
que puede responder 422 va antes de la transacción, así un rechazo no gasta radicado. En la misma transacción que el ticket se guarda
una fila de `support.legal_acceptances` por documento aceptado, con el HTML **tal como lo sirvió** `GET /form`: con solicitante, el aviso de
privacidad (IP, pregunta mostrada, URL y caracterización) y los términos con la IP; anónimo, el aviso anónimo y los términos, **los dos sin
IP**. Con solicitante y `customers` prendido y un único cliente con ese documento, el ticket queda vinculado; con cero o varios, el
detalle los ofrece como candidatos. **Un anónimo** no guarda cliente —tampoco se lo vincula la consola: `PUT …/tickets/{id}/customer`
responde `422 support.ticket.anonymous_has_no_customer` y el detalle no ofrece candidatos—, foto del solicitante, documento ni IP, y sólo
se responde por la página de consulta (canal `Portal`). La base lo respalda con `CK_tickets_anonymous` (migración
`AddSupportAnonymousTicketCheck`): un anónimo no puede tener `customer_id` ni ninguna columna `requester_*`, tampoco por SQL crudo, y con
`CK_legal_acceptances_ip`: el aviso anónimo nunca lleva IP y el de privacidad siempre. Una aceptación por ticket y tipo
(`IX_legal_acceptances_ticket_kind`, `409 support.public.legal_acceptance_duplicate`, que hoy ningún camino alcanza).

**Fecha de radicación y término** (spec 2026-11-15 §9.1, D-6). La respuesta del radicado trae `receivedAt` (la radicación),
`termBusinessDays` (`null` = el tipo no tiene término) y `termIsLegal` («plazo legal» u «objetivo»); la de `POST /tickets/lookup` suma
`termBusinessDays`. Los dos salen de la **foto del tipo en el ticket**, nunca del tipo vivo: editar el tipo después no cambia lo que ve el
solicitante. **Con prórroga** (D-11, owner 2026-11-16): `termBusinessDays` es el término vigente, la foto más los días hábiles de la
prórroga (`ExtensionBusinessDays`), y las dos respuestas traen `termExtended` (`true` si el plazo se prorrogó), así cuadran con `dueOn`, que
ya la incluye.

**Radicado idempotente** (`clientRequestId`, spec 2026-11-15 §9.2, D-7). El portal genera un UUID al empezar a radicar y lo manda en cada
reintento de ese mismo radicado (timeout, red caída, 5xx). Un reintento con la misma clave en el mismo tenant responde `201` con el mismo
`number`, `receivedAt`, `termIsLegal`, `successMessage` (el de la versión del formulario con que se radicó) y `consultUrl`, y con `dueOn`,
`termBusinessDays` y `termExtended` de ahora (con la prórroga, si la hubo entre los dos intentos): no crea otro ticket, no gasta número y no vuelve a mandar el correo de apertura. **La clave se mira antes de Turnstile**, del
validador y de las versiones, **y el cuerpo no se compara** (como el checkout de la tienda). Eso salta Turnstile **sólo cuando el primer
intento ya confirmó**: mientras el original sigue corriendo, la clave todavía no existe, y un reintento con el token ya gastado responde
`422 support.public.captcha_invalid`. El portal entonces vuelve a correr Turnstile y reenvía con el **mismo** `clientRequestId`.
Dos `POST` simultáneos con la misma clave dan un solo ticket: dentro de la transacción, después de tomar el número (el `UPDATE … RETURNING`
del contador serializa las altas del tenant), la clave se vuelve a buscar; si otro la confirmó, la transacción se deshace (el número
vuelve) y se responde el ganador. El índice único parcial `IX_tickets_tenant_client_request` es el respaldo. Sin clave, cada `POST` es
un radicado nuevo; la clave no se audita ni va a la traza, y el reintento cuenta para el limitador. **`clientRequestId` es un UUID v4**
(decisión del owner D-10, 2026-11-15; lo que da `crypto.randomUUID()`, como las claves de subida): vacío (`00000000-…`) u otra versión (un
v7, por ejemplo) es `422 validation.failed` con `errors.ClientRequestId`. **El portal nunca tiene dos radicados en vuelo** y pinta sólo la
última respuesta: un reintento de un anónimo rota el PIN, así que el PIN de una respuesta anterior ya no sirve.

| El PIN del primer intento | `pin` del reintento | `pinEmailed` |
| --- | --- | --- |
| Salió por correo (`last_sent_channel = Email`, sin fallo) | `null`: no se vuelve a mostrar ni a mandar | `true` |
| El correo del primer intento todavía no se anotó, y el PIN se emitió hace 10 minutos o menos (en vuelo) | `null`: un PIN nuevo invalidaría el que va en el correo | `true` |
| No salió por ningún canal (anónimo, `send_failed`, `send_pending`), o el correo sigue sin anotarse más de 10 minutos después del PIN (el proceso murió entre confirmar y anotar) | Uno **nuevo**; el anterior deja de servir. Sólo en la respuesta, sin correo | `false` |

**Turnstile se gasta antes de validar los adjuntos.** El token es de un solo uso y radicar lo verifica primero (anti-abuso: nada más
corre sin él). Por eso **la página reinicia el widget después de cualquier `422` de `POST /tickets`** —un adjunto inválido, un campo, la
versión del formulario—, no sólo después de `support.public.captcha_invalid`: reintentar con el mismo token responde
`captcha_invalid` aunque el resto ya esté bien.

**PIN.** Seis dígitos aleatorios. Todo ticket nace con uno, también el manual y el que se abre desde una conversación, y **sólo se guarda
su hash** (PBKDF2, `support.ticket_access.pin_hash`). La clave vive en `ticket_access` y no en el ticket, para que un
intento fallido no le suba la versión. Consultar con radicado y PIN: un radicado que no existe, un PIN errado y un radicado bloqueado
responden lo mismo, `422 support.public.lookup_failed`, y los tres pagan el mismo PBKDF2 y hacen las mismas lecturas (el contexto de
plazos y la búsqueda de la clave; un radicado mal escrito busca el 0, que no existe). **Cinco fallos seguidos bloquean ese radicado
15 minutos**, también para el PIN correcto; un acierto pone el contador en cero. Mirar el bloqueo y anotar el intento ocurren con la fila
de `ticket_access` tomada (`SELECT … FOR UPDATE`), así N intentos a la vez cuentan N. «Reenviar PIN» responde siempre `202`: si el ticket
tiene a dónde (ver abajo) y pasaron 10 minutos desde el último envío, genera un PIN nuevo y lo manda **al destino registrado, nunca a uno
del visitante**; el nuevo reemplaza al viejo sólo si salió, y reenviar no desbloquea. El reenvío reclama el envío antes de mandar
(enfriamiento y `send_pending`, confirmados) y anota el resultado real después: si el proceso muere entre los dos, la consola ve
`send_pending`, nunca un envío exitoso que no ocurrió. La consola ve en el detalle (`ConsultAccess`) cuándo y por qué canal salió la
información y por qué no, nunca el PIN.

**A dónde va la información con el PIN** (`SupportOpeningSender`, plan F10, F11 y F23 a F26). La misma regla al crear el ticket y al
reenviar el PIN; al crear, sale **después** de confirmar el ticket y anota su resultado en un segundo guardado que toca sólo
`ticket_access`: el ticket queda creado pase lo que pase con el envío, y la `version` del `201` sigue valiendo para el siguiente
`If-Match`. Sale aunque el formulario del tenant no esté publicado: la página de consulta no depende de él (owner, 2026-11-15).

| Ticket | Canal (`last_sent_channel`) | Qué sale |
| --- | --- | --- |
| Del formulario, con solicitante | `Email` | El correo de apertura al correo de la foto. El PIN además viaja en la respuesta del radicado |
| Del formulario, anónimo | — | Nada: el PIN sólo se ve en la respuesta del radicado |
| Manual con canal `Email` | `Email` | El correo de apertura al correo de la foto |
| Manual con canal `WhatsAppTemplate` o `Thread`, sin conversación | — | **Nada** (owner, 2026-11-15): mandar a un celular nuevo pide elegir conexión y no cabe en el alta. El PIN se emite igual |
| Desde una conversación, o manual que ya vinculó una (si su canal no es `Email`) | `Thread` | Texto por el hilo, sólo con la ventana de 24 h abierta; cerrada, nada (`window_closed`). No hay plantilla de apertura (F24) |

El texto por el hilo, tuteando: `Registramos tu caso {radicado} ({tipo}). Fecha límite de respuesta: {fecha}. Consulta su estado en
{enlace} con la clave {pin}.` La fecha va en el formato regional del tenant y, si el tipo no tiene término, dice `sin fecha límite`. El
enlace de consulta nunca lleva el PIN. Mensajería exige un autor para la fila del mensaje: firma quien creó el ticket si sigue activo; si
no, su responsable; si tampoco, el primer gestor en el orden de `GET /assignees`. Sin ninguno no sale (`no_author`). El `clientId` del
mensaje sale del ticket y de la emisión del PIN (`pin_issued_at`): un reintento de la misma emisión no manda dos veces, y un reenvío es
otro mensaje. El texto queda en el hilo de Mensajería como cualquier mensaje saliente, con el PIN: es el canal que lo entrega.

**Adjuntos** (plan F30 a F37). Pasan por el pipeline de Storage —staging, magic bytes, antivirus, cuarentena, promoción a `files/` y
barrido— con el dueño `FileOwnerType.SupportTicket`, por la subida sin miembro `IStorageSystemUploads` (Storage.Application, sin endpoint
propio) y el adaptador `SupportAttachmentStorage` de Bootstrapper. El flujo de la página: abrir (`POST /attachments`), `PUT` del archivo
directo al bucket privado de R2 con `uploadUrl` y los `headers` tal cual (`Content-Type` e `If-None-Match: *` van firmados), completar y,
si sale `available`, mandar el par al radicar.

- **El tenant los permite o no** (spec §9.3). `allow_attachments` es de cada versión del formulario (`PUT /settings/public-form`,
  `allowAttachments`, obligatorio como los demás interruptores) y arranca **prendido** (owner, 2026-11-15; también el default de la
  columna): el tenant los apaga si no los quiere. `GET /form` lo trae en `attachments` (`{ allowed, maxFiles, maxSizeBytes, mimeTypes }`):
  la página esconde el control con `allowed` y arma su `accept` y sus avisos con el resto, sin una lista propia. Apagado, abrir,
  completar y radicar con una lista de adjuntos no vacía responden `422 support.public.attachments_disabled`, antes de tocar Storage;
  radicar sin adjuntos sigue igual.

- **Límites** (`SupportPublicAttachmentRules`): hasta **3** archivos por ticket, **10 MB** cada uno, nombre simple de hasta 260 y sólo
  **PDF, JPEG, PNG y WebP** (`.pdf`, `.jpg`, `.jpeg`, `.png`, `.webp`; `application/pdf`, `image/jpeg`, `image/png`, `image/webp`).
  `image/jpg`, que no es un tipo registrado pero algunos navegadores mandan, se toma como `image/jpeg`. Abrir fuera de eso es `422
  validation.failed` con el campo (`Name`, `MimeType`, `SizeBytes`); cuatro o un par repetido al radicar, con `Attachments`. La lista es
  un subconjunto de la de Storage (`SupportAttachmentParityTests`).
- **Sin Word ni Excel mientras `Storage:ClamAv:Enabled` esté apagado en producción** (owner, 2026-11-15): un adjunto anónimo con macros
  lo abre un asesor. Sumar un tipo de Office exige prender ClamAV primero.
- **Tope por tenant** (owner, 2026-11-15): el limitador por IP no frena a muchas IPs a la vez llenando el bucket. Un tenant tiene a lo
  sumo **200** subidas abiertas en las últimas 24 h que ningún ticket reclamó (`MaxPendingPerTenantPerDay`; las reclamadas no cuentan);
  pasado eso, abrir responde `422 support.public.attachments_busy`, sin tocar Storage, y la página dice «Estamos recibiendo muchos
  archivos. Intenta más tarde.». Es un tope blando a propósito: cuenta y después abre, así que entran
  unas pocas de más bajo ráfaga simultánea; un candado por tenant en un endpoint anónimo expondría el pool de conexiones de toda la API
  (quien espera retiene una conexión y quien lo tiene necesita otra para Storage). El conteo es SQL crudo en `SupportAttachmentStorage` (Bootstrapper) sobre `storage.file_resources` (servido por
  `IX_file_resources_tenant_owner_created`) y `support.ticket_attachments`.
- **`uploadKey`** es un GUID aleatorio (v4) que sólo recibe quien abrió la sesión y que Storage guarda como `OwnerId` del archivo. Completar
  y radicar exigen el par `(fileId, uploadKey)` del mismo tenant: otra clave, un id que no existe, un archivo sin subir o en cuarentena
  responden lo mismo, `422 support.public.attachment_invalid`. Completar antes de que termine el `PUT` es `428
  support.public.attachment_not_uploaded`. Un archivo `rejected` no es un error de la llamada: la página avisa antes de radicar.
- Los adjuntos se insertan en `support.ticket_attachments` en la misma transacción del ticket. El único `IX_ticket_attachments_file`
  impide que dos radicaciones reclamen el mismo archivo y se traduce por nombre al mismo 422; la radicación entera se deshace.
- **Lo que nadie reclama se purga.** El barrido de staging de Storage borra, pasada la retención (`Storage:StagingRetentionHours`, 24
  h), el adjunto `Available` que ninguna sonda retiene; la de Soporte (`SupportAttachmentReferenceProbe`) retiene los reclamados. Mira
  sólo los de la última semana pasada la retención: un adjunto sin reclamar más viejo (el worker caído más de siete días) queda sin purgar.
  La fila se guarda (`Purged`) antes de borrar los objetos: si borrar falla, queda un objeto huérfano en el bucket y un log de error,
  nunca una fila `Available` que apunta a un objeto borrado. Queda abierta una carrera en el borde de las 24 h: un radicado que reclama
  el archivo justo después de que la sonda dijo «nadie lo retiene» y antes de que la purga se guarde. Exige que la página espere 24 h
  entre subir y radicar.
- **Consola:** el detalle del ticket trae `attachments` (`id`, `fileName`, `mimeType`, `sizeBytes`) y
  `GET /api/v1/tenants/{tenantId}/support/tickets/{ticketId}/attachments/{attachmentId}/download` (`support.ticket.view`) responde `{ url }`
  firmada de 5 minutos, sin pedir `storage.file.read`; audita `support.ticket.attachment_downloaded`. Otro tenant es 403; un adjunto que
  no es del ticket, `404 support.attachment.not_found`.
- **Los archivos `SupportTicket` no se ven desde la consola de Storage.** Son dueño de sistema (`SystemOwnedFiles`): `GET /files` no
  los lista, cada ruta por id de `/files/{fileId}` (descarga, publicación, metadatos, completar, cancelar, borrar) responde `404
  storage.file.not_found` aunque el miembro tenga todo `storage.file.*`, y `POST /files` con `ownerType: SupportTicket` es `422
  storage.file.owner_type_invalid`. Tampoco los ven Catalog (foto de producto) ni Quotations (adjunto de pedido). Sólo llegan por las
  rutas de Soporte.

**Rutas de la consola** (fase 3), bajo `/api/v1/tenants/{tenantId}/support`:

| Método y ruta | Política | Qué hace |
| --- | --- | --- |
| `GET` · `PUT /settings/public-form` | `support.settings.view` · `support.settings.manage` | Configuración del formulario; cada guardado crea una versión. Publicar exige los tres documentos (`privacy_notice_required`, `anonymous_notice_required`, `terms_required` → `422 support.form.legal_texts_required`) y la empresa que responde (`organization_required`, `organization_inactive` → `422 support.form.organization_required` / `organization_inactive`). La respuesta trae `organization` (la elegida, con `isActive`) y `organizationOptions` (las activas del tenant, por nombre: el selector no necesita `companies.company.read`); el cuerpo del `PUT` suma `organizationCompanyId` (`null` = sin empresa) |
| `GET /settings/legal-documents` | `support.settings.view` | Los tres documentos vigentes; el que no tiene versión trae su ejemplo (`version: 0`, `isExample: true`). Al final, `variables: [{ name, label, example }]`, las que el editor puede insertar |
| `GET /settings/legal-documents/{kind}/versions` · `…/versions/{version}` | `support.settings.view` | Historial de un tipo, de la más nueva a la más vieja, y una versión vieja |
| `POST /settings/legal-documents/{kind}` (`If-Match`, `0` sin versión) | `support.settings.manage` | Publica la versión `n+1` del tipo, saneada y con sus variables sin resolver; la respuesta es lo guardado. Una variable que no está en la lista es `422 support.legal_document.unknown_variable` |
| `GET /tickets/{ticketId}/legal-acceptances/{kind}` | `support.ticket.view` | La foto de lo que el solicitante aceptó: HTML servido, pregunta, URL, caracterización e IP. Sin fila, `404 support.legal_acceptance.not_found` |
| `GET /tickets/{ticketId}/attachments/{attachmentId}/download` | `support.ticket.view` | URL firmada del adjunto (arriba) |

`{kind}` ∈ `privacy-notice`, `anonymous-notice`, `terms` (otro es `404 support.legal_documents.kind_not_found`); en los cuerpos viaja como
`PrivacyNotice`, `AnonymousNotice`, `Terms`. El detalle del ticket trae `legalAcceptances` (`kind`, `version`, `acceptedAt`, `visitorIp`, sin el
contenido), en ese orden y `[]` en un ticket manual. Las rutas `…/settings/legal-texts` de la fase 3 ya no existen. **`contentHtml` llega
saneado en toda lectura** de la consola (documento vigente, versión vieja, respuesta del `POST` y foto de una aceptación): lo guardado ya es
punto fijo del saneador, así que los bytes no cambian y la prueba guardada no se toca. `authorizationQuestion`, `organization.*`,
`tenantName` y `successMessage` son texto plano: nunca van a `innerHTML`. El autor (`createdBy`) trae `isMe` contra quien lee.

**Configuración** `Support:PublicForm` (`appsettings.json`, defaults en código):

| Clave | Default | Para qué |
| --- | --- | --- |
| `BaseUrl` | `https://soporte.lulocrm.com` | Arma el enlace de consulta (`{BaseUrl}/{slug}/consultar?radicado=SOP-…`, sin PIN) |
| `Hostname` | `soporte.lulocrm.com` | El hostname que Turnstile tiene que reportar; se exige sólo en `Production` |
| `AllowedOrigins` | vacía | Orígenes exactos de la página para la política CORS `support-public` (sin credenciales, `GET` y `POST`, sólo `Content-Type`) |

`AllowedOrigins` sigue las reglas de `Cors:AllowedOrigins`: https exacto, sin comodín, ruta ni barra final; `http://localhost[:puerto]`
sólo en `Development` (`appsettings.Development.json` trae `http://localhost:4322`, el puerto fijo del portal `qep-frontend-support` en
local; `3002` es la consola, que no llama al grupo público). En `Production` es
obligatoria y no puede traer un `#{…}#` sin resolver: el pod no arranca (`SupportPublicFormOptionsValidator`). Turnstile es el de la tienda
(`Store:Turnstile:*`, ver [Tienda pública](#secretos)). **El bucket privado de R2 necesita CORS para el origen de la página**, porque el
`PUT` del adjunto sale del navegador: `PUT` con los headers `Content-Type` e `If-None-Match` desde `https://soporte.lulocrm.com` (y
`http://localhost:4322` para probar en local).

**El alias estable de `develop` del portal en Vercel** todavía no existe (el proyecto de Vercel no se ha creado). Cuando exista, se agrega
como `Support__PublicForm__AllowedOrigins__N` del backend de desarrollo y en el CORS del bucket de desarrollo; no va en `appsettings*.json`.

**Slugs reservados.** Las rutas fijas del host (`consultar`, `calificar`, `api`…) no pueden ser slug de un tenant nuevo (`422
tenancy.slug.reserved`). Antes de desplegar se corre la consulta de los avisos de [Soporte](#soporte) contra producción.

**Prueba legal.** `support.legal_document_versions` y `support.legal_acceptances` son insert-only: ningún método edita ni borra,
`legal_acceptances` no tiene FK (ni al ticket ni a la versión: guarda su propia foto del HTML servido) y el `Down` de
`AddSupportLegalDocuments` no las borra. **Todo HTML de un documento pasa por `ISupportHtmlSanitizer`**: al publicar (se guarda la salida),
al servir `GET /form` y al escribir la foto. El saneador corre hasta que la salida no cambia y rechaza (`422 validation.failed` en
`ContentHtml`) un HTML de más de 10 000 elementos o más de 100 niveles de anidamiento. Las tablas de la fase 3,
`support.legal_text_versions` y `support.consents`, se quedan con sus filas y **sin escritor ni lector**: siguen mapeadas en
`SupportDbContext` sólo para que EF no las borre, y el `Down` de `AddSupportPublicForm` tampoco las borra.

**Migraciones:** `Support` `AddSupportPublicForm` (`form_versions`, `legal_text_versions`, `consents`, `ticket_access` y las columnas del
formulario en `tickets`), `Support` `AddSupportTicketAttachments` (`ticket_attachments`), `Support` `AddSupportFormAllowAttachments`
(`form_versions.allow_attachments`, `true` por defecto), `Support` `AddSupportAnonymousTicketCheck` (`CK_tickets_anonymous`) y `Storage`
`AddFileResourceTenantOwnerCreatedIndex` (`IX_file_resources_tenant_owner_created`, para el tope de subidas). `SupportTicket` se guarda
por nombre en `owner_type`, sin migración. `Support` `AddSupportLegalDocuments` (`legal_document_versions` y `legal_acceptances`) es
aditiva y **rellena**: la versión vigente de los textos de la fase 3 de cada tenant pasa a `PrivacyNotice` versión 1 (autorización más
política, como HTML canónico: escapa `&`, `<`, `>`, un párrafo por línea en blanco, `<br>` por salto) y, si tenía términos, a `Terms`
versión 1, con el autor y la fecha de origen; ningún `AnonymousNotice`, y un texto sólo de espacios no se copia. Si un texto convertido
pasa de 60 000 caracteres la migración **se detiene** con el id del tenant (`tenant … legal text exceeds 60000 characters after
escaping`) en vez de recortar un texto legal: se acorta a mano y se vuelve a desplegar. `Support` `AddSupportTicketClientRequestId`
(`tickets.client_request_id` y el índice único parcial `IX_tickets_tenant_client_request`) es aditiva y sin relleno; su `Down` quita
índice y columna. `Support` `AddSupportFormOrganization` (`form_versions.organization_company_id`, `IX_form_versions_organization_company`
y la FK `FK_form_versions_companies_organization_company_id` a `companies.companies` con `ON DELETE RESTRICT`, escrita a mano porque EF no
modela relaciones entre contextos, como `pos.cash_sessions`) es aditiva; su `Down` quita FK, índice y columna. Companies migra antes que
Support.

#### Tiempo real y notas internas de Soporte

Spec `docs/superpowers/specs/2026-11-15-support-realtime-and-notes-design.md`, plan
`docs/superpowers/plans/2026-11-15-support-realtime-and-notes-backend.md`. La consola sabe al instante que un ticket cambió y quién más
está respondiendo, por un hub de SignalR **global** que no es de ningún módulo; y el asesor deja notas internas con texto y archivos que
nunca salen al solicitante.

**El hub.** Vive en `src/Bootstrapper/Realtime/` (`AppHub`), mapeado en `/api/v1/hubs/app` (no en `/hubs/app`: el query `tenantId` sólo
se acepta bajo `/api/v1/hubs/`, plan N1). Mismo transporte que el de Mensajería: sólo WebSocket con `skipNegotiation: true` y sesión por
cookie. Al conectar, sin claim de tenant o sin membresía activa la conexión se aborta; el endpoint no exige módulo (lo exige cada tópico).

| Qué | Valor |
| --- | --- |
| Métodos | `Subscribe(topic)` (no devuelve nada; repetirlo no duplica), `Unsubscribe(topic)` (nunca falla), `SetPresence(topic, state)` con `state` ∈ `composing`, `none` |
| Tópicos de Soporte | `support` (la bandeja) y `support:ticket:{ticketId}` (un detalle, el id en minúsculas, formato `D`). Exigen el módulo `support` y `support.ticket.read` o `support.ticket.manage`; un ticket, además, del tenant. `SetPresence` sólo en un ticket al que **esa conexión** ya se suscribió y con `manage`. Lo demás —tópico mal formado, sin autorizador, más de 50 por conexión, estado desconocido— es `HubException` con `support.realtime.topic_forbidden` (owner O3) |
| Eventos | `supportTicketChanged { ticketId }` a los suscritos al ticket y a la bandeja; `presenceChanged { topic, members: [{ memberId, name, state }] }` a cada conexión del ticket **sin su propio miembro** (lista vacía: nadie más escribe). Suscribirse a un ticket manda enseguida la foto de la presencia sólo a esa conexión |
| Grupo | `{tenantId}:{topic}`: el tenant es siempre el de la conexión, nunca uno que mande el cliente |
| Fanout | `pg_notify('qep_realtime', …)` después del commit (`PgRealtimePublisher`, conexión con pool); cada pod tiene un `RealtimeEventsListener` en `LISTEN qep_realtime` con conexión propia sin pool (la misma regla de `ConnectionStrings:QepDatabaseListener`) que entrega los avisos **en orden** a sus conexiones. Dos `LISTEN` por pod mientras Mensajería no se mude |
| Presencia | Valkey, clave `qep:rt:presence:{tenantId}:{topic}` (área `Valkey:Areas:Presence`), una entrada por **conexión** (`{memberId}:{connectionId}`) que vale 45 s; la clave expira a los 60 s y la pantalla renueva cada 20 s. Sin Valkey, memoria del pod. `RealtimePresenceSweeper` relee cada 5 s lo que el pod entregó no vacío, así una entrada de un pod caído desaparece a los ≤ 50 s |

Avisan crear (consola, conversación y formulario público), estado, responsable, tipo, cliente, fijar, soltar, seguir el hilo, responder,
nota, clasificar y «De ninguno», y el worker del hilo. Un aviso que falla se registra y no deshace la escritura. La fusión y el borrado de
clientes **no** avisan (owner O5): la pantalla los ve al releer.

**Notas internas.** Dos tablas insert-only, `support.ticket_notes` y `support.ticket_note_files`. Una nota no pasa por el agregado del
ticket: no sube `version` ni cambia el `ETag`, no escribe `ticket_events`, no sale en eventos de integración y **nunca** aparece en
`public/support`. Rutas, bajo `/api/v1/tenants/{tenantId}/support`:

| Ruta | Permiso | Qué hace |
| --- | --- | --- |
| `GET /tickets/{ticketId}/notes?before=&take=` | `support.ticket.view` | Página del más nuevo al más viejo (`SupportTicketNotesDto`); `take` 1–100, 50 por defecto; `before` es el cursor de `next` |
| `POST /tickets/{ticketId}/notes` | `support.ticket.manage` | `{ clientId, text, fileIds }` sin `If-Match` → `201 SupportTicketNoteDto`. Texto de 1 a 4 096 recortado, sin caracteres de control salvo salto de línea y tabulador; hasta 5 archivos |
| `POST /tickets/{ticketId}/notes/attachments` | `support.ticket.manage` | `{ name, mimeType, sizeBytes }` → `201 { fileId, uploadUrl, headers }`; mismos límites que el formulario público (10 MB, PDF/JPEG/PNG/WebP) |
| `POST /tickets/{ticketId}/notes/attachments/{fileId}/complete` | `support.ticket.manage` | `{ status }` (`available` o `rejected`) |
| `GET /tickets/{ticketId}/notes/{noteId}/files/{fileId}/download` | `support.ticket.view` | `{ url }` firmada 5 minutos; audita `support.ticket.attachment_downloaded` |

El `clientId` es el id de la nota: el reintento del mismo autor en el mismo ticket recibe la nota guardada (aun con el ticket ya cerrado);
el mismo `clientId` en otro ticket o de otro autor, o la carrera que pierde, es `409 concurrency.conflict`. Errores: `422 validation.failed`
(`errors.ClientId`, `Text`, `Take`, `Before`, `Name`, `MimeType`, `SizeBytes`), `422 support.note.too_many_files` (más de 5 o repetidos),
`422 support.note.file_invalid` (no existe, es de otro ticket u otro miembro, no está `Available` o ya está en otra nota),
`422 support.note.ticket_closed` (crear o abrir una subida en un ticket cerrado), `428 support.public.attachment_not_uploaded` (completar
antes del `PUT`) y `404 support.attachment.not_found` (la nota no es del ticket o el archivo no es de la nota). Crear una nota audita
`support.ticket.note_added` y avisa por el hub.

Los archivos usan la subida sin miembro de Storage con dueño `SupportTicket` y clave `SupportNoteUploads.OwnerKey(ticket, miembro)`
(SHA-256 de `support-note|{ticketId}|{memberId}` marcado como UUID v8, plan N18): sólo quien abrió la subida la usa, y sólo en una nota
de ese ticket; una subida del formulario público no sirve en una nota ni al revés. `ticket_note_files` guarda la foto del archivo
(nombre, tipo y tamaño) y la retiene `SupportAttachmentReferenceProbe` frente al barrido de staging. El tope `attachments_busy` del
formulario cuenta sólo claves v4 (las suyas), así que una subida de nota, adjunta o abandonada, nunca cuenta; y completar o radicar
con un `uploadKey` que no es v4 responde `422 support.public.attachment_invalid` (`SupportPublicUploads`).

**Migración:** `Support` `AddSupportTicketNotes` (`ticket_notes`, `ticket_note_files`), aditiva.

#### Gestión y plazos de Soporte (fase 4a)

Plan `docs/superpowers/plans/2026-11-15-support-phase-4a-management-and-deadlines-backend.md` (spec §7.2, §7.5, §9.4 y §12). Cuatro
ajustes de gestión y la prórroga del plazo. Todas las escrituras de ticket auditan en la misma transacción y avisan por el hub después
de confirmar; un cambio que no cambia nada no sube `version`, no audita y no avisa.

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `POST /tickets` (cambia) | `support.ticket.manage` | `CreateTicketRequest` + `anonymous` (`bool?`, null = `false`) | `201 SupportTicketDetailDto`; `issuedPin` sólo si es anónimo |
| `DELETE /settings/ticket-types/{typeId}` (`If-Match`) | `support.settings.manage` | — | `204`; 404, 412, `422 support.ticket_type.in_use`, 428 |
| `POST /tickets/{ticketId}/take` (`If-Match`) | `support.ticket.manage` | — | `SupportTicketDetailDto`; 404, 412, 422, 428 |
| `POST /tickets/{ticketId}/extension` (`If-Match`) | `support.ticket.manage` | `{ businessDays, reason }` | `SupportTicketDetailDto`; 404, 412, 422, 428 |

**Alta manual anónima.** Con `anonymous: true`, `customerId` y `requester` tienen que venir `null` y `replyChannel` tiene que ser
`Portal`; si no, `422 validation.failed` en `CustomerId`, `Requester` o `ReplyChannel`. Sin anónimo, `Portal` también es
`422 validation.failed` en `ReplyChannel`. `manualMedium` sigue obligatorio. El ticket nace con `isAnonymous: true`, sin cliente ni foto
del solicitante, y con PIN como cualquier otro: **el PIN viaja una sola vez**, en `issuedPin` del `201` del alta, para que el asesor se
lo dicte a quien llamó o vino en persona (es su única forma de consultar el radicado). En el `GET` del detalle, en la respuesta de cualquier
acción y en la auditoría es `null` o no está. No se manda nada al abrir (no hay a dónde): `consultAccess.channel` queda `null`.

**Borrar un tipo de ticket.** Sólo uno que ningún ticket usa, en cualquier estado (si no, `422 support.ticket_type.in_use`: se
desactiva). La regla la cierra la base con la FK `FK_tickets_ticket_types_ticket_type_id` (`ON DELETE RESTRICT`), que
`SupportUnitOfWork` traduce por nombre: un borrado que choca con un ticket (`23001`) es `in_use`, y un alta o cambio de tipo contra un
tipo recién borrado (`23503`) es `422 support.ticket_type.unavailable`. El handler pregunta antes solo para responder rápido. Los tipos
sembrados también se borran y no se vuelven a sembrar. `SupportTicketTypeDto` trae `inUse` para que la tabla de ajustes ofrezca
«Eliminar» o sólo «Desactivar». Audita `support.ticket_type.deleted`.

**«Asignármelo».** Asigna el ticket a la membresía de quien llama, con la misma regla del responsable (membresía activa con
`support.ticket.manage`). Ya suyo: `200` sin cambio de versión. Resuelto o cerrado: `422 support.ticket.not_editable`. Audita
`support.ticket.assigned`. Todo `SupportMemberDto` trae `isMe`, falso fuera de una sesión (consulta pública, worker del hilo).

**Prórroga del plazo** (spec §7.2). Una sola por ticket, con motivo, antes de que venza, y nunca mayor que el término inicial (el plazo
total no pasa del doble). Se guarda en **días hábiles** (`extension_business_days`) y no como fecha: el vencimiento es siempre
término + prórroga + pausas desde la radicación, así una pausa posterior o un cambio de tipo la siguen contando (cambiar de tipo conserva
los días de prórroga y recalcula con el término nuevo). `businessDays` va de 1 a 365 y `reason` de 1 a 1 000 caracteres recortado
(si no, `422 validation.failed` en `BusinessDays` o `Reason`). Las reglas del dominio, en este orden:

| Código (422) | Cuándo |
| --- | --- |
| `support.ticket.not_editable` | El ticket está `Resolved` o `Closed` |
| `support.ticket.extension_already_used` | Ya se prorrogó |
| `support.ticket.extension_no_term` | El tipo no tiene término |
| `support.ticket.extension_clock_stopped` | El reloj está detenido (`WaitingOnCustomer`) |
| `support.ticket.extension_overdue` | Hoy (fecha local del tenant) es posterior al vencimiento; el mismo día del vencimiento todavía se puede |
| `support.ticket.extension_too_long` | `businessDays` es mayor que el término del ticket |

El detalle trae en `clock` la prórroga hecha (`extension: { businessDays, reason, extendedAt }`) y `maxExtensionBusinessDays`: el
término si hoy se puede prorrogar, `null` si no; con eso la pantalla muestra u oculta «Prorrogar» sin repetir las reglas. La traza
suma el evento `Extended` con `extensionBusinessDays`, `fromDueOn`, `toDueOn` y `reason`. Audita `support.ticket.extended` con los
campos `dueOn` y `extension`, **nunca el motivo**. El backend no le avisa al solicitante: el asesor manda el aviso como una respuesta
normal (`POST …/replies`), por el canal del ticket.

**Migraciones:** `Support` `AddTicketTypeForeignKey` (la FK) y `Support` `AddTicketExtension` (`extension_business_days int not null
default 0`, `extension_reason varchar(1000)`, `extended_at timestamptz`, el CHECK `CK_tickets_extension` y `Extended` en
`CK_ticket_events_kind`). Las dos son aditivas.

**Avisos para quien opera:**

- **Antes de desplegar `AddTicketTypeForeignKey`**, en producción esta consulta tiene que dar `0`; si no, la FK no se crea y la
  migración falla (un ticket apunta a un tipo que ya no existe, cosa que hasta hoy ningún camino del código hacía):

  ```sql
  SELECT count(*) FROM support.tickets t WHERE NOT EXISTS (SELECT 1 FROM support.ticket_types y WHERE y.id = t.ticket_type_id);
  ```

- **Durante el rollout**, un pod viejo no conoce el valor `Extended` de `ticket_events.kind`: EF lo lee con `Enum.Parse`, así que el
  detalle de un ticket ya prorrogado responde `500` en ese pod (los demás tickets, no). Es el mismo caso que los eventos nuevos de la
  fase 2 y se arregla solo al terminar el rollout; para evitarlo, nadie prorroga hasta que no quede un pod viejo.
- **Un pod viejo también recalcula `due_on` sin la prórroga.** Al reanudar un ticket que esperaba al cliente (lo hace solo
  `SupportThreadFollower` cuando el cliente escribe) o al cambiarle el tipo, el código viejo escribe término + pausas y pierde los
  días prorrogados; nada lo corrige hasta otra reanudación o cambio de tipo en un pod nuevo. Por eso la regla de no prorrogar vale
  hasta que no quede un pod viejo.
- **Rollback del código:** con tickets ya prorrogados, el código viejo da `500` en el detalle de cada uno, en todos los pods y para
  siempre (no lee `Extended`), y les recalcula el vencimiento sin la prórroga. Un rollback después de la primera prórroga exige
  volver a desplegar el código nuevo; no lo hagas sin revisar `SELECT count(*) FROM support.tickets WHERE extended_at IS NOT NULL;`.
- **«El doble del término» se mide contra el término vigente al prorrogar** (P-3 y P-5). Si después le cambias el tipo a uno de
  término menor, la prórroga se conserva y el total puede pasar del doble del término nuevo. Es lo decidido: no lo «arregles»
  recortando la prórroga.

#### PIN nuevo de un ticket anónimo

Spec `docs/superpowers/specs/2026-11-16-support-anonymous-pin-reissue-design.md`. Un anónimo no tiene correo ni hilo, así que
«Reenviar PIN» del portal nunca le manda nada; si perdió el PIN o quedó bloqueado, el asesor le genera uno nuevo desde la consola.

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `POST /tickets/{ticketId}/pin` (`If-Match`) | `support.ticket.manage` | — | `SupportTicketDetailDto` con `issuedPin` y `consultUrl`; 403, 404, 412, `422 support.ticket.pin_not_anonymous`, 428 |

- Sólo anónimos: un ticket con solicitante o cliente usa el reenvío público. Vale en cualquier estado, cerrado incluido.
- **Desbloquea** la consulta: pone en cero los fallos y quita el bloqueo (el reenvío público sigue sin desbloquear). El PIN viejo deja
  de servir.
- Sube la `version`, suma el evento `PinReissued` (sólo el actor) y audita `support.ticket.pin_reissued` con el campo `pinIssuedAt`.
  El PIN nunca va a la traza, a la auditoría ni a un log.
- `consultUrl` (`string?`, último campo del detalle) es el enlace de la página de consulta del radicado. Viaja **sólo** junto con
  `issuedPin`: en este `200` y en el `201` del alta manual anónima. En toda otra lectura los dos son `null`.
- **Migración:** `Support` `AddSupportPinReissuedEventKind`, que sólo agrega `PinReissued` a `CK_ticket_events_kind`. Durante el
  rollout un pod viejo no lee `PinReissued` (EF hace `Enum.Parse`) y el detalle de ese ticket le da `500`, como con `Extended`: nadie
  genera un PIN nuevo hasta que no quede un pod viejo.

#### Respuesta formal, encuesta, cierre automático y reportes de Soporte (fase 4b)

Plan `docs/superpowers/plans/2026-11-15-support-phase-4b-formal-reply-survey-reports-backend.md` (spec §7.5, §8.3, §8.4, §8.5, §9.4 y
§10). Cuatro piezas: la respuesta formal en PDF, la encuesta de satisfacción de cada resolución, el cierre automático de los tickets
resueltos y el reporte de Soporte.

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `POST /tickets/{ticketId}/replies` (cambia) | `support.ticket.manage` | + `formal: { decision, signerTitle, annexes[] }` | `201`; 422 `support.reply.formal_organization_required`, `formal_signer_required`, `document_render_failed` |
| `GET /tickets/{ticketId}/reply-options` (cambia) | `support.ticket.view` | — | + `formal` (sugerida, motivo de bloqueo, motivo del hilo, conexiones con la plantilla) |
| `GET /tickets/{ticketId}/replies/{replyId}/document` | `support.ticket.view` | — | URL firmada de 5 minutos; 404 `support.reply.document_not_found` |
| `GET` / `PUT /settings` (cambia) | `support.settings.view` / `support.settings.manage` | + `formalNextStep`, `surveyEnabled`, `autoCloseBusinessDays` | + los mismos y el rango del cierre |
| `POST` / `PUT /settings/ticket-types…` (cambia) | `support.settings.manage` | + `formalNextStep` | + `formalNextStep` |
| `GET /reports/summary?from=YYYY-MM-DD&to=YYYY-MM-DD` | `support.ticket.view` | — | `SupportReportDto` |

Todas bajo `/api/v1/tenants/{tenantId}/support`. Las públicas, bajo `/api/v1/public/support/{tenantSlug}` (CORS `support-public`, sin
credenciales, `no-store`, exentas de CSRF por el prefijo, todas con el limitador de la consulta):

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `POST /tickets/lookup` (cambia) | como antes | + `survey: { canRate, rating, options }` (null sin resolución); cada respuesta formal trae `documentUrl` (10 minutos) y `documentFileName` |
| `POST /tickets/rating` | `{ number, pin, turnstileToken, rating, comment }` | lo mismo que la consulta; 422 `support.public.lookup_failed`, `support.survey.closed`, `support.survey.already_answered` |
| `POST /surveys/lookup` | `{ token }` | `SupportPublicSurveyDto`; 404 `support.survey.not_found` |
| `POST /surveys/answer` | `{ token, rating, comment }` | `SupportPublicSurveyDto`; 404, 422 `closed` / `already_answered` / `validation.failed` |

**Los ajustes del `PUT /settings` son opcionales, salvo el umbral.** El frontend publicado manda sólo `{ dueSoonBusinessDays }`, así que
`formalNextStep`, `surveyEnabled` y `autoCloseBusinessDays` ausentes (o `null`) conservan lo guardado; el rango 1–30 del cierre se valida
sólo si llega. Para borrar el «siguiente paso» se manda vacío (`""`). La misma regla vale para `formalNextStep` en el
`PUT /settings/ticket-types/{id}`, aunque el resto de ese cuerpo reemplace el tipo entero: el frontend publicado no manda el campo, y
editar un tipo no le borra su siguiente paso.

**Respuesta formal.** Una respuesta normal con `formal` en el cuerpo, en cualquier tipo y estado (el tipo sólo la *sugiere*, con
`requiresFormalResponse`). `text` es el mensaje del canal; `formal.decision` (1 a 20 000 caracteres, con saltos de línea) es el análisis y
la decisión del PDF; `formal.signerTitle` (1 a 120) es el cargo; `formal.annexes`, de 0 a 10 textos de hasta 200, se imprimen como lista.
`formal` y `uploadId` no van juntos. **Firma el responsable del ticket**, no quien envía: sin responsable, `422
support.reply.formal_signer_required`. El membrete es la empresa del formulario del ticket o, sin ella, la del formulario vigente; sin
ninguna, `422 support.reply.formal_organization_required`. El «siguiente paso» (sección 7) es el del tipo o, si no tiene, el del tenant;
sin los dos, el PDF omite la sección. El PDF se genera (Typst en `qcode-pdf`, `formal-reply.typ`) y se guarda **antes** de la fila de la
respuesta, en el bucket privado (`support/tenants/{tenantId}/replies/{yyyy}/{MM}/{guid}.pdf`); `support.ticket_reply_documents` guarda
la foto de lo que salió. Un reintento de una respuesta `Pending` manda el **PDF guardado**, nunca lo vuelve a generar. Por canal: en el
hilo de WhatsApp, un documento con `text` de pie; en Instagram, el texto y después el archivo; fuera de la ventana de WhatsApp, la
plantilla «Respuesta formal» con el PDF de encabezado; por correo, adjunto; por la página de consulta, publicado y descargable.

**Plantillas.** Dos tipos nuevos de `MessageTemplateKind`, aprovisionados **por conexión** como «Aviso de respuesta»:

| Plantilla | Tipo y nombre | Aprovisionar | Borrador |
| --- | --- | --- | --- |
| Respuesta formal | `SupportFormalReply`, `qep_soporte_formal` (`UTILITY`, encabezado de documento) | `POST …/template-management/support-formal-reply` | `GET …/support-formal-reply/draft` |
| Encuesta | `SupportSurvey`, `qep_soporte_encuesta` (`UTILITY`, tres botones fijos `Buena`, `Regular`, `Mala`) | `POST …/template-management/support-survey` | `GET …/support-survey/draft` |

Las rutas cuelgan de `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}` y piden `messaging.template.manage`. El tenant
edita el texto (cuerpo opcional `{ body }`), no los botones: Soporte lee el toque por su texto. Una plantilla aprobada no se edita:
cambiarla es una versión nueva (`_v2`…) y otra aprobación.

**Encuesta.** Toda transición a Resuelto pide una encuesta de ese ciclo, en el dominio (`ChangeStatus`), aunque el tenant la tenga
apagada (`surveyEnabled`, apagada por defecto): el worker la salta con `survey_disabled`. Una por `(ticket, número de resolución)`. El
worker la manda por el canal del ticket: anónimo o por la página de consulta, publicada ahí; conversación de WhatsApp, la plantilla de
la encuesta (sin plantilla aprobada, sin Mensajería, sin autor o con el contacto bloqueado: «sin encuesta» con el motivo); por correo,
o un ticket de Instagram con correo, un correo con el enlace de calificación; **nunca por Instagram**. Si el ticket se reabrió antes del
envío, «sin encuesta» con `ticket_reopened`. `send_failed` y `connection_unavailable` se reintentan a 1, 5 y 15 minutos y después quedan
`Failed`; una excepción inesperada sigue el mismo camino. El toque de WhatsApp que cita la encuesta la califica; después sale **un**
mensaje libre que pide el comentario («Gracias. Si quieres, cuéntanos en un mensaje por qué elegiste esa opción.»), sin reintento ni
recordatorio, y el siguiente texto del cliente **escrito** dentro de las 24 horas del toque es el comentario (la ventana se mide con la
hora del mensaje, no con la del worker). Por el correo, el enlace es
`{Support:PublicForm:BaseUrl}/{slug}/calificar?token=…`: el token viaja en el cuerpo de la API, nunca en una ruta, y sólo se guarda su
SHA-256. La página de consulta califica con radicado y PIN, con la misma verificación (y el mismo conteo de intentos) que consultar; un
anónimo sólo califica por ahí. La encuesta se responde una vez, hasta que el ticket se reabre (también después del cierre automático).
Un cambio de la encuesta no sube la versión del ticket, pero avisa por el hub.

**La encuesta y el asistente.** El toque de un botón de la encuesta entra al hilo como cualquier entrante, pero **no se encola para el
asistente** (owner, 2026-11-16): Mensajería lo reconoce porque cita un saliente cuya plantilla (nombre e idioma del `details`) es la fila
`SupportSurvey` de esa conexión (`InboundIngestion.IsSupportSurveyTapAsync`). **Efecto conocido y aceptado:** el comentario libre que el
cliente escribe después sí despierta al asistente, si la conversación lo tiene activo y sin asesor, porque no cita nada que lo distinga
de un texto cualquiera. Tampoco se filtra el toque de una encuesta cuya fila de plantilla se borró.

**Cierre automático.** Un ticket Resuelto se cierra el día después del N-ésimo día hábil (calendario de Soporte) contado desde la
resolución o desde la última respuesta del cliente que sigue en el expediente (vínculo `Linked` o `Pinned`), lo que sea más tarde.
`autoCloseBusinessDays` va de 1 a 30, 5 por defecto, y no se apaga. Soltar la respuesta devuelve la fecha; el toque y el comentario de
ninguna encuesta del tenant cuentan (el seguimiento vincula cada entrante a todos los tickets abiertos de la conversación, así que el
toque de la encuesta de un ticket también queda en el otro). Cierra el sistema: traza sin actor, auditoría de sistema `support.ticket.status_changed` y `TicketClosed` en el
hilo. El detalle trae `clock.autoCloseOn` mientras está Resuelto, recalculado en cada lectura.

**Reporte.** La población son los tickets cuya **fecha local de radicación** cae en `[from, to]` (las dos obligatorias, hasta 366 días).
Trae los seis estados, todos los tipos del tenant (activos o no, en su orden) y las tres calificaciones, también en cero. Los totales
cierran: `onTime + late + withoutTerm = resolved` y `pending + sent + withoutSurvey + failed + disabled = requested`. Resuelto es un
ticket Resuelto o cerrado **desde** Resuelto: uno reabierto y cerrado desde «Esperando al cliente» conserva el `resolved_at` del ciclo
anterior y no cuenta. El tiempo de
resolución son los días hábiles después de la radicación hasta la última resolución, en bruto (sin descontar pausas).

**Workers** (sección `Support:Workers`, defaults en código; `Support:Workers:Enabled` en `false` los apaga):

| Worker | Clave | Default | Qué hace |
| --- | --- | --- | --- |
| `SupportSurveyWorker` | `SurveyIntervalSeconds` | 30 | Sin timbres, cada cuánto mira la cola `support_survey` |
| | `SurveyBatchSize` | 5 | Encuestas por reclamo; cada una renueva su reclamo de 2 minutos antes de mandarse |
| | `SurveyCommentWindowHours` | 24 | La ventana del comentario, desde el toque |
| | `SurveyAnswerLookbackDays` | 14 | Hasta cuándo se buscan toques de una encuesta mandada |
| `SupportAutoCloseWorker` | `AutoCloseIntervalMinutes` | 15 | Cada cuánto revisa los tickets Resueltos |

**Migraciones:** `Support` `AddSupportFormalNextStep`, `Support` `AddSupportReplyDocuments`, `Messaging`
`AddSupportFormalAndSurveyTemplateKinds`, `Support` `AddSupportSurveys` y `Support` `AddSupportAutoCloseAndSurveyIndexes` (los índices
parciales de los recorridos del cierre automático y de la encuesta). Las cinco son aditivas.

##### Despliegue (fase 4b)

| Migración | Pods viejos durante el rollout |
| --- | --- |
| `AddSupportFormalNextStep` | Ignoran las columnas. |
| `AddSupportReplyDocuments` | Una respuesta que borra un pod viejo se lleva su documento por la cascada. |
| `AddSupportFormalAndSurveyTemplateKinds` | Ver abajo: no aprovisionar las plantillas nuevas mientras quede un pod viejo. |
| `AddSupportSurveys` | Una resolución que hace un pod viejo no pide encuesta (aceptado: no hay relleno al arrancar). |
| `AddSupportAutoCloseAndSurveyIndexes` | Ignoran los índices: son parciales sobre `support.tickets` (`IX_tickets_resolved_auto_close`) y `support.ticket_surveys` (`IX_ticket_surveys_awaiting_comment`, `…_awaiting_rating`, `…_comment_message`, `…_rating_message`), con `CREATE INDEX` simple (no `CONCURRENTLY`): bloquean las escrituras de esas tablas mientras se construyen, que en tablas de este tamaño es un instante. Aditiva. |

- **Los tipos nuevos de plantilla se guardan por nombre.** Un pod viejo (o el código de antes, tras un rollback) no conoce
  `SupportFormalReply` ni `SupportSurvey` en `message_templates.kind`: EF los lee con `Enum.Parse` y falla. En una conexión que ya tenga
  una de esas plantillas, la gestión de plantillas y el refresco del espejo responden `500` en ese pod. Aprovisiona las plantillas nuevas
  sólo cuando no quede un pod viejo, y no hagas rollback después de aprovisionarlas: el `Down` de
  `AddSupportFormalAndSurveyTemplateKinds` **borra esas filas**, y con ellas plantillas ya aprobadas por Meta. Lo mismo vale para
  cualquier valor nuevo de los enums de Soporte que se guardan por nombre (`SupportTicketEventKind` y los de la encuesta).
- **Soporte usa la clave de `qcode-pdf` de todo el backend** (`QUOTATIONS_PDF_API_KEY` y `QUOTATIONS_PDF_BASE_URL`, decisión del
  owner del 2026-11-17, que reemplaza la clave propia `SUPPORT_PDF_*`). Sin ella, `SupportPdfOptionsValidator` (`ValidateOnStart`) no
  deja arrancar el pod en producción; un `#{…}#` sin resolver cuenta como ausente. Ver § Generación del PDF.
- **Frontend: la página `/calificar?token=…` tiene que responder con `Referrer-Policy: no-referrer`.** El token va en la query de la
  URL de la página (sólo la API lo recibe en el cuerpo); sin esa cabecera, cualquier recurso de terceros que la página cargue (una
  fuente, una analítica, el logo del tenant) recibe la URL completa con el token en el `Referer`, y con él se califica la encuesta.
- **La plantilla del PDF nunca se generó contra el `qcode-pdf` real.** Antes del primer envío en producción, genera a mano y revisa:
  una decisión larga con saltos de línea; con logo y sin logo; un ticket anónimo («Solicitante anónimo», sin documento ni correo); diez
  anexos; y textos con `#`, `[`, `*` y `$` (tienen que salir como texto, nunca como marcado de Typst).
- **El prefijo `support/` del bucket privado de R2 no lleva regla de ciclo de vida:** la respuesta formal es prueba legal y se guarda
  diez años (spec §13.10). Nunca la pongas bajo `quotations/`, que sí tiene ciclo de vida.
- **La cola nueva `support_survey`** (pendientes y edad de la más vieja con `attempts = 0`) la publica el muestreador de colas, pero el
  dashboard y las reglas de alerta viven en `migracion-k8s`: hay que sumarla allá.
- **Los campos nuevos del `PUT /settings` son opcionales** (ausentes conservan lo guardado): el frontend publicado sigue funcionando
  sin cambios.
- **Verificar contra Meta real:** que la plantilla con tres botones de respuesta rápida se acepte sin parámetros de botón y que el
  toque llegue con `button.text` y `context.id` (V-1); la categoría con que Meta aprueba `qep_soporte_encuesta` (V-2, puede ser
  `MARKETING`); y que Instagram acepte el PDF como archivo desde la URL firmada de 10 minutos (V-3).

### API externa

Un sistema del tenant (un ERP, una tienda) envía plantillas de WhatsApp con una **clave de API** en lugar de una sesión. Las claves las crea
y revoca un administrador con el permiso `developer.api_key.manage`, en `/api/v1/tenants/{tenantId}/api-keys`; la clave completa viaja
**una sola vez**, en la respuesta de creación (`secret`). Diseño:
[`docs/superpowers/specs/2026-10-30-api-keys-design.md`](docs/superpowers/specs/2026-10-30-api-keys-design.md).

**Claves y scopes.** Formato `qep_<prefijo>_<secreto>`: prefijo de 8 caracteres base62 (público, la lista de claves lo muestra) y secreto de
256 bits en base62. Se guarda solo el hash SHA-256 del secreto. Cada clave lleva sus scopes (hoy uno, `whatsapp.templates.send`), una
lista opcional de números (`connectionIds`; sin lista, todos los del tenant) y un vencimiento opcional. Máximo 25 claves activas por tenant
y nombre único entre las activas. Revocar es inmediato: el request siguiente da 401. Una clave **nunca** recibe permisos de sesión, así
que no entra a ninguna ruta interna, y la cookie no entra a la API externa.

**Autenticación.** Header `X-Api-Key` en cada petición. Ausente, malformada, desconocida, revocada, vencida o de un tenant no activo dan la
misma respuesta: `401 external.auth.invalid_key`.

**Endpoints** (todos bajo `/api/v1/external/whatsapp`):

| Método y ruta | Devuelve |
| --- | --- |
| `GET /connections` | Los números que la clave puede usar: `id`, nombre y teléfono |
| `GET /connections/{connectionId}/templates` | Las plantillas enviables: nombre, idioma, categoría, `header` (`null` o `{ "type": "image" \| "video" \| "document" }`) y parámetros |
| `POST /messages` | `201` al enviar, `200` si es un reintento de un envío ya hecho |

Cuerpo de `POST /messages`, en un archivo, porque PowerShell rompe las comillas dobles al pasarlas a un `.exe`:

```powershell
@'
{
  "idempotencyKey": "0192f3a4-7b1c-7e2a-9d4f-3a5b6c7d8e9f",
  "connectionId": "0192f3a4-7b1c-7e2a-9d4f-000000000001",
  "to": "573001234567",
  "template": { "name": "aviso_despacho", "language": "es_CO", "parameters": { "1": "Ana", "2": "4821" } }
}
'@ | Set-Content -Encoding utf8 cuerpo.json

$env:QEP_API_KEY = "qep_xxxxxxxx_..."
curl.exe -s https://<host>/api/v1/external/whatsapp/connections -H "X-Api-Key: $env:QEP_API_KEY"
curl.exe -s https://<host>/api/v1/external/whatsapp/connections/<connectionId>/templates -H "X-Api-Key: $env:QEP_API_KEY"
curl.exe -s -X POST https://<host>/api/v1/external/whatsapp/messages -H "X-Api-Key: $env:QEP_API_KEY" -H "Content-Type: application/json" -d "@cuerpo.json"
```

**Encabezado multimedia.** Una plantilla aprobada con encabezado de imagen, video o documento (`header` distinto de `null` en el listado)
exige que el envío lo lleve, por enlace: QEP **no descarga** el archivo, le entrega el enlace a Meta tal cual.

```powershell
@'
{
  "idempotencyKey": "0192f3a4-7b1c-7e2a-9d4f-3a5b6c7d8e9f",
  "connectionId": "0192f3a4-7b1c-7e2a-9d4f-000000000001",
  "to": "573001234567",
  "template": {
    "name": "aviso_logo",
    "language": "es_CO",
    "header": { "type": "image", "link": "https://cdn.example.com/logo.png" },
    "parameters": { "1": "1014853", "2": "Pendiente" }
  }
}
'@ | Set-Content -Encoding utf8 cuerpo-imagen.json
```

Reglas de `template.header` (el `422 validation.failed` marca `template.header.type`, `.link` o `.filename`):

- `type`: `image`, `video` o `document` (en minúsculas; al leer no distingue mayúsculas).
- `link`: obligatorio. URL absoluta `https`, de máximo 2048 caracteres, sin espacios ni caracteres de control y sin credenciales
  (`https://usuario:clave@host` se rechaza). `http` se rechaza. Si el enlace lleva un token en la query, QEP no lo escribe en logs ni en el
  registro de llamadas; sí queda en el mensaje y el hilo lo muestra.
- `filename`: opcional y solo con `document`; es el nombre que ve quien recibe el archivo. Máximo 240 caracteres, no puede ser solo
  espacios ni llevar `/`, `\` ni caracteres de control. Con `image` o `video` es error.
- El `header` debe coincidir con la plantilla: si la plantilla lo pide y falta, si sobra, o si es de otro tipo (un `video` para una
  plantilla de imagen), responde `422 messaging.template.header_mismatch` y el `detail` dice qué pide la plantilla.

Un enlace que Meta no puede bajar (404, un tipo o tamaño que no acepta) **no** da error en el `POST`: Meta acepta el envío y el mensaje
queda `Failed` después, por webhook. Se ve en `GET /messages/{id}` con `failureCode` y `failureReason`.

Respuesta de envío: `{ "messageId": "…", "conversationId": "…", "status": "Sent" }`; en un reintento, `status` trae el estado actual del mensaje, que puede ser posterior a `Sent` (por ejemplo `Read`). El `idempotencyKey` es obligatorio, lo genera el sistema
externo y es la llave de idempotencia **por tenant**: repetirlo con el mismo destino, plantilla, idioma, parámetros y `header` (tipo, enlace
y `filename`) devuelve `200` con lo guardado, sin volver a enviar; con otra cosa, `409`, y mandar un `header` donde el primer envío no lo
llevaba (o al revés) también es otra cosa. La excepción es un mensaje que quedó `Failed`: repetir su `idempotencyKey` lo reenvía con el cuerpo
nuevo, así que si Meta no pudo bajar el enlace, corrígelo y reintenta con la misma clave. Esa excepción no cubre al `Failed` cuyo fallo es `Unconfirmed` (el `502 external.send_unconfirmed`): ahí Meta pudo haber aceptado el mensaje, así que repetir la clave devuelve el estado guardado y no reenvía. `to` lleva indicativo de país (se aceptan `+`, espacios y guiones); sin él responde
`422`, porque no se adivina el país. Solo las plantillas `MARKETING` heredan las reglas de campañas (opt-out del cliente, cliente inactivo);
una conversación bloqueada rechaza cualquier categoría.

**Errores.** Todo es `ProblemDetails` con `code`, y los `code` son contrato público: cambiarlos rompe integraciones. `title` y `detail` son
texto para personas y pueden cambiar: decide por `code`. En un `422 validation.failed`, `detail` es un texto fijo y lo que hay que corregir
viene por campo en `errors`.

| Status | `code` | Cuándo |
| --- | --- | --- |
| 401 | `external.auth.invalid_key` | Clave ausente, malformada, desconocida, revocada, vencida o de un tenant no activo |
| 403 | `external.auth.scope_missing` | La clave no tiene el scope |
| 403 | `external.connection_not_allowed` | Número fuera de la clave o de otro tenant |
| 403 | `tenancy.module_not_enabled` | `developer` o `messaging` apagados |
| 409 | `external.idempotency_key_reused` | Mismo `idempotencyKey` con otro cuerpo (destino, plantilla, idioma, parámetros o `header`) |
| 422 | `validation.failed` | Con el mapa `errors` |
| 422 | `messaging.template.not_available` | Plantilla inexistente, no aprobada o no enviable |
| 422 | `messaging.template.parameters_mismatch` | Parámetros que no coinciden |
| 422 | `messaging.template.header_mismatch` | Falta el encabezado, sobra o es de otro tipo; `detail` dice cuál pide la plantilla |
| 422 | `messaging.connection_unavailable` | La conexión está pausada, necesita atención o Meta rechazó sus credenciales |
| 422 | `messaging.templates.unavailable` | Meta no contestó la lista de plantillas; reintentar es seguro |
| 422 | `messaging.channel_unsupported` | La conexión es de Instagram: no tiene plantillas |
| 422 | `external.recipient_blocked` | Contacto bloqueado |
| 422 | `external.recipient_opted_out` | Plantilla `MARKETING` a quien no la acepta |
| 422 | `external.recipient_inactive` | Plantilla `MARKETING` a un cliente inactivo |
| 422 | `external.message_rejected` | Meta rechazó con certeza; el mismo `idempotencyKey` reenvía |
| 429 | `external.rate_limited` | Límite de QEP o de Meta, con `Retry-After` |
| 502 | `external.send_unconfirmed` | No se sabe si salió |

**`external.send_unconfirmed` significa «no sé si salió».** Meta pudo haber aceptado el mensaje aunque no lo confirmó. Repetir el mismo
`idempotencyKey` repite el `502` y **no** reenvía; decidir si se manda otro exige un `idempotencyKey` nuevo, y esa decisión es del sistema externo.

**Límites.** Dos, porque el rate limiter corre antes de autenticar: 600 por minuto por IP (frena a quien prueba claves inventadas,
`ApiAccess:RateLimit:PerIpPerMinute`) y 300 por minuto por clave después de autenticar (`ApiAccess:RateLimit:PerKeyPerMinute`). **Con
`Valkey:Areas:RateLimiting` prendido los dos se cuentan en Valkey** y valen para todas las réplicas. Apagado, o con Valkey caído, cada
réplica lleva su propia cuenta en memoria y el límite efectivo se multiplica por el número de réplicas. Ambos deben ser mayores que 0: un 0 o un negativo tumba el arranque (`ValidateOnStart`), no se interpreta como «sin límite».

**Autoría.** El mensaje queda firmado con la clave (`sent_by_kind = 4`, `sent_by_api_key_id`, `sent_by_api_key_name`) y el hilo lo muestra como
`sentByApiKey`. Un intento rechazado no va a auditoría sino al log, con el prefijo y nunca el secreto (`Information` si la clave existe y está revocada, vencida o con secreto errado; `Debug` si es desconocida o malformada); la creación y la revocación sí
se auditan (`api_access.key.created`, `api_access.key.revoked`).

### Módulo Wms (bodegas)

Módulo `Modules.Wms.{Domain,Application,Infrastructure,Api}`, esquema `wms`, clave de módulo `wms` (`Dependencies = [Catalog]`, última de
`TenantModuleKeys.All`, fuera de `DefaultForNewTenants`: nace apagado). Es la fase 1 de
[`docs/superpowers/specs/2026-10-31-wms-arquitectura-design.md`](docs/superpowers/specs/2026-10-31-wms-arquitectura-design.md): bodegas,
árbol de ubicaciones, dueños, unidades de medida y perfil logístico de las variantes. El libro de inventario (WMS-D) está descrito en la subsección «Inventario (WMS): ledger y saldos» más abajo. Rutas bajo
`/api/v1/tenants/{tenantId}/wms` (`X-Tenant-Id`).

**Cómo se prende.** Por la consola de operador: `POST /api/v1/tenants/{tenantId}/operator/tenants/{targetTenantId}/modules/changes`, que escribe
la fila de `tenancy.tenant_modules` con `module_key = 'wms'` (con `catalog` ya prendido). Mientras el frontend no conozca la clave `wms`, la
pantalla del operador oculta el módulo y se prende llamando a la API. Sin la fila, todo `/wms/*` responde 403 `tenancy.module_not_enabled`; con otro tenant en la ruta, 403 `authorization.denied`, nunca 404.

**Permisos.** Diecisiete, `wms.<recurso>.<acción>` (`WmsPermissions`), cada uno con su política en `AddAuthorization` y
`RequiredModules: [wms]`. Sólo el rol de sistema `admin` los trae; ningún otro. Las rutas de Bodegas usan cuatro: `wms.warehouse.manage`,
`wms.owner.manage`, `wms.profile.manage` y `wms.document.read`; las de inventario suman `wms.stock.read`, `wms.stock.export`,
`wms.adjustment.propose` y `wms.adjustment.approve`; las de tareas, `wms.task.manage` y `wms.putaway.perform`. El resto es de fases
posteriores.

**Lo que nace solo.**

- **Con cada bodega:** las ubicaciones virtuales `SUPPLIER`, `CUSTOMER`, `ADJUSTMENT` y `TRANSIT`, y la zona `RECEIVING` con el muelle
  `REC-01`. Las virtuales son de solo lectura: un `PUT` sobre una con cuerpo válido responde 422 `wms.location.virtual_immutable` (un
  cuerpo inválido responde antes `validation.failed`, también 422), y no se borran ni se les imprime etiqueta. `RECEIVING` y `REC-01`
  tampoco se borran (mismo código). Un nodo con hijos o con saldo tampoco se borra.
- **En el primer acceso del tenant** (los `GET` de bodegas, dueños, unidades y perfiles, y sus `POST`/`PUT`; no `GET …/locations` ni `scannable`, y no al prender el módulo:
  Tenancy no avisa): el dueño `Self` y las seis
  unidades `UND, KG, G, L, ML, M`. Lo escribe `IWmsProvisioner` con `INSERT … ON CONFLICT DO NOTHING`; una consulta directa a
  `wms.owners` de un tenant que nunca entró devuelve cero filas, y no es un error.
- **Perfil sin fila:** el `GET /variant-profiles` lo calcula en memoria con `version: 0`; el `PUT` con `version: 0` lo crea.

**`scannable` y el `ETag`.** `GET …/locations/scannable` devuelve `{ id, code, path, role }` de las ubicaciones no virtuales de la bodega y
un `etag` que cambia cuando cambia cualquiera. La app lo guarda y manda `If-None-Match`: igual → 304 sin cuerpo. Es la cache con la que
el escáner resuelve un código contra su bodega.

**P-1: los identificadores de ubicación no se escriben.** `wms.identifiers` es único por `(tenant_id, value)` y el código de una
ubicación es único **por bodega**: la segunda bodega repetiría `SUPPLIER` o `REC-01`. La app resuelve el código contra el `scannable` de
su bodega, no contra `identifiers`. El spec §25 queda por corregir.

**Etiquetas (RF-004).** `POST …/locations/labels` con `{ locationIds }` (1 a 500; uno ajeno a la bodega es 422 `wms.location.not_found`)
devuelve un PDF con una etiqueta de 60×30 mm por ubicación, en Code 128 (paquete `@preview/tiaoma` de Typst). Sale por `qcode-pdf` con la
**misma clave de cotizaciones** (`Quotations:Pdf`), desde `WmsPdfGateway` en Bootstrapper: el módulo no ve esa clave. Si el servicio no
resuelve el paquete o cae, la respuesta es 422 `wms.label.render_failed` con el mensaje del compilador. La prueba contra el servicio real
es manual (se necesita la clave del developer, nunca en un log); las automáticas sustituyen el renderer.

**Migraciones:** `Tenancy` `AddWmsModuleKey` (el CHECK de `tenant_modules` admite `wms`) y `Wms` `InitialWms` (esquema `wms`).

#### Inventario (WMS): ledger y saldos

Fase 2 del mismo spec (WMS-D). El inventario es un **ledger inmutable**: `wms.stock_movements` guarda cada movimiento con origen y
destino (par de claves `StockKey`: dueño, ubicación, variante, lote y estado) y `wms.stock_balances` es el saldo derivado, que sólo
escribe `InventoryLedger` en la misma transacción. Nada se edita: `trg_stock_movements_immutable` lanza `wms.ledger.immutable` ante
cualquier `UPDATE` o `DELETE`, y un error se corrige con un movimiento de **reverso** (`POST /stock/movements/{id}/reverse`), que apunta
al original. Quitarle al rol de la aplicación el `TRUNCATE` sobre `wms.stock_movements` y sobre cada una de sus particiones mensuales es
una tarea **pendiente de la plataforma** (repo `migracion-k8s`), no una migración: hoy nada en `k8s/` lo hace y las migraciones corren con la conexión de la aplicación. Las lecturas salen de `wms.v_stock_rows` y `wms.v_stock_summary`, donde viven las fórmulas de disponible, picking, recibido y
bloqueado. Las sondas de «con existencias» (`hasStock` del perfil, borrar una ubicación, desactivar un dueño) cuentan saldo físico: una
ubicación no virtual con `on_hand` o `reserved` distinto de cero. Una variante con movimientos o saldos en el ledger tampoco se borra
de Catalog (`CatalogVariantUsageChecker`): se desactiva.

La clave del saldo incluye además la **unidad logística** (`handling_unit_id`, caja o palet; D-12): `NULL` es stock suelto, y el
movimiento guarda la de cada extremo (`from_handling_unit_id`, `to_handling_unit_id`). En WMS-D va siempre en `NULL`, salvo lo que copie
un reverso: el agregado `HandlingUnit`, su tabla y sus rutas llegan con el corte que confirme recepciones.

**Rutas** (bajo `/api/v1/tenants/{tenantId}/wms`). **IK** exige el header `Idempotency-Key`; **SK** deriva la clave en el servidor.

| Ruta | Permiso | Idempotencia |
| --- | --- | --- |
| `GET /stock` | `wms.stock.read` | — |
| `GET /stock/summary` | `wms.stock.read` | — |
| `GET /stock/movements` | `wms.stock.read` | — |
| `POST /stock/export` | `wms.stock.export` | — (entrega por correo) |
| `POST /stock/movements/{id}/reverse` | `wms.adjustment.approve` | IK |
| `GET /adjustments` | `wms.document.read` | — |
| `POST /adjustments` | `wms.adjustment.propose` | IK |
| `POST /adjustments/{id}/approve` | `wms.adjustment.approve` | — (versión) |
| `POST /adjustments/{id}/reject` | `wms.adjustment.approve` | — (versión) |
| `POST /adjustments/{id}/post` | `wms.adjustment.approve` | SK: `adjustment:{id}:post:{version}`; el header se ignora |
| `POST /imports/initial-balances/preview` | `wms.adjustment.propose` | — (no escribe saldos) |
| `POST /imports/initial-balances/commit` | `wms.adjustment.propose` | IK |

**`Idempotency-Key`.** Sin header en una ruta IK: 428 `wms.idempotency_key_required`. Misma clave y mismo cuerpo: la respuesta guardada
con **200** (aunque el original fuera 201). Misma clave con otro cuerpo: 409 `wms.idempotency_key_reused`. El `request_hash` sale del DTO
enlazado, no del cuerpo crudo. La clave de otro tenant es 403, nunca una respuesta guardada ajena. Los recibos (`wms.operation_receipts`)
se purgan a los 45 días (`Wms:Workers:ReceiptRetentionDays`). Repetir `post` con una versión vieja devuelve el 200 guardado, no 412.

**Ventana de `occurredAt`.** `[ahora − 30 días, ahora + 5 min]`; fuera de ella, 422 `wms.movement.occurred_at_out_of_range`, también al
postear un ajuste propuesto hace tiempo con fecha física. La ventana es la que permite purgar los recibos a 45 días.

**Workers** (opciones `Wms:Workers:*`, validadas al arrancar; un cero o un negativo tumba el arranque):

| Worker | Qué hace | Opciones (por defecto) |
| --- | --- | --- |
| `BalanceChangePublisher` | Vacía `wms.balance_change_queue` y publica `wms.balance.changed.v1` al outbox | `BalanceChangePublishSeconds` (10) |
| `BalanceReconciliationWorker` | Una vez al día compara el saldo con el ledger por bodega; registra la diferencia en `wms.balance_checks` y **no la corrige**. Con dos pods, un candado por bodega evita la fila repetida | `ReconciliationHourUtc` (3), `ReconciliationSweepMinutes` (30) |
| `PartitionMaintenanceWorker` | Asegura la partición del mes actual y la del siguiente de `stock_movements` | `PartitionSweepHours` (24) |
| `WmsHousekeepingWorker` | Purga recibos pasados de `ReceiptRetentionDays`, previews vencidos (por su propia columna) y saldos en cero (`on_hand = 0` y `reserved = 0`) | `HousekeepingHours` (24), `HousekeepingStartupSeconds` (60), `ReceiptRetentionDays` (45) |
| `WmsExportDeliveryWorker` (Notifications) | Manda por correo el enlace del export (`wms.export-ready.v1`) | — |
| `TaskLeaseSweeper` | Devuelve al pool (o a su asignado) las tareas con el lease vencido; toma las filas con `FOR UPDATE SKIP LOCKED`, así dos pods reparten trabajo | `TaskLeaseSweepSeconds` (60; de 10 a 600) |

`WorkerStartupSeconds` (30) retrasa la primera pasada de los tres workers del ledger y del barrido de tareas: con un intervalo largo y un
despliegue por commit, el pod se reiniciaría antes de que el temporizador cumpliera.

**Métricas** (meter `Qep.Wms`, sin etiqueta de tenant): `qep.wms.movements` (contador, etiqueta `reason`), `qep.wms.confirm.duration`
(histograma, ms), `qep.wms.balance.drift` (claves distintas en la última conciliación), `qep.wms.partition.missing` (1 si falta la
partición del mes actual o del siguiente) y `qep.wms.reconciliation.failed` (bodegas que la última conciliación no pudo revisar). En
Prometheus salen como `qep_wms_movements_total`, `qep_wms_balance_drift`, `qep_wms_partition_missing` y `qep_wms_reconciliation_failed`
(el histograma agrega el sufijo de unidad del exportador). Las alertas viven en el repo de plataforma (`migracion-k8s`), no acá.

**Partición faltante.** `stock_movements` está particionada por mes de `occurred_at`, con límites en UTC. Si `qep_wms_partition_missing`
vale 1, el worker no pudo crear la partición. Las filas fuera de rango caen en `stock_movements_default` y **se quedan ahí** (el trigger
impide moverlas). Se **crea** la partición (`CREATE TABLE wms.stock_movements_yYYYYmMM PARTITION OF wms.stock_movements FOR VALUES FROM … TO …`)
y **nunca se desprende** una existente. Si `DEFAULT` ya tiene filas de ese mes, el `CREATE … PARTITION OF` falla y se resuelve a mano en
ventana de mantenimiento. Un `SELECT` sin `occurred_at` en el `WHERE` recorre todas las particiones.

**Carga inicial por Excel.** `POST /imports/initial-balances/preview?warehouseId=…&ownerId=…` (multipart, campo `file`) lee la primera hoja
y ubica por cabecera, no por posición, seis columnas: `code`, `location`, `lot`, `expiration`, `quantity` y `uom`. **Tope de 500 filas
por archivo** (más es 422 `wms.import.too_many_rows`): la carga es un solo batch del ledger y cada fila un movimiento. El archivo admite
5 MB comprimido, 20 MB descomprimido y 100 entradas (si no, 422 `wms.import.invalid_file`). El preview responde **200 aunque haya
problemas**: cada fila lleva sus `issues` y `canCommit` es `false` si hay alguno; si una clave (dueño, ubicación, variante, lote) ya tiene
saldo, la fila trae el issue bloqueante `wms.import.stock_exists`, así que un complemento va por ajustes. `POST /imports/initial-balances/commit`
con `{ previewId, version }` y `Idempotency-Key` responde 201, o **422 `wms.import.invalid_file` si queda algún issue**. El preview vive 30 minutos.

**Export.** `POST /stock/export` responde 202 y no devuelve el archivo: arma el Excel, lo guarda en Storage y publica `wms.export-ready.v1`;
`WmsExportDeliveryWorker` manda el enlace de descarga por correo y la campana de quien lo pidió recibe un `ExportReady` con
`exportKind: "Stock"`.

**Regla pendiente de confirmación del owner.** Con saldo, además del rastreo, no se puede cambiar la unidad base, bajar la precisión
ni cambiar el factor de una presentación existente: 422 `wms.profile.base_uom_change_requires_identification`. RF-010 sólo nombra el
rastreo; si el owner prefiere otra regla, se relaja en `VariantLogisticsProfile.Update`.

**Migraciones:** `Wms` `AddInventoryLedger` (ledger, saldos, vistas, partición y trigger). Su `Down` está vacío a propósito (ver `CLAUDE.md`).

#### Tareas de bodega (motor de tareas)

Spec [`docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md`](docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md)
§6. Una tarea (`wms.tasks`, con `task_lines` y `task_events`) es una intención; el ledger es el hecho. Crear, asignar, priorizar o
cancelar nunca mueve stock: sólo la confirmación de una línea, que escribe un movimiento `InternalMove` con documento
`("Task", taskId, lineId)`. En este corte hay un solo tipo, `Move` (movimiento interno), y ejecutarlo exige `wms.putaway.perform`.

**Estados.** `Planned` (creada sin publicar o retenida), `Ready` (en el pool), `Assigned` (preasignada), `InProgress` (reclamada con lease),
`Done` y `Cancelled` (terminales). Cada transición sube `version` y deja un evento; el latido no.

**Supervisor** (bajo `/api/v1/tenants/{tenantId}/wms`). Las acciones llevan `version` (412 `concurrency.conflict`), responden la tarea
sin `events` y se auditan.

| Ruta | Permiso | Cuerpo / idempotencia |
| --- | --- | --- |
| `POST /tasks` | `wms.task.manage` | IK. `{ warehouseId, ownerId?, priority?, dueAt?, note?, publish = true, assignToMemberId?, lines: [{ variantId, lotId?, stockStatus = "Available", fromLocationId, toLocationId, quantity }] }` → 201 |
| `GET /tasks?warehouseId&status&type&assignedMemberId&page&pageSize` | `wms.document.read` | — |
| `GET /tasks/summary?warehouseId` | `wms.document.read` | Tablero: `byStatus` y `byType` completos, también en cero, y quién tiene qué |
| `GET /tasks/assignees?warehouseId&type` | `wms.task.manage` | Membresías activas con el permiso del tipo |
| `GET /tasks/{id}` | `wms.document.read` | La única respuesta con `events` |
| `POST /tasks/{id}/publish`, `/unassign` | `wms.task.manage` | `{ version }` |
| `POST /tasks/{id}/hold` | `wms.task.manage` | `{ version, reason? }` |
| `POST /tasks/{id}/assign` | `wms.task.manage` | `{ memberId, version, reason? }`; motivo obligatorio si estaba `InProgress` |
| `POST /tasks/{id}/priority` | `wms.task.manage` | `{ priority, urgent, version }` |
| `POST /tasks/{id}/cancel`, `/lines/{lineId}/cancel` | `wms.task.manage` | `{ reason, version }` |

**Operario.** La ruta exige `wms.document.read` y el handler, además, el permiso del tipo.

| Ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /tasks/mine?warehouseId` | — | `{ current, assigned }` |
| `POST /tasks/next` | `{ warehouseId, deviceId }` | 200 la tarea ya reclamada; **204** si no hay ninguna. Repetirla devuelve la que tiene en curso |
| `POST /tasks/{id}/claim` | `{ deviceId }` | 200 |
| `POST /tasks/{id}/heartbeat` | `{ leaseToken }` | 200 `{ leaseExpiresAt }` |
| `POST /tasks/{id}/unclaim` | `{ leaseToken, reason }` (`ShiftEnd`, `CannotReach`, `Skipped`, `Other`) | 200 |
| `POST /tasks/{id}/lines/{lineId}/confirm` (IK) | `{ leaseToken, fromLocationId, toLocationId, lotId?, quantity, overrideReason?, occurredAt, deviceId }` | 200 |
| `POST /tasks/{id}/lines/{lineId}/short` (IK) | `{ leaseToken, quantityFound, lotId?, incidentType, note?, photoFileIds?, occurredAt, deviceId }` | 200 la tarea |
| `POST /tasks/{id}/lines/{lineId}/count` (IK) | `{ leaseToken, locationId, lotId?, quantityCounted, occurredAt, deviceId }` | 200 la tarea |

`short` confirma lo encontrado hacia el destino planeado, cierra la línea como `Short` y deja la novedad en la misma transacción y con
la misma `Idempotency-Key`; nunca ajusta stock. `count` sólo existe para el tipo `Count` (permiso `wms.count.perform`); `confirm` y
`short` sobre un `Count`, y `count` sobre un `Move`, responden 422 `wms.task.invalid_transition`.

**Lease.** `claimedBy*`, `leaseExpiresAt` y `leaseToken` sólo viajan con la tarea `InProgress`, y `leaseToken` sólo a quien la tiene. El
lease de `Move` dura 15 minutos; el latido y cada confirmación lo renuevan. Al vencer, el barrido devuelve la tarea y conserva quién la
reclamó y su token: una **confirmación tardía** con ese token la reanuda si nadie más la tomó (si quien confirma ya tiene otra en curso,
se aplica sin reanudar). Latir y soltar exigen el lease vigente. La misma persona que retoma desde el mismo celular conserva el token.
`next` no vuelve a ofrecer durante 15 minutos lo que la persona soltó con `Skipped`, salvo que se lo asignen. Los eventos de una
confirmación llevan la hora del escaneo (`occurredAt`).

**Códigos.** 404 `wms.task.not_found` (tarea o línea). 409 `wms.task.lease_lost`, `wms.task.member_busy` (con `taskId`; falta cuando lo
detecta el índice único). 422 `wms.task.invalid_transition` (con `from` y `action`), `wms.task.assigned_to_other`,
`wms.task.assignee_not_eligible`, `wms.task.location_invalid`, `wms.task.location_mismatch`, `wms.task.lot_mismatch`,
`wms.task.quantity_exceeds_remaining`, `wms.task.line_invalid`. Al crear llegan además `wms.profile.variant_unknown`,
`wms.uom.precision_exceeded` y `wms.lot.not_found` (404); al confirmar, los del ledger (`wms.stock.insufficient`,
`wms.location.blocked`, `wms.lot.required`, `wms.movement.occurred_at_out_of_range`…) tal cual.

Una variante con una línea abierta en una tarea sin terminar, o con una novedad abierta, no se borra de Catalog.

#### Novedades y conteo de verificación (T2)

Spec `docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md` §8; plan `docs/superpowers/plans/2026-11-26-wms-t2-novedades.md`.

Una **novedad** (`Incident`) es algo que el operario o el sistema encontró distinto de lo esperado. Catálogo cerrado en T2:
`ShortMove`, `LocationMismatch`, `UnreadableLabel`, `CountVariance` y `SyncConflict`.

| Ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `GET /incidents?warehouseId&status&type&page&pageSize` | `wms.document.read` | — | página de novedades (BFF: códigos, nombres y producto ya resueltos) |
| `GET /incidents/{id}` | `wms.document.read` | — | la ficha, con `allowedResolutions`, `canBeRejected`, evidencia e historia |
| `POST /incidents` (IK) | `wms.document.read` + permiso de algún tipo de tarea | `{ warehouseId, type, locationId?, variantId?, lotId?, quantity?, rawCode?, note, photoFileIds?, sourceTaskId?, occurredAt, deviceId }` | 201 la ficha (200 si se repite la clave). Sólo `UnreadableLabel` y `LocationMismatch` |
| `POST /incidents/{id}/review` | `wms.discrepancy.resolve` | `{ version }` | la ficha |
| `POST /incidents/{id}/resolve` (SK) | `wms.discrepancy.resolve` | `{ version, resolution, note? }` | `{ batchId?, createdTaskIds }` |
| `POST /incidents/{id}/reject` | `wms.discrepancy.resolve` | `{ version, note }` | la ficha |

- **Resoluciones.** `Accept` (`ShortMove`, `LocationMismatch`, `UnreadableLabel`; no mueve nada) y `AcceptIntoStock` (`SyncConflict` con algo
  encontrado y una línea de origen con destino: repite el movimiento de la línea por lo encontrado, con el supervisor como actor, una
  sola llamada al ledger). Sólo `SyncConflict` se rechaza. `CountVariance` no se resuelve ni se rechaza a mano: la cierra su ajuste.
  Quien reportó una novedad no la acepta en el stock (`wms.incident.self_resolution`).
- **`SyncConflict`.** Una acción de línea (`confirm`, `short`, `count`) que llega con el lease perdido se rechaza con 409
  `wms.task.lease_lost`, pero el hecho físico queda como novedad (se confirma antes del 409; quien nunca reclamó la tarea no deja
  novedad). Reintentar con la misma clave no la duplica.
- **El `Count` es a ciegas.** Un `ShortMove` o un `LocationMismatch` con ubicación y variante genera, en la misma transacción, un
  `Count` de esa clave de stock (o se enlaza al que ya está abierto: `count-key`). Una variante de series no genera `Count`. El operario
  no recibe lo esperado: la línea trae lo planeado en cero y el servidor lee el saldo al contar. La persona que reportó el faltante no
  recibe su propio conteo con `next` (el supervisor puede asignárselo). Sin diferencia, el sistema resuelve las novedades; con
  diferencia, propone un ajuste `CountVariance` (nunca toca el ledger) que **no aprueba** quien contó ni quien reportó el faltante, y
  contabilizar el ajuste resuelve la novedad `CountVariance` (rechazarlo la rechaza). Cancelar el `Count` desenlaza sus novedades, que
  siguen abiertas.
- **Códigos.** 404 `wms.incident.not_found`. 422 `wms.incident.self_resolution`, `wms.incident.resolution_not_allowed`,
  `wms.incident.type_not_allowed`, `wms.incident.evidence_invalid`, `wms.incident.invalid`. 412 con la `version` vieja de una acción del
  supervisor. Al contar, además, `wms.serial.required` y `wms.lot.required`.
- **Tablero.** `GET /tasks/summary` trae `openIncidents` y lista el tipo `Count` aunque esté en cero.

**Despliegue de T2.** Las dos migraciones (`AddIncidentsAndShortLines`, `AddCountTasks`) son **sólo de expansión** (tablas nuevas,
una columna nula, `CHECK` que admiten más valores): un pod con el código de T1 sigue escribiendo filas válidas. Lo contrario no es seguro:
T1 lee los enums por nombre y un pod de T1 que cargue una tarea `Count`, una línea `Short` o un evento `LineShort` lanza. Se despliega T2 como
una sola versión (las migraciones corren bajo el candado de arranque antes de que el pod nuevo atienda; con `replicas: 1` el pod viejo
sólo convive hasta que el nuevo está listo, y un `Count` dura 30 minutos, así que el barrido viejo no llega a ver uno). **No hay vuelta
atrás a código anterior a T2 una vez que existe una tarea `Count` o una línea `Short`: se corrige hacia adelante** (el `Down` de
`AddCountTasks` falla a propósito con esos datos). Desde T2 el barrido y el tablero ignoran los valores que el build no conoce, así que
los cortes siguientes no repiten este problema.

**Migraciones:** `Wms` `AddWarehouseTasks`, `AddTaskBoardIndexes` (índices del tablero y del listado), `AddTaskEventRecordedAt` (`recorded_at` en `task_events`, el orden de la historia), `AddIncidentsAndShortLines` (novedades, evidencia e historia; la línea `Short`) y `AddCountTasks` (el tipo `Count`, `task_lines.quantity_counted`).

## Verificación

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build
```

### Pruebas en dos niveles

| Nivel | Comando | Cuándo |
| --- | --- | --- |
| Rápido: sólo lo que cambió la rama | `.\scripts\test-feature.ps1` | Antes de mergear a `develop` |
| Completo: todos los proyectos | `.\scripts\run-all-tests.ps1` | De noche, o antes de llevar `develop` a `main` |

```powershell
.\scripts\test-feature.ps1                 # compara contra develop
.\scripts\test-feature.ps1 -DryRun         # muestra el plan sin compilar ni probar
.\scripts\test-feature.ps1 -Base HEAD~3    # compara contra otra referencia
.\scripts\test-feature.ps1 -NoBuild        # usa la compilación que ya existe
```

El nivel rápido toma lo cambiado desde el punto en que la rama se separó de la base, commiteado o no, y corre:
compilación (con `--no-restore`: restaura antes en un worktree nuevo), `ArchitectureTests`, las unitarias de cada módulo
tocado, `Bootstrapper.UnitTests` si tocaste `Bootstrapper` o `Api`, y **sólo las clases de integración nuevas o
modificadas**. No corre las demás clases de integración que tu cambio pueda romper; esas aparecen en la corrida
completa. Lee los avisos: dicen qué quedó sin probar y qué «Requiere autorización del owner» antes del merge (una
prueba de `ArchitectureTests` modificada o una referencia nueva entre módulos). El reporte queda en
`TestResults\feature\report.md`, y sale con 1 si un paso falla, no deja `.trx` o su filtro no ejecuta ninguna prueba.

Un reporte completo en verde vale para el commit que dice su encabezado, con el árbol limpio. Para programarlo todas las
noches (se registra una vez, desde la raíz del checkout que quieras probar):

```powershell
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -File `"$PWD\scripts\run-all-tests.ps1`""
Register-ScheduledTask -TaskName "qep-backend-pruebas" -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At 1am)
```

Necesita el equipo encendido, Docker Desktop abierto y ningún `Api.exe` ni `testhost` vivo. Si algo de eso falta no hay
reporte nuevo: se nota en la fecha y el commit del encabezado. Las pruebas del propio selector se corren con
`powershell -NoProfile -File scripts\tests\TestSelection.Tests.ps1`.

### Correr todas las pruebas y sacar el reporte de fallas

`dotnet test` sobre toda la solución en paralelo no sirve en Windows: cada clase de integración levanta un
Postgres con Testcontainers y se agotan puertos y memoria. Usa el script, que corre **un proyecto a la vez**:

```powershell
.\scripts\run-all-tests.ps1                          # todo: unitarias primero, luego integración
.\scripts\run-all-tests.ps1 -Kind Unit               # sólo unitarias (no necesita Docker)
.\scripts\run-all-tests.ps1 -Filter Quotations       # sólo los proyectos cuya ruta contenga el texto
.\scripts\run-all-tests.ps1 -Filter Quotations -NoBuild -MaxThreads 2
```

Los proyectos que declaran `[assembly: CollectionBehavior(MaxParallelThreads = n)]` (comparten un solo contenedor) corren
con ese número; los demás, con 4. `-MaxThreads` pisa los dos casos: úsalo si una corrida agota puertos o conexiones.

Antes de correrlo:

- **Docker Desktop debe estar corriendo** (`docker info` responde). Sin daemon, todas las pruebas de integración
  fallan por conexión y el resumen de `dotnet test` muestra miles de "errores" aunque el build esté limpio.
- No debe haber `Api.exe` ni `testhost` vivos; el script se detiene si los encuentra. El build host de C# Dev Kit
  (`dotnet.exe` de VS Code) no estorba.
- `Modules.Ai.Eval` queda fuera: es la evaluación con IA real y se corre aparte (ver su sección).

Salida, en `TestResults/` (ignorada por git y borrada en cada corrida):

- `report.md`: tabla por proyecto (total, pasan, fallan, segundos), las causas de falla más repetidas y la lista de
  pruebas que fallan por proyecto.
- `<proyecto>/<proyecto>.trx`: el detalle completo. Ábrelo con `Get-Content -LiteralPath` (los nombres llevan corchetes).

La suite cubre reglas de los agregados Tenant, Membership y User, dependencias
entre capas, aislamiento entre tenants, permisos de solo lectura, invitaciones
repetidas, concurrencia con `ETag`, escritura de auditoría/Outbox e idempotencia
de Inbox.

### Consultas por request

Con Postgres en otro nodo, la latencia de un endpoint es consultas en serie × ida y vuelta de red (10-20 ms), así que
lo que se mide es cuántos comandos SQL ejecuta un request, no cuánto tarda cada uno.
`RequestQueryBudgetTests` (Tenancy) levanta el host con la autenticación real, pone un `DbCommandInterceptor` en todos
los `DbContext` de módulo y agrupa los comandos por el header `X-Query-Budget` del request; falla si
`GET /memberships?search=` pasa de 8 comandos en frío o de 5 con la caché caliente (hoy son 8 y 3; antes eran 7 + N
miembros). Para presupuestar otra ruta, copia la prueba: un header distinto por request y el listado de SQL sale en el
mensaje de la falla. En producción el mismo número es la cantidad de spans de Npgsql dentro de la traza del request en
Tempo (`{ span.http.route = "/api/v1/tenants/{tenantId}/memberships" } | count() by (span:name)`), o
`db.client.operation.duration` por ruta en Prometheus.

En Quotations el contador no es un interceptor sino el `ActivityListener` de la fuente `Npgsql`, filtrado por la traza del
request (`QuotationsApiHarness.StatementsOfAsync`): cuenta exactamente los spans que ve Tempo, incluidas las dos consultas
transversales del stub de auth (estado del tenant y módulos). El presupuesto es el conteo medido redondeado hacia arriba al
múltiplo de 5: un techo contra regresiones, no una meta.

| Ruta | Prueba | Medido (2026-11-17) | Presupuesto |
| --- | --- | --- | --- |
| `GET /quotations/{id}` (dos líneas, cuenta de cobro) | `RequestQueryBudgetApiTests` (Quotations) | 24 | 25 |
| `GET /orders/{id}` (convertido de esa cotización) | `RequestQueryBudgetApiTests` (Quotations) | 24 | 25 |
