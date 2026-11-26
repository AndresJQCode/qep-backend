using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public enum HrWorkplaceOutcome
{
    Found,
    NotFound,
    OtherCompany,
}

/// <summary><c>Workplace</c> sólo viene con <see cref="HrWorkplaceOutcome.Found"/>.</summary>
public readonly record struct HrWorkplaceMatch(HrWorkplaceOutcome Outcome, Workplace? Workplace);

/// <summary>
/// La configuración del tenant armada una vez por request para resolver las columnas de la importación por texto: empresa, cargo,
/// área y centro de trabajo. Sólo lo <b>no archivado</b> se ofrece (un centro cuenta si su sede tampoco lo está) y sólo las empresas
/// activas (D-12: una empresa inactiva no recibe vínculos nuevos).
/// </summary>
public sealed class HrSettingsIndex
{
    private const string Separator = " - ";

    private readonly List<(HrCompanyRef Company, string Key)> _companies;
    private readonly List<(Position Position, string Key)> _positions;
    private readonly List<(Area Area, string Key)> _areas;
    private readonly List<WorkplaceEntry> _workplaces;

    private sealed record WorkplaceEntry(Workplace Workplace, Guid CompanyId, string FullKey, string Label);

    public HrSettingsIndex(HrSettingsSnapshot settings, IReadOnlyList<HrCompanyRef> activeCompanies)
    {
        var active = activeCompanies.Where(company => company.IsActive).ToList();
        _companies = active
            .OrderBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(company => company.Id)
            .Select(company => (company, Fold($"{company.Name}{Separator}{company.TaxId}")))
            .ToList();
        _positions = settings.Positions
            .Where(position => !position.Archived)
            .OrderBy(position => position.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(position => position.Id.Value)
            .Select(position => (position, Fold(position.Name)))
            .ToList();
        _areas = settings.Areas
            .Where(area => !area.Archived)
            .OrderBy(area => area.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(area => area.Id.Value)
            .Select(area => (area, Fold(area.Name)))
            .ToList();

        var sites = settings.Sites.Where(site => !site.Archived).ToDictionary(site => site.Id);
        _workplaces = settings.Workplaces
            .Where(workplace => !workplace.Archived && sites.ContainsKey(workplace.SiteId))
            .Select(workplace =>
            {
                var site = sites[workplace.SiteId];
                var label = $"{workplace.Name}{Separator}{site.Name}";
                return new WorkplaceEntry(workplace, site.CompanyId, Fold(label), label);
            })
            .ToList();
    }

    /// <summary>La única empresa activa, o <c>null</c> si hay varias (o ninguna): la columna <c>EMPRESA</c> vacía sólo se resuelve con una.</summary>
    public HrCompanyRef? SingleCompany => _companies.Count == 1 ? _companies[0].Company : null;

    public IReadOnlyList<string> CompanyLabels =>
        _companies.Select(entry => $"{entry.Company.Name}{Separator}{entry.Company.TaxId}").ToList();

    public IReadOnlyList<string> PositionLabels => _positions.Select(entry => entry.Position.Name).ToList();

    public IReadOnlyList<string> AreaLabels => _areas.Select(entry => entry.Area.Name).ToList();

    /// <summary>«centro - sede» de los centros de una empresa activa, por centro y sede.</summary>
    public IReadOnlyList<string> WorkplaceLabels => _workplaces
        .Where(entry => _companies.Any(company => company.Company.Id == entry.CompanyId))
        .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Workplace.Id.Value)
        .Select(entry => entry.Label)
        .ToList();

    public HrCompanyRef? FindCompany(string text) =>
        Fold(text) is { Length: > 0 } key ? _companies.FirstOrDefault(entry => entry.Key == key).Company : null;

    public PositionId? FindPosition(string text) =>
        Fold(text) is { Length: > 0 } key && _positions.FirstOrDefault(entry => entry.Key == key) is { Position: { } position }
            ? position.Id
            : null;

    public AreaId? FindArea(string text) =>
        Fold(text) is { Length: > 0 } key && _areas.FirstOrDefault(entry => entry.Key == key) is { Area: { } area }
            ? area.Id
            : null;

    public string? PositionName(PositionId id) =>
        _positions.FirstOrDefault(entry => entry.Position.Id == id).Position?.Name;

    /// <summary>El texto que la vista previa muestra del centro: «centro - sede».</summary>
    public string WorkplaceLabel(Workplace workplace) =>
        _workplaces.FirstOrDefault(entry => entry.Workplace.Id == workplace.Id)?.Label ?? workplace.Name;

    /// <summary>
    /// Un centro se busca <b>entre los de las sedes de <paramref name="companyId"/></b> sólo por «centro - sede» (decisión del
    /// owner: el nombre del centro sólo es único por sede, así que el nombre solo no se acepta). Un centro que
    /// existe pero sólo en una sede de otra empresa es <see cref="HrWorkplaceOutcome.OtherCompany"/>.
    /// </summary>
    public HrWorkplaceMatch FindWorkplace(string text, Guid companyId)
    {
        if (Fold(text) is not { Length: > 0 } key)
        {
            return new HrWorkplaceMatch(HrWorkplaceOutcome.NotFound, null);
        }

        var own = _workplaces.Where(entry => entry.CompanyId == companyId).ToList();
        if (Resolve(own, key) is { } found)
        {
            return new HrWorkplaceMatch(HrWorkplaceOutcome.Found, found.Workplace);
        }

        var others = _workplaces.Where(entry => entry.CompanyId != companyId).ToList();
        return Resolve(others, key) is not null
            ? new HrWorkplaceMatch(HrWorkplaceOutcome.OtherCompany, null)
            : new HrWorkplaceMatch(HrWorkplaceOutcome.NotFound, null);
    }

    private static WorkplaceEntry? Resolve(List<WorkplaceEntry> candidates, string key)
    {
        var byFullKey = candidates.Where(entry => entry.FullKey == key).ToList();
        return byFullKey.Count == 1 ? byFullKey[0] : null;
    }

    private static string Fold(string text) => HrText.Fold(text) ?? string.Empty;
}
