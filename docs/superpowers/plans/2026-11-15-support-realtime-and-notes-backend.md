# Soporte — hub global en tiempo real, presencia por ticket y notas internas, backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de la sección «Antes de empezar» mandan sobre el proceso del skill** (bloques con un solo implementador, una revisión por bloque).

**Goal:** Que la consola sepa al instante que un ticket cambió (otro asesor, el worker del hilo) y que otro asesor está respondiendo el mismo ticket, por un hub de SignalR global que no es de ningún módulo; y que el asesor deje en el ticket notas internas con texto y hasta 5 adjuntos, que nunca salen al solicitante.

**Architecture:** `AppHub` vive en `src/Bootstrapper/Realtime/` y se mapea en `/api/v1/hubs/app`. El contrato común (`IRealtimeTopicAuthorizer`, `IRealtimePublisher`) va en `BuildingBlocks.Application`; cada módulo registra el autorizador de su prefijo de tópico. Los avisos salen después del commit por `pg_notify('qep_realtime', …)` y un listener por pod (`LISTEN qep_realtime`, conexión propia directa a Postgres) los entrega a sus conexiones, igual que Mensajería. La presencia «está respondiendo» vive en Valkey (`qep:rt:presence:{tenantId}:{topic}`, por conexión) con respaldo en memoria del pod. Las notas son dos tablas insert-only de `support`; sus archivos usan la subida sin miembro de Storage por el puerto `ISupportAttachmentStorage` de la fase 3. Mensajería no cambia: sigue en `MessagingHub`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs + SignalR, EF Core 10 + Npgsql (`LISTEN/NOTIFY`), StackExchange.Redis contra Valkey, FluentValidation, xUnit v3, Testcontainers (PostgreSQL y Valkey), `Microsoft.AspNetCore.SignalR.Client` en pruebas.

**Spec:** [`docs/superpowers/specs/2026-11-15-support-realtime-and-notes-design.md`](../specs/2026-11-15-support-realtime-and-notes-design.md), completo; **§9 manda sobre la redacción anterior**. Planes previos del módulo: [`fase 2`](2026-11-15-support-phase-2-messaging-backend.md) y [`fase 3`](2026-11-15-support-phase-3-public-form-backend.md) (la subida sin miembro, F30–F37, que las notas reusan).

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-realtime-notes`, rama `feature/support-realtime-notes`, sale de `develop` en `6e8c64f7` (fases 1 a 3 mergeadas). Rutas absolutas siempre: un script con `cwd` relativo escribe en el checkout de `develop`.
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena. Commit con `git commit -m "…" -- <rutas>`.
- Conventional commits en inglés, **sin `Co-Authored-By` ni ninguna atribución de IA**, aunque el harness la sugiera.
- PowerShell, sin `&&`. Si `Api.exe` o `dotnet Api.dll` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- **Presupuesto de pruebas (owner, 2026-11-12):**
  - TDD: la prueba primero. Si el RED esperado es de compilación (`CS0246`, `CS1061`…), la evidencia es la salida de `dotnet build` del proyecto de pruebas; **no se levantan contenedores para un RED de compilación**. Un RED de comportamiento (la prueba compila y falla) sí se corre, filtrado por nombre.
  - Mientras se itera, integración **filtrada por nombre**: `dotnet test $SI --filter "FullyQualifiedName~<NombreDeLaPrueba>" -- xUnit.MaxParallelThreads=4`. Nunca por clase mientras se itera.
  - Al cerrar cada bloque, **una** corrida de cada clase de prueba tocada en el bloque (filtro por nombre de clase).
  - Antes de mergear a `develop`: la suite de `$SU`, `$SI`, `$BU` y `$AT` (los proyectos de los módulos tocados), uno a la vez. Toda la solución no hace falta: no se tocan migraciones de otros módulos ni `Program.cs` más allá de mapear el hub; si la revisión final dice que el cambio es transversal, entonces sí.
  - **Nunca dos corridas de integración a la vez.** Antes de una corrida: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. Si hay otra, espera; no la detengas.
  - Los subagentes corren las pruebas **en primer plano**.
  - Guarda las salidas literales de RED y GREEN en el reporte del bloque.
- Atajos: `$W` = worktree; `$SU`/`$SI` = `$W\tests\Modules\Support\Modules.Support.{UnitTests,IntegrationTests}`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`.
- Migración con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add AddSupportTicketNotes --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  ```
- `$SI` necesita `<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" />` (la versión ya está en `Directory.Packages.props`, 10.0.13). Después: `dotnet restore $SI --force-evaluate` para regenerar su `packages.lock.json`, y verificar `dotnet restore --locked-mode` desde `$W`.
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Ruta del hub: `/api/v1/hubs/app` (ver N1). Métodos `Subscribe(topic)`, `Unsubscribe(topic)`, `SetPresence(topic, state)` con `state ∈ {composing, none}`. Eventos `supportTicketChanged { ticketId }` y `presenceChanged { topic, members: [{ memberId, name, state }] }`. Tópicos de Soporte: `support` y `support:ticket:{ticketId}`.
- El tenant de la conexión sale del claim `tenant_id` y el miembro de `FindActiveMembershipIdAsync(subject, tenant)`, exactamente como `MessagingHub.cs:28-51`. El tenant de un tópico es siempre el de la conexión; el grupo de SignalR es `{tenantId:D}:{topic}`.
- El hub avisa, no transporta: los payloads llevan ids (y en presencia, nombres de miembros presentes); la pantalla relee por HTTP.
- Avisos **después** del commit; un fallo del publicador se registra y no se propaga. Nada que se deshace publica.
- Presencia: TTL de la entrada 45 s, `EXPIRE` de la clave 60 s, el cliente renueva cada 20 s. Por conexión (`{memberId}:{connectionId}`), nunca por miembro.
- `presenceChanged` se entrega a cada conexión **sin su propio miembro** (la SPA no conoce su `memberId`).
- Support no referencia `Modules.Messaging.*` ni `Modules.Storage.*`; `SupportLayerTests` no se afloja. BuildingBlocks no referencia módulos. Mensajería no cambia.
- Notas: texto 1 a 4 096 recortado, sin caracteres de control salvo `\n`, `\r`, `\t` (la regla de `SupportTicketReply.IsValidText`); hasta 5 archivos; `POST` sin `If-Match` y **sin subir `version`** del ticket; ninguna nota en `public/support`, en eventos de integración ni en `ticket_events`.
- Tablas de notas insert-only: ningún método de dominio edita ni borra una nota.
- Mensajes legibles en español tuteando; códigos, rutas, eventos y tópicos como contrato, en inglés.

## Decisiones del plan

Mías (planner) sobre lo que el spec deja al implementador (§8) o no cubre. Las marcadas **(owner)** están en «Decisiones abiertas del owner» y el plan las asume como aquí dicen hasta que responda.

