# Numeración de documentos configurable por tenant — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** El prefijo, el año, el separador y el ancho del número de cotización y de pedido pasan a ser un dato por tenant y por tipo de documento, configurable con SQL documentado. Sin fila, el comportamiento actual (`QUO-2026-0001`, `PED-2026-0001`) no cambia para ningún tenant existente, y no hay backfill.

**Architecture:** Un valor inmutable `DocumentNumberFormat` y un formateador puro `DocumentNumberFormatter` en `Modules.Quotations.Application` reemplazan a `OrderNumberFormatter` y `QuotationNumberFormatter`. El puerto `IDocumentNumberingFormatLookup` vive en Application y su adaptador `DocumentNumberingFormatLookup` en `Modules.Quotations.Infrastructure.Persistence`, sobre la tabla nueva `quotations.document_numbering_formats`. `CreateQuotationHandler` y `ConvertQuotationToOrderHandler` leen el formato una vez por request, piden el consecutivo a los generadores que ya existen —con el año del tenant si el formato lleva año, y con `0` si no— y formatean. `IQuotationNumberGenerator` e `IOrderNumberGenerator` no cambian de firma y las tablas de contadores no cambian de forma.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `rollForward: latestPatch`), EF Core 10.0.11 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, FluentValidation, xUnit v3 3.2.2, Testcontainers 4.14.0 (`postgres:18-alpine`).

**Spec:** docs/superpowers/specs/2026-09-17-numeracion-configurable-design.md

## Global Constraints

- El formato es un dato por tenant y por tipo de documento, no una constante de código (decisión 1).
- **Sin endpoint ni pantalla**: se configura con el SQL del runbook (decisión 2). Ningún permiso nuevo, ningún endpoint nuevo, ningún DTO nuevo. `qep-frontend` no se toca.
- **Sin fila, el comportamiento actual**: prefijo `PED-` / `QUO-`, con año, separador `-` y 4 dígitos (decisión 3). Ningún tenant existente cambia y no hay migración de datos.
- **El consecutivo sólo avanza**: fijar el siguiente número usa `GREATEST` (decisión 4). Correr el mismo SQL dos veces no retrocede el contador.
- **Un formato sin año usa la fila `year = 0`** de la tabla de contadores que ya existe; con año se sigue usando la fila del año (decisión 5). No se crean tablas de contador nuevas.
- Rangos del formato, iguales en el `CHECK` de la base y en la validación de `DocumentNumberFormat.Create`: `prefix` de 0 a 10 caracteres en `[A-Za-z0-9-]`; `year_separator` en `''`, `-` o `/`; `min_digits` de 1 a 10; `document_type` en `order` o `quotation`.
- El año que se usa cuando el formato lo incluye es `calendar.Today.Year` (`ITenantClock`, spec 2026-09-17 de fechas locales), nunca `UtcNow.Year`.
- `IQuotationNumberGenerator` e `IOrderNumberGenerator` no cambian de firma, y `quotations.quotation_number_counters` / `quotations.order_number_counters` no cambian de forma.
- Un número que no cabe en 20 caracteres lo rechaza el dominio con `quotation.quotation.number_too_long` / `order.order.number_too_long`, que ya existen. Ningún status HTTP se arma a mano: el mapeo es central en `src/Api/ApiExceptionHandler.cs`.
- `Modules.Quotations.Application` no referencia EF Core ni Npgsql: el adaptador del lookup vive en Infrastructure. Ninguna regla de `tests/ArchitectureTests/` cambia.
- **Fuera de alcance:** endpoint o pantalla de configuración, backfill o renumeración de lo ya emitido, la numeración de `ExportLoadSeeder` (carga sintética, sigue por año UTC) y el resto de los documentos.
- Todo se hace en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion`, rama `feature/numeracion-configurable`, creada desde `feature/fechas-locales-del-tenant` (HEAD `f443ea3`, que ya contiene `develop`). **El nombre de la carpeta es corto a propósito:** los `Designer.cs` de las migraciones de este repo tienen rutas largas y Windows corta en 260 caracteres. Cada bloque de comandos empieza con `Set-Location` a ese worktree.
- Los comandos van en Windows PowerShell 5.1: sin `&&`, con `A; if ($?) { B }`, y `$env:VAR = "…"` en línea aparte.
- `Api.exe` corriendo bloquea `build`, `test` y los comandos `ef`: antes de cada corrida, `Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force`.
- Las pruebas de integración necesitan Docker corriendo (Testcontainers); se corren en primer plano, nunca en background.
- Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- Las migraciones se generan con el factory de diseño, **sin `--startup-project`**: `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`.
- Toda factoría de integración fija su configuración con `UseSetting`; nunca hereda los user-secrets de quien corre las pruebas.
- `Directory.Packages.props` no cambia; si por algún motivo cambiara, los `packages.lock.json` se regeneran con `dotnet restore --force-evaluate` y se commitean en el mismo commit.
- TDD estricto: RED antes que GREEN, y pegas en el handoff la salida literal de las dos corridas.
- Commits: Conventional Commits en español, en minúscula (`feat(quotations): …`). **Nunca** `Co-Authored-By` ni otra atribución de IA, aunque una herramienta lo sugiera. Cada commit va con el guard de rama y después `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`.
- Idioma: la prosa y los comentarios de código en español de Colombia, tuteando; identificadores, códigos de error y mensajes de excepción en inglés, como el resto del repo.
- Archivos nuevos en UTF-8 sin BOM y con LF (`.editorconfig`). `dotnet format` reporta `ENDOFLINE` y `CHARSET` en archivos viejos por `core.autocrlf=true`: ese ruido se filtra; cualquier otro diagnóstico en una línea que tocaste se corrige.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: toda conversión a texto lleva `CultureInfo.InvariantCulture`, y un `using` o una clase de prueba que queda sin uso se borra en la misma tarea.
- **Nunca imprimir el valor de un secreto.** El runbook del README usa `psql` con la conexión de administración; la contraseña no se saca con `kubectl get secret`.

---

## Hallazgos contra el código (2026-09-17, sobre `feature/fechas-locales-del-tenant` @ `f443ea3`)

1. **Los dos formateadores son `internal static` y tienen exactamente un consumidor cada uno.** `QuotationNumberFormatter.Format` sólo lo llama `CreateQuotation.cs:69`; `OrderNumberFormatter.Format` sólo lo llama `ConvertQuotationToOrder.cs:97`. No hay más usos en `src/`. En `tests/` sólo existe `OrderNumberFormatterTests.cs` (no hay `QuotationNumberFormatterTests.cs`), y se borra con el formateador que prueba.
2. **La documentación XML de `IQuotationNumberGenerator.cs:7` nombra `QuotationNumberFormatter` con un `<see cref="…"/>`.** No hay `GenerateDocumentationFile` en `Directory.Build.props`, así que un cref roto no rompe el build — pero igual se corrige en la tarea que borra la clase.
3. **Los dos handlers ya tienen la forma post-spec-1**: piden `calendar = await tenantClock.GetAsync(...)`, derivan `now = calendar.UtcNow` y `year = calendar.Today.Year`, y pasan `year` al generador (`CreateQuotation.cs:63-69`, `ConvertQuotationToOrder.cs:92-97`). Este plan sólo mete el lookup del formato entre el calendario y el generador.
4. **La PK de los contadores admite `year = 0` sin tocar nada.** `quotation_number_counters` y `order_number_counters` tienen `PrimaryKey(tenant_id, year)` con `year integer` y **sin ningún `CHECK`** (`20260824171832_InitialQuotations.cs:18-29`, snapshot `:614-631`). El mapeo son cuatro líneas por contador, sin conversiones (`QuotationsDbContext.cs`, `ConfigureQuotationNumberCounter` y `ConfigureOrderNumberCounter`).
5. **No hay un solo `HasCheckConstraint` en todo el repo.** Esta tabla estrena el patrón: los `CHECK` se declaran en `ToTable(name, schema, table => …)` para que la migración generada los traiga, y el nombre se escribe completo (EF lo usa literal).
6. **`Modules.Quotations.Application.csproj` sólo abre `InternalsVisibleTo` a `Modules.Quotations.UnitTests`.** Por eso `DocumentNumberFormat`, `DocumentNumberType` e `IDocumentNumberingFormatLookup` son **públicos** (los usa el adaptador, que vive en Infrastructure) y `DocumentNumberFormatter` queda **internal**, igual que los dos formateadores que reemplaza.
7. **`QuotationsDbContextMappingTests` es el patrón de prueba de mapeo del módulo**, y ya trae `TheModelHasNoChangesPendingAMigration` (`:207-213`): en cuanto la configuración de EF describa la tabla nueva y la migración no exista, esa prueba se pone roja sola. Es el RED de la Task 2 y no hay que escribirlo.
8. **El harness ya sabe fijar el reloj**: `QuotationsApiHarness.NewYearsEveInBogota` (`:49-51`) y `QepApiFactory(connectionString, runExportWorker, publicPaymentProofLinks, utcNow)` (`:693-697`, `:771-775`) llegaron con el spec 1. Las pruebas nuevas lo usan para que el año del tenant sea **2026** literal, y no el del calendario de la máquina.
9. **Escribir SQL crudo contra la base de una prueba ya tiene precedente**: `QuotationExpirationApiTests.cs:178-186` abre un scope de la factoría, pide el `QuotationsDbContext` y llama `dbContext.Database.ExecuteSqlAsync($"…")`. Las pruebas nuevas siembran el formato y el contador exactamente así, con el mismo SQL del runbook.
10. **Discrepancia entre el spec y el mecanismo del contador, en la prueba de integración de PW.** El spec dice «el contador en 234234 … obtiene `PW234235`». El `UPDATE … RETURNING next_value - 1` (`OrderNumberGenerator.cs:20-28`) emite el valor que la fila tiene y deja el siguiente, así que una fila en 234234 emite **234234**. Para que el primer número emitido sea `PW234235`, la fila va en **234235** — que es justo lo que el runbook del propio spec pide (`:102-103`: `VALUES (:tenant_id, 0, :siguiente_numero)`, el **siguiente** número, no el último emitido). Este plan siembra `next_value = 234235` y lo documenta en el README como «el siguiente número que quieres que salga».
11. **`ExportLoadSeeder` numera en SQL, por año UTC, y queda como está** (`ExportLoadSeeder.cs:242`, `:351`): el spec lo declara fuera de alcance. La verificación final lo espera ahí y no lo trata como resto.
12. **`Order.OrderNumberMaxLength` y `Quotation.QuotationNumberMaxLength` valen 20** (`Order.cs:18`, `Quotation.cs:25`) y el rechazo es del dominio: `QuotationsDomainException("order.order.number_too_long", …)` (`Order.cs:432-436`) y `("quotation.quotation.number_too_long", …)` (`Quotation.cs:929-933`).

**Decisiones de este plan donde el spec deja margen:**

- **El código de error del formato inválido es `quotation.numbering.format_invalid`**, lanzado como `QuotationsDomainException` desde `DocumentNumberFormat.Create`. El spec pide «código de dominio y 422» sin nombrarlo. Cubre los dos tipos de documento porque el formato es uno solo; si el owner prefiere `order.numbering.*` y `quotation.numbering.*` por separado, es un cambio de una línea.
- **El tipo de documento viaja como `enum DocumentNumberType { Quotation, Order }`** en Application, y el adaptador lo traduce a los textos `quotation` / `order` de la columna. Un `string` suelto haría que un typo se vea recién en tiempo de ejecución, y la columna ya tiene su `CHECK`.
- **La entidad de EF se llama `DocumentNumberingFormat`** (igual que la tabla) y el valor de Application `DocumentNumberFormat`. Son dos cosas distintas a propósito: una es una fila con `TenantId` y `DocumentType`, la otra es el formato ya validado, sin tenant, que el formateador consume.
- **`year_separator` se mapea como `varchar(1)` y `prefix` como `varchar(10)`**, además del `CHECK`. El spec los describe como `text`; el largo máximo es la misma regla escrita dos veces, y la que atrapa antes.
- **Cada formateador se borra en la tarea que deja de usarlo** —`OrderNumberFormatter` en la Task 4, `QuotationNumberFormatter` en la Task 5— y no los dos juntos al final: así ningún commit queda con código muerto ni con una referencia colgada.
- **El lookup devuelve el default sin consultar dos veces**: una sola lectura por (tenant, tipo), y si no hay fila, `DocumentNumberFormat.DefaultFor(documentType)`. No se cachea entre requests: la tabla se escribe a mano y un cambio tiene que verse en el siguiente documento, no en el siguiente despliegue.

## Entrega

| Commit | Tarea |
| --- | --- |
| `docs(quotations): plan de numeración configurable por tenant` | 0 |
| `feat(quotations): formato de número de documento configurable` | 1 |
| `feat(quotations): tabla de formatos de numeración por tenant` | 2 |
| `feat(quotations): lectura del formato de numeración del tenant` | 3 |
| `feat(quotations): numerar pedidos con el formato del tenant` | 4 |
| `feat(quotations): numerar cotizaciones con el formato del tenant` | 5 |
| `test(quotations): numeración configurable de punta a punta` | 6 |
| `docs(quotations): runbook de numeración por tenant` | 7 |
| sólo si la verificación final pide cambios | 8 |

La rama no se publica ni se mergea desde este plan.

---

### Task 0: Worktree, rama y baseline

**Files:**
- Ninguno de código. Copia y commitea este plan en la rama.

**Interfaces:**
- Consumes: la rama `feature/fechas-locales-del-tenant` (spec 1 ya implementado: `ITenantClock`, `TenantCalendar`, `QuotationsApiHarness.NewYearsEveInBogota`, `QepApiFactory(..., utcNow:)`).
- Produces: el worktree `qep-backend-worktrees\numeracion` en `feature/numeracion-configurable`, y `$env:TEMP\qep-numeracion-baseline-failed.txt` con las pruebas que ya fallan, por nombre. Es la referencia de la Task 8.

- [ ] **Step 1: Crear el worktree desde `feature/fechas-locales-del-tenant`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git fetch origin
git log -1 --oneline feature/fechas-locales-del-tenant
git worktree add -b feature/numeracion-configurable ..\qep-backend-worktrees\numeracion feature/fechas-locales-del-tenant
Copy-Item docs\superpowers\plans\2026-09-17-numeracion-configurable.md ..\qep-backend-worktrees\numeracion\docs\superpowers\plans\
```

