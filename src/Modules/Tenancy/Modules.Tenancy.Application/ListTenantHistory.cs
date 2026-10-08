using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record ListTenantHistoryQuery(TenantId TenantId, TenantId TargetTenantId, string? Module, int Page, int PageSize)
    : IQuery<OperatorHistoryPageDto>;

public sealed class ListTenantHistoryValidator : AbstractValidator<ListTenantHistoryQuery>
{
    public ListTenantHistoryValidator()
    {
        RuleFor(query => query.Module)
            .Must(OperatorInput.IsModuleKey).When(query => query.Module is not null)
            .WithMessage("Unknown module key.");
        RuleFor(query => query.Page).InclusiveBetween(1, OperatorInput.MaxPage);
        RuleFor(query => query.PageSize).InclusiveBetween(1, OperatorInput.MaxPageSize);
    }
}

public sealed class ListTenantHistoryHandler(
    IOperatorTenantReader reader,
    ITenantDirectory tenantDirectory,
    IUserDirectory userDirectory,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListTenantHistoryQuery> validator)
    : IQueryHandler<ListTenantHistoryQuery, OperatorHistoryPageDto>
{
    public async Task<OperatorHistoryPageDto> HandleAsync(ListTenantHistoryQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        // D7: el operador ve todos los tenants, así que aquí el 404 sí es correcto.
        _ = await tenantDirectory.GetStatusAsync(query.TargetTenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");

        var module = query.Module is null ? null : TenantModuleKey.Parse(query.Module);
        var history = await reader.ListHistoryAsync(query.TargetTenantId, module, query.Page, query.PageSize, cancellationToken);

        // Directo desde Tenancy.Application, que ya referencia Identity.Application (ListMemberships.cs:102),
        // y una consulta por actor de la página, no por lote.
        var emails = new Dictionary<Guid, string?>();
        foreach (var actor in history.Batches.Select(batch => batch[0].ActorUserId).Distinct())
        {
            emails[actor] = await userDirectory.GetEmailAsync(actor, cancellationToken);
        }

        var items = history.Batches
            .Select(batch =>
            {
                // Todas las filas de un lote comparten actor, motivo, nota y momento: se escriben juntas.
                var head = batch[0];
                return new OperatorHistoryBatchDto(
                    head.BatchId, TenantChangeVocabulary.ToText(head.Kind), head.OccurredAt, head.ActorUserId,
                    emails[head.ActorUserId], TenantChangeVocabulary.ToText(head.Reason), head.Note,
                    batch.Select(change => new OperatorHistoryChangeDto(change.ModuleKey?.Value, change.FromStatus, change.ToStatus))
                        .ToArray());
            })
            .ToArray();
        return new OperatorHistoryPageDto(items, history.Total, query.Page, query.PageSize);
    }
}
