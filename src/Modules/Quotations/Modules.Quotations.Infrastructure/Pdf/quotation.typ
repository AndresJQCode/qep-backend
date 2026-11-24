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
// El logo del tenant viaja en el payload como asset (spec 2026-09-19); el color de marca sigue
// fuera de alcance. La referencia se sigue en estructura y no en paleta: una sola tinta neutra,
// sin acento de color que le ponga la marca de nadie a un documento comercial ajeno.
#let data = json(sys.inputs.data)

// Un solo layout para cotización y pedido (decisión del owner). Lo único que cambia es el nombre del
// documento —membrete y pie— y el renglón de vigencia, que un pedido no tiene. `kind` llega como texto
// ("Quotation" / "Order", `QuotationPdfKind`); cualquier otro valor se imprime como cotización.
#let es-pedido = data.kind == "Order"
// Spec 2026-11-22 (i18n): el idioma del tenant. En España la cotización es «Presupuesto» —masculino, como el pedido— y el
// asesor, «Comercial» (owner). Cualquier otro valor se imprime como Latinoamérica.
#let espana = data.language == "es-ES"
#let documento = if es-pedido { "Pedido" } else if espana { "Presupuesto" } else { "Cotización" }
#let masculino = es-pedido or espana

#let tinta = rgb("#14181F")
// 5.8:1 sobre blanco. Los rótulos van en 7pt y a esa medida un gris "de diseño" deja de leerse.
#let apagado = rgb("#5F6672")
#let filete = rgb("#E3E5E9")
#let panel = rgb("#F5F6F8")
// Celeste neutro para el remate del total, como en la referencia: no es el color de marca de
// ningún tenant, es el mismo acento para todos — sólo marca la última cifra que importa.
#let realce = rgb("#DCE9F7")

// Acentos fijos de la plantilla, iguales para todos los tenants — no son el color de marca de
// nadie, son el estilo estándar del documento, como en la referencia: naranja para lo comercial
// (la tabla de productos, el aviso de pago y el resultado de esa tabla) y azul rey sólo para el
// remate de los totales, la única cifra que el documento quiere que salte por encima de todo lo
// demás. El resto del texto administrativo (ficha del documento, condiciones de pago, el resto
// de los totales) se queda en la tinta neutra.
#let naranja = rgb("#C0392B")
#let azul-rey = rgb("#0047AB")

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
// que se arma acá y no en C#. `issuedOn` y `validUntil` llegan sin hora (`2026-09-06`): la
// emisión ya viene en el día del tenant (spec 2026-09-17, punto 7), y no hay nada que cortar.
#let fecha(iso) = {
  let partes = iso.split("-")
  str(int(partes.at(2))) + " de " + meses.at(int(partes.at(1)) - 1) + " de " + partes.at(0)
}

// El separador de miles se arma a mano: Typst no tiene formato por locale. Los separadores
// (miles y decimal) llegan en `data` desde el formato numérico del tenant (spec 2026-10-23).
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
  signo + partes.join(data.thousandsSeparator)
}

// Los decimales los manda el mapper desde el catálogo de monedas (spec 2026-10-08): 0 en pesos,
// 2 en dólares y euros. Redondear a entero está bien en pesos y es plata mal dicha en dólares.
#let importe(valor) = {
  if data.decimals == 0 {
    miles(calc.round(valor))
  } else {
    let centavos = int(calc.round(valor * 100))
    let resto = calc.abs(calc.rem(centavos, 100))
    let decimales = if resto < 10 { "0" + str(resto) } else { str(resto) }
    miles(calc.quo(centavos, 100)) + data.decimalSeparator + decimales
  }
}

#let moneda = data.symbol + " "

// Las cantidades pueden ser fraccionarias (metros, kilos) pero casi nunca lo son: imprimir
// "12,00" donde va "12" mete ruido en la columna que se lee de un vistazo.
#let cantidad(valor) = {
  let centesimas = int(calc.round(valor * 100))
  let entero = miles(calc.quo(centesimas, 100))
  let resto = calc.abs(calc.rem(centesimas, 100))
  if resto == 0 {
    entero
  } else if calc.rem(resto, 10) == 0 {
    entero + data.decimalSeparator + str(calc.quo(resto, 10))
  } else if resto < 10 {
    entero + data.decimalSeparator + "0" + str(resto)
  } else {
    entero + data.decimalSeparator + str(resto)
  }
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

// Un renglón de la ficha del documento: "Etiqueta: dato" en una sola línea, como en la
// referencia — no una tabla de rótulo/valor, sino el formato de formulario que el comprador
// ya lee en la cotización que el owner fijó.
#let renglon(etiqueta, valor) = [#etiqueta: #valor]

