using System.Globalization;
using Modules.Billing.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class CustomerMessagePlannerTests
{
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static readonly DateTimeOffset Now = new(2026, 3, 10, 13, 0, 0, TimeSpan.Zero);

    private static readonly Guid Customer = Guid.NewGuid();

    private static readonly IReadOnlySet<(Guid ChargeId, string Step)> NothingCovered = new HashSet<(Guid, string)>();

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Un cobro <c>Pending</c> con ese vencimiento, de un servicio propio del mismo cliente.</summary>
    private static Charge ChargeDue(string dueDate, decimal amount = 100m, string currency = "COP")
    {
        var due = Date(dueDate);
        var service = BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Customer, "Hosting", amount, currency, BillingCycle.Monthly, due, Now);
        return Charge.Generate(service, due, Now);
    }

    [Fact]
    public void NoStepDueMeansNoMessage()
    {
        Assert.Null(CustomerMessagePlanner.Plan([ChargeDue("2026-03-20")], NothingCovered, Today));
    }

    [Fact]
    public void OneUpcomingChargeIsAReminderCoveringItsStep()
    {
        var charge = ChargeDue("2026-03-10");

        var plan = CustomerMessagePlanner.Plan([charge], NothingCovered, Today);

        Assert.NotNull(plan);
        Assert.Equal(BillingMessageKind.Reminder, plan.Kind);
        Assert.Equal([charge.Id], plan.Charges.Select(c => c.Id));
        Assert.Equal([(charge.Id, "due")], plan.Steps);
        Assert.Equal(Date("2026-03-10"), plan.NearestDueDate);
    }

    [Fact]
    public void TwoUpcomingChargesTheSameDayAreOneReminder()
    {
        var dueToday = ChargeDue("2026-03-10");
        var dueIn5 = ChargeDue("2026-03-15");

        var plan = CustomerMessagePlanner.Plan([dueIn5, dueToday], NothingCovered, Today);

        Assert.NotNull(plan);
        Assert.Equal(BillingMessageKind.Reminder, plan.Kind);
        Assert.Equal([dueToday.Id, dueIn5.Id], plan.Charges.Select(c => c.Id));
        Assert.Equal(
            new HashSet<(Guid, string)> { (dueToday.Id, "due"), (dueIn5.Id, "before_5") },
            plan.Steps.ToHashSet());
        Assert.Equal(Date("2026-03-10"), plan.NearestDueDate);
    }

    [Fact]
    public void AnOverdueStepMakesAnOverdueMessageListingEveryOverdueCharge()
    {
        var after10Today = ChargeDue("2026-02-25");
        var overdueAlreadyCovered = ChargeDue("2026-02-20");
        var upcoming = ChargeDue("2026-03-15");
        var covered = new HashSet<(Guid, string)> { (overdueAlreadyCovered.Id, "after_17") };

        var plan = CustomerMessagePlanner.Plan([upcoming, after10Today, overdueAlreadyCovered], covered, Today);

        Assert.NotNull(plan);
        Assert.Equal(BillingMessageKind.Overdue, plan.Kind);
        // Lista todos los vencidos del cliente (no sólo el que disparó), del más viejo al más nuevo; el que vence después no.
        Assert.Equal([overdueAlreadyCovered.Id, after10Today.Id], plan.Charges.Select(c => c.Id));
        // Cubre los pasos que tocaban hoy, incluido el before_5 del que todavía no vence: ese aviso ya va en este mensaje.
        Assert.Equal(
            new HashSet<(Guid, string)> { (after10Today.Id, "after_10"), (upcoming.Id, "before_5") },
            plan.Steps.ToHashSet());
    }

    [Fact]
    public void AlreadyCoveredStepsAreIgnored()
    {
        var charge = ChargeDue("2026-03-10");
        var covered = new HashSet<(Guid, string)> { (charge.Id, "due") };

        Assert.Null(CustomerMessagePlanner.Plan([charge], covered, Today));
    }

    [Fact]
    public void ACoveredLatestStepIsNotReplacedByAnEarlierUncoveredOne()
    {
        // Ruling F8: el paso es el último con fecha <= hoy; si ése ya está cubierto no se vuelve a uno anterior sin fila.
        var charge = ChargeDue("2026-02-25");
        var covered = new HashSet<(Guid, string)> { (charge.Id, "after_10") };

        Assert.Null(CustomerMessagePlanner.Plan([charge], covered, Today));
    }

    [Fact]
    public void ChargesThatAreNotPendingNeverTriggerAMessage()
    {
        var paid = ChargeDue("2026-03-10");
        paid.MarkPaid(Today, null, Guid.NewGuid(), Today, Now);

        Assert.Null(CustomerMessagePlanner.Plan([paid], NothingCovered, Today));
    }
}
