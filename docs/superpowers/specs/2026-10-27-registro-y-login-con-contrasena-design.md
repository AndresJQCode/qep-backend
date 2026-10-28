# Registro y login con email y contraseña — backend

Fecha: 2026-10-27. Spec par del frontend:
`qep-frontend/docs/superpowers/specs/2026-10-27-registro-y-login-con-contrasena-design.md`.

## Intención

Hoy la única forma de entrar a QEP es Google. Se suma **email + contraseña** como segunda vía,
para registrarse (crear empresa) y para iniciar sesión. Google sigue igual. Las dos vías
terminan en el mismo usuario, la misma sesión opaca (`qep_session`) y la misma aceptación de
invitaciones.

## Restricción que manda todo el diseño

El email es la llave que une credenciales: un login de Google sin `provider_link` se vincula al
usuario existente con ese email (`ProviderLinkingService.cs:45`). Por eso **ninguna contraseña
da acceso a una cuenta cuyo email no se haya probado**. Si no, alguien registra
`victima@x.com` con contraseña propia y, cuando la víctima entra con Google, cae en la cuenta
del atacante. Toda regla de abajo existe para cerrar eso.

## Objetivos

- Registrar empresa con email + contraseña, con verificación del email antes de la primera sesión.
- Iniciar sesión con email + contraseña.
- Recuperar contraseña por email. El mismo flujo sirve para que un usuario de Google **cree**
  su contraseña.
- Aceptar una invitación con contraseña, desde la pantalla de invitación y anunciado en el mail.
- No revelar si un email existe en ningún endpoint anónimo.

## No objetivos

- Cambiar la contraseña desde el perfil con sesión iniciada.
- 2FA, passkeys, magic links.
- Proveedores distintos de Google; desvincular Google.
- Bloqueo de cuenta por intentos fallidos (sólo rate limit por IP; ver «Pendiente»).

## Anti-objetivos (sería un fracaso aunque funcione)

- Una contraseña que entra a una cuenta con email sin verificar.
- Una respuesta, código o tiempo de respuesta que distinga «el email existe» de «no existe».
- Un token o una contraseña en texto plano en la base, en un log o en la auditoría.

## Modelo de datos (módulo Identity)

| Cambio | Detalle |
| --- | --- |
| `identity.users.email_verified_at` (`timestamptz null`) | `null` = no probado. La migración la llena con `linked_at` del primer `provider_link` de cada usuario: Google ya lo probó. |
| `identity.password_credentials` | `user_id` (PK, FK a `users` con `ON DELETE CASCADE`), `hash` (text), `updated_at`. Una por usuario. Es una credencial más, como `provider_links`; no va en `users`. |
| `identity.email_tokens` | `id`, `user_id` (FK cascade), `purpose` (`verify` \| `reset`), `token_hash` (SHA-256, único), `expires_at`, `used_at null`, `created_at`. Mismo patrón que `sessions`: sólo se guarda el hash. |

`User` gana `EmailVerifiedAt` y `MarkEmailVerified(now)` (idempotente). Vincular Google llama a
`MarkEmailVerified` en **los dos** caminos: `ProviderLinkingService` (login) y
`OwnerProvisioningService` (registro con Google). Ojo: `OwnerProvisioningService` no comprueba
`email_verified` por sí mismo; lo hace su llamador (`RegistrationEndpoints.cs:81`). Que la
comprobación pase al servicio, para que marcar el email como verificado no dependa de quién lo
llame.

**Vincular un proveedor a un usuario con `email_verified_at null` borra su credencial de
contraseña e invalida sus tokens de email**, en los dos caminos. La regla vive en un solo lugar
compartido (dominio o servicio de Identity), no duplicada en cada servicio. Ver «El ataque» en
Pruebas.

Las cascadas importan: `OrphanUserCleanup` borra físicamente al usuario cuando se va su última
membresía, y no puede quedar una credencial o un token huérfano.

## Hash de contraseña

`Microsoft.AspNetCore.Identity.PasswordHasher<User>` (PBKDF2 con el formato versionado de
ASP.NET Core), detrás de un `IPasswordHasher` propio en Application e implementado en
Infrastructure. No se instala ASP.NET Core Identity completo. Si `VerifyHashedPassword` devuelve
`SuccessRehashNeeded`, se re-hashea al iniciar sesión.