// ---------------------------------------------------------------------------- secciones

// Las secciones que configura el tenant (aviso de pago, términos, proceso de entrega; spec 2026-11-16 §5.3) llegan como un árbol
// de bloques ya resuelto en C#, nunca como markup. El texto de cada tramo se inserta como cadena (`[#run.text]`): un `#`, un `*`,
// un `$` o un `[` que escriba el tenant sale literal. Nada de lo que trae `data` pasa por una evaluación ni se convierte en markup.
#let runs(lista) = {
  for run in lista {
    if run.break {
      linebreak()
    } else {
      let tramo = [#run.text]
      if run.bold { tramo = strong(tramo) }
      if run.italic { tramo = emph(tramo) }
      if run.underline { tramo = underline(tramo) }
      if run.strike { tramo = strike(tramo) }
      // El saneador sólo deja `https`, `mailto` y `tel`: el enlace llega ya filtrado.
      if run.href != none { tramo = link(run.href, tramo) }
      tramo
    }
  }
}

// Los títulos del contenido son texto en negrita y no `heading()`: un `heading` entraría al esquema del PDF y a la numeración, y
// el documento no tiene índice. El tamaño baja con el nivel (2, 3, 4).
#let bloques(lista) = {
  for bloque in lista {
    if bloque.kind == "paragraph" {
      par[#runs(bloque.runs)]
    } else if bloque.kind == "heading" {
      let tamano = if bloque.level == 2 { 10pt } else if bloque.level == 3 { 9.5pt } else { 9pt }
      par(text(weight: "bold", size: tamano)[#runs(bloque.runs)])
    } else if bloque.kind == "list" {
      // Cada ítem es a su vez una lista de bloques: así una lista se anida dentro de otra.
      let elementos = bloque.items.map(elemento => [#bloques(elemento)])
      if bloque.ordered { enum(..elementos) } else { list(..elementos) }
    } else if bloque.kind == "quote" {
      pad(left: 8pt, text(fill: apagado)[#bloques(bloque.children)])
    } else if bloque.kind == "rule" {
      line(length: 100%, stroke: 0.4pt + filete)
    }
  }
}

// Un documento sin secciones trae `data.sections` en `none`; cada sección, en `none` si no sale en este tipo de documento.
#let aviso = if data.sections == none { none } else { data.sections.paymentNotice }
#let terminos = if data.sections == none { none } else { data.sections.terms }
#let proceso = if data.sections == none { none } else { data.sections.deliveryProcess }
// El tenant decide por tipo de documento si salen las líneas de la cuenta para el pago (spec 2026-11-16 pdf-payment-account).
#let mostrar-cuenta = data.sections != none and data.sections.showPaymentAccount
// Una empresa sin cuenta (spec 2026-11-17 §4.2) trae `bankName` en `none`: no hay a dónde pagar y el bloque no sale; el
// membrete sigue.
#let cuenta-visible = data.billingAccount != none and data.billingAccount.bankName != none and mostrar-cuenta

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
        #documento #data.quotationNumber#if data.billingAccount != none [ · #data.billingAccount.companyName]
      ],
      text(size: 7.5pt, fill: apagado)[
        Página #counter(page).display() de #counter(page).final().first()
      ],
    )
  ],
)

// ---------------------------------------------------------------------------- encabezado

// El emisor ancla arriba a la izquierda, como en la referencia: logo, razón social, NIT,
// dirección y teléfono, en ese orden. El logo es el que sube el tenant en `/settings` y viaja
// por el canal `assets` de `qcode-pdf`, no un archivo del despliegue: sin logo subido, el
// membrete se imprime sin imagen.
// Sin cuenta de cobro no hay emisor que imprimir, y entonces el ancla de la esquina es el tipo
// de documento. Con emisor, "Cotización" pasa a ser el encabezado del renglón de número a la
// derecha: decirlo en los dos lados es decirlo dos veces.
#let hay-emisor = data.billingAccount != none

#grid(
  columns: (1fr, auto),
  column-gutter: 10mm,
  align: (left + top, right + top),
  [
    // Acotado por alto y por ancho: el archivo lo sube el tenant, así que su relación de
    // aspecto es desconocida y un logo alto y angosto fijado sólo por ancho empuja el resto
    // del membrete hacia abajo. `fit: "contain"` lo mete dentro de la caja sin deformarlo.
    #if data.logo != none [
      #image("assets/" + data.logo.fileName, width: 34mm, height: 16mm, fit: "contain")
      #v(5pt, weak: true)
    ]
    #if hay-emisor [
      #text(size: 13pt, weight: "bold", tracking: -0.01em)[#data.billingAccount.companyName]
      #if data.billingAccount.companyTaxId != none [
        \ #text(size: 9pt, fill: apagado)[#data.billingAccount.companyDocumentLabel #data.billingAccount.companyTaxId]
      ]
      #if data.billingAccount.companyAddress != "" [
        \ #text(size: 8.5pt, fill: apagado)[Dirección empresa: #data.billingAccount.companyAddress]
      ]
      #if data.billingAccount.companyPhone != "" [
        \ #text(size: 8.5pt, fill: apagado)[Teléfono: #data.billingAccount.companyPhone]
      ]
    ] else [
      #text(size: 14pt, weight: "bold", tracking: -0.01em)[#documento]
    ]
  ],
  block[
    #if hay-emisor [
      #text(size: 12pt, weight: "bold")[#documento]
    ] else [
      #text(size: 12pt, weight: "bold")[#data.quotationNumber]
    ]
    #v(3pt, weak: true)
    #par(leading: 0.6em)[
      #if hay-emisor [#renglon("N.°", data.quotationNumber) \ ]
      // Concuerda con el documento: «la cotización», emitida; «el pedido» y «el presupuesto», emitido.
      #renglon(if masculino { "Emitido" } else { "Emitida" }, fecha(data.issuedOn)) \
      // Un pedido no vence: "Sin vencimiento" se leería como una condición comercial que nadie pactó.
      #if not es-pedido [
        #renglon(
          if masculino { "Válido hasta" } else { "Válida hasta" },
          if data.validUntil == none { text(fill: apagado)[Sin vencimiento] } else { fecha(data.validUntil) },
        ) \
      ]
      #renglon(if espana { "Comercial" } else { "Asesor" }, if data.advisorLabel == "" { vacio } else { data.advisorLabel }) \
      #renglon("Cliente", if data.customerName == "" { vacio } else { data.customerName })
      #if data.customerCuc != "" [ \ #renglon("NIT / CUC", data.customerCuc)]
      #if data.customerContact != "" [ \ #renglon("Contacto", data.customerContact)]
    ]
  ],
)

