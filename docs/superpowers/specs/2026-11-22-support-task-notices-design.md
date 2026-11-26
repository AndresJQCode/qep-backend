# Soporte: avisos de tareas asignadas (campana, WhatsApp, correo) — diseño (backend)

Fecha: 2026-11-22. Estado: **por aprobar**. Hermano de
`2026-11-22-support-internal-tasks-design.md` (las tareas; este spec cuelga del evento que ése deja) y
**dependiente** de `2026-11-22-in-app-notifications-backend-design.md` (la campana, otra sesión, también por
aprobar). Spec del frontend: `qep-frontend/docs/superpowers/specs/2026-11-22-support-task-notices-design.md`.

## 1. Intención

Que el miembro al que le asignan una tarea **o un ticket de cliente** (D-7) **se entere**, sin depender de
que tenga Soporte abierto. El owner (2026-11-22) pidió esta escalera, la misma para los dos:

1. **En la app**: una entrada en la campana, **siempre**, y en vivo por el hub (SignalR) si está conectado.
2. Si **no la leyó en 30 minutos y no tiene QEP abierto**: por **WhatsApp**. Mientras tenga QEP abierto
   no sale nada por fuera, aunque no la haya leído (owner, 2026-11-22, D-1).
3. Si no se puede por WhatsApp: por **correo**.

## 2. Metas, no metas y antimetas

**Metas**
- Una tarea asignada a otro miembro siempre deja una entrada en su campana.
- Una tarea que sigue sin leerse a los 30 minutos sale por WhatsApp **sólo si el miembro no tiene QEP
  abierto**; si lo tiene, se espera a que lo cierre (o la lea). Sin WhatsApp posible, por correo.
- El celular del miembro es **opcional**; lo carga él o quien administra miembros.

**No metas (v1)**
- Avisar al creador cuando la tarea se resuelve (queda en su lista «Creados por mí»).
- Recordatorios de tareas por vencer, y resúmenes diarios.
- Preferencias por tipo de aviso (la campana ya las excluye, su §3).
- Web Push con la pestaña cerrada.

**Antimetas**
- Que el **asunto o la descripción** de una tarea viaje por WhatsApp: un número mal escrito se lo
  mostraría a un tercero. El mensaje de WhatsApp es genérico.
- Que un aviso fallido deshaga o retrase la asignación. Un aviso es secundario, siempre.
- Que se avise dos veces por el mismo canal o que se escale una tarea que ya no es de ese miembro.

## 3. Restricciones verificadas en el código

- **Los miembros no tienen celular**: no hay campo de teléfono en Identity, Tenancy ni Authorization.
  Un miembro entra con su correo.
- **Soporte no tiene outbox**; el spec de la campana (§5.6) hace que Soporte mapee la proyección de
  `platform.outbox_messages`. Este spec **reusa ese mapeo**: depende de él.
- **La campana** (`notifications.in_app_notifications`): por miembro y tenant, con `read_at`,
  `resolved_at`, `dedupe_key`, retención de 90 días, y consumidores en `Notifications` drenados por
  `NotificationsDeliveryWorker`. Los `kind` son un contrato cerrado: este spec **suma uno**
  (`SupportTaskAssigned`). El frontend pinta un `kind` desconocido como genérico.
- **La entrega en vivo** es un grupo por miembro que el `AppHub` une en `OnConnectedAsync`
  (`notificationsChanged`). `AppHubConnections` es **en memoria por pod** y la presencia
  (`IRealtimePresence`) es por tópico y con TTL de 45 s: **no existe «¿este miembro está conectado?»**
  confiable entre réplicas. **Sí existe el patrón**: la presencia de la bandeja de Mensajería
  (`MessagingPresence` + `ValkeyMessagingPresence` + `PresenceHeartbeatWorker`, spec 2026-11-13 D-2) es un
  registro local por pod con latido de 30 s a un sorted set de Valkey por tenant y ventana de lectura de
  90 s. Este spec lo calca para el `AppHub` (§5.4, «Presencia»). El `AppHub` lo abre **todo miembro con
  tenant** (`RealtimeConnection` en `_authenticated.tsx`), así que «conectado al `AppHub`» = «tiene QEP
  abierto», en primer o segundo plano.
- **WhatsApp a un miembro** necesita una plantilla de sistema aprobada por Meta en una conexión del
  tenant. Precedente: `WholesaleInvitationNotice` (kind de sistema, creación por conexión con
  `POST …/template-management/<kind>`, worker en `Bootstrapper`, el fallo no frena la acción
  principal, errores `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>`).
  El `kind` de plantilla tuvo un `varchar(16)` que rompió una vez (Soporte formal): el nombre nuevo va
  corto y se verifica el ancho antes.
