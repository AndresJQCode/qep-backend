# Logo del tenant — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un tenant puede subir su logo (`Storage`), asignarlo desde `Tenancy` (`PUT`/`DELETE /settings/logo`) y verlo impreso en el PDF de sus cotizaciones (`Quotations`), con la caché del PDF invalidándose cuando el logo cambia.

**Architecture:** El logo entra por el pipeline de `Storage` sin cambios de forma (`POST /files` con `ownerType = Tenant`, `PUT` prefirmado, `complete`); un puerto nuevo de `Tenancy` (`ITenantLogoStorage`), implementado en `Bootstrapper`, publica/despublica ese archivo en el bucket público de `Storage` sin pasar por los handlers ni el dispatcher de `Storage`. `Tenant` gana `LogoFileId`/`LogoPublicKey` y dos comandos nuevos bajo `/settings/logo`. `Quotations` lee el logo por un segundo puerto (`IQuotationLogoLookup`, adaptado en `Bootstrapper`) que baja los bytes del original privado y se los pasa a `qcode-pdf` como `assets`; `QuotationPdf` compara `LogoFileId` además de `Version` para decidir si regenera.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `rollForward: latestPatch`), EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`), Typst vía `qcode-pdf`.

**Spec:** `docs/superpowers/specs/2026-09-19-logo-del-tenant-design.md` (backend, autoridad de este plan) y `../qep-frontend/docs/superpowers/specs/2026-09-19-logo-del-tenant-design.md` (contraparte, sólo contexto de contrato).

## Global Constraints

- **TDD estricto: RED antes que GREEN, con evidencia literal de ambos**, salvo donde se indica explícitamente que un paso es un refactor de comportamiento preservado (Tasks 2 y 6, donde el "RED" es la suite existente compilando en rojo por la firma nueva, no un caso nuevo).
- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`** por archivo bloqueado (`Api.csproj` no declara `AssemblyName`, así que el proceso se llama `Api.exe`). Antes de cada corrida: `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- **La suite de integración usa Testcontainers: exige Docker corriendo y tarda decenas de minutos.** Durante el ciclo se corren sólo las clases de prueba tocadas, con `--filter "FullyQualifiedName~<Clase>"`; la suite completa se corre **una vez**, al final (Task 14). Siempre en primer plano, nunca en background.
- **Migraciones: factory de diseño, nunca `--startup-project`.** `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`:
  `dotnet ef migrations add <Nombre> --project <proyecto .Infrastructure> --context <Modulo>DbContext -o Persistence/Migrations`.
  Este plan agrega dos: `AddTenantLogo` (Task 4, `Modules.Tenancy.Infrastructure`, `TenancyDbContext`) y `AddQuotationPdfLogo` (Task 12, `Modules.Quotations.Infrastructure`, `QuotationsDbContext`). Ningún `.Designer.cs` histórico se toca.
- **Commits: Conventional Commits, sin atribución de IA (nunca `Co-Authored-By`).** Este plan desdobla 4 de los 6 commits del spec en un commit interino + el commit final con el mensaje **exacto** del spec, para que cada tarea cierre con su propio commit (regla de esta sesión) y el historial siga terminando en los 8 mensajes que el spec nombra en su "Orden de trabajo". La tabla de Entrega marca cuál tarea lleva el mensaje literal del spec. Cada commit va con el guard de rama y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`.
- **Comentarios en español neutro tuteando, sin voseo** (regla del `CLAUDE.md` de `qep-backend`, sin importar el estilo de salida del asistente); identificadores, códigos de error y nombres de prueba en inglés.
- **Contrato HTTP nuevo, tal como lo fija el spec:** `logo: { fileId: guid, url: string | null } | null` dentro de `TenantSettingsResponse`; `PUT /api/v1/tenants/{tenantId}/settings/logo` `{ fileId }` con `If-Match`; `DELETE` del mismo path, mismo `If-Match`. Ningún otro campo del contrato de `/settings` cambia.
- **Códigos de error nuevos, exactos:** `tenancy.logo.file_not_found`, `tenancy.logo.not_owned`, `tenancy.logo.not_available`, `tenancy.logo.not_image`, `tenancy.logo.too_large` — los cinco `422` vía `TenantDomainException` desde el adaptador de Bootstrapper. Ningún código nuevo fuera de esta lista.
- **Límites del logo:** sólo `image/png`, `image/jpeg`, `image/webp`; tamaño máximo 2 MiB (`2 * 1024 * 1024` bytes), validados en el adaptador **además** de `FileUploadPolicy` (25 MiB). `FileOwnerType` gana `Tenant = 6`, persistido **por nombre**.
- **`StorageKey` sigue `internal`** (`Modules.Storage.Application`): la extracción de `FilePublication` no lo hace público.
- **Orden de publicación/retiro (decisiones 7 y 8 del spec):** reemplazar publica el archivo nuevo *antes* y commitea Tenancy; recién después retira el viejo (mejor esfuerzo, con `ILogger`, sin fallar el request). Quitar el logo retira la copia pública *antes* de commitear Tenancy (idempotente).
- **Fuera de alcance** (spec): marca en login/registro, color de marca, SVG, invalidar la caché del PDF por cambio de plantilla, más de un logo, logo en WhatsApp/correo, barrido de archivos `Tenant` huérfanos, `GetManyAsync` en `IFileResourceRepository`. `qep-frontend` no se toca desde este plan.
- Todo se hace en el checkout `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`, rama `feature/logo-del-tenant` (ya existe al escribir este plan, ver Task 0). No hace falta worktree.
- Comandos en **Windows PowerShell 5.1**: sin `&&`, con `A; if ($?) { B }`, y `$env:VAR = "…"` en línea aparte. Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: un `using` sin uso se borra en la misma tarea. Archivos nuevos en UTF-8 sin BOM y con LF.
- **Nunca imprimir el valor de un secreto.** Ningún comando de este plan necesita uno; los dos prerequisitos de ambiente (`Storage:R2:PublicBucket`, `Storage:R2:PublicBaseUrl`) sólo se **verifica que existan** en Task 0, nunca se imprime su valor.

---

## Hallazgos contra el código (2026-09-19)

1. **El spec cita `QepServiceCollectionExtensions.cs:143-147` para "registrar a mano, como todos"**, pero ese rango es el comentario ilustrativo sobre el registro manual (el de tasas), no la ubicación real de los handlers de Tenancy. `GetTenantSettingsQuery`/`UpdateTenantSettingsCommand` se registran en `:55-60`. No es una discrepancia de fondo — el spec señala el patrón, no la línea — pero este plan usa la línea real.
2. **No existe una prueba de integración para `POST /{quotationId}/pdf` hoy.** El spec cita `Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs` para `ExportAfterAssigningALogoRegenerates`, pero ese archivo prueba la exportación **asíncrona a Excel** (`POST /export`, con `ExportJobWorker`), un feature distinto sin relación con el PDF. El único test existente del PDF es unitario (`ExportQuotationPdfHandlerTests.cs`). Este plan crea un archivo nuevo, `QuotationPdfExportApiTests.cs`, en la misma carpeta, reusando `QuotationsApiHarness` (que ya expone `StubPdfRenderer`/`StubPdfStorage`/`RegisterTenantAsync`/etc.) — el equivalente real más cercano a lo que el spec pedía.
3. **`PublishFileHandler` verifica `IPublicObjectStorage.IsConfigured` antes de cargar el `FileResource`**; al mover esa validación adentro de `FilePublication.PublishAsync` (que recibe el recurso ya cargado), un archivo inexistente con el bucket público sin configurar pasa a responder `storage.file.not_found` en vez de `storage.public.not_configured`. Ningún test de la suite hoy fija ese orden (`rg "storage.public.not_configured" tests/` no tiene resultados) — cambio de comportamiento menor y sin cobertura que se documenta en la Task 2, no se evita.
4. **`FilePublication` necesita su propio `IClock`** para poder cerrar `Task<string> PublishAsync(FileResource, CancellationToken)`/`Task UnpublishAsync(FileResource, CancellationToken)` con la firma exacta del spec (sin `occurredAt` como parámetro). Los handlers que la llaman conservan su propio `IClock` para `SoftDelete`/auditoría, así que dos llamados en el mismo request pueden leer el reloj con nanosegundos de diferencia — no observable con `FixedClock` en pruebas ni con ninguna aserción existente.
5. **`Modules.Tenancy.Application` no referencia `Microsoft.Extensions.Logging`** hoy. El spec deja abierta la decisión de si el mejor esfuerzo de "retirar el logo viejo" (paso 8 del handler) loguea desde el handler (sumando esa referencia) o desde el adaptador. Este plan elige **el adaptador** (`TenantLogoStorage.UnpublishAsync` ya recibe `ILogger<TenantLogoStorage>` para sus propios logs; el handler llama a un método adicional `TryUnpublishAsync` que nunca lanza) — la opción que el propio spec ofrece como alternativa sin sumar la referencia.
6. **No hay un tenant sembrado con id fijo salvo el primero.** `TenancyDatabaseInitializer.DevelopmentTenantId` (`01900000-0000-7000-8000-000000000001`) sólo siembra cuando la tabla `tenancy.tenants` está vacía; un segundo tenant para las pruebas de "archivo de otro tenant" no necesita existir como fila de `Tenant` — a `Storage` le alcanza con otro `X-Tenant-Id` en el header, sin FK entre `storage.file_resources` y `tenancy.tenants`.

## Entrega

| Commit (mensaje) | Tarea que lo produce |
| --- | --- |
| interino `feat(storage): FileOwnerType.Tenant para el logo del tenant` | 1 |
| **`feat(storage): owner type Tenant y servicio FilePublication`** (spec, ítem 1) | 2 |
| interino `feat(tenancy): Tenant.SetLogo y RemoveLogo con su evento de dominio` | 3 |
| **`feat(tenancy): el tenant guarda su logo`** (spec, ítem 2) | 4 |
| interino `feat(tenancy): puerto ITenantLogoStorage` | 5 |
| **`feat(tenancy): puerto ITenantLogoStorage y adaptador en Bootstrapper`** (spec, ítem 3) | 6 |
| interino `feat(tenancy): comandos y DTO para el logo de settings` | 7 |
| **`feat(tenancy): PUT y DELETE del logo en settings`** (spec, ítem 4) | 8 |
| interino `feat(quotations): QuotationPdfLogo en el documento, el mapper y el renderer` | 9 |
| **`feat(quotations): el PDF imprime el logo del tenant`** (spec, ítem 5) | 10 |
| interino `feat(quotations): QuotationPdf.LogoFileId invalida por logo` | 11 |
| **`feat(quotations): la caché del PDF se invalida por logo`** (spec, ítem 6) | 12 |
| **`docs: logo del tenant`** (spec, ítem 7) | 13 |
| `fix(...): …`, sólo si la verificación final lo pide | 14 |

La rama no se publica ni se mergea desde este plan.

---

### Task 0: Rama y baseline

**Files:**
- Ninguno de código.

**Interfaces:**
- Consumes: rama `feature/logo-del-tenant`, ya creada al momento de escribir este plan (ver `git status` de la sesión).
- Produces: confirmación de herramientas, bucket público configurado, y los números de prueba base (handoff) contra los que se comparan las Tasks 6, 8, 10, 12 y 14.

- [ ] **Step 1: Confirmar la rama y el estado del árbol**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git status --short
git log -1 --oneline
```

Esperado: `feature/logo-del-tenant`; `git status` vacío (el plan mismo no se commitea desde acá); el último commit es `c34af17 docs: diseño del logo del tenant` o posterior. Si la rama es otra, **para y pregunta** antes de seguir — no crear una rama nueva a mitad de este plan.

- [ ] **Step 2: Herramientas y specs**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Test-Path docs\superpowers\specs\2026-09-19-logo-del-tenant-design.md
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: `True`; `Get-Process` sin salida; `docker info` con una versión; `dotnet ef` con una versión 10.x. Si `dotnet ef` no está: `dotnet tool install --global dotnet-ef`.

- [ ] **Step 3: Verificar (sin imprimir valores) los dos prerequisitos de ambiente**

El spec exige `Storage:R2:PublicBucket` y `Storage:R2:PublicBaseUrl` configurados para asignar logos en cualquier ambiente que no sea la suite de pruebas (que sustituye `IPublicObjectStorage` por un doble). Esto sólo importa para correr la API real en local, no para las pruebas de este plan — se verifica su **existencia**, nunca su valor:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet user-secrets list --project src/Api | Select-String -Pattern "Storage:R2:PublicBucket|Storage:R2:PublicBaseUrl" | Measure-Object | Select-Object -ExpandProperty Count
```

Esperado: `2`, si se va a levantar la API local para probar a mano. Si da menos de 2, no bloquea este plan (las pruebas automatizadas no lo necesitan) — se anota en el handoff para quien despliegue.

- [ ] **Step 4: Restore, build y baseline de pruebas unitarias**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`; las cinco corridas con `Failed: 0`. Anota en el handoff el número de pruebas correctas de cada una — son el baseline de las Tasks 6, 8, 10, 12 y 14.

---

### Task 1: `FileOwnerType.Tenant`

Agrega el valor de enum que el logo necesita como dueño del archivo. Se persiste por nombre
(`HasConversion<string>()` en `StorageDbContext`), así que no hace falta migración.

**Files:**
- Modify: `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs:19-33`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FileResourceTests.cs`

**Interfaces:**
- Consumes: `FileResource.CreatePendingUpload(FileResourceId, Guid, Guid, FileOwnerType, string, string, long, string, DateTimeOffset)` (existente, sin cambio de firma).
- Produces: `Modules.Storage.Domain.FileOwnerType.Tenant = 6`.

- [ ] **Step 1: Escribir la prueba que falla**

Agrega en `tests/Modules/Storage/Modules.Storage.UnitTests/FileResourceTests.cs`, después de `NewPendingImage`:

```csharp
    [Fact]
    public void CreatePendingUploadAcceptsTenantAsOwnerType()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.Tenant,
            "logo.png", "image/png", 1024, "staging/tenant-logo", DateTimeOffset.UtcNow);

        Assert.Equal(FileOwnerType.Tenant, resource.OwnerType);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --filter "FullyQualifiedName~Modules.Storage.UnitTests.FileResourceTests.CreatePendingUploadAcceptsTenantAsOwnerType"
```

Esperado: **no compila**. `CS0117: 'FileOwnerType' no contiene una definición para 'Tenant'` (en inglés si el SDK está en inglés). Pega la salida en el handoff.

- [ ] **Step 3: Agregar el valor**

En `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs`, reemplaza el bloque `:19-33` por:

```csharp
public enum FileOwnerType
{
    User = 1,
    Entity = 2,
    System = 3,

    // CAT-05: un archivo puede pertenecer a un producto del catálogo. Antes quedaba guardado como
    // User, porque el endpoint caía en silencio a ese valor cuando el string no parseaba.
    Product = 4,

    // Spec 2026-09-16, D2: un comprobante de pago de un pedido. Espera en staging/ hasta que se
    // adjunta (D4) y ahí se mueve al bucket público (D3). Los comprobantes subidos antes de v2
    // siguen siendo User y no cambian (D13).
    PaymentProof = 5,

    // Spec 2026-09-19: el logo de un tenant. Sube por el mismo pipeline que cualquier imagen
    // (POST /files, PUT prefirmado, complete) y Tenancy lo asigna después con su propio endpoint
    // — ver ITenantLogoStorage. OwnerId es el tenantId.
    Tenant = 6
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --filter "FullyQualifiedName~Modules.Storage.UnitTests.FileResourceTests"
```

Esperado: `Failed: 0`, con un caso más que el baseline de Task 0.

- [ ] **Step 5: Build de toda la solución**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`.

- [ ] **Step 6: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs tests/Modules/Storage/Modules.Storage.UnitTests/FileResourceTests.cs
git commit -m "feat(storage): FileOwnerType.Tenant para el logo del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con dos archivos; el `Select-String` sin salida.

---

### Task 2: `FilePublication` — extraer la copia+rollback de `PublishFileHandler`/`SoftDeleteFileHandler`

Decisión 5 del spec: la lógica de copiar el original y sus variantes al bucket público (con
rollback si una copia falla) se extrae a un servicio público de `Modules.Storage.Application`,
que usan `PublishFileHandler`, `UnpublishFileHandler` y `SoftDeleteFileHandler` **y** el
adaptador nuevo de Tenancy (Task 6). Es un refactor de comportamiento preservado: no hay caso de
negocio nuevo, así que el "RED" de este task es la suite existente rota por la firma nueva de los
tres handlers, no un caso nuevo.

**Files:**
- Create: `src/Modules/Storage/Modules.Storage.Application/FilePublication.cs`
- Modify: `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs` (`PublishFileHandler` y `UnpublishFileHandler` completos)
- Modify: `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs` (`SoftDeleteFileHandler` completo)
- Modify: `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs:38-39`
- Modify: `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs`
- Test: `tests/Modules/Storage/Modules.Storage.UnitTests/FilePublicationTests.cs` (nuevo)

**Interfaces:**
- Consumes: `IPublicObjectStorage` (`Modules.Storage.Application`), `IClock` (`BuildingBlocks.Application`), `FileResource.EnsureDownloadable()`/`Publish(string, DateTimeOffset)`/`Unpublish(DateTimeOffset)` (existentes), `StorageKey.PublicFor`/`PublicVariantFor` (`internal`, mismo assembly).
- Produces:

```csharp
namespace Modules.Storage.Application;

