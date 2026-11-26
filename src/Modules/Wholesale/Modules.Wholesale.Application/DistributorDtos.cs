using System.Text.Json.Serialization;

namespace Modules.Wholesale.Application;

/// <summary>
/// La lista del distribuidor (spec 2026-11-22: una lista de Catalog). <c>AdjustmentPercent</c> va con signo: negativo es descuento.
/// <c>Exists</c> en <c>false</c> dice que la lista ya no está (o el distribuidor viene de antes de la unificación): la pantalla le
/// pide elegir una en vez de pintar un nombre que no existe.
/// </summary>
public sealed record DistributorPriceListDto(Guid Id, string Name, decimal AdjustmentPercent, bool Exists = true);

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

/// <summary>
/// El aviso por WhatsApp de la invitación (spec 2026-11-18), con lo que el detalle pinta (BFF): el estado por nombre
/// (<c>Pending</c>, <c>Sent</c>, <c>Failed</c>), el código sólo con <c>Failed</c> y el número de la conexión por la que sale,
/// sin pedir la conexión aparte. <c>ConnectionLabel</c> es <c>"Sin nombre"</c> si la conexión ya no está activa (D-7).
/// </summary>
public sealed record DistributorWhatsAppNoticeDto(string Status, string? FailureCode, string ConnectionLabel);

/// <summary>La invitación del detalle. <c>Expired</c> es perezoso: invitada con <c>ExpiresAt</c> pasado. <c>WhatsAppNotice</c>
/// es <c>null</c> sin teléfono y tras un cambio de teléfono o conexión, hasta el próximo «Reenviar» (P-3).</summary>
public sealed record DistributorInvitationDto(
    DateTimeOffset SentAt,
    DateTimeOffset ExpiresAt,
    bool Expired,
    DateTimeOffset? AcceptedAt,
    DistributorWhatsAppNoticeDto? WhatsAppNotice);

/// <summary>
/// Una fila de la lista de distribuidores (BFF): nombres de lista, asesor y tenant ya resueltos. <c>Version</c> va para que
/// «Reenviar» desde la fila mande su <c>If-Match</c> sin pedir el detalle (adenda del contrato). <c>Status</c> por nombre.
/// <c>Phone</c> es el celular del aviso en E.164, o <c>null</c> (spec 2026-11-18).
/// </summary>
public sealed record DistributorSummaryDto(
    Guid Id,
    string Status,
    long Version,
    string BusinessName,
    string Country,
    string IdentificationType,
    string IdentificationNumber,
    string ContactName,
    string Email,
    string? Phone,
    DistributorPriceListDto PriceList,
    DistributorAdvisorDto Advisor,
    DistributorTenantDto? DistributorTenant,
    bool InvitationExpired,
    DistributorInvitationSummaryDto? Invitation,
    DateTimeOffset CreatedAt);

/// <summary>
/// La ciudad del distribuidor, con el nombre ya resuelto. <c>Id</c> es el de Geography en Colombia y <c>null</c> afuera, donde
/// la ciudad es el texto que escribió el mayorista.
/// <para><c>DepartmentId</c> y <c>DepartmentName</c> van por la pantalla (BFF): la edición elige departamento y luego ciudad, y
/// sin ellos no tiene cómo preseleccionar la guardada. Son <c>null</c> afuera de Colombia y si la ciudad ya no se
/// encuentra.</para>
/// </summary>
public sealed record DistributorCityDto(Guid? Id, string Name, Guid? DepartmentId = null, string? DepartmentName = null);

/// <summary>
/// El distribuidor completo para su pantalla (BFF). <c>AllowedActions</c> ya cruza estado y permiso (en minúscula y en el orden
/// del contrato: <c>edit</c>, <c>resend</c>, <c>delete</c>, <c>block</c>, <c>unblock</c>); sin
/// <c>wholesale.distributor.manage</c> llega vacío. <c>Phone</c> es el celular del aviso en E.164, o <c>null</c> (spec
/// 2026-11-18).
/// <para><c>InvitationUrl</c> (spec 2026-11-19 §1) es el link para «Copiar» y sólo viene en la respuesta de invitar (el mismo
/// token del correo) y en la de rotar el link: la base guarda el hash, así que es la única vez que existe. En cualquier otra
/// respuesta es <c>null</c> y el frontend tiene que rotar para volver a tenerlo; sin este campo no habría cómo copiarlo.</para>
/// <para><c>City</c> trae el nombre ya resuelto (BFF: la pantalla de detalle lo pinta sin conocer Geography) y es <c>null</c>
/// en una invitación anterior a la migración; <c>Address</c> es opcional. El resumen de la lista no los lleva: la grilla no los
/// muestra y resolverlos por fila sería una consulta más por página.</para>
/// </summary>
public sealed record DistributorDetailDto(
    Guid Id,
    string Status,
    long Version,
    string BusinessName,
    string Country,
    string IdentificationType,
    string IdentificationNumber,
    string ContactName,
    string Email,
    string? Phone,
    DistributorPriceListDto PriceList,
    DistributorAdvisorDto Advisor,
    DistributorCompanyDto BillingCompany,
    DistributorTenantDto? DistributorTenant,
    Guid? CustomerId,
    DistributorInvitationDto Invitation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<string> AllowedActions,
    string? InvitationUrl = null,
    DistributorCityDto? City = null,
    string? Address = null)
{
    /// <summary>
    /// El <c>ToString</c> generado imprimiría <c>InvitationUrl</c>, que lleva el token plano: un log o un mensaje de aserción
    /// que nombre el DTO filtraría el link. Sólo se imprime lo que identifica la fila.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Status = ").Append(Status).Append(", Version = ").Append(Version);
        return true;
    }
}

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

/// <summary>Un número de WhatsApp para el aviso: <c>Label</c> es su número visible o, sin número, su nombre (D-7).</summary>
public sealed record DistributorWhatsAppConnectionDto(Guid Id, string Label);

/// <summary>Todo lo que el formulario de invitar o editar necesita elegir, en una llamada y por nombre.
/// <c>WhatsAppConnections</c> son las listas para el aviso (spec 2026-11-18); vacía = el formulario no ofrece el aviso.
/// <c>DefaultCountry</c> es el país con que arranca «Invitar» (spec 2026-11-18, país y documento): el de la configuración regional
/// del tenant, o Colombia sin fila.</summary>
public sealed record DistributorOptionsDto(
    IReadOnlyList<DistributorPriceListDto> PriceLists,
    IReadOnlyList<DistributorAdvisorDto> Advisors,
    IReadOnlyList<DistributorCompanyOptionDto> Companies,
    IReadOnlyList<DistributorWhatsAppConnectionDto> WhatsAppConnections,
    string DefaultCountry);

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
