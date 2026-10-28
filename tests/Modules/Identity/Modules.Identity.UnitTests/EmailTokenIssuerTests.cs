using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

public sealed class EmailTokenIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmailTokenRepository tokens = new();
    private readonly EmailTokenIssuer issuer;
    private readonly Guid userId = Guid.NewGuid();

    public EmailTokenIssuerTests() => issuer = new EmailTokenIssuer(tokens, new FixedClock(Now));

    [Fact]
    public async Task IssuingInvalidatesThePreviousUnusedTokensOfTheSamePurpose()
    {
        await issuer.IssueAsync(userId, EmailTokenPurpose.Verify, TestContext.Current.CancellationToken);

        Assert.Equal(
            (userId, (EmailTokenPurpose?)EmailTokenPurpose.Verify),
            Assert.Single(tokens.Invalidations));
    }

    [Fact]
    public async Task OnlyTheHashOfTheRawTokenIsStored()
    {
        var raw = await issuer.IssueAsync(userId, EmailTokenPurpose.Reset, TestContext.Current.CancellationToken);

        Assert.Equal(43, raw.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", raw);
        Assert.Equal(EmailTokenCodec.Hash(raw), Assert.Single(tokens.Added).TokenHash);
    }
}
