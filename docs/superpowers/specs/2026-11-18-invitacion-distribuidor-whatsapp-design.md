# Aviso por WhatsApp de la invitación de distribuidor — diseño (backend)

> **Reemplazado en lo del link y la identidad por
> [`2026-11-19-onboarding-distribuidor-por-link-design.md`](2026-11-19-onboarding-distribuidor-por-link-design.md).** El link ya
> no prueba el buzón, el correo de quien acepta no tiene que ser el invitado, «Copiar» rota el token y hay un evento de WhatsApp
> con el link. Lo de este spec sobre el aviso sin link sigue vigente hasta que el owner confirme que se retira.

Fecha: 2026-11-18. Estado: listo para aprobación del owner. Spec hermano:
`qep-frontend/docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md`.

## Intención

La invitación de distribuidor sigue llegando **por correo**, con el link, como hoy (plan
`2026-11-17-mayorista-fase-2-backend.md`): el correo es la identidad y el link prueba el buzón. El owner quiere que, si el
mayorista tiene WhatsApp conectado, pueda **avisarle también por WhatsApp** que le llegó la invitación, por si el correo cae en
spam.

## Objetivos

- Campo opcional `phone` en la invitación. Si viene, además del correo sale un **aviso por WhatsApp** desde una conexión del
  mayorista con la plantilla de aviso aprobada. Con varias conexiones listas, el mayorista elige desde cuál.
- El aviso **no lleva el link ni el token**. Dice que fue invitado como distribuidor en Lulo CRM, a qué correo le llegó la invitación y que revise spam si no la encuentra.
- El aviso queda como una **conversación normal** en la bandeja de Mensajería del mayorista (decisión del owner, 2026-11-18):
  si el distribuidor responde, lo atiende la casa.
- «Reenviar invitación» manda el correo nuevo y, si hay teléfono, un aviso nuevo.
- El mayorista ve en el detalle si el aviso salió o falló, y por qué.

## No objetivos

- Invitar por WhatsApp (link por WhatsApp). Lo descartó el owner: el link en un celular que escribe el mayorista no prueba el
  correo y obligaba a reglas de seguridad nuevas en Identity.
- Cambiar la página pública de aceptación, Identity o la verificación de correo.
- Otros canales.

## Anti-objetivos

- Que el token o el link viajen por WhatsApp o lleguen a Mensajería.
- Que un fallo del aviso (Meta caído, plantilla sin aprobar) frene, retrase o cambie el correo o la invitación.

## Enfoques considerados

Que el WhatsApp sea un aviso sin link es decisión del owner (antes se diseñó la invitación por WhatsApp con link; se descartó).
Para mandar el aviso:

1. **Outbox, como el correo (elegido).** El commit de la invitación escribe un evento propio, sin token, y un worker manda la
   plantilla. Meta caído se reintenta; la invitación nunca depende del aviso.
2. **Envío sincrónico en el `POST`.** Error inmediato, pero amarra el alta a Meta. Descartado.
3. **Mensaje libre sin plantilla.** Meta no lo permite fuera de la ventana de 24 h. Descartado.

## Diseño

### 1. Plantilla de sistema `WholesaleInvitationNotice`

- `MessageTemplateKind.WholesaleInvitationNotice`, al final del enum. Se guarda por nombre: revisar el largo de la columna
  (precedente: el `varchar(16)` que rompió la plantilla formal de Soporte y la migración `WidenMessageTemplateKind`).
- Aprovisionamiento calcado de `OrderTemplateProvisioning` (borrador, versión `_vN`, adopción de una externa con el mismo
  nombre), por conexión, con el mismo permiso que la de pedido (`POST …/template-management/wholesale-invitation-notice`).
- Categoría `UTILITY`, idioma `es`, **sin botones ni encabezado**. Variables con nombre: `{{nombre_contacto}}`,
  `{{mayorista}}` (nombre del tenant), `{{correo}}` (el correo de la invitación, **completo**: decisión del owner, 2026-11-18,
  para que el distribuidor sepa exactamente en qué buzón buscar; se acepta que un número equivocado lo vea). Sin vencimiento:
  el aviso sólo dice que fue invitado y dónde buscar (owner, 2026-11-18). El producto se nombra «Lulo CRM».
  Texto base: «¡Hola, {{nombre_contacto}}! {{mayorista}} te invitó como distribuidor en la plataforma Lulo CRM. Te enviamos
  la invitación al correo {{correo}}. Si no la encuentras en tu bandeja de entrada, revisa la carpeta de spam o correo no
  deseado.» Termina en texto, no en variable (Meta rechaza las que terminan con una; regla de
  `SupportReplyTemplateDefinitionTests.cs:53`).