#v(12pt)

// ---------------------------------------------------------------------------- pago

// El recuadro de condiciones de pago va arriba y no al pie, como en la referencia: es lo que el
// comprador necesita antes de decidir, no una nota final.
#let hay-recuadro = data.paymentMethod != none or data.cashOnDelivery or cuenta-visible
#if hay-recuadro [
  #block(
    breakable: false,
    width: 100%,
    stroke: 0.8pt + tinta,
    inset: 10pt,
  )[
    #par(leading: 0.6em)[
      // Contraentrega reemplaza a la forma de pago escrita a mano (spec §7): el cliente paga al
      // recibir, así que un segundo «Forma de pago:» se contradiría.
      #if data.cashOnDelivery [
        #renglon("Forma de pago", "Contraentrega") \
      ] else if data.paymentMethod != none [
        #renglon("Forma de pago", data.paymentMethod) \
      ]
      #if cuenta-visible [
        #renglon("Cuenta para el pago", data.billingAccount.companyName) \
        #data.billingAccount.bankName \
        #text(weight: "bold")[
          Número: #data.billingAccount.accountNumber (#data.billingAccount.currency)
        ]
      ]
    ]
  ]
]
// El aviso de pago del tenant, pegado al recuadro, como en la referencia. Sale por sus propias banderas aunque no haya recuadro
// (spec 2026-11-16 pdf-payment-account, decisión 4): ya no depende de la cuenta para el pago.
#if aviso != none [
  #v(6pt, weak: true)
  #text(size: 7.5pt, fill: naranja)[
    #if aviso.title != "" [#text(weight: "bold")[#aviso.title]]
    #bloques(aviso.blocks)
  ]
]
#if hay-recuadro or aviso != none [
  #v(12pt)
]

// ---------------------------------------------------------------------------- partes

// Sólo cuando alguna parte tiene datos propios o el cliente recoge en tienda. Si las dos son
// "los mismos datos del cliente", la banda entera desaparece en vez de repetir por tercera vez
// lo que la ficha ya dice. Recoger en tienda sí la muestra aunque la facturación siga al cliente:
// sin ella el documento callaría que no hay entrega, y callar se lee como "a la dirección del
// cliente".
// El NIT va en su renglón y con rótulo, no pegado al contacto. Lo trae la facturación: el fijo de
// consumidor final, que no tiene contacto ni dirección —sin el NIT, "Consumidor final" a secas no
// identifica la factura—, o el documento de una facturación a otra persona. La entrega no lo trae.
#let parte(valor) = [
  #valor.name
  #if valor.taxId != "" [\ NIT #valor.taxId]
  #if valor.contact != "" [\ #contacto(valor.contact)]
  #if valor.location != "" [\ #valor.location]
]

