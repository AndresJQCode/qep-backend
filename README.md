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
`CorsSettingsValidator`, `OperatorTenantOptionsValidator` y los dos de Integrations,
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
| `Messaging:Webhook:MaxBodyBytes`                     | `4194304`                                                                                     | Tope del cuerpo del webhook (Meta manda hasta 3 MB); más → 413 |
| `Messaging:Webhook:ConcurrencyLimit` / `QueueLimit`  | `64` / `256`                                                                                  | Limitador `webhook`: concurrencia global con cola, no por IP |
| `Messaging:Workers:*`                                | `DeliveryPollSeconds 3`, `MediaPollSeconds 5`, `CampaignPollSeconds 2`, `PurgeIntervalHours 24`, `DeliveryRetentionDays 7` | Intervalos de los workers de entregas, medios, campañas y purga |
| `Messaging:Campaigns:MessagesPerSecond`              | `20` (opcional; `1 <= valor <= MaxMessagesPerSecond`)                                         | Ritmo inicial y objetivo de envío de una campaña **por conexión**, compartido por todas las réplicas (cubo en Postgres, `messaging.connection_send_budgets`). Es adaptativo: un `130429` de Meta lo baja a la mitad y pausa la conexión 30 s; sin nuevos `130429` sube 1 mps cada 30 s hasta este valor |
| `Messaging:Campaigns:MaxMessagesPerSecond`           | `80` (opcional; hasta `1000`)                                                                 | Techo del ritmo: el throughput por defecto de Meta por número. Súbelo sólo si Meta subió el de tu número |
| `Messaging:MarketingOptOut:ButtonTexts`              | `["Detener promociones", "Stop promotions"]` (opcional)                                       | Textos (o payloads) del botón de respuesta rápida de una plantilla que apagan el marketing del cliente, con origen `button`. Sin mayúsculas ni tildes; un mensaje de texto escrito a mano nunca cuenta. Si se configura, **reemplaza** la lista |
| `Messaging:Search:StatementTimeoutMs`                | `2000`                                                                                        | Tope de la búsqueda en el historial; al pasarlo responde 422 en `q` |

Ejemplo con variables de entorno:

```powershell
$env:ConnectionStrings__QepDatabase = "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev"
$env:OpenTelemetry__Endpoint = "http://localhost:4317"
```

En k8s no se define `OpenTelemetry:Endpoint`: el Collector se referencia con la
variable estándar `OTEL_EXPORTER_OTLP_ENDPOINT` (leída directamente por el
exportador OTLP), junto con `OTEL_SERVICE_NAME` y, opcionalmente,
`OTEL_RESOURCE_ATTRIBUTES` para `service.namespace`/`service.instance.id`.

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

Sin conexión, «Enviar» responde `422 quotation.whatsapp.no_channel`; no existe un envío de mentira
que registre el mensaje y lo dé por enviado.

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

- **No corre dentro del arranque.** El `startupProbe` le da al pod 60 s, y sembrar decenas de miles
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

## Módulos por tenant

