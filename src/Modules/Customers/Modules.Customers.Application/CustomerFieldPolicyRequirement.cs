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
        // Forma del par, siempre y sin leer el tenant. Tipo sin numero conserva el codigo que ya daba el dominio
        // (identification_number_required); numero sin tipo es un documento a medias. La base tambien lo rechaza
        // (CK_customers_identification_pair), pero mejor decirlo aca por campo.
        var hasType = !string.IsNullOrWhiteSpace(command.IdentificationType);
        var hasNumber = !string.IsNullOrWhiteSpace(command.IdentificationNumber);
        if (hasType && !hasNumber)
        {
            const string numberRequired = "customers.customer.identification_number_required";
            throw new CustomersDomainException(
                numberRequired,
                "The identification number is required.",
                new Dictionary<string, string[]> { ["IdentificationNumber"] = [numberRequired] });
        }

        if (hasNumber && !hasType)
        {
            throw new CustomersDomainException(
                CustomersErrorCodes.IdentificationIncomplete,
                "The identification needs both its type and its number.",
                new Dictionary<string, string[]> { ["IdentificationType"] = [CustomersErrorCodes.IdentificationIncomplete] });
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

        // Un solo error con los campos de documento y ciudad juntos, para que el formulario marque todo de una vez;
        // el codigo es el del documento cuando falla, y el de la ciudad si solo falla ella.
        var errors = new Dictionary<string, string[]>();
        string? code = null;

        if (evaluateDocument && policy.Document.OnCustomerSave && !hasType)
        {
            code = CustomersErrorCodes.IdentificationRequired;
            errors["IdentificationType"] = [code];
            errors["IdentificationNumber"] = [code];
        }

        if (evaluateCity && policy.City.OnCustomerSave)
        {
            if (IsColombia(command.Country))
            {
                if (command.CityId is null)
                {
                    code ??= CustomersErrorCodes.CityRequired;
                    errors["CityId"] = [CustomersErrorCodes.CityRequired];
                }
            }
            else if (string.IsNullOrWhiteSpace(command.CityName))
            {
                code ??= CustomersErrorCodes.CityNameRequired;
                errors["CityName"] = [CustomersErrorCodes.CityNameRequired];
            }
        }

        if (code is not null)
        {
            throw new CustomersDomainException(code, "The tenant requires fields the customer does not have.", errors);
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
