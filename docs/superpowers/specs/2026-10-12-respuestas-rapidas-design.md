# Mensajería: respuestas rápidas

**Fecha:** 2026-10-12
**Módulos:** Messaging (agregado, persistencia, endpoints, permiso nuevo). Authorization (política del
permiso). Bootstrapper (adaptador del nombre del tenant). **Frontend:** `qep-frontend`,
`features/messaging/` y `routes/_authenticated/settings/`.
**Estado:** alcance, variables, backend (sección 1) y frontend (sección 2) aprobados por el owner en
conversación el 2026-10-12.
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` («el spec base»). Lo que este documento no
toca sigue como está ahí.
**Ramas:** `feature/respuestas-rapidas` desde `develop` `5ad9fa2` en `qep-backend`; la homónima desde
`develop` en `qep-frontend`.

## 1. Contexto y alcance

El asesor escribe a mano, conversación tras conversación, los mismos textos: saludo, horarios, datos de
pago, tiempos de entrega. Una **respuesta rápida** es un texto guardado con un atajo (`/horarios`) que el
asesor inserta en el compositor de la caja, lo revisa y lo envía.

**Dentro:**

- Respuestas rápidas **globales del tenant**: todas las ve todo asesor con acceso a la caja.
- Gestión (crear, editar, borrar) en Settings, con permiso propio.
- Tres variables fijas que se resuelven al insertar: `{nombre}`, `{asesor}`, `{empresa}`.
- Inserción desde el compositor con `/` y con un botón.

**Fuera** (§7): respuestas personales por asesor, adjuntos, variables adicionales, uso fuera de la
ventana de 24 h, métricas de uso.

## 2. Decisiones

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-Q1 | Globales del tenant; **sin** `owner_member_id`. | Decisión del owner. Una sola voz de marca, un solo lugar para corregir un dato (precio, cuenta bancaria), y nada se pierde cuando un asesor se va. Agregar personales después es una columna nullable y un filtro (YAGNI). |
| D-Q2 | Contenido: texto con variables. Sin adjuntos. | Decisión del owner. El adjunto se puede agregar después sin rediseño. |
| D-Q3 | Variables cerradas: `{nombre}`, `{asesor}`, `{empresa}`. Otra entre llaves es error de dominio. | Una variable que nadie resuelve llegaría literal al cliente. |
| D-Q4 | Se resuelven **en el frontend, al insertar**; el texto entra al compositor editable y no se envía solo. El envío es el `SendMessage` de texto que ya existe. | El asesor ve exactamente lo que va a mandar y corrige un `{nombre}` vacío. El camino de envío no cambia. |
| D-Q5 | `GET /quick-replies` devuelve también `variables.asesor` y `variables.empresa`, resueltos por el servidor para quien llama. | BFF: la SPA no conoce su `memberId` ni su nombre de asesor (`/auth/me` sólo da `userId`; mismo motivo que `AssignedToDto.IsMe`). `{nombre}` depende de la conversación y la pantalla ya lo tiene. |
| D-Q6 | Leer usa `messaging.conversation.read`; gestionar usa el nuevo `messaging.quick_reply.manage`, de núcleo y de fábrica sólo en `admin`. | Lo que el asesor manda en nombre de la empresa lo decide quien administra, como `CampaignManage`. |
| D-Q7 | Lista completa sin paginar, ordenada por `shortcut`. | El compositor filtra en memoria mientras se escribe; un tenant tiene decenas, no miles. |
| D-Q8 | Sin popover ni botón con la ventana de 24 h cerrada. | Ahí el compositor ya obliga a plantilla (spec base); ofrecer texto libre sería ofrecer algo que no se puede enviar. |

## 3. Backend

### 3.1 Modelo

Tabla `messaging.quick_replies`:

| Columna | Tipo | Regla |
| --- | --- | --- |
| `id` | `uuid` | PK |
| `tenant_id` | `uuid` | todo método del repositorio lo recibe |
| `shortcut` | `varchar(32)` | `^[a-z0-9-]{2,32}$`, se guarda en minúsculas y sin `/` |
| `body` | `varchar(4096)` | de 1 a 4096 caracteres después de `Trim` (4096 es el tope de texto de WhatsApp) |
| `created_at`, `updated_at` | `timestamptz` | |
| `version` | `bigint` | concurrencia optimista, como el resto del módulo |

Índice único `IX_quick_replies_tenant_shortcut` sobre `(tenant_id, shortcut)`.

### 3.2 Dominio

Agregado `QuickReply` con `Create(tenantId, shortcut, body, now)` y `Update(shortcut, body, now)`. Valida:

- `shortcut` con el patrón de §3.1 → `messaging.quick_reply.invalid_shortcut`.
- `body` vacío o mayor a 4096 → `messaging.quick_reply.invalid_body`.
- Todo `{...}` del cuerpo debe ser una de las tres variables de D-Q3 → `messaging.quick_reply.unknown_variable`.
  Las llaves sueltas sin cierre (`{`, `}`) son texto y se permiten.

Los tres son `DomainException` → 422 por el mapeo central.

### 3.3 Aplicación

Casos de uso: `ListQuickReplies`, `CreateQuickReply`, `UpdateQuickReply`, `DeleteQuickReply`.

- `Create` y `Update` llevan validador de FluentValidation (texto libre) con las mismas reglas de formato,
  para que el 422 `validation.failed` traiga el mapa `errors` por campo (`shortcut`, `body`).
- Cada handler revalida tenant y permiso antes del repositorio y llama a
  `TenantModuleGuard.EnsureEnabledAsync` (spec base, decisión 5). Otro tenant → **403, nunca 404**.
- `Update` y `Delete` reciben `version`; si no coincide → 412.
- `Update` y `Delete` de un id inexistente en el tenant → 404 `messaging.quick_reply.not_found`.
- Crear, editar y borrar se auditan por `IMessagingAuditRecorder`, en la misma transacción.

**Variables de quien llama (D-Q5):**

- `asesor`: el `DisplayName` de la membresía de quien llama, por `CallerMembership` +
  `IMessagingMemberNames`. Sin nombre → `""`.
- `empresa`: `Tenant.DisplayName`, por un puerto nuevo `IMessagingTenantNames`
  (`Task<string?> FindAsync(Guid tenantId, CancellationToken)`), con adaptador en Bootstrapper sobre Tenancy.
  Messaging no referencia `Modules.Tenancy.Application` para esto. Sin nombre → `""`.

### 3.4 Infrastructure

- Configuración EF y migración `AddQuickReplies` (con el factory de diseño, ver `CLAUDE.md`).
- `MessagingUnitOfWork` traduce la violación de `IX_quick_replies_tenant_shortcut` a
  `messaging.quick_reply.shortcut_taken` como excepción de dominio → **422**, igual que
  `catalog.product.code_taken` en `CatalogUnitOfWork`, discriminando por **nombre de índice**, no sólo por `23505`.

### 3.5 Permiso

`MessagingPermissions.QuickReplyManage = "messaging.quick_reply.manage"`: la constante **y** su política en
`AddAuthorization` (`QepServiceCollectionExtensions`). De fábrica sólo en `admin`.

### 3.6 Contrato HTTP

En el grupo de `MessagingEndpoints` (`/api/tenants/{tenantId:guid}/messaging`):

| Método | Ruta | Permiso | Respuesta |
| --- | --- | --- | --- |
| `GET` | `/quick-replies` | `messaging.conversation.read` | `200 QuickReplyListDto` |
| `POST` | `/quick-replies` | `messaging.quick_reply.manage` | `201 QuickReplyDto` |
| `PUT` | `/quick-replies/{quickReplyId:guid}` | `messaging.quick_reply.manage` | `200 QuickReplyDto` |
| `DELETE` | `/quick-replies/{quickReplyId:guid}?version=` | `messaging.quick_reply.manage` | `204` |

```csharp
public sealed record QuickReplyDto(Guid Id, string Shortcut, string Body, DateTimeOffset UpdatedAt, long Version);

