# Onboarding de distribuidor por link — diseño (backend)

Fecha: 2026-11-19. Estado: borrador para aprobación del owner. Spec hermano pendiente en `qep-frontend` (pantalla pública
de la invitación). Reabre y **reemplaza** dos decisiones del spec `2026-11-18-invitacion-distribuidor-whatsapp-design.md`:
«el correo es la identidad y el link la prueba» y «el WhatsApp es un aviso sin link».

## Intención

Hoy el distribuidor invitado tiene que coincidir con el correo de la invitación, y para aceptar con un tenant nuevo escribe
nombre, slug, cultura, zona horaria y formato de fecha. Para vincular un tenant que ya tiene debe ser su **owner**; un miembro
con otro rol (p. ej. billing) no puede.

El owner quiere un onboarding corto: el mayorista crea el distribuidor, el sistema genera un link, el mayorista lo manda por
el medio que quiera, y quien lo abre queda vinculado con el mínimo de pasos. **El link identifica al distribuidor y al
mayorista**; ya no es una prueba de buzón.

## Objetivos

1. **Link copiable siempre.** La pantalla del mayorista muestra «Copiar» tenga o no una conexión de WhatsApp. El link también
   puede salir por plantilla de WhatsApp desde una conexión lista del mayorista (evento nuevo, **con** link).
2. **Distribuidor nuevo (sin cuenta):** el link crea su tenant con los datos que escribió el mayorista. El distribuidor sólo
   escribe su contraseña, **confirma su correo** y **marca la aceptación de términos**. Queda vinculado.
3. **Distribuidor con cuenta:** inicia sesión y queda vinculado. No se actualiza nada de ningún lado, salvo el caso del
   correo distinto (objetivo 4).
4. **Correo distinto:** si el correo que confirma el distribuidor es distinto al que escribió el mayorista (cuenta nueva o
   cuenta existente), se actualiza el correo en el registro del distribuidor del mayorista y se **avisa** a las dos partes.
5. **Con sesión iniciada, la pantalla dice en qué organización está.** Si no es owner ni admin de ese tenant, avisa que un
   administrador debe aceptar la invitación y ofrece: **cambiar de organización** (otro tenant de su cuenta donde sea owner o
   admin, o cerrar sesión para entrar con otra cuenta) o **crear un tenant nuevo** con los datos del mayorista y la cuenta
   que ya tiene.
6. **Vincular un tenant existente exige ser owner o admin** (rol `admin`, `SystemRoleKeys.Admin`), no sólo owner.

## No objetivos

- Defaults de cultura, zona horaria y formato de fecha por país. Se usan los mismos del registro público
  (`es-CO`, `America/Bogota`, `dd/MM/yyyy`, `RegistrationEndpoints.cs`) y el tenant los cambia en sus ajustes.
- Otros canales de envío desde el sistema (correo transaccional aparte, SMS).
- Cambiar el modelo del vínculo (`Distributor`, `LinkedTenantId`, cliente en el mayorista, proveedor en el distribuidor).
- Tocar el flujo de invitación de **miembros**.

## Anti-objetivos

- Que un fallo a mitad de la aceptación deje la invitación gastada y la relación a medias. Se conserva la regla de
  `mayorista-compras.md`: pasos idempotentes y la invitación se consume en el último commit, el de `wholesale`.
- Que el correo del mayorista se actualice **antes** de que el distribuidor demuestre que controla el correo nuevo.
- Que un link reenviado o filtrado le dé la relación a quien no debía **sin que el mayorista lo pueda cortar**: el link sigue
  siendo de un solo uso, vence a las 72 h y «Reenviar» lo rota.
- Que el token plano se guarde o se registre en logs.

## Restricciones verificadas en el código

- El token se guarda **hasheado** (`InvitationTokens.HashOf`, SHA-256) y vence a las 72 h (`Distributor.ResendInvitation`).
  El servidor no puede reconstruir un link ya enviado; sólo ve el hash.
- `GET /wholesale-invitations/{token}` es anónimo; `session` (Google) y `session-with-password` aprovisionan al usuario **por
  el correo invitado** (`GetOrProvisionInvitedUserAsync`). `accept` exige sesión y hoy pide `Mode` + `DisplayName` + `Slug`
  (o `TenantId`).
