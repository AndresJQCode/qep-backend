using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>La fecha del documento es la de su <c>CreatedAt</c> en la zona del tenant (spec impuestos por tipo §7.3,
/// §13): decide de qué año es la UVT congelada.</summary>
public sealed class DocumentDatesTests
{
    [Fact]
    public void NewYearsEveEveningInBogotaIsThatYear()
    {
        var bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
        // El reloj del tenant ya en 2027 en los dos husos: la fecha sale del CreatedAt, no del hoy.
        var calendar = new TenantCalendar(new DateTimeOffset(2027, 1, 2, 12, 0, 0, TimeSpan.Zero), bogota);

        var date = DocumentDates.LocalDate(calendar, new DateTimeOffset(2027, 1, 1, 1, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 12, 31), date);
    }
}