/// BFF (D-Q5): la SPA no conoce su nombre de asesor ni tiene garantizado el del tenant en la caja.
public sealed record QuickReplyVariablesDto(string Asesor, string Empresa);

public sealed record QuickReplyListDto(IReadOnlyList<QuickReplyDto> Items, QuickReplyVariablesDto Variables);

public sealed record SaveQuickReplyRequest(string Shortcut, string Body, long? Version);
```

`shortcut` viaja sin `/`. El `POST` ignora `version`; el `PUT` la exige.

### 3.7 Pruebas

- **Unitarias del agregado:** patrón del atajo (válidos, mayúsculas normalizadas, `/` inicial rechazado),
  cuerpo vacío y de 4097, variable desconocida, llaves sueltas permitidas.
- **Integración (`MessagingApiHarness`):** CRUD completo; `GET` con `variables` resueltas; 403 sin
  `messaging.quick_reply.manage` al gestionar (pedir permisos por `X-Permissions`); 403 desde otro tenant;
  `shortcut_taken`; 412 por versión; 422 `validation.failed` con `errors.shortcut`; 403
  `tenancy.module_not_enabled` sin el módulo; auditoría escrita.

## 4. Frontend

### 4.1 Gestión

`features/messaging/` (única feature consumidora, `SDD-ADR-07`); la ruta `/settings/quick-replies` sólo
cablea.

- Tarjeta en `/settings` visible sólo con `messaging.quick_reply.manage`, como la de integraciones.
- Tabla: atajo (`/horarios`), vista previa del cuerpo en una línea, acciones editar y borrar (con
  confirmación).
- Diálogo de formulario: atajo con prefijo `/` visual, textarea con chips que insertan `{nombre}`,
  `{asesor}` y `{empresa}` en el cursor, contador de 4096.
- Errores: 422 con `errors` marca el campo; `shortcut_taken` marca el atajo; 412 avisa «Otra persona
  editó esta respuesta» y recarga la lista.
- Estado vacío: «Aún no tienes respuestas rápidas» con el botón para crear la primera.

### 4.2 Uso en `message-composer.tsx`

- Con el texto empezando por `/` se abre un popover que filtra por atajo y cuerpo. ↑/↓ navegan, Enter
  elige, Esc cierra.
- Elegir reemplaza el `/término` por el cuerpo **resuelto**, con el cursor al final. No envía.
- Botón ⚡ junto al adjunto que abre la misma lista, para quien no conoce los atajos.
- Ventana de 24 h cerrada: ni popover ni botón (D-Q8).
- Lista vacía: «Sin respuestas rápidas»; con el permiso de gestión, enlace a `/settings/quick-replies`.

### 4.3 Resolución

`features/messaging/utils/resolve-quick-reply.ts`:
`resolveQuickReply(body, { nombre, asesor, empresa }): string`.

- `nombre` = `customer.name` si hay ficha; si no, `contact.profileName`; si no, `""`.
- Una variable vacía se reemplaza por `""` y se colapsan los espacios dobles que deja; se recortan los
  extremos.

### 4.4 Pruebas (Vitest)

- `resolveQuickReply`: las tres variables, variable vacía, espacios dobles, variable repetida, texto sin
  variables.
- Página de gestión: listar, crear, editar, borrar, 422 por campo, `shortcut_taken`, 412.
- Compositor: `/` abre y filtra, teclado, inserción resuelta, sin popover con ventana cerrada.
- Ruta: con y sin permiso de gestión.

## 5. Entregas

1. **Backend** (`qep-backend`, `feature/respuestas-rapidas`): §3 completo.
2. **Frontend** (`qep-frontend`, `feature/respuestas-rapidas`): §4 completo.

Dos slices, uno por repo. El frontend puede desarrollarse en paralelo contra el contrato de §3.6.

## 6. Despliegue

Backend primero (trae la migración `AddQuickReplies`), después frontend. El permiso nuevo exige
reiniciar la API y recargar la sesión para que `admin` lo vea.

## 7. Fuera de alcance

- Respuestas personales por asesor (D-Q1).
- Adjuntos (D-Q2).
- Variables distintas de las tres de D-Q3.
- Respuestas rápidas con la ventana de 24 h cerrada: para eso están las plantillas.
- Orden manual, categorías, métricas de uso.
