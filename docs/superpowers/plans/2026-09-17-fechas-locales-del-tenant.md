# Fechas en el huso del tenant — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Todo instante que se corta en días (consecutivos, vigencias, vencimientos, filtros, reportes, series mensuales, PDF, Excel y correos) usa el día local del tenant (`Tenant.TimeZone`) en vez del día UTC, sin cambiar cómo se guardan los instantes ni la forma de la API.

**Architecture:** Un tipo puro `TenantCalendar` y un puerto `ITenantClock` en `Modules.Tenancy.Application`; la implementación `TenantClock` vive en `Modules.Tenancy.Infrastructure` y combina `IClock`, `ITenantDirectory.GetTimeZoneAsync` y `TimeZoneInfo.FindSystemTimeZoneById`. Los handlers y procesadores piden un calendario por tenant y le pasan **instantes** a repositorios y orígenes de reporte; nadie en Infrastructure decide husos. La serie mensual de los reportes agrupa en el huso del tenant, en SQL si Npgsql lo traduce y en memoria si no (decisión por spike en Task 7).

**Tech Stack:** .NET 10, EF Core 10.0.11 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, FluentValidation, ClosedXML 0.105.1, DocumentFormat.OpenXml 3.1.1, Typst (`quotation.typ`), xUnit v3 3.2.2, Testcontainers 4.14.0 (`postgres:18-alpine`).

**Spec:** docs/superpowers/specs/2026-09-17-fechas-locales-del-tenant-design.md

## Global Constraints

- El día de negocio es el día local del tenant; los instantes se siguen guardando en UTC y `IClock` no cambia (decisiones 1 y 2).
- No hay default a UTC: si el tenant no existe, `TenantClock` lanza `ResourceNotFoundException("tenancy.tenant.not_found", …)` (decisión 3).
- Sin backfill ni migraciones: lo ya guardado queda como está (decisión 4).
- `qep-frontend` no se toca: ningún DTO ni contrato HTTP cambia de forma.
- `ITenantClock` se registra scoped y resuelve el huso una vez por tenant por scope; los procesos que recorren varios tenants piden un calendario por tenant, nunca uno por fila.
- `IQuotationNumberGenerator` e `IOrderNumberGenerator` no cambian de firma (2a/2b).
- Ninguna regla de `tests/ArchitectureTests/` cambia, y ningún módulo gana referencias nuevas.
- Todo se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant`, rama `feature/fechas-locales-del-tenant`, creada desde `develop`. Cada bloque de comandos empieza con `Set-Location` a ese worktree: el checkout principal tiene cambios sin commitear que no son de este trabajo.
- Los comandos van en Windows PowerShell 5.1: sin `&&`, con `A; if ($?) { B }`, y `$env:VAR = "…"` en línea aparte.
- `Api.exe` corriendo bloquea `build` y `test`: antes de cada corrida, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las pruebas de integración necesitan Docker corriendo (Testcontainers); se corren en primer plano, nunca en background.
- Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- Toda factoría de integración fija su configuración con `UseSetting`; nunca hereda los user-secrets de quien corre las pruebas. El reloj fijo se inyecta sólo en las pruebas que lo piden.
- `Directory.Packages.props` no cambia; si por algún motivo cambiara, los `packages.lock.json` se regeneran con `dotnet restore --force-evaluate` y se commitean en el mismo commit.
- TDD estricto: RED antes que GREEN, y pegas en el handoff la salida literal de las dos corridas.
- Commits: Conventional Commits en español, en minúscula (`fix(quotations): …`). **Nunca** `Co-Authored-By` ni otra atribución de IA, aunque una herramienta lo sugiera. Cada commit va con el guard de rama y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`.
- Idioma: la prosa y los comentarios de código en español de Colombia, tuteando; identificadores, códigos de error y mensajes de excepción en inglés, como el resto del repo.
- Archivos nuevos en UTF-8 sin BOM y con LF (`.editorconfig`). `dotnet format` reporta `ENDOFLINE` y `CHARSET` en archivos viejos por `core.autocrlf=true`: ese ruido se filtra; cualquier otro diagnóstico en una línea que tocaste se corrige.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: toda conversión a texto lleva `CultureInfo.InvariantCulture`, y un `using` o una clase de prueba que queda sin uso se borra en la misma tarea.

---

## Hallazgos contra el código (2026-09-17)

1. **Npgsql 10.0.3 no expone `EF.Functions.AtTimeZone`.** La documentación XML del paquete (`Npgsql.EntityFrameworkCore.PostgreSQL.xml`) sólo trae `AtTimeZoneExpression` interno; lo que el proveedor traduce a `AT TIME ZONE` es `TimeZoneInfo.ConvertTimeBySystemTimeZoneId` (la cadena aparece en el ensamblado). La serie mensual (punto 5) se prueba con esa forma en el spike de Task 7, en dos variantes: sobre la columna `DateTimeOffset` y sobre `.UtcDateTime`.
2. **Los cuatro orígenes de reporte viven en `src/Bootstrapper/`** (`OrdersReportSource.cs`, `QuotationsReportSource.cs`, `CustomerReportSource.cs`, `PriceChangeReportSource.cs`), igual que `ReportDateRange` (`ReportingLookups.cs:146-153`). Los criterios (`OrdersReportCriteria` y hermanos) están en `Modules.Reporting.Application/ReportingFilters.cs` y llevan `DateOnly? From/To` que hoy leen los orígenes.
3. **Ninguna factoría de integración de Quotations, Reporting ni Catalog permite fijar el reloj.** `IClock` se registra scoped (`src/Bootstrapper/QepServiceCollectionExtensions.cs:52`). Task 3 suma `utcNow` a `QuotationsApiHarness.QepApiFactory`, Task 6 a `ReportingApiHarness.QepApiFactory` y Task 11 a la factoría privada de `ProductExportApiTests`. `CustomersApiHarness.QepApiFactory` ya acepta `configureServices`, y por ahí se inyecta.
4. **`ProductExportApiTests` usa el tenant `01900000-0000-7000-8000-000000000081`, que no existe en `tenancy.tenants`.** En cuanto `ExportProductsHandler` pide el calendario, ese export responde 404. Task 11 siembra la fila del tenant en la prueba.
5. **`QuotationExportApiTests.cs:224` y `OrderExportApiTests.cs:172` parsean la celda «Fecha» con `DateTimeOffset.Parse`.** El spec no las lista, pero con `yyyy-MM-dd HH:mm` sin offset dejan de comparar el instante. Task 10 las reescribe.
6. **`QuotationExportApiTests.cs:289-301` y `OrderExportApiTests.cs:226-240` llaman al repositorio con `DateOnly`.** Cambian a instantes en Task 5.
7. **`ExportProducts.cs` no escribe ninguna fecha en celdas**: sólo el nombre del archivo (`:165-168`). El export de productos no tiene pruebas unitarias; se cubre por integración.
8. **Las tres plantillas de correo no tienen el tenant**, pero sus tres workers sí: `tenantId` viene en el payload (`CustomerExportDeliveryWorker`, `ProductExportDeliveryWorker`, `QuotationsExportReadyDeliveryWorker`). El calendario se resuelve en el worker, después de comprobar el destinatario, y el huso viaja a la plantilla.
9. **`ExportLoadSeeder` numera por año UTC y calcula `valid_until` con la fecha UTC en SQL** (`LoadQuotationsSql`: `extract(year FROM … AT TIME ZONE 'UTC')` y `(… AT TIME ZONE 'UTC')::date + 15`). El punto 8c sólo pide el `today`; la numeración de la carga sintética queda en UTC y se reporta como decisión pendiente del owner.
10. **Con el reloj fijo en `2027-01-01T04:00Z` la carga sintética produce una cotización que vence justo el 2026-12-31** (la `n = 53`: `d = 15`, `m = 521`, creada `2026-12-16T19:19Z`). Esa fila es la que pone en rojo la prueba de 8c.
11. **El checkout principal no está limpio** (`src/Bootstrapper/QepServiceCollectionExtensions.cs` y `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthorizationCatalogApiTests.cs` modificados) y `develop` avanza durante la sesión. Por eso se trabaja en un worktree, y este plan no toca `QepServiceCollectionExtensions.cs`.
12. **`GetQuotationsReportSummaryHandler` es el único handler de Reporting con `IClock`**; al pasar a `ITenantClock`, `FixedClock` de `ReportingTestDoubles.cs:20-23` queda sin uso y se borra (Task 6).
13. **El doble `StubQuotationListRepository` y `StubOrderListRepository` anotan los filtros en `RecordedExportSearch` / `RecordedOrderExportSearch`** (`QuotationsTestDoubles.cs:293-300`, `:526-534`): cambian a instantes con el repositorio.

**Decisiones de este plan donde el spec deja margen:**

- **`TenantClock` memoriza el huso por tenant dentro del scope** (un `Dictionary<Guid, TimeZoneInfo>`), así «una resolución por request» vale aunque dos componentes del mismo request pidan el calendario. `UtcNow` se lee de `IClock` en cada llamada.
- **Los listados sólo piden el calendario si llega alguna fecha** (`TenantDayRange.ResolveAsync`): un listado sin rango no paga esa consulta. Exportar y procesar el export siempre lo piden, porque el rango es obligatorio.
- **Los criterios de reporte cambian `DateOnly? From, DateOnly? To` por `ReportPeriod Period`** (`Start`, `EndExclusive`, `TimeZone`). La ventana anterior se sigue calculando sobre los `DateOnly` del filtro con `ReportComparisonWindow.Preceding` y se convierte con el mismo calendario.
- **`QuotationPdfDocument.CreatedAt` (`DateTimeOffset`) pasa a `IssuedOn` (`DateOnly`)**: el JSON lleva `issuedOn: "2026-12-31"` y la plantilla deja de cortar en la `T`. Es contrato interno con `qcode-pdf`, no con el frontend.
- **`TenantCalendar.StartOfDayUtc` también resuelve una medianoche ambigua** (vuelta atrás del horario de verano a las 01:00): toma su primera ocurrencia. El spec sólo nombra el hueco; la ambigüedad es el mismo problema del otro lado.
- **Las plantillas de correo reciben `TimeZoneInfo`** y formatean ellas; el worker no le pasa una fecha ya convertida, para que la prueba de la plantilla cubra la conversión.

## Entrega

| Commit | Tarea |
| --- | --- |
| `docs(tenancy): plan de fechas en el huso del tenant` | 0 |
| `feat(tenancy): calendario del tenant` | 1 |
| `feat(tenancy): reloj del tenant` | 2 |
| `fix(quotations): consecutivo y vigencia por defecto en el día del tenant` | 3 |
| `fix(quotations): vencer cotizaciones con el hoy de cada tenant` | 4 |
| `fix(quotations): filtrar listados y exports por el día del tenant` | 5 |
| `fix(reporting): cortar los rangos de los reportes en el día del tenant` | 6 |
| `fix(reporting): series mensuales en el mes del tenant` | 7 |
| `fix(reporting): vencidas y por vencer con el hoy del tenant` | 8 |
| `fix(quotations): fecha de emisión del PDF en el día del tenant` | 9 |
| `fix(quotations): fechas y nombres de los excel en la hora del tenant` | 10 |
| `fix(customers): fechas y nombres de los excel de clientes y productos en la hora del tenant` | 11 |
| `fix(notifications): vencimiento de los enlaces de exportación en la hora del tenant` | 12 |
| `fix(bootstrapper): la carga sintética vence con el hoy del tenant` | 13 |
| sólo si la verificación final pide cambios | 14 |

La rama no se publica ni se mergea desde este plan.

---

### Task 0: Worktree, rama y baseline

**Files:**
- Ninguno de código. Copia y commitea este plan en la rama.

**Interfaces:**
- Consumes: nada.
- Produces: el worktree `qep-backend-worktrees\fechas-locales-del-tenant` en `feature/fechas-locales-del-tenant`, y `$env:TEMP\qep-fechas-baseline-failed.txt` con las pruebas que ya fallan, por nombre. Es la referencia de Task 14.

- [ ] **Step 1: Crear el worktree desde `develop`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git fetch origin
git branch --show-current
git log -1 --oneline develop
git worktree add -b feature/fechas-locales-del-tenant ..\qep-backend-worktrees\fechas-locales-del-tenant develop
Copy-Item docs\superpowers\plans\2026-09-17-fechas-locales-del-tenant.md ..\qep-backend-worktrees\fechas-locales-del-tenant\docs\superpowers\plans\
```

Esperado: `Preparing worktree (new branch 'feature/fechas-locales-del-tenant')`. Si `develop` local está detrás de `origin/develop`, **para y pregunta** antes de crear la rama.

- [ ] **Step 2: Comprobar herramientas y que el spec está en la base**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
git branch --show-current
git status --short
Test-Path docs\superpowers\specs\2026-09-17-fechas-locales-del-tenant-design.md
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
```

Esperado: rama `feature/fechas-locales-del-tenant`; `git status` sólo con `?? docs/superpowers/plans/2026-09-17-fechas-locales-del-tenant.md`; `True`; `Get-Process` sin salida; `docker info` con una versión.

- [ ] **Step 3: Restore y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`.

- [ ] **Step 4: Baseline de la suite completa, por nombre**

Tarda decenas de minutos; corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
$baseline = Join-Path $env:TEMP "qep-fechas-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $baseline
Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-fechas-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-fechas-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff.

- [ ] **Step 5: Commitear el plan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add docs/superpowers/plans/2026-09-17-fechas-locales-del-tenant.md; git commit -m "docs(tenancy): plan de fechas en el huso del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit creado y el `Select-String` sin salida.

---

### Task 1: `TenantCalendar`

El tipo puro que corta instantes en días del tenant. Sin DI ni base.

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/TenantCalendar.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantCalendarTests.cs` (nuevo)

**Interfaces:**
- Consumes: nada.
- Produces (namespace `Modules.Tenancy.Application`):

```csharp
public sealed class TenantCalendar
{
    public TenantCalendar(DateTimeOffset utcNow, TimeZoneInfo timeZone);
    public DateTimeOffset UtcNow { get; }
    public TimeZoneInfo TimeZone { get; }
    public DateOnly Today { get; }
    public DateTimeOffset ToLocal(DateTimeOffset instant);
    public DateTimeOffset StartOfDayUtc(DateOnly date);
    public DateTimeOffset EndOfDayExclusiveUtc(DateOnly date);
}
```

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantCalendarTests.cs`:

```csharp
using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// El día de negocio es el día local del tenant (spec 2026-09-17, decisión 1). Todas las fronteras
/// salen del mismo instante de ejemplo: el 31 de diciembre de 2026 a las 23:00 en Bogotá, que en
/// UTC ya es 2027.
/// </summary>
public sealed class TenantCalendarTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TodayIsTheLocalDateOnNewYearsEve()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        Assert.Equal(new DateOnly(2026, 12, 31), calendar.Today);
    }

    // 00:30 UTC son las 19:30 del día anterior en Bogotá: la frontera diaria que hoy mueve todo.
    [Fact]
    public void TodayStaysOnTheLocalDayAfterSevenInTheEvening()
    {
        var calendar = new TenantCalendar(new DateTimeOffset(2026, 9, 17, 0, 30, 0, TimeSpan.Zero), Bogota);

        Assert.Equal(new DateOnly(2026, 9, 16), calendar.Today);
    }

    // Llegue con el offset que llegue, UtcNow es el mismo instante expresado en UTC.
    [Fact]
    public void UtcNowIsTheSameInstantWithAZeroOffset()
    {
        var calendar = new TenantCalendar(
            new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5)), Bogota);

        Assert.Equal(NewYearsEveInBogota, calendar.UtcNow);
        Assert.Equal(TimeSpan.Zero, calendar.UtcNow.Offset);
        Assert.Same(Bogota, calendar.TimeZone);
    }

    [Fact]
    public void ToLocalShowsTheInstantOnTheTenantsWallClock()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        var local = calendar.ToLocal(NewYearsEveInBogota);

        Assert.Equal(new DateTime(2026, 12, 31, 23, 0, 0), local.DateTime);
        Assert.Equal(TimeSpan.FromHours(-5), local.Offset);
    }

    [Fact]
    public void StartOfDayIsTheLocalMidnightAsAnUtcInstant()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        var start = calendar.StartOfDayUtc(new DateOnly(2026, 12, 31));

        Assert.Equal(new DateTimeOffset(2026, 12, 31, 5, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void EndOfDayExclusiveIsTheNextLocalMidnight()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero),
            calendar.EndOfDayExclusiveUtc(new DateOnly(2026, 12, 31)));
    }

    // Nueva York adelanta el reloj a las 02:00 del 8 de marzo de 2026: ese día dura 23 horas, y el
    // tipo no puede suponer que todos duran 24 aunque Colombia no tenga horario de verano.
    [Fact]
    public void ADayThatSpringsForwardLastsTwentyThreeHours()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var day = new DateOnly(2026, 3, 8);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), calendar.StartOfDayUtc(day));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), calendar.EndOfDayExclusiveUtc(day));
    }

    // Chile adelanta el reloj a la medianoche del 6 de septiembre de 2026: las 00:00 locales no
    // existen, y el día empieza en el primer instante válido, las 01:00 (-03:00).
    [Fact]
    public void AMidnightInsideTheSpringForwardGapStartsAtTheFirstValidInstant()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero),
            calendar.StartOfDayUtc(new DateOnly(2026, 9, 6)));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero),
            calendar.EndOfDayExclusiveUtc(new DateOnly(2026, 9, 5)));
    }

    // Azores atrasa el reloj de 01:00 a 00:00 el 25 de octubre de 2026: la medianoche ocurre dos
    // veces, y el día empieza en la primera (offset 0), no en la segunda (-01:00).
    [Fact]
    public void AnAmbiguousMidnightStartsAtItsFirstOccurrence()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Azores"));

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
            calendar.StartOfDayUtc(new DateOnly(2026, 10, 25)));
    }
}
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~TenantCalendarTests"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'TenantCalendar' could not be found`.

- [ ] **Step 3: Implementar `TenantCalendar`**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/TenantCalendar.cs`:

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// Corta instantes en días del tenant (spec 2026-09-17). Los instantes se siguen guardando en UTC;
/// lo que cambia es cómo se cortan en días y cómo se muestran. Puro e inmutable: se construye con el
/// instante actual y el huso, y se prueba sin base ni DI. Lo arma <see cref="ITenantClock"/>.
/// </summary>
public sealed class TenantCalendar
{
    public TenantCalendar(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        UtcNow = utcNow.ToUniversalTime();
        TimeZone = timeZone;
        Today = DateOnly.FromDateTime(ToLocal(UtcNow).DateTime);
    }

    public DateTimeOffset UtcNow { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>La fecha local de <see cref="UtcNow"/>: el "hoy" del negocio.</summary>
    public DateOnly Today { get; }

    /// <summary>El mismo instante en la hora del tenant, para mostrarlo.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>
    /// Las 00:00 locales de <paramref name="date"/> como instante. Si caen en el hueco del horario de
    /// verano, el día empieza en el primer instante válido; si ocurren dos veces, en la primera. El
    /// tipo no supone que el huso no tiene horario de verano, aunque Colombia no lo tenga.
    /// </summary>
    public DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Los huecos son de minutos enteros: avanzar de a uno llega al primer instante válido en, a lo
        // sumo, la duración del hueco.
        while (TimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (TimeZone.IsAmbiguousTime(local))
        {
            // El offset mayor es el de la primera ocurrencia: UTC = local - offset.
            var firstOffset = TimeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, firstOffset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone), TimeSpan.Zero);
    }

    /// <summary>Las 00:00 locales del día siguiente: el límite superior exclusivo de
    /// <paramref name="date"/>. "Hasta el 31" incluye todo el 31 del tenant.</summary>
    public DateTimeOffset EndOfDayExclusiveUtc(DateOnly date) => StartOfDayUtc(date.AddDays(1));
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests/Modules.Tenancy.UnitTests.csproj --filter "FullyQualifiedName~TenantCalendarTests"
```

Esperado: `Correctas: 9` (o `Passed: 9`), `Con errores: 0`. Si fallan sólo las de Santiago o Azores, los datos de husos de esta máquina no tienen la regla de 2026: **para y pregunta** antes de cambiar la fecha de la prueba; no cambies la implementación para que pase.

- [ ] **Step 5: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Tenancy/Modules.Tenancy.Application/TenantCalendar.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantCalendarTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`.

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Tenancy/Modules.Tenancy.Application/TenantCalendar.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantCalendarTests.cs; git commit -m "feat(tenancy): calendario del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 2: `ITenantClock` y `TenantClock`

El puerto, su implementación en Infrastructure y el registro scoped. `TenantClock` es `internal` y Tenancy.Infrastructure no tiene `InternalsVisibleTo`: se prueba resuelto del host real, en Tenancy.IntegrationTests.

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantClock.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantClock.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs:29`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantClockTests.cs` (nuevo)

**Interfaces:**
- Consumes: `TenantCalendar` (Task 1), `IClock` (`BuildingBlocks.Application`), `ITenantDirectory.GetTimeZoneAsync(TenantId, CancellationToken)`, `ResourceNotFoundException(string code, string message)`.
- Produces:

```csharp
namespace Modules.Tenancy.Application;
public interface ITenantClock
{
    Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

`internal sealed class TenantClock(IClock clock, ITenantDirectory directory) : ITenantClock` en `namespace Modules.Tenancy.Infrastructure`, registrado con `services.AddScoped<ITenantClock, TenantClock>()`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantClockTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El reloj del tenant resuelto del host real (spec 2026-09-17): <c>TenantClock</c> es internal, y
/// lo que importa verificar es el cableado —huso desde la base, instante desde <see cref="IClock"/>—
/// y que no haya default a UTC cuando el tenant no existe (decisión 3).
/// </summary>
public sealed class TenantClockTests
{
    // El tenant que TenancyDatabaseInitializer siembra en Development, con America/Bogota.
    private static readonly Guid DevelopmentTenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnknownTenantHasNoCalendar()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var tenantClock = scope.ServiceProvider.GetRequiredService<ITenantClock>();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            tenantClock.GetAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task TheCalendarTakesTheTenantsTimeZoneAndTheInjectedClock()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        await using var scope = factory.Services.CreateAsyncScope();
        var tenantClock = scope.ServiceProvider.GetRequiredService<ITenantClock>();

        var calendar = await tenantClock.GetAsync(DevelopmentTenantId, TestContext.Current.CancellationToken);

