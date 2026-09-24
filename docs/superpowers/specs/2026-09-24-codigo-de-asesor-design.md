# Código de asesor en la membresía

**Fecha:** 2026-09-24
**Módulos:** Tenancy (backend) — Memberships (frontend)
**Estado:** aprobado en brainstorming, pendiente de revisión del spec escrito

## Problema

El tenant necesita asociar a cada asesor el código con el que lo identifica un sistema externo
(ERP, contabilidad), para que los registros de ese sistema y los de QEP casen por código. Hoy la
membresía guarda el nombre (`display_name`, spec 2026-09-11) pero no un código.

No todos los miembros tienen código: un facturador o un administrador puede no tenerlo.

## Decisiones

Todas tomadas con el developer durante el brainstorming del 2026-09-24.

### D1 — Opcional

El código no es obligatorio en ningún punto: ni al invitar, ni al editar, ni para cotizar.

### D2 — Entero positivo

Sólo números enteros, `>= 1`. Se guarda como `integer`, así que los ceros a la izquierda no se
conservan: `0012` y `12` son el mismo código. Si el sistema externo los distinguiera, el tipo
tendría que pasar a texto de sólo dígitos — no es el caso según lo acordado.

### D3 — Único por tenant cuando existe

Dos membresías del mismo tenant no pueden tener el mismo código. Varias sin código sí: la
unicidad es un índice único parcial `(tenant_id, advisor_code) WHERE advisor_code IS NOT NULL`.
El mismo código en tenants distintos es válido: cada tenant tiene su propio sistema externo.

### D4 — La membresía quitada conserva su código

Una membresía en `Removed` mantiene el código y lo sigue bloqueando. En el sistema externo ese
código queda atado al historial de esa persona; reasignarlo mezclaría los registros de dos
personas. El índice de D3 no filtra por estado.

### D5 — Columna en `memberships`, no tabla aparte

"Asesor" no es un tipo de miembro en el modelo: `QuotationAdvisorResolver` toma como asesor de
una cotización a la membresía activa de quien la crea, sea cual sea su rol, y los roles los
define cada tenant. El código es un atributo opcional de la persona **dentro del tenant**, que
es exactamente `Membership` — por eso tampoco va en `identity.users`. `NULL` significa "no tiene
código en el sistema externo".

Una tabla 1:0..1 (`advisor_profiles`) no impediría que un facturador tenga código —esa regla no
existe en ningún lado— y costaría un join, una segunda concurrencia y la pregunta de qué
`Version` protege el PUT.

**Disparador para migrar a tabla aparte:** el día que llegue un segundo atributo exclusivo del
asesor (zona, comisión, meta), varios códigos por miembro, historial de códigos, o códigos que
existan sin membresía.

### D6 — Un solo PUT de perfil para nombre y código

`PUT /api/v1/tenants/{tenantId}/memberships/{id}/profile` con `{ displayName, advisorCode }`
reemplaza a `PUT .../display-name`.

- El diálogo edita los dos campos juntos. Dos PUT desde el mismo diálogo chocan: cada uno sube
  `Version` y el segundo `If-Match` viaja con la versión vieja → 412, la pantalla pisándose sola.
- `display-name` se separó de `roles` porque son permisos distintos (spec 2026-09-11, D4). Nombre
  y código comparten `AdvisorshipManage`, así que ese motivo no aplica acá.

### D7 — Retiro de `display-name` en dos pasos

1. El backend publica `PUT .../profile` y **conserva** `PUT .../display-name`.
2. El frontend migra a `profile` y se publica.
3. Un slice posterior del backend borra `display-name` (endpoint, comando, validador, handler).

Así ningún despliegue deja la SPA apuntando a un endpoint que no existe.

### D8 — La búsqueda del roster también encuentra por código

`?search=12` encuentra al asesor con código `12`, además del correo y el nombre. Coincidencia
exacta sobre el texto del código, no por contenido: `1` no debe traer a `12`, `21` y `100`.

### D9 — El código viaja en el Excel de pedidos

El Excel de pedidos (`OrdersExportProcessor`) es lo que consume el ERP contable del tenant, y es
por donde el código llega al sistema externo.

