# WhatsApp template with a fixed image header — backend

Date: 2026-11-17. Pair spec: `qep-frontend/docs/superpowers/specs/2026-11-17-template-image-header-design.md`.

## Intent

A tenant wants to create, from QEP's template builder, a WhatsApp template whose header is an image
(a promo banner, for example) and send it from the console: chat, «Nueva conversación» and campaigns.
The image is **fixed per template**: it is chosen once, when the template is created, and every send
uses it. Nobody picks an image at send time.

Today:

- Free creation (`POST …/template-management/templates`, `CreateTemplateCommand`) only takes a text
  header. `TemplateComponentsBuilder` already knows a media header, but only `DOCUMENT` with a
  `header_handle` for the system templates (order, «Respuesta formal»).
- `TemplateCatalog` marks every media-header template `sendable = false` with reason `MediaHeader`.
  Only the external API sends them (`SendableWithHeaderMedia`, spec 2026-11-13), because the caller
  brings its own `link`.
- `SendTemplateRequest` (chat) and `CampaignRequest` carry no header.

## Goals

1. Create a free template with an `IMAGE` header from QEP.
2. Store the image so QEP can send it on every send.
3. Chat, «Nueva conversación» and campaigns send such a template, with the stored image, and the
   tenant never picks it.
4. The external API keeps working as today.

## Non-goals

- Video or document headers in free creation (the builder can add them later on the same path).
- Choosing the image per send.
- Image templates created in Meta's console and imported by «Sincronizar»: QEP has no image for
  them, so they stay `sendable = false` (`MediaHeader`) in the console. The external API still
  sends them with its own link.
- Editing the image of an existing template (templates are not editable, D-T6: create another one).

## Anti-goals

- A template that shows as sendable in the console but fails at Meta because the image is missing.
- Making the external API depend on the stored image: it keeps sending the link its caller sends.

## Approaches considered

- **A. Public image, sent by link (chosen by the owner).** The image is published to the public
  bucket at creation; its URL is stored on the template row; every send adds
  `header: { type: image, link }` with `TemplateHeader.ByLink`, the path the external API already
  uses. No extra Meta calls per send. Trade-off: anyone with the URL can see the image, which does
  not matter for an image that goes out to every recipient anyway.
- **B. Private image, uploaded to Meta as media.** Like the quotation PDF (`DocumentById`). Media ids
  expire after 30 days, so it needs a cached id with renewal, and a big campaign depends on that
  cache. Rejected: more parts and more Meta calls with no benefit for a non-confidential image.

## Design

### Upload

Dedicated endpoint (owner, 2026-11-17):
`POST …/template-management/header-image/upload-session`, authorized with
`messaging.template.manage` only, so a custom role that can manage templates but lacks
`storage.file.upload` can still create an image template. It takes the file name, content type and
size, validates them (JPEG/PNG, ≤ 5 MB) before issuing anything, creates the file through the
Storage port (owner type `MessageTemplate`) and returns `{ fileId, uploadUrl }`. The browser `PUT`s
the bytes to `uploadUrl` and then sends the create request with `fileId` as `headerImageFileId`.
The create handler confirms the upload (the equivalent of `/files/{id}/complete`) through the same
port before using the file. Same pattern as Messaging's `…/messages/media/upload-session`.

### Create contract

`CreateTemplateRequest` gains `headerImageFileId: Guid?`.

- `header` (text) and `headerImageFileId` are mutually exclusive: both present is a 422 with a key
  in `errors` (field `header`).
- The file must belong to the tenant, be uploaded (complete) and be an image Meta accepts for an
  image header: `image/jpeg` or `image/png`, at most 5 MB. Anything else is a 422 with its own code,
  mapped to the field.
- On create, the handler:
  1. Publishes the file to the public bucket with `FilePublication` (like the tenant logo adapter:
     the authorization is `messaging.template.manage`, not `storage.file.publish`).
  2. Uploads the bytes to Meta's Resumable Upload (`/{AppId}/uploads`) for the `header_handle`, the
     same client call the order template uses.
  3. Builds the payload with a `HEADER` component `format: IMAGE` and `example.header_handle`.
     `TemplateComponentsBuilder` generalizes its document-only parameter to a media header with a
     format.
  4. Stores the public URL (and the file id) on the `messaging.message_templates` row.
- Upload failures follow the order template's rules (`upload_failed`, `no_app_id`), mapped in
  `ApiExceptionHandler` as today.

