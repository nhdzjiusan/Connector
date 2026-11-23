namespace PeripheralPower;

internal interface IHidReportChannel
{
    void WriteOutput(byte[] report);
    byte[]? ReadInput();
    void SetFeature(byte[] report);
    byte[]? GetFeature(byte reportId);
    void BeginPhase(int phase) { }
}

internal readonly record struct HidProtocolEvent(int Phase, string Stage, string Direction, string ReportType, byte[]? Frame, string? Reason = null);

internal static class HidRuleEngine
{
    internal static HidRuleResult Execute(HidRule rule, HidRuleDocument catalog, IHidReportChannel channel,
        Action<byte[], string>? onFrame = null, Action<HidProtocolEvent>? trace = null)
    {
        int phase = 1;
        HidRuleResult Exchange(HidTransaction transaction, int index)
        {
            phase = index;
            return Execute(transaction, channel, onFrame: onFrame, trace: trace, phase: index,
                inputReportId: rule.Match.Interface?.InputReportId);
        }
        var outcome = rule.BuiltinDriver is { } driver
            ? HidBuiltinDrivers.Execute(driver, catalog.DriverSteps![driver], Exchange)
            : Exchange(rule.Transaction, 1);
        trace?.Invoke(new(phase, "outcome", "none", "none", null, outcome.Disposition switch
        {
            HidDisposition.Accepted => "accepted",
            HidDisposition.Asleep => "asleep",
            HidDisposition.Invalid => "invalid",
            _ => "no_match"
        }));
        return outcome;
    }

    internal static byte[] BuildQuery(HidQuery query)
    {
        if (query.Mode == HidQueryMode.PassiveListen) return [];
        if (query.Mode == HidQueryMode.FeatureGetOnly) return [(byte)query.ReportId!.Value];

        var report = new byte[query.Length];
        foreach (var (index, value) in query.Bytes!)
            report[index] = (byte)value;
        if (query.Checksum is { Kind: not HidChecksumKind.None } checksum)
            report[checksum.Index] = Checksum(report, checksum);
        return report;
    }

    internal static HidRuleResult Execute(HidTransaction transaction, IHidReportChannel channel,
        Action<byte[]>? onQuery = null, Action<byte[], string>? onFrame = null,
        Action<HidProtocolEvent>? trace = null, int phase = 1, int? inputReportId = null)
    {
        channel.BeginPhase(phase);
        var query = BuildQuery(transaction.Query);
        var mode = transaction.Query.Mode;
        bool feature = mode is HidQueryMode.FeatureSetGet or HidQueryMode.FeatureGetOnly;
        onQuery?.Invoke(query);
        trace?.Invoke(new(phase, "query", mode == HidQueryMode.PassiveListen ? "none" : "tx",
            feature ? "feature" : mode == HidQueryMode.PassiveListen ? "input" : "output", query));
        if (mode == HidQueryMode.OutputThenInput) channel.WriteOutput(query);
        if (mode == HidQueryMode.FeatureSetGet) channel.SetFeature(query);

        bool invalidValue = false;
        for (int read = 0; read < transaction.Response.MaxReads; read++)
        {
            var frame = feature ? channel.GetFeature(query[0]) : channel.ReadInput();
            if (frame is null) break;
            HidRuleResult result;
            string reason;
            if (!feature && inputReportId is int id && frame.Length > 0 && frame[0] != id)
            {
                result = new(HidDisposition.NoMatch);
                reason = "report_id";
            }
            else result = Inspect(transaction, frame, out reason);
            onFrame?.Invoke(frame, reason);
            trace?.Invoke(new(phase, "receive", "rx", feature ? "feature" : "input", frame, reason));
            if (result.Disposition == HidDisposition.Invalid)
            {
                invalidValue = true;
                continue;
            }
            if (result.Disposition != HidDisposition.NoMatch) return result;
        }
        return new(invalidValue ? HidDisposition.Invalid : HidDisposition.NoMatch);
    }

