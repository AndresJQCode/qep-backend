using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

// Spec 2026-11-22 (D-9): los precios fijos de una lista. Se paginan y se escriben por lote, aparte del encabezado: una lista puede
// tener miles de filas. Todo lote sube la versión de la lista, que es el If-Match.

public sealed record ListPriceListOverridesQuery(Guid TenantId, Guid PriceListId, string? Search, int Page, int PageSize)
    : IQuery<PriceListOverridesPageDto>;

public sealed class ListPriceListOverridesValidator : AbstractValidator<ListPriceListOverridesQuery>
{
    public const int MaxPageSize = 100;

    public ListPriceListOverridesValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, MaxPageSize);
        RuleFor(query => query.Search).MaximumLength(200);
    }
}

public sealed class ListPriceListOverridesHandler(
    IExecutionContext executionContext,
    IPriceListRepository priceLists,
    IValidator<ListPriceListOverridesQuery> validator)
    : IQueryHandler<ListPriceListOverridesQuery, PriceListOverridesPageDto>
{
    public async Task<PriceListOverridesPageDto> HandleAsync(
        ListPriceListOverridesQuery query, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, query.TenantId, CatalogPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var id = new PriceListId(query.PriceListId);
        var list = await priceLists.GetAsync(query.TenantId, id, cancellationToken)
            ?? throw PriceListTerms.NotFound(query.PriceListId);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var (items, total) = await priceLists.PageOverridesAsync(
            query.TenantId, id, search, query.Page, query.PageSize, cancellationToken);
        return new PriceListOverridesPageDto(
            items.Select(PriceListOverrideMapping.ToDto).ToList(), query.Page, query.PageSize, total, list.Version);
    }
}

public sealed record PriceListOverrideSetInput(Guid VariantId, string Currency, decimal Amount);

public sealed record PriceListOverrideRemoveInput(Guid VariantId, string Currency);

public sealed record UpdatePriceListOverridesCommand(
    Guid TenantId,
    Guid PriceListId,
    IReadOnlyList<PriceListOverrideSetInput>? Set,
    IReadOnlyList<PriceListOverrideRemoveInput>? Remove,
    long ExpectedVersion)
    : ICommand<PriceListOverridesWriteDto>;

public sealed class UpdatePriceListOverridesValidator : AbstractValidator<UpdatePriceListOverridesCommand>
{
    public const int MaxRows = 500;

    public UpdatePriceListOverridesValidator()
    {
        RuleFor(command => command)
            .Must(command => (command.Set?.Count ?? 0) + (command.Remove?.Count ?? 0) <= MaxRows)
            .WithName("Set")
            .WithMessage($"A batch writes at most {MaxRows} rows.");

        RuleFor(command => command).Custom((command, context) =>
        {
            // La segunda aparición de una variante y moneda es la que sobra, en el set y entre set y remove.
            var seen = new HashSet<(Guid, string)>();
            var set = command.Set ?? [];
            for (var index = 0; index < set.Count; index++)
            {
                var item = set[index];
                if (item is null)
                {
                    context.AddFailure(new ValidationFailure($"Set[{index}]", "The row is empty."));
                    continue;
                }

                if (item.VariantId == Guid.Empty)
                {
                    context.AddFailure(new ValidationFailure($"Set[{index}].VariantId", "The variant is required."));
                }

                if (!Tenancy.Application.Currencies.IsSupported(item.Currency))
                {
                    context.AddFailure(new ValidationFailure($"Set[{index}].Currency", "The currency is not supported."));
                }
                else if (!seen.Add((item.VariantId, Tenancy.Application.Currencies.Normalize(item.Currency))))
                {
                    context.AddFailure(new ValidationFailure($"Set[{index}]", "The variant and currency are already in the batch."));
                }

                if (item.Amount <= 0m
                    || item.Amount > PriceListOverride.MaxAmount
                    || decimal.Round(item.Amount, PriceListOverride.AmountDecimals) != item.Amount)
                {
                    context.AddFailure(new ValidationFailure(
                        $"Set[{index}].Amount", "The fixed price must be greater than zero and have at most two decimals."));
                }
            }

            var remove = command.Remove ?? [];
            for (var index = 0; index < remove.Count; index++)
            {
                var item = remove[index];
                if (item is null || item.VariantId == Guid.Empty || !Tenancy.Application.Currencies.IsSupported(item.Currency))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Remove[{index}]", "The variant is required and the currency must be supported."));
                    continue;
                }

                if (!seen.Add((item.VariantId, Tenancy.Application.Currencies.Normalize(item.Currency))))
                {
                    context.AddFailure(new ValidationFailure($"Remove[{index}]", "The variant and currency are already in the batch."));
                }
            }
        });
    }
}

