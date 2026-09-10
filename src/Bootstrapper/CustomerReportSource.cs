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

    public async Task<CustomerReportAggregate> SummarizeAsync(
        CustomerReportCriteria criteria,
        int rankSize,
        CancellationToken cancellationToken)
    {
        var filtered = await FilterCustomersAsync(criteria, cancellationToken);

        // GroupBy sobre una constante es el "agregar todo el conjunto", que traduce a un SELECT con
        // agregados y sin GROUP BY. Sobre cero filas no devuelve ninguna, y de ahi el fallback: un
        // resumen vacio es cero, nunca nulo.
        var totals = await filtered
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Active = group.Count(customer => customer.IsActive),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (totals is null || totals.Count == 0)
        {
            return new CustomerReportAggregate(0, 0, [], [], []);
        }

        var monthly = await SummarizeByMonthAsync(filtered, cancellationToken);
        var byClassification = await RankClassificationsAsync(
            filtered, rankSize, totals.Count, cancellationToken);
        var byDepartment = await RankDepartmentsAsync(
            filtered, rankSize, cancellationToken);

        return new CustomerReportAggregate(
            totals.Count, totals.Active, monthly, byClassification, byDepartment);
    }

    /// <summary>
    /// Las altas por mes, en **UTC** — el mismo huso en el que <c>ReportDateRange</c> corta el
    /// rango. Agrupar en el huso de la sesion de PostgreSQL pondria un alta del 1 de enero en
    /// diciembre para un tenant en America/Bogota.
    ///
    /// Solo vienen los meses con altas: rellenar los huecos con cero depende del rango que el eje
    /// dibuje, asi que es del frontend.
    /// </summary>
    private static async Task<IReadOnlyList<ReportCountPointDto>> SummarizeByMonthAsync(
        IQueryable<Customer> filtered,
        CancellationToken cancellationToken)
    {
        var months = await filtered
            .GroupBy(customer => new
            {
                customer.CreatedAt.UtcDateTime.Year,
                customer.CreatedAt.UtcDateTime.Month,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Count = group.Count(),
            })
            .OrderBy(point => point.Year)
            .ThenBy(point => point.Month)
            .ToListAsync(cancellationToken);

        return months
            .Select(point => new ReportCountPointDto(point.Year, point.Month, point.Count))
            .ToArray();
    }

    /// <summary>
    /// Las clasificaciones con mas clientes, con el resto plegado en "Otros".
    ///
    /// Mismo plegado que <c>PriceChangeReportSource.RankProductsAsync</c>: desempate por id para que
    /// dos clasificaciones empatadas no hagan parpadear el ranking entre dos llamadas identicas, y
    /// el resto **por resta contra el total ya calculado**, no con una consulta mas.
    ///
    /// El nombre se resuelve aparte y puede venir nulo: entre <c>customers</c> y
    /// <c>client_classifications</c> no hay FK real, asi que una clasificacion borrada deja al
    /// cliente en el reporte sin nombre, en vez de sacarlo.
    /// </summary>
    private async Task<IReadOnlyList<CustomerGroupEntryDto>> RankClassificationsAsync(
        IQueryable<Customer> filtered,
        int rankSize,
        int totalCount,
        CancellationToken cancellationToken)
    {
        if (rankSize <= 0)
        {
            return [];
        }

        var distinctCount = await filtered
            .Select(customer => customer.ClassificationId)
            .Distinct()
            .CountAsync(cancellationToken);

        var top = await filtered
            .GroupBy(customer => customer.ClassificationId)
            .Select(group => new { ClassificationId = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.ClassificationId)
            .Take(rankSize)
            .ToListAsync(cancellationToken);

        var ids = top.Select(entry => entry.ClassificationId).ToArray();
        // Una consulta de nombres para el ranking entero, no una por fila.
        var names = await customers.ClientClassifications
            .AsNoTracking()
            .Where(classification => ids.Contains(classification.Id))
            .Select(classification => new { classification.Id, classification.Name })
            .ToDictionaryAsync(classification => classification.Id, cancellationToken);

        var ranked = top
            .Select(entry => new CustomerGroupEntryDto(
                entry.ClassificationId.Value,
                names.GetValueOrDefault(entry.ClassificationId)?.Name,
                EntityCount: 1,
                entry.Count))
            .ToList();

        return Fold(ranked, rankSize, distinctCount, totalCount);
    }

    /// <summary>
    /// Los departamentos con mas clientes, con el resto plegado en "Otros".
    ///
    /// **Este no se agrupa entero en la base y no se puede:** <c>Customer</c> guarda la ciudad de su
    /// direccion principal y nada mas, y el departamento vive del otro lado de la frontera de
    /// <c>Geography</c> — el mismo motivo por el que el filtro por departamento primero traduce a
    /// que ciudades caen dentro. Asi que la base agrupa por ciudad, que devuelve **una fila por
    /// ciudad con clientes y no una por cliente** (1.122 municipios en todo el pais como techo), y
    /// el plegado a departamento se hace sobre ese resultado ya chico.
    ///
    /// Una ciudad que el lookup no resuelve queda afuera del reparto: sin departamento no hay grupo
    /// al cual sumarla, y meterla en "Otros" la contaria como un departamento mas que nadie podria
    /// nombrar. La FK contra <c>geography.cities</c> hace que en la practica no pase.
    /// </summary>
    private async Task<IReadOnlyList<CustomerGroupEntryDto>> RankDepartmentsAsync(
        IQueryable<Customer> filtered,
        int rankSize,
        CancellationToken cancellationToken)
    {
        if (rankSize <= 0)
        {
            return [];
        }

        // La proyeccion a tipo anonimo antes del GroupBy no es adorno: EF no traduce un agregado
        // sobre una proyeccion a record, y agrupar directo por la subconsulta de la direccion
        // principal lo lleva a evaluar en cliente.
        var byCity = await filtered
            .Select(customer => new
            {
                CityId = customer.Addresses
                    .Where(address => address.IsPrincipal)
                    .Select(address => address.CityId)
                    .FirstOrDefault(),
            })
            .GroupBy(row => row.CityId)
            .Select(group => new { CityId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        if (byCity.Count == 0)
        {
            return [];
        }

        var cities = await geographyLookup.FindCitiesAsync(
            byCity.Select(row => row.CityId).ToArray(), cancellationToken);

        var byDepartment = byCity
            .Select(row => new
            {
                City = cities.GetValueOrDefault(row.CityId),
                row.Count,
            })
            .Where(row => row.City is not null)
            .GroupBy(row => new { row.City!.DepartmentId, row.City!.DepartmentName })
            .Select(group => new CustomerGroupEntryDto(
                group.Key.DepartmentId,
                group.Key.DepartmentName,
                EntityCount: 1,
                group.Sum(row => row.Count)))
            // Mismo desempate que el resto de los rankings: sin orden total, dos departamentos
            // empatados se intercambian entre dos llamadas identicas y el ranking parpadea.
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Id)
            .ToList();

        // El total del reparto es la suma de sus grupos y no el de clientes: una ciudad sin
        // resolver quedo afuera arriba, y restar contra el total la convertiria en "Otros".
        var placed = byDepartment.Sum(entry => entry.Count);

        return Fold(byDepartment.Take(rankSize).ToList(), rankSize, byDepartment.Count, placed);
    }

    /// <summary>
    /// La fila "Otros": <c>Id</c> nulo, cuantas entidades pliega y cuantos clientes suman, por resta
    /// contra el total ya calculado. Solo aparece cuando el ranking se lleno y quedo algo afuera.
    /// </summary>
    private static List<CustomerGroupEntryDto> Fold(
        List<CustomerGroupEntryDto> ranked,
        int rankSize,
        int distinctCount,
        int totalCount)
    {
        var remaining = distinctCount - ranked.Count;
        if (ranked.Count == rankSize && remaining > 0)
        {
            ranked.Add(new CustomerGroupEntryDto(
                Id: null,
                Label: null,
                remaining,
                totalCount - ranked.Sum(entry => entry.Count)));
        }

        return ranked;
    }

    /// <summary>
    /// Los filtros del reporte, compartidos por el listado, la exportacion y el resumen: que los
    /// tres salgan de aca es lo que hace imposible que el panel, la tabla y el Excel hablen de
    /// conjuntos distintos.
    /// </summary>
    private async Task<IQueryable<Customer>> FilterCustomersAsync(
        CustomerReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        var query = customers.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == criteria.TenantId);

        // El rango corta por fecha de alta, la unica fecha que tiene un cliente.
        if (criteria.From is { } from)
        {
            var start = ReportDateRange.InclusiveStart(from);
            query = query.Where(customer => customer.CreatedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = ReportDateRange.ExclusiveEnd(to);
            query = query.Where(customer => customer.CreatedAt < end);
        }

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

        return query;
    }

    private async Task<IQueryable<CustomerRow>> BuildQueryAsync(
        CustomerReportCriteria criteria,
        CancellationToken cancellationToken)
    {
        var query = await FilterCustomersAsync(criteria, cancellationToken);

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
