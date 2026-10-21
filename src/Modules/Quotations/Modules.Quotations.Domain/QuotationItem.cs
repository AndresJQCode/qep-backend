using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Una línea de producto de una cotización (modelo-datos-cotizaciones.md §2.2). Entidad hija de
/// <see cref="Quotation"/> — no tiene repositorio propio ni se crea fuera de él, mismo criterio
/// que <c>PriceScale</c> dentro de <c>Product</c> en Catalog.
/// </summary>
public sealed class QuotationItem
{
    // EF Core materializa por acá. El código nunca construye la entidad así: sólo
    // Quotation.AddItem hace cumplir los invariantes.
    private QuotationItem()
    {
    }

    private QuotationItem(
        QuotationItemId id,
        QuotationId quotationId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        string currency,
        DateTimeOffset occurredAt)
    {
        Id = id;
        QuotationId = quotationId;
        VariantId = variantId;
        ProductId = productId;
        Position = position;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Apply(quantity, unitPrice, discountPercentage, taxPercentage, currency, occurredAt);
    }

    public QuotationItemId Id { get; private set; }

    public QuotationId QuotationId { get; private set; }

    /// <summary>La variante que vende la línea (spec 2026-10-20 §4): referencia blanda a
    /// <c>catalog.product_variants</c>, sin FK por el mismo motivo que <see cref="ProductId"/>. Es la
    /// clave de la línea: dos tallas del mismo producto son dos líneas, y precio, escalas y foto son
    /// de la variante. Las líneas anteriores a las variantes la tienen igual al producto, porque la
    /// variante por defecto nació con el id del producto (migración <c>AddVariantToLines</c>).</summary>
    public Guid VariantId { get; private set; }

    /// <summary>Referencia blanda al catálogo de productos (módulo Catalog). Sin FK a propósito:
    /// ningún módulo de negocio referencia las tablas de otro. Se conserva junto a
    /// <see cref="VariantId"/> para que un reporte pueda agrupar por producto.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>
    /// El código de la variante congelado cuando la cotización salió del borrador (owner,
    /// 2026-09-26). Null mientras es borrador —ahí se lee en vivo del catálogo, para que los
    /// cambios del producto se vean— y en las líneas enviadas antes de que la columna existiera,
    /// que se siguen leyendo en vivo: no hay backfill, porque este módulo no lee las tablas de
    /// Catalog. Quien lo muestra decide con <c>QuotationItemProductLabel</c>.
    /// </summary>
    public string? ProductCode { get; private set; }

