using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 12: lo que el worker del recordatorio exige sin tocar la base.
public sealed class AppointmentReminderRulesTests
{
    [Fact]
    public void TheIdempotencyKeyIsTheOneOfTheContract()
    {
        var id = Guid.Parse("0192a7d0-0000-7000-8000-000000000001");

        Assert.Equal("appointment-reminder:0192a7d0-0000-7000-8000-000000000001", AppointmentReminderRules.KeyOf(id));
    }

    [Fact]
    public void ThereIsOneIncreasingLeasePerAttemptSoARetryNeverRunsTwiceInARow()
    {
        Assert.Equal(AppointmentReminderRules.MaxAttempts, AppointmentReminderRules.Leases.Count);
        Assert.Equal(AppointmentReminderRules.Leases.Order(), AppointmentReminderRules.Leases);
        Assert.All(AppointmentReminderRules.Leases, lease => Assert.True(lease > TimeSpan.Zero));
    }

    // Una dirección de WhatsApp es E.164: «+» y de 8 a 15 dígitos, sin ceros a la izquierda.
    [Theory]
    [InlineData("+573001234567", true)]
    [InlineData("+14155550123", true)]
    [InlineData("573001234567", false)]
    [InlineData("+0573001234567", false)]
    [InlineData("+5730012", false)]
    [InlineData("+57300123456789012", false)]
    [InlineData("+57 300 1234567", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnE164NumberIsDialable(string? phone, bool dialable) =>
        Assert.Equal(dialable, AppointmentReminderRules.IsDialable(phone));
}
