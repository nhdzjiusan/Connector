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

    private sealed record Candidate(HidDevice Device, DeviceItem Collection, HidRule Rule);
    private sealed record Inventory(Candidate[] Candidates, HashSet<string> Present);
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
                alias.Usage == ((uint)rule.Match.UsagePage << 16 | (uint)rule.Match.Usage));
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

    public static async Task<PollResult> ReadAsync(Action<string>? diagnostic = null)
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
                try { return Discover(Rules.Value, token); }
                finally { Enumeration.Release(); }
            });
            ObserveFailure(inventory);
            var found = await inventory.WaitAsync(token).ConfigureAwait(false);
            present = found.Present;
            tasks = found.Candidates.Select(candidate =>
                Task.Run(() => ReadLocked(candidate, Rules.Value, elapsed, token, diagnostic))).ToArray();
            foreach (var task in tasks) ObserveFailure(task);
            await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { expired = true; }
        catch (Exception ex) { ErrorLog.Write("USB 设备扫描", ex); expired = true; }

        var readings = SelectReadings(tasks.Where(t => t.IsCompletedSuccessfully)
            .Select(t => t.Result).OfType<EndpointReading>());
        return new(readings, present, expired || present?.Count > readings.Length);
    }

    private static Inventory Discover(HidRuleDocument catalog, CancellationToken token)
    {
        var candidates = new List<Candidate>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            token.ThrowIfCancellationRequested();
            var possible = catalog.Rules.Where(r => r.Match.Vid == device.VendorID && r.Match.Pids.Contains(device.ProductID))
                .OrderByDescending(r => r.Match.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
            if (possible.Length == 0) continue;
            try
            {
                int input = device.GetMaxInputReportLength(), output = device.GetMaxOutputReportLength();
                int feature = device.GetMaxFeatureReportLength();
                // System collections on multifunction receivers can be inaccessible to HidSharp.
                if (output == 0 && feature == 0 && possible.All(r => r.Match.MinOutput > 0 || r.Match.MinFeature > 0))
                    continue;
                var collections = device.GetReportDescriptor().DeviceItems
                    .Select(item => (Item: item, Usages: item.Usages.GetAllValues().ToArray()))
                    .Where(entry => entry.Usages.All(usage => (usage >> 16) >= 0xFF00)).ToArray();

                foreach (var rule in possible)
                {
                    uint exactUsage = (uint)rule.Match.UsagePage << 16 | (uint)rule.Match.Usage;
                    var item = collections.FirstOrDefault(collection => collection.Usages.Contains(exactUsage)).Item;
                    if (item is null) continue;
                    present.Add(ReadingId(rule));
                    if (!rule.Match.Matches(new(device.VendorID, device.ProductID, rule.Match.UsagePage,
                        rule.Match.Usage, input, output, feature))) continue;

                    candidates.Add(new(device, item, rule));
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                foreach (var rule in possible) uncertain.Add(ReadingId(rule));
                ErrorLog.Write("HID 报文描述符", ex);
            }
        }

        // A descriptor failure is inconclusive. Do not lose previously cached data for it.
        present.UnionWith(uncertain);
        return new(candidates.ToArray(), present);
    }

    private static EndpointReading? ReadLocked(Candidate candidate, HidRuleDocument catalog, Stopwatch elapsed,
        CancellationToken token, Action<string>? diagnostic)
    {
        var (device, collection, rule) = candidate;
        token.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DevicePath.ToUpperInvariant())));
        using var gate = new Mutex(false, @"Local\PeripheralPower.Hid." + key);
        var remaining = ScanBudgetMs - elapsed.ElapsedMilliseconds;
        if (remaining <= 0) return null;
        try { if (!gate.WaitOne((int)remaining)) return null; }
        catch (AbandonedMutexException ex) { ErrorLog.Write("回收 HID 设备锁", ex); }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!device.TryOpen(out var stream))
            {
                diagnostic?.Invoke($"{rule.Name} 打开失败");
                return null;
            }
            using (stream)
            {
                // The G9 Pro descriptor reconstructed by HidSharp can omit its working Output report.
                // Only the fixed shipped command or a legacy alias of that command gets this exception.
                bool allowUndescribedOutput = rule.AllowUndescribedOutput;
                var channel = new HidSharpReportChannel(device, stream, collection, elapsed, ScanBudgetMs, token, allowUndescribedOutput);
                var result = HidRuleEngine.Execute(rule, catalog, channel, diagnostic is null ? null :
                    (bytes, reason) => diagnostic($"{device.ProductID:X4} {Convert.ToHexString(bytes.AsSpan(0, Math.Min(20, bytes.Length)))} [{reason}]"));
                return result.Sample is { } sample ? new(rule, sample) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostic?.Invoke($"{rule.Name}：{ex.Message}");
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
