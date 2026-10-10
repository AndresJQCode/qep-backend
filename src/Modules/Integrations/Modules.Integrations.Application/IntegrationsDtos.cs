namespace Modules.Integrations.Application;

// BFF (CLAUDE.md, «Convenciones del backend»): la pantalla Ajustes → Integraciones dibuja el formulario
// desde el catálogo y no conoce los enums del backend, así que todo viaja por nombre (Active, Messaging,
// Secret) y en camelCase. Las claves de los diccionarios viajan tal cual (fromNumber, apiToken).

/// <summary>Los proveedores visibles para el tenant, completa aunque esté vacía: la pantalla dibuja
/// «no hay integraciones para tus módulos» sin preguntar nada más.</summary>
public sealed record IntegrationsCatalogResponse(IReadOnlyList<ProviderResponse> Providers);

/// <summary><c>connectionCount</c> viaja calculado para que la tarjeta muestre «2 de 20» sin pedir la
/// lista.</summary>
public sealed record ProviderResponse(
    string Key,
    string DisplayName,
    string Category,
    IReadOnlyList<ProviderFieldResponse> Fields,
    int MaxConnections,
    int ConnectionCount,
    ProviderOnboardingResponse Onboarding);

/// <summary>BFF: la tarjeta del catálogo decide si abre un formulario o el popup de Meta sin conocer el
/// proveedor. <c>appId</c> y <c>configId</c> son públicos (viajan en la URL del popup); el secreto nunca
/// sale. Con <c>Kind = "Form"</c> los otros tres van ausentes, para que la forma sea exactamente
/// <c>{ "kind": "Form" }</c>.</summary>
public sealed record ProviderOnboardingResponse(
    string Kind,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AppId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ConfigId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GraphApiVersion);

/// <summary>Sin el patrón: el formulario no valida con regex del backend; el 422 marca el campo. Sin los
/// campos internos (spec 2026-10-09 §6.1): los llena el backend —Embedded Signup o el probador— y el
/// formulario no tiene nada que pedir por ellos; sí salen en <c>ConnectionResponse</c>, que es lo que la
/// pantalla muestra de una conexión ya hecha.</summary>
public sealed record ProviderFieldResponse(string Key, string Label, string Kind, bool Required, int MaxLength);

/// <summary>Sin paginación: hay tope de 20 conexiones por proveedor (spec, «Endpoints»).</summary>
public sealed record ConnectionsResponse(IReadOnlyList<ConnectionResponse> Items);

/// <summary>
/// <c>fields</c> y <c>secrets</c> llevan <b>todas</b> las claves del proveedor (P21), para que el
/// formulario no tenga que cruzar con el catálogo para saber qué falta. Nunca un valor de secreto.
/// </summary>
public sealed record ConnectionResponse(
    Guid Id,
    string ProviderKey,
    string Name,
    string Status,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyDictionary<string, SecretStateResponse> Secrets,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    ConnectionAuthorResponse CreatedBy,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary><c>readable</c> descifra de verdad y descarta el valor: si la llave se retiró o los bytes
/// se dañaron, la pantalla pide pegar la clave otra vez.</summary>
public sealed record SecretStateResponse(bool Configured, DateTimeOffset? UpdatedAt, bool Readable);

/// <summary>El nombre viaja resuelto (nombre de la membresía o correo) para que la lista no pida los
/// miembros aparte; <c>null</c> si la membresía ya no está en el tenant.</summary>
public sealed record ConnectionAuthorResponse(Guid MemberId, string? DisplayName);
