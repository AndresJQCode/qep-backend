using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Catalog.Application;
using Modules.Customers.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Copilot;

/// <summary>
/// Spec 2026-11-17 (copiloto) §6 y plan fase 2 C3.2: clientes, catálogo y el resumen de Mensajería, con la misma consulta que cada endpoint
/// (<c>CustomerEndpoints</c>, <c>ProductEndpoints</c>, <c>MessagingReportEndpoints</c>). D-C10 (5): el MVP no lee notas de clientes (son otro
/// endpoint, <c>CustomerEngagementEndpoints</c>, que ninguna herramienta despacha) ni texto de conversaciones; la ficha tampoco lleva
/// direcciones, consentimiento ni contactos. El costo del producto es el que devuelve el handler: <c>null</c> si el miembro no puede verlo
/// (<c>CatalogCostAccess</c>), y así viaja.
/// </summary>
internal static class CopilotDirectoryTools
{
    /// <summary>§6: el detalle de un producto lleva a lo sumo estas variantes.</summary>
    public const int MaxVariants = 50;

    public static IReadOnlyList<ICopilotTool> All() =>
    [
        new SearchCustomersTool(),
        new GetCustomerTool(),
        new SearchProductsTool(),
        new GetProductTool(),
        new GetMessagingSummaryTool(),
    ];

    /// <summary>La etiqueta de un cliente es la de su ficha: la razón social si la tiene, si no el nombre de contacto.</summary>
    public static CopilotCitation CustomerRef(CustomerDto customer) =>
        new(CopilotCitationKinds.Customer, customer.Id.ToString(), customer.BusinessName ?? customer.Name);

    public static CopilotCitation ProductRef(Guid id, string name) => new(CopilotCitationKinds.Product, id.ToString(), name);
}

internal sealed class SearchCustomersTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_customers",
        "Busca clientes, 20 por página, por nombre, razón social, documento, CUC o teléfono. Sirve para encontrar clientes concretos y su id; los conteos salen de get_customers_summary.",
        CustomersPermissions.CustomerRead,
        TenantModuleKeys.Customers,
        """
        "search":{"type":["string","null"],"description":"Texto a buscar en nombre, razón social, documento o CUC, o null."},"identificationNumber":{"type":["string","null"],"description":"Número de documento exacto o null."},"phone":{"type":["string","null"],"description":"Teléfono o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListCustomersQuery(
                call.Principal.TenantId,
                CopilotArguments.Search(call.Arguments, "search"),
                Name: null,
                CopilotArguments.Search(call.Arguments, "identificationNumber"),
                Cuc: null,
                DepartmentIds: null,
                CityIds: null,
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize,
                Phone: CopilotArguments.Search(call.Arguments, "phone")),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, customer => new
        {
            @ref = CopilotDirectoryTools.CustomerRef(customer),
            customer.Cuc,
            customer.Name,
            customer.BusinessName,
            customer.IdentificationNumber,
            city = customer.City?.Name ?? customer.CityName,
            classification = customer.Classification?.Name,
            customer.IsActive,
            customer.IsComplete,
        });
        return CopilotToolResult.Ok(list, [.. page.Items.Take(CopilotReportTools.PageSize).Select(CopilotDirectoryTools.CustomerRef)]);
    }
}

internal sealed class GetCustomerTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_customer",
        "La ficha de un cliente: nombre, razón social, documento, contacto, ciudad, clasificación, etiquetas, empresa de la que es contacto y si está activo. No trae notas ni conversaciones.",
        CustomersPermissions.CustomerRead,
        TenantModuleKeys.Customers,
        """
        "customerId":{"type":"string","description":"Id del cliente (uuid), tomado de una búsqueda o de un resumen."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var customer = await call.Dispatcher.QueryAsync(
            new GetCustomerQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "customerId")), cancellationToken);
        var reference = CopilotDirectoryTools.CustomerRef(customer);
        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                customer.Cuc,
                customer.Name,
                customer.BusinessName,
                customer.PersonType,
                customer.IdentificationType,
                customer.IdentificationNumber,
                customer.Phone,
                customer.Email,
                customer.Country,
                city = customer.City?.Name ?? customer.CityName,
                department = customer.Department?.Name,
                classification = customer.Classification?.Name,
                tags = customer.Tags.Select(tag => tag.Name).ToList(),
                company = customer.Company is { } company ? new { company.Id, company.Name } : null,
                customer.ContactCount,
                customer.WithRetention,
                customer.VatSurplus,
                customer.IsActive,
                customer.IsComplete,
                customer.CreatedAt,
            },
            [reference]);
    }
}