        Assert.Equal("America/Bogota", calendar.TimeZone.Id);
        Assert.Equal(NewYearsEveInBogota, calendar.UtcNow);
        Assert.Equal(new DateOnly(2026, 12, 31), calendar.Today);
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class QepApiFactory(string connectionString, DateTimeOffset? utcNow = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: mismo criterio que TenantSettingsApiTests (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            if (utcNow is { } fixedNow)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                });
            }
        }
    }
}
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~TenantClockTests"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'ITenantClock' could not be found`.

- [ ] **Step 3: El puerto**

Crea `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantClock.cs`:

```csharp
namespace Modules.Tenancy.Application;

/// <summary>
/// El calendario de un tenant: el instante de ahora y su huso (spec 2026-09-17). Vive junto a
/// <see cref="IExecutionContext"/> e <see cref="ITenantDirectory"/> porque todos los módulos que
/// cortan instantes en días ya referencian esta capa.
///
/// No hay default a UTC: un tenant que no existe es <c>tenancy.tenant.not_found</c>, porque un
/// default silencioso es justo el defecto que este puerto quita (decisión 3). Quien recorre varios
/// tenants pide un calendario por tenant, no uno por fila.
/// </summary>
public interface ITenantClock
{
    Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: La implementación**

Crea `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantClock.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Combina <see cref="IClock"/>, el huso guardado del tenant y <see cref="TimeZoneInfo"/>. Scoped:
/// el huso se lee una vez por tenant por scope, así un request que corta varias fechas —o un proceso
/// que recorre tenants— no repite la consulta. El instante se lee de <see cref="IClock"/> en cada
/// llamada. <c>Tenant.TimeZone</c> ya viene validado como ID IANA (<c>Tenant.ValidateTimeZone</c>).
/// </summary>
internal sealed class TenantClock(IClock clock, ITenantDirectory directory) : ITenantClock
{
    private readonly Dictionary<Guid, TimeZoneInfo> _timeZones = [];

    public async Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!_timeZones.TryGetValue(tenantId, out var timeZone))
        {
            var timeZoneId = await directory.GetTimeZoneAsync(new TenantId(tenantId), cancellationToken)
                ?? throw new ResourceNotFoundException(
                    "tenancy.tenant.not_found",
                    "Tenant was not found.");
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            _timeZones[tenantId] = timeZone;
        }

        return new TenantCalendar(clock.UtcNow, timeZone);
    }
}
```

- [ ] **Step 5: El registro**

En `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<ITenantDirectory, TenantDirectory>();
```

por:

```csharp
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        // Spec 2026-09-17: el día de negocio es el del tenant. Scoped para memorizar el huso por
        // request (TenantClock).
        services.AddScoped<ITenantClock, TenantClock>();
```

- [ ] **Step 6: Correr y ver el GREEN, más las pruebas de arquitectura**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/Modules.Tenancy.IntegrationTests.csproj --filter "FullyQualifiedName~TenantClockTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `TenantClockTests` con 2 correctas y 0 errores; `ArchitectureTests` en verde sin cambios de regla (`TenancyLayerTests` no necesita nada: Infrastructure ya referencia Application).

- [ ] **Step 7: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Tenancy/Modules.Tenancy.Application/ITenantClock.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantClock.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantClockTests.cs
```

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Tenancy/Modules.Tenancy.Application/ITenantClock.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenantClock.cs src/Modules/Tenancy/Modules.Tenancy.Infrastructure/TenancyInfrastructureExtensions.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantClockTests.cs; git commit -m "feat(tenancy): reloj del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 3: Consecutivo y vigencia por defecto en el día del tenant (2a, 2b, 2c)

El año de `QUO-`/`PED-` y la vigencia por defecto salen de `calendar.Today`. Las tres pruebas que armaban el número con `DateTime.UtcNow.Year` pasan a reloj fijo: además de fijar el comportamiento viejo, eran intermitentes cada fin de año. Para eso la factoría de Quotations aprende a fijar el reloj.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs:28-38,63-65,73-76,106-109`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:48-61,92-94`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:1-21` (usings), `:45-47` (constante), `:674-781` (`QepApiFactory`), `:900-915` (doble `FixedClock` al final de la clase)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs:10-30`, `OrderApiTests.cs:60-89`, `OrderListApiTests.cs:20-38`

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar` (Tasks 1-2).
- Produces:
  - `CreateQuotationHandler(IQuotationRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationCustomerLookup, IQuotationCompanyLookup, IQuotationNumberGenerator, IMembershipDirectory, IExecutionContext, ITenantClock tenantClock, IValidator<CreateQuotationCommand>)` — `IClock` sale de la firma.
  - `ConvertQuotationToOrderHandler(IQuotationRepository, IOrderRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationCustomerLookup, IQuotationFileLookup, IPaymentProofPublisher, IOrderPaymentProofEventPublisher, IOrderNumberGenerator, IMembershipDirectory, IExecutionContext, ITenantClock tenantClock, IValidator<ConvertQuotationToOrderCommand>)` — `IClock` sale de la firma.
  - En `QuotationsApiHarness`: `public static readonly DateTimeOffset NewYearsEveInBogota`, `public sealed class FixedClock(DateTimeOffset utcNow) : IClock` y `QepApiFactory(string connectionString, bool runExportWorker = false, bool publicPaymentProofLinks = false, DateTimeOffset? utcNow = null)`. Tasks 4, 5, 10 y 13 los usan.

- [ ] **Step 1: El reloj fijo en la factoría de Quotations**

En `QuotationsApiHarness.cs`, reemplaza:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
```

por:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
```

Reemplaza:

```csharp
internal static class QuotationsApiHarness
{
    public static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";
```

por:

```csharp
internal static class QuotationsApiHarness
{
    /// <summary>31 de diciembre de 2026 a las 23:00 en Bogotá, que en UTC ya es 2027: la frontera
    /// de la spec 2026-09-17. Las pruebas del día del tenant fijan acá el reloj del host.</summary>
    public static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    public static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";
```

Reemplaza:

```csharp
    public sealed class QepApiFactory(
        string connectionString, bool runExportWorker = false, bool publicPaymentProofLinks = false)
        : WebApplicationFactory<Program>
```

por:

```csharp
    public sealed class QepApiFactory(
        string connectionString,
        bool runExportWorker = false,
        bool publicPaymentProofLinks = false,
        DateTimeOffset? utcNow = null)
        : WebApplicationFactory<Program>
```

Reemplaza:

```csharp
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);

                // El publicador real de comprobantes copia al bucket público de R2 por este puerto
```

por:

```csharp
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);

                // Reloj fijo sólo para las pruebas que lo piden (spec 2026-09-17): cortar un instante
                // en días se prueba en la frontera, y el reloj real la cruza cuando quiere. Scoped,
                // igual que SystemClock en QepServiceCollectionExtensions.
                if (utcNow is { } fixedNow)
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                }

                // El publicador real de comprobantes copia al bucket público de R2 por este puerto
```

Y reemplaza el cierre del archivo:

```csharp
                    .Select(key => new PublicStoredObject(key, DateTimeOffset.UtcNow))
                    .ToArray(),
                ContinuationToken: null));
    }
}
```

por:

```csharp
                    .Select(key => new PublicStoredObject(key, DateTimeOffset.UtcNow))
                    .ToArray(),
                ContinuationToken: null));
    }

    /// <summary>El reloj de <see cref="QepApiFactory"/> cuando la prueba pide <c>utcNow</c>.</summary>
    public sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
```

- [ ] **Step 2: Reescribir las tres pruebas en la frontera**

En `QuotationApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
```

por:

```csharp
    // Con el reloj en el 31 de diciembre a las 23:00 de Bogotá (2027 en UTC): el consecutivo es del
    // año del tenant y la vigencia por defecto cuenta quince días desde su hoy (spec 2026-09-17,
    // puntos 2a y 2c). Con el año de UTC la prueba además fallaba sola cada fin de año.
    [Fact]
    public async Task CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
```

y reemplaza:

```csharp
        Assert.StartsWith(
            $"QUO-{DateTime.UtcNow.Year}-", quotation.QuotationNumber, StringComparison.Ordinal);
```

por:

```csharp
        Assert.StartsWith("QUO-2026-", quotation.QuotationNumber, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2027, 1, 15), quotation.ValidUntil);
```

En `OrderApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task ConvertCreatesTheOrderAndLeavesTheQuotationConverted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
```

por:

```csharp
    // Reloj en la frontera de fin de año de Bogotá: el pedido numera con el año del tenant (spec
    // 2026-09-17, punto 2b).
    [Fact]
    public async Task ConvertCreatesTheOrderAndLeavesTheQuotationConverted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
```

y reemplaza:

```csharp
        Assert.StartsWith(
            $"PED-{DateTime.UtcNow.Year}-", order.OrderNumber, StringComparison.Ordinal);
```

por:

```csharp
        Assert.StartsWith("PED-2026-", order.OrderNumber, StringComparison.Ordinal);
```

En `OrderListApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task ListReturnsTheOrderWithItsClientAdvisorAndTotalsResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
```

por:

```csharp
    [Fact]
    public async Task ListReturnsTheOrderWithItsClientAdvisorAndTotalsResolved()
    {
        await using var database = await StartDatabaseAsync();
        // Reloj fijo: el número del pedido lleva el año del tenant (spec 2026-09-17, punto 2b).
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
```

y reemplaza:

```csharp
        Assert.StartsWith($"PED-{DateTime.UtcNow.Year}-", row.OrderNumber, StringComparison.Ordinal);
```

por:

```csharp
        Assert.StartsWith("PED-2026-", row.OrderNumber, StringComparison.Ordinal);
```

- [ ] **Step 3: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationApiTests.CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor|FullyQualifiedName~OrderApiTests.ConvertCreatesTheOrderAndLeavesTheQuotationConverted|FullyQualifiedName~OrderListApiTests.ListReturnsTheOrderWithItsClientAdvisorAndTotalsResolved"
```

Esperado: las tres fallan con `Assert.StartsWith() Failure: String start does not match`, con la cadena real empezando en `QUO-2027-` o `PED-2027-`. Si alguna falla por otra cosa (un 4xx en la siembra con el reloj fijo), **para y pregunta**: no es el RED que se busca.

- [ ] **Step 4: `CreateQuotationHandler` en el día del tenant**

En `CreateQuotation.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateQuotationCommand> validator)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<CreateQuotationCommand> validator)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
```

Reemplaza:

```csharp
        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var quotationNumber = QuotationNumberFormatter.Format(now.Year, sequence);
```

por:

```csharp
        // El año del consecutivo es el del día del tenant, no el de UTC (spec 2026-09-17, punto 2a):
        // en Bogotá, el 31 de diciembre desde las 19:00 UTC ya es el año siguiente.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var sequence = await numberGenerator.NextAsync(command.TenantId, year, cancellationToken);
        var quotationNumber = QuotationNumberFormatter.Format(year, sequence);
```

Reemplaza:

```csharp
            // Sin vigencia en el request, quince dias desde hoy. Se resuelve **al crear** y
            // queda guardado: calcularlo al leer haria que la misma cotizacion mostrara una
            // fecha distinta cada dia.
            command.ValidUntil ?? DefaultValidUntil(now),
```

por:

```csharp
            // Sin vigencia en el request, quince días desde el hoy del tenant (spec 2026-09-17,
            // punto 2c). Se resuelve **al crear** y queda guardado: calcularlo al leer haría que la
            // misma cotización mostrara una fecha distinta cada día.
            command.ValidUntil ?? calendar.Today.AddDays(DefaultValidityDays),
```

Reemplaza:

```csharp
    public const int DefaultValidityDays = 15;

    private static DateOnly DefaultValidUntil(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime).AddDays(DefaultValidityDays);
}
```

por:

```csharp
    public const int DefaultValidityDays = 15;
}
```

- [ ] **Step 5: `ConvertQuotationToOrderHandler` en el día del tenant**

En `ConvertQuotationToOrder.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ConvertQuotationToOrderCommand> validator)
    : ICommandHandler<ConvertQuotationToOrderCommand, OrderDto>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ConvertQuotationToOrderCommand> validator)
    : ICommandHandler<ConvertQuotationToOrderCommand, OrderDto>
```

Reemplaza:

```csharp
        var now = clock.UtcNow;
        var sequence = await numberGenerator.NextAsync(command.TenantId, now.Year, cancellationToken);
        var orderNumber = OrderNumberFormatter.Format(now.Year, sequence);
```

por:

```csharp
        // El año del pedido es el del día del tenant (spec 2026-09-17, punto 2b).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var sequence = await numberGenerator.NextAsync(command.TenantId, year, cancellationToken);
        var orderNumber = OrderNumberFormatter.Format(year, sequence);
```

Los dos archivos siguen usando `BuildingBlocks.Application` (`ICommand`, `ICommandHandler`): el `using` se queda.

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationApiTests|FullyQualifiedName~OrderApiTests|FullyQualifiedName~OrderListApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todo en verde. Las demás pruebas de esos tres archivos siguen con el reloj real.

- [ ] **Step 7: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs
```

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs; git commit -m "fix(quotations): consecutivo y vigencia por defecto en el día del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 4: El vencimiento automático con el hoy de cada tenant (punto 1)

Consulta amplia `Sent` con `ValidUntil < hoyUTC + 2`, agrupada por tenant, con un calendario por tenant y el corte fino en memoria.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Expiration/QuotationExpirationProcessor.cs:1-66`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:101-132` (`RegisterTenantAsync`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExpirationApiTests.cs:134` (prueba nueva y dos helpers antes de `RunExpirationSweepAsync`)

**Interfaces:**
- Consumes: `ITenantClock`; `QuotationsApiHarness.NewYearsEveInBogota` y `QepApiFactory(…, utcNow:)` (Task 3).
- Produces:
  - `QuotationExpirationProcessor(QuotationsDbContext dbContext, IQuotationAuditPublisher auditPublisher, IClock clock, ITenantClock tenantClock)`.
  - `QuotationsApiHarness.RegisterTenantInTimeZoneAsync(QepApiFactory factory, string timeZone, params string[] permissions)` → `Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client)>`. `RegisterTenantAsync(factory, permissions)` queda como atajo con `"America/Bogota"`.

- [ ] **Step 1: Registrar un tenant en otro huso**

En `QuotationsApiHarness.cs`, reemplaza:

```csharp
    public static async Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client)> RegisterTenantAsync(
        QepApiFactory factory, params string[] permissions)
    {
```

por:

```csharp
    public static Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client)> RegisterTenantAsync(
        QepApiFactory factory, params string[] permissions) =>
        RegisterTenantInTimeZoneAsync(factory, "America/Bogota", permissions);

    /// <summary>Lo mismo que <see cref="RegisterTenantAsync"/> con otro huso: el barrido de
    /// vencimiento corta el día por tenant (spec 2026-09-17, punto 1).</summary>
    public static async Task<(Guid TenantId, Guid OwnerUserId, HttpClient Client)> RegisterTenantInTimeZoneAsync(
        QepApiFactory factory, string timeZone, params string[] permissions)
    {
```

y reemplaza, dentro del mismo método:

```csharp
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
```

por:

```csharp
                defaultCulture = "es-CO",
                timeZone,
                dateFormat = "yyyy-MM-dd",
```

- [ ] **Step 2: Escribir la prueba que falla**

En `QuotationExpirationApiTests.cs`, reemplaza:

```csharp
    private static async Task<int> RunExpirationSweepAsync(QepApiFactory factory)
```

por:

```csharp
    // Spec 2026-09-17, punto 1: el 31 de diciembre a las 23:00 en Bogotá ya es 2027 en UTC, y una
    // cotización que vence ese día sigue vigente. Un tenant en UTC+14 ya vive el 1 de enero y la suya
    // sí vence: el corte es por tenant, dentro del mismo barrido.
    [Fact]
    public async Task SweepCutsTheDayInEachTenantsTimeZone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var dueTodayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 31));
        var dueYesterdayInKiritimati = await SentQuotationValidUntilAsync(
            factory, "Pacific/Kiritimati", new DateOnly(2026, 12, 31));
        var dueYesterdayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 30));

        var expiredCount = await RunExpirationSweepAsync(factory);

        Assert.Equal(2, expiredCount);
        Assert.Equal(QuotationStatus.Sent, await StatusOfAsync(factory, dueTodayInBogota));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInKiritimati));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInBogota));
    }

    // Decisión del owner (2026-09-17): un tenant cuyo huso no se puede resolver se salta y se
    // registra, sin caer en UTC y sin frenar el barrido de los demás.
    [Fact]
    public async Task SweepSkipsATenantWhoseTimeZoneCannotBeResolvedAndExpiresTheRest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var orphan = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 1));
        var dueYesterdayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 30));
        await MoveToUnknownTenantAsync(factory, orphan);

        var expiredCount = await RunExpirationSweepAsync(factory);

        Assert.Equal(1, expiredCount);
        Assert.Equal(QuotationStatus.Sent, await StatusOfAsync(factory, orphan));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInBogota));
    }

    // Sin fila en tenancy.tenants: el mismo estado que deja un tenant borrado a mano.
    private static async Task MoveToUnknownTenantAsync(QepApiFactory factory, QuotationId quotationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var unknownTenantId = Guid.CreateVersion7();
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE quotations.quotations SET tenant_id = {unknownTenantId} WHERE id = {quotationId.Value}",
            TestContext.Current.CancellationToken);
    }

    // Enviada por la API y con la vigencia corrida en la base: el paso del tiempo es lo que se simula.
    private static async Task<QuotationId> SentQuotationValidUntilAsync(
        QepApiFactory factory, string timeZone, DateOnly validUntil)
    {
        var (tenantId, _, client) = await RegisterTenantInTimeZoneAsync(factory, timeZone, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var quotationId = new QuotationId(quotation.Id);
        await SetValidUntilAsync(factory, quotationId, validUntil);
        return quotationId;
    }

    private static async Task<QuotationStatus> StatusOfAsync(QepApiFactory factory, QuotationId quotationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.Id == quotationId)
            .Select(quotation => quotation.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> RunExpirationSweepAsync(QepApiFactory factory)
```

- [ ] **Step 3: Correrla y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationExpirationApiTests.SweepCutsTheDayInEachTenantsTimeZone|FullyQualifiedName~QuotationExpirationApiTests.SweepSkipsATenantWhoseTimeZoneCannotBeResolvedAndExpiresTheRest"
```

Esperado:
- `SweepCutsTheDayInEachTenantsTimeZone`: `Assert.Equal() Failure: Values differ`, `Expected: 2`, `Actual: 3`. El hoy UTC (2027-01-01) vence también la de Bogotá.
- `SweepSkipsATenantWhoseTimeZoneCannotBeResolvedAndExpiresTheRest`: `Expected: 1`, `Actual: 2`. Hoy el barrido no mira el tenant y vence también la huérfana.

Si `MoveToUnknownTenantAsync` falla por una FK hacia `tenancy.tenants` o por otra tabla que exija el mismo `tenant_id`, detente y avisa: la prueba necesita otra forma de dejar un tenant sin fila, y no se inventa aquí.

- [ ] **Step 4: Implementar el barrido por tenant**

Reemplaza el contenido entero de `QuotationExpirationProcessor.cs` por:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Infrastructure.Expiration;

/// <summary>
/// US-19: mueve a <see cref="QuotationStatus.Expired"/> las cotizaciones <c>Sent</c> cuya
/// <c>ValidUntil</c> ya pasó. <c>internal</c>, igual que <c>IOutboxProcessor</c> en Tenancy — es
/// mecanismo de infraestructura, no un puerto que otro módulo consuma. Visible además para
/// <c>Modules.Quotations.IntegrationTests</c> (ver <c>InternalsVisibleTo</c> en el csproj): las
/// pruebas de integración lo invocan directo para no depender del temporizador del worker.
/// </summary>
internal interface IQuotationExpirationProcessor
{
    Task<int> ExpirePastDueQuotationsAsync(CancellationToken cancellationToken);
}

internal sealed partial class QuotationExpirationProcessor(
    QuotationsDbContext dbContext,
    IQuotationAuditPublisher auditPublisher,
    IClock clock,
    ITenantClock tenantClock,
    ILogger<QuotationExpirationProcessor> logger) : IQuotationExpirationProcessor
{
    // Sin actor humano detrás del vencimiento automático: Guid.Empty es el sentinela de "sistema"
    // para el actorId que ICatalogAuditPublisher-style publishers ya exigen.
    private static readonly Guid SystemActorId = Guid.Empty;

    public async Task<int> ExpirePastDueQuotationsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // "Hoy" es el día del tenant (spec 2026-09-17, punto 1). Ningún huso adelanta más de un día a
        // UTC, así que lo vencido para cualquier tenant vence antes de hoyUTC + 2: la consulta amplia
        // usa el índice, y el corte fino se hace en memoria con el calendario de cada tenant.
        var ceiling = DateOnly.FromDateTime(now.UtcDateTime).AddDays(2);
        var candidates = await dbContext.Quotations
            .Where(quotation => quotation.Status == QuotationStatus.Sent)
            .Where(quotation => quotation.ValidUntil != null && quotation.ValidUntil < ceiling)
            .ToListAsync(cancellationToken);

        var expired = 0;
        // Un calendario por tenant, no uno por fila.
        foreach (var byTenant in candidates.GroupBy(quotation => quotation.TenantId))
        {
            TenantCalendar calendar;
            try
            {
                calendar = await tenantClock.GetAsync(byTenant.Key, cancellationToken);
            }
            catch (ResourceNotFoundException)
            {
                // Decisión del owner (2026-09-17): sin huso no se adivina el día, ni con UTC por
                // defecto. Se salta el tenant y se registra; los demás tenants siguen venciendo.
                LogTenantSkipped(logger, byTenant.Key, byTenant.Count());
                continue;
            }

            foreach (var quotation in byTenant.Where(quotation => quotation.ValidUntil < calendar.Today))
            {
                quotation.Expire(now);
                dbContext.QuotationHistoryEntries.Add(QuotationHistoryEntry.Create(
                    QuotationHistoryEntryId.New(),
                    quotation.Id,
                    QuotationHistoryEventType.Expired,
                    memberId: null,
                    QuotationChangeSummary.Expired(),
                    now));
                auditPublisher.Publish(
                    quotation.TenantId,
                    SystemActorId,
                    "quotation.quotation.expired",
                    quotation.Id.ToString(),
                    "success",
                    now);
                expired++;
            }
        }

        if (expired > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return expired;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Quotation expiration skipped tenant {TenantId}: its time zone could not be resolved. {CandidateCount} candidate quotations were left untouched.")]
    private static partial void LogTenantSkipped(ILogger logger, Guid tenantId, int candidateCount);
}
```

Verifica que `ResourceNotFoundException` sea la excepción que lanza `TenantClock` (Task 2) y que viva en `BuildingBlocks.Application`, que ya está importado.

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationExpirationApiTests|FullyQualifiedName~QuotationSendVoidApiTests"
```

Esperado: todo en verde, las cuatro pruebas viejas del barrido incluidas.

- [ ] **Step 6: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Infrastructure/Expiration/QuotationExpirationProcessor.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExpirationApiTests.cs
```

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Expiration/QuotationExpirationProcessor.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExpirationApiTests.cs; git commit -m "fix(quotations): vencer cotizaciones con el hoy de cada tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 5: Filtros de listado, conteo y export de cotizaciones y pedidos (punto 3)

El handler (o el procesador del export) convierte los `DateOnly` desde/hasta con `StartOfDayUtc` / `EndOfDayExclusiveUtc`, y **el repositorio recibe instantes**: `createdFrom` inclusivo y `createdBefore` exclusivo (`convertedFrom` / `convertedBefore` en pedidos). Infrastructure no decide husos.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/TenantDayRange.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs:5-70`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs:27-102`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs:38-190`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs:62-217`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ListQuotations.cs:93-126`, `ListOrders.cs:70-106`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs:68-136`, `ExportOrders.cs:58-128`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs:1-74`, `OrdersExportProcessor.cs:1-101`
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs:162-200,287-400,526-640`, `ListQuotationsHandlerTests.cs:186-191,276-282`, `ListOrdersHandlerTests.cs` (`NewHandler`), `ExportQuotationsHandlerTests.cs:21-23,222-225,278-285`, `ExportOrdersHandlerTests.cs:18-20,161-165,200-208`, `QuotationsExportProcessorTests.cs:17-19,126-128,211-223`, `OrdersExportProcessorTests.cs:17-19,259-263,316-326`
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs:285-302`, `OrderExportApiTests.cs:222-241`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationListApiTests.cs`, `OrderListApiTests.cs` (una prueba nueva en cada uno)

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar` (Tasks 1-2); `NewYearsEveInBogota` y `QepApiFactory(…, utcNow:)` (Task 3).
- Produces:
  - `internal readonly record struct TenantInstantRange(DateTimeOffset? From, DateTimeOffset? Before)` y `internal static class TenantDayRange` con `Task<TenantInstantRange> ResolveAsync(ITenantClock tenantClock, Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)` y `TenantInstantRange Of(TenantCalendar calendar, DateOnly? from, DateOnly? to)`, en `Modules.Quotations.Application`.
  - `IQuotationRepository.SearchAsync / ListForExportAsync / AnyForExportAsync` con `DateTimeOffset? createdFrom, DateTimeOffset? createdBefore` en lugar de `DateOnly? createdFrom, DateOnly? createdTo`.
  - `IOrderRepository.SearchAsync / AnyForExportAsync / ListForExportAsync` con `DateTimeOffset? convertedFrom, DateTimeOffset? convertedBefore`.
  - `ListQuotationsHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)` y `ListOrdersHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)`.
  - `ExportQuotationsHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)` y `ExportOrdersHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)` — `IClock` sale de las dos firmas.
  - `QuotationsExportProcessor(IQuotationRepository, IQuotationCustomerLookup, IQuotationAdvisorLookup, IExportWorkbookWriter, IExportFileStorage, ITenantClock tenantClock)` y `OrdersExportProcessor(IOrderRepository, IQuotationCustomerLookup, IQuotationAdvisorLookup, IPaymentProofPublisher, IExportWorkbookWriter, IExportFileStorage, ITenantClock tenantClock)`. Task 10 usa ese mismo calendario.
  - En `QuotationsTestDoubles.cs`: `internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota") : ITenantClock` con `List<Guid> RequestedTenantIds`; `RecordedExportSearch(Guid? ClientId, IReadOnlyCollection<Guid>? ClientIds, MemberId? AdvisorId, QuotationStatus? Status, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedBefore, string? QuotationNumber)` y `RecordedOrderExportSearch(Guid? ClientId, IReadOnlyCollection<Guid>? ClientIds, MemberId? AdvisorId, OrderStatus? Status, OrderPaymentStatus? PaymentStatus, DateTimeOffset? ConvertedFrom, DateTimeOffset? ConvertedBefore, string? OrderNumber)`. Tasks 9 y 10 usan `FixedTenantClock`.