public sealed class FilePublication(IPublicObjectStorage publicStorage, IClock clock)
{
    public Task<string> PublishAsync(FileResource resource, CancellationToken cancellationToken);
    public Task UnpublishAsync(FileResource resource, CancellationToken cancellationToken);
}
```

  `PublishFileHandler`/`UnpublishFileHandler`/`SoftDeleteFileHandler` ganan un parámetro
  `FilePublication filePublication` en su constructor primario (posición: justo después de
  `IStorageUnitOfWork unitOfWork`). `PublishFileHandler` y `UnpublishFileHandler` **conservan**
  `IPublicObjectStorage publicStorage` porque lo siguen necesitando para `resource.ToDto(publicStorage)`.

- [ ] **Step 1: Escribir las pruebas de `FilePublication` que fallan (no compilan)**

Crea `tests/Modules/Storage/Modules.Storage.UnitTests/FilePublicationTests.cs`:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// La copia del original y sus variantes al bucket público, con rollback si una copia falla
/// (decisión 5 del spec 2026-09-19). Extraído de `PublishFileHandler`/`SoftDeleteFileHandler`
/// para que el adaptador de logo del tenant (Bootstrapper) lo use sin pasar por esos handlers.
/// </summary>
public sealed class FilePublicationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishRejectsWhenThePublicBucketIsNotConfigured()
    {
        var resource = AvailableImage();
        var publication = new FilePublication(new UnconfiguredPublicObjectStorage(), new FixedClock(Now));

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            publication.PublishAsync(resource, TestContext.Current.CancellationToken));

        Assert.Equal("storage.public.not_configured", error.Code);
        Assert.Null(resource.PublicStorageKey);
    }

    [Fact]
    public async Task PublishCopiesTheOriginalAndMarksThePublicKey()
    {
        var resource = AvailableImage();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        var publicKey = await publication.PublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(resource.PublicStorageKey, publicKey);
        Assert.Equal(resource.StorageKey, storage.Copies[publicKey]);
    }

    [Fact]
    public async Task PublishRollsBackTheOriginalCopyWhenAVariantCopyFails()
    {
        var resource = AvailableImage();
        resource.AddVariant("thumb", $"{resource.StorageKey}/variants/thumb.png", "image/png", 200, 200, 512);
        var storage = new FailingOnVariantPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publication.PublishAsync(resource, TestContext.Current.CancellationToken));

        Assert.Empty(storage.Copies);
    }

    [Fact]
    public async Task UnpublishDeletesTheOriginalAndEachVariantCopy()
    {
        var resource = AvailableImage();
        resource.AddVariant("thumb", $"{resource.StorageKey}/variants/thumb.png", "image/png", 200, 200, 512);
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));
        var publicKey = await publication.PublishAsync(resource, TestContext.Current.CancellationToken);

        await publication.UnpublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Contains(publicKey, storage.DeletedKeys);
        Assert.Equal(2, storage.DeletedKeys.Count);
        Assert.Null(resource.PublicStorageKey);
    }

    [Fact]
    public async Task UnpublishWithoutAPublicKeyIsANoOp()
    {
        var resource = AvailableImage();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        await publication.UnpublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Empty(storage.DeletedKeys);
    }

    private static FileResource AvailableImage()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.User,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        resource.CompleteUpload("checksum", 2048, Now);
        resource.Promote($"files/tenants/{TenantId:N}/logo", Now);
        return resource;
    }

    private sealed class UnconfiguredPublicObjectStorage : IPublicObjectStorage
    {
        public bool IsConfigured => false;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Copia el original y falla en la primera variante, para ejercer el rollback: la copia del
    // original queda en `Copies` hasta el catch, que la borra por su cuenta.
    private sealed class FailingOnVariantPublicObjectStorage : IPublicObjectStorage
    {
        public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            if (privateKey.Contains("/variants/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure copying a variant.");
            }

            Copies[publicKey] = privateKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            Copies.Remove(publicKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --filter "FullyQualifiedName~Modules.Storage.UnitTests.FilePublicationTests"
```

Esperado: **no compila**. `CS0246: no se encontró el tipo o el nombre del espacio de nombres 'FilePublication'`. Pega la salida en el handoff.

- [ ] **Step 3: Crear `FilePublication`**

Crea `src/Modules/Storage/Modules.Storage.Application/FilePublication.cs`:

```csharp
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// Copia un recurso (y sus variantes) al bucket público, o retira esas copias. Extraído de
/// `PublishFileHandler`/`SoftDeleteFileHandler` (decisión 5 del spec 2026-09-19) para que el
/// adaptador de logo del tenant en Bootstrapper lo use sin pasar por esos handlers ni por sus
/// permisos — publicar el logo lo autoriza `tenancy.settings.update`, no `storage.file.publish`.
///
/// No decide nada de autorización ni de auditoría: eso se queda en cada handler que lo llama, que
/// es quien sabe quién y por qué. Tampoco decide el `now` de sus llamadores: tiene su propio
/// `IClock` para poder cerrar su firma sin un parámetro `occurredAt` — dos llamados del mismo
/// request pueden diferir en microsegundos frente al `now` que el handler usa para auditoría o
/// para `SoftDelete`, algo que ningún caso de uso observa hoy.
/// </summary>
public sealed class FilePublication(IPublicObjectStorage publicStorage, IClock clock)
{
    public async Task<string> PublishAsync(FileResource resource, CancellationToken cancellationToken)
    {
        if (!publicStorage.IsConfigured)
        {
            throw new StorageDomainException(
                "storage.public.not_configured",
                "Public image storage is not configured.");
        }

        resource.EnsureDownloadable();
        var publicKey = resource.PublicStorageKey ?? StorageKey.PublicFor(
            resource.TenantId, resource.Id, resource.Name);
        // Validar todos los invariantes de publicación (imagen, clave) antes de crear cualquier
        // objeto público.
        resource.Publish(publicKey, clock.UtcNow);
        var copiedKeys = new List<string>();

        try
        {
            await publicStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
            copiedKeys.Add(publicKey);
            foreach (var variant in resource.Variants)
            {
                var variantKey = StorageKey.PublicVariantFor(publicKey, variant);
                await publicStorage.CopyFromPrivateAsync(variant.StorageKey, variantKey, cancellationToken);
                copiedKeys.Add(variantKey);
            }
        }
        catch
        {
            foreach (var key in copiedKeys)
            {
                try { await publicStorage.DeleteAsync(key, CancellationToken.None); }
                catch { /* best-effort rollback; retrying publish is safe */ }
            }
            throw;
        }

        return publicKey;
    }

    public async Task UnpublishAsync(FileResource resource, CancellationToken cancellationToken)
    {
        if (resource.PublicStorageKey is not { } publicKey)
        {
            return;
        }

        await publicStorage.DeleteAsync(publicKey, cancellationToken);
        foreach (var variant in resource.Variants)
        {
            await publicStorage.DeleteAsync(
                StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
        }

        resource.Unpublish(clock.UtcNow);
    }
}
```

- [ ] **Step 4: `PublishFileHandler`/`UnpublishFileHandler` llaman a `FilePublication`**

Reemplaza el archivo entero `src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record PublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed record UnpublishFileCommand(Guid TenantId, Guid FileId) : ICommand<FileResourceDto>;

public sealed class PublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<PublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        PublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);

        var resource = await LoadAsync(repository, command.TenantId, command.FileId, cancellationToken);
        // Spec 2026-09-16, D15: un comprobante sólo llega al público por el movimiento. Por acá
        // copiaría desde su temporal, que después de moverse ya no existe.
        PaymentProofGuard.EnsureNotPaymentProof(resource);

        var publicKey = await filePublication.PublishAsync(resource, cancellationToken);

        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return resource.ToDto(publicStorage);
    }

    internal static async Task<FileResource> LoadAsync(
        IFileResourceRepository repository, Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new ResourceNotFoundException("storage.file.not_found", "The file resource was not found.");
        }
        return resource;
    }
}

public sealed class UnpublishFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<UnpublishFileCommand, FileResourceDto>
{
    public async Task<FileResourceDto> HandleAsync(
        UnpublishFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FilePublish);
        var resource = await PublishFileHandler.LoadAsync(
            repository, command.TenantId, command.FileId, cancellationToken);

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        if (resource.PublicStorageKey is not null)
        {
            await filePublication.UnpublishAsync(resource, cancellationToken);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "storage.file.unpublished",
                resource.Id.ToString(), "success", clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return resource.ToDto(publicStorage);
    }
}
```

- [ ] **Step 5: `SoftDeleteFileHandler` llama a `FilePublication`**

Reemplaza el archivo entero `src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record SoftDeleteFileCommand(Guid TenantId, Guid FileResourceId)
    : ICommand<SoftDeleteResult>;

public sealed class SoftDeleteFileHandler(
    IFileResourceRepository repository,
    IStorageUnitOfWork unitOfWork,
    FilePublication filePublication,
    IEnumerable<IFileReferenceProbe> fileReferenceProbes,
    IStorageAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SoftDeleteFileCommand, SoftDeleteResult>
{
    public async Task<SoftDeleteResult> HandleAsync(
        SoftDeleteFileCommand command,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, StoragePermissions.FileDelete);

        var resource = await repository.GetAsync(
            new FileResourceId(command.FileResourceId), cancellationToken);
        if (resource is null || resource.TenantId != command.TenantId)
        {
            throw new ResourceNotFoundException(
                "storage.file.not_found", "The file resource was not found.");
        }

        // Spec 2026-09-16, D15: antes de tocar el bucket. La copia pública de un comprobante adjunto
        // es la que enlaza el Excel.
        await PaymentProofGuard.EnsureNotReferencedAsync(resource, fileReferenceProbes, cancellationToken);

        await filePublication.UnpublishAsync(resource, cancellationToken);
        // Borrado lógico; el objeto se retiene hasta que pase la ventana de retención.
        resource.SoftDelete(clock.UtcNow);

        auditPublisher.Publish(
            resource.TenantId,
            executionContext.SubjectId,
            "storage.file.deleted",
            resource.Id.ToString(),
            "success",
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SoftDeleteResult(true);
    }
}
```

- [ ] **Step 6: Registrar `FilePublication` en DI**

En `src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs`, después
de la línea `services.AddScoped<IStorageAuditPublisher, StorageAuditPublisher>();` (justo debajo de
`:40`, antes del comentario de `IUserReferenceProbe`), agrega:

```csharp
        // Decisión 5 del spec 2026-09-19: copiar+rollback al bucket público, compartido por
        // PublishFileHandler/SoftDeleteFileHandler y por el adaptador de logo del tenant
        // (Bootstrapper). Scoped como el repositorio: sin estado propio entre requests.
        services.AddScoped<FilePublication>();
```

- [ ] **Step 7: Actualizar los constructores en `PaymentProofFileManagementTests.cs`**

En `tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs`:

Reemplaza `DeleteHandler` (líneas 204-216) por:

```csharp
    private static SoftDeleteFileHandler DeleteHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            new FilePublication(storage, new FixedClock(Now)),
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
```

Reemplaza `UnpublishHandler` (líneas 218-230) por:

```csharp
    private static UnpublishFileHandler UnpublishHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            new FilePublication(storage, new FixedClock(Now)),
            storage,
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
```

En `PublishingAPaymentProofIsAlwaysRejected` (líneas 124-142), reemplaza la construcción de
`PublishFileHandler`:

```csharp
        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            new PublishFileHandler(
                    new InMemoryFileResourceRepository(proof),
                    new CountingStorageUnitOfWork(),
                    new FilePublication(storage, new FixedClock(Now)),
                    storage,
                    new RecordingStorageAuditPublisher(),
                    new AllowAllExecutionContext(TenantId),
                    new FixedClock(Now))
                .HandleAsync(new PublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));
```

- [ ] **Step 8: Correr y ver el GREEN — `FilePublicationTests` y la suite existente**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --filter "FullyQualifiedName~Modules.Storage.UnitTests.FilePublicationTests"
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --filter "FullyQualifiedName~Modules.Storage.UnitTests.PaymentProofFileManagementTests"
```

Esperado: `FilePublicationTests` con `Failed: 0` (5 casos); `PaymentProofFileManagementTests` con
`Failed: 0` y el mismo número de casos que antes de este task — el refactor no cambia comportamiento
observable para esas pruebas.

- [ ] **Step 9: Suite de Storage completa (unitaria) + build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --no-build
```

Esperado: build con `0 Errores`; suite con `Failed: 0`.

- [ ] **Step 10: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Storage/Modules.Storage.Application/FilePublication.cs src/Modules/Storage/Modules.Storage.Application/SetFilePublication.cs src/Modules/Storage/Modules.Storage.Application/SoftDeleteFile.cs src/Modules/Storage/Modules.Storage.Infrastructure/StorageInfrastructureExtensions.cs tests/Modules/Storage/Modules.Storage.UnitTests/FilePublicationTests.cs tests/Modules/Storage/Modules.Storage.UnitTests/PaymentProofFileManagementTests.cs
git commit -m "feat(storage): owner type Tenant y servicio FilePublication"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con seis archivos; el `Select-String` sin salida.

---

