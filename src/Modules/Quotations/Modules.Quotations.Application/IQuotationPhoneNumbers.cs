namespace Modules.Quotations.Application;

/// <summary>
/// Parte un teléfono para el Excel de pedidos sin indicativo (spec 2026-11-25 indicativo). Quotations no
/// referencia a Customers, que es quien sabe partirlos: el adaptador vive en Bootstrapper.
/// </summary>
public interface IQuotationPhoneNumbers
{
    /// <summary>El número nacional de un teléfono en formato internacional (<c>+573001234567</c> →
    /// <c>3001234567</c>). Null si no se puede partir; nunca lanza. El teléfono no se registra en logs.</summary>
    string? NationalNumberOf(string phone);
}
