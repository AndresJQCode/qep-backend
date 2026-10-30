using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

public sealed class ProfileRulesTests
{
    private static ServiceContractsCustomerRef Customer(string? phone, string? email) =>
        new(Guid.CreateVersion7(), "Panadería La Esquina", phone, email, true);

    private static ServiceContractsMemberRef Member(bool isActive, bool canManage) =>
        new(Guid.CreateVersion7(), "Ana Admin", "ana@example.com", isActive, canManage);

    [Fact]
    public void WhatsAppNeedsAPhoneAndEmailNeedsAnAddress()
    {
        var withoutPhone = Customer(null, "pan@example.com");
        var withoutEmail = Customer("+573001234567", null);

        var whatsApp = Assert.Throws<ServiceContractsDomainException>(
            () => ProfileRules.EnsureChannelIsReachable(BillingChannel.WhatsApp, withoutPhone));
        var email = Assert.Throws<ServiceContractsDomainException>(
            () => ProfileRules.EnsureChannelIsReachable(BillingChannel.Email, withoutEmail));

        Assert.Equal("billing.profile.whatsapp_requires_phone", whatsApp.Code);
        Assert.Equal("billing.profile.email_requires_address", email.Code);

        // Con el dato que el canal necesita no hay nada que decir, aunque falte el del otro.
        ProfileRules.EnsureChannelIsReachable(BillingChannel.WhatsApp, withoutEmail);
        ProfileRules.EnsureChannelIsReachable(BillingChannel.Email, withoutPhone);
    }

    [Fact]
    public void ABlankPhoneOrEmailCountsAsMissing()
    {
        Assert.Equal(
            "billing.profile.whatsapp_requires_phone",
            Assert.Throws<ServiceContractsDomainException>(
                () => ProfileRules.EnsureChannelIsReachable(BillingChannel.WhatsApp, Customer("  ", null))).Code);
        Assert.Equal(
            "billing.profile.email_requires_address",
            Assert.Throws<ServiceContractsDomainException>(
                () => ProfileRules.EnsureChannelIsReachable(BillingChannel.Email, Customer(null, ""))).Code);
    }

    [Fact]
    public void TheAssigneeMustExistBeActiveAndAbleToManage()
    {
        ProfileRules.EnsureAssigneeCanManage(Member(isActive: true, canManage: true));

        foreach (var invalid in new ServiceContractsMemberRef?[]
        {
            null,
            Member(isActive: false, canManage: true),
            Member(isActive: true, canManage: false),
        })
        {
            var error = Assert.Throws<ServiceContractsDomainException>(() => ProfileRules.EnsureAssigneeCanManage(invalid));
            Assert.Equal("billing.profile.assignee_invalid", error.Code);
        }
    }

    [Theory]
    [InlineData("WhatsApp", true)]
    [InlineData("Email", true)]
    [InlineData("whatsapp", false)]
    [InlineData("Sms", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheTwoChannelNamesParse(string? text, bool expected)
    {
        Assert.Equal(expected, ProfileRules.TryParseChannel(text, out _));
    }
}
