using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF se arma desde la **misma** respuesta que dibuja la pantalla, no desde el agregado.
/// Asi no pueden divergir: si la pantalla muestra un nombre de producto o una razon social, el
/// documento que recibe el cliente muestra lo mismo, resuelto por el mismo composer.
///
/// Lo que este mapeo decide es la forma de las lineas que el documento imprime como texto
/// corrido -- contacto y ubicacion en una linea cada uno -- porque el `.typ` no deberia tener
/// que saber que hacer cuando el cliente no tiene correo.
/// </summary>
public sealed class QuotationPdfDocumentMapperTests
{
    [Fact]
    public void MapsTheHeaderAndTheTotals()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal("QUO-2026-0002", document.QuotationNumber);
        Assert.Equal(new DateOnly(2026, 9, 30), document.ValidUntil);
        Assert.Equal("COP", document.Currency);
        Assert.Equal(273700m, document.Total);
        Assert.Equal(43700m, document.TaxAmount);
    }

    [Fact]
    public void JoinsPhoneAndEmailInASingleContactLine()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal("3001234567 · compras@ejemplo.co", document.CustomerContact);
    }

    // Un cliente sin correo no imprime el separador colgando.
    [Fact]
    public void OmitsTheSeparatorWhenTheCustomerHasNoEmail()
    {
        var client = Client() with { Email = null };

        var document = QuotationPdfDocumentMapper.From(Response() with { Client = client });

        Assert.Equal("3001234567", document.CustomerContact);
    }

    [Fact]
    public void JoinsAddressAndCityInASingleLocationLine()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal("Calle 100 #15-20, Bogotá", document.CustomerLocation);
    }

    // `ClientId` es una referencia blanda entre modulos: una cotizacion cuyo cliente se borro
    // tiene que poder exportarse igual, y no reventar al armar el documento.
    [Fact]
    public void SurvivesAQuotationWhoseCustomerNoLongerExists()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { Client = null });

        Assert.Equal(string.Empty, document.CustomerName);
        Assert.Equal(string.Empty, document.CustomerContact);
    }

    [Fact]
    public void MapsEachLineWithItsResolvedProductName()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        var line = Assert.Single(document.Items);
        Assert.Equal("Tornillo hexagonal 3/8", line.ProductName);
        Assert.Equal(100m, line.Quantity);
        Assert.Equal(1500m, line.UnitPrice);
        Assert.Equal(150000m, line.Subtotal);
    }

    // Sin datos propios, cada parte dice "los del cliente" en vez de repetir el bloque Cliente.
    [Fact]
    public void PartiesWithoutTheirOwnDataFallBackToTheCustomer()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.True(document.Billing.SameAsCustomer);
        Assert.True(document.Shipping.SameAsCustomer);
    }

    private static QuotationClientResponse Client() => new(
        Guid.CreateVersion7(),
        "CUC-0042",
        "Comercializadora del Norte S.A.S.",
        "3001234567",
        "compras@ejemplo.co",
        "Calle 100 #15-20",
        Guid.CreateVersion7(),
        "Bogotá",
        Guid.CreateVersion7(),
        "Cundinamarca",
        null,
        false,
        false,
        true,
        DateTimeOffset.UtcNow,
        []);

    private static QuotationResponse Response() => new(
        Guid.CreateVersion7(),
        "QUO-2026-0002",
        Client().Id,
        Client(),
        Guid.CreateVersion7(),
        "ana@qep.co",
        "Draft",
        new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 9, 30),
        "Transferencia",
        "COP",
        230000m,
        19m,
        43700m,
        0m,
        273700m,
        false,
        0m,
        273700m,
        null,
        [],
        false,
        null,
        Guid.CreateVersion7(),
        null,
        DateTimeOffset.UtcNow,
        null,
        null,
        true,
        false,
        [
            new QuotationItemResponse(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "Tornillo hexagonal 3/8",
                "TOR-38",
                null,
                [],
                100m,
                1500m,
                0m,
                0m,
                150000m,
                19,
                28500m,
                1)
        ]);
}
