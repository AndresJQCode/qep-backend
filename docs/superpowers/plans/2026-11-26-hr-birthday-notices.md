# Employee birthday notices Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A daily digest of employee birthdays (today and today + N) to the HR managers each tenant picks, by bell, and optionally WhatsApp and email.

**Architecture:** HR owns the setting and the daily run, and publishes `hr.birthdays.digest.v1` per manager through its outbox. Notifications
writes the bell entry and one delivery row per channel in the same transaction; a worker sends them through ports implemented in
Bootstrapper. Messaging gets a new system template kind `HrBirthdayNotice`.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers (integration tests are written, not run).

**Spec:** `docs/superpowers/specs/2026-11-26-hr-birthday-notices-design.md`

## Global Constraints

- **No test is run** (owner decision, 2026-11-26): tests are written first; RED = the test project fails to build; GREEN = `dotnet build`
  of the solution with 0 errors. Never `dotnet test`, never `test-feature.ps1`. Commits and the Trello card say «pruebas escritas, sin correr».
- `Api.exe` running locks the build: if `dotnet build` fails with MSB3021, stop and tell the owner (never kill it).
- Work only in the worktree `qep-backend-worktrees/hr-birthday-notices`, branch `feature/hr-birthday-notices`.
- Commits: conventional commits ending with a blank line and
  `Session: Recordatorio de cumpleaños en RRHH` / `Co-Authored-By: Claude <model> <noreply@anthropic.com>`.
- Product text in Colombian Spanish, tuteo. Contract identifiers (codes, enum names, routes) untranslated.
- No birth year or age in any event, entry, email or WhatsApp.
- Limits: managers 0..20 distinct; `LeadDays` 0..7, default 1; 50 names per group in the event.
- Error codes (exact): `hr.birthday_notice.manager_not_found`, `hr.birthday_notice.manager_without_access`,
  `hr.birthday_notice.connection_required`, `hr.birthday_notice.connection_invalid`.
- Event name: `hr.birthdays.digest.v1`. Bell kind: `HrBirthdays`, dedupe key `hr-birthdays:{runDate}`. Template kind: `HrBirthdayNotice`,
  template name `qep_cumpleanos_equipo`. Email template ref: `hr.birthdays-digest.v1`.
- No change to `tests/ArchitectureTests`; no new module-to-module reference (cross-module only through ports implemented in Bootstrapper).
- `docs/estado.md` is **not** touched on this branch.

## Review Focus

1. **A manager saved, then their role loses `hr.employee.read`** → the run skips them (no event); GET shows `hasHrAccess: false`. Test in Task 3.
2. **29 February birthday in a non-leap year** → announced on 28 February, both as "today" and as "upcoming". Test in Task 1.
3. **Lead days crossing the year (31 Dec + 1)** → upcoming is 1 January's birthdays. Test in Task 1.
4. **The same digest event delivered twice** (outbox retry) → one bell entry and one delivery row per channel. Test in Task 5.
5. **A delivery still pending after its run date ended** → `Skipped stale`, nothing sent. Test in Task 6.

---

## Block A — HR (one implementer, Tasks 1–3)

### Task 1: Birthday matching and the setting aggregate (Domain)

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Domain/BirthdayCalendar.cs`
- Create: `src/Modules/Hr/Modules.Hr.Domain/BirthdayNoticeSetting.cs`
- Modify: `src/Modules/Hr/Modules.Hr.Domain/HrErrorCodes.cs` (four codes above, in the `// 422` group)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/BirthdayCalendarTests.cs`, `tests/Modules/Hr/Modules.Hr.UnitTests/BirthdayNoticeSettingTests.cs`

**Interfaces:**
- Produces:
  - `public static class BirthdayCalendar { public static bool FallsOn(DateOnly birthDate, DateOnly day); }`
  - `public sealed class BirthdayNoticeSetting` with `Guid TenantId`, `IReadOnlyList<Guid> ManagerMemberIds`, `int LeadDays`,
    `bool WhatsAppEnabled`, `Guid? WhatsAppConnectionId`, `bool EmailEnabled`, `Guid UpdatedByMemberId`, `long Version`,
    `DateTimeOffset CreatedAt`, `UpdatedAt`; `public const int MaxManagers = 20, MaxLeadDays = 7, DefaultLeadDays = 1;`
    `static BirthdayNoticeSetting Create(Guid tenantId, BirthdayNoticeValues values, Guid updatedBy, DateTimeOffset now)`,
    `void Update(BirthdayNoticeValues values, Guid updatedBy, DateTimeOffset now)` (Version++).
  - `public sealed record BirthdayNoticeValues(IReadOnlyList<Guid> ManagerMemberIds, int LeadDays, bool WhatsAppEnabled, Guid? WhatsAppConnectionId, bool EmailEnabled);`

