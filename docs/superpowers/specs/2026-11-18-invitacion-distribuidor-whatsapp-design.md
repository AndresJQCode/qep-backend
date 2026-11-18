# Invitación de distribuidor por WhatsApp — diseño (backend)

Fecha: 2026-11-18. Estado: listo para aprobación del owner. Spec hermano:
`qep-frontend/docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md`.

## Intención

Hoy el mayorista invita a un distribuidor y el link le llega **sólo por correo** (plan `2026-11-17-mayorista-fase-2-backend.md`).
El owner quiere que el mayorista **elija el canal** de la invitación: correo o WhatsApp (y, más adelante, otros canales). Llena
el mismo formulario de hoy; si elige WhatsApp escribe el celular y, si tiene varios números conectados, elige desde cuál sale. Al
distribuidor le llega el link por ese canal, lo abre, crea su contraseña (o entra con Google) y acepta.

## Objetivos

- La invitación tiene un `channel`: `Email` (el de hoy) o `WhatsApp`. Es uno solo; el modelo admite agregar canales después.
- Con `WhatsApp`: `phone` obligatorio y una conexión del mayorista con la plantilla de invitación aprobada (si tiene una sola,
  va implícita). El link sale por una plantilla de sistema aprobada por Meta, **sin correo**.
- El campo `email` sigue **obligatorio en los dos canales**: con `WhatsApp` no se le escribe, pero es la cuenta con la que el
  distribuidor entra y la aceptación se ata a él (§2).
- «Reenviar invitación» manda por el canal guardado, con el token nuevo. Editar (sólo `Invited`) puede cambiar canal, teléfono
  y conexión.
- El mayorista ve en el detalle si el WhatsApp salió o falló, y por qué.

## No objetivos

- Mandar por los dos canales a la vez.
- Mandar el link a mano desde el celular del mayorista (`wa.me`): descartado por el owner.
- Instagram u otros canales en este corte.
- Cambiar la página pública de aceptación más allá de lo que exige la regla de seguridad (§2).

## Anti-objetivos (sería un fracaso aunque funcione)

- Que el token plano quede guardado en una tabla, en un log o **en la bandeja de Mensajería** del mayorista, a la vista de sus
  asesores.
- Que quien reciba el WhatsApp (el número lo escribe el mayorista; puede estar equivocado o ser el suyo) pueda **ponerle
  contraseña a un usuario de QEP que ya existe**: el usuario es global, y eso es tomar su cuenta en todos sus tenants.
- Que un Meta caído deje la invitación a medias o bloquee el `POST` de invitar.

## Enfoques considerados

Elegir el canal (correo **o** WhatsApp, no los dos) es pedido explícito del owner. Para el envío por WhatsApp:

1. **Outbox, como el correo (elegido).** El commit de la invitación escribe en el outbox y un worker manda la plantilla. El
   token sigue viajando sólo en el payload del outbox (regla vigente del plan fase 2), un Meta caído se reintenta y la invitación
   nunca queda a medias. Costo: el resultado no se ve en el acto; el detalle lo muestra después.
2. **Envío sincrónico en el `POST`.** Da el error inmediato, pero el token pasa por el handler, amarra el alta a Meta y deja
   invitaciones guardadas sin mensaje si Meta falla después del commit. Descartado.
3. **Mensaje libre sin plantilla.** Meta no lo permite fuera de la ventana de 24 h, y un distribuidor nuevo nunca escribió.
   Descartado.
4. **`wa.me` desde el celular del mayorista.** Barato, pero el token pasa por su navegador y no queda registro. Descartado por
   el owner.

## Diseño

### 1. El canal vive en la invitación

`wholesale.distributor_invitations` agrega `channel` (`email` | `whatsapp`, con `CHECK`; las filas existentes quedan `email`),
`phone` y `connection_id` (nulos con `email`). Un solo token, como hoy. Las rutas públicas leen el canal de la invitación.

Por qué: el link por correo prueba el buzón; el de WhatsApp prueba sólo el número que escribió el mayorista. La regla de §2
restringe sólo lo que el WhatsApp no prueba y deja el correo intacto.

### 2. Regla de seguridad del canal WhatsApp

Con una invitación de canal `WhatsApp`:

- `POST …/session` (Google): igual que hoy. Google prueba el correo.
- `POST …/session-with-password` **nunca marca el correo verificado** con canal `WhatsApp` (el link no prueba el buzón):
  - el correo **no tiene usuario** en Identity → crea usuario y contraseña, sin verificar, con gracia (abajo);
  - el correo tiene usuario **con contraseña** → la verifica como hoy; si el correo seguía sin verificar, recibe la gracia;
  - el correo tiene usuario **sin contraseña** (sólo Google) → **403 `wholesale.invitation.password_not_allowed`**, sin tocar
    nada. Ese usuario entra con Google.
