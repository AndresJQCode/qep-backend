# Support (PQRSF) — Phase 4b «Formal reply, satisfaction survey and reports», backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans
> to implement this plan block by block. The workspace rule on test budget and plan execution (root `CLAUDE.md`, «Presupuesto de pruebas
> y ejecución de planes») overrides the skill: one implementer per block, one review per block, one strong review at the end. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** An advisor can send the **formal reply** of a ticket as a PDF (Typst through `qcode-pdf`, with the tenant letterhead, the
case summary, the advisor's analysis and decision, the «next step» text configured per tenant and per ticket type, signature and annexes)
through the ticket's channel — thread, the new formal WhatsApp template with the PDF as document header, email attachment or the consult
page — and the requester can download it from the public consult. Every transition to `Resolved` requests a **satisfaction survey**
(Good / Regular / Bad plus an optional comment), sent by the ticket's channel when the tenant turned the survey on, idempotent per
`(ticket, resolution number)`; a worker **auto-closes** `Resolved` tickets after N business days without a customer reply. A **report**
endpoint gives the Support figures (by type, overdue, resolution time, satisfaction).

**Architecture:** Mostly inside the Support module, on top of `develop` at `737812a6` (phases 1–3, realtime + notes, 4a, priority and
portal legal documents merged). Messaging gains two system template kinds appended at the end of `MessageTemplateKind`
(`SupportFormalReply`, with a document header; `SupportSurvey`, with three quick-reply buttons) and three send methods on
`ISupportReplyOutbound`; Notifications gains email attachments and a survey email. Support writes only its own schema; everything it
needs from Messaging, Notifications, Companies, Tenancy and Storage enters through ports with adapters in Bootstrapper. Two raw-SQL feeds
in Bootstrapper read `messaging.messages` by name (survey answers and the customer's last reply for the auto-close) and are tied by
tests against the migrated schema. Two new hosted services (`SupportSurveyWorker`, `SupportAutoCloseWorker`) are classified in
`MultiReplicaInventory`; the survey table is a queue with its doorbell. Reports are computed in Support from its own tables, no
cross-module SQL.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql,
Typst through `qcode-pdf`.

**Spec:** [`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`](../specs/2026-11-14-support-tickets-pqrs-design.md):
§7.5 (formal reply per type), §8.3 (reply and PDF content), §8.4 (survey and auto-close), §8.5 (templates), §9.4 (consult shows the PDF),
§10 «Reportes», §12 phase 4, §13 points 5, 6, 8 and 10. Related: [`2026-11-15-support-portal-legal-documents-design.md`](../specs/2026-11-15-support-portal-legal-documents-design.md)
(the consult page and the company that answers). Previous plans: [phase 2](2026-11-15-support-phase-2-messaging-backend.md) (reply
pipeline, `SupportReply` template), [phase 3](2026-11-15-support-phase-3-public-form-backend.md) (public group, PIN, consult),
[portal legal documents](2026-11-15-support-portal-legal-documents-backend.md) (form organization),
[phase 4a](2026-11-15-support-phase-4a-management-and-deadlines-backend.md) (format model).

---

## Before you start

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-phase-4b`, branch
  `feature/support-phase-4b`, created from `develop` at `737812a6`. **Absolute paths always**: the shell's cwd resets to the main checkout
  (`…\qep-backend`, on `develop`), and a relative script writes there.
- Before **every** commit: `git -C <worktree> branch --show-current`. If it says `main` or `develop`, stop.
- Conventional commits in English, `git commit -m "…" -- <paths>`, **no `Co-Authored-By` and no AI attribution**, even if the harness
  suggests one.
- PowerShell, no `&&`. If `Api.exe` or `dotnet Api.dll` is running the build fails on a locked file: report it, do not stop it.
- **Tests (owner, 2026-11-15):** implementers **do not run tests**, neither unit nor integration. TDD order: test first; RED = `dotnet
  build` of the test project failing on compilation (`CS0246`, `CS1061`, `CS7036`…); GREEN = `dotnet build` of the solution with no
  errors and no warnings. A test that compiles from the start is marked «RED without evidence: not run». Both outputs, literal, go in the
  block report. At the end of each block the implementer hands the owner the exact `--filter` list of the block (below, per task); the
  owner runs them one at a time, never two integration runs at once.
- Shortcuts: `$W` = worktree; `$SU`/`$SI` = `$W\tests\Modules\Support\Modules.Support.{UnitTests,IntegrationTests}`;
  `$MU`/`$MI` = `$W\tests\Modules\Messaging\Modules.Messaging.{UnitTests,IntegrationTests}`; `$NU` =
  `$W\tests\Modules\Notifications\Modules.Notifications.UnitTests`; `$CI` = `$W\tests\Modules\Customers\Modules.Customers.IntegrationTests`;
  `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`.
- Migrations with the design factory, from `$W`:
  ```powershell
  dotnet ef migrations add <Name> --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  dotnet ef migrations add <Name> --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
  ```
- `docs/estado.md` **is not touched** on this branch.
- Code comments and documentation addressed to a reader: Colombian Spanish, tuteo (repo rule). Identifiers, codes and routes in English.
  Product copy (PDF, emails, templates, WhatsApp texts) in Colombian Spanish, tuteo, professional register.

## Global Constraints

- Support does not reference `Modules.Messaging.*`, `Customers.*`, `Companies.*`, `Notifications.*`, `Storage.*` nor `Tenancy.Infrastructure`;
  `SupportLayerTests` is not loosened. Nothing new in `Modules.Support.Application` references EF Core or Npgsql.
- Tenant isolation in two layers: route `{tenantId:guid}` + policy, and the handler revalidates (`SupportAuthorization.EnsureAsync` /
  `EnsureCanViewAsync`). Every repository method takes `tenantId`. The public group never reads `IExecutionContext` nor the cookie: the
  tenant comes from the slug (`SupportPublicResolver`).
- Every new write on a ticket or on its survey: audit (`ISupportAuditPublisher`, `PublishSystem` for workers) in the same transaction,
  and `ISupportRealtime.TicketChangedAsync(…, CancellationToken.None)` **after** the commit (plan N13). A survey row change does **not**
  bump the ticket version (same reason as the worker's `Linked` rows: a 412 to the advisor who has the ticket open), but it does notify.
- Errors: no HTTP status by hand in a handler. `SupportDomainException` → 422, `RequestConcurrencyException` → 412, missing `If-Match` →
  428, `ResourceNotFoundException` → 404. Every free text (decision, signer title, annexes, next-step texts, survey comment) has a
  FluentValidation validator besides the domain guard.
- DB errors are translated in `SupportUnitOfWork` **by index/constraint name** and SQLSTATE, never by code alone.
- Every migration is **additive** (expand). New enum values go **at the end** and are stored by name: `MessageTemplateKind`
  (`SupportFormalReply`, `SupportSurvey`). `SupportTicketEventKind` **does not change** in 4b (no new trace kind).
- Failure and skip codes of replies and surveys come only from `SupportChannelReasons` (Task 8a vocabulary); the two new ones
  (`organization_required`, `survey_disabled`) are added there and to `All`.
- No PIN, survey token, email address, comment or reply text in logs, audit `changedFields`, events or traces. Audit carries field
  names only.
- Legal proof: `support.ticket_reply_documents` is insert-only (its only delete is the cascade of a reply row that was never sent, see
  H9), and the R2 objects under `support/` have **no lifecycle rule** (10-year retention, spec §13.10). The `Down` of
  `AddSupportReplyDocuments` is never run in production.
- New hosted services enter `MultiReplicaInventory` in the same commit; a new singleton built by a factory enters `InterfaceFactories`.
  No new startup initialization (nothing goes into `RunUnderStartupLockAsync`).
- The survey table is a queue: it enters `QueueDoorbellInterceptor.Tables`, `DoorbellQueues` and the observability queue list.
- Columns with Messaging message ids in `support.*` enter `CustomerDeletionReferences` (`ticket_surveys.message_id`, `rating_message_id`,
  `comment_message_id`, `prompt_message_id`). The one new member-id column, `ticket_reply_documents.signer_member_id` (H3, owner
  2026-11-16), enters `SupportUserReferenceProbe` and `SupportUserReferenceProbeTests` in the same commit; the survey author is resolved
  at send time and not stored.
- Support never reuses `Quotations:Pdf:ApiKey`: its own section `Support:Pdf`.

## Plan decisions

Mine (planner) on what the spec leaves to the implementer, or what the code forces. Those marked **(owner, 2026-11-16, Q-n)** were
decided by the owner on 2026-11-16 (see «Owner decisions (2026-11-16)»); the text here is already the decided one.

### Formal reply

| # | Decision | Why |
| --- | --- | --- |
| H1 | A formal reply is a normal reply with a `formal` object in the body of `POST /tickets/{id}/replies`: `{ decision, signerTitle, annexes[] }`. Allowed on **any** ticket type and state (replying is allowed in any state, R9 extended). `RequiresFormalResponse` already exists on the type and on the ticket snapshot (`SupportTicket.cs:48`, `SupportTicketTypeSnapshotDto`) and only *suggests* it (`replyOptions.formal.suggested`). | Spec §8.3: «el asesor puede elegirla en cualquier tipo». The type already carries the flag (phase 1); verified, nothing to add. |
| H2 | `text` stays the **channel message** (existing rules: thread 4096, Instagram 1000, template one line ≤ 1024, caption ≤ 1024 when the PDF goes as a document); `formal.decision` is the PDF section 6, 1–20 000 characters, line breaks allowed. **(owner, 2026-11-16, Q-5)** | The channel message and the legal letter have different limits; the template variable `respuesta` cannot hold a letter. |
| H3 | Signature: the ticket's **assignee** signs, not the sender. Name = the assignee's display name (snapshot), title = `formal.signerTitle`, free text 1–120, **required**, typed at send. A formal reply on a ticket without assignee → `422 support.reply.formal_signer_required` (new `SupportErrorCodes.ReplyFormalSignerRequired`; a `SupportDomainException`, so the central `ApiExceptionHandler` maps it to 422 with no change there), and `reply-options` says `signer_required` (new `SupportChannelReasons.SignerRequired`, in `All`). The document stores `signer_member_id`, the name snapshot and the title. **(owner, 2026-11-16, Q-7)** | No job title exists in Tenancy or Identity (verified). Spec §8.3.8 «firma del responsable». The member id is a membership reference: `SupportUserReferenceProbe`. |
| H4 | Annexes: `formal.annexes`, 0–10 strings of 1–200, printed as a list. No files travel with them. **(owner, 2026-11-16, Q-8)** | Spec §8.3.8 «lista de anexos»; a formal reply already carries one document. |
| H5 | Letterhead: the company of the ticket's form version (`FormVersionId` → `organization_company_id`) or, without one, of the **current** form version; name, document type and number, address, email (and city). Logo = tenant logo (PNG/JPEG; WebP is skipped because `qcode-pdf` rejects it). No company → `422 support.reply.formal_organization_required`, and `reply-options` says `organization_required`. **(owner, 2026-11-16, Q-6)** | The tenant has no tax id, address or email (`Tenant.cs`); `Company` has them (`Company.cs:102-131`) and the portal already picks «the company that answers» (portal legal plan L30). |
| H6 | «Next step» (section 7): `ticket_types.formal_next_step` overrides `support.tenant_settings.formal_next_step`; both nullable, plain text ≤ 2000. Both empty → the section is omitted. Snapshot into the document row. | Spec §8.3.7 and §13.6: configurable per tenant and per type; the legal text is a deployment concern. |
| H7 | The PDF is rendered and stored **before** the `Pending` reply row; the document row is inserted in the same `SaveChanges` as the reply. A resumed `Pending` reply re-sends the **stored** document, never re-renders. Render failure → `422 support.reply.document_render_failed`, nothing persisted; **final review: a failure saving the PDF to R2 is the same 422** (only the exception type is logged), never a 500 (the R2 object, if uploaded, is deleted best effort). | Same idempotency rule as the reply (CM1): a retry sends exactly what the row saved. Precedent of the 422 for an upstream PDF failure: `quotation.pdf.render_failed`. |
| H8 | Storage: private bucket, key `support/tenants/{tenantId:N}/replies/{yyyy}/{MM}/{guid:N}.pdf`, through a Support port (`ISupportDocumentStorage`) with adapter in Bootstrapper over `IObjectStorage` (calque of `QuotationPdfStorage`). Downloads by presigned URL: 5 minutes in the console, 10 minutes in the public consult. | Legal retention (§13.10): never under `quotations/` (lifecycle). |
| H9 | Table `support.ticket_reply_documents` (PK `reply_id`, FK → `ticket_replies.id` `ON DELETE CASCADE`), insert-only. The cascade only fires when `SendTicketReplyHandler` removes a reply the channel rejected **before** sending (`SendTicketReply.cs:214`): that reply never existed for the requester. | Legal proof of what was sent, like `support.legal_acceptances`; one row per reply. |
| H10 | Delivery of the PDF by route: **thread WhatsApp, window open** → one document message with `text` as caption; **thread Instagram, window open** → the text message, then a `file` attachment (two messages, the second with `DocumentClientId` from the document row); **WhatsApp outside the window or without conversation** → the new template `SupportFormalReply` with the PDF as document header (not approved → existing `support.reply.template_not_approved`); **email** → attachment; **portal** → published, downloadable in the consult. | Spec §8.3 and §8.5. A document header cannot be optional in a Meta template, so the formal one is a **separate kind** from `SupportReply`. **(owner, 2026-11-16, Q-1)** |
| H11 | `formal` and `uploadId` are mutually exclusive (`422 validation.failed` on `UploadId`): one document per reply. | Keeps the thread to one attachment per message. |
| H12 | `Support:Pdf` options (`BaseUrl` default `https://qcode-pdf.qcode.co`, `ApiKey`, `TimeoutSeconds` 30), validator with `ValidateOnStart`: HTTPS always; `ApiKey` required in `Production`, an unresolved pipeline token `#{…}#` counts as absent (`SeedOptions.IsUnreplacedPipelineToken`, gotcha «Un token del pipeline sin resolver llega literal»). Renderer in `Modules.Support.Infrastructure/Pdf`, template `formal-reply.typ` embedded; data as camelCase JSON read with `json(sys.inputs.data)`, every string printed as text (never evaluated as markup). | Spec §8.3 / §13.5: own key and section; calque of `QCodePdfRenderer`. |
| H13 | Console download `GET /tickets/{ticketId}/replies/{replyId}/document` (policy `support.ticket.view`, revalidated by `EnsureCanViewAsync`), audit `support.ticket.reply_document_downloaded`. Public: `SupportPublicReplyDto` gains `DocumentUrl`/`DocumentFileName`, filled only in the `lookup` response (PIN-gated; no new public route). | §9.4: «muestra la respuesta (incluido el PDF)»; a separate public download route would need the PIN again. |

### Survey and auto-close

| # | Decision | Why |
| --- | --- | --- |
| H14 | Settings: `survey_enabled` (default **false**), `auto_close_business_days` (default 5, range 1–30; auto-close is never off). **(owner, 2026-11-16, Q-19, Q-20)** | Spec §8.4: toggle per tenant; «desactivada, el cierre automático funciona igual». |
| H15 | `SupportTicket.ChangeStatus(Resolved, …)` adds a `SupportTicketSurvey` (`Pending`, `ResolutionNumber = ResolutionCount`) to a new navigation, persisted like `Events`. **Every** path to `Resolved` creates it without remembering to. The worker skips it with `survey_disabled` if the tenant has the survey off **when it processes it**. Unique `(ticket_id, resolution_number)` (`UX_ticket_surveys_ticket_resolution`). **(owner, 2026-11-16, Q-9)** | Spec §8.4: idempotent per `(ticket, número de resolución)`; two handlers reach `Resolved` today (`ChangeTicketStatus`, `SendTicketReply` with `resolve`). |
| H16 | Channel per survey, decided by the worker: anonymous or `Portal` → `Portal` only (status `Sent` with no message, like a `Portal` reply); a **WhatsApp** conversation → template `SupportSurvey` (`qep_soporte_encuesta`, Buena/Regular/Mala); an **email** contact → email with the rating link; an **Instagram** ticket → **never through Instagram**: the email link if the ticket has an email, else `Portal` only; anything else → `Portal`. WhatsApp without an approved template, Messaging off, contact blocked → `Skipped` with the reason («sin encuesta», not failed). `send_failed` / `connection_unavailable` retry 3 times (1, 5, 15 min) then `Failed`. **(owner, 2026-11-16, Q-2)** | Spec §8.4 «por qué canal». Owner: no tenant uses Instagram yet and no Instagram tickets will be created for now, so no Instagram survey path is built (Instagram has no outbound interactives either, `BotOutbound.cs:79`, D-I1). |
| H17 | Author of a survey sent by Messaging: the same rule as the PIN information (`SupportOpeningSender.AuthorAsync`, plan F11), extracted to `SupportSystemAuthor`; none → `Skipped no_author`. | Messaging rows need `SentByMemberId`; no member id stored on the survey. |
| H18 | WhatsApp answer: the inbound with `kind = Interactive`, `direction = 1`, not imported, whose `reply_to_message_id` is the survey's `message_id` (Meta's `context.id`, resolved by `InboundIngestion`), its `text` matched to Buena/Regular/Mala (case, accents and edges ignored). Right after recording the tap, the worker sends **one** free-form text inside the window the tap just opened (no template): «Gracias. Si quieres, cuéntanos en un mensaje por qué elegiste esa opción.», through `ISupportReplyChannels.SendThreadAsync` (the support reply outbound: idempotent by `ClientId`, no `OutboundGuards`, does not take, assign or reopen the conversation nor count as an advisor reply, `seen: 0`) with a **deterministic** `ClientId` per survey (`SupportSurveyPrompt.ClientIdFor(surveyId)`, derived like `SupportOpeningSender.ClientIdFor`); its outcome is stored (`prompt_message_id` and `prompt_sent_at`, or `prompt_failure_code`), with no retry nor reminder. Comment: the first inbound `Text` of that conversation after the tap and within `Support:Workers:SurveyCommentWindowHours` (24) **of the tap**, cut to 1000; after 24 h nothing is awaited and later messages are ordinary inbound. **(owner, 2026-11-16, Q-11)** | Spec §8.4: buttons, «comentario libre opcional en el mensaje siguiente». A tap on an old survey only counts for the survey it quotes (same lesson as the data consent, `FlowEngine.IsStaleConsentTap`). |
| H19 | Rating link: `{Support:PublicForm:BaseUrl}/{slug}/calificar?token={token}`; token = 32 random bytes base64url, only its SHA-256 is stored (`UX_ticket_surveys_token`). The API takes the token in the **body**, never in the path (no token in access logs): `POST /api/v1/public/support/{slug}/surveys/lookup` and `/surveys/answer`, with the `lookup` limiter, no Turnstile. **(owner, 2026-11-16, Q-12)** | Spec §8.4 «correo: enlace a una página pública»; `calificar` is already reserved (`TenantSlugReservations.cs`). |
| H20 | Consult page: `lookup` returns `survey` (can rate, current rating), and `POST /api/v1/public/support/{slug}/tickets/rating` takes `{ number, pin, turnstileToken, rating, comment }` with **the same verification as `lookup`** (Turnstile, locked row, five failures lock 15 min). Anonymous tickets rate only here. | Spec §8.4: «formulario público… en la página de consulta», «anónimo: sólo por la página de consulta». |
| H21 | A survey is answerable when it has no rating, its status is not `Pending` and not `Skipped survey_disabled`, the ticket is `Resolved` or `Closed`, and `ticket.ResolutionCount == survey.ResolutionNumber` (reopened = that resolution is no longer judged). First answer wins (`UPDATE … WHERE rating IS NULL`); a second → `422 support.survey.already_answered`; not answerable → `422 support.survey.closed`. **(owner, 2026-11-16, Q-10)** | Spec «una vez por ciclo de resolución». |
| H22 | Auto-close: a `Resolved` ticket closes when the tenant's local date is **after** `Add(calendar, max(resolvedOn, lastCustomerReplyOn), N)`; `lastCustomerReplyOn` = the last customer inbound (`messaging.messages.direction = 1`, not imported) after `resolved_at` whose link in `support.ticket_messages` is **currently in state `Linked` or `Pinned`** (still in the ticket's record): an advisor «Soltar» (`Released`) removes it from the count and the close date falls back to the previous one (or to the resolution). The survey tap and the survey comment never count. N per tenant, 1–30, default 5. Closes as the system (`ChangeStatus(Closed, null, …)`, `PublishSystem`, `TicketClosed` in the thread). Detail shows `clock.autoCloseOn`, recomputed on every read. **(owner, 2026-11-16, Q-4)** | Spec §8.4 «N días hábiles sin respuesta del cliente», and P4 (an inbound does not reopen a `Resolved`): without the restart, a customer who writes «sigue fallando» is closed for good (`Closed` is terminal). |
| H23 | Old pods during the rollout run the old `ChangeStatus`: a resolution they make gets no survey row. Accepted; the README says it. | No startup backfill (startup-lock rule) for a non-legal feature. |

