using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>El texto de base de cada enum (spec 2026-10-08 §3): minúscula, como los CHECK.</summary>
public sealed class TenantChangeVocabularyTests
{
    [Theory]
    [InlineData(ChangeReason.Contract, "contract")]
    [InlineData(ChangeReason.Courtesy, "courtesy")]
    [InlineData(ChangeReason.Nonpayment, "nonpayment")]
    [InlineData(ChangeReason.Cancellation, "cancellation")]
    [InlineData(ChangeReason.Correction, "correction")]
    public void ReasonsRoundTrip(ChangeReason reason, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(reason));
        Assert.Equal(reason, TenantChangeVocabulary.ParseReason(text));
    }

    [Theory]
    [InlineData(TenantModuleStatus.Active, "active")]
    [InlineData(TenantModuleStatus.Inactive, "inactive")]
    public void ModuleStatusesRoundTrip(TenantModuleStatus status, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(status));
        Assert.Equal(status, TenantChangeVocabulary.ParseModuleStatus(text));
    }

    [Theory]
    [InlineData(TenantChangeKind.Module, "module")]
    [InlineData(TenantChangeKind.TenantStatus, "tenant_status")]
    public void KindsRoundTrip(TenantChangeKind kind, string text)
    {
        Assert.Equal(text, TenantChangeVocabulary.ToText(kind));
        Assert.Equal(kind, TenantChangeVocabulary.ParseKind(text));
    }

    // Igual que el CHECK: sin ignorar mayúsculas.
    [Theory]
    [InlineData("Contract")]
    [InlineData("refund")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownReasonDoesNotParse(string? text)
    {
        Assert.False(TenantChangeVocabulary.TryParseReason(text, out _));
        if (text is not null)
        {
            Assert.Throws<ArgumentException>(() => TenantChangeVocabulary.ParseReason(text));
        }
    }
}