### Task 3: `Tenant.SetLogo`/`RemoveLogo` y `TenantLogoUpdatedDomainEvent`

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantLogoUpdatedDomainEvent.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (después de `DateFormat`, y después de `UpdateSettings`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs`

**Interfaces:**
- Consumes: `TenantDomainException(string code, string message)`, `IDomainEvent` (`BuildingBlocks.Domain`), `Tenant.EnsureActive()` (`private`, existente).
- Produces (namespace `Modules.Tenancy.Domain`):

```csharp
public sealed record TenantLogoUpdatedDomainEvent(
    Guid EventId, DateTimeOffset OccurredAt, TenantId TenantId, long Version, Guid? LogoFileId)
    : IDomainEvent;

public sealed class Tenant
{
    public Guid? LogoFileId { get; private set; }
    public string? LogoPublicKey { get; private set; }
    public bool SetLogo(Guid fileId, string publicKey, DateTimeOffset occurredAt);
    public bool RemoveLogo(DateTimeOffset occurredAt);
}
```

- [ ] **Step 1: Escribir las pruebas que fallan**

Primero lee `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs` completo para copiar
el fixture (`TenantId`, `Now`, el helper `Create`/`NewActiveTenant`) — este plan no repite ese
fixture porque el archivo puede tener nombres propios. Agrega, al final de la clase, antes de la
llave de cierre:

```csharp
    // Decisión 3 del spec 2026-09-19: el mismo agregado guarda el archivo y la clave pública
    // (la URL no se guarda: se arma al leer con la base pública vigente).
    [Fact]
    public void SetLogoIncrementsVersionAndRaisesTheEvent()
    {
        var tenant = NewActiveTenant();
        var fileId = Guid.CreateVersion7();

        var changed = tenant.SetLogo(fileId, "tenants/x/media/y/original.png", Now.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(fileId, tenant.LogoFileId);
        Assert.Equal("tenants/x/media/y/original.png", tenant.LogoPublicKey);
        Assert.Equal(2, tenant.Version);
        var domainEvent = Assert.IsType<TenantLogoUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(fileId, domainEvent.LogoFileId);
        Assert.Equal(2, domainEvent.Version);
    }

    [Fact]
    public void SetLogoWithTheSameFileIsANoOp()
    {
        var tenant = NewActiveTenant();
        var fileId = Guid.CreateVersion7();
        tenant.SetLogo(fileId, "tenants/x/media/y/original.png", Now.AddMinutes(1));
        tenant.PullDomainEvents();

        var changed = tenant.SetLogo(fileId, "tenants/x/media/y/original.png", Now.AddMinutes(2));

        Assert.False(changed);
        Assert.Equal(2, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    [Fact]
    public void RemoveLogoClearsBothFieldsAndRaisesTheEventWithNull()
    {
        var tenant = NewActiveTenant();
        var fileId = Guid.CreateVersion7();
        tenant.SetLogo(fileId, "tenants/x/media/y/original.png", Now.AddMinutes(1));
        tenant.PullDomainEvents();

        var changed = tenant.RemoveLogo(Now.AddMinutes(2));

        Assert.True(changed);
        Assert.Null(tenant.LogoFileId);
        Assert.Null(tenant.LogoPublicKey);
        Assert.Equal(3, tenant.Version);
        var domainEvent = Assert.IsType<TenantLogoUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Null(domainEvent.LogoFileId);
    }

    [Fact]
    public void RemoveLogoWithoutLogoIsANoOp()
    {
        var tenant = NewActiveTenant();

        var changed = tenant.RemoveLogo(Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(1, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    // SetLogoOnAnInactiveTenantIsRejected: no hay forma pública de desactivar un Tenant hoy
    // (TenantStatus sólo lo pone el constructor, Tenant.cs:36), así que este caso no se puede
    // construir sin agregar API al agregado sólo para la prueba — el spec documenta esto
    // explícitamente y lo deja cubierto por el EnsureActive que UpdateSettings ya ejercita
    // (UpdateSettingsOnAnInactiveTenantIsRejected o equivalente, si existe en este archivo).
```

Si `TenantTests.cs` no tiene un helper `NewActiveTenant` con ese nombre exacto, usa el que el
archivo ya tenga (mismo patrón que `Tenant.Create(...)` con los seis parámetros) y ajusta las
cuatro pruebas de arriba a ese nombre.

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.TenantTests"
```

Esperado: **no compila**. `CS1061: 'Tenant' no contiene una definición para 'SetLogo'` (y
`'RemoveLogo'`, `'LogoFileId'`) y `CS0246` para `TenantLogoUpdatedDomainEvent`. Pega la salida en
el handoff.

- [ ] **Step 3: Crear el evento de dominio**

Crea `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantLogoUpdatedDomainEvent.cs`:

```csharp
using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// `LogoFileId = null` significa "se quitó el logo": no hay un evento aparte para no duplicar el
/// mapeo de <c>OutboxWriter</c> (decisión 11 del spec 2026-09-19). No reusa
/// <see cref="TenantSettingsUpdatedDomainEvent"/> porque <c>TenantSettingsChangeLogProjection</c>
/// lo proyectaría como un cambio de configuración, y el nombre del evento mentiría.
/// </summary>
public sealed record TenantLogoUpdatedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    TenantId TenantId,
    long Version,
    Guid? LogoFileId) : IDomainEvent;
```

- [ ] **Step 4: `Tenant.LogoFileId`/`LogoPublicKey`, `SetLogo`, `RemoveLogo`**

En `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs`, después de la propiedad `DateFormat`
(línea 54), agrega:

```csharp
    /// <summary>
    /// El archivo del logo en Storage, o null sin logo. La URL pública **no** se guarda acá: se
    /// arma al leer con <see cref="LogoPublicKey"/> y la base pública configurada (decisión 3 del
    /// spec 2026-09-19) — igual que <c>FileResourceDto.PublicUrl</c>. `Tenant` no conoce
    /// `FileResource`: los límites de tipo y tamaño del logo los valida el adaptador que sí lo
    /// tiene a mano (<c>ITenantLogoStorage</c>).
    /// </summary>
    public Guid? LogoFileId { get; private set; }

    /// <summary>La clave pública en el bucket, sólo para armar la URL sin volver a preguntarle a
    /// Storage en cada <c>GET /settings</c>.</summary>
    public string? LogoPublicKey { get; private set; }
```

Después del método `UpdateSettings` (línea 113, antes de `PullDomainEvents`), agrega:

```csharp
    /// <summary>
    /// Asigna el logo. `false` sin cambios (mismo `fileId` ya vigente) — el caller no sube
    /// `Version` ni escribe auditoría en ese caso. `publicKey` vacía es un error del adaptador, no
    /// de la persona que sube el archivo: por eso `ArgumentException` y no un código de dominio.
    /// </summary>
    public bool SetLogo(Guid fileId, string publicKey, DateTimeOffset occurredAt)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        if (LogoFileId == fileId)
        {
            return false;
        }

        LogoFileId = fileId;
        LogoPublicKey = publicKey;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new TenantLogoUpdatedDomainEvent(
            Guid.CreateVersion7(), occurredAt, Id, Version, LogoFileId));

        return true;
    }

    /// <summary>`false` si el tenant ya no tenía logo.</summary>
    public bool RemoveLogo(DateTimeOffset occurredAt)
    {
        EnsureActive();

        if (LogoFileId is null)
        {
            return false;
        }

        LogoFileId = null;
        LogoPublicKey = null;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new TenantLogoUpdatedDomainEvent(
            Guid.CreateVersion7(), occurredAt, Id, Version, null));

        return true;
    }
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.TenantTests"
```

Esperado: `Failed: 0`, con cuatro casos más que el baseline (`SetLogoIncrementsVersionAndRaisesTheEvent`,
`SetLogoWithTheSameFileIsANoOp`, `RemoveLogoClearsBothFieldsAndRaisesTheEventWithNull`,
`RemoveLogoWithoutLogoIsANoOp`).

- [ ] **Step 6: Build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`. `TenancyDbContext` todavía no mapea `LogoFileId`/
`LogoPublicKey` — eso es la Task 4 — pero EF no exige mapeo explícito para compilar; sólo fallaría
en runtime, y este task no levanta la base.

- [ ] **Step 7: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Tenancy/Modules.Tenancy.Domain/TenantLogoUpdatedDomainEvent.cs src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs
git commit -m "feat(tenancy): Tenant.SetLogo y RemoveLogo con su evento de dominio"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con tres archivos; el `Select-String` sin salida.

---

### Task 4: EF, migración `AddTenantLogo` y `OutboxWriter`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:37-72` (`ConfigureTenant`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OutboxWriter.cs:14-25`
- Create (generado por `dotnet ef`): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddTenantLogo.cs` y `.Designer.cs`; modificado: `TenancyDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: `Tenant.LogoFileId`/`LogoPublicKey` (Task 3), `TenancyDbContext` (público).
- Produces: columnas `tenancy.tenants.logo_file_id uuid NULL` y `logo_public_key character varying(512) NULL`; el evento `TenantLogoUpdatedDomainEvent` mapeado a `"tenancy.tenant-logo-updated.v1"`.

- [ ] **Step 1: Configurar las dos columnas nuevas en `TenancyDbContext`**

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs`, dentro de
`ConfigureTenant`, después de `tenant.Property(value => value.DateFormat)...` (línea 65) y antes de
`tenant.Property(value => value.Version)...`, agrega:

```csharp
        // 512 es el largo de storage.file_resources.public_storage_key (StorageDbContext.cs:56):
        // la clave que se guarda acá es esa misma.
        tenant.Property(value => value.LogoFileId).HasColumnName("logo_file_id");
        tenant.Property(value => value.LogoPublicKey)
            .HasColumnName("logo_public_key")
            .HasMaxLength(512);
```

- [ ] **Step 2: Mapear el evento en `OutboxWriter`**

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OutboxWriter.cs`, en el `switch`
de `:14-24`, agrega un caso nuevo antes del `_ =>` final:

```csharp
            TenantLogoUpdatedDomainEvent => "tenancy.tenant-logo-updated.v1",
```

Sin esta línea el handler de la Task 8 lanza `InvalidOperationException` en runtime y el `PUT`
responde 500 — el mismo modo de falla documentado en `:23-24`.

- [ ] **Step 3: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddTenantLogo --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Esperado: `Done.` y tres archivos escritos: `<timestamp>_AddTenantLogo.cs`,
`<timestamp>_AddTenantLogo.Designer.cs`, `TenancyDbContextModelSnapshot.cs` modificado. Abre la
migración generada y confirma que trae dos `AddColumn<...>` (`logo_file_id` tipo `uuid`, nullable;
`logo_public_key` tipo `character varying(512)`, nullable) y que `Down` los quita — mismo patrón que
`20260912133442_AddMembershipDisplayName.cs`. Sin backfill: ningún tenant tiene logo todavía.

- [ ] **Step 4: Build y prueba de migración manual contra Testcontainers**

No hace falta una prueba de integración dedicada para esta migración (a diferencia de
`AddCustomerContactAddress` en el plan de referencia, acá no hay backfill ni dato previo que
migrar: las dos columnas nacen `NULL`). Se verifica que la migración aplica limpio como parte de
`TenantLogoApiTests` en la Task 8, que arranca el host completo y migra al último. Por ahora, sólo
build:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`.

- [ ] **Step 5: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OutboxWriter.cs "src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/*_AddTenantLogo.cs" "src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/*_AddTenantLogo.Designer.cs" src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs
git commit -m "feat(tenancy): el tenant guarda su logo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con cinco archivos (los dos de la migración cuentan como uno cada uno); el
`Select-String` sin salida.

---

### Task 5: Puerto `ITenantLogoStorage`, `TenantDirectory.GetLogoFileIdAsync` y `TenantSettingsDto.Logo`

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantLogoStorage.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantSettingsDto.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantMappings.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/GetTenantSettings.cs`

**Interfaces:**
- Consumes: `Tenant.LogoFileId`/`LogoPublicKey` (Task 3).
- Produces (namespace `Modules.Tenancy.Application`):

```csharp
public interface ITenantLogoStorage
{
    Task<TenantLogoPublication> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
    Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
    Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
    string? GetUrl(string publicKey);
}

public sealed record TenantLogoPublication(string PublicKey);

public interface ITenantDirectory
{
    // ... GetSlugAsync, GetDisplayNameAsync, GetTimeZoneAsync existentes ...
    Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken);
}

public sealed record TenantLogoDto(Guid FileId, string? Url);

public sealed record TenantSettingsDto(
    TenantId TenantId, string DisplayName, string DefaultCulture, string TimeZone,
    string DateFormat, long Version, TenantLogoDto? Logo);
```

No hay una prueba unitaria propia para este task: `ITenantLogoStorage` es un puerto sin
implementación todavía (la trae la Task 6) y `GetLogoFileIdAsync`/`ToSettingsDto` son proyecciones
de una línea, del mismo tamaño que `GetTimeZoneAsync` — se ejercitan por integración en la Task 8.
Es infraestructura de aplicación que las Tasks 6-8 necesitan para compilar; no es un "RED" de
comportamiento nuevo.

- [ ] **Step 1: Crear el puerto `ITenantLogoStorage`**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantLogoStorage.cs`:

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// Publica y despublica el logo de un tenant en el bucket público de Storage, sin pasar por los
/// handlers ni el dispatcher de ese módulo (decisión 4 del spec 2026-09-19): publicar el logo lo
/// autoriza <c>tenancy.settings.update</c>, no <c>storage.file.publish</c>, que es lo que exigen
/// <c>PublishFileHandler</c>/<c>SoftDeleteFileHandler</c>. Implementado en Bootstrapper
/// (<c>TenantLogoStorage</c>), que ya referencia los dos módulos.
/// </summary>
public interface ITenantLogoStorage
{
    /// <summary>
    /// Valida que <paramref name="fileId"/> sea una imagen disponible del tenant, dentro del
    /// límite de tamaño, y la publica. Lanza <c>TenantDomainException</c> con los códigos
    /// <c>tenancy.logo.file_not_found</c>, <c>tenancy.logo.not_owned</c>,
    /// <c>tenancy.logo.not_available</c>, <c>tenancy.logo.not_image</c> o
    /// <c>tenancy.logo.too_large</c>; o <c>StorageDomainException</c> con
    /// <c>storage.public.not_configured</c> si el bucket público no está configurado.
    /// </summary>
    Task<TenantLogoPublication> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Despublica y borra lógicamente el archivo. Idempotente: un archivo que ya no existe, es de
    /// otro tenant, o ya está borrado, vuelve sin error — el commit de Tenancy que dispara esta
    /// llamada puede fallar después de que el archivo nuevo ya se publicó (decisión 8 del spec).
    /// </summary>
    Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Igual que <see cref="UnpublishAsync"/> pero nunca lanza: registra la falla y vuelve. Existe
    /// para el paso 8 de <c>SetTenantLogoHandler</c> (retirar el logo viejo, mejor esfuerzo,
    /// después de que Tenancy ya commiteó el nuevo) — <c>Modules.Tenancy.Application</c> no
    /// referencia <c>Microsoft.Extensions.Logging</c>, así que el registro vive acá, del lado del
    /// adaptador, que sí tiene un <c>ILogger</c>.
    /// </summary>
    Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>`null` si el bucket público no está configurado — nunca lanza.</summary>
    string? GetUrl(string publicKey);
}

/// <summary>La clave pública bajo la que quedó publicado el logo.</summary>
public sealed record TenantLogoPublication(string PublicKey);
```

- [ ] **Step 2: `ITenantDirectory.GetLogoFileIdAsync`**

En `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs`, después de
`GetTimeZoneAsync` (línea 17), agrega:

```csharp

    /// <summary>El logo vigente del tenant, o null sin logo. Es la única lectura que Quotations
    /// necesita de Tenancy para imprimir el logo en el PDF (decisión 9 del spec 2026-09-19).</summary>
    Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken);
```

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs`, después de
`GetTimeZoneAsync` (línea 25), agrega:

```csharp

    public async Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.LogoFileId)
            .SingleOrDefaultAsync(cancellationToken);
```

- [ ] **Step 3: `TenantSettingsDto.Logo` y `TenantMappings.ToSettingsDto`**

Reemplaza el archivo entero `src/Modules/Tenancy/Modules.Tenancy.Application/TenantSettingsDto.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record TenantSettingsDto(
    TenantId TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version,
    TenantLogoDto? Logo);

/// <summary>
/// La URL viaja **resuelta**: es la regla BFF del repo — el sidebar necesita un `src` listo, no
/// una clave que armar con una base que el navegador no conoce. `null` sólo si el bucket público
/// se desconfiguró después de asignar el logo; `FileId` viaja igual en ese caso para que la
/// pantalla ofrezca quitar y no subir (spec 2026-09-19, § Contrato).
/// </summary>
public sealed record TenantLogoDto(Guid FileId, string? Url);
```

Reemplaza el archivo entero `src/Modules/Tenancy/Modules.Tenancy.Application/TenantMappings.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class TenantMappings
{
    public static TenantSettingsDto ToSettingsDto(this Tenant tenant, ITenantLogoStorage logoStorage) =>
        new(
            tenant.Id,
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            tenant.Version,
            LogoOf(tenant, logoStorage));

    private static TenantLogoDto? LogoOf(Tenant tenant, ITenantLogoStorage logoStorage) =>
        tenant.LogoFileId is { } fileId
            ? new TenantLogoDto(fileId, tenant.LogoPublicKey is { } key ? logoStorage.GetUrl(key) : null)
            : null;
}
```

- [ ] **Step 4: Inyectar `ITenantLogoStorage` en `GetTenantSettingsHandler`**

En `src/Modules/Tenancy/Modules.Tenancy.Application/GetTenantSettings.cs`, agrega el parámetro al
constructor primario y pásalo a `ToSettingsDto`:

```csharp
public sealed class GetTenantSettingsHandler(
    ITenantRepository tenantRepository,
    IExecutionContext executionContext,
    ITenantLogoStorage logoStorage)
    : IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        GetTenantSettingsQuery query,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(query.TenantId);
        var tenant = await tenantRepository.GetAsync(query.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found",
                "Tenant settings were not found.");

        return tenant.ToSettingsDto(logoStorage);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read tenant settings.");
        }
    }
}
```

`UpdateTenantSettingsHandler` gana el mismo parámetro y el mismo cambio en sus dos
`tenant.ToSettingsDto()` (líneas 68 y 94) — se hace en la Task 7, que ya toca ese archivo para los
comandos nuevos, así no se edita dos veces el mismo método en tasks distintos.

- [ ] **Step 5: Ajustar `UpdateTenantSettingsHandler` sólo para que compile, y build**

`UpdateTenantSettingsHandler.HandleAsync` llama dos veces a `tenant.ToSettingsDto()` (líneas 68 y
94), que ahora exige un `ITenantLogoStorage`. En
`src/Modules/Tenancy/Modules.Tenancy.Application/UpdateTenantSettings.cs`, agrega
`ITenantLogoStorage logoStorage` al constructor primario de `UpdateTenantSettingsHandler` (después
de `IValidator<UpdateTenantSettingsCommand> validator`) y cambia esas dos llamadas a
`tenant.ToSettingsDto(logoStorage)`. Nada más de ese archivo cambia en este task — el comportamiento
nuevo de `UpdateTenantSettings.cs` (los comandos de logo) llega en la Task 7, que es la siguiente en
tocarlo.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`. Ningún registro de `ITenantLogoStorage` existe aún en DI
— eso no lo detecta el build, sólo `CompositionRootTests`/runtime (Task 6).

- [ ] **Step 6: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Tenancy/Modules.Tenancy.Application/ITenantLogoStorage.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Application/TenantSettingsDto.cs src/Modules/Tenancy/Modules.Tenancy.Application/TenantMappings.cs src/Modules/Tenancy/Modules.Tenancy.Application/GetTenantSettings.cs src/Modules/Tenancy/Modules.Tenancy.Application/UpdateTenantSettings.cs
git commit -m "feat(tenancy): puerto ITenantLogoStorage"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con siete archivos; el `Select-String` sin salida.

---

### Task 6: Adaptador `TenantLogoStorage` en Bootstrapper + DI

**Files:**
- Create: `src/Bootstrapper/TenantLogoStorage.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:443-444` (junto a los demás adaptadores Storage↔módulo)
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/TenantLogoStorageTests.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs` (agrega los dobles que faltan: unidad de trabajo, ejecución, reloj)

**Interfaces:**
- Consumes: `IFileResourceRepository`, `FilePublication`, `IPublicObjectStorage`, `IStorageAuditPublisher`, `IStorageUnitOfWork` (`Modules.Storage.Application`); `IExecutionContext`, `IClock` (`Modules.Tenancy.Application`/`BuildingBlocks.Application`); `TenantDomainException` (`Modules.Tenancy.Domain`).
- Produces: `Bootstrapper.TenantLogoStorage : ITenantLogoStorage`, registrado `scoped`.

- [ ] **Step 1: Escribir las pruebas que fallan (no compilan)**

Primero agrega en `tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs` los tres dobles
que `TenantLogoStorageTests` necesita y que hoy no existen en ese archivo (después de
`RecordingPublicObjectStorage`):

```csharp

internal sealed class CountingStorageUnitOfWork : IStorageUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class RecordingStorageAuditPublisher : IStorageAuditPublisher
{
    public List<string> Actions { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);

    public void PublishSystem(
        Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);
}

internal sealed class AllowAllExecutionContext(Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => true;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
```

Este archivo necesita dos `using` nuevos al principio: `Modules.Tenancy.Application;` y
`Modules.Tenancy.Domain;` (para `IExecutionContext`/`TenantId`); `IClock` ya resuelve por
`BuildingBlocks.Application`, que Bootstrapper.UnitTests ya referencia (el proyecto compila
`PublicPaymentProofPublisher` hoy, que usa tipos de Storage.Application).

Crea `tests/Bootstrapper/Bootstrapper.UnitTests/TenantLogoStorageTests.cs`:

```csharp
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El adaptador de Tenancy sobre Storage (decisión 4 del spec 2026-09-19): valida el archivo con
/// las reglas del logo (tipo, tamaño, dueño) y usa <see cref="FilePublication"/> para copiarlo al
/// bucket público, sin pasar por los handlers de Storage ni por sus permisos.
/// </summary>
public sealed class TenantLogoStorageTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishRejectsAFileFromAnotherTenant()
    {
        var file = AvailableLogo(Guid.CreateVersion7(), "image/png");
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.file_not_found", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAMissingFile()
    {
        var storage = NewStorage(resources: [], new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.file_not_found", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileOwnedByAnotherEntity()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.Product,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.Promote($"files/tenants/{TenantId:N}/logo", Now);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_owned", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileThatIsNotAvailableYet()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, FileOwnerType.Tenant,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_available", error.Code);
    }

    [Fact]
    public async Task PublishRejectsANonImage()
    {
        var file = AvailableLogo(TenantId, "application/pdf", "logo.pdf");
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_image", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileOverTwoMebibytes()
    {
        var file = AvailableLogo(TenantId, "image/png", sizeBytes: (2 * 1024 * 1024) + 1);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.too_large", error.Code);
    }

    [Fact]
    public async Task PublishCopiesUnderTenantsMediaAndMarksThePublicKey()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);

        var publication = await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.StartsWith(
            $"tenants/{TenantId:N}/media/{file.Id.Value:N}/", publication.PublicKey, StringComparison.Ordinal);
        Assert.Equal(file.StorageKey, publicStorage.Copies[publication.PublicKey]);
        Assert.Equal(publication.PublicKey, file.PublicStorageKey);
    }

    [Fact]
    public async Task UnpublishDeletesTheCopyAndLeavesTheResourceDeleted()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);
        await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotEmpty(publicStorage.DeletedKeys);
        Assert.Equal(FileResourceStatus.Deleted, file.Status);
    }

    [Fact]
    public async Task UnpublishOnAnAlreadyDeletedResourceDoesNotThrow()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);
        await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        // Segunda vez, ya Deleted: no debe lanzar (decisión 8 del spec — el commit de Tenancy
        // puede fallar después del primer retiro y quien reintente vuelve a llamar acá).
        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void GetUrlReturnsNullWithoutAConfiguredBucket()
    {
        var storage = NewStorage(resources: [], new UnconfiguredPublicObjectStorage());

        Assert.Null(storage.GetUrl("tenants/x/media/y/original.png"));
    }

    private static FileResource AvailableLogo(
        Guid tenantId, string mimeType, string name = "logo.png", long sizeBytes = 2048)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, tenantId, FileOwnerType.Tenant,
            name, mimeType, sizeBytes, $"staging/tenants/{tenantId:N}/logo", Now);
        file.CompleteUpload("checksum", sizeBytes, Now);
        file.MarkClean(Now);
        return file;
    }

    private static TenantLogoStorage NewStorage(FileResource file, IPublicObjectStorage publicStorage) =>
        NewStorage([file], publicStorage);

    private static TenantLogoStorage NewStorage(FileResource[] resources, IPublicObjectStorage publicStorage) =>
        new(
            new InMemoryFileResourceRepository(resources),
            new FilePublication(publicStorage, new FixedClock(Now)),
            publicStorage,
            new RecordingStorageAuditPublisher(),
            new CountingStorageUnitOfWork(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now),
            NullLoggerFactory.Instance.CreateLogger<TenantLogoStorage>());

    private sealed class UnconfiguredPublicObjectStorage : IPublicObjectStorage
    {
        public bool IsConfigured => false;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

`NullLoggerFactory` viene de `Microsoft.Extensions.Logging.Abstractions`, ya referenciado por
Bootstrapper (el host lo usa para todo su logging). Agrega
`using Microsoft.Extensions.Logging.Abstractions;` al principio del archivo.

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --filter "FullyQualifiedName~Bootstrapper.UnitTests.TenantLogoStorageTests"
```

Esperado: **no compila**. `CS0246: no se encontró el tipo o el nombre del espacio de nombres 'TenantLogoStorage'`. Pega la salida en el handoff.

- [ ] **Step 3: Implementar `TenantLogoStorage`**

Crea `src/Bootstrapper/TenantLogoStorage.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Publica y despublica el logo de un tenant en el bucket público de Storage (decisión 4 del spec
/// 2026-09-19), con `FilePublication` (Storage.Application) para la copia+rollback y sin pasar
/// por `PublishFileHandler`/`SoftDeleteFileHandler` ni por sus permisos — publicar el logo lo
/// autoriza `tenancy.settings.update`, no `storage.file.publish`. Mismo criterio que
/// <see cref="PublicPaymentProofPublisher"/>: un adaptador de Bootstrapper que lanza excepciones
/// de dominio de <b>otro</b> módulo (<see cref="TenantDomainException"/>), porque acá es donde
/// vive la regla de negocio "qué archivo puede ser el logo de un tenant".
/// </summary>
internal sealed class TenantLogoStorage(
    IFileResourceRepository repository,
    FilePublication filePublication,
    IPublicObjectStorage publicStorage,
    IStorageAuditPublisher auditPublisher,
    IStorageUnitOfWork storageUnitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<TenantLogoStorage> logger) : ITenantLogoStorage
{
    // Decisión 6 del spec: sólo estos tres tipos, y hasta 2 MiB. Además de FileUploadPolicy (25
    // MiB): ese tope es el general de subida, éste es el del logo en particular.
    private const long MaxSizeBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> AllowedMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };

    private static readonly Action<ILogger, Guid, Guid, Exception> LogUnpublishFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(6000, nameof(LogUnpublishFailed)),
            "No se pudo retirar el logo anterior (tenant {TenantId}, archivo {FileId}); queda publicado hasta que alguien lo borre.");

    public async Task<TenantLogoPublication> PublishAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await LoadOwnedAsync(tenantId, fileId, cancellationToken);

        if (resource.Status is not FileResourceStatus.Available)
        {
            throw new TenantDomainException(
                "tenancy.logo.not_available", "The logo file has not finished uploading yet.");
        }

        if (!AllowedMimeTypes.Contains(resource.MimeType))
        {
            throw new TenantDomainException(
                "tenancy.logo.not_image", "The logo must be a PNG, JPEG or WEBP image.");
        }

        if (resource.SizeBytes > MaxSizeBytes)
        {
            throw new TenantDomainException(
                "tenancy.logo.too_large", $"The logo cannot exceed {MaxSizeBytes} bytes.");
        }

        // FilePublication ya valida IsConfigured (storage.public.not_configured) y EnsureDownloadable.
        var publicKey = await filePublication.PublishAsync(resource, cancellationToken);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.published",
            resource.Id.ToString(), "success", clock.UtcNow);
        // Ese commit es de Storage y ocurre antes del de Tenancy (paso 5 del handler, Task 7).
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);

        return new TenantLogoPublication(publicKey);
    }

    public async Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        if (resource is null || resource.TenantId != tenantId ||
            resource.Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            // Idempotente (decisión 8 del spec): el commit de Tenancy que dispara esto puede
            // fallar después de que el archivo ya se retiró en un intento anterior.
            return;
        }

        await filePublication.UnpublishAsync(resource, cancellationToken);
        resource.SoftDelete(clock.UtcNow);
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, "storage.file.deleted",
            resource.Id.ToString(), "success", clock.UtcNow);
        await storageUnitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            await UnpublishAsync(tenantId, fileId, cancellationToken);
        }
        catch (Exception exception)
        {
            LogUnpublishFailed(logger, tenantId, fileId, exception);
        }
    }

    public string? GetUrl(string publicKey) =>
        publicStorage.IsConfigured ? publicStorage.GetUrl(publicKey) : null;

    private async Task<FileResource> LoadOwnedAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        // Un solo código para "no existe" y "es de otro tenant" (PublicPaymentProofPublisher.cs:52-60):
        // distinguirlos confirmaría que el id existe en otro tenant.
        if (resource is null || resource.TenantId != tenantId)
        {
            throw new TenantDomainException(
                "tenancy.logo.file_not_found", $"File '{fileId}' was not found in this tenant.");
        }

        if (resource.OwnerType is not FileOwnerType.Tenant || resource.OwnerId != tenantId)
        {
            throw new TenantDomainException(
                "tenancy.logo.not_owned", "The file does not belong to this tenant's logo slot.");
        }

        return resource;
    }
}
```

- [ ] **Step 4: Registrar en DI**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, después de
`services.AddScoped<IQuotationPdfProvider, QuotationPdfProvider>();` (línea 444, junto a los demás
adaptadores Storage↔módulo), agrega:

```csharp

        // Decisión 4 del spec 2026-09-19: publica/despublica el logo del tenant sobre Storage sin
        // pasar por sus handlers ni sus permisos.
        services.AddScoped<ITenantLogoStorage, TenantLogoStorage>();
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --filter "FullyQualifiedName~Bootstrapper.UnitTests.TenantLogoStorageTests"
```

Esperado: `Failed: 0` (11 casos).

- [ ] **Step 6: `CompositionRootTests` y suite de Bootstrapper**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --filter "FullyQualifiedName~ArchitectureTests.CompositionRootTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-build
```

Esperado: build con `0 Errores`; `CompositionRootTests` con `Failed: 0` (`GetTenantSettingsQuery`/
`UpdateTenantSettingsCommand` siguen con handler registrado — no cambia por este task, pero
confirma que nada quedó roto); Bootstrapper.UnitTests con `Failed: 0` y once casos más que el
baseline.

- [ ] **Step 7: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Bootstrapper/TenantLogoStorage.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Bootstrapper/Bootstrapper.UnitTests/TenantLogoStorageTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/StorageTestDoubles.cs
git commit -m "feat(tenancy): puerto ITenantLogoStorage y adaptador en Bootstrapper"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con cuatro archivos; el `Select-String` sin salida.

---

### Task 7: `SetTenantLogoCommand`/`RemoveTenantLogoCommand` y sus handlers

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/SetTenantLogo.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/RemoveTenantLogo.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/SetTenantLogoValidatorTests.cs` (nuevo)

**Interfaces:**
- Consumes: `ITenantRepository`, `ITenancyUnitOfWork`, `IExecutionContext`, `IAuditRecorder` (`Modules.Audit.Application`), `IOutboxWriter`, `IClock`, `ITenantLogoStorage`, `Tenant.SetLogo`/`RemoveLogo` (Task 3), `Tenant.ToSettingsDto(ITenantLogoStorage)` (Task 5).
- Produces (namespace `Modules.Tenancy.Application`):

```csharp
public sealed record SetTenantLogoCommand(
    TenantId TenantId, Guid FileId, long ExpectedVersion, string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed record RemoveTenantLogoCommand(
    TenantId TenantId, long ExpectedVersion, string CorrelationId) : ICommand<TenantSettingsDto>;
```

No hay pruebas unitarias de los handlers (mismo patrón que `UpdateTenantSettingsHandler`, que
tampoco las tiene): se ejercitan por integración en la Task 8. Sólo los validadores tienen prueba
unitaria propia, como pide el spec.

- [ ] **Step 1: Escribir la prueba del validador que falla**

Crea `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/SetTenantLogoValidatorTests.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class SetTenantLogoValidatorTests
{
    private readonly SetTenantLogoValidator _validator = new();

    private static readonly TenantId TenantId = new(Guid.CreateVersion7());

    [Fact]
    public void RejectsAnEmptyFileId()
    {
        var result = _validator.Validate(new SetTenantLogoCommand(TenantId, Guid.Empty, 1, "corr-1"));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("FileId", failure.PropertyName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveExpectedVersion(long expectedVersion)
    {
        var result = _validator.Validate(
            new SetTenantLogoCommand(TenantId, Guid.CreateVersion7(), expectedVersion, "corr-1"));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("ExpectedVersion", failure.PropertyName);
    }

    [Fact]
    public void AcceptsAValidCommand()
    {
        var result = _validator.Validate(
            new SetTenantLogoCommand(TenantId, Guid.CreateVersion7(), 1, "corr-1"));

        Assert.True(result.IsValid);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.SetTenantLogoValidatorTests"
```

Esperado: **no compila**. `CS0246: no se encontró el tipo o el nombre del espacio de nombres 'SetTenantLogoCommand'`. Pega la salida en el handoff.

- [ ] **Step 3: `SetTenantLogoCommand` y su handler**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/SetTenantLogo.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record SetTenantLogoCommand(
    TenantId TenantId,
    Guid FileId,
    long ExpectedVersion,
    string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed class SetTenantLogoValidator : AbstractValidator<SetTenantLogoCommand>
{
    public SetTenantLogoValidator()
    {
        RuleFor(command => command.FileId).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class SetTenantLogoHandler(
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantLogoStorage logoStorage,
    IValidator<SetTenantLogoCommand> validator)
    : ICommandHandler<SetTenantLogoCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        SetTenantLogoCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await tenantRepository.GetAsync(command.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found", "Tenant settings were not found.");

        // Estas tres fallas ocurren antes de tocar Storage: un 412 no deja copias públicas huérfanas.
        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "Tenant settings changed after they were loaded.");
        }

        if (command.FileId == tenant.LogoFileId)
        {
            return tenant.ToSettingsDto(logoStorage);
        }

        var previousFileId = tenant.LogoFileId;
        var publication = await logoStorage.PublishAsync(
            command.TenantId.Value, command.FileId, cancellationToken);

        try
        {
            tenant.SetLogo(command.FileId, publication.PublicKey, clock.UtcNow);
        }
        catch
        {
            // El agregado rechazó la asignación (p. ej. tenancy.tenant.not_active): la copia
            // pública del archivo nuevo se retira, no queda huérfana.
            await logoStorage.TryUnpublishAsync(command.TenantId.Value, command.FileId, cancellationToken);
            throw;
        }

        var events = tenant.PullDomainEvents();
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.logo.updated",
            "tenant",
            tenant.Id.ToString(),
            "success",
            ["logoFileId"],
            clock.UtcNow);

        foreach (var domainEvent in events)
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // El commit de Tenancy falló (p. ej. choque de concurrencia de EF entre dos PUT
            // simultáneos que pasaron el paso 3): mismo retiro que el catch de arriba.
            await logoStorage.TryUnpublishAsync(command.TenantId.Value, command.FileId, cancellationToken);
            throw;
        }

        if (previousFileId is { } oldFileId && oldFileId != command.FileId)
        {
            // El logo nuevo ya está commiteado: una falla acá no falla el request (decisión 7 del
            // spec). TryUnpublishAsync la registra en log y no la relanza.
            await logoStorage.TryUnpublishAsync(command.TenantId.Value, oldFileId, cancellationToken);
        }

        return tenant.ToSettingsDto(logoStorage);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsUpdate))
        {
            throw new RequestForbiddenException(
                "authorization.denied", "The subject cannot update tenant settings.");
        }
    }
}
```

- [ ] **Step 4: `RemoveTenantLogoCommand` y su handler**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/RemoveTenantLogo.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record RemoveTenantLogoCommand(
    TenantId TenantId,
    long ExpectedVersion,
    string CorrelationId) : ICommand<TenantSettingsDto>;

public sealed class RemoveTenantLogoValidator : AbstractValidator<RemoveTenantLogoCommand>
{
    public RemoveTenantLogoValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class RemoveTenantLogoHandler(
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantLogoStorage logoStorage,
    IValidator<RemoveTenantLogoCommand> validator)
    : ICommandHandler<RemoveTenantLogoCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> HandleAsync(
        RemoveTenantLogoCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await tenantRepository.GetAsync(command.TenantId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                "tenancy.tenant.not_found", "Tenant settings were not found.");

        if (tenant.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "Tenant settings changed after they were loaded.");
        }

        if (tenant.LogoFileId is not { } fileId)
        {
            return tenant.ToSettingsDto(logoStorage);
        }

        // Decisión 8 del spec: retirar la copia pública antes de commitear Tenancy. Si el commit
        // falla, la persona vuelve a quitarlo — UnpublishAsync es idempotente.
        await logoStorage.UnpublishAsync(command.TenantId.Value, fileId, cancellationToken);

        tenant.RemoveLogo(clock.UtcNow);
        var events = tenant.PullDomainEvents();
        auditRecorder.Record(
            tenant.Id.Value,
            executionContext.SubjectId,
            "tenancy.logo.removed",
            "tenant",
            tenant.Id.ToString(),
            "success",
            ["logoFileId"],
            clock.UtcNow);

        foreach (var domainEvent in events)
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return tenant.ToSettingsDto(logoStorage);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsUpdate))
        {
            throw new RequestForbiddenException(
                "authorization.denied", "The subject cannot update tenant settings.");
        }
    }
}
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.SetTenantLogoValidatorTests"
```

Esperado: `Failed: 0` (4 casos).

- [ ] **Step 6: Build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`. Ninguno de los dos handlers está registrado en DI
todavía (eso es 500 en runtime si algo los invocara; ni siquiera hay endpoint que los llame — eso
es la Task 8) ni cubierto por `CompositionRootTests` hasta el próximo build de ArchitectureTests.

- [ ] **Step 7: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Tenancy/Modules.Tenancy.Application/SetTenantLogo.cs src/Modules/Tenancy/Modules.Tenancy.Application/RemoveTenantLogo.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/SetTenantLogoValidatorTests.cs
git commit -m "feat(tenancy): comandos y DTO para el logo de settings"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con tres archivos; el `Select-String` sin salida.

---
### Task 8: `PUT`/`DELETE /settings/logo`, DI e integración

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs` (archivo completo)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:55-60` (después del registro de `UpdateTenantSettingsCommand`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantLogoApiTests.cs` (nuevo)

**Interfaces:**
- Consumes: `SetTenantLogoCommand`/`RemoveTenantLogoCommand` (Task 7), `TenantSettingsDto.Logo` (Task 5), `IRequestDispatcher` (existente).
- Produces:

```csharp
public sealed record SetTenantLogoRequest(Guid FileId);

public sealed record TenantLogoResponse(Guid FileId, string? Url);

public sealed record TenantSettingsResponse(
    Guid TenantId, string DisplayName, string DefaultCulture, string TimeZone,
    string DateFormat, long Version, TenantLogoResponse? Logo);
```

  `PUT /api/v1/tenants/{tenantId:guid}/settings/logo` y `DELETE` del mismo path, los dos bajo
  `TenancyPermissions.SettingsUpdate` y con `If-Match` obligatorio.

- [ ] **Step 1: Escribir la prueba de integración que falla**

Primero lee el harness que ya existe para los dobles en memoria de Storage,
`tests/Modules/Storage/Modules.Storage.IntegrationTests/PaymentProofStorageHarness.cs:384-570`
(`InMemoryObjectStorage`, `InMemoryPublicObjectStorage`) — este task copia una versión recortada de
esas dos clases dentro de `Modules.Tenancy.IntegrationTests`, como ya hace
`QuotationsApiHarness.cs:886`/`:955` para su propio proyecto: son `internal` a cada proyecto de
pruebas y no se comparten por referencia de assembly.

Crea `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantLogoApiTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// PUT/DELETE /settings/logo de punta a punta (spec 2026-09-19): sube el archivo por el pipeline
/// de Storage, lo asigna, y confirma que `GET /settings` refleja la URL pública, la auditoría y el
/// outbox. Factory propia (no `TenantSettingsApiTests.QepApiFactory`) porque sustituye
/// `IObjectStorage`/`IPublicObjectStorage` por dobles en memoria — sin eso, el `PUT` real
/// intentaría hablarle a R2.
/// </summary>
public sealed class TenantLogoApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string StoragePermissions =
        "storage.file.upload,tenancy.settings.read,tenancy.settings.update";

    [Fact]
    public async Task UploadCompleteAndAssignShowsTheLogoUrlInSettings()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        var settings = await response.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(settings!.Logo);
        Assert.Equal(fileId, settings.Logo!.FileId);
        Assert.StartsWith(
            InMemoryPublicObjectStorage.BaseUrl, settings.Logo.Url, StringComparison.Ordinal);

        var getResponse = await client.GetAsync(SettingsUrl, TestContext.Current.CancellationToken);
        var getSettings = await getResponse.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(settings.Logo.Url, getSettings!.Logo!.Url);
    }

    [Fact]
    public async Task ReplacingTheLogoUnpublishesAndDeletesTheOldFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var oldFileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var firstEtag = await GetEtagAsync(client);
        var firstPut = await PutLogoAsync(client, firstEtag, oldFileId);
        var secondEtag = firstPut.Headers.ETag!.Tag;

        var newFileId = await UploadTenantFileAsync(client, factory, "image/webp", 2048);
        var secondPut = await PutLogoAsync(client, secondEtag, newFileId);

        Assert.Equal(HttpStatusCode.OK, secondPut.StatusCode);
        var deletedKey = await FileOwnPublicKeyAsync(factory, oldFileId);
        Assert.Contains(deletedKey, factory.PublicObjectStorage.DeletedKeys);
        var status = await FileStatusAsync(factory, oldFileId);
        Assert.NotEqual("Available", status);
    }

    [Fact]
    public async Task RemovingTheLogoLeavesSettingsWithoutLogo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);
        var afterPut = await PutLogoAsync(client, etag, fileId);
        var putEtag = afterPut.Headers.ETag!.Tag;

        var response = await DeleteLogoAsync(client, putEtag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(putEtag, response.Headers.ETag!.Tag);
        var settings = await response.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(settings!.Logo);
    }

    [Fact]
    public async Task AFileOfAnotherTenantIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        using var otherClient = factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        otherClient.DefaultRequestHeaders.Add("X-Tenant-Id", OtherTenantId);
        otherClient.DefaultRequestHeaders.Add("X-Permissions", "storage.file.upload");
        var otherTenantFileId = await UploadTenantFileAsync(
            otherClient, factory, "image/png", 2048, tenantId: OtherTenantId);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, otherTenantFileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.file_not_found", problem?.Code);
    }

    [Fact]
    public async Task ANonImageIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "application/pdf", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.not_image", problem?.Code);
    }

    [Fact]
    public async Task AFileOverTwoMebibytesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", (2 * 1024 * 1024) + 1);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.too_large", problem?.Code);
    }

    [Fact]
    public async Task StaleIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var staleEtag = await GetEtagAsync(client);
        await PatchDisplayNameAsync(client, staleEtag);

        var response = await PutLogoAsync(client, staleEtag, fileId);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task MissingIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"{SettingsUrl}/logo")
        {
            Content = JsonContent.Create(new SetTenantLogoRequestPayload(fileId)),
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task AssigningWritesAuditAndOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT outcome FROM audit.entries
            WHERE tenant_id = @tenantId AND action = 'tenancy.logo.updated'
            ORDER BY occurred_at DESC LIMIT 1
            """,
            TenantId);
        Assert.NotNull(audit);
        Assert.Equal("success", audit![0]);

        var outbox = await QueryRowAsync(
            connection,
            """
            SELECT event_name FROM platform.outbox_messages
            WHERE event_name = 'tenancy.tenant-logo-updated.v1'
            ORDER BY occurred_at DESC LIMIT 1
            """);
        Assert.NotNull(outbox);
    }

    private static string SettingsUrl => $"/api/v1/tenants/{TenantId}/settings";

    private static async Task<Guid> UploadTenantFileAsync(
        HttpClient client, TenantLogoApiFactory factory, string mimeType, int sizeBytes,
        string tenantId = TenantId)
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.Parse(tenantId),
                ownerType = "Tenant",
                name = "logo" + (mimeType == "application/pdf" ? ".pdf" : ".png"),
                mimeType,
                sizeBytes,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = (await sessionResponse.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken))!;

        factory.ObjectStorage.Upload(session.StorageKey, new byte[sizeBytes]);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        return session.FileResourceId;
    }

    private static async Task<HttpResponseMessage> PutLogoAsync(HttpClient client, string ifMatch, Guid fileId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{SettingsUrl}/logo")
        {
            Content = JsonContent.Create(new SetTenantLogoRequestPayload(fileId)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteLogoAsync(HttpClient client, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SettingsUrl}/logo");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task PatchDisplayNameAsync(HttpClient client, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, SettingsUrl)
        {
            Content = JsonContent.Create(new
            {
                displayName = $"QCode {Guid.NewGuid():N}"[..24],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "dd/MM/yyyy",
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> GetEtagAsync(HttpClient client)
    {
        var response = await client.GetAsync(SettingsUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static async Task<string> FileOwnPublicKeyAsync(TenantLogoApiFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var resource = await repository.GetAsync(
            new Modules.Storage.Domain.FileResourceId(fileId), TestContext.Current.CancellationToken);
        return resource!.PublicStorageKey!;
    }

    private static async Task<string> FileStatusAsync(TenantLogoApiFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var resource = await repository.GetAsync(
            new Modules.Storage.Domain.FileResourceId(fileId), TestContext.Current.CancellationToken);
        return resource!.Status.ToString();
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection, string sql, string? tenantId = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenantId is not null)
        {
            command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(TenantLogoApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", StoragePermissions);
        return client;
    }

    private sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

    private sealed record SetTenantLogoRequestPayload(Guid FileId);

    private sealed record TenantLogoPayload(Guid FileId, string? Url);

    private sealed record SettingsPayload(
        Guid TenantId, string DisplayName, long Version, TenantLogoPayload? Logo);

    private sealed record ProblemPayload(string? Code);

    /// <summary>El host con los dos buckets de Storage en memoria — mismo patrón que
    /// `StorageApiFactory` (`PaymentProofStorageHarness.cs:384-426`).</summary>
    private sealed class TenantLogoApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        public InMemoryObjectStorage ObjectStorage { get; } = new();

        public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);
                services.RemoveAll<IPublicObjectStorage>();
                services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
            });
        }
    }

    /// <summary>Recortado de `PaymentProofStorageHarness.InMemoryObjectStorage`: concurrente
    /// porque los workers de Storage (staging cleanup) corren en el host mientras la prueba
    /// sube y lee.</summary>
    internal sealed class InMemoryObjectStorage : IObjectStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken)
        {
            if (!_objects.TryGetValue(key, out var content))
            {
                return Task.FromResult<StoredObject?>(null);
            }

            return Task.FromResult<StoredObject?>(
                new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(
            string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
        {
            _objects[destinationKey] = _objects[sourceKey].ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_objects[key].ToArray());

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            _objects[key] = content.ToArray();
            return Task.CompletedTask;
        }

        public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();
    }

    /// <summary>Recortado de `PaymentProofStorageHarness.InMemoryPublicObjectStorage`.</summary>
    internal sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
    {
        public const string BaseUrl = "https://assets.qep.test";

        private readonly ConcurrentDictionary<string, byte> _objects = new(StringComparer.Ordinal);

        public List<string> DeletedKeys { get; } = [];

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            _objects[publicKey] = 0;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            _objects.TryRemove(publicKey, out _);
            DeletedKeys.Add(publicKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.ContainsKey(publicKey));

        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.TenantLogoApiTests"
```

Esperado: **no compila** — `SetTenantLogoRequestPayload`/la ruta `PUT .../logo` no existen; el
`POST /files` con `ownerType: "Tenant"` respondería `422 storage.file.owner_type_invalid` si
llegara a correr (no llega: el `PUT /logo` da 404 al no estar mapeado). Pega la salida (o, si
compila por casualidad contra rutas existentes, la lista de asserts que fallan) en el handoff.

- [ ] **Step 3: `TenantSettingsEndpoints.cs` completo, con `logo`, `PUT` y `DELETE`**

Reemplaza el archivo entero `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

public static class TenantSettingsEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateTenantSettingsRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-09-19: el archivo ya sube por el pipeline de Storage (POST /files, PUT
        // prefirmado, complete); este endpoint sólo lo asigna. Mismo permiso y mismo If-Match
        // obligatorio que el PATCH de arriba — administrar el tenant es una sola autoridad.
        group.MapPut("/logo", SetLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<SetTenantLogoRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/logo", RemoveLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(
            new GetTenantSettingsQuery(new TenantId(tenantId)),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateTenantSettingsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new UpdateTenantSettingsCommand(
                new TenantId(tenantId),
                request.DisplayName,
                request.DefaultCulture,
                request.TimeZone,
                request.DateFormat,
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> SetLogoAsync(
        Guid tenantId,
        SetTenantLogoRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new SetTenantLogoCommand(
                new TenantId(tenantId), request.FileId, expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> RemoveLogoAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new RemoveTenantLogoCommand(new TenantId(tenantId), expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static long RequireIfMatch(HttpContext httpContext)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded version is required.");
        }

        return expectedVersion;
    }

    private static IResult SettingsResult(
        TenantSettingsDto settings,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(new TenantSettingsResponse(
            settings.TenantId.Value,
            settings.DisplayName,
            settings.DefaultCulture,
            settings.TimeZone,
            settings.DateFormat,
            settings.Version,
            settings.Logo is { } logo ? new TenantLogoResponse(logo.FileId, logo.Url) : null));
    }

    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

public sealed record UpdateTenantSettingsRequest(
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat);

public sealed record SetTenantLogoRequest(Guid FileId);

/// <summary>`Url` viaja resuelta (regla BFF del repo): el sidebar necesita un `src` listo, no una
/// clave que armar con una base que el navegador no conoce. Sólo es `null` si el bucket público se
/// desconfiguró después de asignar el logo; `FileId` viaja igual para que la pantalla ofrezca
/// quitar y no subir (spec 2026-09-19, § Contrato).</summary>
public sealed record TenantLogoResponse(Guid FileId, string? Url);

public sealed record TenantSettingsResponse(
    Guid TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version,
    TenantLogoResponse? Logo);
```

`RequireIfMatch` es una extracción del mismo `TryParseVersion` que `UpdateAsync` ya usaba
(`:56-61` en la versión anterior del archivo) — antes vivía inline en `UpdateAsync`; ahora lo
comparten los tres verbos que exigen `If-Match`. `GetAsync` no lo necesita.

- [ ] **Step 4: Registrar los dos handlers en DI**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, después de
`UpdateTenantSettingsHandler>();` (línea 60), agrega:

```csharp
        services.AddScoped<
            ICommandHandler<SetTenantLogoCommand, TenantSettingsDto>,
            SetTenantLogoHandler>();
        services.AddScoped<
            ICommandHandler<RemoveTenantLogoCommand, TenantSettingsDto>,
            RemoveTenantLogoHandler>();
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.TenantLogoApiTests"
```

Esperado: build con `0 Errores`; `TenantLogoApiTests` con `Failed: 0` (9 casos). Exige Docker
corriendo.

- [ ] **Step 6: `TenantSettingsApiTests`, `CompositionRootTests` y las unitarias de Tenancy**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.TenantSettingsApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --no-build
```

Esperado: `TenantSettingsApiTests` con `Failed: 0` (el `PATCH`/`GET` no cambiaron de contrato,
sólo ganaron el campo `logo`, que esas pruebas no leen); `ArchitectureTests` con `Failed: 0`,
incluido `CompositionRootTests.EveryCommandAndQueryHasItsHandlerRegistered` (los dos handlers
nuevos están registrados); Tenancy.UnitTests con `Failed: 0`.

- [ ] **Step 7: `dotnet format` sobre todo lo de Tenancy tocado en las Tasks 3-8**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs src/Modules/Tenancy/Modules.Tenancy.Domain/TenantLogoUpdatedDomainEvent.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenantLogoStorage.cs src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Application/TenantSettingsDto.cs src/Modules/Tenancy/Modules.Tenancy.Application/TenantMappings.cs src/Modules/Tenancy/Modules.Tenancy.Application/GetTenantSettings.cs src/Modules/Tenancy/Modules.Tenancy.Application/UpdateTenantSettings.cs src/Modules/Tenancy/Modules.Tenancy.Application/SetTenantLogo.cs src/Modules/Tenancy/Modules.Tenancy.Application/RemoveTenantLogo.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/OutboxWriter.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenantDirectory.cs src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs src/Bootstrapper/TenantLogoStorage.cs src/Bootstrapper/QepServiceCollectionExtensions.cs
```

Esperado: sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que este plan no tocó.

- [ ] **Step 8: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantLogoApiTests.cs
git commit -m "feat(tenancy): PUT y DELETE del logo en settings"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con tres archivos; el `Select-String` sin salida.

---
### Task 9: `QuotationPdfLogo`, el mapper, el renderer y la plantilla

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs` (agrega `QuotationPdfLogo` y el parámetro `Logo` de `QuotationPdfDocument`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:26-51` (`From`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/QCodePdfRenderer.cs:32-62`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:11-13`, `:148-166`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs`

**Interfaces:**
- Consumes: nada nuevo de otros módulos — todavía no hay quien resuelva el logo (eso es la Task 10); este task sólo hace que el documento y el renderer sepan **transportarlo**.
- Produces (namespace `Modules.Quotations.Application`):

```csharp
public sealed record QuotationPdfLogo(string FileName, [property: JsonIgnore] byte[] Content);

public sealed record QuotationPdfDocument(
    /* ... los mismos 20 parámetros existentes ... */,
    bool CustomerVatSurplus,
    QuotationPdfLogo? Logo);

public static class QuotationPdfDocumentMapper
{
    public static QuotationPdfDocument From(
        QuotationResponse quotation, TenantCalendar calendar, QuotationPdfLogo? logo);
}
```

- [ ] **Step 1: Escribir las pruebas del renderer que fallan**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs`, agrega el
campo `Logo` al `Document` de fixture (línea 46, después de `CustomerVatSurplus: false)`), pasando
a:

```csharp
        CustomerVatSurplus: false,
        Logo: null);
```

Agrega estas tres pruebas después de `RenderSendsThePartyTaxIdAsItsOwnField`:

```csharp
    // El logo viaja como asset con el mismo nombre que data.logo.fileName (contrato de
    // qcode-pdf: README.md:32, Services/TypstService.cs:106-129).
    [Fact]
    public async Task RenderSendsTheLogoAsAnAssetAndNamesItInData()
    {
        var (renderer, capture) = NewRenderer();
        var logoBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var document = Document with { Logo = new QuotationPdfLogo("logo.png", logoBytes) };

        await renderer.RenderAsync(document, TestContext.Current.CancellationToken);

        var body = capture.Body();
        Assert.Equal(
            Convert.ToBase64String(logoBytes),
            body.GetProperty("assets").GetProperty("logo.png").GetString());
        Assert.Equal(
            "logo.png",
            body.GetProperty("data").GetProperty("logo").GetProperty("fileName").GetString());
    }

    // Un tenant sin logo no cambia la forma del request de hoy: sin la propiedad assets, y
    // data.logo en null.
    [Fact]
    public async Task RenderOmitsAssetsWithoutLogo()
    {
        var (renderer, capture) = NewRenderer();

        await renderer.RenderAsync(Document, TestContext.Current.CancellationToken);

        var body = capture.Body();
        Assert.False(body.TryGetProperty("assets", out _));
        Assert.Equal(
            JsonValueKind.Null, body.GetProperty("data").GetProperty("logo").ValueKind);
    }

    // [JsonIgnore] en QuotationPdfLogo.Content: los bytes viajan sólo en assets, nunca
    // duplicados en base64 dentro de data.
    [Fact]
    public async Task RenderDoesNotPutTheLogoBytesInData()
    {
        var (renderer, capture) = NewRenderer();
        var document = Document with
        {
            Logo = new QuotationPdfLogo("logo.png", [0x89, 0x50, 0x4E, 0x47]),
        };

        await renderer.RenderAsync(document, TestContext.Current.CancellationToken);

        var logo = capture.Body().GetProperty("data").GetProperty("logo");
        Assert.False(logo.TryGetProperty("content", out _));
    }
```

Agrega `using System.Text.Json;` al principio del archivo si no está ya (`JsonValueKind` lo
necesita) — ya está, por `JsonDocument`/`JsonElement` en `RequestCapture.Body()`.

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.QCodePdfRendererTests"
```

Esperado: **no compila**. `CS1739`/`CS7036: falta el parámetro 'Logo'` en el fixture `Document`, y
`CS0246: no se encontró 'QuotationPdfLogo'`. Pega la salida en el handoff.

- [ ] **Step 3: `QuotationPdfLogo` y `QuotationPdfDocument.Logo`**

En `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs`, agrega
`using System.Text.Json.Serialization;` al principio, y cambia la firma de `QuotationPdfDocument`
(línea 13) agregando el último parámetro:

```csharp
    bool CustomerVatSurplus,
    /// <summary>El logo del tenant, o null sin logo — el request a qcode-pdf no cambia de forma
    /// en ese caso (spec 2026-09-19). `Content` lleva [JsonIgnore]: viaja sólo como asset
    /// (`QCodePdfRenderer`), nunca duplicado en base64 dentro de `data`.</summary>
    QuotationPdfLogo? Logo);
```

Al final del archivo, después de `IQuotationPdfRenderer`, agrega:

```csharp

/// <summary>
/// El logo tal como lo necesita `qcode-pdf`: el nombre del asset (`logo.png`, `logo.jpg`,
/// `logo.webp`, según la extensión del original) y sus bytes. `FileName` es lo único que
/// `data.logo.fileName` expone a la plantilla; `Content` va aparte, en `assets`.
/// </summary>
public sealed record QuotationPdfLogo(string FileName, [property: JsonIgnore] byte[] Content);
```

- [ ] **Step 4: `QuotationPdfDocumentMapper.From` recibe el logo**

En `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs`, cambia
la firma de `From` (línea 26) y el último argumento del `new(...)` (línea 51):

```csharp
    public static QuotationPdfDocument From(
        QuotationResponse quotation, TenantCalendar calendar, QuotationPdfLogo? logo) =>
        new(
            quotation.QuotationNumber,
            DateOnly.FromDateTime(calendar.ToLocal(quotation.CreatedAt).DateTime),
            quotation.ValidUntil,
            quotation.Client?.Name ?? string.Empty,
            quotation.Client?.Cuc ?? string.Empty,
            Join(ContactSeparator, quotation.Client?.Phone, quotation.Client?.Email),
            Join(LocationSeparator, quotation.Client?.Address, quotation.Client?.CityName),
            BillingFor(quotation),
            ShippingFor(quotation),
            quotation.IsStorePickup,
            AdvisorLabelFor(quotation),
            quotation.Currency,
            BillingAccountFor(quotation),
            quotation.PaymentMethod,
            quotation.Notes,
            [.. quotation.Items.Select(LineFor)],
            quotation.Subtotal,
            quotation.DiscountAmount,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.Total,
            quotation.RetentionAmount,
            quotation.NetTotal,
            quotation.CustomerVatSurplus,
            logo);
```

- [ ] **Step 5: `QCodePdfRenderer` manda `assets` sólo cuando hay logo**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/QCodePdfRenderer.cs`, reemplaza
`RenderAsync` (líneas 32-62):

```csharp
    public async Task<byte[]> RenderAsync(
        QuotationPdfDocument document, CancellationToken cancellationToken)
    {
        // Dictionary y no un anónimo: el request necesita una propiedad `assets` condicional, y
        // dos formas anónimas distintas no comparten tipo estático. El contrato con qcode-pdf
        // (README.md:32, Services/TypstService.cs:106-129, verificado el 2026-09-19) es
        // { source, data, filename, assets: { "<nombre>": "<base64>" } }, con `assets` ausente
        // cuando no hay nada que adjuntar — el request de un tenant sin logo no cambia de forma.
        var payload = new Dictionary<string, object?>
        {
            ["source"] = Template,
            ["data"] = JsonSerializer.SerializeToElement(document, DataFormat),
            ["filename"] = $"Cotizacion-{document.QuotationNumber}.pdf",
        };
        if (document.Logo is { } logo)
        {
            payload["assets"] = new Dictionary<string, string>
            {
                [logo.FileName] = Convert.ToBase64String(logo.Content),
            };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/pdf")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // El 400 trae `{ error, details }` con el mensaje del compilador de Typst. Sin
            // esto el caso de uso seguiría con un cuerpo que no es un PDF y el cliente
            // recibiría un adjunto roto, que es un fallo mucho más caro de diagnosticar.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new QuotationsDomainException(
                "quotation.pdf.render_failed",
                $"qcode-pdf responded {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
```

- [ ] **Step 6: La plantilla imprime el logo**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ`, corrige el
comentario del encabezado del archivo (líneas 11-13):

```typst
// El logo del tenant viaja en el payload como asset (spec 2026-09-19); el color de marca sigue
// fuera de alcance. La referencia se sigue en estructura y no en paleta: una sola tinta neutra,
// sin acento de color que le ponga la marca de nadie a un documento comercial ajeno.
```

Corrige el comentario de la zona del encabezado (líneas 148-149):

```typst
// El emisor ancla arriba a la izquierda, como en la referencia. Con logo, va primero — el peso
// tipográfico de la razón social ya no es lo único que sostiene esa esquina.
```

Reemplaza el bloque `if hay-emisor [...] else [...]` de la celda izquierda (líneas 159-166):

```typst
  if hay-emisor [
    #if data.logo != none [
      #image("assets/" + data.logo.fileName, height: 16mm, fit: "contain")
      #v(6pt)
    ]
    #text(size: 14pt, weight: "bold", tracking: -0.01em)[#data.billingAccount.companyName]
    #if data.billingAccount.companyTaxId != none [
      \ #text(size: 9pt, fill: apagado)[NIT #data.billingAccount.companyTaxId]
    ]
  ] else [
    #if data.logo != none [
      #image("assets/" + data.logo.fileName, height: 16mm, fit: "contain")
      #v(6pt)
    ]
    #text(size: 14pt, weight: "bold", tracking: -0.01em)[Cotización]
  ],
