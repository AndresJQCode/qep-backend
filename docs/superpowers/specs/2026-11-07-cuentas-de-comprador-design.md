# Cuentas de comprador — diseño (backend)

Fecha: 2026-11-07 · Repo: `qep-backend` · Spec hermano: `qep-storefront/docs/superpowers/specs/2026-11-07-cuentas-de-comprador-design.md`
Mockups aprobados: https://claude.ai/artifact/EKAGqLx63XgmCCK2pZK9tj

## Intención

El comprador que vuelve a una tienda no debe tener que escribir de nuevo sus datos en el checkout, aunque compre
desde otro dispositivo. Para eso necesita una cuenta en el servidor: con email y contraseña, que guarde sus datos
de checkout y le sirva en todas las tiendas de la plataforma.

Esto revierte en parte el fuera de alcance de `2026-11-03-tienda-publica-design.md` (línea 29). Las cuentas de
visitante entran. El seguimiento del pedido por el visitante sigue fuera.

## Metas

- Una **cuenta de comprador única para toda la plataforma**: el mismo email y la misma contraseña sirven en cualquier tienda.
- Login con **email y contraseña**, verificación de email y recuperación de contraseña. Los emails salen por Infobip
  (`IEmailChannel`, módulo Notifications).
- Un **perfil guardado** con los datos del checkout. El storefront lo usa para completar el formulario.
- Crear la cuenta **desde el checkout**, al enviar el pedido, o desde un registro aparte.
- Desde el checkout con sesión iniciada, poder **actualizar el perfil** con los datos del pedido.
- **Eliminar la cuenta** por pedido del titular (Ley 1581).

## No metas

- Historial de pedidos o seguimiento del pedido por el comprador.
- Varias direcciones guardadas: hay una de envío y una de facturación.
- Login social (Google y otros), códigos por email, SMS.
- Sesión compartida entre tiendas o SSO.
- Vincular la cuenta de comprador con la entidad `Customer` del tenant.

## Anti-metas (sería un fracaso aunque funcione)

- Que un tenant vea datos de un comprador que **no le hizo un pedido**. El perfil es del comprador. Cada tienda
  recibe los datos solo dentro del pedido, como hoy.
- Que se pueda averiguar si un email tiene cuenta. Registro, recuperación de contraseña y "guardar mis datos" en el
  checkout responden igual exista o no la cuenta.
- Mezclar compradores con los usuarios de la consola (`identity.users`) o darles cualquier acceso a la consola.
- Que una sesión de comprador sirva en otra tienda distinta de la que la emitió.
- Que un error con la cuenta (registro, email, perfil) haga fallar o demore el pedido.

## Decisiones tomadas con el owner

1. La cuenta es única para toda la plataforma y el inicio de sesión es por tienda. La sesión queda atada al host de
   la tienda donde se inició, y el comprador inicia sesión de nuevo en cada tienda con las mismas credenciales.
2. Se entra con email y contraseña.
3. **Al crear la cuenta en el checkout**, el pedido se procesa como siempre y la cuenta se crea pendiente de verificar.
4. **No se puede iniciar sesión sin verificar el email.** El enlace de verificación dura 24 h y se puede reenviar.
5. **El enlace de recuperación de contraseña** dura 1 h.
6. **Email ya registrado al crear la cuenta en el checkout:** el pedido sale igual y no se crea ni se modifica
   ninguna cuenta. A ese email se envía "ya tienes una cuenta, inicia sesión la próxima vez". La respuesta en
   pantalla es la misma que en el caso feliz.
7. **Checkout con sesión:**
   - El formulario viene completo con el perfil guardado.
   - Si el comprador cambia algo, el storefront ofrece "Actualizar mis datos guardados", marcada por defecto.
   - El perfil se actualiza solo si esa casilla va marcada.
8. **Página "Mis datos":** editar el perfil, cambiar la contraseña y eliminar la cuenta.
9. **Eliminar la cuenta** borra las credenciales, el perfil y las sesiones. Los pedidos que ya recibió cada tienda no
   se tocan.
   - **Se conserva solo el registro de la autorización**: fecha, IP, host de la tienda, URL y versión de la
     política, y el email como referencia del titular.
   - Se guarda durante el plazo que exija la ley. La Ley 1581 y el Decreto 1377 obligan a conservar la prueba de la
     autorización, pero no fijan un número de años. El plazo lo define la asesoría legal (pendiente 1). Mientras
     tanto, **no se borra automáticamente**.
   - Va en una tabla propia, separada de la cuenta, para que sobreviva a la eliminación.