Reglas: **mínimo 10 caracteres, máximo 128**, sin reglas de composición (criterio NIST
800-63B). Validación con FluentValidation; el código de error es `validation.failed` con
`errors.password`.

## Tokens de email

- 32 bytes aleatorios, base64url en el link; en la base sólo el SHA-256.
- Vigencia: `verify` 24 h, `reset` 1 h. Un solo uso (`used_at`).
- Emitir un token nuevo de un propósito invalida los anteriores no usados del mismo usuario y
  propósito.
- Token inexistente, vencido o usado → 422 `identity.email_token.invalid`, sin distinguir cuál.

## Endpoints

Todos anónimos, bajo `/api/v1/auth`. Pasan por CSRF (`X-Qep-Client`) y CORS como el resto de
los mutantes. Rate limit nuevo `RateLimiterPolicies.Credentials`: **10 por minuto por IP y por
endpoint** (cada endpoint con su propio contador), más estricto que `Public` (120/min). Así
registrar → verificar → iniciar sesión desde una misma IP no compite por el mismo cupo.

Sólo es efectivo en producción, donde el navegador llama directo a `qep-api.qcode.co`. En local
y en los previews de Vercel el request llega por proxy y la IP es la del proxy. Se acepta.

### `POST /auth/password/register-tenant`

Cuerpo: **plano**, el mismo de `/auth/register-tenant` más `email` y `password`
(`{ email, password, displayName, slug, … }`). Plano a propósito: así los nombres de campo de
los errores de validación son los mismos que hoy y el frontend reusa su mapeo.
Respuesta: **202** `{ }` siempre que el cuerpo sea válido.

- Gate `Registration:PublicTenantSignupEnabled` igual que el registro con Google (mismo status y
  código cuando está apagado).
- **Antes de mirar si el email existe**, siempre y en este orden: validar el cuerpo (422
  `validation.failed`), comprobar que el slug esté libre (422 con el mismo código de dominio que
  hoy) y calcular el hash de la contraseña. Recién después se ramifica por email. Así ni el 422
  del slug ni el tiempo del PBKDF2 delatan si el email existe. Hace falta una consulta de
  disponibilidad de slug en Tenancy, aparte de la traducción del índice único (que sigue como
  red de seguridad por la carrera).
- **Email nuevo:** crea el usuario (`Active`, `email_verified_at null`), su credencial, el tenant
  y la membresía de owner, y emite un token `verify` con mail de verificación. No abre sesión.
- **Email existente** (de Google, invitado o con contraseña): **no** crea nada ni toca la
  credencial. Manda un mail «ya tienes una cuenta: inicia sesión o recupera tu contraseña» y
  responde el mismo 202.

Decisión del builder: crear el tenant al registrar, no al verificar, para no inventar una tabla
de registros pendientes que cruce Identity y Tenancy. El costo es que un registro sin verificar
reserva su slug. Se acepta y queda listado en «Pendiente».

`SDD-CT-07` aplica igual: aprovisionar el usuario y crear el tenant son unidades de trabajo
distintas. Este endpoint no la empeora ni la arregla; reusar el mismo orden que
`RegistrationEndpoints`.

### `POST /auth/email/verify`

Cuerpo: `{ token }`. Respuesta: **200** con el mismo cuerpo que `/auth/session` y cookie de
sesión.

Marca el email como verificado, consume el token, acepta las membresías `Invited`
(`MembershipActivationService`, igual que el login) y emite la sesión.

### `POST /auth/password/login`

Cuerpo: `{ email, password }`. Respuesta: **200** con el mismo cuerpo que `/auth/session` y
cookie.

- Email inexistente, sin credencial de contraseña o contraseña mala → **401**
  `identity.credentials.invalid`. Con email inexistente se corre igual una verificación contra
  un hash ficticio, para que el tiempo no delate.
- Contraseña correcta y `email_verified_at null` → **403** `identity.email.not_verified`, y se
  reemite el token `verify` con su mail. Sólo se revela con la contraseña correcta, así que no
  enumera.