```

El `image(..., fit: "contain")` respeta el ancho de la columna izquierda (`1fr` del `grid` de la
línea 156, con `column-gutter: 10mm` frente a la ficha de la derecha) sin declarar un ancho fijo:
un logo apaisado no empuja la ficha porque la columna ya está acotada por el `grid` — 60mm es, en
la práctica, el sobrante típico de esa columna en A4 con los márgenes de `:130`, no un valor que
la plantilla fije a mano.

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.QCodePdfRendererTests"
```

Esperado: `Failed: 0`, con tres casos más que el baseline.

- [ ] **Step 8: Confirmar el único caller roto**

`QuotationPdfDocumentMapper.From` cambió de firma: `QuotationPdfProvider.EnsureCurrentAsync`
(`QuotationPdfProvider.cs:49`) llama `QuotationPdfDocumentMapper.From(response, calendar)` sin
logo y deja de compilar hasta la Task 11, que es la que agrega el tercer argumento ahí. Este task
**no** toca `QuotationPdfProvider.cs`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: **no compila** — `CS7036` en `QuotationPdfProvider.cs:49`. Es el único error esperado;
si aparece cualquier otro, es un caller de `From`/constructor de `QuotationPdfDocument` que este
plan no vio (por ejemplo, un fixture de otra prueba que construye el documento a mano) —
agrégale `Logo: null` en ese mismo archivo para que siga compilando, y anótalo en el handoff.

