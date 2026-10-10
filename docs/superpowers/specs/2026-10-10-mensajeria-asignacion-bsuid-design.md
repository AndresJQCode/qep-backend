# Mensajería: asignación, BSUID, cliente incompleto y respuestas citadas

**Fecha:** 2026-10-10
**Módulos:** Messaging (identidad por BSUID, asignación, eventos en el historial, respuestas
citadas), Customers (ficha incompleta, `whatsapp_user_id`), Quotations (bloqueo de cliente
incompleto), Reporting (excluye incompletos), Bootstrapper (adaptadores nuevos de los puertos de
Messaging). Authorization **sin** permisos nuevos.
**Estado:** decisiones 1–7 aprobadas por el owner en conversación el 2026-10-10. Las decisiones
menores que el spec tuvo que tomar para cerrarlas están en §13 (D-A1 a D-A12), a ratificar.
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` (en adelante, «el spec base»).
Todo lo que este documento no toca sigue como está ahí; las citas «base §N» apuntan a ese archivo.
**Rama:** `feature/mensajeria-asignacion` desde `develop` `2d68627`, que ya trae el módulo Messaging.
**Frontend:** construye sobre `origin/feature/mensajeria-whatsapp` de `qep-frontend` (no está
mergeada en `develop` del frontend). Su plan se escribe después, con estos contratos fijos (§14).

## 1. Contexto y alcance

La bandeja de WhatsApp del spec base funciona, pero tiene cuatro huecos que el owner quiere cerrar
antes de desplegarla por primera vez:

1. **La identidad de la persona.** El spec base ata cada conversación a `(connection_id, wa_id)`, el
   teléfono. Meta ya no garantiza el teléfono: desde abril de 2026 cada webhook trae un
   identificador por negocio (BSUID) y el teléfono puede faltar (§3).
2. **De quién es cada conversación.** Hoy cualquier asesor responde cualquier conversación (base
   §1, «Fuera de alcance»: asignación). Con varios asesores, dos le contestan a la misma persona.
3. **La persona que escribe y no es cliente.** El spec base sólo empareja por teléfono al leer
   (base decisión 10); quien no está en Customers queda como un número suelto.
4. **Contexto en el hilo.** No hay respuestas citadas ni rastro de quién tomó, transfirió o
   resolvió.

**Messaging no está desplegado en ningún ambiente.** Eso hace barato cambiar la clave de la
conversación ahora (una migración sobre tablas vacías en producción) y caro después.

### Dentro de alcance

- Identidad por BSUID: clave `(connection_id, user_id)`, teléfono opcional, cambio de número.
- Cliente incompleto: todo el que escribe es cliente de QEP; la ficha se completa después.
- Asignación: tomar, transferir, liberar; sólo el asignado responde; autoasignación al responder;
  asignación pegajosa por cliente; filtros y contadores.
- Respuestas citadas en los dos sentidos.
- Eventos del sistema en el historial.
- Envío síncrono, idempotente y optimista (sin cambio de contrato, §6.1.7).

### Fuera de alcance

Plantillas (fuera de la ventana de 24 h), campañas, medios salientes, el botón de Meta para pedir
datos de contacto (`REQUEST_CONTACT_INFO`), unir personas entre portafolios por
`parent_user_id` (se guarda, no se usa), y mostrar «Reenviado» (`context.forwarded`,
`frequently_forwarded`: no se guardan en este slice).

## 2. Decisiones del owner (2026-10-10)

| # | Decisión | Elegido | Por qué |
| --- | --- | --- | --- |
| 1 | Identidad | **BSUID**. La conversación se identifica por `(connection_id, user_id)`; `wa_id` queda como dato opcional (dígitos cuando está); `username` y `parent_user_id` son datos de perfil, no claves. Se envía con `recipient` = BSUID; las filas viejas sin BSUID envían con `to` hasta que su primer entrante con BSUID las adopta | Meta manda el BSUID **siempre** y el teléfono **a veces** (§3). Una clave que puede faltar no es clave. El `username` es opcional y cambia: tampoco |
| 2 | Cliente incompleto | Todo el que escribe es cliente: si no existe, se crea con ficha **`Incomplete`**. Mientras lo sea, identificación, correo, dirección, ciudad, clasificación, CUC y teléfono pueden faltar. Completar = el `PUT` de hoy con todos los datos → `Complete` y CUC. No se le cotiza ni se le vende a un incompleto | La bandeja necesita un cliente estable al que colgar la conversación y la asignación; obligar a llenar la ficha antes de contestar frena la venta |
| 3 | Asignación | `assigned_member_id` en la conversación. Cualquiera con `messaging.conversation.manage` toma (aun de otro asesor), transfiere o libera, siempre con `If-Match`. **Sólo el asignado responde**; responder una sin asignar la toma. Pegajosa por cliente: resolver no libera, y una conversación nueva del mismo cliente hereda el asignado | Un dueño por conversación evita respuestas cruzadas; tomar de otro sin pedir permiso evita que una conversación quede presa de alguien ausente |
| 4 | Respuestas citadas | `replyTo` opcional al enviar → `context.message_id` de Meta. Lo entrante se resuelve al ingerir. `Message.replyTo` en el contrato | Contexto en el hilo sin copiar el texto citado |
| 5 | Eventos del sistema | Filas de `messages` con `direction = System` y `kind = Event`; nunca van a WhatsApp, no suben `unreadCount`, no cambian la foto de la lista, no se buscan | El historial cuenta qué pasó, en orden, con la misma paginación del hilo |
| 6 | Envío | Se mantiene síncrono e idempotente por `clientId` (base §8.3). El frontend lo vuelve optimista con cola por conversación. **Sin cambio de contrato** | El 201/422 inmediato ya da lo que la burbuja necesita; la cola garantiza el orden |
| 7 | Frontend | Plan aparte, después, sobre estos contratos | Los contratos se fijan primero |

## 3. Lo verificado en la documentación de Meta (2026-10-10)

| Tema | Lo verificado | Fuente |
| --- | --- | --- |
| Formato del BSUID | Código de país ISO 3166 alfa-2 + `.` + hasta 128 alfanuméricos (`US.13491208655302741918`). Único por par portafolio de negocio–persona; **se regenera cuando la persona cambia de número** | https://developers.facebook.com/documentation/business-messaging/whatsapp/business-scoped-user-ids |
| Username | 3 a 35 caracteres: letras inglesas, dígitos, punto y guion bajo; al menos una letra; sin punto al inicio, al final ni dos seguidos. Opcional y puede cambiar | misma |
| Entrante | `contacts[].user_id` siempre; `contacts[].profile.username` sólo si la persona activó usuario; `contacts[].parent_user_id` sólo con BSUID padre activado; `contacts[].wa_id` **se omite** si la persona tiene usuario y no hubo interacción reciente. `messages[].from_user_id` (BSUID); `messages[].from` (teléfono) condicionado a la ventana de 30 días o a la libreta de contactos | misma |
| Estados | `statuses[].recipient_user_id` siempre; `recipient_id` se omite si se envió a BSUID sin teléfono disponible. `contacts[]` sólo en `sent`/`delivered`/`read`; en `failed` no viene | misma |
| Cambio de número | Webhook `user_id_update` con `user_id.{previous, current}` (y `parent_user_id` con la misma forma); mensaje de sistema `user_changed_user_id` con cuerpo `"User <NAME> changed from <OLD_BSUID> to <NEW_BSUID>"`, el `user_id` nuevo y, a veces, `wa_id` | misma |
| Enviar | `recipient` acepta BSUID (o BSUID padre). **Si vienen `to` y `recipient`, gana `to`**. La respuesta trae `contacts[].input` y `contacts[].user_id` (sin `wa_id`) cuando se envió por BSUID | misma |
| Fechas | BSUID en webhooks desde principios de abril de 2026; envío por BSUID desde julio de 2026 | misma |
| Responder citando | `"context": { "message_id": "<wamid>" }` en el envío. **No se puede enviar una reacción como respuesta citada.** La cita no se dibuja si el mensaje citado pasó a almacenamiento largo (típicamente a los 30 días) o si se responde con plantilla | https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/contextual-replies/ |
| Cita entrante | `messages[].context.{id, from, forwarded, frequently_forwarded, referred_product}`; `id` es el `wamid` citado | https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/text/ |

**No verificado (riesgo en §13):** la documentación no muestra un cuerpo de ejemplo de
`user_id_update` ni del mensaje de sistema `user_changed_user_id`, ni dice si `user_id_update` es un
campo que se suscribe aparte en la app. El diseño procesa los dos caminos de forma idempotente
(§8.3), así que basta con que llegue uno.

**Correcciones a lo conversado, por el código y la documentación:**

1. **`username` mide hasta 35** (Meta). La columna queda en `varchar(64)`: margen sin costo.
2. **El código de error de cotización sigue la convención del módulo.** Lo conversado fue
   `quotations.customer.incomplete`; Quotations usa `quotation.quotation.client_*`
   (`QuotationCustomerEligibility.cs:26-40`: `client_not_found`, `client_cuc_missing`,
   `client_inactive`). El código es **`quotation.quotation.client_incomplete`** (D-A4).
3. **Sin el chequeo nuevo, un incompleto ya se bloquearía, con el código equivocado.**
   `QuotationCustomerEligibility.Ensure` rechaza un cliente sin CUC con `client_cuc_missing`; un
   incompleto no tiene CUC. El chequeo nuevo va **antes** que ése, para que la pantalla diga
   «completa la ficha» y no «falta el CUC».
4. **«Customers ya tiene un estado».** `IsActive` y `ChangeCustomerStatus` (activar/desactivar) son
   el estado del cliente. La columna nueva se llama `completeness` (enum `CustomerCompleteness`) para
   no chocar con eso.
5. **El `CHECK` de ficha completa sólo cubre lo que hoy es `NOT NULL` en la base.** `phone`,
   `email`, `city_id` y `city_name` ya son nullable en `customers.customers` (snapshot de
   `CustomersDbContext`); meterlos en el `CHECK` podría tumbar la migración con filas viejas. Esos
   siguen exigidos por `CustomerWriteRules`, como hoy (§7.2, D-A11).
6. **Transferir necesita a quién.** El frontend no tiene cómo listar los miembros que pueden
   responder; se agrega `GET /messaging/assignees` (D-A1).
7. **Autoasignar no puede bloquear la conversación mientras Meta responde.** El envío mantiene una
   transacción abierta hasta 10 s con candado sólo sobre la fila del mensaje (base §8.3); si la
   autoasignación escribiera la conversación dentro de esa transacción, la ingesta de esa
   conversación esperaría esos 10 s. La autoasignación va en su propia sentencia, antes (§8.5).
8. **El frontend trata una `direction` desconocida como error de contrato** (base §5.4). `System`
   rompe la versión actual del SPA: frontend y backend se despliegan juntos (§13).

## 4. Trazabilidad: historias → componentes

| # | Historia (owner, 2026-10-10) | Componentes |
| --- | --- | --- |
| 18 | «Como asesor, veo y respondo a una persona aunque Meta no me mande su teléfono» | `WebhookPayloadParser` (BSUID), `conversations.user_id`, `IX_conversations_connection_user`, `InboundIngestion` (§8.1), `WhatsAppCloudClient` con `recipient` (§8.5) |
| 19 | «Como asesor, si la persona cambia de número, sigo en la misma conversación y con el mismo cliente» | `user_id_update` y `user_changed_user_id` en el parser, `ContactNumberChange` (§8.3), `IMessagingCustomerDirectory.ReplaceWhatsAppUserIdAsync` |
| 20 | «Como asesor, quien me escribe por WhatsApp aparece como cliente de QEP aunque todavía no tenga ficha» | `IMessagingCustomerDirectory.EnsureAsync` → `ICustomerWhatsAppDirectory` (Customers) → `Customer.CreateIncomplete`; `conversations.customer_id` (§8.2) |
| 21 | «Como asesor, completo la ficha del cliente y desde ahí le puedo cotizar» | `PUT /customers/{id}` sobre un incompleto → `Complete` + CUC (§6.2); `isComplete` y filtro en la lista |
| 22 | «Como asesor, no le cotizo ni le vendo a un cliente con la ficha incompleta» | `QuotationCustomerEligibility` → `quotation.quotation.client_incomplete` (§6.3) |
| 23 | «Como asesor, tomo una conversación, aunque la tenga otro, y desde ese momento sólo yo le respondo» | `POST …/take`, `assigned_member_id`, `messaging.conversation.assigned_to_other` (§8.4, §8.5) |
| 24 | «Como asesor, transfiero una conversación a un compañero que pueda responder, o la libero» | `POST …/transfer`, `POST …/release`, `GET /messaging/assignees`, `messaging.conversation.assignee_cannot_reply` |
| 25 | «Como asesor, si respondo una conversación sin dueño, queda mía» | Autoasignación en `SendMessageHandler` (§8.5) |
| 26 | «Como asesor, cuando un cliente mío vuelve a escribir, la conversación llega a mí» | Herencia del asignado en la ingesta (§8.2), `IX_conversations_tenant_customer_activity` |
| 27 | «Como asesor, filtro mis conversaciones y las sin asignar, y veo cuántas hay» | `assigned=me\|none\|all`, `counts.mine`, `counts.unassigned` (§5.1, §7.1) |
| 28 | «Como asesor, respondo citando un mensaje y veo cuándo la persona cita uno» | `replyTo` en el envío, `context.id` en la ingesta, `messages.reply_to_message_id`, `Message.replyTo` (§8.6) |
| 29 | «Como asesor, veo en el hilo quién tomó, transfirió, liberó, resolvió o reabrió, y cuándo se creó o vinculó el cliente» | Eventos `System`/`Event` (§6.1.5, §8.7) |
| 30 | «Como asesor, mi mensaje aparece al instante, sé si salió, y si falla lo reintento sin duplicarlo ni desordenar los siguientes» | Envío idempotente de base §8.3 + cola optimista del frontend (§6.1.7, §14) |

## 5. Contratos HTTP (delta)

Base: `/api/v1/tenants/{tenantId}`. Enums por nombre. Lo que no se menciona sigue igual que en base §5.

### 5.1 Messaging

**`ConversationSummary`** — cambia `contact` y `customer`, se agrega `assignedTo`:

```json
{ "id": "…", "connectionId": "…", "connectionName": "Ventas",
  "contact": { "userId": "CO.1349120865530274" | null,
               "waId": "573001234567" | null,
               "username": "laura.perez" | null,
               "profileName": "Laura Pérez" | null },
  "customer": { "id": "…", "name": "Laura Pérez", "isComplete": false } | null,
  "assignedTo": { "memberId": "…", "displayName": "Andrés", "isMe": true } | null,
  "status": "Open", "unreadCount": 2, "lastMessage": { … } | null,
  "customerWindowExpiresAt": "…" | null, "updatedAt": "…", "version": 3 }
