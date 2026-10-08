# Integraciones: gestión de conexiones por tenant

**Fecha:** 2026-10-08
**Módulos:** Integrations (nuevo: Domain, Application, Infrastructure, Api), Tenancy (sólo lectura
de módulos activos), Authorization (dos permisos nuevos en `admin`), Quotations (se le quita
WhatsApp por tenant), Api/Bootstrapper (mapeo y políticas); frontend `features/integrations` (nuevo)
y `features/tenant-settings` (se le quita la sección de WhatsApp).
**Estado:** aprobado en conversación con el owner el 2026-10-08; pendiente de su lectura final.
**Depende de:** módulos por tenant (`ITenantModules`, `TenantModuleKeys`, `TenantModuleGuard`,
`tenancy.module_not_enabled`), ya en `develop`.
**Reemplaza a:** `2026-10-07-whatsapp-por-tenant-design.md` en todo lo que es cuenta, secreto,
cifrado, rotación y pantalla de Ajustes. Lo que ese spec dice del **envío** de la cotización queda
suspendido hasta el spec del consumidor (ver «Fuera de alcance»).

## Problema

WhatsApp por tenant quedó dentro de Quotations: la cuenta de Zenvia del tenant, su token cifrado,
la rotación de la llave, el cliente HTTP, los endpoints de Ajustes y la tabla
`quotations.tenant_whatsapp_settings` —unos 40 archivos—. Zenvia no es de cotizaciones: el owner
quiere campañas por WhatsApp, mensajería oficial (WhatsApp Cloud, Messenger, Instagram),
transportadoras para guías y OpenAI con la clave de cada tenant. Con el diseño de hoy, cada módulo
nuevo tendría que depender de Quotations para usar una cuenta del tenant, o repetir el cifrado y
la pantalla.

Nada de lo por tenant está en `main`. En `main` sólo existe el sender global de QEP
(`Quotations:WhatsApp:{ApiToken, FromNumber, TemplateId}`) y sus claves de k8s. Ése es el momento
de mover la frontera sin migrar datos de producción.

## Objetivo

Un módulo **Integrations** que es dueño de **las conexiones del tenant con plataformas externas** y
de nada más: qué proveedores existen, cuáles ve cada tenant, cómo se conecta uno, dónde queda la
credencial cifrada, si la conexión está activa, pausada o necesita atención, y quién hizo qué.
Qué hace cada módulo con una conexión —mandar una cotización, generar una guía, pedirle algo a
OpenAI— **no** es de este spec: cada consumidor tendrá el suyo y pedirá la conexión por un puerto.

## Decisiones del owner (2026-10-08)

| # | Decisión | Elegido |
| --- | --- | --- |
| 1 | ¿Sigue la cuenta compartida de QEP (`Shared`)? | **No.** Cada tenant conecta la suya. El owner no tiene cuenta de Zenvia; la cuenta global de hoy pertenece a un tenant. |
| 2 | ¿Cuántas conexiones del mismo proveedor por tenant? | **Varias.** Cada una con nombre. |
| 3 | ¿Quién administra? | **Permisos propios:** `integrations.connection.read` y `integrations.connection.manage`, de fábrica en `admin`. |
| 4 | ¿Módulo vendible o núcleo? | **Núcleo, con catálogo filtrado:** el tenant sólo ve los proveedores que consume alguno de sus módulos activos. |
| 5 | ¿Credencial al guardar? | **Se valida contra el proveedor; si falla no se guarda.** |
| 6 | ¿Desconectar? | **Pausar y eliminar, separados.** Eliminar pide escribir el nombre. |
| 7 | ¿Dónde en la SPA? | **`Ajustes → Integraciones`**, página propia. La sección de WhatsApp de Ajustes desaparece. |
| 8 | Enfoque de implementación | Módulo nuevo con **una fila genérica por conexión** y **catálogo en código**. |

## Criterios de éxito

