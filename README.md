# WATS Export Wizard Converter

Imports UUT reports in the WATS Export Wizard layout: tab-separated (TSV) as exported, or
comma/semicolon-separated (CSV).

## Integration Details

| Property | Value |
|----------|-------|
| **Category** | WATS Client converter |
| **Type** | FileConverter |
| **Format** | TSV (tab-separated, as written by the Export Wizard); comma, semicolon and pipe also supported |
| **Test type** | General |
| **Assembly** | `Virinco.WATS.Converter.WATS.ExportWizard.dll` |
| **Class** | `Virinco.WATS.Converter.WATS.ExportWizard.ExportWizardConverter` |

## About

The Export Wizard in WATS exports UUT reports to a tab-separated (TSV) text file: one row per report,
with the report header first and then a group of columns for every step. This converter reads such a
file and submits one UUT report per row, including the step hierarchy, measurements, limits, misc info
and sub-units. Use it to move reports between WATS servers, or to import results that were prepared in
the same layout.

The format has been extended so the same layout can also be imported with comma, semicolon or pipe
as the separator.

### See what the format looks like

The quickest way to understand the layout is to open the example files in [ExampleData](ExampleData),
in a spreadsheet or a text editor:

- [Minimal_Tab.txt](ExampleData/Minimal_Tab.txt) - the smallest useful file: two reports with a few
  report fields and a main sequence with three steps. Start here if you want to create files yourself.
- [AllSteps_Tab.txt](ExampleData/AllSteps_Tab.txt) - one report with every supported step type:
  numeric, pass/fail and string value steps (single and multiple), sequence calls, loops, misc info
  and sub-units.

