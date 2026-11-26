# Cambiar la imagen del encabezado de una plantilla — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `PUT …/templates/{templateId}/header-image` reemplaza (o asigna) la imagen guardada de una plantilla de encabezado `IMAGE`, sin llamar a Meta, conservando la imagen anterior.

**Architecture:** Método de dominio `ReplaceHeaderImage` + entidad nueva de imágenes retiradas (tabla propia, FK a la plantilla). Las retiradas cuentan como referenciadas para la limpieza de Storage y para `IsHeaderImageInUseAsync`; el borrado de la plantilla las libera. El handler reutiliza la validación/publicación/liberación de la creación, extraída a una pieza compartida.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-20-cambiar-imagen-encabezado-plantilla-design.md`

## Global Constraints

- Ninguna llamada a Meta en el reemplazo; `Status` y `ComponentsJson` no se tocan.
- La imagen anterior **no** se libera al reemplazar (decisión del owner, 2026-11-20).
- Errores: sólo códigos existentes (`TemplateNotFound`, `TemplateHeaderMismatch`, los `TemplateHeaderImage*`, `connection_unavailable`, `validation.failed`).
- Auditoría nueva: `MessagingAuditActions.TemplateHeaderImageReplaced = "messaging.template.header_image_replaced"`.
- Comentarios en español, explican por qué; citan la spec 2026-11-20.
- Presupuesto de pruebas del workspace: integración filtrada por nombre mientras se itera; nunca dos corridas de integración a la vez (`Get-Process testhost, dotnet` antes); `Api.exe` detenido antes de compilar.

## Review Focus

- Reemplazar con el mismo `fileId` que ya tiene la plantilla → 422 `TemplateHeaderImageRejected`, la imagen actual intacta (Task 3).
- Reemplazar dos veces seguidas → dos filas retiradas, ninguna liberada (Task 3, integración).
- Fallo del guardado después de publicar → la imagen **nueva** se libera; la actual sigue siendo de la plantilla (Task 3, unitaria).
- Plantilla sincronizada sin imagen y encabezado `IMAGE` → se asigna, no se crea fila retirada, y pasa a `Sendable` (Task 3).
- Plantilla de texto o de documento (la de cotización) → 422 `TemplateHeaderMismatch` sin publicar nada (Task 3).

---

### Task 1: Imágenes retiradas en dominio y persistencia

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplateRetiredHeaderImage.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs` (junto a `AttachHeaderImage`, ~l.260)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (DbSet, nombre de índice, `Configure…`)
- Create: migración `AddRetiredTemplateHeaderImages` en `Persistence/Migrations`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageTemplateReplaceHeaderImageTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessageTemplatePersistenceTests.cs`

**Interfaces:**
- Produces: `public sealed class MessageTemplateRetiredHeaderImage { Guid Id; Guid TenantId; Guid TemplateId; Guid FileId; string PublicUrl; DateTimeOffset RetiredAt; static MessageTemplateRetiredHeaderImage Create(Guid tenantId, Guid templateId, Guid fileId, string publicUrl, DateTimeOffset retiredAt) }` (`Id` con `Guid.CreateVersion7()`).
- Produces: `MessageTemplateRetiredHeaderImage? MessageTemplate.ReplaceHeaderImage(Guid fileId, string publicUrl, DateTimeOffset now)` — mismas guardas de argumento que `AttachHeaderImage` (fileId no vacío, URL ≤ `HeaderImageUrlMaxLength`); igual al actual → `ArgumentException`; devuelve la retirada (o `null` sin imagen previa); actualiza `UpdatedAt`.
- Produces: `MessagingDbContext.MessageTemplateRetiredHeaderImages`, tabla `messaging.message_template_retired_header_images` (`id`, `tenant_id`, `template_id`, `file_id`, `public_url` máx. `HeaderImageUrlMaxLength`, `retired_at`), FK `template_id` → `message_templates.id` con `OnDelete(Cascade)`, índice único `UX_message_template_retired_header_images_file` (constante `MessageTemplateRetiredHeaderImageFileIndex`) sobre `file_id`.

- [ ] **Step 1: Pruebas unitarias del dominio** — `ReplacingWithoutAnImageAssignsAndRetiresNothing` (devuelve `null`, `HeaderImageFileId`/`HeaderImageUrl` nuevos), `ReplacingAnImageRetiresThePreviousOne` (retirada con `TenantId`, `TemplateId`, `FileId` y `PublicUrl` anteriores, `RetiredAt == now`), `ReplacingWithTheSameFileThrows`, `ReplacingWithAnEmptyFileIdThrows`.
- [ ] **Step 2: Correr** `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageTemplateReplaceHeaderImageTests"` → falla de compilación (`ReplaceHeaderImage` no existe). Esa salida es la evidencia RED.
- [ ] **Step 3: Implementar** la entidad y el método.
- [ ] **Step 4: Correr** el mismo filtro → PASS.
- [ ] **Step 5: Mapeo EF + migración.** `dotnet ef migrations add AddRetiredTemplateHeaderImages --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext --output-dir Persistence/Migrations` (usa `MessagingDbContextFactory`). Revisar que el `Up` cree sólo la tabla, la FK y el índice.
- [ ] **Step 6: Prueba de persistencia** en `MessageTemplatePersistenceTests`: `RetiredHeaderImageRoundTripsAndCascadesWithItsTemplate` (guarda plantilla + retirada, relee, borra la plantilla, la retirada ya no está). Correr con `--filter "FullyQualifiedName~RetiredHeaderImageRoundTrips"` → PASS.
- [ ] **Step 7: Commit** `feat(messaging): retired template header images`.

