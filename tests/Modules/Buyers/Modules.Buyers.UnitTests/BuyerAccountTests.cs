using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static BuyerAccount NewAccount() => BuyerAccount.Register(Guid.NewGuid(), "ana@correo.com", "hash-1", Now);

    [Fact]
    public void RegisterStartsUnverified()
    {
        var account = NewAccount();
        Assert.False(account.IsVerified);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Equal(Now, account.CreatedAt);
    }

    [Fact]
    public void ReplacePendingRegistrationThrowsOnAVerifiedAccount()
    {
        var account = NewAccount();
        account.MarkEmailVerified(Now);
        Assert.Throws<InvalidOperationException>(() => account.ReplacePendingRegistration("hash-2", Now));
    }

    [Fact]
    public void ReplacePendingRegistrationSwapsTheHashAndMovesUpdatedAt()
    {
        var account = NewAccount();
        var later = Now.AddHours(1);
        account.ReplacePendingRegistration("hash-2", later);
        Assert.Equal("hash-2", account.PasswordHash);
        Assert.Equal(later, account.UpdatedAt);
    }

    [Fact]
    public void MarkEmailVerifiedKeepsTheFirstTimestamp()
    {
        var account = NewAccount();
        account.MarkEmailVerified(Now.AddMinutes(1));
        account.MarkEmailVerified(Now.AddMinutes(9));
        Assert.True(account.IsVerified);
        Assert.Equal(Now.AddMinutes(1), account.EmailVerifiedAt);
    }

    [Fact]
    public void ChangePasswordHashSwapsTheHash()
    {
        var account = NewAccount();
        account.ChangePasswordHash("hash-3", Now.AddDays(1));
        Assert.Equal("hash-3", account.PasswordHash);
        Assert.Equal(Now.AddDays(1), account.UpdatedAt);
    }
}
