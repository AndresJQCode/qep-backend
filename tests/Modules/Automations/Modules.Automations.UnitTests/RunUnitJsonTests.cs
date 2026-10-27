using System.Text;
using System.Text.Json.Nodes;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-25 D-E8: el JSON de la fila de la unidad y sus topes por valor y por fila.</summary>
public sealed class RunUnitJsonTests
{
    private static readonly IReadOnlySet<string> NoAttachments = new HashSet<string>();

    private static RunUnitStep Step(int step, params StepWrite[] writes) =>
        new(new StepRecord(step, StepSeqs.Entry, $"n{step}", "OpenQuestion", "Answered", writes, [], false, null, null), null, []);

    private static JsonValue Text(int length) => JsonValue.Create(new string('a', length));

    [Fact]
    public void AValueOver8KbIsTruncated()
    {
        var json = RunUnitJson.Serialize([Step(1, new StepWrite("x", Text(9214)))], NoAttachments);

        var write = JsonNode.Parse(json)![0]!["writes"]![0]!;
        Assert.True(write["truncated"]!.GetValue<bool>());
        Assert.Equal(9216, write["bytes"]!.GetValue<int>());
        Assert.Null(write["value"]);
        Assert.Equal("x", write["name"]!.GetValue<string>());
    }

    [Fact]
    public void ARowOver256KbDropsTheLargestValuesFirst()
    {
        var writes = new List<StepWrite>();
        for (var i = 0; i < 40; i++)
        {
            writes.Add(new StepWrite($"v{i}", Text(7 * 1024 - 2 - i)));
        }

        writes.Add(new StepWrite("small", Text(10)));
        var json = RunUnitJson.Serialize([Step(1, writes.ToArray())], NoAttachments);

        Assert.True(Encoding.UTF8.GetByteCount(json) <= RunUnitJson.RowMaxBytes);
        var row = RunUnitJson.Parse(json).Single();
        var dropped = row.Writes.Where(w => w.Truncated).ToList();
        Assert.NotEmpty(dropped);
        Assert.All(dropped, w => Assert.NotNull(w.Bytes));
        Assert.NotNull(row.Writes.Single(w => w.Name == "small").Value);
        // El más grande (v0) cae antes que el más chico de los grandes (v39).
        Assert.True(row.Writes.Single(w => w.Name == "v0").Truncated);
        Assert.False(row.Writes.Single(w => w.Name == "v39").Truncated);
    }

    [Fact]
    public void AnAttachmentUrlNeverIsStored()
    {
        var attachment = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(), ["fileName"] = "f.pdf", ["mimeType"] = "application/pdf", ["stored"] = true,
            ["url"] = "https://r2/secret?sig=1",
        };
        var mapped = new JsonObject { ["url"] = "https://api/x" };

        var json = RunUnitJson.Serialize(
            [Step(1, new StepWrite("factura", attachment), new StepWrite("otra", mapped))], new HashSet<string> { "factura" });

        var writes = JsonNode.Parse(json)![0]!["writes"]!;
        Assert.Null(writes[0]!["value"]!["url"]);
        Assert.Equal("f.pdf", writes[0]!["value"]!["fileName"]!.GetValue<string>());
        Assert.Equal("https://api/x", writes[1]!["value"]!["url"]!.GetValue<string>());
        Assert.NotNull(attachment["url"]); // el original no se muta
    }

    [Fact]
    public void TruncatedBytesIsTheRealUtf8SizeOfTheUnescapedCompactJson()
    {
        var options = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var big = JsonValue.Create(new string('á', 5000) + "😀")!;

        var json = RunUnitJson.Serialize([Step(1, new StepWrite("x", big), new StepWrite("s", JsonValue.Create("Cómo estás 😀")))], NoAttachments);

        var writes = JsonNode.Parse(json)![0]!["writes"]!;
        Assert.Equal(Encoding.UTF8.GetByteCount(big.ToJsonString(options)), writes[0]!["bytes"]!.GetValue<int>());
        Assert.Equal("Cómo estás 😀", writes[1]!["value"]!.GetValue<string>());
        // Las tildes no se escapan; el emoji (fuera del BMP) sí, y el tamaño medido lo cuenta tal como se guarda.
        Assert.Contains("Cómo estás", json);
    }

    [Fact]
    public void RoundTrips()
    {
        var m = Guid.NewGuid();
        var c = Guid.NewGuid();
        var record = new StepRecord(3, StepSeqs.Exit, "q1", "OpenQuestion", "Answered", [new StepWrite("doc", JsonValue.Create("123"))], [0], true, "m0", "next");
        var step = new RunUnitStep(record, m, [new RunStepSend("Text", c, 4)]);

        var row = RunUnitJson.Parse(RunUnitJson.Serialize([step], NoAttachments)).Single();

        Assert.Equal(3, row.Step);
        Assert.Equal(StepSeqs.Exit, row.Seq);
        Assert.Equal("q1", row.NodeId);
        Assert.Equal("m0", row.From);
        Assert.Equal("next", row.Via);
        Assert.True(row.Reset);
        Assert.Equal(m, row.InputMessageId);
        Assert.Equal(new RunStepSend("Text", c, 4), Assert.Single(row.Sends));
        Assert.Equal("123", Assert.Single(row.Writes).Value!.GetValue<string>());
    }
}