```

- `contact.userId` es `null` sólo en una conversación vieja que todavía no recibió un entrante con
  BSUID (§8.1). `waId` es `null` cuando Meta no mandó el teléfono. **Al menos uno de los dos
  viene** (`CK_conversations_identity`).
- `customer` ya no se resuelve por teléfono al leer: sale de `conversations.customer_id` (§6.1.2).
  `isComplete` viaja para que el encabezado del hilo ofrezca «Completar ficha» sin otra llamada
  (BFF; D-A8). `customer` sólo es `null` en una conversación vieja sin cliente y sin coincidencia
  por teléfono.
- `assignedTo.isMe` es `true` cuando `assigned_member_id` es la membresía activa de quien llama en ese
  tenant (D-A13). BFF: la SPA no conoce su `memberId` (`/auth/me` y `/authorization/me` sólo dan el
  `userId`), así que no puede decidir sola si la conversación es suya. Se resuelve una vez por request.
- `assignedTo.displayName` nunca es `null`: la membresía que ya no está viaja como
  `"Miembro eliminado"` (D-M20 del spec base).
- `lastMessage` nunca es un evento (§6.1.5).

**`MessageHit.contact`** (búsqueda en el historial, base §5.3) cambia igual que
`ConversationSummary.contact`, y `MessageHit.customer` gana `isComplete`.

**`GET /messaging/conversations?status=&assigned=me|none|all&search=&page=&pageSize=`**

- `assigned` opcional, default `all`. `me` = asignadas a la membresía de quien llama; `none` = sin
  asignar. Otro valor → 422 `validation.failed` con `errors["assigned"]`.
- `search` ahora busca por nombre de perfil, `username`, dígitos del teléfono y nombre del cliente.
- `counts` gana dos números, de todo el tenant y sin búsqueda, como los que ya estaban:

```json
"counts": { "open": 4, "unread": 3, "mine": 2, "unassigned": 1 }
```

`mine` y `unassigned` cuentan **sólo conversaciones abiertas** (D-A10): son las pestañas de la cola
de trabajo; una resuelta no espera a nadie.

**`Message`** — `direction` gana `"System"`, `kind` gana `"Event"`, y dos campos nuevos:

```json
{ "id": "…",
  "direction": "Inbound" | "Outbound" | "System",
  "kind": "Text" | … | "Unsupported" | "Event",
  "text": "…" | null, "media": … | null, "location": … | null,
  "status": "Sent" | "Delivered" | "Read" | "Failed",
  "failureReason": "…" | null, "at": "…",
  "sentBy": { "memberId": "…", "displayName": "…" } | null,
  "clientId": "…" | null,
  "replyTo": { "id": "…", "direction": "Inbound" | "Outbound",
               "kind": "Text" | "Image" | …, "preview": "¿Tienen disponible…?" | null } | null,
  "event": { "type": "Taken" | "Transferred" | "Released" | "AutoTaken" | "Inherited" |
                     "Resolved" | "Reopened" | "CustomerCreated" | "CustomerLinked" |
                     "ContactChangedNumber",
             "actor":    { "memberId": "…", "displayName": "…" } | null,
             "target":   { "memberId": "…", "displayName": "…" } | null,
             "previous": { "memberId": "…", "displayName": "…" } | null } | null }
