# Logo del tenant — diseño (backend)

**Fecha:** 2026-09-19
**Repo:** `qep-backend` (módulos `Tenancy`, `Storage` y `Quotations`; adaptadores en `Bootstrapper`)
**Contraparte:** `qep-frontend/docs/superpowers/specs/2026-09-19-logo-del-tenant-design.md`
**Estado:** aprobado en conversación; pendiente de revisión escrita

## Problema

Un tenant no puede subir su logo. Hoy la marca es configuración de build del frontend
(`VITE_TENANT_BRAND_LOGO`, ver la spec del frontend): una imagen por despliegue, la misma para
todos los tenants de esa build. Y el PDF de la cotización es white-label a secas: `quotation.typ:11-13`
lo dice explícito ("el payload no trae ni logo ni color de marca") y el encabezado se sostiene sólo
con el peso tipográfico de la razón social (`quotation.typ:148-149`).

Lo que ya existe y sirve:

- `Tenant` (`Tenant.cs:6-217`) tiene `Version` como token de concurrencia (`:56`,
  `TenancyDbContext.cs:66-68`), `UpdateSettings` con `EnsureActive` + `Version++` + evento de dominio
  (`:74-113`), y su handler ya hace `If-Match` → 412, auditoría y outbox (`UpdateTenantSettings.cs:52-57`,
  `:78-91`).
- `Storage` tiene la subida completa —sesión, `PUT` prefirmado, `complete`— (`StorageEndpoints.cs:22-34`),
  publicación al bucket público con copia del original y sus variantes y rollback si una copia falla
  (`SetFilePublication.cs:37-63`), y borrado lógico que despublica (`SoftDeleteFile.cs:40-51`).
- `Quotations` cachea el PDF por `Quotation.Version` (`QuotationPdf.cs:60`,
  `QuotationPdfProvider.cs:31-39`) y lo renderiza contra `qcode-pdf` con el markup entero en cada
  request (`QCodePdfRenderer.cs:32-62`).

Lo que falta es el dato —`Tenant` no tiene logo— y las tres costuras: cómo se publica, cómo lo lee el
sidebar y cómo llega al PDF.

## Decisiones tomadas

| #   | Decisión                                                                                                                                                                                                                                                                                          | Alternativa descartada                                                                                                                                                                                                                                                       |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | El logo entra por el pipeline de `Storage` tal cual está: `POST /files` con `ownerType = Tenant` (**valor nuevo** de `FileOwnerType`), `PUT` prefirmado, `POST .../complete`. Después, un endpoint **nuevo** de Tenancy lo asigna.                                                                     | Endpoint propio que reciba multipart: duplica escaneo, checksum y variantes que `CompleteUpload.cs:67-110` ya hace.                                                                                                                                                              |
| 2   | La asignación vive en Tenancy: `PUT/DELETE /tenants/{tenantId}/settings/logo`, bajo `TenancyPermissions.SettingsUpdate`, con `If-Match` obligatorio como el `PATCH /settings`. Devuelven el `TenantSettingsResponse` completo con `Version`/`ETag` nuevos.                                              | Reusar `PUT /files/{id}/publication` y que el frontend guarde el `fileId` en `PATCH /settings`: exige `storage.file.publish` a quien sólo administra el tenant, y deja dos requests que pueden quedar a medias.                                                                     |
| 3   | El agregado guarda `LogoFileId` **y** `LogoPublicKey`. La URL **no** se guarda: se arma al leer con la base pública configurada, igual que `FileResourceDto.PublicUrl` (`StorageDtos.cs:70-74`).                                                                                                        | Guardar sólo `LogoFileId` y resolver la clave leyendo `FileResource` en cada `GET /settings`: una lectura cruzada Tenancy→Storage por cada pintado del sidebar. Guardar la URL: cambiar `PublicBaseUrl` dejaría URLs muertas en base.                                             |
| 4   | Publicar y despublicar el archivo lo hace un **puerto de Tenancy** (`ITenantLogoStorage`) implementado en `Bootstrapper` sobre repositorio + bucket público de Storage, **sin pasar por los handlers de Storage ni por el dispatcher**. Mismo criterio que `PublicPaymentProofPublisher.cs:24-26`.        | Despachar `PublishFileCommand`/`SoftDeleteFileCommand`: sus handlers exigen `storage.file.publish` y `storage.file.delete` del sujeto (`SetFilePublication.cs:23-24`, `SoftDeleteFile.cs:24-25`), permisos que no tienen que ver con administrar el tenant.                         |
| 5   | La lógica de copia original + variantes + rollback se **extrae** de `PublishFileHandler` (`SetFilePublication.cs:37-63`) a un servicio público de `Modules.Storage.Application` (`FilePublication`), que usan el handler y el adaptador. `StorageKey` sigue `internal` (`StorageKey.cs:9`).           | Copiar el bucle en el adaptador: dos lugares que tienen que producir la misma clave pública, porque `SoftDeleteFileHandler` borra por `StorageKey.PublicVariantFor` (`SoftDeleteFile.cs:45-46`). Hacer `StorageKey` público expone la forma de las claves fuera del módulo.        |
| 6   | Límites del logo: sólo `image/png`, `image/jpeg`, `image/webp`; ≤ 2 MiB. Se validan en el adaptador contra el `FileResource` ya subido, **además** de `FileUploadPolicy` (25 MiB, `FileUploadPolicy.cs:7`).                                                                                             | Bajar el tope global de `FileUploadPolicy`: rompe comprobantes y portadas. Aceptar SVG: `FileUploadPolicy.cs:9-21` no lo admite y un SVG en un bucket público puede llevar script.                                                                                                 |
| 7   | Reemplazar un logo publica el nuevo **antes** y commitea Tenancy; recién después retira el viejo (despublica + borrado lógico). Si el retiro falla se registra en log y el archivo viejo queda publicado hasta que alguien lo borre por `DELETE /files/{id}`.                                          | Retirar primero: entre el retiro y el commit el sidebar y el PDF quedan sin logo, y si el commit falla se perdió el logo vigente.                                                                                                                                                   |
| 8   | Quitar el logo retira la copia pública **antes** de commitear Tenancy. Si el commit falla, la persona vuelve a quitarlo: `RemoveLogo` es idempotente.                                                                                                                                                 | Commitear primero: un logo que la persona pidió quitar seguiría servido por URL pública hasta que alguien limpie.                                                                                                                                                                  |
| 9   | El PDF obtiene el logo por un puerto de Quotations (`IQuotationLogoLookup`) implementado en `Bootstrapper`: lee `LogoFileId` por `ITenantDirectory` y baja los **bytes del original privado** con `IObjectStorage.DownloadAsync` (`IObjectStorage.cs:47`). Viajan a `qcode-pdf` en `assets`.          | Bajar la URL pública por HTTP desde la API: egreso a red, `HttpClient` nuevo, y depende de que el bucket público esté configurado en ese ambiente. El objeto privado es el canónico, mismo criterio que `QuotationPdf.cs:37-39`.                                                    |
| 10  | La caché del PDF gana `LogoFileId`: está obsoleto si `QuotationVersion < Version` **o** `LogoFileId != LogoFileId` vigente del tenant.                                                                                                                                                                | Subir `Quotation.Version` de todas las cotizaciones al cambiar el logo: una escritura masiva cruzada de módulos para invalidar una caché.                                                                                                                                          |
| 11  | Evento de dominio **nuevo** `TenantLogoUpdatedDomainEvent`, con su mapeo en `OutboxWriter`. Auditoría `tenancy.logo.updated` / `tenancy.logo.removed` por `IAuditRecorder`, como `tenancy.settings.updated` (`UpdateTenantSettings.cs:78-86`).                                                       | Reusar `TenantSettingsUpdatedDomainEvent` con `changedFields = ["logoFileId"]`: `TenantSettingsChangeLogProjection` lo proyectaría como cambio de configuración y el nombre del evento mentiría.                                                                                    |

