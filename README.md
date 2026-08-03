# QEP Backend

Backend de **QCode Enterprise Platform (QEP)** implementado como monolito
modular sobre .NET 10.

El alcance ejecutable actual incluye:

- el corte vertical de configuración de tenants, con lectura y actualización;
- invitación de memberships con aprovisionamiento de usuarios en Identity;
- aislamiento por tenant y autorización basada en permisos;
- control de concurrencia optimista mediante `ETag` e `If-Match`;
- persistencia PostgreSQL con migraciones de Entity Framework Core;
- auditoría transaccional y publicación mediante Outbox/Inbox idempotente;
- base del módulo Identity para usuarios y vínculos con proveedores externos;
- trazas y métricas con OpenTelemetry;
- pruebas unitarias, de arquitectura e integración.

> [!WARNING]
> La autenticación por encabezados es exclusivamente para `Development`. No se
> debe desplegar en un ambiente compartido o productivo. Consulte
> [`docs/decisions/0001-development-auth-stub.md`](docs/decisions/0001-development-auth-stub.md).

## Requisitos

- .NET SDK `10.0.301` o un parche posterior compatible con `global.json`;
- Docker con Docker Compose.

Las pruebas de integración también requieren Docker, ya que crean una instancia
aislada de PostgreSQL mediante Testcontainers.

## Ejecución local

Desde este directorio:

```powershell
docker compose up -d
dotnet restore --locked-mode
dotnet run --project src/Api --launch-profile http
```

Al iniciar, la API aplica automáticamente las migraciones de los módulos
Tenancy e Identity. En `Development`, si aún no existen tenants, también crea el
tenant de demostración.

| Recurso | Dirección |
|---|---|
| API | `http://localhost:5100` |
| Health check | `http://localhost:5100/health/live` |
| Documento OpenAPI (solo Development) | `http://localhost:5100/openapi/v1.json` |
| PostgreSQL | `localhost:5432` |
| OTLP gRPC / HTTP | `localhost:4317` / `localhost:4318` |
| Métricas del collector | `http://localhost:8889/metrics` |

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

| Clave | Valor local | Uso |
|---|---|---|
| `ConnectionStrings:QepDatabase` | PostgreSQL en `localhost:5432/qep` | Conexión compartida por los módulos |
| `OpenTelemetry:Endpoint` | `http://localhost:4317` | Exportación OTLP de trazas y métricas |
| `OTEL_SERVICE_NAME` | sin definir (cae a `qep-api`) | `service.name` del recurso; en k8s lo fija el Deployment |
| `Authentication:Authority` | vacío | Emisor OIDC requerido fuera de Development |
| `Authentication:Audience` | `qep-api` | Audiencia JWT requerida fuera de Development |

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
dotnet user-secrets set "Notifications:BaseUrl" "<https://xxxxx.api.infobip.com>" --project src/Api
dotnet user-secrets set "Notifications:ApiKey"  "<infobip-api-key>"                --project src/Api
```

Cloudflare R2 (object storage obligatorio, ADR 0020):

```powershell
dotnet user-secrets set "Storage:R2:AccountId"       "<account-id>"      --project src/Api
dotnet user-secrets set "Storage:R2:AccessKeyId"     "<access-key-id>"   --project src/Api
dotnet user-secrets set "Storage:R2:SecretAccessKey" "<secret-access-key>" --project src/Api
dotnet user-secrets set "Storage:R2:Bucket"          "<bucket>"          --project src/Api
# Endpoint opcional; si se omite se deriva como https://<AccountId>.r2.cloudflarestorage.com
dotnet user-secrets set "Storage:R2:Endpoint"        "https://<account-id>.r2.cloudflarestorage.com" --project src/Api
```

No existe fallback local. La validación de arranque exige `AccessKeyId`,
`SecretAccessKey`, `Bucket` y `Endpoint` o `AccountId` en todos los ambientes.
Las pruebas automatizadas sustituyen `IObjectStorage` por un test double en
memoria; ese adapter no forma parte de la aplicación.

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
X-Permissions: tenancy.settings.read,tenancy.settings.update,tenancy.membership.invite,tenancy.membership.read,tenancy.membership.manage
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

| `Authentication:UseDevelopmentStub` | Esquema activo | Cómo autentica |
|---|---|---|
| `true` | Stub por encabezados (`DevelopmentAuthenticationHandler`) | Lee `X-Subject-Id`, `X-Tenant-Id`, `X-Permissions` opcional. Sin proveedor real. |
| `false` (**por defecto**) | JWT Bearer (Google, ADR 0014/0015) | Valida el token del proveedor; exige `Authentication:Google:ClientId` (o `Authentication:Audience`) y `Authentication:Authority`. |

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

## API implementada

### Health check

```http
GET /health/live
```

Es anónimo y responde `200 OK` con `{"status":"healthy"}`.

### Configuración del tenant

| Método | Ruta | Permiso |
|---|---|---|
| `GET` | `/api/v1/tenants/{tenantId}/settings` | `tenancy.settings.read` |
| `PATCH` | `/api/v1/tenants/{tenantId}/settings` | `tenancy.settings.update` |

El `GET` devuelve la configuración y un encabezado `ETag` con su versión. El
`PATCH` exige enviar esa versión en `If-Match`; una versión desactualizada
produce `412 Precondition Failed`.

Ejemplo completo en PowerShell:

```powershell
$tenantId = "01900000-0000-7000-8000-000000000001"
$headers = @{
  "X-Subject-Id" = "01900000-0000-7000-8000-000000000002"
  "X-Tenant-Id"  = $tenantId
}

