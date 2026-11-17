using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// What a requester accepted when filing (spec 2026-11-15 §5.2, plan L13).
public sealed class SupportLegalAcceptanceTests
{
    private const string Ip = "203.0.113.7";
    private const string Question = "¿Autorizas a Novasteel para el tratamiento de los datos personales suministrados en este formulario?";

    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Author = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly SupportTicketId Ticket = new(Guid.Parse("0193a000-0000-7000-8000-0000000000b1"));
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

    private static SupportLegalDocumentVersion Version(
        SupportLegalDocumentKind kind, Guid? tenantId = null, string? url = null) =>
        SupportLegalDocumentVersion.Create(
            Guid.CreateVersion7(), tenantId ?? TenantId, kind, 3, new SanitizedHtml("<p>Texto vigente.</p>", true), null, url, Author,
            Now.AddDays(-1));

    private static SupportLegalAcceptance Record(
        SupportLegalDocumentVersion version,
        string? question = null,
        string? characterization = null,
        string? ip = null,
        string served = "<p>Texto servido.</p>") =>
        SupportLegalAcceptance.Record(Guid.CreateVersion7(), TenantId, Ticket, version, served, question, characterization, ip, Now);

    [Fact]
    public void AnExampleOrAnotherTenantsVersionIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Record(SupportLegalDocumentExamples.For(SupportLegalDocumentKind.Terms, TenantId)));
        Assert.Throws<ArgumentException>(() => Record(Version(SupportLegalDocumentKind.Terms, Guid.CreateVersion7())));
        Assert.Throws<ArgumentException>(() => Record(Version(SupportLegalDocumentKind.Terms), served: " "));
        Assert.Throws<ArgumentException>(() => Record(
            Version(SupportLegalDocumentKind.Terms), served: new string('a', SupportLegalDocumentVersion.ContentMaxLength + 1)));
    }

    [Fact]
    public void APrivacyAcceptanceNeedsIpQuestionAndCharacterization()
    {
        var privacy = Version(SupportLegalDocumentKind.PrivacyNotice);
        var characterization = SupportLegalNotices.Characterization;

        Assert.Throws<ArgumentException>(() => Record(privacy, Question, characterization, ip: null));
        Assert.Throws<ArgumentException>(() => Record(privacy, question: null, characterization, Ip));
        Assert.Throws<ArgumentException>(() => Record(privacy, Question, characterization: null, Ip));
        // An over-long IP is no longer rejected: it is cut to the column (final review N3, ALongerIpIsCutToTheColumn).

        var accepted = Record(privacy, Question, characterization, Ip);

        Assert.Equal(SupportLegalDocumentKind.PrivacyNotice, accepted.Kind);
        Assert.Equal(Question, accepted.AuthorizationQuestion);
        Assert.Equal(characterization, accepted.CharacterizationText);
        Assert.Equal(Ip, accepted.VisitorIp);
    }

    [Fact]
    public void AnAnonymousNoticeAcceptanceRejectsAnIp()
    {
        var notice = Version(SupportLegalDocumentKind.AnonymousNotice);

        Assert.Throws<ArgumentException>(() => Record(notice, ip: Ip));
        Assert.Throws<ArgumentException>(() => Record(notice, question: Question));
        Assert.Throws<ArgumentException>(() => Record(notice, characterization: SupportLegalNotices.Characterization));

        var accepted = Record(notice);

        Assert.Equal(SupportLegalDocumentKind.AnonymousNotice, accepted.Kind);
        Assert.Null(accepted.VisitorIp);
        Assert.Null(accepted.AuthorizationQuestion);
        Assert.Null(accepted.CharacterizationText);
    }

    // Final review N3: an IPv6 with a scope id can pass 45 characters; it is cut to the column, like BuyerSession, never a failed filing.
    [Fact]
    public void ALongerIpIsCutToTheColumn()
    {
        const string scoped = "fe80:0000:0000:0000:0204:61ff:fe9d:f156%ethernet-adapter-12";
        var privacy = Version(SupportLegalDocumentKind.PrivacyNotice);

        var accepted = Record(privacy, Question, SupportLegalNotices.Characterization, " " + scoped + " ");

        Assert.Equal(SupportLegalAcceptance.VisitorIpMaxLength, accepted.VisitorIp!.Length);
        Assert.Equal(scoped[..SupportLegalAcceptance.VisitorIpMaxLength], accepted.VisitorIp);
        Assert.Equal(Ip, Record(Version(SupportLegalDocumentKind.Terms), ip: Ip).VisitorIp);
    }

    [Fact]
    public void TermsTakeAnIpOrNone()
    {
        var terms = Version(SupportLegalDocumentKind.Terms);

        Assert.Equal(Ip, Record(terms, ip: Ip).VisitorIp);
        Assert.Null(Record(terms).VisitorIp);
        Assert.Throws<ArgumentException>(() => Record(terms, question: Question));
        Assert.Throws<ArgumentException>(() => Record(terms, characterization: SupportLegalNotices.Characterization));
    }

    [Fact]
    public void ItKeepsTheServedHtmlAndCopiesThePolicyUrl()
    {
        var privacy = Version(SupportLegalDocumentKind.PrivacyNotice, url: "https://example.com/politica");

        var accepted = Record(privacy, Question, SupportLegalNotices.Characterization, Ip, served: "<p>Lo que se sirvió.</p>");

        Assert.Equal("<p>Lo que se sirvió.</p>", accepted.ContentHtml);
        Assert.Equal("https://example.com/politica", accepted.PolicyUrl);
        Assert.Equal(privacy.Id, accepted.DocumentVersionId);
        Assert.Equal(3, accepted.DocumentVersion);
        Assert.Equal(Ticket, accepted.TicketId);
        Assert.Equal(TenantId, accepted.TenantId);
        Assert.Equal(Now, accepted.AcceptedAt);
    }
}
