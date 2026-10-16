using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class CarrierTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private static Carrier NewCarrier(
        string name = "Coordinadora",
        IReadOnlyCollection<string>? external = null) =>
        Carrier.Create(CarrierId.New(), TenantId, name, external ?? [], false, 1, Now);

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<QuotationsDomainException>(act).Code);

    [Fact]
    public void NormalizeIgnoresCaseAccentsAndSpaces() =>
        Assert.Equal("inter rapidisimo", CarrierName.Normalize("  Inter   Rapidísimo "));

    [Fact]
    public void CreateTrimsTheNameAndKeepsItsNormalizedEntry()
    {
        var carrier = NewCarrier("  Coordinadora ");

        Assert.Equal("Coordinadora", carrier.Name);
        var entry = Assert.Single(carrier.Names);
        Assert.Equal(CarrierNameKind.Name, entry.Kind);
        Assert.Equal("coordinadora", entry.NormalizedName);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.True(carrier.IsActive);
        Assert.Equal(1, carrier.Version);
    }

    [Fact]
    public void RejectsAnEmptyName() =>
        AssertCode("quotations.carrier.name_required", () => NewCarrier("  "));

    [Fact]
    public void RejectsANameLongerThan60() =>
        AssertCode("quotations.carrier.name_too_long", () => NewCarrier(new string('a', 61)));

    [Fact]
    public void RejectsAnExternalNameLongerThan120() =>
        AssertCode(
            "quotations.carrier.external_name_too_long",
            () => NewCarrier(external: [new string('a', 121)]));

    [Fact]
    public void RejectsMoreThan20ExternalNames() =>
        AssertCode(
            "quotations.carrier.too_many_external_names",
            () => NewCarrier(external: Enumerable.Range(0, 21).Select(i => $"n{i}").ToList()));

    [Fact]
    public void RejectsAnExternalNameRepeatedOrEqualToItsOwnName()
    {
        AssertCode(
            "quotations.carrier.external_name_duplicated",
            () => NewCarrier(external: ["Envío", "envio"]));
        AssertCode(
            "quotations.carrier.external_name_duplicated",
            () => NewCarrier("Coordinadora", ["COORDINADORA"]));
    }

    [Fact]
    public void UpdateReplacesTheExternalNames()
    {
        var carrier = NewCarrier(external: ["Viejo"]);

        carrier.Update("Servientrega", ["Nuevo", "Otro"], true, Now.AddHours(1));

        Assert.Equal("Servientrega", carrier.Name);
        Assert.True(carrier.SupportsCashOnDelivery);
        Assert.Equal(["Nuevo", "Otro"], carrier.ExternalNames);
        Assert.Equal(
            ["servientrega", "nuevo", "otro"],
            carrier.Names.Select(n => n.NormalizedName).ToArray());
        Assert.Equal(2, carrier.Version);
    }

    [Fact]
    public void DeactivateAndActivateToggleIsActive()
    {
        var carrier = NewCarrier();

        carrier.Deactivate(Now);
        Assert.False(carrier.IsActive);
        Assert.Equal(2, carrier.Version);

        carrier.Activate(Now);
        Assert.True(carrier.IsActive);
        Assert.Equal(3, carrier.Version);

        carrier.MoveTo(5, Now);
        Assert.Equal(5, carrier.SortOrder);
        Assert.Equal(4, carrier.Version);
    }
}