## Contrato (lo que ve el frontend)

**Existente, cambia de forma.** `TenantSettingsResponse` (`TenantSettingsEndpoints.cs:115-121`) gana
un campo:

```
logo: { fileId: guid, url: string | null } | null
```

`null` cuando el tenant no tiene logo. `url` sólo es `null` si el bucket público se desconfiguró
después de asignar el logo (`IPublicObjectStorage.IsConfigured`, `IPublicObjectStorage.cs:5`); en ese
caso `fileId` viaja igual para que la pantalla ofrezca quitar y no subir. El comentario del DTO tiene
que decir por qué la URL viaja resuelta: es la regla BFF del repo — el sidebar necesita un `src`
listo, no una clave que armar con una base que el navegador no conoce.

**Nuevos**, en el mismo grupo `/api/v1/tenants/{tenantId:guid}/settings` (`TenantSettingsEndpoints.cs:16`):

- `PUT /logo`, cuerpo `{ fileId }`. Permiso `TenancyPermissions.SettingsUpdate`
  (`TenancyPermissions.cs:6`). `If-Match` obligatorio: sin él, `428 precondition.if_match_required`
  con el mismo `TryParseVersion` (`TenantSettingsEndpoints.cs:56-61`, `:90-106`); desactualizado,
  `412 concurrency.conflict` (`UpdateTenantSettings.cs:52-57`, `ApiExceptionHandler.cs:139-140`).
  Responde `200` con el `TenantSettingsResponse` completo y `ETag` nuevo (`:76-88`). Mismo `fileId`
  que el vigente: `200` sin subir `Version`.
- `DELETE /logo`. Mismo permiso, mismo `If-Match`. Sin logo: `200` sin subir `Version`.
- `GET /` y `PATCH /` no cambian de reglas; sólo devuelven el campo nuevo.

Códigos de error del `PUT`, todos `422` por `DomainException` (`ApiExceptionHandler.cs:145-146`):

| Código                          | Cuándo                                                                                                      | Origen                                                   |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- |
| `tenancy.logo.file_not_found`   | **nuevo.** El `fileId` no existe o es de otro tenant. Un solo código, como `PublicPaymentProofPublisher.cs:52-60`. | adaptador                                                |
| `tenancy.logo.not_owned`        | **nuevo.** Existe en el tenant pero `OwnerType != Tenant` o `OwnerId != tenantId`.                          | adaptador                                                |
| `tenancy.logo.not_available`    | **nuevo.** `Status != Available` (`FileResource.cs:75`, `FileResourceEnums.cs:5-13`).                        | adaptador                                                |
| `tenancy.logo.not_image`        | **nuevo.** `MimeType` fuera de PNG/JPEG/WebP.                                                                | adaptador                                                |
| `tenancy.logo.too_large`        | **nuevo.** `SizeBytes > 2 MiB` (`FileResource.cs:57`, el tamaño verificado en `CompleteUpload`).             | adaptador                                                |
| `storage.public.not_configured` | existente (`SetFilePublication.cs:25-30`). Bucket público sin configurar.                                    | `FilePublication` (Storage)                              |
| `tenancy.tenant.not_active`     | existente (`Tenant.cs:126-128`).                                                                             | `Tenant.SetLogo` / `RemoveLogo`                          |
| `validation.failed`             | existente. `fileId` vacío, con mapa `errors` (`ApiExceptionHandler.cs:58-65`).                               | validador FluentValidation del comando                   |