Esperado: `Preparing worktree (new branch 'feature/numeracion-configurable')`, y el `git log -1` mostrando `f443ea3 Merge branch 'develop' into feature/fechas-locales-del-tenant` o un commit posterior de esa misma rama. **Si el plan todavía vive sólo en el worktree `fechas-tenant`**, cópialo desde ahí:

```powershell
Copy-Item C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\fechas-tenant\docs\superpowers\plans\2026-09-17-numeracion-configurable.md C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion\docs\superpowers\plans\
```

Si `feature/fechas-locales-del-tenant` local está detrás de `origin/feature/fechas-locales-del-tenant`, **para y pregunta** antes de crear la rama.

- [ ] **Step 2: Comprobar herramientas y que los dos specs están en la base**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git branch --show-current
git status --short
Test-Path docs\superpowers\specs\2026-09-17-numeracion-configurable-design.md
Test-Path docs\superpowers\specs\2026-09-17-fechas-locales-del-tenant-design.md
Test-Path src\Modules\Tenancy\Modules.Tenancy.Application\TenantCalendar.cs
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: rama `feature/numeracion-configurable`; `git status` sólo con `?? docs/superpowers/plans/2026-09-17-numeracion-configurable.md`; los tres `Test-Path` en `True` (el tercero confirma que el spec 1 está implementado en esta base); `Get-Process` sin salida; `docker info` con una versión; `dotnet ef` con una versión 10.x. Si `dotnet ef` no está, instálalo con `dotnet tool install --global dotnet-ef` y vuelve a comprobar.

- [ ] **Step 3: Restore y build**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
```

Esperado: `Compilación correcta.` (o `Build succeeded.`) con `0 Advertencia(s)` y `0 Errores`.

- [ ] **Step 4: Baseline de la suite completa, por nombre**

Tarda decenas de minutos; corre en primer plano.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
$baseline = Join-Path $env:TEMP "qep-numeracion-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger trx --results-directory $baseline
Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-numeracion-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-numeracion-baseline-failed.txt")
```

Esperado: la lista de las que ya fallan, posiblemente vacía. Pégala en el handoff: es contra ella, por nombre, que se mide la Task 8.

- [ ] **Step 5: Commitear el plan**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add docs/superpowers/plans/2026-09-17-numeracion-configurable.md; git commit -m "docs(quotations): plan de numeración configurable por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el commit creado y el `Select-String` sin salida.

---

### Task 1: `DocumentNumberFormat` y `DocumentNumberFormatter`

El valor del formato, con su validación y su default, y el formateador puro que lo aplica. Sin DI, sin base y sin conocer el tenant.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/DocumentNumbering.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentNumberFormatterTests.cs` (nuevo)

**Interfaces:**
- Consumes: `QuotationsDomainException` (`Modules.Quotations.Domain`), `Order.OrderNumberMaxLength`, `Quotation.QuotationNumberMaxLength`.
- Produces (namespace `Modules.Quotations.Application`):

```csharp
public enum DocumentNumberType { Quotation, Order }

public sealed record DocumentNumberFormat
{
    public const int PrefixMaxLength = 10;
    public const int MinDigitsLowerBound = 1;
    public const int MinDigitsUpperBound = 10;
    public const int DefaultMinDigits = 4;
    public const string DefaultYearSeparator = "-";

    public string Prefix { get; }
    public bool IncludeYear { get; }
    public string YearSeparator { get; }
    public int MinDigits { get; }

    public static DocumentNumberFormat Create(
        string prefix, bool includeYear, string yearSeparator, int minDigits);
    public static DocumentNumberFormat DefaultFor(DocumentNumberType documentType);
}