- **Correo**: `IEmailChannel` y plantillas en `Notifications.Application` (`SupportReplyEmailTemplate`
  etc.). El miembro siempre tiene correo.
- **HR**: `Employee` tiene `MemberId` (nullable) y `Phone` (texto, sin formato garantizado).
- Todo worker nuevo entra a `MultiReplicaInventory`, todo consumidor nuevo del outbox escucha
  `DoorbellQueues.Outbox`, y las migraciones son expand-only (reglas de la campana, §4).

## 4. Enfoques considerados

| | Enfoque | Veredicto |
|---|---|---|
| A | Escalar por **no leído a 30 min**, sin mirar si está conectado | reemplazado (owner, 2026-11-22, D-1) |
| B | Mirar presencia al asignar: desconectado ⇒ WhatsApp ya | descartado: un aviso externo a los segundos de asignar es ruido si iba a abrir QEP en un rato |
| **A+P** | **No leído a 30 min y, en ese momento, sin QEP abierto**; si lo tiene abierto, se vuelve a mirar cada minuto hasta que lo cierre o la lea | **elegido** |
| C | WhatsApp o correo inmediatos, sin campana | descartado: es lo que el owner quiso evitar |

**Por qué A+P.** El owner (2026-11-22): «si tiene el backoffice abierto no le notifique por canales
externos, sólo si no ha leído; si le creo un ticket, que le llegue a la campana». La presencia se mira **al
vencer**, no al asignar, porque es entonces cuando se decide salir por fuera. Un conectado que no lee **no**
escala mientras siga conectado: se enteró por la campana y el sonido. Si cierra QEP con la tarea sin leer,
escala en el siguiente minuto (más la ventana de 90 s de la presencia). Un desconectado escala a los 30
minutos, como antes.

## 5. Diseño

### 5.1 Flujo

```
Asignan la tarea ─► evento support.ticket.task-assigned.v1 (outbox de Soporte, misma transacción)
        │
        ▼  consumidor en Notifications
  1. entrada en la campana (kind SupportTaskAssigned) + notificationsChanged en vivo
  2. fila de escalada con vence_a = ahora + 30 min
        │
        ▼  worker de escalada, cada minuto, con lease
  ¿vence_a cumplido?  no ─► espera
  ¿la entrada sigue sin leer, sin resolver, y la tarea sigue siendo de ese miembro y abierta?
        no ─► se cancela (nada que avisar)
  ¿el miembro tiene QEP abierto (presencia del AppHub)?
        sí ─► se pospone 1 min (vence_a = ahora + 1 min), sin contar intento
        no ─► WhatsApp si el tenant eligió una conexión (/settings/support) con la plantilla
              aprobada y el miembro tiene celular válido
              si no se puede, o falla ─► correo
```

### 5.2 Evento

`support.ticket.task-assigned.v1` (`tenantId`, `ticketId`, `ticketNumber`, `assigneeMemberId`,
`assignedByMemberId`, `occurredAt`). Lo publica Soporte al **crear** una tarea y al **reasignarla**,
nunca cuando el responsable es quien la asigna (a uno mismo no se le avisa). Correlación: el ticket.

**Tickets de cliente (D-7):** `support.ticket.assigned.v1`, mismo payload, al asignar un ticket de cliente a
**otro** miembro. Tomarlo (a uno mismo) o dejarlo sin responsable no avisa. Y
`support.ticket.assignee-released.v1` (`…, formerAssigneeMemberId, reason, isInternal`) al reasignar,
tomar uno ajeno, resolver o cerrar, **para tareas y tickets de cliente**: cierra la entrada de quien lo
tenía.

### 5.3 Campana

- `kind` nuevo `SupportTaskAssigned` (tareas) y `SupportTicketAssigned` (tickets de cliente, D-7; mismo
  `data`, la pantalla cambia «la tarea» por «el ticket»), `data { ticketId, ticketNumber, actorName }`. `actorName` se
  resuelve al leer (como en `ConversationAssigned`). **Nunca el asunto.**
- `dedupe_key`: `support-task-assigned:{ticketId}:{id del mensaje del outbox}`, así dos reasignaciones
  del mismo ticket son entradas distintas y un reintento no duplica.
