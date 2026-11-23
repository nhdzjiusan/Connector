using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;
using System.Globalization;
using System.Text.Json;

namespace PeripheralPower;

internal static class HidRuleLoader
{
    internal const int MaxReportLength = 4096;
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxFiles = 64;
    private const int MaxRules = 64;

    internal static HidRuleDocument LoadEmbedded()
    {
        using var stream = typeof(HidRuleLoader).Assembly.GetManifestResourceStream("PeripheralPower.HidRules.Defaults")
            ?? throw new InvalidDataException("Missing embedded HID battery rules.");
        var document = JsonSerializer.Deserialize(stream, HidRuleJsonContext.Strict.HidRuleDocument)
            ?? throw new InvalidDataException("Missing default HID rule document.");
        ValidateDocument(document, embedded: true);
        return document with
        {
            Rules = document.Rules.Select(rule => rule.Id == UsbBattery.HeadsetRuleId
                ? rule with { AllowUndescribedOutput = true } : rule).ToArray(),
            BuiltinIds = document.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    internal static HidRule[] LoadExternalFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[MaxFileBytes + 1];
        int length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        Require(length <= MaxFileBytes, "Rule file is larger than 64 KiB.");
        var data = bytes.AsMemory(0, length);
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) data = data[3..];
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        Require(root.ValueKind == JsonValueKind.Object, "Expected a rule object or a document with a rules array.");
        ValidateNumericKeys(root);
        var document = root.TryGetProperty("rules", out _)
            ? root.Deserialize(HidRuleJsonContext.Strict.HidRuleDocument)
            : new HidRuleDocument([root.Deserialize(HidRuleJsonContext.Strict.HidRule)!]);
        if (document is null) throw new InvalidDataException("Empty HID rule document.");
        ValidateDocument(document, embedded: false);
        return document.Rules;
    }

