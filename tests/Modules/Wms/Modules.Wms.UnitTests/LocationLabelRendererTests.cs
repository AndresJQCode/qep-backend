using System.Text.Json;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Pdf;

namespace Modules.Wms.UnitTests;

public sealed class LocationLabelRendererTests
{
    private sealed class CapturingGateway : IWmsPdfGateway
    {
        public string? Source { get; private set; }

        public object? Data { get; private set; }

        public string? FileName { get; private set; }

        public Task<byte[]> RenderAsync(string source, object data, string fileName, CancellationToken ct)
        {
            Source = source;
            Data = data;
            FileName = fileName;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
    }

    private static readonly JsonSerializerOptions WebFormat = new(JsonSerializerDefaults.Web);

    private static readonly LocationLabel[] Labels =
    [
        new("REC-01", "BOG/RECEIVING/REC-01", "BOG"),
        new("A-01", "BOG/A/A-01", "BOG"),
    ];

    [Fact]
    public async Task TheTemplateEncodesTheCodeAsCode128()
    {
        var gateway = new CapturingGateway();

        await new LocationLabelRenderer(gateway).RenderLocationLabelsAsync(Labels, TestContext.Current.CancellationToken);

        Assert.NotNull(gateway.Source);
        Assert.Contains("@preview/tiaoma", gateway.Source);
        Assert.Contains("Code128", gateway.Source);
    }

    [Fact]
    public async Task TheDataCarriesOneEntryPerLabelInCamelCase()
    {
        var gateway = new CapturingGateway();

        await new LocationLabelRenderer(gateway).RenderLocationLabelsAsync(Labels, TestContext.Current.CancellationToken);

        // Es el contrato con la plantilla, que lee d.labels[i].code / .path / .warehouseCode.
        var json = JsonSerializer.SerializeToElement(gateway.Data, WebFormat);
        var labels = json.GetProperty("labels");
        Assert.Equal(2, labels.GetArrayLength());
        Assert.Equal("REC-01", labels[0].GetProperty("code").GetString());
        Assert.Equal("BOG/RECEIVING/REC-01", labels[0].GetProperty("path").GetString());
        Assert.Equal("BOG", labels[0].GetProperty("warehouseCode").GetString());
    }

    [Fact]
    public async Task TheFileNameCarriesTheWarehouseCode()
    {
        var gateway = new CapturingGateway();

        await new LocationLabelRenderer(gateway).RenderLocationLabelsAsync(Labels, TestContext.Current.CancellationToken);

        Assert.Equal("etiquetas-BOG.pdf", gateway.FileName);
    }

    [Fact]
    public async Task TheRenderedBytesAreTheGatewaysAnswer()
    {
        var bytes = await new LocationLabelRenderer(new CapturingGateway())
            .RenderLocationLabelsAsync(Labels, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }
}

public sealed class RenderLocationLabelsValidatorTests
{
    private static FluentValidation.Results.ValidationResult Validate(params Guid[] ids) =>
        new RenderLocationLabelsValidator().Validate(new RenderLocationLabelsCommand(Guid.NewGuid(), Guid.NewGuid(), ids));

    [Fact]
    public void OneIdPasses() => Assert.True(Validate(Guid.NewGuid()).IsValid);

    [Fact]
    public void FiveHundredIdsPass() =>
        Assert.True(Validate(Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray()).IsValid);

    [Fact]
    public void NoIdsIsInvalid() =>
        Assert.Contains(Validate().Errors, error => error.PropertyName == "LocationIds");

    [Fact]
    public void MoreThanFiveHundredIdsIsInvalid() =>
        Assert.Contains(
            Validate(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray()).Errors,
            error => error.PropertyName == "LocationIds");
}
