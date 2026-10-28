# Mensajería: motivo de resolución

**Fecha:** 2026-10-15
**Módulos:** Messaging (dominio, persistencia con migración, endpoints, permiso nuevo). Bootstrapper (rol de
fábrica, definición y política del permiso, registro de handlers). **Frontend:** `qep-frontend`,
`features/messaging/` y `routes/_authenticated/settings/`.
**Estado:** alcance, enfoque y diseño aprobados por el owner en conversación el 2026-10-15. Pendiente de
implementar.
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` («el spec base»), en particular su §8.5
(resolver y reabrir), y sobre `2026-10-10-mensajeria-asignacion-bsuid-design.md` §6.1.5 (eventos del
historial). Lo que este documento no toca sigue como está ahí.
**Ramas:** `feature/motivo-de-resolucion` desde `develop` en `qep-backend`; la homónima desde `develop` en
`qep-frontend`.

## 1. Contexto y alcance

Hoy `POST …/resolve` cierra la conversación sin decir por qué. El negocio quiere saber **por qué se
resuelven** las conversaciones (venta cerrada, sin respuesta, consulta resuelta, spam…). Al resolver, el
asesor puede indicar un motivo **opcional**: lo busca en una lista y, si no existe, lo escribe y se crea en
el acto. La interacción es la de las etiquetas de clientes (`customer-tag-picker`), con un motivo por
resolución.

**Dentro:**

- Un catálogo de motivos por tenant, que se alimenta solo desde el diálogo de resolver.
- `POST …/resolve` con cuerpo opcional: un motivo existente o un nombre nuevo.
- El motivo en el evento `Resolved` del historial y en el resumen de la conversación mientras esté resuelta.
- Gestión del catálogo (renombrar y archivar) con permiso propio, de fábrica en `admin` y `advisor`.

**Fuera** (§7): reportes y filtros por motivo, motivo obligatorio, motivo al reabrir, alta de motivos desde
la pantalla de gestión, color.

## 2. Decisiones

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-R1 | **Un** motivo opcional por resolución. | Decisión del owner. Un conteo por motivo suma exacto; con varios, una resolución contaría en varios. |
| D-R2 | El motivo pertenece a **cada resolución**, no a la conversación: viaja en el evento `Resolved`. La conversación guarda además `resolution_reason_id` del estado vigente, que se limpia al reabrir. | Una conversación se resuelve y reabre muchas veces; cada cierre tiene su porqué. La columna existe sólo para que el resumen lo dibuje sin recorrer el historial (D-R7). |
| D-R3 | Sin color. Sólo nombre. | Decisión del owner. Se lee en el historial y el encabezado, no en la fila de la lista. |
| D-R4 | **Buscar o crear en el mismo `resolve`** (enfoque A aprobado): `{ reasonId }` o `{ reasonName }`. Si el nombre ya existe entre los vigentes (sin distinguir mayúsculas, recortado), se reutiliza; si no, se crea en la misma transacción que la resolución. | BFF: el cliente no tiene que crear antes ni manejar `name_taken` en el caso común, y un resolver que falla (412, `already_resolved`) no deja motivos huérfanos. |
| D-R5 | Borrar es **archivar** (`deleted_at`). Un archivado sale del catálogo y del selector, pero el historial y el resumen lo siguen nombrando. Escribir el nombre de uno archivado crea uno nuevo. | El evento guarda sólo el id y el nombre se resuelve al leer (convención de §6.1.5). Un borrado físico dejaría resoluciones viejas sin nombre. |
| D-R6 | Renombrar cambia el nombre **también en el historial pasado**. | Es para corregir errores de tipeo y unificar variantes; el id es la identidad. |
| D-R7 | `ConversationSummary.ResolutionReason` (`{ id, name }` o `null`): con valor sólo mientras `Status == Resolved` y se resolvió con motivo. | BFF: el encabezado del hilo dibuja «Resuelta · Venta cerrada» y la fila de resueltas puede mostrarlo, sin pedir el historial. |
| D-R8 | Leer el catálogo: `messaging.conversation.read`. Crear en línea: lo que ya pide resolver, `messaging.conversation.manage`. Renombrar y archivar: el nuevo `messaging.resolution_reason.manage`, de núcleo y **de fábrica en `admin` y `advisor`**. | Decisión del owner: los asesores y los admin gestionan. Permiso aparte y no `conversation.manage` para que un tenant pueda quitárselo a un rol custom sin quitarle la bandeja. |
| D-R9 | Tope de **100** motivos vigentes por tenant; nombre de **1 a 60** caracteres después de `Trim`. Reutilizar uno existente nunca choca con el tope. | Mismo orden que `Tag.MaxPerTenant`. Un selector con cientos deja de servir para elegir. |
| D-R10 | Lista completa sin paginar, ordenada por nombre. | El selector filtra en memoria mientras se escribe, como las respuestas rápidas (D-Q7). |
| D-R11 | La carrera de dos asesores que crean **el mismo nombre nuevo en el mismo instante** la arbitra el índice único y sale como 409 `messaging.resolution_reason.name_taken`. El cliente reintenta **una vez el mismo request**: el segundo intento encuentra el motivo y lo reutiliza. | La transacción fallida se revierte entera, así que el `If-Match` sigue vigente. Absorberla en el servidor obligaría a reintentar con un `DbContext` sucio o a abrir una transacción explícita desde Application, que no conoce EF. El caso común (el nombre ya existía antes) lo absorbe D-R4. |

## 3. Backend

### 3.1 Modelo

Tabla nueva `messaging.resolution_reasons`:

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | `uuid` | PK (`Guid.CreateVersion7()`) |
| `tenant_id` | `uuid` | todo método del repositorio lo recibe |
| `name` | `varchar(60)` | recortado, de 1 a 60 caracteres |
| `created_at`, `updated_at` | `timestamptz` | |
| `deleted_at` | `timestamptz null` | archivado (D-R5) |
| `version` | `bigint` | concurrencia optimista del renombrar |

Índice único **parcial y por expresión** `IX_resolution_reasons_tenant_name` sobre
`(tenant_id, lower(name)) WHERE deleted_at IS NULL`. Va en la migración con SQL, como `IX_tags_tenant_name`
de Customers: EF no modela índices por expresión. Su nombre vive en una constante de `MessagingDbContext`
(`ResolutionReasonNameIndex`), como `QuickReplyShortcutIndex`.

Columna nueva en `messaging.conversations`: `resolution_reason_id uuid null`, con FK a
`resolution_reasons(id)` `ON DELETE RESTRICT` (nunca se borra una fila: D-R5).

### 3.2 Dominio

- `ResolutionReason` (agregado nuevo): `Create(id, tenantId, name, now)`, `Rename(name, now)` y
  `Archive(now)`, con `NameMaxLength = 60` y `MaxPerTenant = 100`.
  - Normaliza el nombre con `Trim`.
  - Vacío → `messaging.resolution_reason.name_required`. Largo → `messaging.resolution_reason.name_too_long`.
  - `EnsureTenantCapacity(existingCount)` → `messaging.resolution_reason.limit_reached`.
  - Archivar uno ya archivado no ocurre por API: el handler responde 404 antes (§3.4).
- `Conversation.Resolve(now, Guid? reasonId)` guarda `ResolutionReasonId`. `Conversation.Reopen(now)` lo pone
  en `null`.
- `ConversationEvent` suma `Guid? ReasonId = null`. `ConversationEventJson` lo escribe y lo lee como
  `reasonId`. Un JSON viejo sin el campo se lee como `null`.

### 3.3 Resolver: `POST /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/resolve`

Cuerpo **opcional** (`ResolveConversationRequest`):

```json
{ "reasonId": "0192…" }
{ "reasonName": "Venta cerrada" }
```

- **Sin cuerpo, `{}` o `reasonName` en blanco = sin motivo**, exactamente como hoy. El frontend actual no
  cambia de comportamiento.
- `reasonId` y `reasonName` a la vez → 422 `validation.failed` con `errors.reasonName`.
- `reasonName` de más de 60 caracteres → 422 `validation.failed` con `errors.reasonName`.
- `reasonId` que no está entre los vigentes del tenant (archivado o de otro tenant) → 404
  `messaging.resolution_reason.not_found`.
- `reasonName`:
  - Si coincide con uno vigente (sin distinguir mayúsculas, recortado), lo reutiliza.
  - Si no coincide, lo crea. Si ya hay 100, responde 422 `messaging.resolution_reason.limit_reached`.
  - La carrera simultánea se resuelve como dice D-R11.
- El resto no cambia: `If-Match` obligatorio (428), 412 por versión, 422
  `messaging.conversation.already_resolved`, 403 con el módulo apagado.

**Orden en el handler.** Se comprueban autorización, existencia, versión y `conversation.Resolve` (las reglas
del dominio) **antes** de buscar o crear el motivo. Después se registran el evento con `ReasonId` y la
auditoría, se hace **un solo** `SaveChangesAsync` y por último `notifier.ConversationChangedAsync`, como hoy.
El motivo nuevo es una entidad agregada al `DbContext` (`repository.Add`), no SQL suelto: así entra en la
misma transacción que la resolución y la auditoría.

La violación de `IX_resolution_reasons_tenant_name` la traduce `MessagingUnitOfWork` **por nombre de
índice** a `ResourceConflictException(messaging.resolution_reason.name_taken)` → 409 (como
`customers.tag.name_taken`; una `MessagingDomainException` saldría 422), junto a las que ya traduce.

Responde 200 con el `ConversationSummary`, que ya trae `resolutionReason` (D-R7).

### 3.4 Catálogo: `/api/v1/tenants/{tenantId}/messaging/resolution-reasons`

| Ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /` | `messaging.conversation.read` | 200 `[{ id, name, version }]`, sólo vigentes, por `name` |
| `PATCH /{reasonId}` con `{ name }` e `If-Match` | `messaging.resolution_reason.manage` | 200 `{ id, name, version }`. 404 si está archivado o no existe. 409 `name_taken` si choca con otro vigente. 412 por versión. 428 sin `If-Match` |
| `DELETE /{reasonId}` | `messaging.resolution_reason.manage` | 204, archivado. 404 si ya estaba archivado o no existe |

