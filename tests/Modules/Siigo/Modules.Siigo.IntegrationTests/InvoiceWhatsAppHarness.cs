using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Lo que necesitan las pruebas de «enviar la factura por WhatsApp» (spec 2026-11-21): canales, sender y almacenamiento de mentira, sólo del
/// host que los pide. Ninguna prueba llama a Meta, a Zenvia ni a R2: el PDF, la URL pública y el teléfono se quedan en los dobles.
/// </summary>
internal static class InvoiceWhatsAppHarness
{
    /// <summary>El PDF que «entrega» Siigo: una frase sentinela entre las marcas de un PDF, para buscarla en logs, auditoría y respuestas.</summary>
    public static readonly byte[] PdfBytes = "%PDF-1.4 invoice-pdf-sentinel-7c41"u8.ToArray();

    public static readonly string PdfBase64 = Convert.ToBase64String(PdfBytes);

    public static readonly MetaWhatsAppChannel Meta = new(Guid.Parse("01900000-0000-7000-8000-00000000be7a"), "Meta de prueba", "573001112233");

    public static readonly ZenviaWhatsAppChannel Zenvia = new(
        Guid.Parse("01900000-0000-7000-8000-00000000c0de"), "Zenvia de prueba", "token-de-prueba", "570000000000",
        "d0e79149-ed61-4784-9883-d86807f46ce5", "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", InvoiceTemplateId: "7f3c2a10-1b2c-4d5e-8f60-a1b2c3d4e5f6");

    /// <summary>Un host derivado del del mundo (misma base y mismo doble de Siigo) con los canales dados y los dobles de envío.</summary>
    public static (WebApplicationFactory<Program> Factory, InvoiceWhatsAppDoubles Doubles) Host(
        InvoicingWorld world, params WhatsAppChannel[] channels)
    {
        var doubles = new InvoiceWhatsAppDoubles(channels);
        var factory = world.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWhatsAppChannelDirectory>();
            services.AddSingleton<IWhatsAppChannelDirectory>(doubles.Directory);
            services.RemoveAll<IWhatsAppSender>();
            services.AddSingleton<IWhatsAppSender>(doubles.Sender);
            services.RemoveAll<IQuotationPdfStorage>();
            services.AddSingleton<IQuotationPdfStorage>(doubles.Storage);
        }));
        return (factory, doubles);
    }

    /// <summary>Lo que Siigo contesta al pedir el PDF de la factura que <see cref="ArrangeAsync"/> deja creada.</summary>
    public static void SiigoServesThePdf(InvoicingWorld world) =>
        world.Siigo.On(
            "/v1/invoices/d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f/pdf", HttpStatusCode.OK, "{\"base64\":\"" + PdfBase64 + "\"}");
}

internal sealed class InvoiceWhatsAppDoubles(IEnumerable<WhatsAppChannel> channels)
{
    public RecordingChannelDirectory Directory { get; } = new(channels);

    public RecordingInvoiceSender Sender { get; } = new();

    public RecordingInvoiceStorage Storage { get; } = new();
}

/// <summary>Como el adaptador real: una Zenvia sin <c>InvoiceTemplateId</c> no es un canal para una factura.</summary>
internal sealed class RecordingChannelDirectory(IEnumerable<WhatsAppChannel> channels) : IWhatsAppChannelDirectory
{
    public Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(
        Guid tenantId, WhatsAppDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WhatsAppChannel>>(
            [.. channels.Where(channel => document != WhatsAppDocument.Invoice || channel is not ZenviaWhatsAppChannel { InvoiceTemplateId: null or "" })]);
}

internal sealed class RecordingInvoiceSender : IWhatsAppSender
{
    private readonly Lock _gate = new();
    private readonly List<(WhatsAppChannel Channel, WhatsAppInvoiceMessage Message)> _invoices = [];

    public IReadOnlyList<(WhatsAppChannel Channel, WhatsAppInvoiceMessage Message)> Invoices
    {
        get
        {
            lock (_gate)
            {
                return [.. _invoices];
            }
        }
    }

    /// <summary>Si viene, el envío de la factura la lanza en vez de registrarse (el camino de falla).</summary>
    public Exception? Failure { get; set; }

    public Task SendInvoiceAsync(WhatsAppChannel channel, WhatsAppInvoiceMessage message, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _invoices.Add((channel, message));
        }

        return Task.CompletedTask;
    }

    public Task SendQuotationAsync(WhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SendOrderAsync(WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Sólo sabe publicar la copia pública de una factura, bajo <c>invoices/</c> y con una clave aleatoria.</summary>
internal sealed class RecordingInvoiceStorage : IQuotationPdfStorage
{
    public const string PublicBase = "https://public.invoice-test.example/";

    private readonly Lock _gate = new();
    private readonly List<(string Url, byte[] Content)> _published = [];

    public IReadOnlyList<(string Url, byte[] Content)> Published
    {
        get
        {
            lock (_gate)
            {
                return [.. _published];
            }
        }
    }

    /// <summary>Si se pone, publicar la copia lanza esta falla (bucket público sin configurar).</summary>
    public Exception? PublishFailure { get; set; }

    public Task<string> PublishInvoiceAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (PublishFailure is not null)
        {
            throw PublishFailure;
        }

        var url = $"{PublicBase}invoices/{Guid.NewGuid():N}.pdf";
        lock (_gate)
        {
            _published.Add((url, content.ToArray()));
        }

        return Task.FromResult(url);
    }

    public Task<string> SaveAsync(Guid tenantId, QuotationId quotationId, byte[] content, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> SaveOrderAsync(Guid tenantId, OrderId orderId, byte[] content, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> PublishAsync(string storageKey, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string> CreateDownloadUrlAsync(string storageKey, string downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
