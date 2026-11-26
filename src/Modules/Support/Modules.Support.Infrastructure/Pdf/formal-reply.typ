// Plantilla Typst de la respuesta formal de Soporte (fase 4b, spec §8.3). Viaja entera a `qcode-pdf` en cada request: el servicio
// es genérico y no guarda plantillas. Los datos llegan aparte, en el JSON de entrada: la serialización de `SupportFormalReplyDocument` en
// camelCase. SupportFormalReplyTemplateTests revisa que cada campo que esta plantilla lee exista en ese contrato.
//
// Todo texto que llega en los datos se imprime como texto, nunca como marcado: un `#` o un `[` que escriba el solicitante o el asesor
// sale tal cual. Por eso aquí no hay `eval` y los textos entran sólo por `#valor` o por las funciones de abajo.
//
// El orden es el de la spec §8.3: membrete, radicado y fechas, destinatario, asunto, resumen del caso, análisis y decisión, siguiente
// paso (si hay) y firma con anexos.
//
// `data.termIsLegal` llega pero esta plantilla no lo lee a propósito: el owner quitó la línea del «término legal» el 2026-11-16. No la
// vuelvas a agregar sin revisarlo con el abogado.
#let data = json(sys.inputs.data)

#let tinta = rgb("#14181F")
// 5.8:1 sobre blanco: a 7pt un gris más claro deja de leerse.
#let apagado = rgb("#5F6672")
#let filete = rgb("#E3E5E9")

// Liberation Sans la instala la imagen de `qcode-pdf`; la lista de respaldo evita que, sin ella, el documento salga en la serif por
// defecto sin que nadie se entere (mismo criterio que la plantilla de cotizaciones).
#set text(font: ("Liberation Sans", "Arial", "Helvetica"), size: 10pt, fill: tinta, lang: "es")
#set par(leading: 0.65em, spacing: 0.9em, justify: false)

// ---------------------------------------------------------------------------- formato

// Las fechas llegan en ISO (`2026-11-16`) porque el contrato es JSON; se imprimen con el patrón del tenant. Un patrón desconocido
// cae en día/mes/año, el de Colombia.
#let fecha(iso) = {
  let partes = iso.split("-")
  let (anio, mes, dia) = (partes.at(0), partes.at(1), partes.at(2))
  if data.dateFormat == "yyyy-MM-dd" {
    anio + "-" + mes + "-" + dia
  } else if data.dateFormat == "MM/dd/yyyy" {
    mes + "/" + dia + "/" + anio
  } else {
    dia + "/" + mes + "/" + anio
  }
}

// Un texto libre con saltos de línea: cada línea entra como texto y los saltos se respetan. Las líneas en blanco quedan, para que
// los párrafos que escribió el asesor se vean como párrafos.
#let parrafos(texto) = texto.replace("\r", "").split("\n").map(linea => [#linea]).join(linebreak())

#let rotulo(texto) = text(size: 7.5pt, weight: "bold", fill: apagado, tracking: 0.08em)[#upper(texto)]

#let seccion(titulo, cuerpo) = [
  #v(10pt, weak: true)
  #rotulo(titulo)
  #v(3pt, weak: true)
  #cuerpo
]

// ---------------------------------------------------------------------------- página

// El pie repite el radicado en cada hoja: una carta de varias páginas se imprime y se separa.
#set page(
  paper: "a4",
  margin: (x: 22mm, top: 18mm, bottom: 20mm),
  footer: context [
    #line(length: 100%, stroke: 0.4pt + filete)
    #v(4pt)
    #grid(
      columns: (1fr, auto),
      text(size: 7.5pt, fill: apagado)[Respuesta al radicado #data.ticketNumber · #data.letterhead.companyName],
      text(size: 7.5pt, fill: apagado)[Página #counter(page).display() de #counter(page).final().first()],
    )
  ],
)

// ---------------------------------------------------------------------------- 1. membrete

#grid(
  columns: (1fr, auto),
  column-gutter: 10mm,
  align: (left + top, right + top),
  [
    #if data.letterhead.logo != none [
      #image("assets/" + data.letterhead.logo.fileName, width: 36mm, height: 16mm, fit: "contain")
      #v(5pt, weak: true)
    ]
    #text(size: 12pt, weight: "bold")[#data.letterhead.companyName] \
    #text(fill: apagado)[#data.letterhead.documentTypeCode #data.letterhead.documentNumber]
    #if data.letterhead.address != none [
      \ #text(fill: apagado)[#data.letterhead.address#if data.letterhead.cityName != none [, #data.letterhead.cityName]]
    ] else if data.letterhead.cityName != none [
      \ #text(fill: apagado)[#data.letterhead.cityName]
    ]
    #if data.letterhead.email != none [ \ #text(fill: apagado)[#data.letterhead.email] ]
    #if data.letterhead.phone != none [ \ #text(fill: apagado)[#data.letterhead.phone] ]
  ],
  [
    // ------------------------------------------------------------------------ 2. radicado y fechas
    #rotulo("Respuesta formal") \
    #text(size: 12pt, weight: "bold")[#data.ticketNumber] \
    Radicado el #fecha(data.receivedOn) \
    Respondido el #fecha(data.respondedOn)
  ],
)

#v(6pt)
#line(length: 100%, stroke: 0.6pt + filete)

// ---------------------------------------------------------------------------- 3. destinatario

#seccion("Para")[
  #if data.anonymous [
    Solicitante anónimo
  ] else [
    #text(weight: "bold")[#data.recipientName]
    #if data.recipientDocument != none [ \ #data.recipientDocument ]
    #if data.recipientEmail != none [ \ #data.recipientEmail ]
    #if data.recipientAddress != none [ \ #data.recipientAddress ]
  ]
]

// ---------------------------------------------------------------------------- 4. asunto

#v(12pt, weak: true)
#text(weight: "bold")[Respuesta a tu #lower(data.typeName) #data.ticketNumber del #fecha(data.receivedOn)]

// ---------------------------------------------------------------------------- 5. resumen del caso

#seccion("Resumen de tu solicitud")[
  #text(weight: "bold")[#data.subject]
  #v(3pt, weak: true)
  #parrafos(data.description)
]

// ---------------------------------------------------------------------------- 6. análisis y decisión

#seccion("Análisis y decisión")[
  #parrafos(data.decision)
]

// ---------------------------------------------------------------------------- 7. siguiente paso

// Sin texto del tipo ni del tenant, la sección no se imprime (spec §13.6).
#if data.nextStep != none [
  #seccion("Siguiente paso")[
    #parrafos(data.nextStep)
  ]
]

// ---------------------------------------------------------------------------- 8. firma y anexos

#v(22pt, weak: true)
#block(breakable: false)[
  Atentamente,
  #v(16pt)
  #text(weight: "bold")[#data.signerName] \
  #data.signerTitle \
  #text(fill: apagado)[#data.letterhead.companyName]
]

#if data.annexes.len() > 0 [
  #seccion("Anexos")[
    #list(..data.annexes.map(anexo => [#anexo]))
  ]
]
