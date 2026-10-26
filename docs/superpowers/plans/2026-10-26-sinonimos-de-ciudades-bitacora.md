# Bitácora de ejecución — sinónimos de ciudades (2026-10-26)

Decisiones (Ruling), pendientes menores y estado por tarea de la ejecución por subagentes. Backend primero, frontend después.

## Backend

# SDD ledger — plan: docs/superpowers/plans/2026-10-26-sinonimos-de-ciudades-backend.md

Worktree: C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\sinonimos-de-ciudades
Branch: feature/sinonimos-de-ciudades — merge base 7d6479ad (develop at 801f071a + spec 58753531 + plan 7d6479ad)
Spec: docs/superpowers/specs/2026-10-26-sinonimos-de-ciudades-design.md
Owner authorization (2026-10-26): implement, commit, merge to develop (local, no push), delete worktree.

Ruling: pre-flight conflict scan delegated to a read-only subagent instead of the controller reading all 1503 lines — keeps controller context for 14 task loops — cost if wrong: a cross-task conflict surfaces later in a task review instead of up front.

## Pre-flight (table: preflight.md, 114 rows, 18 findings) — all accepted
Ruling P1 (SC-06): ShopifyShippingFingerprint lives in Shopify.Domain, not Application — ShopifyLayerTests forbids Domain→Application — cost if wrong: none, pure placement.
Ruling P2 (SC-02): Geography.Application references Modules.Audit.Domain (AuditActorType) + regenerate lock files — needed by IGeographyAuditRecorder — cost: one extra ProjectReference.
Ruling P3+P4 (SC-09): reuse Tenancy's OperatorAuthorization (make it public in Tenancy.Application) instead of a verbatim copy GeographyOperatorAuthorization; unit tests target it — avoids duplicated logic — cost: Tenancy API surface grows by one class.
Ruling P5 (SC-09): GeographyLayerTests allows only Tenancy and Audit (not Identity) — Global Constraint wins — cost: if Identity is truly needed, task reports BLOCKED.
Ruling P6 (SC-09): CountCorrectionsAsync for an empty set returns one zero entry per contract, test expects that — BFF contract wins.
Ruling P7 (SC-11): SC-11 implements both GeographyEventPublisher methods (resolved + corrected) and both golden payload tests in GeographyEventPublisherTests.
Ruling P8: ListBySynonymAsync moves from SC-06 to SC-07, with its test.
Ruling P9 (SC-13): the IExternalOrderWriter change gets a Quotations test, the service file and FakeExternalOrderWriter double updated, all in git add.
Ruling P10: PartyCityOnly = !IsStorePickup && ShippingPartyFields(...) is not null — store pickup orders never get a shipping party.
Ruling P11: desvío S10 wording fixed (import does not require email at ShopifyOrderImporter.cs:299; Order.EnsureCreatable rejects incomplete party) — import behavior unchanged; party_city_only still requires email.
Ruling P12: customer corrections audited once, by IShopifyAuditRecorder (S9); SC-05 adds no Customers audit.
Ruling P13: every task's RED step runs the same test set as its GREEN step (integration classes included).
Ruling P14 (SC-14): end-to-end tests accepted as characterization tests (no RED required) — documented in report.
Ruling P15 (SC-13): backfill fills CustomerId per S4 (only the customer this import created incomplete).
Ruling P16: Review Focus gotcha 4 pinned by AResolveJobUsesTheSynonymsCurrentCity and JobsOfADeletedSynonymAreSkipped.
Ruling P17: CitySynonym.Create receives the row's NormalizedText, never re-normalizes truncated text.
Ruling P18: text drift fixes (five classes not six; SC-07 Bootstrapper file; SC-12 consumes SC-07; line :74 not :101) — implementers follow preflight.md wording.
Implementers: read the rulings above that name your task.
BE Task 1: dispatched, BASE 7d6479ad
Task 1: Ruling: brief self-contradiction on UnresolvedCity.UpdatedAt — add UpdatedAt (set by Create and every transition); SC-02 must map an updated_at column — operator wants "when was it resolved/discarded" — cost if wrong: one unused column.
Task 1: Ruling: reviewer Minor 1 (Truncate may leave trailing space / split surrogate in a UNIQUE key) promoted into the fix round — NormalizedText is the dedupe key of SC-03 — cost: none.
Task 1: minor (deferred): property UnresolvedCity.CityText collides with class CityText (brief-mandated name).
Task 1: minor (deferred): CitySuggestions ties with same Name+similarity lack a deterministic tiebreak (ThenBy CityId).
Task 1: fix round 1/5 (5 addressed pending re-review; commits a455e024..846599fb). Ruling: CRLF ENDOFLINE-only format errors on files already CRLF at HEAD are the repo baseline (~90k), not a finding — converting would bloat the diff — cost: none.
Task 1: re-review: 5/5 addressed; low: Truncate TrimEnd only when over max (cosmetic, sampleAddress).
Task 1: minor (deferred): UnresolvedCity.Create accepts an empty normalized text (carry to SC-03: reporter must ignore blank texts).
Task 1: complete (commits 7d6479ad..846599fb, review clean)
Task 2: dispatched, BASE 846599fb — carry: map UnresolvedCity.UpdatedAt (updated_at column); P2 (Audit.Domain ref + lock files).
Note: main checkout (develop) gained unrelated changes at 12:39:02 (poll-interval visibility in AuditProjectionWorker/QuotationEventsWorker/PaymentProofMoveWorker + 6 untracked *WorkerTests/OptionsTests). Not part of SC-02's scope (geography) and not in our branch — treated as someone else's work; left untouched; reported to owner.
Task 2: implementer DONE_WITH_CONCERNS (d1c42ead): SourceRef string→Guid in SC-01 domain; LockCityTextAsync untested (SC-03/SC-11 exercise it); sample_address 600 duplicated in DbContext.
Task 2: Ruling: SourceRef string→Guid accepted (brief says source_ref uuid; entity read-only) — cost: none.
Task 2: minor (deferred → carry into SC-03): LockCityTextAsync untested (add no-transaction-throws + inside-transaction tests); SampleAddressMaxLength/ProvinceCodeMaxLength duplicated in GeographyDbContext (make Domain constants public and reference them).
Task 2: minor (deferred): no test for DivipolaName-beats-synonym nor Name-beats-DivipolaName across two cities.
Task 2: ⚠️ resolved: ShopifyGeographyLookup switching to MatchByNameAsync is SC-06's job (plan) — confirm there.
Task 2: complete (commits 846599fb..d1c42ead, review clean)
Task 3: dispatched, BASE d1c42ead
Task 3: Ruling: blank city text → new outcome `Ignored` (all ids null) instead of the brief's ArgumentException — a throw would make the Shopify import retry forever — carry to SC-06 (importer treats Ignored as nothing to do) and SC-13 (maps to skipped) — cost if wrong: a blank text silently never reaches the queue (it has nothing to resolve anyway).
Task 3: minor (deferred): race read-back branch (DO NOTHING on occurrence) untested; "another text" test reuses same Seen (should bump and assert unchanged); waiter count reads all advisory waiters in DB (flaky risk in full suite — watch in SC-14); throw-away aggregate id vs repository id; Ignored not documented as id-less.
Task 3: ⚠️ carry to SC-06/SC-13: only report texts that failed all three §4.1 paths; handle Ignored.
Task 3: complete (commits d1c42ead..4c420987, review clean)
Task 4: dispatched, BASE 4c420987
Cross-repo carry → SC-10/SC-12: list endpoints echo the REQUESTED page in the page field (no server-side clamp); FE isUnsettledEmpty depends on it.
Task 4: minor (deferred → carry into SC-08): RefusalKind repeats 5 domain codes as literals, only 2 tested through the service (lift into ExternalOrderErrors + one service test per Kind incl. StorePickup); order.order.source_invalid propagates as exception → SC-08 must map it to Skipped (not retry to Failed); IX_order_parties_order_role 23505 not translated to concurrency.conflict in QuotationsUnitOfWork.
Task 4: minor (deferred): "untouched" tests don't assert Version/UpdatedAt unchanged; RED evidence only unit project; IExternalOrderWriter.cs grew 118 lines.
Task 4: complete (commits 4c420987..8d5f8ac0, review clean)
Task 5: dispatched, BASE 8d5f8ac0
Task 5: Ruling: brief test 'without email' replaced by 'without phone' — CustomerWriteRules require phone, email optional; code wins (repo rule) — cost: none.
Task 5: Ruling: P12 covers only city CORRECTION audits (CityCorrected); completing an incomplete customer keeps Customers' `completed` audit via PublishByIntegration (same as every completion path) — so a completion leaves two entries: Customers `completed` + Shopify's correction entry (carry to SC-07/SC-08) — cost if wrong: one extra audit row.
Task 5: minor (deferred → final triage): a taken document burns one CUC per completion job (pre-check FindExistingIdentificationsAsync before PrepareAsync); AlreadyApplied false positive when a human set the same target city; taken-document test doesn't assert burn/audit; Customers throwers still use literals (link to CustomersErrorCodes); save-time FK translations escape as exceptions (worker retries); CorrectCity no guard for same city/Guid.Empty; RED evidence only unit project.
Task 5: ⚠️ carry to SC-06/07: store ProvisionedCustomer.Version as the customer fingerprint (S7); Shopify doubles return Version = 0.
Task 5: complete (commits 8d5f8ac0..5db3d621, review clean)
Task 6: dispatched, BASE 5db3d621
Cross-repo carry → SC-07: the city-correction inbox poll must be ≤ ~15 s (FE polls 60 s for the first progress of a new synonym; develop e7ea2083 moved outbox publisher/delivery to 15 s). Also SC-10: GET /unresolved/{id} returns 200 with status for Resolved/Discarded rows (FE now relies on status, not 404).
Task 6: Ruling: sample_address must NOT include the buyer's name or phone — it is shown in a cross-tenant operator console and the operator only needs the address text (spec example + mockup are address only); shape = address1, address2, city, province, countryCode joined with ", " — data minimization — cost if wrong: operator sees a bit less context (the tenant still has the full order). Carry the same shape to SC-13 backfill.
Task 6: Ruling: accept the extra guard (import correction row only when the saved shipping party's CityId equals the synonym's city) — protects a human edit on AlreadyExisted — cost: none.
Task 6: minor (deferred): retry after AttachCustomer saved yields occurrence CustomerId=null / CustomerCityOnly=false; store-pickup order with city-blocked customer not reported (transient-failure edge).
Task 6: sample-address ruling applied in 675439ca (ShopifyUnresolvedCitySample.Of, reuse in SC-13).
Task 6: minor (deferred → carry into SC-07): no test that a throwing reporter makes the import throw (FakeUnresolvedCityReporter.OnReport unused) — add it.
Task 6: minor (deferred): ForCustomer(Import) row lost if the AttachCustomer save loses a concurrency race (same family as concern 2); RecordShippingCityAsync 9 params / two jobs; retry test uses a new mirror row per attempt.
Task 6: complete (commits 5db3d621..675439ca, review clean)
Task 7: dispatched, BASE 675439ca
Task 7: Ruling: inbox poll 10 s instead of the brief's 3 s — aligned with develop e7ea2083 polling cadence and under the ≤15 s cross-repo carry; outbox publish (≤15 s) + inbox 10 s + SC-08 worker poll must stay well under the FE 60 s first-progress window — carry to SC-08: worker poll ≤ 10 s — cost if wrong: FE stops polling before the first progress shows (operator refreshes by hand).
Task 7: Ruling: accept in-event dedupe by (target, id) — prevents a poison message on the unique index — cost: none.
Task 7: carry to SC-08: importer-seeded jobs have no message id (not covered by the unique index); a re-imported order can seed a second Resolve job → SC-08 must skip it as shipping_present / AlreadyApplied.
Task 7: Ruling (plan-mandated Important): ShopifyClaimedOutboxConsumer/ShopifyInbox are verbatim copies of Pipeline's — accepted: modules cannot reference another module's Infrastructure and the repo already copies this per module; moving it to BuildingBlocks is an owner refactor — cost if wrong: a bug fix to the claim logic has to be applied in two places.
Task 7: minor (deferred): Guid.Empty ids in payload retry forever (treat as malformed); all-or-nothing parsing of occurrences; AFailingReportMakesTheImportThrow has vacuous asserts; IX_city_correction_jobs_message_target untested; ApplyResolvedAsync ignores the token.
Task 7: complete (commits 675439ca..8adb5156, review clean)
Task 8: dispatched, BASE 8adb5156
Task 8: Ruling: duplicates skip with existing reasons (shipping_present / customer_not_eligible) instead of a new already_applied; NotExternal → order_not_found — fewer enum values — cost if wrong: a slightly misleading skip reason in counts/logs.
Task 8: Ruling: accept CorrectExternalShippingCity returning false when the party already equals the expected one at the target city (idempotent retry after crash) — cost: none.
Task 8: Ruling: fix BOTH halves of the retry-after-cross-module-commit hole now (orders: city-agnostic AlreadyApplied in AttachExternalShipping/CorrectExternalShippingCity + fingerprint the persisted city; customers: outcome returns persisted CityId, AlreadyApplied by version+1 and non-city data, fingerprint the written city) and let step 5 heal — a silently wrong city is the spec's anti-goal — cost: a Quotations/Customers domain change in SC-08.
Task 8: Ruling: promote reviewer Minor 1 (lost self-seeded Recorrect) to the fix round with the durable fix: single save under pg_advisory_xact_lock(synonym) shared with ApplyCorrectedAsync; also fix the misleading log — anti-goal again — cost: one more lock on a rare path.
Task 8: promoted: last_error uses GetBaseException(); test that a failing job doesn't block a healthy sibling; MarkApplied/MarkSkipped guarded by status (lease overlap double audit).
Task 8: minor (deferred): no version-bump asserts in refusal tests; retry-then-leaves-Pending inherent non-atomicity (document); audit resource_type "order" vs Quotations "quotation" (document in README in SC-14); file growth; fake inconsistency.
Task 8: fix round 1/5 (findings 2,3,4,5 + order half of 1 addressed; open: N1 regression — customer city-agnostic AlreadyApplied claims a human city edit / human completion and step 5 then overwrites it; N2 claim blocks on a locked running job; commits 40acab3d..7d756541)
Task 8: Ruling (N1): exact fix — persist on the job row, self-committing BEFORE each writer call, the city that attempt requested; Customers accepts the city-agnostic AlreadyApplied only when version == expected+1 AND the customer's current city is one THIS job requested in an earlier attempt; otherwise NotEligible(edited)/(complete). Add the needed column(s) by amending SC-07's unmerged AddCityCorrectionJobs migration (regenerate it; never deployed) — cost if wrong: one more column.
Task 8: Ruling (N2): claim with FOR UPDATE SKIP LOCKED.
Task 8: Ruling (promoted from out-of-scope): the importer's Import-origin fingerprint must be written under the same LockCitySynonymAsync in a Shopify transaction, so a `corrected` event listing concurrently can't leave it at the old city — anti-goal — cost: a lock on the import path for synonym matches only.
Task 8: minor (deferred): N3 WasCompletedWith misses a real retry if store default classification or payload changed (document); N4 LogGaveUp wording, long comment line, no rollback-after-writer integration test.
Task 8: fix round 2 committed 3d0c5c47 (requested_city_ids, migration regenerated 20261026211954, SKIP LOCKED, importer lock).
Task 8: fix round 2/5 (all open addressed; regression checks a-d hold; commits 7d756541..3d0c5c47)
Task 8: minor (deferred → final triage / document in SC-14 README): lease overlap can lose a customer fingerprint+audit in a narrow interleaving (mitigation: RecordRequestedCityAsync before the synonym read, or re-read requested_city_ids and retry once on NotEligible); residual (c) failed-attempt city later set by a person; concurrent completions can burn one CUC; two Recorrect jobs on the same fingerprint can each audit once; flow test asserts requested_city_ids only indirectly.
Task 8: complete (commits 8adb5156..3d0c5c47, 2 fix rounds, review clean)
Task 9: dispatched, BASE 3d0c5c47
Owner decision (2026-10-26): speed up — batch SC-10+SC-11 and SC-12+SC-13 (one dispatch + one review per batch), sonnet reviewers for these endpoint batches, opus only for the final whole-branch review.
Task 9: implementer DONE_WITH_CONCERNS cb3101f0. Note: shipping parties never keep identification_number (OrderParty.Apply), so concern 4 is moot.
Ruling (speed): review SC-09 in parallel with the SC-10+11 batch implementer; any SC-09 finding is carried into the batch instead of a separate fix round — cost if wrong: a finding lands one dispatch later.
Batch SC-10+11: dispatched, BASE cb3101f0
Task 9: minor (deferred → SC-12+13 batch / SC-14): no test that the two policies exist (endpoint tests will hit them; a 500 would show); no cross-tenant test of ReadCorrectionsAsync's tenant join; ReadCorrectionsAsync unbounded per synonym.
Task 9: complete (commits 3d0c5c47..cb3101f0, review clean)
Owner instruction (2026-10-26): stop running tests; owner runs them later. From SC-10+11 on: implementers write tests but only build and format; reviewers don't run tests. Test evidence pending for owner.
Batch SC-10+11: implementer DONE_WITH_CONCERNS f2d7343c (SC-10), ab95de0b (SC-11). SC-11 tests written NOT run (owner order); SC-10 integration re-run unverified. Not yet reviewed. Waiting on owner's choice: merge now / finish all.