- `EnsureEmail` (`InvitedEmailMatches`) exige igualdad de correo en `ListLinkableTenantsAsync`, `AcceptAsync` y la sesión.
- `ListOwnedActiveTenantsAsync` une `tenants.owner_membership_id` con la membresía activa del usuario.
- El registro público **no** tiene aceptación de términos en el código (sólo Soporte la modela, para su formulario público).
- Identity tiene verificación de correo por token de un solo uso (`IEmailVerification`, `EmailTokenPurpose`).

## Enfoques

- **A. Evolucionar los endpoints existentes (elegido).** Se conservan `session`, `session-with-password` y `accept`, y la
  aceptación sigue en pasos idempotentes. Se relaja la exigencia de correo igual, se arma el tenant nuevo con los datos del
  mayorista, y se agregan la confirmación de correo, la aceptación de términos y la propagación del correo distinto.
- **B. Un endpoint único de onboarding que haga usuario, tenant y vínculo en una llamada.** Descartado: choca con la regla de
  no atomicidad entre módulos (un fallo a mitad dejaría la invitación gastada y la relación a medias, sin link para
  reintentar) y reescribe contratos que el frontend ya consume.

El owner dejó el cómo en manos de quien diseña; A es decisión de este diseño, no una aprobación explícita del owner.

## El diseño

### 1. El link y «Copiar»

- Como el token vive hasheado, **«Copiar» genera un link nuevo**: rota el token (vigencia nueva de 72 h) y devuelve la URL en
  la respuesta, una sola vez. El link anterior deja de funcionar. El frontend avisa esto antes de rotar si la invitación ya se
  mandó por otro medio.
- La respuesta de «invitar» también devuelve el link recién generado, para que el primer «Copiar» no rote nada.
- «Reenviar» sigue rotando y reenviando por los canales automáticos. Todo cambio de token es una sola operación del dominio
  (`ResendInvitation`), no una variante nueva.
- La URL se arma con `AccountLinks.WholesaleInvitation` (`Notifications:AppBaseUrl`), como hoy.

### 2. WhatsApp con link

- Evento nuevo en el outbox con el link, consumido en Mayorista con inbox propio, enviando por la conexión elegida con una
  plantilla nueva aprobada. **Reemplaza** el aviso sin link: el aviso actual (`wholesale.distributor-invitation-notice.v1`) y
  su estado en el detalle se conservan hasta que el owner confirme que se retiran.
- El token **viaja sólo en el payload del outbox**; `SensitivePaths.Mask` sigue tapándolo en logs y en spans. Cuidado nuevo: el
  link ahora llega a Mensajería como contenido de un mensaje saliente; el mensaje guardado debe enmascarar el token.

### 3. Qué ve quien abre el link

`GET /wholesale-invitations/{token}` sigue anónimo y devuelve mayorista, razón social, contacto, estado y vigencia. Además
devuelve el correo **completo**, como hoy (D-20): el flujo del owner lo necesita para precargar el formulario, y enmascararlo lo
rompería. El costo es que quien tenga el link ve el correo que escribió el mayorista; el link ya iba a ese correo. Quien tiene
sesión ve, aparte, su tenant actual y sus tenants donde puede aceptar.

### 3b. Formulario «¿Con qué empresa le compras a …?» (decisión del owner, 2026-11-19, versión final)

El distribuidor **no vuelve a diligenciar** el nombre del tenant ni el identificador: llegan **prellenados y editables**. El
nombre es la razón social (`businessName`) tal como la escribió el mayorista, y el identificador es `suggestedSlug`, un campo
nuevo del `GET` que sale de `ITenantSlugSuggestions`. El slug se genera **sin la forma societaria del final del nombre**
(`SAS`, `S.A.S`, `SA`, `S.A`, `LTDA`, `LTA`, con o sin puntos y con coma antes: `Andina, S.A.S.` da `andina`); sólo cuenta una
palabra suelta al final, así que `Casas` o `Pesa` no pierden nada, y el nombre del tenant conserva la forma societaria.