### Reports

| # | Decision | Why |
| --- | --- | --- |
| H24 | Reports live in **Support**: `GET /support/reports/summary?from=YYYY-MM-DD&to=YYYY-MM-DD`, policy `support.ticket.view`, computed in Application from Support's own tables. **(owner, 2026-11-16, Q-3, Q-16)** | Every figure is in the `support` schema; Reporting would read it by raw SQL (rename warning) and gate it behind `reporting.*` permissions and module. |
| H25 | Population = tickets whose **local reception date** is in `[from, to]` (≤ 366 days, both required). `byStatus` brings the six statuses, `byType` every ticket type of the tenant (active or not, by `Position`), `ratings` the three ratings — all complete even in zero. Totals close: `onTime + late + withoutTerm = resolved`; `pending + sent + withoutSurvey + failed + disabled = requested`. Resolution time = business days `d` with `receivedOn < d ≤ resolvedOn` (last resolution), gross of pauses; average with one decimal and median. **(owner, 2026-11-16, Q-17)** | BFF rules: fixed-size collections travel complete; a total that does not close is explained by a field. |

## Already covered, no task

- **«Requires formal reply» per type:** exists since phase 1 (`SupportTicketType.RequiresFormalResponse`, seed in
  `SupportTicketTypeDefaults.cs`, snapshot on the ticket and in `SupportTicketTypeSnapshotDto`). 4b only reads it.
- **Resolution cycles:** `SupportTicket.ResolutionCount` and `ResolvedAt` exist (`SupportTicket.cs:94-97`); the survey key uses them.
- **No auto-close exists** today (searched `AutoClose`, `auto_close`, `survey`, `satisfaction`, `encuesta` in `src`: only the reserved
  slug `calificar`). Task 14 builds it.
- **`calificar`** is already a reserved tenant slug (`TenantSlugReservations.cs`, phase 3 F1) for the rating page.
- **Business calendar:** `SupportBusinessDays`, `ISupportBusinessCalendarSource` and `SupportBusinessCalendar` (Bootstrapper) are reused
  as they are.
- **«Apertura de Soporte» template:** not built, by owner decision (phase 2). 4b does not add it.

## Review Focus

1. **A formal reply that is retried sends the same PDF:** a `Pending` reply older than 60 s is resumed with the stored document and the
   stored `DocumentClientId`; the renderer is not called again. Task 8: `AResumedFormalReplySendsTheStoredDocument`.
2. **Every path to `Resolved` requests exactly one survey per cycle:** status change, «Enviar y marcar Resuelto», resolve → reopen →
   resolve gives resolution numbers 1 and 2; the unique index makes a double insert impossible. Task 10:
   `EveryResolutionRequestsOneSurvey`, `ReopeningAndResolvingAgainRequestsASecondSurvey`.
3. **A WhatsApp tap counts only for the survey it quotes:** a tap on survey 1 after survey 2 was sent rates nothing (survey 1 is no
   longer answerable). Task 12: `ATapOnAnOldSurveyRatesNothing`.
4. **Auto-close respects the customer, and only while the advisor keeps the message:** a customer reply in state `Linked` or `Pinned` after the
   resolution moves `autoCloseOn`; releasing it moves the date back; the survey tap and comment never count. Task 14:
   `ACustomerReplyAfterResolvingRestartsTheCount`, `ReleasingTheReplyMovesTheCloseDateBack`, `ASurveyAnswerDoesNotHoldTheClose`.
5. **No secret leaks:** the rating token never appears in a route, a log, the audit or an event; the PIN of `tickets/rating` neither.
   Task 13: `TheSurveyTokenNeverReachesLogsAuditOrEvents` (host in `Trace`, like `ThePinNeverReachesLogsAuditOrEvents`).
6. **The raw-SQL feeds run against the migrated schema:** `SupportSurveyFeed` and `SupportAutoCloseFeed` in
   `SupportBootstrapperAdaptersTests`.
7. **The comment prompt goes out once and is not an advisor reply:** a retried tick re-sends with the same deterministic `ClientId` and
   Messaging returns the same message; the conversation is not taken nor reassigned. Task 12: `ThePromptIsSentOnceAfterTheTap`,
   `ThePromptDoesNotTakeTheConversation`.
8. **The assignee signs:** the PDF and the document row carry the assignee, not the sender; no assignee → 422. Task 8:
   `TheAssigneeSignsNotTheSender`, `AFormalReplyWithoutAssigneeIs422SignerRequired`.

---

## Screen contract (BFF)

Under `/api/v1/tenants/{tenantId:guid}/support`:

| Method and route | Permission | Body | Response |
| --- | --- | --- | --- |
| `POST /tickets/{ticketId}/replies` (changes) | `Manage` | `SupportReplyRequest` + `SupportFormalReplyRequest? Formal` (last) | `201 SupportReplyResultDto`; new 422 `formal_organization_required`, `formal_signer_required`, `document_render_failed` |
| `GET /tickets/{ticketId}/reply-options` (changes) | `TicketView` | — | `SupportReplyOptionsDto` + `SupportFormalOptionsDto Formal` (last) |
| `GET /tickets/{ticketId}/replies/{replyId:guid}/document` | `TicketView` | — | `SupportDocumentLinkDto`; 404 `support.reply.document_not_found` |
| `GET /settings`, `PUT /settings` (change) | `SettingsView` / `SettingsManage` | `UpdateSupportSettingsRequest` + `FormalNextStep`, `SurveyEnabled`, `AutoCloseBusinessDays` | `SupportSettingsDto` (appended fields) |
| `POST`/`PUT /settings/ticket-types…` (change) | `SettingsManage` | + `string? FormalNextStep` (last) | `SupportTicketTypeDto` + `FormalNextStep` (last) |
| `GET /reports/summary?from&to` | `TicketView` | — | `SupportReportDto` |

