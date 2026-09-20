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

// Un renglón de la ficha del documento: "Etiqueta: dato" en una sola línea, como en la
// referencia — no una tabla de rótulo/valor, sino el formato de formulario que el comprador
// ya lee en la cotización que el owner fijó.
#let renglon(etiqueta, valor) = [#etiqueta: #valor]

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
        \ #text(size: 9pt, fill: apagado)[NIT #data.billingAccount.companyTaxId]
      ]
      #if data.billingAccount.companyAddress != "" [
        \ #text(size: 8.5pt, fill: apagado)[Dirección empresa: #data.billingAccount.companyAddress]
      ]
      #if data.billingAccount.companyPhone != "" [
        \ #text(size: 8.5pt, fill: apagado)[Teléfono: #data.billingAccount.companyPhone]
      ]
    ] else [
      #text(size: 14pt, weight: "bold", tracking: -0.01em)[Cotización]
    ]
  ],
  block[
    #if hay-emisor [
      #text(size: 12pt, weight: "bold")[Cotización]
    ] else [
      #text(size: 12pt, weight: "bold")[#data.quotationNumber]
    ]
    #v(3pt, weak: true)
    #par(leading: 0.6em)[
      #if hay-emisor [#renglon("N.°", data.quotationNumber) \ ]
      #renglon("Emitida", fecha(data.issuedOn)) \
      #renglon(
        "Válida hasta",
        if data.validUntil == none { text(fill: apagado)[Sin vencimiento] } else { fecha(data.validUntil) },
      ) \
      #renglon("Asesor", if data.advisorLabel == "" { vacio } else { data.advisorLabel }) \
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
#if data.paymentMethod != none or data.billingAccount != none [
  #block(
    breakable: false,
    width: 100%,
    stroke: 0.8pt + tinta,
    inset: 10pt,
  )[
    #par(leading: 0.6em)[
      #if data.paymentMethod != none [#renglon("Forma de pago", data.paymentMethod) \ ]
      #if data.billingAccount != none [
        #renglon("Cuenta para el pago", data.billingAccount.companyName) \
        #data.billingAccount.bankName \
        #text(weight: "bold")[
          Número: #data.billingAccount.accountNumber (#data.billingAccount.currency)
        ]
      ]
    ]
  ]
  // Aviso fijo del pie de pago, como en la referencia: no es un dato de la cotización —no hay
  // dónde guardarlo hoy, ni varía de una a otra— sino la política bancaria del negocio que usa
  // esta plantilla, igual para todos sus documentos.
  #if data.billingAccount != none [
    #v(6pt, weak: true)
    #text(size: 7.5pt, fill: naranja)[
      #text(weight: "bold")[Ten en cuenta:] \
      no aceptamos pagos por corresponsal bancario, \
      ni depósitos en entidades bancarias, ni cajeros. \
      Solo debes pagar el valor de tu pedido, el valor a pagar \
      debe de ser libre de toda comisión bancaria.
    ]
  ]
  #v(12pt)
]

// ---------------------------------------------------------------------------- partes

// Sólo cuando alguna parte tiene datos propios o el cliente recoge en tienda. Si las dos son
// "los mismos datos del cliente", la banda entera desaparece en vez de repetir por tercera vez
// lo que la ficha ya dice. Recoger en tienda sí la muestra aunque la facturación siga al cliente:
// sin ella el documento callaría que no hay entrega, y callar se lee como "a la dirección del
// cliente".
// El NIT va en su renglón y con rótulo, no pegado al contacto. Hoy sólo lo trae consumidor
// final, que no tiene contacto ni dirección: sin el NIT, "Consumidor final" a secas no identifica
// la factura.
#let parte(valor) = [
  #valor.name
  #if valor.taxId != "" [\ NIT #valor.taxId]
  #if valor.contact != "" [\ #contacto(valor.contact)]
  #if valor.location != "" [\ #valor.location]
]