- [ ] **Step 1: El doble del reloj del tenant y los filtros anotados**

En `QuotationsTestDoubles.cs`, reemplaza:

```csharp
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Los filtros con que se preguntó por filas o se leyó para exportar.</summary>
internal sealed record RecordedExportSearch(
    Guid? ClientId,
    IReadOnlyCollection<Guid>? ClientIds,
    MemberId? AdvisorId,
    QuotationStatus? Status,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    string? QuotationNumber);
```

por:

```csharp
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>El calendario de un tenant en un huso fijo, Bogotá por defecto (spec 2026-09-17). Anota
/// por qué tenant se preguntó: un proceso que recorre tenants pide uno por tenant.</summary>
internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota") : ITenantClock
{
    public List<Guid> RequestedTenantIds { get; } = [];

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        RequestedTenantIds.Add(tenantId);
        return Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }
}

/// <summary>Los filtros con que se preguntó por filas o se leyó para exportar, ya como instantes:
/// desde inclusivo y antes-de exclusivo (spec 2026-09-17, punto 3).</summary>
internal sealed record RecordedExportSearch(
    Guid? ClientId,
    IReadOnlyCollection<Guid>? ClientIds,
    MemberId? AdvisorId,
    QuotationStatus? Status,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedBefore,
    string? QuotationNumber);
```

Reemplaza **todas** las apariciones (seis, en `StubQuotationRepository` y `StubQuotationListRepository`) de:

```csharp
        DateOnly? createdFrom,
        DateOnly? createdTo,
```

por:

```csharp
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
```

Reemplaza **todas** las apariciones (dos) de:

```csharp
            clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);
```

por:

```csharp
            clientId, clientIds, advisorId, status, createdFrom, createdBefore, quotationNumber);
```

Reemplaza:

```csharp
    OrderPaymentStatus? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? OrderNumber);
```

por:

```csharp
    OrderPaymentStatus? PaymentStatus,
    DateTimeOffset? ConvertedFrom,
    DateTimeOffset? ConvertedBefore,
    string? OrderNumber);
```

Reemplaza **todas** las apariciones (tres, en `StubOrderListRepository`) de:

```csharp
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
```

por:

```csharp
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
```

Reemplaza **todas** las apariciones (dos) de:

```csharp
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, orderNumber);
```

por:

```csharp
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
```

- [ ] **Step 2: Las pruebas unitarias con instantes y el reloj del tenant**

En `ListQuotationsHandlerTests.cs`, reemplaza:

```csharp
            new StubQuotationAdvisorLookup(),
            new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.OrderRead));
```

por:

```csharp
            new StubQuotationAdvisorLookup(),
            new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.OrderRead),
            new FixedTenantClock(Now));
```

y reemplaza:

```csharp
            advisors,
            new StubExecutionContext(SubjectId, TenantId));
}
```

por:

```csharp
            advisors,
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));
}
```

En `ListOrdersHandlerTests.cs`, reemplaza:

```csharp
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubExecutionContext(SubjectId, TenantId));
```

por:

```csharp
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));
```

En `ExportQuotationsHandlerTests.cs`, reemplaza:

```csharp
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);
```

por:

```csharp
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El mismo rango cortado en el día de Bogotá (spec 2026-09-17, punto 3): 00:00 del 1 de enero y
    // 00:00 del día siguiente al "hasta".
    private static readonly DateTimeOffset FromUtc = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
```

reemplaza:

```csharp
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, From, To, "0001"),
```

por:

```csharp
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, FromUtc, BeforeUtc, "0001"),
```

y reemplaza:

```csharp
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
```

por:

```csharp
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));
```

En `ExportOrdersHandlerTests.cs`, reemplaza:

```csharp
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);
```

por:

```csharp
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El mismo rango cortado en el día de Bogotá (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
```

reemplaza:

```csharp
                ClientId, null, AdvisorId, OrderStatus.Pending, OrderPaymentStatus.PaymentPending, From, To, "PED-2026"),
```

por:

```csharp
                ClientId, null, AdvisorId, OrderStatus.Pending, OrderPaymentStatus.PaymentPending, FromUtc, BeforeUtc, "PED-2026"),
```

y reemplaza:

```csharp
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
```

por:

```csharp
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));
```

En `QuotationsExportProcessorTests.cs`, reemplaza:

```csharp
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);
```

por:

```csharp
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El rango guardado en el job, cortado en el día de Bogotá al procesar (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
```

reemplaza:

```csharp
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, From, To, "0001"),
```

por:

```csharp
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, FromUtc, BeforeUtc, "0001"),
```

y reemplaza:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
```

por:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedTenantClock(Now));
```

En `OrdersExportProcessorTests.cs`, reemplaza:

```csharp
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);
```

por:

```csharp
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El rango guardado en el job, cortado en el día de Bogotá al procesar (spec 2026-09-17, punto 3).
    private static readonly DateTimeOffset FromUtc = new(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
```

reemplaza:

```csharp
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, From, To, "PED"),
```

por:

```csharp
                ClientId, null, AdvisorId, OrderStatus.Approved, OrderPaymentStatus.FullPaymentReceived, FromUtc, BeforeUtc, "PED"),
```

y reemplaza:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedClock(Now));
```

por:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedTenantClock(Now));
```

- [ ] **Step 3: Las pruebas de integración en la frontera**

En `QuotationListApiTests.cs`, agrega antes del cierre de la clase (la última `}` del archivo):

```csharp
    // Spec 2026-09-17, punto 3: "hasta el 31" es el 31 del tenant. Creada el 31 a las 23:00 en
    // Bogotá —ya 2027 en UTC—, entra en el día 31 y no en el 1 de enero, y el total del listado (el
    // conteo) dice lo mismo que las filas.
    [Fact]
    public async Task ListFiltersTheDateRangeByTheTenantsLocalDay()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var lastDay = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?createdFrom=2026-12-31&createdTo=2026-12-31",
            TestContext.Current.CancellationToken);
        var nextDay = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?createdFrom=2027-01-01&createdTo=2027-01-01",
            TestContext.Current.CancellationToken);

        Assert.NotNull(lastDay);
        Assert.Equal(quotation.Id, Assert.Single(lastDay.Items).Id);
        Assert.Equal(1, lastDay.Total);
        Assert.NotNull(nextDay);
        Assert.Empty(nextDay.Items);
        Assert.Equal(0, nextDay.Total);
    }
```

En `OrderListApiTests.cs`, reemplaza:

```csharp
    private static async Task<OrderResponse> ConvertToOrderAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
```

por:

```csharp
    // Spec 2026-09-17, punto 3: el pedido convertido el 31 a las 23:00 de Bogotá —ya 2027 en UTC—
    // entra en el día 31 del tenant y no en el 1 de enero.
    [Fact]
    public async Task ListFiltersTheConversionRangeByTheTenantsLocalDay()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ConvertToOrderAsync(client, tenantId, quotation.Id);

        var lastDay = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?convertedFrom=2026-12-31&convertedTo=2026-12-31",
            TestContext.Current.CancellationToken);
        var nextDay = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?convertedFrom=2027-01-01&convertedTo=2027-01-01",
            TestContext.Current.CancellationToken);

        Assert.NotNull(lastDay);
        Assert.Equal(quotation.Id, Assert.Single(lastDay.Items).QuotationId);
        Assert.Equal(1, lastDay.Total);
        Assert.NotNull(nextDay);
        Assert.Empty(nextDay.Items);
        Assert.Equal(0, nextDay.Total);
    }

    private static async Task<OrderResponse> ConvertToOrderAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
```

En `QuotationExportApiTests.cs`, reemplaza:

```csharp
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQuotationRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            QuotationStatus.Draft,
            today.AddDays(-7),
            today.AddDays(1),
```

por:

```csharp
        // El repositorio recibe instantes desde la spec 2026-09-17 (punto 3): la ventana es amplia a
        // propósito, lo que se prueba es el keyset.
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQuotationRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            QuotationStatus.Draft,
            now.AddDays(-7),
            now.AddDays(2),
```

En `OrderExportApiTests.cs`, reemplaza:

```csharp
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            OrderStatus.Pending,
            paymentStatus: null,
            today.AddDays(-7),
            today.AddDays(1),
```

por:

```csharp
        // El repositorio recibe instantes desde la spec 2026-09-17 (punto 3): la ventana es amplia a
        // propósito, lo que se prueba es el keyset.
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderRepository>().ListForExportAsync(
            tenantId,
            clientId: null,
            clientIds: null,
            advisorId: null,
            OrderStatus.Pending,
            paymentStatus: null,
            now.AddDays(-7),
            now.AddDays(2),
```

- [ ] **Step 4: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~ExportQuotationsHandlerTests|FullyQualifiedName~ExportOrdersHandlerTests|FullyQualifiedName~QuotationsExportProcessorTests|FullyQualifiedName~OrdersExportProcessorTests|FullyQualifiedName~ListQuotationsHandlerTests|FullyQualifiedName~ListOrdersHandlerTests"
```

Esperado: falla la compilación de las pruebas unitarias con `error CS1503` (`cannot convert from 'System.DateTimeOffset?' to 'System.DateOnly?'` en los dobles contra la interfaz vieja, y `cannot convert from 'FixedTenantClock' to 'BuildingBlocks.Application.IClock'` en los constructores). Es el RED de la firma; las pruebas de integración del paso 3 compilan en otro proyecto y fallan igual por `ListForExportAsync` con instantes.

- [ ] **Step 5: El rango de días del tenant**

Crea `src/Modules/Quotations/Modules.Quotations.Application/TenantDayRange.cs`:

```csharp
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Un rango de días del tenant como instantes: <see cref="From"/> inclusivo y
/// <see cref="Before"/> exclusivo. Nulo es "sin ese extremo".</summary>
internal readonly record struct TenantInstantRange(DateTimeOffset? From, DateTimeOffset? Before);

/// <summary>
/// Corta los filtros de fecha del listado y del export en el día del tenant (spec 2026-09-17, punto
/// 3): el desde es su 00:00 local, y el hasta es exclusivo en el 00:00 local del día siguiente, así
/// "hasta el 31" incluye todo el 31 del tenant. El repositorio recibe los instantes y no decide husos.
/// </summary>
internal static class TenantDayRange
{
    /// <summary>Sin fechas no hay nada que cortar, y no se consulta el huso: el listado sin rango no
    /// paga esa ida a la base.</summary>
    public static async Task<TenantInstantRange> ResolveAsync(
        ITenantClock tenantClock,
        Guid tenantId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (from is null && to is null)
        {
            return new TenantInstantRange(null, null);
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return Of(calendar, from, to);
    }

    public static TenantInstantRange Of(TenantCalendar calendar, DateOnly? from, DateOnly? to) =>
        new(
            from is { } start ? calendar.StartOfDayUtc(start) : (DateTimeOffset?)null,
            to is { } end ? calendar.EndOfDayExclusiveUtc(end) : (DateTimeOffset?)null);
}
```

- [ ] **Step 6: Los repositorios reciben instantes**

En `IQuotationRepository.cs`, reemplaza:

```csharp
// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar. Mismo criterio que ICustomerRepository.
```

por:

```csharp
// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar. Mismo criterio que ICustomerRepository.
//
// Las fechas de los filtros llegan como instantes: `createdFrom` inclusivo y `createdBefore`
// exclusivo, ya cortados en el día del tenant por quien llama (spec 2026-09-17, punto 3). El
// repositorio no decide husos.
```

y reemplaza **todas** las apariciones (tres) de:

```csharp
        DateOnly? createdFrom,
        DateOnly? createdTo,
```

por:

```csharp
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
```

En `IOrderRepository.cs`, reemplaza:

```csharp
public interface IOrderRepository
{
```

por:

```csharp
/// <summary>
/// Las fechas de los filtros llegan como instantes: <c>convertedFrom</c> inclusivo y
/// <c>convertedBefore</c> exclusivo, ya cortados en el día del tenant por quien llama (spec
/// 2026-09-17, punto 3). El repositorio no decide husos.
/// </summary>
public interface IOrderRepository
{
```

y reemplaza **todas** las apariciones (tres) de:

```csharp
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
```

por:

```csharp
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
```

En `QuotationRepository.cs`, reemplaza **todas** las apariciones (cuatro) de:

```csharp
        DateOnly? createdFrom,
        DateOnly? createdTo,
```

por:

```csharp
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
```

reemplaza **todas** las apariciones (tres) de:

```csharp
            tenantId, clientId, clientIds, advisorId, status, createdFrom, createdTo, quotationNumber);
```

por:

```csharp
            tenantId, clientId, clientIds, advisorId, status, createdFrom, createdBefore, quotationNumber);
```

y reemplaza:

```csharp
        if (createdFrom is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(quotation => quotation.CreatedAt >= fromUtc);
        }

        if (createdTo is { } to)
        {
            // Limite superior exclusivo al dia siguiente: "hasta el 30" incluye todo el 30,
            // no solo el instante 00:00:00 de esa fecha.
            var toUtcExclusive = new DateTimeOffset(
                to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(quotation => quotation.CreatedAt < toUtcExclusive);
        }
```

por:

```csharp
        if (createdFrom is { } from)
        {
            query = query.Where(quotation => quotation.CreatedAt >= from);
        }

        if (createdBefore is { } before)
        {
            // Exclusivo: quien llama ya lo corrió al 00:00 local del día siguiente al "hasta" (spec
            // 2026-09-17, punto 3), así "hasta el 30" incluye todo el 30 del tenant.
            query = query.Where(quotation => quotation.CreatedAt < before);
        }
```

En `OrderRepository.cs`, reemplaza **todas** las apariciones (cuatro) de:

```csharp
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
```

por:

```csharp
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
```

reemplaza **todas** las apariciones (tres) de:

```csharp
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedTo, orderNumber);
```

por:

```csharp
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
```

y reemplaza:

```csharp
        if (convertedFrom is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            orders = orders.Where(order => order.ConvertedAt >= fromUtc);
        }

        if (convertedTo is { } to)
        {
            // Limite superior exclusivo al dia siguiente, igual que el listado de cotizaciones:
            // "hasta el 30" incluye todo el 30, no solo su instante 00:00:00.
            var toUtcExclusive = new DateTimeOffset(
                to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            orders = orders.Where(order => order.ConvertedAt < toUtcExclusive);
        }
```

por:

```csharp
        if (convertedFrom is { } from)
        {
            orders = orders.Where(order => order.ConvertedAt >= from);
        }

        if (convertedBefore is { } before)
        {
            // Exclusivo, igual que el listado de cotizaciones: quien llama ya lo corrió al 00:00
            // local del día siguiente al "hasta" (spec 2026-09-17, punto 3).
            orders = orders.Where(order => order.ConvertedAt < before);
        }
```

- [ ] **Step 7: Los listados cortan en el día del tenant**

En `ListQuotations.cs`, reemplaza:

```csharp
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuotationsQuery, QuotationPage>
```

por:

```csharp
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListQuotationsQuery, QuotationPage>
```

y reemplaza:

```csharp
        var (quotations, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            query.CreatedFrom,
            query.CreatedTo,
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 3): el total del listado es
        // el mismo conteo, así que también cambia.
        var created = await TenantDayRange.ResolveAsync(
            tenantClock, query.TenantId, query.CreatedFrom, query.CreatedTo, cancellationToken);

        var (quotations, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            created.From,
            created.Before,
```

En `ListOrders.cs`, reemplaza:

```csharp
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListOrdersQuery, OrderPage>
```

por:

```csharp
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListOrdersQuery, OrderPage>
```

y reemplaza:

```csharp
        var (rows, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            query.ConvertedFrom,
            query.ConvertedTo,
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 3).
        var converted = await TenantDayRange.ResolveAsync(
            tenantClock, query.TenantId, query.ConvertedFrom, query.ConvertedTo, cancellationToken);

        var (rows, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            converted.From,
            converted.Before,
```

- [ ] **Step 8: Los pedidos de export cortan en el día del tenant**

En `ExportQuotations.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>
```

reemplaza:

```csharp
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            command.CreatedFrom,
            command.CreatedTo,
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 3). El job guarda las fechas
        // tal como llegaron: el procesador las vuelve a cortar con el calendario de ese momento.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var created = TenantDayRange.Of(calendar, command.CreatedFrom, command.CreatedTo);
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            created.From,
            created.Before,
```

y reemplaza:

```csharp
                command.QuotationNumber)),
            clock.UtcNow);
```

por:

```csharp
                command.QuotationNumber)),
            calendar.UtcNow);
```

En `ExportOrders.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportOrdersCommand, ExportJobAccepted>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportOrdersCommand, ExportJobAccepted>
```

reemplaza:

```csharp
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            command.ConvertedFrom,
            command.ConvertedTo,
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 3).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var converted = TenantDayRange.Of(calendar, command.ConvertedFrom, command.ConvertedTo);
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            converted.From,
            converted.Before,
```

y reemplaza:

```csharp
                command.OrderNumber)),
            clock.UtcNow);
```

por:

```csharp
                command.OrderNumber)),
            calendar.UtcNow);
```

Los dos archivos siguen usando `BuildingBlocks.Application` (`ICommand`): el `using` se queda.

- [ ] **Step 9: Los procesadores cortan en el día del tenant**

En `QuotationsExportProcessor.cs`, reemplaza:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
```

por:

```csharp
using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
```

reemplaza:

```csharp
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
```

por:

```csharp
    IExportFileStorage storage,
    ITenantClock tenantClock)
    : IExportJobProcessor
```

reemplaza:

```csharp
        var generatedAt = clock.UtcNow;
```

por:

```csharp
        // Un calendario por job (spec 2026-09-17): corta el rango guardado en el día del tenant.
        var calendar = await tenantClock.GetAsync(job.TenantId, cancellationToken);
        var created = TenantDayRange.Of(calendar, filters.CreatedFrom, filters.CreatedTo);
        var generatedAt = calendar.UtcNow;
```

y reemplaza:

```csharp
                filters.CreatedFrom,
                filters.CreatedTo,
```

por:

```csharp
                created.From,
                created.Before,
```

En `OrdersExportProcessor.cs`, reemplaza:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
```

por:

```csharp
using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
```

reemplaza:

```csharp
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
```

por:

```csharp
    IExportFileStorage storage,
    ITenantClock tenantClock)
    : IExportJobProcessor
```

reemplaza:

```csharp
        var generatedAt = clock.UtcNow;
```

por:

```csharp
        // Un calendario por job (spec 2026-09-17): corta el rango guardado en el día del tenant.
        var calendar = await tenantClock.GetAsync(job.TenantId, cancellationToken);
        var converted = TenantDayRange.Of(calendar, filters.ConvertedFrom, filters.ConvertedTo);
        var generatedAt = calendar.UtcNow;
