using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

public sealed class ClosureAndSessionTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AClosureNeedsAPositiveRange() =>
        Assert.Throws<ArgumentException>(() =>
            Closure.Create(ClosureId.New(), Tenant, LocationId.New(), null, Start, Start, "x", Now));

    [Fact]
    public void AClosureWithoutResourceClosesTheWholeLocationAndOneWithAResourceOnlyThatOne()
    {
        var perez = ResourceId.New();
        var north = LocationId.New();
        var south = LocationId.New();
        var wholeLocation = Closure.Create(ClosureId.New(), Tenant, north, null, Start, Start.AddHours(4), "Festivo", Now);
        var onlyPerez = Closure.Create(ClosureId.New(), Tenant, north, perez, Start, Start.AddHours(4), "Congreso", Now);

        Assert.True(wholeLocation.AppliesTo(ResourceId.New(), north));
        Assert.False(wholeLocation.AppliesTo(ResourceId.New(), south));
        Assert.True(onlyPerez.AppliesTo(perez, north));
        Assert.False(onlyPerez.AppliesTo(ResourceId.New(), north));
    }

    [Fact]
    public void AClosureOverlapsARangeThatCrossesItButNotOneThatOnlyTouchesIt()
    {
        var closure = Closure.Create(ClosureId.New(), Tenant, LocationId.New(), null, Start, Start.AddHours(4), null, Now);

        Assert.True(closure.Overlaps(Start.AddHours(3), Start.AddHours(5)));
        Assert.False(closure.Overlaps(Start.AddHours(4), Start.AddHours(5)));
        Assert.False(closure.Overlaps(Start.AddHours(-1), Start));
    }

    private static Session NewSession(int capacity = 1, bool published = false, int buffer = 10) =>
        Session.Create(SessionId.New(), Tenant, ResourceId.New(), ServiceId.New(), Start, Start.AddMinutes(30), buffer, capacity, published, Now);

    [Fact]
    public void ASessionOccupiesItsRangePlusTheBufferAndStartsActiveAndEmpty()
    {
        var session = NewSession();

        Assert.Equal(Start.AddMinutes(40), session.OccupiesUntil);
        Assert.Equal((0, true), (session.BookedCount, session.IsActive));
        Assert.True(session.HasRoom);
    }

    [Fact]
    public void ASessionNeverHoldsMoreThanItsCapacity()
    {
        var session = NewSession(capacity: 2);

        session.Reserve(Now);
        session.Reserve(Now);

        Assert.False(session.HasRoom);
        var error = Assert.Throws<SchedulingDomainException>(() => session.Reserve(Now));
        Assert.Equal(SchedulingErrorCodes.SessionFull, error.Code);
        Assert.Equal(2, session.BookedCount);
    }

    [Fact]
    public void AnOpenedByFirstBookingSessionReleasesItsRangeWhenTheLastSeatIsFreed()
    {
        var session = NewSession(capacity: 3);
        session.Reserve(Now);
        session.Reserve(Now);

        session.Release(Now);
        Assert.True(session.IsActive);
        session.Release(Now);
        Assert.False(session.IsActive);
        Assert.Equal(0, session.BookedCount);
    }

    [Fact]
    public void APublishedSessionStaysActiveWhenItEmpties()
    {
        var session = NewSession(capacity: 5, published: true);
        session.Reserve(Now);

        session.Release(Now);

        Assert.True(session.IsActive);
    }

    [Fact]
    public void ReservingAnInactiveSessionReopensIt()
    {
        var session = NewSession();
        session.Reserve(Now);
        session.Release(Now);
        Assert.False(session.IsActive);

        session.Reserve(Now.AddMinutes(1));

        Assert.True(session.IsActive);
        Assert.Equal(1, session.BookedCount);
    }

    [Fact]
    public void ReleasingAnEmptySessionIsAProgrammingError() =>
        Assert.Throws<InvalidOperationException>(() => NewSession().Release(Now));

    [Fact]
    public void DeactivatingAPublishedSessionWithoutBookingsFreesItsRangeAndKeepsItMarkedAsPublished()
    {
        var session = NewSession(capacity: 5, published: true);

        session.Deactivate(Now.AddMinutes(1));

        Assert.False(session.IsActive);
        Assert.True(session.IsPublished);
        Assert.Equal(2, session.Version);
    }

    [Fact]
    public void ABookingNeverReopensAPublishedSessionTheTenantRemoved()
    {
        var session = NewSession(capacity: 5, published: true);
        session.Deactivate(Now);

        Assert.Throws<InvalidOperationException>(() => session.Reserve(Now.AddMinutes(1)));

        Assert.False(session.IsActive);
        Assert.Equal(0, session.BookedCount);
    }

    [Fact]
    public void ASessionWithBookingsCannotBeDeactivated()
    {
        var session = NewSession(capacity: 5, published: true);
        session.Reserve(Now);

        Assert.Throws<InvalidOperationException>(() => session.Deactivate(Now));
    }

    [Fact]
    public void RepublishingAnInactiveSessionReactivatesItWithTheCurrentRangeAndCapacity()
    {
        var session = NewSession(capacity: 5, published: true);
        session.Deactivate(Now);

        session.Republish(Start.AddMinutes(45), bufferMinutes: 15, capacity: 8, Now.AddMinutes(1));

        Assert.True(session.IsActive);
        Assert.True(session.IsPublished);
        Assert.Equal(8, session.Capacity);
        Assert.Equal(Start.AddMinutes(45), session.EndsAt);
        Assert.Equal(Start.AddMinutes(60), session.OccupiesUntil);
        Assert.Equal(0, session.BookedCount);
    }

    [Fact]
    public void AnActiveSessionCannotBeRepublished()
    {
        var session = NewSession(capacity: 5, published: true);

        Assert.Throws<InvalidOperationException>(() => session.Republish(Start.AddMinutes(30), 0, 5, Now));
    }

    [Fact]
    public void ACapacityBelowOneAndARangeThatEndsBeforeItStartsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewSession(capacity: 0));
        Assert.Throws<ArgumentException>(() =>
            Session.Create(SessionId.New(), Tenant, ResourceId.New(), ServiceId.New(), Start, Start, 0, 1, false, Now));
    }
}