Cada tenant tiene prendidos los módulos comerciales que contrató, en `tenancy.tenant_modules`: la
fila **activa** (`status = 'active'`) es el módulo. Una fila `inactive` es un módulo apagado que
conserva quién lo prendió y desde cuándo (`source`, `enabled_at`, `status_changed_at`). Las claves son
`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting`, `pos`, `messaging`, `pipeline`, `shopify` y `automations`; `quotations` exige
`catalog`, `customers` y `companies`, `orders` exige `quotations`, `pos` exige `catalog` y
`companies`, `shopify` exige `orders` y `automations` exige `messaging`. `messaging` (la bandeja de WhatsApp, sin dependencias) no viene con el signup y se prende por tenant desde la consola de operador o con el SQL de respaldo. `pipeline` (el embudo de ventas) exige `customers`, no viene con el signup y se prende igual. `automations` (el constructor de chatbots; último de la lista) no viene con el signup y se prende por tenant desde la consola de operador. Sin `messaging`, el triage de `pipeline` queda vacío; sin `quotations`, no le llegan eventos. Un módulo sin su dependencia cuenta como apagado. Identidad, Tenancy, Authorization,
Storage, Platform, Geography, Audit, Notifications e Integrations son núcleo y no se apagan;
Integrations filtra su catálogo de proveedores por los módulos activos del tenant.

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
| `/api/v1/tenants/{tenantId}/files`                 | `GET`, `POST`, y `complete`, `metadata`, `download-url`, `publication`, borrado por archivo | `storage.file.read` / `.upload` / `.publish` / `.delete`                                     |
| `/api/v1/tenants/{tenantId}/customers`             | `identification-matches` (`GET`) y `anomalies` (`GET`); el resto del módulo está en sus secciones (ver [Clientes: documento repetido](#clientes-documento-repetido-coincidencias-y-anomalías)) | `customers.customer.read` |
| `/api/v1/tenants/{tenantId}/customers/{customerId}` | `merge-preview` (`GET`) y `merge` (`POST`, `If-Match`); ver [Clientes: fusión](#clientes-fusión) | `customers.customer.merge` |
| `/api/v1/tenants/{tenantId}/orders`                | `GET` (filtros, entre ellos `source` y `timeZone`), `POST` (pedido manual), `preview` (`POST`), `export` (`POST`, mismos filtros), y por pedido `GET`, `PUT` (`If-Match`), `preview`, `approve`, `cancel`, `invoice`, `uninvoice`, `proofs` (`POST`, `DELETE`), `items` (`POST`), `send` (`POST`, cuerpo opcional) (ver [Pedidos](#pedidos-datos-propios-y-pedido-manual) y [Enviar el pedido por WhatsApp](#enviar-el-pedido-por-whatsapp)) | `quotations.order.read` / `.manage` / `.approve` / `.cancel` / `.invoice` |
| `/api/v1/tenants/{tenantId}/pos`                   | 14 operaciones: caja y ventas del punto de venta (ver [POS](#pos-caja-y-ventas))             | `pos.register.operate` / `pos.sale.read` / `.create` / `.void` / `.customer`                 |
| `/api/v1/tenants/{tenantId}/operator/tenants`      | `GET`, y por tenant `GET`, `modules/changes` (`POST`), `providers/changes` (`POST`), `status` (`POST`, `If-Match`), `history` (`GET`) | `operator.tenants.read` / `operator.modules.manage` / `operator.tenants.manage`, sólo en el tenant operador |
| `/api/v1/tenants/{tenantId}/integrations`          | `catalog` (`GET`), `connections` (`GET`, `POST`), y por conexión `GET`, `PUT` (`If-Match`), `test` (`POST`), `pause` y `resume` (`POST`, `If-Match`), `DELETE` (`If-Match`) | `integrations.connection.read` / `.manage` |
| `/api/v1/tenants/{tenantId}/integrations/whatsapp/embedded-signup` | `POST` | `integrations.connection.manage` |
| `/api/v1/tenants/{tenantId}/messaging` | `conversations` (`GET`), por conversación `GET`, `messages` (`GET`, `POST`), `messages/audio` (`POST`, multipart), `read` (`POST`), `resolve` (`POST`, `If-Match`, cuerpo opcional `{ reasonId }` o `{ reasonName }`) y `reopen` (`POST`, `If-Match`), `automation/pause` y `automation/resume` (`POST`, `If-Match`, `.manage`; ver [Asistente en la conversación](#asistente-en-la-conversación)); `messages/search` (`GET`); `media/{messageId}` (`GET`); `conversations/{id}/customer` (`PUT`, `If-Match`, cuerpo `{ customerId }`, `.manage`; ver [Identidad, clientes y asignación](#identidad-clientes-y-asignación-spec-2026-10-10)) | `messaging.conversation.read` / `.manage` |
| `/api/v1/tenants/{tenantId}/messaging/quick-replies` | `GET`, `POST`; por respuesta `PUT` (`version` en el cuerpo) y `DELETE` (`?version=`) | `messaging.conversation.read` (`GET`) / `messaging.quick_reply.manage` |
| `/api/v1/tenants/{tenantId}/messaging/resolution-reasons` | `GET`; por motivo `PATCH` (`If-Match`) y `DELETE` (archiva). Se crean al resolver | `messaging.conversation.read` (`GET`) / `messaging.resolution_reason.manage` |
| `/api/v1/tenants/{tenantId}/messaging/business-hours` | `GET`, `POST`; por horario `PUT` (`If-Match`), `DELETE` (`If-Match`), `duplicate` (`POST`) y `default` (`POST`, `If-Match`) (ver [Horarios de atención](#horarios-de-atención)) | `messaging.conversation.read` (`GET`) / `messaging.business_hours.manage` |
| `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management` | `GET`, `templates` (`POST`), por plantilla `DELETE`, `quotation` (`POST`), `sync` (`POST`) (ver [Plantillas de WhatsApp desde QEP](#plantillas-de-whatsapp-desde-qep)) | `messaging.template.manage` |
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
  `Converted` y congelada; editar el pedido pendiente (`PUT /orders/{id}`, `POST /orders/{id}/items`)
  cambia `order_items`, nunca la cotización.
- `Manual`: `POST /orders` crea un pedido sin cotización desde la pantalla, con el mismo cuerpo que
  una cotización más el estado de pago y los comprobantes; `POST /orders/preview` lo calcula sin
  guardarlo ni gastar número, y sin cuenta de cobro lo precia en la moneda por defecto del tenant (crear
  sí la exige). Exige cliente completo con CUC, como convertir. Permiso
  `quotations.order.manage`.
- Integraciones (`Shopify`): sin sesión HTTP, por `IExternalOrderWriter`; acepta un cliente
  incompleto y lleva `externalReference` (`#1001`).

Todos nacen `Pending` y publican `quotations.order.created.v1` con `source` y `quotationId`
(nulo sin cotización). Pipeline lo consume: un pedido sin cotización gana el único negocio abierto
del cliente o crea uno ganado (`pipeline.deal_orders`). La lista (`GET /orders?source=`), el
detalle, el Excel y el reporte de pedidos leen del pedido. Los pedidos que existían antes los copió
la migración `AddOrderOwnData` desde su cotización.

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

### Enviar el pedido por WhatsApp

`POST /api/v1/tenants/{tenantId}/orders/{orderId}/send` le manda al cliente el PDF del pedido por
WhatsApp, como `POST /quotations/{id}/send` lo hace con la cotización. Contrato completo y códigos de
error en [`docs/integracion-cotizaciones-y-pedidos.md`](docs/integracion-cotizaciones-y-pedidos.md).

- **Permiso** `quotations.order.manage`. **Cuerpo opcional** `{ "recipient": "Customer" | "Billing" }`
  (default `Customer`): el teléfono lo resuelve el backend — el del cliente, o el de la facturación
  propia del pedido (`order.order.billing_phone_missing` si no la tiene).
- **Responde el `OrderDetailResponse` entero**, con la entrada `Sent` en el historial.
- **No cambia el pedido**: ni estado ni `version`. Se manda en `Pending`, `Approved` e `Invoiced`; un
  anulado responde `order.order.already_cancelled` antes de cualquier efecto externo. Deja historial
  (`OrderHistoryEventType.Sent`, que se guarda como texto: sin migración) y auditoría
  `quotation.order.sent`. No exige cliente completo: un pedido de Shopify puede tenerlo incompleto.
- **El PDF es el de la cotización** (`quotation.typ`, layout único por decisión del owner) con
  `kind: "Order"`: dice «Pedido» en el membrete y el pie, no imprime «Válida hasta» y el archivo se
  llama `Pedido-<número>.pdf`. Sale del detalle del pedido (`QuotationPdfDocumentMapper.FromOrder`),
  nunca de la cotización de origen, que puede no existir.
- **Sin caché ni migración**: se genera en cada envío y se guarda bajo `quotations/tenants/…/orders/`
  (lo limpia la misma regla de lifecycle), y la copia pública para Meta sale como la de la cotización.
  Cachearlo pediría una tabla invalidada por `Order.Version`, y enviar un pedido es raro.
- **Sin registro propio de fallas**: el log de fallas de la API guarda la excepción, y `order.send.failed`
  nombra el paso.
- **Plantilla propia en Zenvia**: el `orderTemplateId` de la conexión de Zenvia del tenant, ver
  [Plantilla de WhatsApp](#plantilla-de-whatsapp-zenvia). Sin él, enviar un pedido responde
  `order.whatsapp.not_configured` antes de generar el PDF; sin conexión, `order.whatsapp.no_channel`, y con
  más de una, `order.whatsapp.channel_required`.

### Catálogo: opciones, variantes y galería

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
- Las líneas enviadas antes de este cambio no tienen foto y se siguen leyendo en vivo; no hay
  backfill, porque Quotations no lee las tablas de Catalog. Se congelan solas si la cotización se
  reenvía o se convierte.
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
`freight_without_tax`, `freight_tax`, `freight_charge_mode` y `cash_on_delivery` (2026-10-17) y
`variant` (2026-10-20).

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
| `GET /products`                    | `pos.sale.create`      | Búsqueda paginada por nombre, `Code` o código de barras: productos con sus variantes vendibles |
| `GET /products/by-code`            | `pos.sale.create`      | El escáner: la variante por código de barras exacto y, si ninguna lo tiene, por su `Code` |
| `POST /sales/preview`              | `pos.sale.create`      | Calcula totales de un carrito sin guardar nada; cada línea es una variante (`variantId`) |
| `POST /sales`                      | `pos.sale.create`      | Crea la venta, con una línea por variante; idempotente por el `id` del cliente (201 la primera vez, 200 al repetir); `customerId` opcional |
| `GET /customers`                   | `pos.sale.customer`    | Clientes elegibles de la caja: activos con documento, hasta 20, por nombre, documento o CUC |
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

### Clientes: fusión

Spec `2026-10-24-fusion-de-clientes`. Junta clientes duplicados en uno; **no se puede deshacer**.

- **Rutas** (`customers.customer.merge`, riesgo `high`; lo tienen por defecto los roles de sistema `admin` y `advisor`, en código):
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
dinámicamente por módulo. Los roles no están hardcodeados: `RoleDefinition` /
`RoleCatalog` mapean un rol (p. ej. `admin`, `advisor`) a un
conjunto de permisos.

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

### Infraestructura local

`compose.yaml` solo levanta PostgreSQL 18 y el `otel-collector`. No hay Redis
ni broker de mensajería: el patrón Outbox cumple ese rol dentro del monolito.

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
adjunto, a través de una plantilla aprobada por Meta. Sale por **la conexión de Zenvia del tenant**
(Integraciones, plan 2026-10-21): cuál es la plantilla vigente lo dice su campo `templateId`, y el
número emisor, su `fromNumber`. No hay cuenta ni plantilla global. Un tenant sin conexión disponible
recibe `422 quotation.whatsapp.no_channel`, y uno con más de una, `quotation.whatsapp.channel_required`
(elegir entre varias es del plan 3 del spec 2026-10-21). Las variables de la plantilla son las que
`ZenviaWhatsAppSender` completa en runtime:

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

> **Decisión pendiente (owner):** el texto de la plantilla del pedido, su `name` y su aprobación en
> Zenvia/Meta. Por eso `orderTemplateId` es opcional en la conexión: sin él, el endpoint responde
> `order.whatsapp.not_configured`. Se crea con el mismo procedimiento de abajo, cambiando
> `components.body.text` y quitando `valid_until` de `examples`, y su `id` se carga en la conexión de cada
> tenant.

**Cambiar el texto de la plantilla no se hace acá: se crea una plantilla nueva en Zenvia y se
edita el `templateId` de la conexión de Zenvia del tenant con el `id` que devuelva.** Meta no permite
editar una plantilla aprobada, y en Zenvia la plantilla va atada al número emisor: cada tenant usa la
que creó con su propia cuenta. Si además cambian las variables, hay que tocar
`WhatsAppQuotationMessage` y `ZenviaWhatsAppSender`, y actualizar el `templateId` de las conexiones en
el mismo despliegue.

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

**Sin la API key el pod no arranca en producción.** A diferencia de WhatsApp, acá no hay
fallback: `qcode-pdf` respondería 401 y todo envío moriría con `quotation.pdf.render_failed`.
`QuotationsOptionsValidator` la exige con `ValidateOnStart` porque descubrirlo cuando una
asesora aprieta *Enviar* es más caro que un despliegue que no levanta. Fuera de producción la
clave es opcional.

`BaseUrl` debe ser HTTPS absoluta **en todo ambiente**, incluido local: la cotización completa
—precios, cliente, totales— viaja en el cuerpo del POST.

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
`statuses` sí se aplican. Las entregas procesadas se purgan a los 7 días. No se suscriben los campos
de coexistencia (`history`, `smb_app_state_sync`, `smb_message_echoes`): lo que se responde desde la app
del teléfono no aparece en QEP todavía.

**Entrantes que Meta no entrega.** Un mensaje `type: "unsupported"` se guarda como `Unsupported` con el `type` y los
`errors` de Meta en `details`. Al leer, `unsupportedReason` lo traduce a español como `failureReason`
(`UnsupportedMessageReasons`): `131060` (la persona escribió pero el contenido no llegó; Meta no lo reenvía) y
`131051` (tipo que la Cloud API no entrega); cualquier otro código viaja `null`. La lista de conversaciones no lo
trae. Handoff al frontend: `docs/superpowers/plans/2026-10-14-mensaje-no-disponible-frontend.md`.

**Lista e hilo: paginación por cursor.** `GET …/conversations` devuelve `{ items, nextCursor, counts }`: el
cliente manda `nextCursor` tal cual en `?cursor=` para la página siguiente (`pageSize` 1–50, default 30) y
`null` significa que no hay más. El cursor es la posición `(last_activity_at, id)` de la última fila, así que una
conversación que sube al tope mientras se recorre la lista no repite ni salta filas, que es lo que pasaba con
`OFFSET`. Un cursor que no emitió el servidor da 422 `validation.failed` en `cursor`. No hay `total` ni `page`:
los números de las pestañas son `counts`. El hilo (`…/messages?before=&limit=`) ya usaba el id del mensaje más
viejo como cursor, con `hasMore`.

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
el lado mayor en 1600 px y sin EXIF (el original se borra); documento pdf, doc, docx, xls o xlsx hasta 100 MB. `caption`
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

**La tabla está en borrador hasta las verificaciones V-1..V-3 con Meta real** (las corre el owner; ver el spec): lo que Meta
no acepte se quita de `OutboundMediaPolicy` y de esta tabla. No hay transcodificación ni `ffmpeg`: `.mov`, `.webm`, `.avi`,
`.wav`, `.flac`, `.wma` y el `.ogg` Vorbis son `unsupported_format`. El video sale con `type: video` y su `caption`; el audio,
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
El cuerpo va de 1 a 4096 caracteres y sólo admite `{nombre}`, `{asesor}` y `{empresa}`. El `GET` devuelve la lista
entera por atajo y `variables.asesor` / `variables.empresa` ya resueltos para quien llama; `{nombre}` lo resuelve el
frontend con la conversación. `PUT` y `DELETE` exigen la versión (412 si cambió).

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
| Fanout | `pg_notify('messaging_events', …)` después del commit (`PgMessagingNotifier`); cada pod tiene un `MessagingEventsListener` en `LISTEN` que reparte a sus conexiones. Con dos réplicas no hace falta sticky session ni Redis. |
| Fallos | Un aviso que falla no reintenta la entrega ni deshace el envío: se registra y el polling relajado del frontend (60/30 s) lo recoge. |

Lo que se publica: asignar, transferir, liberar, resolver, reabrir, leído, no leído, saliente consolidado
(`Sent` o `Failed`), acuse aplicado y entrante insertado. Un reenvío de Meta no avisa. Instagram va por el mismo
camino: un DM es un entrante y suena igual; un `is_echo` (el negocio respondió desde la app) mueve la foto de la
lista sin ser un entrante, así que sólo avisa `conversationChanged`; el visto (`read`) y el borrado (`is_deleted`)
avisan `conversationChanged` como un acuse de WhatsApp.

**Runtime ≥ 10.0.13.** CVE-2026-70159 (DoS en SignalR, `SignalR.Common` ≤ 10.0.12) afecta al servidor, que
sale del runtime del `Dockerfile`, no de NuGet. Por eso la imagen se clava en `aspnet:10.0.13` y no en el flotante
`10.0`, y los paquetes `Microsoft.*` de `Directory.Packages.props` van en la misma línea 10.0.13.

La preferencia de sonido viaja en `GET/PUT /api/v1/auth/preferences` como `messageSound` (default `true`). El
`PUT` acepta cada campo por separado y conserva el que no viene; un cuerpo vacío es 422
`identity.preference.empty`.
#### Plantillas de WhatsApp desde QEP

Spec: `docs/superpowers/specs/2026-10-14-gestor-de-plantillas-whatsapp-design.md`. El admin (permiso
`messaging.template.manage`, de fábrica sólo en `admin`) gestiona las plantillas de cada conexión `whatsapp-cloud`
bajo `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management`:

| Método y ruta | Qué hace |
| --- | --- |
| `GET /` | La pantalla: conexión, estado de la plantilla de cotización, última sincronización y todas las plantillas con su estado y motivo de rechazo. Si la conexión nunca se sincronizó, la primera lectura llena el espejo. |
| `POST /templates` | Crea una plantilla libre (texto, variables con nombre, pie, botones) y la manda a aprobar. |
| `POST /quotation` | Aprovisiona la plantilla de cotización con su PDF de ejemplo; reintenta si la anterior fue rechazada; adopta una `qep_cotizacion` igual creada a mano en Meta. |
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
versión siguiente con el mismo PDF, hasta cinco creaciones. El prefijo `qep_cotizacion_` está reservado: ninguna libre
lo puede usar. Hay a lo sumo una fila `Quotation` por conexión (`UX_message_templates_quotation`); el nombre vigente es
el de esa fila, no una constante. El PDF de ejemplo lo genera el backend (`QuotationTemplateDefinition.ExamplePdf()`).
Si el admin borra la `Quotation` desde la pantalla, la fila desaparece y el siguiente aprovisionamiento vuelve a
calcular `qep_cotizacion`, que Meta tiene en cuarentena: **la recuperación depende de reconocer ese error** como
`name_taken`. `TemplateGraphErrors` lo hace cuando `error_user_title` o `error_user_msg` dicen «already exist»,
«being deleted», o «deleted» junto a «language» o «template»; el cuerpo real está a confirmar (handoff de Meta). Si
no lo reconoce, sale `rejected_by_meta` y cada intento recalcula la versión 1.

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
token cuando le quedan menos de 15 días; un `190` lo pasa a `NeedsAttention` (`token_expired`).

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
| `GET` | `/triage` | `pipeline.deal.read` |
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
`Ganado`/`Perdido` sólo de los últimos 30 días. El triage trae como máximo 200 conversaciones con el conteo
real. Cada decisión de triage guarda el último entrante contra el que se tomó: con el índice único
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
- **Borrar** un chatbot con corridas vivas es 409 `automations.chatbot.has_live_runs`.
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
      la conversación que se abre o se reabre **después** de publicar: una que se abrió entre el Embedded Signup y la
      publicación sigue sin bot hasta que se resuelva y vuelva a abrir, así que este paso va enseguida del anterior.
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
  defecto y las claves del atributo del documento.
- El comprador se busca por documento, por el cliente de Shopify ya visto en el espejo, por teléfono y por correo; si no
  aparece, nace completo (con CUC) cuando hay documento, clasificación, dirección y ciudad, e incompleto si no. El
  cliente nunca detiene la importación salvo que exista y esté inactivo.
- El pedido entra por `IExternalOrderWriter` con la clave externa `"{shopId}:{shopifyOrderId}"`: el índice único
  `IX_orders_tenant_external_key` hace que el mismo pedido dos veces (webhook y conciliación a la vez, o el worker que
  cae entre crear el pedido y guardar la fila) deje un solo pedido. Lo que entra con algo que revisar lleva `warnings`
  (`customer_incomplete`, `shipping_address_unresolved`, `total_difference`, `carrier_unrecognized`,
  `cash_on_delivery_unmatched`); la bandeja los saca de «Con avisos» con «Marcar como revisado».
- Después de importado, sólo se sincronizan el pago (un pedido `Pending` sin comprobantes; con comprobantes o ya
  aprobado, el rechazo queda en `last_error`) y la cancelación (se anula en QEP a nombre del asesor; uno ya facturado
  queda `Cancelled` con `invoiced_before_cancel` para anularlo a mano). Un pedido cancelado que llega sin fila del espejo
  pero con su pedido en QEP también se anula.
- `ShopifyReconcileWorker` corre cada `Shopify:Reconcile:IntervalMinutes` por tienda operativa: trae por GraphQL lo
  actualizado desde la última corrida (5 minutos de solape; la primera, desde el enlace: no se importa historial) y
  reintenta hasta 200 `Waiting`. Así, crear el producto que faltaba o prender el módulo otra vez surte efecto sin tocar
  nada más. `POST …/shopify/stores/{connectionId}/sync` hace lo mismo en el request; `POST …/shopify/orders/{id}/retry`,
  una sola fila.

**La bandeja.** `GET /tenants/{tenantId}/shopify/orders` pagina el espejo (tope 50, más recientes primero) con
`state` (repetible o separado por comas; sin él, `Waiting`), `connectionId`, `withWarnings=true` (avisos sin revisar;
sin `state` no filtra por estado) y `cancelledAfterInvoice=true`: sólo las `Cancelled` cuyo `wait_detail` es
`{"reason":"invoiced_before_cancel"}`. Ese filtro implica `state=Cancelled`; con otro `state` responde 422
`validation.failed` con `errors.cancelledAfterInvoice`. Sin `connectionId`, el `total` de `withWarnings=true` y el de
`cancelledAfterInvoice=true` son iguales a los contadores de `GET …/shopify/orders/summary`, así que la sección pagina
en el servidor y nunca filtra en el navegador. `cancelledAfterInvoice=true` junto con `withWarnings=true` suma los dos
filtros (AND): los cancelados después de facturar que todavía tienen avisos sin revisar.

Cada fila trae `lastError` —texto libre para soporte y logs, que la pantalla no muestra— y `lastErrorCodes`, siempre
presente (`[]` sin error): los códigos de `lastError` partidos por `"; "`, en orden y sin repetir. Un código de
Quotations viaja tal cual; `invalid_payload: …` se vuelve `shopify.order.invalid_payload`; cualquier otra cosa (un
nombre de enum, un texto) es `shopify.order.sync_failed`. Hoy los valores posibles son `order.order.not_pending`,
`order.order.payment_has_proofs`, `order.order.not_found`, `tenancy.tenant.not_found`,
`shopify.order.invalid_payload` y `shopify.order.sync_failed`; el texto de cada uno lo tiene el diccionario del frontend
(`features/shopify`), y uno que no conozca se muestra con un mensaje genérico. La normalización está en
`ShopifyLastErrorCodes`.

**Configuración.**

| Clave | Dónde vive en prod | Qué |
| --- | --- | --- |
| `Shopify:App:ClientId` | ConfigMap, desde `SHOPIFY_APP_CLIENT_ID` | Client ID de la app en el Dev Dashboard |
| `Shopify:App:ClientSecret` | Secret, desde la variable **secreta** `SHOPIFY_APP_CLIENT_SECRET` | Firma los webhooks, verifica el HMAC de la instalación y canjea el `code` |
| `Shopify:App:ApiVersion` | ConfigMap, `2026-10` | Versión de la Admin API (`^\d{4}-(01\|04\|07\|10)$`) |
| `Shopify:App:Scopes` | ConfigMap, `read_orders,read_customers` | Lo que pide la autorización |
| `Shopify:App:AllowedRedirectOrigins:N` | ConfigMap, `https://qep.qcode.co` | Orígenes del `redirect_uri` (local: `http://localhost:3002`, en `appsettings.Development.json`) |
| `Shopify:App:WebhookCallbackUrl` | ConfigMap, `https://qep-api.qcode.co/api/webhooks/shopify` | Lo que el enlace registra en cada tienda |
| `Shopify:Reconcile:IntervalMinutes` | ConfigMap, `10` | Periodo de la conciliación |
| `Shopify:Webhook:MaxBodyBytes`, `DeliveryPollSeconds` | defaults del código (`ShopifyWebhookOptions`: 1 MiB y 3 s); se pisan por configuración si hace falta | Tope del cuerpo (413) y sondeo del worker |

En `Production`, `ShopifyAppOptionsValidator` exige con `ValidateOnStart` `ClientId`, `ClientSecret`,
`WebhookCallbackUrl` (https) y al menos un origen; un `ClientId` o `ClientSecret` que llega como `#{…}#` (la variable
no está en `Backend-prod`) cuenta como ausente. Sin ellas el pod entra en crash-loop, a propósito. Fuera de
`Production`, sin `ClientId` y `ClientSecret` el proveedor sale del catálogo y el módulo arranca igual.

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
   `AddCustomerEmailIndex` en Customers, `AddShopifyProvider` y `AddShopifyInstalls` en Integrations, `InitialShopify` y
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

## Verificación

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build
```

La suite cubre reglas de los agregados Tenant, Membership y User, dependencias
entre capas, aislamiento entre tenants, permisos de solo lectura, invitaciones
repetidas, concurrencia con `ETag`, escritura de auditoría/Outbox e idempotencia
de Inbox.
