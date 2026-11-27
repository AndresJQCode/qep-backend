# Employee birthday notices (HR) — design

Status: approved by the owner on 2026-11-26. Repo: `qep-backend`. Frontend counterpart: card https://trello.com/c/4kvFw8GV.

## 1. Intent

The owner wants the platform to remind one or more **designated members** of a tenant ("birthday managers") when an employee's
birthday is coming up, so that person sends the congratulation email or tells the coworkers. The system **does not** congratulate
the employee: it only reminds the manager. Managers must have access to HR.

## 2. Goals, non-goals, anti-goals

Goals

- A daily digest per manager listing the employees whose birthday is **today** and those whose birthday is **exactly N days ahead**
  (N = lead days, configurable per tenant, default 1, `0` = only the same day).
- Delivered by the bell (always), and optionally by **WhatsApp** and/or **email**, each toggled per tenant.
- The tenant picks the managers and the WhatsApp connection in one HR setting.

Non-goals

- Sending anything to the employee or to coworkers automatically.
- Weekly digests, per-employee opt-out, or birthdays of members who are not employees.
- Escalation-if-unread (the task-notice model): channels go out together, not as fallbacks.

Anti-goals (failure even if it "works")

- A member without `hr.employee.read` receiving employee data (names, birthday).
  Accepted exception (owner, 2026-11-27): the WhatsApp digest goes out through Messaging, which stores it in a conversation
  with the manager's phone, so inbox users may read it without HR access — the same behavior as the Support task notices.
  It carries only names and day/month (no year, no age). A send path that leaves no inbox trace is separate work.
- Exposing the birth **year** or age anywhere in the notice.
- One WhatsApp per employee (cost): it is one message per manager per day, whatever the number of birthdays.
- The same digest sent twice to a manager (replicas, retries).

## 3. Constraints (verified in the code)

- `Employee.BirthDate` (`DateOnly?`, column `hr.employees.birth_date`) is **optional**: an employee without it is never announced.
- `Employee.Status`: `Active`, `Retired`, `Deactivated`. Only `Active` employees are announced.
- Existing daily-run pattern in HR: `HrMedicalExamsReminderWorker` → `HrMedicalExamsReminderProcessor`, gated by `Hr:SendHourLocal`
  (default 8, tenant local time), tenant `Active`, module `hr` enabled, `TenantRunStore` (one run per tenant/day) and
  `DailyDigestStore` (one digest per member/day, lease-based claim). Events go out through HR's outbox and become bell entries in
  `Notifications` (`HrInAppConsumer`, dedupe key per day).
- `hr.daily_digests` and the tenant run table are keyed without a "kind": they belong to the medical-exam digest.
- Members: `Membership.PhoneE164` is optional; the account email is resolved by `ITaskNoticeRecipients` (Notifications port,
  returns `IsActive`, `DisplayName`, `Email`, `PhoneE164`).
- WhatsApp to a member already exists for task notices: `TaskNoticeWhatsAppSender` (Bootstrapper) sends an approved system template
  of `MessageTemplateKind.TaskNotice` through `ITaskNoticeOutbound`, idempotent by `ClientId`. System templates are provisioned per
  connection (`ProvisionTaskNoticeTemplateHandler`, readiness port, draft query, status in the template-management DTO).
- Email: `IEmailChannel` in Notifications; templates are static renderers (`SupportTaskAssignedEmailTemplate`), links built with
  `AccountLinks` on `Notifications:AppBaseUrl`.
- HR settings: `GET /api/v1/tenants/{tenantId}/hr/settings` needs `hr.employee.read`; writes need `hr.settings.manage` (and the
  handler also requires `hr.employee.read`), each `PUT` with `If-Match`.
- Frontend employee detail route: `/hr/employees/{employeeId}` (verified in `qep-frontend` `develop`, `routeTree.gen.ts`).
- A new permission is not needed (owner chose explicit managers, option C).

## 4. Approaches considered

Who chooses the managers

- A. New permission `hr.birthday.notify` — rejected: configuration split between roles and settings; a broad role would fan out
  WhatsApp messages unexpectedly.
