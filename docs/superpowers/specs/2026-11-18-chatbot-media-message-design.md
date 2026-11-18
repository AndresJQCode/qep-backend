# Chatbot «Mensaje» block with media — backend

Date: 2026-11-18. Pair spec (editor, canvas, simulator):
`qep-frontend/docs/superpowers/specs/2026-11-18-chatbot-media-message-design.md`.
Origin: competitive review of the chatbot builder, wave P0. Owner's standing rule: when there is a
choice, take the most professional option.

## Intent

Today the «Mensaje» block sends text, optionally with one URL button (`MessageNode(Text, UrlButton?)`,
`FlowDefinition.cs:30`). Every competitor (ManyChat, Botpress, Typebot, Respond.io) lets a flow send an
image, a video, an audio or a document. Without it a tenant cannot send a catalog, a price list PDF, a
product video or a voice note from a bot, which is the most common thing a sales bot does. The original
builder spec left it out on purpose (`2026-10-21-constructor-de-chatbots-design.md` §13, «Imagen o
archivo»); this spec brings it in.

## Goals

1. A «Mensaje» block can carry **one** media file — image, video, audio or document — plus its text as
   the caption, and keeps its optional URL button.
2. The file is uploaded once from the editor and reused on every send, forever, on every connection of
   the tenant, without re-uploading and without expiring.
3. The simulator shows what the customer would receive, media included.
4. The inbox shows the bot's media message like an advisor's media message.
5. No public file outlives every chatbot that uses it.

## Non-goals

- Several files in one block (send two blocks).
- Choosing the file per run from a variable (a URL from an HTTP response). Later, as its own spec.
- Stickers, locations, contacts.
- Carrying the file inside the export JSON (see Export / import).
- Video and audio formats beyond today's `OutboundMediaPolicy`.

## Anti-goals

- A published bot that silently stops sending its image (an expired Meta media id, a deleted file).
- A block whose caption the customer never sees without the editor saying so (audio has no caption).
- A tenant reaching another tenant's file through a pasted or imported flow.

## Approaches considered

| Approach | Trade-off | Verdict |
|---|---|---|
| **A. Stored file, sent by public link** | Upload once to Storage, publish to the public bucket, every send passes `link`. Same mechanism as the template header image (spec 2026-11-17), already in production. Works for every phone number and for Instagram. The URL is public but unguessable — acceptable for content a bot sends to anyone who writes. | **Chosen** |
| B. Upload once to Meta, store the media id | Media ids expire after 30 days and belong to one phone number: a published bot would break silently, and a second connection would need its own upload. | Rejected |
| C. Private file, uploaded to Meta on every send | Correct but one extra Meta call and upload per message, slower bot replies, more failure points. | Rejected |

## Design

### Flow contract (schema stays 1, additive)

`message` node gains an optional field:

```json
"media": { "kind": "image" | "video" | "audio" | "document", "fileId": "<guid>" } | null
```

- Absent or `null` = today's text message. Older drafts and older frontends keep working; a frontend
  that does not know the field drops nothing because the backend reads it as `null`.
- The flow stores only the **file id**. The public URL, file name, size and MIME are resolved from
  Storage when needed, so nothing in the flow can point to an arbitrary URL.
- `FlowJson` reads and writes it; `MessageNode` gains `Media: MessageMedia?`.

### Upload

Dedicated endpoints under the chatbot, authorized with `automations.chatbot.manage` only (a custom
role that can build bots but lacks `storage.file.upload` must still be able to add an image; same
reasoning as the template header):

1. `POST …/automations/chatbots/{chatbotId}/media/upload-session` with `{ fileName, contentType,
   sizeBytes }`. Validates against the policy below **before** issuing anything, creates the Storage
   file through a port (new system owner type **`ChatbotMedia`**, `OwnerId` = tenant id, persisted by
   name, no migration in Storage) and returns `{ fileId, uploadUrl, headers }`.
