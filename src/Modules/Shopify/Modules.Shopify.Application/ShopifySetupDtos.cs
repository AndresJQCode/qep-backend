namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §7: todo lo que el asistente de la tienda dibuja, en una sola lectura (BFF). La pantalla no
/// deduce el estado de ningún paso: <see cref="Steps"/>, <see cref="OpenStep"/>, <c>missing</c> y <c>blockedBy</c> salen de acá.
/// Las colecciones viajan siempre, también vacías (<c>[]</c>, nunca ausentes), y los enums con su nombre.
/// </summary>
/// <param name="Steps">Siempre los cinco, en el orden de <see cref="ShopifySetupStepKeys.All"/>.</param>
/// <param name="OpenStep">El primer paso que no está <c>Done</c>; <c>null</c> si todos lo están (retomar donde quedó).</param>
/// <param name="Companies">Las empresas activas del tenant, para elegir una existente en vez de crearla.</param>
/// <param name="Waiting">Los pedidos en espera de la tienda por motivo: lo que la cabecera muestra como «Lo que te falta».</param>
public sealed record ShopifySetupDto(
    ShopifySetupStoreDto Store,
    IReadOnlyList<ShopifySetupStepDto> Steps,
    string? OpenStep,
    ShopifySetupCompanySectionDto Company,
    IReadOnlyList<ShopifySetupCompanyOptionDto> Companies,
    ShopifySetupAdvisorSectionDto Advisor,
    ShopifySetupBuyersSectionDto Buyers,
    ShopifySetupProductsSectionDto Products,
    ShopifySetupHistorySectionDto History,
    IReadOnlyList<ShopifyWaitingGroupDto> Waiting,
    ShopifySetupStatsDto Stats);

/// <summary>La tienda. <see cref="Settings"/> es el mismo <see cref="ShopifyStoreDto"/> del <c>GET</c> de la tienda (spec §7): el
/// cuerpo completo que el <c>PUT</c> necesita, con su <c>version</c>, para que los pasos «Asesor» y «Compradores» guarden sin otra
/// lectura. <see cref="Currency"/> es la del perfil de Shopify o, si no se leyó, la de la cuenta guardada; <c>null</c> sin ninguna.
/// <see cref="ConnectionStatus"/> es el nombre del estado de Integrations, tal cual.</summary>
public sealed record ShopifySetupStoreDto(string Name, string? ShopDomain, string? Currency, string ConnectionStatus, ShopifyStoreDto Settings);

/// <summary>Un paso con su estado por nombre (<c>Done</c>, <c>Pending</c>, <c>Blocked</c>). <see cref="BlockedBy"/> es <c>[]</c> salvo
/// en <c>Blocked</c>; <see cref="Missing"/> son códigos de <see cref="ShopifySetupMissing"/> (<c>[]</c> en un paso hecho).</summary>
public sealed record ShopifySetupStepDto(string Key, string Status, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> Missing);

/// <summary>El paso «Empresa». <see cref="Selected"/> es la empresa de la tienda si sigue activa; <c>null</c> si no tiene o se
/// desactivó. <see cref="SuggestedChargesVat"/> (spec §4.3) es <c>null</c> cuando no hay datos para sugerir (sin
/// <c>read_products</c>, sin productos, Shopify caído o el paso ya hecho): la pantalla no preselecciona nada.</summary>
public sealed record ShopifySetupCompanySectionDto(
    ShopifySetupCompanyOptionDto? Selected, ShopifySuggestedCompanyDto Suggested, bool? SuggestedChargesVat);

/// <summary>Una empresa del tenant. <see cref="HasBankAccount"/> cambia de sentido según dónde viaja, a propósito, para que la
/// pantalla no compare cuentas: en <c>company.selected</c> dice que la <b>tienda</b> guarda una cuenta de esta empresa; en
/// <c>companies</c>, que la empresa tiene al menos una. <see cref="TaxId"/> es <c>null</c> si la empresa no tiene NIT
/// (<c>tax_id_missing</c>).</summary>
public sealed record ShopifySetupCompanyOptionDto(
    Guid Id,
    string Name,
    string? TaxId,
    string DocumentType,
    string Country,
    bool ChargesVat,
    bool HasBankAccount,
    IReadOnlyList<ShopifySetupBankAccountDto> BankAccounts);