- [ ] **Step 1: Write the failing tests**

`BirthdayCalendarTests`: `SameMonthAndDayMatches` (1990-05-20 on 2026-05-20 → true), `OtherDayDoesNotMatch`,
`Feb29IsAnnouncedOnFeb28InNonLeapYear` (2000-02-29 on 2027-02-28 → true), `Feb29IsNotAnnouncedOnFeb28InLeapYear` (on 2028-02-28 → false,
on 2028-02-29 → true), `Dec31PlusOneIsJanFirst` (2001-01-01 on `new DateOnly(2026,12,31).AddDays(1)` → true).

`BirthdayNoticeSettingTests`: `CreateStartsAtVersion1`, `UpdateBumpsVersion`, `WhatsAppEnabledWithoutConnectionThrows` (code
`hr.birthday_notice.connection_required`, field `whatsAppConnectionId`), `WhatsAppDisabledClearsConnection` (stored `WhatsAppConnectionId` null),
`DuplicateManagersAreStoredOnce`.

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests` → fails: `BirthdayCalendar` / `BirthdayNoticeSetting` not found.

- [ ] **Step 3: Implement** the two types and the codes. Manager list as a `uuid[]`-friendly `List<Guid>` (Task 2 maps it). Range checks of
  `LeadDays` and the managers count stay in the validator (Task 2); the aggregate only enforces the connection rule and de-duplication.

- [ ] **Step 4: GREEN** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests` → 0 errors.

- [ ] **Step 5: Commit** — `feat(hr): add birthday matching and notice setting`

### Task 2: The setting API (Application, Infrastructure, Api)

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/BirthdayNotices/BirthdayNoticeDtos.cs` (request, DTO, validator)
- Create: `src/Modules/Hr/Modules.Hr.Application/BirthdayNotices/GetBirthdayNotices.cs`, `SaveBirthdayNotices.cs`
- Create: `src/Modules/Hr/Modules.Hr.Application/BirthdayNotices/IBirthdayNoticeSettingRepository.cs`, `IHrWhatsAppConnections.cs`, `IHrMemberContacts.cs`
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/BirthdayNoticeSettingRepository.cs`
- Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/HrDbContext.cs` (DbSet + `ConfigureBirthdayNoticeSettings`, table `hr.birthday_notice_settings`, PK `tenant_id`, `manager_member_ids uuid[]`, `version` concurrency token)
- Create: migration `AddBirthdayNoticeSettings` in `src/Modules/Hr/Modules.Hr.Infrastructure/Persistence/Migrations/`
- Modify: `src/Modules/Hr/Modules.Hr.Api/HrEndpoints.cs` (two routes in `MapSettings`)
- Modify: `src/Modules/Hr/Modules.Hr.Application/HrAuditActions.cs` (`BirthdayNoticesSaved = "hr.birthday_notices.saved"`, resource `birthday_notices`)
- Create: `src/Bootstrapper/HrWhatsAppConnections.cs`, `src/Bootstrapper/HrMemberContacts.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers by hand next to `GetHrSettingsHandler`; the two adapters next to `IHrMemberDirectory`); `src/Modules/Hr/Modules.Hr.Infrastructure/HrInfrastructureExtensions.cs` (repository)
- Modify: `README.md` (HR contracts section: the two routes and codes)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/BirthdayNoticeValidatorTests.cs`, `tests/Modules/Hr/Modules.Hr.IntegrationTests/BirthdayNoticesApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/HrWhatsAppConnectionsTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `public sealed record BirthdayNoticeRequest(IReadOnlyList<Guid>? ManagerMemberIds, int? LeadDays, bool? WhatsAppEnabled, Guid? WhatsAppConnectionId, bool? EmailEnabled);`
  - `public sealed record BirthdayManagerDto(Guid MemberId, string DisplayName, bool HasPhone, bool HasEmail, bool HasHrAccess);`
  - `public sealed record BirthdayNoticesDto(IReadOnlyList<BirthdayManagerDto> Managers, int LeadDays, bool WhatsAppEnabled, Guid? WhatsAppConnectionId, bool EmailEnabled, long? Version);`
  - `GetBirthdayNoticesQuery(Guid TenantId) : IQuery<BirthdayNoticesDto>`; `SaveBirthdayNoticesCommand(Guid TenantId, BirthdayNoticeRequest Request, string? IfMatch) : ICommand<BirthdayNoticesDto>`
  - `IBirthdayNoticeSettingRepository { Task<BirthdayNoticeSetting?> FindAsync(Guid tenantId, CancellationToken); void Add(BirthdayNoticeSetting); }`
  - `IHrWhatsAppConnections { Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken); }` (adapter over `IIntegrationConnections.ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key)`, as `WholesaleWhatsAppConnections`)
  - `IHrMemberContacts { Task<IReadOnlyDictionary<Guid, HrMemberContact>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken); }`, `record HrMemberContact(bool HasPhone, bool HasEmail)` (adapter reuses what `TaskNoticeRecipients` reads: `MembershipPhoneReader`, `IUserDirectory`)