Under `/api/v1/public/support/{tenantSlug}` (CORS `support-public`, no credentials, `no-store`, CSRF exempt by prefix):

| Method and route | Limiter | Body | Response |
| --- | --- | --- | --- |
| `POST /tickets/lookup` (changes) | lookup | as today | `SupportPublicTicketDto` + `SupportPublicSurveyStateDto? Survey` (last); each reply + `DocumentUrl`, `DocumentFileName` |
| `POST /tickets/rating` | lookup | `{ number, pin, turnstileToken, rating, comment }` | `SupportPublicTicketDto`; 422 `lookup_failed` (same as lookup), `survey.closed`, `survey.already_answered` |
| `POST /surveys/lookup` | lookup | `{ token }` | `SupportPublicSurveyDto`; 404 `support.survey.not_found` |
| `POST /surveys/answer` | lookup | `{ token, rating, comment }` | `SupportPublicSurveyDto`; 404, 422 `closed`/`already_answered`, 422 `validation.failed` |

Under `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management` (Messaging, `messaging.template.manage`):
`POST /support-formal-reply`, `GET /support-formal-reply/draft`, `POST /support-survey`, `GET /support-survey/draft` — same contract as
`/support-reply` (phase 2).

Shapes (records append at the **end**; every enum by name):

```csharp
public sealed record SupportFormalReplyRequest(string? Decision, string? SignerTitle, IReadOnlyList<string>? Annexes);
public sealed record SupportReplyDocumentDto(string FileName, long SizeBytes, SupportMemberDto Signer, string SignerTitle);  // SupportReplyDto + Document (last); Signer.DisplayName is the snapshot
public sealed record SupportFormalOptionsDto(
    bool Suggested,                // ticket.RequiresFormalResponse: the composer pre-checks «Respuesta formal»
    string? BlockedReason,         // organization_required, signer_required (no assignee), or null
    string? ThreadReason,          // null if the thread can take a formal reply now; template_not_approved, window_closed, …
    IReadOnlyList<SupportConnectionOptionDto> TemplateConnections);   // connections with SupportFormalReply approved (no conversation)
public sealed record SupportDocumentLinkDto(Uri Url, string FileName, DateTimeOffset ExpiresAt);
public sealed record SupportSettingsDto(int DueSoonBusinessDays, int MinDueSoonBusinessDays, int MaxDueSoonBusinessDays, long Version,
    string? FormalNextStep, bool SurveyEnabled, int AutoCloseBusinessDays, int MinAutoCloseBusinessDays, int MaxAutoCloseBusinessDays);
public sealed record SupportSurveyDto(int ResolutionNumber, string Status, string? Channel, string? ReasonCode, DateTimeOffset? SentAt,
    string? Rating, string? Comment, DateTimeOffset? AnsweredAt, string? AnsweredVia, bool CommentPromptSent);   // SupportTicketDetailDto + Surveys (last); Channel/AnsweredVia ∈ WhatsApp, Email, Portal
// SupportClockDto + DateOnly? AutoCloseOn (last): only while Resolved.
public sealed record SupportPublicSurveyStateDto(bool CanRate, string? Rating, IReadOnlyList<string> Options);   // Options = Good, Regular, Bad
public sealed record SupportPublicSurveyDto(string TenantName, string? LogoUrl, string? AccentColor, string Number, string TypeName,
    bool CanRate, string? Rating, IReadOnlyList<string> Options);
public sealed record SupportReportDto(DateOnly From, DateOnly To, int Received, int Resolved, int OverdueNow,
    IReadOnlyList<SupportStatusCountDto> ByStatus, IReadOnlyList<SupportReportTypeRowDto> ByType,
    SupportResolutionTimeDto ResolutionTime, SupportSatisfactionDto Satisfaction);
public sealed record SupportReportTypeRowDto(Guid TypeId, string Name, bool IsActive, int Received, int Resolved, int OverdueNow,
    decimal? AverageResolutionBusinessDays, IReadOnlyList<SupportRatingCountDto> Ratings);
public sealed record SupportResolutionTimeDto(int Resolved, decimal? AverageBusinessDays, decimal? MedianBusinessDays, int OnTime, int Late,
    int WithoutTerm);
public sealed record SupportSatisfactionDto(int Requested, int Pending, int Sent, int WithoutSurvey, int Failed, int Disabled, int Answered,
    IReadOnlyList<SupportRatingCountDto> Ratings);
public sealed record SupportRatingCountDto(string Rating, int Count);
```

Each DTO carries in its `<summary>` why it has that shape (repo BFF rule): e.g. `Formal.ThreadReason` exists so the composer does not
learn template kinds; `Satisfaction.Disabled` exists so `Requested` closes when the tenant had the survey off.

## File structure

```
src/Modules/Support/
  Domain/          SupportTicket.cs (+Surveys, ChangeStatus creates the survey), SupportTicketSurvey.cs, SupportSurveyStatus.cs,
                   SupportSurveyChannel.cs, SupportSurveyRating.cs (+SupportSurveyRatings), SupportReplyDocument.cs,
                   SupportTenantSettings.cs (+FormalNextStep, SurveyEnabled, AutoCloseBusinessDays), SupportTicketType.cs (+FormalNextStep),
                   SupportErrorCodes.cs
  Application/     Replies/SendTicketReply.cs, Replies/GetReplyOptions.cs, Replies/SupportFormalReplies.cs (new),
                   Replies/GetReplyDocumentDownload.cs (new), ISupportFormalReplyRenderer.cs, ISupportDocumentStorage.cs,
                   ISupportLetterhead.cs, ISupportReplyChannels.cs (+3 methods, +2 reasons), ISupportReplyEmail.cs (+document),
                   ISupportSurveyEmail.cs, ISupportSurveyFeed.cs, ISupportAutoCloseFeed.cs, Surveys/* (new), AutoClose/SupportAutoCloser.cs,
                   Reports/GetSupportReport.cs, Public/LookupPublicTicket.cs, Public/SupportPublicAccess.cs (new), Public/RatePublicTicket.cs,
                   Public/PublicSurveys.cs, Opening/SupportOpeningSender.cs (author extracted), Settings/*, TicketTypes/*, SupportDtos.cs
  Infrastructure/  Pdf/SupportPdfOptions.cs, Pdf/SupportPdfOptionsValidator.cs, Pdf/SupportPdfRenderer.cs, Pdf/formal-reply.typ,
                   SupportSurveyWorker.cs, SupportAutoCloseWorker.cs, SupportWorkerOptions.cs, Persistence/* , 3 migrations
  Api/             SupportEndpoints.cs, PublicSupportEndpoints.cs
src/Modules/Messaging/  MessageTemplate.cs (+2 kinds, names), SystemTemplateVariables.cs, SupportFormalReplyTemplateProvisioning.cs,
                   SupportSurveyTemplateProvisioning.cs, SupportReplyOutbound.cs (+3 methods), readiness, TemplateManagement*, endpoints,
                   1 migration
src/Modules/Notifications/  IEmailChannel.cs (+attachments), InfobipEmailChannel.cs, LogEmailChannel.cs, SupportReplyEmailTemplate.cs,
                   SupportSurveyEmailTemplate.cs (new)
src/BuildingBlocks/  BuildingBlocks.Application/QueueDoorbell.cs (+SupportSurvey), BuildingBlocks.Infrastructure/QueueDoorbellInterceptor.cs
src/Bootstrapper/  SupportDocumentStorage.cs, SupportLetterhead.cs, SupportSurveyEmailSender.cs, SupportSurveyFeed.cs,
                   SupportAutoCloseFeed.cs, SupportReplyChannels.cs, SupportReplyEmailSender.cs, SupportOrganizationDirectory.cs,
                   CustomerDeletionReferences.cs, Observability/QueueNames.cs + QueueMetricsSource.cs, QepServiceCollectionExtensions.cs
k8s/prod-configMap.yaml (Support__Pdf__BaseUrl), k8s/prod-secret.yaml (Support__Pdf__ApiKey)
tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs
README.md § Soporte, CLAUDE.md (Support gotchas), spec §8.3/§8.4/§8.5/§10 corrections
```

## Migrations and rollout

| Migration | Module | Additive? | Old pods during rollout |
| --- | --- | --- | --- |
| `AddSupportFormalNextStep` (Task 1) | Support | Yes: nullable `tenant_settings.formal_next_step`, `ticket_types.formal_next_step` | Ignore the columns. |
| `AddSupportReplyDocuments` (Task 2) | Support | Yes: new table `ticket_reply_documents` | A reply removed by an old pod cascades its document. |
| `AddSupportFormalAndSurveyTemplateKinds` (Task 4) | Messaging | Yes: rewrites `CK_message_templates_kind` with two more names, two partial unique indexes | An old pod reading a row of a new kind fails `Enum.Parse` in the template screens: **provision the new templates only when no old pod remains** (same as `AddSupportReplyTemplateKind`). `Down` deletes those rows (precedent). |
| `AddSupportSurveys` (Task 10) | Support | Yes: `tenant_settings.survey_enabled` (default `false`), `tenant_settings.auto_close_business_days` (default 5, CHECK 1–30), new table `ticket_surveys` | Resolutions made by an old pod get no survey (H23). |

Configuration: `Support:Pdf:BaseUrl` (ConfigMap, `#{SUPPORT_PDF_BASE_URL}#`), `Support:Pdf:ApiKey` (Secret, `#{SUPPORT_PDF_API_KEY}#`, group
`Backend-prod`); `Support:Workers:SurveyIntervalSeconds` (30), `AutoCloseIntervalMinutes` (15), `SurveyCommentWindowHours` (24),
`SurveyAnswerLookbackDays` (14) with defaults in code. **Without `Support:Pdf:ApiKey` the production pod does not start** — the variable
goes into `Backend-prod` before this reaches `main`.

---

## Block A — Formal reply foundations, Support only (Tasks 1–3, one implementer)

### Task 1: «Next step» text per tenant and per ticket type