1. Quotations no tiene Zenvia, cifrado, rotación ni tabla de WhatsApp. `rg -i "zenvia|SecretProtection|ProtectedSecret" src/Modules/Quotations` sólo encuentra el sender global que ya está en `main`.
2. Un administrador con `integrations.connection.manage` crea una conexión de Zenvia desde Ajustes; una clave mala no se guarda y el error llega al campo.
3. Ninguna respuesta HTTP, log ni auditoría contiene el valor de un secreto. Hay una prueba que lo verifica con un valor sentinela, como `WhatsAppSecretLeakTests`.
4. Rotar la llave es cambiar `ActiveKeyId` y dejar la vieja: el worker re-cifra todos los secretos sin pedirle nada al tenant.
5. Un proveedor nuevo es una clase en el catálogo, su probador y sus pruebas. Sin migración. La suite lo demuestra con un proveedor falso registrado sólo en pruebas.
6. Apagarle a un tenant el único módulo que consume un proveedor lo saca de su catálogo y oculta sus conexiones; reactivarlo las devuelve intactas.

## Modelo

### Catálogo (en código, `Modules.Integrations.Domain`)

```text
IntegrationProvider
  Key              "zenvia"                      ^[a-z0-9-]{2,32}$, estable, va a la base y al JSON
  DisplayName      "Zenvia (WhatsApp)"
  Category         Messaging | Shipping | Ai
  ConsumingModules [TenantModuleKeys.Quotations]  qué módulos lo usan; vacío = nunca visible
  Fields           [FieldDefinition]
  MaxConnections   20                             por tenant y proveedor

FieldDefinition
  Key       "apiToken"            ^[a-zA-Z][a-zA-Z0-9]{1,39}$, clave en el JSON y en `fields.<key>` del mapa `errors`
  Label     "API token"           texto en español, tuteo
  Kind      Text | Secret | Phone | Url
  Required  true
  MaxLength 512
  Pattern   regex opcional (Phone: E.164 sin «+», como `from_number` hoy)
```

`IntegrationProviders.All` es la lista cerrada, como `TenantModuleKeys.All`. El catálogo de la
versión 1 tiene **un** proveedor:

| Key | Categoría | Consumen | Campos |
| --- | --- | --- | --- |
| `zenvia` | Messaging | `quotations` | `apiToken` (Secret, requerido), `fromNumber` (Phone, requerido) |

**La plantilla de cotización no es un campo de la conexión.** Es del consumidor: cuando Quotations
pase a usar Integrations, guardará su `TemplateId` por tenant en su propio spec. Por eso la
conexión de Zenvia sólo tiene cuenta y número.

OpenAI, Meta y transportadoras **no** entran en el catálogo todavía: sin módulo consumidor activo
nunca serían visibles (decisión 4) y serían código muerto. Entran con el spec de su consumidor.

### Visibilidad para un tenant

Un proveedor es **visible** para un tenant si alguno de sus `ConsumingModules` está activo en
`ITenantModules.FindAsync(tenantId)`. Con el stub de desarrollo (`FindAsync` en `null`) todo es
visible, mismo criterio que `TenantModuleGuard`. Las conexiones de un proveedor no visible **no se
borran ni se pausan**: se ocultan en la lista y sus endpoints responden 403
`tenancy.module_not_enabled`, igual que el resto de la app con un módulo apagado.

### Conexión (`IntegrationConnection`, agregado)

```text
integrations.connections
  id                 uuid PK (v7)
  tenant_id          uuid        índice
  provider_key       varchar(32) CHECK contra las claves del catálogo (otra migración por proveedor nuevo, a propósito, como `tenant_modules`)
  name               varchar(80) único por (tenant_id, provider_key, lower(name))
  status             varchar(16) CHECK IN ('Active','Paused','NeedsAttention')
  fields             jsonb       sólo los campos Kind <> Secret, validados contra el catálogo
  last_verified_at   timestamptz null   última prueba exitosa contra el proveedor
  last_failure_at    timestamptz null
  last_failure_code  varchar(64) null   p. ej. credentials_rejected
  created_at         timestamptz
  created_by         uuid        MemberId de quien la creó
  updated_at         timestamptz
  version            bigint      concurrencia optimista, If-Match

integrations.connection_secrets
  connection_id      uuid FK → connections ON DELETE CASCADE
  field_key          varchar(40)
  key_id             varchar(32)   llave con la que está cifrado
  ciphertext         bytea
  updated_at         timestamptz
  PK (connection_id, field_key)
```

Los secretos van en tabla hija y no dentro del `jsonb` para que el worker de rotación los
recorra por `key_id` con un índice y para que un `SELECT fields` nunca pueda traerlos por error.

**Estados y transiciones:**

