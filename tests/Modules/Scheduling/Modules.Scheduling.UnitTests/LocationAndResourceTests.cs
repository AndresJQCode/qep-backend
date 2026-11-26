using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

public sealed class LocationAndResourceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALocationStartsAtVersionOneWithATrimmedName()
    {
        var location = Location.Create(LocationId.New(), Tenant, "  Chapinero  ", isMain: true, Now);

        Assert.Equal("Chapinero", location.Name);
        Assert.True(location.IsMain);
        Assert.Equal(1, location.Version);
        Assert.Equal((Now, Now), (location.CreatedAt, location.UpdatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => Location.Create(LocationId.New(), Tenant, name, isMain: false, Now));
        Assert.Throws<ArgumentException>(() =>
            Resource.Create(ResourceId.New(), Tenant, LocationId.New(), name, memberId: null, Now));
    }

    [Fact]
    public void TheMainLocationCannotBeArchived()
    {
        var main = Location.Create(LocationId.New(), Tenant, "Principal", isMain: true, Now);

        var error = Assert.Throws<SchedulingDomainException>(() => main.Archive(Now.AddHours(1)));

        Assert.Equal(SchedulingErrorCodes.MainLocationCannotBeArchived, error.Code);
        Assert.False(main.Archived);
    }

    [Fact]
    public void AnotherLocationCanBeArchivedAndRestoredAndEachChangeBumpsTheVersion()
    {
        var location = Location.Create(LocationId.New(), Tenant, "Usaquén", isMain: false, Now);

        location.Rename("Usaquén Norte", Now.AddMinutes(1));
        location.Archive(Now.AddMinutes(2));
        location.Archive(Now.AddMinutes(3));
        Assert.True(location.Archived);
        Assert.Equal(Now.AddMinutes(2), location.ArchivedAt);
        Assert.Equal(3, location.Version);

        location.Unarchive(Now.AddMinutes(4));
        Assert.False(location.Archived);
        Assert.Equal(4, location.Version);
    }

    [Fact]
    public void AResourceKeepsItsOptionalMemberLinkAndCanChangeIt()
    {
        var member = Guid.CreateVersion7();
        var resource = Resource.Create(ResourceId.New(), Tenant, LocationId.New(), " Dra. Pérez ", member, Now);

        Assert.Equal("Dra. Pérez", resource.DisplayName);
        Assert.Equal(member, resource.MemberId);

        resource.Update("Dra. Laura Pérez", memberId: null, Now.AddMinutes(1));
        Assert.Equal("Dra. Laura Pérez", resource.DisplayName);
        Assert.Null(resource.MemberId);
        Assert.Equal(2, resource.Version);
    }

    [Fact]
    public void AResourceCanBeArchivedAndRestored()
    {
        var resource = Resource.Create(ResourceId.New(), Tenant, LocationId.New(), "Silla 2", null, Now);

        resource.Archive(Now.AddMinutes(1));
        Assert.True(resource.Archived);
        resource.Unarchive(Now.AddMinutes(2));
        Assert.False(resource.Archived);
    }
}