    internal static HidRuleDocument LoadDirectory(string directory, HidRuleDocument defaults, Action<string, Exception> error)
    {
        var active = defaults.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var shipped = new Dictionary<string, HidRule>(active, StringComparer.OrdinalIgnoreCase);
        var shippedGroups = shipped.Values.Select(r => r.Match.DeviceGroup).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory)) return defaults;
            var paths = Directory.EnumerateFiles(directory, "*.json").Take(MaxFiles + 1).ToArray();
            Require(paths.Length <= MaxFiles, "The HID rule directory contains more than 64 JSON files.");
            foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var rules = LoadExternalFile(path);
                    Require(active.Count + rules.Count(r => !active.ContainsKey(r.Id)) <= MaxRules,
                        "The active HID rule catalog would exceed 64 entries.");
                    var validated = rules.Select(rule =>
                    {
                        if (shipped.TryGetValue(rule.Id, out var original)) return ValidateOverride(rule, original);
                        Require(!shippedGroups.Contains(rule.Match.DeviceGroup), $"{rule.Id}: deviceGroup is reserved by an embedded rule.");
                        return rule;
                    }).ToArray();
                    foreach (var rule in validated) active[rule.Id] = rule;
                }
                catch (Exception ex) when (IsRuleError(ex)) { error(path, ex); }
            }
        }
        catch (Exception ex) when (IsRuleError(ex)) { error(directory, ex); }
        return defaults with { Rules = active.Values.ToArray() };
    }

    internal static bool IsRuleError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or InvalidDataException;

    internal static bool ExternalIdentityAllowed(int vid, IEnumerable<int> pids, int page) =>
        // Known HID bootloaders can expose private collections. Reject their USB identities
        // even when a rule specifies a seemingly valid vendor Usage Page.
        page is >= 0xFF01 and <= 0xFFFF && !pids.Any(pid => IsBlockedDevice(vid, pid));

    internal static bool IsBlockedDevice(int vid, int pid) =>
        (vid, pid) is (0x16C0, 0x0478) or (0x16C0, 0x05DF) or (0x03EB, 0x2067); // HalfKay, BootloadHID, QMK HID

    private static HidRule ValidateOverride(HidRule rule, HidRule original)
    {
        var match = rule.Match;
        var shipped = original.Match;
        Require(rule.Kind == original.Kind && match.Pids.Order().SequenceEqual(shipped.Pids.Order()) &&
            (match with { Pids = shipped.Pids }) == shipped, $"{rule.Id}: overriding an embedded rule must preserve its match and kind.");
        // The shipped G9 command remains eligible for the native compatibility exception,
        // even when an override changes the parser. No different command inherits it.
        bool stockOutput = original.AllowUndescribedOutput && rule.Query?.Mode == original.Query?.Mode &&
            HidRuleEngine.BuildQuery(rule.Query!).SequenceEqual(HidRuleEngine.BuildQuery(original.Query!));
        return rule with { Id = original.Id, AllowUndescribedOutput = stockOutput, ShippedFallback = original };
    }

    private static void ValidateDocument(HidRuleDocument document, bool embedded)
    {
        Require(document.SchemaVersion == 1, "Unsupported HID rule schema version.");
        var steps = document.DriverSteps;
        Require(steps is null || embedded, "Only built-in rules can contain driverSteps.");
        foreach (var (name, phases) in steps ?? [])
        {
            Require(HidBuiltinDrivers.ValidPhases(name, phases), "Unknown built-in driver or invalid phases.");
            foreach (var phase in phases!) ValidateTransaction(phase, requireParse: false);
        }
        Require(document.Rules is { Length: <= MaxRules }, "Expected at most 64 rules.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in document.Rules!)
        {
            Require(rule is not null && ValidId(rule.Id) && ids.Add(rule.Id), "Missing, invalid or duplicate rule id.");
            try { ValidateRule(rule!, embedded, steps); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{rule!.Id}: {ex.Message}", ex); }
        }
    }

    private static void ValidateRule(HidRule rule, bool embedded, Dictionary<string, HidTransaction[]>? steps)
    {
        Require(!string.IsNullOrWhiteSpace(rule.Name) && rule.Name.Length <= 100, "Expected a device name of up to 100 characters.");
        var match = rule.Match;
        Require(match is not null && match.Vid is >= 1 and <= 65535 &&
            match.Pids is { Length: >= 1 and <= 32 } && match.Pids.All(pid => pid is >= 1 and <= 65535) &&
            match.Pids.Distinct().Count() == match.Pids.Length, "Expected an exact VID and distinct nonzero PIDs.");
        bool scoped = match!.Usage is >= 1 and <= 65535 && match.UsagePage is >= 0xFF00 and <= 0xFFFF &&
            (embedded || ExternalIdentityAllowed(match.Vid, match.Pids, match.UsagePage.Value));
        bool passive = match.Usage is null && match.UsagePage is null &&
            rule.Query?.Mode == HidQueryMode.PassiveListen && match.Interface?.InputReportId is not null &&
            !match.Pids.Any(pid => IsBlockedDevice(match.Vid, pid));
        Require(scoped || passive,
            "Expected an exact permitted vendor Usage Page and Usage.");
        if (match.Interface is { } selector)
            Require((selector.InputReportId is not null || selector.OutputReportId is not null || selector.FeatureReportId is not null) &&
                ValidReportId(selector.InputReportId) && ValidReportId(selector.OutputReportId) && ValidReportId(selector.FeatureReportId),
                "Invalid interface Report ID selector.");
        Require(ValidId(match.DeviceGroup) && match.Priority is >= -1000 and <= 1000, "Invalid deviceGroup or priority.");
        Require(ValidReportLength(match.MinInput) && ValidReportLength(match.MinOutput) &&
            ValidReportLength(match.MinFeature), "Invalid minimum report length.");

        if (rule.BuiltinDriver is { } driver)
        {
            Require(embedded && HidBuiltinDrivers.IsRegistered(driver) && steps != null && steps.ContainsKey(driver) &&
                rule.Query is null && rule.Response is null && rule.Parse is null,
                "builtinDriver is reserved for an embedded rule and is exclusive with query, response and parse.");
            return;
        }
        ValidateTransaction(rule.Transaction, requireParse: true);
        if (match.Interface is { } descriptor && rule.Query is { } query)
        {
            if (query.Mode == HidQueryMode.OutputThenInput && descriptor.OutputReportId is int output)
                Require(query.Bytes![0] == output, "Query Report ID does not match interface.outputReportId.");
            if (query.Mode is HidQueryMode.FeatureSetGet or HidQueryMode.FeatureGetOnly && descriptor.FeatureReportId is int feature)
                Require((query.ReportId ?? query.Bytes![0]) == feature, "Query Report ID does not match interface.featureReportId.");
        }
    }

    private static void ValidateTransaction(HidTransaction? transaction, bool requireParse)
    {
        Require(transaction is not null && transaction.Query is not null && transaction.Response is not null,
            "Expected query and response.");
        var query = transaction!.Query;
        switch (query.Mode)
        {
            case HidQueryMode.OutputThenInput:
            case HidQueryMode.FeatureSetGet:
                Require(query.ReportId is null && query.Length >= 1 && ValidReportLength(query.Length) &&
                    query.Bytes is not null && query.Bytes.ContainsKey(0) &&
                    query.Bytes.All(b => b.Key >= 0 && b.Key < query.Length && IsByte(b.Value)),
                    "Write query requires length and sparse bytes including index 0 (Report ID).");
                ValidateChecksum(query.Checksum, query.Length);
                Require(query.Checksum is not { Kind: not HidChecksumKind.None, Index: 0 },
                    "A checksum cannot replace the Report ID.");
                break;
            case HidQueryMode.FeatureGetOnly:
                Require(query.ReportId is int id && IsByte(id) && query.Length == 0 && query.Bytes is null && query.Checksum is null,
                    "feature_get_only requires reportId and takes no write fields.");
                break;
            case HidQueryMode.PassiveListen:
                Require(query.ReportId is null && query.Length == 0 && query.Bytes is null && query.Checksum is null,
                    "passive_listen takes no query fields.");
                break;
        }

        var response = transaction.Response;
        Require(response.MinLength >= 1 && ValidReportLength(response.MinLength) &&
            (response.ExactLength is null || response.ExactLength >= response.MinLength && ValidReportLength(response.ExactLength.Value)) &&
            response.MaxReads is >= 1 and <= 16, "Invalid response length or maxReads (1–16).");
        int length = response.ExactLength ?? response.MinLength;
        ValidateTests(response.Expect, length);
        ValidateTests(response.AsleepWhen, length);
        ValidateTests(response.InvalidWhen, length);
        ValidateChecksum(response.Checksum, length);

        if (transaction.Parse is not { } parse)
        {
            Require(!requireParse, "Expected parse.");
            return;
        }
        var level = parse.Level;
        Require(level?.Field is { } field && field.Width is 1 or 2 && field.Index >= 0 &&
            field.Index <= length - field.Width && field.Shift >= 0 && field.Shift < 8 * field.Width &&
            (field.Mask is null || field.Mask >= 1 && field.Mask <= (1 << (8 * field.Width)) - 1),
            "Invalid level field index, width, mask or shift.");
        Require(double.IsFinite(level!.Factor) && double.IsFinite(level.Offset) &&
            double.IsFinite(65535 * Math.Abs(level.Factor) + Math.Abs(level.Offset)), "Invalid scale coefficients.");
        if (level.Decode == HidDecode.Lookup)
            Require(level.Values is { Count: >= 1 and <= 256 } &&
                level.Values.All(v => v.Key is >= 0 and <= 65535 && v.Value is >= 0 and <= 100),
                "Invalid battery lookup table.");
        if (level.Decode == HidDecode.VoltageMv)
        {
            var points = level.Points;
            Require(points is { Length: >= 2 and <= 32 } &&
                points.All(p => p is not null && p.Millivolts is >= 0 and <= 65535 && p.Percent is >= 0 and <= 100),
                "Invalid battery voltage points.");
            for (int i = 1; i < points!.Length; i++)
                Require(points[i].Millivolts > points[i - 1].Millivolts && points[i].Percent >= points[i - 1].Percent,
                    "Voltage points must increase in voltage without decreasing in percent.");
        }
        foreach (var state in parse.States ?? [])
        {
            Require(state?.When is not null, "Each state needs a when predicate.");
            ValidateTests(state!.When, length);
        }
        Require(parse.States is null || parse.States.Length <= 16, "Too many state mappings.");
    }

    private static void ValidateTests(HidTest[][]? groups, int length)
    {
        if (groups is null) return;
        Require(groups.Length is >= 1 and <= 16, "Expected 1–16 predicate groups.");
        foreach (var group in groups)
        {
            Require(group is { Length: >= 1 and <= 16 }, "Empty or oversized predicate group.");
            foreach (var test in group!)
                Require(test is not null && test.Index >= 0 && test.Index < length && test.Shift is >= 0 and <= 7 &&
                    (test.Mask is null || test.Mask is >= 1 and <= 255) &&
                    (test.Equal is not null || test.Min is not null || test.Max is not null) &&
                    (test.Equal is null || IsByte(test.Equal.Value)) && (test.Min is null || IsByte(test.Min.Value)) &&
                    (test.Max is null || IsByte(test.Max.Value)) &&
                    (test.Min is null || test.Max is null || test.Min <= test.Max),
                    "Invalid byte predicate index, mask, shift or comparison.");
        }
    }

    private static void ValidateChecksum(HidChecksum? checksum, int length)
    {
        if (checksum is null || checksum.Kind == HidChecksumKind.None) return;
        Require(checksum.Start >= 0 && checksum.End > checksum.Start && checksum.End <= length &&
            checksum.Index >= 0 && checksum.Index < length && (checksum.Index < checksum.Start || checksum.Index >= checksum.End) &&
            IsByte(checksum.Constant) && IsByte(checksum.Initial) && IsByte(checksum.XorOut) &&
            checksum.Polynomial is >= 1 and <= 255, "Invalid checksum range, destination or parameters.");
    }

    private static bool ValidId(string? value) => value is { Length: >= 1 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static void ValidateNumericKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateNumericKeys(child);
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is ("bytes" or "values") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in property.Value.EnumerateObject())
                    Require(int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int key) &&
                        entry.Name == key.ToString(CultureInfo.InvariantCulture), "Byte and lookup keys must be canonical decimal integers.");
            }
            else ValidateNumericKeys(property.Value);
        }
    }
    private static bool ValidReportLength(int length) => length is >= 0 and <= MaxReportLength;
    private static bool IsByte(int value) => value is >= 0 and <= 255;
    private static bool ValidReportId(int? value) => value is null or >= 0 and <= 255;

    private static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }
}
