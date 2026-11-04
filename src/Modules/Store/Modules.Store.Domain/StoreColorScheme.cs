namespace Modules.Store.Domain;

/// <summary>
/// Razones WCAG truncadas a 2 decimales, como las muestra el backoffice. Se truncan y no se redondean
/// para que la razón mostrada sea menor que 4.50 exactamente cuando la bandera está encendida: 4.496
/// redondea a 4.50 pero no cumple. Las banderas se calculan con la razón sin truncar.
/// </summary>
public sealed record StoreColorChecks(
    double OnPrimaryContrast,
    double AccentTextContrast,
    bool OnPrimaryLowContrast,
    bool AccentTextLowContrast);

public sealed record StoreResolvedColors(
    StoreColor Primary,
    StoreColor OnPrimary,
    StoreColor AccentText,
    StoreColorChecks Checks);

public static class StoreColorScheme
{
    public const double MinimumContrast = 4.5;

    private static readonly StoreColor NearBlack = new(0x11, 0x11, 0x11);

    /// <summary>
    /// Lo que pinta la tienda. Un override se toma tal cual, aunque contraste poco: el backoffice
    /// avisa y el backend nunca lo rechaza (decisión del dueño, 2026-11-03).
    /// </summary>
    public static StoreResolvedColors Resolve(StoreColor primary, StoreColor? onPrimary, StoreColor? accentText)
    {
        var resolvedOnPrimary = onPrimary ?? AutoOnPrimary(primary);
        var resolvedAccentText = accentText ?? AutoAccentText(primary);

        var onPrimaryRatio = StoreColor.Contrast(resolvedOnPrimary, primary);
        var accentRatio = StoreColor.Contrast(resolvedAccentText, StoreColor.White);

        var checks = new StoreColorChecks(
            Truncate(onPrimaryRatio),
            Truncate(accentRatio),
            onPrimaryRatio < MinimumContrast,
            accentRatio < MinimumContrast);

        return new StoreResolvedColors(primary, resolvedOnPrimary, resolvedAccentText, checks);
    }

    private static double Truncate(double ratio) => Math.Floor(ratio * 100d) / 100d;

    private static StoreColor AutoOnPrimary(StoreColor primary) =>
        StoreColor.Contrast(StoreColor.White, primary) >= StoreColor.Contrast(NearBlack, primary)
            ? StoreColor.White
            : NearBlack;

    /// <summary>
    /// El primario oscurecido de a 1 % de luminosidad hasta llegar a 4.5 sobre blanco. Se parte
    /// siempre del primario original: encadenar redondeos de byte deriva el tono.
    /// </summary>
    private static StoreColor AutoAccentText(StoreColor primary)
    {
        for (var step = 0; step <= 100; step++)
        {
            var candidate = step == 0 ? primary : primary.Darken(step / 100d);
            if (StoreColor.Contrast(candidate, StoreColor.White) >= MinimumContrast)
            {
                return candidate;
            }
        }

        return new StoreColor(0, 0, 0);
    }
}