| # | Decisión | Por qué |
| --- | --- | --- |
| N1 | **Ruta `/api/v1/hubs/app`, no `/hubs/app`.** | `TenantRequestResolver.cs:18,33` acepta el query `tenantId` sólo bajo `/api/v1/hubs`: en `/hubs/app` la conexión no tendría claim `tenant_id` y abortaría siempre. Además el proxy de Vite y el rewrite de Vercel sólo llevan `/api/*`. Es la forma del spec («`/hubs/app`» dentro de la familia de hubs) con el prefijo que el código exige. |
| N2 | El contrato va en `src/BuildingBlocks/BuildingBlocks.Application/Realtime/` con namespace `BuildingBlocks.Application.Realtime`, no en un proyecto `BuildingBlocks.Realtime` nuevo. | Un proyecto nuevo pide csproj, `packages.lock.json`, entrada en `Backend.slnx` y reglas de arquitectura para dos interfaces. `BuildingBlocks.Application` ya lo referencian Support y Bootstrapper. |
| N3 | `IRealtimeTopicAuthorizer` suma `CanSetPresenceAsync` y `ForbiddenCode` a lo que lista §5.1. | §5.1 pide `support.ticket.manage` para `SetPresence` y un código propio de rechazo; con sólo `CanSubscribeAsync` el hub no puede saberlo sin conocer a Soporte. |
| N4 | `SupportRealtimeAuthorizer` resuelve permisos **por base**, no por claims: puerto nuevo `ISupportMemberPermissions` (Support.Application) con adaptador `SupportMemberPermissions` en Bootstrapper (membresía activa de Tenancy + `IRolePermissionChecker`, como `SupportMemberDirectory.ToRefAsync`). Módulo: misma regla que `TenantModuleGuard`. | La firma del spec recibe `memberId`, no claims; y un `Subscribe` horas después del handshake debe ver el permiso de hoy. Puerto nuevo y no un método más en `ISupportMemberDirectory`: sumar un método rompe cada doble que lo implementa (precedente F2 de la fase 3). |
| N5 | `SetPresence` sólo vale en `support:ticket:{id}` y sólo en un tópico al que **esa conexión** ya se suscribió; si no, `HubException` con el código del autorizador. Una conexión tiene a lo sumo 50 tópicos; el 51.º se rechaza igual. | La presencia de la bandeja no tiene sentido (§5.3 habla del ticket). El tope evita que un cliente roto llene la memoria del pod. |
| N6 | Al suscribirse a `support:ticket:{id}`, la «foto actual» llega como un evento `presenceChanged` sólo a esa conexión (no como valor de retorno de `Subscribe`). `Subscribe` a `support` no manda presencia. | La pantalla tiene un solo manejador para la presencia. |
| N7 | Publicador: `PgRealtimePublisher` abre una conexión con pool de `ConnectionStrings:QepDatabase` por aviso y corre `SELECT pg_notify('qep_realtime', @payload)` fuera de transacción. Listener: `RealtimeEventsListener`, conexión **propia** sin pool, resuelta con `StartupLockConnectionString.Resolve` (la misma regla de `ListenerConnectionString`, ya amarrada por `StartupLockConnectionStringParityTests`). No comparte la conexión del listener de Mensajería. | §8 lo deja abierto. Compartir obligaría a abrir los internals de Messaging; `pg_notify` sí pasa por PgBouncer y `LISTEN` no (gotcha del listener). Dos `LISTEN` por pod mientras Mensajería no migre. |
| N8 | Sobre de `NOTIFY`: `RealtimeEnvelope(string Kind, Guid TenantId, string Topic, JsonElement Payload)`, JSON `JsonSerializerDefaults.Web`. Un sobre que no se puede leer se descarta con un `Warning`. | Mismo criterio que `MessagingEventJson`. El payload de presencia con nombres queda muy por debajo de 8 000 bytes salvo con más de ~60 miembros escribiendo en un ticket a la vez. |
| N9 | Entrega de `presenceChanged`: cada pod la recibe por `NOTIFY` con la lista completa y la manda a **sus** conexiones suscritas, agrupadas por miembro, quitando al miembro de cada grupo (`AppHubConnections`, registro en memoria por pod: conexión → tenant, miembro y tópicos). El resto de los eventos va al grupo de SignalR. | §8 deja abierto cómo excluir al propio miembro; un grupo de SignalR manda el mismo payload a todos. |
| N10 | Vencimiento sin desconexión (pod caído, red cortada): `RealtimePresenceSweeper`, un `BackgroundService` por pod, cada 5 s relee la presencia de los grupos cuya última foto entregada por **este** pod no estaba vacía, y si cambió la entrega a sus conexiones (local, sin `NOTIFY`). | Sin barrido, una entrada vencida no la quita nadie hasta el siguiente cambio y el fantasma dura indefinidamente (anti-objetivo de §2: ~45 s). Con 5 s el peor caso es ~50 s. |
| N11 | Presencia: `IRealtimePresence` (Bootstrapper) con `ValkeyRealtimePresence` (área `Valkey:Areas:Presence`, la misma de Mensajería) y `LocalRealtimePresence` (memoria del pod, también respaldo cuando `ValkeyGateway.TryAsync` falla). Se registran en `ValkeyServiceCollectionExtensions.AddPresence`. Lectura en dos comandos (`ZREMRANGEBYSCORE` hasta ahora, `ZRANGEBYSCORE` desde ahora): la ACL de producción no permite Lua. | §5.3 y su degradación aceptada; `-@scripting` en la ACL (gotcha de `ValkeyFixedWindowRateLimiter`). |
| N12 | Nombres de la presencia: `IRealtimeMemberNames` (Bootstrapper) implementado por `MessagingMemberNames`, que ya implementa la regla `DisplayName ?? correo` para Mensajería y Pipeline. | «La regla vive en un solo lugar» (comentario de `MessagingMemberNames`). |
| N13 | `ISupportRealtime.TicketChangedAsync` se llama **explícitamente** después del commit en cada handler de escritura y en `SupportThreadFollower`, con `CancellationToken.None`. Escrituras: crear (consola, conversación y formulario público), estado, responsable, tipo, cliente, fijar, soltar, seguir el hilo, responder, nota, clasificar (el ticket elegido) y «De ninguno» (cada ticket candidato). Worker: el ticket vinculado, cada candidato de una fila nueva sin clasificar y el ticket de una fila que pasa sola. | §5.2. Explícito y no en `SupportUnitOfWork`: varias escrituras confirman con `ISupportTransaction`, no con `SaveChangesAsync`, y el UoW no sabe cuándo está confirmado. |
| N14 | Fuera de N13: la fusión y el borrado de clientes (`CustomerMergeReferences`, `CustomerDeletionReferences`, SQL crudo en Bootstrapper) **no** avisan. | No son handlers de Soporte; la pantalla los ve al releer. Queda como seguimiento. |
| N15 | La nota usa el `clientId` como `id` (PK de `ticket_notes`), como la respuesta usa el suyo. Reintento con el mismo `clientId`, mismo ticket y mismo autor: `201` con la nota guardada, **antes** de mirar si el ticket está `Closed`. Mismo `clientId` en otro ticket o de otro autor, o la carrera que pierde en `PK_ticket_notes`: `409 concurrency.conflict` (código existente). **(owner)** | §5.4 «idempotente por `clientId`» sin código para el choque; no se inventa uno. |
| N16 | El texto inválido lo atrapa `AddTicketNoteValidator`: `422 validation.failed` con `errors.text`. `support.note.text_invalid` lo lanza la guarda de dominio (`SupportTicketNote.Create`), inalcanzable por HTTP. `support.note.too_many_files`, `support.note.file_invalid` y `support.note.ticket_closed` sí salen por HTTP. | Regla del repo: texto libre lleva validador (el campo) además del dominio (el código). Precedente `SendTicketReplyValidator`. |
| N17 | `support.ticket_note_files` guarda además `file_name`, `mime_type` y `size_bytes`, la foto del archivo al crear la nota (además de `note_id`, `file_id`, `position` de §5.4). **(owner)** | `SupportNoteFileDto` los lleva y Support no ve a Storage: sin la foto, listar notas pediría a Storage cada archivo. Precedente: `support.ticket_attachments` (F35). |
| N18 | El `ownerId` de Storage de un archivo de nota es `SupportNoteUploads.OwnerKey(ticketId, memberId)`: los 16 primeros bytes de SHA-256 de `support-note|{ticketId:D}|{memberId:D}`. No es el id del ticket a secas. **(owner)** | §5.4 exige «subido por el mismo miembro» y Storage no guarda quién subió: el par `(owner_type, owner_id)` es lo único que `IStorageSystemUploads` exige en cada operación. Una subida del formulario público (clave aleatoria v4) no se puede usar en una nota ni al revés. No es un secreto contra un colega con `manage` en el mismo ticket, que igual puede escribir la nota. |
| N19 | Archivos de nota: mismos límites que el formulario (`SupportPublicAttachmentRules`: 10 MB, PDF/JPEG/PNG/WebP, nombre ≤ 260) y tope propio `SupportTicketNote.MaxFiles = 5`. Abrir y completar responden `422 support.note.file_invalid` ante una declaración inválida que pase el validador o un archivo ajeno; completar antes del `PUT` es `428 support.public.attachment_not_uploaded` (código existente). Abrir en un ticket `Closed`: `422 support.note.ticket_closed`. | §5.4. Reusa códigos existentes donde el spec no da uno. **(owner, junto con N15)** |
| N20 | `SupportAttachmentStorage.CountPendingAsync` (el tope `attachments_busy` del formulario público) cuenta sólo las subidas cuyo `owner_id` es un UUID v4, la clave del formulario (`SupportPublicUploads.KeyVersion`); la de una nota es v8 (`SupportNoteUploads.OwnerKeyVersion`). La versión se lee con `(get_byte(uuid_send(owner_id), 6) >> 4) = 4`, no con `uuid_extract_version` (Postgres 17+, versión de producción no probada en el repo). Completar o radicar con un `uploadKey` que no es v4 es `422 support.public.attachment_invalid` (`SupportPublicUploads.IsKey`, revisión final S2). Reemplaza al `NOT EXISTS` sobre `support.ticket_note_files` de la primera versión (revisión del Bloque B). | Sin eso, cada archivo de nota —reclamado o abandonado— contaría como «subida pendiente» del formulario, y a los 200 el formulario dejaría de aceptar adjuntos. |
| N21 | Descarga: `GET …/notes/{noteId}/files/{fileId}/download` responde `404 support.attachment.not_found` (código existente) si la nota no es de ese ticket o el archivo no es de esa nota; audita con `SupportAuditActions.TicketAttachmentDownloaded`. Crear una nota audita `support.ticket.note_added` (acción nueva, `SupportAuditActions.TicketNoteAdded`). | Mismo camino que `GetTicketAttachmentDownloadHandler`. |
| N22 | Página de notas: `SupportTicketNotesDto(IReadOnlyList<SupportTicketNoteDto> Items, SupportMessageCursorDto? Next)`, cursor opaco `SupportMessageCursor.Encode(createdAt, id)`; orden `created_at DESC, id DESC`. `take` 1–100 (50 por defecto) con validador, como `ListTicketMessagesQuery`. | §5.4 fija `before`/`take`; la forma de la página copia la del expediente. |
| N23 | `ticket_notes.author_member_id` entra en `SupportUserReferenceProbe`. | Gotcha del repo: una columna con id de membresía que no se suma deja borrar al usuario sin error. |
| N24 | El hub se registra en `AddQepPlatform` (`QepServiceCollectionExtensions`) con `AddAppRealtime()` y se mapea en `Program.cs` con `app.MapAppHub()` después de `MapMessagingHub()`. | Registrar en `Program.cs` obligaría a sumar la línea a `ReplayedProgramRegistrations` de `MultiReplicaInventoryTests`. |

