using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): la relación con un distribuidor y su invitación (D-1, D-14).</summary>
public sealed class DistributorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private static Distributor Invite(string email = "ana@distri.co", string tokenHash = "hash-1") =>
        Distributor.Invite(
            Tenant, email, "  Distribuciones Norte SAS ", " 900123456 ", " Ana Pérez ", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), tokenHash, Guid.CreateVersion7(), Now);

    private static Distributor Active()
    {
        var distributor = Invite();
        distributor.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));
        return distributor;
    }

    private static Distributor Blocked()
    {
        var distributor = Active();
        distributor.Block(Now.AddHours(2));
        return distributor;
    }

    [Fact]
    public void InviteStartsInvitedWithA72HourInvitationAndALowercaseEmail()
    {
        var priceList = PriceListId.New();
        var advisor = Guid.CreateVersion7();
        var company = Guid.CreateVersion7();
        var invitedBy = Guid.CreateVersion7();
        var reserved = Guid.CreateVersion7();

        var distributor = Distributor.Invite(
            Tenant, " Ana@Distri.CO ", "  Distribuciones Norte SAS ", " 900123456 ", " Ana Pérez ", priceList, advisor,
            company, invitedBy, "hash-1", reserved, Now);

        Assert.NotEqual(Guid.Empty, distributor.Id.Value);
        Assert.Equal((Tenant, "ana@distri.co", "Distribuciones Norte SAS", "900123456", "Ana Pérez"),
            (distributor.TenantId, distributor.Email, distributor.BusinessName, distributor.IdentificationNumber, distributor.ContactName));
        Assert.Equal((priceList, advisor, company, invitedBy),
            (distributor.PriceListId, distributor.AdvisorMemberId, distributor.BillingCompanyId, distributor.InvitedByUserId));
        Assert.Equal((DistributorStatus.Invited, 1L, Now, Now), (distributor.Status, distributor.Version, distributor.CreatedAt, distributor.UpdatedAt));
        Assert.Null(distributor.DistributorTenantId);
        Assert.Null(distributor.CustomerId);
        Assert.Null(distributor.ActivatedAt);
        Assert.Equal(("hash-1", Now, Now.AddHours(72), reserved),
            (distributor.Invitation.TokenHash, distributor.Invitation.SentAt, distributor.Invitation.ExpiresAt, distributor.Invitation.ReservedTenantId));
        Assert.Null(distributor.Invitation.AcceptedAt);
        Assert.Null(distributor.Invitation.AcceptedByUserId);
        Assert.Equal(InvitationViewState.Pending, distributor.InvitationState(Now));
    }

    [Fact]
    public void TheInvitationTimeToLiveMatchesTheMembershipOne() =>
        Assert.Equal(Membership.DefaultInvitationTimeToLive, Distributor.InvitationTimeToLive);

    // Review Focus 4: reenviar rota el token y la vigencia sobre la misma fila; el tenant reservado no cambia.
    [Fact]
    public void ResendRotatesTheTokenAndRenewsTheExpiry()
    {
        var distributor = Invite();
        var reserved = distributor.Invitation.ReservedTenantId;
        var later = Now.AddHours(100);

        distributor.ResendInvitation("hash-2", later);

        Assert.Equal(("hash-2", later, later.AddHours(72), reserved),
            (distributor.Invitation.TokenHash, distributor.Invitation.SentAt, distributor.Invitation.ExpiresAt, distributor.Invitation.ReservedTenantId));
        Assert.Equal((2L, later), (distributor.Version, distributor.UpdatedAt));
        Assert.Equal(InvitationViewState.Pending, distributor.InvitationState(later));
    }

    [Fact]
    public void ResendOutsideInvitedThrowsInvalidTransition()
    {
        var distributor = Active();

        var error = Assert.Throws<WholesaleDomainException>(() => distributor.ResendInvitation("hash-2", Now.AddHours(3)));

        Assert.Equal(WholesaleErrorCodes.DistributorInvalidTransition, error.Code);
    }

    [Fact]
    public void InvitationStateIsExpiredAfterTheWindowAndAcceptedAfterAccept()
    {
        var distributor = Invite();

        Assert.Equal(InvitationViewState.Pending, distributor.InvitationState(Now.AddHours(72)));
        Assert.Equal(InvitationViewState.Expired, distributor.InvitationState(Now.AddHours(72).AddTicks(1)));

        distributor.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));

        Assert.Equal(InvitationViewState.Accepted, distributor.InvitationState(Now.AddDays(30)));
    }

    [Fact]
    public void AcceptActivatesAndRecordsTenantCustomerAndUser()
    {
        var distributor = Invite();
        var user = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();
        var at = Now.AddHours(5);

        distributor.Accept(user, tenant, customer, at);

        Assert.Equal((DistributorStatus.Active, tenant, customer, at, 2L, at),
            (distributor.Status, distributor.DistributorTenantId, distributor.CustomerId, distributor.ActivatedAt, distributor.Version, distributor.UpdatedAt));
        Assert.Equal((at, user), (distributor.Invitation.AcceptedAt, distributor.Invitation.AcceptedByUserId));
    }

    [Fact]
    public void AcceptAfterExpiryThrowsExpiredAndTwiceThrowsAlreadyAccepted()
    {
        var expired = Invite();
        var expiredError = Assert.Throws<WholesaleDomainException>(() =>
            expired.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(73)));
        Assert.Equal(WholesaleErrorCodes.InvitationExpired, expiredError.Code);
        Assert.Equal((DistributorStatus.Invited, 1L), (expired.Status, expired.Version));

        var accepted = Active();
        var twiceError = Assert.Throws<WholesaleDomainException>(() =>
            accepted.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(2)));
        Assert.Equal(WholesaleErrorCodes.InvitationAlreadyAccepted, twiceError.Code);
    }

    // Review Focus 3 (dominio): el correo de la sesión casa sin importar mayúsculas ni espacios alrededor.
    [Fact]
    public void EmailMatchIgnoresCaseAndSurroundingSpaces()
    {
        var distributor = Invite("ana@distri.co");

        Assert.True(distributor.InvitedEmailMatches(" Ana@Distri.CO"));
        Assert.True(distributor.InvitedEmailMatches("ana@distri.co "));
        Assert.False(distributor.InvitedEmailMatches("otra@distri.co"));
        Assert.False(distributor.InvitedEmailMatches(null));
        Assert.False(distributor.InvitedEmailMatches("  "));
    }

    [Fact]
    public void ChangeTermsWorksInAnyStateAndBumpsTheVersion()
    {
        var distributor = Blocked();
        var version = distributor.Version;
        var priceList = PriceListId.New();
        var advisor = Guid.CreateVersion7();
        var company = Guid.CreateVersion7();
        var at = Now.AddHours(9);

        distributor.ChangeTerms(priceList, advisor, company, at);

        Assert.Equal((priceList, advisor, company, version + 1, at),
            (distributor.PriceListId, distributor.AdvisorMemberId, distributor.BillingCompanyId, distributor.Version, distributor.UpdatedAt));
    }

    [Fact]
    public void BlockAndUnblockMoveBetweenActiveAndBlocked()
    {
        var distributor = Active();

        distributor.Block(Now.AddHours(2));
        Assert.Equal((DistributorStatus.Blocked, 3L), (distributor.Status, distributor.Version));

        distributor.Unblock(Now.AddHours(3));
        Assert.Equal((DistributorStatus.Active, 4L, Now.AddHours(3)), (distributor.Status, distributor.Version, distributor.UpdatedAt));
    }

    public static TheoryData<string, string> InvalidBlockTransitions => new()
    {
        { "Invited", "block" },
        { "Blocked", "block" },
        { "Invited", "unblock" },
        { "Active", "unblock" },
    };

    [Theory]
    [MemberData(nameof(InvalidBlockTransitions))]
    public void BlockOnlyFromActiveAndUnblockOnlyFromBlocked(string state, string action)
    {
        var distributor = state switch
        {
            "Invited" => Invite(),
            "Active" => Active(),
            _ => Blocked(),
        };
        var version = distributor.Version;

        var error = Assert.Throws<WholesaleDomainException>(() =>
        {
            if (action == "block")
            {
                distributor.Block(Now.AddHours(5));
            }
            else
            {
                distributor.Unblock(Now.AddHours(5));
            }
        });

        Assert.Equal(WholesaleErrorCodes.DistributorInvalidTransition, error.Code);
        Assert.Equal(version, distributor.Version);
    }

    [Fact]
    public void DeleteOnlyWhileInvited()
    {
        Invite().EnsureDeletable();

        foreach (var distributor in new[] { Active(), Blocked() })
        {
            var error = Assert.Throws<WholesaleDomainException>(distributor.EnsureDeletable);
            Assert.Equal(WholesaleErrorCodes.DistributorInvalidTransition, error.Code);
        }
    }

    [Fact]
    public void AllowedActionsFollowTheState()
    {
        Assert.Equal([DistributorAction.Edit, DistributorAction.Resend, DistributorAction.Delete], Invite().AllowedActions());
        Assert.Equal([DistributorAction.Edit, DistributorAction.Block], Active().AllowedActions());
        Assert.Equal([DistributorAction.Edit, DistributorAction.Unblock], Blocked().AllowedActions());
    }

    // Una invitación vencida se puede reenviar (renueva la vigencia) y borrar: el vencimiento es perezoso, no un estado.
    [Fact]
    public void AnExpiredInvitationIsStillInvitedForResendAndDelete()
    {
        var distributor = Invite();

        Assert.Equal(InvitationViewState.Expired, distributor.InvitationState(Now.AddDays(10)));
        Assert.Equal(DistributorStatus.Invited, distributor.Status);
        Assert.Equal([DistributorAction.Edit, DistributorAction.Resend, DistributorAction.Delete], distributor.AllowedActions());
    }
}
