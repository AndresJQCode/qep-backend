# Código de asesor — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que cada membresía pueda llevar un código de asesor opcional —entero positivo, único por tenant cuando existe— que se carga al invitar o desde el roster con un `PUT .../profile` nuevo (nombre y código juntos), se busca exacto en el roster y viaja en la columna `Cod. Asesor` del Excel de pedidos, sin retirar todavía `PUT .../display-name`.

**Architecture:** `Membership` (Tenancy) gana `int? AdvisorCode`, normalizado en el agregado (`null` pasa, `< 1` es `tenancy.membership.advisor_code_invalid`); `Rename` se reemplaza por `UpdateProfile(displayName, advisorCode, now) : bool`, y el handler de `display-name` lo llama conservando el código. La unicidad (D3, D4) la sostiene un índice único parcial `IX_memberships_tenant_id_advisor_code` que `TenancyUnitOfWork` traduce por nombre a `tenancy.membership.advisor_code_taken`, con un chequeo previo en los handlers (`IMembershipRepository.IsAdvisorCodeTakenAsync`). Invitar y el `PUT .../profile` nuevo llevan el código; el roster lo muestra y lo busca exacto. En Quotations, `QuotationAdvisor` suma `AdvisorCode` desde el adaptador de `Bootstrapper` y `OrdersExportProcessor` lo resuelve por lote y lo escribe al final de cada fila.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`; las pruebas de integración necesitan Docker corriendo).

**Spec:** `docs/superpowers/specs/2026-09-24-codigo-de-asesor-design.md` — secciones D1–D9, "Backend" y "Quotations — Excel de pedidos". Este plan cubre sólo el slice 1 de backend. **Fuera:** el frontend, el slice 3 (borrar `PUT .../display-name`, que acá sigue funcionando) y D10 (homologación de columnas).

## Global Constraints

- **TDD estricto: RED antes que GREEN, con la salida literal de ambos pegada en el handoff.** Donde el RED es "no compila" (firma nueva), se pega el error del compilador.
- **Commits: Conventional Commits en español, sin atribución de IA y sin trailer `Co-Authored-By`.** Después de cada commit, `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, `git commit --amend` para quitarlo antes de seguir.
- **Guard de rama antes de cada commit** —la rama es un estado y cambia entre comandos, así que ni el snapshot de arranque ni un `git status` anterior son autoridad—:

  ```powershell
  $branch = git branch --show-current
  if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
  ```

  Si lanza, **para y pregunta**; no crees ni cambies de rama a mitad del plan.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`.
- **`Api.exe` o `dotnet Api.dll` corriendo bloquean `dotnet build`, `dotnet test` y `dotnet ef`** (MSB3021, archivo bloqueado). Antes de cada corrida:

  ```powershell
  Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
  Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Migración: factory de diseño, nunca `--startup-project`** (`Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`). La única de este plan:
  `dotnet ef migrations add AddMembershipAdvisorCode --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`.
- **Contrato exacto, tal como lo fija el spec:**
  - columna `tenancy.memberships.advisor_code integer NULL`;
  - índice `IX_memberships_tenant_id_advisor_code`, único, sobre `(tenant_id, advisor_code)`, `WHERE advisor_code IS NOT NULL`, **sin** filtro por estado (D4);
  - códigos de error `tenancy.membership.advisor_code_invalid` (dominio, `< 1`) y `tenancy.membership.advisor_code_taken` (repetido en el tenant); el validador responde `422 validation.failed` con `errors.AdvisorCode`;
  - acción de auditoría `tenancy.membership.profile_updated`, recurso `membership`, resultado `success`, sólo si algo cambió;
  - ruta `PUT /api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}/profile` con `{ displayName, advisorCode }`, `If-Match` obligatorio (428 sin él, 412 con versión vieja) y `ETag` en la respuesta, permiso `advisorship.manage`;
  - encabezado del Excel `Cod. Asesor`, **última** columna, celda numérica o vacía.
- `PUT .../display-name` **sigue funcionando** (D7): mismo contrato, misma auditoría `tenancy.membership.renamed`, y no toca el código.
- **Comentarios en español tuteando, nunca voseo** (regla del `CLAUDE.md` de `qep-backend`, aunque el estilo de salida del asistente vosee); identificadores, códigos de error y nombres de prueba en inglés.
- Comandos en **Windows PowerShell 5.1**: sin `&&` (`A; if ($?) { B }`), `$env:VAR = "…"` en línea aparte. Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: un `using` sin uso se borra en la misma tarea. Archivos nuevos en UTF-8 sin BOM.
- **Durante el ciclo se corren sólo las clases tocadas** (`--filter "FullyQualifiedName~<Clase>"`), siempre en primer plano; la suite completa corre una vez, en Task 7, y la regresión se mide **por nombre de prueba** contra el baseline de Task 0, nunca por conteo.
- **Nunca imprimir el valor de un secreto.** Ningún paso de este plan necesita uno.
- Tasks 1 y 2 se commitean por separado pero **no se pushea entre las dos**: entre ellas EF mapea `AdvisorCode` a una columna que todavía no existe y la integración de Tenancy falla.

## Review Focus

Cinco modos de falla que el spec no prueba explícitamente y que es probable que aparezcan. Cada uno tiene su prueba en la tarea dueña:

1. **Carrera por el mismo código.** Dos requests pasan el chequeo previo a la vez y el segundo choca contra el índice. Sin la traducción por nombre en `TenancyUnitOfWork`, el `23505` sale como `500 server.unexpected`. → Task 2, `ADuplicateCodeThatReachesTheDatabaseIsTheDomainCodeAndNotAServerError` (ejerce el índice saltándose el chequeo previo, que es exactamente lo que pasa en la carrera).
2. **Códigos retenidos: membresía quitada y re-invitación.** Una `Removed` sigue bloqueando su código (D4), así que (a) invitar a otra persona con ese código es 422; (b) re-invitar a la propia quitada con su código **no** debe chocar consigo misma (el chequeo excluye su id); (c) re-invitar una vencida con un código de otra persona es 422 y deja la fila intacta; (d) re-invitar una invitación viva con un código tomado es la no-op de siempre, no un 422; (e) re-invitar **sin** código conserva el que la membresía ya tenía —no lo borra ni lo libera; borrar es sólo por `PUT .../profile`— y no dispara el chequeo de unicidad. → Task 1 (`ReinviteWithoutACodeKeepsThePreviousOne`), Task 2 (`IsAdvisorCodeTaken…`) y Task 3 (`ARemovedMembershipKeepsBlockingItsCode`, `ReinvitingARemovedMemberWithItsOwnCodeIsAccepted`, `ReinvitingARemovedMemberWithoutACodeKeepsIt`, `ReinvitingALapsedInvitationWithACodeTakenByAnotherIsRejected`, `InvitingAgainWhileTheInvitationIsLiveIgnoresEvenATakenCode`).
3. **`advisorCode` que no es un entero.** Con el request tipado `int?`, un `12.5` o un `99999999999` hacen fallar el binding con `BadHttpRequestException`, y `ApiExceptionHandler` convierte toda excepción no reconocida en **500** (precedente documentado en `GeographyEndpoints.cs:48-50`). Por eso el request lo recibe como `decimal?` y la API lo traduce (`AdvisorCodeInput`): entero representable pasa, cualquier otra cosa llega al validador como inválida → `422 errors.AdvisorCode`. `"12"` como string se acepta como `12` (las opciones web de System.Text.Json leen números desde string). → Task 3 (`InviteWithAnAdvisorCodeThatIsNotAPositiveIntegerMarksTheField`, `InviteWithTheAdvisorCodeAsANumericStringIsAccepted`) y Task 4 (`ProfileUpdateWithADecimalCodeMarksTheField`).
4. **Búsqueda por contenido en vez de exacta.** `?search=1` no puede traer a `12`, `21` ni `100` (D8). La comparación es numérica sobre el término parseado, así que además `0012` encuentra a `12` (D2: son el mismo código). → Task 5, `ListMembershipsHandlerTests`.
5. **Excel con una asesora que no resuelve.** Si `Quotation.AdvisorId` apunta a una membresía que el lookup no devuelve (otro tenant, fila borrada) o que no tiene código, la celda tiene que salir vacía y no romper el lote ni correr las columnas. → Task 6, `CodAsesorIsEmptyWhenTheAdvisorDoesNotResolve` y `CodAsesorIsEmptyWhenTheAdvisorHasNoCode`.

---

## Hallazgos contra el código (2026-09-24)

Verificados leyendo el código en `feature/codigo-de-asesor` (`755f608`), no supuestos. **Ante discrepancia gana el código.**

1. **El README dice `PATCH .../display-name`; el código mapea `PUT`** (`MembershipEndpoints.cs:74`, y así lo usan `MembershipLifecycleApiTests` y `QuotationAdvisorNameApiTests`). El README se corrige en Task 4, junto con la sección nueva de `profile`.
2. **El request de invitar y el de `profile` reciben `advisorCode` como `decimal?`, no `int?` como dice el spec.** Motivo en el Review Focus, punto 3: un valor no entero en un `int?` se convierte en 500. El comando y los DTO de respuesta sí son `int?`, como pide el spec. La traducción vive en `src/Modules/Tenancy/Modules.Tenancy.Api/AdvisorCodeInput.cs`. Un string no numérico (`"abc"`) sigue siendo 500 —falla el binding antes de llegar a la API, igual que cualquier otro campo tipado hoy—; el frontend lo corta con zod y no se amplía el alcance a `ApiExceptionHandler`.
3. **`Invite` y `Reinvite` reciben `int? advisorCode = null` como último parámetro, opcional.** El spec pide que lo reciban; el orden no lo fija. Opcional al final deja sin tocar los once llamados de las pruebas y a `InvitationServiceTests`, y el único llamado de producción (`InviteMemberHandler`) lo pasa explícito, cubierto por las pruebas de integración de Task 3.
4. **`Rename` desaparece y el handler de `display-name` pasa a llamar `UpdateProfile(command.DisplayName, membership.AdvisorCode, now)`**, así que renombrar no borra el código. Las pruebas de dominio de `Rename` se reescriben sobre `UpdateProfile` (Task 1); las de integración de `display-name` no cambian y siguen verdes.
5. **Re-invitar sin código conserva el que había (decisión del developer, 2026-09-24; el spec ya lo dice en "Application y API").** Un cuerpo **con** código lo reemplaza, con el chequeo de unicidad de siempre; un cuerpo **sin** código deja el de la membresía intacto, así que una quitada que vuelve no pierde ni libera su código (coherente con D4). Borrar el código es sólo por `PUT .../profile`. En `Reinvite`, `advisorCode` nulo significa "sin cambios", a diferencia de `UpdateProfile`, donde nulo borra. Lo fijan `ReinviteWithoutACodeKeepsThePreviousOne` y `ReinvitingARemovedMembershipWithoutACodeKeepsIt` (Task 1) y `ReinvitingARemovedMemberWithoutACodeKeepsIt` (Task 3).
6. **El chequeo previo de invitar corre después de aprovisionar el usuario en Identity** (necesita el id de la membresía existente para excluirse a sí misma). Un 422 por código tomado deja un usuario de Identity sin membresía, que `OrphanUserCleanupWorker` recoge igual que hoy recoge el de un invite que falla en la base. No se agrega compensación.
7. **No existen pruebas unitarias de `ListMembershipsHandler`.** El spec las pide; se crea `ListMembershipsHandlerTests.cs` con dobles propios en `MembershipHandlerTestDoubles.cs`, reusando `InMemoryTenantRepository` y `FixedClock` de `TenantLogoHandlerTestDoubles.cs`. La búsqueda no se prueba por integración porque los correos de las pruebas (`invitee-{guid}@example.com`) contienen dígitos y `?search=1` los encontraría por correo.
8. **Dos pruebas existentes fijan el encabezado exacto del Excel de pedidos** y se rompen con la columna nueva: `OrdersExportProcessorTests.WritesTheErpColumnsInOrder` y `OrderExportApiTests.TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail`. Las dos se actualizan en Task 6.
9. **El README describe columnas «Comprobante 1» a «Comprobante 5» en el Excel de pedidos** (sección "Comprobantes de pago públicos"), que ya no existen desde el ajuste 2026-09-20 (hoy son «Fecha Pago 1» a «Fecha Pago 5»). No es de este slice: se deja anotado para quien toque esa sección.
10. **La búsqueda por código acepta ceros a la izquierda** (`0012` encuentra a `12`): el spec dice "coincidencia exacta sobre el texto del código", y el texto de un `integer` no tiene ceros a la izquierda. Se compara numéricamente contra el término parseado con `NumberStyles.None`, que es lo coherente con D2.

## Entrega

| Commit (mensaje) | Tarea |
| --- | --- |
| `docs(tenancy): plan del código de asesor` | 0 |
| `feat(tenancy): código de asesor en la membresía` | 1 |
| `feat(tenancy): columna e índice único parcial del código de asesor` | 2 |
| `feat(tenancy): invitar con código de asesor` | 3 |
| `feat(tenancy): PUT profile edita nombre y código de asesor` | 4 |
| `feat(tenancy): el roster busca por código de asesor exacto` | 5 |
| `feat(quotations): Cod. Asesor en el Excel de pedidos` | 6 |
| `fix(...): …`, sólo si la verificación final lo pide | 7 |

La rama no se publica ni se mergea desde este plan.

## File Structure

