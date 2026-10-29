# Messaging statistics report — design

Date: 2026-10-28. Decided by the owner where marked "(owner)"; everything else is a recommendation
to ratify. Cross-repo: endpoint in `qep-backend` (module Messaging), screen in `qep-frontend`
(`features/reports`). Per `SDD-ADR-08` this becomes **two slices** with their own IDs, one per ledger.

## Why

Reports today cover orders, quotations, price changes and customers. Nothing answers "how well do we
attend our inbox": how many conversations, how many are new people, and how long we take to answer.
Response time only means something against the schedule the business promises, so the report counts
**business hours**, not wall-clock.

## Decisions

- D-1 (owner) Visible to anyone with `messaging.conversation.read` **and** tenant module `messaging`.
  No new permission.
- D-2 (owner) The user picks a date range (`from`/`to`, `DateOnly`, tenant timezone) and a grouping:
  Día / Semana / Mes. Weeks start on **Monday** (ISO).
- D-3 (owner) **Conversations are sessions**: a conversation's first opening plus each `Reopened`
  event, bucketed by session start. Session start uses `created_at` (ingestion time), **never**
  `occurred_at`: Meta redelivers late (`reentrega-tardia-meta-reabre-resuelta`; `InboundIngestion.cs:220`).
- D-4 (owner) **New contact** = a conversation whose *first inbound* message (direction 1) falls in the
  period. Outbound-first openers (campaigns, quotation sends) do not count until the contact writes.
  Split by channel. **Returning contact** = contact with a session start in the period that is not new.
- D-5 (owner) Two response times, both from the session-start inbound to the first qualifying
  outbound inside that session: **first response** (any outbound, chatbot included) and **first
  advisor response** (`sent_by_kind = Member`).
- D-6 (owner) Time counts **business hours only**; with no schedule applicable, wall-clock.
- D-7 Computation is **SQL for extraction, C# for time math** (§Computation).
- D-8 The backend **zero-fills** the series. BFF rule: the browser never computes. Reports with
  `groupBy=month` elsewhere return only months with rows and the frontend fills; that was acceptable
  for a single fixed grouping, but day/week with a user-chosen range makes the frontend own calendar
  logic (ISO weeks, tz). One implementation, in the server, next to `TenantCalendar`.
- D-9 Range limits, 422 beyond them: day ≤ 92 days, week ≤ 53 weeks, month ≤ 24 months.
- D-10 Lives in **Messaging**, not Reporting: `ArchitectureTests` forbid Reporting → Messaging.
- D-11 The route needs module `messaging` only; the `reporting` module key is **not** required
  (§Module gating, decided).
- D-12 Campaign sends and system/event messages never count as responses.
- D-13 `channelsWithData` (owner, 2026-10-29): the frontend hides Instagram when the tenant has no Instagram
  integration, unless there is Instagram data in the period (connected, then disconnected). The backend says
  which channels had activity; the browser never infers it. It is a top-level array of `"WhatsApp"` /
  `"Instagram"`, in that fixed order, only those present: channels with at least one **session** (same
  definition as `totals.conversations`: conversation created or `Reopened`, by `created_at`) in the current
  **or** the previous period. It **ignores the `channel` filter**, so the options stay stable when the user
  filters by one channel. `[]` when there is no data. One extra tenant/`created_at`-bounded query over the
  contiguous previous+current range (`IMessagingReportSource.ChannelsWithDataAsync`), served by the report indexes.

## Findings that shape the design (verified in code)

### Business hours model

- A schedule is `BusinessHoursSchedule` (`Modules.Messaging.Domain/BusinessHoursSchedule.cs:9`),
  **many per tenant**, named, at most one `IsDefault` (`:30`). Week = always 7 entries, day 1 = Monday
  … 7 = Sunday (`:230-252`), up to 3 ranges per day (`MaxRangesPerDay`, `:12`), each range in
  **minutes from local midnight** `[From, To)` with `To ≤ 1440`, no midnight crossing (`:287-309`).
- Date exceptions (`Exceptions`, `:36`, max 100, past ones dropped on save) and an optional Colombian
  holiday calendar (`HolidayCountry`, `:38`; `ColombianHolidays.For(year)`). Precedence per local date
  (`EffectiveRanges`, `:182-197`): exception → holiday (closed all day) → weekday.
