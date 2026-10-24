namespace Modules.Suppliers.Domain;

/// <summary>
/// Todo lo que una persona escribe de un proveedor, tal como llega: texto sin normalizar. Lo
/// consumen <see cref="Supplier.Create"/> y <see cref="Supplier.Update"/>, que son quienes
/// normalizan y validan. Vive en el dominio para que ambas firmas no crezcan a quince parámetros
/// sueltos, la mayoría <c>string?</c>, que el compilador no distingue entre sí.
/// </summary>
public sealed record SupplierWriteData(
    string? Name,
    string? BusinessName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Country,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    int PaymentTermDays,
    string? Category,
    string? Notes,
    IReadOnlyList<SupplierBankAccount> BankAccounts,
    IReadOnlyList<SupplierContact> Contacts);