internal static class DocumentNumberFormatter
{
    public static string Format(DocumentNumberFormat format, int year, long sequence);
}
```

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentNumberFormatterTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El formato del número de documento (spec 2026-09-17 de numeración configurable). Es puro: recibe
/// el formato, el año y el consecutivo, y devuelve el texto. Los cuatro casos de la tabla del spec,
/// más los rangos que la base también hace cumplir con un CHECK.
/// </summary>
public sealed class DocumentNumberFormatterTests
{
    // Sin fila en la base, el comportamiento de siempre: ningún tenant existente cambia.
    [Fact]
    public void TheDefaultQuotationFormatKeepsTodaysNumber()
    {
        var format = DocumentNumberFormat.DefaultFor(DocumentNumberType.Quotation);

        Assert.Equal("QUO-2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }

    [Fact]
    public void TheDefaultOrderFormatKeepsTodaysNumber()
    {
        var format = DocumentNumberFormat.DefaultFor(DocumentNumberType.Order);

        Assert.Equal("PED-2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }

    // El caso que motivó el spec: el cliente PW viene de otro sistema y sigue su propio consecutivo,
    // sin año y sin relleno.
    [Fact]
    public void AFormatWithoutYearIsJustPrefixAndSequence()
    {
        var format = DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: "", minDigits: 1);

        Assert.Equal("PW234235", DocumentNumberFormatter.Format(format, 2026, 234_235L));
    }

    [Fact]
    public void AFormatWithYearAndSixDigitsPadsToItsWidth()
    {
        var format = DocumentNumberFormat.Create("PW-", includeYear: true, yearSeparator: "-", minDigits: 6);

        Assert.Equal("PW-2026-000007", DocumentNumberFormatter.Format(format, 2026, 7L));
    }

    // El consecutivo nunca se recorta: min_digits es un mínimo, no un ancho fijo.
    [Fact]
    public void ASequenceWiderThanMinDigitsIsNotTruncated()
    {
        var format = DocumentNumberFormat.Create("PED-", includeYear: true, yearSeparator: "-", minDigits: 4);

        Assert.Equal("PED-2027-12345", DocumentNumberFormatter.Format(format, 2027, 12_345L));
    }

    [Fact]
    public void TheYearSeparatorCanBeASlash()
    {
        var format = DocumentNumberFormat.Create("FV", includeYear: true, yearSeparator: "/", minDigits: 3);

        Assert.Equal("FV2026/042", DocumentNumberFormatter.Format(format, 2026, 42L));
    }

    /// <summary>
    /// El formateador no recorta ni valida el largo: el que decide es el dominio, que ya tiene el
    /// código de error y el 422 (<c>order.order.number_too_long</c>). Ese consecutivo se pierde y
    /// queda un hueco en la serie — lo mismo que ya pasa con el CUC.
    /// </summary>
    [Fact]
    public void ANumberLongerThanTwentyCharactersIsRejectedByTheDomain()
    {
        var format = DocumentNumberFormat.Create(
            "ABCDEFGHIJ", includeYear: true, yearSeparator: "-", minDigits: 10);

        var number = DocumentNumberFormatter.Format(format, 2026, 1L);

        Assert.Equal("ABCDEFGHIJ2026-0000000001", number);
        Assert.True(number.Length > Order.OrderNumberMaxLength);
        Assert.True(number.Length > Quotation.QuotationNumberMaxLength);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.Create(
                OrderId.New(),
                Guid.CreateVersion7(),
                number,
                QuotationId.New(),
                OrderPaymentStatus.FullPaymentReceived,
                null,
                new MemberId(Guid.CreateVersion7()),
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 1m)],
                DateTimeOffset.UtcNow));

        Assert.Equal("order.order.number_too_long", error.Code);
    }

    [Theory]
    [InlineData("PW ")]          // el espacio no está en [A-Za-z0-9-]
    [InlineData("P_W")]
    [InlineData("ABCDEFGHIJK")]  // once caracteres
    public void AnInvalidPrefixIsRejected(string prefix)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create(prefix, includeYear: false, yearSeparator: "", minDigits: 1));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("--")]
    [InlineData(" ")]
    public void AnInvalidYearSeparatorIsRejected(string yearSeparator)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create("PW", includeYear: true, yearSeparator, minDigits: 1));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AnInvalidMinDigitsIsRejected(int minDigits)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            DocumentNumberFormat.Create("PW", includeYear: false, yearSeparator: "", minDigits));

        Assert.Equal("quotation.numbering.format_invalid", error.Code);
    }

    // Un prefijo vacío es válido: el número es sólo el año y el consecutivo.
    [Fact]
    public void AnEmptyPrefixIsAccepted()
    {
        var format = DocumentNumberFormat.Create("", includeYear: true, yearSeparator: "-", minDigits: 4);

        Assert.Equal("2026-0001", DocumentNumberFormatter.Format(format, 2026, 1L));
    }
}
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~DocumentNumberFormatterTests"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'DocumentNumberFormat' could not be found` (y el mismo error para `DocumentNumberType` y `DocumentNumberFormatter`).

- [ ] **Step 3: Implementar el valor y el formateador**

Crea `src/Modules/Quotations/Modules.Quotations.Application/DocumentNumbering.cs`:

```csharp
using System.Globalization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Los dos documentos con consecutivo propio. Es el <c>document_type</c> de
/// <c>quotations.document_numbering_formats</c>; el adaptador traduce cada valor a su texto.</summary>
public enum DocumentNumberType
{
    Quotation,
    Order,
}

/// <summary>
/// Cómo se arma el número de un documento de un tenant (spec 2026-09-17): el prefijo, si lleva el
/// año, con qué separador, y con cuántos dígitos mínimos se rellena el consecutivo.
///
/// Es un valor puro y validado: quien lo construye pasó por <see cref="Create"/>, así que el
/// formateador no vuelve a comprobar nada. Los mismos tres rangos van como <c>CHECK</c> en la base
/// —es configuración que se escribe a mano y el <c>CHECK</c> es la única red que no depende de quién
/// corra el SQL—, pero se validan también acá: una base restaurada o migrada a mano puede traer una
/// fila que el <c>CHECK</c> nunca vio.
/// </summary>
public sealed record DocumentNumberFormat
{
    public const int PrefixMaxLength = 10;

    public const int MinDigitsLowerBound = 1;

    public const int MinDigitsUpperBound = 10;

    /// <summary>Los cuatro dígitos que emiten hoy <c>QUO-2026-0001</c> y <c>PED-2026-0001</c>.</summary>
    public const int DefaultMinDigits = 4;

    public const string DefaultYearSeparator = "-";

    private const string InvalidFormatCode = "quotation.numbering.format_invalid";

    private static readonly string[] AllowedYearSeparators = ["", "-", "/"];

    private DocumentNumberFormat(string prefix, bool includeYear, string yearSeparator, int minDigits)
    {
        Prefix = prefix;
        IncludeYear = includeYear;
        YearSeparator = yearSeparator;
        MinDigits = minDigits;
    }

    /// <summary>De 0 a 10 caracteres en <c>[A-Za-z0-9-]</c>. Vacío es válido.</summary>
    public string Prefix { get; }

    public bool IncludeYear { get; }

    /// <summary>Sólo se usa si <see cref="IncludeYear"/>. Vacío, <c>-</c> o <c>/</c>.</summary>
    public string YearSeparator { get; }

    /// <summary>Relleno con ceros a la izquierda. Es un mínimo, no un ancho: un consecutivo más
    /// largo sale entero.</summary>
    public int MinDigits { get; }

    public static DocumentNumberFormat Create(
        string prefix,
        bool includeYear,
        string yearSeparator,
        int minDigits)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(yearSeparator);

        if (prefix.Length > PrefixMaxLength || !prefix.All(IsAllowedPrefixCharacter))
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                $"The document number prefix must be at most {PrefixMaxLength} characters " +
                "of letters, digits or hyphens.");
        }

        if (!AllowedYearSeparators.Contains(yearSeparator, StringComparer.Ordinal))
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                "The document number year separator must be empty, '-' or '/'.");
        }

        if (minDigits is < MinDigitsLowerBound or > MinDigitsUpperBound)
        {
            throw new QuotationsDomainException(
                InvalidFormatCode,
                $"The document number minimum digits must be between {MinDigitsLowerBound} " +
                $"and {MinDigitsUpperBound}.");
        }

        return new DocumentNumberFormat(prefix, includeYear, yearSeparator, minDigits);
    }

    /// <summary>El formato de un tenant sin fila: lo que el código emitía antes de que el formato
    /// fuera un dato (decisión 3 del spec).</summary>
    public static DocumentNumberFormat DefaultFor(DocumentNumberType documentType) =>
        new(
            documentType == DocumentNumberType.Order ? "PED-" : "QUO-",
            includeYear: true,
            DefaultYearSeparator,
            DefaultMinDigits);

    private static bool IsAllowedPrefixCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '-';
}

/// <summary>
/// Arma el número a partir del formato, el año y el consecutivo. Reemplaza a
/// <c>QuotationNumberFormatter</c> y <c>OrderNumberFormatter</c>, que tenían el formato fijo en
/// código. Es puro: no consulta nada y no comprueba el largo — de los 20 caracteres se encarga el
/// dominio, que ya tiene el código de error.
/// </summary>
internal static class DocumentNumberFormatter
{
    public static string Format(DocumentNumberFormat format, int year, long sequence)
    {
        var digits = sequence.ToString(
            CultureInfo.InvariantCulture.NumberFormat).PadLeft(format.MinDigits, '0');

        return format.IncludeYear
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{format.Prefix}{year}{format.YearSeparator}{digits}")
            : format.Prefix + digits;
    }
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~DocumentNumberFormatterTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `DocumentNumberFormatterTests` con 15 correctas (7 `[Fact]` + 8 casos de `[Theory]`) y 0 errores; `ArchitectureTests` en verde sin cambiar ninguna regla — `DocumentNumbering.cs` sólo referencia `Modules.Quotations.Domain`, que Application ya referenciaba.

- [ ] **Step 5: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/DocumentNumbering.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentNumberFormatterTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`.

- [ ] **Step 6: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/DocumentNumbering.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/DocumentNumberFormatterTests.cs; git commit -m "feat(quotations): formato de número de documento configurable"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 2: La tabla `quotations.document_numbering_formats`

La fila por (tenant, tipo de documento), con los tres rangos como `CHECK` en la base. Es la primera tabla del repo con `CHECK`: se declaran en el `ToTable` para que la migración generada los traiga.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormat.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:9-44` (el `DbSet` y la llamada en `OnModelCreating`) y el bloque de `ConfigureQuotationNumberCounter` (se agrega el método nuevo justo después)
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddDocumentNumberingFormats.cs` y su `.Designer.cs`, más el `QuotationsDbContextModelSnapshot.cs` que la generación actualiza
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` (una prueba nueva al final, antes de `TheModelHasNoChangesPendingAMigration`)

**Interfaces:**
- Consumes: nada de las tareas anteriores.
- Produces (namespace `Modules.Quotations.Infrastructure.Persistence`):

```csharp
internal sealed class DocumentNumberingFormat
{
    public Guid TenantId { get; init; }
    public string DocumentType { get; init; }
    public string Prefix { get; init; }
    public bool IncludeYear { get; init; }
    public string YearSeparator { get; init; }
    public int MinDigits { get; init; }
}

