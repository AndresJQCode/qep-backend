# Shopify: parte de envío de pedidos de otros países — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Un implementador por **bloque**, no por tarea (`CLAUDE.md` del workspace).

**Goal:** un pedido de Shopify con dirección de envío de fuera de Colombia nace con su parte de envío (país, ciudad escrita, región, código postal), y las ediciones de Shopify sobre esa dirección se aplican como en Colombia.

**Architecture:** una regla única en `Modules.Shopify.Application` (`ShopifyForeignAddress`) convierte la dirección del snapshot en destino; la usan el importador y el planificador de ediciones. Geography entra por un método nuevo del puerto `IShopifyGeographyLookup`. Quotations no cambia: los adaptadores de Bootstrapper sólo pasan los campos que `ExternalShippingParty` y `QuotationPartyRequest` ya tienen.

**Tech Stack:** .NET 10, xUnit, Testcontainers (Postgres) sólo en integración.

**Spec:** [handoff](../specs/2026-11-24-shopify-parte-de-envio-otros-paises-backend-handoff.md) (lee su sección «Correcciones contra el código») y el diseño en `qep-frontend/docs/superpowers/specs/2026-11-24-shopify-parte-de-envio-otros-paises-design.md`.

## Global Constraints

- **Rama** `feature/shopify-foreign-shipping`, worktree `..\qep-backend-worktrees\shopify-foreign-shipping`, creada desde `develop` en `257e47663`. Sin push. Nunca sobre `main`.
- **Pregunta al owner antes de correr cualquier cosa** (build, pruebas, scripts). Cada «Run» de este plan requiere su permiso; puede darlo por bloque.
- Nunca dos corridas de integración a la vez en la máquina: mira `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` y espera, sin detener nada. Las pruebas van en primer plano.
- TDD: el RED por compilación se evidencia con la salida del build, sin contenedores.
- **Colombia no cambia.** Un `countryCode` vacío o ausente sigue el camino de hoy.
- Sin backfill (B-1). Sin alta completa del cliente extranjero (D-2). La corrección del panel sigue sólo Colombia. Sin cambios en Quotations ni en `ShopifyOrderPushProcessor`. Sin migraciones.
- Todo snapshot entra por `ShopifyOrderIngestion.ApplyAsync`. `Modules.Shopify.Application` no referencia Quotations ni Geography (`ShopifyLayerTests`).
- Aviso nuevo, literal: `shipping_region_unresolved`, con `detail: null`. Ciudad escrita: máximo 120.
- Comentarios y documentación en español, tuteando. Conventional commits que terminan con las líneas `Session: <nombre>` y `Co-Authored-By: Claude <modelo> <noreply@anthropic.com>` (quien lanza un subagente se las pasa).
- `docs/estado.md` no se toca en la rama.

## Review Focus

1. El asesor elige la región de un pedido importado sin ella y Shopify devuelve el eco: no es cambio ni conflicto. (Task 5)
2. `province_code` con espacios o en minúsculas (`" m "`): se traduce igual. (Task 1)
3. Ciudad extranjera de más de 120 caracteres: el pedido entra sin parte, no queda `order_rejected`. (Task 4)
4. Dirección extranjera sin correo del comprador: sin parte y con aviso, no `order_rejected`. (Task 4)
5. Edición que sólo cambia el `zip` de un pedido de España: se aplica y conserva la región. (Task 5)

## Estructura de archivos

| Archivo (bajo `src/`) | Cambio |
| --- | --- |
| `Modules/Shopify/Modules.Shopify.Application/SpainProvinceCodes.cs` | `IneFor` |
| `…/ShopifyShippingAddressRules.cs` | `HasRegionCatalog`, `RegionCodeFor` |
| `…/ShopifyPorts.cs` | `FindRegionAsync`; `ShopifyPartyDraft` crece |
| `…/ShopifyForeignAddress.cs` (nuevo) | la regla de destino extranjero |
| `…/ShopifyOrderImporter.cs` | rama extranjera y aviso |
| `…/ShopifyEditPlanner.cs` | rama extranjera |
| `…/ShopifyDtos.cs` | comentario de los avisos (el nuevo no lleva `detail`) |
| `Modules/Shopify/Modules.Shopify.Domain/ShopifyWarnings.cs` | constante nueva |
| `…Domain/ShopifyCityCorrection.cs` | `PartyCityNameMaxLength` |
| `Bootstrapper/ShopifyGeographyLookup.cs` | adaptador de `FindRegionAsync` |
| `Bootstrapper/ShopifyOrderWriter.cs`, `ShopifyOrderCorrectionWriter.cs`, `ShopifyCityCorrectionWriter.cs` | pasan los campos |