```

- Un evento llega con `direction: "System"`, `kind: "Event"`, `status: "Delivered"`, `event` lleno
  y `text`, `media`, `location`, `failureReason`, `sentBy`, `clientId` y `replyTo` en `null`. Un
  mensaje que no es evento llega con `event: null`.
- `event.actor` es `null` cuando lo hizo el sistema (reapertura por un entrante, cliente creado,
  cambio de número, herencia). Qué lleva cada tipo: §6.1.5.
- `replyTo.preview` es el texto o la leyenda del citado, recortado a 200; `null` si no tiene.
  `replyTo` es `null` si el mensaje no cita nada **o** si cita algo que QEP no tiene (§8.6).

**`POST /messaging/conversations/{id}/messages`** — el cuerpo gana `replyTo`:

```json
{ "clientId": "uuid", "text": "Sí, tenemos 12 unidades.", "replyTo": "uuid" | null }
```

- `replyTo` es el `id` de **nuestro** mensaje (no el `wamid`). Tiene que ser de esta
  conversación, tener `wamid`, no ser una reacción ni un evento. Si no → 422 `validation.failed`
  con `errors["replyTo"]`.
- 422 nuevo: `messaging.conversation.assigned_to_other` (está asignada a otra persona).
- Una conversación sin asignar queda asignada a quien envía (evento `AutoTaken`); la respuesta
  sigue siendo **201 `Message`**. El frontend vuelve a pedir la conversación para ver `assignedTo`.
- La idempotencia de base §8.3 no cambia: repetir un `clientId` ya enviado devuelve el mensaje
  guardado, con su `replyTo` guardado, aunque el cuerpo traiga otro.

**Endpoints nuevos.** Permiso `manage`, `If-Match` obligatorio con la `version` entre comillas
(428 sin él, 412 desactualizado), como resolve/reopen. Responden **200 `ConversationSummary`**.

| Método y ruta | Cuerpo | Qué hace | 422 |
| --- | --- | --- | --- |
| `POST /messaging/conversations/{id}/take` | — | Asigna a quien llama, la tenga quien la tenga. Si ya era suya, 200 sin cambios (D-A2) | — |
| `POST /messaging/conversations/{id}/transfer` | `{ "memberId": "uuid" }` | Asigna a esa membresía | `messaging.conversation.assignee_cannot_reply` (no es membresía activa del tenant o sus roles no conceden `manage`); `validation.failed` con `errors["memberId"]` (vacío) |
| `POST /messaging/conversations/{id}/release` | — | Deja sin asignar. Sin asignar ya, 200 sin cambios (D-A2) | — |

**`GET /messaging/assignees`** — permiso `manage` (D-A1). Las membresías activas del tenant cuyos
roles conceden `messaging.conversation.manage`, ordenadas por nombre:

```json
{ "items": [ { "memberId": "…", "displayName": "Andrés" } ] }
```

### 5.2 Customers

**`CustomerDto`** (detalle y fila de la lista) gana `isComplete: boolean`. Con
`isComplete: false` pueden venir en `null`: `cuc`, `identificationType`, `identificationNumber`,
`email`, `address`, `country`, `city`, `cityName`, `classification` (y `phone`, que ya era
nullable). Con `isComplete: true` la forma es la de hoy.

**`GET /customers?…&isComplete=true|false`** — filtro opcional; sin él, todos. Otro valor → 422
`validation.failed` con `errors["isComplete"]`.

**`PUT /customers/{id}`** — sin cambios de forma. Sobre un incompleto exige los mismos campos que
hoy (`CustomerWriteRules`) y lo deja `Complete` con CUC nuevo (§6.2). No hay edición parcial de un
incompleto: o se completa, o queda como está.

**Export Excel de clientes** — incluye incompletos, con celdas vacías y una columna nueva
«Estado de la ficha» (`Completa` / `Incompleta`).

### 5.3 Quotations

422 nuevo **`quotation.quotation.client_incomplete`** al crear una cotización, cambiar su cliente,
enviarla o convertirla en pedido con un cliente incompleto (§6.3).

## 6. Diseño por módulo

### 6.1 Messaging

#### 6.1.1 Identidad

- `Conversation` gana `UserId` (`string?`), `Username`, `ParentUserId`, `CustomerId`,
  `AssignedMemberId`, `AssignedAt`. `WaId` pasa a `string?`; cuando viene, la regla de hoy (1 a 20
  dígitos, `Conversation.cs:79-81`) sigue.
- `Conversation.UserIdMaxLength = 150` (BSUID: 2 + 1 + 128 = 131), `UsernameMaxLength = 64`.
  Validación de forma del BSUID: `^[A-Z]{2}\.[A-Za-z0-9]{1,128}$`. `parent_user_id` se guarda
  recortado a 150 sin validar forma (no se usa en este slice).
- `ContactDto(string? UserId, string? WaId, string? Username, string? ProfileName)`
  (`MessagingDtos.cs:8`).

#### 6.1.2 Cliente de la conversación

- `conversations.customer_id` se fija **en la ingesta** (§8.2) y no se resuelve más por teléfono al
  leer. Esto **reemplaza la decisión 10 del spec base** («se resuelve al leer, no se guarda»): el
  argumento era no dejar un id viejo si el cliente se borraba; hoy Customers no borra clientes
  (sólo desactiva, `ChangeCustomerStatus`), y un id estable es lo que la asignación pegajosa
  necesita.
- **Fallback para lo viejo:** con `customer_id` en `null`, `ConversationSummaryBuilder` sigue
  emparejando por teléfono (`MatchAsync`, como hoy en `MessagingSupport.cs:49`).
- El nombre se resuelve por página con un puerto nuevo, `FindRefsAsync(tenantId, customerIds)` →
  `{ id, name, isComplete }`.

#### 6.1.3 Asignación

- `Conversation.Take(memberId, now)`, `TransferTo(memberId, now)`, `Release(now)`: cambian
  `AssignedMemberId`/`AssignedAt`, `Touch` (versión + 1) y devuelven el evento a registrar. Un
  `Take` de quien ya la tiene, o un `Release` sin asignado, no cambia nada ni sube la versión.
- Los tres handlers (`TakeConversationHandler`, `TransferConversationHandler`,
  `ReleaseConversationHandler`) siguen el orden de base §6.4 (tenant y permiso, `TenantModuleGuard`,
  conversación del tenant) y `ConversationConcurrency.EnsureVersion` (`ConversationLifecycle.cs:119`).
- **Puerto nuevo `IMessagingAssignees`** (adaptador en Bootstrapper sobre `IMembershipRepository`
  de Tenancy e `ITenantRoleCatalog.PermissionsForAsync` de Authorization):
  - `CanReplyAsync(tenantId, memberId)` → la membresía existe **en ese tenant**, está activa y sus
    roles conceden `messaging.conversation.manage`.
  - `ListAsync(tenantId)` → las que cumplen eso, con su nombre (para `GET /messaging/assignees`).
  Se consulta por pedido, sin caché: los permisos se resuelven por request en este repo (memoria
  del proyecto «Login no valida email ni cachea permisos»), y un cambio de rol tiene que verse ya.
- **Auditoría** (`IMessagingAuditRecorder`, camino atómico de base §6.4): acciones nuevas
  `messaging.conversation.taken`, `messaging.conversation.transferred`,
  `messaging.conversation.released`, `messaging.conversation.auto_taken`. La herencia no se audita:
  no la hace una persona.

#### 6.1.4 Envío

- `IWhatsAppCloudClient.SendTextAsync(sender, SendTarget target, text, callbackData, string?
  contextWamid, ct)`. `SendTarget` es `ByUserId(bsuid)` o `ByPhone(waId)`; el cliente arma
  `recipient` **o** `to`, nunca los dos (si vienen los dos, Meta usa `to`, §3). Con `contextWamid`
  agrega `context: { message_id }`.
- Elección: `user_id` si la conversación lo tiene; si no, `wa_id` (fila vieja).
- `SendMessageCommand` gana `ReplyTo` (`Guid?`); el validador sólo exige GUID no vacío si viene. La
  pertenencia la comprueba el handler (§8.5).
- `messages.reply_to_message_id` se guarda también en el saliente.

#### 6.1.5 Eventos

- `MessageDirection.System = 3`, `MessageKind.Event = 13` (`MessageEnums.cs`), con su mapa de ida y
  vuelta y su prueba.
- `details` (jsonb) de un evento: `{ "type": "Transferred", "actor": "<memberId>", "target":
  "<memberId>", "previous": "<memberId>" }`, con las claves que apliquen. **Sólo ids**: los nombres
  se resuelven al leer con `IMessagingMemberNames`, como `sentBy`. `CustomerCreated` y
  `CustomerLinked` agregan `"customerId"`. Nunca teléfonos, BSUIDs, tokens ni textos de Meta.

| `type` | Quién lo escribe | `actor` | `target` | `previous` |
| --- | --- | --- | --- | --- |
| `Taken` | take | quien toma | — | el asignado anterior, si había |
| `Transferred` | transfer | quien transfiere | el nuevo | el anterior, si había |
| `Released` | release | quien libera | — | el que estaba |
| `AutoTaken` | envío a una sin asignar | quien envía | — | — |
| `Inherited` | ingesta, conversación nueva de un cliente con asignado (D-A3) | — | el heredado | — |
| `Resolved` / `Reopened` | resolve / reopen | quien lo hace | — | — |
| `Reopened` | ingesta, entrante sobre una resuelta | — | — | — |
| `CustomerCreated` | ingesta, cliente incompleto nuevo | — | — | — |
| `CustomerLinked` | ingesta, cliente existente (por BSUID o teléfono) | — | — | — |
| `ContactChangedNumber` | cambio de número (§8.3) | — | — | — |

- Un evento **no** sube `unread_count`, **no** toca `last_message_*` ni `last_activity_at`, **no**
  cambia `last_inbound_*`. Su `search_vector` sale vacío solo: `text` y `caption` son `NULL`, así
  que la búsqueda (base §7.3) nunca lo encuentra, sin tocar la columna generada.
- `status` de un evento = `Delivered` (2) (D-A5). La columna es `NOT NULL` con `CHECK 1..4`;
  `Failed` está prohibido para lo que no es saliente (`CK_messages_inbound_not_failed`), y el SPA
  sólo dibuja el ícono de estado en salientes (base §5.4). Hacerla nullable obligaría a tocar el
  `CHECK`, el mapa y el contrato por un valor que nadie mira.

#### 6.1.6 Búsqueda de la lista

`ListAsync` (`ConversationQueries.cs`, `IConversationQueries.cs:30`) cambia:

- `profile_name ILIKE @p OR username ILIKE @p OR wa_id LIKE @d` (dígitos sólo con
  `ConversationSearchTerms.NumberDigits`, sin cambios) `OR customer_id = ANY(@customerIds) OR
  (customer_id IS NULL AND wa_id = ANY(@phones))`.
- `@customerIds` sale de un método nuevo del puerto, `FindIdsByNameAsync(tenantId, term, cap =
  200)` (usa `IX_customers_name_trgm`); `@phones` sigue saliendo de `FindWaIdsByNameAsync` para las
  conversaciones viejas.
- Filtro `assigned`: `AND assigned_member_id = @me` o `AND assigned_member_id IS NULL`.

#### 6.1.7 Envío optimista (sin cambio de backend)

Base §8.3 ya da lo que hace falta: 201 o 422 con motivo, idempotencia por `clientId`, un `Failed`
se reenvía sobre la misma fila y nunca se duplica, y el cierre del reclamo ignora el aborto del
request (`SendMessage.cs:67-70`). El frontend (§14) agrega la burbuja optimista, la cola serial por
conversación y el `clientId` persistido antes del primer intento. Su timeout de request tiene que
ser **mayor que los 10 s** del cliente `messaging.meta-graph` (base decisión 3): 15 s.

### 6.2 Customers

- **`CustomerCompleteness { Complete, Incomplete }`**, columna `completeness` (`varchar(16)`, enum
  por nombre como el resto del módulo).
- **`Customer.CreateIncomplete(id, tenantId, name, phone?, country?, whatsAppUserId, now,
  phoneNormalizer)`**: `Cuc`, identificación, correo, dirección, ciudad y clasificación en `null`;
  sin libreta de direcciones; `IsActive = true`; `PhoneE164` calculado como hoy. Las propiedades
  del agregado que hoy son `string` no anulable (`Cuc`, `IdentificationNumber`, `Address`,
  `Country`) y `IdentificationType`, `ClassificationId` pasan a anulables; `Customer.Create` (la
  ficha completa) no cambia sus reglas.
- **`Customer.Complete(…)`**: lo usa `UpdateCustomerHandler` cuando el cliente es `Incomplete`.
  Recibe los mismos datos que `Update` más el CUC, que el handler genera con `ICucGenerator` +
  `CucFormatter` exactamente como `CreateCustomerHandler`. Siembra la libreta como `Create`. Deja
  `Complete`. `Update` sobre un completo no cambia.
- **`WhatsAppUserId`** (`string?`, ≤ 150) único por tenant. El agregado no tiene métodos para
  ponerlo ni cambiarlo: lo escriben `UPDATE` condicionales del repositorio
  (`TryAttachWhatsAppUserIdAsync`, sólo si no tenía uno, D-A6; `TryReplaceWhatsAppUserIdAsync`,
  sólo si tenía el anterior, §8.3), sin pasar por el agregado ni subir la versión. La regla del valor vive en
  `Customer.NormalizeWhatsAppUserId`.
- **Nombre del incompleto** (decisión del owner, en este orden): nombre de perfil de WhatsApp →
  `username` → teléfono (`+` y dígitos) → `"Contacto de WhatsApp"`. Recortado a
  `Customer.NameMaxLength`.
- **País del incompleto:** el prefijo ISO del BSUID si es una región que libphonenumber conoce
  (`PhoneNumberUtil.GetSupportedRegions()`); si no, la región del teléfono si lo hay; si no,
  `null`. Completar la ficha lo exige igual.
- **Puerto nuevo para Messaging, `ICustomerWhatsAppDirectory`** (Customers.Application):

```csharp
public sealed record WhatsAppContact(string UserId, string? PhoneE164, string? ProfileName, string? Username);
public enum EnsureOutcome { Existing, Linked, Created }
public sealed record EnsuredCustomer(Guid CustomerId, EnsureOutcome Outcome);

Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken ct);
Task ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken ct);
Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct); // Id, Name, IsComplete
Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken ct);
```

  `EnsureAsync` (§8.2): por BSUID → `Existing`; si no, por teléfono con la regla de duplicados D-M7
  → `Linked` (y le pone el BSUID **sólo si no tenía otro**, D-A6); si no, crea el incompleto →
  `Created`. La carrera la arbitra `IX_customers_tenant_whatsapp_user_id`: `CustomersUnitOfWork`
  traduce esa violación **por nombre de índice** a una excepción interna, y `EnsureAsync` relee por
  BSUID y devuelve `Existing`.
- **Auditoría:** acción nueva `customers.customer.created_from_messaging` por
  `ICustomersAuditPublisher` (outbox, misma transacción). `Publish` no tiene metadatos, así que el
  origen va en la acción (D-A9). El actor es `Guid.Empty`: no hay persona detrás de un webhook.
  `Complete` publica `customers.customer.completed`.
- **Lista:** `ListCustomersQuery` gana `bool? IsComplete`; el filtro usa
  `IX_customers_tenant_incomplete`. `CustomerDto` gana `IsComplete`.
- **Export:** `ClosedXmlCustomerExportBuilder` agrega la columna y escribe vacío lo que falta.
- **Barrido de consumidores:** todo lo que hoy lee `Cuc`, `Country`, `IdentificationType`,
  `IdentificationNumber`, `Address` o `ClassificationId` como no nulos (mapeos, importador,
  `CustomerReportSource`, `IQuotationCustomerLookup`) se revisa en la tarea de Customers. El
  compilador lo marca al volverlos anulables.

### 6.3 Quotations

- `QuotationCustomerRef` gana `IsComplete`; el adaptador de `IQuotationCustomerLookup` en
  Bootstrapper lo llena.
- `QuotationCustomerEligibility.Ensure` agrega, **después** de `client_not_found` y **antes** de
  `client_cuc_missing`: `!customer.IsComplete` → `QuotationsDomainException(
  "quotation.quotation.client_incomplete", …)` (422). Cubre los cuatro llamadores que ya existen:
  `CreateQuotation.cs:60`, `ChangeQuotationClient.cs:56`, `SendQuotation.cs:103` y
  `ConvertQuotationToOrder.cs:83` (el pedido es lo facturable).
- POS no referencia clientes de Customers (`PosCustomerResponse` es una copia de texto): no aplica.

### 6.4 Reporting

`CustomerReportSource` (Bootstrapper) agrega `completeness = 'Complete'` a la lista y al resumen:
un incompleto no tiene clasificación ni ciudad, y contarlo distorsionaría los cortes por esos
campos.

### 6.5 Bootstrapper

| Puerto (Messaging.Application) | Adaptador | Sobre |
| --- | --- | --- |
| `IMessagingCustomerDirectory` (amplía) | `MessagingCustomerDirectory` | `ICustomerPhoneDirectory` (lo viejo) + `ICustomerWhatsAppDirectory` (nuevo) |
| `IMessagingAssignees` (nuevo) | `MessagingAssignees` | `IMembershipRepository`, `IUserDirectory`, `ITenantRoleCatalog` |

`MessagingLayerTests` sigue verificando que Messaging no referencia Customers.

### 6.6 Authorization

Sin permisos nuevos. Tomar, transferir y liberar usan `messaging.conversation.manage`. No se copia
el patrón `reporting.all_advisors.read`: el owner decidió que cualquiera con `manage` toma de otro.

## 7. Base de datos

### 7.1 Messaging — migración `AddBsuidAssignmentAndEvents`

```sql
ALTER TABLE messaging.conversations
    ADD COLUMN user_id            varchar(150) NULL,
    ADD COLUMN username           varchar(64)  NULL,
    ADD COLUMN parent_user_id     varchar(150) NULL,
    ADD COLUMN customer_id        uuid         NULL,
    ADD COLUMN assigned_member_id uuid         NULL,
    ADD COLUMN assigned_at        timestamptz  NULL,
    ALTER COLUMN wa_id DROP NOT NULL,
    ADD CONSTRAINT "CK_conversations_identity"   CHECK (user_id IS NOT NULL OR wa_id IS NOT NULL),
    ADD CONSTRAINT "CK_conversations_assignment" CHECK ((assigned_member_id IS NULL) = (assigned_at IS NULL));

