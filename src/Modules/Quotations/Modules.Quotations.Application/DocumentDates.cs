using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// La fecha de un documento para resolver la unidad tributaria (spec impuestos por tipo §7.3, D6/D20): el día de su
/// <c>CreatedAt</c> en la zona del tenant, no en UTC. El 31 de diciembre a las 20:00 en Bogotá ya es 2027 en UTC y
/// tiene que seguir usando la UVT de ese año.
/// </summary>
public static class DocumentDates
{
    public static DateOnly LocalDate(TenantCalendar calendar, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return DateOnly.FromDateTime(calendar.ToLocal(createdAt).DateTime);
    }
}
