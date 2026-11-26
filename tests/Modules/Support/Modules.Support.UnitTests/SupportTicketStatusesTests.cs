using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

public sealed class SupportTicketStatusesTests
{
    public static TheoryData<SupportTicketStatus, SupportTicketStatus[]> Transitions => new()
    {
        { SupportTicketStatus.New, [SupportTicketStatus.Open, SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.Resolved] },
        { SupportTicketStatus.Open, [SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.WaitingInternal, SupportTicketStatus.Resolved] },
        {
            SupportTicketStatus.WaitingOnCustomer,
            [SupportTicketStatus.Open, SupportTicketStatus.WaitingInternal, SupportTicketStatus.Resolved, SupportTicketStatus.Closed]
        },
        { SupportTicketStatus.WaitingInternal, [SupportTicketStatus.Open, SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.Resolved] },
        { SupportTicketStatus.Resolved, [SupportTicketStatus.Closed, SupportTicketStatus.Open] },
        { SupportTicketStatus.Closed, [] },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void AllowedFromIsTheContractTableInOrder(SupportTicketStatus from, SupportTicketStatus[] expected) =>
        Assert.Equal(expected, SupportTicketStatuses.AllowedFrom(from));

    [Fact]
    public void OrderedIsTheSixStatusesInContractOrder() =>
        Assert.Equal(
            [
                SupportTicketStatus.New, SupportTicketStatus.Open, SupportTicketStatus.WaitingOnCustomer,
                SupportTicketStatus.WaitingInternal, SupportTicketStatus.Resolved, SupportTicketStatus.Closed,
            ],
            SupportTicketStatuses.Ordered);

    [Fact]
    public void NothingGoesBackToNew() =>
        Assert.All(
            SupportTicketStatuses.Ordered,
            from => Assert.DoesNotContain(SupportTicketStatus.New, SupportTicketStatuses.AllowedFrom(from)));

    [Fact]
    public void ClosedIsTerminal() =>
        Assert.Empty(SupportTicketStatuses.AllowedFrom(SupportTicketStatus.Closed));

    [Fact]
    public void OnlyWaitingOnCustomerResolvedAndClosedStopTheClock() =>
        Assert.Equal(
            [SupportTicketStatus.WaitingOnCustomer, SupportTicketStatus.Resolved, SupportTicketStatus.Closed],
            SupportTicketStatuses.Ordered.Where(SupportTicketStatuses.StopsTheClock));

    [Fact]
    public void AllowedForATaskNeverOffersWaitingOnCustomer() =>
        Assert.All(
            SupportTicketStatuses.Ordered,
            from => Assert.DoesNotContain(SupportTicketStatus.WaitingOnCustomer, SupportTicketStatuses.AllowedFor(from, isInternal: true)));

    [Fact]
    public void AllowedForAnExternalTicketIsAllowedFrom() =>
        Assert.All(
            SupportTicketStatuses.Ordered,
            from => Assert.Equal(SupportTicketStatuses.AllowedFrom(from), SupportTicketStatuses.AllowedFor(from, isInternal: false)));
}
