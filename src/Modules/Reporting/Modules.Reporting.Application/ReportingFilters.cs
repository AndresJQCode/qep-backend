using FluentValidation;
using Modules.Reporting.Domain;

namespace Modules.Reporting.Application;

/// <summary>
/// Los filtros del reporte de ventas tal como llegan por query string. <c>PaymentStatus</c> es
/// texto y no el enum: el valor lo escribe el llamador, y un enum en la firma haría que un valor
/// inválido lo rechazara el binder con un 400 opaco en vez del 422 con el mapa <c>errors</c> que
/// el contrato fija.
///
/// El mismo record lo usan el listado y la exportación —el contrato pide exactamente los mismos
/// filtros menos la paginación—, así que también hay un solo validador para los dos caminos.
/// </summary>
public sealed record SalesReportFilter(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
    Guid? ClientId,
    string? PaymentStatus);

/// <summary>Los mismos filtros ya validados y parseados, que es lo que ve el adaptador. Separado
/// del filtro de entrada para que ningún origen de datos tenga que volver a interpretar
/// texto.</summary>
public sealed record SalesReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
    Guid? ClientId,
    SalePaymentStatusFilter? PaymentStatus);

/// <summary>Ver <see cref="SalesReportFilter"/>.</summary>
public sealed record QuotationsReportFilter(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
    Guid? ClientId,
    string? Status);

/// <summary>Ver <see cref="SalesReportCriteria"/>.</summary>
public sealed record QuotationsReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? AdvisorId,
    Guid? ClientId,
    QuotationStatusFilter? Status);

/// <summary>Ver <see cref="SalesReportFilter"/>.</summary>
public sealed record PriceChangeReportFilter(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? ProductId,
    Guid? ChangedBy,
    string? Field);

/// <summary>Ver <see cref="SalesReportCriteria"/>.</summary>
public sealed record PriceChangeReportCriteria(
    Guid TenantId,
    DateOnly? From,
    DateOnly? To,
    Guid? ProductId,
    Guid? ChangedBy,
    PriceChangeField? Field);

/// <summary>
/// El reporte de clientes no filtra por fecha: no hay ninguna columna temporal que el negocio
/// pida acotar. <c>IsActive</c> nulo trae activos e inactivos.
/// </summary>
public sealed record CustomerReportFilter(
    Guid TenantId,
    bool? IsActive,
    Guid? ClassificationId,
    Guid? DepartmentId);

/// <summary>Ver <see cref="SalesReportCriteria"/>. No tiene nada que parsear, pero existe igual
/// para que los cuatro puertos reciban el mismo tipo de objeto.</summary>
public sealed record CustomerReportCriteria(
    Guid TenantId,
    bool? IsActive,
    Guid? ClassificationId,
    Guid? DepartmentId);

/// <summary>
/// Traduce filtro de entrada a criterio.
///
/// El parseo es la única traducción: los enums ya los valida
/// <see cref="SalesReportFilterValidator"/> y compañía, así que llegar acá con un texto inválido
/// es un error de programación, no de entrada — y <c>ReportFilterParser</c> tira en vez de elegir
/// un valor en silencio.
/// </summary>
public static class ReportFilterMapping
{
    public static SalesReportCriteria ToCriteria(this SalesReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParsePaymentStatus(filter.PaymentStatus));

    public static QuotationsReportCriteria ToCriteria(this QuotationsReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.AdvisorId,
            filter.ClientId,
            ReportFilterParser.ParseQuotationStatus(filter.Status));

    public static PriceChangeReportCriteria ToCriteria(this PriceChangeReportFilter filter) =>
        new(
            filter.TenantId,
            filter.From,
            filter.To,
            filter.ProductId,
            filter.ChangedBy,
            ReportFilterParser.ParsePriceChangeField(filter.Field));

    public static CustomerReportCriteria ToCriteria(this CustomerReportFilter filter) =>
        new(filter.TenantId, filter.IsActive, filter.ClassificationId, filter.DepartmentId);
}

/// <summary>
/// Un caso de uso con texto libre lleva validador aunque el dominio ya valide: el dominio da un
/// código (<c>reporting.filter.invalid</c> → 422) y el validador da el **campo**
/// (<c>validation.failed</c> → 422 con el mapa <c>errors</c>). El nombre de propiedad viaja en el
/// mapa, así que el frontend puede marcar el control equivocado.
/// </summary>
public sealed class SalesReportFilterValidator : AbstractValidator<SalesReportFilter>
{
    public SalesReportFilterValidator()
    {
        RuleFor(filter => filter.PaymentStatus)
            .Must(value => ReportFilterParser.TryParsePaymentStatus(value, out _))
            .When(filter => !string.IsNullOrWhiteSpace(filter.PaymentStatus))
            .WithMessage(
                "paymentStatus must be one of FullPaymentReceived, PartialPaymentReceived, "
                    + "PaymentPending.");
        RuleFor(filter => filter.To)
            .GreaterThanOrEqualTo(filter => filter.From!.Value)
            .When(filter => filter.From is not null && filter.To is not null)
            .WithMessage("to must be on or after from.");
    }
}

/// <summary>Ver <see cref="SalesReportFilterValidator"/>.</summary>
public sealed class QuotationsReportFilterValidator : AbstractValidator<QuotationsReportFilter>
{
    public QuotationsReportFilterValidator()
    {
        RuleFor(filter => filter.Status)
            .Must(value => ReportFilterParser.TryParseQuotationStatus(value, out _))
            .When(filter => !string.IsNullOrWhiteSpace(filter.Status))
            .WithMessage("status must be one of Draft, Sent, Expired, Voided.");
        RuleFor(filter => filter.To)
            .GreaterThanOrEqualTo(filter => filter.From!.Value)
            .When(filter => filter.From is not null && filter.To is not null)
            .WithMessage("to must be on or after from.");
    }
}

/// <summary>Ver <see cref="SalesReportFilterValidator"/>.</summary>
public sealed class PriceChangeReportFilterValidator : AbstractValidator<PriceChangeReportFilter>
{
    public PriceChangeReportFilterValidator()
    {
        RuleFor(filter => filter.Field)
            .Must(value => ReportFilterParser.TryParsePriceChangeField(value, out _))
            .When(filter => !string.IsNullOrWhiteSpace(filter.Field))
            .WithMessage("field must be one of PriceBaseUsd, PriceBaseCop, ScaleDiscount.");
        RuleFor(filter => filter.To)
            .GreaterThanOrEqualTo(filter => filter.From!.Value)
            .When(filter => filter.From is not null && filter.To is not null)
            .WithMessage("to must be on or after from.");
    }
}

/// <summary>Sin nada que validar todavía —los tres filtros son tipados—, pero declarado para que
/// agregar uno de texto no pase por decidir de nuevo si el reporte lleva validador.</summary>
public sealed class CustomerReportFilterValidator : AbstractValidator<CustomerReportFilter>;
