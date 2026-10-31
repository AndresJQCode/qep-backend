using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>
/// The optional free-text description of a contract: the server normalises it (trim, blank is null), caps it with a domain code
/// and keeps it apart from the price history.
/// </summary>
public sealed class ServiceContractDescriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ServiceContract NewService(string? description = null) =>
        ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100m, "COP",
            BillingCycle.Monthly, new DateOnly(2026, 11, 1), Now, description);

    private static void AssertTooLong(Action act) =>
        Assert.Equal(
            ServiceContractsErrorCodes.ServiceDescriptionTooLong,
            Assert.Throws<ServiceContractsDomainException>(act).Code);

    [Fact]
    public void TheCodeFollowsTheWireConvention() =>
        Assert.Equal("billing.service.description_too_long", ServiceContractsErrorCodes.ServiceDescriptionTooLong);

    [Fact]
    public void ADescriptionIsOptionalWhenCreating() => Assert.Null(NewService().Description);

    [Fact]
    public void CreateTrimsTheEndsAndKeepsInnerLineBreaks() =>
        Assert.Equal("Incluye dominio.\nSoporte 24/7.", NewService("  Incluye dominio.\nSoporte 24/7.\r\n ").Description);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \r\n\t ")]
    public void ABlankDescriptionIsNull(string blank)
    {
        Assert.Null(NewService(blank).Description);

        var service = NewService("Incluye dominio");
        service.Update("Hosting", blank, 100m, "COP", null, null, Now);
        Assert.Null(service.Description);
    }

    [Fact]
    public void TheLimitIsMeasuredAfterTrimming()
    {
        var atTheLimit = new string('a', ServiceContract.DescriptionMaxLength);

        Assert.Equal(1000, ServiceContract.DescriptionMaxLength);
        Assert.Equal(atTheLimit, NewService($"  {atTheLimit}  ").Description);
    }

    [Fact]
    public void CreateRejectsADescriptionOverTheLimit() =>
        AssertTooLong(() => NewService(new string('a', ServiceContract.DescriptionMaxLength + 1)));

    [Fact]
    public void UpdateReplacesAndClearsTheDescription()
    {
        var service = NewService("Antes");

        service.Update("Hosting", "  Después  ", 100m, "COP", null, null, Now);
        Assert.Equal("Después", service.Description);

        service.Update("Hosting", null, 100m, "COP", null, null, Now);
        Assert.Null(service.Description);
    }

    [Fact]
    public void ADescriptionChangeNeedsNoReasonAndRecordsNoPriceChange()
    {
        var service = NewService("Antes");

        var change = service.Update("Hosting", "Después", 100.00m, "COP", null, null, Now.AddDays(1));

        Assert.Null(change);
        Assert.Equal(2, service.Version);
    }

    [Fact]
    public void ARejectedDescriptionTouchesNothing()
    {
        var service = NewService("Antes");

        AssertTooLong(() => service.Update(
            "Otro", new string('a', ServiceContract.DescriptionMaxLength + 1), 120m, "COP", "Ajuste", null, Now.AddDays(1)));

        Assert.Equal("Hosting", service.Name);
        Assert.Equal("Antes", service.Description);
        Assert.Equal(100m, service.Price);
        Assert.Equal(1, service.Version);
    }

    [Fact]
    public void ARejectedPriceChangeKeepsTheDescription()
    {
        var service = NewService("Antes");

        Assert.Throws<ServiceContractsDomainException>(() => service.Update("Hosting", "Después", 120m, "COP", null, null, Now));

        Assert.Equal("Antes", service.Description);
    }

    [Fact]
    public void TheCommandsCarryTheDescriptionOfTheRequest()
    {
        var create = CreateServiceCommand.From(
            Guid.NewGuid(), new CreateServiceRequest(Guid.NewGuid(), "Hosting", 100m, null, "Monthly", new DateOnly(2026, 11, 1), null, "Incluye dominio"));
        var update = UpdateServiceCommand.From(
            Guid.NewGuid(), Guid.NewGuid(), new UpdateServiceRequest("Hosting", 100m, "COP", null, "Incluye dominio"), 1);

        Assert.Equal("Incluye dominio", create.Description);
        Assert.Equal("Incluye dominio", update.Description);
    }

    [Theory]
    [InlineData("Incluye dominio")]
    [InlineData(null)]
    public void TheServiceDtoExposesTheDescription(string? description)
    {
        var dto = ServiceContractsMapping.ToServiceDto(
            NewService(description), new CustomerRefDto(Guid.NewGuid(), "Panadería", null, null), null, 0, []);

        Assert.Equal(description, dto.Description);
    }
}
