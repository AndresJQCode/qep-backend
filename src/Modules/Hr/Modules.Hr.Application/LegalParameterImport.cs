using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

// La carga anual de valores legales por Excel (spec 2026-11-22, «Carga por Excel»). Como el resto de la consola, la autorización de
// operador va primero en cada handler y no revisa el módulo `hr`. Las tres operaciones piden `manage`: descargar la plantilla y ver la
// vista previa son pasos de cargar, y quien sólo puede leer no tiene por qué hacerlos.

public sealed record GetLegalParameterImportTemplateQuery(TenantId TenantId) : IQuery<LegalParameterImportTemplateFile>;

/// <summary>El Excel subido a <c>POST /import/preview</c>. <c>Content</c> es el stream del <c>IFormFile</c>, vivo mientras dura el request.</summary>
public sealed record PreviewLegalParameterImportCommand(TenantId TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<LegalParameterImportPreviewDto>;

public sealed record ImportLegalParametersCommand(TenantId TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<LegalParameterImportResultDto>;

/// <summary>
/// La plantilla del año que viene: las 30 claves con el valor que seguiría rigiendo el 1 de enero siguiente (el último con fecha menor
/// o igual a esa, no el de hoy: si ya hay una fila futura, arrastrar el valor de hoy la pisaría) y el conjunto de tramos vigente.
/// «Hoy» sale del calendario del tenant operador.
/// </summary>
public sealed class GetLegalParameterImportTemplateHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    ILegalParameterRepository repository,
    ITenantClock tenantClock,
    ILegalParameterTemplateBuilder templateBuilder)
    : IQueryHandler<GetLegalParameterImportTemplateQuery, LegalParameterImportTemplateFile>
{
    public async Task<LegalParameterImportTemplateFile> HandleAsync(
        GetLegalParameterImportTemplateQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var today = (await tenantClock.GetAsync(query.TenantId.Value, cancellationToken)).Today;
        var nextYearStart = new DateOnly(today.Year + 1, 1, 1);
        var values = await repository.ListValuesAsync(cancellationToken);
        var bracketRows = await repository.ListBracketsAsync(cancellationToken);

        var parameters = Enum.GetValues<LegalParameterKey>()
            .Select(key =>
            {
                var label = LegalParameterLabels.Of(key);
                var inForce = values
                    .Where(value => value.Key == key && value.EffectiveFrom <= nextYearStart)
                    .MaxBy(value => value.EffectiveFrom);
                return new LegalParameterTemplateRow(key, label.Name, label.Unit, inForce?.Value, inForce?.EffectiveFrom);
            })
            .ToList();

        var currentSet = bracketRows
            .Where(row => row.Key == LegalBracketKey.SolidarityFund && row.EffectiveFrom <= nextYearStart)
            .GroupBy(row => row.EffectiveFrom)
            .OrderByDescending(group => group.Key)
            .FirstOrDefault()?
            .OrderBy(row => row.LowerBound)
            .Select(row => new LegalBracketInput(row.LowerBound, row.UpperBound, row.Rate))
            .ToList() ?? [];

        var content = templateBuilder.Build(new LegalParameterTemplateData(nextYearStart, parameters, currentSet));
        return new LegalParameterImportTemplateFile(content, LegalParameterImportColumns.TemplateFileName(nextYearStart.Year));
    }
}

/// <summary>La vista previa: el mismo análisis que la confirmación, sin escribir nada.</summary>
public sealed class PreviewLegalParameterImportHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParameterImportAnalyzer analyzer)
    : ICommandHandler<PreviewLegalParameterImportCommand, LegalParameterImportPreviewDto>
{
    public async Task<LegalParameterImportPreviewDto> HandleAsync(
        PreviewLegalParameterImportCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);
        return analysis.Preview;
    }
}

/// <summary>
/// La confirmación, todo o nada: corre el mismo análisis que la vista previa y, con algún error, responde 422
/// <c>hr.legal_parameter.import.rows_invalid</c> sin escribir. Si no, aplica todo por <see cref="LegalParameterWriter"/> con un lote
/// y el nombre del archivo: una sola unidad de trabajo, un solo aumento de versión de la caché. Un <c>Carry</c> se escribe como una
/// fila nueva y se cuenta aparte en el resultado.
/// </summary>
public sealed class ImportLegalParametersHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock,
    LegalParameterImportAnalyzer analyzer,
    LegalParameterWriter writer)
    : ICommandHandler<ImportLegalParametersCommand, LegalParameterImportResultDto>
{
    public async Task<LegalParameterImportResultDto> HandleAsync(
        ImportLegalParametersCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);
        if (!analysis.Preview.CanApply)
        {
            throw new HrDomainException(
                HrErrorCodes.LegalParameterImportRowsInvalid, "El archivo tiene filas con errores. Corrígelas y vuelve a intentar.");
        }

        var receivedAt = clock.UtcNow;
        var batchId = Guid.CreateVersion7();
        var summary = analysis.Preview.Summary;
        if (analysis.ParameterEdits.Count > 0 || analysis.BracketEdits.Count > 0)
        {
            await writer.ApplyAsync(
                command.TenantId.Value,
                analysis.ParameterEdits,
                analysis.BracketEdits,
                new LegalParameterChangeOrigin(LegalParameterChangeSource.Excel, batchId, analysis.FileName),
                cancellationToken);
        }

        return new LegalParameterImportResultDto(
            analysis.FileName, receivedAt, batchId, summary.Create, summary.Carry, summary.Update, summary.Unchanged);
    }
}
