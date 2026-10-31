# API keys por tenant y API externa — diseño

- **Fecha:** 2026-10-30
- **Módulos:** ApiAccess (nuevo), Messaging, Tenancy, Bootstrapper, `qep-frontend`
- **Estado:** aprobado por el owner el 2026-10-30, sin construir
- **Verificado contra el código:** `develop` en `a64a74d4`, los dos repos

## 1. Contexto y alcance

Un tenant tiene su propio software (un ERP, un sistema de despachos) y necesita enviar plantillas de WhatsApp por el número que
conectó a QEP. Hoy es imposible: el único acceso a la API es la cookie de sesión de una persona.

Esta entrega construye **una credencial general, no una de Messaging**: claves de API por tenant, cada una con los scopes que el
admin le permita, y una superficie externa versionada a la que cada scope abre rutas concretas. El primer scope es
`whatsapp.templates.send`. Crear cotizaciones por API (`quotations.create`) es el siguiente proyecto, con su propio diseño: exige
decidir qué asesor queda asignado (`CreateQuotation.cs:72`), y eso es negocio, no técnica.

### Metas

1. El admin del tenant crea, ve y revoca claves en una sección **Desarrolladores** de QEP.
2. Cada clave tiene scopes elegidos de un catálogo público. Agregar un scope de otro módulo no toca el módulo ApiAccess.
3. Con una clave, un sistema externo lista los números permitidos, lista sus plantillas enviables y envía una plantilla a un número.
4. Lo enviado queda en el hilo de la bandeja con el nombre de la clave como autor; la respuesta del cliente sigue el camino normal.

### No-metas

- Webhooks de estado (entregado, leído, fallido) hacia el sistema externo, y consulta de estado de un mensaje.
- Envío masivo por la API, adjuntos, plantillas con encabezado multimedia (hoy no son enviables en ningún camino) y texto libre.
- Portal de documentación público. La documentación vive en la sección Desarrolladores y en el README.
- `quotations.create` y cualquier otro scope. El mockup lo muestra como «Próximamente» solo para ilustrar el catálogo.

### Anti-metas: es un fracaso aunque funcione si…

- Una clave entra a cualquier endpoint interno. El SPA sigue siendo el único consumidor de la API BFF.
- La clave completa queda en la base, en un log, en auditoría, en `platform.request_failures` o en una traza.
- Una clave de un tenant toca datos o conexiones de otro.
- Una clave revocada o vencida sigue funcionando, aunque sea por unos segundos.
- Un reintento del sistema externo manda dos mensajes.
- La API se vuelve una vía para mandar marketing a quien no lo quiere recibir, porque se saltó lo que las campañas sí verifican.

## 2. Lo que existe (verificado)

- **Autenticación:** el esquema por defecto es la cookie (`QepServiceCollectionExtensions.cs:2121`). `RequireAuthorization` puede
  fijar un esquema (`QepAuthenticationMode.cs:48-50`, precedente `GoogleBearer`). **La rama del stub retorna antes de registrar
  esquemas extra** (`:2089-2097`).
- **Orden del pipeline** (`Program.cs:129-152`): ForwardedHeaders → ExceptionHandler → CORS → RequestFailureLogging → RateLimiter →
  CSRF (solo sin stub) → Authentication → Authorization. El rate limiter corre **antes** de autenticar.
- **CSRF:** la exención es por ruta (`RequireCsrfHeaderMiddleware.cs:58-61`). Sin exención, un `POST` externo da 403 antes de autenticar.
- **`HttpExecutionContext`:** `SubjectId` sale de `qep_sub`/`sub`, y un claim faltante da 500 (`HttpExecutionContext.cs:16-35`). Se usa
  231 veces en 146 archivos de los módulos, siempre suponiendo una persona. `ExternalClaimsTransformation` no toca un principal que
  ya trae `qep_sub` y `tenant_id`.
- **401/403 del middleware** van sin cuerpo: no pasan por `ApiExceptionHandler` (`Program.cs:138-140`).
- **Envío por teléfono:** `BillingTemplateOutbound.cs:56-112`, atado a sus plantillas fijas. Lo genérico es público o `internal` de
  Messaging.Application: `TemplateOutbound.ResolveAsync`, `TemplateCatalog.RequireSendable/BuildSend`, `OpenByPhoneAsync`,
  `OutboundSettlement`. `OutboundGuards` no sirve sin usuario (`OutboundGuards.cs:10,45,70`).
