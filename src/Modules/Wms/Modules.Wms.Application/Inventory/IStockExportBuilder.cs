using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record StockExportFile(byte[] Content, string FileName);

/// <summary>Arma el Excel de saldos. Puerto en Application y adaptador ClosedXML en Infrastructure: qué librería escribe el archivo es
/// un detalle de infraestructura. El <paramref name="calendar"/> es el del tenant: el nombre del archivo va en su hora.</summary>
public interface IStockExportBuilder
{
    StockExportFile Build(IReadOnlyList<StockRowDto> rows, TenantCalendar calendar, CancellationToken cancellationToken);
}