`storage.file.owner_type_invalid` (`StorageEndpoints.cs:91-97`) deja de salir para `"Tenant"` en
cuanto exista el valor del enum; el frontend lo envía tal cual, sin dígitos.

## Dominio (`Modules.Tenancy.Domain`)

- `Tenant`: propiedades `Guid? LogoFileId` y `string? LogoPublicKey`.
- `bool SetLogo(Guid fileId, string publicKey, DateTimeOffset occurredAt)`: `EnsureActive()`
  (`Tenant.cs:122-130`); si `fileId == LogoFileId` devuelve `false` sin tocar nada; si no, asigna las dos
  propiedades, `Version++`, `UpdatedAt`, y agrega `TenantLogoUpdatedDomainEvent` — mismo patrón que
  `UpdateSettings` (`:94-112`). `publicKey` vacía es `ArgumentException`: es un error del adaptador,
  no de la persona.
- `bool RemoveLogo(DateTimeOffset occurredAt)`: `EnsureActive()`; sin logo devuelve `false`; si no,
  pone ambas en `null`, `Version++`, `UpdatedAt`, evento con `LogoFileId = null`.
- `TenantLogoUpdatedDomainEvent(Guid EventId, DateTimeOffset OccurredAt, TenantId TenantId, long Version, Guid? LogoFileId) : IDomainEvent`,
  **nuevo**, con la forma de `TenantSettingsUpdatedDomainEvent.cs:5-10`. `LogoFileId = null` significa
  "se quitó"; no hay evento aparte para no duplicar el mapeo de outbox.
- El límite de 2 MiB y los tres tipos **no** viven en el agregado: `Tenant` no conoce `FileResource`.
  Viven en el adaptador (ver Adaptadores), que es quien tiene el archivo a mano.

## Aplicación (`Modules.Tenancy.Application`)

- **Puerto `ITenantLogoStorage`**, nuevo, junto a `ITenantDirectory` e `ITenantClock`:
  - `Task<TenantLogoPublication> PublishAsync(Guid tenantId, Guid fileId, CancellationToken)`:
    valida y publica; devuelve `(string PublicKey)`. Lanza los `tenancy.logo.*` de arriba.
  - `Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken)`: despublica y borra
    lógicamente el archivo. Un archivo ya `Deleted`/`Purged` no es error: la operación es idempotente
    porque el commit de Tenancy puede fallar después (decisión 8).
  - `string? GetUrl(string publicKey)`: `null` si el bucket público no está configurado. El adaptador
    **no** llama a `R2PublicObjectStorage.GetUrl` sin comprobar `IsConfigured`: lanza
    (`R2PublicObjectStorage.cs:54-59`).
- `TenantSettingsDto` (`TenantSettingsDto.cs:5-11`) gana `TenantLogoDto? Logo` con `(Guid FileId, string? Url)`.
  `TenantMappings.ToSettingsDto` (`TenantMappings.cs:7-14`) pasa a recibir `ITenantLogoStorage` para
  resolver la URL, como `FileResourceMapping.ToDto(resource, publicStorage)` (`StorageDtos.cs:43`).
  `GetTenantSettingsHandler` (`GetTenantSettings.cs:8-11`) y `UpdateTenantSettingsHandler` lo inyectan.
- **`SetTenantLogoCommand(TenantId, Guid FileId, long ExpectedVersion, string CorrelationId) : ICommand<TenantSettingsDto>`**
  y su handler, calcados de `UpdateTenantSettings.cs:30-96`, en este orden:
  1. Validador: `FileId` no vacío, `ExpectedVersion > 0` (como `:26`).
  2. `EnsureAuthorized` con `SettingsUpdate` (`:97-106`).
  3. Cargar el tenant; `404 tenancy.tenant.not_found`; `Version != ExpectedVersion` → `RequestConcurrencyException` (`:52-57`).
     Estas tres fallas ocurren **antes** de tocar Storage: un 412 no deja copias públicas huérfanas.
  4. Si `FileId == tenant.LogoFileId`: devolver el DTO sin más.
  5. `logoStorage.PublishAsync` → commit de **Storage** (ver Adaptadores).
  6. `tenant.SetLogo(fileId, publication.PublicKey, clock.UtcNow)`. Si lanza (`not_active`), `UnpublishAsync`
     del nuevo en `catch` y relanzar.
  7. `PullDomainEvents`, `auditRecorder.Record(tenantId, subjectId, "tenancy.logo.updated", "tenant", id, "success", ["logoFileId"], now)`,
     `outboxWriter.Add` por evento, `unitOfWork.SaveChangesAsync` → commit de **Tenancy**. Si falla,
     `UnpublishAsync` del nuevo en `catch` (mejor esfuerzo) y relanzar.
  8. Si había un logo anterior distinto: `UnpublishAsync(oldFileId)`. Una excepción acá **no** falla el
     request: el logo nuevo ya está commiteado. Se registra por `ILogger` en el adaptador (ver abajo).
  Los retiros de los pasos 6, 7 y 8 van con `CancellationToken.None`, no con el token del request:
  cada uno corre después de un commit (de Storage o de Tenancy), y un request cancelado no puede
  dejar una copia pública huérfana.