**Files:**
- Modify: `Modules.Support.Domain/SupportTenantSettings.cs` (`FormalNextStep`, `ChangeFormalNextStep`), `SupportTicketType.cs`
  (`FormalNextStep` in `Create`/`Update`, `FormalNextStepMaxLength = 2000`), `SupportTicketTypeDefaults.cs` (seed `null`),
  `Application/Settings/{GetSupportSettings,UpdateSupportSettings}.cs`, `TicketTypes/{CreateTicketType,UpdateTicketType}.cs`,
  `SupportDtos.cs` (`SupportSettingsDto`, `SupportTicketTypeDto`), `Infrastructure/Persistence/{SupportDbContext,SupportProvisioner}.cs`
  (column mapping; the provisioner's `INSERT` does not list the new columns: their DB default is `NULL`)
- Create: migration `AddSupportFormalNextStep` (never edit a committed migration: Task 2 adds its own)
- Test: `$SU\SupportTenantSettingsTests.cs` (new), `$SU\SupportTicketTypeTests.cs` (new or existing), `$SU\UpdateSupportSettingsValidatorTests.cs`
  (new), `$SI\SupportSettingsApiTests.cs`, `$SI\SupportTicketTypesApiTests.cs`

**Interfaces — Produces:**
```csharp
public const int FormalNextStepMaxLength = 2000;                         // SupportTenantSettings and SupportTicketType
public string? FormalNextStep { get; private set; }                      // both; trimmed, empty → null
public void ChangeFormalNextStep(string? text, DateTimeOffset now);     // SupportTenantSettings; same value → no-op, no version
public sealed record UpdateSupportSettingsRequest(int? DueSoonBusinessDays, string? FormalNextStep = null);   // Task 10 appends the rest
// CreateTicketTypeRequest / UpdateTicketTypeRequest: + string? FormalNextStep = null (last). SupportTicketTypeDto: + string? FormalNextStep (last).
```
The `PUT` is a full replacement: `formalNextStep: null` clears it (**changed 2026-11-16: optional for backward compatibility with the shipped frontend** — absent or null keeps the stored text, `""` clears it; **the same rule for the ticket type's `PUT` since the final review**, even though the rest of that body replaces the type). Audit fields `formalNextStep` (settings) and `formalNextStep` (type).
Validators: ≤ 2000 after trim, no control characters except line break and tab.

- [ ] **Step 1: tests**
  - `SupportTenantSettingsTests.TheNextStepIsTrimmedAndAnEmptyOneIsNull`, `SettingTheSameNextStepDoesNotBumpTheVersion`,
    `ANextStepLongerThan2000Throws`.
  - `SupportTicketTypeTests.ATypeKeepsItsOwnNextStep` (create and update, null clears).
  - `UpdateSupportSettingsValidatorTests.TheNextStepIsAtMost2000WithoutControlCharacters` (key `FormalNextStep`).
  - `SupportSettingsApiTests.TheTenantNextStepRoundTripsAndIsAudited` — `PUT` with text ⇒ `GET` returns it; audit `support.settings.updated`
    with `["formalNextStep"]`, never the text.
  - `SupportTicketTypesApiTests.ATypeNextStepRoundTrips` and `ANextStepOver2000Is422`.
- [ ] **Step 2: RED** — `dotnet build $SU` / `$SI` fail (`CS1061 FormalNextStep`, `CS7036`).
- [ ] **Step 3: implement.** Columns `support.tenant_settings.formal_next_step varchar(2000) null`, `support.ticket_types.formal_next_step
  varchar(2000) null`. Migration additive.
- [ ] **Step 4: GREEN** — solution build clean.
- [ ] **Step 5: commit** — `feat(support): configure the formal reply next step per tenant and ticket type`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportTenantSettingsTests|FullyQualifiedName~SupportTicketTypeTests|FullyQualifiedName~UpdateSupportSettingsValidatorTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~NextStep"`.

### Task 2: The reply document (domain and persistence)

**Files:**
- Create: `Modules.Support.Domain/SupportReplyDocument.cs`; migration `AddSupportReplyDocuments`
- Modify: `SupportErrorCodes.cs`, `src/Bootstrapper/SupportUserReferenceProbe.cs` (+ `ticket_reply_documents.signer_member_id`),
  `Infrastructure/Persistence/SupportDbContext.cs` (`ReplyDocuments`, table, FK cascade, CHECKs),
  `ISupportReplyRepository.cs` + `SupportReplyRepository.cs` (`AddDocument`, `FindDocumentAsync`, `ListDocumentsAsync(tenantId, ticketId)`)
- Test: `$SU\SupportReplyDocumentTests.cs` (new), `$SI\SupportPersistenceTests.cs`

**Interfaces — Produces:**
```csharp
public sealed class SupportReplyDocument          // insert-only: no mutators
{
    public const int DecisionMaxLength = 20_000; public const int SignerTitleMaxLength = 120;
    public const int MaxAnnexes = 10; public const int AnnexMaxLength = 200;
    public Guid ReplyId { get; } public Guid TenantId { get; } public SupportTicketId TicketId { get; }
    public string Decision { get; } public string? NextStep { get; }
    public Guid SignerMemberId { get; } public string SignerName { get; } public string SignerTitle { get; }   // the assignee (H3)
    public IReadOnlyList<string> Annexes { get; }      // jsonb
    public string StorageKey { get; } public string FileName { get; } public long SizeBytes { get; } public string Sha256Hex { get; }
    public DateOnly RespondedOn { get; }               // tenant local date printed in the PDF
    public Guid DocumentClientId { get; }              // clientId of the second Instagram message (H10)
    public DateTimeOffset CreatedAt { get; }
    public static SupportReplyDocument Create(SupportTicketReply reply, string decision, string? nextStep, Guid signerMemberId, string signerName,
        string signerTitle, IReadOnlyList<string> annexes, string storageKey, string fileName, byte[] content, DateOnly respondedOn,
        DateTimeOffset now);
    public static string FileNameFor(string displayNumber) => $"Respuesta-{displayNumber}.pdf";
}
public const string ReplyFormalOrganizationRequired = "support.reply.formal_organization_required";
public const string ReplyDocumentRenderFailed = "support.reply.document_render_failed";
public const string ReplyDocumentNotFound = "support.reply.document_not_found";
public const string ReplyFormalSignerRequired = "support.reply.formal_signer_required";   // owner 2026-11-16, Q-7
```
Table `support.ticket_reply_documents`: `reply_id uuid PK FK → ticket_replies(id) ON DELETE CASCADE`, `tenant_id`, `ticket_id`,
`decision varchar(20000)`, `next_step varchar(2000) null`, `signer_member_id uuid` (index `IX_ticket_reply_documents_signer`), `signer_name varchar(200)`, `signer_title varchar(120)`, `annexes jsonb`,
`storage_key varchar(512)`, `file_name varchar(64)`, `size_bytes bigint CHECK > 0`, `sha256 char(64)`, `responded_on date`,
`document_client_id uuid`, `created_at`. Index `IX_ticket_reply_documents_tenant_ticket`. `SupportDbContext` documents «insert-only;
the only delete is the cascade of a never-sent reply (H9)». A reply with a document is only `Text`/`Template`/`Email`/`Portal` with
`UploadId == null` (the domain guards it in `Create`).

- [ ] **Step 1: tests** — `SupportReplyDocumentTests.ItTrimsAndKeepsTheDecisionWithLineBreaks`, `ItRejectsMoreThanTenAnnexesOrALongOne`,
  `ItRejectsAReplyWithAnAttachment`, `ItHashesTheContent` (SHA-256 hex of the bytes), `TheFileNameCarriesTheTicketNumber`;
  `SupportPersistenceTests.AReplyDocumentRoundTrips` and `RemovingANeverSentReplyRemovesItsDocument` (cascade);
  `SupportUserReferenceProbeTests.TheSignerOfAFormalReplyHoldsTheUser`.
- [ ] **Step 2: RED** — `dotnet build $SU` fails (`CS0246 SupportReplyDocument`).
- [ ] **Step 3: implement.** Migration additive (new table).
- [ ] **Step 4: GREEN.** **Step 5: commit** — `feat(support): store the formal reply document of a reply`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportReplyDocumentTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~AReplyDocumentRoundTrips|FullyQualifiedName~RemovingANeverSentReplyRemovesItsDocument|FullyQualifiedName~TheSignerOfAFormalReplyHoldsTheUser"`.

### Task 3: Renderer, letterhead and storage

**Files:**
- Create: `Modules.Support.Application/{ISupportFormalReplyRenderer,ISupportDocumentStorage,ISupportLetterhead}.cs`,
  `Modules.Support.Application/Replies/SupportFormalReplyDocument.cs` (the data contract), `Modules.Support.Infrastructure/Pdf/{SupportPdfOptions,
  SupportPdfOptionsValidator,SupportPdfRenderer}.cs`, `Pdf/formal-reply.typ` (`<EmbeddedResource>` in the `.csproj`),
  `src/Bootstrapper/{SupportDocumentStorage,SupportLetterhead}.cs`
- Modify: `ISupportOrganizationDirectory.cs` (`SupportOrganizationRef` + `Address`, `Email`, `Phone`, `CityName`, last),
  `src/Bootstrapper/SupportOrganizationDirectory.cs`, `SupportInfrastructureExtensions.cs` (options + validator `ValidateOnStart`, renderer
  singleton by factory with `new HttpClient { Timeout = … }`), `QepServiceCollectionExtensions.cs` (adapters scoped),
  `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (`InterfaceFactories` entry for `ISupportFormalReplyRenderer`),
  `appsettings.json` (`Support:Pdf:BaseUrl`), `k8s/prod-configMap.yaml`, `k8s/prod-secret.yaml`
- Test: `$SU\SupportFormalReplyTemplateTests.cs` (new; `Modules.Support.UnitTests` already references Infrastructure),
  `$BU\SupportPdfOptionsValidatorTests.cs`, `$BU\SupportLetterheadTests.cs`, `$SI\SupportBootstrapperAdaptersTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record SupportPdfLogo(string FileName, byte[] Content);                          // .png or .jpg
public sealed record SupportLetterheadData(string CompanyName, string DocumentTypeCode, string DocumentNumber, string? Address,
    string? CityName, string? Email, string? Phone, SupportPdfLogo? Logo);
public interface ISupportLetterhead
{   // the company of formVersionId if given and of the tenant, else of the current form version; null without a company
    Task<SupportLetterheadData?> FindAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken);
}
public sealed record SupportFormalReplyDocument(            // camelCase JSON: contract with formal-reply.typ
    SupportLetterheadData Letterhead, string TicketNumber, DateOnly ReceivedOn, DateOnly RespondedOn, string TypeName, bool TermIsLegal,
    string RecipientName, string? RecipientDocument, string? RecipientEmail, string? RecipientAddress, bool Anonymous,
    string Subject, string Description, string Decision, string? NextStep, string SignerName, string SignerTitle,
    IReadOnlyList<string> Annexes, string DateFormat);
public interface ISupportFormalReplyRenderer { Task<byte[]> RenderAsync(SupportFormalReplyDocument document, CancellationToken ct); }
public interface ISupportDocumentStorage
{
    Task<string> SaveAsync(Guid tenantId, byte[] content, CancellationToken ct);                // returns the key (H8)
    Task<byte[]> ReadAsync(string storageKey, CancellationToken ct);
    Task<Uri> CreateDownloadUrlAsync(string storageKey, string fileName, TimeSpan lifetime, CancellationToken ct);
    Task DeleteAsync(string storageKey, CancellationToken ct);                                   // best effort, never throws
}
public sealed class SupportPdfOptions { public const string SectionName = "Support:Pdf"; public string BaseUrl { get; init; } =
    "https://qcode-pdf.qcode.co"; public string ApiKey { get; init; } = string.Empty; public int TimeoutSeconds { get; init; } = 30; }
```
`SupportPdfRenderer` = calque of `QCodePdfRenderer` (`POST {BaseUrl}/pdf`, `X-Api-Key`, `{ source, data, filename, assets }`); a non-2xx
throws `SupportDomainException(ReplyDocumentRenderFailed)` with the status code in the message but **not** the body (it can echo the
requester's data); logs the status only. `SupportLetterhead` reads the form versions through `ISupportFormRepository`, the company through
`ISupportOrganizationDirectory`, the logo like `QuotationTenantLogoLookup` (Tenancy + Storage) skipping `image/webp`.

- [ ] **Step 1: tests**
  - `SupportFormalReplyTemplateTests.TheTemplateIsEmbedded` and `TheTemplateReadsOnlyFieldsOfTheDocument` — every `data.<name>` in the
    `.typ` is a camelCase property of `SupportFormalReplyDocument` (regex over the resource; a rename breaks it here, not in production).
  - `SupportPdfOptionsValidatorTests.TheKeyIsRequiredInProduction`, `AnUnresolvedPipelineTokenCountsAsMissing`, `TheBaseUrlMustBeHttps`,
    `OutsideProductionAnEmptyKeyIsAccepted`.
  - `SupportLetterheadTests.ItUsesTheTicketFormCompanyFirst`, `ItFallsBackToTheCurrentFormCompany`, `ItIsNullWithoutACompany`,
    `ItSkipsAWebpLogo` (doubles of the ports).
  - `SupportBootstrapperAdaptersTests.TheDocumentStorageSavesUnderTheSupportPrefixAndReadsBack` (fake object storage of the harness).
- [ ] **Step 2: RED** — builds of `$SU`, `$BU`, `$SI` fail.
- [ ] **Step 3: implement.** The Typst template prints, in order, §8.3 points 1–8 (letterhead with logo, number and dates, recipient,
  subject line «Respuesta a tu {tipo} {radicado} del {fecha}», summary = subject + description, analysis and decision, next step if any,
  signature and annexes). Anonymous: recipient «Solicitante anónimo», no document nor email. Dates with the tenant `DateFormat`. Add
  `Support__Pdf__BaseUrl: "#{SUPPORT_PDF_BASE_URL}#"` to the ConfigMap and `Support__Pdf__ApiKey: "#{SUPPORT_PDF_API_KEY}#"` to the
  Secret, next to the Quotations ones.
- [ ] **Step 4: GREEN.** **Step 5: commit** — `feat(support): render the formal reply PDF through qcode-pdf with its own key`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportFormalReplyTemplateTests"`;
  `dotnet test $BU --filter "FullyQualifiedName~SupportPdfOptionsValidatorTests|FullyQualifiedName~SupportLetterheadTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~TheDocumentStorageSaves"`; `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests"`.

**Block A close** (owner, one at a time): `SupportSettingsApiTests`, `SupportTicketTypesApiTests`, `SupportPersistenceTests`,
`SupportBootstrapperAdaptersTests` (`--filter "FullyQualifiedName~<Class>"`, `-- xUnit.MaxParallelThreads=4`), and `dotnet test $SU`.

**Block A review** (one reviewer): H5–H9, H12; the template never evaluates data as markup; the renderer never logs the response
body; the key is never `Quotations:Pdf:ApiKey`; migration additive; the inventory entry.

## Block B — What Messaging and Notifications expose (Tasks 4–7, one implementer)

### Task 4: Template kinds and the formal reply template

**Files:** the Messaging files `SupportReply` touched in phase 2 (plan phase 2, Task 4), copying the **document-header** path of `Order`
(`OrderTemplateProvisioning.cs`: `ExamplePdf()` with `MinimalPdf`, `UploadTemplateExampleAsync`, `header_handle`):
- Modify: `MessageTemplate.cs` (`SupportFormalReply`, `SupportSurvey` **at the end**; `MessageTemplateName.SupportFormalReplyName =
  "qep_soporte_formal"`, `SupportFormalReplyPrefix = "qep_soporte_formal_"`, `SupportSurveyName = "qep_soporte_encuesta"`,
  `SupportSurveyPrefix = "qep_soporte_encuesta_"`, both prefixes in `IsReserved`; `AdoptAsSupportFormalReply`), `SystemTemplateVariables.cs`
  (`SupportFormalReply` = the `SupportReply` list; `SupportSurvey` = fixed + `radicado`), `SystemTemplateDrafts.cs`, `TemplateManagement*.cs`
  (overview + `SupportFormalReplyTemplate`, `SupportSurveyTemplate` status, last), `TemplateManagementEndpoints.cs`,
  `TemplateManagementPorts.cs` + `MessageTemplateRepository.cs` (`FindSupportFormalReplyAsync`, `FindSupportSurveyAsync`),
  `MessagingDbContext.cs`, `MessagingErrorCodes.cs` (`messaging.template.support_formal_reply_exists|_conflict`,
  `messaging.template.support_survey_exists|_conflict`), `IMessagingAuditRecorder.cs`
  (`messaging.template.support_formal_reply_provisioned`, `messaging.template.support_survey_provisioned`),
  `SupportReplyTemplateProvisioning.cs` (only its `<summary>`: the formal PDF is a separate kind, not a version of this one)
- Create: `Modules.Messaging.Application/SupportFormalReplyTemplateProvisioning.cs`, `Infrastructure/Persistence/SupportFormalReplyTemplateReadiness.cs`,
  migration `AddSupportFormalAndSurveyTemplateKinds` (CHECK with both names; `UX_message_templates_support_formal_reply` and
  `UX_message_templates_support_survey`, partial on `kind`)
- Test: `$MU\SupportFormalReplyTemplateDefinitionTests.cs`, `$MU\SystemTemplateVariablesTests.cs`, `$MI\SupportFormalReplyTemplateProvisioningApiTests.cs`

**Definition:** name by versions like `SupportReply`; `es`; `UTILITY`; header `DOCUMENT`; base body
`"Hola {{nombre_contacto}}, adjuntamos la respuesta formal a tu caso {{radicado}}. {{respuesta}} Si tienes dudas, responde este mensaje."`
(never starts or ends with a variable); example file `respuesta-formal-ejemplo.pdf`. `IsAdoptable`: a `DOCUMENT` header, no buttons,
variables from the list or `fullname`. The text is the tenant's to edit (Q-15).

- [ ] **Step 1: tests** — `SupportFormalReplyTemplateDefinitionTests.NameVersionsAndPrefixAreReserved`, `TheBaseBodyNeitherStartsNorEndsWithAVariable`,
  `ExamplesHaveNoForbiddenCharacters`, `OnlyADocumentHeaderTemplateIsAdoptable`; `SystemTemplateVariablesTests.SupportListsStartWithTheFixedOnes`;
  `SupportFormalReplyTemplateProvisioningApiTests.ProvisionsOnceAndUploadsTheExample`, `ASecondProvisionIs409`,
  `ReadinessReturnsTheApprovedRow`, `TheOverviewShowsBothSupportTemplates`.
- [ ] **Step 2: RED** — `$MU`/`$MI` builds fail. **Step 3: implement.** **Step 4: GREEN.**
- [ ] **Step 5: commit** — `feat(messaging): add the support formal reply template with a document header`.
- Filters: `dotnet test $MU --filter "FullyQualifiedName~SupportFormalReplyTemplateDefinitionTests|FullyQualifiedName~SystemTemplateVariablesTests"`;
  `dotnet test $MI --filter "FullyQualifiedName~SupportFormalReplyTemplateProvisioningApiTests"`.

### Task 5: The survey template

**Files:** Create `SupportSurveyTemplateProvisioning.cs`, `SupportSurveyTemplateReadiness.cs`; Modify the same lists as Task 4 for the
survey (endpoints `/support-survey`, `/support-survey/draft`).

**Definition:** `qep_soporte_encuesta` by versions; `es`; `UTILITY` (Q-15: Meta may require `MARKETING` for a survey; `allow_category_change`
is still not sent, D-T10); no header; base body `"Hola {{nombre_contacto}}, tu caso {{radicado}} quedó resuelto. ¿Cómo calificas la
atención que recibiste?"`; three `QUICK_REPLY` buttons **fixed**: `Buena`, `Regular`, `Mala` (`SupportSurveyTemplateDefinition.ButtonLabels`),
not editable by the tenant. `IsAdoptable`: no header, exactly those three quick replies in that order.

- [ ] **Step 1: tests** — `SupportSurveyTemplateDefinitionTests.TheButtonsAreTheThreeFixedQuickReplies`, `TheBaseBodyNeitherStartsNorEndsWithAVariable`,
  `ATemplateWithOtherButtonsIsNotAdoptable`; `SupportSurveyTemplateProvisioningApiTests.ProvisionsOnceWithTheButtons`, `ASecondProvisionIs409`,
  `ReadinessReturnsTheApprovedRow`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(messaging): add the support survey template with quick replies`.
- Filters: `dotnet test $MU --filter "FullyQualifiedName~SupportSurveyTemplateDefinitionTests"`; `dotnet test $MI --filter "FullyQualifiedName~SupportSurveyTemplateProvisioningApiTests"`.

### Task 6: Sending a document and a survey from Support

**Files:** Modify `Modules.Messaging.Application/SupportReplyOutbound.cs` (3 methods, `SupportReplySendResult.Sent` + `DocumentMessageId`
last), `MessagingInfrastructureExtensions.cs` (readiness registrations). Test `$MI\SupportReplyOutboundTests.cs`.

**Interfaces — Produces:**
```csharp
public sealed record SupportThreadDocumentSend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid DocumentClientId,
    Guid AuthorMemberId, string Text, ReadOnlyMemory<byte> Document, string FileName);
