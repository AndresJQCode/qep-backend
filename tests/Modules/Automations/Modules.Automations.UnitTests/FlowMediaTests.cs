using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-18: los ids de archivo que un flujo usa, para resolverlos en el editor y para saber qué archivo sigue en uso.</summary>
public sealed class FlowMediaTests
{
    private static readonly Guid Catalog = Guid.Parse("7d2f0c55-1a4e-4c1f-9b0e-3f6a2d8e9c11");
    private static readonly Guid Jingle = Guid.Parse("0b8a1e3c-5d2f-4a6b-8c9d-1e2f3a4b5c6d");

    [Fact]
    public void FileIdsOfListsEachMediaOnce()
    {
        var flow = FlowJson.Parse(Json(
            Message("m1", "image", Catalog),
            Message("m2", "audio", Jingle),
            Message("m3", "image", Catalog),
            """{ "id": "m4", "kind": "message", "title": "Texto", "text": "Hola", "media": null }"""));

        Assert.Equal([Catalog, Jingle], FlowMedia.FileIdsOf(flow));
    }

    // Lo lee el barrido sobre el jsonb guardado: sin pasar por el lector estricto, y sólo «media» de los mensajes.
    [Fact]
    public void FileIdsInReadsRawJsonAndIgnoresOtherKinds()
    {
        var json = Json(
            Message("m1", "document", Catalog),
            $$"""{ "id": "h1", "kind": "handoff", "title": "A", "media": { "kind": "image", "fileId": "{{Jingle}}" } }""",
            """{ "id": "m2", "kind": "message", "title": "Roto", "media": { "kind": "image", "fileId": "no-es-un-id" } }""",
            Message("m3", "document", Catalog));

        Assert.Equal([Catalog], FlowMedia.FileIdsIn(json));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "nodes": 7 }""")]
    [InlineData("""{ "nodes": [ 1, null, { "kind": "message", "media": "x" } ] }""")]
    public void FileIdsInOfGarbageIsEmpty(string json)
    {
        Assert.Empty(FlowMedia.FileIdsIn(json));
    }

    [Fact]
    public void TheKindsAreLowerCaseAndClosed()
    {
        Assert.Equal(["image", "video", "audio", "document"], ChatbotMediaKinds.All);
    }

    private static string Message(string id, string kind, Guid fileId) =>
        $$"""{ "id": "{{id}}", "kind": "message", "title": "M", "text": "", "urlButton": null, "media": { "kind": "{{kind}}", "fileId": "{{fileId}}" } }""";

    private static string Json(params string[] nodes) =>
        $$"""{ "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [], "nodes": [ {{string.Join(", ", nodes)}} ] }""";
}