Routes (group `/api/v1/tenants/{tenantId:guid}/hr`):
- `GET /birthday-notices` → `.RequireAuthorization(HrPermissions.EmployeeRead)`, 200 `BirthdayNoticesDto`, 403.
- `PUT /birthday-notices` → `.RequireAuthorization(HrPermissions.SettingsManage)`, accepts `BirthdayNoticeRequest`, 200, 403, 412, 422, 428.

Handler rules (from the spec §5.1): authorize (`EmployeeRead`, + `SettingsManage` on PUT) before anything; PUT with no row requires `If-Match`
absent (present → 412 `concurrency.conflict`), with a row `HrIfMatch.Require` + `HrSettingsSupport.EnsureVersion`; managers must be in
`IHrMemberDirectory.FindAsync` active → else `manager_not_found`; in `ListWithPermissionAsync(EmployeeRead)` → else `manager_without_access`;
connection checked with `IHrWhatsAppConnections` only when WhatsApp is enabled → else `connection_invalid`. `UpdatedByMemberId` =
`IHrMemberDirectory.FindBySubjectAsync(tenantId, executionContext.SubjectId)`. Audit with field names in the same `SaveChangesAsync`.
GET with no row → `LeadDays` 1, channels off, empty managers, `Version` null. `BirthdayNoticesDto` carries a comment: the `Has*` flags exist
so the screen warns without extra calls (BFF).

- [ ] **Step 1: Write the failing tests**

Validator: `LeadDaysOutOfRangeFails` (-1 and 8 → field `leadDays`), `MoreThan20ManagersFails` (field `managerMemberIds`), `NullListIsRequired`.
API: `GetWithoutRowAnswersDefaults`, `FirstPutCreatesWithoutIfMatch` (200, `version` 1), `FirstPutWithIfMatchIs412`, `SecondPutWithoutIfMatchIs428`,
`StalePutIs412`, `UnknownManagerIs422ManagerNotFound`, `ManagerWithoutHrReadIs422ManagerWithoutAccess`, `WhatsAppWithoutConnectionIs422ConnectionRequired`,
`ForeignConnectionIs422ConnectionInvalid`, `OtherTenantIs403`, `PutWithoutSettingsManageIs403`, `PutWritesAudit` (row in `hr` audit with `managerMemberIds`).
Bootstrapper: `OnlyActiveWhatsAppConnectionsOfTheTenantAreValid`.

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests; dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → missing types.
- [ ] **Step 3: Implement** the files above; migration with `dotnet ef migrations add AddBirthdayNoticeSettings` (project `src/Modules/Hr/Modules.Hr.Infrastructure`, startup `src/Api`).
- [ ] **Step 4: GREEN** — `dotnet build` (solution) → 0 errors.
- [ ] **Step 5: Commit** — `feat(hr): add birthday notice settings endpoints`

### Task 3: The daily run (HR Infrastructure)

