using System.Text.Json.Serialization;

namespace Modules.Wholesale.Application;

/// <summary>La lista de precios del distribuidor con lo que la fila pinta: nombre y descuento, sin pedir la lista aparte.</summary>
public sealed record DistributorPriceListDto(Guid Id, string Name, decimal DiscountPercent);

/// <summary>El asesor con su nombre visible ya resuelto (<c>"Sin nombre"</c> si la membresía ya no está, D-15).</summary>
public sealed record DistributorAdvisorDto(Guid MemberId, string Name);

/// <summary>El tenant del distribuidor con su nombre vigente, desde que aceptó.</summary>
public sealed record DistributorTenantDto(Guid Id, string Name);

/// <summary>La empresa facturadora que da la identidad del proveedor vinculado (D-2).</summary>
public sealed record DistributorCompanyDto(Guid Id, string Name);

/// <summary>
/// La invitación en la fila (ruling W3): «Vence 20/11 · Reenviar». Sólo viene mientras la relación está invitada.
/// <c>CanResend</c> cruza el estado con <c>wholesale.distributor.manage</c>, y sigue en <c>true</c> con la invitación vencida:
/// reenviar es como se renueva.
/// </summary>
public sealed record DistributorInvitationSummaryDto(DateTimeOffset ExpiresAt, bool CanResend);

/// <summary>La invitación del detalle. <c>Expired</c> es perezoso: invitada con <c>ExpiresAt</c> pasado.</summary>
public sealed record DistributorInvitationDto(DateTimeOffset SentAt, DateTimeOffset ExpiresAt, bool Expired, DateTimeOffset? AcceptedAt);

/// <summary>
/// Una fila de la lista de distribuidores (BFF): nombres de lista, asesor y tenant ya resueltos. <c>Version</c> va para que
/// «Reenviar» desde la fila mande su <c>If-Match</c> sin pedir el detalle (adenda del contrato). <c>Status</c> por nombre.
/// </summary>
public sealed record DistributorSummaryDto(
    Guid Id,
    string Status,
    long Version,
    string BusinessName,
    string IdentificationNumber,
    string ContactName,
    string Email,
    DistributorPriceListDto PriceList,
    DistributorAdvisorDto Advisor,
    DistributorTenantDto? DistributorTenant,
    bool InvitationExpired,
    DistributorInvitationSummaryDto? Invitation,
    DateTimeOffset CreatedAt);

/// <summary>
/// El distribuidor completo para su pantalla (BFF). <c>AllowedActions</c> ya cruza estado y permiso (en minúscula y en el orden
/// del contrato: <c>edit</c>, <c>resend</c>, <c>delete</c>, <c>block</c>, <c>unblock</c>); sin
/// <c>wholesale.distributor.manage</c> llega vacío. El token nunca viaja.
/// </summary>
public sealed record DistributorDetailDto(
    Guid Id,
    string Status,
    long Version,
    string BusinessName,
    string IdentificationNumber,
    string ContactName,
    string Email,
    DistributorPriceListDto PriceList,
    DistributorAdvisorDto Advisor,
    DistributorCompanyDto BillingCompany,
    DistributorTenantDto? DistributorTenant,
    Guid? CustomerId,
    DistributorInvitationDto Invitation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<string> AllowedActions);

/// <summary>
/// Cuántos distribuidores hay por estado con <c>search</c> y sin <c>status</c>, para las pestañas. Las claves son el nombre del
/// estado tal cual y <c>all</c> en minúscula, así que cada propiedad lleva su <c>JsonPropertyName</c> explícito (como
/// <c>PurchaseOrderStatusCountsDto</c>). Siempre vienen las cuatro, también en cero; <c>All</c> es la suma.
/// </summary>
public sealed record DistributorStatusCountsDto(
    [property: JsonPropertyName("Invited")] int Invited,
    [property: JsonPropertyName("Active")] int Active,
    [property: JsonPropertyName("Blocked")] int Blocked,
    [property: JsonPropertyName("all")] int All);

/// <summary>El sobre de paginación de las listas del módulo más <c>Counts</c>, que la pantalla nunca suma.</summary>
public sealed record DistributorsResponse(
    IReadOnlyList<DistributorSummaryDto> Items,
    int Total,
    int Page,
    int PageSize,
    DistributorStatusCountsDto Counts);

/// <summary>Una empresa para elegir la facturadora: <c>Eligible</c> dice si su documento sirve para el proveedor (D-2), así la
/// pantalla la muestra deshabilitada en vez de esperar el 422.</summary>
public sealed record DistributorCompanyOptionDto(Guid Id, string Name, string DocumentType, string DocumentNumber, bool Eligible);

/// <summary>Todo lo que el formulario de invitar o editar necesita elegir, en una llamada y por nombre.</summary>
public sealed record DistributorOptionsDto(
    IReadOnlyList<DistributorPriceListDto> PriceLists,
    IReadOnlyList<DistributorAdvisorDto> Advisors,
    IReadOnlyList<DistributorCompanyOptionDto> Companies);

/// <summary>
/// La invitación vista desde el link, sin sesión (D-20). <c>Status</c> es <c>pending</c>, <c>accepted</c> o <c>expired</c>,
/// derivado (vencimiento perezoso). <c>hasPassword</c> lo suma la composición en <c>src/Api</c>, que conoce Identity.
/// </summary>
public sealed record InvitationSummaryDto(
    string WholesalerName,
    string BusinessName,
    string ContactName,
    string Email,
    string Status,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Un tenant que quien acepta administra como owner. <c>IneligibleReason</c> (ruling W6) dice por qué no se puede elegir:
/// <c>same_tenant</c> (es el mayorista) o <c>already_linked</c>; <c>null</c> si se puede. <c>ModulesToEnable</c> son las
/// claves de <c>catalog</c>, <c>suppliers</c> y <c>purchases</c> que vincular prendería.
/// </summary>
public sealed record LinkableTenantDto(
    Guid TenantId,
    string Name,
    bool Linkable,
    string? IneligibleReason,
    IReadOnlyList<string> ModulesToEnable);

/// <summary>
/// El resultado de aceptar. <c>Created</c> dice si el tenant es el que se reservó para esta invitación; <c>EnabledModules</c>,
/// lo que esta aceptación prendió. Una aceptación repetida por el mismo usuario responde <c>Created = false</c> y sin módulos
/// (ruling W9). Después, la SPA relee <c>/auth/me</c>.
/// </summary>
public sealed record AcceptedInvitationDto(Guid TenantId, string TenantName, bool Created, IReadOnlyList<string> EnabledModules);

/// <summary>Cómo quiere aceptar: creando un tenant o vinculando uno propio.</summary>
public abstract record AcceptInvitationChoice;

/// <summary>Crear el tenant del distribuidor con los datos y reglas de <c>register-tenant</c>.</summary>
public sealed record NewTenantChoice(Modules.Tenancy.Application.TenantRegistrationData Data) : AcceptInvitationChoice;

/// <summary>Vincular un tenant que quien acepta administra como owner.</summary>
public sealed record LinkTenantChoice(Guid TenantId) : AcceptInvitationChoice;
