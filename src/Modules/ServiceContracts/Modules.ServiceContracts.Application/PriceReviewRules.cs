using FluentValidation;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// El reajuste de un contrato tal como llega por el cable (<c>{ adjustmentMode, ipcExtraPoints, nextReviewDate }</c>), con todo
/// anulable para que su ausencia sea un <c>validation.failed</c> con el campo y no un 400 del binding.
/// <c>NextReviewDate</c> nula en el alta es la revisión por defecto: el aniversario siguiente a lo último generado.
/// </summary>
public sealed record PriceReviewInput(string? AdjustmentMode, decimal? IpcExtraPoints, DateOnly? NextReviewDate);

/// <summary>Las reglas del reajuste (spec reajuste §7 y §8).</summary>
internal static class PriceReviewRules
{
    /// <summary>Precisión de <c>billing.services.ipc_extra_points</c> (<c>numeric(5,2)</c>).</summary>
    private const int PointsPrecision = 5;

    private const int PointsScale = 2;

    /// <summary>Sólo los dos nombres exactos del contrato, como <see cref="ServiceRules.TryParseCycle"/>.</summary>
    public static bool TryParseMode(string? text, out AdjustmentMode mode)
    {
        switch (text)
        {
            case nameof(AdjustmentMode.Manual):
                mode = AdjustmentMode.Manual;
                return true;
            case nameof(AdjustmentMode.Ipc):
                mode = AdjustmentMode.Ipc;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static IRuleBuilderOptions<T, string?> MustBeAnAdjustmentMode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => TryParseMode(text, out _)).WithMessage("AdjustmentMode must be Manual or Ipc.");

    public static IRuleBuilderOptions<T, decimal?> MustBeIpcExtraPoints<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.NotNull()
            .InclusiveBetween(0m, ServiceContract.MaxIpcExtraPoints)
            .PrecisionScale(PointsPrecision, PointsScale, ignoreTrailingZeros: true);
}