**Crear**

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Application/AdvisorCodeAvailability.cs` | Chequeo previo de unicidad, compartido por invitar y `profile` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberProfile.cs` | Comando, validador y handler de `PUT .../profile` |
| `src/Modules/Tenancy/Modules.Tenancy.Api/AdvisorCodeInput.cs` | Traduce el `decimal?` del JSON al `int?` del comando |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddMembershipAdvisorCode.cs` (+ `.Designer.cs`) | Migración generada |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs` | Repositorio en memoria, directorio de usuarios y contexto de lectura |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListMembershipsHandlerTests.cs` | Búsqueda exacta por código |

**Modificar — producción**

| Archivo | Cambio |
| --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs` | `AdvisorCode`, `NormalizeAdvisorCode`, parámetro en `Invite`/`Reinvite`, `UpdateProfile` en lugar de `Rename` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs` | Llama `UpdateProfile` conservando el código |
| `src/Modules/Tenancy/Modules.Tenancy.Application/IMembershipRepository.cs` | `IsAdvisorCodeTakenAsync` |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs` | Implementación |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` | Columna e índice parcial |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs` | Traduce el `23505` del índice nuevo |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs` | Regenerado por `dotnet ef` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs` | Código en comando, validador y handler |
| `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs` | `AdvisorCode` |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs` | `AdvisorCode` en el item; búsqueda exacta |
| `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` | Request/response con código; endpoint `profile` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs:95-97` | Registro del handler nuevo |
| `src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs` | `QuotationAdvisor.AdvisorCode` |
| `src/Bootstrapper/QuotationAdvisorLookup.cs` | Llena el código desde la membresía |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` | Inyecta el lookup; columna `Cod. Asesor` |
| `README.md` | Invitar con código, sección `profile`, `PUT` en `display-name`, columna del Excel |

**Modificar — pruebas**

| Archivo | Cambio |
| --- | --- |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs` | Pruebas del código; `Rename` → `UpdateProfile` |
| `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs` | `IsAdvisorCodeTakenAsync` en su doble |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs` | Índice, carrera, repositorio, `PUT .../profile` |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs` | Invitar con código |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` | Stub del lookup con código y registro de pedidos |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs` | Columna `Cod. Asesor` |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` | Encabezado nuevo y punta a punta del código |

**No se tocan, a propósito:** `TenantRegistrationService` y `TenancySeeder` (usan `CreateActive`, que nace sin código), `SuspendMember.cs`, `RemoveMember.cs`, `ReactivateMember.cs`, `UpdateMemberRoles.cs` (pasan por `ToListItemDto`, que ya lleva el código), `QuotationsExportProcessor` y todo listado, PDF o reporte (fuera de alcance del spec), `ApiExceptionHandler` (hallazgo 2).

---

### Task 0: Rama y baseline

**Files:**
- Ninguno de código. Commitea este plan.

**Interfaces:**
- Consumes: rama `feature/codigo-de-asesor`, ya creada con el spec commiteado (`755f608 docs(tenancy): spec del código de asesor`).
- Produces: `$env:TEMP\qep-codigo-asesor-baseline-failed.txt` con los nombres de las pruebas que **ya** fallan antes de empezar.

- [ ] **Step 1: Comprobar la rama y el árbol**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git status --short
git log -1 --oneline
```

Esperado: `feature/codigo-de-asesor`; como único cambio `?? docs/superpowers/plans/2026-09-24-codigo-de-asesor.md`; último commit `755f608` o posterior. Si la rama es otra, **para y pregunta**.

- [ ] **Step 2: Herramientas**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: `Get-Process` sin salida; una versión de Docker; `dotnet ef` 10.x (si falta: `dotnet tool install --global dotnet-ef`).

- [ ] **Step 3: Commitear el plan**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add docs/superpowers/plans/2026-09-24-codigo-de-asesor.md
git commit -m "docs(tenancy): plan del código de asesor"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

- [ ] **Step 4: Baseline de la suite completa, por nombre**

Con Docker corriendo y la API detenida:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore --locked-mode
dotnet build --no-restore
$baseline = Join-Path $env:TEMP "qep-codigo-asesor-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $baseline
Get-ChildItem $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-codigo-asesor-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-codigo-asesor-baseline-failed.txt")
```

Esperado: build con `0 Errores`; la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff: es contra lo que se compara Task 7.

---

### Task 1: Dominio — `AdvisorCode` y `UpdateProfile`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs` (propiedad después de `DisplayName` `:75-83`; `Invite` `:114-164`; `Reinvite` `:290-342`; `Rename` `:487-508`; helpers `:554-565`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs:71-87`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs`

**Interfaces:**
- Consumes: `TenantDomainException(string code, string message)`; `Membership.NormalizeDisplayName(string?)` (privado, existente).
- Produces:
  - `public int? AdvisorCode { get; private set; }`
  - `public static Membership Invite(MembershipId id, Guid userId, TenantId tenantId, string displayName, IEnumerable<string> roles, string origin, string invitationToken, string invitationTokenHash, DateTimeOffset invitedAt, TimeSpan timeToLive, int? advisorCode = null)`
  - `public void Reinvite(string displayName, IEnumerable<string> roles, string invitationToken, string invitationTokenHash, DateTimeOffset occurredAt, TimeSpan timeToLive, int? advisorCode = null)`
  - `public bool UpdateProfile(string displayName, int? advisorCode, DateTimeOffset occurredAt)` — reemplaza a `Rename(string, DateTimeOffset)`, que deja de existir.
  - `private static int? NormalizeAdvisorCode(int? value)` → lanza `tenancy.membership.advisor_code_invalid` si `< 1`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs`:

**(a)** Reemplaza el helper `Invite(Guid userId)` (al final de la clase) por:

```csharp
    private static Membership Invite(Guid userId, int? advisorCode = null) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            TenantId.New(),
            InvitedName,
            ["advisor"],
            "invitation",
            Token,
            TokenHash,
            InvitedAt,
            Ttl,
            advisorCode);
```

**(b)** Reemplaza el bloque que va desde el `/// <summary>` que abre con `/// El nombre es presentación del tenant: vive en la membresía, se normaliza en el agregado y` hasta la llave que cierra `RenameRaisesNoDomainEvent()` (inclusive) por:

```csharp
    /// <summary>
    /// El perfil —nombre y código de asesor— es presentación del tenant: vive en la membresía, se
    /// normaliza en el agregado y cambiarlo cuenta como cambio —sube la versión— porque el roster
    /// lo edita con If-Match.
    /// </summary>
    [Fact]
    public void UpdateProfileTrimsTheNameAndBumpsTheVersion()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;
        var updatedAt = InvitedAt.AddHours(1);

        var changed = membership.UpdateProfile("  Ana María Pérez  ", null, updatedAt);

        Assert.True(changed);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(version + 1, membership.Version);
        Assert.Equal(updatedAt, membership.UpdatedAt);
    }

    // Guardar dos veces lo mismo no es un cambio: subir la versión invalidaría el If-Match de
    // otra pestaña por nada.
    [Fact]
    public void UpdateProfileWithTheSameNormalizedValuesIsANoOp()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(1));
        var version = membership.Version;
        var updatedAt = membership.UpdatedAt;

        var changed = membership.UpdateProfile("  Ana María Pérez ", 12, InvitedAt.AddHours(2));

        Assert.False(changed);
        Assert.Equal(version, membership.Version);
        Assert.Equal(updatedAt, membership.UpdatedAt);
    }

    // Spec 2026-09-24: cambiar sólo el código es un cambio de perfil como cualquier otro.
    [Fact]
    public void ChangingOnlyTheAdvisorCodeBumpsTheVersion()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;

        var changed = membership.UpdateProfile(InvitedName, 12, InvitedAt.AddHours(1));

        Assert.True(changed);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(version + 1, membership.Version);
    }

    // D1: el código es opcional también al editar; mandarlo nulo lo borra.
    [Fact]
    public void UpdateProfileWithANullCodeClearsIt()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;

        var changed = membership.UpdateProfile(InvitedName, null, InvitedAt.AddHours(1));

        Assert.True(changed);
        Assert.Null(membership.AdvisorCode);
        Assert.Equal(version + 1, membership.Version);
    }

    // D2: entero >= 1. Un rechazo no deja nada a medias: ni el nombre nuevo ni la versión.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void UpdateProfileRejectsANonPositiveCodeAndLeavesTheMembershipUntouched(int advisorCode)
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;

        var error = Assert.Throws<TenantDomainException>(
            () => membership.UpdateProfile("Ana María Pérez", advisorCode, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(version, membership.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateProfileRejectsABlankNameAndLeavesTheMembershipUntouched(string displayName)
    {
        var membership = Invite(Guid.CreateVersion7());
        var before = membership.DisplayName;
        var version = membership.Version;

        var error = Assert.Throws<TenantDomainException>(
            () => membership.UpdateProfile(displayName, 12, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
        Assert.Equal(before, membership.DisplayName);
        Assert.Null(membership.AdvisorCode);
        Assert.Equal(version, membership.Version);
    }

    [Fact]
    public void UpdateProfileRejectsANameLongerThanTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());

        var error = Assert.Throws<TenantDomainException>(() => membership.UpdateProfile(
            new string('a', Membership.DisplayNameMaxLength + 1), null, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    [Fact]
    public void UpdateProfileAcceptsANameExactlyAsLongAsTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());
        var name = new string('a', Membership.DisplayNameMaxLength);

        membership.UpdateProfile(name, null, InvitedAt.AddHours(1));

        Assert.Equal(name, membership.DisplayName);
    }

    // El perfil no cambia el acceso, así que se puede cargar en cualquier estado.
    [Fact]
    public void UpdateProfileWorksOnASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(2));

        Assert.True(membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(3)));
        Assert.Equal(MembershipState.Suspended, membership.State);
    }

    // El owner entra por register-tenant, no por invitación: nace sin nombre ni código y los
    // carga desde el roster. La protección de owner no alcanza al perfil.
    [Fact]
    public void TheOwnerStartsWithoutANameOrCodeAndCanGetThemLater()
    {
        var owner = CreateOwner();

        Assert.Null(owner.DisplayName);
        Assert.Null(owner.AdvisorCode);
        Assert.True(owner.UpdateProfile("Laura Gómez", 7, InvitedAt.AddHours(1)));
        Assert.Equal("Laura Gómez", owner.DisplayName);
        Assert.Equal(7, owner.AdvisorCode);
    }

    // Nadie consume un cambio de perfil fuera de Tenancy: el PDF y el Excel lo leen al generarse.
    [Fact]
    public void UpdateProfileRaisesNoDomainEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(1));

        Assert.Empty(membership.DomainEvents);
    }
```

**(c)** Agrega, justo antes del helper `Invite(Guid userId, …)`:

```csharp
    [Fact]
    public void InviteStoresTheAdvisorCode()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);

        Assert.Equal(12, membership.AdvisorCode);
    }

    // D1: opcional al invitar.
    [Fact]
    public void InviteWithoutAnAdvisorCodeLeavesItNull()
    {
        var membership = Invite(Guid.CreateVersion7());

        Assert.Null(membership.AdvisorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InviteRejectsANonPositiveAdvisorCode(int advisorCode)
    {
        var error = Assert.Throws<TenantDomainException>(
            () => Invite(Guid.CreateVersion7(), advisorCode));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
    }

    // Spec 2026-09-24, Application y API: en la re-invitación un código en el cuerpo reemplaza al
    // que había.
    [Fact]
    public void ReinviteAppliesTheAdvisorCodeFromTheBody()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(
            InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl, advisorCode: 34);

        Assert.Equal(34, membership.AdvisorCode);
    }

    // A diferencia del nombre, un cuerpo sin código no borra el que había: la re-invitación lo
    // conserva (decisión del developer, 2026-09-24). Borrar el código es sólo por PUT .../profile.
    [Fact]
    public void ReinviteWithoutACodeKeepsThePreviousOne()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

        Assert.Equal(12, membership.AdvisorCode);
    }

    // D4: la quitada que vuelve sin código en el cuerpo sigue con el suyo.
    [Fact]
    public void ReinvitingARemovedMembershipWithoutACodeKeepsIt()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        membership.Reinvite(
            InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, InvitedAt.AddHours(2), Ttl);

        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(12, membership.AdvisorCode);
    }

    // Un código inválido corta antes de tocar nada: ni roles, ni token, ni versión.
    [Fact]
    public void AReinviteWithAnInvalidCodeLeavesTheMembershipUntouched()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                InvitedName, ["tenancy.admin"], RenewedToken, RenewedTokenHash, lapsed, Ttl,
                advisorCode: 0));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(["advisor"], membership.Roles);
        Assert.Equal(TokenHash, membership.InvitationTokenHash);
        Assert.Equal(version, membership.Version);
    }

    // D4: la membresía quitada conserva su código.
    [Fact]
    public void RemovingAMembershipKeepsItsAdvisorCode()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);

        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        Assert.Equal(MembershipState.Removed, membership.State);
        Assert.Equal(12, membership.AdvisorCode);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.MembershipTests"
```

Esperado: **no compila**. `CS1739` (`'Invite' no tiene un parámetro denominado 'advisorCode'` o el equivalente posicional `CS1501`), `CS1061` (`'Membership' no contiene una definición para 'UpdateProfile'` / `'AdvisorCode'`). Pega la salida.

- [ ] **Step 3: Implementar en el agregado**

En `src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs`:

**(a)** Reemplaza el comentario y la propiedad `DisplayName` (`:75-83`) por:

```csharp
    /// <summary>
    /// El nombre con el que el tenant presenta a esta persona; hoy lo imprime el PDF de
    /// cotización en la ficha "Asesor". Vive en la membresía y no en el usuario de Identity
    /// porque el usuario es global: con el nombre ahí, el admin de otro tenant cambiaría lo que
    /// imprimen los PDFs de este. Nulo en las filas anteriores a este cambio y en el owner de
    /// registro, que nunca pasó por una invitación; se carga desde el roster con
    /// <see cref="UpdateProfile"/>.
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// El código con el que un sistema externo del tenant (ERP, contabilidad) identifica a esta
    /// persona, para que sus registros y los de QEP casen (spec 2026-09-24). Opcional (D1): nulo
    /// significa "no tiene código allá". Entero positivo (D2), así que <c>0012</c> y <c>12</c> son
    /// el mismo. La unicidad por tenant (D3) no vive acá —una membresía no ve a las demás—: la
    /// sostiene el índice único parcial de la base, con un chequeo previo en los handlers para
    /// responder claro. Una membresía quitada lo conserva y lo sigue bloqueando (D4).
    /// </summary>
    public int? AdvisorCode { get; private set; }
```

