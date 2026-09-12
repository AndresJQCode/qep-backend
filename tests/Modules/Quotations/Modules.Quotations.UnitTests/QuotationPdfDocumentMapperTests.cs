using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF se arma desde la **misma** respuesta que dibuja la pantalla, no desde el agregado.
/// Asi no pueden divergir: si la pantalla muestra un nombre de producto o una razon social, el
/// documento que recibe el cliente muestra lo mismo, resuelto por el mismo composer.
///
/// Lo que este mapeo decide es la forma de lo que el documento imprime: contacto y ubicacion en
/// una linea cada uno, y sobre todo **los importes por linea**, que son los que el cliente
/// comprueba a mano con una calculadora.
///
/// La fixture no es inventada: son las dos lineas de una cotizacion real de un mayorista, con
/// sus totales verificados contra el PDF que el cliente recibio.
/// </summary>
public sealed class QuotationPdfDocumentMapperTests
{
    [Fact]
    public void MapsTheHeaderAndTheTotals()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal("QUO-2026-0042", document.QuotationNumber);
        Assert.Equal(new DateOnly(2026, 9, 30), document.ValidUntil);
        Assert.Equal("COP", document.Currency);
        Assert.Equal(1290857.15m, document.Subtotal);
        Assert.Equal(245262.85m, document.TaxAmount);
        Assert.Equal(1536120m, document.Total);
    }

    // La retencion en la fuente la calcula el dominio (Quotation.RecalculateTotals) y viaja en la
    // respuesta desde entonces, pero el documento no la imprimia: un cliente con retencion leia
    // un total que no es lo que iba a pagar en efectivo.
    [Fact]
    public void MapsTheRetentionSnapshotAndTheNetTotal()
    {
        var response = Response() with { RetentionAmount = 32271.43m, NetTotal = 1503848.57m };

        var document = QuotationPdfDocumentMapper.From(response);

        Assert.Equal(32271.43m, document.RetentionAmount);
        Assert.Equal(1503848.57m, document.NetTotal);
    }

    // Sin esta bandera el documento tiene un IVA en cero que no explica por que: el lector no
    // puede distinguir "productos exentos" de "error de calculo".
    [Fact]
    public void MapsTheVatSurplusFlagSoTheZeroTaxCanExplainItself()
    {
        var response = Response() with { CustomerVatSurplus = true, TaxAmount = 0m };

        var document = QuotationPdfDocumentMapper.From(response);

        Assert.True(document.CustomerVatSurplus);
    }

    [Fact]
    public void MapsEachLineWithItsResolvedProductName()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal(2, document.Items.Count);
        Assert.Equal("BRONCEADOR RITUAL DEL SOL", document.Items[0].ProductName);
        Assert.Equal(12m, document.Items[0].Quantity);
        Assert.Equal(35900m, document.Items[0].UnitPrice);
    }

    /// <summary>
    /// El invariante que hace honesto al documento. El precio del producto viene **con IVA
    /// adentro** (QuotationItem.Apply), pero `QuotationItemResponse.Subtotal` es la base **sin**
    /// IVA. Imprimir esa base al lado del unitario dejaba una linea que no multiplica: 12 x
    /// 35.900 no da 307.714. El cliente lo comprueba con la calculadora y el documento se
    /// contradice a si mismo.
    /// </summary>
    [Fact]
    public void TheDiscountedUnitPriceTimesTheQuantityIsExactlyTheLineTotal()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        foreach (var line in document.Items)
        {
            Assert.Equal(line.LineTotal, line.DiscountedUnitPrice * line.Quantity);
        }
    }

    [Fact]
    public void TheLineTotalIsTheGrossMinusItsDiscountWithTheTaxStillInside()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        // 12 x 35.900 = 430.800, menos 15% = 366.180. Con el IVA adentro, que es como se cobra.
        Assert.Equal(30515m, document.Items[0].DiscountedUnitPrice);
        Assert.Equal(366180m, document.Items[0].LineTotal);
        Assert.Equal(97495m, document.Items[1].DiscountedUnitPrice);
        Assert.Equal(1169940m, document.Items[1].LineTotal);
    }

    // Lo que el mayorista gana si revende a valor publico: la suma de los descuentos de linea.
    // Es el mismo numero que el encabezado ya trae como DiscountAmount, no uno nuevo.
    [Fact]
    public void TheDocumentDiscountIsTheBuyerMargin()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.Equal(271080m, document.DiscountAmount);
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

    // D6: la ficha "Asesor" presenta a la persona por su nombre, que es lo que el cliente espera
    // leer en un documento comercial.
    [Fact]
    public void TheAdvisorLabelPrefersTheName()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { AdvisorName = "Ana Pérez" });

        Assert.Equal("Ana Pérez", document.AdvisorLabel);
    }

    // Sin nombre —membresías anteriores al nombre y el owner hasta que lo cargue— el documento
    // sigue saliendo con el correo, que es lo que imprimía antes.
    [Fact]
    public void TheAdvisorLabelFallsBackToTheEmail()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { AdvisorName = null });

        Assert.Equal("ana@qep.co", document.AdvisorLabel);
    }

    // Sin ninguno de los dos queda vacío, y quotation.typ ya sabe resolver el vacío.
    [Fact]
    public void TheAdvisorLabelIsEmptyWithoutNameOrEmail()
    {
        var document = QuotationPdfDocumentMapper.From(
            Response() with { AdvisorName = null, AdvisorEmail = null });

        Assert.Equal(string.Empty, document.AdvisorLabel);
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

    // Sin datos propios, cada parte dice "los del cliente" en vez de repetir el bloque Cliente.
    [Fact]
    public void PartiesWithoutTheirOwnDataFallBackToTheCustomer()
    {
        var document = QuotationPdfDocumentMapper.From(Response());

        Assert.True(document.Billing.SameAsCustomer);
        Assert.True(document.Shipping.SameAsCustomer);
        Assert.False(document.IsStorePickup);
    }

    // Recoger en tienda no es "los mismos datos del cliente": eso se lee como "se entrega en la
    // direccion del cliente", que es justo lo que no va a pasar. La entrega deja de ser la del
    // cliente y el documento lo dice con su propia bandera.
    [Fact]
    public void AStorePickupQuotationDoesNotFallBackToTheCustomerAddress()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { IsStorePickup = true });

        Assert.True(document.IsStorePickup);
        Assert.False(document.Shipping.SameAsCustomer);
        Assert.Equal(string.Empty, document.Shipping.Location);
        Assert.True(document.Billing.SameAsCustomer);
    }

    // Consumidor final tiene nombre y NIT fijos y nada mas: ni contacto ni direccion, que en la
    // parte ausente se heredarian del cliente real y dirian a quien se le vende, no a quien se le
    // factura.
    [Fact]
    public void AFinalConsumerQuotationBillsToTheFixedNameAndTaxId()
    {
        var document = QuotationPdfDocumentMapper.From(Response() with { BillsToFinalConsumer = true });

        Assert.False(document.Billing.SameAsCustomer);
        Assert.Equal("Consumidor final", document.Billing.Name);
        Assert.Equal("222222222222", document.Billing.TaxId);
        Assert.Equal(string.Empty, document.Billing.Contact);
        Assert.Equal(string.Empty, document.Billing.Location);
        Assert.True(document.Shipping.SameAsCustomer);
    }

    // Una parte de facturacion propia no guarda identificacion: el campo queda vacio, que la
    // plantilla lee como "no imprimir".
    [Fact]
    public void ABillingPartyWithItsOwnDataHasNoTaxId()
    {
        var party = new QuotationPartyResponse(
            Guid.CreateVersion7(), "Billing", "Sede administrativa", null, null, "Carrera 7",
            null, null);

        var document = QuotationPdfDocumentMapper.From(Response() with { Parties = [party] });

        Assert.Equal("Sede administrativa", document.Billing.Name);
        Assert.Equal(string.Empty, document.Billing.TaxId);
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
        "QUO-2026-0042",
        Client().Id,
        Client(),
        Guid.CreateVersion7(),
        "ana@qep.co",
        null,
        "Draft",
        new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 9, 30),
        "Anticipado",
        "COP",
        1290857.15m,
        19m,
        245262.85m,
        271080m,
        1536120m,
        false,
        0m,
        1536120m,
        null,
        [],
        false,
        false,
        false,
        null,
        Guid.CreateVersion7(),
        null,
        DateTimeOffset.UtcNow,
        null,
        null,
        true,
        false,
        false,
        [
            // 12 x 35.900 = 430.800; 15% = 64.620; linea = 366.180 con IVA adentro.
            // IVA contenido = 366.180 x 19 / 119 = 58.465,71; base = 307.714,29.
            new QuotationItemResponse(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "BRONCEADOR RITUAL DEL SOL",
                "BRO-01",
                null,
                [],
                12m,
                35900m,
                15m,
                64620m,
                307714.29m,
                19,
                58465.71m,
                1),
            // 12 x 114.700 = 1.376.400; 15% = 206.460; linea = 1.169.940.
            // IVA contenido = 186.797,14; base = 983.142,86.
            new QuotationItemResponse(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "COMBO JALEA REAL 3",
                "JAL-03",
                null,
                [],
                12m,
                114700m,
                15m,
                206460m,
                983142.86m,
                19,
                186797.14m,
                2)
        ]);
}
