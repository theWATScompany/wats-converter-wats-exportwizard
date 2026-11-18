using Xunit;
using Xunit.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.WATS.ExportWizard.Tests
{
    // Offline: Submit is overridden, so nothing is sent to a WATS server.
    // Each sample export is converted again with one header column or one step field (e.g. every
    // "(Step path)" column) removed or blanked; every row must still produce a report - except rows without
    // a serial number, which must never be submitted.
    public class OmittedFieldsTests : TDM
    {
        // Sample exports are copied next to the test assembly; the working directory is not guaranteed.
        private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

        private readonly List<Report> _submitted = new List<Report>();

        public override bool Submit(SubmitMethod method, Report report)
        {
            _submitted.Add(report);
            return true;
        }

        [Fact]
        public void ConvertsWhenFieldsAreOmitted()
        {
            InitializeAPI(false);
            var failures = new List<string>();

            foreach (var fn in Directory.GetFiles(DataDir, "*.txt"))
            {
                LoadSample(fn, out var header, out var rows, out var firstStep);
                var name = Path.GetFileName(fn);

                foreach (var group in ColumnGroups(header, firstStep))
                {
                    // Without a serial number nothing may be submitted
                    int expected = group.Key == "'Serial number'" ? 0 : rows.Count;
                    Check(name, "drop " + group.Key, Join(DropColumns(header, rows, group.Value, out var kept), kept), expected, failures);
                    Check(name, "blank " + group.Key, Join(header, BlankColumns(rows, group.Value)), expected, failures);
                }

                var stepCols = new HashSet<int>(Enumerable.Range(firstStep, header.Length - firstStep));
                var metaCols = new HashSet<int>(Enumerable.Range(0, firstStep));
                Check(name, "no step columns", Join(DropColumns(header, rows, stepCols, out var r1), r1), rows.Count, failures);
                Check(name, "no metadata columns", Join(DropColumns(header, rows, metaCols, out var r2), r2), 0, failures);
                Check(name, "no |<- ->| markers", Join(header.Select(h => h.Replace("|<-", "").Replace("->|", "").Trim()).ToArray(), rows), rows.Count, failures);
                Check(name, "rows cut off at half width", Join(header, rows.Select(r => r.Take(r.Length / 2).ToArray()).ToList()), rows.Count, failures);
                Check(name, "metadata columns reversed",
                    Join(header.Take(firstStep).Reverse().Concat(header.Skip(firstStep)).ToArray(),
                         rows.Select(r => r.Take(firstStep).Reverse().Concat(r.Skip(firstStep)).ToArray()).ToList()), rows.Count, failures);
            }

            if (failures.Count > 0)
                throw new XunitException(failures.Count + " variant(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        // The "|<-" / "->|" group markers are optional: without them (tab or comma separated) every
        // sample must give the same steps - type, name, status and measurement count - as with them.
        [Fact]
        public void GroupMarkersAreOptional()
        {
            InitializeAPI(false);
            var failures = new List<string>();

            foreach (var fn in Directory.GetFiles(DataDir, "*.txt"))
            {
                LoadSample(fn, out var header, out var rows, out _);
                var unmarked = header.Select(h => h.Replace("|<-", "").Replace("->|", "").Trim()).ToArray();

                Convert(new ExportWizardConverter(), Join(header, rows));
                var expected = _submitted.Cast<UUTReport>().Select(Signature).ToList();

                foreach (var variant in new[] { "tab", "comma" })
                {
                    Convert(new ExportWizardConverter(), variant == "tab" ? Join(unmarked, rows) : JoinCsv(unmarked, rows));
                    var actual = _submitted.Cast<UUTReport>().Select(Signature).ToList();
                    if (!actual.SequenceEqual(expected))
                        failures.Add($"{Path.GetFileName(fn)} / no markers, {variant}: steps differ from the marked file");
                }
            }

            if (failures.Count > 0)
                throw new XunitException(failures.Count + " variant(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        private static string Signature(UUTReport uut)
            => uut.SerialNumber + "|" + uut.Status + "|" + string.Join("|", uut.AllSteps.Select(s =>
                   s.GetType().Name + ":" + s.Name + ":" + s.Status
                   + ":" + ((s as NumericLimitStep)?.Tests.Length ?? (s as PassFailStep)?.Tests.Length ?? (s as StringValueStep)?.Tests.Length ?? 0)));

        [Fact]
        public void RowWithoutSerialNumberIsNotSubmitted()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            int snCol = Array.IndexOf(header, "Serial number");
            var expectedSerials = rows.Where((r, i) => i != 2).Select(r => r[snCol]).ToList();
            rows[2][snCol] = "";

            Convert(new ExportWizardConverter(), Join(header, rows));

            var submittedSerials = _submitted.Cast<UUTReport>().Select(r => r.SerialNumber).ToList();
            if (!submittedSerials.SequenceEqual(expectedSerials))
                throw new XunitException("Submitted serials: " + string.Join(", ", submittedSerials) + Environment.NewLine
                                                + "Expected: " + string.Join(", ", expectedSerials));
        }

        [Fact]
        public void PartNumberParameterFillsMissingPartNumber()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            var noPartNumber = Join(DropColumns(header, rows, new HashSet<int> { Array.IndexOf(header, "Part number") }, out var kept), kept);

            // Parameter names from the WATS Client Configurator are matched case-insensitively
            Convert(new ExportWizardConverter(new Dictionary<string, string> { ["partnumber"] = "PN-FROM-CONFIG" }), noPartNumber);

            var partNumbers = _submitted.Cast<UUTReport>().Select(r => r.PartNumber).Distinct().ToList();
            if (_submitted.Count != rows.Count || partNumbers.Count != 1 || partNumbers[0] != "PN-FROM-CONFIG")
                throw new XunitException($"{_submitted.Count} reports with part number(s): " + string.Join(", ", partNumbers));
        }

        // A comma file where one value contains an unquoted comma: that row is shifted, so it must be skipped
        // (and logged) while the correctly quoted rows are submitted.
        [Fact]
        public void RowWithUnquotedCommaIsNotSubmitted()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            int snCol = Array.IndexOf(header, "Serial number"), commentCol = Array.IndexOf(header, "Comment");
            rows[2][commentCol] = "Retested, fixture 2";
            var expectedSerials = rows.Where((r, i) => i != 2).Select(r => r[snCol]).ToList();

            var csv = JoinCsv(header, rows).Replace("\"Retested, fixture 2\"", "Retested, fixture 2");
            Convert(new ExportWizardConverter(), csv);

            var submittedSerials = _submitted.Cast<UUTReport>().Select(r => r.SerialNumber).ToList();
            if (!submittedSerials.SequenceEqual(expectedSerials))
                throw new XunitException("Submitted serials: " + string.Join(", ", submittedSerials));
        }

        // Statuses: the agreed values in any case are accepted; any other value rejects that report only.
        [Fact]
        public void UnknownStatusRejectsOnlyThatReport()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            int snCol = Array.IndexOf(header, "Serial number"), statusCol = Array.IndexOf(header, "Status");
            int stepStatusCol = Array.FindIndex(header, h => h.Replace("->|", "").TrimEnd().EndsWith("(Status)", StringComparison.Ordinal));

            rows[0][statusCol] = rows[0][statusCol].ToUpperInvariant();         // "FAILED" / "PASSED": accepted
            rows[1][stepStatusCol] = rows[1][stepStatusCol].ToLowerInvariant(); // "failed" / "passed": accepted
            rows[2][stepStatusCol] = "OK";                                      // unknown step status: rejected
            rows[3][statusCol] = "NG";                                          // unknown report status: rejected
            var expectedSerials = rows.Where((r, i) => i != 2 && i != 3).Select(r => r[snCol]).ToList();
            var expectedStatus = rows[0][statusCol];

            Convert(new ExportWizardConverter(), Join(header, rows));

            var submitted = _submitted.Cast<UUTReport>().ToList();
            if (!submitted.Select(r => r.SerialNumber).SequenceEqual(expectedSerials)
                || !string.Equals(submitted[0].Status.ToString(), expectedStatus, StringComparison.OrdinalIgnoreCase))
                throw new XunitException("Submitted: " + string.Join(", ", submitted.Select(r => r.SerialNumber + "=" + r.Status)));
        }

        // An export with only "(Status)" per step has single-column groups ("|<- Step (Status) ->|"): two marker
        // pipes per separator. The separator must still be detected as tab, comma or semicolon.
        [Fact]
        public void SingleColumnStepGroupsKeepTheirSeparator()
        {
            InitializeAPI(false);
            var failures = new List<string>();

            foreach (var fn in Directory.GetFiles(DataDir, "*.txt"))
            {
                LoadSample(fn, out var header, out var rows, out var firstStep);
                var keep = Enumerable.Range(0, header.Length)
                    .Where(i => i < firstStep || header[i].Replace("->|", "").TrimEnd().EndsWith("(Status)", StringComparison.Ordinal)).ToArray();
                var statusHeader = keep.Select(i => i < firstStep ? header[i] : "|<- " + header[i].Replace("|<-", "").Replace("->|", "").Trim() + " ->|").ToArray();
                var statusRows = rows.Select(r => keep.Select(i => i < r.Length ? r[i] : "").ToArray()).ToList();

                foreach (var d in new[] { '\t', ',', ';' })
                {
                    var content = string.Join("\r\n", new[] { statusHeader }.Concat(statusRows).Select(r => string.Join(d.ToString(), r.Select(c =>
                        c.IndexOf(d) >= 0 || c.IndexOf('"') >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))));
                    Check(Path.GetFileName(fn), $"status-only, separator '{(d == '\t' ? "tab" : d.ToString())}'", content, rows.Count, failures);
                }
            }

            if (failures.Count > 0)
                throw new XunitException(failures.Count + " variant(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        // A report needs a start time from "Date/time" or "UTC date/time"; one of the two is enough.
        [Fact]
        public void StartTimeIsRequired()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            int snCol = Array.IndexOf(header, "Serial number"), localCol = Array.IndexOf(header, "Date/time"), utcCol = Array.IndexOf(header, "UTC date/time");

            rows[1][utcCol] = "";                                  // local time only: submitted
            rows[2][localCol] = "";                                // UTC time only: submitted, local derived from UTC
            rows[3][localCol] = ""; rows[3][utcCol] = "";          // neither: rejected
            rows[4][localCol] = "not a date"; rows[4][utcCol] = "";// unrecognised: rejected
            var expectedSerials = rows.Where((r, i) => i != 3 && i != 4).Select(r => r[snCol]).ToList();

            Convert(new ExportWizardConverter(), Join(header, rows));

            var submitted = _submitted.Cast<UUTReport>().ToList();
            var utcOnly = submitted[2]; // rows 0-2 are submitted in order; serial numbers repeat in this sample
            if (!submitted.Select(r => r.SerialNumber).SequenceEqual(expectedSerials)
                || utcOnly.StartDateTime != utcOnly.StartDateTimeUTC.ToLocalTime())
                throw new XunitException("Submitted: " + string.Join(", ", submitted.Select(r => r.SerialNumber + "@" + r.StartDateTime.ToString("s"))));
        }

        // Revision, test operation, station and operator parameters (listed in the Configurator) fill in those
        // fields when the file lacks them; a value in the file always wins.
        [Fact]
        public void HeaderParametersFillMissingFields()
        {
            InitializeAPI(false);
            // Listed with the default that is actually used when neither the file nor a parameter has a value
            var listed = new ExportWizardConverter().ConverterParameters;
            var expectedDefaults = new Dictionary<string, string>
            {
                ["PartNumber"] = "PN1", ["PartRevisionNumber"] = "", ["OperationTypeCode"] = "10", ["StationName"] = "", ["Operator"] = "operator"
            };
            foreach (var kv in expectedDefaults)
                if (!listed.TryGetValue(kv.Key, out var value) || value != kv.Value)
                    throw new XunitException($"Configurator default for {kv.Key}: '{value}', expected '{kv.Value}'");

            // With those defaults and no values in the file, the report gets exactly the listed values
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var h0, out var r0, out _);
            var noValues = new HashSet<int>(new[] { "Part number", "Test operation", "Operator" }.Select(c => Array.IndexOf(h0, c)));
            Convert(new ExportWizardConverter(), Join(h0, BlankColumns(r0, noValues)));
            var defaultsOff = _submitted.Cast<UUTReport>().Where(r => r.PartNumber != "PN1" || r.OperationType.Code != "10" || r.Operator != "operator").ToList();
            Convert(new ExportWizardConverter(new Dictionary<string, string>()), Join(h0, BlankColumns(r0, noValues))); // nothing configured at all
            defaultsOff.AddRange(_submitted.Cast<UUTReport>().Where(r => r.PartNumber != "PN1" || r.OperationType.Code != "10" || r.Operator != "operator"));
            if (defaultsOff.Count > 0)
                throw new XunitException("Defaults not applied: " + string.Join("; ", defaultsOff.Select(r => $"{r.PartNumber}/{r.OperationType.Code}/{r.Operator}")));

            var process = GetOperationTypes().FirstOrDefault(o => o.Code != "10");
            if (process == null) throw new XunitException("No process other than 10 known to the API - cannot test OperationTypeCode");
            var args = new Dictionary<string, string>
            {
                ["PartRevisionNumber"] = "REV-P", ["OperationTypeCode"] = process.Code, ["StationName"] = "STATION-P", ["Operator"] = "operator-p"
            };

            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            var drop = new HashSet<int>(new[] { "Revision", "Test operation", "Station name", "Operator" }.Select(h => Array.IndexOf(header, h)));

            Convert(new ExportWizardConverter(args), Join(DropColumns(header, rows, drop, out var kept), kept));
            var bad = _submitted.Cast<UUTReport>().Where(r => r.PartRevisionNumber != "REV-P" || r.OperationType.Code != process.Code
                                                              || r.StationName != "STATION-P" || r.Operator != "operator-p").ToList();
            if (_submitted.Count != rows.Count || bad.Count > 0)
                throw new XunitException("From parameters: " + string.Join("; ", _submitted.Cast<UUTReport>().Select(r => $"{r.PartRevisionNumber}/{r.OperationType.Code}/{r.StationName}/{r.Operator}")));

            // With the columns present: the file's value where the cell has one, the parameter only for empty cells
            Convert(new ExportWizardConverter(args), Join(header, rows));
            string Expected(string[] row, string column, string parameter)
            {
                var cell = row[Array.IndexOf(header, column)];
                return string.IsNullOrWhiteSpace(cell) ? parameter : cell.Trim();
            }
            var reports = _submitted.Cast<UUTReport>().ToList();
            for (int i = 0; i < rows.Count; i++)
                if (reports[i].PartRevisionNumber != Expected(rows[i], "Revision", "REV-P")
                    || reports[i].StationName != Expected(rows[i], "Station name", "STATION-P")
                    || reports[i].Operator != Expected(rows[i], "Operator", "operator-p"))
                    throw new XunitException($"Row {i + 1}: {reports[i].PartRevisionNumber}/{reports[i].StationName}/{reports[i].Operator} - a parameter overrode a value in the file, or did not fill an empty one");
        }

        // A sequence called twice shares one step path; the export shows the second call only by column order (its
        // steps come after another sequence). Steps that only later rows have are appended at the end of the header,
        // so their position says nothing and must not start a new call.
        [Fact]
        public void RepeatedSequenceCallsAreSeparated()
        {
            InitializeAPI(false);
            var header = new List<string> { "Serial number", "Part number", "Test operation", "Status", "Date/time" };
            void Seq(string name, string path) => header.AddRange(new[] { "|<- " + name, name + " (File name)", name + " (Status)", name + " (Step path) ->|" });
            void Num(string name) => header.AddRange(new[] { "|<- " + name, name + " (Low limit)", name + " (High limit)", name + " (Comp operator)", name + " (Status)", name + " (Step path) ->|" });
            Seq("MainSequence Callback", ""); Seq("A", ""); Num("a1"); Seq("B", ""); Num("b1"); Num("a2");

            string[] Row(string sn, bool withA2, string a2Status) => new[]
            {
                sn, "PN-REPEAT", "SW Debug", a2Status == "Failed" ? "Failed" : "Passed", "2026-Jan-15 10:00:00",
                "MainSequence Callback", "seq.seq", "Passed", "MainSequence Callback",
                "A", "seq.seq", "Passed", "MainSequence Callback/A",
                "5", "0", "10", "GELE", "Passed", "MainSequence Callback/A/a1",
                "B", "seq.seq", "Passed", "MainSequence Callback/B",
                "5", "0", "10", "GELE", "Passed", "MainSequence Callback/B/b1",
                withA2 ? "50" : "", withA2 ? "0" : "", withA2 ? "10" : "", withA2 ? "GELE" : "", withA2 ? a2Status : "", withA2 ? "MainSequence Callback/A/a2" : ""
            };
            int Depth(Step s) { int d = 0; for (var p = s.Parent; p != null; p = p.Parent) d++; return d; }
            string TopLevel(UUTReport r) => string.Join(", ", r.AllSteps.Where(s => Depth(s) == 1).Select(s => s.Name + "=" + s.Status));

            // a2 in the first row: a second call of A after B; it has no status column of its own, so it takes a2's
            Convert(new ExportWizardConverter(), Join(header.ToArray(), new List<string[]> { Row("REPEAT-1", true, "Failed") }));
            var repeated = TopLevel((UUTReport)_submitted.Single());
            if (repeated != "A=Passed, B=Passed, A=Failed")
                throw new XunitException("Repeated call: " + repeated);

            // a2 only in a later row: its columns were appended, so it belongs to the one call of A
            Convert(new ExportWizardConverter(), Join(header.ToArray(), new List<string[]> { Row("REPEAT-2", false, ""), Row("REPEAT-3", true, "Passed") }));
            var appended = TopLevel(_submitted.Cast<UUTReport>().Last());
            if (appended != "A=Passed, B=Passed")
                throw new XunitException("Step only in a later row: " + appended);
        }

        // The Configurator lists PartNumber with the default PN1, which fills in a missing part number.
        [Fact]
        public void PartNumberParameterDefaultsToPN1()
        {
            InitializeAPI(false);
            var converter = new ExportWizardConverter();
            if (!converter.ConverterParameters.TryGetValue("PartNumber", out var pn) || pn != "PN1")
                throw new XunitException("Default PartNumber parameter: " + (pn ?? "<missing>"));

            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2025-12-02_09-13-44.txt"), out var header, out var rows, out _);
            Convert(converter, Join(header, BlankColumns(rows, new HashSet<int> { Array.IndexOf(header, "Part number") })));
            if (_submitted.Cast<UUTReport>().Any(r => r.PartNumber != "PN1"))
                throw new XunitException("Part numbers: " + string.Join(", ", _submitted.Cast<UUTReport>().Select(r => r.PartNumber)));
        }

        // "<name> - String" / "<name> - Numeric" columns form one misc info item, whatever the name.
        [Fact]
        public void MiscInfoIsImportedByName()
        {
            InitializeAPI(false);
            LoadSample(Path.Combine(DataDir, "WATSExport_UUT_2026-09-28_08-43-40.txt"), out var header, out var rows, out _);
            Convert(new ExportWizardConverter(), Join(header, rows));

            var uut = (UUTReport)_submitted.Single();
            var misc = uut.MiscInfo.Select(m => $"{m.Description}|{m.DataString}|{m.DataNumeric}").ToList();
            var expected = new[] { "MiscInfoNumeric||100", "MiscInfoString|Hello|0", "InvalidXML|This is a test with < and >|0" };
            if (!misc.SequenceEqual(expected) || uut.BatchSerialNumber != "BatchSN")
                throw new XunitException("Misc info: " + string.Join("; ", misc) + " / batch serial: " + uut.BatchSerialNumber);
        }

        private void Convert(ExportWizardConverter converter, string content)
        {
            _submitted.Clear();
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                converter.ImportReport(this, ms);
        }

        private void Check(string file, string variant, string content, int expectedReports, List<string> failures)
        {
            try
            {
                Convert(new ExportWizardConverter(), content);
                if (_submitted.Count != expectedReports)
                    failures.Add($"{file} / {variant}: {_submitted.Count} reports, expected {expectedReports}");
            }
            catch (InvalidDataException ex) when (expectedReports == 0 && _submitted.Count == 0 && ex.Message.Contains("serial number"))
            {
                // Expected: every row lacks a serial number, so the whole file is rejected
            }
            catch (Exception ex)
            {
                failures.Add($"{file} / {variant}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The first 20 reports are enough to cover a file's column layout.
        private static void LoadSample(string fn, out string[] header, out List<string[]> rows, out int firstStep)
        {
            var lines = File.ReadAllLines(fn, Encoding.UTF8).Where(l => l.Length > 0).Take(21).ToArray();
            header = lines[0].Split('\t');
            rows = lines.Skip(1).Select(l => l.Split('\t')).ToList();
            var h = header;
            firstStep = Array.FindIndex(h, x => x.TrimStart().StartsWith("|<-", StringComparison.Ordinal));
            if (firstStep < 0) firstStep = h.Length;
        }

        // Metadata columns one by one, step columns grouped by field kind ("(Status)", "(Step path)", ..., "<value>").
        private static Dictionary<string, HashSet<int>> ColumnGroups(string[] header, int firstStep)
        {
            var groups = new Dictionary<string, HashSet<int>>();
            for (int i = 0; i < header.Length; i++)
            {
                var key = i < firstStep ? "'" + header[i] + "'" : FieldKind(header[i]);
                if (!groups.TryGetValue(key, out var set)) groups[key] = set = new HashSet<int>();
                set.Add(i);
            }
            return groups;
        }

        private static string FieldKind(string header)
        {
            var h = header.Replace("|<-", "").Replace("->|", "").Trim();
            int lp = h.LastIndexOf('(');
            if (lp >= 0 && h.EndsWith(")", StringComparison.Ordinal))
            {
                var field = h.Substring(lp + 1, h.Length - lp - 2).Trim();
                if (!field.StartsWith("Loop index", StringComparison.Ordinal)) return "(" + field + ")";
            }
            return "<value>";
        }

        // Removes columns the way an export without those fields would look: a removed "|<-" opener moves
        // to the next kept column of its group, a removed "->|" closer to the previous kept one.
        private static string[] DropColumns(string[] header, List<string[]> rows, HashSet<int> drop, out List<string[]> keptRows)
        {
            var h = (string[])header.Clone();
            foreach (int i in drop.OrderBy(x => x))
            {
                var t = h[i].Trim();
                if (t.StartsWith("|<-", StringComparison.Ordinal))
                {
                    for (int j = i + 1; j < h.Length && !h[j].TrimStart().StartsWith("|<-", StringComparison.Ordinal); j++)
                        if (!drop.Contains(j)) { h[j] = "|<- " + h[j].Trim(); break; }
                }
                if (t.EndsWith("->|", StringComparison.Ordinal))
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (drop.Contains(j)) { if (h[j].TrimStart().StartsWith("|<-", StringComparison.Ordinal)) break; continue; }
                        if (h[j].Trim().Length > 0 && !h[j].TrimEnd().EndsWith("->|", StringComparison.Ordinal)) h[j] = h[j].TrimEnd() + " ->|";
                        break;
                    }
                }
            }

            var keep = Enumerable.Range(0, h.Length).Where(i => !drop.Contains(i)).ToArray();
            keptRows = rows.Select(r => keep.Select(i => i < r.Length ? r[i] : "").ToArray()).ToList();
            return keep.Select(i => h[i]).ToArray();
        }

        private static List<string[]> BlankColumns(List<string[]> rows, HashSet<int> cols)
            => rows.Select(r => r.Select((c, i) => cols.Contains(i) ? "" : c).ToArray()).ToList();

        private static string Join(string[] header, List<string[]> rows)
            => string.Join("\r\n", new[] { string.Join("\t", header) }.Concat(rows.Select(r => string.Join("\t", r))));

        // Comma separated with RFC 4180 quoting.
        private static string JoinCsv(string[] header, List<string[]> rows)
            => string.Join("\r\n", new[] { header }.Concat(rows).Select(r => string.Join(",", r.Select(c =>
                   c.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))));
    }
}