Consumidores revisados que **no** cambian: `ShopifyCityCorrections` y `ShopifyOrderCorrections` arman partes colombianas (los campos nuevos van `null`); `ShopifyCityCorrection.Party` es sólo de Colombia; `ShopifyOrderProjection` ya guarda y compara `Country`, `CityName`, `RegionId` y `PostalCode`; `ShopifyOrderPushProcessor`, `ShopifyPushAddress` y `ExternalOrderDeliveryCheck` ya son multipaís; `ShopifyOrderChanges.CatchUpAsync` no recalcula avisos de envío; `ShopifyUnresolvedCityBackfill` es sólo de Colombia; `ShopifyOrderEditDecisions` replanea con el planificador.

---

## Task 0: verificar la base (antes de escribir código)

La rama de perfiles (`d65658756`) se mergeó sin ejecutar sus pruebas unitarias ni `test-feature.ps1`.

- [ ] **Step 1: preguntar al owner** cuál de estas dos corridas autoriza, y esperar:
  - Amplia (la pendiente en `docs/estado.md`): `.\scripts\test-feature.ps1 -Base 96463c08e`. Incluye además todo lo mergeado después (Siigo, roles por tenant, teléfono). Primero con `-DryRun` para mostrarle el plan.
  - Acotada: `dotnet test` de `tests/Modules/Shopify/Modules.Shopify.UnitTests`, `tests/Bootstrapper/Bootstrapper.UnitTests` y `tests/ArchitectureTests/ArchitectureTests`, uno a la vez (Bootstrapper necesita Docker).
- [ ] **Step 2: correr lo autorizado** desde el worktree y guardar la salida literal.
- [ ] **Step 3: informar.** Cada falla se reporta con su nombre y mensaje. Si algo está rojo, **no empieces el Bloque 1** sin que el owner decida. `docs/estado.md` menciona 3 fallas sin confirmar en Bootstrapper y Architecture (rama de código postal).
- [ ] **Step 4: commitear los documentos**

```powershell
git add docs/superpowers
git commit -m "docs(shopify): plan foreign shipping party for orders outside Colombia"
```

---

# Bloque 1 — reglas, puerto y adaptadores (Tasks 1–3)

## Task 1: traducción ISO → INE

**Files:** `SpainProvinceCodes.cs`, `ShopifyShippingAddressRules.cs`; pruebas `SpainProvinceCodesTests.cs`, `ShopifyShippingAddressRulesTests.cs`.

**Produces:**
- `public static string? SpainProvinceCodes.IneFor(string? isoCode)` — recorta, sin distinguir mayúsculas; `null` si no es una de las 52.
- `public static bool ShopifyShippingAddressRules.HasRegionCatalog(string country)` — sobre el `CountriesWithCatalog` que ya existe.
- `public static string? ShopifyShippingAddressRules.RegionCodeFor(string country, string? provinceCode)` — el inverso de `ProvinceCodeFor`; sólo `ES` tiene tabla.

- [ ] **Step 1: pruebas que fallan**