## Review Focus

1. **Un tópico ajeno no se cuela:** un miembro con sólo el rol `billing` (sin Soporte) no se suscribe a `support` ni a un ticket; un ticket de otro tenant tampoco; y dos tenants suscritos a `support` no reciben los avisos del otro aunque el texto del tópico sea idéntico. Tarea 1: `AMemberWithoutSupportCannotSubscribe`, `ATicketOfAnotherTenantIsForbidden`, `TwoTenantsOnTheSameTopicDoNotSeeEachOther`.
2. **Dos pestañas del mismo miembro no se pisan:** pestaña A y B escribiendo, A se cierra: el otro asesor sigue viendo al miembro (B sigue). Tarea 3: `ClosingOneTabKeepsTheMemberPresentFromTheOther`.
3. **Fantasma de un pod caído:** una entrada que no se renueva desaparece de lo que ven los demás a los ≤ 50 s sin que nadie se desconecte. Tarea 3: `TheSweeperDropsAnExpiredEntryWithoutADisconnect`.
4. **Un archivo de nota no tumba el formulario público ni se borra solo:** con notas con archivos —o con subidas de nota abiertas y nunca adjuntadas— el tope `attachments_busy` no se mueve, porque cuenta sólo dueños v4 y la clave de una nota es v8 (N20); y el barrido de staging conserva el archivo de una nota. Tarea 5: `NoteFilesDoNotCountAsPendingPublicUploads`, `TheReferenceProbeKeepsANoteFile`.
5. **Una nota nunca toca la versión ni sale afuera:** después de crear una nota, un `PUT` de estado con la `version` cargada antes no da 412; la consulta pública del mismo ticket no muestra el texto; `ticket_events` no gana fila. Tarea 4: `ANoteDoesNotBumpTheVersionNorWriteTheTrace`, `TheConsultNeverShowsANote`.

---

## Contrato para las pantallas (BFF)

### Hub

| Qué | Valor |
| --- | --- |
| URL | `/api/v1/hubs/app?tenantId={tenantId}`, sólo WebSocket con `skipNegotiation: true` (como el de Mensajería). Sesión por cookie; sin permiso de módulo en el endpoint. |
| Al conectar | Sin claim de tenant, sin membresía activa: la conexión se aborta. |
| `Subscribe(topic: string)` | `support` (la bandeja) o `support:ticket:{ticketId}` (un detalle). Exige módulo `support` y `support.ticket.read` o `support.ticket.manage`; para un ticket, que sea del tenant. Si no: `HubException` con mensaje `support.realtime.topic_forbidden`. `Subscribe` **no devuelve nada**: suscribirse a un ticket manda enseguida un evento `presenceChanged` sólo a esa conexión con la foto actual (N6). Repetir la suscripción no duplica nada. |
| `Unsubscribe(topic: string)` | Sale del grupo y quita la presencia de esa conexión en ese tópico. Nunca falla. |
| `SetPresence(topic: string, state: string)` | `state` ∈ `composing`, `none`. Sólo en un `support:ticket:{id}` al que la conexión ya se suscribió y con `support.ticket.manage`; si no, `HubException` `support.realtime.topic_forbidden`. `composing` vale 45 s: la pantalla lo renueva cada 20 s mientras haya texto, y manda `none` al vaciar o enviar. |

| Evento | Payload | A quién |
| --- | --- | --- |
| `supportTicketChanged` | `{ ticketId: string }` | A los suscritos a `support:ticket:{ticketId}` **y** a los de `support`. La pantalla invalida el detalle (y las notas) o la bandeja y relee por HTTP. |
| `presenceChanged` | `{ topic: string, members: [{ memberId: string, name: string, state: "composing" }] }` | A cada conexión suscrita al ticket, **sin su propio miembro**. Lista vacía = nadie más escribe. Llega al suscribirse, tras cada `SetPresence`/desconexión y cuando una entrada vence. |

### HTTP, bajo `/api/v1/tenants/{tenantId:guid}/support`

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /tickets/{ticketId}/notes?before=&take=` | `SupportPolicies.TicketView` | `SupportTicketNotesDto` |
| `POST /tickets/{ticketId}/notes` (sin `If-Match`) | `support.ticket.manage` | `201 SupportTicketNoteDto` |
| `POST /tickets/{ticketId}/notes/attachments` | `support.ticket.manage` | `SupportNoteAttachmentSessionDto` |
| `POST /tickets/{ticketId}/notes/attachments/{fileId}/complete` | `support.ticket.manage` | `SupportNoteAttachmentStatusDto` |
| `GET /tickets/{ticketId}/notes/{noteId}/files/{fileId}/download` | `SupportPolicies.TicketView` | `SupportAttachmentDownloadDto` (`{ url }`, firmada 5 min) |

```csharp
public sealed record AddTicketNoteRequest(Guid? ClientId, string? Text, IReadOnlyList<Guid>? FileIds);
public sealed record SupportTicketNoteDto(Guid Id, string Text, SupportMemberDto Author, DateTimeOffset CreatedAt,
    IReadOnlyList<SupportNoteFileDto> Files);                                   // Files en el orden en que se mandaron
