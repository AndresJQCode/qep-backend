"""Convierte Lista-de-ciudades.xlsx de Siigo en el JSON que embebe Geography.

Uso (PowerShell, con el .xlsx en una carpeta propia y este script en el repo):
    python -I scripts\\geography\\convert_siigo_cities.py <ruta\\cities.xlsx> <ruta\\siigo-billing-cities.json>

Sólo biblioteca estándar (zipfile + xml.etree): no ejecuta nada del archivo descargado.
"""
import json
import sys
import zipfile
import xml.etree.ElementTree as ET

NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
REL = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"
HEADER = ["CityCode", "CityName", "StateCode", "StateName", "CountryCode", "CountryName"]
# Recorta espacios normales y de no separación (U+00A0), como pide el spec.
STRIP = " \t\r\n   ﻿"


def clean(value):
    return (value or "").strip(STRIP)


def shared_strings(archive):
    if "xl/sharedStrings.xml" not in archive.namelist():
        return []
    root = ET.fromstring(archive.read("xl/sharedStrings.xml"))
    return ["".join(t.text or "" for t in si.iter("{%s}t" % NS["m"])) for si in root.findall("m:si", NS)]


def first_sheet_path(archive):
    workbook = ET.fromstring(archive.read("xl/workbook.xml"))
    first = workbook.find("m:sheets/m:sheet", NS)
    rel_id = first.get(REL)
    rels = ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))
    for rel in rels:
        if rel.get("Id") == rel_id:
            target = rel.get("Target").lstrip("/")
            return target if target.startswith("xl/") else "xl/" + target
    raise SystemExit("sheet1 not found")


def column_index(ref):
    letters = "".join(ch for ch in ref if ch.isalpha())
    index = 0
    for ch in letters:
        index = index * 26 + (ord(ch.upper()) - 64)
    return index - 1


def rows(archive):
    strings = shared_strings(archive)
    sheet = ET.fromstring(archive.read(first_sheet_path(archive)))
    for row in sheet.iter("{%s}row" % NS["m"]):
        values = [""] * len(HEADER)
        for cell in row.findall("m:c", NS):
            index = column_index(cell.get("r"))
            if index >= len(HEADER):
                continue
            kind = cell.get("t")
            if kind == "inlineStr":
                text = "".join(t.text or "" for t in cell.iter("{%s}t" % NS["m"]))
            else:
                node = cell.find("m:v", NS)
                text = "" if node is None else (node.text or "")
                if kind == "s" and text:
                    text = strings[int(text)]
            values[index] = text
        yield values


def main(source, target):
    with zipfile.ZipFile(source) as archive:
        all_rows = list(rows(archive))
    header = [clean(value) for value in all_rows[0]]
    if header != HEADER:
        raise SystemExit(f"Unexpected header: {header}")

    records, seen = [], set()
    for number, raw in enumerate(all_rows[1:], start=2):
        city_code, city_name, state_code, state_name, country_code, country_name = (clean(v) for v in raw)
        if not any((city_code, city_name, state_code, state_name, country_code, country_name)):
            continue
        if not all((city_code, city_name, state_code, state_name, country_code, country_name)):
            raise SystemExit(f"Row {number} has an empty column")
        # Colombia sigue por DANE (spec 2026-11-26): no se importa.
        if country_code.upper() == "CO":
            continue
        key = (country_code.upper(), state_code, city_code)
        if key in seen:
            raise SystemExit(f"Row {number} repeats {key}")
        seen.add(key)
        records.append({
            "countryCode": country_code,
            "countryName": country_name,
            "stateCode": state_code,
            "stateName": state_name,
            "cityCode": city_code,
            "cityName": city_name,
        })

    records.sort(key=lambda r: (r["countryCode"].upper(), r["stateCode"], r["cityCode"]))
    # Una ciudad por línea: el diff de una lista nueva de Siigo se lee fila por fila.
    lines = [json.dumps(record, ensure_ascii=False, separators=(",", ":")) for record in records]
    with open(target, "w", encoding="utf-8", newline="\n") as output:
        output.write("[\n" + ",\n".join(lines) + "\n]\n")
    countries = {r["countryCode"].upper() for r in records}
    print(f"{len(records)} cities, {len(countries)} countries -> {target}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