// en QuotationsDbContext
internal DbSet<DocumentNumberingFormat> DocumentNumberingFormats { get; }
```

- [ ] **Step 1: Escribir la prueba de mapeo que falla**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, reemplaza:

```csharp
    /// <summary>
    /// El modelo y el último snapshot describen la misma base. Renombrar un tipo CLR sin tocar
```

por:

```csharp
    /// <summary>
    /// La tabla de formatos de numeración (spec 2026-09-17). La PK es (tenant, tipo de documento) y
    /// los tres rangos del spec van como CHECK: es configuración que se escribe a mano con SQL, y el
    /// CHECK es la única red que no depende de quién corra ese SQL. Los nombres van a mano, así que
    /// un typo no lo ve el compilador: lo vería la próxima migración, creando otra columna.
    /// </summary>
    [Fact]
    public void DocumentNumberingFormatsMapToTheirTableColumnsAndCheckConstraints()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var format = model.FindEntityType(typeof(DocumentNumberingFormat));

        Assert.NotNull(format);
        Assert.Equal("document_numbering_formats", format.GetTableName());
        Assert.Equal("quotations", format.GetSchema());
        Assert.Equal(
            ["document_type", "include_year", "min_digits", "prefix", "tenant_id", "year_separator"],
            format.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("PK_document_numbering_formats", format.FindPrimaryKey()!.GetName());
        Assert.Equal(
            ["TenantId", "DocumentType"],
            format.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(10, format.FindProperty(nameof(DocumentNumberingFormat.Prefix))!.GetMaxLength());
        Assert.Equal(1, format.FindProperty(nameof(DocumentNumberingFormat.YearSeparator))!.GetMaxLength());
        Assert.Equal(
            ["CK_document_numbering_formats_document_type",
             "CK_document_numbering_formats_min_digits",
             "CK_document_numbering_formats_prefix",
             "CK_document_numbering_formats_year_separator"],
            format.GetCheckConstraints().Select(constraint => constraint.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// El modelo y el último snapshot describen la misma base. Renombrar un tipo CLR sin tocar
```

- [ ] **Step 2: Correrla y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'DocumentNumberingFormat' could not be found`.

- [ ] **Step 3: La entidad**

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormat.cs`:

```csharp
namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// El formato del número de un tipo de documento de un tenant (spec 2026-09-17). Una fila por
/// (tenant, tipo); sin fila, el lector devuelve el default y nada cambia.
///
/// Vive en Infrastructure y no en Domain, mismo criterio que <see cref="QuotationNumberCounter"/>:
/// no es una regla de negocio, es la fila que la configuración escribe. El valor validado que la
/// aplicación consume es <c>DocumentNumberFormat</c>, en Application.
/// </summary>
internal sealed class DocumentNumberingFormat
{
    public Guid TenantId { get; init; }

    /// <summary><c>order</c> o <c>quotation</c>, con su CHECK en la base. El adaptador traduce
    /// desde <c>DocumentNumberType</c>.</summary>
    public string DocumentType { get; init; } = string.Empty;

    public string Prefix { get; init; } = string.Empty;

    public bool IncludeYear { get; init; }

    /// <summary>Vacío, <c>-</c> o <c>/</c>. Sólo se usa si <see cref="IncludeYear"/>.</summary>
    public string YearSeparator { get; init; } = string.Empty;

    public int MinDigits { get; init; }
}
```

- [ ] **Step 4: La configuración de EF**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`, reemplaza:

```csharp
    internal DbSet<QuotationNumberCounter> QuotationNumberCounters => Set<QuotationNumberCounter>();
```

por:

```csharp
    internal DbSet<QuotationNumberCounter> QuotationNumberCounters => Set<QuotationNumberCounter>();

    internal DbSet<DocumentNumberingFormat> DocumentNumberingFormats => Set<DocumentNumberingFormat>();
```

Reemplaza:

```csharp
        ConfigureQuotationNumberCounter(modelBuilder);
        ConfigureOrder(modelBuilder);
```

por:

```csharp
        ConfigureQuotationNumberCounter(modelBuilder);
        ConfigureDocumentNumberingFormat(modelBuilder);
        ConfigureOrder(modelBuilder);
```

Y reemplaza:

```csharp
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
```

por:

```csharp
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    /// <summary>
    /// El formato del número por (tenant, tipo de documento), spec 2026-09-17. Los tres rangos van
    /// como CHECK además de en <c>DocumentNumberFormat.Create</c>: la tabla se escribe con SQL a
    /// mano, y el CHECK es la única red que no depende de quién corra ese SQL. Los nombres de las
    /// restricciones se escriben completos porque EF los usa literales.
    /// </summary>
    private static void ConfigureDocumentNumberingFormat(ModelBuilder modelBuilder)
    {
        var format = modelBuilder.Entity<DocumentNumberingFormat>();
        format.ToTable("document_numbering_formats", "quotations", table =>
        {
            table.HasCheckConstraint(
                "CK_document_numbering_formats_document_type",
                "document_type IN ('order', 'quotation')");
            table.HasCheckConstraint(
                "CK_document_numbering_formats_prefix",
                "prefix ~ '^[A-Za-z0-9-]{0,10}$'");
            table.HasCheckConstraint(
                "CK_document_numbering_formats_year_separator",
                "year_separator IN ('', '-', '/')");
            table.HasCheckConstraint(
                "CK_document_numbering_formats_min_digits",
                "min_digits BETWEEN 1 AND 10");
        });
        format.HasKey(value => new { value.TenantId, value.DocumentType });
        format.Property(value => value.TenantId).HasColumnName("tenant_id");
        format.Property(value => value.DocumentType).HasColumnName("document_type").HasMaxLength(20);
        format.Property(value => value.Prefix).HasColumnName("prefix").HasMaxLength(10);
        format.Property(value => value.IncludeYear).HasColumnName("include_year");
        format.Property(value => value.YearSeparator).HasColumnName("year_separator").HasMaxLength(1);
        format.Property(value => value.MinDigits).HasColumnName("min_digits");
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
```

- [ ] **Step 5: Generar la migración con el factory de diseño**

Sin `--startup-project`: `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`. No necesita base: generar una migración compara el modelo con el snapshot, no con una base.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddDocumentNumberingFormats --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git status --short
```

Esperado: `Done. To undo this action, use 'ef migrations remove'`, y `git status` con tres archivos: el `<timestamp>_AddDocumentNumberingFormats.cs`, su `.Designer.cs` y el `QuotationsDbContextModelSnapshot.cs` modificado. Si aparece **cualquier otro** cambio en el snapshot —una columna, un índice, una tabla que este plan no tocó—, **para y pregunta**: significa que el modelo ya venía desalineado.

- [ ] **Step 6: Documentar la migración generada**

Abre el `<timestamp>_AddDocumentNumberingFormats.cs` generado, comprueba que el `Up` crea la tabla con las seis columnas, la PK `PK_document_numbering_formats` y las cuatro `CheckConstraint`, y ponle el resumen — el resto del archivo **no se toca**:

```csharp
    /// <summary>
    /// El formato del número de cotización y de pedido, por tenant (spec 2026-09-17).
    ///
    /// Tabla vacía y sin backfill: un tenant sin fila sigue emitiendo `QUO-2026-0001` y
    /// `PED-2026-0001`, que es el default del código. Los cuatro CHECK son la red de la
    /// configuración, que se escribe con el SQL del runbook y no por un endpoint.
    /// </summary>
    public partial class AddDocumentNumberingFormats : Migration
```

- [ ] **Step 7: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationsDbContextMappingTests"
```

Esperado: 8 correctas y 0 errores, incluida `TheModelHasNoChangesPendingAMigration` (que es la prueba de que el snapshot quedó al día).

- [ ] **Step 8: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormat.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs
```

Esperado: ningún diagnóstico que no sea `ENDOFLINE` o `CHARSET`. Los archivos generados por `dotnet ef` no se pasan por `--include`: nacen con BOM y `dotnet format` los reescribiría, igual que los demás `Designer.cs` del repo.

- [ ] **Step 9: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormat.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs; git commit -m "feat(quotations): tabla de formatos de numeración por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git show --stat HEAD
```

Esperado: el commit con cinco archivos (entidad, DbContext, migración, Designer, snapshot) más la prueba, y el `Select-String` sin salida.

---

### Task 3: El puerto `IDocumentNumberingFormatLookup` y su adaptador

Leer la fila del tenant, o el default si no hay. Una lectura por (tenant, tipo) y por request, sin caché entre requests: la tabla se escribe a mano y el cambio tiene que verse en el documento siguiente.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IDocumentNumberingFormatLookup.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormatLookup.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:49` (una línea después del registro de `IQuotationNumberGenerator`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingFormatLookupTests.cs` (nuevo)

**Interfaces:**
- Consumes: `DocumentNumberFormat`, `DocumentNumberType` (Task 1); `DocumentNumberingFormat`, `QuotationsDbContext` (Task 2).
- Produces:

```csharp
// namespace Modules.Quotations.Application
public interface IDocumentNumberingFormatLookup
{
    Task<DocumentNumberFormat> GetAsync(
        Guid tenantId, DocumentNumberType documentType, CancellationToken cancellationToken);
}

// namespace Modules.Quotations.Infrastructure.Persistence
internal sealed class DocumentNumberingFormatLookup(QuotationsDbContext dbContext)
    : IDocumentNumberingFormatLookup;
```

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingFormatLookupTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El lector del formato de numeración (spec 2026-09-17): la fila del tenant, o el default si no
/// hay. Contra la base de verdad y no contra un doble, porque lo que se prueba es justo el SQL y el
/// mapeo — y los CHECK, que sólo existen ahí.
/// </summary>
public sealed class DocumentNumberingFormatLookupTests
{
    [Fact]
    public async Task ATenantWithoutARowGetsTheDefaultFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();

        var quotation = await LookupAsync(factory, tenantId, DocumentNumberType.Quotation);
        var order = await LookupAsync(factory, tenantId, DocumentNumberType.Order);

        Assert.Equal("QUO-", quotation.Prefix);
        Assert.Equal("PED-", order.Prefix);
        Assert.True(quotation.IncludeYear);
        Assert.True(order.IncludeYear);
        Assert.Equal("-", quotation.YearSeparator);
        Assert.Equal(4, quotation.MinDigits);
        Assert.Equal(4, order.MinDigits);
    }

    [Fact]
    public async Task ATenantWithARowGetsItsOwnFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 1);

        var order = await LookupAsync(factory, tenantId, DocumentNumberType.Order);

        Assert.Equal("PW", order.Prefix);
        Assert.False(order.IncludeYear);
        Assert.Equal("", order.YearSeparator);
        Assert.Equal(1, order.MinDigits);
    }

    // La fila es por (tenant, tipo): configurar pedidos no toca cotizaciones.
    [Fact]
    public async Task TheRowOfOneDocumentTypeDoesNotAffectTheOther()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 1);

        var quotation = await LookupAsync(factory, tenantId, DocumentNumberType.Quotation);

        Assert.Equal("QUO-", quotation.Prefix);
        Assert.True(quotation.IncludeYear);
    }

    // La fila de un tenant no la ve otro: el aislamiento es por la PK, no por un filtro del handler.
    [Fact]
    public async Task TheRowOfOneTenantDoesNotAffectAnother()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var configured = Guid.CreateVersion7();
        var untouched = Guid.CreateVersion7();
        await SetDocumentNumberFormatAsync(factory, configured, "order", "PW", includeYear: false, "", 1);

        var order = await LookupAsync(factory, untouched, DocumentNumberType.Order);

        Assert.Equal("PED-", order.Prefix);
    }

    /// <summary>
    /// El CHECK es la única red de una tabla que se escribe a mano. Un min_digits de 11 no entra, así
    /// que el lector nunca ve un formato imposible.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRejectsAnOutOfRangeFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            SetDocumentNumberFormatAsync(factory, tenantId, "order", "PW", includeYear: false, "", 11));

        Assert.Contains(
            "CK_document_numbering_formats_min_digits",
            error.InnerException!.Message,
            StringComparison.Ordinal);
    }

    private static async Task<DocumentNumberFormat> LookupAsync(
        QepApiFactory factory, Guid tenantId, DocumentNumberType documentType)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentNumberingFormatLookup>()
            .GetAsync(tenantId, documentType, TestContext.Current.CancellationToken);
    }

    /// <summary>El mismo UPSERT del runbook del README, palabra por palabra: si el runbook deja de
    /// funcionar, estas pruebas se caen con él.</summary>
    internal static async Task SetDocumentNumberFormatAsync(
        QepApiFactory factory,
        Guid tenantId,
        string documentType,
        string prefix,
        bool includeYear,
        string yearSeparator,
        int minDigits)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.document_numbering_formats
                   (tenant_id, document_type, prefix, include_year, year_separator, min_digits)
            VALUES ({tenantId}, {documentType}, {prefix}, {includeYear}, {yearSeparator}, {minDigits})
            ON CONFLICT (tenant_id, document_type) DO UPDATE
            SET prefix = EXCLUDED.prefix, include_year = EXCLUDED.include_year,
                year_separator = EXCLUDED.year_separator, min_digits = EXCLUDED.min_digits
            """,
            TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Correrlas y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~DocumentNumberingFormatLookupTests"
```

Esperado: falla la compilación con `error CS0246: The type or namespace name 'IDocumentNumberingFormatLookup' could not be found`.

- [ ] **Step 3: El puerto**

Crea `src/Modules/Quotations/Modules.Quotations.Application/IDocumentNumberingFormatLookup.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>
/// El formato con el que este tenant numera este tipo de documento (spec 2026-09-17). Siempre
/// devuelve uno: sin fila, el default de <see cref="DocumentNumberFormat.DefaultFor"/>, que es lo
/// que el código emitía antes de que el formato fuera un dato. «Sin fila» no es un error.
///
/// El adaptador vive en Infrastructure porque la fila está en el esquema <c>quotations</c>; el
/// puerto vive acá porque quién lee el formato y cuándo es decisión del flujo.
/// </summary>
public interface IDocumentNumberingFormatLookup
{
    Task<DocumentNumberFormat> GetAsync(
        Guid tenantId,
        DocumentNumberType documentType,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: El adaptador**

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormatLookup.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Lee la fila de <c>quotations.document_numbering_formats</c>, o devuelve el default si no hay.
/// Una consulta por (tenant, tipo) y por request, sin caché entre requests: la tabla se escribe a
/// mano con el runbook, y un formato nuevo tiene que verse en el documento siguiente, no en el
/// despliegue siguiente.
///
/// Los rangos se revalidan en <see cref="DocumentNumberFormat.Create"/> aunque la base los tenga
/// como CHECK: una base restaurada o migrada a mano puede traer una fila que el CHECK nunca vio, y
/// entonces la emisión falla con código de dominio y 422 en vez de emitir un número a medias.
/// </summary>
internal sealed class DocumentNumberingFormatLookup(QuotationsDbContext dbContext)
    : IDocumentNumberingFormatLookup
{
    public async Task<DocumentNumberFormat> GetAsync(
        Guid tenantId,
        DocumentNumberType documentType,
        CancellationToken cancellationToken)
    {
        var columnValue = ColumnValueOf(documentType);
        var row = await dbContext.DocumentNumberingFormats
            .AsNoTracking()
            .SingleOrDefaultAsync(
                format => format.TenantId == tenantId && format.DocumentType == columnValue,
                cancellationToken);

        return row is null
            ? DocumentNumberFormat.DefaultFor(documentType)
            : DocumentNumberFormat.Create(row.Prefix, row.IncludeYear, row.YearSeparator, row.MinDigits);
    }

    private static string ColumnValueOf(DocumentNumberType documentType) => documentType switch
    {
        DocumentNumberType.Quotation => "quotation",
        DocumentNumberType.Order => "order",
        _ => throw new ArgumentOutOfRangeException(nameof(documentType)),
    };
}
```

- [ ] **Step 5: El registro**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`, reemplaza:

```csharp
        services.AddScoped<IQuotationNumberGenerator, QuotationNumberGenerator>();
```

por:

```csharp
        services.AddScoped<IQuotationNumberGenerator, QuotationNumberGenerator>();
        // Spec 2026-09-17: el formato del número es un dato por tenant. Scoped porque lee por el
        // DbContext del request, igual que los generadores de consecutivo.
        services.AddScoped<IDocumentNumberingFormatLookup, DocumentNumberingFormatLookup>();
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~DocumentNumberingFormatLookupTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: `DocumentNumberingFormatLookupTests` con 5 correctas y 0 errores; `ArchitectureTests` en verde — `IDocumentNumberingFormatLookup` no arrastra EF ni Npgsql a Application, que es lo que verifica `ApplicationDoesNotReferencePersistenceLibraries`.

- [ ] **Step 7: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/IDocumentNumberingFormatLookup.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormatLookup.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingFormatLookupTests.cs
```

- [ ] **Step 8: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IDocumentNumberingFormatLookup.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/DocumentNumberingFormatLookup.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingFormatLookupTests.cs; git commit -m "feat(quotations): lectura del formato de numeración del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 4: Numerar pedidos con el formato del tenant

`ConvertQuotationToOrderHandler` lee el formato, pide el consecutivo con el año del tenant o con `0`, y formatea. `OrderNumberFormatter` y su prueba se van en este mismo commit: quedan sin un solo consumidor.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs:49-63` (la firma) y `:92-97` (la emisión)
- Delete: `src/Modules/Quotations/Modules.Quotations.Application/OrderNumberFormatter.cs`
- Delete: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs:68-97` (la prueba existente `ConvertCreatesTheOrderAndLeavesTheQuotationConverted` se deja como está: es la prueba de que un tenant sin fila no cambia) y una prueba nueva en el mismo archivo

**Interfaces:**
- Consumes: `IDocumentNumberingFormatLookup`, `DocumentNumberType`, `DocumentNumberFormat`, `DocumentNumberFormatter` (Tasks 1 y 3); `ITenantClock`, `TenantCalendar` (spec 1); `IOrderNumberGenerator` (sin cambios de firma).
- Produces: `ConvertQuotationToOrderHandler(IQuotationRepository, IOrderRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationCustomerLookup, IQuotationFileLookup, IPaymentProofPublisher, IOrderPaymentProofEventPublisher, IOrderNumberGenerator, IDocumentNumberingFormatLookup numberingFormats, IMembershipDirectory, IExecutionContext, ITenantClock, IValidator<ConvertQuotationToOrderCommand>)`.

- [ ] **Step 1: Escribir la prueba que falla**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs`, agrega esta prueba justo **después** de `ConvertCreatesTheOrderAndLeavesTheQuotationConverted` (o sea, después del cierre `}` de ese método):

```csharp
    /// <summary>
    /// El caso que motivó el spec 2026-09-17 de numeración: un tenant que viene de otro sistema
    /// sigue su propio consecutivo, con su prefijo y sin año. El contador es la fila `year = 0`, la
    /// que no se reinicia. Reloj fijo en la frontera de fin de año de Bogotá para que el año no
    /// dependa del calendario de la máquina — y para dejar a la vista que acá el año no se usa.
    /// </summary>
    [Fact]
    public async Task ConvertUsesThePrefixAndCounterConfiguredForTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "order", "PW", includeYear: false, "", 1);
        // El siguiente número que queremos que salga, no el último que emitió el sistema viejo.
        await SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var first = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        var second = await ConvertOneAsync(client, factory, tenantId, clientId, productId);

        Assert.Equal("PW234235", first);
        Assert.Equal("PW234236", second);
    }

    private static async Task<string> ConvertOneAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid clientId, Guid productId)
    {
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                "Pago verificado",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order.OrderNumber;
    }

    /// <summary>El paso 2 del runbook del README, palabra por palabra: el UPSERT con GREATEST que
    /// fija el siguiente número sin poder retroceder.</summary>
    internal static async Task SetOrderCounterAsync(
        QepApiFactory factory, Guid tenantId, int year, long nextValue)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, {nextValue})
            ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = GREATEST(quotations.order_number_counters.next_value, EXCLUDED.next_value)
            """,
            TestContext.Current.CancellationToken);
    }