    /// <summary>El nombre visible de la variante (spec 2026-10-20 §3.4), congelado junto con
    /// <see cref="ProductCode"/> y con la misma regla.</summary>
    public string? ProductName { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>Precio base del producto, snapshot al momento de agregar la línea. No se
    /// recalcula si el precio del producto cambia después (modelo-datos-cotizaciones.md
    /// §1.8): una cotización es un documento histórico.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>Calculado por la aplicación según la escala de precios del producto para
    /// <see cref="Quantity"/> — nunca editable a mano (decisión confirmada).</summary>
    public decimal DiscountPercentage { get; private set; }

    /// <summary>
    /// De dónde salió <see cref="DiscountPercentage"/>. Se persiste porque la respuesta se arma
    /// leyendo la línea guardada y no el resultado del recálculo: sin columna, la pantalla no
    /// tiene cómo saber si el descuento se lo ganó la línea sola, se lo dio el grupo o se lo dio
    /// el piso global que eligió el asesor.
    /// </summary>
    public QuotationDiscountOrigin DiscountOrigin { get; private set; }

    public decimal DiscountAmount { get; private set; }

    /// <summary>La base sin IVA de la línea: lo cobrado (Quantity × UnitPrice − DiscountAmount,
    /// que viene con IVA incluido) menos el <see cref="TaxAmount"/> contenido en ese monto.
    /// </summary>
    public decimal Subtotal { get; private set; }

    /// <summary>Tasa de impuesto del producto (<c>Catalog.TaxRate.Percentage</c>), snapshot al
    /// momento de agregar/editar la línea — mismo criterio que <see cref="UnitPrice"/>: una
    /// cotización es un documento histórico, no sigue la tasa del producto si ésta cambia
    /// después. 0 si el producto no tiene tasa de impuesto asignada, y **siempre 0 en USD**: en
    /// dólares el IVA no se discrimina (ver <see cref="Apply"/>).</summary>
    public int TaxPercentage { get; private set; }

    /// <summary>El IVA **contenido** en lo cobrado por la línea, no uno agregado encima: el
    /// precio del producto ya lo trae. El impuesto de la cotización es la suma de este
    /// campo en todas sus líneas (<see cref="Quotation.RecalculateTotals"/>), no un porcentaje
    /// único aplicado al subtotal completo. En USD es 0: el precio se cobra entero y el IVA no se
    /// discrimina, así que <see cref="Subtotal"/> es lo cobrado completo.</summary>
    public decimal TaxAmount { get; private set; }

    /// <summary>
    /// Lo que cuesta cada unidad ya con el descuento aplicado, IVA adentro — la misma unidad que
    /// <see cref="UnitPrice"/>, no la de <see cref="Subtotal"/>.
    ///
    /// Derivado, no persistido: sale de los tres campos que sí se guardan, así que no hay columna
    /// ni migración y una línea vieja lo reporta igual. Se calcula desde el total de la línea y
    /// no como <c>UnitPrice × (1 − %)</c>: <see cref="DiscountAmount"/> se redondea sobre el
    /// bruto, y restarlo es lo único que reconstruye exactamente lo que se cobra. Mismo criterio
    /// que ya usaba <c>QuotationPdfDocumentMapper</c> para imprimirlo, que ahora lo lee de acá.
    /// La cantidad es siempre mayor que cero por invariante de <see cref="Apply"/>; el guardia
    /// cubre la entidad a medio materializar, no al agregado.
    /// </summary>
    public decimal DiscountedUnitPrice =>
        Quantity > 0 ? Round((Round(Quantity * UnitPrice) - DiscountAmount) / Quantity) : 0m;

    /// <summary>
    /// El precio unitario neto: con el descuento ya aplicado y sin el IVA que trae adentro. Es lo
    /// que el Excel de pedidos pone en "Valor Unit" (2026-10-03), lo que el ERP cobra por unidad.
    ///
    /// Sale de <see cref="Subtotal"/> / <see cref="Quantity"/> y no de restarle el descuento a
    /// <see cref="UnitPriceWithoutTax"/>: el subtotal ya es la base sin IVA de la línea después del
    /// descuento —el de la escala propia, el del grupo o el del piso global, que el recálculo deja
    /// guardado en la línea—, así que <c>Quantity × DiscountedUnitPriceWithoutTax</c> reconstruye la
    /// misma base sobre la que se calcularon <see cref="TaxAmount"/> y los totales, salvo el
    /// redondeo a dos decimales por unidad. Derivado, no persistido, igual que sus vecinos; el
    /// guardia de la cantidad es el mismo de <see cref="DiscountedUnitPrice"/>.
    /// </summary>
    public decimal DiscountedUnitPriceWithoutTax => Quantity > 0 ? Round(Subtotal / Quantity) : 0m;

    /// <summary>
    /// El precio unitario bruto con el IVA que trae adentro quitado: <see cref="UnitPrice"/> se
    /// carga con IVA incluido (ver <see cref="Apply"/>), así que esto es la base por unidad, antes
    /// de cualquier descuento. Lo pide el Excel de pedidos ("Valor Unit sin IVA", 2026-09-24); el
    /// neto, con descuento, es <see cref="DiscountedUnitPriceWithoutTax"/>.
    ///
    /// Derivado, no persistido, igual que <see cref="DiscountedUnitPrice"/>: sin columna ni
    /// migración, y una línea vieja lo reporta igual. Misma familia de fórmula que
    /// <see cref="TaxAmount"/> (<c>× tasa / (100 + tasa)</c>, no <c>× tasa / 100</c>), así que
    /// <c>UnitPrice − UnitPriceWithoutTax</c> es el IVA contenido por unidad. Con tasa 0 es igual a
    /// <see cref="UnitPrice"/> y el divisor nunca es 0. Redondeado a dos decimales como todo importe
    /// de la línea.
    /// </summary>
    public decimal UnitPriceWithoutTax => Round(UnitPrice * 100m / (100m + TaxPercentage));

    /// <summary>Posición de la fila para mantener el orden de despliegue.</summary>
    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static QuotationItem Create(
        QuotationItemId id,
        QuotationId quotationId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        string currency,
        DateTimeOffset occurredAt) =>
        new(id, quotationId, variantId, productId, quantity, unitPrice, discountPercentage, taxPercentage, position, currency, occurredAt);

    /// <summary>
    /// Congela código y nombre si la línea todavía no los tiene. Nunca los pisa: lo que el cliente
    /// ya recibió no cambia porque el catálogo cambie después, ni en un reenvío ni al convertir. No
    /// toca <see cref="UpdatedAt"/>: no es una edición de la línea.
    /// </summary>
    internal void CaptureProduct(QuotationProductSnapshot product)
    {
        if (ProductCode is not null)
        {
            return;
        }

        ProductCode = product.Code;
        ProductName = product.Name;
    }

    internal void UpdateQuantity(
        decimal quantity,
        decimal discountPercentage,
        int taxPercentage,
        string currency,
        DateTimeOffset occurredAt) =>
        Apply(quantity, UnitPrice, discountPercentage, taxPercentage, currency, occurredAt);

    /// <summary>Cambia sólo el descuento y rehace los importes de la línea, conservando cantidad,
    /// precio y tasa. Lo usa el recálculo global de la cotización
    /// (<see cref="Quotation.ApplyGroupDiscounts"/>): la agrupación de escalas, el piso global y
    /// la compuerta de compra mínima mueven el descuento sin que la línea haya cambiado en nada
    /// más.</summary>
    internal void ApplyDiscount(
        decimal discountPercentage,
        QuotationDiscountOrigin origin,
        string currency,
        DateTimeOffset occurredAt)
    {
        DiscountOrigin = origin;
        Apply(Quantity, UnitPrice, discountPercentage, TaxPercentage, currency, occurredAt);
    }

    /// <summary>Vuelve a nacer con el precio del producto en otra moneda, sin tocar la
    /// cantidad. El descuento y el impuesto también se rehacen: la escala de cantidad y la
    /// tasa se resuelven contra el catálogo junto con el precio, y conservar los viejos
    /// mezclaría dos monedas dentro de una misma línea.</summary>
    internal void Reprice(
        QuotationItemPricing pricing, string currency, DateTimeOffset occurredAt) =>
        Apply(
            Quantity,
            pricing.UnitPrice,
            pricing.DiscountPercentage,
            pricing.TaxPercentage,
            currency,
            occurredAt);

    private void Apply(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        string currency,
        DateTimeOffset occurredAt)
    {
        if (quantity <= 0)
        {
            throw new QuotationsDomainException(
                "quotation.item.quantity_invalid",
                "The item quantity must be greater than zero.");
        }

        if (unitPrice < 0)
        {
            throw new QuotationsDomainException(
                "quotation.item.unit_price_negative",
                "The item unit price cannot be negative.");
        }

        if (discountPercentage < 0 || discountPercentage > 100)
        {
            throw new QuotationsDomainException(
                "quotation.item.discount_out_of_range",
                "The item discount percentage must be between 0 and 100.");
        }

        if (taxPercentage < 0 || taxPercentage > 100)
        {
            throw new QuotationsDomainException(
                "quotation.item.tax_percentage_out_of_range",
                "The item tax percentage must be between 0 and 100.");
        }

        // En COP el precio con IVA se cobra en pesos enteros, el de lista y el ya descontado
        // (hotfix 2026-10-14): la regla y su motivo viven en VatIncludedLine, compartida con POS.
        unitPrice = VatIncludedLine.ChargedListPrice(unitPrice, currency);

        // Fuera de COP el IVA no se discrimina (hotfix 2026-10-14; en main era solo USD, develop
        // ya maneja cualquier moneda del catalogo). El precio se cobra completo, como si trajera
        // el IVA adentro, pero la linea no lo separa: tasa 0, impuesto 0 y la base es lo cobrado
        // entero. Se pisa la tasa y no solo el monto para que UnitPriceWithoutTax, el
        // Excel de pedidos ("Tasa IVA", "Valor Unit sin IVA") y la pantalla cuenten la misma
        // historia. La tasa del producto no se pierde: al volver a COP, Reprice la trae del
        // catalogo junto con el precio en pesos.
        if (!ItemizesTaxIn(currency))
        {
            taxPercentage = 0;
        }

        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercentage = discountPercentage;
        TaxPercentage = taxPercentage;

        // La fórmula vive en BuildingBlocks (spec 2026-10-07): POS la comparte para cobrar al
        // centavo lo mismo que una cotización detal. Las validaciones y los códigos
        // quotation.item.* siguen aquí.
        var amounts = VatIncludedLine.Compute(
            quantity, unitPrice, discountPercentage, taxPercentage, currency);
        DiscountAmount = amounts.DiscountAmount;
        TaxAmount = amounts.TaxAmount;
        Subtotal = amounts.Subtotal;
        UpdatedAt = occurredAt;
    }

    /// <summary>Si en esa moneda el IVA se discrimina. Solo en COP, que es donde el IVA existe: en
    /// USD y en cualquier otra moneda del catalogo el precio se cobra completo y la linea no separa
    /// el impuesto (hotfix 2026-10-14). Misma regla que <c>itemizesTaxIn</c> en el frontend.</summary>
    internal static bool ItemizesTaxIn(string currency) => currency == TaxItemizedCurrency;

    private const string TaxItemizedCurrency = "COP";

    private static decimal Round(decimal value) => VatIncludedLine.Round(value);
}
