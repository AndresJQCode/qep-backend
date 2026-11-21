using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class HrSettingsIndexTests
{
    private static readonly Guid TenantId = HrTestData.TenantId;
    private static readonly DateTimeOffset Now = HrTestData.Now;

    private static readonly HrCompanyRef Qep = new(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), "Comercializadora QEP S.A.S.", "900123456", true);
    private static readonly HrCompanyRef North = new(Guid.Parse("01900000-0000-7000-8000-0000000000c2"), "Servicios del Norte S.A.S.", "800555111", true);

    private static Site NewSite(HrCompanyRef company, string name) =>
        Site.Create(SiteId.New(), TenantId, company.Id, name, null, null, null, Now);

    private static Workplace NewWorkplace(Site site, string name) =>
        Workplace.Create(WorkplaceId.New(), TenantId, site, name, null, ArlRiskClass.III, false, Now);

    private static HrSettingsIndex Build(
        IReadOnlyList<Site> sites,
        IReadOnlyList<Workplace> workplaces,
        IReadOnlyList<HrCompanyRef>? companies = null,
        IReadOnlyList<Area>? areas = null,
        IReadOnlyList<Position>? positions = null) =>
        new(new HrSettingsSnapshot(sites, workplaces, areas ?? [], positions ?? [], new HashSet<Guid>()), companies ?? [Qep, North]);

    [Fact]
    public void AWorkplaceMatchesWithItsSite()
    {
        var main = NewSite(Qep, "Principal");
        var warehouse = NewWorkplace(main, "Bodega");
        var index = Build([main], [warehouse]);

        var match = index.FindWorkplace("bodega - principal", Qep.Id);

        Assert.Equal(HrWorkplaceOutcome.Found, match.Outcome);
        Assert.Equal(warehouse.Id, match.Workplace!.Id);
        Assert.Equal(["Bodega - Principal"], index.WorkplaceLabels);
    }

    [Fact]
    public void ABareWorkplaceNameDoesNotMatchEvenWhenUnique()
    {
        var main = NewSite(Qep, "Principal");
        var warehouse = NewWorkplace(main, "Bodega");
        var index = Build([main], [warehouse]);

        Assert.Equal(HrWorkplaceOutcome.NotFound, index.FindWorkplace("BODEGA", Qep.Id).Outcome);
        Assert.Equal(HrWorkplaceOutcome.Found, index.FindWorkplace("BODEGA - PRINCIPAL", Qep.Id).Outcome);
    }

    [Fact]
    public void ARepeatedWorkplaceNameNeedsTheSite()
    {
        var main = NewSite(Qep, "Principal");
        var annex = NewSite(Qep, "Anexo");
        var first = NewWorkplace(main, "Bodega");
        var second = NewWorkplace(annex, "Bodega");
        var index = Build([main, annex], [first, second]);

        Assert.Equal(HrWorkplaceOutcome.NotFound, index.FindWorkplace("Bodega", Qep.Id).Outcome);
        Assert.Equal(second.Id, index.FindWorkplace("Bodega - Anexo", Qep.Id).Workplace!.Id);
    }

    [Fact]
    public void ArchivedSettingsAreNotOffered()
    {
        var main = NewSite(Qep, "Principal");
        var archivedSite = NewSite(Qep, "Vieja");
        var underArchivedSite = NewWorkplace(archivedSite, "Depósito");
        archivedSite.Archive(Now);
        var warehouse = NewWorkplace(main, "Bodega");
        var archivedWorkplace = NewWorkplace(main, "Taller");
        archivedWorkplace.Archive(Now);
        var activePosition = Position.Create(PositionId.New(), TenantId, "Analista", Now);
        var archivedPosition = Position.Create(PositionId.New(), TenantId, "Auxiliar", Now);
        archivedPosition.Archive(Now);
        var activeArea = Area.Create(AreaId.New(), TenantId, "Operaciones", Now);
        var archivedArea = Area.Create(AreaId.New(), TenantId, "Ventas", Now);
        archivedArea.Archive(Now);
        var index = Build(
            [main, archivedSite], [warehouse, archivedWorkplace, underArchivedSite],
            areas: [activeArea, archivedArea], positions: [activePosition, archivedPosition]);

        Assert.Equal(["Bodega - Principal"], index.WorkplaceLabels);
        Assert.Equal(HrWorkplaceOutcome.NotFound, index.FindWorkplace("Taller - Principal", Qep.Id).Outcome);
        Assert.Equal(HrWorkplaceOutcome.NotFound, index.FindWorkplace("Depósito - Vieja", Qep.Id).Outcome);
        Assert.Equal(["Analista"], index.PositionLabels);
        Assert.Null(index.FindPosition("Auxiliar"));
        Assert.Equal(activePosition.Id, index.FindPosition("analista"));
        Assert.Equal(["Operaciones"], index.AreaLabels);
        Assert.Null(index.FindArea("Ventas"));
        Assert.Equal(activeArea.Id, index.FindArea("OPERACIONES"));
    }

    [Fact]
    public void ACompanyMatchesByNameAndTaxId()
    {
        var index = Build([], []);

        Assert.Equal(Qep.Id, index.FindCompany("comercializadora qep s.a.s. - 900123456")!.Id);
        Assert.Null(index.FindCompany("Comercializadora QEP S.A.S."));
        Assert.Equal(["Comercializadora QEP S.A.S. - 900123456", "Servicios del Norte S.A.S. - 800555111"], index.CompanyLabels);
    }

    [Fact]
    public void AnInactiveCompanyIsNotOffered()
    {
        var index = Build([], [], companies: [Qep, North with { IsActive = false }]);

        Assert.Equal(["Comercializadora QEP S.A.S. - 900123456"], index.CompanyLabels);
        Assert.Null(index.FindCompany("Servicios del Norte S.A.S. - 800555111"));
    }

    [Fact]
    public void WithSeveralCompaniesThereIsNoSingle()
    {
        Assert.Null(Build([], []).SingleCompany);
        Assert.Equal(Qep.Id, Build([], [], companies: [Qep]).SingleCompany!.Id);
    }

    [Fact]
    public void AWorkplaceOfAnotherCompanyIsOtherCompany()
    {
        var main = NewSite(Qep, "Principal");
        var north = NewSite(North, "Norte");
        var index = Build([main, north], [NewWorkplace(main, "Bodega"), NewWorkplace(north, "Planta")]);

        var match = index.FindWorkplace("Planta - Norte", Qep.Id);

        Assert.Equal(HrWorkplaceOutcome.OtherCompany, match.Outcome);
        Assert.Null(match.Workplace);
        Assert.Equal(HrWorkplaceOutcome.Found, index.FindWorkplace("Planta - Norte", North.Id).Outcome);
    }

    [Fact]
    public void ASameNamedWorkplaceResolvesInsideTheRowCompany()
    {
        var main = NewSite(Qep, "Principal");
        var north = NewSite(North, "Norte");
        var qepWarehouse = NewWorkplace(main, "Bodega");
        var northWarehouse = NewWorkplace(north, "Bodega");
        var index = Build([main, north], [qepWarehouse, northWarehouse]);

        Assert.Equal(qepWarehouse.Id, index.FindWorkplace("Bodega - Principal", Qep.Id).Workplace!.Id);
        Assert.Equal(northWarehouse.Id, index.FindWorkplace("Bodega - Norte", North.Id).Workplace!.Id);
        Assert.Equal(HrWorkplaceOutcome.NotFound, index.FindWorkplace("Bodega", Qep.Id).Outcome);
    }
}
