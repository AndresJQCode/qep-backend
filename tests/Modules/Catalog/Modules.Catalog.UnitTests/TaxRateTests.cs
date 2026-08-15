using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class TaxRateTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsActive()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);

        Assert.True(taxRate.IsActive);
        Assert.Equal(TenantId, taxRate.TenantId);
        Assert.Equal("IVA general", taxRate.Name);
        Assert.Equal(19, taxRate.Percentage);
        Assert.Equal(1, taxRate.Version);
        Assert.Equal(Now, taxRate.CreatedAt);
        Assert.Equal(Now, taxRate.UpdatedAt);
    }

    // Mismo criterio que Product: el índice único es sobre (tenant_id, name), y " IVA general"
    // contra "IVA general" serían dos filas para lo que una persona lee como la misma tasa.
    [Fact]
    public void CreateTrimsName()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "  IVA general  ", 19, Now);

        Assert.Equal("IVA general", taxRate.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            TaxRate.Create(TaxRateId.New(), TenantId, name, 19, Now));

        Assert.Equal("catalog.tax_rate.name_required", error.Code);
    }

    // La columna es varchar(120). Sin guarda de dominio el valor llega a PostgreSQL y vuelve
    // como 500 server.unexpected, que es la forma de defecto de SDD-CT-06.
    [Fact]
    public void CreateRejectsNameOverOneHundredTwentyCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            TaxRate.Create(TaxRateId.New(), TenantId, new string('a', 121), 19, Now));

        Assert.Equal("catalog.tax_rate.name_too_long", error.Code);
    }

    // CA-CAT-03-06. El porcentaje es entero de 0 decimales (P-008, owner 2026-08-10) y sólo
    // tiene sentido dentro de 0..100: un negativo devolvería plata y uno mayor a 100 cobraría
    // más impuesto que el valor de la línea.
    [Theory]
    [InlineData(-1)]
    [InlineData(-19)]
    [InlineData(101)]
    [InlineData(1000)]
    public void CreateRejectsPercentageOutOfRange(int percentage)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", percentage, Now));

        Assert.Equal("catalog.tax_rate.percentage_out_of_range", error.Code);
    }

    // Los dos extremos son válidos y hay que probarlos: 0 es el exento colombiano y 100 es el
    // límite superior. Sin esta prueba, un guard escrito con > y < en vez de >= y <= pasa
    // desapercibido hasta que alguien carga una tasa exenta.
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void CreateAcceptsBoundaryPercentages(int percentage)
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "Exento", percentage, Now);

        Assert.Equal(percentage, taxRate.Percentage);
    }

    [Fact]
    public void UpdateChangesNameAndPercentageAndAdvancesUpdatedAt()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);
        var later = Now.AddMinutes(5);

        taxRate.Update("IVA reducido", 5, later);

        Assert.Equal("IVA reducido", taxRate.Name);
        Assert.Equal(5, taxRate.Percentage);
        Assert.Equal(later, taxRate.UpdatedAt);
        Assert.Equal(Now, taxRate.CreatedAt);
    }

    // El token de concurrencia nace con el agregado, no se agrega después. Product lo tuvo que
    // sumar en la corrección de la revisión de 4 lentes de CAT-02, donde dos lentes
    // independientes llegaron al mismo lost update.
    [Fact]
    public void UpdateAdvancesTheConcurrencyToken()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);

        taxRate.Update("IVA reducido", 5, Now.AddMinutes(5));

        Assert.Equal(2, taxRate.Version);
    }

    [Fact]
    public void UpdateRejectsBlankName()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            taxRate.Update("  ", 19, Now.AddMinutes(5)));

        Assert.Equal("catalog.tax_rate.name_required", error.Code);
    }

    [Fact]
    public void UpdateRejectsPercentageOutOfRange()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            taxRate.Update("IVA general", 101, Now.AddMinutes(5)));

        Assert.Equal("catalog.tax_rate.percentage_out_of_range", error.Code);
    }

    [Fact]
    public void DeactivateTurnsTaxRateInactiveAndAdvancesUpdatedAt()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);
        var later = Now.AddMinutes(5);

        taxRate.Deactivate(later);

        Assert.False(taxRate.IsActive);
        Assert.Equal(later, taxRate.UpdatedAt);
        Assert.Equal(2, taxRate.Version);
    }

    // CA-CAT-03-09: inactivar dos veces es un error de negocio, no un éxito silencioso.
    [Fact]
    public void DeactivateRejectsAnAlreadyInactiveTaxRate()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);
        taxRate.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            taxRate.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("catalog.tax_rate.already_inactive", error.Code);
    }

    [Fact]
    public void UpdateRejectsAnInactiveTaxRate()
    {
        var taxRate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA general", 19, Now);
        taxRate.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            taxRate.Update("IVA reducido", 5, Now.AddMinutes(10)));

        Assert.Equal("catalog.tax_rate.inactive", error.Code);
    }
}
