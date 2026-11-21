namespace Modules.Coordinadora.Domain;

/// <summary>
/// Un paquete como lo pide Coordinadora en <c>detalle</c> (spec 2026-11-21 §3): <c>alto</c>, <c>ancho</c> y <c>largo</c> en centímetros
/// enteros, <c>peso</c> en kilos con hasta dos decimales y <c>unidades</c>. Es el paquete por defecto de la conexión (§5.2) y el que la
/// persona corrige por envío (§5.3). Los topes son de este diseño, no de Coordinadora: frenan un cero o un valor imposible antes de
/// gastar una llamada.
/// </summary>
public sealed record CoordinadoraPackage(int HeightCm, int WidthCm, int LengthCm, decimal WeightKg, int Units)
{
    public const int MaxDimensionCm = 300;
    public const decimal MaxWeightKg = 999.99m;
    public const int MaxUnits = 99;

    /// <summary>El paquete validado: un valor fuera de rango es <c>coordinadora.package.invalid</c>.</summary>
    public static CoordinadoraPackage Create(int heightCm, int widthCm, int lengthCm, decimal weightKg, int units)
    {
        if (heightCm is < 1 or > MaxDimensionCm || widthCm is < 1 or > MaxDimensionCm || lengthCm is < 1 or > MaxDimensionCm)
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.PackageInvalid, $"Each dimension of the package must be between 1 and {MaxDimensionCm} cm.");
        }

        if (weightKg <= 0 || weightKg > MaxWeightKg || decimal.Round(weightKg, 2) != weightKg)
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.PackageInvalid, $"The weight must be more than 0 and up to {MaxWeightKg} kg, with two decimals at most.");
        }

        if (units is < 1 or > MaxUnits)
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.PackageInvalid, $"The units of the package must be between 1 and {MaxUnits}.");
        }

        return new CoordinadoraPackage(heightCm, widthCm, lengthCm, weightKg, units);
    }
}
