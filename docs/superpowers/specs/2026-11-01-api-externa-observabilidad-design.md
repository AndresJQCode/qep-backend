# API externa: saber qué pasó con cada llamada

Fecha: 2026-11-01. Repos: `qep-backend` y `qep-frontend`. Base: `2026-10-30-api-keys-design.md`.

## Por qué

Un integrador envió la plantilla de cobro vencido por la API externa. Meta rechazó el pago, el dueño lo
arregló, y el reintento con la misma `idempotencyKey` respondió `201` sin que llegara nada ni apareciera
otra burbuja. Nadie pudo saber qué pasó sin leer código y base de datos.

Hoy:

- `201` significa «Meta aceptó», no «entregado», y la API no ofrece otra forma de consultar.
- Lo que Meta rechaza después llega por webhook y sólo se ve en la burbuja del hilo.
- Las fallas de la API externa quedan en `platform.request_failures` con `tenant_id` nulo: el tenant sale de
  la ruta (`RequestFailure.Create`) y la ruta externa no lo trae. El log por tenant no las muestra.
- Un `failed` tardío del primer intento puede pisar un reintento correcto (ver parte 3).
- Meta puede reclasificar una plantilla de cobro de `UTILITY` a `MARKETING` sin que nadie se entere. Pasó con
  `qep_cobro_vencido`.

Los consumidores son aplicaciones externas de los tenants. El objetivo es que quien integra pueda depurar
sin soporte de QEP.

## Objetivos

1. Registro de cada llamada a la API externa, visible en Configuración › Desarrolladores.
2. Consulta de estado de un mensaje para que la aplicación externa reaccione sola.
3. Un `failed` tardío no puede pisar un reintento.
4. Aviso cuando Meta cambia la categoría de una plantilla de sistema.
5. Guía de respuestas e idempotencia para integradores.

## No objetivos

- Webhooks salientes hacia el integrador. Se descartó por costo; el `GET` de estado cubre la necesidad.
- Procesar el webhook `template_category_update`. Un cambio posterior se ve al sincronizar.
- Corregir `tenant_id` nulo en `platform.request_failures`. Ese log queda para admin; decisión del autor del
  documento, porque el registro nuevo es el que ve el tenant.
- Cambiar qué significa `201` o el contrato actual del `POST`.
- Avisos por categoría para plantillas creadas por el tenant.

## Anti-objetivos

Sería un fracaso aunque funcione si:

- Un error al escribir el registro tumba o demora un envío.
- El registro guarda la API key, el cuerpo del request, un teléfono o los parámetros de la plantilla.
- Un tenant ve filas de otro, o quien administra claves ve una traza de excepción.

## Parte 1: registro de consumos

### Decidido con el owner

- Vive en Configuración › Desarrolladores.
- Guarda todas las llamadas, exitosas y fallidas, con fines de depuración.
- Retención de 15 días.
- Es para personas; el `GET` de la parte 2 es para aplicaciones.

### Tabla

Tabla propia en el esquema de `ApiAccess`, no `platform.request_failures`: ese log guarda sólo fallas, es de
admin y contiene trazas.

Una fila por llamada autenticada a `/api/v1/external/`:

| Campo | Nota |
| --- | --- |
| `id` | |
| `tenant_id` | del claim de la clave, nunca de la ruta |
| `api_key_id` | |
| `api_key_name` | copia del nombre, como `sent_by_api_key_name`: la clave puede renombrarse |
| `method`, `path` | sin query string |
| `status_code` | |
| `error_code` | el `code` del ProblemDetails, nulo si fue éxito |
| `message_id` | nulo si la llamada no resolvió un mensaje |
| `trace_id` | |
| `duration_ms` | |
| `occurred_at` | |

Nunca se guarda: cuerpo, query string, headers, teléfono, parámetros ni `idempotencyKey`.

Una llamada sin clave válida conocida no tiene tenant y no se registra. Una clave revocada o vencida sí se
identifica; si en el punto de autenticación está su tenant, se registra con su `401`.

### Escritura