public sealed record ShopifySetupBankAccountDto(string BankName, string AccountNumber, string Currency);

/// <summary>Lo que Shopify sabe de la tienda, como empresa a crear. <see cref="CityName"/> es el texto de Shopify cuando la ciudad
/// no se resolvió en Geography (o el país no es <c>CO</c>); entonces <see cref="CityId"/> es <c>null</c>.
/// <see cref="DepartmentId"/> puede venir aunque la ciudad no, para preseleccionar el departamento.</summary>
public sealed record ShopifySuggestedCompanyDto(
    string Name, string? Address, Guid? CityId, Guid? DepartmentId, string? CityName, string? Phone, string? Email, string? Country);

/// <summary>El paso «Asesor». <see cref="DefaultMembershipId"/> es el asesor guardado o, sin él, quien está configurando: así el
/// paso se resuelve con un clic.</summary>
public sealed record ShopifySetupAdvisorSectionDto(
    Guid? SelectedMembershipId, Guid? DefaultMembershipId, IReadOnlyList<ShopifySetupMemberDto> Options);

/// <summary>Una membresía activa. <see cref="IsMe"/> marca la de quien hace la lectura.</summary>
public sealed record ShopifySetupMemberDto(Guid MembershipId, string Name, bool IsMe);

/// <summary>El paso «Compradores»: las mismas opciones que <c>GET …/document-sources</c>, los tipos de documento de Customers y las
/// clasificaciones activas, con lo que la tienda ya tiene (o los valores por defecto de una tienda sin configurar).</summary>
public sealed record ShopifySetupBuyersSectionDto(
    IReadOnlyList<string> DocumentSources,
    string DocumentAttributeKey,
    string DocumentTypeAttributeKey,
    IReadOnlyList<string> IdentificationTypes,
    string DefaultIdentificationType,
    Guid? DefaultClassificationId,
    IReadOnlyList<ShopifySetupClassificationDto> Classifications);

public sealed record ShopifySetupClassificationDto(Guid Id, string Name);

/// <summary>El paso «Productos». <see cref="ReauthorizeUrl"/> sólo viaja sin <c>read_products</c> y si el cliente mandó
/// <c>redirectUri</c>. <see cref="Coverage"/> es la última medición guardada, o la que se midió en esta lectura (plan D8); <c>null</c>
/// si nunca se midió o Shopify no respondió.</summary>
public sealed record ShopifySetupProductsSectionDto(
    bool ScopeGranted,
    string? ReauthorizeUrl,
    ShopifyProductImportDto? LastImport,
    ShopifySetupCoverageDto? Coverage,
    DateTimeOffset? CoverageAcceptedAt);

/// <summary>La forma de <see cref="ShopifyProductCoverageDto"/> más la hora de la medición: la pantalla dice «medido hace…».</summary>
public sealed record ShopifySetupCoverageDto(
    int ShopifyVariants, int Matched, bool CatalogEmpty, IReadOnlyList<ShopifyMissingVariantDto> MissingSample, DateTimeOffset MeasuredAt);

/// <summary>El paso «Historial»: los días configurados y la última corrida desde el asistente (<c>null</c> si nunca corrió).</summary>
public sealed record ShopifySetupHistorySectionDto(int? HistoryDays, ShopifySetupHistoryRunDto? LastRun);

/// <summary>La corrida desde el asistente. <see cref="Waiting"/> son los pedidos de esa corrida que quedaron en espera, por motivo.</summary>
public sealed record ShopifySetupHistoryRunDto(
    DateTimeOffset RanAt, int Days, int Fetched, int Imported, IReadOnlyList<ShopifyWaitingCountDto> Waiting);

public sealed record ShopifyWaitingCountDto(string Reason, int Count);

/// <summary>Los pedidos en espera por motivo. <see cref="Sample"/>: hasta 5 SKU para <c>product_unmatched</c>; <c>[]</c> para los
/// demás motivos.</summary>
public sealed record ShopifyWaitingGroupDto(string Reason, int Count, IReadOnlyList<string> Sample);

/// <summary>La cabecera del asistente: pedidos importados los últimos 7 días, productos vinculados a la tienda y la última
/// conciliación.</summary>
public sealed record ShopifySetupStatsDto(int ImportedLast7Days, int ProductsSynced, DateTimeOffset? LastSyncAt);