| De → a | Quién | Cómo |
| --- | --- | --- |
| — → `Active` | admin | `POST /connections` con prueba exitosa |
| `Active` → `Paused` | admin | `POST .../pause` |
| `Paused` → `Active` | admin | `POST .../resume`. **Vuelve a probar**: si la credencial ya no sirve, queda en `NeedsAttention` y el resume responde 422 `integrations.connection.credentials_rejected` |
| `Active` → `NeedsAttention` | un consumidor | `IConnectionHealthReporter.ReportCredentialsRejectedAsync` tras un 401/403 **definitivo** del proveedor. Sin umbral: un rechazo de credenciales no es intermitente |
| `NeedsAttention` → `Active` | admin | `PUT` que cambie al menos un campo y pase la prueba, o `POST .../test` que pase |
| cualquiera → eliminada | admin | `DELETE` con If-Match; borra fila y secretos |

`Paused` y `NeedsAttention` son invisibles para los consumidores: `IIntegrationConnections.ResolveAsync`
no los devuelve (ver «Puertos»).

**Reglas del agregado (`IntegrationConnection`):**

- `Create(provider, tenantId, name, fields, secrets, createdBy, now)`: valida nombre (trim, 1–80,
  sin saltos de línea), que cada campo requerido venga, que no venga ningún campo fuera del
  catálogo, patrones y largos. Nada se asigna hasta que todo valida.
- `Update(name, fields, secrets, now)`: un secreto **ausente conserva el guardado**; para borrarlo no
  hay gesto, porque todos los secretos de la versión 1 son requeridos. Si algún día un secreto es
  opcional, se agrega un `clear` explícito; no se reutiliza `null`. Es distinto del criterio de
  `OrderPaymentProof.UpdatePaidOn` y se decide así a propósito: una credencial de sólo escritura no
  puede borrarse por un campo que el formulario no manda.
- Devuelve `changedFields` (claves de campo, secretos incluidos por clave, nunca por valor) para la
  auditoría y para decidir si hay que volver a probar.
- `Pause/Resume/MarkNeedsAttention(code, now)/MarkVerified(now)/Reprotect(fieldKey, protectedSecret)`.
- Códigos de dominio: `integrations.connection.name_invalid`, `integrations.connection.name_taken`
  (lo traduce Infrastructure desde el índice único, por **nombre de índice**),
  `integrations.connection.field_required`, `integrations.connection.field_invalid`,
  `integrations.connection.field_unknown`, `integrations.connection.limit_reached`,
  `integrations.connection.not_paused`, `integrations.connection.not_active`.

### Secreto en reposo

Se mueve tal cual lo que ya existe en Quotations, con tres cambios de nombre:

- `ProtectedSecret { KeyId, Ciphertext }` → `Modules.Integrations.Domain`.
- `IWhatsAppSecretProtector` → `ISecretProtector` en `Modules.Integrations.Application`:
  `Protect(connectionId, fieldKey, plaintext)`, `Unprotect(connectionId, fieldKey, secret)`,
  `ActiveKeyId`, `HasKey(keyId)`.
- `AesGcmWhatsAppSecretProtector` → `AesGcmSecretProtector`, AES-256-GCM, nonce 12, tag 16, con
  **AAD** `integrations.connection:{connectionId}:{fieldKey}`. Un ciphertext copiado a otra
  conexión o a otro campo no descifra. El prefijo viejo (`quotations.whatsapp.api_token:`) no se
  conserva: nada cifrado con él llegó a producción.
- `SecretProtectionOptions.SectionName` = `Integrations:SecretProtection`
  (`ActiveKeyId` en ConfigMap; `Keys:<id>` en Secret). `SecretProtectionOptionsValidator` con
  `ValidateOnStart`: en `Production` exige `ActiveKeyId` y que su llave exista y sean 32 bytes en
  base64; fuera de producción, sin llave activa el módulo arranca y **crear o editar una conexión
  responde 503 `integrations.secret_protection.unavailable`** (la lectura sigue funcionando).
- `WhatsAppTokenRekeyWorker` → `ConnectionSecretRekeyWorker`: al arrancar y cada
  `Integrations:SecretProtection:RekeyIntervalMinutes` (default 60), re-cifra en lotes de 100 todo
  `connection_secrets` cuyo `key_id <> ActiveKeyId` y cuya llave vieja siga configurada. Lo que no
  puede descifrar lo deja y lo cuenta en un log de advertencia **sin el valor**, una vez por
  corrida. Mismo comportamiento que hoy.
