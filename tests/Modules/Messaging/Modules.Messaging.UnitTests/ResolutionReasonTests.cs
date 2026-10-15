using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-15 §3.2: el motivo se guarda recortado, de 1 a 60 caracteres; renombrar y archivar suben la versión; el tope
/// es de 100 vigentes por tenant.</summary>
public sealed class ResolutionReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public void CreateTrimsTheNameAndStartsAtVersionOne()
    {
        var id = Guid.CreateVersion7();

        var reason = ResolutionReason.Create(id, TenantId, "  Venta cerrada  ", Now);

        Assert.Equal(id, reason.Id);
        Assert.Equal(TenantId, reason.TenantId);
        Assert.Equal("Venta cerrada", reason.Name);
        Assert.Equal(1, reason.Version);
        Assert.Equal(Now, reason.CreatedAt);
        Assert.Equal(Now, reason.UpdatedAt);
        Assert.Null(reason.DeletedAt);
        Assert.False(reason.IsArchived);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRequired(string? name)
    {
        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.Create(Guid.CreateVersion7(), TenantId, name, Now));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameRequired, error.Code);
    }

    [Fact]
    public void SixtyCharactersFitAndSixtyOneDoNot()
    {
        var sixty = new string('a', ResolutionReason.NameMaxLength);

        Assert.Equal(sixty, ResolutionReason.Create(Guid.CreateVersion7(), TenantId, $" {sixty} ", Now).Name);
        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.Create(Guid.CreateVersion7(), TenantId, sixty + "a", Now));
        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameTooLong, error.Code);
    }

    // Review Focus 5: la spec dice sólo Trim. Dos espacios adentro son otro nombre; cambiarlo es cambiar la spec.
    [Fact]
    public void InnerSpacesAreKept() =>
        Assert.Equal("Venta  cerrada", ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Venta  cerrada", Now).Name);

    [Fact]
    public void RenameTrimsBumpsTheVersionAndARejectionLeavesItAsItWas()
    {
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Venta cerada", Now);

        reason.Rename(" Venta cerrada ", Now.AddMinutes(1));
        var error = Assert.Throws<MessagingDomainException>(() => reason.Rename(" ", Now.AddMinutes(2)));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonNameRequired, error.Code);
        Assert.Equal("Venta cerrada", reason.Name);
        Assert.Equal(2, reason.Version);
        Assert.Equal(Now.AddMinutes(1), reason.UpdatedAt);
    }

    [Fact]
    public void ArchiveStampsDeletedAtAndBumpsTheVersion()
    {
        var reason = ResolutionReason.Create(Guid.CreateVersion7(), TenantId, "Spam", Now);

        reason.Archive(Now.AddMinutes(5));

        Assert.True(reason.IsArchived);
        Assert.Equal(Now.AddMinutes(5), reason.DeletedAt);
        Assert.Equal(Now.AddMinutes(5), reason.UpdatedAt);
        Assert.Equal(2, reason.Version);
    }

    [Fact]
    public void TheHundredAndFirstIsRejected()
    {
        ResolutionReason.EnsureTenantCapacity(ResolutionReason.MaxPerTenant - 1);

        var error = Assert.Throws<MessagingDomainException>(() => ResolutionReason.EnsureTenantCapacity(ResolutionReason.MaxPerTenant));

        Assert.Equal(MessagingErrorCodes.ResolutionReasonLimitReached, error.Code);
    }
}