public sealed record SupportNoteFileDto(Guid FileId, string Name, string MimeType, long SizeBytes);
public sealed record SupportTicketNotesDto(IReadOnlyList<SupportTicketNoteDto> Items, SupportMessageCursorDto? Next);
public sealed record SupportNoteAttachmentRequest(string? Name, string? MimeType, long? SizeBytes);
public sealed record SupportNoteAttachmentSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);
public sealed record SupportNoteAttachmentStatusDto(string Status);             // "available" | "rejected"
```

Errores: `422 validation.failed` (`errors.ClientId`, `errors.Text`, `errors.Take`, `errors.Before`, `errors.Name`, `errors.MimeType`, `errors.SizeBytes`: las claves llegan en PascalCase, el `PropertyName` de FluentValidation tal como lo emite `ApiExceptionHandler`; el frontend ya las pasa a minúscula inicial); `422 support.note.too_many_files` (más de 5 o repetidos); `422 support.note.file_invalid` (archivo que no existe, de otro ticket u otro miembro, no `Available`, ya usado en otra nota); `422 support.note.ticket_closed`; `428 support.public.attachment_not_uploaded` (completar antes del `PUT`); `409 concurrency.conflict` (N15); `404 support.ticket.not_found`/`support.attachment.not_found`; `403` por tenant o permiso. Crear una nota **no** cambia `version` del ticket ni su `ETag`. El autor de la nota es la membresía de quien llama; un autor quitado sigue nombrado («Sin nombre» si no tiene nombre visible).

## Estructura de archivos

```
src/BuildingBlocks/BuildingBlocks.Application/Realtime/IRealtimeTopicAuthorizer.cs, IRealtimePublisher.cs        (nuevos)
src/Bootstrapper/Realtime/  AppHub, AppRealtimeExtensions, AppHubConnections, AppHubDispatcher, RealtimeTopics,
                            RealtimeEnvelope (+ RealtimeEventJson), PgRealtimePublisher, RealtimeEventsListener,
                            IRealtimePresence, LocalRealtimePresence, RealtimePresenceSweeper, IRealtimeMemberNames  (nuevos)
src/Bootstrapper/Valkey/    Presence/ValkeyRealtimePresence.cs (nuevo), ValkeyKeys (+RealtimePresence), ValkeyServiceCollectionExtensions (+)
src/Bootstrapper/           MessagingMemberNames (+IRealtimeMemberNames), SupportMemberPermissions (nuevo),
                            SupportAttachmentStorage (N20), SupportUserReferenceProbe (N23), QepServiceCollectionExtensions (+)
src/Api/Program.cs          app.MapAppHub()
src/Modules/Support/
  Domain/         SupportTicketNote, SupportTicketNoteFile, SupportNoteUploads, SupportErrorCodes (+4)
  Application/    ISupportRealtime, ISupportMemberPermissions, SupportRealtimeTopics, ISupportTicketNoteRepository,
                  Notes/ (AddTicketNote, ListTicketNotes, OpenNoteAttachment, CompleteNoteAttachment, GetNoteFileDownload),
                  SupportDtos (+), SupportAuditActions (+), cada handler de escritura y SupportThreadFollower (+aviso)
  Infrastructure/ SupportRealtimeNotifier, SupportTicketNoteRepository, SupportDbContext (+), SupportUnitOfWork (+),
                  SupportAttachmentReferenceProbe (+notas), migración AddSupportTicketNotes
  Api/            SupportRealtimeAuthorizer (+registro), SupportEndpoints (+notas)
```

---

## Bloque A — Hub global, avisos y presencia (Tareas 1–3, un implementador)

Las tres tocan `AppHub`, `AppHubConnections` y el registro de Bootstrapper: van juntas.

### Task 1: Hub global con tópicos y reparto por `NOTIFY`

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Application/Realtime/IRealtimeTopicAuthorizer.cs`, `IRealtimePublisher.cs`
- Create: `src/Bootstrapper/Realtime/AppHub.cs`, `AppRealtimeExtensions.cs`, `AppHubConnections.cs`, `AppHubDispatcher.cs`, `RealtimeTopics.cs`, `RealtimeEnvelope.cs`, `PgRealtimePublisher.cs`, `RealtimeEventsListener.cs`, `src/Bootstrapper/SupportMemberPermissions.cs`
- Create: `Modules.Support.Application/ISupportMemberPermissions.cs`, `SupportRealtimeTopics.cs`; `Modules.Support.Api/SupportRealtimeAuthorizer.cs`
- Modify: `Modules.Support.Domain/SupportErrorCodes.cs` (`RealtimeTopicForbidden`), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (N24, `ISupportMemberPermissions`, `IRealtimeTopicAuthorizer` de Soporte), `src/Api/Program.cs` (`app.MapAppHub()`), `$SI\Modules.Support.IntegrationTests.csproj` (SignalR.Client), `$AT\MultiReplicaInventory.cs`
- Test: `$SI\AppHubSupportTests.cs`, `$SI\AppHubHarness.cs`, `$BU\Realtime\RealtimeTopicsTests.cs`, `$BU\Realtime\RealtimeEnvelopeTests.cs`, `$AT\MultiReplicaInventoryTests.cs` (ya existe; debe seguir verde)

**Interfaces:**
- Produces:
  ```csharp
  namespace BuildingBlocks.Application.Realtime;
  public interface IRealtimeTopicAuthorizer {
      string Prefix { get; }                       // "support": el texto del tópico hasta el primer ':' (o el tópico entero)
      string ForbiddenCode { get; }                // "support.realtime.topic_forbidden"
      Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken);
      Task<bool> CanSetPresenceAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken);
  }
  public interface IRealtimePublisher {           // después del commit; nunca lanza (registra y sigue)
      Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken);
  }

  // Bootstrapper.Realtime
  public sealed class AppHub : Hub { Task Subscribe(string topic); Task Unsubscribe(string topic); Task SetPresence(string topic, string state); }
  public static class AppRealtimeExtensions {
      public const string HubPath = "/api/v1/hubs/app";
      public static IServiceCollection AddAppRealtime(this IServiceCollection services);
      public static IEndpointRouteBuilder MapAppHub(this IEndpointRouteBuilder endpoints);   // MapHub<AppHub>(HubPath).RequireAuthorization()
  }
  internal static class RealtimeTopics {
      public const int MaxLength = 200; public const int MaxPerConnection = 50;
      public static string GroupOf(Guid tenantId, string topic);   // $"{tenantId:D}:{topic}"
      public static string PrefixOf(string topic);
  }
  internal static class AppHubEvents { public const string PresenceChanged = "presenceChanged"; }
  internal sealed record RealtimeEnvelope(string Kind, Guid TenantId, string Topic, JsonElement Payload);   // canal "qep_realtime"
  internal sealed class AppHubConnections {      // singleton, por pod
      void Register(string connectionId, Guid tenantId, Guid memberId);
      bool TryAddTopic(string connectionId, string topic);         // false al pasar MaxPerConnection
      bool HasTopic(string connectionId, string topic);
      void RemoveTopic(string connectionId, string topic);
      IReadOnlyList<string> Remove(string connectionId);           // los tópicos que tenía
      IReadOnlyList<(string ConnectionId, Guid MemberId)> SubscribersOf(Guid tenantId, string topic);
  }

  // Modules.Support.Application
  public interface ISupportMemberPermissions {
      Task<bool> HasAnyAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);
  }  // false si la membresía no es del tenant o no está activa
  public static class SupportRealtimeTopics {
      public const string Prefix = "support"; public const string Inbox = "support";
      public static string Ticket(Guid ticketId);                  // $"support:ticket:{ticketId:D}"
      public static bool TryParseTicket(string topic, out Guid ticketId);   // exacto, Guid formato D
  }
  public static class SupportRealtimeEvents { public const string TicketChanged = "supportTicketChanged"; }
  ```

