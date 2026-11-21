using BuildingBlocks.Domain;

namespace Modules.Coordinadora.Domain;

/// <summary>Una regla del dominio de Coordinadora que no se cumple. El código es <c>coordinadora.*</c>; el status lo decide el mapeo
/// central (<c>ApiExceptionHandler</c>), nunca un handler.</summary>
public sealed class CoordinadoraDomainException(string code, string message)
    : DomainException(code, message);
