# QEP Backend

Backend de **QCode Enterprise Platform (QEP)** implementado como monolito
modular sobre .NET 10.

El alcance ejecutable actual incluye:

- el corte vertical de configuración de tenants, con lectura y actualización;
- ciclo de vida completo de memberships: invitación con aprovisionamiento de
  usuarios en Identity, listado, suspensión, remoción, reactivación y roles;
- registro público de tenants, sesión por cookie y catálogo de autorización;
- catálogo de productos, con listado, alta, edición y desactivación;
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

- .NET SDK `10.0.400` o un parche posterior compatible con `global.json`;
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
`CorsSettingsValidator` y `OperatorTenantOptionsValidator`): si algo
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
| `Cors:AllowedOrigins`                                  | ausente (sin CORS: en local la SPA va por el proxy de Vite)                                   | Orígenes exactos desde los que el navegador llama a la API con la cookie de sesión. Cada uno `https://host[:puerto]`, sin barra final, path ni comodín; uno inválido ⇒ la API no inicia. La defensa CSRF depende de que sea exacta: un comodín —ni siquiera `*.qcode.co`, donde corren otras apps— la desactiva (ver [Pipeline HTTP](#pipeline-http-programcs)). En k8s: `https://qep.qcode.co` |
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
secretos de usuario de `Api`. Copie los comandos y solo reemplace los valores
`<...>`; ejecútelos desde este directorio.

> Verifique con `dotnet user-secrets list --project src/Api`. No agregue estos
> valores a `appsettings.Development.json`. `SenderEmail` (Infobip) no es secreto
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

Zenvia (envío de la cotización por WhatsApp, `SendQuotation.cs` — "de momento" con estas dos
variables). Sin configurar, `AddWhatsAppSender` cae a `LogWhatsAppSender` (registra el mensaje,
no llama a nada externo): "Enviar" sigue funcionando igual que hoy, sólo que sin mandar el
WhatsApp real hasta que esto se aprovisione:

```powershell
dotnet user-secrets set "Quotations:WhatsApp:ApiToken"    "<zenvia-api-token>"    --project src/Api
dotnet user-secrets set "Quotations:WhatsApp:FromNumber"  "<zenvia-from-number>" --project src/Api
```

> `TemplateId` no es secreto — vive en `appsettings.json`, mismo criterio que
> `SenderEmail` en Infobip. Pisarlo por ambiente es opcional
> (`Quotations:WhatsApp:TemplateId`).

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
evitarlo, pedila sin eco:

```powershell
$k = Read-Host "clave qep-dev" -AsSecureString
[Environment]::SetEnvironmentVariable("QCODE_PDF_API_KEY",
  [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($k)), "User")
```

Después **cerrá VS Code por completo y volvé a abrirlo**: los procesos heredan el entorno al
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
[homologación](#columnas-del-excel-de-pedidos-por-tenant-homologación)). Pensada para el ambiente
desplegado durante el desarrollo, donde la base se borra y se vuelve a crear: después
de un borrado no hay ningún paso manual, alcanza con que la aplicación reinicie.

| Clave                          | Por defecto | Qué hace                                                                      |
| ------------------------------ | ----------- | ----------------------------------------------------------------------------- |
| `Seed__Enabled`                | `false`     | Interruptor de la semilla de arranque. Apagado, no se siembra nada            |
| `Seed__OwnerEmail`             | sin valor   | Email que recibe la membresía con rol `admin`                                 |
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

Es idempotente: el tenant por id, el usuario por email, la membresía por el par
usuario-tenant y los productos por código. Correrla muchas veces —cada reinicio de pod
lo hace— no duplica nada. Tampoco actualiza: cambiar un precio ya sembrado es un `PUT`,
no una segunda corrida.

Tres campos del archivo de origen no se cargan porque el dominio no los tiene: peso
neto, peso bruto y unidad de empaque. La imagen tampoco: en el origen es una ruta, y
`Product.ImageFileId` es un archivo de la [biblioteca](#biblioteca-de-archivos-cloudflare-r2),
que se sube aparte. Los cuatro quedan en
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
`catalog`, `customers`, `companies`, `quotations`, `orders`, `reporting` y `pos`; `quotations` exige
`catalog`, `customers` y `companies`, `orders` exige `quotations` y `pos` exige `catalog` y
`companies`. Un módulo sin su dependencia cuenta como apagado. Identidad, Tenancy, Authorization,
Storage, Platform, Geography, Audit y Notifications son núcleo y no se apagan.

Apagar un módulo descarta sus permisos en el request siguiente, por cookie y por el stub de
desarrollo (este último sólo cuando el tenant existe en `tenancy.tenants`). No borra datos ni corta
trabajos en vuelo. La SPA lo lee de `GET /api/v1/tenants/{tenantId}/modules` (autenticado, sin
permiso), que siempre devuelve los siete con `enabled`, `contracted` y `missingDependencies`, y se
entera en hasta 5 minutos.

Un tenant del signup nace con los seis sin `pos` mientras `Entitlements:GrantDefaultModulesOnSignup`
esté en `true` (el default), y sin ninguno en `false`. El de la semilla nace con los siete. Ni el
signup ni la semilla escriben historial: el origen queda en `source`.

### Consola de operador

La vía normal para prender y apagar módulos, y para inactivar o reactivar un tenant, es la **consola
de operador**: la sección «Plataforma» de la SPA, que sólo aparece para el tenant configurado en
`Platform:OperatorTenantId` (QCode). Cada operación queda en `tenancy.tenant_changes` con motivo,
nota opcional, quién y cuándo, agrupada por lote; el historial no se edita ni se borra. Un lote que
dejaría un módulo activo sin una dependencia se rechaza con
`422 tenancy.modules.inconsistent_dependencies`: la consola arma la cascada sola.

**Inactivar un tenant** lo saca del selector de sesión y todo request a ese tenant recibe 403,
incluso con la sesión ya abierta. No toca sus módulos: reactivarlo deja todo como estaba. Los
workers, el outbox y los enlaces públicos (PDF, comprobantes) de un tenant inactivo **siguen
funcionando** (DECISIÓN-PENDIENTE del spec 2026-10-08). El tenant operador no se puede inactivar.

### Tenant operador

`Platform:OperatorTenantId` es **opcional en todo ambiente**:

- Sin la clave no hay consola: los endpoints `/operator/*` responden 403 a todos y el resto de la
  API funciona igual. En `Production`, además, se registra una advertencia al arrancar.
- `Guid.Empty` tumba el arranque (`OperatorTenantOptionsValidator` con `ValidateOnStart`).
- **No** va en `appsettings.json` ni como marcador en el ConfigMap: el id de QCode no está en el repo
  y el CI despliega `main` sin pruebas, así que un valor de relleno tumbaría los pods. Su lugar en el
  inventario es `src/Api/appsettings.example.json`.

Para habilitarla en producción:

1. Desplegar el backend antes que el frontend. Sin la clave, el despliegue no cambia nada visible.
2. Obtener el id de QCode por el acceso a la base que QCode ya usa para operaciones manuales:
   `SELECT id FROM tenancy.tenants WHERE slug = '<slug de QCode>';`
3. Agregar `Platform__OperatorTenantId: "<id>"` al ConfigMap de producción y reiniciar el despliegue.
4. El `admin` de QCode ve «Plataforma» (puede requerir recargar la SPA).

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
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note, status, status_changed_at) SELECT id, 'pos', now(), 'manual', 'Activado por QCode', 'active', now() FROM tenancy.tenants WHERE slug = 'origen-botanico' ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now();"

# Apagar orders (no deja historial)
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "UPDATE tenancy.tenant_modules m SET status = 'inactive', status_changed_at = now() FROM tenancy.tenants t WHERE t.id = m.tenant_id AND t.slug = 'origen-botanico' AND m.module_key = 'orders';"
```

**Después de desplegar `AddTenantModules`** (checklist del despliegue): un pod viejo puede crear
tenants sin filas durante el rolling update. La consulta sólo lista —un tenant sin filas también puede
ser legítimo— y la reparación se hace por slug, después de mirar cada uno:

```powershell
# Tenants sin módulos creados en el último día
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "SELECT t.id, t.slug, t.created_at FROM tenancy.tenants t WHERE NOT EXISTS (SELECT 1 FROM tenancy.tenant_modules m WHERE m.tenant_id = t.id) AND t.created_at >= now() - interval '1 day' ORDER BY t.created_at;"

# Reparar uno: los seis del signup
docker exec postgres18 psql -U postgres -d dev_lulo_crm_v2 -c "INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, note) SELECT t.id, m.key, now(), 'manual', 'Alta durante el despliegue de AddTenantModules' FROM tenancy.tenants t CROSS JOIN (VALUES ('catalog'),('customers'),('companies'),('quotations'),('orders'),('reporting')) AS m(key) WHERE t.slug = 'slug-del-tenant' ON CONFLICT (tenant_id, module_key) DO NOTHING;"
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

| Grupo de rutas                                     | Operaciones                                                                                 | Autorización                                                                                 |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `/health/live`                                     | `GET`                                                                                       | anónimo                                                                                      |
| `/health/ready`                                    | `GET`                                                                                       | anónimo                                                                                      |
| `/api/v1/auth/registration-policy`                 | `GET`                                                                                       | anónimo                                                                                      |
| `/api/v1/auth/register-tenant`                     | `POST`                                                                                      | token del proveedor OIDC                                                                     |
| `/api/v1/auth/session`                             | `POST`                                                                                      | token del proveedor OIDC                                                                     |
| `/api/v1/auth/me`, `/api/v1/auth/logout`           | `GET`, `POST`                                                                               | sólo autenticación                                                                           |
| `/api/v1/tenants/{tenantId}/authorization/me`      | `GET`                                                                                       | sólo autenticación (deliberado: pedir permiso para saber qué permisos se tienen es circular) |
| `/api/v1/tenants/{tenantId}/authorization/catalog` | `GET`                                                                                       | `advisorship.read`                                                                    |
| `/api/v1/tenants/{tenantId}/settings`              | `GET`, `PUT`                                                                                | `tenancy.settings.read` / `.update`                                                          |
| `/api/v1/tenants/{tenantId}/memberships`           | `POST`, `GET`, y `suspend`, `remove`, `reactivate`, `roles`, `profile` por membership   | `advisorship.invite` / `.read` / `.manage`                                            |
| `/api/v1/tenants/{tenantId}/catalog/products`      | `GET`, `POST`, `PUT`, y `deactivate` por producto                                           | `catalog.product.read` / `.manage`                                                           |
| `/api/v1/tenants/{tenantId}/files`                 | `GET`, `POST`, y `complete`, `metadata`, `download-url`, `publication`, borrado por archivo | `storage.file.read` / `.upload` / `.publish` / `.delete`                                     |
| `/api/v1/tenants/{tenantId}/pos`                   | 12 operaciones: caja y ventas del punto de venta (ver [POS](#pos-caja-y-ventas))             | `pos.register.operate` / `pos.sale.read` / `.create` / `.void`                               |
| `/api/v1/tenants/{tenantId}/operator/tenants`      | `GET`, y por tenant `GET`, `modules/changes` (`POST`), `status` (`POST`, `If-Match`), `history` (`GET`) | `operator.tenants.read` / `operator.modules.manage` / `operator.tenants.manage`, sólo en el tenant operador |

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
membresía asesora de la cotización con el código **de hoy** —si se lo cambian, los pedidos viejos
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

### Código y nombre del producto en cada línea

Una línea de cotización referencia su producto por `productId` (sin FK) y congela precio e IVA al
agregarse. El **código y el nombre** siguen otra regla (owner, 2026-09-26):

- Mientras la cotización es **borrador**, se leen en vivo del catálogo: si el producto cambia, la
  cotización lo muestra.
- Cuando **sale del borrador**, cada línea guarda el código y el nombre que tenía su producto en ese
  momento (`quotation_items.product_code` / `product_name`), y desde ahí la cotización y su pedido
  ya no cambian con el catálogo. Sale del borrador al **enviarla** y también al **convertirla en
  pedido** sin haberla enviado.
- Lo congelado no se pisa nunca: ni un reenvío ni la conversión lo vuelven a leer. Sí se completan
  las líneas que todavía no lo tienen: las agregadas a una cotización ya enviada se congelan en el
  siguiente envío (o al convertir), y las que se suman a un pedido pendiente
  (`POST /orders/{id}/items`, `PUT /orders/{id}`) se congelan al sumarlas.
- Un producto que el catálogo ya no devuelve no frena el envío: esa línea queda sin foto y se sigue
  leyendo en vivo, como antes.
- Las líneas enviadas antes de este cambio no tienen foto y se siguen leyendo en vivo; no hay
  backfill, porque Quotations no lee las tablas de Catalog. Se congelan solas si la cotización se
  reenvía o se convierte.
- Una cotización anulada desde borrador no congela nada (ya no se usa ni en pedidos ni en el
  Excel). Ninguna transición devuelve una cotización a borrador.

La regla vive en un solo lugar (`QuotationItemProductLabel`) y la usan la respuesta de cotización y
de pedido, el PDF (que sale de esa misma respuesta) y `Cod. Producto` del Excel de pedidos. La forma
de la respuesta no cambió: `items[].productCode` y `items[].productName` siguen ahí; lo que cambia
es de dónde salen. Portada y escalas de la línea siguen siendo las de hoy.

### Columnas del Excel de pedidos por tenant (homologación)

Cada ERP importa por encabezado con su propia plantilla, así que el tenant puede renombrar,
reordenar y ocultar las 47 columnas del Excel de pedidos y agregar hasta 40 columnas fijas
(`Tipo Doc` = `FV`, `Bodega` = `01`), desde su configuración. Una fija cuyo valor es un número
canónico en cultura invariante (`0.19`, `9999`, `-1`, `901851609`) sale como **número**, que un
Excel en `es-CO` muestra `0,19` y el ERP lee como cifra; el resto (`02`, `PM`, `1,5`, `+1`, vacío)
sale como el texto que se escribió (2026-09-26). Una misma columna del catálogo
puede ir más de una vez con encabezados distintos: el ERP puede leer el mismo dato bajo varios
nombres (la fecha del pedido en `FECHA`, `Bloq/act` y `Vencimiento`).

Las dos últimas del catálogo (2026-09-25) son `Fecha Pedido` (`order_date`: el día en que nació
el pedido, en la zona del tenant, como texto `yyyy-MM-dd`) y `Cliente` (`customer_name`: a nombre
de quién sale la factura — `Consumidor final`, el nombre de la parte de facturación propia, la
razón social si la cotización factura a ella, o el nombre de la ficha del cliente).

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
al agregarse (`QuotationItem.TaxPercentage`), no de la tarifa de hoy. `IVA` (`tax`) sigue siendo el
monto.

Después (2026-09-26) va `NIT Empresa` (`company_tax_id`): el NIT de la empresa por la que se
factura la cotización, la misma que nombra `EMPRESA`, tal como está hoy en Companies. Con la misma
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
pedido es de recogida (`Quotation.IsStorePickup`) y `Coordinadora` en cualquier otro caso, como
texto. Los dos textos son contrato del ERP del tenant. La recogida no cambia ninguna otra columna:
`Direccion`, `Ciudad` y `Telefono` siguen cayendo a los datos del cliente sin parte de envío propia.
También **nace oculta**, por la misma razón que `coordinadora_city`.

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
`coordinadora_city` (2026-10-02), las `payment_method_N` (2026-10-03) y `carrier` (2026-10-05).

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
| `GET /products`                    | `pos.sale.create`      | Búsqueda paginada de productos para vender                                       |
| `GET /products/by-code`            | `pos.sale.create`      | Busca un producto por código exacto (lector de barras)                           |
| `POST /sales/preview`              | `pos.sale.create`      | Calcula totales de un carrito sin guardar nada                                   |
| `POST /sales`                      | `pos.sale.create`      | Crea la venta; idempotente por el `id` del cliente (201 la primera vez, 200 al repetir) |
| `GET /sales/{saleId}`              | `pos.sale.read`        | Detalle de una venta                                                             |
| `GET /sales`                       | `pos.sale.read`        | Lista paginada de ventas, con filtros                                            |
| `POST /sales/{saleId}/void`        | `pos.sale.void`        | Anula una venta, con motivo; sólo si su caja sigue abierta                       |

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
adjunto, a través de una plantilla aprobada por Meta. Cuál es la plantilla vigente lo dice
`Quotations:WhatsApp:TemplateId` en [`appsettings.json`](src/Api/appsettings.json) — acá no se
repite, para que no haya dos versiones de la verdad. Sus variables son las que
`ZenviaWhatsAppSender` completa en runtime:

| Variable | De dónde sale |
| --- | --- |
| `documentUrl` | URL pública de la copia que publica `IQuotationPdfStorage.PublishAsync` (`SendQuotation.cs`); Meta no puede bajar una prefirmada de R2 |
| `fullname` | `Customer.Name` |
| `order_number` | `Quotation.QuotationNumber` |
| `total` | `Quotation.Total`, formateado `C0` en `es-CO` |
| `valid_until` | `Quotation.ValidUntil`, formateado `d 'de' MMMM 'de' yyyy` en `es-CO` |

**Cambiar el texto de la plantilla no se hace acá: se crea una plantilla nueva en Zenvia y se
apunta `Quotations:WhatsApp:TemplateId` al `id` que devuelva.** Meta no permite editar una
plantilla aprobada. Si además cambian las variables, hay que tocar
`WhatsAppQuotationMessage` y `ZenviaWhatsAppSender`.

#### Crear una plantilla nueva

```bash
curl --location 'https://api.zenvia.com/v2/templates' --header 'X-API-TOKEN: <zenvia-api-token>' --header 'Content-Type: application/json' --data-raw '{
  "channel": "WHATSAPP",
  "name": "cotizacion_pdf_cliente",
  "locale": "es",
  "senderId": "<el mismo valor de Quotations:WhatsApp:FromNumber>",
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
- **`senderId` es el número emisor**, el mismo valor que `Quotations:WhatsApp:FromNumber`. El
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

Las tres claves se inyectan por entorno y se resuelven desde el variable group
`Backend-<env>` de Azure DevOps:

| Clave | Manifiesto | Token del pipeline |
| --- | --- | --- |
| `Quotations__WhatsApp__TemplateId` | [`k8s/prod-configMap.yaml`](k8s/prod-configMap.yaml) | `QUOTATIONS_WHATSAPP_TEMPLATE_ID` |
| `Quotations__WhatsApp__ApiToken` | [`k8s/prod-secret.yaml`](k8s/prod-secret.yaml) | `QUOTATIONS_WHATSAPP_API_TOKEN` |
| `Quotations__WhatsApp__FromNumber` | [`k8s/prod-secret.yaml`](k8s/prod-secret.yaml) | `QUOTATIONS_WHATSAPP_FROM_NUMBER` |

El `TemplateId` va al ConfigMap aunque repita el valor por defecto de la imagen: cambiar el
texto del mensaje no toca código, pero exige una plantilla nueva —Meta no permite editar una
aprobada— y sin esta clave ese cambio obligaría a reconstruir y desplegar la imagen.

**Si el token o el número faltan, no hay error.** `AddWhatsAppSender` registra
`LogWhatsAppSender`, el endpoint responde 200, la cotización queda `Sent` y el cliente no recibe
nada. Es el mismo criterio que `Notifications:EmailProvider` con Infobip, y el precio de que las
pruebas de integración no necesiten credenciales.

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

### WhatsApp por tenant

Cada tenant elige en Configuración por dónde salen sus cotizaciones (spec 2026-10-07):

| Modo | Qué pasa al enviar | `whatsAppOutcome` |
| --- | --- | --- |
| sin fila / `Shared` | la cuenta de QEP (`Quotations:WhatsApp:*`, o `LogWhatsAppSender` fuera de producción) | `Accepted` |
| `Own` | la cuenta de Zenvia del tenant: su token, su número, su plantilla | `Accepted` |
| `Disabled` | PDF y snapshots sí; ni copia pública ni mensaje. La cotización queda en `Sent` y la respuesta lo dice | `Disabled` |

La API key de la cuenta propia se guarda cifrada con AES-256-GCM en
`quotations.tenant_whatsapp_settings` (`nonce || ciphertext || tag`, AAD atado al tenant). La llave
vive **fuera** de la base, en `Quotations:SecretProtection`:

| Clave | Dónde vive en prod | Contenido |
| --- | --- | --- |
| `Quotations:SecretProtection:ActiveKeyId` | ConfigMap | id de llave (`^[a-z0-9]{1,32}$`); no es secreto |
| `Quotations:SecretProtection:Keys:<id>` | Secret, desde la variable secreta del grupo `Backend-prod` | 32 bytes en base64 |

En `Production` todo es obligatorio y se valida al arrancar; fuera de producción la sección puede
faltar (el `GET` funciona y un `PUT` en `Own` con key responde 500 nombrando la clave que falta).

#### Generar una llave en local

`dotnet user-secrets set` imprime la clave **y su valor**, y `list` imprime todos los valores: el
`set` va con `| Out-Null` y la verificación cuenta.

```powershell
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Quotations:SecretProtection:Keys:k1" $key --project src/Api | Out-Null
Remove-Variable key, bytes
dotnet user-secrets set "Quotations:SecretProtection:ActiveKeyId" "k1" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "SecretProtection:Keys:k1" | Measure-Object
```

El último comando tiene que dar `Count 1`.

#### Custodia de la llave de producción

- Para generar la de producción sin imprimirla: el mismo bloque de arriba, pero terminando en
  `Set-Clipboard $key` en vez de `user-secrets set`; se pega en la variable secreta y en la bóveda,
  y después se vacía el portapapeles (`Set-Clipboard -Value $null`).
- Fuente de verdad: la variable **secreta** `QUOTATIONS_SECRET_PROTECTION_KEY_K1` del grupo
  `Backend-prod` (Azure DevOps). Respaldo: una copia en la bóveda del owner. Perder las dos es
  perder todas las API keys guardadas: cada tenant en `Own` tendría que volver a pegar la suya.
- No se edita con `kubectl`: el pipeline vuelve a aplicar `k8s/prod-secret.yaml` en cada deploy.
- Para ver que existe, sin su valor: `kubectl --context contabo-prod -n <ns> describe secret <nombre>`.
  Nunca `get -o yaml`, `-o json` ni `custom-columns` sobre `.data`.

#### Rotación (dos despliegues)

Nunca se retira una llave a la que todavía apunta alguna fila.

1. **Declarar**: variable `QUOTATIONS_SECRET_PROTECTION_KEY_K2` en el grupo y línea
   `Quotations__SecretProtection__Keys__k2` en `prod-secret.yaml`, con `ActiveKeyId` todavía en `k1`.
   Desplegar.
2. **Activar**: `ActiveKeyId = k2` en el ConfigMap. Desplegar. `WhatsAppTokenRekeyWorker` re-cifra
   al arrancar cada fila en otra llave, y cualquier `PUT` que encuentre una rezagada la drena.
3. **Contar** (sólo ids y conteos):

   ```sql
   SELECT api_token_key_id, count(*) FROM quotations.tenant_whatsapp_settings GROUP BY 1;
   ```

   Si quedan filas en `k1`, el log del worker dice cuáles se saltó y por qué (tenant id y key id) y
   su línea final da `rekey finished: n re-encrypted, m skipped`. Con `m = 0` y filas en `k1`, es la
   ventana del rolling update: un reinicio lo resuelve.

   ```powershell
   kubectl --context contabo-prod -n <namespace> rollout restart deployment/<deployment>
   kubectl --context contabo-prod -n <namespace> rollout status deployment/<deployment>
   ```

4. **Retirar** `k1` (variable y línea del Secret) sólo cuando el conteo da 0 en `k1`. Si la llave se
   filtró: las mismas fases y, además, pedirles a los tenants en `Own` que roten su token en Zenvia.

#### Orden de despliegue

1. Variable secreta en `Backend-prod` (y su copia en la bóveda). Sin ella, el pod nuevo entra en
   crash-loop por `SecretProtectionOptionsValidator`.
2. Backend: código, migración `AddTenantWhatsAppSettings`, la línea del Secret y la del ConfigMap,
   en el mismo merge.
3. Frontend, después. Si sale antes, `GET .../whatsapp-channel` da 404 y el envío sigue como hoy.

**Rollback del backend:** la imagen vieja ignora la tabla, así que **todos** los tenants vuelven a
la cuenta de QEP, incluidos los que eligieron `Disabled`. Avisarles antes, o revertir sólo con la
tabla sin filas `Disabled`/`Own`.

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