- Al **reasignarse o resolverse/cerrarse** la tarea, la entrada se marca resuelta y leída (mismo
  mecanismo que `WholesaleOrderPendingApproval`, su D-13): el worker lo verifica antes de escalar.
- «Leída» es `read_at` de la entrada, como lo define la campana. *Pendiente de coordinar con esa
  sesión:* abrir la tarea desde la campana debe marcarla leída; abrirla por otro camino (URL, bandeja)
  hoy no. Propuesta: el detalle de una tarea marca como leída la entrada de ese ticket para quien la
  abre (un endpoint o un parámetro en la ruta de lectura, no un acoplamiento Soporte → Notifications).

### 5.4 Escalada

- Tabla `notifications.task_notice_escalations`: `id`, `tenant_id`, `in_app_notification_id`,
  `ticket_id`, `member_id`, `due_at`, `status` (`Pending | Skipped | Whatsapp | Email | Failed`),
  `skip_reason`, `channel_attempts` (jsonb: canal, fecha, resultado), `claimed_until`, `created_at`.
  Índice único por `in_app_notification_id`: una escalada por entrada.
- Worker `TaskNoticeEscalationWorker`, hosted service, reclamo con lease (`FOR UPDATE SKIP LOCKED`),
  aislamiento por tenant y fila (un error se loguea y sigue), en `MultiReplicaInventory`.
  Espera configurable `Notifications:TaskNotice:EscalationAfterMinutes` (default **30**).
- **Comprobación al enviar**, no al encolar, por un puerto angosto `ITaskNoticeState` implementado en
  `Bootstrapper` (el único lugar que puede tocar Soporte): la tarea sigue abierta y el responsable
  sigue siendo ese miembro. La entrada leída o resuelta se lee de la propia tabla de la campana.
- **Presencia (D-1).** Después de la comprobación anterior, y sólo si se iba a enviar, el worker pregunta
  por un puerto angosto `ITaskNoticePresence.IsConnectedAsync(tenantId, memberId)` (implementado en
  `Bootstrapper`) si el miembro tiene QEP abierto. Si lo tiene, la fila **se pospone**: `due_at = ahora +
  1 min`, sin lease y sin contar intento (no agota `MaxAttempts`) y sin anotar nada en `channel_attempts`
  (crecería cada minuto). No hay tope: mientras siga conectado y sin leer, no sale nada por fuera.
  - El registro calca el de la bandeja: el `AppHub` anota la conexión en el registro local
    (`AppHubConnections`, que ya guarda tenant y miembro por conexión) y, con `Valkey:Areas:Presence`
    prendido, en un sorted set por tenant `qep:rt:online:{tenantId}` (miembro → segundo Unix del último
    latido). Un latido de 30 s por pod reescribe a sus conectados y recorta lo viejo; se lee «conectado»
    si el último latido cae dentro de 90 s. Al desconectar sólo se quita si el pod no le tiene otra pestaña.
  - **Sin Valkey, o si Valkey falla**, se lee el registro local del pod. Con varias réplicas eso puede
    decir «desconectado» de alguien conectado a otro pod: el resultado es el comportamiento anterior
    (escala a los 30 min). Falla hacia avisar, nunca hacia callar.
- **Elección de canal:** WhatsApp si (a) el tenant **configuró en `/settings/support`** la conexión
  de los avisos de tareas (§5.7) y ésta sigue activa y con la plantilla `TaskNotice` aprobada, (b) el
  miembro tiene celular válido E.164, propio o autocompletado de RR. HH. (§5.5). Sin conexión
  configurada nunca se usa WhatsApp, aunque el tenant tenga una. Si falta algo o el envío falla (`template_not_approved`,
  `connection_unavailable`, `send_failed`), **correo**. Un solo intento por canal; no hay reintento
  cruzado: si WhatsApp «se envió», no se manda además el correo.
- **Correo:** plantilla `SupportTaskAssignedEmailTemplate` en `Notifications.Application`, con el
  enlace a la tarea (`/support/{ticketId}`, armado como los otros enlaces de cuenta). El correo sí
  puede llevar el asunto: va al correo con el que el miembro inicia sesión.
- Un aviso externo **no** se reenvía ni se repite: una escalada, un canal.

### 5.5 Celular del miembro

- Columna opcional **`phone_e164`** en la membresía (Tenancy), no en el usuario: el celular de trabajo
  puede ser distinto en cada empresa. E.164 validado (`^\+[1-9]\d{7,14}$`, la misma regla de
  `TicketRequesterNormalizer`).
