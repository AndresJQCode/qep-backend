using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public static class CustomerMergeLimits
{
    /// <summary>D-F9: cuántos clientes entran en una sola fusión.</summary>
    public const int MaxMerged = 10;
}

/// <summary>Las ocho clases que la fusión mueve al que queda, en el orden en que la confirmación las lista.</summary>
public enum CustomerMergeMoveKind
{
    Quotations,
    Orders,
    Conversations,
    PosSales,
    Deals,
    Notes,
    Addresses,
    Tags,
}

public sealed record GetCustomerMergePreviewQuery(Guid TenantId, Guid SurvivorId, IReadOnlyList<Guid>? MergedCustomerIds)
    : IQuery<CustomerMergePreviewResponse>;

/// <summary>
/// Las reglas de los ids a fusionar, compartidas con el comando de la fusión: todas caen sobre <c>MergedCustomerIds</c>, la
/// clave que el formulario sabe marcar. El primer fallo gana. Un <c>Guid.Empty</c> también se rechaza aquí (desvío F3).
/// </summary>
internal static class CustomerMergeIdRules
{
    public static IRuleBuilderOptionsConditions<T, IReadOnlyList<Guid>?> MustBeMergeable<T>(
        this IRuleBuilder<T, IReadOnlyList<Guid>?> rule, Func<T, Guid> survivorId) =>
        rule.Custom((ids, context) =>
        {
            if (ids is null || ids.Count < 1 || ids.Count > CustomerMergeLimits.MaxMerged || ids.Any(id => id == Guid.Empty))
            {
                context.AddFailure($"Elige entre 1 y {CustomerMergeLimits.MaxMerged} clientes para fusionar.");
            }
            else if (ids.Distinct().Count() != ids.Count)
            {
                context.AddFailure("No repitas un cliente.");
            }
            else if (ids.Contains(survivorId(context.InstanceToValidate)))
            {
                context.AddFailure("El cliente que queda no puede estar entre los que se fusionan.");
            }
        });
}

public sealed class GetCustomerMergePreviewValidator : AbstractValidator<GetCustomerMergePreviewQuery>
{
    public GetCustomerMergePreviewValidator()
    {
        RuleFor(query => query.MergedCustomerIds).MustBeMergeable(query => query.SurvivorId);
    }
}

public sealed class GetCustomerMergePreviewHandler(
    ICustomerRepository repository,
    ICustomerMergeStore mergeStore,
    ICustomerMergeReferences references,
    ICustomerTagRepository tags,
    IExecutionContext executionContext,
    IValidator<GetCustomerMergePreviewQuery> validator)
    : IQueryHandler<GetCustomerMergePreviewQuery, CustomerMergePreviewResponse>
{
    public async Task<CustomerMergePreviewResponse> HandleAsync(
        GetCustomerMergePreviewQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerMerge);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var mergedIds = query.MergedCustomerIds!;

        // Un id de otro tenant no aparece: es «no existe», igual que uno inexistente, y nunca confirma nada.
        var found = await repository.FindManyAsync(
            query.TenantId, [new CustomerId(query.SurvivorId), .. mergedIds.Select(id => new CustomerId(id))], cancellationToken);
        var survivor = found.GetValueOrDefault(new CustomerId(query.SurvivorId)) ?? throw CustomerNotFound.For(query.SurvivorId);
        var merged = new List<Customer>(mergedIds.Count);
        foreach (var id in mergedIds)
        {
            merged.Add(found.GetValueOrDefault(new CustomerId(id)) ?? throw new CustomersDomainException(
                "customers.merge.customer_not_found", $"Customer '{id}' was not found."));
        }

        var plan = CustomerMergePlan.Create(survivor, merged);

        // D-F2: lo de un fusionado anterior (C → B) también pasa al que queda cuando B se fusiona.
        var chain = await mergeStore.FindIdsMergedIntoAsync(query.TenantId, mergedIds.ToArray(), cancellationToken);
        var moved = mergedIds.Concat(chain).Distinct().ToArray();
        var referenceCounts = await references.CountAsync(query.TenantId, moved, cancellationToken);
        var owned = await mergeStore.CountOwnedAsync(query.TenantId, moved, cancellationToken);
        var tagsGained = await CountTagsGainedAsync(query.TenantId, plan, cancellationToken);

        return new CustomerMergePreviewResponse(
            ToParty(survivor),
            merged.Select(ToParty).ToArray(),
            DocumentsDiffer(survivor, merged),
            [
                Move(CustomerMergeMoveKind.Quotations, referenceCounts.Quotations),
                Move(CustomerMergeMoveKind.Orders, referenceCounts.Orders),
                Move(CustomerMergeMoveKind.Conversations, referenceCounts.Conversations),
                Move(CustomerMergeMoveKind.PosSales, referenceCounts.PosSales),
                Move(CustomerMergeMoveKind.Deals, referenceCounts.Deals),
                Move(CustomerMergeMoveKind.Notes, owned.Notes),
                Move(CustomerMergeMoveKind.Addresses, owned.Addresses),
                Move(CustomerMergeMoveKind.Tags, tagsGained),
            ],
            plan.FilledFields.Select(field => field.ToString()).ToArray());
    }

    private async Task<int> CountTagsGainedAsync(Guid tenantId, CustomerMergePlan plan, CancellationToken cancellationToken) =>
        (await CustomerMergeTags.PlanAsync(tags, tenantId, plan, cancellationToken)).Merge.Added.Count;

    private static CustomerMergeMoveDto Move(CustomerMergeMoveKind kind, int count) => new(kind.ToString(), count);

    private static CustomerMergePartyDto ToParty(Customer customer) => new(
        customer.Id.Value,
        customer.Name,
        customer.IdentificationType?.ToWireValue(),
        customer.IdentificationNumber,
        customer.Cuc,
        customer.IsComplete,
        customer.IsActive,
        customer.Version,
        customer.PersonType?.ToWireValue());

    // §3.4: se compara el número normalizado; si a uno de los dos le falta el número, no cuenta como distinto.
    private static bool DocumentsDiffer(Customer survivor, IReadOnlyList<Customer> merged)
    {
        var kept = CustomerIdentificationNormalizer.Normalize(survivor.IdentificationNumber);
        if (kept.Length == 0)
        {
            return false;
        }

        return merged.Any(customer =>
        {
            var other = CustomerIdentificationNormalizer.Normalize(customer.IdentificationNumber);
            return other.Length > 0 && other != kept;
        });
    }
}