- [ ] **Step 9: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/QCodePdfRenderer.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs
git commit -m "feat(quotations): QuotationPdfLogo en el documento, el mapper y el renderer"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con cinco archivos; el `Select-String` sin salida. **La solución sigue sin
compilar** hasta la Task 10 (`QuotationPdfProvider.cs`) — es esperado, mismo criterio que el
hallazgo 3 del plan de referencia (`direccion-de-contacto-propia`): dos tasks seguidas, sin
levantar `Api` ni correr integración entre ellas.

---

### Task 10: Puerto `IQuotationLogoLookup` y adaptador `QuotationTenantLogoLookup`

El spec cierra el ítem 5 de su Orden de trabajo exactamente acá — "`QuotationPdfLogo`, mapper,
renderer con `assets`, plantilla, `IQuotationLogoLookup` + adaptador" — **sin** tocar todavía
`QuotationPdfProvider` (eso es el ítem 6, Task 11). El build queda con el mismo único error de la
Task 9 hasta entonces; este task no lo agrava ni lo arregla.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLogoLookup.cs`
- Create: `src/Bootstrapper/QuotationTenantLogoLookup.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:443-444`

**Interfaces:**
- Consumes: `ITenantDirectory.GetLogoFileIdAsync` (Task 5), `IFileResourceRepository`, `IObjectStorage` (`Modules.Storage.Application`).
- Produces (namespace `Modules.Quotations.Application`):

```csharp
public interface IQuotationLogoLookup
{
    Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken);
}

