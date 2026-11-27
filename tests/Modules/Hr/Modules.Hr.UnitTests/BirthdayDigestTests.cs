using Modules.Hr.Application.BirthdayNotices;

namespace Modules.Hr.UnitTests;

/// <summary>Spec 2026-11-26 §5.2: the pure composer of the digest (today, the exact day N ahead, cap and order). No database.</summary>
public sealed class BirthdayDigestTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    private static (Guid Id, string Name, DateOnly BirthDate) Employee(string name, int month, int day, int year = 1990) =>
        (Guid.NewGuid(), name, new DateOnly(year, month, day));

    [Fact]
    public void TodayAndUpcomingAreSeparated()
    {
        var ana = Employee("Ana Pérez", 11, 26);
        var luis = Employee("Luis Gómez", 11, 27);
        var carla = Employee("Carla Ruiz", 11, 28);

        var digest = BirthdayDigest.Compose([ana, luis, carla], Today, leadDays: 1);

        Assert.Equal([new BirthdayPerson(ana.Id, "Ana Pérez")], digest.Today);
        Assert.Equal(1, digest.TodayTotal);
        Assert.Equal([new BirthdayPerson(luis.Id, "Luis Gómez")], digest.Upcoming);
        Assert.Equal(1, digest.UpcomingTotal);
        Assert.Equal(new DateOnly(2026, 11, 27), digest.UpcomingDate);
    }

    [Fact]
    public void NamesAreOrderedIgnoringAccentsAndCase()
    {
        var zoe = Employee("Zoe Díaz", 11, 26);
        var alvaro = Employee("Álvaro Soto", 11, 26);
        var beatriz = Employee("beatriz Mora", 11, 26);

        var digest = BirthdayDigest.Compose([zoe, alvaro, beatriz], Today, leadDays: 0);

        Assert.Equal(["Álvaro Soto", "beatriz Mora", "Zoe Díaz"], digest.Today.Select(person => person.EmployeeName));
    }

    [Fact]
    public void LeadZeroHasNoUpcoming()
    {
        var digest = BirthdayDigest.Compose([Employee("Ana Pérez", 11, 26), Employee("Luis Gómez", 11, 27)], Today, leadDays: 0);

        Assert.Single(digest.Today);
        Assert.Null(digest.UpcomingDate);
        Assert.Empty(digest.Upcoming);
        Assert.Equal(0, digest.UpcomingTotal);
    }

    [Fact]
    public void MoreThan50AreCappedWithRealTotal()
    {
        var people = Enumerable.Range(0, 60).Select(index => Employee($"Persona {index:00}", 11, 26)).ToList();

        var digest = BirthdayDigest.Compose(people, Today, leadDays: 1);

        Assert.Equal(BirthdayDigest.MaxNames, digest.Today.Count);
        Assert.Equal(60, digest.TodayTotal);
        // The first 50 by name: the cap never drops an arbitrary person.
        Assert.Equal("Persona 00", digest.Today[0].EmployeeName);
        Assert.Equal("Persona 49", digest.Today[^1].EmployeeName);
    }

    [Fact]
    public void SortedByName()
    {
        var digest = BirthdayDigest.Compose(
            [Employee("zoe Díaz", 11, 26), Employee("Ana Pérez", 11, 26), Employee("beto Mora", 11, 26)], Today, leadDays: 1);

        Assert.Equal(["Ana Pérez", "beto Mora", "zoe Díaz"], digest.Today.Select(person => person.EmployeeName));
    }

    [Fact]
    public void Feb29IsUpcomingOnFeb27InANonLeapYear()
    {
        var leapling = Employee("Ana Pérez", 2, 29, 2000);

        var digest = BirthdayDigest.Compose([leapling], new DateOnly(2027, 2, 27), leadDays: 1);

        Assert.Equal([new BirthdayPerson(leapling.Id, "Ana Pérez")], digest.Upcoming);
        Assert.Equal(new DateOnly(2027, 2, 28), digest.UpcomingDate);
    }

    [Fact]
    public void NobodyYieldsEmptyListsAndZeroTotals()
    {
        var digest = BirthdayDigest.Compose([], Today, leadDays: 3);

        Assert.Empty(digest.Today);
        Assert.Empty(digest.Upcoming);
        Assert.Equal(0, digest.TodayTotal);
        Assert.Equal(0, digest.UpcomingTotal);
        Assert.Equal(new DateOnly(2026, 11, 29), digest.UpcomingDate);
    }
}
