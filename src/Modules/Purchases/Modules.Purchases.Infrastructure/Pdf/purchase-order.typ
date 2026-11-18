// Plantilla Typst de la orden de compra. Viaja entera a `qcode-pdf` en cada request: el servicio es genérico y no guarda
// plantillas. Los datos llegan aparte, en el JSON de entrada: la serialización de `PurchaseOrderPdfDocument` en camelCase. Montos,
// cantidades y fechas llegan ya formateados con el formato del tenant: aquí no se calcula nada.
//
// Todo texto que llega en los datos se imprime como texto, nunca como marcado: un `#` o un `[` en el nombre de un producto o en las
// notas sale tal cual. Por eso aquí no hay `eval` y los textos entran sólo por `#valor`.
//
// Orden: encabezado con la empresa y el número, bloque del proveedor, tabla Código/Producto/Cantidad/Costo/IVA/Subtotal, totales y
// notas.
#let data = json(sys.inputs.data)

#let tinta = rgb("#14181F")
// 5.8:1 sobre blanco: a 7pt un gris más claro deja de leerse.
#let apagado = rgb("#5F6672")
#let filete = rgb("#E3E5E9")
#let panel = rgb("#F5F6F8")

// Liberation Sans la instala la imagen de `qcode-pdf`; la lista de respaldo evita que, sin ella, el documento salga en la serif por
// defecto sin que nadie se entere (mismo criterio que la plantilla de cotizaciones).
#set text(
  font: ("Liberation Sans", "Arial", "Helvetica"),
  size: 9pt,
  fill: tinta,
  lang: "es",
  number-width: "tabular",
)
#set par(leading: 0.62em, spacing: 0.62em)

#let rotulo(texto) = text(size: 7pt, weight: "bold", fill: apagado, tracking: 0.08em)[#upper(texto)]

// Un texto libre con saltos de línea: cada línea entra como texto y los saltos se respetan.
#let parrafos(texto) = texto.replace("\r", "").split("\n").map(linea => [#linea]).join(linebreak())

// El pie repite el número en cada hoja: una orden de varias páginas se imprime y se separa.
#set page(
  paper: "a4",
  margin: (x: 16mm, top: 16mm, bottom: 18mm),
  footer: context [
    #line(length: 100%, stroke: 0.4pt + filete)
    #v(4pt)
    #grid(
      columns: (1fr, auto),
      text(size: 7pt, fill: apagado)[Orden de compra #data.number · #data.tenantName],
      text(size: 7pt, fill: apagado)[Página #counter(page).display() de #counter(page).final().first()],
    )
  ],
)

// ---------------------------------------------------------------------------- encabezado

#grid(
  columns: (1fr, auto),
  column-gutter: 10mm,
  align: (left + top, right + top),
  [
    #text(size: 13pt, weight: "bold")[#data.tenantName]
  ],
  [
    #text(size: 13pt, weight: "bold")[Orden de compra]
    #v(2pt, weak: true)
    #text(size: 11pt)[#data.number]
    #v(6pt, weak: true)
    #grid(
      columns: (auto, auto),
      column-gutter: 6pt,
      row-gutter: 4pt,
      align: (right, left),
      rotulo("Fecha"), [#data.issuedOn],
      rotulo("Estado"), [#data.statusLabel],
      rotulo("Moneda"), [#data.currency],
    )
  ],
)

#v(10pt)
#line(length: 100%, stroke: 0.6pt + filete)
#v(8pt)

// ---------------------------------------------------------------------------- proveedor

#block(fill: panel, inset: 8pt, radius: 2pt, width: 100%)[
  #rotulo("Proveedor")
  #v(3pt, weak: true)
  #text(weight: "bold")[#data.supplierName]
  #if data.supplierIdentification != "" [
    #linebreak()
    #text(fill: apagado)[#data.supplierIdentification]
  ]
]

#v(10pt)

// ---------------------------------------------------------------------------- líneas

#table(
  columns: (auto, 1fr, auto, auto, auto, auto),
  stroke: (x, y) => if y == 0 { none } else { (bottom: 0.4pt + filete) },
  fill: (x, y) => if y == 0 { tinta } else { none },
  inset: (x: 5pt, y: 5pt),
  align: (x, y) => if x >= 2 { right + horizon } else { left + horizon },
  table.header(
    text(fill: white, weight: "bold")[Código],
    text(fill: white, weight: "bold")[Producto],
    text(fill: white, weight: "bold")[Cantidad],
    text(fill: white, weight: "bold")[Costo],
    text(fill: white, weight: "bold")[IVA],
    text(fill: white, weight: "bold")[Subtotal],
  ),
  ..data.lines.map(fila => (
    [#fila.code],
    [#fila.name],
    [#fila.quantity],
    [#fila.unitCost],
    [#fila.taxRate],
    [#fila.subtotal],
  )).flatten(),
)

#if data.showsConfirmedValues [
  #v(4pt)
  #text(size: 7.5pt, fill: apagado)[Cantidades y costos confirmados por el proveedor.]
]

// ---------------------------------------------------------------------------- totales

#v(8pt)
#align(right)[
  #grid(
    columns: (auto, auto),
    column-gutter: 14pt,
    row-gutter: 5pt,
    align: (right, right),
    rotulo("Subtotal"), [#data.subtotal],
    rotulo("IVA"), [#data.tax],
    rotulo("Total"), text(size: 11pt, weight: "bold")[#data.total],
  )
]

// ---------------------------------------------------------------------------- notas

#if data.notes != none and data.notes != "" [
  #v(12pt)
  #rotulo("Notas")
  #v(3pt, weak: true)
  #parrafos(data.notes)
]
