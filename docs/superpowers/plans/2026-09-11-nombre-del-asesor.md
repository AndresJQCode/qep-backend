# Nombre del asesor — Plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el PDF de cotización imprima en la ficha "Asesor" el nombre completo de quien la atiende, cargado en la membresía al invitar o desde el roster, y que caiga al correo cuando no hay nombre.

**Architecture:** `Membership` (Tenancy) gana `DisplayName`, nulo sólo en filas viejas y en el owner, normalizado en el agregado (trim, 1..150). Invitar lo exige; un endpoint propio, `PATCH .../memberships/{membershipId}/display-name`, lo edita con `If-Match` y auditoría atómica, y el roster lo muestra y lo busca. En Quotations, el puerto `IQuotationAdvisorLookup` pasa a devolver correo **y** nombre desde el adaptador de `Bootstrapper`, `QuotationResponse` suma `AdvisorName` y `QuotationPdfDocumentMapper` elige `AdvisorName ?? AdvisorEmail ?? ""`. Listados, historial, ventas y reportes siguen con el correo.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit v3, Testcontainers (las pruebas de integración necesitan Docker corriendo).

**Spec:** `docs/superpowers/specs/2026-09-11-nombre-del-asesor-design.md` — leerla antes de la primera tarea. Este plan argumenta desde ella; sólo cubre la entrega de **backend**.

## Global Constraints

- Nombre: trim, largo entre 1 y 150 caracteres (`Membership.DisplayNameMaxLength = 150`).
- Columna: `tenancy.memberships.display_name character varying(150) null`.
- Código de dominio del nombre inválido: `tenancy.membership.display_name_invalid`.
- Acción de auditoría del renombre: `tenancy.membership.renamed`, recurso `membership`, resultado `success`.
- Permiso del renombre: `TenancyPermissions.AdvisorshipManage` (ya existe con su política; no se registra ninguna nueva).
- Ruta: `PATCH /api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}/display-name`.
- `If-Match` obligatorio, mismo comportamiento que `PATCH .../roles`: sin él `428 precondition.if_match_required`; con versión vieja `412`.
- Validación de texto libre: FluentValidation `NotEmpty` + `MaximumLength(150)` → `422 validation.failed` con el mapa `errors` (clave `DisplayName`, PascalCase).
- PDF: `AdvisorLabel = AdvisorName ?? AdvisorEmail ?? ""`. `quotation.typ` no se toca.
- Caché del PDF intacto: `QuotationPdfProvider` no se toca; los PDFs ya generados siguen con el correo (D7).
- Listados de cotizaciones y ventas, historial, filtros y reportes siguen mostrando el correo (D1).
- Rama: `feature/nombre-del-asesor`, creada desde `main` en Task 0 después de comprobar `git branch --show-current`. Nunca commitear en `main`; comprobar la rama **antes de cada commit**.
- Commits: conventional commits en español, **sin atribución de IA y sin trailer `Co-Authored-By`**.
- Comandos para el developer en **PowerShell** (`A; if ($?) { B }`, nunca `&&`; `curl.exe`, nunca `curl`).
- `Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y `dotnet ef`: detenerlo antes (`Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`).
- TDD obligatorio: RED antes que GREEN, pegando la salida literal de ambos.
- Regresión medida **por nombre de prueba** contra el baseline de Task 0, nunca por conteo.
- Entrega: invitar desde la SPA vieja responde 422 cuando esto se despliegue. Los dos deploys salen juntos, o el backend sale después de que el front ya mande `displayName`.

---

## Hallazgos contra el código (2026-09-12)

Verificados leyendo el código en `main` (`645da58`), no supuestos:

1. **`911a423` no toca nada de este plan.** Cambió `SendQuotation`, `QuotationEndpoints` y `QuotationsDtos` para elegir el destinatario del envío; no llama a `IQuotationAdvisorLookup` ni a `QuotationPdfDocumentMapper`. Los consumidores del puerto siguen siendo cuatro: `ListQuotations`, `ListSales`, `ListQuotationHistory` y `QuotationResponseComposer`.
2. **Los cinco handlers del roster pasan por un solo mapeo.** `ListMemberships`, `SuspendMember`, `RemoveMember`, `ReactivateMember` y `UpdateMemberRoles` construyen el item con `membership.ToListItemDto(email)` (`ListMemberships.cs:27`). Llenar `DisplayName` ahí los cubre a todos, y al handler nuevo también. Ninguno de esos cuatro archivos se edita.
3. **El barrido tiene un archivo que la spec no lista:** `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:283-286` postea invitaciones. Las "unas 35 llamadas" de `MembershipApiTests` pasan todas por **dos** puntos: el helper `InviteAsync` (`:814-827`) y un cuerpo crudo (`:391-400`).
4. **El validador corre antes que la autorización** en `InviteMemberHandler` (`InviteMember.cs:42-43`). Sin el barrido, `InviteToAnotherTenantIsForbidden` pasaría de 403 a 422, e `InviteWithUnknownRoleIsRejected` de `role_unknown` a `validation.failed`. Por eso el barrido va **antes** que el cambio de contrato (ver orden de tareas).
5. **403 contra 404.** El handler responde 403 cuando el tenant de la ruta no es el del contexto, o falta el permiso. Un `membershipId` de otro tenant **bajo la ruta del tenant propio** responde 404 `tenancy.membership.not_found`, igual que un id inexistente (`MembershipLoader.cs:15-18`), que es lo mismo que ya hacen suspend, remove y roles. No filtra existencia porque los dos casos son indistinguibles. La prueba de 403 de Task 6 ejerce el primer caso, como `ManageFromAnotherTenantIsForbidden`.
6. **Colisión de nombres con Reporting.** `ReportingDtos.AdvisorName` ya existe y lleva el **correo** (`ReportingLookups`, adaptador propio de Reporting). El `QuotationResponse.AdvisorName` nuevo lleva el **nombre**. Reporting no cambia (D1), pero seis comentarios que dicen "el sistema no guarda nombre de persona" pasan a ser falsos y se corrigen en Task 5 y Task 7.
7. **Decisiones que la spec no cubría**, tomadas acá:
   - `Membership.Rename` devuelve `bool` (precedente: `Expire`). El handler audita y guarda **sólo si cambió**: un renombre al mismo nombre no deja una auditoría de algo que no pasó.
   - `Rename` no emite evento de dominio: nadie lo consumiría.
   - `MembershipDto.DisplayName` y `MembershipResponse.DisplayName` son `string?`, porque el reinvite no-op devuelve la fila existente, que puede ser vieja y no tener nombre (D5).
8. **Orden ajustado.** El barrido (Task 3) va antes que el contrato de invitación (Task 4): mandar `displayName` a una API que todavía no lo lee es inofensivo, y así la suite nunca queda roja entre commits. Task 1 y Task 2 se commitean por separado pero **no se pushea entre las dos**. Entre ellas EF mapea `DisplayName` por convención a una columna que todavía no existe, y la integración de Tenancy falla.

---

## File Structure

**Crear**

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs` | Comando, validador y handler del renombre (autorización doble capa, versión, auditoría atómica) |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddMembershipDisplayName.cs` (+ `.Designer.cs`) | Migración generada: columna `display_name` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationAdvisorNameApiTests.cs` | Punta a punta: renombrar la membresía llega al detalle de la cotización por el adaptador de `Bootstrapper` |

**Modificar — producción**

| Archivo | Cambio |
| --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs` | `DisplayNameMaxLength`, `DisplayName`, `NormalizeDisplayName`, `Rename`; `displayName` en `Invite`/`Reinvite` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:94-96` | Mapeo de `display_name` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs` | Regenerado por `dotnet ef` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs` | `DisplayName` en comando, validador y handler |
| `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs` | `DisplayName` en el DTO y su mapeo |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs` | `DisplayName` en `MembershipListItemDto` y `ToListItemDto`; búsqueda por nombre |
| `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` | Request/response de invitar, item del roster, endpoint `display-name` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs:86-88` | Registro del handler nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs` | `QuotationAdvisor` y `FindAsync` |
| `src/Bootstrapper/QuotationAdvisorLookup.cs` | Toma `DisplayName` de la membresía que ya trae |
| `src/Modules/Quotations/Modules.Quotations.Application/ListQuotations.cs:126-140` | `.Email` del lookup nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/ListSales.cs:119-130` | `.Email` del lookup nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/ListQuotationHistory.cs:49-69` | `.Email` del lookup nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs:38-58` | Correo y nombre |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:247-248` | `QuotationResponse.AdvisorName` |
| `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:35` | Regla D6 |
| `src/Modules/Quotations/Modules.Quotations.Application/SalesDtos.cs:71-72` | Comentario que pasa a ser falso |
| `src/Bootstrapper/ReportingLookups.cs:14-19`, `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs:7-12` | Comentarios que pasan a ser falsos (sin cambio de código) |
| `README.md:433-495` | Contrato de invitar y del renombre |

**Modificar — pruebas**

| Archivo | Cambio |
| --- | --- |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs` | Pruebas de `Rename`, `Invite`, `Reinvite`; llamadas a la firma nueva |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs:248-261` | Firma nueva de `Invite` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs` | Barrido + pruebas de invitar y del roster |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs` | Barrido + pruebas del `PATCH display-name` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/InvitationApiTests.cs:280` | Barrido |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs:149` | Barrido |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs:159,228,277,307` | Barrido |
| `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InvitationNotificationTests.cs:86` | Barrido |
| `tests/Modules/Audit/Modules.Audit.IntegrationTests/AuditRecordingTests.cs:163` | Barrido |
| `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:285` | Barrido |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:110-124,329-335` | Doble del lookup y composer stub |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ListSalesHandlerTests.cs`, `ListQuotationsHandlerTests.cs` | Guarda de D1 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs` | Regla D6 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleListApiTests.cs:43-44`, `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/SalesReportApiTests.cs:50-51`, `ReportingApiHarness.cs:99-102` | Comentarios que pasan a ser falsos |

**No se tocan, a propósito:** `quotation.typ`, `QuotationPdfProvider` (D7), `TenancySeeder` y `TenantRegistrationService` (usan `CreateActive`, que queda sin nombre), `SuspendMember.cs`, `RemoveMember.cs`, `ReactivateMember.cs` y `UpdateMemberRoles.cs` (hallazgo 2), `InvitationEmailTemplate` (fuera de alcance), `ReportingLookups` (D1, sólo su comentario).