- [ ] **Paso 1: pruebas**
  - `RealtimeTopicsTests`: `TheGroupCarriesTheTenant` (`GroupOf(t, "support") == $"{t:D}:support"`); `ThePrefixIsTheTextBeforeTheFirstColon` (`support:ticket:x` → `support`; `support` → `support`).
  - `RealtimeEnvelopeTests`: `TheEnvelopeRoundTripsInCamelCase` (`kind`, `tenantId`, `topic`, `payload`); `AnUnreadablePayloadIsNull` (JSON roto, `TenantId` vacío, `Kind` vacío → `null`).
  - `AppHubSupportTests` (host propio con `OpenOwnHostAsync`, cliente SignalR con long polling como `MessagingHubTests.Connect`, URL `api/v1/hubs/app?tenantId=…`; la membresía la siembra `SeedCallerMembershipAsync` con el rol que la prueba pide):
    - `AnAdvisorSubscribesToTheInboxAndReceivesATicketChange` — rol `advisor`; `Subscribe("support")`; un `IRealtimePublisher` del contenedor publica `supportTicketChanged` en `support` → llega `{ ticketId }`.
    - `AMemberWithoutSupportCannotSubscribe` — rol `billing`; `Subscribe("support")` lanza `HubException` cuyo mensaje contiene `support.realtime.topic_forbidden`.
    - `ATicketOfAnotherTenantIsForbidden` — ticket sembrado en `OtherTenantId`; `Subscribe(SupportRealtimeTopics.Ticket(id))` desde el tenant propio → `HubException`.
    - `AnUnknownTicketOrMalformedTopicIsForbidden` — `support:ticket:no-es-guid`, `support:otro`, `nadie:sabe`, un tópico de 201 caracteres → `HubException`.
    - `WithTheSupportModuleOffNobodySubscribes` — tenant sembrado sin `support`.
    - `TwoTenantsOnTheSameTopicDoNotSeeEachOther` — dos conexiones de dos tenants suscritas a `support`; publicar en uno → el otro no recibe en 2 s.
    - `UnsubscribeStopsTheEvents`.
    - `WithoutMembershipTheConnectionIsAborted` — sujeto sin membresía: la conexión se cierra al conectar.
- [ ] **Paso 2: RED** — `dotnet build $BU` y `dotnet build $SI` fallan por `RealtimeTopics`, `AppHub`, `SupportRealtimeTopics` inexistentes (`CS0246`). Sin contenedores.
- [ ] **Paso 3: implementar**
  - `AppHub.OnConnectedAsync`: copia de `MessagingHub.cs:28-51` sin el chequeo de módulo (el hub no es de nadie): claims `qep_sub`/`sub` y `tenant_id`, `IMembershipDirectory.FindActiveMembershipIdAsync`, `AppHubConnections.Register`. `Subscribe`: longitud ≤ `MaxLength`, autorizador por `PrefixOf` (ninguno → `HubException("support.realtime.topic_forbidden")`, O3), `CanSubscribeAsync`, `TryAddTopic`, `Groups.AddToGroupAsync(GroupOf(...))`. Los autorizadores se resuelven del scope de la invocación (`IEnumerable<IRealtimeTopicAuthorizer>`, registrados `Scoped`).
  - `SupportRealtimeAuthorizer` (Support.Api, registrado desde `AddQepPlatform`): módulo `support` prendido (`ITenantModules`), `ISupportMemberPermissions.HasAnyAsync([Read, Manage])`, y para un ticket `ISupportTicketRepository.FindAsync(tenantId, …)` no nulo. `CanSetPresenceAsync`: tópico de ticket del tenant y `HasAnyAsync([Manage])`.
  - `PgRealtimePublisher` (N7) y `RealtimeEventsListener` (copia de `MessagingEventsListener`: backoff 1/2/5/10/30 s, `Task.Run` por notificación, log con el nombre de la clave, nunca el valor). `AppHubDispatcher.DispatchAsync(RealtimeEnvelope)`: `presenceChanged` según N9; cualquier otro `Kind` al grupo, con el `Payload` tal cual.
  - `MultiReplicaInventory`: `RealtimeEventsListener` como `PerPod` (LISTEN por pod) y `AppHubConnections` como estado por pod (registro de las conexiones de ese pod), con su línea de referencia como las de Mensajería.
- [ ] **Paso 4: GREEN** — cada prueba nueva filtrada por nombre (`--filter "FullyQualifiedName~AppHubSupportTests.AnAdvisorSubscribesToTheInboxAndReceivesATicketChange"`, etc.); `dotnet test $BU --filter "FullyQualifiedName~Realtime"`; `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests"`; `dotnet build` de la solución sin avisos.
- [ ] **Paso 5: commit** — `feat(realtime): add the global app hub with per-module topic authorization`.

### Task 2: Avisos `supportTicketChanged` desde cada escritura y el worker

**Files:**
- Create: `Modules.Support.Application/ISupportRealtime.cs`, `Modules.Support.Infrastructure/SupportRealtimeNotifier.cs`
- Modify: `Tickets/CreateTicket.cs`, `ChangeTicketStatus.cs`, `AssignTicket.cs`, `ChangeTicketType.cs`, `LinkTicketCustomer.cs`; `Conversations/CreateTicketFromConversation.cs`; `Public/FilePublicTicket.cs`; `Thread/PinTicketMessage.cs`, `ReleaseTicketMessage.cs`, `SetTicketFollowThread.cs`, `AssignUnclassified.cs`, `DismissUnclassified.cs`, `SupportThreadFollower.cs`; `Replies/SendTicketReply.cs`; `SupportInfrastructureExtensions.cs` (registro)
- Test: `$SI\RecordingRealtimePublisher.cs`, `$SI\SupportRealtimeNoticesApiTests.cs`

**Interfaces:**
- Consumes: Task 1 (`IRealtimePublisher`, `SupportRealtimeTopics`, `SupportRealtimeEvents`).
- Produces:
  ```csharp
  public interface ISupportRealtime {              // Modules.Support.Application; nunca lanza
      Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken);
  }  // SupportRealtimeNotifier: publica { ticketId } en Ticket(ticketId) y en Inbox
  ```

- [ ] **Paso 1: pruebas** — `RecordingRealtimePublisher` (singleton, guarda `(TenantId, Topic, Kind, Payload)`) reemplaza a `IRealtimePublisher` en un host propio por clase (`OpenOwnHostAsync(services => …)`, worker apagado). Cada prueba siembra su tenant y afirma que, **después** de la respuesta, hay exactamente dos avisos `supportTicketChanged` con ese `ticketId`, uno en `support:ticket:{id}` y otro en `support`:
  - `CreatingATicketAnnouncesIt` (consola), `CreatingFromAConversationAnnouncesIt`, `FilingThePublicFormAnnouncesIt`.
  - `StatusAssigneeTypeAndCustomerAnnounceTheTicket` (cuatro `PUT` seguidos, ocho avisos, en orden).
  - `PinReleaseAndFollowAnnounceTheTicket`, `AReplyAnnouncesTheTicket`.
  - `ClassifyingAnnouncesTheChosenTicketAndDismissingAnnouncesEveryCandidate`.
  - `AFailedWriteAnnouncesNothing` — `PUT /status` con `If-Match` viejo → `412` y cero avisos; `422 support.ticket.not_editable` → cero avisos.
  - `TheWorkerAnnouncesALinkedInboundWithoutBumpingTheVersion` — entrante sembrado, `SupportThreadFollower.ProcessBatchAsync` a mano → aviso del ticket, `version` igual.
  - `TheWorkerAnnouncesEveryCandidateOfANewUnclassifiedRow`.
  - `APublisherFailureDoesNotFailTheWrite` — publicador que lanza: la escritura responde 200 igual (lo garantiza `SupportRealtimeNotifier`).
