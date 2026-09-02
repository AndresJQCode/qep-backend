using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Arma un Excel ya cargado con las filas que fallaron en una importación anterior — mismo
/// permiso que importar/descargar la plantilla (<c>CustomersPermissions.CustomerImport</c>)
/// porque es parte del mismo flujo, no un recurso de lectura general. El frontend guarda
/// <see cref="ImportRowError.RowData"/> de la respuesta de <see cref="ImportCustomersCommand"/>
/// y la reenvía tal cual acá; este handler no reinterpreta ni valida esos valores — sólo los
/// escribe en el Excel para que la persona los corrija ahí, no en el modal.
/// </summary>
public sealed record ExportFailedCustomerRowsQuery(
    Guid TenantId, IReadOnlyList<CustomerImportRowData> Rows) : IQuery<CustomerImportTemplateFile>;

public sealed class ExportFailedCustomerRowsHandler(
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomerImportTemplateBuilder templateBuilder,
    IExecutionContext executionContext)
    : IQueryHandler<ExportFailedCustomerRowsQuery, CustomerImportTemplateFile>
{
    private const string FileName = "clientes-a-corregir.xlsx";

    public async Task<CustomerImportTemplateFile> HandleAsync(
        ExportFailedCustomerRowsQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.CustomerImport);

        // Sin filas no hay nada que exportar — mismo criterio "fail loud" que
        // `file_empty_data` en la importación, en vez de devolver un Excel con sólo la
        // cabecera y dejar que la persona se pregunte por qué no trajo nada.
        if (query.Rows.Count == 0)
        {
            throw new CustomersDomainException(
                "customers.import.export_empty",
                "There are no failed rows to export.");
        }

        var departments = await geographyLookup.ListDepartmentsWithCitiesAsync(cancellationToken);
        var classifications = await classificationRepository.ListAsync(query.TenantId, cancellationToken);
        var activeClassificationNames = classifications
            .Where(classification => classification.IsActive)
            .Select(classification => classification.Name)
            .ToArray();
        var departmentOptions = departments
            .Select(department => new CustomerImportDepartmentOption(
                department.Name, department.DivipolaCode, department.CityNames))
            .ToArray();

        var content = templateBuilder.BuildWithRows(
            departmentOptions,
            activeClassificationNames,
            IdentificationTypeParser.SupportedWireValues,
            query.Rows,
            cancellationToken);
        return new CustomerImportTemplateFile(content, FileName);
    }
}
