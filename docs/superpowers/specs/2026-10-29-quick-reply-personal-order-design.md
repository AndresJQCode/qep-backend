# Quick replies: personal order — design

Date: 2026-10-29. Cross-repo: endpoint in `qep-backend` (module Messaging), drag-to-reorder in
`qep-frontend` (`features/messaging`). Per `SDD-ADR-08` each repo tracks its own slice.

## Why

Every advisor uses a handful of quick replies far more than the rest, and the list is ordered by
shortcut. Each advisor decides the order in which the tenant's quick replies appear **to them**. The
order is personal: it never changes what other members see, and the catalog (create, edit, delete with
`messaging.quick_reply.manage`) is unchanged.

## Contract

### `GET /api/v1/tenants/{tenantId}/messaging/quick-replies` (existing)

Same route, permission (`messaging.conversation.read`) and response shape (`QuickReplyListDto
{ items, variables }`, no new fields). Only the order of `items` changes, and only for the caller:

1. First the quick replies present in the caller's saved order, in that order.
2. Then every other quick reply by `shortcut`, ordinal (the previous order, D-Q7).

A member who never saved an order, and a caller without an active membership, see exactly the previous
order. Saved ids that no longer exist (deleted) are skipped.

### `PUT /api/v1/tenants/{tenantId}/messaging/quick-replies/order` (new)

- Body: `{ "quickReplyIds": ["<guid>", ...] }`, top first.
- Permission: `messaging.conversation.read`, the same as the `GET`, **not** `manage`: any member who can
  read quick replies orders them for themselves. Tenant and module are revalidated in the handler (403
  with code, as the rest of the module).
- Replaces the caller's previous order entirely. No `If-Match` and no version: it is personal, last write
  wins.
- Ids that are not existing quick replies of the tenant are ignored (a concurrent delete must not fail
  the save); duplicates keep their first occurrence. Only the normalized list is stored.
- Empty list is valid: it resets to the shortcut order.
- `200` with the same `QuickReplyListDto` as the `GET`, already ordered for the caller.
- `422 validation.failed` with `errors.quickReplyIds` when the body is missing, `quickReplyIds` is
  `null`, or it has more than 500 ids (`QuickReplyOrder.MaxItems`).
- `403 authorization.denied` when the caller has no active membership in the tenant (there is no one to
  save the order for), same as sending a message.

## Storage

`messaging.quick_reply_orders`, one row per member (migration `AddQuickReplyOrders`):

| column            | type          | notes                                                    |
| ----------------- | ------------- | -------------------------------------------------------- |
| `tenant_id`       | `uuid`        | PK part                                                  |
| `member_id`       | `uuid`        | PK part; soft reference to the Tenancy membership        |
| `quick_reply_ids` | `uuid[]`      | ordered; `CK_quick_reply_orders_cap`: at most 500        |
| `updated_at`      | `timestamptz` |                                                          |

Why an array per member and not a row per (member, quick reply, position): the order is always read and
replaced whole, a tenant has tens of quick replies, and a single row makes "replace entirely" one upsert
with no gaps or position renumbering. There is no FK to `quick_replies`: a delete leaves the id in the
array and the read skips it, so deleting needs no cleanup. `member_id` has no FK either: memberships live
in another schema (same as `assigned_member_id`).

The write is an `INSERT … ON CONFLICT (tenant_id, member_id) DO UPDATE` and not a tracked add/update:
two first saves of the same member at once (two tabs) would otherwise collide on the PK, and the
contract is last write wins, not a 412.

The caller's member is resolved with `CallerMembership` (the same as `assigned=me`, `isMe` and the
`asesor` variable).

## Audit

Quick reply writes are audited, so this one is too, in the handler and in the same transaction as the
upsert: action `messaging.quick_reply.order_saved`, resource type `quick_reply_order`, resource id the
membership id whose order changed, actor the caller, `changed_fields` `[]`.
