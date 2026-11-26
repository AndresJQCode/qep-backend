# Notificaciones en la app y alertas en vivo — Plan de implementación (backend)

> **Para quien ejecute:** SUB-SKILL requerido: `superpowers:subagent-driven-development` (recomendado) o `superpowers:executing-plans`.
> Los pasos usan casillas (`- [ ]`). **El `CLAUDE.md` del workspace manda sobre el proceso del skill** («Presupuesto de pruebas y
> ejecución de planes»): 13 tareas en **3 bloques** (A: Tareas 1–5, B: Tareas 6–9, C: Tareas 10, 11a, 11b y 12), **un implementador por bloque**,
> una revisión por bloque y la revisión fuerte (toda la rama) una sola vez al final. Cada tarea conserva su commit.

**Meta:** campana por miembro en `Notifications` (tabla, consumidores del outbox, cuatro rutas, retención) y alertas en vivo por el
`AppHub` (grupo del miembro y tópico `messaging`), con sus cinco productores y el aviso nuevo de ticket sin asignar.

**Arquitectura:** cada productor escribe un evento en el outbox de su módulo en la transacción del cambio; `Notifications` lo reclama
(`InboxClaims`), escribe la entrada y, después del commit, publica `notificationsChanged` al grupo del miembro con
`IRealtimeMemberPublisher`. La alerta de entrante la entrega un `IMessagingEventSink` de Bootstrapper directo al `AppHub` de cada pod.

**Stack:** .NET 10, EF Core + Npgsql, SignalR, xUnit v3, Testcontainers (sólo integración).

**Spec:** [`docs/superpowers/specs/2026-11-22-in-app-notifications-backend-design.md`](../specs/2026-11-22-in-app-notifications-backend-design.md) —
léelo antes de empezar; los §, D-n y nombres de este plan son los de ahí.

## Dónde se trabaja

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\in-app-notifications`, rama
  `feature/in-app-notifications` (creada desde `develop` `c9282c9af`). **Nunca** en el checkout `qep-backend` (está en `main` y es del
  owner).
- `git branch --show-current` debe decir `feature/in-app-notifications` antes de editar y **otra vez** antes de cada commit (comando
  aparte, no encadenado al commit).
- Merge final a `develop` (nunca `main`), **antes** que el frontend. `docs/estado.md` se actualiza **en `develop`, al mergear**, en el
  mismo merge o en un `docs:` inmediato: fila nueva «Notificaciones en la app (campana) y alertas en vivo» y entrada en el Registro.
  La rama de feature **no** toca `docs/estado.md`.

## Restricciones globales

- Texto que ve una persona, comentarios y docs en **español colombiano, tuteando**. Códigos, rutas, permisos, enums y eventos, tal cual.
- **Conventional commits, sin atribución de IA**: ignora cualquier trailer `Co-Authored-By` que te sugiera el entorno.
- **TDD con evidencia literal de RED y GREEN.** El RED de un fallo de compilación es la salida de `dotnet build`, sin contenedores.
  La integración corre en GREEN, **filtrada por nombre** (`--filter "FullyQualifiedName~Nombre"`); la clase completa una vez al final
  del bloque. **Nunca dos corridas de integración a la vez**: antes de una, `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`;
  si hay una, espera (es de otra sesión; no la detengas). Corre las pruebas **en primer plano**.
- `Api.exe` corriendo bloquea build y pruebas: avisa, no lo detengas.
- Migraciones **expand-only**, generadas con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- Todo hosted service nuevo entra a `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` en el mismo commit, con evidencia.
- El `QueueDoorbellInterceptor` no se registra a mano: `QueueDoorbellRegistration.AddQueueDoorbellInterceptor` ya lo agrega a todo `DbContext`
  de `Modules.*`, y timbra también los `INSERT` crudos.
- Cada commit deja el modelo de EF al día: una tarea que mapea columnas nuevas genera su migración en ese mismo commit.
- Valores fijados por el spec: retención **90 días** (`Notifications:InApp:RetentionDays`), lote de borrado **1.000**, edad máxima de evento
  **24 h**, `limit` **1–50** (default **20**), umbral **1–72 h** (default **4**) contado en el horario de atención del tenant (spec D-14; sin horario, corrido), destinatarios **≤ 20**, sondeo del
  worker de Soporte **5 min**.
- Suite completa sólo antes de mergear y limitada a los proyectos tocados: Notifications, Messaging, Support, ServiceContracts,
  Bootstrapper.UnitTests, ArchitectureTests (el cambio toca Bootstrapper y `Program.cs`, así que además compila toda la solución).

## Foco de revisión

1. Un `Subscribe("@member:<otro>")` desde el cliente se rechaza; nadie recibe avisos de otro miembro. Tarea 1 (`TheMemberGroupCannotBeSubscribed`).
2. `GET`/`POST read` con el id de una entrada de **otro miembro del mismo tenant** responde 404, y con otro tenant en la ruta, 403. Tarea 4.
3. El primer despliegue **no** crea entradas por eventos de más de 24 h. Tarea 3 (`EventsOlderThanTheMaxAgeAreNeverClaimed`).
4. Un `Take` o `AutoTaken` no avisa; un `Transferred` a uno mismo tampoco. Tarea 6.
5. Un ticket asignado antes del umbral no avisa; desasignado de nuevo vuelve a contar desde cero; uno que esperó días al cliente y vuelve a
   `Open` no avisa en el acto. Tarea 12.

---

## Bloque A — plataforma y núcleo de la campana

### Tarea 1: Entrega personal en el `AppHub`

**Archivos:** crear `src/BuildingBlocks/BuildingBlocks.Application/Realtime/IRealtimeMemberPublisher.cs`; modificar
`src/Bootstrapper/Realtime/{AppHub.cs,AppHubDispatcher.cs,RealtimeEnvelope.cs,RealtimeTopics.cs,PgRealtimePublisher.cs,AppRealtimeExtensions.cs}`;
probar en `tests/Bootstrapper/Bootstrapper.UnitTests/` (archivos de prueba del hub ya existentes: súmales los casos; si no hay, crea
`AppHubMemberGroupTests.cs`).

**Interfaces — produce:**
- `public interface IRealtimeMemberPublisher { Task PublishToMemberAsync(Guid tenantId, Guid memberId, string kind, object payload, CancellationToken cancellationToken); }`
  (nunca lanza, como `IRealtimePublisher`).
- `RealtimeEnvelope(string Kind, Guid TenantId, string Topic, JsonElement Payload, Guid? MemberId = null)`.
- `RealtimeTopics.MemberTopic = "@member"`, `RealtimeTopics.MemberGroupOf(Guid tenantId, Guid memberId) => $"{tenantId:D}:@member:{memberId:D}"`,
  `RealtimeTopics.ForbiddenCode = "realtime.topic_forbidden"` (D-8; deja de referenciar `SupportErrorCodes`).
- `PgRealtimePublisher` implementa también `IRealtimeMemberPublisher`; `AddAppRealtime` registra la misma instancia para las dos interfaces.

- [ ] **Paso 1: RED** — pruebas: `OnConnectedJoinsTheMemberGroup` (la conexión queda en `MemberGroupOf(tenant, member)`),
  `AMemberEnvelopeGoesToTheMemberGroupOnly` (dispatcher con `MemberId` → `Clients.Group(MemberGroupOf…)`, no al grupo del tópico),
  `TheMemberGroupCannotBeSubscribed` (`Subscribe("@member:" + otherMember)` lanza `HubException` con `realtime.topic_forbidden`),
  `AnUnknownTopicIsRefusedWithTheGenericCode` (reemplaza la aserción vieja con `support.realtime.topic_forbidden`; búscala con
  `rg "RealtimeTopicForbidden|topic_forbidden" tests`), `AnEnvelopeWithoutMemberIdStillDeserializes` (JSON de un pod viejo) y
  `NoAuthorizerPrefixStartsWithAt` (resuelve todos los `IRealtimeTopicAuthorizer` del contenedor y afirma que ninguno empieza por `@`).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Esperado: no compila
  (`RealtimeTopics.MemberGroupOf` no existe). Pega la salida.
- [ ] **Paso 3: implementa** — en `OnConnectedAsync`, después de `_connections.Register`, `Groups.AddToGroupAsync(Context.ConnectionId,
  RealtimeTopics.MemberGroupOf(tenantId, memberId.Value), Context.ConnectionAborted)`. En `DispatchAsync`, si `envelope.MemberId is { } m`,
  manda a `MemberGroupOf(envelope.TenantId, m)` y vuelve. `PublishToMemberAsync` arma el sobre con `Topic = MemberTopic`.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AppHub|FullyQualifiedName~Realtime"`. Pega el resumen.
