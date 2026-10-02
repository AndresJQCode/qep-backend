# Purga de la membresía huérfana

**Fecha:** 2026-10-02
**Módulos:** Identity, Tenancy, BuildingBlocks (backend)
**Estado:** aprobado por el owner; enmienda D4 del spec
[`2026-09-24-codigo-de-asesor-design.md`](2026-09-24-codigo-de-asesor-design.md)

## Problema

Cuando quitas a un miembro, `Membership.Remove` no borra la fila: la pasa a `Removed` y emite
`tenancy.membership-removed.v1`. `OrphanUserCleanupWorker` consume ese evento y, si ninguna
`IUserReferenceProbe` retiene al usuario (Tenancy, Quotations, Storage), lo borra físicamente
bajo el advisory lock de `UserLifecycleLockKey`.

Las membresías `Removed` y `Expired` de ese usuario se quedaban en `tenancy.memberships`,
apuntando a un usuario que ya no existe. Y como la unicidad del código de asesor cuenta a las
quitadas (D4 del spec 2026-09-24, en `IsAdvisorCodeTakenAsync` y en el índice parcial
`IX_memberships_tenant_id_advisor_code`), el código de esa persona quedaba bloqueado para
siempre:

- otra persona no podía tomarlo, aunque la dueña nunca hubiera vendido nada;
- si la misma persona volvía a ser invitada, Identity le creaba un usuario nuevo y Tenancy una
  membresía nueva, que no podía recuperar su propio código: la fila vieja lo seguía ocupando.

## Decisión

**Cuando el usuario se borra por no tener historia, sus membresías `Removed` y `Expired` se
borran físicamente con él**, y su código de asesor queda libre (aprobado por el owner).

D4 sigue en pie para quien tiene historia. Si una cotización, un pedido o un archivo retiene al
usuario, el worker no lo borra, no se purga nada y la membresía quitada sigue bloqueando el
código. Eso no cambia: la purga sólo corre en la rama donde el usuario ya se iba a borrar.

## Cómo

### Contrato nuevo en BuildingBlocks: `IUserReferencePurger`

Hermano de `IUserReferenceProbe`: la sonda dice si el usuario se retiene; el purgador borra lo
que, sin retenerlo, todavía lo nombra.

```csharp
public interface IUserReferencePurger
{
    string Source { get; }
    Task PurgeAsync(Guid userId, CancellationToken cancellationToken);
}
```

Vive en BuildingBlocks por la misma razón que la sonda: Identity resuelve
`IEnumerable<IUserReferencePurger>` sin referenciar a ningún módulo de negocio, y cada módulo
registra el suyo sin referenciar a Identity.

### Regla de dominio: `Membership.EnsurePurgeable()`

Lanza `TenantDomainException("tenancy.membership.not_purgeable")` si la membresía todavía da o
promete acceso. Esa frontera está escrita una sola vez, en `Membership.GrantsOrPromisesAccess`
(`Invited`, `Active` o `Suspended`), y la usan las dos puntas: `MembershipUserReferenceProbe`
retiene al usuario si alguna membresía la cumple, y `EnsurePurgeable` rechaza las que la
cumplen. Con dos reglas escritas aparte —una afirmando los estados vivos, la otra negando los
terminales— un estado nuevo podía quedar suelto para la sonda y rechazado por la purga, y el
worker fallaría con el mismo mensaje en cada tick, para siempre. Una prueba recorre todos los
valores de `MembershipState` y exige que `EnsurePurgeable` lance exactamente cuando la propiedad
es verdadera.

Vive en el agregado para que el purgador no dependa de que la sonda ya la miró.

### Repositorio: `IMembershipRepository.Remove`

Borrado físico, implementado en `MembershipRepository` y en los dobles de prueba. Quitar a un
miembro sigue siendo `Membership.Remove`, que cambia el estado y conserva la fila.

### `MembershipUserReferencePurger` (Tenancy.Application)

1. `ListByUserAsync(userId)`; si no hay nada, vuelve sin commitear.
2. `EnsurePurgeable()` sobre **todas** antes de borrar ninguna: si aparece una viva, corta sin
   dejar nada a medias.
3. Por cada una: `Remove` y una entrada de auditoría `tenancy.membership.purged`, recurso
   `membership`, id de la membresía, tenant de la membresía, actor de sistema
   (`AuditActorType.System`) con el id del usuario afectado — el mismo criterio que
   `identity.user.deleted` en el worker.
4. Un solo `SaveChangesAsync`. No emite eventos de outbox: nadie consume un borrado de
   membresía, y quitarla ya emitió el suyo.

Se registra en `TenancyInfrastructureExtensions`, junto a la sonda y con el mismo ciclo de vida.

### `OrphanUserCleanupWorker`

Resuelve los purgadores junto con las sondas y, en la rama donde nada retiene al usuario, los
llama **antes** de borrar sesiones y usuario.

Cada mensaje del lote corre en su propio scope de DI, con sus propios `DbContext`. Antes había
un scope por lote, y el catch sólo limpiaba el `ChangeTracker` de Identity: si la purga de A
fallaba al guardar, sus borrados y su auditoría quedaban rastreados en el `TenancyDbContext`
compartido, y el `SaveChanges` de la purga de B los commiteaba —borraba la membresía de A sin
que su usuario se borrara—. El worker no puede limpiar el contexto de Tenancy porque no lo
conoce; con un scope por mensaje, lo rastreado muere con él.