---

### Task 0: Rama y baseline

**Files:** ninguno de código. Commitea la spec y este plan.

**Interfaces:**
- Consumes: nada.
- Produces: rama `feature/nombre-del-asesor`; archivo `$env:TEMP\qep-nombre-asesor-baseline-failed.txt` con los nombres de las pruebas que **ya** fallan antes de empezar.

- [ ] **Step 1: Comprobar la rama y crear la de trabajo**

```powershell
git branch --show-current
git status --short
```

Esperado: `main`, y como únicos cambios `?? docs/superpowers/specs/2026-09-11-nombre-del-asesor-design.md` y `?? docs/superpowers/plans/2026-09-11-nombre-del-asesor.md`. Si la rama no es `main`, parar y preguntar: el snapshot de arranque de sesión no es autoridad sobre la rama.

```powershell
git switch -c feature/nombre-del-asesor
git branch --show-current
```

Esperado: `feature/nombre-del-asesor`.

- [ ] **Step 2: Commitear spec y plan**

```powershell
git add docs/superpowers/specs/2026-09-11-nombre-del-asesor-design.md docs/superpowers/plans/2026-09-11-nombre-del-asesor.md
git commit -m "docs: spec y plan del nombre del asesor"
```

- [ ] **Step 3: Baseline de la suite completa, por nombre**

Con Docker corriendo y `Api.exe` detenido:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore --locked-mode
dotnet build --no-restore
$baseline = Join-Path $env:TEMP "qep-nombre-asesor-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $baseline
Get-ChildItem $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-nombre-asesor-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-nombre-asesor-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pegarla en el handoff. **La regresión se mide contra esta lista, por nombre**, no por conteo.

---

### Task 1: Dominio — `DisplayName` y `Rename`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs` (constante junto a `AdminRole` `:28`; propiedad junto a `Origin` `:67`; `Rename` después de `ChangeRoles` `:405-451`; `NormalizeDisplayName` junto a `ValidateOrigin` `:490-501`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `public const int Membership.DisplayNameMaxLength = 150;`
  - `public string? Membership.DisplayName { get; private set; }`
  - `public bool Membership.Rename(string displayName, DateTimeOffset occurredAt)`: `true` si cambió (sube `Version`, actualiza `UpdatedAt`); `false` sin tocar nada si el nombre normalizado es el actual. Lanza `TenantDomainException("tenancy.membership.display_name_invalid")`.
  - `private static string Membership.NormalizeDisplayName(string? value)`: la usa Task 4.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `MembershipTests.cs`, antes de los helpers privados (`:599`):

```csharp
    /// <summary>
    /// El nombre es presentación del tenant: vive en la membresía, se normaliza en el agregado y
    /// renombrar cuenta como cambio —sube la versión— porque el roster lo edita con If-Match.
    /// </summary>
    [Fact]
    public void RenameTrimsTheNameAndBumpsTheVersion()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;
        var renamedAt = InvitedAt.AddHours(1);

        var changed = membership.Rename("  Ana María Pérez  ", renamedAt);

        Assert.True(changed);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(version + 1, membership.Version);
        Assert.Equal(renamedAt, membership.UpdatedAt);
    }

    // Guardar dos veces lo mismo no es un cambio: subir la versión invalidaría el If-Match de
    // otra pestaña por nada.
    [Fact]
    public void RenameWithTheSameNormalizedNameIsANoOp()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Rename("Ana María Pérez", InvitedAt.AddHours(1));
        var version = membership.Version;
        var updatedAt = membership.UpdatedAt;

        var changed = membership.Rename("  Ana María Pérez ", InvitedAt.AddHours(2));

        Assert.False(changed);
        Assert.Equal(version, membership.Version);
        Assert.Equal(updatedAt, membership.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RenameRejectsABlankNameAndLeavesTheMembershipUntouched(string displayName)
    {
        var membership = Invite(Guid.CreateVersion7());
        var before = membership.DisplayName;
        var version = membership.Version;

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Rename(displayName, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
        Assert.Equal(before, membership.DisplayName);
        Assert.Equal(version, membership.Version);
    }

    [Fact]
    public void RenameRejectsANameLongerThanTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());

        var error = Assert.Throws<TenantDomainException>(() => membership.Rename(
            new string('a', Membership.DisplayNameMaxLength + 1), InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    [Fact]
    public void RenameAcceptsANameExactlyAsLongAsTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());
        var name = new string('a', Membership.DisplayNameMaxLength);

        membership.Rename(name, InvitedAt.AddHours(1));

        Assert.Equal(name, membership.DisplayName);
    }

    // El nombre no cambia el acceso, así que se puede cargar en cualquier estado.
    [Fact]
    public void RenameWorksOnASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.Suspend(InvitedAt.AddHours(2));

        Assert.True(membership.Rename("Ana María Pérez", InvitedAt.AddHours(3)));
        Assert.Equal(MembershipState.Suspended, membership.State);
    }

    // D3: el owner entra por register-tenant, no por invitación, así que nace sin nombre y lo
    // carga desde el roster. La protección de owner no alcanza al nombre.
    [Fact]
    public void TheOwnerStartsWithoutANameAndCanBeNamedLater()
    {
        var owner = CreateOwner();

        Assert.Null(owner.DisplayName);
        Assert.True(owner.Rename("Laura Gómez", InvitedAt.AddHours(1)));
        Assert.Equal("Laura Gómez", owner.DisplayName);
    }

    // Nadie consume un renombre fuera de Tenancy: el PDF lee el nombre cuando se genera.
    [Fact]
    public void RenameRaisesNoDomainEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.Rename("Ana María Pérez", InvitedAt.AddHours(1));

        Assert.Empty(membership.DomainEvents);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~MembershipTests"
```

Esperado: no compila — `error CS1061: 'Membership' does not contain a definition for 'Rename'` (y `'DisplayName'`), y `error CS0117: 'Membership' does not contain a definition for 'DisplayNameMaxLength'`. Un fallo de compilación es un RED válido: es la ausencia de los miembros que la prueba afirma. Pegar la salida.

- [ ] **Step 3: Implementar**

En `Membership.cs`, debajo de `AdminRole` (`:28`):

```csharp
    /// <summary>
    /// Largo máximo del nombre de la persona. Es el mismo de la columna <c>display_name</c> y del
    /// validador de Application: las tres capas tienen que decir lo mismo.
    /// </summary>
    public const int DisplayNameMaxLength = 150;
```

Debajo de `Origin` (`:67`):

```csharp
    /// <summary>
    /// El nombre con el que el tenant presenta a esta persona; hoy lo imprime el PDF de
    /// cotización en la ficha "Asesor". Vive en la membresía y no en el usuario de Identity
    /// porque el usuario es global: con el nombre ahí, el admin de otro tenant cambiaría lo que
    /// imprimen los PDFs de este. Nulo en las filas anteriores a este cambio y en el owner de
    /// registro, que nunca pasó por una invitación; se carga desde el roster con
    /// <see cref="Rename"/>.
    /// </summary>
    public string? DisplayName { get; private set; }
```

Después de `ChangeRoles` (`:451`), antes de `PullDomainEvents`:

```csharp
    /// <summary>
    /// Cambia el nombre de la persona. Vale en cualquier estado: el nombre es presentación y no
    /// cambia el acceso.
    /// </summary>
    /// <returns>
    /// <c>false</c> —sin tocar versión ni fecha— cuando el nombre normalizado es el que ya
    /// tiene. Un guardado repetido no invalida el If-Match de otra pestaña ni deja auditado un
    /// cambio que no ocurrió.
    /// </returns>
    public bool Rename(string displayName, DateTimeOffset occurredAt)
    {
        var normalized = NormalizeDisplayName(displayName);
        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        DisplayName = normalized;
        Version++;
        UpdatedAt = occurredAt;
        return true;
    }
```

Junto a `ValidateOrigin` (`:490`):

```csharp
    private static string NormalizeDisplayName(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > DisplayNameMaxLength)
        {
            throw new TenantDomainException(
                "tenancy.membership.display_name_invalid",
                $"Membership display name must be a non-empty value of at most {DisplayNameMaxLength} characters.");
        }

        return normalized;
    }
```

`CreateActive` no se toca: sin asignación, `DisplayName` queda en `null`.

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
```

Esperado: PASS el proyecto entero, las nuevas y las que ya existían. Pegar la salida.

**No correr todavía la integración de Tenancy:** EF ya mapea `DisplayName` por convención a una columna `"DisplayName"` que no existe, y va a fallar con `42703`. Lo cierra Task 2. No pushear entre Task 1 y Task 2.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs
git commit -m "feat(tenancy): nombre de la persona en la membresia"
```

---

### Task 2: Persistencia — columna y migración

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:94-96` (después de `Origin`)
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddMembershipDisplayName.cs` y `.Designer.cs` (generados)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs` (regenerado)
- Test: el comando `dotnet ef migrations has-pending-model-changes` y `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests`

**Interfaces:**
- Consumes: `Membership.DisplayName`, `Membership.DisplayNameMaxLength` (Task 1).
- Produces: columna `tenancy.memberships.display_name character varying(150) null`.

El contexto real es `TenancyDbContext` (`TenancyDbContext.cs:8`), con factory de diseño `TenancyDbContextFactory` (`TenancyDbContextFactory.cs:6`). La última migración de Tenancy es `20260831193140_AddMembershipInvitationTokenHash`.

- [ ] **Step 1: RED — el modelo va delante de las migraciones**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations has-pending-model-changes --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext
```

Esperado: exit code distinto de 0 con `Changes have been made to the model since the last migration. Add a new migration.` (es la propiedad de Task 1, mapeada por convención). Pegar la salida.

- [ ] **Step 2: Mapear la columna**

En `TenancyDbContext.ConfigureMembership`, después del bloque de `Origin` (`:94-96`):

```csharp
        // Nulo en las filas anteriores a este cambio y en el owner de registro, que no pasó por
        // una invitación: el nombre se carga después desde el roster. El largo sale del agregado
        // para que columna, dominio y validador no puedan divergir.
        membership.Property(value => value.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Membership.DisplayNameMaxLength);
```

- [ ] **Step 3: Generar la migración con el factory de diseño**