- [ ] **Paso 5: commit** — `feat(realtime): deliver app hub notices to a member's own group`.

### Tarea 2: Tabla `notifications.in_app_notifications`

**Archivos:** crear `src/Modules/Notifications/Modules.Notifications.Domain/{InAppNotification.cs,InAppNotificationKind.cs}`; modificar
`Modules.Notifications.Infrastructure/Persistence/NotificationsDbContext.cs`; generar migración `AddInAppNotifications`; probar en
`tests/Modules/Notifications/Modules.Notifications.UnitTests/{InAppNotificationTests.cs,NotificationsDbContextMappingTests.cs}`.

**Interfaces — produce:**
- `public enum InAppNotificationKind { ConversationAssigned, ExportReady, ExportFailed, SupportCustomerReplied, SupportTicketUnassigned, WholesaleOrderPendingApproval, ServiceChargesOverdue }`
- `public sealed class InAppNotification` con `static Create(Guid tenantId, Guid memberId, InAppNotificationKind kind, Guid? resourceId, string dataJson, string dedupeKey, Guid? sourceMessageId, DateTimeOffset createdAt)`,
  `bool MarkRead(DateTimeOffset now)` (false si ya estaba leída; no cambia `ReadAt`), `bool Resolve(string resolution, DateTimeOffset now)`
  (si ya estaba resuelta devuelve `false` y no cambia nada; si no, pone `ResolvedAt`, escribe `resolution` en `data` y marca leída), propiedades del §11 del spec; `DedupeKeyMaxLength = 200`;
  `ToString()` sin `data`.
- `NotificationsDbContext.InAppNotifications` (`DbSet`), tabla e índices exactamente como §11 (nombres `IX_in_app_notifications_*`).

- [ ] **Paso 1: RED** — `CreateKeepsTheMemberAndKind`, `MarkReadIsIdempotentAndKeepsTheFirstReadAt`, `ResolveMarksReadAndSetsTheResolution`,
  `ResolvingTwiceKeepsTheFirstResolution`,
  `ToStringDoesNotPrintTheData`; en el mapeo: `InAppNotificationsMapToTheirTableAndIndexes` (tabla, `data` `jsonb`, índice único
  `IX_in_app_notifications_member_dedupe`, índice parcial `IX_in_app_notifications_member_unread` con filtro `read_at IS NULL`).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** entidad, enum y mapeo (`kind` con `HasConversion<string>().HasMaxLength(64)`, orden descendente en el índice de
  lista); genera la migración y revisa que `Up` sólo cree la tabla y sus índices.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~InAppNotification|FullyQualifiedName~NotificationsDbContextMappingTests"`.
- [ ] **Paso 5: commit** — `feat(notifications): add the in-app notifications table`.

### Tarea 3: Consumidores de la campana y exportaciones

**Archivos:** crear `Modules.Notifications.Infrastructure/InApp/{InAppOutboxConsumer.cs,InAppNotificationWriter.cs,ExportsInAppConsumer.cs}`;
modificar `Messaging/NotificationsDeliveryWorker.cs` (drena también `IEnumerable<InAppOutboxConsumer>`) y
`NotificationsInfrastructureExtensions.cs`; probar en `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InAppExportsTests.cs`
y `…UnitTests/NotificationsDeliveryWorkerTests.cs`.

