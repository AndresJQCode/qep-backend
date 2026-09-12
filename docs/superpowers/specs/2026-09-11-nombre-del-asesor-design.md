# Nombre del asesor en la membresía y en el PDF de cotización

**Fecha:** 2026-09-11
**Módulos:** Tenancy, Quotations (backend) — Memberships (frontend, spec aparte)
**Estado:** aprobado, pendiente de plan de implementación

## Problema

El PDF de cotización imprime en la ficha "Asesor" el **correo** de quien la atiende
(`quotation.typ:180` ← `QuotationPdfDocument.AdvisorLabel` ← `QuotationResponse.AdvisorEmail` ←
`QuotationAdvisorLookup.FindEmailsAsync`). Se quiere el nombre completo.

El nombre no existe en ningún lado: `identity.users` tiene `id`, `email`, `status` y fechas;
`Membership` no guarda nada de la persona; invitar pide sólo `Email` y `Roles`; y el login con
Google no lee el claim `name`. Tampoco hay forma de cargarlo para el owner, que entra por
`register-tenant` y no por invitación.

## Decisiones

Todas tomadas con el developer durante el brainstorming del 2026-09-11.

### D1 — Alcance: sólo el PDF

El nombre reemplaza al correo **únicamente en el PDF**. Se captura al invitar, se muestra y edita
en el roster de miembros, y llega al PDF. Listados de cotizaciones y ventas, historial, filtros de
asesor y reportes/Excel siguen mostrando el correo. Llevarlo ahí es un trabajo aparte.

### D2 — El nombre vive en la membresía, no en el usuario

`User` (Identity) es global: la misma persona puede ser miembro de varios tenants. Con el nombre
ahí, el admin del tenant B que lo edita desde su roster cambiaría el nombre que imprimen los PDFs
del tenant A, que es una escritura entre tenants. El nombre en el PDF es un dato de presentación
del tenant, así que va en `Membership` (Tenancy). `AdvisorId` de la cotización ya es un id de
membresía, así que el lookup no suma saltos.

Costo aceptado: una persona en dos tenants tiene el nombre cargado dos veces, y pueden diferir.

### D3 — Obligatorio al invitar; nulo sólo en filas viejas y en el owner

Toda invitación nueva exige el nombre. Las membresías existentes y la del owner quedan con
`null` hasta que alguien lo cargue desde el roster (D4). El PDF cae al correo cuando no hay
nombre (D6).

### D4 — Se edita con un endpoint propio

`PATCH .../memberships/{membershipId}/display-name`. Se descartó un `PATCH` único de membresía
con `{ displayName?, roles? }` porque mezcla dos permisos (`AdvisorshipRolesManage` para roles,
`AdvisorshipManage` para el nombre): el handler tendría que elegir la autorización según los
campos que vinieron, y además habría que tocar un endpoint que ya funciona.

### D5 — El reinvite no-op no toca el nombre

Invitar a alguien con invitación viva o membresía activa sigue sin hacer nada, y el nombre del
cuerpo se ignora. Sólo el reinvite de una invitación vencida (`Membership.Reinvite`) lo
sobreescribe, junto con los roles y la ventana. Para cambiar el nombre de un miembro activo está
D4.

### D6 — El PDF prefiere el nombre y cae al correo

`AdvisorLabel = AdvisorName ?? AdvisorEmail ?? ""`. `quotation.typ` no se toca: ya imprime
`advisorLabel` y ya resuelve el vacío.

### D7 — El caché del PDF no cambia

`QuotationPdfProvider` regenera sólo cuando cambia `Quotation.Version`. Los PDFs ya generados
siguen con el correo, y renombrar un asesor no los reescribe. El nombre aparece en cotizaciones
nuevas o en las que se editan. Un documento ya enviado al cliente es un registro, y no se
reescribe por un dato de presentación.

## Diseño — backend

### Dominio (`Modules.Tenancy.Domain`)

- `Membership.DisplayName` es `string?`.
- Normalización en el agregado: trim y largo entre 1 y 150. Fuera de eso lanza
  `TenantDomainException("tenancy.membership.display_name_invalid")`.
- `Membership.Invite(...)` y `Membership.Reinvite(...)` reciben `displayName`.
  `CreateActive` (owner) no lo recibe y queda `null`.
- `Membership.Rename(string displayName, DateTimeOffset occurredAt)` normaliza, asigna, sube
  `Version` y actualiza `UpdatedAt`. Si el nombre normalizado es igual al actual, no hace nada
  y no sube la versión.

### Persistencia (`Modules.Tenancy.Infrastructure`)

- Columna `display_name character varying(150) null` en `tenancy.memberships`.
- Migración `AddMembershipDisplayName`, generada con el factory de diseño:

  ```powershell
  dotnet ef migrations add AddMembershipDisplayName --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
  ```

  El nombre exacto del contexto se confirma en el plan antes de correrla.

### Invitar

- `MembershipInviteRequest(string Email, string DisplayName, IReadOnlyCollection<string>? Roles)`.
- `InviteMemberCommand` suma `DisplayName`.
- `InviteMemberValidator`: `DisplayName` `NotEmpty` y `MaximumLength(150)`. El 422 sale como
  `validation.failed` con el mapa `errors`, el único que el formulario sabe leer para marcar el
  input. El dominio además da el código propio (`display_name_invalid`) como segunda capa.
- `MembershipDto` y `MembershipResponse` suman `DisplayName`.

### Editar el nombre

`PATCH /api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}/display-name`

- Body `MembershipDisplayNameUpdateRequest(string? DisplayName)`.
- `If-Match` obligatorio con la versión cargada, reusando `TryParseVersion`. Sin él responde
  `428 precondition.if_match_required`; con una versión vieja responde 412. Es el mismo
  comportamiento que `PATCH .../roles`.
