# Avisos de tareas asignadas de Soporte — Plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de `qep/CLAUDE.md` («Presupuesto de pruebas y ejecución de planes») mandan sobre el proceso del skill:** un implementador por bloque, una revisión por bloque, una revisión fuerte al final.

**Goal:** que el miembro al que le asignan una tarea interna de Soporte se entere: entrada en la campana al instante, WhatsApp si no la leyó en 30 minutos y correo si WhatsApp no se puede, con un celular opcional por membresía (propio o tomado de RR. HH.).

**Architecture:** Soporte ya publica `support.ticket.task-assigned.v1`; un consumidor de la campana (Notifications) crea la entrada `SupportTaskAssigned` y, en la misma transacción, una fila de escalada con vencimiento. Un worker con lease reclama las filas vencidas, vuelve a comprobar al enviar (entrada leída/resuelta, tarea abierta y del mismo miembro) por puertos angostos implementados en `src/Bootstrapper`, y elige WhatsApp (plantilla de sistema `TaskNotice` por la conexión elegida en `/settings/support`) o correo. El celular vive en la membresía (Tenancy) y el de RR. HH. se resuelve al leer por un puerto de Bootstrapper.

**Tech Stack:** .NET 10, EF Core + Npgsql (migraciones expand-only), FluentValidation, xUnit v3 + Testcontainers, SignalR (`IRealtimeMemberPublisher`), Meta WhatsApp Cloud (vía Mensajería).

**Spec:** `docs/superpowers/specs/2026-11-22-support-task-notices-design.md` (contrato). Contexto: `2026-11-22-support-internal-tasks-design.md`, `2026-11-22-in-app-notifications-backend-design.md` y su plan `docs/superpowers/plans/2026-11-22-in-app-notifications-backend.md` (ya implementado: el código manda). Spec par del frontend: `qep-frontend/docs/superpowers/specs/2026-11-22-support-task-notices-design.md`.

## Dónde se trabaja

Worktree `qep-backend-worktrees/support-task-notices`, rama `feature/support-task-notices` (desde `develop`, ya con tareas internas y campana). Nunca en `main` ni `develop`. Antes de tocar un archivo y antes de cada commit: `git branch --show-current` debe decir `feature/support-task-notices`.

## Global Constraints

- Plan y copy en español neutro tuteando, sin voseo; identificadores, comentarios y nombres de archivo en inglés.
- Conventional commits, **sin** atribución de IA ni `Co-Authored-By`. Un commit por tarea.
- TDD: RED antes que GREEN, con evidencia literal. RED por compilación = salida de `dotnet build` del proyecto de prueba, sin contenedores.
- Por bloque sólo: `dotnet build` + pruebas **unitarias** nuevas filtradas por nombre. Las de integración y `ArchitectureTests` se **escriben** en su tarea pero se corren **una sola vez** al final de la rama (sección «Cierre»). Antes de cualquier corrida pesada: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay un `testhost` de otra sesión, se espera (nunca se detiene). Pruebas en primer plano.
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`).
- Migraciones expand-only: columnas nullable o con default, índices y tablas nuevas; nada se borra ni se estrecha. Se generan con `dotnet ef migrations add <Nombre> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations` (genera archivos; **no** se aplica a ninguna base a mano) y el snapshot se commitea con la migración.
- Todo hosted service nuevo entra a `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` con evidencia «archivo:línea — mecanismo».
- Todo handler, puerto o adaptador nuevo se registra a mano en `src/Bootstrapper/QepServiceCollectionExtensions.cs` (o en el `…InfrastructureExtensions.cs` de su módulo si es del módulo).
- Cruces Notifications ↔ Support ↔ Tenancy ↔ Hr ↔ Messaging ↔ Integrations **sólo** por puertos angostos implementados en `src/Bootstrapper` (patrón `WholesaleInvitationNoticeSender`, `WholesaleWhatsAppConnections`, `InAppNotificationNames`). `Application` sin EF Core ni Npgsql.
- Ancho del `kind` de plantilla: `messaging.message_templates.kind` es `varchar(32)` (`MessageTemplate.KindMaxLength = 32`, `MessageTemplate.cs:177`; snapshot línea 838). `TaskNotice` mide 10. `notifications.in_app_notifications.kind` es `varchar(64)`; `SupportTaskAssigned` mide 19.
- E.164 del celular: `^\+[1-9]\d{7,14}$` (misma regla de `TicketRequesterNormalizer.IsMobile`, `CreateTicket.cs:165-175`). Columna `phone_e164 varchar(16)`.
- Espera de escalada: `Notifications:TaskNotice:EscalationAfterMinutes`, default **30**, global (D-4).
- El mensaje de WhatsApp **nunca** lleva asunto ni descripción. El correo sí puede llevar el asunto.
- Un aviso fallido nunca deshace ni retrasa la asignación; una escalada, un canal; nunca dos canales.
- `docs/estado.md` **no** se toca en la rama (se actualiza en `develop` al mergear).

## Review Focus

1. **Reasignar en la misma transacción** (A→B): el evento `task-assigned` de B y el `assignee-released` de A llevan el mismo `occurred_at`; resolver por `kind + resource` sin miembro cerraría también la entrada nueva de B. Esperado: sólo se resuelven las de A. Prueba: `ReassigningResolvesOnlyTheFormerAssigneesEntry` (Task 9).
2. **Dos réplicas del worker** sobre la misma fila vencida: esperado un solo envío. Prueba: `TwoConcurrentRunsSendOnce` (Task 12), más `ClientId` = id de la escalada en WhatsApp.
3. **Teléfono de RR. HH. raro** (`"300 123 4567"`, `"0313…"`, vacío, otro país): esperado sólo se usa si normaliza con certeza al E.164 del país del tenant; si no, `null` y correo. Pruebas: `MemberPhoneDirectoryTests` (Task 2).
4. **Miembro con QEP abierto** (spec D-1): esperado sin WhatsApp ni correo mientras siga conectado, aunque no lea; escala en cuanto se desconecta; una caída de Valkey o de la presencia escala (nunca calla). Pruebas: `AConnectedMemberIsDeferredAMinuteWithoutSending`, `StayingConnectedNeverExhaustsTheAttempts`, `EscalatesOnceTheMemberDisconnects`, `APresenceFailureEscalates` (Task 12), `ClosingOneOfTwoTabsKeepsItConnected` (Task 11b).
5. **Frontend viejo** que manda `PUT …/profile` sin `phone`, o `PUT …/support/settings` sin `taskNotice`: esperado conserva lo guardado. Pruebas: `ProfilePutWithoutPhoneKeepsIt` (Task 3), `PutWithoutTaskNoticeKeepsTheConnection` (Task 7).
5. **Conexión elegida que después se pausa o pierde la plantilla**: esperado el worker cae a correo con el código en `channel_attempts`, y los ajustes siguen mostrando la elección (no se borra sola). Pruebas: `FallsBackToEmailWhenTemplateIsNotApproved` (Task 12), `TheChosenConnectionSurvivesWhenItIsNoLongerListed` (Task 7).

---

## Bloque 1 — Celular del miembro (Tenancy, RR. HH., Bootstrapper)

### Task 1: `phone_e164` en la membresía

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/MemberPhone.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs` (propiedad, `UpdateProfile` sobrecargado, `ChangeOwnPhone`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:308-360` (`ConfigureMembership`)
- Create: migración `AddMembershipPhone` (+ snapshot)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipPhoneTests.cs`

**Interfaces:**
- Produces:
  - `public static class MemberPhone { public const int MaxLength = 16; public static string? Normalize(string? value); public static bool IsValid(string value); }` — recorta; vacío → `null`; no cumple E.164 → `TenantDomainException("tenancy.membership.phone_invalid")`.
  - `public readonly record struct PhoneChange(bool Provided, string? Value) { public static PhoneChange Keep { get; } public static PhoneChange Set(string? value); }`
  - `Membership.PhoneE164 { get; private set; }` (`string?`).
  - `public MembershipProfileChanges UpdateProfile(string displayName, int? advisorCode, PhoneChange phone, DateTimeOffset occurredAt)`; `public readonly record struct MembershipProfileChanges(bool Profile, bool Phone) { public bool Any; }`. El `bool UpdateProfile(string, int?, DateTimeOffset)` existente queda y delega con `PhoneChange.Keep` (sus pruebas no cambian).
  - `public bool ChangeOwnPhone(string? phoneE164, DateTimeOffset occurredAt)`.
  - Ambos suben `Version` **una vez** si algo cambió, y no la tocan si nada cambió. Valen en cualquier estado (como el perfil).

- [ ] **Step 1: Write the failing test** — `MembershipPhoneTests`:
  - `NormalizeTrimsAndKeepsAValidE164`: `MemberPhone.Normalize(" +573001234567 ")` == `"+573001234567"`.
  - `NormalizeTurnsBlankIntoNull`: `""`, `"  "`, `null` → `null`.
  - `NormalizeRejectsWhatIsNotE164` (Theory: `"3001234567"`, `"+0573001234567"`, `"+57 300 123 4567"`, `"+1234567"`, `"+1234567890123456"`): lanza `TenantDomainException` con `Code == "tenancy.membership.phone_invalid"`.
  - `UpdateProfileWithAPhoneBumpsTheVersionOnce`: nombre igual, teléfono nuevo → `Changes.Phone == true`, `Profile == false`, `Version` +1.
  - `UpdateProfileWithNameAndPhoneBumpsTheVersionOnce`: ambos cambian → `Version` +1.
  - `UpdateProfileKeepingThePhoneLeavesIt`: `PhoneChange.Keep` no toca `PhoneE164`.
  - `UpdateProfileWithNullPhoneClearsIt`.
  - `ChangeOwnPhoneWithTheSameValueChangesNothing`: devuelve `false`, `Version` igual.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests`. Expected: no compila (`MemberPhone`, `PhoneChange` no existen).
- [ ] **Step 3: Implement** lo de *Produces*. Mapear en `ConfigureMembership`: `membership.Property(value => value.PhoneE164).HasColumnName("phone_e164").HasMaxLength(MemberPhone.MaxLength);` (nullable, sin índice). Generar `AddMembershipPhone` con el comando de Global Constraints (`--context TenancyDbContext`); revisar que el `Up` sólo agregue la columna nullable.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipPhoneTests|FullyQualifiedName~MembershipTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `git add src/Modules/Tenancy tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipPhoneTests.cs; git commit -m "feat(tenancy): optional notice phone on the membership"`

### Task 2: Teléfono efectivo (propio o de RR. HH.)

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/IHrMemberPhones.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/HrMemberPhones.cs`; Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/HrInfrastructureExtensions.cs:31` (registro)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/MemberPhones.cs` (`IMemberPhoneDirectory`, `MemberPhoneSource`, `EffectivePhone`, `MembershipPhoneReader`)
- Create: `src/Bootstrapper/MemberPhoneDirectory.cs`; Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `HrPhoneNormalizer`, línea ~601)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipPhoneReaderTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/MemberPhoneDirectoryTests.cs`, `tests/Modules/Hr/Modules.Hr.IntegrationTests/HrMemberPhonesTests.cs`

**Interfaces:**
- Consumes: `Membership.PhoneE164` (Task 1); `IPhoneNumberNormalizer.ToE164(string?, string country)` (`Modules.Customers.Domain`); `ITenantModules.FindAsync` + `TenantModuleSet.IsEnabled(TenantModuleKeys.Hr)`; `ITenantDirectory.GetRegionalSettingsAsync(...).Country`.
- Produces:
  - `Modules.Hr.Application`: `public sealed record HrMemberPhone(Guid MemberId, string Phone);` `public interface IHrMemberPhones { Task<IReadOnlyList<HrMemberPhone>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken); }` — sólo empleados con `MemberId` en la lista, `Status == EmployeeStatus.Active` y `Phone` no vacío; `AsNoTracking`.
  - `Modules.Tenancy.Application`: `public enum MemberPhoneSource { Member, Hr }`; `public sealed record EffectivePhone(string? Phone, MemberPhoneSource? Source) { public static EffectivePhone None; public string? SourceName => Source?.ToString(); }`; `public interface IMemberPhoneDirectory { Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken); }` (devuelve **sólo** E.164 ya normalizados); `public sealed class MembershipPhoneReader(IMemberPhoneDirectory directory) { Task<EffectivePhone> ForAsync(Membership membership, CancellationToken ct); Task<IReadOnlyDictionary<MembershipId, EffectivePhone>> ForAsync(IReadOnlyList<Membership> memberships, CancellationToken ct); }` — regla: `PhoneE164 ?? hr`; propio gana; sólo pregunta a RR. HH. por las que no tienen propio (ninguna sin propio ⇒ ninguna llamada).
  - Bootstrapper: `internal sealed class MemberPhoneDirectory(ITenantModules modules, ITenantDirectory tenants, IHrMemberPhones hr, IPhoneNumberNormalizer phones) : IMemberPhoneDirectory` — módulo `hr` apagado o tenant sin fila ⇒ vacío sin consultar HR; cada teléfono pasa por `phones.ToE164(phone, country)` y luego por `MemberPhone.IsValid`; lo que no normaliza se omite (nunca se adivina).

- [ ] **Step 1: Write the failing tests**
  - `MembershipPhoneReaderTests` (doble de `IMemberPhoneDirectory` que cuenta llamadas): `OwnPhoneWins` (propio y HR ⇒ propio, `Member`); `HrFillsWhenThereIsNoOwnPhone` (⇒ HR, `Hr`); `NeitherIsNone` (⇒ `EffectivePhone.None`, `SourceName == null`); `DoesNotAskHrWhenEveryMemberHasItsOwn` (0 llamadas); `ClearingTheOwnPhoneBringsBackHr`.
  - `MemberPhoneDirectoryTests` (dobles de los cuatro puertos; normalizador real `LibPhoneNumberNormalizer` no es accesible ⇒ doble que mapea `"3001234567"`→`"+573001234567"`, `"+573001234567"`→igual, resto→`null`): `HrModuleOffReturnsNothingAndDoesNotAskHr`; `NormalizesDigitsWithTheTenantCountry` (país `"CO"` llega al normalizador); `DropsWhatDoesNotNormalize` (`"300 abc"` ⇒ ausente); `DropsANormalizedValueThatIsNotE164` (normalizador devuelve `"+12"` ⇒ ausente).
  - `HrMemberPhonesTests` (integración, Testcontainers del módulo): `ListsOnlyActiveEmployeesWithPhoneAndMember` — siembra activo con teléfono, retirado con teléfono, activo sin teléfono, activo de otro tenant; sólo el primero vuelve.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests; if ($?) { dotnet build tests/Bootstrapper/Bootstrapper.UnitTests }`. Expected: no compila.
