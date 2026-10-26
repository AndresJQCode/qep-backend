using Modules.Geography.Application;

namespace Modules.Geography.UnitTests;

/// <summary>Spec 2026-10-26 §4.6: el impacto de corregir un sinónimo, grupo por grupo, desde las huellas de los dos orígenes y las
/// ocurrencias de la fila; todos los grupos viajan también en cero.</summary>
public sealed class CorrectImpactCalculatorTests
{
    private static readonly CityRefDto Cali = new(Guid.CreateVersion7(), "CALI", "76001");
    private static readonly CityRefDto Palmira = new(Guid.CreateVersion7(), "PALMIRA", "76520");
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, string> Names = new() { [TenantA] = "Ritual", [TenantB] = "Lulo" };

    [Fact]
    public void APendingOrderStillAsWrittenChanges()
    {
        var impact = CorrectImpactCalculator.Compute(
            Cali,
            Palmira,
            [
                OrderFootprint(TenantA, "Pending", stillAsWritten: true),
                OrderFootprint(TenantA, "Pending", stillAsWritten: true),
                OrderFootprint(TenantB, "Pending", stillAsWritten: true),
            ],
            [],
            Names);

        Assert.Equal(
            new ImpactCountDto(3, [new ImpactTenantCountDto(TenantA, "Ritual", 2), new ImpactTenantCountDto(TenantB, "Lulo", 1)]),
            impact.OrdersToChange);
        Assert.Equal(0, impact.OrdersEdited.Count);
        Assert.Equal((Cali, Palmira), (impact.From, impact.To));
    }