internal sealed class SearchProductsTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_products",
        "Busca productos del catálogo, 20 por página, por nombre o código, con su rango de precios por moneda y cuántas variantes tienen.",
        CatalogPermissions.ProductRead,
        TenantModuleKeys.Catalog,
        """
        "search":{"type":["string","null"],"description":"Texto a buscar en nombre o código, o null."},"isActive":{"type":["boolean","null"],"description":"true sólo activos, false sólo inactivos, null todos."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListProductsQuery(
                call.Principal.TenantId,
                CopilotArguments.Search(call.Arguments, "search"),
                Name: null,
                Code: null,
                CopilotArguments.Flag(call.Arguments, "isActive"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, product => new
        {
            @ref = CopilotDirectoryTools.ProductRef(product.Id, product.Name),
            product.Name,
            product.Code,
            product.IsActive,
            product.VariantCount,
            product.PriceRange,
        });
        return CopilotToolResult.Ok(
            list, [.. page.Items.Take(CopilotReportTools.PageSize).Select(product => CopilotDirectoryTools.ProductRef(product.Id, product.Name))]);
    }
}

internal sealed class GetProductTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_product",
        "El detalle de un producto: descripción, opciones y hasta 50 variantes con código, precios por moneda y, sólo si quien pregunta puede verlo, el costo (costs null = no lo puede ver).",
        CatalogPermissions.ProductRead,
        TenantModuleKeys.Catalog,
        """
        "productId":{"type":"string","description":"Id del producto (uuid), tomado de una búsqueda."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var product = await call.Dispatcher.QueryAsync(
            new GetProductQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "productId")), cancellationToken);
        var reference = CopilotDirectoryTools.ProductRef(product.Id, product.Name);
        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                product.Name,
                product.Description,
                product.IsActive,
                product.VisibleInStore,
                options = product.Options.Select(option => new { option.Name, values = option.Values.Select(value => value.Name).ToList() }).ToList(),
                variants = new
                {
                    items = product.Variants.Take(CopilotDirectoryTools.MaxVariants).Select(variant => new
                    {
                        variant.Id,
                        variant.Name,
                        variant.Code,
                        variant.Barcode,
                        variant.IsActive,
                        variant.Prices,
                        // El handler ya decidió: null si el miembro no tiene catalog.product_cost.read (ni .manage).
                        variant.Costs,
                    }).ToList(),
                    total = product.Variants.Count,
                    truncated = product.Variants.Count > CopilotDirectoryTools.MaxVariants,
                },
            },
            [reference]);
    }
}

internal sealed class GetMessagingSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_messaging_summary",
        "Resumen de Mensajería del período: conversaciones, contactos nuevos y recurrentes, tiempos de primera respuesta y de respuesta del asesor, sesiones por asesor y lo que resolvieron los chatbots, con la comparación contra el período anterior. Sólo agregados: no trae el texto de ningún mensaje.",
        MessagingPermissions.ConversationRead,
        TenantModuleKeys.Messaging,
        """
        "from":{"type":"string","description":"Primer día del período, yyyy-MM-dd, en la fecha del negocio."},"to":{"type":"string","description":"Último día del período, yyyy-MM-dd, inclusive."},"groupBy":{"type":"string","enum":["day","week","month"],"description":"Cómo agrupar la serie."},"channel":{"type":["string","null"],"description":"WhatsApp, Instagram o null para ambos."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetMessagingReportSummaryQuery(
                call.Principal.TenantId, from, to, CopilotArguments.Code(call.Arguments, "groupBy"), CopilotArguments.Code(call.Arguments, "channel")),
            cancellationToken);
        return CopilotToolResult.Ok(new { summary }, []);
    }
}