- Después de responder la llamada, con contexto propio, sin transacción compartida con el request.
- Una falla al escribir se loguea y se absorbe, como `TouchLastUsedAsync`.
- `message_id` se fija apenas el reclamo conoce el mensaje, para que también quede en un `502` o `422` de Meta.
  El mecanismo queda al implementador; `ApiAccess` no ve Messaging.

### Purga

Trabajo automático diario que borra filas de más de 15 días. Índice por `(tenant_id, occurred_at DESC)`.

### Lectura

`GET /api/v1/tenants/{tenantId}/api-keys/requests`, con `ApiAccessPermissions.ApiKeyManage` y módulo `developer`,
filtrada por tenant.

- Filtros: clave, resultado (`success`, `failure`), fechas y `traceId`.
- `failure` incluye los envíos aceptados que fallaron después: es el caso que motiva este documento. El puerto entrega los
  `message_id` en `Failed` de claves de API en ese rango de fechas, y la consulta filtra por `status_code >= 400 OR message_id
  = ANY(…)`, para que página y total salgan bien. El rango está acotado por los 15 días de retención.
- Paginada, de la más nueva a la más vieja, con la convención de listas del proyecto.
- Filas con `message_id` traen estado actual del mensaje: `status`, `failureCode`, `failureReason`. Se resuelve
  al leer, por un puerto con adaptador en Bootstrapper; precedente: `IApiKeyConnectionDirectory`.
- `failureReason` usa `MessageFailureReasons`.

### Pantalla

Pestaña «Registro» en Desarrolladores. Columnas: fecha, clave, llamada, status, resultado del mensaje y detalle.
Un envío aceptado que falló después se lee como `201 → Fallido` y su motivo, marcado como falla. Cubre carga,
vacío, error y éxito. La ruta y la forma exacta de las pestañas quedan al implementador. Sin rediseñar lo demás
de la página.

## Parte 2: consulta de estado

`GET /api/v1/external/whatsapp/messages/{messageId}`

- Scope `whatsapp.templates.send`, módulo `developer`, tenant de la clave.
- Sólo mensajes con `sent_by_kind = 4` del tenant y de una conexión que la clave pueda usar. Cualquier otro caso es
  `404`, sin revelar si existe.

```json
{
  "messageId": "…",
  "conversationId": "…",
  "status": "Failed",
  "failureCode": 131042,
  "failureReason": "…"
}
```

`status`: `Sent`, `Delivered`, `Read`, `Failed` o `Unconfirmed`. `Unconfirmed` es `Failed` con `failure_code = -1`:
Meta pudo haberlo aceptado y QEP no lo reenvía. `failureCode` y `failureReason` son nulos fuera de esos dos.

Un `Unconfirmed` puede no resolverse nunca, y eso se mantiene: un `failed` no lo convierte en `Failed`, porque podría ser
del intento anterior y habilitar un reenvío duplicado. La salida es la de la guía actual: revisar el hilo y, si se decide
mandar otro, usar una `idempotencyKey` nueva asumiendo el riesgo de duplicado.

El texto de `-1` en `MessageFailureReasons` termina en «Intenta de nuevo», pensado para el asesor. El `GET` y el registro usan
un texto propio para `Unconfirmed` que no invite a reintentar; el de la burbuja no cambia.

Una persona no puede reintentar desde la bandeja un mensaje enviado por API: el SPA sólo ofrece «Reintentar» en su cola local.
Por eso `sent_by_kind = 4` no cambia en la práctica y no se agrega nada para ese caso.

Esta llamada también se registra en la parte 1.

## Parte 3: `failed` tardío

Causa: `StatusIngestion.ApplyAsync` encuentra la fila por `wamid = X OR id = callback`, y el callback se repite
entre intentos. Un `failed` del primer intento encuentra por callback la fila ya reenviada y la deja en `Failed`
aunque Meta haya aceptado el segundo.

Corrección, en tres cambios que van juntos:

1. El callback sólo identifica la fila si no tiene `wamid`:

   ```sql
   AND (wamid = {update.Wamid} OR (id = {callback} AND wamid IS NULL))
   ```

