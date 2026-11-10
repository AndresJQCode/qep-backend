using System.Net;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Domain;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Store no tiene rama propia en el manejador central: el status lo decide el tipo de excepción. Esta prueba fija, con el manejador
/// real de la aplicación, un representante de cada tipo que la tienda lanza, con la misma excepción que lanza su código. Las rutas que
/// las disparan las prueban <see cref="StoreSettingsApiTests"/>, <see cref="StoreInboxApiTests"/>, <see cref="StoreTakeApiTests"/>
/// y <see cref="PublicOrderRequestApiTests"/>.
/// </summary>
public sealed class StoreErrorMappingApiTests
{
    [Fact]
    public async Task StoreErrorsMapToTheirStatus()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        (Exception Thrown, string Code, HttpStatusCode Status)[] cases =
        [
            // UpdateStoreSettings.cs:157 y StoreUnitOfWork.cs:61.
            (new ResourceConflictException(StoreErrorCodes.SlugTaken, "taken"), StoreErrorCodes.SlugTaken, HttpStatusCode.Conflict),
            // TakeOrderRequest.cs:63.
            (new ResourceConflictException(StoreErrorCodes.NotNew, "not new"), StoreErrorCodes.NotNew, HttpStatusCode.Conflict),
            // SubmitOrderRequest.cs:167.
            (new StoreDomainException(StoreErrorCodes.ConsentRequired, "consent"), StoreErrorCodes.ConsentRequired, HttpStatusCode.UnprocessableEntity),
            // StorefrontResolver.cs:62.
            (new StoreDomainException(StoreErrorCodes.PublicCurrencyInvalid, "currency"), StoreErrorCodes.PublicCurrencyInvalid, HttpStatusCode.UnprocessableEntity),
            // TakeOrderRequest.cs:71.
            (new StoreDomainException(StoreErrorCodes.CurrencyNotInStore, "currency"), StoreErrorCodes.CurrencyNotInStore, HttpStatusCode.UnprocessableEntity),
            // UpdateStoreSettings.cs: el asesor de la tienda no es un miembro activo del tenant.
            (new StoreDomainException(StoreErrorCodes.AdvisorNotFound, "advisor"), StoreErrorCodes.AdvisorNotFound, HttpStatusCode.UnprocessableEntity),
            // StoreNotFound.cs:11.
            (new ResourceNotFoundException(StoreErrorCodes.OrderRequestNotFound, "missing"), StoreErrorCodes.OrderRequestNotFound, HttpStatusCode.NotFound),
            // CloudflareTurnstileVerifier.cs:148.
            (new ServiceUnavailableException(StoreErrorCodes.CaptchaUnavailable, "down"), StoreErrorCodes.CaptchaUnavailable, HttpStatusCode.ServiceUnavailable),
        ];

        foreach (var (thrown, code, status) in cases)
        {
            var (actualStatus, body) = await HandleAsync(fixture, thrown);

            Assert.True(actualStatus == (int)status, $"{code}: {actualStatus} {body.ToJsonString()}");
            Assert.Equal(code, (string)body["code"]!);
        }
    }

    private static async Task<(int Status, JsonNode Body)> HandleAsync(StoreSettingsFixture fixture, Exception exception)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        using var stream = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Path = "/api/v1/public/stores/demo.lulocrm.com/order-requests";
        context.Response.Body = stream;
        var handled = false;
        foreach (var handler in scope.ServiceProvider.GetServices<IExceptionHandler>())
        {
            if (await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken))
            {
                handled = true;
                break;
            }
        }

        Assert.True(handled, $"Nothing handled {exception.GetType().Name}.");
        stream.Position = 0;
        return (context.Response.StatusCode, JsonNode.Parse(stream)!);
    }
}
