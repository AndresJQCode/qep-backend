using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record GetVariantProfilesQuery(Guid TenantId, IReadOnlyList<Guid> VariantIds) : IQuery<VariantProfilesDto>;

public sealed class GetVariantProfilesValidator : AbstractValidator<GetVariantProfilesQuery>
{
    public const int MaxIds = 200;

    public GetVariantProfilesValidator()
    {
        RuleFor(query => query.VariantIds)
            .Must(ids => ids.Count is >= 1 and <= MaxIds)
            .WithMessage($"Send between 1 and {MaxIds} variant ids.");
        // El endpoint vuelve Guid.Empty un token que no es un Guid: así un id mal escrito es 422 con el campo y no un 400 sin mapa.
        RuleFor(query => query.VariantIds)
            .Must(ids => ids.All(id => id != Guid.Empty))
            .WithMessage("Every variant id must be a valid GUID.");
    }
}

/// <summary>
/// Lote de perfiles (hasta 200 variantes). Las variantes salen de Catalog y los desconocidos —o de otro tenant— se omiten. Una variante
/// sin perfil guardado se devuelve calculada en memoria con <c>Version = 0</c> (P-8): un GET no escribe, salvo el provisioner.
/// </summary>
public sealed class GetVariantProfilesHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    IVariantStockProbe stockProbe,
    IClock clock,
    IValidator<GetVariantProfilesQuery> validator)
    : IQueryHandler<GetVariantProfilesQuery, VariantProfilesDto>
{
    public async Task<VariantProfilesDto> HandleAsync(GetVariantProfilesQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var ids = query.VariantIds.Distinct().ToArray();
        var snapshots = (await catalog.GetVariantsAsync(query.TenantId, ids, cancellationToken)).ToDictionary(variant => variant.VariantId);
        var saved = (await profiles.ListAsync(query.TenantId, ids, cancellationToken)).ToDictionary(profile => profile.VariantId);
        var now = clock.UtcNow;
        // Una consulta para todo el lote y no una por variante (hasta 200).
        var withStock = await stockProbe.HasStockForManyAsync(query.TenantId, snapshots.Keys.ToArray(), cancellationToken);

        var items = new List<VariantProfileDto>(snapshots.Count);
        // En el orden en que se pidieron: la pantalla alinea la respuesta con su lista.
        foreach (var id in ids)
        {
            if (!snapshots.TryGetValue(id, out var variant))
            {
                continue;
            }

            var hasStock = withStock.Contains(id);
            if (saved.TryGetValue(id, out var profile))
            {
                items.Add(VariantProfileDtoMapper.ToDto(profile, variant, hasStock, stored: true));
            }
            else
            {
                var fallback = VariantLogisticsProfile.CreateDefault(query.TenantId, id, variant.ProductId, variant.PackagingUnits, now);
                items.Add(VariantProfileDtoMapper.ToDto(fallback, variant, hasStock, stored: false));
            }
        }

        return new VariantProfilesDto(items);
    }
}