```csharp
// SpainProvinceCodesTests
[Theory]
[InlineData("M", "28")]
[InlineData(" m ", "28")]
[InlineData("B", "08")]
[InlineData("CO", "14")]
[InlineData("XX", null)]
[InlineData("", null)]
[InlineData(null, null)]
public void IneForTranslatesTheIsoCodeBack(string? iso, string? ine) => Assert.Equal(ine, SpainProvinceCodes.IneFor(iso));

[Fact]
public void IneForInvertsEveryEntry() =>
    Assert.All(SpainProvinceCodes.All, entry => Assert.Equal(entry.Key, SpainProvinceCodes.IneFor(entry.Value)));

// ShopifyShippingAddressRulesTests
[Theory]
[InlineData("ES", true)]
[InlineData("es", true)]
[InlineData("FR", false)]
[InlineData("CO", false)]
public void OnlySpainHasARegionCatalog(string country, bool expected) =>
    Assert.Equal(expected, ShopifyShippingAddressRules.HasRegionCatalog(country));

[Theory]
[InlineData("ES", "M", "28")]
[InlineData("ES", null, null)]
[InlineData("FR", "M", null)]
public void TheRegionCodeComesFromTheProvinceCode(string country, string? province, string? expected) =>
    Assert.Equal(expected, ShopifyShippingAddressRules.RegionCodeFor(country, province));
```

La paridad contra la semilla ya la cubren `SpainProvinceCodesGeographyTests` (claves = regiones sembradas) más `IneForInvertsEveryEntry`.

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Shopify/Modules.Shopify.UnitTests`; falla porque no existen los tres métodos.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~SpainProvinceCodesTests|FullyQualifiedName~ShopifyShippingAddressRulesTests"`.
- [ ] **Step 5: commit** `feat(shopify): translate Spanish province codes back to INE`

## Task 2: buscar la región en Geography

**Files:** `ShopifyPorts.cs` (`IShopifyGeographyLookup`), `Bootstrapper/ShopifyGeographyLookup.cs`; pruebas `ShopifyAdaptersTests.cs`, `GeographyTestDoubles.cs`, `ShopifyImporterTestDoubles.cs`.

**Produces:** `Task<Guid?> FindRegionAsync(string countryCode, string regionCode, CancellationToken cancellationToken);` — id de la región con ese código oficial en ese país (`countryCode` en mayúscula), o `null`.

- [ ] **Step 1: prueba que falla** (Bootstrapper.UnitTests). `CountingRegionRepository.ListByCountryAsync` hoy lanza `NotSupportedException`: haz que filtre por `CountryCode`.

```csharp
[Fact]
public async Task GeographyLookupFindsARegionByCountryAndCode()
{
    var madrid = Region.Create(RegionId.New(), "ES", "28", "Madrid");
    var lookup = new ShopifyGeographyLookup(new NamedDepartments(), new NamedCities(), new CountingRegionRepository(madrid));

    Assert.Equal(madrid.Id.Value, await lookup.FindRegionAsync("ES", "28", Ct));
    Assert.Null(await lookup.FindRegionAsync("ES", "99", Ct));
    Assert.Null(await lookup.FindRegionAsync("FR", "28", Ct));
}
```

- [ ] **Step 2: RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`.
- [ ] **Step 3: implementar** con `IRegionRepository.ListByCountryAsync` y comparación ordinal de `Code` (sin método nuevo en el repositorio). En `FakeGeographyLookup` añade `List<(string Country, string Code)> RegionRequests` y resuelve contra su diccionario `Regions`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~GeographyLookup"`.
- [ ] **Step 5: commit** `feat(shopify): look up a region by country and official code`

## Task 3: el borrador lleva el destino extranjero

**Files:** `ShopifyPorts.cs`, `ShopifyCityCorrection.cs`, los tres adaptadores; pruebas `ShopifyAdaptersTests.cs`, `ShopifyCityCorrectionParityTests.cs`.

**Produces:**
- `ShopifyPartyDraft(string? Name, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? CityId, string? PostalCode = null, string? Country = null, string? CityName = null, Guid? RegionId = null)` — mismo orden que `ShopifyShippingFingerprint`.
- `ShopifyCityCorrection.PartyCityNameMaxLength = 120`.
- `internal static ExternalShippingParty ShopifyOrderWriter.ToExternalParty(ShopifyPartyDraft party)` — única conversión para `ApplyEditAsync`, `ShopifyOrderCorrectionWriter` y `ShopifyCityCorrectionWriter.AttachShippingAsync`.

- [ ] **Step 1: pruebas que fallan**

