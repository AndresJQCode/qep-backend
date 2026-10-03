# Purga de la membresía huérfana

**Fecha:** 2026-10-02
**Módulos:** Identity, Tenancy, BuildingBlocks, Catalog, Quotations (backend)
**Estado:** aprobado por el owner; enmienda D4 del spec
[`2026-09-24-codigo-de-asesor-design.md`](2026-09-24-codigo-de-asesor-design.md)

## Problema

Cuando quitas a un miembro, `Membership.Remove` no borra la fila: la pasa a `Removed` y emite
`tenancy.membership-removed.v1`. `OrphanUserCleanupWorker` consume ese evento y, si ninguna
`IUserReferenceProbe` retiene al usuario (Tenancy, Quotations, Storage, Catalog), lo borra físicamente
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

D4 sigue en pie para quien tiene historia. Si una cotización, un pedido, un archivo o un cambio
de precio retiene al usuario, el worker no lo borra, no se purga nada y la membresía quitada sigue bloqueando el
código. Eso no cambia: la purga sólo corre en la rama donde el usuario ya se iba a borrar.

## Cómo

### Contrato nuevo en BuildingBlocks: `IUserReferencePurger`

Hermano de `IUserReferenceProbe`: la sonda dice si el usuario se retiene; el purgador borra lo
que, sin retenerlo, todavía lo nombra.

```csharp
public interface IUserReferencePurger
{
    string Source { get; }
    Task<int> PurgeAsync(Guid userId, CancellationToken cancellationToken);
}
```

`PurgeAsync` devuelve cuántas filas borró (`0` sin nada que purgar). El worker lo escribe en su
log; el purgador no loguea (ver «Reintentos y log de la purga»).

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

1. `ListByUserAsync(userId)`; si no hay nada, devuelve `0` sin commitear.
2. `EnsurePurgeable()` sobre **todas** antes de borrar ninguna: si aparece una viva, corta sin
   dejar nada a medias.
3. Por cada una: `Remove` y una entrada de auditoría `tenancy.membership.purged`, recurso
   `membership`, id de la membresía, tenant de la membresía, actor de sistema
   (`AuditActorType.System`) con el id del usuario afectado — el mismo criterio que
   `identity.user.deleted` en el worker.
4. Un solo `SaveChangesAsync`. No emite eventos de outbox: nadie consume un borrado de
   membresía, y quitarla ya emitió el suyo.
5. Devuelve cuántas membresías borró.

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

- **Falla después de la purga y antes del commit de Identity:** el inbox no se marca como
  procesado, así que el mensaje vuelve en su reintento. El usuario sigue existiendo, las sondas
  siguen diciendo que no, el purgador no encuentra nada (no-op) y el usuario se borra.
- **Al revés** (usuario primero, membresías después), una falla entre los dos commits dejaría
  filas apuntando a un usuario inexistente, y el mensaje ya marcado: nada las volvería a mirar.

Por eso el purgador tiene que ser idempotente: sin nada que purgar, no hace nada ni commitea.

**Costo aceptado.** Si la purga commitea y después falla el commit de Identity, el usuario queda
vivo sin sus filas `Removed`/`Expired`, y su código de asesor ya está libre. Se acepta porque ese
usuario no tiene historia y no puede generarla: no tiene acceso, y la única forma de volver es
una invitación, que se serializa con el mismo lock de ciclo de vida. Esa invitación encuentra al
usuario, no encuentra membresía y crea una nueva. El reintento borra al usuario si para
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
código ya quedó atado a un historial. Sin cotizaciones, pedidos, archivos ni cambios de precio no
hay historial que mezclar.

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
- Integración (`OrphanUserCleanupTests`): quien quitas después de cambiar el precio de un producto
  conserva su usuario, su membresía quitada y su código de asesor (ver «Columnas con id de usuario
  sin sonda», abajo).
- Integración (`OrphanUserCleanupTests`, ver «Reintentos y log de la purga»): un mensaje cuyo
  purgador falla siempre queda con un intento y no se reintenta antes de que venza su espera —una
  baja sana procesada en el medio prueba que hubo otro tick—; con el reloj de la prueba avanzando
  cada espera llega a seis intentos sin quedar nunca procesado, la espera se queda en 15 minutos
  y no se retoma antes; cuando la falla se arregla, el reintento siguiente borra al usuario y su
  membresía y termina el mensaje. Veinte mensajes envenenados, más viejos que una baja sana, no la dejan
  sin turno. La prueba de no filtrar estado rastreado ahora escribe las dos bajas en un solo
  commit: con backoff, A ya no vuelve en cada tick, y por la API nada garantiza que las dos caigan
  en el mismo lote.
