using BuildingBlocks.Domain;

namespace Modules.Buyers.Domain;

/// <summary>
/// La contraseña actual que mandó el comprador no es la suya. Es un 422 con el campo marcado y no un 401 (D12): hay sesión válida, y un
/// 401 haría que el BFF borre la cookie.
/// </summary>
public sealed class BuyerPasswordIncorrectException : DomainException, IHasFieldErrors
{
    public BuyerPasswordIncorrectException(string field)
        : base(BuyerErrorCodes.PasswordIncorrect, "The password is not correct.") =>
        FieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = ["La contraseña no es correcta."],
        };

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
