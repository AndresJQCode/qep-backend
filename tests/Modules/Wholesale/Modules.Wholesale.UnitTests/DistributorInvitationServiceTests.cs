using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>
/// La invitación vista desde el link (D-3, D-16, D-17): todos los puertos falsos y el reloj fijo. Lo que se prueba acá es el
/// orden de las verificaciones y que cada paso sea idempotente; los adaptadores de verdad los cubre la integración.
/// </summary>
public sealed class DistributorInvitationServiceTests
{
    private static readonly TenantModuleKey[] WholesalerModules =
    [
        TenantModuleKeys.Catalog, TenantModuleKeys.Customers, TenantModuleKeys.Companies, TenantModuleKeys.Quotations,
        TenantModuleKeys.Orders, TenantModuleKeys.Wholesale,
    ];

    private static readonly TenantRegistrationData NewTenantData =
        new("Distri Norte", "distri-norte", "es-CO", "America/Bogota", "dd/MM/yyyy");

    private readonly DistributorWorld _world = new();
    private readonly FakeTenancy _tenancy = new();
    private readonly FakeSupplierLinker _suppliers = new();
    private readonly FakeCustomerProvisioning _customers = new();
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly string _token = InvitationTokens.Generate();
    private readonly Distributor _invitation;

    public DistributorInvitationServiceTests()
    {
        _tenancy.AddTenant(_world.Tenant, "Mayorista Central", Guid.CreateVersion7(), TenantStatus.Active, WholesalerModules);
        _invitation = _world.SeedInvited(tokenHash: InvitationTokens.HashOf(_token));
    }

    private static CancellationToken Ct => DistributorWorld.Ct;

    private DistributorInvitationService Service() => new(
        _world.Clock,
        _world.Distributors,
        _tenancy,
        _tenancy,
        _tenancy,
        _tenancy,
        _tenancy,
        _world.Companies,
        _suppliers,
        _customers,
        _world.UnitOfWork,
        _world.Audit);

    private Task<AcceptedInvitationDto> AcceptAsync(AcceptInvitationChoice choice, string? email = "ana@distri.co", Guid? user = null) =>
        Service().AcceptAsync(_token, user ?? _user, email, choice, "trace-accept", Ct);

    private void AssertNothingWritten()
    {
        Assert.Equal(0, _tenancy.Writes);
        Assert.Empty(_suppliers.Calls);
        Assert.Empty(_customers.Calls);
        Assert.Equal(0, _world.UnitOfWork.Saves);
        Assert.Empty(_world.Audit.Entries);
        Assert.Equal(DistributorStatus.Invited, _invitation.Status);
    }

    [Fact]
    public async Task AcceptCreatesTheCustomerWithTheDistributorType()
    {
        var token = InvitationTokens.Generate();
        _world.SeedInvited("X-123", tokenHash: InvitationTokens.HashOf(token), identificationType: "CE");

        await Service().AcceptAsync(token, _user, "ana@distri.co", new NewTenantChoice(NewTenantData), "trace-accept", Ct);

        var customer = Assert.Single(_customers.Calls);
        Assert.Equal(("CO", "CE", "X-123"), (customer.Country, customer.IdentificationType, customer.Identification));
    }

    [Fact]
    public async Task AcceptCreatesTheCustomerWithTheDistributorCountry()
    {
        var token = InvitationTokens.Generate();
        _world.SeedInvited("B12345678", tokenHash: InvitationTokens.HashOf(token), identificationType: "NIF", country: "ES");

        await Service().AcceptAsync(token, _user, "ana@distri.co", new NewTenantChoice(NewTenantData), "trace-accept", Ct);

        Assert.Equal(("ES", "NIF"), (Assert.Single(_customers.Calls).Country, _customers.Calls[0].IdentificationType));
    }

