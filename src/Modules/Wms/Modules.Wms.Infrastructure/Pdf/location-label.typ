#import "@preview/tiaoma:0.3.0": barcode
#set page(width: 60mm, height: 30mm, margin: 3mm)
#set text(font: "Liberation Sans")
#let d = json(sys.inputs.data)
#for item in d.labels [
  #align(center)[#barcode(item.code, "Code128", width: 50mm, height: 14mm)]
  #align(center)[#text(size: 9pt, weight: "bold")[#item.code]]
  #align(center)[#text(size: 7pt)[#item.path]]
  #pagebreak(weak: true)
]