- Correcta y verificado → acepta membresías `Invited`, emite la sesión y escribe auditoría de
  login como hoy.
- Este 401 **no** es «sesión vencida». El frontend lo mapea antes de que llegue al cliente
  consciente de sesión.

### `POST /auth/password/forgot`

Cuerpo: `{ email }`. Respuesta: **202** siempre.

Si el usuario existe, emite un token `reset` y manda el mail; si no, no hace nada. Vale para
usuarios con contraseña, sólo con Google o `Invited`: es como un invitado o un usuario de Google
**crea** su contraseña.

### `POST /auth/password/reset`

Cuerpo: `{ token, password }`. Respuesta: **204**.

Crea o reemplaza la credencial, consume el token, marca el email como verificado (el mail lo
probó), y revoca todas las sesiones del usuario (`RevokeAllForUserAsync`). No abre sesión: el
frontend manda al login.

Un usuario `Invited` sigue `Invited` hasta su primer login, igual que con Google. El login lo
activa (`ProviderLinkingService` hace hoy el `Activate`; el login con contraseña hace lo mismo).

### Invitaciones: aceptar con contraseña

El invitado puede aceptar desde `/invitations/{token}` con Google (como hoy) **o** con
contraseña. El token de invitación llegó al email invitado, así que prueba que es su dueño: no
hace falta un mail de verificación aparte.

- `GET /invitations/{token}` (anónimo, ya rate-limited) suma `hasPassword: bool`: si el usuario
  invitado ya tiene credencial de contraseña. Quien lo lee ya tiene el token, que nombra el
  email, así que no enumera nada nuevo.
- `POST /invitations/{token}/accept-with-password`, anónimo, con CSRF y la política
  `Credentials`. Cuerpo: `{ password }`. Respuesta: **200** con el mismo cuerpo que
  `/auth/session` y cookie.
  - Sin credencial → la crea (reglas de contraseña de siempre).
  - Con credencial → la verifica. Si no coincide → 401 `identity.credentials.invalid`. **Nunca**
    la reemplaza: quien olvidó la suya va a «olvidé mi contraseña».
  - En los dos casos: marca el email como verificado, acepta la invitación con la misma lógica que
    `POST /invitations/{token}/accept`, acepta las demás membresías `Invited` y emite la sesión.
  - Token de invitación inválido, vencido o ya aceptado → el mismo error que devuelve hoy
    `GET /invitations/{token}`.
- Vive en `src/Api`, junto a `InvitationEndpoints`, porque compone Tenancy (token, aceptación) e
  Identity (credencial, sesión), igual que `AuthSessionEndpoints`.

**Mail de invitación:** `InvitationEmailTemplate` sube de versión (`TemplateRef`) y el texto dice
que se puede aceptar con Google o creando una contraseña. El link no cambia.

## Composición compartida

`AuthSessionEndpoints.EstablishAsync` hace hoy «aceptar membresías invitadas + emitir sesión +
escribir cookie + armar el cuerpo». Eso sale a un helper que usan `/auth/session`,
`/auth/email/verify` y `/auth/password/login`, para que el cuerpo de respuesta no diverja.

## Mails

Por outbox y `IEmailChannel`, igual que `InvitationDeliveryWorker` + `InvitationEmailTemplate`.
Tres plantillas en español neutro, tuteando: verificar email, recuperar contraseña y «ya tienes
una cuenta».

**Base de los links:** clave nueva `Notifications:AppBaseUrl` (con su validador de opciones;
`http://localhost:3002` en `appsettings.json`). `Notifications:LoginUrl` no existe; sólo existe
`InvitationUrl` (`NotificationsOptions.cs:20-22`), y no se deriva de ella recortando
`/invitations`.

- `{AppBaseUrl}/verify-email?token=…`
- `{AppBaseUrl}/reset-password?token=…`
- `{AppBaseUrl}/login` y `{AppBaseUrl}/forgot-password` en el mail de cuenta existente.