- B. Reuse `hr.employee.read` — rejected: notifies too many people, duplicates the congratulation.
- **C. Explicit list in an HR birthday setting — chosen by the owner.** The screen is needed anyway for the WhatsApp connection.

When

- **A. Same day + lead days, configurable (default 1) — chosen by the owner.** B (same day only) and C (weekly + daily) rejected.

Who sends WhatsApp/email

- **1. Notifications sends (chosen by the owner).** HR publishes one digest event per manager; Notifications writes the bell entry and
  the channel deliveries, and sends through ports implemented in Bootstrapper (as task notices do). HR knows nothing about WhatsApp.
- 2. HR sends from its worker — rejected: HR would own Meta failures and retries; no business module sends on its own today.
- 3. Reuse the `TaskNotice` template — rejected: its body speaks of a ticket and its variable is `numero`.

## 5. Design

### 5.1 HR: the setting

New aggregate `BirthdayNoticeSetting` (one row per tenant, table `hr.birthday_notice_settings`), own `Version`:

| Field | Rule |
|---|---|
| `ManagerMemberIds` | 0..20 member ids, distinct. Empty list = feature off. |
| `LeadDays` | 0..7, default 1. |
| `WhatsAppEnabled` + `WhatsAppConnectionId` | Connection required when enabled; must be a WhatsApp connection of the tenant. |
| `EmailEnabled` | bool. |
| `UpdatedByMemberId` | The member who last saved it; author of the WhatsApp messages (§5.3). |

No row = feature off (the GET answers the defaults: empty managers, `LeadDays` 1, both channels off, `version` null).

Endpoints (in `HrEndpoints`, group `/api/v1/tenants/{tenantId}/hr`):

- `GET /birthday-notices` — `hr.employee.read` (+ handler revalidates tenant and permission). Answers the setting with each manager
  as `{ memberId, displayName, hasPhone, hasEmail, hasHrAccess }` so the screen can warn ("sin teléfono: no le llegará el WhatsApp",
  "ya no tiene acceso a RRHH") without extra calls. BFF: the reason is written on the DTO.
- `PUT /birthday-notices` — `hr.settings.manage` + `hr.employee.read`. Upsert: with no row, the PUT creates it and `If-Match` must
  be **absent** (sending one answers 412: the screen thinks there is a version that does not exist); with a row, `If-Match` is
  required (428 without it, 412 if stale), read with `HrIfMatch.Require`. Body
  `{ managerMemberIds, leadDays, whatsAppEnabled, whatsAppConnectionId, emailEnabled }`. Answers the same DTO as the GET.
- Validation: FluentValidation for shape (field map `errors`); domain codes for rules:
  - `hr.birthday_notice.manager_not_found` — a member id that is not an active member of the tenant (422, field `managerMemberIds`).
  - `hr.birthday_notice.manager_without_access` — a member without `hr.employee.read` (422, field `managerMemberIds`).
  - A manager without phone (WhatsApp on) or without email (email on) is **not** an error: the GET flags it with `hasPhone` /
    `hasEmail` and the delivery is skipped with its reason.
  - `hr.birthday_notice.connection_required` — WhatsApp enabled without connection (422, field `whatsAppConnectionId`).
  - `hr.birthday_notice.connection_invalid` — not a WhatsApp connection of the tenant (422, field `whatsAppConnectionId`).
- Audit (`IHrAuditPublisher`, field names only) in the same unit of work.
- Connection check: new HR port `IHrWhatsAppConnections` implemented in Bootstrapper over Messaging's connection directory.
- The setting is **not** added to `HrSettingsDto` (it is a separate screen block; keeps the existing DTO stable).

### 5.2 HR: the daily run

New `HrBirthdayNoticesProcessor`, run by the same worker tick as the medical exams (or a sibling worker with the same shape — builder's
call), with the same gates: from `Hr:SendHourLocal`, tenant `Active`, module `hr` enabled.

- Deduplication in **new tables**, not the medical-exam ones (their keys have no kind): `hr.birthday_notice_runs`
  (tenant, run_date) and `hr.birthday_notice_digests` (tenant, member, run_date), same claim/lease/`FailStale` semantics as
  `TenantRunStore` / `DailyDigestStore`. (Builder may generalize the existing stores with a `kind` column instead, if the migration
  keeps the medical-exam rows intact; it is not required.)
