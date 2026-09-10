// Plantilla Typst de la cotización. Viaja entera al servicio `qcode-pdf` en cada request:
// ese servicio es genérico y no guarda plantillas. Los datos llegan aparte, en `data.json`,
// y son la serializacion de `QuotationPdfDocument` en camelCase.
//
// La estructura sigue la de una cotización real de mayorista que el owner fijó como referencia:
// emisor arriba a la izquierda, datos del documento a la derecha, condiciones de pago en un
// recuadro propio, tabla con banda oscura, y el bloque de totales a la derecha rematado por el
// total resaltado. Esa arquitectura de información no es estética: es la que el comprador ya
// sabe leer.
//
// El documento es white-label —lo manda el tenant a SU cliente y el payload no trae ni logo ni
// color de marca— así que la referencia se sigue en estructura y no en paleta: una sola tinta
// neutra, sin acento de color que le ponga la marca de nadie a un documento comercial ajeno.
#let data = json(sys.inputs.data)

#let tinta = rgb("#14181F")
// 5.8:1 sobre blanco. Los rótulos van en 7pt y a esa medida un gris "de diseño" deja de leerse.
#let apagado = rgb("#5F6672")
#let filete = rgb("#E3E5E9")
#let panel = rgb("#F5F6F8")
#let realce = rgb("#E6E9ED")

// Liberation Sans la instala la imagen de `qcode-pdf` (`apk add ttf-liberation`), a propósito
// por ser metric-compatible con Arial. No está embebida en Typst, así que la lista de respaldo
// no es decorativa: sin ella, una imagen que dejara de instalarla sacaría el documento con la
// serif por defecto y nadie se enteraría. Arial y Helvetica comparten métricas, con lo que el
// respaldo no recompone el documento.
#set text(
  font: ("Liberation Sans", "Arial", "Helvetica"),
  size: 9pt,
  fill: tinta,
  lang: "es",
  // Cifras tabulares en todo el documento: en una tabla de importes las columnas se alinean
  // por dígito, no por casualidad.
  number-width: "tabular",
)
#set par(leading: 0.62em, spacing: 0.62em)

// ---------------------------------------------------------------------------- formato

#let meses = (
  "enero", "febrero", "marzo", "abril", "mayo", "junio",
  "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
)

// Las fechas llegan en ISO porque el contrato es JSON; el documento lo lee una persona, así
// que se arma acá y no en C#. `createdAt` viene con hora y offset (`2026-09-06T10:00:00+00:00`)
// y `validUntil` sin hora: cortar en la "T" cubre las dos.
#let fecha(iso) = {
  let partes = iso.split("T").at(0).split("-")
  str(int(partes.at(2))) + " de " + meses.at(int(partes.at(1)) - 1) + " de " + partes.at(0)
}

// El separador de miles se arma a mano: Typst no tiene formato por locale.
#let miles(valor) = {
  let entero = int(valor)
  let signo = if entero < 0 { "-" } else { "" }
  let texto = str(calc.abs(entero))
  let partes = ()
  while texto.len() > 3 {
    partes.insert(0, texto.slice(texto.len() - 3))
    texto = texto.slice(0, texto.len() - 3)
  }
  partes.insert(0, texto)
  signo + partes.join(".")
}

// Monedas sin fracción de uso corriente. La distinción importa: redondear a entero está bien
// en pesos y es plata mal dicha en dólares — un unitario de 12,50 impreso como 13 no cierra
// contra su propia línea, y esa diferencia la descubre el cliente con la calculadora.
#let sin-decimales = ("COP", "CLP", "PYG", "JPY", "KRW", "VND", "ISK")

#let importe(valor) = {
  if data.currency in sin-decimales {
    miles(calc.round(valor))
  } else {
    let centavos = int(calc.round(valor * 100))
    let resto = calc.abs(calc.rem(centavos, 100))
    let decimales = if resto < 10 { "0" + str(resto) } else { str(resto) }
    miles(calc.quo(centavos, 100)) + "," + decimales
  }
}