```csharp
// ShopifyAdaptersTests
private static ShopifyPartyDraft SpanishDraftParty(Guid regionId) => new(
    "Lucía Fernández", "+34600111222", "entregas@madrid.es", "Calle Mayor 1", null, null, "28013", "ES", "Madrid", regionId);

[Fact]
public async Task OrderWriterCreateKeepsAForeignParty()
{
    var writer = new FakeExternalOrderWriter();
    var regionId = Guid.CreateVersion7();

    await OrderWriter(writer).CreateAsync(Draft() with { ShippingParty = SpanishDraftParty(regionId) }, Ct);

    Assert.Equal(
        new QuotationPartyRequest(
            "Lucía Fernández", null, "+34600111222", "entregas@madrid.es", "Calle Mayor 1", null, null,
            Country: "ES", CityName: "Madrid", RegionId: regionId, PostalCode: "28013"),
        Assert.Single(writer.Created).Parties!.Shipping);
}

[Fact]
public async Task AShopifyAddressEditKeepsAForeignParty()
{
    var (orderId, regionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var orders = new FakeExternalOrderWriter { OnApplyEdit = _ => EditState(orderId) };

    await OrderWriter(orders).ApplyEditAsync(
        new ShopifyOrderEditDraft(TenantId, orderId, 3, null, null, SpanishDraftParty(regionId), false, null, Now), Ct);

    Assert.Equal(
        new ExternalShippingParty(
            "Lucía Fernández", "+34600111222", "entregas@madrid.es", "Calle Mayor 1", null, null, "28013", "ES", "Madrid",
            regionId),
        Assert.Single(orders.Edits).ShippingParty);
}

// ShopifyCityCorrectionParityTests
[Fact]
public void TheCityNameWidthMatchesQuotations() =>
    Assert.Equal(QuotationPartyDetails.CityNameMaxLength, ShopifyCityCorrection.PartyCityNameMaxLength);
```

- [ ] **Step 2: RED** — `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`.
- [ ] **Step 3: implementar.** `ToExternal` pasa `Country`, `CityName`, `RegionId` y `PostalCode` por nombre. `ShopifyCityCorrectionWriter.CorrectShippingCityAsync` agrega `expected.Country`, `expected.CityName` y `expected.RegionId`. La corrección del asesor pasa a mandar `PostalCode`; Quotations lo sigue ignorando (`ExternalOrderService.CorrectByMemberAsync` impone el guardado), así que no cambia el comportamiento. Actualiza el comentario del record.
- [ ] **Step 4: GREEN y cierre del bloque** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyAdaptersTests|FullyQualifiedName~ShopifyCityCorrectionParityTests"` y después `dotnet build`.
- [ ] **Step 5: commit** `feat(shopify): carry country, city name and region in the party draft`

---

# Bloque 2 — importador y planificador (Tasks 4–5)

## Task 4: importador

**Files:** crear `ShopifyForeignAddress.cs`; modificar `ShopifyOrderImporter.cs`, `ShopifyWarnings.cs`, `ShopifyDtos.cs`; pruebas `ShopifyImportResultTests.cs`, `ShopifyImporterTestDoubles.cs`.

**Consumes:** Tasks 1–3.

**Produces:**

```csharp
internal static class ShopifyForeignAddress
{
    // País presente y distinto de CO (ShopifyPushAddress.IsColombia: vacío cuenta como Colombia).
    public static bool IsForeign(ShopifyAddress? address);

    // null sin ciudad, o con la ciudad recortada de más de ShopifyCityCorrection.PartyCityNameMaxLength.
    // Country en mayúscula; sólo consulta Geography si el país tiene catálogo y la provincia se traduce.
    public static Task<ShopifyForeignDestination?> ResolveAsync(
        IShopifyGeographyLookup geography, ShopifyAddress address, CancellationToken cancellationToken);
}

internal sealed record ShopifyForeignDestination(string Country, string CityName, Guid? RegionId, bool RegionUnresolved);
```

- `ShopifyWarnings.ShippingRegionUnresolved = "shipping_region_unresolved"`, en `All` después de `ShippingAddressUnresolved`.
- `ShopifyOrderImporter` recibe `IShopifyGeographyLookup geography` en su constructor (`ImporterWorld` le pasa su `Geography`).
- `WarningsJson` suma al final `bool shippingRegionUnresolved = false`; el aviso sale justo después de `shipping_address_unresolved`.

