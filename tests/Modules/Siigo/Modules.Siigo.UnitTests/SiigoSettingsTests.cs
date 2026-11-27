using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.2: la configuración de una conexión. Toda escritura sube la versión (es el <c>If-Match</c> de los
/// <c>PUT</c>), y una configuración nueva sale con la DIAN apagada.</summary>
public sealed class SiigoSettingsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static SiigoSettings NewSettings() => SiigoSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());

    private static SiigoDocumentSettings Document(bool sendToDian = false) =>
        new("doc-1", null, null, sendToDian, false, false, null);

    [Fact]
    public void ANewSettingsHasDianOffAndEverythingElseEmpty()
    {
        var settings = NewSettings();

        Assert.False(settings.SendToDian);
        Assert.False(settings.SendMail);
        Assert.False(settings.FreightSeparateInvoice);
        Assert.Equal(0, settings.Version);
        Assert.Null(settings.CompanyId);
        Assert.Null(settings.DocumentTypeId);
        Assert.Null(settings.CashOnDeliveryPaymentTypeId);
        Assert.Empty(settings.VariantLinks);
        Assert.Empty(settings.TaxLinks);
        Assert.Empty(settings.WithholdingLinks);
        Assert.Empty(settings.PaymentLinks);
    }

    [Fact]
    public void APriceModeCreatedByDefaultIsBaseWithDiscount()
    {
        Assert.Equal(SiigoPriceMode.BaseWithDiscount, NewSettings().PriceMode);
    }

    [Fact]
    public void UpdateDocumentKeepsThePriceModeWhenItIsNull()
    {
        var settings = NewSettings();
        settings.UpdateDocument(Document() with { PriceMode = SiigoPriceMode.DiscountedPrice });

        settings.UpdateDocument(Document() with { PriceMode = null });

        Assert.Equal(SiigoPriceMode.DiscountedPrice, settings.PriceMode);
    }

    [Fact]
    public void UpdateDocumentChangesThePriceMode()
    {
        var settings = NewSettings();
        var before = settings.Version;

        settings.UpdateDocument(Document() with { PriceMode = SiigoPriceMode.DiscountedPrice });

        Assert.Equal(SiigoPriceMode.DiscountedPrice, settings.PriceMode);
        Assert.Equal(before + 1, settings.Version);
    }

    [Fact]
    public void EveryWriteBumpsTheVersion()
    {
        var settings = NewSettings();
        var variantId = Guid.CreateVersion7();

        settings.AssignCompany(Guid.CreateVersion7());
        Assert.Equal(1, settings.Version);
        settings.UpdateDocument(Document());
        Assert.Equal(2, settings.Version);
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "tax-19")]);
        Assert.Equal(3, settings.Version);
        settings.ReplaceWithholdingLinks([new SiigoWithholdingLink(Guid.CreateVersion7(), "tax-rf")]);
        Assert.Equal(4, settings.Version);
        settings.UpdatePayments(new SiigoPaymentSettings([new SiigoPaymentLink("BANCO|1", "pay-1")], "credit", 30, "cod", "default"));
        Assert.Equal(5, settings.Version);
        settings.LinkVariant(variantId, "AB-1", "p-1", "Producto", SiigoLinkOrigin.Manual, Guid.CreateVersion7(), At);
        Assert.Equal(6, settings.Version);
        settings.UnlinkVariant(variantId);
        Assert.Equal(7, settings.Version);
    }

    [Fact]
    public void ADocumentUpdateKeepsTheSwitchesTheCallerChose()
    {
        var settings = NewSettings();

        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", "cc-1", "wh-1", true, true, true, "FLETE"));

        Assert.True(settings.SendToDian);
        Assert.True(settings.SendMail);
        Assert.True(settings.FreightSeparateInvoice);
        Assert.Equal("FLETE", settings.FreightProductCode);
        Assert.Equal("cc-1", settings.CostCenterId);
        Assert.Equal("wh-1", settings.WarehouseId);
    }

    [Fact]
    public void ReplacingTaxLinksKeepsOneLinkPerPercentage()
    {
        var settings = NewSettings();
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "a"), new SiigoTaxLink(5, "b")]);

        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "c"), new SiigoTaxLink(0, null)]);

        Assert.Equal([0, 19], settings.TaxLinks.Select(link => link.TaxPercentage).Order());
        Assert.Equal("c", settings.TaxLinks.Single(link => link.TaxPercentage == 19).SiigoTaxId);
        Assert.Null(settings.TaxLinks.Single(link => link.TaxPercentage == 0).SiigoTaxId);
    }

    [Fact]
    public void ADuplicatedPercentageIsRejected()
    {
        var settings = NewSettings();

        var error = Assert.Throws<SiigoDomainException>(
            () => settings.ReplaceTaxLinks([new SiigoTaxLink(19, "a"), new SiigoTaxLink(19, "b")]));

        Assert.Equal(SiigoErrorCodes.SettingsDuplicateLink, error.Code);
    }

    [Fact]
    public void ADuplicatedAccountIsRejected()
    {
        var settings = NewSettings();

        var error = Assert.Throws<SiigoDomainException>(() => settings.UpdatePayments(
            new SiigoPaymentSettings([new SiigoPaymentLink("K", "a"), new SiigoPaymentLink("K", "b")], null, null, null, null)));

        Assert.Equal(SiigoErrorCodes.SettingsDuplicateLink, error.Code);
    }

    [Fact]
    public void ManualLinkReplacesAnAutoLink()
    {
        var settings = NewSettings();
        var variantId = Guid.CreateVersion7();
        var autoBy = Guid.CreateVersion7();
        var manualBy = Guid.CreateVersion7();
        settings.LinkVariant(variantId, "AB-1", "p-1", "Uno", SiigoLinkOrigin.AutoCode, autoBy, At);

        settings.LinkVariant(variantId, "AB-9", "p-9", "Nueve", SiigoLinkOrigin.Manual, manualBy, At.AddMinutes(1));

        var link = Assert.Single(settings.VariantLinks);
        Assert.Equal("p-9", link.SiigoProductId);
        Assert.Equal("AB-9", link.SiigoProductCode);
        Assert.Equal(SiigoLinkOrigin.Manual, link.Origin);
        Assert.Equal(manualBy, link.LinkedBy);
        Assert.Equal(At.AddMinutes(1), link.LinkedAt);
    }

    [Fact]
    public void UnlinkingAVariantWithoutLinkChangesNothing()
    {
        var settings = NewSettings();

        settings.UnlinkVariant(Guid.CreateVersion7());

        Assert.Equal(0, settings.Version);
    }

    [Fact]
    public void ACreditTermNeedsItsPaymentType()
    {
        var settings = NewSettings();

        var error = Assert.Throws<SiigoDomainException>(
            () => settings.UpdatePayments(new SiigoPaymentSettings([], null, 30, null, null)));

        Assert.Equal(SiigoErrorCodes.SettingsCreditInvalid, error.Code);
    }

    [Fact]
    public void ReplaceSellerLinksReplacesTheStepAndSellerForFindsIt()
    {
        var settings = NewSettings();
        var vanesa = Guid.CreateVersion7();
        var andrea = Guid.CreateVersion7();
        settings.ReplaceSellerLinks([new SiigoSellerLink(vanesa, "800", "VANESA HERNANDEZ"), new SiigoSellerLink(andrea, "744", "ANDREA RITUAL")]);
        var version = settings.Version;

        settings.ReplaceSellerLinks([new SiigoSellerLink(andrea, " 745 ", "ANDREA NUEVA")]);

        Assert.Null(settings.SellerFor(vanesa));
        Assert.Equal("745", settings.SellerFor(andrea));
        var link = Assert.Single(settings.SellerLinks);
        Assert.Equal((settings.ConnectionId, settings.TenantId, "ANDREA NUEVA"), (link.ConnectionId, link.TenantId, link.SiigoUserName));
        Assert.Equal(version + 1, settings.Version);
    }

    [Fact]
    public void ASellerLinkClampsTheNameAndFallsBackToTheId()
    {
        var settings = NewSettings();
        var long250 = new string('N', 250);
        var member = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        settings.ReplaceSellerLinks([new SiigoSellerLink(member, "800", long250), new SiigoSellerLink(other, "744", "   ")]);

        Assert.Equal(SiigoSellerLink.UserNameMaxLength, settings.SellerLinks.Single(l => l.MembershipId == member).SiigoUserName.Length);
        Assert.Equal("744", settings.SellerLinks.Single(l => l.MembershipId == other).SiigoUserName);

        settings.ReplaceSellerLinks([new SiigoSellerLink(member, "800", ""), new SiigoSellerLink(other, "744", long250)]);

        Assert.Equal("800", settings.SellerLinks.Single(l => l.MembershipId == member).SiigoUserName);
        Assert.Equal(SiigoSellerLink.UserNameMaxLength, settings.SellerLinks.Single(l => l.MembershipId == other).SiigoUserName.Length);
    }

    [Fact]
    public void ARepeatedMemberIsADuplicateLink()
    {
        var member = Guid.CreateVersion7();

        var error = Assert.Throws<SiigoDomainException>(
            () => NewSettings().ReplaceSellerLinks([new SiigoSellerLink(member, "800", "A"), new SiigoSellerLink(member, "744", "B")]));

        Assert.Equal(SiigoErrorCodes.SettingsDuplicateLink, error.Code);
    }

    [Fact]
    public void MarkingTheAutoLinkKeepsTheDateWithoutBumpingTheVersion()
    {
        var settings = NewSettings();

        settings.MarkSellersAutoLinked(At);
        settings.MarkProductsAutoLinked(At.AddMinutes(1));

        Assert.Equal((At, At.AddMinutes(1)), (settings.SellersAutoLinkedAt, settings.ProductsAutoLinkedAt));
        Assert.Equal(0, settings.Version);
    }

    private static SiigoSettings ConfiguredSettings()
    {
        var settings = NewSettings();
        settings.AssignCompany(Guid.CreateVersion7());
        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", "cc-1", "wh-1", true, true, true, "FLETE", SiigoPriceMode.DiscountedPrice));
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "tax-19")]);
        settings.ReplaceWithholdingLinks([new SiigoWithholdingLink(Guid.CreateVersion7(), "tax-rf")]);
        settings.UpdatePayments(new SiigoPaymentSettings(
            [new SiigoPaymentLink("BANCO|1", "pay-1"), new SiigoPaymentLink("BANCO|2", null)], "credit", 30, "cod", "default"));
        settings.ReplaceSellerLinks([new SiigoSellerLink(Guid.CreateVersion7(), "u-1", "Ana")]);
        settings.LinkVariant(Guid.CreateVersion7(), "AB-1", "p-1", "Producto", SiigoLinkOrigin.Manual, Guid.CreateVersion7(), At);
        settings.MarkSellersAutoLinked(At);
        settings.MarkProductsAutoLinked(At);
        return settings;
    }

    [Fact]
    public void SavedPaymentTypeIdsListsEveryDistinctSavedIdAndSkipsTheEmptyOnes()
    {
        var settings = ConfiguredSettings();

        Assert.Equal(["pay-1", "credit", "cod", "default"], settings.SavedPaymentTypeIds());
    }

    [Fact]
    public void ResetForAnotherAccountClearsTheAccountDataAndKeepsTheCompanyAndTheSwitches()
    {
        var settings = ConfiguredSettings();
        var companyId = settings.CompanyId;
        var before = settings.Version;

        settings.ResetForAnotherAccount();

        Assert.Null(settings.DocumentTypeId);
        Assert.Null(settings.CostCenterId);
        Assert.Null(settings.WarehouseId);
        Assert.Null(settings.FreightProductCode);
        Assert.Null(settings.CreditPaymentTypeId);
        Assert.Null(settings.CreditDays);
        Assert.Null(settings.CashOnDeliveryPaymentTypeId);
        Assert.Null(settings.DefaultPaymentTypeId);
        Assert.Empty(settings.TaxLinks);
        Assert.Empty(settings.WithholdingLinks);
        Assert.Empty(settings.PaymentLinks);
        Assert.Empty(settings.SellerLinks);
        Assert.Empty(settings.VariantLinks);
        Assert.Null(settings.SellersAutoLinkedAt);
        Assert.Null(settings.ProductsAutoLinkedAt);
        Assert.Equal(companyId, settings.CompanyId);
        Assert.True(settings.SendToDian);
        Assert.True(settings.SendMail);
        Assert.True(settings.FreightSeparateInvoice);
        Assert.Equal(SiigoPriceMode.DiscountedPrice, settings.PriceMode);
        Assert.Equal(before + 1, settings.Version);
    }

    [Fact]
    public void AfterTheResetTheDocumentTaxesPaymentsAndSellersStepsAreIncomplete()
    {
        var settings = ConfiguredSettings();

        settings.ResetForAnotherAccount();

        var incomplete = SiigoSettingsReadiness.Evaluate(settings).Where(step => !step.Complete).Select(step => step.Step);
        Assert.Equal(
            [SiigoSetupStep.Document, SiigoSetupStep.Taxes, SiigoSetupStep.Payments, SiigoSetupStep.Sellers],
            incomplete);
    }
}