- `RequireAuthorization(TenancyPermissions.AdvisorshipManage)`, y el handler revalida tenant y
  permiso antes de tocar el repositorio (403 si el tenant de la ruta no es el de la sesión). Un
  id de membresía de otro tenant bajo la ruta propia no aparece en
  `FindByIdAsync(id, tenantId)` y responde 404, **igual que un id inexistente**, así que no
  confirma que exista en otro lado. Es el comportamiento actual de `/roles`. El permiso ya existe
  con su política, así que no se registra ninguna nueva.
- `UpdateMemberDisplayNameCommand(TenantId, MembershipId, string DisplayName, long ExpectedVersion, string CorrelationId)`
  con su validador (`NotEmpty` y `MaximumLength(150)`).
- La auditoría va atómica, con `IAuditRecorder` en la misma transacción:
  `tenancy.membership.renamed`, recurso `membership`, resultado `success`.
- Responde `200` con `MembershipListItemResponse` y `ETag`, para que el front repinte la fila.
- Se permite renombrar en cualquier estado de la membresía. El nombre es presentación y no
  cambia el acceso.

### Roster

- `MembershipListItemDto` y `MembershipListItemResponse` suman `string? DisplayName`. Los
  handlers que devuelven la fila (`ListMemberships`, `SuspendMember`, `RemoveMember`,
  `ReactivateMember`, `UpdateMemberRoles` y el nuevo) la arman todos con el mismo mapeo,
  `ToListItemDto`, así que el cambio va en un solo lugar.
- La búsqueda de `ListMembershipsHandler` matchea también por nombre, además del correo.

### PDF (`Modules.Quotations` + `Bootstrapper`)

- `IQuotationAdvisorLookup.FindEmailsAsync` pasa a `FindAsync` y devuelve
  `IReadOnlyDictionary<Guid, QuotationAdvisor>` con
  `QuotationAdvisor(string? Email, string? DisplayName)`.
- `QuotationAdvisorLookup` toma `DisplayName` de la membresía que ya trae, sin sumar consultas.
- `ListQuotations`, `ListSales` y `ListQuotationHistory` toman `.Email`, y su contrato hacia el
  front no cambia (D1).
- `QuotationResponse` suma `string? AdvisorName`. Es aditivo en el detalle y no rompe al front.
- `QuotationPdfDocumentMapper` aplica D6.
- El fake de `QuotationsTestDoubles` se adapta a la firma nueva.

## Diseño — frontend (resumen; el detalle va en el spec de `qep-frontend`)

- **Invitar:** campo obligatorio "Nombre completo" en `invite-member-panel.tsx`, con trim y
  máximo 150. El `errors.displayName` del 422 se marca en el input, y el toast nombra a la
  persona.
- **Roster:** la celda "Persona" muestra el nombre y, debajo, el correo en gris. Si falta el
  nombre muestra sólo el correo con un aviso "Sin nombre".
- **Editar nombre:** acción del menú de fila, visible con `AdvisorshipManage`. Abre un diálogo
  con un input precargado y manda `If-Match`. El 412 se maneja igual que en editar roles.
- Fuera de alcance: pantallas de cotizaciones, ventas, historial y filtros (D1).

## Pruebas

Con TDD: RED antes que GREEN, con evidencia literal de cada fase.

**Unitarias**

- `Membership`: `Invite`, `Reinvite` y `Rename` normalizan el nombre y rechazan el vacío y los
  151 caracteres. `Rename` sube la versión, y un `Rename` con el mismo nombre no la sube.
  `CreateActive` deja el nombre en `null`.
- `QuotationPdfDocumentMapperTests`: gana el nombre, sin nombre cae al correo y sin ninguno de
  los dos queda vacío.

**Integración (`Modules.Tenancy.IntegrationTests`)**

- Invitar sin `displayName` da 422 `validation.failed` con `errors.DisplayName`.
- Invitar con nombre lo devuelve en la respuesta `201` y en el roster.
- `PATCH display-name`:
  - 200 con `ETag` nuevo
  - 428 sin `If-Match`
  - 412 con versión vieja
  - 403 con la ruta de un tenant ajeno; 404 con un id de otro tenant bajo la ruta propia
  - 422 con nombre vacío
  - auditoría `tenancy.membership.renamed` grabada
- La búsqueda del roster encuentra por nombre.

**Barrido obligatorio.** El nombre pasa a ser requerido al invitar, así que hay que barrer
**todos** los cuerpos crudos de invitación en las pruebas de integración, no sólo los harness
(gotcha del `CLAUDE.md`):

- `MembershipApiTests`: sus invitaciones pasan por un helper más un cuerpo crudo
- `InvitationApiTests`, `MembershipLifecycleApiTests`, `AuthSessionApiTests` y `RealAuthenticationApiTests`
- `InvitationNotificationTests`, `AuditRecordingTests` y `OrphanUserCleanupTests` (Identity)
- toda otra prueba que postee a `/memberships`

La regresión se mide por nombre de prueba contra un baseline tomado antes de empezar.

## Entrega

Son dos repos con developers distintos, así que hay dos entregas. El backend va primero, porque
el front necesita el contrato. El spec del front va en
`qep-frontend/docs/superpowers/specs/`. Mientras el front no despliegue, invitar desde la SPA
vieja responde 422: **los dos deploys tienen que salir juntos**, o el backend tiene que salir
después de que el front ya mande el campo.

## Fuera de alcance

- Nombre en listados, historial, filtros y reportes (D1).
- Pedir el nombre en `register-tenant`: el owner lo carga desde el roster.
- Usar el nombre en el correo de invitación (`InvitationEmailTemplate`), que sigue diciendo
  "Hola,".
- Regenerar PDFs ya generados (D7).
