namespace Modules.Quotations.Application;

/// <summary>Una cuenta de comprador de la tienda, sólo con lo que la consola muestra (spec 2026-11-15 §4).</summary>
public sealed record QuotationBuyerAccountRef(Guid Id, string Email);

/// <summary>
/// Quotations → Buyers (spec 2026-11-15 §4): el detalle del pedido muestra el correo de su cuenta de comprador y el asesor
/// la cambia por correo. Quotations no ve Buyers (<c>QuotationsLayerTests</c>): el adaptador vive en Bootstrapper. Las
/// cuentas son globales, no del tenant.
/// </summary>
public interface IQuotationBuyerAccountLookup
{
    /// <summary>La cuenta por id; <c>null</c> si ya no existe. Borrar la cuenta no limpia <c>buyer_account_id</c> del
    /// pedido, así que un id sin cuenta es un estado normal.</summary>
    Task<QuotationBuyerAccountRef?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>La cuenta verificada de ese correo, normalizado como lo hace Buyers; <c>null</c> si no existe o no está
    /// verificada. Un solo resultado para los dos casos: el asesor no necesita distinguirlos.</summary>
    Task<QuotationBuyerAccountRef?> FindVerifiedByEmailAsync(string email, CancellationToken cancellationToken);
}
