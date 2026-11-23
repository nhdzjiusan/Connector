using HidSharp;
using HidSharp.Reports;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralPower;

internal enum DeviceKind { Mouse, Keyboard, Headset, Generic }

internal sealed record BatteryReading(string Id, string Name, DeviceKind Kind, int Percent, bool Charging,
    string Connection, bool IsReference = false, bool FullOnPower = false, DateTime? SampledAt = null, bool Stale = false,
    [property: JsonIgnore] HidPower? ReportedPower = null)
{
    public string PowerStatus => Stale ? $"参考电量 · 上次更新 {SampledAt:HH:mm}" : FullOnPower ? "已充满 · 电源已连接" :
        Charging && IsReference ? "充电中 · 参考电量" : Charging ? "充电中" :
        ReportedPower == HidPower.ExternalReference ? "参考电量 · 已接外部电源" :
        IsReference ? "参考电量 · 供电状态未知" : "";
}

internal static class UsbBattery
{
    private const int ScanBudgetMs = 3000;
    internal const string HeadsetRuleId = "mchose-g9-pro-receiver";

    private sealed record Candidate(HidDevice Device, DeviceItem Collection, HidRule Rule, int EndpointId);
    private sealed record Inventory(Candidate[] Candidates, HashSet<string> Present, HidDevice[] Others);
    internal readonly record struct EndpointReading(HidRule Rule, HidSample Sample);

    private static readonly Lazy<HidRuleDocument> Rules = new(LoadRules);
    private static readonly SemaphoreSlim Enumeration = new(1);

    internal static void InitializeRules() => _ = Rules.Value;

