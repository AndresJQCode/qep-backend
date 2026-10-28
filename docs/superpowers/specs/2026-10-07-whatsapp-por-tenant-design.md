# Envío de cotizaciones por WhatsApp configurable por tenant (Zenvia)

**Fecha:** 2026-10-07
**Módulos:** Quotations (Domain, Application, Infrastructure, Api), Tenancy (sólo lectura de
capacidades), Api/Bootstrapper (mapeo); frontend `features/tenant-settings` y `features/quotes`
**Estado:** borrador para revisión del owner (rondas 1, 2 y 3 de revisión aplicadas)
**Depende de:** el spec de módulos por tenant (entitlements) de esta misma noche, por el puerto
`ITenantModules`, `TenantModuleGuard`, `TenantModuleKeys.Quotations` y el código
`tenancy.module_not_enabled`.

## Problema

Hoy todas las empresas mandan sus cotizaciones por **la misma cuenta de Zenvia**: la de
`Quotations:WhatsApp:{ApiToken, FromNumber, TemplateId}`, global al proceso.
`QuotationsInfrastructureExtensions.AddWhatsAppSender` (`:100-122`) registra un único
`IWhatsAppSender` singleton: `ZenviaWhatsAppSender` si las tres claves están, `LogWhatsAppSender`
(que sólo escribe un log) si falta alguna. `QuotationsOptionsValidator` (`:26-28`) exige las tres
en `Production` para que el no-op no sea un envío fantasma.

Eso impide tres cosas que el owner pidió:

- que una empresa mande desde **su propio número** y con **su propia plantilla** aprobada;
- que una empresa **no** mande WhatsApp (porque comparte el PDF por otro canal);
- que lo configure un administrador del tenant desde Ajustes, sin que QCode toque un secreto de k8s.

## Objetivo

Una configuración de WhatsApp por tenant, guardada en Quotations, editable desde Ajustes, con tres
modos —**cuenta de QEP** (`Shared`, la de hoy), **cuenta propia** (`Own`) y **desactivado**
(`Disabled`)—, que se puede cambiar en cualquier dirección. La API key de la cuenta propia queda
cifrada en reposo y es de sólo escritura. El envío de la cotización resuelve el modo en cada
`POST .../send`, sin romper a los tenants que hoy dependen de la configuración global.

## Criterios de éxito

1. Un tenant sin configuración, o en modo `Shared`, envía exactamente como hoy: las pruebas de
   **integración** existentes siguen verdes sin tocarlas. Las **unitarias** que construyen las piezas
   que cambian de firma llevan sólo ediciones mecánicas, sin cambiar lo que afirman:
   `SendQuotationHandlerTests` (el handler recibe `IWhatsAppChannelResolver` en vez de
   `IWhatsAppSender`), `ZenviaWhatsAppSenderTests` (el sender recibe `ZenviaSenderSettings` en vez de
   `IOptions<QuotationsOptions>`) y `QuotationChangeSummaryTests` (si `SendFailed(stage)` suma el
   parámetro de WhatsApp saltado, las llamadas existentes lo pasan en `false`).
2. Un tenant en modo `Own` envía con **su** token, **su** número y **su** plantilla.
3. Un tenant en modo `Disabled` marca la cotización como enviada **y la respuesta y la pantalla
   dicen explícitamente que no salió ningún WhatsApp**.
4. Ningún modo es un callejón sin salida: desde `Disabled` se vuelve a `Shared` o a `Own` desde la
   misma pantalla, y pasar de `Own` a `Shared` no obliga a volver a escribir las credenciales
   propias para regresar a `Own`.
5. La API key no sale nunca de la API: ni en el `GET`, ni en el `PUT`, ni en un `ProblemDetails`,
   ni en `platform.request_failures`, ni en un log. Hay pruebas que lo afirman sobre cada uno de
   esos caminos (ver "Pruebas").
6. Un volcado de la base sola no alcanza para leer la API key.

## Reglas de resolución

Se resuelve al principio de cada envío, una lectura por PK, sin caché:

| Fila en `quotations.tenant_whatsapp_settings` | Canal efectivo | Qué pasa al enviar | `whatsAppOutcome` |
| --- | --- | --- | --- |
| No existe | `Shared` | Lo de hoy: el `IWhatsAppSender` global (Zenvia con las claves globales, o `LogWhatsAppSender` fuera de producción) | `Accepted` |
| `mode = 'Shared'` | `Shared` | Igual que sin fila. Las credenciales propias guardadas, si las hay, se ignoran | `Accepted` |
| `mode = 'Own'` | `Own` | `ZenviaWhatsAppSender` armado con el token, número y plantilla del tenant | `Accepted` |
| `mode = 'Disabled'` | `Disabled` | PDF y snapshots como siempre, **sin** copia pública ni mensaje; la cotización pasa a `Sent` | `Disabled` |

"Sin fila" y `Shared` son el mismo canal a propósito: un tenant nuevo nace sin fila y envía como
hoy, y un tenant que eligió volver a la cuenta de QEP queda exactamente igual.

`Accepted` y no `Sent`: un 2xx de Zenvia significa que encoló el mensaje, la entrega la resuelve
Meta después (mismo criterio que `LogAccepted` en `ZenviaWhatsAppSender.cs:35-44`).

### Cómo se hace explícito que no salió nada

Es la trampa del "envío fantasma" (memoria `envio-whatsapp-fantasma-log-sender`, y el motivo
entero de `QuotationsOptionsValidator`): un 200 con la cotización en `Sent` no se distingue de un
envío real. Con `Disabled` eso pasa **a propósito**, así que tiene que decirse en cuatro lugares:

1. **Antes de enviar.** `GET /api/v1/tenants/{tenantId}/quotations/whatsapp-channel` (permiso
   `quotations.quotation.manage`, el mismo de enviar) responde `{ "enabled": false, "mode": "Disabled" }`
   cuando el modo es `Disabled` (`enabled` es `mode != Disabled`; sin fila, `true` con `mode: "Shared"`).
   `mode` viaja para que la pantalla sepa si una falla de Zenvia es de la cuenta propia (ver
   "Envío" en Frontend). El flujo de envío la pide con `queryClient.fetchQuery` **dentro** de
   `start()`, antes de decidir qué mostrar, y abre un diálogo de confirmación en vez de preguntar
   destinatario:
   - primer envío: *"El envío por WhatsApp está desactivado para tu empresa. La cotización quedará
     marcada como enviada, pero el cliente no recibirá ningún mensaje: descarga el PDF y compártelo
     tú."*, botones **"Marcar como enviada"** y "Cancelar";
   - reenvío: *"El envío por WhatsApp está desactivado para tu empresa. La cotización quedará
     marcada como reenviada, pero el cliente no recibirá ningún mensaje: descarga el PDF y
     compártelo tú."*, botones **"Marcar como reenviada"** y "Cancelar".

   Si esta consulta falla (red, o un `404` porque el frontend llegó a producción antes que el
   backend), el canal se trata como **desconocido** y el flujo sigue el camino normal: preguntar
   destinatario si hay dos y enviar. No se bloquea el envío por no poder leer el canal: la
   respuesta del `send` (paso 2) es la que decide qué se le dice a la persona.
2. **En la respuesta del `send`.** `QuotationResponse` suma `WhatsAppOutcome` (`"Accepted"` |
   `"Disabled"`), nulo fuera del envío. La pantalla lo lee de la respuesta y no del paso 1: si un
   administrador desactivó WhatsApp entre que se abrió el diálogo y se confirmó, la respuesta es
   la que manda.
3. **En el aviso.** Con `Disabled`, `toast.warning` en vez de `toast.success`, con la acción
   "Descargar PDF" (el `POST .../pdf` que ya existe):
   - primer envío: *"Cotización marcada como enviada. No se envió WhatsApp: está desactivado para tu
     empresa."*;
   - reenvío: *"Cotización marcada como reenviada. No se envió WhatsApp: está desactivado para tu
     empresa."*

   Los textos actuales *"Cotización enviada por WhatsApp."* / *"Cotización reenviada por
   WhatsApp."* (`use-quote-send-flow.tsx:70-74`) quedan sólo para `Accepted`.
4. **En el historial.** El tipo de evento sigue siendo `Sent`/`Resent` (es lo que cambió de
   estado), pero el resumen, en `QuotationChangeSummary`, dice *"Marcada como enviada sin WhatsApp:
   el envío por WhatsApp está desactivado para la empresa."* o *"Marcada como reenviada sin
   WhatsApp: el envío por WhatsApp está desactivado para la empresa."*

## Manejo del secreto

### Qué hay hoy en el código

- **No hay Data Protection configurado**: ni `AddDataProtection` ni `PersistKeysTo*` en `src/`, y
  `docs/superpowers/plans/2026-09-24-replicas-multiples.md:27` se apoya en eso. Aunque el framework
  lo registre de forma implícita, sin `PersistKeysTo*` el anillo de llaves vive en el filesystem
  del pod o en memoria, y `k8s/prod-deployment.yaml` no monta volúmenes: **cada deploy perdería
  la llave** y con ella todas las API keys guardadas.
- Los secretos de producción llegan por variables de entorno desde `k8s/prod-secret.yaml`, cuyos
  valores son **tokens de reemplazo del pipeline** (`#{...}#`, comentario en `:19-21`): el valor real
  vive en el grupo de variables `Backend-prod` de Azure DevOps (`azure-pipelines.yml:21`) y el
  pipeline aplica el manifiesto en cada deploy (`azure-pipelines.yml:49-55`). En local llegan por
  user-secrets. `System.Security.Cryptography` ya se usa (`SessionService`, `InvitationTokens`).

### Decisión: AES-256-GCM con llave en configuración secreta

`System.Security.Cryptography.AesGcm`, en la caja de .NET, con la llave fuera de la base.

Por qué no Data Protection persistido: exigiría `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`
(paquete nuevo, `Directory.Packages.props` y los 74 `packages.lock.json` regenerados) y, si las
llaves se persisten en la misma base, un volcado de la base trae la llave y el texto cifrado
juntos: no protege nada sin además cifrar el anillo con un certificado, que es otra pieza que
aprovisionar. Con AES-GCM la llave vive **fuera** de la base, que es lo que cumple el criterio 6.

Formato:

- `api_token_ciphertext` (`bytea`) = `nonce(12) || ciphertext || tag(16)`, nonce aleatorio por
  cifrado (`RandomNumberGenerator`).
- `api_token_key_id` (`varchar(32)`): qué llave lo cifró.
- **Datos asociados (AAD)** = los bytes UTF-8 de
  `"quotations.whatsapp.api_token:" + tenantId.ToString("D")` (formato `D` explícito: minúsculas,
  con guiones, sin llaves; cualquier otro formato no descifraría lo ya guardado). Una fila copiada
  a otro tenant por SQL no descifra: falla el tag.