- [ ] **Step 3: Implement** lo de *Produces*; registrar `IHrMemberPhones` en `HrInfrastructureExtensions`, `IMemberPhoneDirectory → MemberPhoneDirectory` y `MembershipPhoneReader` (scoped) en `QepServiceCollectionExtensions`.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipPhoneReaderTests"; if ($?) { dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MemberPhoneDirectoryTests" }`. Expected: PASS. (`HrMemberPhonesTests` corre en el Cierre; aquí sólo compila: `dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests`.)
- [ ] **Step 5: Commit** — `feat(tenancy): effective member phone with the HR fallback`

### Task 3: API del celular (perfil propio, Miembros y lista)

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/MyMembership.cs` (`GetMyMembershipQuery`, `UpdateMyPhoneCommand`, validador, handlers, `MyMembershipDto`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberProfile.cs` (comando con `PhoneChange Phone`, validador, auditoría)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs` (`MembershipListItemDto` + `Phone`, `PhoneSource`; `ToListItemDto(email, tenant, EffectivePhone? phone = null)`; el handler llena el teléfono **sólo** si `executionContext.HasPermission(TenancyPermissions.AdvisorshipManage)`)
- Modify: `UpdateMemberRoles.cs`, `SuspendMember.cs`, `RemoveMember.cs`, `ReactivateMember.cs` (devuelven la fila con su teléfono: todos exigen `AdvisorshipManage`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` (rutas `GET /me`, `PUT /me/phone`; `MembershipProfileUpdateRequest` + `JsonElement Phone = default`; `MembershipListItemResponse` + `Phone`, `PhoneSource`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:193` (handlers y validador nuevos)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipProfileRequestTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipPhoneApiTests.cs`

**Interfaces:**
- Consumes: `PhoneChange`, `MemberPhone`, `Membership.UpdateProfile(…, PhoneChange, …)`, `ChangeOwnPhone` (Task 1); `MembershipPhoneReader` (Task 2).
- Produces (contrato HTTP):
  - `GET /api/v1/tenants/{tenantId}/memberships/me` — sólo sesión (sin permiso); tenant de la ruta ≠ el de la sesión o sin membresía **activa** ⇒ 403 `authorization.denied`. 200 `{ membershipId, phone, phoneSource }` (`phoneSource`: `"Member" | "Hr" | null`).
  - `PUT /api/v1/tenants/{tenantId}/memberships/me/phone` cuerpo `{ "phone": "+57…" | null }`, **sin** `If-Match` (dato personal, como marcar leída), mismas reglas de acceso. 200 con el mismo cuerpo del `GET`. 422 `validation.failed` con `errors.Phone` si no es E.164. Sube la `Version` de la membresía: un diálogo de Miembros abierto recibe 412 en su próximo `PUT …/profile` (correcto). Auditoría `tenancy.membership.phone_updated`, `changedFields: ["phone"]`, sin el número.
  - `PUT …/memberships/{id}/profile`: `phone` **ausente** ⇒ conserva; `null` ⇒ borra; texto ⇒ valida y guarda; otro tipo JSON ⇒ 422 `errors.Phone`. Mismo `If-Match`. Auditoría `tenancy.membership.profile_updated` con `changedFields` = `["phone"]` si cambió el celular, `[]` si no (hoy siempre `[]`, `UpdateMemberProfile.cs:94-102`).
  - `MembershipListItemResponse` suma `string? Phone, string? PhoneSource` (al final). Sin `advisorship.manage` ambos `null`.
  - Records: `public sealed record MyMembershipDto(Guid MembershipId, string? Phone, string? PhoneSource);` `public sealed record GetMyMembershipQuery(TenantId TenantId) : IQuery<MyMembershipDto>;` `public sealed record UpdateMyPhoneCommand(TenantId TenantId, string? Phone, string CorrelationId) : ICommand<MyMembershipDto>;` `UpdateMemberProfileCommand(…, PhoneChange Phone)` (nuevo parámetro antes de `ExpectedVersion`); `public sealed record MembershipProfileUpdateRequest(string? DisplayName, decimal? AdvisorCode, JsonElement Phone = default);` `public sealed record MyPhoneUpdateRequest(string? Phone);` `public static class PhoneInput { public static PhoneChange ToChange(JsonElement phone); }` (`Undefined`→`Keep`, `Null`→`Set(null)`, `String`→`Set(texto)`, otro ⇒ `PhoneChange` inválido que el validador rechaza).

- [ ] **Step 1: Write the failing tests**
  - `MembershipProfileRequestTests` (deserializa con `JsonSerializerOptions(JsonSerializerDefaults.Web)`): `AbsentPhoneIsKeep` (`{"displayName":"Ana"}` ⇒ `PhoneInput.ToChange(...) == PhoneChange.Keep`); `NullPhoneClears` (⇒ `Provided && Value is null`); `TextPhoneSets`.
  - `MembershipPhoneApiTests`:
    - `OwnPhoneRoundTrips`: `PUT /me/phone {"phone":"+573001234567"}` ⇒ 200 `phone`, `phoneSource == "Member"`; `GET /me` igual.
    - `OwnPhoneRejectsWhatIsNotE164`: `"3001234567"` ⇒ 422 con `errors.Phone`.
    - `OwnPhoneNullClears`.
    - `MeIsForbiddenForAnotherTenant`: ruta con otro tenant ⇒ 403.
    - `MeOnlyTouchesTheCallersMembership`: un miembro cambia el suyo; el del owner no cambia (lectura SQL de `tenancy.memberships.phone_e164`).
    - `AdminSetsThePhoneThroughTheProfilePut`: `PUT …/{id}/profile` con `If-Match` y `phone` ⇒ 200, `phone`, `phoneSource == "Member"`, `ETag` nuevo.
    - `ProfilePutWithoutPhoneKeepsIt`: cuerpo sin `phone` ⇒ el guardado sigue.
    - `ProfilePutWithNullPhoneClearsIt`.
    - `ProfilePutWithAStalePhoneVersionIs412`: el miembro cambia su celular por `/me/phone` y el admin manda la versión anterior ⇒ 412.
    - `ListShowsPhonesOnlyToWhoManagesMembers`: con `advisorship.manage` la fila trae `phone`; un usuario con `advisorship.read` sin `manage` ve `phone == null` y `phoneSource == null`.
    - `ListShowsTheHrPhoneWhenThereIsNoOwn`: con `IMemberPhoneDirectory` reemplazado en el host por un doble que devuelve `+573009998877` ⇒ `phoneSource == "Hr"`.
    - `PhoneChangeIsAuditedWithoutTheNumber`: la fila de auditoría nombra `phone` en `changed_fields` y su JSON no contiene el número.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests; if ($?) { dotnet build tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests }`. Expected: no compila.
- [ ] **Step 3: Implement** el contrato. Los handlers `/me` resuelven la membresía con `IMembershipRepository.FindByUserAndTenantAsync(executionContext.SubjectId, tenantId)` y exigen `State == Active`. Rutas en `MembershipEndpoints` con `.RequireAuthorization()` (sólo sesión), `.Produces<MyMembershipResponse>()`, 403 y 422.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipProfileRequestTests|FullyQualifiedName~ListMembershipsHandlerTests"`. Expected: PASS. `MembershipPhoneApiTests` sólo compila aquí.
- [ ] **Step 5: Commit** — `feat(tenancy): own and admin-managed notice phone in the membership API`

**Revisión del Bloque 1** (una, lente `review-risk`: datos personales y permisos).

---

## Bloque 2 — Plantilla de WhatsApp `TaskNotice` (Mensajería)

### Task 4: Tipo, aprovisionamiento y estado de la plantilla

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` (enum `TaskNotice` **al final**, `MessageTemplateName.TaskNoticeName = "qep_tarea_asignada"` y `TaskNoticePrefix = "qep_tarea_asignada_"` en `IsReserved`, `AdoptAsTaskNotice(DateTimeOffset)`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (`TemplateTaskNoticeExists = "messaging.template.task_notice_exists"`, `TemplateTaskNoticeConflict = "messaging.template.task_notice_conflict"`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateVariables.cs` (`TaskNotice`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TaskNoticeTemplateProvisioning.cs` (calco de `WholesaleInvitationNoticeTemplateProvisioning.cs`)
- Modify: `SystemTemplateDrafts.cs` (`GetTaskNoticeTemplateDraftQuery` + handler), `TemplateManagementPorts.cs` (`FindTaskNoticeAsync`, `ITaskNoticeTemplateReadiness`), `TemplateManagementDtos.cs` (`TaskNoticeTemplateStatusDto` y `TemplateManagementDto.TaskNoticeTemplate = null` al final), `TemplateManagement.cs:77-98`, `IMessagingAuditRecorder.cs` (`TemplateTaskNoticeProvisioned = "messaging.template.task_notice_provisioned"`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (CHECK `CK_message_templates_kind` con `'TaskNotice'`, índice `UX_message_templates_task_notice` filtrado `kind = 'TaskNotice'`), `MessagingSaveChanges.cs` (23505 del índice ⇒ `TemplateTaskNoticeExists`), `MessageTemplateRepository.cs`, `Persistence/TaskNoticeTemplateReadiness.cs` (nuevo), `MessagingInfrastructureExtensions.cs:151`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs` (`POST /task-notice`, `GET /task-notice/draft`, `ProvisionTaskNoticeTemplateRequest(string? Body)`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1593-1606`
- Create: migración Messaging `AddTaskNoticeTemplateKind` (+ snapshot), calco de `20261118145651_AddWholesaleInvitationNoticeTemplateKind.cs` con la lista vigente del CHECK (`…'WholesaleInvitationNotice','Invoice','TaskNotice'`) y un `Down` que borra las filas `TaskNotice` antes de restaurar el CHECK.
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TaskNoticeTemplateDefinitionTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TaskNoticeTemplateProvisioningApiTests.cs`

**Interfaces:**
- Produces:
  - `SystemTemplateVariables.TaskNotice` = las cuatro fijas + `new("numero", "Número de la tarea", "SOP-000127")`. `asesor` (fija) la llena Mensajería con el nombre de `SentByMemberId` (`SystemTemplateSend.cs:106-115`): por eso el envío manda como autor a quien asignó.
  - `public static class TaskNoticeTemplateDefinition { Name = MessageTemplateName.TaskNoticeName; Language = "es"; Category = "UTILITY"; NumberKey = "numero"; BaseBody = "¡Hola! {{asesor}} te asignó {{numero}} en QEP. Ábrelo para ver el detalle."; NameFor(int); VersionOf(string?); Draft(string name, string body); Payload(string name, string body); IsAdoptable(string componentsJson); }` — mismo comportamiento que su gemela mayorista (sólo cuerpo, sin encabezado ni botones, versiones `_vN`). El cuerpo base no empieza ni termina con variable (Meta lo rechaza). Ver P-1.
  - `ProvisionTaskNoticeTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>` + validador (cuerpo sólo con variables de `TaskNotice`) + handler (permiso `messaging.template.manage`, sin gate de módulo: Soporte no es requisito de Mensajería).
  - `public interface ITaskNoticeTemplateReadiness { Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken ct); Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken ct); }` — `FindStatusAsync` = estado crudo de Meta de la fila `TaskNotice` o `null` sin fila.
  - Rutas: `POST /api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management/task-notice` (201 `ManagedTemplateDto`; 409 `task_notice_exists`/`task_notice_conflict`) y `GET …/task-notice/draft` (`SystemTemplateDraftDto`).

- [ ] **Step 1: Write the failing tests**
  - `TaskNoticeTemplateDefinitionTests`: `BaseBodyUsesOnlyTaskNoticeVariables` (`TemplateBodyRules.Check(BaseBody, KeysOf(TaskNotice))` es `null`); `TheVariablesNeverIncludeSubjectOrDescription` (`KeysOf(TaskNotice)` no contiene ninguna clave que contenga `asunto`, `descripcion`, `subject`, `description`, y es exactamente `{nombre_contacto, asesor, empresa, saludo, numero}`); `BaseBodyDoesNotStartOrEndWithAVariable`; `NameForOneIsTheBareName` (`"qep_tarea_asignada"`) y `NameForTwoIsV2`; `VersionOfRejectsForeignNames` (`"qep_tarea_asignada_v02"`, `"otra"` ⇒ `null`); `TheReservedPrefixCoversTheName` (`MessageTemplateName.IsReserved("qep_tarea_asignada_v3")`); `TheKindFitsItsColumn` (`nameof(MessageTemplateKind.TaskNotice).Length <= MessageTemplate.KindMaxLength`).
  - `TaskNoticeTemplateProvisioningApiTests` (calco de `WholesaleInvitationNoticeTemplateProvisioningApiTests`): crea con texto base ⇒ 201, fila `kind = 'TaskNotice'`, `PENDING`; segunda vez ⇒ 409 `messaging.template.task_notice_exists`; sin `messaging.template.manage` ⇒ 403; cuerpo con `{{asunto}}` ⇒ 422 `errors.body`; la pantalla de plantillas trae `taskNoticeTemplate`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: no compila.
- [ ] **Step 3: Implement** lo de *Produces* y la migración. Antes de generarla: confirmar en el snapshot que `kind` sigue `character varying(32)`.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TaskNoticeTemplateDefinitionTests"`. Expected: PASS. La de integración compila (`dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`).
- [ ] **Step 5: Commit** — `feat(messaging): TaskNotice system template per connection`

### Task 5: Envío del aviso por la plantilla

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TaskNoticeOutbound.cs` (calco de `WholesaleInvitationNoticeOutbound.cs`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs:38`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TaskNoticeOutboundTests.cs` (calco de `InvoiceTemplateOutboundTests.cs` / dobles de `TemplateTestDoubles.cs`)

**Interfaces:**
- Consumes: `ApprovedTemplate` de `ITaskNoticeTemplateReadiness` (Task 4); `PlainTemplateOutbound`.
- Produces: `public sealed record TaskNoticeSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string WaId, string RecipientName, ApprovedTemplate Template, IReadOnlyDictionary<string, string> Values);` `public abstract record TaskNoticeSendResult { Sent(Guid MessageId, Guid ConversationId); ConnectionUnavailable(string? Reason); Failed(string Code); }` `public interface ITaskNoticeOutbound { Task<TaskNoticeSendResult> SendAsync(TaskNoticeSend send, CancellationToken ct); }` — sin `CustomerId`, `LegacyPositionalKeys: null`.

- [ ] **Step 1: Write the failing test** — `TaskNoticeOutboundTests`: `SendsTheNamedValuesWithTheSenderAsAuthor` (el `PlainTemplateSend` capturado lleva `ClientId`, `WaId`, `CustomerId == null`, valores `numero`); `TheSameClientIdTwiceIsOneMessage` (el segundo devuelve el mismo `MessageId`); `APausedConnectionIsConnectionUnavailable`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: no compila.
- [ ] **Step 3: Implement** `TaskNoticeOutbound` y su registro.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TaskNoticeOutboundTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(messaging): send the task notice template`

**Revisión del Bloque 2** (una, lente `review-reliability`).

---

## Bloque 3 — Soporte: liberación de la tarea, ajuste `taskNotice` y lector

### Task 6: Eventos `support.ticket.assigned.v1` (tickets de cliente) y `support.ticket.assignee-released.v1` (spec D-7)

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Application/ISupportIntegrationEvents.cs`, `src/Modules/Support/Modules.Support.Infrastructure/Persistence/SupportIntegrationEvents.cs`
- Modify: `src/Modules/Support/Modules.Support.Application/Tickets/AssignTicket.cs:64-78`, `TakeTicket.cs` (inyecta `ISupportIntegrationEvents`), `ChangeTicketStatus.cs:55-60`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportAssigneeEventsApiTests.cs`

**Interfaces:**
- Produces:
  - `void PublishTicketAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt);` — evento `support.ticket.assigned.v1`, payload **idéntico** al de `task-assigned` (`SupportIntegrationEvents.cs:66-85`: `ticketId, tenantId, ticketNumber, assigneeMemberId, assignedByMemberId`). `task-assigned` no cambia (tareas); `assigned` es sólo para tickets de cliente.
  - `void PublishAssigneeReleased(SupportTicket ticket, Guid formerAssigneeMemberId, string reason, DateTimeOffset occurredAt);` — evento `support.ticket.assignee-released.v1`, con `public static class SupportAssigneeReleaseReasons { Reassigned = "Reassigned"; Resolved = "Resolved"; Closed = "Closed"; }` en `Modules.Support.Application`. Payload camelCase **exacto**: `{ ticketId, tenantId, ticketNumber, formerAssigneeMemberId, reason, isInternal }`; correlación: el ticket.
- Reglas (sólo si la versión cambió):
  - Asignar un ticket **de cliente** a otro (`!ticket.IsInternal`, nuevo responsable no nulo y distinto de quien actúa) ⇒ `PublishTicketAssigned`. Asignarlo a sí mismo o dejarlo sin responsable no publica `assigned`. Las tareas siguen con `PublishTaskAssigned`, como hoy.
  - Liberación, para tareas **y** tickets de cliente:
    - Asignar: `formerAssignee is { } former && former != ticket.AssigneeMemberId` ⇒ `Reassigned` (también cuando el nuevo es quien actúa o queda sin responsable).
    - Tomar: `former is { } && former != actor` ⇒ `Reassigned`. Tomar nunca publica `assigned` (a uno mismo no se le avisa).
    - Cambiar estado: el estado anterior no era `Resolved` ni `Closed`, el nuevo es `Resolved` o `Closed`, y hay responsable ⇒ razón = nombre del estado nuevo. Se publica antes de `SaveIfChangedAsync` en las dos ramas del handler, así viaja en la misma transacción.

- [ ] **Step 1: Write the failing test** — `SupportAssigneeEventsApiTests` (lee `platform.outbox_messages` como `SupportTaskRulesApiTests.cs:71`): `AssigningACustomerTicketToSomeoneElsePublishesAssigned` (payload con `assigneeMemberId`, `assignedByMemberId`, ningún `task-assigned`); `AssigningACustomerTicketToOneselfPublishesNoAssigned`; `UnassigningACustomerTicketPublishesNoAssigned`; `TakingACustomerTicketPublishesNoAssigned`; `AssigningATaskStillPublishesTaskAssignedOnly`; `ReassigningATaskReleasesTheFormerAssignee` (`assignee-released` con A, `reason = "Reassigned"`, `isInternal = true`, y un `task-assigned` para B); `ReassigningACustomerTicketReleasesTheFormerAssignee` (`isInternal = false`, y un `assigned` para B); `TakingSomeoneElsesTicketReleasesThem`; `ResolvingATicketReleasesItsAssignee` (`reason = "Resolved"`); `ClosingAResolvedTicketPublishesNothingNew`; `SavingTheSameAssigneePublishesNothing`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Expected: no compila (`PublishAssigneeReleased` y `SupportAssigneeReleaseReasons` no existen).
- [ ] **Step 3: Implement** las reglas.
- [ ] **Step 4: Verify** — `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Expected: compila. La prueba de integración corre en el Cierre.
- [ ] **Step 5: Commit** — `feat(support): publish ticket assignment and release events`

### Task 7: Ajuste `taskNotice` en `/settings/support`

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTenantSettings.cs` (`TaskNoticeConnectionId`, parámetro nuevo de `Update`, `SupportTenantSettingsChanges.TaskNotice`)
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportErrorCodes.cs` (`TaskNoticeConnectionInvalid = "support.settings.task_notice_connection_invalid"`)
- Create: `src/Modules/Support/Modules.Support.Application/ISupportTaskNoticeChannels.cs`
- Modify: `src/Modules/Support/Modules.Support.Application/SupportDtos.cs:31-41,570-581`, `Settings/SupportSettingsComposer.cs`, `Settings/UpdateSupportSettings.cs`
- Modify: `src/Modules/Support/Modules.Support.Infrastructure/Persistence/SupportDbContext.cs:484-530` (columna `task_notice_connection_id uuid null`)
- Create: migración Support `AddTaskNoticeConnection` (+ snapshot)
- Create: `src/Bootstrapper/SupportTaskNoticeChannels.cs`; Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:2266-2290`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsOptions.cs` y `NotificationsOptionsValidator.cs` (`TaskNoticeOptions`, ver la nota al final del bloque)
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTenantSettingsTests.cs` (casos nuevos), `tests/Bootstrapper/Bootstrapper.UnitTests/SupportTaskNoticeChannelsTests.cs`, `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskNoticeSettingsApiTests.cs`, `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsOptionsValidatorTests.cs` (`RejectsAnEscalationOutsideOneMinuteToOneDay`: 0 y 1441 inválidos, 1 y 1440 válidos)

**Interfaces:**
- Consumes: `ITaskNoticeTemplateReadiness.FindStatusAsync` (Task 4); `IIntegrationConnections.ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, ct)` y `WhatsAppCloudFieldKeys.DisplayPhoneNumber` (como `WholesaleWhatsAppConnections.cs:20-41`); `IOptions<NotificationsOptions>` (Task 9 agrega `TaskNotice.EscalationAfterMinutes`; hasta entonces el adaptador usa la constante `TaskNoticeOptions.DefaultEscalationAfterMinutes` — ver orden de bloques abajo).
- Produces:
  - `SupportTenantSettings.TaskNoticeConnectionId { get; private set; }` (`Guid?`); `Update(int, string?, bool, int, SupportUnassignedAlertSettings, Guid? taskNoticeConnectionId, DateTimeOffset now)`; los tres atajos (`ChangeDueSoon`, `ChangeSurvey`, `ChangeFormalNextStep`) pasan `TaskNoticeConnectionId`.
  - `public sealed record SupportTaskNoticeConnection(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);` `public static class SupportTaskNoticeTemplateStatuses { Approved; Pending; Rejected; Missing; }` `public interface ISupportTaskNoticeChannels { Task<IReadOnlyList<SupportTaskNoticeConnection>> ListAsync(Guid tenantId, CancellationToken ct); int EscalationAfterMinutes { get; } }`.
  - DTO: `public sealed record SupportTaskNoticeDto(Guid? WhatsAppConnectionId, IReadOnlyList<SupportTaskNoticeConnectionDto> Connections, int EscalationAfterMinutes);` `public sealed record SupportTaskNoticeConnectionDto(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);` `SupportSettingsDto` suma `SupportTaskNoticeDto TaskNotice` al final; `SupportDtoMapping.ToDto(settings, unassignedAlert, taskNotice)`.
  - Request: `UpdateSupportSettingsRequest(…, TaskNoticeRequest? TaskNotice = null)`; `public sealed record TaskNoticeRequest(Guid? WhatsAppConnectionId);` comando con `TaskNoticeInput? TaskNotice` (`public sealed record TaskNoticeInput(Guid? WhatsAppConnectionId);`). Ausente o `null` ⇒ conserva. Con id: debe estar en `ListAsync` ⇒ si no, `SupportDomainException(TaskNoticeConnectionInvalid)` (422). Sin plantilla aprobada se acepta. Auditoría nombra `taskNotice`.
  - Mapeo de `TemplateStatus` en `SupportTaskNoticeChannels`: `null` ⇒ `Missing`; `APPROVED` ⇒ `Approved`; `MessageTemplateStatuses.IsLive(s)` (PENDING, IN_APPEAL) ⇒ `Pending`; cualquier otro ⇒ `Rejected`. Orden de la lista: por `Name` (invariante) y luego id.

- [ ] **Step 1: Write the failing tests**
  - `SupportTenantSettingsTests`: `ChangingTheTaskNoticeConnectionBumpsTheVersionOnce`; `TheSameConnectionChangesNothing`; `ClearingTheConnectionIsAChange`; `TheShortcutsKeepTheConnection` (`ChangeDueSoon` no la toca).
  - `SupportTaskNoticeChannelsTests`: `MapsTheTemplateStatus` (Theory: `null`→`Missing`, `"APPROVED"`→`Approved`, `"PENDING"`→`Pending`, `"IN_APPEAL"`→`Pending`, `"REJECTED"`→`Rejected`, `"PAUSED"`→`Rejected`); `UsesTheDisplayPhoneNumber`; `ExposesTheConfiguredEscalationMinutes`.
  - `SupportTaskNoticeSettingsApiTests` (con `ISupportTaskNoticeChannels` doble en el host): `GetReturnsNullConnectionAndTheOptionsByDefault` (`taskNotice.whatsAppConnectionId == null`, `connections` con `templateStatus`, `escalationAfterMinutes == 30`); `PutSavesAConnectionWithoutAnApprovedTemplate` (status `Pending` ⇒ 200); `PutRejectsAConnectionThatIsNotListed` ⇒ 422 `support.settings.task_notice_connection_invalid`; `PutWithoutTaskNoticeKeepsTheConnection`; `PutWithNullTaskNoticeKeepsTheConnection`; `PutWithNullConnectionClearsIt`; `TheChosenConnectionSurvivesWhenItIsNoLongerListed` (se guarda, el doble deja de listarla, el `GET` sigue devolviendo el id); `ChangeIsAuditedAsTaskNotice`; `StaleIfMatchIs412`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Support/Modules.Support.UnitTests; if ($?) { dotnet build tests/Bootstrapper/Bootstrapper.UnitTests }`. Expected: no compila.
- [ ] **Step 3: Implement** lo de *Produces* y la migración (sólo `AddColumn` nullable).
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~SupportTenantSettingsTests|FullyQualifiedName~UpdateSupportSettingsValidatorTests"; if ($?) { dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SupportTaskNoticeChannelsTests" }; if ($?) { dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~NotificationsOptionsValidatorTests" }`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(support): choose the WhatsApp connection for task notices`

### Task 8: Lector de la tarea para la escalada

**Files:**
- Create: `src/Modules/Support/Modules.Support.Application/ISupportTaskNoticeReader.cs`
- Create: `src/Modules/Support/Modules.Support.Infrastructure/Persistence/SupportTaskNoticeReader.cs`; Modify: `SupportInfrastructureExtensions.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskNoticeReaderTests.cs`

**Interfaces:**
- Produces: `public sealed record SupportTaskNoticeSnapshot(bool IsInternal, bool IsOpen, Guid? AssigneeMemberId, string TicketNumber, string Subject, Guid? WhatsAppConnectionId);` `public interface ISupportTaskNoticeReader { Task<SupportTaskNoticeSnapshot?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken ct); }` — `AsNoTracking`; `IsOpen` = estado distinto de `Resolved` y `Closed`; `TicketNumber` con `SupportTicketNumber.Format`; `WhatsAppConnectionId` = `tenant_settings.task_notice_connection_id` (fila ausente ⇒ `null`); ticket de otro tenant ⇒ `null`.

- [ ] **Step 1: Write the failing test** — `SupportTaskNoticeReaderTests`: `ReadsAnOpenTaskWithItsAssigneeAndConnection`; `ReadsAnOpenCustomerTicket` (`IsInternal == false`, abierto; spec D-7: el lector sirve a los dos); `AResolvedTaskIsNotOpen`; `AnotherTenantsTicketIsNull`; `WithoutSettingsTheConnectionIsNull`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Expected: no compila.
- [ ] **Step 3: Implement** y registrar.
- [ ] **Step 4: Verify** — `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Expected: compila (la integración corre en el Cierre).
- [ ] **Step 5: Commit** — `feat(support): read a task's notice state`

**Revisión del Bloque 3** (una, lente `review-reliability`).

> Orden: el Bloque 3 necesita la constante `TaskNoticeOptions.DefaultEscalationAfterMinutes`. Para no depender del Bloque 4, la Task 7 crea `TaskNoticeOptions` en `src/Modules/Notifications/Modules.Notifications.Infrastructure/NotificationsOptions.cs` (`public sealed class TaskNoticeOptions { public const int DefaultEscalationAfterMinutes = 30; public const int MinEscalationAfterMinutes = 1; public const int MaxEscalationAfterMinutes = 1440; public int EscalationAfterMinutes { get; init; } = DefaultEscalationAfterMinutes; }` y `NotificationsOptions.TaskNotice`) y la regla de rango en `NotificationsOptionsValidator` con su prueba `RejectsAnEscalationOutsideOneMinuteToOneDay` en `NotificationsOptionsValidatorTests`. La Task 9 la consume.

---

## Bloque 4 — Notifications: campana, marca de leída, escalada y correo

### Task 9: Entrada `SupportTaskAssigned`, resolución por miembro y fila de escalada

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Domain/InAppNotificationKind.cs` (`SupportTaskAssigned` y `SupportTicketAssigned` al final; 21 caracteres, cabe en `varchar(64)`)
- Create: `src/Modules/Notifications/Modules.Notifications.Domain/TaskNoticeEscalation.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/InApp/InAppNotificationMapper.cs:25-111`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/InApp/InAppOutboxConsumer.cs` (`InAppResolution` + `MemberId`; hook `OnInsertedAsync`), `InAppNotificationWriter.cs:31-119`, `SupportInAppConsumer.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Persistence/NotificationsDbContext.cs` (`TaskNoticeEscalations`)
- Create: migración Notifications `AddTaskNoticeEscalations` (+ snapshot)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/InAppDtoAndParsingTests.cs` (casos nuevos), `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs` (casos nuevos), `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InAppSupportTaskTests.cs`

**Interfaces:**
- Consumes: payloads de `task-assigned` (`SupportIntegrationEvents.cs:66-85`: `ticketId, tenantId, ticketNumber, assigneeMemberId, assignedByMemberId`), `assigned` (mismo payload) y `assignee-released` (Task 6); `TaskNoticeOptions` (Task 7).
- Produces:
  - `TaskNoticeEscalation` (dominio): `Id, TenantId, InAppNotificationId, TicketId, MemberId, AssignedByMemberId, DueAt, Status (TaskNoticeEscalationStatus), SkipReason (string?), ChannelAttemptsJson (string, default "[]"), Attempts (int), ClaimedUntil (DateTimeOffset?), CreatedAt, CompletedAt (DateTimeOffset?)`; `public enum TaskNoticeEscalationStatus { Pending, Skipped, Whatsapp, Email, Failed }` (nombres del spec §5.4, guardados como texto `varchar(16)`). Métodos: `static Create(...)`, `Skip(string reason, DateTimeOffset now)`, `RecordAttempt(string channel, string result, DateTimeOffset at)`, `CompleteBy(TaskNoticeEscalationStatus status, DateTimeOffset now)`, `Defer(DateTimeOffset nextDueAt)` (spec D-1: `DueAt = nextDueAt`, `ClaimedUntil = null`, `Attempts = Math.Max(0, Attempts - 1)` —devuelve el intento que contó el reclamo—, sin tocar `ChannelAttemptsJson`); todos sólo desde `Pending`. `public static class TaskNoticeSkipReasons { Read = "read"; Resolved = "resolved"; NotAssignee = "not_assignee"; TaskClosed = "task_closed"; TicketMissing = "ticket_missing"; MemberInactive = "member_inactive"; AttemptsExhausted = "attempts_exhausted"; }`.
  - Tabla `notifications.task_notice_escalations` (columnas en snake_case de lo anterior; `channel_attempts jsonb`), índice **único** `UX_task_notice_escalations_notification` sobre `in_app_notification_id`, índice `IX_task_notice_escalations_due` sobre `(status, due_at)`, FK a `in_app_notifications(id)` `ON DELETE CASCADE` (la retención de 90 días borra la entrada; la escalada ya terminó hace mucho).
  - `internal sealed record InAppResolution(Guid TenantId, InAppNotificationKind Kind, Guid ResourceId, string Resolution, Guid? MemberId = null);` — con `MemberId`, el `UPDATE` de `InAppNotificationWriter.ResolveAsync` suma `AND member_id = {memberId}` (dos sentencias SQL, sin `IS NULL` sobre parámetro).
  - `InAppOutboxConsumer`: `protected virtual Task OnInsertedAsync(NotificationsDbContext db, OutboxRecord record, IReadOnlyList<InAppNotification> inserted, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;` invocado por `InAppNotificationWriter.SaveAsync` **dentro** de la transacción, después de insertar y antes del `processed_at`, sólo con las entradas que de verdad se insertaron. `SaveAsync` recibe el callback como parámetro opcional `Func<IReadOnlyList<InAppNotification>, Task>? onInserted = null`.
  - `SupportInAppConsumer`: `EventNames` suma `support.ticket.task-assigned.v1`, `support.ticket.assigned.v1` y `support.ticket.assignee-released.v1`. Asignada (los dos eventos de asignación, mismo código; `task-assigned` ⇒ kind `SupportTaskAssigned` y prefijo `support-task-assigned`, `assigned` ⇒ kind `SupportTicketAssigned` y prefijo `support-ticket-assigned`): si `assigneeMemberId == assignedByMemberId` ⇒ ninguna entrada; si no, una entrada del kind, `resourceId = ticketId`, data `{ ticketId, ticketNumber, actorMemberId = assignedByMemberId, resolution = null }`, `dedupeKey = $"{prefijo}:{ticketId:D}:{record.Id:D}"`. `OnInsertedAsync` inserta una escalada por entrada nueva (de cualquiera de los dos kinds) con `INSERT … ON CONFLICT (in_app_notification_id) DO NOTHING`, `due_at = now + EscalationAfterMinutes`. Liberada: `ResolutionsAsync` ⇒ `InAppResolution(tenantId, isInternal ? SupportTaskAssigned : SupportTicketAssigned, ticketId, reason, MemberId: formerAssigneeMemberId)`.
  - Mapper: `KeysOf(SupportTaskAssigned) = KeysOf(SupportTicketAssigned) = ["ticketId", "ticketNumber", "resolution"]`; `actorName` resuelto de `actorMemberId` con `IInAppNotificationNames.MemberNamesAsync` (el mismo lote que `ConversationAssigned`); sin nombre ⇒ `null`. `actorMemberId` no sale en el DTO.

- [ ] **Step 1: Write the failing tests**
  - `InAppDtoAndParsingTests`: `TaskAssignedServesTicketNumberAndActorNameButNoSubject` (data con `ticketId`, `ticketNumber`, `actorName`, `resolution`; sin `actorMemberId` ni `subject`); `TaskAssignedWithoutAKnownActorHasNullActorName`.
  - `NotificationsDbContextMappingTests`: `TaskNoticeEscalationsHaveAUniqueIndexOnTheEntry`; `TheEscalationStatusFitsItsColumn` (todo nombre del enum ≤ 16).
  - `TaskNoticeEscalationTests` (unitaria, dominio): `DeferMovesTheDueTimeAndGivesTheClaimBack` (con `Attempts = 1` y `ClaimedUntil` puesto ⇒ `DueAt` nuevo, `ClaimedUntil == null`, `Attempts == 0`, `ChannelAttemptsJson == "[]"`); `DeferOutsidePendingThrows`.
  - `InAppSupportTaskTests` (helpers de `InAppTestSupport`):
    - `TaskAssignedCreatesAnEntryAndOneEscalation` — una entrada `SupportTaskAssigned` para el asignado con `dedupe_key` `support-task-assigned:{ticket}:{message}`; una fila en `task_notice_escalations` con `status = 'Pending'`, `due_at = created_at + 30 min` (± 1 s), `in_app_notification_id` = la entrada; un `notificationsChanged`.
    - `ARetriedMessageAddsNoEntryNorEscalation` — se borra `processed_at` del inbox y se drena otra vez ⇒ sigue 1 y 1.
    - `SelfAssignmentNotifiesNobody` — `assignee == assignedBy` ⇒ 0 entradas, 0 escalas.
    - `TwoAssignmentsOfTheSameTicketAreTwoEntries`.
    - `ReassigningResolvesOnlyTheFormerAssigneesEntry` — entrada abierta de A; mensajes `assignee-released(A)` y `task-assigned(B)` con el mismo `occurred_at` ⇒ la de A `resolved_at` y `read_at` no nulos, `data.resolution = "Reassigned"`; la de B sin resolver.
    - `ResolvingTheTaskResolvesTheEntry` (`reason = "Resolved"`).
    - `CustomerTicketAssignedCreatesATicketEntryAndOneEscalation` — evento `assigned` ⇒ entrada `SupportTicketAssigned` con `dedupe_key` `support-ticket-assigned:{ticket}:{message}` y una escalada `Pending`.
    - `ReleasingACustomerTicketResolvesOnlyTheTicketEntry` — `assignee-released` con `isInternal = false` resuelve la `SupportTicketAssigned` de A y no toca una `SupportTaskAssigned` del mismo miembro.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests`. Expected: no compila.
- [ ] **Step 3: Implement** lo de *Produces* y la migración.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~InAppDtoAndParsingTests|FullyQualifiedName~NotificationsDbContextMappingTests|FullyQualifiedName~NotificationsOptionsValidatorTests|FullyQualifiedName~TaskNoticeEscalationTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): bell entry and escalation row for assigned tasks`

### Task 10: Marcar leídas por recurso (D-2)

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/InApp/MarkResourceNotificationsRead.cs`
- Modify: `IInAppNotificationRepository.cs`, `src/Modules/Notifications/Modules.Notifications.Infrastructure/InApp/InAppNotificationRepository.cs`, `InAppNotificationDtos.cs`, `src/Modules/Notifications/Modules.Notifications.Api/NotificationEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:~2040` (handler + validador)
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InAppNotificationEndpointsTests.cs` (casos nuevos), `tests/Modules/Notifications/Modules.Notifications.UnitTests/InAppDtoAndParsingTests.cs` (validador)

**Interfaces:**
- Produces:
  - `POST /api/v1/tenants/{tenantId}/notifications/read-by-resource`, cuerpo `{ "kind": "SupportTaskAssigned", "resourceId": "uuid" }`, sólo sesión, sin `If-Match`, idempotente. 200 `{ "marked": int, "unreadCount": int }`. 403 como el resto (`InAppAuthorization`). 422 `validation.failed` con `errors.kind` (no es un `InAppNotificationKind` exacto) o `errors.resourceId` (vacío). Ninguna entrada ⇒ 200 `marked: 0` (nunca 404). Con `marked > 0` publica `notificationsChanged` con `notificationId = null`.
  - `public sealed record MarkResourceReadRequest(string? Kind, Guid? ResourceId);` `public sealed record MarkResourceNotificationsReadCommand(Guid TenantId, string Kind, Guid ResourceId) : ICommand<MarkResourceReadResultDto>;` `public sealed record MarkResourceReadResultDto(int Marked, int UnreadCount);` `Task<int> MarkReadByResourceAsync(Guid tenantId, Guid memberId, InAppNotificationKind kind, Guid resourceId, DateTimeOffset now, CancellationToken ct)` (sólo las del que llama y sin leer).
  - Contrato para el frontend (spec par §4.5): al abrir el detalle de un ticket, por cualquier camino, el SPA llama este endpoint con `kind = "SupportTaskAssigned"` si es una tarea o `"SupportTicketAssigned"` si es de cliente, y el `ticketId`. Ver discrepancia D-3.

- [ ] **Step 1: Write the failing tests** — `MarkResourceReadValidator`: `RejectsAnUnknownKind` (`"supporttaskassigned"` ⇒ error en `kind`); `RejectsAnEmptyResource`. Integración: `ReadByResourceMarksOnlyTheCallersEntriesOfThatTicket` (dos entradas del que llama sobre el ticket, una de otro ticket, una de otro miembro sobre el mismo ticket ⇒ `marked == 2`, las otras dos siguen sin leer, `unreadCount` correcto, un `notificationsChanged`); `ReadByResourceTwiceMarksNothingTheSecondTime` (`marked == 0`, sin publicación); `ReadByResourceForAnotherTenantIs403`.
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests`. Expected: no compila.
- [ ] **Step 3: Implement** y registrar.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~MarkResourceRead"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): mark a resource's entries read`

### Task 11: Puertos de la escalada, decisión y correo

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/TaskNotices/TaskNoticePorts.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Application/TaskNotices/TaskNoticePolicy.cs`
- Create: `src/Modules/Notifications/Modules.Notifications.Application/SupportTaskAssignedEmailTemplate.cs`; Modify: `AccountLinks.cs` (`SupportTask(string appBaseUrl, Guid ticketId) => $"{base}/support/{ticketId:D}"`)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/TaskNoticePolicyTests.cs`, `tests/Modules/Notifications/Modules.Notifications.UnitTests/SupportTaskAssignedEmailTemplateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record TaskNoticeTicket(bool IsInternal, bool IsOpen, Guid? AssigneeMemberId, string TicketNumber, string Subject, Guid? WhatsAppConnectionId);` `public interface ITaskNoticeState { Task<TaskNoticeTicket?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken ct); }`
  - `public sealed record TaskNoticeRecipient(bool IsActive, string DisplayName, string? Email, string? PhoneE164);` `public interface ITaskNoticeRecipients { Task<TaskNoticeRecipient?> FindAsync(Guid tenantId, Guid memberId, CancellationToken ct); }`
  - `public sealed record TaskNoticeWhatsAppSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string PhoneE164, string RecipientName, string TicketNumber);` `public abstract record TaskNoticeWhatsAppResult { Sent; Failed(string Code); }` `public interface ITaskNoticeWhatsApp { Task<TaskNoticeWhatsAppResult> SendAsync(TaskNoticeWhatsAppSend send, CancellationToken ct); }` — códigos: `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>` (mismos de `WholesaleInvitationNoticeSender.cs:35-70`).
  - `public interface ITaskNoticePresence { Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken ct); }` (spec D-1: ¿tiene QEP abierto?).
  - `public static class TaskNoticePolicy { public static readonly TimeSpan PresenceRecheck = TimeSpan.FromMinutes(1); public static string? SkipReason(bool entryRead, bool entryResolved, TaskNoticeTicket? ticket, Guid memberId, TaskNoticeRecipient? recipient); public static TaskNoticeRoute Route(Guid? connectionId, string? phoneE164); }` con `public abstract record TaskNoticeRoute { WhatsApp(Guid ConnectionId, string PhoneE164); EmailOnly(string Reason); }`. Orden de `SkipReason`: resuelta ⇒ `resolved`; leída ⇒ `read`; ticket `null` ⇒ `ticket_missing` (spec D-7: un ticket de cliente también se escala); no abierta ⇒ `task_closed`; responsable distinto ⇒ `not_assignee`; destinatario `null` o inactivo ⇒ `member_inactive`; si no, `null` (se envía). `Route`: sin conexión ⇒ `EmailOnly("no_connection")`; sin celular ⇒ `EmailOnly("no_phone")`; si no, `WhatsApp`.
  - `SupportTaskAssignedEmailTemplate { TemplateRef = "support.task-assigned.v1"; static EmailMessage Render(string recipientAddress, bool isTask, string? actorName, string ticketNumber, string subject, string link); }` — con `isTask`: asunto «Te asignaron la tarea {ticketNumber}»; cuerpo «Hola, {actorName} te asignó la tarea {ticketNumber}: «{subject}». Ábrela en QEP: {link}» (sin `actorName`: «Te asignaron la tarea…»). Sin `isTask` (ticket de cliente, spec D-7): «el ticket» en lugar de «la tarea» y «Ábrelo»; `subject` y `actorName` codificados con `WebUtility.HtmlEncode` en el HTML. Ver P-2.

- [ ] **Step 1: Write the failing tests** — `TaskNoticePolicyTests`: un caso por cada motivo de `SkipReason` en el orden dado, incluido `ResolvedWinsOverRead`; `OpenTaskOfTheSameActiveMemberIsSent` (⇒ `null`); `RouteWithoutConnectionIsEmail` aunque haya celular (D-5: sin conexión configurada nunca WhatsApp); `RouteWithoutPhoneIsEmail`; `RouteWithBothIsWhatsApp`. `SupportTaskAssignedEmailTemplateTests`: `CarriesNumberSubjectAndLink`; `EncodesTheSubjectInHtml` (`"<b>x</b>"` ⇒ `&lt;b&gt;`); `WithoutActorUsesTheImpersonalSentence`; `ACustomerTicketSaysTicketNotTask`; `ACustomerTicketIsNotSkippedAsMissing` (en `TaskNoticePolicyTests`: `IsInternal = false`, abierto, mismo miembro ⇒ `null`); `LinkPointsToTheTask` (`AccountLinks.SupportTask("https://qep.qcode.co/", id)` == `$"https://qep.qcode.co/support/{id:D}"`).
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests`. Expected: no compila.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~TaskNoticePolicyTests|FullyQualifiedName~SupportTaskAssignedEmailTemplateTests"`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): task notice ports, routing policy and email`

### Task 11b: Presencia de miembros en el `AppHub` (spec D-1, §5.4 «Presencia»)

Calco de la presencia de la bandeja (`MessagingPresence`, `ValkeyMessagingPresence`, `PresenceHeartbeatWorker`, `ValkeyMessagingPresenceReader`), pero alimentada por el `AppHub`, que abre todo miembro con tenant. Leer esos cuatro archivos completos antes de empezar.

**Files:**
- Modify: `src/Bootstrapper/Realtime/AppHubConnections.cs` (`Snapshot()` y `HasMember(Guid tenantId, Guid memberId)`; `Remove` ya existe)
- Create: `src/Bootstrapper/Realtime/AppMemberPresence.cs` (interfaz + `LocalAppMemberPresence`)
- Create: `src/Bootstrapper/Valkey/Presence/ValkeyAppMemberPresence.cs`, `src/Bootstrapper/Valkey/Presence/AppPresenceHeartbeatWorker.cs`
- Modify: `src/Bootstrapper/Valkey/ValkeyKeys.cs` (`AppOnline(Guid tenantId) => $"{Prefix}rt:online:{tenantId:D}"`)
- Modify: `src/Bootstrapper/Realtime/AppHub.cs` (`OnConnectedAsync` tras `Register`; `OnDisconnectedAsync` tras quitar la conexión)
- Modify: `src/Bootstrapper/Valkey/ValkeyServiceCollectionExtensions.cs` (`AddPresence`: local siempre; Valkey + latido con el área prendida)
- Create: `src/Bootstrapper/TaskNoticePresence.cs`; Modify: `QepServiceCollectionExtensions.cs` (`ITaskNoticePresence → TaskNoticePresence`)
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (`AppPresenceHeartbeatWorker`, `PerPod`, junto a la línea 185)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/Realtime/LocalAppMemberPresenceTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/Valkey/ValkeyAppMemberPresenceTests.cs` (fixture `ValkeyPlainServer`, como `ValkeyPresenceTests`)

**Interfaces:**
- Produces:
  - `internal interface IAppMemberPresence { void Connected(Guid tenantId, Guid memberId); void Disconnected(Guid tenantId, Guid memberId); Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken ct); }`
  - `LocalAppMemberPresence(AppHubConnections connections)`: `Connected`/`Disconnected` no hacen nada (el registro ya lo lleva el hub); `IsConnectedAsync` = `connections.HasMember(...)`.
  - `ValkeyAppMemberPresence(LocalAppMemberPresence local, AppHubConnections connections, ValkeyGateway gateway, TimeProvider time)`: `Connected` = `ZADD qep:rt:online:{tenant} {member} {ahora}` + `EXPIRE` (`ValkeyPresence.KeyTtl`), **sin esperar** (calco de `ValkeyMessagingPresence.Connected`); `Disconnected` = `ZREM` sólo si `!connections.HasMember(...)` (otra pestaña en este pod), sin esperar; `IsConnectedAsync` = `ZSCORE` y conectado si `score > ValkeyPresence.CutoffOf(ahora)`; si el gateway falla, `local.IsConnectedAsync`.
  - `AppPresenceHeartbeatWorker(AppHubConnections connections, ValkeyGateway gateway, TimeProvider time) : BackgroundService` — calco de `PresenceHeartbeatWorker` con `connections.Snapshot()` y `ValkeyKeys.AppOnline`; `internal Task BeatAsync(CancellationToken)`.
  - `AppHub`: `OnConnectedAsync` llama `_presence.Connected(tenantId, memberId)` después de `_connections.Register`; `OnDisconnectedAsync` llama `_presence.Disconnected(tenantId, memberId)` después de quitar la conexión del registro (para que `HasMember` vea las otras pestañas). Un fallo de presencia nunca rompe la conexión ni la limpieza.
  - `TaskNoticePresence(IAppMemberPresence presence) : ITaskNoticePresence` (mapeo 1:1).
  - Inventario: `new("Bootstrapper.Valkey.Presence.AppPresenceHeartbeatWorker", WorkerClassification.PerPod, "Bootstrapper/Valkey/Presence/AppPresenceHeartbeatWorker.cs:<líneas> — late los conectados al AppHub de su pod (ZADD idempotente); sólo con Valkey:Areas:Presence")`.

- [ ] **Step 1: Write the failing tests**
  - `LocalAppMemberPresenceTests`: `AConnectedMemberIsConnected`; `ClosingTheOnlyTabDisconnects`; `ClosingOneOfTwoTabsKeepsItConnected`; `AnotherTenantIsNotConnected`.
  - `ValkeyAppMemberPresenceTests` (reloj manual, tenant nuevo por prueba, sondeo de 2 s como `ValkeyPresenceTests`): `TwoPodsSeeEachOthersMembers` (conectado en el pod A ⇒ el pod B lo lee conectado); `WithoutHeartbeatItIsDisconnectedAfterNinetySeconds`; `TheHeartbeatKeepsItConnected` (`BeatAsync` y +60 s ⇒ sigue); `DisconnectingWithAnotherLocalTabKeepsIt`; `WhenValkeyFailsItReadsTheLocalRegistry` (gateway con el área caída ⇒ responde lo del pod).
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Expected: no compila (`IAppMemberPresence` no existe).
- [ ] **Step 3: Implement** lo de *Produces* y los registros.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~LocalAppMemberPresenceTests"`. Expected: PASS. `ValkeyAppMemberPresenceTests` levanta un contenedor: corre en el Cierre (aquí sólo compila).
- [ ] **Step 5: Commit** — `feat(realtime): know whether a member has the app open`

### Task 12: `TaskNoticeEscalationWorker` y adaptadores de Bootstrapper

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/TaskNotices/TaskNoticeEscalationWorker.cs`, `TaskNoticeEscalationProcessor.cs`
- Modify: `NotificationsInfrastructureExtensions.cs:72-80` (processor scoped + `AddHostedService<TaskNoticeEscalationWorker>()`)
- Create: `src/Bootstrapper/TaskNoticeState.cs`, `src/Bootstrapper/TaskNoticeRecipients.cs`, `src/Bootstrapper/TaskNoticeWhatsAppSender.cs`; Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a `InAppNotificationNames`, ~2040)
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (bloque «Claimed», junto a la línea 126)
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/TaskNoticeEscalationTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/TaskNoticeWhatsAppSenderTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/TaskNoticeRecipientsTests.cs`

**Interfaces:**
- Consumes: Task 9 (tabla, entidad, `Defer`), Task 11 (puertos, política, correo), Task 11b (`ITaskNoticePresence`), Task 8 (`ISupportTaskNoticeReader`), Task 2 (`MembershipPhoneReader`), Task 4-5 (`ITaskNoticeTemplateReadiness`, `ITaskNoticeOutbound`), `IInAppNotificationNames.MemberNamesAsync`, `IEmailChannel`, `IOptions<NotificationsOptions>.AppBaseUrl`, `IClock`.
- Produces:
  - `TaskNoticeEscalationWorker : BackgroundService` — `PeriodicTimer` de 1 min, `internal static readonly TimeSpan Period`; cada tick llama `TaskNoticeEscalationProcessor.RunAsync(now, ct)` en un scope nuevo; una excepción del tick se loguea y sigue.
  - `internal sealed partial class TaskNoticeEscalationProcessor { internal const int BatchSize = 20; internal const int MaxAttempts = 3; internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2); internal Task<int> RunAsync(DateTimeOffset now, CancellationToken ct); }` — reclamo en SQL crudo:
    ```sql
    UPDATE notifications.task_notice_escalations SET claimed_until = {now + Lease}, attempts = attempts + 1
    WHERE id IN (SELECT id FROM notifications.task_notice_escalations
                 WHERE status = 'Pending' AND due_at <= {now} AND (claimed_until IS NULL OR claimed_until < {now})
                 ORDER BY due_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
    RETURNING id
    ```
    Por fila, aislada (`try/catch` que loguea, `ChangeTracker.Clear()` en `finally`): `attempts > MaxAttempts` ⇒ `Failed` + `attempts_exhausted`; lee la entrada (`read_at`, `resolved_at`) de la misma tabla, `ITaskNoticeState`, `ITaskNoticeRecipients`; `TaskNoticePolicy.SkipReason` ⇒ `Skip`; si no, `ITaskNoticePresence.IsConnectedAsync(tenantId, memberId)` ⇒ conectado: `Defer(now + TaskNoticePolicy.PresenceRecheck)`, se guarda y no se envía nada (spec D-1); una excepción de la presencia se trata como «desconectado» (falla hacia avisar) y se loguea; si no, `Route`: WhatsApp con `ClientId = escalation.Id`, `SentByMemberId = AssignedByMemberId`, ⇒ `Sent` ⇒ `CompleteBy(Whatsapp)`; `Failed(code)` ⇒ se anota y sigue a correo. Correo: sin `Email` ⇒ intento `email`/`no_email` y `Failed`; con correo ⇒ `IEmailChannel.SendAsync` (excepción ⇒ `failed`, estado `Failed`; éxito ⇒ `Email`). Cada intento va a `channel_attempts` como `{ "channel": "whatsapp"|"email", "at": "<ISO>", "result": "sent"|"<código>" }`. Guardado con `CancellationToken.None` después del envío.
  - `TaskNoticeState(ISupportTaskNoticeReader reader) : ITaskNoticeState` (mapeo 1:1).
  - `TaskNoticeRecipients(IMembershipRepository memberships, IUserDirectory users, MembershipPhoneReader phones) : ITaskNoticeRecipients` — `IsActive = State == Active`; `DisplayName = membership.DisplayName ?? email ?? "Miembro"`; `PhoneE164` = teléfono efectivo; membresía inexistente ⇒ `null`.
  - `TaskNoticeWhatsAppSender(ITaskNoticeTemplateReadiness readiness, ITaskNoticeOutbound outbound) : ITaskNoticeWhatsApp` — calco de `WholesaleInvitationNoticeSender`: sin plantilla aprobada ⇒ `template_not_approved`; `WaId` = sólo dígitos del E.164; valores **sólo** `{ numero = TicketNumber, nombre_contacto = RecipientName de una línea }`; mapea `ConnectionUnavailable`/`Failed`.
  - Inventario: `new("Modules.Notifications.Infrastructure.TaskNotices.TaskNoticeEscalationWorker", WorkerClassification.Claimed, "Modules/Notifications/Modules.Notifications.Infrastructure/TaskNotices/TaskNoticeEscalationProcessor.cs:<líneas del UPDATE> — FOR UPDATE SKIP LOCKED + claimed_until de 2 min; el WhatsApp es idempotente por ClientId (el id de la escalada)")`.

- [ ] **Step 1: Write the failing tests**
  - `TaskNoticeWhatsAppSenderTests`: `WithoutAnApprovedTemplateFailsWithTemplateNotApproved` (y no llama al outbound); `SendsOnlyTheNumberAndTheContactName` (las claves de `Values` son exactamente `{numero, nombre_contacto}`: el asunto no viaja); `TheWaIdIsDigitsOnly`; `MapsConnectionUnavailableAndFailed` (`connection_unavailable:paused`, `send_failed:messaging.message.rejected`).
  - `TaskNoticeRecipientsTests`: `SuspendedMembershipIsInactive`; `UsesTheEffectivePhone`; `UnknownMembershipIsNull`.
  - `TaskNoticeEscalationTests` (host con dobles de `ITaskNoticeState`, `ITaskNoticeRecipients`, `ITaskNoticePresence` (por defecto desconectado), `ITaskNoticeWhatsApp` e `IEmailChannel` registrando llamadas; filas sembradas vía consumidor de la Task 9 o SQL directo; `RunAsync` con `now` explícito):
    - `NothingHappensBeforeTheDueTime`.
    - `AConnectedMemberIsDeferredAMinuteWithoutSending` ⇒ sigue `Pending`, `due_at = now + 1 min`, `attempts = 0`, `claimed_until` nulo, `channel_attempts = []`, 0 envíos.
    - `StayingConnectedNeverExhaustsTheAttempts` ⇒ cinco corridas seguidas, una por minuto, conectado ⇒ sigue `Pending`, nunca `attempts_exhausted`.
    - `EscalatesOnceTheMemberDisconnects` ⇒ conectado en la primera corrida, desconectado en la siguiente ⇒ `Whatsapp` o `Email`.
    - `AConnectedMemberWhoReadsIsSkippedAsRead` ⇒ pospuesta, se marca leída, siguiente corrida ⇒ `Skipped`/`read`.
    - `APresenceFailureEscalates` ⇒ el doble lanza ⇒ se envía como desconectado.
    - `DoesNotEscalateAReadEntry` ⇒ `Skipped`/`read`, 0 envíos.
    - `DoesNotEscalateAResolvedEntry` ⇒ `resolved`.
    - `DoesNotEscalateAReassignedTask` ⇒ `not_assignee`.
    - `DoesNotEscalateAClosedTask` ⇒ `task_closed`.
    - `EscalatesByWhatsAppWithConnectionApprovedTemplateAndPhone` ⇒ `Whatsapp`, un envío con `ClientId == escalationId` y `SentByMemberId == assignedBy`, 0 correos.
    - `FallsBackToEmailWithoutPhone` ⇒ `Email`, `channel_attempts` sólo `email`/`sent`.
    - `FallsBackToEmailWithoutConnection` ⇒ `Email` aunque haya celular; 0 WhatsApp.
    - `FallsBackToEmailWhenTemplateIsNotApproved` ⇒ intentos `whatsapp`/`template_not_approved` y `email`/`sent`.
    - `FallsBackToEmailWhenMetaFails` ⇒ `whatsapp`/`send_failed:…`, luego correo.
    - `NeverSendsByTwoChannels` ⇒ con WhatsApp `Sent` no hay correo.
    - `EscalatesACustomerTicketToo` ⇒ escalada de una entrada `SupportTicketAssigned` con ticket `IsInternal = false` ⇒ `Whatsapp`; por correo, el texto dice «el ticket».
    - `AFailingRowDoesNotStopTheOthers` — dos tenants; el doble de `ITaskNoticeState` lanza para el primero ⇒ el segundo se envía y el primero sigue `Pending` con `attempts = 1`.
    - `AfterThreeClaimsTheRowFails` ⇒ `Failed`/`attempts_exhausted`.
    - `TwoConcurrentRunsSendOnce` — dos `RunAsync` en paralelo (`Task.WhenAll`) sobre la misma fila ⇒ un solo envío.
    - `ARowClaimedByAnotherReplicaIsNotTakenUntilTheLeaseEnds`.
  - `MultiReplicaInventoryTests` ya exige la entrada: compila y queda roja hasta registrar el worker (verificación en el Cierre).
- [ ] **Step 2: Run test to verify it fails** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests; if ($?) { dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests }`. Expected: no compila.
- [ ] **Step 3: Implement** lo de *Produces*; registrar adaptadores, processor, hosted service e inventario en el mismo commit.
- [ ] **Step 4: Run test to verify it passes** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~TaskNoticeWhatsAppSenderTests|FullyQualifiedName~TaskNoticeRecipientsTests"`. Expected: PASS. `dotnet build tests/ArchitectureTests/ArchitectureTests` en verde.
- [ ] **Step 5: Commit** — `feat(notifications): escalate unread task notices by WhatsApp or email`

**Revisión del Bloque 4** (una, lente `review-resilience`: worker, reclamo y fallos parciales).

---

## Cierre de la rama (una sola vez)

- [ ] `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay `testhost` de otra sesión, esperar.
- [ ] Arquitectura: `dotnet test tests/ArchitectureTests/ArchitectureTests`. Esperado: verde (capas, handlers registrados, `MultiReplicaInventoryTests`).
- [ ] Integración, **un proyecto a la vez**, con `-- xUnit.MaxParallelThreads=4`, sólo módulos tocados: Tenancy, Hr (sólo `--filter "FullyQualifiedName~HrMemberPhonesTests"`), Messaging (`--filter "FullyQualifiedName~TaskNotice"`), Support, Notifications. Validar cada ruta con `Test-Path` antes (una ruta vacía corre toda la solución).
- [ ] Unitarias completas de Tenancy, Support, Messaging, Notifications y `Bootstrapper.UnitTests`.
- [ ] Revisión fuerte de la rama entera (modelo más capaz). Hot path de datos personales y >400 líneas ⇒ 4R según el flujo de `gentle-ai review`.
- [ ] Merge a `develop` (nunca `main`); orden de despliegue: backend antes que frontend; un frontend viejo pinta `SupportTaskAssigned` como genérico. Después del merge, en `develop`: `docs/estado.md`.

## Discrepancias con el spec (gana el código)

- **D-1.** §5.2 dice que el payload trae `occurredAt`; el código publica cinco campos sin él (`SupportIntegrationEvents.cs:72-84`). Se usa `outbox_messages.occurred_at`.
- **D-2.** §5.3 pide resolver la entrada al reasignar, resolver o cerrar, pero Soporte no publica nada en esos casos (`TakeTicket.cs:30` «Tomar nunca avisa»; `ChangeTicketStatus.cs` sin evento). Se agrega `support.ticket.assignee-released.v1` (Task 6; cubre tareas y tickets de cliente, spec D-7). Además el mecanismo D-13 resuelve **todas** las entradas del `kind + resource` (`InAppNotificationWriter.cs:105-119`), lo que cerraría también la entrada nueva del reasignado: se agrega el filtro por miembro.
- **D-3.** §5.3/D-2 proponen «un endpoint o un parámetro en la ruta de lectura»; la campana sólo marca por id (`NotificationEndpoints.cs:31`). Se elige `POST …/notifications/read-by-resource` (Task 10); el spec del frontend §4.5 debe nombrarlo.
- **D-4.** §5.3 fija `data { ticketId, ticketNumber, actorName }`; la resolución D-13 escribe `resolution` en `data` (`InAppNotification.cs:81-94`), así que el contrato suma `resolution` (`null | Reassigned | Resolved | Closed`).
- **D-5.** §3/§5.5 dicen que `Employee.Phone` es texto libre; el alta y la edición ya exigen E.164 (`HrRequestValidators.cs:57-59`) y la importación normaliza (`IHrPhoneNormalizer`). Se normaliza igual, por filas viejas.
- **D-6.** §5.5 habla de «la membresía propia»: no existe ruta para leerla (`MembershipEndpoints.cs` sólo tiene el roster). Se agrega `GET …/memberships/me`.
- **D-7.** §5.5: «los demás no ven el número de otros». La lista exige sólo `advisorship.read` (`MembershipEndpoints.cs:33-34`) y la usa también el selector de asesores; por eso `phone` sólo se llena con `advisorship.manage`.
- **D-8.** §5.5: «ausente conserva, `null` borra» en `PUT …/profile`, pero en ese mismo cuerpo `AdvisorCode` ausente **borra** (`MembershipEndpoints.cs:315-318`). El celular usa un `JsonElement` de tres estados; el código de asesor no cambia.
- **D-9.** §5.6: `{{asesor}}` es variable **fija** que Mensajería llena con el nombre del `SentByMemberId` (`SystemTemplateSend.cs:106-115`); por eso el envío va con quien asignó como autor.

## Decisiones pendientes del owner

- **P-1.** Nombre `qep_tarea_asignada` y texto base de la plantilla («¡Hola! {{asesor}} te asignó {{numero}} en QEP. Ábrelo para ver el detalle.», genérico para tareas y tickets de cliente, spec D-7): el spec propone «{{asesor}} te asignó la tarea {{numero}}. Ábrela en QEP.», que **empieza con variable** y Meta lo rechaza. Confirmar texto.
- **P-2.** Copy final del correo (propuesta en la Task 11).
- **P-3.** El aviso por WhatsApp abre (o reusa) una conversación en Mensajería con el número del miembro, igual que el aviso de invitación mayorista (`PlainTemplateOutbound`). ¿Se acepta que aparezca en la bandeja?
- **P-4.** El teléfono de RR. HH. se acepta si libphonenumber lo valida para el país del tenant, aunque sea fijo; un fijo falla en Meta y cae a correo. ¿Exigir tipo móvil?
- **P-5.** Membresía suspendida o quitada del asignado: la escalada se salta (`member_inactive`); la entrada de campana igual se crea.
- **P-6.** Cambiar el celular propio sube la versión de la membresía (un diálogo de Miembros abierto recibe 412). Alternativa: no tocar la versión desde `/me/phone`.