```powershell
dotnet ef migrations add AddMembershipDisplayName --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Abrir el `<timestamp>_AddMembershipDisplayName.cs` generado y verificar que `Up` es exactamente esto, sin otras operaciones colgadas:

```csharp
            migrationBuilder.AddColumn<string>(
                name: "display_name",
                schema: "tenancy",
                table: "memberships",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
```

y `Down` un `DropColumn` de `display_name` en `tenancy.memberships`. Si aparece cualquier otra operación, parar: el snapshot tenía deuda previa y no se mezcla con este cambio.

- [ ] **Step 4: GREEN — sin cambios pendientes**

```powershell
dotnet ef migrations has-pending-model-changes --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext
```

Esperado: `No changes have been made to the model since the last migration.` Pegar la salida.

- [ ] **Step 5: La integración de Tenancy vuelve a verde**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
```

Esperado: mismo resultado que el baseline para este proyecto, comparado por nombre. Pegar el resumen.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence
git commit -m "feat(tenancy): persistir el nombre de la membresia"
```

---

### Task 3: Barrido — toda invitación de las pruebas manda `displayName`

Va **antes** del cambio de contrato: la API todavía ignora el campo, así que agregarlo no cambia ningún resultado, y cuando Task 4 lo vuelva obligatorio ninguna prueba ajena se cae. Este paso no tiene RED propio. La prueba de que hace falta es la RED de Task 4, que sin él tumbaría estas pruebas con 422 (hallazgo 4).

**Files (lista completa, verificada con `rg "memberships\"" tests`):**
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs:391-400` (cuerpo crudo de `InviteWithUnknownRoleIsRejected`) y `:814-827` (helper `InviteAsync`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs:411-428` (helper `InviteAsync`)
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/InvitationApiTests.cs:280`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs:149`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs:159`, `:228`, `:277`, `:307`
- Modify: `tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InvitationNotificationTests.cs:86`
- Modify: `tests/Modules/Audit/Modules.Audit.IntegrationTests/AuditRecordingTests.cs:163`
- Modify: `tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs:285` (no está en la lista de la spec)

Sin cambios, verificado: ningún seeder invita (`TenancySeeder.cs:73` usa `CreateActive`); `QuotationsApiHarness` y `ReportingApiHarness` sólo registran tenants; `new InviteMemberCommand(` sólo aparece en `MembershipEndpoints.cs:185`.

**Interfaces:**
- Consumes: nada de producción.
- Produces:
  - `MembershipApiTests.InviteAsync(HttpClient client, string tenantId, string email, string[]? roles = null, string displayName = DefaultDisplayName)`
  - `MembershipLifecycleApiTests.InviteAsync(HttpClient client, string tenantId, string email, IReadOnlyCollection<string>? roles = null, string displayName = DefaultDisplayName)`
  - En los dos: `private const string DefaultDisplayName = "Ana Pérez";`

- [ ] **Step 1: `MembershipApiTests`**

Junto a `BillingRoles` (`:20`):

```csharp
    private const string DefaultDisplayName = "Ana Pérez";
```

El cuerpo crudo de `InviteWithUnknownRoleIsRejected` (`:395-399`). Sin el nombre, el validador —que corre antes que el catálogo de roles— devolvería `validation.failed` en vez de `role_unknown`:

```csharp
            Content = JsonContent.Create(new
            {
                email = NewEmail(),
                displayName = DefaultDisplayName,
                roles = UnknownRoles
            })
```

El helper (`:814-827`):

```csharp
    private static async Task<HttpResponseMessage> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        string[]? roles = null,
        string displayName = DefaultDisplayName)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName, roles = roles ?? DefaultRoles })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 2: `MembershipLifecycleApiTests`**

Junto a `AdminRoles` (`:355`):

```csharp
    private const string DefaultDisplayName = "Ana Pérez";
```

El helper (`:411-428`):

```csharp
    private static async Task<Guid> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string>? roles = null,
        string displayName = DefaultDisplayName)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName, roles = roles ?? AdvisorRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        return membership!.Id;
    }
```

- [ ] **Step 3: Los helpers de una sola línea**

`InvitationApiTests.cs:280`, `AuthSessionApiTests.cs:149`, `InvitationNotificationTests.cs:86` y `AuditRecordingTests.cs:163` tienen `new { email, roles = DefaultRoles }`. Cada uno pasa a:

```csharp
            Content = JsonContent.Create(new { email, displayName = "Ana Pérez", roles = DefaultRoles })
```

conservando la coma final donde ya estaba (`InvitationNotificationTests.cs:86` y `AuditRecordingTests.cs:163` terminan en `),`).

`OrphanUserCleanupTests.cs:285` tiene `new { email, roles = AdvisorRoles }` y pasa a:

```csharp
            Content = JsonContent.Create(new { email, displayName = "Ana Pérez", roles = AdvisorRoles })
```

- [ ] **Step 4: `RealAuthenticationApiTests`, cuatro cuerpos**

`:159`:

```csharp
            Content = JsonContent.Create(new { email = memberEmail, displayName = "Ana Pérez", roles = AdvisorRoles }),
```

`:228`:

```csharp
            Content = JsonContent.Create(new { email = secondOwnerEmail, displayName = "Ana Pérez", roles = AdvisorRoles }),
```

`:277` y `:307`:

```csharp
            Content = JsonContent.Create(new { email = NewEmail(), displayName = "Ana Pérez", roles = AdvisorRoles }),
```

- [ ] **Step 5: Verificar que el barrido es completo**

```powershell
rg -n "JsonContent.Create\(new \{ email" tests
rg -n -A3 '/memberships.\)' tests
```

(El `.` reemplaza a la comilla de cierre: PowerShell 5.1 se come las comillas embebidas al pasarlas a un `.exe`.)

Esperado: cada cuerpo de invitación que aparece lleva `displayName`. El único cuerpo multilínea es el de `MembershipApiTests.cs:395`, ya cubierto en Step 1.

- [ ] **Step 6: Correr los proyectos tocados y comparar con el baseline**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~InvitationNotificationTests"
dotnet test tests/Modules/Audit/Modules.Audit.IntegrationTests --filter "FullyQualifiedName~AuditRecordingTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~OrphanUserCleanupTests"
```

Esperado: exactamente el mismo resultado que el baseline, por nombre. La API todavía ignora el campo. Pegar los resúmenes.

- [ ] **Step 7: Commit**

```powershell
git branch --show-current
git add tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests tests/Modules/Notifications/Modules.Notifications.IntegrationTests/InvitationNotificationTests.cs tests/Modules/Audit/Modules.Audit.IntegrationTests/AuditRecordingTests.cs tests/Modules/Identity/Modules.Identity.IntegrationTests/OrphanUserCleanupTests.cs
git commit -m "test: mandar el nombre en toda invitacion de las pruebas de integracion"
```

---

### Task 4: Invitar exige el nombre

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs:98-145` (`Invite`) y `:261-310` (`Reinvite`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs:9-22`, `:74-83`, `:122-142`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs:5-29`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs:177-208`, `:242-258`
- Modify: `README.md:449-485`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs:248-261`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs`

**Interfaces:**
- Consumes: `Membership.NormalizeDisplayName`, `Membership.DisplayNameMaxLength` (Task 1); `MembershipApiTests.InviteAsync(..., string displayName = DefaultDisplayName)` (Task 3).
- Produces:
  - `Membership.Invite(MembershipId id, Guid userId, TenantId tenantId, string displayName, IEnumerable<string> roles, string origin, string invitationToken, string invitationTokenHash, DateTimeOffset invitedAt, TimeSpan timeToLive)`
  - `Membership.Reinvite(string displayName, IEnumerable<string> roles, string invitationToken, string invitationTokenHash, DateTimeOffset occurredAt, TimeSpan timeToLive)`
  - `InviteMemberCommand(TenantId TenantId, string Email, string DisplayName, IReadOnlyCollection<string> Roles, string CorrelationId)`
  - `MembershipDto(MembershipId Id, Guid UserId, string? DisplayName, TenantId TenantId, MembershipState State, IReadOnlyCollection<string> Roles, DateTimeOffset InvitedAt, DateTimeOffset? AcceptedAt, DateTimeOffset ExpiresAt, long Version)`
  - `MembershipInviteRequest(string Email, string DisplayName, IReadOnlyCollection<string>? Roles)`
  - `MembershipResponse(Guid Id, Guid UserId, string Email, string? DisplayName, Guid TenantId, string State, IReadOnlyCollection<string> Roles, DateTimeOffset InvitedAt, DateTimeOffset? AcceptedAt, DateTimeOffset ExpiresAt, long Version)`

- [ ] **Step 1: Pruebas unitarias que fallan**

En `MembershipTests.cs`, junto a las constantes de token (`:14-17`):

```csharp
    private const string InvitedName = "Ana Pérez";
```

Agregar, antes de los helpers:

```csharp
    [Fact]
    public void InviteStoresTheTrimmedDisplayName()
    {
        var membership = Membership.Invite(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            "  Ana Pérez  ",
            ["advisor"],
            "invitation",
            Token,
            TokenHash,
            InvitedAt,
            Ttl);

        Assert.Equal("Ana Pérez", membership.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InviteRejectsABlankDisplayName(string displayName)
    {
        var error = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                TenantId.New(),
                displayName,
                ["advisor"],
                "invitation",
                Token,
                TokenHash,
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    [Fact]
    public void InviteRejectsADisplayNameLongerThanTheColumn()
    {
        var error = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                TenantId.New(),
                new string('a', Membership.DisplayNameMaxLength + 1),
                ["advisor"],
                "invitation",
                Token,
                TokenHash,
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    // D5: renovar una invitación vencida reescribe el nombre junto con los roles y la ventana.
    [Fact]
    public void ReinviteOverwritesTheDisplayName()
    {
        var membership = Invite(Guid.CreateVersion7());
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(
            "  Ana María Pérez  ", ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

        Assert.Equal("Ana María Pérez", membership.DisplayName);
    }

    // Mismo criterio que ARejectedReinviteLeavesRolesUntouched: un rechazo no deja nada a medias,
    // ni roles nuevos ni un token rotado.
    [Fact]
    public void AReinviteWithABlankNameLeavesTheMembershipUntouched()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                "   ", ["tenancy.admin"], RenewedToken, RenewedTokenHash, lapsed, Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(["advisor"], membership.Roles);
        Assert.Equal(TokenHash, membership.InvitationTokenHash);
        Assert.Equal(version, membership.Version);
    }
```

Pasar a la firma nueva las llamadas que ya existen:

- Helper `Invite(Guid userId)` (`:599-609`): agregar `InvitedName,` después de `TenantId.New(),`.
- `InviteRequiresAnInvitationToken` (`:280-289`) e `InviteWithEmptyUserThrows` (`:338-347`): agregar `InvitedName,` después de `TenantId.New(),`.
- Las nueve llamadas a `Reinvite` (`:305`, `:371`, `:389`, `:404`, `:421-422`, `:436-441`, `:461-466`, `:480-485`, `:502-507`): `InvitedName` va como **primer** argumento, antes de la colección de roles. Las de una línea quedan así:

  ```csharp
          membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);
  ```

  y las multilínea así (la línea de roles gana el nombre adelante, el resto no cambia):

  ```csharp
              () => membership.Reinvite(
                  InvitedName,
                  ["advisor"],
  ```

  En `:371` y `:503` la colección es `["tenancy.admin"]`: sólo se antepone `InvitedName`.

En `InvitationServiceTests.cs`, `InvitedMembership` (`:252-261`): agregar `"Ana Pérez",` después de `tenantId,`.

- [ ] **Step 2: Pruebas de integración que fallan**

En `MembershipApiTests.cs`: agregar `using System.Text.Json;` y sumar `string? DisplayName` como **último** parámetro de `MembershipPayload` (`:895-905`):

```csharp
    private sealed record MembershipPayload(
        Guid Id,
        Guid UserId,
        string Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version,
        string? DisplayName);
```

Junto a los helpers privados:

```csharp
    // El formulario marca el input leyendo las claves de `errors`, en PascalCase: es lo único
    // que se afirma del 422 de validación.
    private static async Task<string[]> ValidationFieldsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }
```

Y las pruebas:

```csharp
    // El 422 tiene que ser validation.failed con el mapa `errors`: es el único que el formulario
    // de invitación sabe leer para marcar el input. El código propio del dominio
    // (display_name_invalid) queda como segunda capa, detrás del validador.
    [Fact]
    public async Task InviteWithoutADisplayNameMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{TenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = NewEmail(), roles = DefaultRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("DisplayName", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task InviteWithADisplayNameLongerThanTheColumnMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(
            client, TenantId, NewEmail(), displayName: new string('a', 151));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("DisplayName", await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task InviteReturnsAndStoresTheTrimmedDisplayName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(
            client, TenantId, NewEmail(), displayName: "  Ana Pérez  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Ana Pérez", membership!.DisplayName);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var row = await QueryRowAsync(
            connection,
            "SELECT display_name FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.Equal("Ana Pérez", row![0]);
    }

    // D5: una invitación viva es una no-op, y el nombre del cuerpo se ignora igual que los roles.
    // Para renombrar a alguien está PATCH .../display-name.
    [Fact]
    public async Task InvitingAgainWhileTheInvitationIsLiveKeepsTheFirstName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        await InviteAsync(client, TenantId, email, displayName: "Ana Pérez");
        var second = await InviteAsync(client, TenantId, email, displayName: "Otra Persona");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var membership = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Ana Pérez", membership!.DisplayName);
    }

    // D5: renovar una invitación vencida sí reescribe el nombre, junto con los roles y la ventana.
    [Fact]
    public async Task ReinvitingALapsedInvitationOverwritesTheName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email, displayName: "Ana Pérez");
        var invited = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        await LapseInvitationAsync(database, invited!.Id);

        var renewed = await InviteAsync(client, TenantId, email, displayName: "Ana María Pérez");

        Assert.Equal(HttpStatusCode.Created, renewed.StatusCode);
        var membership = await renewed.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(invited.Id, membership!.Id);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
    }
```

- [ ] **Step 3: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
```

Esperado: no compila — `error CS1501: No overload for method 'Invite' takes 10 arguments` y `error CS1501: No overload for method 'Reinvite' takes 6 arguments`. Pegar la salida.

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~MembershipApiTests"
```

Esperado (el proyecto de integración compila, porque no referencia la firma del dominio): FAIL en las cinco nuevas. `InviteWithoutADisplayNameMarksTheField` y `...LongerThanTheColumn...` con `Expected: UnprocessableEntity, Actual: Created`; las otras tres con `Assert.Equal() Failure` sobre `DisplayName` (`Actual: null`), o sobre `display_name` vacío en la fila. Pegar la salida.

- [ ] **Step 4: Dominio — `Invite` y `Reinvite` reciben el nombre**

En `Membership.cs`, la firma de `Invite` (`:98-107`) pasa a:

```csharp
    public static Membership Invite(
        MembershipId id,
        Guid userId,
        TenantId tenantId,
        string displayName,
        IEnumerable<string> roles,
        string origin,
        string invitationToken,
        string invitationTokenHash,
        DateTimeOffset invitedAt,
        TimeSpan timeToLive)
```

Después de `ValidateInvitationToken(invitationToken, invitationTokenHash);` (`:123`):

```csharp
        var normalizedName = NormalizeDisplayName(displayName);
```

y el inicializador del `new Membership(...)` (`:133-135`) pasa a:

```csharp
        {
            InvitationTokenHash = invitationTokenHash,
            DisplayName = normalizedName,
        };
```

La firma de `Reinvite` (`:261-266`) pasa a:

```csharp
    public void Reinvite(
        string displayName,
        IEnumerable<string> roles,
        string invitationToken,
        string invitationTokenHash,
        DateTimeOffset occurredAt,
        TimeSpan timeToLive)
```

Después de `ValidateInvitationToken(invitationToken, invitationTokenHash);` (`:275`), antes de los chequeos de estado, para que un nombre inválido no deje nada a medias:

```csharp
        var normalizedName = NormalizeDisplayName(displayName);
```

Y en el bloque de mutación, junto a `_roles.AddRange(NormalizeRoles(roles));` (`:292`):

```csharp
        DisplayName = normalizedName;
```

Agregar al `<summary>` de `Reinvite` (`:246`) la línea:

```csharp
    /// El nombre se reescribe junto con los roles: quien renueva una invitación vencida la está
    /// armando de nuevo (spec 2026-09-11, D5).
```

- [ ] **Step 5: Application — comando, validador y handler**

En `InviteMember.cs`, el comando y el validador (`:9-22`):

```csharp
public sealed record InviteMemberCommand(
    TenantId TenantId,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    string CorrelationId) : ICommand<MembershipDto>;

public sealed class InviteMemberValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberValidator()
    {
        RuleFor(command => command.Email).NotEmpty().MaximumLength(254);
        // El dominio ya rechaza el nombre vacío con su propio código, pero ese 422 no trae el
        // mapa `errors` y el formulario no sabría qué input marcar. Éste sí.
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        RuleFor(command => command.Roles).NotNull();
    }
}
```

La llamada a `Membership.Invite` (`:74-83`):

```csharp
        var membership = Membership.Invite(
            MembershipId.New(),
            userId,
            command.TenantId,
            command.DisplayName,
            command.Roles,
            Origin,
            invitationToken,
            InvitationTokens.HashOf(invitationToken),
            clock.UtcNow,
            Membership.DefaultInvitationTimeToLive);
```

En `ReinviteExistingAsync`, el comentario de la no-op (`:122-124`) pasa a:

```csharp
        // Una invitación viva y una membresía activa son las dos no-ops. Renovar una invitación
        // viva movería un plazo con el que alguien cuenta e invalidaría el link que ya está en
        // su bandeja. El nombre del cuerpo se ignora igual que los roles: para renombrar a un
        // miembro está PATCH .../display-name (spec 2026-09-11, D5).
```

y la llamada a `Reinvite` (`:137-142`):

```csharp
        existing.Reinvite(
            command.DisplayName,
            command.Roles,
            invitationToken,
            InvitationTokens.HashOf(invitationToken),
            now,
            Membership.DefaultInvitationTimeToLive);
```