- [ ] **Step 1: pruebas que fallan** (`ShopifyImportResultTests`)

```csharp
private static readonly Guid MadridRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");

private static readonly ShopifyAddress Madrid = new(
    "Lucía Fernández", "Calle Mayor 1", null, "Madrid", "Madrid", "M", "ES", "+34600111222", null, " 28013 ");

private const string RegionWarning = """{"code":"shipping_region_unresolved","detail":null}""";

private static ImporterWorld WorldWithMadrid()
{
    var world = new ImporterWorld();
    world.Geography.Regions[MadridRegionId] = ("ES", "28");
    return world;
}

private static async Task<(ShopifyOrderDraft Draft, string Warnings)> ImportAddressAsync(
    ImporterWorld world, ShopifyAddress address)
{
    var snapshot = Order(address: address);
    var row = world.RowFor(snapshot);
    await world.ImportAsync(row, snapshot);
    return (Assert.Single(world.Writer.Creates).Draft, row.Warnings ?? string.Empty);
}

[Fact]
public async Task ASpanishAddressBecomesTheShippingParty()
{
    var (draft, warnings) = await ImportAddressAsync(WorldWithMadrid(), Madrid);

    Assert.Equal(
        new ShopifyPartyDraft(
            "Lucía Fernández", "+34600111222", "laura@verde.co", "Calle Mayor 1", null, null, "28013", "ES", "Madrid",
            MadridRegionId),
        draft.ShippingParty);
    Assert.Equal("Shopify #1001", draft.Notes);
    Assert.DoesNotContain("shipping_", warnings, StringComparison.Ordinal);
}

[Theory]
[InlineData(null)]
[InlineData(" ")]
[InlineData("XX")]
[InlineData("B")] // se traduce, pero Geography no la tiene en este mundo
public async Task ASpanishAddressWithoutRegionKeepsThePartyAndWarns(string? provinceCode)
{
    var (draft, warnings) = await ImportAddressAsync(WorldWithMadrid(), Madrid with { ProvinceCode = provinceCode });

    Assert.Equal(("ES", "Madrid", (Guid?)null), (draft.ShippingParty!.Country, draft.ShippingParty.CityName, draft.ShippingParty.RegionId));
    Assert.Contains(RegionWarning, warnings, StringComparison.Ordinal);
    Assert.Equal("Shopify #1001", draft.Notes);
}

[Fact]
public async Task ACountryWithoutCatalogKeepsThePartyWithoutWarning()
{
    var world = new ImporterWorld();

    var (draft, warnings) = await ImportAddressAsync(world, Madrid with { CountryCode = "fr", City = " Lyon " });

    Assert.Equal(("FR", "Lyon", (Guid?)null), (draft.ShippingParty!.Country, draft.ShippingParty.CityName, draft.ShippingParty.RegionId));
    Assert.DoesNotContain("shipping_", warnings, StringComparison.Ordinal);
    Assert.Empty(world.Geography.RegionRequests);
}

[Theory]
[InlineData(0)]
[InlineData(121)]
public async Task AForeignCityThatDoesNotFitLeavesNoParty(int cityLength)
{
    var (draft, warnings) = await ImportAddressAsync(WorldWithMadrid(), Madrid with { City = new string('a', cityLength) });

    Assert.Null(draft.ShippingParty);
    Assert.Contains("shipping_address_unresolved", warnings, StringComparison.Ordinal);
    Assert.DoesNotContain("shipping_region_unresolved", warnings, StringComparison.Ordinal);
    Assert.Contains(ShopifyOrderImporter.RawAddressLabel, draft.Notes, StringComparison.Ordinal);
}

[Fact]
public async Task AForeignAddressWithoutBuyerEmailLeavesNoParty()
{
    var world = WorldWithMadrid();
    var snapshot = Order(address: Madrid, buyer: new ShopifyBuyer("Lucía", "Fernández", null, "+34600111222"));
    var row = world.RowFor(snapshot);

    await world.ImportAsync(row, snapshot);

    Assert.Null(Assert.Single(world.Writer.Creates).Draft.ShippingParty);
    Assert.Equal(ShopifyImportState.Imported, row.ImportState);
    Assert.Contains("shipping_address_unresolved", row.Warnings ?? string.Empty, StringComparison.Ordinal);
}
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Shopify/Modules.Shopify.UnitTests`.
- [ ] **Step 3: implementar.** Sin recogida y con `IsForeign`, la parte sale de `ShippingPartyFields` (pide correo) más el destino, con `ShippingPostalCode`; Geography no se consulta si faltan campos. `RegionUnresolved` sólo cuenta si hay parte. El camino colombiano (`ShippingParty`) queda igual, y `RecordShippingCityAsync` también.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyImportResultTests"`; las colombianas siguen verdes sin editarlas.
- [ ] **Step 5: commit** `feat(shopify): import the shipping party of orders outside Colombia`