public sealed record SupportFormalTemplateSend(Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId,
    string RecipientName, Guid ClientId, Guid AuthorMemberId, string TicketNumber, string Text, ReadOnlyMemory<byte> Document, string FileName);
public sealed record SupportSurveyTemplateSend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId,
    string RecipientName, string TicketNumber);
// ISupportReplyOutbound:
Task<SupportReplySendResult> SendThreadDocumentAsync(SupportThreadDocumentSend send, CancellationToken ct);
Task<SupportReplySendResult> SendFormalTemplateAsync(SupportFormalTemplateSend send, CancellationToken ct);
Task<SupportReplySendResult> SendSurveyTemplateAsync(SupportSurveyTemplateSend send, CancellationToken ct);
public sealed record Sent(Guid MessageId, Guid ConversationId, Guid? DocumentMessageId = null) : SupportReplySendResult;
```
- `SendThreadDocumentAsync`: same guards as `SendThreadAsync` (not found, already sent by `ClientId`, blocked, window). WhatsApp: caption
  ≤ 1024 else `Rejected(text_invalid)`; upload the bytes to Meta and to R2 like `DocumentTemplateOutbound` (`UploadMediaAsync`,
  `MessagingMediaKeys.For(claim.MessageId)`, `OutboundMedia` in the draft, `MessageKind.Document`), send the document with caption.
  Instagram: send the text (claim `ClientId`), then the file (claim `DocumentClientId`, `OutboundMedia` + R2 + `CreateDownloadUrlAsync`,
  `SendAttachmentAsync(…, "file", url)`); `Sent(textId, conversation, fileId)`. A file that fails after the text went out →
  `Failed(fileMessageId, send_failed)` (the text stays sent; documented).
- `SendFormalTemplateAsync`: the steps of `SendTemplateAsync` with `ISupportFormalReplyTemplateReadiness` and the document header of
  `DocumentTemplateOutbound` (`header` component with the Meta media id).
- `SendSurveyTemplateAsync`: the steps of `SendTemplateAsync` for an existing WhatsApp conversation with `ISupportSurveyTemplateReadiness`;
  values `nombre_contacto`, `radicado` and the fixed ones by `TemplateSenderValues`; buttons travel without parameters (Meta returns the
  label as `button.text`, V-1).
- All idempotent by `ClientId`, `seen: 0`, no `OutboundGuards`, settlement by `OutboundSettlement` (turns off `history_managed`).

- [ ] **Step 1: tests** — `AFormalDocumentInsideTheWindowGoesAsOneDocumentWithCaption`, `AFormalDocumentWithALongCaptionIsRejected`,
  `AnInstagramFormalReplySendsTheTextThenTheFile`, `RetryingAFormalDocumentWithTheSameClientIdsSendsOnce`,
  `AFormalTemplateCarriesTheDocumentHeader`, `AFormalTemplateWithoutApprovalIsRejected`, `ASurveyTemplateGoesToTheConversation`,
  `ASurveyWithoutApprovalIsRejected`, `ASurveyToABlockedContactIsRejected`, `ASurveyDoesNotTakeNorReopenTheConversation`.
- [ ] **Step 2: RED** — `$MI` build fails. **Step 3–4.**
- [ ] **Step 5: commit** — `feat(messaging): send support documents, formal templates and surveys`.
- Filters: `dotnet test $MI --filter "FullyQualifiedName~SupportReplyOutboundTests"`.

### Task 7: Email attachments and the survey email

**Files:** Modify `Modules.Notifications.Application/IEmailChannel.cs` (`EmailMessage` + `IReadOnlyList<EmailAttachment>? Attachments =
null`, last; `EmailAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content)`), `Infrastructure/Channels/InfobipEmailChannel.cs`
(one `attachment` part per file in the multipart), `LogEmailChannel.cs` (logs file names and sizes, never the content),
`SupportReplyEmailTemplate.cs` (`SupportReplyEmailInput` + `bool HasDocument = false`: one more paragraph «Adjuntamos la respuesta formal
de tu caso en PDF.»). Create `SupportSurveyEmailTemplate.cs` (`SupportSurveyEmailInput(ToAddress, TenantName, TicketNumber, TicketSubject,
RatingUrl)`; subject «¿Cómo te atendimos en tu caso {radicado}?», the URL escaped in the HTML).
Test: `$NU\InfobipEmailChannelTests.cs`, `$NU\SupportReplyEmailTemplateTests.cs`, `$NU\SupportSurveyEmailTemplateTests.cs`.

- [ ] **Step 1: tests** — `InfobipEmailChannelTests.AnAttachmentTravelsAsAMultipartPart`, `WithoutAttachmentsTheRequestDoesNotChange`;
  `SupportReplyEmailTemplateTests.AFormalReplyMentionsTheAttachedPdf`; `SupportSurveyEmailTemplateTests.TheLinkIsEscapedAndTheTextTutea`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(notifications): send email attachments and the support survey email`.
- Filters: `dotnet test $NU --filter "FullyQualifiedName~InfobipEmailChannelTests|FullyQualifiedName~SupportReplyEmailTemplateTests|FullyQualifiedName~SupportSurveyEmailTemplateTests"`.

**Block B close:** `SupportReplyOutboundTests`, `SupportFormalReplyTemplateProvisioningApiTests`, `SupportSurveyTemplateProvisioningApiTests`
(one at a time), `dotnet test $MU`, `dotnet test $NU`.

**Block B review:** both kinds at the end of the enum and in the CHECK; reserved prefixes; the document header path copied from
`Order`, not reinvented; idempotency by both client ids; no `OutboundGuards`; the email channel never logs content.

## Block C — Formal reply end to end, survey model and sending (Tasks 8–11, one implementer)

### Task 8: `SendTicketReply` with a formal reply

**Files:**
- Modify: `Replies/SendTicketReply.cs` (request, command, validator, handler), `Replies/GetReplyOptions.cs`, `ISupportReplyChannels.cs`
  (`SendThreadDocumentAsync`, `SendFormalTemplateAsync`, `ListFormalTemplateConnectionsAsync`, `IsFormalTemplateApprovedAsync`;
  `SupportChannelReasons.OrganizationRequired = "organization_required"` and `SignerRequired = "signer_required"` in `All`), `ISupportReplyEmail.cs` (`SupportReplyEmail` +
  `SupportEmailDocument? Document`, last), `src/Bootstrapper/{SupportReplyChannels,SupportReplyEmailSender}.cs`, `SupportDtos.cs`
  (`SupportReplyDto.Document`, `SupportReplyOptionsDto.Formal`), `Tickets/SupportTicketDtoComposer.cs` (documents of the replies, one
  query), `SupportTicket.RecordReply` (details + `formal`), `SupportTicketEventDto` + `bool? Formal` (last, only `ReplySent`)
- Create: `Replies/SupportFormalReplies.cs` (builds the document: letterhead, recipient, next step, signer = assignee, render, store, row)
- Test: `$SU\SendTicketReplyValidatorTests.cs`, `$SI\SupportTicketRepliesApiTests.cs`, `$SI\SupportReplyDoubles.cs` (renderer double
  that records calls), `$BU\SupportFailureCodeParityTests.cs` (still green with the new Support-only code)