// Las cantidades pueden ser fraccionarias (metros, kilos) pero casi nunca lo son: imprimir
// "12,00" donde va "12" mete ruido en la columna que se lee de un vistazo.
#let cantidad(valor) = {
  let centesimas = int(calc.round(valor * 100))
  let entero = miles(calc.quo(centesimas, 100))
  let resto = calc.abs(calc.rem(centesimas, 100))
  if resto == 0 {
    entero
  } else if calc.rem(resto, 10) == 0 {
    entero + "," + str(calc.quo(resto, 10))
  } else if resto < 10 {
    entero + ",0" + str(resto)
  } else {
    entero + "," + str(resto)
  }
}

#let porcentaje(valor) = {
  if valor == int(valor) { str(int(valor)) } else { str(valor).replace(".", ",") }
}

// ---------------------------------------------------------------------------- piezas

#let rotulo(texto, color: apagado) = text(
  size: 7pt, weight: "bold", fill: color, tracking: 0.09em,
)[#upper(texto)]

#let vacio = text(fill: apagado)[—]

// Teléfono y correo llegan unidos con " · " desde C#, que es la forma correcta en una línea
// ancha. En una columna angosta ese separador queda colgando al final del renglón y el correo
// arranca solo abajo, así que acá se abren en líneas.
#let contacto(texto) = texto.split(" · ").map(parte => [#parte]).join(linebreak())

// Un renglón de la ficha del documento: rótulo a la izquierda, dato a la derecha.
#let ficha(etiqueta, valor) = (
  text(size: 7.5pt, fill: apagado)[#upper(etiqueta)],
  [#valor],
)

// ---------------------------------------------------------------------------- página

// El pie repite el número en cada hoja: una cotización de varias páginas se imprime, se separa
// y se vuelve a juntar, y sin eso la hoja 2 no dice de qué documento es.
#set page(
  paper: "a4",
  margin: (x: 18mm, top: 15mm, bottom: 17mm),
  footer: context [
    #line(length: 100%, stroke: 0.4pt + filete)
    #v(4pt)
    #grid(
      columns: (1fr, auto),
      text(size: 7.5pt, fill: apagado)[
        Cotización #data.quotationNumber#if data.billingAccount != none [ · #data.billingAccount.companyName]
      ],
      text(size: 7.5pt, fill: apagado)[
        Página #counter(page).display() de #counter(page).final().first()
      ],
    )
  ],
)

// ---------------------------------------------------------------------------- encabezado

// El emisor ancla arriba a la izquierda, como en la referencia. Sin logo en el payload, el peso
// tipográfico de la razón social es lo único que sostiene esa esquina.
// Sin cuenta de cobro no hay emisor que imprimir, y entonces el ancla de la esquina es el tipo
// de documento. Con emisor, "Cotización" pasa a ser el rótulo del número en la ficha: decirlo
// en los dos lados es decirlo dos veces.
#let hay-emisor = data.billingAccount != none

#grid(
  columns: (1fr, auto),
  column-gutter: 10mm,
  align: (left + top, right + top),
  if hay-emisor [
    #text(size: 14pt, weight: "bold", tracking: -0.01em)[#data.billingAccount.companyName]
    #if data.billingAccount.companyTaxId != none [
      \ #text(size: 9pt, fill: apagado)[NIT #data.billingAccount.companyTaxId]
    ]
  ] else [
    #text(size: 14pt, weight: "bold", tracking: -0.01em)[Cotización]
  ],
  block[
    #grid(
      columns: (auto, auto),
      column-gutter: 7mm,
      row-gutter: 4pt,
      align: (left, right),
      if hay-emisor { text(size: 7.5pt, fill: apagado)[COTIZACIÓN] } else { [] },
      text(size: 12pt, weight: "bold")[#data.quotationNumber],
      ..ficha("Emitida", fecha(data.createdAt)),
      ..ficha(
        "Válida hasta",
        if data.validUntil == none { text(fill: apagado)[Sin vencimiento] } else { fecha(data.validUntil) },
      ),
      ..ficha("Asesor", if data.advisorLabel == "" { vacio } else { data.advisorLabel }),
      ..ficha("Cliente", if data.customerName == "" { vacio } else { data.customerName }),
      ..if data.customerCuc != "" { ficha("NIT / CUC", data.customerCuc) } else { () },
      ..if data.customerContact != "" { ficha("Contacto", data.customerContact) } else { () },
    )
  ],
)