`PATCH` lleva validador (`name` no vacío, ≤ 60) porque es texto libre (regla del repo). Va en
`ResolutionReasonEndpoints.cs`, mapeado bajo el grupo `messaging` como `MapQuickReplyEndpoints`. Cada handler
revalida tenant, permiso y módulo con `MessagingAuthorization.EnsureAsync`.

No hay `POST`: el catálogo se alimenta desde el resolver (decisión del owner, fuera de alcance §7).

### 3.5 Lectura: historial y resumen

- `MessageEventDto` suma `ResolutionReasonRefDto? Reason = null` con `{ id, name }`. Sólo lo llena un evento
  `Resolved` con `reasonId`.
- `ConversationSummary` suma `ResolutionReasonRefDto? ResolutionReason = null` al final, con valor por
  defecto como `Channel`. Lleva la nota BFF de D-R7 en el XML doc.
- Los nombres se resuelven **incluidos los archivados** (D-R5) con una sola consulta por página:
  el puerto aparte `IResolutionReasonNames.FindAsync(tenantId, ids)` (lo implementa `ResolutionReasonRepository`). La usan `ListMessages` y
  `ConversationSummaryBuilder`, igual que los nombres de miembros.
- Un `reasonId` que no aparece (no debería pasar, porque la FK y el archivado lo impiden) se lee como
  `null`, no como error.

