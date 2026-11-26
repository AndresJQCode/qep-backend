using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

/// <summary>Los catálogos que la plantilla y el análisis leen del tenant y de Geografía, una vez por request.</summary>
public sealed class EmployeeImportCatalogs(
    IHrSettingsRepository settings,
    IHrCompanyLookup companies,
    IHrGeographyLookup geography)
{
    /// <summary>La configuración no archivada con las empresas <b>activas</b> del tenant.</summary>
    public async Task<HrSettingsIndex> LoadSettingsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        new(
            await settings.ListAsync(tenantId, cancellationToken),
            await companies.ListActiveAsync(tenantId, cancellationToken));

    public async Task<HrCountryIndex> LoadCountriesAsync(CancellationToken cancellationToken) =>
        new(await geography.ListCountriesAsync(cancellationToken));

    public async Task<HrCityIndex> LoadCitiesAsync(CancellationToken cancellationToken) =>
        new(await geography.ListAllCitiesAsync(cancellationToken));
}

/// <summary>
/// Lo que la vista previa y la confirmación ven del archivo. <c>TotalRows</c> son las filas con datos. <c>Settings</c> y
/// <c>LegalTable</c> son lo que se leyó una vez para analizar: la vista previa pinta nombres con ellos y la confirmación calcula los
/// pisos a la fecha de ingreso de cada fila sin volver a leer.
/// </summary>
public sealed record EmployeeImportAnalysis(
    string FileName,
    int TotalRows,
    IReadOnlyList<ParsedEmployeeRow> ValidRows,
    IReadOnlyList<EmployeeImportRowIssueDto> Errors,
    IReadOnlyList<EmployeeImportRowIssueDto> Warnings,
    HrSettingsIndex Settings,
    IReadOnlyList<LegalParameterValue> LegalTable);

/// <summary>
/// El camino compartido de la vista previa y la confirmación: configuración lista → archivo → filas → reglas de fila → documentos
/// repetidos en el archivo → documentos que ya existen. No escribe. Como no hay staging, la confirmación vuelve a correr todo sobre
/// el mismo archivo: lo que cambió entre las dos (un empleado creado a mano con un documento del Excel) sale como error de esa fila.
/// </summary>
public sealed class EmployeeImportAnalyzer(
    IExcelEmployeeImporter importer,
    HrSetup setup,
    EmployeeImportCatalogs catalogs,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ILegalParameterSource legalParameters,
    IHrPhoneNormalizer phones,
    IEmployeeRepository employees)
{
    public async Task<EmployeeImportAnalysis> AnalyzeAsync(
        Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        if (!await setup.IsCompleteAsync(tenantId, cancellationToken))
        {
            throw new HrDomainException(
                HrErrorCodes.SetupIncomplete, "Configura una empresa activa con sede y centro de trabajo, y un cargo, antes de importar.");
        }

        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var rows = await importer.ReadAsync(content, cancellationToken);
        if (rows.Count == 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportEmpty, "El archivo no tiene filas con datos.");
        }

        if (rows.Count > HrImportColumns.MaxRows)
        {
            throw new HrDomainException(
                HrErrorCodes.ImportTooManyRows, $"El archivo tiene más de {HrImportColumns.MaxRows} filas.");
        }

        var settings = await catalogs.LoadSettingsAsync(tenantId, cancellationToken);
        var legalTable = await legalParameters.ListAsync(cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var tenantCountry = EmployeeCountry.Effective(
            null, (await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken))?.Country);
        var context = new EmployeeImportContext(
            calendar.Today,
            tenantCountry,
            await catalogs.LoadCountriesAsync(cancellationToken),
            await catalogs.LoadCitiesAsync(cancellationToken),
            settings,
            phones,
            legalTable);

        var errors = new List<EmployeeImportRowIssueDto>();
        var warnings = new List<EmployeeImportRowIssueDto>();
        var valid = new Dictionary<int, ParsedEmployeeRow>();
        foreach (var row in rows)
        {
            var result = EmployeeImportRowParser.Parse(row, context);
            errors.AddRange(result.Errors);
            if (result.Row is not null)
            {
                valid[row.RowNumber] = result.Row;
                warnings.AddRange(result.Warnings);
            }
        }

        // El duplicado se mira entre todas las filas con número, también las que ya traen otro error: las dos quedan marcadas.
        foreach (var group in rows
                     .Select(row => (row.RowNumber, Key: HrText.Fold(row.Cells.GetValueOrDefault(HrImportColumns.DocumentNumber))))
                     .Where(entry => entry.Key is not null)
                     .GroupBy(entry => entry.Key!, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var (rowNumber, _) in group)
            {
                errors.Add(new EmployeeImportRowIssueDto(
                    rowNumber,
                    HrErrorCodes.ImportRow.DocumentDuplicatedInFile,
                    "El número de documento se repite en otra fila del archivo.",
                    "documentNumber"));
                valid.Remove(rowNumber);
            }
        }

        if (valid.Count > 0)
        {
            var taken = await employees.FindTakenDocumentsAsync(
                tenantId, valid.Values.Select(row => row.Profile.DocumentNumber).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
            foreach (var row in valid.Values.Where(row => taken.Contains(row.Profile.DocumentNumber)).ToList())
            {
                errors.Add(new EmployeeImportRowIssueDto(
                    row.RowNumber, HrErrorCodes.ImportRow.DocumentTaken, "Ya hay un empleado con ese documento.", "documentNumber"));
                valid.Remove(row.RowNumber);
            }
        }

        // Un aviso sólo se lista si su fila quedó válida.
        return new EmployeeImportAnalysis(
            name,
            rows.Count,
            valid.Values.OrderBy(row => row.RowNumber).ToList(),
            errors.OrderBy(issue => issue.RowNumber).ThenBy(issue => issue.Field, StringComparer.Ordinal).ToList(),
            warnings.Where(warning => valid.ContainsKey(warning.RowNumber))
                .OrderBy(issue => issue.RowNumber).ThenBy(issue => issue.Field, StringComparer.Ordinal).ToList(),
            settings,
            legalTable);
    }

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || sizeInBytes <= 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportFileRequired, "Adjunta un archivo .xlsx.");
        }

        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new HrDomainException(HrErrorCodes.ImportExtensionInvalid, "Sólo se aceptan archivos .xlsx.");
        }

        if (sizeInBytes > HrImportColumns.MaxSizeInBytes)
        {
            throw new HrDomainException(
                HrErrorCodes.ImportTooLarge, $"El archivo pasa de {HrImportColumns.MaxSizeInBytes / (1024 * 1024)} MB.");
        }

        return fileName.Trim();
    }
}
