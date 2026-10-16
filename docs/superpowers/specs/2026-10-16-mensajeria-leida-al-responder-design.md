# Mensajería: abrir no marca leída; responder, resolver o el botón sí

**Fecha:** 2026-10-16
**Módulos:** Messaging (Domain, Application, Infrastructure). Sin migración, sin permisos nuevos, sin
endpoints nuevos, sin códigos de error nuevos.
**Frontend:** `qep-frontend`, feature `messaging`.
**Es un delta sobre** `2026-10-14-mensajeria-marcar-no-leida-design.md` («el spec de no leída») y el
§8.4 de `2026-10-09-mensajeria-whatsapp-design.md` («el spec base»). Lo que no se toca acá sigue igual.

## 1. Por qué

Hoy abrir un hilo lo marca leído solo: `useConversationActions` manda `POST …/read` apenas ve
`unreadCount > 0`. El owner usa la marca de no leída como **lista de pendientes por contestar**:
entra a mirar un mensaje que no puede o no quiere contestar en ese momento, sale, y el pendiente
desapareció. «Marcar como no leída» (spec de no leída) lo arregla a mano, pero hay que acordarse cada
vez.

La marca tiene que irse cuando el pendiente de verdad se cerró, no cuando alguien lo miró.

## 2. Qué cambia

| Evento | Hoy | Con este cambio |
| ------ | --- | --------------- |
| Abrir el hilo | `unread_count = 0` + acuse a Meta | **Nada** |
| Un asesor responde desde el hilo y Meta lo acepta | Nada | `unread_count = 0` + acuse a Meta |
| Resolver | Nada | `unread_count = 0` + acuse a Meta |
| Botón «Marcar como leída» (nuevo, en el hilo) | — | `POST …/read`, el de siempre |
| Entra un mensaje del cliente | `+1` | `+1` (igual) |
| «Marcar como no leída» (menú «Más acciones» del encabezado) | Igual | Igual |

**El cliente ve el doble check azul sólo cuando le responden, cuando alguien marca leída o cuando se
resuelve** (decisión del owner: nunca por abrir).

### 2.1 Decisiones del owner

- **D1.** Abrir el hilo no toca la marca ni manda acuse.
- **D2.** Responder la quita sola.
- **D3.** La marca sigue siendo **una por conversación, compartida por el equipo**: si responde
  cualquier asesor, se le quita a todos. Nada de marcas por persona.
- **D4.** El acuse a Meta (check azul) se manda al responder o al marcar leída, nunca al abrir.
- **D5.** El control manual es una **barra sobre el compositor** (opción C de los mockups), no un
  botón en la fila de la lista ni un chip en el encabezado.
- **D6.** Resolver quita la marca.

### 2.2 Fuera de alcance

- Marca por asesor (D3).
- Acción en la fila de la lista (D5; además el spec de no leída §1 explica por qué la fila no admite
  botones).
- Renombrar el filtro «No leídas» o el contador del título de la pestaña: siguen leyendo
  `unreadCount` / `counts.unread`, que ahora significa «pendiente de respuesta» sin cambiar de nombre.
- Campañas: un envío masivo no es una respuesta a esa persona y **no** quita la marca.

## 3. Backend

### 3.1 Responder quita la marca

Cuenta como respuesta todo saliente que un asesor manda a esa persona y que **Meta acepta**. Hay cinco
caminos y todos terminan en la rama `SendOutcome.Sent` de `OutboundGuards.SettleAsync`:

- texto (`SendMessageHandler`), nota de voz (`SendAudioMessageHandler`), plantilla
  (`SendTemplateMessageHandler`) y adjunto (`SendMediaMessageHandler`). El frontend hoy no envía
  adjuntos, así que este camino sólo tiene prueba de backend;
- `POST /conversations` cuando la conversación ya existía (`StartConversationHandler` →
  `TemplateOutbound`). Es una plantilla que un asesor le manda a esa persona: también la responde.

Un envío que Meta rechaza (`Failed`, conexión caída) no quita nada, porque el cliente no recibió
respuesta. Las campañas van por `CampaignDispatch`, que tiene su propio `SettleAsync` y no pasa por
`OutboundGuards`: no tocan la marca.

