using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.WATS.ExportWizard
{
    public class ExportWizardConverter : IReportConverter_v2
    {
        private const string LoopIndexToken = "(Loop index:";
        private const int MaxReportTextLength = 4000;

        // Fallbacks used when a column is missing or empty (see PopulateUut). Keys are case-insensitive.
        public Dictionary<string, string> ConverterParameters { get; }

        // Defaults used when neither the file nor a converter parameter gives a value. The Configurator shows the
        // same values, so what it lists is what is actually used.
        private const string DefaultPartNumber = "PN1";
        private const string DefaultOperationTypeCode = "10";
        private const string DefaultOperator = "operator";

        // Used by the WATS Client Configurator to list the converter's parameters. Each is only used when the file
        // has no value for that field. Empty revision and station mean: no revision, the WATS Client's own station.
        public ExportWizardConverter()
        {
            ConverterParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PartNumber"] = DefaultPartNumber,
                ["PartRevisionNumber"] = string.Empty,
                ["OperationTypeCode"] = DefaultOperationTypeCode,
                ["StationName"] = string.Empty,
                ["Operator"] = DefaultOperator
            };
        }

        // Used by the WATS Client at runtime with the parameters configured for this converter.
        public ExportWizardConverter(Dictionary<string, string> args)
        {
            ConverterParameters = args == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(args, StringComparer.OrdinalIgnoreCase);
        }

        public void CleanUp() { }

        private readonly Dictionary<string, NumericLimitStep> _multiNumericSteps = new Dictionary<string, NumericLimitStep>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PassFailStep> _multiPassFailSteps = new Dictionary<string, PassFailStep>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StringValueStep> _multiStringSteps = new Dictionary<string, StringValueStep>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Step> _loopSummaries = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<SequenceCall, string> _openLoopByParent = new Dictionary<SequenceCall, string>();

        // Repeated calls of one sequence share a step path, so the export can't separate them by path. Column order
        // can: per parent sequence, the child sequence entered last; returning to an earlier child after another
        // one starts a new call of it (see GetOrCreateSequence).
        // The Export Wizard adds a step's columns where the step is first seen while reading the reports in order, so
        // positions follow execution order only for steps the first report has. Steps that only later reports have
        // (e.g. a newer sequence version) are appended at the end, so they never start a new call.
        private readonly Dictionary<SequenceCall, string> _lastChildSequencePath = new Dictionary<SequenceCall, string>();
        private readonly Dictionary<SequenceCall, StepStatusType> _repeatedCallStatus = new Dictionary<SequenceCall, StepStatusType>();
        // Step.Parent returns a new wrapper object on every call, so the tree is tracked here: each sequence the
        // converter creates -> the sequence it was added to, and the sequence the last created step went into.
        private readonly Dictionary<SequenceCall, SequenceCall> _sequenceParent = new Dictionary<SequenceCall, SequenceCall>();
        private SequenceCall _createdUnder;
        private HashSet<int> _columnsInFirstRow;     // columns with a value in the first data row
        private bool _stepPositionFollowsExecution;  // the step being created has a value in the first data row

        private static readonly string[] SkipIgnoredKeys =
        {
            "Status", "Step time", "Step path", "Report text", "File name", "Version", "Comp operator", "Operator", "Result", "Name"
        };

        // Every "(Field)" suffix this export format is known to emit. Used to tell a real field-column
        // apart from a bare step/marker name that just happens to contain parens (e.g. "Foo (Loop index: 0)").
        private static readonly HashSet<string> KnownStepFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Status", "Step time", "Step path", "Report text", "File name", "Version",
            "Limit", "Low limit", "High limit", "Units", "Comp operator",
            "Value format", "Limit format", "Low limit format", "High limit format",
            "Response", "Button format", "Measurement"
        };

        private static readonly string[] DateFormats =
        {
            "yyyy-MMM-dd HH:mm:ss",
            "yyyy-MMM-dd HH:mm:ss.FFF",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.FFF"
        };

        private enum StepKind { Numeric, PassFail, StringValue, Action, CallExe, MessagePopup }

        private sealed class StepFieldValues
        {
            public string Name;
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> FieldIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public bool IsSequence;
            public bool ForceAction;
            public bool NoCompOperatorColumn; // the export has no "(Comp operator)" columns at all
            public int MeasurementColIndex = -1;

            // Populated by ApplyLoopPathDisambiguation: identifies which loop (if any) this step belongs to.
            public string LoopBaseKey;      // container path with the loop suffix stripped - same for every iteration of one loop
            public string LoopIterationKey; // container path WITH the loop suffix - same for every measurement of one iteration
            public int? LoopIterationIndex; // null for the loop's own unindexed/summary entry
        }

        private sealed class LoopInfo
        {
            public int Iterations;
            public int Passed;
            public int Failed;
            public int EndingIndex;
            public StepKind Kind; // dominant kind across the loop's members - all iterations must share one type
        }

        private sealed class MetaColumns
        {
            public int Serial = -1, PartNumber = -1, Revision = -1, Operation = -1, Station = -1, Location = -1,
                       Purpose = -1, LocalDateTime = -1, UtcDateTime = -1, ExecutionTime = -1, BatchSerial = -1,
                       SocketIndex = -1, Operator = -1, FixtureId = -1, ErrorCode = -1, ErrorMessage = -1,
                       Status = -1, SwFilename = -1, SwVersion = -1, Comment = -1, Run = -1;

            public HashSet<int> MappedIndices() => new HashSet<int>
            {
                Serial, PartNumber, Revision, Operation, Station, Location, Purpose, LocalDateTime, UtcDateTime,
                ExecutionTime, FixtureId, ErrorCode, ErrorMessage, BatchSerial, SocketIndex, Operator, Status,
                SwFilename, SwVersion, Comment, Run
            };
        }

        private enum LimitShape { None, Single, Range }

        // Resolved numeric measurement: which AddTest/AddMultipleTest overload to use, and its arguments.
        private sealed class NumericValues
        {
            public double Measured, Low, High, Limit;
            public string Units;
            public CompOperatorType Op;
            public LimitShape Shape;
        }

        public Report ImportReport(TDM api, Stream file)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (file == null) throw new ArgumentNullException(nameof(file));

            api.TestMode = TestModeType.Import;
            _columnsInFirstRow = null; // per file: the first data row defines which column positions follow execution order

            // StreamReader detects UTF-8/UTF-16/UTF-32 BOMs itself; UTF-8 is the fallback.
            using (var sr = new StreamReader(file, Encoding.UTF8, true, 1024, leaveOpen: true))
            {
                ReadHeader(sr, out var delimiter, out var headers, out var firstGroupCol, out var metaMap);

                var stepStatusColumns = new List<KeyValuePair<int, string>>(); // column -> step name
                for (int i = firstGroupCol; i < headers.Length; i++)
                {
                    ParseStepHeader(headers[i] ?? string.Empty, out var stepName, out var field);
                    if (string.Equals(field, "Status", StringComparison.OrdinalIgnoreCase)) stepStatusColumns.Add(new KeyValuePair<int, string>(i, stepName));
                }

                UUTReport last = null;
                int row = 0, rowsWithoutSerial = 0, rowsTooWide = 0, rowsBadStatus = 0, rowsWithoutStartTime = 0;
                string line;
                while ((line = ReadRecord(sr, delimiter)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cells = Split(line, delimiter);
                    if (cells.Length == 0 || cells.All(string.IsNullOrEmpty)) continue;
                    row++;
                    if (row == 1)
                        _columnsInFirstRow = new HashSet<int>(Enumerable.Range(0, cells.Length).Where(i => !string.IsNullOrWhiteSpace(cells[i])));

                    // More cells than header columns: a value contains the separator without being quoted, so every
                    // later cell is shifted into the wrong column. Never guess which ones belong together.
                    if (cells.Length > headers.Length)
                    {
                        rowsTooWide++;
                        LogError(api, $"Data row {row}: {cells.Length} cells but the header has {headers.Length} columns - a value contains an unquoted"
                                      + $" {DelimiterName(delimiter)}; report not submitted (serial number '{Get(cells, metaMap.Serial)}'). Put values containing it in double quotes.");
                        continue;
                    }

                    // The serial number identifies the unit and has no fallback: such a row is never submitted.
                    if (Get(cells, metaMap.Serial) == null)
                    {
                        rowsWithoutSerial++;
                        LogError(api, $"Data row {row}: no serial number - report not submitted"
                                      + $" (part number '{Get(cells, metaMap.PartNumber)}', date/time '{Get(cells, metaMap.LocalDateTime) ?? Get(cells, metaMap.UtcDateTime)}').");
                        continue;
                    }

                    // Only the agreed status values are accepted; anything else would be guessed at, so the report is rejected.
                    var unknownStatus = FindUnknownStatus(cells, metaMap, stepStatusColumns);
                    if (unknownStatus != null)
                    {
                        rowsBadStatus++;
                        LogError(api, $"Data row {row}: unknown {unknownStatus} - report not submitted (serial number '{Get(cells, metaMap.Serial)}')."
                                      + " Accepted (any case): Passed/Pass/P, Failed/Fail/F, Error, Terminated; for steps also Skipped/Skip, Done.");
                        continue;
                    }

                    // The start time is required: without it WATS would use the import time, and importing the
                    // file again would create new reports instead of overwriting these.
                    var missingStartTime = FindMissingStartTime(cells, metaMap);
                    if (missingStartTime != null)
                    {
                        rowsWithoutStartTime++;
                        LogError(api, $"Data row {row}: no start date/time - {missingStartTime}; report not submitted (serial number '{Get(cells, metaMap.Serial)}').");
                        continue;
                    }

                    var uut = CreateUut(api, cells, metaMap);
                    PopulateUut(uut, cells, metaMap);
                    PopulateMisc(uut, headers, cells, metaMap, firstGroupCol);
                    PopulateSubUnits(uut, headers, cells);

                    var root = uut.GetRootSequenceCall();
                    var sequenceCache = new Dictionary<string, SequenceCall>(StringComparer.OrdinalIgnoreCase)
                    {
                        [string.Empty] = root
                    };

                    _multiNumericSteps.Clear();
                    _multiPassFailSteps.Clear();
                    _multiStringSteps.Clear();
                    _loopSummaries.Clear();
                    _openLoopByParent.Clear();
                    _lastChildSequencePath.Clear();
                    _repeatedCallStatus.Clear();
                    _sequenceParent.Clear();

                    bool anyFail = BuildAllSteps(root, headers, cells, firstGroupCol, sequenceCache);
                    foreach (var kv in _repeatedCallStatus) kv.Key.Status = kv.Value;

                    // Use report-level status from CSV if available, otherwise infer from steps
                    var reportStatus = Get(cells, metaMap.Status);
                    if (!string.IsNullOrWhiteSpace(reportStatus))
                        uut.Status = MapUutStatus(reportStatus);
                    else
                        uut.Status = anyFail ? UUTStatusType.Failed : UUTStatusType.Passed;

                    api.Submit(uut);
                    last = uut;
                }

                if (last == null)
                {
                    var rejected = new List<string>();
                    if (rowsWithoutSerial > 0) rejected.Add($"{rowsWithoutSerial} missing a serial number");
                    if (rowsTooWide > 0) rejected.Add($"{rowsTooWide} with more cells than the header (unquoted {DelimiterName(delimiter)} in a value)");
                    if (rowsBadStatus > 0) rejected.Add($"{rowsBadStatus} with an unknown status");
                    if (rowsWithoutStartTime > 0) rejected.Add($"{rowsWithoutStartTime} without a start date/time");
                    throw new InvalidDataException(rejected.Count == 0
                        ? "No data rows found."
                        : "No report submitted: every data row was rejected - " + string.Join(", ", rejected) + ".");
                }
                return last;
            }
        }

        private static readonly HashSet<string> ReportStatusValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Passed", "Pass", "P", "Failed", "Fail", "F", "Error", "Terminated"
        };

        private static readonly HashSet<string> StepStatusValues = new HashSet<string>(ReportStatusValues, StringComparer.OrdinalIgnoreCase)
        {
            "Skipped", "Skip", "Done"
        };

        // The first report or step status that is not one of the agreed values, described for the log; null when all are valid.
        private static string FindUnknownStatus(string[] cells, MetaColumns meta, List<KeyValuePair<int, string>> stepStatusColumns)
        {
            var report = Get(cells, meta.Status);
            if (report != null && !ReportStatusValues.Contains(report)) return $"report status '{report}'";

            foreach (var column in stepStatusColumns)
            {
                var value = Get(cells, column.Key);
                if (value != null && !StepStatusValues.Contains(value)) return $"status '{value}' of step '{column.Value}'";
            }
            return null;
        }

        // Why this row has no usable start time, for the log; null when "Date/time" or "UTC date/time" holds a
        // recognised date. There is deliberately no parameter fallback: the start time differs per report.
        private static string FindMissingStartTime(string[] cells, MetaColumns meta)
        {
            string local = Get(cells, meta.LocalDateTime), utc = Get(cells, meta.UtcDateTime);
            if (ParseDate(local).HasValue || ParseDate(utc).HasValue) return null;

            if (meta.LocalDateTime < 0 && meta.UtcDateTime < 0) return "the file has no 'Date/time' or 'UTC date/time' column";
            if (local == null && utc == null) return "'Date/time' and 'UTC date/time' are empty";
            return local != null
                ? $"'Date/time' value '{local}' is not a recognised date (use e.g. 2026-Jan-15 10:00:00 or 2026-01-15 10:00:00)"
                : $"'UTC date/time' value '{utc}' is not a recognised date (use e.g. 2026-Jan-15 09:00:00 or 2026-01-15 09:00:00)";
        }

        private static string DelimiterName(char d)
            => d == '\t' ? "tab" : d == ',' ? "comma" : d == ';' ? "semicolon" : "'" + d + "'";

        // Goes to the WATS Client's error log for the file being converted; Trace when run outside the client (tests).
        private static void LogError(TDM api, string message)
        {
            var source = api.ConversionSource;
            if (source != null) source.LogErrorMessage(message);
            else System.Diagnostics.Trace.TraceError(message);
        }

        private bool BuildAllSteps(
            SequenceCall root,
            string[] headers,
            string[] cells,
            int startIndex,
            Dictionary<string, SequenceCall> sequenceCache)
        {
            bool anyFail = false;

            var stepColumnIndexes = Enumerable.Range(startIndex, Math.Max(0, headers.Length - startIndex)).ToList();
            var steps = ParseSegmentOrdered(headers, cells, stepColumnIndexes);

            // Exports can leave out step fields; compensate where another field still carries the information.
            bool noCompOperatorColumn = !HasStepField(headers, startIndex, "Comp operator");
            foreach (var step in steps) step.NoCompOperatorColumn = noCompOperatorColumn;
            if (!HasStepField(headers, startIndex, "File name")) MarkSequencesByChildPaths(steps);

            ApplyLoopPathDisambiguation(steps);
            var loopInfo = ComputeLoopInfo(steps);

            foreach (var step in steps)
            {
                if (IsRootOrMainSequenceCallback(step, root))
                {
                    var rootStatusTxt = GetField(step.Fields, "Status");
                    anyFail |= IsFailing(MapStepStatus(rootStatusTxt));
                    if (!string.IsNullOrWhiteSpace(rootStatusTxt)) root.Status = MapStepStatus(rootStatusTxt);

                    var rootPath = GetField(step.Fields, "Step path");
                    if (!string.IsNullOrWhiteSpace(rootPath)) GetOrCreateSequence(sequenceCache, rootPath.Trim(), includeLeaf: true, knownLeafName: step.Name);
                    continue;
                }

                if (ShouldSkipStep(step)) continue;

                _createdUnder = null;
                var status = CreateStep(root, step, sequenceCache, loopInfo);
                if (IsFailing(status)) anyFail = true;
                RecordFailureInRepeatedCalls(_createdUnder, status);
            }

            foreach (var kv in _openLoopByParent) kv.Key.StopLoop();

            return anyFail;
        }

        private static bool HasStepField(string[] headers, int startIndex, string fieldName)
        {
            for (int i = startIndex; i < headers.Length; i++)
            {
                ParseStepHeader(headers[i] ?? string.Empty, out _, out var field);
                if (string.Equals(field, fieldName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Sequences are normally recognised by their "(File name)" column. Without it, a step is a sequence
        // when another step's path lies inside its own path.
        private static void MarkSequencesByChildPaths(List<StepFieldValues> steps)
        {
            var parentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps)
            {
                var path = OwnPath(step);
                for (int slash = path?.LastIndexOf('/') ?? -1; slash > 0; slash = path.LastIndexOf('/', slash - 1))
                    parentPaths.Add(path.Substring(0, slash));
            }

            foreach (var step in steps)
            {
                var path = OwnPath(step);
                if (path != null && path.IndexOf("//", StringComparison.Ordinal) < 0 && parentPaths.Contains(path)) step.IsSequence = true;
            }
        }

        // A step's "Step path" without any "//measurement" suffix (multi-measurement containers are steps, not sequences).
        private static string OwnPath(StepFieldValues step)
        {
            var path = GetField(step.Fields, "Step path")?.Trim();
            if (string.IsNullOrEmpty(path)) return null;
            int multi = path.IndexOf("//", StringComparison.Ordinal);
            return multi >= 0 ? path.Substring(0, multi) : path;
        }

        private UUTReport CreateUut(TDM api, string[] cells, MetaColumns meta)
        {
            string sn = Get(cells, meta.Serial); // never null: ImportReport skips rows without one
            // File value first, then the converter parameter, then the default the Configurator lists
            string pn = Get(cells, meta.PartNumber) ?? Param("PartNumber") ?? DefaultPartNumber;
            string rev = Get(cells, meta.Revision) ?? Param("PartRevisionNumber") ?? string.Empty;
            string op = Get(cells, meta.Operation) ?? Param("OperationTypeCode") ?? DefaultOperationTypeCode;
            string seqName = Get(cells, meta.SwFilename) ?? op;
            string seqVer = Get(cells, meta.SwVersion) ?? (string.IsNullOrEmpty(rev) ? "1.0" : rev);
            string oper = Get(cells, meta.Operator) ?? Param("Operator") ?? DefaultOperator;
            return api.CreateUUTReport(oper, pn, rev, sn, op, seqName, seqVer);
        }

        // A converter parameter's value, or null when it is not configured or empty. Keys are case-insensitive.
        private string Param(string key)
            => ConverterParameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        private void PopulateUut(UUTReport uut, string[] cells, MetaColumns meta)
        {
            // Cell value wins; otherwise fall back to the converter parameter of the same name. Only the
            // parameters the Configurator lists (see the constructor) have a fallback.
            void Assign(string value, string paramKey, Action<string> setter)
            {
                if (!string.IsNullOrWhiteSpace(value)) { setter(value); return; }
                var p = Param(paramKey);
                if (!string.IsNullOrWhiteSpace(p)) setter(p);
            }

            // File value only. These differ per test run (fixture, comment, error, execution time), or the
            // WATS Client already supplies them (location, purpose), so a fixed parameter value would be wrong.
            void FromFile(string value, Action<string> setter)
            {
                if (!string.IsNullOrWhiteSpace(value)) setter(value);
            }

            Assign(Get(cells, meta.PartNumber), "PartNumber", v => uut.PartNumber = v);
            Assign(Get(cells, meta.Revision), "PartRevisionNumber", v => uut.PartRevisionNumber = v);
            Assign(Get(cells, meta.Station), "StationName", v => uut.StationName = v);
            Assign(Get(cells, meta.Operator), "Operator", v => uut.Operator = v);
            FromFile(Get(cells, meta.Location), v => uut.Location = v);
            FromFile(Get(cells, meta.Purpose), v => uut.Purpose = v);
            FromFile(Get(cells, meta.FixtureId), v => uut.FixtureId = v);
            FromFile(Get(cells, meta.Comment), v => uut.Comment = v);

            // The start time differs per report, so it only ever comes from the row; ImportReport has already
            // rejected rows where neither column holds a recognised date (FindMissingStartTime).
            var dtLocal = ParseDate(Get(cells, meta.LocalDateTime));
            if (dtLocal.HasValue) uut.StartDateTime = dtLocal.Value;

            var dtUtc = ParseDate(Get(cells, meta.UtcDateTime));
            if (dtUtc.HasValue) uut.StartDateTimeUTC = dtUtc.Value;

            // Only the UTC time exported: derive the local start from it (in this machine's time zone).
            if (!dtLocal.HasValue && dtUtc.HasValue)
                uut.StartDateTime = DateTime.SpecifyKind(dtUtc.Value, DateTimeKind.Utc).ToLocalTime();

            if (TryDouble(Get(cells, meta.ExecutionTime), out var exec)) uut.ExecutionTime = exec;

            var batchSerial = Get(cells, meta.BatchSerial);
            if (batchSerial != null) uut.BatchSerialNumber = batchSerial;
            if (meta.SocketIndex >= 0 && short.TryParse(Get(cells, meta.SocketIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var site))
                uut.TestSocketIndex = site;
            if (meta.ErrorCode >= 0 && int.TryParse(Get(cells, meta.ErrorCode), NumberStyles.Integer, CultureInfo.InvariantCulture, out var err))
                uut.ErrorCode = err;

            FromFile(Get(cells, meta.ErrorMessage), v => uut.ErrorMessage = v);
        }

        // The Export Wizard writes each misc info item as "<name> - String", "<name> - Numeric" and (optionally)
        // "<name> - Numeric format" columns; together they are ONE item. The name is free text.
        private static readonly Regex MiscInfoColumn =
            new Regex(@"^(?<name>.+?)\s+-\s+(?<part>String|Numeric|Numeric format)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void PopulateMisc(UUTReport uut, string[] headers, string[] cells, MetaColumns meta, int stop)
        {
            var mapped = meta.MappedIndices();
            var miscNames = new List<string>();
            var miscParts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            int limit = Math.Min(stop, headers.Length);
            for (int i = 0; i < limit; i++)
            {
                if (mapped.Contains(i)) continue;

                string h = headers[i];
                string v = Get(cells, i);
                if (string.IsNullOrWhiteSpace(h)) continue;

                var misc = MiscInfoColumn.Match(h.Trim());
                if (misc.Success && !h.TrimStart().StartsWith("subunit", StringComparison.OrdinalIgnoreCase))
                {
                    var name = misc.Groups["name"].Value.Trim();
                    if (!miscParts.TryGetValue(name, out var parts))
                    {
                        miscParts[name] = parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        miscNames.Add(name);
                    }
                    if (v != null) parts[misc.Groups["part"].Value] = v;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(v)) continue;

                // Skip format columns (e.g., "Execution time format", "Value format")
                if (h.IndexOf("format", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Skip WATS link and Report ID columns
                if (h.IndexOf("wats link", StringComparison.OrdinalIgnoreCase) >= 0 || h.IndexOf("report id", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Skip product name (already mapped)
                if (h.Equals("product name", StringComparison.OrdinalIgnoreCase)) continue;

                // Hand-made misc columns that don't follow the "<name> - String" pattern
                // (sub-unit columns are handled by PopulateSubUnits)
                if (h.IndexOf("misc", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddMiscIfValue(uut, SanitizeKey(h), v);
            }

            foreach (var name in miscNames)
            {
                var parts = miscParts[name];
                parts.TryGetValue("String", out var text);
                parts.TryGetValue("Numeric", out var numberText);
                parts.TryGetValue("Numeric format", out var format);

                // WATS stores the numeric part as a 16-bit whole number; anything else is kept as text
                bool hasNumber = short.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
                if (!hasNumber && string.IsNullOrEmpty(text)) text = numberText;
                if (!hasNumber && string.IsNullOrEmpty(text)) continue; // item not used in this report

                var info = hasNumber ? uut.AddMiscUUTInfo(name, text ?? string.Empty, number) : uut.AddMiscUUTInfo(name, text);
                if (!string.IsNullOrEmpty(format)) info.DataNumericFormat = format;
            }
        }

        private void PopulateSubUnits(UUTReport uut, string[] headers, string[] cells)
        {
            var subInfos = new Dictionary<int, Dictionary<string, string>>();

            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i];
                if (string.IsNullOrWhiteSpace(h)) continue;
                var v = Get(cells, i);
                if (string.IsNullOrWhiteSpace(v)) continue;

                var hl = h.ToLowerInvariant();
                if (!hl.Contains("subunit")) continue;

                int number = ExtractTrailingInt(h);
                if (number < 0) number = 0;

                if (!subInfos.TryGetValue(number, out var dict))
                {
                    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    subInfos[number] = dict;
                }

                if (hl.Contains("type")) dict["Type"] = v;
                else if (hl.Contains("pn")) dict["PN"] = v;
                else if (hl.Contains("sn")) dict["SN"] = v;
                else if (hl.Contains("rev")) dict["Rev"] = v;
            }

            foreach (var kv in subInfos.Values)
            {
                kv.TryGetValue("Type", out var t);
                kv.TryGetValue("PN", out var pn);
                kv.TryGetValue("SN", out var sn);
                kv.TryGetValue("Rev", out var rev);

                if (!string.IsNullOrWhiteSpace(pn) || !string.IsNullOrWhiteSpace(sn))
                    uut.AddUUTPartInfo(t ?? string.Empty, pn ?? string.Empty, sn ?? string.Empty, rev ?? string.Empty);
            }
        }

        private static bool ShouldSkipStep(StepFieldValues step)
        {
            // No values in this row: the step (sequence or leaf) wasn't executed for this UUT
            if (step.Fields.Count == 0) return true;
            if (step.IsSequence) return false;

            var measurementVal = GetField(step.Fields, "Measurement");
            if (!string.IsNullOrWhiteSpace(measurementVal)) return false;

            bool hasStatusOnly = step.Fields.Any(kv => string.Equals(kv.Key, "Status", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                                 && step.Fields.All(kv => string.Equals(kv.Key, "Status", StringComparison.OrdinalIgnoreCase)
                                                         || string.IsNullOrWhiteSpace(kv.Value)
                                                         || SkipIgnoredKeys.Contains(kv.Key));
            if (hasStatusOnly) return false;

            // Keep the step if any populated field is meaningful (not an ignored bookkeeping key) or numeric
            foreach (var kv in step.Fields)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;

                bool isIgnored = SkipIgnoredKeys.Any(ik => string.Equals(kv.Key, ik, StringComparison.OrdinalIgnoreCase));
                if (!isIgnored || TryDouble(kv.Value, out _)) return false;
            }

            return true;
        }

        private static void ParseStepHeader(string header, out string stepName, out string fieldName)
        {
            stepName = null;
            fieldName = null;

            var h = header.Trim();
            if (h.StartsWith("|<-", StringComparison.Ordinal) || h.EndsWith("->|", StringComparison.Ordinal))
            {
                h = h.TrimStart('|', '<', '-').TrimEnd('-', '>', '|').Trim();
            }

            int idx = h.LastIndexOf('(');
            if (idx < 0 || !h.EndsWith(")", StringComparison.Ordinal)) return;

            stepName = h.Substring(0, idx).Trim();
            fieldName = h.Substring(idx + 1, h.Length - idx - 2).Trim();
        }

        private SequenceCall GetOrCreateSequence(Dictionary<string, SequenceCall> cache, string fullPath, bool includeLeaf, string knownLeafName = null)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return cache[string.Empty];

            var rawSegments = fullPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var segmentsList = new List<string>();

            for (int i = 0; i < rawSegments.Length; i++)
            {
                var seg = rawSegments[i];
                if (i < rawSegments.Length - 1
                    && seg.Equals("Pass", StringComparison.OrdinalIgnoreCase)
                    && rawSegments[i + 1].Equals("Fail tests", StringComparison.OrdinalIgnoreCase))
                {
                    segmentsList.Add("Pass/Fail tests");
                    i++;
                }
                else
                {
                    segmentsList.Add(seg);
                }
            }

            // WATS's "Step path" field doesn't escape "/" within a step's own name, so a step literally
            // named e.g. "Foo/Bar" gets exported as ".../Foo/Bar" - indistinguishable by naive splitting
            // from a real nested sequence "Foo" containing a step "Bar". Since we always know the step's
            // true (unsplit) name from its header group, collapse the trailing fragments back into one
            // when they reconstitute it.
            if (!string.IsNullOrEmpty(knownLeafName) && knownLeafName.IndexOf('/') >= 0)
            {
                var leafPartCount = knownLeafName.Split('/').Length;
                if (leafPartCount > 1 && segmentsList.Count >= leafPartCount)
                {
                    var tailStart = segmentsList.Count - leafPartCount;
                    var tailJoined = string.Join("/", segmentsList.Skip(tailStart));
                    if (tailJoined.Equals(knownLeafName, StringComparison.OrdinalIgnoreCase))
                    {
                        segmentsList.RemoveRange(tailStart, leafPartCount);
                        segmentsList.Add(knownLeafName);
                    }
                }
            }

            var segments = segmentsList.ToArray();
            int segCount = includeLeaf ? segments.Length : Math.Max(segments.Length - 1, 0);

            var currentPath = string.Empty;
            var current = cache[string.Empty];

            // The root SequenceCall should always correspond to the TRUE main/root sequence group,
            // which is the group that contains columns like "<SeqName> (File name)".
            // Cache it under its actual name (first segment) if present, rather than aliasing by matching names.
            if (segments.Length > 0)
            {
                var rootTitle = segments[0];
                if (!string.IsNullOrWhiteSpace(rootTitle) && !cache.ContainsKey(rootTitle))
                    cache[rootTitle] = current;
            }

            for (int idx = 0; idx < segCount; idx++)
            {
                var seg = segments[idx];
                currentPath = string.IsNullOrEmpty(currentPath) ? seg : currentPath + "/" + seg;

                if (!cache.TryGetValue(currentPath, out var next))
                {
                    next = current.AddSequenceCall(seg);
                    cache[currentPath] = next;
                    _sequenceParent[next] = current;
                }
                else if (next != current // the root is cached under its own name too: not a child
                         && _stepPositionFollowsExecution
                         && _lastChildSequencePath.TryGetValue(current, out var lastChild)
                         && !string.Equals(lastChild, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Back to a sequence after a sibling sequence ran: a new call of it, with fresh sub-sequences
                    next = current.AddSequenceCall(seg);
                    cache[currentPath] = next;
                    _sequenceParent[next] = current;
                    foreach (var key in cache.Keys.Where(k => k.StartsWith(currentPath + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                        cache.Remove(key);
                    _repeatedCallStatus[next] = StepStatusType.Passed;
                }

                if (next != current && _stepPositionFollowsExecution) _lastChildSequencePath[current] = currentPath;
                current = next;
            }

            return current;
        }

        // A repeated sequence call has no status column of its own in the export: Passed, unless a step inside it
        // failed or errored (Done/Skipped steps don't make a sequence Done/Skipped).
        private void RecordFailureInRepeatedCalls(SequenceCall container, StepStatusType status)
        {
            if (_repeatedCallStatus.Count == 0 || !IsFailing(status)) return;
            for (var seq = container; seq != null; seq = _sequenceParent.TryGetValue(seq, out var up) ? up : null)
                if (_repeatedCallStatus.TryGetValue(seq, out var current)) _repeatedCallStatus[seq] = Worst(current, status);
        }

        private static bool TryExtractMultipleTestParts(string stepPath, out string parent, out string measurementName)
        {
            parent = null;
            measurementName = null;
            if (string.IsNullOrEmpty(stepPath)) return false;

            int idx = stepPath.IndexOf("//", StringComparison.Ordinal);
            if (idx < 0) return false;

            parent = stepPath.Substring(0, idx);
            measurementName = stepPath.Substring(idx + 2);
            return true;
        }

        private StepStatusType CreateStep(SequenceCall root, StepFieldValues step, Dictionary<string, SequenceCall> sequenceCache, Dictionary<string, LoopInfo> loopInfo)
        {
            var f = step.Fields;
            _stepPositionFollowsExecution = _columnsInFirstRow != null
                && (step.FieldIndices.Values.Any(_columnsInFirstRow.Contains) || _columnsInFirstRow.Contains(step.MeasurementColIndex));

            string statusTxt = GetField(f, "Status");
            StepStatusType status = MapStepStatus(statusTxt);

            TryDouble(GetField(f, "Step time"), out var stepTime);
            string reportText = GetField(f, "Report text");
            string compOp = GetField(f, "Comp operator");
            string rawPath = GetField(f, "Step path");

            LoopInfo thisLoopInfo = null;
            bool isLoopMember = step.LoopBaseKey != null && loopInfo.TryGetValue(step.LoopBaseKey, out thisLoopInfo);

            if (step.IsSequence && string.IsNullOrWhiteSpace(rawPath))
            {
                // No "Step path": the hierarchy is unknown, so the sequence goes directly under the root -
                // the same place leaf steps without a path end up (see GetStepParent).
                EnsureLoopStateForParent(root, null);
                var flatSeq = root.AddSequenceCall(step.Name);
                _sequenceParent[flatSeq] = root;
                _createdUnder = root;
                if (!string.IsNullOrWhiteSpace(statusTxt)) flatSeq.Status = status;
                ApplyCommon(flatSeq, stepTime, reportText);
                return status;
            }

            if (step.IsSequence)
            {
                var seqPath = rawPath.Trim();

                // Sequences normally resolve to THEMSELVES (includeLeaf:true); loop members instead need the
                // TRUE parent (one level up) so we can Start/StopLoop and add each iteration as a sibling of it.
                var trueParent = GetOrCreateSequence(sequenceCache, seqPath, includeLeaf: false, knownLeafName: step.Name);
                EnsureLoopStateForParent(trueParent, isLoopMember ? step.LoopBaseKey : null);

                SequenceCall thisSeq;
                if (isLoopMember)
                {
                    var baseName = LastSegment(step.LoopBaseKey);
                    var summary = GetOrStartLoop(trueParent, step, () => StartLoop<SequenceCall>(trueParent, baseName, thisLoopInfo));

                    thisSeq = step.LoopIterationIndex == null
                        ? (SequenceCall)summary
                        : trueParent.AddSequenceCall(baseName, (short)step.LoopIterationIndex.Value);

                    sequenceCache[seqPath] = thisSeq;
                    _lastChildSequencePath[trueParent] = seqPath; // this iteration is now the parent's current child
                    _sequenceParent[thisSeq] = trueParent;
                    _createdUnder = trueParent;
                }
                else
                {
                    thisSeq = GetOrCreateSequence(sequenceCache, seqPath, includeLeaf: true, knownLeafName: step.Name);
                    _createdUnder = _sequenceParent.TryGetValue(thisSeq, out var up) ? up : null;
                }

                if (!string.IsNullOrWhiteSpace(statusTxt)) thisSeq.Status = status;
                ApplyCommon(thisSeq, stepTime, reportText);
                return status;
            }

            var parent = GetStepParent(root, step, rawPath, sequenceCache, out var isMultiMeasurement, out var multiContainerPath, out var multiMeasurementName);
            _createdUnder = parent;
            EnsureLoopStateForParent(parent, isLoopMember ? step.LoopBaseKey : null);

            var kind = isLoopMember ? thisLoopInfo.Kind : DetermineKind(step);

            if (isMultiMeasurement)
            {
                var containerStepName = isLoopMember
                    ? LastSegment(step.LoopBaseKey)
                    : (multiContainerPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? multiContainerPath);
                return CreateMultiMeasurementStep(parent, kind, containerStepName, step, status, stepTime, reportText, compOp, multiContainerPath, multiMeasurementName, thisLoopInfo);
            }

            return CreateSingleStep(parent, kind, step, status, stepTime, reportText, compOp, thisLoopInfo);
        }

        private SequenceCall GetStepParent(
            SequenceCall root,
            StepFieldValues step,
            string rawPath,
            Dictionary<string, SequenceCall> sequenceCache,
            out bool isMultiMeasurement,
            out string multiContainerPath,
            out string multiMeasurementName)
        {
            isMultiMeasurement = false;
            multiContainerPath = null;
            multiMeasurementName = null;

            if (string.IsNullOrWhiteSpace(rawPath)) return root;

            if (TryExtractMultipleTestParts(rawPath, out multiContainerPath, out multiMeasurementName))
            {
                isMultiMeasurement = true;
                return GetOrCreateSequence(sequenceCache, multiContainerPath.Trim(), includeLeaf: false);
            }

            return GetOrCreateSequence(sequenceCache, rawPath.Trim(), includeLeaf: false, knownLeafName: step.Name);
        }

        private StepStatusType CreateMultiMeasurementStep(
            SequenceCall parent,
            StepKind kind,
            string containerStepName,
            StepFieldValues step,
            StepStatusType status,
            double stepTime,
            string reportText,
            string compOp,
            string multiContainerPath,
            string multiMeasurementName,
            LoopInfo loopInfoOrNull)
        {
            switch (kind)
            {
                case StepKind.Numeric:
                {
                    if (!_multiNumericSteps.TryGetValue(multiContainerPath, out var num))
                    {
                        num = (NumericLimitStep)GetOrCreateLeafStep(parent, StepKind.Numeric, containerStepName, step, loopInfoOrNull);
                        _multiNumericSteps[multiContainerPath] = num;
                        ApplyCommon(num, stepTime, reportText);
                        num.Status = StepStatusType.Passed;
                    }

                    AddNumericMultipleTest(num, step, compOp, multiMeasurementName);
                    return num.Status;
                }
                case StepKind.StringValue:
                {
                    if (!_multiStringSteps.TryGetValue(multiContainerPath, out var str))
                    {
                        str = (StringValueStep)GetOrCreateLeafStep(parent, StepKind.StringValue, containerStepName, step, loopInfoOrNull);
                        _multiStringSteps[multiContainerPath] = str;
                        ApplyCommon(str, stepTime, reportText);
                        str.Status = StepStatusType.Passed;
                    }

                    AddStringMultipleTest(str, step, compOp, multiMeasurementName);
                    return str.Status;
                }
                case StepKind.PassFail:
                case StepKind.Action:
                default:
                {
                    if (!_multiPassFailSteps.TryGetValue(multiContainerPath, out var pf))
                    {
                        pf = (PassFailStep)GetOrCreateLeafStep(parent, StepKind.PassFail, containerStepName, step, loopInfoOrNull);
                        _multiPassFailSteps[multiContainerPath] = pf;
                        ApplyCommon(pf, stepTime, reportText);
                        pf.Status = StepStatusType.Passed;
                    }

                    // Each measurement keeps its own CSV status; the container (which has no status of its
                    // own in the export) gets the worst of them - same as numeric/string multi steps.
                    var name = multiMeasurementName ?? "Result";
                    bool passed = !IsFailing(status);
                    AddWithOptionalStatus(HasExplicitStatus(step),
                        () => pf.AddMultipleTest(passed, name, status),
                        () => pf.AddMultipleTest(passed, name));
                    pf.Status = Worst(pf.Status, status);
                    return pf.Status;
                }
            }
        }

        private StepStatusType CreateSingleStep(SequenceCall parent, StepKind kind, StepFieldValues step, StepStatusType status, double stepTime, string reportText, string compOp, LoopInfo loopInfoOrNull)
        {
            var f = step.Fields;

            // MessagePopup steps carry their own dedicated Response parameter (see below) - only fold
            // "Response" into ReportText for other kinds where it has no proper home.
            if (kind != StepKind.MessagePopup)
            {
                var response = GetField(f, "Response");
                if (!string.IsNullOrWhiteSpace(response))
                {
                    reportText = string.IsNullOrWhiteSpace(reportText) ? response : $"{reportText}; Response: {response}";
                }
            }

            var baseName = loopInfoOrNull != null ? StripLoopIndexSuffix(step.Name) : step.Name;
            short? loopIndex = step.LoopIterationIndex.HasValue ? (short?)step.LoopIterationIndex.Value : null;
            Step created;

            switch (kind)
            {
                case StepKind.Numeric:
                {
                    var num = (NumericLimitStep)GetOrCreateLeafStep(parent, StepKind.Numeric, baseName, step, loopInfoOrNull);
                    AddNumericSingleTest(num, step, compOp);
                    created = num;
                    break;
                }
                case StepKind.StringValue:
                {
                    var str = (StringValueStep)GetOrCreateLeafStep(parent, StepKind.StringValue, baseName, step, loopInfoOrNull);
                    ResolveStringComparison(step, compOp, out var actualValue, out var op, out var expected);

                    if (op == CompOperatorType.LOG || string.IsNullOrWhiteSpace(expected)) str.AddTest(actualValue);
                    else str.AddTest(op, actualValue, expected);
                    created = str;
                    break;
                }
                case StepKind.PassFail:
                {
                    var pf = (PassFailStep)GetOrCreateLeafStep(parent, StepKind.PassFail, baseName, step, loopInfoOrNull);

                    bool passed;
                    var mtxt = GetField(f, "Measurement");
                    if (!string.IsNullOrWhiteSpace(mtxt))
                    {
                        var mv = mtxt.Trim();
                        passed = mv.Equals("true", StringComparison.OrdinalIgnoreCase) || mv.Equals("t", StringComparison.OrdinalIgnoreCase) || mv == "1";
                    }
                    else
                    {
                        passed = !IsFailing(status);
                    }

                    AddWithOptionalStatus(HasExplicitStatus(step),
                        () => pf.AddTest(passed, status),
                        () => pf.AddTest(passed));
                    created = pf;
                    break;
                }
                case StepKind.CallExe:
                {
                    // The CSV never carries an exit code for CallExe steps - default to 0 (the value the API
                    // itself would report for a normal/successful external program call).
                    if (loopInfoOrNull == null)
                    {
                        created = parent.AddCallExeStep(baseName, 0d);
                        break;
                    }

                    var summary = GetOrStartLoop(parent, step, () => StartLoop<CallExeStep>(parent, baseName, loopInfoOrNull));
                    created = loopIndex == null ? (CallExeStep)summary : parent.AddCallExeStep(baseName, 0d, loopIndex.Value);
                    break;
                }
                case StepKind.MessagePopup:
                {
                    var buttonTxt = GetField(f, "Measurement");
                    short.TryParse(buttonTxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buttonPressed);
                    var responseValue = GetField(f, "Response") ?? string.Empty;

                    if (loopInfoOrNull == null)
                    {
                        created = parent.AddMessagePopupStep(baseName, buttonPressed, responseValue);
                        break;
                    }

                    var summary = GetOrStartLoop(parent, step, () => StartLoop<MessagePopupStep>(parent, baseName, loopInfoOrNull));
                    created = loopIndex == null ? (MessagePopupStep)summary : parent.AddMessagePopupStep(baseName, buttonPressed, responseValue, loopIndex.Value);
                    break;
                }
                default:
                {
                    var inferred = InferGenericStepType(step.Name);
                    if (loopInfoOrNull == null)
                    {
                        created = parent.AddGenericStep(inferred, baseName);
                        break;
                    }

                    var summary = GetOrStartLoop(parent, step, () => StartLoop<GenericStep>(parent, baseName, loopInfoOrNull));
                    created = loopIndex == null ? (GenericStep)summary : parent.AddGenericStep(inferred, baseName, loopIndex.Value);
                    break;
                }
            }

            created.Status = status;
            created.StepTime = stepTime;
            if (!string.IsNullOrWhiteSpace(reportText)) created.ReportText = Truncate(reportText, MaxReportTextLength);
            return status;
        }

        // Only sets StepTime/ReportText when the CSV actually carries a value.
        private static void ApplyCommon(Step step, double stepTime, string reportText)
        {
            if (stepTime > 0) step.StepTime = stepTime;
            if (!string.IsNullOrWhiteSpace(reportText)) step.ReportText = Truncate(reportText, MaxReportTextLength);
        }

        // Tries the overload that carries the source status first; falls back to the status-less overload
        // if the API rejects it. Without an explicit status only the status-less overload is used.
        private static void AddWithOptionalStatus(bool hasExplicitStatus, Action withStatus, Action withoutStatus)
        {
            if (!hasExplicitStatus) { withoutStatus(); return; }
            try { withStatus(); }
            catch { withoutStatus(); }
        }

        private static void AddNumericMultipleTest(NumericLimitStep num, StepFieldValues step, string compOp, string measurementName)
        {
            bool hasExplicitStatus = HasExplicitStatus(step);
            var measurementStatus = GetMeasurementStatus(step);
            var v = ResolveNumeric(step, compOp);
            var name = measurementName ?? "Measurement";

            switch (v.Shape)
            {
                case LimitShape.Range:
                    AddWithOptionalStatus(hasExplicitStatus,
                        () => num.AddMultipleTest(v.Measured, v.Op, v.Low, v.High, v.Units, name, measurementStatus),
                        () => num.AddMultipleTest(v.Measured, v.Op, v.Low, v.High, v.Units, name));
                    break;
                case LimitShape.Single:
                    AddWithOptionalStatus(hasExplicitStatus,
                        () => num.AddMultipleTest(v.Measured, v.Op, v.Limit, v.Units, name, measurementStatus),
                        () => num.AddMultipleTest(v.Measured, v.Op, v.Limit, v.Units, name));
                    break;
                default:
                    AddWithOptionalStatus(hasExplicitStatus,
                        () => num.AddMultipleTest(v.Measured, v.Units, name, measurementStatus),
                        () => num.AddMultipleTest(v.Measured, v.Units, name));
                    break;
            }

            num.Status = Worst(num.Status, measurementStatus);
        }

        private static void AddNumericSingleTest(NumericLimitStep num, StepFieldValues step, string compOp)
        {
            var v = ResolveNumeric(step, compOp);

            switch (v.Shape)
            {
                case LimitShape.Range: num.AddTest(v.Measured, v.Op, v.Low, v.High, v.Units); break;
                case LimitShape.Single: num.AddTest(v.Measured, v.Op, v.Limit, v.Units); break;
                default: num.AddTest(v.Measured, v.Units); break;
            }
        }

        // Shared by single and multi numeric steps: measured value, limits, units, and the operator
        // (defaulting LOG to a limit-appropriate operator, and reducing range operators to one bound).
        private static NumericValues ResolveNumeric(StepFieldValues step, string compOp)
        {
            var f = step.Fields;

            double measured = double.NaN;
            if (f.TryGetValue("Measurement", out var mtxt)) TryDouble(mtxt, out measured);

            GetNumericLimitsForStep(step, out var low, out var high, out var singleLimit);

            bool lowPresent = !double.IsNaN(low);
            bool highPresent = !double.IsNaN(high);

            if (double.IsNaN(singleLimit))
            {
                if (lowPresent && !highPresent) singleLimit = low;
                else if (highPresent && !lowPresent) singleLimit = high;
            }

            if (double.IsNaN(measured)) measured = ParseMeasuredValue(f, low, high, singleLimit);

            var v = new NumericValues { Measured = measured, Low = low, High = high, Limit = singleLimit, Units = GetUnits(f), Shape = LimitShape.None };
            var op = MapCompOperator(compOp);

            if (lowPresent && highPresent)
            {
                v.Op = op == CompOperatorType.LOG ? CompOperatorType.GELE : op;
                v.Shape = LimitShape.Range;
            }
            else if (!double.IsNaN(singleLimit))
            {
                if (op == CompOperatorType.LOG) op = lowPresent ? CompOperatorType.GE : (highPresent ? CompOperatorType.LE : CompOperatorType.LOG);
                v.Op = ReduceToSingleBoundOperator(op, lowPresent: lowPresent, highPresent: highPresent);
                if (v.Op != CompOperatorType.LOG) v.Shape = LimitShape.Single;
            }

            return v;
        }

        private static void GetNumericLimitsForStep(StepFieldValues step, out double low, out double high, out double single)
        {
            low = high = single = double.NaN;

            var f = step.Fields;
            if (f.TryGetValue("Low limit", out var lowTxt) && TryDouble(lowTxt, out var l)) low = l;
            if (f.TryGetValue("High limit", out var highTxt) && TryDouble(highTxt, out var h)) high = h;
            if (f.TryGetValue("Limit", out var limitTxt) && TryDouble(limitTxt, out var s)) single = s;
        }

        private static StepStatusType GetMeasurementStatus(StepFieldValues step)
        {
            var statusText = GetField(step.Fields, "Status");
            return string.IsNullOrWhiteSpace(statusText) ? StepStatusType.Passed : MapStepStatus(statusText);
        }

        private static bool HasExplicitStatus(StepFieldValues step)
            => !string.IsNullOrWhiteSpace(GetField(step.Fields, "Status"));

        // Shared by single and multi string steps: the actual value, the expected value (preferring the
        // Limit column aligned with this measurement), and the operator (LOG becomes EQ when a limit exists).
        private static void ResolveStringComparison(StepFieldValues step, string compOp, out string actualValue, out CompOperatorType op, out string expected)
        {
            var f = step.Fields;

            actualValue = FirstField(f, "Result", "Value", "Measurement") ?? step.Name;
            if (!TryGetFieldAtIndex(step, "Limit", step.MeasurementColIndex, out expected)) expected = GetField(f, "Limit");

            op = MapCompOperator(compOp);
            if (op == CompOperatorType.LOG && !string.IsNullOrWhiteSpace(expected)) op = CompOperatorType.EQ;
        }

        private static void AddStringMultipleTest(StringValueStep str, StepFieldValues step, string compOp, string multiMeasurementName)
        {
            bool hasExplicitStatus = HasExplicitStatus(step);
            var measurementStatus = GetMeasurementStatus(step);
            ResolveStringComparison(step, compOp, out var actualValue, out var op, out var expected);
            var name = multiMeasurementName ?? "Value";

            if (op == CompOperatorType.LOG || string.IsNullOrWhiteSpace(expected))
                AddWithOptionalStatus(hasExplicitStatus,
                    () => str.AddMultipleTest(actualValue, name, measurementStatus),
                    () => str.AddMultipleTest(actualValue, name));
            else
                AddWithOptionalStatus(hasExplicitStatus,
                    () => str.AddMultipleTest(op, actualValue, expected, name, measurementStatus),
                    () => str.AddMultipleTest(op, actualValue, expected, name));

            str.Status = Worst(str.Status, measurementStatus);
        }

        private static GenericStepTypes InferGenericStepType(string stepName)
        {
            if (string.IsNullOrWhiteSpace(stepName)) return GenericStepTypes.Action;

            var name = stepName.Trim();

            // Many exports name a generic step exactly after its WATS type (e.g. "BatchSyncronization Step",
            // "IVIPowerSupply Step") - try an exact enum match first so every GenericStepTypes value gets its
            // own icon, before falling back to keyword inference for natural-language step names.
            var candidate = name.EndsWith(" Step", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - " Step".Length).TrimEnd()
                : name;
            if (Enum.TryParse<GenericStepTypes>(candidate, ignoreCase: true, out var exact)) return exact;

            var lower = name.ToLowerInvariant();
            var collapsed = Collapse(name);

            if (collapsed.StartsWith("elseif") || lower.StartsWith("else if", StringComparison.Ordinal)) return GenericStepTypes.ElseIf;
            if (collapsed.StartsWith("if")) return GenericStepTypes.If;
            if (collapsed.StartsWith("else")) return GenericStepTypes.Else;
            if (collapsed.StartsWith("endif") || lower.StartsWith("end ", StringComparison.Ordinal) || lower == "end") return GenericStepTypes.End;

            if (collapsed.StartsWith("foreach") || lower.Contains("for each")) return GenericStepTypes.ForEach;
            if (collapsed.StartsWith("for")) return GenericStepTypes.For;
            if (collapsed.StartsWith("break")) return GenericStepTypes.Break;
            if (collapsed.StartsWith("continue")) return GenericStepTypes.Continue;
            if (collapsed.Contains("dowhile") || lower.Contains("do while")) return GenericStepTypes.DoWhile;
            if (collapsed.StartsWith("while")) return GenericStepTypes.While;

            if (collapsed.StartsWith("select")) return GenericStepTypes.Select;
            if (collapsed.StartsWith("case")) return GenericStepTypes.Case;
            if (collapsed.StartsWith("switch")) return GenericStepTypes.Switch;

            if (collapsed.Contains("lock")) return GenericStepTypes.Lock;
            if (collapsed.Contains("rendezvous")) return GenericStepTypes.Rendezvous;
            if (collapsed.Contains("queue")) return GenericStepTypes.Queue;
            if (collapsed.Contains("notification") || collapsed.Contains("notify")) return GenericStepTypes.Notification;
            if (collapsed.Contains("wait")) return GenericStepTypes.Wait;

            if (collapsed.Contains("batchsyncronization") || collapsed.Contains("batchsynchronization")) return GenericStepTypes.BatchSyncronization;
            if (collapsed.Contains("useautoscheduledresource")) return GenericStepTypes.UseAutoScheduledResource;
            if (collapsed.Contains("autoschedule")) return GenericStepTypes.AutoSchedule;
            if (collapsed.Contains("threadpriority")) return GenericStepTypes.ThreadPriority;
            if (collapsed.Contains("semaphore")) return GenericStepTypes.Semaphore;
            if (collapsed.Contains("batchspecification")) return GenericStepTypes.BatchSpecification;
            if (collapsed.Contains("dataoperation")) return GenericStepTypes.DataOperation;

            if (lower.Contains("open database") || lower.Contains("open db")) return GenericStepTypes.OpenDatabase;
            if (lower.Contains("open sql")) return GenericStepTypes.OpenSQLStatement;
            if (lower.Contains("close sql")) return GenericStepTypes.CloseSQLStatement;
            if (lower.Contains("close database") || lower.Contains("close db")) return GenericStepTypes.CloseDatabase;

            if (collapsed.Contains("ividmm") || lower.Contains(" dmm")) return GenericStepTypes.IVIDmm;
            if (collapsed.Contains("iviscope") || lower.Contains(" scope")) return GenericStepTypes.IVIScope;
            if (collapsed.Contains("ivifgen") || lower.Contains(" fgen") || lower.Contains("function generator")) return GenericStepTypes.IVIFgen;
            if (collapsed.Contains("ivipowersupply") || lower.Contains("power supply") || lower.Contains(" psu")) return GenericStepTypes.IVIPowerSupply;
            if (collapsed.Contains("ivitools")) return GenericStepTypes.IVITools;
            if (collapsed.Contains("checkremotesystemstatus")) return GenericStepTypes.CheckRemoteSystemStatus;
            if (collapsed.Contains("runviasynchronously")) return GenericStepTypes.RunVIAsynchronously;

            if (collapsed.StartsWith("label") || lower.StartsWith("set label") || lower.Contains("create label")) return GenericStepTypes.Label;
            if (collapsed.StartsWith("goto")) return GenericStepTypes.Goto;
            if (lower.Contains("ftp")) return GenericStepTypes.FTPFiles;

            return GenericStepTypes.Action;
        }

        private static CompOperatorType ReduceToSingleBoundOperator(CompOperatorType op, bool lowPresent, bool highPresent)
        {
            switch (op)
            {
                case CompOperatorType.GE:
                case CompOperatorType.GT:
                case CompOperatorType.LE:
                case CompOperatorType.LT:
                case CompOperatorType.EQ:
                case CompOperatorType.NE:
                case CompOperatorType.LOG:
                    return op;

                case CompOperatorType.GELE:
                case CompOperatorType.GELT:
                case CompOperatorType.GTLE:
                case CompOperatorType.GTLT:
                    return lowPresent ? CompOperatorType.GE : (highPresent ? CompOperatorType.LE : CompOperatorType.LOG);

                case CompOperatorType.LEGE:
                case CompOperatorType.LEGT:
                case CompOperatorType.LTGE:
                case CompOperatorType.LTGT:
                    return highPresent ? CompOperatorType.LE : (lowPresent ? CompOperatorType.GE : CompOperatorType.LOG);

                default:
                    return CompOperatorType.LOG;
            }
        }

        // Exact (case-insensitive) key first, then the first key that contains it.
        private static string GetField(Dictionary<string, string> dict, string key)
        {
            if (dict.TryGetValue(key, out var v)) return v;

            foreach (var kv in dict)
                if (kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;

            return null;
        }

        private static string FirstField(Dictionary<string, string> dict, params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = GetField(dict, k);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static bool TryGetFieldAtIndex(StepFieldValues step, string key, int index, out string value)
        {
            value = null;
            if (index < 0) return false;

            foreach (var kv in step.FieldIndices)
            {
                if (kv.Value != index) continue;
                if (kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (step.Fields.TryGetValue(kv.Key, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    value = v;
                    return true;
                }
            }

            return false;
        }

        private static StepStatusType MapStepStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return StepStatusType.Passed;

            switch (s.Trim().ToLowerInvariant())
            {
                case "p":
                case "pass":
                case "passed":
                    return StepStatusType.Passed;
                case "f":
                case "fail":
                case "failed":
                    return StepStatusType.Failed;
                case "skip":
                case "skipped":
                    return StepStatusType.Skipped;
                case "done":
                    return StepStatusType.Done;
                case "error":
                    return StepStatusType.Error;
                case "terminated":
                    return StepStatusType.Terminated;
                default:
                    return StepStatusType.Passed;
            }
        }

        private static UUTStatusType MapUutStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return UUTStatusType.Passed;

            switch (s.Trim().ToLowerInvariant())
            {
                case "p":
                case "pass":
                case "passed":
                    return UUTStatusType.Passed;
                case "f":
                case "fail":
                case "failed":
                    return UUTStatusType.Failed;
                case "error":
                    return UUTStatusType.Error;
                case "terminated":
                    return UUTStatusType.Terminated;
                default:
                    return UUTStatusType.Passed;
            }
        }

        private static bool IsFailing(StepStatusType s)
            => s == StepStatusType.Failed || s == StepStatusType.Error;

        private static StepStatusType Worst(StepStatusType a, StepStatusType b)
        {
            int Rank(StepStatusType s)
            {
                switch (s)
                {
                    case StepStatusType.Error: return 5;
                    case StepStatusType.Failed: return 4;
                    case StepStatusType.Terminated: return 3;
                    case StepStatusType.Skipped: return 2;
                    case StepStatusType.Done: return 1;
                    case StepStatusType.Passed:
                    default:
                        return 0;
                }
            }

            return Rank(b) > Rank(a) ? b : a;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static bool TryDouble(string v, out double d)
            => double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d);

        // Tab-delimited exports are split verbatim. Other delimiters (comma, semicolon, ...) follow
        // RFC 4180 quoting, so values like "Oslo, Norway" stay in one cell.
        private static string[] Split(string line, char d)
        {
            if (d == '\t') return line.Split(new[] { d }, StringSplitOptions.None);
            var cells = SplitQuoted(line, d, out _);
            if (d == ';')
                for (int i = 0; i < cells.Length; i++) cells[i] = NormalizeDecimalComma(cells[i]);
            return cells;
        }

        // Semicolon-delimited files come from decimal-comma locales: "3,14" / "1.234,5" -> "3.14" / "1234.5".
        // Only cells that are entirely such a number are touched.
        private static readonly Regex DecimalCommaNumber =
            new Regex(@"^\s*[-+]?(\d{1,3}(\.\d{3})+|\d+),\d+([eE][-+]?\d+)?\s*$", RegexOptions.Compiled);

        private static string NormalizeDecimalComma(string cell)
            => DecimalCommaNumber.IsMatch(cell) ? cell.Replace(".", string.Empty).Replace(',', '.') : cell;

        // A quote opens a quoted section only at the start of a cell; inside it, "" is a literal quote.
        private static string[] SplitQuoted(string line, char d, out bool unterminated)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool inQuotes = false, atCellStart = true;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch != '"') cell.Append(ch);
                    else if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else if (ch == d)
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                    atCellStart = true;
                    continue;
                }
                else if (ch == '"' && atCellStart) inQuotes = true;
                else cell.Append(ch);
                atCellStart = false;
            }

            cells.Add(cell.ToString());
            unterminated = inQuotes;
            return cells.ToArray();
        }

        // Next physical line, extended with following lines while a quoted cell spans a line break.
        private static string ReadRecord(StreamReader sr, char d)
            => ContinueQuotedRecord(sr, sr.ReadLine(), d);

        private static string ContinueQuotedRecord(StreamReader sr, string line, char d)
        {
            if (line == null || d == '\t') return line;
            while (true)
            {
                SplitQuoted(line, d, out var open);
                string next;
                if (!open || (next = sr.ReadLine()) == null) return line;
                line += "\n" + next;
            }
        }

        private static string Get(string[] arr, int i)
            => (i < 0 || i >= arr.Length) ? null : (string.IsNullOrWhiteSpace(arr[i]) ? null : arr[i].Trim());

        // Last "/"-separated segment of a path (the whole string when there is no "/").
        private static string LastSegment(string path)
            => path.Substring(path.LastIndexOf('/') + 1);

        // Lower-cased letters and digits only, for fuzzy step-name matching.
        private static string Collapse(string s)
            => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static void ReadHeader(StreamReader sr, out char delimiter, out string[] headers, out int firstGroupCol, out MetaColumns metaMap)
        {
            var headerLine = ReadNextNonEmpty(sr, 10);
            if (headerLine == null) throw new InvalidDataException("Missing header line.");

            delimiter = AutoDetectDelimiter(headerLine);
            headerLine = ContinueQuotedRecord(sr, headerLine, delimiter);
            headers = Split(headerLine, delimiter);

            firstGroupCol = Array.FindIndex(headers, h => h != null && h.TrimStart().StartsWith("|<-", StringComparison.Ordinal));
            if (firstGroupCol < 0) firstGroupCol = FindFirstStepColumn(headers);

            metaMap = DetectMetaColumnIndices(headers, firstGroupCol);
        }

        // Exports without "|<-" group markers: steps start at the first "<Step> (<known field>)" column, or at
        // the bare value column right before it when that column carries the same step's name.
        private static int FindFirstStepColumn(string[] headers)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i]?.Trim() ?? string.Empty;
                int lp = h.LastIndexOf('(');
                if (lp <= 0 || !h.EndsWith(")", StringComparison.Ordinal)) continue;
                if (!KnownStepFieldNames.Contains(h.Substring(lp + 1, h.Length - lp - 2).Trim())) continue;

                var stepName = h.Substring(0, lp).Trim();
                return i > 0 && string.Equals(headers[i - 1]?.Trim(), stepName, StringComparison.OrdinalIgnoreCase) ? i - 1 : i;
            }
            return headers.Length;
        }

        private static string ReadNextNonEmpty(StreamReader sr, int max)
        {
            string line;
            int c = 0;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) return line;
                if (++c >= max) break;
            }
            return null;
        }

        private static char AutoDetectDelimiter(string line)
        {
            // The "|<-" / "->|" group markers are part of the header names, not separators. Counting them would
            // make '|' win whenever step groups are a single column ("|<- Step (Status) ->|" = 2 pipes, 1 separator).
            line = line.Replace("|<-", string.Empty).Replace("->|", string.Empty);

            var cand = new[] { '\t', ',', ';', '|' };
            int best = -1;
            char bestD = '\t';

            foreach (var d in cand)
            {
                int c = line.Count(ch => ch == d);
                if (c > best)
                {
                    best = c;
                    bestD = d;
                }
            }

            return bestD;
        }

        private static string GetUnits(Dictionary<string, string> f)
        {
            var kv = f.FirstOrDefault(k => k.Key.IndexOf("Unit", StringComparison.OrdinalIgnoreCase) >= 0);
            return string.IsNullOrEmpty(kv.Key) ? string.Empty : (kv.Value ?? string.Empty);
        }

        private static double ParseMeasuredValue(Dictionary<string, string> f, double low, double high, double limit)
        {
            double measured = double.NaN;
            int numericCount = 0;

            foreach (var kv in f)
            {
                if (!TryDouble(kv.Value, out var d)) continue;

                numericCount++;
                var k = kv.Key;
                bool isLimitKey = k.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0
                                  || k.IndexOf("low", StringComparison.OrdinalIgnoreCase) >= 0
                                  || k.IndexOf("high", StringComparison.OrdinalIgnoreCase) >= 0
                                  || k.IndexOf("unit", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isLimitKey)
                {
                    measured = d;
                    break;
                }
            }

            if (double.IsNaN(measured) && numericCount == 1)
                measured = !double.IsNaN(limit) ? limit : (!double.IsNaN(low) ? low : high);

            return measured;
        }

        private static CompOperatorType MapCompOperator(string op)
        {
            if (string.IsNullOrWhiteSpace(op)) return CompOperatorType.LOG;

            switch (op.Trim().ToUpperInvariant())
            {
                case "EQ": return CompOperatorType.EQ;
                case "NE": return CompOperatorType.NE;
                case "GT": return CompOperatorType.GT;
                case "LT": return CompOperatorType.LT;
                case "GE": return CompOperatorType.GE;
                case "LE": return CompOperatorType.LE;
                case "GELE": return CompOperatorType.GELE;
                case "GELT": return CompOperatorType.GELT;
                case "GTLE": return CompOperatorType.GTLE;
                case "GTLT": return CompOperatorType.GTLT;
                case "LEGE": return CompOperatorType.LEGE;
                case "LEGT": return CompOperatorType.LEGT;
                case "LTGE": return CompOperatorType.LTGE;
                case "LTGT": return CompOperatorType.LTGT;
                // String comparison operators
                case "IGNORECASE": return CompOperatorType.IGNORECASE;
                case "CASESENSIT":
                case "CASESENSITIVE": return CompOperatorType.EQ; // Case-sensitive equals
                default: return CompOperatorType.LOG;
            }
        }

        private static DateTime? ParseDate(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;

            foreach (var f in DateFormats)
                if (DateTime.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                    return dt;

            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var gen)) return gen;
            return null;
        }

        private static void AddMiscIfValue(UUTReport uut, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) uut.AddMiscUUTInfo(key, value);
        }

        private static int ExtractTrailingInt(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            int end = text.Length - 1;
            while (end >= 0 && char.IsWhiteSpace(text[end])) end--;

            int start = end;
            while (start >= 0 && char.IsDigit(text[start])) start--;

            if (start == end) return -1;

            return int.TryParse(text.Substring(start + 1, end - start), out var n) ? n : -1;
        }

        private static string SanitizeKey(string key)
        {
            var sb = new StringBuilder(key.Length);
            foreach (var ch in key)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch)) sb.Append('_');
            }
            return sb.ToString().Trim('_');
        }

        // CallExeStep and MessagePopupStep have no exposed comparison/measurement fields in this CSV
        // format (just Status/Step time/... plus, for MessagePopup, Response) - indistinguishable from a
        // plain generic/passfail step by field shape alone, so only the step's own name gives it away.
        private static StepKind DetermineKind(StepFieldValues step)
        {
            if (!string.IsNullOrWhiteSpace(step.Name))
            {
                var collapsed = Collapse(step.Name);
                if (collapsed.Contains("callexe")) return StepKind.CallExe;
                if (collapsed.Contains("messagepopup")) return StepKind.MessagePopup;
            }
            return step.ForceAction ? StepKind.Action : DetermineStepKind(step.Fields, step.NoCompOperatorColumn);
        }

        private static StepKind DetermineStepKind(Dictionary<string, string> fields, bool noCompOperatorColumn)
        {
            string measurement = GetField(fields, "Measurement");
            string resultOrValue = FirstField(fields, "Result", "Value");
            string comp = GetField(fields, "Comp operator");

            bool hasAnyLimitKey = fields.Keys.Any(k => k.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0);

            bool anyNumericLimit = false;
            if (hasAnyLimitKey)
            {
                foreach (var kv in fields)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    var key = kv.Key;
                    if (key.IndexOf("limit", StringComparison.OrdinalIgnoreCase) < 0
                        && key.IndexOf("low", StringComparison.OrdinalIgnoreCase) < 0
                        && key.IndexOf("high", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (TryDouble(kv.Value, out _))
                    {
                        anyNumericLimit = true;
                        break;
                    }
                }
            }

            bool measurementIsBool = false;
            bool measurementIsNumeric = false;
            if (!string.IsNullOrWhiteSpace(measurement))
            {
                var mv = measurement.Trim();
                measurementIsBool = mv.Equals("true", StringComparison.OrdinalIgnoreCase)
                                   || mv.Equals("false", StringComparison.OrdinalIgnoreCase)
                                   || mv.Equals("t", StringComparison.OrdinalIgnoreCase)
                                   || mv.Equals("f", StringComparison.OrdinalIgnoreCase)
                                   || mv == "0" || mv == "1";
                measurementIsNumeric = TryDouble(measurement, out _);
            }

            if (anyNumericLimit || (measurementIsNumeric && (hasAnyLimitKey || !string.IsNullOrWhiteSpace(comp))))
                return StepKind.Numeric;

            if (measurementIsBool)
                return StepKind.PassFail;

            if ((hasAnyLimitKey || !string.IsNullOrWhiteSpace(comp)) && (!string.IsNullOrWhiteSpace(measurement) || !string.IsNullOrWhiteSpace(resultOrValue)))
                return StepKind.StringValue;

            // Without a "(Comp operator)" column a logged value with no limits looks like an action step;
            // keep it as a LOG measurement so the value isn't lost.
            if (noCompOperatorColumn && !string.IsNullOrWhiteSpace(measurement))
                return measurementIsNumeric ? StepKind.Numeric : StepKind.StringValue;

            bool hasStatus = !string.IsNullOrWhiteSpace(GetField(fields, "Status"));
            bool hasOther = fields.Any(kv => !string.IsNullOrWhiteSpace(kv.Value)
                                            && !string.Equals(kv.Key, "Status", StringComparison.OrdinalIgnoreCase)
                                            && !SkipIgnoredKeys.Contains(kv.Key));
            if (hasStatus && !hasOther) return StepKind.PassFail;

            return StepKind.Action;
        }

        private static MetaColumns DetectMetaColumnIndices(string[] headers, int stop)
        {
            var meta = new MetaColumns();
            for (int i = 0; i < Math.Min(stop, headers.Length); i++)
            {
                var h = headers[i];
                if (string.IsNullOrWhiteSpace(h)) continue;

                var hl = h.ToLowerInvariant();
                // "Subunit Rev 1" etc. belong to PopulateSubUnits - never let them stand in for the UUT's own fields
                if (hl.StartsWith("subunit", StringComparison.Ordinal)) continue;
                if (meta.Serial < 0 && hl.Contains("serial") && !hl.Contains("batch")) meta.Serial = i;
                else if (meta.PartNumber < 0 && hl.Contains("part") && hl.Contains("number")) meta.PartNumber = i;
                else if (meta.Revision < 0 && hl.Contains("rev")) meta.Revision = i;
                else if (meta.Operation < 0 && hl.Contains("operation")) meta.Operation = i;
                else if (meta.Station < 0 && hl.Contains("station")) meta.Station = i;
                else if (meta.Location < 0 && hl == "location") meta.Location = i;
                else if (meta.Purpose < 0 && hl.Contains("purpose")) meta.Purpose = i;
                else if (meta.Status < 0 && hl == "status") meta.Status = i;
                else if (meta.LocalDateTime < 0 && hl.Contains("date/time") && !hl.Contains("utc")) meta.LocalDateTime = i;
                else if (meta.UtcDateTime < 0 && hl.Contains("utc") && hl.Contains("date/time")) meta.UtcDateTime = i;
                else if (meta.SwFilename < 0 && hl.Contains("sw filename")) meta.SwFilename = i;
                else if (meta.SwVersion < 0 && hl.Contains("sw fileversion")) meta.SwVersion = i;
                else if (meta.ExecutionTime < 0 && hl.Contains("execution") && hl.Contains("time") && !hl.Contains("format")) meta.ExecutionTime = i;
                else if (meta.BatchSerial < 0 && hl.Contains("batch") && hl.Contains("serial")) meta.BatchSerial = i;
                else if (meta.SocketIndex < 0 && (hl.Contains("socket") || hl.Contains("site")) && !hl.Contains("format")) meta.SocketIndex = i;
                else if (meta.Operator < 0 && hl.Contains("operator")) meta.Operator = i;
                else if (meta.FixtureId < 0 && hl.Contains("fixture")) meta.FixtureId = i;
                else if (meta.Comment < 0 && hl == "comment") meta.Comment = i;
                else if (meta.ErrorCode < 0 && hl.Contains("error code") && !hl.Contains("format")) meta.ErrorCode = i;
                else if (meta.ErrorMessage < 0 && hl.Contains("error message")) meta.ErrorMessage = i;
                else if (meta.Run < 0 && hl == "run") meta.Run = i;
            }
            return meta;
        }

        // Bare/marker columns always start a brand new occurrence - this is what correctly keeps repeated
        // names in separate loop iterations from merging. But a step's own "(Field)" columns are not
        // always contiguous: some exports interleave a step's closing fields (Status/Step time/...) with
        // OTHER sibling multi-measurements in between (e.g. "Foo (Status)" appearing after "Bar", "Baz"
        // have already been fully written). So "(Field)" columns must attach to the most recent occurrence
        // of that exact name - not just whichever step happened to be parsed last.
        private static List<StepFieldValues> ParseSegmentOrdered(string[] headers, string[] cells, List<int> segmentColumns)
        {
            bool hasGroupMarkers = segmentColumns.Any(sc => headers[sc] != null && headers[sc].TrimStart().StartsWith("|<-", StringComparison.Ordinal));
            var result = new List<StepFieldValues>();
            var latestByName = new Dictionary<string, StepFieldValues>(StringComparer.OrdinalIgnoreCase);

            foreach (int sc in segmentColumns)
            {
                var raw = headers[sc];
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var trimmed = raw.Trim();
                var withoutMarker = trimmed.StartsWith("|<-", StringComparison.Ordinal)
                    ? trimmed.TrimStart('|', '<', '-').Trim()
                    : trimmed;

                // The LAST column of a step's span also carries a trailing "->|" - strip it before
                // checking for a "(Field)" suffix, otherwise the closing marker hides the ")" and the
                // step's final field (often "Report text") gets misread as a brand new bare step.
                var withoutClosing = withoutMarker.EndsWith("->|", StringComparison.Ordinal)
                    ? withoutMarker.Substring(0, withoutMarker.Length - 3).TrimEnd()
                    : withoutMarker;

                // A column only carries a "(Field)" suffix if its trailing parenthetical is one of the
                // known field names - not just "any parens present". Step/marker names can themselves
                // contain parens (e.g. "Foo (Loop index: 0)"), which must stay part of the name, not be
                // misread as a field suffix (that would silently drop the step's own measurement value).
                bool isBaseCol = true;
                if (withoutClosing.EndsWith(")", StringComparison.Ordinal))
                {
                    int lp = withoutClosing.LastIndexOf('(');
                    if (lp >= 0)
                    {
                        var candidateField = withoutClosing.Substring(lp + 1, withoutClosing.Length - lp - 2).Trim();
                        if (KnownStepFieldNames.Contains(candidateField)) isBaseCol = false;
                    }
                }

                if (isBaseCol)
                {
                    if (string.IsNullOrEmpty(withoutMarker)) continue;

                    var fresh = new StepFieldValues { Name = withoutMarker };
                    result.Add(fresh);
                    latestByName[withoutMarker] = fresh;

                    var val = Get(cells, sc);
                    if (!string.IsNullOrWhiteSpace(val)) fresh.Fields["Measurement"] = val;
                    fresh.MeasurementColIndex = sc;
                    continue;
                }

                ParseStepHeader(raw, out var stepName, out var fieldName);
                if (string.IsNullOrWhiteSpace(stepName) || string.IsNullOrWhiteSpace(fieldName)) continue;

                // A "|<-" marker always opens a new step group, even when the group has no bare value column
                // (e.g. "|<- Log step information (Status)"): a step name reused in several sequences must
                // become one step per group, not one step whose fields each later group overwrites.
                // Without markers, a field column opens a group when no step of that name is open yet, or
                // when the latest one already has this field (a second group of the same name).
                bool opensGroup = hasGroupMarkers
                    ? raw.TrimStart().StartsWith("|<-", StringComparison.Ordinal)
                    : !latestByName.TryGetValue(stepName, out var latest) || latest.FieldIndices.ContainsKey(fieldName);
                if (opensGroup || !latestByName.TryGetValue(stepName, out var target))
                {
                    target = new StepFieldValues { Name = stepName };
                    result.Add(target);
                    latestByName[stepName] = target;
                }

                var value = Get(cells, sc);
                if (!string.IsNullOrWhiteSpace(value)) target.Fields[fieldName] = value;
                target.FieldIndices[fieldName] = sc;

                if (fieldName.Equals("Measurement", StringComparison.OrdinalIgnoreCase)) target.MeasurementColIndex = sc;
                if (fieldName.Equals("File name", StringComparison.OrdinalIgnoreCase)) target.IsSequence = true;
                if (opensGroup && fieldName.Equals("Status", StringComparison.OrdinalIgnoreCase)) target.ForceAction = true;
            }

            // The Export Wizard opens a group without a value column with "|<- Step (Status)" - an action step.
            // Without markers that is only visible while Status stays the group's first column, so recognise
            // it by the missing value column instead, whatever the field order.
            if (!hasGroupMarkers)
                foreach (var step in result)
                    if (step.MeasurementColIndex < 0 && !step.IsSequence && step.FieldIndices.ContainsKey("Status")) step.ForceAction = true;

            return result;
        }

        /// <summary>
        /// Extracts the loop index from step names like "StepName (Loop index: 0)".
        /// Returns the index as a string, or null if not a loop step.
        /// </summary>
        private static string ExtractLoopIndex(string stepName)
        {
            if (string.IsNullOrWhiteSpace(stepName)) return null;

            int tokenStart = stepName.IndexOf(LoopIndexToken, StringComparison.OrdinalIgnoreCase);
            if (tokenStart < 0) return null;

            int valueStart = tokenStart + LoopIndexToken.Length;
            int closeParen = stepName.IndexOf(')', valueStart);
            if (closeParen < 0) closeParen = stepName.Length;

            var indexStr = stepName.Substring(valueStart, closeParen - valueStart).Trim();
            return string.IsNullOrEmpty(indexStr) ? "0" : indexStr;
        }

        // WATS never disambiguates "Step path" between loop iterations - a looped container step and
        // all of its descendants report the exact same path on every iteration; only the container's own
        // group name gains a "(Loop index: N)" suffix. Without correction, every iteration would resolve
        // to the same parent/step and collide. This walks steps in header order (which mirrors the
        // report's depth-first structure) and splices a "[Loop N]" marker into the path at the container
        // level, so each iteration's subtree gets its own distinct path - descendants inherit whichever
        // iteration's marker was seen most recently for their container, until a later one replaces it.
        private static void ApplyLoopPathDisambiguation(List<StepFieldValues> steps)
        {
            var activeLoopSuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in steps)
            {
                var rawPath = GetField(step.Fields, "Step path");
                if (string.IsNullOrWhiteSpace(rawPath)) continue;

                int sepIdx = rawPath.IndexOf("//", StringComparison.Ordinal);
                string containerPath = sepIdx >= 0 ? rawPath.Substring(0, sepIdx) : rawPath;
                string remainder = sepIdx >= 0 ? rawPath.Substring(sepIdx) : string.Empty;
                string unmodifiedContainerPath = containerPath;

                string bestKey = null;
                foreach (var key in activeLoopSuffix.Keys)
                {
                    bool isSelfOrDescendant = containerPath.Equals(key, StringComparison.OrdinalIgnoreCase)
                        || containerPath.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase);
                    if (isSelfOrDescendant && (bestKey == null || key.Length > bestKey.Length)) bestKey = key;
                }
                if (bestKey != null)
                    containerPath = bestKey + activeLoopSuffix[bestKey] + containerPath.Substring(bestKey.Length);

                step.LoopBaseKey = containerPath;

                var loopIndex = ExtractLoopIndex(step.Name);
                if (loopIndex != null)
                {
                    var suffix = " [Loop " + loopIndex + "]";
                    activeLoopSuffix[unmodifiedContainerPath] = suffix;
                    containerPath = unmodifiedContainerPath + suffix;
                    // Re-anchor to this step's own (pre-registration) base - the ancestor-match above can
                    // wrongly latch onto a PRIOR SIBLING iteration's suffix, since same-path-different-index
                    // siblings look identical to a nested descendant to that check.
                    step.LoopBaseKey = unmodifiedContainerPath;
                    step.LoopIterationIndex = int.TryParse(loopIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idxVal) ? (int?)idxVal : null;
                }

                step.LoopIterationKey = containerPath;
                step.Fields["Step path"] = containerPath + remainder;
            }
        }

        private static string StripLoopIndexSuffix(string stepName)
        {
            if (string.IsNullOrWhiteSpace(stepName)) return stepName;
            int tokenStart = stepName.IndexOf(LoopIndexToken, StringComparison.OrdinalIgnoreCase);
            return tokenStart < 0 ? stepName : stepName.Substring(0, tokenStart).TrimEnd();
        }

        // Aggregates loop members (grouped by LoopBaseKey) into the counts StartLoop requires, and picks
        // one dominant step kind per loop so every iteration is added with the same declared type - even
        // when the loop's own unindexed summary entry is too sparse (e.g. status-only) to infer a kind from.
        private static Dictionary<string, LoopInfo> ComputeLoopInfo(List<StepFieldValues> steps)
        {
            var iterations = new Dictionary<string, (string baseKey, int? index, StepStatusType status)>(StringComparer.OrdinalIgnoreCase);
            var kindVotes = new Dictionary<string, StepKind>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in steps)
            {
                if (step.LoopIterationKey == null) continue;

                var status = MapStepStatus(GetField(step.Fields, "Status"));
                if (!iterations.TryGetValue(step.LoopIterationKey, out var existing))
                    iterations[step.LoopIterationKey] = (step.LoopBaseKey, step.LoopIterationIndex, status);
                else if (IsFailing(status))
                    iterations[step.LoopIterationKey] = (existing.baseKey, existing.index, status);

                // Sequences don't have a measurement "kind" (Numeric/StringValue/PassFail) - they're always
                // started with T=SequenceCall regardless, so they must not vote on this loop's leaf kind.
                if (step.LoopBaseKey != null && !step.IsSequence)
                {
                    var kind = DetermineKind(step);
                    if (!kindVotes.TryGetValue(step.LoopBaseKey, out var existingKind) || existingKind == StepKind.Action)
                        kindVotes[step.LoopBaseKey] = kind;
                }
            }

            var result = new Dictionary<string, LoopInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in iterations.Values.GroupBy(v => v.baseKey, StringComparer.OrdinalIgnoreCase))
            {
                var indexed = group.Where(v => v.index.HasValue).ToList();
                if (indexed.Count == 0) continue; // no "(Loop index: N)" sibling found - not a real loop

                result[group.Key] = new LoopInfo
                {
                    Iterations = indexed.Count,
                    Passed = indexed.Count(v => !IsFailing(v.status)),
                    Failed = indexed.Count(v => IsFailing(v.status)),
                    EndingIndex = indexed.Max(v => v.index.Value),
                    Kind = kindVotes.TryGetValue(group.Key, out var k) ? k : StepKind.Numeric
                };
            }
            return result;
        }

        private void EnsureLoopStateForParent(SequenceCall parent, string activeLoopBaseKeyOrNull)
        {
            if (_openLoopByParent.TryGetValue(parent, out var openKey))
            {
                if (string.Equals(openKey, activeLoopBaseKeyOrNull, StringComparison.OrdinalIgnoreCase)) return;
                parent.StopLoop();
                _openLoopByParent.Remove(parent);
            }
        }

        // Returns the loop's summary step, starting the loop on first use (one StartLoop per LoopBaseKey)
        // and recording it as the parent's open loop so EnsureLoopStateForParent/BuildAllSteps can stop it.
        private Step GetOrStartLoop(SequenceCall parent, StepFieldValues step, Func<Step> startLoop)
        {
            if (!_loopSummaries.TryGetValue(step.LoopBaseKey, out var summary))
            {
                summary = startLoop();
                _loopSummaries[step.LoopBaseKey] = summary;
                _openLoopByParent[parent] = step.LoopBaseKey;
            }
            return summary;
        }

        private static T StartLoop<T>(SequenceCall parent, string baseName, LoopInfo info) where T : Step
            => parent.StartLoop<T>(baseName, (short)info.Iterations, (short)info.Passed, (short)info.Failed, (short)info.EndingIndex);

        private static Step StartLoopForKind(SequenceCall parent, StepKind kind, string baseName, LoopInfo info)
        {
            switch (kind)
            {
                case StepKind.Numeric: return StartLoop<NumericLimitStep>(parent, baseName, info);
                case StepKind.StringValue: return StartLoop<StringValueStep>(parent, baseName, info);
                case StepKind.PassFail:
                case StepKind.Action:
                default: return StartLoop<PassFailStep>(parent, baseName, info);
            }
        }

        private static Step AddIndexedLeaf(SequenceCall parent, StepKind kind, string baseName, short index)
        {
            switch (kind)
            {
                case StepKind.Numeric: return parent.AddNumericLimitStep(baseName, index);
                case StepKind.StringValue: return parent.AddStringValueStep(baseName, index);
                case StepKind.PassFail:
                case StepKind.Action:
                default: return parent.AddPassFailStep(baseName, index);
            }
        }

        private static Step CreatePlainLeaf(SequenceCall parent, StepKind kind, string baseName)
        {
            switch (kind)
            {
                case StepKind.Numeric: return parent.AddNumericLimitStep(baseName);
                case StepKind.StringValue: return parent.AddStringValueStep(baseName);
                case StepKind.PassFail:
                case StepKind.Action:
                default: return parent.AddPassFailStep(baseName);
            }
        }

        // Creates (or reuses) the leaf step object for one measurement group, using the real WATS loop
        // API (StartLoop/indexed Add*) when this step belongs to a detected loop, so the report shows a
        // native loop summary instead of plain steps that merely look loop-like by name.
        private Step GetOrCreateLeafStep(SequenceCall parent, StepKind kind, string baseName, StepFieldValues step, LoopInfo loopInfoOrNull)
        {
            if (loopInfoOrNull == null) return CreatePlainLeaf(parent, kind, baseName);

            var summary = GetOrStartLoop(parent, step, () => StartLoopForKind(parent, loopInfoOrNull.Kind, baseName, loopInfoOrNull));
            if (step.LoopIterationIndex == null) return summary;

            return AddIndexedLeaf(parent, loopInfoOrNull.Kind, baseName, (short)step.LoopIterationIndex.Value);
        }

        private static bool IsRootOrMainSequenceCallback(StepFieldValues step, SequenceCall root)
            => !string.IsNullOrWhiteSpace(step.Name)
               && (string.Equals(step.Name, root.Name, StringComparison.OrdinalIgnoreCase)
                   || step.Name.IndexOf("MainSequence Callback", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
