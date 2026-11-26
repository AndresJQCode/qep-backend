using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Application.RichText;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Pdf;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El agujero que estas pruebas tapan: la plantilla `quotation.typ` **no la compila nadie** en el
/// camino de `dotnet build` ni de `dotnet test`. Es un recurso embebido que viaja como texto, así
/// que romperla —renombrar un campo del DTO, equivocarse en la sintaxis— deja el build verde, las
/// pruebas verdes, y falla recién en produccion cuando un asesor le da a "enviar": el servicio
/// responde 400 y el caso de uso tira `quotation.pdf.render_failed`.
///
/// Ya paso una vez con los importes de linea. La diferencia entre "el documento dice un numero
/// mal" y "el documento no existe" es de grado, no de clase: las dos las descubre el cliente.
///
/// Todo lo que se verifica sale del **mismo request** que `QCodePdfRenderer` le manda al servicio
/// —`source` y `data` capturados de la llamada real— y no de una copia. Si se reconstruyera la
/// plantilla o la serializacion por separado, la prueba podria pasar mientras produccion falla,
/// que es exactamente el problema que viene a resolver.
/// </summary>
public sealed partial class QuotationTemplateTests
{
    // Los nombres con los que la plantilla desreferencia el payload: la raiz, la variable de cada
    // linea, la de cada parte (`parte(data.billing)`), las tres secciones configurables y el bloque
    // y el tramo que recorren `bloques()` y `runs()`. Un binding nuevo que no figure aca no se
    // verifica -- es un hueco de cobertura, no un falso rojo, y por eso vive a la vista.
    private static readonly string[] Bindings = ["data", "item", "valor", "aviso", "terminos", "proceso", "bloque", "run"];

    /// <summary>
    /// El contrato entre el DTO y la plantilla, que hoy solo existe como un comentario en
    /// <c>QCodePdfRenderer</c>: "cambiarlo aca rompe el `.typ` sin que falle la compilacion de
    /// C#". Cada campo que la plantilla lee tiene que estar en el JSON que viaja al lado.
    ///
    /// No necesita `typst` instalado: corre siempre, en cualquier maquina y en CI.
    /// </summary>
    [Fact]
    public async Task EveryFieldTheTemplateReadsExistsInThePayload()
    {
        var (source, data, _) = await CapturedRequestAsync(Complete());

        // Los comentarios del `.typ` nombran campos a proposito ("la plantilla lee
        // data.quotationNumber"), y un ejemplo en un comentario no es un campo que haga falta.
        var markup = string.Join(
            '\n',
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

        var checkedPaths = 0;
        foreach (var reference in Reference().Matches(markup).Cast<Match>())
        {
            var binding = reference.Groups[1].Value;
            if (!Bindings.Contains(binding)) continue;

            AssertPathExists(RootFor(binding, data), binding, reference.Groups[2].Value);
            checkedPaths++;
        }

        // Sin esto la prueba pasaria en verde si la expresion dejara de encontrar nada.
        Assert.True(checkedPaths > 20, $"solo se verificaron {checkedPaths} referencias");
    }

    // Dos retenciones: una fila por cada una, rotulada con el nombre de la tasa, y el neto al final.
    [Fact]
    public async Task TheTemplateCompilesWithTwoWithholdings() =>
        await AssertCompilesAsync(Complete() with
        {
            RetentionAmount = 10_398m,
            Withholdings =
            [
                new QuotationPdfWithholding("Retefuente 2,5 %", 7_500m, "2,5 %"),
                new QuotationPdfWithholding("ReteICA Bogotá", 2_898m, "0,966 %"),
            ],
            NetTotal = 1_525_722m,
        });

    // El rótulo de cada fila es el nombre de la tasa y su porcentaje efectivo entre paréntesis
    // («ReteICA Bogotá (0,966 %):», owner 2026-11-15). El porcentaje es el del documento, no el del nombre: si un asesor lo
    // edita, el PDF dice el que se aplicó. Las tasas se nombran sin porcentaje; la consola lo muestra con el mismo formato.
    [Fact]
    public async Task EachWithholdingRowCarriesItsEffectivePercentage()
    {
        var (source, data, _) = await CapturedRequestAsync(Complete() with
        {
            Withholdings =
            [
                new QuotationPdfWithholding("ReteICA Bogotá", 2_898m, "0,966 %"),
                new QuotationPdfWithholding("Retefuente", 7_500m, "2,5 %"),
            ],
        });

        const string Label = "retencion.name + \" (\" + retencion.percentageLabel + \"):\"";
        Assert.Contains(Label, StripComments(source), StringComparison.Ordinal);
        Assert.Equal<string>(
            ["ReteICA Bogotá (0,966 %):", "Retefuente (2,5 %):"],
            data.GetProperty("withholdings").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString() + " (" + row.GetProperty("percentageLabel").GetString() + "):")
                .ToArray());
    }

    // El caso completo ejercita todas las ramas que dibujan algo: descuento por linea, retencion,
    // cuenta de cobro y partes con datos propios. También lleva las tres secciones del spec 2026-11-16 §5.3, con cada forma de
    // bloque y cada marca (AllSections).
    [Fact]
    public async Task TheTemplateCompilesWithACompleteDocument() =>
        await AssertCompilesAsync(Complete());