    private static HidRuleDocument LoadRules()
    {
        var defaults = HidRuleLoader.LoadEmbedded();
        var configured = HidRuleLoader.LoadDirectory(Path.Combine(Settings.Folder, "rules"), defaults, ErrorLog.Write);
        string path = Path.Combine(Settings.Folder, "protocols.json");
        if (!File.Exists(path)) return configured;

        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[32769];
            int size = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (size > 32768) throw new InvalidDataException("Legacy protocol file exceeds 32 KiB.");
            var bytes = buffer.AsSpan(0, size);
            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
            var aliases = JsonSerializer.Deserialize(bytes, HidRuleJsonContext.Strict.LegacyHidAliasArray);
            if (aliases is not { Length: <= 64 }) throw new InvalidDataException("Expected up to 64 legacy aliases.");

            return AddLegacyAliases(defaults, configured, aliases);
        }
        catch (Exception ex) when (HidRuleLoader.IsRuleError(ex))
        {
            ErrorLog.Write(path, ex);
            return configured;
        }
    }

    internal static HidRuleDocument AddLegacyAliases(HidRuleDocument defaults, HidRuleDocument configured, LegacyHidAlias[] aliases)
    {
        var active = configured.Rules.ToList();
        foreach (var alias in aliases)
        {
            if (alias is null || alias.Vendor is < 1 or > 65535 || alias.Product is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(alias.Name) || alias.Name.Length > 100 ||
                !HidRuleLoader.ExternalIdentityAllowed(alias.Vendor, [alias.Product], (int)(alias.Usage >> 16)))
                throw new InvalidDataException("Invalid legacy HID identity or name.");

            var template = defaults.Rules.FirstOrDefault(rule => rule.Kind == alias.Kind &&
                rule.Match.Transport == HidTransport.Receiver &&
                alias.Usage == ((uint)rule.Match.UsagePage.GetValueOrDefault() << 16 | (uint)rule.Match.Usage.GetValueOrDefault()));
            if (template is null) throw new InvalidDataException("Legacy alias must use a supported receiver protocol.");

            if (defaults.Rules.Concat(active).Any(r => r.Match.Vid == alias.Vendor && r.Match.Pids.Contains(alias.Product) &&
                r.Match.UsagePage == template.Match.UsagePage && r.Match.Usage == template.Match.Usage))
                continue;
            string key = $"{alias.Vendor:X4}-{alias.Product:X4}-{alias.Usage:X8}";
            active.Add(template with
            {
                Id = $"legacy-{key}", Name = alias.Name,
                Match = template.Match with { Vid = alias.Vendor, Pids = [alias.Product], DeviceGroup = key }
            });
        }
        return configured with { Rules = active.ToArray() };
    }

    public static async Task<PollResult> ReadAsync(HidScanCapture? diagnostic = null)
    {
        var elapsed = Stopwatch.StartNew();
        using var budget = new CancellationTokenSource(ScanBudgetMs);
        var token = budget.Token;
        Task<EndpointReading?>[] tasks = [];
        HashSet<string>? present = null;
        bool expired = false;
        try
        {
            if (!Enumeration.Wait(0)) return new([], null, true);
            var inventory = Task.Run(() =>
            {
                try { return Discover(Rules.Value, token, diagnostic); }
                finally { Enumeration.Release(); }
            });
            ObserveFailure(inventory);
            var found = await inventory.WaitAsync(token).ConfigureAwait(false);
            present = found.Present;
            tasks = found.Candidates.Select(candidate =>
                Task.Run(() => ReadLocked(candidate, Rules.Value, elapsed, token, diagnostic))).ToArray();
            foreach (var task in tasks) ObserveFailure(task);
            Task? metadata = null;
            if (diagnostic is not null && found.Others.Length > 0)
            {
                metadata = Task.Run(() => DescribeUnmatched(found.Others, diagnostic, token));
                ObserveFailure(metadata);
            }
            await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
            if (metadata is not null)
            {
                try { await metadata.WaitAsync(token).ConfigureAwait(false); }
                catch (Exception) { diagnostic!.NoteLimited(); } // Metadata failure does not make the battery scan partial.
            }
        }
        catch (OperationCanceledException) { expired = true; diagnostic?.NoteLimited(); }
        catch (Exception ex) { ErrorLog.Write("USB 设备扫描", ex); expired = true; }

        var readings = SelectReadings(tasks.Where(t => t.IsCompletedSuccessfully)
            .Select(t => t.Result).OfType<EndpointReading>());
        return new(readings, present, expired || present?.Count > readings.Length);
    }

    private static Inventory Discover(HidRuleDocument catalog, CancellationToken token, HidScanCapture? diagnostic)
    {
        var candidates = new List<Candidate>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        var passiveMatches = new Dictionary<(string Id, int Pid), int>();
        var unsurePassive = new HashSet<(string Id, int Pid)>();
        var others = new List<HidDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            token.ThrowIfCancellationRequested();
            var possible = catalog.Rules.Where(r => r.Match.Vid == device.VendorID && r.Match.Pids.Contains(device.ProductID))
                .OrderByDescending(r => catalog.BuiltinIds?.Contains(r.Id) == true)
                .ThenByDescending(r => r.Match.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
            if (possible.Length == 0)
            {
                if (diagnostic is not null) others.Add(device);
                continue;
            }
            int endpoint = diagnostic?.AllocateEndpoint(device.VendorID, device.ProductID) ?? 0;
            int? inputLength = null, outputLength = null, featureLength = null;
            try
            {
                int input = device.GetMaxInputReportLength();
                inputLength = input;
                int output = device.GetMaxOutputReportLength();
                outputLength = output;
                int feature = device.GetMaxFeatureReportLength();
                featureLength = feature;
                // System collections on multifunction receivers can be inaccessible to HidSharp.
                if (output == 0 && feature == 0 &&
                    possible.All(r => r.Match.MinOutput > 0 || r.Match.MinFeature > 0))
                {
                    if (endpoint != 0)
                        diagnostic!.ObserveEndpoint(new(endpoint, device.VendorID, device.ProductID, input, output, feature,
                            [], null, "interface_or_capacity_mismatch"));
                    continue;
                }
                var collections = device.GetReportDescriptor().DeviceItems
                    .Select(item => (Item: item, Usages: item.Usages.GetAllValues().ToArray())).ToArray();
                Candidate? chosen = null;
                string status = "collection_not_matched";

                foreach (var rule in possible)
                {
                    bool unscoped = rule.Match.UsagePage is null;
                    if (chosen is not null && !unscoped) continue;
                    var matches = new List<DeviceItem>();
                    foreach (var (item, usages) in collections)
                    {
                        if (usages.Length == 0 || usages.Any(usage => (usage >> 16) < 0xFF00)) continue;
                        uint? usage = SelectUsage(rule.Match, usages);
                        if (usage is not uint selected) continue;

                        if (!unscoped && chosen is null) present.Add(ReadingId(rule));
                        var reports = rule.Match.Interface is null ? null : DescribeReports(item);
                        if (rule.Match.Matches(new(device.VendorID, device.ProductID, (int)(selected >> 16),
                            (int)(selected & 0xFFFF), input, output, feature, reports)) && (!unscoped || input > 0))
                            matches.Add(item);
                        else if (chosen is null) status = "interface_or_capacity_mismatch";
                    }

                    if (unscoped && matches.Count > 0)
                    {
                        var key = (rule.Id, device.ProductID);
                        passiveMatches[key] = passiveMatches.GetValueOrDefault(key) + matches.Count;
                    }
                    if (chosen is not null || matches.Count == 0) continue;
                    if (rule.Match.Interface is not null && matches.Count > 1)
                    {
                        status = "ambiguous_collection";
                        continue;
                    }

                    var selectedRule = EffectiveRule(rule, matches[0], input, output, feature);
                    if (!ReferenceEquals(selectedRule, rule)) status = "override_incompatible_using_builtin";
                    else status = "matched";
                    chosen = new(device, matches[0], selectedRule, endpoint);
                    present.Add(ReadingId(rule));
                }
                if (chosen is not null) candidates.Add(chosen);
                if (endpoint != 0)
                    diagnostic!.ObserveEndpoint(new(endpoint, device.VendorID, device.ProductID, input, output, feature,
                        DescribeCollections(collections, diagnostic), chosen?.Rule.Id, status));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                foreach (var rule in possible) uncertain.Add(ReadingId(rule));
                foreach (var rule in possible.Where(r => r.Match.UsagePage is null))
                    unsurePassive.Add((rule.Id, device.ProductID));
                if (endpoint != 0)
                    diagnostic!.ObserveEndpoint(new(endpoint, device.VendorID, device.ProductID, inputLength, outputLength,
                        featureLength, [], null, "descriptor_unavailable"));
                ErrorLog.Write("HID 报文描述符", ex);
            }
        }

        // A descriptor failure is inconclusive. Do not lose previously cached data for it.
        present.UnionWith(uncertain);
        var qualified = candidates.Where(candidate =>
        {
            if (candidate.Rule.Match.UsagePage is not null) return true;
            var key = (candidate.Rule.Id, candidate.Device.ProductID);
            if (passiveMatches[key] == 1 && !unsurePassive.Contains(key)) return true;
            diagnostic?.SetEndpointStatus(candidate.EndpointId, "ambiguous_collection");
            return false;
        }).ToArray();
        return new(qualified, present, others.ToArray());
    }

    internal static HidRule EffectiveRule(HidRule rule, DeviceItem collection, int input, int output, int feature)
    {
        if (rule.ShippedFallback is not { } fallback || rule.Query is not { } query) return rule;
        int replyLength = rule.Response!.ExactLength ?? rule.Response.MinLength;
        bool supported = query.Mode switch
        {
            HidQueryMode.OutputThenInput => input >= replyLength && input <= HidRuleLoader.MaxReportLength &&
                output >= query.Length && output <= HidRuleLoader.MaxReportLength &&
                HidSharpReportChannel.SupportsReport(collection.OutputReports, query.Bytes![0], query.Length, rule.AllowUndescribedOutput),
            HidQueryMode.FeatureSetGet or HidQueryMode.FeatureGetOnly =>
                feature >= Math.Max(query.Length, replyLength) && feature <= HidRuleLoader.MaxReportLength &&
                HidSharpReportChannel.SupportsReport(collection.FeatureReports, query.ReportId ?? query.Bytes![0], Math.Max(query.Length, 1), false),
            HidQueryMode.PassiveListen => input >= replyLength && input <= HidRuleLoader.MaxReportLength,
            _ => false
        };
        return supported ? rule : fallback;
    }

    internal static uint? SelectUsage(HidMatch match, uint[] usages)
    {
        if (match.UsagePage is not int page)
            return usages.Length == 1 && (usages[0] >> 16) is >= 0xFF01 and <= 0xFFFF && (usages[0] & 0xFFFF) != 0
                ? usages[0] : null;
        uint expected = (uint)page << 16 | (uint)match.Usage.GetValueOrDefault();
        return usages.Contains(expected) ? expected : null;
    }

    private static HidCollectionReports DescribeReports(DeviceItem item) => new(
        item.InputReports.Select(r => (int)r.ReportID).Distinct().Order().ToArray(),
        item.OutputReports.Select(r => (int)r.ReportID).Distinct().Order().ToArray(),
        item.FeatureReports.Select(r => (int)r.ReportID).Distinct().Order().ToArray());

    private static HidCollectionDiagnostic[] DescribeCollections(IEnumerable<(DeviceItem Item, uint[] Usages)> collections,
        HidScanCapture capture)
    {
        var selected = collections.Take(17).ToArray();
        if (selected.Length > 16 || selected.Any(x => x.Usages.Length > 32)) capture.NoteLimited();
        return selected.Take(16).Select(x =>
        {
            var reports = DescribeReports(x.Item);
            if (reports.Input.Length > 32 || reports.Output.Length > 32 || reports.Feature.Length > 32) capture.NoteLimited();
            return new HidCollectionDiagnostic(x.Usages.Take(32).Select(value =>
                    new HidUsageDiagnostic((int)(value >> 16), (int)(value & 0xFFFF))).ToArray(),
                new(reports.Input.Take(32).ToArray(), reports.Output.Take(32).ToArray(), reports.Feature.Take(32).ToArray()));
        }).ToArray();
    }

    private static void DescribeUnmatched(HidDevice[] devices, HidScanCapture capture, CancellationToken token)
    {
        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();
            int id = capture.AllocateEndpoint(device.VendorID, device.ProductID);
            if (id == 0) break;
            int? input = null, output = null, feature = null;
            try
            {
                input = device.GetMaxInputReportLength();
                output = device.GetMaxOutputReportLength();
                feature = device.GetMaxFeatureReportLength();
                var report = DescribeCollections(device.GetReportDescriptor().DeviceItems.Select(item =>
                    (item, item.Usages.GetAllValues().ToArray())), capture);
                capture.ObserveEndpoint(new(id, device.VendorID, device.ProductID, input, output, feature, report, null, "no_rule"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                capture.ObserveEndpoint(new(id, device.VendorID, device.ProductID, input, output, feature, [], null, "descriptor_unavailable"));
            }
        }
    }

    private static EndpointReading? ReadLocked(Candidate candidate, HidRuleDocument catalog, Stopwatch elapsed,
        CancellationToken token, HidScanCapture? diagnostic)
    {
        var (device, collection, rule, endpoint) = candidate;
        Action<HidProtocolEvent>? trace = diagnostic is null ? null :
            entry => diagnostic.Record(endpoint, rule, device.ProductID, entry);
        token.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DevicePath.ToUpperInvariant())));
        using var gate = new Mutex(false, @"Local\PeripheralPower.Hid." + key);
        var remaining = ScanBudgetMs - elapsed.ElapsedMilliseconds;
        if (remaining <= 0) { diagnostic?.SetEndpointStatus(endpoint, "budget_expired"); return null; }
        try
        {
            if (!gate.WaitOne((int)remaining)) { diagnostic?.SetEndpointStatus(endpoint, "lock_timeout"); return null; }
        }
        catch (AbandonedMutexException ex) { ErrorLog.Write("回收 HID 设备锁", ex); }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!device.TryOpen(out var stream))
            {
                diagnostic?.SetEndpointStatus(endpoint, "open_failed");
                trace?.Invoke(new(0, "error", "none", "none", null, "open_failed"));
                return null;
            }
            using (stream)
            {
                // The G9 Pro descriptor reconstructed by HidSharp can omit its working Output report.
                // Only the fixed shipped command or a legacy alias of that command gets this exception.
                bool allowUndescribedOutput = rule.AllowUndescribedOutput;
                var channel = new HidSharpReportChannel(device, stream, collection, elapsed, ScanBudgetMs, token,
                    allowUndescribedOutput, trace);
                var result = HidRuleEngine.Execute(rule, catalog, channel, trace: trace);
                return result.Sample is { } sample ? new(rule, sample) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostic?.SetEndpointStatus(endpoint, "io_failed");
            trace?.Invoke(new(0, "error", "none", "none", null, ex.GetType().Name));
            if (ex is not TimeoutException) ErrorLog.Write(rule.Name, ex);
            return null;
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static BatteryReading[] SelectReadings(IEnumerable<EndpointReading> values) =>
        values.GroupBy(value => ReadingId(value.Rule), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.Rule.Match.Priority)
                .ThenBy(value => value.Rule.Id, StringComparer.Ordinal).First())
            .Select(value => new BatteryReading(ReadingId(value.Rule), value.Rule.Name, value.Rule.Kind, value.Sample.Percent,
                value.Sample.Power == HidPower.Charging, value.Rule.Match.Transport == HidTransport.Wired ? "USB" : "2.4 GHz",
                value.Sample.IsReference || value.Sample.Power == HidPower.ExternalReference,
                value.Sample.Power == HidPower.Full, ReportedPower: value.Sample.Power)).ToArray();

    internal static string ReadingId(HidRule rule) => "usb-" + rule.Match.DeviceGroup;

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(t =>
        {
            if (t.Exception!.GetBaseException() is not OperationCanceledException)
                ErrorLog.Write("HID 后台读取", t.Exception.GetBaseException());
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
