namespace Modules.Customers.Application;

/// <summary>
/// Resuelve ciudad y departamento del modulo <c>Geography</c> para armar el CUC y para las
/// respuestas de <c>Customer</c> que devuelven la ciudad y el departamento resueltos.
///
/// Puerto en Application, adaptador en Bootstrapper — mismo patron que <c>IProductImageLookup</c>
/// entre Catalog y Storage (CAT-05). <c>Modules.Customers.Application</c> no puede referenciar
/// <c>Modules.Geography.Application</c> directamente:
/// <c>CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules</c> lo prohibe a
/// proposito, para que el acoplamiento entre dos modulos de negocio quede en el composition root
/// —que ya referencia a los dos y cuyo trabajo es exactamente cablearlos— y no se cuele por un
/// ProjectReference. El adaptador (<c>CustomerGeographyLookup</c>) vive en <c>Bootstrapper</c>.
/// </summary>
public interface ICustomerGeographyLookup
{
    Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="FindCityAsync"/>, para que <c>ListCustomersHandler</c>
    /// resuelva la pagina entera con una sola consulta en vez de una por cliente.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);

    /// <summary>
    /// Resuelve una ciudad por **nombre**, dentro de un departamento tambien por nombre — para la
    /// importacion masiva (Fase 5), donde el Excel lo llena una persona y trae texto, no un
    /// <see cref="Guid"/>.
    ///
    /// **Siempre el par, nunca la ciudad sola:** el mismo nombre de ciudad puede repetirse en mas
    /// de un departamento (varios "La Union" o "San Jose" del DIVIPOLA), asi que buscar solo por
    /// nombre de ciudad es ambiguo. La comparacion es insensible a mayusculas/minusculas y a
    /// espacios sobrantes, porque asi es como una persona tipea un nombre en una celda.
    /// </summary>
    Task<CustomerCityRef?> FindCityByNameAsync(
        string departmentName, string cityName, CancellationToken cancellationToken);

    /// <summary>
    /// Todos los departamentos DIVIPOLA. La usa la plantilla de importacion (Fase 6) para armar su
    /// hoja de referencia — la persona que llena el Excel necesita ver los nombres validos, no
    /// adivinarlos.
    /// </summary>
    Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// La ciudad y su departamento, traducidos al vocabulario de <c>customers</c>. Trae los dos juntos
/// porque el CUC necesita el codigo DIVIPOLA del departamento y las respuestas de cliente
/// necesitan los dos objetos resueltos — pedirlos por separado seria una segunda consulta por
/// cliente.
/// </summary>
public sealed record CustomerCityRef(
    Guid CityId,
    string CityDivipolaCode,
    string CityName,
    Guid DepartmentId,
    string DepartmentDivipolaCode,
    string DepartmentName);