- Assignment is **per connection**: `ConnectionBusinessHours` (`ConnectionBusinessHours.cs:6`,
  key `ConnectionId`). Effective schedule of a connection: assignment → tenant default → **none =
  always open** (`BusinessHoursRepository.GetStatusesAsync`, `BusinessHoursRepository.cs:74-105`;
  `IBusinessHours`, `BusinessHoursPorts.cs:41`, D-H4/D-H10). "Not configured" is therefore the absence
  of both rows; there is no "unconfigured" flag.
- Timezone: the **tenant's** (`ITenantClock.GetAsync` → `TenantCalendar.TimeZone`,
  `BusinessHoursRepository.cs:83`). Schedules store no timezone.
- Existing reusable pieces: `IsOpenAt(instant, tz)`, `NextChangeAt(instant, tz)` (`:117,128`,
  14-day horizon) and the private `LocalToUtc` (`:203`, handles DST gaps/ambiguity). They answer
  "is it open" and "next flip", **not** "business seconds between two instants". The auto-reply gate
  (`AutomationBusinessHours.IsOpenAsync`, `Bootstrapper/AutomationBusinessHours.cs`) only calls
  `IsOpenAt` for *now*. We add the elapsed-time calculation; we do not reimplement the schedule rules.

### Messages and conversations schema

- `messaging.messages` (`MessageRecord.cs:7`): `direction` smallint 1 in / 2 out / 3 system
  (`MessageEnums.cs:26`); `kind` 11 Template, 13 Event (`:35`); `sent_by_kind` 1 Member / 2 Chatbot,
  default 1 (`MessageRecord.cs:25`, `MessageEnums.cs:18`); `sent_by_member_id`; `occurred_at` (Meta's
  clock) vs `created_at` (our clock, `MessageRecord.cs:13,38`). Existing indexes are per conversation
  (`MessagingDbContext.cs:321`); there is **no** index on `(tenant_id, created_at)`.
- Events are rows with `direction = 3, kind = 13` and `details->>'type'` (`ConversationEventRows.cs:20`,
  `ConversationEvent.cs:40`, `ConversationEventType.Reopened/Resolved`). `Reopened` is written from
  ingestion (`InboundIngestion.cs:191-195`, `created_at = now`, `occurred_at = message.OccurredAt - 3 ms`)
  and from the manual reopen (`ConversationLifecycle.cs:219`, which has an `Actor`).
- **Caveat:** a manual reopen by an advisor is a `Reopened` event with **no inbound**. D-3 says a
  session is each `Reopened`; we keep that, but the session has no start inbound (§Session model rule S3).
- `conversations.channel` (`'WhatsApp'|'Instagram'`, `MessagingDbContext.cs:27,190`), `assigned_member_id`
  (`:197`, current assignee, not historical), `created_at` (`:214`, set to ingestion `now` at birth,
  `InboundIngestion.cs:282-292`; outbound-first creation in `ConversationOpener.cs:41,50,108`).
- Campaign sends: `campaign_recipients.message_id` (`CampaignDispatchStore.cs:285`, index
  `IX_campaign_recipients_message`, `MessagingDbContext.cs:434`). Their message has kind 11,
  `sent_by_member_id = recipient.CreatedByMemberId` (`:270-271`) — it looks like a member send, so it
  **must** be excluded by the join.
- Instagram echo (`InboundIngestion.IngestEchoAsync`, `:41-48`): outbound, status Sent, **no author, no
  `client_id`**. Default `sent_by_kind` is 1, so an echo reads as `Member` with `sent_by_member_id NULL`.
- Member names: `IMessagingMemberNames.FindAsync(tenantId, ids)` (`IMessagingMemberNames.cs:7`),
  already used by messages and campaigns; missing member → not in the dictionary → «Miembro eliminado»
  (D-M20). No new port needed.
- Auth: `MessagingAuthorization.EnsureAsync(ctx, modules, tenantId, permission, ct)`
  (`MessagingSupport.cs:13-22`): 403 `authorization.denied` without permission, then
  `TenantModuleGuard` → 403 `tenancy.module_not_enabled`. `ReportingAuthorization` has no module guard
  (`ReportingAuthorization.cs:13`): Reporting masks its permissions via `RequiredModules` instead.

## Session model (precise definitions)

For a tenant, in `[Start, EndExclusive)` (tenant-local days → UTC via `calendar.StartOfDayUtc` /
`EndOfDayExclusiveUtc`, as `ReportPeriod.Of`, `ReportingFilters.cs:14-21`):