- Unitarias de `MembershipUserReferencePurger`: devuelve cuántas membresías borró, y `0` sin nada
  que purgar.

## Columnas con id de usuario sin sonda

Al cerrar este spec quedaban dos columnas que guardan el id de `identity.users` —no una
membresía— y que ninguna sonda miraba, así que borrar un usuario las dejaba colgando. La regla
para decidir es la misma para las dos: **el worker evalúa a cada usuario una sola vez por baja**.
Una sonda retiene para siempre; por eso sólo va donde la fila es historia permanente.

### `catalog.product_price_changes.changed_by` → sonda

El histórico de precios es append-only y no se purga nunca (`ProductPriceChange`), y el reporte
de cambios de precio resuelve `changed_by` a un correo (`ReportingPeopleLookup.EmailsByUserIdAsync`):
con el usuario borrado, el autor del cambio salía vacío. Es historia, igual que una cotización.

`ProductPriceChangeUserReferenceProbe` (Catalog.Infrastructure, `Source => "catalog"`) responde
`AnyAsync(change => change.ChangedBy == userId)`, sin pasar por membresías ni filtrar por tenant:
la columna guarda el `SubjectId` tal cual (`UpdateProductHandler`, `CopyPriceScalesHandler`). Se
registra en `CatalogInfrastructureExtensions`, como las demás.

**Sin índice sobre `changed_by`.** El worker consulta una vez por membresía quitada, y las sondas
de Quotations ya recorren sin índice columnas de tablas más grandes (`quotation_history.member_id`,
`quotations.created_by`). Un índice lo pagaría cada cambio de precio —incluida la copia masiva de
escalas— para acelerar una consulta rara. Si la sonda llegara a pesar, el índice es una migración
de Catalog sin otro cambio.

### `quotations.export_jobs.requested_by` → nada

Una sonda acá es justamente el error: retendría al usuario para siempre por una fila que se borra
sola. Y no hace falta un purgador, porque nada se rompe con el usuario ausente:

- **Quién lee la columna.** El límite de pendientes (`ExportJobQueue.CountPendingAsync`), que sólo
  cuenta y que un usuario borrado ya no puede alcanzar; la auditoría del runner
  (`ExportJobRunner`, `actor_id`), que es snapshot; y los eventos `quotations.export-ready.v1` y
  `quotations.export-failed.v1` (`ExportJobEventPublisher`), cuyo `subjectId` resuelve
  Notifications. No hay listado de "mis exportaciones" ni descarga autenticada: el enlace viaja
  prefirmado en el correo.
- **Un job pendiente de un usuario borrado** se procesa igual —el runner no mira al usuario— y su
  correo no sale: `OutboxDeliveryWorker.ResolveRecipientAsync` no encuentra la dirección, guarda la
  notificación en `Failed` con `recipient_email_unavailable` y cierra el inbox, sin reintentos.
  Es lo que debe pasar: nadie recibe datos del tenant después de su baja.
- **El archivo** no es un `FileResource` de Storage ni tiene dueño: es un objeto bajo
  `exports/tenants/{tenant}/jobs/{job}.xlsx` (`ExportFileStorage.KeyFor`) que vence la regla de
  lifecycle `expire-exports` del bucket. `PurgeFinishedBeforeAsync` sólo borra filas.
- **La retención.** Todo job termina —completo o fallido en cuatro intentos, unos 21 minutos— y
  `ExportJobWorker` borra una vez por día los terminados hace más de `ExportJob.Retention` (30
  días). La fila colgada dura eso como mucho.

No hay cambio de código; el razonamiento queda en `ExportJob.RequestedBy`.

### Las demás columnas con id de usuario

Barrido de toda asignación de `IExecutionContext.SubjectId` a una entidad persistida y de toda
columna con forma de id de usuario en los `DbContext`. Ninguna otra necesita sonda:

- `audit.entries.actor_id`: append-only, snapshot.
- `notifications.notifications.recipient_id`: append-only, guarda la dirección en
  `recipient_address`.
- `platform.request_failures.subject_id`: diagnóstico; `ListRequestFailures` lo devuelve crudo, sin
  resolverlo, y `DELETE /request-log` borra lo que pasó los 7 días de
  `PurgeRequestFailuresHandler.RetentionDays`.
- Customers, Companies, Geography, Authorization y el resto de Catalog sólo usan el `SubjectId`
  para auditar o para el evento de su exportación (outbox, transitorio); sus tablas no guardan
  ids de usuario.
- `storage.file_resources.owner_id`, las columnas `MemberId` de Quotations y
  `tenancy.memberships.user_id` ya tenían su sonda.

## Reintentos y log de la purga

Eran los pendientes de este spec. Quedaron resueltos así.

### Reintentos con backoff, sin abandono

**Problema.** Un mensaje que fallaba siempre —un purgador que lanza
`tenancy.membership.not_purgeable`, una sonda caída— se reintentaba cada 3 s, sin tope ni espera,
con un Error y su stack trace en cada tick. Y como el lote se ordena por `OccurredAt`, ocupaba
un lugar en la cabeza para siempre: veinte así dejaban sin turno a todo mensaje más nuevo.

**Qué patrón se copió.** El repositorio ya tenía cuatro formas de consumir con reintento:

| Dónde | Qué hace ante un fallo |
| --- | --- |
| `OutboxProcessor` (Tenancy, `OutboxProcessor.cs:53-57`) | suma `attempts` y guarda `last_error` en `platform.outbox_messages`, pero no tiene tope ni espera ni filtra por intentos: reintenta en cada tick para siempre. Es el mismo problema. |
| `PaymentProofMoveProcessor` y `PaymentProofDetachProcessor` (Storage, `:95-102` y `:128-135`) | sin fila en el inbox, vuelve en el tick siguiente. Mismo problema. |
| `OutboxDeliveryWorker` + `InboxClaims` (Notifications, `OutboxDeliveryWorker.cs:29-41,106-117,197-213`; `InboxClaims.cs:18-43`) | **reclamo con lease en el inbox propio**: una sentencia `INSERT ... ON CONFLICT DO UPDATE ... RETURNING attempts` que se commitea sola, suma el intento y deja la fila reclamada hasta `claimed_until`. El lote excluye lo procesado y lo reclamado con lease vivo. Pasado `MaxAttempts = 3`, cierra el mensaje sin procesarlo. Lease fijo de 2 minutos. |
| `ExportJob` (Quotations, `ExportJob.cs:22,39-40,145-159`) | `Attempts`, `NextAttemptAt` y la única curva de espera del repositorio: `RetryDelays = [1, 5, 15]` minutos con `MaxAttempts = 4` (D11). |

Se copió **el reclamo de Notifications**, que es el caso más parecido: un consumidor del Outbox de
plataforma con inbox propio de clave (consumidor, id de mensaje). Y como su lease es fijo —es un
lease, no una espera—, la curva sale de **`ExportJob` (D11)**. El tope de los dos no se copió (ver
abajo).

**Cómo queda.**

- `identity.inbox_messages` suma `attempts` (default 1) y `claimed_until`, y `processed_at` pasa a
  nullable: migración `20261003010629_AddInboxClaims` de Identity, igual columna por columna a la
  de Notifications. Las filas que ya existen quedan procesadas con un intento.
- `IdentityInboxClaims.TryClaimAsync` es `InboxClaims` copiado —ningún módulo referencia la
  infraestructura de otro— con una diferencia: el lease del reclamo número n es
  `RetryDelays[min(n, 3) - 1]`, así que es a la vez el lease de quien procesa y la espera antes del
  reintento. Esperas de 1, 5 y 15 minutos, y 15 en cada intento siguiente.
- El worker reclama antes de procesar. Como el reclamo se commitea solo, el intento queda contado
  aunque la unidad de trabajo del mensaje falle y su scope se descarte entero: no hace falta
  escribir el fallo después, en otro contexto.
- El lote filtra lo procesado y lo que espera su reintento (`processed_at IS NOT NULL OR
  claimed_until >= now`), igual que Notifications. Un mensaje envenenado deja de ocupar lugar y el
  lote se llena con lo que sí toca.