**Sólo se limpia lo que el asesor tenía a la vista.** Entre que el envío se admite (`permit.Now`) y
que Meta contesta pueden pasar hasta 10 s. Además, la cola del frontend sigue enviando con el hilo
cerrado, y un fallido puede reintentarse mucho después con el mismo `clientId`. Un entrante que llegó
en ese hueco es un pendiente nuevo y no puede borrarse ni recibir el check azul sin que nadie lo lea.
En la rama `Sent`, después de `CommitSentAsync`:

1. Un `UPDATE` atómico, sin comparar `version` (spec base §8.4), que deja `unread_count` en la cantidad
   de entrantes con `occurred_at > permit.Now`. Es el mismo conteo con `UPDATE … FROM (SELECT count(*)
   …)` que usa `MarkUnreadFromAsync`, pero sin `GREATEST`, porque acá sí baja. Si no había nada que
   bajar, no toca nada. El instante de admisión tiene que llegar hasta `SettleAsync`; cómo se pasa
   queda a criterio de quien construye.
2. Si bajó algo, `notifier.ConversationChangedAsync` (la lista de las otras pestañas lo relee) y el
   **mismo acuse best effort** que `MarkConversationReadHandler`, con las mismas guardas
   (`CanAcknowledgeReading`, conexión activa, 5 s, y un fallo sólo se loguea). En WhatsApp va
   `MarkReadAsync` con el `wamid` del **último entrante anterior a `permit.Now`**, no con
   `last_inbound_wamid`. En Instagram va `MarkSeenAsync`, que es por persona: se manda sólo si el
   conteo quedó en 0.
3. Va **después** de que el saliente está commiteado y con `CancellationToken.None`, como el resto de
   ese tramo: un fallo acá nunca convierte un envío exitoso en error. La respuesta del endpoint de envío
   no cambia.

El acuse hoy vive dentro de `MarkConversationReadHandler`; se extrae a una pieza compartida que usan
los dos caminos (y el de resolver). Nombre y ubicación, a criterio de quien construye.

**Concurrencia con `version`.** El `UPDATE` sube `version`, como `/read`. No es un caso nuevo: el
primer envío en una conversación sin asignar ya la sube (autoasignación en `AdmitAsync`), y por eso
`settleSent` en `outbox-drainer.ts` invalida cabecera y lista. **Decisión de diseño: no se cambia la
semántica de `version`.** El requisito es no abrir una clase nueva de 412. Después de un envío
confirmado, el `version` se refresca con la misma invalidación que ya existe. Después del `/read` de
la barra, `markRead` invalida la conversación al terminar, cosa que hoy no hace. Si un «Resolver» sale
con el envío todavía en vuelo y choca, lo atrapa el manejo de 412 que ya existe (`shouldReload`),
igual que pasa hoy con la autoasignación. No hay reintento automático del resolver.

### 3.2 Resolver quita la marca

`Conversation.Resolve` pone `UnreadCount = 0`. Va en el mismo `SaveChanges` que la resolución, el evento
y la auditoría, con el `If-Match` de siempre. Después del commit, si había no leídos, el mismo acuse
best effort de §3.1. Reabrir no restaura la marca.

### 3.3 `/read` y `/unread`

Sin cambios de contrato. `/read` pasa a llamarse sólo desde el botón de la barra.

## 4. Frontend

### 4.1 Abrir no marca

Se borra el efecto de `useConversationActions` que manda `/read` al ver `unreadCount > 0`, junto con
`lastRead` y la bandera `readMuted` (spec de no leída §4.1): existían sólo para que el `/read`
automático no deshiciera una marca recién puesta. «Marcar como no leída» sigue volviendo a la bandeja
como hoy.

`useConversationActions` expone `markRead()`: parche optimista `unreadCount: 0` con
`patchConversationCaches`, `POST …/read` y, al terminar, invalidación de la conversación para traer el
`version` nuevo (§3.1).

### 4.2 La barra

Componente nuevo en `features/messaging/components/`, montado en `conversation-thread-page.tsx` justo
encima de `MessageComposer`.

- **Se ve** con `canManage && unreadCount > 0`. No depende de `canReply`: con la ventana de 24 h
  cerrada o la conversación tomada por otro igual se puede marcar leída.
- **Copy:** «Sigue sin leer para el equipo. Se marca como leída cuando respondas.» y el botón
  «✓ Marcar como leída» (icono `Check` de lucide). Español neutro, tuteo.