$settings = Invoke-WebRequest `
  -Uri "http://localhost:5100/api/v1/tenants/$tenantId/settings" `
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
  -Uri "http://localhost:5100/api/v1/tenants/$tenantId/settings" `
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
  "version": 2
}
```

Formatos de fecha admitidos: `yyyy-MM-dd`, `dd/MM/yyyy` y `MM/dd/yyyy`.

### Invitación de memberships

| Método | Ruta | Permiso |
|---|---|---|
| `POST` | `/api/v1/tenants/{tenantId}/memberships` | `tenancy.membership.invite` |

La operación obtiene o crea en Identity un usuario invitado, y después crea su
Membership en Tenancy con estado `Invited`, auditoría y el evento Outbox
`tenancy.membership-invited.v1`. La invitación vence después de 72 horas.

Ejemplo en PowerShell, reutilizando `$tenantId` y `$headers` del ejemplo
anterior:

```powershell
$body = @{
  email = "new.member@example.com"
  roles = @("tenancy.member")
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5100/api/v1/tenants/$tenantId/memberships" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```

Respuesta `201 Created`:

```json
{
  "id": "01900000-0000-7000-8000-000000000010",
  "userId": "01900000-0000-7000-8000-000000000011",
  "tenantId": "01900000-0000-7000-8000-000000000001",
  "state": "Invited",
  "roles": ["tenancy.member"],
  "invitedAt": "2026-07-05T21:00:00+00:00",
  "acceptedAt": null,
  "expiresAt": "2026-07-08T21:00:00+00:00"
}
```

Repetir secuencialmente la invitación para el mismo email y tenant devuelve la
Membership existente sin crear duplicados.

Los errores usan `ProblemDetails` e incluyen `code` y `traceId`; los errores de
validación también incluyen un mapa `errors`.

| Estado | Significado |
|---|---|
| `401` | No se pudo autenticar la solicitud |
| `403` | Falta el permiso o el tenant de la ruta no coincide con el contexto |
| `404` | No existe el tenant solicitado |
| `412` | El `ETag` enviado ya no es la versión vigente |
| `422` | Falló una validación o regla de dominio |
| `428` | Falta un encabezado `If-Match` válido |

## Arquitectura y persistencia