- `IWholesaleInvitationNoticeReadiness.FindApprovedAsync(tenant, connection)`, como `IBillingTemplateReadiness`.

### 2. Contrato HTTP

- `POST /wholesale/distributors` y `PUT /{id}` (sólo `Invited`) aceptan `phone: string | null` y
  `whatsAppConnectionId: Guid | null`. Teléfono normalizado como en Clientes/Mensajería (E.164; país del tenant por defecto).
  Claves del 422: `Phone`, `WhatsAppConnectionId`. Sin `phone`, `whatsAppConnectionId` se ignora.
- Resolución de la conexión (sólo con `phone`): sin `whatsAppConnectionId`, si hay **una** conexión lista se usa esa; si hay
  varias → 422 `wholesale.distributor.whatsapp_connection_required`; si no hay ninguna → 422
  `wholesale.distributor.whatsapp_unavailable`. Un `whatsAppConnectionId` que no esté lista da el mismo `whatsapp_unavailable`.
- `PUT` con teléfono o conexión distintos **no** avisa: el aviso nuevo sale con «Reenviar».
- `GET /wholesale/distributors/options` agrega `whatsAppConnections: { id, label }[]`: las conexiones `whatsapp-cloud`
  activas del tenant con la plantilla `WholesaleInvitationNotice` aprobada. Vacía ⇒ la pantalla no ofrece el aviso.
- `DistributorDetail` agrega `phone` e `invitation.whatsAppNotice: { status: 'Pending' | 'Sent' | 'Failed', failureCode:
  string | null, connectionLabel } | null` (`null` sin teléfono). `DistributorSummary` agrega `phone`.
- Reenviar: rota el token y manda el correo como hoy. Si hay teléfono, el aviso vuelve a `Pending` y sale otra vez. Si la
  conexión guardada ya no está lista, el correo sale igual y el aviso queda `Failed` con `whatsapp_unavailable` (el reenvío no
  falla por el aviso).

### 3. Envío

- Con `phone`, el commit de invitar/reenviar escribe, **además** del evento del correo de hoy, un evento
  `wholesale.distributor-invitation-notice.v1` **sin token**: `tenantId`, `distributorId`, `phone`, `connectionId`, el miembro
  que invitó, `contactName`, el correo y la `generation` de la invitación.
- `generation` es una columna nueva de la invitación (filas existentes: `0`). Sube en cada reenvío y en cada `PUT` que cambie
  teléfono o conexión, así un resultado tardío nunca pisa el estado actual.
- Un worker en `Bootstrapper` manda la plantilla. Va ahí porque es el único lugar que puede tocar Wholesale, Mensajería e
  Integrations (precedente `ServiceContractsMetaWhatsAppSender`). Busca la plantilla aprobada y manda por un
  `I…TemplateOutbound` de Mensajería con `CustomerId: null`, que abre o reusa la conversación por teléfono (`OpenByPhoneAsync`)
  como en cobros: asignación, no leídos y bots como cualquier otra.
- El worker devuelve el resultado a Wholesale por un puerto (`RecordInvitationNotice(distributorId, generation, status,
  failureCode)`), y Wholesale ignora el resultado de una `generation` vieja. Los códigos son los de cobros:
  `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>`.
- El teléfono no se loguea.
- Auditoría: `wholesale.distributor.invited` e `invitation_resent` agregan `whatsAppNotice: true|false` a los datos. Sin acción
  nueva.

### 4. Errores nuevos

| Status | Código |
| --- | --- |
| 422 | `wholesale.distributor.whatsapp_connection_required`, `wholesale.distributor.whatsapp_unavailable` |

## Pruebas clave

- Con `phone`: salen el evento del correo (como hoy) y el del aviso. El del aviso **no contiene el token** (se busca el token
  plano en el payload).
- Sin `phone`: sólo el correo (regresión de lo de hoy).
- El aviso falla (plantilla sin aprobar, Meta caído): la invitación y el correo no cambian, y el detalle muestra `Failed` con
  el código.
- Reenviar con teléfono: correo nuevo y aviso nuevo; con la conexión ya no lista, correo sí y aviso `Failed`.
- Un resultado del worker de una `generation` vieja no pisa el estado.
- Con varias conexiones listas y sin `whatsAppConnectionId` → 422.

## Lo que queda al builder

- Nombres de clases y la migración, incluidos el `CHECK` y el largo de `kind`.