#v(12pt)

// ---------------------------------------------------------------------------- pago

// El recuadro de condiciones de pago va arriba y no al pie, como en la referencia: es lo que el
// comprador necesita antes de decidir, no una nota final.
#if data.paymentMethod != none or data.billingAccount != none [
  #block(
    breakable: false,
    width: 100%,
    stroke: 0.8pt + tinta,
    inset: 10pt,
  )[
    #grid(
      columns: (1fr, 1.5fr),
      column-gutter: 8mm,
      if data.paymentMethod != none [
        #rotulo("Forma de pago")
        #v(3pt, weak: true)
        #data.paymentMethod
      ] else [],
      if data.billingAccount != none [
        #rotulo("Cuenta para el pago")
        #v(3pt, weak: true)
        #data.billingAccount.companyName \
        #text(weight: "bold")[
          #data.billingAccount.bankName · #data.billingAccount.accountNumber
          (#data.billingAccount.currency)
        ]
      ] else [],
    )
  ]
  #v(12pt)
]

// ---------------------------------------------------------------------------- partes

// Sólo cuando alguna parte tiene datos propios. Si las dos son "los mismos datos del cliente",
// la banda entera desaparece en vez de repetir por tercera vez lo que la ficha ya dice.
#let parte(valor) = [
  #valor.name
  #if valor.contact != "" [\ #contacto(valor.contact)]
  #if valor.location != "" [\ #valor.location]
]

#if not data.billing.sameAsCustomer or not data.shipping.sameAsCustomer [
  #grid(
    columns: (1fr, 1fr),
    column-gutter: 10mm,
    [
      #rotulo("Facturar a")
      #v(3pt, weak: true)
      #if data.billing.sameAsCustomer {
        text(fill: apagado, style: "italic")[Los mismos datos del cliente]
      } else {
        parte(data.billing)
      }
    ],
    [
      #rotulo("Entregar en")
      #v(3pt, weak: true)
      #if data.shipping.sameAsCustomer {
        text(fill: apagado, style: "italic")[Los mismos datos del cliente]
      } else {
        parte(data.shipping)
      }
    ],
  )
  #v(14pt)
]

// ---------------------------------------------------------------------------- ítems

// La columna de valor con descuento sólo aparece si alguna línea lo tiene: sin descuentos sería
// idéntica al valor público, y una columna que repite a la de al lado es ruido en la tabla que
// el cliente lee para decidir.
#let hay-descuento = data.items.any(item => item.discountPercentage > 0)

#let columnas = if hay-descuento {
  (1fr, auto, auto, auto, auto)
} else {
  (1fr, auto, auto, auto)
}
#let titulos = if hay-descuento {
  ("Producto", "Valor público", "Valor con descuento", "Cantidad", "Subtotal")
} else {
  ("Producto", "Valor unitario", "Cantidad", "Subtotal")
}

