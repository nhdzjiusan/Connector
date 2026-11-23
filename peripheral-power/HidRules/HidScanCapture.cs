using System.Text.Json.Serialization;

namespace PeripheralPower;

internal sealed record HidCollectionReports(
    [property: JsonPropertyName("input")] int[] Input,
    [property: JsonPropertyName("output")] int[] Output,
    [property: JsonPropertyName("feature")] int[] Feature);

internal sealed record HidUsageDiagnostic(
    [property: JsonPropertyName("usagePage")] int UsagePage,
    [property: JsonPropertyName("usage")] int Usage);

internal sealed record HidCollectionDiagnostic(
    [property: JsonPropertyName("usages")] HidUsageDiagnostic[] Usages,
    [property: JsonPropertyName("reports")] HidCollectionReports Reports);

internal sealed record HidEndpointDiagnostic(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("vid")] int Vid,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("inputLength")] int? InputLength,
    [property: JsonPropertyName("outputLength")] int? OutputLength,
    [property: JsonPropertyName("featureLength")] int? FeatureLength,
    [property: JsonPropertyName("collections")] HidCollectionDiagnostic[] Collections,
    [property: JsonPropertyName("ruleId")] string? RuleId,
    [property: JsonPropertyName("status")] string Status);

internal sealed record HidExchangeDiagnostic(
    [property: JsonPropertyName("endpointId")] int EndpointId,
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("phase")] int Phase,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("reportType")] string ReportType,
    [property: JsonPropertyName("reportId")] int? ReportId,
    [property: JsonPropertyName("length")] int? Length,
    [property: JsonPropertyName("capturedLength")] int? CapturedLength,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("hex")] string? Hex,
    [property: JsonPropertyName("reason")] string? Reason);

internal sealed record HidScanDiagnostic(
    [property: JsonPropertyName("endpoints")] HidEndpointDiagnostic[] Endpoints,
    [property: JsonPropertyName("exchanges")] HidExchangeDiagnostic[] Exchanges,
    [property: JsonPropertyName("captureLimited")] bool CaptureLimited);

internal sealed class HidScanCapture
{
    private const int MaxEndpoints = 128;
    private const int MaxExchanges = 512;
    private const int MaxBytes = 128 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<int, HidEndpointDiagnostic> endpoints = [];
    private readonly List<HidExchangeDiagnostic> exchanges = [];
    private readonly List<string> details = [];
    private int nextEndpoint, bytes;
    private bool limited, closed;

    internal int AllocateEndpoint(int vid, int pid)
    {
        lock (gate)
        {
            if (closed) return 0;
            if (nextEndpoint == MaxEndpoints) { limited = true; return 0; }
            int id = ++nextEndpoint;
            endpoints.Add(id, new(id, vid, pid, null, null, null, [], null, "enumeration_pending"));
            return id;
        }
    }

    internal void ObserveEndpoint(HidEndpointDiagnostic endpoint)
    {
        lock (gate)
            if (!closed && endpoint.Id != 0) endpoints[endpoint.Id] = endpoint;
    }

    internal void SetEndpointStatus(int id, string reason)
    {
        lock (gate)
            if (!closed && endpoints.TryGetValue(id, out var endpoint)) endpoints[id] = endpoint with { Status = reason };
    }

    internal void NoteLimited()
    {
        lock (gate) if (!closed) limited = true;
    }

    internal void Record(int endpointId, HidRule rule, int pid, HidProtocolEvent item)
    {
        lock (gate)
        {
            if (closed || endpointId == 0) return;
            if (exchanges.Count == MaxExchanges) { limited = true; return; }
            var frame = item.Frame;
            int? length = frame?.Length;
            int? saved = length is int count ? Math.Min(Math.Min(count, HidRuleLoader.MaxReportLength), MaxBytes - bytes) : null;
            bool truncated = saved < length;
            limited |= truncated;
            bytes += saved ?? 0;
            exchanges.Add(new(endpointId, rule.Id, item.Phase, item.Stage, item.Direction, item.ReportType,
                frame is { Length: > 0 } ? frame[0] : null, length, saved, truncated,
                frame is null ? null : Convert.ToHexString(frame.AsSpan(0, saved!.Value)), item.Reason));
            if (item.Stage == "receive" && frame is not null)
                details.Add($"{pid:X4} {Convert.ToHexString(frame.AsSpan(0, Math.Min(20, frame.Length)))} [{item.Reason}]");
            else if (item.Stage == "error")
                details.Add($"{rule.Name}：{item.Reason}");
        }
    }

    internal (HidScanDiagnostic Diagnostic, string[] Details) Finish()
    {
        lock (gate)
        {
            closed = true;
            return (new(endpoints.Values.OrderBy(x => x.Id).ToArray(), exchanges.ToArray(), limited), details.ToArray());
        }
    }
}