    private static HidRuleResult Inspect(HidTransaction transaction, ReadOnlySpan<byte> frame, out string reason)
    {
        var response = transaction.Response;
        if (frame.Length < response.MinLength || response.ExactLength is int length && frame.Length != length)
        {
            reason = "length";
            return new(HidDisposition.NoMatch);
        }
        if (response.Expect is { } expect && !Matches(frame, expect))
        {
            reason = "expect";
            return new(HidDisposition.NoMatch);
        }
        if (response.Checksum is { Kind: not HidChecksumKind.None } checksum &&
            frame[checksum.Index] != Checksum(frame, checksum))
        {
            reason = "checksum";
            return new(HidDisposition.NoMatch);
        }
        if (response.AsleepWhen is { } asleep && Matches(frame, asleep))
        {
            reason = "asleep";
            return new(HidDisposition.Asleep);
        }
        if (response.InvalidWhen is { } invalid && Matches(frame, invalid))
        {
            reason = "invalid_when";
            return new(HidDisposition.NoMatch);
        }
        if (transaction.Parse is not { } parse)
        {
            reason = "accepted";
            return new(HidDisposition.Accepted);
        }

        var level = parse.Level;
        int raw = level.Field.Get(frame);
        double? percent = level.Decode switch
        {
            HidDecode.Percent => raw,
            HidDecode.Scale => raw * level.Factor + level.Offset,
            HidDecode.Lookup => level.Values!.TryGetValue(raw, out int mapped) ? mapped : null,
            HidDecode.VoltageMv => Interpolate(raw * level.Factor + level.Offset, level.Points!),
            _ => null
        };
        if (percent is not double value || !double.IsFinite(value) || value is < 0 or > 100)
        {
            reason = "invalid battery value";
            return new(HidDisposition.Invalid);
        }

        var power = parse.DefaultPower;
        bool reference = false;
        foreach (var state in parse.States ?? [])
        {
            if (!Matches(frame, state.When)) continue;
            power = state.Power;
            reference = state.IsReference;
            break;
        }
        reason = "accepted";
        return new(HidDisposition.Accepted, new((int)Math.Round(value, MidpointRounding.AwayFromZero), power, reference));
    }

    private static bool Matches(ReadOnlySpan<byte> frame, HidTest[][] groups)
    {
        foreach (var group in groups)
        {
            bool matched = true;
            foreach (var test in group)
                if (!test.Matches(frame)) { matched = false; break; }
            if (matched) return true;
        }
        return false;
    }

    private static double Interpolate(double millivolts, HidVoltagePoint[] points)
    {
        if (millivolts <= points[0].Millivolts) return points[0].Percent;
        for (int i = 1; i < points.Length; i++)
        {
            var upper = points[i];
            if (millivolts > upper.Millivolts) continue;
            var lower = points[i - 1];
            return lower.Percent + (upper.Percent - lower.Percent) *
                (millivolts - lower.Millivolts) / (upper.Millivolts - lower.Millivolts);
        }
        return points[^1].Percent;
    }

    internal static byte Checksum(ReadOnlySpan<byte> report, HidChecksum spec)
    {
        if (spec.Kind == HidChecksumKind.None) return 0;
        int value = spec.Kind == HidChecksumKind.Crc8 ? spec.Initial : 0;
        foreach (byte next in report[spec.Start..spec.End])
        {
            if (spec.Kind == HidChecksumKind.Crc8)
            {
                value ^= next;
                for (int bit = 0; bit < 8; bit++)
                    value = ((value << 1) ^ ((value & 0x80) != 0 ? spec.Polynomial : 0)) & 0xFF;
            }
            else value = spec.Kind == HidChecksumKind.Xor ? value ^ next : (value + next) & 0xFF;
        }
        return (byte)(spec.Kind switch
        {
            HidChecksumKind.SubFromConst8 => (spec.Constant - value) & 0xFF,
            HidChecksumKind.Crc8 => value ^ spec.XorOut,
            _ => value
        });
    }
}