`accept` recibe `DisplayName` y `Slug` **opcionales**. Con ellos se crea el tenant tal cual (el nombre recortado); sin ellos,
con la razón social y el slug sugerido. Si el slug es el sugerido y otra aceptación lo toma en una carrera, el servidor
reintenta con otro sufijo (hasta 3 veces). Si el slug lo escribió el distribuidor y está tomado, no se reemplaza solo: responde
422 con `tenancy.slug.taken` para que lo corrija, y la invitación sigue pendiente. El formato lo sigue validando Tenancy.
Esto reemplaza dos decisiones del mismo día (primero «prellenado y editable», luego «sin campos»).

**Versión de los términos.** El `GET` de la invitación devuelve también `termsVersion`: la versión vigente
(`Terms:CurrentVersion`), o nula si no hay una configurada. El frontend no puede conocerla de otro modo; la devuelve al crear
la cuenta (`session-with-password`) o el tenant (`accept`). Con `termsVersion` nulo la pantalla no ofrece crear cuenta nueva
(el backend falla cerrado con `identity.terms.unavailable`).

### 4. Distribuidor nuevo

1. El distribuidor escribe un correo (precargado con el del mayorista), contraseña y marca los términos.
2. Se manda un código de un solo uso a **ese** correo. No sirve `EmailTokenPurpose`: `identity.email_tokens` cuelga de un
   usuario (`TryConsumeAsync` devuelve el `userId`) y aquí todavía no hay usuario. Identity guarda el desafío aparte, por
   correo (hash del código, intentos, vigencia). Hasta que no se confirma no se crea usuario, tenant ni vínculo, y no se toca
   el correo del mayorista.
3. El código se consume **en la misma llamada** que crea el usuario y la contraseña (`session-with-password`), de modo que no
   hay prueba que guardar entre pasos. Si algo falla después, el usuario ya existe y el reintento es un inicio de sesión
   normal con su contraseña; no hace falta otro código. Luego, la aceptación idempotente: usuario (`GetOrProvisionInvitedUserAsync` con el correo
   **confirmado**), tenant reservado (`RegisterInvitedTenantAsync`) con `DisplayName` = razón social, `Slug` generado de la
   razón social con sufijo numérico si está tomado, y los defaults del registro público; proveedor, cliente y commit final.
4. La aceptación de términos se **registra** con quién, cuándo y qué versión (ver «Pendiente del owner»).

### 5. Distribuidor con cuenta

Inicia sesión (Google o contraseña) y acepta. Para vincular un tenant existente, la membresía activa del usuario en ese tenant
debe ser owner (`owner_membership_id`) **o** tener el rol `admin`. `ListLinkableTenantsAsync` pasa a listar esos tenants y marca
los demás como no vinculables con motivo. `LinkTenantChoice` se valida con la misma regla (hoy `NotTenantOwner`); el código de
error se conserva para no romper al frontend.

### 6. Correo distinto

- Comparación con el correo del registro del distribuidor, normalizado como `Distributor.Email`. Igual: no se escribe nada.
- Distinto: el dominio expone una operación que cambia `Distributor.Email`, audita el cambio (con ids, no con los correos
  completos) y publica un evento por outbox. Un consumidor manda un correo de aviso al correo anterior y al nuevo.
- Se aplica **en el mismo commit final** de la aceptación, nunca antes.

### 7. Cuenta con sesión sin permiso

La pantalla muestra el tenant actual y el aviso. «Cambiar de organización» lista los tenants de la cuenta donde puede aceptar
o cierra sesión. «Crear un tenant nuevo» usa el camino del distribuidor nuevo, pero sin contraseña ni código: el correo ya es
el de la sesión, ya verificado. Los términos también se marcan.

## Decisión del owner sobre el correo distinto (2026-11-19)

Prevalece el correo que ingresa el distribuidor. Se escribe en el registro del mayorista **sin aprobación** y sólo con aviso a
las dos partes. No hay estado «pendiente» ni pantalla de aprobación. El riesgo de un link filtrado queda cubierto por: un solo
uso, vigencia de 72 h, rotación por el mayorista, la auditoría del cambio y el aviso al correo anterior.

