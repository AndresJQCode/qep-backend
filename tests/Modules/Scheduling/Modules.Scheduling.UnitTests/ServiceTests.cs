using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

public sealed class ServiceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AServiceKeepsItsDurationAndBuffer()
    {
        var service = Service.Create(ServiceId.New(), Tenant, " Limpieza ", 30, 10, bookableByChannel: true, Now);

        Assert.Equal("Limpieza", service.Name);
        Assert.Equal((30, 10, true), (service.DurationMinutes, service.BufferMinutes, service.BookableByChannel));
        Assert.Equal(40, service.OccupiedMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(7)]
    [InlineData(485)]
    public void ADurationThatIsNotAPositiveMultipleOfFiveUpToEightHoursIsRejected(int duration) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service.Create(ServiceId.New(), Tenant, "X", duration, 0, false, Now));

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(245)]
    public void ABufferThatIsNegativeOrNotAMultipleOfFiveOrOverFourHoursIsRejected(int buffer) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service.Create(ServiceId.New(), Tenant, "X", 30, buffer, false, Now));

    [Fact]
    public void UpdatingBumpsTheVersionAndArchivingKeepsTheRow()
    {
        var service = Service.Create(ServiceId.New(), Tenant, "Valoración", 30, 0, true, Now);

        service.Update("Valoración inicial", 45, 5, false, Now.AddMinutes(1));
        service.Archive(Now.AddMinutes(2));

        Assert.Equal((45, 5, false), (service.DurationMinutes, service.BufferMinutes, service.BookableByChannel));
        Assert.True(service.Archived);
        Assert.Equal(3, service.Version);
    }
}