**Interfaces — Produces:**
```csharp
public sealed record SupportReplyRequest(Guid? ClientId, string? Channel, string? Text, Guid? ConnectionId, Guid? UploadId, bool Resolve,
    string? FileName = null, SupportFormalReplyRequest? Formal = null);
public sealed record SendTicketReplyCommand(…, string? FileName = null, SupportFormalReplyRequest? Formal = null);
public sealed record SupportEmailDocument(string FileName, ReadOnlyMemory<byte> Content);
```
Handler order (only the new steps; the rest as today): after `RouteAsync`, if `Formal` is present on a **new** reply →
`SupportFormalReplies.PrepareAsync` (assignee or `422 formal_signer_required`, its name by `ISupportMemberDirectory.NamesAsync`;
letterhead or `422 formal_organization_required`; WhatsApp thread caption ≤ 1024 else
`ValidationException` on `Text`; render; `SaveAsync` to R2) → `SupportTicketReply.Start` → `SupportReplyDocument.Create` → one
`SaveChangesAsync` (on the duplicate-client-id race, delete the R2 object best effort). `SendAsync` picks the document variants when the
reply has a document: `Text` → `SendThreadDocumentAsync`, `Template` → `SendFormalTemplateAsync`, `Email` → email with the attachment,
`Portal` → published. A resumed `Pending` with a document reads the bytes from R2 (`ReadAsync`) and sends the same. A `Rejected` new
reply is removed with its document (cascade) and its R2 object (best effort). `TemplateToPhoneAsync` checks
`IsFormalTemplateApprovedAsync` for a formal reply. `Sent.DocumentMessageId` is pinned too.

- [ ] **Step 1: tests**
  - `SendTicketReplyValidatorTests.AFormalReplyNeedsADecisionAndASignerTitle`, `TheDecisionIsAtMost20000`, `AtMostTenAnnexesOf200`,
    `FormalAndAnAttachmentAreExclusive` (key `UploadId`).
  - `SupportTicketRepliesApiTests.AFormalThreadReplySendsOneDocumentAndStoresIt` — 201, `reply.document.fileName == "Respuesta-SOP-…pdf"`,
    the renderer received the decision, next step of the type over the tenant's, signer = assignee name and the typed title; the
    document row stores `signerMemberId`; Messaging double got the bytes.
  - `TheAssigneeSignsNotTheSender` (sender ≠ assignee), `AFormalReplyWithoutAssigneeIs422SignerRequired` (nothing persisted, no render),
    `ReplyOptionsSaySignerRequiredWithoutAssignee`.
  - `AFormalReplyOutsideTheWindowUsesTheFormalTemplate`, `AFormalReplyWithoutTheFormalTemplateIs422TemplateNotApproved`,
    `AFormalEmailReplyAttachesThePdf`, `AFormalPortalReplyIsPublishedWithItsDocument`,
    `AFormalReplyWithoutACompanyIs422OrganizationRequired`, `ARenderFailureIs422AndLeavesNothing` (no reply row, no document row),
    `AResumedFormalReplySendsTheStoredDocument` (renderer called once), `ARejectedFormalReplyRemovesItsDocument`,
    `ReplyOptionsSuggestFormalForATypeThatRequiresIt` and `ReplyOptionsSayOrganizationRequired`.
- [ ] **Step 2: RED** — `$SU`/`$SI` builds fail (`CS1739 Formal`, `CS0117 OrganizationRequired`).
- [ ] **Step 3–4.**
- [ ] **Step 5: commit** — `feat(support): send the formal reply PDF through the ticket channel`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SendTicketReplyValidatorTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~Formal|FullyQualifiedName~ReplyOptionsSay|FullyQualifiedName~TheAssigneeSigns"`; `dotnet test $BU --filter "FullyQualifiedName~SupportFailureCodeParityTests"`.

### Task 9: Downloading the document (console and consult)

**Files:** Create `Replies/GetReplyDocumentDownload.cs`; Modify `SupportEndpoints.cs`, `SupportAuditActions.cs`
(`TicketReplyDocumentDownloaded = "support.ticket.reply_document_downloaded"`), `Public/LookupPublicTicket.cs`, `Public/SupportPublicDtos.cs`
(`SupportPublicReplyDto` + `string? DocumentUrl, string? DocumentFileName`, last). Test `$SI\SupportTicketRepliesApiTests.cs`,
`$SI\SupportPublicLookupApiTests.cs`.