## Frontend

# SDD ledger — plan: docs/superpowers/plans/2026-10-26-sinonimos-de-ciudades-frontend.md

Worktree: C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\sinonimos-de-ciudades
Branch: feature/sinonimos-de-ciudades from develop 607f0a8f; plan commit a7d2cf70 (merge base for final review: 607f0a8f)
Spec: qep-backend worktree docs/superpowers/specs/2026-10-26-sinonimos-de-ciudades-design.md; binding HTTP contract: backend plan § «Contrato HTTP para el frontend».
Owner authorization (2026-10-26): implement, commit, merge to develop (local, no push), delete worktree.

Task 0: complete (worktree created, bun install ok, CodeGraph init ok — done by controller)
Ruling: no separate pre-flight scan for the frontend plan (734 lines, written by a capable model against the fixed backend contract) — the per-task reviews are the net — cost if wrong: a cross-task naming mismatch surfaces in a later task review.
FE Task 1: dispatched, BASE a7d2cf70
Task 1: Ruling: accept describeBackfillFailure treating ApiError(524) as "no supimos si terminó" — Cloudflare HTML 524 arrives as ApiError, not NetworkError; Review Focus 5 needs it — cost if wrong: a real 524 error message reads as "safe to repeat" (it is).
Task 1: minor (deferred → carry into Task 9): no test for describeBackfillFailure(ApiError(524)).
Task 1: minor (deferred): no direct API tests for fetchCitySummary/fetchCorrectImpact/discard/restore/retry/backfill (covered later by containers).
Task 1: minor (deferred): only 524 mapped as cut connection; 502/504 not.
Task 1: complete (commits a7d2cf70..bc811c11, review clean)
FE Task 2: dispatched, BASE bc811c11
Task 2: minor (deferred): shouldPollSynonyms lacks tests at exactly 60 s and for failedCorrections-only progress.
Task 2: minor (deferred): PENDING_ORDERS / CUSTOMERS are functions named like constants.
Task 2: complete (commits bc811c11..ceac2168, review clean)
FE Task 3: dispatched, BASE ceac2168
Task 3: minor (deferred): no test of useCorrectImpact nor of the no-city guards of both impact hooks; 20 ms real sleep in one test; filters page restore and search-reset untested.
Task 3: complete (commits ceac2168..d2703fc8, review clean)
Ruling (carry into Task 4): the F7 invalidation of ['operator', tenant, 'cities'] must NOT re-POST a still-mounted impact query — invalidate with a predicate that skips the impact keys (they are re-read only when their dialog opens) — reviewer of Task 3 showed a mounted impact dialog would re-POST on every write — cost if wrong: a stale impact stays on screen until the dialog reopens (it always closes on success anyway).
FE Task 4: dispatched, BASE d2703fc8
Task 4: minor (deferred): onSuccess re-calls requireActingTenant (unreachable throw); unused `_input: void`; 50 ms sleep; only resolve checked for invalidation; backfill 524 path still untested (Task 9).
Task 4: complete (commits d2703fc8..f26aefc1, review clean)
FE Task 5: dispatched, BASE f26aefc1
Task 5: Ruling: double-click guard relies on parent isSubmitting (mutation pending), like the other dialogs; generic "No pudimos calcular el impacto" alert accepted — cost if wrong: two POSTs on a pathological double click (backend 409/422 covers it).
Task 5: minor (deferred → carry into Task 8): Review Focus 3 test doesn't isolate impactFailure (use status ready + impact + impactFailure); Review Focus 4 needs a container test proving one `resolve` POST on double click.
Task 5: minor (deferred): import order in city-impact-groups.tsx (Task 10 lint).
Task 5: complete (commits f26aefc1..e56656b6, review clean)
FE Task 6: dispatched, BASE e56656b6
Task 6: minor (deferred → carry into Task 7/8): CityViewTabs `to` typed as string for /synonyms and /discarded — tighten when the routes land.
Task 6: minor (deferred → carry into Task 9): shell canManage button untested; onBackfill no-op placeholder.
Task 6: minor (deferred): summary failure shows no message in the shell; canReadCities hard-coded true without comment.
Task 6: complete (commits e56656b6..b1499665, review clean)
FE Task 7: dispatched, BASE b1499665
Task 7: minor (deferred → carry into Task 8): a non-reload failure (422) stays on screen after choosing another city in the correct dialog — call correct.reset() in onChooseCity; Task 8 resolve flow must do the same from the start.
Task 7: minor (deferred): banner copy switch untested; retry of another row silently blocked while one retry is pending; import order in synonyms.tsx (Task 10 lint); untested: non-reload delete/correct failure, delete 404 → notice, retry failure, F4 polling via route, department filter.
Task 7: complete (commits b1499665..2b778399, review clean)
FE Task 8: dispatched, BASE 2b778399
Note: develop advanced 607f0a8f→3db98d34 (owner, e2e smoke, 12:52) — merge at the end.
Task 8: minor (deferred): chooseCity reset not isolated by its test (accepted: modal blocks it); dead wiring onRestore/onSelect; emptyText uses undebounced search; untested discard/restore failure, aria-pressed, restored row disappearing.
Task 8: fix round 1 dispatched — Important: empty last page shows false "nothing pending" (clamp page; also synonyms after delete); promoted: merge duplicate operator-cities imports in unresolved-city-panel.tsx; detail 404 must re-read the list.
Task 8: fix round 1/5 (2 addressed, 1 open — transient false-empty flash while clamping to page 1 via keepPreviousData placeholder; commits 040cc4fb..b062cd5c)
Task 8: fix round 2/5 (1 addressed, 0 open; commits b062cd5c..06c6b08f)
Task 8: Ruling: isUnsettledEmpty relies on the backend echoing the REQUESTED page in `page` (never a server-side clamp) — carried to backend SC-10/SC-12 as a contract rule — cost if wrong: indefinite "Cargando…" on an empty mismatched page.
Task 8: complete (commits 2b778399..06c6b08f, review clean)
FE Task 9: dispatched, BASE 06c6b08f
Task 9: minor (deferred): import order cities.tsx (Task 10 lint); cut-connection test doesn't assert console re-read; no route-level 524 test; redundant template literal in dialog:67.
Task 9: complete (commits 06c6b08f..53fda9a0, review clean)
FE Task 10: dispatched, BASE 53fda9a0
Task 10: Ruling: plan-mandated gotcha text is inaccurate (polling) and descriptive — rewrite it accurate and prescriptive — repo CLAUDE.md is prescriptive by rule — cost: none. Fix round 1 dispatched (gotcha + named failure evidence).
Task 10: fix round 1/5 (2 partially addressed — gotcha says both dialogs freeze the city (only resolve does); report evidence formatting; commits c1eca35b..2b397e47). Round 2 dispatched.
Task 10: fix round 2/5 — Ruling: scoped re-review of a 1-line CLAUDE.md change done by the controller reading the diff (it states each dialog's rule exactly as the finding asked) instead of another reviewer seat — cost if wrong: a doc wording slip, caught by the final review.
Task 10: complete (commits 53fda9a0..1bbb3278, review clean). Pre-existing on develop 3db98d34: 3 assertion failures in catalog/components/product-form.test.tsx (not ours).
Final review: dispatched over 607f0a8f..1bbb3278
Final review: Ready after fixes — 2 Important (stale resolved/discarded row drawn as pending; stale-impact refusal dead-ends) + 6 Minor bundled into ONE fix wave (final-fix-findings.md). Not fixed: client/server clock in 60 s window (can stay); contract-text dependency carried to backend.
Final fix wave: f6db50f4 — re-review: 8/8 addressed, no new breakage. FINAL REVIEW CLEAN.
Ruling: hold the FE merge to develop until the backend branch is done and merged — CLAUDE.md "desplegar back primero"; a develop FE calling endpoints that don't exist yet would break the Ciudades tab — cost if wrong: FE waits a few hours in its branch.