```

Y agrega los dos `using` que esa prueba necesita (`Modules.Quotations.Infrastructure.Persistence` ya está). Reemplaza el bloque completo del encabezado:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

por:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

- [ ] **Step 2: Correrla y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~OrderApiTests.ConvertUsesThePrefixAndCounterConfiguredForTheTenant"
```

Esperado: falla con `Assert.Equal() Failure: Strings differ`, esperado `PW234235` y real `PED-2026-0001` — el handler todavía ignora el formato y usa la fila del año. Si falla por otra cosa (un 4xx en la siembra), **para y pregunta**: no es el RED que se busca.

- [ ] **Step 3: Cablear `ConvertQuotationToOrderHandler`**

En `ConvertQuotationToOrder.cs`, reemplaza:

```csharp
    IOrderNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
```

por:

```csharp
    IOrderNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
```

Reemplaza:

```csharp
        // El año del pedido es el del día del tenant (spec 2026-09-17, punto 2b).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var sequence = await numberGenerator.NextAsync(command.TenantId, year, cancellationToken);
        var orderNumber = OrderNumberFormatter.Format(year, sequence);
```

por:

```csharp
        // El año del pedido es el del día del tenant (spec 2026-09-17, punto 2b).
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        // El formato es un dato del tenant (spec 2026-09-17 de numeración). Un formato sin año usa
        // la fila `year = 0` del contador, que no se reinicia; con año, la fila del año, como
        // siempre. El contador no depende del prefijo: cambiar `PED-` por `PW` no reinicia la serie.
        var format = await numberingFormats.GetAsync(
            command.TenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
        var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);
```

