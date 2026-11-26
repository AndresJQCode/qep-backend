using System.Text.Json;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Una configuración de Siigo completa y un pedido con números reales anonimizados, para las pruebas del armador.</summary>
internal static class InvoiceTestData
{
    public static readonly Guid Line1Variant = Guid.Parse("0193a1b2-0001-7000-8000-000000000001");
    public static readonly Guid Line2Variant = Guid.Parse("0193a1b2-0002-7000-8000-000000000002");
    public static readonly Guid ReteFuenteRate = Guid.Parse("0193a1b2-0003-7000-8000-000000000003");
    public static readonly Guid ReteIcaRate = Guid.Parse("0193a1b2-0004-7000-8000-000000000004");
    public static readonly Guid ProofA = Guid.Parse("0193a1b2-0005-7000-8000-000000000005");
    public static readonly Guid ProofB = Guid.Parse("0193a1b2-0006-7000-8000-000000000006");
    public static readonly DateOnly Today = new(2026, 11, 18);
    public const string BankKey = "BANCOLOMBIA|000123456";
    public static readonly Guid Advisor = Guid.Parse("0193a1b2-000c-7000-8000-00000000000c");

    /// <summary>Todo vinculado: IVA 19 (y 0 sin vínculo), las dos retenciones, la cuenta de cobro, crédito a 30 días, contraentrega y
    /// el producto del flete.</summary>
    public static SiigoSettings Settings(
        bool freightSeparate = false,
        bool sendToDian = false,
        bool sendMail = false,
        bool linkVariants = true,
        bool linkBank = true,
        bool linkAdvisor = true,
        SiigoPriceMode priceMode = SiigoPriceMode.BaseWithDiscount)
    {
        var settings = SiigoSettings.Create(Guid.Parse("0193a1b2-0009-7000-8000-000000000009"), Guid.Parse("0193a1b2-000a-7000-8000-00000000000a"));
        settings.AssignCompany(Guid.Parse("0193a1b2-000b-7000-8000-00000000000b"));
        settings.UpdateDocument(new SiigoDocumentSettings("24446", "10", "1", sendToDian, sendMail, freightSeparate, "FLETE", priceMode));
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "7001")]);
        settings.ReplaceWithholdingLinks(
            [new SiigoWithholdingLink(ReteFuenteRate, "8001"), new SiigoWithholdingLink(ReteIcaRate, "8002")]);
        settings.UpdatePayments(new SiigoPaymentSettings(
            linkBank ? [new SiigoPaymentLink(BankKey, "9001")] : [],
            CreditPaymentTypeId: "9002",
            CreditDays: 30,
            CashOnDeliveryPaymentTypeId: "9003",
            DefaultPaymentTypeId: "9004"));
        if (linkAdvisor)
        {
            settings.ReplaceSellerLinks([new SiigoSellerLink(Advisor, "629", "ANA GOMEZ")]);
        }

        if (linkVariants)
        {
            var at = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
            settings.LinkVariant(Line1Variant, "SKU-1", "p-1", "Producto uno", SiigoLinkOrigin.Manual, Guid.NewGuid(), at);
            settings.LinkVariant(Line2Variant, "SKU-2", "p-2", "Producto dos", SiigoLinkOrigin.Manual, Guid.NewGuid(), at);
        }

        return settings;
    }

    public static SiigoBilledPartyInput Company() =>
        new(false, SiigoPersonKind.Juridica, SiigoIdentificationKind.Nit, "900.123.456-7", "ACME SAS", "Calle 1 # 2-3", "11001", "11", "ana@acme.test",
            new SiigoPhone("57", "3001234567"));

    /// <summary>Dos líneas: 2 × 50.000 con IVA 19 % y 3 × 33.333,333333 con 10 % de descuento y sin IVA. Productos: 209.000.</summary>
    public static SiigoInvoiceInput Order() =>
        new(
            OrderId: Guid.Parse("0193a1b2-0010-7000-8000-000000000010"),
            OrderNumber: "P-1042",
            Currency: "COP",
            Today: Today,
            CashOnDelivery: false,
            Lines:
            [
                new SiigoInvoiceLineInput(Line1Variant, "Producto uno", 2m, 100000m, 0m, 19, 19000m),
                new SiigoInvoiceLineInput(Line2Variant, "Producto dos", 3m, 90000m, 10m, 0, 0m),
            ],
            FreightSubtotal: 0m,
            FreightTaxAmount: 0m,
            FreightTaxPercentage: 0,
            ProductsTotal: 209000m,
            Total: 209000m,
            RetentionAmount: 0m,
            NetTotal: 209000m,
            Withholdings: [],
            PaymentProofs: [],
            BillingAccountKey: BankKey,
            BuyerNotes: null,
            BilledParty: Company(),
            CompanyId: Guid.Parse("0193a1b2-000b-7000-8000-00000000000b"),
            OrderVersion: 7,
            AdvisorMembershipId: Advisor,
            AdvisorLabel: "Andrea Ritual");

    public static SiigoInvoiceDraft Ready(SiigoBuildResult result, SiigoInvoiceKind kind = SiigoInvoiceKind.Order) =>
        Assert.IsType<SiigoBuildResult.Ready>(result).Drafts.Single(draft => draft.Kind == kind);

    public static JsonDocument Json(SiigoInvoiceDraft draft) => JsonDocument.Parse(draft.Request.Json);
}