/// <summary>
/// Las etiquetas de una fusión, compartidas por la vista previa y la fusión para que cuenten lo mismo: las del que queda
/// primero; después las de cada fusionado en el orden pedido y, dentro de cada uno, por fecha de asignación y por id.
/// </summary>
internal static class CustomerMergeTags
{
    public static async Task<(CustomerTagMerge Merge, IReadOnlyList<CustomerTagAssignment> MergedAssignments)> PlanAsync(
        ICustomerTagRepository tags, Guid tenantId, CustomerMergePlan plan, CancellationToken cancellationToken)
    {
        var survivorTags = (await tags.ListAssignmentsAsync(tenantId, plan.Survivor.Id, cancellationToken))
            .Select(assignment => assignment.TagId)
            .ToArray();
        var mergedAssignments = new List<CustomerTagAssignment>();
        foreach (var customer in plan.Merged)
        {
            var assignments = await tags.ListAssignmentsAsync(tenantId, customer.Id, cancellationToken);
            mergedAssignments.AddRange(
                assignments.OrderBy(assignment => assignment.AssignedAt).ThenBy(assignment => assignment.TagId.Value));
        }

        var merge = CustomerTagAssignment.PlanMerge(survivorTags, mergedAssignments.Select(assignment => assignment.TagId));
        return (merge, mergedAssignments);
    }
}

public sealed record MergeCustomersCommand(
    Guid TenantId, Guid SurvivorId, IReadOnlyList<Guid>? MergedCustomerIds, long ExpectedVersion)
    : ICommand<CustomerMergeResult>;

/// <summary>El que queda ya con lo movido, y las etiquetas que no cupieron (D-F5), en el orden en que se intentaron.</summary>
public sealed record CustomerMergeResult(CustomerDto Survivor, IReadOnlyList<CustomerMergeDroppedTagDto> DroppedTags);

public sealed class MergeCustomersValidator : AbstractValidator<MergeCustomersCommand>
{
    public MergeCustomersValidator()
    {
        RuleFor(command => command.MergedCustomerIds).MustBeMergeable(command => command.SurvivorId);
    }
}