## Task 5: planificador de ediciones

**Files:** `ShopifyEditPlanner.cs`; pruebas `ShopifyEditPlannerTests.cs`.

**Consumes:** `ShopifyForeignAddress`.

**Comportamiento de `AddressAsync`, en este orden:** recogida (igual) → eco de parte extranjera (igual) → si `IsForeign(incoming.ShippingAddress)`, destino con `ResolveAsync`; `null` ⇒ conflicto `city_unresolved` **sin** reportar a la bandeja; si no, el camino colombiano de hoy → correo, `party_incomplete`, zip, eco y `qep_changed` comunes a los dos, con `Country`, `CityName` y `RegionId` en la huella candidata y en el borrador. Sin región se aplica sin región.

- [ ] **Step 1: pruebas que fallan**

```csharp
private static readonly Guid MadridRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");

private static ShopifyShippingFingerprint SpanishParty(Guid? regionId, string address = "Calle Mayor 1") =>
    new("Laura Gómez", "600111222", "laura@verde.co", address, null, null, "28013", "ES", "Madrid", regionId);

// imported: lo que QEP escribió desde Shopify (proyección). current: el pedido de ahora.
private static async Task<(ShopifyEditTestWorld World, ShopifyEditPlan Plan)> PlanSpanishAsync(
    ShopifyAddress incoming, ShopifyShippingFingerprint? imported, ShopifyShippingFingerprint? current,
    ShopifyAddress? applied = null)
{
    var world = new ShopifyEditTestWorld();
    world.World.Geography.Regions[MadridRegionId] = ("ES", "28");
    world.Applied = Snapshot(address: applied ?? Madrid);
    world.Order = world.Order with { ShippingParty = imported, ShippingCountry = imported?.Country };
    var projection = world.Projection;
    world.Order = world.Order with { ShippingParty = current, ShippingCountry = current?.Country };
    var plan = await world.Planner.PlanAsync(
        world.World.Store, world.ImportedRow(), world.Applied, projection, Snapshot(address: incoming), world.Order, null,
        TestContext.Current.CancellationToken);
    return (world, plan);
}

private static readonly ShopifyAddress Alcala = Madrid with { Address1 = "Calle de Alcalá 20" };

[Fact]
public async Task AForeignAddressEditAppliesWhenQepDidNotTouchIt()
{
    var (_, plan) = await PlanSpanishAsync(Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

    Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
    Assert.Equal(
        new ShopifyPartyDraft(
            "Laura Gómez", "+34 600 111 222", "laura@verde.co", "Calle de Alcalá 20", null, null, "28013", "ES", "Madrid",
            MadridRegionId),
        plan.Draft!.ShippingParty);
}

[Fact]
public async Task AForeignAddressEditOverAnAdvisorChangeIsAConflict()
{
    var (_, plan) = await PlanSpanishAsync(
        Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId, "Calle Nueva 2"));

    var group = Assert.Single(plan.Groups);
    Assert.Equal((ShopifyEditGroupOutcome.Conflict, "qep_changed"), (group.Outcome, group.Reason));
    Assert.Null(plan.Draft);
}

[Fact]
public async Task AForeignAddressQepAlreadyHasIsAnEcho()
{
    var (_, plan) = await PlanSpanishAsync(
        Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId, "Calle de Alcalá 20"));

    Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
    Assert.Empty(plan.Groups);
}

[Fact]
public async Task TheEchoOfARegionChosenByTheAdvisorIsNotAGroup()
{
    // Importado sin región; el asesor la eligió y Shopify devuelve lo que se le mandó.
    var (_, plan) = await PlanSpanishAsync(
        Madrid, SpanishParty(null), SpanishParty(MadridRegionId),
        applied: Madrid with { Province = null, ProvinceCode = null });

    Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
    Assert.Empty(plan.Groups);
}

[Fact]
public async Task AForeignAddressAppliesOverAnOrderWithoutParty()
{
    var (_, plan) = await PlanSpanishAsync(Alcala, imported: null, current: null);

    Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
    Assert.Equal("ES", plan.Draft!.ShippingParty!.Country);
}

[Fact]
public async Task AZipOnlyForeignEditKeepsTheRegion()
{
    var (_, plan) = await PlanSpanishAsync(
        Madrid with { Zip = "28001" }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

    Assert.Equal(("28001", (Guid?)MadridRegionId), (plan.Draft!.ShippingParty!.PostalCode, plan.Draft.ShippingParty.RegionId));
}

[Fact]
public async Task AForeignEditWithoutTranslatableProvinceAppliesWithoutRegion()
{
    var (_, plan) = await PlanSpanishAsync(
        Alcala with { ProvinceCode = "XX" }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

    Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
    Assert.Null(plan.Draft!.ShippingParty!.RegionId);
}

[Fact]
public async Task AForeignEditWithoutCityIsUnresolvedAndNeverReported()
{
    var (world, plan) = await PlanSpanishAsync(
        Alcala with { City = " " }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

    Assert.Equal("city_unresolved", Assert.Single(plan.Groups).Reason);
    Assert.Empty(world.World.UnresolvedCities.Reports);
}
```