You can also export a few of your own reports with the Export Wizard in WATS and use that file as a
template. [File format](#file-format) below describes the columns, and which rows are rejected.

## Getting Started

* [What is WATS?](https://wats.com)
* [Setting up a custom converter](https://support.wats.com/hc/en-us/articles/13344321749788-Setting-up-a-custom-converter)

## Download

Download the latest release from the [Releases](https://github.com/theWATScompany/wats-converter-wats-exportwizard/releases/latest) page.
Each release has two zip files: `net472` for WATS Client 6 and `net8.0-windows` for WATS Client 7.

## Installation

1. Download the zip for your WATS Client version from the [Releases](https://github.com/theWATScompany/wats-converter-wats-exportwizard/releases/latest) page and extract it. If Windows blocks the download, right-click the zip, open **Properties** and select **Unblock** before extracting.
2. Open the **WATS Client Configurator** as administrator, go to **Converters** and click **ADD CONVERTER**.
3. Click **BROWSE** next to **Assembly** and select `Virinco.WATS.Converter.WATS.ExportWizard.dll`. The DLL can be in any folder — the Configurator copies it into the WATS Client installation folder.
4. Select `ExportWizardConverter` in **Class**. The **Parameters** tab is filled with the converter's default parameters.
5. Set **Input path**, **Filter** (for example `*.txt` or `*.csv`) and **PPA** (post-process action), and adjust the values on the **Parameters** tab.
6. Click **Apply** and answer **Yes** to restart the WATS Client Service.

## Parameters

Each parameter is used only when the file has no value for that field. A value in the file always wins.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `PartNumber` | `PN1` | Part number for reports without one. |
| `PartRevisionNumber` | *(empty)* | Revision for reports without one. Empty means no revision. |
| `OperationTypeCode` | `10` | Test operation (process code) for reports without one. |
| `StationName` | *(empty)* | Station name for reports without one. Empty means the WATS Client's own station. |
| `Operator` | `operator` | Operator for reports without one. |

Location and purpose come from the file, otherwise from the WATS Client's own settings. Fixture ID,
comment, error message and execution time come only from the file.

## File format

- **Encoding:** UTF-8, or UTF-16/UTF-32 with a byte order mark.
- **Separator:** tab (the Export Wizard's format), or comma, semicolon or pipe, detected from the
  header row. Tab files are split as they are. The other separators use standard CSV quoting:
  `"a, b"`, `""` for a quote, and line breaks inside quoted values. Semicolon files are read with
  decimal commas: a cell that is only a number like `3,14` or `1.234,5` is read as `3.14` or `1234.5`.
- **Header columns:** `Serial number`, `Part number`, `Revision`, `Test operation`, `Status`,
  `Date/time`, `UTC date/time`, `Station name`, `Location`, `Purpose`, `Operator`, `Fixture ID`,
  `Batch serial number`, `Test socket index`, `Execution time`, `Error code`, `Error message`, `Comment`,
  `SW filename` and `SW fileversion`.
  - **Dates:** `2026-Jan-15 10:00:00`, `2026-01-15 10:00:00` or ISO 8601 (`2026-01-15T10:00:00`),
    optionally with milliseconds. With only `UTC date/time`, the local time is calculated from it.
  - **Report status:** when `Status` is empty, the report is Failed if a step failed or had an error,
    otherwise Passed.
  - **Misc info:** columns named `<name> - String` / `<name> - Numeric` (and `<name> - Numeric format`)
    become one misc info item `<name>`. The numeric part must be a whole number from -32768 to 32767;
    otherwise the value is kept as text.
  - **Sub-units:** `Subunit` columns (type, part number, serial number and revision, numbered per
    sub-unit) become sub-units.
  - `WATS link`, `Report ID` and `Product name` are not imported.
- **Step columns:** each step is a group of columns, `|<- Step name`, `Step name (Status)`,
  `Step name (Low limit)`, `(High limit)`, `(Units)`, `(Comp operator)`, `(Step path)`, `(Step time)`,
  `(Report text)` and so on, closed by `->|`. The `|<-` / `->|` markers are optional. A step without any
  value in a row was not run for that unit, and is left out of that report.
- **Step types:**
  - numeric limit, pass/fail and string value steps, single and multiple (`//` in the step path)
  - sequence calls, including a sequence that is called more than once
  - loops, recognised from `(Loop index: N)` in the step name
  - Call Executable and Message Popup steps, recognised by their step type name in the step name
  - other steps with only a status become action steps
- **Comparison operators:** `EQ`, `NE`, `GT`, `LT`, `GE`, `LE`, `GELE`, `GELT`, `GTLE`, `GTLT`, `LEGE`,
  `LEGT`, `LTGE`, `LTGT` and, for strings, `IGNORECASE` and `CASESENSIT`. An empty or other value is
  logged without a comparison (`LOG`).
- **Omitted fields:** the Export Wizard lets you leave fields out. Only the serial number and a start
  time are required.
  - Without `(Step path)` columns, all steps are placed directly under the main sequence.
  - Without `(Comp operator)` columns, values without limits are kept as logged measurements.

### Rows that are not imported

A row is skipped, and the reason written to the WATS Client log (`wats.log`), when it:

- has no serial number,
- has no recognisable `Date/time` or `UTC date/time`,
- has more cells than the header (an unquoted separator in a value), or
- has a status that is not one of `Passed`/`Pass`/`P`, `Failed`/`Fail`/`F`, `Error`, `Terminated`
  (steps also `Skipped`/`Skip` and `Done`), in any letter case.

The other rows in the file are still imported. If no row can be imported, the file fails.

## Testing

The tests use xUnit and run offline; nothing is sent to a WATS server.

- `ConverterTests.cs` converts every sample file (ConvertOnly, ConvertAndValidate, ConvertAndSimulate).
- `OmittedFieldsTests.cs` re-converts the samples with fields removed, blanked or reordered, and checks
  the rules above.

```powershell
dotnet test ConverterTests.csproj
```

## Contributing

We welcome contributions! Feel free to open an issue or create a pull request.

## Troubleshooting

### Converter failed to start

- Ensure the WATS Client Service has folder permission to the input path.
- Restart the WATS Client Service after configuration changes.

### Converter class drop-down is empty

- The DLL file may be blocked by Windows. Right-click the file, open Properties, and click Unblock.
- Check that you picked the zip for your WATS Client version (`net472` for Client 6, `net8.0-windows` for Client 7).

### Some rows are missing in WATS

- Open `wats.log` and look for the file name: every skipped row is logged with its row number and the reason.

### Other issues

Contact [WATS Support](mailto:support@wats.com) and include the `wats.log` file.

## Resources

- [GitHub Repository](https://github.com/theWATScompany/wats-converter-wats-exportwizard)
- [WATS Documentation](https://support.wats.com)
- [Setting up a custom converter](https://support.wats.com/hc/en-us/articles/13344321749788-Setting-up-a-custom-converter)

## License

See [LICENSE](LICENSE.md) for details.
