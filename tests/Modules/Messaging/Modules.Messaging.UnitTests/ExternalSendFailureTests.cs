using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-30 §6.3.2: la liquidación lanza MessageRejected tanto para rechazo como para sin confirmar; Map decide por el resultado.</summary>
public sealed class ExternalSendFailureTests
{
    private static MessagingDomainException Rejected() => new(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

    [Fact]
    public void UnconfirmedIsA502()
    {
        var error = Assert.IsType<UpstreamServiceException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.Unconfirmed, null, null, null), Rejected()));
        Assert.Equal(MessagingErrorCodes.ExternalSendUnconfirmed, error.Code);
    }

    [Fact]
    public void NotSentIsA502()
    {
        var error = Assert.IsType<UpstreamServiceException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.NotSent, null, null, null), Rejected()));
        Assert.Equal(MessagingErrorCodes.ExternalSendUnconfirmed, error.Code);
    }

    [Fact]
    public void AWamidlessSuccessIsA502()
    {
        var error = Assert.IsType<UpstreamServiceException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.Sent, null, null, null), Rejected()));
        Assert.Equal(MessagingErrorCodes.ExternalSendUnconfirmed, error.Code);
    }

    [Fact]
    public void MetaOptOutIsRecipientOptedOut()
    {
        var error = Assert.IsType<MessagingDomainException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.MarketingOptOut, "x"), Rejected()));
        Assert.Equal(MessagingErrorCodes.ExternalRecipientOptedOut, error.Code);
    }

    [Theory]
    [InlineData(MetaErrorCodes.ThroughputLimit)]
    [InlineData(MetaErrorCodes.PairRateLimit)]
    public void ThroughputAndPairLimitsAre429(int code)
    {
        var error = Assert.IsType<RequestRateLimitedException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.GraphError, null, code, "x"), Rejected()));
        Assert.Equal(ExternalApiErrorCodes.RateLimited, error.Code);
        Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
    }

    [Theory]
    [InlineData(132001)]
    [InlineData(null)]
    public void AnyOtherGraphErrorIsMessageRejected(int? code)
    {
        var error = Assert.IsType<MessagingDomainException>(
            ExternalSendFailure.Map(new SendResult(SendOutcome.GraphError, null, code, "x"), Rejected()));
        Assert.Equal(MessagingErrorCodes.ExternalMessageRejected, error.Code);
    }

    [Fact]
    public void RejectedCredentialsStayConnectionUnavailable()
    {
        var thrown = new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "down");
        var mapped = ExternalSendFailure.Map(new SendResult(SendOutcome.GraphError, null, 190, "x"), thrown);
        Assert.Same(thrown, mapped);
    }

    [Fact]
    public void MetaTitleNeverLeaks()
    {
        const string title = "SECRET-META-TITLE";
        foreach (var result in new[]
        {
            new SendResult(SendOutcome.GraphError, null, 132001, title),
            new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.MarketingOptOut, title),
            new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.ThroughputLimit, title),
            new SendResult(SendOutcome.Unconfirmed, null, null, title),
        })
        {
            Assert.DoesNotContain(title, ExternalSendFailure.Map(result, Rejected()).Message);
        }
    }
}