DROP INDEX messaging."IX_conversations_connection_wa";
CREATE UNIQUE INDEX "IX_conversations_connection_user"
    ON messaging.conversations (connection_id, user_id) WHERE user_id IS NOT NULL;
CREATE UNIQUE INDEX "IX_conversations_connection_wa_legacy"
    ON messaging.conversations (connection_id, wa_id) WHERE user_id IS NULL;
CREATE INDEX "IX_conversations_tenant_assignee_status_activity"
    ON messaging.conversations (tenant_id, assigned_member_id, status, last_activity_at DESC, id DESC)
    WHERE assigned_member_id IS NOT NULL;
CREATE INDEX "IX_conversations_tenant_unassigned_status_activity"
    ON messaging.conversations (tenant_id, status, last_activity_at DESC, id DESC)
    WHERE assigned_member_id IS NULL;
CREATE INDEX "IX_conversations_tenant_customer_activity"
    ON messaging.conversations (tenant_id, customer_id, last_activity_at DESC)
    WHERE customer_id IS NOT NULL;
CREATE INDEX "IX_conversations_username_trgm"
    ON messaging.conversations USING GIN (username gin_trgm_ops);

ALTER TABLE messaging.messages
    ADD COLUMN reply_to_message_id uuid NULL,
    ADD COLUMN reply_to_wamid      text NULL,
    DROP CONSTRAINT "CK_messages_direction",
    ADD CONSTRAINT "CK_messages_direction" CHECK (direction IN (1, 2, 3)),
    DROP CONSTRAINT "CK_messages_kind",
    ADD CONSTRAINT "CK_messages_kind" CHECK (kind BETWEEN 1 AND 13),
    ADD CONSTRAINT "CK_messages_system_is_event" CHECK ((direction = 3) = (kind = 13)),
    ADD CONSTRAINT "CK_messages_event_shape" CHECK (
        direction <> 3 OR (wamid IS NULL AND client_id IS NULL AND status = 2 AND details IS NOT NULL));
