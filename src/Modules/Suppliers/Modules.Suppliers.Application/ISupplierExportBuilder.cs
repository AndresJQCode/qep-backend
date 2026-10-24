using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

/// <summary>
/// Arma el Excel del directorio de proveedores. Puerto en Application y adaptador ClosedXML en
/// Infrastructure: qué librería escribe el archivo es un detalle de infraestructura.
/// </summary>
public interface ISupplierExportBuilder
{
    /// <summary>El <paramref name="calendar"/> es el del tenant: el nombre del archivo va en su hora.</summary>
    SupplierExportFile Build(
        IReadOnlyList<SupplierResponse> suppliers,
        TenantCalendar calendar,
        CancellationToken cancellationToken);
}

public sealed record SupplierExportFile(byte[] Content, string FileName);
