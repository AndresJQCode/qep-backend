using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantChangeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AModuleChangeFromNoRowKeepsTheTextOfTheColumn()
    {
        var tenantId = TenantId.New();
        var batchId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();

        var change = TenantChange.ForModule(
            tenantId, batchId, TenantModuleKeys.Pos, null, TenantModuleStatus.Active, ChangeReason.Contract, "  ", actor, At);

        Assert.NotEqual(Guid.Empty, change.Id);
        Assert.Equal(tenantId, change.TenantId);
        Assert.Equal(batchId, change.BatchId);
        Assert.Equal(TenantChangeKind.Module, change.Kind);
        Assert.Same(TenantModuleKeys.Pos, change.ModuleKey);
        Assert.Null(change.FromStatus);              // NULL = la fila no existía
        Assert.Equal("active", change.ToStatus);
        Assert.Null(change.Note);                    // sólo espacios se guarda null (Review Focus 5)
        Assert.Equal(actor, change.ActorUserId);
        Assert.Equal(At, change.OccurredAt);
    }

    [Fact]
    public void ATenantStatusChangeUsesTheEnumNameAndNoModule()
    {
        var change = TenantChange.ForTenantStatus(
            TenantId.New(), Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended,
            ChangeReason.Nonpayment, " Factura de septiembre ", Guid.CreateVersion7(), At);

        Assert.Equal(TenantChangeKind.TenantStatus, change.Kind);
        Assert.Null(change.ModuleKey);
        Assert.Equal("Active", change.FromStatus);
        Assert.Equal("Suspended", change.ToStatus);
        Assert.Equal("Factura de septiembre", change.Note);
    }
}