- **Opt-out y bloqueo:** el chequeo de marketing vive solo en campañas (`CampaignDispatch.cs:293,492`). `BillingTemplateOutbound` no
  mira `BlockedAt`; `BotOutbound.cs:71` sí.
- **Idempotencia:** el índice es por `(conversation_id, client_id)` (`MessagingDbContext.cs:363-364`). El mismo `clientId` con otro
  número cae en otra conversación y sale un **segundo mensaje**.
- **Autor:** `sent_by_kind IN (1,2)` por CHECK, y `AuthorKindOf` exige miembro o chatbot (`OutboundMessages.cs:70-79`). El spec
  borrador de agentes IA reserva `3 = Agent` (`2026-10-29-agentes-ia-design.md:60`).
- **Reporte:** `MessagingReportSource` `fo` no filtra autor (`:84-88`): un mensaje de API contaría como primera respuesta.
- **Capacidad:** `TenantModuleGuard` **no bloquea** si no encuentra el tenant (`TenantModuleGuard.cs:22`). La variante que falla
  cerrada es la de `ServiceContractsReminderProcessor.cs:69-70`.
- **Roles de sistema:** admin, advisor, billing, cashier, en código. No existe `owner`.

## 3. Decisiones

**Del owner:**

- **D1.** La clave la crea y revoca el admin del tenant desde una sección Desarrolladores.
- **D2.** La clave es general, con scopes. No queda limitada a Messaging.
- **D3.** Primera entrega: la base más `whatsapp.templates.send`. `quotations.create` es un proyecto aparte.
- **D13.** Una conversación nueva creada por la API nace `Open`, visible en la bandeja. Consecuencia aceptada: con volumen alto, esas
  conversaciones aparecen abiertas y sin asignar, entran al triage del embudo si tienen cliente y cuentan como sesión desde su creación,
  igual que una que abre un asesor con plantilla (S3). La alternativa `Resolved` se descartó.

**Propuestas por el asistente, aprobadas con el documento:**

- **D4. Scopes públicos propios, no los 69 permisos internos.** Un catálogo corto, y cada scope abre rutas externas específicas. Cada
  módulo declara sus scopes con `ApiScopeDefinition` en DI, como `PermissionDefinition`. ApiAccess no conoce ninguno por nombre.
- **D5. Superficie propia: `/api/v1/external/...`.** La API interna es BFF y cambia con cada pantalla; un ERP necesita un contrato estable.
  Por dentro se reutilizan las piezas de dominio, no los handlers que suponen usuario. **Costo aceptado:** cada capacidad exige endpoint
  y contrato.
- **D6. El tenant sale de la clave, nunca de la ruta.** Excepción deliberada a `{tenantId:guid}`: una clave pertenece a un tenant, así que
  un id en la ruta solo agrega una forma de equivocarse. Sigue la doble capa: el endpoint exige el scope y el handler lo revalida.
- **D7. Capacidad `developer`,** sin dependencias (cada scope pide su propio módulo), fuera de `DefaultForNewTenants`, prendida por el
  operador. Motivo: cada plantilla le cuesta plata al tenant en Meta y es superficie de abuso.
- **D8. Autor nuevo `ApiKey = 4`.** Se salta el 3 para no pisar la reserva de agentes IA.
- **D9. La clave no se edita.** Para cambiar scopes o números se crea otra y se revoca la anterior. Se revoca, no se borra: los mensajes
  y la auditoría la siguen nombrando.
- **D10. Respuesta síncrona.** Meta confirma en el mismo request, como en el envío del asesor.
- **D11. Un contacto bloqueado no recibe nada, y `MARKETING` hereda las reglas de campañas.** Detalle en §6.3.1.
- **D12. Solo máquina a máquina.** `X-Api-Key` no entra en el CORS del SPA (`CorsServiceCollectionExtensions.cs:41`) a propósito: un
  navegador expondría la clave.

## 4. Modelo

### 4.1 `api_access.api_keys`

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | `uuid` | GUID v7 |
| `tenant_id` | `uuid` | |
| `name` | `varchar(80)` | Único entre las activas del tenant (índice parcial). Es lo que ve el asesor |
| `key_prefix` | `varchar(16)` | Parte pública de la clave. Índice único |
| `secret_hash` | `bytea` | SHA-256 del secreto |
| `scopes` | `text[]` | Al menos uno |
| `connection_ids` | `uuid[]` nullable | `NULL` = todos los números |
| `expires_at` | `timestamptz` nullable | |
| `last_used_at` | `timestamptz` nullable | Se actualiza como máximo una vez por minuto |
| `created_at`, `created_by_member_id` | | |
| `revoked_at`, `revoked_by_member_id` | nullable | |
| `version` | `int` | Concurrencia optimista |

