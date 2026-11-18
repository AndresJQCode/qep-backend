# WhatsApp Template Image Header Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Workspace override:** the tasks run in 3 blocks with one implementer per block (each task keeps its own commit), one review per block, and one strong whole-branch review at the end (workspace `CLAUDE.md`, «Presupuesto de pruebas»).

**Goal:** Create a free WhatsApp template with a fixed `IMAGE` header from QEP, store the image publicly, send it by link from chat, «Nueva conversación» and campaigns, and never leave an orphaned public image.

**Architecture:** Messaging declares a port (`IMessagingTemplateImageStore`) that a Bootstrapper adapter implements over Storage (`IStorageSystemUploads`, `FilePublication`, `IObjectStorage`), the same way `TenantLogoStorage` works. The template row gains the file id and the public URL. The catalog marks an image template sendable only if it has a stored URL, and the senders pass `TemplateHeader.ByLink("image", url, null)`. When a row is removed (delete or sync), Messaging writes `messaging.template.header-image-released.v1` to `platform.outbox_messages` in the same transaction. Storage consumes it like `catalog.product.images-released.v1`: it unpublishes, soft-deletes and retries until it succeeds.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-template-image-header-design.md` (pair: `qep-frontend/docs/superpowers/specs/2026-11-17-template-image-header-design.md`).

## Global Constraints

- Work in a worktree branched from `develop`. Merge into `develop` only, never into `main`. Use conventional commits with no AI attribution. Before every commit, check `git branch --show-current`.
- Image header formats: `image/jpeg` or `image/png`, `≤ 5 MB` (`5 * 1024 * 1024`). These are checked at upload-session time and again at create.
- `header` (text) and `headerImageFileId` are mutually exclusive. If both are sent, the response is 422 `validation.failed` with `errors.header`.
- File problems at create are 422 with a `messaging.template.header_image_*` code, plus `errors.headerImageFileId`. The frontend maps that key to the header field.
- The upload session is authorized by `messaging.template.manage` only, never by `storage.file.*`.
- Upload failures to Meta follow the order template: `messaging.template.upload_failed`, and `no_app_id` is logged and surfaces as `upload_failed` (`OrderTemplateProvisioning.cs:227-241`).
- `ManagedTemplateDto` and `MessageTemplateDto` gain `headerImageUrl: string | null`. `header` stays text-only.
- The external API is unchanged. It never falls back to the stored image.
- `Modules.Messaging.Application` does not reference Storage, EF Core or Npgsql (`MessagingLayerTests`). HTTP statuses are never built in a handler; mapping is central in `src/Api/ApiExceptionHandler.cs`.
- The migration is additive (two nullable columns) and touches Messaging only. Storage gains an enum value persisted by name in `varchar(20)` (`FileResourceEnums.cs:15-18`), so it needs no migration.
- **Tests:** do RED with `dotnet build` output when the failure is a compile error. Integration tests run only filtered by name (`--filter "FullyQualifiedName~<Name>"`). **A full integration run (class, project or suite) happens only when the owner asks, because other sessions share the machine.** Before any filtered integration run, check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. If one is running, wait; never stop it.
- Every command is PowerShell. If `Api.exe` is running, builds fail; stop only an `Api.exe` you started yourself.

## Review Focus

1. **A sync or delete that runs while the release fails.** If R2 is down when the outbox handler runs, the message must stay unprocessed (no inbox row) and be retried. Pinned in Task 9 by `ATransientUnpublishFailureLeavesTheMessageForTheNextTick`.
2. **A sync must not wipe the image columns.** `SyncFrom` rewrites components, and Meta's `header_handle` URL there is not ours. Pinned in Task 4 by `SyncFromKeepsTheHeaderImage`.
3. **Create fails after publish.** This covers a Meta 400, a failed upload to Meta, and a lost name race on `SaveChanges`. The image is released before the error leaves, and nothing stays published. Pinned in Task 6 by `AMetaRejectionReleasesThePublishedImage` and `AFailedMetaUploadReleasesThePublishedImage`.
4. **An image template from Meta's console (no stored URL)** stays `sendable=false` / `media_header` with `headerImageUrl: null`, and chat and campaigns still reject it with `not_available`. Pinned in Task 7 by `AnImageTemplateWithoutAStoredUrlStaysMediaHeader`.
5. **The external API with a stored-image template** still requires its own header (`header_mismatch` without one) and sends the caller's link, not ours. Pinned in Task 7 by `TheExternalApiIgnoresTheStoredImage`.

## HTTP contract (the frontend plan depends on it)

- `POST …/connections/{connectionId}/template-management/header-image/upload-session`, body `{ fileName, mimeType, sizeBytes }`.
  - `200` returns `{ fileId, uploadUrl, headers }`. This mirrors `MediaUploadSessionDto` (`CreateMediaUploadSession.cs:13`). `headers` holds exactly the signed PUT headers (`Content-Type`, `If-None-Match: *`), and the browser sends them unchanged.
  - `422 validation.failed` uses the `errors` keys `fileName`, `mimeType` and `sizeBytes`.
  - `422 messaging.template.header_image_unsupported` (not JPEG/PNG) or `messaging.template.header_image_too_large` (> 5 MB) carry no `errors` map; they are a form-level error.
  - `403` without `messaging.template.manage`.
- `POST …/template-management/templates` gains `headerImageFileId: Guid | null`.
  - `422 validation.failed` with `errors.header` when both `header` and `headerImageFileId` are sent.
  - `422` with `errors.headerImageFileId` for each of: `messaging.template.header_image_not_found`, `messaging.template.header_image_not_uploaded`, `messaging.template.header_image_rejected`, `messaging.template.header_image_unsupported`, `messaging.template.header_image_too_large`.
  - Upload or Meta failures stay as today: `messaging.template.upload_failed`, `messaging.template.rejected_by_meta`, and the rest.
- Responses: `ManagedTemplateDto.headerImageUrl` and the catalog's `MessageTemplateDto.headerImageUrl` (`string | null`).

---

## Worktree

```powershell
$repo = "c:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git -C $repo fetch origin
git -C $repo worktree add "$repo-worktrees\template-image-header" -b feature/template-image-header develop
Set-Location "$repo-worktrees\template-image-header"
git branch --show-current   # feature/template-image-header
```

## File map

| File | Responsibility |
| --- | --- |
| `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs` | `FileOwnerType.MessageTemplate = 9`. `SystemOwnedFiles` includes it (see open question 1). |
| `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs` | `[MessageTemplate] = TenantModuleKeys.Messaging` |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingTemplateImageStore.cs` (new) | Port and records |
| `src/Modules/Messaging/Modules.Messaging.Application/TemplateHeaderImages.cs` (new) | Policy (types, 5 MB), upload-session command, validator and handler |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingDomainException.cs` | `IHasFieldErrors` (copy of `CatalogDomainException`) |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | Five `TemplateHeaderImage*` codes |
| `src/Bootstrapper/MessagingTemplateImageStore.cs` (new) | Adapter over Storage, registered next to `TenantLogoStorage` (`QepServiceCollectionExtensions.cs:1705`) |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` | `HeaderImageFileId`, `HeaderImageUrl`, `AttachHeaderImage` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` | Column mapping (`:796-837`); outbox projection gains writable columns (`:892-901`) |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingInbox.cs` | `MessagingOutboxRecord` gains `CorrelationId`, `ProcessedAt`, `Attempts`, `LastError` |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<ts>_AddTemplateHeaderImage.cs` | Two nullable columns |
| `src/Modules/Messaging/Modules.Messaging.Application/TemplateComponentsBuilder.cs` | `TemplateMediaHeader(Format, Handle)` replaces `string? documentHeaderHandle` |
| `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs` | Create (`:169-342`), delete (`:347-386`), DTO mapping (`:31-57`) |
| `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs` | Upload-session route; `CreateTemplateRequest.HeaderImageFileId` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs` | `MessageTemplateDto.HeaderImageUrl`, `FromStored(..., headerImageUrl)`, `StoredHeaderOf` |
| `src/Modules/Messaging/Modules.Messaging.Application/TemplateMirrorSync.cs` | Catalog passes the URL (`:164`); sync removal publishes the release (`:92`) |
| `src/Modules/Messaging/Modules.Messaging.Application/TemplateOutbound.cs`, `CampaignDispatch.cs` | Pass the stored header (`:54`, `:276`) |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingIntegrationEvents.cs` (new) + `ITemplateHeaderImageReleases` | Producer side of the event |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/TemplateHeaderImageReleasePublisher.cs` (new) | Writes the outbox row on `MessagingDbContext` |
| `src/Modules/Storage/Modules.Storage.Application/StorageSubscribedEvents.cs` | Consumer copy of the event name |
| `src/Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageReleaseProcessor.cs`, `…Worker.cs` (new) | Unpublish and soft-delete with an inbox |
| `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` | Entry for the new worker |

