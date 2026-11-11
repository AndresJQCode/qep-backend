using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.1: borra un lote de clientes elegidos en la lista. Los duplicados se ignoran (D-7).
/// </summary>
public sealed record BulkDeleteCustomersCommand(Guid TenantId, IReadOnlyList<Guid> CustomerIds) : ICommand<BulkDeleteCustomersResult>
{
    /// <summary>D-7: una página de la lista, lo máximo que se puede marcar a la vez.</summary>
    public const int MaxCustomers = 100;
}

/// <summary>
/// Las tres listas van siempre, en el orden en que llegaron los ids. <c>NotFound</c> no es un error: con dos pestañas
/// abiertas, el otro ya lo borró.
/// </summary>
public sealed record BulkDeleteCustomersResult(
    IReadOnlyList<Guid> Deleted,
    IReadOnlyList<BlockedCustomerDto> Blocked,
    IReadOnlyList<Guid> NotFound);

public sealed record BlockedCustomerDto(Guid CustomerId, string Name, IReadOnlyList<string> Reasons);

/// <summary>Spec 2026-11-10 §4.2: por qué un cliente no se borra. El frontend los toma de aquí.</summary>
public static class CustomerDeletionBlockReasons
{
    public const string Quotations = "quotations";
    public const string Orders = "orders";
    public const string Loans = "loans";
    public const string ServiceContracts = "serviceContracts";
    public const string BlockedContact = "blockedContact";

    /// <summary>El orden del contrato: así salen los motivos en la respuesta, venga como venga del adaptador.</summary>
    public static IReadOnlyList<string> Ordered { get; } = [Quotations, Orders, Loans, ServiceContracts, BlockedContact];
}

public sealed class BulkDeleteCustomersValidator : AbstractValidator<BulkDeleteCustomersCommand>
{
    public BulkDeleteCustomersValidator()
    {
        // Sin la regla de distintos de SetProductsStoreVisibilityValidator: aquí los duplicados se ignoran (§4.1).
        RuleFor(command => command.CustomerIds)
            // Cascade.Stop: sin esto las reglas de abajo corren también sobre una lista nula.
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Elige al menos un cliente.")
            .Must(ids => ids.Count <= BulkDeleteCustomersCommand.MaxCustomers)
                .WithMessage($"Puedes borrar a lo sumo {BulkDeleteCustomersCommand.MaxCustomers} clientes a la vez.")
            .Must(ids => ids.All(id => id != Guid.Empty))
                .WithMessage("Un cliente no puede venir vacío.");
    }
}

