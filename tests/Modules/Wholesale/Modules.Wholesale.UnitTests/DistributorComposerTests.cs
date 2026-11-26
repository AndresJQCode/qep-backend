using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Los DTOs del contrato de distribuidores, armados en lote.</summary>
public sealed class DistributorComposerTests
{
    private static CancellationToken Ct => DistributorWorld.Ct;

    [Fact]
    public async Task DetailAndSummaryCarryTheCountryAndTheType()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited("123", identificationType: "CC");

        var detail = await world.Composer().DetailAsync(distributor, Ct);
        var summary = Assert.Single(await world.Composer().SummariesAsync(world.Tenant, [distributor], Ct));

        Assert.Equal(("CO", "CC"), (detail.Country, detail.IdentificationType));
        Assert.Equal(("CO", "CC"), (summary.Country, summary.IdentificationType));
    }

    [Fact]
    public async Task TheDetailCarriesTheResolvedColombianCity()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited(cityId: world.MedellinId, address: "Calle 10 # 40-20");

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(
            new DistributorCityDto(world.MedellinId, "Medellín", world.Cities.DepartmentId, "Antioquia"), detail.City);
        Assert.Equal("Calle 10 # 40-20", detail.Address);
    }

    [Fact]
    public async Task TheDetailCarriesTheTypedCityOutsideColombia()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited(country: "ES", identificationType: "NIF", cityName: "Madrid");

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(new DistributorCityDto(null, "Madrid"), detail.City);
    }

    [Fact]
    public async Task TheDetailWithoutCityHasNullCity()
    {
        var world = new DistributorWorld();

        var detail = await world.Composer().DetailAsync(world.SeedInvited(), Ct);

        Assert.Null(detail.City);
        Assert.Null(detail.Address);
    }

    [Fact]
    public async Task AllowedActionsAreEmptyWithoutTheManagePermission()
    {
        var world = new DistributorWorld(WholesalePermissions.DistributorRead);
        var invited = world.SeedInvited();

        var detail = await world.Composer().DetailAsync(invited, Ct);
        var summary = Assert.Single(await world.Composer().SummariesAsync(world.Tenant, [invited], Ct));

        Assert.Empty(detail.AllowedActions);
        Assert.False(summary.Invitation!.CanResend);
    }

    [Fact]
    public async Task AllowedActionsFollowTheStateInLowercase()
    {
        var world = new DistributorWorld();

        var invited = await world.Composer().DetailAsync(world.SeedInvited("1"), Ct);
        var active = await world.Composer().DetailAsync(world.SeedActive("2"), Ct);

        Assert.Equal(["edit", "resend", "delete"], invited.AllowedActions);
        Assert.Equal(["edit", "block"], active.AllowedActions);
    }

    [Fact]
    public async Task SummariesResolveNamesInOneCallPerPort()
    {
        var world = new DistributorWorld();
        var distributorTenant = Guid.CreateVersion7();
        world.TenantNames.Names[distributorTenant] = "Distri Norte";
        var items = new[] { world.SeedInvited("1"), world.SeedActive("2", distributorTenant), world.SeedActive("3") };

        var summaries = await world.Composer().SummariesAsync(world.Tenant, items, Ct);

        Assert.Equal(3, summaries.Count);
        Assert.Equal(1, world.Members.FindNamesCalls);
        Assert.Equal(1, world.PriceLists.ListCalls);
        Assert.Equal(1, world.TenantNames.Calls);
        Assert.Equal(("Distri Norte", distributorTenant), (summaries[1].DistributorTenant!.Name, summaries[1].DistributorTenant!.Id));
        Assert.Null(summaries[0].DistributorTenant);
        Assert.All(summaries, summary => Assert.Equal(("Mayoristas", -15m), (summary.PriceList.Name, summary.PriceList.AdjustmentPercent)));
    }

    [Fact]
    public async Task AMissingMemberIsSinNombre()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited();
        world.Members.Names.Clear();

        var detail = await world.Composer().DetailAsync(distributor, Ct);
        var summary = Assert.Single(await world.Composer().SummariesAsync(world.Tenant, [distributor], Ct));

        Assert.Equal((world.Advisor, "Sin nombre"), (detail.Advisor.MemberId, detail.Advisor.Name));
        Assert.Equal("Sin nombre", summary.Advisor.Name);
    }

    // Ruling W3: la fila pinta «Vence 20/11 · Reenviar»: version, y la invitación sólo mientras está invitada. canResend sigue
    // en true con la invitación vencida, porque reenviar es como se renueva.
    [Fact]
    public async Task TheSummaryCarriesVersionAndTheInvitationOnlyWhileInvited()
    {
        var world = new DistributorWorld();
        var invited = world.SeedInvited("1");
        var active = world.SeedActive("2");
        world.Clock.UtcNow = DistributorWorld.Now.AddDays(4);

        var summaries = await world.Composer().SummariesAsync(world.Tenant, [invited, active], Ct);

        Assert.Equal((1L, true), (summaries[0].Version, summaries[0].InvitationExpired));
        Assert.Equal(new DistributorInvitationSummaryDto(DistributorWorld.Now.AddHours(72), CanResend: true), summaries[0].Invitation);
        Assert.Equal((2L, false), (summaries[1].Version, summaries[1].InvitationExpired));
        Assert.Null(summaries[1].Invitation);
    }

    [Fact]
    public async Task TheDetailCarriesTheBillingCompanyAndTheInvitation()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedActive();

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(new DistributorCompanyDto(world.Company.Id, "Mayorista SAS"), detail.BillingCompany);
        Assert.Equal(
            new DistributorInvitationDto(
                DistributorWorld.Now, DistributorWorld.Now.AddHours(72), false, DistributorWorld.Now.AddHours(1), null),
            detail.Invitation);
        Assert.Equal((DistributorWorld.Now.AddHours(1), distributor.CustomerId), (detail.ActivatedAt, detail.CustomerId));
        Assert.Equal(nameof(DistributorStatus.Active), detail.Status);
    }

    // ---- Spec 2026-11-18: aviso por WhatsApp de la invitación ----

    private const string Phone = "+573001234567";

    // BFF: el detalle pinta el estado del aviso y su número sin pedir la conexión aparte.
    [Fact]
    public async Task DetailCarriesThePhoneAndTheNoticeWithItsConnectionLabel()
    {
        var world = new DistributorWorld();
        var connection = world.WhatsApp.AddReady("+57 300 999 0000");
        var distributor = world.SeedInvited(phone: Phone, whatsAppConnectionId: connection);

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(Phone, detail.Phone);
        Assert.Equal(new DistributorWhatsAppNoticeDto("Pending", null, "+57 300 999 0000"), detail.Invitation.WhatsAppNotice);
    }

    // D-7: sin aviso no se pregunta por la conexión.
    [Fact]
    public async Task DetailWithoutPhoneHasANullNotice()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited();
        world.WhatsApp.ThrowWhenCalled = true;

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Null(detail.Phone);
        Assert.Null(detail.Invitation.WhatsAppNotice);
    }

    [Fact]
    public async Task AFailedNoticeCarriesItsCode()
    {
        var world = new DistributorWorld();
        var connection = world.WhatsApp.AddReady("+57 300 999 0000");
        var distributor = world.SeedInvited(phone: Phone, whatsAppConnectionId: connection);
        distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Failed, "connection_unavailable:paused");

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(
            new DistributorWhatsAppNoticeDto("Failed", "connection_unavailable:paused", "+57 300 999 0000"),
            detail.Invitation.WhatsAppNotice);
    }

    [Fact]
    public async Task ANoticeOnAConnectionThatIsGoneIsLabeledSinNombre()
    {
        var world = new DistributorWorld();
        var distributor = world.SeedInvited(phone: Phone, whatsAppConnectionId: Guid.CreateVersion7());
        distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Sent, null);

        var detail = await world.Composer().DetailAsync(distributor, Ct);

        Assert.Equal(new DistributorWhatsAppNoticeDto("Sent", null, "Sin nombre"), detail.Invitation.WhatsAppNotice);
    }

    [Fact]
    public async Task SummariesCarryThePhone()
    {
        var world = new DistributorWorld();
        var withPhone = world.SeedInvited("1", phone: Phone, whatsAppConnectionId: Guid.CreateVersion7());
        var withoutPhone = world.SeedInvited("2");
        world.WhatsApp.ThrowWhenCalled = true;

        var summaries = await world.Composer().SummariesAsync(world.Tenant, [withPhone, withoutPhone], Ct);

        Assert.Equal([Phone, null], summaries.Select(summary => summary.Phone));
    }
}
