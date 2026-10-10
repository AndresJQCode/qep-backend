using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Código y nombre del producto de cada línea (owner, 2026-09-26): mientras la cotización es
/// borrador se leen del catálogo en vivo; al salir del borrador —enviarla o convertirla en pedido—
/// cada línea se queda con los que tenía el producto en ese momento, y de ahí en más la cotización
/// y su pedido ya no cambian con el catálogo.
/// </summary>
public sealed class QuotationProductSnapshotTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    [Fact]
    public void ADraftLineHasNoProductSnapshot()
    {
        var quotation = NewQuotationWith(ProductA);

        var item = Assert.Single(quotation.Items);
        Assert.Null(item.ProductCode);
        Assert.Null(item.ProductName);
    }

    [Fact]
    public void SendingCapturesTheProductCodeAndNameOfEveryLine()
    {
        var quotation = NewQuotationWith(ProductA, ProductB);

        quotation.Send(AdvisorId, Now, Catalog(
            (ProductA, "A-001", "Tornillo"),
            (ProductB, "B-002", "Tuerca")));

        var a = quotation.Items.Single(item => item.ProductId == ProductA);
        var b = quotation.Items.Single(item => item.ProductId == ProductB);
        Assert.Equal(("A-001", "Tornillo"), (a.ProductCode, a.ProductName));
        Assert.Equal(("B-002", "Tuerca"), (b.ProductCode, b.ProductName));
    }

    // Un reenvío no vuelve a mirar el catálogo para lo que ya se congeló: lo que el cliente
    // recibió la primera vez es lo que dice el documento.
    [Fact]
    public void ResendingKeepsTheSnapshotTakenOnTheFirstSend()
    {
        var quotation = NewQuotationWith(ProductA);
        quotation.Send(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));

        quotation.Send(AdvisorId, Now.AddHours(1), Catalog((ProductA, "A-999", "Tornillo nuevo")));

        var item = Assert.Single(quotation.Items);
        Assert.Equal(("A-001", "Tornillo"), (item.ProductCode, item.ProductName));
    }

    // Una línea agregada después del primer envío todavía no le llegó al cliente: se congela en
    // el envío siguiente, junto con el documento que la incluye.
    [Fact]
    public void ResendingCapturesLinesAddedAfterTheFirstSend()
    {
        var quotation = NewQuotationWith(ProductA);
        quotation.Send(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));
        AddLine(quotation, ProductB);

        quotation.Send(AdvisorId, Now.AddHours(1), Catalog(
            (ProductA, "A-999", "Tornillo nuevo"),
            (ProductB, "B-002", "Tuerca")));

        var b = quotation.Items.Single(item => item.ProductId == ProductB);
        Assert.Equal(("B-002", "Tuerca"), (b.ProductCode, b.ProductName));
    }

    // Un producto que el catálogo ya no devuelve no frena el envío: la línea queda sin snapshot
    // y se sigue leyendo en vivo, igual que antes de que el snapshot existiera.
    [Fact]
    public void SendingWithAnUnresolvedProductLeavesThatLineWithoutSnapshot()
    {
        var quotation = NewQuotationWith(ProductA, ProductB);

        quotation.Send(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        var b = quotation.Items.Single(item => item.ProductId == ProductB);
        Assert.Null(b.ProductCode);
        Assert.Null(b.ProductName);
    }

    [Fact]
    public void ASendThatIsRejectedDoesNotCaptureAnything()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId,
            validUntil: null, null, null, QuotationParties.Empty, BillingAccount, "COP", false, false,
            AdvisorId, Now);
        AddLine(quotation, ProductA);

        Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo"))));

        Assert.Null(Assert.Single(quotation.Items).ProductCode);
    }

    // Convertir un borrador en pedido también lo saca del borrador sin pasar por el envío: el
    // pedido tiene que nacer con los productos congelados igual.
    [Fact]
    public void ConvertingADraftToAnOrderCapturesTheProducts()
    {
        var quotation = NewQuotationWith(ProductA);

        quotation.ConvertToOrder(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));

        var item = Assert.Single(quotation.Items);
        Assert.Equal(("A-001", "Tornillo"), (item.ProductCode, item.ProductName));
    }

    [Fact]
    public void ConvertingASentQuotationKeepsWhatWasSent()
    {
        var quotation = NewQuotationWith(ProductA);
        quotation.Send(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));

        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1), Catalog((ProductA, "A-999", "Otro")));

        Assert.Equal("A-001", Assert.Single(quotation.Items).ProductCode);
    }

    // Una línea que se suma al pedido ya convertido no tiene un envío posterior que la congele:
    // se congela al agregarla, y las que ya estaban no se tocan.
    [Fact]
    public void LinesAddedAfterConversionAreCapturedWithoutTouchingTheOthers()
    {
        var quotation = NewQuotationWith(ProductA);
        quotation.ConvertToOrder(AdvisorId, Now, Catalog((ProductA, "A-001", "Tornillo")));
        quotation.AddItemAfterConversion(
            QuotationItemId.New(), ProductB, 1m, 1_000m, 0m, 19, AdvisorId, Now.AddHours(1));
        var version = quotation.Version;

        quotation.CaptureProductSnapshotsAfterConversion(Catalog(
            (ProductA, "A-999", "Otro"),
            (ProductB, "B-002", "Tuerca")));

        Assert.Equal("A-001", quotation.Items.Single(item => item.ProductId == ProductA).ProductCode);
        Assert.Equal("B-002", quotation.Items.Single(item => item.ProductId == ProductB).ProductCode);
        // No es una edición: la versión ya la subió la línea que se agregó.
        Assert.Equal(version, quotation.Version);
    }

    private static Quotation NewQuotationWith(params Guid[] productIds)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId,
            new DateOnly(2026, 10, 30), "Transferencia bancaria", null, QuotationParties.Empty,
            BillingAccount, "COP", false, false, AdvisorId, Now);
        foreach (var productId in productIds)
        {
            AddLine(quotation, productId);
        }

        return quotation;
    }

    private static void AddLine(Quotation quotation, Guid productId) =>
        quotation.AddItem(
            QuotationItemId.New(), productId, quantity: 1m, unitPrice: 1_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

    private static Dictionary<Guid, QuotationProductSnapshot> Catalog(
        params (Guid Id, string Code, string Name)[] products) =>
        products.ToDictionary(
            product => product.Id,
            product => new QuotationProductSnapshot(product.Code, product.Name));
}