**Interfaces — produce:**
- `internal abstract class InAppOutboxConsumer` con `protected abstract string Consumer { get; }`, `protected abstract IReadOnlyCollection<string> EventNames { get; }`,
  `protected abstract Task<IReadOnlyList<InAppNotification>> BuildAsync(OutboxRecord record, InAppContext context, CancellationToken ct)`,
  `internal virtual Task<bool> DrainAsync(CancellationToken ct)`; constantes `BatchSize = 20`, `MaxAttempts = 3`, `Lease = 2 min`,
  `MaxEventAge = TimeSpan.FromHours(24)`. Mismo reclamo que `OutboxDeliveryWorker` (`InboxClaims.TryClaimAsync`); candidatos con
  `record.OccurredAt >= now - MaxEventAge` y `EventNames.Contains(record.EventName)`, en orden de `OccurredAt`.
  Un `BuildAsync` que devuelve vacío procesa el mensaje sin entrada.
- `InAppNotificationWriter.SaveAsync(...)` (spec D-7): en **una** transacción explícita, cada entrada con su propio
  `INSERT INTO notifications.in_app_notifications … ON CONFLICT (tenant_id, member_id, dedupe_key) DO NOTHING RETURNING id` (SQL crudo, no
  `SaveChanges`: un 23505 dentro de un `SaveChanges` revertiría todo el lote), después el `processed_at` del inbox, y commit. **Después** del
  commit, `IRealtimeMemberPublisher.PublishToMemberAsync(tenant, member, "notificationsChanged", new { notificationId }, CancellationToken.None)`
  sólo por las filas que el `RETURNING` devolvió.
- `InAppContext(IMembershipDirectory Memberships, IClock Clock, NotificationsDbContext Database)`.
- `ExportsInAppConsumer`: `Consumer = "notifications.in-app.exports"`, los cinco eventos de exportación; `subjectId` → membresía (D-5);
  `exportKind` = `Products`/`Customers`/`Suppliers` por evento, o el `kind` del payload de Quotations; `rowCount` de `productCount`,
  `customerCount`, `supplierCount` o `rowCount`; `ExportFailed` sólo con `exportKind`. `dedupe_key` = id del mensaje.

- [ ] **Paso 1: RED** — unitarias: `TheLoopDrainsTheInAppConsumersToo`. Integración: `AProductExportReadyCreatesAnEntryForTheRequesterMembership`,
  `AnExportOfAUserWithoutMembershipLeavesNoEntry`, `AQuotationsExportFailedCreatesAnExportFailedEntry`,
  `EventsOlderThanTheMaxAgeAreNeverClaimed` (fila de outbox con `occurred_at` de hace 25 h: sin fila en `inbox_messages` ni entrada),
  `ProcessingTheSameMessageTwiceLeavesOneEntry`, `ANewEntryIsPublishedToTheMemberAfterCommit` (publicador falso registrado en el harness),
  `APartiallyDuplicatedBatchKeepsTheNewEntriesAndFinishes` (un mensaje con tres destinatarios cuando uno ya tenía su entrada con la misma
  `dedupe_key`: quedan las tres, `processed_at` puesto en el primer intento, una sola publicación por cada nueva).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** según Interfaces; registra los consumidores como singletons `InAppOutboxConsumer`.
- [ ] **Paso 4: GREEN** — unitarias con `--filter "FullyQualifiedName~NotificationsDeliveryWorkerTests"`; luego **una** corrida de integración:
  `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InAppExportsTests"`.
- [ ] **Paso 5: commit** — `feat(notifications): consume export events into the in-app inbox`.

### Tarea 4: Rutas de la campana (`Modules.Notifications.Api`)

**Archivos:** crear proyecto `src/Modules/Notifications/Modules.Notifications.Api/` (`.csproj` con `packages.lock.json`, sumado a la
solución y referenciado por `src/Api/Api.csproj`), `NotificationEndpoints.cs`; crear en Application `InApp/{ListInAppNotifications.cs,
GetUnreadCount.cs,MarkNotificationRead.cs,MarkAllNotificationsRead.cs,InAppNotificationDtos.cs,IInAppNotificationRepository.cs,
IInAppNotificationNames.cs,NotificationsErrorCodes.cs}`; `Modules.Notifications.Application.csproj` suma `ProjectReference` a
`BuildingBlocks.Application` y `Modules.Tenancy.Application` (`IMembershipDirectory`, `IExecutionContext`), como `Modules.Support.Application`:
`NotificationsLayerTests` sólo prohíbe que Application vea Infrastructure/Api, y ningún test prohíbe Tenancy a Notifications; repositorio en
Infrastructure; registro de handlers y validadores en `NotificationsInfrastructureExtensions` (o donde el resto de módulos registra los suyos;
lo verifica `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered`); **un solo implementador** de los nombres:
`src/Bootstrapper/InAppNotificationNames.cs`, que delega `MemberNamesAsync` en `IMessagingMemberNames` (la regla de `MessagingMemberNames`) y en
esta tarea devuelve un diccionario **vacío** en `ConversationContactNamesAsync` (lo completa la Tarea 6; hasta entonces `contactName` sale `null`);
`Program.cs` (`app.MapNotificationEndpoints();` junto a los demás); `tests/ArchitectureTests/ArchitectureTests/NotificationsLayerTests.cs`;
pruebas en `…IntegrationTests/InAppNotificationEndpointsTests.cs`.

**Interfaces — produce:**
- Rutas, DTOs y códigos **exactos** del §8.1–8.5 del spec. `NotificationsErrorCodes.NotificationNotFound = "notifications.notification.not_found"`.
- `InAppNotificationDto(Guid Id, string Kind, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? ResolvedAt, JsonElement Data)`,
  `InAppNotificationPageDto(IReadOnlyList<InAppNotificationDto> Items, string? NextCursor, int UnreadCount)` (el comentario del DTO explica
  el `unreadCount` por BFF).
- `IInAppNotificationNames { Task<IReadOnlyDictionary<Guid,string>> MemberNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct); Task<IReadOnlyDictionary<Guid,string>> ConversationContactNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken ct); }`
  — implementado sólo por `Bootstrapper/InAppNotificationNames` (ver Archivos).
- Cursor: base64url de `{createdAt:O}|{id:D}`; uno ilegible → `ValidationException` en `cursor`.
- `MarkAllReadRequest(string? UpTo)`; el validador lo parsea (ISO 8601 con zona) → 422 `validation.failed` con `errors.upTo` (spec §8.4). `CreatedAt`
  se serializa con el valor leído de la base, formato `"O"`.

