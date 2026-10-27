using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Spec 2026-10-26 D-C12: el recorrido de los pedidos que ya estaban importados antes de existir la bandeja. Geography no ve
/// Shopify: el adaptador (<c>HistoricalCityBackfillAdapter</c>, Bootstrapper) recorre el espejo y reporta cada ciudad sin
/// resolver por <see cref="IUnresolvedCityReporter"/>, que es idempotente por la fila del espejo (correrlo dos veces no
/// reporta nada nuevo).
/// </summary>
public interface IHistoricalCityBackfill
{
    Task<CityBackfillResultDto> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Corre el recorrido a mano desde la consola (S6). Síncrono: al desplegar son pocos cientos de filas.</summary>
public sealed record RunCityBackfillCommand(TenantId TenantId) : ICommand<CityBackfillResultDto>;

/// <summary>La auditoría (en el tenant operador) lleva los cinco conteos, también en cero.</summary>
public sealed class RunCityBackfillHandler(
    IHistoricalCityBackfill backfill,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
    : ICommandHandler<RunCityBackfillCommand, CityBackfillResultDto>
{
    public async Task<CityBackfillResultDto> HandleAsync(RunCityBackfillCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        var result = await backfill.RunAsync(cancellationToken);
        audit.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            GeographyAuditActions.BackfillRan,
            GeographyAuditActions.BackfillResourceType,
            GeographyAuditActions.BackfillResourceType,
            JsonSerializer.Serialize(new
            {
                scanned = result.Scanned,
                reported = result.Reported,
                alreadyReported = result.AlreadyReported,
                nowMatching = result.NowMatching,
                skipped = result.Skipped,
            }),
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
