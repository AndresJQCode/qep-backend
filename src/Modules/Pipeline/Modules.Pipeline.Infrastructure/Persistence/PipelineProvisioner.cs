using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-13 D4 y D31: Tenancy no avisa cuando se activa un módulo (H1), así que el embudo se crea en el primer acceso. Una
/// transacción propia con <c>INSERT … ON CONFLICT (tenant_id) DO NOTHING RETURNING id</c>: dos peticiones a la vez esperan el
/// índice único, una gana y la otra no inserta nada; las etapas y los motivos sólo se siembran si esta llamada creó el embudo.
/// SQL crudo y no <c>SaveChanges</c> para no arrastrar cambios ajenos que el contexto ya tuviera rastreados.
/// </summary>
internal sealed class PipelineProvisioner(PipelineDbContext dbContext, IClock clock) : IPipelineProvisioner
{
    public async Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(tenantId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var now = clock.UtcNow;
        var seed = SalesPipeline.CreateDefault(tenantId, now);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var inserted = await dbContext.Database.SqlQuery<Guid>($"""
                INSERT INTO pipeline.pipelines (id, tenant_id, name, version, created_at, updated_at)
                VALUES ({seed.Id}, {tenantId}, {seed.Name}, {seed.Version}, {now}, {now})
                ON CONFLICT (tenant_id) DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (inserted.Count == 1)
            {
                foreach (var stage in seed.OrderedStages)
                {
                    await dbContext.Database.ExecuteSqlAsync($"""
                        INSERT INTO pipeline.stages (id, pipeline_id, name, position, probability, kind, stall_days, is_quotation_stage)
                        VALUES ({stage.Id}, {seed.Id}, {stage.Name}, {stage.Position}, {stage.Probability}, {stage.Kind.ToString()}, {stage.StallDays}, {stage.IsQuotationStage})
                        """, cancellationToken);
                }

                await SeedReasonsAsync(tenantId, ReasonKind.Loss, PipelineSeed.LossReasons, now, cancellationToken);
                await SeedReasonsAsync(tenantId, ReasonKind.Dismiss, PipelineSeed.DismissReasons, now, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await LoadAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"The pipeline of tenant '{tenantId}' was not provisioned.");
    }

    private Task<SalesPipeline?> LoadAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Pipelines.Include(pipeline => pipeline.Stages).SingleOrDefaultAsync(pipeline => pipeline.TenantId == tenantId, cancellationToken);

    private async Task SeedReasonsAsync(
        Guid tenantId, ReasonKind kind, IReadOnlyList<string> names, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var index = 0; index < names.Count; index++)
        {
            var reason = PipelineReason.Create(tenantId, kind, names[index], index + 1, now);
            await dbContext.Database.ExecuteSqlAsync($"""
                INSERT INTO pipeline.reasons (id, tenant_id, kind, name, position, is_active, created_at, updated_at)
                VALUES ({reason.Id}, {tenantId}, {kind.ToString()}, {reason.Name}, {reason.Position}, TRUE, {now}, {now})
                """, cancellationToken);
        }
    }
}