```txt
src/
  Api/                         # Host HTTP y manejo de errores
  Bootstrapper/                # Composición, autenticación y autorización
  BuildingBlocks/                  # Domain, Application, Infrastructure y Observability
  Modules/
    Tenancy/                       # Api, Application, Domain e Infrastructure
    Identity/                      # Application, Domain e Infrastructure
tests/
  ArchitectureTests/
  Modules/Identity/
    Modules.Identity.UnitTests/
  Modules/Tenancy/
    Modules.Tenancy.UnitTests/
    Modules.Tenancy.IntegrationTests/
```

PostgreSQL separa los datos por esquemas:

- `tenancy`: tenants, memberships y proyección del historial de cambios;
- `identity`: usuarios y vínculos con proveedores;
- `audit`: entradas de auditoría;
- `platform`: mensajes Outbox e Inbox.

Tenancy e Identity usan `DbContext` independientes sobre `QepDatabase`.
Tenancy registra su historial de migraciones en
`platform.__ef_migrations_history` e Identity en
`identity.__ef_migrations_history`, evitando colisiones entre ambos módulos.

Una actualización efectiva de la configuración incrementa su versión y guarda,
en la misma unidad de trabajo, la auditoría y el evento
`tenancy.tenant-settings-updated.v1`. Un worker interno procesa el Outbox cada
dos segundos. El Inbox evita repetir los efectos si el mismo evento se vuelve a
entregar. Actualmente este despacho es interno al monolito y no utiliza un
broker externo.

El módulo Identity ya contiene dominio, persistencia y el servicio para obtener
o aprovisionar usuarios invitados, pero todavía no expone endpoints HTTP.
Tenancy consume ese servicio mediante un contrato de Application al procesar
una invitación.

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
UseExceptionHandler → UseAuthentication → UseAuthorization → endpoints
```

`ApiExceptionHandler` (`IExceptionHandler`) centraliza el mapeo de excepciones
a `ProblemDetails` (RFC 7807):

| Excepción | Código HTTP |
|---|---|
| `ResourceNotFoundException` | 404 |
| `RequestForbiddenException` | 403 |
| `RequestConcurrencyException` | 412 |
| `PreconditionRequiredException` | 428 |
| `ValidationException` (FluentValidation) | 422, con mapa `errors` por campo |
| `DomainException` | 422 |
| Cualquier otra | 500 |

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

Monolito modular con Clean Architecture estricta por módulo. Cada módulo
(`Tenancy`, `Identity`, `Authorization`, `Notifications`, `Audit`, `Storage`)
se compone de cuatro assemblies: `Domain` →
`Application` → `Infrastructure` → `Api`. `tests/ArchitectureTests` contiene
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
`RoleCatalog` mapean un rol (p. ej. `tenancy.owner`, `tenancy.member`) a un
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

| Instrumentación | Aporta |
|---|---|
| `AddAspNetCoreInstrumentation` (traza + métrica) | Span raíz por request y el histograma `http.server.request.duration` (p50/p95/p99 por ruta) |
| `AddHttpClientInstrumentation` | Spans de llamadas salientes con `HttpClient` |
| `AddNpgsql` (Npgsql.OpenTelemetry) | Un span hijo por comando SQL bajo el span del request; expone N+1 como spans idénticos repetidos |
| Meter `Npgsql` | Conexiones busy/idle/waiting del pool (`db.client.connection.*`) |
| `AddRuntimeInstrumentation` | GC, heap, threadpool, contención — distingue degradación de la app vs. de la base |
| `ActivitySource`/`Meter` propios (`Qep.Platform`) | Instrumentación manual específica del dominio |

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

El análisis antimalware usa el protocolo `INSTREAM` de ClamAV. En producción
configura `Storage:ClamAv:Enabled=true`, junto con `Host`, `Port` y
`TimeoutSeconds`. Si ClamAV no responde, el archivo no se promociona. El modo
deshabilitado existe únicamente para desarrollo local y pruebas.

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