- [ ] **Step 4: Borrar `OrderNumberFormatter` y su prueba**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git rm src/Modules/Quotations/Modules.Quotations.Application/OrderNumberFormatter.cs
git rm tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs
```

Los tres casos que probaba `OrderNumberFormatterTests` están cubiertos por `DocumentNumberFormatterTests`: `PED-2026-0001` en `TheDefaultOrderFormatKeepsTodaysNumber` y `PED-2027-12345` en `ASequenceWiderThanMinDigitsIsNotTruncated`.

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~OrderApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
```

Esperado: todo en verde. En particular `ConvertCreatesTheOrderAndLeavesTheQuotationConverted` sigue esperando `PED-2026-` sin tocarla: es la prueba de que un tenant **sin fila** no cambia.

- [ ] **Step 6: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs
```

- [ ] **Step 7: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs src/Modules/Quotations/Modules.Quotations.Application/OrderNumberFormatter.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderApiTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderNumberFormatterTests.cs; git commit -m "feat(quotations): numerar pedidos con el formato del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
git show --stat HEAD
```

Esperado: cuatro archivos, dos de ellos borrados.

---

### Task 5: Numerar cotizaciones con el formato del tenant

Lo mismo en `CreateQuotationHandler`, y se va `QuotationNumberFormatter` con el cref que lo nombraba.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs:28-39` (la firma) y `:63-69` (la emisión)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationNumberGenerator.cs:3-9` (la documentación que nombra el formateador que se borra)
- Delete: `src/Modules/Quotations/Modules.Quotations.Application/QuotationNumberFormatter.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs` (una prueba nueva; la existente `CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor` se deja como está)

**Interfaces:**
- Consumes: lo mismo que la Task 4, con `DocumentNumberType.Quotation`.
- Produces: `CreateQuotationHandler(IQuotationRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationCustomerLookup, IQuotationCompanyLookup, IQuotationNumberGenerator, IDocumentNumberingFormatLookup numberingFormats, IMembershipDirectory, IExecutionContext, ITenantClock, IValidator<CreateQuotationCommand>)`.

