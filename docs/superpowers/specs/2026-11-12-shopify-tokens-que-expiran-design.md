# Shopify: tokens offline que expiran

Fecha: 2026-11-12. Estado: borrador para aprobación del owner. Repo: `qep-backend` (el frontend sólo traduce un texto, §8).

## 1. Por qué

Desde el **2027-01-01** Shopify exige a toda app pública tokens offline que expiran; un token que no expira recibe
errores de autenticación desde ese día
([changelog](https://shopify.dev/changelog/expiring-offline-access-tokens-required-for-all-public-apps-as-of-january-1-2027)).
QEP pide hoy el token sin `expiring` y no lo refresca (`ShopifyAdminGateway.cs:56-82`). Lo anticipó el spec de onboarding
§9.10; este spec lo cierra del lado de QEP.

Contexto que decidió el owner (2026-11-12):

- En QEP no hay tiendas reales conectadas: todas las conexiones de Shopify son de prueba.
- Lulo CRM v1 (legado) sí tiene tiendas en producción, pero **no se parchea**: se cierra cuando QEP la reemplace.
- Las tiendas de la v1 pasan a QEP por el **camino A** del §9.10: el comercio abre la app desde su Shopify y autoriza por
  OAuth. No hay token exchange de tokens viejos.
- Cuando el comercio abre la app, **su empresa ya existe en QEP con sus usuarios**: las crea la migración de la v1, que
  es externa y de un solo uso, y las deja con el módulo `shopify` activo (`GrantDefaultModulesOnSignup` sólo corre en el
  registro). El comercio inicia sesión con su usuario migrado y enlaza la tienda a esa empresa
  (`install/complete`); no pasa por el registro ni por el onboarding de §9.3 del spec de onboarding, que es lo que el
  camino A suponía. Este spec no cambia ese flujo: sólo cambia el token que el enlace guarda.

**Riesgo aceptado por el owner:** si QEP no reemplaza a la v1 antes del 2027-01-01, las tiendas de la v1 pierden la
integración ese día.

## 2. Objetivos

- Toda conexión de Shopify nueva o reconectada obtiene un token offline que expira (`expiring=1`) y su `refresh_token`.
- El token se refresca antes de vencer sin intervención de nadie, mientras la conexión exista.
- Si el refresco ya no es posible, la conexión queda en `NeedsAttention` con un código que le diga a la persona que tiene
  que volver a abrir la app desde Shopify.

## 3. No objetivos

- Parchear Lulo CRM v1.
- Token exchange (migrar tokens que no expiran a tokens que expiran).
- Tokens online o sesiones de una app embebida (S-8 del onboarding sigue abierta).
- Cambiar la forma en que los consumidores llaman a la Admin API, más allá de leer el token vigente.

## 4. Anti-objetivos (fallas aunque «funcione»)

- **Dos refrescos simultáneos de la misma tienda.** El refresh token rota, y Shopify advierte que adquirir y refrescar a la
  vez, o refrescar dos veces, puede dejar guardado un par ya retirado. El síntoma es una tienda que se cae una hora
  después sin causa visible.
- **Una conexión sana marcada `NeedsAttention`** porque un consumidor vio un token vencido mientras el worker iba tarde.
- **Un refresh token, un access token o una respuesta de `/admin/oauth/access_token` en un log, en la auditoría o en un
  mensaje de error.**

## 5. Lo que dice Shopify (verificado el 2026-11-12)

Fuentes: [offline access tokens](https://shopify.dev/docs/apps/build/authentication-authorization/access-tokens/offline-access-tokens),
[implement token exchange § Refresh](https://shopify.dev/docs/apps/build/authentication-authorization/implement-token-exchange),
[migrate to expiring offline tokens](https://shopify.dev/docs/apps/build/authentication-authorization/migrate-to-expiring-offline-access-tokens).

| Tema | Dato |
| --- | --- |
| Canje del code | `POST https://{shop}/admin/oauth/access_token` con `client_id`, `client_secret`, `code` y **`expiring=1`** en el cuerpo |
| Respuesta | `access_token`, `scope`, `expires_in` (3600 = 1 h), `refresh_token`, `refresh_token_expires_in` (7776000 = 90 días) |
| Refresco | mismo endpoint, `application/x-www-form-urlencoded` (según el ejemplo de la guía), con `client_id`, `client_secret`, `grant_type=refresh_token` y `refresh_token` |
| Rotación | cada refresco devuelve access token **y** refresh token nuevos; se usa el nuevo la próxima vez. El viejo deja de servir cuando se usa el nuevo, cuando se adquiere un token nuevo por OAuth, a los 30 días de su primer uso o a los 90 días de emitido |
| Falla | refresh token vencido, reemplazado o inválido: `401` con `{"error": "invalid_request"}`. Sólo se recupera si el comercio vuelve a abrir la app |
| Concurrencia | no adquirir y refrescar para la misma tienda a la vez: cada operación retira el resultado de la otra |

La guía no publica la forma exacta de la respuesta del refresco; se asume la misma del canje. La prueba manual contra una
tienda real (§9) lo confirma.

## 6. Diseño

### 6.1 Canje y guardado

- `ShopifyAdminGateway` manda `expiring=1` en el canje del code. `ShopifyTokenGrant` suma `RefreshToken`,
  `ExpiresAt` y `RefreshTokenExpiresAt`, calculados a partir de `expires_in` y `refresh_token_expires_in` y del reloj
  inyectado.
- **Si la respuesta no trae `refresh_token` ni `expires_in`, la instalación falla.** No se guarda un token que no expira.
  Es una precondición del 2027-01-01, no una preferencia.
- `shopify_installs` (el paso intermedio) guarda el refresh token cifrado y los dos vencimientos, igual que hoy guarda
  `access_token_protected`. Requiere una migración en Integrations.
- `LinkShopifyStore` guarda en la conexión:
  - el secreto nuevo `refreshToken`, cifrado con `ISecretProtector` y AAD `connectionId + fieldKey`, como `accessToken`;
  - los campos `tokenExpiresAt` y `refreshTokenExpiresAt` en `fields`, con el patrón de `InstagramFieldKeys.TokenExpiresAt`.
- `ConnectionSecretRekeyWorker` recifra el secreto nuevo y la columna nueva de `shopify_installs`. Hay que confirmar si
  ya lo hace por ser genérico.
- **La instalación vive más que el token.** Una instalación canjeada dura 7 días (`ShopifyInstall.ExchangedLifetime`) y su
  token, 1 hora. Por eso `LinkShopifyStore`, antes de registrar webhooks, refresca el token de la instalación si le
  quedan menos del margen de §6.2. Lo hace con el refresh token de la instalación, bajo el candado de §6.2 tomado por el
  id de la instalación, y guarda el par nuevo. Si ese refresco responde 401, el enlace falla con un error que pide volver a
  abrir la app desde Shopify; no se crea una conexión muerta. Decisión del diseño: se descartaron acortar la vida de la
  instalación (rompe el flujo de quien instala y enlaza otro día) y que el worker refresque instalaciones (mantiene vivos
  tokens de instalaciones que nadie va a enlazar).

### 6.2 Refresco: un solo escritor

- Worker nuevo `ShopifyTokenRefreshWorker` en `Modules.Integrations.Infrastructure`, con el modelo de
  `InstagramTokenRefreshWorker`.
- Recorre las conexiones de Shopify que tienen `refreshToken` y refresca las que vencen dentro del margen.
- Entran todos los estados, salvo `NeedsAttention` con `token_expired` o `app_uninstalled`, que son los únicos de los que
  el refresco no puede sacar a una conexión.
  - La pausa la decide el usuario: una conexión pausada tiene que poder reanudarse sin reautorizar.
  - `NeedsAttention` por otra causa (`webhooks_not_registered`, `credentials_rejected`, `provider_unreachable`…) también
    se refresca. Si no, una hora después el token estaría vencido, «Probar» recibiría un 401 y una falla recuperable se
    volvería permanente (`credentials_rejected`).
  - Decisión del diseño: el refresco no cambia el estado de la conexión, sólo sus secretos y vencimientos. La sacan de
    `NeedsAttention` los caminos que ya lo hacen (`ApplyVerification`).
- Constantes (decisión del diseño, ajustables): el worker corre cada 5 minutos y refresca cuando al token le quedan 20
  minutos o menos. Con tokens de 1 hora, eso deja tres vueltas de margen ante una falla transitoria.
- **Exclusión entre procesos:** el refresco de una conexión se hace con un candado de base de datos por conexión, tomado
  sin esperar (`pg_try_advisory_xact_lock` o equivalente). Mientras se tiene el candado:
  1. se relee la conexión;
  2. se confirma que todavía haga falta refrescar;
  3. se llama a Shopify;
  4. se guarda.

  El `Version` optimista **no alcanza** por sí solo: el perdedor ya habría consumido el refresh token en Shopify. Hoy hay
  una réplica (CLAUDE.md, «La presencia de la bandeja es por proceso»), pero el candado es lo que permite subir
  `replicas` sin romper Shopify.
- **Adquirir y refrescar no se pisan:** `LinkShopifyStore` (instalar o reconectar) toma el mismo candado al reemplazar los
  secretos de una conexión que ya existe. El worker, al tomar el candado, relee la conexión, y si el secreto cambió desde
  la lectura, no refresca.
- Guardado: `connection.Update(...)` con los secretos y campos nuevos, en un `SaveChanges`. Si el guardado falla después
  de que Shopify respondió, el par nuevo se pierde. El viejo sigue sirviendo hasta que se use el nuevo, que nadie tiene,
  así que la siguiente vuelta refresca con el viejo. Así se recupera solo, y hay que probarlo (§9).

### 6.3 Fallas del refresco

| Respuesta | Qué hace |
| --- | --- |
| `401` / `invalid_request`, o cualquier 4xx salvo 429 | `NeedsAttention` con `token_expired` por `IConnectionHealthReporter`; se deja de refrescar. Vale para cualquier estado, también `Paused` (owner, 2026-11-12): `ReportCredentialsRejectedAsync` ignora lo que no está `Active`, así que hace falta un reporte propio |
| 429, 5xx, timeout, red | no cambia el estado; se reintenta en la siguiente vuelta. Se loguea sin cuerpo ni tokens |
| La conexión no tiene `refreshToken` (conexión de prueba anterior a este cambio) | `NeedsAttention` con `token_expired` en la primera vuelta. La persona reconecta. Decisión del diseño: no hay tiendas reales que lo sufran |
| El evento `connection-needs-attention` al pasar a `token_expired` | Se publica desde `Active` y desde `NeedsAttention` con otro código, con `failureCode = token_expired` en el payload; desde `Paused` no (la pausa fue del usuario), y ya en `token_expired` ni publica ni audita (owner, 2026-11-12: el evento identifica el error) |

### 6.4 Consumidores

- Nadie fuera del worker refresca. `ShopifyConnectionDirectory.OperationalTokenAsync` y los caminos de Integrations que
  leen `accessToken` (`ShopifyConnectionTester`, `LinkShopifyStore` al registrar webhooks) leen el token guardado, sin
  cambios.
- **Un token vencido no marca la conexión ni la vuelve no operable.** No se reutiliza `IsOperational = false`, porque
  tiene efectos más allá de «saltar»:
  - `ShopifyOrderIngestion` guarda la fila del espejo sin importar el pedido;
  - `ShopifyWebhookDeliveryProcessor.RedactShopAsync` da la tienda por no reinstalada y borra su espejo y sus entregas.

  Si un worker atrasado provocara eso, perderíamos datos. Por eso el directorio expone el vencimiento por separado: por
  ejemplo, `ShopifyConnectionRef` con el token en `null`, o una bandera de «token no disponible» con la conexión todavía
  operable. Sólo los caminos que llaman a la Admin API (la conciliación y lo que pasa por `ReconcileStore`) saltan esa
  corrida sin reportar nada; la ingesta de webhooks y el redact siguen igual. La forma exacta del contrato del puerto la
  decide quien construya, con prueba de que el redact no borra con un token vencido.
- **El token viejo no muere con el refresco.** Shopify retira el token anterior, pero sigue valiendo hasta que se cumple su
  `expires_in` (offline access tokens: «They remain valid until their `expires_in` duration ends»), así que una
  conciliación en curso no se cae.
- Por si acaso, se agrega una red barata: ante un 401 con un token vigente, el consumidor relee el token una vez. Si cambió,
  reintenta; si es el mismo, sigue como hoy (`credentials_rejected`). Así, una app desinstalada sin webhook o un token
  revocado se siguen viendo.

### 6.5 Observabilidad

- Se loguea cada refresco con su resultado: conexión, `ok` | `rejected` | `transient`, y el vencimiento nuevo. Nunca el
  token ni el cuerpo de la respuesta.
- Auditoría: igual que el refresco de Instagram. Si Instagram no audita el refresco, este tampoco.

## 7. Restricciones

- Arquitectura: el worker y el gateway viven en Integrations. Shopify sigue sin referenciar `Modules.Integrations.Application`
  (`ShopifyLayerTests`).
- TDD con evidencia de RED y GREEN. El HTTP de Shopify se prueba con un fake del gateway en unitarias y con un handler
  falso en integración, nunca contra Shopify.
- La migración nueva de `shopify_installs` se genera con el factory de diseño (CLAUDE.md, «Gotchas»).

## 8. Frontend (`qep-frontend`)

`token_expired` ya existe como código (Instagram). Hay que confirmar que el texto que el frontend muestra para una
conexión de Shopify con `token_expired` le diga a la persona que vuelva a abrir la app desde su Shopify, no algo de
Instagram ni de Meta. Si hace falta cambiarlo, va con su propio spec en ese repo.

## 9. Verificación

- Unitarias e integración:
  - canje con `expiring=1`;
  - rechazo de una respuesta sin `refresh_token`;
  - guardado de los secretos y campos nuevos;
  - refresco dentro del margen y no antes;
  - candado: dos refrescos simultáneos hacen **una** llamada a Shopify;
  - reconectar mientras el worker corre;
  - 401 → `NeedsAttention` con `token_expired`; 5xx → sin cambio;
  - conexión sin `refreshToken` → `NeedsAttention`;
  - token vencido en el directorio → corrida saltada sin marcar;
  - con un token vencido, el redact no borra nada y la ingesta de webhooks importa igual;
  - enlazar una instalación de más de 1 hora → refresca antes de registrar webhooks; con 401, el enlace falla sin crear
    la conexión;
  - `NeedsAttention` con `webhooks_not_registered` se sigue refrescando; con `token_expired` o `app_uninstalled`, no;
  - 401 con un token vigente que cambió entre la lectura y la llamada → se relee y se reintenta una vez;
  - guardado fallido tras el refresco → la siguiente vuelta se recupera;
  - ningún token en los logs (con el host en `Trace`, como `TheKeyNeverAppearsInLogsAuditOrHttpCalls`).
- Manual, contra una tienda de desarrollo real y antes de `main`:
  - instalar y ver `tokenExpiresAt` a 1 hora;
  - esperar al refresco y confirmar que la conciliación sigue funcionando;
  - confirmar la forma de la respuesta del refresco.

## 10. Lo que queda para quien construya

- Forma exacta del candado: advisory lock por conexión o un lease en tabla.
- Nombres de las columnas nuevas de `shopify_installs` y de los campos.
- Si `ConnectionSecretRekeyWorker` necesita cambios.
- Si el worker de Instagram y este comparten una base común. No es necesario; dos workers parecidos son aceptables.