- **Sin tope: un mensaje nunca se abandona.** Cada reclamo procesa; después del tercero la espera
  queda en 15 minutos para siempre. Una falla con intento menor a `ErrorAfterAttempts = 4` es un
  Warning; desde el cuarto, un Error. Los dos llevan la excepción, el id del mensaje, el del
  usuario y el número de intento. Así un mensaje trabado aparece en Error cada 15 minutos hasta
  que se arregle la causa, y el primer reintento después lo procesa solo. Si lo que falla es el
  reclamo mismo (la base, por ejemplo), no se contó ningún intento: se loguea aparte, en
  Warning, y el mensaje vuelve en el tick siguiente.
- Lo que no cambió: el advisory lock, un scope por mensaje, y el éxito marca el inbox
  (`processed_at`) en el mismo `SaveChanges` y la misma transacción que el borrado.
- El "ahora" del worker sale de `IClock`, como en Notifications: las pruebas vencen las esperas
  avanzando el reloj, sin dormir minutos.
- El `Down` de la migración borra las filas reclamadas y sin terminar antes de volver a hacer
  `processed_at` obligatorio. Sin eso, el rollback las sellaría con `0001-01-01` y el worker de
  antes las daría por procesadas; borradas, el mensaje vuelve a la cola.

**Por qué se aparta de Notifications y no abandona.** El tope venía copiado de Notifications,
donde un correo que llega con media hora de atraso ya no sirve. Borrar a un usuario huérfano no
tiene plazo. Abandonar convertía una falla pasajera de un módulo —una sonda o un purgador caídos
unos 36 minutos, por un deploy malo— en residuo permanente: usuarios que nunca se limpian y que
sólo se recuperan con SQL a mano. Lo que había que resolver eran dos cosas, el ruido en el log y
el lote acaparado, y el backoff solo ya las resuelve.

### Log de la purga

`IUserReferencePurger.PurgeAsync` devuelve `Task<int>`: cuántas filas borró. El worker arma, una
sola vez y en una variable, el resumen por fuente (`tenancy=1`, o `none` sin purgadores) y lo suma a
la línea Information del borrado: `User {UserId} deleted after membership removal: no module
references it. Purged rows by source: {PurgedRows}.` Armarlo dentro de la llamada al logger es lo
que rechaza CA1873. Los purgadores no loguean: el log de la purga vive en un solo lugar.

### Revocación de sesiones

Quedó como pendiente al cerrar lo de arriba, y se resolvió en un cambio aparte.

**Problema.** `SessionRevocationWorker` corre sobre el mismo evento y sobre
`tenancy.membership-suspended.v1`. Tenía un solo scope por lote y **ningún catch por mensaje**: un
mensaje que lanzaba cortaba el lote entero en `LogTickFailed`, y como el lote se ordena por
`OccurredAt`, ese mensaje quedaba primero en cada tick y **ninguna revocación posterior a él se
procesaba nunca**. Sin tope ni backoff. Sus fuentes de fallo eran un payload ilegible, que Tenancy
no escribe, y un conflicto de concurrencia con `OrphanUserCleanupWorker` al tocar las mismas
sesiones.

**Qué tan grave era: defensa en profundidad, no un acceso abierto.** El acceso al tenant se corta
en el request siguiente a la suspensión o la baja, sin esperar al worker:

- `ExternalClaimsTransformation.TransformAsync` corre el paso de tenant y permisos también para el
  principal de la cookie de sesión (`src/Bootstrapper/Authentication/ExternalClaimsTransformation.cs:57-61`),
  y en ese paso, si `IAuthorizationService.ResolvePermissionsAsync` devuelve `null`, no agrega ni
  el claim de tenant ni los de permisos (`:112-119`).
- `AuthorizationService.ResolvePermissionsAsync` devuelve `null` cuando no hay membresía activa
  (`src/Modules/Authorization/Modules.Authorization.Application/AuthorizationService.cs:33-40`), y
  `MembershipDirectory.FindActiveRolesAsync` sólo da roles para `State == Active`
  (`src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDirectory.cs:17-19`): `Suspended` y
  `Removed` quedan afuera.
