# Chatbot «Mensaje» Block with Media — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Workspace override** (workspace `CLAUDE.md`, «Presupuesto de pruebas y ejecución de planes»): the tasks run in **4 blocks with one implementer per block** (each task keeps its own commit), one review per block, and one strong whole-branch review at the end. Block D is gated (see its header).

**Goal:** A chatbot «Mensaje» block carries one stored image, video, audio or document (text = caption, URL button kept), sent by public link on every connection, shown in the simulator and the inbox, and released when no chatbot uses it.

**Architecture:** The file is a Storage system file (`FileOwnerType.ChatbotMedia`, owner = tenant), uploaded through two Automations endpoints and published to the public bucket on `complete`. Automations reaches Storage, Messaging's media policy, sniffer and image compressor only through ports with Bootstrapper adapters. The flow stores `{ kind, fileId }`; the engine emits `Effect.SendMedia`, `RunEffects` resolves the public URL and `IBotOutbound` sends by `link` (one `cta_url` interactive with a media header when there is a button). Orphans are released by a daily Storage sweep and by an outbox message written when a chatbot is deleted; both ask Automations which ids are still referenced.

**Tech Stack:** .NET 10, EF Core + Npgsql (jsonb), FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-18-chatbot-media-message-design.md` (pair: `qep-frontend/docs/superpowers/specs/2026-11-18-chatbot-media-message-design.md`).

## Global Constraints

- Worktree `qep-backend-worktrees/chatbot-media`, branch `feature/chatbot-media-message`. Merge into `develop` only, never `main`. Conventional commits, **no AI attribution**. Run `git branch --show-current` right before every commit.
- Flow schema stays `1`; `message.media` is `{ "kind": "image" | "video" | "audio" | "document", "fileId": "<guid>" } | null`. Absent = `null`. The flow never stores a URL.
- Upload endpoints are authorized by `automations.chatbot.manage` only, never `storage.file.*`.
- Error codes (422, `AutomationsDomainException` with field errors): `automations.media.unsupported_type`, `automations.media.too_large`, `automations.media.not_uploaded`, `automations.media.not_found`.
- Media policy = `OutboundMediaPolicy`, WhatsApp column, read at runtime through a port (the bot follows the policy automatically): image jpg/png 25 MB (≤ 5 MB after compression), video mp4/3gp 16 MB, audio mp3/m4a/aac/amr/ogg (Opus) 16 MB, document pdf/doc/docx/xls/xlsx 100 MB.
- Validator: caption ≤ 1024; `message.media_missing` is an **Error** with the exact text «Falta el archivo de este mensaje. Vuelve a subirlo.»; `message.audio_caption_separate` (**Warning**, «El audio no lleva texto: se envía como un mensaje aparte, después del audio.») and `message.audio_button_separate` (**Warning**).
- Resolution of a file id is always scoped by tenant id; a foreign id behaves like a missing one.
- `Modules.Automations.Application` and `Modules.Storage.*` never reference Messaging, Storage (from Automations), EF Core or Npgsql across modules; adapters live in `src/Bootstrapper` (`ArchitectureTests` stay green).
- Every new field in responses is additive. Deploy order: backend first. Migrations are additive (Messaging: two nullable columns; Automations: snapshot-only outbox projection).
- Copy to people in Spanish (Colombia), tuteando. Identifiers, codes and comments' identifiers in English.
- **Tests:** RED for a compile failure is the `dotnet build` output (no containers). Integration tests are filtered by test name while iterating (`--filter "FullyQualifiedName~<Name>"`). Before any integration run: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; if one runs, wait, never stop it. Never two integration runs at once; subagents run tests in the foreground. At block end: each touched **unit** project in full, then each touched **integration class** once, in sequence, with `-- xUnit.MaxParallelThreads=4`. Touched integration projects run in full only before merging.
- Every command is PowerShell. If `Api.exe` is running, builds fail; stop only an `Api.exe` you started.

## Review Focus

1. **A published bot whose draft lost `media`** (old tab autosave, spec Q5): the file is still referenced by the published version and must survive the sweep and a delete of another chatbot. Pinned in Task 11 by `AFileOnlyInAPublishedVersionIsReferenced`.
2. **A flow pasted or imported from another tenant**: its `fileId` must never resolve, never appear in `mediaFiles` and must raise `message.media_missing`. Pinned in Task 6 by `AForeignFileIsMissingAndNotListed` and in Task 5 by `ResolveIgnoresAnotherTenantsFile`.
3. **Media plus URL button with an empty caption**: WhatsApp's `cta_url` requires a body, so `text.empty` must still block publishing (the spec only lifts it for plain media). Pinned in Task 6 by `MediaWithAButtonStillNeedsText`.
4. **A unit retried after the audio went out but its follow-up failed**: the audio must not be sent twice. Pinned in Task 7 (engine splits into two effects → two `clientId`s) by `AudioWithTextIsTwoSendsInOrder` and in Task 10 by `ARetriedUnitDoesNotResendTheAudio`.
5. **Two chatbots sharing one file (duplicate), one deleted**: the file stays published. Pinned in Task 12 by `DeletingOneOfTwoChatbotsSharingAFileKeepsIt`.

## HTTP contract (the frontend plan depends on it)

- `POST /api/v1/tenants/{tenantId}/automations/chatbots/{chatbotId}/media/upload-session`, body `{ fileName, contentType, sizeBytes }`.
  - `200` → `{ fileId, uploadUrl, headers }`; `headers` are exactly the signed PUT headers (`Content-Type`, `If-None-Match: *`).
  - `422 validation.failed` with `errors.fileName|contentType|sizeBytes`.
  - `422 automations.media.unsupported_type` (`errors.fileName`), `422 automations.media.too_large` (`errors.sizeBytes`).
  - `404 automations.chatbot.not_found`; `403` without `automations.chatbot.manage`.
- `POST …/chatbots/{chatbotId}/media/{fileId}/complete`, no body.
  - `200` → `{ fileId, kind, fileName, contentType, sizeBytes, url }` (`kind` lower-case; `url` absolute public URL; after compression `fileName` ends in `.jpg` and `contentType` is `image/jpeg`). Idempotent.
  - `422 automations.media.not_uploaded` | `automations.media.not_found` | `automations.media.unsupported_type` (content does not match, or rejected by the scan), each with `errors.fileId`.
- `GET …/chatbots/{chatbotId}` (`ChatbotEditorDto`) gains `mediaFiles: [{ fileId, kind, fileName, contentType, sizeBytes, url }]` — never `null`, `[]` without media.
- Simulator transcript entry gains `media: { kind, url, fileName, contentType } | null`.
- Inbox `MessageDto.media.url` is the absolute public URL for a bot media message (still a same-origin `/messaging/media/{id}` path for everything else).

---

## File map

| File | Responsibility |
| --- | --- |
| `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs`, `FileResource.cs` | `ChatbotMedia = 10`, system-owned; publish non-images and replace content for this owner |
| `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs`, `FileUploadPolicy.cs`, `UploadCompletion.cs`, `StorageSystemUploads.cs`, `CompleteUpload.cs` | Owner-scoped types and caps; sniff + shrink in completion |
| `src/Modules/Storage/Modules.Storage.Application/IChatbotMediaProcessing.cs`, `IChatbotMediaReferences.cs` (new) | Storage ports implemented in Bootstrapper |
| `src/Modules/Storage/Modules.Storage.Infrastructure/UnreferencedFiles/*` (new) | Generalized sweep (template images + chatbot media) and its worker |
| `src/Modules/Storage/Modules.Storage.Infrastructure/ChatbotMedia/*` (new) | Release processor and worker for the delete outbox message |
| `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs`, `FlowJson.cs`, `FlowMedia.cs` (new), `FlowValidator.cs`, `FlowIssue.cs` | Contract, file-id extraction, validation |
| `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs`, `FlowEngine.cs`, `Simulation.cs`, `AutomationsErrorCodes.cs` | `SendMedia`, audio split, transcript media, codes |
| `src/Modules/Automations/Modules.Automations.Application/ChatbotMediaPorts.cs`, `ChatbotMedia.cs` (new) | Ports, upload commands and handlers |
| `src/Modules/Automations/Modules.Automations.Application/ChatbotEditorBuilder.cs`, `AutomationsDtos.cs`, `Simulations.cs`, `Engine/RunEffects.cs`, `Engine/RunUnitLog.cs`, `AutomationsPorts.cs`, `Chatbots.cs` | Editor `mediaFiles`, simulator, send, delete outbox |
| `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs` | Two routes |
| `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/*` | Outbox projection, publisher, migration `AddOutboxProjection` |
| `src/Modules/Messaging/Modules.Messaging.Application/InteractiveSend.cs`, `IWhatsAppCloudClient.cs`, `BotOutbound.cs`, `IOutboundMessages.cs`, `IMessageQueries.cs`, `MessagingSupport.cs` | Link sends, media header, bot media, inbox URL |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs`, `Persistence/OutboundMessages.cs`, `MessageQueries.cs`, `MessagingDbContext.cs`, `MessageRecord.cs`, migration `AddBotMediaReference` | Payloads and the stored reference |
| `src/Bootstrapper/ChatbotMediaPolicy.cs`, `ChatbotMediaStore.cs`, `ChatbotMediaProcessing.cs`, `ChatbotMediaReferences.cs` (new), `AutomationMessenger.cs`, `QepServiceCollectionExtensions.cs` | Adapters and DI |
| `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | Renamed sweep worker, new release worker |

---

## Block A — Storage owner, flow contract, upload (Tasks 1-5)

### Task 1: `FileOwnerType.ChatbotMedia` with owner-scoped types, caps and publication

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs` (enum, `SystemOwnedFiles`), `src/Modules/Storage/Modules.Storage.Domain/FileResource.cs:184-212,380-400`
- Modify: `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs`, `FileUploadPolicy.cs`, `StorageSystemUploads.cs` (`OpenAsync`)
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs`, `FileUploadPolicyTests.cs`, `FileResourceTests.cs`

**Interfaces:**
- Produces: `FileOwnerType.ChatbotMedia = 10` (persisted by name, ≤ 20 chars, no migration), in `SystemOwnedFiles.OwnerTypes`; `FileOwnerModules.ModuleOf(ChatbotMedia) == TenantModuleKeys.Automations`.
- Produces in `FileUploadPolicy`: `public const long ChatbotMediaMaxSizeBytes = 100L * 1024 * 1024;`, `public const long ChatbotMediaLinkImageMaxBytes = 5L * 1024 * 1024;`, `public static long MaxSizeBytesFor(FileOwnerType ownerType)` (`ChatbotMedia` → 100 MB, every other → `MaxSizeBytes`), and `public static void ValidateDeclaration(string name, string mimeType, long sizeBytes, long maxSizeBytes, FileOwnerType ownerType)`. For `ChatbotMedia` the allowed table is the general one plus `.mp4 video/mp4`, `.3gp video/3gpp`, `.mp3 audio/mpeg`, `.m4a audio/mp4`, `.aac audio/aac`, `.amr audio/amr`, `.ogg audio/ogg`; the cap guard uses `MaxSizeBytesFor(ownerType)`. The existing 4-arg overload keeps the general table. `StorageSystemUploads.OpenAsync` calls the 5-arg overload with its `ownerType`.
- Produces in `FileResource`: `Publish` accepts any MIME when `OwnerType == ChatbotMedia` (images only for every other owner, unchanged); `ReplaceContentWithProcessedImage` accepts `PaymentProof` **or** `ChatbotMedia`.

- [ ] **Step 1: Write the failing tests.**
  - `AChatbotMediaFileBelongsToAutomations` and `AChatbotMediaFileIsSystemOwned`.
  - `ChatbotMediaAcceptsEveryOutboundMediaFormat`: theory over `("a.mp4","video/mp4")`, `("a.3gp","video/3gpp")`, `("a.mp3","audio/mpeg")`, `("a.m4a","audio/mp4")`, `("a.aac","audio/aac")`, `("a.amr","audio/amr")`, `("a.ogg","audio/ogg")` with `ownerType: ChatbotMedia` and `maxSizeBytes: 16 MB` → no throw.
  - `TheConsoleStillRejectsVideo`: `ValidateDeclaration("a.mp4","video/mp4",10,FileUploadPolicy.MaxSizeBytes)` throws `storage.file.type_not_allowed`.
  - `ChatbotMediaAllowsAHundredMegabyteDocument`: `("a.pdf","application/pdf", 100 MB, 100 MB, ChatbotMedia)` passes; the same with `FileOwnerType.Product` throws `ArgumentOutOfRangeException`.
  - `AChatbotMediaPdfCanBePublished`, `AProductPdfStillCannotBePublished` (`storage.file.public_image_required`).
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests` → `CS0117 'FileOwnerType' does not contain a definition for 'ChatbotMedia'`.
- [ ] **Step 3: Implement.** Enum comment in the existing style (OwnerId is the tenant; uploaded only through the chatbot editor session; persisted by name). Keep the console table untouched.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModulesTests|FullyQualifiedName~FileUploadPolicyTests|FullyQualifiedName~FileResourceTests|FullyQualifiedName~FileOwnerFilterTests"`.
- [ ] **Step 5: Commit.** `feat(storage): add the ChatbotMedia file owner with media types`

### Task 2: Completion sniffs chatbot media and shrinks a large image

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/IChatbotMediaProcessing.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Application/UploadCompletion.cs`, `StorageSystemUploads.cs`, `CompleteUpload.cs` (constructor of `UploadCompletion`), Storage test doubles and harness registrations
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/StorageSystemUploadsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// Implemented in Bootstrapper over Messaging's OutboundMediaSniffer and IMessagingImageCompressor (spec Q6).
  public interface IChatbotMediaProcessing
  {
      bool Matches(string name, ReadOnlySpan<byte> header);
      /// null = unreadable image.
      Task<ProcessedChatbotImage?> ShrinkAsync(byte[] content, CancellationToken cancellationToken);
  }
  public sealed record ProcessedChatbotImage(byte[] Content, string MimeType, string Extension);
  ```
- `UploadCompletion` rules for `OwnerType == ChatbotMedia`: the stored-size cap is `FileUploadPolicy.MaxSizeBytesFor(ownerType)`; the content check is `processing.Matches(resource.Name, content.AsSpan(0, Math.Min(64, content.Length)))` instead of `IFileContentInspector`; after a `Clean` scan, an `image/*` whose `content.Length > ChatbotMediaLinkImageMaxBytes` is shrunk, uploaded over the staging key, re-`Stat`ed and recorded with `ReplaceContentWithProcessedImage`, and the shrunk bytes continue to variants and promotion. `ShrinkAsync` returning `null` follows `QuarantineUnprocessableImageAsync` (→ `SystemUploadOutcome.Rejected`). Other owners are unchanged.

- [ ] **Step 1: Write the failing tests** (doubles for object storage, scanner and the new port):
  - `AChatbotMediaMp3IsCheckedByTheMediaSnifferNotTheConsoleInspector`: inspector double returns `false`, processing returns `true` → `Available`.
  - `AChatbotMediaFileWhoseHeaderDoesNotMatchIsRejected`.
  - `ASixMegabyteChatbotImageIsShrunkBeforePromotion`: `Name == "catalogo.jpg"`, `MimeType == "image/jpeg"`, `SizeBytes == shrunk.Length`, promoted object holds the shrunk bytes.
  - `AFourMegabyteChatbotImageIsKeptAsIs` (shrink never called).
  - `AnUnreadableLargeChatbotImageIsRejected`.
  - `AFortyMegabyteChatbotPdfPassesTheStoredSizeCap`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests` → `CS0246 IChatbotMediaProcessing`.
- [ ] **Step 3: Implement.** Thread the port through `UploadCompletion`, `StorageSystemUploads` and `CompleteUploadHandler`. Storage registers no implementation: the real one is Bootstrapper's (Task 5). Add a pass-through fake to the Storage integration harness (`PaymentProofStorageHarness`) so its host resolves.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~StorageSystemUploadsTests"`.
- [ ] **Step 5: Commit.** `feat(storage): sniff and shrink chatbot media on completion`

### Task 3: Flow contract — `MessageMedia`, JSON and file-id extraction

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs:29-33`, `FlowJson.cs:141-147,344-345,409-415`
- Create: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowMedia.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowJsonTests.cs`, `TemplateParametersTests.cs`, new `FlowMediaTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record MessageNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, UrlButton? UrlButton, MessageMedia? Media = null);
  public sealed record MessageMedia(string Kind, Guid FileId);
  public static class ChatbotMediaKinds { public const string Image = "image", Video = "video", Audio = "audio", Document = "document"; public static readonly IReadOnlyList<string> All; }
  public static class FlowMedia
  {
      public static IReadOnlyList<Guid> FileIdsOf(FlowDefinition flow);   // distinct, in node order
      public static IReadOnlyList<Guid> FileIdsIn(string flowJson);       // tolerant scan of nodes[*] kind=message media.fileId; never throws, malformed → []
  }
  ```
- `FlowJson` writes `media` always (`null` or `{ kind, fileId }`, `fileId` as lower-case `D`), reads absent/`null` as `null`, a kind outside `ChatbotMediaKinds.All` or a non-guid `fileId` as `flow.malformed`.

- [ ] **Step 1: Write the failing tests.**
  - `AMessageWithoutMediaReadsNull` (old draft JSON) and `AMessageWithMediaRoundTrips` (`Serialize(Parse(json))` keeps `"media":{"kind":"image","fileId":"…"}`).
  - `AnUnknownMediaKindIsMalformed`, `ANonGuidFileIdIsMalformed` (`FlowFormatException.Code == "flow.malformed"`).
  - `FileIdsOfListsEachMediaOnce`; `FileIdsInReadsRawJsonAndIgnoresOtherKinds`; `FileIdsInOfGarbageIsEmpty` (input `"{"`).
  - `ApplyKeepsTheMedia`: `TemplateParameters.Apply` on a flow with media keeps `Media` equal.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests` → `CS1739` (no `Media` parameter) / `CS0103 FlowMedia`.
- [ ] **Step 3: Implement.** `TemplateParameters.Apply` already uses `message with { … }`, so `Media` carries; the test pins it.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowJsonTests|FullyQualifiedName~FlowMediaTests|FullyQualifiedName~TemplateParametersTests"`.
- [ ] **Step 5: Commit.** `feat(automations): optional media on the message block`

### Task 4: Upload session and completion endpoints

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/ChatbotMediaPorts.cs`, `ChatbotMedia.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/AutomationsErrorCodes.cs`, `src/Modules/Automations/Modules.Automations.Api/ChatbotEndpoints.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers next to `:1399`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotMediaHandlersTests.cs` (new); `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotMediaApiTests.cs` (new), `FakeChatbotMediaStore.cs` (new), `AutomationsApiHarness.cs` (registration)

**Interfaces:**
- Produces (Domain): `MediaUnsupportedType = "automations.media.unsupported_type"`, `MediaTooLarge = "automations.media.too_large"`, `MediaNotUploaded = "automations.media.not_uploaded"`, `MediaNotFound = "automations.media.not_found"`.
- Produces (Application, `ChatbotMediaPorts.cs`):
  ```csharp
  public sealed record ChatbotMediaRule(string Kind, string ContentType, long MaxBytes);
  public interface IChatbotMediaPolicy { ChatbotMediaRule? RuleFor(string fileName); }   // null = not sendable
  public interface IChatbotMediaStore
  {
      Task<ChatbotMediaSession> OpenUploadAsync(Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken);
      /// Completes (idempotent), publishes, and reports. Foreign/unknown id → NotFound.
      Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
      /// One batch lookup: Available + published ChatbotMedia files of this tenant; absent ids are simply missing.
      Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
  }
  public sealed record ChatbotMediaSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);
  public enum ChatbotMediaState { Available, NotFound, NotUploaded, Rejected }
  public sealed record ChatbotMediaCompletion(ChatbotMediaState State, ChatbotMediaFile? File);
  public sealed record ChatbotMediaFile(Guid FileId, string Kind, string FileName, string ContentType, long SizeBytes, Uri Url);
  ```
- Produces (Application, `ChatbotMedia.cs`): `CreateChatbotMediaSessionCommand(Guid TenantId, Guid ChatbotId, string? FileName, string? ContentType, long SizeBytes) : ICommand<ChatbotMediaSessionDto>`, `CompleteChatbotMediaCommand(Guid TenantId, Guid ChatbotId, Guid FileId) : ICommand<ChatbotMediaFileDto>`, `ChatbotMediaSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers)`, `ChatbotMediaFileDto(Guid FileId, string Kind, string FileName, string ContentType, long SizeBytes, string Url)` with `static ChatbotMediaFileDto From(ChatbotMediaFile file)`, validator `CreateChatbotMediaSessionValidator` (messages and `OverridePropertyName` copied from `CreateTemplateHeaderImageSessionValidator`), and
  ```csharp
  public static class ChatbotMediaMessages
  {
      public const string UnsupportedType = "Ese tipo de archivo no se puede enviar. Usa JPG, PNG, MP4, 3GP, MP3, M4A, AAC, AMR, OGG, PDF, Word o Excel.";
      public const string TooLarge = "El archivo supera el tamaño máximo para su tipo.";
      public const string NotUploaded = "El archivo todavía no terminó de subir. Intenta de nuevo.";
      public const string NotFound = "No encontramos el archivo. Vuelve a subirlo.";
  }
  ```
- Routes (in `MapChatbotEndpoints`): `POST /{chatbotId:guid}/media/upload-session` (body `CreateChatbotMediaSessionRequest(string? FileName, string? ContentType, long SizeBytes)`) and `POST /{chatbotId:guid}/media/{fileId:guid}/complete`, both `.RequireAuthorization(AutomationsPermissions.ChatbotManage)`, `200`, no `If-Match` (the chatbot does not change).
- Handler order: `AutomationsAuthorization.EnsureAsync(…, ChatbotManage)` → validator → chatbot exists in tenant (`AutomationsNotFound.Chatbot`) → session: `policy.RuleFor(fileName)`; `null` or `contentType.Trim()` ≠ `rule.ContentType` (ordinal-ignore-case) → `MediaUnsupportedType` with `errors.fileName`; `sizeBytes > rule.MaxBytes` → `MediaTooLarge` with `errors.sizeBytes`; then `OpenUploadAsync(…, rule.ContentType, …, rule.MaxBytes)`. Complete: `NotFound → MediaNotFound`, `NotUploaded → MediaNotUploaded`, `Rejected → MediaUnsupportedType`, each with `errors.fileId`.

- [ ] **Step 1: Write the failing unit tests** (fake policy and store record calls): `APngUnderTheCapOpensASession`; `AGifIsUnsupportedOnFileNameBeforeAnyUpload`; `ADeclaredTypeOtherThanTheCanonicalIsUnsupported` (`a.mp3` with `audio/mp3`); `AVideoOverSixteenMegabytesIsTooLargeOnSizeBytes`; `WithoutChatbotManageItIsForbidden`; `AnUnknownChatbotIsNotFound`; `CompleteMapsEachStateToItsCodeOnFileId` (theory over the three states); `CompleteReturnsTheFile`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests` → `CS0246 IChatbotMediaStore`.
- [ ] **Step 3: Implement** codes, ports, handlers, endpoints and DI. In the integration harness add `public FakeChatbotMediaStore Media { get; } = new();` registered with `RemoveAll<IChatbotMediaStore>()` + `AddSingleton<IChatbotMediaStore>(Media)`; the fake keeps files per tenant and returns `https://public.test/<fileId>/<fileName>` as `Url`. The policy stays the real adapter (Task 5).
- [ ] **Step 4: Integration test** `ChatbotMediaApiTests.TheSessionNeedsOnlyChatbotManage`: a member with only `automations.chatbot.manage` gets `200` with `fileId`, `uploadUrl`, `headers`; a member with only `automations.chatbot.read` gets `403`. `ACompleteOfAnotherTenantsFileIsNotFoundOnFileId`: `422`, `code == automations.media.not_found`, `errors.fileId` present.
- [ ] **Step 5: GREEN.** Unit filter `ChatbotMediaHandlersTests`; process check; integration filter `ChatbotMediaApiTests` by each test name.
- [ ] **Step 6: Commit.** `feat(automations): upload media for a chatbot message`

### Task 5: Bootstrapper adapters — policy, store, processing

**Files:**
- Create: `src/Bootstrapper/ChatbotMediaPolicy.cs`, `ChatbotMediaStore.cs`, `ChatbotMediaProcessing.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (next to `MessagingTemplateImageStore`, `:1812`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ChatbotMediaStoreTests.cs`, `ChatbotMediaPolicyParityTests.cs` (new)

**Interfaces:**
- Consumes: Task 1-2 Storage API; Task 4 ports; Messaging `OutboundMediaPolicy` (`FormatOf`, `RuleOf`, `KindOf`, `MimeTypeOf`), `OutboundMediaSniffer.Detect(ReadOnlySpan<byte>, string extension)`, `IMessagingImageCompressor.CompressAsync(Stream, ct)`.
- Produces: `internal sealed class ChatbotMediaPolicy : IChatbotMediaPolicy` (WhatsApp column; kind = lower-case `MessageKind` name); `internal sealed class ChatbotMediaStore : IChatbotMediaStore` (owner `(ChatbotMedia, ownerId: tenantId)`, shape of `MessagingTemplateImageStore`: open → `IStorageSystemUploads.OpenAsync`, translating `storage.file.type_not_allowed|mime_mismatch` → `MediaUnsupportedType` and `size_invalid` → `MediaTooLarge`; complete → `CompleteAsync`, then publish once with `FilePublication.PublishAsync`, audit `storage.file.published` as `executionContext.SubjectId`, save, URL from `IPublicObjectStorage.GetUrl`; an already-published file returns its URL without publishing again; resolve → `IFileResourceRepository.ListByIdsAsync`, keeping only `TenantId == tenantId && OwnerType == ChatbotMedia && OwnerId == tenantId && Status == Available && PublicStorageKey != null`); `internal sealed class ChatbotMediaProcessing : IChatbotMediaProcessing` (`Matches` = `OutboundMediaSniffer.Detect(header, OutboundMediaPolicy.ExtensionOf(name)) is not null`; `ShrinkAsync` = the compressor, returned as `("image/jpeg", ".jpg")`).

- [ ] **Step 1: Write the failing tests.**
  - Parity `EveryWhatsAppRuleIsAcceptedByStorageForChatbotMedia`: for each `OutboundMediaPolicy.Rules`, `FileUploadPolicy.ValidateDeclaration("x" + rule.Extension, rule.MimeType, 1, rule.WhatsAppMaxBytes, FileOwnerType.ChatbotMedia)` does not throw, and `ChatbotMediaPolicy.RuleFor("x" + rule.Extension)` equals `(kind, rule.MimeType, rule.WhatsAppMaxBytes)`.
  - `AWebpIsNotSendable` (`RuleFor("a.webp") == null`).
  - `OpenCreatesAChatbotMediaFileOwnedByTheTenantWithTheRuleCap`.
  - `CompletePublishesOnceAndReturnsThePublicUrl` (second call does not publish again).
  - `CompleteMapsObjectMissingToNotUploadedAndRejectedToRejected`.
  - `ResolveIgnoresAnotherTenantsFile` (also another owner type, a deleted file and an unpublished one).
- [ ] **Step 2: RED.** `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → `CS0246 ChatbotMediaStore`.
- [ ] **Step 3: Implement** and register the three adapters (scoped) plus `IChatbotMediaProcessing`.
- [ ] **Step 4: GREEN.** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ChatbotMediaStoreTests|FullyQualifiedName~ChatbotMediaPolicyParityTests"`, then `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~AutomationsLayerTests|FullyQualifiedName~StorageLayerTests|FullyQualifiedName~CompositionRootTests"`.
- [ ] **Step 5: Commit.** `feat(bootstrapper): Storage and Messaging adapters for chatbot media`

**Block A end:** unit projects Storage, Automations, Bootstrapper in full; integration class `ChatbotMediaApiTests` once; Storage integration classes `StorageSystemUploadsApiTests`, `SystemOwnedFilesApiTests` once. **Block A review** (one reviewer, Tasks 1-5).

---

## Block B — Validation, editor, engine, simulator, WhatsApp send, inbox (Tasks 6-10)

### Task 6: Validator rules and the editor's `mediaFiles`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowIssue.cs` (context and codes), `FlowValidator.cs:175-186,600-612,776-780`
- Modify: `src/Modules/Automations/Modules.Automations.Application/ChatbotEditorBuilder.cs`, `AutomationsDtos.cs` (`ChatbotEditorDto`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs`, `ChatbotEditorBuilderTests.cs`; integration `ChatbotsApiTests.cs`

**Interfaces:**
- Consumes: `MessageMedia`, `FlowMedia.FileIdsOf` (Task 3); `IChatbotMediaStore.ResolveAsync` (Task 4).
- Produces: `FlowValidationContext(…, IReadOnlyDictionary<Guid, string>? MediaFiles = null)` (fileId → resolved kind; `null` = none); codes `MessageMediaMissing = "message.media_missing"`, `MessageAudioCaptionSeparate = "message.audio_caption_separate"`, `MessageAudioButtonSeparate = "message.audio_button_separate"`; `ChatbotEditorDto(…, bool AiEnabled, IReadOnlyList<ChatbotMediaFileDto> MediaFiles)`.
- Rules for a `MessageNode` with `Media`:
  - `text.empty` is not raised, **unless** the block has a URL button (the `cta_url` body is required).
  - Length cap: audio without button → 4096 (the text goes alone, after the audio); every other media case → 1024.
  - `fileId` absent from `context.MediaFiles`, or present with a different kind → Error `message.media_missing`, message «Falta el archivo de este mensaje. Vuelve a subirlo.».
  - Audio with non-empty text → Warning `message.audio_caption_separate` («El audio no lleva texto: se envía como un mensaje aparte, después del audio.»); audio with button → Warning `message.audio_button_separate` («El audio no lleva botón: el botón se envía en un mensaje aparte, después del audio.»).
  - `{variables}` in the caption keep today's check (`InterpolatedTexts` unchanged).
- `ChatbotEditorBuilder`: resolve once per request **only if** `FlowMedia.FileIdsOf(flow)` is non-empty (as `AiOfAsync` does), feed `MediaFiles` to the context and `[.. resolved.Select(ChatbotMediaFileDto.From)]` to the DTO; the save/validate/publish path (`ValidateAsync`) resolves the same way. A stored draft that does not parse gives `mediaFiles: []`.

- [ ] **Step 1: Write the failing tests.**
  - Validator: `MediaWithoutTextIsValid`; `MediaWithAButtonStillNeedsText`; `ACaptionOver1024IsTooLong`; `AudioTextUpTo4096IsAccepted`; `AMissingFileIsAnErrorWithTheSpecText`; `AFileOfAnotherKindIsMissing`; `AudioWithTextWarnsCaptionSeparate`; `AudioWithAButtonWarnsButtonSeparate`; `AnUnknownVariableInTheCaptionStillWarns`.
  - Builder: `AForeignFileIsMissingAndNotListed` (fake store returns nothing for the id → issue present, `MediaFiles` empty); `AFlowWithoutMediaNeverAsksTheStore`.
  - Integration `TheEditorListsTheDraftsMediaFiles`: seed the fake store with a file, save a draft referencing it, `GET` → `mediaFiles[0].url == "https://public.test/…"`; publishing a draft with a missing id → `422 automations.chatbot.invalid` with `message.media_missing` under `errors.flow`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests` → `CS0117 FlowIssueCodes.MessageMediaMissing`.
- [ ] **Step 3: Implement.** `IChatbotMediaStore` joins `ChatbotEditorBuilder`'s constructor; the DTO comment states the BFF reason (the editor draws files it did not upload in this session without one request per file).
- [ ] **Step 4: GREEN.** Unit filter `FlowValidatorTests|ChatbotEditorBuilderTests`; process check; integration filter `TheEditorListsTheDraftsMediaFiles`.
- [ ] **Step 5: Commit.** `feat(automations): validate message media and list it in the editor`

### Task 7: Engine `SendMedia` and the simulator

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs`, `FlowEngine.cs:659-671`, `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs:122-142`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Simulations.cs` (`SimulationRunner` ctor, effect loop, `Lines`), `Engine/RunUnitLog.cs:52-57`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineTests.cs`, `SimulationRunnerTests.cs`, `RunUnitLogTests.cs`

**Interfaces:**
- Produces: `Effect.SendMedia(string NodeId, string Kind, Guid FileId, string? Caption, UrlButton? UrlButton) : Effect`; `TranscriptEntry(…, DateTimeOffset At, TranscriptMedia? Media = null)`; `TranscriptMedia(string Kind, string Url, string FileName, string ContentType)`; `RunUnitLog` names it `"Media"`.
- Engine, for a message with media (caption and button interpolated with `Text`; a caption blank after interpolation is `null`):
  - image / video / document → one `SendMedia(id, kind, fileId, caption, button)`.
  - audio → `SendMedia(id, audio, fileId, null, null)`, then the follow-up: with button `SendInteractive(CtaUrl(captionOrEmpty, label, url))`, otherwise `SendOptional(text)`.
  - without media → today's behaviour, unchanged.
- `SimulationRunner(IHttpBlockExecutor http, IAutomationBusinessHours businessHours, IChatbotMediaStore media, IClock clock)`: on `SendMedia` it resolves the file; found → bot line with `Text = caption ?? ""`, `UrlButton` when present and `Media` set; not found → a `System` line with «Falta el archivo de este mensaje. Vuelve a subirlo.» and the bot line without media.

- [ ] **Step 1: Write the failing tests.**
  - `AnImageWithCaptionAndButtonIsOneSendMedia`; `ACaptionBlankAfterInterpolationIsNull`; `AudioWithTextIsTwoSendsInOrder` (`SendMedia` with null caption, then `SendText`); `AudioWithAButtonSendsTheCtaAfterTheAudio`; `AMessageWithoutMediaStillSendsText`.
  - Simulator: `AnImageShowsAsMediaInTheTranscript` (`Media.Url == fake url`, `Text == caption`); `AudioWithTextIsTwoTranscriptLines`; `AMissingFileAddsASystemLine`.
  - `ASendMediaIsLoggedAsMedia`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests` → `CS0426 'SendMedia' does not exist in 'Effect'`.
- [ ] **Step 3: Implement.** The simulator's runner is built in the simulation handlers' DI; add the store there.
- [ ] **Step 4: GREEN.** Unit filter `FlowEngineTests|SimulationRunnerTests|RunUnitLogTests|FlowEngineStepRecordTests`.
- [ ] **Step 5: Commit.** `feat(automations): send media from the engine and the simulator`

### Task 8: Meta link sends and the `cta_url` media header

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/InteractiveSend.cs`, `IWhatsAppCloudClient.cs`, `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:240-275`
- Modify: every other `IWhatsAppCloudClient` implementation (test fakes, `ServiceContractsMetaWhatsAppSender` if it implements it) to compile
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/InteractiveSendTests.cs`, `MessageMappingInteractiveTests.cs`, the Meta client unit tests (`WhatsAppCloudClientTests` or the file that pins `SendImageAsync` payloads)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record InteractiveMediaHeader(string Type, Uri Link, string? FileName);   // Type: image | video | document
  public sealed record CtaUrlSend(string Body, string DisplayText, Uri Url, string? Header = null, string? Footer = null, InteractiveMediaHeader? MediaHeader = null) : InteractiveSend(Body, Header, Footer);
  public sealed record MediaLink(MessageKind Kind, Uri Url, string? FileName);          // Kind: Image | Video | Audio | Document
  // IWhatsAppCloudClient
  Task<SendResult> SendMediaLinkAsync(MessagingSender sender, SendTarget target, MediaLink media, string? caption, string callbackData, string? contextWamid, CancellationToken cancellationToken);
  ```
- Validation codes: `interactive.header.conflict` (text `Header` and `MediaHeader` together), `interactive.header.media_type_invalid`, `interactive.header.link_not_https`.
- `ToMetaObject`: `header = { type: <Type>, <Type>: { link, filename? } }` (`filename` only for document). `SendMediaLinkAsync`: `{ type: <kind>, <kind>: { link, caption?, filename? } }`; audio never carries `caption`; `filename` only for document.
- `InteractiveDetailsJson.Parse` keeps returning the `cta_url` DTO when the stored object has a media header (the media itself reaches the bubble through `MessageDto.Media`, Task 9).

- [ ] **Step 1: Write the failing tests.**
  - `ACtaWithAnImageHeaderSerializesTheLink`: `ToMetaObject(...)["header"]` is `{type:"image", image:{link:"https://pub/x.jpg"}}`; document adds `filename`.
  - `TextAndMediaHeaderTogetherConflict`; `AnAudioHeaderIsInvalid`; `AnHttpHeaderLinkIsNotHttps`.
  - `AStoredCtaWithAMediaHeaderStillParses` (`Interactive.Cta.Url` set).
  - Meta client: `SendMediaLinkPostsTheLinkAndCaption` for image; `AnAudioLinkNeverCarriesACaption`; `ADocumentLinkCarriesItsFileName`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests` → `CS0246 InteractiveMediaHeader`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN.** Unit filter `InteractiveSendTests|MessageMappingInteractiveTests|WhatsAppCloudClient`, then `dotnet build` the solution (every client fake compiles).
- [ ] **Step 5: Commit.** `feat(messaging): send media by link and cta_url media headers`

### Task 9: Bot media in `IBotOutbound` and the inbox reference

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BotOutbound.cs`, `IOutboundMessages.cs` (`OutboundMedia`), `IMessageQueries.cs` (`MessageMediaRow`), `MessagingSupport.cs:252`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs:290-306`, `MessageQueries.cs:14`, `MessageRecord.cs:59-75`, `MessagingDbContext.cs:456-474`
- Create: migration `AddBotMediaReference` (Messaging)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BotOutboundTests.cs`

**Interfaces:**
- Consumes: Task 8 types.
- Produces:
  ```csharp
  public sealed record BotMediaSend(MessageKind Kind, Uri Link, string FileName, string MimeType, long SizeBytes, Guid StorageFileId);
  public sealed record BotSend(Guid TenantId, Guid ConversationId, BotRef Bot, Guid ClientId, string? Text, InteractiveSend? Interactive, BotMediaSend? Media = null);
  public sealed record OutboundMedia(string MimeType, long SizeBytes, string? Sha256Hex, string? FileName = null, Guid? StorageFileId = null, string? PublicUrl = null);
  public sealed record MessageMediaRow(string MimeType, string? FileName, string? PublicUrl = null);
  ```
  (the chatbot convenience constructor of `BotSend` gains the optional `media` too).
- Columns: `message_media.storage_file_id uuid NULL`, `message_media.public_url varchar(2048) NULL`. With `PublicUrl` set, `UpsertMediaAsync` writes `storage_key = NULL`, `meta_media_id = ''`, `stored_at = occurredAt` (no copy job, no R2 object, customer deletion skips it).
- `BotOutbound` with `Media` (WhatsApp; another channel → `Rejected(MessagingErrorCodes.MessageRejected)` until Block D): same claim, idempotency, window and settlement as text. Caption (`Text`) acceptable and ≤ `SendMediaMessageHandler.MaxCaptionLength`; audio with a caption is rejected; link must be `https`. Without `Interactive`: draft `Kind = Media.Kind`, `Text = null`, `Caption = caption`, Meta `SendMediaLinkAsync`. With `Interactive` (`CtaUrlSend` with `MediaHeader`): draft `Kind = Interactive`, `Text = body`, `DetailsJson` from the interactive, Meta `SendInteractiveAsync`. Both write `OutboundMedia(MimeType, SizeBytes, null, FileName, StorageFileId, Link.AbsoluteUri)`.
- `MessagingSupport.ToDto`: `media.url = row.Media.PublicUrl ?? MediaUrl(tenantId, row.Id)` (comment: BFF, the bubble loads the public file; no bytes are copied per send).

- [ ] **Step 1: Write the failing integration tests** (`FakeMetaGraphHandler` captures bodies):
  - `ABotImageGoesByLinkWithCaption`: Meta body `type == "image"`, `image.link == url`, `image.caption == "Catálogo"`; the row has `kind = Image`, `caption`, and `message_media.public_url == url`, `storage_key IS NULL`; the thread DTO `media.url == url`.
  - `AMediaWithButtonIsOneInteractiveWithAMediaHeader`: one Meta call, `interactive.type == "cta_url"`, `header.image.link == url`; the thread DTO has `media.url == url` **and** `interactive.cta`.
  - `ABotDocumentCarriesItsFileName`.
  - `RepeatingTheClientIdDoesNotResendMedia`.
  - `ABotAudioWithACaptionIsRejected`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests` → `CS0246 BotMediaSend`.
- [ ] **Step 3: Implement**, then `dotnet ef migrations add AddBotMediaReference --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`; check `Up` only adds the two nullable columns.
- [ ] **Step 4: GREEN.** Process check; integration filter each new name.
- [ ] **Step 5: Commit.** `feat(messaging): bot media messages by public link`

### Task 10: `RunEffects` sends media through the messenger

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs:407-414`, `Engine/RunEffects.cs:96-125,349-354`, `src/Bootstrapper/AutomationMessenger.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`; integration `EngineEndToEndTests.cs`

**Interfaces:**
- Consumes: `Effect.SendMedia` (Task 7), `IChatbotMediaStore.ResolveAsync` (Task 4), `BotSend.Media` / `CtaUrlSend.MediaHeader` (Tasks 8-9).
- Produces: `IAutomationMessenger.SendMediaAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, BotMediaMessage message, CancellationToken cancellationToken)`; `public sealed record BotMediaMessage(ChatbotMediaFile File, string? Caption, UrlButton? UrlButton);`.
- `RunEffects`: `SendMedia` takes a `clientId` like the other sends (same counter); resolves the file with one `ResolveAsync`; missing → `BotSendOutcome.Rejected(AutomationsErrorCodes.MediaNotFound)` through `StopAsync` (today's failure path: `SendFailed` handoff), no silent text fallback. The **resolved** kind, name and type are sent, never the flow's.
- `AutomationMessenger.SendMediaAsync`: kind → `MessageKind`; with a button whose URL is not absolute http(s) → `Rejected("messaging.interactive.invalid")` (as today); with a button → `Interactive = new CtaUrlSend(caption ?? "", label, url, MediaHeader: new InteractiveMediaHeader(kind, file.Url, kind == document ? file.FileName : null))`, `Text = null`; without → `Text = caption`; `Media = new BotMediaSend(kind, file.Url, file.FileName, file.ContentType, file.SizeBytes, file.FileId)`.

- [ ] **Step 1: Write the failing tests.**
  - Unit `ASendMediaCallsTheMessengerWithTheResolvedFile`; `AMissingFileFailsTheRunWithMediaNotFound` (handoff kind `SendFailed`, code `automations.media.not_found`); `ARetriedUnitDoesNotResendTheAudio` (first unit: audio `Sent`, follow-up `Rejected`; retry: the messenger fake sees the audio's `clientId` again and the follow-up's, and Messaging's idempotency is what prevents the resend — assert the two `clientId`s are distinct and stable across attempts).
  - Adapter `AnImageWithButtonBecomesACtaWithAMediaHeader`; `AnAudioBecomesAPlainMediaSend`.
  - Integration `APublishedImageMessageReachesMetaByLink`: inbound «hola» → Meta body `image.link == "https://public.test/…"` and caption.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Automations/Modules.Automations.UnitTests` → `CS0535 'FakeMessenger' does not implement 'SendMediaAsync'`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN.** Unit filters; process check; integration filter `APublishedImageMessageReachesMetaByLink`.
- [ ] **Step 5: Commit.** `feat(automations): bots send their message media`

**Block B end:** unit projects Automations, Messaging, Bootstrapper in full; integration classes `ChatbotsApiTests`, `BotOutboundTests`, `SimulationsApiTests` once, then `EngineEndToEndTests` once (≈22 min, last). **Block B review** (one reviewer, Tasks 6-10).

---

## Block C — No orphaned public files (Tasks 11-12)

### Task 11: In-use query and the generalized daily sweep

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/IChatbotMediaReferences.cs`, `src/Bootstrapper/ChatbotMediaReferences.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/UnreferencedFiles/UnreferencedFileCandidates.cs`, `UnreferencedFileSweep.cs`, `UnreferencedFileReleaser.cs`, `UnreferencedFileSweepWorker.cs`, `ChatbotMediaSweep.cs`
- Modify (move behaviour, keep names where noted): `TemplateImages/TemplateHeaderImageSweep.cs` (becomes a subclass), delete `TemplateImages/TemplateHeaderImageFiles.cs` and `TemplateHeaderImageSweepWorker.cs`; `StorageInfrastructureExtensions.cs:82-85`; `QepServiceCollectionExtensions.cs:1814`; `MultiReplicaInventory.cs`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/TemplateHeaderImageSweepTests.cs` (fakes renamed), new `ChatbotMediaSweepTests.cs`; integration `tests/Modules/Storage/Modules.Storage.IntegrationTests/TemplateHeaderImageSweepTests.cs` (stays green), new `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotMediaReferencesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IChatbotMediaReferences   // Storage.Application; same contract as ITemplateImageReferences
  { Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken); }
  internal sealed record UnreferencedFileCandidate(Guid TenantId, FileResourceId Id, FileOwnerType OwnerType, DateTimeOffset CreatedAt);
  internal interface IUnreferencedFileCandidates   // TemplateHeaderImageFiles with the owner as a parameter
  {
      Task<IReadOnlyList<UnreferencedFileCandidate>> ListStaleAsync(FileOwnerType ownerType, DateTimeOffset createdBefore, UnreferencedFileCandidate? after, int take, CancellationToken cancellationToken);
      Task<FileResource?> ClaimAsync(FileOwnerType ownerType, Guid tenantId, FileResourceId id, CancellationToken cancellationToken);
      Task CommitAsync(CancellationToken cancellationToken);
      Task AbandonAsync();
  }
  internal interface IUnreferencedFileSweep { FileOwnerType OwnerType { get; } Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken); }
  /// Claim under lock → recheck the reference → remove public copies → soft-delete → system audit storage.file.deleted with the reason → commit.
  internal sealed class UnreferencedFileReleaser(IUnreferencedFileCandidates files, FilePublication filePublication, IStorageAuditPublisher auditPublisher, ILogger<UnreferencedFileReleaser> logger)
  { Task<bool> ReleaseAsync(FileOwnerType ownerType, Guid tenantId, FileResourceId id, string reason, Func<Task<bool>> stillReferenced, DateTimeOffset now, CancellationToken cancellationToken); }
  ```
  `UnreferencedFileSweep` is the abstract base holding today's paging/grouping/fail-closed loop (`Grace = 24 h`, `BatchSize = 200`) with abstract `OwnerType`, `Reason`, `FindReferencedAsync`. `TemplateHeaderImageSweep` keeps `Reason = "template_header_image_unreferenced"`; `ChatbotMediaSweep` uses `ChatbotMedia`, `Reason = "chatbot_media_unreferenced"`. `UnreferencedFileSweepWorker` (schedule of today's worker) resolves every `IUnreferencedFileSweep` and runs each in its own `try`, so one failing sweep does not skip the other.
- `ChatbotMediaReferences` (raw SQL over `AutomationsDbContext`, ids compared as lower-case text so a malformed value never throws a cast):
  ```sql
  SELECT DISTINCT node->'media'->>'fileId' AS "Value"
  FROM (SELECT draft_flow AS flow FROM automations.chatbots WHERE tenant_id = {tenantId}
        UNION ALL SELECT flow FROM automations.chatbot_versions WHERE tenant_id = {tenantId}
        UNION ALL SELECT flow FROM automations.chatbot_templates WHERE tenant_id = {tenantId}) AS f
  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(f.flow->'nodes') = 'array' THEN f.flow->'nodes' ELSE '[]'::jsonb END) AS node
  WHERE node->>'kind' = 'message' AND node->'media'->>'fileId' = ANY({idsAsLowerText})
  ```
  No index: the scan is one tenant's chatbots, versions and saved templates, asked once per tenant per page. Saved tenant templates count as in use (a saved template is a kept artifact; see Spec gaps).

- [ ] **Step 1: Write the failing tests.**
  - `ChatbotMediaReferencesTests`: `AFileInADraftIsReferenced`; `AFileOnlyInAPublishedVersionIsReferenced`; `AFileInASavedTemplateIsReferenced`; `AnotherTenantsReferenceDoesNotCount`; `AnUnreferencedFileIsNotReturned`.
  - `ChatbotMediaSweepTests` (unit, fakes): `AnUnreferencedChatbotFileOlderThanADayIsReleasedWithItsReason`; `AReferencedOneIsKept`; `AFailingReferenceQueryKeepsTheTenantsFiles`; `TheRecheckUnderTheLockKeepsAFileReferencedMeanwhile`.
  - `TheWorkerRunsTheChatbotSweepWhenTheTemplateSweepThrows`.
  - Existing `TemplateHeaderImageSweepTests` (unit and integration) pass after the rename.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests` → `CS0246 IUnreferencedFileCandidates`.
- [ ] **Step 3: Implement**; update the inventory entry of the worker (`WorkerClassification` unchanged from the template sweep, evidence pointing to `UnreferencedFileCandidates.cs` `FOR UPDATE SKIP LOCKED`).
- [ ] **Step 4: GREEN.** Unit filters `ChatbotMediaSweepTests|TemplateHeaderImageSweepTests`; process check; integration `ChatbotMediaReferencesTests` (Automations) and `TemplateHeaderImageSweepTests` (Storage) by name, one at a time; `ArchitectureTests` filter `MultiReplicaInventoryTests|StorageLayerTests|CompositionRootTests`.
- [ ] **Step 5: Commit.** `feat(storage): daily sweep of unreferenced chatbot media`

### Task 12: Chatbot delete writes a release message; Storage consumes it

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/ChatbotMediaPorts.cs` (event name, port), `Chatbots.cs:801-829`
- Create: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsOutboxRecord.cs`, `ChatbotMediaReleasePublisher.cs`; migration `AddOutboxProjection` (Automations)
- Modify: `AutomationsDbContext.cs` (`OutboxMessages`, mapping copied from `MessagingDbContext.cs:905-914`, `ExcludeFromMigrations`), Automations Infrastructure DI
- Modify: `src/Modules/Storage/Modules.Storage.Application/StorageSubscribedEvents.cs`
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/ChatbotMedia/ChatbotMediaReleaseProcessor.cs`, `ChatbotMediaReleaseWorker.cs`; registration in `StorageInfrastructureExtensions.cs`; `MultiReplicaInventory.cs` entry
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ChatbotMediaReleasedEventParityTests.cs` (new); Automations integration `ChatbotsApiTests.cs`; Storage integration `ChatbotMediaReleaseTests.cs` (new, on `PaymentProofStorageHarness`, removing the new worker as the template release test does)

**Interfaces:**
- Produces: `AutomationsIntegrationEvents.ChatbotMediaReleased = "automations.chatbot.media-released.v1"` and `StorageSubscribedEvents.ChatbotMediaReleased` (same literal); `public interface IChatbotMediaReleases { void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt); }`, Infrastructure implementation adding an outbox row (`Id = Guid.CreateVersion7()`, payload `{ "tenantId", "fileIds": [...] }`, `CorrelationId = Guid.NewGuid().ToString()`, `Attempts = 0`) without saving.
- `DeleteChatbotHandler`: before `chatbots.Remove`, `ids = FlowMedia.FileIdsIn(chatbot.DraftFlowJson) ∪ each version's FileIdsIn(FlowJson)`; publish only when non-empty; one `SaveChangesAsync` commits both. `FileIdsIn` never throws, so the delete never fails because of a file.
- `ChatbotMediaReleaseProcessor` (`Consumer = "storage.chatbot-media-release"`, `Reason = "chatbot_media_released"`, skeleton of `TemplateHeaderImageReleaseProcessor`): per message, `IChatbotMediaReferences.FindReferencedAsync(tenantId, fileIds)`; each unreferenced id goes through `UnreferencedFileReleaser` (Task 11) with a recheck; when every id was released or skipped, add the inbox row and save. A failing reference query or release adds no inbox row (retried next tick); a malformed payload is logged and marked. Worker: `DoorbellPolling.RunAsync` on `DoorbellQueues.Outbox` with `SafetyNet`, 15 s.
- Migration `AddOutboxProjection`: `Up`/`Down` empty (the table is `platform`'s); it exists to move the snapshot.

- [ ] **Step 1: Write the failing tests.**
  - Parity `TheProducerAndTheConsumerNameTheSameEvent` (both equal the literal).
  - Automations integration `DeletingAChatbotWithMediaWritesOneReleaseInTheSameTransaction` (`platform.outbox_messages` row, `payload->'fileIds'` has the draft's and a published version's id); `DeletingAChatbotWithoutMediaWritesNothing`; `ADeleteRefusedForLiveRunsWritesNothing`.
  - Storage integration `AReleasedChatbotFileIsUnpublishedAndSoftDeleted`; `DeletingOneOfTwoChatbotsSharingAFileKeepsIt` (references fake answers the id as referenced → file stays `Available`, inbox row written); `AFailingReferenceQueryLeavesTheMessageForTheNextTick`; `AFileOfAnotherOwnerIsNotTouched`.
- [ ] **Step 2: RED.** `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → `CS0117 StorageSubscribedEvents.ChatbotMediaReleased`.
- [ ] **Step 3: Implement**, then `dotnet ef migrations add AddOutboxProjection --project src/Modules/Automations/Modules.Automations.Infrastructure --context AutomationsDbContext -o Persistence/Migrations`; confirm empty `Up`/`Down` and no DDL on `platform`.
- [ ] **Step 4: GREEN.** Parity filter; process check; each integration name; `ArchitectureTests` filter `MultiReplicaInventoryTests|AutomationsLayerTests|StorageLayerTests`.
- [ ] **Step 5: Commit.** `feat(automations): release chatbot media when a chatbot is deleted`

**Block C end:** unit projects Storage, Automations, Bootstrapper, ArchitectureTests in full; integration classes `ChatbotMediaReferencesTests`, `ChatbotMediaReleaseTests`, `TemplateHeaderImageSweepTests`, `TemplateHeaderImageReleaseTests` once each. **Block C review** (one reviewer, Tasks 11-12).

---

## Block D — Instagram bot media (Task 13) — **gated: run only after the owner's go**

The spec (Q1) puts Instagram in scope, but the code does not let a chatbot reach Instagram: a chatbot cannot be attached to an Instagram connection (`Chatbots.cs:310`, `automations.chatbot.channel_not_supported`) and `BotOutbound` answers `NotAutomated` to a chatbot on Instagram (`BotOutbound.cs:91-93`); there is also no «Instagram button fallback» today. This block adds the send path so it is ready, and changes neither gate. Skipping it leaves Blocks A-C complete.

### Task 13: Instagram media in `BotOutbound`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BotOutbound.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BotOutboundTests.cs` (sends from a `BotRef.Agent`, the only bot the channel gate admits today)

**Interfaces:**
- Consumes: `BotSend.Media` (Task 9), `IInstagramClient.SendAttachmentAsync(sender, recipientId, attachmentType, Uri url, ct)`, `OutboundMediaPolicy.InstagramAttachmentType`, `OutboundMediaPolicy.MaxBytesOf(format, Instagram)`.
- Behaviour: a format Instagram does not accept, or over its cap → `Rejected(MessagingErrorCodes.MessageRejected)` before claiming. Otherwise one claimed message (the attachment, by the public URL, with `OutboundMedia` as in Task 9), then — each with its own derived id `BotFollowUpClientId.For(clientId, n)` (`n = 1, 2`; UUID v5 over the parent id, so a retry derives the same ids) — the caption as a text message, then the button as text `"{label}: {url}"`. Each goes through the normal claim/settle; the result is the first message's `Sent`, or the first failure.

- [ ] **Step 1: Write the failing tests:** `AnInstagramImageGoesAsAnAttachmentThenItsCaption`; `AnInstagramButtonBecomesATextAfterTheCaption`; `AnInstagramDocxIsRejectedBeforeAnyCall`; `RetryingAfterTheCaptionFailedDoesNotResendTheAttachment`.
- [ ] **Step 2: RED.** `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests` → `CS0103 BotFollowUpClientId`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN.** Process check; each name.
- [ ] **Step 5: Commit.** `feat(messaging): Instagram media for bot messages`

**Block D review** (one reviewer, risk lens). Then the **strong whole-branch review** (`develop...feature/chatbot-media-message`).

## Before merging to `develop`

- `dotnet build` the solution with 0 warnings; `dotnet format --verify-no-changes`.
- Touched integration projects in full, one at a time (Storage, Automations, Messaging), with the process check before each.
- Merge `--no-ff` into `develop`; on `develop`, update `docs/estado.md` (row + Registro) in a `docs:` commit. Deploy backend before frontend.

## Spec gaps resolved in this plan (for the owner)

1. **Storage could not hold this media.** The console policy has no video/audio, caps at 25 MB and publishes only images. Tasks 1-2 add owner-scoped rules for `ChatbotMedia` only; the console is unchanged.
2. **Media + URL button with empty text** cannot be sent (`cta_url` needs a body). `text.empty` still applies when a button is present (Task 6).
3. **Saved tenant templates** (`chatbot_templates`) also hold file ids; they count as in use (Task 11), otherwise a template would lose its file after the sweep.
4. **Inbox history after release:** a bot bubble points to the public URL; after the file is released (all chatbots stopped using it) the old bubble's image is gone. The plan follows the spec (messages do not keep files alive). Alternative: count `message_media.storage_file_id` as in use, which keeps a file public as long as any conversation shows it.
5. **A rejected upload** (content mismatch or antivirus) is `automations.media.unsupported_type`: the spec has no fifth code.
6. **Instagram** is not reachable by a chatbot today (Block D header).
7. **Memory:** Storage's completion reads the whole object (up to 100 MB for a document) to sniff and scan, as it does today up to 25 MB.

## Owner rulings after the plan (2026-11-18, binding — they override the sections above)

1. **Block D is not executed.** A chatbot cannot be attached to an Instagram connection today
   (`Chatbots.cs:310`) and `BotOutbound` refuses chatbot sends on Instagram (`BotOutbound.cs:91-93`).
   Instagram media for bots waits until chatbots support Instagram at all; it is a separate future spec.
   `BotOutbound` with `Media` on any channel other than WhatsApp stays `Rejected(MessageRejected)` (Task 9).
2. **Inbox history keeps its files.** Task 11's in-use check also counts `message_media.storage_file_id`
   (bot messages that reference a `ChatbotMedia` file) as referenced, reached through a Messaging port
   in the same Bootstrapper adapter. A file stays public while any chatbot, version, saved template **or
   conversation message** references it. Add the test `AFileShownInAConversationIsNotReleased` to Task 11
   and `DeletingTheChatbotKeepsAFileTheInboxShows` to Task 12.
3. **`kind` is lower case** everywhere (`image`, `video`, `audio`, `document`): flow, `complete`,
   `mediaFiles`, simulator transcript — as Task 5 already says.