#if data.isStorePickup or data.carrierName != none or not data.billing.sameAsCustomer or not data.shipping.sameAsCustomer [
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
      // "Entregar en: Recoger en tienda" se contradice en el mismo renglón, así que con
      // recogida el rótulo pasa a nombrar la modalidad y no un destino.
      #rotulo(if data.isStorePickup { "Entrega" } else { "Entregar en" })
      #v(3pt, weak: true)
      #if data.isStorePickup {
        text(weight: "bold")[Recoger en tienda]
      } else if data.shipping.sameAsCustomer {
        text(fill: apagado, style: "italic")[Los mismos datos del cliente]
      } else {
        parte(data.shipping)
      }
      // La transportadora no existe con recogida en tienda (el dominio la limpia).
      #if not data.isStorePickup and data.carrierName != none [
        \ #renglon("Transportadora", data.carrierName)
      ]
    ],
  )
  #v(14pt)
]

// ---------------------------------------------------------------------------- ítems

// La columna de valor con descuento sólo aparece si alguna línea lo tiene: sin descuentos sería
// idéntica al valor público, y una columna que repite a la de al lado es ruido en la tabla que
// el cliente lee para decidir.
#let hay-descuento = data.items.any(item => item.discountPercentage > 0)

// El pedido imprime otra tabla (owner 2026-11-23): # Item, Código, Descripción, Cantidad, Vr. unitario, Vr. total. El
// unitario es el que ya trae el descuento, así que unitario × cantidad = total de la fila y no hace falta una columna
// de valor público.
#let columnas = if es-pedido {
  (auto, auto, 1fr, auto, auto, auto)
} else if hay-descuento {
  (1fr, auto, auto, auto, auto)
} else {
  (1fr, auto, auto, auto)
}
#let titulos = if es-pedido {
  ("# Item", "Código", "Descripción", "Cantidad", "Vr. unitario", "Vr. total")
} else if hay-descuento {
  ("Producto", "Valor público", "Valor con descuento", "Cantidad", "Subtotal")
} else {
  ("Producto", "Valor unitario", "Cantidad", "Subtotal")
}

