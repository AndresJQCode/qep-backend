using Modules.Customers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="IQuotationPhoneNumbers"/> (spec 2026-11-25 indicativo) sobre
/// <see cref="IPhoneNumberSplitter"/> de Customers. Sin región de respaldo: sólo se parte lo que trae su
/// país escrito, así que un número local nunca se reinterpreta como el de otro país.
/// </summary>
internal sealed class QuotationPhoneNumbers(IPhoneNumberSplitter phones) : IQuotationPhoneNumbers
{
    public string? NationalNumberOf(string phone) => phones.Split(phone)?.NationalNumber;
}