### Task 2: Las retiradas cuentan como en uso y se liberan al borrar

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementPorts.cs` (`IMessageTemplateRepository`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessageTemplateRepository.cs:84`
- Modify: `src/Bootstrapper/TemplateImageReferences.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs` (`DeleteTemplateHandler`, ~l.570)
- Modify: el `FakeMessageTemplateRepository` de las unitarias
- Test: `DeleteTemplateHeaderImageTests.cs`, `TemplateImageReferencesTests.cs`, `TemplateManagementDeleteApiTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `void IMessageTemplateRepository.AddRetiredHeaderImage(MessageTemplateRetiredHeaderImage image)`; `Task<IReadOnlyList<Guid>> IMessageTemplateRepository.ListRetiredHeaderImageFileIdsAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken)`.
- `IsHeaderImageInUseAsync` y `TemplateImageReferences.FindReferencedAsync` devuelven también los `file_id` retirados del tenant (una sola consulta cada uno o dos `AnyAsync`/uniones; sin tracker, como hoy).

- [ ] **Step 1: Pruebas** — unitaria `DeletingPublishesTheReleaseOfEveryRetiredImageToo` (plantilla con imagen actual y dos retiradas en el fake → tres `Published`, todas antes del único guardado). Integración: `TemplateImageReferencesTests.RetiredImagesCountAsReferenced` (y no las de otro tenant); `TemplateManagementDeleteApiTests.DeletingReleasesCurrentAndRetiredImages`.
- [ ] **Step 2: Correr** la unitaria → falla de compilación (miembros del repositorio no existen).
- [ ] **Step 3: Implementar** repositorio, fake, `TemplateImageReferences` y el borrado (`ListRetiredHeaderImageFileIdsAsync` antes de `repository.Remove`, un `imageReleases.Publish` por archivo).
- [ ] **Step 4: Correr** unitaria y las dos de integración por nombre (`--filter "FullyQualifiedName~RetiredImagesCountAsReferenced|FullyQualifiedName~DeletingReleasesCurrentAndRetiredImages"`) → PASS.
- [ ] **Step 5: Commit** `feat(messaging): retired header images stay referenced until the template is deleted`.

### Task 3: Comando, endpoint y `headerMedia`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ReplaceTemplateHeaderImage.cs` (comando, validador, handler)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateHeaderImageIntake.cs` (extraído de `CreateTemplateHandler`)
- Modify: `TemplateManagement.cs` (`CreateTemplateHandler` usa la pieza extraída; `ManagedTemplateMapping.ToDto` pasa `described.HeaderMedia`)
- Modify: `TemplateManagementDtos.cs` (`ManagedTemplateDto` suma `string? HeaderMedia = null` al final)
- Modify: `IMessagingAuditRecorder.cs` (constante de auditoría)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs` (ruta + `ReplaceTemplateHeaderImageRequest`)
- Modify: registro del handler/validador donde se registra `DeleteTemplateHandler` (`QepServiceCollectionExtensions.cs`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ReplaceTemplateHeaderImageTests.cs`, `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/TemplateHeaderImageApiTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ReplaceHeaderImage`), Task 2 (`AddRetiredHeaderImage`, `IsHeaderImageInUseAsync` con retiradas).
- Produces: `public sealed record ReplaceTemplateHeaderImageCommand(Guid TenantId, Guid ConnectionId, Guid TemplateId, Guid? HeaderImageFileId) : ICommand<ManagedTemplateDto>`; validador: `HeaderImageFileId` no nulo ni vacío, mensaje `TemplateMessages.HeaderImage`, clave `headerImageFileId`.
- Produces: `internal sealed class TemplateHeaderImageIntake` con `Task<string> EnsureUsableImageAsync(Guid tenantId, Guid fileId, CancellationToken)` (devuelve el MIME) y `Task ReleaseOrDeferAsync(Guid tenantId, Guid fileId)`, mismo comportamiento y logs que hoy en `CreateTemplateHandler`.
- Produces: `PUT /templates/{templateId:guid}/header-image`, `Accepts<ReplaceTemplateHeaderImageRequest>`, `Produces<ManagedTemplateDto>()`, 403/404/422; `public sealed record ReplaceTemplateHeaderImageRequest(Guid? HeaderImageFileId)`.

Orden del handler: autorización → validador → `ResolveTemplateSourceAsync` (422 `connection_unavailable`) → `FindAsync` (404) → `TemplateCatalog.FromStored(...).HeaderMedia != TemplateHeaderMedia.Image` ⇒ 422 `TemplateHeaderMismatch` → `IsHeaderImageInUseAsync` ⇒ 422 `TemplateHeaderImageRejected` con `fieldErrors` → `EnsureUsableImageAsync` → `try { PublishAsync; ReplaceHeaderImage; AddRetiredHeaderImage si no es null; audit; SaveChangesAsync; saved = true } finally { si !saved → ReleaseOrDeferAsync(nueva) }` → `ManagedTemplateMapping.ToDto`.

- [ ] **Step 1: Refactor sin cambio de comportamiento** — mover a `TemplateHeaderImageIntake`; correr `--filter "FullyQualifiedName~CreateTemplateWithHeaderImageTests"` → PASS igual que antes.
- [ ] **Step 2: Pruebas unitarias** en `ReplaceTemplateHeaderImageTests`: `ReplacesTheImageAndRetiresThePreviousWithoutCallingMeta` (ningún request al fake de Meta, una retirada, ningún `Published`, auditoría `messaging.template.header_image_replaced`); `AssignsAnImageToASyncedImageTemplate` (sin retirada, DTO `Sendable` y `HeaderMedia == "Image"`); `RejectsATextHeaderTemplateWithHeaderMismatch` y `RejectsADocumentHeaderTemplateWithHeaderMismatch` (nada publicado); `RejectsTheFileTheTemplateAlreadyUses`; `ReleasesTheNewImageWhenTheSaveFails` (la actual sigue en la fila); `UnknownTemplateIsNotFound`.
- [ ] **Step 3: Correr** `--filter "FullyQualifiedName~ReplaceTemplateHeaderImageTests"` → falla de compilación.
- [ ] **Step 4: Implementar** comando, validador, handler, DTO, mapeo, auditoría, endpoint y registro.
- [ ] **Step 5: Correr** las unitarias → PASS.
- [ ] **Step 6: Integración** en `TemplateHeaderImageApiTests`: `ReplacingTheHeaderImageReturnsTheNewUrlAndKeepsTheOld` (dos reemplazos seguidos: la respuesta trae la URL nueva y `headerMedia: "Image"`, dos filas retiradas, el `FakeTemplateImageStore` no liberó nada); `ReplacingWithoutPermissionIsForbidden`. Correr filtrado por esos nombres → PASS.
- [ ] **Step 7: Fin de bloque** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests` completo y, una vez, las clases de integración tocadas (`TemplateHeaderImageApiTests`, `TemplateImageReferencesTests`, `TemplateManagementDeleteApiTests`, `MessageTemplatePersistenceTests`) con `-- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests`.
- [ ] **Step 8: Commit** `feat(messaging): replace a template header image without Meta review`.

## Ejecución

Dos bloques (regla del workspace): **Bloque A** = Tasks 1–2 (persistencia y referencias), **Bloque B** = Task 3. Una revisión por bloque y una revisión fuerte de la rama al final. Antes de mergear a `develop`: proyectos de prueba de Messaging y Storage (`Modules.Storage.IntegrationTests` cubre la limpieza diaria).