En `AForeignAddressThatIsNotTheEchoStaysAConflict` (ya existe) la razón esperada pasa de `city_unresolved` a `qep_changed`: en ese mundo la parte del pedido difiere de la proyección.

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --filter "FullyQualifiedName~ShopifyEditPlannerTests"`; las que esperan aplicar o `qep_changed` fallan por aserción (`city_unresolved`). Las dos de eco ya pasan: fijan lo que no se puede romper.
- [ ] **Step 3: implementar.**
- [ ] **Step 4: GREEN y cierre del bloque** — `dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests` completo.
- [ ] **Step 5: commit** `feat(shopify): apply Shopify edits to shipping addresses outside Colombia`

---

# Bloque 3 — integración, documentos y cierre (Task 6)

## Task 6: de punta a punta

**Files:** crear `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyForeignShippingTests.cs`; modificar `README.md`, `.claude/rules/shopify.md`.

- [ ] **Step 1: escribir la prueba**

```csharp
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Quotations.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>Un pedido de Shopify de España nace con su parte de envío y toma las ediciones de esa dirección.</summary>
public sealed class ShopifyForeignShippingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Spanish(string address1, string updatedAt)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        order["updated_at"] = updatedAt;
        var address = order["shipping_address"]!;
        address["address1"] = address1;
        address["address2"] = null;
        address["city"] = "Madrid";
        address["province"] = "Madrid";
        address["province_code"] = "M";
        address["country"] = "Spain";
        address["country_code"] = "ES";
        address["zip"] = "28013";
        return order;
    }

    [Fact]
    public async Task ASpanishOrderIsBornWithItsShippingPartyAndTakesAddressEdits()
    {
        await using var flow = await Flow.StartAsync();
        await flow.SetEditSyncAsync("Automatic");

        await flow.DeliverAsync("orders/create", "wh-1", Spanish("Calle Mayor 1", "2026-10-01T10:00:00-05:00"));

        var regionId = await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28");
        using var client = flow.Client(OrdersPermissions.OrderRead);
        var route = $"/api/v1/tenants/{flow.World.TenantId}/orders/{await ShopifyOrderDelivery.OrderIdAsync(flow)}";
        var detail = (await client.GetFromJsonAsync<JsonObject>(route, Ct))!;
        var party = detail["order"]!["parties"]!.AsArray().Single(node => node!["role"]!.GetValue<string>() == "Shipping")!;
        Assert.Equal(
            ("ES", "Madrid", regionId, "28013"),
            (party["country"]!.GetValue<string>(), party["cityName"]!.GetValue<string>(),
                party["region"]!["id"]!.GetValue<Guid>(), party["postalCode"]!.GetValue<string>()));

        await flow.DeliverAsync("orders/updated", "wh-2", Spanish("Calle de Alcalá 20", "2026-10-01T10:30:00-05:00"));

        Assert.Equal("Applied", await flow.ScalarAsync(
            "SELECT coalesce(string_agg(status, ','), 'none') FROM shopify.order_edits"));
        Assert.StartsWith("Calle de Alcalá 20", await flow.ScalarAsync(
            "SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"), StringComparison.Ordinal);
    }
}
```

La fecha de `updated_at` del segundo webhook tiene que ser posterior a la del fixture; ajusta las dos si el fixture trae una mayor.

- [ ] **Step 2: GREEN** (el comportamiento ya está; si falla, depura antes de tocar la prueba) — `dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --filter "FullyQualifiedName~ShopifyForeignShippingTests"`.
- [ ] **Step 3: clases afectadas** — mismo proyecto con `--filter "FullyQualifiedName~ShopifyForeignShippingTests|FullyQualifiedName~ShopifyDeliveryEditPushTests|FullyQualifiedName~ShopifyEditSyncFlowTests|FullyQualifiedName~ShopifyImportFlowTests|FullyQualifiedName~SpainProvinceCodesGeographyTests"`. Si una prueba esperaba «sin parte» o `city_unresolved` para una dirección extranjera, muéstrasela al owner antes de cambiarla (es cambio de contrato; este spec sólo cubre lo descrito arriba).
- [ ] **Step 4: documentos.** En `README.md`: sumar `shipping_region_unresolved` a la lista de avisos; describir que fuera de Colombia el pedido nace con parte (país, ciudad escrita, región de España, código postal) y cuándo no; y reemplazar «es conflicto `city_unresolved`, porque las ciudades de afuera todavía no se importan» por: lo que no es eco se compara como en Colombia (aplicar o `qep_changed`), y `city_unresolved` queda para una dirección sin ciudad o de más de 120 caracteres. En `.claude/rules/shopify.md`, en la regla de los tres orígenes, agregar: «La dirección extranjera que **entra** la arma `ShopifyForeignAddress`, para el importador y el planificador».
- [ ] **Step 5: commit** `test(shopify): cover foreign shipping end to end` (con los documentos).
- [ ] **Step 6: cierre (pide permiso).** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos. Revisión final por un subagente de sólo lectura sin el contexto de quien implementó. Antes de mergear a `develop`, muestra cada aviso «Requiere autorización del owner». El merge y la fila de `docs/estado.md` van en `develop`, sólo si el owner lo pide. Sin push.
- [ ] **Step 7: prompt para `qep-frontend`.** El diseño pone el texto del aviso en su plan de perfiles (Task 16): `features/shopify/utils/describe-warning.ts` → «Elige la provincia de la dirección de envío en Editar pedido.», y el texto del panel «corrige esta en Shopify» apuntando a «Editar pedido». Entrega al owner el prompt con la rama, el commit y si ya está en `develop`; no toques ese repo.

---

## Decisiones pendientes del owner

El plan implementa el valor «por defecto»; cambiarlo es local a la tarea indicada.

| # | Decisión | Por defecto en este plan | Tarea |
| --- | --- | --- | --- |
| P-1 | Ciudad extranjera de más de 120 caracteres: ¿se recorta o queda sin parte? | Sin parte, `shipping_address_unresolved` (en edición, `city_unresolved`) | 4, 5 |
| P-2 | Una edición de Shopify que deja un pedido de España sin región: ¿agrega `shipping_region_unresolved` a la fila? | No (el spec sólo pide aplicar sin región) | 5 |
| P-3 | Con un envío `ShippingAddress` abierto (retenido), el asesor **acepta** un cambio que deja la parte de España sin región: el worker no la puede mandar y reintenta | Sin cambio (el spec dice «nada nuevo» en el worker) | — |
| P-4 | `ShopifyOrderDiff` no compara el país: un cambio de **sólo** país en Shopify no genera cambio. Agregar la fila `country` cambia el contrato de «Cambios por revisar» | Sin cambio | — |
