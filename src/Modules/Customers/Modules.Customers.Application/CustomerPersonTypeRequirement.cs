using Modules.Customers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-26: con el ajuste <c>OnCustomerSave</c> del tenant, guardar un cliente colombiano exige su tipo de
/// persona. Es una regla de lectura cruzada (el ajuste vive en Tenancy), por eso va aparte del validador, que no
/// conoce al tenant, y se llama después de él. Sólo la usan el alta y el PUT: POS, provisión e importación no la
/// aplican (spec § Decisiones menores).
/// </summary>
internal static class CustomerPersonTypeRequirement
{
    public static async Task EnsureSatisfiedAsync(
        ITenantDirectory directory,
        Guid tenantId,
        string customerCountry,
        string? personType,
        CancellationToken cancellationToken)
    {
        // Lo barato primero: un cliente de afuera o con tipo ya dicho no necesita leer el tenant.
        if (!PersonTypeRequirementPolicy.IsColombian(customerCountry.Trim())
            || !string.IsNullOrWhiteSpace(personType))
        {
            return;
        }

        var settings = await directory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        if (settings?.EffectivePersonTypeRequirement != PersonTypeRequirement.OnCustomerSave)
        {
            return;
        }

        throw new CustomersDomainException(
            CustomersErrorCodes.PersonTypeRequired,
            "The tenant requires the customer's person type.",
            new Dictionary<string, string[]> { ["PersonType"] = [CustomersErrorCodes.PersonTypeRequired] });
    }
}