- [ ] **Paso 1: RED** — `NotificationsLayerTests`: `ApiDoesNotReferenceInfrastructure` y `ApplicationDoesNotReferenceApi` como en los otros
  módulos. Integración: `ListReturnsOnlyTheCallersEntriesNewestFirst`, `TheCursorPagesWithoutRepeating`, `UnreadOnlyFilters`,
  `AnotherTenantInTheRouteIs403`, `AnotherMembersEntryIs404OnRead`, `MarkReadIsIdempotentAndReturnsTheUnreadCount`,
  `ReadAllRespectsUpTo` (una entrada posterior a `upTo` sigue sin leer), `ReadAllWithTheCreatedAtAsReceivedIncludesThatEntry` (el texto de
  `createdAt` de la lista, tal cual, como `upTo`), `AMalformedUpToIs422OnUpTo`, `AnExpiredExportHasNoDownloadUrl`,
  `ConversationAssignedResolvesTheActorName` (y `contactName` nulo hasta la Tarea 6), `WritesPublishNotificationsChangedToTheMember`
  (`read-all` publica `notificationId: null`); `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` sigue en verde.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/ArchitectureTests/ArchitectureTests` y `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** handlers (revalidación de tenant + `FindActiveMembershipIdAsync`, 403 `authorization.denied`), repositorio que
  filtra por `tenant_id` y `member_id` siempre, el armado de `data` del §8.5 (nombres al leer; `downloadUrl` nulo desde `expiresAt`) y las rutas
  con `RequireAuthorization()`. Agrega `Microsoft.EntityFrameworkCore` sólo en Infrastructure (ArchitectureTests).
- [ ] **Paso 4: GREEN** — `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~NotificationsLayerTests|FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered"`; luego
  `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InAppNotificationEndpointsTests"`.
- [ ] **Paso 5: commit** — `feat(notifications): add the in-app inbox endpoints`.

### Tarea 5: Retención

**Archivos:** crear `Modules.Notifications.Infrastructure/InApp/InAppNotificationRetentionWorker.cs`; modificar `NotificationsOptions.cs`
(`InApp.RetentionDays = 90`, validado 1–365 en `NotificationsOptionsValidator`), `appsettings` de ejemplo si `ConfigurationExampleTests` lo exige,
`MultiReplicaInventory.cs`; probar en `…IntegrationTests/InAppRetentionTests.cs` y `…UnitTests/NotificationsOptionsValidatorTests.cs`.

**Interfaces — produce:** `internal sealed class InAppNotificationRetentionWorker : BackgroundService` con
`internal Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)` (borra `created_at < now − RetentionDays` en lotes de 1.000 hasta
agotar, y quita `downloadUrl` de `data` en `ExportReady` vencidas); período 1 h. Inventario: `WorkerClassification.Idempotent` — `DELETE` por corte e `UPDATE` idempotente.

- [ ] **Paso 1: RED** — `PurgeDeletesOnlyWhatIsOlderThanTheRetention`, `PurgeDeletesInBatchesUntilDone` (2.500 viejas → 0),
  `PurgeScrubsExpiredDownloadUrls`, `RetentionDaysOutOfRangeIsInvalid`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** con `ExecuteSqlAsync` (subconsulta de ids `LIMIT 1000`) y la entrada del inventario con archivo y línea.
- [ ] **Paso 4: GREEN** — `dotnet test …UnitTests --filter "FullyQualifiedName~NotificationsOptionsValidatorTests"`;
  `dotnet test …IntegrationTests --filter "FullyQualifiedName~InAppRetentionTests"`; `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests"`.
- [ ] **Paso 5: cierre del bloque A** — el proyecto completo, una sola corrida: `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests -- xUnit.MaxParallelThreads=4`.
- [ ] **Paso 6: commit** — `feat(notifications): expire in-app notifications after ninety days`.

---

## Bloque B — productores de Mensajería, Mayorista y Cobros

### Tarea 6: `messaging.conversation.assigned.v1` y su consumidor

**Archivos:** crear `Modules.Messaging.Application/ConversationAssignmentNotice.cs`,
`Modules.Messaging.Infrastructure/Persistence/ConversationAssignmentOutbox.cs`; modificar
`Persistence/ConversationEvents.cs` y `Webhook/ConversationEventRows.cs` (escriben el evento junto a la fila, misma transacción);
si Mensajería no tiene un lector de nombres por ids,
crear `Modules.Messaging.Application/IMessagingConversationLabels.cs` + implementación en Infrastructure; en Notifications,
`InApp/ConversationsInAppConsumer.cs`; en Bootstrapper, `InAppNotificationNames.ConversationContactNamesAsync`. Pruebas:
`tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAssignmentNoticeTests.cs`,
`…Messaging.IntegrationTests/ConversationAssignedOutboxTests.cs`, `…Notifications.IntegrationTests/InAppConversationsTests.cs`.

**Interfaces — produce:**
- `public static class ConversationAssignmentNotice { public const string EventName = "messaging.conversation.assigned.v1"; public static AssignmentNotice? From(ConversationEvent value); }`
  con `public sealed record AssignmentNotice(Guid MemberId, string Reason, Guid? ActorMemberId)`; `Reason` ∈ `Transferred`, `Inherited`, `Handoff`.
  Reglas del §6: `Transferred` con `Target` no nulo y distinto de `Actor`; `Inherited` con `Target`; `AutomationPaused` con
  `PauseReason == Handoff` y `Handoff.AssignedMemberId`. Todo lo demás, `null`.
- Payload §12; `CorrelationId` = id de la conversación. `OccurredAt` de la **fila del outbox** = `IClock.UtcNow` (en la ingesta, el `now` que
  ya recibe `ConversationEventRows.InsertAsync`); el `occurredAt` del **payload** = la hora del evento (la de Meta en la ingesta). Spec §12.
- Contacto: nombre del cliente vinculado, si no el `profile_name`, si no `null` (spec §8.5).