- Toda política de permiso exige el claim (`src/Bootstrapper/QepServiceCollectionExtensions.cs:1153-1154`,
  `RequireClaim(QepClaimTypes.Permission, permission)`), y los handlers revalidan tenant y permiso.

Lo que una sesión sin revocar todavía permite es lo que no pide un tenant, como `/auth/me`, y los
tenants donde la persona sigue activa (la revocación la saca de todos a propósito). Un atraso en
la revocación es eso, no acceso al tenant del que la sacaron.

**Cómo queda.**

- El esqueleto que ya tenía `OrphanUserCleanupWorker` se extrajo a `ClaimedOutboxConsumer`
  (Identity.Infrastructure/Messaging), y los dos workers lo usan: lote con el filtro de procesados
  y reclamos vivos, reclamo con `IdentityInboxClaims` bajo la clave de cada consumidor, un scope y
  un try/catch por mensaje, y `MarkProcessedAsync` para terminar la fila del reclamo en el mismo
  `SaveChanges` que el efecto. Cada worker conserva lo suyo: qué hace con el mensaje, su curva y
  sus logs. La lectura del `userId` del payload pasó a `MembershipEventPayload`, compartida.
- Sin migración nueva: las columnas son las de `20261003010629_AddInboxClaims`. Las filas que ya
  tenía `identity.session-revocation` están procesadas y el filtro las sigue excluyendo.
- **Curva más corta: 5 s, 30 s y 5 minutos, y 5 de ahí en adelante.** Acá el atraso es una sesión
  que sigue viva, y un reintento cuesta una consulta y un `UPDATE` por sesión; la de 1, 5 y 15
  minutos del huérfano es para un trabajo sin plazo. La primera espera es casi un tick porque la
  falla más probable es pasajera —un conflicto de concurrencia con `OrphanUserCleanupWorker` sobre
  las mismas sesiones— y antes del reclamo ese reintento llegaba a los 3 s del tick siguiente: con
  una primera espera de 30 s la sesión quedaba viva diez veces más.
- **Sin abandono**, por la misma razón que el huérfano y con más motivo: una revocación abandonada
  deja viva una sesión que tenía que morir, y sólo se arregla a mano.
- `ErrorAfterAttempts = 3`: el tercer intento es el primero que deja por delante la espera de 5
  minutos. Si falla, la causa ya sobrevivió a dos reintentos rápidos (5 y 30 s) y la sesión va a
  seguir viva por lo menos cinco minutos más; antes, Warning. La falla del reclamo mismo
  va en su propia línea, en Warning. Todas llevan excepción, id del mensaje, id del usuario —leído
  sin lanzar, en una variable por CA1873— e intento.
- El "ahora" sale de `IClock`, para el reclamo, el `revoked_at`, la auditoría y el inbox. Antes
  era `DateTimeOffset.UtcNow`; `SystemClock` lo trunca a microsegundos, que es lo mismo que
  `timestamptz` ya guardaba, así que no cambia nada observable.

**Pruebas** (`SessionRevocationTests`, Identity.IntegrationTests): un mensaje ilegible más viejo
que una suspensión sana no impide que la sesión sana se revoque (RED contra el worker anterior:
la sesión nunca se revocaba); un mensaje que falla siempre queda reclamado 5 s en su primer
intento, no se retoma antes de su lease, se retoma después, llega a cinco intentos sin terminar,
el lease se satura en 5 minutos, y cuando se arregla el payload el reintento siguiente revoca y
termina el mensaje; y quitar una membresía (`tenancy.membership-removed.v1`) revoca la sesión con
motivo `membership_removed` y termina el mensaje. El camino feliz de punta a punta con cookie real
sigue en `RealAuthenticationApiTests.SuspendingMembershipRevokesTheMembersActiveSession`. Las
pruebas leen la curva de la instancia del worker que corre (`ClaimedOutboxConsumer.LeaseFor` y
`Leases`), que es el único lugar donde vive una vez armado.

## Pendientes

Ninguno de los de este spec.

## Fuera de alcance

- `tenants.owner_membership_id` no se toca: la membresía del owner nunca llega a `Removed`
  (`EnsureNotOwner`).
- La auditoría (`audit.entries.resource_id`) queda apuntando a membresías y usuarios borrados a
  propósito: es append-only y guarda snapshot.
