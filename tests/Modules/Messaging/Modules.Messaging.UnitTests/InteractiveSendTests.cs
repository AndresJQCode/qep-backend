using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 D-I1: los topes de WhatsApp para mensajes interactivos y la forma exacta del objeto <c>interactive</c> de Meta.</summary>
public sealed class InteractiveSendTests
{
    private static ListRow[] Rows(int count, int start = 0) =>
        Enumerable.Range(start, count).Select(i => new ListRow($"r{i}", $"Fila {i}")).ToArray();

    [Fact] public void ThreeButtonsOfTwentyCharactersAreValid() => Assert.Empty(InteractiveSendValidation.Validate(new ButtonsSend("¿Qué necesitas?", [new("a", new string('x', 20)), new("b", "Dos"), new("c", "Tres")])));
    [Fact] public void FourButtonsFail() => Assert.Contains("interactive.buttons.too_many", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a","1"),new("b","2"),new("c","3"),new("d","4")])));
    [Fact] public void AButtonTitleOfTwentyOneFails() => Assert.Contains("interactive.buttons.title_too_long", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", new string('x', 21))])));
    [Fact] public void ElevenRowsFail() => Assert.Contains("interactive.list.too_many_rows", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, Rows(11))])));
    [Fact] public void RowsAreCountedAcrossSections() => Assert.Contains("interactive.list.too_many_rows", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new("A", Rows(6)), new("B", Rows(5, 6))])));
    [Fact] public void DuplicatedRowIdsFail() => Assert.Contains("interactive.list.id_duplicated", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new("r","a"), new("r","b")])])));
    // Los topes que faltaban (revisión final): encabezado, pie, largo de los ids y las piezas vacías que Graph rechaza al enviar.
    [Fact] public void AHeaderOfSixtyOneFails() => Assert.Contains("interactive.header.too_long", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", "1")], Header: new string('h', 61))));
    [Fact] public void AFooterOfSixtyOneFails() => Assert.Contains("interactive.footer.too_long", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", "1")], Footer: new string('f', 61))));
    [Fact] public void AHeaderAndFooterOfSixtyAreValid() => Assert.Empty(InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", "1")], new string('h', 60), new string('f', 60))));
    [Fact] public void AButtonIdOf257Fails() => Assert.Contains("interactive.buttons.id_too_long", InteractiveSendValidation.Validate(new ButtonsSend("x", [new(new string('i', 257), "1")])));
    [Fact] public void AButtonIdOf256IsValid() => Assert.Empty(InteractiveSendValidation.Validate(new ButtonsSend("x", [new(new string('i', 256), "1")])));
    [Fact] public void AnEmptyButtonIdFails() => Assert.Contains("interactive.buttons.id_empty", InteractiveSendValidation.Validate(new ButtonsSend("x", [new(" ", "1")])));
    [Fact] public void AnEmptyButtonTitleFails() => Assert.Contains("interactive.buttons.title_empty", InteractiveSendValidation.Validate(new ButtonsSend("x", [new("a", "")])));
    [Fact] public void ARowIdOf201Fails() => Assert.Contains("interactive.list.row_id_too_long", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new(new string('r', InteractiveLimits.RowIdMax + 1), "a")])])));
    [Fact] public void ARowIdOf200IsValid() => Assert.Empty(InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new(new string('r', 200), "a")])])));
    [Fact] public void AnEmptyRowIdFails() => Assert.Contains("interactive.list.id_empty", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new("", "a")])])));
    [Fact] public void AnEmptyRowTitleFails() => Assert.Contains("interactive.list.row_title_empty", InteractiveSendValidation.Validate(new ListSend("x", "Ver", [new(null, [new("r", "  ")])])));
    [Fact] public void AnEmptyListButtonLabelFails() => Assert.Contains("interactive.list.button_empty", InteractiveSendValidation.Validate(new ListSend("x", "", [new(null, Rows(1))])));
    [Fact] public void AnEmptyCtaDisplayTextFails() => Assert.Contains("interactive.cta.display_text_empty", InteractiveSendValidation.Validate(new CtaUrlSend("x", " ", new Uri("https://app.clasic.com.co"))));
    [Fact] public void CtaUrlMustBeHttps() => Assert.Contains("interactive.cta.url_not_https", InteractiveSendValidation.Validate(new CtaUrlSend("x", "Ir", new Uri("http://app.clasic.com.co"))));

    [Fact]
    public void ButtonsMetaObjectHasTheMetaShape()
    {
        var json = InteractiveSendJson.ToMetaObject(new ButtonsSend("¿Qué necesitas?", [new("tech", "Servicio técnico")])).ToJsonString(InteractiveSendJson.DetailsOptions);
        Assert.Equal("""{"type":"button","body":{"text":"¿Qué necesitas?"},"action":{"buttons":[{"type":"reply","reply":{"id":"tech","title":"Servicio técnico"}}]}}""", json);
    }

    [Fact]
    public void ListMetaObjectOmitsAbsentHeaderFooterAndDescription()
    {
        var json = InteractiveSendJson.ToMetaObject(new ListSend("Elige", "Ver equipos", [new("Equipos", [new("1", "Calentador")])])).ToJsonString(InteractiveSendJson.DetailsOptions);
        Assert.Equal("""{"type":"list","body":{"text":"Elige"},"action":{"button":"Ver equipos","sections":[{"title":"Equipos","rows":[{"id":"1","title":"Calentador"}]}]}}""", json);
    }

    [Fact]
    public void CtaUrlMetaObjectHasTheMetaShape()
    {
        var json = InteractiveSendJson.ToMetaObject(new CtaUrlSend("No estás registrado.", "Registrarme", new Uri("https://app.clasic.com.co/registro-clientes"))).ToJsonString(InteractiveSendJson.DetailsOptions);
        Assert.Equal("""{"type":"cta_url","body":{"text":"No estás registrado."},"action":{"name":"cta_url","parameters":{"display_text":"Registrarme","url":"https://app.clasic.com.co/registro-clientes"}}}""", json);
    }
}