public sealed record QuotationLogoRef(Guid FileId, string StorageKey, string Extension);
```

No hay prueba unitaria propia para el adaptador — mismo patrón que `QuotationFileLookup`/
`ProductImageLookup`, que tampoco la tienen: son traducciones de una llamada, y las cubre la
integración (Task 12).

- [ ] **Step 1: Crear el puerto**

Crea `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLogoLookup.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>
/// El logo del tenant, para imprimirlo en el PDF de la cotización (decisión 9 del spec
/// 2026-09-19). Lee los bytes del <b>original privado</b> con <c>IObjectStorage.DownloadAsync</c>
/// y no la URL pública por HTTP: el objeto privado es el canónico, mismo criterio que
/// <see cref="QuotationPdf.StorageKey"/> con el PDF ya generado — evita un egreso de red nuevo y
/// no depende de que el bucket público esté configurado en ese ambiente.
/// </summary>
public interface IQuotationLogoLookup
{
    /// <summary>`null` sin logo, o si el archivo no está <c>Available</c> (alguien lo borró por
    /// Storage): el PDF sale sin logo en vez de fallar.</summary>
    Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken);
}

/// <summary><paramref name="Extension"/> sale del <c>MimeType</c>, con punto (`.png`, `.jpg`,
/// `.webp`) — los mismos tres tipos que <c>TenantLogoStorage</c> admite al asignar el logo.
/// </summary>
public sealed record QuotationLogoRef(Guid FileId, string StorageKey, string Extension);
```

- [ ] **Step 2: Crear el adaptador**

Crea `src/Bootstrapper/QuotationTenantLogoLookup.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta `Tenancy` (el `LogoFileId` vigente) y `Storage` (el `FileResource`) al puerto que
/// `quotations` declara (decisión 9 del spec 2026-09-19). Mismo criterio que
/// <see cref="QuotationFileLookup"/> entre Quotations y Storage: ningún módulo de negocio
/// referencia al otro, y el composition root es el único lugar legítimo para ese acoplamiento.
/// </summary>
internal sealed class QuotationTenantLogoLookup(
    ITenantDirectory tenantDirectory,
    IFileResourceRepository repository,
    IObjectStorage objectStorage) : IQuotationLogoLookup
{
    // Los mismos tres tipos que TenantLogoStorage admite al asignar el logo (sin PDF, a
    // diferencia de PublicPaymentProofPublisher.ExtensionsByMimeType).
    private static readonly Dictionary<string, string> ExtensionsByMimeType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/webp"] = ".webp",
        };

    public async Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var fileId = await tenantDirectory.GetLogoFileIdAsync(new TenantId(tenantId), cancellationToken);
        if (fileId is null)
        {
            return null;
        }

        var resource = await repository.GetAsync(new FileResourceId(fileId.Value), cancellationToken);
        if (resource is null ||
            resource.Status is not FileResourceStatus.Available ||
            !ExtensionsByMimeType.TryGetValue(resource.MimeType, out var extension))
        {
            // Sin archivo Available (alguien lo borró por Storage) o con un tipo que
            // TenantLogoStorage no debería haber dejado asignar: el PDF sale sin logo, no falla.
            return null;
        }

        return new QuotationLogoRef(resource.Id.Value, resource.StorageKey, extension);
    }

    public Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken) =>
        objectStorage.DownloadAsync(reference.StorageKey, cancellationToken);
}
```

- [ ] **Step 3: Registrar en DI**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, después de
`services.AddScoped<IQuotationPdfProvider, QuotationPdfProvider>();` (línea 444, o la línea a la
que haya quedado después de la Task 6), agrega:

```csharp

        // Decisión 9 del spec 2026-09-19: el PDF lee el logo del tenant por Tenancy (el
        // LogoFileId vigente) y Storage (los bytes del original privado).
        services.AddScoped<IQuotationLogoLookup, QuotationTenantLogoLookup>();
```

- [ ] **Step 4: Build — mismo único error, sin agravarlo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: el mismo `CS7036` en `QuotationPdfProvider.cs:49` que dejó la Task 9, sin ningún error
nuevo. `IQuotationLogoLookup`/`QuotationTenantLogoLookup` compilan aislados — nada los llama
todavía.

- [ ] **Step 5: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Quotations/Modules.Quotations.Application/IQuotationLogoLookup.cs src/Bootstrapper/QuotationTenantLogoLookup.cs src/Bootstrapper/QepServiceCollectionExtensions.cs
git commit -m "feat(quotations): el PDF imprime el logo del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con tres archivos; el `Select-String` sin salida. El build **sigue roto** —
se arregla en la Task 11, la siguiente en tocar `QuotationPdfProvider.cs`.

---

### Task 11: `QuotationPdf.LogoFileId`, `QuotationPdfProvider` y `QuotationPdfDocumentMapperTests`

Esta task arregla el build que las Tasks 9 y 10 dejaron roto: `QuotationPdfProvider` gana
`IQuotationLogoLookup` y pasa a llamar `QuotationPdfDocumentMapper.From` con el tercer argumento.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs` (archivo completo)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs` (archivo completo)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfTests.cs` (archivo completo)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs:25-26` (`Map`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (agrega `StubQuotationLogoLookup`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs`

**Interfaces:**
- Consumes: `IQuotationLogoLookup` (Task 10).
- Produces (namespace `Modules.Quotations.Domain`):

```csharp
public sealed class QuotationPdf
{
    public Guid? LogoFileId { get; private set; }

    public static QuotationPdf Generate(
        QuotationId quotationId, Guid tenantId, string storageKey, long quotationVersion,
        Guid? logoFileId, DateTimeOffset generatedAt);

    public bool IsStaleFor(long quotationVersion, Guid? logoFileId);

    public void Regenerate(string storageKey, long quotationVersion, Guid? logoFileId, DateTimeOffset generatedAt);
}
```

- [ ] **Step 1: Escribir las pruebas de `QuotationPdf` que fallan**

Reemplaza el archivo entero `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfTests.cs`:

```csharp
namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF de una cotización es un artefacto derivado: se puede volver a generar en cualquier
/// momento a partir del agregado. Lo único que esta entidad decide es **cuándo hace falta**, y
/// lo decide contra `Quotation.Version` y contra `LogoFileId` del tenant (spec 2026-09-19,
/// decisión 10), que ya se incrementa/cambia por su cuenta. Sin eso, cada envío y cada
/// exportación pagarían una llamada a `qcode-pdf` para producir un documento idéntico al anterior.
/// </summary>
public sealed class QuotationPdfTests
{
    private static readonly QuotationId Quotation = QuotationId.New();
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid LogoFileId = Guid.CreateVersion7();
    private static readonly Guid OtherLogoFileId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APdfGeneratedForTheCurrentVersionIsStillGood()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.False(pdf.IsStaleFor(7, null));
    }

    [Fact]
    public void APdfBecomesStaleWhenTheQuotationChanges()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.True(pdf.IsStaleFor(8, null));
    }

    // No se compara con `!=` sino con `<`: si el PDF quedara adelante de la cotización -- una
    // restauración de base, una escritura fuera de orden -- regenerarlo no arregla nada y
    // volvería a hacerlo en cada pedido, para siempre.
    [Fact]
    public void APdfAheadOfTheQuotationIsNotConsideredStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 9, null, Now);

        Assert.False(pdf.IsStaleFor(8, null));
    }

    [Fact]
    public void RegeneratingPointsAtTheNewObjectAndVersion()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);
        var later = Now.AddHours(3);

        pdf.Regenerate("quotations/tenants/x/b.pdf", 9, null, later);

        Assert.Equal("quotations/tenants/x/b.pdf", pdf.StorageKey);
        Assert.Equal(9, pdf.QuotationVersion);
        Assert.Equal(later, pdf.GeneratedAt);
        Assert.False(pdf.IsStaleFor(9, null));
    }

    // Decisión 10 del spec 2026-09-19: el logo invalida la caché igual que la versión, comparado
    // por `!=` porque no tiene orden — a diferencia de `QuotationVersion`, que sólo sube.
    [Fact]
    public void APdfWithAnotherLogoIsStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.True(pdf.IsStaleFor(7, OtherLogoFileId));
    }

    [Fact]
    public void APdfWithTheSameLogoIsNotStale()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.False(pdf.IsStaleFor(7, LogoFileId));
    }

    [Fact]
    public void APdfWithoutLogoIsStaleOnceTheTenantHasOne()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, null, Now);

        Assert.True(pdf.IsStaleFor(7, LogoFileId));
    }

    // "Y la vuelta" (spec, § Pruebas): un tenant que tenía logo y lo quita también invalida.
    [Fact]
    public void APdfWithALogoIsStaleOnceTheTenantRemovesIt()
    {
        var pdf = QuotationPdf.Generate(Quotation, Tenant, "quotations/tenants/x/a.pdf", 7, LogoFileId, Now);

        Assert.True(pdf.IsStaleFor(7, null));
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.QuotationPdfTests"
```

Esperado: **no compila**. `CS1501: no hay sobrecarga para el método 'Generate' que tome 5 argumentos`
(y lo mismo para `IsStaleFor`/`Regenerate`). Pega la salida en el handoff.

- [ ] **Step 3: `QuotationPdf.LogoFileId`**