---

## Block A — Storage owner, port, adapter, upload session (Tasks 1-3)

### Task 1: `FileOwnerType.MessageTemplate`

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs:19-63`
- Modify: `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs:18-31`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs`

**Interfaces:**
- Produces: `FileOwnerType.MessageTemplate` (value 9, a name of at most 20 characters). `FileOwnerModules.ModuleOf(MessageTemplate) == TenantModuleKeys.Messaging`. `SystemOwnedFiles.Includes(MessageTemplate) == true`.

- [ ] **Step 1: Write the failing tests** in `FileOwnerModulesTests`:
  - `AMessageTemplateFileBelongsToMessaging`: `Assert.Equal(TenantModuleKeys.Messaging, FileOwnerModules.ModuleOf(FileOwnerType.MessageTemplate))`.
  - `AMessageTemplateFileIsSystemOwned`: `Assert.True(SystemOwnedFiles.Includes(FileOwnerType.MessageTemplate))`.
  - `TheOwnerTypeNameFitsItsColumn`: every `Enum.GetNames<FileOwnerType>()` has length ≤ 20.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Storage/Modules.Storage.UnitTests`. Expected: `CS0117 'FileOwnerType' does not contain a definition for 'MessageTemplate'`.
- [ ] **Step 3: Implement.** Add the enum value with a comment that follows the existing ones: OwnerId is the tenantId, the file is uploaded only through Messaging's session, and it is persisted by name. Add the map entry and add the value to `SystemOwnedFiles.OwnerTypes`.
- [ ] **Step 4: GREEN.** Run `dotnet test tests/Modules/Storage/Modules.Storage.UnitTests --filter "FullyQualifiedName~FileOwnerModulesTests|FullyQualifiedName~FileOwnerFilterTests"`. Expected: pass. `FileOwnerFilterTests` checks that the listing still excludes system owners.
- [ ] **Step 5: Commit.** `feat(storage): add the MessageTemplate file owner`

### Task 2: Messaging port, error codes and the upload session

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingTemplateImageStore.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateHeaderImages.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingDomainException.cs`, `MessagingErrorCodes.cs` (after `TemplateUploadFailed`, `:155`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs` (route in `MapTemplateManagementEndpoints`, request record at the end)
- Modify: Messaging Application DI registration (where `CreateMediaUploadSessionValidator` is registered)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateHeaderImageSessionTests.cs` (new), `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateHeaderImageApiTests.cs` (new), `MessagingApiHarness.cs` (fake registration)

**Interfaces:**
- Produces (Application):
  ```csharp
  public interface IMessagingTemplateImageStore
  {
      Task<TemplateImageUploadSession> OpenUploadAsync(Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken);
      /// Completes the upload (idempotent) and reports the file; NotFound for another tenant/owner or an unknown id.
      Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
      Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
      /// Publishes to the public bucket and returns the absolute public URL.
      Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
      /// Unpublishes and soft-deletes; idempotent (missing, deleted or foreign file is a no-op).
      Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
  }
  public sealed record TemplateImageUploadSession(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers);
  public enum TemplateImageState { Available, NotFound, NotUploaded, Rejected }
  public sealed record TemplateImageFile(TemplateImageState State, string? MimeType, long SizeBytes);
  public static class TemplateHeaderImagePolicy
  {
      public const long MaxSizeBytes = 5 * 1024 * 1024;
      public static bool IsAllowedMimeType(string? mimeType); // image/jpeg | image/png, ordinal-ignore-case, trimmed
  }
  public sealed record CreateTemplateHeaderImageSessionCommand(Guid TenantId, Guid ConnectionId, string? FileName, string? MimeType, long SizeBytes)
      : ICommand<TemplateHeaderImageSessionDto>;
  /// BFF: headers are exactly the signed PUT headers (Content-Type, If-None-Match), as MediaUploadSessionDto.
  public sealed record TemplateHeaderImageSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);
  ```
- Produces (Domain): `MessagingDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors)` implementing `IHasFieldErrors`. Keep the existing `(code, message, Exception)` overload, and call the new one with a named `fieldErrors:` argument so a `null` is never ambiguous. New codes:
  - `TemplateHeaderImageUnsupported = "messaging.template.header_image_unsupported"`
  - `TemplateHeaderImageTooLarge = "messaging.template.header_image_too_large"`
  - `TemplateHeaderImageNotFound = "messaging.template.header_image_not_found"`
  - `TemplateHeaderImageNotUploaded = "messaging.template.header_image_not_uploaded"`
  - `TemplateHeaderImageRejected = "messaging.template.header_image_rejected"`
- Route: `POST /connections/{connectionId:guid}/template-management/header-image/upload-session`, with `.RequireAuthorization(MessagingPermissions.TemplateManage)`. It returns `200` with the DTO. Body: `CreateTemplateHeaderImageSessionRequest(string? FileName, string? MimeType, long SizeBytes)`.

- [ ] **Step 1: Write the failing unit tests** (the fake port records calls):
  - `ASessionForAPngUnderFiveMegabytesOpensAnUpload`: the handler returns the fake's `FileId`/`UploadUrl`/`Headers`, and the fake got `(tenantId, "banner.png", "image/png", 1024)`.
  - `AGifIsUnsupportedBeforeAnyUpload`: `messaging.template.header_image_unsupported`, and the fake was never called.
  - `MoreThanFiveMegabytesIsTooLarge`: `SizeBytes = 5 * 1024 * 1024 + 1` gives `messaging.template.header_image_too_large`, with no call.
  - `AnEmptyFileNameFailsValidation`: `ValidationException` with key `fileName`.
  - `WithoutTemplateManageItIsForbidden`: the `MessagingAuthorization` 403, with no call.
  - `AFieldErrorTravelsInTheException`: `new MessagingDomainException("c", "m", fieldErrors: new Dictionary<string,string[]>{["x"]=["y"]}).FieldErrors["x"]` equals `["y"]`.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS0246` for `IMessagingTemplateImageStore` / `CreateTemplateHeaderImageSessionCommand`.
- [ ] **Step 3: Implement** the port, policy, codes, exception overload, validator (`fileName` not empty and ≤ 256, `mimeType` not empty, `sizeBytes > 0`, with `OverridePropertyName` as in `CreateMediaUploadSessionValidator`), handler, endpoint and request record. The handler order is: `MessagingAuthorization.EnsureAsync(..., TemplateManage)`, then the validator, then the policy (domain codes, no field map, same as `CreateMediaUploadSessionHandler.cs:69-81`), then the port.
- [ ] **Step 4: Add the integration fake.** In `MessagingApiHarness.QepApiFactory`, add `public FakeTemplateImageStore TemplateImages { get; } = new();` and register it inside `ConfigureTestServices` with `RemoveAll<IMessagingTemplateImageStore>()` + `AddSingleton<IMessagingTemplateImageStore>(TemplateImages)`. The fake keeps files in a dictionary (`State`, `MimeType`, `Size`, `Bytes`, `Published`, `Released`) and returns `https://public.test/<fileId>.png` from `PublishAsync`. It can be told to throw from `PublishAsync`/`ReleaseAsync`.
- [ ] **Step 5: Write the integration test** `TemplateHeaderImageApiTests.TheSessionIsOpenWithTemplateManageOnly`. A client with only `messaging.template.manage` (no `storage.file.upload`) gets `200` with `fileId`, `uploadUrl` and `headers`. A client without it gets `403`.
- [ ] **Step 6: GREEN.** Run `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~TemplateHeaderImageSessionTests"`. Then run `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` and wait while either runs. Then run `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TheSessionIsOpenWithTemplateManageOnly"`. Expected: pass.
- [ ] **Step 7: Commit.** `feat(messaging): upload session for a template header image`

### Task 3: Bootstrapper adapter `MessagingTemplateImageStore`

**Files:**
- Create: `src/Bootstrapper/MessagingTemplateImageStore.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (next to `:1705`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTemplateImageStoreTests.cs` (new, with `StorageTestDoubles.cs`)

**Interfaces:**
- Consumes: `IStorageSystemUploads.OpenAsync/CompleteAsync/FindAvailableAsync` (`IStorageSystemUploads.cs:17-34`), `IFileResourceRepository`, `IObjectStorage.DownloadAsync` (`IObjectStorage.cs:47`), `FilePublication.PublishAsync/UnpublishAsync` (`FilePublication.cs:20,61`), `IPublicObjectStorage.GetUrl` (`:23`), `IStorageAuditPublisher`, `IStorageUnitOfWork`, `IExecutionContext`, `IClock`.
- Produces: `internal sealed class MessagingTemplateImageStore : IMessagingTemplateImageStore`. In every call the owner is `(FileOwnerType.MessageTemplate, ownerId: tenantId)`.

- [ ] **Step 1: Write the failing tests**, modeled on `TenantLogoStorageTests`:
  - `OpenCreatesAMessageTemplateFileCappedAtFiveMegabytes`: the system uploads double got `ownerType MessageTemplate`, `ownerId == tenantId`, `maxSizeBytes == TemplateHeaderImagePolicy.MaxSizeBytes`.
  - `CompleteMapsEachOutcome`: `Available → Available` (with mime and size from `FindAvailableAsync`), `NotFound → NotFound`, `ObjectMissing → NotUploaded`, `Rejected → Rejected`.
  - `AFileOfAnotherTenantIsNotFound`.
  - `PublishReturnsThePublicUrlAndAuditsAsTheCaller`: the URL equals `GetUrl(publicKey)`, `storage.file.published` is audited with `executionContext.SubjectId`, and the Storage unit of work saved once.
  - `ReleaseUnpublishesAndSoftDeletes`: the status becomes `Deleted`, and `UnpublishAsync` was called.
  - `ReleaseIsANoOpForADeletedOrForeignFile`.
- [ ] **Step 2: RED.** Run `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Expected: `CS0246 MessagingTemplateImageStore`.
- [ ] **Step 3: Implement** following `TenantLogoStorage.cs:40-130`: load by id, check that tenant, owner type and ownerId match, publish or unpublish, `SoftDelete`, audit, then save. `ReadAsync` uses `resource.StorageKey` with `DownloadAsync`. Register it as scoped.
- [ ] **Step 4: GREEN.** Run `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingTemplateImageStoreTests"`.
- [ ] **Step 5: Commit.** `feat(bootstrapper): Storage adapter for template header images`

**Block A review** (one reviewer, diff of Tasks 1-3).

---

## Block B — Row, payload, create, catalog and sends (Tasks 4-7)

### Task 4: Template row stores the image; the sync keeps it

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs:146-260`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:814-819`
- Create: migration `AddTemplateHeaderImage`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageTemplatePersistenceTests.cs`

**Interfaces:**
- Produces: `Guid? HeaderImageFileId`, `string? HeaderImageUrl` (private set), `public const int HeaderImageUrlMaxLength = 2048`, and `public void AttachHeaderImage(Guid fileId, string publicUrl, DateTimeOffset now)`. It throws `ArgumentException` for an empty id, a blank URL or a URL that is too long, and also when one is already attached. Columns: `header_image_file_id uuid NULL`, `header_image_url varchar(2048) NULL`.

- [ ] **Step 1: Write the failing tests.**
  - `AttachHeaderImageStoresIdAndUrl`.
  - `SyncFromKeepsTheHeaderImage`: attach, then `SyncFrom("APPROVED", null, "MARKETING", "NAMED", "[]", now)`. Both fields are unchanged.
  - `ApplyStatusKeepsTheHeaderImage`.
  - Integration `TheHeaderImageColumnsRoundTrip`: save a row with an image, read it back through a new context, and get equal values.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS1061 'MessageTemplate' does not contain a definition for 'AttachHeaderImage'`.
- [ ] **Step 3: Implement** the properties and method. `SyncFrom`/`ApplyStatus` stay untouched, and a comment says why: the image is QEP's data, not Meta's. Map the columns, then run:
  `dotnet ef migrations add AddTemplateHeaderImage --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`. Check that `Up` only adds the two nullable columns. The outbox projection change in Task 8 also lands in the snapshot; regenerate the migration there if Task 8 changes the model.
- [ ] **Step 4: GREEN.** Run the unit tests filtered by `MessageTemplateTests`. Then do the process check and run the integration test filtered by `TheHeaderImageColumnsRoundTrip`.
- [ ] **Step 5: Commit.** `feat(messaging): store a template's header image on its row`

### Task 5: `TemplateComponentsBuilder` media header with a format

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateComponentsBuilder.cs:36-65`
- Modify the callers: `QuotationTemplateProvisioning.cs:77,280`, `OrderTemplateProvisioning.cs:67,202`, `SupportFormalReplyTemplateProvisioning.cs:75,199`; tests `TemplateComponentsBuilderTests.cs:85`, `SupportReplyTemplateProvisioningApiTests.cs:115`, `QuotationTemplateOutboundTests.cs:87`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateComponentsBuilderTests.cs`

**Interfaces:**
- Produces: `public sealed record TemplateMediaHeader(string Format, string Handle) { public static TemplateMediaHeader Document(string handle); public static TemplateMediaHeader Image(string handle); }`. `CreatePayload(TemplateDraft draft, TemplateMediaHeader? mediaHeader = null)` and `Components(TemplateDraft draft, TemplateMediaHeader? mediaHeader = null)` emit `{type: HEADER, format: <Format>, example: {header_handle: [handle]}}`. A media header wins over `draft.Header`.

- [ ] **Step 1: Write the failing tests.**
  - `AnImageHeaderCarriesFormatImageAndTheHandle`: `Components(Draft(header: null), TemplateMediaHeader.Image("4::img"))[0]` has `format == "IMAGE"` and `example.header_handle[0] == "4::img"`.
  - Update the existing document test (`:85`) to `TemplateMediaHeader.Document("4::handle")` and keep `format == "DOCUMENT"`.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS0246 TemplateMediaHeader`.
- [ ] **Step 3: Implement** the change and update the 6 production call sites to `TemplateMediaHeader.Document(handle)`.
- [ ] **Step 4: GREEN.** Run the unit tests filtered by `TemplateComponentsBuilderTests|SystemTemplateSendTests`, then `dotnet build` the solution.
- [ ] **Step 5: Commit.** `refactor(messaging): template media header carries its format`

### Task 6: Create with `headerImageFileId`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs:149-342` (messages, command, validator, handler, `ManagedTemplateMapping`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementDtos.cs:10-31`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs:142-151,224-232`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateManagementCreateApiTests.cs`, unit validator tests in `TemplateHeaderImageSessionTests.cs` (or a new `CreateTemplateValidatorTests` in the same project)

**Interfaces:**
- Consumes: the Task 2 port and policy, `AttachHeaderImage` (Task 4), `TemplateMediaHeader.Image` (Task 5), `IWhatsAppCloudClient.UploadTemplateExampleAsync(source, ReadOnlyMemory<byte>, mime, fileName, ct)` (`IWhatsAppCloudClient.cs:118`).
- Produces: `CreateTemplateCommand(..., IReadOnlyDictionary<string,string?>? Examples, Guid? HeaderImageFileId = null)`, `CreateTemplateRequest(..., Guid? HeaderImageFileId)`, and `ManagedTemplateDto(..., string? RequestedCategory = null, string? HeaderImageUrl = null)`, filled from `template.HeaderImageUrl`. New `TemplateMessages.HeaderExclusive = "Elige un encabezado de texto o una imagen, no los dos."` and `TemplateMessages.HeaderImage = "Sube una imagen JPG o PNG de hasta 5 MB."`

**Handler order** (the validator runs before any side effect):
1. Authorization, validator (exclusive rule on `header`), connection, name check in the mirror. None of these touch the file.
2. If `HeaderImageFileId` is set: `CompleteAsync`. `NotFound → header_image_not_found`, `NotUploaded → header_image_not_uploaded`, `Rejected → header_image_rejected`; a type or size outside the policy maps to `header_image_unsupported`/`header_image_too_large`. Each carries the field error `headerImageFileId: [TemplateMessages.HeaderImage]`.
3. `PublishAsync` gives `url`. From here everything runs in `try { … } catch when not host-cancel { await ReleaseOrDeferAsync(...); throw; }`:
   `ReadAsync` → `UploadTemplateExampleAsync`. A failure is logged like `OrderTemplateProvisioning.LogUploadFailed` and becomes `upload_failed`. Then `CreateTemplateAsync(CreatePayload(draft, TemplateMediaHeader.Image(handle)))` → `Pending(...)` with `Components(draft, Image(handle))` → `AttachHeaderImage(fileId, url, now)` → audit → `SaveChangesAsync`.
4. `ReleaseOrDeferAsync` calls `ReleaseAsync(..., CancellationToken.None)`. If that throws, it logs and writes the release event (Task 8 port) on a clean save, so the outbox retries it (see open question 3). In Block B, before Task 8 exists, only the synchronous release is wired; Task 8 adds the fallback.

- [ ] **Step 1: Write the failing tests** (validator unit tests first, then integration):
  - `HeaderAndHeaderImageTogetherFailOnHeader`: `ValidationException` with key `header` and message `HeaderExclusive`.
  - Integration `CreatingWithAnImagePostsAnImageHeaderAndStoresTheUrl`: the fake has an Available `image/png` of 2 KB. Expect `201`, `headerImageUrl == "https://public.test/<id>.png"`, `header == null`, the Meta payload's first component `format == "IMAGE"` with `example.header_handle[0]` equal to the upload response's `h`, one upload POST to `/{MetaAppId}/uploads`, and the DB row's `header_image_file_id`/`header_image_url` set.
  - `AnImageOfAnotherTenantIsHeaderImageNotFoundOnTheField`: `422`, `code == messaging.template.header_image_not_found`, `errors.headerImageFileId` present, no Meta call, nothing published.
  - `AnImageNotYetUploadedIsNotUploaded`, `AQuarantinedImageIsRejected`, `AGifIsUnsupported`, `ASixMegabyteImageIsTooLarge`: each code, `errors.headerImageFileId`, and no publish.
  - `AMetaRejectionReleasesThePublishedImage`: Meta `POST message_templates` returns 400. Expect `422 rejected_by_meta`, the fake shows `Published` then `Released`, and no row.
  - `AFailedMetaUploadReleasesThePublishedImage`: the uploads endpoint returns 500. Expect `422 messaging.template.upload_failed`, `Released`, and no create call.
  - `ATextHeaderStillWorksWithoutAnImage`: the existing `CreatingPostsToMetaStoresAPendingFreeTemplateAndAudits` stays green, and `headerImageUrl` is `null`.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS1739` (no `HeaderImageFileId` parameter).
- [ ] **Step 3: Implement** per the order above. Keep the endpoint's `CreateTemplateCommand` construction in sync.
- [ ] **Step 4: GREEN.** Run the unit tests filtered by `HeaderAndHeaderImageTogetherFailOnHeader`. Then do the process check and run the integration tests, one filter per name while iterating, for example `--filter "FullyQualifiedName~CreatingWithAnImagePostsAnImageHeaderAndStoresTheUrl"`. Finally run one filter that groups this task's names: `--filter "FullyQualifiedName~TemplateManagementCreateApiTests.CreatingWithAnImage|FullyQualifiedName~ReleasesThePublishedImage|FullyQualifiedName~HeaderImage"`.
- [ ] **Step 5: Commit.** `feat(messaging): create a template with a fixed image header`

### Task 7: Catalog sendable with a stored image; chat, new conversation and campaigns send it

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs:21-34,290-313`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateMirrorSync.cs:164`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateOutbound.cs:54`, `CampaignDispatch.cs:276`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogFromStoredTests.cs`, `CampaignDispatcherTests.cs`, `SendTemplateMessageHandlerTests.cs`; integration `SendTemplateApiTests.cs`, `CampaignDispatchTests.cs`

**Interfaces:**
- Produces: `MessageTemplateDto(..., bool SendableWithHeaderMedia = false, string? HeaderImageUrl = null)`, and `TemplateCatalog.FromStored(string name, string language, string category, string parameterFormat, string componentsJson, string? headerImageUrl = null)`. Rule: if `headerImageUrl` is not null, `HeaderMedia == Image` and `SendableWithHeaderMedia`, the result is `with { Sendable = true, UnsupportedReason = null, HeaderImageUrl = url }`. Otherwise it is unchanged, with `HeaderImageUrl = null`. Also `public static TemplateHeader? StoredHeaderOf(MessageTemplateDto template)`, which returns `ByLink("image", url, null)` when `HeaderImageUrl` is set.
- `MirroredTemplateCatalog` passes `row.HeaderImageUrl`. The senders call `BuildSend(template, values, TemplateCatalog.StoredHeaderOf(template))`. `SendExternalTemplate` is **not** touched.

- [ ] **Step 1: Write the failing tests.**
  - `AnImageTemplateWithAStoredUrlIsSendable`: components with an `IMAGE` header plus a body. Expect `Sendable`, `UnsupportedReason == null`, `HeaderImageUrl == url`, `HeaderMedia == "Image"`.
  - `AnImageTemplateWithoutAStoredUrlStaysMediaHeader`: `Sendable == false`, `UnsupportedReason == "media_header"`, `HeaderImageUrl == null`.
  - `AStoredUrlDoesNotUnlockAVideoHeaderOrADynamicUrlButton`: a `VIDEO` header, or `IMAGE` plus a dynamic URL button, stays not sendable.
  - `StoredHeaderOfIsAnImageLink`.
  - Integration `SendingAnImageTemplateFromTheChatSendsTheStoredLink`: seed a row with an image URL. The Meta send body has `components[0] == {type: header, parameters: [{type: image, image: {link: url}}]}`, and the message's `details.template.header.link == url`.
  - Integration `ACampaignOfAnImageTemplateSendsTheStoredLink` (same assertion on the worker's call).
  - Integration `TheExternalApiIgnoresTheStoredImage`: without a header it gets `422 messaging.template.header_mismatch`. With `header.link = X` the Meta body carries `X`, not the stored URL.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS1739` (no `headerImageUrl` parameter) / `CS0117 StoredHeaderOf`.
- [ ] **Step 3: Implement** the change. The comment on `HeaderImageUrl` (BFF) says why: the composer previews the image, and the frontend never sends it.
- [ ] **Step 4: GREEN.** Run the unit tests filtered by `TemplateCatalogFromStoredTests|TemplateCatalogExternalTests|CampaignDispatcherTests|SendTemplateMessageHandlerTests|StartConversationHandlerTests`. Then do the process check and run each new integration test by its name.
- [ ] **Step 5: Commit.** `feat(messaging): send image templates with their stored header`

**Block B review** (one reviewer, diff of Tasks 4-7).

---

## Block C — No orphaned images (Tasks 8-9)

### Task 8: Release event in the same transaction as the row removal

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingIntegrationEvents.cs` (`public const string TemplateHeaderImageReleased = "messaging.template.header-image-released.v1";`)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ITemplateHeaderImageReleases.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/TemplateHeaderImageReleasePublisher.cs`
- Modify: `MessagingInbox.cs:22-31`, `MessagingDbContext.cs:892-901` (writable outbox columns), Infrastructure DI
- Modify: `TemplateManagement.cs:381` (delete), `TemplateMirrorSync.cs:92` (sync removal), and Task 6's `ReleaseOrDeferAsync`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateMirrorSyncTests.cs`; integration `TemplateManagementDeleteApiTests.cs`, `TemplateManagementApiTests.cs` (sync)

**Interfaces:**
- Produces: `public interface ITemplateHeaderImageReleases { void Publish(Guid tenantId, Guid fileId, DateTimeOffset occurredAt); }`. The Infrastructure implementation adds a `MessagingOutboxRecord` with `Id = Guid.CreateVersion7()`, `EventName`, a payload of camelCase `{ "tenantId", "fileId" }`, `CorrelationId = Guid.NewGuid().ToString()`, `OccurredAt` and `Attempts = 0`. It does not save; the caller's `SaveChangesAsync` commits it with the row removal. This follows `ProductImageReleasePublisher.cs:11-27`.
- `MessagingOutboxRecord` gains `CorrelationId` (`correlation_id`, max 100), `ProcessedAt`, `Attempts` and `LastError`. `platform.outbox_messages` has `correlation_id` and `attempts` as NOT NULL (`InitialPlatform.cs:54,57`). The table stays `ExcludeFromMigrations`. If the snapshot changes, regenerate Task 4's migration so there is a single one, and check that its `Up` has no DDL on `platform`.

- [ ] **Step 1: Write the failing tests.**
  - Unit `ASyncThatRemovesAnImageTemplatePublishesItsRelease`: the fake `ITemplateHeaderImageReleases` gets `(tenantId, fileId)` once. A removed row without an image publishes nothing, and an incomplete listing publishes nothing.
  - Integration `DeletingAnImageTemplateWritesTheReleaseInTheSameTransaction`: after `204`, `platform.outbox_messages` has one `messaging.template.header-image-released.v1` whose `payload->>'fileId'` is the file. The image is not yet released by the request (the fake's `Released` is false).
  - Integration `AFailedDeleteWritesNoRelease`: Meta's delete returns 500. Expect `422` and zero release rows.
  - Integration `ASyncThatDropsAnImageTemplateWritesTheRelease`.
  - Integration `AFailedSynchronousReleaseFallsBackToTheOutbox`: Meta create gets 400 and the fake's `ReleaseAsync` throws. Expect `422 rejected_by_meta` and one release row.
- [ ] **Step 2: RED.** Run `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`. Expected: `CS0246 ITemplateHeaderImageReleases`.
- [ ] **Step 3: Implement** the change. Inject the port into `DeleteTemplateHandler`, `TemplateMirrorSync` and `CreateTemplateHandler`. Publish only if `template.HeaderImageFileId is { } fileId`.
- [ ] **Step 4: GREEN.** Run the unit tests filtered by `TemplateMirrorSyncTests`. Then do the process check and run each new integration test by its name.
- [ ] **Step 5: Commit.** `feat(messaging): release a template header image through the outbox`

### Task 9: Storage consumer unpublishes and soft-deletes, with retries

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Application/StorageSubscribedEvents.cs` (`TemplateHeaderImageReleased = "messaging.template.header-image-released.v1"`)
- Create: `src/Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageReleaseProcessor.cs`, `TemplateHeaderImageReleaseWorker.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:75-76`
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entry next to `:153`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/TemplateHeaderImageReleaseEventParityTests.cs` (new, a copy of `ProductImageReleaseEventParityTests`); `tests/Modules/Storage/Modules.Storage.IntegrationTests/TemplateHeaderImageReleaseTests.cs` (new, on `PaymentProofStorageHarness`; remove the new worker there as `:506` does)

**Interfaces:**
- Produces: `internal interface ITemplateHeaderImageReleaseProcessor { Task<int> ProcessPendingAsync(CancellationToken cancellationToken); }`, with `Consumer = "storage.template-header-image-release"`. The skeleton copies `ProductImageReleaseProcessor.cs:85-240` without probes. Per message it loads the file by `(id, tenantId)`. It skips (and marks the inbox) a missing file, `OwnerType != MessageTemplate` or `Deleted/Purged`. Otherwise it runs `filePublication.UnpublishAsync`, `resource.SoftDelete(now)`, and the system audit `storage.file.deleted` with reason `template_header_image_released`, then adds the inbox row and saves. A malformed payload is logged and marked. A transient exception clears the tracker, adds no inbox row, and the message comes back on the next tick.
- Worker: `DoorbellPolling.RunAsync` on `DoorbellQueues.Outbox` with `DoorbellQueues.SafetyNet(DoorbellQueues.Outbox)` (backend `CLAUDE.md`; shape of `CitySynonymEventsWorker.cs:19-46`), interval 15 s.
- Inventory entry: `WorkerClassification.Noisy`, evidence `"Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageReleaseProcessor.cs:<line> — anti-join contra el inbox sin reclamar; el que pierde choca con la clave del inbox"`.

- [ ] **Step 1: Write the failing tests.**
  - Parity `TheProducerAndTheConsumerNameTheSameEvent`: `Assert.Equal(MessagingIntegrationEvents.TemplateHeaderImageReleased, StorageSubscribedEvents.TemplateHeaderImageReleased)`, and both equal the literal `"messaging.template.header-image-released.v1"`.
  - Integration `AReleasedImageIsUnpublishedAndSoftDeleted`: seed a published Available `MessageTemplate` file and an outbox row. Expect status `Deleted`, the public objects gone, and an inbox row for the consumer.
  - `AFileOfAnotherOwnerTypeIsNotTouched` (a `Product` file stays `Available`).
  - `ATransientUnpublishFailureLeavesTheMessageForTheNextTick`: the public storage double throws once. The first pass has no inbox row and the file is `Available`; the second pass gives `Deleted` and an inbox row.
  - `AReleaseOfAnAlreadyDeletedFileIsMarkedProcessed`.
- [ ] **Step 2: RED.** Run `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` and `dotnet build tests/Modules/Storage/Modules.Storage.IntegrationTests`. Expected: `CS0117 StorageSubscribedEvents.TemplateHeaderImageReleased` / `CS0246 TemplateHeaderImageReleaseProcessor`.
- [ ] **Step 3: Implement** the processor, worker, registration and inventory entry, all in this commit (backend `CLAUDE.md`, MultiReplicaInventory).
- [ ] **Step 4: GREEN.** Run `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~TemplateHeaderImageReleaseEventParityTests"`. Then do the process check and run the Storage integration tests filtered by `TemplateHeaderImageReleaseTests`, one name at a time while iterating. Then run `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests|FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~StorageLayerTests|FullyQualifiedName~CompositionRootTests"`.
- [ ] **Step 5: Commit.** `feat(storage): unpublish template header images released by Messaging`

**Block C review** (one reviewer, diff of Tasks 8-9). Then the **strong whole-branch review** (most capable model, `develop...feature/template-image-header`).

## Before merging to `develop`

- `dotnet build` the solution with 0 warnings.
- The full integration run of Messaging and Storage happens **only when the owner asks** (other sessions share the machine). Until then the evidence is the name-filtered runs above.
- Merge into `develop` with `--no-ff`. Then, on `develop`, update `docs/estado.md` (a row plus the Registro) in a `docs:` commit. Deploy order: backend before frontend; the migration is additive.

## Open questions for the owner

1. **Is `MessageTemplate` a system-owned file?** The spec only says "mapped to Messaging". If it is not in `SystemOwnedFiles` (`FileResourceEnums.cs:57-63`), a member with `storage.file.delete`/`publish` can delete or unpublish a live header image from the Storage console, and the template stays sendable but fails at Meta (spec anti-goal). The plan includes it (Task 1). If the owner says no, drop that line and `AMessageTemplateFileIsSystemOwned`.
2. **This is the first time Messaging writes to `platform.outbox_messages`.** `MessagingOutboxRecord` is a read-only projection without `correlation_id`/`attempts` (`MessagingInbox.cs:22-31`, `MessagingDbContext.cs:892-901`), and both columns are NOT NULL (`InitialPlatform.cs:54,57`). Task 8 widens the projection. The alternative is a second write entity like `CatalogOutboxMessage`.
3. **A failed synchronous release when create fails.** The spec says the image is deleted "before returning the error". If `ReleaseAsync` itself throws (R2 down), Task 8 falls back to the release event so the outbox retries it. Confirm the fallback is acceptable instead of failing harder.
4. **Upload-session response shape.** The spec names `{ fileId, uploadUrl }`. The plan adds `headers` (`Content-Type`, `If-None-Match: *`), because R2 signs both (`StorageSystemUploads.cs:58-67`). `tenant-logo-upload.ts` in the frontend already sends exactly those two headers.