- Birthdays of a date `d`: `Active` employees with `BirthDate` whose month/day equals `d`'s; **29 February** birthdays are announced on
  28 February in non-leap years. Computed in SQL or in memory per tenant (tenants have few employees; builder's call).
- `today` = tenant calendar date; `upcomingDate = today + LeadDays` (only when `LeadDays > 0`).
- Nothing to announce (both lists empty) → nothing published, run closed.
- Recipients: the setting's managers **intersected** with `ListWithPermissionAsync(tenantId, hr.employee.read)` at run time; a manager
  who lost access or is no longer active is skipped silently (the GET shows `hasHrAccess: false`).
- One outbox event per manager: `hr.birthdays.digest.v1`

```json
{
  "tenantId": "…", "memberId": "…", "runDate": "2026-11-26",
  "today":    [{ "employeeId": "…", "employeeName": "Ana Pérez" }],
  "upcoming": [{ "employeeId": "…", "employeeName": "Luis Gómez" }],
  "upcomingDate": "2026-11-27",
  "todayTotal": 1, "upcomingTotal": 1,
  "channels": { "whatsAppConnectionId": "…" | null, "email": true, "sentByMemberId": "…" },
  "expiresAt": "2026-11-27T05:00:00Z"
}
```

  Names only; never the birth date or age. Lists ordered by name, **capped at 50 per group**, with `todayTotal` /
  `upcomingTotal` carrying the real counts so the bell, the email and WhatsApp can say «y N más». The channels travel in the event (the setting at run time), so
  Notifications does not read HR's tables.

### 5.3 Notifications: bell + deliveries

`HrInAppConsumer` handles `hr.birthdays.digest.v1` too (or a sibling consumer — builder's call):

- Bell entry `InAppNotificationKind.HrBirthdays`, resource null, data
  `{ runDate, today: [...], upcoming: [...], upcomingDate }`, dedupe key `hr-birthdays:{runDate}`. Mapper projection keys added.
- In the **same transaction**, one row per enabled channel in a new table `notifications.hr_birthday_deliveries`
  (`id`, `tenant_id`, `member_id`, `run_date`, `channel` `whatsapp|email`, `connection_id`, `payload` jsonb, `status`
  `Pending|Sent|Failed|Skipped`, `result_code`, `attempts`, `claimed_until`, `created_at`, `completed_at`), unique
  (`tenant_id`, `member_id`, `run_date`, `channel`), inserted with `ON CONFLICT DO NOTHING` (a replayed event changes nothing).
- New `HrBirthdayDeliveryWorker` + processor calqued on `TaskNoticeEscalationProcessor`: lease claim with `FOR UPDATE SKIP LOCKED`,
  `MaxAttempts` 3, re-check the member right before sending (`ITaskNoticeRecipients`; inactive → `Skipped member_inactive`), renew
  ownership before sending. **No presence deferral and no read check**: the owner wants the channels regardless of the bell.
- **Stale cutoff:** a delivery still `Pending` once its `run_date` is over in the tenant's time zone becomes `Skipped stale`
  (a «Hoy» sent the next day would be wrong). HR computes `expiresAt` (end of the run date in the tenant time zone, as UTC) and it travels in the event and the row (`expires_at`).
- WhatsApp: needs `PhoneE164` (else `Skipped no_phone`) and an approved `HrBirthdayNotice` template on the connection (else
  `Failed template_not_approved`). Sent through a new port `IHrBirthdayWhatsApp` implemented in Bootstrapper
  (`HrBirthdayWhatsAppSender`, calqued on `TaskNoticeWhatsAppSender`), `ClientId` = delivery id (idempotent). Messaging's outbound
  requires an author member (the message row stores it, `SentByMemberId` is a non-nullable `Guid`): the author is the member who
  last saved the setting (`BirthdayNoticeSetting.UpdatedByMemberId`, travels in the event as `sentByMemberId`), the same way a task
  notice is authored by whoever assigned. The template does not use `{{asesor}}`, so that name never shows in the text.
- Email: needs the account email (else `Skipped no_email`). New renderer `HrBirthdayDigestEmailTemplate` (`hr.birthdays-digest.v1`):
  subject «Cumpleaños de hoy en tu equipo» or «Próximos cumpleaños en tu equipo» when only upcoming (no company name: it would
  need a new lookup, and the reader already knows their company); body lists both groups with a link per
  employee to `AccountLinks.HrEmployee(appBaseUrl, employeeId)` → `/hr/employees/{id}`; names HTML-encoded; tutea.
- Channels are independent: one failing never blocks the other or the bell.

### 5.4 Messaging: the system template

New `MessageTemplateKind.HrBirthdayNotice` (appended at the end: stored by name), with the same set of pieces as `TaskNotice`:
definition (name, language `es`, category `UTILITY`, base body, examples), provisioning command/handler + endpoint under
`template-management`, readiness port, draft query, outbound send, status in the template-management DTO, unique index per
connection, audit action.

Base body (Meta rejects line breaks inside variables; lists are joined with «, »):

> Hola {{nombre_contacto}}, estos son los cumpleaños del equipo. Hoy: {{hoy}}. Próximos ({{fecha}}): {{proximos}}. Puedes ver el
> detalle en la campana de QEP.

- Variables are only the four above; none of the fixed ones (`asesor`, `empresa`, `saludo`).
- Empty group → «nadie» (Meta rejects empty variables).
- A group longer than the variable limit → the first names and «y N más».
- Variable names follow `SystemTemplateVariables` conventions; the builder settles the exact keys.

### 5.5 Permissions, options, migrations

- No new permission.
- No new options: reuses `Hr:SweepMinutes`, `Hr:SendHourLocal`, `Notifications:AppBaseUrl`.
- Migrations: HR (setting + run/digest tables), Notifications (deliveries table), Messaging (kind/unique index if needed).
- Architecture: new ports are implemented in Bootstrapper; no new module-to-module reference. `HrLayerTests` must stay green;
  any change there needs owner authorization.

## 6. Tests (TDD, RED before GREEN)

- Unit: birthday matching (same day, lead days, 29 Feb in leap/non-leap years, year boundary Dec 31 + 1), setting validation, email
  renderer (encoding, empty groups), WhatsApp variable folding («nadie», «y N más»).
- Integration (HR): GET defaults, PUT happy path, each error code, 403 cross-tenant, 412/428; processor publishes one event per
  eligible manager, none when empty, skips managers without access, one run per day.
- Integration (Notifications): consumer writes the entry + one delivery per enabled channel, replay is a no-op; processor sends
  WhatsApp/email, skip reasons, retries, idempotency.

## 7. Calls made by Claude (owner may correct)

- One digest per manager per day with all birthdays; upcoming = exactly `today + LeadDays`, not a range.
- Weekend birthdays are announced on their day; 29 Feb → 28 Feb in non-leap years.
- Only `Active` employees; employees without birth date are ignored.
- Up to 20 managers; lead days 0..7.
- Channels go out together with the bell (no escalation-if-unread).
- The email goes to the member's account email.
- No year or age anywhere.
- First PUT without `If-Match` creates the row; later ones require it.
- No company name in the email subject; no `{{asesor}}` in the template.
- Lists capped at 50 per group with real totals; deliveries expire when the run date ends (`Skipped stale`).
- Missing phone/email is a warning in the GET, not a PUT error.

## 8. Left to the builder

- Same worker vs sibling worker in HR; same consumer vs sibling consumer in Notifications.
- New dedupe tables vs generalizing the medical-exam stores.
- Exact template variable keys and truncation length.
- SQL vs in-memory birthday matching.

## 9. Frontend (prompt to `qep-frontend`, after backend merge)

- HR settings block «Cumpleaños»: managers picker (members with HR access; `GET /hr/members-available` or the membership list —
  to confirm), lead days, WhatsApp toggle + connection select + template status/provision button, email toggle; warnings from
  `hasPhone`/`hasEmail`/`hasHrAccess`.
- Bell renderer for `HrBirthdays`.
- Template-management screen: the new system template.