10. **Duración de la sesión:** 30 días como máximo y 7 días sin uso, igual que `qep_session`.
11. **Al crear la cuenta, el comprador acepta la política de datos de la plataforma**, no la de la tienda.
    - Quien responde por los datos de la cuenta es la plataforma.
    - La política es una página del storefront en una ruta fija, la misma en todas las tiendas: `https://{host}/cuenta/politica`.
      El borrador del texto está en `qep-storefront/docs/superpowers/specs/2026-11-07-politica-de-datos-cuentas-borrador.md`.
    - El consentimiento guarda la URL absoluta, armada por el backend con el `{host}` de la ruta, y un identificador
      de versión de la política. El cuerpo de la petición no aporta ninguno de los dos.
    - La política de la tienda se sigue aceptando con cada pedido, como hoy. Por eso no importa que una tienda no
      tenga la suya (`OnlineStore.DataPolicyUrl` es opcional).
12. **Los emails de cuenta salen desde la plataforma**, con su remitente y su nombre.
    - El cuerpo nombra la tienda donde se hizo la acción, por ejemplo "Confirma tu cuenta para comprar en Acme Store".
    - No hay un dominio de envío por tenant.
13. **Turnstile** (`ICaptchaVerifier`) en las rutas que mandan email: registro, olvidé mi contraseña y reenviar
    verificación. También tienen el límite de tasa por IP.
    - El login no lleva Turnstile.
    - "Crear cuenta" desde el checkout no necesita uno propio: usa el del pedido.

## Restricciones del código existente

- **Las rutas públicas viven bajo `/api/v1/public/stores/{host}`** (`PublicStoreEndpoints.cs`). Se autentican con
  `X-Qep-Storefront-Key` (`StorefrontKeyFilter`), y `X-Qep-Visitor-Ip` es confiable solo con esa clave. El
  middleware de CSRF las salta por prefijo (`StorefrontPaths.cs`). Las rutas de comprador nuevas siguen ese mismo
  esquema, porque **las llama el BFF del storefront de servidor a servidor, no el navegador**.
- **Patrones de Identity que se replican sin compartir tablas:**
  - hash de contraseñas con `PasswordHasher` (`AspNetCorePasswordHasher.cs`);
  - tokens de un solo uso guardados como hash (`EmailToken`, `EmailTokenIssuer`);
  - sesiones guardadas como hash (`Session.cs`);
  - envío de emails por outbox con un worker por plantilla (`*DeliveryWorker.cs`) y plantillas en C#.
- **La política de contraseñas** es la misma que la de Identity (`registro-y-login-con-contrasena-design.md`).
- **Límites de tasa por IP del visitante.** Las rutas de credenciales (login, registro, recuperación de
  contraseña, reenvío de verificación) usan límites como la política `Credentials`: 10 por minuto, por ruta y por
  IP del visitante.
- **Traducción de errores de base:** se hace en Infrastructure. La unicidad del email se discrimina por nombre de
  índice.
- **Capas:** las verifica `ArchitectureTests`. Si se crea un módulo nuevo, trae su propio `<Modulo>LayerTests.cs`.

## Forma de la solución

### Cuenta y perfil

- **`BuyerAccount`, de alcance de plataforma, sin `TenantId`:**
  - email normalizado y único;
  - hash de la contraseña;
  - `EmailVerifiedAt`;
  - fechas de creación y modificación.
- **`BuyerProfile`, uno por cuenta.** Es la última versión guardada de los bloques del checkout, con los mismos
  campos y las mismas reglas que `PublicBillingInput` y `PublicShippingInput`, más el indicador "mismo envío".
  - El perfil puede guardar un país o una ciudad que una tienda no vende. Precargar no valida: la validación
    sigue siendo la del pedido.
- **Consentimiento.** Al crear la cuenta se registra el momento, la IP del visitante, el host de la tienda y la URL
  de la política de datos de la plataforma que se aceptó (decisión 11).

### Sesión

- **Login exitoso:** la sesión queda ligada a la cuenta y al host de la tienda. El backend devuelve el token en
  claro una sola vez y guarda solo su hash.
- **Cada ruta autenticada** recibe el token y el `{host}` de la ruta. Si el host no coincide con el de la sesión,
  responde `401`.
- **Cookie:** el backend no escribe cookies para el comprador. La escribe el storefront en su propio dominio (ver
  el spec del storefront).

### Rutas nuevas (bajo `/api/v1/public/stores/{host}`)

