namespace Modules.ServiceContracts.Application;

/// <summary>
/// Una fila del Excel de cobros, sin ninguna validación de negocio: cada celda es el texto crudo (recortado, vacío → null).
/// Fechas y números que Excel guarda como tales llegan ya como texto invariante (<c>yyyy-MM-dd</c>, punto decimal).
/// <c>RowNumber</c> es la fila real del Excel (la 1 es el encabezado). Copia de <c>ExcelCustomerRow</c>.
/// </summary>
public sealed record ExcelServiceContractsRow(
    int RowNumber,
    string? CustomerName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Country,
    string? ServiceName,
    string? Price,
    string? Currency,
    string? Cycle,
    string? DueDate,
    string? Channel,
    string? AssigneeEmail);

/// <summary>
/// Lee el Excel de cobros (adaptador ClosedXML en Infrastructure, como <c>IExcelCustomerImporter</c>). Sólo parseo
/// estructural: un archivo que no se puede abrir lanza <c>billing.import.unreadable</c> y uno sin los trece encabezados
/// de <see cref="ServiceContractsImportColumns"/>, <c>billing.import.header_mismatch</c> (los dos, <c>ServiceContractsDomainException</c>).
/// </summary>
public interface IExcelServiceContractsImporter
{
    Task<IReadOnlyList<ExcelServiceContractsRow>> ReadAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Arma la plantilla <c>.xlsx</c> (adaptador ClosedXML), con los desplegables de §3.2b.</summary>
public interface IServiceContractsImportTemplateBuilder
{
    byte[] Build(
        IReadOnlyCollection<string> identificationTypes,
        IReadOnlyCollection<string> currencies,
        CancellationToken cancellationToken);
}