**Formato de la clave:** `qep_<prefijo>_<secreto>`. El prefijo son 8 caracteres base62 y el secreto 32 bytes aleatorios de un generador
criptográfico en base62. El prefijo fijo `qep_` permite que los escáneres de secretos la detecten.

**SHA-256 y no un hash lento:** el secreto tiene 256 bits de entropía, así que no hay diccionario que atacar, y bcrypt costaría en cada
request. La comparación es en tiempo constante (`CryptographicOperations.FixedTimeEquals`).

**Máximo 25 claves activas por tenant** (`api_access.key_limit_reached`).

### 4.2 Scopes

```csharp
public sealed record ApiScopeDefinition(
    string Key, string Name, string Description, string Category,
    string RequiredModule, bool RestrictableByConnection);
```

Vive en BuildingBlocks.Application, con la constante en el módulo dueño. Messaging registra `whatsapp.templates.send` con
`RequiredModule = messaging` y `RestrictableByConnection = true`. El catálogo que ve el admin solo trae los scopes de módulos activos del
tenant. `connection_ids` vale solo si algún scope de la clave lo admite; si no, 422.

Si un módulo se apaga, la clave conserva el scope pero sus rutas responden 403 `tenancy.module_not_enabled`. Si se prende de nuevo, vuelve
a funcionar: el admin no tiene que recrear nada.

### 4.3 Identidad en un request

`ApiKeyAuthenticationHandler`, en Bootstrapper, se registra **en las dos ramas** de `AddAuthentication`, con stub y sin él. Hay que
moverlo antes del `return` de la rama del stub, o la API externa no existe en local.

- Toma `X-Api-Key`, busca por prefijo y compara el hash. Lee en base en **cada** request, sin caché: una caché es una ventana en la
  que una clave revocada sigue viva.
- Rechaza con la misma respuesta una clave ausente, malformada, desconocida, revocada o vencida, o de tenant no activo: 401
  `external.auth.invalid_key`. El motivo real va al log, con el prefijo y nunca el secreto.
- La capacidad `developer` apagada da 403 `tenancy.module_not_enabled`, comprobada fallando cerrado (§2).
- El principal lleva `qep_sub` = id de la clave (para que `ExternalClaimsTransformation` no intente resolver una persona), `tenant_id`,
  y claims propios `qep_api_key_id`, `qep_api_key_name`, `qep_api_scope` y `qep_api_connection`. **Nunca claims `permission`:** así
  ninguna política interna pasa con una clave, aunque alguien cambie un esquema por error.

`HandleChallengeAsync` y `HandleForbiddenAsync` escriben ProblemDetails con `code`: el comportamiento por defecto es cuerpo vacío, y un
integrador externo no tiene otra forma de saber qué pasó.

Los handlers externos leen un puerto nuevo, no `IExecutionContext`:

```csharp
public interface IApiCaller
{
    Guid ApiKeyId { get; }
    string ApiKeyName { get; }
    Guid TenantId { get; }
    bool HasScope(string scope);
    bool CanUseConnection(Guid connectionId);
}
```

### 4.4 Autor del mensaje

Migración de Messaging:

- `messages.sent_by_api_key_id uuid` y `messages.sent_by_api_key_name varchar(80)`. El nombre es foto: Messaging no ve ApiAccess y la
  clave no se renombra (D9), así que evita un puerto.
- CHECK `sent_by_kind IN (1,2,4)`. `SentByOneCheck` y un CHECK nuevo: kind 4 exige clave y prohíbe miembro y chatbot; kinds 1 y 2
  prohíben clave. **`<> 2` hoy dejaría pasar un 4 con miembro.**
- `OutboundDraft` lleva `SentByApiKey`; `AuthorKindOf` exige exactamente uno de tres. Pasa también por `CommitSentAsync`.
- `MessageRow`, `ExistingOutbound` y sus `SELECT` traen las columnas. `MessageDto` agrega `sentByApiKey: { id, name }`.
- `MessagingReportSource.fo` excluye kind 4: una plantilla automática no es una primera respuesta de un asesor.

**Índice nuevo** `IX_messages_tenant_api_client`, único en `(tenant_id, client_id) WHERE sent_by_kind = 4`, traducido por nombre (§6.3).

## 5. Endpoints

### 5.1 Administración (sesión, permiso `developer.api_key.manage`)