Configuración: sección `Quotations:SecretProtection`, clase **`SecretProtectionOptions` propia**
(`SectionName = "Quotations:SecretProtection"`), registrada aparte de `QuotationsOptions` con
`AddOptions<SecretProtectionOptions>().Bind(...).ValidateOnStart()` y su propio
`SecretProtectionOptionsValidator`. No es una propiedad de `QuotationsOptions`: así sus reglas no
se mezclan con las de WhatsApp y PDF, y su validador se prueba solo.

| Clave | Dónde vive en prod | Contenido |
| --- | --- | --- |
| `Quotations:SecretProtection:ActiveKeyId` | ConfigMap (`Quotations__SecretProtection__ActiveKeyId`) | id de llave; no es secreto |
| `Quotations:SecretProtection:Keys:<id>` | Secret (`Quotations__SecretProtection__Keys__k1`), valor desde la variable secreta del pipeline | 32 bytes en base64 |

Los ids de llave cumplen `^[a-z0-9]{1,32}$` (caben en `api_token_key_id` y no necesitan escape en
una variable de entorno). Todo mensaje del validador o del protector nombra **el id y la clave de
configuración, nunca el valor**.

`appsettings.example.json` documenta la sección con valores vacíos —`"SecretProtection": {
"ActiveKeyId": "", "Keys": {} }`— y **nunca** con algo que parezca una llave en base64: un ejemplo
con forma de llave real termina copiado a un ambiente. `ConfigurationExampleTests` trata `Keys`
(un diccionario) como hoja, así que el objeto vacío alcanza.

### Generar una llave en local

**Advertencia:** `dotnet user-secrets set` confirma el guardado imprimiendo la clave **y su
valor**, y `dotnet user-secrets list` imprime todos los valores. Las dos salidas quedan en el
historial de la sesión, en un log o en una captura. Por eso el `set` va siempre con `| Out-Null`, la
verificación se hace contando y la llave nunca se escribe literal en la línea de comandos (sólo
viaja en una variable que se borra al terminar):

```powershell
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Quotations:SecretProtection:Keys:k1" $key --project src/Api | Out-Null
Remove-Variable key, bytes
dotnet user-secrets set "Quotations:SecretProtection:ActiveKeyId" "k1" --project src/Api | Out-Null

# Verificar que quedó, sin ver el valor: tiene que dar Count 1.
dotnet user-secrets list --project src/Api | Select-String -Pattern "SecretProtection:Keys:k1" | Measure-Object
```

### Llave de producción: custodia

- **Fuente de verdad:** una variable **secreta** del grupo `Backend-prod` en Azure DevOps,
  `QUOTATIONS_SECRET_PROTECTION_KEY_K1`. `k8s/prod-secret.yaml` suma la línea
  `Quotations__SecretProtection__Keys__k1: "#{QUOTATIONS_SECRET_PROTECTION_KEY_K1}#"`.
- **Respaldo:** una copia fuera de Azure DevOps, en la bóveda de secretos del owner. Perder las dos
  copias es perder todas las API keys guardadas: cada tenant en `Own` tendría que volver a pegar la
  suya (el envío fallaría en `Channel` hasta entonces).
- **No se edita con `kubectl`:** el pipeline vuelve a aplicar `prod-secret.yaml` en cada deploy, así
  que un `kubectl edit` o `patch` sobre el Secret se pisa con el siguiente despliegue. La llave se
  cambia en la variable del grupo y se despliega.
- Para generarla en la máquina del owner, el mismo bloque de arriba hasta `$key`, y después
  `$key | Set-Clipboard`, pegar en la variable secreta del grupo y en la bóveda,
  `Set-Clipboard -Value $null` y `Remove-Variable key, bytes`.
- Verificar en el clúster que la clave existe, sin su valor: `kubectl describe secret` (nombres y
  tamaños). **Nunca** `get -o yaml`, `-o json` ni `custom-columns` sobre `.data`.

### Rotación

**Regla:** nunca se retira una llave a la que todavía apunta alguna fila.

En **dos fases**, cada una con su despliegue. Declarar `k2` y activarla en el mismo despliegue
dejaría una ventana en la que un pod viejo (sólo `k1`) y uno nuevo (`k2` activa) conviven durante
el rolling update: lo que el nuevo cifre con `k2`, el viejo no lo lee.

1. **Fase (a), declarar:** variable nueva `QUOTATIONS_SECRET_PROTECTION_KEY_K2` en el grupo y línea
   nueva `Quotations__SecretProtection__Keys__k2` en `prod-secret.yaml`, con `ActiveKeyId` **todavía
   en `k1`**; desplegar. Desde aquí todos los pods conocen las dos llaves y nada cambia.
2. **Fase (b), activar:** `ActiveKeyId = k2` en el ConfigMap; desplegar. Lo que se guarde desde ahí
   sale con `k2`; lo viejo se sigue leyendo con `k1` por su `key_id`. **Ventana del rolling
   update:** mientras conviven pods, uno viejo (todavía con `k1` activa) que atienda un `PUT` puede
   volver a cifrar una fila con `k1`, incluso una que el worker de un pod nuevo ya había pasado a
   `k2`. Por eso, si después del rollout el conteo del paso 3 no da 0 en `k1`, se reinicia el
   deployment **una vez** (ya sin pods viejos, el worker de cada arranque la recoge):

   ```powershell
   kubectl --context contabo-prod -n <namespace> rollout restart deployment/<deployment>
   kubectl --context contabo-prod -n <namespace> rollout status deployment/<deployment>
   ```
3. **Re-cifrado al arrancar:** `WhatsAppTokenRekeyWorker` (ver Infrastructure) corre una vez en cada
   arranque y re-cifra con la activa toda fila cuyo `api_token_key_id` no sea la activa. Después del
   despliegue de la fase (b), `SELECT api_token_key_id, count(*) FROM quotations.tenant_whatsapp_settings GROUP BY 1`
   tiene que dar 0 filas en `k1`. Si no da 0, el log del worker dice qué tenants se saltó y por qué
   (sólo tenant id y key id), y su log final da el resumen (`rekey finished: n re-encrypted, m
   skipped`). Si las saltadas son 0 y aun así quedan filas en `k1`, es la ventana del rolling update
   de la fase (b): se resuelve con el reinicio de arriba.
4. **Drenaje al guardar**, como red adicional: en cualquier `PUT` sobre una fila cuya key está
   cifrada con una llave que no es la activa, el handler la re-cifra con la activa aunque el `PUT`
   no traiga `apiKey`. Si no se puede descifrar (llave ausente o tag inválido), se salta el
   re-cifrado y el `PUT` se guarda igual: el drenaje nunca convierte un guardado en un 500.
5. `k1` se retira (variable, línea del Secret) sólo cuando el conteo de arriba da 0 para `k1`.
   Retirarla antes deja a esos tenants con `apiKeyReadable: false` en el `GET` y, si están en
   `Own`, sin poder enviar (falla en la etapa `Channel`, ver abajo) hasta que vuelvan a pegar la key.
6. Si la llave se filtró: las dos fases, y en cuanto el conteo dé 0 se retira la filtrada. Además
   hay que pedirles a los tenants en `Own` que roten su token en Zenvia: la llave vieja descifra
   cualquier volcado anterior de la base.

### Sólo escritura y nunca en un log

Por dónde podría salir hoy un valor que viaje en una excepción, verificado en el código:

| Camino | Qué guarda o muestra |
| --- | --- |
| `ProblemDetails.Detail = exception.Message` (`ApiExceptionHandler.cs:52`) | el mensaje, en el cuerpo de la respuesta |
| `LogRequestFailure` / `LogUnhandledException` (`ApiExceptionHandler.cs:41-45`) | la excepción entera (mensaje, internas, pila) en el log JSON |
| `AddAspNetCoreInstrumentation(o => o.RecordException = true)` (`QepObservability.cs:51`) | la excepción como evento de la traza (Tempo) |
| `RequestFailureCapture.Describe` (`RequestFailureCapture.cs:46-50`) | `exception.Message` y `exception.ToString()` en `platform.request_failures` (sólo métodos que escriben), legible con `platform.request_log.read` (`PlatformPermissions.cs:15`) |
| `ZenviaWhatsAppSender` (`ZenviaWhatsAppSender.cs:95-97`) | hoy mete el **cuerpo crudo** de la respuesta de Zenvia en el mensaje de `send_failed` |

Los cuatro primeros consumen lo mismo: el mensaje y la cadena de excepciones. La regla, entonces,
es sobre las excepciones: **ninguna excepción de este flujo lleva la key ni el cuerpo crudo de la
respuesta de la cuenta propia de un tenant**. En concreto:

- El `GET` devuelve `apiKeyConfigured`, `apiKeyUpdatedAt` y `apiKeyReadable`; **ni el valor ni sus
  últimos caracteres** (decisión abajo).
- `PUT` con `apiKey` ausente o `null` conserva la guardada. No hay forma de "borrarla": se cambia
  de modo.
- `UpdateWhatsAppSettingsCommand`, el record del cuerpo del `PUT` (`UpdateWhatsAppSettingsRequest`,
  en Api) y `ZenviaSenderSettings` sobreescriben `ToString()` (el de un `record` imprime todas sus
  propiedades) y devuelven la key o el token como `***`. Los tres son `record` que llevan el valor en
  claro, y basta un `{Settings}` en un log o un depurador que los imprima para filtrarlo.
- El validador nunca usa `{PropertyValue}` en un mensaje de `ApiKey`; los mensajes de FluentValidation
  terminan en `ValidationException.Message`, que llega a los cuatro caminos de la tabla.
- `AesGcmWhatsAppSecretProtector` lanza con mensajes que nombran la clave de configuración o el id
  de llave, nunca un valor.
- El sender de una **cuenta propia** (`Own`) arma los mensajes de `credentials_rejected` y
  `send_failed` con el código de estado y, si lo hay, el **código de error de Zenvia** parseado
  (`"Zenvia responded 400 (code: XYZ)."`), **nunca el cuerpo crudo** (ver Infrastructure). El
  sender de la cuenta de QEP (`Shared`) conserva el cuerpo como hoy (decisión abajo).
- El `GET` de settings descifra la key para saber si es legible y **descarta el valor** en el acto:
  sólo sale un booleano.
- El token sólo viaja en el header `X-API-TOKEN` (`ZenviaWhatsAppSender.cs:89`); la
  instrumentación de `HttpClient` de OTel registra la URL, no los headers.
- Se verificó que nada registra cuerpos de request: el dispatcher no loguea comandos
  (`RequestDispatcher.cs`), no hay `UseHttpLogging`, y `RequestFailure` guarda mensaje y detalle de
  la excepción, no el body.

Las pruebas de "Pruebas → Integración" ejercitan los cuatro caminos con una key centinela.

## Modelo de datos

Migración `AddTenantWhatsAppSettings` en `QuotationsDbContext` (con el factory de diseño, ver
CLAUDE.md):

