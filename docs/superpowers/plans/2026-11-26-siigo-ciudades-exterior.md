# Facturar en Siigo a clientes de otros países — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Un implementador por **bloque**, no por tarea, y una revisión por bloque (`CLAUDE.md` del workspace).

**Goal:** una parte facturada de otro país sale a Siigo con su `country_code`, `state_code` y `city_code` de la lista de Siigo, y lo que no coincide queda en una bandeja del operador que lo vincula una vez para todos los tenants.

**Architecture:** Geography gana el catálogo de Siigo (`geography.billing_cities`, sembrado desde un JSON embebido), los sinónimos y la bandeja del exterior, un resolvedor que sólo lee y un reportero que escribe; la consola del operador suma rutas bajo `/operator/cities/foreign`. Quotations entrega la ciudad escrita de una sola fuente. Bootstrapper cruza: `SiigoOrderSource` resuelve (lectura) y un puerto nuevo de Siigo, `ISiigoForeignCityInbox`, anota (escritura); sólo la vista previa y la emisión llaman al puerto, nunca la disponibilidad.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers (Postgres) sólo en integración, Python 3 (biblioteca estándar) para convertir la lista.

**Spec:** [`docs/superpowers/specs/2026-11-26-siigo-ciudades-exterior-design.md`](../specs/2026-11-26-siigo-ciudades-exterior-design.md). Léelo entero antes de empezar: es el contrato.

## Global Constraints

