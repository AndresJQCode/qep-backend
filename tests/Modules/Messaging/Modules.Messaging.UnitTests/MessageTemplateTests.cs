using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.2: el espejo de una plantilla de Meta. Pending/Mirrored, ApplyStatus (mayúsculas, «NONE» = sin
/// motivo, recorte al ancho de la columna), SyncFrom conserva kind, AdoptAsQuotation sólo desde External, IsLive y las reglas del nombre.</summary>
public sealed class MessageTemplateTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private static MessageTemplate Pending(MessageTemplateKind kind = MessageTemplateKind.Free, string name = "promo_octubre", string? status = "pending") =>
        MessageTemplate.Pending(TenantId, ConnectionId, "900", name, "es", "MARKETING", "NAMED", status, "[]", kind, Guid.CreateVersion7(), Now);

    private static MessageTemplate External(string name = "externa", string? status = "APPROVED", string? reason = null) =>
        MessageTemplate.Mirrored(TenantId, ConnectionId, "901", name, "es", "UTILITY", "POSITIONAL", status, reason, "[]", Now);

    [Fact]
    public void PendingNormalizesTheStatusAndKeepsTheKind()
    {
        var template = Pending(MessageTemplateKind.Quotation, MessageTemplateName.QuotationName, " pending ");

        Assert.Equal(MessageTemplateStatuses.Pending, template.Status);
        Assert.Equal(MessageTemplateKind.Quotation, template.Kind);
        Assert.Null(template.StatusReason);
        Assert.Null(template.SyncedAt);
        Assert.True(template.IsLive);
        Assert.False(template.IsApproved);
        Assert.Equal((Now, Now), (template.CreatedAt, template.UpdatedAt));
    }

    [Fact]
    public void PendingWithTheExternalKindIsAProgrammingError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Pending(MessageTemplateKind.External));

    [Fact]
    public void AFreeTemplateCannotTakeTheReservedQuotationName()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => Pending(MessageTemplateKind.Free, MessageTemplateName.QuotationName));
        Assert.Equal(MessagingErrorCodes.TemplateNameReserved, exception.Code);
    }

    [Fact]
    public void AFreeTemplateWithAnInvalidNameIsRejected()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => Pending(MessageTemplateKind.Free, "Promo Octubre"));
        Assert.Equal(MessagingErrorCodes.TemplateNameInvalid, exception.Code);
    }

    [Fact]
    public void MirroredIsExternalAlreadySyncedAndTakesMetaNamesAsTheyCome()
    {
        var template = External("Nombre-Con-Mayúsculas", "approved", "none");

        Assert.Equal(MessageTemplateKind.External, template.Kind);
        Assert.Equal(MessageTemplateStatuses.Approved, template.Status);
        Assert.Null(template.StatusReason);
        Assert.Equal(Now, template.SyncedAt);
        Assert.Null(template.CreatedByMemberId);
    }

    [Fact]
    public void ApplyStatusUppercasesAndKeepsTheReason()
    {
        var template = Pending();
        var later = Now.AddMinutes(5);

        template.ApplyStatus("rejected", "INVALID_FORMAT", later);

        Assert.Equal((MessageTemplateStatuses.Rejected, "INVALID_FORMAT", later), (template.Status, template.StatusReason, template.UpdatedAt));
        Assert.False(template.IsLive);
    }

    // Review Focus 2: Meta manda reason = "NONE" en una aprobación; no es un motivo.
    [Fact]
    public void ApplyStatusDropsANoneReason()
    {
        var template = Pending();
        template.ApplyStatus("REJECTED", "SCAM", Now);

        template.ApplyStatus("APPROVED", "NONE", Now.AddMinutes(1));

        Assert.Null(template.StatusReason);
        Assert.True(template.IsApproved);
    }

    [Fact]
    public void ApplyStatusTruncatesWhatDoesNotFitTheColumns()
    {
        var template = Pending();

        template.ApplyStatus(new string('x', 40), new string('r', 80), Now);

        Assert.Equal(32, template.Status.Length);
        Assert.Equal(64, template.StatusReason!.Length);
    }

    [Fact]
    public void AnEmptyStatusIsUnknownNotAnException()
    {
        var template = Pending(status: null);
        Assert.Equal(MessageTemplateStatuses.Unknown, template.Status);
    }

    [Fact]
    public void SyncFromKeepsTheKindAndStampsSyncedAt()
    {
        var template = Pending(MessageTemplateKind.Quotation, MessageTemplateName.QuotationName);
        var later = Now.AddHours(1);

        template.SyncFrom("APPROVED", null, "UTILITY", "NAMED", """[{"type":"BODY","text":"Hola"}]""", later);

        Assert.Equal(MessageTemplateKind.Quotation, template.Kind);
        Assert.Equal((MessageTemplateStatuses.Approved, later, later), (template.Status, template.SyncedAt, template.UpdatedAt));
        Assert.Equal("""[{"type":"BODY","text":"Hola"}]""", template.ComponentsJson);
    }

    [Fact]
    public void AdoptAsQuotationOnlyWorksOnAnExternalTemplate()
    {
        var external = External(MessageTemplateName.QuotationName);
        external.AdoptAsQuotation(Now.AddMinutes(1));
        Assert.Equal(MessageTemplateKind.Quotation, external.Kind);

        var free = Pending();
        var exception = Assert.Throws<MessagingDomainException>(() => free.AdoptAsQuotation(Now));
        Assert.Equal(MessagingErrorCodes.TemplateQuotationConflict, exception.Code);
    }

    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("APPROVED", true)]
    [InlineData("IN_APPEAL", true)]
    [InlineData("REJECTED", false)]
    [InlineData("PAUSED", false)]
    [InlineData("DISABLED", false)]
    [InlineData("LIMIT_EXCEEDED", false)]
    [InlineData("PENDING_DELETION", false)]
    [InlineData("UNKNOWN", false)]
    public void IsLiveIsPendingApprovedOrInAppeal(string status, bool live) => Assert.Equal(live, MessageTemplateStatuses.IsLive(status));

    [Theory]
    [InlineData("promo_octubre", true)]
    [InlineData("a", true)]
    [InlineData("promo-octubre", false)]
    [InlineData("Promo", false)]
    [InlineData("promo octubre", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheNameTakesLowercaseDigitsAndUnderscores(string? name, bool valid) => Assert.Equal(valid, MessageTemplateName.IsValid(name));

    [Fact]
    public void ANameLongerThan512IsInvalid() => Assert.False(MessageTemplateName.IsValid(new string('a', 513)));

    [Theory]
    [InlineData("qep_cotizacion", true)]
    [InlineData("qep_cotizacion_v2", true)]
    [InlineData("qep_cotizaciones", false)]
    [InlineData("cotizacion", false)]
    public void TheQuotationNameAndItsPrefixAreReserved(string name, bool reserved) => Assert.Equal(reserved, MessageTemplateName.IsReserved(name));
}