Permiso nuevo en `ApiAccessPermissions`, con su política, en el rol `admin`, `RequiredModules: [developer]`. La constante mueve la cuenta
fija de `PermissionDiscoveryFindsTheSixtyNineConstants`.

| Método y ruta | Devuelve |
| --- | --- |
| `GET /api/v1/tenants/{tenantId}/api-keys` | Claves, con prefijo, scopes, números, último uso, vencimiento y estado. Nunca el hash |
| `GET /api/v1/tenants/{tenantId}/api-keys/scopes` | Catálogo disponible para el tenant, agrupado por categoría |
| `POST /api/v1/tenants/{tenantId}/api-keys` | 201 con la clave y `secret`: **la única respuesta que lo trae** |
| `POST /api/v1/tenants/{tenantId}/api-keys/{id}/revoke` | La clave revocada. Revocar dos veces no falla |

`status` viaja calculado (`Active`, `ExpiringSoon` a 30 días o menos, `Expired`, `Revoked`) para que la pantalla no repita la regla.

Validador de FluentValidation en la creación: nombre obligatorio, scopes conocidos y disponibles, números del tenant, vencimiento
futuro. Conexión ajena: 403, nunca 404.

### 5.2 Externos (`X-Api-Key`, scope `whatsapp.templates.send`)

Requieren `RequireAuthorization` con el esquema fijo `ApiKey`. La cookie no entra aquí, y `X-Api-Key` no entra en las rutas internas.

| Método y ruta | Devuelve |
| --- | --- |
| `GET /api/v1/external/whatsapp/connections` | Los números que la clave puede usar: `id`, nombre, teléfono |
| `GET /api/v1/external/whatsapp/connections/{connectionId}/templates` | Las plantillas enviables: nombre, idioma, categoría, parámetros |
| `POST /api/v1/external/whatsapp/messages` | 201 al enviar, 200 si es un reintento ya enviado |

El primer endpoint existe porque sin él el integrador no tiene de dónde sacar `connectionId`. El mockup no lo mostraba.

```json
{
  "clientId": "0192f3a4-7b1c-7e2a-9d4f-3a5b6c7d8e9f",
  "connectionId": "0192f3a4-7b1c-7e2a-9d4f-000000000001",
  "to": "573001234567",
  "template": { "name": "aviso_despacho", "language": "es_CO", "parameters": { "1": "Ana", "2": "4821" } }
}
```

```json
{ "messageId": "…", "conversationId": "…", "status": "Sent" }
```

- `clientId` es obligatorio, UUID y lo genera el sistema externo.
- `to` lleva indicativo de país. Se aceptan `+`, espacios y guiones; se normaliza a dígitos, de 8 a 15, y se valida con
  `libphonenumber` (el que ya usa Customers, por un puerto). No se adivina el país: `3001234567` sin indicativo se leería como +30 y
  le llegaría a otra persona, así que responde 422.

### 5.3 Errores

Todo es ProblemDetails con `code`. **Los `code` son contrato público:** cambiarlos rompe integraciones.

| Status | `code` | Cuándo |
| --- | --- | --- |
| 401 | `external.auth.invalid_key` | §4.3 |
| 403 | `external.auth.scope_missing` | La clave no tiene el scope |
| 403 | `external.connection_not_allowed` | Número fuera de la clave o de otro tenant |
| 403 | `tenancy.module_not_enabled` | `developer` o `messaging` apagados |
| 409 | `external.client_id_reused` | Mismo `clientId` con otro destino o plantilla |
| 422 | `validation.failed` | Con mapa `errors` |
| 422 | `messaging.template.not_available` | Plantilla inexistente, no aprobada o no enviable |
| 422 | `messaging.template.parameters_mismatch` | Parámetros que no coinciden |
| 422 | `external.recipient_blocked` | Contacto bloqueado |
| 422 | `external.recipient_opted_out` | Plantilla `MARKETING` a quien no la acepta |
| 422 | `external.recipient_inactive` | Plantilla `MARKETING` a un cliente inactivo |
| 422 | `external.message_rejected` | Meta rechazó con certeza |
| 429 | `external.rate_limited` | Límite de QEP o de Meta, con `Retry-After` |
| 502 | `external.send_unconfirmed` | No se sabe si salió (§6.3.2) |

Los `code` existentes de Messaging y los de conexión no disponible o rechazo de Meta se confirman contra `MessagingErrorCodes` al
escribir el plan. Lo que falte de esta tabla se agrega ahí.

## 6. Ejecución