### Storage on the template row

New nullable columns on `messaging.message_templates`: the header image's file id and public URL.
Migration in Messaging only.

**«Sincronizar» and webhooks must not wipe them.** The mirror rewrites status, components and
reasons from Meta; the stored image is QEP's own data and survives a sync.

### Catalog and sending

- `TemplateCatalog` (the console's catalog, `GET …/connections/{id}/templates`): an `IMAGE` header
  template **with a stored image URL** is `sendable = true` and carries the URL
  (`headerImageUrl`) so the composer can preview it. Without a stored image it stays
  `sendable = false` / `MediaHeader`, as today. Video and document headers are unchanged.
- `TemplateOutbound.SendAsync` (chat and «Nueva conversación») and `CampaignDispatch` call
  `BuildSend` without a header today (`TemplateOutbound.cs:32,54`, `CampaignDispatch.cs:276`); both
  must pass `TemplateHeader.ByLink("image", url)` when the template has a stored image. The request
  contracts do not change: the frontend never sends the image.
- External API: unchanged. It sends the link its caller passes; it does not fall back to the stored
  image.

### Responses

`ManagedTemplateDto` and the catalog DTO gain `headerImageUrl: string | null` (`header` stays the
text header and is `null` for an image header). Older frontends ignore the field.

### No orphaned images (owner, 2026-11-17)

No public header image may outlive its template row.

- **Row removed** — by `DELETE …/templates/{id}` or by `TemplateMirrorSync` when Meta no longer
  lists the template: the same transaction that removes the row writes an outbox message; its
  handler unpublishes and soft-deletes the image, retried by the outbox until it succeeds. The
  delete request itself never fails because of the image.
- **Create fails after the image was published** (Meta rejects the `POST`, upload to Meta fails):
  the handler unpublishes and deletes the image before returning the error. If that immediate
  release itself fails (storage down), the handler writes the same outbox release message instead,
  so the image is still released by retry.
- The file is **system-owned** in Storage: the Storage console cannot delete or unpublish a live
  header image.
- A **rejected** template keeps its row, so its image is not an orphan; it is released when the row
  is removed.
- An abandoned form leaves nothing: the frontend uploads only on submit.

### Closing the residual gaps (owner, 2026-11-17, after the first merge)

- **Public copies without a stored key.** Releasing a `MessageTemplate` file (immediately or from the outbox) removes
  its public objects using the stored `PublicStorageKey` or, when none was saved, the deterministic
  `StorageKey.PublicFor(tenantId, id, name)` plus its variants. A failed Storage save after the copy no longer leaves
  public objects behind.
- **Daily sweep as the final net.** A Storage worker lists live `MessageTemplate` files older than 24 h, asks Messaging
  (through a Bootstrapper adapter) which file ids a template row still references, and releases the rest (public
  copies removed, soft-deleted, system audit). It covers every path that only logs today: the in-use check failing,
  the outbox write failing, and an upload whose create never arrived. The 24 h grace keeps it away from creates in
  flight.
- A commit whose acknowledgement is lost is already safe: the release re-checks the database first and skips a file
  a saved row uses.

## Constraints

- TDD, RED before GREEN with literal evidence (workspace rules).
- Messaging Application does not reference EF Core or Npgsql (`ArchitectureTests`).
- Integration tests filtered by name while iterating; the Messaging project once before merging.
- Deploy backend before frontend: the new fields are additive and an older frontend keeps working.

## Calls made by the designer (check them)

- **New `FileOwnerType` `MessageTemplate`**, mapped to the Messaging module in `FileOwnerModules`.
  Reusing `Store` or `Tenant` would tie templates to another module's gate or ownership check.
- **Messaging ↔ Storage through a port and a Bootstrapper adapter**, like `TenantLogoStorage`:
  Messaging Application cannot reference Storage. The port validates the file (tenant, owner type,
  complete, JPEG/PNG, ≤ 5 MB), reads its bytes for Meta's upload, publishes it and returns the
  public URL, and releases it. The migration stays in Messaging; Storage only gains the owner type.

## Left to the builder

- Exact error codes (follow the `messaging.template.*` naming) and their `ApiExceptionHandler`
  mapping.
- The upload-session request/response shape beyond `fileId` and `uploadUrl` (follow Messaging's
  media upload session).
- Column names and the migration name.
