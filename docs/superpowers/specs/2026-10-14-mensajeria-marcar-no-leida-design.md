# Mensajería: marcar una conversación como no leída

**Fecha:** 2026-10-14
**Módulos:** Messaging (Application, Infrastructure, Api), Bootstrapper (registro del handler).
Sin migración, sin permisos nuevos, sin códigos de error nuevos.
**Estado:** diseño acotado presentado en conversación el 2026-10-14 y construido con TDD en
`feat/messaging-marcar-no-leida` de los dos repos. Sin mergear: espera a que terminen otras sesiones
que están tocando Messaging.
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` (en adelante, «el spec base»), en
particular su §8.4 (marcar leído). Todo lo que este documento no toca sigue como está ahí.
**Frontend:** `qep-frontend`, misma rama, desde `develop`.

## 1. Contexto y alcance

Abrir un hilo lo marca leído solo: `useConversationActions` manda `POST …/read` en cuanto ve
`unreadCount > 0`. No hay forma de deshacerlo. Quien abre una conversación para mirarla de pasada
pierde la señal de que la tiene pendiente, y en una bandeja compartida eso significa que nadie la
responde.

La funcionalidad es la de WhatsApp y cualquier cliente de correo: **«Marcar como no leída»** deja la
conversación con el indicador de pendiente para volver después.

### Dentro de alcance

- Un endpoint nuevo, espejo de `/read`.
- La acción en el encabezado del hilo, con su vuelta a la lista.

### Fuera de alcance

- **Marcar no leído un mensaje puntual.** El backend no guarda lectura por mensaje: sólo
  `unread_count` por conversación. Marcar «desde este mensaje» exigiría un modelo
  nuevo y no lo pidió nadie.
- **La acción en la fila de la lista.** La fila entera es un `Link`; un botón dentro de un enlace es
  HTML inválido y rompe el teclado. Se marca desde el hilo abierto. Si la pantalla lo necesita en la
  lista, es otro cambio de UI, no de contrato.
- **Acuse a Meta.** WhatsApp no tiene un estado «no leído» que se pueda devolver: un `read` enviado no
  se retracta. La marca vive sólo en QEP.

## 2. Semántica

«No leída» es un **marcador**, no un conteo real. La acción pone `unread_count = 1` **sólo si estaba
en 0**:

| Antes | Después | Respuesta |
| ----- | ------- | --------- |
| `0` | `1`, `version + 1`, `updated_at = now` | 204 |
| `> 0` | sin cambios | 204 |
| no existe en el tenant | sin cambios | 404 |

Con no leídos reales no hay nada que marcar: pisar un 3 con un 1 mentiría sobre cuántos mensajes
llegaron. El `1` es el mismo número que vería la pantalla si hubiera entrado un mensaje nuevo, así
que la fila, el badge y `counts.unread` lo dibujan sin saber que fue una marca manual.

Un entrante posterior suma sobre el `1` como sobre cualquier contador (§7.5 del spec base). Abrir el
hilo lo vuelve a 0 por el `/read` de siempre.

## 3. Backend

### 3.1 Contrato

`POST /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/unread`

- **Permiso:** `messaging.conversation.manage`, como `/read`. Con sólo `read` → 403.
- **Sin `If-Match`.** Mismo motivo que `/read` (spec base §8.4): la ingesta sube `version` por SQL en
  cada entrante, así que un token de concurrencia convertiría la acción en 412 por un mensaje que
  nadie estaba mirando.
- **Sin cuerpo.** Responde **204** siempre que la conversación exista en el tenant.
- **403** con el módulo `messaging` apagado, aunque el permiso esté (decisión 5 del spec base).
- **404** para un id de otro tenant, igual que `/read`: el handler llega a `ExistsAsync` sólo después
  de que la revalidación de tenant y permiso pasó.

### 3.2 Piezas

| Capa | Qué |
| ---- | --- |
| Application | `MarkConversationUnreadCommand` y `MarkConversationUnreadHandler` en `ConversationLifecycle.cs`, junto a los de leído. |
| Application | `IConversationRepository.MarkUnreadAsync(tenantId, conversationId, now, ct)` → `bool`. |
| Infrastructure | `ConversationRepository.MarkUnreadAsync`: un `UPDATE … SET unread_count = 1, updated_at, version = version + 1 WHERE id AND tenant_id AND unread_count = 0`. |
| Api | `MapPost("/conversations/{conversationId:guid}/unread")` con `RequireAuthorization(ConversationManage)`. |
| Bootstrapper | `AddScoped<ICommandHandler<MarkConversationUnreadCommand, bool>, MarkConversationUnreadHandler>()`. Sin él, 500. |

**Una sentencia, sin cargar el agregado.** Mismo patrón que `MarkReadAsync`: la fila queda con su
candado sólo mientras corre el `UPDATE`, una ingesta concurrente espera y suma después. Cargar y
guardar `Conversation` con EF traería de vuelta el 412 que el endpoint evita.

**Sin auditoría ni evento en el hilo.** `/read` tampoco los registra. Es estado de la bandeja, no una
decisión sobre la conversación como resolver o transferir.

## 4. Frontend

- **Servicio:** `markConversationUnread(tenantId, conversationId)` en `messaging.api.ts`. Un `POST`
  sin versión y sin cuerpo, como `markConversationRead`.
- **Hook:** `useConversationActions` expone `markUnread`. Aplica el parche optimista
  (`unreadCount: 1`) a la cabecera y a la lista con `patchConversationCaches`, y si el POST falla
  invalida las dos para que el siguiente poll diga lo cierto.
- **Vista:** menú «Más acciones» (`MoreHorizontal`) en `ThreadHeader`, a la derecha de
  Resolver/Reabrir, con «Marcar como no leída». Sólo con `manage`, como el resto de acciones.
- **Contenedor:** la ruta `/messaging/$conversationId` llama a `markUnread` y navega a `/messaging`.

### 4.1 Por qué vuelve a la lista

No es cortesía: es lo que hace que la marca sobreviva. El hilo abierto manda `/read` cada vez que ve
`unreadCount > 0`, así que quedarse adentro la deshace en el siguiente poll (5 s). Por eso además
`markUnread` levanta una bandera que apaga el `/read` automático durante el resto del montaje: el
parche optimista pone el `1` antes de que la navegación desmonte el hilo, y sin la bandera ese mismo
render mandaría el `/read`.

## 5. Pruebas

**Backend** (`ConversationLifecycleApiTests`, integración con Postgres real):

- Sube a 1 una sola vez: dos `/unread` seguidos dejan `1|version + 2` (contando el `/read` previo),
  ningún request a Meta, y `GET /conversations` muestra `unreadCount: 1` y `counts.unread: 1`.
- Con `unread_count = 3` responde 204 y deja `3|version`.
- 403 con sólo `read`, 404 desde otro tenant, 403 con el módulo apagado.

**Frontend:**

- `messaging.api.test.ts`: `POST …/unread` sin `If-Match`, con `X-Qep-Client` y sin cuerpo.
- `$conversationId.test.tsx`: el menú marca, navega a `/messaging`, manda un solo `/unread`, ningún
  `/read`, y la fila se lee «Laura Pérez, 1 sin leer» antes del siguiente poll.
- Sin `manage` no aparece «Más acciones».

## 6. Despliegue

Backend y frontend son independientes: el backend primero. Un frontend sin el backend nuevo recibiría
404 en `/unread` y el `onError` del hook devuelve la fila a su estado real. Rollback sin efectos: no
hay migración y un `unread_count = 1` es un valor válido para cualquier versión.