- La variable secreta del pipeline pasa a ser `INTEGRATIONS_SECRET_PROTECTION_KEY_K1`. Las líneas
  `Quotations__SecretProtection__*` de `k8s/prod-configMap.yaml` y `k8s/prod-secret.yaml` se quitan:
  nunca se desplegaron.

### Probar la credencial (`IConnectionTester`)

Puerto de Application: `Task<ConnectionTestResult> TestAsync(IntegrationProvider provider,
IReadOnlyDictionary<string,string> fields, IReadOnlyDictionary<string,string> secrets, ct)`.
Un adaptador por proveedor en Infrastructure, elegido por `provider.Key` en un
`ConnectionTesterRegistry`. Resultado: `Ok`, `CredentialsRejected`, `Unreachable(reason)` o
`Invalid(fieldKey, reason)`.

- **Zenvia:** `GET {BaseUrl}/v2/templates` con `X-API-TOKEN`. 2xx → `Ok`; 401/403 →
  `CredentialsRejected`; timeout, 5xx o excepción de red → `Unreachable`. El número emisor no se
  valida contra Zenvia: la API no lo expone de forma estable (ver DECISIÓN-PENDIENTE 1).
  `Integrations:Zenvia:BaseUrl` (default `https://api.zenvia.com`) reemplaza a
  `Quotations:WhatsApp:BaseUrl` **sólo para esta prueba**; el sender global de Quotations sigue
  leyendo el suyo hasta el spec del consumidor.
- **Timeout** de 10 s por prueba, `HttpClient` propio del módulo registrado con
  `IHttpClientFactory`, `User-Agent: qep-integrations`.
- La prueba corre **después** de validar campos y **antes** de `SaveChanges`. `Unreachable` también
  bloquea el guardado: mejor un «no pude verificar, inténtalo de nuevo» que una conexión activa
  que nadie probó (decisión 5).
- `POST /connections/{id}/test` ejecuta la misma prueba con lo guardado y actualiza
  `last_verified_at` o `last_failure_*`; si pasa y estaba en `NeedsAttention`, vuelve a `Active`.

### Puertos para los consumidores (se definen y prueban acá; nadie los usa todavía)

En `Modules.Integrations.Application`:

```csharp
public interface IIntegrationConnections
{
    // Sólo Active y visible para el tenant. Null si no existe, es de otro tenant, está Paused o
    // NeedsAttention: el consumidor decide qué hacer con "no hay conexión". Los secretos vienen en
    // claro, en memoria, para ese request; el consumidor no los persiste ni los registra.
    Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken ct);

    // Las Active de un proveedor, para que la pantalla del consumidor deje elegir una.
    Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken ct);
}

public interface IConnectionHealthReporter
{
    Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken ct);
}
```

Cada consumidor declara **su propio puerto** en su Application (p. ej. `IQuotationWhatsAppAccount`)
y el adaptador vive en `src/Bootstrapper`, como `QuotationFileLookup` (CAT-05). Un módulo de
negocio nunca referencia `Modules.Integrations.Application`; `ArchitectureTests` lo verifica.

### Eventos de outbox (para que un consumidor suelte una conexión que ya no sirve)

`integrations.connection-paused.v1`, `integrations.connection-deleted.v1`,
`integrations.connection-needs-attention.v1`. Payload: `{ tenantId, connectionId, providerKey,
occurredAt }`. Se escriben con `IOutboxWriter` en la misma transacción. Sin consumidores en este
spec.

### Auditoría

Camino atómico (`IAuditRecorder`, proyección `audit.entries` en `IntegrationsDbContext`, ADR 0019):
`integrations.connection.created|updated|paused|resumed|deleted|verified|needs_attention`, recurso
`integration_connection`, con `changedFields` por clave. **Nunca un valor de campo**, ni público:
un `fromNumber` tampoco va a auditoría; va la clave `fromNumber`.

## Backend

### Proyectos

