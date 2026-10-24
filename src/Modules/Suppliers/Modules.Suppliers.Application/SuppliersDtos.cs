namespace Modules.Suppliers.Application;

// Contrato HTTP del directorio de proveedores (spec 2026-10-23 §6.2). La pantalla manda la forma:
// cada record existe porque una vista lo pide, no porque el agregado lo tenga.

/// <summary>
/// La ciudad DIVIPOLA ya resuelta (id, código y nombre) para que la ficha la pinte sin una segunda
/// llamada a Geography. Sólo viaja en proveedores colombianos; fuera de Colombia es <c>null</c> y
/// la ciudad va escrita en <see cref="SupplierResponse.CityName"/>.
/// </summary>
public sealed record SupplierCityDto(Guid Id, string DivipolaCode, string Name);

/// <summary>El departamento de la ciudad, resuelto junto con ella por el mismo motivo que <see cref="SupplierCityDto"/>.</summary>
public sealed record SupplierDepartmentDto(Guid Id, string DivipolaCode, string Name);

/// <summary>
/// Una cuenta bancaria, en el detalle y en el cuerpo de escritura con la misma forma: el PUT
/// reemplaza el conjunto entero, así que lo que el formulario recibe es exactamente lo que devuelve.
/// </summary>
public sealed record SupplierBankAccountPayload(string BankName, string AccountNumber, string Currency);

/// <summary>Un contacto del proveedor, con la misma forma en el detalle y en la escritura. Sólo el nombre es obligatorio.</summary>
public sealed record SupplierContactPayload(string Name, string? Role, string? Phone, string? Email);

/// <summary>
/// El detalle completo; lo devuelven GET, POST, PUT, activate y deactivate para que la ficha no
/// tenga que volver a pedirlo tras guardar. La ciudad viaja resuelta (<c>city</c>/<c>department</c>)
/// en Colombia y como texto (<c>cityName</c>) fuera de ella; los opcionales vuelven siempre como
/// <c>null</c>, nunca ausentes.
/// </summary>
public sealed record SupplierResponse(
    Guid Id,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string Country,
    SupplierCityDto? City,
    SupplierDepartmentDto? Department,
    string? CityName,
    int PaymentTermDays,
    string? Category,
    string? Notes,
    IReadOnlyList<SupplierBankAccountPayload> BankAccounts,
    IReadOnlyList<SupplierContactPayload> Contacts,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Sólo banco y número: la grilla muestra dónde pagarle, no la moneda de cada cuenta (como en Empresas).</summary>
public sealed record SupplierListBankAccount(string BankName, string AccountNumber);

/// <summary>
/// El subconjunto que pinta la grilla. <c>City</c> es un solo texto —el nombre DIVIPOLA o la
/// ciudad escrita— porque la grilla no distingue de dónde viene; el servidor lo resuelve para
/// que el navegador no tenga que combinar dos campos.
/// </summary>
public sealed record SupplierListItemResponse(
    Guid Id,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? City,
    string? Category,
    int PaymentTermDays,
    IReadOnlyList<SupplierListBankAccount> BankAccounts,
    bool IsActive);

/// <summary>La página de la lista con el total que la grilla necesita para paginar.</summary>
public sealed record SuppliersResponse(
    IReadOnlyCollection<SupplierListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Las categorías distintas en uso: alimentan el filtro de la lista y las sugerencias del formulario.</summary>
public sealed record SupplierCategoriesResponse(IReadOnlyList<string> Items);

/// <summary>
/// El cuerpo de POST y de PUT: es **un solo record** porque el spec define crear y editar con los
/// mismos campos (reemplazo entero: los opcionales en <c>null</c> limpian y las listas que llegan
/// son las que quedan). Excluye <c>id</c>, <c>isActive</c> y las fechas, que no se escriben aquí, y
/// la ciudad resuelta, de la que se manda sólo <c>cityId</c> (Colombia) o <c>cityName</c> (otro país).
/// </summary>
public sealed record SupplierWriteRequest(
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string Country,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    int PaymentTermDays,
    string? Category,
    string? Notes,
    IReadOnlyList<SupplierBankAccountPayload> BankAccounts,
    IReadOnlyList<SupplierContactPayload> Contacts);

/// <summary>
/// La respuesta de <c>POST .../suppliers/export</c>. Confirma que el archivo se generó y se subió,
/// y hasta cuándo sirve el enlace; el enlace en sí sale por correo, no por aquí (el frontend no
/// debe tomar el atajo de descargarlo desde la respuesta).
/// </summary>
public sealed record ExportSuppliersResponse(
    string FileName,
    int SupplierCount,
    DateTimeOffset ExpiresAt);
