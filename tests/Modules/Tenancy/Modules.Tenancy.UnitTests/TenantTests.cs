using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateSettingsWithChangesIncrementsVersionAndRaisesEvent()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            "QCode Enterprise",
            "en-US",
            "America/New_York",
            "MM/dd/yyyy",
            CreatedAt.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal(2, tenant.Version);
        var domainEvent = Assert.Single(tenant.DomainEvents);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(domainEvent);
        Assert.Equal(
            ["displayName", "defaultCulture", "timeZone", "dateFormat"],
            settingsUpdated.ChangedFields);
    }

    [Fact]
    public void UpdateSettingsWithoutChangesDoesNotRaiseEvent()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            CreatedAt.AddMinutes(5));

        Assert.False(changed);
        Assert.Equal(1, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    [Fact]
    public void CreateWithInvalidCultureThrowsDomainException()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Tenant.Create(
                TenantId.New(),
                "qcode-demo",
                "QCode Demo",
                "_",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                CreatedAt));

        Assert.Equal("tenancy.settings.culture.invalid", exception.Code);
    }

    private static readonly MembershipId OwnerMembershipId = MembershipId.New();

    private static Tenant CreateTenant(MembershipId? owner = null) =>
        Tenant.Create(
            TenantId.New(),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            owner ?? OwnerMembershipId,
            CreatedAt);

    // Decisión 3 del spec 2026-09-19: el mismo agregado guarda el archivo y la clave pública
    // (la URL no se guarda: se arma al leer con la base pública vigente).
    [Fact]
    public void SetLogoIncrementsVersionAndRaisesTheEvent()
    {
        var tenant = CreateTenant();
        var fileId = Guid.CreateVersion7();

        var changed = tenant.SetLogo(fileId, "tenants/x/media/y/original.png", CreatedAt.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(fileId, tenant.LogoFileId);
        Assert.Equal("tenants/x/media/y/original.png", tenant.LogoPublicKey);
        Assert.Equal(2, tenant.Version);
        var domainEvent = Assert.IsType<TenantLogoUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(fileId, domainEvent.LogoFileId);
        Assert.Equal(2, domainEvent.Version);
    }

    [Fact]
    public void SetLogoWithTheSameFileIsANoOp()
    {
        var tenant = CreateTenant();
        var fileId = Guid.CreateVersion7();
        tenant.SetLogo(fileId, "tenants/x/media/y/original.png", CreatedAt.AddMinutes(1));
        tenant.PullDomainEvents();

        var changed = tenant.SetLogo(fileId, "tenants/x/media/y/original.png", CreatedAt.AddMinutes(2));

        Assert.False(changed);
        Assert.Equal(2, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    [Fact]
    public void RemoveLogoClearsBothFieldsAndRaisesTheEventWithNull()
    {
        var tenant = CreateTenant();
        var fileId = Guid.CreateVersion7();
        tenant.SetLogo(fileId, "tenants/x/media/y/original.png", CreatedAt.AddMinutes(1));
        tenant.PullDomainEvents();

        var changed = tenant.RemoveLogo(CreatedAt.AddMinutes(2));

        Assert.True(changed);
        Assert.Null(tenant.LogoFileId);
        Assert.Null(tenant.LogoPublicKey);
        Assert.Equal(3, tenant.Version);
        var domainEvent = Assert.IsType<TenantLogoUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Null(domainEvent.LogoFileId);
    }

    [Fact]
    public void RemoveLogoWithoutLogoIsANoOp()
    {
        var tenant = CreateTenant();

        var changed = tenant.RemoveLogo(CreatedAt.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(1, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    // SetLogoOnAnInactiveTenantIsRejected: no hay forma pública de desactivar un Tenant hoy
    // (TenantStatus sólo lo pone el constructor, Tenant.cs:36), así que este caso no se puede
    // construir sin agregar API al agregado sólo para la prueba — el spec documenta esto
    // explícitamente y lo deja cubierto por el EnsureActive que UpdateSettings ya ejercita
    // (no hay un caso "OnAnInactiveTenant" separado en este archivo para UpdateSettings tampoco;
    // EnsureActive es el mismo guard privado que ambos métodos comparten).

    /// <summary>
    /// El tenant nombra a su autoridad al nacer. Antes el owner se deducia de
    /// <c>memberships.origin = 'registration'</c>, que responde "como nacio esta membresia" y no
    /// "quien manda en este tenant"; despues paso a una columna que admitia <c>NULL</c>, y el
    /// unico tenant que la dejaba vacia -- el <c>qcode-demo</c> del inicializador -- quedaba sin
    /// ninguna membresia protegida. Ahora no hay tenant sin owner: lo exige el constructor.
    /// </summary>
    [Fact]
    public void CreateRecordsTheOwnerMembership()
    {
        var owner = MembershipId.New();

        var tenant = CreateTenant(owner);

        Assert.Equal(owner, tenant.OwnerMembershipId);
        Assert.True(tenant.IsOwner(owner));
    }

    [Fact]
    public void IsOwnerIsFalseForAnotherMembership()
    {
        var tenant = CreateTenant();

        Assert.False(tenant.IsOwner(MembershipId.New()));
    }
}
