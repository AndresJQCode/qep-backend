using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-13: una asociación de línea personalizada con lo que la bandeja pinta junto a ella. BFF: el nombre visible de la
/// variante y su código viajan para que la pantalla no consulte Catalog por fila. <see cref="ProductName"/> y
/// <see cref="VariantCode"/> son <c>null</c> e <see cref="IsActive"/> <c>false</c> cuando la variante ya no existe en Catalog (no
/// hay FK): la fila sigue a la vista para que se pueda quitar, y la pantalla la muestra como «Variante eliminada».
/// </summary>
public sealed record ShopifyCustomLineMappingDto(
    Guid Id, string Title, Guid VariantId, string? ProductName, string? VariantCode, bool IsActive);

/// <summary>El cuerpo de <c>POST …/custom-line-mappings</c>. Todo opcional en el binding para que un campo ausente sea un 422 con
/// <c>errors</c> del validador y no un 400 del deserializador.</summary>
public sealed record CreateShopifyCustomLineMappingRequest(string? Title, Guid? VariantId);

/// <summary><c>GET …/custom-line-mappings</c> (<c>quotations.order.read</c>), por título en orden ordinal.</summary>
public sealed record ListShopifyCustomLineMappingsQuery(Guid TenantId, Guid ConnectionId)
    : IQuery<IReadOnlyList<ShopifyCustomLineMappingDto>>;

/// <summary><c>POST …/custom-line-mappings</c> (<c>quotations.order.manage</c>).</summary>
public sealed record CreateShopifyCustomLineMappingCommand(Guid TenantId, Guid ConnectionId, string? Title, Guid VariantId)
    : ICommand<ShopifyCustomLineMappingDto>;

/// <summary><c>DELETE …/custom-line-mappings/{id}</c> (<c>quotations.order.manage</c>). El resultado dice si había algo que
/// borrar; la ruta responde 204 de las dos maneras.</summary>
public sealed record DeleteShopifyCustomLineMappingCommand(Guid TenantId, Guid ConnectionId, Guid MappingId) : ICommand<bool>;

/// <summary>El título se mide con la misma regla que <see cref="ShopifyCustomLineMapping.Create"/> (recortado y por <c>Length</c>),
/// para que el 422 del campo y el dominio no discrepen: un título que el validador deja pasar nunca lanza en el dominio.</summary>
public sealed class CreateShopifyCustomLineMappingValidator : AbstractValidator<CreateShopifyCustomLineMappingCommand>
{
    public CreateShopifyCustomLineMappingValidator()
    {
        RuleFor(command => command.Title)
            .Must(title => ShopifyCustomLineMapping.NormalizeTitle(title) is { Length: <= ShopifyCustomLineMapping.TitleMaxLength })
            .WithMessage($"The title is required and cannot exceed {ShopifyCustomLineMapping.TitleMaxLength} characters.")
            .OverridePropertyName("title");
        RuleFor(command => command.VariantId).NotEmpty().OverridePropertyName("variantId");
    }
}

/// <summary>Orden ordinal por título (D9): determinista e igual en cualquier collation de Postgres.</summary>
public sealed class ListShopifyCustomLineMappingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyCustomLineMappingRepository mappings,
    IShopifyVariantLabels labels) : IQueryHandler<ListShopifyCustomLineMappingsQuery, IReadOnlyList<ShopifyCustomLineMappingDto>>
{
    public async Task<IReadOnlyList<ShopifyCustomLineMappingDto>> HandleAsync(
        ListShopifyCustomLineMappingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);
        await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);

        var rows = await mappings.ListAsync(query.TenantId, query.ConnectionId, cancellationToken);
        var described = await labels.DescribeAsync(query.TenantId, rows.Select(row => row.VariantId).Distinct().ToArray(), cancellationToken);
        return rows
            .OrderBy(row => row.Title, StringComparer.Ordinal)
            .Select(row => ShopifyCustomLineMappingMapper.ToDto(row, described))
            .ToArray();
    }
}

