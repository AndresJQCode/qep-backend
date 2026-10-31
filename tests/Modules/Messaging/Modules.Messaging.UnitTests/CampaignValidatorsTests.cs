using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2 y la aclaración de E3: las claves de <c>errors</c> son las del cuerpo JSON (camelCase, la anidada
/// con punto): <c>name</c>, <c>connectionId</c>, <c>template.name</c>, <c>template.language</c>, <c>parameters.&lt;clave&gt;</c>,
/// <c>audience</c>, <c>audience.tagIds</c>, <c>audience.customerIds</c>, <c>scheduledAt</c> y <c>expectedVersion</c>.</summary>
public sealed class CampaignValidatorsTests
{
    private static readonly CampaignBed Bed = new();

    private static string[] Keys(CreateCampaignCommand command)
    {
        var result = new CreateCampaignValidator(Bed.Clock).Validate(command);
        return [.. result.Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void AValidInputHasNoErrors() => Assert.Empty(Keys(Bed.CreateCommand()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsOnName(string? name) => Assert.Equal(["name"], Keys(Bed.CreateCommand(name: name)));

    [Fact]
    public void ANameOver120IsOnName() => Assert.Equal(["name"], Keys(Bed.CreateCommand(name: new string('a', 121))));

    [Fact]
    public void AMissingConnectionIsOnConnectionId() =>
        Assert.Equal(["connectionId"], Keys(Bed.CreateCommand() with { ConnectionId = null }));

    [Fact]
    public void ATemplateWithoutNameOrLanguageIsOnItsTwoKeys() =>
        Assert.Equal(["template.language", "template.name"], Keys(Bed.CreateCommand() with { TemplateName = "", TemplateLanguage = null }));

    [Fact]
    public void MissingParametersAreOnParameters() =>
        Assert.Equal(["parameters"], Keys(Bed.CreateCommand() with { Parameters = null }));

    [Fact]
    public void ATextValueThatBreaksMetasRulesIsOnItsOwnKey()
    {
        var parameters = new Dictionary<string, CampaignParameterInput?>
        {
            ["1"] = new(CampaignParameter.TextWire, "Ana\nMaría"),
            ["2"] = new(CampaignParameter.TextWire, "ok"),
        };

        Assert.Equal(["parameters.1"], Keys(Bed.CreateCommand(parameters: parameters)));
    }

    [Fact]
    public void AnUnknownSourceOrAMissingTextIsOnItsKey()
    {
        var parameters = new Dictionary<string, CampaignParameterInput?>
        {
            ["1"] = new("magic", null),
            ["2"] = new(CampaignParameter.TextWire, null),
            ["3"] = null,
        };

        Assert.Equal(["parameters.1", "parameters.2", "parameters.3"], Keys(Bed.CreateCommand(parameters: parameters)));
    }

    [Fact]
    public void TheCustomerNameNeedsNoValue() =>
        Assert.Empty(Keys(Bed.CreateCommand(parameters: new Dictionary<string, CampaignParameterInput?> { ["1"] = new(CampaignParameter.CustomerNameWire, "ignorado") })));

    [Fact]
    public void TheGreetingNeedsNoValue() =>
        Assert.Empty(Keys(Bed.CreateCommand(parameters: new Dictionary<string, CampaignParameterInput?> { ["1"] = new(CampaignParameter.GreetingWire, null) })));

    // «Buenos días» lleva un espacio y rompería el enlace de un botón, igual que el nombre del cliente.
    [Fact]
    public void AButtonLinkCannotComeFromTheGreeting() =>
        Assert.Equal(["parameters.button:0"], Keys(Bed.CreateCommand(parameters: new Dictionary<string, CampaignParameterInput?> { ["button:0"] = new(CampaignParameter.GreetingWire, null) })));

    [Fact]
    public void AnEmptyAudienceIsOnAudience() =>
        Assert.Equal(["audience"], Keys(Bed.CreateCommand(tagIds: [], customerIds: [])));

    [Fact]
    public void MoreThan500LooseCustomersAreOnTheirKey() =>
        Assert.Equal(["audience.customerIds"], Keys(Bed.CreateCommand(tagIds: [], customerIds: [.. Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7())])));

    [Fact]
    public void AnEmptyGuidInTheTagsIsOnTagIds() =>
        Assert.Equal(["audience.tagIds"], Keys(Bed.CreateCommand(tagIds: [Guid.Empty])));

    [Fact]
    public void TheScheduleIsInTheFutureAndWithin30Days()
    {
        Assert.Empty(Keys(Bed.CreateCommand(scheduledAt: Bed.Bed.Now.AddHours(1))));
        Assert.Equal(["scheduledAt"], Keys(Bed.CreateCommand(scheduledAt: Bed.Bed.Now)));
        Assert.Equal(["scheduledAt"], Keys(Bed.CreateCommand(scheduledAt: Bed.Bed.Now.AddDays(31))));
    }

    [Fact]
    public void UpdateAlsoAsksForAnExpectedVersion()
    {
        var validator = new UpdateCampaignValidator(Bed.Clock);
        var campaign = Guid.CreateVersion7();

        Assert.Empty(validator.Validate(Bed.UpdateCommand(campaign, 3)).Errors);
        Assert.Equal(["expectedVersion"], validator.Validate(Bed.UpdateCommand(campaign, null)).Errors.Select(error => error.PropertyName));
        Assert.Equal(["expectedVersion"], validator.Validate(Bed.UpdateCommand(campaign, 0)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void LaunchAndCancelAskForAnExpectedVersion()
    {
        var id = Guid.CreateVersion7();

        Assert.Empty(new LaunchCampaignValidator().Validate(new LaunchCampaignCommand(Bed.Bed.TenantId, id, 1)).Errors);
        Assert.Equal(["expectedVersion"], new LaunchCampaignValidator().Validate(new LaunchCampaignCommand(Bed.Bed.TenantId, id, null)).Errors.Select(error => error.PropertyName));
        Assert.Equal(["expectedVersion"], new CancelCampaignValidator().Validate(new CancelCampaignCommand(Bed.Bed.TenantId, id, -1)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void ThePagesAreBounded()
    {
        var list = new ListCampaignsValidator();
        Assert.Empty(list.Validate(new ListCampaignsQuery(Bed.Bed.TenantId, null, null)).Errors);
        Assert.Equal(["page", "pageSize"], list.Validate(new ListCampaignsQuery(Bed.Bed.TenantId, 0, 101)).Errors.Select(error => error.PropertyName).Order(StringComparer.Ordinal));

        var recipients = new ListCampaignRecipientsValidator();
        Assert.Empty(recipients.Validate(new ListCampaignRecipientsQuery(Bed.Bed.TenantId, Guid.CreateVersion7(), "Skipped", 1, 50)).Errors);
        Assert.Equal(["status"], recipients.Validate(new ListCampaignRecipientsQuery(Bed.Bed.TenantId, Guid.CreateVersion7(), "Nope", 1, 50)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void ThePreviewTakesAtMost500LooseCustomersAndAKnownCategoryShape()
    {
        var validator = new PreviewAudienceValidator();
        Assert.Empty(validator.Validate(new PreviewAudienceQuery(Bed.Bed.TenantId, [], [], "MARKETING")).Errors);
        Assert.Equal(["customerIds"], validator.Validate(new PreviewAudienceQuery(Bed.Bed.TenantId, [], [.. Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7())], "MARKETING")).Errors.Select(error => error.PropertyName));
        Assert.Equal(["templateCategory"], validator.Validate(new PreviewAudienceQuery(Bed.Bed.TenantId, [], [], "")).Errors.Select(error => error.PropertyName));
    }
}
