using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>Las claves del 422 de distribuidores del contrato: <c>Email</c>, <c>BusinessName</c>, <c>IdentificationNumber</c>,
/// <c>ContactName</c>, <c>PriceListId</c>, <c>AdvisorId</c>, <c>BillingCompanyId</c> y <c>Status</c>.</summary>
public sealed class DistributorWriteRulesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static InviteDistributorCommand Invite(
        string email = "ana@distri.co",
        string businessName = "Distribuciones Norte SAS",
        string identificationNumber = "900123456",
        string contactName = "Ana Pérez") =>
        new(Tenant, email, businessName, identificationNumber, contactName, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "trace");

    private static string[] Failed(InviteDistributorCommand command) =>
        new InviteDistributorValidator().Validate(command).Errors.Select(error => error.PropertyName).Distinct().ToArray();

    [Fact]
    public void InviteRequiresAValidEmailAndTheTextLimits()
    {
        Assert.Empty(Failed(Invite()));
        Assert.Empty(Failed(Invite(email: "  Ana@Distri.CO  ")));
        Assert.Equal(["Email"], Failed(Invite(email: "ana-distri.co")));
        Assert.Equal(["Email"], Failed(Invite(email: "  ")));
        Assert.Equal(["Email"], Failed(Invite(email: new string('a', 245) + "@distri.co")));
        Assert.Equal(["BusinessName"], Failed(Invite(businessName: new string('a', 161))));
        Assert.Equal(["BusinessName"], Failed(Invite(businessName: " ")));
        Assert.Empty(Failed(Invite(businessName: " " + new string('a', 160) + " ")));
        Assert.Equal(["IdentificationNumber"], Failed(Invite(identificationNumber: new string('1', 33))));
        Assert.Equal(["IdentificationNumber"], Failed(Invite(identificationNumber: "")));
        Assert.Equal(["ContactName"], Failed(Invite(contactName: new string('a', 161))));
        Assert.Equal(["ContactName"], Failed(Invite(contactName: "")));
    }

    [Fact]
    public void InviteAndUpdateRequireTheThreeReferences()
    {
        var invite = new InviteDistributorCommand(
            Tenant, "ana@distri.co", "Norte", "900123456", "Ana", Guid.Empty, Guid.Empty, Guid.Empty, "trace");
        var update = new UpdateDistributorCommand(Tenant, Guid.CreateVersion7(), 1, Guid.Empty, Guid.Empty, Guid.Empty);

        Assert.Equal(["PriceListId", "AdvisorId", "BillingCompanyId"], Failed(invite));
        Assert.Equal(
            ["PriceListId", "AdvisorId", "BillingCompanyId"],
            new UpdateDistributorValidator().Validate(update).Errors.Select(error => error.PropertyName).Distinct());
    }

    // Spec 2026-11-18: el teléfono se recorta y llega con a lo sumo 32 caracteres; la conexión vacía sólo cuenta con teléfono.
    [Fact]
    public void ThePhoneHasAtMost32CharactersAndAnEmptyConnectionOnlyCountsWithAPhone()
    {
        Assert.Empty(Failed(Invite() with { Phone = " " + new string('1', 32) + " ", WhatsAppConnectionId = Guid.CreateVersion7() }));
        Assert.Equal(["Phone"], Failed(Invite() with { Phone = new string('1', 33) }));
        Assert.Equal(["WhatsAppConnectionId"], Failed(Invite() with { Phone = "3001234567", WhatsAppConnectionId = Guid.Empty }));
        Assert.Empty(Failed(Invite() with { Phone = "  ", WhatsAppConnectionId = Guid.Empty }));

        var update = new UpdateDistributorCommand(
            Tenant, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new string('1', 33), Guid.Empty);
        Assert.Equal(
            ["Phone", "WhatsAppConnectionId"],
            new UpdateDistributorValidator().Validate(update).Errors.Select(error => error.PropertyName).Distinct());
    }

    [Theory]
    [InlineData("paused", false)]
    [InlineData("1", false)]
    [InlineData("active", true)]
    [InlineData("INVITED", true)]
    [InlineData("Blocked", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void ListRejectsAnUnknownStatusAndAcceptsAnyCase(string? status, bool valid)
    {
        var errors = new ListDistributorsValidator()
            .Validate(new ListDistributorsQuery(Tenant, status, null, 1, 50))
            .Errors.Select(error => error.PropertyName).ToArray();

        Assert.Equal(valid ? [] : ["Status"], errors);
    }
}