- [ ] **Step 1: tests** — `TheConsoleDownloadsAFormalDocumentAndAudits` (5-minute URL, audit with the ticket as resource),
  `ADocumentOfAnotherTicketIs404`, `AReaderCanDownload` (only `read`), `TheConsultShowsTheDocumentUrlOfAFormalReply` (10-minute URL; null on
  a plain reply), `TheConsultDoesNotShowTheDocumentOfAFailedReply`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): download the formal reply from the console and the consult page`.
- Filters: `dotnet test $SI --filter "FullyQualifiedName~Download|FullyQualifiedName~TheConsultShowsTheDocument|FullyQualifiedName~TheConsultDoesNotShowTheDocument"`.

### Task 10: The survey model, the settings and the request on every resolution

**Files:**
- Create: `Modules.Support.Domain/{SupportTicketSurvey,SupportSurveyStatus,SupportSurveyChannel,SupportSurveyRating}.cs`, migration `AddSupportSurveys`
- Modify: `SupportTicket.cs` (`surveys` field + `Surveys` navigation; `ChangeStatus` to `Resolved` adds one), `SupportTenantSettings.cs`
  (`SurveyEnabled`, `AutoCloseBusinessDays`, `Min/MaxAutoCloseBusinessDays = 1/30`, `DefaultAutoCloseBusinessDays = 5`, `ChangeSurvey`),
  `Settings/*` (request + `bool? SurveyEnabled, int? AutoCloseBusinessDays`, required — **changed 2026-11-16: optional for backward compatibility with the shipped frontend**; null keeps the stored value, the 1–30 range is validated only when present), `SupportDbContext.cs` (table, CHECKs by
  `Enum.GetNames`, indexes, navigation like `Events`), `SupportUnitOfWork.cs` (`UX_ticket_surveys_ticket_resolution` → `concurrency.conflict`
  by name: unreachable after the version check, kept as a backstop), `BuildingBlocks.Application/QueueDoorbell.cs`
  (`DoorbellQueues.SupportSurvey = "support_survey"`, `All`, `SafetyNet` 30 s), `BuildingBlocks.Infrastructure/QueueDoorbellInterceptor.cs`
  (`["support.ticket_surveys"] = DoorbellQueues.SupportSurvey`), `ISupportTicketRepository` (`ListSurveysAsync(tenantId, ticketId)`),
  `SupportDtos.cs` (`SupportSurveyDto`, detail + `Surveys`), `SupportTicketDtoComposer.cs`
- Test: `$SU\SupportTicketSurveyTests.cs` (new), `$SU\SupportTenantSettingsTests.cs`, `$SI\SupportPersistenceTests.cs`,
  `$SI\SupportTicketActionsApiTests.cs`, `$SI\SupportSettingsApiTests.cs`, `$BU\Valkey\QueueDoorbellInterceptorTests.cs`

**Interfaces — Produces:**
```csharp
public enum SupportSurveyStatus { Pending, Sent, Skipped, Failed }
public enum SupportSurveyChannel { WhatsApp, Email, Portal }   // no Instagram (owner 2026-11-16, Q-2)
public enum SupportSurveyRating { Good, Regular, Bad }
public sealed class SupportTicketSurvey
{
    public const int CommentMaxLength = 1000;
    public Guid Id { get; } public Guid TenantId { get; } public SupportTicketId TicketId { get; } public int ResolutionNumber { get; }
    public SupportSurveyStatus Status { get; } public SupportSurveyChannel? Channel { get; } public string? ReasonCode { get; }
    public Guid? MessageId { get; } public byte[]? TokenHash { get; } public int Attempts { get; } public DateTimeOffset NextAttemptAt { get; }
    public DateTimeOffset? LockedUntil { get; } public DateTimeOffset? SentAt { get; }
    public SupportSurveyRating? Rating { get; } public string? Comment { get; } public DateTimeOffset? AnsweredAt { get; }
    public SupportSurveyChannel? AnsweredVia { get; } public Guid? RatingMessageId { get; } public Guid? CommentMessageId { get; }
    public DateTimeOffset? CommentUntil { get; }                                      // tap + 24 h (H18)
    public Guid? PromptMessageId { get; } public DateTimeOffset? PromptSentAt { get; } public string? PromptFailureCode { get; }
    public DateTimeOffset CreatedAt { get; } public DateTimeOffset UpdatedAt { get; }
    internal static SupportTicketSurvey Request(Guid tenantId, SupportTicketId ticketId, int resolutionNumber, DateTimeOffset now);
    public void MarkSent(SupportSurveyChannel channel, Guid? messageId, byte[]? tokenHash, DateTimeOffset now, string? reasonCode = null);
    public void Skip(string reasonCode, SupportSurveyChannel? channel, DateTimeOffset now);
    public void Retry(string code, DateTimeOffset nextAttemptAt, DateTimeOffset now);   // Failed after the third attempt
    public bool IsAnswerable(SupportTicket ticket);                                     // H21
    public void Answer(SupportSurveyRating rating, string? comment, SupportSurveyChannel via, Guid? ratingMessageId,
        DateTimeOffset? commentUntil, DateTimeOffset now);                              // throws already_answered / closed
    public void AddComment(string comment, Guid messageId, DateTimeOffset now);         // only once, only before CommentUntil
    public void RecordPrompt(Guid? messageId, string? failureCode, DateTimeOffset now); // once; no retry (H18)
}
public static class SupportSurveyRatings { public static SupportSurveyRating? FromButtonText(string? text); }   // Buena/Regular/Mala
public const string SurveyNotFound = "support.survey.not_found"; public const string SurveyClosed = "support.survey.closed";
public const string SurveyAlreadyAnswered = "support.survey.already_answered";
// SupportChannelReasons.SurveyDisabled = "survey_disabled" (in All)
```
Table `support.ticket_surveys`: the columns above; FK `ticket_id → tickets(id) RESTRICT`; `UX_ticket_surveys_ticket_resolution
(ticket_id, resolution_number)`; `UX_ticket_surveys_token (token_hash) WHERE token_hash IS NOT NULL`;
`IX_ticket_surveys_pending (next_attempt_at) WHERE status = 'Pending'`; `IX_ticket_surveys_message (message_id) WHERE message_id IS NOT
NULL`; `IX_ticket_surveys_tenant_ticket`. Prompt columns `prompt_message_id uuid null`, `prompt_sent_at timestamptz null`,
`prompt_failure_code varchar(64) null` (a `SupportChannelReasons` code). CHECK `CK_ticket_surveys_comment` (length ≤ 1000), CHECK that a `Sent` row has a channel.
Settings: `survey_enabled boolean NOT NULL DEFAULT false`, `auto_close_business_days int NOT NULL DEFAULT 5` with `CK_tenant_settings_auto_close`
(1–30). The provisioner's `INSERT` relies on the defaults. The survey row is **not** legal proof: it can be updated.

- [ ] **Step 1: tests**
  - `SupportTicketSurveyTests.ResolvingRequestsASurveyWithTheResolutionNumber`, `ReopeningAndResolvingAgainRequestsTheNext`,
    `OtherTransitionsRequestNothing`, `AnswerOnlyOnceAndOnlyWhileAnswerable` (reopened, `Pending`, `survey_disabled`),
    `TheCommentIsCutTo1000AndOnlyBeforeItsWindow`, `ThePromptIsRecordedOnce`, `ThirdFailureBecomesFailed`,
    `FromButtonTextIgnoresCaseAccentsAndEdges`.
  - `SupportTenantSettingsTests.AutoCloseIsBetween1And30AndTheSurveyIsOffByDefault`.
  - `SupportPersistenceTests.ASurveyRoundTrips` and `TheResolutionUniqueIndexRejectsADuplicate` (SQL insert ⇒ `23505` on that name).
  - `SupportTicketActionsApiTests.EveryResolutionRequestsOneSurvey` (status change and «Enviar y marcar Resuelto»),
    `ReopeningAndResolvingAgainRequestsASecondSurvey`, `TheDetailListsTheSurveys`.
  - `SupportSettingsApiTests.TheSurveyToggleAndTheAutoCloseDaysRoundTrip`, `AutoCloseOutOfRangeIs422`.
  - `QueueDoorbellInterceptorTests.AnInsertIntoTicketSurveysRingsItsQueue`.
- [ ] **Step 2: RED.** **Step 3: implement** (migration additive). **Step 4: GREEN.**
- [ ] **Step 5: commit** — `feat(support): request a satisfaction survey on every resolution`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportTicketSurveyTests|FullyQualifiedName~SupportTenantSettingsTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~Survey|FullyQualifiedName~AutoCloseOutOfRange"`;
  `dotnet test $BU --filter "FullyQualifiedName~QueueDoorbellInterceptorTests"`.

### Task 11: The survey worker (channel, send, skip, retry)

**Files:**
- Create: `Application/Surveys/{SupportSurveySender,SupportSystemAuthor}.cs`, `ISupportSurveyEmail.cs`,
  `Infrastructure/SupportSurveyWorker.cs`, `Infrastructure/Persistence/SupportSurveyRepository.cs` (`ClaimAsync(take)` with
  `FOR UPDATE SKIP LOCKED` and a 2-minute `locked_until`), `src/Bootstrapper/SupportSurveyEmailSender.cs`
- Modify: `Opening/SupportOpeningSender.cs` (uses `SupportSystemAuthor`), `ISupportReplyChannels.cs` (+ `SendSurveyTemplateAsync`,
  `IsSurveyTemplateApprovedAsync`) and its adapter, `SupportWorkerOptions.cs` (`SurveyIntervalSeconds`, `SurveyCommentWindowHours`,
  `SurveyAnswerLookbackDays`, `AutoCloseIntervalMinutes`), `SupportInfrastructureExtensions.cs` (worker under `Support:Workers:Enabled`),
  `MultiReplicaInventory.cs` (`SupportSurveyWorker` = `Claimed`), `tests/…/SupportApiHarness.cs` + `SupportHostWithoutWorkers.cs` (remove the
  new workers by `ImplementationType`), `src/Bootstrapper/Observability/{QueueNames,QueueMetricsSource}.cs` (`support_survey`: pending
  count and age of the oldest `Pending` with `attempts = 0`)
- Test: `$SU\SupportSurveySenderTests.cs` (doubles), `$SI\SupportSurveyApiTests.cs` (own host without workers, calls the sender by hand),
  `$BU\QueueMetricsSourceTests.cs`, `$AT` (`MultiReplicaInventoryTests`)

Sender per claimed row (H16, H17): tenant survey off → `Skip(survey_disabled)`; ticket anonymous or `ReplyChannel = Portal` →
`MarkSent(Portal)`; WhatsApp conversation → Messaging module off `Skip(messaging_disabled)`, no approved `SupportSurvey` on the
conversation's connection `Skip(template_not_approved)`, else `SendSurveyTemplateAsync` with `ClientId = survey.Id`; `ReplyChannel =
Email` with an email → survey email; an Instagram conversation → **nothing through Instagram** (owner 2026-11-16, Q-2): the survey email if
the requester snapshot has an email, else `MarkSent(Portal)`; anything else → `MarkSent(Portal)`. `Rejected` reasons map to `Skip`; `send_failed`/`connection_unavailable` → `Retry`. Every outcome
commits with `PublishSystem("support.ticket.survey_sent" | "support.ticket.survey_skipped" | "support.ticket.survey_failed", ["survey"])`
and then `TicketChangedAsync`.

- [ ] **Step 1: tests** — `ASurveyOffTenantSkipsWithSurveyDisabled`, `AnAnonymousTicketIsPublishedOnThePortal`,
  `AWhatsAppTicketSendsTheTemplateWithTheSurveyAsClientId`, `WithoutTheTemplateTheTicketIsWithoutSurvey`,
  `AnInstagramTicketWithAnEmailGetsTheEmailLink`, `AnInstagramTicketWithoutEmailIsPortalOnly` (no Messaging call in either),
  `AnEmailTicketGetsTheEmailWithATokenAndOnlyItsHashIsStored`,
  `ASendFailureRetriesThreeTimesThenFails`, `NoAuthorSkips`, `TwoWorkersNeverClaimTheSameRow` (integration, `SKIP LOCKED`),
  `ProcessingAnnouncesTheTicket`; `QueueMetricsSourceTests.TheSupportSurveyQueueIsMeasured`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): send the satisfaction survey by the ticket channel`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportSurveySenderTests"`; `dotnet test $SI --filter "FullyQualifiedName~SupportSurveyApiTests"`;
  `dotnet test $BU --filter "FullyQualifiedName~QueueMetricsSourceTests"`; `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~CompositionRootTests"`.

**Block C close:** `SupportTicketRepliesApiTests`, `SupportPublicLookupApiTests`, `SupportTicketActionsApiTests`, `SupportSettingsApiTests`,
`SupportSurveyApiTests`, `SupportPersistenceTests` (one at a time), `dotnet test $SU`.

**Block C review:** H1–H23 as built; the resume path never re-renders; every path to `Resolved` passes through the domain; the worker
classification and the doorbell; the token hash only; no ticket version bump from a survey change.

## Block D — Answers, auto-close, reports and documentation (Tasks 12–16, one implementer)

### Task 12: Capturing the WhatsApp answer, the comment prompt and the comment

**Files:** Create `ISupportSurveyFeed.cs`, `Application/Surveys/SupportSurveyAnswers.cs` (runs inside the survey worker tick, after the
sends), `Application/Surveys/SupportSurveyPrompt.cs` (the text and `ClientIdFor(surveyId)`), `src/Bootstrapper/SupportSurveyFeed.cs`.
Modify `src/Bootstrapper/CustomerDeletionReferences.cs` (null `ticket_surveys.message_id`, `rating_message_id`, `comment_message_id`,
`prompt_message_id` of the tickets whose conversation is unlinked).

Per tap (H18, owner 2026-11-16, Q-11): `Answer(…, commentUntil: tap.OccurredAt + 24 h)` and commit; then, if the prompt was never
recorded, `SendThreadAsync` with the author rule of H17, `ClientId = SupportSurveyPrompt.ClientIdFor(survey.Id)` and the text
«Gracias. Si quieres, cuéntanos en un mensaje por qué elegiste esa opción.»; `RecordPrompt` with the message id or the
`SupportChannelReasons` code (`no_author` included), commit, `TicketChangedAsync`. A crash between the send and `RecordPrompt` re-sends on
the next tick with the same `ClientId`: Messaging returns the message already sent. No retry after a recorded failure, no reminder.
Test `$SI\SupportSurveyApiTests.cs`, `$SI\SupportBootstrapperAdaptersTests.cs`, `$CI\CustomerDeletionAcrossModulesTests.cs`,
`$BU\SupportSurveyParityTests.cs` (new).

**Interfaces — Produces:**
```csharp
public sealed record SupportSurveyTap(Guid TenantId, Guid SurveyId, Guid MessageId, string? Text, DateTimeOffset OccurredAt);
public sealed record SupportSurveyComment(Guid TenantId, Guid SurveyId, Guid MessageId, string Text);
public interface ISupportSurveyFeed
{   // raw SQL over support.ticket_surveys and messaging.messages, all tenants, within SurveyAnswerLookbackDays
    Task<IReadOnlyList<SupportSurveyTap>> ListTapsAsync(int take, CancellationToken ct);       // Sent, WhatsApp, rating null: first Interactive inbound quoting message_id
    Task<IReadOnlyList<SupportSurveyComment>> ListCommentsAsync(int take, CancellationToken ct);   // rated by WhatsApp, comment null, now < comment_until: first Text inbound after the tap
}
```
Reads by name: `messaging.messages` (`id`, `tenant_id`, `conversation_id`, `direction`, `kind`, `text`, `reply_to_message_id`,
`occurred_at`, `created_at`, `imported`). A tap whose text is not a label, or whose survey is no longer answerable, is ignored (the
survey stays unrated; it leaves the feed after the lookback).

- [ ] **Step 1: tests** — `ATapOnTheSurveyRatesItWithItsMessage`, `ThePromptIsSentOnceAfterTheTap` (two ticks, one message; same
  `ClientId` on a resend), `ThePromptDoesNotTakeTheConversation` (assignee, status and unread unchanged), `AFailedPromptIsNotRetried`,
  `TheNextTextIsTheCommentAndALaterOneIsNot`, `ACommentAfterTheWindowIsIgnored`,
  `ATapOnAnOldSurveyRatesNothing`, `ATapWithAnUnknownTextRatesNothing`, `ATapRatesOnlyOnce`, `AnAnswerAnnouncesTheTicket`;
  `SupportBootstrapperAdaptersTests.TheSurveyFeedRunsAgainstTheMigratedSchema`; `CustomerDeletionAcrossModulesTests` (extend the existing
  deletion scenario: the survey message ids are null after the deletion); `SupportSurveyParityTests.TheButtonLabelsMatchTheMessagingTemplate`
  (`SupportSurveyRatings` vs `SupportSurveyTemplateDefinition.ButtonLabels`).
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): capture the WhatsApp survey answer, prompt for and store the comment`.
- Filters: `dotnet test $SI --filter "FullyQualifiedName~Tap|FullyQualifiedName~Comment|FullyQualifiedName~Prompt|FullyQualifiedName~TheSurveyFeedRuns"`;
  `dotnet test $CI --filter "FullyQualifiedName~CustomerDeletionAcrossModulesTests"`; `dotnet test $BU --filter "FullyQualifiedName~SupportSurveyParityTests"`.

### Task 13: Rating from the consult page and from the link

**Files:** Create `Public/SupportPublicAccess.cs` (the verification of `LookupPublicTicketHandler` extracted: Turnstile, tenant,
`LockByNumberAsync`, PBKDF2 against hash or decoy, failure count, lock), `Public/RatePublicTicket.cs`, `Public/PublicSurveys.cs`
(`LookupPublicSurveyCommand`, `AnswerPublicSurveyCommand`, validators). Modify `LookupPublicTicket.cs` (uses the extracted verification;
response + `Survey`), `SupportPublicDtos.cs`, `PublicSupportEndpoints.cs` (three routes with the `lookup` limiter; bodies read with the
64 KB cap like the others), `ISupportTicketRepository` (`FindSurveyByTokenHashAsync`, `FindLatestSurveyAsync`).
Test `$SI\SupportPublicSurveyApiTests.cs` (new, `SupportPublicRealAuthHost` for the CSRF/cookie case), `$SI\SupportPublicLookupApiTests.cs`,
`$SU\PublicSurveyValidatorTests.cs`.

Rules: rating ∈ `Good|Regular|Bad` (exact name, else `validation.failed` on `Rating`); comment ≤ 1000 trimmed, no control characters except
line break; the token is 43 base64url characters (else 404, same body as unknown). `answer` hashes the token and loads the survey by
`UX_ticket_surveys_token`, filtered by the slug's tenant. `tickets/rating` failures are exactly those of `lookup` (same code, same body,
same counting); a correct PIN with a non-answerable survey → `422 support.survey.closed`. Answers set `AnsweredVia = Portal` (consult) or
`Email` (link). After commit, `TicketChangedAsync`. Audit `support.ticket.survey_answered` (`PublishSystem`,
`["survey"]`).

- [ ] **Step 1: tests** — `TheLinkShowsTheSurveyAndAnswersItOnce`, `AnUnknownOrMalformedTokenIs404WithTheSameBody`,
  `ATokenOfAnotherTenantIs404`, `TheConsultRatesAnAnonymousTicket`, `AWrongPinOnRatingCountsLikeALookup` (five ⇒ locked),
  `ARatingOnAReopenedTicketIs422Closed`, `TheLookupSaysWhetherItCanBeRated`, `TheSurveyTokenNeverReachesLogsAuditOrEvents` (host in
  `Trace`), `ThePublicSurveyRoutesIgnoreTheSessionCookieWithRealAuth`; `PublicSurveyValidatorTests.TheRatingIsOneOfThreeAndTheCommentIsAtMost1000`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): rate a ticket from the consult page and from the survey link`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~PublicSurveyValidatorTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~SupportPublicSurveyApiTests|FullyQualifiedName~SupportPublicLookupApiTests"`.

### Task 14: Auto-close

**Files:** Create `ISupportAutoCloseFeed.cs`, `AutoClose/SupportAutoCloser.cs`, `Infrastructure/SupportAutoCloseWorker.cs`,
`src/Bootstrapper/SupportAutoCloseFeed.cs`. Modify `SupportInfrastructureExtensions.cs`, `MultiReplicaInventory.cs`
(`SupportAutoCloseWorker` = `Noisy`: the second pod loses on the ticket version), `SupportTicketDtoComposer.cs` and `SupportDtos.cs`
(`SupportClockDto.AutoCloseOn`), `SupportAuditActions.cs` (none new: `support.ticket.status_changed` as the system), harness removals.
Test `$SU\SupportAutoCloserTests.cs`, `$SI\SupportAutoCloseApiTests.cs` (own host without workers, `MutableTenantClock`),
`$SI\SupportBootstrapperAdaptersTests.cs`.

**Interfaces — Produces:**
```csharp
public sealed record SupportAutoCloseCandidate(Guid TenantId, Guid TicketId, DateTimeOffset ResolvedAt, DateTimeOffset? LastCustomerReplyAt);
public interface ISupportAutoCloseFeed
{
    Task<IReadOnlyList<SupportAutoCloseCandidate>> ListCandidatesAsync(int take, CancellationToken ct);    // status Resolved, resolved_at < now() - 1 day, oldest first
    Task<DateTimeOffset?> LastCustomerReplyAsync(Guid tenantId, Guid ticketId, DateTimeOffset after, CancellationToken ct);
}
public static DateOnly AutoCloseOn(ISupportBusinessCalendar calendar, DateOnly resolvedOn, DateOnly? lastReplyOn, int businessDays);
```
The last customer reply (H22, owner 2026-11-16, Q-4): max `ticket_messages.occurred_at` of the ticket after `resolved_at` whose link
`state IN ('Linked', 'Pinned')` **now** (a `Released` link does not count; the date is recomputed every tick and every detail read, so releasing moves
`autoCloseOn` back), whose message is `direction = 1`, not imported, and is not a survey answer (not `Interactive` quoting one of the
ticket's survey `message_id`s, not a survey `comment_message_id`). Reads `support.ticket_messages.state` by name too. A `Pinned` customer
message counts as well: the owner's rule is «still in the ticket; releasing it removes it» (2026-11-16). Test:
`APinnedReplyAlsoHoldsTheClose`. Closing (H22): reload tracked, still `Resolved` with the same `ResolvedAt`, transaction: `ChangeStatus(Closed,
null, …)`, `TicketClosed` in the thread if there is a conversation, `PublishSystem(status_changed, ["status"])`, commit; then
`TicketChangedAsync` and `NotifyChangedAsync`. A `RequestConcurrencyException` skips the ticket (logged at `Debug`).

- [ ] **Step 1: tests** — `SupportAutoCloserTests.ItClosesTheDayAfterTheNthBusinessDay`, `HolidaysAndWeekendsDoNotCount` (CO calendar
  double); `SupportAutoCloseApiTests.AResolvedTicketClosesAfterNBusinessDays` (trace `StatusChanged` without actor, audit as system,
  `TicketClosed` in the thread), `ACustomerReplyAfterResolvingRestartsTheCount`, `ReleasingTheReplyMovesTheCloseDateBack`,
  `ASurveyAnswerDoesNotHoldTheClose`, `TheSurveyCommentDoesNotHoldTheClose`, `AClosedTicketCanStillBeRated` (H21),
  `AReopenedTicketIsNotClosed`, `TheDetailShowsAutoCloseOn`, `ClosingAnnouncesTheTicket`;
  `SupportBootstrapperAdaptersTests.TheAutoCloseFeedRunsAgainstTheMigratedSchema`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): auto-close resolved tickets after N business days`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~SupportAutoCloserTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~SupportAutoCloseApiTests|FullyQualifiedName~TheAutoCloseFeedRuns"`;
  `dotnet test $AT --filter "FullyQualifiedName~MultiReplicaInventoryTests"`.

### Task 15: Reports

**Files:** Create `Application/Reports/GetSupportReport.cs` (query, validator, handler), `ISupportReportRepository.cs` +
`Infrastructure/Persistence/SupportReportRepository.cs` (two `AsNoTracking` projections: tickets of the range, surveys of those tickets).
Modify `SupportDtos.cs`, `SupportEndpoints.cs` (`GET /reports/summary`, `RequireAuthorization(SupportPolicies.TicketView)`; the handler
calls `EnsureCanViewAsync`). Test `$SU\GetSupportReportValidatorTests.cs`, `$SU\SupportReportCalculatorTests.cs`, `$SI\SupportReportsApiTests.cs`.

UTC bounds from the local dates in the tenant zone (`[from 00:00, to + 1 day 00:00)` local). `OverdueNow` with `SupportDueStates.Evaluate`
and the tenant threshold. Ratings: answered surveys of the population, any cycle. `Disabled` = `Skipped survey_disabled`; `WithoutSurvey`
= other `Skipped`.

- [ ] **Step 1: tests** — `GetSupportReportValidatorTests.BothDatesAreRequiredFromBeforeToAndAtMost366Days`;
  `SupportReportCalculatorTests.TheBusinessDaysToResolveCountFromTheDayAfterReceptionToTheResolutionDay`, `TheMedianOfAnEvenCount`,
  `OnTimeLateAndWithoutTermAddUpToResolved`, `SurveyBucketsAddUpToRequested`;
  `SupportReportsApiTests.EveryStatusTypeAndRatingTravelsEvenInZero`, `TicketsOutsideTheRangeDoNotCount` (local midnight edges),
  `AReaderCanSeeTheReport`, `AnotherTenantIs403`, `ARangeOver366DaysIs422`.
- [ ] **Step 2–4.** **Step 5: commit** — `feat(support): report tickets by type, deadline, resolution time and satisfaction`.
- Filters: `dotnet test $SU --filter "FullyQualifiedName~GetSupportReportValidatorTests|FullyQualifiedName~SupportReportCalculatorTests"`;
  `dotnet test $SI --filter "FullyQualifiedName~SupportReportsApiTests"`.

### Task 16: Documentation

**Files:** `README.md` § Soporte (new routes, settings, the two templates and how to provision them, `Support:Pdf`, the workers and their
options, the rollout table above, the R2 prefix without lifecycle, V-1..V-3 to verify against Meta), `CLAUDE.md` Support gotchas (below),
spec corrections (§8.3, §8.4, §8.5, §10 — «Spec discrepancies» 1–6), `SupportReplyTemplateProvisioning.cs` summary already done in Task 4.

CLAUDE.md gotchas to add, one bullet each:
1. A formal reply is rendered once: a resumed `Pending` sends the **stored** document; `ticket_reply_documents` is insert-only and its
   `Down` is not run; nothing under `support/` in R2 has a lifecycle rule.
2. `Support:Pdf:ApiKey` is its own `qcode-pdf` key; never `Quotations:Pdf:ApiKey`; required in `Production`.
3. Every transition to `Resolved` requests the survey **in the domain** (`ChangeStatus`): a new path to `Resolved` gets it for free; one
   that bypasses `ChangeStatus` (raw SQL) leaves the resolution without a survey.
4. `SupportSurveyFeed` and `SupportAutoCloseFeed` read `messaging.messages` by name (`reply_to_message_id`, `kind`, `text`,
   `direction`, `imported`); renaming compiles and stops answers and auto-close silently; tied by `SupportBootstrapperAdaptersTests`.
5. The survey buttons are fixed (`Buena`, `Regular`, `Mala`) in two copies tied by `SupportSurveyParityTests`; a tap counts only for the
   survey it quotes.
6. A survey change never bumps the ticket version, but it notifies the hub.
7. The rating token travels in the body, never in a route; only its SHA-256 is stored.
8. The formal reply is signed by the ticket's assignee; `ticket_reply_documents.signer_member_id` is in `SupportUserReferenceProbe`.
9. Auto-close counts only customer replies whose link is `Linked` or `Pinned` right now: releasing one moves the close date back; the survey tap and
   comment never count.
10. The comment prompt is sent once per survey with a deterministic `ClientId` through the support reply outbound: it is not an advisor
   reply and never takes the conversation. No survey goes through Instagram.

- [ ] **Step 1–4:** build clean (docs only). **Step 5: commit** — `docs(support): document the formal reply, the survey, auto-close and reports`.

**Block D close:** `SupportSurveyApiTests`, `SupportPublicSurveyApiTests`, `SupportAutoCloseApiTests`, `SupportReportsApiTests`,
`SupportBootstrapperAdaptersTests`, `CustomerDeletionAcrossModulesTests` (one at a time), `dotnet test $SU`, `dotnet test $BU`.

**Block D review:** H18–H25; the feeds' SQL against the CLAUDE.md read-by-name rules; the public routes (CORS, CSRF exemption by
prefix, `no-store`, limiter, no session); the token and PIN never logged; report totals close; the auto-close never closes a reopened
ticket.

**Before merging to `develop`** (owner): touched projects whole, one at a time: `Modules.Support.UnitTests`,
`Modules.Support.IntegrationTests` (`-- xUnit.MaxParallelThreads=4`), `Modules.Messaging.UnitTests`, `Modules.Messaging.IntegrationTests`,
`Modules.Notifications.UnitTests`, `Modules.Customers.IntegrationTests` (`CustomerDeletionAcrossModulesTests` at least),
`Bootstrapper.UnitTests`, `ArchitectureTests` (`SupportLayerTests`, `MultiReplicaInventoryTests`, `CompositionRootTests`). **Final review**
(most capable model, whole branch against `develop`).

---

## Owner decisions (2026-11-16)

The owner answered all twelve blocking questions on 2026-11-16. The plan above already reflects each one.

**Accepted as proposed:**

- **Q-1** Separate template kind `SupportFormalReply` (`qep_soporte_formal`) for the formal PDF outside the window (H10). (owner, 2026-11-16)
- **Q-3** Reports inside Support, `GET /support/reports/summary` (H24). (owner, 2026-11-16)
- **Q-5** Two texts: `text` is the channel message, `formal.decision` the PDF body (H2). (owner, 2026-11-16)
- **Q-6** Letterhead company: the ticket's form version company, else the current form's, else `422
  support.reply.formal_organization_required` (H5). (owner, 2026-11-16)
- **Q-8** Annexes are a printed text list only (H4). (owner, 2026-11-16)
- **Q-9** A survey row is always created on resolution; skipped `survey_disabled` when the tenant has it off (H15). (owner, 2026-11-16)
- **Q-10** A survey is answerable until the ticket is reopened, also after the auto-close (H21). (owner, 2026-11-16)
- **Q-12** No Turnstile on the rating routes; token in the body (H19). (owner, 2026-11-16)

**Changed by the owner:**

- **Q-2** No survey through Instagram at all. WhatsApp contact → `qep_soporte_encuesta` with three buttons; email contact → rating
  link email; Instagram ticket → email link if it has an email, else the consult page only; anonymous → consult page only. No Instagram
  survey task, and no block on creating tickets from Instagram conversations (no tenant uses Instagram yet). (owner, 2026-11-16) —
  H16, Tasks 10 (`SupportSurveyChannel` without Instagram), 11 and 13.
- **Q-4** Auto-close restarts the N business days only with customer inbound whose link is **currently** `Linked`; releasing the
  message removes it and the date falls back; survey tap and comment never count; N per tenant 1–30, default 5. (owner, 2026-11-16) —
  H22, Task 14, Review Focus 4.
- **Q-7** The ticket's **assignee** signs; the title is a required free text typed at send; no assignee → `422
  support.reply.formal_signer_required` (+ `signer_required` in `reply-options`); the document stores signer member id, name snapshot
  and title, so `SupportUserReferenceProbe` gains a column. (owner, 2026-11-16) — H3, Tasks 2 and 8, Review Focus 8.
- **Q-11** After the tap, one free-form prompt inside the window it opened: «Gracias. Si quieres, cuéntanos en un mensaje por qué
  elegiste esa opción.»; idempotent by a deterministic `ClientId` per survey, through the support reply outbound (not an advisor reply,
  never takes the conversation); the next customer text within 24 h of the tap is the comment; no retry nor reminder. (owner,
  2026-11-16) — H18, Tasks 10 (prompt columns) and 12, Review Focus 7.

**Deploy only, defaults kept** (owner, 2026-11-16, no objection):

- **Q-13** `qcode-pdf` key for Support (spec §13.5): create it and set `SUPPORT_PDF_API_KEY` (and `SUPPORT_PDF_BASE_URL`) in
  `Backend-prod` **before** this reaches `main`: without it the pod does not start.
- **Q-14** Legal «next step» text per ticket type (spec §13.6): loaded by each tenant in settings; empty → the PDF omits section 7.
- **Q-15** Texts and Meta category of `qep_soporte_formal` and `qep_soporte_encuesta` (spec §13.8). Default `UTILITY`; Meta may classify a
  survey as `MARKETING` (V-2). An approved template is never edited: a change is a new version.
- **Q-16** Who sees the report: everyone with `support.ticket.read` or `manage`.
- **Q-17** Report population and measure (H25): by reception date, gross business days to the last resolution.
- **Q-18** No lifecycle rule on the `support/` prefix of the private bucket (10-year retention, §13.10).
- **Q-19** Auto-close default 5 business days, range 1–30, always on.
- **Q-20** Survey off by default for every tenant.
- **Q-21** Resolutions made by an old pod during the rollout get no survey (H23).

**Settled (2026-11-16):** «still linked» means the message is still in the ticket's record, so both `Linked` and `Pinned` hold the
auto-close; only `Released` drops out (Task 14, `APinnedReplyAlsoHoldsTheClose`).

## Spec discrepancies (spec against code)

1. §8.5 lists one «Aviso de respuesta» whose formal variant carries the PDF as document header; a Meta template cannot have an optional
   header. The code already has `SupportReply` without header (phase 2), and its summary said the PDF «llegará como una versión nueva»;
   4b adds `SupportFormalReply` (Q-1) and corrects both.
2. §8.4 «Instagram: mensaje con los mismos botones»: the owner dropped any survey through Instagram (2026-11-16, Q-2); an Instagram
   ticket gets the email link or the consult page. Task 16 corrects §8.4.
3. §8.3 «se adjunta al canal de origen» by email: `IEmailChannel`/`EmailMessage` have no attachments; Task 7 adds them.
4. §8.3.1 «Membrete del tenant: razón social, NIT, dirección y correo»: the tenant has none of those (`Tenant.cs`); they live on
   `Company`. The plan uses the company of the form (Q-6).
5. §8.3.8 «Firma del responsable (nombre y cargo)»: the assignee signs; no job title exists for a member, so the title is free text per
   reply (owner 2026-11-16, Q-7).
6. §8.4 «a los N días hábiles sin respuesta del cliente» with P4 (an inbound does not reopen a `Resolved`, phase 2): the owner decided the
   count restarts on a customer reply whose link is currently `Linked` (2026-11-16, Q-4).
7. §10 «Reportes… Si Reporting las lee por SQL crudo»: the plan keeps them in Support, with no cross-module SQL (Q-3).

## To verify against real Meta (handoff)

- **V-1** A template with three `QUICK_REPLY` buttons sent **without** button parameters is accepted, and the tap arrives as `type:
  "button"` with `button.text` = the label and `context.id` = the template message (so `reply_to_message_id` resolves).
- **V-2** `qep_soporte_encuesta` approved as `UTILITY` (else as `MARKETING`, with its cost per Meta's pricing).
- **V-3** Instagram accepts the PDF as a `file` attachment from a 10-minute presigned R2 URL (same family as the open V-4/V-8 of
  Instagram media in `CLAUDE.md`).