`src/Modules/Integrations/Modules.Integrations.{Domain,Application,Infrastructure,Api}` con sus
`packages.lock.json` (regenerar con `dotnet restore --force-evaluate`), `IntegrationsDbContext`
(esquema `integrations`, con `IntegrationsDbContextFactory` para `dotnet ef`), `IntegrationsUnitOfWork`
(traduce `IX_connections_tenant_provider_name` → `integrations.connection.name_taken` y
`DbUpdateConcurrencyException` → `concurrency.conflict`), y `tests/ArchitectureTests/IntegrationsLayerTests.cs`.

Referencias permitidas: `Modules.Integrations.Application` → `Modules.Tenancy.Application` (por
`ITenantModules`, `IExecutionContext`) y `Modules.Audit.Application`. Ningún otro `Modules.*`.

### Permisos

`IntegrationsPermissions.ConnectionRead = "integrations.connection.read"` y
`ConnectionManage = "integrations.connection.manage"`. Las dos mitades: constantes **y** políticas
en `AddAuthorization` (`QepServiceCollectionExtensions`), más la semilla del rol de sistema `admin`
en Authorization. Los handlers revalidan tenant y permiso antes del repositorio (403, nunca 404). El
stub de desarrollo no los concede: las pruebas los piden por `X-Permissions`.

### Endpoints (`/api/v1/tenants/{tenantId:guid}/integrations`)

| Método y ruta | Permiso | Respuesta | Notas |
| --- | --- | --- | --- |
| `GET /catalog` | read | `IntegrationsCatalogResponse { providers[] }` | Sólo proveedores visibles. Cada uno con `key, displayName, category, fields[] {key, label, kind, required, maxLength}, maxConnections, connectionCount`. Colección completa aunque esté vacía |
| `GET /connections` | read | `ConnectionsResponse { items[] }` | Todas las del tenant cuyo proveedor es visible, ordenadas por proveedor y nombre. Sin paginación: hay tope de 20 por proveedor |
| `POST /connections` | manage | 201 `ConnectionResponse` | Cuerpo `{ providerKey, name, fields{} , secrets{} }`. Valida, prueba, guarda |
| `GET /connections/{id}` | read | `ConnectionResponse` | |
| `PUT /connections/{id}` | manage | `ConnectionResponse` | If-Match obligatorio (428/412). Cuerpo `{ name, fields{}, secrets{} }`; secreto ausente conserva. Prueba sólo si cambió algún campo o secreto |
| `POST /connections/{id}/test` | manage | `ConnectionResponse` | Prueba con lo guardado |
| `POST /connections/{id}/pause` | manage | `ConnectionResponse` | If-Match |
| `POST /connections/{id}/resume` | manage | `ConnectionResponse` | If-Match; vuelve a probar |
| `DELETE /connections/{id}` | manage | 204 | If-Match |

`ConnectionResponse`:

```json
{
  "id": "…", "providerKey": "zenvia", "name": "WhatsApp sede norte", "status": "Active",
  "fields": { "fromNumber": "573001234567" },
  "secrets": { "apiToken": { "configured": true, "updatedAt": "2026-10-08T14:00:00Z", "readable": true } },
  "lastVerifiedAt": "…", "lastFailureAt": null, "lastFailureCode": null,
  "createdAt": "…", "createdBy": { "memberId": "…", "displayName": "…" }, "updatedAt": "…", "version": 3
}
```

`readable` descifra de verdad y descarta el valor, como hoy `ApiKeyReadable`: detecta llave
retirada y bytes corruptos para que la pantalla pida pegar la clave otra vez. Los enums viajan
por nombre (`Active`, `Messaging`, `Secret`): el diccionario lo tiene el frontend.

### Códigos de error (mapeo central en `ApiExceptionHandler`)

| Código | HTTP | Cuándo |
| --- | --- | --- |
| `validation.failed` con `errors` `{ "name": [...], "fields.fromNumber": [...], "secrets.apiToken": [...] }` | 422 | FluentValidation: requeridos, largos, patrones, claves desconocidas, `providerKey` fuera del catálogo |
| `integrations.connection.name_taken` | 422 | índice único |
| `integrations.connection.limit_reached` | 422 | 20 por proveedor |
| `integrations.connection.credentials_rejected` | 422 | el proveedor rechazó la credencial; `errors: { "secrets.apiToken": [...] }` para que el formulario marque el campo |
| `integrations.connection.provider_unreachable` | 422 | no se pudo verificar; el formulario lo muestra como aviso, no en un campo |
| `integrations.connection.not_paused` / `not_active` | 422 | resume sin estar pausada / pause sin estar activa |
| `integrations.secret_protection.unavailable` | 503 | sin llave activa fuera de producción |
| `tenancy.module_not_enabled` | 403 | proveedor no visible para el tenant |
| `authorization.denied` | 403 | otro tenant o sin permiso |
| `integrations.connection.not_found` | 404 | id inexistente **dentro del tenant** |
| `concurrency.conflict` / precondición | 412 / 428 | If-Match |