### 6.1 CSRF, CORS y límites

- `RequireCsrfHeaderMiddleware` exime el prefijo `/api/v1/external/`, con el mismo mecanismo que `WebhookPaths`. Es seguro porque esas rutas
  no aceptan cookie (el comentario `:30-33` del middleware pide exactamente eso). Se prueba con el stub apagado: en modo stub ese
  middleware no se registra y la prueba pasaría aunque la exención estuviera rota.
- **Dos límites**, porque el rate limiter corre antes de autenticar:
  1. Política `external` por IP, 600/min: frena intentos con claves inventadas.
  2. Filtro de endpoint por `ApiKeyId` después de autenticar, 300/min, configurable en `ApiAccess:RateLimit:PerKeyPerMinute`.
  Particionar la política 1 por el header crearía una partición por cada clave falsa. Los dos viven en memoria: hay una réplica hoy, y si
  crecen las réplicas el límite efectivo se multiplica. Queda anotado.

### 6.2 `last_used_at`

`ExecuteUpdate` condicionado a que el valor tenga más de un minuto, fuera de la transacción del request. Si falla se loguea y se sigue: un
contador no puede tumbar un envío.

### 6.3 Enviar

`SendExternalTemplateHandler` en Messaging.Application, para poder usar lo `internal`. Es `BillingTemplateOutbound` generalizado, con
este orden:

1. Scope y número (`IApiCaller`) y capacidad `messaging`.
2. **Idempotencia antes de todo:** busca por `(tenant_id, client_id)` con kind 4. Si existe y no es `Failed`: mismo destino, plantilla, idioma y
   parámetros responde 200 con lo guardado; cualquier diferencia, 409. Por tenant y no por clave, para que rotar una clave en medio
   de un reintento no duplique.
3. `ResolveSenderWithReasonAsync` (Instagram cae como `channel_unsupported`) y `TemplateCatalog.RequireSendable/BuildSend`, **antes** de
   abrir la conversación: una plantilla mala no debe dejar un hilo vacío.
4. Busca al cliente por teléfono. Con una sola coincidencia, lo liga; con varias o ninguna, `customerId = null`. No crea clientes.
5. Reglas del destinatario (§6.3.1).
6. `OpenByPhoneAsync`, sin variante. Una conversación existente no cambia de estado ni de asignación. Una nueva nace `Open` y sin asignar
   (D13).
7. `ClaimAsync` con kind 4. Desde aquí todo va con `CancellationToken.None`, como en Billing: si Meta aceptó, cancelar no puede borrar la
   fila. El mismo `clientId` en carrera con otro destino choca con `IX_messages_tenant_api_client`, que se traduce por nombre a 409.
8. Envío y `OutboundSettlement` con `seen: 0`.

`BillingTemplateOutbound` no se toca. La lógica común se extrae solo si queda evidente al escribir, no por adelantado.

#### 6.3.1 Reglas del destinatario

La API la usa el sistema del tenant, que también manda avisos operativos. Por eso **solo `MARKETING` hereda las reglas de campañas**:

| Caso | Plantilla no marketing | `MARKETING` |
| --- | --- | --- |
| Conversación bloqueada | 422 `external.recipient_blocked` | Igual |
| Cliente inactivo | Sale | 422 `external.recipient_inactive` |
| Opt-out del número | Sale | 422 `external.recipient_opted_out` |
| Número sin cliente | Sale | Sale, y Meta decide |
| Varias coincidencias | Sale, `customerId = null` | Si alguna tiene opt-out, 422 |

Si Meta rechaza por opt-out (`MetaErrorCodes.MarketingOptOut`), responde `external.recipient_opted_out`, y se anota solo si hay cliente, como
`TemplateOutbound.cs:78`.

#### 6.3.2 Reintentos y envíos sin confirmar

`OutboundSettlement.cs:74-78` deja `Failed` también cuando Meta pudo haber aceptado (`MessageFailureReasons.Unconfirmed`). Reenviar ahí
mandaría dos mensajes, así que se sigue la regla de campañas (`CampaignDispatch.cs:341`):

| Resultado | Respuesta | Con el mismo `clientId` |
| --- | --- | --- |
| Meta rechazó con certeza | 422 `external.message_rejected` | Reenvía: es lo que hace `OutboundGuards.cs:53` |
| Sin confirmar (timeout, 5xx, 2xx sin id) | 502 `external.send_unconfirmed` | **No reenvía.** Repite el 502 |
| Meta limita (130429) | 429 `external.rate_limited` | Reenvía |