```

y reemplaza:

```csharp
                filters.ConvertedFrom,
                filters.ConvertedTo,
```

por:

```csharp
                converted.From,
                converted.Before,
```

Si el build dice que `BuildingBlocks.Application` sí hacía falta en algún procesador (`CS0246`), vuelve a agregar ese `using`; si dice que `System.Globalization` quedó sin uso, no aplica todavía: los dos procesadores lo siguen usando en `ToCells`.

- [ ] **Step 10: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationListApiTests|FullyQualifiedName~OrderListApiTests|FullyQualifiedName~QuotationExportApiTests|FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~OrderContractApiTests"
```

Esperado: build con 0 errores y 0 advertencias; las unitarias en verde; las de integración en verde, `ListFiltersTheDateRangeByTheTenantsLocalDay` y `ListFiltersTheConversionRangeByTheTenantsLocalDay` incluidas.

- [ ] **Step 11: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/TenantDayRange.cs src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs src/Modules/Quotations/Modules.Quotations.Application/ListQuotations.cs src/Modules/Quotations/Modules.Quotations.Application/ListOrders.cs src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs src/Modules/Quotations/Modules.Quotations.Application/ExportOrders.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationListApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs
```

- [ ] **Step 12: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/TenantDayRange.cs src/Modules/Quotations/Modules.Quotations.Application/IQuotationRepository.cs src/Modules/Quotations/Modules.Quotations.Application/IOrderRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs src/Modules/Quotations/Modules.Quotations.Application/ListQuotations.cs src/Modules/Quotations/Modules.Quotations.Application/ListOrders.cs src/Modules/Quotations/Modules.Quotations.Application/ExportQuotations.cs src/Modules/Quotations/Modules.Quotations.Application/ExportOrders.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ListQuotationsHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ListOrdersHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationsHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportOrdersHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationListApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderListApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs; git commit -m "fix(quotations): filtrar listados y exports por el día del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 6: Rangos de los cuatro reportes en el día del tenant (punto 4)

`ReportDateRange` desaparece. Los criterios llevan un `ReportPeriod` con los instantes ya cortados y el huso; los ocho handlers piden el calendario y la ventana anterior se corta con el mismo.

**Files:**
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ReportingFilters.cs:1-144`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/ListOrdersReport.cs:18-40`, `ListQuotationsReport.cs:15-37`, `ListPriceChangeReport.cs:16-38`, `ListCustomerReport.cs:20-42`
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/GetOrdersReportSummary.cs:17-72`, `QuotationsReportSummary.cs:119-190`, `PriceChangeReportSummary.cs:87-149`, `CustomerReportSummary.cs:68-128`
- Modify: `src/Bootstrapper/ReportingLookups.cs:135-153` (borrar `ReportDateRange`)
- Modify: `src/Bootstrapper/OrdersReportSource.cs:97-100,228-238`, `QuotationsReportSource.cs:84-85,359-369`, `CustomerReportSource.cs:74-76,269-280`, `PriceChangeReportSource.cs:91-93,248-258`
- Modify (tests): `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportingTestDoubles.cs:1-23`, `OrdersReportHandlerTests.cs:101-111`, `OrdersReportSummaryHandlerTests.cs:127-130,180-187`, `QuotationsReportSummaryHandlerTests.cs:148-151,208-216`, `PriceChangeReportSummaryHandlerTests.cs:138-141,192-199`, `CustomerReportSummaryHandlerTests.cs:159-162,213-220`
- Modify (tests): `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs:1-16,30-32,437-485`
- Test: `OrdersReportHandlerTests.cs` y `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs` (una prueba nueva en cada uno)

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar` (Tasks 1-2).
- Produces (`Modules.Reporting.Application`):
  - `public sealed record ReportPeriod(DateTimeOffset? Start, DateTimeOffset? EndExclusive, TimeZoneInfo TimeZone)` con `public static ReportPeriod Of(TenantCalendar calendar, DateOnly? from, DateOnly? to)`.
  - `OrdersReportCriteria(Guid TenantId, ReportPeriod Period, Guid? AdvisorId, Guid? ClientId, OrderPaymentStatusFilter? PaymentStatus)`, `QuotationsReportCriteria(Guid TenantId, ReportPeriod Period, Guid? AdvisorId, Guid? ClientId, QuotationStatusFilter? Status)`, `PriceChangeReportCriteria(Guid TenantId, ReportPeriod Period, Guid? ProductId, Guid? ChangedBy, PriceChangeField? Field)`, `CustomerReportCriteria(Guid TenantId, ReportPeriod Period, bool? IsActive, Guid? ClassificationId, Guid? DepartmentId)`.
  - `ReportFilterMapping.ToCriteria(this <X>ReportFilter filter, TenantCalendar calendar)` para los cuatro filtros.
  - Los ocho handlers con `ITenantClock tenantClock` como último parámetro; `GetQuotationsReportSummaryHandler(IQuotationsReportSource, IValidator<QuotationsReportFilter>, IExecutionContext, ITenantClock)` pierde `IClock`.
  - En `ReportingTestDoubles.cs`: `internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota") : ITenantClock` con un constructor sin parámetros (`2026-09-03T14:30Z`). `FixedClock` se borra.
  - En `ReportingApiHarness`: `NewYearsEveInBogota`, `FixedClock` y `QepApiFactory(string connectionString, DateTimeOffset? utcNow = null)`. Tasks 7 y 8 los usan.

- [ ] **Step 1: El doble del reloj del tenant en Reporting**

En `ReportingTestDoubles.cs`, reemplaza:

```csharp
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
```

por:

```csharp
/// <summary>El calendario de un tenant en un huso fijo, Bogotá por defecto (spec 2026-09-17). Sin
/// instante, media tarde del 3 de septiembre de 2026 en UTC: las pruebas que no miran el hoy no
/// tienen que inventarlo.</summary>
internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota")
    : ITenantClock
{
    public FixedTenantClock()
        : this(new DateTimeOffset(2026, 9, 3, 14, 30, 0, TimeSpan.Zero))
    {
    }

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
}
```

Si después de este cambio el build marca `using BuildingBlocks.Application;` sin uso en `ReportingTestDoubles.cs`, bórralo.

- [ ] **Step 2: Las pruebas unitarias con el período en instantes**

En `OrdersReportHandlerTests.cs`, reemplaza:

```csharp
        new(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));

    private static OrdersReportFilter Filter(string? paymentStatus = null) =>
```

por:

```csharp
        new(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock());

    // Spec 2026-09-17, punto 4: el reporte de diciembre va del 00:00 del 1 de diciembre al 00:00 del
    // 1 de enero en el huso del tenant, y el huso viaja para la serie mensual.
    [Fact]
    public async Task ListingCutsTheRangeAtTheTenantsLocalMidnights()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.OrdersRead);

        await handler.HandleAsync(
            new ListOrdersReportQuery(
                new OrdersReportFilter(
                    Tenant, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null, null, null),
                1,
                50),
            TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastCriteria);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero), source.LastCriteria.Period.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero), source.LastCriteria.Period.EndExclusive);
        Assert.Equal("America/Bogota", source.LastCriteria.Period.TimeZone.Id);
    }

    private static OrdersReportFilter Filter(string? paymentStatus = null) =>
```

En `OrdersReportSummaryHandlerTests.cs`, `PriceChangeReportSummaryHandlerTests.cs` y `CustomerReportSummaryHandlerTests.cs`, reemplaza en cada uno:

```csharp
        Assert.Equal(new DateOnly(2025, 12, 1), preceding.From);
        Assert.Equal(new DateOnly(2025, 12, 31), preceding.To);
```

por:

```csharp
        // La ventana anterior se corta en el día del tenant igual que la pedida (spec 2026-09-17,
        // punto 4): 00:00 del 1 de diciembre y 00:00 del 1 de enero, en Bogotá.
        Assert.Equal(new DateTimeOffset(2025, 12, 1, 5, 0, 0, TimeSpan.Zero), preceding.Period.Start);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero), preceding.Period.EndExclusive);
```

y reemplaza el helper del handler agregando el reloj. En `OrdersReportSummaryHandlerTests.cs`:

```csharp
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));
```

por:

```csharp
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock());
```

En `PriceChangeReportSummaryHandlerTests.cs`:

```csharp
            new PriceChangeReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));
```

por:

```csharp
            new PriceChangeReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock());
```

En `CustomerReportSummaryHandlerTests.cs`:

```csharp
            new CustomerReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));
```

por:

```csharp
            new CustomerReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock());
```

En `QuotationsReportSummaryHandlerTests.cs`, reemplaza la misma aserción de la ventana anterior (las dos líneas de `preceding.From`/`preceding.To`) con el bloque de arriba, y reemplaza:

```csharp
            new FakeExecutionContext(callerTenant, permissions),
            new FixedClock(Now));
```

por:

```csharp
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock(Now));
```

- [ ] **Step 3: El reloj fijo en la factoría de Reporting y la prueba de integración**

En `ReportingApiHarness.cs`, reemplaza:

```csharp
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
```

por:

```csharp
using System.Net.Http.Json;
using System.Security.Cryptography;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
```

reemplaza:

```csharp
internal static class ReportingApiHarness
{
    public static string ReportsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/reports";
```

por:

```csharp
internal static class ReportingApiHarness
{
    /// <summary>31 de diciembre de 2026 a las 23:00 en Bogotá, que en UTC ya es 2027: la frontera
    /// de la spec 2026-09-17. Todo lo que se siembra con este reloj cae en diciembre para el tenant y
    /// en enero para UTC.</summary>
    public static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    public static string ReportsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/reports";
```

reemplaza:

```csharp
    public sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
```

por:

```csharp
    public sealed class QepApiFactory(string connectionString, DateTimeOffset? utcNow = null)
        : WebApplicationFactory<Program>
```

reemplaza:

```csharp
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);
```

por:

```csharp
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);

                // Reloj fijo sólo para las pruebas que lo piden (spec 2026-09-17): el día y el mes del
                // tenant se prueban en la frontera. Scoped, igual que SystemClock.
                if (utcNow is { } fixedNow)
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                }
```

y agrega, justo antes de `private sealed class StubPdfRenderer : IQuotationPdfRenderer`:

```csharp
    /// <summary>El reloj de <see cref="QepApiFactory"/> cuando la prueba pide <c>utcNow</c>.</summary>
    public sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

```

En `OrdersReportApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task ExportRoutesNoLongerExist()
```

por:

```csharp
    // Spec 2026-09-17, punto 4: "hasta el 31" es el 31 del tenant. El pedido convertido el 31 a las
    // 23:00 de Bogotá —ya 2027 en UTC— está en el reporte de diciembre y no en el de enero.
    [Fact]
    public async Task TheDateRangeIsCutAtTheTenantsLocalMidnight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var order = await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);

        var december = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);
        var january = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders?from=2027-01-01&to=2027-01-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(december);
        Assert.Equal(order.Id, Assert.Single(december.Items).OrderId);
        Assert.NotNull(january);
        Assert.Empty(january.Items);
    }

    [Fact]
    public async Task ExportRoutesNoLongerExist()
```

- [ ] **Step 4: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests/Modules.Reporting.UnitTests.csproj
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~OrdersReportApiTests.TheDateRangeIsCutAtTheTenantsLocalMidnight"
```

Esperado: las unitarias no compilan (`error CS1061: 'OrdersReportCriteria' does not contain a definition for 'Period'` y `CS1729` por el argumento de más en los constructores de los handlers). La de integración compila y falla con `Assert.Single() Failure: The collection was empty`: con el corte en UTC el pedido cae en enero.

- [ ] **Step 5: `ReportPeriod` y los criterios**

En `ReportingFilters.cs`, reemplaza:

```csharp
using FluentValidation;
using Modules.Reporting.Domain;

namespace Modules.Reporting.Application;
```

por:

```csharp
using FluentValidation;
using Modules.Reporting.Domain;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El rango de un reporte ya cortado en el día del tenant (spec 2026-09-17, decisión 1 y punto 4):
/// <see cref="Start"/> es el 00:00 local del "desde" y <see cref="EndExclusive"/> el 00:00 local del
/// día siguiente al "hasta", los dos como instantes. <see cref="TimeZone"/> viaja para la serie
/// mensual (punto 5). Cierra la decisión de producto que <c>ReportDateRange</c> dejaba abierta: los
/// orígenes comparan instantes y no deciden husos.
/// </summary>
public sealed record ReportPeriod(DateTimeOffset? Start, DateTimeOffset? EndExclusive, TimeZoneInfo TimeZone)
{
    public static ReportPeriod Of(TenantCalendar calendar, DateOnly? from, DateOnly? to) =>
        new(
            from is { } start ? calendar.StartOfDayUtc(start) : (DateTimeOffset?)null,
            to is { } end ? calendar.EndOfDayExclusiveUtc(end) : (DateTimeOffset?)null,
            calendar.TimeZone);
}
```

Reemplaza:

```csharp
public sealed record OrdersReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
```

por:

```csharp
public sealed record OrdersReportCriteria(
    Guid TenantId,
    ReportPeriod Period,
    Guid? AdvisorId,
```

Reemplaza:

```csharp
public sealed record QuotationsReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
```

por:

```csharp
public sealed record QuotationsReportCriteria(
    Guid TenantId,
    ReportPeriod Period,
    Guid? AdvisorId,
```

Reemplaza:

```csharp
public sealed record PriceChangeReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? ProductId,
```

por:

```csharp
public sealed record PriceChangeReportCriteria(
    Guid TenantId,
    ReportPeriod Period,
    Guid? ProductId,
```

Reemplaza:

```csharp
public sealed record CustomerReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    bool? IsActive,
```

por:

```csharp
public sealed record CustomerReportCriteria(
    Guid TenantId,
    ReportPeriod Period,
    bool? IsActive,
```

Y reemplaza la clase `ReportFilterMapping` entera:

```csharp
public static class ReportFilterMapping
{
    public static OrdersReportCriteria ToCriteria(this OrdersReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParsePaymentStatus(filter.PaymentStatus));

    public static QuotationsReportCriteria ToCriteria(this QuotationsReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParseQuotationStatus(filter.Status));

    public static PriceChangeReportCriteria ToCriteria(this PriceChangeReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.ProductId,
            filter.ChangedBy,
            ReportFilterParser.ParsePriceChangeField(filter.Field));

    public static CustomerReportCriteria ToCriteria(this CustomerReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.IsActive,
            filter.ClassificationId,
            filter.DepartmentId);
}
```

por:

```csharp
public static class ReportFilterMapping
{
    public static OrdersReportCriteria ToCriteria(this OrdersReportFilter filter, TenantCalendar calendar) =>
        new(
            filter.TenantId,
            ReportPeriod.Of(calendar, filter.From, filter.To),
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParsePaymentStatus(filter.PaymentStatus));

    public static QuotationsReportCriteria ToCriteria(this QuotationsReportFilter filter, TenantCalendar calendar) =>
        new(
            filter.TenantId,
            ReportPeriod.Of(calendar, filter.From, filter.To),
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParseQuotationStatus(filter.Status));

    public static PriceChangeReportCriteria ToCriteria(this PriceChangeReportFilter filter, TenantCalendar calendar) =>
        new(
            filter.TenantId,
            ReportPeriod.Of(calendar, filter.From, filter.To),
            filter.ProductId,
            filter.ChangedBy,
            ReportFilterParser.ParsePriceChangeField(filter.Field));

    public static CustomerReportCriteria ToCriteria(this CustomerReportFilter filter, TenantCalendar calendar) =>
        new(
            filter.TenantId,
            ReportPeriod.Of(calendar, filter.From, filter.To),
            filter.IsActive,
            filter.ClassificationId,
            filter.DepartmentId);
}
```

- [ ] **Step 6: Los cuatro listados piden el calendario**

En `ListOrdersReport.cs`, reemplaza:

```csharp
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListOrdersReportQuery, ReportPage<OrdersReportItemDto>>
```

por:

```csharp
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListOrdersReportQuery, ReportPage<OrdersReportItemDto>>
```

y reemplaza:

```csharp
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<OrdersReportItemDto>(items, total, page, pageSize);
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(calendar), page, pageSize, cancellationToken);

        return new ReportPage<OrdersReportItemDto>(items, total, page, pageSize);
```

En `ListQuotationsReport.cs`, reemplaza:

```csharp
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>
```

por:

```csharp
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>
```

y reemplaza:

```csharp
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<QuotationsReportItemDto>(items, total, page, pageSize);
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(calendar), page, pageSize, cancellationToken);

        return new ReportPage<QuotationsReportItemDto>(items, total, page, pageSize);
```

En `ListPriceChangeReport.cs`, reemplaza:

```csharp
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListPriceChangeReportQuery, ReportPage<PriceChangeReportItemDto>>
```

por:

```csharp
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListPriceChangeReportQuery, ReportPage<PriceChangeReportItemDto>>
```

y reemplaza:

```csharp
        var (rows, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var (rows, total) = await source.ListAsync(
            query.Filter.ToCriteria(calendar), page, pageSize, cancellationToken);
```

En `ListCustomerReport.cs`, reemplaza:

```csharp
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomerReportQuery, ReportPage<CustomerReportItemDto>>
```

por:

```csharp
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListCustomerReportQuery, ReportPage<CustomerReportItemDto>>
```

y reemplaza:

```csharp
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<CustomerReportItemDto>(items, total, page, pageSize);
```

por:

```csharp
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(calendar), page, pageSize, cancellationToken);

        return new ReportPage<CustomerReportItemDto>(items, total, page, pageSize);
```

- [ ] **Step 7: Los cuatro resúmenes y su ventana anterior**

En `GetOrdersReportSummary.cs`, reemplaza:

```csharp
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrdersReportSummaryQuery, OrdersReportSummaryDto>
```

por:

```csharp
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetOrdersReportSummaryQuery, OrdersReportSummaryDto>
```

reemplaza:

```csharp
        var criteria = query.Filter.ToCriteria();
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);
```

por:

```csharp
        // El rango y la ventana anterior se cortan con el mismo calendario (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);
```

reemplaza:

```csharp
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }
```

por:

```csharp
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, cancellationToken));
    }
```

y reemplaza:

```csharp
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        OrdersReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
```

por:

```csharp
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        OrdersReportFilter filter,
        OrdersReportCriteria criteria,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        // La ventana se calcula sobre las fechas del filtro y se corta en el día del tenant, igual
        // que la pedida.
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
```

En `QuotationsReportSummary.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetQuotationsReportSummaryQuery, QuotationsReportSummaryDto>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetQuotationsReportSummaryQuery, QuotationsReportSummaryDto>
```

reemplaza:

```csharp
        var criteria = query.Filter.ToCriteria();
        // "Hoy" en UTC, el mismo huso en el que se corta el rango de fechas: mezclar dos husos
        // dentro del mismo reporte pondría el borde de un tramo un día corrido del borde del
        // filtro.
        var options = new QuotationsSummaryOptions(
            ReportSummaryRules.RankSize,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
```

por:

```csharp
        // El rango y la ventana anterior se cortan con el mismo calendario (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        // "Hoy" todavía en UTC: el punto 6 de la spec lo pasa al día del tenant.
        var options = new QuotationsSummaryOptions(
            ReportSummaryRules.RankSize,
            DateOnly.FromDateTime(calendar.UtcNow.UtcDateTime),
```

reemplaza:

```csharp
            await SummarizePrecedingAsync(criteria, options, cancellationToken));
    }
```

por:

```csharp
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, options, cancellationToken));
    }
```

y reemplaza:

```csharp
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        QuotationsReportCriteria criteria,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
```

por:

```csharp
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        QuotationsReportFilter filter,
        QuotationsReportCriteria criteria,
        TenantCalendar calendar,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
```

En `PriceChangeReportSummary.cs`, reemplaza:

```csharp
/// <summary>
/// Sin reloj inyectado, a diferencia del de cotizaciones: no hay ningún tramo que dependa de qué
/// día es hoy. Un cambio de precio ya pasó — no vence.
/// </summary>
public sealed class GetPriceChangeReportSummaryHandler(
    IPriceChangeReportSource source,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<GetPriceChangeReportSummaryQuery, PriceChangeReportSummaryDto>
```

por:

```csharp
/// <summary>
/// A diferencia del de cotizaciones, ningún tramo depende de qué día es hoy: un cambio de precio ya
/// pasó, no vence. El calendario del tenant sólo corta el rango (spec 2026-09-17, punto 4).
/// </summary>
public sealed class GetPriceChangeReportSummaryHandler(
    IPriceChangeReportSource source,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetPriceChangeReportSummaryQuery, PriceChangeReportSummaryDto>
```

reemplaza:

```csharp
        var criteria = query.Filter.ToCriteria();
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new PriceChangeReportSummaryDto(
```

por:

```csharp
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new PriceChangeReportSummaryDto(
```

reemplaza:

```csharp
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }
```

por:

```csharp
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, cancellationToken));
    }
