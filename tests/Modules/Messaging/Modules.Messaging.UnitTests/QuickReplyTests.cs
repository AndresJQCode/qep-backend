using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-12 §3.1, §3.2 y §3.7: el agregado <see cref="QuickReply"/>. El atajo se normaliza (recorte y minúsculas) y
/// cumple <c>^[a-z0-9-]{2,32}$</c>; el cuerpo se recorta y lleva de 1 a 4096 caracteres; las únicas variables son <c>{nombre_contacto}</c>,
/// <c>{asesor}</c>, <c>{empresa}</c> y <c>{saludo}</c>, más el heredado <c>{nombre}</c>, y una llave suelta es texto.</summary>
public sealed class QuickReplyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    private static QuickReply Reply(string? shortcut = "horarios", string? body = "Abrimos de 8 a 6.") =>
        QuickReply.Create(TenantId, shortcut, body, Now);

    private static string CodeOf(Action action) => Assert.Throws<MessagingDomainException>(action).Code;

    [Fact]
    public void CreateNormalizesTheShortcutAndTrimsTheBody()
    {
        var reply = Reply(shortcut: "  Horarios ", body: "  Abrimos de 8 a 6, {nombre}.  ");

        Assert.NotEqual(Guid.Empty, reply.Id);
        Assert.Equal(TenantId, reply.TenantId);
        Assert.Equal(("horarios", "Abrimos de 8 a 6, {nombre}."), (reply.Shortcut, reply.Body));
        Assert.Equal((Now, Now, 1L), (reply.CreatedAt, reply.UpdatedAt, reply.Version));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("horarios")]
    [InlineData("pago-2")]
    [InlineData("24h")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // 32
    public void AValidShortcutIsKept(string shortcut) => Assert.Equal(shortcut, Reply(shortcut: shortcut).Shortcut);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("/horarios")]
    [InlineData("hola mundo")]
    [InlineData("hola_mundo")]
    [InlineData("ñandú")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33
    public void AnInvalidShortcutIsRefused(string? shortcut) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidShortcut, CodeOf(() => Reply(shortcut: shortcut)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    public void ABlankBodyIsRefused(string? body) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidBody, CodeOf(() => Reply(body: body)));

    [Fact]
    public void TheBodyLimitCountsAfterTrimming()
    {
        Assert.Equal(4096, Reply(body: "  " + new string('a', 4096) + "  ").Body.Length);
        Assert.Equal(MessagingErrorCodes.QuickReplyInvalidBody, CodeOf(() => Reply(body: new string('a', 4097))));
    }

    [Theory]
    [InlineData("Hola {Nombre}")]
    [InlineData("Hola { nombre }")]
    [InlineData("Hola {}")]
    [InlineData("Total {precio} para {nombre}")]
    [InlineData("Hola {Nombre_Contacto}")]
    [InlineData("{Saludo}")]
    public void AnUnknownVariableIsRefused(string body) =>
        Assert.Equal(MessagingErrorCodes.QuickReplyUnknownVariable, CodeOf(() => Reply(body: body)));

    [Theory]
    [InlineData("Hola {nombre}, soy {asesor} de {empresa}.")]
    [InlineData("{saludo} {nombre_contacto}, soy {asesor} de {empresa}.")]
    [InlineData("{nombre}{nombre}")]
    [InlineData("Precio {10% off")]
    [InlineData("Cierra } antes")]
    [InlineData("{{nombre}}")]
    [InlineData("Sin variables")]
    public void LooseBracesAreText(string body) => Assert.Equal(body, Reply(body: body).Body);

    // {nombre} sigue entrando mientras dure el despliegue (un SPA viejo lo manda), pero el mensaje ya no lo ofrece.
    [Fact]
    public void TheMessageOfAnUnknownVariableOffersTheFourCurrentOnes()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => Reply(body: "{precio}"));

        Assert.Equal("'{precio}' is not a variable; use {nombre_contacto}, {asesor}, {empresa} or {saludo}.", exception.Message);
    }

    [Fact]
    public void UpdateReplacesBothFieldsBumpsTheVersionAndKeepsTheCreation()
    {
        var reply = Reply();

        reply.Update(" Horarios ", "Abrimos de 7 a 7.", Later);

        Assert.Equal(("horarios", "Abrimos de 7 a 7."), (reply.Shortcut, reply.Body));
        Assert.Equal((Now, Later, 2L), (reply.CreatedAt, reply.UpdatedAt, reply.Version));
    }

    [Fact]
    public void ARefusedUpdateLeavesTheReplyAsItWas()
    {
        var reply = Reply();

        Assert.Equal(MessagingErrorCodes.QuickReplyUnknownVariable, CodeOf(() => reply.Update("otro", "Hola {cliente}", Later)));

        Assert.Equal(("horarios", "Abrimos de 8 a 6.", Now, 1L), (reply.Shortcut, reply.Body, reply.UpdatedAt, reply.Version));
    }

    [Fact]
    public void TheCodesAreTheOnesOfTheContract()
    {
        Assert.Equal("messaging.quick_reply.invalid_shortcut", MessagingErrorCodes.QuickReplyInvalidShortcut);
        Assert.Equal("messaging.quick_reply.invalid_body", MessagingErrorCodes.QuickReplyInvalidBody);
        Assert.Equal("messaging.quick_reply.unknown_variable", MessagingErrorCodes.QuickReplyUnknownVariable);
        Assert.Equal("messaging.quick_reply.shortcut_taken", MessagingErrorCodes.QuickReplyShortcutTaken);
        Assert.Equal("messaging.quick_reply.not_found", MessagingErrorCodes.QuickReplyNotFound);
    }
}