#if data.isStorePickup or not data.billing.sameAsCustomer or not data.shipping.sameAsCustomer [
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
  // Fila 0 oscura para el encabezado; el resto alterna blanco y panel, como en la referencia
  // — la cebra ayuda a leer una fila completa en una tabla ancha, sin depender del color.
  fill: (x, y) => if y == 0 { tinta } else if calc.even(y) { panel },
  stroke: (x, y) => (bottom: if y == 0 { none } else { 0.4pt + filete }),
  table.header(..titulos.map(titulo => rotulo(titulo, color: white))),
  ..data.items
    .map(item => {
      let celdas = ([#item.productName],)
      if hay-descuento {
        celdas.push([#importe(item.unitPrice)])
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
  // En mayúsculas como en la referencia: es la letra chica que el vendedor quiere que el
  // comprador no pase por alto, y en mayúsculas se lee como aviso, no como un párrafo más.
  #block(width: 130mm)[#upper(data.notes)]
]

#v(14pt)

// ---------------------------------------------------------------------------- totales

// Sin corte de página en el medio: un total separado de sus sumandos se lee como otra cosa.
// Peso de cada renglón: 0 normal, 1 fuerte, 2 remate. El remate marca siempre la **última**
// cifra que importa, que no siempre es el total: con retención, lo que el cliente gira es el
// neto.
//
// La retención va entre el IVA y el total, como en la referencia, y se imprime siempre —incluso
// en cero— porque ahí es donde el comprador espera verla. Lo que sigue siendo cierto es que no
// resta del total: `Total inversión` es lo facturado, y sólo cuando hay retención de verdad
// aparece además `Neto a pagar`, que es lo que el cliente gira. Sin eso, una retención distinta
// de cero se leería como si ya estuviera descontada del total de arriba, que es falso.
#let hay-retencion = data.retentionAmount > 0

#let renglones = {
  let filas = (
    ("Valor antes de IVA:", "$ " + importe(data.subtotal), 0),
    (
      if data.customerVatSurplus { "Total IVA · excedente de IVA:" } else { "Total IVA:" },
      "$ " + importe(data.taxAmount),
      0,
    ),
    (
      "Retención en la fuente:",
      (if hay-retencion { "-$ " } else { "$ " }) + importe(data.retentionAmount),
      0,
    ),
    (
      "Total inversión (" + data.currency + "):",
      "$ " + importe(data.total),
      if hay-retencion { 1 } else { 2 },
    ),
  )

  if hay-retencion {
    filas.push(("Neto a pagar (" + data.currency + "):", "$ " + importe(data.netTotal), 2))
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
      #text(style: "italic")[Total utilidad: \$ #importe(data.discountAmount)]
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

// ---------------------------------------------------------------------------- proceso
//
// Segunda página fija, igual que el logo y el aviso de pago: no es un dato de la cotización —no
// cambia de una a otra— sino la política de logística del negocio que emite el documento, así
// que va escrita acá y no armada desde `data`.
#pagebreak()

#text(size: 11pt, weight: "bold")[Proceso de Alistamiento y Recepción de Pedidos.]

#v(10pt)

#text(weight: "bold")[Alistamiento y Entrega:] \
El proveedor preparará los pedidos bajo filmación y los entregará a la empresa transportadora
dentro de los 8 días hábiles siguientes a la confirmación del pedido, siempre que estos hayan
sido solicitados y pagados por el mayorista.

#v(8pt)

#text(weight: "bold")[Recepción de la Mercancía:] \
Al recibir los productos de la transportadora, el mayorista deberá revisar el estado del
paquete. No recibir en caso de detectar:

\- Cinta contramarcada deteriorada \
\- Cajas dañadas y/o abiertas \
\- Cintas, zunchos o cajas diferentes a las del proveedor

#v(4pt)

Si decide recibir el paquete a pesar de alguna de estas irregularidades, lo hará bajo su
responsabilidad. En este caso, deberá:

\- Dejar constancia de la novedad en la guía de recepción de la transportadora. \
\- Informar de inmediato al proveedor. \
\- Enviar fotografías como evidencia.

#v(8pt)

#text(weight: "bold")[Verificación del Contenido:] \
Una vez recibido el pedido, el mayorista deberá grabar un video al abrir las cajas o paquetes,
evidenciando el estado y la cantidad de los productos.

#v(8pt)

#text(weight: "bold")[Reporte de Novedades:] \
Si detecta daños, faltantes o deterioro en los productos, deberá informar al proveedor en un
plazo máximo de 1 día hábil después de recibirlos. El reporte debe incluir:

\- Video de la apertura del paquete. \
\- Fotografías de las cajas. \
\- Imágenes de los 3 stickers identificativos del proveedor.