- `InvitationSummary` agrega `canCreatePassword: boolean` (BFF: la pantalla no deduce la regla). Con canal `Email` es `true`;
  con `WhatsApp` es `false` cuando el correo ya existe sin contraseña.

**Gracia para verificar el correo** (decisión del owner, 2026-11-18: sólo para distribuidores invitados por WhatsApp, nunca
global):

- `identity.users` agrega `email_verification_grace_until` (nula). La fija **sólo** este flujo, en la misma operación que crea o
  verifica la credencial: ahora + 7 días. Ningún otro camino la escribe.
- Al fijarla se emite `EmailVerificationRequested` (el correo de verificación que ya existe, `EmailVerification.cs`).
- `PasswordLogin.cs:92-103`: con correo sin verificar y `email_verification_grace_until` en el futuro, el login **pasa** y vuelve
  a emitir el correo de verificación como mucho una vez por día (el builder elige cómo limitarlo). Vencida la gracia, la regla de
  hoy: 403 `email_not_verified` y correo nuevo.
- `/auth/me` agrega `emailVerificationDueAt` (nulo si verificado o sin gracia) para que el SPA muestre el aviso.
- Verificar el correo (`MarkEmailVerified`, por cualquier camino) limpia la gracia.
- `VerifiedProviderLinker` no cambia: si durante la gracia vincula Google, la credencial se borra y entra con Google.
- Riesgo aceptado: con un número equivocado, un tercero puede crear la cuenta de un correo que no existía en QEP y usarla hasta
  7 días; el dueño real la recupera con «olvidé mi contraseña».

### 3. Plantilla de sistema `WholesaleInvitation`

- `MessageTemplateKind.WholesaleInvitation`, al final del enum. Se guarda por nombre: hay que revisar el largo de la columna
  (precedente: el `varchar(16)` que rompió la plantilla formal de Soporte, y la migración `WidenMessageTemplateKind`).
- Aprovisionamiento calcado de `OrderTemplateProvisioning` (borrador, versión `_vN`, adopción de una externa con el mismo
  nombre), por conexión, con el mismo permiso que la de pedido (`POST …/template-management/wholesale-invitation`).
- Categoría `UTILITY`, idioma `es`. Variables con nombre: `{{nombre_contacto}}` (contacto del distribuidor), `{{mayorista}}`
  (nombre del tenant) y `{{vencimiento}}` (fecha y hora con el formato regional del tenant). Texto base propuesto: «¡Hola,
  {{nombre_contacto}}! {{mayorista}} te invita a comprarle como distribuidor en QEP. Abre el enlace para crear tu acceso.
  Vence el {{vencimiento}}.»
- Un botón URL dinámico: `{AppBaseUrl}/wholesale-invitations/{{1}}`, con el token como sufijo. Es la misma base que usa el
  correo (`AccountLinks.WholesaleInvitation`, `WholesaleInvitationDeliveryWorker`); la clave `Notifications:WholesaleInvitationUrl`
  que nombra el plan de la fase 2 no existe. La base se fija al aprovisionar (es por ambiente); cambiarla exige una plantilla
  nueva.
- `IWholesaleInvitationTemplateReadiness.FindApprovedAsync(tenant, connection)`, como `IBillingTemplateReadiness`.

### 4. Contrato HTTP

- `POST /wholesale/distributors` y `PUT /{id}` (sólo `Invited`) aceptan `channel: 'Email' | 'WhatsApp'` (ausente ⇒ `Email`,
  para que un cliente viejo siga funcionando), `phone: string | null` y `whatsAppConnectionId: Guid | null`. Con `Email`,
  `phone` y `whatsAppConnectionId` se ignoran. Con `WhatsApp`, `phone` es obligatorio y se normaliza como en
  Clientes/Mensajería (E.164; país del tenant por defecto). Claves del 422: `Channel`, `Phone`, `WhatsAppConnectionId`.
- Resolución de la conexión (sólo con `WhatsApp`): sin `whatsAppConnectionId`, si hay **una** conexión lista se usa esa; si
  hay varias → 422 `wholesale.distributor.whatsapp_connection_required`; si no hay ninguna → 422
  `wholesale.distributor.whatsapp_unavailable`. Un `whatsAppConnectionId` que no esté lista da el mismo `whatsapp_unavailable`.