- [ ] **Paso 1: RED** — unitarias: `ATransferToSomeoneElseNotifiesTheTarget`, `ATransferToOneselfDoesNotNotify`, `TakenAndAutoTakenDoNotNotify`,
  `InheritedNotifiesTheHeir`, `AHandoffWithAMemberNotifiesThatMember`, `AHandoffWithoutMemberDoesNotNotify`. Integración (Messaging):
  `TransferWritesTheAssignedEventInTheSameTransaction` (handler real; un fallo forzado del `SaveChanges` no deja evento),
  `InheritedIngestionWritesTheAssignedEvent`, `ALateMetaRedeliveryStampsTheOutboxRowWithNow` (entrante con hora de Meta de hace 30 h: la fila
  del outbox lleva la hora de escritura y el payload la de Meta). Integración (Notifications): `AnAssignedEventCreatesAConversationEntryWithReason`,
  `TheEntryResolvesTheContactName` (ahora sí, con `ConversationContactNamesAsync`).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Esperado: no compila.
- [ ] **Paso 3: implementa** según Interfaces. En el camino SQL, el insert al outbox es otro `ExecuteSqlAsync` sobre el mismo `dbContext`
  (comparte la transacción de la ingesta).
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationAssignmentNoticeTests"`;
  luego, de a una: `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationAssignedOutboxTests"` y
  `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InAppConversationsTests"`.
- [ ] **Paso 5: commit** — `feat(messaging): publish conversation assignments to the outbox for the in-app inbox`.

### Tarea 7: Alerta de entrante por el `AppHub`

**Archivos:** crear `Modules.Messaging.Application/{MessagingRealtimeTopics.cs,IMessagingMemberPermissions.cs}`,
`Modules.Messaging.Api/Realtime/MessagingRealtimeAuthorizer.cs` (+ `AddMessagingRealtimeAuthorizer`), `src/Bootstrapper/MessagingMemberPermissions.cs`
(copia de `SupportMemberPermissions.cs`), `src/Bootstrapper/Realtime/AppHubMessagingAlertSink.cs`; modificar `MessagingErrorCodes`
(`RealtimeTopicForbidden = "messaging.realtime.topic_forbidden"`), `AppRealtimeExtensions.AddAppRealtime` (registra el sink como otro
`IMessagingEventSink`), el registro DI del autorizador; pruebas en `tests/Bootstrapper/Bootstrapper.UnitTests/AppHubMessagingAlertSinkTests.cs` y
`…Messaging.UnitTests/MessagingRealtimeAuthorizerTests.cs`.

**Interfaces — produce:**
- `MessagingRealtimeTopics.Prefix = "messaging"`, `MessagingRealtimeTopics.Inbox = "messaging"`, `MessagingRealtimeEvents.InboundMessageAlert = "inboundMessageAlert"`.
- `IMessagingMemberPermissions.HasAnyAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken ct)`.
- Autorizador: sólo `topic == "messaging"`, módulo `messaging` prendido (tenant sin fila: no se niega, como Soporte) y `ConversationRead`;
  `CanSetPresenceAsync` siempre `false`.
- Sink (singleton, como `MessagingHubEventSink`; resuelve `ITenantModules` e `IMessagingMemberPermissions` en un scope propio por evento): para
  `MessagingEventKinds.InboundMessage`, `{ conversationId, messageId }` a `MemberGroupOf(tenant, AssignedMemberId)` **sólo si** el módulo `messaging`
  está prendido (tenant sin fila: no se niega) y ese miembro tiene `ConversationRead` (spec §9.2); sin asignado, a
  `RealtimeTopics.GroupOf(tenant, "messaging")`; otros kinds, nada.

- [ ] **Paso 1: RED** — `AnAssignedInboundAlertsOnlyThatMember`, `AnAssignedMemberWithoutConversationReadGetsNoAlert`,
  `WithTheMessagingModuleOffTheAssignedMemberGetsNoAlert`, `AnUnassignedInboundAlertsTheMessagingTopic`, `AConversationChangedSendsNoAlert`,
  `TheAlertCarriesIdsOnly` (el payload serializado tiene exactamente `conversationId` y `messageId`); autorizador:
  `OnlyTheExactMessagingTopicIsAccepted`, `WithoutConversationReadItIsRefused`, `WithTheModuleOffItIsRefused`, `PresenceIsAlwaysRefused`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Esperado: no compila.
- [ ] **Paso 3: implementa**. `MessagingHubEventSink` **no cambia** (sigue mandando `notify`, D-9).
- [ ] **Paso 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AppHubMessagingAlertSinkTests"` y
  `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessagingRealtimeAuthorizerTests"`.
- [ ] **Paso 5: commit** — `feat(messaging): alert inbound messages through the app hub`.

### Tarea 8: Pedidos mayoristas por aprobar

**Archivos:** crear `Modules.Notifications.Infrastructure/InApp/WholesaleInAppConsumer.cs`; probar en `…Notifications.IntegrationTests/InAppWholesaleTests.cs`.

**Interfaces — produce:** `Consumer = "notifications.in-app.wholesale"`, eventos `quotations.order.created.v1`, `…wholesale-approved.v1`,
`…wholesale-cancelled.v1` (D-13: un consumidor, orden de `occurred_at`). `created` con `source != "Wholesale"` → sin entrada. `created` mayorista →
entrada para `advisorMemberId`, `resource_id = orderId`, `data { orderId, orderNumber, resolution: null }`. `approved`/`cancelled` → `Resolve("Approved"|"Cancelled")`
sobre las entradas `(tenant, WholesaleOrderPendingApproval, orderId)` **todavía sin resolver** (`CancelOrder` publica `wholesale-cancelled` también
para un pedido aprobado), y `notificationsChanged` sólo a los miembros cuya entrada cambió.

- [ ] **Paso 1: RED** — `AWholesaleOrderNotifiesItsAdvisorOnly`, `AManualOrderLeavesNoEntry`, `ApprovalResolvesAndReadsTheEntry`,
  `CancellationResolvesTheEntry`, `ApprovalWithoutEntryDoesNothing`, `ACancelAfterApprovalKeepsApproved`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — `dotnet test …Notifications.IntegrationTests --filter "FullyQualifiedName~InAppWholesaleTests"`.
- [ ] **Paso 5: commit** — `feat(notifications): notify the advisor of a wholesale order awaiting approval`.

### Tarea 9: Cobros vencidos del día

