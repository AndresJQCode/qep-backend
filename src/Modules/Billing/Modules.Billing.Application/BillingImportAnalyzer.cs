using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>
/// Lo que la vista previa y la importación ven del archivo: las filas planeadas, los errores por fila y la «hoy» del tenant con
/// la que se contaron los cobros. <c>TotalRows</c> son las filas de datos no vacías.
/// </summary>
public sealed record BillingImportAnalysis(
    string FileName,
    int TotalRows,
    BillingImportPlan Plan,
    IReadOnlyList<ImportRowErrorDto> Errors,
    DateOnly Today);

/// <summary>
/// El camino compartido de <c>POST /import/preview</c> y <c>POST /import</c> (§3.2b): archivo → reglas de fila → duplicados del
/// archivo → emparejamiento → plan. No escribe. Como no hay staging, la importación lo vuelve a correr entero sobre el mismo
/// archivo: lo que cambió entre la vista previa y la confirmación (un servicio creado a mano, un encargado que perdió el permiso)
/// sale como error de esa fila.
/// </summary>
public sealed class BillingImportAnalyzer(
    IExcelBillingImporter importer,
    IValidator<ExcelBillingRow> rowRules,
    IBillingPhoneNormalizer phones,
    IBillingCustomerLookup customers,
    ICustomerBillingProfileRepository profiles,
    IBillingServiceRepository services,
    IBillingMemberDirectory members,
    ITenantClock tenantClock,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IClock clock)
{
    /// <summary>§3.2b: tope de filas por archivo.</summary>
    public const int MaxRows = 500;

    /// <summary>10 MB, el mismo tope que la importación de clientes.</summary>
    public const long MaxSizeInBytes = 10 * 1024 * 1024;

    private static readonly string[] AcceptedExtensions = [".xlsx", ".xls"];

    public async Task<BillingImportAnalysis> AnalyzeAsync(
        Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var rows = await importer.ReadAsync(content, cancellationToken);
        if (rows.Count > MaxRows)
        {
            throw new BillingDomainException(
                BillingErrorCodes.ImportTooManyRows, $"The file has {rows.Count} rows; the limit is {MaxRows}.");
        }

        var errors = new List<ImportRowErrorDto>();
        var parsed = new List<BillingImportRow>(rows.Count);
        foreach (var row in rows)
        {
            var validation = await rowRules.ValidateAsync(row, cancellationToken);
            if (validation.IsValid)
            {
                parsed.Add(BillingImportRow.From(row, phones));
            }
            else
            {
                errors.AddRange(validation.Errors.Select(failure =>
                    new ImportRowErrorDto(row.RowNumber, failure.ErrorCode, failure.ErrorMessage, failure.PropertyName)));
            }
        }

        var (firsts, duplicates) = ExcelBillingRowDeduplicator.Partition(parsed);
        errors.AddRange(duplicates.Select(row => new ImportRowErrorDto(
            row.RowNumber,
            BillingErrorCodes.ImportRowDuplicateService,
            "An earlier row of this file has the same customer and service.",
            nameof(ExcelBillingRow.ServiceName))));

        var context = await BuildContextAsync(tenantId, firsts, cancellationToken);
        var plan = BillingImportPlanner.Plan(firsts, context);
        errors.AddRange(plan.Errors);

        return new BillingImportAnalysis(
            name,
            rows.Count,
            plan,
            errors.OrderBy(error => error.RowNumber).ToArray(),
            context.Today);
    }

    private async Task<BillingImportContext> BuildContextAsync(
        Guid tenantId, IReadOnlyList<BillingImportRow> rows, CancellationToken cancellationToken)
    {
        var matchesByRow = new Dictionary<int, IReadOnlyList<BillingCustomerRef>>();
        foreach (var row in rows)
        {
            matchesByRow[row.RowNumber] = await MatchAsync(tenantId, row, cancellationToken);
        }

        var matchedIds = matchesByRow.Values.SelectMany(matches => matches).Select(customer => customer.Id).Distinct().ToArray();
        var withProfile = (await profiles.ListByCustomersAsync(tenantId, matchedIds, cancellationToken))
            .Select(profile => profile.CustomerId)
            .ToHashSet();
        var openNames = (await services.ListOpenByCustomersAsync(tenantId, matchedIds, cancellationToken))
            .GroupBy(service => service.CustomerId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<string>)group.Select(service => service.Name).ToArray());

        var managers = new Dictionary<string, BillingMemberRef>(StringComparer.Ordinal);
        foreach (var email in rows.Select(row => row.AssigneeEmail).Distinct(StringComparer.Ordinal))
        {
            if (await members.FindManagerByEmailAsync(tenantId, email, cancellationToken) is { } member)
            {
                managers[email] = member;
            }
        }

        var today = (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;
        var defaultCurrency = await tenantDefaultCurrency.GetAsync(tenantId, cancellationToken);
        return new BillingImportContext(today, clock.UtcNow, defaultCurrency, matchesByRow, withProfile, openNames, managers);
    }

    /// <summary>
    /// §3.2b: documento → E.164 → correo. Decide la primera clave que la fila trae y que encuentra algo; sin ninguna, la fila crea
    /// el cliente. Seguir a la siguiente clave cuando el documento no encuentra nada evita duplicar al cliente incompleto que
    /// llegó por WhatsApp (tiene teléfono, no documento).
    /// </summary>
    private async Task<IReadOnlyList<BillingCustomerRef>> MatchAsync(
        Guid tenantId, BillingImportRow row, CancellationToken cancellationToken)
    {
        if (row.IdentificationType is { } type && row.IdentificationNumber is { } number)
        {
            var byDocument = await customers.FindByIdentificationAsync(tenantId, type, number, cancellationToken);
            if (byDocument.Count > 0)
            {
                return byDocument;
            }
        }

        if (row.PhoneE164 is { } phone)
        {
            var byPhone = await customers.FindByPhoneE164Async(tenantId, phone, cancellationToken);
            if (byPhone.Count > 0)
            {
                return byPhone;
            }
        }

        return row.Email is { } email
            ? await customers.FindByEmailAsync(tenantId, email, cancellationToken)
            : [];
    }

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        var trimmed = fileName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || sizeInBytes <= 0)
        {
            throw new BillingDomainException(BillingErrorCodes.ImportFileRequired, "An Excel file is required.");
        }

        if (!AcceptedExtensions.Any(extension => trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BillingDomainException(BillingErrorCodes.ImportExtensionInvalid, "Only .xlsx and .xls files are accepted.");
        }

        return sizeInBytes > MaxSizeInBytes
            ? throw new BillingDomainException(BillingErrorCodes.ImportTooLarge, "The file cannot exceed 10 MB.")
            : trimmed;
    }
}