El integrador debe tratar `external.send_unconfirmed` como «no sé si salió»: decidir si manda otro exige un `clientId` nuevo, y esa decisión
queda explícita de su lado.

### 6.4 Auditoría

Puerto propio `IApiAccessAuditRecorder` con adaptador en Infrastructure, atado a `ApiAccessDbContext` (patrón `IIntegrationsAuditRecorder`).
Acciones: `api_access.key.created` y `api_access.key.revoked`, sin secreto.

**Un intento rechazado no va a auditoría** sino al log: escribir una fila por cada clave falsa convierte la auditoría en un blanco de
saturación. El envío queda en el propio mensaje. Por la misma razón, `RequestFailureCapture.ShouldCapture` excluye 401 y 429 bajo
`/api/v1/external/`; el resto se sigue capturando para diagnosticar integraciones.

### 6.5 Frontend (`qep-frontend`, rama propia)

- Feature `src/features/developers/`: `pages/`, `components/`, `hooks/`, `services/`, `types/`, según Screaming Architecture.
- Sección dentro de Configuración (`use-settings-sections.ts`, con `can()` y `moduleOn()`) y `ModuleGate` en la ruta. Sin el permiso no se
  piden datos.
- Clave `developer` en `tenant-modules.ts` y sus listas (`request-log.ts`, `test/operator.ts`).
- La clave recién creada vive solo en el estado del diálogo. Nunca en caché de TanStack Query ni en la URL.
- `message-bubble.tsx` firma «Enviado por API · {nombre}». Tipos y parser en `messaging.ts` y `messaging.api.ts`.

Referencia visual: [mockup navegable](../mockups/2026-10-30-api-keys.html) (lista, crear, clave creada, revocar y bandeja). Copy en español
colombiano, tuteando.

### 6.6 Módulo y despliegue

Plantilla: Suppliers, commit `d8d1f9e4`. Proyectos `Modules.ApiAccess.{Domain,Application,Infrastructure,Api}` y sus pruebas;
`ApiAccessLayerTests.cs`; `Backend.slnx`, `Api.csproj`, `Bootstrapper.csproj`, `ArchitectureTests.csproj`; migraciones en esquema
`api_access` con historial propio e inicializador en `Program.cs`; **lock files regenerados** o el `docker build` falla con `NU1004`.

Clave `developer` en `TenantModuleKey.cs` más la migración que reemplaza el CHECK de Tenancy, sin backfill.

Orden: backend, luego frontend. Con el backend nuevo y el frontend viejo, un mensaje de API sale sin firma pero no rompe nada. No hay
configuración nueva obligatoria en `Backend-prod`.

## 7. Prueba

TDD, con RED y GREEN literales. Además de los casos felices, cada anti-meta tiene su prueba:

- `X-Api-Key` válida en una ruta interna: 401. Cookie válida en `/api/v1/external/...`: 401.
- Clave del tenant A contra una conexión del tenant B: 403.
- Revocar y usar en el request siguiente: 401. Lo mismo al vencer.
- Scope ausente: 403 en el endpoint **y** en el handler llamado directo.
- `POST` externo sin `X-Qep-Client` con stub apagado: pasa. Con ruta interna: 403 (la exención no se ensanchó).
- La API funciona con el stub prendido (el esquema se registró en las dos ramas).
- Secreto ausente de logs en nivel `Trace`, de auditoría y de `platform.request_failures` (patrón
  `TheKeyNeverAppearsInLogsAuditOrHttpCalls`).
- `clientId`: repetido igual da 200 sin segundo envío a Meta; con otro número da 409; en paralelo con otro número da un solo envío; un rechazo
  cierto reintenta; **un envío sin confirmar no vuelve a llamar a Meta**.
- Cada fila de §6.3.1.
- `developer` apagado, `messaging` apagado, marketing a opt-out, contacto bloqueado, conexión de Instagram.
- CHECK de autor: un kind 4 con miembro falla en la base.
- `MessagingReportSource`: un mensaje de API no es primera respuesta; su conversación nueva sí cuenta como sesión (D13).
- `CompositionRootTests`: handlers, permiso y scopes registrados.

Contra Meta real no se puede verificar en CI: el envío se prueba una vez en local con la app de desarrollo antes de dar la entrega por buena.

## 8. A criterio de quien construye

- Nombres internos de clases y la extracción de lógica común con Billing.
- Forma exacta del log de rechazo, sin secreto.
- Consultas y sus índices, salvo los fijados aquí.
