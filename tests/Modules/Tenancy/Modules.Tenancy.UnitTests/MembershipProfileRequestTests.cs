using System.Text.Json;
using FluentValidation;
using Modules.Tenancy.Api;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// The phone of a profile PUT has three states, and only the body tells them apart: an old frontend never sends it (keep), a new
/// one sends <c>null</c> to clear and text to set.
/// </summary>
public sealed class MembershipProfileRequestTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AbsentPhoneIsKeep()
    {
        var request = JsonSerializer.Deserialize<MembershipProfileUpdateRequest>("""{"displayName":"Ana"}""", Web)!;

        Assert.Equal(PhoneChange.Keep, PhoneInput.ToChange(request.Phone));
    }

    [Fact]
    public void NullPhoneClears()
    {
        var request = JsonSerializer.Deserialize<MembershipProfileUpdateRequest>(
            """{"displayName":"Ana","phone":null}""", Web)!;

        var change = PhoneInput.ToChange(request.Phone);

        Assert.True(change.Provided);
        Assert.Null(change.Value);
    }

    [Fact]
    public void TextPhoneSets()
    {
        var request = JsonSerializer.Deserialize<MembershipProfileUpdateRequest>(
            """{"displayName":"Ana","phone":"+573001234567"}""", Web)!;

        var change = PhoneInput.ToChange(request.Phone);

        Assert.True(change.Provided);
        Assert.Equal("+573001234567", change.Value);
    }

    [Theory]
    [InlineData("""{"displayName":"Ana","phone":3001234567}""")]
    [InlineData("""{"displayName":"Ana","phone":{"a":1}}""")]
    [InlineData("""{"displayName":"Ana","phone":"3001234567"}""")]
    public async Task APhoneThatIsNotE164FailsOnThePhoneField(string body)
    {
        var request = JsonSerializer.Deserialize<MembershipProfileUpdateRequest>(body, Web)!;
        var command = new UpdateMemberProfileCommand(
            TenantId.New(), MembershipId.New(), "Ana", null, PhoneInput.ToChange(request.Phone), 1, "corr");

        var result = await new UpdateMemberProfileValidator().ValidateAsync(
            command, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "Phone");
    }

    [Fact]
    public async Task KeepAndNullAreValid()
    {
        var validator = new UpdateMemberProfileValidator();
        foreach (var phone in new[] { PhoneChange.Keep, PhoneChange.Set(null), PhoneChange.Set("+573001234567") })
        {
            var result = await validator.ValidateAsync(
                new UpdateMemberProfileCommand(TenantId.New(), MembershipId.New(), "Ana", null, phone, 1, "corr"),
                TestContext.Current.CancellationToken);

            Assert.True(result.IsValid);
        }
    }
}
