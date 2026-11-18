# Changelog

## [1.0.0] - 2026-11-16

### Added
- Imports WATS Export Wizard UUT exports: one UUT report per row, with step hierarchy, numeric,
  pass/fail and string value steps (single and multiple), sequence calls, loops, misc info and sub-units.
- Reads the Export Wizard's tab-separated (TSV) format. Comma, semicolon and pipe separators are also
  supported, detected from the header row, with CSV quoting and decimal commas in semicolon files.
- Works when the Export Wizard leaves fields or the `|<-` / `->|` group markers out.
- Converter parameters `PartNumber`, `PartRevisionNumber`, `OperationTypeCode`, `StationName` and
  `Operator` fill fields the file has no value for.
- Rows without serial number or start time, with extra cells or with an unknown status are skipped
  and logged; the rest of the file is still imported.
- Builds for WATS Client 6 (`net472`) and WATS Client 7 (`net8.0-windows`).
