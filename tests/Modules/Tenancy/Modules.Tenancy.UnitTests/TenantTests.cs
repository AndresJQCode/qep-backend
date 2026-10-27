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
            tenant.DefaultCurrency,
            tenant.NumberFormat,
            tenant.Country,
            tenant.PersonTypeRequirement,
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
    public void CreateDefaultsToPesosAndCommaDecimal()
    {
        var tenant = CreateTenant();

        Assert.Equal("COP", tenant.DefaultCurrency);
        Assert.Equal(TenantNumberFormats.CommaDecimal, tenant.NumberFormat);
    }

    [Fact]
    public void UpdateSettingsChangesCurrencyAndNumberFormatAndTracksThem()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            "usd", "1,234.56", tenant.Country, tenant.PersonTypeRequirement, CreatedAt.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal("USD", tenant.DefaultCurrency);
        Assert.Equal("1,234.56", tenant.NumberFormat);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(["defaultCurrency", "numberFormat"], settingsUpdated.ChangedFields);
    }

    [Fact]
    public void UpdateSettingsWithAMalformedCurrencyThrowsAndChangesNothing()
    {
        var tenant = CreateTenant();

        var exception = Assert.Throws<TenantDomainException>(() => tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            "US", tenant.NumberFormat, tenant.Country, tenant.PersonTypeRequirement, CreatedAt.AddMinutes(5)));

        Assert.Equal("tenancy.settings.default_currency.invalid", exception.Code);
        Assert.Equal("COP", tenant.DefaultCurrency);
        Assert.Equal(1, tenant.Version);
    }

    // Los tenants que ya existían antes del país son colombianos: la migración rellena con el
    // mismo valor que Create, así que un tenant nuevo y uno viejo no se distinguen.
    [Fact]
    public void CreateDefaultsToColombia()
    {
        var tenant = CreateTenant();

        Assert.Equal("CO", tenant.Country);
    }

    [Fact]
    public void UpdateSettingsChangesTheCountryNormalizedAndTracksIt()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            tenant.DefaultCurrency, tenant.NumberFormat, " mx ", tenant.PersonTypeRequirement, CreatedAt.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal("MX", tenant.Country);
        Assert.Equal(2, tenant.Version);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(["country"], settingsUpdated.ChangedFields);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C")]
    [InlineData("COL")]
    [InlineData("C1")]
    public void UpdateSettingsWithAMalformedCountryThrowsAndChangesNothing(string country)
    {
        var tenant = CreateTenant();

        var exception = Assert.Throws<TenantDomainException>(() => tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            tenant.DefaultCurrency, tenant.NumberFormat, country, tenant.PersonTypeRequirement, CreatedAt.AddMinutes(5)));

        Assert.Equal("tenancy.settings.country.invalid", exception.Code);
        Assert.Equal("CO", tenant.Country);
        Assert.Equal(1, tenant.Version);
    }

    // Spec 2026-10-26: un tenant nuevo no exige tipo de persona; la migración rellena con el mismo
    // «Never», así que uno nuevo y uno viejo no se distinguen.
    [Fact]
    public void PersonTypeRequirementStartsAtNever()
    {
        var tenant = CreateTenant();

        Assert.Equal(PersonTypeRequirement.Never, tenant.PersonTypeRequirement);
    }

    [Fact]
    public void UpdateSettingsChangesThePersonTypeRequirementAndTracksIt()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            tenant.DefaultCurrency, tenant.NumberFormat, tenant.Country,
            PersonTypeRequirement.OnOrderApproval, CreatedAt.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal(PersonTypeRequirement.OnOrderApproval, tenant.PersonTypeRequirement);
        Assert.Equal(2, tenant.Version);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(["personTypeRequirement"], settingsUpdated.ChangedFields);
    }

    // Lo guardado sólo cuenta si el tenant es colombiano (regla única del país, Global Constraints).
    [Theory]
    [InlineData("CO", PersonTypeRequirement.OnCustomerSave, PersonTypeRequirement.OnCustomerSave)]
    [InlineData("co", PersonTypeRequirement.OnOrderApproval, PersonTypeRequirement.OnOrderApproval)]
    [InlineData("ES", PersonTypeRequirement.OnCustomerSave, PersonTypeRequirement.Never)]
    [InlineData(null, PersonTypeRequirement.OnOrderApproval, PersonTypeRequirement.Never)]
    public void EffectiveIsNeverOutsideColombia(
        string? country, PersonTypeRequirement stored, PersonTypeRequirement expected) =>
        Assert.Equal(expected, PersonTypeRequirementPolicy.Effective(country, stored));

    [Theory]
    [InlineData("co", true)]
    [InlineData("CO", true)]
    [InlineData("ES", false)]
    [InlineData(null, false)]
    public void IsColombianIgnoresCase(string? country, bool expected) =>
        Assert.Equal(expected, PersonTypeRequirementPolicy.IsColombian(country));

    [Fact]
    public void UpdateSettingsWithoutChangesDoesNotRaiseEvent()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            tenant.DefaultCurrency,
            tenant.NumberFormat,
            tenant.Country,
            tenant.PersonTypeRequirement,
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

    // The browser formats every local date and time with `Intl.DateTimeFormat({ timeZone })`,
    // which only understands IANA ids. `FindSystemTimeZoneById` also resolves Windows ids, so
    // without the IANA check "SA Pacific Standard Time" would be stored and break every screen.
    [Theory]
    [InlineData("SA Pacific Standard Time")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    public void CreateWithANonIanaTimeZoneThrowsDomainException(string timeZone)
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Tenant.Create(
                TenantId.New(),
                "qcode-demo",
                "QCode Demo",
                "es-CO",
                timeZone,
                "yyyy-MM-dd",
                MembershipId.New(),
                CreatedAt));

        Assert.Equal("tenancy.settings.time_zone.invalid", exception.Code);
    }

    [Fact]
    public void UpdateSettingsWithAWindowsTimeZoneIdThrowsDomainException()
    {
        var tenant = CreateTenant();

        var exception = Assert.Throws<TenantDomainException>(() =>
            tenant.UpdateSettings(
                tenant.DisplayName,
                tenant.DefaultCulture,
                "SA Pacific Standard Time",
                tenant.DateFormat,
                tenant.DefaultCurrency,
                tenant.NumberFormat,
                tenant.Country,
                tenant.PersonTypeRequirement,
                CreatedAt.AddMinutes(5)));

        Assert.Equal("tenancy.settings.time_zone.invalid", exception.Code);
        Assert.Equal("America/Bogota", tenant.TimeZone);
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