- [ ] **Paso 2: RED** — `dotnet build $SI` falla por `ISupportRealtime` inexistente.
- [ ] **Paso 3: implementar** — una línea por handler, después del `SaveChangesAsync`/`CommitAsync` que confirma y antes de devolver, con `CancellationToken.None` (N13). En `SendTicketReply` también en el camino del retomado; en `DismissUnclassified` por cada candidato. `SupportRealtimeNotifier` envuelve en `try/catch` y registra con `LoggerMessage` (sin payload).
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): announce every ticket change through the app hub`.

### Task 3: Presencia «está respondiendo» por conexión

**Files:**
- Create: `src/Bootstrapper/Realtime/IRealtimePresence.cs`, `LocalRealtimePresence.cs`, `RealtimePresenceSweeper.cs`, `IRealtimeMemberNames.cs`; `src/Bootstrapper/Valkey/Presence/ValkeyRealtimePresence.cs`
- Modify: `AppHub.cs` (`SetPresence`, `Unsubscribe`, `OnDisconnectedAsync`, foto al suscribirse), `AppHubDispatcher.cs`, `ValkeyKeys.cs`, `ValkeyServiceCollectionExtensions.cs` (`AddPresence`), `MessagingMemberNames.cs`, `$AT\MultiReplicaInventory.cs`
- Test: `$BU\Realtime\LocalRealtimePresenceTests.cs`, `$BU\Realtime\RealtimePresenceSweeperTests.cs`, `$BU\Valkey\ValkeyRealtimePresenceTests.cs`, `$SI\AppHubPresenceTests.cs`

**Interfaces:**
- Consumes: Task 1 (`AppHubConnections`, `AppHubDispatcher`, `IRealtimePublisher`).
- Produces:
  ```csharp
  internal static class RealtimePresence {
      public static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(45);
      public static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(60);
      public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
      public const string Composing = "composing"; public const string None = "none";
  }
  internal interface IRealtimePresence {          // devuelven los miembros presentes (distintos, vigentes) tras el cambio
      Task<IReadOnlyList<Guid>> SetComposingAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct);
      Task<IReadOnlyList<Guid>> ClearAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct);
      Task<IReadOnlyList<Guid>> ReadAsync(Guid tenantId, string topic, CancellationToken ct);
  }
  internal interface IRealtimeMemberNames {
      Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
  }
  // ValkeyKeys
  public static RedisKey RealtimePresence(Guid tenantId, string topic) => $"{Prefix}rt:presence:{tenantId:D}:{topic}";
  ```

- [ ] **Paso 1: pruebas**
  - `LocalRealtimePresenceTests` (con un `TimeProvider` de prueba como `ValkeyPresenceTests.PresenceClock`): `ComposingAppearsAndNoneRemovesOnlyThatConnection`; `TwoConnectionsOfOneMemberCountOnce`; `AnEntryExpiresAfter45Seconds` (a los 44 s está, a los 46 s no); `RenewingExtendsTheEntry`.
  - `ValkeyRealtimePresenceTests` (contenedor de `ValkeyTestServer`, con `withProductionAcl: true`): las mismas cuatro, más `TheKeyIsTheRealtimePresenceKeyAndExpiresIn60Seconds` (`TTL` ≤ 60) y `WhenValkeyFailsItFallsBackToTheLocalRegistry` (multiplexer caído → responde desde `LocalRealtimePresence` y suma `qep_valkey_fallbacks_total{area="presence"}`).
  - `RealtimePresenceSweeperTests`: `TheSweeperDropsAnExpiredEntryWithoutADisconnect` — foto entregada con un miembro, el reloj avanza 46 s, `SweepOnceAsync` → el dispatcher recibe `presenceChanged` con `members: []`; `ANonChangingGroupIsNotResent`.
  - `AppHubPresenceTests` (host propio, roles `advisor`, dos sujetos A y B en el mismo ticket):
    - `ComposingReachesTheOtherMemberWithoutTheirOwnEntry` — A `SetPresence(ticket, "composing")` → B recibe `members: [{ memberId: A, name: "Ana", state: "composing" }]`; A recibe `members: []`.
    - `SubscribingReceivesTheCurrentSnapshot` — A escribe, B se suscribe después → B recibe la foto con A.
    - `NoneAndDisconnectClearThePresence`.
    - `ClosingOneTabKeepsTheMemberPresentFromTheOther` — A con dos conexiones escribiendo, cierra una → B sigue viendo a A.
    - `SetPresenceNeedsManageAndASubscription` — ningún rol de sistema da `read` sin `manage` (el harness: `admin` y `advisor` gestionan, `billing` no ve Soporte), así que esta prueba levanta su host con un `ISupportMemberPermissions` de prueba que para un miembro dado responde `true` a `Read` y `false` a `Manage`: se suscribe al ticket y `SetPresence` → `HubException`. Además: `advisor` sin suscribirse antes → `HubException`; en `support` → `HubException`; `state` distinto de `composing`/`none` → `HubException`.
- [ ] **Paso 2: RED** — `dotnet build $BU` falla por `LocalRealtimePresence`, `RealtimePresenceSweeper`, `ValkeyRealtimePresence` inexistentes.
- [ ] **Paso 3: implementar** — Valkey según §5.3 y N11 (miembro del sorted set `{memberId:D}:{connectionId}`, score = Unix de `now + 45 s`; lectura recorta y agrupa por la parte antes de `:`); las escrituras del hub sí se esperan (a diferencia de `ValkeyMessagingPresence`) porque devuelven la lista que se publica, y `TryAsync` ya acota con `OperationTimeoutMs`. Tras cada cambio: nombres por `IRealtimeMemberNames` (sin nombre → «Sin nombre») y `IRealtimePublisher.PublishAsync(tenant, topic, "presenceChanged", { topic, members })`. Al desconectar: `ClearAsync` y aviso por cada tópico de `AppHubConnections.Remove`. El barrido guarda por grupo la última lista entregada por el pod (en `AppHubDispatcher`). `MultiReplicaInventory`: `RealtimePresenceSweeper` `PerPod`; `LocalRealtimePresence` `SharedWithLocalFallback` con la variable `Valkey__Areas__Presence`.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(realtime): share ticket composing presence per connection`.

**Cierre del Bloque A:** una corrida por clase: `AppHubSupportTests`, `SupportRealtimeNoticesApiTests`, `AppHubPresenceTests` (`$SI`, una a la vez), `Realtime` y `ValkeyRealtimePresenceTests` (`$BU`), `MultiReplicaInventoryTests` y `SupportLayerTests` (`$AT`).

**Revisión del Bloque A** (un revisor): N1–N14 y N24, Review Focus 1–3, que `AppHub` no lea nunca un tenant del cliente, que ningún aviso salga antes del commit, y el inventario de réplicas.

## Bloque B — Notas internas (Tareas 4–5, un implementador)

### Task 4: Crear y leer notas (texto)

**Files:**
- Create: `Modules.Support.Domain/SupportTicketNote.cs`, `SupportTicketNoteFile.cs`; `Modules.Support.Application/ISupportTicketNoteRepository.cs`, `Notes/AddTicketNote.cs`, `Notes/ListTicketNotes.cs`; `Modules.Support.Infrastructure/Persistence/SupportTicketNoteRepository.cs`; migración `AddSupportTicketNotes`
- Modify: `SupportErrorCodes.cs` (`NoteTextInvalid`, `NoteTooManyFiles`, `NoteFileInvalid`, `NoteTicketClosed`), `SupportDtos.cs` (DTOs del contrato), `SupportAuditActions.cs` (`TicketNoteAdded`), `SupportDbContext.cs`, `SupportUnitOfWork.cs` (`PK_ticket_notes` → 409, `IX_ticket_note_files_file` → `file_invalid`), `SupportInfrastructureExtensions.cs`, `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs` (handlers y validadores a mano), `src/Bootstrapper/SupportUserReferenceProbe.cs` (N23)
- Test: `$SU\SupportTicketNoteTests.cs`, `$SU\AddTicketNoteValidatorTests.cs`, `$SI\SupportTicketNotesApiTests.cs`, `$SI\SupportRealtimeNoticesApiTests.cs` (+1), `$SI\SupportUserReferenceProbeTests.cs` (+1)