**(b)** En `Invite`, agrega el parámetro al final de la firma y el código al inicializador. La firma queda:

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
        TimeSpan timeToLive,
        int? advisorCode = null)
```

y las líneas `var normalizedName = NormalizeDisplayName(displayName);` … `DisplayName = normalizedName, };` pasan a ser:

```csharp
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedCode = NormalizeAdvisorCode(advisorCode);

        var membership = new Membership(
            id,
            userId,
            tenantId,
            roles,
            origin,
            invitedAt,
            invitedAt + timeToLive)
        {
            InvitationTokenHash = invitationTokenHash,
            DisplayName = normalizedName,
            AdvisorCode = normalizedCode,
        };
```

**(c)** En `Reinvite`: agrega al final del `<remarks>`, antes de `/// </remarks>`:

```csharp
    ///
    /// El código de asesor no viaja igual que el nombre: uno en el cuerpo reemplaza al que había,
    /// pero un cuerpo sin código lo conserva (spec 2026-09-24, Application y API). Así una quitada
    /// que vuelve no pierde ni libera su código (D4); borrarlo es sólo por
    /// <see cref="UpdateProfile"/>. La unicidad la revisa el handler antes de llamar acá.
```

La firma queda:

```csharp
    public void Reinvite(
        string displayName,
        IEnumerable<string> roles,
        string invitationToken,
        string invitationTokenHash,
        DateTimeOffset occurredAt,
        TimeSpan timeToLive,
        int? advisorCode = null)
```

reemplaza `var normalizedName = NormalizeDisplayName(displayName);` por:

```csharp
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedCode = NormalizeAdvisorCode(advisorCode);
```

y `DisplayName = normalizedName;` por:

```csharp
        DisplayName = normalizedName;
        // Nulo es "sin cambios", no "borrar": ver el <remarks>.
        AdvisorCode = normalizedCode ?? AdvisorCode;
```

**(d)** Reemplaza el método `Rename` completo, con su comentario (`:487-508`), por:

```csharp
    /// <summary>
    /// Cambia el perfil de la persona: el nombre y el código de asesor (spec 2026-09-24, D6).
    /// Vale en cualquier estado: el perfil es presentación y no cambia el acceso.
    /// </summary>
    /// <remarks>
    /// Los dos se validan antes de tocar nada, así que un rechazo no deja el nombre cambiado y el
    /// código viejo. La unicidad del código no se mira acá —una membresía no ve a las demás—: la
    /// revisa el handler y, ante una carrera, el índice de la base.
    /// </remarks>
    /// <returns>
    /// <c>false</c> —sin tocar versión ni fecha— cuando nombre y código normalizados son los que
    /// ya tiene. Un guardado repetido no invalida el If-Match de otra pestaña ni deja auditado un
    /// cambio que no ocurrió.
    /// </returns>
    public bool UpdateProfile(string displayName, int? advisorCode, DateTimeOffset occurredAt)
    {
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedCode = NormalizeAdvisorCode(advisorCode);
        if (string.Equals(DisplayName, normalizedName, StringComparison.Ordinal) &&
            AdvisorCode == normalizedCode)
        {
            return false;
        }

        DisplayName = normalizedName;
        AdvisorCode = normalizedCode;
        Version++;
        UpdatedAt = occurredAt;
        return true;
    }
```

**(e)** Agrega, justo después de `NormalizeDisplayName`:

```csharp
    // D2: entero >= 1. Nulo pasa: no tener código es un estado válido (D1).
    private static int? NormalizeAdvisorCode(int? value)
    {
        if (value is < 1)
        {
            throw new TenantDomainException(
                "tenancy.membership.advisor_code_invalid",
                "An advisor code must be a positive integer.");
        }

        return value;
    }
```

- [ ] **Step 4: Mantener vivo `display-name` (D7)**

En `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs`, reemplaza:

```csharp
        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: renombrar al mismo nombre es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        if (membership.Rename(command.DisplayName, now))
```

por:

```csharp
        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: renombrar al mismo nombre es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        //
        // Este endpoint no conoce el código de asesor, así que le pasa al agregado el que ya
        // tiene: renombrar por acá no lo borra. Se retira en un slice posterior (spec 2026-09-24,
        // D7), cuando el frontend ya use PUT .../profile.
        if (membership.UpdateProfile(command.DisplayName, membership.AdvisorCode, now))
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj
```

Esperado: compila y `Failed: 0` en todo el proyecto (incluye `InvitationServiceTests`, que sigue compilando porque el parámetro nuevo es opcional). Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Tenancy/Modules.Tenancy.Domain/Membership.cs src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberDisplayName.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipTests.cs
git commit -m "feat(tenancy): código de asesor en la membresía"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida. **No pushees todavía** (ver Global Constraints).

---

### Task 2: Persistencia — columna, índice parcial, traducción del `23505` y `IsAdvisorCodeTakenAsync`

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (en `ConfigureMembership`, después del mapeo de `DisplayName`)
- Create (generado): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddMembershipAdvisorCode.cs` y `.Designer.cs`
- Modify (generado): `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/TenancyDbContextModelSnapshot.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/IMembershipRepository.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs` (doble `MembershipRepo`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `Membership.UpdateProfile(string, int?, DateTimeOffset)` (Task 1); `IMembershipRepository.FindByIdAsync(MembershipId, TenantId, CancellationToken)`; `ITenancyUnitOfWork.SaveChangesAsync(CancellationToken)`.
- Produces:
  - `Task<bool> IMembershipRepository.IsAdvisorCodeTakenAsync(TenantId tenantId, int advisorCode, MembershipId? exceptMembershipId, CancellationToken cancellationToken)` — cuenta membresías en **cualquier** estado (D4).
  - `TenancyUnitOfWork.SaveChangesAsync` traduce `PostgresException { SqlState: "23505", ConstraintName: "IX_memberships_tenant_id_advisor_code" }` a `TenantDomainException("tenancy.membership.advisor_code_taken", …)`.
  - Columna `advisor_code integer NULL` e índice `IX_memberships_tenant_id_advisor_code` único parcial.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`:

**(a)** Agrega a los `using` del archivo:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
```

**(b)** Agrega estas tres pruebas justo antes de `private static readonly string[] AdvisorRoles = ["advisor"];`:

```csharp
    // D3: único por tenant sólo cuando existe, y sin filtrar por estado (D4). El índice es la
    // autoridad ante una carrera, así que se verifica su forma en la base y no sólo su efecto.
    [Fact]
    public async Task TheAdvisorCodeIndexIsUniquePerTenantAndSkipsMembersWithoutCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Registrar arranca la API, y arrancarla aplica las migraciones.
        await RegisterTenantWithOwnerAsync(factory);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'tenancy' AND indexname = 'IX_memberships_tenant_id_advisor_code'
            """,
            connection);
        var definition = (string?)await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.Contains("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, advisor_code)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (advisor_code IS NOT NULL)", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("state", definition, StringComparison.Ordinal);
    }

    // Review Focus 1: dos requests que pasan el chequeo previo a la vez. El segundo llega a la
    // base, y el 23505 de este índice tiene que salir como el código de dominio (422) y no como
    // un 500. Se ejerce saltándose el handler, que es exactamente lo que pasa en la carrera.
    // Las dos membresías nacen sin código y conviven: el índice es parcial.
    [Fact]
    public async Task ADuplicateCodeThatReachesTheDatabaseIsTheDomainCodeAndNotAServerError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        var second = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        await SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, first, 7);

        var error = await Assert.ThrowsAsync<TenantDomainException>(
            () => SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, second, 7));

        Assert.Equal("tenancy.membership.advisor_code_taken", error.Code);
    }

    // D4 y el chequeo previo: una quitada sigue ocupando su código, la propia membresía no se
    // cuenta a sí misma, y otro tenant tiene su propio espacio de códigos (D3).
    [Fact]
    public async Task IsAdvisorCodeTakenSeesRemovedMembersSkipsTheExcludedOneAndIgnoresOtherTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _, _) = await RegisterTenantWithOwnerAsync(factory);
        var holder = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        await SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, holder, 7);
        var removal = await SendActionAsync(ownerClient, tenantId, holder, "remove");
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var tenant = new TenantId(Guid.Parse(tenantId));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await memberships.IsAdvisorCodeTakenAsync(tenant, 7, null, cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(
            tenant, 7, new MembershipId(holder), cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(
            new TenantId(Guid.Parse(otherTenantId)), 7, null, cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(tenant, 8, null, cancellationToken));
    }
```

**(c)** Agrega este helper justo después de `SendDisplayNameAsync`:

```csharp
    // Carga la membresía y le pone el código por el agregado, sin pasar por ningún handler: es
    // lo que deja a la prueba llegar al índice sin el chequeo previo.
    private static async Task SetAdvisorCodeThroughTheAggregateAsync(
        QepApiFactory factory,
        string tenantId,
        Guid membershipId,
        int? advisorCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>();
        var membership = await memberships.FindByIdAsync(
            new MembershipId(membershipId),
            new TenantId(Guid.Parse(tenantId)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        membership.UpdateProfile(
            membership.DisplayName ?? DefaultDisplayName, advisorCode, DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
```

**(d)** En `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs`, dentro de `MembershipRepo`, agrega antes de `public void Add(Membership membership) => _memberships.Add(membership);`:

```csharp
        public Task<bool> IsAdvisorCodeTakenAsync(
            TenantId tenantId,
            int advisorCode,
            MembershipId? exceptMembershipId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipLifecycleApiTests"
```

Esperado: **no compila**. `CS1061: 'IMembershipRepository' no contiene una definición para 'IsAdvisorCodeTakenAsync'`. Pega la salida.

- [ ] **Step 3: Puerto e implementación del repositorio**

En `src/Modules/Tenancy/Modules.Tenancy.Application/IMembershipRepository.cs`, agrega antes de `void Add(Membership membership);`:

```csharp
    // ¿Alguna membresía del tenant —en cualquier estado, quitadas incluidas (spec 2026-09-24, D4)—
    // tiene este código de asesor? exceptMembershipId deja afuera a la propia membresía, para que
    // editar o re-invitar sin cambiar el código no choque consigo mismo. Es el chequeo previo que
    // responde claro; ante una carrera la autoridad es el índice único parcial.
    Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken);
```

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs`, agrega antes de `public void Add(Membership membership) => dbContext.Memberships.Add(membership);`:

```csharp
    public Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken)
    {
        // Sin filtro por estado a propósito (D4): una quitada conserva su código y lo sigue
        // bloqueando, igual que el índice.
        var query = dbContext.Memberships.Where(membership =>
            membership.TenantId == tenantId && membership.AdvisorCode == advisorCode);
        if (exceptMembershipId is { } except)
        {
            query = query.Where(membership => membership.Id != except);
        }

        return query.AnyAsync(cancellationToken);
    }
```

- [ ] **Step 4: Mapeo EF**

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs`, en `ConfigureMembership`, agrega justo después del bloque de `DisplayName` (el que termina en `.HasMaxLength(Membership.DisplayNameMaxLength);`):

```csharp
        // Spec 2026-09-24. Nulo es "no tiene código en el sistema externo" (D1). Único por tenant
        // sólo cuando existe (D3): el filtro deja convivir a todas las membresías sin código. No
        // filtra por estado (D4): una quitada conserva su código y lo sigue bloqueando. El nombre
        // va explícito porque TenancyUnitOfWork lo reconoce por nombre para traducir el 23505.
        membership.Property(value => value.AdvisorCode).HasColumnName("advisor_code");
        membership.HasIndex(value => new { value.TenantId, value.AdvisorCode })
            .IsUnique()
            .HasFilter("advisor_code IS NOT NULL")
            .HasDatabaseName("IX_memberships_tenant_id_advisor_code");
```

- [ ] **Step 5: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddMembershipAdvisorCode --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
Get-ChildItem src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations -Filter "*AddMembershipAdvisorCode.cs"
```

Esperado: `Done.` y un archivo `<timestamp>_AddMembershipAdvisorCode.cs` (más su `.Designer.cs`). Abre el `.cs` y verifica que su `Up` sea exactamente esto (el `Down` hace `DropIndex` y `DropColumn`):

```csharp
            migrationBuilder.AddColumn<int>(
                name: "advisor_code",
                schema: "tenancy",
                table: "memberships",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memberships_tenant_id_advisor_code",
                schema: "tenancy",
                table: "memberships",
                columns: new[] { "tenant_id", "advisor_code" },
                unique: true,
                filter: "advisor_code IS NOT NULL");
```

Si el `Up` trae cualquier otra operación (una columna o índice que no es de este cambio), **para**: el snapshot estaba desfasado y eso se resuelve antes de seguir, no se commitea encima.

- [ ] **Step 6: Traducir el `23505` del índice nuevo**

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs`, agrega después de la constante `TenantSlugIndex`:

```csharp
    /// <summary>
    /// Índice único parcial del código de asesor (spec 2026-09-24, D3). Se reconoce por nombre, y
    /// no sólo por el 23505, porque memberships tiene otros índices únicos —(user_id, tenant_id) e
    /// invitation_token_hash— y etiquetarlos como "código tomado" mandaría a corregir el campo
    /// equivocado.
    /// </summary>
    private const string AdvisorCodeIndex = "IX_memberships_tenant_id_advisor_code";
```

y agrega este `catch` después del de `TenantSlugIndex` (antes de la llave que cierra `SaveChangesAsync`):

```csharp
        // El handler ya pregunta antes con IsAdvisorCodeTakenAsync, pero dos requests pueden
        // pasar ese chequeo a la vez: el índice es la autoridad, y su choque es un 422 del
        // dominio, no un 500.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: AdvisorCodeIndex,
            })
        {
            throw new TenantDomainException(
                "tenancy.membership.advisor_code_taken",
                "The advisor code is already in use in this tenant.");
        }
```

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipLifecycleApiTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `Failed: 0` en las tres. En la primera están las tres pruebas nuevas **y** las de `display-name` (`Rename*`), que siguen verdes con el handler de Task 1. Pega la salida.

- [ ] **Step 8: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Tenancy/Modules.Tenancy.Application/IMembershipRepository.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/MembershipRepository.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyUnitOfWork.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations tests/Modules/Tenancy/Modules.Tenancy.UnitTests/InvitationServiceTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs
git status --short
git commit -m "feat(tenancy): columna e índice único parcial del código de asesor"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera del índice salvo lo que no es de esta tarea (debería estar vacío); el último comando sin salida.

---

### Task 3: Invitar con código (incluida la re-invitación) y DTOs

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/AdvisorCodeAvailability.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Api/AdvisorCodeInput.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs` (`MembershipListItemDto` y `ToListItemDto`, `:1-44`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` (`InviteAsync` `:216-235`, `ToResponse` `:237-249`, `ToListItemResponse` `:251-264`, records `:284-336`)
- Modify: `README.md` (sección "Invitación de memberships", `:683-740`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs`

**Interfaces:**
- Consumes: `IMembershipRepository.IsAdvisorCodeTakenAsync` (Task 2); `Membership.Invite(…, int? advisorCode = null)` y `Membership.Reinvite(…, int? advisorCode = null)` (Task 1).
- Produces:
  - `internal static class AdvisorCodeAvailability` con `public static Task EnsureAvailableAsync(IMembershipRepository memberships, TenantId tenantId, int? advisorCode, MembershipId? exceptMembershipId, CancellationToken cancellationToken)` → lanza `TenantDomainException("tenancy.membership.advisor_code_taken", …)`.
  - `internal static class AdvisorCodeInput` con `public static int? ToCommandValue(decimal? value)`.
  - `InviteMemberCommand(TenantId TenantId, string Email, string DisplayName, int? AdvisorCode, IReadOnlyCollection<string> Roles, string CorrelationId)`.
  - `MembershipDto(…, string? DisplayName, int? AdvisorCode, TenantId TenantId, …)`; `MembershipListItemDto(…, string? DisplayName, int? AdvisorCode, TenantId TenantId, …)`.
  - `MembershipInviteRequest(string Email, string DisplayName, IReadOnlyCollection<string>? Roles, decimal? AdvisorCode)`; `MembershipResponse` y `MembershipListItemResponse` con `int? AdvisorCode` después de `DisplayName`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs`:

**(a)** Reemplaza el helper `InviteAsync` por:

```csharp
    // advisorCode es object para poder mandar lo que un cliente mal armado mandaría: un decimal,
    // un número fuera de rango o un string. Nulo viaja como `"advisorCode": null`.
    private static async Task<HttpResponseMessage> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        string[]? roles = null,
        string displayName = DefaultDisplayName,
        object? advisorCode = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(
                new { email, displayName, roles = roles ?? DefaultRoles, advisorCode })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
```

**(b)** En los records `MembershipPayload` y `MembershipListItemPayload`, agrega `int? AdvisorCode` como último parámetro (después de `string? DisplayName`):

```csharp
        long Version,
        string? DisplayName,
        int? AdvisorCode);
```

**(c)** Agrega estas pruebas justo antes de `private static async Task<HttpResponseMessage> ReactivateAsync(`:

```csharp
    [Fact]
    public async Task InviteWithAnAdvisorCodeReturnsAndStoresIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 12);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(12, membership!.AdvisorCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var row = await QueryRowAsync(
            connection,
            "SELECT advisor_code FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.Equal("12", row![0]);
    }

    // D1: el código es opcional al invitar.
    [Fact]
    public async Task InviteWithoutAnAdvisorCodeLeavesItNull()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(membership!.AdvisorCode);
    }

    // Review Focus 3: nada que no sea un entero positivo puede salir como 500. Cero y negativo los
    // corta el validador; un decimal o un número que no cabe en un int los convierte AdvisorCodeInput
    // en un valor inválido para que también los corte el validador, con el campo marcado.
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(12.5)]
    [InlineData(99999999999L)]
    public async Task InviteWithAnAdvisorCodeThatIsNotAPositiveIntegerMarksTheField(object advisorCode)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail(), advisorCode: advisorCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("AdvisorCode", await ValidationFieldsAsync(response));
    }

    // Review Focus 3: las opciones web de System.Text.Json leen números desde string, así que "12"
    // es 12. Queda fijado para que un cambio de opciones no lo convierta en 500 sin aviso.
    [Fact]
    public async Task InviteWithTheAdvisorCodeAsANumericStringIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail(), advisorCode: "12");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(12, membership!.AdvisorCode);
    }

    // D3: dos membresías del mismo tenant no comparten código.
    [Fact]
    public async Task InviteWithAnAdvisorCodeTakenInTheTenantIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);

        var response = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);
    }

    // D4 / Review Focus 2: la quitada conserva su código y lo sigue bloqueando.
    [Fact]
    public async Task ARemovedMembershipKeepsBlockingItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var invited = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);
        var holder = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        var removal = await RemoveAsync(client, TenantId, holder!.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        var response = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);
    }

    // Review Focus 2: re-invitar a la quitada con su propio código no choca consigo misma.
    [Fact]
    public async Task ReinvitingARemovedMemberWithItsOwnCodeIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var invited = await InviteAsync(client, TenantId, email, advisorCode: 7);
        var holder = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        var removal = await RemoveAsync(client, TenantId, holder!.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        var response = await InviteAsync(client, TenantId, email, advisorCode: 7);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewed = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(holder.Id, renewed!.Id);
        Assert.Equal("Invited", renewed.State);
        Assert.Equal(7, renewed.AdvisorCode);
    }

    // Review Focus 2 (e): re-invitar a la quitada sin código en el cuerpo le conserva el suyo, y
    // el código sigue ocupado para cualquier otra persona.
    [Fact]
    public async Task ReinvitingARemovedMemberWithoutACodeKeepsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var invited = await InviteAsync(client, TenantId, email, advisorCode: 7);
        var holder = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        var removal = await RemoveAsync(client, TenantId, holder!.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        var response = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewed = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(holder.Id, renewed!.Id);
        Assert.Equal("Invited", renewed.State);
        Assert.Equal(7, renewed.AdvisorCode);
        var taken = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
    }

    // Spec: en la re-invitación de una vencida un código en el cuerpo reemplaza al que había.
    [Fact]
    public async Task ReinvitingALapsedInvitationAppliesTheNewCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var first = await InviteAsync(client, TenantId, email, advisorCode: 12);
        var invited = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        await LapseInvitationAsync(database, invited!.Id);

        var response = await InviteAsync(client, TenantId, email, advisorCode: 34);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewed = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(invited.Id, renewed!.Id);
        Assert.Equal(34, renewed.AdvisorCode);
    }

    // Review Focus 2: una vencida no se renueva con el código de otra persona, y el rechazo no la
    // deja a medias: ni renovada, ni con el código nuevo.
    [Fact]
    public async Task ReinvitingALapsedInvitationWithACodeTakenByAnotherIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var first = await InviteAsync(client, TenantId, email);
        var lapsed = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        await LapseInvitationAsync(database, lapsed!.Id);
        await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);

        var response = await InviteAsync(client, TenantId, email, advisorCode: 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var row = await QueryRowAsync(
            connection,
            "SELECT version, advisor_code FROM tenancy.memberships WHERE id = @id",
            ("id", lapsed.Id));
        Assert.Equal(lapsed.Version.ToString(CultureInfo.InvariantCulture), row![0]);
        Assert.Equal(string.Empty, row[1]);
    }

    // Review Focus 2: una invitación viva es la no-op de siempre —el cuerpo se ignora entero—, así
    // que un código tomado tampoco la convierte en un 422.
    [Fact]
    public async Task InvitingAgainWhileTheInvitationIsLiveIgnoresEvenATakenCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        await InviteAsync(client, TenantId, email);
        await InviteAsync(client, TenantId, NewEmail(), advisorCode: 7);

        var response = await InviteAsync(client, TenantId, email, advisorCode: 7);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(membership!.AdvisorCode);
    }

    [Fact]
    public async Task ListShowsEachMembersAdvisorCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var invited = await InviteAsync(client, TenantId, NewEmail(), advisorCode: 12);
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        var row = Assert.Single(list!.Items, item => item.Id == membership!.Id);
        Assert.Equal(12, row.AdvisorCode);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipApiTests"
```

Esperado: compila y **fallan** las pruebas nuevas —el request ignora `advisorCode` hoy—: `InviteWithAnAdvisorCodeReturnsAndStoresIt` (`Assert.Equal() Failure: Expected: 12, Actual: null`), las cuatro filas de `InviteWithAnAdvisorCodeThatIsNotAPositiveIntegerMarksTheField` (`Expected: UnprocessableEntity, Actual: Created`), `InviteWithAnAdvisorCodeTakenInTheTenantIsRejected`, `ARemovedMembershipKeepsBlockingItsCode`, `ReinvitingARemovedMemberWithItsOwnCodeIsAccepted` y `ReinvitingARemovedMemberWithoutACodeKeepsIt` (código nulo), `ReinvitingALapsedInvitationAppliesTheNewCode`, `ReinvitingALapsedInvitationWithACodeTakenByAnotherIsRejected`, `InviteWithTheAdvisorCodeAsANumericStringIsAccepted` y `ListShowsEachMembersAdvisorCode`. `InviteWithoutAnAdvisorCodeLeavesItNull` e `InvitingAgainWhileTheInvitationIsLiveIgnoresEvenATakenCode` ya pasan (fijan comportamiento que no debe cambiar). Pega la salida.

- [ ] **Step 3: El chequeo previo compartido**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/AdvisorCodeAvailability.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El chequeo previo de unicidad del código de asesor (spec 2026-09-24, D3), compartido por
/// invitar y por PUT .../profile. Existe para responder claro sin depender de la base; ante una
/// carrera la autoridad sigue siendo el índice único parcial, que TenancyUnitOfWork traduce al
/// mismo código.
/// </summary>
internal static class AdvisorCodeAvailability
{
    public static async Task EnsureAvailableAsync(
        IMembershipRepository memberships,
        TenantId tenantId,
        int? advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken)
    {
        // Sin código no hay nada que chocar (D1): el índice deja convivir a todas las nulas.
        if (advisorCode is not { } code)
        {
            return;
        }

        if (await memberships.IsAdvisorCodeTakenAsync(
                tenantId, code, exceptMembershipId, cancellationToken))
        {
            throw new TenantDomainException(
                "tenancy.membership.advisor_code_taken",
                "The advisor code is already in use in this tenant.");
        }
    }
}
```

- [ ] **Step 4: Comando, validador y handler de invitar**

Reemplaza `src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs` completo por:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record InviteMemberCommand(
    TenantId TenantId,
    string Email,
    string DisplayName,
    int? AdvisorCode,
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
        // Mismo criterio para el código de asesor (spec 2026-09-24, D2): el dominio da
        // `advisor_code_invalid`, el validador da `errors.AdvisorCode`. Opcional (D1).
        RuleFor(command => command.AdvisorCode)
            .GreaterThan(0)
            .When(command => command.AdvisorCode is not null);
        RuleFor(command => command.Roles).NotNull();
    }
}

public sealed class InviteMemberHandler(
    IIdentityProvisioning identityProvisioning,
    IMembershipRepository membershipRepository,
    IRoleReferenceValidator roleReferenceValidator,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    IValidator<InviteMemberCommand> validator)
    : ICommandHandler<InviteMemberCommand, MembershipDto>
{
    private const string Origin = "invitation";

    public async Task<MembershipDto> HandleAsync(
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);
        // Cualquier rol del catálogo del tenant es invitable, incluido admin y los roles
        // definidos por el tenant: la única validación de rol es que el catálogo lo conozca.
        await EnsureKnownRolesAsync(command.TenantId, command.Roles, cancellationToken);

        // Desde acá hasta el commit se corre serializado con el borrado de usuarios huérfanos
        // de Identity; ver ITenancyUnitOfWork.BeginUserLifecycleScopeAsync.
        await using var lifecycle = await unitOfWork.BeginUserLifecycleScopeAsync(
            command.Email,
            cancellationToken);

        // Aprovisiona (o resuelve) el usuario invitado por el contrato de Identity. Es
        // idempotente por email, así que re-invitar reutiliza el mismo id de usuario.
        var userId = await identityProvisioning.GetOrProvisionInvitedUserAsync(
            command.Email,
            cancellationToken);

        var existing = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            command.TenantId,
            cancellationToken);
        if (existing is not null)
        {
            var renewed = await ReinviteExistingAsync(existing, command, cancellationToken);
            await lifecycle.CommitAsync(cancellationToken);
            return renewed;
        }

        // El chequeo del código va después de aprovisionar porque la re-invitación necesita
        // excluir a la propia membresía, y sólo se sabe cuál es con el usuario resuelto. Si
        // choca, el usuario recién aprovisionado queda sin membresía y lo recoge
        // OrphanUserCleanupWorker, igual que cuando un invite falla en la base.
        await AdvisorCodeAvailability.EnsureAvailableAsync(
            membershipRepository,
            command.TenantId,
            command.AdvisorCode,
            exceptMembershipId: null,
            cancellationToken);

        // El token plano nace acá y sólo entra al agregado para viajar en el evento de
        // dominio (outbox → email); la fila persiste únicamente su hash.
        var invitationToken = InvitationTokens.Generate();
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
            Membership.DefaultInvitationTimeToLive,
            command.AdvisorCode);
        membershipRepository.Add(membership);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.invited",
            "membership",
            membership.Id.ToString(),
            "success",
            [],
            clock.UtcNow);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await lifecycle.CommitAsync(cancellationToken);
        return membership.ToDto();
    }

    /// <summary>
    /// Decide qué significa una segunda invitación para una membresía que ya existe.
    /// </summary>
    /// <remarks>
    /// La renovación pasa sobre la fila existente, nunca insertando una segunda:
    /// (UserId, TenantId) es UNIQUE (TenancyDbContext.cs:105). Crear acá una membresía nueva
    /// —como hacía este handler para cualquier estado que no fuera Invited/Active— viola ese
    /// índice y sale como un 500. Ver SDD-CT-15.
    /// </remarks>
    private async Task<MembershipDto> ReinviteExistingAsync(
        Membership existing,
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // Una invitación viva y una membresía activa son las dos no-ops. Renovar una invitación
        // viva movería un plazo con el que alguien cuenta e invalidaría el link que ya está en
        // su bandeja. El nombre y el código del cuerpo se ignoran igual que los roles —tampoco
        // se valida si el código está tomado—: para cambiarle el perfil a un miembro está
        // PUT .../profile (spec 2026-09-24, D6).
        var invitationIsLive =
            existing.State == MembershipState.Invited && now <= existing.ExpiresAt;
        if (invitationIsLive || existing.State == MembershipState.Active)
        {
            return existing.ToDto();
        }

        // Todo lo demás es una invitación vencida (todavía en Invited, porque el vencimiento es
        // perezoso y nadie intentó entrar), una ya marcada como Expired o una membresía quitada.
        // Las tres son renovables (SDD-OD-04; la quitada, por decisión del owner) y vuelven a
        // Invited, así que la persona tiene que aceptar de nuevo. Suspended no: Reinvite la
        // rechaza y se levanta con Reactivate (SDD-OD-13).
        //
        // El código se revisa excluyendo a esta misma membresía: una quitada que vuelve con su
        // propio código no choca consigo misma (D4), pero no puede llevarse el de otra persona.
        // Sin código en el cuerpo no hay nada que revisar: Reinvite conserva el que ya tenía, que
        // es suyo (decisión del developer, 2026-09-24; borrar es sólo por PUT .../profile).
        await AdvisorCodeAvailability.EnsureAvailableAsync(
            membershipRepository,
            command.TenantId,
            command.AdvisorCode,
            existing.Id,
            cancellationToken);

        // Token nuevo en cada renovación: el link vencido muere con su ventana.
        var invitationToken = InvitationTokens.Generate();
        existing.Reinvite(
            command.DisplayName,
            command.Roles,
            invitationToken,
            InvitationTokens.HashOf(invitationToken),
            now,
            Membership.DefaultInvitationTimeToLive,
            command.AdvisorCode);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.invited",
            "membership",
            existing.Id.ToString(),
            "success",
            [],
            now);

        // La renovación sólo le llega a la persona por el outbox: InvitationDeliveryWorker
        // manda el email a partir de este evento. Persistir sin emitirlo no renueva nada
        // que el invitado pueda ver.
        foreach (var domainEvent in existing.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return existing.ToDto();
    }

    private async Task EnsureKnownRolesAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var normalizedRoles = roles
            .Select(role => role.Trim())
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRoles.Length == 0)
        {
            throw new TenantDomainException(
                "tenancy.membership.roles_required",
                "A membership requires at least one role.");
        }

        foreach (var role in normalizedRoles)
        {
            if (!await roleReferenceValidator.IsKnownRoleAsync(tenantId, role, cancellationToken))
            {
                throw new TenantDomainException(
                    "tenancy.membership.role_unknown",
                    $"The role '{role}' is not part of the authorization catalog.");
            }
        }
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipInvite))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot invite members to this tenant.");
        }
    }
}
```

- [ ] **Step 5: DTOs de Application**

En `src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs`, reemplaza el record y el mapeo por:

```csharp
/// <param name="DisplayName">
/// Nulo cuando la membresía es anterior al nombre o es la del owner: el reinvite no-op devuelve
/// la fila existente tal como está (spec 2026-09-11, D5).
/// </param>
/// <param name="AdvisorCode">
/// El código del sistema externo del tenant (spec 2026-09-24). Nulo si no tiene (D1).
/// </param>
public sealed record MembershipDto(
    MembershipId Id,
    Guid UserId,
    string? DisplayName,
    int? AdvisorCode,
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
            membership.AdvisorCode,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}
```

En `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs`, en el `<param>` de `DisplayName` de `MembershipListItemDto` agrega después:

```csharp
/// <param name="AdvisorCode">
/// El código con el que el sistema externo del tenant identifica a la persona (spec 2026-09-24).
/// Nulo si no tiene (D1); la fila del roster entonces no muestra nada.
/// </param>
```

y reemplaza el record y el mapeo por:

```csharp
public sealed record MembershipListItemDto(
    MembershipId Id,
    Guid UserId,
    string? Email,
    string? DisplayName,
    int? AdvisorCode,
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
    public static MembershipListItemDto ToListItemDto(
        this Membership membership,
        string? email,
        Tenant tenant) =>
        new(
            membership.Id,
            membership.UserId,
            email,
            membership.DisplayName,
            membership.AdvisorCode,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version,
            tenant.IsOwner(membership.Id));
}
```

- [ ] **Step 6: La traducción del JSON y el contrato HTTP**

Crea `src/Modules/Tenancy/Modules.Tenancy.Api/AdvisorCodeInput.cs`:

```csharp
namespace Modules.Tenancy.Api;

/// <summary>
/// Traduce el <c>advisorCode</c> del JSON al <c>int?</c> que esperan los comandos.
/// </summary>
/// <remarks>
/// El request lo recibe como <see cref="decimal"/> y no como <see cref="int"/> porque un
/// <c>12.5</c> o un <c>99999999999</c> en un <c>int?</c> hacen fallar el binding con
/// <c>BadHttpRequestException</c>, y <c>ApiExceptionHandler</c> reduce toda excepción que no
/// reconoce a un 500 (mismo problema que documenta <c>GeographyEndpoints</c>). Acá cualquier valor
/// que no sea un entero representable llega al validador como <see cref="NotAPositiveInteger"/>,
/// que lo rechaza con <c>errors.AdvisorCode</c>: el único 422 que el formulario sabe marcar.
/// </remarks>
internal static class AdvisorCodeInput
{
    private const int NotAPositiveInteger = -1;

    public static int? ToCommandValue(decimal? value) =>
        value switch
        {
            null => null,
            { } number when number == decimal.Truncate(number)
                && number >= int.MinValue
                && number <= int.MaxValue => (int)number,
            _ => NotAPositiveInteger,
        };
}
```

En `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs`:

**(a)** En `InviteAsync`, reemplaza la construcción del comando por:

```csharp
            new InviteMemberCommand(
                new TenantId(tenantId),
                request.Email,
                request.DisplayName,
                AdvisorCodeInput.ToCommandValue(request.AdvisorCode),
                request.Roles ?? [],
                httpContext.TraceIdentifier),
```

**(b)** Reemplaza `ToResponse` y `ToListItemResponse` por:

```csharp
    private static MembershipResponse ToResponse(MembershipDto membership, string email) =>
        new(
            membership.Id.Value,
            membership.UserId,
            email,
            membership.DisplayName,
            membership.AdvisorCode,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);

    private static MembershipListItemResponse ToListItemResponse(MembershipListItemDto membership) =>
        new(
            membership.Id.Value,
            membership.UserId,
            membership.Email,
            membership.DisplayName,
            membership.AdvisorCode,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version,
            membership.IsOwner);
```

**(c)** Reemplaza el comentario y el record `MembershipInviteRequest` por:

```csharp
/// <param name="DisplayName">
/// Obligatorio. Sin él —ausente, vacío o de más de 150 caracteres— responde 422
/// <c>validation.failed</c> con <c>errors.DisplayName</c>, el único 422 que el formulario sabe
/// marcar en el input.
/// </param>
/// <param name="AdvisorCode">
/// Opcional (spec 2026-09-24, D1). <see cref="decimal"/> a propósito —ver
/// <see cref="AdvisorCodeInput"/>—: lo que no sea un entero positivo responde 422 con
/// <c>errors.AdvisorCode</c>; uno ya tomado en el tenant, 422
/// <c>tenancy.membership.advisor_code_taken</c>.
/// </param>
public sealed record MembershipInviteRequest(
    string Email,
    string DisplayName,
    IReadOnlyCollection<string>? Roles,
    decimal? AdvisorCode);
```

**(d)** Reemplaza `MembershipResponse` por:

```csharp
public sealed record MembershipResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string? DisplayName,
    int? AdvisorCode,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);
```

**(e)** Reemplaza el comentario y el record `MembershipListItemResponse` por:

```csharp
/// <param name="DisplayName">
/// Nulo en membresías anteriores al nombre y en el owner hasta que se cargue. La celda
/// "Persona" muestra entonces sólo el correo, con el aviso "Sin nombre".
/// </param>
/// <param name="AdvisorCode">
/// Nulo si la persona no tiene código en el sistema externo del tenant (spec 2026-09-24, D1): la
/// fila no muestra nada, en vez de un "Cód." vacío.
/// </param>
public sealed record MembershipListItemResponse(
    Guid Id,
    Guid UserId,
    string? Email,
    string? DisplayName,
    int? AdvisorCode,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version,
    bool IsOwner);
```

- [ ] **Step 7: README — invitar con código**

En `README.md`, sección "Invitación de memberships":

**(a)** En el ejemplo de PowerShell, reemplaza el cuerpo por:

```powershell
$body = @{
  email = "new.member@example.com"
  displayName = "Ana Pérez"
  advisorCode = 12
  roles = @("advisor")
} | ConvertTo-Json
```

**(b)** En el JSON de la respuesta `201 Created`, agrega después de `"displayName": "Ana Pérez",`:

```json
  "advisorCode": 12,
```

**(c)** Reemplaza el párrafo que empieza con `` `displayName` es obligatorio`` por:

```markdown
`displayName` es obligatorio: se guarda sin espacios a los costados y admite entre 1 y 150
caracteres. Sin él responde `422 validation.failed` con `errors.DisplayName`.

`advisorCode` es opcional: el código entero positivo con el que el sistema externo del tenant
(ERP, contabilidad) identifica a la persona. Se guarda como `integer`, así que `0012` y `12` son
el mismo. Un valor que no sea un entero mayor que cero responde `422 validation.failed` con
`errors.AdvisorCode`; uno que ya tenga otra membresía del tenant —incluida una quitada, que
conserva el suyo—, `422 tenancy.membership.advisor_code_taken`.

Una invitación viva o una membresía activa ignoran el nombre y el código del cuerpo. Renovar una
invitación vencida o una membresía quitada reescribe el nombre y, si el cuerpo trae
`advisorCode`, lo reemplaza; sin `advisorCode` conserva el que la membresía ya tenía. Para
cambiárselos a un miembro —incluido borrar el código— está `PUT .../profile`.
```

- [ ] **Step 8: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipApiTests|FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipLifecycleApiTests|FullyQualifiedName~Modules.Tenancy.IntegrationTests.InvitationApiTests"
```

Esperado: `Failed: 0`. Pega la salida.

- [ ] **Step 9: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Tenancy/Modules.Tenancy.Application/AdvisorCodeAvailability.cs src/Modules/Tenancy/Modules.Tenancy.Application/InviteMember.cs src/Modules/Tenancy/Modules.Tenancy.Application/MembershipDto.cs src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs src/Modules/Tenancy/Modules.Tenancy.Api/AdvisorCodeInput.cs src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs README.md tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipApiTests.cs
git commit -m "feat(tenancy): invitar con código de asesor"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 4: `PUT .../profile` — nombre y código juntos

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberProfile.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs` (registro de ruta después de `display-name` `:72-82`; handler nuevo después de `UpdateDisplayNameAsync`; record de request)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:95-97`
- Modify: `README.md` (tabla de rutas `:582`; sección "Nombre del miembro" `:753-780`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`

**Interfaces:**
- Consumes: `Membership.UpdateProfile(string, int?, DateTimeOffset)` (Task 1); `AdvisorCodeAvailability.EnsureAvailableAsync` y `AdvisorCodeInput.ToCommandValue` (Task 3); `TenantLoader.LoadAsync`, `MembershipLoader.LoadAsync`, `IUserDirectory.GetEmailAsync`, `ToListItemDto(string?, Tenant)`.
- Produces:
  - `public sealed record UpdateMemberProfileCommand(TenantId TenantId, MembershipId MembershipId, string DisplayName, int? AdvisorCode, long ExpectedVersion, string CorrelationId) : ICommand<MembershipListItemDto>`
  - `UpdateMemberProfileValidator`, `UpdateMemberProfileHandler`
  - `PUT /api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}/profile` con `MembershipProfileUpdateRequest(string? DisplayName, decimal? AdvisorCode)` → `200 MembershipListItemResponse` + `ETag`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs`:

**(a)** Reemplaza `InviteAsync` y `SendInviteAsync` por:

```csharp
    private static async Task<Guid> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string>? roles = null,
        string displayName = DefaultDisplayName,
        int? advisorCode = null)
    {
        var response = await SendInviteAsync(
            client, tenantId, email, roles ?? AdvisorRoles, displayName, advisorCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        return membership!.Id;
    }

    private static async Task<HttpResponseMessage> SendInviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string> roles,
        string displayName,
        int? advisorCode = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName, roles, advisorCode })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
```

**(b)** Agrega, después de `SendDisplayNameAsync`:

```csharp
    // advisorCode es object para poder mandar un decimal (Review Focus 3).
    private static async Task<HttpResponseMessage> SendProfileAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string? displayName,
        object? advisorCode,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile")
        {
            Content = JsonContent.Create(new { displayName, advisorCode })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
```

**(c)** En el record `MembershipListItemPayload`, agrega `int? AdvisorCode` como último parámetro:

```csharp
        bool IsOwner,
        string? DisplayName,
        int? AdvisorCode);
```

**(d)** Agrega estas pruebas justo antes de `TheAdvisorCodeIndexIsUniquePerTenantAndSkipsMembersWithoutCode` (Task 2):

```csharp
    // 200 con la fila entera y el ETag nuevo: el diálogo repinta la fila y ya tiene qué mandar
    // en el próximo If-Match. La auditoría va en la misma transacción que el cambio.
    [Fact]
    public async Task ProfileUpdateReturnsTheRowWithANewEtagAndIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "  Ana María Pérez  ", 12);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(memberId, membership!.Id);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(2, membership.Version);
        Assert.Equal("Invited", membership.State);
        Assert.Equal(AdvisorRoles, membership.Roles);
        var outcomes = await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.profile_updated");
        Assert.Equal("success", Assert.Single(outcomes));
    }

    // Guardar sin tocar nada no es un cambio: misma versión y nada auditado, o una pantalla
    // abierta en otro lado recibe un 412 falso (spec 2026-09-24, Dominio).
    [Fact]
    public async Task ProfileUpdateWithoutChangesKeepsTheVersionAndRecordsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 12);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 12);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Empty(await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.profile_updated"));
    }

    [Fact]
    public async Task ProfileUpdateRequiresIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", 12, expectedVersion: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem!.Code);
    }

    [Fact]
    public async Task ProfileUpdateWithAStaleVersionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", 12, expectedVersion: 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdateWithAZeroCodeMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("AdvisorCode", out _));
    }

    // Review Focus 3: un decimal no puede salir como 500.
    [Fact]
    public async Task ProfileUpdateWithADecimalCodeMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 12.5m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("AdvisorCode", out _));
    }

    // D3: el código ya lo tiene otra membresía del tenant.
    [Fact]
    public async Task ProfileUpdateWithACodeTakenInTheTenantIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);
    }

    // D3: cada tenant tiene su propio sistema externo, así que el mismo código vale en otro.
    [Fact]
    public async Task TheSameCodeInAnotherTenantIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _, otherOwnerClient) = await RegisterTenantWithOwnerAsync(factory);
        await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var otherMemberId = await InviteAsync(
            otherOwnerClient, otherTenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            otherOwnerClient, otherTenantId, otherMemberId, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(7, membership!.AdvisorCode);
    }

    // D1: mandar null borra el código, y borrarlo lo libera para otra persona.
    [Fact]
    public async Task ClearingTheCodeWithNullFreesItForAnotherMember()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var holder = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var other = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var cleared = await SendProfileAsync(
            ownerClient, tenantId, holder, DefaultDisplayName, null);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedRow = await cleared.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(clearedRow!.AdvisorCode);
        Assert.Equal(2, clearedRow.Version);

        var taken = await SendProfileAsync(ownerClient, tenantId, other, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    // 403 y nunca 404: la ruta pide un tenant que no es el del contexto (doble capa).
    [Fact]
    public async Task ProfileUpdateFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var otherClient = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await SendProfileAsync(
            otherClient, tenantId, ownerMembershipId, "Ana María Pérez", 12);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdateRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        using var readerOnly = CreateClient(factory, Guid.CreateVersion7().ToString(), tenantId);
        readerOnly.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var response = await SendProfileAsync(
            readerOnly, tenantId, memberId, "Ana María Pérez", 12);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // D7: display-name sigue vivo hasta que el frontend migre, y renombrar por ahí no borra el
    // código que se cargó por profile o al invitar.
    [Fact]
    public async Task TheDisplayNameEndpointKeepsTheAdvisorCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Ana María Pérez", membership!.DisplayName);
        Assert.Equal(7, membership.AdvisorCode);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipLifecycleApiTests"
```

Esperado: compila; **fallan** todas las `ProfileUpdate*`, `TheSameCodeInAnotherTenantIsAccepted` y `ClearingTheCodeWithNullFreesItForAnotherMember` con `Expected: OK / UnprocessableEntity / PreconditionRequired / …, Actual: NotFound` (la ruta no existe; `ProfileUpdateFromAnotherTenantIsForbidden` y `ProfileUpdateRequiresTheManagePermission` también dan `NotFound`). `TheDisplayNameEndpointKeepsTheAdvisorCode` ya pasa (lo aseguró Task 1). Pega la salida.

- [ ] **Step 3: Comando, validador y handler**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberProfile.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateMemberProfileCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string DisplayName,
    int? AdvisorCode,
    long ExpectedVersion,
    string CorrelationId) : ICommand<MembershipListItemDto>;

/// <summary>
/// Texto libre y un número, así que lleva validador aunque el dominio ya valide: el dominio da el
/// código (<c>display_name_invalid</c>, <c>advisor_code_invalid</c>), el validador da el campo
/// (<c>errors.DisplayName</c>, <c>errors.AdvisorCode</c>), que es lo único con lo que el diálogo
/// sabe marcar el input.
/// </summary>
public sealed class UpdateMemberProfileValidator : AbstractValidator<UpdateMemberProfileCommand>
{
    public UpdateMemberProfileValidator()
    {
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        RuleFor(command => command.AdvisorCode)
            .GreaterThan(0)
            .When(command => command.AdvisorCode is not null);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Edita el perfil de un miembro del roster —nombre y código de asesor— en un solo PUT (spec
/// 2026-09-24, D6). Uno solo y no dos porque el diálogo edita los dos campos juntos: con dos PUT,
/// el primero sube la versión y el segundo viaja con el If-Match viejo, y la pantalla se pisa
/// sola con un 412. Nombre y código comparten <c>AdvisorshipManage</c>, así que no hay dos
/// permisos que separar, que es lo que sí separó a display-name de roles.
///
/// Reemplaza a <see cref="UpdateMemberDisplayNameHandler"/>, que convive hasta que el frontend
/// migre (D7). Se permite en cualquier estado y sobre la propia membresía, igual que aquél.
/// </summary>
public sealed class UpdateMemberProfileHandler(
    IMembershipRepository membershipRepository,
    ITenantRepository tenantRepository,
    IUserDirectory userDirectory,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateMemberProfileCommand> validator)
    : ICommandHandler<UpdateMemberProfileCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        UpdateMemberProfileCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await TenantLoader.LoadAsync(
            tenantRepository, command.TenantId, cancellationToken);
        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        if (membership.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The membership changed after it was loaded.");
        }

        // Sólo se pregunta si el código cambia: guardar el propio no choca consigo mismo, y así
        // un guardado sin cambios no cuesta una consulta.
        if (command.AdvisorCode != membership.AdvisorCode)
        {
            await AdvisorCodeAvailability.EnsureAvailableAsync(
                membershipRepository,
                command.TenantId,
                command.AdvisorCode,
                membership.Id,
                cancellationToken);
        }

        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: un guardado sin cambios es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        if (membership.UpdateProfile(command.DisplayName, command.AdvisorCode, now))
        {
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.membership.profile_updated",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
        return membership.ToListItemDto(email, tenant);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot update member profiles of this tenant.");
        }
    }
}
```

- [ ] **Step 4: Endpoint**

En `src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs`:

**(a)** Reemplaza el comentario de `display-name` y agrega la ruta nueva. El bloque desde `// Con If-Match, igual que `/roles`` hasta el `.ProducesProblem(StatusCodes.Status422UnprocessableEntity);` del `display-name` queda:

```csharp
        // Con If-Match, igual que `/roles`: dos administradores renombrando a la misma persona es
        // una carrera real, y el nombre termina impreso en un PDF que se le manda al cliente.
        // Se retira cuando el frontend migre a `/profile` (spec 2026-09-24, D7).
        group.MapPut("/{membershipId:guid}/display-name", UpdateDisplayNameAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Accepts<MembershipDisplayNameUpdateRequest>("application/json")
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Nombre y código de asesor en un solo PUT (spec 2026-09-24, D6): el diálogo los edita
        // juntos, y dos PUT desde el mismo diálogo chocarían entre sí por el If-Match.
        group.MapPut("/{membershipId:guid}/profile", UpdateProfileAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Accepts<MembershipProfileUpdateRequest>("application/json")
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

**(b)** Agrega, justo después del método `UpdateDisplayNameAsync`:

```csharp
    private static async Task<IResult> UpdateProfileAsync(
        Guid tenantId,
        Guid membershipId,
        MembershipProfileUpdateRequest request,
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
            new UpdateMemberProfileCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                request.DisplayName ?? string.Empty,
                AdvisorCodeInput.ToCommandValue(request.AdvisorCode),
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{membership.Version}\"";
        return Results.Ok(ToListItemResponse(membership));
    }
```

**(c)** Agrega, justo después del record `MembershipDisplayNameUpdateRequest`:

```csharp
/// <summary>
/// El perfil completo que edita el diálogo "Editar miembro" (spec 2026-09-24, D6).
/// <c>DisplayName</c> es nullable por la misma razón que en
/// <see cref="MembershipDisplayNameUpdateRequest"/>: ausente llega como nulo, el endpoint lo pasa
/// a vacío y el validador lo rechaza con <c>errors.DisplayName</c>. <c>AdvisorCode</c> nulo borra
/// el código (D1); es <see cref="decimal"/> por lo que explica <see cref="AdvisorCodeInput"/>.
/// </summary>
public sealed record MembershipProfileUpdateRequest(string? DisplayName, decimal? AdvisorCode);
```

- [ ] **Step 5: Registrar el handler**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega justo después del registro de `UpdateMemberDisplayNameHandler` (`:95-97`):

```csharp
        services.AddScoped<
            ICommandHandler<UpdateMemberProfileCommand, MembershipListItemDto>,
            UpdateMemberProfileHandler>();
```

El validador lo levanta `AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>()` (`:417`), que es el mismo assembly; no hace falta registrarlo a mano.

- [ ] **Step 6: README — `profile` y el verbo real de `display-name`**

En `README.md`:

**(a)** En la tabla de rutas (`:582`), reemplaza `` `suspend`, `remove`, `reactivate`, `roles`, `display-name` por membership `` por `` `suspend`, `remove`, `reactivate`, `roles`, `profile`, `display-name` por membership ``.

**(b)** Reemplaza la sección `### Nombre del miembro` completa (desde el título hasta el párrafo que termina en `` `errors.DisplayName`. `` antes de `### Aceptación de la invitación`) por:

````markdown
### Perfil del miembro: nombre y código de asesor

| Método | Ruta                                                              | Permiso              |
| ------ | ----------------------------------------------------------------- | -------------------- |
| `PUT`  | `/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile`   | `advisorship.manage` |

Cambia, en un solo request, el nombre con el que el tenant presenta a la persona —el que imprime
el PDF de cotización— y su código de asesor, el entero con el que la identifica el sistema externo
del tenant. Van juntos porque el diálogo los edita juntos: con dos requests, el segundo viajaría
con el `If-Match` viejo y respondería `412`. Vale en cualquier estado de la membresía y sobre la
propia: el owner, que entra por `register-tenant` sin nombre ni código, los carga desde acá.

Exige `If-Match` con la versión cargada, igual que `PUT .../roles`, y responde `200` con la fila
del roster y el `ETag` nuevo. Guardar sin cambios no sube la versión ni se audita; un cambio real
se audita como `tenancy.membership.profile_updated`.

```powershell
$body = @{ displayName = "Ana María Pérez"; advisorCode = 12 } | ConvertTo-Json
$putHeaders = $headers.Clone()
$putHeaders["If-Match"] = '"1"'

Invoke-RestMethod `
  -Method Put `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/memberships/$membershipId/profile" `
  -Headers $putHeaders `
  -ContentType "application/json" `
  -Body $body
```

`advisorCode` es opcional; `null` borra el código. Sin `If-Match` responde
`428 precondition.if_match_required`; con una versión vieja, `412`; con un nombre vacío o de más
de 150 caracteres, `422 validation.failed` con `errors.DisplayName`; con un código que no sea un
entero mayor que cero, `422 validation.failed` con `errors.AdvisorCode`; con un código que ya
tiene otra membresía del tenant —incluida una quitada, que conserva el suyo—,
`422 tenancy.membership.advisor_code_taken`. El mismo código en otro tenant es válido.

`PUT .../display-name` (`{ displayName }`, mismas reglas de `If-Match`, auditado como
`tenancy.membership.renamed`) sigue disponible mientras el frontend migra a `profile`; cambia sólo
el nombre y conserva el código. Se retira en un slice posterior.
````

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipLifecycleApiTests|FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `Failed: 0` en las dos. Pega la salida.

- [ ] **Step 8: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Tenancy/Modules.Tenancy.Application/UpdateMemberProfile.cs src/Modules/Tenancy/Modules.Tenancy.Api/MembershipEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs README.md tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/MembershipLifecycleApiTests.cs
git commit -m "feat(tenancy): PUT profile edita nombre y código de asesor"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 5: El roster busca por código exacto

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs` (`ApplySearch` y `Matches`, `:301-323` antes de Task 3)
- Create: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs`
- Create: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListMembershipsHandlerTests.cs`

**Interfaces:**
- Consumes: `ListMembershipsHandler(IMembershipRepository, ITenantRepository, IUserDirectory, IExecutionContext, IClock)`; `ListMembershipsQuery(TenantId TenantId, MembershipViewState? State = null, string? Search = null, string? Role = null)`; `InMemoryTenantRepository(Tenant)` y `FixedClock(DateTimeOffset)` (existentes en `TenantLogoHandlerTestDoubles.cs`); `IMembershipRepository.IsAdvisorCodeTakenAsync` (Task 2).
- Produces: `ApplySearch` con coincidencia exacta por código; dobles `InMemoryMembershipRepository`, `StubUserDirectory`, `RosterReadExecutionContext`.

- [ ] **Step 1: Dobles de prueba**

Crea `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs`:

```csharp
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Dobles de los puertos que usan los handlers del roster. A mano y sin librería de mocking, como
// el resto del repositorio. Lo que un handler del roster no usa lanza, para que una prueba no
// pase por un camino que no creía estar ejerciendo.

internal sealed class InMemoryMembershipRepository(params Membership[] memberships)
    : IMembershipRepository
{
    private readonly List<Membership> _memberships = [.. memberships];

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            _memberships.Where(membership => membership.TenantId == tenantId).ToList());

    public Task<Membership?> FindByIdAsync(
        MembershipId id, TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(
            membership => membership.Id == id && membership.TenantId == tenantId));

    public Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.Any(membership =>
            membership.TenantId == tenantId &&
            membership.AdvisorCode == advisorCode &&
            membership.Id != exceptMembershipId));

    public void Add(Membership membership) => _memberships.Add(membership);

    public Task<Membership?> FindByUserAndTenantAsync(
        Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Membership?> FindByInvitationTokenHashAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ActiveTenantSummary>> ListActiveTenantSummariesByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Membership>> ListByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
        TenantId tenantId, MembershipId excludeId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class StubUserDirectory(IReadOnlyDictionary<Guid, string> emails) : IUserDirectory
{
    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(emails.TryGetValue(userId, out var email) ? email : null);
}

internal sealed class RosterReadExecutionContext(TenantId tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = tenantId;

    public bool HasPermission(string permission) => permission == TenancyPermissions.AdvisorshipRead;
}
```

- [ ] **Step 2: Escribir las pruebas que fallan**

Crea `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListMembershipsHandlerTests.cs`:

```csharp
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// La búsqueda del roster por código de asesor (spec 2026-09-24, D8): exacta sobre el código, no
/// por contenido. Unitaria y no de integración porque los correos que siembra la integración
/// (<c>invitee-{guid}@example.com</c>) tienen dígitos, y <c>?search=1</c> los encontraría por
/// correo. Acá los correos y nombres no tienen ninguno.
/// </summary>
public sealed class ListMembershipsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantUnderTest = TenantId.New();

    // Review Focus 4: "1" no trae a 12, 21 ni 100.
    [Fact]
    public async Task SearchingOneDoesNotMatchTwelveTwentyOneOrOneHundred()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 12),
            ("carlos@example.com", "Carlos Mejía", 21),
            ("diana@example.com", "Diana Ríos", 100));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "1"), TestContext.Current.CancellationToken);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.Counts.Total);
    }

    [Fact]
    public async Task SearchingACodeFindsExactlyThatMember()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 1),
            ("carlos@example.com", "Carlos Mejía", 12),
            ("diana@example.com", "Diana Ríos", null));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "12"), TestContext.Current.CancellationToken);

        var only = Assert.Single(list.Items);
        Assert.Equal("carlos@example.com", only.Email);
        Assert.Equal(12, only.AdvisorCode);
    }

    // D2: se guarda como integer, así que 0012 y 12 son el mismo código.
    [Fact]
    public async Task SearchingACodeWithLeadingZerosFindsTheSameMember()
    {
        var (memberships, emails) = Roster(("carlos@example.com", "Carlos Mejía", 12));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: " 0012 "), TestContext.Current.CancellationToken);

        Assert.Equal(12, Assert.Single(list.Items).AdvisorCode);
    }

    // La búsqueda por código se suma a la de correo y nombre, no la reemplaza.
    [Fact]
    public async Task TextSearchStillMatchesByNameAndEmail()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 12),
            ("carlos@example.com", "Carlos Mejía", null));

        var byName = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "mejía"), TestContext.Current.CancellationToken);
        var byEmail = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "ana@"), TestContext.Current.CancellationToken);

        Assert.Equal("carlos@example.com", Assert.Single(byName.Items).Email);
        Assert.Equal("ana@example.com", Assert.Single(byEmail.Items).Email);
    }

    private static ListMembershipsHandler Handler(
        IReadOnlyList<Membership> memberships,
        IReadOnlyDictionary<Guid, string> emails) =>
        new(
            new InMemoryMembershipRepository([.. memberships]),
            new InMemoryTenantRepository(Tenant.Create(
                TenantUnderTest, "acme", "Acme", "es-CO", "America/Bogota", "yyyy-MM-dd",
                MembershipId.New(), Now)),
            new StubUserDirectory(emails),
            new RosterReadExecutionContext(TenantUnderTest),
            new FixedClock(Now));

    private static (IReadOnlyList<Membership> Memberships, IReadOnlyDictionary<Guid, string> Emails)
        Roster(params (string Email, string DisplayName, int? AdvisorCode)[] people)
    {
        var memberships = new List<Membership>();
        var emails = new Dictionary<Guid, string>();
        foreach (var (email, displayName, advisorCode) in people)
        {
            var userId = Guid.CreateVersion7();
            emails[userId] = email;
            memberships.Add(Membership.Invite(
                MembershipId.New(),
                userId,
                TenantUnderTest,
                displayName,
                ["advisor"],
                "invitation",
                $"token-{userId:N}",
                $"hash-{userId:N}",
                Now.AddHours(-1),
                Membership.DefaultInvitationTimeToLive,
                advisorCode));
        }

        return (memberships, emails);
    }
}
```

El campo se llama `TenantUnderTest` y no `Tenant` a propósito: con ese nombre taparía al tipo `Tenant` del dominio y `Tenant.Create(...)` no compilaría.

- [ ] **Step 3: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.UnitTests.ListMembershipsHandlerTests"
```

Esperado: compila; **fallan** `SearchingACodeFindsExactlyThatMember` y `SearchingACodeWithLeadingZerosFindsTheSameMember` (`Assert.Single() Failure: The collection was empty`). `SearchingOneDoesNotMatchTwelveTwentyOneOrOneHundred` y `TextSearchStillMatchesByNameAndEmail` ya pasan (fijan lo que la implementación no puede romper). Pega la salida.

- [ ] **Step 4: Implementar la coincidencia exacta**

En `src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs`, agrega `using System.Globalization;` al inicio y reemplaza el comentario, `ApplySearch` y `Matches` por:

```csharp
    /// <summary>
    /// La búsqueda es por correo, por nombre o por código de asesor, los datos con los que alguien
    /// identifica a una persona en el roster; el UserId es un GUID que nadie escribe de memoria. Un
    /// campo nulo no coincide con ningún texto, así que una membresía vieja sin nombre se sigue
    /// encontrando por su correo.
    /// </summary>
    /// <remarks>
    /// Correo y nombre coinciden por contenido; el código, exacto (spec 2026-09-24, D8): quien
    /// busca "1" busca al asesor 1, no a 12, 21 y 100. Se compara como número y no como texto
    /// porque el código es un entero (D2): "0012" encuentra a 12, que es el mismo código.
    /// </remarks>
    private static IReadOnlyList<MembershipListItemDto> ApplySearch(
        IReadOnlyList<MembershipListItemDto> items,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return items;
        }

        var term = search.Trim();
        var advisorCode = ParseAdvisorCode(term);
        return items
            .Where(item =>
                Matches(item.Email, term) ||
                Matches(item.DisplayName, term) ||
                (advisorCode is not null && item.AdvisorCode == advisorCode))
            .ToList();
    }

    private static bool Matches(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    // Sólo dígitos: NumberStyles.None rechaza signo, espacios internos y separadores, así que un
    // término que no es un código posible no filtra por código.
    private static int? ParseAdvisorCode(string term) =>
        int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code > 0
            ? code
            : null;
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Tenancy.IntegrationTests.MembershipApiTests"
```

Esperado: `Failed: 0` en las dos (la segunda cubre `ListFiltersBySearchOnEmail` y `SearchFindsAMemberByName`, que no deben cambiar). Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Tenancy/Modules.Tenancy.Application/ListMemberships.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/MembershipHandlerTestDoubles.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/ListMembershipsHandlerTests.cs
git commit -m "feat(tenancy): el roster busca por código de asesor exacto"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 6: `Cod. Asesor` en el Excel de pedidos

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs:3-16`
- Modify: `src/Bootstrapper/QuotationAdvisorLookup.cs:43-47`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (constructor `:19-28`, `Columns` `:39-65`, `BatchContext` `:139-151`, `LoadBatchContextAsync` `:153-205`, `RowsFor` `:207-267`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:133-154`
- Modify: `README.md` (sección "Perfil del miembro" de Task 4)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`

**Interfaces:**
- Consumes: `Membership.AdvisorCode` (Task 1); `IQuotationAdvisorLookup.FindAsync(Guid tenantId, IReadOnlyCollection<Guid> membershipIds, CancellationToken)` (existente, ya registrado en `QepServiceCollectionExtensions.cs:478`); `ExportCell.OfNumber(decimal)`, `ExportCell.OfText(string?)`; `Quotation.AdvisorId : MemberId`; `PUT .../profile` (Task 4, para el punta a punta).
- Produces:
  - `public sealed record QuotationAdvisor(string? Email, string? DisplayName, int? AdvisorCode)`
  - `OrdersExportProcessor(IOrderRepository, IQuotationCustomerLookup, IQuotationProductLookup, IQuotationCompanyLookup, IQuotationGeographyLookup, IQuotationAdvisorLookup, IExportWorkbookWriter, IExportFileStorage, ITenantClock)`
  - `OrdersExportProcessor.Columns` con `new("Cod. Asesor", 14)` al final (índice 22).

- [ ] **Step 1: El doble del lookup**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs`, reemplaza `StubQuotationAdvisorLookup` completo por:

```csharp
/// <summary><paramref name="resolves"/> en false simula la membresía que ya no es del tenant: el
/// adaptador real la deja fuera del diccionario, que no es lo mismo que devolverla sin correo ni
/// nombre. <see cref="Requests"/> guarda los ids de cada llamada, para fijar que se pide por lote
/// y con ids distintos.</summary>
internal sealed class StubQuotationAdvisorLookup(
    string? email = null, string? displayName = null, bool resolves = true, int? advisorCode = null)
    : IQuotationAdvisorLookup
{
    public int FindCalls { get; private set; }

    public List<IReadOnlyCollection<Guid>> Requests { get; } = [];

    public Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        Requests.Add(membershipIds);
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationAdvisor>>(
            membershipIds
                .Where(_ => resolves)
                .Distinct()
                .ToDictionary(id => id, _ => new QuotationAdvisor(email, displayName, advisorCode)));
    }
}
```

`advisorCode` va al final a propósito: los llamados existentes pasan `resolves:` por nombre y los dos primeros por posición, así que ninguno cambia.

- [ ] **Step 2: Escribir las pruebas unitarias que fallan**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`:

**(a)** En `WritesTheErpColumnsInOrder`, reemplaza la lista esperada por:

```csharp
            [
                "EMPRESA", "Forma de pago 1", "V. Consignacion 1", "Cod. Producto", "U.Medida",
                "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
                "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
                "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
                "Cod. Asesor",
            ],
```

**(b)** Agrega estas pruebas justo antes de `private static ExportJob NewJob(`:

```csharp
    // Spec 2026-09-24, D9: la columna nueva va al final, después de Email, para no mover nada de
    // lo que el ERP ya importa.
    [Fact]
    public async Task CodAsesorIsTheLastColumn()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("Cod. Asesor", writer.Columns[^1].Header);
        Assert.Equal(writer.Columns.Count, Assert.Single(writer.Rows).Count);
    }

    // D9: celda numérica, repetida en cada línea del pedido como el resto de sus campos.
    [Fact]
    public async Task CodAsesorCarriesTheAdvisorsCodeOnEveryLine()
    {
        var writer = new RecordingExportWorkbookWriter();
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(
                new StubOrderListRepository(row),
                writer,
                advisors: new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: 12))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(12m, cells[22].Number);
            Assert.Null(cells[22].Text);
        }
    }

    // D9: vacía si la membresía asesora no tiene código.
    [Fact]
    public async Task CodAsesorIsEmptyWhenTheAdvisorHasNoCode()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                advisors: new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: null))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var cell = Assert.Single(writer.Rows)[22];
        Assert.Equal(string.Empty, cell.Text);
        Assert.Null(cell.Number);
    }

    // Review Focus 5: una asesora que el lookup no devuelve (otro tenant, fila borrada) deja la
    // celda vacía, sin romper el lote ni correr las columnas.
    [Fact]
    public async Task CodAsesorIsEmptyWhenTheAdvisorDoesNotResolve()
    {
        var writer = new RecordingExportWorkbookWriter();

        await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001")),
                writer,
                advisors: new StubQuotationAdvisorLookup(resolves: false))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        var row = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, row.Count);
        Assert.Equal(string.Empty, row[22].Text);
        Assert.Null(row[22].Number);
    }

    // D9: una consulta por lote, con las asesoras distintas del lote — no una por pedido ni por
    // línea.
    [Fact]
    public async Task ResolvesTheAdvisorsOncePerBatchWithTheDistinctIds()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
            .ToArray();
        var advisors = new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno", advisorCode: 12);

        await NewProcessor(new StubOrderListRepository(rows), advisors: advisors)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(2, advisors.FindCalls);
        Assert.All(advisors.Requests, request => Assert.Equal([AdvisorId.Value], request));
    }
```

**(c)** Reemplaza `NewProcessor` por:

```csharp
    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        FixedTenantClock? tenantClock = null,
        StubQuotationCustomerLookup? customers = null,
        StubQuotationProductLookup? products = null,
        StubQuotationCompanyLookup? companies = null,
        StubQuotationGeographyLookup? geography = null,
        StubQuotationAdvisorLookup? advisors = null) =>
        new(repository,
            customers ?? new StubQuotationCustomerLookup(DefaultCustomer),
            products ?? new StubQuotationProductLookup(
                new Dictionary<Guid, QuotationProductRef>
                {
                    [ProductId] = DefaultProduct,
                    [OtherProductId] = DefaultProduct with { Id = OtherProductId, Code = "OTR-002" },
                }),
            companies ?? new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            geography ?? new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));
```

- [ ] **Step 3: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportProcessorTests"
```

Esperado: **no compila**. `CS1729: 'QuotationAdvisor' no contiene un constructor que tome 3 argumentos` (en el doble) y `CS1503` en `NewProcessor` (el sexto argumento es un `StubQuotationAdvisorLookup` donde el constructor espera `IExportWorkbookWriter`). Pega la salida.

- [ ] **Step 4: `QuotationAdvisor.AdvisorCode` y el adaptador**

En `src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs`, reemplaza el comentario y el record (`:3-16`) por:

```csharp
/// <summary>Lo que Tenancy e Identity saben de la asesora de una cotización.</summary>
/// <param name="Email">El correo, de Identity. Null si el usuario ya no resuelve (dado de baja
/// en Identity). Es lo que muestran el detalle, el historial y el listado de pedidos.</param>
/// <param name="DisplayName">El nombre que el tenant cargó en la membresía. Null en las
/// membresías anteriores a que existiera y en las creadas con <c>CreateActive</c> —el owner al
/// registrarse y los miembros sembrados— hasta que alguien lo cargue desde el roster.</param>
/// <param name="AdvisorCode">El código con el que el sistema externo del tenant identifica a la
/// asesora (spec 2026-09-24). Es el de hoy, no uno congelado al vender (D9): si se lo cambian, los
/// pedidos viejos salen con el nuevo. Null si la membresía no tiene código.</param>
public sealed record QuotationAdvisor(string? Email, string? DisplayName, int? AdvisorCode)
{
    /// <summary>Cómo presentar a la asesora donde se la muestra por nombre —el listado de
    /// cotizaciones y su Excel—: el nombre o, mientras la membresía no tenga uno, el correo
    /// (spec 2026-09-11, D1, nota del 2026-09-14). Es el mismo respaldo que aplica el PDF sobre
    /// <c>QuotationResponse</c> (D6). Null sólo si no hay ninguno de los dos.</summary>
    public string? Label => DisplayName ?? Email;
}
```

En `src/Bootstrapper/QuotationAdvisorLookup.cs`, reemplaza el comentario del bucle y la construcción:

```csharp
        // El correo sí es una búsqueda por usuario: IUserDirectory sólo resuelve por id único,
        // igual que en ListMembershipsHandler. Acá el conteo es la cantidad de asesoras
        // **distintas** de la página —una o dos en la práctica—, no una por fila. El nombre y el
        // código de asesor salen de la membresía que ya se trajo, sin sumar consultas.
        var advisors = new Dictionary<Guid, QuotationAdvisor>(scoped.Count);
        foreach (var membership in scoped)
        {
            advisors[membership.Id.Value] = new QuotationAdvisor(
                await users.GetEmailAsync(membership.UserId, cancellationToken),
                membership.DisplayName,
                membership.AdvisorCode);
        }
```

- [ ] **Step 5: El procesador**

En `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs`:

**(a)** Constructor:

```csharp
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationGeographyLookup geographyLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    ITenantClock tenantClock)
    : IExportJobProcessor
```

**(b)** En el comentario de `Columns`, agrega antes de `/// </summary>`:

```csharp
    /// "Cod. Asesor" (spec 2026-09-24, D9) va al final a propósito: el ERP lee por encabezado, y
    /// al final no mueve nada de lo que ya importa. Es el nombre por defecto que la homologación de
    /// columnas por tenant (D10, otro spec) podrá renombrar.
```

y agrega después de `new("Email", 30),`:

```csharp
        new("Cod. Asesor", 14),
```

**(c)** `BatchContext` gana las asesoras:

```csharp
    /// <summary>Todo lo que un lote necesita resuelto de una sola vez, para que <see cref="RowsFor"/>
    /// no pida nada por fila: líneas y partes por cotización, comprobantes por pedido, productos y
    /// empresas por id, ciudades de las partes de entrega, la ficha de cada cliente del lote —
    /// la necesitan tanto "Documento" (siempre) como el respaldo de "los mismos datos del cliente"
    /// cuando el pedido no tiene una parte de entrega propia— y la asesora de cada cotización, por
    /// su código.</summary>
    private readonly record struct BatchContext(
        IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationItem>> ItemsByQuotation,
        IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationParty>> PartiesByQuotation,
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>> ProofsByOrder,
        IReadOnlyDictionary<Guid, QuotationProductRef> Products,
        IReadOnlyDictionary<Guid, QuotationCompanyRef> Companies,
        IReadOnlyDictionary<Guid, string> CityNames,
        IReadOnlyDictionary<Guid, QuotationCustomerRef> Customers,
        IReadOnlyDictionary<Guid, QuotationAdvisor> Advisors);
```

**(d)** En `LoadBatchContextAsync`, reemplaza desde `// Todos los clientes del lote, no sólo los que necesitan el respaldo: "Documento" los` hasta el `return` inclusive por:

```csharp
        // Todos los clientes del lote, no sólo los que necesitan el respaldo: "Documento" los
        // necesita a todos.
        var clientIds = batch.Select(row => row.Quotation.ClientId).Distinct().ToArray();
        var customers = await customerLookup.FindManyAsync(tenantId, clientIds, cancellationToken);

        // "Cod. Asesor" (D9): Quotation.AdvisorId → membresía → código de hoy, en una consulta
        // por lote con las asesoras distintas, que en un lote son una o dos.
        var advisorIds = batch.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray();
        var advisors = await advisorLookup.FindAsync(tenantId, advisorIds, cancellationToken);

        return new BatchContext(
            itemsByQuotation, partiesByQuotation, proofsByOrder, products, companies, cityNames, customers,
            advisors);
```

**(e)** En `RowsFor`, agrega después de `var observaciones = quotation.Notes ?? string.Empty;`:

```csharp
        var codAsesor = AdvisorCodeCell(quotation, context.Advisors);
```

y agrega después de `ExportCell.OfText(email),` (dentro de la fila):

```csharp
                codAsesor,
```

**(f)** Agrega este método justo antes de `private static ExportCell PaymentDateCell(`:

```csharp
    // "Cod. Asesor" (D9): numérica, para que el ERP la lea como el número que es. Vacía si la
    // membresía no tiene código o si el lookup no la devuelve —otro tenant, una fila que ya no
    // está—: un pedido sin código no es una razón para frenar el archivo.
    private static ExportCell AdvisorCodeCell(
        Quotation quotation, IReadOnlyDictionary<Guid, QuotationAdvisor> advisors) =>
        advisors.TryGetValue(quotation.AdvisorId.Value, out var advisor)
            && advisor.AdvisorCode is { } code
                ? ExportCell.OfNumber(code)
                : ExportCell.OfText(string.Empty);
```

- [ ] **Step 6: Correr y ver el GREEN unitario**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
```

Esperado: `Failed: 0` en todo el proyecto (incluye `ListOrdersHandlerTests`, `ListQuotationsHandlerTests` y `QuotationsExportProcessorTests`, que usan el doble). Pega la salida.

- [ ] **Step 7: El punta a punta**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`:

**(a)** En `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail`, reemplaza la lista del encabezado por:

```csharp
            [
                "EMPRESA", "Forma de pago 1", "V. Consignacion 1", "Cod. Producto", "U.Medida",
                "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
                "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
                "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
                "Cod. Asesor",
            ],
```

y agrega después de `Assert.Equal("compras@verde.co", first[21]);`:

```csharp
        // El owner nace sin código (CreateActive): la celda sale vacía.
        Assert.Equal(string.Empty, first[22]);
```

**(b)** Agrega esta prueba justo antes de `private static string Iso(DateOnly date) =>`:

```csharp
    // Spec 2026-09-24, D9, de punta a punta: el código se carga por PUT .../profile en Tenancy, el
    // adaptador de Bootstrapper lo resuelve desde la membresía, y sale como número en la última
    // columna. Ninguna prueba unitaria ve ese cruce.
    [Fact]
    public async Task TheOrdersWorkbookCarriesTheAdvisorsCodeInTheLastColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // X-Permissions reemplaza el set por defecto del stub, así que los de Tenancy para leer el
        // roster y editar el perfil se piden explícitos.
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "advisorship.read", "advisorship.manage"]);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);
        await SetOwnerAdvisorCodeAsync(client, tenantId, 7);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        Assert.Equal("Cod. Asesor", sheet.Rows[0][^1]);
        Assert.Equal("7", sheet.Rows[1][22]);
        Assert.True(sheet.NumericCells[1][22]);
    }

    // La asesora de CreateOrderAsync es el owner, la única membresía del tenant recién registrado.
    // La versión se lee del roster en vez de suponerla: If-Match tiene que llevar la vigente.
    private static async Task SetOwnerAdvisorCodeAsync(HttpClient client, Guid tenantId, int advisorCode)
    {
        var roster = await client.GetFromJsonAsync<MembershipRosterPayload>(
            $"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        var owner = Assert.Single(roster!.Items, item => item.IsOwner);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{owner.Id}/profile")
        {
            Content = JsonContent.Create(new { displayName = "Laura Gómez", advisorCode })
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{owner.Version}\"");
        using var updated = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }
```

**(c)** Agrega, junto a los otros records privados del final de la clase:

```csharp
    private sealed record MembershipRosterPayload(IReadOnlyList<MembershipRosterRowPayload> Items);

    private sealed record MembershipRosterRowPayload(Guid Id, long Version, bool IsOwner);
```

- [ ] **Step 8: README — la columna del Excel**

En `README.md`, al final de la sección "Perfil del miembro: nombre y código de asesor" (Task 4), agrega:

```markdown
El código de asesor llega al sistema externo por el Excel de pedidos: la columna `Cod. Asesor`,
la última, numérica y repetida en cada línea del pedido. Se resuelve al exportar desde la
membresía asesora de la cotización con el código **de hoy** —si se lo cambian, los pedidos viejos
salen con el nuevo— y queda vacía si la membresía no tiene código.
```

- [ ] **Step 9: Correr y ver el GREEN de integración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrderExportApiTests|FullyQualifiedName~Modules.Quotations.IntegrationTests.QuotationAdvisorNameApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `Failed: 0` en las dos. Pega la salida.

- [ ] **Step 10: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Application/IQuotationAdvisorLookup.cs src/Bootstrapper/QuotationAdvisorLookup.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs README.md tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs
git commit -m "feat(quotations): Cod. Asesor en el Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 7: Verificación final contra el baseline

**Files:**
- Ninguno, salvo lo que la verificación obligue a corregir (commit `fix(...)` aparte, con su propio RED/GREEN).

**Interfaces:**
- Consumes: `$env:TEMP\qep-codigo-asesor-baseline-failed.txt` (Task 0).
- Produces: evidencia de build limpio, suite completa sin regresiones por nombre, y barrido de cuerpos crudos.

- [ ] **Step 1: Barrido de cuerpos crudos** (gotcha del `CLAUDE.md`: "al agregar un campo requerido o una precondición, hay que barrer las pruebas de integración por cuerpos crudos")

El campo es opcional, así que ningún cuerpo existente debería romper; se verifica igual:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-ChildItem tests -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '/memberships"|/memberships\?|/display-name|/profile"|MembershipInviteRequest|MembershipDisplayNameUpdateRequest' |
    ForEach-Object { "{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim() }
```

Esperado: los posteos de invitación en `MembershipApiTests`, `MembershipLifecycleApiTests`, `InvitationApiTests`, `AuthSessionApiTests`, `RealAuthenticationApiTests`, `InvitationNotificationTests`, `AuditRecordingTests` y `OrphanUserCleanupTests`; los `display-name` de `MembershipLifecycleApiTests` y `QuotationAdvisorNameApiTests`; los `profile` nuevos. Para cada uno confirma que **no** manda `advisorCode` con un valor que ahora se rechace (`0`, negativo, decimal) ni deserializa la respuesta en un record posicional que dependa del orden de las propiedades. Anota la lista en el handoff.

- [ ] **Step 2: Build y suite completa, por nombre**

Con Docker corriendo y la API detenida:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore --locked-mode
dotnet build --no-restore
$final = Join-Path $env:TEMP "qep-codigo-asesor-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $final
Get-ChildItem $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-codigo-asesor-final-failed.txt")
Compare-Object `
    (Get-Content (Join-Path $env:TEMP "qep-codigo-asesor-baseline-failed.txt")) `
    (Get-Content (Join-Path $env:TEMP "qep-codigo-asesor-final-failed.txt"))
```

Esperado: `dotnet restore --locked-mode` sin `NU1004` (este plan no toca `Directory.Packages.props`); build con `0 Errores`; `Compare-Object` **sin filas `=>`** (ninguna prueba falla ahora que no fallara antes). Una fila `<=` es una prueba que se arregló sola: se anota, no bloquea. Pega la salida.

Si aparece una fila `=>`: se diagnostica con `superpowers:systematic-debugging`, se escribe la prueba que la reproduce si no existe, se corrige y se commitea aparte:

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/codigo-de-asesor") { throw "Rama inesperada: '$branch'. No se commitea." }
git add <rutas explícitas del arreglo>
git commit -m "fix(<modulo>): <qué se arregló>"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

- [ ] **Step 3: Cierre**

```powershell
git status --short
git log --oneline 755f608..HEAD
```

Esperado: árbol limpio; los commits de la tabla de Entrega en orden. Entrega en el handoff: las salidas RED/GREEN de cada tarea, el barrido del Step 1, el `Compare-Object` del Step 2, y la ambigüedad del spec que queda abierta (hallazgo 6). La rama no se pushea desde este plan.