## Por qué un contrato síncrono y no un evento `identity.user-deleted.v1`

- Identity no tiene outbox propio donde escribir: lee el Outbox de plataforma y marca su inbox,
  pero no publica. Agregarle uno es otro diseño.
- Un evento asíncrono deja una ventana en la que el usuario ya no existe y la membresía sigue
  bloqueando el código. Es justo la ventana en la que alguien intenta re-invitar a la persona
  después de quitarla, y recibiría un `advisor_code_taken` que no se entiende.

## Orden e idempotencia

Son dos `DbContext` y dos commits —primero Tenancy, después Identity—, así que la operación no
es atómica. El orden es lo que la hace recuperable:

- **Falla después de la purga y antes del commit de Identity:** el inbox no se marca, así que el
  mensaje vuelve en el tick siguiente. El usuario sigue existiendo, las sondas siguen diciendo
  que no, el purgador no encuentra nada (no-op) y el usuario se borra.
- **Al revés** (usuario primero, membresías después), una falla entre los dos commits dejaría
  filas apuntando a un usuario inexistente, y el mensaje ya marcado: nada las volvería a mirar.

Por eso el purgador tiene que ser idempotente: sin nada que purgar, no hace nada ni commitea.

**Costo aceptado.** Si la purga commitea y después falla el commit de Identity, el usuario queda
vivo sin sus filas `Removed`/`Expired`, y su código de asesor ya está libre. Se acepta porque ese
usuario no tiene historia y no puede generarla: no tiene acceso, y la única forma de volver es
una invitación, que se serializa con el mismo lock de ciclo de vida. Esa invitación encuentra al
usuario, no encuentra membresía y crea una nueva. El tick siguiente borra al usuario si para
entonces nada lo retiene.

## La trampa: no volver a tomar el lock

El purgador corre mientras el worker sostiene `pg_advisory_xact_lock(hashtext(UserLifecycleLockKey.For(email)))`
en la transacción de Identity. Tenancy usa **otra conexión**, así que si el purgador pidiera el
mismo lock —por ejemplo, guardando con `BeginUserLifecycleScopeAsync` como hace
`InviteMemberHandler`— esperaría a una transacción que no termina hasta que él vuelva. Postgres
no lo detecta como deadlock: una de las dos esperas está en el cliente, no en la base. El
purgador guarda con `SaveChangesAsync` y nada más.

El lock sigue haciendo su trabajo: una invitación concurrente espera a que Identity commitee, y
para entonces el usuario y sus membresías ya no existen, así que crea los dos de nuevo y puede
tomar el código que acaba de quedar libre.

## Enmienda a D4

D4 decía: "Una membresía en `Removed` mantiene el código y lo sigue bloqueando". Ahora:

> Una membresía en `Removed` mantiene el código y lo sigue bloqueando **mientras exista**. Si su
> usuario se borra por no tener historia, la membresía se purga y el código queda libre.

La razón de D4 —no mezclar en el sistema externo los registros de dos personas— supone que el
código ya quedó atado a un historial. Sin cotizaciones, pedidos ni archivos no hay historial que
mezclar.

## Pruebas (TDD, RED antes que GREEN)

- Unitarias de `Membership`: `EnsurePurgeable` lanza para `Invited`, `Active` y `Suspended`, y
  pasa para `Removed` y `Expired`; y, recorriendo todo `MembershipState`, lanza exactamente
  cuando `GrantsOrPromisesAccess` es verdadera.
- Unitarias de `MembershipUserReferencePurger`: borra las quitadas y vencidas del usuario, una
  auditoría por membresía y un solo commit; sin membresías no commitea; con una viva lanza y no
  borra nada.
- Integración (`OrphanUserCleanupTests`): quitar a un miembro sin historia borra usuario y
  membresía, y otro correo puede tomar el código; con historia se conservan los dos y el código
  sigue tomado; re-invitar el mismo correo después de la purga crea una membresía nueva que
  recupera su código; un mensaje que falla con borrados rastreados en Tenancy no se los pasa al
  siguiente del lote (un purgador de prueba que falla sólo para A).
- Pruebas existentes que quitan a alguien sin historia y después miran la fila quitada (D4,
  re-invitación sobre la misma fila, quitar dos veces) corrían contra el worker: ahora retienen
  al usuario con una invitación viva en otro tenant, que es el caso que de verdad ejercitan.

## Pendientes

- El worker reintenta un mensaje que falla cada 3 s, sin tope ni backoff. Es el patrón que ya
  tenía; la purga le suma una fuente de falla más.
- No hay línea de log con la cantidad de membresías purgadas: la auditoría
  (`tenancy.membership.purged`, una entrada por membresía) es el rastro.

## Fuera de alcance

- `catalog.product_price_changes.changed_by` y `quotations.export_jobs.requested_by` guardan ids
  de usuario y no tienen sonda: borrar un usuario ya los deja colgando hoy, con o sin esta purga.
  Decidir si retienen al usuario, se purgan o pueden colgar es otro slice.
- `tenants.owner_membership_id` no se toca: la membresía del owner nunca llega a `Removed`
  (`EnsureNotOwner`).
- La auditoría (`audit.entries.resource_id`) queda apuntando a membresías y usuarios borrados a
  propósito: es append-only y guarda snapshot.