- **`RemoveTenantLogoCommand(TenantId, long ExpectedVersion, string CorrelationId)`** y su handler:
  pasos 1-3 iguales; sin logo, devolver el DTO; `tenant.RemoveLogo` en memoria (su `EnsureActive`
  rechaza antes de tocar Storage); `UnpublishAsync(LogoFileId)` **antes del commit** (decisión 8);
  después auditoría `tenancy.logo.removed` con `["logoFileId"]`, outbox, commit.
- Registrar los dos handlers a mano en `QepServiceCollectionExtensions` como todos
  (`QepServiceCollectionExtensions.cs:55-60`); `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered`
  (`CompositionRootTests.cs:28-45`) lo verifica.
- `ITenantDirectory` (`ITenantDirectory.cs:8-18`) gana `Task<Guid?> GetLogoFileIdAsync(TenantId, CancellationToken)`,
  implementado en `TenantDirectory.cs` con la misma proyección que `GetTimeZoneAsync` (`TenantDirectory.cs:39-43`).
  Es la única lectura que Quotations necesita de Tenancy (decisión 9).

## Infraestructura (`Modules.Tenancy.Infrastructure`)

- `TenancyDbContext.ConfigureTenant` (`TenancyDbContext.cs:37-72`): `logo_file_id` (`uuid`, null) y
  `logo_public_key` (`character varying(512)`, null). 512 es el largo de `storage.file_resources.public_storage_key`
  (`StorageDbContext.cs:56`): la clave que se guarda acá es esa misma.
- Migración `AddTenantLogo` con `dotnet ef migrations add`, dos `AddColumn` nulos como
  `20260912133442_AddMembershipDisplayName.cs:13-19`. Sin backfill: ningún tenant tiene logo. `Down`
  quita las dos columnas.
- `OutboxWriter.Add` (`OutboxWriter.cs:14-25`): agregar `TenantLogoUpdatedDomainEvent => "tenancy.tenant-logo-updated.v1"`.
  **Sin esta línea el handler lanza `InvalidOperationException` en runtime** (`:23-24`) y el `PUT`
  responde 500. Hoy nadie consume el evento; queda para quien lo necesite.
- `TenancyAuditRecorder` no cambia: deriva la fuente del prefijo de la acción
  (`TenancyAuditRecorder.cs:34`, `:39`), así que `tenancy.logo.updated` cae en `tenancy`.

## Storage (`Modules.Storage.*`)

- `FileOwnerType` (`FileResourceEnums.cs:19-33`) gana `Tenant = 6`. Se persiste **por nombre** en
  `character varying(20)` (`:15-18`, `StorageDbContext.cs:45-48`): agregar valores es seguro, sin
  migración. `OwnerId` es el `tenantId`. `CreateUploadSessionHandler` no valida `OwnerId` contra el
  tipo (`CreateUploadSession.cs:27-44`); la relación la verifica el adaptador al asignar.
- `CompleteUpload` no cambia: un `Tenant` es imagen, así que sigue el camino de `files/` con variantes
  (`CompleteUpload.cs:73-110`). Las variantes se publican y se despublican junto con el original, como
  hoy (`SetFilePublication.cs:48-53`, `SoftDeleteFile.cs:43-47`).
- **`FilePublication`**, servicio público nuevo en `Modules.Storage.Application` (decisión 5):
  - `Task<string> PublishAsync(FileResource resource, CancellationToken)`: `IsConfigured` o
    `storage.public.not_configured` (`SetFilePublication.cs:25-30`); `resource.EnsureDownloadable()`;
    clave `resource.PublicStorageKey ?? StorageKey.PublicFor(...)` (`:37-38`); `resource.Publish` (`:41`,
    que además exige `image/*`, `FileResource.cs:351-356`); copia original + variantes con el rollback
    de `:44-63`. Devuelve la clave.
  - `Task UnpublishAsync(FileResource resource, CancellationToken)`: los borrados de `SoftDeleteFile.cs:40-49`.
  - `PublishFileHandler`, `UnpublishFileHandler` y `SoftDeleteFileHandler` pasan a llamarlo. Autorización,
    `PaymentProofGuard` y auditoría se quedan en los handlers: el servicio no sabe quién llama.
  - Se registra en `StorageInfrastructureExtensions.cs:38-39`, scoped como el repositorio.
- **Sondas y barridos: no hace falta ninguna.** `PaymentProofOrphanCleanupProcessor` recorre sólo
  `payment-proofs/` (`PaymentProofOrphanCleanupProcessor.cs:44-46`, `:110-117`) y las claves del logo
  van a `tenants/{tid}/media/{fileId}/original.ext` (`StorageKey.cs:25-26`), fuera de su alcance. La
  sonda `FilePublicObjectReferenceProbe` (`FilePublicObjectReferenceProbe.cs:16-19`) responde por
  `PublicStorageKey`, que el logo sí registra vía `FileResource.Publish`: si un día un barrido cubriera
  `tenants/`, ya lo protegería. No se agrega una sonda de Tenancy.