- **Worktree** `c:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\siigo-foreign-cities`, rama `feature/siigo-foreign-cities`. Todo comando corre con ese directorio como actual (nunca en el checkout principal). Antes de cada commit, `git branch --show-current` en ese momento.
- **Colombia no cambia:** DANE, `city_synonyms`, `unresolved_cities`, trabajos de corrección de Shopify y eventos `geography.city_synonym.*` quedan como están. Colombia no se importa al catálogo.
- **Los códigos se guardan y se envían tal cual vienen** (`Us`, `Co`, `AG`, `KY`); `country_code` del catálogo es el mismo en mayúscula. Los nombres se recortan, también de espacios de no separación. La lista **no se corrige** (Miami trae `city_code = "1"`).
- **Una factura nunca sale con una ciudad que nadie eligió:** sin coincidencia única ni sinónimo vivo, `customer_city_unlinked`.
- **Un fallo al anotar no tumba la vista previa ni la emisión:** warning con el tipo de la excepción (`[LoggerMessage]`, CA1873) y sigue.
- **Resolver es lectura, anotar es escritura**, en clases separadas. `SiigoInvoiceAvailabilityHandler` no recibe el puerto de anotar.
- Capas: ni Siigo ni Quotations referencian a Geography; el cruce va en `src/Bootstrapper`. Traducir errores de base va en Infrastructure, por **nombre de índice**.
- Migraciones sólo de expansión, en `geography`, con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/Geography/Modules.Geography.Infrastructure --context GeographyDbContext -o Persistence/Migrations`.
- Rutas nuevas sólo bajo `/api/v1/tenants/{tenantId}/operator/cities/foreign`, con `operator.cities.read` / `operator.cities.manage` y doble capa (`RequireAuthorization` + `OperatorAuthorization.EnsureAuthorized`). Ningún permiso nuevo.
- Códigos literales: faltante `customer_city_unlinked` (sin `reference` ni `label`); error `geography.billing_city.not_in_country` (422). Reusa `geography.synonym.not_found`, `.already_exists`, `.matches_city`, `.same_city`, `.deleted`, `geography.unresolved.not_found`, `.not_pending`, `.not_discarded`, `concurrency.conflict`, `validation.failed`.
- `id_type` de Siigo: Tax ID → `"50"`; NIF y NIE → `"42"`. El documento de NIF, NIE y Tax ID viaja como el pasaporte: sólo letras y dígitos ASCII.
- TDD, RED antes que GREEN con evidencia literal. Un RED que falla por compilación se evidencia con la salida de `dotnet build`, sin contenedores.
- Integración: mientras iteras, `--filter "FullyQualifiedName~<Clase>.<Prueba>"`; la clase completa una vez al final del bloque. Antes de toda corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una, espera (no la detengas: es de otra sesión). Nunca dos a la vez. Siempre en primer plano. En esta máquina, antes: `$env:Seed__Enabled = "false"`. Proyecto por proyecto, con `-- xUnit.MaxParallelThreads=4`.
- Nada de `dotnet format` sobre el repo (quita BOM y choca con CRLF). Nada de `Set-Content` para escribir archivos (rompe tildes): usa el editor. Antes de commitear, `git status` y revisa que ningún `.cs` quedó en 0 bytes.
- Texto: comentarios, documentación y mensajes al developer en español colombiano tuteando; identificadores, código y commits en inglés.
- **Commits:** conventional commits, uno por tarea, y el mensaje termina exactamente con estas dos líneas tras una línea en blanco (quien lanza al implementador le pasa el modelo):

  ```powershell
  git commit -m @'
  feat(geography): seed the Siigo billing city list

  Session: Envío de indicativo y país en Siigo
  Co-Authored-By: Claude <modelo que escribió el cambio> <noreply@anthropic.com>
  '@
  ```

  Ninguna otra línea de atribución. Los commits ya hechos no se reescriben.
- `docs/estado.md` **no se toca en la rama**: se actualiza en `develop` al mergear. Nada va a `main`.

## Review Focus

1. **Texto con otra forma que la lista** (`" miámi "`, `"MIAMI\u00A0"`, país `"us"` en minúscula): coincide con Miami y factura con `Us`/`09`/`1`. (Task 3, Task 8)
2. **Nombre repetido en un país** (`Boston` en `Us` con `02119` y `2101`; `Berlin` y `Berlín` en `De`): nunca coincide solo, va a la bandeja, y el operador **sí** puede vincularlo a cualquiera de los dos sin `matches_city`. (Task 3, Task 5)
3. **La misma orden vista muchas veces** (vista previa repetida, vista previa y luego tanda): una sola ocurrencia por pedido y la fila no se mueve. (Task 3, Task 9)
4. **La bandeja falla** (Geography lanza): la vista previa responde con sus faltantes y la tanda deja el pedido `Skipped` con `not_ready`, no `Failed` ni 500. (Task 8)
5. **Parte de facturación extranjera sin ciudad y ficha con ciudad**: `customer_city_missing`, sin mezclar el país de una con la ciudad de la otra. (Task 6)

---

## Estructura de archivos

| Archivo | Cambio |
| --- | --- |
| `scripts/geography/convert_siigo_cities.py` (nuevo) | Convierte el `.xlsx` de Siigo al JSON embebido |
| `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/Data/siigo-billing-cities.json` (nuevo) | El catálogo (lo embebe el glob `Seed\Data\*.json` del `.csproj`) |
| `…Geography.Domain/BillingCity.cs` (nuevo) | `BillingCityId`, `BillingCity` |
| `…Geography.Domain/ForeignCitySynonym.cs`, `UnresolvedForeignCity.cs`, `UnresolvedForeignCityOccurrence.cs` (nuevos) | Sinónimos y bandeja del exterior |
| `…Geography.Domain/CityText.cs`, `UnresolvedCity.cs`, `CitySuggestions.cs` | `CityText.Truncate`; sobrecarga `Rank` para el catálogo |
| `…Geography.Application/IBillingCityRepository.cs`, `IForeignCitySynonymRepository.cs`, `IUnresolvedForeignCityRepository.cs` (nuevos) | Puertos de persistencia |
| `…Geography.Application/ForeignBillingCityResolver.cs`, `ForeignCityReporter.cs` (nuevos) | Resolver (lee) y anotar (escribe) |
| `…Geography.Application/ForeignCityOperatorDtos.cs`, `ForeignCityOperatorQueries.cs`, `ForeignCityOperatorCommands.cs`, `ForeignCityViews.cs` (nuevos) | Consola del operador |
| `…Geography.Application/IGeographyUnitOfWork.cs`, `GeographyAuditActions.cs` | Candado del exterior; acciones de auditoría |
| `…Geography.Infrastructure/Persistence/GeographyDbContext.cs`, `GeographyUnitOfWork.cs` | Tablas, índices, candado, 409 por índice |
| `…Geography.Infrastructure/Persistence/BillingCityRepository.cs`, `ForeignCitySynonymRepository.cs`, `UnresolvedForeignCityRepository.cs` (nuevos) | Repositorios |
| `…Geography.Infrastructure/Seed/BillingCityDataParser.cs`, `BillingCitySeeder.cs` (nuevos) | Importador idempotente |
| `…Geography.Infrastructure/GeographyInfrastructureExtensions.cs`, `GeographyDatabaseInitializer.cs` | Registro y arranque |
| `…Geography.Infrastructure/Persistence/Migrations/*_AddBillingCities.cs`, `*_AddForeignCitySynonyms.cs` (nuevas) | Expansión |
| `…Geography.Api/ForeignCityOperatorEndpoints.cs` (nuevo); `src/Api/Program.cs:374` | Rutas |
| `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs`, `ExternalOrderInvoicingService.cs` | `CityName` y una sola fuente |
| `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs`, `SiigoIdentificationTypes.cs`, `SiigoCustomerMapper.cs`, `SiigoMissing.cs`, `SiigoInvoiceBuilder.cs` | País de Siigo, tipos de documento, faltante nuevo |
| `…Siigo.Application/SiigoPorts.cs`, `Invoicing/SiigoForeignCityAnnotation.cs` (nuevo), `PreviewSiigoInvoice.cs`, `IssueSiigoInvoice.cs` | Puerto de anotar y quién lo llama |
| `src/Bootstrapper/SiigoGeographyLookup.cs`, `SiigoOrderSource.cs`, `SiigoForeignCityInbox.cs` (nuevo), `QepServiceCollectionExtensions.cs` | Adaptadores y registro |
| `README.md`, `.claude/rules/geografia-ciudades.md`, `.claude/rules/siigo.md`, `docs/superpowers/specs/2026-11-18-integracion-siigo-design.md` | Documentación (Task 9) |

Revisados que **no** cambian: `SiigoInvoiceAvailabilityHandler` (no recibe el puerto); `CreateSiigoInvoiceBatchHandler` (no carga el pedido); `UnresolvedCityReporter`, `CityRepository.MatchByNameAsync`, `GeographyEventPublisher`, `CityCorrectionInsights` (Colombia); `CustomerDeletionReferences` (las ocurrencias del exterior no guardan cliente); `MultiReplicaInventory` (el seeder corre dentro de `InitializeGeographyDatabaseAsync`, que ya está bajo el candado de arranque; no hay hosted service nuevo).

---

# Bloque 1 — catálogo, bandeja y resolución en Geography (Tasks 1–3)

Un implementador. Al final del bloque, las clases de integración completas de este bloque (`BillingCitySeedTests`, `ForeignBillingCityResolverTests`, `ForeignCityReporterTests`) una vez, y una revisión del bloque.

- [ ] **Step 0: base.** En el worktree nuevo: `dotnet restore Backend.slnx`, luego `dotnet build Backend.slnx`. Guarda la salida: debe compilar sin errores antes de tocar nada. Si no compila, para y avisa.

## Task 1: catálogo de ciudades de Siigo

**Files:**
- Create: `scripts/geography/convert_siigo_cities.py`, `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/Data/siigo-billing-cities.json`, `src/Modules/Geography/Modules.Geography.Domain/BillingCity.cs`, `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/BillingCityDataParser.cs`, `src/Modules/Geography/Modules.Geography.Infrastructure/Seed/BillingCitySeeder.cs`, migración `AddBillingCities`
- Modify: `GeographyDbContext.cs`, `GeographyInfrastructureExtensions.cs`, `GeographyDatabaseInitializer.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/BillingCityTests.cs`, `BillingCityDataParserTests.cs`, `BillingCitySeedDataTests.cs`; `tests/Modules/Geography/Modules.Geography.IntegrationTests/BillingCitySeedTests.cs`

**Interfaces — Produces:**
- `public readonly record struct BillingCityId(Guid Value)` con `New()` (`Guid.CreateVersion7()`) y `ToString()`, como `CitySynonymId`.
- `public sealed class BillingCity` con `Id`, `CountryCode` (mayúscula), `SiigoCountryCode` (tal cual), `CountryName`, `StateCode`, `StateName`, `CityCode`, `CityName`, `NormalizedName`; `public static BillingCity Create(BillingCityId id, string siigoCountryCode, string countryName, string stateCode, string stateName, string cityCode, string cityName)`. Constantes de ancho: `CountryNameMaxLength = 80`, `CodeMaxLength = 10`, `NameMaxLength = 120`.
- `internal sealed record BillingCityRecord(string SiigoCountryCode, string CountryName, string StateCode, string StateName, string CityCode, string CityName)` y `internal static class BillingCityDataParser { public static IReadOnlyList<BillingCityRecord> Parse(Stream stream); }`.
- `internal sealed class BillingCitySeeder(GeographyDbContext dbContext, Func<Stream> openSource)` con `public const string ResourceSuffix = "Seed.Data.siigo-billing-cities.json"`, `public const string StateKey = "billing-cities:siigo"`, `internal const int SeederVersion = 1`, `internal static string ComputeHash(Stream source)` y `public Task SeedAsync(CancellationToken cancellationToken)`.
- `GeographyDbContext.BillingCities` (`DbSet<BillingCity>`) y las constantes de índice `BillingCityCodesIndex = "IX_billing_cities_country_state_city"`, `BillingCityNameIndex = "IX_billing_cities_country_name"`.

### Generar el JSON

El `.xlsx` es un dato descargado: va en una carpeta nueva y vacía, el script vive en el repo y Python corre aislado (`-I`), con el script y el archivo pasados por ruta. El script sólo usa la biblioteca estándar (`zipfile` + `xml.etree`; desde Python 3.7.1 `xml.etree` no resuelve entidades externas): no ejecuta nada del archivo.

```powershell
$dl = Join-Path $env:TEMP ("siigo-list-" + (Get-Date -Format yyyyMMddHHmmss))
New-Item -ItemType Directory $dl | Out-Null
curl.exe -sSL -o "$dl\cities.xlsx" "https://saprodcentralassets.blob.core.windows.net/siigoapi/documentation/Lista-de-ciudades.xlsx"
python -I scripts\geography\convert_siigo_cities.py "$dl\cities.xlsx" src\Modules\Geography\Modules.Geography.Infrastructure\Seed\Data\siigo-billing-cities.json
```

Salida esperada con la lista del 2026-11-26: `3482 cities, 219 countries -> …`. El archivo queda en UTF-8 con LF; Git avisa que lo pasará a CRLF en tu copia de trabajo (`core.autocrlf=true`): es lo mismo que pasa con `es-cities.json`, y la huella del seeder cambia por máquina sin consecuencia (resiembra una vez). El script es éste, literal (decide los valores: recorte, exclusión de Colombia, orden y forma):

```python
"""Convierte Lista-de-ciudades.xlsx de Siigo en el JSON que embebe Geography.

Uso (PowerShell, con el .xlsx en una carpeta propia y este script en el repo):
    python -I scripts\\geography\\convert_siigo_cities.py <ruta\\cities.xlsx> <ruta\\siigo-billing-cities.json>

Sólo biblioteca estándar (zipfile + xml.etree): no ejecuta nada del archivo descargado.
"""
import json
import sys
import zipfile
import xml.etree.ElementTree as ET

NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
REL = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"
HEADER = ["CityCode", "CityName", "StateCode", "StateName", "CountryCode", "CountryName"]
# Recorta espacios normales y de no separación (U+00A0), como pide el spec.
STRIP = " \t\r\n\u00a0\u2007\u202f\ufeff"


def clean(value):
    return (value or "").strip(STRIP)


def shared_strings(archive):
    if "xl/sharedStrings.xml" not in archive.namelist():
        return []
    root = ET.fromstring(archive.read("xl/sharedStrings.xml"))
    return ["".join(t.text or "" for t in si.iter("{%s}t" % NS["m"])) for si in root.findall("m:si", NS)]


def first_sheet_path(archive):
    workbook = ET.fromstring(archive.read("xl/workbook.xml"))
    first = workbook.find("m:sheets/m:sheet", NS)
    rel_id = first.get(REL)
    rels = ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))
    for rel in rels:
        if rel.get("Id") == rel_id:
            target = rel.get("Target").lstrip("/")
            return target if target.startswith("xl/") else "xl/" + target
    raise SystemExit("sheet1 not found")


def column_index(ref):
    letters = "".join(ch for ch in ref if ch.isalpha())
    index = 0
    for ch in letters:
        index = index * 26 + (ord(ch.upper()) - 64)
    return index - 1


def rows(archive):
    strings = shared_strings(archive)
    sheet = ET.fromstring(archive.read(first_sheet_path(archive)))
    for row in sheet.iter("{%s}row" % NS["m"]):
        values = [""] * len(HEADER)
        for cell in row.findall("m:c", NS):
            index = column_index(cell.get("r"))
            if index >= len(HEADER):
                continue
            kind = cell.get("t")
            if kind == "inlineStr":
                text = "".join(t.text or "" for t in cell.iter("{%s}t" % NS["m"]))
            else:
                node = cell.find("m:v", NS)
                text = "" if node is None else (node.text or "")
                if kind == "s" and text:
                    text = strings[int(text)]
            values[index] = text
        yield values


def main(source, target):
    with zipfile.ZipFile(source) as archive:
        all_rows = list(rows(archive))
    header = [clean(value) for value in all_rows[0]]
    if header != HEADER:
        raise SystemExit(f"Unexpected header: {header}")

    records, seen = [], set()
    for number, raw in enumerate(all_rows[1:], start=2):
        city_code, city_name, state_code, state_name, country_code, country_name = (clean(v) for v in raw)
        if not any((city_code, city_name, state_code, state_name, country_code, country_name)):
            continue
        if not all((city_code, city_name, state_code, state_name, country_code, country_name)):
            raise SystemExit(f"Row {number} has an empty column")
        # Colombia sigue por DANE (spec 2026-11-26): no se importa.
        if country_code.upper() == "CO":
            continue
        key = (country_code.upper(), state_code, city_code)
        if key in seen:
            raise SystemExit(f"Row {number} repeats {key}")
        seen.add(key)
        records.append({
            "countryCode": country_code,
            "countryName": country_name,
            "stateCode": state_code,
            "stateName": state_name,
            "cityCode": city_code,
            "cityName": city_name,
        })

    records.sort(key=lambda r: (r["countryCode"].upper(), r["stateCode"], r["cityCode"]))
    # Una ciudad por línea: el diff de una lista nueva de Siigo se lee fila por fila.
    lines = [json.dumps(record, ensure_ascii=False, separators=(",", ":")) for record in records]
    with open(target, "w", encoding="utf-8", newline="\n") as output:
        output.write("[\n" + ",\n".join(lines) + "\n]\n")
    countries = {r["countryCode"].upper() for r in records}
    print(f"{len(records)} cities, {len(countries)} countries -> {target}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
```

Hechos verificados sobre la lista del 2026-11-26 (las pruebas los usan): 3.482 filas fuera de Colombia, 219 países, todos de dos letras; `(país en mayúscula, state, city)` es único; nombres de hasta 38 caracteres, estados de hasta 50, países de hasta 43, `state_code` de hasta 2 y `city_code` de hasta 5; 35 nombres normalizados repetidos dentro de su país (77 filas). Filas que usan las pruebas: `Us/09 Florida/1 Miami`, `Us/09/32801 Orlando`, `Us/21 Massachusetts/02119 Boston` y `Us/21/2101 Boston`, `De/04/0401 Berlin` y `De/23/2301 Berlín`, `Ec/20 Pastaza/20075 Quito`, `Ve/10 Distrito Capital/1001 Caracas`. De los 41 países de QEP (`countries.json`), sólo `GD` (Granada) no está en la lista.

- [ ] **Step 1: pruebas unitarias que fallan**

```csharp
// BillingCityTests
[Fact]
public void TheCountryIsUppercasedAndTheSiigoCodeKeptAsItCame()
{
    var city = BillingCity.Create(BillingCityId.New(), "Us", " Estados Unidos ", "09", "Florida\u00A0", "1", "\u00A0Miami ");
    Assert.Equal(("US", "Us"), (city.CountryCode, city.SiigoCountryCode));
    Assert.Equal(("Estados Unidos", "Florida", "Miami", "MIAMI"), (city.CountryName, city.StateName, city.CityName, city.NormalizedName));
    Assert.Equal(("09", "1"), (city.StateCode, city.CityCode));
}

[Theory]
[InlineData("USA")]
[InlineData("U1")]
[InlineData("")]
public void ACountryThatIsNotTwoLettersIsRejected(string country) =>
    Assert.Equal("geography.billing_city.country_invalid",
        Assert.Throws<GeographyDomainException>(() => BillingCity.Create(BillingCityId.New(), country, "X", "1", "X", "1", "X")).Code);

// BillingCityDataParserTests (JSON en memoria, misma forma que el archivo)
[Fact] public void NamesAndCodesAreTrimmedIncludingNonBreakingSpaces() { /* "\u00A0Miami " → "Miami" */ }
[Fact] public void ColombiaInTheFileIsRejected() { /* countryCode "Co" → InvalidOperationException */ }
[Fact] public void ARepeatedCountryStateAndCityIsRejected() { /* "Us"/"09"/"1" y "US"/"09"/"1" → InvalidOperationException */ }
[Fact] public void AnEmptyFieldIsRejected() { /* cityName " " → InvalidOperationException */ }

