using BuildingBlocks.Domain;

namespace Modules.Siigo.Domain;

/// <summary>Una regla del dominio de Siigo que no se cumple. El código es <c>siigo.*</c>; el status lo decide el mapeo central
/// (<c>ApiExceptionHandler</c>), nunca un handler.</summary>
public sealed class SiigoDomainException(string code, string message)
    : DomainException(code, message);