#table(
  columns: columnas,
  align: (left, ..titulos.slice(1).map(_ => right)),
  inset: (x: 8pt, y: 7.5pt),
  fill: (x, y) => if y == 0 { tinta },
  stroke: (x, y) => (bottom: if y == 0 { none } else { 0.4pt + filete }),
  table.header(..titulos.map(titulo => rotulo(titulo, color: white))),
  ..data.items
    .map(item => {
      let celdas = ([#item.productName],)
      if hay-descuento {
        // El valor público tachado marca de dónde bajó el precio; el efectivo va al lado.
        celdas.push(
          if item.discountPercentage > 0 {
            text(fill: apagado)[#strike[#importe(item.unitPrice)]]
          } else {
            [#importe(item.unitPrice)]
          },
        )
        celdas.push([#importe(item.discountedUnitPrice)])
      } else {
        celdas.push([#importe(item.unitPrice)])
      }
      celdas.push([#cantidad(item.quantity)])
      celdas.push([#importe(item.lineTotal)])
      celdas
    })
    .flatten()
)

// ---------------------------------------------------------------------------- notas

// Va entre la tabla y los totales, como en la cotizacion de referencia: son las condiciones
// que califican los precios de arriba, y leerlas despues del total es leerlas tarde.
//
// Acotado a 130mm y no al ancho de la caja: a 9pt, los 174mm del texto corrido dan renglones de
// ~110 caracteres y el ojo pierde el salto de línea. Las observaciones son el único párrafo
// largo del documento, así que es el único bloque que necesita medida propia.
#if data.notes != none [
  #v(14pt)
  #rotulo("Observaciones")
  #v(4pt, weak: true)
  #block(width: 130mm)[#data.notes]
]

#v(14pt)

// ---------------------------------------------------------------------------- totales

// Sin corte de página en el medio: un total separado de sus sumandos se lee como otra cosa.
// Peso de cada renglón: 0 normal, 1 fuerte, 2 remate. El remate marca siempre la **última**
// cifra que importa, que no siempre es el total: con retención, lo que el cliente gira es el
// neto.
//
// La retención va **después** del total y no antes. El orden importa: total es lo facturado y
// la retención se calcula sobre eso (`Total - Retención = Neto`), así que ponerla arriba
// sugiere que se resta para llegar al total, que es falso. Con retención en cero el renglón no
// aparece: una fila que dice "0" en el lugar equivocado no informa, confunde.
#let hay-retencion = data.retentionAmount > 0

#let renglones = {
  let filas = (
    ("Valor antes de IVA", importe(data.subtotal), 0),
    (
      if data.customerVatSurplus { "Total IVA · excedente de IVA" } else { "Total IVA" },
      importe(data.taxAmount),
      0,
    ),
    ("Total inversión (" + data.currency + ")", importe(data.total), if hay-retencion { 1 } else { 2 }),
  )

  if hay-retencion {
    filas.push(("Retención en la fuente", "-" + importe(data.retentionAmount), 0))
    filas.push(("Neto a pagar (" + data.currency + ")", importe(data.netTotal), 2))
  }

  filas
}

#block(breakable: false, width: 100%)[
  #grid(
    columns: (1fr, auto),
    column-gutter: 10mm,
    align: (left + bottom, right),
    // Lo que el mayorista gana si revende a valor público: la suma de los descuentos de línea.
    // Es un argumento de venta, y por eso vive al lado del total y no escondido en la tabla.
    if data.discountAmount > 0 [
      #text(size: 8pt, fill: apagado, style: "italic")[
        Ahorro sobre el valor público \
      ]
      #text(size: 11pt, weight: "bold")[#data.currency #importe(data.discountAmount)]
    ] else [],
    block(width: 82mm)[
      #table(
        columns: (1fr, auto),
        align: (left, right),
        inset: (x: 9pt, y: 6.5pt),
        stroke: none,
        fill: (x, y) => if renglones.at(y).at(2) == 2 { realce } else { panel },
        ..renglones
          .map(fila => {
            let peso = fila.at(2)
            (
              if peso == 0 {
                text(size: 8.5pt, fill: apagado)[#upper(fila.at(0))]
              } else {
                text(size: 9pt, weight: "bold")[#upper(fila.at(0))]
              },
              if peso == 2 {
                text(size: 12pt, weight: "bold")[#fila.at(1)]
              } else if peso == 1 {
                text(size: 9.5pt, weight: "bold")[#fila.at(1)]
              } else {
                [#fila.at(1)]
              },
            )
          })
          .flatten()
      )
    ],
  )
]