**Archivos:** crear `Modules.ServiceContracts.Infrastructure/Persistence/ServiceContractsOutboxMessage.cs` (proyección como `CatalogOutboxMessage`)
y `Reminders/OverdueDigestEventPublisher.cs`; modificar `ServiceContractsDbContext.cs` (`internal DbSet<ServiceContractsOutboxMessage> Outbox`,
`ExcludeFromMigrations`; el interceptor de timbre ya lo pone `QueueDoorbellRegistration`), `Reminders/ServiceContractsReminderProcessor.cs` (`SendDigestAsync`); en Notifications
`InApp/BillingInAppConsumer.cs`. Pruebas: `…ServiceContracts.IntegrationTests/OverdueDigestEventTests.cs`, `…Notifications.IntegrationTests/InAppBillingTests.cs`,
`tests/ArchitectureTests/ArchitectureTests` (`CompositionRootTests`).

**Interfaces — produce:** evento `billing.charges.overdue-digest.v1` con el payload del §12, escrito y guardado **justo después** de
`digests.TryClaimAsync` y **antes** del chequeo de correo, sólo si `overdue.Length > 0` (D-17). Consumidor `notifications.in-app.billing`,
`dedupe_key = "billing-overdue:" + runDate`.

- [ ] **Paso 1: RED** — `ADigestWithOverdueChargesPublishesTheEvent`, `ADigestWithoutOverdueChargesPublishesNothing`,
  `AMemberWithoutEmailStillGetsTheEvent`, `ARetakenClaimDoesNotDuplicateTheEntry` (dos eventos del mismo día → una entrada),
  `AClaimLeftSendingIsRetakenAndPublishes` (lease vencido en la misma fecha → el evento sale en la corrida siguiente; spec D-17);
  `EveryDbContextThatMapsADoorbellTableHasTheInterceptor` ya existe y debe seguir en verde sin tocar nada.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — de a una: `dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --filter "FullyQualifiedName~OverdueDigestEventTests"`;
  `dotnet test …Notifications.IntegrationTests --filter "FullyQualifiedName~InAppBillingTests"`;
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`.
- [ ] **Paso 5: cierre del bloque B** — las clases tocadas completas, de a una (`ConversationAssignedOutboxTests`, `InAppConversationsTests`,
  `InAppWholesaleTests`, `InAppBillingTests`, `OverdueDigestEventTests`) con `-- xUnit.MaxParallelThreads=4`.
- [ ] **Paso 6: commit** — `feat(service-contracts): publish the daily overdue charges for the in-app inbox`.

---

## Bloque C — Soporte

### Tarea 10: Outbox de Soporte y «el cliente respondió»

**Archivos:** crear `Modules.Support.Application/ISupportIntegrationEvents.cs`, `Modules.Support.Infrastructure/Persistence/{SupportOutboxMessage.cs,SupportIntegrationEvents.cs}`;
modificar `SupportDbContext.cs` (proyección; el interceptor de timbre ya lo pone `QueueDoorbellRegistration`), `Thread/SupportThreadRules.cs`
(`ReopenIfWaiting(SupportTicket ticket, SupportReadContext context, ISupportAuditPublisher auditPublisher, ISupportIntegrationEvents events, Guid? actorMemberId)`:
si reabrió, el ticket tiene responsable y `actorMemberId != ticket.AssigneeMemberId`, publica), y sus **tres** llamadores:
`SupportThreadFollower.cs` (las dos llamadas, `actorMemberId: null`) y `Thread/AssignUnclassified.cs` (`actor`); en Notifications `InApp/SupportInAppConsumer.cs`. Pruebas: `…Support.UnitTests/SupportThreadRulesTests.cs`,
`…Support.IntegrationTests/CustomerRepliedEventTests.cs`, `…Notifications.IntegrationTests/InAppSupportTests.cs`.

**Interfaces — produce:**
- `public interface ISupportIntegrationEvents { void PublishCustomerReplied(SupportTicket ticket, DateTimeOffset occurredAt); void PublishUnassignedAlert(SupportTicket ticket, IReadOnlyList<Guid> recipientMemberIds, int thresholdHours, DateTimeOffset unassignedSince, DateTimeOffset occurredAt); }`
  (acumula en `SupportDbContext`; commitea con la unidad de trabajo). `ticketNumber` = `SupportTicketNumber.Format(ticket.Number)`.
- Consumidor `notifications.in-app.support` con los dos eventos de Soporte (el de alerta lo produce la Tarea 12; el consumidor ya lo lee:
  una entrada por destinatario, `dedupe_key = "support-unassigned:{ticketId}:{unassignedSince:O}"`).

- [ ] **Paso 1: RED** — unitarias: `ReopeningAnAssignedTicketPublishesCustomerReplied`, `ReopeningAnUnassignedTicketPublishesNothing`,
  `ATicketThatWasNotWaitingPublishesNothing`, `TheAssigneeClassifyingTheMessageIsNotNotified` (actor = responsable → nada). Integración: `ALinkedInboundOnAWaitingTicketWritesTheEvent` (seguidor del hilo real),
  `CustomerRepliedCreatesAnEntryForTheAssignee`, `AnUnassignedAlertCreatesOneEntryPerRecipient`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~SupportThreadRulesTests"`; de a una:
  `…Support.IntegrationTests --filter "FullyQualifiedName~CustomerRepliedEventTests"` y `…Notifications.IntegrationTests --filter "FullyQualifiedName~InAppSupportTests"`.
- [ ] **Paso 5: commit** — `feat(support): notify the assignee when the customer replies`.

### Tarea 11a: El horario de atención como capacidad compartida