### 3.6 Ingesta: la reapertura por SQL

`InboundIngestion` reabre la conversación con un `UPDATE … SET status = 'Open'` que no pasa por el agregado.
**Ese mismo `UPDATE` tiene que poner `resolution_reason_id = NULL`.** Sin eso, una conversación reabierta por
un mensaje entrante seguiría mostrando el motivo de su resolución anterior. Es el único camino de escritura
de `status` fuera del agregado: `OutboundMessages`, `CampaignDispatchStore` y `ConversationOpener` no
reabren.

### 3.7 Permiso

- `MessagingPermissions.ResolutionReasonManage = "messaging.resolution_reason.manage"`.
- Las tres mitades en `QepServiceCollectionExtensions`, como `QuickReplyManage`:
  1. En los roles de fábrica `admin` **y** `advisor` (el rol vive en código: sin migración de datos).
  2. Su `PermissionDefinition`: «Gestionar motivos de resolución», «Permite renombrar y archivar los motivos
     con los que se resuelven las conversaciones de la bandeja.», módulo `Messaging`, riesgo `low`,
     `RequiredModules: []`.
  3. Su política en `AddAuthorization`. Sin ella el síntoma es 500, no 403.
- `CompositionRootTests` gana su caso: el permiso está en `admin` y `advisor`, no en `billing`, y es de
  núcleo.

