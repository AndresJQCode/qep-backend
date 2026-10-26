using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>Spec 2026-10-26 §4.4: el impacto de confirmar, grupo por grupo, y todos los grupos viajan también en cero.</summary>
public sealed class ResolveImpactCalculatorTests
{
    private static readonly CityRefDto Cali = new(Guid.CreateVersion7(), "CALI", "76001");
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, string> Names = new() { [TenantA] = "Ritual", [TenantB] = "Lulo" };

    [Fact]
    public void PendingWithoutShippingAndPartyCityOnlyIsToFixByTenant()
    {
        var occurrences = new[]
        {
            Occurrences.Of(TenantA, partyCityOnly: true),
            Occurrences.Of(TenantA, partyCityOnly: true),
            Occurrences.Of(TenantB, partyCityOnly: true),
        };
        var orders = occurrences.Select(occurrence => Order(occurrence, "Pending", hasShipping: false)).ToList();

        var impact = ResolveImpactCalculator.Compute(Cali, occurrences, orders, [], Names);

        Assert.Equal(3, impact.OrdersToFix.Count);
        Assert.Equal(
            [new ImpactTenantCountDto(TenantA, "Ritual", 2), new ImpactTenantCountDto(TenantB, "Lulo", 1)],
            impact.OrdersToFix.ByTenant);
        Assert.Equal(0, impact.OrdersMissingData.Count);
        Assert.Equal(Cali, impact.City);
    }

    [Fact]
    public void PendingWithoutShippingMissingDataIsMissingData()
    {
        var occurrence = Occurrences.Of(TenantA, partyCityOnly: false);

        var impact = ResolveImpactCalculator.Compute(
            Cali, [occurrence], [Order(occurrence, "Pending", hasShipping: false)], [], Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantA, "Ritual", 1)]), impact.OrdersMissingData);
        Assert.Equal(0, impact.OrdersToFix.Count);
    }

    [Fact]
    public void PendingWithShippingIsWithShipping()
    {
        var occurrence = Occurrences.Of(TenantB, partyCityOnly: true);

        var impact = ResolveImpactCalculator.Compute(
            Cali, [occurrence], [Order(occurrence, "Pending", hasShipping: true)], [], Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantB, "Lulo", 1)]), impact.OrdersWithShipping);
        Assert.Equal(0, impact.OrdersToFix.Count);
    }

    [Fact]
    public void NotPendingIsListedWithTenantNumberAndStatus()
    {
        var approved = Occurrences.Of(TenantA, partyCityOnly: true);
        var cancelled = Occurrences.Of(TenantB, partyCityOnly: true);
        var gone = Occurrences.Of(TenantB, partyCityOnly: true);

        var impact = ResolveImpactCalculator.Compute(
            Cali,
            [approved, cancelled, gone],
            [Order(approved, "Approved", hasShipping: true, number: "PED-7"), Order(cancelled, "Cancelled", hasShipping: false, number: "PED-2")],
            [],
            Names);

        Assert.Equal(2, impact.OrdersNotPending.Count);
        Assert.Equal(
            [
                new ImpactOrderDto(TenantB, "Lulo", cancelled.OrderId, "PED-2", "Cancelled"),
                new ImpactOrderDto(TenantA, "Ritual", approved.OrderId, "PED-7", "Approved"),
            ],
            impact.OrdersNotPending.Orders);
        Assert.Equal(0, impact.OrdersWithShipping.Count);
    }

    [Fact]
    public void CityOnlyIncompleteVersionOneIsToCompleteAndTheRestStayIncomplete()
    {
        var toComplete = Occurrences.Of(TenantA, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var missingMore = Occurrences.Of(TenantA, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: false);
        var edited = Occurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var merged = Occurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var alreadyComplete = Occurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        CustomerInsight Customer(UnresolvedCityOccurrence occurrence, bool complete = false, bool isMerged = false, long version = 1) =>
            new(occurrence.TenantId, occurrence.CustomerId!.Value, complete, isMerged, IsActive: true, version);

        var impact = ResolveImpactCalculator.Compute(
            Cali,
            [toComplete, missingMore, edited, merged, alreadyComplete],
            [],
            [
                Customer(toComplete), Customer(missingMore), Customer(edited, version: 2), Customer(merged, isMerged: true),
                Customer(alreadyComplete, complete: true, version: 2),
            ],
            Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantA, "Ritual", 1)]), impact.CustomersToComplete);
        Assert.Equal(
            new ImpactCountDto(3, [new ImpactTenantCountDto(TenantB, "Lulo", 2), new ImpactTenantCountDto(TenantA, "Ritual", 1)]),
            impact.CustomersStayIncomplete);
    }

    [Fact]
    public void EveryGroupTravelsInZero()
    {
        var impact = ResolveImpactCalculator.Compute(Cali, [], [], [], Names);

        ImpactCountDto[] groups =
        [
            impact.OrdersToFix, impact.OrdersMissingData, impact.OrdersWithShipping, impact.CustomersToComplete,
            impact.CustomersStayIncomplete,
        ];
        Assert.All(groups, group =>
        {
            Assert.NotNull(group);
            Assert.Equal(0, group.Count);
            Assert.Empty(group.ByTenant);
        });
        Assert.Equal(0, impact.OrdersNotPending.Count);
        Assert.Empty(impact.OrdersNotPending.Orders);
    }

    private static OrderInsight Order(UnresolvedCityOccurrence occurrence, string status, bool hasShipping, string number = "PED-1") =>
        new(occurrence.TenantId, occurrence.OrderId, number, status, hasShipping, IsStorePickup: false);
}

/// <summary><see cref="UnresolvedCityOccurrence"/> no tiene constructor público (la escribe el SQL del reporte, SC-03): las pruebas
/// puras la arman por reflexión.</summary>
internal static class Occurrences
{
    public static UnresolvedCityOccurrence Of(
        Guid tenantId, bool partyCityOnly, Guid? customerId = null, bool customerCityOnly = false)
    {
        var occurrence = (UnresolvedCityOccurrence)Activator.CreateInstance(typeof(UnresolvedCityOccurrence), nonPublic: true)!;
        Set(occurrence, nameof(UnresolvedCityOccurrence.Id), Guid.CreateVersion7());
        Set(occurrence, nameof(UnresolvedCityOccurrence.UnresolvedCityId), UnresolvedCityId.New());
        Set(occurrence, nameof(UnresolvedCityOccurrence.TenantId), tenantId);
        Set(occurrence, nameof(UnresolvedCityOccurrence.Source), "shopify");
        Set(occurrence, nameof(UnresolvedCityOccurrence.SourceRef), Guid.CreateVersion7());
        Set(occurrence, nameof(UnresolvedCityOccurrence.OrderId), Guid.CreateVersion7());
        Set(occurrence, nameof(UnresolvedCityOccurrence.CustomerId), customerId);
        Set(occurrence, nameof(UnresolvedCityOccurrence.PartyCityOnly), partyCityOnly);
        Set(occurrence, nameof(UnresolvedCityOccurrence.CustomerCityOnly), customerCityOnly);
        return occurrence;
    }

    private static void Set(UnresolvedCityOccurrence occurrence, string property, object? value) =>
        typeof(UnresolvedCityOccurrence).GetProperty(property)!.SetValue(occurrence, value);
}