    [Fact]
    public async Task NewTenantCreatesTheReservedTenantSupplierAndCustomerAndActivates()
    {
        var result = await AcceptAsync(new NewTenantChoice(NewTenantData));

        var reserved = _invitation.Invitation.ReservedTenantId;
        var registered = Assert.Single(_tenancy.Registered);
        Assert.Equal((reserved, _user, NewTenantData), (registered.TenantId, registered.Owner, registered.Data));
        Assert.Equal(WholesaleModuleSets.InvitedDistributor, registered.Modules);
        Assert.Equal([NewTenantData], _tenancy.Checked);
        var supplier = Assert.Single(_suppliers.Calls);
        Assert.Equal((reserved, _world.Tenant, "Mayorista Central", _world.Company, _user),
            (supplier.DistributorTenantId, supplier.WholesalerTenantId, supplier.WholesalerName, supplier.Company, supplier.Actor));
        var customer = Assert.Single(_customers.Calls);
        // D-7: el cliente nace en el mayorista con quien invitó como actor, no con quien acepta.
        Assert.Equal((_world.Tenant, _invitation.InvitedByUserId, _invitation.BusinessName, "CO", "NIT", "900123456", "ana@distri.co"),
            (customer.WholesalerTenantId, customer.Actor, customer.BusinessName, customer.Country, customer.IdentificationType, customer.Identification, customer.Email));
        Assert.Equal(
            (DistributorStatus.Active, (Guid?)reserved, (Guid?)_customers.CustomerId, (Guid?)_user),
            (_invitation.Status, _invitation.DistributorTenantId, _invitation.CustomerId, _invitation.Invitation.AcceptedByUserId));
        Assert.Equal((reserved, "Distri Norte", true), (result.TenantId, result.TenantName, result.Created));
        Assert.Equal(["catalog", "customers", "companies", "quotations", "orders", "reporting", "suppliers", "purchases"], result.EnabledModules);
        var audit = Assert.Single(_world.Audit.Entries);
        Assert.Equal((_world.Tenant, _user, "wholesale.distributor.accepted", "distributor"),
            (audit.TenantId, audit.ActorId, audit.Action, audit.ResourceType));
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    [Fact]
    public async Task LinkTenantEnablesOnlyTheMissingModules()
    {
        var mine = _tenancy.AddTenant("Mi Tienda", _user, TenantStatus.Active, TenantModuleKeys.Catalog);

        var result = await AcceptAsync(new LinkTenantChoice(mine));

        var grant = Assert.Single(_tenancy.Grants);
        Assert.Equal((mine, _user, "Invitación de mayorista: Mayorista Central"), (grant.TenantId, grant.Actor, grant.Note));
        Assert.Equal(["catalog", "suppliers", "purchases"], grant.Keys.Select(key => key.Value));
        Assert.Equal((mine, "Mi Tienda", false), (result.TenantId, result.TenantName, result.Created));
        Assert.Equal(["suppliers", "purchases"], result.EnabledModules);
        Assert.Empty(_tenancy.Registered);
        Assert.Equal(mine, Assert.Single(_suppliers.Calls).DistributorTenantId);
        Assert.Equal((DistributorStatus.Active, (Guid?)mine), (_invitation.Status, _invitation.DistributorTenantId));
    }

    [Theory]
    [InlineData(" Ana@Distri.CO ")]
    [InlineData("ANA@DISTRI.CO")]
    public async Task EmailMatchIgnoresCaseAndSurroundingSpaces(string sessionEmail)
    {
        var result = await AcceptAsync(new NewTenantChoice(NewTenantData), sessionEmail);

        Assert.True(result.Created);
        Assert.Equal(DistributorStatus.Active, _invitation.Status);
    }

    [Theory]
    [InlineData("otra@distri.co")]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnotherAccountIsForbiddenAndNothingIsWritten(string? sessionEmail)
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            AcceptAsync(new NewTenantChoice(NewTenantData), sessionEmail));
        var listing = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Service().ListLinkableTenantsAsync(_token, _user, sessionEmail, Ct));

        Assert.Equal("wholesale.invitation.email_mismatch", error.Code);
        Assert.Equal("wholesale.invitation.email_mismatch", listing.Code);
        Assert.Empty(_tenancy.Checked);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnotherAccountCannotLearnTheStateOfAnExpiredInvitation()
    {
        _world.Clock.UtcNow = DistributorWorld.Now.AddDays(4);

        // El correo se mira antes de revelar si venció.
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            AcceptAsync(new NewTenantChoice(NewTenantData), "otra@distri.co"));

        Assert.Equal("wholesale.invitation.email_mismatch", error.Code);
    }

    [Fact]
    public async Task LinkingATenantYouDoNotOwnIs403()
    {
        var foreign = _tenancy.AddTenant("Ajena", Guid.CreateVersion7(), TenantStatus.Active, TenantModuleKeys.Catalog);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => AcceptAsync(new LinkTenantChoice(foreign)));

        Assert.Equal("wholesale.invitation.not_tenant_owner", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task LinkingTheWholesalerItselfIs422SelfLink()
    {
        // Quien acepta es también owner del mayorista (el mismo correo en las dos organizaciones).
        _tenancy.Tenants[_world.Tenant] = _tenancy.Tenants[_world.Tenant] with { Owner = _user };

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() => AcceptAsync(new LinkTenantChoice(_world.Tenant)));

        Assert.Equal("wholesale.invitation.self_link", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task LinkingATenantAlreadyLinkedIs409()
    {
        var mine = _tenancy.AddTenant("Mi Tienda", _user, TenantStatus.Active, TenantModuleKeys.Catalog);
        _world.SeedActive("700111222", distributorTenant: mine);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AcceptAsync(new LinkTenantChoice(mine)));

        Assert.Equal("wholesale.distributor.already_linked", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnExpiredInvitationIs410AndAnUnknownTokenIs404()
    {
        _world.Clock.UtcNow = DistributorWorld.Now.AddHours(72).AddTicks(1);

        var expired = await Assert.ThrowsAsync<ResourceGoneException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));
        var expiredSession = await Assert.ThrowsAsync<ResourceGoneException>(() => Service().RequirePendingAsync(_token, Ct));
        var unknown = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Service().AcceptAsync(InvitationTokens.Generate(), _user, "ana@distri.co", new NewTenantChoice(NewTenantData), "t", Ct));
        var unknownSummary = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Service().FindAsync(InvitationTokens.Generate(), Ct));

        // El dominio lo diría como 422; el contrato es 410 y 404.
        Assert.Equal("wholesale.invitation.expired", expired.Code);
        Assert.Equal("wholesale.invitation.expired", expiredSession.Code);
        Assert.Equal("wholesale.invitation.not_found", unknown.Code);
        Assert.Equal("wholesale.invitation.not_found", unknownSummary.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AWholesalerWithoutTheModuleIs409Unavailable()
    {
        var entry = _tenancy.Tenants[_world.Tenant];
        entry.Modules.Remove(TenantModuleKeys.Wholesale);

        var withoutModule = await Assert.ThrowsAsync<ResourceConflictException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));
        entry.Modules.Add(TenantModuleKeys.Wholesale);
        _tenancy.Tenants[_world.Tenant] = entry with { Status = TenantStatus.Suspended };
        var suspended = await Assert.ThrowsAsync<ResourceConflictException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));

        Assert.Equal("wholesale.invitation.unavailable", withoutModule.Code);
        Assert.Equal("wholesale.invitation.unavailable", suspended.Code);
        AssertNothingWritten();
    }

    // Ruling W16 (a): la empresa facturadora sigue siendo elegible por documento pero la desactivaron después de invitar.
    [Fact]
    public async Task AnInactiveBillingCompanyIs409Unavailable()
    {
        var index = _world.Companies.Companies.FindIndex(company => company.Id == _world.Company.Id);
        _world.Companies.Companies[index] = _world.Company with { IsActive = false };

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));

        Assert.Equal("wholesale.invitation.unavailable", error.Code);
        AssertNothingWritten();
    }

    // Rulings W16 (b) y W18: el mayorista bloqueó la relación; volver a aceptar no es el doble clic de D-16. 403, como el spec.
    [Fact]
    public async Task AcceptingABlockedRelationshipIs403Inactive()
    {
        await AcceptAsync(new NewTenantChoice(NewTenantData));
        _invitation.Block(DistributorWorld.Now);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));

        Assert.Equal("wholesale.relationship.inactive", error.Code);
        Assert.Single(_tenancy.Registered);
        Assert.Equal(1, _world.UnitOfWork.Saves);
        Assert.Equal(DistributorStatus.Blocked, _invitation.Status);
    }

    // Review Focus 1 y ruling W9: el doble clic termina con un solo tenant, un proveedor y un cliente; la segunda respuesta
    // es 200 sobre el mismo tenant, con created=false y sin módulos prendidos, y no escribe nada.
    [Fact]
    public async Task AcceptingTwiceBySameUserReturnsTheSameResult()
    {
        var first = await AcceptAsync(new NewTenantChoice(NewTenantData));

        var second = await AcceptAsync(new NewTenantChoice(NewTenantData));

        Assert.Equal((first.TenantId, first.TenantName), (second.TenantId, second.TenantName));
        Assert.False(second.Created);
        Assert.Empty(second.EnabledModules);
        Assert.Single(_tenancy.Registered);
        Assert.Single(_suppliers.Calls);
        Assert.Single(_customers.Calls);
        Assert.Equal(1, _world.UnitOfWork.Saves);
        Assert.Single(_world.Audit.Entries);
    }

    [Fact]
    public async Task AnInvitationAcceptedByAnotherUserIs409AlreadyAccepted()
    {
        await AcceptAsync(new NewTenantChoice(NewTenantData));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            AcceptAsync(new NewTenantChoice(NewTenantData), user: Guid.CreateVersion7()));
        var session = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().RequirePendingAsync(_token, Ct));

        Assert.Equal("wholesale.invitation.already_accepted", error.Code);
        Assert.Equal("wholesale.invitation.already_accepted", session.Code);
        Assert.Single(_tenancy.Registered);
    }

    // Review Focus 2: el paso del cliente falla; la invitación sigue pendiente y el reintento termina sobre el tenant
    // reservado sin crear otro ni volver a validar el slug (que ya es suyo).
    [Fact]
    public async Task RetryAfterAFailedCustomerStepReusesTheReservedTenant()
    {
        _customers.FailTimes = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));
        Assert.Equal(DistributorStatus.Invited, _invitation.Status);
        Assert.Equal(InvitationViewState.Pending, _invitation.InvitationState(_world.Clock.UtcNow));
        Assert.Equal(0, _world.UnitOfWork.Saves);

        var retry = await AcceptAsync(new NewTenantChoice(NewTenantData));

        var reserved = _invitation.Invitation.ReservedTenantId;
        Assert.Single(_tenancy.Registered);
        Assert.Single(_tenancy.Checked);
        Assert.Equal((reserved, true), (retry.TenantId, retry.Created));
        Assert.Equal(WholesaleModuleSets.InvitedDistributor.Select(key => key.Value), retry.EnabledModules);
        Assert.Equal(2, _suppliers.Calls.Count); // idempotente: el enlazador devuelve el mismo proveedor
        Assert.Equal(DistributorStatus.Active, _invitation.Status);
        Assert.Equal(1, _world.UnitOfWork.Saves);
    }

    // D-17: un intento anterior ya creó el tenant reservado; aunque ahora pida vincular otro, termina sobre el reservado.
    [Fact]
    public async Task AReservedTenantThatExistsWinsOverTheRequestedMode()
    {
        var reserved = _invitation.Invitation.ReservedTenantId;
        _tenancy.AddTenant(reserved, "Distri Norte", _user, TenantStatus.Active, [.. WholesaleModuleSets.InvitedDistributor]);
        var other = _tenancy.AddTenant("Otra Mía", _user, TenantStatus.Active, TenantModuleKeys.Catalog);

        var result = await AcceptAsync(new LinkTenantChoice(other));

        Assert.Equal((reserved, "Distri Norte", true), (result.TenantId, result.TenantName, result.Created));
        Assert.Empty(_tenancy.Grants);
        Assert.Empty(_tenancy.Registered);
        Assert.Equal(reserved, Assert.Single(_suppliers.Calls).DistributorTenantId);
        Assert.Equal((Guid?)reserved, _invitation.DistributorTenantId);
    }

    // Review Focus 1, la carrera de verdad: la otra pestaña commitea el tenant reservado entre la relectura y el chequeo del slug,
    // que ahora dice «tomado» por el propio tenant. Termina sobre el reservado, sin un 422.
    [Fact]
    public async Task AConcurrentAcceptThatCreatedTheReservedTenantFirstIsNotASlugError()
    {
        var reserved = _invitation.Invitation.ReservedTenantId;
        _tenancy.BeforeEnsureRegistrable = () =>
            _tenancy.AddTenant(reserved, "Distri Norte", _user, TenantStatus.Active, [.. WholesaleModuleSets.InvitedDistributor]);

        var result = await AcceptAsync(new NewTenantChoice(NewTenantData));

        Assert.Equal((reserved, "Distri Norte", true), (result.TenantId, result.TenantName, result.Created));
        Assert.Empty(_tenancy.Registered);
        Assert.Equal(DistributorStatus.Active, _invitation.Status);
    }

    [Fact]
    public async Task ASlugTakenByAnotherTenantIsStillA422()
    {
        _tenancy.AddTenant("Distri Norte", Guid.CreateVersion7(), TenantStatus.Active, TenantModuleKeys.Catalog);

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));

        Assert.Equal("tenancy.slug.taken", error.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AReservedTenantOwnedBySomeoneElseIsNeverHandedOver()
    {
        var reserved = _invitation.Invitation.ReservedTenantId;
        _tenancy.AddTenant(reserved, "Ajeno", Guid.CreateVersion7(), TenantStatus.Active, TenantModuleKeys.Catalog);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => AcceptAsync(new NewTenantChoice(NewTenantData)));

        Assert.Equal("wholesale.invitation.not_tenant_owner", error.Code);
        AssertNothingWritten();
    }

    // Una carrera perdida al guardar (412 o el índice único) suelta los cambios y relee: si la ganó este mismo usuario, la
    // respuesta es la de un doble clic.
    [Fact]
    public async Task AConcurrentAcceptThatWonTheSaveReturnsTheSameResult()
    {
        var winner = _world.SeedInvited("111", tokenHash: _invitation.Invitation.TokenHash);
        _world.Distributors.Distributors.Remove(winner);
        winner.Accept(_user, _invitation.Invitation.ReservedTenantId, Guid.CreateVersion7(), DistributorWorld.Now);
        _world.UnitOfWork.FailNextSaveWith = new RequestConcurrencyException("concurrency.conflict", "lost");
        _world.UnitOfWork.OnDiscard = () => _world.Distributors.Replace(_invitation, winner);

        var result = await AcceptAsync(new NewTenantChoice(NewTenantData));

        Assert.Equal((_invitation.Invitation.ReservedTenantId, false), (result.TenantId, result.Created));
        Assert.Empty(result.EnabledModules);
        Assert.Equal(1, _world.UnitOfWork.Discards);
    }

    [Fact]
    public async Task ALinkRaceOnTheUniqueIndexIs409AlreadyLinked()
    {
        var mine = _tenancy.AddTenant("Mi Tienda", _user, TenantStatus.Active, TenantModuleKeys.Catalog);
        var fresh = _world.SeedInvited("222", tokenHash: _invitation.Invitation.TokenHash);
        _world.Distributors.Distributors.Remove(fresh);
        _world.UnitOfWork.FailNextSaveWith = new ResourceConflictException("wholesale.distributor.already_linked", "index");
        _world.UnitOfWork.OnDiscard = () => _world.Distributors.Replace(_invitation, fresh);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => AcceptAsync(new LinkTenantChoice(mine)));

        Assert.Equal("wholesale.distributor.already_linked", error.Code);
        Assert.Equal(1, _world.UnitOfWork.Discards);
    }

    [Fact]
    public async Task LinkableTenantsMarkTheWholesalerAndTheAlreadyLinked()
    {
        _tenancy.Tenants[_world.Tenant] = _tenancy.Tenants[_world.Tenant] with { Owner = _user };
        var linked = _tenancy.AddTenant("B Ya Vinculada", _user, TenantStatus.Active, TenantModuleKeys.Catalog);
        _world.SeedActive("700111222", distributorTenant: linked);
        var free = _tenancy.AddTenant("A Libre", _user, TenantStatus.Active,
            TenantModuleKeys.Catalog, TenantModuleKeys.Suppliers);

        var tenants = await Service().ListLinkableTenantsAsync(_token, _user, "ana@distri.co", Ct);

        Assert.Equal(
            [
                (free, "A Libre", true, (string?)null, "purchases"),
                (linked, "B Ya Vinculada", false, "already_linked", "suppliers,purchases"),
                (_world.Tenant, "Mayorista Central", false, "same_tenant", "suppliers,purchases"),
            ],
            tenants.Select(tenant => (tenant.TenantId, tenant.Name, tenant.Linkable, tenant.IneligibleReason,
                string.Join(",", tenant.ModulesToEnable))));
        AssertNothingWritten();
    }

    [Fact]
    public async Task FindShowsTheDerivedStatusWithoutTheToken()
    {
        var pending = await Service().FindAsync(_token, Ct);
        _world.Clock.UtcNow = DistributorWorld.Now.AddDays(4);
        var expired = await Service().FindAsync(_token, Ct);

        Assert.Equal(
            new InvitationSummaryDto("Mayorista Central", _invitation.BusinessName, "Ana Pérez", "ana@distri.co", "pending",
                DistributorWorld.Now.AddHours(72)),
            pending);
        Assert.Equal("expired", expired.Status);
    }

    [Fact]
    public async Task RequirePendingReturnsTheInvitationWhilePending()
    {
        var distributor = await Service().RequirePendingAsync(_token, Ct);

        Assert.Same(_invitation, distributor);
    }
}
