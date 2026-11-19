using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>Chatbot CRM action spec (2026-11-19): a plain read of the open stages, without tracking and without provisioning.</summary>
internal sealed class PipelineStageLookup(PipelineDbContext dbContext) : IPipelineStageLookup
{
    public async Task<IReadOnlyList<PipelineStageRef>> ListOpenAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Stages
            .AsNoTracking()
            .Where(stage => stage.Kind == StageKind.Open
                && dbContext.Pipelines.Any(pipeline => pipeline.Id == stage.PipelineId && pipeline.TenantId == tenantId))
            .OrderBy(stage => stage.Position)
            .Select(stage => new PipelineStageRef(stage.Id, stage.Name))
            .ToListAsync(cancellationToken);
}
