# Open a customer's conversation without sending — backend design

Date: 2026-11-17. Cross-repo work: the frontend spec is
`qep-frontend/docs/superpowers/specs/2026-11-17-escribir-por-whatsapp-abre-el-hilo-design.md`.
**Deploy order: backend first.**

## Intent

«Escribir por WhatsApp» (today only on the detail of a `Store` order) opens the «Nueva
conversación» modal, which forces the advisor to pick and send a template. The owner wants the
button to behave like opening the customer from the Mensajería inbox: land in the thread, assigned,
with the cursor in the composer. The composer already decides what can be sent (free text inside
Meta's 24 h window, templates only outside it).

The blocker is on the backend: a conversation is only created by `POST /conversations`
(`StartConversation.cs`), which always sends a template. A customer who never wrote has no thread
to land in. This spec adds an endpoint that **finds or creates** the customer's conversation
**without sending anything**.

## Goals

- One call that returns the conversation the advisor should land in, creating an empty one when
  none exists.
- Leave it open (reopened if it was resolved) and assigned to the caller when it was unassigned.

## Non-goals

- Sending anything. The first message still goes through the thread (template or text, as today).
- Instagram: a conversation is never started there (spec 2026-10-14 §3).
- Changing `POST /conversations` (start with template) — it stays for «Nueva conversación».

## Anti-goals

- Taking a conversation away from another advisor without them noticing. If it is assigned to
  someone else, it is returned **as is**; the thread already shows «Tomar conversación».
- Leaving garbage rows for unreachable customers: every 404/422 is checked **before** opening.

## Approaches considered

1. **Frontend only: search by phone + `take`.** Rejected: it cannot reach a customer with no
   conversation (the owner's main case), and it chains calls in the browser (BFF rule).
2. **Frontend falls back to the template modal when there is no thread.** Rejected by the owner:
   the button must always land in the thread.
3. **New backend endpoint "open the customer's conversation"** — chosen. It reuses
   `IConversationOpener.OpenAsync`, which already finds or creates by BSUID / phone /
   `customer_id` respecting the unique indexes.

## Contract

`POST /api/v1/tenants/{tenantId}/messaging/conversations/open`

- Permission `messaging.conversation.manage` (same as `POST /conversations`), module `messaging`,
  `X-Qep-Client` like every mutation. No `If-Match` (nothing pre-existing is versioned by the caller).
- Body: `{ "customerId": "<guid>", "connectionId": "<guid> | null" }`.
- Response: `{ "conversation": ConversationSummary, "created": bool }` — `201` when created,
  `200` when found. `ConversationSummary` is the same shape as `GET /conversations/{id}`.

### Rules, in order

1. Validation: `customerId` required and not empty (`422 validation.failed`).
2. Customer: same checks and codes as `StartConversationHandler` —
   `404 messaging.customer.not_found` (missing or inactive), `422 messaging.customer.unreachable`
   (neither phone nor WhatsApp user id).
3. **Without `connectionId`** — the tenant's `Active` WhatsApp connections: exactly one → use it
   (go to 4); none → `422 messaging.connection.unavailable` (existing code); several →
   `422 messaging.conversation.connection_required` (**new code**), **even if the customer already
   has a thread on one of them**: owner decision 2026-11-17, a tenant with several numbers always
   chooses the number, the endpoint never picks "the most recent thread". The 422 carries no list:
   the SPA already reads the connections.
4. **With `connectionId`:** Instagram → `422 messaging.channel_unsupported` (existing);
   not `Active` → `422 messaging.connection.unavailable`; otherwise `opener.OpenAsync` (finds the
   customer's conversation on that connection or creates it).
5. **Status (owner decision 2026-11-17):** a `Resolved` conversation is **reopened**, with the same
   domain transition, event and audit as `POST …/reopen`. Reason: the thread locks the composer of a
   resolved conversation (`conversation-thread-page.tsx`, `lockedReason`), and the button must land
   ready to type.
6. **Assignment (after 5):** if the conversation has no assignee, assign it to the caller, with the
   same event and audit as `take`. Assigned at open, not at first send: the owner asked for
   "asignado". If it is assigned to someone (caller or other), do not touch it.
7. Steps 5 and 6 run in one unit of work; a failure leaves the conversation as it was.

### Empty conversations

A created conversation has no messages (`LastMessageAt = null`). It must appear in the inbox like
any other (sorted by `LastActivityAt`), and every DTO/preview path must tolerate the missing last
message. Inbound ingestion must adopt it later (it already adopts phone-only rows — `ConversationOpener`
header, step 1a of spec 2026-10-10 §8.1).

## Constraints

- Errors mapped in `src/Api/ApiExceptionHandler.cs`; the new code goes in `MessagingErrorCodes`.
- Architecture tests: Application does not reference EF Core.
- TDD, RED before GREEN. Integration tests filtered by test name while iterating
  (workspace `CLAUDE.md`, test budget).

## Accepted consequences

- An advisor who opens and never writes leaves an empty conversation assigned to them in «mías»,
  sorted by `LastActivityAt` = time of opening. Accepted (decision above).

## Left to the builder

- Name and place of the command/handler (follow `StartConversation.cs`).
- How to reuse the reopen/take domain methods without duplicating their events and audit.
- `docs/estado.md` is updated on `develop` at merge time, not on the feature branch.
