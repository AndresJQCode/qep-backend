using Modules.Hr.Domain;
using static Modules.Hr.UnitTests.HrTestData;

namespace Modules.Hr.UnitTests;

public sealed class OrganizationTests
{
    private static Site NewSite(string name = "Principal") =>
        Site.Create(SiteId.New(), TenantId, CompanyId, name, null, null, null, Now);

    [Fact]
    public void AWorkplaceCannotBeCreatedOnAnArchivedSite()
    {
        var site = NewSite();
        site.Archive(Now);

        var exception = Rejects(() =>
            Workplace.Create(WorkplaceId.New(), TenantId, site, "Bodega", null, ArlRiskClass.I, false, Now));

        Assert.Equal("hr.settings.site_archived", exception.Code);
    }

    [Fact]
    public void ArchivingTwiceIsRejected()
    {
        var area = Area.Create(AreaId.New(), TenantId, "Logística", Now);
        area.Archive(Now);

        Assert.Equal("hr.settings.already_archived", Rejects(() => area.Archive(Now)).Code);
    }

    [Fact]
    public void UnarchivingAnActiveOneIsRejected()
    {
        var position = Position.Create(PositionId.New(), TenantId, "Analista", Now);

        Assert.Equal("hr.settings.not_archived", Rejects(() => position.Unarchive(Now)).Code);
    }

    [Fact]
    public void NamesAreNormalizedForUniqueness()
    {
        var first = Area.Create(AreaId.New(), TenantId, "  Bodega   NORTE ", Now);
        var second = Area.Create(AreaId.New(), TenantId, "bodega norte", Now);
        var accented = Area.Create(AreaId.New(), TenantId, "Logística", Now);

        Assert.Equal(first.NormalizedName, second.NormalizedName);
        Assert.Equal("bodega norte", first.NormalizedName);
        Assert.Equal("logistica", accented.NormalizedName);
    }

    [Fact]
    public void UpdatingASiteKeepsItsCompany()
    {
        var site = NewSite();

        site.Update("Otra sede", "Cra 1", null, null, Now.AddMinutes(1));

        Assert.Equal(CompanyId, site.CompanyId);
        Assert.Equal("Otra sede", site.Name);
        Assert.Equal(2, site.Version);
    }

    [Fact]
    public void ArchivingAndRenamingBumpTheVersion()
    {
        var position = Position.Create(PositionId.New(), TenantId, "Analista", Now);

        position.Rename("Analista senior", Now.AddMinutes(1));
        position.Archive(Now.AddMinutes(2));
        position.Unarchive(Now.AddMinutes(3));

        Assert.Equal(4, position.Version);
        Assert.False(position.Archived);
    }
}
