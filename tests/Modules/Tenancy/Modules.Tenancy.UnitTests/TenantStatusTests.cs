using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4: Suspend/Reactivate, motivos por dirección y versión.</summary>
public sealed class TenantStatusTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = CreatedAt.AddHours(1);

    private static Tenant NewTenant() => Tenant.Create(
        TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO", "America/Bogota", "yyyy-MM-dd",
        MembershipId.New(), CreatedAt);

    [Theory]
    [InlineData(ChangeReason.Nonpayment)]
    [InlineData(ChangeReason.Cancellation)]
    public void SuspendMovesToSuspendedBumpsVersionAndRaisesNoEvent(ChangeReason reason)
    {
        var tenant = NewTenant();

        tenant.Suspend(reason, Later);

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(2, tenant.Version);
        Assert.Equal(Later, tenant.UpdatedAt);
        Assert.Empty(tenant.DomainEvents);   // OutboxWriter lanza ante un evento sin mapear (§4)
    }

    [Theory]
    [InlineData(ChangeReason.Contract)]
    [InlineData(ChangeReason.Courtesy)]
    [InlineData(ChangeReason.Correction)]
    public void SuspendRejectsAReasonOfTheOtherDirection(ChangeReason reason) =>
        Assert.Equal("tenancy.tenant.reason_not_allowed",
            Assert.Throws<TenantDomainException>(() => NewTenant().Suspend(reason, Later)).Code);

    [Fact]
    public void SuspendingTwiceIsAlreadyInactive()
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.already_inactive",
            Assert.Throws<TenantDomainException>(() => tenant.Suspend(ChangeReason.Nonpayment, Later)).Code);
    }

    [Theory]
    [InlineData(ChangeReason.Contract)]
    [InlineData(ChangeReason.Courtesy)]
    [InlineData(ChangeReason.Correction)]
    public void ReactivateReturnsToActive(ChangeReason reason)
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        tenant.Reactivate(reason, Later.AddHours(1));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(3, tenant.Version);
    }

    [Theory]
    [InlineData(ChangeReason.Nonpayment)]
    [InlineData(ChangeReason.Cancellation)]
    public void ReactivateRejectsAReasonOfTheOtherDirection(ChangeReason reason)
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.reason_not_allowed",
            Assert.Throws<TenantDomainException>(() => tenant.Reactivate(reason, Later)).Code);
    }

    // Decisión P5 del plan: nadie asigna hoy estos estados, pero si llegaran el mensaje no puede hablar de
    // ajustes, que es lo que dice EnsureActive.
    [Theory]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Failed)]
    [InlineData(TenantStatus.Decommissioning)]
    [InlineData(TenantStatus.Decommissioned)]
    public void SuspendOrReactivateFromAnotherStatusIsNotActiveWithItsOwnMessage(TenantStatus status)
    {
        var tenant = NewTenant();
        typeof(Tenant).GetProperty(nameof(Tenant.Status))!.SetValue(tenant, status);

        var suspend = Assert.Throws<TenantDomainException>(() => tenant.Suspend(ChangeReason.Nonpayment, Later));
        var reactivate = Assert.Throws<TenantDomainException>(() => tenant.Reactivate(ChangeReason.Contract, Later));

        Assert.Equal(("tenancy.tenant.not_active", "Only an active tenant can be suspended."), (suspend.Code, suspend.Message));
        Assert.Equal(("tenancy.tenant.not_active", "Only a suspended tenant can be reactivated."), (reactivate.Code, reactivate.Message));
    }

    [Fact]
    public void ReactivatingAnActiveTenantIsAlreadyActive() =>
        Assert.Equal("tenancy.tenant.already_active",
            Assert.Throws<TenantDomainException>(() => NewTenant().Reactivate(ChangeReason.Contract, Later)).Code);

    // EnsureActive ya existía (Tenant.cs:211-218): un tenant inactivo no edita sus ajustes.
    [Fact]
    public void ASuspendedTenantCannotUpdateItsSettings()
    {
        var tenant = NewTenant();
        tenant.Suspend(ChangeReason.Nonpayment, Later);

        Assert.Equal("tenancy.tenant.not_active", Assert.Throws<TenantDomainException>(() =>
            tenant.UpdateSettings("Otro", "es-CO", "America/Bogota", "yyyy-MM-dd", Later)).Code);
    }
}
