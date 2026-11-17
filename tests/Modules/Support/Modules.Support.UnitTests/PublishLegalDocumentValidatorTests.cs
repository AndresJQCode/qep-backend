using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>The fields of a legal document publication (spec 2026-11-15 §6.4 and §7, plan L5 and L6). The validator gives the field;
/// what only the sanitized HTML can tell (no visible text, more than 60 000) is the handler's, with its own codes.</summary>
public sealed class PublishLegalDocumentValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static List<string> Errors(
        string? kind = "privacy-notice",
        string? content = "<p>Autorizo.</p>",
        string? question = null,
        string? url = null,
        long version = 0) =>
        new PublishLegalDocumentValidator()
            .Validate(PublishLegalDocumentCommand.From(Tenant, kind, new PublishLegalDocumentRequest(content, question, url), version))
            .Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void ContentIsRequiredAndAtMost120000Raw()
    {
        Assert.Empty(Errors(content: new string('a', 120_000)));
        Assert.Equal(["ContentHtml"], Errors(content: new string('a', 120_001)));
        Assert.Equal(["ContentHtml"], Errors(content: null));
        Assert.Equal(["ContentHtml"], Errors(content: "   "));
    }

    [Theory]
    [InlineData("anonymous-notice")]
    [InlineData("terms")]
    public void TheQuestionIsOnlyForThePrivacyNotice(string kind)
    {
        Assert.Equal(["AuthorizationQuestion"], Errors(kind, question: "¿Autorizas?"));
        Assert.Empty(Errors(kind, question: "  "));
        Assert.Empty(Errors(question: "¿Autorizas?"));
    }

    [Fact]
    public void ThePolicyUrlIsOnlyForThePrivacyNoticeAndHttps()
    {
        Assert.Empty(Errors(url: "https://example.com/politica"));
        Assert.Empty(Errors(url: null));
        Assert.Equal(["PolicyUrl"], Errors(url: "http://example.com/politica"));
        Assert.Equal(["PolicyUrl"], Errors(url: "example.com/politica"));
        Assert.Equal(["PolicyUrl"], Errors(url: "https://example.com/" + new string('a', 481)));
        Assert.Equal(["PolicyUrl"], Errors("terms", url: "https://example.com/politica"));
        Assert.Empty(Errors("terms", url: " "));
    }

    [Fact]
    public void TheQuestionIsAtMost500()
    {
        Assert.Empty(Errors(question: new string('a', 500)));
        Assert.Equal(["AuthorizationQuestion"], Errors(question: new string('a', 501)));
        Assert.Equal(["AuthorizationQuestion"], Errors(question: "¿Autorizas\na la empresa?"));
    }

    [Fact]
    public void TheExpectedVersionIsNotNegative()
    {
        Assert.Empty(Errors(version: 0));
        Assert.Equal(["ExpectedVersion"], Errors(version: -1));
    }
}