Reemplaza el archivo entero `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// El PDF ya generado de una cotización, y con qué versión del agregado —y qué logo del tenant—
/// se generó.
///
/// Es un artefacto **derivado**: se puede reconstruir en cualquier momento desde la cotización,
/// así que no vive en su agregado ni tiene invariantes de negocio propias. Existe para no pagar
/// una llamada a `qcode-pdf` cada vez que alguien exporta o reenvía un documento que no cambió.
///
/// Uno por cotización: la clave primaria es <see cref="QuotationId"/>. Regenerar pisa la fila y
/// el objeto anterior queda huérfano en el bucket, donde lo limpia la regla de lifecycle.
/// </summary>
public sealed class QuotationPdf
{
    private QuotationPdf()
    {
    }

    private QuotationPdf(
        QuotationId quotationId,
        Guid tenantId,
        string storageKey,
        long quotationVersion,
        Guid? logoFileId,
        DateTimeOffset generatedAt)
    {
        QuotationId = quotationId;
        TenantId = tenantId;
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
        LogoFileId = logoFileId;
        GeneratedAt = generatedAt;
    }

    public QuotationId QuotationId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>La clave en el bucket **privado**, que es el canónico. La copia pública que se
    /// le entrega a Meta se crea en cada envío y no se registra acá: es descartable.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary><c>Quotation.Version</c> al momento de generar. No hace falta un mecanismo
    /// propio de invalidación: el agregado ya lo incrementa en cada cambio.</summary>
    public long QuotationVersion { get; private set; }

    /// <summary>El <c>LogoFileId</c> vigente del tenant al momento de generar, o null sin logo
    /// (spec 2026-09-19, decisión 10). Compara por <c>!=</c> en <see cref="IsStaleFor"/> porque,
    /// a diferencia de <see cref="QuotationVersion"/>, no tiene orden.</summary>
    public Guid? LogoFileId { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public static QuotationPdf Generate(
        QuotationId quotationId,
        Guid tenantId,
        string storageKey,
        long quotationVersion,
        Guid? logoFileId,
        DateTimeOffset generatedAt) =>
        new(quotationId, tenantId, storageKey, quotationVersion, logoFileId, generatedAt);

    /// <summary>
    /// La versión se compara con <c>&lt;</c> y no con <c>!=</c>: un PDF que quedara **adelante**
    /// de la cotización —una restauración de base, una escritura fuera de orden— no se arregla
    /// regenerándolo, y tratarlo como obsoleto lo haría regenerar en cada pedido, para siempre. El
    /// logo se compara por <c>!=</c> porque no tiene ese mismo orden.
    /// </summary>
    public bool IsStaleFor(long quotationVersion, Guid? logoFileId) =>
        QuotationVersion < quotationVersion || LogoFileId != logoFileId;

    public void Regenerate(
        string storageKey, long quotationVersion, Guid? logoFileId, DateTimeOffset generatedAt)
    {
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
        LogoFileId = logoFileId;
        GeneratedAt = generatedAt;
    }
}
```

- [ ] **Step 4: `QuotationPdfProvider` inyecta `IQuotationLogoLookup`**

Reemplaza el archivo entero `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs`:

```csharp
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Deja el PDF de una cotización al día y devuelve el registro. Lo comparten exportar y enviar
/// porque los dos necesitan exactamente lo mismo, y duplicar esa decisión terminaría con uno de
/// los dos regenerando de más --o de menos-- sin que nadie lo note.
///
/// No guarda: agrega la fila al repositorio cuando es nueva y muta la existente cuando no, pero
/// el <c>SaveChangesAsync</c> lo hace el caso de uso, que es el dueño de la transacción.
/// </summary>
public interface IQuotationPdfProvider
{
    Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken);
}

public sealed class QuotationPdfProvider(
    IQuotationRepository repository,
    IQuotationResponseComposer composer,
    IQuotationPdfRenderer renderer,
    IQuotationPdfStorage storage,
    ITenantClock tenantClock,
    IQuotationLogoLookup logoLookup)
    : IQuotationPdfProvider
{
    public async Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken)
    {
        var pdf = await repository.FindPdfAsync(
            quotation.TenantId, quotation.Id, cancellationToken);

        // Una lectura de Tenancy y otra de Storage por export, sin bajar bytes todavía (spec
        // 2026-09-19, decisión 9): sólo hace falta el FileId para decidir si el PDF sigue vigente.
        var logo = await logoLookup.FindAsync(quotation.TenantId, cancellationToken);

        // Generar cuesta una llamada de red a `qcode-pdf` mas una subida a R2, y la mayoria de
        // los pedidos son de cotizaciones que nadie toco desde el anterior.
        if (pdf is not null && !pdf.IsStaleFor(quotation.Version, logo?.FileId))
        {
            return pdf;
        }

        // Desde la misma respuesta que dibuja la pantalla: si el documento y la pantalla se
        // armaran por caminos distintos terminarian diciendo cosas distintas de la misma
        // cotizacion, y la diferencia la descubriria el cliente. La fecha de emisión se imprime en
        // el día del tenant (spec 2026-09-17, punto 7).
        var calendar = await tenantClock.GetAsync(quotation.TenantId, cancellationToken);
        var response = await composer.ComposeAsync(
            quotation.TenantId, quotation.ToDto(), cancellationToken);
        // Los bytes sólo se bajan acá, al regenerar — nunca sólo para decidir si hace falta.
        var pdfLogo = logo is null
            ? null
            : new QuotationPdfLogo(
                "logo" + logo.Extension, await logoLookup.ReadAsync(logo, cancellationToken));
        var content = await renderer.RenderAsync(
            QuotationPdfDocumentMapper.From(response, calendar, pdfLogo), cancellationToken);
        var storageKey = await storage.SaveAsync(
            quotation.TenantId, quotation.Id, content, cancellationToken);

        var now = calendar.UtcNow;
        if (pdf is null)
        {
            pdf = QuotationPdf.Generate(
                quotation.Id, quotation.TenantId, storageKey, quotation.Version, logo?.FileId, now);
            repository.AddPdf(pdf);
        }
        else
        {
            pdf.Regenerate(storageKey, quotation.Version, logo?.FileId, now);
        }

        return pdf;
    }
}
```

- [ ] **Step 5: `QuotationPdfDocumentMapperTests` — el helper `Map` pasa el logo**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs`,
reemplaza el helper `Map` (líneas 25-26) — un parámetro opcional evita tocar los veinte llamados
existentes:

```csharp
    private static QuotationPdfDocument Map(QuotationResponse quotation, QuotationPdfLogo? logo = null) =>
        QuotationPdfDocumentMapper.From(quotation, Calendar, logo);
```

Agrega, después de `ABillingPartyWithItsOwnDataHasNoTaxId` (antes de los helpers privados):

```csharp

    [Fact]
    public void MapsTheLogoWhenPresent()
    {
        var logo = new QuotationPdfLogo("logo.png", [0x89, 0x50, 0x4E, 0x47]);

        var document = Map(Response(), logo);

        Assert.Same(logo, document.Logo);
    }

    [Fact]
    public void MapsNoLogoAsNull()
    {
        var document = Map(Response());

        Assert.Null(document.Logo);
    }
```

- [ ] **Step 6: `StubQuotationLogoLookup` y el rediseño de `ExportQuotationPdfHandlerTests`**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs`, agrega después
de `CountingPdfRenderer` (después de la línea 478):

```csharp

/// <summary>El logo que el export ve, mutable entre dos llamados de la misma prueba — así se
/// puede simular que el tenant cambió de logo entre dos exportaciones (spec 2026-09-19, decisión
/// 10).</summary>
internal sealed class StubQuotationLogoLookup : IQuotationLogoLookup
{
    public QuotationLogoRef? Logo { get; set; }

    public Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Logo);

    public Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]>([0x89, 0x50, 0x4E, 0x47]);
}
```

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs`,
reemplaza `NewHandler` (líneas 87-128) por:

```csharp
    private static (
        ExportQuotationPdfHandler Handler,
        CountingPdfRenderer Renderer,
        RecordingPdfStorage Storage,
        StubQuotationRepository Repository,
        StubQuotationLogoLookup LogoLookup,
        Quotation Quotation) NewHandler()
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 9, 30),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);
        CurrentQuotationId = quotation.Id.Value;

        var repository = new StubQuotationRepository(quotation);
        var renderer = new CountingPdfRenderer();
        var storage = new RecordingPdfStorage(DownloadUrl);
        var logoLookup = new StubQuotationLogoLookup();

        var handler = new ExportQuotationPdfHandler(
            repository,
            new NoOpQuotationsUnitOfWork(),
            new QuotationPdfProvider(
                repository,
                new StubQuotationResponseComposer(),
                renderer,
                storage,
                new FixedTenantClock(Now),
                logoLookup),
            storage,
            new StubExecutionContext(SubjectId, TenantId));

        return (handler, renderer, storage, repository, logoLookup, quotation);
    }
```

Actualiza la desestructuración de las cuatro pruebas existentes (una posición nueva, `_`, antes de
`quotation`/al final):

- `TheFirstExportGeneratesTheDocumentAndRecordsIt`: `var (handler, renderer, _, repository, _, quotation) = NewHandler();`
- `ExportingTwiceWithoutChangesDoesNotRegenerate`: `var (handler, renderer, storage, _, _, _) = NewHandler();`
- `ExportingAfterAChangeRegenerates`: `var (handler, renderer, _, _, _, quotation) = NewHandler();`
- `TheDownloadIsNamedAfterTheQuotation`: `var (handler, _, storage, _, _, _) = NewHandler();`

Agrega, después de `TheDownloadIsNamedAfterTheQuotation`:

```csharp

    // El logo del tenant invalida la caché igual que un cambio de la cotización (spec 2026-09-19,
    // decisión 10): el segundo export ve otro FileId en el lookup y vuelve a generar.
    [Fact]
    public async Task ChangingTheTenantLogoRegenerates()
    {
        var (handler, renderer, _, _, logoLookup, _) = NewHandler();
        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        logoLookup.Logo = new QuotationLogoRef(Guid.CreateVersion7(), "files/tenants/x/logo", ".png");
        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(2, renderer.Calls);
    }

    [Fact]
    public async Task ExportingTwiceWithTheSameLogoDoesNotRegenerate()
    {
        var (handler, renderer, _, _, logoLookup, _) = NewHandler();
        logoLookup.Logo = new QuotationLogoRef(Guid.CreateVersion7(), "files/tenants/x/logo", ".png");
        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(1, renderer.Calls);
    }
```

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build
```

Esperado: build con `0 Advertencia(s)`, `0 Errores` — el build vuelve a estar verde, primera vez
desde la Task 9. `Modules.Quotations.UnitTests` con `Failed: 0`, con seis casos más que el baseline
de Task 0 (`APdfWithAnotherLogoIsStale`, `APdfWithTheSameLogoIsNotStale`,
`APdfWithoutLogoIsStaleOnceTheTenantHasOne`, `APdfWithALogoIsStaleOnceTheTenantRemovesIt`,
`MapsTheLogoWhenPresent`, `MapsNoLogoAsNull`) más los dos de
`ChangingTheTenantLogoRegenerates`/`ExportingTwiceWithTheSameLogoDoesNotRegenerate` — ocho en
total, además de los tres de `QCodePdfRendererTests` de la Task 9.

- [ ] **Step 8: Suite completa de Quotations (unitaria) y ArchitectureTests**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
```

Esperado: `Failed: 0`, incluido
`QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` (`IQuotationLogoLookup`
es un puerto propio del módulo; no suma ninguna referencia nueva a otro módulo de negocio).

- [ ] **Step 9: Commit interino**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs
git commit -m "feat(quotations): QuotationPdf.LogoFileId invalida por logo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con seis archivos; el `Select-String` sin salida.

---
### Task 12: Migración `AddQuotationPdfLogo` e integración de punta a punta

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:295-321` (`ConfigureQuotationPdf`)
- Create (generado por `dotnet ef`): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddQuotationPdfLogo.cs` y `.Designer.cs`; modificado: `QuotationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs` (nuevo — hallazgo 2: el archivo que el spec citaba, `QuotationExportApiTests.cs`, prueba el export a Excel, no el PDF)

**Interfaces:**
- Consumes: `QuotationPdf.LogoFileId` (Task 11), `QuotationsApiHarness` (`RegisterTenantAsync`, `ManagerPermissions`, `CreateActiveCustomerAsync`, `CreateQuotationAsync`, `QuotationsUrl`, `QepApiFactory.ObjectStorage`).
- Produces: columna `quotations.quotation_pdfs.logo_file_id uuid NULL`; la migración `<timestamp>_AddQuotationPdfLogo`.

- [ ] **Step 1: Configurar la columna en `QuotationsDbContext`**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`,
dentro de `ConfigureQuotationPdf`, después de
`pdf.Property(value => value.QuotationVersion).HasColumnName("quotation_version");` (línea 309) y
antes de `pdf.Property(value => value.GeneratedAt)...`, agrega:

```csharp
        pdf.Property(value => value.LogoFileId).HasColumnName("logo_file_id");
```

- [ ] **Step 2: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddQuotationPdfLogo --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

Esperado: `Done.`; la migración trae un solo `AddColumn<Guid>("logo_file_id", ..., nullable: true)`
sobre `quotation_pdfs`, y `Down` lo quita. Sin backfill: las filas existentes quedan `NULL` — un
tenant que después asigna logo las ve obsoletas en el próximo export (lo deseado); uno sin logo no
regenera nada.

- [ ] **Step 3: Build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`.

- [ ] **Step 4: Escribir la prueba de integración que falla**

Primero lee `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`
completo (ya se leyó en la preparación de este plan: `RegisterTenantAsync`/`ManagerPermissions`
en `:103-164`, `CreateActiveCustomerAsync`/`CreateQuotationAsync` más abajo, `StubPdfRenderer` en
`:752-757`, `QepApiFactory.ObjectStorage`/`InMemoryObjectStorage` en `:886`,
`InMemoryPublicObjectStorage` en `:955`). El host de `QepApiFactory` levanta la aplicación
**completa** — Tenancy incluida — así que este archivo puede asignar el logo por
`PUT /settings/logo` en el mismo cliente que exporta, sin una factory propia.

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El PDF de la cotización, de punta a punta con el logo del tenant (spec 2026-09-19): exportar,
/// asignar un logo por Tenancy, y volver a exportar. `QuotationExportApiTests.cs` en esta misma
/// carpeta prueba la exportación **asíncrona a Excel** (`POST /export`), un endpoint distinto sin
/// relación con este — este archivo es el primero que ejercita `POST /{quotationId}/pdf` por
/// integración (antes sólo tenía cobertura unitaria, `ExportQuotationPdfHandlerTests`).
/// </summary>
public sealed class QuotationPdfExportApiTests
{
    [Fact]
    public async Task ExportAfterAssigningALogoRegenerates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, TenancyPermissions.SettingsRead, TenancyPermissions.SettingsUpdate]);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);

        var firstExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstExport.StatusCode);
        var first = await firstExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        var fileId = await UploadAndCompleteTenantFileAsync(client, factory, tenantId);
        var etag = await GetSettingsEtagAsync(client, tenantId);
        var putLogo = await client.SendAsync(
            PutLogoRequest(tenantId, etag, fileId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, putLogo.StatusCode);

        var secondExport = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondExport.StatusCode);
        var second = await secondExport.Content.ReadFromJsonAsync<ExportDto>(TestContext.Current.CancellationToken);

        Assert.NotEqual(first!.GeneratedAt, second!.GeneratedAt);
    }

    private static async Task<Guid> UploadAndCompleteTenantFileAsync(
        HttpClient client, Guid tenantId, QepApiFactory factory)
    {
        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new { ownerId = tenantId, ownerType = "Tenant", name = "logo.png", mimeType = "image/png", sizeBytes = 2048 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = (await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponseDto>(
            TestContext.Current.CancellationToken))!;

        factory.ObjectStorage.Upload(session.StorageKey, new byte[2048]);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        return session.FileResourceId;
    }

    private static async Task<string> GetSettingsEtagAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage PutLogoRequest(Guid tenantId, string etag, Guid fileId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/settings/logo")
        {
            Content = JsonContent.Create(new { fileId }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private sealed record ExportDto(string Url, DateTimeOffset GeneratedAt);
}
```

`UploadSessionResponseDto` ya existe como `private sealed record` dentro de `QuotationsApiHarness`
(línea 763): al vivir en el mismo namespace y ser `private` a esa clase, este archivo no puede
referenciarlo directo. Cambia su modificador en `QuotationsApiHarness.cs` de `private` a
`internal`:

```csharp
    internal sealed record UploadSessionResponseDto(Guid FileResourceId, string UploadUrl, string StorageKey);
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.QuotationPdfExportApiTests"
```

Esperado: si `UploadSessionResponseDto` seguía `private`, **no compila** (`CS0122`). Con el cambio
de visibilidad ya aplicado, corre pero **falla** en la aserción final si la migración
`AddQuotationPdfLogo` no llegó a aplicarse contra el Testcontainer (el host migra al arrancar; si
el Step 2/3 de esta task no se hicieron, la columna no existe y el `PUT`/`GET` fallan con un 500 de
Npgsql por columna faltante). Pega la salida en el handoff — a esta altura del plan la migración ya
existe (Steps 1-3), así que lo esperable es `Assert.NotEqual` fallando por bug si algo del
provider/mapper quedó mal, no un error de columna.

- [ ] **Step 3: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.QuotationPdfExportApiTests"
```

Esperado: `Failed: 0` (1 caso). Exige Docker corriendo.

- [ ] **Step 4: `ExportQuotationPdfHandlerTests`, `QuotationExportApiTests` (Excel) y `ArchitectureTests`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.QuotationExportApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
```

Esperado: las tres corridas con `Failed: 0` — `QuotationExportApiTests` (Excel) no cambia de
comportamiento, sólo comparte el archivo `UploadSessionResponseDto` cuya visibilidad se amplió.

