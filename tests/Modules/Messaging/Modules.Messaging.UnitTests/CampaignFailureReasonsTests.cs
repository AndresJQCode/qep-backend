using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2 y la aclaración de E3: <c>failureReason</c> de campañas y destinatarios es texto en español
/// (tuteo colombiano) calculado al leer desde el código; los motivos de omisión tienen su texto fijo y los códigos de Meta se
/// reusan de <see cref="MessageFailureReasons"/>.</summary>
public sealed class CampaignFailureReasonsTests
{
    [Theory]
    [InlineData("opted_out", "No quiere recibir promociones.")]
    [InlineData("inactive", "El cliente está inactivo.")]
    [InlineData("unreachable", "El cliente no tiene WhatsApp.")]
    [InlineData("campaign_cancelled", "La campaña se canceló.")]
    [InlineData("campaign_failed", "La campaña se detuvo.")]
    [InlineData("unknown_outcome", "No se pudo confirmar el envío; no se reintentó para evitar duplicados.")]
    public void ASkippedOrOutcomeCodeHasItsFixedText(string code, string expected) =>
        Assert.Equal(expected, CampaignFailureReasons.For(code));

    [Fact]
    public void AMetaCodeReusesTheMessageText()
    {
        Assert.Equal(MessageFailureReasons.For(131049), CampaignFailureReasons.For("131049"));
        Assert.Equal(MessageFailureReasons.For(132015), CampaignFailureReasons.For("132015"));
        Assert.Equal(MessageFailureReasons.For(MessageFailureReasons.Unconfirmed), CampaignFailureReasons.For(CampaignFailureCodes.Unconfirmed));
    }

    [Fact]
    public void NotSentHasItsOwnText() =>
        Assert.Contains("no salió", CampaignFailureReasons.For(CampaignFailureCodes.NotSent), StringComparison.Ordinal);

    [Fact]
    public void NoCodeIsNoReasonAndAnUnknownNumberIsTheGenericText()
    {
        Assert.Null(CampaignFailureReasons.For(null));
        Assert.Equal(MessageFailureReasons.Generic, CampaignFailureReasons.For("999999"));
    }

    [Fact]
    public void RejectedCredentialsAndAnUnavailableConnectionStopTheCampaignWithTheirOwnText()
    {
        Assert.Contains("credenciales", CampaignFailureReasons.For("190"), StringComparison.Ordinal);
        Assert.Contains("credenciales", CampaignFailureReasons.For("133010"), StringComparison.Ordinal);
        Assert.Contains("conexión", CampaignFailureReasons.For(CampaignFailureCodes.ConnectionUnavailable), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(132001)]
    [InlineData(132015)]
    [InlineData(132016)]
    [InlineData(190)]
    [InlineData(133010)]
    public void TheseGraphCodesStopTheWholeCampaign(int code) =>
        Assert.True(CampaignFailureCodes.StopsTheCampaign(code));

    [Theory]
    [InlineData(131049)]
    [InlineData(131050)]
    [InlineData(130429)]
    [InlineData(131056)]
    [InlineData(100)]
    public void OtherCodesOnlyFailTheRecipient(int code) =>
        Assert.False(CampaignFailureCodes.StopsTheCampaign(code));

    [Theory]
    [InlineData(130429)]
    [InlineData(131056)]
    public void ThroughputAndPairRateAreRetriable(int code) =>
        Assert.True(CampaignFailureCodes.IsRetriable(code));

    [Theory]
    [InlineData(131049)]
    [InlineData(131050)]
    [InlineData(131026)]
    [InlineData(132000)]
    [InlineData(132018)]
    public void TheRestAreNot(int code) =>
        Assert.False(CampaignFailureCodes.IsRetriable(code));

    [Theory]
    [InlineData(CampaignRecipientStatus.Failed, "131049", true)]
    [InlineData(CampaignRecipientStatus.Failed, "unknown_outcome", true)]
    [InlineData(CampaignRecipientStatus.Failed, null, true)]
    [InlineData(CampaignRecipientStatus.Failed, "131050", false)]
    [InlineData(CampaignRecipientStatus.Skipped, "campaign_failed", true)]
    [InlineData(CampaignRecipientStatus.Skipped, "opted_out", false)]
    [InlineData(CampaignRecipientStatus.Skipped, "campaign_cancelled", false)]
    [InlineData(CampaignRecipientStatus.Sent, null, false)]
    public void AManualRetryTakesEveryFailureButTheOptOutAndWhatTheStopLeftUntried(CampaignRecipientStatus status, string? code, bool expected) =>
        Assert.Equal(expected, CampaignFailureCodes.IsRetriableByHand(status, code));
}