```

| Objeto | Por qué |
| --- | --- |
| `IX_conversations_connection_user` | La nueva clave: blanco del `ON CONFLICT` de la ingesta (base §7.5, sentencia 1). Parcial porque las filas viejas no tienen `user_id` |
| `IX_conversations_connection_wa_legacy` | Mantiene «una conversación por teléfono» **sólo** entre las viejas, y es el índice de la adopción (§8.1: `WHERE connection_id = @c AND user_id IS NULL AND wa_id = @w`). Una conversación con BSUID puede repetir teléfono (número reciclado por otra persona) |
| `IX_conversations_tenant_assignee_status_activity` | Pestaña «Mías» (camino caliente: poll cada 15 s) y `counts.mine` (index-only con `status = 'Open'`) |
| `IX_conversations_tenant_unassigned_status_activity` | Pestaña «Sin asignar» y `counts.unassigned`. Parcial: en régimen la mayoría está asignada y el índice queda chico |
| `IX_conversations_tenant_customer_activity` | Herencia del asignado: la conversación más reciente del cliente, una búsqueda por índice |
| `IX_conversations_username_trgm` | Búsqueda «contiene» por `username`, como `profile_name` |
| `CK_conversations_identity` | Una conversación sin BSUID ni teléfono no tendría a quién enviar |
| `CK_messages_system_is_event`, `CK_messages_event_shape` | Un evento nunca tiene `wamid` (no fue a WhatsApp), ni `clientId`, y siempre lleva su detalle |

- **`assigned_member_id` sin FK**: Messaging no comparte base lógica con Tenancy (referencia blanda,
  como `sent_by_member_id`). Una membresía borrada se ve como «Miembro eliminado».
- **`reply_to_message_id` sin FK**: la tabla más grande pagaría un chequeo por inserción, y el
  único borrado es por cascada de la conversación, que se lleva a los dos. `reply_to_wamid` guarda
  el `wamid` citado tal como llegó, aunque no se haya resuelto.
- `CK_messages_inbound_not_failed` (`direction = 2 OR status <> 4`) **no cambia**: ya cubre
  `System`.
- Todo cambio sobre `messages` es sobre una tabla vacía en producción (Messaging no está
  desplegado): ni el `ALTER` ni los `CHECK` reescriben nada caro. Por eso va ahora.

### 7.2 Customers — migración `AddCustomerCompleteness`

```sql
ALTER TABLE customers.customers
    ADD COLUMN completeness     varchar(16)  NOT NULL DEFAULT 'Complete',
    ADD COLUMN whatsapp_user_id varchar(150) NULL,
    ALTER COLUMN cuc                   DROP NOT NULL,
    ALTER COLUMN identification_type   DROP NOT NULL,
    ALTER COLUMN identification_number DROP NOT NULL,
    ALTER COLUMN address               DROP NOT NULL,
    ALTER COLUMN country               DROP NOT NULL,
    ALTER COLUMN classification_id     DROP NOT NULL,
    ADD CONSTRAINT "CK_customers_completeness" CHECK (completeness IN ('Complete', 'Incomplete')),
    ADD CONSTRAINT "CK_customers_complete_fields" CHECK (
        completeness = 'Incomplete' OR (
            cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL
            AND address IS NOT NULL AND country IS NOT NULL AND classification_id IS NOT NULL));

CREATE UNIQUE INDEX "IX_customers_tenant_whatsapp_user_id"
    ON customers.customers (tenant_id, whatsapp_user_id) WHERE whatsapp_user_id IS NOT NULL;
CREATE INDEX "IX_customers_tenant_incomplete"
    ON customers.customers (tenant_id) WHERE completeness = 'Incomplete';
```

- `DEFAULT 'Complete'` llena las filas existentes en el `ADD COLUMN` (en PostgreSQL ≥ 11 es un
  cambio de catálogo, sin reescribir la tabla) y **se queda durante el despliegue**: producción
  corre una réplica con `maxSurge 1`, así que el pod viejo sigue atendiendo después de que el nuevo
  migró, y su `INSERT` no nombra `completeness`; sin el `DEFAULT`, crear o importar un cliente
  moriría con `23502`. El modelo de EF lo declara (`HasDefaultValue`) con un centinela inválido,
  para que el código nuevo siempre escriba el valor, también `Complete`. Se quita en una migración
  posterior, cuando ya no pueda correr ningún binario anterior a esta (D-A11).
- `CK_customers_complete_fields` cubre exactamente las columnas que hoy son `NOT NULL` (§3,
  corrección 5). Todas las filas existentes lo cumplen por construcción.
- `IX_customers_tenant_identification` y `IX_customers_tenant_cuc` **no cambian**: un índice único
  de PostgreSQL trata los `NULL` como distintos, así que muchos incompletos sin documento ni CUC
  conviven sin filtro parcial, y la regla «inactivar no libera el documento» sigue intacta.
- La FK compuesta a `client_classifications` acepta `classification_id` nulo (`MATCH SIMPLE`).
- `IX_customers_tenant_whatsapp_user_id`: la identidad y el árbitro de la carrera de §9.2.
  `CustomersUnitOfWork` lo agrega a su lista de índices traducidos por nombre.

## 8. Flujos

### 8.1 Ingesta con BSUID (reemplaza el paso 1 de base §7.5)

**Parser** (`WebhookPayloadParser.cs`):

- `contacts[]` se indexa por `user_id` (no por `wa_id`, `WebhookPayloadParser.cs:72-79`): de cada
  uno, `profile.name`, `profile.username`, `parent_user_id` y `wa_id` si vienen.
- Un mensaje **exige** `from_user_id` con la forma de §6.1.1. `from` pasa a opcional; si viene,
  la regla de dígitos de hoy (`WebhookPayloadParser.cs:112-117`) sigue, y un `from` inválido se
  descarta como dato (el mensaje entra igual). Sin `from_user_id` válido el mensaje se salta y se
  registra (tolerancia de base: nunca lanza).
- `messages[].context.id` → `InboundMessage.QuotedWamid`.
- `type = "system"` con `system.type = "user_changed_user_id"` → `UserIdChange` (§8.3), no un
  mensaje `Unsupported`. Otro `system` sigue siendo `Unsupported`.
- `field = "user_id_update"` → `UserIdChange` con `user_id.previous` / `user_id.current`.
- `statuses[]` sin cambios: se correlacionan por `wamid` o por `biz_opaque_callback_data` (base
  §7.5). `recipient_user_id` y `recipient_id` no se usan.

**Antes de la transacción** (sólo para entrantes de una conexión `Active` o `NeedsAttention` con el
módulo prendido, base §8.2):

1. `SELECT id, customer_id FROM conversations WHERE connection_id = @c AND user_id = @u`.
2. Si no hay conversación **o** no tiene `customer_id`: `IMessagingCustomerDirectory.EnsureAsync`
   (§8.2). Con conversación y cliente, no se llama a Customers: el camino común no cuesta una
   consulta más por mensaje.
3. Si no hay conversación: el asignado a heredar (§8.2).

**En la transacción** (las sentencias 2 y 3 de base §7.5 no cambian salvo lo marcado):

```sql
-- 1a. Adopción de una conversación vieja (sólo si el mensaje trajo teléfono).
UPDATE messaging.conversations SET user_id = @userId
WHERE connection_id = @c AND user_id IS NULL AND wa_id = @waId
RETURNING id;
-- 1b. Si no adoptó: crear, ahora por la clave nueva.
INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, wa_id, username, parent_user_id,
       profile_name, customer_id, assigned_member_id, assigned_at, status, unread_count, last_activity_at,
       created_at, updated_at, version)