    // Alguien tocó la parte de envío después de que el sinónimo la escribiera: se respeta, y se cuenta aparte.
    [Fact]
    public void APendingOrderEditedIsRespected()
    {
        var impact = CorrectImpactCalculator.Compute(
            Cali,
            Palmira,
            [OrderFootprint(TenantA, "Pending", stillAsWritten: true), OrderFootprint(TenantB, "Pending", stillAsWritten: false)],
            [],
            Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantA, "Ritual", 1)]), impact.OrdersToChange);
        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantB, "Lulo", 1)]), impact.OrdersEdited);
    }

    // Un pedido que ya no está Pending no se recorrige (con la parte intacta o editada): se lista completo con su estado. Uno que ya
    // no existe (sin estado) no cuenta en ningún grupo.
    [Fact]
    public void ANotPendingOrderIsListedWithItsStatus()
    {
        var approved = OrderFootprint(TenantA, "Approved", stillAsWritten: true, number: "PED-7");
        var cancelled = OrderFootprint(TenantB, "Cancelled", stillAsWritten: false, number: "PED-2");
        var gone = OrderFootprint(TenantB, null, stillAsWritten: false);

        var impact = CorrectImpactCalculator.Compute(Cali, Palmira, [approved, cancelled, gone], [], Names);

        Assert.Equal(2, impact.OrdersNotPending.Count);
        Assert.Equal(
            [
                new ImpactOrderDto(TenantB, "Lulo", cancelled.TargetId, "PED-2", "Cancelled"),
                new ImpactOrderDto(TenantA, "Ritual", approved.TargetId, "PED-7", "Approved"),
            ],
            impact.OrdersNotPending.Orders);
        Assert.Equal(0, impact.OrdersToChange.Count);
        Assert.Equal(0, impact.OrdersEdited.Count);
    }

    [Fact]
    public void ACustomerAtItsVersionChangesAndAnEditedOneIsCounted()
    {
        var impact = CorrectImpactCalculator.Compute(
            Cali,
            Palmira,
            [
                CustomerFootprint(TenantA, stillAsWritten: true),
                CustomerFootprint(TenantA, stillAsWritten: true),
                CustomerFootprint(TenantB, stillAsWritten: false),
            ],
            [],
            Names);

        Assert.Equal(new ImpactCountDto(2, [new ImpactTenantCountDto(TenantA, "Ritual", 2)]), impact.CustomersToChange);
        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantB, "Lulo", 1)]), impact.CustomersEdited);
    }

    // Una ocurrencia está corregida si su pedido o su cliente tiene huella; si ninguno, el trabajo falló, se saltó o sigue pendiente.
    [Fact]
    public void OccurrencesWithoutCorrectionAreUncorrected()
    {
        var customerId = Guid.CreateVersion7();
        var corrected = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true);
        var correctedThroughTheCustomer = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: false, customerId: customerId, customerCityOnly: true);
        var uncorrected = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true);
        var uncorrectedOfAnotherTenant = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true, customerId: Guid.CreateVersion7());
        // La misma llave en otro tenant no es la huella: los pares viajan siempre juntos.
        var sameOrderOtherTenant = ResolveImpactOccurrences.Of(TenantB, partyCityOnly: true);

        var impact = CorrectImpactCalculator.Compute(
            Cali,
            Palmira,
            [
                OrderFootprint(TenantA, "Pending", stillAsWritten: true, orderId: corrected.OrderId),
                CustomerFootprint(TenantA, stillAsWritten: true, customerId: customerId),
                OrderFootprint(TenantA, "Pending", stillAsWritten: true, orderId: sameOrderOtherTenant.OrderId),
            ],
            [corrected, correctedThroughTheCustomer, uncorrected, uncorrectedOfAnotherTenant, sameOrderOtherTenant],
            Names);

        Assert.Equal(
            new ImpactCountDto(3, [new ImpactTenantCountDto(TenantB, "Lulo", 2), new ImpactTenantCountDto(TenantA, "Ritual", 1)]),
            impact.OccurrencesUncorrected);
    }

    // El consumidor salta la huella que ya está en la ciudad nueva: no es un cambio ni una edición, pero sí una ocurrencia corregida.
    [Fact]
    public void AFootprintAlreadyInTheTargetCityIsNotChangedNorEditedButStaysCorrected()
    {
        var customerId = Guid.CreateVersion7();
        var order = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: true);
        var customerOccurrence = ResolveImpactOccurrences.Of(TenantA, partyCityOnly: false, customerId: customerId, customerCityOnly: true);

        var impact = CorrectImpactCalculator.Compute(
            Cali,
            Palmira,
            [
                OrderFootprint(TenantA, "Pending", stillAsWritten: true, orderId: order.OrderId) with { CityId = Palmira.Id },
                OrderFootprint(TenantB, "Pending", stillAsWritten: false) with { CityId = Palmira.Id },
                CustomerFootprint(TenantA, stillAsWritten: true, customerId: customerId) with { CityId = Palmira.Id },
                CustomerFootprint(TenantB, stillAsWritten: false) with { CityId = Palmira.Id },
                OrderFootprint(TenantB, "Pending", stillAsWritten: true),
            ],
            [order, customerOccurrence],
            Names);

        Assert.Equal(new ImpactCountDto(1, [new ImpactTenantCountDto(TenantB, "Lulo", 1)]), impact.OrdersToChange);
        Assert.Equal(0, impact.OrdersEdited.Count);
        Assert.Equal(0, impact.CustomersToChange.Count);
        Assert.Equal(0, impact.CustomersEdited.Count);
        Assert.Equal(0, impact.OccurrencesUncorrected.Count);
    }

    [Fact]
    public void EveryGroupTravelsInZero()
    {
        var impact = CorrectImpactCalculator.Compute(Cali, Palmira, [], [], Names);

        ImpactCountDto[] groups =
        [
            impact.OrdersToChange, impact.OrdersEdited, impact.CustomersToChange, impact.CustomersEdited, impact.OccurrencesUncorrected,
        ];
        Assert.All(groups, group =>
        {
            Assert.NotNull(group);
            Assert.Equal(0, group.Count);
            Assert.Empty(group.ByTenant);
        });
        Assert.Equal(0, impact.OrdersNotPending.Count);
        Assert.Empty(impact.OrdersNotPending.Orders);
        Assert.Equal((Cali, Palmira), (impact.From, impact.To));
    }

    private static CorrectionInsight OrderFootprint(
        Guid tenantId, string? status, bool stillAsWritten, string number = "PED-1", Guid? orderId = null) =>
        new(tenantId, "Order", orderId ?? Guid.CreateVersion7(), Cali.Id, status is null ? null : number, status, stillAsWritten);

    private static CorrectionInsight CustomerFootprint(Guid tenantId, bool stillAsWritten, Guid? customerId = null) =>
        new(tenantId, "Customer", customerId ?? Guid.CreateVersion7(), Cali.Id, null, null, stillAsWritten);
}
