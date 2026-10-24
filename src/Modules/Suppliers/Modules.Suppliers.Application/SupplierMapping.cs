using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Application;

internal static class SupplierMapping
{
    /// <summary>
    /// Del agregado al detalle, con la ciudad ya resuelta por el llamador (<c>null</c> fuera de
    /// Colombia). No la resuelve esta función: cada handler decide cómo (una consulta puntual en
    /// Get/Create/Update, un lote en List) y esto sólo ensambla.
    /// </summary>
    public static SupplierResponse ToResponse(this Supplier supplier, SupplierCityRef? city) => new(
        supplier.Id.Value,
        supplier.Name,
        supplier.BusinessName,
        supplier.Identification.Type.ToWire(),
        supplier.Identification.Number,
        supplier.Contact.Phone,
        supplier.Contact.Email,
        supplier.Contact.Address,
        supplier.Contact.Country,
        city is null ? null : new SupplierCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        city is null
            ? null
            : new SupplierDepartmentDto(city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        supplier.CityName,
        supplier.PaymentTermDays,
        supplier.Category,
        supplier.Notes,
        [.. supplier.BankAccounts.Select(account =>
            new SupplierBankAccountPayload(account.BankName, account.AccountNumber, account.Currency))],
        [.. supplier.Contacts.Select(contact =>
            new SupplierContactPayload(contact.Name, contact.Role, contact.Phone, contact.Email))],
        supplier.IsActive,
        supplier.CreatedAt,
        supplier.UpdatedAt);

    /// <summary>
    /// La versión de un solo proveedor: resuelve su ciudad (sólo si la tiene) y arma el detalle. La
    /// FK blanda a Geography no la garantiza la base, pero un proveedor guardado pasó por
    /// <see cref="ResolveCityAsync"/>: un miss aquí es corrupción de datos, no una entrada inválida,
    /// y por eso es <see cref="InvalidOperationException"/> (500) y no un 422.
    /// </summary>
    public static async Task<SupplierResponse> ToResponseAsync(
        this Supplier supplier,
        ISupplierGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        if (supplier.CityId is not { } cityId)
        {
            return supplier.ToResponse(null);
        }

        var city = await geographyLookup.FindCityAsync(cityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"City '{cityId}' referenced by supplier '{supplier.Id}' was not found.");

        return supplier.ToResponse(city);
    }

    public static SupplierListItemResponse ToListItem(this Supplier supplier, SupplierCityRef? city) => new(
        supplier.Id.Value,
        supplier.Name,
        supplier.BusinessName,
        supplier.Identification.Type.ToWire(),
        supplier.Identification.Number,
        supplier.Contact.Phone,
        city?.CityName ?? supplier.CityName,
        supplier.Category,
        supplier.PaymentTermDays,
        [.. supplier.BankAccounts.Select(account =>
            new SupplierListBankAccount(account.BankName, account.AccountNumber))],
        supplier.IsActive);

    /// <summary>
    /// Sólo un proveedor colombiano tiene ciudad DIVIPOLA que resolver. El validador ya se aseguró
    /// de que venga la que corresponde al país; aquí se comprueba que Geography la conozca.
    /// </summary>
    public static async Task<SupplierCityRef?> ResolveCityAsync(
        this ISupplierWriteCommand command,
        ISupplierGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        var isColombian = string.Equals(
            command.Country.Trim(),
            Supplier.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);

        return isColombian
            ? await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new SuppliersDomainException(
                    "suppliers.supplier.city_not_found",
                    "The city was not found.")
            : null;
    }

    /// <summary>
    /// Del contrato HTTP al dominio, sin normalizar ni validar: eso es trabajo de
    /// <see cref="Supplier.Create"/> y de <see cref="Supplier.Update"/>, y repetirlo aquí crearía una
    /// segunda definición de «válido». El <c>?? []</c> cubre una lista ausente del JSON, que el
    /// validador ya rechaza antes con su mapa por campo.
    /// </summary>
    public static SupplierWriteData ToWriteData(this ISupplierWriteCommand command) => new(
        command.Name,
        command.BusinessName,
        command.IdentificationType,
        command.IdentificationNumber,
        command.Country,
        command.CityId,
        command.CityName,
        command.Phone,
        command.Email,
        command.Address,
        command.PaymentTermDays,
        command.Category,
        command.Notes,
        [.. (command.BankAccounts ?? []).Select(account => new SupplierBankAccount
        {
            BankName = account.BankName,
            AccountNumber = account.AccountNumber,
            Currency = account.Currency
        })],
        [.. (command.Contacts ?? []).Select(contact => new SupplierContact
        {
            Name = contact.Name,
            Role = contact.Role,
            Phone = contact.Phone,
            Email = contact.Email
        })]);
}