### Nunca en un log

Mismas reglas que el spec anterior, ahora para todo secreto: `ISecretProtector` no registra; el
tester registra código y status HTTP del proveedor, nunca headers ni cuerpo de la petición; el
`HttpClient` del módulo no tiene logging de headers. La prueba de fuga manda un token sentinela y
busca su valor en logs capturados, respuestas, auditoría y outbox.

## Lo que se le quita a Quotations y al frontend (fase 0)

Decisión 1 (sin `Shared`) y 8 (módulo nuevo) hacen que el código de WhatsApp por tenant en
`develop` no tenga destino. Se **retira** antes de construir Integrations, para que `develop` pueda
ir a `main` con entitlements, POS y la consola sin arrastrarlo:

**Backend (Quotations):** `TenantWhatsAppSettings`, `WhatsAppMode`, `WhatsAppProvider`,
`ITenantWhatsAppSettingsRepository`, `TenantWhatsAppSettingsRepository`, `IWhatsAppSecretProtector`,
`AesGcmWhatsAppSecretProtector`, `WhatsAppTokenRekeyWorker`, `SecretProtectionOptions(+Validator)`,
`IWhatsAppChannelResolver`, `WhatsAppChannelResolver`, `GetWhatsAppSettings`,
`UpdateWhatsAppSettings`, `GetWhatsAppChannel`, `WhatsAppSettingsDto`, `WhatsAppSettingsEndpoints`,
la ruta `/quotations/whatsapp-channel`, `QuotationWhatsAppOutcome` y `QuotationResponse.WhatsAppOutcome`,
`QuotationChangeSummary.SentWithoutWhatsApp/ResentWithoutWhatsApp`, el mapeo y los `CHECK` de
`tenant_whatsapp_settings` en `QuotationsDbContext`, la traducción en `QuotationsUnitOfWork`, y una
migración nueva `DropTenantWhatsAppSettings` (no se edita la que ya existe: hay bases locales con
ella). `SendQuotationHandler` vuelve al comportamiento de `main`: `IWhatsAppSender` global
(`ZenviaWhatsAppSender` o `LogWhatsAppSender`), `ZenviaSenderSettings` desaparece y el sender vuelve
a leer `QuotationsOptions.WhatsApp`. `ProtectedSecret`, el protector AES, el worker y **sus
pruebas** no se borran: se mueven a Integrations en la fase 1 con `git mv` para conservar historia.

**Frontend:** `features/tenant-settings/{components/whatsapp-settings-section*, hooks/use-whatsapp-settings*,
services/whatsapp-settings.api*, types/whatsapp-settings*}`, el `whatsAppSection` de
`routes/_authenticated/settings/index.tsx`, y en `features/quotes` el `/whatsapp-channel`
(`quotes.api.ts:317-367`), `whatsAppOutcome` (`use-quote-send-flow.tsx`) y el aviso de «cuenta
propia» en el diálogo de envío. Los códigos `quotation.whatsapp.recipient_missing|send_failed|
credentials_rejected` se quedan: son del sender global que sigue en `main`.

**Pruebas:** las de WhatsApp por tenant se retiran con el código; `SendQuotationHandlerTests`,
`QuotationsOptionsValidatorTests` y los harness (`QuotationsApiHarness`, `ReportingApiHarness`,
`PosApiHarness`, `ConfigurationExampleTests`) vuelven a su forma de `main`.

Esta fase se puede hacer **sola y primero**, en su propio commit (`revert`), y desbloquea el
despliegue de lo demás. Ver `HANDOFF-integraciones.md`.

## Frontend (`src/features/integrations/`, Screaming Architecture)