**Files:**
- Create: `src/Modules/Hr/Modules.Hr.Application/BirthdayNotices/BirthdayDigest.cs` (pure composer)
- Create: `src/Modules/Hr/Modules.Hr.Infrastructure/Reminders/HrBirthdayNoticesProcessor.cs`, `BirthdayRunStore.cs`, `BirthdayDigestStore.cs`, `BirthdayEventPublisher.cs`
- Modify: `src/Modules/Hr/Modules.Hr.Infrastructure/Reminders/HrMedicalExamsReminderWorker.cs` → after each tenant's medical run, also run `IHrBirthdayNoticesProcessor` in its own scope and try/catch (one tenant list query; tenants without a setting row exit at once)
- Modify: `HrDbContext.cs` + migration `AddBirthdayNoticeRuns` (tables `hr.birthday_notice_runs` PK `(tenant_id, run_date)`, `hr.birthday_notice_digests` PK `(tenant_id, member_id, run_date)`, same columns and status CK as `tenant_runs` / `daily_digests`)
- Modify: `HrInfrastructureExtensions.cs` (scoped registrations)
- Test: `tests/Modules/Hr/Modules.Hr.UnitTests/BirthdayDigestTests.cs`, `tests/Modules/Hr/Modules.Hr.IntegrationTests/BirthdayNoticesRunTests.cs`

**Interfaces:**
- Consumes: Task 1 `BirthdayCalendar`, Task 2 repository.
- Produces (consumed by Task 5, exact JSON camelCase):
  - `public sealed record BirthdayPerson(Guid EmployeeId, string EmployeeName);`
  - `public sealed record BirthdayDigestPayload(Guid TenantId, Guid MemberId, DateOnly RunDate, IReadOnlyList<BirthdayPerson> Today, int TodayTotal, IReadOnlyList<BirthdayPerson> Upcoming, int UpcomingTotal, DateOnly? UpcomingDate, BirthdayDigestChannels Channels, DateTimeOffset ExpiresAt);`
  - `public sealed record BirthdayDigestChannels(Guid? WhatsAppConnectionId, bool Email, Guid SentByMemberId);`
  - `public static class BirthdayDigest { public const int MaxNames = 50; public static (IReadOnlyList<BirthdayPerson> Today, int TodayTotal, IReadOnlyList<BirthdayPerson> Upcoming, int UpcomingTotal, DateOnly? UpcomingDate) Compose(IReadOnlyList<(Guid Id, string Name, DateOnly BirthDate)> activeEmployees, DateOnly today, int leadDays); }` (ordered by name, ordinal ignore-case; `UpcomingDate` null when `leadDays` is 0)
  - `public interface IHrBirthdayNoticesProcessor { Task ProcessTenantAsync(Guid tenantId, CancellationToken ct); }`

Processor rules: same gates as `HrMedicalExamsReminderProcessor` (`SendHourLocal`, tenant `Active`, module `hr`); no setting or empty managers →
return before claiming; claim run; read `Active` employees with `BirthDate` (projection: id, `FirstName + " " + LastName`, birth date);
nothing to announce → finish run; recipients = setting managers ∩ `ListWithPermissionAsync(EmployeeRead)`; per manager claim digest, publish,
mark sent/failed (same structure as `SendDigestsAsync`). `ExpiresAt` = start of `today + 1` in the tenant time zone, as UTC (via `TenantCalendar`).
`WhatsAppConnectionId` in the event only when `WhatsAppEnabled`.

- [ ] **Step 1: Write the failing tests**