| Operación | Notas |
| --- | --- |
| Registrar cuenta | Datos del perfil opcionales. Respuesta neutra. Envía el email de verificación, o el de "ya tienes cuenta" si el email existe. |
| Verificar email | Recibe el token del enlace. |
| Reenviar verificación | Respuesta neutra. |
| Iniciar sesión | Responde igual si la contraseña es mala o si el email no existe. Una cuenta sin verificar no entra (decisión pendiente 3). |
| Cerrar sesión | Revoca la sesión. |
| Obtener sesión y perfil | Para precargar el checkout y para "Mis datos". |
| Actualizar perfil | |
| Cambiar contraseña | Pide la contraseña actual y revoca las demás sesiones. |
| Olvidé mi contraseña / Restablecer | Respuesta neutra. Al restablecer se revocan todas las sesiones. |
| Eliminar cuenta | Pide la contraseña actual. |

**Pedido (`POST /order-requests`):** el cuerpo suma dos campos opcionales.

- **Crear cuenta:** trae una contraseña. Usa el email y los datos del bloque de facturación y de envío del pedido.
  Se procesa **después** de aceptar el pedido. Si falla, no revierte el pedido ni cambia su respuesta; queda un log.
- **Actualizar perfil:** solo es válido con una sesión de comprador que pertenezca al mismo host. Reemplaza el
  perfil con los bloques del pedido.

Si un reintento trae el mismo `ClientRequestId`, devuelve el primer recibo **sin** volver a crear la cuenta ni
actualizar el perfil.

### Emails

Todos se envían por outbox y `IEmailChannel`. Hay cuatro plantillas nuevas:

- verificación de la cuenta;
- "ya tienes una cuenta";
- restablecer la contraseña;
- cuenta eliminada.

Los enlaces apuntan al **host de la tienda** donde se originó la acción, por ejemplo
`https://{host}/cuenta/verificar?token=…`. Las rutas exactas las define el spec del storefront. El remitente es el
de la plataforma; ver la decisión pendiente 2.

### Casos borde (decididos por el diseñador, no por el owner)

- **Contraseña inválida al crear la cuenta en el checkout.** Se valida con el resto del cuerpo del pedido y devuelve
  `422` en ese campo, antes de aceptar el pedido. Es un error del formulario que el comprador puede corregir.
  - "La cuenta nunca hace fallar el pedido" aplica a lo que ocurre **después** de aceptarlo: email existente, fallo
    al guardar o al encolar el email. Esos errores solo se registran en el log.
  - El storefront también valida la contraseña antes de enviar.
- **"Actualizar perfil" con una sesión que ya no sirve** (vencida, de otro host o de una cuenta eliminada). El pedido
  se acepta y el indicador se ignora; queda un log. El BFF borra la cookie si la respuesta indica que la sesión no es
  válida.
- **Email que existe pero no está verificado** (en el registro o en el checkout):
  - No se envía "ya tienes una cuenta". La contraseña nueva reemplaza la anterior, los tokens de verificación
    previos se invalidan y se envía otra vez el email de verificación.
  - El perfil también se reemplaza. Hasta verificar, nadie puede iniciar sesión, así que no queda expuesto.
  - **Restablecer la contraseña** de una cuenta sin verificar la marca como verificada, porque prueba que el
    comprador controla el email.
  - Las cuentas sin verificar se borran a los 7 días y el email queda libre.
  - Esto evita que alguien "ocupe" un email ajeno.
  - **Riesgo aceptado:** si alguien registra de nuevo el mismo email después del dueño, el último enlace que reciba
    el dueño activa la contraseña de ese otro. Para que pase, el dueño tiene que hacer clic en un email que no
    pidió. Si pasa, restablecer la contraseña lo corrige.
- **Email de login y email del perfil:**
  - El email de la cuenta (el del login) y el de facturación del perfil son independientes.
  - "Actualizar mis datos guardados" cambia solo el perfil; nunca el email de login.
  - El header muestra el email de login.
  - Cambiar el email de login queda fuera de este alcance.
- **Login de una cuenta sin verificar:**
  - Con la contraseña correcta, se responde explícitamente "confirma tu email" y se ofrece reenviar. No revela nada
    que quien escribe no sepa ya.
  - Con una contraseña incorrecta, la respuesta es la neutra de siempre.

## Lo que queda para quien construya

- Si la cuenta va en un módulo nuevo (por ejemplo `Buyers`) o dentro de `Store`. Debe quedar separada de Identity.
- Los nombres de tablas, rutas, códigos de error y el header del token de sesión.
- Cómo limpiar sesiones y tokens vencidos.

## Decisiones pendientes del owner

1. **Datos legales de la política de la plataforma.** El borrador marca como `[PENDIENTE]` lo que no se puede
   inventar: razón social, NIT, domicilio y canal de contacto. Además, conviene que un abogado lo revise antes de
   producción y que defina el plazo de conservación de la prueba de autorización (decisión 9).
2. **Dirección y nombre del remitente de la plataforma.** Hoy no están en el código.
