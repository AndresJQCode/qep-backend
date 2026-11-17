using Modules.Support.Domain;
using Modules.Support.Infrastructure.Legal;

namespace Modules.Support.UnitTests;

// The versioned legal documents of the portal (spec 2026-11-15 §5.1 and §6.4, plan L1, L5, L7 and L9).
public sealed class SupportLegalDocumentVersionTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Author = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
    private static readonly SanitizedHtml Content = new("<p>Autorizo el tratamiento de mis datos.</p>", true);

    private static SupportLegalDocumentVersion Create(
        SupportLegalDocumentKind kind = SupportLegalDocumentKind.PrivacyNotice,
        int version = 1,
        SanitizedHtml? content = null,
        string? question = null,
        string? url = null) =>
        SupportLegalDocumentVersion.Create(
            Guid.CreateVersion7(), TenantId, kind, version, content ?? Content, question, url, Author, Now);

    [Fact]
    public void VersionStartsAtOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: -1));

        var created = Create(SupportLegalDocumentKind.Terms);

        Assert.Equal(1, created.Version);
        Assert.Equal(SupportLegalDocumentKind.Terms, created.Kind);
        Assert.Equal(Content.Html, created.ContentHtml);
        Assert.Equal(Author, created.CreatedBy);
        Assert.Equal(Now, created.CreatedAt);
        Assert.False(created.IsExample);
    }

    [Fact]
    public void EmptyVisibleTextIsContentEmpty()
    {
        var exception = Assert.Throws<SupportDomainException>(() => Create(content: new SanitizedHtml("<p></p>", false)));

        Assert.Equal(SupportErrorCodes.LegalDocumentsContentEmpty, exception.Code);
        Assert.Equal("support.legal_documents.content_empty", exception.Code);
    }

    [Fact]
    public void MoreThan60000IsContentTooLong()
    {
        var limit = "<p>" + new string('a', SupportLegalDocumentVersion.ContentMaxLength - 7) + "</p>";
        var tooLong = "<p>" + new string('a', SupportLegalDocumentVersion.ContentMaxLength - 6) + "</p>";

        var exception = Assert.Throws<SupportDomainException>(() => Create(content: new SanitizedHtml(tooLong, true)));

        Assert.Equal(60_000, SupportLegalDocumentVersion.ContentMaxLength);
        Assert.Equal(SupportErrorCodes.LegalDocumentsContentTooLong, exception.Code);
        Assert.Equal("support.legal_documents.content_too_long", exception.Code);
        Assert.Equal(limit, Create(content: new SanitizedHtml(limit, true)).ContentHtml);
    }

    [Theory]
    [InlineData(SupportLegalDocumentKind.AnonymousNotice)]
    [InlineData(SupportLegalDocumentKind.Terms)]
    public void QuestionAndUrlOnlyOnThePrivacyNotice(SupportLegalDocumentKind kind)
    {
        Assert.Throws<ArgumentException>(() => Create(kind, question: "¿Autorizas?"));
        Assert.Throws<ArgumentException>(() => Create(kind, url: "https://example.com/politica"));

        var blank = Create(kind, question: "  ", url: " ");
        var privacy = Create(question: "¿Autorizas?", url: "https://example.com/politica");

        Assert.Null(blank.AuthorizationQuestion);
        Assert.Null(blank.PolicyUrl);
        Assert.Equal("¿Autorizas?", privacy.AuthorizationQuestion);
        Assert.Equal("https://example.com/politica", privacy.PolicyUrl);
    }

    [Theory]
    [InlineData("http://example.com/politica")]
    [InlineData("example.com/politica")]
    [InlineData("ftp://example.com/politica")]
    [InlineData("javascript:alert(1)")]
    public void ThePolicyUrlMustBeHttps(string url)
    {
        Assert.False(SupportLegalDocumentVersion.IsValidPolicyUrl(url));
        Assert.Throws<ArgumentException>(() => Create(url: url));
        Assert.Throws<ArgumentException>(() => Create(url: "https://example.com/" + new string('a', 500)));
        Assert.Equal("https://example.com/politica", Create(url: " https://example.com/politica ").PolicyUrl);
    }

    [Fact]
    public void TheQuestionIsTrimmedAndAtMost500()
    {
        var limit = new string('a', SupportLegalDocumentVersion.AuthorizationQuestionMaxLength);

        Assert.Equal("¿Autorizas?", Create(question: "  ¿Autorizas?  ").AuthorizationQuestion);
        Assert.Equal(limit, Create(question: limit).AuthorizationQuestion);
        Assert.Throws<ArgumentException>(() => Create(question: limit + "a"));
        Assert.Throws<ArgumentException>(() => Create(question: "¿Autorizas\na la empresa?"));
        Assert.Throws<ArgumentException>(() => Create(question: "¿Autorizas\ta la empresa?"));
    }

    // Spec 2026-11-16 (D-F8): un token que no está en la lista no se publica, ni en el HTML ni en la pregunta; los conocidos se guardan
    // como token, sin resolver.
    [Fact]
    public void AnUnknownVariableIsRejected()
    {
        var inContent = Assert.Throws<SupportDomainException>(
            () => Create(SupportLegalDocumentKind.Terms, content: new SanitizedHtml("<p>{{empresa}} en {{ciudad}}</p>", true)));
        var inQuestion = Assert.Throws<SupportDomainException>(() => Create(question: "¿Autorizas a {{Empresa}}?"));

        Assert.Equal(SupportErrorCodes.LegalDocumentUnknownVariable, inContent.Code);
        Assert.Equal("support.legal_document.unknown_variable", inContent.Code);
        Assert.Equal(SupportErrorCodes.LegalDocumentUnknownVariable, inQuestion.Code);

        var known = Create(content: new SanitizedHtml("<p>{{ empresa }} ({{tipo_documento}})</p>", true), question: "¿Autorizas a {{empresa}}?");
        Assert.Equal("<p>{{ empresa }} ({{tipo_documento}})</p>", known.ContentHtml);
        Assert.Equal("¿Autorizas a {{empresa}}?", known.AuthorizationQuestion);
    }

    [Theory]
    [InlineData(SupportLegalDocumentKind.PrivacyNotice)]
    [InlineData(SupportLegalDocumentKind.AnonymousNotice)]
    [InlineData(SupportLegalDocumentKind.Terms)]
    public void EachKindHasAnExampleAtVersionZero(SupportLegalDocumentKind kind)
    {
        var example = SupportLegalDocumentExamples.For(kind, TenantId);
        var sanitized = new HtmlSupportSanitizer().Sanitize(example.ContentHtml);

        Assert.True(example.IsExample);
        Assert.Equal(0, example.Version);
        Assert.Equal(kind, example.Kind);
        Assert.Equal(TenantId, example.TenantId);
        Assert.Null(example.AuthorizationQuestion);
        Assert.Null(example.PolicyUrl);
        Assert.Equal(Guid.Empty, example.CreatedBy);
        Assert.Equal(example.ContentHtml, sanitized.Html);
        Assert.True(sanitized.HasVisibleText);
    }

    [Fact]
    public void TheDefaultQuestionNamesTheTenant()
    {
        Assert.Equal(
            "¿Autorizas a Novasteel S.A.S. para el tratamiento de los datos personales suministrados en este formulario?",
            SupportLegalNotices.DefaultAuthorizationQuestion(" Novasteel S.A.S. "));
        Assert.Throws<ArgumentException>(() => SupportLegalNotices.DefaultAuthorizationQuestion(" "));
        Assert.Equal(
            "Los datos solicitados en este formulario serán tratados para caracterizar a los usuarios de la Entidad y procurar una mejora continua en la atención.",
            SupportLegalNotices.Characterization);
    }

    [Theory]
    [InlineData("privacy-notice", SupportLegalDocumentKind.PrivacyNotice)]
    [InlineData("anonymous-notice", SupportLegalDocumentKind.AnonymousNotice)]
    [InlineData("terms", SupportLegalDocumentKind.Terms)]
    public void RouteSegmentsRoundTrip(string segment, SupportLegalDocumentKind kind)
    {
        Assert.True(SupportLegalDocumentKinds.TryParseRoute(segment, out var parsed));
        Assert.Equal(kind, parsed);
        Assert.Equal(segment, SupportLegalDocumentKinds.ToRoute(kind));

        foreach (var wrong in new[] { "privacy_notice", "PrivacyNotice", "Terms", "TERMS", "", " terms", null })
        {
            Assert.False(SupportLegalDocumentKinds.TryParseRoute(wrong, out _));
        }
    }
}