- Cambiar el canal con `PUT` **no** reenvía: para mandar por el canal nuevo se usa «Reenviar». El builder confirma que hoy
  editar el correo tampoco reenvía, y mantiene el mismo criterio.
- `GET /wholesale/distributors/options` agrega `whatsAppConnections: { id, label }[]`: las conexiones `whatsapp-cloud`
  activas del tenant con la plantilla `WholesaleInvitation` aprobada. Vacía ⇒ la pantalla no ofrece WhatsApp.
- `DistributorDetail` agrega `phone`, `invitation.channel` e `invitation.whatsApp: { status: 'Pending' | 'Sent' | 'Failed',
  failureCode: string | null, connectionLabel } | null` (`null` con canal `Email`). `DistributorSummary` agrega `phone` e
  `invitationChannel`.
- Reenviar rota el token y manda por el canal guardado. Con `WhatsApp` vuelve a `Pending`. Si la conexión guardada ya no está
  lista, responde 422 `whatsapp_unavailable` y no rota nada: el mayorista edita y elige otra conexión o el correo.

### 5. Envío

- Con canal `WhatsApp`, el commit de invitar/reenviar escribe en el outbox, **en vez del evento del correo**, un evento
  `wholesale.distributor-whatsapp-invited.v1` con el token, `phone`, `connectionId`, el miembro que invitó y la `generation`
  de la invitación (sube en cada reenvío).
- Un worker en `Bootstrapper` manda la plantilla. Va ahí porque es el único lugar que puede tocar Wholesale, Mensajería e
  Integrations (precedente `ServiceContractsMetaWhatsAppSender`). Busca la plantilla aprobada y manda por un
  `I…TemplateOutbound` de Mensajería con `CustomerId: null`.
- **La invitación queda como una conversación normal** en la bandeja del mayorista (decisión del owner, 2026-11-18): el envío
  abre o reusa la conversación por teléfono como en cobros (`OpenByPhoneAsync`), con asignación, no leídos y bots como cualquier
  otra. Si el distribuidor responde, lo atiende la casa.
- **El token no sale de este worker hacia ningún lado más que Meta**. Va tapado (`••••`) en todo lo que Mensajería guarda o
  emite del mensaje: la fila, los parámetros, la URL del botón, la vista previa de la conversación y los eventos de SignalR. El
  builder recorre esos caminos y deja una prueba que busca el token plano.
- `generation` es una columna nueva de la invitación (filas existentes: `0`). Sube en cada reenvío **y** en cada `PUT` que
  cambie el canal, el teléfono o la conexión, así un resultado tardío del canal anterior nunca pisa el estado actual.
- El worker devuelve el resultado a Wholesale por un puerto (`RecordWhatsAppDelivery(distributorId, generation, status,
  failureCode)`), y Wholesale ignora el resultado de una `generation` vieja. Los códigos son los de cobros:
  `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>`.
- Ni el token, ni el link, ni el teléfono se loguean.
- Auditoría: `wholesale.distributor.invited` e `invitation_resent` ya existen; agregan `channel` a los datos. Sin acción nueva.

### 6. Errores nuevos

| Status | Código |
| --- | --- |
| 403 | `wholesale.invitation.password_not_allowed` |
| 422 | `wholesale.distributor.whatsapp_connection_required`, `wholesale.distributor.whatsapp_unavailable` |

## Pruebas clave

- Canal `WhatsApp` + correo con usuario sólo-Google → 403, y nada cambia: ni credencial, ni verificado, ni invitación.
- Canal `WhatsApp` + correo nuevo → crea usuario y contraseña, correo **no** verificado, gracia de 7 días y correo de
  verificación emitido; aceptar funciona.
- Login con contraseña: sin verificar y con gracia vigente → pasa; con gracia vencida o sin gracia → 403 `email_not_verified`
  (regresión de lo de hoy para todo usuario que no vino de WhatsApp).
- Verificar el correo limpia la gracia.
- Canal `WhatsApp` no escribe el evento del correo; canal `Email` no escribe el de WhatsApp.
- `POST` sin `channel` ⇒ `Email`, comportamiento de hoy (regresión).
- Reenviar rota el token, y el viejo responde 404.
- Un resultado del worker de una `generation` vieja no pisa el estado.
- El mensaje guardado en Mensajería no contiene el token (se busca el token plano en la fila).
- Con varias conexiones listas y sin `whatsAppConnectionId` → 422.

## Lo que queda al builder

- Dónde y cómo se tapa el parámetro en Mensajería.
- Nombres de clases y la migración, incluidos el `CHECK` y el largo de `kind`.
- Qué implica «correo no verificado» en Identity (§2).