/// <summary>
/// Orden de chequeos (D10): tenant, permiso y módulo (403), validador (422), conexión (403), membresía (403), variante (422) y
/// guardado (409). El 409 sale sólo del índice único, sin consulta previa (D11), y la auditoría va en la misma unidad de trabajo:
/// si el 409 gana no queda nada.
/// </summary>
public sealed class CreateShopifyCustomLineMappingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyCustomLineMappingRepository mappings,
    IShopifyProductLookup products,
    IShopifyVariantLabels labels,
    IMembershipDirectory memberships,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateShopifyCustomLineMappingCommand> validator) : ICommandHandler<CreateShopifyCustomLineMappingCommand, ShopifyCustomLineMappingDto>
{
    public async Task<ShopifyCustomLineMappingDto> HandleAsync(
        CreateShopifyCustomLineMappingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await ShopifyStoreSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var createdBy = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw ShopifyAuthorization.Denied();

        var found = await products.FindByVariantIdsAsync(command.TenantId, [command.VariantId], cancellationToken);
        if (!found.TryGetValue(command.VariantId, out var variant))
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.CustomLineVariantNotFound, "The variant does not exist in the tenant.");
        }

        if (!variant.IsActive)
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.CustomLineVariantInactive, "The variant is inactive.");
        }

        var now = clock.UtcNow;
        var mapping = ShopifyCustomLineMapping.Create(
            command.TenantId, command.ConnectionId, command.Title!, command.VariantId, createdBy, now);
        mappings.Add(mapping);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.CustomLineMappingCreated,
            ShopifyAuditActions.CustomLineMappingResourceType,
            mapping.Id.ToString(),
            ShopifyCustomLineMappingMapper.AuditDetail(mapping),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var described = await labels.DescribeAsync(command.TenantId, [mapping.VariantId], cancellationToken);
        return ShopifyCustomLineMappingMapper.ToDto(mapping, described);
    }
}

/// <summary>D7: borrar un id que no existe en <c>(tenant, conexión)</c> es idempotente: no hace nada y no audita. Un id de otro
/// tenant tampoco revela nada: el mismo resultado, nada borrado.</summary>
public sealed class DeleteShopifyCustomLineMappingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyCustomLineMappingRepository mappings,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<DeleteShopifyCustomLineMappingCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteShopifyCustomLineMappingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);
        await ShopifyStoreSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);

        var mapping = await mappings.FindAsync(command.TenantId, command.ConnectionId, command.MappingId, cancellationToken);
        if (mapping is null)
        {
            return false;
        }

        mappings.Remove(mapping);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.CustomLineMappingDeleted,
            ShopifyAuditActions.CustomLineMappingResourceType,
            mapping.Id.ToString(),
            ShopifyCustomLineMappingMapper.AuditDetail(mapping),
            clock.UtcNow);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception))
        {
            // D7: otro DELETE del mismo id ganó la carrera (el guardado no afectó ninguna fila): ya está borrada, y esa
            // petición es la que audita.
            return false;
        }

        return true;
    }
}

internal static class ShopifyCustomLineMappingMapper
{
    /// <summary>D5: una variante que Catalog ya no conoce sale con textos <c>null</c> y sin poder venderse.</summary>
    public static ShopifyCustomLineMappingDto ToDto(
        ShopifyCustomLineMapping mapping, IReadOnlyDictionary<Guid, ShopifyVariantLabel> described) =>
        described.TryGetValue(mapping.VariantId, out var label)
            ? new ShopifyCustomLineMappingDto(mapping.Id, mapping.Title, mapping.VariantId, label.DisplayName, label.Code, label.IsActive)
            : new ShopifyCustomLineMappingDto(mapping.Id, mapping.Title, mapping.VariantId, null, null, false);

    /// <summary>D8: ids y el título (no es dato sensible).</summary>
    public static string AuditDetail(ShopifyCustomLineMapping mapping) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["connectionId"] = mapping.ConnectionId.ToString(),
            ["title"] = mapping.Title,
            ["variantId"] = mapping.VariantId.ToString(),
        });
}