```sql
CREATE TABLE quotations.tenant_whatsapp_settings (
    tenant_id             uuid         NOT NULL,
    mode                  varchar(16)  NOT NULL,
    provider              varchar(16)  NULL,
    api_token_ciphertext  bytea        NULL,
    api_token_key_id      varchar(32)  NULL,
    api_token_updated_at  timestamptz  NULL,
    from_number           varchar(15)  NULL,
    template_id           varchar(64)  NULL,
    version               bigint       NOT NULL,
    updated_at            timestamptz  NOT NULL,
    CONSTRAINT "PK_tenant_whatsapp_settings" PRIMARY KEY (tenant_id),
    CONSTRAINT "CK_tenant_whatsapp_settings_mode"
        CHECK (mode IN ('Shared', 'Own', 'Disabled')),
    CONSTRAINT "CK_tenant_whatsapp_settings_provider"
        CHECK (provider IS NULL OR provider IN ('Zenvia')),
    CONSTRAINT "CK_tenant_whatsapp_settings_token_pair"
        CHECK ((api_token_ciphertext IS NULL) = (api_token_key_id IS NULL)),
    CONSTRAINT "CK_tenant_whatsapp_settings_own_complete"
        CHECK (mode <> 'Own' OR (provider IS NOT NULL AND api_token_ciphertext IS NOT NULL
                                 AND from_number IS NOT NULL AND template_id IS NOT NULL))
);
```

- `mode` viaja y se guarda por nombre (`HasConversion<string>()`), como el resto de enums del
  módulo.
- En `Shared` y `Disabled` las columnas de la cuenta propia **se conservan** si estaban: son lo que
  permite volver a `Own` sin reescribirlas. Sólo se escriben con un `PUT` en `Own`.
- Sin FK a `tenancy.tenants`: mismo criterio que `orders_export_layouts` (un `DbContext` por
  módulo, sin FKs entre schemas).
- **Sin `updated_by`**, aunque el brief lo listaba: sería una columna con id de usuario sin sonda,
  y la regla del spec `2026-10-02-purga-de-membresia-huerfana` es que una sonda sólo va donde la
  fila es historia permanente. Esto es configuración. La auditoría registra quién hizo **qué clase**
  de cambio (ver "Auditoría"), no el valor anterior ni el nuevo.
- `version` es token de concurrencia (`IsConcurrencyToken`), como `OrdersExportLayout`.
- Sin backfill: ningún tenant tiene fila al migrar, así que todos quedan en `Shared`, que es hoy.

## Backend

### Domain (`Modules.Quotations.Domain`)

- `WhatsAppMode { Shared, Own, Disabled }` y `WhatsAppProvider { Zenvia }`: viajan por nombre.
- `ProtectedSecret(string KeyId, byte[] Ciphertext)`: value object opaco; el dominio no cifra.
- `TenantWhatsAppSettings` (agregado, PK `TenantId`): `Mode`, `Provider?`, `ApiToken`
  (`ProtectedSecret?`), `ApiTokenUpdatedAt?`, `FromNumber?`, `TemplateId?`, `Version`, `UpdatedAt`.
  - `static CreateEmpty(tenantId, now)`: en memoria, versión 1, `Mode = Shared`, sin credenciales.
    Sirve para el chequeo de versión del primer `PUT`, igual que `OrdersExportLayout.CreateDefault`
    (D9 del spec 2026-09-24).
  - `WhatsAppSettingsChanges Configure(WhatsAppMode mode, WhatsAppProvider? provider, ProtectedSecret? newToken, ProtectedSecret? rekeyedToken, string? fromNumber, string? templateId, DateTimeOffset now)`:
    - `newToken`, `provider`, `fromNumber` y `templateId` nulos **conservan** lo guardado; con valor,
      lo reemplazan. El handler sólo los pasa con valor cuando `mode` es `Own` (en `Shared` y
      `Disabled` los campos propios del cuerpo se ignoran).
    - Un `newToken` siempre cuenta como reemplazo, aunque el texto sea el mismo de antes: el dominio
      no ve el texto, y el cifrado nuevo trae otro nonce. Actualiza `ApiTokenUpdatedAt`.
    - `rekeyedToken` es el mismo token cifrado con la llave activa (drenaje de rotación): reemplaza
      el texto cifrado **sin** tocar `ApiTokenUpdatedAt`. Nunca viene junto con `newToken`.
    - Si queda `Own` sin las cuatro piezas lanza
      `QuotationsDomainException("quotation.whatsapp_settings.incomplete")`.
    - Pasar a `Shared` o a `Disabled` **conserva** token, número y plantilla.
    - Devuelve qué cambió (`ModeChanged`, `ApiKeyReplaced`, `DetailsChanged`, `KeyRotated`); todo en
      falso es un no-op y no sube versión. Con cualquier cambio sube la versión **una sola vez**, por
      muchas clases que apliquen: un guardado es un incremento.
  - `bool Reprotect(ProtectedSecret rekeyed, DateTimeOffset now)`: lo mismo que `rekeyedToken`, para
    quien re-cifra sin un `PUT` (`WhatsAppTokenRekeyWorker`). No toca `ApiTokenUpdatedAt` y sube la
    versión una vez. El handler del `PUT` no la llama: pasa el re-cifrado por `Configure`.

### Application (`Modules.Quotations.Application`)

Puertos:

```csharp
public interface ITenantWhatsAppSettingsRepository   // FindAsync(tenantId, ct) + Add(settings), sin Update
public interface IWhatsAppSecretProtector
{
    string? ActiveKeyId { get; }                      // null si falta o está vacía
    bool HasKey(string keyId);                        // la llave está configurada; vacío = ausente
    ProtectedSecret Protect(Guid tenantId, string plaintext);   // sin ActiveKeyId lanza
    string Unprotect(Guid tenantId, ProtectedSecret secret);    // lanza si no puede
    bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext);
}
public sealed record WhatsAppChannel(WhatsAppMode Mode, IWhatsAppSender? Sender); // Sender nulo ⇔ Disabled
public interface IWhatsAppChannelResolver            // Task<WhatsAppChannel> ResolveAsync(tenantId, ct)
```

`TryUnprotect` devuelve `false` —sin lanzar y sin loguear nada— cuando la llave del `KeyId` falta o
está vacía, o cuando el descifrado lanza `AuthenticationTagMismatchException` o
`CryptographicException` (bytes dañados, AAD de otro tenant, llave equivocada con el mismo id).
`HasKey` no basta para saber si una key es legible: dice que la llave existe, no que sea la que
cifró esa fila.

Casos de uso:

- `GetWhatsAppSettingsQuery(TenantId)` → `WhatsAppSettingsDto`. Permiso `tenancy.settings.read`.
  `ApiKeyReadable` sale de `TryUnprotect`: el valor descifrado se descarta en el acto.
- `UpdateWhatsAppSettingsCommand(TenantId, string Mode, string? Provider, string? ApiKey, string? FromNumber, string? TemplateId, long ExpectedVersion)`
  → `WhatsAppSettingsDto`. Permiso `tenancy.settings.update`. Mismo esqueleto que
  `UpdateOrdersExportLayoutHandler`, en este orden:
  1. **autoriza antes de validar** (hallazgo B1 de ese spec);
  2. gate por capacidad (abajo);
  3. validador: sólo **formato**, y sólo de los campos que vienen (ver la tabla);
  4. fila o `CreateEmpty`, y chequeo de versión (412);
  5. si `Mode` es `Own`, **lo requerido**, que depende de la fila y por eso no es del validador. Por
     cada campo que no viene en el cuerpo y que la fila no tiene, un error en su campo, todos
     juntos en una sola `ValidationException` (mismo mapa `errors`):
     - `Provider` → *"Elige el proveedor de tu cuenta de WhatsApp."*;
     - `ApiKey`, sin key guardada → *"Pega la API key de tu cuenta de Zenvia."*;
     - `ApiKey`, con key guardada que no se puede leer (`TryUnprotect` en `false`) →
       *"La API key guardada ya no se puede leer: vuelve a pegarla."*;
     - `FromNumber` → *"Escribe el número emisor de tu cuenta de Zenvia."*;
     - `TemplateId` → *"Escribe el id de la plantilla aprobada."*

     Así `{ "mode": "Own" }` vuelve a la cuenta propia cuando todo está guardado y legible, y si
     falta algo dice exactamente qué.
  6. si `Mode` no es `Own`, `Provider`, `ApiKey`, `FromNumber` y `TemplateId` del cuerpo **se
     ignoran**: ni se validan ni se guardan;
  7. drenaje: si no vino `apiKey`, hay key guardada y su `KeyId` no es `ActiveKeyId`,
     `TryUnprotect`; en `true`, `Protect` con la activa y el resultado va como `rekeyedToken`; en
     `false`, **se salta** el re-cifrado y el guardado sigue (un `PUT` a `Disabled` o `Shared` sobre
     una fila ilegible no puede terminar en 500);
  8. `Protect` de la `apiKey` si vino (sólo en `Own`), y **un** `Configure` con todo: una versión
     por guardado;
  9. si algo cambió: `Add` si no había fila, auditoría (abajo), un `SaveChangesAsync`.
- `GetWhatsAppChannelQuery(TenantId)` → `{ Enabled, Mode }` (`Enabled` es `Mode != Disabled`; sin
  fila, `true` y `Shared`). Permiso `quotations.quotation.manage`. Existe aparte del `GET` de
  settings porque los roles son editables (`advisorship.roles.manage`): quien puede enviar no
  necesariamente puede leer Ajustes. `Mode` viaja por la regla BFF: la pantalla de envío lo usa
  para agregar la pista "revisa la plantilla en Configuración" cuando falla una cuenta propia, sin
  pedir `tenancy.settings.read`.

**Gate por capacidad.** `tenancy.*` es núcleo en el diseño de entitlements, así que el
enmascaramiento de permisos no apaga estos endpoints aunque el tenant no tenga cotizaciones. Los
dos handlers de settings, justo después de `QuotationsAuthorization.EnsureAuthorized` y antes de
leer el repositorio y del validador, llaman a
`TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Quotations, ct)`,
que lanza `RequestForbiddenException("tenancy.module_not_enabled")` → 403 con ese código
(`ApiExceptionHandler.cs:135-136`). Es el patrón que el spec de entitlements fija para
`/orders-export-layout`. Quotations.Application ya referencia Tenancy.Application (por
`IMembershipDirectory`), así que no hay referencia nueva. Con `ITenantModules.FindAsync` en `null`
—un tenant simulado por el stub de desarrollo, sin fila en `tenancy.tenants`— el guard **no
lanza**: la prueba del gate necesita un tenant real (ver "Pruebas"). `GetWhatsAppChannel` y el
envío no lo necesitan: `quotations.quotation.manage` ya cae con el enmascaramiento.

