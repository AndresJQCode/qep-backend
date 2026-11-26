# Messaging: filter conversations by connection

Date: 2026-11-17. Scope: `qep-backend`. Frontend counterpart:
`qep-frontend/docs/superpowers/specs/2026-11-17-messaging-connection-chip-and-filter-design.md`.

## Intent

A tenant with several WhatsApp numbers (or WhatsApp and Instagram) needs to narrow the inbox to
the conversations that arrived through one connection.

## Current state

`GET /api/v1/tenants/{tenantId}/messaging/conversations` (`MessagingEndpoints.cs:375`,
`ListConversations.cs`) filters by `status, search, assigned, channel, tagId, unread, sort,
automation`, paginated by cursor. A conversation belongs to exactly one connection
(`ConnectionId`), and the response already carries `connectionId` and `connectionName`.

## Decision

Add an optional `connectionId` (Guid) query parameter to the listing.

- Present: only conversations with that `ConnectionId`. Combines with every other filter and
  with the cursor (the cursor is a keyset `(LastActivityAt, Id)`; filters are re-applied on every
  page, as with `tagId`).
- Absent: unchanged behaviour.
- A `connectionId` that is not a Guid: rejected by the minimal-API binding, exactly as `tagId`
  is today; no validator rule.
- A `connectionId` of another tenant or of a deleted connection: an empty page, not an error
  (the frontend keeps the choice in the URL and the inbox simply shows nothing).

No new permission: `messaging.conversation.read`, the same as the listing.

## Out of scope

- Colour per connection on the connection entity (second phase, see the frontend spec).
- Any change to `GET …/messaging/connections`.

## Left to the builder

- Index check: `(TenantId, ConnectionId, Status, LastMessageAt)` or whatever the current listing
  index is; add `ConnectionId` only if the planner needs it.
- Tests: filter alone, filter combined with `status=Resolved`, counts unchanged, foreign
  connection empty page.
