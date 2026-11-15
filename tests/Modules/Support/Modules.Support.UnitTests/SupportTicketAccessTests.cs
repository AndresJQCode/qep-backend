using System.Text.RegularExpressions;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// La clave de consulta del ticket (plan fase 3, F7): cinco fallos seguidos bloquean 15 minutos y un reenvío no desbloquea.
public sealed class SupportTicketAccessTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static SupportTicketAccess NewAccess() => SupportTicketAccess.Issue(SupportTicketId.New(), TenantId, "hash-1", Now);

    [Fact]
    public void ANewAccessIsUnlockedWithoutFailuresOrDeliveries()
    {
        var access = NewAccess();

        Assert.Equal("hash-1", access.PinHash);
        Assert.Equal(0, access.FailedAttempts);
        Assert.Null(access.LockedUntil);
        Assert.Equal(Now, access.PinIssuedAt);
        Assert.Null(access.LastSentAt);
        Assert.False(access.IsLocked(Now));
    }

    [Fact]
    public void TheFifthFailureLocksForFifteenMinutes()
    {
        var access = NewAccess();
        for (var attempt = 1; attempt < SupportTicketAccess.MaxFailures; attempt++)
        {
            access.RecordFailure(Now.AddSeconds(attempt));
            Assert.False(access.IsLocked(Now.AddSeconds(attempt)));
        }

        var fifth = Now.AddMinutes(1);
        access.RecordFailure(fifth);

        Assert.Equal(5, SupportTicketAccess.MaxFailures);
        Assert.Equal(TimeSpan.FromMinutes(15), SupportTicketAccess.LockFor);
        Assert.Equal(fifth + SupportTicketAccess.LockFor, access.LockedUntil);
        Assert.True(access.IsLocked(fifth));
        Assert.True(access.IsLocked(fifth.AddMinutes(14)));
        Assert.False(access.IsLocked(fifth.AddMinutes(15)));
    }

    [Fact]
    public void ASuccessResetsTheFailures()
    {
        var access = NewAccess();
        for (var attempt = 0; attempt < SupportTicketAccess.MaxFailures - 1; attempt++)
        {
            access.RecordFailure(Now);
        }

        access.RecordSuccess();
        access.RecordFailure(Now);

        Assert.Equal(1, access.FailedAttempts);
        Assert.False(access.IsLocked(Now));
    }

    [Fact]
    public void ReissueKeepsTheLock()
    {
        var access = NewAccess();
        for (var attempt = 0; attempt < SupportTicketAccess.MaxFailures; attempt++)
        {
            access.RecordFailure(Now);
        }

        var later = Now.AddMinutes(2);
        access.Reissue("hash-2", later);

        Assert.Equal("hash-2", access.PinHash);
        Assert.Equal(later, access.PinIssuedAt);
        Assert.True(access.IsLocked(later));
        Assert.Equal(Now + SupportTicketAccess.LockFor, access.LockedUntil);
    }

    [Fact]
    public void CanResendOnlyAfterTenMinutes()
    {
        var access = NewAccess();
        Assert.True(access.CanResend(Now));

        access.RecordDelivery("Email", failureCode: null, Now);

        Assert.False(access.CanResend(Now.AddMinutes(9)));
        Assert.True(access.CanResend(Now.AddMinutes(10)));
        Assert.Equal(Now, access.LastSentAt);
        Assert.Equal("Email", access.LastSentChannel);
        Assert.Null(access.LastFailureCode);
    }

    [Fact]
    public void ADeliveryRecordsItsFailure()
    {
        var access = NewAccess();

        access.RecordDelivery("Email", "send_failed", Now);

        Assert.Equal("send_failed", access.LastFailureCode);
        Assert.Throws<ArgumentException>(() => access.RecordDelivery(" ", null, Now));
    }
}

public sealed class SupportPinTests
{
    [Fact]
    public void ItIsSixDigits()
    {
        var pattern = new Regex(@"^\d{6}$", RegexOptions.None, TimeSpan.FromSeconds(1));

        for (var index = 0; index < 1000; index++)
        {
            Assert.Matches(pattern, SupportPin.Generate());
        }
    }
}