**Mails sin tenant.** `Notification.TenantId` es un `Guid` obligatorio (`Notification.cs:37`) y
`OutboxDeliveryWorker.ResolveRecipientAsync` recibe un tenant. Estos tres mails no tienen
tenant (el usuario puede tener cero, uno o varios). Decisión: `Notification.TenantId` pasa a
nullable, con migración. Antes de cambiarlo hay que revisar todo filtro por tenant (consultas,
RLS si la hubiera) que lea esa tabla: un `null` no puede aparecer en el listado de ningún
tenant. Si esa revisión muestra que nullable rompe algo, la salida es un camino de entrega
propio de Identity sobre `IEmailChannel`, sin registro `Notification`, y se anota el desvío.

**El token crudo viaja en el payload del outbox.** Es el precedente: la invitación lo hace hoy
(`InvitationDeliveryWorker.cs:30`). Eso significa que el token está en texto plano en la base
mientras el outbox no se purgue, lo que contradice el tercer anti-objetivo. Se acepta en este
slice con la vigencia corta como mitigación (24 h y 1 h). Queda en «Pendiente».

## Auditoría

Eventos con el auditor de Identity: `password.registered`, `email.verified`,
`password.login.succeeded`, `password.login.failed` (sin el email crudo si no existe el
usuario), `password.reset.requested` y `password.reset.completed`. Nunca contraseña ni token.

## Pruebas (TDD, RED antes que GREEN)

- Unitarias de dominio: `MarkEmailVerified`, reglas del token (vencido, usado, invalidación del
  anterior).
- Integración (Testcontainers, como `SessionRevocationTests`), con
  `Authentication__UseDevelopmentStub=false` donde importe cookie o CSRF:
  - Registro → mail capturado (canal de log o fake de `IEmailChannel`) → verify → sesión válida
    en `/auth/me`.
  - Login antes de verificar → 403 `not_verified`; con contraseña mala → 401 idéntico al de
    email inexistente.
  - Registro con email existente → 202, sin tenant nuevo, sin cambio de credencial.
  - Forgot con email inexistente → 202, sin mail.
  - Reset → sesiones previas revocadas; login con la contraseña nueva funciona.
  - Invitado: forgot → reset → login → membresía `Active`.
  - Invitado sin contraseña: `accept-with-password` → credencial creada, email verificado,
    membresía `Active`, sesión válida. `GET /invitations/{token}` pasa a `hasPassword: true`.
  - Invitado con contraseña: la correcta acepta; la incorrecta da 401 y no cambia la credencial.
  - Token de invitación vencido → mismo error que el `GET`.
  - Snapshot de la plantilla de invitación nueva.
  - **El ataque**, en dos casos: registro con contraseña sin verificar seguido de (a) login de
    Google y (b) registro con Google, con el mismo email. En los dos, la contraseña del atacante
    deja de servir. Ver nota abajo.
  - Registro con slug tomado → mismo 422 con email nuevo y con email existente.
  - Rate limit: el 11.º request por minuto al mismo endpoint desde la misma IP → 429; otro
    endpoint desde esa IP sigue respondiendo.
  - Ningún mail sin tenant aparece en consultas filtradas por tenant de Notifications.
  - Borrar al usuario huérfano arrastra credencial y tokens.
- `IdentityLayerTests`: Application no referencia `Microsoft.AspNetCore.Identity`.

Nota sobre el ataque: si el atacante registra primero, la víctima entra después con Google y
se vincula a ese usuario. La contraseña del atacante sigue ahí, sin email verificado… hasta que
el login de Google llama a `MarkEmailVerified`. **Al vincular Google a un usuario cuyo email no
estaba verificado, se borra su credencial de contraseña** y se invalidan sus tokens. El tenant
que creó ese registro queda con la víctima como owner (es su usuario) y el atacante sin forma de
entrar. Es raro pero real; el borrado de la credencial es obligatorio y su prueba también.

## Pendiente (no bloquea este slice)

- Bloqueo por cuenta (no sólo por IP) ante fuerza bruta distribuida.
- Limpieza de registros nunca verificados (usuario + tenant) y de sus slugs reservados.
- Cambiar la contraseña con sesión iniciada.
- Tokens crudos en el payload del outbox (invitaciones incluidas): purgar el payload después
  de entregar, o pasar el token por otro canal.
- Corregir el `CLAUDE.md` raíz, que menciona `Notifications:LoginUrl`, una clave que no existe.
