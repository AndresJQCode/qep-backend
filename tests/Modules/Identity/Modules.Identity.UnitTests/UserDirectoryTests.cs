using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): la invitación de un distribuidor busca al usuario por el correo invitado.</summary>
public sealed class UserDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FindUserIdByEmailNormalizesTheAddress()
    {
        var users = new FakeUserRepository();
        var ana = User.CreateInvited(UserId.New(), "ana@x.co", Now);
        users.Stored.Add(ana);
        var directory = new UserDirectory(users);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ana.Id.Value, await directory.FindUserIdByEmailAsync(" Ana@X.CO ", ct));
        Assert.Null(await directory.FindUserIdByEmailAsync("otra@x.co", ct));
        Assert.Null(await directory.FindUserIdByEmailAsync("no-es-correo", ct));
        Assert.Null(await directory.FindUserIdByEmailAsync("   ", ct));
    }
}