**Interfaces:**
- Consumes: Task 2 (`ISupportRealtime`).
- Produces:
  ```csharp
  public sealed class SupportTicketNote {
      public const int MaxFiles = 5;
      public static SupportTicketNote Create(Guid id, Guid tenantId, SupportTicketId ticketId, Guid authorMemberId, string text,
          IReadOnlyList<SupportNoteFileSnapshot> files, DateTimeOffset now);   // texto: SupportTicketReply.IsValidText; si no, NoteTextInvalid;
                                                                                 // > MaxFiles o repetidos: NoteTooManyFiles
      Guid Id; Guid TenantId; SupportTicketId TicketId; Guid AuthorMemberId; string Text; DateTimeOffset CreatedAt;
      IReadOnlyList<SupportTicketNoteFile> Files;                                // por Position
  }
  public sealed record SupportNoteFileSnapshot(Guid FileId, string FileName, string MimeType, long SizeBytes);
  public sealed class SupportTicketNoteFile { Guid NoteId; Guid FileId; int Position; string FileName; string MimeType; long SizeBytes; }
  public interface ISupportTicketNoteRepository {
      void Add(SupportTicketNote note);
      Task<SupportTicketNote?> FindAsync(Guid tenantId, Guid noteId, CancellationToken ct);                       // sin tracking
      Task<IReadOnlyList<SupportTicketNote>> ListAsync(Guid tenantId, SupportTicketId ticketId,
          (DateTimeOffset CreatedAt, Guid Id)? before, int take, CancellationToken ct);                         // DESC, take + 1 para saber si hay más
  }
  public sealed record AddTicketNoteCommand(Guid TenantId, Guid TicketId, Guid? ClientId, string? Text, IReadOnlyList<Guid>? FileIds)
      : ICommand<SupportTicketNoteDto>;
  public sealed record ListTicketNotesQuery(Guid TenantId, Guid TicketId, string? Before, int Take) : IQuery<SupportTicketNotesDto>
      { public const int DefaultTake = 50; public const int MaxTake = 100; }
  ```
  Tablas: `support.ticket_notes (id PK, tenant_id, ticket_id FK → tickets ON DELETE CASCADE, author_member_id, text varchar(4096), created_at)`, índice `IX_ticket_notes_tenant_ticket_created (tenant_id, ticket_id, created_at DESC, id DESC)`; `support.ticket_note_files (note_id FK → ticket_notes ON DELETE CASCADE, file_id, position, file_name varchar(260), mime_type varchar(100), size_bytes bigint CHECK > 0)`, PK `(note_id, file_id)`, único `IX_ticket_note_files_file (file_id)`, `CHECK (position BETWEEN 0 AND 4)`. Migración aditiva.

- [ ] **Paso 1: pruebas**
  - `SupportTicketNoteTests`: `CreateTrimsTheText`; `CreateRejectsEmptyTooLongOrControlCharacters` (`""`, `"   "`, 4 097, `"a\u0007"` → `support.note.text_invalid`; `"a\nb\tc"` pasa); `CreateRejectsSixFilesOrARepeatedOne` (`support.note.too_many_files`); `FilesKeepTheirOrder`.
  - `AddTicketNoteValidatorTests`: `clientId` nulo o vacío; texto inválido → error en `Text`.
  - `SupportTicketNotesApiTests` (`SharedSupportHost`; cada prueba siembra su tenant):
    - `AManagerAddsANoteAndReadsItBack` — `201`, `Author.Id` = membresía de quien llama, `Files = []`; `GET` la devuelve primera.
    - `TheSameClientIdReturnsTheSameNote` — dos `POST` iguales → un solo registro, mismo `Id` y `CreatedAt`.
    - `TheSameClientIdOnAnotherTicketIs409` (N15).
    - `AClosedTicketRejectsANewNoteButReplaysAnOldOne` — `422 support.note.ticket_closed`; el reintento de una nota creada antes de cerrar → `201`.
    - `ANoteDoesNotBumpTheVersionNorWriteTheTrace` — `version` y número de `Events` del detalle iguales; `PUT /status` con la versión de antes → `200`.
    - `TheConsultNeverShowsANote` — ticket del formulario público con nota; `POST …/public/support/{slug}/tickets/lookup` → el cuerpo no contiene el texto de la nota.
    - `ANoteAnnouncesTheTicket` — va en `SupportRealtimeNoticesApiTests` (Tarea 2), que ya tiene el host con `RecordingRealtimePublisher`: dos avisos.
    - `NotesPageNewestFirstWithACursor` — 3 notas, `take=2` → 2 y `Next`; con `before` → la tercera, `Next = null`; `take=0` y `take=101` → `422 validation.failed`.
    - `AReaderListsButCannotAdd` — `CreateReader`: `GET 200`, `POST 403`; otro tenant en la ruta → `403`; ticket ajeno → `404`.
    - `AnAuditEntryIsWritten` — `CountAuditEntriesAsync(…, "support.ticket.note_added", ticketId) == 1`.
  - `SupportUserReferenceProbeTests.ANoteAuthorKeepsTheUser`.
- [ ] **Paso 2: RED** — `dotnet build $SU` y `$SI` fallan por `SupportTicketNote` y `AddTicketNoteCommand` inexistentes.
- [ ] **Paso 3: implementar** — orden del handler: `EnsureAsync(Manage)` → membresía activa (403) → validador → ticket (404) → nota existente por `clientId` (N15) → `Closed` (422) → archivos (en esta tarea sólo `FileIds` vacío o nulo; con alguno, `support.note.file_invalid` hasta la Tarea 5) → `Add` + auditoría → `SaveChangesAsync` → `ISupportRealtime.TicketChangedAsync` → `201` (`Results.Created` a la ruta de notas). La nota **no** pasa por el agregado `SupportTicket` (no toca `version`). Autor por `ISupportMemberDirectory.NamesAsync`.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre; `dotnet test $SU --filter "FullyQualifiedName~SupportTicketNote"`.
- [ ] **Paso 5: commit** — `feat(support): add internal notes to tickets`.

### Task 5: Archivos de las notas

**Files:**
- Create: `Modules.Support.Domain/SupportNoteUploads.cs`; `Modules.Support.Application/Notes/OpenNoteAttachment.cs`, `CompleteNoteAttachment.cs`, `GetNoteFileDownload.cs`
- Modify: `Notes/AddTicketNote.cs` (archivos), `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`, `Persistence/SupportTicketAttachmentRepository.cs` (`SupportAttachmentReferenceProbe` mira también `ticket_note_files`), `src/Bootstrapper/SupportAttachmentStorage.cs` (N20), `README.md` (§ Soporte y § Tiempo real), `CLAUDE.md` (Gotchas)
- Test: `$SU\SupportNoteUploadsTests.cs`, `$SI\SupportTicketNoteFilesApiTests.cs`

**Interfaces:**
- Consumes: Task 4; `ISupportAttachmentStorage` (fase 3, sin cambios de firma: el `uploadKey` es el `OwnerKey` de N18).
- Produces:
  ```csharp
  public static class SupportNoteUploads { public static Guid OwnerKey(SupportTicketId ticketId, Guid memberId); }   // N18, determinista
  public sealed record OpenNoteAttachmentCommand(Guid TenantId, Guid TicketId, string? Name, string? MimeType, long? SizeBytes)
      : ICommand<SupportNoteAttachmentSessionDto>;
  public sealed record CompleteNoteAttachmentCommand(Guid TenantId, Guid TicketId, Guid FileId) : ICommand<SupportNoteAttachmentStatusDto>;
  public sealed record GetNoteFileDownloadCommand(Guid TenantId, Guid TicketId, Guid NoteId, Guid FileId) : ICommand<SupportAttachmentDownloadDto>;
  ```

