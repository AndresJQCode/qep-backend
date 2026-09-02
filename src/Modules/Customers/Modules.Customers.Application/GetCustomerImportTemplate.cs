using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// La plantilla de importacion (Fase 6): el mismo permiso que importar
/// (<c>CustomersPermissions.CustomerImport</c>) porque descargarla es parte del mismo flujo, no un
/// recurso de lectura general.
/// </summary>
public sealed record GetCustomerImportTemplateQuery(Guid TenantId) : IQuery<CustomerImportTemplateFile>;

public sealed class GetCustomerImportTemplateHandler(
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomerImportTemplateBuilder templateBuilder,
    IExecutionContext executionContext)
    : IQueryHandler<GetCustomerImportTemplateQuery, CustomerImportTemplateFile>
{
    private const string FileName = "plantilla-clientes.xlsx";

    public async Task<CustomerImportTemplateFile> HandleAsync(
        GetCustomerImportTemplateQuery query,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CustomersPermissions.CustomerImport);

        var departments = await geographyLookup.ListDepartmentsWithCitiesAsync(cancellationToken);

        // Solo las clasificaciones activas: una que esta desactivada no deberia sugerirse en una
        // plantilla que alguien va a llenar de ahora en adelante, aunque FindByNameAsync (el que
        // resuelve la fila al importar) no la rechace por ese motivo — mismo criterio de
        // FindAsync, que tampoco filtra por IsActive al resolver una referencia existente.
        var classifications = await classificationRepository.ListAsync(query.TenantId, cancellationToken);
        var activeClassificationNames = classifications
            .Where(classification => classification.IsActive)
            .Select(classification => classification.Name)
            .ToArray();
        var departmentOptions = departments
            .Select(department => new CustomerImportDepartmentOption(
                department.Name, department.DivipolaCode, department.CityNames))
            .ToArray();

        var content = templateBuilder.Build(
            departmentOptions,
            activeClassificationNames,
            IdentificationTypeParser.SupportedWireValues,
            cancellationToken);
        return new CustomerImportTemplateFile(content, FileName);
    }
}