2. Un `failed` que aplica por webhook deja `wamid = NULL`, en lugar de conservarlo.
3. `CommitFailedAsync` también deja `wamid = NULL`.

Sin 2 y 3 el predicado introduce una regresión: una fila reenviada conservaría el `wamid` viejo, así que el webhook
del intento vigente no la encontraría. Con la columna nula, ese webhook coincide por callback, espera el candado del
reintento y Postgres reevalúa la fila después del commit. No hace falta columna ni migración.

Requisitos:

- El chequeo `exists` conserva `wamid = X OR id = callback`: un estado obsoleto debe dar `NoChange`. `NotFound` con
  callback produce `RetryLater` en `WebhookDeliveryProcessor`, y un estado viejo se reintentaría sin sentido.
- Antes de implementar, confirmar que nada lee `wamid` de filas `Failed`. Confirmado en la revisión final: `OutboundGuards.cs:105`,
  `ApplySeenAsync` e `InboundIngestion` leen `wamid`, pero actúan sobre filas entrantes o citadas, nunca sobre un saliente
  `Failed`; un `NULL` en esa fila no cambia su resultado.
- `FindMarketingOptOutCustomerAsync` no se cambia: si Meta informa opt-out, sigue siendo cierto para esa persona.
- Riesgo residual aceptado: un `sent` o `delivered` viejo reentregado mientras la fila está `Unconfirmed` podría resolverla
  por callback. Exige que Meta reentregue un acuse de un intento ya fallido.
- Pruebas de regresión primero, RED antes que GREEN, en integración:
  - `failed` tardío de X después de un reintento aceptado con Y: sigue `Sent` con Y.
  - Reintento sin confirmar, y acuse de Y: pasa a `Sent` con Y.
  - Webhook de Y durante el reintento: espera y aplica sobre el resultado.
  - `Unconfirmed` sin intento previo: se sigue resolviendo por callback.

## Parte 4: aviso de categoría

Sólo plantillas de sistema de `BillingTemplateDefinitions`.

- `CategoryChanged` se calcula en backend comparando la categoría real con la de su definición. No requiere migración,
  así cubre la `qep_cobro_vencido` que ya existe. La comparación debe reconocer nombres con sufijo (`_v2`).
- El DTO de plantillas de administración expone `requestedCategory` cuando difiere. La pantalla
  `/integrations/whatsapp/$connectionId/templates` muestra el aviso: Meta la clasificó como Marketing, puede fallar a
  personas que apagaron promociones, y el costo cambia. Con backend viejo el campo no existe y no hay aviso.
- Al aprovisionar con categoría distinta se escribe un log `Warning` con tenant, plantilla y ambas categorías.
- Sigue sin enviarse `allow_category_change` (D-T10). El aviso no bloquea envíos.

## Parte 5: guía para integradores

En `ApiUsageGuide`, copy en español neutro:

| Respuesta | Qué significa | Qué hacer |
| --- | --- | --- |
| `201` | Meta aceptó, no es entrega | consultar estado |
| `200` | la clave ya se usó con ese envío; no se reenvió | consultar estado |
| `201` con clave repetida | el anterior falló con certeza; se reenvió en la misma burbuja | consultar estado |
| `409` | clave usada con otro destino o conexión | usar otra clave solamente si es otro envío |
| `422` | parámetros o plantilla inválidos | tomar `parameters` de `GET …/templates` |
| `502` | no se confirmó con Meta; puede haber salido | consultar estado; mandar otro con clave nueva es decisión del integrador y puede duplicar |

Debe aclarar que `Sent` no significa entregado, y enlazar a la pestaña «Registro».

## Restricciones

- Reglas de capas de `ArchitectureTests`; `ApiAccess` no referencia Messaging.
- Worktrees propios en los dos repos: ambos `develop` tienen trabajo de otras sesiones.
- Unitarias por tarea; integración una vez, al final.
- Deploy: backend primero.

## Queda al implementador

- Punto exacto del pipeline para capturar y cómo llega `message_id` al registro.
- Forma de la pestaña y paginación según convenciones existentes.
- Si la purga usa un worker existente o uno nuevo.