/// <summary>
/// Spec 2026-10-24 §3.2: fusiona clientes en el de la ruta. Valida en el mismo orden que la vista previa (el que queda → 404,
/// cada fusionado → <c>customers.merge.customer_not_found</c>, después <see cref="CustomerMergePlan.Create"/>) y, entre el
/// que queda y los fusionados, la versión → 412.
///
/// **Todo va en una transacción explícita, con dos <c>SaveChanges</c> en orden fijo.** EF ordena los <c>UPDATE</c> de un
/// <c>SaveChanges</c> por clave primaria, no por el orden en que se tocaron las entidades (CLAUDE.md, el precedente de
/// <c>BusinessHoursRepository.SwapDefaultAsync</c>): si el que queda tiene el id menor, su <c>UPDATE</c> con el BSUID, el
/// IGSID o el documento del fusionado correría antes de que el fusionado los suelte y chocaría con el índice único. Por eso
/// el primero marca a los fusionados (<see cref="CustomerMergePlan.MarkMerged"/>, que suelta lo que van a dar) y el segundo
/// llena al que queda (<see cref="CustomerMergePlan.FillSurvivor"/>) y cambia las etiquetas. Después, en la misma
/// transacción, el SQL de notas y direcciones y el de los otros módulos. Sin el <c>Commit</c>, el <c>Dispose</c> deshace
/// todo: la fusión queda completa o no pasa.
/// </summary>
public sealed class MergeCustomersHandler(
    ICustomerRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomerMergeStore mergeStore,
    ICustomerMergeReferences references,
    ICustomerTagRepository tags,
    ICustomerGeographyLookup geographyLookup,
    IClientClassificationRepository classificationRepository,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<MergeCustomersCommand> validator)
    : ICommandHandler<MergeCustomersCommand, CustomerMergeResult>
{
    public async Task<CustomerMergeResult> HandleAsync(MergeCustomersCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerMerge);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var mergedIds = command.MergedCustomerIds!;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var survivor = await repository.FindAsync(command.TenantId, new CustomerId(command.SurvivorId), cancellationToken)
            ?? throw CustomerNotFound.For(command.SurvivorId);
        if (survivor.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The customer changed since the merge preview was loaded.");
        }

        // Un id de otro tenant no aparece: es «no existe», igual que uno inexistente, y nunca confirma nada.
        var merged = new List<Customer>(mergedIds.Count);
        foreach (var id in mergedIds)
        {
            merged.Add(await repository.FindAsync(command.TenantId, new CustomerId(id), cancellationToken)
                ?? throw new CustomersDomainException("customers.merge.customer_not_found", $"Customer '{id}' was not found."));
        }

        var plan = CustomerMergePlan.Create(survivor, merged);

        // D-F2: los que ya apuntaban a un fusionado (C → B) pasan a apuntar al que queda, y lo suyo también se mueve.
        var chain = await mergeStore.FindIdsMergedIntoAsync(command.TenantId, mergedIds.ToArray(), cancellationToken);
        var moved = mergedIds.Concat(chain).Distinct().ToArray();
        var (tagMerge, mergedAssignments) = await CustomerMergeTags.PlanAsync(tags, command.TenantId, plan, cancellationToken);

        var now = clock.UtcNow;
        var actor = executionContext.SubjectId;

        // Primer SaveChanges: los fusionados, que sueltan BSUID, IGSID y documento.
        plan.MarkMerged(now);
        foreach (var customer in plan.Merged)
        {
            auditPublisher.Publish(command.TenantId, actor, CustomerAuditActions.Merged, customer.Id.ToString(), "success", now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await mergeStore.RepointMergedIntoAsync(command.TenantId, chain, survivor.Id.Value, cancellationToken);

        // Segundo SaveChanges: el que queda los toma, y las etiquetas cambian de dueño. Las de los fusionados se borran todas:
        // un fusionado no conserva etiquetas, y las que no cupieron vuelven en DroppedTags.
        plan.FillSurvivor(now);
        foreach (var assignment in mergedAssignments)
        {
            tags.RemoveAssignment(assignment);
        }

        foreach (var tagId in tagMerge.Added)
        {
            tags.AddAssignment(CustomerTagAssignment.Create(survivor.Id, tagId, command.TenantId, actor, now));
        }

        auditPublisher.Publish(command.TenantId, actor, CustomerAuditActions.Merged, survivor.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await mergeStore.MoveOwnedAsync(command.TenantId, survivor.Id.Value, moved, cancellationToken);
        await references.MoveAsync(command.TenantId, survivor.Id.Value, moved, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // Se relee sin tracking (FC-04 duda 1): MoveOwnedAsync es un ExecuteUpdate, así que la instancia cargada no ve las
        // direcciones que llegaron, y la respuesta tiene que traer la libreta entera.
        var fresh = (await repository.FindManyAsync(command.TenantId, [survivor.Id], cancellationToken))[survivor.Id];
        var dto = await fresh.ToDtoAsync(geographyLookup, classificationRepository, tags, cancellationToken);
        return new CustomerMergeResult(dto, await DroppedAsync(command.TenantId, tagMerge.Dropped, cancellationToken));
    }

    private async Task<IReadOnlyList<CustomerMergeDroppedTagDto>> DroppedAsync(
        Guid tenantId, IReadOnlyList<TagId> dropped, CancellationToken cancellationToken)
    {
        if (dropped.Count == 0)
        {
            return [];
        }

        var names = (await tags.ListByIdsAsync(tenantId, dropped, cancellationToken)).ToDictionary(tag => tag.Id, tag => tag.Name);
        return dropped.Where(names.ContainsKey).Select(id => new CustomerMergeDroppedTagDto(id.Value, names[id])).ToArray();
    }
}