- S1 A **session** starts at the conversation's `created_at` (first session) or at a `Reopened` event's
  `created_at`. It ends at the next `Reopened` of the same conversation (exclusive) or never.
- S2 The session's **start inbound** is the first `direction = 1` message with `created_at ≥ session
  start` and `< next session start`. Ingestion inserts the `Reopened` event and the inbound in the same
  transaction with the same `now`, so `>=` on `created_at` catches it. Tie-break by `(created_at, id)`.
- S3 A session without a start inbound (outbound-first conversation not yet answered by the contact, or a
  manual advisor reopen) is **not a response-time session**: no response clock exists. It still counts as
  a **conversation** (D-3) but is excluded from unanswered and from both response-time distributions.
  Decided (owner): a manual reopen without inbound counts as a conversation but not as a response-time session.
- S4 **First response**: first `direction = 2` message in the session after the start inbound, excluding
  campaign messages (`message_id IN campaign_recipients`) and `kind = 13`/`direction = 3`. Status `Failed`
  outbounds do **not** count (nothing reached the contact). **First advisor response**: the same with
  `sent_by_kind = 1 AND sent_by_member_id IS NOT NULL`.
- S5 Member **template send** (`kind = 11`, member, not campaign — e.g. quotation template): counts as
  advisor response. It is a real human action; excluding it would punish the quotation flow.
- S6 **Instagram echo** (outbound, `sent_by_member_id NULL`, `sent_by_kind = 1`): counts as *first
  response* (the business replied) but **not** as *advisor response* (no identifiable author, so no
  advisor to credit). Rationale: the `NOT NULL` author is the discriminator.
- S7 **Unanswered** (per metric): session with a start inbound, no qualifying response, and the session
  is still current at report time. A session closed by a later `Reopened` without a response *is*
  unanswered. A session whose conversation is `Resolved` without any response is also unanswered
  (resolving is not answering). So: "no qualifying outbound within the session", evaluated at `UtcNow`.
- S8 Sessions whose start is in range are measured completely even if the response lands after `to`
  (read messages up to `UtcNow`).
- S9 New contact: conversation's first `direction = 1` message `created_at` in range, any session. Sessions
  in range whose conversation's first inbound is earlier → returning. A conversation is counted once as
  new, in the bucket of that first inbound.
- S10 **Per-advisor attribution**: the member of the first qualifying advisor response in the session
  (who actually answered), not the current `assigned_member_id`. «Unassigned unanswered» = sessions with no
  advisor response, attributed to the conversation's *current* `assigned_member_id` (null → «Sin asignar»).
  `sessionsAttended` = sessions with an advisor response by that member.

## Business-seconds clock

`BusinessHoursClock` — pure, in `Modules.Messaging.Application` (no I/O), unit-tested.

```
long BusinessSeconds(DateTimeOffset from, DateTimeOffset to,
                     Func<Guid?, BusinessHoursSchedule?> scheduleOf, // by connection
                     TimeZoneInfo tz)
```

- Returns 0 if `to ≤ from`. With `schedule == null` → `(to - from).TotalSeconds` (wall-clock, D-6).
- Otherwise iterate **local days** from `local(from).Date` to `local(to).Date`; per day take
  `EffectiveRanges(date)` (exposed as a public read-only `RangesFor(DateOnly)` on the aggregate; same
  precedence exception → holiday → weekday), convert each range's `From/To` minutes to instants with
  `LocalToUtc` (made `internal`/shared), clamp to `[from, to]`, sum the overlaps. Cost O(days × ranges),
  no minute sweep. A message arriving out of hours and answered before opening → 0 s.
- DST: Colombia has none, but conversion goes through tz for every boundary (gap → first valid instant,
  ambiguity → first occurrence, as `LocalToUtc` already does). A day with a 23/25 h length needs no
  special case because ranges are minutes-from-midnight mapped to instants.
- The schedule is resolved **per connection** (assignment → default → none), so a tenant with two
  connections and different schedules is correct. Schedules are loaded once per request
  (`IBusinessHoursRepository.ListAsync` + `ListAssignmentsAsync`; tens per tenant).
  `businessHoursApplied` is a plain boolean (decided): true if **at least one** session in scope used a schedule.
- Schedule edits are **not historical**: the report applies *today's* schedule to past sessions. Stated in
  the UI footnote; not versioning schedules.

## Computation (D-7)

One SQL query returns one row per session in the range (and the open window needed), computed with
`LEAD`/lateral lookups; C# does time math, bucketing, percentiles.

```sql
-- sketch, parameters: tenant, startUtc, endUtc, nowUtc, channel?
WITH starts AS (
  SELECT c.id AS conversation_id, c.connection_id, c.channel, c.assigned_member_id,
         c.created_at AS session_start, 'first' AS kind
  FROM messaging.conversations c WHERE c.tenant_id = @t
  UNION ALL
  SELECT m.conversation_id, c.connection_id, c.channel, c.assigned_member_id,
         m.created_at, 'reopen'
  FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
  WHERE m.tenant_id = @t AND m.direction = 3 AND m.kind = 13 AND m.details->>'type' = 'Reopened'
), sessions AS (
  SELECT *, LEAD(session_start) OVER (PARTITION BY conversation_id ORDER BY session_start) AS session_end
  FROM starts
)
SELECT s.*,
  (SELECT min(created_at) FROM messaging.messages i WHERE i.conversation_id = s.conversation_id
     AND i.direction = 1 AND i.created_at >= s.session_start
     AND (s.session_end IS NULL OR i.created_at < s.session_end))              AS start_inbound_at,
  -- first_any_out and first_member_out: lateral min over direction = 2, status <> 4, created_at > start_inbound,
  -- NOT EXISTS (campaign_recipients cr WHERE cr.message_id = o.id); member variant adds sent_by_member_id IS NOT NULL
  (SELECT m2.sent_by_member_id ...)                                            AS first_member_id