**Auditoría.** `IQuotationAuditPublisher.Publish` (`IQuotationAuditPublisher.cs:7-9`) recibe tenant,
sujeto, acción, recurso, resultado y fecha: no lleva campos. Lo que se audita lo dice la acción, una
por clase de cambio, todas por outbox y con el `tenantId` como recurso, como
`quotations.orders_export_layout.updated` (`UpdateOrdersExportLayout.cs:120`):

| Acción | Cuándo |
| --- | --- |
| `quotations.whatsapp_settings.mode_changed` | cambió `Mode` |
| `quotations.whatsapp_settings.api_key_replaced` | el `PUT` trajo `apiKey` (aunque sea la misma) |
| `quotations.whatsapp_settings.updated` | cambió proveedor, número o plantilla, o el `PUT` drenó la llave (`KeyRotated`) |

Un mismo guardado publica una entrada por cada clase que aplique. La auditoría dice **quién hizo
qué clase de cambio y cuándo**; no guarda ni el modo de destino, ni el número, ni la plantilla.
El re-cifrado de `WhatsAppTokenRekeyWorker` no se audita: no hay una persona detrás y no cambia
ningún valor de la configuración (misma key, otra llave); queda en su log.

`WhatsAppSettingsDto`:

`TenantId`, `Mode`, `Provider?`, `FromNumber?`, `TemplateId?`, `Version` (también en `ETag`), y los
que viajan por la regla BFF, con el motivo escrito en el DTO:

| Campo | Por qué viaja |
| --- | --- |
| `ApiKeyConfigured`, `ApiKeyUpdatedAt` | estado "configurada el 7 de octubre" sin exponer la key |
| `ApiKeyReadable` | la key guardada se descifra de verdad (`TryUnprotect`, valor descartado); `null` sin key. Detecta llave ausente **y** bytes que no descifran. La pantalla pide volver a pegarla sin deducirlo de un envío fallido |
| `Modes` (`["Shared","Own","Disabled"]`) | colección fija y completa: el select no conoce el enum |
| `Providers` (`["Zenvia"]`) | ídem para el proveedor |

En `Shared` y `Disabled` el DTO **sí** devuelve el proveedor, el número y la plantilla guardados (y
el estado de la key): son lo que la pantalla muestra al volver a "Cuenta propia".

### Validador (`UpdateWhatsAppSettingsValidator`)

El dominio da el código, el validador da el campo (mapa `errors`, `ApiExceptionHandler.cs:58-65`).
El validador **no ve la fila**, así que sólo valida **formato** y sólo de lo que viene. Lo
**requerido** en `Own` depende de lo guardado y lo decide el handler (paso 5), con el mismo mapa
`errors` y un mensaje por campo. Si el validador exigiera los campos en `Own`, `{ "mode": "Own" }`
—volver a la cuenta propia ya guardada— sería siempre 422.

| Campo | Regla de formato | Cuándo se evalúa |
| --- | --- | --- |
| `Mode` | exactamente `"Shared"`, `"Own"` o `"Disabled"` (ordinal) | siempre |
| `Provider` | exactamente `"Zenvia"` (ordinal, como `OrdersExportColumnKinds`) | si `Mode` es `Own` y viene |
| `ApiKey` | recortada, cumple `^[\x21-\x7E]{1,512}$` (ASCII visible: sin espacios, controles ni caracteres fuera de ASCII) | si `Mode` es `Own` y viene; `""` es inválido, no "borrar" |
| `FromNumber` | sólo dígitos, 10–15 (E.164 sin `+`) | si `Mode` es `Own` y viene |
| `TemplateId` | GUID en formato `D` (la plantilla vigente en `appsettings.json` tiene esa forma) | si `Mode` es `Own` y viene |
| `ExpectedVersion` | `> 0` | siempre |

En `Shared` y `Disabled` los campos de la cuenta propia **se ignoran** (ni se validan ni se
guardan): el frontend no los manda en esos modos, y aceptar que se guarden "para después" era un
comportamiento que sólo existía por la API.

### Infrastructure (`Modules.Quotations.Infrastructure`)

- `Persistence/TenantWhatsAppSettingsRepository.cs` + configuración en `QuotationsDbContext`
  (`ProtectedSecret` opcional con `OwnsOne` en dos columnas de la misma tabla; `Mode` y `Provider`
  con `HasConversion<string>()`).
- `QuotationsUnitOfWork`: traducir `PK_tenant_whatsapp_settings` a `RequestConcurrencyException`
  (412), como `OrdersExportLayoutKey` (`:38`): dos primeros `PUT` con `If-Match: "1"` chocan en la PK.
- `SecretProtection/SecretProtectionOptions.cs` (`string? ActiveKeyId`,
  `Dictionary<string, string?> Keys`) + `SecretProtectionOptionsValidator.cs`, registrados por su
  cuenta (ver "Validación al arrancar"). **Vacío = ausente** en todas partes: en el validador, en
  `ActiveKeyId` del protector y en `HasKey`. El harness fija `Keys:k1 = ""` para tapar los
  user-secrets del developer, así que un `HasKey("k1")` que contara la clave vacía como presente
  diría "legible" sobre una llave que no existe.
- `Whatsapp/AesGcmWhatsAppSecretProtector.cs` (singleton): lee `SecretProtectionOptions`, cifra
  con `ActiveKeyId`, descifra con el `KeyId` de la fila, AAD como arriba. `Protect` sin
  `ActiveKeyId` y `Unprotect` sin la llave pedida lanzan `InvalidOperationException` con un mensaje
  que nombra la clave de configuración (`Quotations:SecretProtection:Keys:<id>`), nunca el valor.
  `TryUnprotect` envuelve lo mismo y devuelve `false` (ver Application).