`BirthdayDigestTests`: `TodayAndUpcomingAreSeparated` (lead 1), `LeadZeroHasNoUpcoming` (`UpcomingDate` null, empty list),
`MoreThan50AreCappedWithRealTotal` (60 → 50 names, total 60), `SortedByName`.
`BirthdayNoticesRunTests`: `PublishesOneEventPerManagerWithHrAccess` (two managers, one with access → one outbox row `hr.birthdays.digest.v1`),
`ManagerWhoLostAccessIsSkipped` (Review Focus 1), `NothingToAnnouncePublishesNothing`, `RetiredEmployeeIsNotAnnounced`,
`EmployeeWithoutBirthDateIsIgnored`, `RunsOncePerDay` (second call publishes nothing), `BeforeSendHourDoesNothing`,
`PayloadHasNoBirthDate` (payload JSON has no `birthDate` key and no year), `MedicalRunIsUntouched` (`hr.tenant_runs` gets no row from the birthday run).

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Hr/Modules.Hr.UnitTests; dotnet build tests/Modules/Hr/Modules.Hr.IntegrationTests` → missing types.
- [ ] **Step 3: Implement** the files above.
- [ ] **Step 4: GREEN** — `dotnet build` → 0 errors.
- [ ] **Step 5: Commit** — `feat(hr): publish the daily birthday digest`

**Block A review:** one read-only reviewer over Tasks 1–3 (no tests run).

## Block B — Messaging, Notifications, Bootstrapper (one implementer, Tasks 4–6)

### Task 4: System template `HrBirthdayNotice` (Messaging)

Mirror every `TaskNotice` piece (inventory: `TaskNoticeTemplateProvisioning.cs`, `SystemTemplateDrafts.cs`, `TaskNoticeOutbound.cs`,
`TaskNoticeTemplateReadiness.cs`, `TemplateManagement.cs`, `MessagingDbContext.cs`, `MessagingSaveChanges.cs`, `TemplateManagementEndpoints.cs`).

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` (`MessageTemplateKind.HrBirthdayNotice` last; `MessageTemplateName.HrBirthdayNoticeName = "qep_cumpleanos_equipo"`, prefix `"qep_cumpleanos_equipo_"`, both in `IsReserved`; `AdoptAsHrBirthdayNotice(DateTimeOffset)`)
- Modify: `MessagingErrorCodes.cs` (`TemplateHrBirthdayNoticeExists = "messaging.template.hr_birthday_notice_exists"`, `TemplateHrBirthdayNoticeConflict = "messaging.template.hr_birthday_notice_conflict"`), `MessagingAuditActions` (`TemplateHrBirthdayNoticeProvisioned = "messaging.template.hr_birthday_notice_provisioned"`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/HrBirthdayNoticeTemplateProvisioning.cs`, `HrBirthdayNoticeOutbound.cs`
- Modify: `SystemTemplateVariables.cs` (`HrBirthdayNotice` list: `nombre_contacto`, `hoy`, `fecha`, `proximos` — **no** fixed `asesor`/`empresa`/`saludo`), `SystemTemplateDrafts.cs` (query + handler), `TemplateManagementPorts.cs` (readiness port, `FindHrBirthdayNoticeAsync`), `TemplateManagementDtos.cs` (`HrBirthdayNoticeTemplateStatusDto`, optional last parameter of `TemplateManagementDto`), `TemplateManagement.cs`
- Modify/Create: `MessageTemplateRepository.cs`, `Persistence/HrBirthdayNoticeTemplateReadiness.cs`, `MessagingDbContext.cs` (`UX_message_templates_hr_birthday_notice`, CK `CK_message_templates_kind`), `MessagingSaveChanges.cs`, migration `AddHrBirthdayNoticeTemplateKind`, `MessagingInfrastructureExtensions.cs`
- Modify: `TemplateManagementEndpoints.cs` (`POST /hr-birthday-notice`, `GET /hr-birthday-notice/draft`, `messaging.template.manage`), `QepServiceCollectionExtensions.cs` (handlers)
- Modify: `README.md` (template-management routes)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/HrBirthdayNoticeTemplateDefinitionTests.cs`, `HrBirthdayNoticeOutboundTests.cs`, update `MessageTemplateKindCheckMigrationTests` expectations if they list kinds; `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/HrBirthdayNoticeTemplateProvisioningApiTests.cs`

**Interfaces:**
- Produces:
  - `HrBirthdayNoticeTemplateDefinition` with `Name`, `Language = "es"`, `Category = "UTILITY"`, keys `TodayKey = "hoy"`, `DateKey = "fecha"`, `UpcomingKey = "proximos"`, and
    `BaseBody = "Hola {{nombre_contacto}}, estos son los cumpleaños del equipo. Hoy: {{hoy}}. Próximos ({{fecha}}): {{proximos}}. Puedes ver el detalle en la campana de QEP."`
  - `public interface IHrBirthdayNoticeTemplateReadiness { Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken); Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken); }`
  - `public sealed record HrBirthdayNoticeSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string WaId, string RecipientName, ApprovedTemplate Template, IReadOnlyDictionary<string,string> Values);`
  - `public interface IHrBirthdayNoticeOutbound { Task<TaskNoticeSendResult> SendAsync(HrBirthdayNoticeSend send, CancellationToken ct); }` (reuses `TaskNoticeSendResult`; wraps `PlainTemplateOutbound` like `TaskNoticeOutbound`)

- [ ] **Step 1: Write the failing tests** — Definition: `BaseBodyUsesOnlyItsFourVariables`, `NameForAndVersionOfRoundTrip`, `IsAdoptableRejectsHeaderOrButtons`.
  Outbound: `SendsTheApprovedTemplateWithTheGivenValues`. API: `ProvisionCreatesPendingTemplate` (201, `kind` `HrBirthdayNotice`), `SecondProvisionIs422Exists`,
  `DraftAnswersTheBaseBody`, `TemplateManagementCarriesTheStatus`, `WithoutTemplateManageIs403`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests; dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests` → missing types.
- [ ] **Step 3: Implement** by mirroring the `TaskNotice` pieces.
- [ ] **Step 4: GREEN** — `dotnet build` → 0 errors.
- [ ] **Step 5: Commit** — `feat(messaging): add the hr birthday notice system template`

### Task 5: Bell entry and delivery rows (Notifications)

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Domain/InAppNotificationKind.cs` (`HrBirthdays`)
- Create: `src/Modules/Notifications/Modules.Notifications.Domain/HrBirthdayDelivery.cs` (entity + `HrBirthdayDeliveryStatus { Pending, Sent, Failed, Skipped }` + `HrBirthdayDeliveryChannels { WhatsApp = "whatsapp", Email = "email" }` + skip reasons `stale`, `no_phone`, `no_email`, `member_inactive`, `attempts_exhausted`)
- Modify: `InAppNotificationMapper.cs` (`HrBirthdays => ["runDate","today","todayTotal","upcoming","upcomingTotal","upcomingDate"]`)
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/InApp/HrInAppConsumer.cs` (third event; `OnInsertedAsync` inserts deliveries)
- Modify: `NotificationsDbContext.cs` + migration `AddHrBirthdayDeliveries` (table per spec §5.3 plus `sent_by_member_id`, `expires_at`; unique `(tenant_id, member_id, run_date, channel)`)
- Test: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InAppHrBirthdaysTests.cs`, `tests/Modules/Notifications/Modules.Notifications.UnitTests/NotificationsDbContextMappingTests.cs` (new table)

**Interfaces:**
- Consumes: Task 3 payload JSON.
- Produces: `HrBirthdayDelivery` (`Id`, `TenantId`, `MemberId`, `RunDate`, `Channel`, `ConnectionId`, `SentByMemberId`, `PayloadJson` = the `today/upcoming/totals/upcomingDate` part, `Status`, `ResultCode`, `Attempts`, `ClaimedUntil`, `ExpiresAt`, `CreatedAt`, `CompletedAt`), DbSet `HrBirthdayDeliveries`.

Rows are inserted with raw SQL `ON CONFLICT DO NOTHING` inside `OnInsertedAsync` (as `SupportInAppConsumer` does): a WhatsApp row only when
`channels.whatsAppConnectionId` is present, an email row only when `channels.email` is true.

- [ ] **Step 1: Write the failing tests** — `DigestWritesOneBellEntryPerManagerAndDay`, `DigestWritesOneDeliveryPerEnabledChannel` (both on → 2 rows; email only → 1),
  `ReplayedDigestChangesNothing` (Review Focus 4), `BellDataHasNoBirthDate`, `MapperKeepsTheBirthdayKeys`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests` → missing types.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `dotnet build` → 0 errors.
- [ ] **Step 5: Commit** — `feat(notifications): record birthday digests in the bell`

### Task 6: Sending WhatsApp and email (Notifications + Bootstrapper)

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/HrBirthdays/HrBirthdayPorts.cs`, `HrBirthdayDigestEmailTemplate.cs`, `HrBirthdayWhatsAppText.cs`
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/AccountLinks.cs` (`HrEmployee(string appBaseUrl, Guid employeeId)` → `/hr/employees/{id:D}`)
- Create: `src/Modules/Notifications/Modules.Notifications.Infrastructure/HrBirthdays/HrBirthdayDeliveryProcessor.cs`, `HrBirthdayDeliveryWorker.cs`
- Modify: `NotificationsInfrastructureExtensions.cs` (processor scoped, worker hosted)
- Create: `src/Bootstrapper/HrBirthdayWhatsAppSender.cs`; Modify: `QepServiceCollectionExtensions.cs` (`IHrBirthdayWhatsApp`)
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/HrBirthdayDigestEmailTemplateTests.cs`, `HrBirthdayWhatsAppTextTests.cs`; `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/HrBirthdayDeliveryTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/HrBirthdayWhatsAppSenderTests.cs`

**Interfaces:**
- Consumes: Task 4 readiness + outbound; Task 5 entity; existing `ITaskNoticeRecipients`, `IEmailChannel`.
- Produces:
  - `public sealed record HrBirthdayWhatsAppSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string PhoneE164, string RecipientName, string Today, string UpcomingDate, string Upcoming);`
  - `public interface IHrBirthdayWhatsApp { Task<TaskNoticeWhatsAppResult> SendAsync(HrBirthdayWhatsAppSend send, CancellationToken ct); }` (reuses result types and codes `template_not_approved`, `connection_unavailable:<r>`, `send_failed:<c>`)
  - `public static class HrBirthdayWhatsAppText { public static string Names(IReadOnlyList<string> names, int total, int maxLength); }` → «nadie» when empty; «Ana, Luis y 3 más» when `total` > names shown or the joined text exceeds `maxLength` (200).
  - `public static class HrBirthdayDigestEmailTemplate { public const string TemplateRef = "hr.birthdays-digest.v1"; public static EmailMessage Render(string to, IReadOnlyList<(Guid Id, string Name)> today, int todayTotal, IReadOnlyList<(Guid Id, string Name)> upcoming, int upcomingTotal, DateOnly? upcomingDate, string appBaseUrl); }`
    Subject «Cumpleaños de hoy en tu equipo» when `today` is not empty, else «Próximos cumpleaños en tu equipo»; one link per employee via `AccountLinks.HrEmployee`; names HTML-encoded; «y N más» when total > shown.

Processor rules (mirror `TaskNoticeEscalationProcessor`: claim with lease + `FOR UPDATE SKIP LOCKED`, `MaxAttempts` 3, ownership check, renew before send):
`now >= ExpiresAt` → `Skipped stale`; recipient via `ITaskNoticeRecipients` (null/inactive → `Skipped member_inactive`); WhatsApp needs `PhoneE164`
(else `Skipped no_phone`), `ClientId` = delivery id; email needs `Email` (else `Skipped no_email`); an exception sending is `Failed` with its code;
no presence deferral, no read check, no fallback between channels.

- [ ] **Step 1: Write the failing tests** — Text: `EmptyIsNadie`, `ManyAreFoldedWithYNMas`, `TotalAboveShownAddsYNMas`.
  Email: `SubjectIsTodayWhenThereAreBirthdaysToday`, `SubjectIsUpcomingWhenOnlyUpcoming`, `NamesAreHtmlEncoded`, `EachEmployeeLinksToTheProfile`.
  Delivery (integration): `WhatsAppIsSentOnceWithTheDeliveryIdAsClientId`, `EmailIsSent`, `NoPhoneIsSkipped`, `NoEmailIsSkipped`,
  `InactiveMemberIsSkipped`, `ExpiredIsSkippedStale` (Review Focus 5), `FailedWhatsAppDoesNotBlockEmail`, `AttemptsExhaustedFails`.
  Sender: `NotApprovedTemplateFails`, `FillsTheFourVariables`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Notifications/Modules.Notifications.UnitTests; dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests; dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → missing types.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — `dotnet build` → 0 errors.
- [ ] **Step 5: Commit** — `feat(notifications): send birthday digests by whatsapp and email`

**Block B review:** one read-only reviewer over Tasks 4–6.

## Close

- [ ] Final whole-branch review by a read-only subagent without implementation context (owner rule: architecture changes and HTTP contracts listed for owner authorization — the new routes are covered by the approved spec).
- [ ] Trello card: comment branch, last commit, «pruebas escritas, sin correr»; move to «Code Review».
- [ ] Hand the owner the `qep-frontend` prompt (spec §9 + the final contract: routes, DTO fields, error codes, bell kind and data keys, template-management routes).
