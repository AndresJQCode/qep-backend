using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-30 §6.3.1: una prueba por fila de la tabla de destinatarios.</summary>
public sealed class ExternalRecipientPolicyTests
{
    private const string Utility = "UTILITY";
    private const string Marketing = "MARKETING";

    private static MessagingCustomerContact Contact(bool active = true, bool optOut = false) =>
        new(Guid.NewGuid(), "Cliente", "+573001234567", null, active, optOut);

    [Fact]
    public void ABlockedConversationStopsEveryCategory()
    {
        Assert.Equal(MessagingErrorCodes.ExternalRecipientBlocked, ExternalRecipientPolicy.Evaluate(Utility, true, []));
        Assert.Equal(MessagingErrorCodes.ExternalRecipientBlocked, ExternalRecipientPolicy.Evaluate(Marketing, true, [Contact()]));
    }

    [Fact]
    public void AnInactiveCustomerOnlyStopsMarketing()
    {
        Assert.Null(ExternalRecipientPolicy.Evaluate(Utility, false, [Contact(active: false)]));
        Assert.Equal(MessagingErrorCodes.ExternalRecipientInactive, ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(active: false)]));
    }

    [Fact]
    public void AnOptedOutNumberOnlyStopsMarketing()
    {
        Assert.Null(ExternalRecipientPolicy.Evaluate(Utility, false, [Contact(optOut: true)]));
        Assert.Equal(MessagingErrorCodes.ExternalRecipientOptedOut, ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(optOut: true)]));
    }

    [Fact]
    public void NoCustomerGoesOutInBothCategories()
    {
        Assert.Null(ExternalRecipientPolicy.Evaluate(Utility, false, []));
        Assert.Null(ExternalRecipientPolicy.Evaluate(Marketing, false, []));
    }

    [Fact]
    public void SeveralMatchesOnlyStopMarketingOnOptOut()
    {
        Assert.Equal(
            MessagingErrorCodes.ExternalRecipientOptedOut,
            ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(), Contact(optOut: true)]));
        Assert.Null(ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(), Contact(active: false)]));
        Assert.Null(ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(), Contact()]));
    }

    [Fact]
    public void InactiveWinsOverOptOutWithASingleCustomer()
    {
        Assert.Equal(
            MessagingErrorCodes.ExternalRecipientInactive,
            ExternalRecipientPolicy.Evaluate(Marketing, false, [Contact(active: false, optOut: true)]));
    }

    [Fact]
    public void TheCategoryIsComparedIgnoringCase()
    {
        Assert.Equal(MessagingErrorCodes.ExternalRecipientOptedOut, ExternalRecipientPolicy.Evaluate("marketing", false, [Contact(optOut: true)]));
    }
}
