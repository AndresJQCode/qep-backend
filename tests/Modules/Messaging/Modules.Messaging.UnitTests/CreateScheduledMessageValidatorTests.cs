using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09 D5 y «Validadores»: la forma del cuerpo de <c>POST …/scheduled-messages</c>. <c>{saludo}</c> mide 13 caracteres
/// (lo más largo que puede resolverse), no los 8 que escribe la persona; por eso los casos de longitud usan un cuerpo que en bruto cabe
/// y ya resuelto no.</summary>
public sealed class CreateScheduledMessageValidatorTests
{
    private const string Token = "{saludo}";

    private static readonly CreateScheduledMessageValidator Validator = new();

    private static CreateScheduledMessageCommand Text(string? text, Guid? clientId = null, DateTimeOffset? at = null, bool useDefaults = true) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), useDefaults ? clientId ?? Guid.CreateVersion7() : clientId, useDefaults ? at ?? DateTimeOffset.UtcNow.AddHours(1) : at,
            text, null, null, null);

    private static CreateScheduledMessageCommand Media(string? fileName, string? caption = null, Guid? uploadId = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), null, uploadId ?? Guid.CreateVersion7(), fileName, caption);

    private static string[] Keys(CreateScheduledMessageCommand command) =>
        [.. Validator.Validate(command).Errors.Select(error => error.PropertyName).Distinct()];

    [Fact]
    public void AValidTextAndAValidUploadPass()
    {
        Assert.Empty(Keys(Text("Hola {saludo}")));
        Assert.Empty(Keys(Media("catalogo.jpg", "Te lo comparto")));
    }

    [Fact]
    public void TextAndUploadIdTogetherOrNeitherIsInvalid()
    {
        var both = Text("Hola") with { UploadId = Guid.CreateVersion7(), FileName = "a.pdf" };
        var neither = Text(null);

        foreach (var command in new[] { both, neither })
        {
            var result = Validator.Validate(command);
            var error = Assert.Single(result.Errors, error => error.PropertyName == "text");
            Assert.Equal("Escribe un mensaje o adjunta un archivo, no los dos.", error.ErrorMessage);
        }
    }

    [Fact]
    public void TheClientIdAndTheTimeAreRequired()
    {
        Assert.Contains("clientId", Keys(Text("Hola", clientId: Guid.Empty)));
        Assert.Contains("clientId", Keys(Text("Hola", clientId: null, useDefaults: false)));
        Assert.Contains("scheduledAt", Keys(Text("Hola", at: null, useDefaults: false) with { ClientId = Guid.CreateVersion7() }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hola\u0000")]
    public void AnEmptyOrControlTextIsInvalid(string text) => Assert.Contains("text", Keys(Text(text)));

    [Theory]
    [InlineData(4085, false)] // 4085 + {saludo} = 4093 en bruto (cabe), 4098 resuelto (no cabe)
    [InlineData(4083, true)] // 4083 + {saludo} = 4091 en bruto, 4096 resuelto: justo en el tope
    public void TheGreetingTokenCountsThirteenAgainstTheTextLimit(int bodyLength, bool valid)
    {
        var text = new string('a', bodyLength) + Token;

        Assert.Equal(valid, !Keys(Text(text)).Contains("text"));
    }

    [Fact]
    public void ATextOverTheLimitWithoutTokensIsInvalid()
    {
        Assert.Contains("text", Keys(Text(new string('a', 4097))));
        Assert.DoesNotContain("text", Keys(Text(new string('a', 4096))));
    }

    [Theory]
    [InlineData(1013, false)] // 1013 + {saludo} = 1021 en bruto, 1026 resuelto
    [InlineData(1011, true)] // 1011 + {saludo} = 1019 en bruto, 1024 resuelto
    public void TheGreetingTokenCountsThirteenAgainstTheCaptionLimit(int bodyLength, bool valid)
    {
        var caption = new string('a', bodyLength) + Token;

        Assert.Equal(valid, !Keys(Media("foto.jpg", caption)).Contains("caption"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnUploadNeedsAFileName(string? fileName) => Assert.Contains("fileName", Keys(Media(fileName)));

    [Fact]
    public void AnAudioFileTravelsWithoutCaptionButABlankOneIsIgnored()
    {
        var error = Assert.Single(Validator.Validate(Media("nota.mp3", "con texto")).Errors, error => error.PropertyName == "caption");
        Assert.Equal("Los audios se envían sin texto.", error.ErrorMessage);
        Assert.Empty(Keys(Media("nota.mp3", "   ")));
    }

    [Fact]
    public void AnEmptyUploadIdIsInvalid() => Assert.Contains("uploadId", Keys(Media("a.pdf", uploadId: Guid.Empty)));
}