- Columna nueva **`Cod. Asesor`**, **al final** (después de `Email`). El ERP actual lee por
  encabezado, no por índice, así que la posición no importa; al final no mueve nada de lo que
  ya importa.
- Celda numérica (`ExportCell.OfNumber`); **vacía** si la membresía asesora no tiene código.
- Se repite en cada línea del pedido, como el resto de los campos del pedido.
- Se resuelve `Quotation.AdvisorId` → membresía → `AdvisorCode` con **el código de hoy**, no uno
  congelado al vender: mismo criterio que U.Medida. D4 sostiene esto — el asesor quitado conserva
  su código, así que sus pedidos viejos siguen exportando bien. Si al asesor le cambian el
  código, sus pedidos viejos salen con el nuevo; si el ERP necesitara el histórico, habría que
  congelar el código en el pedido, y eso es otro diseño.
- `QuotationAdvisor` suma `AdvisorCode`; `OrdersExportProcessor` pasa a usar
  `IQuotationAdvisorLookup`, una consulta por lote.

### D10 — La homologación de columnas por tenant es otro spec

Que cada tenant defina el encabezado y el orden de cada columna exportada —opcional, con
nombres por defecto— se pidió en el mismo brainstorming y se separó a un spec propio: es un
modelo nuevo (llaves estables por columna, persistencia, API y pantalla), no un campo. Este spec
no lo espera: `Cod. Asesor` es el nombre por defecto que esa homologación podrá renombrar.

## Backend

### Dominio — `Membership`

- `int? AdvisorCode { get; private set; }`.
- `NormalizeAdvisorCode(int?)`: `null` pasa; `< 1` lanza
  `TenantDomainException("tenancy.membership.advisor_code_invalid", ...)`.
- `Invite(...)` y `Reinvite(...)` reciben `int? advisorCode`.
- `Rename(displayName, now)` se reemplaza por `UpdateProfile(displayName, advisorCode, now) : bool`.
  No-op si ninguno de los dos cambia: sin él, guardar sin tocar nada consume una versión y
  provoca un 412 falso en otra pantalla abierta.

La unicidad (D3) no vive en el agregado: una membresía no ve a las demás.

### Persistencia — Infrastructure

- Migración `AddMembershipAdvisorCode`: columna `advisor_code integer NULL` en `tenancy.memberships`
  e índice único parcial `IX_memberships_tenant_id_advisor_code`.
- `TenancyUnitOfWork.SaveChangesAsync` traduce el `23505` **de ese índice, por nombre** a
  `TenantDomainException("tenancy.membership.advisor_code_taken", ...)` → 422. Mismo patrón que
  `IX_tenants_slug`; discriminar por nombre porque `memberships` ya tiene otros índices únicos.
- `IMembershipRepository.IsAdvisorCodeTakenAsync(tenantId, code, exceptMembershipId, ct)`: chequeo
  previo en el handler para responder claro sin depender de la base. El índice sigue siendo la
  autoridad ante una carrera.

### Application y API

- `InviteMemberCommand` y su request: `int? AdvisorCode`. `InviteMemberValidator`:
  `RuleFor(AdvisorCode).GreaterThan(0).When(not null)` → `errors.AdvisorCode`.
  En la re-invitación de una membresía vencida o quitada, un código en el cuerpo reemplaza al
  que tenía; **un cuerpo sin código conserva el anterior** (decisión del developer, 2026-09-24):
  olvidar un campo opcional no debe liberar un código que el ERP tiene atado a esa persona (D4).
  Para borrarlo está `PUT .../profile`. En una invitación viva o una membresía activa el código
  se ignora (no-op, como hoy).
- `UpdateMemberProfileCommand(TenantId, MembershipId, DisplayName, AdvisorCode, ExpectedVersion,
  CorrelationId)` con validador (nombre como hoy, código como arriba, `ExpectedVersion > 0`) y
  handler calcado de `UpdateMemberDisplayNameHandler`: permiso `AdvisorshipManage`, revalida
  tenant, 412 por versión, audita `tenancy.membership.profile_updated` sólo si cambió, devuelve
  `MembershipListItemDto`.