### 3.8 Auditoría y tiempo real

- `IMessagingAuditRecorder.RecordResolutionReason(tenantId, actorId, action, reasonId, occurredAt)` con
  `ResolutionReasonResourceType = "resolution_reason"` y las acciones `messaging.resolution_reason.created`
  (sólo cuando el resolver lo crea), `…renamed` y `…archived`. Todas van en la misma transacción que el
  cambio.
- `messaging.conversation.resolved` se sigue registrando como hoy. El motivo queda en el evento del historial,
  no en la auditoría.
- El resolver ya notifica `ConversationChanged`. Renombrar y archivar **no** notifican: no cambian ninguna
  conversación, y el frontend invalida su consulta del catálogo al mutar.

### 3.9 Piezas

| Capa | Qué |
| --- | --- |
| Domain | `ResolutionReason.cs`. `Conversation.Resolve/Reopen`. `ConversationEvent.ReasonId`. Códigos en `MessagingErrorCodes`. |
| Application | `ResolutionReasons.cs` (list, rename, archive, validadores). `IResolutionReasonRepository`. `ResolveConversationCommand` con `ReasonId`/`ReasonName` y su validador nuevo. DTOs en `MessagingDtos.cs`. `ConversationEventJson`. `MessagingSupport` (evento y resumen). Auditoría. |
| Infrastructure | Entidad y columna en `MessagingDbContext`, migración `AddResolutionReasons` (índice por SQL). `ResolutionReasonRepository`. Traducción en `MessagingUnitOfWork`. `InboundIngestion` (§3.6). `ConversationQueries` lee `resolution_reason_id`. |
| Api | `ResolutionReasonEndpoints.cs`. `ResolveAsync` acepta el cuerpo opcional. |
| Bootstrapper | Permiso en roles, definición y política. Registro de los handlers nuevos y del validador de resolver. |

### 3.10 Códigos de error nuevos

| Código | Status | Cuándo |
| --- | --- | --- |
| `messaging.resolution_reason.not_found` | 404 | `reasonId` o ruta de catálogo que no está vigente en el tenant |
| `messaging.resolution_reason.name_taken` | 409 | renombrar a un nombre vigente, o la carrera de D-R11 |
| `messaging.resolution_reason.name_required` | 422 | dominio, nombre vacío al renombrar |
| `messaging.resolution_reason.name_too_long` | 422 | dominio, > 60 |
| `messaging.resolution_reason.limit_reached` | 422 | crear el motivo 101 |

## 4. Frontend

Punto de partida en `qep-frontend` (verifica la rama antes de leer, como pide el `CLAUDE.md` del backend):
`thread-header.tsx` (la acción de resolver), `utils/conversation-event.ts` (el texto del evento `Resolved`) y
`features/customers/components/customer-tag-picker.tsx` (el combobox que sirve de modelo).

- **Diálogo de resolver.** «Resolver» abre un diálogo con un combobox opcional «Motivo (opcional)»:
  - Filtra el `GET /resolution-reasons` en memoria.
  - Si el texto no coincide con ninguno, ofrece «Crear "X"». Elegirlo **no** crea nada: guarda el texto, que
    viaja como `reasonName` al confirmar.
  - Elegir uno existente manda `reasonId`. Vacío no manda cuerpo.
  - Ante 409 `messaging.resolution_reason.name_taken` reintenta una vez el mismo request (D-R11) e invalida
    el catálogo.
