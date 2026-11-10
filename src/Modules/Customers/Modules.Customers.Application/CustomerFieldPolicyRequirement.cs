using Modules.Customers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Opcion B de la politica de campos del cliente: el documento y la ciudad se exigen **al guardar** solo cuando el
/// tenant lo pide y el guardado los toca: al crear, al completar un incompleto, o cuando el PUT cambia o borra el campo.
/// Un PUT que deja intacto un campo ausente pasa, para no bloquear la edicion de clientes que ya existian asi.
/// Es una regla de lectura cruzada (la politica vive en Tenancy), por eso va aparte del validador y se llama despues
/// de el. Lanza <see cref="CustomersDomainException"/> con el mapa por campo, nunca <c>ValidationException</c>.
/// </summary>
internal static class CustomerFieldPolicyRequirement
{
    public static async Task EnsureSatisfiedAsync(
        ITenantDirectory directory,
        Guid tenantId,
        ICustomerWriteCommand command,
        Customer? current,
        CancellationToken cancellationToken)
    {
        // Forma del par, siempre y sin leer el tenant: un tipo sin numero (o al reves) es un documento a medias, y la
        // base lo rechaza (CK_customers_identification_pair), asi que mejor decirlo aca por campo.
        var hasType = !string.IsNullOrWhiteSpace(command.IdentificationType);
        var hasNumber = !string.IsNullOrWhiteSpace(command.IdentificationNumber);
        if (hasType != hasNumber)
        {
            var field = hasType ? "IdentificationNumber" : "IdentificationType";
            throw new CustomersDomainException(
                CustomersErrorCodes.IdentificationIncomplete,
                "The identification needs both its type and its number.",
                new Dictionary<string, string[]> { [field] = [CustomersErrorCodes.IdentificationIncomplete] });
        }

        var evaluateAll = current is null || !current.IsComplete;
        var evaluateDocument = evaluateAll || DocumentChanged(command, current!);
        var evaluateCity = evaluateAll || CityChanged(command, current!);
        if (!evaluateDocument && !evaluateCity)
        {
            return;
        }

        var settings = await directory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var policy = settings?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default;

        if (evaluateDocument && policy.Document.OnCustomerSave && !hasType)
        {
            throw new CustomersDomainException(
                CustomersErrorCodes.IdentificationRequired,
                "The tenant requires the customer's identification.",
                new Dictionary<string, string[]>
                {
                    ["IdentificationType"] = [CustomersErrorCodes.IdentificationRequired],
                    ["IdentificationNumber"] = [CustomersErrorCodes.IdentificationRequired],
                });
        }

        if (evaluateCity && policy.City.OnCustomerSave)
        {
            if (IsColombia(command.Country))
            {
                if (command.CityId is null)
                {
                    throw new CustomersDomainException(
                        CustomersErrorCodes.CityRequired,
                        "The tenant requires the customer's city.",
                        new Dictionary<string, string[]> { ["CityId"] = [CustomersErrorCodes.CityRequired] });
                }
            }
            else if (string.IsNullOrWhiteSpace(command.CityName))
            {
                throw new CustomersDomainException(
                    CustomersErrorCodes.CityNameRequired,
                    "The tenant requires the customer's city.",
                    new Dictionary<string, string[]> { ["CityName"] = [CustomersErrorCodes.CityNameRequired] });
            }
        }
    }

    private static bool DocumentChanged(ICustomerWriteCommand command, Customer current)
    {
        if (string.IsNullOrWhiteSpace(command.IdentificationType))
        {
            // Sin documento en el comando: es cambio solo si el cliente tenia uno (se esta borrando).
            return current.IdentificationType is not null || current.IdentificationNumber is not null;
        }

        return IdentificationTypeParser.Parse(command.IdentificationType) != current.IdentificationType
            || !string.Equals(command.IdentificationNumber!.Trim(), current.IdentificationNumber, StringComparison.Ordinal);
    }

    private static bool CityChanged(ICustomerWriteCommand command, Customer current) =>
        !string.Equals(command.Country?.Trim(), current.Country?.Trim(), StringComparison.OrdinalIgnoreCase)
        || command.CityId != current.CityId
        || !string.Equals(Blank(command.CityName), Blank(current.CityName), StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Un pais ausente cuenta como Colombia, igual que CustomerWriteRules.IsColombian.
    private static bool IsColombia(string? country) =>
        string.IsNullOrWhiteSpace(country)
        || string.Equals(country.Trim(), Customer.ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);
}