- Endpoint `PUT .../profile` en `MembershipEndpoints`, con `If-Match` y ETag como `display-name`.
- `MembershipListItemDto` y `MembershipDto` agregan `int? AdvisorCode`.
- `ListMembershipsHandler.ApplySearch` agrega la coincidencia exacta por código (D8).

### Quotations — Excel de pedidos (D9)

- `QuotationAdvisor(Email, DisplayName, AdvisorCode)`; `QuotationAdvisorLookup` (Bootstrapper)
  lo llena desde la membresía que ya trae.
- `OrdersExportProcessor`: inyecta `IQuotationAdvisorLookup`, lo resuelve en
  `LoadBatchContextAsync` para los `AdvisorId` distintos del lote, y agrega la columna
  `Cod. Asesor` al final de `Columns` y de cada fila.

### Pruebas (TDD, RED antes que GREEN)

- Unitarias de `Membership`: código nulo, `0` y negativo rechazados, no-op de `UpdateProfile`,
  cambio de sólo el código sube la versión.
- Unitarias de `ListMembershipsHandler`: búsqueda exacta por código.
- Integración de `PUT .../profile`: 200 con DTO completo, 412 con versión vieja, 422
  `validation.failed` con `errors.AdvisorCode` para `0`, 422 `advisor_code_taken` con código
  repetido en el mismo tenant, 200 con el mismo código en otro tenant, 200 al borrar (`null`),
  403 con tenant ajeno, auditoría registrada.
- Integración de invitación: con código, sin código, con código repetido.
- Índice parcial: dos membresías sin código en el mismo tenant no chocan; una `Removed` sigue
  bloqueando su código.
- Unitarias de `OrdersExportProcessor`: `Cod. Asesor` es la última columna; la celda trae el
  código del asesor del pedido en cada línea; vacía si no tiene código o la membresía no resuelve.
- Barrer las pruebas de integración que arman cuerpos a mano para `display-name` y `invite`
  (gotcha del `CLAUDE.md`): el campo es opcional, no deberían romper, pero se verifica.

## Frontend (`qep-frontend`, `features/memberships/`)

- `services/memberships.api.ts`: `MembershipListItem.advisorCode: number | null`; `inviteMember`
  envía `advisorCode`; `updateMemberProfile(tenantId, id, { displayName, advisorCode, version })`
  reemplaza a `updateMemberDisplayName`; `MembershipField` suma `'advisorCode'`; el mapeo de 422
  cubre `errors.AdvisorCode` y el código de dominio `tenancy.membership.advisor_code_taken`.
- `types/advisor-code.schema.ts`: zod compartido por invitar y editar. Vacío → `null`; si trae
  valor, sólo dígitos y entero `>= 1`.
- `EditDisplayNameDialog` → `EditMemberDialog` ("Editar miembro"): "Nombre completo" y "Código de
  asesor (opcional)". El `snapshot` congela nombre, código y versión al abrir; si nada cambió,
  cierra sin enviar. Vaciar el código envía `null`.
- `InviteMemberForm`: campo opcional "Código de asesor".
- Fila del roster: el código junto al nombre (`Cód. 12`); nada si no tiene.
- Buscador: placeholder "Buscar por nombre, correo o código".
- Pruebas Vitest + Testing Library: esquema (vacío, `0`, `abc`, `12`), no-op del diálogo, vaciar
  el código, 422 marcado en el input correcto, invitación con código, `If-Match` entrecomillado.

Textos del producto en español colombiano, tuteando: "Ese código ya lo tiene otro asesor.",
"El código debe ser un número entero mayor que cero."

## Entrega

Un slice por repo, cada uno en su ledger:

1. Backend: dominio, migración, invitación, `PUT .../profile`, DTOs, búsqueda y la columna
   `Cod. Asesor` del Excel de pedidos. Conserva `display-name`.
2. Frontend: todo lo de la sección anterior. Se publica después del backend.
3. Backend: borrar `PUT .../display-name` y su comando, validador y handler.

## Fuera de alcance

- Homologación de columnas de exportación por tenant (D10), en su propio spec.
- El código en el Excel de cotizaciones, el PDF, los listados de cotizaciones o pedidos, o los
  reportes. Sólo el Excel de pedidos lo lleva (D9).
- Restringir el código a miembros con un rol o permiso de venta (D5).
