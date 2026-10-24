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

    private async Task<int> CountTagsGainedAsync(Guid tenantId, CustomerMergePlan plan, CancellationToken cancellationToken)
    {
        var survivorTags = (await tags.ListAssignmentsAsync(tenantId, plan.Survivor.Id, cancellationToken))
            .Select(assignment => assignment.TagId)
            .ToArray();
        var mergedTags = new List<TagId>();
        foreach (var customer in plan.Merged)
        {
            var assignments = await tags.ListAssignmentsAsync(tenantId, customer.Id, cancellationToken);
            mergedTags.AddRange(assignments.OrderBy(assignment => assignment.AssignedAt).ThenBy(assignment => assignment.TagId.Value)
                .Select(assignment => assignment.TagId));
        }

        return CustomerTagAssignment.PlanMerge(survivorTags, mergedTags).Added.Count;
    }

    private static CustomerMergeMoveDto Move(CustomerMergeMoveKind kind, int count) => new(kind.ToString(), count);

    private static CustomerMergePartyDto ToParty(Customer customer) => new(
        customer.Id.Value,
        customer.Name,
        customer.IdentificationType?.ToWireValue(),
        customer.IdentificationNumber,
        customer.Cuc,
        customer.IsComplete,
        customer.IsActive,
        customer.Version);

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