`MembershipDto.cs` completo:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="DisplayName">
/// Nulo cuando la membresía es anterior al nombre o es la del owner: el reinvite no-op devuelve
/// la fila existente tal como está (spec 2026-09-11, D5).
/// </param>
public sealed record MembershipDto(
    MembershipId Id,
    Guid UserId,
    string? DisplayName,
    TenantId TenantId,
    MembershipState State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

public static class MembershipMappings
{
    public static MembershipDto ToDto(this Membership membership) =>
        new(
            membership.Id,
            membership.UserId,
            membership.DisplayName,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}
```

- [ ] **Step 6: Api — request y response**

En `MembershipEndpoints.cs`, `InviteAsync` (`:184-190`):

```csharp
        var membership = await dispatcher.SendAsync(
            new InviteMemberCommand(
                new TenantId(tenantId),
                request.Email,
                request.DisplayName,
                request.Roles ?? [],
                httpContext.TraceIdentifier),
            cancellationToken);
```

`ToResponse` (`:197-208`):

```csharp
    private static MembershipResponse ToResponse(MembershipDto membership, string email) =>
        new(
            membership.Id.Value,
            membership.UserId,
            email,
            membership.DisplayName,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
```

Los records (`:242-258`):

```csharp
/// <param name="DisplayName">
/// Obligatorio. Sin él —ausente, vacío o de más de 150 caracteres— responde 422
/// <c>validation.failed</c> con <c>errors.DisplayName</c>, el único 422 que el formulario sabe
/// marcar en el input.
/// </param>
public sealed record MembershipInviteRequest(
    string Email,
    string DisplayName,
    IReadOnlyCollection<string>? Roles);

public sealed record MembershipRolesUpdateRequest(IReadOnlyCollection<string>? Roles);

public sealed record MembershipResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string? DisplayName,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);
```

- [ ] **Step 7: README — el contrato de invitar**

En `README.md`, el cuerpo del ejemplo (`:453-456`) pasa a:

```powershell
$body = @{
  email = "new.member@example.com"
  displayName = "Ana Pérez"
  roles = @("advisor")
} | ConvertTo-Json
```

En la respuesta de ejemplo (`:469-478`), agregar `"displayName": "Ana Pérez",` después de la línea de `"userId"`. Después del párrafo "Repetir secuencialmente la invitación..." (`:481-482`), agregar:

```markdown
`displayName` es obligatorio: se guarda sin espacios a los costados y admite entre 1 y 150
caracteres. Sin él responde `422 validation.failed` con `errors.DisplayName`. Una invitación
viva o una membresía activa ignoran el nombre del cuerpo; sólo renovar una invitación vencida
lo reescribe. Para cambiárselo a un miembro está `PATCH .../display-name`.
```

- [ ] **Step 8: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
```

Esperado: PASS los dos proyectos enteros, con el mismo resultado que el baseline por nombre más las nuevas en verde. Pegar la salida.

- [ ] **Step 9: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs README.md tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs
git commit -m "feat(tenancy): pedir el nombre al invitar"
```

---

### Task 5: Roster — mostrar y buscar por nombre

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs:12-40` (DTO y mapeo), `:148-167` (`ApplySearch`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs:210-222` (`ToListItemResponse`), `:260-271` (`MembershipListItemResponse`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs`

**Interfaces:**
- Consumes: `Membership.DisplayName` (Task 1); `InviteAsync(..., displayName:)` (Task 3); invitar con nombre (Task 4).
- Produces:
  - `MembershipListItemDto(MembershipId Id, Guid UserId, string? Email, string? DisplayName, TenantId TenantId, MembershipState State, IReadOnlyCollection<string> Roles, DateTimeOffset InvitedAt, DateTimeOffset? AcceptedAt, DateTimeOffset ExpiresAt, long Version, bool IsOwner)`
  - `MembershipListItemResponse(Guid Id, Guid UserId, string? Email, string? DisplayName, Guid TenantId, string State, IReadOnlyCollection<string> Roles, DateTimeOffset InvitedAt, DateTimeOffset? AcceptedAt, DateTimeOffset ExpiresAt, long Version, bool IsOwner)`
  - `ToListItemDto(this Membership membership, string? email)`: misma firma, ahora con `DisplayName`. La usan `ListMemberships`, `SuspendMember`, `RemoveMember`, `ReactivateMember`, `UpdateMemberRoles` y el handler de Task 6.

- [ ] **Step 1: Pruebas que fallan**

En `MembershipApiTests.cs`, sumar `string? DisplayName` como **último** parámetro de `MembershipListItemPayload` (`:907-917`):

```csharp
    private sealed record MembershipListItemPayload(
        Guid Id,
        Guid UserId,
        string? Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version,
        string? DisplayName);
```

Y las pruebas:

```csharp
    [Fact]
    public async Task ListShowsEachMembersDisplayName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var invited = await InviteAsync(
            client, TenantId, NewEmail(), displayName: "Valentina Ríos");
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        var row = Assert.Single(list!.Items, item => item.Id == membership!.Id);
        Assert.Equal("Valentina Ríos", row.DisplayName);
    }

    // La búsqueda encuentra por nombre además de por correo, sin distinguir mayúsculas: el
    // nombre es lo que la persona que administra el roster recuerda.
    [Fact]
    public async Task SearchFindsAMemberByName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var wanted = await InviteAsync(
            client, TenantId, NewEmail(), displayName: "Valentina Ríos");
        var wantedMembership = await wanted.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        await InviteAsync(client, TenantId, NewEmail(), displayName: "Carlos Mejía");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships?search=valentina",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        var only = Assert.Single(list!.Items);
        Assert.Equal(wantedMembership!.Id, only.Id);
    }

    // Suspend, remove, reactivate y roles devuelven la fila por el mismo mapeo que el listado:
    // la pantalla la repinta con lo que recibe, y sin el nombre la celda "Persona" se vaciaría.
    [Fact]
    public async Task ReactivateReturnsTheRowWithItsDisplayName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var invited = await InviteAsync(
            client, TenantId, NewEmail(), displayName: "Valentina Ríos");
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        await SetStateAsync(database, membership!.Id, "Suspended");

        var response = await ReactivateAsync(client, TenantId, membership.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Valentina Ríos", row!.DisplayName);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~MembershipApiTests.ListShowsEachMembersDisplayName|FullyQualifiedName~MembershipApiTests.SearchFindsAMemberByName|FullyQualifiedName~MembershipApiTests.ReactivateReturnsTheRowWithItsDisplayName"
```

Esperado: FAIL las tres. `ListShows...` y `Reactivate...` con `Assert.Equal() Failure` (`Actual: null`); `SearchFindsAMemberByName` con `Assert.Single() Failure: The collection was empty`. Pegar la salida.

- [ ] **Step 3: DTO, mapeo y búsqueda**

En `ListMemberships.cs`, el record y el mapeo (`:12-40`):

```csharp
/// <param name="DisplayName">
/// El nombre que el tenant cargó para esta persona. Nulo en las membresías anteriores al nombre
/// y en el owner hasta que alguien lo cargue: la pantalla muestra entonces sólo el correo.
/// </param>
/// <param name="IsOwner">
/// Marca la membresía del owner (Origin de registro, ADR 0017), la que el dominio protege de
/// suspender, quitar o perder `admin`. Viaja en el contrato para que el frontend deshabilite
/// esas acciones en vez de descubrir el 422 al intentarlas.
/// </param>
public sealed record MembershipListItemDto(
    MembershipId Id,
    Guid UserId,
    string? Email,
    string? DisplayName,
    TenantId TenantId,
    MembershipState State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version,
    bool IsOwner);

public static class MembershipListItemMappings
{
    public static MembershipListItemDto ToListItemDto(this Membership membership, string? email) =>
        new(
            membership.Id,
            membership.UserId,
            email,
            membership.DisplayName,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version,
            membership.Origin == Membership.RegistrationOrigin);
}
```

`ApplySearch` (`:148-167`):

```csharp
    /// <summary>
    /// La búsqueda es por correo o por nombre, los dos datos con los que alguien identifica a una
    /// persona en el roster; el UserId es un GUID que nadie escribe de memoria. Un campo nulo no
    /// coincide con ningún texto, así que una membresía vieja sin nombre se sigue encontrando por
    /// su correo.
    /// </summary>
    private static IReadOnlyList<MembershipListItemDto> ApplySearch(
        IReadOnlyList<MembershipListItemDto> items,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return items;
        }

        var term = search.Trim();
        return items
            .Where(item => Matches(item.Email, term) || Matches(item.DisplayName, term))
            .ToList();
    }

    private static bool Matches(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 4: Response**

En `MembershipEndpoints.cs`, `ToListItemResponse` (`:210-222`):

```csharp
    private static MembershipListItemResponse ToListItemResponse(MembershipListItemDto membership) =>
        new(
            membership.Id.Value,
            membership.UserId,
            membership.Email,
            membership.DisplayName,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version,
            membership.IsOwner);
```

`MembershipListItemResponse` (`:260-271`):

```csharp
/// <param name="DisplayName">
/// Nulo en membresías anteriores al nombre y en el owner hasta que se cargue. La celda
/// "Persona" muestra entonces sólo el correo, con el aviso "Sin nombre".
/// </param>
public sealed record MembershipListItemResponse(
    Guid Id,
    Guid UserId,
    string? Email,
    string? DisplayName,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version,
    bool IsOwner);
```

- [ ] **Step 5: Verificar que el mapeo es único**

```powershell
rg -n "new MembershipListItemDto\(|ToListItemDto\(" src/Modules/Tenancy
```

Esperado: ningún `new MembershipListItemDto(` —la única construcción es el `new(` tipado por destino dentro de `ToListItemDto`—, y `ToListItemDto(` seis veces: la definición en `ListMemberships.cs` y las llamadas en `ListMemberships`, `SuspendMember`, `RemoveMember`, `ReactivateMember` y `UpdateMemberRoles`. Si aparece otra construcción, llenarle `DisplayName` también.

- [ ] **Step 6: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
```

Esperado: PASS el proyecto entero. Pegar la salida.

- [ ] **Step 7: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs
git commit -m "feat(tenancy): mostrar y buscar el nombre en el roster"
```

---

### Task 6: Editar el nombre — `PATCH .../display-name`

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` (mapeo después de `/roles` `:62-70`; método después de `UpdateRolesAsync` `:100`; record junto a `MembershipRolesUpdateRequest` `:246`)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:86-88`
- Modify: `README.md` (sección nueva antes de `### Aceptación de la invitación`, `:496`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `Membership.Rename(string, DateTimeOffset) → bool` (Task 1); `ToListItemDto` con `DisplayName` (Task 5); `MembershipLoader.LoadAsync`, `IUserDirectory.GetEmailAsync`, `IAuditRecorder.Record`, `ITenancyUnitOfWork.SaveChangesAsync`, `RequestConcurrencyException`, `RequestForbiddenException`, `PreconditionRequiredException`, `TryParseVersion` (ya existen).
- Produces:
  - `UpdateMemberDisplayNameCommand(TenantId TenantId, MembershipId MembershipId, string DisplayName, long ExpectedVersion, string CorrelationId) : ICommand<MembershipListItemDto>`
  - `UpdateMemberDisplayNameValidator`, `UpdateMemberDisplayNameHandler`
  - `MembershipDisplayNameUpdateRequest(string? DisplayName)`
  - `PATCH /api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}/display-name` → `200 MembershipListItemResponse` + `ETag`.

El validador lo registra solo `AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>()` (`QepServiceCollectionExtensions.cs:388`, mismo assembly). El handler se registra a mano, como los otros cinco del roster.

- [ ] **Step 1: Pruebas que fallan**

En `MembershipLifecycleApiTests.cs`: agregar `using System.Text.Json;` y sumar `string? DisplayName` como **último** parámetro de `MembershipListItemPayload` (`:529-540`):

```csharp
    private sealed record MembershipListItemPayload(
        Guid Id,
        Guid UserId,
        string? Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version,
        bool IsOwner,
        string? DisplayName);
```

Helpers, junto a `SendRolesAsync`:

```csharp
    private static async Task<HttpResponseMessage> SendDisplayNameAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string? displayName,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/display-name")
        {
            Content = JsonContent.Create(new { displayName })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<string>> AuditOutcomesAsync(
        string connectionString,
        Guid membershipId,
        string action)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT outcome FROM audit.entries
            WHERE resource_id = @resourceId AND action = @action
            """,
            connection);
        command.Parameters.AddWithValue("resourceId", membershipId.ToString());
        command.Parameters.AddWithValue("action", action);
        var outcomes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            outcomes.Add(reader.GetString(0));
        }

        return outcomes;
    }
```

Las pruebas, después de `ManageOfUnknownMembershipIsNotFound` (`:352`):

```csharp
    // 200 con la fila entera y el ETag nuevo: el front repinta la fila y ya tiene qué mandar en
    // el próximo If-Match. La auditoría va en la misma transacción que el cambio.
    [Fact]
    public async Task RenameReturnsTheRowWithANewEtagAndIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, memberId, "  Ana María Pérez  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Ana María Pérez", membership!.DisplayName);
        Assert.Equal(2, membership.Version);
        var outcomes = await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.renamed");
        Assert.Equal("success", Assert.Single(outcomes));
    }

    // Guardar el mismo nombre no es un cambio: misma versión, y nada auditado.
    [Fact]
    public async Task RenamingToTheSameNameKeepsTheVersionAndRecordsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, displayName: "Ana Pérez");

        var response = await SendDisplayNameAsync(ownerClient, tenantId, memberId, "Ana Pérez");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Empty(await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.renamed"));
    }

    // D3: el owner entra por register-tenant, sin nombre, y lo carga desde el roster.
    [Fact]
    public async Task TheOwnerCanNameTheirOwnMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, ownerMembershipId, "Laura Gómez");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Laura Gómez", membership!.DisplayName);
        Assert.True(membership.IsOwner);
    }

    [Fact]
    public async Task RenameRequiresIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", expectedVersion: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem!.Code);
    }

    [Fact]
    public async Task RenameWithAStaleVersionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", expectedVersion: 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    // 403 y nunca 404: la ruta pide un tenant que no es el del contexto. El handler lo revalida
    // antes de tocar el repositorio (doble capa), así que no se entera de si el id existe.
    [Fact]
    public async Task RenameFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var otherClient = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await SendDisplayNameAsync(
            otherClient, tenantId, ownerMembershipId, "Ana María Pérez");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RenameRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        using var readerOnly = CreateClient(factory, Guid.CreateVersion7().ToString(), tenantId);
        readerOnly.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var response = await SendDisplayNameAsync(
            readerOnly, tenantId, memberId, "Ana María Pérez");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RenameWithABlankNameMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendDisplayNameAsync(ownerClient, tenantId, memberId, "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("DisplayName", out _));
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "(FullyQualifiedName~MembershipLifecycleApiTests&FullyQualifiedName~Renam)|FullyQualifiedName~MembershipLifecycleApiTests.TheOwnerCanNameTheirOwnMembership"
```

`Renam` y no `Rename`, a propósito: tiene que atrapar también `RenamingToTheSameName...`.

Esperado: FAIL las ocho. La ruta no existe, así que todas reciben `404 NotFound` donde esperan `OK`, `PreconditionRequired`, `PreconditionFailed`, `Forbidden` o `UnprocessableEntity`. Pegar la salida.

- [ ] **Step 3: Comando, validador y handler**

Crear `UpdateMemberDisplayName.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateMemberDisplayNameCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string DisplayName,
    long ExpectedVersion,
    string CorrelationId) : ICommand<MembershipListItemDto>;

/// <summary>
/// Texto libre, así que lleva validador aunque el dominio ya valide: el dominio da el código
/// (<c>display_name_invalid</c>), el validador da el campo (<c>errors.DisplayName</c>), que es
/// lo único con lo que el diálogo sabe marcar el input.
/// </summary>
public sealed class UpdateMemberDisplayNameValidator
    : AbstractValidator<UpdateMemberDisplayNameCommand>
{
    public UpdateMemberDisplayNameValidator()
    {
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Renombra a un miembro del roster (spec 2026-09-11, D4). Endpoint propio y no parte de
/// <c>PATCH .../roles</c>: son dos permisos distintos, y un PATCH mezclado obligaría a elegir la
/// autorización según los campos que vinieron.
///
/// Se permite en cualquier estado —el nombre es presentación y no cambia el acceso— y también
/// sobre la propia membresía, a diferencia de los roles: el owner carga su nombre desde acá.
/// </summary>
public sealed class UpdateMemberDisplayNameHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateMemberDisplayNameCommand> validator)
    : ICommandHandler<UpdateMemberDisplayNameCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        UpdateMemberDisplayNameCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        if (membership.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The membership changed after it was loaded.");
        }

        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: renombrar al mismo nombre es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        if (membership.Rename(command.DisplayName, now))
        {
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.membership.renamed",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
        return membership.ToListItemDto(email);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot rename members of this tenant.");
        }
    }
}
```

- [ ] **Step 4: Endpoint**

En `MembershipEndpoints.MapMembershipEndpoints`, después del mapeo de `/roles` (`:62-70`):

```csharp
        // Con If-Match, igual que `/roles`: dos administradores renombrando a la misma persona es
        // una carrera real, y el nombre termina impreso en un PDF que se le manda al cliente.
        group.MapPatch("/{membershipId:guid}/display-name", UpdateDisplayNameAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Accepts<MembershipDisplayNameUpdateRequest>("application/json")
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Después de `UpdateRolesAsync` (`:100`):

```csharp
    private static async Task<IResult> UpdateDisplayNameAsync(
        Guid tenantId,
        Guid membershipId,
        MembershipDisplayNameUpdateRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded membership version is required.");
        }

        var membership = await dispatcher.SendAsync(
            new UpdateMemberDisplayNameCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                request.DisplayName ?? string.Empty,
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{membership.Version}\"";
        return Results.Ok(ToListItemResponse(membership));
    }
```

Junto a `MembershipRolesUpdateRequest` (`:246`):

```csharp
/// <summary>
/// Nullable a propósito: un cuerpo sin el campo llega como vacío al validador y sale como 422
/// con <c>errors.DisplayName</c>, no como un 400 del binder que el diálogo no sabe leer.
/// </summary>
public sealed record MembershipDisplayNameUpdateRequest(string? DisplayName);
```

- [ ] **Step 5: Registrar el handler**

En `QepServiceCollectionExtensions.cs`, después de `UpdateMemberRolesHandler` (`:86-88`):

```csharp
        services.AddScoped<
            ICommandHandler<UpdateMemberDisplayNameCommand, MembershipListItemDto>,
            UpdateMemberDisplayNameHandler>();
```

- [ ] **Step 6: README — el endpoint nuevo**

En `README.md`, antes de `### Aceptación de la invitación` (`:496`):

````markdown
### Nombre del miembro

| Método  | Ruta                                                                  | Permiso              |
| ------- | --------------------------------------------------------------------- | -------------------- |
| `PATCH` | `/api/v1/tenants/{tenantId}/memberships/{membershipId}/display-name` | `advisorship.manage` |

Cambia el nombre con el que el tenant presenta a la persona, el que imprime el PDF de
cotización. Vale en cualquier estado de la membresía y sobre la propia: el owner, que entra por
`register-tenant` sin nombre, lo carga desde acá. Exige `If-Match` con la versión cargada, igual
que `PATCH .../roles`, y responde `200` con la fila del roster y el `ETag` nuevo. Guardar el
mismo nombre no sube la versión ni se audita; un cambio real se audita como
`tenancy.membership.renamed`.

```powershell
$body = @{ displayName = "Ana María Pérez" } | ConvertTo-Json
$patchHeaders = $headers.Clone()
$patchHeaders["If-Match"] = '"1"'

Invoke-RestMethod `
  -Method Patch `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/memberships/$membershipId/display-name" `
  -Headers $patchHeaders `
  -ContentType "application/json" `
  -Body $body
```

Sin `If-Match` responde `428 precondition.if_match_required`; con una versión vieja, `412`; con
un nombre vacío o de más de 150 caracteres, `422 validation.failed` con `errors.DisplayName`.
````

- [ ] **Step 7: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Esperado: PASS los dos proyectos enteros. `ArchitectureTests` confirma que el archivo nuevo de Application no referencia EF ni Npgsql. Pegar la salida.

- [ ] **Step 8: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs README.md tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs
git commit -m "feat(tenancy): editar el nombre de un miembro"
```

---

### Task 7: El lookup de asesora devuelve correo y nombre

Cambia el puerto sin cambiar lo que ven listados, ventas e historial (D1). La guarda de D1 son dos pruebas unitarias con un asesor que **sí** tiene nombre.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs` (reescritura)
- Modify: `src/Bootstrapper/QuotationAdvisorLookup.cs` (reescritura)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListQuotations.cs:126-140`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListSales.cs:119-130`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListQuotationHistory.cs:49-69`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs:38-39`, `:58`
- Modify (sólo comentarios que pasan a ser falsos): `src/Modules/Quotations/Modules.Quotations.Application/SalesDtos.cs:71-72`, `src/Bootstrapper/ReportingLookups.cs:14-19`, `src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs:7-12`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/SaleListApiTests.cs:43-44`, `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/SalesReportApiTests.cs:50-51`, `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs:99-102`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:110-124`, `ListSalesHandlerTests.cs`, `ListQuotationsHandlerTests.cs`

**Interfaces:**
- Consumes: `Membership.DisplayName` (Task 1), vía `IMembershipRepository.ListByTenantAsync` en el adaptador.
- Produces:
  - `public sealed record QuotationAdvisor(string? Email, string? DisplayName);`
  - `IQuotationAdvisorLookup.FindAsync(Guid tenantId, IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken)` → `Task<IReadOnlyDictionary<Guid, QuotationAdvisor>>`. Reemplaza a `FindEmailsAsync`, que desaparece.
  - `StubQuotationAdvisorLookup(string? email = null, string? displayName = null)` con `int FindCalls`.

- [ ] **Step 1: Pruebas y doble que fallan**

En `QuotationsTestDoubles.cs`, reemplazar `StubQuotationAdvisorLookup` (`:110-124`):

```csharp
internal sealed class StubQuotationAdvisorLookup(string? email = null, string? displayName = null)
    : IQuotationAdvisorLookup
{
    public int FindCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationAdvisor>>(
            membershipIds
                .Distinct()
                .ToDictionary(id => id, _ => new QuotationAdvisor(email, displayName)));
    }
}
```

En `ListSalesHandlerTests.cs`, el doble del `NewHandler` (`:142`) pasa a:

```csharp
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
```

y se agrega la guarda, después de `ListPutsTheClientAndTheQuotationTotalsOnEachRow`:

```csharp
    // El nombre de la asesora llega sólo al PDF (spec 2026-09-11, D1): la fila de ventas sigue
    // mostrando el correo aunque la membresía tenga nombre.
    [Fact]
    public async Task ListKeepsTheAdvisorEmailEvenWhenTheMemberHasAName()
    {
        var handler = NewHandler(NewCustomerLookup(), NewRow("VEN-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(page.Items).AdvisorEmail);
    }
```

En `ListQuotationsHandlerTests.cs`, el doble del `NewHandler` (`:100`) pasa a:

```csharp
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
```

y se agrega:

```csharp
    // D1: el listado de cotizaciones sigue con el correo; el nombre es sólo del PDF.
    [Fact]
    public async Task ListKeepsTheAdvisorEmailEvenWhenTheMemberHasAName()
    {
        var handler = NewHandler(NewCustomerLookup(), NewQuotation("QUO-2026-0001", ClientId));

        var page = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("asesora@qcode.co", Assert.Single(page.Items).AdvisorEmail);
    }
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ListSalesHandlerTests|FullyQualifiedName~ListQuotationsHandlerTests"
```

Esperado: no compila — `error CS0246: The type or namespace name 'QuotationAdvisor' could not be found` y `error CS0535: 'StubQuotationAdvisorLookup' does not implement interface member 'IQuotationAdvisorLookup.FindEmailsAsync(...)'`. Pegar la salida.

- [ ] **Step 3: El puerto**

Reemplazar `IQuotationAdvisorLookup.cs` por:

```csharp
namespace Modules.Quotations.Application;

/// <summary>Lo que Tenancy e Identity saben de la asesora de una cotización.</summary>
/// <param name="Email">El correo, de Identity. Null si el usuario ya no resuelve (dado de baja
/// en Identity). Es lo que muestran listados, historial y ventas.</param>
/// <param name="DisplayName">El nombre que el tenant cargó en la membresía. Null en las
/// membresías anteriores a que existiera y en el owner hasta que alguien lo cargue desde el
/// roster. Hoy lo usa sólo el PDF, que cae al correo cuando falta (spec 2026-09-11, D1 y D6).</param>
public sealed record QuotationAdvisor(string? Email, string? DisplayName);

/// <summary>
/// Puerto hacia Tenancy/Identity para poner nombre a la asesora de una cotización.
/// <c>Quotation.AdvisorId</c> es un <c>MemberId</c> —una membresía, no un usuario—: el nombre
/// vive en esa membresía y el correo en Identity, dos lugares que ningún módulo de negocio puede
/// leer por su cuenta. El adaptador vive en <c>Bootstrapper</c>, mismo criterio que
/// <see cref="IQuotationCustomerLookup"/>.
///
/// Batch por la misma razón que <see cref="IQuotationCustomerLookup.FindNamesAsync"/>: el
/// listado necesita todas las asesoras de la página de una vez. Una membresía que no es del
/// tenant no aparece en el diccionario.
/// </summary>
public interface IQuotationAdvisorLookup
{
    Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: El adaptador**

Reemplazar `src/Bootstrapper/QuotationAdvisorLookup.cs` por:

```csharp
using Modules.Identity.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Resuelve correo y nombre de cada asesora de una cotización: <c>MemberId</c> → membresía
/// (Tenancy, que trae el nombre) → usuario (Identity, que trae el correo).
///
/// Vive acá y no en ninguno de los tres módulos, mismo criterio que
/// <see cref="QuotationCustomerLookup"/>: el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
/// </summary>
internal sealed class QuotationAdvisorLookup(
    IMembershipRepository memberships,
    IUserDirectory users)
    : IQuotationAdvisorLookup
{
    public async Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        if (membershipIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationAdvisor>();
        }

        // Las membresías del tenant se traen de una: son pocas por tenant (mismo supuesto que
        // documenta ListMembershipsHandler) y así el filtro por id no cuesta una consulta por
        // asesora.
        var wanted = membershipIds.ToHashSet();
        var scoped = (await memberships.ListByTenantAsync(
                new TenantId(tenantId), cancellationToken))
            .Where(membership => wanted.Contains(membership.Id.Value))
            .ToList();

        // El correo sí es una búsqueda por usuario: IUserDirectory sólo resuelve por id único,
        // igual que en ListMembershipsHandler. Acá el conteo es la cantidad de asesoras
        // **distintas** de la página —una o dos en la práctica—, no una por fila. El nombre sale
        // de la membresía que ya se trajo, sin sumar consultas.
        var advisors = new Dictionary<Guid, QuotationAdvisor>(scoped.Count);
        foreach (var membership in scoped)
        {
            advisors[membership.Id.Value] = new QuotationAdvisor(
                await users.GetEmailAsync(membership.UserId, cancellationToken),
                membership.DisplayName);
        }

        return advisors;
    }
}
```

- [ ] **Step 5: Los cuatro consumidores**

`ListQuotations.cs:126-140`:

```csharp
        // Misma idea que los nombres de cliente: una ida por página, con los ids sin repetir.
        // Antes el frontend se traía el padrón de miembros entero para poner un correo en cada
        // fila. La fila muestra el correo aunque la asesora tenga nombre: el nombre sólo llega al
        // PDF (spec 2026-09-11, D1).
        var advisors = quotations.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                query.TenantId,
                quotations.Select(quotation => quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        var items = quotations
            .Select(quotation => quotation.ToListItemDto(
                clientNames.GetValueOrDefault(quotation.ClientId),
                advisors.GetValueOrDefault(quotation.AdvisorId.Value)?.Email))
            .ToArray();
```

`ListSales.cs:119-130`:

```csharp
        // El correo y no el nombre, mismo criterio que el listado de cotizaciones (D1).
        var advisors = rows.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                query.TenantId,
                rows.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        var items = rows
            .Select(row => row.ToListItemDto(
                clientNames.GetValueOrDefault(row.Quotation.ClientId),
                advisors.GetValueOrDefault(row.Quotation.AdvisorId.Value)?.Email))
            .ToArray();
```

`ListQuotationHistory.cs:49-69`:

```csharp
        // Una consulta para todos los correos y no uno por entrada: un historial largo repite las
        // mismas dos o tres personas. El historial muestra el correo, no el nombre (D1).
        var memberIds = entries
            .Where(entry => entry.MemberId.HasValue)
            .Select(entry => entry.MemberId!.Value.Value)
            .Distinct()
            .ToArray();
        var advisors = memberIds.Length == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(query.TenantId, memberIds, cancellationToken);

        return entries
            .Select(entry => new QuotationHistoryEntryDto(
                entry.Id.Value,
                entry.EventType.ToString(),
                entry.EventAt,
                entry.MemberId?.Value,
                entry.MemberId is { } memberId
                    ? advisors.GetValueOrDefault(memberId.Value)?.Email
                    : null,
                entry.Details))
            .ToArray();
```

`QuotationResponseComposer.cs:38-39`:

```csharp
        var advisors = await advisorLookup.FindAsync(
            tenantId, [quotation.AdvisorId], cancellationToken);
        var advisor = advisors.GetValueOrDefault(quotation.AdvisorId);
```

y `:58`:

```csharp
            advisor?.Email,
```

- [ ] **Step 6: Comentarios que pasan a ser falsos**

Sin cambio de código; afirmaban que el sistema no guarda nombre de persona.

`SalesDtos.cs:71-72`:

```csharp
    /// <summary>Correo de la asesora, no su nombre: el nombre de la membresía sólo llega al PDF
    /// (spec 2026-09-11, D1). Mismo criterio y misma nulabilidad que <c>QuotationListItemResponse</c>.</summary>
```

`ReportingLookups.cs:14-19`:

```csharp
/// **Los reportes muestran el email, no el nombre.** El nombre existe desde el 2026-09-11
/// —<c>Modules.Tenancy.Domain.Membership.DisplayName</c>—, pero por decisión de alcance sólo lo
/// imprime el PDF de cotización (spec 2026-09-11, D1); llevarlo a reportes es un trabajo aparte.
/// Así que lo que viaja en <c>advisorName</c> y <c>changedByName</c> sigue siendo el email — los
/// nombres de campo se mantienen porque son los que fija el contrato de API con el frontend.
```

`ReportingDtos.cs:7-12`:

```csharp
/// <c>AdvisorName</c> es el **email** del asesor, no su nombre. El nombre vive en
/// <c>Tenancy.Membership.DisplayName</c> desde el 2026-09-11, pero sólo lo usa el PDF de
/// cotización (spec 2026-09-11, D1). El nombre del campo se mantiene porque es el que el contrato
/// de API fija con el frontend; léase "la etiqueta con la que mostrar a esta persona". Nulo
/// cuando la fila de usuario no está.
```

`SaleListApiTests.cs:43-44`:

```csharp
        // La asesora se muestra por correo: el nombre de la membresia solo llega al PDF (spec
        // 2026-09-11, D1), mismo criterio que el listado de cotizaciones.
```

`SalesReportApiTests.cs:50-51`:

```csharp
        // advisorName es el email: el nombre de la membresia solo llega al PDF de cotizacion
        // (spec 2026-09-11, D1). Ver la seccion del contrato al respecto.
```

`ReportingApiHarness.cs:99-102`:

```csharp
    /// <summary>Registra un tenant nuevo para conseguir una Membership de dueño ya en Active, y
    /// devuelve un cliente autenticado como ese dueño. <c>OwnerEmail</c> vuelve porque es el
    /// valor que los reportes muestran en <c>advisorName</c>/<c>changedByName</c>: el nombre de la
    /// membresía sólo llega al PDF (spec 2026-09-11, D1).</summary>
```

- [ ] **Step 7: Correr y verificar que pasan**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
rg -n "FindEmailsAsync|FindEmailsCalls" src tests
```

Esperado: build sin errores; PASS el proyecto de unitarias entero, incluidas las dos guardas nuevas; `rg` sin resultados. La integración de Quotations se corre en Task 8, que ejerce este adaptador punta a punta. Pegar la salida.

- [ ] **Step 8: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations/Modules.Quotations.Application src/Bootstrapper/QuotationAdvisorLookup.cs src/Bootstrapper/ReportingLookups.cs src/Modules/Reporting/Modules.Reporting.Application/ReportingDtos.cs tests/Modules/Quotations tests/Modules/Reporting/Modules.Reporting.IntegrationTests/SalesReportApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs
git commit -m "refactor(quotations): el lookup de asesora devuelve correo y nombre"
```

---

### Task 8: PDF — el nombre gana, el correo es el respaldo

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:247-248` (`QuotationResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs:57-58`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:35`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:334-335` (`StubQuotationResponseComposer`)
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationAdvisorNameApiTests.cs`

**Interfaces:**
- Consumes: `QuotationAdvisor.DisplayName` y la variable `advisor` del composer (Task 7); `PATCH .../display-name` (Task 6); `RegisterTenantAsync`, `CreateActiveCustomerAsync`, `CreateQuotationAsync`, `QuotationsUrl`, `ManagerPermissions`, `StartDatabaseAsync`, `QepApiFactory` de `QuotationsApiHarness`.
- Produces:
  - `QuotationResponse(..., Guid AdvisorId, string? AdvisorEmail, string? AdvisorName, string Status, ...)`: el parámetro nuevo va **justo después de `AdvisorEmail`**. Es aditivo en el JSON; en C# obliga a actualizar las dos construcciones posicionales (`QuotationResponseComposer.cs:52` y `QuotationsTestDoubles.cs:329`).
  - `QuotationPdfDocument.AdvisorLabel = AdvisorName ?? AdvisorEmail ?? ""`.

- [ ] **Step 1: Pruebas unitarias que fallan**

En `QuotationPdfDocumentMapperTests.cs`, dentro de `Response()` (`:221-290`), agregar `null,` como nuevo argumento **después** de `"ana@qep.co",` (`:227`), de modo que queden:

```csharp
        Guid.CreateVersion7(),
        "ana@qep.co",
        null,
        "Draft",
```

Y las pruebas, después de `JoinsAddressAndCityInASingleLocationLine`:

```csharp
    // D6: la ficha "Asesor" presenta a la persona por su nombre, que es lo que el cliente espera
    // leer en un documento comercial.
    [Fact]
    public void TheAdvisorLabelPrefersTheName()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { AdvisorName = "Ana Pérez" });

        Assert.Equal("Ana Pérez", document.AdvisorLabel);
    }

    // Sin nombre —membresías anteriores al nombre y el owner hasta que lo cargue— el documento
    // sigue saliendo con el correo, que es lo que imprimía antes.
    [Fact]
    public void TheAdvisorLabelFallsBackToTheEmail()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { AdvisorName = null });

        Assert.Equal("ana@qep.co", document.AdvisorLabel);
    }

    // Sin ninguno de los dos queda vacío, y quotation.typ ya sabe resolver el vacío.
    [Fact]
    public void TheAdvisorLabelIsEmptyWithoutNameOrEmail()
    {
        var document = QuotationPdfDocumentMapper.From(
            Response() with { AdvisorName = null, AdvisorEmail = null });

        Assert.Equal(string.Empty, document.AdvisorLabel);
    }
```

En `QuotationsTestDoubles.cs`, `StubQuotationResponseComposer`: después del `null,` que corresponde a `AdvisorEmail` (`:335`), agregar otro `null,` para `AdvisorName`:

```csharp
            quotation.AdvisorId,
            null,
            null,
            quotation.Status,
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPdfDocumentMapperTests"
```

Esperado: no compila — `error CS0117: 'QuotationResponse' does not contain a definition for 'AdvisorName'` y `error CS1729: 'QuotationResponse' does not contain a constructor that takes 35 arguments`. Pegar la salida.

- [ ] **Step 3: El campo en la respuesta**

En `QuotationsDtos.cs`, dentro de `QuotationResponse`, después de `string? AdvisorEmail,` (`:248`):

```csharp
    /// <summary>El nombre que el tenant cargó en la membresía de la asesora. Null en membresías
    /// anteriores al nombre y en el owner hasta que lo cargue desde el roster. Hoy sólo lo usa el
    /// PDF, que cae a <c>AdvisorEmail</c> cuando falta (spec 2026-09-11, D6); la pantalla sigue
    /// mostrando el correo (D1). Aditivo: un front que no lo lee no se entera.</summary>
    string? AdvisorName,
```

En `QuotationResponseComposer.cs`, después de `advisor?.Email,` (`:58`):

```csharp
            advisor?.DisplayName,
```

- [ ] **Step 4: La regla del PDF**

En `QuotationPdfDocumentMapper.From`, reemplazar `quotation.AdvisorEmail ?? string.Empty,` (`:35`) por:

```csharp
            AdvisorLabelFor(quotation),
```

y agregar, junto a `BillingAccountFor`:

```csharp
    /// <summary>La ficha "Asesor" presenta a la persona por su nombre, y cae al correo mientras la
    /// membresía no tenga uno (filas anteriores al nombre y el owner). Sin ninguno de los dos,
    /// vacío: <c>quotation.typ</c> ya lo resuelve. Un PDF ya generado conserva lo que imprimió:
    /// el caché sólo se invalida por <c>Quotation.Version</c> (spec 2026-09-11, D7).</summary>
    private static string AdvisorLabelFor(QuotationResponse quotation) =>
        quotation.AdvisorName ?? quotation.AdvisorEmail ?? string.Empty;
```

`QuotationPdfProvider` y `quotation.typ` **no se tocan**.

- [ ] **Step 5: Correr las unitarias y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS el proyecto entero, incluidas `QuotationTemplateTests` y `QCodePdfRendererTests` sin tocarlas. Pegar la salida.

- [ ] **Step 6: La prueba de punta a punta**

Ninguna prueba unitaria ve el adaptador de `Bootstrapper`. Ésta lo recorre entero: nombrar la membresía por la API de Tenancy llega al detalle de la cotización.

Crear `QuotationAdvisorNameApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El nombre de la asesora cruza tres módulos —membresía en Tenancy, correo en Identity,
/// cotización en Quotations— por el adaptador de <c>Bootstrapper</c>, que ninguna prueba
/// unitaria ve. Ésta lo recorre punta a punta.
/// </summary>
public sealed class QuotationAdvisorNameApiTests
{
    // X-Permissions reemplaza el set por defecto del stub, así que los permisos de Tenancy para
    // leer el roster y renombrar se piden explícitos.
    private static readonly string[] Permissions =
        [.. ManagerPermissions, "advisorship.read", "advisorship.manage"];

    [Fact]
    public async Task TheQuotationDetailCarriesTheAdvisorNameOnceTheMemberIsNamed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // La asesora es el owner, que nace sin nombre porque lo crea register-tenant y no una
        // invitación: el detalle trae sólo el correo.
        Assert.Null(quotation.AdvisorName);
        Assert.NotNull(quotation.AdvisorEmail);

        var version = await MembershipVersionAsync(client, tenantId, quotation.AdvisorId);
        using var rename = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{quotation.AdvisorId}/display-name")
        {
            Content = JsonContent.Create(new { displayName = "Laura Gómez" })
        };
        rename.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        var renamed = await client.SendAsync(rename, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(fetched);
        Assert.Equal("Laura Gómez", fetched.AdvisorName);
        // D1: el correo sigue viajando igual; la pantalla lo sigue usando.
        Assert.Equal(quotation.AdvisorEmail, fetched.AdvisorEmail);
    }

    // La versión se lee del roster en vez de suponerla: If-Match tiene que llevar la vigente.
    private static async Task<long> MembershipVersionAsync(
        HttpClient client, Guid tenantId, Guid membershipId)
    {
        var roster = await client.GetFromJsonAsync<MembershipListPayload>(
            $"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        Assert.NotNull(roster);
        return Assert.Single(roster.Items, item => item.Id == membershipId).Version;
    }

    private sealed record MembershipListPayload(IReadOnlyList<MembershipRowPayload> Items);

    private sealed record MembershipRowPayload(Guid Id, long Version);
}
```

- [ ] **Step 7: Correr la integración de Quotations**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests
```

Esperado: PASS el proyecto entero, la nueva y todas las que leen detalle, listados, ventas e historial (cubre también Task 7). Si la nueva falla con `Assert.Null() Failure` sobre `AdvisorName`, el composer no está pasando `advisor?.DisplayName` (Step 3). Pegar la salida.

- [ ] **Step 8: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationAdvisorNameApiTests.cs
git commit -m "feat(quotations): imprimir el nombre del asesor en el pdf"
```

---

### Task 9: Verificación final contra el baseline

**Files:** ninguno, salvo que `dotnet format` pida cambios.

**Interfaces:**
- Consumes: `$env:TEMP\qep-nombre-asesor-baseline-failed.txt` (Task 0).
- Produces: evidencia para el handoff.

- [ ] **Step 1: La secuencia de README § Verificación**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore
```

Esperado: `restore` sin `NU1004` (este plan no toca `Directory.Packages.props` ni los lock files); `format` sin cambios; `build` sin errores ni warnings nuevos. Si `format` reporta algo, correr `dotnet format`, revisar que el diff sea sólo de formato en archivos de este plan y commitearlo como `style: formato`. Pegar la salida.

- [ ] **Step 2: Suite completa y comparación por nombre**

```powershell
$final = Join-Path $env:TEMP "qep-nombre-asesor-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $final
Get-ChildItem $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-nombre-asesor-final-failed.txt")
Compare-Object `
    (Get-Content (Join-Path $env:TEMP "qep-nombre-asesor-baseline-failed.txt")) `
    (Get-Content (Join-Path $env:TEMP "qep-nombre-asesor-final-failed.txt"))
```

Esperado: ninguna línea con `=>` (una prueba que falla ahora y no fallaba antes). Una línea con `<=` es una prueba que estaba rota en el baseline y ahora pasa: anotarla, no es regresión. Si `Get-Content` del baseline vino vacío, `Compare-Object` falla con `ReferenceObject is null`. En ese caso, esperado: `qep-nombre-asesor-final-failed.txt` también vacío. Pegar la salida.

- [ ] **Step 3: Barridos de consistencia**

```powershell
rg -n "FindEmailsAsync" src tests
rg -n "display_name_invalid|tenancy.membership.renamed" src
git log --format="%h %s%n%b" main..HEAD | Select-String -Pattern "Co-Authored-By|Claude|Generated with"
git branch --show-current
```

Esperado: el primero sin resultados. El segundo con `Membership.cs` y `UpdateMemberDisplayName.cs`. El tercero **sin** resultados. La rama, `feature/nombre-del-asesor`.

- [ ] **Step 4: Handoff**

Anotar para el developer, con la salida literal de los pasos anteriores:

- La lista de commits (`git log --oneline main..HEAD`).
- Que invitar sin `displayName` responde 422: **el deploy de backend sale junto con el del front**, o después de que el front ya mande el campo.
- Que los PDFs ya generados siguen con el correo hasta que la cotización cambie de versión (D7).
- Que Reporting sigue llamando `advisorName` al correo, mientras que `QuotationResponse.AdvisorName` es el nombre (hallazgo 6).

Después seguir con `superpowers:finishing-a-development-branch`. Push y PR sólo si el developer lo pide.
