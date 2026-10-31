using BuildingBlocks.Application;
using Modules.ApiAccess.Application;

namespace Modules.ApiAccess.UnitTests;

public sealed class CreateApiKeyValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static readonly ApiScopeDefinition TemplatesSend = new(
        "whatsapp.templates.send", "Enviar plantillas", "Incluye consultar las plantillas aprobadas.", "WhatsApp",
        "messaging", RestrictableByConnection: true);

    private static CreateApiKeyValidator Validator() =>
        new([TemplatesSend], new FixedClock(Now));

    private static CreateApiKeyCommand Command(
        string? name = "ERP",
        IReadOnlyList<string>? scopes = null,
        IReadOnlyList<Guid>? connectionIds = null,
        DateTimeOffset? expiresAt = null) =>
        new(TenantId, name, scopes ?? ["whatsapp.templates.send"], connectionIds, expiresAt);

    private static string[] ErrorKeys(CreateApiKeyCommand command) =>
        Validator().Validate(command).Errors.Select(error => error.PropertyName).Distinct().Order().ToArray();

    [Fact]
    public void AValidCommandPasses()
    {
        var result = Validator().Validate(Command(
            connectionIds: [Guid.CreateVersion7()], expiresAt: Now.AddDays(90)));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameIsRequired(string? name) =>
        Assert.Equal(["name"], ErrorKeys(Command(name: name)));

    [Fact]
    public void TheNameLengthIsMeasuredAfterTrimming()
    {
        Assert.True(Validator().Validate(Command(name: "  " + new string('a', 80) + "  ")).IsValid);
        Assert.Equal(["name"], ErrorKeys(Command(name: new string('a', 81))));
    }

    [Fact]
    public void ScopesCannotBeNullOrEmpty()
    {
        Assert.Equal(["scopes"], ErrorKeys(Command(scopes: [])));
        Assert.Equal(["scopes"], ErrorKeys(new CreateApiKeyCommand(TenantId, "ERP", null, null, null)));
    }

    [Fact]
    public void AnUnknownOrRepeatedScopeIsRejected()
    {
        Assert.Equal(["scopes"], ErrorKeys(Command(scopes: ["no.such.scope"])));
        Assert.Equal(
            ["scopes"],
            ErrorKeys(Command(scopes: ["whatsapp.templates.send", "whatsapp.templates.send"])));
    }

    [Fact]
    public void AnEmptyConnectionListIsRejectedInsteadOfMeaningEveryNumber() =>
        Assert.Equal(["connectionIds"], ErrorKeys(Command(connectionIds: [])));

    [Fact]
    public void AnEmptyGuidInTheConnectionListIsRejected() =>
        Assert.Equal(["connectionIds"], ErrorKeys(Command(connectionIds: [Guid.Empty])));

    [Fact]
    public void ExpiryMustBeInTheFuture()
    {
        Assert.Equal(["expiresAt"], ErrorKeys(Command(expiresAt: Now)));
        Assert.Equal(["expiresAt"], ErrorKeys(Command(expiresAt: Now.AddMinutes(-1))));
        Assert.True(Validator().Validate(Command(expiresAt: Now.AddSeconds(1))).IsValid);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