// BillingCitySeedDataTests: el recurso embebido real
[Fact]
public void TheEmbeddedListHasTheSiigoCitiesOutsideColombia()
{
    using var stream = GeographySeeder.OpenResource(BillingCitySeeder.ResourceSuffix);
    var records = BillingCityDataParser.Parse(stream);
    Assert.Equal(3482, records.Count);
    Assert.Equal(219, records.Select(r => r.SiigoCountryCode.ToUpperInvariant()).Distinct().Count());
    Assert.DoesNotContain(records, r => r.SiigoCountryCode.Equals("CO", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(records, r => r is { SiigoCountryCode: "Us", StateCode: "09", CityCode: "1", CityName: "Miami", StateName: "Florida" });
}
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.UnitTests`; falla porque no existen `BillingCity`, `BillingCityDataParser` ni `BillingCitySeeder`. Guarda la salida.
- [ ] **Step 3: implementar el dominio y el parser.**
  - `BillingCity.Create` valida país de dos letras ASCII (`geography.billing_city.country_invalid`), códigos y nombres no vacíos y dentro de su ancho (`geography.billing_city.field_invalid`), recorta con `Trim()` (que incluye U+00A0) y calcula `NormalizedName = CityText.Normalize(cityName)`.
  - `BillingCityDataParser.Parse`: `JsonSerializer` sin distinguir mayúsculas sobre `{countryCode, countryName, stateCode, stateName, cityCode, cityName}`; lanza `InvalidOperationException` (como `RegionDataParser`) ante un campo vacío, un país `CO` sin importar mayúsculas, o una llave `(país en mayúscula, stateCode, cityCode)` repetida.
- [ ] **Step 4: genera el JSON** con el comando de arriba y confirma la salida `3482 cities, 219 countries`.
- [ ] **Step 5: GREEN unitario** — `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~BillingCity"`.
- [ ] **Step 6: prueba de integración que falla** (`BillingCitySeedTests`, cada prueba con su base: tocan la semilla, no pueden compartir host)

```csharp
[Fact]
public async Task TheSiigoListIsSeededOnStartupWithoutColombia()
{
    await using var database = await StartDatabaseAsync();
    using var factory = new QepApiFactory(database.GetConnectionString());
    using var warmUpClient = factory.CreateClient();
    await using var scope = factory.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();

    Assert.Equal(3482, await dbContext.BillingCities.CountAsync(Ct));
    Assert.False(await dbContext.BillingCities.AnyAsync(city => city.CountryCode == "CO", Ct));
    var miami = await dbContext.BillingCities.SingleAsync(city => city.CountryCode == "US" && city.CityCode == "1", Ct);
    Assert.Equal(("Us", "09", "Florida", "Miami", "MIAMI"),
        (miami.SiigoCountryCode, miami.StateCode, miami.StateName, miami.CityName, miami.NormalizedName));
}

[Fact] public async Task ReseedingWithTheSameFileDoesNotReadTheTable()
{ /* UPDATE geography.billing_cities SET city_name = 'Cambiada' WHERE country_code = 'US' AND city_code = '1'; InitializeGeographyDatabaseAsync; sigue 'Cambiada' */ }

[Fact] public async Task ADifferentStoredHashRenamesButNeverDeletes()
{ /* renombra Miami, INSERT una fila extra ('US','Us','Estados Unidos','99','X','9999','Ficticia','FICTICIA'),
     UPDATE geography.seed_state SET content_hash = 'viejo' WHERE key = 'billing-cities:siigo'; re-inicializa:
     Miami vuelve a 'Miami', la ficticia sigue y el total es 3483 */ }
```

- [ ] **Step 7: implementar persistencia y seeder.**
  - `GeographyDbContext.ConfigureBillingCity`: tabla `geography.billing_cities`, columnas `id`, `country_code` (2), `siigo_country_code` (2), `country_name` (80), `state_code` (10), `state_name` (120), `city_code` (10), `city_name` (120), `normalized_name` (`CityText.MaxLength`); único `BillingCityCodesIndex` sobre `(country_code, state_code, city_code)` y `BillingCityNameIndex` sobre `(country_code, normalized_name)`.
  - Migración `AddBillingCities` (sólo crea la tabla y sus índices).
  - `BillingCitySeeder.SeedAsync`, calcado de `RegionSeeder.SeedCountryAsync`: huella = SHA-256 de `"billing-cities-seed-v{SeederVersion}\n"` + bytes del recurso, en hex minúscula; igual a la de `geography.seed_state` (`StateKey`) → termina sin leer la tabla. Si no: parsea y valida antes de tocar la base, y en una transacción hace un solo `INSERT … SELECT * FROM unnest(…) ON CONFLICT (country_code, state_code, city_code) DO UPDATE SET siigo_country_code, country_name, state_name, city_name, normalized_name = EXCLUDED.… WHERE` alguno `IS DISTINCT FROM`, más el upsert de `seed_state`. **Nunca borra.** Los valores salen de `BillingCity.Create`.
  - Registro en `GeographyInfrastructureExtensions`: `services.AddScoped(provider => new BillingCitySeeder(provider.GetRequiredService<GeographyDbContext>(), () => GeographySeeder.OpenResource(BillingCitySeeder.ResourceSuffix)));`. En `GeographyDatabaseInitializer`, después de `RegionSeeder`.
- [ ] **Step 8: GREEN integración**, una prueba por vez mientras iteras:
  ```powershell
  Get-Process testhost, dotnet -ErrorAction SilentlyContinue
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Geography/Modules.Geography.IntegrationTests --filter "FullyQualifiedName~BillingCitySeedTests.TheSiigoListIsSeededOnStartupWithoutColombia" -- xUnit.MaxParallelThreads=4
  ```
  Luego las otras dos por nombre.
- [ ] **Step 9: commit** `feat(geography): seed the Siigo billing city list` (incluye el script, el JSON y la migración).

## Task 2: sinónimos y bandeja del exterior (dominio y persistencia)

**Files:**
- Create: `…Domain/ForeignCitySynonym.cs`, `…Domain/UnresolvedForeignCity.cs`, `…Domain/UnresolvedForeignCityOccurrence.cs`, `…Application/IBillingCityRepository.cs`, `…Application/IForeignCitySynonymRepository.cs`, `…Application/IUnresolvedForeignCityRepository.cs`, `…Infrastructure/Persistence/BillingCityRepository.cs`, `ForeignCitySynonymRepository.cs`, `UnresolvedForeignCityRepository.cs`, migración `AddForeignCitySynonyms`
- Modify: `…Domain/CityText.cs`, `…Domain/UnresolvedCity.cs`, `…Domain/CitySuggestions.cs`, `…Application/IGeographyUnitOfWork.cs`, `…Infrastructure/Persistence/GeographyDbContext.cs`, `GeographyUnitOfWork.cs`, `GeographyInfrastructureExtensions.cs`
- Test: `tests/Modules/Geography/Modules.Geography.UnitTests/CityTextTests.cs`, `CitySuggestionsTests.cs`, `UnresolvedForeignCityTests.cs` (nuevo), `ForeignCitySynonymTests.cs` (nuevo)

**Interfaces — Consumes:** `BillingCity`, `BillingCityId` (Task 1).

**Interfaces — Produces:**
- `public static string CityText.Truncate(string value, int max)` — la regla que hoy es privada en `UnresolvedCity` (no parte un par sustituto, `TrimEnd()` al cortar). `UnresolvedCity` pasa a llamarla; su comportamiento no cambia.
- `public readonly record struct UnresolvedForeignCityId(Guid Value)` y `ForeignCitySynonymId(Guid Value)`, con `New()` y `ToString()`.
- `public sealed class UnresolvedForeignCity`: `Id`, `CountryCode` (mayúscula), `NormalizedText`, `CityText`, `Status` (**reusa** `UnresolvedCityStatus`), `SynonymId` (`ForeignCitySynonymId?`), `FirstSeenAt`, `LastSeenAt`, `UpdatedAt`, `Version`; `public static string KeyFor(string cityText)` = `CityText.Truncate(CityText.Normalize(cityText), CityText.MaxLength)`; `public static UnresolvedForeignCity Create(UnresolvedForeignCityId id, string countryCode, string cityText, DateTimeOffset seenAt)` (recorta sin lanzar, como `UnresolvedCity.Create`); `Resolve(ForeignCitySynonymId, DateTimeOffset)`, `Discard(DateTimeOffset)`, `Restore(DateTimeOffset)` con los mismos códigos que `UnresolvedCity` (`geography.unresolved.not_pending`, `.not_discarded`).
- `public sealed class UnresolvedForeignCityOccurrence`: `Id` (Guid), `UnresolvedCityId`, `TenantId`, `OrderId`, `SeenAt`.
- `public sealed class ForeignCitySynonym`: `Id`, `CountryCode`, `NormalizedText`, `DisplayText`, `BillingCityId`, `CreatedBy`, `CreatedAt`, `UpdatedBy`, `UpdatedAt`, `DeletedAt`, `DeletedBy`, `Version`, `IsDeleted`; `public static ForeignCitySynonym Create(ForeignCitySynonymId id, UnresolvedForeignCity row, BillingCityId billingCityId, Guid createdBy, DateTimeOffset now)` (llave y texto de la fila, sin renormalizar; `geography.synonym.text_required` si la llave está vacía); `BillingCityId ChangeCity(BillingCityId, Guid by, DateTimeOffset now)` (`geography.synonym.same_city`, `.deleted`); `Delete(Guid by, DateTimeOffset now)` (`.deleted`). Mismo patrón que `CitySynonym`.
- `public sealed record BillingCitySuggestion(BillingCityId BillingCityId, int Similarity)` y `public static IReadOnlyList<BillingCitySuggestion> CitySuggestions.Rank(string normalizedText, IEnumerable<BillingCity> cities)`: `Score` contra `NormalizedName`, piso `MinimumSimilarity`, orden por similitud descendente, luego `CityName`, `StateName` y `CityCode` ordinales, y `MaxSuggestions`. La sobrecarga de Colombia no se toca.
- `public sealed record BillingCountry(string Code, string Name);`
- `public interface IBillingCityRepository` (sin seguimiento):
  - `Task<bool> CountryExistsAsync(string countryCode, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<BillingCity>> MatchByNameAsync(string countryCode, string normalizedName, CancellationToken cancellationToken)`
  - `Task<BillingCity?> FindAsync(BillingCityId id, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<BillingCity>> ListByIdsAsync(IReadOnlyCollection<BillingCityId> ids, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<BillingCity>> ListByCountriesAsync(IReadOnlyCollection<string> countryCodes, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<BillingCity>> SearchAsync(string countryCode, string? normalizedSearch, int limit, CancellationToken cancellationToken)` — orden `NormalizedName`, `StateName`, `CityCode`; `Contains` (se traduce a `strpos`: `%` y `_` son texto).
  - `Task<IReadOnlyList<BillingCountry>> ListCountriesAsync(CancellationToken cancellationToken)` — distintos `(country_code, country_name)`.
- `public interface IForeignCitySynonymRepository`: `FindAsync(ForeignCitySynonymId, ct)` (borrados incluidos), `FindActiveAsync(string countryCode, string normalizedText, ct)`, `void Add(ForeignCitySynonym)`, `RefreshAsync(ForeignCitySynonym, ct)`, `Task<(IReadOnlyList<ForeignCitySynonym> Items, int Total)> ListAsync(string? countryCode, string? normalizedSearch, int page, int pageSize, ct)` (vivos, `updated_at` desc y `id` desc; la búsqueda mira el texto normalizado del sinónimo o el `normalized_name` de su ciudad).
- `public sealed record UnresolvedForeignCityRow(UnresolvedForeignCity City, int OrderCount, int TenantCount);`
- `public interface IUnresolvedForeignCityRepository`:
  - `Task<(UnresolvedForeignCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(UnresolvedForeignCity candidate, Guid tenantId, Guid orderId, CancellationToken cancellationToken)` — dentro de la transacción y con el candado tomado.
  - `FindAsync(UnresolvedForeignCityId, ct)` (con seguimiento), `RefreshAsync(UnresolvedForeignCity, ct)`.
  - `Task<(IReadOnlyList<UnresolvedForeignCityRow> Rows, int Total)> ListAsync(UnresolvedCityStatus status, string? countryCode, string? normalizedSearch, int page, int pageSize, ct)` — `last_seen_at` desc, `id` desc, como `UnresolvedCityRepository.ListAsync`.
  - `Task<IReadOnlyList<UnresolvedForeignCityOccurrence>> ListOccurrencesAsync(UnresolvedForeignCityId id, ct)`.
  - `Task<IReadOnlyDictionary<string, int>> CountPendingByCountryAsync(ct)` — filas `Pending` por país.
- `IGeographyUnitOfWork.LockForeignCityTextAsync(string countryCode, string normalizedText, CancellationToken cancellationToken)` — `pg_advisory_xact_lock(hashtextextended('foreign:' || country || ':' || text, 0))`, exige transacción abierta como `LockCityTextAsync`. El prefijo `foreign:` evita chocar con la llave colombiana `{departmentId}:{texto}`.
- Constantes en `GeographyDbContext`: `ForeignSynonymTextActiveIndex = "IX_foreign_city_synonyms_country_text_active"`, `UnresolvedForeignTextOpenIndex = "IX_unresolved_foreign_cities_country_text_open"`, `ForeignOccurrenceOrderIndex = "IX_unresolved_foreign_city_occurrences_city_order"`.

- [ ] **Step 1: pruebas unitarias que fallan**

```csharp
// CityTextTests
[Theory]
[InlineData("ABC", 5, "ABC")]
[InlineData("ABCD EF", 5, "ABCD")]      // corta y no deja el espacio final
[InlineData("AB\uD83D\uDE00", 3, "AB")] // no parte el par sustituto
public void TruncateCutsWithoutAPartialPairOrATrailingSpace(string value, int max, string expected) =>
    Assert.Equal(expected, CityText.Truncate(value, max));

// UnresolvedForeignCityTests
[Fact]
public void CreateUppercasesTheCountryAndKeysTheText()
{
    var row = UnresolvedForeignCity.Create(UnresolvedForeignCityId.New(), " us ", "  Miámy ", Seen);
    Assert.Equal(("US", "MIAMY", "Miámy", UnresolvedCityStatus.Pending, 1L), (row.CountryCode, row.NormalizedText, row.CityText, row.Status, row.Version));
    Assert.Equal(row.NormalizedText, UnresolvedForeignCity.KeyFor("  Miámy "));
}
[Fact] public void ResolvingTwiceIsNotPending() { /* Resolve, Resolve → "geography.unresolved.not_pending" */ }
[Fact] public void RestoringAPendingRowIsNotDiscarded() { /* Restore → "geography.unresolved.not_discarded" */ }

// ForeignCitySynonymTests
[Fact] public void ASynonymTakesTheKeyAndTextOfItsRow() { /* NormalizedText == row.NormalizedText, DisplayText == row.CityText, Version 1 */ }
[Fact] public void ChangingToTheSameCityIsSameCity() { /* "geography.synonym.same_city" */ }
[Fact] public void DeletingTwiceIsDeleted() { /* "geography.synonym.deleted" */ }

// CitySuggestionsTests
[Fact]
public void TheCatalogRankingTiesByNameStateAndCode()
{
    var miami = BillingCity.Create(BillingCityId.New(), "Us", "Estados Unidos", "09", "Florida", "1", "Miami");
    var bostonA = BillingCity.Create(BillingCityId.New(), "Us", "Estados Unidos", "21", "Massachusetts", "2101", "Boston");
    var bostonB = BillingCity.Create(BillingCityId.New(), "Us", "Estados Unidos", "21", "Massachusetts", "02119", "Boston");

    Assert.Equal([new BillingCitySuggestion(miami.Id, 80)], CitySuggestions.Rank("MIAMY", [bostonA, miami, bostonB]));
    Assert.Equal([bostonB.Id, bostonA.Id], CitySuggestions.Rank("BOSTON", [bostonA, bostonB, miami]).Select(s => s.BillingCityId));
}
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.UnitTests`.
- [ ] **Step 3: implementar dominio, puertos, `DbContext`, repositorios y migración.**
  - Tablas (todas en `geography`): `foreign_city_synonyms` (`id`, `country_code` 2, `normalized_text`/`display_text` `CityText.MaxLength`, `billing_city_id` FK a `billing_cities` restrict, `created_by`, `created_at`, `updated_by`, `updated_at`, `deleted_at`, `deleted_by`, `version` token de concurrencia; único parcial `ForeignSynonymTextActiveIndex` sobre `(country_code, normalized_text)` `WHERE deleted_at IS NULL`; índice `IX_foreign_city_synonyms_billing_city`). `unresolved_foreign_cities` (`id`, `country_code`, `normalized_text`, `city_text`, `status` texto de 16 con `CK_unresolved_foreign_cities_status` = `GeographyDbContext.StatusCheck()`, `synonym_id` FK a `foreign_city_synonyms` restrict, `first_seen_at`, `last_seen_at`, `updated_at`, `version`; único parcial `UnresolvedForeignTextOpenIndex` sobre `(country_code, normalized_text)` `WHERE status <> 'Resolved'`; `IX_unresolved_foreign_cities_status_last_seen` como el colombiano). `unresolved_foreign_city_occurrences` (`id`, `unresolved_city_id` FK restrict, `tenant_id`, `order_id`, `seen_at`; único `ForeignOccurrenceOrderIndex` sobre `(unresolved_city_id, order_id)`).
  - Migración `AddForeignCitySynonyms` (sólo crea).
  - `GeographyUnitOfWork.SaveChangesAsync`: un `23505` con `ForeignSynonymTextActiveIndex` también es `ResourceConflictException("geography.synonym.already_exists", …)`.
  - `UnresolvedForeignCityRepository.UpsertWithOccurrenceAsync`, SQL crudo (EF no expresa el índice parcial; cada `INSERT … RETURNING` se lee entero, sin componer encima, y la hora va `ToUniversalTime()`):

    ```sql
    INSERT INTO geography.unresolved_foreign_cities AS u
        (id, country_code, normalized_text, city_text, status, first_seen_at, last_seen_at, updated_at, version)
    VALUES ({newId}, {country}, {key}, {text}, 'Pending', {seenAt}, {seenAt}, {seenAt}, 1)
    ON CONFLICT (country_code, normalized_text) WHERE status <> 'Resolved' DO UPDATE SET
        first_seen_at = LEAST(u.first_seen_at, EXCLUDED.first_seen_at),
        last_seen_at = GREATEST(u.last_seen_at, EXCLUDED.last_seen_at)
    RETURNING id AS "Value";

    INSERT INTO geography.unresolved_foreign_city_occurrences (id, unresolved_city_id, tenant_id, order_id, seen_at)
    VALUES ({newId}, {rowId}, {tenantId}, {orderId}, {seenAt})
    ON CONFLICT (unresolved_city_id, order_id) DO NOTHING
    RETURNING id AS "Value";
    ```

    Sin fila en el segundo `RETURNING` → `(rowId, false)`; quien llama no commitea y el rollback deshace el `last_seen_at` movido.
  - Registra los tres repositorios en `GeographyInfrastructureExtensions`.
- [ ] **Step 4: GREEN unitario** — `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests --filter "FullyQualifiedName~CityTextTests|FullyQualifiedName~CitySuggestionsTests|FullyQualifiedName~UnresolvedForeignCityTests|FullyQualifiedName~ForeignCitySynonymTests|FullyQualifiedName~UnresolvedCityTests"` (la última prueba que `UnresolvedCity` no cambió).
- [ ] **Step 5: commit** `feat(geography): add foreign city synonyms and inbox tables`.

## Task 3: resolver y anotar

**Files:**
- Create: `…Application/ForeignBillingCityResolver.cs`, `…Application/ForeignCityReporter.cs`
- Modify: `GeographyInfrastructureExtensions.cs`
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/ForeignBillingCityResolverTests.cs`, `ForeignCityReporterTests.cs` (nuevos)

**Interfaces — Consumes:** Task 1 y Task 2.

**Interfaces — Produces:**

```csharp
public enum ForeignCityResolutionKind { CountryNotInCatalog, Matched, Unresolved }
public sealed record BillingCityCodes(string SiigoCountryCode, string StateCode, string CityCode);
public sealed record ForeignCityResolution(ForeignCityResolutionKind Kind, BillingCityCodes? Codes);
/// <summary>Sólo lee. Lo llama Bootstrapper al armar el pedido para Siigo; global, sin IExecutionContext.</summary>
public interface IForeignBillingCityResolver
{
    Task<ForeignCityResolution> ResolveAsync(string countryCode, string cityText, CancellationToken cancellationToken);
}

/// <summary>Lleva la ciudad escrita: el ToString no la escribe.</summary>
public sealed record ForeignCityReport(Guid TenantId, Guid OrderId, string CountryCode, string CityText, DateTimeOffset SeenAt);
public enum ForeignCityReportOutcome { Recorded, AlreadyRecorded, SynonymExists, CountryNotInCatalog, Ignored }
/// <summary>Escribe y puede fallar. Global, sin IExecutionContext.</summary>
public interface IForeignCityReporter
{
    Task<ForeignCityReportOutcome> ReportAsync(ForeignCityReport report, CancellationToken cancellationToken);
}
```

Clases `ForeignBillingCityResolver(IBillingCityRepository cities, IForeignCitySynonymRepository synonyms)` y `ForeignCityReporter(IGeographyUnitOfWork unitOfWork, IBillingCityRepository cities, IForeignCitySynonymRepository synonyms, IUnresolvedForeignCityRepository unresolved)`, registradas como `AddScoped<IForeignBillingCityResolver, …>` y `AddScoped<IForeignCityReporter, …>`.

Algoritmo del resolvedor (spec «Cómo se resuelve una ciudad»; el país llega recortado y en mayúscula):
1. `!CountryExistsAsync(country)` → `CountryNotInCatalog`.
2. `key = UnresolvedForeignCity.KeyFor(cityText)`; vacía → `Unresolved`.
3. `MatchByNameAsync(country, key)` con **exactamente una** fila → `Matched` con sus códigos.
4. Sinónimo vivo de `(country, key)` → `Matched` con los códigos de su `BillingCityId`.
5. Si no, `Unresolved`.

Reportero: candidato con `UnresolvedForeignCity.Create` (pone llave y recortes); llave vacía → `Ignored`; país fuera del catálogo → `CountryNotInCatalog`; transacción; `LockForeignCityTextAsync(country, key)`; sinónimo vivo → `SynonymExists` sin escribir; `UpsertWithOccurrenceAsync`; ocurrencia ya existente → `AlreadyRecorded` sin commit; si no, commit → `Recorded`.

- [ ] **Step 1: pruebas de integración que fallan** (cada una con su base: escriben datos globales)

```csharp
// ForeignBillingCityResolverTests
[Theory]
[InlineData("US", " miámi\u00A0", "Us", "09", "1")]
[InlineData("us", "MIAMI", "Us", "09", "1")]
[InlineData("EC", "Quito", "Ec", "20", "20075")] // la lista no se corrige: Quito queda en Pastaza
public async Task AUniqueNameMatchesWhateverItsCaseAccentsOrSpaces(string country, string text, string siigoCountry, string state, string city)
{
    // ...arranque como RegionSeedTests...
    var result = await resolver.ResolveAsync(country.Trim().ToUpperInvariant(), text, Ct);
    Assert.Equal(new ForeignCityResolution(ForeignCityResolutionKind.Matched, new BillingCityCodes(siigoCountry, state, city)), result);
}

[Theory]
[InlineData("US", "Boston")]
[InlineData("DE", "Berlín")]
public async Task ARepeatedNameDoesNotMatchByItself(string country, string text) { /* Unresolved, Codes null */ }

[Fact] public async Task ALiveSynonymResolvesAndADeletedOneDoesNot() { /* INSERT synonym 'MIAMY' → Matched (Us,09,1); UPDATE deleted_at → Unresolved */ }

[Theory]
[InlineData("GD", "St. George's")]
[InlineData("CO", "Bogotá")]
public async Task ACountryOutsideTheCatalogIsCountryNotInCatalog(string country, string text) { /* Kind CountryNotInCatalog */ }

[Fact] public async Task ResolvingNeverWrites() { /* tras resolver "Miamy": count(*) de unresolved_foreign_cities y de occurrences = 0 */ }

// ForeignCityReporterTests
[Fact] public async Task AFirstReportCreatesAPendingRowWithItsOccurrence() { /* " Miamy " US → Recorded; fila (US, MIAMY, Miamy, Pending); ocurrencia (tenant, order, seen) */ }
[Fact] public async Task TheSameOrderReportedAgainIsAlreadyRecordedAndDoesNotMoveTheRow() { /* 2º reporte con seen +1h → AlreadyRecorded; last_seen_at sin cambio; 1 ocurrencia */ }
[Fact] public async Task TwoOrdersOfTheSameTextAreOneRowWithTwoOccurrences() { }
[Fact] public async Task ADiscardedTextAccumulatesAndStaysDiscarded() { /* UPDATE status='Discarded' → reporte de otra orden suma y sigue Discarded */ }
[Fact] public async Task AnActiveSynonymAnswersSynonymExistsWithoutWriting() { }
[Fact] public async Task ACountryOutsideTheCatalogWritesNothing() { /* "GD" → CountryNotInCatalog, 0 filas */ }
[Theory, InlineData(""), InlineData("\u0301")] public async Task ABlankTextIsIgnored(string text) { }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.IntegrationTests`; falla por los tipos que faltan.
- [ ] **Step 3: implementar** las dos clases y su registro.
- [ ] **Step 4: GREEN** — por nombre, una a una, p. ej. `--filter "FullyQualifiedName~ForeignBillingCityResolverTests.AUniqueNameMatchesWhateverItsCaseAccentsOrSpaces"`.
- [ ] **Step 5: commit** `feat(geography): resolve and report foreign billing cities`.

- [ ] **Cierre del bloque 1:** `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests` completo y, en fila, las tres clases de integración del bloque (`--filter "FullyQualifiedName~BillingCitySeedTests|FullyQualifiedName~ForeignBillingCityResolverTests|FullyQualifiedName~ForeignCityReporterTests"`). Evidencia literal del resumen. Revisión del bloque (un revisor, solo lectura).

---

# Bloque 2 — consola del operador (Tasks 4–5)

Depende del Bloque 1. Puede correr en paralelo con el Bloque 3 en otra sesión, pero las corridas de integración van en fila. Al final, `ForeignCityOperatorApiTests` completa una vez y una revisión del bloque.

## Task 4: lecturas de la consola

**Files:**
- Create: `…Application/ForeignCityOperatorDtos.cs`, `…Application/ForeignCityOperatorQueries.cs`, `…Application/ForeignCityViews.cs`, `src/Modules/Geography/Modules.Geography.Api/ForeignCityOperatorEndpoints.cs`
- Modify: `src/Api/Program.cs` (después de `app.MapCityOperatorEndpoints();`, línea 374), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a los handlers de la consola de ciudades, ~línea 1607)
- Test: `tests/Modules/Geography/Modules.Geography.IntegrationTests/ForeignCityOperatorApiTests.cs` (nuevo)

**Interfaces — Produces (DTO; JSON camelCase, estados con el nombre del enum):**

```csharp
public sealed record BillingCountryRefDto(string Code, string Name);
public sealed record BillingCountryDto(string Code, string Name, int PendingCount);
public sealed record BillingCityRefDto(Guid Id, string Name, string StateName, string CityCode);
public sealed record BillingCitySuggestionDto(Guid Id, string Name, string StateName, string CityCode, int Similarity);
public sealed record UnresolvedForeignCityListItemDto(
    Guid Id, string Text, string NormalizedText, BillingCountryRefDto Country, string Status, int OrderCount, int TenantCount,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, BillingCitySuggestionDto? BestSuggestion, long Version);
public sealed record UnresolvedForeignCityPageDto(IReadOnlyList<UnresolvedForeignCityListItemDto> Items, int Total, int Page, int PageSize);
public sealed record UnresolvedForeignCityDetailDto(
    Guid Id, string Text, string NormalizedText, BillingCountryRefDto Country, string Status, int OrderCount, int TenantCount,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, BillingCitySuggestionDto? BestSuggestion, long Version,
    IReadOnlyList<UnresolvedCityTenantDto> Tenants, IReadOnlyList<BillingCitySuggestionDto> Suggestions);
public sealed record ForeignCitySynonymDto(
    Guid Id, string Text, string NormalizedText, BillingCountryRefDto Country, BillingCityRefDto City,
    CitySynonymActorDto CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
public sealed record ForeignCitySynonymPageDto(IReadOnlyList<ForeignCitySynonymDto> Items, int Total, int Page, int PageSize);
```

En cada DTO, el porqué BFF en su `<summary>` (el código de la ciudad viaja porque la lista repite nombre y departamento: `Boston`, `Massachusetts`, `2101` y `02119`).

Consultas (handlers con `OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant)` **primero**, luego el validador):
- `ListBillingCountriesQuery(TenantId TenantId) : IQuery<IReadOnlyList<BillingCountryDto>>` — `ListCountriesAsync` + `CountPendingByCountryAsync`; orden: con pendientes primero, luego por `CityText.Normalize(Name)` ordinal (así «Emiratos Árabes Unidos» no cae al final por la tilde).
- `SearchBillingCitiesQuery(TenantId TenantId, string? Country, string? Search) : IQuery<IReadOnlyList<BillingCityRefDto>>` — hasta `ForeignCityOperatorInput.CatalogLimit = 50`.
- `ListUnresolvedForeignCitiesQuery(TenantId TenantId, string? Country, string? Status, string? Search, int Page, int PageSize) : IQuery<UnresolvedForeignCityPageDto>` — `bestSuggestion` desde `ListByCountriesAsync` de los países de la página (una consulta, no una por fila).
- `GetUnresolvedForeignCityQuery(TenantId TenantId, UnresolvedForeignCityId Id) : IQuery<UnresolvedForeignCityDetailDto>` — 404 `geography.unresolved.not_found` sólo si nunca existió; `Resolved` y `Discarded` responden con su estado; `tenants` por ocurrencias, nombres con `ICityCorrectionInsights.ReadTenantNamesAsync` y `UnresolvedCityViews.TenantName` como respaldo, orden por `orderCount` desc y nombre; `suggestions` con `CitySuggestions.Rank` del país.
- `ListForeignCitySynonymsQuery(TenantId TenantId, string? Country, string? Search, int Page, int PageSize) : IQuery<ForeignCitySynonymPageDto>` — `createdBy.email` con `ReadUserEmailsAsync`, una consulta por página.

Validadores (`422 validation.failed` con la clave del campo): `ForeignCityOperatorInput.IsCountryCode(string)` = dos letras ASCII. `SearchBillingCitiesValidator`: `Country` obligatorio y código válido, `Search` hasta `CityOperatorInput.MaxSearchLength`. `ListUnresolvedForeignCitiesValidator`: `Country` nulo o válido, `Status` con `CityOperatorInput.IsListStatus`, `Search`, `Page` (1..`CityOperatorInput.MaxPage`), `PageSize` (1..`CityOperatorInput.MaxPageSize`). `ListForeignCitySynonymsValidator`: igual sin `Status`. El país se compara en mayúscula.

Rutas en `ForeignCityOperatorEndpoints.MapForeignCityOperatorEndpoints()`, grupo `/api/v1/tenants/{tenantId:guid}/operator/cities/foreign`, tag `Operator`: `GET /countries`, `GET /catalog`, `GET /unresolved`, `GET /unresolved/{id:guid}`, `GET /synonyms`, todas con `RequireAuthorization(GeographyOperatorPermissions.CitiesRead)` y `pageSize = CityOperatorInput.DefaultPageSize` por defecto.

- [ ] **Step 1: pruebas que fallan** (`ForeignCityOperatorApiTests`, mismo arranque que `CityOperatorApiTests`: `QepApiFactory(connectionString, OperatorTenantId)`, `StubClient`, filas sembradas con el `IForeignCityReporter` real de un scope)

```csharp
[Fact] public async Task EveryForeignReadRouteAnswers403WithoutThePermission()
{ /* "tenancy.settings.read": /countries, /catalog?country=US, /unresolved, /unresolved/{nuevo}, /synonyms → 403 */ }

[Fact]
public async Task TheCountriesPutThoseWithPendingFirstAndThenByName()
{
    await ReportAsync(factory, Report("VE", "Caracaz"));
    await ReportAsync(factory, Report("US", "Miamy"));
    var countries = await client.GetFromJsonAsync<List<BillingCountryDto>>($"{Base}/countries", Ct);
    Assert.Equal(219, countries!.Count);
    Assert.Equal([("US", 1), ("VE", 1)], countries.Take(2).Select(c => (c.Code, c.PendingCount)));
    Assert.Equal("Estados Unidos", countries[0].Name);
    Assert.Equal(0, countries[2].PendingCount);
}

[Fact]
public async Task TheCatalogShowsTheCitiesOfOneCountryWithTheirCode()
{
    var bostons = await client.GetFromJsonAsync<List<BillingCityRefDto>>($"{Base}/catalog?country=us&search=bost", Ct);
    Assert.Equal([("Boston", "Massachusetts", "02119"), ("Boston", "Massachusetts", "2101")],
        bostons!.Select(c => (c.Name, c.StateName, c.CityCode)));
    Assert.Equal(50, (await client.GetFromJsonAsync<List<BillingCityRefDto>>($"{Base}/catalog?country=US", Ct))!.Count);
    await AssertProblemAsync(await client.GetAsync($"{Base}/catalog", Ct), HttpStatusCode.UnprocessableEntity, "validation.failed");
}

[Fact]
public async Task TheInboxFiltersByCountryAndCarriesTheBestSuggestion()
{ /* "Miamy" US y "Caracaz" VE; ?country=US → 1 ítem: text "Miamy", country {US, Estados Unidos}, Pending, orderCount 1,
     bestSuggestion (Miami, Florida, "1", 80); sin country → 2 ítems por last_seen desc */ }

[Fact] public async Task TheDetailListsTenantsAndUpToThreeSuggestions() { /* dos tenants: tenants por orderCount; suggestions ≤ 3, [0] Miami */ }

[Fact] public async Task AnUnknownFilterIs422() { /* ?status=Resolved, ?pageSize=0, ?country=USA → 422 validation.failed */ }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.IntegrationTests`.
- [ ] **Step 3: implementar** DTO, consultas, validadores, vistas (`ForeignCityViews`, `internal static`), rutas, registro de handlers en `QepServiceCollectionExtensions` (los validadores entran solos por `AddValidatorsFromAssemblyContaining<ListUnresolvedCitiesValidator>`) y `app.MapForeignCityOperatorEndpoints();` en `Program.cs`. Sin el registro de un handler, su ruta responde 500.
- [ ] **Step 4: GREEN** por nombre de prueba.
- [ ] **Step 5: commit** `feat(geography): read the foreign city inbox in the operator console`.

## Task 5: escrituras de la consola

**Files:**
- Create: `…Application/ForeignCityOperatorCommands.cs`
- Modify: `…Application/GeographyAuditActions.cs`, `ForeignCityOperatorEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `ForeignCityOperatorApiTests.cs`

**Interfaces — Consumes:** Task 4 (DTO, vistas, rutas).

**Interfaces — Produces:**
- Comandos: `ResolveUnresolvedForeignCityCommand(TenantId TenantId, UnresolvedForeignCityId Id, Guid BillingCityId) : ICommand<ForeignCitySynonymDto>`; `DiscardUnresolvedForeignCityCommand(TenantId, UnresolvedForeignCityId)` y `RestoreUnresolvedForeignCityCommand(…)` : `ICommand<UnresolvedForeignCityListItemDto>`; `CorrectForeignCitySynonymCommand(TenantId TenantId, ForeignCitySynonymId Id, Guid BillingCityId, long ExpectedVersion) : ICommand<ForeignCitySynonymDto>`; `DeleteForeignCitySynonymCommand(TenantId, ForeignCitySynonymId) : ICommand<bool>`.
- Validadores: `BillingCityId` no vacío; `ExpectedVersion >= 1`.
- Acciones de auditoría (en el tenant operador, actor `Human`): `ForeignSynonymCreated = "geography.foreign_city_synonym.created"`, `ForeignSynonymCityChanged = "geography.foreign_city_synonym.city_changed"`, `ForeignSynonymDeleted = "geography.foreign_city_synonym.deleted"`, `UnresolvedForeignCityDiscarded = "geography.unresolved_foreign_city.discarded"`, `UnresolvedForeignCityRestored = "geography.unresolved_foreign_city.restored"`; tipos `ForeignSynonymResourceType = "foreign_city_synonym"`, `UnresolvedForeignCityResourceType = "unresolved_foreign_city"`. El payload lleva país, texto normalizado e ids, nunca datos de clientes.
- Rutas (`RequireAuthorization(GeographyOperatorPermissions.CitiesManage)`): `POST /unresolved/{id:guid}/resolve` `{ billingCityId }`, `POST /unresolved/{id:guid}/discard`, `POST /unresolved/{id:guid}/restore`, `PUT /synonyms/{id:guid}` `{ billingCityId, expectedVersion }`, `DELETE /synonyms/{id:guid}` → 204. Cuerpos `BillingCityChoiceRequest(Guid? BillingCityId)` y `CorrectForeignCitySynonymRequest(Guid? BillingCityId, long? ExpectedVersion)`, nulables para que un campo ausente sea 422 y no 400.

Reglas, calcadas de `ResolveUnresolvedCityHandler`, `UnresolvedCityTriage`, `CorrectCitySynonymHandler` y `DeleteCitySynonymHandler` (autorización primero; transacción; primera lectura sólo para saber qué candado tomar; `LockForeignCityTextAsync`; `Refresh`; volver a verificar):
- **Confirmar:** fila inexistente 404 `geography.unresolved.not_found`; no `Pending` 422 `geography.unresolved.not_pending`; ciudad inexistente o de otro país 422 `geography.billing_city.not_in_country`; el texto coincide con **exactamente una** ciudad del país (`MatchByNameAsync(...).Count == 1`) 422 `geography.synonym.matches_city` — con dos o más **sí** se confirma, porque nunca coincidiría solo; sinónimo vivo 409 `geography.synonym.already_exists` (y el índice único da lo mismo en la carrera). `ForeignCitySynonym.Create`, `row.Resolve`, auditoría, `SaveChangesAsync`, commit.
- **Descartar / restaurar:** transición del dominio (422), auditoría, guardado con `Version` (412); devuelve la fila como la pinta la tabla.
- **Corregir:** inexistente o borrado 404 `geography.synonym.not_found`; `ExpectedVersion` distinta 412 `concurrency.conflict`; ciudad de otro país 422 `geography.billing_city.not_in_country`; misma ciudad 422 `geography.synonym.same_city`.
- **Borrar:** 404 si no existe o ya está borrado; borrado lógico; 204. No publica nada ni toca facturas.

- [ ] **Step 1: pruebas que fallan**

```csharp
[Fact] public async Task EveryForeignWriteRouteAnswers403WithOnlyRead() { /* CitiesRead: resolve, discard, restore, PUT, DELETE → 403 */ }

[Fact]
public async Task ResolvingCreatesASynonymThatTheNextResolutionUses()
{
    var rowId = await ReportAsync(factory, Report("US", "Miamy"));
    var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");

    var synonym = await (await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = miamiId }, Ct))
        .Content.ReadFromJsonAsync<ForeignCitySynonymDto>(Ct);

    Assert.Equal(("Miamy", "MIAMY", "US", "Miami", "Florida", "1", 1L),
        (synonym!.Text, synonym.NormalizedText, synonym.Country.Code, synonym.City.Name, synonym.City.StateName, synonym.City.CityCode, synonym.Version));
    Assert.Equal(new BillingCityCodes("Us", "09", "1"), (await ResolveAsync(factory, "US", "Miamy")).Codes);
    Assert.Equal("Resolved", (await DetailAsync(client, rowId)).Status);
    Assert.Equal(1, await AuditCountAsync(factory, "geography.foreign_city_synonym.created"));
}

[Fact] public async Task ACityOfAnotherCountryIs422NotInCountry() { /* Caracas (VE) para una fila US */ }
[Fact] public async Task ARepeatedNameCanBeResolvedButAUniqueOneIs422MatchesCity()
{ /* "Boston" (US) → resolve a 2101 → 200; "Miami" reportado directo → resolve → 422 geography.synonym.matches_city */ }
[Fact] public async Task ResolvingTwiceIs422NotPending() { }
[Fact] public async Task DiscardAndRestoreFollowTheColombianRules() { /* discard → Discarded; otra vez 422 not_pending; restore → Pending; otra vez 422 not_discarded */ }
[Fact] public async Task CorrectingNeedsTheCurrentVersionAndAnotherCityOfTheCountry()
{ /* PUT Orlando con version 1 → 200 version 2; otra vez con 1 → 412 concurrency.conflict; misma ciudad → 422 same_city; VE → 422 not_in_country */ }
[Fact] public async Task DeletingIs204ThenNotFoundAndTheTextOpensANewRow()
{ /* DELETE → 204; DELETE → 404 geography.synonym.not_found; nuevo reporte del texto con otra orden → fila Pending nueva */ }
[Fact] public async Task TheSynonymListFiltersByCountryAndSearch() { /* ?country=US&search=mia → el sinónimo; ?country=VE → vacío */ }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Geography/Modules.Geography.IntegrationTests`.
- [ ] **Step 3: implementar** comandos, auditoría, rutas y registro.
- [ ] **Step 4: GREEN** por nombre de prueba.
- [ ] **Step 5: commit** `feat(geography): resolve, triage and correct foreign city synonyms`.

- [ ] **Cierre del bloque 2:** `dotnet test tests/Modules/Geography/Modules.Geography.UnitTests` y la clase `ForeignCityOperatorApiTests` completa, en fila. Revisión del bloque.

---

# Bloque 3 — facturación (Tasks 6–9)

Depende del Bloque 1 (no del 2). Al final, las clases tocadas completas una vez y una revisión del bloque.

## Task 6: Quotations entrega la ciudad de una sola fuente

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs`, `ExternalOrderInvoicingService.cs` (`ResolvePartyAsync`, líneas 294-325)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderExternalInvoicingTests.cs`

**Interfaces — Produces:** `ExternalInvoicingParty(…, string? Phone, string? CityName = null)` — parámetro opcional al final (las construcciones posicionales de hoy compilan). Su `<summary>` documenta la regla.

Regla (spec «Facturación»): `CityId`, `DepartmentId`, `Country` y `CityName` salen juntos de la parte de facturación si ésta declara `CityId`, `CityName` o `Country` (no en blanco); si no, los cuatro de la ficha (`QuotationCustomerRef.CityId`, `.DepartmentId`, `.Country`, `.CityName`). Los textos se recortan y un blanco es `null`. Consumidor final: `CityName` nulo.

- [ ] **Step 1: pruebas que fallan** (junto a `GetForInvoicingTakesTheBilledCountryFromWhereTheCityComesFrom`, que debe seguir verde)

```csharp
[Fact]
public async Task AForeignBillingPartyBringsItsOwnCityName()
{ /* billing { Country = "US", CityName = " Miami " }, ficha CO con CityId → (null, null, "US", "Miami") en (CityId, DepartmentId, Country, CityName) */ }

[Fact]
public async Task AForeignBillingPartyWithoutCityDoesNotBorrowTheCustomerCity()
{ /* billing { Country = "VE" } sin ciudad, ficha CO con CityId → (null, null, "VE", null): el armador dirá customer_city_missing */ }

[Fact]
public async Task WithoutBillingCityTheFourComeFromTheCustomer()
{ /* billing sólo con nombre y documento; ficha US con CityName "Miami" → (null, null, "US", "Miami") */ }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` (no existe `CityName`).
- [ ] **Step 3: implementar** en `ResolvePartyAsync`; borra el comentario de `billingCityComplete` que ya no aplica.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderExternalInvoicingTests"`.
- [ ] **Step 5: commit** `fix(quotations): take the billed city from a single source`.

## Task 7: Siigo — país de Siigo, tipos de documento y faltante nuevo

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs`, `SiigoIdentificationTypes.cs`, `SiigoCustomerMapper.cs`, `SiigoMissing.cs`, `SiigoInvoiceBuilder.cs` (`CollectCustomerMissing`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoCustomerMapperTests.cs`, `SiigoInvoiceBuilderTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs` (`EveryQepIdentificationTypeEitherHasASiigoCodeOrIsKnownToBeUnsupported`)

**Interfaces — Produces:**
- `public sealed record SiigoUnlinkedCity(string CountryCode, string CityText, bool Reportable)` con `ToString() => "SiigoUnlinkedCity { }"`. `CountryCode` es el de QEP en mayúscula; `Reportable` es `false` cuando el país no está en el catálogo (no se anota).
- `SiigoBilledPartyInput(…, SiigoPhone? Phone, string? SiigoCountryCode = null, SiigoUnlinkedCity? UnlinkedCity = null)`.
- `SiigoMissing.CustomerCityUnlinked = "customer_city_unlinked"` y su línea en el `<summary>`.
- `SiigoIdentificationTypes.CodeFor`: `TaxId → "50"`, `Nif → "42"`, `Nie → "42"`; actualiza su `<summary>` (la tabla nueva y que falta que la confirme quien lleva la contabilidad antes de emitir a la DIAN).

- [ ] **Step 1: pruebas que fallan**

```csharp
// SiigoCustomerMapperTests
[Theory]
[InlineData(SiigoIdentificationKind.TaxId, "50")]
[InlineData(SiigoIdentificationKind.Nif, "42")]
[InlineData(SiigoIdentificationKind.Nie, "42")]
public void AForeignDocumentHasItsSiigoCode(SiigoIdentificationKind kind, string code) =>
    Assert.Equal(code, SiigoIdentificationTypes.CodeFor(kind));

[Theory]
[InlineData(SiigoIdentificationKind.Nif, " X-123.4 ", "X1234")]
[InlineData(SiigoIdentificationKind.Nie, "y 9876543-z", "y9876543z")]
[InlineData(SiigoIdentificationKind.TaxId, "12-3456789", "123456789")]
public void AForeignDocumentTravelsLikeThePassport(SiigoIdentificationKind kind, string raw, string expected) =>
    Assert.Equal(expected, SiigoCustomerMapper.Identification(Person() with { IdentificationType = kind, Identification = raw }));

[Fact]
public void AForeignPartySendsTheSiigoCountryAsItCame()
{
    var request = SiigoCustomerMapper.Map(Company() with { IdentificationType = SiigoIdentificationKind.TaxId, Identification = "123456789",
        SiigoCountryCode = "Us", StateCode = "09", CityCode = "1" })!;
    Assert.Equal(("50", "Us", "09", "1"), (request.IdTypeCode, request.CountryCode, request.StateCode, request.CityCode));
}
// TheAddressCarriesTheDaneCodes sigue esperando "Co" (sin SiigoCountryCode) y AFinalConsumerUsesTheDianGenericDocument también.

// SiigoInvoiceBuilderTests
[Fact]
public void AnUnlinkedForeignCityIsUnlinkedNotMissing()
{
    var input = Order() with { BilledParty = Company() with { CityCode = null, StateCode = null,
        UnlinkedCity = new SiigoUnlinkedCity("US", "Miamy", Reportable: true) } };
    var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing;
    var unlinked = Assert.Single(missing, value => value.Code.StartsWith("customer_city", StringComparison.Ordinal));
    Assert.Equal(new SiigoMissing("customer_city_unlinked", null, null), unlinked);
}

[Fact]
public void TheUnlinkedCityNeverPrints() =>
    Assert.DoesNotContain("Miamy", new SiigoUnlinkedCity("US", "Miamy", true).ToString(), StringComparison.Ordinal);
```

Reemplaza `ANifNieOrTaxIdIsUnsupported` (ya no lo son) por una prueba de que un documento **sin tipo** sigue en `customer_identification_type_unsupported` y `Map` devuelve `null`; cambia la fila `Nif, " X-123.4 ", "X-123.4"` de `TheDocumentIsSentAsSiigoExpectsIt` a `"X1234"`. En `SiigoOrderSourceTests.EveryQepIdentificationTypeEitherHasASiigoCodeOrIsKnownToBeUnsupported`, el esperado pasa a los siete nombres de `PartyIdentificationType` (renómbrala `EveryQepIdentificationTypeHasASiigoCode`).

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`.
- [ ] **Step 3: implementar.** `Identification`: `Ce`, `Pasaporte`, `Nif`, `Nie` y `TaxId` → sólo `char.IsAsciiLetterOrDigit`. `Map`: el país es `Clean(party.SiigoCountryCode) ?? "Co"` para la parte no consumidora final; el consumidor final sigue en `"Co"`. `CollectCustomerMissing`: con `UnlinkedCity` no nulo agrega `customer_city_unlinked` **en lugar de** `customer_city_missing`, en la misma posición (así `AllMissingAreReportedTogether` no cambia).
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoCustomerMapperTests|FullyQualifiedName~SiigoInvoiceBuilderTests"`.
- [ ] **Step 5: commit** `feat(siigo): send the Siigo country and foreign document types`.

## Task 8: resolver al armar y anotar sólo en vista previa y emisión

**Files:**
- Modify: `src/Bootstrapper/SiigoGeographyLookup.cs`, `src/Bootstrapper/SiigoOrderSource.cs`, `src/Bootstrapper/QepServiceCollectionExtensions.cs` (~línea 2313), `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs`, `Invoicing/PreviewSiigoInvoice.cs`, `Invoicing/IssueSiigoInvoice.cs`
- Create: `src/Bootstrapper/SiigoForeignCityInbox.cs`, `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoForeignCityAnnotation.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`, `SiigoForeignCityInboxTests.cs` (nuevo); `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoForeignCityAnnotationTests.cs` (nuevo), `IssueSiigoInvoiceHandlerTests.cs` (sólo el `Harness`)

**Interfaces — Consumes:** `IForeignBillingCityResolver`, `IForeignCityReporter`, `ForeignCityReport` (Task 3); `ExternalInvoicingParty.CityName` (Task 6); `SiigoBilledPartyInput.SiigoCountryCode`, `.UnlinkedCity`, `SiigoUnlinkedCity` (Task 7).

**Interfaces — Produces:**
- Bootstrapper: `internal sealed record SiigoForeignCityCodes(string CountryCode, string StateCode, string CityCode);` (`CountryCode` = el de Siigo), `internal sealed record SiigoForeignCityLookup(bool CountryInCatalog, SiigoForeignCityCodes? Codes);` y `ISiigoGeographyLookup.FindForeignCodesAsync(string countryCode, string cityText, CancellationToken cancellationToken) : Task<SiigoForeignCityLookup>`. `SiigoGeographyLookup` recibe además `IForeignBillingCityResolver` y traduce `CountryNotInCatalog → (false, null)`, `Unresolved → (true, null)`, `Matched → (true, códigos)`.
- Siigo, en `SiigoPorts.cs`:

  ```csharp
  /// <summary>Geography. Anota en la bandeja del operador una ciudad del exterior que no se pudo vincular (spec 2026-11-26). Escribe y
  /// puede fallar: quien llama no deja que eso tumbe la facturación. Lo llaman sólo la vista previa y la emisión.</summary>
  public interface ISiigoForeignCityInbox
  {
      Task ReportAsync(Guid tenantId, Guid orderId, Invoicing.SiigoUnlinkedCity city, CancellationToken cancellationToken);
  }
  ```

- `internal static partial class SiigoForeignCityAnnotation` con `public static Task AnnotateAsync(ISiigoForeignCityInbox inbox, ILogger logger, Guid tenantId, SiigoInvoiceInput input, CancellationToken cancellationToken)`: sólo con `input.BilledParty.UnlinkedCity is { Reportable: true }`; captura `Exception` `when (!cancellationToken.IsCancellationRequested)` y loguea con `[LoggerMessage(Level = LogLevel.Warning, Message = "Annotating the foreign city of the order in the operator inbox failed ({ExceptionType}); invoicing goes on.")]` el `exception.GetType().Name` (nunca el texto de la ciudad). Patrón de `SiigoSavedInvoice.IsQueuedAsync`.
- `internal sealed class SiigoForeignCityInbox(IForeignCityReporter reporter, IClock clock) : ISiigoForeignCityInbox` → `reporter.ReportAsync(new ForeignCityReport(tenantId, orderId, city.CountryCode, city.CityText, clock.UtcNow), …)`.

`SiigoOrderSource.GetAsync`, para una parte no consumidora final:
- País vacío o `CO`: DANE por `CityId` como hoy; `SiigoCountryCode` nulo.
- Otro país con `CityName` no en blanco: `country = party.Country.Trim().ToUpperInvariant()`; `FindForeignCodesAsync(country, party.CityName)`; con códigos → `CityCode`, `StateCode` y `SiigoCountryCode`; sin códigos → `UnlinkedCity = new SiigoUnlinkedCity(country, party.CityName.Trim(), Reportable: lookup.CountryInCatalog)`.
- Otro país sin `CityName`: nada (el armador dice `customer_city_missing`).

Dónde se anota (y dónde no):
- `PreviewSiigoInvoiceHandler` gana `ISiigoForeignCityInbox foreignCities` y `ILogger<PreviewSiigoInvoiceHandler> logger`; llama `AnnotateAsync` justo después de `SiigoInvoiceSupport.LoadAsync`. Su `<summary>` dice ahora que lo único que escribe es esa anotación, en Geography.
- `IssueSiigoInvoiceHandler` gana los mismos dos parámetros (al final del constructor); llama `AnnotateAsync` en `BuildNewAsync` justo después de `LoadAsync`, antes de armar. `ResumeAsync` no carga el pedido ni anota. La anotación usa el `DbContext` de Geography (otra conexión), así que no entra en la transacción de Siigo ni la tumba.
- `SiigoInvoiceAvailabilityHandler` **no cambia**: no recibe el puerto, así que no puede anotar.
- Si `LoadAsync` lanza (moneda, empresa, conexión), no se anota: el pedido todavía no se puede facturar.
- Registro: `services.AddScoped<ISiigoForeignCityInbox, SiigoForeignCityInbox>();` junto a `ISiigoOrderSource`.

- [ ] **Step 1: pruebas que fallan**

```csharp
// SiigoOrderSourceTests (FakeGeography gana un diccionario de respuestas y la lista de lo que se le pidió)
[Fact]
public async Task AForeignCityInTheListTravelsWithTheSiigoCodes()
{ /* Party US, CityName " Miami " → lookup("US","Miami") = (true, ("Us","09","1")) → (CityCode, StateCode, SiigoCountryCode) = ("1","09","Us"), UnlinkedCity null */ }

[Fact]
public async Task ALowercaseCountryIsAskedInUppercase() { /* Country " us " → la consulta fue ("US", "Miami") */ }

[Fact]
public async Task AForeignCityOutOfTheListIsUnlinkedAndReportable() { /* (true, null) → UnlinkedCity ("US","Miamy",true), códigos nulos */ }

[Fact]
public async Task ACountryOutOfTheCatalogIsUnlinkedButNotReportable() { /* GD → (false, null) → UnlinkedCity Reportable false */ }

[Fact]
public async Task AForeignPartyWithoutCityAsksNothing() { /* CityName null → sin consulta, UnlinkedCity null, códigos nulos */ }
// ACityOutsideColombiaGetsNoDaneCode: ajusta su parte a CityName null para que siga probando lo que dice.

// SiigoForeignCityInboxTests
[Fact] public async Task TheInboxReportsTheTenantOrderCountryAndTextAtTheClockTime() { }

// SiigoForeignCityAnnotationTests
[Fact] public async Task AReportableCityIsAnnotatedOnce() { }
[Fact] public async Task ANonReportableOrLinkedCityIsNotAnnotated() { }
[Fact]
public async Task AFailingInboxLogsAWarningWithTheExceptionTypeAndDoesNotThrow()
{ /* inbox lanza InvalidOperationException("Miamy") → no lanza; un warning; el mensaje contiene "InvalidOperationException" y no "Miamy" */ }
[Fact] public async Task ACancelledCallerStillSeesTheCancellation() { /* token cancelado + inbox que lanza OperationCanceledException → sube */ }
```

En `IssueSiigoInvoiceHandlerTests.Harness`, pasa un inbox que lanza si se le llama (la reanudación nunca anota) y `NullLogger<IssueSiigoInvoiceHandler>.Instance`.

- [ ] **Step 2: RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` y `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoForeignCityAnnotationTests|FullyQualifiedName~IssueSiigoInvoiceHandlerTests"`, y luego (Bootstrapper necesita Docker; mira antes que no haya otra corrida) `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SiigoOrderSourceTests|FullyQualifiedName~SiigoForeignCityInboxTests"`.
- [ ] **Step 5: commit** `feat(siigo): resolve foreign cities and annotate the operator inbox`.

## Task 9: de punta a punta y documentación

**Files:**
- Modify: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoicingHarness.cs` (`CreateCustomerAsync` y `ArrangeAsync` ganan `ForeignCustomer? foreignCustomer = null`, con `internal sealed record ForeignCustomer(string Country, string CityName)`: crea el cliente con `country`, `cityName`, sin `cityId`, `identificationType = "TAX_ID"`, `identificationNumber = "12-3456789"`, `personType = "Juridica"` y `phone = "+1 305 555 0100"`)
- Create: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoForeignCityInvoiceTests.cs`
- Docs: `README.md`, `.claude/rules/geografia-ciudades.md`, `.claude/rules/siigo.md`, `docs/superpowers/specs/2026-11-18-integracion-siigo-design.md`

- [ ] **Step 1: pruebas que fallan** (cada una con su base, como `SiigoInvoiceApiTests`)

```csharp
[Fact]
public async Task AForeignCustomerWhoseCityIsInTheListIsInvoicedWithTheSiigoCodes()
{
    await using var database = await StartDatabaseAsync();
    using var world = await ArrangeAsync(database, foreignCustomer: new ForeignCustomer("US", "Miami"));

    var rows = await IssueRowsThroughBatchAsync(world);

    Assert.Equal("Created", rows[0].GetProperty("status").GetString());
    using var customer = JsonDocument.Parse(world.Siigo.Calls.Single(call => call.Path == "/v1/customers" && call.Method == HttpMethod.Post).Body!);
    var city = customer.RootElement.GetProperty("address").GetProperty("city");
    Assert.Equal(("Us", "09", "1"), (city.GetProperty("country_code").GetString(), city.GetProperty("state_code").GetString(), city.GetProperty("city_code").GetString()));
    Assert.Equal(("50", "123456789"), (customer.RootElement.GetProperty("id_type").GetString(), customer.RootElement.GetProperty("identification").GetString()));
}

[Fact]
public async Task AnUnlinkedCityShowsInThePreviewAndIsAnnotatedOncePerOrder()
{ /* "Miamy": dos GET /preview → missing contiene "customer_city_unlinked" y no "customer_city_missing";
     1 fila en geography.unresolved_foreign_cities (US, MIAMY) y 1 ocurrencia con order_id = world.OrderId y tenant_id = world.TenantId */ }

[Fact]
public async Task TheAvailabilityCheckNeverAnnotates()
{ /* "Miamy": GET /availability → 200; count(*) de geography.unresolved_foreign_cities = 0 */ }

[Fact]
public async Task AnUnlinkedCityIsSkippedByTheBatchAndAnnotated()
{ /* IssueThroughBatchAsync → ("Skipped", "siigo.invoice.not_ready"), missing contiene "customer_city_unlinked";
     nada llegó a /v1/customers ni /v1/invoices; 1 ocurrencia del pedido */ }

[Fact]
public async Task OnceTheOperatorLinksTheTextTheNextIssueInvoicesIt()
{ /* "Miamy": INSERT en geography.foreign_city_synonyms (país 'US', 'MIAMY', 'Miamy', billing_city_id de US/09/1, version 1);
     la tanda crea la factura y el cliente sale con ("Us","09","1") */ }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` (no existe `ForeignCustomer`). Si al implementar la creación del cliente o del pedido extranjero falla por otra regla (teléfono, documento por país), corrige el dato de la prueba, no el producto, y anótalo en el informe del bloque.
- [ ] **Step 3: implementar** el cambio del harness.
- [ ] **Step 4: GREEN** — una por nombre, p. ej. `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoForeignCityInvoiceTests.TheAvailabilityCheckNeverAnnotates" -- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: commit** `test(siigo): invoice a foreign customer end to end`.
- [ ] **Step 6: documentación.**
  - `README.md`, después de «Sinónimos de ciudades»: sección «Ciudades del exterior para Siigo» con la tabla de rutas de `/operator/cities/foreign` (las diez, con permiso y qué hace), el orden de resolución (coincidencia única, sinónimo, bandeja), que la disponibilidad no anota, y cómo regenerar el JSON (el bloque de PowerShell de la Task 1).
  - `.claude/rules/geografia-ciudades.md`: (1) la lista de Siigo no se corrige y el seeder nunca borra (un sinónimo apunta ahí); (2) un nombre repetido en un país nunca coincide solo y confirmarlo **no** es `matches_city`; (3) el candado del exterior lleva el prefijo `foreign:` y no comparte llave con Colombia.
  - `.claude/rules/siigo.md`: resolver es lectura en `SiigoOrderSource`; anotar es `ISiigoForeignCityInbox`, sólo desde la vista previa y la emisión; la disponibilidad no lo recibe; un fallo deja warning con el tipo y sigue; `id_type` 50/42 sin confirmar por contabilidad; sin probar contra Siigo real (facturar con `send_to_dian` apagado y revisar país, departamento, ciudad y teléfono).
  - `docs/superpowers/specs/2026-11-18-integracion-siigo-design.md`, junto a la tabla de `id_type` (línea 323): una línea que diga que el spec 2026-11-26 agrega Tax ID `50` y NIF/NIE `42`.
- [ ] **Step 7: commit** `docs: document foreign billing cities for Siigo`.

- [ ] **Cierre del bloque 3:** en fila, `Modules.Siigo.UnitTests` y `Modules.Quotations.UnitTests` completos, `Bootstrapper.UnitTests` completo, y las clases `SiigoForeignCityInvoiceTests` y `SiigoInvoiceApiTests` completas (el harness cambió). Revisión del bloque.

---

## Cierre de la rama

- [ ] **Revisión fuerte de toda la rama**, una vez, por un subagente de solo lectura con el modelo más capaz y sin el contexto de quien implementó, contra el spec y este plan. Pídele mirar en especial el Review Focus y que la disponibilidad no tenga camino a `ISiigoForeignCityInbox`.
- [ ] **Puerta de merge:** `dotnet restore Backend.slnx` (worktree nuevo), luego `.\scripts\test-feature.ps1 -DryRun` para ver el plan y después `.\scripts\test-feature.ps1`, en verde y **leyendo los avisos**. Esperados: el transversal por `src/Bootstrapper` y `src/Api`; «Apoyo de pruebas cambiado» por `SiigoInvoicingHarness.cs`; «Quotations: cambió producción y no corre ninguna prueba de integración» (la cubre `SiigoForeignCityInvoiceTests`). Ninguno de «Requiere autorización del owner»: no se tocan `tests/ArchitectureTests/` ni referencias entre módulos; si aparece uno, para y muéstraselo al owner.
- [ ] **Contrato HTTP:** las rutas nuevas, `customer_city_unlinked` y `geography.billing_city.not_in_country` están aprobados por el spec. Cualquier otra forma que cambie se le muestra al owner antes del merge.
- [ ] **Merge a `develop`** (nunca a `main`); en `develop`, en el mismo merge o en un `docs:` inmediato, actualiza `docs/estado.md` (fila y Registro) con los commits.
- [ ] **Prompt para `qep-frontend`** al owner (este repo no toca el frontend): pantalla «Exterior» en `/operator/cities` con las diez rutas y sus DTO tal como quedaron en `ForeignCityOperatorDtos.cs`, los permisos, los códigos de error, el faltante `customer_city_unlinked` en la vista previa y la tanda, la rama y el commit del merge, y las decisiones pendientes de abajo.

## Decisiones que el plan toma y debe conocer el owner

- `SiigoBilledPartyInput` gana, además del código de país de Siigo que pide el spec, `UnlinkedCity`: el armador necesita distinguir «sin ciudad» de «ciudad escrita sin vincular», y la ciudad escrita no llegaba a Siigo. El texto nunca se imprime ni entra a `SiigoMissing`.
- Confirmar un texto es `matches_city` sólo si coincide con **una** ciudad del país: con dos o más (Boston) el spec manda a la bandeja, así que confirmarlo tiene que poderse.
- `GET /catalog` exige `country` (422 si falta). `GET /countries` ordena por nombre sin tildes.
- El detalle de la bandeja no lleva `synonymId` (el spec lista «la fila, tenants y suggestions»; el colombiano sí lo lleva).
- No se anota si la carga del pedido falla antes de armar (moneda, empresa o conexión).

---

## Self-review

- **Cobertura del spec:** catálogo y seeder (T1); sinónimos, bandeja y ocurrencias (T2); los cuatro pasos de resolución y el candado compartido (T3, T5); anotar sólo en vista previa y emisión, idempotente y sin tumbar (T3, T8, T9); `CityName` y una sola fuente (T6); país de Siigo, `customer_city_unlinked`, tipos 50/42 y documento como pasaporte (T7); las diez rutas, DTO, códigos, auditoría y sin vista de impacto ni eventos (T4, T5); nada de Colombia cambia (T2 Step 4 corre `UnresolvedCityTests`; sobrecarga `Rank` aparte; prefijo de candado). Sin reintento automático: nada lo agrega. El teléfono no cambia.
- **Tipos:** `BillingCityId`, `ForeignCitySynonymId`, `UnresolvedForeignCityId`, `BillingCityCodes`, `ForeignCityResolution`, `ForeignCityReport`, `SiigoUnlinkedCity`, `SiigoForeignCityLookup` y `ISiigoForeignCityInbox` se definen una vez y se usan con el mismo nombre en las tareas siguientes.
- **Review Focus:** cada línea tiene su prueba en la tarea que posee el código (T3/T8, T3/T5, T3/T9, T8, T6).