- `DELETE /files/{fileId}` sobre el logo vigente, hecho a mano por alguien con `storage.file.delete`:
  despublica y borra el archivo, pero `Tenant.LogoFileId` sigue apuntándolo y `url` queda muerta. Es el
  mismo borde que hoy tiene una portada de producto; se acepta (ver Bordes).

## Adaptadores (`Bootstrapper`)

- **`TenantLogoStorage : ITenantLogoStorage`**, nuevo, con `IFileResourceRepository`, `FilePublication`,
  `IPublicObjectStorage`, `IStorageAuditPublisher`, `IStorageUnitOfWork`, `IExecutionContext`, `IClock`
  e `ILogger<TenantLogoStorage>`. Registrado scoped junto a los demás adaptadores Storage↔módulo
  (`QepServiceCollectionExtensions.cs:425-444`).
  - `PublishAsync`: `repository.GetAsync`; `null` o `TenantId != tenantId` → `tenancy.logo.file_not_found`;
    `OwnerType != Tenant || OwnerId != tenantId` → `not_owned`; `Status != Available` → `not_available`;
    `MimeType` fuera de `{image/png, image/jpeg, image/webp}` → `not_image`; `SizeBytes > 2 MiB` →
    `too_large`. Todas son `TenantDomainException` (`TenantDomainException.cs:5-6`): Bootstrapper ya
    lanza excepciones de dominio de otro módulo desde un adaptador (`PublicPaymentProofPublisher.cs:57`).
    Después `filePublication.PublishAsync(resource)`, `auditPublisher.Publish(..., "storage.file.published", ...)`
    como `SetFilePublication.cs:65-67`, y `storageUnitOfWork.SaveChangesAsync`. **Ese commit es de
    Storage y ocurre antes del de Tenancy** (paso 5 del handler).
  - `UnpublishAsync`: cargar; si no existe, es de otro tenant, no es el logo del tenant
    (`OwnerType != Tenant || OwnerId != tenantId`), o ya está `Deleted`/`Purged`, volver sin
    error; si no, `filePublication.UnpublishAsync`, `resource.SoftDelete` (`FileResource.cs:158-170`),
    auditoría `storage.file.deleted` (`SoftDeleteFile.cs:53-59`), commit de Storage. Una excepción se
    propaga: el handler decide si la traga (paso 8) o la relanza (pasos 6-7). En el paso 8 la registra el
    handler con un `ILogger<SetTenantLogoHandler>`: `Modules.Tenancy.Application` no usa logging hoy y
    hace falta la referencia a `Microsoft.Extensions.Logging.Abstractions`; si se prefiere no sumarla,
    el mejor esfuerzo se mueve al adaptador como `TryUnpublishAsync` y se documenta ahí.
  - `GetUrl`: `publicStorage.IsConfigured ? publicStorage.GetUrl(key) : null`.
- **`QuotationTenantLogoLookup : IQuotationLogoLookup`**, nuevo, con `ITenantDirectory`,
  `IFileResourceRepository` e `IObjectStorage`. `FindAsync(tenantId)` → `GetLogoFileIdAsync`; `null` si
  no hay; carga el `FileResource` y devuelve `QuotationLogoRef(FileId, StorageKey, Extension)` con la
  extensión derivada del `MimeType` (tabla como `PublicPaymentProofPublisher.cs:38-45`, sin PDF). Si
  el archivo no existe, es de otro tenant o no está `Available` (alguien lo borró por Storage) devuelve
  `null`: el PDF sale sin logo en vez de fallar. `ReadAsync(ref)` → `objectStorage.DownloadAsync(ref.StorageKey)`
  (`IObjectStorage.cs:47`), y `null` si la descarga falla (ver Bordes, "Logo que no se puede leer").
  Mismo patrón que `QuotationFileLookup.cs:22-25` y `ProductImageLookup.cs:20-22`.
- `QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` (`QuotationsLayerTests.cs:61-71`)
  y `TenancyLayerTests.ApplicationDoesNotReferenceInfrastructureOrApi` (`TenancyLayerTests.cs:21-28`)
  siguen verdes: Tenancy.Application no referencia Storage (`Modules.Tenancy.Application.csproj:4-7`) y
  Quotations.Application sólo suma un puerto propio.

## Quotations (PDF)

- `QuotationPdfDocument` (`QuotationPdfDocument.cs:13-61`) gana `QuotationPdfLogo? Logo` con
  `(string FileName, byte[] Content)`, `FileName = "logo" + extensión` (`logo.png`, `logo.jpg`,
  `logo.webp`). `Content` lleva `[JsonIgnore]`: `System.Text.Json` ya se usa en Application
  (`ExportJobSupport.cs`), y sin el atributo los bytes viajarían en `data` en base64 además de en
  `assets`. La plantilla lee `data.logo.fileName`; `null` cuando no hay.
- `QuotationPdfDocumentMapper.From` (`QuotationPdfDocumentMapper.cs:26-51`) recibe además
  `QuotationPdfLogo? logo` y lo pasa tal cual.
