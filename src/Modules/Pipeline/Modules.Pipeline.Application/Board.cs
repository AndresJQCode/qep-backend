using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>D33: los topes del tablero.</summary>
public static class BoardLimits
{
    public const int CardsPerColumn = 200;

    public static readonly TimeSpan ClosedWindow = TimeSpan.FromDays(30);
}

/// <summary>Por etapa y moneda: cuántos negocios (con monto o sin él) y la suma de los que tienen monto; nula si ninguno tiene
/// (D34), y entonces esa moneda no aparece en los totales.</summary>
public sealed record BoardAggregateRow(Guid StageId, string Currency, int Count, decimal? Amount);

public sealed record BoardCardRow(
    Guid Id, Guid StageId, string Title, Guid CustomerId, Guid OwnerMemberId, decimal? Amount, string Currency,
    DateTimeOffset StageEnteredAt, DateTimeOffset LastChangedAt, string? PrimaryQuotationNumber, long Version);

/// <summary>D33 y P9 del plan: los agregados son de la columna entera; las tarjetas, las primeras <c>perStage</c> por etapa
/// (abiertas por <c>stage_entered_at</c> ascendente, cerradas por <c>closed_at</c> descendente). Cerrados sólo desde
/// <c>closedSince</c>. <c>ownerMemberId</c> nulo = todos.</summary>
public interface IPipelineBoardQueries
{
    Task<IReadOnlyList<BoardAggregateRow>> AggregateAsync(Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, CancellationToken cancellationToken);

    Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, int perStage, CancellationToken cancellationToken);
}

/// <summary>
/// BFF (§6): TODAS las columnas, incluso en cero. Una columna que desaparece obliga a la pantalla a conocer las etapas para
/// dibujar la que falta (precedente: <c>ReportStatusSliceDto</c>). Las sumas van por moneda (D24): una cotización puede ir en USD
/// y sumar pesos con dólares no significa nada.
/// </summary>
public sealed record PipelineBoardDto(IReadOnlyList<BoardColumnDto> Columns, string Owner);

/// <summary>D33: <see cref="Deals"/> trae como máximo 200; <see cref="Count"/> y <see cref="Totals"/> son de TODA la columna, para
/// que la pantalla diga «200 de 340» sin otra llamada. Won y Lost sólo cuentan lo cerrado en los últimos 30 días.</summary>
public sealed record BoardColumnDto(
    Guid StageId, string Name, string Kind, int Probability, bool IsQuotationStage,
    int Count, IReadOnlyList<CurrencyTotalDto> Totals, IReadOnlyList<DealCardDto> Deals);

/// <summary>Weighted = Σ amount × probability / 100, calculado en el servidor: la pantalla no repite la regla. Un negocio sin monto
/// (D34) no entra en ninguna suma.</summary>
public sealed record CurrencyTotalDto(string Currency, decimal Amount, decimal Weighted);

/// <summary>BFF: nombre del dueño, completitud del cliente, días en la etapa e IsStalled ya resueltos; la pantalla no tiene con qué
/// calcularlos (no ve membresías ni conversaciones).</summary>
public sealed record DealCardDto(
    Guid Id, string Title, Guid CustomerId, string CustomerName, bool CustomerIsComplete, decimal? Amount, string Currency,
    Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version);

public sealed record GetBoardQuery(Guid TenantId, string? Owner) : IQuery<PipelineBoardDto>;

public sealed class GetBoardValidator : AbstractValidator<GetBoardQuery>
{
    public GetBoardValidator() =>
        RuleFor(query => query.Owner).Must(owner => owner is null or "me" or "all").WithMessage("Owner must be 'me' or 'all'.");
}

public sealed class GetBoardHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IPipelineBoardQueries board,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    PipelineCaller caller,
    IClock clock,
    IValidator<GetBoardQuery> validator)
    : IQueryHandler<GetBoardQuery, PipelineBoardDto>
{
    public async Task<PipelineBoardDto> HandleAsync(GetBoardQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var owner = query.Owner ?? "me";
        Guid? ownerId = owner == "me" ? await caller.RequireAsync(query.TenantId, cancellationToken) : null;
        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var closedSince = now - BoardLimits.ClosedWindow;
        var aggregates = await board.AggregateAsync(query.TenantId, ownerId, closedSince, cancellationToken);
        var cards = await board.ListCardsAsync(query.TenantId, ownerId, closedSince, BoardLimits.CardsPerColumn, cancellationToken);

        var customerIds = cards.Select(card => card.CustomerId).Distinct().ToArray();
        var customerRefs = await customers.FindAsync(query.TenantId, customerIds, cancellationToken);
        var activity = await conversations.FindActivityAsync(query.TenantId, customerIds, cancellationToken);
        var names = await members.FindAsync(query.TenantId, cards.Select(card => card.OwnerMemberId).Distinct().ToArray(), cancellationToken);

        var columns = pipeline.OrderedStages.Select(stage =>
        {
            var stageAggregates = aggregates.Where(row => row.StageId == stage.Id).ToArray();
            var totals = stageAggregates
                .Where(row => row.Amount is not null)
                .OrderBy(row => Currencies.OrderOf(row.Currency))
                .ThenBy(row => row.Currency, StringComparer.Ordinal)
                .Select(row => new CurrencyTotalDto(row.Currency, row.Amount!.Value, Math.Round(row.Amount.Value * stage.Probability / 100m, 2)))
                .ToArray();
            var stageCards = cards
                .Where(card => card.StageId == stage.Id)
                .Select(card =>
                {
                    var customer = customerRefs.GetValueOrDefault(card.CustomerId);
                    var facts = DealCardMapper.Describe(pipeline, card.StageId, card.StageEnteredAt, card.LastChangedAt, card.OwnerMemberId,
                        names, activity.GetValueOrDefault(card.CustomerId)?.LastMessageAt, now);
                    return new DealCardDto(
                        card.Id, card.Title, card.CustomerId, DealCardMapper.CustomerName(customer), customer?.IsComplete ?? false,
                        card.Amount, card.Currency, card.OwnerMemberId, facts.OwnerName, facts.IsStalled, facts.DaysInStage,
                        card.PrimaryQuotationNumber, card.Version);
                })
                .ToArray();
            return new BoardColumnDto(
                stage.Id, stage.Name, stage.Kind.ToString(), stage.Probability, stage.IsQuotationStage,
                stageAggregates.Sum(row => row.Count), totals, stageCards);
        }).ToArray();

        return new PipelineBoardDto(columns, owner);
    }
}
