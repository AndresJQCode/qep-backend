using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Arma el Excel del padron de clientes. Puerto en Application y adaptador ClosedXML en
/// Infrastructure, igual que <see cref="ICustomerImportTemplateBuilder"/>: que libreria escribe el
/// archivo es un detalle de infraestructura.
/// </summary>
public interface ICustomerExportBuilder
{
    /// <summary>El <paramref name="calendar"/> es el del tenant: el nombre del archivo y las fechas
    /// van en su hora (spec 2026-09-17, punto 8a), y <c>calendar.UtcNow</c> es el instante en que se
    /// generó.</summary>
    CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        TenantCalendar calendar,
        CancellationToken cancellationToken);
}

public sealed record CustomerExportFile(byte[] Content, string FileName);