**Archivos:** crear `src/BuildingBlocks/BuildingBlocks.Application/BusinessHours/IBusinessHoursProvider.cs` (con `BusinessHoursScheduleRef` y
`BusinessHoursSpan`, exactamente como spec D-14) y `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/BusinessHoursProvider.cs`; modificar
`MessagingInfrastructureExtensions.cs` (`services.AddScoped<IBusinessHoursProvider, BusinessHoursProvider>()`, junto a los registros de
`IBusinessHoursRepository`/`IBusinessHours`); probar en `tests/Modules/Messaging/Modules.Messaging.UnitTests/BusinessHoursProviderTests.cs` (repositorio y
`ITenantClock` falsos) y `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (o el archivo que verifique registros: el contenedor real
resuelve `IBusinessHoursProvider`).

**Interfaces — produce:** `IBusinessHoursProvider` (spec D-14). Resolución, **una sola vez** en un método privado del provider: `scheduleId` que
`FindAsync` encuentra → ese; si no (nulo o borrado) → `FindDefaultAsync`; si no hay → `null`. `ElapsedBusinessSecondsAsync` =
`BusinessHoursClock.BusinessSeconds(schedule, from, to, calendar.TimeZone)` con `calendar = await tenantClock.GetAsync(tenantId, ct)`;
`IsOpenAsync` con la misma resolución (sin horario, `true`), reusando la regla de `BusinessHoursRepository.GetStatusAsync` (si allí vive en un helper
privado, extráelo a uno interno compartido; no la copies). `ListSchedulesAsync` = `ListAsync` mapeado, por nombre, con `HasWeeklyHours` = algún día
de la semana con franjas. Al terminar, actualiza el comentario de `SupportLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` para nombrar
la excepción del contrato compartido (spec D-14, desvío deliberado); la aserción no cambia.

- [ ] **Paso 1: RED** — `ElapsedUsesTheChosenSchedule`, `ADeletedScheduleFallsBackToTheDefault`, `ANullScheduleUsesTheDefault`,
  `NoScheduleCountsCalendarHours` (`EffectiveScheduleId` nulo, segundos = `to − from`), `ElapsedUsesTheTenantTimeZone`,
  `AScheduleWithoutRangesCountsZero` (spec §5.18), `IsOpenWithoutSchedulesIsTrue`, `ListSchedulesReturnsIdNameDefaultAndWeeklyHours`; y
  `TheContainerResolvesTheBusinessHoursProvider`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Esperado: no compila (`IBusinessHoursProvider` no existe).
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~BusinessHoursProviderTests"`;
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MessagingLayerTests"`.
- [ ] **Paso 5: commit** — `feat(messaging): expose business hours as a shared capability`.

### Tarea 11b: Ajustes del aviso con el horario elegido

**Archivos:** crear `Modules.Support.Domain/SupportUnassignedAlertSettings.cs`; modificar `SupportTenantSettings.cs` (propiedades, `Update` con un parámetro
`SupportUnassignedAlertSettings unassignedAlert`, `SupportTenantSettingsChanges.UnassignedAlert`, y `UnassignedAlertEnabledAt` que se pone al pasar de
apagado a prendido), `SupportErrorCodes.cs` (`AlertRecipientInvalid = "support.settings.alert_recipient_invalid"`,
`AlertScheduleInvalid = "support.settings.alert_schedule_invalid"`), `Settings/UpdateSupportSettings.cs` (request y validador §8.6; el handler valida
destinatarios contra `ISupportMemberDirectory.ListManagersAsync` y `scheduleId` contra `IBusinessHoursProvider.ListSchedulesAsync`),
`Settings/GetSupportSettings.cs` + `SupportDtos.cs` + mapeo (bloque `unassignedAlert` del §8.6: `scheduleId`, `schedule.source`, `scheduleOptions` de
`IBusinessHoursProvider` con `hasWeeklyHours`, `timeZone` de `ITenantClock`, destinatarios con nombre y `canReceive` vía `ISupportMemberDirectory.FindAsync`,
`recipientOptions` vía `ListManagersAsync`), `SupportDbContext.cs`; **migración propia de esta tarea**, `AddUnassignedAlertSettings`, sólo con las
columnas de ajustes del §11 (la Tarea 12 trae la suya): así este commit deja el modelo de EF al día y `develop` en verde. Pruebas: `…Support.UnitTests/SupportTenantSettingsTests.cs`,
`…Support.IntegrationTests/SupportSettingsEndpointTests.cs` (súmale casos), `tests/ArchitectureTests/ArchitectureTests` (`SupportLayerTests`).

**Interfaces — consume:** `IBusinessHoursProvider` (Tarea 11a; Soporte sólo suma el uso de `BuildingBlocks.Application`, que ya referencia, así que
`SupportLayerTests` no cambia). **Produce:**
- `public sealed record SupportUnassignedAlertSettings(bool Enabled, int ThresholdHours, Guid? ScheduleId, IReadOnlyList<Guid> RecipientIds)`
  con `MinThresholdHours = 1`, `MaxThresholdHours = 72`, `MaxRecipients = 20`, `Default` (apagado, 4, `null`, vacío).
- `schedule.source` lo calcula el handler del `GET`: `None` sin horarios; `Chosen` si `ScheduleId` está en las opciones; `ChosenMissing` si no está;
  `Default` con `ScheduleId` nulo y un predeterminado.

- [ ] **Paso 1: RED** — dominio: `UpdateBumpsTheVersionOnceWithTheAlert`, `EnablingSetsEnabledAtAndReEnablingMovesIt`, `ThresholdOutOfRangeIsRejected`. Arquitectura: `SupportLayerTests`
  sigue en verde (Soporte no referencia `Modules.Messaging.*`). Integración: `PutWithoutUnassignedAlertKeepsTheStoredOne`,
  `PutWithANonManagerRecipientIs422AlertRecipientInvalid`, `PutEnabledWithoutRecipientsIs422OnRecipientIds`,
  `PutWithAScheduleOfAnotherTenantIs422AlertScheduleInvalid`, `GetReturnsRecipientsInOrderWithCanReceive`,
  `GetReportsChosenMissingAfterTheScheduleIsDeleted`, `GetReportsNoneWithoutSchedules`, `ScheduleOptionsNeedOnlySupportSettingsView` (miembro con
  `support.settings.manage` y sin `messaging.conversation.read` ve los nombres).
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila
  (`SupportUnassignedAlertSettings` no existe).
- [ ] **Paso 3: implementa** y genera `dotnet ef migrations add AddUnassignedAlertSettings --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations`;
  revisa que `Up` sólo agregue columnas con default.
- [ ] **Paso 4: GREEN** — `dotnet test …Support.UnitTests --filter "FullyQualifiedName~SupportTenantSettingsTests"`,
  `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~SupportLayerTests"` y una corrida de integración:
  `dotnet test tests/Modules/Support/Modules.Support.IntegrationTests --filter "FullyQualifiedName~SupportSettingsEndpointTests"`.
