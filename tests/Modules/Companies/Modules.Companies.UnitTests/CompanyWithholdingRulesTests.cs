using BuildingBlocks.Domain.Taxes;
using Modules.Companies.Application;
using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>
/// Spec ReteICA por empresa §7.3: la empresa sólo guarda tasas municipales activas de su país, una por municipio y tipo. Lo ya
/// guardado e inactivo se tolera (A16).
/// </summary>
public sealed class CompanyWithholdingRulesTests
{
    private static readonly Guid Bogota = Guid.CreateVersion7();
    private static readonly Guid Medellin = Guid.CreateVersion7();

    private static CompanyTaxRateRef Rate(
        string type = "ReteICA",
        string country = "CO",
        bool isActive = true,
        bool withholds = true,
        Guid? cityId = null) =>
        new(Guid.CreateVersion7(), $"{type} {country}", type, country, 0.966m, isActive, withholds,
            cityId ?? (type == "ReteICA" ? Bogota : null));

    private static void Validate(
        IReadOnlyCollection<CompanyTaxRateRef> found,
        IReadOnlyCollection<Guid> requested,
        IReadOnlyCollection<Guid>? alreadyAssigned = null,
        string country = "CO") =>
        CompanyWithholdingRules.Validate(
            found.ToDictionary(rate => rate.Id), requested, alreadyAssigned ?? [], country);

    private static string Fails(Action action) => Assert.Throws<CompaniesDomainException>(action).Code;

    public static TheoryData<string> InvalidCases => ["missing", "iva", "retefuente", "spain", "newInactive"];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidRatesAreRejected(string invalid)
    {
        var rate = invalid switch
        {
            "iva" => Rate("IVA", withholds: false),
            "retefuente" => Rate("Retefuente"),
            "spain" => Rate(country: "ES"),
            "newInactive" => Rate(isActive: false),
            _ => null,
        };
        var id = rate?.Id ?? Guid.CreateVersion7();

        Assert.Equal(
            "companies.company.withholding_tax_rate_invalid",
            Fails(() => Validate(rate is null ? [] : [rate], [id])));
    }

    [Fact]
    public void TwoInTheSameCityAndTypeAreRepeated()
    {
        var first = Rate(cityId: Bogota);
        var second = Rate(cityId: Bogota);

        Assert.Equal(
            "companies.company.withholding_municipality_repeated",
            Fails(() => Validate([first, second], [first.Id, second.Id])));
    }

    // Spec 2026-11-18 §4.3: una tasa municipal es una por tipo y municipio, cualquiera sea su alcance (la edición del
    // porcentaje y la re-resolución por ciudad identifican la fila por tipo).
    [Fact]
    public void GoodsAndServicesInTheSameMunicipalityAreRepeated()
    {
        var goods = Rate(cityId: Bogota) with { AppliesTo = WithholdingScope.Goods };
        var services = Rate(cityId: Bogota) with { AppliesTo = WithholdingScope.Services };

        Assert.Equal(
            "companies.company.withholding_municipality_repeated",
            Fails(() => Validate([goods, services], [goods.Id, services.Id])));
    }

    [Fact]
    public void AllWithAnotherScopeIsRepeated()
    {
        var all = Rate(cityId: Bogota);
        var services = Rate(cityId: Bogota) with { AppliesTo = WithholdingScope.Services };

        Assert.Equal(
            "companies.company.withholding_municipality_repeated",
            Fails(() => Validate([all, services], [all.Id, services.Id])));
    }

    [Fact]
    public void TwoOfTheSameScopeAreRepeated()
    {
        var first = Rate(cityId: Bogota) with { AppliesTo = WithholdingScope.Services };
        var second = Rate(cityId: Bogota) with { AppliesTo = WithholdingScope.Services };

        Assert.Equal(
            "companies.company.withholding_municipality_repeated",
            Fails(() => Validate([first, second], [first.Id, second.Id])));
    }

    [Fact]
    public void BogotaAndMedellinPass()
    {
        var bogota = Rate(cityId: Bogota);
        var medellin = Rate(cityId: Medellin);

        Validate([bogota, medellin], [bogota.Id, medellin.Id]);
    }

    // A16: desactivar una tasa asignada da in_use, pero la carrera entre las dos escrituras no la frena ninguna FK. Lo que
    // ya estaba guardado no hace fallar el PUT que lo reenvía.
    [Fact]
    public void AlreadyAssignedInactiveIsTolerated()
    {
        var inactive = Rate(isActive: false);

        Validate([inactive], [inactive.Id], alreadyAssigned: [inactive.Id]);
    }

    [Fact]
    public void AnEmptyListPasses() => Validate([], []);
}