#table(
  columns: columnas,
  align: if es-pedido {
    (center, left, left, right, right, right)
  } else {
    (left, ..titulos.slice(1).map(_ => right))
  },
  inset: (x: 8pt, y: 7.5pt),
  // Fila 0 oscura para el encabezado; el resto alterna blanco y panel, como en la referencia
  // — la cebra ayuda a leer una fila completa en una tabla ancha, sin depender del color.
  fill: (x, y) => if y == 0 { tinta } else if calc.even(y) { panel },
  stroke: (x, y) => (bottom: if y == 0 { none } else { 0.4pt + filete }),
  table.header(..titulos.map(titulo => rotulo(titulo, color: white))),
  ..data.items
    .enumerate()
    .map(((posicion, item)) => {
      let celdas = if es-pedido {
        ([#str(posicion + 1)], [#if item.productCode == none { "" } else { item.productCode }], [#item.productName])
      } else {
        ([#item.productName],)
      }
      if es-pedido {
        celdas.push([#if item.unit == none { cantidad(item.quantity) } else { cantidad(item.quantity) + " " + item.unit }])
        celdas.push([#importe(item.discountedUnitPrice)])
        celdas.push([#importe(item.lineTotal)])
        return celdas
      }
      if hay-descuento {
        celdas.push([#importe(item.unitPrice)])
        celdas.push([#importe(item.discountedUnitPrice)])
      } else {
        celdas.push([#importe(item.unitPrice)])
      }
      // La unidad («2 horas», spec 2026-11-18 §6) llega en español y con el plural resuelto, como texto: se concatena a
      // la cadena de la cantidad y se inserta como cadena, nunca como markup. Sin unidad (todas las líneas en «unidad»)
      // la celda es la de antes.
      celdas.push([#if item.unit == none { cantidad(item.quantity) } else { cantidad(item.quantity) + " " + item.unit }])
      celdas.push([#importe(item.lineTotal)])
      celdas
    })
    .flatten()
)

#v(14pt)

// ---------------------------------------------------------------------------- totales

// Sin corte de página en el medio: un total separado de sus sumandos se lee como otra cosa.
// Peso de cada renglón: 0 normal, 1 fuerte, 2 remate. El remate marca siempre la **última**
// cifra que importa, que no siempre es el total: con retención, lo que el cliente gira es el
// neto.
//
// Cada retención que aplicó va en su propio renglón, rotulado con el nombre de la tasa y su porcentaje efectivo entre
// paréntesis («ReteICA Bogotá (0,966 %):», owner 2026-11-15), entre el IVA y el total (spec impuestos por tipo §9.3).
// El porcentaje es el del documento, no el del nombre: si un asesor lo edita, el PDF dice el que se aplicó. Por eso las
// tasas se nombran sin porcentaje. Llega ya escrito con el separador del tenant: la plantilla no lo formatea.
// Sin retenciones no hay renglón: no se imprime una fila en cero. Lo
// que sigue siendo cierto es que no restan del total: `Total inversión` es lo facturado, y sólo cuando hay
// retención de verdad aparece además `Neto a pagar`, que es lo que el cliente gira. Sin eso, una retención
// se leería como si ya estuviera descontada del total de arriba, que es falso.
#let hay-retencion = data.withholdings.len() > 0

// Envío: el IVA del envío va en su propio renglón y el IVA de productos sigue siendo `taxAmount`.
// `Charged` suma (base + IVA); `Free` y `OnDelivery` no suman nada al total: el renglón sólo
// explica por qué no hay cobro. Con `OnDelivery` el valor, si lo hay, es informativo.
#let nombre-envio = if data.carrierName == none { "" } else { " (" + data.carrierName + ")" }

//
// El renglón del IVA del envío se oculta en cero: en USD no hay IVA y con excedente de IVA tampoco se
// cobra, así que un «IVA del envío: 0» sobra y, con excedente, contradiría la marca del IVA de arriba.
#let filas-envio = if data.freightChargeMode == "Charged" {
  let base = (("Envío" + nombre-envio + ":", moneda + importe(data.freightSubtotal), 0),)
  if data.freightTaxAmount > 0 {
    base + (
      (
        "IVA del envío (" + str(data.freightTaxPercentage) + " %):",
        moneda + importe(data.freightTaxAmount),
        0,
      ),
    )
  } else {
    base
  }
} else if data.freightChargeMode == "Free" {
  (("Envío" + nombre-envio + ": Gratis", "", 0),)
} else if data.freightChargeMode == "OnDelivery" {
  (
    (
      "Envío" + nombre-envio + ": al cobro, lo paga el cliente a la transportadora",
      if data.freightAmount > 0 { moneda + importe(data.freightAmount) } else { "" },
      0,
    ),
  )
} else {
  ()
}

#let renglones = {
  let filas = (
    ("Valor antes de IVA:", moneda + importe(data.subtotal), 0),
    (
      if data.customerVatSurplus { "Total IVA · excedente de IVA:" } else { "Total IVA:" },
      moneda + importe(data.taxAmount),
      0,
    ),
    ..filas-envio,
    ..data.withholdings.map(retencion => (
      retencion.name + " (" + retencion.percentageLabel + "):",
      "-" + moneda + importe(retencion.amount),
      0,
    )),
    (
      "Total inversión (" + data.currency + "):",
      moneda + importe(data.total),
      if hay-retencion { 1 } else { 2 },
    ),
  )

  if hay-retencion {
    filas.push(("Neto a pagar (" + data.currency + "):", moneda + importe(data.netTotal), 2))
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
      #text(style: "italic")[Total utilidad: #moneda#importe(data.discountAmount)]
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
              if peso == 2 {
                text(size: 9pt, weight: "bold", fill: azul-rey)[#upper(fila.at(0))]
              } else if peso == 1 {
                text(size: 9pt, weight: "bold")[#upper(fila.at(0))]
              } else {
                text(size: 8.5pt)[#upper(fila.at(0))]
              },
              if peso == 2 {
                text(size: 12pt, weight: "bold", fill: azul-rey)[#fila.at(1)]
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

// ---------------------------------------------------------------------------- términos

// Después de los totales, en la misma página si caben; si no, fluyen a la siguiente.
#if terminos != none [
  #v(14pt)
  #if terminos.title != "" [
    #text(size: 10pt, weight: "bold")[#terminos.title]
    #v(4pt, weak: true)
  ]
  #text(size: 8.5pt)[#bloques(terminos.blocks)]
]

// ---------------------------------------------------------------------------- proceso

// El proceso de entrega del tenant va en su propia página al final, y sólo si está configurado para este documento.
#if proceso != none [
  #pagebreak()
  #if proceso.title != "" [
    #text(size: 11pt, weight: "bold")[#proceso.title]
    #v(10pt)
  ]
  #bloques(proceso.blocks)
]