- [ ] **Step 5: `dotnet format` sobre todo lo de Quotations tocado en las Tasks 9-12**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs src/Modules/Quotations/Modules.Quotations.Application/IQuotationLogoLookup.cs src/Modules/Quotations/Modules.Quotations.Domain/QuotationPdf.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/QCodePdfRenderer.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Bootstrapper/QuotationTenantLogoLookup.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs
```

Esperado: sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que este plan no tocó.

- [ ] **Step 6: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddQuotationPdfLogo.cs" "src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/*_AddQuotationPdfLogo.Designer.cs" src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPdfExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs
git commit -m "feat(quotations): la caché del PDF se invalida por logo"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con seis archivos (los dos de la migración cuentan como uno cada uno); el
`Select-String` sin salida.

---

### Task 13: `docs: logo del tenant`

El spec pide en su ítem 7 el README **y** los comentarios de `quotation.typ` que decían que no
había logo. Este plan ya corrigió esos comentarios en la Task 9 (Step 6), en el mismo commit que
el código que los invalidaba — mismo criterio que el plan de referencia
(`direccion-de-contacto-propia`, hallazgo/regla "los comentarios se corrigen en el mismo commit
que el código, no en un barrido aparte"). Esta task sólo actualiza el README: el contrato de
`/settings` con el campo `logo`, los dos endpoints nuevos, y el prerequisito del bucket público
(que ya documenta `Storage:R2:PublicBucket`/`PublicBaseUrl` para publicar productos — se amplía
para que también cubra el logo).

**Files:**
- Modify: `README.md:127`, `:130`, `:547-598`

**Interfaces:**
- Consumes: nada de código.
- Produces: README actualizado.

- [ ] **Step 1: Barrido — confirmar que no queda ningún comentario de código diciendo "sin logo"**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
rg -n -i "sin logo|no hay logo|no trae.*logo" src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ
```

Esperado: sin resultados — la Task 9 ya los corrigió. Si aparece algo, corrígelo acá mismo (es la
única excepción a "esta task sólo toca el README") y anótalo en el handoff.

- [ ] **Step 2: Ampliar la fila del bucket público (línea 127) y la de `PaymentProofs:PublicLinks` (línea 130)**

En `README.md`, en la tabla de variables de Storage, reemplaza la fila de `Storage:R2:PublicBucket`
(línea 127):

```markdown
| `Storage:R2:PublicBucket` + `Storage:R2:PublicBaseUrl` | ausentes                                                                                      | Bucket público de lectura y su dominio. **Se configuran juntos o ninguno**; `PublicBaseUrl` debe ser HTTPS absoluta. Los usa la publicación de imágenes de producto **y** el logo del tenant (`PUT /settings/logo`); sin ellos, `PUT` responde `422 storage.public.not_configured` |
```

- [ ] **Step 3: Documentar `PUT`/`DELETE /settings/logo`**

En `README.md`, sección "Configuración del tenant" (línea 545 en adelante), reemplaza la tabla de
métodos (líneas 547-550) por:

```markdown
| Método   | Ruta                                        | Permiso                   |
| -------- | -------------------------------------------- | -------------------------- |
| `GET`    | `/api/v1/tenants/{tenantId}/settings`        | `tenancy.settings.read`   |
| `PATCH`  | `/api/v1/tenants/{tenantId}/settings`        | `tenancy.settings.update` |
| `PUT`    | `/api/v1/tenants/{tenantId}/settings/logo`   | `tenancy.settings.update` |
| `DELETE` | `/api/v1/tenants/{tenantId}/settings/logo`   | `tenancy.settings.update` |
```

Después del párrafo `El GET devuelve la configuración...` (línea 552-554), agrega:

```markdown

El logo se sube primero por la biblioteca de archivos (`POST /files` con
`ownerType: "Tenant"`, `PUT` a la URL firmada, `POST /files/{id}/complete` — ver
"Biblioteca de archivos" más abajo) y recién después se asigna con `PUT .../settings/logo`,
cuerpo `{ "fileId": "<guid>" }` y el mismo `If-Match` que el `PATCH`. Sólo PNG, JPEG o WEBP, hasta
2 MiB; `DELETE` lo quita, mismo `If-Match`. Los dos devuelven el `TenantSettingsResponse`
completo, con `version`/`ETag` nuevos. Exigen `Storage:R2:PublicBucket` y
`Storage:R2:PublicBaseUrl` configurados (ver la tabla de arriba); sin ellos, `PUT` responde
`422 storage.public.not_configured` y `GET` sigue devolviendo `logo.url: null` para un tenant que
ya tenía uno asignado.
```

Reemplaza el bloque de respuesta de ejemplo (líneas 589-597) por:

```json
{
  "tenantId": "01900000-0000-7000-8000-000000000001",
  "displayName": "QCode Enterprise",
  "defaultCulture": "es-CO",
  "timeZone": "America/Bogota",
  "dateFormat": "dd/MM/yyyy",
  "version": 2,
  "logo": null
}
```

- [ ] **Step 4: Build (nada de código cambió, pero confirma que el repo sigue sano)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet build Backend.slnx --no-restore
```

Esperado: `0 Advertencia(s)`, `0 Errores`.

- [ ] **Step 5: Commit (mensaje exacto del spec)**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
if ((git branch --show-current) -ne "feature/logo-del-tenant") { throw "ABORT: rama equivocada" }
git add README.md
git commit -m "docs: logo del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit con un archivo; el `Select-String` sin salida.

---

### Task 14: Verificación final

**Files:**
- Ninguno, salvo que la verificación pida un arreglo; en ese caso, el arreglo va con su propia
  prueba y su commit `fix(...): …`.

**Interfaces:**
- Consumes: todo lo anterior y los números del baseline (Task 0, Step 4).
- Produces: el handoff con la salida literal de cada comando y la lista de commits.

- [ ] **Step 1: Los comandos de README § Verificación**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet format Backend.slnx --verify-no-changes --no-restore
dotnet build Backend.slnx --no-restore
git status --short
git diff --stat main HEAD -- "**/packages.lock.json" Directory.Packages.props
```

Esperado: restore sin `NU1004` (ningún `packages.lock.json` cambió — este plan no toca
dependencias); `dotnet format` sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que esta
rama no tocó y en los `Designer.cs` generados; build con `0 Advertencia(s)` y `0 Errores`;
`git status` vacío; el `diff --stat` sin archivos.

- [ ] **Step 2: `ArchitectureTests` y las unitarias de los cuatro módulos tocados**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
git diff --stat main HEAD -- tests/ArchitectureTests
dotnet test tests/Modules/Storage/Modules.Storage.UnitTests/Modules.Storage.UnitTests.csproj --no-build
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-build
```

Esperado: `ArchitectureTests` con `Failed: 0` y el `diff --stat` de `tests/ArchitectureTests` vacío
(ninguna regla cambió: `QuotationsLayerTests`/`TenancyLayerTests` siguen verdes tal cual estaban);
las cuatro suites unitarias con `Failed: 0` y más casos que el baseline de Task 0 (Storage: +7 —
`FileResourceTests` +1, `FilePublicationTests` +5, y las mismas de `PaymentProofFileManagementTests`
que ya tenía; Tenancy: +8 — `TenantTests` +4, `SetTenantLogoValidatorTests` +4; Quotations: +14 —
`QCodePdfRendererTests` +3, `QuotationPdfTests` +4, `QuotationPdfDocumentMapperTests` +2,
`ExportQuotationPdfHandlerTests` +2, más los casos que ya tenían; Bootstrapper: +11 —
`TenantLogoStorageTests`).

- [ ] **Step 3: La suite completa, una sola vez**

Tarda decenas de minutos y exige Docker; corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
$final = Join-Path $env:TEMP "qep-logo-del-tenant-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $final
Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique
```

Esperado: la lista de fallidas **vacía**. Si alguna falla, antes de tocar código comprueba si ya
fallaba en `main`: `git worktree add ..\qep-backend-worktrees\baseline main` y corre sólo esa
clase ahí con `dotnet test <csproj> --filter "FullyQualifiedName~<Clase>"`; si también falla en
`main`, se anota en el handoff y no es de este plan; si sólo falla acá, se arregla con su prueba en
un commit `fix(...): …` y se repite este paso. Al terminar,
`git worktree remove ..\qep-backend-worktrees\baseline`.

- [ ] **Step 4: Las pruebas que el spec nombra, por nombre**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult | ForEach-Object { "$($_.outcome) $($_.testName)" }
} | Select-String -Pattern "SetLogoIncrementsVersionAndRaisesTheEvent|SetLogoWithTheSameFileIsANoOp|RemoveLogoClearsBothFieldsAndRaisesTheEventWithNull|RemoveLogoWithoutLogoIsANoOp|UploadCompleteAndAssignShowsTheLogoUrlInSettings|ReplacingTheLogoUnpublishesAndDeletesTheOldFile|RemovingTheLogoLeavesSettingsWithoutLogo|AFileOfAnotherTenantIsRejected|ANonImageIsRejected|AFileOverTwoMebibytesIsRejected|StaleIfMatchIsRejected|MissingIfMatchIsRejected|AssigningWritesAuditAndOutbox|APdfWithAnotherLogoIsStale|APdfWithTheSameLogoIsNotStale|APdfWithoutLogoIsStaleOnceTheTenantHasOne|APdfWithALogoIsStaleOnceTheTenantRemovesIt|RenderSendsTheLogoAsAnAssetAndNamesItInData|RenderOmitsAssetsWithoutLogo|RenderDoesNotPutTheLogoBytesInData|ChangingTheTenantLogoRegenerates|ExportingTwiceWithTheSameLogoDoesNotRegenerate|ExportAfterAssigningALogoRegenerates"
```

Esperado: cada nombre aparece al menos una vez, todos con `Passed`.

- [ ] **Step 5: Historial**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git log --format="%h %s" main..HEAD
git log --format=%B main..HEAD | Select-String -SimpleMatch "Co-Authored-By"
git diff --stat main HEAD
```

Esperado: los trece commits de la tabla de Entrega, en orden (catorce si el Step 3 pidió un
`fix`); el `Select-String` sin salida; el `diff --stat` sin un solo archivo de `qep-frontend` ni
de `k8s/`. La rama **no se publica ni se mergea** desde este plan: el handoff dice qué quedó y el
owner decide. Recuerda en el handoff el orden de deploy del spec (§ Entrega): backend primero —
las dos migraciones agregan columnas nulas, así que una API vieja contra la base migrada sigue
funcionando—, frontend después.

---

## Auto-revisión del plan contra el spec

**Cobertura del spec.** Decisión 1 (el logo entra por el pipeline de Storage, `ownerType = Tenant`
nuevo) → Task 1. Decisión 2 (`PUT`/`DELETE /settings/logo`, mismo permiso e `If-Match` que el
`PATCH`) → Tasks 7-8. Decisión 3 (`LogoFileId`+`LogoPublicKey`, URL resuelta al leer) → Tasks 3, 5,
8. Decisión 4 (puerto de Tenancy, sin pasar por handlers/permisos de Storage) → Tasks 5-6. Decisión
5 (`FilePublication` extraído, `StorageKey` sigue `internal`) → Task 2. Decisión 6 (límites del
logo en el adaptador, además de `FileUploadPolicy`) → Task 6
(`PublishRejectsANonImage`/`PublishRejectsAFileOverTwoMebibytes`). Decisión 7 (publicar el nuevo
antes, retirar el viejo después, mejor esfuerzo) → Task 7 (`SetTenantLogoHandler`, pasos 5-8).
Decisión 8 (quitar retira antes de commitear; `RemoveLogo`/`UnpublishAsync` idempotentes) → Tasks
3 (`RemoveLogoWithoutLogoIsANoOp`), 6 (`UnpublishOnAnAlreadyDeletedResourceDoesNotThrow`), 7
(`RemoveTenantLogoHandler`). Decisión 9 (el PDF lee el original privado, no la URL pública) → Task
10 (`QuotationTenantLogoLookup.ReadAsync` vía `IObjectStorage.DownloadAsync`). Decisión 10 (la
caché compara `LogoFileId` además de `Version`) → Tasks 11-12. Decisión 11 (evento nuevo,
`OutboxWriter`, auditoría `tenancy.logo.updated`/`.removed`) → Tasks 3-4 (evento y mapeo), 7
(auditoría). § Contrato: los cinco códigos `tenancy.logo.*` → Task 6; `storage.public.not_configured`
→ Task 2 (`FilePublication`); `tenancy.tenant.not_active` → Task 3 (`EnsureActive` en
`SetLogo`/`RemoveLogo`); `validation.failed` → Task 7 (`SetTenantLogoValidator`/
`RemoveTenantLogoValidator`); `storage.file.owner_type_invalid` dejando de salir para `"Tenant"` →
Task 1. § Dominio: `SetLogo`/`RemoveLogo`, el evento, el límite fuera del agregado → Task 3. §
Aplicación: puerto, DTO, comandos → Tasks 5, 7. § Infraestructura: columnas, migración,
`OutboxWriter` → Task 4. § Storage: `FileOwnerType.Tenant`, `FilePublication`, ninguna sonda nueva
(no hay tarea: el spec lo descarta explícitamente y este plan no agrega ninguna) → Tasks 1-2. §
Adaptadores: `TenantLogoStorage`, `QuotationTenantLogoLookup`, las dos `LayerTests` siguen verdes →
Tasks 6, 10 (`ArchitectureTests` corre sin cambios esperados en cada task que toca un módulo). §
Quotations (PDF): `QuotationPdfLogo`, mapper, `QCodePdfRenderer`, plantilla, `QuotationPdf`,
`QuotationPdfProvider`, migración → Tasks 9, 11-12. § Bordes: permisos por rol (sin ítem de
trabajo, el spec lo confirma verificado contra el catálogo existente — no hay tarea de código);
stub de desarrollo (`X-Permissions` en los tests de integración de las Tasks 8, 12); archivo
`Tenant` ya publicado (idempotencia de `FilePublication.PublishAsync`, cubierta por su propio test
de Task 2); bucket sin configurar (Task 6 `GetUrlReturnsNullWithoutAConfiguredBucket`, Task 2
`PublishRejectsWhenThePublicBucketIsNotConfigured`); archivo del logo borrado por Storage (Task 10,
`FindAsync` devuelve `null` si no está `Available`); tenant inactivo (Task 3); concurrencia (Tasks
7-8, `StaleIfMatchIsRejected`); archivo `Tenant` subido y nunca asignado (sin tarea, el spec lo
acepta explícitamente). § Pruebas: cada nombre que el spec fija tiene su paso, y el barrido final
(Task 14, Step 4) los lista todos. § Orden de trabajo: los ocho commits del spec son el mensaje
final de las Tasks 2, 4, 6, 8, 10, 12, 13 y el verificador es la Task 14; el desdoblamiento en
commits interinos es una decisión de esta sesión (Global Constraints), no del spec. § Entrega: las
dos migraciones con columnas nulas (Tasks 4, 12); los dos prerequisitos de ambiente verificados sin
imprimir valores (Task 0) y documentados (Task 13); orden de despliegue backend-primero recordado
en el handoff final (Task 14, Step 5).

**Discrepancias contra el código encontradas durante la investigación** (reportadas también en el
handoff de esta sesión): el spec cita `QepServiceCollectionExtensions.cs:143-147` para el registro
manual de los handlers de Tenancy; la ubicación real es `:55-60` (hallazgo 1) — la cita del spec
señala el patrón ilustrado en esas líneas, no la ubicación, así que no es un error del spec, sólo
una lectura más precisa. El spec cita `Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs`
como el lugar de `ExportAfterAssigningALogoRegenerates`; ese archivo prueba la exportación
asíncrona a Excel (`POST /export`), no el PDF (`POST /{quotationId}/pdf`), que hoy no tiene ninguna
prueba de integración — este plan crea `QuotationPdfExportApiTests.cs` en su lugar (hallazgo 2).

**Placeholders.** No hay `TBD`, `TODO`, "similar a la Task N" ni "agregar validación": cada paso de
código muestra el código completo o el diff exacto sobre líneas citadas, y cada paso de corrida
muestra el comando y la salida esperada. Los únicos `<…>` son `<timestamp>` (lo pone `dotnet ef`;
los commits lo resuelven por glob `*_AddTenantLogo.cs`/`*_AddQuotationPdfLogo.cs`) y
`<Clase>`/`<csproj>` en el paso condicional de la Task 14 que sólo corre si algo falló.

**Consistencia de tipos.** `ITenantLogoStorage.PublishAsync(Guid, Guid, CancellationToken) : Task<TenantLogoPublication>`
se define en la Task 5 y se implementa igual en la Task 6 (`TenantLogoStorage`) y se consume igual
en la Task 7 (`SetTenantLogoHandler`). `TenantSettingsDto.Logo : TenantLogoDto?` con
`(Guid FileId, string? Url)` es el mismo tipo en `TenantMappings.ToSettingsDto` (Task 5),
`GetTenantSettingsHandler`/`UpdateTenantSettingsHandler` (Tasks 5, 7) y
`TenantSettingsEndpoints.SettingsResult` (Task 8, que lo traduce a `TenantLogoResponse` con los
mismos dos campos). `FilePublication(IPublicObjectStorage, IClock)` con
`PublishAsync(FileResource, CancellationToken) : Task<string>` y
`UnpublishAsync(FileResource, CancellationToken) : Task` es la misma firma en su definición (Task
2), en `PublishFileHandler`/`UnpublishFileHandler`/`SoftDeleteFileHandler` (Task 2) y en
`TenantLogoStorage` (Task 6). `IQuotationLogoLookup.FindAsync(Guid, CancellationToken) : Task<QuotationLogoRef?>`
y `ReadAsync(QuotationLogoRef, CancellationToken) : Task<byte[]>` se definen en la Task 10 y se
consumen igual en `QuotationPdfProvider` (Task 11) y en el doble `StubQuotationLogoLookup` (Task
11). `QuotationPdf.Generate`/`IsStaleFor`/`Regenerate` reciben `Guid? logoFileId` en la misma
posición en su definición (Task 11), en `QuotationPdfProvider.EnsureCurrentAsync` (Task 11) y en
cada prueba de `QuotationPdfTests` (Task 11). El nombre de las dos migraciones es `AddTenantLogo`
(Task 4) y `AddQuotationPdfLogo` (Task 12) en el comando `dotnet ef`, en la clase generada, y en el
`git add` del commit que las incluye.