    // Un tenant sin secciones: `data.sections` llega en `none` y nada lo desreferencia.
    [Fact]
    public async Task TheTemplateCompilesWithoutSections()
    {
        var (_, data, _) = await CapturedRequestAsync(Minimal());

        Assert.Equal(JsonValueKind.Null, data.GetProperty("sections").ValueKind);
        await AssertCompilesAsync(Minimal());
    }

    // Y con el objeto presente pero cada sección en `none` (ninguna sale en este documento).
    [Fact]
    public async Task TheTemplateCompilesWithEverySectionLeftOut() =>
        await AssertCompilesAsync(Complete() with { Sections = new QuotationPdfSections(null, null, null, ShowPaymentAccount: true) });

    // La cuenta apagada por el tenant (spec 2026-11-16 pdf-payment-account): salen las tres líneas de la cuenta y nada más; el
    // recuadro sigue por la forma de pago. El caso con la cuenta encendida es el control de que el texto se lee de verdad.
    [Fact]
    public async Task PaymentAccountHiddenPrintsNoAccountLines()
    {
        var shown = await RenderedTextAsync(Complete());
        var hidden = await RenderedTextAsync(Complete() with { Sections = AllSections() with { ShowPaymentAccount = false } });

        // Typst parte «Número:» en dos nodos y el texto plano los une con un espacio: se busca la palabra, que en la plantilla
        // sólo aparece en el renglón del número de cuenta.
        Assert.Contains("Cuenta para el pago", shown, StringComparison.Ordinal);
        Assert.Contains("Número", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Cuenta para el pago", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("Número", hidden, StringComparison.Ordinal);
        Assert.Contains("Forma de pago", hidden, StringComparison.Ordinal);
        Assert.Contains(PaymentBox, hidden, StringComparison.Ordinal);
    }

    // Sin cuenta que mostrar, sin forma de pago y sin contraentrega, el recuadro no tiene nada que decir: no se dibuja vacío.
    [Fact]
    public async Task PaymentAccountHiddenWithoutPaymentMethodPrintsNoPaymentBox()
    {
        var text = await RenderedTextAsync(Complete() with
        {
            PaymentMethod = null,
            CashOnDelivery = false,
            Sections = AllSections() with { ShowPaymentAccount = false },
        });

        Assert.DoesNotContain(PaymentBox, text, StringComparison.Ordinal);
    }

    // Spec 2026-11-17 §4.2: una empresa sin cuenta conserva el membrete, pero no hay bloque de pago que pintar.
    [Fact]
    public async Task TheTemplateOmitsThePaymentBlockWithoutBank()
    {
        var complete = Complete();
        var text = await RenderedTextAsync(complete with
        {
            BillingAccount = complete.BillingAccount! with { BankName = null, AccountNumber = null },
        });

        Assert.DoesNotContain("Cuenta para el pago", text, StringComparison.Ordinal);
        Assert.Contains(complete.BillingAccount!.CompanyName, text, StringComparison.Ordinal);
    }

    // El aviso ya no depende de la cuenta (decisión 4): sin cuenta y sin recuadro, sale solo.
    [Fact]
    public async Task PaymentNoticePrintsWithoutBillingAccount()
    {
        var text = await RenderedTextAsync(Complete() with
        {
            BillingAccount = null,
            PaymentMethod = null,
            CashOnDelivery = false,
        });

        Assert.DoesNotContain(PaymentBox, text, StringComparison.Ordinal);
        Assert.Contains("Ten en cuenta:", text, StringComparison.Ordinal);
    }

    // El texto del tenant sale literal: lo que en Typst sería código, matemática, escape, contenido,
    // negrita, énfasis, referencia o etiqueta no se interpreta, así que tampoco rompe la compilación.
    [Fact]
    public async Task TheTemplateCompilesWithTypstSyntaxInSectionText() =>
        await AssertCompilesAsync(Complete() with
        {
            Sections = new QuotationPdfSections(
                new QuotationPdfSection("#let x = 1 $a$", [Paragraph(new RichTextRun("#let x = 1 $a$ \\ [b] *c* _d_ @e <f>"))]),
                new QuotationPdfSection(
                    "] #pagebreak() [",
                    [Paragraph(new RichTextRun("#let x = 1 $a$ \\ [b] *c* _d_ @e <f>", Bold: true, Href: "https://ejemplo.co/#[x]"))]),
                null,
                ShowPaymentAccount: true),
        });

    [Fact]
    public async Task TheTemplateNeverEvaluatesData()
    {
        var (source, _, _) = await CapturedRequestAsync(Minimal());

        Assert.DoesNotContain("eval(", StripComments(source), StringComparison.Ordinal);
    }

    // Los textos del aviso y del proceso que estaban escritos en la plantilla ahora los configura el
    // tenant: la plantilla ya no imprime ninguno por su cuenta.
    [Fact]
    public async Task TheFixedTextsAreGone()
    {
        var (source, _, _) = await CapturedRequestAsync(Minimal());

        Assert.DoesNotContain("corresponsal bancario", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Alistamiento y Entrega", source, StringComparison.Ordinal);
    }

    // El logo del tenant viaja por `assets`, no dentro de `data`, y la plantilla lo resuelve por
    // nombre de archivo. Ninguna otra prueba compila esa rama: el merge con `feature/sales`
    // reescribio justo ese bloque del membrete. En **webp** a proposito — es el formato que un
    // tenant sube y el que rompia el PDF mientras `qcode-pdf` corria Typst 0.13
    // (`unknown image format`); si alguien vuelve a bajar `TYPST_VERSION`, esto lo agarra.
    [Fact]
    public async Task TheTemplateCompilesWithATenantLogo() =>
        await AssertCompilesAsync(Complete() with
        {
            Logo = new QuotationPdfLogo(
                "logo-del-tenant.webp",
                Convert.FromBase64String(
                    "UklGRiQAAABXRUJQVlA4IBgAAAAwAQCdASoBAAEAAwA0JaQAA3AA/vuUAAA=")),
        });

    // Y el caso vacio ejercita las que **no** dibujan. Es donde una plantilla se rompe de verdad:
    // `data.billingAccount.companyName` revienta si nadie pregunto por el `none` de arriba.
    [Fact]
    public async Task TheTemplateCompilesWithEverythingOptionalMissing() =>
        await AssertCompilesAsync(Minimal());

    // Recoger en tienda es su propia rama en "Entregar en", y la unica que dibuja la banda de
    // partes con la facturacion en los datos del cliente y sin fila de envio.
    [Fact]
    public async Task TheTemplateCompilesWithAStorePickup() =>
        await AssertCompilesAsync(Minimal() with
        {
            Shipping = new QuotationPdfParty(false, string.Empty, string.Empty, string.Empty),
            IsStorePickup = true,
        });

    // La prueba de contrato de arriba sólo verifica los campos que la plantilla lee; esta
    // verifica que la rama de recoger en tienda exista y lea el campo correcto.
    [Fact]
    public async Task TheTemplatePrintsStorePickupFromItsOwnFlag()
    {
        var (source, data, _) = await CapturedRequestAsync(Minimal() with { IsStorePickup = true });

        Assert.True(data.GetProperty("isStorePickup").GetBoolean());
        Assert.Contains("data.isStorePickup", source, StringComparison.Ordinal);
        Assert.Contains("Recoger en tienda", source, StringComparison.Ordinal);
    }

    // Spec 2026-09-17, punto 7: la fecha de emisión llega ya en el día del tenant y sin hora, y la
    // plantilla la imprime tal cual. Cortar un ISO en la "T" es lo que hacía decir "1 de enero de
    // 2027" a un documento emitido el 31 de diciembre en Bogotá.
    [Fact]
    public async Task TheTemplatePrintsTheIssueDateFromItsLocalDate()
    {
        var (source, data, _) = await CapturedRequestAsync(Minimal() with { IssuedOn = new DateOnly(2026, 12, 31) });

        Assert.Equal("2026-12-31", data.GetProperty("issuedOn").GetString());
        Assert.Contains("fecha(data.issuedOn)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("split(\"T\")", source, StringComparison.Ordinal);
    }

    // Consumidor final no tiene contacto ni direccion: lo que lo identifica en la factura es el
    // NIT generico, y la plantilla lo imprime desde su propio campo.
    [Fact]
    public async Task TheTemplatePrintsThePartyTaxIdFromItsOwnField()
    {
        var (source, data, _) = await CapturedRequestAsync(Minimal() with
        {
            Billing = FinalConsumerBilling(),
        });

        Assert.Equal(
            FinalConsumer.IdentificationNumber,
            data.GetProperty("billing").GetProperty("taxId").GetString());
        Assert.Contains("valor.taxId", source, StringComparison.Ordinal);
    }

    // La rama de consumidor final dibuja la banda de partes con una facturacion que no es la del
    // cliente y no tiene contacto ni direccion: la unica combinacion con el renglon del NIT.
    [Fact]
    public async Task TheTemplateCompilesWithAFinalConsumerBilling() =>
        await AssertCompilesAsync(Minimal() with { Billing = FinalConsumerBilling() });

    // Un envío cobrado sin IVA (USD, o cliente con excedente de IVA) no imprime «IVA del envío: 0»: el
    // renglón sobra y, con excedente, contradiría el «excedente de IVA» del renglón de productos.
    [Fact]
    public async Task TheTemplateHidesTheFreightTaxRowWhenItIsZero()
    {
        var (source, _, _) = await CapturedRequestAsync(Minimal() with
        {
            FreightChargeMode = "Charged",
            FreightAmount = 20m,
            FreightSubtotal = 20m,
        });

        Assert.Contains("if data.freightTaxAmount > 0", source, StringComparison.Ordinal);
    }

    // Y esa rama compila: cobrado en USD, sin IVA del envío.
    [Fact]
    public async Task TheTemplateCompilesWithAChargedFreightWithoutTax() =>
        await AssertCompilesAsync(Minimal() with
        {
            CarrierName = "Coordinadora",
            FreightChargeMode = "Charged",
            FreightAmount = 20m,
            FreightSubtotal = 20m,
        });

    // El pedido usa la misma plantilla (owner: «layout único para cotización y pedido»): compila en
    // las dos ramas del membrete, con y sin emisor, y sin vigencia.
    [Fact]
    public async Task TheTemplateCompilesWithAnOrder() =>
        await AssertCompilesAsync(Complete() with { Kind = QuotationPdfKind.Order, ValidUntil = null });

    [Fact]
    public async Task TheTemplateCompilesWithAMinimalOrder() =>
        await AssertCompilesAsync(Minimal() with { Kind = QuotationPdfKind.Order });

    // Lo único que el pedido cambia: el nombre del documento —membrete y pie— y el renglón de
    // vigencia, que un pedido no tiene. La prueba de contrato de arriba sólo mira que `data.kind`
    // exista; esta, que la plantilla decida con él.
    [Fact]
    public async Task TheTemplateNamesTheDocumentAfterItsKindAndDropsTheValidityOfAnOrder()
    {
        var (source, data, _) = await CapturedRequestAsync(Minimal() with { Kind = QuotationPdfKind.Order });

        Assert.Equal("Order", data.GetProperty("kind").GetString());
        Assert.Contains("data.kind == \"Order\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Pedido\"", source, StringComparison.Ordinal);
        // El nombre del documento sale de una sola variable: ningún "Cotización" suelto queda impreso
        // fuera de ella (membrete con emisor, membrete sin emisor y pie).
        Assert.Equal(1, Count(StripComments(source), "Cotización"));
        Assert.Contains("if not es-pedido [", source, StringComparison.Ordinal);
    }

    // «Emitida» concuerda con «la cotización»; el pedido y el presupuesto de España son masculinos.
    [Fact]
    public async Task TheIssueDateAgreesWithTheDocumentGender()
    {
        var (source, _, _) = await CapturedRequestAsync(Minimal() with { Kind = QuotationPdfKind.Order });

        Assert.Contains("#let masculino = es-pedido or espana", source, StringComparison.Ordinal);
        Assert.Contains("if masculino { \"Emitido\" } else { \"Emitida\" }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("renglon(\"Emitida\"", source, StringComparison.Ordinal);
    }

    // Spec 2026-11-22 (i18n): el idioma del tenant viaja en `data`; sin tocarlo, es-419.
    [Fact]
    public async Task ThePayloadCarriesTheLanguage()
    {
        var (_, spain, _) = await CapturedRequestAsync(Minimal() with { Language = "es-ES" });
        var (_, defaults, _) = await CapturedRequestAsync(Minimal());

        Assert.Equal("es-ES", spain.GetProperty("language").GetString());
        Assert.Equal("es-419", defaults.GetProperty("language").GetString());
    }

    // En España la cotización es «presupuesto» (masculino: «Emitido», «Válido hasta») y el asesor, «comercial».
    [Fact]
    public async Task InSpainTheQuotationPrintsAsABudgetWithACommercial()
    {
        var text = await RenderedTextAsync(Complete() with { Language = "es-ES" });

        Assert.Contains("Presupuesto", text, StringComparison.Ordinal);
        Assert.Contains("Emitido", text, StringComparison.Ordinal);
        Assert.Contains("Válido hasta", text, StringComparison.Ordinal);
        Assert.Contains("Comercial", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cotización", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Emitida", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Asesor", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InLatinAmericaTheQuotationPrintsAsBefore()
    {
        var text = await RenderedTextAsync(Complete());

        Assert.Contains("Cotización", text, StringComparison.Ordinal);
        Assert.Contains("Emitida", text, StringComparison.Ordinal);
        Assert.Contains("Válida hasta", text, StringComparison.Ordinal);
        Assert.Contains("Asesor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Presupuesto", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InSpainTheOrderStaysAnOrderWithACommercial()
    {
        var text = await RenderedTextAsync(Complete() with { Kind = QuotationPdfKind.Order, ValidUntil = null, Language = "es-ES" });

        Assert.Contains("Pedido", text, StringComparison.Ordinal);
        Assert.Contains("Comercial", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Presupuesto", text, StringComparison.Ordinal);
    }

    // Spec 2026-10-23 D-R1: los separadores del tenant viajan en `data`; sin tocarlos, los colombianos.
    [Fact]
    public async Task ThePayloadCarriesTheSeparators()
    {
        var (_, dotDecimal, _) = await CapturedRequestAsync(
            Complete() with { ThousandsSeparator = ",", DecimalSeparator = "." });
        var (_, defaults, _) = await CapturedRequestAsync(Minimal());

        Assert.Equal(",", dotDecimal.GetProperty("thousandsSeparator").GetString());
        Assert.Equal(".", dotDecimal.GetProperty("decimalSeparator").GetString());
        Assert.Equal(".", defaults.GetProperty("thousandsSeparator").GetString());
        Assert.Equal(",", defaults.GetProperty("decimalSeparator").GetString());
    }

    // Los ayudantes de importe no llevan separadores escritos a mano: salen del payload.
    [Fact]
    public async Task TheAmountHelpersUseTheTenantSeparators()
    {
        var (source, _, _) = await CapturedRequestAsync(Minimal());
        var markup = StripComments(source);

        Assert.Contains("partes.join(data.thousandsSeparator)", markup, StringComparison.Ordinal);
        Assert.Contains("data.decimalSeparator", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("partes.join(\".\")", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("\",\" +", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("\",0\" +", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("porcentaje(", markup, StringComparison.Ordinal);
        // La fecha sigue escrita en español: no depende del formato numérico.
        Assert.Contains("fecha(data.issuedOn)", markup, StringComparison.Ordinal);
        Assert.Contains("#let meses", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTemplateCompilesWithADotDecimalDocument() =>
        await AssertCompilesAsync(Complete() with { ThousandsSeparator = ",", DecimalSeparator = "." });

    [Fact]
    public async Task TheTemplateCompilesWithADotDecimalMinimalDocument() =>
        await AssertCompilesAsync(Minimal() with { ThousandsSeparator = ",", DecimalSeparator = "." });

    // Typst evalúa las ramas de cantidad() de forma perezosa: con cantidades enteras nunca entra
    // a las que usan el separador decimal, y un error ahí sólo aparecería en producción como un 422.
    // 12.5 y 1234.5 ejercen la rama de décimas, 0.05 la de centésimas sueltas y 3.75 la general.
    [Fact]
    public async Task TheTemplateCompilesFractionalQuantitiesWithADotDecimalDocument() =>
        await AssertCompilesAsync(WithFractionalQuantities(Complete()) with
        {
            ThousandsSeparator = ",",
            DecimalSeparator = ".",
        });

    [Fact]
    public async Task TheTemplateCompilesFractionalQuantitiesWithTheColombianDefaults() =>
        await AssertCompilesAsync(WithFractionalQuantities(Complete()));

    // Spec 2026-11-18 §6: la unidad va junto a la cantidad como texto plano. Con texto que en Typst sería markup, sale literal.
    [Fact]
    public async Task TheTemplateCompilesWithUnits() =>
        await AssertCompilesAsync(Complete() with
        {
            Items =
            [
                new QuotationPdfLine("Soporte técnico", 2m, 80000m, 0m, 80000m, 160000m, "horas"),
                new QuotationPdfLine("Tornillo", 1m, 1500m, 0m, 1500m, 1500m, "unidad"),
                new QuotationPdfLine("Raro", 1m, 1000m, 0m, 1000m, 1000m, "#pagebreak() $x$ [y]"),
            ],
        });

    [Fact]
    public async Task TheTemplatePrintsTheUnitNextToTheQuantity()
    {
        var withUnits = await RenderedTextAsync(Complete() with
        {
            Items = [new QuotationPdfLine("Soporte técnico", 2m, 80000m, 0m, 80000m, 160000m, "horas")],
        });
        var withoutUnits = await RenderedTextAsync(Complete() with
        {
            Items = [new QuotationPdfLine("Soporte técnico", 2m, 80000m, 0m, 80000m, 160000m)],
        });

        Assert.Contains("horas", withUnits, StringComparison.Ordinal);
        Assert.DoesNotContain("horas", withoutUnits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrderTableHasItemNumberAndCodeColumns()
    {
        var text = await RenderedTextAsync(Complete() with
        {
            Kind = QuotationPdfKind.Order,
            ValidUntil = null,
            Items =
            [
                new QuotationPdfLine("Cable por metro", 2m, 35900m, 15m, 30515m, 61030m, ProductCode: "CAB-001"),
                new QuotationPdfLine("Tornillo", 1m, 1500m, 0m, 1500m, 1500m, ProductCode: "TOR-002"),
            ],
        });

        foreach (var expected in new[]
        {
            // Los rótulos del encabezado salen en mayúsculas (`rotulo`).
            "# ITEM", "CÓDIGO", "DESCRIPCIÓN", "CANTIDAD", "VR. UNITARIO", "VR. TOTAL", "CAB-001", "TOR-002",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("VALOR CON DESCUENTO", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQuotationTableKeepsItsColumns()
    {
        var text = await RenderedTextAsync(Complete() with
        {
            Items = [new QuotationPdfLine("Tornillo", 1m, 1500m, 0m, 1500m, 1500m, ProductCode: "TOR-002")],
        });

        Assert.DoesNotContain("TOR-002", text, StringComparison.Ordinal);
        Assert.Contains("PRODUCTO", text, StringComparison.Ordinal);
    }

    private static QuotationPdfDocument WithFractionalQuantities(QuotationPdfDocument document) =>
        document with
        {
            Items =
            [
                new QuotationPdfLine("Cable por metro", 12.5m, 35900m, 0m, 35900m, 448750m),
                new QuotationPdfLine("Tornillo suelto", 0.05m, 114700m, 0m, 114700m, 5735m),
                new QuotationPdfLine("Resina a granel", 1234.5m, 1000m, 0m, 1000m, 1234500m),
                new QuotationPdfLine("Lámina", 3.75m, 2000m, 0m, 2000m, 7500m),
            ],
        };

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string StripComments(string source) =>
        string.Join(
            '\n',
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

    private static QuotationPdfParty FinalConsumerBilling() => new(
        false,
        FinalConsumer.Name,
        string.Empty,
        string.Empty,
        FinalConsumer.IdentificationNumber);

    // ------------------------------------------------------------------ compilacion

    private static async Task AssertCompilesAsync(QuotationPdfDocument document)
    {
        var typst = RequireTypst();
        var workspace = await WorkspaceForAsync(document);
        try
        {
            var template = Path.Combine(workspace.FullName, "quotation.typ");
            var output = Path.Combine(workspace.FullName, "quotation.pdf");
            var (exitCode, _, diagnostics) = await RunAsync(
                typst, workspace.FullName, ["compile", "--input", "data=data.json", template, output]);

            Assert.True(exitCode == 0, $"typst no pudo compilar la plantilla:\n{diagnostics}");
            Assert.True(File.Exists(output), "typst devolvio 0 pero no dejo el PDF.");

            var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal("%PDF"u8.ToArray(), bytes[..4]);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    // Marca que `TextoPlano` antepone a un `block` con borde. En la plantilla el único es el recuadro de condiciones de pago, así
    // que su presencia en el texto dice si el recuadro se dibuja.
    private const string PaymentBox = "[recuadro]";

    // Un envoltorio que incluye la plantilla tal cual y recorre el contenido que produce, campo por campo, hasta el texto. El PDF
    // lleva el texto comprimido y con fuentes recortadas, y el export HTML de Typst descarta `grid`: leer el árbol de contenido
    // es la forma de ver qué imprime la plantilla sin agregar dependencias. El pie va en `context` y no se ve, lo que acá sobra.
    private const string TextoPlano = """
        #let plano(c) = {
          if type(c) == str { return c }
          if type(c) != content { return "" }
          if c.func() == linebreak { return "\n" }
          let campos = c.fields()
          if "text" in campos and type(campos.text) == str { return campos.text }
          let partes = ()
          if c.func() == block and "stroke" in campos { partes.push("[recuadro]") }
          for (_, valor) in campos {
            if type(valor) == content { partes.push(plano(valor)) }
            else if type(valor) == array {
              for elemento in valor { if type(elemento) == content { partes.push(plano(elemento)) } }
            }
          }
          if partes.len() == 0 { "" } else { partes.join(" ") }
        }
        #metadata(plano(include "quotation.typ")) <texto>
        """;

    private static async Task<string> RenderedTextAsync(QuotationPdfDocument document)
    {
        var typst = RequireTypst();
        var workspace = await WorkspaceForAsync(document);
        try
        {
            var wrapper = Path.Combine(workspace.FullName, "texto.typ");
            await File.WriteAllTextAsync(wrapper, TextoPlano, TestContext.Current.CancellationToken);
            var (exitCode, output, diagnostics) = await RunAsync(
                typst,
                workspace.FullName,
                ["query", "--input", "data=data.json", wrapper, "<texto>", "--field", "value", "--one"]);

            Assert.True(exitCode == 0, $"typst no pudo leer el texto de la plantilla:\n{diagnostics}");
            return JsonSerializer.Deserialize<string>(output)!;
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static string RequireTypst()
    {
        var typst = FindTypst();
        if (typst is null)
        {
            // En CI la ausencia del binario no puede ser un skip: seria el mismo agujero con otra
            // forma, y en verde. El workflow instala la version clavada en el contenedor.
            Assert.SkipWhen(
                !RunningOnCi,
                "`typst` no esta en PATH ni en TYPST_BIN. Instalalo para ejercer esta prueba: " +
                "https://github.com/typst/typst/releases");

            Assert.Fail("`typst` tiene que estar instalado en CI para compilar la plantilla.");
        }

        return typst!;
    }

    private static async Task<DirectoryInfo> WorkspaceForAsync(QuotationPdfDocument document)
    {
        var (source, data, assets) = await CapturedRequestAsync(document);

        // Mismo layout que arma `qcode-pdf`: la plantilla y su `data.json` en el mismo directorio,
        // porque la ruta de `--input` se resuelve relativa al `.typ` y no al directorio de trabajo.
        var workspace = Directory.CreateTempSubdirectory("qep-typst");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.FullName, "quotation.typ"), source, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.FullName, "data.json"),
            data.GetRawText(),
            TestContext.Current.CancellationToken);

        // `qcode-pdf` deja cada asset en `assets/<nombre>` junto al `.typ` (README, "assets"),
        // que es lo que resuelve el `#image("assets/" + data.logo.fileName)` de la plantilla.
        if (assets is { } adjuntos)
        {
            var carpeta = Directory.CreateDirectory(
                Path.Combine(workspace.FullName, "assets"));
            foreach (var adjunto in adjuntos.EnumerateObject())
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(carpeta.FullName, adjunto.Name),
                    Convert.FromBase64String(adjunto.Value.GetString()!),
                    TestContext.Current.CancellationToken);
            }
        }

        return workspace;
    }

    private static async Task<(int ExitCode, string Output, string Diagnostics)> RunAsync(
        string executable, string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"No se pudo iniciar {executable}.");

        // `typst` avisa por `warning` cuando una fuente no existe y cae a la serif por defecto,
        // asi que la salida de error importa incluso con exit 0. Las dos salidas se leen a la vez:
        // una sola leída hasta el final puede bloquear al proceso si la otra llena su buffer.
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var diagnostics = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await diagnostics);
    }

    // `TYPST_BIN` es la misma variable que el `Dockerfile` de `qcode-pdf` le fija al servicio.
    private static string? FindTypst()
    {
        var configured = Environment.GetEnvironmentVariable("TYPST_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var name = OperatingSystem.IsWindows() ? "typst.exe" : "typst";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Una entrada de PATH con caracteres invalidos no es motivo para no seguir buscando.
            }
        }

        return null;
    }

    private static bool RunningOnCi =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    // ------------------------------------------------------------------ contrato

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9]*)\.([A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)*)")]
    private static partial Regex Reference();

    // Un bloque y un tramo serializados llevan todos sus campos (los que no usan, en null), así que
    // cualquier bloque sirve de raiz para `bloque.*` y cualquier tramo para `run.*`.
    private static JsonElement RootFor(string binding, JsonElement data) => binding switch
    {
        "item" => data.GetProperty("items")[0],
        "valor" => data.GetProperty("billing"),
        "aviso" => data.GetProperty("sections").GetProperty("paymentNotice"),
        "terminos" => data.GetProperty("sections").GetProperty("terms"),
        "proceso" => data.GetProperty("sections").GetProperty("deliveryProcess"),
        "bloque" => data.GetProperty("sections").GetProperty("terms").GetProperty("blocks")[0],
        "run" => data.GetProperty("sections").GetProperty("terms").GetProperty("blocks").EnumerateArray()
            .First(block => block.GetProperty("runs").ValueKind == JsonValueKind.Array)
            .GetProperty("runs")[0],
        _ => data,
    };

    /// <summary>
    /// Camina la ruta contra el JSON. En cuanto el nodo deja de ser un objeto, lo que sigue es un
    /// metodo de Typst y no un campo: `data.items.any(...)` es la coleccion `items` y el `.any` de
    /// la libreria, no un campo llamado "any".
    /// </summary>
    private static void AssertPathExists(JsonElement root, string binding, string path)
    {
        var current = root;
        var walked = binding;

        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return;

            Assert.True(
                current.TryGetProperty(segment, out var next),
                $"La plantilla lee `{walked}.{segment}` y el payload no lo trae. " +
                "Si se renombro un campo de QuotationPdfDocument, hay que renombrarlo tambien en " +
                "quotation.typ: el `.typ` no lo ve el compilador de C#.");

            walked = $"{walked}.{segment}";
            current = next;
        }
    }

    // ------------------------------------------------------------------ el request real

    private static async Task<(string Source, JsonElement Data, JsonElement? Assets)>
        CapturedRequestAsync(QuotationPdfDocument document)
    {
        var capture = new RequestCapture();
        var options = Options.Create(new QuotationsOptions
        {
            Pdf = new PdfOptions { BaseUrl = "https://qcode-pdf.qcode.co", ApiKey = "clave-de-prueba" },
        });

        var renderer = new QCodePdfRenderer(
            new HttpClient(new CapturingHandler(capture)), options);
        await renderer.RenderAsync(document, TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(capture.Json).RootElement;
        var assets = body.TryGetProperty("assets", out var found) ? found : (JsonElement?)null;
        return (body.GetProperty("source").GetString()!, body.GetProperty("data"), assets);
    }

    private sealed class RequestCapture
    {
        public string Json { get; set; } = "{}";
    }

    private sealed class CapturingHandler(RequestCapture capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Json = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46]),
            };
        }
    }

    // ------------------------------------------------------------------ documentos

    private static QuotationPdfDocument Complete() => new(
        QuotationNumber: "QUO-2026-0042",
        IssuedOn: new DateOnly(2026, 9, 10),
        ValidUntil: new DateOnly(2026, 9, 30),
        CustomerName: "Comercializadora del Norte S.A.S.",
        CustomerCuc: "CUC-0042",
        CustomerContact: "3001234567 · compras@ejemplo.co",
        CustomerLocation: "Calle 100 #15-20, Bogotá",
        Billing: new QuotationPdfParty(
            false, "Sede administrativa", "6015550198 · facturacion@ejemplo.co", "Carrera 7 #71-52"),
        Shipping: new QuotationPdfParty(
            false, "Bodega Fontibón", "3109987766 · bodega@ejemplo.co", "Zona Franca, Bodega 14"),
        IsStorePickup: false,
        AdvisorLabel: "ana.perez@ejemplo.co",
        Currency: "COP",
        Symbol: "$",
        Decimals: 0,
        BillingAccount: new QuotationPdfBillingAccount(
            "Ferretería Andina S.A.S.", "900.123.456-7", "NIT", "Carrera 7 #71-52, Bogotá", "6015550100",
            "Bancolombia", "123-456789-01", "COP"),
        PaymentMethod: "Transferencia bancaria a 30 días",
        Items:
        [
            new QuotationPdfLine("BRONCEADOR RITUAL DEL SOL", 12m, 35900m, 15m, 30515m, 366180m),
            new QuotationPdfLine("COMBO JALEA REAL 3", 12m, 114700m, 15m, 97495m, 1169940m),
        ],
        Subtotal: 1290857.15m,
        DiscountAmount: 271080m,
        TaxPercentage: 19m,
        TaxAmount: 245262.85m,
        Total: 1536120m,
        RetentionAmount: 32271.43m,
        Withholdings:
        [
            new QuotationPdfWithholding("Retefuente 2,5 %", 32271.43m, "2,5 %"),
        ],
        NetTotal: 1503848.57m,
        CustomerVatSurplus: false,
        Logo: null,
        CarrierName: "Servientrega",
        FreightChargeMode: "Charged",
        FreightAmount: 15000m,
        FreightTaxPercentage: 19,
        FreightSubtotal: 12605.04m,
        FreightTaxAmount: 2394.96m,
        CashOnDelivery: true,
        Sections: AllSections());

    // Las tres secciones con cada forma de bloque y cada marca: título de los tres niveles, lista
    // ordenada con otra anidada, cita, regla, salto de línea y enlace.
    private static QuotationPdfSections AllSections() => new(
        PaymentNotice: new QuotationPdfSection(
            "Ten en cuenta:",
            [
                Paragraph(
                    new RichTextRun("No recibimos pagos en efectivo."),
                    new RichTextRun(string.Empty, Break: true),
                    new RichTextRun("Paga el valor exacto", Bold: true),
                    new RichTextRun(", sin comisiones.")),
            ]),
        Terms: new QuotationPdfSection(
            "Términos y condiciones",
            [
                Paragraph(
                    new RichTextRun("Precios en "),
                    new RichTextRun("pesos", Italic: true),
                    new RichTextRun(", "),
                    new RichTextRun("sujetos", Underline: true),
                    new RichTextRun(" a "),
                    new RichTextRun("cambio", Strike: true),
                    new RichTextRun(". Consulta "),
                    new RichTextRun("la política", Href: "https://ejemplo.co/politica"),
                    new RichTextRun(".")),
                new RichTextBlock(RichTextBlockKind.Heading, Level: 2, Runs: [new RichTextRun("Garantía")]),
                new RichTextBlock(RichTextBlockKind.Heading, Level: 3, Runs: [new RichTextRun("Cobertura")]),
                new RichTextBlock(RichTextBlockKind.Heading, Level: 4, Runs: [new RichTextRun("Exclusiones")]),
                new RichTextBlock(
                    RichTextBlockKind.List,
                    Ordered: true,
                    Items:
                    [
                        [Paragraph(new RichTextRun("Primero"))],
                        [
                            Paragraph(new RichTextRun("Segundo, con detalle:")),
                            new RichTextBlock(
                                RichTextBlockKind.List,
                                Items: [[Paragraph(new RichTextRun("Uno"))], [Paragraph(new RichTextRun("Otro"))]]),
                        ],
                    ]),
                new RichTextBlock(RichTextBlockKind.Quote, Children: [Paragraph(new RichTextRun("Lo dice el fabricante."))]),
                new RichTextBlock(RichTextBlockKind.Rule),
                Paragraph(new RichTextRun("Escríbenos a "), new RichTextRun("ventas@ejemplo.co", Href: "mailto:ventas@ejemplo.co")),
            ]),
        DeliveryProcess: new QuotationPdfSection(
            "Proceso de entrega",
            [
                Paragraph(new RichTextRun("Al recibir:", Bold: true), new RichTextRun(string.Empty, Break: true), new RichTextRun("Revisa el paquete.")),
                new RichTextBlock(
                    RichTextBlockKind.List,
                    Items: [[Paragraph(new RichTextRun("Cajas en buen estado"))], [Paragraph(new RichTextRun("Cinta sin abrir"))]]),
            ]),
        ShowPaymentAccount: true);

    private static RichTextBlock Paragraph(params RichTextRun[] runs) =>
        new(RichTextBlockKind.Paragraph, Runs: runs);

    private static QuotationPdfDocument Minimal() => new(
        QuotationNumber: "QUO-2026-0002",
        IssuedOn: new DateOnly(2026, 9, 10),
        ValidUntil: null,
        CustomerName: string.Empty,
        CustomerCuc: string.Empty,
        CustomerContact: string.Empty,
        CustomerLocation: string.Empty,
        Billing: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        Shipping: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        IsStorePickup: false,
        AdvisorLabel: string.Empty,
        Currency: "USD",
        Symbol: "US$",
        Decimals: 2,
        BillingAccount: null,
        PaymentMethod: null,
        Items: [new QuotationPdfLine("Tornillo hexagonal 3/8", 100m, 15.5m, 0m, 15.5m, 1550m)],
        Subtotal: 1302.52m,
        DiscountAmount: 0m,
        TaxPercentage: 19m,
        TaxAmount: 247.48m,
        Total: 1550m,
        RetentionAmount: 0m,
        Withholdings: [],
        NetTotal: 1550m,
        CustomerVatSurplus: true,
        Logo: null);
}
