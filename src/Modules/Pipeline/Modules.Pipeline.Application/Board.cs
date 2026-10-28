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

    /// <summary>Todos los negocios abiertos (sin tope), con sólo lo que el estancado necesita. El estancado depende del último
    /// mensaje del cliente, que vive en Messaging, así que no se puede contar en SQL: se trae la fila mínima y lo decide
    /// <see cref="DealCardMapper"/>, la misma regla que pinta la tarjeta.</summary>
    Task<IReadOnlyList<OpenDealActivityRow>> ListOpenActivityAsync(Guid tenantId, Guid? ownerMemberId, CancellationToken cancellationToken);
}

public sealed record OpenDealActivityRow(Guid StageId, Guid CustomerId, DateTimeOffset LastChangedAt);

/// <summary>
/// BFF (§6): TODAS las columnas, incluso en cero. Una columna que desaparece obliga a la pantalla a conocer las etapas para
/// dibujar la que falta (precedente: <c>ReportStatusSliceDto</c>). Las sumas van por moneda (D24): una cotización puede ir en USD
/// y sumar pesos con dólares no significa nada.
/// </summary>
public sealed record PipelineBoardDto(IReadOnlyList<BoardColumnDto> Columns, string Owner, BoardSummaryDto Summary);

/// <summary>
/// BFF: la franja de indicadores sobre las columnas. La pantalla no suma columnas: el servidor entrega los números ya cerrados.
/// Sólo etapas abiertas (Won y Lost no son embudo vivo) y con el mismo filtro de dueño que las columnas. <see cref="OpenCount"/> es la
/// suma de los <c>Count</c> de las columnas abiertas (la columna entera, no las 200 tarjetas). <see cref="OpenTotals"/> va por moneda
/// (D24) y su <c>Weighted</c> suma los ponderados ya redondeados de cada columna, para que la franja cuadre al centavo con lo que la
/// pantalla muestra. <see cref="StalledCount"/> son TODOS los negocios abiertos estancados, también los que el tope de tarjetas deja
/// fuera: con sólo las tarjetas, una columna de 340 diría menos de lo que hay.
/// </summary>
public sealed record BoardSummaryDto(int OpenCount, int StalledCount, IReadOnlyList<CurrencyTotalDto> OpenTotals);

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
        var openDeals = await board.ListOpenActivityAsync(query.TenantId, ownerId, cancellationToken);

        var customerIds = cards.Select(card => card.CustomerId).Distinct().ToArray();
        var customerRefs = await customers.FindAsync(query.TenantId, customerIds, cancellationToken);
        // Una sola lectura de actividad para las tarjetas y para el estancado de la franja, que incluye clientes sin tarjeta.
        var activity = await conversations.FindActivityAsync(
            query.TenantId, customerIds.Concat(openDeals.Select(deal => deal.CustomerId)).Distinct().ToArray(), cancellationToken);
        var names = await members.FindAsync(query.TenantId, cards.Select(card => card.OwnerMemberId).Distinct().ToArray(), cancellationToken);

        var columns = pipeline.OrderedStages.Select(stage =>
        {
            var stageAggregates = aggregates.Where(row => row.StageId == stage.Id).ToArray();
            var totals = stageAggregates
                .Where(row => row.Amount is not null)
                .OrderBy(row => Currencies.OrderOf(row.Currency))
                .ThenBy(row => row.Currency, StringComparer.Ordinal)
                .Select(row => new CurrencyTotalDto(row.Currency, row.Amount!.Value, Math.Round(row.Amount.Value * stage.Probability / 100m, 2, MidpointRounding.AwayFromZero)))
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

        var openColumns = columns.Where(column => column.Kind == nameof(StageKind.Open)).ToArray();
        var stalledCount = openDeals.Count(deal => DealCardMapper.IsStalled(
            pipeline, deal.StageId, deal.LastChangedAt, activity.GetValueOrDefault(deal.CustomerId)?.LastMessageAt, now));
        return new PipelineBoardDto(columns, owner, new BoardSummaryDto(openColumns.Sum(column => column.Count), stalledCount, OpenTotals(openColumns)));
    }

    /// <summary>Por moneda (D24) y en el orden de los totales de columna. Se suma lo que la pantalla ya ve en cada columna, ponderado
    /// redondeado incluido, en vez de recalcular desde los negocios: así la franja y las columnas nunca difieren en un centavo. Una
    /// moneda aparece sólo si alguna columna abierta la totaliza (D34).</summary>
    private static CurrencyTotalDto[] OpenTotals(IEnumerable<BoardColumnDto> openColumns) =>
        [.. openColumns
            .SelectMany(column => column.Totals)
            .GroupBy(total => total.Currency, StringComparer.Ordinal)
            .OrderBy(group => Currencies.OrderOf(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CurrencyTotalDto(group.Key, group.Sum(total => total.Amount), group.Sum(total => total.Weighted)))];
}