- [ ] **Paso 5: commit** — `feat(support): configure the unassigned-ticket alert on the tenant's business hours`.

### Tarea 12: `unassigned_since` y el worker del aviso

**Archivos:** modificar `Modules.Support.Domain/SupportTicket.cs` (`UnassignedSince` con la regla de spec D-15: se pone al **entrar** en «sin
responsable y en `New`/`Open`/`WaitingInternal`» —alta sin responsable, `Assign(null)` en un estado que cuenta, `ChangeStatus` hacia un estado que
cuenta sin responsable— y se borra con `Assign(miembro)` o al pasar a `WaitingOnCustomer`/`Resolved`/`Closed`; entre dos estados que cuentan no
cambia), `SupportDbContext.cs`; crear `Modules.Support.Infrastructure/SupportUnassignedAlertWorker.cs` (+ `SupportWorkerOptions`
con `UnassignedAlertInterval = 5 min`) y `Persistence/SupportUnassignedAlertStore.cs`; migración `AddUnassignedAlerts` (la columna del ticket, su
índice y la tabla `unassigned_alerts` del §11, con el backfill
`UPDATE support.tickets SET unassigned_since = now() WHERE assignee_member_id IS NULL AND status IN ('New','Open','WaitingInternal')`);
`MultiReplicaInventory.cs`; `.claude/rules/soporte-hub-notas-encuestas.md` (viñeta del aviso) y `.claude/rules/infra-valkey-sesiones.md` (grupo del
miembro del `AppHub`). Pruebas: `…Support.UnitTests/SupportTicketTests.cs` (súmale), `…Support.IntegrationTests/UnassignedAlertWorkerTests.cs`.

**Interfaces — produce:** `internal Task<int> SupportUnassignedAlertWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct)`: por tenant con
el aviso prendido, sana `unassigned_since` nulos de tickets sin responsable en un estado que cuenta (sin tocar `version`), toma tickets `New|Open|WaitingInternal` sin responsable sin fila en
`unassigned_alerts`, pide `IBusinessHoursProvider.ElapsedBusinessSecondsAsync(tenant, settings.ScheduleId, max(unassigned_since, enabled_at), now)` y, si
`BusinessSeconds >= thresholdHours × 3600`, en **una** transacción inserta el reclamo
(`ON CONFLICT DO NOTHING`) y, si ganó y quedan destinatarios con `canReceive`, `PublishUnassignedAlert`. Inventario:
`WorkerClassification.Idempotent` — el reclamo es un `INSERT … ON CONFLICT DO NOTHING` sobre la PK `(ticket_id, unassigned_since)`.

- [ ] **Paso 1: RED** — unitarias: `CreatingWithoutAssigneeSetsUnassignedSince`, `AssigningClearsItAndUnassigningSetsItAgain`,
  `WaitingOnCustomerClearsItAndReopeningSetsItAgain` (`ChangeStatus` a `WaitingOnCustomer` → `null`; vuelta a `Open` sin responsable → la hora de la
  vuelta), `MovingBetweenCountingStatesKeepsIt` (`Open` → `WaitingInternal` no lo cambia), `ResolvingClearsIt`. Integración:
  `ATicketThatWaitedOnTheCustomerDoesNotAlertWhenItReopens` (días en `WaitingOnCustomer`, `ReopenIfWaiting`, corrida inmediata → sin evento),
  `TheBackfillOnlyStampsCountingStates`,
  `ATicketPastTheThresholdAlertsOnce` (dos corridas → un evento), `ATicketAssignedBeforeTheThresholdDoesNotAlert`,
  `UnassigningAgainRestartsTheCounter` (segundo tramo → segundo evento), `InactiveRecipientsAreSkippedAndNoneLeftMeansNoEvent`,
  `TheClockStartsWhenTheAlertWasEnabled`, `ANullUnassignedSinceIsHealedWithoutBumpingTheVersion`, `ADisabledTenantIsSkipped`,
  `ClosedHoursDoNotCount` (horario real creado con `POST /messaging/business-hours` en el harness: un ticket que queda sin asignar el viernes a las 17:00 con un horario de
  lunes a viernes 08:00–18:00 y umbral 2 h avisa el lunes a las 09:00, no antes), `WithoutSchedulesHoursCountAroundTheClock`.
- [ ] **Paso 2: verifica el RED** — `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3: implementa**; genera la migración `AddUnassignedAlerts` y revisa que sólo agregue (columna nullable, tabla, índice, backfill).
- [ ] **Paso 4: GREEN** — de a una: `…Support.IntegrationTests --filter "FullyQualifiedName~UnassignedAlertWorkerTests"`,
  `…Support.IntegrationTests --filter "FullyQualifiedName~SupportSettingsEndpointTests"`,
  `tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~SupportLayerTests"`.
- [ ] **Paso 5: cierre del bloque C y de la rama** — `dotnet build` de la solución; luego, **de a un proyecto y en primer plano**, con
  `-- xUnit.MaxParallelThreads=4`: `Modules.Notifications.IntegrationTests`, `Modules.Support.IntegrationTests`,
  `Modules.ServiceContracts.IntegrationTests`, `Modules.Messaging.IntegrationTests`, y las unitarias de esos módulos más
  `Bootstrapper.UnitTests` y `ArchitectureTests`. Pega cada resumen.
- [ ] **Paso 6: commit** — `feat(support): alert leaders when a ticket stays unassigned`.

---

## Merge (después de la revisión fuerte)

1. `git -C <worktree> branch --show-current` → `feature/in-app-notifications`. Merge a `develop` en el checkout que esté en `develop` (no en el de
   `main`); si ninguno lo está, worktree temporal de ruta corta.
2. En `develop`: `docs/estado.md` (fila y Registro) en el merge o un `docs: record the in-app notifications in estado` inmediato.
3. Sin push: lo decide el owner. Deploy **backend primero**; el frontend después (spec del frontend §10).
4. Seguimiento (otra rama, cuando el frontend nuevo esté en producción): quitar `notify` de `MessagingHubEventSink` (spec D-9).