2. Browser `PUT`s the bytes to `uploadUrl`.
3. `POST …/automations/chatbots/{chatbotId}/media/{fileId}/complete`: confirms the upload, sniffs the
   real content type (the declared one is not trusted), compresses an image above 5 MB with the same
   preparer Messaging uses for outbound images, publishes it to the public bucket and returns
   `{ fileId, kind, fileName, contentType, sizeBytes, url }`.

Errors are domain codes mapped in `ApiExceptionHandler`, each with its field: `automations.media.
unsupported_type`, `automations.media.too_large`, `automations.media.not_uploaded`,
`automations.media.not_found`.

### Media policy

Taken from `OutboundMediaPolicy` (WhatsApp column) so the bot and the advisor accept the same files:

| Kind | Formats | Max |
|---|---|---|
| image | jpg, png | 25 MB uploaded, ≤ 5 MB after compression (Meta's link limit) |
| video | mp4, 3gp | 16 MB |
| audio | mp3, m4a, aac, amr, ogg (Opus) | 16 MB |
| document | pdf, doc, docx, xls, xlsx | 100 MB |

`OutboundMediaPolicy` marks video and audio as draft until V-1..V-3. This spec follows whatever the
policy says at build time; the owner's open decisions there apply here too.

### Validation (`FlowValidator`)

- With media, `text` may be empty (it is the caption). `TextEmpty` is not raised.
- Caption limit: 1024 characters (WhatsApp media caption). With a URL button the existing interactive
  body limit applies.
- `media.fileId` must resolve to a live `ChatbotMedia` file of **this tenant**, completed. Otherwise
  **Error** `message.media_missing` («Falta el archivo de este mensaje. Vuelve a subirlo.») — it blocks
  publishing. The validator is pure Domain: the Application layer resolves the file ids of the draft
  through the port first and hands the validator the set of valid ids.
- Audio with text: **Warning** `message.audio_caption_separate` («El audio no lleva texto: se envía
  como un mensaje aparte, después del audio.»).
- Audio with a URL button: **Warning** `message.audio_button_separate` (the button goes in a separate
  interactive message after the audio).
- `{variables}` in the caption are checked like today's text (`FlowValidator.cs:778`).

### Execution

- New effect `Effect.SendMedia(NodeId, Kind, FileId, Caption?, UrlButton?)`. `FlowEngine.Send(MessageNode)`
  emits it when the node has media; otherwise today's behaviour is unchanged.
- `RunEffects` resolves the public URL through the port and calls a new
  `IAutomationMessenger.SendMediaAsync`. `BotSend` gains a media variant; `BotOutbound` keeps its
  conversation claim and idempotency exactly as for text.
- WhatsApp:
  - image / video / document without button → the media message by **link**, with caption (document
    also with `filename`).
  - image / video / document **with** button → one `interactive` `cta_url` message with a media
    `header` (image, video or document) and the caption as body. One message, as the editor draws it.
  - audio → the audio by link; then, if there is text or a button, a second message (text, or the
    `cta_url` interactive).
  - The Meta client gains link variants of the media sends where it only has media-id variants today.
- Instagram: the attachment by URL (as `SendMediaMessage` already does), then the caption as a separate
  text message, and the button as today's Instagram fallback.
- A failed media send follows today's bot failure path (the run's existing error handling for a send
  that Meta rejects). No silent fallback to text.
- The send is recorded in the conversation as an outbound media message so the inbox draws it. It
  **references** the stored public file; it does not copy the bytes per message.

### Editor response

`GET` of the chatbot editor gains `mediaFiles: [{ fileId, kind, fileName, contentType, sizeBytes, url }]`
for every file id referenced by the draft that resolves for the tenant (one batch lookup, not one per
file). A missing id is simply absent; the validator already reports it. Older frontends ignore the field.

### Simulator

The transcript entry gains `media: { kind, url, fileName, contentType } | null`. Audio-with-text and
button cases produce the same sequence of entries the real send produces.

### Lifecycle — no orphaned public files

A `ChatbotMedia` file is **in use** while the draft or any published version of any chatbot of the
tenant references its id.

- Upload never attaches anything: the draft's autosave stores the id.
- Removing or replacing the media in the editor does not delete the file immediately — undo must be
  able to bring it back.