```

y reemplaza:

```csharp
    private async Task<PriceChangeComparisonDto?> SummarizePrecedingAsync(
        PriceChangeReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
```

por:

```csharp
    private async Task<PriceChangeComparisonDto?> SummarizePrecedingAsync(
        PriceChangeReportFilter filter,
        PriceChangeReportCriteria criteria,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
```

En `CustomerReportSummary.cs`, reemplaza:

```csharp
/// <summary>
/// Sin reloj inyectado, igual que el de cambios de precio: no hay ningún tramo que dependa de qué
/// día es hoy. Un alta ya pasó — no vence.
/// </summary>
public sealed class GetCustomerReportSummaryHandler(
    ICustomerReportSource source,
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<GetCustomerReportSummaryQuery, CustomerReportSummaryDto>
```

por:

```csharp
/// <summary>
/// Igual que el de cambios de precio, ningún tramo depende de qué día es hoy: un alta ya pasó, no
/// vence. El calendario del tenant sólo corta el rango (spec 2026-09-17, punto 4).
/// </summary>
public sealed class GetCustomerReportSummaryHandler(
    ICustomerReportSource source,
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetCustomerReportSummaryQuery, CustomerReportSummaryDto>
```

reemplaza:

```csharp
        var criteria = query.Filter.ToCriteria();
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new CustomerReportSummaryDto(
```

por:

```csharp
        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var criteria = query.Filter.ToCriteria(calendar);
        var current = await source.SummarizeAsync(
            criteria, ReportSummaryRules.RankSize, cancellationToken);

        return new CustomerReportSummaryDto(
```

reemplaza:

```csharp
            await SummarizePrecedingAsync(criteria, cancellationToken));
    }
```

por:

```csharp
            await SummarizePrecedingAsync(query.Filter, criteria, calendar, cancellationToken));
    }
```

y reemplaza:

```csharp
    private async Task<CustomerComparisonDto?> SummarizePrecedingAsync(
        CustomerReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
```

por:

```csharp
    private async Task<CustomerComparisonDto?> SummarizePrecedingAsync(
        CustomerReportFilter filter,
        CustomerReportCriteria criteria,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(filter.From, filter.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { Period = ReportPeriod.Of(calendar, window.From, window.To) },
```

Si el build marca `using BuildingBlocks.Application;` sin uso en `QuotationsReportSummary.cs`, no lo quites: sigue haciendo falta por `IQuery`/`IQueryHandler`.

- [ ] **Step 8: Los orígenes comparan instantes y `ReportDateRange` se va**

En `src/Bootstrapper/ReportingLookups.cs`, borra desde la línea en blanco que sigue a `internal sealed record ReportingClientRef(string Name, string Cuc);` hasta el final del archivo, es decir, este bloque completo:

```csharp

/// <summary>
/// Traduce un rango de fechas calendarias al rango de instantes con el que se consulta.
///
/// El limite superior es **exclusivo al dia siguiente** y no <c>&lt;=</c> sobre el mismo dia: el
/// contrato dice "inclusive (whole day)", y un <c>&lt;= to</c> contra una columna
/// <c>timestamptz</c> deja afuera todo lo que paso despues de la medianoche del ultimo dia.
///
/// En UTC, que es como estan guardadas las columnas. Un tenant en America/Bogota vera el corte
/// del dia en UTC y no en su huso; alinearlo al huso del tenant es una decision de producto que
/// el contrato no toma, asi que no se inventa aca.
/// </summary>
internal static class ReportDateRange
{
    public static DateTimeOffset InclusiveStart(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateTimeOffset ExclusiveEnd(DateOnly date) =>
        new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
```

En `OrdersReportSource.cs`, reemplaza:

```csharp
        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            orders = orders.Where(order => order.ConvertedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            orders = orders.Where(order => order.ConvertedAt < end);
        }
```

por:

```csharp
        // Instantes ya cortados en el día del tenant (spec 2026-09-17, punto 4): acá no se decide huso.
        if (criteria.Period.Start is { } start)
        {
            orders = orders.Where(order => order.ConvertedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            orders = orders.Where(order => order.ConvertedAt < end);
        }
```

y reemplaza:

```csharp
        // La serie mensual va en **UTC**, el mismo huso en el que ReportDateRange corta el rango.
        // Agrupar en el huso de la sesion de PostgreSQL pondria un pedido del 1 de enero en
        // diciembre para un tenant en America/Bogota, y ademas haria que el resultado dependiera
        // de una configuracion de conexion en vez del dato.
```

por:

```csharp
        // La serie mensual todavía va en UTC: el punto 5 de la spec 2026-09-17 la pasa al huso del
        // tenant. Agrupar en el huso de la sesión de PostgreSQL haría que el resultado dependiera
        // de una configuración de conexión en vez del dato.
```

En `QuotationsReportSource.cs`, reemplaza:

```csharp
        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            query = query.Where(quotation => quotation.CreatedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            query = query.Where(quotation => quotation.CreatedAt < end);
        }
```

por:

```csharp
        // Instantes ya cortados en el día del tenant (spec 2026-09-17, punto 4): acá no se decide huso.
        if (criteria.Period.Start is { } start)
        {
            query = query.Where(quotation => quotation.CreatedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            query = query.Where(quotation => quotation.CreatedAt < end);
        }
```

y reemplaza:

```csharp
    /// <summary>La serie mensual por fecha de creacion, en UTC — mismo huso en el que
    /// <see cref="ReportDateRange"/> corta el rango. Solo vuelven los meses con cotizaciones.</summary>
```

por:

```csharp
    /// <summary>La serie mensual por fecha de creación, todavía en UTC: el punto 5 de la spec
    /// 2026-09-17 la pasa al huso del tenant. Sólo vuelven los meses con cotizaciones.</summary>
```

En `CustomerReportSource.cs`, reemplaza:

```csharp
        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            query = query.Where(customer => customer.CreatedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            query = query.Where(customer => customer.CreatedAt < end);
        }
```

por:

```csharp
        if (criteria.Period.Start is { } start)
        {
            query = query.Where(customer => customer.CreatedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            query = query.Where(customer => customer.CreatedAt < end);
        }
```

y reemplaza:

```csharp
    /// Las altas por mes, en **UTC** — el mismo huso en el que <c>ReportDateRange</c> corta el
    /// rango. Agrupar en el huso de la sesion de PostgreSQL pondria un alta del 1 de enero en
    /// diciembre para un tenant en America/Bogota.
```

por:

```csharp
    /// Las altas por mes, todavía en UTC: el punto 5 de la spec 2026-09-17 las pasa al huso del
    /// tenant.
```

En `PriceChangeReportSource.cs`, reemplaza:

```csharp
        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            changes = changes.Where(change => change.ChangedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            changes = changes.Where(change => change.ChangedAt < end);
        }
```

por:

```csharp
        if (criteria.Period.Start is { } start)
        {
            changes = changes.Where(change => change.ChangedAt >= start);
        }

        if (criteria.Period.EndExclusive is { } end)
        {
            changes = changes.Where(change => change.ChangedAt < end);
        }
```

y reemplaza:

```csharp
    /// La serie mensual por fecha del cambio, en **UTC** — el mismo huso en el que
    /// <c>ReportDateRange</c> corta el rango. Agrupar en el huso de la sesion de PostgreSQL
    /// pondria un cambio del 1 de enero en diciembre para un tenant en America/Bogota.
```

por:

```csharp
    /// La serie mensual por fecha del cambio, todavía en UTC: el punto 5 de la spec 2026-09-17 la
    /// pasa al huso del tenant.
```

En `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportComparisonWindowTests.cs:10`, reemplaza `inclusivas igual que en el filtro (<c>ReportDateRange</c>).` por `inclusivas igual que en el filtro (<c>ReportPeriod</c>).`.

- [ ] **Step 9: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-ChildItem -Recurse -Filter *.cs -Path src\Bootstrapper, src\Modules\Reporting, tests\Modules\Reporting | Select-String -Pattern "ReportDateRange"
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests/Modules.Reporting.UnitTests.csproj
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `Select-String` sin salida; build con 0 errores y 0 advertencias; las tres suites en verde, `ListingCutsTheRangeAtTheTenantsLocalMidnights` y `TheDateRangeIsCutAtTheTenantsLocalMidnight` incluidas.

- [ ] **Step 10: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Reporting/Modules.Reporting.Application src/Bootstrapper/ReportingLookups.cs src/Bootstrapper/OrdersReportSource.cs src/Bootstrapper/QuotationsReportSource.cs src/Bootstrapper/CustomerReportSource.cs src/Bootstrapper/PriceChangeReportSource.cs tests/Modules/Reporting
```

- [ ] **Step 11: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Reporting/Modules.Reporting.Application/ReportingFilters.cs src/Modules/Reporting/Modules.Reporting.Application/ListOrdersReport.cs src/Modules/Reporting/Modules.Reporting.Application/ListQuotationsReport.cs src/Modules/Reporting/Modules.Reporting.Application/ListPriceChangeReport.cs src/Modules/Reporting/Modules.Reporting.Application/ListCustomerReport.cs src/Modules/Reporting/Modules.Reporting.Application/GetOrdersReportSummary.cs src/Modules/Reporting/Modules.Reporting.Application/QuotationsReportSummary.cs src/Modules/Reporting/Modules.Reporting.Application/PriceChangeReportSummary.cs src/Modules/Reporting/Modules.Reporting.Application/CustomerReportSummary.cs src/Bootstrapper/ReportingLookups.cs src/Bootstrapper/OrdersReportSource.cs src/Bootstrapper/QuotationsReportSource.cs src/Bootstrapper/CustomerReportSource.cs src/Bootstrapper/PriceChangeReportSource.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportingTestDoubles.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/OrdersReportHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/OrdersReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/QuotationsReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/PriceChangeReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/CustomerReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportComparisonWindowTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportApiTests.cs; git commit -m "fix(reporting): cortar los rangos de los reportes en el día del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 7: Series mensuales en el mes del tenant (punto 5)

La agrupación mensual de los cuatro reportes pasa al huso del tenant. **El primer paso es un spike**: Npgsql 10.0.3 no tiene `EF.Functions.AtTimeZone` (hallazgo 1), y lo que hay que averiguar es si traduce `TimeZoneInfo.ConvertTimeBySystemTimeZoneId` sobre la columna dentro de un `GroupBy`. El resultado elige una de tres implementaciones, todas escritas abajo.

**Files:**
- Create (temporal, **no se commitea**): `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/MonthlyGroupingTranslationSpikeTests.cs`
- Modify: `src/Bootstrapper/OrdersReportSource.cs:97-124`, `QuotationsReportSource.cs:71,84-112`, `CustomerReportSource.cs:64,74-104`, `PriceChangeReportSource.cs:81,91-120`
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs`, `QuotationsReportSummaryApiTests.cs`, `PriceChangeReportSummaryApiTests.cs` (una prueba nueva en cada uno) y `CustomerReportSummaryApiTests.cs:8-17,21-24,50-54` (reescrita)

**Interfaces:**
- Consumes: `ReportPeriod.TimeZone` (Task 6); `ReportingApiHarness.NewYearsEveInBogota` y `QepApiFactory(string, DateTimeOffset?)` (Task 6).
- Produces: nada público. Las firmas privadas pasan a `SummarizeByMonthAsync(IQueryable<Quotation> rows, TimeZoneInfo timeZone, CancellationToken)`, `SummarizeByMonthAsync(IQueryable<Customer> filtered, TimeZoneInfo timeZone, CancellationToken)` y `SummarizeByMonthAsync(IQueryable<ProductPriceChange> changes, TimeZoneInfo timeZone, CancellationToken)`.

- [ ] **Step 1: El spike de traducción**

Crea `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/MonthlyGroupingTranslationSpikeTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Spike de la spec 2026-09-17, punto 5: ¿Npgsql traduce el cambio de huso sobre una columna
/// <c>timestamptz</c> dentro de un <c>GROUP BY</c>? Se borra al terminar la Task 7; no se commitea.
/// </summary>
public sealed class MonthlyGroupingTranslationSpikeTests
{
    // Variante A: sobre la propiedad DateTimeOffset tal cual.
    [Fact]
    public async Task VariantAConvertsTheDateTimeOffsetColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenantId = await SeedOneOrderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var timeZoneId = "America/Bogota";

        var query = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId)
            .GroupBy(order => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(order.ConvertedAt, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(order.ConvertedAt, timeZoneId).Month,
            })
            .Select(group => new { group.Key.Year, group.Key.Month, Count = group.Count() });

        Assert.Contains("AT TIME ZONE", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        var month = Assert.Single(await query.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal((2026, 12, 1), (month.Year, month.Month, month.Count));
    }

    // Variante B: sobre .UtcDateTime, que es lo que la serie usa hoy y EF ya sabe traducir.
    [Fact]
    public async Task VariantBConvertsTheUtcDateTimeOfTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenantId = await SeedOneOrderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var timeZoneId = "America/Bogota";

        var query = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId)
            .GroupBy(order => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(order.ConvertedAt.UtcDateTime, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(order.ConvertedAt.UtcDateTime, timeZoneId).Month,
            })
            .Select(group => new { group.Key.Year, group.Key.Month, Count = group.Count() });

        Assert.Contains("AT TIME ZONE", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        var month = Assert.Single(await query.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal((2026, 12, 1), (month.Year, month.Month, month.Count));
    }

    private static async Task<Guid> SeedOneOrderAsync(QepApiFactory factory)
    {
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);
        return tenant.TenantId;
    }
}
```

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~MonthlyGroupingTranslationSpikeTests"
```

Pega la salida literal en el handoff (incluido el SQL si alguna variante falla en la aserción de `AT TIME ZONE`).

- [ ] **Step 2: Decidir la implementación y borrar el spike**

Regla de decisión, en este orden:

1. **Pasó la variante A** → implementación **SQL**, tal como está escrita en el Step 5.
2. **Falló la A y pasó la B** → implementación **SQL** del Step 5, reemplazando en los cuatro orígenes el texto `.ConvertedAt, timeZoneId)` por `.ConvertedAt.UtcDateTime, timeZoneId)`, `.CreatedAt, timeZoneId)` por `.CreatedAt.UtcDateTime, timeZoneId)` y `.ChangedAt, timeZoneId)` por `.ChangedAt.UtcDateTime, timeZoneId)`.
3. **Fallaron las dos** (`InvalidOperationException … could not be translated`, un `PostgresException` de `GROUP BY`, o un mes que no es diciembre) → implementación **en memoria** del Step 6. El volumen es de un solo tenant y un período acotado (spec, punto 5).

Anota en el handoff qué rama elegiste y por qué. Después borra el spike:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Remove-Item tests\Modules\Reporting\Modules.Reporting.IntegrationTests\MonthlyGroupingTranslationSpikeTests.cs
git status --short
```

Esperado: `git status` limpio.

- [ ] **Step 3: Escribir las pruebas que fallan**

En `OrdersReportSummaryApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task SummaryReturnsZerosWhenTheTenantHasNoOrders()
```

por:

```csharp
    // Spec 2026-09-17, punto 5: el pedido convertido el 31 de diciembre a las 23:00 de Bogotá —ya
    // enero en UTC— cuenta en la serie de diciembre del tenant.
    [Fact]
    public async Task TheMonthlySeriesGroupsByTheTenantsLocalMonth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);

        var summary = await client.GetFromJsonAsync<OrdersReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.OrderCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12), (month.Year, month.Month));
        Assert.Equal(quotation.Total, month.Total);
    }

    [Fact]
    public async Task SummaryReturnsZerosWhenTheTenantHasNoOrders()
```

En `QuotationsReportSummaryApiTests.cs`, reemplaza:

```csharp
public sealed class QuotationsReportSummaryApiTests
{
```

por:

```csharp
public sealed class QuotationsReportSummaryApiTests
{
    // Spec 2026-09-17, punto 5: la cotización creada el 31 de diciembre a las 23:00 de Bogotá —ya
    // enero en UTC— cuenta en la serie de diciembre del tenant.
    [Fact]
    public async Task TheMonthlySeriesGroupsByTheTenantsLocalMonth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);

        var summary = await client.GetFromJsonAsync<QuotationsReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.QuotationCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12), (month.Year, month.Month));
        Assert.Equal(quotation.Total, month.Total);
    }

```

En `PriceChangeReportSummaryApiTests.cs`, reemplaza:

```csharp
    [Fact]
    public async Task SummaryCountsTheChangesTheirDirectionAndTheProductsTouched()
```

por:

```csharp
    // Spec 2026-09-17, punto 5: el cambio de precio del 31 de diciembre a las 23:00 de Bogotá —ya
    // enero en UTC— cuenta en la serie de diciembre del tenant.
    [Fact]
    public async Task TheMonthlySeriesGroupsByTheTenantsLocalMonth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var productId = await CreateProductAsync(client, tenant.TenantId, baseCop: 100_000m);
        await ChangeProductBaseCopAsync(client, tenant.TenantId, productId, 120_000m);

        var summary = await client.GetFromJsonAsync<PriceChangeReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/price-changes/summary?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.ChangeCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12, 1), (month.Year, month.Month, month.Count));
    }

    [Fact]
    public async Task SummaryCountsTheChangesTheirDirectionAndTheProductsTouched()
```

En `CustomerReportSummaryApiTests.cs`, reemplaza:

```csharp
/// - La serie mensual agrupa por <c>CreatedAt</c> en UTC, con la misma expresion que EF tiene que
///   saber traducir a un <c>date_part</c>.
```

por:

```csharp
/// - La serie mensual agrupa por <c>CreatedAt</c> en el mes del tenant (spec 2026-09-17, punto 5),
///   con una expresión que EF tiene que saber traducir, o con la proyección que la reemplaza.
```

reemplaza:

```csharp
    [Fact]
    public async Task SummaryCountsTheCustomersAndHowManyAreActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
```

por:

```csharp
    // Con el reloj en el 31 de diciembre a las 23:00 de Bogotá: la serie es la del mes del tenant, y
    // con DateTime.UtcNow la prueba además dependía de a qué hora corría.
    [Fact]
    public async Task SummaryCountsTheCustomersAndHowManyAreActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
```

y reemplaza:

```csharp
        // Los dos se dieron de alta hoy, asi que la serie tiene un solo mes con los dos.
        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);
        Assert.Equal(DateTime.UtcNow.Year, month.Year);
        Assert.Equal(DateTime.UtcNow.Month, month.Month);
```

por:

```csharp
        // Los dos se dieron de alta el 31 de diciembre local: un solo mes, diciembre de 2026.
        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);
        Assert.Equal(2026, month.Year);
        Assert.Equal(12, month.Month);
```

- [ ] **Step 4: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~TheMonthlySeriesGroupsByTheTenantsLocalMonth|FullyQualifiedName~CustomerReportSummaryApiTests.SummaryCountsTheCustomersAndHowManyAreActive"
```

Esperado: las cuatro fallan con `Assert.Equal() Failure: Values differ`; en las tres nuevas `Expected: (2026, 12…)` y `Actual: (2027, 1…)`, y en la de clientes `Expected: 2026`, `Actual: 2027`.

- [ ] **Step 5 (rama SQL, si el spike pasó con A o B): agrupar con `AT TIME ZONE`**

Salta este paso si la regla del Step 2 eligió memoria.

En `OrdersReportSource.cs`, reemplaza:

```csharp
        // La serie mensual todavía va en UTC: el punto 5 de la spec 2026-09-17 la pasa al huso del
        // tenant. Agrupar en el huso de la sesión de PostgreSQL haría que el resultado dependiera
        // de una configuración de conexión en vez del dato.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var monthRows = await joined
            .GroupBy(row => new
            {
                row.order.ConvertedAt.UtcDateTime.Year,
                row.order.ConvertedAt.UtcDateTime.Month,
            })
```

por:

```csharp
        // La serie mensual va en el mes del tenant (spec 2026-09-17, punto 5): el pedido de las 20:00
        // del último día en Bogotá es de ese mes, aunque en UTC ya sea el siguiente. Se agrupa en SQL
        // con `AT TIME ZONE` y el ID IANA del tenant —lo que Npgsql traduce desde
        // TimeZoneInfo.ConvertTimeBySystemTimeZoneId (spike de la Task 7)—, nunca en el huso de la
        // sesión de PostgreSQL, que haría depender el resultado de la conexión.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var timeZoneId = criteria.Period.TimeZone.Id;
        var monthRows = await joined
            .GroupBy(row => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.order.ConvertedAt, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(row.order.ConvertedAt, timeZoneId).Month,
            })
```

