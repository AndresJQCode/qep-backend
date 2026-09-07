// Plantilla Typst de la cotización. Viaja entera al servicio `qcode-pdf` en cada request:
// ese servicio es genérico y no guarda plantillas. Los datos llegan aparte, en `data.json`,
// y son la serializacion de `QuotationPdfDocument` en camelCase.
#let data = json(sys.inputs.data)

#set page(paper: "a4", margin: (x: 18mm, y: 16mm))
// Liberation Sans la instala la imagen de `qcode-pdf` (`apk add ttf-liberation`), a
// propósito por ser metric-compatible con Arial. No está embebida en Typst: si la imagen
// dejara de instalarla, el documento saldría con la serif por defecto y nadie se enteraría.
#set text(size: 9pt, font: "Liberation Sans")

#let meses = (
  "enero", "febrero", "marzo", "abril", "mayo", "junio",
  "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
)

// Las fechas llegan en ISO (`2026-09-30`) porque el contrato es JSON; el documento lo lee
// una persona, así que se arma acá y no en C#.
#let fecha(iso) = {
  let partes = iso.split("-")
  str(int(partes.at(2))) + " de " + meses.at(int(partes.at(1)) - 1) + " de " + partes.at(0)
}

#let money(value) = {
  // El separador de miles se arma a mano: Typst no tiene formato por locale.
  let entero = calc.round(value)
  let texto = str(entero)
  let partes = ()
  while texto.len() > 3 {
    partes.insert(0, texto.slice(texto.len() - 3))
    texto = texto.slice(0, texto.len() - 3)
  }
  partes.insert(0, texto)
  data.currency + " " + partes.join(".")
}

#grid(
  columns: (1fr, auto),
  align(left, text(size: 15pt, weight: "bold")[Cotización #data.quotationNumber]),
  align(right)[
    Válida hasta: #if data.validUntil == none [sin vencimiento] else [#fecha(data.validUntil)] \
    Asesor: #data.advisorLabel
  ],
)

#line(length: 100%, stroke: 0.5pt)
#v(4pt)

*Cliente* \
#data.customerName #if data.customerCuc != "" [ (#data.customerCuc)] \
#data.customerContact \
#data.customerLocation

#v(8pt)

#table(
  columns: (1fr, auto, auto, auto),
  align: (left, right, right, right),
  stroke: 0.4pt,
  table.header([*Producto*], [*Cant.*], [*Unitario*], [*Subtotal*]),
  ..data.items
    .map(item => (
      [#item.productName],
      [#item.quantity],
      [#money(item.unitPrice)],
      [#money(item.subtotal)],
    ))
    .flatten()
)

#v(6pt)
#align(right)[
  #table(
    columns: (auto, auto),
    align: (left, right),
    stroke: none,
    [Subtotal], [#money(data.subtotal)],
    [Descuento], [#money(data.discountAmount)],
    [IVA (#data.taxPercentage%)], [#money(data.taxAmount)],
    [*Total*], [*#money(data.total)*],
  )
]

#if data.paymentMethod != none [
  #v(6pt)
  *Forma de pago:* #data.paymentMethod
]

#if data.notes != none [
  #v(6pt)
  *Observaciones:* #data.notes
]