- [ ] **Step 1: Escribir la prueba que falla**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs`, agrega esta prueba justo **después** de `CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor`:

```csharp
    /// <summary>
    /// La cotización numera con el formato del tenant (spec 2026-09-17 de numeración), y el
    /// consecutivo sin año sale de la fila `year = 0`, que no se reinicia. Reloj fijo en la frontera
    /// de Bogotá: el año no se usa, y que la prueba no dependa del calendario de la máquina.
    /// </summary>
    [Fact]
    public async Task CreateUsesThePrefixAndCounterConfiguredForTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "quotation", "CT", includeYear: false, "", 5);
        await SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var first = await CreateQuotationAsync(client, tenantId, clientId);
        var second = await CreateQuotationAsync(client, tenantId, clientId);

        Assert.Equal("CT90001", first.QuotationNumber);
        Assert.Equal("CT90002", second.QuotationNumber);
    }

    /// <summary>El paso 2 del runbook del README para cotizaciones: el mismo UPSERT con GREATEST,
    /// sobre <c>quotation_number_counters</c>.</summary>
    internal static async Task SetQuotationCounterAsync(
        QepApiFactory factory, Guid tenantId, int year, long nextValue)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.quotation_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, {nextValue})
            ON CONFLICT (tenant_id, year) DO UPDATE
            SET next_value = GREATEST(quotations.quotation_number_counters.next_value, EXCLUDED.next_value)
            """,
            TestContext.Current.CancellationToken);
    }
```

Y reemplaza el bloque de `using` del archivo:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

por:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

- [ ] **Step 2: Correrla y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationApiTests.CreateUsesThePrefixAndCounterConfiguredForTheTenant"
```

Esperado: falla con `Assert.Equal() Failure: Strings differ`, esperado `CT90001` y real `QUO-2026-0001`.

- [ ] **Step 3: Cablear `CreateQuotationHandler`**

En `CreateQuotation.cs`, reemplaza:

```csharp
    IQuotationNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
```

por:

```csharp
    IQuotationNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    IMembershipDirectory membershipDirectory,
```

Reemplaza:

```csharp
        // El año del consecutivo es el del día del tenant, no el de UTC (spec 2026-09-17, punto 2a):
        // en Bogotá, el 31 de diciembre desde las 19:00 UTC ya es el año siguiente.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var sequence = await numberGenerator.NextAsync(command.TenantId, year, cancellationToken);
        var quotationNumber = QuotationNumberFormatter.Format(year, sequence);
```

por:

```csharp
        // El año del consecutivo es el del día del tenant, no el de UTC (spec 2026-09-17, punto 2a):
        // en Bogotá, el 31 de diciembre desde las 19:00 UTC ya es el año siguiente.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        // El formato es un dato del tenant (spec 2026-09-17 de numeración). Sin año, el consecutivo
        // sale de la fila `year = 0`, que no se reinicia; con año, de la fila del año.
        var format = await numberingFormats.GetAsync(
            command.TenantId, DocumentNumberType.Quotation, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        var sequence = await numberGenerator.NextAsync(command.TenantId, counterYear, cancellationToken);
        var quotationNumber = DocumentNumberFormatter.Format(format, year, sequence);
```

- [ ] **Step 4: Corregir la documentación de `IQuotationNumberGenerator`**

En `src/Modules/Quotations/Modules.Quotations.Application/IQuotationNumberGenerator.cs`, reemplaza:

```csharp
/// <summary>
/// Emite el próximo consecutivo del número de cotización de un tenant para un año dado. Mismo
/// mecanismo que <c>ICucGenerator</c> en Customers: un contador atómico por tenant (acá, también
/// por año) resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. El formato final
/// (<c>QUO-2026-0001</c>) lo arma <see cref="QuotationNumberFormatter"/> — este puerto sólo
/// resuelve la concurrencia del consecutivo.
/// </summary>
```

por:

```csharp
/// <summary>
/// Emite el próximo consecutivo del número de cotización de un tenant para un año dado. Mismo
/// mecanismo que <c>ICucGenerator</c> en Customers: un contador atómico por tenant (acá, también
/// por año) resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. El formato final lo arma
/// <c>DocumentNumberFormatter</c> con el formato del tenant — este puerto sólo resuelve la
/// concurrencia del consecutivo.
///
/// El <c>year</c> es el del día del tenant cuando el formato lleva año, y <b>0</b> cuando no lo
/// lleva (spec 2026-09-17): la fila <c>year = 0</c> es el contador que no se reinicia.
/// </summary>
```

Y en `src/Modules/Quotations/Modules.Quotations.Application/IOrderNumberGenerator.cs`, reemplaza:

```csharp
/// <summary>Mismo mecanismo que <see cref="IQuotationNumberGenerator"/>: un contador atómico por
/// (tenant, año), resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure.</summary>
```

por:

```csharp
/// <summary>Mismo mecanismo que <see cref="IQuotationNumberGenerator"/>: un contador atómico por
/// (tenant, año), resuelto con <c>UPDATE ... RETURNING</c> en Infrastructure. Un formato sin año
/// pide el consecutivo con <c>year = 0</c> (spec 2026-09-17).</summary>
```

- [ ] **Step 5: Borrar `QuotationNumberFormatter`**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git rm src/Modules/Quotations/Modules.Quotations.Application/QuotationNumberFormatter.cs
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationApiTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todo en verde, con `CreateReturnsADraftWithAGeneratedNumberAndTheResolvedAdvisor` intacta esperando `QUO-2026-`.

- [ ] **Step 7: Barrido de referencias colgadas**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-ChildItem -Recurse -Include *.cs,*.md -Path src,tests,docs | Select-String -Pattern "OrderNumberFormatter|QuotationNumberFormatter"
```

Esperado: sólo coincidencias dentro de `docs/superpowers/specs/` y `docs/superpowers/plans/` (los specs y este plan, que los nombran para decir que se reemplazan). **Ninguna** en `src/` ni en `tests/`.

- [ ] **Step 8: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/IQuotationNumberGenerator.cs src/Modules/Quotations/Modules.Quotations.Application/IOrderNumberGenerator.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs
```

- [ ] **Step 9: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/CreateQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/IQuotationNumberGenerator.cs src/Modules/Quotations/Modules.Quotations.Application/IOrderNumberGenerator.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationNumberFormatter.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationApiTests.cs; git commit -m "feat(quotations): numerar cotizaciones con el formato del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 6: Las pruebas de integración que faltan del spec

Dos tenants con formatos distintos, un tenant sin fila que sigue igual en los dos documentos, y el `GREATEST` del runbook corrido dos veces. Van en un archivo propio: son las pruebas de la **feature**, no de un endpoint.

**Files:**
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingApiTests.cs`

**Interfaces:**
- Consumes: `DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync` (Task 3), `OrderApiTests.SetOrderCounterAsync` (Task 4), `QuotationApiTests.SetQuotationCounterAsync` (Task 5), y el harness (`RegisterTenantAsync`, `CreateActiveCustomerAsync`, `CreateProductWithScalesAsync`, `CreateSentQuotationAsync`, `CreateAvailablePaymentProofFileAsync`, `CreateQuotationAsync`, `NewYearsEveInBogota`).
- Produces: nada de producción.

- [ ] **Step 1: Escribir las pruebas**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingApiTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La numeración configurable por tenant, de punta a punta (spec 2026-09-17). El reloj va fijo en la
/// frontera de fin de año de Bogotá: así el año del tenant es 2026 literal en todas las aserciones y
/// ninguna se vuelve intermitente cada 31 de diciembre.
/// </summary>
public sealed class DocumentNumberingApiTests
{
    // Dos tenants, dos formatos, dos contadores. El aislamiento es por la PK de las tres tablas.
    [Fact]
    public async Task TwoTenantsWithDifferentFormatsDoNotInterfere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);

        var (pwTenantId, _, pwClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = pwClient;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, pwTenantId, "order", "PW", includeYear: false, "", 1);
        await OrderApiTests.SetOrderCounterAsync(factory, pwTenantId, year: 0, nextValue: 234_235L);

        var (wideTenantId, _, wideClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = wideClient;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, wideTenantId, "order", "PW-", includeYear: true, "-", 6);
        await OrderApiTests.SetOrderCounterAsync(factory, wideTenantId, year: 2026, nextValue: 7L);

        var pwNumber = await ConvertAsync(pwClient, factory, pwTenantId);
        var wideNumber = await ConvertAsync(wideClient, factory, wideTenantId);
        var pwSecond = await ConvertAsync(pwClient, factory, pwTenantId);

        Assert.Equal("PW234235", pwNumber);
        Assert.Equal("PW-2026-000007", wideNumber);
        // El segundo pedido del tenant PW sigue su propia serie: el otro tenant no la tocó.
        Assert.Equal("PW234236", pwSecond);
    }

    /// <summary>
    /// La prueba de que nada cambia para los que ya están: un tenant sin fila sigue emitiendo
    /// `QUO-2026-…` y `PED-2026-…`, con los cuatro dígitos de siempre.
    /// </summary>
    [Fact]
    public async Task ATenantWithoutARowKeepsTheDefaultNumbersForBothDocuments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var orderNumber = await ConvertQuotationAsync(client, factory, tenantId, quotation);

        Assert.Equal("QUO-2026-0001", quotation.QuotationNumber);
        Assert.Equal("PED-2026-0001", orderNumber);
    }

    /// <summary>
    /// La regla «el consecutivo sólo avanza» (decisión 4 del spec), en una línea: el UPSERT del
    /// runbook con GREATEST. Correrlo dos veces —o con un número menor, que es el error real: copiar
    /// el SQL viejo— no retrocede el contador, porque un número repetido chocaría contra
    /// `IX_orders_tenant_number`.
    /// </summary>
    [Fact]
    public async Task TheRunbookUpsertNeverRewindsTheCounter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var tenantId = Guid.CreateVersion7();

        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 100L);

        Assert.Equal(234_235L, await OrderCounterAsync(factory, tenantId, year: 0));

        // Y sí avanza cuando el número nuevo es mayor.
        await OrderApiTests.SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 300_000L);

        Assert.Equal(300_000L, await OrderCounterAsync(factory, tenantId, year: 0));
    }

    /// <summary>Lo mismo sobre `quotation_number_counters`: son dos tablas y dos comandos del
    /// runbook, así que el GREATEST se prueba en las dos.</summary>
    [Fact]
    public async Task TheRunbookUpsertNeverRewindsTheQuotationCounter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var tenantId = Guid.CreateVersion7();

        await QuotationApiTests.SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 90_001L);
        await QuotationApiTests.SetQuotationCounterAsync(factory, tenantId, year: 0, nextValue: 5L);

        Assert.Equal(90_001L, await QuotationCounterAsync(factory, tenantId, year: 0));
    }

    private static async Task<string> ConvertAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        return await ConvertQuotationAsync(client, factory, tenantId, quotation);
    }

    private static async Task<string> ConvertQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, QuotationResponse quotation)
    {
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                "Pago verificado",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order.OrderNumber;
    }

    private static async Task<long> OrderCounterAsync(QepApiFactory factory, Guid tenantId, int year)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var values = await dbContext.Database
            .SqlQuery<long>(
                $"""
                SELECT next_value AS "Value" FROM quotations.order_number_counters
                WHERE tenant_id = {tenantId} AND year = {year}
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(values);
    }

    private static async Task<long> QuotationCounterAsync(QepApiFactory factory, Guid tenantId, int year)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var values = await dbContext.Database
            .SqlQuery<long>(
                $"""
                SELECT next_value AS "Value" FROM quotations.quotation_number_counters
                WHERE tenant_id = {tenantId} AND year = {year}
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(values);
    }
}
```

**Antes de correr, comprueba la ruta de conversión.** `ConvertQuotationAsync` la escribe a mano porque `OrderUrl` es privado de `OrderApiTests`. Si `OrderApiTests.OrderUrl` no arma exactamente `/api/v1/tenants/{tenantId}/quotations/{quotationId}/order`, usa la que arme ese helper — no inventes la ruta:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Select-String -Path tests\Modules\Quotations\Modules.Quotations.IntegrationTests\OrderApiTests.cs -Pattern "OrderUrl"
```

- [ ] **Step 2: Correr y ver el resultado**

Estas cuatro pruebas nacen en verde: el comportamiento ya lo pusieron las Tasks 4 y 5. No son el RED de nada — son la cobertura que el spec pide y que ninguna prueba anterior cubre (dos tenants, el default en los dos documentos a la vez, y el `GREATEST`).

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~DocumentNumberingApiTests"
```

Esperado: 4 correctas y 0 errores. Si `TheRunbookUpsertNeverRewindsTheCounter` falla, el `GREATEST` del runbook está mal y el README no se escribe hasta arreglarlo: **para y avisa**, no cambies la aserción.

- [ ] **Step 3: Comprobar que sí atrapan una regresión**

Revierte a mano, y sólo en tu copia de trabajo, el `counterYear` de `ConvertQuotationToOrder.cs` (déjalo en `year` en vez de `format.IncludeYear ? year : 0`), corre `TwoTenantsWithDifferentFormatsDoNotInterfere`, comprueba que se pone roja, y **deshaz el cambio**:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git diff --stat
git checkout -- src/Modules/Quotations/Modules.Quotations.Application/ConvertQuotationToOrder.cs
git status --short
```

Esperado: la prueba roja con `PW1` en vez de `PW234235` (el contador del año 2026 arranca en 1), y después del `checkout` el `git status` sólo con el archivo de pruebas nuevo.

- [ ] **Step 4: Formato**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet format Backend.slnx --verify-no-changes --no-restore --include tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingApiTests.cs
```

- [ ] **Step 5: Commit**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add tests/Modules/Quotations/Modules.Quotations.IntegrationTests/DocumentNumberingApiTests.cs; git commit -m "test(quotations): numeración configurable de punta a punta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 7: El runbook en el README

Las tres operaciones del spec, en la forma en que las va a correr una persona en Windows: `psql` desde PowerShell, con el SQL en archivo y sin imprimir la contraseña.

**Files:**
- Modify: `README.md` (una sección nueva, inmediatamente **antes** de la línea `## API implementada`)

**Interfaces:**
- Consumes: la tabla y los contadores (Tasks 2 a 5).
- Produces: nada de código.

- [ ] **Step 1: Escribir la sección**

En `README.md`, reemplaza:

```markdown
## API implementada
```

por:

```markdown
## Numeración de documentos por tenant

El número de cotización y el de pedido salen de un formato **por tenant y por tipo de documento**,
guardado en `quotations.document_numbering_formats`. Sin fila para ese tenant, el formato es el de
siempre y no hay nada que hacer: `QUO-2026-0001` y `PED-2026-0001`.

Se configura con SQL, no con un endpoint ni una pantalla: la numeración es una decisión de negocio
que se toma al montar al cliente, y el permiso más cercano (`platform.*`) lo tiene el `admin` **del
tenant**, o sea el admin del cliente. Spec:
[`docs/superpowers/specs/2026-09-17-numeracion-configurable-design.md`](docs/superpowers/specs/2026-09-17-numeracion-configurable-design.md).

| Columna          | Qué vale                                                        |
| ---------------- | --------------------------------------------------------------- |
| `tenant_id`      | El tenant. Junto a `document_type` es la clave primaria          |
| `document_type`  | `order` o `quotation`                                           |
| `prefix`         | De 0 a 10 caracteres en `[A-Za-z0-9-]`. El guion va en el prefijo, si lo quieres |
| `include_year`   | `true` o `false`                                                 |
| `year_separator` | `''`, `-` o `/`. Sólo se usa si `include_year`                   |
| `min_digits`     | De 1 a 10. Relleno con ceros a la izquierda; es un mínimo, no un ancho |

| Caso                       | `prefix` | `include_year` | `year_separator` | `min_digits` | Resultado        |
| -------------------------- | -------- | -------------- | ---------------- | ------------ | ---------------- |
| Default actual (sin fila)  | `PED-`   | `true`         | `-`              | `4`          | `PED-2026-0001`  |
| Cliente que trae su serie  | `PW`     | `false`        | `''`             | `1`          | `PW234235`       |
| Con año y seis dígitos     | `PW-`    | `true`         | `-`              | `6`          | `PW-2026-000007` |

Los cuatro rangos son `CHECK` en la base: un `min_digits` de 11 o un `document_type` inventado no
entran, sin importar quién corra el SQL.

### Configurar un tenant

Los cuerpos van a archivo y se mandan con `-f`: PowerShell rompe las comillas al pasar SQL largo por
`-c`. La contraseña la pide `psql`; **no la saques con `kubectl get secret`, que imprime los valores**
(ver [Reglas duras](CLAUDE.md)).

1. **El formato.** Guarda esto como `numeracion.sql`, con el tenant y los valores que correspondan:

   ```sql
   \set tenant_id '00000000-0000-0000-0000-000000000000'

   INSERT INTO quotations.document_numbering_formats
          (tenant_id, document_type, prefix, include_year, year_separator, min_digits)
   VALUES (:'tenant_id', 'order', 'PW', false, '', 1)
   ON CONFLICT (tenant_id, document_type) DO UPDATE
   SET prefix = EXCLUDED.prefix, include_year = EXCLUDED.include_year,
       year_separator = EXCLUDED.year_separator, min_digits = EXCLUDED.min_digits;
   ```

   Repite el `INSERT` con `'quotation'` si también quieres cambiar las cotizaciones: son dos filas
   independientes, y configurar pedidos no toca cotizaciones.

2. **El consecutivo, si el cliente viene de otro sistema.** `:siguiente_numero` es **el próximo
   número que quieres que salga**, no el último que emitió el sistema viejo. `year = 0` es la fila
   del formato **sin** año; con año va el año (`2026`).

   ```sql
   INSERT INTO quotations.order_number_counters (tenant_id, year, next_value)
   VALUES (:'tenant_id', 0, 234235)
   ON CONFLICT (tenant_id, year) DO UPDATE
   SET next_value = GREATEST(quotations.order_number_counters.next_value, EXCLUDED.next_value);
   ```

   El `GREATEST` es la regla «el consecutivo sólo avanza» en una línea: correr el mismo SQL dos
   veces, o con un número menor, **no** retrocede el contador. Un número repetido chocaría contra
   `IX_orders_tenant_number` y el alta fallaría. La tabla de cotizaciones es
   `quotations.quotation_number_counters`, con las mismas columnas.

3. **Correrlo y verificar.**

   ```powershell
   psql -h <host> -p <puerto> -U <usuario> -d <base> -v ON_ERROR_STOP=1 -f numeracion.sql
   psql -h <host> -p <puerto> -U <usuario> -d <base> -c "SELECT * FROM quotations.document_numbering_formats WHERE tenant_id = '<tenant-id>'"
   psql -h <host> -p <puerto> -U <usuario> -d <base> -c "SELECT * FROM quotations.order_number_counters WHERE tenant_id = '<tenant-id>'"
   ```

   No hace falta reiniciar el pod: el formato se lee en cada emisión, así que el siguiente documento
   ya sale con el nuevo.

### Qué pasa cuando cambias un formato a mitad de camino

- **Cambiar el prefijo no reinicia nada.** El contador no depende del prefijo: pasar de `PED-` a
  `PW` deja la serie donde iba.
- **Pasar de con año a sin año (o al revés) cambia de fila de contador**, porque una es la del año y
  la otra es la de `year = 0`. Por eso el paso 2 existe: fija el siguiente número de la fila nueva.
- **Lo ya emitido no se reescribe.** No hay backfill ni renumeración: el formato nuevo aplica a lo
  que se emita de aquí en adelante.
- **Si el número no cabe en 20 caracteres**, la emisión falla con `422` y el código
  `order.order.number_too_long` o `quotation.quotation.number_too_long`. Ese consecutivo se pierde y
  queda un hueco en la serie. Diez de prefijo, más el año con separador, más diez dígitos no caben:
  haz la cuenta antes de configurar.

## API implementada
```

- [ ] **Step 2: Comprobar los enlaces y el SQL contra el código**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Test-Path docs\superpowers\specs\2026-09-17-numeracion-configurable-design.md
Test-Path CLAUDE.md
Select-String -Path README.md -Pattern "document_numbering_formats|GREATEST|order_number_counters"
Select-String -Path tests\Modules\Quotations\Modules.Quotations.IntegrationTests\DocumentNumberingFormatLookupTests.cs -Pattern "ON CONFLICT"
```

Esperado: los dos `Test-Path` en `True`, y el `ON CONFLICT (tenant_id, document_type) DO UPDATE` del README idéntico al que corren las pruebas. Si difieren, gana el que está en verde en las pruebas y el README se corrige.

- [ ] **Step 3: Commit**

`dotnet format` no aplica a un `.md`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
if ((git branch --show-current) -ne "feature/numeracion-configurable") { throw "ABORT: rama equivocada" }; git add README.md; git commit -m "docs(quotations): runbook de numeración por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task 8: Verificación final

**Files:**
- Ninguno, salvo que la verificación pida un arreglo; en ese caso, el arreglo va con su propia prueba y su commit `fix(quotations): …`.

**Interfaces:**
- Consumes: todo lo anterior y `$env:TEMP\qep-numeracion-baseline-failed.txt` (Task 0).
- Produces: el handoff con la salida literal de cada comando.

- [ ] **Step 1: Barrido de formato fijo en código que haya quedado**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-ChildItem -Recurse -Filter *.cs -Path src | Select-String -Pattern "OrderNumberFormatter|QuotationNumberFormatter"
Get-ChildItem -Recurse -Filter *.cs -Path tests | Select-String -Pattern "OrderNumberFormatter|QuotationNumberFormatter"
Get-ChildItem -Recurse -Filter *.cs -Path src | Select-String -Pattern "'PED-'|'QUO-'|\`"PED-\`"|\`"QUO-\`""
```

Esperado: las dos primeras sin salida. En la tercera, sólo `DocumentNumbering.cs` (el default) y `src/Bootstrapper/Seeding/ExportLoadSeeder.cs` (`:242`, `:351`), que el spec deja **fuera de alcance**: la carga sintética sigue numerando por año UTC en SQL. Cualquier otra coincidencia es un formato que quedó fijo: **para y pregunta**.

- [ ] **Step 2: Los comandos de README § Verificación**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore
```

Esperado: restore sin `NU1004` (ningún `packages.lock.json` cambió); `dotnet format` sin diagnósticos salvo `ENDOFLINE`/`CHARSET` en archivos que esta rama no tocó y en los `Designer.cs` generados por `dotnet ef`; build con `0 Advertencia(s)` y `0 Errores`.

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git status --short
git diff --stat (git merge-base feature/fechas-locales-del-tenant HEAD) HEAD -- "**/packages.lock.json" Directory.Packages.props
```

Esperado: `git status` vacío y el `diff --stat` sin archivos.

- [ ] **Step 3: La suite completa, comparada por nombre con el baseline**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
$final = Join-Path $env:TEMP "qep-numeracion-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $final
$failed = Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique
$baseline = Get-Content (Join-Path $env:TEMP "qep-numeracion-baseline-failed.txt") -ErrorAction SilentlyContinue
Compare-Object -ReferenceObject @($baseline) -DifferenceObject @($failed) | Where-Object { $_.SideIndicator -eq "=>" }
```

Esperado: `Compare-Object` sin salida: ninguna prueba falla ahora que no fallara en el baseline. Pega la lista de nombres fallidos (si hay) en el handoff.

- [ ] **Step 4: Arquitectura y la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --no-build
git diff (git merge-base feature/fechas-locales-del-tenant HEAD) HEAD --stat -- tests/ArchitectureTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build --filter "FullyQualifiedName~TheModelHasNoChangesPendingAMigration"
```

Esperado: `ArchitectureTests` en verde; el `diff` de `tests/ArchitectureTests` vacío (ninguna regla cambió); y `TheModelHasNoChangesPendingAMigration` correcta — el modelo y el snapshot describen la misma base.

- [ ] **Step 5: Historial**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\numeracion
git log --format="%h %s" (git merge-base feature/fechas-locales-del-tenant HEAD)..HEAD
git log --format=%B (git merge-base feature/fechas-locales-del-tenant HEAD)..HEAD | Select-String -SimpleMatch "Co-Authored-By"
git diff (git merge-base feature/fechas-locales-del-tenant HEAD) HEAD --stat
```

Esperado: los ocho commits de la tabla de Entrega, en orden; el `Select-String` sin salida; y el `diff --stat` sin un solo archivo de `qep-frontend`, de `k8s/` ni de `Directory.Packages.props`. La rama **no se publica ni se mergea** desde este plan: el handoff dice qué quedó y el owner decide.
