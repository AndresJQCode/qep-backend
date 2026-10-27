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
            ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true),
            ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true),
            ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true),
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
        var occurrence = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: false);

        var impact = ResolveImpactCalculator.Compute(
            Cali, [occurrence], [Order(occurrence, "Pending", hasShipping: false)], [], Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantA, "Ritual", 1)]), impact.OrdersMissingData);
        Assert.Equal(0, impact.OrdersToFix.Count);
    }

    [Fact]
    public void PendingWithShippingIsWithShipping()
    {
        var occurrence = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true);

        var impact = ResolveImpactCalculator.Compute(
            Cali, [occurrence], [Order(occurrence, "Pending", hasShipping: true)], [], Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantB, "Lulo", 1)]), impact.OrdersWithShipping);
        Assert.Equal(0, impact.OrdersToFix.Count);
    }

    [Fact]
    public void NotPendingIsListedWithTenantNumberAndStatus()
    {
        var approved = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true);
        var cancelled = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true);
        var gone = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true);

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
    public void AStorePickupOrderIsInNoOrderGroup()
    {
        // Nunca recibe parte de envío (P10): ni «a corregir», ni «faltan datos», ni «no se toca» aunque ya no esté Pending.
        var pending = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: false);
        var approved = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: false);

        var impact = ResolveImpactCalculator.Compute(
            Cali,
            [pending, approved],
            [Order(pending, "Pending", hasShipping: false, storePickup: true), Order(approved, "Approved", hasShipping: false, storePickup: true)],
            [],
            Names);

        Assert.Equal(0, impact.OrdersToFix.Count);
        Assert.Equal(0, impact.OrdersMissingData.Count);
        Assert.Equal(0, impact.OrdersWithShipping.Count);
        Assert.Equal(0, impact.OrdersNotPending.Count);
    }

    [Fact]
    public void CityOnlyIncompleteVersionOneIsToCompleteAndOnlyTheOnesMissingMoreStayIncomplete()
    {
        var toComplete = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var missingMore = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: false);
        var missingMoreAndEdited = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: false);
        var edited = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var merged = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var inactive = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        var alreadyComplete = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7(), customerCityOnly: true);
        CustomerInsight Customer(
            UnresolvedCityOccurrence occurrence, bool complete = false, bool isMerged = false, bool isActive = true, long version = 1) =>
            new(occurrence.TenantId, occurrence.CustomerId!.Value, complete, isMerged, isActive, version);

        var impact = ResolveImpactCalculator.Compute(
            Cali,
            [toComplete, missingMore, missingMoreAndEdited, edited, merged, inactive, alreadyComplete],
            [],
            [
                Customer(toComplete), Customer(missingMore), Customer(missingMoreAndEdited, version: 2), Customer(edited, version: 2),
                Customer(merged, isMerged: true), Customer(inactive, isActive: false), Customer(alreadyComplete, complete: true, version: 2),
            ],
            Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantA, "Ritual", 1)]), impact.CustomersToComplete);
        // Sólo los que la importación creó incompletos y les falta algo más que la ciudad, editados o no. El editado, el
        // fusionado y el inactivo que sólo necesitaban la ciudad no están en ningún grupo.
        Assert.Equal(
            new ImpactCountDto(2, [new ImpactTenantCountDto(TenantB, "Lulo", 1), new ImpactTenantCountDto(TenantA, "Ritual", 1)]),
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

    private static OrderInsight Order(
        UnresolvedCityOccurrence occurrence, string status, bool hasShipping, string number = "PED-1", bool storePickup = false) =>
        new(occurrence.TenantId, occurrence.OrderId, number, status, hasShipping, storePickup);
}

/// <summary><see cref="UnresolvedCityOccurrence"/> no tiene constructor público (la escribe el SQL del reporte, SC-03): las pruebas
/// puras de los cálculos de impacto (confirmar y corregir) la arman por reflexión.</summary>
internal static class ResolveImpactOccurrences
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