/// <summary>
/// Escribe un lote: <c>set</c> crea o cambia, <c>remove</c> borra (borrar uno que no existe no es error: el resultado es el
/// pedido). Cada variante de <c>set</c> tiene que ser del tenant, en cualquier estado; una ajena es 422
/// <c>catalog.price_list.variant_not_found</c> con <c>errors.Set[i].VariantId</c>. Devuelve las filas escritas en el orden recibido.
/// </summary>
public sealed class UpdatePriceListOverridesHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IValidator<UpdatePriceListOverridesCommand> validator)
    : ICommandHandler<UpdatePriceListOverridesCommand, PriceListOverridesWriteDto>
{
    private static readonly string[] VariantNotFoundMessage = ["The variant does not exist in the catalog."];

    public async Task<PriceListOverridesWriteDto> HandleAsync(
        UpdatePriceListOverridesCommand command, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, command.TenantId, CatalogPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var id = new PriceListId(command.PriceListId);
        var list = await priceLists.GetAsync(command.TenantId, id, cancellationToken)
            ?? throw PriceListTerms.NotFound(command.PriceListId);
        PriceListTerms.EnsureVersion(list, command.ExpectedVersion);

        var set = (command.Set ?? [])
            .Select(item => item with { Currency = Tenancy.Application.Currencies.Normalize(item.Currency) })
            .ToList();
        var remove = (command.Remove ?? [])
            .Select(item => item with { Currency = Tenancy.Application.Currencies.Normalize(item.Currency) })
            .ToList();

        var variants = await priceLists.FindVariantsAsync(
            command.TenantId, set.Select(item => item.VariantId).Distinct().ToList(), cancellationToken);
        var missing = set
            .Select((item, index) => (item, index))
            .Where(pair => !variants.ContainsKey(pair.item.VariantId))
            .ToDictionary(pair => $"Set[{pair.index}].VariantId", _ => VariantNotFoundMessage);
        if (missing.Count > 0)
        {
            throw new CatalogDomainException(
                "catalog.price_list.variant_not_found", "One of the variants does not exist in the catalog.", missing);
        }

        var touched = set.Select(item => new VariantId(item.VariantId))
            .Concat(remove.Select(item => new VariantId(item.VariantId)))
            .Distinct()
            .ToList();
        var stored = (await priceLists.FindOverridesForUpdateAsync(command.TenantId, id, touched, cancellationToken))
            .ToDictionary(item => (item.VariantId.Value, item.Currency));

        foreach (var item in set)
        {
            if (stored.TryGetValue((item.VariantId, item.Currency), out var existing))
            {
                existing.ChangeAmount(item.Amount);
            }
            else
            {
                priceLists.AddOverride(PriceListOverride.Create(
                    command.TenantId, id, new VariantId(item.VariantId), item.Currency, item.Amount));
            }
        }

        foreach (var item in remove)
        {
            if (stored.TryGetValue((item.VariantId, item.Currency), out var existing))
            {
                priceLists.RemoveOverride(existing);
            }
        }

        var now = clock.UtcNow;
        list.TouchOverrides(now);
        auditPublisher.PublishResource(
            command.TenantId, executionContext.SubjectId, PriceListAuditActions.OverridesChanged,
            PriceListAuditActions.PriceListResource, list.Id.ToString(), ["overrides"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new PriceListOverridesWriteDto(
            list.Version,
            set.Select(item =>
            {
                var variant = variants[item.VariantId];
                return new PriceListOverrideDto(
                    variant.VariantId,
                    variant.ProductId,
                    variant.Code,
                    variant.Name,
                    item.Currency,
                    variant.BasePrices.TryGetValue(item.Currency, out var basePrice) ? basePrice : null,
                    item.Amount);
            }).ToList());
    }
}

internal static class PriceListOverrideMapping
{
    public static PriceListOverrideDto ToDto(PriceListOverrideRow row) =>
        new(row.VariantId, row.ProductId, row.Code, row.Name, row.Currency, row.BasePrice, row.Amount);
}

public sealed record GetChannelPricesQuery(Guid TenantId) : IQuery<ChannelPricesDto>;

public sealed class GetChannelPricesHandler(IExecutionContext executionContext, IPriceListRepository priceLists)
    : IQueryHandler<GetChannelPricesQuery, ChannelPricesDto>
{
    public async Task<ChannelPricesDto> HandleAsync(GetChannelPricesQuery query, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, query.TenantId, CatalogPermissions.PriceListManage);
        return await ChannelPricesResponse.BuildAsync(query.TenantId, priceLists, cancellationToken);
    }
}

public sealed record ChannelPriceInput(string Channel, Guid? PriceListId);

/// <summary>Los canales que no vienen no cambian; <c>priceListId</c> <c>null</c> vuelve el canal al precio base.</summary>
public sealed record UpdateChannelPricesCommand(Guid TenantId, IReadOnlyList<ChannelPriceInput>? Channels)
    : ICommand<ChannelPricesDto>;

public sealed class UpdateChannelPricesValidator : AbstractValidator<UpdateChannelPricesCommand>
{
    public UpdateChannelPricesValidator()
    {
        RuleFor(command => command.Channels).NotNull();
        RuleFor(command => command.Channels).Custom((channels, context) =>
        {
            if (channels is null)
            {
                return;
            }

            var seen = new HashSet<PriceChannel>();
            for (var index = 0; index < channels.Count; index++)
            {
                if (channels[index] is not { } item
                    || !PriceListTerms.TryParseChannel(item.Channel, out var channel)
                    || !seen.Add(channel))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Channels[{index}].Channel", "The channel must be Pos, Store or Quotations, once."));
                }
                else if (item.PriceListId == Guid.Empty)
                {
                    context.AddFailure(new ValidationFailure($"Channels[{index}].PriceListId", "The price list id is empty."));
                }
            }
        });
    }
}

