#import "@preview/tiaoma:0.3.0": barcode
#set page(width: 60mm, height: 30mm, margin: 3mm)
#set text(font: "Liberation Sans")
// El área útil es de 24 mm: el espaciado por defecto entre párrafos desborda y la ruta cae en una segunda página.
#set par(spacing: 1mm, leading: 0.3em)
#set block(spacing: 1mm)
#let d = json(sys.inputs.data)
#for item in d.labels [
  #block(height: 100%, width: 100%, clip: true)[
    #align(center)[#barcode(item.code, "Code128", width: 50mm, height: 14mm)]
    #align(center)[#text(size: 9pt, weight: "bold")[#item.code]]
    #align(center)[#text(size: 7pt)[#item.path]]
  ]
  #pagebreak(weak: true)
]