FROM sessions s
WHERE s.session_start >= @start AND s.session_start < @end AND (@channel IS NULL OR s.channel = @channel);
```

Plus a second small query: per conversation in range, the instant of its **first inbound ever**
(`min(created_at) WHERE direction = 1`), to classify new vs. returning (S9). It runs over the conversations
that have a session in range only.

- All timestamps are `created_at`; no `occurred_at` anywhere (D-3).
- The previous equal-length period runs the same two queries with the shifted range (length in local days;
  for `month` grouping the comparison period is the same number of days, not "previous month", to stay
  comparable with the other reports' comparison semantics — decided: same number of days).
- **Index (migration `AddMessagesTenantCreatedIndex`, Messaging):**
  `CREATE INDEX CONCURRENTLY IX_messages_tenant_created ON messaging.messages (tenant_id, created_at)
  INCLUDE (conversation_id, direction)` and a partial `IX_messages_events_reopened ON messages
  (tenant_id, created_at) WHERE direction = 3 AND kind = 13`. Conversations already have
  `(tenant_id, …)` indexes; `IX_conversations_tenant_*` suffice for the first-session branch only with a
  created_at filter, so add `IX_conversations_tenant_created (tenant_id, created_at)`. Migration follows
  the repo's existing convention for index creation (verify `CONCURRENTLY` precedent before using it).
- Volume: rows returned = sessions in range. 92 days × a busy tenant (say 2 000 sessions/day) = ~184 k
  rows × ~80 B ≈ 15 MB in memory — acceptable, and the max range keeps it bounded. Stream with
  `AsAsyncEnumerable`, accumulate into per-bucket aggregators; never materialize a list of full rows.
  Above 250 k sessions in range: log a warning only, no 422 (decided).

## Backend contract

`GET /api/v1/tenants/{tenantId}/messaging/reports/summary` — `from`, `to` (`yyyy-MM-dd`, required),
`groupBy` (`day|week|month`, required), `channel` (`WhatsApp|Instagram`, optional; strings, not enum, so a
bad value is a 422 with `errors`, as `ReportingFilters.cs:24-27`). Permission `messaging.conversation.read`
+ module `messaging` via `MessagingAuthorization.EnsureAsync`. Handler `GetMessagingReportSummaryHandler`
(Application), source port `IMessagingReportSource` implemented in Infrastructure with raw SQL (like
`OrdersReportSource`), tz from `ITenantClock`.

Validation (FluentValidation, 422 with `errors`): `from`/`to` required, `from ≤ to`, `to` ≤ tenant today,
range within D-9, `groupBy` and `channel` known. Codes: `messaging.report.range_invalid`,
`messaging.report.range_too_large`, `messaging.report.group_by_invalid`, `messaging.report.channel_invalid`.

```json
{
  "period":     { "from": "2026-10-01", "to": "2026-10-28", "groupBy": "week", "timeZone": "America/Bogota" },
  "previous":   { "from": "2026-09-03", "to": "2026-09-30" },
  "businessHoursApplied": true,
  "totals": {
    "current":  { "conversations": 412, "newContacts": 188, "returningContacts": 160,
                  "newContactsByChannel": { "whatsApp": 150, "instagram": 38 } },
    "previous": { "conversations": 380, "newContacts": 170, "returningContacts": 141,
                  "newContactsByChannel": { "whatsApp": 140, "instagram": 30 } }
  },
  "series": [
    { "start": "2026-09-28", "conversations": 80, "newContacts": 31 }
  ],
  "firstResponse": {
    "answeredSessions": 380, "unansweredSessions": 12,
    "medianSeconds": 240, "p90Seconds": 2700,
    "previous": { "medianSeconds": 300, "p90Seconds": 3100 },
    "buckets": [
      { "key": "lt5m", "count": 210 }, { "key": "5to15m", "count": 80 }, { "key": "15to60m", "count": 50 },
      { "key": "1to4h", "count": 30 }, { "key": "gt4h", "count": 10 }
    ]
  },
  "advisorResponse": { "...same shape as firstResponse..." : null },
  "advisors": [
    { "memberId": "…", "displayName": "Laura Gómez", "sessionsAttended": 96,
      "medianAdvisorResponseSeconds": 420, "answeredUnder15mRatio": 0.82, "unansweredAssigned": 2 },
    { "memberId": null, "displayName": "Sin asignar", "sessionsAttended": 0,
      "medianAdvisorResponseSeconds": null, "answeredUnder15mRatio": null, "unansweredAssigned": 7 }
  ],
  "channelsWithData": ["WhatsApp", "Instagram"]
}
```

Rules: `series` is zero-filled and ordered; `start` is the bucket's first local date (Monday for week; the
first of the month for month — the first and last bucket are clipped by the range for *counting* but keep
their natural `start`). Durations are **seconds, integers** (business seconds when
`businessHoursApplied`); `null` median/p90 when there are no answered sessions (never 0). Percentiles:
nearest-rank, documented. Buckets are right-open: `<300`, `[300,900)`, `[900,3600)`, `[3600,14400)`,
`≥14400`. `answeredUnder15mRatio` is over sessions the advisor attended. `advisors` is ordered by
`sessionsAttended` desc, «Sin asignar» last and present only if `unansweredAssigned > 0`. The «Sin asignar»
display string is the server's (Spanish, tuteo irrelevant) so the client does not invent it; deleted
members → «Miembro eliminado».

## Module gating and consistency

Backend: `MessagingAuthorization.EnsureAsync` gives 403 `authorization.denied` / `tenancy.module_not_enabled`; the Reporting module is not involved. Decided (owner): the frontend route is gated with `ModuleGate modules={['messaging']}` only, consistent with the backend.

## Frontend (`qep-frontend`, `features/reports`)

- Route `src/routes/_authenticated/reports/messaging.tsx` (name follows existing `reports/*` routes —
  verify): container with `ModuleGate`, search params `from`, `to`, `groupBy`, `channel` validated
  (`validateSearch` returning `undefined` for absent values, see `tanstack-search-hereda-de-rutas-padre`).
  Delegates to `features/reports/pages/messaging-report-page.tsx` (props only).
- `reports-tabs.tsx`: entry `{ to: '/reports/messaging', label: 'Mensajería', permission:
  MESSAGING_PERMISSIONS.conversationRead, module: 'messaging' }`. The tab list gains **module
  awareness**: `useTenantModules()`; an entry with `module` is hidden unless that module is enabled
  (while `loading` hidden, mirroring the permission handling at `reports-tabs.tsx:59-60`). Add
  `conversationRead` to `MESSAGING_PERMISSIONS` if absent. `reports-index-page.tsx` gets the same
  filter and a card «Mensajería» — «Conversaciones, contactos nuevos y tiempos de respuesta.»
- Services `messaging-report.api.ts` (single `apiRequest`, own parser; absent optional → `null`) and hook
  `use-messaging-report.ts` (TanStack Query, key `['reports', tenant, 'messaging', params]`,
  `placeholderData: keepPreviousData`).
- Page layout, reusing `report-stat-tile`, `report-bucket-bars`, `format-report-values.ts`
  (add `formatReportDuration(seconds)`: «45 s», «12 min», «2 h 05 min», `EMPTY_CELL` for null):
  filter bar (range + «Agrupar por»: Día / Semana / Mes + «Canal»: Todos / WhatsApp / Instagram), KPI tiles
  (Conversaciones, Contactos nuevos, Contactos recurrentes, Mediana 1.ª respuesta, Mediana 1.ª respuesta de
  un asesor, Sin responder) each with delta vs `previous`, a series chart (conversations vs new contacts),
  two bucket-bar blocks («Primera respuesta», «Primera respuesta de un asesor»), the advisors table.
  Footnote when `businessHoursApplied`: «Los tiempos cuentan sólo el horario de atención configurado. Se
  aplica el horario actual, también a periodos pasados.» Otherwise: «Aún no configuras un horario de
  atención: los tiempos se cuentan de corrido.»
- Chart colors: max 3 hues (`chart-tokens-no-son-paleta-categorica`).
- States: **loading** (skeleton tiles), **permission** (no `conversationRead`: «No tienes permiso para ver
  este reporte.»; module off: ModuleGate card), **empty** (all totals 0: «No hay conversaciones en este
  periodo. Prueba con otro rango.»), **error** (422 `range_too_large` mapped by `code` to «Elige un rango
  menor: hasta 92 días por día, 53 semanas por semana o 24 meses por mes.»; other: «No pudimos cargar el
  reporte. Intenta de nuevo.» + retry), **success**. Changing grouping to one the range exceeds is
  prevented client-side by clamping `from` and showing the limit in a hint (the server still validates).
- Nothing aggregated in the browser: no sums, no medians, no zero-fill, no ratios.

## Out of scope

Per-conversation drill-down, CSV export, per-connection split, SLA targets, historical schedule
versioning, chatbot-only performance, resolution-time metrics.

## Resolved decisions

- DP-1 Frontend route gated with `['messaging']` only (not `reporting`).
- DP-2 A manual reopen without inbound counts as a conversation, not as a response-time session.
- DP-3 `businessHoursApplied` is a plain boolean.
- DP-4 Month-grouping comparison period = the same number of days.
- DP-5 Over 250 k sessions in range: log a warning only; no 422.
- DP-6 Failed outbound messages never count as a response.

## Test plan (TDD: RED before GREEN, literal evidence in the ledger)

Backend unit (`tests/…Messaging.UnitTests`):
- `BusinessHoursClockTests`: no schedule → wall-clock; same-day single range; multiple ranges (3/day);
  out-of-hours arrival answered before opening → 0; spans overnight/weekend; exception day open/closed;
  Colombian holiday closed; `to ≤ from` → 0; tz with DST gap and ambiguity (inject a US zone to prove the
  generality even though Colombia has none); schedule per connection resolution (assignment → default → none).
- `MessagingReportAggregatorTests`: session start via created_at not occurred_at; S3 sessions excluded;
  S5 member template counts; S6 echo counts only for first response; campaign excluded; unanswered
  definition S7; new vs returning (S9); advisor attribution (S10); buckets boundaries (299/300/899/900…);
  nearest-rank median/p90; null (not 0) with no data; zero-filled series for day/week/month incl. ISO
  week starting Monday and clipped edges.
- Validator tests: ranges, `groupBy`, `channel`, `from > to`, future `to`.

Backend integration (Testcontainers, harness like `ReportingApiHarness`):
- SQL: sessions via `Reopened` events (ingestion and manual), `LEAD` end bounds, campaign join exclusion,
  Failed excluded, tenant isolation, channel filter, `AT TIME ZONE`/day boundary near midnight Bogotá.
- 403 `authorization.denied` without permission; 403 `tenancy.module_not_enabled` with module off; 422 per
  limit (92 d / 53 w / 24 m) and invalid params, `errors` map shape.
- Member names: deleted member → «Miembro eliminado»; «Sin asignar» row.
- Migration applies on a seeded DB; index used (`EXPLAIN` smoke on a 100 k-row fixture).

Frontend (Vitest + Testing Library, `renderRoute`):
- Page states: loading, permission, module off, empty, error (generic and `range_too_large` by code),
  success; fetch stub asserted (no unstubbed network).
- `ReportsTabs` and index: «Mensajería» hidden without `messaging.conversation.read`, hidden with module off,
  hidden while loading, shown otherwise; existing entries unaffected.
- Route test: `/reports/messaging` redirects anonymous to login; ModuleGate card with module off; search
  params parse (`undefined` for absent, invalid `groupBy` falls back).
- `formatReportDuration`, parser (absent optionals → null), delta rendering with null previous.
- Static guard: no aggregation in the feature (review checklist, not a test).
