using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Reporting.Application;

namespace Bootstrapper;

/// <summary>
/// El origen del reporte de clientes (Clientes CUC): <c>customers.customers</c>, con la
/// clasificacion resuelta por join y la geografia por <c>ICustomerGeographyLookup</c> — el mismo
/// puerto que ya usa el listado de <c>customers</c>, que el composition root cablea contra
/// <c>Geography</c>. Ver <see cref="SalesReportSource"/> sobre por que este adaptador vive aca.
///
/// **El departamento no esta en <c>Customer</c>**: la entidad solo guarda <c>CityId</c>. Por eso
/// el filtro por departamento se traduce primero a que ciudades caen dentro (una consulta), y el
/// nombre del departamento sale despues de resolver las ciudades de la pagina (otra consulta) —
/// nunca una por fila.
/// </summary>
internal sealed class CustomerReportSource(
    CustomersDbContext customers,
    ICustomerGeographyLookup geographyLookup) : ICustomerReportSource
{
    public async Task<(IReadOnlyList<CustomerReportItemDto> Items, int Total)> ListAsync(
        CustomerReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = await BuildQueryAsync(criteria, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (await ToDtosAsync(rows, cancellationToken), total);
    }

    public async Task<IReadOnlyList<CustomerReportItemDto>> ListForExportAsync(
        CustomerReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = await BuildQueryAsync(criteria, cancellationToken);
        var rows = await query.Take(limit).ToListAsync(cancellationToken);
        return await ToDtosAsync(rows, cancellationToken);
    }

    private async Task<IQueryable<CustomerRow>> BuildQueryAsync(
        CustomerReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        var query = customers.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == criteria.TenantId);

        // Nulo trae los dos estados, que es lo que el contrato dice de isActive ausente.
        if (criteria.IsActive is { } isActive)
        {
            query = query.Where(customer => customer.IsActive == isActive);
        }

        if (criteria.ClassificationId is { } classificationId)
        {
            var classification = new ClientClassificationId(classificationId);
            query = query.Where(customer => customer.ClassificationId == classification);
        }

        if (criteria.DepartmentId is { } departmentId)
        {
            var cityIds = await geographyLookup.ListCityIdsByDepartmentsAsync(
                [departmentId], cancellationToken);
            // Un departamento sin ciudades no puede tener clientes: la lista vacia hace que el
            // Contains no matchee nada, que es la respuesta correcta y no "todos".
            // La ciudad del cliente es la de su direccion principal (CLI-DIR-01): el reporte
            // agrupa por donde esta el cliente, no por cada bodega que tenga.
            query = query.Where(customer =>
                customer.Addresses.Any(address =>
                    address.IsPrincipal && cityIds.Contains(address.CityId)));
        }

        var joined = from customer in query
                     join classification in customers.ClientClassifications.AsNoTracking()
                         on customer.ClassificationId equals classification.Id into matches
                     from classification in matches.DefaultIfEmpty()
                     select new { customer, classification };

        // Join izquierdo y no interno, a diferencia del historico de precios: entre customers y
        // client_classifications no hay FK real, asi que una clasificacion borrada dejaria al
        // cliente fuera del reporte en vez de mostrarlo sin clasificacion.
        //
        // Ver SalesReportSource sobre el orden total.
        return joined
            .OrderBy(row => row.customer.Cuc)
            .Select(row => new CustomerRow(
                row.customer.Id,
                row.customer.Cuc,
                row.customer.Name,
                row.customer.IdentificationType,
                row.customer.IdentificationNumber,
                row.customer.ClassificationId,
                row.classification == null ? null : row.classification.Name,
                row.customer.Addresses
                    .Where(address => address.IsPrincipal)
                    .Select(address => address.CityId)
                    .FirstOrDefault(),
                row.customer.IsActive,
                row.customer.CreatedAt));
    }

    private async Task<IReadOnlyList<CustomerReportItemDto>> ToDtosAsync(
        IReadOnlyList<CustomerRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var cities = await geographyLookup.FindCitiesAsync(
            rows.Select(row => row.CityId).ToArray(), cancellationToken);

        return rows
            .Select(row =>
            {
                cities.TryGetValue(row.CityId, out var city);
                return new CustomerReportItemDto(
                    row.CustomerId.Value,
                    row.Cuc,
                    row.Name,
                    // El nombre del enum (`Nit`) y no el valor de cable en mayusculas (`NIT`) que
                    // usan los endpoints de customers: es lo que fija el contrato de este reporte.
                    row.IdentificationType.ToString(),
                    row.IdentificationNumber,
                    row.ClassificationId.Value,
                    row.ClassificationName,
                    city?.DepartmentId,
                    city?.DepartmentName,
                    row.CityId,
                    city?.CityName,
                    row.IsActive,
                    row.CreatedAt);
            })
            .ToArray();
    }

    private sealed record CustomerRow(
        CustomerId CustomerId,
        string Cuc,
        string Name,
        IdentificationType IdentificationType,
        string IdentificationNumber,
        ClientClassificationId ClassificationId,
        string? ClassificationName,
        Guid CityId,
        bool IsActive,
        DateTimeOffset CreatedAt);
}
