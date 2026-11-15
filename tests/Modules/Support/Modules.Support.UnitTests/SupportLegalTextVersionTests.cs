using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Los textos legales versionados y el consentimiento que los copia (spec §9.5, plan F16 y F17).
public sealed class SupportLegalTextVersionTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Author = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static SupportLegalTextVersion Create(
        string authorization = "Autorizo el tratamiento de mis datos.", string? policyText = null,
        string? policyUrl = "https://example.com/politica", string? termsText = null) =>
        SupportLegalTextVersion.Create(
            Guid.CreateVersion7(), TenantId, 1, authorization, policyText, policyUrl, termsText, Author, Now);

    [Fact]
    public void ItNeedsAuthorizationAndAPolicyTextOrUrl()
    {
        Assert.Throws<ArgumentException>(() => Create(authorization: "  "));
        var missing = Assert.Throws<SupportDomainException>(() => Create(policyText: " ", policyUrl: null));

        Assert.Equal(SupportErrorCodes.LegalTextsPolicyRequired, missing.Code);
        Assert.Equal("support.legal_texts.policy_required", missing.Code);
        Assert.Equal("Política", Create(policyText: " Política ", policyUrl: null).PolicyText);
    }

    [Theory]
    [InlineData("http://example.com/politica")]
    [InlineData("example.com/politica")]
    [InlineData("ftp://example.com/politica")]
    public void APolicyUrlMustBeHttps(string url) => Assert.Throws<ArgumentException>(() => Create(policyUrl: url));

    [Fact]
    public void TheTextsHaveTheirLimits()
    {
        var tooLong = new string('a', SupportLegalTextVersion.TextMaxLength + 1);

        Assert.Throws<ArgumentException>(() => Create(authorization: tooLong));
        Assert.Throws<ArgumentException>(() => Create(policyText: tooLong));
        Assert.Throws<ArgumentException>(() => Create(termsText: tooLong));
        Assert.Throws<ArgumentException>(() => Create(policyUrl: "https://example.com/" + new string('a', 500)));
    }

    [Fact]
    public void TheExampleIsVersionZeroAndValid()
    {
        var example = SupportLegalTextVersion.Example(TenantId);

        Assert.True(example.IsExample);
        Assert.Equal(0, example.Version);
        Assert.False(string.IsNullOrWhiteSpace(example.AuthorizationText));
        Assert.True(example.PolicyText is not null || example.PolicyUrl is not null);
        Assert.False(Create().IsExample);
    }

    [Fact]
    public void AConsentCopiesTheTextTheRequesterSaw()
    {
        var text = Create(policyText: "Política v1", termsText: "Términos v1");
        var ticket = SupportTicketId.New();

        var consent = SupportConsent.Record(Guid.CreateVersion7(), TenantId, ticket, text, "203.0.113.7", Now);

        Assert.Equal(ticket, consent.TicketId);
        Assert.Equal(text.Id, consent.LegalTextVersionId);
        Assert.Equal(1, consent.LegalTextVersion);
        Assert.Equal(text.AuthorizationText, consent.AuthorizationText);
        Assert.Equal("Política v1", consent.PolicyText);
        Assert.Equal(text.PolicyUrl, consent.PolicyUrl);
        Assert.Equal("Términos v1", consent.TermsText);
        Assert.Equal("203.0.113.7", consent.VisitorIp);
        Assert.Equal(Now, consent.AcceptedAt);
    }

    [Fact]
    public void AConsentNeedsAPublishedTextOfTheTenantAndAnIp()
    {
        var ticket = SupportTicketId.New();

        Assert.Throws<ArgumentException>(() => SupportConsent.Record(
            Guid.CreateVersion7(), TenantId, ticket, SupportLegalTextVersion.Example(TenantId), "203.0.113.7", Now));
        Assert.Throws<ArgumentException>(() => SupportConsent.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ticket, Create(), "203.0.113.7", Now));
        Assert.Throws<ArgumentException>(() => SupportConsent.Record(Guid.CreateVersion7(), TenantId, ticket, Create(), " ", Now));
        Assert.Throws<ArgumentException>(() => SupportConsent.Record(
            Guid.CreateVersion7(), TenantId, ticket, Create(), new string('1', SupportConsent.VisitorIpMaxLength + 1), Now));
    }
}