- [ ] **Paso 1: pruebas**
  - `SupportNoteUploadsTests`: `TheKeyIsStablePerTicketAndMember`; `AnotherMemberOrTicketGetsAnotherKey`.
  - `SupportTicketNoteFilesApiTests` (fixture de clase calcada de `SupportPublicAttachmentsHost`: Storage real con `IObjectStorage` reemplazado por `InMemorySupportObjectStorage`, donde la prueba deja el objeto con `Put` en lugar del `PUT` del navegador):
    - `ANoteKeepsItsFilesInOrder` — abrir, `Put` en el bucket en memoria, completar dos archivos, crear con `fileIds: [b, a]` → `Files` en ese orden, con nombre, tipo y tamaño.
    - `SixFilesAreRejected` → `422 support.note.too_many_files`; `ARepeatedFileIsRejected` → `too_many_files`.
    - `AFileOfAnotherMemberOrTicketIsInvalid` — subido por B o para otro ticket → `422 support.note.file_invalid`.
    - `APendingOrRejectedFileIsInvalid`.
    - `TheSameFileCannotBeInTwoNotes` → `file_invalid`.
    - `APublicUploadCannotBeUsedInANoteAndViceVersa`.
    - `OpeningOnAClosedTicketIs422` (`support.note.ticket_closed`); `OpeningAnExeIs422ValidationFailed` (`errors.name`); `CompletingBeforeThePutIs428`.
    - `TheDownloadNeedsTicketViewAndChecksTheNote` — lector `200 { url }`; nota de otro ticket o archivo de otra nota → `404 support.attachment.not_found`; otro tenant → `403`.
    - `NoteFilesDoNotCountAsPendingPublicUploads` — tras reclamar 2 archivos en notas, `CountPendingAsync` del tenant no los cuenta.
    - `TheReferenceProbeKeepsANoteFile` — `SupportAttachmentReferenceProbe.HasReferencesAsync(fileDeNota)` es `true`.
- [ ] **Paso 2: RED** — `dotnet build $SU` falla por `SupportNoteUploads` inexistente; `$SI` por `OpenNoteAttachmentCommand`.
- [ ] **Paso 3: implementar** (N17–N21). Abrir y completar: `EnsureAsync(Manage)` → membresía → validador (las mismas reglas de `OpenPublicAttachmentValidator`, sin el tope `attachments_busy`) → ticket (404) → `Closed` (422) → `ISupportAttachmentStorage` con `OwnerKey(ticket, actor)`; un `SupportDomainException` de `OpenAsync` se vuelve `support.note.file_invalid`. Crear: cada `fileId` por `FindAvailableAsync(tenant, fileId, OwnerKey(ticket, actor))`; nulo → `file_invalid`; la foto va a `SupportNoteFileSnapshot`. Docs: README (rutas de notas, el hub `/api/v1/hubs/app`, sus métodos, eventos y tópicos, `qep_realtime`, la clave de Valkey) y `CLAUDE.md` (Gotchas): el hub vive en Bootstrapper y no es de nadie; un módulo nuevo se suma con su `IRealtimeTopicAuthorizer`; los avisos se llaman a mano después del commit (N13) y la fusión/borrado de clientes no avisan; la presencia es por conexión y la barre `RealtimePresenceSweeper`; `ticket_note_files` entra en el probe de archivos y en `CountPendingAsync`; `ticket_notes.author_member_id` en `SupportUserReferenceProbe`; el `OwnerKey` de las notas.
- [ ] **Paso 4: GREEN** — cada prueba filtrada por nombre.
- [ ] **Paso 5: commit** — `feat(support): attach files to internal notes`.

**Cierre del Bloque B:** una corrida por clase: `SupportTicketNotesApiTests`, `SupportTicketNoteFilesApiTests`, `SupportUserReferenceProbeTests`, `SupportPublicAttachmentsApiTests` (N20 la toca) en `$SI`, una a la vez; `SupportTicketNote` y `SupportNoteUploads` en `$SU`.

**Revisión del Bloque B** (un revisor): N15–N23, Review Focus 4–5, que ninguna nota salga por `public/support`, y que el formulario público siga igual.

**Antes de mergear a `develop`:** suites completas de `$SU`, `$SI`, `$BU`, `$AT`, una a la vez, con `-- xUnit.MaxParallelThreads=4` en las de integración; `dotnet format --verify-no-changes`; `dotnet build` limpio.

**Revisión final** (modelo más capaz, toda la rama contra `develop`): arquitectura (`SupportLayerTests`, BuildingBlocks sin módulos, Mensajería intacta), aislamiento de tenant en el hub, avisos sólo después del commit, presencia sin fantasmas y notas sin fuga al solicitante.

## Decisiones del owner (resueltas 2026-11-15)

**Las cinco quedan aprobadas tal como el plan las asume.** O3: el código pasa a `realtime.topic_forbidden` cuando un segundo
módulo use el hub, no antes.

- **O1 (N15, N19, N21):** el choque de `clientId` usa `409 concurrency.conflict`; completar antes del `PUT` usa `428 support.public.attachment_not_uploaded`; nota o archivo inexistente en la descarga usa `404 support.attachment.not_found`. Ninguno es un código nuevo; si prefieres códigos `support.note.*`, hay que sumarlos al spec y al frontend.
- **O2 (N17):** `ticket_note_files` guarda la foto del archivo (nombre, tipo, tamaño), columnas que §5.4 no lista.
- **O3 (N3, N5):** un tópico de un prefijo sin autorizador, un tópico mal formado, el tope de 50 tópicos y un `state` desconocido responden el mismo `support.realtime.topic_forbidden`, aunque el tópico no sea de Soporte. Cuando haya un segundo módulo en el hub, conviene un código genérico (`realtime.topic_forbidden`) que hoy el spec no tiene.
- **O4 (N18):** «subido por el mismo miembro» se cumple con una clave de dueño derivada de ticket y miembro, no con el id del ticket como dice §5.4.
- **O5 (N14):** la fusión y el borrado de clientes no avisan por el hub.

## Seguimientos aceptados (revisión final, no se implementan en esta rama)

- **N1 — Carrera de la foto de presencia al suscribirse:** `AppHub.Subscribe` se une al grupo y después lee la foto que manda a la
  conexión (N6); un `presenceChanged` más nuevo que llegue en medio puede quedar pisado por esa foto hasta el siguiente cambio.
- **N2 — Sin puesta al día tras re-`LISTEN`:** cuando `RealtimeEventsListener` pierde la conexión y vuelve a `LISTEN`, los avisos de ese
  hueco no se reentregan. La pantalla se corrige al siguiente aviso o al releer.
- **N3 — Un acceso revocado sigue recibiendo hasta reconectar:** los tópicos se autorizan al suscribirse; quitar el permiso o la
  membresía no saca a una conexión ya suscrita. Mismo comportamiento que `MessagingHub`.
- **N7 — Un `NOTIFY` de más de 8 000 bytes sólo se loguea:** `pg_notify` lo rechaza, `PgRealtimePublisher` lo registra como `Warning`
  y el aviso no sale.
  En la práctica sólo lo alcanza una presencia con más de ~60 miembros escribiendo a la vez (N8).

## Spec discrepancies (spec contra código)

1. §5.1 dice `/hubs/app`; el tenant por query sólo existe bajo `/api/v1/hubs` (`TenantRequestResolver.cs:18`). El plan usa `/api/v1/hubs/app` (N1).
2. §5.1 pide «Bootstrapper suma la referencia al framework de ASP.NET Core si le falta»: ya la tiene (`Bootstrapper.csproj`, `FrameworkReference Microsoft.AspNetCore.App`).
3. §5.4 remite a los «límites de `SupportPublicAttachmentRules` (10 MB, tipos de `FileUploadPolicy`)»: hoy esa clase acepta sólo PDF, JPEG, PNG y WebP (owner, fase 3: Word y Excel fuera mientras ClamAV esté apagado) y tope de 3 archivos para el formulario. Las notas usan sus tipos y tamaño, con tope propio de 5 (N19).
4. §5.4 cita `SupportAttachmentReferenceProbe.cs` como archivo de Bootstrapper: la clase vive en `Modules.Support.Infrastructure/Persistence/SupportTicketAttachmentRepository.cs:34`.