- **Dos puertas de entrada** (owner, 2026-11-22: «de su perfil, pero desde Miembros también se
  podría modificar»):
  1. **El propio miembro**, desde su perfil: `PUT …/memberships/me/phone` (sólo la membresía propia);
     `null` lo borra. 422 por formato.
  2. **Quien administra miembros**, desde Miembros: el celular se **suma al cuerpo de
     `PUT …/memberships/{id}/profile`** (el que ya edita nombre y código de asesor, permiso
     `advisorship.manage`, `If-Match`). Va en el mismo `PUT` porque dos `PUT` desde un diálogo
     chocarían por el `If-Match` (la razón que ya documenta ese endpoint). Ausente conserva lo
     guardado (un frontend viejo no lo manda); `null` lo borra.
- **Sin casilla de consentimiento** (owner, 2026-11-22). Un número guardado, por el miembro o por quien
  administra, **vale de inmediato** para los avisos: no hay estado «pendiente» ni columna de
  consentimiento. La pantalla sólo **informa** para qué se usa (texto de ayuda). Es una decisión del
  owner y, por ser la empresa quien contacta a su propio personal por un canal de trabajo, la
  responsabilidad sobre ese contacto es del tenant; queda anotado para que nadie la reabra por error.
- **Lectura:** `MembershipListItemResponse` y la membresía propia traen `phone` (E.164 o `null`).
  Quien administra ve el número; el resto de los miembros **no** ve el de otros (la lista de miembros
  que ven los demás no lo incluye). Auditoría: el cambio de celular nombra `phone` en `changedFields`
  sin guardar el número en el log.
- **Autocompletado desde RR. HH.** (owner, 2026-11-22): si el miembro **no tiene celular propio**, se
  usa el `Phone` de la ficha de **HR** vinculada (`Employee.MemberId`) y se muestra ya puesto en su
  perfil y en Miembros. Reglas:
  - **Se resuelve al leer, no se copia.** El teléfono efectivo es `phone_e164 ?? hrPhone`. Así no
    hay dos copias que se desincronicen cuando HR cambia el número o termina el contrato.
  - **El propio gana.** Guardar un celular propio lo pisa; borrarlo vuelve a mostrar el de HR.
  - Sólo con el módulo `hr` encendido para el tenant y una ficha vigente (no terminada).
  - `Employee.Phone` es texto libre sin formato garantizado: se **normaliza a E.164** (ya con `+`, o
    sólo dígitos del país del tenant cuando el prefijo se conoce con certeza, como un celular
    colombiano de 10 dígitos que empieza por 3). Si no se puede normalizar con certeza, **no se usa**
    y se cae a correo; nunca se adivina un número.
  - Lectura: `phone` es el efectivo y `phoneSource` (`Member | Hr | null`) dice de dónde sale; el
    SPA lo muestra, no lo deduce. Sólo `phone_e164` es editable por Miembros y por el perfil.
  - El cruce Tenancy ↔ HR vive en un puerto angosto (`IMemberPhoneDirectory`) implementado en
    `Bootstrapper`; ni Tenancy ni Notifications referencian a HR.

### 5.6 Plantilla de WhatsApp `TaskNotice`

Plantilla de sistema (categoría utilidad), por conexión, creada como la de invitación de distribuidor
(`POST …/template-management/task-notice`, `messaging.template.manage`, tarjeta en
`/integrations/whatsapp/$connectionId/templates`). Texto, con dos variables con nombre:
«¡Hola! {{asesor}} te asignó {{numero}} en QEP. Ábrelo para ver el detalle.» (genérico: sirve para tareas y tickets de cliente, D-7). **Sin asunto ni descripción.** El nombre
del `kind` es `TaskNotice` (10 caracteres): verifica el ancho de la columna antes de migrar.

### 5.7 Ajustes en `/settings/support`

Se suma a `SupportSettingsDto` (mismo `GET`/`PUT …/support/settings`, mismo `If-Match`, permiso
`support.settings.manage`) un bloque `taskNotice`:

```json
"taskNotice": {
  "whatsAppConnectionId": "uuid | null",
  "connections": [ { "connectionId": "uuid", "name": "string", "phoneNumber": "string | null", "templateStatus": "Approved | Pending | Rejected | Missing" } ],
  "escalationAfterMinutes": 30
}
```

- `null` (el valor por defecto) = **no se usa WhatsApp**: sólo campana y correo. Es lo correcto antes
  de que alguien lo configure.
