using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): invitar, editar, reenviar, bloquear y borrar distribuidores.</summary>
public sealed class DistributorHandlersTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly DistributorWorld _world = new();

    private static CancellationToken Ct => DistributorWorld.Ct;

    [Fact]
    public async Task InviteStoresOnlyTheHashAndPublishesTheTokenOnce()
    {
        var detail = await _world.InviteHandler().HandleAsync(_world.InviteCommand(email: "  Ana@Distri.CO "), Ct);

        var published = Assert.Single(_world.Events.Invitations);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Equal(InvitationTokens.HashOf(published.Token), stored.Invitation.TokenHash);
        Assert.NotEqual(published.Token, stored.Invitation.TokenHash);
        Assert.Equal(
            (_world.Tenant, stored.Id.Value, "ana@distri.co", "Distribuciones Norte SAS", DistributorWorld.Now.AddHours(72), "trace-1"),
            (published.TenantId, published.DistributorId, published.Email, published.BusinessName, published.ExpiresAt, published.CorrelationId));
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal(("wholesale.distributor.invited", "distributor", stored.Id.ToString(), _world.Context.SubjectId),
            (audit.Action, audit.ResourceType, audit.ResourceId, audit.ActorId));
        Assert.DoesNotContain(audit.ChangedFields, field => field.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(published.Token, string.Join("|", audit.ChangedFields.Append(audit.ResourceId)));
        Assert.Equal(_world.Context.SubjectId, stored.InvitedByUserId);
        Assert.Equal(("Invited", 1L), (detail.Status, detail.Version));
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    // Spec 2026-11-24: la prueba del buzón es un secreto aparte del token; en la base queda su hash y sólo viaja al correo.
    [Fact]
    public async Task InviteStoresOnlyTheEmailProofHashAndPublishesTheProof()
    {
        var detail = await _world.InviteHandler().HandleAsync(_world.InviteCommand(), Ct);

        var published = Assert.Single(_world.Events.Invitations);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Equal(InvitationTokens.HashOf(published.EmailProof), stored.Invitation.EmailProofHash);
        Assert.NotEqual(published.Token, published.EmailProof);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.DoesNotContain(
            published.EmailProof, string.Join("|", audit.ChangedFields.Append(audit.ResourceId)), StringComparison.Ordinal);
        Assert.NotNull(detail.InvitationUrl);
        Assert.DoesNotContain(published.EmailProof, detail.InvitationUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("ev=", detail.InvitationUrl, StringComparison.Ordinal);
    }

    // Review Focus 5: una ciudad que no existe se rechaza al invitar, no cuando el distribuidor intenta aceptar.
    [Fact]
    public async Task InviteWithAnUnknownCityIsCityNotFound()
    {
        var command = _world.InviteCommand() with { CityId = Guid.CreateVersion7() };

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.InviteHandler().HandleAsync(command, Ct));

        Assert.Equal("wholesale.distributor.city_not_found", error.Code);
        Assert.Empty(_world.Distributors.Distributors);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task InviteSavesCityAndAddress()
    {
        await _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Address = " Calle 10 # 40-20 " }, Ct);

        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Equal((_world.MedellinId, "Calle 10 # 40-20"), (stored.CityId, stored.Address));
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Contains("cityId", audit.ChangedFields);
        Assert.Contains("cityName", audit.ChangedFields);
        Assert.Contains("address", audit.ChangedFields);
    }

    [Fact]
    public async Task InviteOutsideColombiaDoesNotLookUpTheCity()
    {
        await _world.InviteHandler().HandleAsync(_world.InviteCommand(country: "ES", identificationType: "NIF"), Ct);

        Assert.Empty(_world.Cities.Calls);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Equal((null, "Madrid"), (stored.CityId, stored.CityName));
    }

    [Fact]
    public async Task InviteWithAnUnknownPriceListIs404()
    {
        var command = _world.InviteCommand() with { PriceListId = Guid.CreateVersion7() };

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _world.InviteHandler().HandleAsync(command, Ct));

        Assert.Equal("wholesale.price_list.not_found", error.Code);
        Assert.Empty(_world.Events.Invitations);
    }

    [Fact]
    public async Task InviteWithAnInactiveAdvisorIs422AdvisorInvalid()
    {
        var gone = Guid.CreateVersion7();
        _world.Members.Names[gone] = "Pedro Ido"; // tiene nombre, pero ya no es membresía activa
        var command = _world.InviteCommand() with { AdvisorId = gone };

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.InviteHandler().HandleAsync(command, Ct));

        Assert.Equal("wholesale.distributor.advisor_invalid", error.Code);
        Assert.Empty(_world.Distributors.Distributors);
        Assert.Empty(_world.Events.Invitations);
    }

    [Theory]
    [InlineData("NIF", true)]
    [InlineData("NIT", false)]
    public async Task InviteWithANifCompanyIs422CompanyInvalid(string documentType, bool isActive)
    {
        // Un documento que el proveedor no admite (Eligible false) o una empresa inactiva.
        var company = _world.Companies.Add("Otra", documentType, isActive);
        var command = _world.InviteCommand() with { BillingCompanyId = company.Id };

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.InviteHandler().HandleAsync(command, Ct));

        Assert.Equal("wholesale.distributor.company_invalid", error.Code);
        Assert.Empty(_world.Distributors.Distributors);
    }

    [Fact]
    public async Task InviteAllowsTheSameNumberWithAnotherType()
    {
        _world.SeedActive("900123456");

        await _world.InviteHandler().HandleAsync(_world.InviteCommand("900123456", identificationType: "CC"), Ct);

        Assert.Equal(["NIT", "CC"], _world.Distributors.Distributors.Select(item => item.IdentificationType));
    }

    [Fact]
    public async Task InviteWithATakenIdentificationIs409()
    {
        _world.SeedActive("900123456");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            _world.InviteHandler().HandleAsync(_world.InviteCommand(" 900123456 "), Ct));

        Assert.Equal("wholesale.distributor.identification_taken", error.Code);
        Assert.Single(_world.Distributors.Distributors);
        Assert.Empty(_world.Events.Invitations);
    }

    [Fact]
    public async Task UpdateRepeatsTheThreeChecksAndAudits()
    {
        var distributor = _world.SeedActive();
        var otherList = _world.PriceLists.Seed(_world.Tenant, "Detal", -5m);
        var otherAdvisor = _world.Members.AddActive("Luis");
        var nif = _world.Companies.Add("Extranjera", "NIF");

        var invalid = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.UpdateHandler().HandleAsync(
            new UpdateDistributorCommand(_world.Tenant, distributor.Id.Value, 2, otherList.Id.Value, otherAdvisor, nif.Id), Ct));
        var detail = await _world.UpdateHandler().HandleAsync(
            new UpdateDistributorCommand(_world.Tenant, distributor.Id.Value, 2, otherList.Id.Value, otherAdvisor, _world.Company.Id), Ct);

        Assert.Equal("wholesale.distributor.company_invalid", invalid.Code);
        Assert.Equal((otherList.Id.Value, "Luis", 3L), (detail.PriceList.Id, detail.Advisor.Name, detail.Version));
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal("wholesale.distributor.updated", audit.Action);
        Assert.Equal(["priceListId", "advisorId", "billingCompanyId"], audit.ChangedFields);
    }

    [Fact]
    public async Task UpdateWithAStaleVersionIs412()
    {
        var distributor = _world.SeedInvited();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => _world.UpdateHandler().HandleAsync(
            new UpdateDistributorCommand(_world.Tenant, distributor.Id.Value, 9, _world.PriceList.Id.Value, _world.Advisor, _world.Company.Id), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task ResendPublishesANewTokenAndAuditsInvitationResent()
    {
        var oldToken = InvitationTokens.Generate();
        var distributor = _world.SeedInvited(tokenHash: InvitationTokens.HashOf(oldToken));
        _world.Clock.UtcNow = DistributorWorld.Now.AddDays(5); // ya vencida: reenviar es como se renueva

        var detail = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, distributor.Id.Value, 1, DistributorAction.Resend, "trace-2"), Ct);

        var published = Assert.Single(_world.Events.Invitations);
        Assert.NotEqual(oldToken, published.Token);
        Assert.Equal(InvitationTokens.HashOf(published.Token), distributor.Invitation.TokenHash);
        Assert.Null(await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf(oldToken), Ct));
        Assert.Equal((DistributorWorld.Now.AddDays(5).AddHours(72), "trace-2"), (published.ExpiresAt, published.CorrelationId));
        Assert.Equal(("wholesale.distributor.invitation_resent", "distributor"),
            (Assert.Single(_world.Audit.Entries).Action, _world.Audit.Entries[0].ResourceType));
        Assert.False(detail.Invitation.Expired);
        Assert.Equal(2L, detail.Version);
    }

    // Spec 2026-11-24: reenviar pone una prueba nueva y la del correo anterior deja de probar el buzón.
    [Fact]
    public async Task ResendPublishesANewEmailProofAndTheOldOneStopsProving()
    {
        var invited = await _world.InviteHandler().HandleAsync(_world.InviteCommand(), Ct);
        var first = Assert.Single(_world.Events.Invitations).EmailProof;

        await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, invited.Id, invited.Version, DistributorAction.Resend, "trace-2"), Ct);

        var second = _world.Events.Invitations[1].EmailProof;
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.NotEqual(first, second);
        Assert.Equal(InvitationTokens.HashOf(second), stored.Invitation.EmailProofHash);
        Assert.False(DistributorEmailProofs.Proves(stored, first, stored.Email));
        Assert.True(DistributorEmailProofs.Proves(stored, second, stored.Email));
    }

    // Spec 2026-11-24: el link de «Copiar» no sale del buzón, así que rotar anula la prueba.
    [Fact]
    public async Task RotateVoidsTheEmailProof()
    {
        var distributor = _world.SeedInvited(emailProofHash: DistributorEmailProofs.Issue().Hash);

        await _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct);

        Assert.Null(distributor.Invitation.EmailProofHash);
    }

    // Spec 2026-11-24: sin esto, quien tiene el correo anterior podría crear la cuenta del nuevo.
    [Theory]
    [InlineData("otra@distri.co", false)]
    [InlineData("ANA@DISTRI.CO", true)]
    public async Task UpdateWithAnotherEmailVoidsTheEmailProof(string email, bool keepsTheProof)
    {
        var hash = DistributorEmailProofs.Issue().Hash;
        var distributor = _world.SeedInvited(cityId: _world.MedellinId, emailProofHash: hash);

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null) with { Email = email }, Ct);

        Assert.Equal(keepsTheProof ? hash : null, distributor.Invitation.EmailProofHash);
    }

    [Fact]
    public async Task BlockAndUnblockAreAuditedAndOutOfStateIs409InvalidTransition()
    {
        var active = _world.SeedActive();
        var invited = _world.SeedInvited("800999111");

        var blocked = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, active.Id.Value, 2, DistributorAction.Block, "t"), Ct);
        var unblocked = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, active.Id.Value, 3, DistributorAction.Unblock, "t"), Ct);
        var outOfState = new List<ResourceConflictException>
        {
            await Assert.ThrowsAsync<ResourceConflictException>(() => _world.TransitionHandler().HandleAsync(
                new TransitionDistributorCommand(_world.Tenant, invited.Id.Value, 1, DistributorAction.Block, "t"), Ct)),
            await Assert.ThrowsAsync<ResourceConflictException>(() => _world.TransitionHandler().HandleAsync(
                new TransitionDistributorCommand(_world.Tenant, active.Id.Value, 4, DistributorAction.Unblock, "t"), Ct)),
            await Assert.ThrowsAsync<ResourceConflictException>(() => _world.TransitionHandler().HandleAsync(
                new TransitionDistributorCommand(_world.Tenant, active.Id.Value, 4, DistributorAction.Resend, "t"), Ct)),
            await Assert.ThrowsAsync<ResourceConflictException>(() =>
                _world.DeleteHandler().HandleAsync(new DeleteDistributorCommand(_world.Tenant, active.Id.Value, 4), Ct)),
        };

        Assert.Equal(("Blocked", "Active"), (blocked.Status, unblocked.Status));
        Assert.Equal(["unblock"], blocked.AllowedActions.Except(["edit"]));
        Assert.Equal(
            ["wholesale.distributor.blocked", "wholesale.distributor.unblocked"],
            _world.Audit.Entries.Select(entry => entry.Action));
        // El dominio lo diría como 422; el contrato es 409 y sale antes de mutar.
        Assert.All(outOfState, error => Assert.Equal("wholesale.distributor.invalid_transition", error.Code));
        Assert.Equal(DistributorStatus.Active, active.Status);
        Assert.Empty(_world.Events.Invitations);
    }

    [Fact]
    public async Task DeleteOnlyWhileInvitedAndThenTheTokenIsGone()
    {
        var token = InvitationTokens.Generate();
        var distributor = _world.SeedInvited(tokenHash: InvitationTokens.HashOf(token));

        var deleted = await _world.DeleteHandler().HandleAsync(
            new DeleteDistributorCommand(_world.Tenant, distributor.Id.Value, 1), Ct);

        Assert.True(deleted);
        Assert.Null(await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf(token), Ct));
        Assert.Equal("wholesale.distributor.deleted", Assert.Single(_world.Audit.Entries).Action);
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnUnknownDistributorIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _world.GetHandler().HandleAsync(new GetDistributorQuery(_world.Tenant, Guid.CreateVersion7()), Ct));

        Assert.Equal("wholesale.distributor.not_found", error.Code);
    }

    [Fact]
    public async Task CountsCarryTheFourKeysEvenAtZero()
    {
        _world.SeedInvited("1");
        _world.SeedInvited("2");
        _world.SeedActive("3");

        var all = await _world.ListHandler().HandleAsync(new ListDistributorsQuery(_world.Tenant, "invited", null, 0, 0), Ct);
        var none = await _world.ListHandler().HandleAsync(new ListDistributorsQuery(_world.Tenant, null, "no-existe", 1, 50), Ct);

        Assert.Equal((2, 1, 0, 3), (all.Counts.Invited, all.Counts.Active, all.Counts.Blocked, all.Counts.All));
        Assert.Equal((2, 1, 50), (all.Total, all.Page, all.PageSize));
        Assert.All(all.Items, item => Assert.Equal("Invited", item.Status));
        Assert.Equal((0, 0, 0, 0), (none.Counts.Invited, none.Counts.Active, none.Counts.Blocked, none.Counts.All));
        Assert.Empty(none.Items);
        var json = System.Text.Json.JsonSerializer.Serialize(all.Counts, WebJson);
        Assert.Equal("{\"Invited\":2,\"Active\":1,\"Blocked\":0,\"all\":3}", json);
    }

    [Fact]
    public async Task OptionsBringListsAdvisorsAndActiveCompaniesByName()
    {
        _world.PriceLists.Seed(_world.Tenant, "Alfa", -5m);
        _world.Members.AddActive("Beto");
        _world.Companies.Add("Extranjera", "NIF");
        _world.Companies.Add("Inactiva", "NIT", isActive: false);

        var options = await _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct);

        Assert.Equal(["Alfa", "Mayoristas"], options.PriceLists.Select(list => list.Name));
        Assert.Equal(["Ana Mayorista", "Beto"], options.Advisors.Select(advisor => advisor.Name));
        Assert.Equal([("Extranjera", false), ("Mayorista SAS", true)], options.Companies.Select(company => (company.Name, company.Eligible)));
    }

    [Fact]
    public async Task OptionsReturnTheTenantCountryAndFallBackToCO()
    {
        var withoutSettings = await _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct);
        _world.Tenancy.Countries[_world.Tenant] = "ES";
        var withSettings = await _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct);

        Assert.Equal("CO", withoutSettings.DefaultCountry);
        Assert.Equal("ES", withSettings.DefaultCountry);
    }

    [Fact]
    public async Task ReadersCannotWriteAndAnotherTenantIsForbidden()
    {
        _world.Context = new FakeExecutionContext(_world.Tenant, WholesalePermissions.DistributorRead);
        var readerError = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            _world.InviteHandler().HandleAsync(_world.InviteCommand(), Ct));
        var optionsError = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct));
        _world.Context = new FakeExecutionContext(
            Guid.CreateVersion7(), WholesalePermissions.DistributorRead, WholesalePermissions.DistributorManage);
        var foreignError = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            _world.ListHandler().HandleAsync(new ListDistributorsQuery(_world.Tenant, null, null, 1, 50), Ct));

        Assert.All([readerError, optionsError, foreignError], error => Assert.Equal("authorization.denied", error.Code));
        Assert.Equal(0, _world.Distributors.Writes);
    }

    // ---- Spec 2026-11-18: aviso por WhatsApp de la invitación ----

    private const string SavedPhone = "+573001234567";

    // Spec 2026-11-19 §2: con teléfono sale el evento nuevo, con el mismo token que el correo, y no el aviso viejo sin link.
    // D-8: el flag va en changed_fields; el teléfono nunca entra a la auditoría.
    [Fact]
    public async Task InvitingWithAPhoneWritesTheLinkEventAndNotTheOldNotice()
    {
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");
        var member = Guid.CreateVersion7();
        _world.Memberships.Active[(_world.Context.SubjectId, _world.Tenant)] = member;

        var detail = await _world.InviteHandler().HandleAsync(
            _world.InviteCommand() with { Phone = " 300 123 4567 ", WhatsAppConnectionId = null }, Ct);

        var email = Assert.Single(_world.Events.Invitations);
        var notice = Assert.Single(_world.Events.Notices);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Equal(
            (_world.Tenant, stored.Id.Value, SavedPhone, connection, member, "Ana Pérez", "ana@distri.co", 0, "trace-1",
                DistributorWorld.Now),
            (notice.TenantId, notice.DistributorId, notice.Phone, notice.ConnectionId, notice.SentByMemberId, notice.ContactName,
                notice.Email, notice.Generation, notice.CorrelationId, notice.OccurredAt));
        Assert.Equal(email.Token, notice.Token);
        Assert.Equal(FakeInvitationLinks.Compose(notice.Token), detail.InvitationUrl);
        // El aviso viejo ya no tiene método en IWholesaleEventPublisher: nadie puede escribirlo (la integración lo cuenta en el
        // outbox).
        Assert.Equal(("CO", " 300 123 4567 ".Trim()), (_world.Phones.Calls.Single().Country, _world.Phones.Calls.Single().Phone));
        Assert.Equal((SavedPhone, (Guid?)connection), (stored.Phone, stored.WhatsAppConnectionId));
        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Pending, stored.Invitation.NoticeStatus);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Contains("whatsAppNotice=true", audit.ChangedFields);
        Assert.DoesNotContain(audit.ChangedFields, field => field.Contains("300", StringComparison.Ordinal));
        Assert.Equal("Invited", detail.Status);
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    // D-6: sin membresía activa de quien invita (stub de desarrollo), el autor del mensaje es el asesor.
    [Fact]
    public async Task InviteWithoutAMembershipSendsTheNoticeAsTheAdvisor()
    {
        _world.WhatsApp.AddReady("+57 300 999 0000");

        await _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Phone = "3001234567" }, Ct);

        Assert.Equal(_world.Advisor, Assert.Single(_world.Events.Notices).SentByMemberId);
    }

    [Fact]
    public async Task InviteWithoutAPhonePublishesOnlyTheEmailAndIgnoresTheConnection()
    {
        var command = _world.InviteCommand() with { Phone = "   ", WhatsAppConnectionId = Guid.CreateVersion7() };

        await _world.InviteHandler().HandleAsync(command, Ct);

        Assert.Single(_world.Events.Invitations);
        Assert.Empty(_world.Events.Notices);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Null(stored.Phone);
        Assert.Null(stored.WhatsAppConnectionId);
        Assert.Null(stored.Invitation.NoticeStatus);
        Assert.Equal(0, _world.WhatsApp.ListCalls);
        Assert.Contains("whatsAppNotice=false", Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task InviteWithTwoReadyConnectionsAndNoneChosenIs422ConnectionRequired()
    {
        _world.WhatsApp.AddReady("+57 300 000 0001");
        _world.WhatsApp.AddReady("+57 300 000 0002");

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Phone = "3001234567" }, Ct));

        Assert.Equal("wholesale.distributor.whatsapp_connection_required", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task InviteWithNoReadyConnectionIs422WhatsAppUnavailable()
    {
        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Phone = "3001234567" }, Ct));

        Assert.Equal("wholesale.distributor.whatsapp_unavailable", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task InviteWithAConnectionThatIsNotReadyIs422WhatsAppUnavailable()
    {
        _world.WhatsApp.AddReady("+57 300 000 0001");
        var command = _world.InviteCommand() with { Phone = "3001234567", WhatsAppConnectionId = Guid.CreateVersion7() };

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.InviteHandler().HandleAsync(command, Ct));

        Assert.Equal("wholesale.distributor.whatsapp_unavailable", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task InviteWithAPhoneThatDoesNotNormalizeIs422OnPhoneAndWritesNothing()
    {
        _world.WhatsApp.AddReady("+57 300 000 0001");

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Phone = "no es un teléfono" }, Ct));

        Assert.Equal(("Phone", "The phone is not valid."),
            (Assert.Single(error.Errors).PropertyName, error.Errors.Single().ErrorMessage));
        AssertNothingWritten();
        Assert.Equal(0, _world.WhatsApp.ListCalls);
    }

    [Fact]
    public async Task InviteNormalizesThePhoneWithTheTenantCountry()
    {
        _world.Tenancy.Countries[_world.Tenant] = "MX";
        _world.WhatsApp.AddReady("+52 55 0000 0000");

        await _world.InviteHandler().HandleAsync(_world.InviteCommand() with { Phone = "5512345678" }, Ct);

        Assert.Equal("MX", Assert.Single(_world.Phones.Calls).Country);
        Assert.Equal("+525512345678", Assert.Single(_world.Distributors.Distributors).Phone);
    }

    // Review Focus 1 (D-9): el formulario reenvía el teléfono guardado con la conexión en null; eso no consulta Integrations,
    // así que editar no falla aunque la conexión ya no esté lista, y la generación no cambia.
    [Fact]
    public async Task UpdateWithTheSamePhoneAndNullConnectionDoesNotResolveTheConnection()
    {
        var connection = Guid.CreateVersion7();
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: connection);
        // Resolver cuáles están listas falla; el label del detalle (D-7) sí se puede pedir.
        _world.WhatsApp.ThrowWhenListed = true;

        var detail = await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, "300 123 4567", null), Ct);

        Assert.Equal(2L, detail.Version);
        Assert.Equal((SavedPhone, (Guid?)connection, 0), (distributor.Phone, distributor.WhatsAppConnectionId, distributor.Invitation.Generation));
        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Pending, distributor.Invitation.NoticeStatus);
    }

    // P-3: no hay aviso para el número nuevo hasta el próximo «Reenviar».
    [Fact]
    public async Task UpdateThatChangesThePhoneBumpsTheGenerationAndClearsTheNotice()
    {
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: connection);

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, "3109876543", null), Ct);

        Assert.Equal(("+573109876543", (Guid?)connection, 1), (distributor.Phone, distributor.WhatsAppConnectionId, distributor.Invitation.Generation));
        Assert.Null(distributor.Invitation.NoticeStatus);
        Assert.Empty(_world.Events.Notices);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal(["priceListId", "advisorId", "billingCompanyId", "phone", "whatsAppConnectionId"], audit.ChangedFields);
    }

    [Fact]
    public async Task UpdateWithoutAPhoneDeletesPhoneAndConnection()
    {
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: Guid.CreateVersion7());
        _world.WhatsApp.ThrowWhenCalled = true;

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, Guid.CreateVersion7()), Ct);

        Assert.Null(distributor.Phone);
        Assert.Null(distributor.WhatsAppConnectionId);
        Assert.Null(distributor.Invitation.NoticeStatus);
        Assert.Equal(1, distributor.Invitation.Generation);
    }

    // P-2: fuera de Invited el teléfono se ignora sin 422, aunque no normalice: el formulario reenvía todos los campos.
    [Fact]
    public async Task UpdateOutsideInvitedIgnoresThePhone()
    {
        var distributor = _world.SeedActive();
        _world.WhatsApp.ThrowWhenCalled = true;

        var detail = await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, "no es un teléfono", null), Ct);

        Assert.Equal(3L, detail.Version);
        Assert.Null(distributor.Phone);
        Assert.Empty(_world.Phones.Calls);
        Assert.Equal(["priceListId", "advisorId", "billingCompanyId"], Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task ResendWithAReadyConnectionQueuesANewNoticeWithTheNewGeneration()
    {
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");
        var member = Guid.CreateVersion7();
        _world.Memberships.Active[(_world.Context.SubjectId, _world.Tenant)] = member;
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: connection);

        await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, distributor.Id.Value, 1, DistributorAction.Resend, "trace-3"), Ct);

        var email = Assert.Single(_world.Events.Invitations);
        var notice = Assert.Single(_world.Events.Notices);
        Assert.Equal((SavedPhone, connection, member, 1, "trace-3"),
            (notice.Phone, notice.ConnectionId, notice.SentByMemberId, notice.Generation, notice.CorrelationId));
        Assert.Equal(email.Token, notice.Token);
        Assert.Equal((1, (InvitationNoticeStatus?)InvitationNoticeStatus.Pending),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus));
        Assert.Contains("whatsAppNotice=true", Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    // Spec 2026-11-19 §2: reenviar rota el token, y el evento nuevo lleva el rotado (el mismo del correo nuevo), no el anterior.
    [Fact]
    public async Task ResendRotatesTheTokenInTheNewEvent()
    {
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");
        var invited = await _world.InviteHandler().HandleAsync(
            _world.InviteCommand() with { Phone = "3001234567", WhatsAppConnectionId = connection }, Ct);
        var first = Assert.Single(_world.Events.Notices).Token;

        await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, invited.Id, invited.Version, DistributorAction.Resend, "trace-3"), Ct);

        Assert.Equal(2, _world.Events.Notices.Count);
        var second = _world.Events.Notices[1];
        Assert.NotEqual(first, second.Token);
        Assert.Equal(_world.Events.Invitations[1].Token, second.Token);
        Assert.Equal(1, second.Generation);
        var stored = Assert.Single(_world.Distributors.Distributors);
        Assert.Null(await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf(first), Ct));
        Assert.Same(stored, await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf(second.Token), Ct));
    }

    // Un fallo del aviso nunca frena el correo: la conexión guardada ya no está lista y el aviso queda fallido sin encolarse.
    [Fact]
    public async Task ResendWithTheConnectionNoLongerReadySendsTheEmailAndFailsTheNotice()
    {
        _world.WhatsApp.AddReady("+57 300 999 0000");
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: Guid.CreateVersion7());

        var detail = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, distributor.Id.Value, 1, DistributorAction.Resend, "trace-3"), Ct);

        Assert.Single(_world.Events.Invitations);
        Assert.Empty(_world.Events.Notices);
        Assert.Equal((1, (InvitationNoticeStatus?)InvitationNoticeStatus.Failed, "whatsapp_unavailable"),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));
        Assert.Contains("whatsAppNotice=false", Assert.Single(_world.Audit.Entries).ChangedFields);
        Assert.Equal(2L, detail.Version);
    }

    // Un fallo del aviso nunca frena el correo: si consultar las conexiones lanza, el reenvío sale y el aviso queda fallido.
    [Fact]
    public async Task ResendWhenListingTheConnectionsThrowsStillSendsTheEmailAndFailsTheNotice()
    {
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: Guid.CreateVersion7());
        _world.WhatsApp.ThrowWhenListed = true;

        var detail = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, distributor.Id.Value, 1, DistributorAction.Resend, "trace-4"), Ct);

        Assert.Equal(1, _world.WhatsApp.ListCalls);
        Assert.Equal("trace-4", Assert.Single(_world.Events.Invitations).CorrelationId);
        Assert.Empty(_world.Events.Notices);
        Assert.Equal((1, (InvitationNoticeStatus?)InvitationNoticeStatus.Failed, "whatsapp_unavailable"),
            (distributor.Invitation.Generation, distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));
        Assert.Contains("whatsAppNotice=false", Assert.Single(_world.Audit.Entries).ChangedFields);
        Assert.Equal(1, _world.UnitOfWork.Saves);
        Assert.Equal(2L, detail.Version);
    }

    // Sin conexiones listas la lista llega vacía y el formulario no ofrece el aviso.
    [Fact]
    public async Task OptionsBringTheReadyWhatsAppConnections()
    {
        var none = await _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct);
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");

        var options = await _world.OptionsHandler().HandleAsync(new GetDistributorOptionsQuery(_world.Tenant), Ct);

        Assert.Empty(none.WhatsAppConnections);
        Assert.Equal([new DistributorWhatsAppConnectionDto(connection, "+57 300 999 0000")], options.WhatsAppConnections);
    }

    // Spec 2026-11-19 §1, foco 5: la respuesta de invitar trae el link con el mismo token que viaja al correo, así el primer
    // «Copiar» no rota nada.
    [Fact]
    public async Task InviteReturnsTheSameLinkThatTheEmailCarries()
    {
        var detail = await _world.InviteHandler().HandleAsync(_world.InviteCommand(), Ct);

        var published = Assert.Single(_world.Events.Invitations);
        Assert.Equal(FakeInvitationLinks.Compose(published.Token), detail.InvitationUrl);
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task GetAndResendCarryNoInvitationUrl()
    {
        var distributor = _world.SeedInvited();

        var read = await _world.GetHandler().HandleAsync(new GetDistributorQuery(_world.Tenant, distributor.Id.Value), Ct);
        var resent = await _world.TransitionHandler().HandleAsync(
            new TransitionDistributorCommand(_world.Tenant, distributor.Id.Value, 1, DistributorAction.Resend, "trace-3"), Ct);

        Assert.Equal((null, null), (read.InvitationUrl, resent.InvitationUrl));
    }

    [Fact]
    public async Task RotateReturnsANewUrlAndTheOldTokenStopsWorking()
    {
        var distributor = _world.SeedInvited(tokenHash: InvitationTokens.HashOf("token-viejo"));
        _world.Clock.UtcNow = DistributorWorld.Now.AddDays(4); // vencida: rotar también la renueva

        var detail = await _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct);

        var token = FakeInvitationLinks.TokenOf(detail.InvitationUrl);
        Assert.Null(await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf("token-viejo"), Ct));
        Assert.Same(distributor, await _world.Distributors.FindByTokenHashAsync(InvitationTokens.HashOf(token), Ct));
        Assert.Equal((2L, DistributorWorld.Now.AddDays(4).AddHours(72), false),
            (detail.Version, detail.Invitation.ExpiresAt, detail.Invitation.Expired));
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task RotateDoesNotQueueNoticesNorEmails()
    {
        var connection = _world.WhatsApp.AddReady("+57 300 999 0000");
        var distributor = _world.SeedInvited(phone: SavedPhone, whatsAppConnectionId: connection);

        await _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct);

        Assert.Empty(_world.Events.Invitations);
        Assert.Empty(_world.Events.Notices);
        Assert.Empty(_world.Events.EmailChanges);
        Assert.Equal((InvitationNoticeStatus?)InvitationNoticeStatus.Pending, distributor.Invitation.NoticeStatus);
    }

    [Fact]
    public async Task RotateOnAnAcceptedDistributorIsConflict()
    {
        var distributor = _world.SeedActive();
        var hash = distributor.Invitation.TokenHash;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct));

        Assert.Equal("wholesale.distributor.invalid_transition", error.Code);
        Assert.Equal(hash, distributor.Invitation.TokenHash);
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task RotateWithAStaleVersionIs412()
    {
        var distributor = _world.SeedInvited();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            _world.RotateHandler().HandleAsync(RotateCommand(distributor) with { ExpectedVersion = 9 }, Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task RotateRequiresDistributorManage()
    {
        var distributor = _world.SeedInvited();
        var hash = distributor.Invitation.TokenHash;
        _world.Context = new FakeExecutionContext(_world.Tenant, WholesalePermissions.DistributorRead);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct));

        Assert.Equal(hash, distributor.Invitation.TokenHash);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheAuditOfTheRotationNamesNoToken()
    {
        var distributor = _world.SeedInvited();

        var detail = await _world.RotateHandler().HandleAsync(RotateCommand(distributor), Ct);

        var token = FakeInvitationLinks.TokenOf(detail.InvitationUrl);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal(("wholesale.distributor.invitation_rotated", "distributor", distributor.Id.ToString(), _world.Context.SubjectId),
            (audit.Action, audit.ResourceType, audit.ResourceId, audit.ActorId));
        Assert.Equal(["invitation"], audit.ChangedFields);
        var written = string.Join("|", audit.ChangedFields.Append(audit.ResourceId));
        Assert.DoesNotContain(token, written, StringComparison.Ordinal);
        Assert.DoesNotContain(InvitationTokens.HashOf(token), written, StringComparison.Ordinal);
    }

    // Decisión del owner 2026-11-24: los datos de la invitación se editan mientras está invitado.
    [Fact]
    public async Task UpdateWhileInvitedChangesTheProfileAndAuditsOnlyTheChangedFieldNames()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId, address: "Calle 1");
        var bogota = _world.Cities.Add("Bogotá");

        var detail = await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with
            {
                BusinessName = " Norte Nuevo SAS ",
                IdentificationNumber = "800111222",
                CityId = bogota,
                Address = "Carrera 7 # 1-1",
            },
            Ct);

        Assert.Equal(("Norte Nuevo SAS", "800111222", (Guid?)bogota, "Carrera 7 # 1-1", 2L),
            (distributor.BusinessName, distributor.IdentificationNumber, distributor.CityId, distributor.Address, detail.Version));
        Assert.Equal("Norte Nuevo SAS", detail.BusinessName);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal(
            ["priceListId", "advisorId", "billingCompanyId", "phone", "whatsAppConnectionId", "businessName", "identificationNumber", "cityId", "address"],
            audit.ChangedFields);
    }

    // Un frontend viejo no manda los campos nuevos: ausente = conserva lo guardado.
    [Fact]
    public async Task UpdateWithoutTheProfileFieldsKeepsEverythingStored()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId, address: "Calle 1");

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null), Ct);

        Assert.Equal(("ana@distri.co", "Distribuciones 900123456", "CO", "NIT", "900123456", "Ana Pérez", (Guid?)_world.MedellinId, "Calle 1"),
            (distributor.Email, distributor.BusinessName, distributor.Country, distributor.IdentificationType,
                distributor.IdentificationNumber, distributor.ContactName, distributor.CityId, distributor.Address));
        Assert.Equal(
            ["priceListId", "advisorId", "billingCompanyId", "phone", "whatsAppConnectionId"],
            Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    // Una fila de antes de la migración (colombiana sin ciudad) sigue editándose con un frontend viejo.
    [Fact]
    public async Task UpdateWithoutTheProfileFieldsDoesNotDemandTheCityOfALegacyInvitation()
    {
        var distributor = _world.SeedInvited();

        var detail = await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null), Ct);

        Assert.Equal(2L, detail.Version);
        Assert.Null(distributor.CityId);
    }

    // Tocar otro dato de una fila vieja tampoco la obliga a completar la ciudad; mandar la ciudad sí la valida.
    [Fact]
    public async Task UpdateOfALegacyInvitationValidatesTheCityOnlyWhenItIsSent()
    {
        var distributor = _world.SeedInvited();

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null) with { BusinessName = "Norte" }, Ct);
        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { CityId = Guid.Empty, ContactName = "Luis" }, Ct));

        Assert.Equal("Norte", distributor.BusinessName);
        Assert.Equal("Ana Pérez", distributor.ContactName);
        Assert.Equal("CityId", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task UpdateClearsTheAddressWithAnEmptyString()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId, address: "Calle 1");

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null) with { Address = "" }, Ct);

        Assert.Null(distributor.Address);
        Assert.Contains("address", Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    // El correo es editable pero no reenvía la invitación ni rota el token: eso es «Reenviar».
    [Fact]
    public async Task UpdateTheEmailNormalizesItAndDoesNotResendNorRotateTheToken()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId);
        var hash = distributor.Invitation.TokenHash;

        await _world.UpdateHandler().HandleAsync(UpdateCommand(distributor, null, null) with { Email = " Nuevo@Distri.co " }, Ct);

        Assert.Equal(("nuevo@distri.co", hash), (distributor.Email, distributor.Invitation.TokenHash));
        Assert.Empty(_world.Events.Invitations);
        Assert.Empty(_world.Events.Notices);
        Assert.Contains("email", Assert.Single(_world.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task UpdateToADocumentThatAnotherDistributorHasIs409AndWritesNothing()
    {
        _world.SeedInvited("111111111", cityId: _world.MedellinId);
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationNumber = " 111111111 " }, Ct));

        Assert.Equal("wholesale.distributor.identification_taken", error.Code);
        Assert.Equal(("222222222", 1L, 0), (distributor.IdentificationNumber, distributor.Version, _world.UnitOfWork.Saves));
    }

    [Fact]
    public async Task UpdateKeepingItsOwnDocumentIsNotAConflict()
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);

        var detail = await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationType = "NIT", IdentificationNumber = "222222222", BusinessName = "Otro nombre" }, Ct);

        Assert.Equal(2L, detail.Version);
    }

    [Fact]
    public async Task UpdateToTheSameNumberWithAnotherTypeIsNotAConflict()
    {
        _world.SeedInvited("111111111", cityId: _world.MedellinId);
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);

        await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationType = "CC", IdentificationNumber = "111111111" }, Ct);

        Assert.Equal(("CC", "111111111"), (distributor.IdentificationType, distributor.IdentificationNumber));
    }

    [Fact]
    public async Task UpdateWithAnUnknownCityIsCityNotFound()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId);

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { CityId = Guid.CreateVersion7() }, Ct));

        Assert.Equal("wholesale.distributor.city_not_found", error.Code);
        Assert.Equal((_world.MedellinId, 0), (distributor.CityId, _world.UnitOfWork.Saves));
    }

    [Theory]
    [InlineData("BusinessName", "   ")]
    [InlineData("ContactName", "")]
    [InlineData("Email", "no-es-correo")]
    [InlineData("IdentificationNumber", " ")]
    public async Task UpdateWithABlankOrInvalidProfileFieldIs422OnThatField(string property, string value)
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId);
        var command = property switch
        {
            "BusinessName" => UpdateCommand(distributor, null, null) with { BusinessName = value },
            "ContactName" => UpdateCommand(distributor, null, null) with { ContactName = value },
            "Email" => UpdateCommand(distributor, null, null) with { Email = value },
            _ => UpdateCommand(distributor, null, null) with { IdentificationNumber = value },
        };

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _world.UpdateHandler().HandleAsync(command, Ct));

        Assert.Equal(property, Assert.Single(error.Errors).PropertyName);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task UpdateWithATypeThatTheCountryDoesNotAdmitIs422OnIdentificationType()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId);

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationType = "NIF" }, Ct));

        Assert.Equal("IdentificationType", Assert.Single(error.Errors).PropertyName);
    }

    // Cambiar de país obliga a la ciudad de ese país y suelta la del anterior.
    [Fact]
    public async Task UpdateToAnotherCountryDemandsItsCityAndDropsTheColombianOne()
    {
        var distributor = _world.SeedInvited(cityId: _world.MedellinId);
        var toSpain = UpdateCommand(distributor, null, null) with { Country = "ES", IdentificationType = "NIF" };

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _world.UpdateHandler().HandleAsync(toSpain, Ct));
        await _world.UpdateHandler().HandleAsync(toSpain with { CityName = "Madrid" }, Ct);

        Assert.Equal("CityName", Assert.Single(error.Errors).PropertyName);
        Assert.Equal(("ES", "NIF", (Guid?)null, "Madrid"), (distributor.Country, distributor.IdentificationType, distributor.CityId, distributor.CityName));
        Assert.Empty(_world.Cities.Calls);
    }

    // P-2: fuera de Invited esos campos no se tocan. Si vienen y difieren, 409 invalid_transition; si vienen iguales (el
    // formulario reenvía lo que pintó) no pasa nada.
    [Fact]
    public async Task UpdateOutsideInvitedRejectsAChangedProfileFieldAndAcceptsTheSameValues()
    {
        var active = _world.SeedActive();
        var blocked = _world.SeedActive("900777888");
        blocked.Block(DistributorWorld.Now.AddHours(2));

        var activeError = await Assert.ThrowsAsync<ResourceConflictException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(active, null, null) with { BusinessName = "Otro nombre" }, Ct));
        var blockedError = await Assert.ThrowsAsync<ResourceConflictException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(blocked, null, null) with { Address = "Calle nueva" }, Ct));
        var detail = await _world.UpdateHandler().HandleAsync(
            UpdateCommand(active, null, null) with
            {
                BusinessName = " Distribuciones 900123456 ",
                Email = "ANA@distri.co",
                Country = "co",
                Address = "",
                // En «CO» la ciudad es el id: el nombre que pinta el detalle puede volver sin contar como cambio.
                CityName = "Medellín",
            },
            Ct);

        Assert.Equal("wholesale.distributor.invalid_transition", activeError.Code);
        Assert.Equal("wholesale.distributor.invalid_transition", blockedError.Code);
        Assert.Equal(("Distribuciones 900123456", 3L), (active.BusinessName, detail.Version));
        Assert.Equal(("Distribuciones 900777888", (string?)null), (blocked.BusinessName, blocked.Address));
    }

    // Una aceptación a medias deja el tenant reservado creado y la invitación pendiente: el reintento es idempotente por
    // documento, así que cambiarlo duplicaría la empresa y el cliente.
    [Theory]
    [InlineData("CO", "NIT", "800111222")]
    [InlineData("CO", "CC", "222222222")]
    [InlineData("ES", "NIF", "222222222")]
    public async Task UpdateOfTheDocumentAfterTheReservedTenantExistsIs409AndWritesNothing(string country, string type, string number)
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);
        SeedReservedTenant(distributor);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with
            {
                Country = country,
                IdentificationType = type,
                IdentificationNumber = number,
                CityName = country == "CO" ? null : "Madrid",
                BusinessName = "Otro nombre",
            },
            Ct));

        Assert.Equal("wholesale.distributor.acceptance_in_progress", error.Code);
        Assert.Equal(
            ("CO", "NIT", "222222222", "Distribuciones 222222222", 1L),
            (distributor.Country, distributor.IdentificationType, distributor.IdentificationNumber, distributor.BusinessName,
                distributor.Version));
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }

    // El formulario reenvía el documento: igual una vez normalizado no es un cambio, y el resto se sigue editando.
    [Fact]
    public async Task UpdateOfOtherFieldsAfterTheReservedTenantExistsPassesWithoutAskingTenancy()
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);
        SeedReservedTenant(distributor);

        await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with
            {
                Country = "co",
                IdentificationType = " nit ",
                IdentificationNumber = " 222222222 ",
                BusinessName = "Otro nombre",
                Email = "nuevo@distri.co",
                Address = "Calle 2",
            },
            Ct);

        Assert.Equal(
            ("Otro nombre", "nuevo@distri.co", "Calle 2", "222222222", 2L),
            (distributor.BusinessName, distributor.Email, distributor.Address, distributor.IdentificationNumber, distributor.Version));
        Assert.Empty(_world.Tenancy.StatusQueries);
    }

    [Fact]
    public async Task UpdateOfTheDocumentWithoutTheReservedTenantAsksTenancyForItAndPasses()
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);

        await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationNumber = "800111222" }, Ct);

        Assert.Equal("800111222", distributor.IdentificationNumber);
        Assert.Equal([distributor.Invitation.ReservedTenantId], _world.Tenancy.StatusQueries);
    }

    // Vinculando un tenant existente no hay tenant reservado: la marca de la aceptación es lo único que lo delata.
    [Fact]
    public async Task UpdateOfTheDocumentAfterTheAcceptanceStartedIs409AndWritesNothing()
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);
        distributor.StartAcceptance(DistributorWorld.Now);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { IdentificationNumber = "800111222" }, Ct));

        Assert.Equal("wholesale.distributor.acceptance_in_progress", error.Code);
        Assert.Equal(("222222222", 2L), (distributor.IdentificationNumber, distributor.Version));
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(0, _world.UnitOfWork.Saves);
        Assert.Empty(_world.Tenancy.StatusQueries);
    }

    [Fact]
    public async Task UpdateOfOtherFieldsAfterTheAcceptanceStartedPasses()
    {
        var distributor = _world.SeedInvited("222222222", cityId: _world.MedellinId);
        distributor.StartAcceptance(DistributorWorld.Now);

        await _world.UpdateHandler().HandleAsync(
            UpdateCommand(distributor, null, null) with { BusinessName = "Otro nombre" }, Ct);

        Assert.Equal(("Otro nombre", 3L), (distributor.BusinessName, distributor.Version));
        Assert.Equal(DistributorWorld.Now, distributor.AcceptanceStartedAt);
    }

    private void SeedReservedTenant(Distributor distributor) =>
        _world.Tenancy.AddTenant(
            distributor.Invitation.ReservedTenantId, "Distribuidor a medias", Guid.CreateVersion7(),
            Modules.Tenancy.Domain.TenantStatus.Active);

    private RotateInvitationLinkCommand RotateCommand(Distributor distributor) =>
        new(_world.Tenant, distributor.Id.Value, distributor.Version, "trace-rotate");

    private UpdateDistributorCommand UpdateCommand(Distributor distributor, string? phone, Guid? connection) =>
        new(_world.Tenant, distributor.Id.Value, distributor.Version, _world.PriceList.Id.Value, _world.Advisor, _world.Company.Id,
            phone, connection);

    private void AssertNothingWritten()
    {
        Assert.Empty(_world.Distributors.Distributors);
        Assert.Empty(_world.Events.Invitations);
        Assert.Empty(_world.Events.Notices);
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(0, _world.UnitOfWork.Saves);
    }
}