En `QuotationsReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(rows, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(rows, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza:

```csharp
    /// <summary>La serie mensual por fecha de creación, todavía en UTC: el punto 5 de la spec
    /// 2026-09-17 la pasa al huso del tenant. Sólo vuelven los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        CancellationToken cancellationToken)
    {
        var months = await rows
            .GroupBy(quotation => new
            {
                quotation.CreatedAt.UtcDateTime.Year,
                quotation.CreatedAt.UtcDateTime.Month,
            })
```

por:

```csharp
    /// <summary>La serie mensual por fecha de creación en el mes del tenant (spec 2026-09-17, punto
    /// 5), agrupada en SQL con <c>AT TIME ZONE</c>. Ver <see cref="OrdersReportSource"/>. Sólo vuelven
    /// los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var timeZoneId = timeZone.Id;
        var months = await rows
            .GroupBy(quotation => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(quotation.CreatedAt, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(quotation.CreatedAt, timeZoneId).Month,
            })
```

En `CustomerReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(filtered, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(filtered, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza:

```csharp
    /// Las altas por mes, todavía en UTC: el punto 5 de la spec 2026-09-17 las pasa al huso del
    /// tenant.
    ///
    /// Solo vienen los meses con altas: rellenar los huecos con cero depende del rango que el eje
    /// dibuje, asi que es del frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<Customer> filtered,
        CancellationToken cancellationToken)
    {
        var months = await filtered
            .GroupBy(customer => new
            {
                customer.CreatedAt.UtcDateTime.Year,
                customer.CreatedAt.UtcDateTime.Month,
            })
```

por:

```csharp
    /// Las altas por mes en el mes del tenant (spec 2026-09-17, punto 5), agrupadas en SQL con
    /// <c>AT TIME ZONE</c>. Ver <see cref="OrdersReportSource"/>.
    ///
    /// Solo vienen los meses con altas: rellenar los huecos con cero depende del rango que el eje
    /// dibuje, asi que es del frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<Customer> filtered,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var timeZoneId = timeZone.Id;
        var months = await filtered
            .GroupBy(customer => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(customer.CreatedAt, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(customer.CreatedAt, timeZoneId).Month,
            })
```

En `PriceChangeReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(changes, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(changes, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza:

```csharp
    /// La serie mensual por fecha del cambio, todavía en UTC: el punto 5 de la spec 2026-09-17 la
    /// pasa al huso del tenant.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        CancellationToken cancellationToken)
    {
        var months = await changes
            .GroupBy(change => new
            {
                change.ChangedAt.UtcDateTime.Year,
                change.ChangedAt.UtcDateTime.Month,
            })
```

por:

```csharp
    /// La serie mensual por fecha del cambio en el mes del tenant (spec 2026-09-17, punto 5),
    /// agrupada en SQL con <c>AT TIME ZONE</c>. Ver <see cref="OrdersReportSource"/>.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var timeZoneId = timeZone.Id;
        var months = await changes
            .GroupBy(change => new
            {
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(change.ChangedAt, timeZoneId).Year,
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(change.ChangedAt, timeZoneId).Month,
            })
```

Si el spike eligió la variante B, aplica ahora los tres reemplazos de `.UtcDateTime` del Step 2 en los cuatro archivos, y en el comentario de `OrdersReportSource.cs` reemplaza `TimeZoneInfo.ConvertTimeBySystemTimeZoneId (spike de la Task 7)` por `TimeZoneInfo.ConvertTimeBySystemTimeZoneId sobre UtcDateTime (spike de la Task 7)`.

- [ ] **Step 6 (rama en memoria, si fallaron A y B): proyectar y agrupar con la hora local**

Salta este paso si la regla del Step 2 eligió SQL.

En `OrdersReportSource.cs`, reemplaza el bloque entero:

```csharp
        // La serie mensual todavía va en UTC: el punto 5 de la spec 2026-09-17 la pasa al huso del
        // tenant. Agrupar en el huso de la sesión de PostgreSQL haría que el resultado dependiera
        // de una configuración de conexión en vez del dato.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var monthRows = await joined
            .GroupBy(row => new
            {
                row.order.ConvertedAt.UtcDateTime.Year,
                row.order.ConvertedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
                Total = group.Sum(row => row.quotation.Total),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        var monthly = monthRows
            .Select(point => new ReportMonthlyPointDto(
                point.Year, point.Month, point.Count, point.Total))
            .ToArray();
```

por:

```csharp
        // La serie mensual va en el mes del tenant (spec 2026-09-17, punto 5): el pedido de las 20:00
        // del último día en Bogotá es de ese mes, aunque en UTC ya sea el siguiente. Npgsql no traduce
        // el cambio de huso dentro del GROUP BY (spike de la Task 7), así que se proyectan sólo el
        // instante y el total —un tenant, un período acotado— y se agrupa en memoria con la hora
        // local. Nunca en el huso de la sesión de PostgreSQL, que haría depender el resultado de la
        // conexión.
        //
        // Solo vuelven los meses con pedidos: rellenar los huecos con cero depende del rango que
        // el eje dibuje, asi que es del frontend.
        var timeZone = criteria.Period.TimeZone;
        var conversions = await joined
            .Select(row => new { row.order.ConvertedAt, row.quotation.Total })
            .ToListAsync(cancellationToken);

        var monthly = conversions
            .GroupBy(row =>
            {
                var local = TimeZoneInfo.ConvertTime(row.ConvertedAt, timeZone);
                return (local.Year, local.Month);
            })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => new ReportMonthlyPointDto(
                group.Key.Year, group.Key.Month, group.Count(), group.Sum(row => row.Total)))
            .ToArray();
```

En `QuotationsReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(rows, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(rows, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza el método entero:

```csharp
    /// <summary>La serie mensual por fecha de creación, todavía en UTC: el punto 5 de la spec
    /// 2026-09-17 la pasa al huso del tenant. Sólo vuelven los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        CancellationToken cancellationToken)
    {
        var months = await rows
            .GroupBy(quotation => new
            {
                quotation.CreatedAt.UtcDateTime.Year,
                quotation.CreatedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
                Total = group.Sum(quotation => quotation.Total),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportMonthlyPointDto(
                point.Year, point.Month, point.Count, point.Total))
            .ToArray();
    }
```

por:

```csharp
    /// <summary>La serie mensual por fecha de creación en el mes del tenant (spec 2026-09-17, punto
    /// 5), agrupada en memoria sobre el instante y el total. Ver <see cref="OrdersReportSource"/>.
    /// Sólo vuelven los meses con cotizaciones.</summary>
    private static async Task<IReadOnlyList<ReportMonthlyPointDto>> SummarizeByMonthAsync(
        IQueryable<Quotation> rows,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var creations = await rows
            .Select(quotation => new { quotation.CreatedAt, quotation.Total })
            .ToListAsync(cancellationToken);

        return creations
            .GroupBy(row =>
            {
                var local = TimeZoneInfo.ConvertTime(row.CreatedAt, timeZone);
                return (local.Year, local.Month);
            })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => new ReportMonthlyPointDto(
                group.Key.Year, group.Key.Month, group.Count(), group.Sum(row => row.Total)))
            .ToArray();
    }
```

En `CustomerReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(filtered, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(filtered, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza el método entero (desde su `/// <summary>`):

```csharp
    /// <summary>
    /// Las altas por mes, todavía en UTC: el punto 5 de la spec 2026-09-17 las pasa al huso del
    /// tenant.
    ///
    /// Solo vienen los meses con altas: rellenar los huecos con cero depende del rango que el eje
    /// dibuje, asi que es del frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<Customer> filtered,
        CancellationToken cancellationToken)
    {
        var months = await filtered
            .GroupBy(customer => new
            {
                customer.CreatedAt.UtcDateTime.Year,
                customer.CreatedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportCountPointDto(point.Year, point.Month, point.Count))
            .ToArray();
    }
```

por:

```csharp
    /// <summary>
    /// Las altas por mes en el mes del tenant (spec 2026-09-17, punto 5), agrupadas en memoria sobre
    /// el instante de alta. Ver <see cref="OrdersReportSource"/>.
    ///
    /// Solo vienen los meses con altas: rellenar los huecos con cero depende del rango que el eje
    /// dibuje, asi que es del frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<Customer> filtered,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var creations = await filtered
            .Select(customer => customer.CreatedAt)
            .ToListAsync(cancellationToken);

        return creations
            .GroupBy(createdAt =>
            {
                var local = TimeZoneInfo.ConvertTime(createdAt, timeZone);
                return (local.Year, local.Month);
            })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => new ReportCountPointDto(group.Key.Year, group.Key.Month, group.Count()))
            .ToArray();
    }
```

En `PriceChangeReportSource.cs`, reemplaza:

```csharp
        var monthly = await SummarizeByMonthAsync(changes, cancellationToken);
```

por:

```csharp
        var monthly = await SummarizeByMonthAsync(changes, criteria.Period.TimeZone, cancellationToken);
```

y reemplaza el método entero (desde su `/// <summary>`):

```csharp
    /// <summary>
    /// La serie mensual por fecha del cambio, todavía en UTC: el punto 5 de la spec 2026-09-17 la
    /// pasa al huso del tenant.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        CancellationToken cancellationToken)
    {
        var months = await changes
            .GroupBy(change => new
            {
                change.ChangedAt.UtcDateTime.Year,
                change.ChangedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportCountPointDto(point.Year, point.Month, point.Count))
            .ToArray();
    }
```

por:

```csharp
    /// <summary>
    /// La serie mensual por fecha del cambio en el mes del tenant (spec 2026-09-17, punto 5),
    /// agrupada en memoria sobre el instante del cambio. Ver <see cref="OrdersReportSource"/>.
    ///
    /// Es un conteo y no un monto: ver <c>PriceChangeReportSummaryDto</c>.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<ProductPriceChange> changes,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var instants = await changes
            .Select(change => change.ChangedAt)
            .ToListAsync(cancellationToken);

        return instants
            .GroupBy(changedAt =>
            {
                var local = TimeZoneInfo.ConvertTime(changedAt, timeZone);
                return (local.Year, local.Month);
            })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => new ReportCountPointDto(group.Key.Year, group.Key.Month, group.Count()))
            .ToArray();
    }
```

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj
```

Esperado: build con 0 errores y 0 advertencias; la suite de integración de Reporting en verde, las cuatro pruebas del Step 3 incluidas y las de resumen existentes (que agregan sin reloj fijo) también.

- [ ] **Step 8: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Bootstrapper/OrdersReportSource.cs src/Bootstrapper/QuotationsReportSource.cs src/Bootstrapper/CustomerReportSource.cs src/Bootstrapper/PriceChangeReportSource.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests
```

- [ ] **Step 9: Commit**

El mensaje dice qué rama eligió el spike. Si fue SQL con A, deja `-m "Agrupa en SQL con AT TIME ZONE sobre la columna (spike, variante A)."`; con B, `-m "Agrupa en SQL con AT TIME ZONE sobre UtcDateTime (spike, variante B)."`; en memoria, `-m "Agrupa en memoria: Npgsql no tradujo el cambio de huso en el GROUP BY (spike)."`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Test-Path tests\Modules\Reporting\Modules.Reporting.IntegrationTests\MonthlyGroupingTranslationSpikeTests.cs
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Bootstrapper/OrdersReportSource.cs src/Bootstrapper/QuotationsReportSource.cs src/Bootstrapper/CustomerReportSource.cs src/Bootstrapper/PriceChangeReportSource.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/OrdersReportSummaryApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/QuotationsReportSummaryApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/PriceChangeReportSummaryApiTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/CustomerReportSummaryApiTests.cs; git commit -m "fix(reporting): series mensuales en el mes del tenant" -m "Agrupa en SQL con AT TIME ZONE sobre la columna (spike, variante A)."
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `Test-Path` devuelve `False` (el spike no existe) antes del commit.

---

### Task 8: Vencidas y por vencer con el hoy del tenant (punto 6)

**Files:**
- Modify: `src/Modules/Reporting/Modules.Reporting.Application/QuotationsReportSummary.cs` (el bloque de `QuotationsSummaryOptions` que dejó Task 6)
- Test: `tests/Modules/Reporting/Modules.Reporting.UnitTests/QuotationsReportSummaryHandlerTests.cs:22-25,94-111` (se renombra y se mueve a la frontera)
- Test: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/QuotationsReportSummaryApiTests.cs` (prueba nueva)

**Interfaces:**
- Consumes: `TenantCalendar.Today` (Task 1), `FixedTenantClock` de Reporting (Task 6), `ReportingApiHarness.NewYearsEveInBogota` (Task 6).
- Produces: `QuotationsSummaryOptions.Today` pasa a ser `calendar.Today`. Ninguna firma cambia.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationsReportSummaryHandlerTests.cs`, reemplaza:

```csharp
    /// <summary>Media tarde en UTC: si el handler tomara la fecha local en vez de la UTC, en un
    /// huso al oeste esto seria todavia el dia anterior.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 14, 30, 0, TimeSpan.Zero);
```

por:

```csharp
    /// <summary>Un instante cualquiera para las pruebas que no miran el hoy.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 14, 30, 0, TimeSpan.Zero);

    /// <summary>31 de diciembre de 2026 a las 23:00 en Bogotá: en UTC ya es 1 de enero.</summary>
    private static readonly DateTimeOffset NewYearsEveInBogota =
        new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);
```

y reemplaza la prueba entera:

```csharp
    /// <summary>El "hoy" que resuelve los tramos sale del reloj inyectado y en UTC — no de
    /// <c>DateTime.Today</c>, que dependeria del huso de la maquina que corre la API.</summary>
    [Fact]
    public async Task SummarizingResolvesTodayFromTheClockInUtc()
    {
        var source = new FakeQuotationsReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        var options = Assert.Single(source.SummarizedOptions);
        Assert.Equal(new DateOnly(2026, 9, 3), options.Today);
```

por:

```csharp
    /// <summary>El "hoy" de los tramos de vigencia y de la cola de vencimientos es el día del tenant
    /// (spec 2026-09-17, punto 6), no el de UTC ni el de la máquina que corre la API: el 31 de
    /// diciembre a las 23:00 en Bogotá, una cotización que vence el 31 todavía no está vencida.</summary>
    [Fact]
    public async Task SummarizingResolvesTodayInTheTenantsTimeZone()
    {
        var source = new FakeQuotationsReportSource();
        var handler = new GetQuotationsReportSummaryHandler(
            source,
            new QuotationsReportFilterValidator(),
            new FakeExecutionContext(Tenant, ReportingPermissions.QuotationRead),
            new FixedTenantClock(NewYearsEveInBogota));

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        var options = Assert.Single(source.SummarizedOptions);
        Assert.Equal(new DateOnly(2026, 12, 31), options.Today);
```

En `QuotationsReportSummaryApiTests.cs`, reemplaza:

```csharp
public sealed class QuotationsReportSummaryApiTests
{
```

por:

```csharp
public sealed class QuotationsReportSummaryApiTests
{
    // Spec 2026-09-17, punto 6: el 31 de diciembre a las 23:00 en Bogotá, la cotización que vence el
    // 31 vence hoy —por vencer, cero días— y no cuenta como vencida.
    [Fact]
    public async Task AQuotationDueOnTheTenantsTodayIsNotExpired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId, validUntil: new DateOnly(2026, 12, 31));

        var summary = await client.GetFromJsonAsync<QuotationsReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Validity.Expired.Count);
        Assert.Equal(1, summary.Validity.WithinSevenDays.Count);
        var expiring = Assert.Single(summary.Expiring);
        Assert.Equal(quotation.Id, expiring.QuotationId);
        Assert.Equal(0, expiring.DaysLeft);
    }

```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests/Modules.Reporting.UnitTests.csproj --filter "FullyQualifiedName~SummarizingResolvesTodayInTheTenantsTimeZone"
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~AQuotationDueOnTheTenantsTodayIsNotExpired"
```

Esperado: la unitaria falla con `Assert.Equal() Failure: Values differ`, `Expected: 2026-12-31`, `Actual: 2027-01-01`; la de integración con `Expected: 0`, `Actual: 1` en `Validity.Expired.Count`.

- [ ] **Step 3: El hoy del tenant**

En `QuotationsReportSummary.cs`, reemplaza:

```csharp
        // "Hoy" todavía en UTC: el punto 6 de la spec lo pasa al día del tenant.
        var options = new QuotationsSummaryOptions(
            ReportSummaryRules.RankSize,
            DateOnly.FromDateTime(calendar.UtcNow.UtcDateTime),
```

por:

```csharp
        // "Hoy" es el día del tenant, el mismo en el que se corta el rango (spec 2026-09-17, punto 6):
        // "vencidas", "por vencer" y DaysLeft no pueden adelantarse un día desde las 19:00 en Bogotá.
        var options = new QuotationsSummaryOptions(
            ReportSummaryRules.RankSize,
            calendar.Today,
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests/Modules.Reporting.UnitTests.csproj
dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests/Modules.Reporting.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationsReportSummaryApiTests"
```

Esperado: todo en verde.

- [ ] **Step 5: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Reporting/Modules.Reporting.Application/QuotationsReportSummary.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/QuotationsReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/QuotationsReportSummaryApiTests.cs
```

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Reporting/Modules.Reporting.Application/QuotationsReportSummary.cs tests/Modules/Reporting/Modules.Reporting.UnitTests/QuotationsReportSummaryHandlerTests.cs tests/Modules/Reporting/Modules.Reporting.IntegrationTests/QuotationsReportSummaryApiTests.cs; git commit -m "fix(reporting): vencidas y por vencer con el hoy del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 9: La fecha de emisión del PDF en el día del tenant (punto 7)

`QuotationPdfDocumentMapper` manda la fecha de emisión ya local (`issuedOn: "yyyy-MM-dd"`) y `quotation.typ` deja de cortar un ISO con hora.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs:13-16`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs:1-48`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs:1-65`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:45-52,175`
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs:21`, `QuotationTemplateTests.cs:320,353` y una prueba nueva, `QuotationPdfDocumentMapperTests.cs` (todas las llamadas y una prueba nueva), `SendQuotationHandlerTests.cs:359-364`, `ExportQuotationPdfHandlerTests.cs:118-123`

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar.ToLocal` (Tasks 1-2); `FixedTenantClock` de Quotations (Task 5).
- Produces:
  - `QuotationPdfDocument(string QuotationNumber, DateOnly IssuedOn, DateOnly? ValidUntil, …)` — el segundo parámetro cambia de `DateTimeOffset CreatedAt` a `DateOnly IssuedOn`; el JSON a `qcode-pdf` lleva `issuedOn`.
  - `QuotationPdfDocumentMapper.From(QuotationResponse quotation, TenantCalendar calendar)`.
  - `QuotationPdfProvider(IQuotationRepository, IQuotationResponseComposer, IQuotationPdfRenderer, IQuotationPdfStorage, ITenantClock tenantClock)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationPdfDocumentMapperTests.cs`, reemplaza:

```csharp
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;
```

por:

```csharp
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;
```

reemplaza **todas** las apariciones de `QuotationPdfDocumentMapper.From(` por `Map(`, y después reemplaza:

```csharp
public sealed class QuotationPdfDocumentMapperTests
{
    [Fact]
    public void MapsTheHeaderAndTheTotals()
```

por:

```csharp
public sealed class QuotationPdfDocumentMapperTests
{
    // El calendario del tenant en Bogotá. El instante no importa para el mapeo: sólo el huso.
    private static readonly TenantCalendar Calendar = new(
        new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static QuotationPdfDocument Map(QuotationResponse quotation) =>
        QuotationPdfDocumentMapper.From(quotation, Calendar);

    // Spec 2026-09-17, punto 7: creada el 31 de diciembre a las 23:00 en Bogotá —1 de enero en UTC—,
    // el documento dice que se emitió el 31 de diciembre de 2026. La fecha viaja sin hora.
    [Fact]
    public void TheIssueDateIsTheTenantsLocalDate()
    {
        var document = Map(Response() with { CreatedAt = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero) });

        Assert.Equal(new DateOnly(2026, 12, 31), document.IssuedOn);
    }

    [Fact]
    public void MapsTheHeaderAndTheTotals()
```

En `QCodePdfRendererTests.cs`, reemplaza:

```csharp
        CreatedAt: new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero),
```

por:

```csharp
        IssuedOn: new DateOnly(2026, 9, 6),
```

En `QuotationTemplateTests.cs`, reemplaza **todas** las apariciones (dos) de:

```csharp
        CreatedAt: new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
```

por:

```csharp
        IssuedOn: new DateOnly(2026, 9, 10),
```

y reemplaza:

```csharp
    // Consumidor final no tiene contacto ni direccion: lo que lo identifica en la factura es el
    // NIT generico, y la plantilla lo imprime desde su propio campo.
```

por:

```csharp
    // Spec 2026-09-17, punto 7: la fecha de emisión llega ya en el día del tenant y sin hora, y la
    // plantilla la imprime tal cual. Cortar un ISO en la "T" es lo que hacía decir "1 de enero de
    // 2027" a un documento emitido el 31 de diciembre en Bogotá.
    [Fact]
    public async Task TheTemplatePrintsTheIssueDateFromItsLocalDate()
    {
        var (source, data) = await CapturedRequestAsync(Minimal() with { IssuedOn = new DateOnly(2026, 12, 31) });

        Assert.Equal("2026-12-31", data.GetProperty("issuedOn").GetString());
        Assert.Contains("fecha(data.issuedOn)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("split(\"T\")", source, StringComparison.Ordinal);
    }

    // Consumidor final no tiene contacto ni direccion: lo que lo identifica en la factura es el
    // NIT generico, y la plantilla lo imprime desde su propio campo.
```

En `SendQuotationHandlerTests.cs`, reemplaza:

```csharp
                new CountingPdfRenderer(),
                storage,
                new FixedClock(Now)),
```

por:

```csharp
                new CountingPdfRenderer(),
                storage,
                new FixedTenantClock(Now)),
```

En `ExportQuotationPdfHandlerTests.cs`, reemplaza:

```csharp
                renderer,
                storage,
                new FixedClock(Now)),
```

por:

```csharp
                renderer,
                storage,
                new FixedTenantClock(Now)),
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationPdfDocumentMapperTests|FullyQualifiedName~QuotationTemplateTests|FullyQualifiedName~QCodePdfRendererTests|FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~ExportQuotationPdfHandlerTests"
```

Esperado: falla la compilación con `error CS1739: The best overload for 'QuotationPdfDocument' does not have a parameter named 'IssuedOn'`, `CS1501: No overload for method 'From' takes 2 arguments` y `CS1503` por `FixedTenantClock` donde se espera `IClock`.

- [ ] **Step 3: El documento lleva la fecha local**

En `QuotationPdfDocument.cs`, reemplaza:

```csharp
public sealed record QuotationPdfDocument(
    string QuotationNumber,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
```

por:

```csharp
public sealed record QuotationPdfDocument(
    string QuotationNumber,
    /// <summary>La fecha de emisión en el día del tenant, sin hora (spec 2026-09-17, punto 7). La
    /// calcula el mapeo: la plantilla la imprime tal cual y no decide husos.</summary>
    DateOnly IssuedOn,
    DateOnly? ValidUntil,
```

En `QuotationPdfDocumentMapper.cs`, reemplaza:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;
```

por:

```csharp
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;
```

y reemplaza:

```csharp
    public static QuotationPdfDocument From(QuotationResponse quotation) =>
        new(
            quotation.QuotationNumber,
            quotation.CreatedAt,
            quotation.ValidUntil,
```

por:

```csharp
    /// <summary>El <paramref name="calendar"/> es el del tenant de la cotización: la fecha de emisión
    /// se imprime en su día, no en el de UTC (spec 2026-09-17, punto 7).</summary>
    public static QuotationPdfDocument From(QuotationResponse quotation, TenantCalendar calendar) =>
        new(
            quotation.QuotationNumber,
            DateOnly.FromDateTime(calendar.ToLocal(quotation.CreatedAt).DateTime),
            quotation.ValidUntil,
```

- [ ] **Step 4: El proveedor pide el calendario**

Reemplaza el contenido entero de `QuotationPdfProvider.cs` por:

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
    ITenantClock tenantClock)
    : IQuotationPdfProvider
{
    public async Task<QuotationPdf> EnsureCurrentAsync(
        Quotation quotation, CancellationToken cancellationToken)
    {
        var pdf = await repository.FindPdfAsync(
            quotation.TenantId, quotation.Id, cancellationToken);

        // Generar cuesta una llamada de red a `qcode-pdf` mas una subida a R2, y la mayoria de
        // los pedidos son de cotizaciones que nadie toco desde el anterior.
        if (pdf is not null && !pdf.IsStaleFor(quotation.Version))
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
        var content = await renderer.RenderAsync(
            QuotationPdfDocumentMapper.From(response, calendar), cancellationToken);
        var storageKey = await storage.SaveAsync(
            quotation.TenantId, quotation.Id, content, cancellationToken);

        var now = calendar.UtcNow;
        if (pdf is null)
        {
            pdf = QuotationPdf.Generate(
                quotation.Id, quotation.TenantId, storageKey, quotation.Version, now);
            repository.AddPdf(pdf);
        }
        else
        {
            pdf.Regenerate(storageKey, quotation.Version, now);
        }

        return pdf;
    }
}
```

- [ ] **Step 5: La plantilla deja de cortar en la `T`**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ`, reemplaza:

```
// Las fechas llegan en ISO porque el contrato es JSON; el documento lo lee una persona, así
// que se arma acá y no en C#. `createdAt` viene con hora y offset (`2026-09-06T10:00:00+00:00`)
// y `validUntil` sin hora: cortar en la "T" cubre las dos.
#let fecha(iso) = {
  let partes = iso.split("T").at(0).split("-")
```

por:

```
// Las fechas llegan en ISO porque el contrato es JSON; el documento lo lee una persona, así
// que se arma acá y no en C#. `issuedOn` y `validUntil` llegan sin hora (`2026-09-06`): la
// emisión ya viene en el día del tenant (spec 2026-09-17, punto 7), y no hay nada que cortar.
#let fecha(iso) = {
  let partes = iso.split("-")
```

y reemplaza:

```
      ..ficha("Emitida", fecha(data.createdAt)),
```

por:

```
      ..ficha("Emitida", fecha(data.issuedOn)),
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationSendVoidApiTests"
```

Esperado: todo en verde, `EveryFieldTheTemplateReadsExistsInThePayload` incluida (lee `data.issuedOn`). Si `typst` está instalado, las `TheTemplateCompiles…` compilan el documento de verdad; si no, se saltan fuera de CI.

- [ ] **Step 7: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs
```

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocumentMapper.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfProvider.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ tests/Modules/Quotations/Modules.Quotations.UnitTests/QCodePdfRendererTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportQuotationPdfHandlerTests.cs; git commit -m "fix(quotations): fecha de emisión del PDF en el día del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 10: Fechas y nombres de los Excel de cotizaciones y pedidos en la hora del tenant (punto 8a, primera mitad)

Las celdas «Fecha» se escriben `yyyy-MM-dd HH:mm` en la hora del tenant, sin offset, y el nombre del archivo usa la hora local. El calendario ya lo tienen los dos procesadores desde Task 5.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs:31-37`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs` (el lambda de filas, el nombre del archivo y `ToCells`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (lo mismo)
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs:1,35,107,111,194-223`, `OrdersExportProcessorTests.cs:1,41,248,302-326`
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs` (helper `LocalMinuteInBogota`), `QuotationExportApiTests.cs:224`, `OrderExportApiTests.cs:172`
- Test: una prueba nueva en cada archivo de pruebas de procesador

**Interfaces:**
- Consumes: el `TenantCalendar calendar` que los procesadores resuelven desde Task 5; `FixedTenantClock` (Task 5).
- Produces:
  - `ExportFileNames.For(string prefix, DateTimeOffset generatedAtLocal)`: escribe el reloj del instante que recibe, sin pasarlo a UTC.
  - `QuotationsApiHarness.LocalMinuteInBogota(DateTimeOffset instant)` → `string` con `yyyy-MM-dd HH:mm`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationsExportProcessorTests.cs`, borra la primera línea `using System.Globalization;` (su único uso es la aserción que cambia abajo). Reemplaza:

```csharp
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[1].Text);
```

por:

```csharp
        // Now es 15:30 UTC: 10:30 en Bogotá, sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal("2026-09-12 10:30", row[1].Text);
```

Reemplaza **todas** las apariciones (dos) de `"cotizaciones-2026-09-12-1530.xlsx"` por `"cotizaciones-2026-09-12-1030.xlsx"`.

Reemplaza:

```csharp
    // Había filas al pedir y ya no al procesar: reintentar da lo mismo.
    [Fact]
    public async Task NoRowsWhenItRunsIsDefinitiveAndUploadsNothing()
```

por:

```csharp
    // Spec 2026-09-17, punto 8a: la celda y el nombre del archivo van en la hora del tenant. Creada y
    // exportada el 31 de diciembre a las 23:00 en Bogotá, nada en el archivo dice 2027.
    [Fact]
    public async Task WritesTheDateAndTheFileNameInTheTenantsLocalTime()
    {
        var newYearsEveInBogota = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);
        var writer = new RecordingExportWorkbookWriter();
        var processor = NewProcessor(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001", newYearsEveInBogota)),
            writer,
            tenantClock: new FixedTenantClock(newYearsEveInBogota));

        var result = await processor.ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("2026-12-31 23:00", Assert.Single(writer.Rows)[1].Text);
        Assert.Equal("cotizaciones-2026-12-31-2300.xlsx", result.FileName);
    }

    // Había filas al pedir y ya no al procesar: reintentar da lo mismo.
    [Fact]
    public async Task NoRowsWhenItRunsIsDefinitiveAndUploadsNothing()
```

Reemplaza:

```csharp
    private static Quotation NewQuotation(string number) =>
        Quotation.Create(
```

por:

```csharp
    private static Quotation NewQuotation(string number, DateTimeOffset? createdAt = null) =>
        Quotation.Create(
```

y, dentro de ese método, reemplaza:

```csharp
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static QuotationsExportProcessor NewProcessor(
        StubQuotationListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        StubQuotationAdvisorLookup? advisors = null) =>
```

por:

```csharp
            customerVatSurplus: false,
            AdvisorId,
            createdAt ?? Now);

    private static QuotationsExportProcessor NewProcessor(
        StubQuotationListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        StubQuotationAdvisorLookup? advisors = null,
        FixedTenantClock? tenantClock = null) =>
```

y reemplaza:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedTenantClock(Now));
```

por:

```csharp
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));
```

En `OrdersExportProcessorTests.cs`, borra la primera línea `using System.Globalization;`. Reemplaza:

```csharp
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row[3].Text);
```

por:

```csharp
        // Now es 15:30 UTC: 10:30 en Bogotá, sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal("2026-09-12 10:30", row[3].Text);
```

Reemplaza:

```csharp
        Assert.Equal("pedidos-2026-09-12-1530.xlsx", result.FileName);
```

por:

```csharp
        Assert.Equal("pedidos-2026-09-12-1030.xlsx", result.FileName);
```

Reemplaza:

```csharp
    [Fact]
    public async Task NoOrdersWhenItRunsIsDefinitive()
```

por:

```csharp
    // Spec 2026-09-17, punto 8a: convertido y exportado el 31 de diciembre a las 23:00 en Bogotá, la
    // celda y el nombre del archivo no dicen 2027.
    [Fact]
    public async Task WritesTheDateAndTheFileNameInTheTenantsLocalTime()
    {
        var newYearsEveInBogota = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);
        var writer = new RecordingExportWorkbookWriter();

        var result = await NewProcessor(
                new StubOrderListRepository(NewRow("PED-2026-0001", null, at: newYearsEveInBogota)),
                writer,
                tenantClock: new FixedTenantClock(newYearsEveInBogota))
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal("2026-12-31 23:00", Assert.Single(writer.Rows)[3].Text);
        Assert.Equal("pedidos-2026-12-31-2300.xlsx", result.FileName);
    }

    [Fact]
    public async Task NoOrdersWhenItRunsIsDefinitive()
```

Reemplaza:

```csharp
    private static OrderWithQuotation NewRow(
        string orderNumber, string? paymentMethod, IReadOnlyList<string?>? publicKeys = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
```

por:

```csharp
    private static OrderWithQuotation NewRow(
        string orderNumber,
        string? paymentMethod,
        IReadOnlyList<string?>? publicKeys = null,
        DateTimeOffset? at = null)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, at ?? Now);
```

reemplaza:

```csharp
            notes: null, AdvisorId, proofs, Now);
        return new OrderWithQuotation(order, quotation);
```

por:

```csharp
            notes: null, AdvisorId, proofs, at ?? Now);
        return new OrderWithQuotation(order, quotation);
```

reemplaza:

```csharp
        StubQuotationAdvisorLookup? advisors = null,
        RecordingPaymentProofPublisher? publisher = null) =>
```

por:

```csharp
        StubQuotationAdvisorLookup? advisors = null,
        RecordingPaymentProofPublisher? publisher = null,
        FixedTenantClock? tenantClock = null) =>
```

y reemplaza:

```csharp
            storage ?? new RecordingExportFileStorage(),
            new FixedTenantClock(Now));
```

por:

```csharp
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));
```

En `QuotationsApiHarness.cs`, reemplaza:

```csharp
using System.Collections.Concurrent;
using System.Net.Http.Json;
```

por:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
```

y reemplaza:

```csharp
    public static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";
```

por:

```csharp
    public static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";

    /// <summary>Cómo escribe un Excel un instante para un tenant de Bogotá (spec 2026-09-17, punto
    /// 8a): la hora local al minuto y sin offset. Los tenants de este harness nacen en
    /// America/Bogota.</summary>
    public static string LocalMinuteInBogota(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"))
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
```

En `QuotationExportApiTests.cs`, reemplaza:

```csharp
        Assert.Equal(items[0].CreatedAt, DateTimeOffset.Parse(first[1], CultureInfo.InvariantCulture));
```

por:

```csharp
        // La fecha sale en la hora del tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal(LocalMinuteInBogota(items[0].CreatedAt), first[1]);
```

En `OrderExportApiTests.cs`, reemplaza:

```csharp
        Assert.Equal(items[0].ConvertedAt, DateTimeOffset.Parse(first[3], CultureInfo.InvariantCulture));
```

por:

```csharp
        // La fecha sale en la hora del tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        Assert.Equal(LocalMinuteInBogota(items[0].ConvertedAt), first[3]);
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationsExportProcessorTests|FullyQualifiedName~OrdersExportProcessorTests"
```

Esperado: `WritesTheListColumnsInTheirOrder` y `WritesTheOrdersListColumnsInTheirOrder` fallan con `Expected: "2026-09-12 10:30"` y `Actual: "2026-09-12T15:30:00.0000000+00:00"`; las de nombre de archivo con `Actual: "cotizaciones-2026-09-12-1530.xlsx"` / `"pedidos-2026-09-12-1530.xlsx"`; las dos nuevas con `Actual: "2027-01-01T04:00:00.0000000+00:00"`.

- [ ] **Step 3: El nombre del archivo con el reloj local**

En `ExportJobSupport.cs`, reemplaza:

```csharp
    /// <summary>D8: <c>{prefijo}-yyyy-MM-dd-HHmm.xlsx</c> con la hora UTC en que se generó —la misma
    /// zona que el vencimiento que dice el correo—.</summary>
    public static string For(string prefix, DateTimeOffset generatedAt) =>
        $"{prefix}-{generatedAt.UtcDateTime.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
```

por:

```csharp
    /// <summary>D8: <c>{prefijo}-yyyy-MM-dd-HHmm.xlsx</c> con la hora del tenant en que se generó
    /// (spec 2026-09-17, punto 8a), la misma en la que el correo dice cuándo vence el enlace. Recibe
    /// el instante ya pasado a la hora local y escribe su reloj tal cual.</summary>
    public static string For(string prefix, DateTimeOffset generatedAtLocal) =>
        $"{prefix}-{generatedAtLocal.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
```

- [ ] **Step 4: Las celdas en la hora del tenant**

En `QuotationsExportProcessor.cs`, reemplaza:

```csharp
                return rows.Select(ToCells);
```

por:

```csharp
                return rows.Select(row => ToCells(row, calendar));
```

reemplaza:

```csharp
        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
```

por:

```csharp
        var fileName = ExportFileNames.For(FilePrefix, calendar.ToLocal(generatedAt));
```

y reemplaza:

```csharp
    private static ExportCell[] ToCells(QuotationListItemDto row) =>
    [
        ExportCell.OfText(row.QuotationNumber),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, y ahí 03/04 deja de ser una fecha sola.
        ExportCell.OfText(row.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
```

por:

```csharp
    private static ExportCell[] ToCells(QuotationListItemDto row, TenantCalendar calendar) =>
    [
        ExportCell.OfText(row.QuotationNumber),
        // Texto y no celda de fecha: una fecha se muestra según la configuración regional de quien
        // abre el archivo, y ahí 03/04 deja de ser una fecha sola. En la hora del tenant, al minuto y
        // sin offset (spec 2026-09-17, punto 8a): quien lee la planilla no convierte husos.
        ExportCell.OfText(calendar.ToLocal(row.CreatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
```

En `OrdersExportProcessor.cs`, reemplaza:

```csharp
                return rows.Select(row => ToCells(row, ProofsOf(proofs, row.Id)));
```

por:

```csharp
                return rows.Select(row => ToCells(row, ProofsOf(proofs, row.Id), calendar));
```

reemplaza:

```csharp
        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
```

por:

```csharp
        var fileName = ExportFileNames.For(FilePrefix, calendar.ToLocal(generatedAt));
```

y reemplaza:

```csharp
    private ExportCell[] ToCells(OrderListItemDto row, IReadOnlyList<OrderExportPaymentProof> proofs) =>
    [
        ExportCell.OfText(row.OrderNumber),
        ExportCell.OfText(row.ClientName),
        // El nombre con respaldo al correo, igual que la tabla (spec 2026-09-11, D1, nota del
        // 2026-09-15). El encabezado sigue siendo "Asesor", como en el Excel de cotizaciones.
        ExportCell.OfText(row.AdvisorName),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, mismo criterio que cotizaciones.
        ExportCell.OfText(row.ConvertedAt.ToString("O", CultureInfo.InvariantCulture)),
```

por:

```csharp
    private ExportCell[] ToCells(
        OrderListItemDto row, IReadOnlyList<OrderExportPaymentProof> proofs, TenantCalendar calendar) =>
    [
        ExportCell.OfText(row.OrderNumber),
        ExportCell.OfText(row.ClientName),
        // El nombre con respaldo al correo, igual que la tabla (spec 2026-09-11, D1, nota del
        // 2026-09-15). El encabezado sigue siendo "Asesor", como en el Excel de cotizaciones.
        ExportCell.OfText(row.AdvisorName),
        // Texto y no celda de fecha, mismo criterio que cotizaciones, en la hora del tenant, al minuto
        // y sin offset (spec 2026-09-17, punto 8a).
        ExportCell.OfText(calendar.ToLocal(row.ConvertedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationExportApiTests|FullyQualifiedName~OrderExportApiTests|FullyQualifiedName~ExportJobRunnerIntegrationTests"
```

Esperado: todo en verde. Las regex `^cotizaciones-\d{4}-\d{2}-\d{2}-\d{4}\.xlsx$` y `^pedidos-…` de las pruebas de integración siguen valiendo.

- [ ] **Step 6: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs
```

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/ExportJobSupport.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationsExportProcessor.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationExportApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs; git commit -m "fix(quotations): fechas y nombres de los excel en la hora del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 11: Fechas y nombres de los Excel de clientes y productos en la hora del tenant (punto 8a, segunda mitad)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs:1-14`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs:1-91`
- Modify: `src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs:31-76`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/ExportProducts.cs:1-3,31-41,71-83,165-168`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerExportApiTests.cs` (prueba nueva, helper y doble de reloj)
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs` (prueba nueva, siembra del tenant y factoría con reloj)

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar` (Tasks 1-2).
- Produces:
  - `ICustomerExportBuilder.Build(IReadOnlyList<CustomerDto> customers, TenantCalendar calendar, CancellationToken cancellationToken)` — `DateTimeOffset generatedAt` sale; el instante es `calendar.UtcNow`.
  - `ExportCustomersHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)` y `ExportProductsHandler(…, IExecutionContext executionContext, ITenantClock tenantClock)` — `IClock` sale de las dos firmas.

- [ ] **Step 1: Escribir la prueba de clientes que falla**

En `CustomerExportApiTests.cs`, reemplaza:

```csharp
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
```

por:

```csharp
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
```

reemplaza:

```csharp
    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
```

por:

```csharp
    // Spec 2026-09-17, punto 8a: con el reloj en el 31 de diciembre a las 23:00 de Bogotá (el tenant
    // de desarrollo que siembra TenancyDatabaseInitializer), el nombre del archivo y las fechas de
    // alta y de actualización salen en la hora del tenant, sin offset.
    [Fact]
    public async Task ExportWritesDatesAndTheFileNameInTheTenantsLocalTime()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = FactoryAt(database, storage, new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero));
        using var client = CreateManager(factory);
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        await CreateCustomerAsync(
            client, city.CityId, classification.Id, "Verde Esencial S.A.S.", "900.123.456-1");

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("clientes-20261231-230000.xlsx", body.FileName);
        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("2026-12-31 23:00", sheet.Cell(2, 13).GetString());
        Assert.Equal("2026-12-31 23:00", sheet.Cell(2, 14).GetString());
    }

    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
```

reemplaza:

```csharp
    private static async Task<List<string>> OutboxEventNamesAsync(string connectionString)
```

por:

```csharp
    private static QepApiFactory FactoryAt(
        Testcontainers.PostgreSql.PostgreSqlContainer database,
        CapturingExportStorage storage,
        DateTimeOffset utcNow) =>
        new(
            database.GetConnectionString(),
            services =>
            {
                services.AddScoped<ICustomerExportStorage>(_ => storage);
                services.RemoveAll<IClock>();
                services.AddScoped<IClock>(_ => new FixedClock(utcNow));
            });

    private static async Task<List<string>> OutboxEventNamesAsync(string connectionString)
```

y reemplaza:

```csharp
    // Doble a mano, como el resto del repositorio: no hay libreria de mocking.
    private sealed class CapturingExportStorage : ICustomerExportStorage
```

por:

```csharp
    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    // Doble a mano, como el resto del repositorio: no hay libreria de mocking.
    private sealed class CapturingExportStorage : ICustomerExportStorage
```

- [ ] **Step 2: Escribir la prueba de productos que falla**

En `ProductExportApiTests.cs`, reemplaza:

```csharp
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
```

por:

```csharp
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Catalog.Application;
using Modules.Tenancy.Infrastructure.Persistence;
```

reemplaza:

```csharp
    public async Task ExportPivotsPriceScalesIntoSharedColumns()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
```

por:

```csharp
    public async Task ExportPivotsPriceScalesIntoSharedColumns()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        await SeedTenantAsync(factory);
```

reemplaza:

```csharp
    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
```

por:

```csharp
    // Spec 2026-09-17, punto 8a: el nombre del archivo lleva la hora del tenant. Con el reloj en el 31
    // de diciembre a las 23:00 de Bogotá no dice 2027.
    [Fact]
    public async Task ExportNamesTheFileWithTheTenantsLocalTime()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), storage, new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero));
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        await SeedTenantAsync(factory);
        await CreateProductAsync(client, "AAA-1", "Vela de soja", []);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("productos-2026-12-31-2300.xlsx", body.FileName);
    }

    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
```

reemplaza:

```csharp
    private static async Task CreateProductAsync(
        HttpClient client, string code, string name, object[] scales)
```

por:

```csharp
    // Este archivo usa un tenant que no pasa por el registro. ExportProductsHandler nombra el archivo
    // con la hora del tenant (spec 2026-09-17, punto 8a), y sin su fila en tenancy.tenants
    // TenantClock responde tenancy.tenant.not_found.
    private static async Task SeedTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        tenancy.Tenants.Add(Modules.Tenancy.Domain.Tenant.Create(
            new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId)),
            "catalog-export-tests",
            "Catalog Export Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task CreateProductAsync(
        HttpClient client, string code, string name, object[] scales)
```

reemplaza:

```csharp
    private sealed class QepApiFactory(
        string connectionString, IProductExportStorage exportStorage)
        : WebApplicationFactory<Program>
```

por:

```csharp
    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class QepApiFactory(
        string connectionString, IProductExportStorage exportStorage, DateTimeOffset? utcNow = null)
        : WebApplicationFactory<Program>
```

y reemplaza:

```csharp
            builder.ConfigureServices(services =>
                services.AddScoped(_ => exportStorage));
```

por:

```csharp
            builder.ConfigureServices(services =>
            {
                services.AddScoped(_ => exportStorage);
                // Reloj fijo sólo para las pruebas que lo piden (spec 2026-09-17).
                if (utcNow is { } fixedNow)
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                }
            });
```

- [ ] **Step 3: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerExportApiTests.ExportWritesDatesAndTheFileNameInTheTenantsLocalTime"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests/Modules.Catalog.IntegrationTests.csproj --filter "FullyQualifiedName~ProductExportApiTests.ExportNamesTheFileWithTheTenantsLocalTime"
```

Esperado: clientes falla con `Expected: "clientes-20261231-230000.xlsx"` y `Actual: "clientes-20270101-040000.xlsx"`; productos con `Expected: "productos-2026-12-31-2300.xlsx"` y `Actual: "productos-2027-01-01-0400.xlsx"`.

- [ ] **Step 4: El Excel de clientes con el calendario**

En `ICustomerExportBuilder.cs`, reemplaza:

```csharp
namespace Modules.Customers.Application;
```

por:

```csharp
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;
```

y reemplaza:

```csharp
    CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
```

por:

```csharp
    /// <summary>El <paramref name="calendar"/> es el del tenant: el nombre del archivo y las fechas
    /// van en su hora (spec 2026-09-17, punto 8a), y <c>calendar.UtcNow</c> es el instante en que se
    /// generó.</summary>
    CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        TenantCalendar calendar,
        CancellationToken cancellationToken);
```

En `ClosedXmlCustomerExportBuilder.cs`, reemplaza:

```csharp
using ClosedXML.Excel;
using Modules.Customers.Application;
```

por:

```csharp
using System.Globalization;
using ClosedXML.Excel;
using Modules.Customers.Application;
using Modules.Tenancy.Application;
```

reemplaza:

```csharp
    public CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
```

por:

```csharp
    public CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
```

reemplaza:

```csharp
            WriteRow(sheet, index + 2, customers[index]);
```

por:

```csharp
            WriteRow(sheet, index + 2, customers[index], calendar);
```

reemplaza:

```csharp
        return new CustomerExportFile(
            stream.ToArray(),
            $"clientes-{generatedAt:yyyyMMdd-HHmmss}.xlsx");
    }

    // El mismo orden que `Columns`. Los textos de las columnas compartidas son los que el
    // importador espera leer: el nombre de la clasificacion y del departamento/ciudad, no sus ids.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerDto customer)
```

por:

```csharp
        // En la hora del tenant (spec 2026-09-17, punto 8a): es la hora que la persona ve en su reloj.
        var generatedAtLocal = calendar.ToLocal(calendar.UtcNow);
        return new CustomerExportFile(
            stream.ToArray(),
            $"clientes-{generatedAtLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xlsx");
    }

    // El mismo orden que `Columns`. Los textos de las columnas compartidas son los que el
    // importador espera leer: el nombre de la clasificacion y del departamento/ciudad, no sus ids.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerDto customer, TenantCalendar calendar)
```

y reemplaza:

```csharp
        // Como texto ISO-8601 y no como fecha de Excel: una celda de fecha se muestra segun la
        // configuracion regional de quien abre el archivo, y ahi 03/04 deja de ser una fecha sola.
        sheet.Cell(excelRow, 13).Value = customer.CreatedAt.ToString("O");
        sheet.Cell(excelRow, 14).Value = customer.UpdatedAt.ToString("O");
```

por:

```csharp
        // Como texto y no como fecha de Excel: una celda de fecha se muestra según la configuración
        // regional de quien abre el archivo, y ahí 03/04 deja de ser una fecha sola. En la hora del
        // tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        sheet.Cell(excelRow, 13).Value =
            calendar.ToLocal(customer.CreatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        sheet.Cell(excelRow, 14).Value =
            calendar.ToLocal(customer.UpdatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
```

En `ExportCustomers.cs`, reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportCustomersCommand, ExportCustomersResult>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportCustomersCommand, ExportCustomersResult>
```

y reemplaza:

```csharp
        var occurredAt = clock.UtcNow;
        var items = await ToDtosAsync(command.TenantId, customers, cancellationToken);
        var file = exportBuilder.Build(items, occurredAt, cancellationToken);
```

por:

```csharp
        // El archivo se nombra y se llena en la hora del tenant (spec 2026-09-17, punto 8a); la
        // auditoría y el evento siguen con el instante UTC.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var occurredAt = calendar.UtcNow;
        var items = await ToDtosAsync(command.TenantId, customers, cancellationToken);
        var file = exportBuilder.Build(items, calendar, cancellationToken);
```

- [ ] **Step 5: El nombre del Excel de productos con la hora local**

En `ExportProducts.cs`, reemplaza:

```csharp
using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;
```

por:

```csharp
using System.Globalization;
using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;
```

reemplaza:

```csharp
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportProductsCommand, ExportProductsResult>
```

por:

```csharp
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportProductsCommand, ExportProductsResult>
```

reemplaza:

```csharp
        var occurredAt = clock.UtcNow;
```

por:

```csharp
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var occurredAt = calendar.UtcNow;
```

reemplaza:

```csharp
        var fileName = FileNameFor(occurredAt);
```

por:

```csharp
        var fileName = FileNameFor(calendar.ToLocal(occurredAt));
```

y reemplaza:

```csharp
    /// <summary>Con la fecha adentro: quien recibe varios correos necesita distinguirlos, y el
    /// nombre es lo unico que ve antes de abrir el archivo.</summary>
    private static string FileNameFor(DateTimeOffset now) =>
        $"productos-{now:yyyy-MM-dd-HHmm}.xlsx";
```

por:

```csharp
    /// <summary>Con la fecha adentro: quien recibe varios correos necesita distinguirlos, y el
    /// nombre es lo unico que ve antes de abrir el archivo. En la hora del tenant (spec 2026-09-17,
    /// punto 8a): recibe el instante ya local y escribe su reloj.</summary>
    private static string FileNameFor(DateTimeOffset localNow) =>
        $"productos-{localNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests/Modules.Customers.IntegrationTests.csproj --filter "FullyQualifiedName~CustomerExportApiTests"
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests/Modules.Catalog.IntegrationTests.csproj --filter "FullyQualifiedName~ProductExportApiTests"
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests/Modules.Catalog.UnitTests.csproj
```

Esperado: build con 0 errores y 0 advertencias (si `BuildingBlocks.Application` queda sin uso en `ExportProducts.cs`, no: `ICommand` sigue ahí); las cinco corridas en verde, `ExportPivotsPriceScalesIntoSharedColumns` incluida gracias a `SeedTenantAsync`.

- [ ] **Step 7: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs src/Modules/Catalog/Modules.Catalog.Application/ExportProducts.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerExportApiTests.cs tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs
```

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerExportBuilder.cs src/Modules/Customers/Modules.Customers.Application/ExportCustomers.cs src/Modules/Catalog/Modules.Catalog.Application/ExportProducts.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerExportApiTests.cs tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductExportApiTests.cs; git commit -m "fix(customers): fechas y nombres de los excel de clientes y productos en la hora del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 12: El vencimiento del enlace en los correos de exportación, en la hora del tenant (punto 8b)

Las tres plantillas muestran `dd/MM/yyyy HH:mm` local, sin la etiqueta `UTC`. Reciben el huso; el worker que arma el correo resuelve el calendario del tenant del export (que viene en el payload) después de comprobar el destinatario.

**Files:**
- Modify: `src/Modules/Notifications/Modules.Notifications.Application/CustomerExportEmailTemplate.cs:18-27`, `ProductExportEmailTemplate.cs:18-28`, `QuotationsExportReadyEmailTemplate.cs:15-25`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs:99-103,233-234`
- Modify: `src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs:35-41`, `ProductExportDeliveryWorker.cs:35-41`, `QuotationsExportReadyDeliveryWorker.cs:35-41`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/CustomerExportEmailTemplateTests.cs`, `QuotationsExportEmailTemplateTests.cs`
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/ProductExportEmailTemplateTests.cs` (nuevo)

**Interfaces:**
- Consumes: `ITenantClock`, `TenantCalendar.TimeZone` (Tasks 1-2).
- Produces:
  - `CustomerExportEmailTemplate.Render(string recipientAddress, string downloadUrl, string fileName, int customerCount, DateTimeOffset expiresAt, TimeZoneInfo timeZone)`.
  - `ProductExportEmailTemplate.Render(string recipientAddress, string downloadUrl, string fileName, int productCount, DateTimeOffset expiresAt, TimeZoneInfo timeZone)`.
  - `QuotationsExportReadyEmailTemplate.Render(string recipientAddress, string kind, string downloadUrl, string fileName, int rowCount, DateTimeOffset expiresAt, TimeZoneInfo timeZone)`.
  - `internal sealed record DeliveryContext(IEmailChannel Channel, IUserDirectory UserDirectory, ITenantDirectory TenantDirectory, ITenantClock TenantClock, IClock Clock)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `CustomerExportEmailTemplateTests.cs`, reemplaza:

```csharp
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);
```

por:

```csharp
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
```

reemplaza **todas** las apariciones (tres) de `ExpiresAt);` por `ExpiresAt, Bogota);`, y reemplaza:

```csharp
            Assert.Contains("01/09/2026 14:30 UTC", body, StringComparison.Ordinal);
        }
```

por:

```csharp
            // 14:30 UTC son las 09:30 en Bogotá: la hora del tenant, sin etiqueta de huso (spec
            // 2026-09-17, punto 8b).
            Assert.Contains("01/09/2026 09:30", body, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
        }
```

y agrega antes del cierre de la clase (la última `}` del archivo):

```csharp

    // El enlace que vence el 1 de enero a las 04:00 UTC vence, para el tenant, el 31 de diciembre a
    // las 23:00.
    [Fact]
    public void RenderShowsTheExpiryOnTheTenantsDay()
    {
        var message = CustomerExportEmailTemplate.Render(
            "compras@verde.co", "https://r2.example/x", "clientes.xlsx", 1,
            new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero), Bogota);

        Assert.Contains("31/12/2026 23:00", message.TextBody, StringComparison.Ordinal);
    }
```

En `QuotationsExportEmailTemplateTests.cs`, reemplaza:

```csharp
    private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);
```

por:

```csharp
    private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
```

reemplaza **todas** las apariciones (tres) de `ExpiresAt);` por `ExpiresAt, Bogota);`, y reemplaza:

```csharp
            Assert.Contains("13/09/2026 15:30 UTC", body, StringComparison.Ordinal);
        }
```

por:

```csharp
            // 15:30 UTC son las 10:30 en Bogotá, sin etiqueta de huso (spec 2026-09-17, punto 8b).
            Assert.Contains("13/09/2026 10:30", body, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
        }
```

Crea `tests/Modules/Notifications/Modules.Notifications.UnitTests/ProductExportEmailTemplateTests.cs`:

```csharp
using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El correo de exportación de productos: mismo contrato que el de clientes, y el
/// vencimiento del enlace en la hora del tenant (spec 2026-09-17, punto 8b).</summary>
public sealed class ProductExportEmailTemplateTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    [Fact]
    public void RenderShowsTheExpiryInTheTenantsLocalTimeWithoutAZoneLabel()
    {
        var message = ProductExportEmailTemplate.Render(
            "compras@verde.co",
            "https://r2.example/exports/productos.xlsx",
            "productos-2026-12-31-2300.xlsx",
            productCount: 3,
            new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero),
            Bogota);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("productos-2026-12-31-2300.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("3 productos", body, StringComparison.Ordinal);
            Assert.Contains("31/12/2026 23:00", body, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests/Modules.Notifications.UnitTests.csproj --filter "FullyQualifiedName~ExportEmailTemplateTests"
```

Esperado: falla la compilación con `error CS1501: No overload for method 'Render' takes 6 arguments` (y `7` en la de cotizaciones).

- [ ] **Step 3: Las plantillas formatean en el huso que reciben**

En `CustomerExportEmailTemplate.cs`, reemplaza:

```csharp
        int customerCount,
        DateTimeOffset expiresAt)
    {
        const string subject = "Tu exportación de clientes está lista";

        var expiry = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
```

por:

```csharp
        int customerCount,
        DateTimeOffset expiresAt,
        TimeZoneInfo timeZone)
    {
        const string subject = "Tu exportación de clientes está lista";

        // En la hora del tenant del export y sin etiqueta de huso (spec 2026-09-17, punto 8b): es la
        // hora que la persona ve en su reloj.
        var expiry = TimeZoneInfo.ConvertTime(expiresAt, timeZone)
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
```

En `ProductExportEmailTemplate.cs`, reemplaza:

```csharp
        int productCount,
        DateTimeOffset expiresAt)
    {
        const string subject = "Tu exportación de productos está lista";

        var expiry = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
```

por:

```csharp
        int productCount,
        DateTimeOffset expiresAt,
        TimeZoneInfo timeZone)
    {
        const string subject = "Tu exportación de productos está lista";

        // En la hora del tenant del export y sin etiqueta de huso (spec 2026-09-17, punto 8b).
        var expiry = TimeZoneInfo.ConvertTime(expiresAt, timeZone)
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
```

En `QuotationsExportReadyEmailTemplate.cs`, reemplaza:

```csharp
        int rowCount,
        DateTimeOffset expiresAt)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"Tu exportación de {names.Plural} está lista";
        var expiry = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
```

por:

```csharp
        int rowCount,
        DateTimeOffset expiresAt,
        TimeZoneInfo timeZone)
    {
        var names = QuotationsExportKindText.Of(kind);
        var subject = $"Tu exportación de {names.Plural} está lista";
        // En la hora del tenant del export y sin etiqueta de huso (spec 2026-09-17, punto 8b).
        var expiry = TimeZoneInfo.ConvertTime(expiresAt, timeZone)
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
```

- [ ] **Step 4: Los workers resuelven el calendario del tenant del export**

En `OutboxDeliveryWorker.cs`, reemplaza:

```csharp
            services.GetRequiredService<ITenantDirectory>(),
            services.GetRequiredService<IClock>());
```

por:

```csharp
            services.GetRequiredService<ITenantDirectory>(),
            services.GetRequiredService<ITenantClock>(),
            services.GetRequiredService<IClock>());
```

y reemplaza:

```csharp
internal sealed record DeliveryContext(
    IEmailChannel Channel, IUserDirectory UserDirectory, ITenantDirectory TenantDirectory, IClock Clock);
```

por:

```csharp
internal sealed record DeliveryContext(
    IEmailChannel Channel,
    IUserDirectory UserDirectory,
    ITenantDirectory TenantDirectory,
    ITenantClock TenantClock,
    IClock Clock);
```

En `CustomerExportDeliveryWorker.cs`, reemplaza:

```csharp
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => CustomerExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.CustomerCount, export.ExpiresAt),
            stoppingToken);
```

por:

```csharp
        // El vencimiento se muestra en la hora del tenant del export (spec 2026-09-17, punto 8b). Un
        // tenant que no resuelve falla acá, fuera del envío: el reclamo queda vivo y se reintenta, en
        // vez de mandar una hora en UTC.
        var calendar = await context.TenantClock.GetAsync(export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => CustomerExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.CustomerCount, export.ExpiresAt,
                calendar.TimeZone),
            stoppingToken);
```

En `ProductExportDeliveryWorker.cs`, reemplaza:

```csharp
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => ProductExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.ProductCount, export.ExpiresAt),
            stoppingToken);
```

por:

```csharp
        // El vencimiento se muestra en la hora del tenant del export (spec 2026-09-17, punto 8b).
        var calendar = await context.TenantClock.GetAsync(export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => ProductExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.ProductCount, export.ExpiresAt,
                calendar.TimeZone),
            stoppingToken);
```

En `QuotationsExportReadyDeliveryWorker.cs`, reemplaza:

```csharp
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => QuotationsExportReadyEmailTemplate.Render(
                recipient, export.Kind, export.DownloadUrl, export.FileName, export.RowCount, export.ExpiresAt),
            stoppingToken);
```

por:

```csharp
        // El vencimiento se muestra en la hora del tenant del export (spec 2026-09-17, punto 8b).
        var calendar = await context.TenantClock.GetAsync(export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => QuotationsExportReadyEmailTemplate.Render(
                recipient, export.Kind, export.DownloadUrl, export.FileName, export.RowCount, export.ExpiresAt,
                calendar.TimeZone),
            stoppingToken);
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests/Modules.Notifications.UnitTests.csproj
dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests/Modules.Notifications.IntegrationTests.csproj
```

Esperado: todo en verde. `DeliveryWorkersCharacterizationTests` y `QuotationsExportNotificationTests` siguen dejando los correos en `Sent`: sus tenants se registran de verdad, así que el calendario resuelve.

- [ ] **Step 6: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Notifications/Modules.Notifications.Application/CustomerExportEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Application/ProductExportEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs tests/Modules/Notifications/Modules.Notifications.UnitTests
```

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Notifications/Modules.Notifications.Application/CustomerExportEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Application/ProductExportEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Application/QuotationsExportReadyEmailTemplate.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/OutboxDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/CustomerExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/ProductExportDeliveryWorker.cs src/Modules/Notifications/Modules.Notifications.Infrastructure/Messaging/QuotationsExportReadyDeliveryWorker.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/CustomerExportEmailTemplateTests.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/QuotationsExportEmailTemplateTests.cs tests/Modules/Notifications/Modules.Notifications.UnitTests/ProductExportEmailTemplateTests.cs; git commit -m "fix(notifications): vencimiento de los enlaces de exportación en la hora del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 13: La carga sintética vence con el hoy del tenant (punto 8c)

`ExportLoadSeeder` marca `Expired` con el mismo `today` local que usa el barrido desde Task 4. La numeración por año UTC de la carga queda como está (hallazgo 9).

**Files:**
- Modify: `src/Bootstrapper/Seeding/ExportLoadSeeder.cs:1-8,71-72`
- Modify (tests): `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs:19-21,66-73,185,213`
- Test: `ExportLoadSeedTests.cs` (prueba nueva)

**Interfaces:**
- Consumes: `ITenantClock` (Task 2); `QuotationsApiHarness.NewYearsEveInBogota` y `QepApiFactory(…, utcNow:)` (Task 3).
- Produces: nada nuevo; `SeedExportLoadAsync` no cambia de firma.

- [ ] **Step 1: Escribir la prueba que falla y reescribir los hoy en UTC**

En `ExportLoadSeedTests.cs`, reemplaza:

```csharp
public sealed class ExportLoadSeedTests
{
    private const string OwnerEmail = "carga@qcode.co";
```

por:

```csharp
public sealed class ExportLoadSeedTests
{
    private const string OwnerEmail = "carga@qcode.co";

    // TenancySeeder siembra el tenant de la carga en America/Bogota: su hoy es el que decide qué vence
    // (spec 2026-09-17, punto 8c).
    private static readonly TimeZoneInfo LoadTenantTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    // Spec 2026-09-17, punto 8c: la carga marca Expired con el hoy del tenant, el mismo del barrido de
    // vencimiento. Con el reloj en el 31 de diciembre a las 23:00 de Bogotá, lo que vence el 31 sigue
    // enviado. Entre las 200 hay al menos una que vence ese día (hallazgo 10 del plan: la n = 53).
    [Fact]
    public async Task TheSeedExpiresWithTheTenantsLocalToday()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var connectionString = database.GetConnectionString();
        var lastDay = new DateOnly(2026, 12, 31);

        await factory.Services.SeedExportLoadAsync(OwnerEmail, 200, TestContext.Current.CancellationToken);

        Assert.NotEqual(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until = @lastDay",
            ("lastDay", lastDay)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until = @lastDay AND status <> 'Sent'",
            ("lastDay", lastDay)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM quotations.quotations WHERE tenant_id = @tenant AND valid_until < @lastDay AND status <> 'Expired'",
            ("lastDay", lastDay)));
    }
```

reemplaza:

```csharp
        // Services: sin esto, platform.outbox_messages todavía no existe. El "hoy" sale del mismo IClock
        // con que decide el seeder y se lee antes de sembrar, no del now() de la base, que en una corrida
        // que cruce la medianoche UTC ya sería otro día.
        await using var clockScope = factory.Services.CreateAsyncScope();
        var today = DateOnly.FromDateTime(
            clockScope.ServiceProvider.GetRequiredService<IClock>().UtcNow.UtcDateTime);
```

por:

```csharp
        // Services: sin esto, platform.outbox_messages todavía no existe. El "hoy" sale del mismo IClock
        // con que decide el seeder, en el huso del tenant de la carga (spec 2026-09-17, punto 8c), y se
        // lee antes de sembrar, no del now() de la base, que en una corrida que cruce la medianoche ya
        // sería otro día.
        await using var clockScope = factory.Services.CreateAsyncScope();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            clockScope.ServiceProvider.GetRequiredService<IClock>().UtcNow, LoadTenantTimeZone).DateTime);
```

reemplaza:

```csharp
        // Los procesadores de verdad sobre un año entero, igual que un pedido desde la pantalla.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
```

por:

```csharp
        // Los procesadores de verdad sobre un año entero, igual que un pedido desde la pantalla, con el
        // hoy del tenant (spec 2026-09-17).
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LoadTenantTimeZone).DateTime);
```

y reemplaza:

```csharp
        var year = DateTime.UtcNow.Year;
```

por:

```csharp
        // El año del consecutivo es el del tenant desde la spec 2026-09-17 (punto 2a).
        var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LoadTenantTimeZone).Year;
```

- [ ] **Step 2: Correrla y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~ExportLoadSeedTests.TheSeedExpiresWithTheTenantsLocalToday"
```

Esperado: la segunda aserción falla con `Assert.Equal() Failure: Values differ`, `Expected: 0`, `Actual: 1` (o más): con el hoy UTC (2027-01-01) la carga siembra `Expired` lo que vence el 31. Si falla la primera (`NotEqual`), la carga no sembró ninguna que venza el 31 con este reloj: **para y pregunta**, el hallazgo 10 dejó de valer.

- [ ] **Step 3: El hoy del tenant en el seeder**

En `src/Bootstrapper/Seeding/ExportLoadSeeder.cs`, reemplaza:

```csharp
using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Identity.Infrastructure.Seed;
using Modules.Tenancy.Infrastructure.Seed;
```

por:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Identity.Infrastructure.Seed;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure.Seed;
```

y reemplaza:

```csharp
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
```

por:

```csharp
        // El tenant de la carga ya existe (SeedTenantAsync, arriba). Su hoy es el mismo con el que
        // vence QuotationExpirationProcessor (spec 2026-09-17, punto 8c): con el de UTC, desde las 19:00
        // en Bogotá la carga sembraba vencidas que el barrido todavía considera vigentes. La numeración
        // y la vigencia de la carga siguen calculándose en UTC dentro del SQL.
        var calendar = await scope.ServiceProvider.GetRequiredService<ITenantClock>()
            .GetAsync(TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var today = calendar.Today;
```

Si el build dice `CS0246: The type or namespace name 'IClock' could not be found` en este archivo, algún otro uso de `IClock` quedó: vuelve a agregar `using BuildingBlocks.Application;`.

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Backend.slnx --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~ExportLoadSeedTests"
```

Esperado: todas las de `ExportLoadSeedTests` en verde.

- [ ] **Step 5: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Bootstrapper/Seeding/ExportLoadSeeder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs
```

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
if ((git branch --show-current) -ne "feature/fechas-locales-del-tenant") { throw "ABORT: rama equivocada" }; git add src/Bootstrapper/Seeding/ExportLoadSeeder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExportLoadSeedTests.cs; git commit -m "fix(bootstrapper): la carga sintética vence con el hoy del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 14: Verificación final

**Files:**
- Ninguno, salvo que la verificación pida un arreglo; en ese caso, el arreglo va con su propia prueba y su commit `fix(<módulo>): …`.

**Interfaces:**
- Consumes: todo lo anterior y `$env:TEMP\qep-fechas-baseline-failed.txt` (Task 0).
- Produces: el handoff con la salida literal de cada comando.

- [ ] **Step 1: Barrido de cortes en UTC que quedaron**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-ChildItem -Recurse -Filter *.cs -Path src | Select-String -Pattern "ReportDateRange|DateOnly\.FromDateTime\(.*UtcDateTime\)|UtcDateTime\.Year|UtcDateTime\.Month|'UTC'"
Get-ChildItem -Recurse -Filter *.cs -Path tests | Select-String -Pattern "DateTime\.UtcNow\.Year|DateTime\.UtcNow\.Month"
```

Esperado: en `src` sólo la línea de `QuotationExpirationProcessor.cs` que calcula el techo amplio (`DateOnly.FromDateTime(now.UtcDateTime).AddDays(2)`) y, si Task 7 eligió la variante B, las `.UtcDateTime` dentro de `ConvertTimeBySystemTimeZoneId`. En `tests`, ninguna. Cualquier otra coincidencia es un corte en UTC que el spec no revisó: **para y pregunta**.

- [ ] **Step 2: Los comandos de README § Verificación**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore
```

Esperado: restore sin `NU1004` (ningún `packages.lock.json` cambió); `dotnet format` sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que esta rama no tocó; build con `0 Advertencia(s)` y `0 Errores`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
git status --short
git diff --stat (git merge-base develop HEAD) HEAD -- "**/packages.lock.json" Directory.Packages.props
```

Esperado: `git status` vacío y el `diff --stat` sin archivos.

- [ ] **Step 3: La suite completa, comparada por nombre con el baseline**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
$final = Join-Path $env:TEMP "qep-fechas-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $final
$failed = Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique
$baseline = Get-Content (Join-Path $env:TEMP "qep-fechas-baseline-failed.txt") -ErrorAction SilentlyContinue
Compare-Object -ReferenceObject @($baseline) -DifferenceObject @($failed) | Where-Object { $_.SideIndicator -eq "=>" }
```

Esperado: `Compare-Object` sin salida: ninguna prueba falla ahora que no fallara en el baseline. Pega la lista de nombres fallidos (si hay) en el handoff.

- [ ] **Step 4: Arquitectura**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
git diff (git merge-base develop HEAD) HEAD --stat -- tests/ArchitectureTests
```

Esperado: en verde, y el `diff` de `tests/ArchitectureTests` vacío: ninguna regla cambió.

- [ ] **Step 5: Historial**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-locales-del-tenant
git log --format="%h %s" (git merge-base develop HEAD)..HEAD
git log --format=%B (git merge-base develop HEAD)..HEAD | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: los commits de la tabla de Entrega, en orden, y el `Select-String` sin salida. La rama no se publica desde este plan.
