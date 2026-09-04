using Modules.Reporting.Application;

namespace Bootstrapper;

/// <summary>
/// La fila "Otros" de un ranking, compartida por los adaptadores de reporting.
///
/// Vive aparte y no copiada en cada adaptador porque la regla tiene una sutileza que se pierde al
/// duplicarla: el resto sale **por resta contra el total ya calculado**, no sumando en una
/// tercera consulta. Es lo unico que garantiza que las filas del ranking y el KPI de arriba
/// cierren exactamente, incluso con importes que no son enteros.
/// </summary>
internal static class ReportRankFolding
{
    /// <summary>
    /// Devuelve la fila de resto, o <c>null</c> cuando no hay resto que mostrar.
    ///
    /// <paramref name="countDistinctAsync"/> se invoca **solo** si el tope se lleno: con menos
    /// entidades que el tope no puede haber resto, y cada ranking se ahorra una consulta.
    /// </summary>
    public static async Task<ReportRankEntryDto?> FoldOthersAsync(
        List<ReportRankEntryDto> named,
        int rankSize,
        int totalCount,
        decimal totalAmount,
        Func<Task<int>> countDistinctAsync)
    {
        if (named.Count < rankSize)
        {
            return null;
        }

        var distinct = await countDistinctAsync();
        var remaining = distinct - named.Count;
        if (remaining <= 0)
        {
            return null;
        }

        return new ReportRankEntryDto(
            Id: null,
            Label: null,
            Secondary: null,
            remaining,
            totalCount - named.Sum(entry => entry.Count),
            totalAmount - named.Sum(entry => entry.Total));
    }
}
