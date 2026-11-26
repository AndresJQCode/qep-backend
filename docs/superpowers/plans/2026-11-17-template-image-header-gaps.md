# Template Image Header — Residual Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Workspace override: one block, one implementer, one review, one strong whole-branch review.

**Goal:** No `MessageTemplate` image can stay public without a template row, whatever fails.

**Architecture:** `FilePublication` gains a removal that works without a stored public key. A daily Storage sweep asks Messaging (Bootstrapper adapter) which file ids are referenced and releases the rest.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-11-17-template-image-header-design.md`, section «Closing the residual gaps».

## Global Constraints

- Worktree `qep-backend-worktrees/template-image-gaps`, branch `fix/template-image-gaps` from `develop`. Merge into `develop` only. Conventional commits, no AI attribution. Check `git branch --show-current` before every commit.
- **Owner test rule:** zero integration tests run (no Testcontainers, not even filtered), zero suites. Only `dotnet build` and unit tests filtered by name (ArchitectureTests filtered, only if they use no containers). Integration tests are written and compiled; the report lists their filtered commands.
- Storage.Application never references Messaging; Messaging.Application never references Storage. The cross-module question goes through a port declared in Storage.Application and implemented in `src/Bootstrapper/`.
- Grace period: files whose `CreatedAt` is older than **24 h**. Sweep interval: once every **24 h** (first run after a short startup delay). Batch size: **200** files per pass.
- Every new worker gets its `MultiReplicaInventory` entry in the same commit.

## Review Focus

1. A file a live template references is never released by the sweep (pinned by `ASweepKeepsAReferencedImage`).
2. A file younger than 24 h is never touched (`ASweepIgnoresFilesInsideTheGrace`).
3. Removal without a stored key also deletes variants and is idempotent when the objects do not exist (`RemovingWithoutAStoredKeyDeletesTheComputedKeyAndVariants`).
4. Two replicas sweeping at once do not double-audit or fail (idempotent soft-delete; classify in the inventory).
5. Another tenant's references never protect or release a file (the port is queried per tenant).

---

## Block A (Tasks 1-2)

### Task 1: remove public copies without a stored key

**Files:** `src/Modules/Storage/Modules.Storage.Application/FilePublication.cs`; callers `src/Bootstrapper/MessagingTemplateImageStore.cs` (`ReleaseAsync`) and `src/Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageReleaseProcessor.cs`; tests in `tests/Modules/Storage/Modules.Storage.UnitTests` and `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTemplateImageStoreTests.cs`.

**Interfaces:** `public Task RemovePublicCopiesAsync(FileResource resource, CancellationToken cancellationToken)` on `FilePublication`: uses `resource.PublicStorageKey ?? StorageKey.PublicFor(resource.TenantId, resource.Id, resource.Name)`, deletes that key and every `StorageKey.PublicVariantFor(key, variant)`, then clears the publication on the resource (same state change as `UnpublishAsync`). `UnpublishAsync` keeps its behavior for every other caller.

- [ ] Failing tests: `RemovingWithoutAStoredKeyDeletesTheComputedKeyAndVariants`, `RemovingWithAStoredKeyDeletesThatKey`, `RemovingIsIdempotentWhenTheObjectsAreGone`; adapter `ReleaseRemovesPublicCopiesWithoutAStoredKey`.
- [ ] RED (`dotnet build` / filtered unit run), implement, GREEN, switch the two template callers to `RemovePublicCopiesAsync`. The processor's existing integration tests must still compile.
- [ ] Commit `fix(storage): remove a template image's public copies without a stored key`.

### Task 2: daily sweep of unreferenced template images

**Files:** port `src/Modules/Storage/Modules.Storage.Application/ITemplateImageReferences.cs`; adapter `src/Bootstrapper/TemplateImageReferences.cs` (reads `messaging.message_templates.header_image_file_id` for one tenant and a set of ids — follow the raw-SQL precedent of `SupportThreadFeed` or reuse `IMessageTemplateRepository` if it is cleaner; justify in the report); sweep `src/Modules/Storage/Modules.Storage.Infrastructure/TemplateImages/TemplateHeaderImageSweep.cs` + `TemplateHeaderImageSweepWorker.cs`; registration in `StorageInfrastructureExtensions.cs` and the Bootstrapper DI; `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`.

**Interfaces:**
- `public interface ITemplateImageReferences { Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken); }`
- `internal interface ITemplateHeaderImageSweep { Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken); }` — returns how many files it released.

Per pass: up to 200 `MessageTemplate` files not `Deleted`/`Purged` with `CreatedAt < now - 24h`, grouped by tenant; ask the port per tenant; for each unreferenced file: `RemovePublicCopiesAsync`, `SoftDelete(now)`, system audit `storage.file.deleted` with reason `template_header_image_unreferenced`, save per file (a failure on one file is logged and does not stop the pass). Worker: runs every 24 h; classify in the inventory.

- [ ] Failing unit tests (fakes for the port and storage): `ASweepReleasesAnUnreferencedImage`, `ASweepKeepsAReferencedImage`, `ASweepIgnoresFilesInsideTheGrace`, `ASweepIgnoresOtherOwnerTypes`, `AFailureOnOneFileDoesNotStopTheSweep`, `ReferencesAreAskedPerTenant`. Adapter unit test if the adapter is unit-testable; otherwise an integration test `TemplateImageReferencesTests.FindsOnlyTheTenantsReferencedIds` (write + compile only).
- [ ] RED, implement, GREEN; ArchitectureTests filtered by `MultiReplicaInventoryTests|StorageLayerTests|MessagingLayerTests|CompositionRootTests` if container-free.
- [ ] Commit `feat(storage): sweep template header images no template references`.

**Block A review**, then the strong whole-branch review, then merge to `develop` (owner authorized), `docs/estado.md` update on `develop`, delete branch and worktree.