/// <summary>Sin <c>If-Match</c>: gana la última escritura, como el acceso rápido del POS. Una lista que no existe es 404.</summary>
public sealed class UpdateChannelPricesHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IValidator<UpdateChannelPricesCommand> validator)
    : ICommandHandler<UpdateChannelPricesCommand, ChannelPricesDto>
{
    public async Task<ChannelPricesDto> HandleAsync(UpdateChannelPricesCommand command, CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(executionContext, command.TenantId, CatalogPermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var lists = (await priceLists.ListAsync(command.TenantId, cancellationToken)).ToDictionary(list => list.Id.Value);
        var current = (await priceLists.ListChannelsAsync(command.TenantId, cancellationToken))
            .ToDictionary(assignment => assignment.Channel);
        var changed = new List<string>();
        foreach (var item in command.Channels!)
        {
            if (!PriceListTerms.TryParseChannel(item.Channel, out var channel))
            {
                throw new InvalidOperationException($"Channel '{item.Channel}' passed validation but is not known.");
            }

            if (item.PriceListId is { } listId && !lists.ContainsKey(listId))
            {
                throw PriceListTerms.NotFound(listId);
            }

            var existing = current.GetValueOrDefault(channel);
            if (existing?.PriceListId.Value == item.PriceListId)
            {
                continue;
            }

            if (item.PriceListId is not { } assigned)
            {
                priceLists.RemoveChannel(existing!);
            }
            else if (existing is not null)
            {
                existing.Reassign(new PriceListId(assigned));
            }
            else
            {
                priceLists.AddChannel(ChannelPriceList.Assign(command.TenantId, channel, new PriceListId(assigned)));
            }

            changed.Add(channel.ToString());
        }

        if (changed.Count > 0)
        {
            auditPublisher.PublishResource(
                command.TenantId, executionContext.SubjectId, PriceListAuditActions.ChannelPricesUpdated,
                PriceListAuditActions.ChannelPricesResource, command.TenantId.ToString(), changed, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ChannelPricesResponse.BuildAsync(command.TenantId, priceLists, cancellationToken);
    }
}

internal static class ChannelPricesResponse
{
    public static async Task<ChannelPricesDto> BuildAsync(
        Guid tenantId, IPriceListRepository priceLists, CancellationToken cancellationToken)
    {
        var lists = (await priceLists.ListAsync(tenantId, cancellationToken)).ToDictionary(list => list.Id);
        var assignments = (await priceLists.ListChannelsAsync(tenantId, cancellationToken))
            .ToDictionary(assignment => assignment.Channel);
        return new ChannelPricesDto(Enum.GetValues<PriceChannel>()
            .Select(channel => new ChannelPriceDto(
                channel.ToString(),
                assignments.TryGetValue(channel, out var assignment) && lists.TryGetValue(assignment.PriceListId, out var list)
                    ? new ChannelPriceListRefDto(list.Id.Value, list.Name)
                    : null))
            .ToList());
    }
}
