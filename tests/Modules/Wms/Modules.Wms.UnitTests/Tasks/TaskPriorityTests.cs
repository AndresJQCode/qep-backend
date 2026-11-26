using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class TaskPriorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(19, 5)]
    [InlineData(20, 6)]
    [InlineData(59, 7)]
    [InlineData(70, 8)]
    [InlineData(200, 8)]
    public void AgingAddsOnePointEveryTwentyMinutesUpToThree(int minutesInThePool, int score) =>
        Assert.Equal(score, TaskPriority.Score(5, false, Now.AddMinutes(-minutesInThePool), null, Now));

    /// <summary>Dos réplicas con relojes corridos: una tarea «liberada en el futuro» no pierde puntos.</summary>
    [Fact]
    public void AgingIsNeverNegative() =>
        Assert.Equal(5, TaskPriority.Score(5, false, Now.AddMinutes(1), null, Now));

    [Fact]
    public void ATaskOutOfThePoolDoesNotAge() =>
        Assert.Equal(5, TaskPriority.Score(5, false, null, null, Now));

    [Fact]
    public void UrgentAddsTen() =>
        Assert.Equal(15, TaskPriority.Score(5, true, Now, null, Now));

    [Fact]
    public void TheScoreAddsEveryTerm() =>
        Assert.Equal(5 + 10 + 3 + 4, TaskPriority.Score(5, true, Now.AddMinutes(-90), Now.AddMinutes(-1), Now));

    [Fact]
    public void ATaskWithoutDueDateHasNoDueTier() => Assert.Equal(0, TaskPriority.DueTier(null, Now));

    [Theory]
    [InlineData(180, 0)]
    [InlineData(121, 0)]
    [InlineData(120, 1)]
    [InlineData(61, 1)]
    [InlineData(60, 2)]
    [InlineData(45, 2)]
    [InlineData(30, 3)]
    [InlineData(20, 3)]
    [InlineData(1, 3)]
    [InlineData(0, 4)]
    [InlineData(-5, 4)]
    public void TheDueTierGrowsAsTheDeadlineApproaches(int minutesLeft, int tier) =>
        Assert.Equal(tier, TaskPriority.DueTier(Now.AddMinutes(minutesLeft), Now));
}