- `connections` lista las conexiones de WhatsApp activas del tenant con el estado de su plantilla
  `TaskNotice`, así la pantalla explica por qué una no se puede elegir (BFF: el SPA no cruza con
  Integraciones). `escalationAfterMinutes` es informativo (el valor global, D-4); no se edita.
- `PUT` acepta `taskNotice` **opcional**: ausente o `null` conserva lo guardado (un frontend viejo no
  lo manda, mismo criterio que `unassignedAlert` de la campana); con valor viaja
  `{ whatsAppConnectionId }`. Errores: 422 `support.settings.task_notice_connection_invalid` si la
  conexión no es del tenant o no está activa. **Elegir una conexión sin plantilla aprobada se permite**
  (se aprueba después), y el worker cae a correo mientras no lo esté. Auditoría nombra `taskNotice` en
  `changedFields`.
- Precedente: la elección del canal de WhatsApp de cotizaciones y pedidos (se guarda por tenant).

## 6. Decisiones del owner (2026-11-22) — todas resueltas

- **D-1.** (Revisada el 2026-11-22; reemplaza «de momento no mirar presencia».) La campana llega
  **siempre**. WhatsApp o correo, **sólo** si a los 30 minutos la entrada sigue sin leer **y** el miembro
  no tiene QEP abierto; si lo tiene, se vuelve a mirar cada minuto (§4, §5.4 «Presencia»).
- **D-2.** Abrir la tarea, por cualquier camino, marca leída su entrada de campana (§5.3). Se
  coordina con la sesión de la campana.
- **D-3.** El celular de RR. HH. **se autocompleta** en el perfil del miembro si no tiene el suyo
  (§5.5), resuelto al leer.
- **D-4.** Los 30 minutos son un valor global de configuración, no por tenant.
- **D-5.** La conexión de WhatsApp de los avisos **se configura en `/settings/support`** (§5.7).
- **D-6.** Sin casilla de consentimiento: un número guardado vale de inmediato (§5.5).
- **D-7.** (2026-11-22, tarde.) Asignar un **ticket de cliente** a otro miembro avisa **igual que una
  tarea**: campana siempre y, a los 30 min sin leer y sin QEP abierto, WhatsApp o correo. El owner eligió
  esto sabiendo que son más mensajes (y más costo de Meta) que con sólo campana. La plantilla de WhatsApp
  es una sola y genérica (§5.6); el correo dice «el ticket» o «la tarea». Tomar un ticket nunca avisa.

## 7. Orden de despliegue y dependencias

1. Debe estar mergeada la campana (tabla, consumidores, grupo del miembro, mapeo del outbox de
   Soporte). **Sin ella, este spec no se puede implementar.**
2. Tareas internas (el evento `task-assigned`).
3. Este spec: kind nuevo, tabla de escalada, worker, plantilla, celular de la membresía, correo.
Backend primero; un frontend viejo pinta el `kind` desconocido como aviso genérico.

## 8. Pruebas (TDD, RED antes que GREEN)

Consumidor: crea entrada y escalada una sola vez ante reintento; no avisa al asignarse a uno mismo.
Worker: no escala si la entrada se leyó, se resolvió, se reasignó o se cerró; con el miembro conectado
pospone 1 min sin contar intento ni enviar, y escala en cuanto deja de estarlo; escala por WhatsApp con
conexión, plantilla aprobada y celular; cae a correo sin celular, sin
conexión, con plantilla sin aprobar o con fallo de Meta; nunca manda por dos canales; aísla el error de
un tenant; lease y reclamo con dos réplicas. Plantilla de WhatsApp: no contiene asunto. Celular:
validación E.164, teléfono efectivo (propio gana, HR si no hay, HR sin normalizar con certeza no se usa,
`phoneSource`, módulo `hr` apagado o ficha terminada), ajustes de `taskNotice` (conexión ajena o inactiva
422, ausente conserva, sin conexión nunca WhatsApp), el miembro sólo edita el suyo, el administrador lo edita por el mismo `PUT …/profile` con su `If-Match`, 422 por formato. Arquitectura intacta: los
adaptadores cross-módulo viven en `Bootstrapper`. La integración corre filtrada por nombre mientras
se itera (reglas del workspace).

## 9. Lo que queda al implementador

Nombres finales de tablas, columnas y errores; el ancho de la columna del `kind` de plantilla; cómo
se normaliza el teléfono de RR. HH. (regla por país del tenant); el texto exacto del correo y de la plantilla (español
neutro tuteando, sin voseo); y la forma exacta de marcar leída la entrada desde el detalle de la tarea
(D-2).
