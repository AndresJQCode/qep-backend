using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-09 §8.1, pasos 4–8 contra Graph. El <c>client_secret</c> y el <c>code</c> van en la
/// query del canje y nunca en un log: <see cref="MetaGraphClient"/> registra sólo status y códigos.</summary>
internal sealed class MetaGraphSignupGateway(MetaGraphClient graph, IOptions<MetaAppOptions> options) : IWhatsAppSignupGateway
{
    public async Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var app = options.Value;
        var query = $"oauth/access_token?client_id={Uri.EscapeDataString(app.AppId ?? string.Empty)}"
            + $"&client_secret={Uri.EscapeDataString(app.AppSecret ?? string.Empty)}&code={Uri.EscapeDataString(code)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path(query));
        var response = await graph.SendAsync("oauth", request, accessToken: null, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<string>(null, Failure(response));
        }

        var token = ReadString(response.Body, "access_token");
        return token is null
            ? new GraphResult<string>(null, new GraphFailure((int)response.Status, null, null, "no_token"))
            : new GraphResult<string>(token, null);
    }

    public async Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, graph.Path($"{Uri.EscapeDataString(phoneNumberId)}/register"))
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin }),
        };
        var response = await graph.SendAsync("register", request, accessToken, cancellationToken);
        return response.IsSuccess ? new GraphResult<bool>(true, null) : new GraphResult<bool>(false, Failure(response));
    }

    public async Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, graph.Path($"{Uri.EscapeDataString(wabaId)}/phone_numbers?fields=id,display_phone_number,verified_name,quality_rating"));
        var response = await graph.SendAsync("phone-numbers", request, accessToken, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, Failure(response));
        }

        var numbers = new List<WabaPhoneNumber>();
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (ReadNumber(item) is { } number)
                    {
                        numbers.Add(number);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
        }

        return new GraphResult<IReadOnlyList<WabaPhoneNumber>>(numbers, null);
    }

    public async Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, graph.Path($"{Uri.EscapeDataString(wabaId)}/subscribed_apps"));
        var response = await graph.SendAsync("subscribed-apps", request, accessToken, cancellationToken);
        return response.IsSuccess ? new GraphResult<bool>(true, null) : new GraphResult<bool>(false, Failure(response));
    }

    public async Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, graph.Path($"{Uri.EscapeDataString(phoneNumberId)}?fields={WhatsAppCloudConnectionTester.Fields}"));
        var response = await graph.SendAsync("phone-number", request, accessToken, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<WabaPhoneNumber>(null, Failure(response));
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var number = ReadNumber(document.RootElement) ?? new WabaPhoneNumber(phoneNumberId, null, null, null);
            return new GraphResult<WabaPhoneNumber>(number with { Id = phoneNumberId }, null);
        }
        catch (JsonException)
        {
            // Un 200 ilegible no tumba el signup: el número existe, sólo no hay datos para mostrar.
            return new GraphResult<WabaPhoneNumber>(new WabaPhoneNumber(phoneNumberId, null, null, null), null);
        }
    }

    private static WabaPhoneNumber? ReadNumber(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(element, "id");
        return id is null
            ? null
            : new WabaPhoneNumber(id, ReadString(element, "display_phone_number"), ReadString(element, "verified_name"), ReadString(element, "quality_rating"));
    }

    private static string? ReadString(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static GraphFailure Failure(MetaGraphResponse response) =>
        new((int)response.Status, response.Error?.Code, response.Error?.Subcode, response.UnreachableReason ?? $"http_{(int)response.Status}");
}