- **Daily sweep** (Storage worker, like the template header sweep): lists live `ChatbotMedia` files
  older than 24 h, asks Automations through a Bootstrapper adapter which of their ids are referenced
  (jsonb search over drafts and published versions), and releases the rest (public copies removed,
  soft-deleted, system audit).
- **Chatbot deleted:** the same transaction writes an outbox message that runs the same in-use check
  for that chatbot's file ids and releases the unreferenced ones. The delete never fails because of a
  file.
- Files are **system-owned**: the Storage console never lists, deletes or unpublishes them.

### Duplicate, templates, export / import, paste

- **Duplicate** and **save as template** (same tenant) keep the file ids: the file is shared, and the
  in-use rule keeps it alive while any chatbot uses it. `TemplateParameters.Apply` must carry `Media`
  through (it rebuilds `MessageNode`).
- **Export:** the JSON keeps `media` with its file id. Nothing else.
- **Import / paste into another tenant:** the id does not resolve for that tenant, so the validator
  raises `message.media_missing` and the editor asks for the file again. A tenant never reads another
  tenant's file: resolution is always scoped by tenant id.
- **Import into the same tenant:** the id still resolves and keeps working.

## Constraints

- TDD, RED before GREEN with literal evidence.
- `Modules.Automations.Application` does not reference EF Core or Npgsql; Storage is reached through a
  port with a Bootstrapper adapter, as Messaging does for template images.
- Integration tests filtered by name while iterating; the touched projects once before merging.
- Deploy backend before frontend: every field is additive.

## Calls made by the designer (check them)

- Approach A (public link) over Meta media ids — robustness over privacy of bot content.
- Upload in the editor is immediate (the editor autosaves); orphans are handled by the 24 h sweep, not
  by uploading on publish.
- Audio's text and button go as separate messages with a warning, instead of being forbidden.
- Media + button becomes one `cta_url` interactive with a media header, so the customer sees one
  message, as drawn.
- Duplicates share files instead of copying them.
- Cross-tenant import does not carry the file; the editor asks for it.

## Builder questions, settled

1. **Instagram is in scope.** `BotOutbound` is text-only for Instagram today («sólo con texto»); this
   work adds an Instagram media send to the **bot** path with the same claim, window, idempotency and
   failure handling as its text send — not by calling the advisor's `SendMediaMessage`. Instagram gets
   the attachment by URL, then the caption as text, then the button through today's Instagram button
   fallback. Plan it as its own task block so its risk is reviewed on its own.
2. **Media header on `cta_url` is wanted** (one message, as drawn). Extend the interactive send (DTO,
   builder, validator header rules, Meta client) with an image / video / document header, and the inbox
   parser so the inbox draws it as **media with the button under it**, one bubble.
3. **Inbox reference: a Messaging migration is allowed.** The outbound bot message stores a reference to
   the Storage file (its id and public URL) instead of `meta_media_id` + copy; the inbox read path
   serves that URL. Deploy order stays backend first.
4. **Video and audio ship now**, following `OutboundMediaPolicy` as it is. V-1..V-3 are verifications
   of Meta's limits, and the advisor path already sends these formats in production with the same table.
   If the policy changes, the bot follows it automatically.
5. **Rollout:** an old frontend tab that autosaves drops `media`; accepted for the deploy window (the
   frontend version check prompts a reload). The in-use query covers the draft **and every published
   version**, so a published bot and its in-flight runs never lose their file.
6. **Ownership is by tenant only** (shared across chatbots of the tenant). Storage publish and the image
   preparer are reached from Automations through ports with Bootstrapper adapters (the preparer adapter
   calls Messaging's), keeping `ArchitectureTests` green.
7. **Undo after the sweep:** a file removed, kept only in the editor's undo history and swept after 24 h
   comes back as `message.media_missing`; the editor asks for it again. Accepted.
8. **No audio duration** in any contract: the canvas chip says «Audio».

## Left to the builder

- Exact names of the port, the adapter and the outbox message.
- The jsonb query for the in-use check and its index, if any.
- How the inbox message references the stored file (column or media record), as long as no bytes are
  copied per send.
