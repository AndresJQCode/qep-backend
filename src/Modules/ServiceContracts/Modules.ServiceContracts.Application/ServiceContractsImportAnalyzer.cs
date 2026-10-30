using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// Lo que la vista previa y la importación ven del archivo: las filas planeadas, los errores por fila y la «hoy» del tenant con
/// la que se contaron los cobros. <c>TotalRows</c> son las filas de datos no vacías.
/// </summary>
public sealed record ServiceContractsImportAnalysis(
    string FileName,
    int TotalRows,
    ServiceContractsImportPlan Plan,
    IReadOnlyList<ImportRowErrorDto> Errors,
    DateOnly Today);

/// <summary>
/// El camino compartido de <c>POST /import/preview</c> y <c>POST /import</c> (§3.2b): archivo → reglas de fila → duplicados del
/// archivo → emparejamiento → plan. No escribe. Como no hay staging, la importación lo vuelve a correr entero sobre el mismo
/// archivo: lo que cambió entre la vista previa y la confirmación (un servicio creado a mano, un encargado que perdió el permiso)
/// sale como error de esa fila.
/// </summary>
public sealed class ServiceContractsImportAnalyzer(
    IExcelServiceContractsImporter importer,
    IValidator<ExcelServiceContractsRow> rowRules,
    IServiceContractsPhoneNormalizer phones,
    IServiceContractsCustomerLookup customers,
    ICustomerBillingProfileRepository profiles,
    IServiceContractRepository services,
    IServiceContractsMemberDirectory members,
    ITenantClock tenantClock,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IClock clock)
{
    /// <summary>§3.2b: tope de filas por archivo.</summary>
    public const int MaxRows = 500;

    /// <summary>10 MB, el mismo tope que la importación de clientes.</summary>
    public const long MaxSizeInBytes = 10 * 1024 * 1024;

    private static readonly string[] AcceptedExtensions = [".xlsx", ".xls"];

    public async Task<ServiceContractsImportAnalysis> AnalyzeAsync(
        Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var rows = await importer.ReadAsync(content, cancellationToken);
        if (rows.Count > MaxRows)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ImportTooManyRows, $"The file has {rows.Count} rows; the limit is {MaxRows}.");
        }

        var errors = new List<ImportRowErrorDto>();
        var parsed = new List<ServiceContractsImportRow>(rows.Count);
        foreach (var row in rows)
        {
            var validation = await rowRules.ValidateAsync(row, cancellationToken);
            if (validation.IsValid)
            {
                parsed.Add(ServiceContractsImportRow.From(row, phones));
            }
            else
            {
                errors.AddRange(validation.Errors.Select(failure =>
                    new ImportRowErrorDto(row.RowNumber, failure.ErrorCode, failure.ErrorMessage, failure.PropertyName)));
            }
        }

        var matchesByRow = new Dictionary<int, IReadOnlyList<ServiceContractsCustomerRef>>();
        foreach (var row in parsed)
        {
            matchesByRow[row.RowNumber] = await MatchAsync(tenantId, row, cancellationToken);
        }

        var (newCustomerKeys, ambiguous) = LinkNewCustomers(parsed, matchesByRow);
        errors.AddRange(ambiguous.Select(row => new ImportRowErrorDto(
            row.RowNumber, ServiceContractsErrorCodes.ImportRowCustomerAmbiguous, "More than one customer matches this row.", null)));

        // El duplicado se mira sobre el cliente ya resuelto, no sobre la celda: la misma persona alcanzada por documento en una
        // fila y por teléfono en otra es un solo cliente, y su servicio no puede nacer dos veces.
        var (firsts, duplicates) = ExcelServiceContractsRowDeduplicator.Partition(
            parsed.Except(ambiguous).ToArray(),
            row => matchesByRow[row.RowNumber] switch
            {
                [var matched] => $"customer:{matched.Id}",
                [] => newCustomerKeys.GetValueOrDefault(row.RowNumber),
                _ => null,
            });
        errors.AddRange(duplicates.Select(row => new ImportRowErrorDto(
            row.RowNumber,
            ServiceContractsErrorCodes.ImportRowDuplicateService,
            "An earlier row of this file has the same customer and service.",
            nameof(ExcelServiceContractsRow.ServiceName))));

        var context = await BuildContextAsync(tenantId, firsts, matchesByRow, newCustomerKeys, cancellationToken);
        var plan = ServiceContractsImportPlanner.Plan(firsts, context);
        errors.AddRange(plan.Errors);

        return new ServiceContractsImportAnalysis(
            name,
            rows.Count,
            plan,
            errors.OrderBy(error => error.RowNumber).ToArray(),
            context.Today);
    }

    private async Task<ServiceContractsImportContext> BuildContextAsync(
        Guid tenantId,
        IReadOnlyList<ServiceContractsImportRow> rows,
        Dictionary<int, IReadOnlyList<ServiceContractsCustomerRef>> allMatches,
        Dictionary<int, string> newCustomerKeys,
        CancellationToken cancellationToken)
    {
        var matchesByRow = rows.ToDictionary(row => row.RowNumber, row => allMatches[row.RowNumber]);
        var matchedIds = matchesByRow.Values.SelectMany(matches => matches).Select(customer => customer.Id).Distinct().ToArray();
        var withProfile = (await profiles.ListByCustomersAsync(tenantId, matchedIds, cancellationToken))
            .Select(profile => profile.CustomerId)
            .ToHashSet();
        var openNames = (await services.ListOpenByCustomersAsync(tenantId, matchedIds, cancellationToken))
            .GroupBy(service => service.CustomerId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<string>)group.Select(service => service.Name).ToArray());

        var managers = new Dictionary<string, ServiceContractsMemberRef>(StringComparer.Ordinal);
        foreach (var email in rows.Select(row => row.AssigneeEmail).Distinct(StringComparer.Ordinal))
        {
            if (await members.FindManagerByEmailAsync(tenantId, email, cancellationToken) is { } member)
            {
                managers[email] = member;
            }
        }

        var today = (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;
        var defaultCurrency = await tenantDefaultCurrency.GetAsync(tenantId, cancellationToken);
        return new ServiceContractsImportContext(
            today, clock.UtcNow, defaultCurrency, matchesByRow, withProfile, openNames, managers, newCustomerKeys);
    }

    /// <summary>
    /// §3.2b, «el primero que aplica decide»: aplica la primera clave que la fila trae —documento, si no E.164, si no correo— y
    /// sólo esa. Si no encuentra a nadie, el cliente es nuevo: un documento nuevo no se empareja con quien tenga su teléfono,
    /// porque serían dos personas con un dato de contacto compartido.
    /// </summary>
    private async Task<IReadOnlyList<ServiceContractsCustomerRef>> MatchAsync(
        Guid tenantId, ServiceContractsImportRow row, CancellationToken cancellationToken)
    {
        if (row.IdentificationType is { } type && row.IdentificationNumber is { } number)
        {
            return await customers.FindByIdentificationAsync(tenantId, type, number, cancellationToken);
        }

        if (row.PhoneE164 is { } phone)
        {
            return await customers.FindByPhoneE164Async(tenantId, phone, cancellationToken);
        }

        return row.Email is { } email
            ? await customers.FindByEmailAsync(tenantId, email, cancellationToken)
            : [];
    }

    /// <summary>
    /// El mismo emparejamiento contra los clientes que el archivo va a crear, que la base todavía no ve. Una fila sin documento
    /// cuyo teléfono (o correo, si no trae teléfono) es el de un cliente nuevo de una fila anterior se suma a ese cliente en vez
    /// de crear otro; si coincide con más de uno, o además con uno de la base, es <c>customer_ambiguous</c>, igual que lo sería
    /// si las filas se importaran de a una. Devuelve la clave del cliente nuevo de cada fila que crea o se suma a uno.
    /// </summary>
    private static (Dictionary<int, string> Keys, List<ServiceContractsImportRow> Ambiguous) LinkNewCustomers(
        List<ServiceContractsImportRow> rows, Dictionary<int, IReadOnlyList<ServiceContractsCustomerRef>> matchesByRow)
    {
        var keys = new Dictionary<int, string>();
        var ambiguous = new List<ServiceContractsImportRow>();
        var created = new List<(string Key, string? Phone, string? Email)>();
        foreach (var row in rows)
        {
            var hasDocument = row.IdentificationType is not null && row.IdentificationNumber is not null;
            var joined = hasDocument
                ? []
                : created
                    .Where(customer => row.PhoneE164 is { } phone
                        ? customer.Phone == phone
                        : row.Email is { } email && string.Equals(customer.Email, email, StringComparison.OrdinalIgnoreCase))
                    .Select(customer => customer.Key)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            var matches = matchesByRow[row.RowNumber].Count;
            if (matches + joined.Length > 1)
            {
                ambiguous.Add(row);
                continue;
            }

            if (matches == 1)
            {
                continue;
            }

            if (joined is [var existingKey])
            {
                keys[row.RowNumber] = existingKey;
            }
            else if (row.CustomerKey is { } key)
            {
                keys[row.RowNumber] = key;
                if (created.All(customer => customer.Key != key))
                {
                    created.Add((key, row.PhoneE164, row.Email));
                }
            }
        }

        return (keys, ambiguous);
    }

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        var trimmed = fileName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || sizeInBytes <= 0)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ImportFileRequired, "An Excel file is required.");
        }

        if (!AcceptedExtensions.Any(extension => trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ImportExtensionInvalid, "Only .xlsx and .xls files are accepted.");
        }

        return sizeInBytes > MaxSizeInBytes
            ? throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ImportTooLarge, "The file cannot exceed 10 MB.")
            : trimmed;
    }
}