- **Al hacer clic** desaparece de inmediato (optimista).
- **Si `/read` falla:** la barra vuelve (se invalidan conversación y lista para que el poll diga lo
  cierto) con un error en línea: «No pudimos marcarla como leída. Intenta de nuevo.» Sin modal.
- **Mientras el POST viaja** el botón queda deshabilitado; no hay doble envío.
- Accesible: es un `role="status"` con el botón como `button` real; no roba el foco del compositor.

### 4.3 Después de responder y de resolver

- **Envío exitoso:** **sin** parche optimista de `unreadCount`, porque el backend puede dejarlo en más
  de 0 si entró algo en el hueco (§3.1) y un 0 forzado parpadearía. Alcanza con la invalidación de
  cabecera y lista que `settleSent` ya hace. Con el hub conectado llega al instante, y por polling en el
  siguiente ciclo. La barra desaparece cuando llega el dato nuevo, no al tipear.
- **Envío fallido:** la marca se queda (la barra sigue).
- **Resolver:** la respuesta ya trae `ConversationSummary` con `unreadCount: 0`; no hace falta nada
  extra.
- **Entra otro mensaje con el hilo abierto:** el poll o el hub traen `unreadCount > 0` y la barra
  vuelve sola.

## 5. Pruebas

**Backend** (integración con Postgres real, junto a `ConversationLifecycleApiTests` y las de envío):

- Texto, adjunto, nota de voz, plantilla y `POST /conversations` sobre una existente, aceptados con
  `unread_count = 2` → queda 0, un acuse a Meta con el último `wamid` entrante y `ConversationChanged`
  notificado.
- Un entrante que llega entre la admisión y la respuesta de Meta → `unread_count = 1`, y el acuse va con
  el `wamid` anterior, no con el del entrante nuevo. En Instagram no hay `mark_seen`.
- Con `unread_count = 0` → ningún acuse.
- Envío que Meta rechaza → `unread_count` intacto, ningún acuse.
- Acuse que falla → el envío responde igual que sin el cambio.
- Instagram → `MarkSeenAsync`.
- Resolver con `unread_count = 3` → 0 y acuse; resolver con 0 → sin acuse.
- Campaña → no toca `unread_count`.

**Frontend** (`$conversationId.test.tsx` con `render-route`, más el hook):

- Abrir un hilo con `unreadCount: 2` **no** manda `/read`, y la fila sigue «… 2 sin leer».
- La barra aparece con `manage` y `unreadCount > 0`; sin `manage` o con 0, no.
- Clic → un solo `POST …/read`, la barra y el badge de la fila se van antes del siguiente poll.
- `/read` que falla → la barra vuelve con el error en línea.
- Enviar un mensaje → la barra se va; un envío fallido la deja.
- Resolver → la barra se va.
- Marcar leída con la barra y resolver enseguida → ningún 412 (el `version` se refrescó).

## 6. Despliegue

**El backend primero.** Un frontend nuevo sobre el backend viejo dejaría marcas que no se quitan al
responder, solo con el botón. Un backend nuevo con el frontend viejo funciona: el `/read` al abrir
sigue existiendo. Sin migración; rollback sin efectos.

El spec de no leída queda parcialmente sustituido: donde dice «abrir el hilo lo vuelve a 0» (§2, §7.1)
y en todo su §4.1, rige este documento. Su §7 («desde este mensaje») ya no tiene superficie en el
frontend (se quitó en `92ea5e5`). El endpoint sigue en el backend y este cambio no lo toca.

## 7. Decisiones de diseño (no del owner)

Las tomé al escribir el diseño. Revísalas al leerlo:

- **R1.** `POST /conversations` sobre una conversación existente cuenta como respuesta (§3.1).
- **R2.** Sólo se limpia lo que llegó antes de admitir el envío. Un entrante en el hueco sigue
  pendiente y no recibe check azul (§3.1).
- **R3.** Resolver también manda el check azul, porque quitar la marca siempre lo manda (§3.2).
- **R4.** No cambia la semántica de `version`. No se abre ninguna clase nueva de 412 y no hay
  reintento automático (§3.1).
- **R5.** Después de enviar no hay parche optimista de `unreadCount`: la barra se va cuando llega el
  dato del servidor (§4.3).
- **R6.** El error de la barra se muestra en línea, sin modal (§4.2).