- `QCodePdfRenderer.RenderAsync` (`QCodePdfRenderer.cs:32-62`): el payload de `:35-40` suma
  `assets = { [document.Logo.FileName] = Convert.ToBase64String(document.Logo.Content) }` cuando hay
  logo, y **omite la propiedad** cuando no: el request de hoy no cambia de forma para un tenant sin
  logo. El contrato `POST /pdf { source, data, filename, assets: { "<nombre>": "<base64>" } }` es
  el de `qcode-pdf`, verificado el 2026-09-19 en su repo (`repositories/qcode-pdf`): `README.md:32`
  lo documenta y `Services/TypstService.cs:106-129` escribe cada entrada en `<workdir>/assets/<nombre>`
  (con `Path.GetFileName`, así que el nombre no puede llevar ruta). Este repo sólo lo menciona en
  `README.md:1224-1242`.
- `quotation.typ`: en la celda izquierda del encabezado (`quotation.typ:155-186`, celda `:159-166`) va
  primero `#if data.logo != none { image("assets/" + data.logo.fileName, height: 16mm, fit: "contain") }` y un
  `#v(6pt)`, después el emisor o "Cotización" como hoy. No hay tope de ancho explícito: la columna
  `1fr` de la grilla (~87mm con la ficha actual) acota el logo, y uno apaisado 8:1 se achica solo sin
  tocar la ficha de la derecha (verificado con un render local el 2026-09-19, typst 0.15.1). Sin logo
  el documento es **idéntico** al de hoy. Una sola tinta, sin
  color de marca: los comentarios de `:11-13` y `:148-149` se corrigen para decir que el logo sí
  viaja y el color no.
- `QuotationPdf` (`QuotationPdf.cs:13-68`): propiedad `Guid? LogoFileId`; `Generate` y `Regenerate`
  la reciben; `IsStaleFor(long quotationVersion, Guid? logoFileId)` es
  `QuotationVersion < quotationVersion || LogoFileId != logoFileId` — el `<` de `:55-59` se conserva;
  el logo se compara por `!=` porque no tiene orden.
- `QuotationPdfProvider.EnsureCurrentAsync` (`QuotationPdfProvider.cs:28-66`): inyecta
  `IQuotationLogoLookup`; `FindAsync` **antes** de decidir (`:36`) — una lectura de Tenancy y otra de
  Storage por export, sin bajar bytes; `ReadAsync` sólo al regenerar. `Generate`/`Regenerate` con
  `logo?.FileId`. Exportar (`ExportQuotationPdf.cs:34`) y enviar (`SendQuotation.cs:84`) no cambian.
- `QuotationsDbContext.ConfigureQuotationPdf` (`QuotationsDbContext.cs:295-314`): `logo_file_id` (`uuid`,
  null). Migración `AddQuotationPdfLogo` en `Modules.Quotations.Infrastructure`. Las filas existentes
  quedan con `null`: un tenant que después asigna logo las ve obsoletas en el próximo export, que es
  lo deseado; un tenant sin logo no regenera nada.
- **Limitación conocida, fuera de alcance:** cambiar la plantilla sigue sin invalidar la caché
  (`QuotationPdfDocumentMapper.cs:124-125`). El logo sí la invalida porque es dato, no plantilla.

## Bordes

- **Permisos por rol.** Verificado en el catálogo de roles de `QepServiceCollectionExtensions.cs`:
  `admin` tiene `tenancy.settings.update` (`:552`) **y** `storage.file.upload` (`:557`) — puede subir y
  asignar. `advisor` tiene `storage.file.upload` (`:625`) y `tenancy.settings.read` (`:597`) pero no
  `update`: puede subir el archivo y recibe `403` al asignarlo, que es lo esperado. `billing` sólo lee
  (`:643`, `:654`). No hay ítem de trabajo en roles. Los roles viven en código: sin migración.
- **Stub de desarrollo.** Concede sólo permisos de Tenancy por defecto (gotcha del `CLAUDE.md`): la prueba
  de integración pide `X-Permissions: storage.file.upload,tenancy.settings.read,tenancy.settings.update`
  como `TenantLogoApiTests.cs:563` (`CreateClient`, vía `StoragePermissions`). `TenantSettingsApiTests`
  no lo manda (`:190-199`) y no lo
  necesita para el `PATCH`.
- **`PUT` con un archivo `Tenant` de una asignación que falló** (un `PUT` anterior que falló en el
  agregado o en el commit de Tenancy): el `catch` de los pasos 6-7 no sólo retira la copia pública,
  también **borra lógicamente** el archivo nuevo (`TryUnpublishAsync` → `UnpublishAsync` →
  `resource.SoftDelete`). Reintentar con el mismo `fileId` responde `422 tenancy.logo.not_available`:
  el reintento necesita una subida nueva. El frontend siempre vuelve a subir el archivo antes de
  asignarlo, así que en la práctica no se ve.
- **Bucket público sin configurar.** `PUT` → `422 storage.public.not_configured` antes de tocar nada.
  `GET /settings` de un tenant con logo → `logo.url = null`. Producción lo configura por
  `k8s/prod-configMap.yaml:51-52`; local, por user-secrets (`README.md:127`, `:200-201`).
- **Archivo del logo borrado por Storage** (`DELETE /files/{id}` con `storage.file.delete`): el sidebar
  recibe una `url` muerta hasta que alguien quite o reemplace el logo. El PDF **se regenera una vez,
  sin logo**: `QuotationTenantLogoLookup.FindAsync` devuelve `null` si el archivo no está `Available`,
  así que la caché compara el `LogoFileId` guardado contra `null`, lo ve vencido y regenera; desde ahí
  queda guardado sin `LogoFileId` y los exports siguientes lo reusan. Aceptado: hoy pasa lo mismo con
  una portada de producto.
