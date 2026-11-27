using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): la relación con un distribuidor y su invitación (D-1, D-14).</summary>
public sealed class DistributorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    // La forma de InvitationTokens.HashOf: SHA-256 en hex minúsculo.
    private const string Hash1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash2 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    // Los hashes de la prueba de buzón: misma forma, valores distintos a los del token.
    private const string Proof1 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Proof2 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    // Ruling W11 (c): la última red sobre el campo más sensible de la invitación. Un token plano (base64url, 43 caracteres),
    // un hash en mayúsculas, uno cortado o uno que no es hex no se guardan nunca.
    [Theory]
    [InlineData("Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InviteRejectsATokenHashThatIsNotALowercaseSha256(string tokenHash)
    {
        Assert.Throws<ArgumentException>(() => Invite(tokenHash: tokenHash));
    }

    [Fact]
    public void ResendRejectsATokenHashThatIsNotALowercaseSha256()
    {
        var distributor = Invite();

        Assert.Throws<ArgumentException>(() =>
            distributor.ResendInvitation("Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q", noticeQueued: false, Now.AddHours(1)));
        Assert.Equal(Hash1, distributor.Invitation.TokenHash);
        Assert.Equal(1, distributor.Version);
    }

    // Ciudad y dirección del distribuidor: el dominio sólo recorta y convierte vacío en null.
    [Fact]
    public void InviteKeepsCityAndAddressTrimmed()
    {
        var cityId = Guid.CreateVersion7();

        var distributor = InviteAt("CO", cityId, "  Madrid ", " Calle 1 ");

        Assert.Equal((cityId, "Madrid", "Calle 1"), (distributor.CityId, distributor.CityName, distributor.Address));
    }

    [Fact]
    public void InviteTurnsBlankCityNameAndAddressIntoNull()
    {
        var distributor = InviteAt("CO", null, "   ", "");

        Assert.Null(distributor.CityName);
        Assert.Null(distributor.Address);
    }

    [Fact]
    public void InviteTreatsAnEmptyCityIdAsNull()
    {
        var distributor = InviteAt("CO", Guid.Empty, null, null);

        Assert.Null(distributor.CityId);
    }

    [Theory]
    [InlineData("CO", true, null, true)]
    [InlineData("CO", false, "Medellín", false)]
    [InlineData("ES", false, "Madrid", true)]
    [InlineData("ES", false, null, false)]
    public void HasCompanyCityFollowsTheCountry(string country, bool withCityId, string? cityName, bool expected)
    {
        var distributor = InviteAt(country, withCityId ? Guid.CreateVersion7() : null, cityName, null);

        Assert.Equal(expected, distributor.HasCompanyCity);
    }

    [Theory]
    [InlineData("NIT")]
    [InlineData("CC")]
    [InlineData("CE")]
    public void InviteRejectsLettersInANumericNumber(string type)
    {
        var exception = Assert.Throws<WholesaleDomainException>(() => Distributor.Invite(
            Tenant, "ana@distri.co", "Distribuciones Norte SAS", "CO", type, "9001234A5", "Ana Pérez", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash1, Guid.CreateVersion7(), null, null, Now));

        Assert.Equal("wholesale.distributor.identification_number_not_numeric", exception.Code);
    }

    [Fact]
    public void ChangeProfileRejectsLettersInANumericNumber()
    {
        var distributor = Invite();

        var exception = Assert.Throws<WholesaleDomainException>(
            () => distributor.ChangeProfile("ana@distri.co", "Norte", "CO", "NIT", "ABC123", "Ana", null, null, null));

        Assert.Equal("wholesale.distributor.identification_number_not_numeric", exception.Code);
    }

    [Fact]
    public void APassportWithLettersIsAccepted()
    {
        var distributor = Distributor.Invite(
            Tenant, "ana@distri.co", "Distribuciones Norte SAS", "CO", "PASAPORTE", "AB123456", "Ana Pérez", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash1, Guid.CreateVersion7(), null, null, Now);

        Assert.Equal("AB123456", distributor.IdentificationNumber);
    }

    private static Distributor InviteAt(string country, Guid? cityId, string? cityName, string? address) =>
        Distributor.Invite(
            Tenant, "ana@distri.co", "Distribuciones Norte SAS", country, "NIT", "900123456", "Ana Pérez", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash1, Guid.CreateVersion7(), null, null, Now,
            cityId, cityName, address);

    private const string Phone = "+573001234567";
    private static readonly Guid Connection = Guid.CreateVersion7();

    private static Distributor Invite(
        string email = "ana@distri.co", string tokenHash = Hash1, string? phone = null, Guid? connection = null,
        string? emailProofHash = null) =>
        Distributor.Invite(
            Tenant, email, "  Distribuciones Norte SAS ", "CO", "NIT", " 900123456 ", " Ana Pérez ", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), tokenHash, Guid.CreateVersion7(), phone,
            connection, Now, emailProofHash: emailProofHash);

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
            Tenant, " Ana@Distri.CO ", "  Distribuciones Norte SAS ", " co ", "nit", " 900123456 ", " Ana Pérez ", priceList, advisor,
            company, invitedBy, Hash1, reserved, null, null, Now);

        Assert.NotEqual(Guid.Empty, distributor.Id.Value);
        Assert.Equal((Tenant, "ana@distri.co", "Distribuciones Norte SAS", "900123456", "Ana Pérez"),
            (distributor.TenantId, distributor.Email, distributor.BusinessName, distributor.IdentificationNumber, distributor.ContactName));
        Assert.Equal(("CO", "NIT"), (distributor.Country, distributor.IdentificationType));
        Assert.Equal((priceList, advisor, company, invitedBy),
            (distributor.PriceListId, distributor.AdvisorMemberId, distributor.BillingCompanyId, distributor.InvitedByUserId));
        Assert.Equal((DistributorStatus.Invited, 1L, Now, Now), (distributor.Status, distributor.Version, distributor.CreatedAt, distributor.UpdatedAt));
        Assert.Null(distributor.DistributorTenantId);
        Assert.Null(distributor.CustomerId);
        Assert.Null(distributor.ActivatedAt);
        Assert.Equal((Hash1, Now, Now.AddHours(72), reserved),
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

        distributor.ResendInvitation(Hash2, noticeQueued: false, later);

        Assert.Equal((Hash2, later, later.AddHours(72), reserved),
            (distributor.Invitation.TokenHash, distributor.Invitation.SentAt, distributor.Invitation.ExpiresAt, distributor.Invitation.ReservedTenantId));
        Assert.Equal((2L, later), (distributor.Version, distributor.UpdatedAt));
        Assert.Equal(InvitationViewState.Pending, distributor.InvitationState(later));
    }

    [Fact]
    public void ResendOutsideInvitedThrowsInvalidTransition()
    {
        var distributor = Active();

        var error = Assert.Throws<WholesaleDomainException>(() => distributor.ResendInvitation(Hash2, noticeQueued: true, Now.AddHours(3)));

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

    // Spec 2026-11-18 (aviso por WhatsApp): con teléfono el aviso nace pendiente en la generación 0; sin él no hay aviso.
    [Fact]
    public void InviteWithAPhoneStartsTheNoticePendingAtGenerationZero()
    {
        var distributor = Invite(phone: Phone, connection: Connection);

        Assert.Equal((Phone, (Guid?)Connection), (distributor.Phone, distributor.WhatsAppConnectionId));
        Assert.Equal((0, (InvitationNoticeStatus?)InvitationNoticeStatus.Pending, (string?)null),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));
    }

    [Fact]
    public void InviteWithoutAPhoneHasNoNotice()
    {
        var distributor = Invite(connection: Connection);

        Assert.Null(distributor.Phone);
        Assert.Null(distributor.WhatsAppConnectionId);
        Assert.Equal(0, distributor.Invitation.Generation);
        Assert.Null(distributor.Invitation.NoticeStatus);
        Assert.Null(distributor.Invitation.NoticeFailureCode);
    }

    [Fact]
    public void ResendBumpsTheGenerationAndQueuesOrFailsTheNotice()
    {
        var distributor = Invite(phone: Phone, connection: Connection);

        distributor.ResendInvitation(Hash2, noticeQueued: false, Now.AddHours(1));

        Assert.Equal((1, (InvitationNoticeStatus?)InvitationNoticeStatus.Failed, (string?)"whatsapp_unavailable"),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));

        distributor.ResendInvitation(Hash1, noticeQueued: true, Now.AddHours(2));

        Assert.Equal((2, (InvitationNoticeStatus?)InvitationNoticeStatus.Pending, (string?)null),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));

        var withoutPhone = Invite();
        withoutPhone.ResendInvitation(Hash2, noticeQueued: true, Now.AddHours(1));

        Assert.Equal(1, withoutPhone.Invitation.Generation);
        Assert.Null(withoutPhone.Invitation.NoticeStatus);
    }

    // P-3: cambiar teléfono o conexión deja el aviso en null hasta el próximo «Reenviar»; sin cambio no pasa nada.
    [Fact]
    public void ChangeNoticeTargetBumpsTheGenerationOnlyWhenSomethingChanges()
    {
        var distributor = Invite(phone: Phone, connection: Connection);
        var version = distributor.Version;

        Assert.False(distributor.ChangeNoticeTarget(Phone, Connection));
        Assert.Equal((0, (InvitationNoticeStatus?)InvitationNoticeStatus.Pending),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus));

        Assert.True(distributor.ChangeNoticeTarget("+573009876543", Connection));
        Assert.Equal(("+573009876543", 1), (distributor.Phone, distributor.Invitation.Generation));
        Assert.Null(distributor.Invitation.NoticeStatus);

        var otherConnection = Guid.CreateVersion7();
        Assert.True(distributor.ChangeNoticeTarget("+573009876543", otherConnection));
        Assert.Equal(((Guid?)otherConnection, 2), (distributor.WhatsAppConnectionId, distributor.Invitation.Generation));

        // La versión la sube ChangeTerms en el mismo PUT, no este método.
        Assert.Equal(version, distributor.Version);
    }

    [Fact]
    public void ChangeNoticeTargetWithANullPhoneClearsPhoneAndConnection()
    {
        var distributor = Invite(phone: Phone, connection: Connection);

        Assert.True(distributor.ChangeNoticeTarget(null, Guid.CreateVersion7()));

        Assert.Null(distributor.Phone);
        Assert.Null(distributor.WhatsAppConnectionId);
        Assert.Null(distributor.Invitation.NoticeStatus);
        Assert.Equal(1, distributor.Invitation.Generation);
        Assert.False(distributor.ChangeNoticeTarget(null, null));
    }

    // P-2: fuera de Invited el teléfono y la conexión se ignoran, sin error.
    [Fact]
    public void ChangeNoticeTargetOutsideInvitedIsIgnored()
    {
        var distributor = Invite(phone: Phone, connection: Connection);
        distributor.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));
        var generation = distributor.Invitation.Generation;

        Assert.False(distributor.ChangeNoticeTarget("+573009876543", Guid.CreateVersion7()));
        Assert.False(distributor.ChangeNoticeTarget(null, null));

        Assert.Equal((Phone, (Guid?)Connection, generation),
            (distributor.Phone, distributor.WhatsAppConnectionId, distributor.Invitation.Generation));
    }

    // Review Focus 2 (D-4): un resultado de una generación vieja, o sin aviso que esperar, no pisa nada.
    [Fact]
    public void AStaleGenerationResultChangesNothing()
    {
        var distributor = Invite(phone: Phone, connection: Connection);
        distributor.ResendInvitation(Hash2, noticeQueued: true, Now.AddHours(1));

        Assert.False(distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Sent, null));
        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Pending, distributor.Invitation.NoticeStatus);

        distributor.ChangeNoticeTarget("+573009876543", Connection);

        Assert.False(distributor.RecordInvitationNotice(1, InvitationNoticeStatus.Failed, "send_failed:x"));
        Assert.False(distributor.RecordInvitationNotice(2, InvitationNoticeStatus.Sent, null));
        Assert.Null(distributor.Invitation.NoticeStatus);
        Assert.Null(distributor.Invitation.NoticeFailureCode);
    }

    // Review Focus 3 (D-3): registrar el resultado no sube Version ni UpdatedAt; el If-Match leído antes sigue valiendo.
    [Fact]
    public void RecordingTheNoticeKeepsVersionAndUpdatedAt()
    {
        var distributor = Invite(phone: Phone, connection: Connection);
        var (version, updatedAt) = (distributor.Version, distributor.UpdatedAt);

        Assert.True(distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Failed, "connection_unavailable:paused"));

        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Failed, distributor.Invitation.NoticeStatus);
        Assert.Equal("connection_unavailable:paused", distributor.Invitation.NoticeFailureCode);
        Assert.Equal((version, updatedAt), (distributor.Version, distributor.UpdatedAt));

        Assert.True(distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Sent, null));

        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Sent, distributor.Invitation.NoticeStatus);
        Assert.Null(distributor.Invitation.NoticeFailureCode);
        Assert.Equal((version, updatedAt), (distributor.Version, distributor.UpdatedAt));
    }

    // Spec 2026-11-19 §6: el correo que confirma el distribuidor reemplaza al del mayorista, normalizado igual que al invitar.
    // El mismo correo con otras mayúsculas o espacios no es un cambio: no escribe nada.
    [Fact]
    public void ReplaceEmailNormalizesAndReportsTheChange()
    {
        var distributor = Invite();

        var same = distributor.ReplaceEmail("  ANA@Distri.co ", Now.AddHours(1));

        Assert.False(same);
        Assert.Equal(("ana@distri.co", 1L, Now), (distributor.Email, distributor.Version, distributor.UpdatedAt));

        var changed = distributor.ReplaceEmail(" Compras@Norte.CO ", Now.AddHours(2));

        Assert.True(changed);
        Assert.Equal(("compras@norte.co", 2L, Now.AddHours(2)), (distributor.Email, distributor.Version, distributor.UpdatedAt));
    }

    [Fact]
    public void ReplaceEmailOnAnAcceptedDistributorIsRejected()
    {
        var active = Active();
        var blocked = Blocked();

        var error = Assert.Throws<WholesaleDomainException>(() => active.ReplaceEmail("otra@distri.co", Now.AddHours(3)));
        Assert.Throws<WholesaleDomainException>(() => blocked.ReplaceEmail("otra@distri.co", Now.AddHours(3)));

        Assert.Equal("wholesale.distributor.invalid_transition", error.Code);
        Assert.Equal(("ana@distri.co", "ana@distri.co"), (active.Email, blocked.Email));
    }

    // Spec 2026-11-19 §1: «Copiar» rota token y vigencia (72 h desde ahora), aunque la invitación ya haya vencido, y no toca
    // el aviso por WhatsApp: no reenvía nada.
    [Fact]
    public void RotateInvitationLinkChangesTokenAndExpiryAndKeepsTheNotice()
    {
        var distributor = Invite(phone: Phone, connection: Connection);
        distributor.RecordInvitationNotice(0, InvitationNoticeStatus.Sent, null);
        var later = Now.AddDays(5);

        distributor.RotateInvitationLink(Hash2, later);

        Assert.Equal((Hash2, later, later.AddHours(72)),
            (distributor.Invitation.TokenHash, distributor.Invitation.SentAt, distributor.Invitation.ExpiresAt));
        Assert.Equal((InvitationViewState.Pending, 2L, later),
            (distributor.InvitationState(later), distributor.Version, distributor.UpdatedAt));
        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Sent, distributor.Invitation.NoticeStatus);
        Assert.Equal((Phone, (Guid?)Connection), (distributor.Phone, distributor.WhatsAppConnectionId));
    }

    [Fact]
    public void RotateInvitationLinkOnlyWhileInvitedAndOnlyWithAHash()
    {
        var active = Active();
        var invited = Invite();

        var error = Assert.Throws<WholesaleDomainException>(() => active.RotateInvitationLink(Hash2, Now.AddHours(2)));
        Assert.Throws<ArgumentException>(() =>
            invited.RotateInvitationLink("Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q", Now.AddHours(2)));

        Assert.Equal("wholesale.distributor.invalid_transition", error.Code);
        Assert.Equal((Hash1, Hash1), (active.Invitation.TokenHash, invited.Invitation.TokenHash));
    }

    // Spec 2026-11-24 (prueba de buzón): el link del correo lleva un secreto propio; acá sólo vive su hash.
    [Fact]
    public void InviteKeepsTheEmailProofHash()
    {
        Assert.Equal(Proof1, Invite(emailProofHash: Proof1).Invitation.EmailProofHash);
        Assert.Null(Invite().Invitation.EmailProofHash);
    }

    // La misma red que la del token: la prueba plana nunca llega a la base aunque quien llama se equivoque de variable.
    [Theory]
    [InlineData("Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InviteAndResendRejectAnEmailProofHashThatIsNotALowercaseSha256(string emailProofHash)
    {
        var onInvite = Assert.Throws<ArgumentException>(() => Invite(emailProofHash: emailProofHash));
        var distributor = Invite(emailProofHash: Proof1);
        var onResend = Assert.Throws<ArgumentException>(() =>
            distributor.ResendInvitation(Hash2, noticeQueued: false, Now.AddHours(1), emailProofHash));

        Assert.Equal(("emailProofHash", "emailProofHash"), (onInvite.ParamName, onResend.ParamName));
        Assert.Equal((Hash1, Proof1, 1L),
            (distributor.Invitation.TokenHash, distributor.Invitation.EmailProofHash, distributor.Version));
    }

    [Fact]
    public void ResendReplacesTheEmailProof()
    {
        var distributor = Invite(emailProofHash: Proof1);

        distributor.ResendInvitation(Hash2, noticeQueued: false, Now.AddHours(1), Proof2);

        Assert.Equal(Proof2, distributor.Invitation.EmailProofHash);
    }

    // «Copiar» rota el token y no manda correo: la prueba que llegó al buzón deja de corresponder a este link.
    [Fact]
    public void RotateInvitationLinkVoidsTheEmailProof()
    {
        var distributor = Invite(emailProofHash: Proof1);

        distributor.RotateInvitationLink(Hash2, Now.AddHours(1));

        Assert.Null(distributor.Invitation.EmailProofHash);
    }

    // Sin esto, quien tiene el correo viejo podría crear la cuenta del nuevo. Otras mayúsculas o espacios no son un cambio.
    [Fact]
    public void ChangeProfileVoidsTheEmailProofOnlyWhenTheEmailChanges()
    {
        var distributor = Invite(emailProofHash: Proof1);

        distributor.ChangeProfile(" Ana@Distri.CO ", "Norte", "CO", "NIT", "900123456", "Ana", null, null, null);

        Assert.Equal(Proof1, distributor.Invitation.EmailProofHash);

        distributor.ChangeProfile("otra@distri.co", "Norte", "CO", "NIT", "900123456", "Ana", null, null, null);

        Assert.Null(distributor.Invitation.EmailProofHash);
        Assert.Equal(Hash1, distributor.Invitation.TokenHash);
    }

    [Fact]
    public void ProvesInvitedMailboxNeedsTheStoredHashAndTheInvitedEmail()
    {
        var distributor = Invite(emailProofHash: Proof1);

        Assert.True(distributor.ProvesInvitedMailbox(Proof1, "ana@distri.co"));
        Assert.False(distributor.ProvesInvitedMailbox(Hash2, "ana@distri.co"));
        Assert.False(distributor.ProvesInvitedMailbox(Proof1, "otra@distri.co"));
        Assert.False(distributor.ProvesInvitedMailbox(Proof1, null));
        Assert.False(distributor.ProvesInvitedMailbox(Proof1, ""));
        Assert.False(Invite().ProvesInvitedMailbox(Proof1, "ana@distri.co"));
        Assert.False(Active().ProvesInvitedMailbox(Proof1, "ana@distri.co"));

        // Con la prueba guardada, aceptada ya no prueba nada: lo que la anula acá es el estado, no la falta del hash.
        distributor.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));

        Assert.False(distributor.ProvesInvitedMailbox(Proof1, "ana@distri.co"));
    }

    [Fact]
    public void ProvesInvitedMailboxIgnoresCaseAndSpacesOfTheEmail() =>
        Assert.True(Invite(emailProofHash: Proof1).ProvesInvitedMailbox(Proof1, " Ana@Distri.CO "));

    // El correo se normaliza como lo guardan Distributor e Identity (recortar y pasar a minúsculas), no comparando sin
    // mayúsculas: una dirección que Identity trataría como otra cuenta no queda probada. Los casos son la i sin punto, la s
    // larga y la sigma final.
    [Theory]
    [InlineData("mike@x.com", "mıke@x.com")]
    [InlineData("sam@x.com", "ſam@x.com")]
    [InlineData("nikosσ@x.com", "nikosς@x.com")]
    public void ProvesInvitedMailboxRejectsALookalikeThatIdentityKeepsApart(string invited, string typed) =>
        Assert.False(Invite(email: invited, emailProofHash: Proof1).ProvesInvitedMailbox(Proof1, typed));

    [Fact]
    public void ProvesInvitedMailboxAcceptsTheInvitedEmailInUpperCase() =>
        Assert.True(Invite(email: "mike@x.com", emailProofHash: Proof1).ProvesInvitedMailbox(Proof1, " MIKE@X.COM "));

    // El reemplazo del correo al aceptar anula la prueba igual que la edición: la prueba es del buzón anterior.
    [Fact]
    public void ReplaceEmailVoidsTheEmailProofOnlyWhenTheEmailChanges()
    {
        var distributor = Invite(emailProofHash: Proof1);

        distributor.ReplaceEmail(" Ana@Distri.CO ", Now.AddHours(1));

        Assert.Equal(Proof1, distributor.Invitation.EmailProofHash);

        distributor.ReplaceEmail("otra@distri.co", Now.AddHours(2));

        Assert.Null(distributor.Invitation.EmailProofHash);
        Assert.Equal(Hash1, distributor.Invitation.TokenHash);
    }

    // Decisión del owner 2026-11-24: los datos de la invitación se editan mientras está invitado. Recorta como Invite y, igual
    // que ChangeNoticeTarget, no sube Version: lo hace ChangeTerms, que corre en el mismo PUT.
    [Fact]
    public void ChangeProfileWhileInvitedReplacesAndTrimsEveryDataField()
    {
        var distributor = Invite();
        var cityId = Guid.CreateVersion7();

        var applied = distributor.ChangeProfile(
            " Compras@Norte.co ", "  Norte Nuevo SAS ", "co", "nit", " 800999888 ", " Luis Gómez ", cityId, null, " Calle 5 # 1-2 ");

        Assert.True(applied);
        Assert.Equal(
            ("compras@norte.co", "Norte Nuevo SAS", "CO", "NIT", "800999888", "Luis Gómez", (Guid?)cityId, (string?)null, "Calle 5 # 1-2"),
            (distributor.Email, distributor.BusinessName, distributor.Country, distributor.IdentificationType,
                distributor.IdentificationNumber, distributor.ContactName, distributor.CityId, distributor.CityName, distributor.Address));
        Assert.Equal(1L, distributor.Version);
    }

    [Fact]
    public void ChangeProfileTurnsBlankCityNameAndAddressIntoNullAndAnEmptyCityIdIntoNull()
    {
        var distributor = InviteAt("CO", Guid.CreateVersion7(), "Medellín", "Calle 1");

        distributor.ChangeProfile("ana@distri.co", "Norte", "CO", "NIT", "900123456", "Ana", Guid.Empty, "  ", "");

        Assert.Equal(((Guid?)null, (string?)null, (string?)null), (distributor.CityId, distributor.CityName, distributor.Address));
        Assert.False(distributor.HasCompanyCity);
    }

    [Fact]
    public void ChangeProfileMovingToAnotherCountryKeepsTheCompanyCityRuleOfThatCountry()
    {
        var distributor = InviteAt("CO", Guid.CreateVersion7(), null, null);

        distributor.ChangeProfile("ana@distri.co", "Norte", "ES", "NIF", "X1234567L", "Ana", null, "Madrid", null);

        Assert.Equal(("ES", "NIF", (Guid?)null, "Madrid", true),
            (distributor.Country, distributor.IdentificationType, distributor.CityId, distributor.CityName, distributor.HasCompanyCity));
    }

    [Fact]
    public void ChangeProfileRejectsAnIdentificationTypeThatTheCountryDoesNotAdmit()
    {
        var distributor = Invite();

        Assert.Throws<ArgumentException>(() =>
            distributor.ChangeProfile("ana@distri.co", "Norte", "CO", "NIF", "900123456", "Ana", null, null, null));
        Assert.Equal(("CO", "NIT"), (distributor.Country, distributor.IdentificationType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeProfileRejectsBlankRequiredTexts(string blank)
    {
        var distributor = Invite();

        Assert.Throws<ArgumentException>(() =>
            distributor.ChangeProfile("ana@distri.co", blank, "CO", "NIT", "900123456", "Ana", null, null, null));
        Assert.Throws<ArgumentException>(() =>
            distributor.ChangeProfile(blank, "Norte", "CO", "NIT", "900123456", "Ana", null, null, null));
    }

    // La marca de «empezó a aceptar» sube la versión una sola vez: un PUT con la anterior queda viejo.
    [Fact]
    public void StartAcceptanceMarksOnceAndBumpsTheVersion()
    {
        var distributor = Invite();

        var first = distributor.StartAcceptance(Now.AddHours(1));
        var again = distributor.StartAcceptance(Now.AddHours(2));

        Assert.True(first);
        Assert.False(again);
        Assert.Equal(
            ((DateTimeOffset?)Now.AddHours(1), Now.AddHours(1), 2L, DistributorStatus.Invited),
            (distributor.AcceptanceStartedAt, distributor.UpdatedAt, distributor.Version, distributor.Status));
        Assert.Null(distributor.Invitation.AcceptedAt);
    }

    [Fact]
    public void StartAcceptanceOutsideInvitedIsAnInvalidTransition()
    {
        var active = Active();

        var error = Assert.Throws<WholesaleDomainException>(() => active.StartAcceptance(Now.AddHours(2)));

        Assert.Equal("wholesale.distributor.invalid_transition", error.Code);
        Assert.Null(active.AcceptanceStartedAt);
    }

    [Fact]
    public void ChangeProfileOutsideInvitedDoesNothingAndReturnsFalse()
    {
        var active = Active();
        var blocked = Blocked();

        var changedActive = active.ChangeProfile("otra@distri.co", "Otra SAS", "CO", "NIT", "1", "Otro", null, null, "X");
        var changedBlocked = blocked.ChangeProfile("otra@distri.co", "Otra SAS", "CO", "NIT", "1", "Otro", null, null, "X");

        Assert.False(changedActive);
        Assert.False(changedBlocked);
        Assert.Equal(("ana@distri.co", "Distribuciones Norte SAS", "900123456", (string?)null),
            (active.Email, active.BusinessName, active.IdentificationNumber, active.Address));
        Assert.Equal(("ana@distri.co", "Distribuciones Norte SAS"), (blocked.Email, blocked.BusinessName));
    }
}
