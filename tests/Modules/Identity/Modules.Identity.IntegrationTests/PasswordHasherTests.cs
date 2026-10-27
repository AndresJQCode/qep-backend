using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.IntegrationTests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void AHashVerifiesOnlyItsOwnPassword()
    {
        var hasher = new AspNetCorePasswordHasher();
        var hash = hasher.Hash("correct horse battery");

        Assert.Equal(PasswordVerificationOutcome.Success, hasher.Verify(hash, "correct horse battery"));
        Assert.Equal(PasswordVerificationOutcome.Failed, hasher.Verify(hash, "wrong horse battery"));
        Assert.DoesNotContain("correct horse battery", hash);
    }

    [Fact]
    public void AMissingHashRunsAgainstTheDummyAndFails()
        => Assert.Equal(
            PasswordVerificationOutcome.Failed,
            new AspNetCorePasswordHasher().Verify(null, "anything at all"));

    [Fact]
    public void AnOlderFormatHashAsksForARehash()
    {
        var v2 = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        })).HashPassword(null!, "correct horse battery");

        Assert.Equal(
            PasswordVerificationOutcome.SuccessRehashNeeded,
            new AspNetCorePasswordHasher().Verify(v2, "correct horse battery"));
    }
}