- `Whatsapp/WhatsAppTokenRekeyWorker.cs`: `BackgroundService` que corre **una sola vez** por
  arranque, con un scope propio como `QuotationExpirationWorker`. Si `ActiveKeyId` es `null` no hace
  nada. Si no, lee las filas con `api_token_key_id <> ActiveKeyId` (son pocas: una por tenant como
  mucho) y, una por una, `TryUnprotect` → `Protect` con la activa → `Reprotect` → `SaveChangesAsync`.
  - Idempotente: en el siguiente arranque esas filas ya no califican.
  - Una fila que no descifra **se salta** y se loguea con el tenant id y el key id, **nunca** un
    valor ni un texto cifrado. Un conflicto de concurrencia (alguien guardó esa fila en el medio, o
    otra réplica la re-cifró) también se salta: el `PUT` o la otra réplica ya la dejó bien o la deja
    el siguiente arranque.
  - Cualquier otra falla (la tabla todavía no existe, la base no responde) se loguea y el worker
    termina; no tumba el host ni reintenta en bucle.
  - Al terminar emite **un** log final sólo con conteos: `rekey finished: n re-encrypted, m skipped`
    (sin tenant ids, sin key ids, sin valores).
  - Expone su terminación: una propiedad `Task Completion` (un `TaskCompletionSource` que se
    completa en un `finally`, haya re-cifrado, saltado o fallado). Las pruebas de integración
    resuelven el worker desde los `IHostedService` del host y esperan esa `Task`; así las
    afirmaciones **negativas** (algo no cambió) no corren contra un worker que todavía no terminó.
  - No audita (ver "Auditoría").
  - `ZenviaSenderSettings` sobreescribe `ToString()` con el token como `***` (ver "Sólo escritura y
    nunca en un log").
- `Whatsapp/ZenviaWhatsAppSender.cs`: deja de leer `IOptions<QuotationsOptions>` y recibe
  `ZenviaSenderSettings(string ApiToken, string FromNumber, string TemplateId, string BaseUrl, ZenviaAccount Account)`,
  con `ZenviaAccount { Qep, Tenant }`:
  - `Tenant` (modo `Own`): un 401/403 sale como
    `QuotationsDomainException("quotation.whatsapp.credentials_rejected")`, y cualquier otro no-2xx
    como `quotation.whatsapp.send_failed`. Los dos mensajes llevan el código de estado y, si el
    cuerpo es un objeto JSON con una propiedad `code` cuyo valor cumple `^[A-Za-z0-9_.-]{1,64}$`,
    ese código (`"Zenvia responded 400 (code: XYZ)."`); si no, sólo el estado. **Nunca el cuerpo
    crudo**: el parseo es tolerante, como `ReadMessageId` (`ZenviaWhatsAppSender.cs:112-126`), y el
    patrón impide que un texto libre del cuerpo se cuele por ese campo. La forma de error de Zenvia
    no está verificada en el código; si no trae `code`, el mensaje queda sólo con el estado, que
    es lo mismo que se pedía en la ronda 1. Con keys que escribe una persona, "credenciales
    rechazadas" es el error más probable y el único accionable desde Ajustes; el código de Zenvia es
    la única pista para el resto (plantilla no aprobada, número que no es de esa cuenta).
  - `Qep` (modo `Shared`): como hoy. Un 401/403 es `send_failed` genérico —el administrador del
    tenant no puede arreglar las credenciales de QEP, así que decirle "revisa la API key" lo
    mandaría a buscar un problema que no tiene— y el mensaje conserva el cuerpo de Zenvia.
- `Whatsapp/ZenviaHttpClient.cs` (singleton): **un** `HttpClient` compartido por el sender global y
  todos los de tenant. Hoy hay uno por proceso (`new HttpClient()` en `AddWhatsAppSender`); armar
  uno por envío agotaría sockets. El token va por request en el header, así que compartir el
  cliente no mezcla credenciales.
- `Whatsapp/WhatsAppChannelResolver.cs` (scoped): `FindAsync` sin tracking → sin fila o `Shared`
  devuelve el `IWhatsAppSender` singleton de hoy; `Own` arma un `ZenviaWhatsAppSender` por envío
  con `Unprotect`, `ZenviaAccount.Tenant` y el `ZenviaHttpClient`; `Disabled`, sin sender. Si
  `Unprotect` falla (llave no configurada, tag inválido) lanza
  `QuotationsDomainException("quotation.whatsapp.settings_unreadable", ..., inner)`. `BaseUrl` y
  `DocumentUrlHours` siguen siendo globales.
- **Sin caché** de la configuración descifrada: es una lectura por PK por envío, y el envío ya paga
  `qcode-pdf`, R2 y Zenvia. Una caché obligaría a invalidar en el `PUT` y entre réplicas.
- `appsettings.example.json` documenta la sección vacía (lo exige `ConfigurationExampleTests`).

### Envío (`SendQuotationHandler`)

Cambios sobre `SendQuotation.cs`:

1. Después de resolver la asesora, etapa nueva `QuotationSendStage.Channel` (al final del enum; no
   se persiste como número) → `channelResolver.ResolveAsync`. Su falla es un
   `QuotationsDomainException` (`settings_unreadable`), así que el `catch` lo relanza tal cual
   (`SendQuotation.cs:154-157`) y no lo envuelve en `quotation.send.failed`: la pantalla recibe un
   código propio que manda a Configuración. El historial anota `SendFailed` con el texto de
   `Channel` y la cotización sigue como estaba, igual que cualquier otra etapa.
2. `Pdf` como hoy (snapshots y documento).
3. Con `Disabled`: se saltan `Publish` (la copia pública sólo existe para Meta), `ResolveRecipient`
   y `WhatsApp`; `Recipient` sigue validando al cliente con `QuotationCustomerEligibility.Ensure`
   (es regla del envío, no del canal). El `Recipient` del cuerpo se ignora.
4. `Persistence` como hoy; el resumen del historial usa el texto "sin WhatsApp" si fue `Disabled`.
5. `QuotationChangeSummary.SendFailed` recibe si el WhatsApp se saltó. Hoy la razón de
   `Persistence` es *"el mensaje salió pero no pudimos registrar el envío. Revisa con el cliente
   antes de reintentar."* (`QuotationChangeSummary.cs:79-80`), que con `Disabled` es falso. Con
   `Disabled` pasa a *"no pudimos registrar el envío. No se mandó ningún WhatsApp, así que puedes
   reintentar."* La razón de `Channel` es *"no pudimos leer la configuración de WhatsApp de la
   empresa. Pide a un administrador que la revise en Configuración."*
6. El handler devuelve `SendQuotationResult(QuotationDto Quotation, QuotationWhatsAppOutcome WhatsApp)`
   (`Accepted | Disabled`), y el endpoint arma `composer.ComposeAsync(...) with { WhatsAppOutcome = ... }`.
   `QuotationResponse` suma `string? WhatsAppOutcome = null` al final: aditivo, un frontend
   desplegado antes no se entera (mismo criterio que `AdvisorName`).

### Endpoints

```
GET  /api/v1/tenants/{tenantId}/quotations/whatsapp-settings   tenancy.settings.read    + capacidad quotations
PUT  /api/v1/tenants/{tenantId}/quotations/whatsapp-settings   tenancy.settings.update  + capacidad quotations (If-Match)
GET  /api/v1/tenants/{tenantId}/quotations/whatsapp-channel    quotations.quotation.manage
```

Los dos de settings van en una clase nueva, `WhatsAppSettingsEndpoints`, con su propio
`MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/whatsapp-settings").WithTags("Tenant settings")`,
igual que `OrdersExportLayoutEndpoints.cs:21-23`, y se mapea donde se mapea esa. Colgarlos del grupo
de `QuotationEndpoints` (`.WithTags("Quotations")`, `QuotationEndpoints.cs:14-15`) y sumarles
`Tenant settings` los dejaría con dos tags en OpenAPI. `whatsapp-channel` sí va en el grupo
`/quotations` de `QuotationEndpoints`, con su tag: es parte del envío. Ninguno choca con
`/{quotationId:guid}` por la restricción de guid (mismo caso que `/export`). Sin permiso nuevo, así
que no hay política nueva en `AddAuthorization`.

```jsonc
// GET sin fila
{ "tenantId": "6f1c...", "mode": "Shared",
  "provider": null, "apiKeyConfigured": false, "apiKeyUpdatedAt": null, "apiKeyReadable": null,
  "fromNumber": null, "templateId": null,
  "modes": ["Shared", "Own", "Disabled"], "providers": ["Zenvia"], "version": 1 }

// PUT, primer guardado con cuenta propia, If-Match: "1"
{ "mode": "Own", "provider": "Zenvia", "apiKey": "<token de Zenvia>",
  "fromNumber": "573001234567", "templateId": "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f" }

// 200, ETag: "2"
{ "tenantId": "6f1c...", "mode": "Own",
  "provider": "Zenvia", "apiKeyConfigured": true, "apiKeyUpdatedAt": "2026-10-07T22:14:03Z",
  "apiKeyReadable": true, "fromNumber": "573001234567",
  "templateId": "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f",
  "modes": ["Shared", "Own", "Disabled"], "providers": ["Zenvia"], "version": 2 }

// GET whatsapp-channel
{ "enabled": true, "mode": "Own" }
```

Volver a la cuenta de QEP o desactivar, sin tocar lo guardado: `{ "mode": "Shared" }` o
`{ "mode": "Disabled" }` (con campos propios en el cuerpo, se ignoran). Volver a la cuenta propia ya
configurada y legible: `{ "mode": "Own" }`. Un 422 trae `code: "validation.failed"` y
`errors: { "ApiKey": [...], "TemplateId": [...] }`, con un mensaje por campo que falte o esté mal.

### Códigos de error

| Status | Código | Cuándo |
| --- | --- | --- |
| 403 | `authorization.denied` | sin permiso u otro tenant en la ruta |
| 403 | `tenancy.module_not_enabled` | settings sin la capacidad `quotations` (sólo con tenant real) |
| 412 | `concurrency.conflict` | versión vieja o dos primeros guardados simultáneos |
| 428 | `precondition.if_match_required` | `PUT` sin `If-Match` válido |
| 422 | `validation.failed` | reglas del validador y del handler, con `errors` |
| 422 | `quotation.whatsapp_settings.incomplete` | invariante del dominio; inalcanzable por la API con el validador delante |
| 422 | `quotation.whatsapp.credentials_rejected` | **nuevo en el envío**, sólo en `Own`: Zenvia respondió 401/403 |
| 422 | `quotation.whatsapp.send_failed` | Zenvia respondió otro no-2xx; en `Shared`, también 401/403 |
| 422 | `quotation.whatsapp.settings_unreadable` | **nuevo**, etapa `Channel`: la key guardada no se puede descifrar |

### Validación al arrancar

`QuotationsOptionsValidator` **sigue exigiendo** las tres claves globales en `Production`: `Shared`
es una opción del producto (la predeterminada), y sin esas claves un tenant en `Shared` caería en
`LogWhatsAppSender`, que es exactamente el envío fantasma que el validador evita.

`SecretProtectionOptionsValidator`, aparte:

- **En `Production`**: `ActiveKeyId` presente y presente en `Keys`; todo id cumple
  `^[a-z0-9]{1,32}$`; **toda** llave declarada decodifica a 32 bytes (falla rápido por una llave mal
  pegada en la variable del pipeline).
- **Fuera de `Production`**: la sección puede faltar (el `GET` funciona, `WhatsAppTokenRekeyWorker`
  no hace nada y un `PUT` en `Own` con key responde 500 con el mensaje que nombra la clave). Si
  `ActiveKeyId` está, su llave tiene que existir y decodificar a 32 bytes; las **demás** llaves no
  se validan al arrancar (fallan al usarse, en `Channel`). Un valor vacío cuenta como ausente.

El porqué de la diferencia son las pruebas de integración: levantan el host completo en
`Development`, donde `CreateBuilder` carga los user-secrets de quien las corre, y el harness sólo
puede **pisar claves por nombre** con `UseSetting` (`QuotationsApiHarness.cs:836-878`); no puede
borrar un `Keys:<id>` que no conoce. Con la regla de producción en todos los ambientes, una llave
mal pegada en los user-secrets de un developer tumbaría el arranque de **todas** las pruebas del
proyecto. El harness fija `ActiveKeyId = "test"`, `Keys:test` con 32 bytes fijos de prueba y
`Keys:k1 = ""` (el id que este spec sugiere para local), igual que ya fija `Quotations:WhatsApp:*`
(`QuotationsApiHarness.cs:876-878`).

## Despliegue y operación

Orden obligatorio:

1. **Variable secreta** `QUOTATIONS_SECRET_PROTECTION_KEY_K1` en el grupo `Backend-prod`, generada
   como dice "Llave de producción: custodia", y su copia en la bóveda.
2. **Backend**: un mismo merge a `main` con el código, la migración, la línea del token en
   `k8s/prod-secret.yaml` y `Quotations__SecretProtection__ActiveKeyId: "k1"` en
   `k8s/prod-configMap.yaml`. El pipeline aplica ConfigMap, Secret y Deployment en ese orden. Sin la
   variable del paso 1, la llave llega vacía o con el token sin reemplazar (lo decide la plantilla
   `k8s/deploy.yml@k8sTemplates`, que no está en este repo); en los dos casos
   `SecretProtectionOptionsValidator` falla con `ValidateOnStart` y el pod nuevo entra en
   crash-loop.
3. **Frontend**, después del backend. Si sale antes, `GET whatsapp-channel` responde 404: el flujo
   de envío lo trata como canal desconocido y envía como hoy, y la sección de Configuración muestra
   su estado de error con "Reintentar" hasta que llegue el backend.

- Verificación en el clúster: `kubectl describe secret` para ver que la clave existe; nunca
  `-o yaml`.
- **Rollback del backend** a una imagen anterior: la tabla queda y la imagen vieja la ignora, así
  que **todos** los tenants vuelven a la cuenta de QEP mientras dure, incluidos los que eligieron
  `Disabled`. Antes de revertir hay que avisarles a esos tenants, o revertir sólo con la tabla vacía
  de filas `Disabled`/`Own`.
- README: sección nueva "WhatsApp por tenant" (modos, resolución, custodia de la llave, rotación en
  dos fases con el re-cifrado al arrancar y el drenaje, SQL de conteo por `key_id`, orden de
  despliegue), y corregir la tabla de "Plantilla de
  WhatsApp": `documentUrl` ya no sale de `IQuotationFileLookup.CreateDownloadUrlAsync` sino de la
  copia pública de `IQuotationPdfStorage.PublishAsync` (`SendQuotation.cs:93-98`).

## Frontend

### Sección "WhatsApp" en Configuración

Una `SettingsSection` nueva en `TenantSettingsPage`, entre el formulario y "Excel de pedidos", con
**guardado y versión propios** (no es parte de `TenantSettingsForm`, que manda su `PUT /settings`).
Título "WhatsApp", descripción *"Cómo le llegan las cotizaciones a tus clientes."*

Contenido, de arriba abajo:

1. **Aviso del canal efectivo** (`role="status"`), según `mode` guardado:
   - `Shared`: *"Tus cotizaciones salen por la cuenta de WhatsApp de QEP."*
   - `Own` con la key legible: *"Tus cotizaciones salen por la cuenta de Zenvia de tu empresa."*
   - `Own` con `apiKeyReadable: false`: *"Tus cotizaciones deberían salir por la cuenta de Zenvia
     de tu empresa, pero no están saliendo: vuelve a pegar la API key."*
   - `Disabled`: *"El envío por WhatsApp está desactivado: al enviar, la cotización se marca como
     enviada y el PDF lo compartes tú."*
2. **Switch** "Enviar cotizaciones por WhatsApp" (`components/ui/switch.tsx`): encendido en
   `Shared` y `Own`, apagado en `Disabled`. Encenderlo desde `Disabled` propone **"Cuenta propia"**
   si están guardados proveedor, número y plantilla y la key es legible (`apiKeyReadable: true`); si
   no, **"Cuenta de QEP"**. Así quien desactivó estando en su cuenta propia vuelve a ella con un clic,
   y nadie aterriza en un `Own` que el `PUT` rechazaría.
3. Con el switch encendido, **"Cuenta desde la que se envía"**: `Select` (no hay radio group en
   `components/ui/`) con *"Cuenta de QEP (predeterminada)"* y *"Cuenta propia"*.
4. Con "Cuenta propia", los campos de la cuenta:
   - **Proveedor**: `Select` con `providers` del DTO; hoy sólo "Zenvia".
   - **API key**: `type="password"`, `autoComplete="off"`. Sin key guardada, vacío y requerido. Con
     key guardada y legible, en vez del input: *"API key configurada · actualizada el 7 de oct. de
     2026"* y **"Reemplazar"**, que muestra el input vacío y "Cancelar"; sin reemplazo el `PUT` no
     lleva `apiKey`. Con `apiKeyReadable: false`: *"La API key guardada ya no se puede leer. Vuelve
     a pegarla para seguir enviando con tu cuenta."* y el input visible y requerido. Después de
     guardar el input se vacía siempre.
   - **Número emisor**: placeholder `573001234567`; ayuda *"El número de WhatsApp registrado en
     Zenvia, con indicativo de país y sin '+'."*
   - **Id de la plantilla**: ayuda *"La plantilla tiene que estar aprobada por Meta y usar
     exactamente estas variables: `fullname`, `order_number`, `total`, `valid_until`, y el PDF como
     documento del encabezado. Si usa otras, el envío falla sin que lo veamos al guardar."*
5. Con el switch apagado o con "Cuenta de QEP", los campos de la cuenta propia se esconden pero
   **no se borran**: el `PUT` lleva sólo `mode`, y el backend conserva lo guardado. Debajo del
   select, si hay cuenta propia guardada: *"Tu cuenta propia queda guardada: puedes volver a ella
   cuando quieras."*
6. Barra de guardado sólo con cambios, como `TenantSettingsForm`. Sin `tenancy.settings.update`,
   todo en lectura.

**React Hook Form + zod** (`types/whatsapp-settings.schema.ts`), como `edit-member-dialog.tsx`: el
422 se traduce con `whatsAppSettingsFieldErrors` (forma de `taxRateFieldErrors`,
`catalog.api.ts:404-418`; `ApiKey → apiKey`, etc.) y se aplica con `setError`. Estados: cargando,
403 `authorization.denied` (la tarjeta no se muestra), 403 `tenancy.module_not_enabled` (la
tarjeta no se muestra; no debería pasar, porque sin la capacidad la sección no se monta), error con
"Reintentar", guardando, 412 (`CONFLICT_MESSAGE` y recarga), 428 (recarga). Sin la capacidad
`quotations` (`useTenantModules`) la sección no se monta.

### Envío

- `services/quotes.api.ts`: `fetchWhatsAppChannel` y `whatsAppChannelQueryKey(tenantId)`; mensajes
  nuevos en `QUOTE_CODE_MESSAGES` (`quotes.api.ts:490-567`, junto a los de WhatsApp en `:557-560`),
  que es de donde `describeQuoteFailure` saca el texto que muestra `QuoteSendFailureDialog`:
  - `quotation.whatsapp.credentials_rejected`: *"La cuenta de WhatsApp de tu empresa rechazó las
    credenciales. Pide a un administrador que revise la API key en Configuración."*
  - `quotation.whatsapp.settings_unreadable`: *"No pudimos leer la configuración de WhatsApp de tu
    empresa. Pide a un administrador que la revise en Configuración."*
  - `quotation.whatsapp.send_failed` (existente, `:559-560`) pasa a tuteo: *"No pudimos enviar la
    cotización por WhatsApp. Intenta de nuevo en un momento."* Con canal `Own`, el diálogo le suma
    *"Si sigue fallando, pide a un administrador que revise en Configuración el número emisor y la
    plantilla de la cuenta de Zenvia de tu empresa."* Con `Shared` o canal desconocido, no: la
    plantilla de QEP no la arregla nadie del tenant.
- `types/quote.ts`: `Quote` suma `whatsAppOutcome?: 'Accepted' | 'Disabled' | null`.
- `hooks/use-quote-send-flow.tsx`: el canal se pide con `queryClient.fetchQuery` **dentro de
  `start()`**, con `whatsAppChannelQueryKey` y `staleTime` corto, siguiendo el patrón con el que ese
  mismo `start()` ya resuelve la cotización (`use-quote-send-flow.tsx:84-99`). Una query de hook
  montada aparte correría en paralelo con el clic y, si no hubiera llegado, el flujo decidiría sin
  saber el canal. Si el `fetchQuery` falla, el canal es **desconocido** y el flujo sigue el de hoy;
  no es un error de pantalla. Con `enabled: false` abre `QuoteSendWithoutWhatsAppDialog` (con su
  variante de reenvío) en vez de preguntar destinatario. El `mode` leído se guarda con el destino
  para la pista de `send_failed`. Al volver, el aviso sale de `whatsAppOutcome` de la respuesta
  (`toast.warning` con "Descargar PDF" para `Disabled`, en envío y reenvío).
- Textos nuevos con tuteo. Los existentes con voseo en lo que se toca —`quotes.api.ts:558`
  ("Agregalo"), `:560` ("Intentá"), `:566` ("Miralo", "reintentá")— se corrigen al tocar el archivo.

### Archivos del frontend

- Nuevos en `features/tenant-settings/`: `types/whatsapp-settings.ts`,
  `types/whatsapp-settings.schema.ts`, `services/whatsapp-settings.api.ts`,
  `hooks/use-whatsapp-settings.ts`, `components/whatsapp-settings-section.tsx` (y sus pruebas).
- Tocados: `pages/tenant-settings-page.tsx` (vista pura: recibe la sección por props) y
  `routes/_authenticated/settings/index.tsx` (container: llama al hook y a `useTenantModules`).
- En `features/quotes/`: `services/quotes.api.ts`, `types/quote.ts`, `hooks/use-quote-send-flow.tsx`,
  `components/quote-send-failure-dialog.tsx` (muestra la pista de cuenta propia que le pasa el
  flujo, debajo del mensaje del código) y el nuevo `components/quote-send-without-whatsapp-dialog.tsx`.
  No hay hook propio del canal: es un `fetchQuery` dentro de `start()`. Todo con una sola feature
  consumidora, así que nada va a `src/components/` (`SDD-ADR-07`).

## Errores y casos borde

- **Tenant nuevo**: nace sin fila, o sea `Shared`, como hoy.
- **Guardar `Shared` sin fila**: no-op. `Configure` sobre `CreateEmpty` (que ya es `Shared`) no
  informa cambios, así que no se crea fila, no sube versión, no se audita y la respuesta es 200 con
  `version: 1`. Es el mismo canal que sin fila, y nada distingue "nunca tocado" de "eligió la cuenta
  de QEP".
- **Primer guardado en `Disabled`**: crea la fila `Disabled`. Es la forma de optar por no mandar
  WhatsApp.
- **Volver de `Disabled`**: a `Shared` con `{ "mode": "Shared" }`; a `Own` con `{ "mode": "Own" }` si
  la cuenta propia está completa y su key es legible, o con los campos que falten.
- **De `Own` a `Shared` y de vuelta**: las credenciales propias se conservan cifradas; volver a `Own`
  no pide nada si la key sigue legible.
- **`Shared` o `Disabled` con campos de cuenta propia en el cuerpo**: se ignoran; no se validan ni
  se guardan. La cuenta propia sólo se escribe con un `PUT` en `Own`.
- **Misma key pegada otra vez**: cuenta como reemplazo (nonce nuevo, `apiKeyUpdatedAt` nuevo,
  versión nueva y auditoría `api_key_replaced`).
- **Llave de cifrado retirada o cambiada, o bytes que no descifran**: el `GET` sigue funcionando
  (`TryUnprotect` no lanza) y dice `apiKeyReadable: false`; en `Own` el envío falla en `Channel` con
  `settings_unreadable`; el administrador vuelve a pegar la key y queda cifrada con la activa. En
  `Shared` o `Disabled` no pasa nada hasta que se vuelva a `Own`, que exige pegarla. Un `PUT` a
  cualquier modo sobre esa fila se guarda: el drenaje se salta, no da 500.
- **Llave activa nueva con filas en la vieja**: `WhatsAppTokenRekeyWorker` las re-cifra al arrancar,
  y cualquier `PUT` posterior que encuentre una rezagada la drena.
- **Fila copiada entre tenants por SQL**: el AAD no coincide, falla en `Channel`.
- **Key con espacios al pegarla**: se recorta; espacios internos, caracteres de control o fuera de
  ASCII visible son 422 (`^[\x21-\x7E]{1,512}$`).
- **Cuenta de QEP rechazada por Zenvia (401/403 en `Shared`)**: `send_failed` genérico, no
  `credentials_rejected`: no es algo que el administrador del tenant pueda arreglar.
- **Plantilla con otras variables**: Zenvia acepta o rechaza según su validación; si la rechaza es
  `quotation.whatsapp.send_failed` en la etapa `WhatsApp`; si la acepta y Meta la descarta después,
  no lo vemos (igual que hoy). Por eso el texto de ayuda y la "Prueba de envío" pendiente.
- **Desactivan mientras una asesora tiene el diálogo abierto**: la respuesta trae `Disabled` y el
  aviso lo dice; no se mandó nada.
- **Reenvío con `Disabled`**: sube `SentAt`, historial *"Marcada como reenviada sin WhatsApp..."*,
  diálogo y aviso en su variante de reenvío.
- **Destinatario `Billing` sin teléfono con `Disabled`**: no falla; el destinatario se ignora.
- **Falla al registrar el envío con `Disabled`**: el historial no dice "el mensaje salió", dice que
  no se mandó ningún WhatsApp y que se puede reintentar.
- **No se puede leer el canal antes de enviar**: el flujo sigue como hoy y la respuesta decide.

## Pruebas (TDD, RED antes que GREEN)

Unitarias (`Modules.Quotations.UnitTests`):

- `TenantWhatsAppSettings`: `Own` sin alguna de las cuatro piezas lanza `incomplete`; pasar a
  `Shared` y a `Disabled` conserva token, número y plantilla; volver a `Own` sin datos nuevos con
  todo guardado no lanza; `Configure` idéntico no informa cambios y no sube versión; `newToken`
  nulo conserva el actual; un `newToken` informa `ApiKeyReplaced` y mueve `ApiTokenUpdatedAt`;
  cambiar de modo informa `ModeChanged`; `rekeyedToken` informa `KeyRotated` y no mueve
  `ApiTokenUpdatedAt`; un `Configure` con modo, detalles y `rekeyedToken` a la vez sube la versión
  **exactamente una vez**; `Reprotect` sube versión una vez y no mueve `ApiTokenUpdatedAt`.
- `AesGcmWhatsAppSecretProtector`: ida y vuelta; dos cifrados del mismo texto difieren (nonce);
  descifrar con otro `tenantId` lanza; el AAD es exactamente el UTF-8 de
  `"quotations.whatsapp.api_token:" + tenantId.ToString("D")` (un texto cifrado a mano con ese AAD
  descifra); `KeyId` desconocido lanza con un mensaje que nombra el id y no contiene ninguna llave;
  después de cambiar `ActiveKeyId` lo viejo se lee con su llave y lo nuevo sale con la nueva;
  `HasKey` (con una llave en `""` da `false`); `ActiveKeyId` en `""` es `null` y `Protect` lanza;
  `TryUnprotect` da `false` sin lanzar con llave ausente, con un byte del texto cifrado alterado y con
  otro `tenantId`; el texto cifrado no contiene los bytes UTF-8 del token.
- `SecretProtectionOptionsValidator`: en `Production` faltan `ActiveKeyId` o su llave → falla; id
  que no cumple el patrón → falla; cualquier llave que no decodifica a 32 bytes → falla; fuera de
  `Production`, una llave no activa mal formada **no** falla y la activa mal formada sí; ningún
  mensaje contiene el valor de una llave.
- `QuotationsOptionsValidatorTests`: las claves globales siguen exigidas en `Production`.
- `UpdateWhatsAppSettingsValidator`: cada regla de formato de la tabla; `{ "mode": "Own" }` sin
  ningún otro campo **pasa** el validador; en `Shared` y `Disabled` un `templateId` o `apiKey` mal
  formado no da error (se ignora); una key con un espacio interno, un carácter de control o una
  `ñ` da error; con una key inválida conocida, ningún mensaje de error la contiene.
- `UpdateWhatsAppSettingsCommand.ToString()` no contiene la key.
- `ZenviaSenderSettings.ToString()` no contiene el token.
- `UpdateWhatsAppSettingsRequest.ToString()` no contiene la key (en la prueba de la capa Api que
  corresponda; la misma afirmación que la del comando).
- `UpdateWhatsAppSettingsHandler`: sin permiso y cuerpo inválido da 403, no 422; sin capacidad
  `quotations` da 403 `tenancy.module_not_enabled` antes de leer el repositorio y del validador; con
  `FindAsync` nulo no lanza; `Own` sin fila y cuerpo vacío da **una** `ValidationException` con
  errores en `Provider`, `ApiKey`, `FromNumber` y `TemplateId`; `{ "mode": "Own" }` con todo
  guardado y legible pasa; `Own` con key ilegible y sin `apiKey` da `ValidationException` en
  `ApiKey`; en `Disabled` con campos propios en el cuerpo no los guarda; sin `apiKey` conserva el
  `ProtectedSecret` (misma referencia de bytes); con key en llave no activa y legible la re-cifra
  con la activa; **`PUT` a `Disabled` sobre una fila en llave no activa con bytes que no descifran
  guarda el modo, no re-cifra y no lanza**; un guardado con cambio de modo y drenaje sube la versión
  una vez; no-op sin auditoría ni commit (incluido `Shared` sin fila, que no llama a `Add`);
  versión vieja da 412; cada clase de cambio publica su acción (`mode_changed`, `api_key_replaced`,
  `updated`) y un guardado con dos clases publica dos.
- `GetWhatsAppSettingsHandler`: sin fila da `mode: Shared`, versión 1; con `KeyId` no configurado
  da `apiKeyReadable: false`; con la llave configurada pero bytes alterados da
  `apiKeyReadable: false`; sin capacidad, 403 `tenancy.module_not_enabled`; ninguna propiedad del
  DTO es el token (prueba por reflexión sobre los nombres de propiedades).
- `GetWhatsAppChannelHandler`: sin fila da `enabled: true, mode: Shared`; `Shared`/`Own` dan
  `enabled: true` con su modo; `Disabled` da `false`.
- `WhatsAppChannelResolver`: sin fila y `Shared` devuelven el sender global; `Own` arma un Zenvia que
  manda `X-API-TOKEN`, `from` y `templateId` del tenant (con `HttpMessageHandler` falso);
  `Disabled` da sin sender; un `Unprotect` que falla da `settings_unreadable`.
- `SendQuotationHandler`: `Disabled` no llama a `PublishAsync` ni al sender, deja `Sent`, historial
  "sin WhatsApp" y resultado `Disabled`; reenvío con `Disabled` da `Resent` con su texto;
  `Shared`/`Own` dan `Accepted`; una falla del resolver se relanza como `settings_unreadable` (no
  como `quotation.send.failed`), anota `SendFailed` con el texto de `Channel` y deja la cotización
  como estaba.
- `QuotationChangeSummaryTests`: textos de envío y reenvío sin WhatsApp; razón de `Channel`; razón
  de `Persistence` con y sin WhatsApp saltado. Las existentes, con edición mecánica si
  `SendFailed(stage)` suma el parámetro (pasan `false`), mismas afirmaciones (criterio 1).
- `ZenviaWhatsAppSenderTests`: las existentes, adaptadas a `ZenviaSenderSettings` (edición mecánica,
  mismas afirmaciones); con `ZenviaAccount.Tenant`, 401 y 403 dan `credentials_rejected` y 500 da
  `send_failed`, y ningún mensaje contiene un cuerpo centinela que devuelve el handler falso; con un
  cuerpo `{"code":"XYZ","message":"<centinela>"}` el mensaje lleva `code: XYZ` y no el centinela;
  con un `code` que no cumple el patrón (espacios, más de 64) el mensaje queda sólo con el estado;
  con `ZenviaAccount.Qep`, 401 da `send_failed`.
- `SendQuotationHandlerTests`: las existentes con edición mecánica (el handler recibe un
  `IWhatsAppChannelResolver` de prueba que devuelve el `RecordingWhatsAppSender` o el
  `FailingWhatsAppSender` de hoy), mismas afirmaciones.

Integración (`Modules.Quotations.IntegrationTests`):

- **Dobles sin tocar las pruebas existentes.** Las de hoy siguen con `LogWhatsAppSender` porque el
  harness vacía `Quotations:WhatsApp:*` (`QuotationsApiHarness.cs:876-878`). Las nuevas reemplazan
  piezas con extensiones al estilo de `WithExportProcessors` (`QuotationsApiHarness.cs:653-662`),
  que usan `WithWebHostBuilder` + `RemoveAll<T>()`: `WithWhatsAppSender(sender)` registra un
  `RecordingWhatsAppSender` propio del proyecto de integración (el de `QuotationsTestDoubles.cs:33`
  es `internal` de las unitarias) y `WithZenviaHandler(handler)` reemplaza el `ZenviaHttpClient` por
  uno con `HttpMessageHandler` que captura y responde lo que la prueba pida. Ninguna prueba
  existente se edita.
- Settings: `GET` sin fila; `PUT` primer guardado `Own`; `GET` después trae `apiKeyConfigured: true`
  y **el cuerpo crudo de la respuesta no contiene la key** (también en la respuesta del `PUT`).
- La columna `api_token_ciphertext` leída por SQL no contiene la key en UTF-8.
- `PUT` sin `apiKey` conserva el texto cifrado; `Own` → `Shared` → `Own` sin reenviar nada deja el
  mismo texto cifrado; `Disabled` → `Shared` vuelve a enviar por el sender global; sin `If-Match`
  428; versión vieja 412; dos primeros guardados simultáneos 412 (traducción de la PK).
- `{ "mode": "Own" }` con todo guardado y legible: 200. `{ "mode": "Shared" }` sin fila: 200 con
  `version: 1` y sin fila en la tabla. `PUT` a `Disabled` sobre una fila con bytes alterados por SQL:
  200, no 500, y el `GET` dice `apiKeyReadable: false`.
- 422 con `errors` por campo: `{ "mode": "Own" }` sin fila trae los cuatro campos.
- **La key no aparece en ningún camino.** Con un `ILoggerProvider` de captura agregado en el
  `ConfigureServices` del `WithWebHostBuilder` y una key centinela:
  - `PUT` en `Own` que da 422 (key centinela válida, `templateId` inválido);
  - `PUT` en `Own` que da 500 (key centinela con `ActiveKeyId` vaciada por `UseSetting`);
  - envío en `Own` donde el handler falso de Zenvia responde 401 con un cuerpo centinela;

  y después de cada uno: ni la key ni el cuerpo centinela aparecen en los logs capturados, en el
  `ProblemDetails` de la respuesta, ni en ninguna fila de `platform.request_failures` (`message` y
  `detail`). La traza de OTel no se lee: `RecordException` registra el mismo mensaje y la misma
  cadena de excepciones que estos tres caminos.
- 403 sin `tenancy.settings.update` (pedido por `X-Permissions`), 403 con otro tenant en la ruta.
- **Gate por capacidad con tenant real**: el stub con un tenant inventado no ejercita el guard
  (`FindAsync` da `null`), así que la prueba registra un tenant con `RegisterTenantAsync`
  (`QuotationsApiHarness.cs:136`), borra su fila `quotations` de `tenancy.tenant_modules` y espera
  403 `tenancy.module_not_enabled` en `GET` y `PUT` de settings.
- `GET whatsapp-channel`: 200 con `{ "enabled": true, "mode": "Shared" }` sin fila y
  `{ "enabled": false, "mode": "Disabled" }` con `Disabled` (forma exacta del cuerpo); 403 sin
  `quotations.quotation.manage`; 403 con otro tenant en la ruta.
- Envío con `Disabled`: 200, `status: "Sent"`, `whatsAppOutcome: "Disabled"`, el sender de prueba
  sin llamadas. **Reenvío** con `Disabled`: 200, `whatsAppOutcome: "Disabled"`, historial `Resent`
  con el texto sin WhatsApp. Con `Own`: el request capturado lleva el token y el número del tenant.
  Con `Shared` explícito: el sender de prueba recibe la llamada.
- `WhatsAppTokenRekeyWorker`, contra la base real del harness: la prueba guarda filas con
  `ActiveKeyId = "old"` y después arranca un host nuevo (`WithWebHostBuilder` + `UseSetting`) con
  `Keys:old` declarada y `test` activa. Cada arranque espera `WhatsAppTokenRekeyWorker.Completion`
  antes de afirmar. Después del arranque: las filas quedan con `api_token_key_id = 'test'` y la key
  se sigue leyendo; las que ya estaban en la activa no cambian de versión; una fila con bytes
  alterados por SQL se salta y el resto sigue; un tercer arranque no cambia nada; los logs
  capturados tienen el tenant id y el key id y no contienen el token ni el texto cifrado, y el log
  final dice `rekey finished: n re-encrypted, m skipped` con los conteos esperados. Las afirmaciones
  **negativas** (el tercer arranque no cambia nada, las filas en la activa conservan su versión, la
  fila dañada se salta) van **siempre** después de esperar `Completion`: un sondeo no distingue "no
  cambió" de "todavía no cambió". Las positivas pueden usar además `WaitUntilAsync`
  (`SessionRevocationTests.cs:414`).
- Las pruebas de envío existentes, sin fila, siguen verdes sin cambios (criterio 1).
- `ArchitectureTests` y `ConfigurationExampleTests` verdes con la sección nueva.

Frontend (Vitest + Testing Library):

- `whatsapp-settings.api.test.ts`: el `PUT` sin reemplazo no lleva `apiKey`; con `Shared` o
  `Disabled` lleva sólo `mode`; `If-Match` con la versión; 422 a campos; 412/428 piden recarga;
  `tenancy.module_not_enabled` se reconoce.
- `whatsapp-settings-section.test.tsx`: los avisos de canal (`Shared`, `Own` legible, `Own` con
  `apiKeyReadable: false` con su texto exacto, `Disabled`); switch apagado esconde campos; "Cuenta de
  QEP" esconde los campos propios y "Cuenta propia" los muestra con lo guardado; encender el switch
  desde `Disabled` propone "Cuenta propia" con proveedor, número, plantilla y key legible guardados,
  y "Cuenta de QEP" si falta alguno o la key no es legible; key configurada muestra estado y
  "Reemplazar"; `apiKeyReadable: false` exige el input; `Own` sin key marca el error; el input queda
  vacío después de guardar; modo lectura sin `tenancy.settings.update`.
- `use-quote-send-flow.test.tsx`: el canal se pide con `fetchQuery` al llamar a `start()` y la
  decisión espera su respuesta; canal desactivado abre la confirmación y no pregunta destinatario,
  en envío y en reenvío; un `fetchQuery` que falla sigue el flujo normal; `whatsAppOutcome:
  "Disabled"` da `toast.warning` con su texto exacto, en envío y en reenvío; `Accepted` conserva los
  textos de hoy; `send_failed` con canal `Own` muestra la pista de Configuración y con `Shared` o
  canal desconocido no.
- `quotes.api.test.ts` / `quote-send-failure-dialog.test.tsx`: los mensajes de
  `credentials_rejected` y `settings_unreadable`, y el de `send_failed` en tuteo.
- `tenant-settings-page.test.tsx`: la sección aparece con la capacidad y no aparece sin ella.

## Decisiones tomadas sin el owner

1. **AES-256-GCM con llave fuera de la base**, no Data Protection (ver "Manejo del secreto").
2. **La key no se devuelve ni en sus últimos 4 caracteres**; sí `apiKeyUpdatedAt` y
   `apiKeyReadable`.
3. **"Número emisor" se suma al formulario** (Zenvia lo exige como `from`; el brief no lo pedía).
4. **[Orquestador] El número emisor propio no se trata como secreto** y lo lee quien tenga
   `tenancy.settings.read`. Contradice el criterio de `k8s/prod-secret.yaml:36-40`, que guarda el
   número **global** en el Secret para que no lo lea cualquiera con `get` sobre ConfigMaps; pero
   ese razonamiento protege el número de QEP frente a operadores del clúster, y acá se trata del
   número **propio** del tenant, que ya ven sus clientes en cada WhatsApp, leído por los
   administradores de ese mismo tenant. Queda en DECISIÓN-PENDIENTE para que el owner lo confirme.
5. **Formatos**: número sólo dígitos 10–15; plantilla con forma de GUID, como la vigente.
6. **Sin `updated_by`** en la tabla (ver "Modelo de datos").
7. **[Orquestador] Tres modos (`Shared`, `Own`, `Disabled`)** en vez de un booleano `enabled`, para
   que `Disabled` no sea de una sola vía. La pantalla muestra un switch "Enviar cotizaciones por
   WhatsApp" y, encendido, la elección entre "Cuenta de QEP (predeterminada)" y "Cuenta propia".
8. **[Orquestador] Pasar a `Shared` o a `Disabled` conserva las credenciales propias**, para que
   volver a `Own` no obligue a reescribirlas. Lo mismo vale para proveedor, número y plantilla: en
   el `PUT`, un campo propio ausente conserva lo guardado.
9. *Retirada en la ronda 2* (reemplazada por la 27). El número se conserva para no renumerar.
10. **Con `Disabled`**: PDF y snapshots sí, copia pública no, destinatario del cuerpo ignorado.
11. **`QuotationResponse.WhatsAppOutcome` aditivo** en vez de cambiar la forma de la respuesta.
12. **Endpoint `whatsapp-channel` aparte**, con el permiso de enviar, porque los roles son editables.
    Si su consulta falla, la pantalla sigue el flujo normal y deja que la respuesta del `send` decida.
13. **Error nuevo `quotation.whatsapp.credentials_rejected` sólo en `Own`**; en `Shared` un 401/403
    es `send_failed` genérico.
14. **Los mensajes de error del sender propio llevan el código de estado y, si lo hay, el código de
    error de Zenvia parseado y acotado por patrón, nunca el cuerpo crudo**; el de la cuenta de QEP
    conserva el cuerpo de Zenvia, porque es la única pista para diagnosticar la plantilla de QEP y
    su token no lo escribe un usuario.
15. **Error nuevo `quotation.whatsapp.settings_unreadable`** para la etapa `Channel`, en vez del
    genérico `quotation.send.failed`: es el único que la pantalla puede convertir en "revísalo en
    Configuración".
16. **Las claves globales siguen exigidas en producción**: respaldan el modo `Shared`, que es la
    opción predeterminada.
17. **El gate por capacidad usa `TenantModuleGuard`** y da 403 `tenancy.module_not_enabled`, el
    patrón del spec de entitlements para `/orders-export-layout`.
18. **Drenaje de rotación al guardar** y `apiKeyReadable` en el `GET` (desde la ronda 2, calculado
    descifrando de verdad; ver la 25).
19. **La validación de formato de las llaves no activas sólo corre en `Production`**, para que unos
    user-secrets mal cargados no tumben las pruebas de integración.
20. **`SecretProtectionOptions` es su propia sección registrada aparte**, no parte de
    `QuotationsOptions`.
21. **Custodia de la llave**: variable secreta del grupo `Backend-prod` como fuente de verdad, copia
    en la bóveda del owner, y nunca cambios con `kubectl` sobre el Secret.
22. **Auditoría con tres acciones** (`mode_changed`, `api_key_replaced`, `updated`), porque el
    publicador no lleva campos.
23. **RHF + zod en la sección nueva**, aunque `TenantSettingsForm` usa `useState`.
24. **El cifrado vive en Quotations.Infrastructure**; sube a BuildingBlocks con un segundo consumidor.
25. **[Orquestador] El drenaje no puede convertir un guardado en un 500**: si la key guardada no se
    descifra (`AuthenticationTagMismatchException`, `CryptographicException` o llave ausente), se
    salta el re-cifrado y el `PUT` se guarda igual. Y `apiKeyReadable` se calcula **intentando
    descifrar** (`TryUnprotect`, valor descartado), no con `HasKey`, que no detecta bytes dañados ni
    una llave distinta con el mismo id.
26. **[Orquestador] Re-cifrado en lote dentro del alcance**: `WhatsAppTokenRekeyWorker` corre una
    vez en cada arranque y re-cifra con la activa toda fila en otra llave. Sin él, una llave
    filtrada no se podía sacar de circulación hasta que cada tenant guardara su configuración. Las
    filas son pocas, es idempotente, se salta lo que no descifra y loguea sólo tenant id y key id.
    Con la rotación en dos fases, el conteo por `key_id` de la vieja llega a 0 tras desplegar la
    fase (b).
27. **[Orquestador] Sin campo `Configured`** (YAGNI): ninguna pantalla lo leía. Guardar `Shared` sin
    fila es un no-op (no crea fila), que es lo que ya hacía `Configure` sobre `CreateEmpty`.
28. **[Orquestador] Los campos de la cuenta propia se ignoran salvo en `Own`**: en `Shared` y
    `Disabled` ni se validan ni se guardan. Se retira el "dejar lista la cuenta propia antes de
    activarla", que sólo existía por la API: el frontend nunca los manda en esos modos.
29. **Lo requerido en `Own` lo decide el handler, no el validador**, porque depende de la fila: el
    validador sólo valida formato de lo que viene. Si no, `{ "mode": "Own" }` sería siempre 422.
30. **Rotación en dos fases** (declarar, desplegar; activar, desplegar) y regla de no retirar una
    llave a la que apunta alguna fila: evita que un pod viejo del rolling update no pueda leer lo que
    uno nuevo ya cifró con la llave nueva.
31. **`whatsapp-channel` devuelve también `mode`**, para que el diálogo de falla agregue la pista
    "revisa la plantilla en Configuración" sólo cuando la cuenta es la propia, sin pedir
    `tenancy.settings.read`. El canal se pide con `fetchQuery` dentro de `start()` para que la
    decisión no dependa de si una query montada aparte ya había llegado.
32. **API key con charset `^[\x21-\x7E]{1,512}$`**: el token viaja en el header `X-API-TOKEN`, y un
    espacio, un control o un carácter fuera de ASCII visible en lo pegado es casi siempre un error
    al copiarlo; mejor un 422 al guardar que un `credentials_rejected` al enviar.

## DECISIÓN-PENDIENTE

- **Confirmar que el número emisor propio lo puede leer quien tenga `tenancy.settings.read`**
  (decisión 4), o tratarlo como la key: de sólo escritura.
- **"Probar envío"**: mandar la plantilla a un número que elija el administrador antes de guardar.
  Necesita decidir a qué número, con qué datos de ejemplo y si cuenta como envío auditado.
- **Validar contra Zenvia al guardar** (`GET /v2/templates/{id}` con la key): detectaría key
  inválida, plantilla de otro número o no aprobada, y variables que no coinciden.
- **Si la cuenta de QEP (`Shared`) sigue siendo opción para siempre**, y con ella las claves globales
  de producción, o se retira algún día.
- **Otros proveedores** (el select ya los admite): cuáles y con qué campos.
- **Notificar al administrador** cuando el envío falla por `credentials_rejected` repetido.

## Fuera de alcance

- Plantillas distintas a la de cotización y variables configurables; webhooks de entrega de Zenvia.
- Administración por personal de QCode (no hay rol de plataforma).
- `BaseUrl` y `DocumentUrlHours` por tenant; migrar `TenantSettingsForm` a RHF.

## Historial de revisión

- Ronda 1 (2026-10-07): 13 hallazgos aplicados.
- Ronda 2 (2026-10-07): 15 hallazgos aplicados.
- Ronda 3 (2026-10-07): sin hallazgos bloqueantes; 4 mejoras menores aplicadas. Spec listo para
  revisión del owner.