## Decisiones de diseño (criterio de quien diseña, no del owner)

Salen de la revisión del diseño; el owner puede cambiarlas.

- **Cuenta existente y correo distinto:** iniciar sesión basta como prueba del correo de la cuenta. Identity no activa a un
  usuario hasta verificar su correo (`MarkEmailVerified` + `Activate`), así que quien inicia sesión ya lo demostró. No hay
  segundo código. El correo nuevo es el de la cuenta que inició sesión.
- **Prueba del correo confirmado:** no hay prueba que guardar. El código se consume en la llamada que crea el usuario (ver §4);
  un reintento tras un fallo a mitad es un inicio de sesión normal. La aceptación sigue siendo idempotente sobre el tenant
  reservado.
- **Términos:** la aceptación es **por usuario** (quién, cuándo, qué versión), en Identity, también en el camino de «crear un
  tenant nuevo con tu sesión». La versión sale de configuración; sin ella configurada el flujo falla cerrado, no acepta con
  una versión inventada.
- **«Copiar» rota:** el token cambia sólo con una acción explícita del mayorista, queda auditado (`distributor.invitation_rotated`,
  sin token) y la respuesta lleva la URL una sola vez. El backend no decide si «ya se mandó»: el aviso es del frontend.
- **Slug:** se reutilizan las reglas del registro público (normalización de tildes, largo, `TenantSlugReservations`). El sufijo
  numérico lo resuelve `EnsureRegistrableAsync`; si dos aceptaciones compiten, gana la que reserva y la otra reintenta.
- **«Admin» para vincular:** sólo el rol de sistema `admin` en una membresía **activa** de ese tenant, además del owner. Un rol
  personalizado con permisos parecidos no cuenta; suspendidas o pendientes tampoco. Es una consulta nueva de Tenancy (junto a
  `TenantOwnershipQuery`), no un cruce directo con Authorization.
- **Cuenta sin contraseña o sin usuario siempre exige el código.** Con el link como única llave, sin esto bastaría el link más
  el correo de otra persona para fijarle una contraseña a su cuenta. Sólo quien ya tiene contraseña entra sin código.
- **WhatsApp con link:** hereda de la conexión elegida las reglas de ventana y consentimiento que ya aplica el envío de
  plantillas. El token no puede guardarse en el mensaje de Mensajería; cómo se enmascara al guardar es trabajo del plan y puede
  salir como ítem aparte.

## Pendiente del owner

- **Términos:** el texto, la URL y la versión que se registra. El código no tiene aceptación de términos en el registro, así que
  esta es una pieza nueva, y el dónde guardarla (Identity, Tenancy) lo decide el diseño del plan.
- **Retirar el aviso sin link** una vez que el WhatsApp con link esté en producción.
- **Rastro de los términos al purgar un usuario:** `identity.terms_acceptances` tiene `ON DELETE CASCADE` hacia
  `identity.users`, así que purgar un usuario (por ejemplo, `OrphanUserCleanupWorker`) borra el registro de quién aceptó qué
  versión de los términos. Mantenerlo así o cambiarlo (conservar la fila sin FK, o anonimizarla) lo decide el owner.

## Lo que queda al constructor

Nombres de endpoints y DTOs, forma exacta del código de confirmación (largo, vigencia, intentos), la migración que agrega lo
de términos, la plantilla de WhatsApp, y el texto de los avisos de correo cambiado (en español colombiano, tuteando).

## Pruebas

- Aceptar con correo igual, con correo distinto (cuenta nueva y cuenta existente) y con link vencido, usado o rotado.
- El correo del mayorista **no** cambia si falla el código, la sesión o el commit; un reintento termina sobre el mismo tenant.
- Un miembro con rol `billing` no puede vincular ese tenant; uno con rol `admin` sí; el owner sigue pudiendo.
- «Copiar» rota el token y devuelve la URL; el link anterior responde 404; el token plano no aparece en logs ni en
  `platform.request_failures`.
- Con y sin conexión de WhatsApp, el link se puede copiar.