- **Historial.** El evento `Resolved` con `event.reason` se dibuja como «Ana resolvió la conversación ·
  Motivo: Venta cerrada».
- **Encabezado.** Con `status === 'Resolved'` y `resolutionReason`: «Resuelta · Venta cerrada».
- **Settings → Mensajería → Motivos de resolución**, visible con `messaging.resolution_reason.manage`: la
  lista con renombrar en línea (con `If-Match`) y archivar con confirmación. No hay botón de crear (§3.4).
- Textos del producto en español colombiano y tuteando.

## 5. Pruebas (TDD, RED antes que GREEN)

**Dominio** (`Modules.Messaging.UnitTests`):

- Normalización y límites del nombre.
- Tope de capacidad.
- `Resolve` guarda el motivo y `Reopen` lo limpia.
- `ConversationEventJson` ida y vuelta con y sin `reasonId`, y un JSON viejo sin el campo.

**Integración** (`Modules.Messaging.IntegrationTests`, archivo nuevo `ResolutionReasonApiTests.cs`):

- El resolver sin cuerpo, con `{}` y con `reasonName` en blanco: idéntico a hoy, `resolutionReason` en
  `null`.
- `reasonName` nuevo: crea el motivo, el resumen lo trae, el evento del hilo lo trae y hay auditoría
  `created`.
- `reasonName` con otro caso o espacios de uno existente: lo reutiliza, sin fila nueva ni auditoría
  `created`.
- `reasonId` válido, archivado (404) y de otro tenant (404).
- Los dos campos a la vez y el nombre largo: 422 `validation.failed` con `errors.reasonName`.
- El motivo 101 nuevo: 422 `limit_reached`. Reutilizar uno existente con 100: pasa.
- Resolver con 412 o `already_resolved` y `reasonName` nuevo: **no** queda el motivo creado.
- Reabrir por API y por un entrante de la ingesta: `resolutionReason` vuelve a `null` y el evento viejo
  conserva su motivo.
- Catálogo:
  - `GET` ordena y omite archivados.
  - `PATCH` cubre renombrar, 409 contra otro vigente, 412 y 428.
  - `DELETE` archiva y deja el nombre visible en el historial. El segundo `DELETE` da 404.
- Renombrar se refleja en el historial pasado (D-R6).
- Permisos: el `PATCH` y el `DELETE` sin `resolution_reason.manage` dan 403 (pedir permisos por
  `X-Permissions`: el stub concede sólo los de tenancy). Con el módulo `messaging` apagado, 403.
- **Barrido de cuerpos crudos** (gotcha del repo): las pruebas que hoy postean a `/resolve` sin cuerpo tienen
  que seguir pasando sin tocarlas. Si alguna falla, el contrato opcional se rompió.

**Arquitectura:** no hay módulo nuevo. Basta con que `MessagingLayerTests` y `CompositionRootTests` pasen,
más el caso nuevo del permiso.

Corre las pruebas acotadas a Messaging y ArchitectureTests, no la suite completa.

## 6. Despliegue

- Una migración aditiva: tabla nueva y columna nullable, sin backfill. Las resoluciones anteriores quedan
  sin motivo, que es lo que eran.
- El permiso entra en roles de fábrica que viven en código: sin migración de datos. Los roles custom no lo
  reciben solos.
- Tras desplegar, reinicia la API y que admins y asesores recarguen su sesión para recibir
  `messaging.resolution_reason.manage`; sin eso, `PATCH`/`DELETE` del catálogo dan 403 (como todo permiso nuevo).
- Backend antes que frontend: el cuerpo opcional es compatible con el frontend actual.

## 7. Fuera de alcance

- Reporte o conteo por motivo, y filtro de la bandeja por motivo. Va en otra spec, cuando haya datos.
- Motivo obligatorio por tenant.
- Motivo al reabrir.
- `POST /resolution-reasons` y alta desde la pantalla de gestión.
- Color o ícono del motivo.
- Desarchivar: escribir el nombre crea uno nuevo (D-R5).