- **Logo que no se puede leer** (R2 caído, objeto privado faltante): `QuotationTenantLogoLookup.ReadAsync`
  registra un warning y devuelve `null` —salvo una cancelación, que se propaga—, y el PDF sale sin logo
  en vez de fallar: ni el export ni el envío por WhatsApp se bloquean por el logo. `QuotationPdfProvider`
  guarda ese PDF **sin** `LogoFileId`, así que el próximo export lo ve vencido y vuelve a intentar
  con el logo.
- **Tenant inactivo**: `422 tenancy.tenant.not_active` en `PUT` y `DELETE`. La copia pública del nuevo
  archivo se retira en el `catch` del paso 6. En `DELETE`, `tenant.RemoveLogo` corre en memoria antes
  de `UnpublishAsync`, así que el rechazo llega antes de retirar nada.
- **Concurrencia**: `Version` sube en `SetLogo` y `RemoveLogo`; un `PATCH /settings` con el `If-Match`
  de antes del logo responde 412, como corresponde. Dos `PUT /logo` simultáneos: el segundo pierde por
  `Version` en el paso 3, antes de publicar; si los dos pasan el paso 3, el token de concurrencia de EF
  (`TenancyDbContext.cs:66-68`) rechaza el segundo commit y su copia se retira en el `catch` del paso 7.
- **Un `Tenant` subido y nunca asignado** queda `Available` en `files/`, privado, visible en `GET /files`.
  No hay barrido; se borra a mano o se reemplaza. Mismo estado que una portada de producto que nadie
  asignó.

## Pruebas

TDD: RED antes que GREEN, con evidencia literal. La suite de integración corre con Testcontainers y
exige Docker; durante el ciclo, sólo los archivos tocados.

- **`Modules.Tenancy.UnitTests/TenantTests.cs`** (junto a `:10-46`):
  - `SetLogoIncrementsVersionAndRaisesTheEvent` — `Version == 2`, un `TenantLogoUpdatedDomainEvent` con el `fileId`.
  - `SetLogoWithTheSameFileIsANoOp` — `false`, `Version == 1`, sin eventos.
  - `RemoveLogoClearsBothFieldsAndRaisesTheEventWithNull`; `RemoveLogoWithoutLogoIsANoOp`.
  - `SetLogoOnAnInactiveTenantIsRejected` — código `tenancy.tenant.not_active`. Hoy no hay forma pública de
    desactivar un `Tenant` (`TenantStatus` sólo lo pone el constructor, `Tenant.cs:36`): si no se puede
    construir el caso sin agregar API al agregado, se documenta y se cubre por el `EnsureActive` que
    `UpdateSettings` ya ejercita.
- **`Modules.Tenancy.UnitTests/SetTenantLogoValidatorTests.cs`** (nuevo): `FileId` vacío y
  `ExpectedVersion <= 0` marcan su campo.
- **`Bootstrapper.UnitTests/TenantLogoStorageTests.cs`** (nuevo), con `InMemoryFileResourceRepository`
  y `RecordingPublicObjectStorage` (`StorageTestDoubles.cs:11`, `:36`), al estilo de
  `PaymentProofPublisherTests.cs:21-132`: un caso por código `tenancy.logo.*`, `PublishAsync` copia
  bajo `tenants/{tid}/media/` y marca `PublicStorageKey`, `UnpublishAsync` borra la copia y deja el
  recurso `Deleted`, `UnpublishAsync` sobre uno ya `Deleted` no lanza, `GetUrl` devuelve `null` sin bucket.
- **`Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs`**: siguen verdes tras extraer
  `FilePublication` (cubren `PublishFileHandler` y `SoftDeleteFileHandler`).
- **`Modules.Tenancy.IntegrationTests/TenantLogoApiTests.cs`** (nuevo). Factory propia que sustituye
  `IObjectStorage` e `IPublicObjectStorage` por los dobles en memoria, como `StorageApiFactory`
  (`PaymentProofStorageHarness.cs:384-426`, `InMemoryPublicObjectStorage` en `:515-560`); los dobles son
  `internal` de ese proyecto y se copian, como ya hace `QuotationsApiHarness.cs:886`, `:955`.
  - `UploadCompleteAndAssignShowsTheLogoUrlInSettings` — sesión con `ownerType = "Tenant"`, `Upload` al
    doble (`StorageFlowTests.cs:40`), `complete`, `PUT /logo` con `If-Match`, `GET /settings` con
    `logo.url` que empieza por `InMemoryPublicObjectStorage.BaseUrl`; `ETag` nuevo.
  - `ReplacingTheLogoUnpublishesAndDeletesTheOldFile` — la clave vieja en `DeletedKeys` del doble;
    `GET /files/{old}` fuera de `Available`.
  - `RemovingTheLogoLeavesSettingsWithoutLogo` — `logo == null`, copia borrada, `Version` sube.
  - `AFileOfAnotherTenantIsRejected` → `422 tenancy.logo.file_not_found`; `ANonImageIsRejected` → `not_image`
    (un PDF `Tenant`); `AFileOverTwoMebibytesIsRejected` → `too_large`.
  - `StaleIfMatchIsRejected` → `412`, calcado de `TenantSettingsApiTests.cs:74-90`; `MissingIfMatchIsRejected` → `428`.
  - `AssigningWritesAuditAndOutbox` — `audit.entries` con `tenancy.logo.updated` y `platform.outbox_messages`
    con `tenancy.tenant-logo-updated.v1`, como `TenantSettingsApiTests.cs:92-135`.
