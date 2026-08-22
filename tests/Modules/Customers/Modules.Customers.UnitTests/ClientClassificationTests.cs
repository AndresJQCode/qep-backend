using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

public sealed class ClientClassificationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsActive()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);

        Assert.True(classification.IsActive);
        Assert.Equal(TenantId, classification.TenantId);
        Assert.Equal("Mayorista", classification.Name);
        Assert.Equal("MAY", classification.Prefix);
        Assert.Equal(1, classification.Version);
        Assert.Equal(Now, classification.CreatedAt);
        Assert.Equal(Now, classification.UpdatedAt);
    }

    // Mismo criterio que TaxRate: los indices unicos son sobre (tenant_id, name) y
    // (tenant_id, prefix), y " Mayorista " contra "Mayorista" serian dos filas para lo que una
    // persona lee como la misma clasificacion.
    [Fact]
    public void CreateTrimsNameAndPrefix()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "  Mayorista  ", "  MAY  ", Now);

        Assert.Equal("Mayorista", classification.Name);
        Assert.Equal("MAY", classification.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CustomersDomainException>(() =>
            ClientClassification.Create(ClientClassificationId.New(), TenantId, name, "MAY", Now));

        Assert.Equal("customers.classification.name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankPrefix(string prefix)
    {
        var error = Assert.Throws<CustomersDomainException>(() =>
            ClientClassification.Create(
                ClientClassificationId.New(), TenantId, "Mayorista", prefix, Now));

        Assert.Equal("customers.classification.prefix_required", error.Code);
    }

    // La columna es varchar(120). Sin guarda de dominio el valor llega a PostgreSQL y vuelve
    // como 500 server.unexpected, que es la forma de defecto de SDD-CT-06.
    [Fact]
    public void CreateRejectsNameOverOneHundredTwentyCharacters()
    {
        var error = Assert.Throws<CustomersDomainException>(() =>
            ClientClassification.Create(
                ClientClassificationId.New(), TenantId, new string('a', 121), "MAY", Now));

        Assert.Equal("customers.classification.name_too_long", error.Code);
    }

    // La columna es varchar(20).
    [Fact]
    public void CreateRejectsPrefixOverTwentyCharacters()
    {
        var error = Assert.Throws<CustomersDomainException>(() =>
            ClientClassification.Create(
                ClientClassificationId.New(), TenantId, "Mayorista", new string('a', 21), Now));

        Assert.Equal("customers.classification.prefix_too_long", error.Code);
    }

    [Fact]
    public void UpdateChangesNameAndPrefixAndAdvancesUpdatedAt()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        var later = Now.AddMinutes(5);

        classification.Update("Minorista", "MIN", later);

        Assert.Equal("Minorista", classification.Name);
        Assert.Equal("MIN", classification.Prefix);
        Assert.Equal(later, classification.UpdatedAt);
        Assert.Equal(Now, classification.CreatedAt);
    }

    // El token de concurrencia nace con el agregado, no se agrega despues. Ver TaxRate.Version.
    [Fact]
    public void UpdateAdvancesTheConcurrencyToken()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);

        classification.Update("Minorista", "MIN", Now.AddMinutes(5));

        Assert.Equal(2, classification.Version);
    }

    [Fact]
    public void UpdateRejectsBlankName()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);

        var error = Assert.Throws<CustomersDomainException>(() =>
            classification.Update("  ", "MAY", Now.AddMinutes(5)));

        Assert.Equal("customers.classification.name_required", error.Code);
    }

    [Fact]
    public void UpdateRejectsBlankPrefix()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);

        var error = Assert.Throws<CustomersDomainException>(() =>
            classification.Update("Mayorista", "  ", Now.AddMinutes(5)));

        Assert.Equal("customers.classification.prefix_required", error.Code);
    }

    [Fact]
    public void DeactivateTurnsClassificationInactiveAndAdvancesUpdatedAt()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        var later = Now.AddMinutes(5);

        classification.Deactivate(later);

        Assert.False(classification.IsActive);
        Assert.Equal(later, classification.UpdatedAt);
        Assert.Equal(2, classification.Version);
    }

    [Fact]
    public void DeactivateRejectsAnAlreadyInactiveClassification()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        classification.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CustomersDomainException>(() =>
            classification.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("customers.classification.already_inactive", error.Code);
    }

    [Fact]
    public void UpdateRejectsAnInactiveClassification()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        classification.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CustomersDomainException>(() =>
            classification.Update("Minorista", "MIN", Now.AddMinutes(10)));

        Assert.Equal("customers.classification.inactive", error.Code);
    }

    [Fact]
    public void ActivateTurnsClassificationActiveAndAdvancesUpdatedAt()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        classification.Deactivate(Now.AddMinutes(5));
        var later = Now.AddMinutes(10);

        classification.Activate(later);

        Assert.True(classification.IsActive);
        Assert.Equal(later, classification.UpdatedAt);
    }

    [Fact]
    public void ActivateRejectsAnAlreadyActiveClassification()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);

        var error = Assert.Throws<CustomersDomainException>(() =>
            classification.Activate(Now.AddMinutes(5)));

        Assert.Equal("customers.classification.already_active", error.Code);
    }

    // Create deja 1, Deactivate 2, Activate 3.
    [Fact]
    public void ActivateAdvancesTheConcurrencyToken()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        classification.Deactivate(Now.AddMinutes(5));

        classification.Activate(Now.AddMinutes(10));

        Assert.Equal(3, classification.Version);
    }

    // El caso que prueba que Update vuelve a funcionar despues de Activate: sin esto se puede
    // entregar un Activate que responde bien y deja la clasificacion igual de congelada, porque
    // Update sigue abriendo con EnsureActive().
    [Fact]
    public void ActivateReopensUpdate()
    {
        var classification = ClientClassification.Create(
            ClientClassificationId.New(), TenantId, "Mayorista", "MAY", Now);
        classification.Deactivate(Now.AddMinutes(5));
        classification.Activate(Now.AddMinutes(10));

        classification.Update("Minorista", "MIN", Now.AddMinutes(15));

        Assert.Equal("Minorista", classification.Name);
        Assert.Equal("MIN", classification.Prefix);
    }
}