/// <summary>
/// Spec 2026-11-10 §4 y §5. Todo el lote va en una transacción sobre la conexión del <c>CustomersDbContext</c>: sin el
/// <c>Commit</c>, el <c>Dispose</c> la deshace, así que un fallo inesperado no deja nada a medias. Los candados van antes de
/// leer quién existe y qué lo bloquea: quien escribe una referencia que bloquea toma el mismo candado compartido y, con eso,
/// nada de lo leído cambia hasta el commit. R2 queda fuera: sale por el outbox después (A-9).
/// </summary>
public sealed class BulkDeleteCustomersHandler(
    ICustomersUnitOfWork unitOfWork,
    ICustomerDeletionReferences references,
    ICustomerDeletionStore store,
    ICustomersAuditPublisher auditPublisher,
    ICustomerMediaReleasePublisher mediaReleasePublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<BulkDeleteCustomersCommand> validator)
    : ICommandHandler<BulkDeleteCustomersCommand, BulkDeleteCustomersResult>
{
    public async Task<BulkDeleteCustomersResult> HandleAsync(BulkDeleteCustomersCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: sin permiso no se revela nada del cuerpo.
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerDelete);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var requested = command.CustomerIds.Distinct().ToArray();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await references.LockAsync(command.TenantId, requested, cancellationToken);

        // Uno de otro tenant o uno fusionado no aparece: es «no existe», igual que uno inexistente.
        var found = (await store.FindDeletableAsync(command.TenantId, requested, cancellationToken))
            .ToDictionary(customer => customer.Id.Value);
        var notFound = requested.Where(id => !found.ContainsKey(id)).ToArray();
        var existing = requested.Where(found.ContainsKey).ToArray();

        var blockers = await references.FindBlockersAsync(command.TenantId, existing, cancellationToken);
        var blocked = existing
            .Where(blockers.ContainsKey)
            .Select(id => new BlockedCustomerDto(id, found[id].Name, InContractOrder(blockers[id])))
            .ToArray();
        var deletable = existing.Where(id => !blockers.ContainsKey(id)).Select(id => found[id]).ToArray();
        var deletableIds = deletable.Select(customer => customer.Id.Value).ToArray();

        // La foto del consentimiento va antes de borrar: después ya no hay de dónde sacar nombre y teléfono (A-5).
        var consents = await store.SnapshotConsentsAsync(
            command.TenantId,
            deletable.Select(customer => (customer.Id.Value, customer.Name, customer.PhoneE164)).ToArray(),
            cancellationToken);
        var outcome = await references.RemoveAsync(command.TenantId, deletableIds, cancellationToken);
        var merged = await store.DeleteAsync(command.TenantId, deletableIds, cancellationToken);

        var now = clock.UtcNow;
        foreach (var customer in deletable)
        {
            var id = customer.Id.Value;
            auditPublisher.PublishWithChangedFields(
                command.TenantId,
                executionContext.SubjectId,
                CustomerAuditActions.Deleted,
                customer.Id.ToString(),
                "success",
                now,
                Snapshot(customer, outcome.Counts.GetValueOrDefault(id), consents.GetValueOrDefault(id), merged.GetValueOrDefault(id)));

            // Sin claves no hay nada que purgar en R2, y un evento vacío sólo le daría trabajo al consumidor.
            if (outcome.StorageKeys.GetValueOrDefault(id) is { Count: > 0 } keys)
            {
                mediaReleasePublisher.Publish(command.TenantId, id, keys, now);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BulkDeleteCustomersResult(deletableIds, blocked, notFound);
    }

    private static readonly Dictionary<string, int> ReasonRank = CustomerDeletionBlockReasons.Ordered
        .Select((reason, index) => (reason, index))
        .ToDictionary(pair => pair.reason, pair => pair.index, StringComparer.Ordinal);

    // Un motivo que no está en el contrato va al final, en vez de perderse: el cliente sigue bloqueado.
    private static string[] InContractOrder(IReadOnlyList<string> reasons) =>
        reasons
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => ReasonRank.GetValueOrDefault(reason, int.MaxValue))
            .ToArray();

    /// <summary>
    /// A-8: la auditoría es lo único que queda del cliente, así que lleva su foto y lo que se llevó, como cadenas
    /// <c>clave=valor</c> en <c>changedFields</c>. Una clave sin valor no se escribe.
    /// </summary>
    private static List<string> Snapshot(Customer customer, CustomerDeletionCounts? counts, int dataConsents, int merged)
    {
        var fields = new List<string> { $"name={customer.Name}" };
        if (customer.PhoneE164 is { } phone)
        {
            fields.Add($"phoneE164={phone}");
        }

        if (customer.IdentificationType is { } type && customer.IdentificationNumber is { } number)
        {
            fields.Add($"identification={type.ToWireValue()} {number}");
        }

        if (counts is not null)
        {
            fields.Add($"conversations={counts.Conversations}");
            fields.Add($"messages={counts.Messages}");
            fields.Add($"deals={counts.Deals}");
            fields.Add($"posSales={counts.PosSales}");
            fields.Add($"campaignRecipients={counts.CampaignRecipients}");
        }

        // Sin consentimientos no hay nada que contar: la clave no se escribe.
        if (dataConsents > 0)
        {
            fields.Add($"dataConsents={dataConsents}");
        }

        fields.Add($"merged={merged}");
        return fields;
    }
}