VALUES (…, @inheritedMemberId, CASE WHEN @inheritedMemberId IS NULL THEN NULL ELSE @now END, 'Open', 0, …, 1)
ON CONFLICT (connection_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
RETURNING id;
-- vacío → SELECT … WHERE connection_id = @c AND user_id = @userId (sentencia aparte, como hoy)
```

La sentencia 3 (contadores y foto) suma:
`wa_id = COALESCE(@waId, wa_id), username = COALESCE(@username, username), parent_user_id =
COALESCE(@parentUserId, parent_user_id), customer_id = COALESCE(customer_id, @customerId)`, y lee
el `status` anterior con un CTE (`WITH old AS (SELECT status FROM … WHERE id = @c FOR NO KEY UPDATE)`)
para saber si reabrió. Es `FOR NO KEY UPDATE` y no `FOR UPDATE` porque el INSERT del mensaje (sentencia 2) ya
tiene un `FOR KEY SHARE` sobre la conversación por la FK, y `FOR UPDATE` choca con ese candado: dos entregas de la
misma conversación quedan en deadlock. Después, en la misma transacción, los eventos que correspondan (§8.7).
`reply_to_message_id` se resuelve en la sentencia 2 con
`(SELECT id FROM messaging.messages WHERE connection_id = @c AND wamid = @quotedWamid)` por
`IX_messages_connection_wamid`.

### 8.2 Asegurar el cliente

`EnsureAsync(tenantId, { userId, phoneE164, profileName, username })` en Customers, **antes** y
**fuera** de la transacción de Messaging (son dos `DbContext` y dos unidades de trabajo):

1. Cliente con ese `whatsapp_user_id` → `Existing`.
2. Si hay teléfono: el de `phone_e164` igual (D-M7: el más viejo, luego el id menor). Si no tiene
   BSUID, se le pone. Si ya tiene **otro** (la misma persona desde otro portafolio, o un número
   reciclado), se vincula sin pisarlo (D-A6). → `Linked`.
3. Si no: `Customer.CreateIncomplete` (§6.2) con auditoría, commit → `Created`. Si el commit choca
   con `IX_customers_tenant_whatsapp_user_id` (otro pod lo creó), se relee por BSUID → `Existing`.

**Idempotencia.** Si Customers commiteó y la transacción de Messaging falla, el reintento de la
entrega encuentra el cliente por BSUID (`Existing`): no hay duplicado. El evento se decide en la
ingesta por la **transición** de `customer_id` de `NULL` a un valor: `CustomerCreated` si el
resultado fue `Created`, si no `CustomerLinked`. Un reintento después de una falla a mitad dice
`CustomerLinked` donde debió decir `CustomerCreated`; se acepta (es informativo).

**Herencia del asignado** (sólo al crear la conversación): la conversación más reciente del mismo
cliente en el tenant (`IX_conversations_tenant_customer_activity`, cualquier conexión y estado) con
`assigned_member_id`; si `IMessagingAssignees.CanReplyAsync` lo confirma, la nueva nace asignada a
esa membresía y lleva el evento `Inherited`. Si no, nace sin asignar.

### 8.3 Cambio de número

Dos señales, procesadas igual y de forma idempotente (la segunda no encuentra nada que cambiar):

- `field = "user_id_update"`: rutas por `metadata.phone_number_id` si viene; si no, por `entry.id`
  (WABA) con `FindByAccountAsync` (base §8.2). El BSUID es por portafolio, así que aplica a todas las
  conexiones de esa WABA.
- Mensaje de sistema `user_changed_user_id`: ruta por `phone_number_id`, como cualquier mensaje. El
  BSUID nuevo es `system.user_id`; el anterior es `from_user_id` si es distinto, y si no, el que
  dice el cuerpo (`changed from <OLD> to <NEW>`). Se aplica **antes** que los entrantes del mismo
  change (también con `Paused` o el módulo apagado): si en esa entrega viene el primer mensaje con el
  BSUID nuevo, cae en la conversación movida en vez de abrir otra. Su evento se fecha 4 ms antes del
  entrante más temprano del change (o a la hora de proceso si no hay entrantes o si ésa es anterior),
  por debajo de los eventos de la ingesta (−1 a −3 ms), para que quede antes en el hilo.

Por cada conexión de la ruta, en una transacción de Messaging:

1. `UPDATE conversations SET user_id = @current, wa_id = COALESCE(@waId, wa_id), version = version + 1,
   updated_at = @now WHERE connection_id = @c AND user_id = @previous RETURNING id`.
2. Si choca con `IX_conversations_connection_user` (ya existe una conversación con el BSUID nuevo,
   porque un mensaje nuevo llegó antes que la señal): **no se fusionan** (D-A7). La vieja conserva su
   BSUID y queda como historia; las dos llevan el evento.
3. Evento `ContactChangedNumber` en cada conversación tocada.

Después, por tenant: `ReplaceWhatsAppUserIdAsync(previous, current)`. Si el BSUID nuevo ya es de
otro cliente, no se toca y se registra en el log. El teléfono del cliente **no** se cambia: es dato
maestro y Meta no siempre manda el nuevo.

### 8.4 Tomar, transferir, liberar

`POST …/take | transfer | release`, `If-Match`:

1. Validador (`memberId` no vacío en `transfer`) → tenant, permiso `manage` y módulo → conversación
   del tenant (404) → membresía activa de quien llama (403 `authorization.denied` si no tiene,
   como `SendMessage.cs:59-60`).
2. `transfer`: `CanReplyAsync(tenantId, memberId)`; si no → 422
   `messaging.conversation.assignee_cannot_reply`. Un `memberId` de otro tenant responde lo mismo:
   no confirma que exista.
3. `EnsureVersion` (412) → `Take`/`TransferTo`/`Release` en el agregado → evento y auditoría en la
   misma transacción → commit (`version` como token de concurrencia, 412 en la carrera).
4. 200 `ConversationSummary` armado como `GET /conversations/{id}`.

Transferirse a uno mismo es un `Take`. Una conversación `Resolved` también se toma, transfiere o
libera: la asignación es por persona, no por estado.

### 8.5 Enviar con autoasignación y cita (delta sobre base §8.3)

Orden nuevo de chequeos:

1. Validador → tenant, permiso y módulo → conversación del tenant (404) → membresía de quien envía.
2. **Idempotencia sobre lo ya commiteado:** `SELECT` del mensaje por `(conversation_id,
   client_id)`. Si existe y no es `Failed` → 201 con ese mensaje, sin mirar nada más («lo que ya
   salió, ya salió», base §8.3).
3. `status = Open` (`not_open`) → ventana (`window_closed`).
4. **`replyTo`** si vino: el mensaje existe en esta conversación, tiene `wamid`, `kind` no es
   `Reaction` ni `Event`. Si no → `ValidationException` con `errors["replyTo"]`. Va antes de la
   asignación porque un request que no pasa la validación no cambia estado: una cita inválida
   deja la conversación sin dueño, sin `AutoTaken`, sin auditoría y con la misma `version`.
5. **Asignación:**
   - asignada a otro → 422 `messaging.conversation.assigned_to_other`;
   - sin asignar → `UPDATE conversations SET assigned_member_id = @me, assigned_at = @now,
     version = version + 1, updated_at = @now WHERE id = @c AND assigned_member_id IS NULL
     RETURNING id`, con el evento `AutoTaken` y la auditoría, en una transacción **corta y propia**
     (§3, corrección 7). Si no actualizó, se relee: si ahora es de otro → 422
     `assigned_to_other`; si es de quien envía, sigue.
6. El reclamo de base §8.3 (`INSERT … ON CONFLICT (conversation_id, client_id)`), que sigue
   cubriendo dos requests en vuelo con el mismo `clientId`, ahora con `reply_to_message_id`.
7. Conexión `Active` → Meta con `recipient` (o `to`, fila vieja) y `context.message_id` si hay
   cita → la tabla de respuestas de base §8.3, sin cambios.

Si Meta rechaza después de la autoasignación, la conversación **queda asignada** a quien intentó
responder: intentar responder es tomar.

### 8.6 Respuestas citadas

- **Saliente:** §8.5 pasos 4 y 7. Meta no dibuja la cita de un mensaje de más de ~30 días (§3); no
  se bloquea: el mensaje sale igual, sin la burbuja citada del lado de la persona.
- **Entrante:** `context.id` → `reply_to_wamid`, y `reply_to_message_id` si QEP tiene ese `wamid` en
  la misma conexión. Si no lo tiene (mensaje enviado desde la app del teléfono en coexistencia, o
  el botón de producto de Meta), `replyTo` sale `null`.
- **Lectura:** `ListMessagesHandler` y `SearchMessagesHandler` resuelven los `replyTo` de la página
  en **una** consulta (`WHERE conversation_id = @c AND id = ANY(@ids)`; en la búsqueda, por
  `tenant_id`), con `direction`, `kind` y el `preview` (texto o leyenda, 200).

### 8.7 Eventos

- Los de asignación, resolver y reabrir (por persona) se insertan en la transacción de su handler,
  con `occurred_at = now`.
- Los de la ingesta (`Reopened` por un entrante, `CustomerCreated`, `CustomerLinked`, `Inherited`)
  se insertan en la transacción de la ingesta justo antes del mensaje, cada uno con su propio
  milisegundo y en orden fijo: `Reopened` **menos 3 ms**, `CustomerCreated`/`CustomerLinked` **menos
  2 ms** e `Inherited` **menos 1 ms** (la conversación se reabre, queda atada a su cliente y hereda el
  asignado de ese cliente). Así quedan justo antes del mensaje que los causó en el orden del hilo
  (`IX_messages_thread`), sin depender del orden de dos UUID v7 generados en el mismo milisegundo.
- `ContactChangedNumber`: `occurred_at = now` del procesamiento (§8.3).
- Un evento no se repite si Meta reenvía el mensaje: sólo se inserta cuando la sentencia 2 insertó
  el mensaje (base §7.5).

## 9. Concurrencia

1. **Dos personas toman a la vez:** las dos mandan la misma `version`; una commitea y la otra recibe
   412. El frontend relee y muestra quién la tiene.
2. **Tomar mientras entra un mensaje:** la ingesta sube `version` (base §7.5), así que un `take` con
   la versión de antes da 412. Es la misma regla que el owner aceptó para resolver.
3. **Autoasignación contra `take`:** la autoasignación es un `UPDATE` condicional
   (`assigned_member_id IS NULL`) que también sube `version`; un `take` con la versión vieja da
   412, y si el `take` ganó primero, el envío recibe `assigned_to_other`.
4. **Dos pods aseguran el mismo cliente:** `IX_customers_tenant_whatsapp_user_id` deja uno; el otro
   relee (§8.2).
5. **Adopción contra creación:** si un pod adopta la conversación vieja por teléfono mientras otro
   (con un mensaje sin teléfono) crea una nueva con el mismo BSUID, la adopción choca con
   `IX_conversations_connection_user`. `MessagingUnitOfWork` lo traduce por nombre de índice y el
   `change` se reintenta: encuentra la nueva por BSUID. La vieja queda sin adoptar.
6. **La ingesta no pisa la asignación:** su `UPDATE` no toca `assigned_*`; la herencia sólo escribe
   en el `INSERT` de una conversación nueva.
7. **Envío y candados:** sigue habiendo un solo candado largo, sobre la fila del mensaje; la
   autoasignación commitea antes (§8.5).
8. **Dueño que cambia durante el envío:** la verificación del dueño en el envío lee la conversación una sola
   vez; si otro asesor la toma o la transfiere entre esa lectura y la llamada a Meta (unos pocos viajes a la
   base), el mensaje del dueño anterior igual sale. Es una ventana residual aceptada, de la misma naturaleza
   que las verificaciones de estado y de ventana del spec base, porque §9.7 prohíbe bloquear la conversación
   durante la llamada a Meta.

## 10. Errores nuevos

| Código | HTTP | Cuándo |
| --- | --- | --- |
| `messaging.conversation.assigned_to_other` | 422 | Enviar a una conversación asignada a otra membresía |
| `messaging.conversation.assignee_cannot_reply` | 422 | `transfer` a una membresía que no es activa del tenant o cuyos roles no conceden `manage` |
| `quotation.quotation.client_incomplete` | 422 | Crear, cambiar cliente, enviar o convertir en pedido con un cliente incompleto |
| `validation.failed` | 422 | `errors["replyTo"]`, `errors["memberId"]`, `errors["assigned"]` (Messaging); `errors["isComplete"]` (Customers) |
| `concurrency.conflict` / precondición | 412 / 428 | `If-Match` en `take`, `transfer`, `release` |
| `authorization.denied` | 403 | Quien llama no tiene membresía activa (take/transfer/release/envío) |

Los dos de Messaging van a `MessagingErrorCodes` (`MessagingErrorCodes.cs`) como
`MessagingDomainException` → 422 por el mapeo central (`ApiExceptionHandler`).

## 11. Seguridad

- **Aislamiento del `memberId`:** `CanReplyAsync` busca la membresía **dentro del tenant de la
  ruta**; una de otro tenant responde igual que una inexistente (422 `assignee_cannot_reply`), sin
  confirmar nada. `GET /messaging/assignees` sólo lista membresías del tenant.
- **Los eventos no filtran nada:** `details` guarda tipo e ids; los nombres se resuelven al leer
  para quien ya tiene `read` en ese tenant. Ni tokens, ni teléfonos, ni BSUIDs, ni textos de Meta.
- **BSUID en logs:** es un identificador personal; se registra igual que hoy el phone number id
  (sin contenido), nunca junto con el texto del mensaje.
- **Cliente creado por el sistema:** la auditoría lo marca con su propia acción y actor vacío; nadie
  puede crear un incompleto por HTTP (no hay endpoint que lo haga).

## 12. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:**

- Parser: entrante sólo con BSUID (sin `from`) → mensaje con `UserId` y `WaId` nulo; sin
  `from_user_id` → se salta; `from` inválido con BSUID válido → entra sin teléfono; `contacts`
  indexado por `user_id` con `username`; `context.id` → `QuotedWamid`; `user_id_update` y
  `user_changed_user_id` → `UserIdChange` (anterior por `from_user_id` y por el cuerpo).
- `Conversation`: `Take`/`TransferTo`/`Release` (versión, no-op sin versión, evento devuelto),
  forma del BSUID.
- Mapa `direction`/`kind` de ida y vuelta con `System`/`Event`; armado de `event` y `replyTo` en
  `MessageMapping`.
- `Customer.CreateIncomplete` (nombre por orden de respaldo, país por prefijo del BSUID, prefijo
  desconocido → región del teléfono → `null`), `Complete` (CUC, libreta, `Complete`),
  `NormalizeWhatsAppUserId`. Poner el BSUID sin pisar otro se prueba en integración, contra el
  `UPDATE` condicional del repositorio.
- `QuotationCustomerEligibility`: incompleto → `client_incomplete` antes que `client_cuc_missing`.
- Validadores: `replyTo`, `memberId`, `assigned`, `isComplete`.

**Integración (Testcontainers + Meta falso):**

- **Entrante sólo con BSUID:** crea conversación con `user_id`, `wa_id` nulo, cliente incompleto
  con nombre de perfil, evento `CustomerCreated` antes del mensaje.
- **Adopción:** conversación vieja con sólo `wa_id`; llega un entrante con BSUID y teléfono → la
  misma conversación gana `user_id`, sin crear otra.
- **Envío:** con BSUID el cuerpo a Meta lleva `recipient` y **no** `to`; fila vieja lleva `to`;
  con cita lleva `context.message_id`.
- **Cambio de número** por las dos señales, en cualquier orden y repetidas: conversación re-keyed,
  cliente con el BSUID nuevo, un solo `ContactChangedNumber` por señal efectiva; choque con una
  conversación nueva → no fusiona, las dos con evento.
- **Carrera de cliente:** N entregas concurrentes del mismo BSUID nuevo → un cliente, una
  conversación, cero errores.
- **Cliente por teléfono:** existe con ese `phone_e164` → `CustomerLinked` y BSUID puesto; si ya
  tenía otro BSUID, vinculado sin pisarlo.
- **Take:** 200 con `assignedTo`; dos `take` con la misma versión → uno 200 y otro **412**; sin
  `If-Match` → 428; take de una ajena → evento `Taken` con `previous`.
- **Transfer:** a un `advisor` → 200 y evento; a una membresía con un rol sin `manage` → 422
  `assignee_cannot_reply`; a una de otro tenant → el mismo 422; a una removida → el mismo 422.
- **Release:** deja `assignedTo: null`; sin asignar → 200 sin subir versión.
- **Enviar a la de otro** → 422 `assigned_to_other` y **cero** llamadas a Meta.
- **Autoasignación:** enviar a una sin asignar → 201, conversación asignada, evento `AutoTaken`;
  Meta rechaza → sigue asignada.
- **Idempotencia y asignación:** reintento de un `clientId` ya enviado después de que otro tomó la
  conversación → 201 con el mismo mensaje.
- **Herencia:** cliente con conversación asignada en la conexión A escribe por la B → nace asignada
  al mismo, evento `Inherited`; si ese miembro ya no tiene `manage` → nace sin asignar.
- **Resolver no libera**; un entrante sobre una resuelta la reabre con evento `Reopened` sin actor.
- **Eventos:** no suben `unreadCount`, no cambian `lastMessage` ni el orden de la lista, la búsqueda
  del historial no los encuentra, `hasMore` del hilo los cuenta, salen con `status: "Delivered"`.
- **Cita en los dos sentidos:** `replyTo` de otra conversación, sin `wamid`, de una reacción o de un
  evento → 422 `validation.failed` en `replyTo`; entrante que cita un saliente nuestro →
  `replyTo` con `direction: "Outbound"`; entrante que cita algo desconocido → `replyTo: null` y
  `reply_to_wamid` guardado.
- **Lista:** `assigned=me|none|all`, `counts.mine` y `counts.unassigned` sólo abiertas; búsqueda
  por `username` y por nombre del cliente vía `customer_id`.
- **Customers:** lista con `isComplete` y filtro; `PUT` completo sobre un incompleto → `Complete`
  con CUC; `PUT` incompleto → 422 `validation.failed` como hoy; export con la columna; el `CHECK`
  rechaza un `Complete` sin CUC (prueba directa de SQL); migración sobre una base con clientes
  viejos sin teléfono ni correo no falla.
- **Quotations:** crear, cambiar cliente, enviar y convertir con un incompleto → 422
  `quotation.quotation.client_incomplete`.
- **Reporting:** el reporte de clientes no cuenta incompletos.

**Arquitectura:** `MessagingLayerTests` sin referencias nuevas a Customers, Tenancy.Infrastructure ni
Authorization; los adaptadores viven en Bootstrapper.

**Fixtures:** los cuerpos de webhook de las pruebas existentes no traen `from_user_id`; se les
agrega (con la regla nueva se saltarían). El harness de Messaging gana un helper para sembrar un
miembro con y sin `manage`.

**Corridas:** por tarea, sólo las clases tocadas más `ArchitectureTests`; la suite completa una vez
al final, comparada por nombre contra `develop` (base §12).

## 13. Riesgos y decisiones

### Decisiones tomadas sin el owner (a ratificar)

| # | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-A1 | `GET /messaging/assignees` (permiso `manage`) para el selector de transferir | Usar otro listado de miembros |
| D-A2 | `take` de quien ya la tiene y `release` sin asignado → 200 sin cambios, no 422 | Dos códigos nuevos |
| D-A3 | Evento `Inherited` al heredar el asignado (no estaba en la lista del owner; sin él, la conversación aparece asignada sin explicación) | Quitar un caso del mapa |
| D-A4 | Código `quotation.quotation.client_incomplete` (convención del módulo) en vez de `quotations.customer.incomplete` | Renombrar una constante y su etiqueta en el frontend |
| D-A5 | `status` de un evento = `Delivered` | Hacer la columna nullable |
| D-A6 | Coincidencia por teléfono con un cliente que ya tiene **otro** BSUID: se vincula sin pisarlo | Crear un incompleto aparte (duplicados con número reciclado) |
| D-A7 | Cambio de número con una conversación ya creada con el BSUID nuevo: no se fusionan | Una fusión que mueva mensajes |
| D-A8 | `customer.isComplete` en `ConversationSummary` y `MessageHit` | Un campo |
| D-A9 | Auditoría `customers.customer.created_from_messaging` con actor `Guid.Empty`; `customers.customer.completed` al completar | Agregar metadatos a `ICustomersAuditPublisher` |
| D-A10 | `counts.mine` y `counts.unassigned` sólo cuentan abiertas | Quitar un filtro |
| D-A11 | `CK_customers_complete_fields` sólo sobre columnas hoy `NOT NULL`; teléfono, correo y ciudad siguen en el validador. El `DEFAULT 'Complete'` de `completeness` se queda para el despliegue con el pod viejo vivo (§7.2) y se quita en una migración posterior, cuando ya no pueda correr ningún binario anterior | Un backfill y ampliar el `CHECK` |
| D-A12 | `customer_id` en la conversación reemplaza la decisión 10 del spec base (emparejar al leer); el teléfono queda sólo para filas viejas | — (es lo que el owner pidió; se anota porque contradice el spec base) |
| D-A13 | `assignedTo.isMe` calculado en el servidor, porque la SPA no conoce su `memberId` | Un campo; sin él, el frontend no sabe si la conversación es suya |

### Riesgos

- **`user_id_update` no verificado** (§3): no se vio un cuerpo de ejemplo ni si se suscribe aparte.
  Las dos señales se procesan; el HANDOFF pide suscribir `user_id_update` si aparece en la lista de
  campos de la app, y la primera señal real se revisa en el log antes de darlo por cerrado.
- **Frontend viejo contra backend nuevo:** `direction: "System"` es error de contrato para el SPA
  actual (base §5.4), y `contact.waId` puede venir `null` (`thread-header.tsx:56` dibuja
  `+{waId}`). Backend y frontend se despliegan juntos; como Messaging no está desplegado, no hay
  usuarios con la versión vieja.
- **Duplicados entre portafolios:** una persona que escribe a dos conexiones de portafolios
  distintos, sin teléfono en el webhook, crea dos clientes incompletos (BSUID distinto por
  portafolio). Unirlos por `parent_user_id` está fuera de alcance; se guarda para hacerlo después.
- **Números reciclados:** con D-A6, el dueño nuevo de un número puede quedar vinculado al cliente del
  dueño anterior. El asesor lo ve en el encabezado; desvincular queda para otro slice.
- **412 en conversaciones muy activas:** cada entrante sube la versión; tomar una conversación que
  recibe mensajes seguidos puede pedir reintentar. El frontend relee y reintenta una vez.
- **Asignado que pierde `manage` o la membresía:** la conversación queda a su nombre hasta que otro
  la tome (tomar de otro está permitido).

### DECISIÓN-PENDIENTE

Ninguna abierta.

## 14. Entregables

**Backend (`qep-backend`, rama `feature/mensajeria-asignacion`):**

- Código y pruebas de §6–§12, con las migraciones `AddBsuidAssignmentAndEvents` (Messaging) y
  `AddCustomerCompleteness` (Customers), generadas con el factory de diseño (CLAUDE.md).
- README «Mensajería»: identidad por BSUID, asignación, eventos; el comando de prueba local con
  un cuerpo de webhook que traiga `from_user_id`.
- `CLAUDE.md`: gotcha de que la clave de la conversación es el BSUID y el teléfono puede faltar, y
  de que un cliente puede ser incompleto (sin CUC) y Quotations lo rechaza.
- HANDOFF para el owner en Meta: suscribir `user_id_update` si la app lo ofrece como campo.

**Frontend (`qep-frontend`, plan aparte sobre `origin/feature/mensajeria-whatsapp`):**

- Tipos: `ConversationContact` con `userId`/`waId`/`username` opcionales, `assignedTo`,
  `customer.isComplete`, `counts.mine`/`unassigned`, `MessageDirection` con `System`,
  `MessageKind` con `Event`, `Message.event` y `Message.replyTo`.
- Lista: pestañas o filtro «Mías / Sin asignar / Todas» con sus contadores; asignado en la fila.
- Hilo: encabezado con nombre, `username` y teléfono si vienen; quién la tiene; botones Tomar,
  Transferir (selector de `GET /messaging/assignees`) y Liberar con `If-Match` y manejo de 412;
  si es de otro, el compositor se reemplaza por «La tiene X — Tomar».
- Eventos como línea centrada; cita en la burbuja y en el compositor (responder a…).
- Envío optimista: burbuja con reloj, ✓ o ⚠ con motivo y «Reintentar» (mismo `clientId`),
  `clientId` persistido antes del primer intento, cola serial por conversación («En espera» detrás
  de un fallido hasta reintentar o descartar), timeout de 15 s, error de red → «No enviado».
- Clientes: insignia «Incompleta», filtro `isComplete`, flujo de completar desde la ficha y desde el
  encabezado del hilo.
- Cotizaciones: etiqueta para `quotation.quotation.client_incomplete` con enlace a completar la
  ficha (junto a las de `client_cuc_missing` en `orders.api.ts`).

## 15. Orden de implementación sugerido

1. Customers: `completeness`, `whatsapp_user_id`, migración, `CreateIncomplete`/`Complete`,
   `ICustomerWhatsAppDirectory`, lista/filtro/export, barrido de consumidores.
2. Quotations y Reporting: `client_incomplete` y exclusión del reporte.
3. Messaging, identidad: migración, parser con BSUID, ingesta con adopción, envío con `recipient`,
   `ContactDto`, búsqueda de la lista. Fixtures actualizados.
4. Messaging, cliente: `EnsureAsync` en la ingesta, `customer_id`, `FindRefsAsync`, fallback viejo.
5. Messaging, eventos: enums, `CHECK`, escritura en resolve/reopen e ingesta, lectura en el hilo.
6. Messaging, asignación: agregado, `IMessagingAssignees`, endpoints, autoasignación, herencia,
   filtros y contadores, auditoría.
7. Messaging, citas: envío y entrante, `replyTo` en lectura.
8. Cambio de número (las dos señales).
9. Suite completa una vez, README, `CLAUDE.md`, HANDOFF.
10. Con los contratos fijos y publicados en la rama, el plan del frontend (§14).

## Historial de revisión

- 2026-10-10: spec escrito sobre las decisiones 1–7 del owner, el código de `develop` `2d68627` y la
  documentación de Meta (§3). Correcciones por el código: código de error con la convención de
  Quotations, columna `completeness` (Customers ya tiene un estado), `CHECK` sólo sobre columnas hoy
  `NOT NULL`, autoasignación fuera del candado largo, `GET /messaging/assignees`. D-A1 a D-A12 a
  ratificar.