```text
features/integrations/
  pages/integrations-page.tsx (+ .test.tsx)           ruta /settings/integrations
  components/
    provider-catalog.tsx                                tarjetas por categoría; «Conectar» abre el formulario
    connection-list.tsx                                 agrupada por proveedor; badge de estado; acciones
    connection-form-dialog.tsx (+ .test.tsx)            campos desde el catálogo; secretos como password
    connection-status-badge.tsx                         Activa / Pausada / Necesita atención
    delete-connection-dialog.tsx (+ .test.tsx)          confirmación escribiendo el nombre
  hooks/use-integrations-catalog.ts, use-connections.ts, use-connection-mutations.ts
  services/integrations.api.ts (+ .test.ts)            rutas, If-Match, mapa de códigos → mensajes
  types/integrations.ts, integrations.schema.ts        zod
  utils/describe-integration-failure.ts
```

- `routes/_authenticated/settings/integrations.tsx` sólo cablea la página. En `settings/index.tsx`
  se agrega una tarjeta «Integraciones» como la de «Columnas del Excel de pedidos», visible con
  `can('integrations.connection.read')`.
- El formulario dibuja los campos a partir de `fields[]` del catálogo: `Text` y `Url` como input,
  `Phone` con la máscara ya usada para `from_number`, `Secret` como password con el estado
  «Configurada el 8 de octubre» y el texto de ayuda «Déjala en blanco para conservar la actual».
  Cuando `readable === false`, aviso «La clave guardada ya no se puede leer: pégala de nuevo».
- Al guardar, el botón muestra «Verificando con el proveedor…»; `credentials_rejected` marca el
  campo del secreto; `provider_unreachable` muestra un aviso arriba con «Intentar de nuevo».
- Eliminar: diálogo que exige escribir el nombre exacto; texto en tuteo: «Esta acción no se puede
  deshacer. Escribe el nombre de la conexión para confirmar».
- Textos de UI en español con tuteo; comentarios en inglés (convención del frontend).

## Despliegue

1. Variable secreta `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` (32 bytes aleatorios en base64) en el
   grupo `Backend-prod`, con copia en la bóveda del owner. `k8s/prod-secret.yaml`:
   `Integrations__SecretProtection__Keys__k1: "#{INTEGRATIONS_SECRET_PROTECTION_KEY_K1}#"`.
   `k8s/prod-configMap.yaml`: `Integrations__SecretProtection__ActiveKeyId: "k1"`. Sin la variable,
   `ValidateOnStart` deja el pod en crash-loop: es a propósito.
2. En local, user-secrets `Integrations:SecretProtection:Keys:k1` y `ActiveKeyId`, con el mismo
   procedimiento del README (verificar con `Measure-Object`, nunca listando el valor).
3. `Quotations__WhatsApp__*` **se quedan**: el sender global sigue hasta el spec del consumidor. El
   README deja de describir «WhatsApp por tenant» y gana «Integraciones»; la rotación se documenta
   igual que hoy, con la sección nueva.
4. Backend antes que frontend. Rollback del backend: el frontend muestra la tarjeta y la API
   responde 404; nada se corrompe porque ningún consumidor depende aún del módulo.

## Pruebas (TDD, RED antes que GREEN, con evidencia literal)

**Unitarias (`tests/Modules/Integrations/Modules.Integrations.UnitTests`):**
agregado (crear, campos requeridos/desconocidos, nombre, transiciones, secreto ausente conserva,
`changedFields` sin valores, `Reprotect`), catálogo (`Parse`, filtro por módulos activos y por
stub), `AesGcmSecretProtector` (movidas; AAD con conexión y campo), validador de opciones
(movido), handlers con dobles (`FakeConnectionTester` por resultado, repositorio en memoria,
`ITenantModules` falso), validadores FluentValidation (claves `fields.x` / `secrets.x`).

**Integración (`Modules.Integrations.IntegrationTests`, Testcontainers):** crear con tester falso
HTTP (`HttpMessageHandler` que responde 200/401/timeout), 422 por credencial rechazada **sin fila
guardada**, nombre repetido, tope de 20, otro tenant 403, módulo apagado 403 y conexión oculta,
pause/resume/test/delete con If-Match (428/412), fuga de secreto con sentinela, worker de
re-cifrado (dos llaves; la retirada deja el secreto y lo cuenta), persistencia de `CHECK`s,
puerto `IIntegrationConnections.ResolveAsync` (Active sí, Paused y NeedsAttention no, otro tenant
no) y `IConnectionHealthReporter`. El harness fija `Integrations:SecretProtection:*` como hoy lo
hacen los de Quotations y Reporting.