- **`Modules.Quotations.UnitTests/QuotationPdfTests.cs`** (junto a `:18-57`): `APdfWithAnotherLogoIsStale`,
  `APdfWithTheSameLogoIsNotStale`, `APdfWithoutLogoIsStaleOnceTheTenantHasOne`, y la vuelta.
- **`Modules.Quotations.UnitTests/QCodePdfRendererTests.cs`** (con `NewRenderer`/`RequestCapture` de
  `:118-147`): `RenderSendsTheLogoAsAnAssetAndNamesItInData` — `assets["logo.png"]` es el base64 de los
  bytes y `data.logo.fileName == "logo.png"`; `RenderOmitsAssetsWithoutLogo` — sin propiedad `assets`
  y `data.logo == null`; `RenderDoesNotPutTheLogoBytesInData`.
- **`Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests`** (existente o nuevo): con y sin logo.
- **`Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs`** (junto a `:37-69`, armado en
  `:115-125` con un `StubQuotationLogoLookup` nuevo): `ChangingTheTenantLogoRegenerates` — segundo
  export con otro `FileId` en el stub → `renderer.Calls == 2`; `ExportingTwiceWithTheSameLogoDoesNotRegenerate`.
- **`Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs`** (nuevo: `QuotationExportApiTests.cs` cubre el export asíncrono a Excel, no el PDF): `ExportAfterAssigningALogoRegenerates`
  — `generatedAt` cambia tras el `PUT /logo`; el `StubPdfRenderer` (`QuotationsApiHarness.cs:752-757`)
  se amplía a contar llamadas o a registrar si el documento traía logo.
- **`ArchitectureTests`**: sin cambios esperados; correr igual, incluido `CompositionRootTests`.

## Orden de trabajo

Rama `feature/logo-del-tenant` desde `main`, en los dos repos: al 2026-09-19 `develop` está 25 commits
detrás de `main` en el backend y 21 en el frontend, y `main` es lo desplegado. Commits en este orden (conventional
commits, sin atribución):

1. `feat(storage): owner type Tenant y servicio FilePublication` — enum + extracción; tests de Storage verdes.
2. `feat(tenancy): el tenant guarda su logo` — dominio, evento, `OutboxWriter`, configuración EF,
   migración y snapshot. Tests unitarios de `Tenant` RED → GREEN.
3. `feat(tenancy): puerto ITenantLogoStorage y adaptador en Bootstrapper` — puerto, `TenantDirectory.GetLogoFileIdAsync`,
   `TenantLogoStorage`, tests del adaptador.
4. `feat(tenancy): PUT y DELETE del logo en settings` — comandos, handlers, validador, DTO con `logo`,
   endpoints, registro en DI. Integración RED → GREEN.
5. `feat(quotations): el PDF imprime el logo del tenant` — `QuotationPdfLogo`, mapper, renderer con `assets`,
   plantilla, `IQuotationLogoLookup` + adaptador. Unit tests del renderer y del mapper.
6. `feat(quotations): la caché del PDF se invalida por logo` — `QuotationPdf.LogoFileId`, provider, migración.
   Tests de `QuotationPdf`, del handler de export y la integración.
7. `docs: logo del tenant` — README (contrato de `/settings`, prerequisito del bucket público) y los
   comentarios de `quotation.typ` que hoy afirman que no hay logo.
8. Suite completa + `ArchitectureTests`.

## Entrega

Ambas migraciones agregan columnas **nulas**: una API vieja contra la base migrada sigue funcionando,
y la API nueva las aplica al arrancar por los inicializadores de módulo (`TenancyDatabaseInitializer.cs`
y `QuotationsDatabaseInitializer.cs`). No hay compuerta de orden entre migración y API más allá de la habitual.

Sí hay dos prerequisitos de ambiente: `Storage:R2:PublicBucket` y `Storage:R2:PublicBaseUrl` deben estar
configurados donde se quiera asignar logos (`README.md:127`; en producción ya lo están,
`k8s/prod-configMap.yaml:51-52`). `qcode-pdf` ya acepta `assets` (ver Quotations); no hay nada que
desplegar de ese lado.

Backend primero, frontend después: el frontend nuevo contra el backend viejo recibe `422 storage.file.owner_type_invalid`
al abrir la sesión de subida y `404` en `PUT /logo`; ninguno pierde datos, pero la pantalla no sirve.
La dirección inversa es benigna: el frontend viejo ignora `logo` en la respuesta.

Ramas al momento de escribir esto: `qep-backend` en `main`, `qep-frontend` en `main`
(`git branch --show-current` en cada uno). Verificar de nuevo antes de commitear.

## Fuera de alcance

- Marca en las pantallas de login y registro (sin sesión no hay `GET /settings`; sigue en `VITE_TENANT_BRAND_*`).
- Color de marca, tema o tipografía del tenant, en la app o en el PDF.
- SVG.
- Invalidar la caché del PDF cuando cambia la plantilla.
- Más de un logo (claro/oscuro) o un logo por documento.
- Logo en las plantillas de WhatsApp y correo.
- Barrido automático de archivos `Tenant` subidos y nunca asignados.
- Un `GetManyAsync` en `IFileResourceRepository`: el logo se lee de a uno.