**Arquitectura:** `IntegrationsLayerTests` (capas, y que ningún `Modules.*` de negocio referencie
`Modules.Integrations.Application`), `ConfigurationExampleTests` con las claves nuevas.

**Frontend (Vitest + Testing Library):** página con catálogo vacío, con conexiones, sin permiso de
gestión (modo lectura), formulario por tipo de campo, secreto en blanco conserva, errores por
código, confirmación de borrado por nombre.

**Suite completa** una sola vez al final por repo, comparada por nombre contra la baseline
(`develop` al empezar), y `dotnet build` sin warnings.

## Decisiones tomadas sin el owner

| # | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D1 | Tope de **20 conexiones por proveedor y tenant** | Una constante |
| D2 | `NeedsAttention` **sin umbral**: un 401/403 definitivo del proveedor basta | Si un proveedor da 401 intermitentes, conexiones pausadas en falso; se agrega umbral en el reporter |
| D3 | `Unreachable` **bloquea** el guardado, igual que `CredentialsRejected` | Si Zenvia tiene caídas largas, el admin no puede conectar hasta que vuelva |
| D4 | `resume` **vuelve a probar** la credencial | Un segundo de más al reanudar |
| D5 | Secreto ausente en `PUT` **conserva** (sólo escritura); no hay `null` que borre | Si un secreto pasa a opcional, se agrega `clear` explícito |
| D6 | La plantilla de cotización **no** es campo de la conexión de Zenvia | Si el consumidor quisiera una plantilla por conexión y no por tenant, se mueve en su spec |
| D7 | AAD con `connectionId` y `fieldKey`; prefijo viejo no se conserva | Nada: no hay ciphertext viejo en producción |
| D8 | Fase 0 retira WhatsApp por tenant de `develop` por `revert` antes de construir, para no bloquear `main` | Rehacer algo de la pantalla en la fase 1; a cambio POS y entitlements salen antes |
| D9 | `connection_secrets` como tabla hija y no `jsonb` | Una tabla más; a cambio rotación con índice y sin riesgo de `SELECT` que traiga secretos |
| D10 | Un `CHECK` en `provider_key` contra el catálogo; proveedor nuevo = migración | Igual que `tenant_modules`, a propósito |

## DECISIÓN-PENDIENTE

1. **Prueba de Zenvia:** confirmar contra la documentación de Zenvia que `GET /v2/templates` acepta
   `X-API-TOKEN` y responde 401 con token inválido. Si no, elegir otro endpoint barato. No validar el
   número emisor hasta que Zenvia exponga una forma estable.
2. **A qué tenant pertenece la cuenta global de hoy** (`Quotations__WhatsApp__*` en prod). Cuando
   llegue el spec del consumidor, ese tenant conecta su cuenta en Integraciones y las claves
   globales se retiran. Hasta entonces nada cambia para él.
3. **Consola de operador:** ¿el operador ve las conexiones de los tenants (sin secretos) para
   soporte? Hoy no.
4. **Límite de pruebas por minuto** (`POST .../test` dispara HTTP saliente): hoy sólo el rate limit
   general de la API.
5. **Aviso al administrador** cuando una conexión pasa a `NeedsAttention` (correo por
   Notifications). Hoy sólo el badge.

## Fuera de alcance

- Cualquier **uso** de una conexión: enviar cotizaciones por la cuenta del tenant (spec del
  consumidor Quotations, que además mueve `TemplateId` a Quotations y retira el sender global),
  campañas, mensajería oficial (Meta: webhooks de entrada, bandeja), guías de transportadoras,
  OpenAI. Cada uno trae su proveedor al catálogo.
- OAuth: todos los proveedores de la versión 1 son por clave pegada. Cuando llegue Meta, el
  catálogo gana `Kind = OAuth` y un flujo de redirección; la tabla ya lo soporta (los tokens son
  secretos más).
- Compartir conexiones entre tenants, o conexiones "provistas por QEP" (decisión 1).
- Webhooks entrantes y sus secretos.

## Historial de revisión

- 2026-10-08: decisiones 1–8 tomadas en conversación con el owner; spec escrito. Pendiente la
  lectura del owner y la revisión del plan en la sesión de implementación.
